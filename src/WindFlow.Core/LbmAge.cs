using ILGPU;
using ILGPU.Runtime;

namespace WindFlow.Core;

// 6k (P14-06): the age of air as a TRANSIENT passive scalar on the running LBM, CPU and GPU, started by AveragedPass at the end of its settle:
//   d(tau)/dt + u.grad(tau) = div((D + nu_t / Sc_t) grad tau) + 1          tau in seconds, 0 on outdoor inflow and on fresh-air supplies
// Discretised exactly like the Hybrid thermal scheme (LbmHybridThermal.cs): every D3Q19 link A -> D is an FV face carrying the LBM's own mass flux
// m = f*_q(A) - f*_qbar(D) (so a uniform tau stays uniform whatever the lattice divergence does), face value by the van Leer MUSCL limiter upwind by
// the sign of m, rho_new = rho* - sum m, tau_new = (rho* tau - sum m tau_face + diffusion + rho* dt) / rho_new. Diffusion is central over the 6 axis
// faces with the face-mean of kappa = D_mol + nu_t / Sc_t (nu_t is the flow pass's own eddy viscosity of THIS step, one extra plane written by the flow
// kernels while the average accumulates) and unit face density (symmetric, so rho tau is conserved). Walls and cut links carry nothing; a face to a
// patch (Fixed) cell carries no diffusion either: a fresh-air supply is convection only (ScalarSolver's supplyFace), a return / fan patch is an outflow (zero gradient). Cells the scalar does not solve are HELD (outdoors outside the building's
// envelope, fresh-air patches and diffuser outlets at 0, sealed pockets at the 24 h cap),
// the ring is 0 on inflow and open faces and zero-gradient elsewhere (the Hybrid ring with tOut = 0), and a return / fan patch cell follows the mean of the solved
// air cells beside it each step (zero gradient: the scalar leaves it by advection only).
// Warm start: the frozen steady age solved on the
// settle-window mean flow (ScalarSolver), so the batches start near statistical steady state; the time mean over a batch is a sum sampled every
// AgeSampleSteps steps. ILGPU: the kernel sits at 13 arguments like StepKernelThermal.

public static partial class LbmSolver
{
    const float AgeScT = 0.9f;               // turbulent Schmidt number, the one ScalarSolver uses (ScalarSolver.ScT)
    const float AgeKappaMax = 0.16f;         // explicit-scheme ceiling on kappa (6 kappa < 1); nu_t stays far below it on every grid seen, this only guards a pathological cell
    internal const int AgeSampleSteps = 8;   // one sample of tau per this many steps goes into the batch sum

    public struct AgeParams { public float Dt, KMol, KMax, ScTInv; }

    /// <summary>One explicit step of the age scalar over the interior air cells the mask marks 1 (see the header). <paramref name="nuT"/>: the flow pass's
    /// eddy viscosity of this step (lattice), <paramref name="src"/>: per-cell increment per step (null = <paramref name="dt"/> seconds per step, the age).</summary>
    static void RunStepAge(VoxelGrid grid, byte[] nearBoundary, float[] cur, float[] nxt, byte[] mask, float[] tc, float[] tn, float[] nuT, float[]? src,
        float dt, float kMol, double[]? diag = null)
    {
        int Nx = grid.Nx, Ny = grid.Ny, Nz = grid.Nz, count = grid.Count;
        if (Ny <= 2 || Nx <= 2) return;
        var cell = grid.Cell;
        int rows = Nz * (Ny - 2);
        Parallel.For(0, rows, rowIdx =>
        {
            int y = 1 + rowIdx % (Ny - 2);
            int z = rowIdx / (Ny - 2);
            if ((z == 0 && grid.OpenZMin) || (z == Nz - 1 && grid.OpenZMax)) return;
            int rowBase = Nx * (y + Ny * z);
            double dSrc = 0, dOutT = 0, dOutM = 0, dInM = 0, dDif = 0;   // the budget of the Sandbox check: source, tau and mass leaving / entering through held cells, diffusive inflow
            for (int x = 1; x < Nx - 1; x++)
            {
                int i = x + rowBase;
                if (cell[i] != VoxelGrid.Air || mask[i] != 1) continue;
                float kA = MathF.Min(kMol + nuT[i] / AgeScT, AgeKappaMax), tA = tc[i];
                int bA = i * Q;
                float rhoA = 0f;
                for (int p = 0; p < Q; p++) rhoA += nxt[bA + p];

                float sumM = 0f, adv = 0f;
                for (int p = 1; p < Q; p++)
                {
                    int cx = HCx[p], cy = HCy[p], cz = HCz[p], pb = HOpp[p];
                    int zD = z + cz;
                    bool lid = zD < 0 || zD >= Nz;
                    if (lid) { if (cx == 0 && cy == 0) continue; cz = 0; zD = z; pb = HMirrorZ[pb]; }
                    int xD = x + cx, yD = y + cy;
                    int D = xD + Nx * (yD + Ny * zD);
                    byte cD = cell[D];
                    if (cD == VoxelGrid.Solid || !lid && LinkCut(nearBoundary, count, i, pb)) continue;
                    float fOut = nxt[bA + p], arrive;
                    if (HybridFresh(grid, cD, xD, yD, zD)) arrive = nxt[D * Q + pb];
                    else if (!lid && cD == VoxelGrid.Fixed && (nearBoundary[D] & 1) != 0)
                        arrive = SupplyBounce(fOut, cur[D * Q + pb], cur[D * Q + p], nearBoundary[D]);
                    else arrive = cur[D * Q + pb];
                    float m = fOut - arrive;
                    sumM += m;
                    float tD = tc[D], tF;
                    if (m > 0f)
                    {
                        int xF = x - cx, yF = y - cy, zF = z - cz;
                        bool has = xF >= 0 && xF < Nx && yF >= 0 && yF < Ny && zF >= 0 && zF < Nz && cell[xF + Nx * (yF + Ny * zF)] != VoxelGrid.Solid
                            && (lid || !LinkCut(nearBoundary, count, i, p));
                        tF = HybridFaceTheta(tA, tD, has ? tc[xF + Nx * (yF + Ny * zF)] : 0f, has);
                    }
                    else
                    {
                        int xF = xD + cx, yF = yD + cy, zF = zD + cz;
                        bool has = xF >= 0 && xF < Nx && yF >= 0 && yF < Ny && zF >= 0 && zF < Nz && cell[xF + Nx * (yF + Ny * zF)] != VoxelGrid.Solid
                            && (lid || !LinkCut(nearBoundary, count, D, pb));
                        tF = HybridFaceTheta(tD, tA, has ? tc[xF + Nx * (yF + Ny * zF)] : 0f, has);
                    }
                    adv += m * tF;
                    if (diag != null && !(mask[D] == 1 && HybridFresh(grid, cD, xD, yD, zD))) { if (m > 0f) { dOutT += (double)m * tF; dOutM += m; } else dInM -= m; }
                }

                float diff = 0f;
                for (int a = 0; a < 6; a++)
                {
                    int pa = 1 + a;
                    int zD = z + HCz[pa];
                    if (zD < 0 || zD >= Nz) continue;
                    int xD = x + HCx[pa], yD = y + HCy[pa];
                    int D = xD + Nx * (yD + Ny * zD);
                    byte cD = cell[D];
                    if (cD == VoxelGrid.Solid || cD == VoxelGrid.Fixed || LinkCut(nearBoundary, count, i, HOpp[pa]) || mask[D] == 2) continue;   // a patch face carries the scalar by advection only
                    bool fresh = HybridFresh(grid, cD, xD, yD, zD);
                    float kD = fresh ? MathF.Min(kMol + nuT[D] / AgeScT, AgeKappaMax) : kA;
                    float dF = 0.5f * (kA + kD) * (tc[D] - tA);
                    diff += dF;
                    if (diag != null && !(mask[D] == 1 && fresh)) dDif += dF;
                }

                float rhoNew = rhoA - sumM;
                float s = src != null ? src[i] : dt;
                tn[i] = (rhoA * tA - adv + diff + rhoA * s) / rhoNew;
                if (diag != null) dSrc += (double)rhoA * s;
            }
            if (diag != null) { diag[5 * rowIdx] = dSrc; diag[5 * rowIdx + 1] = dOutT; diag[5 * rowIdx + 2] = dOutM; diag[5 * rowIdx + 3] = dInM; diag[5 * rowIdx + 4] = dDif; }
        });
    }

    // ---- GPU twin ----

    static bool _gpuAgeChecked;
    static readonly object GpuAgeLock = new();
    static Action<Index1D, ArrayView<int>, ArrayView<byte>, ArrayView<int>, ArrayView<float>, float>? _ageRingKernel;
    static Action<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<int>, ArrayView<float>, ArrayView<float>, ArrayView<byte>, ArrayView<float>,
        ArrayView<float>, ArrayView<float>, ArrayView<float>, GridDims, AgeParams>? _ageKernel;
    static Action<Index1D, ArrayView<float>, ArrayView<float>>? _ageSumKernel;

    static void EnsureGpuAge()
    {
        if (_gpuAgeChecked) return;
        lock (GpuAgeLock)
        {
            if (_gpuAgeChecked) return;
            var accel = _accel!;
            _ageRingKernel = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>, ArrayView<byte>, ArrayView<int>, ArrayView<float>, float>(HybridRingKernel);
            _ageKernel = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<int>, ArrayView<float>, ArrayView<float>, ArrayView<byte>,
                ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, GridDims, AgeParams>(AgeKernel);
            _ageSumKernel = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>>(AgeSumKernel);
            _ageFollowKernel = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>, ArrayView<int>, ArrayView<float>>(AgeFollowKernel);
            _gpuAgeChecked = true;
        }
    }

    static void AgeSumKernel(Index1D i, ArrayView<float> t, ArrayView<float> sum) => sum[i] += t[i];

    static Action<Index1D, ArrayView<int>, ArrayView<int>, ArrayView<float>>? _ageFollowKernel;

    /// <summary>Follower k (a return / fan patch cell) takes the mean of its solved neighbours (nb: 6 per follower, -1 = none).</summary>
    static void AgeFollowKernel(Index1D k, ArrayView<int> idx, ArrayView<int> nb, ArrayView<float> t)
    {
        float s = 0f; int n = 0;
        for (int j = 0; j < 6; j++) { int c = nb[6 * k + j]; if (c >= 0) { s += t[c]; n++; } }
        if (n > 0) t[idx[k]] = s / n;
    }

    /// <summary>GPU twin of <see cref="RunStepAge"/>; <paramref name="src"/> is a dummy of length 1 for the age (Length &lt;= count: the uniform Dt).</summary>
    static void AgeKernel(Index1D i, ArrayView<byte> cellType, ArrayView<byte> nearBoundary, ArrayView<int> dir, ArrayView<float> cur, ArrayView<float> nxt,
        ArrayView<byte> mask, ArrayView<float> tc, ArrayView<float> tn, ArrayView<float> nuT, ArrayView<float> src, GridDims dims, AgeParams ap)
    {
        int idx = i;
        if (cellType[idx] != VoxelGrid.Air || mask[idx] != 1) return;
        int Nx = dims.Nx, Ny = dims.Ny, Nz = dims.Nz, count = dims.Count;
        int x = idx % Nx; int tq = idx / Nx; int y = tq % Ny; int z = tq / Ny;
        if (x == 0 || x == Nx - 1 || y == 0 || y == Ny - 1 || (z == 0 && dims.OpenZMin != 0) || (z == Nz - 1 && dims.OpenZMax != 0)) return;

        float kA = MathF.Min(ap.KMol + nuT[idx] * ap.ScTInv, ap.KMax), tA = tc[idx];
        float rhoA = 0f;
        for (int p = 0; p < 19; p++) rhoA += nxt[p * count + idx];

        float sumM = 0f, adv = 0f;
        for (int p = 1; p < 19; p++)
        {
            int cx = dir[p], cy = dir[19 + p], cz = dir[38 + p], pb = dir[57 + p];
            int zD = z + cz;
            bool lid = zD < 0 || zD >= Nz;
            if (lid) { if (cx == 0 && cy == 0) continue; cz = 0; zD = z; pb = dir[76 + pb]; }
            int xD = x + cx, yD = y + cy;
            int D = xD + Nx * (yD + Ny * zD);
            byte cD = cellType[D];
            if (cD == VoxelGrid.Solid || !lid && LinkCutSoA(nearBoundary, count, idx, pb)) continue;
            float fOut = nxt[p * count + idx], arrive;
            if (HybridFreshDev(cD, xD, yD, zD, Nx, Ny, Nz, dims.OpenZMin, dims.OpenZMax)) arrive = nxt[pb * count + D];
            else if (!lid && cD == VoxelGrid.Fixed && (nearBoundary[D] & 1) != 0)
                arrive = SupplyBounce(fOut, cur[pb * count + D], cur[p * count + D], nearBoundary[D]);
            else arrive = cur[pb * count + D];
            float m = fOut - arrive;
            sumM += m;
            float tD = tc[D], tF;
            if (m > 0f)
            {
                int xF = x - cx, yF = y - cy, zF = z - cz;
                bool has = xF >= 0 && xF < Nx && yF >= 0 && yF < Ny && zF >= 0 && zF < Nz && cellType[xF + Nx * (yF + Ny * zF)] != VoxelGrid.Solid
                    && (lid || !LinkCutSoA(nearBoundary, count, idx, p));
                tF = HybridFaceTheta(tA, tD, has ? tc[xF + Nx * (yF + Ny * zF)] : 0f, has);
            }
            else
            {
                int xF = xD + cx, yF = yD + cy, zF = zD + cz;
                bool has = xF >= 0 && xF < Nx && yF >= 0 && yF < Ny && zF >= 0 && zF < Nz && cellType[xF + Nx * (yF + Ny * zF)] != VoxelGrid.Solid
                    && (lid || !LinkCutSoA(nearBoundary, count, D, pb));
                tF = HybridFaceTheta(tD, tA, has ? tc[xF + Nx * (yF + Ny * zF)] : 0f, has);
            }
            adv += m * tF;
        }

        float diff = 0f;
        for (int a = 0; a < 6; a++)
        {
            int pa = 1 + a;
            int zD = z + dir[38 + pa];
            if (zD < 0 || zD >= Nz) continue;
            int xD = x + dir[pa], yD = y + dir[19 + pa];
            int D = xD + Nx * (yD + Ny * zD);
            byte cD = cellType[D];
            if (cD == VoxelGrid.Solid || cD == VoxelGrid.Fixed || LinkCutSoA(nearBoundary, count, idx, dir[57 + pa]) || mask[D] == 2) continue;
            float kD = HybridFreshDev(cD, xD, yD, zD, Nx, Ny, Nz, dims.OpenZMin, dims.OpenZMax) ? MathF.Min(ap.KMol + nuT[D] * ap.ScTInv, ap.KMax) : kA;
            diff += 0.5f * (kA + kD) * (tc[D] - tA);
        }

        float rhoNew = rhoA - sumM;
        float s = src.Length == count ? src[idx] : ap.Dt;   // RU-09: the CPU's rule (a count-long source); the 1-element dummy means none
        tn[idx] = (rhoA * tA - adv + diff + rhoA * s) / rhoNew;
    }

    public sealed partial class LbmSession
    {
        bool _ageWanted, _ageOn;   // _ageWanted: RunOptions.OnlineAge (the eddy-viscosity plane exists); _ageOn: StartOnlineAge has run
        byte[] _ageMask = [];               // per cell: 1 solved, 2 held at 0 and no diffusion across it (fresh-air patch), 0 held
        float[] _ageCur = [], _ageNxt = [], _ageSum = [], _ageSrc = [];
        int[] _ageFollow = [], _ageFollowNb = [];   // return / fan patch cells and, 6 per cell, the solved cells they follow
        int _ageSamples;
        double[]? _ageDiag;                 // CPU Sandbox check: per-row scratch of RunStepAge, and the running totals below
        double[] _ageBudget = new double[5];
        MemoryBuffer1D<byte, Stride1D.Dense>? _dAgeMask;
        MemoryBuffer1D<float, Stride1D.Dense>? _dAgeCur, _dAgeNxt, _dAgeSum, _dAgeSrc;
        MemoryBuffer1D<int, Stride1D.Dense>? _dAgeFollow, _dAgeFollowNb;

        /// <summary>The plane of the GPU sums buffer that holds this step's eddy viscosity (the one after rho, u, nuT[, T]).</summary>
        int AgeNuPlane => _thermal ? 6 : 5;

        /// <summary>Sandbox check (CPU): accumulate the scalar's budget over the steps that follow: [0] source added (rho* x increment), [1] scalar and [2] mass
        /// leaving through held cells (patches, ring, held interior), [3] mass entering that way, [4] diffusive scalar inflow from held cells. Lattice units
        /// (mass per step in cell volumes, the scalar in seconds); read with <see cref="AgeBudget"/>.</summary>
        internal void AgeDiagnostics(bool on) { _ageDiag = on && !_useGpu ? new double[5 * _grid.Nz * Math.Max(_grid.Ny - 2, 1)] : null; Array.Clear(_ageBudget); }
        internal double[] AgeBudget => _ageBudget;
        /// <summary>The scalar plane as it stands now (the caller's cell order); GPU downloads it. Sandbox check.</summary>
        internal float[] AgeNow() => _useGpu ? _dAgeCur!.GetAsArray1D() : (float[])_ageCur.Clone();
        /// <summary>Sandbox check: samples in the batch sum so far.</summary>
        internal int AgeSamples => _ageSamples;

        /// <summary>True once <see cref="StartOnlineAge"/> has run: <see cref="ToField"/> then carries <see cref="FlowField.Age"/>.</summary>
        public bool OnlineAgeOn => _ageOn;
        /// <summary>Why the online age is not available (empty when it is, or was never asked for).</summary>
        public string OnlineAgeNote { get; private set; } = "";

        /// <summary>Starts the online age from the frozen steady age <paramref name="frozen"/> (ScalarSolver.Solve on the settle-window mean flow, one value
        /// per cell of the caller's grid; NaN outside the building and in solid). Held cells follow ScalarSolver's own classification: NaN = outdoors,
        /// 0 = fresh air (supply patches, diffuser outlets), the 24 h cap = sealed pocket. Needs <see cref="RunOptions.OnlineAge"/> at session start.
        /// Call it right before a <see cref="ResetAveraging"/>: the batch sums restart there. False (with <see cref="OnlineAgeNote"/>) when unavailable.</summary>
        public bool StartOnlineAge(float[] frozen)
        {
            if (!_ageWanted || _inert) { OnlineAgeNote = _inert ? "no flow to carry it" : "not requested for this session"; return false; }
            var g = _grid; var src = _source; int n = g.Count;
            if (frozen.Length != n) { OnlineAgeNote = "the steady age is on another grid"; return false; }
            var init = new float[n]; var mask = new byte[n];
            var cutAir = new List<int>();
            for (int i = 0; i < n; i++)
            {
                byte c = g.Cell[i];
                if (c == VoxelGrid.Solid) continue;
                float a = frozen[i];
                if (float.IsNaN(a)) { if (src.Cell[i] == VoxelGrid.Solid) cutAir.Add(i); continue; }   // outdoors / unanalysed: held at 0; a cut wall cell the LBM streams: below
                init[i] = a;
                if (a <= 0f) mask[i] = 2;
                else if (a < ScalarSolver.AgeCapSeconds * 0.999f && c == VoxelGrid.Air) mask[i] = 1;
            }
            // ponytail: a wall cell the LBM streams (LbmAir, solid in the caller's mask) has no steady age; it starts at the mean of its solved neighbours
            // and is solved with them (held at 0 when none is), ceiling: a facade cell next to open air outdoors; upgrade: a geometric indoor test.
            int Nx = g.Nx, Ny = g.Ny, Nz = g.Nz;
            foreach (int i in cutAir)
            {
                int x = i % Nx, y = i / Nx % Ny, z = i / (Nx * Ny);
                float s = 0f; int k = 0;
                void Add(int j) { if (mask[j] == 1) { s += init[j]; k++; } }
                if (x > 0) Add(i - 1); if (x < Nx - 1) Add(i + 1); if (y > 0) Add(i - Nx); if (y < Ny - 1) Add(i + Nx);
                if (z > 0) Add(i - Nx * Ny); if (z < Nz - 1) Add(i + Nx * Ny);
                if (k > 0 && g.Cell[i] == VoxelGrid.Air) { init[i] = s / k; mask[i] = 1; }
            }
            var follow = new List<int>();
            for (int i = 0; i < n; i++)
                if (g.Cell[i] == VoxelGrid.Fixed && mask[i] == 0 && init[i] > 0f && init[i] < ScalarSolver.AgeCapSeconds * 0.999f) follow.Add(i);
            StartOnlineScalar(init, mask, null, follow.ToArray());
            return true;
        }

        /// <summary>The scalar engine itself: <paramref name="init"/> per cell, <paramref name="mask"/> as above, <paramref name="source"/> the per-cell increment per
        /// step (null = the age: dt seconds per step). Internal so the tracer-conservation check can drive a point source through the same scheme.</summary>
        internal void StartOnlineScalar(float[] init, byte[] mask, float[]? source, int[]? follow = null)
        {
            if (!_ageWanted) throw new InvalidOperationException("RunOptions.OnlineAge was not set for this session");
            int count = _grid.Count;
            DisposeAge();
            _ageMask = mask; _ageCur = (float[])init.Clone(); _ageNxt = (float[])init.Clone(); _ageSum = new float[count]; _ageSrc = source ?? [];
            _ageSamples = 0; OnlineAgeNote = "";
            _ageFollow = follow ?? []; _ageFollowNb = new int[6 * _ageFollow.Length];
            {
                int Nx = _grid.Nx, Ny = _grid.Ny, Nz = _grid.Nz;
                for (int f = 0; f < _ageFollow.Length; f++)
                {
                    int i = _ageFollow[f], x = i % Nx, y = i / Nx % Ny, z = i / (Nx * Ny);
                    int[] nbs = [x > 0 ? i - 1 : -1, x < Nx - 1 ? i + 1 : -1, y > 0 ? i - Nx : -1, y < Ny - 1 ? i + Nx : -1, z > 0 ? i - Nx * Ny : -1, z < Nz - 1 ? i + Nx * Ny : -1];
                    for (int j = 0; j < 6; j++) _ageFollowNb[6 * f + j] = nbs[j] >= 0 && mask[nbs[j]] == 1 ? nbs[j] : -1;
                }
            }
            if (_useGpu)
            {
                var accel = _acc!;
                EnsureGpuAge();
                if (_ageFollow.Length > 0) { _dAgeFollow = accel.Allocate1D(_ageFollow); _dAgeFollowNb = accel.Allocate1D(_ageFollowNb); }
                _dAgeMask = accel.Allocate1D(mask); _dAgeCur = accel.Allocate1D(init); _dAgeNxt = accel.Allocate1D(init);
                _dAgeSum = accel.Allocate1D<float>(count); _dAgeSum.View.MemSetToZero();
                _dAgeSrc = accel.Allocate1D(source ?? new float[1]);
                accel.Synchronize();
            }
            else if (_instant.Length != count) _instant = new float[count];
            _ageOn = true;
        }

        void DisposeAge()
        {
            _dAgeMask?.Dispose(); _dAgeCur?.Dispose(); _dAgeNxt?.Dispose(); _dAgeSum?.Dispose(); _dAgeSrc?.Dispose(); _dAgeFollow?.Dispose(); _dAgeFollowNb?.Dispose();
            _dAgeMask = null; _dAgeCur = null; _dAgeNxt = null; _dAgeSum = null; _dAgeSrc = null; _dAgeFollow = null; _dAgeFollowNb = null;
        }

        void ResetAge()
        {
            _ageSamples = 0;
            if (_ageSum.Length > 0) Array.Clear(_ageSum);
            _dAgeSum?.View.MemSetToZero();
        }

        /// <summary>One step of the age scalar, after the flow pass of a step that accumulates (before the buffers swap). No-op until started.</summary>
        void AgeStep(bool accumulate)
        {
            if (!_ageOn || !accumulate) return;
            var grid = _grid; int count = grid.Count;
            float kMol = ScalarSolver.Diffusivity * _dt / (_dx * _dx);
            bool sample = _steps % AgeSampleSteps == 0;
            if (_useGpu)
            {
                int n = _bl.Index.Length, ringCount = n - _bl.FixedCount;
                if (ringCount > 0) _ageRingKernel!(ringCount, _dBIndex!.View, _dBClass!.View, _dBNeighborSlot!.View, _dAgeCur!.View, 0f);
                if (_ageFollow.Length > 0) _ageFollowKernel!(_ageFollow.Length, _dAgeFollow!.View, _dAgeFollowNb!.View, _dAgeCur!.View);
                _ageKernel!(count, _dCellType!.View, _dNearBoundary!.View, _dDir!.View, _dCur!.View, _dNxt!.View, _dAgeMask!.View, _dAgeCur!.View, _dAgeNxt!.View,
                    _dSums!.View.SubView((long)AgeNuPlane * count, count), _dAgeSrc!.View, _dims, new AgeParams { Dt = _dt, KMol = kMol, KMax = AgeKappaMax, ScTInv = 1f / AgeScT });
                (_dAgeCur, _dAgeNxt) = (_dAgeNxt, _dAgeCur);
                if (sample) { _ageSumKernel!(count, _dAgeCur!.View, _dAgeSum!.View); _ageSamples++; }
            }
            else
            {
                SetBoundaryThermalRingHybrid(_ageCur, _bl, 0f);
                for (int f = 0; f < _ageFollow.Length; f++)
                {
                    float s = 0f; int k = 0;
                    for (int j = 0; j < 6; j++) { int c = _ageFollowNb[6 * f + j]; if (c >= 0) { s += _ageCur[c]; k++; } }
                    if (k > 0) _ageCur[_ageFollow[f]] = s / k;
                }
                RunStepAge(grid, _nearBoundary, _cur, _nxt, _ageMask, _ageCur, _ageNxt, _instant, _ageSrc.Length == count ? _ageSrc : null, _dt, kMol, _ageDiag);
                if (_ageDiag != null)
                    for (int r = 0; r < _ageDiag.Length / 5; r++) for (int k = 0; k < 5; k++) _ageBudget[k] += _ageDiag[5 * r + k];
                (_ageCur, _ageNxt) = (_ageNxt, _ageCur);
                if (sample)
                {
                    var t = _ageCur; var sum = _ageSum;
                    Parallel.For(0, count, i => sum[i] += t[i]);
                    _ageSamples++;
                }
            }
        }

        /// <summary>The time mean of the scalar over the samples since the last <see cref="ResetAveraging"/>: NaN where the cell is held; empty with no sample
        /// yet or a non-finite value (then <see cref="OnlineAgeNote"/> says so and the caller falls back to the steady age).</summary>
        float[] ReadAge()
        {
            if (!_ageOn || _ageSamples == 0) return [];
            var sum = _useGpu ? _dAgeSum!.GetAsArray1D() : _ageSum;
            var mean = new float[sum.Length];
            float inv = 1f / _ageSamples;
            bool bad = false;
            for (int i = 0; i < mean.Length; i++)
            {
                if (_ageMask[i] != 1) { mean[i] = float.NaN; continue; }
                mean[i] = sum[i] * inv;
                bad |= !float.IsFinite(mean[i]);
            }
            if (bad) { OnlineAgeNote = "the online age was not finite"; return []; }
            return mean;
        }
    }
}

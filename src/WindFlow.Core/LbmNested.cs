using System.Diagnostics;
using System.Numerics;
using ILGPU;
using ILGPU.Runtime;

namespace WindFlow.Core;

// One-way 2:1 nesting (PLAN.md Phase 3 step 5): a fine lattice at Dx/2 on a box inside a coarse run,
// its faces driven by the coarse field. See NestedSession.
public static partial class LbmSolver
{
    internal const int NestedSpongeDefault = 8;     // fine cells of sponge band inside the driven faces
    const float NestedSpongeMax = 0.5f;     // per-fine-step blend toward the coarse state at the band's outer edge
    const int NcInflow = 0, NcOutflow = 1, NcSponge = 2;

    /// <summary>Opens a fine lattice at half the coarse Dx on <paramref name="domain"/> (snapped outward to
    /// coarse cell faces and clipped to the coarse domain), driven one-way by <paramref name="coarse"/>:
    /// <list type="bullet">
    /// <item>every air cell on a driven face (the four sides, the top unless it is the coarse lid, the
    /// bottom unless it is the ground) is classified each coarse step by the sign of the coarse velocity
    /// against the outward face normal. Inflow cells get the full coarse state -- trilinear rho and u, plus
    /// the non-equilibrium part rebuilt from the coarse strain rate and rescaled for the halved Dx and dt
    /// (Pi_neq,f = (tau_f / 2 tau_c) Pi_neq,c, Dupuis and Chopard 2003). Outflow cells are a pressure outlet:
    /// the coarse density with a zero-gradient velocity taken from the fine cell inward;</item>
    /// <item>solid face cells stay walls;</item>
    /// <item>a sponge band <paramref name="spongeCells"/> fine cells deep (8-16 is the useful range) blends
    /// the fine populations toward the same coarse state, strongest at the face, so what the fine grid
    /// resolves differently cannot reflect off the driven faces.</item></list>
    /// Both lattices share the coarse velocity scale, so dt halves with Dx and lattice velocities and
    /// densities carry over unchanged. The fine interior starts from the interpolated coarse state.
    /// <para>LIMITATIONS: one-way (the fine result never corrects the coarse run); isothermal only
    /// (thermal runs throw); the coarse state is held constant over the two fine steps of a coarse step
    /// (no time interpolation); the non-equilibrium rebuild uses the molecular tau, not the local eddy
    /// viscosity.</para></summary>
    /// <param name="driveCoarse">true: every <see cref="NestedSession.Step"/> also steps the coarse session
    /// (one coarse step per two fine). false: the caller steps the coarse session itself and the fine box
    /// reads whatever state it is in.</param>
    public static NestedSession StartNested(LbmSession coarse, DomainBox domain, RunOptions options,
        bool driveCoarse = false, int spongeCells = NestedSpongeDefault)
    {
        ArgumentNullException.ThrowIfNull(coarse);
        if (coarse.NestInert) throw new InvalidOperationException("StartNested: the coarse session has no flow to nest in");
        if (options.Thermal || coarse.NestThermal)
            throw new NotSupportedException("StartNested: nested fine boxes are isothermal only (v1)");
        if (!domain.IsValid) throw new ArgumentException("StartNested: the box must be at least a metre on every axis", nameof(domain));

        var cg = coarse.NestGrid;
        var model = coarse.NestModel;
        float dxc = cg.Dx;
        var cMax = cg.Origin + new Vector3(cg.Nx, cg.Ny, cg.Nz) * dxc;
        Vector3 Snap(Vector3 p, bool up)
        {
            var k = (p - cg.Origin) / dxc;
            var s = up ? new Vector3(MathF.Ceiling(k.X - 1e-4f), MathF.Ceiling(k.Y - 1e-4f), MathF.Ceiling(k.Z - 1e-4f))
                       : new Vector3(MathF.Floor(k.X + 1e-4f), MathF.Floor(k.Y + 1e-4f), MathF.Floor(k.Z + 1e-4f));
            return cg.Origin + s * dxc;
        }
        var lo = Vector3.Max(Snap(domain.Min, false), cg.Origin);
        var hi = Vector3.Min(Snap(domain.Max, true), cMax);
        lo.Z = MathF.Max(lo.Z, model.GroundZ);                 // the ground layer is never part of the box
        var box = new DomainBox(lo, hi);
        if (!box.IsValid) throw new ArgumentException("StartNested: the box does not overlap the coarse domain by a metre on every axis", nameof(domain));

        var fineGrid = Voxelizer.BuildNested(model, box, 0.5f * dxc, options.MaxDegreeOfParallelism);
        bool nestTop = MathF.Abs(hi.Z - cMax.Z) > 0.25f * dxc;
        bool nestBottom = lo.Z > model.GroundZ + 1e-3f;
        fineGrid.OpenZMin = nestBottom; fineGrid.OpenZMax = nestTop;
        var fine = new LbmSession(fineGrid, model, coarse.NestWind, options,
            uMaxOverride: coarse.NestVelScale * ULat, nestedRing: true);
        return new NestedSession(coarse, fine, driveCoarse, Math.Clamp(spongeCells, 0, 32), nestTop, nestBottom, box);
    }

    public sealed partial class LbmSession
    {
        internal VoxelGrid NestGrid => _grid;      // the LBM view (6a): what the coupling interpolates and drives
        internal VoxelGrid NestSource => _source;  // the grid the caller built (conservative mask), for analysis
        internal BuildingModel NestModel => _model;
        internal Vector2 NestWind => _wind;
        internal float NestVelScale => _velScale;
        internal float NestTau0 => _tau0;
        internal bool NestGpu => _useGpu;
        internal bool NestInert => _inert;
        internal bool NestThermal => _thermal;
        internal float[] NestCur => _cur;
        internal float[] NestNxt => _nxt;
        internal MemoryBuffer1D<float, Stride1D.Dense>? NestDCur => _dCur;
        internal MemoryBuffer1D<float, Stride1D.Dense>? NestDNxt => _dNxt;
        internal Accelerator? NestAcc => _acc;
        /// <summary>Wall time of steps taken through <see cref="StepQueued"/>, which does not time itself.</summary>
        internal void NestAddElapsed(double seconds) => _elapsed += seconds;
    }

    // ---- per-cell math, shared by the CPU loop and the GPU kernel ---------------------------------

    /// <summary>Regularized non-equilibrium populations of a momentum-flux tensor:
    /// f_neq_i = w_i / (2 cs^4) Q_i : Pi, Q_i = c_i c_i - cs^2 I.</summary>
    static F19 ComputeNeq(float pxx, float pyy, float pzz, float pxy, float pxz, float pyz)
    {
        float t3 = (pxx + pyy + pzz) * (1f / 3f);
        float a = 0.25f * (pxx - t3), b = 0.25f * (pyy - t3), c = 0.25f * (pzz - t3);
        float xyP = 0.125f * (pxx + pyy + 2f * pxy - t3), xyM = 0.125f * (pxx + pyy - 2f * pxy - t3);
        float xzP = 0.125f * (pxx + pzz + 2f * pxz - t3), xzM = 0.125f * (pxx + pzz - 2f * pxz - t3);
        float yzP = 0.125f * (pyy + pzz + 2f * pyz - t3), yzM = 0.125f * (pyy + pzz - 2f * pyz - t3);
        return new F19(-1.5f * t3, a, a, b, b, c, c, xyP, xyP, xyM, xyM, xzP, xzP, xzM, xzM, yzP, yzP, yzM, yzM);
    }

    static F19 AddF(in F19 p, in F19 q) => new(
        p.F0 + q.F0, p.F1 + q.F1, p.F2 + q.F2, p.F3 + q.F3, p.F4 + q.F4, p.F5 + q.F5, p.F6 + q.F6, p.F7 + q.F7,
        p.F8 + q.F8, p.F9 + q.F9, p.F10 + q.F10, p.F11 + q.F11, p.F12 + q.F12, p.F13 + q.F13, p.F14 + q.F14,
        p.F15 + q.F15, p.F16 + q.F16, p.F17 + q.F17, p.F18 + q.F18);

    static F19 BlendF(in F19 p, in F19 q, float s) => new(
        p.F0 + s * (q.F0 - p.F0), p.F1 + s * (q.F1 - p.F1), p.F2 + s * (q.F2 - p.F2), p.F3 + s * (q.F3 - p.F3),
        p.F4 + s * (q.F4 - p.F4), p.F5 + s * (q.F5 - p.F5), p.F6 + s * (q.F6 - p.F6), p.F7 + s * (q.F7 - p.F7),
        p.F8 + s * (q.F8 - p.F8), p.F9 + s * (q.F9 - p.F9), p.F10 + s * (q.F10 - p.F10), p.F11 + s * (q.F11 - p.F11),
        p.F12 + s * (q.F12 - p.F12), p.F13 + s * (q.F13 - p.F13), p.F14 + s * (q.F14 - p.F14), p.F15 + s * (q.F15 - p.F15),
        p.F16 + s * (q.F16 - p.F16), p.F17 + s * (q.F17 - p.F17), p.F18 + s * (q.F18 - p.F18));

    /// <summary>New post-collision populations of one nested cell. Inflow: the coarse state. Outflow: coarse
    /// rho, the inward fine neighbour's velocity with any inflowing normal component removed. Sponge: the
    /// cell's own populations blended by <paramref name="sigma"/> toward the coarse velocity and strain AT
    /// THE CELL'S OWN DENSITY -- both states then carry the same mass, so the band only exchanges momentum.
    /// Blending toward the coarse density as well made the sponge a mass source wherever the two runs
    /// disagreed on pressure (a 21 % box ledger residual on the sample house).</summary>
    static F19 NestedCell(in F19 own, int cls, float rho, float ux, float uy, float uz,
        float pxx, float pyy, float pzz, float pxy, float pxz, float pyz, float sigma,
        float nbx, float nby, float nbz, float nx, float ny, float nz)
    {
        if (cls == NcOutflow)
        {
            float nn = nx * nx + ny * ny + nz * nz;
            float un = nbx * nx + nby * ny + nbz * nz;
            if (un < 0f && nn > 0f) { float s = un / nn; nbx -= s * nx; nby -= s * ny; nbz -= s * nz; }
            return ComputeFeq(rho, nbx, nby, nbz);
        }
        if (cls == NcSponge)
            rho = own.F0 + own.F1 + own.F2 + own.F3 + own.F4 + own.F5 + own.F6 + own.F7 + own.F8 + own.F9 + own.F10
                + own.F11 + own.F12 + own.F13 + own.F14 + own.F15 + own.F16 + own.F17 + own.F18;
        F19 target = AddF(ComputeFeq(rho, ux, uy, uz), ComputeNeq(pxx, pyy, pzz, pxy, pxz, pyz));
        return cls == NcInflow ? target : BlendF(own, target, sigma);
    }

    // ---- GPU kernels (JITted on first nested use) --------------------------------------------------

    static bool _gpuNestedChecked;
    static readonly object GpuNestedLock = new();
    static Action<Index1D, ArrayView<int>, ArrayView<float>, ArrayView<float>, int, int, int>? _nestedCellKernel;
    static Action<Index1D, ArrayView<int>, ArrayView<float>, ArrayView<float>, int, ArrayView<float>, int>? _nestedMomentsKernel;
    static Action<Index1D, ArrayView<float>, ArrayView<int>, ArrayView<float>, int>? _nestedGradKernel;
    static Action<Index1D, ArrayView<int>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>,
        ArrayView<float>, int, int, float, int>? _nestedTargetKernel;

    static void EnsureGpuNested()
    {
        if (_gpuNestedChecked) return;
        lock (GpuNestedLock)
        {
            if (_gpuNestedChecked) return;
            var accel = _accel!;
            _nestedCellKernel = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>, ArrayView<float>, ArrayView<float>, int, int, int>(NestedCellKernel);
            _nestedMomentsKernel = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>, ArrayView<float>, ArrayView<float>, int, ArrayView<float>, int>(NestedMomentsKernel);
            _nestedGradKernel = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<int>, ArrayView<float>, int>(NestedGradKernel);
            _nestedTargetKernel = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
                ArrayView<float>, ArrayView<int>, ArrayView<float>, int, int, float, int>(NestedTargetKernel);
            _gpuNestedChecked = true;
        }
    }

    /// <summary>meta (SoA, n each): [0,n) fine cell, [n,2n) inward neighbour, [2n,3n) class | normal bits;
    /// tgt (SoA, 11 x n): rho, ux, uy, uz, Pxx, Pyy, Pzz, Pxy, Pxz, Pyz, sigma.</summary>
    static void NestedCellKernel(Index1D t, ArrayView<int> meta, ArrayView<float> tgt, ArrayView<float> cur, int count, int n, int k0)
    {
        int k = t + k0;   // P17-02: launched twice, faces [0, Faces) then the sponge [Faces, n)
        int idx = meta[k], nb = meta[n + k], info = meta[2 * n + k];
        int cls = info & 3;
        float nx = ((info >> 2) & 3) - 1, ny = ((info >> 4) & 3) - 1, nz = ((info >> 6) & 3) - 1;
        float bx = 0f, by = 0f, bz = 0f;
        if (cls == NcOutflow) NeighborMacroSoA(cur, nb, count, out bx, out by, out bz);
        F19 own = cls == NcSponge ? ReadSoA(cur, idx, count) : default;
        F19 f = NestedCell(own, cls, tgt[k], tgt[n + k], tgt[2 * n + k], tgt[3 * n + k], tgt[4 * n + k], tgt[5 * n + k],
            tgt[6 * n + k], tgt[7 * n + k], tgt[8 * n + k], tgt[9 * n + k], tgt[10 * n + k], bx, by, bz, nx, ny, nz);
        WriteSoA(cur, idx, count, f);
    }

    /// <summary>rho and m/rho of a list of coarse cells (SoA out: [0,n) rho, [n,2n) ux, [2n,3n) uy, [3n,4n) uz).
    /// An entry ~i (negative) is a boundary-list cell, read from the spare buffer -- see CellSet.Need.</summary>
    static void NestedMomentsKernel(Index1D k, ArrayView<int> idxs, ArrayView<float> cur, ArrayView<float> nxt, int count,
        ArrayView<float> outM, int n)
    {
        int e = idxs[k];
        F19 f = e < 0 ? ReadSoA(nxt, ~e, count) : ReadSoA(cur, e, count);
        float rho = f.F0 + f.F1 + f.F2 + f.F3 + f.F4 + f.F5 + f.F6 + f.F7 + f.F8 + f.F9 + f.F10 + f.F11 + f.F12 + f.F13 + f.F14 + f.F15 + f.F16 + f.F17 + f.F18;
        float inv = 1f / rho;
        outM[k] = rho;
        outM[n + k] = (f.F1 - f.F2 + f.F7 - f.F8 + f.F9 - f.F10 + f.F11 - f.F12 + f.F13 - f.F14) * inv;
        outM[2 * n + k] = (f.F3 - f.F4 + f.F7 - f.F8 - f.F9 + f.F10 + f.F15 - f.F16 + f.F17 - f.F18) * inv;
        outM[3 * n + k] = (f.F5 - f.F6 + f.F11 - f.F12 - f.F13 + f.F14 + f.F15 - f.F16 - f.F17 + f.F18) * inv;
    }

    /// <summary>Velocity gradient of one coarse stencil cell from its six neighbours' moments (central where both
    /// exist, one-sided where one is missing), stored as the strain rate (SoA, 6 x m): Sxx, Syy, Szz, Sxy, Sxz, Syz.
    /// Same arithmetic as the CPU loop in NestedSession.Refresh.</summary>
    static void NestedGradKernel(Index1D s, ArrayView<float> mom, ArrayView<int> nb, ArrayView<float> grad, int m)
    {
        float d00 = GradOne(mom, nb, s, m, 0, 0), d01 = GradOne(mom, nb, s, m, 0, 1), d02 = GradOne(mom, nb, s, m, 0, 2);
        float d10 = GradOne(mom, nb, s, m, 1, 0), d11 = GradOne(mom, nb, s, m, 1, 1), d12 = GradOne(mom, nb, s, m, 1, 2);
        float d20 = GradOne(mom, nb, s, m, 2, 0), d21 = GradOne(mom, nb, s, m, 2, 1), d22 = GradOne(mom, nb, s, m, 2, 2);
        grad[s] = d00; grad[m + s] = d11; grad[2 * m + s] = d22;
        grad[3 * m + s] = 0.5f * (d01 + d10); grad[4 * m + s] = 0.5f * (d02 + d20); grad[5 * m + s] = 0.5f * (d12 + d21);
    }

    /// <summary>d u_b / d x_a of stencil slot s (see NestedGradKernel).</summary>
    static float GradOne(ArrayView<float> mom, ArrayView<int> nb, int s, int m, int a, int b)
    {
        int lo = nb[6 * s + 2 * a], hi = nb[6 * s + 2 * a + 1];
        int row = (1 + b) * m;
        float u0 = mom[row + s];
        if (lo >= 0 && hi >= 0) return 0.5f * (mom[row + hi] - mom[row + lo]);
        if (hi >= 0) return mom[row + hi] - u0;
        if (lo >= 0) return u0 - mom[row + lo];
        return 0f;
    }

    /// <summary>Per fine cell: trilinear coarse rho, u and strain rate -> target (SoA 11 x n: rho, u, Pi_neq, sigma)
    /// and, when <paramref name="classify"/> is 1, the face cell's inflow/outflow class from the sign of u on its
    /// outward normal (written into meta's class row). Same arithmetic as the CPU loop in NestedSession.Refresh.</summary>
    static void NestedTargetKernel(Index1D k, ArrayView<int> sten, ArrayView<float> wts, ArrayView<float> mom, ArrayView<float> grad,
        ArrayView<float> sigma, ArrayView<int> meta, ArrayView<float> tgt, int m, int n, float piScale, int classify)
    {
        float rho = 0f, ux = 0f, uy = 0f, uz = 0f, sxx = 0f, syy = 0f, szz = 0f, sxy = 0f, sxz = 0f, syz = 0f, wSum = 0f;
        for (int c = 0; c < 8; c++)
        {
            int s = sten[8 * k + c];
            if (s < 0) continue;
            float w = wts[8 * k + c];
            wSum += w;
            rho += w * mom[s]; ux += w * mom[m + s]; uy += w * mom[2 * m + s]; uz += w * mom[3 * m + s];
            sxx += w * grad[s]; syy += w * grad[m + s]; szz += w * grad[2 * m + s];
            sxy += w * grad[3 * m + s]; sxz += w * grad[4 * m + s]; syz += w * grad[5 * m + s];
        }
        if (wSum <= 1e-6f) { rho = 1f; ux = 0f; uy = 0f; uz = 0f; sxx = 0f; syy = 0f; szz = 0f; sxy = 0f; sxz = 0f; syz = 0f; }
        float p = piScale * rho;
        tgt[k] = rho; tgt[n + k] = ux; tgt[2 * n + k] = uy; tgt[3 * n + k] = uz;
        tgt[4 * n + k] = p * sxx; tgt[5 * n + k] = p * syy; tgt[6 * n + k] = p * szz;
        tgt[7 * n + k] = p * sxy; tgt[8 * n + k] = p * sxz; tgt[9 * n + k] = p * syz;
        tgt[10 * n + k] = sigma[k];
        if (classify != 0)
        {
            int info = meta[2 * n + k];
            if ((info & 3) != NcSponge)
            {
                int nx = ((info >> 2) & 3) - 1, ny = ((info >> 4) & 3) - 1, nz = ((info >> 6) & 3) - 1;
                bool isIn = ux * nx + uy * ny + uz * nz < 0f;
                meta[2 * n + k] = (info & ~3) | (isIn ? NcInflow : NcOutflow);
            }
        }
    }

    /// <summary>A fine lattice at half the coarse Dx on a box, driven one-way by a coarse
    /// <see cref="LbmSession"/> (see <see cref="StartNested"/>). Same live API as the session: one
    /// <see cref="Step"/> is one COARSE step = two fine steps.</summary>
    public sealed class NestedSession : IDisposable
    {
        readonly LbmSession _coarse, _fine;
        readonly bool _drive, _nestTop, _nestBottom;
        readonly int _sponge;
        readonly DomainBox _box;
        CellSet _set = null!;
        long _steps;
        double _elapsed, _mlups;
        bool _disposed;

        internal NestedSession(LbmSession coarse, LbmSession fine, bool driveCoarse, int spongeCells, bool nestTop, bool nestBottom,
            DomainBox box)
        {
            _coarse = coarse; _fine = fine; _drive = driveCoarse; _sponge = spongeCells;
            _nestTop = nestTop; _nestBottom = nestBottom; _box = box;
            try
            {
                if (fine.NestGpu || coarse.NestGpu) EnsureGpuNested();
                WarmStart();
                _set = BuildBoundarySet();
                WarnSpongeBand();
            }
            catch { fine.Dispose(); throw; }
        }

        /// <summary>The fine grid (Dx = coarse Dx / 2).</summary>
        public VoxelGrid Grid => _fine.NestSource;
        /// <summary>The box actually solved: the requested one snapped outward to coarse cell faces.</summary>
        public DomainBox Box => _box;

        /// <summary>Voxelises <paramref name="model"/> on this box at this Dx -- the grid
        /// <see cref="UpdateCells"/> wants after an opening changes.</summary>
        public VoxelGrid BuildGrid(BuildingModel model) => Voxelizer.BuildNested(model, _box, Grid.Dx, null);
        /// <summary>Depth of the sponge band, metres, inward from every driven face (the face layer plus
        /// the blended cells). Air in it is pulled toward the coarse run, so an opening or device outlet
        /// inside it gets little from the finer grid; the fine grid carries a warning for each one.</summary>
        public float SpongeWidth => (_sponge + 1) * Grid.Dx;
        /// <summary>The coarse session driving this box.</summary>
        public LbmSession Coarse => _coarse;
        /// <summary>Coarse steps advanced by this object.</summary>
        public long Steps => _steps;
        /// <summary>Physical time the fine lattice has simulated, seconds.</summary>
        public double SimSeconds => _fine.SimSeconds;
        /// <summary>The coarse session's planned run length, in coarse steps (one <see cref="Step"/> each).</summary>
        public int PlannedSteps => _coarse.PlannedSteps;
        public int WarmupSteps => _coarse.WarmupSteps;
        public string Backend => _fine.Backend;
        /// <summary>Fine-cell updates per second of the most recent <see cref="Step"/>, millions, including
        /// the coarse step (when driven) and the face/sponge refresh.</summary>
        public double Mlups => _mlups;
        public int FineCells => Grid.Count;
        /// <summary>Driven face cells (inflow + outflow) and sponge cells, as last classified.</summary>
        public int FaceCells => _set.Faces;
        public int SpongeCells => _set.N - _set.Faces;
        /// <summary>Face cells classified inflow at the last refresh. On the GPU the classification stays on the
        /// device, so reading this synchronises and copies the class row back.</summary>
        public int InflowCells
        {
            get
            {
                if (!_set.OnDevice || _set.N == 0) return _set.Inflow;
                _fine.NestAcc!.Synchronize();
                _set.DMeta!.View.CopyToCPU(_set.Meta);
                int n = _set.N, inflow = 0;
                for (int k = 0; k < _set.Faces; k++) if ((_set.Meta[2 * n + k] & 3) == NcInflow) inflow++;
                return inflow;
            }
        }

        /// <summary>Advances <paramref name="coarseSteps"/> coarse steps: per coarse step, step the coarse
        /// session (when driving it), refresh the face and sponge targets from it, then two fine steps, each
        /// preceded by writing the driven cells.</summary>
        public void Step(int coarseSteps, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var sw = Stopwatch.StartNew();
            try
            {
                for (int s = 0; s < coarseSteps; s++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_drive) _coarse.StepQueued(1, ct);
                    Refresh(_set);
                    for (int h = 0; h < 2; h++)
                    {
                        Apply(_set, next: false);
                        _fine.StepQueued(1, ct);
                    }
                    _steps++;
                }
                // One device sync per call, not per coarse step: everything above is queued on one stream.
                if (_fine.NestGpu) _fine.NestAcc!.Synchronize();
                if (_coarse.NestGpu && _coarse.NestAcc != _fine.NestAcc) _coarse.NestAcc!.Synchronize();
            }
            catch (Exception ex) when (_fine.NestGpu && LbmSolver.IsGpuCallFailure(ex)) { throw new LbmSolver.GpuFaultException(ex); }   // RW-02: the nested GPU step
            sw.Stop();
            // GPU steps taken through StepQueued are not timed by the sessions themselves (CPU ones are):
            // split the wall time by cell updates.
            double fineShare = _drive ? 2.0 * Grid.Count / (2.0 * Grid.Count + _coarse.NestGrid.Count) : 1.0;
            if (_fine.NestGpu) _fine.NestAddElapsed(sw.Elapsed.TotalSeconds * fineShare);
            if (_drive && _coarse.NestGpu) _coarse.NestAddElapsed(sw.Elapsed.TotalSeconds * (1 - fineShare));
            _elapsed += sw.Elapsed.TotalSeconds;
            if (sw.Elapsed.TotalSeconds > 0) _mlups = (double)Grid.Count * 2 * coarseSteps / sw.Elapsed.TotalSeconds / 1e6;
        }

        public void ReadVelocity(float[] ux, float[] uy, float[] uz) => _fine.ReadVelocity(ux, uy, uz);
        public FlowField ToField() => _fine.ToField();

        /// <summary>Changes the wind of the coarse run (only when this object drives it -- otherwise the
        /// coarse session's owner does that).</summary>
        public void SetWind(Vector2 wind)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_drive) throw new InvalidOperationException("SetWind: this nested box does not drive its coarse session; call SetWind on the coarse session");
            _coarse.SetWind(wind);
        }

        /// <summary>New cell flags on the SAME fine domain (an opening opened or closed). Pass the matching
        /// coarse grid too when this object drives the coarse session. The face and sponge lists are rebuilt.</summary>
        public void UpdateCells(VoxelGrid newFineGrid, VoxelGrid? newCoarseGrid = null, BuildingModel? model = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (newCoarseGrid != null)
            {
                if (!_drive) throw new InvalidOperationException("UpdateCells: this nested box does not drive its coarse session");
                _coarse.UpdateCells(newCoarseGrid, model);
            }
            newFineGrid.OpenZMin = _nestBottom; newFineGrid.OpenZMax = _nestTop;
            _fine.UpdateCells(newFineGrid, model ?? _coarse.NestModel);
            _set.Dispose();
            _set = BuildBoundarySet();
            WarnSpongeBand();
        }

        /// <param name="startNow">Start the average at the current step even before the session's warm-up has run (a batch loop).</param>
        public void ResetAveraging(bool startNow = false)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _fine.ResetAveraging(startNow);
            if (_drive) _coarse.ResetAveraging(startNow);
        }

        /// <summary>Frees the fine lattice. The coarse session belongs to the caller and stays open.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _set?.Dispose();
            _fine.Dispose();
        }

        // ---- setup -----------------------------------------------------------------------------

        /// <summary>The fine cells and stencils one pass writes, and the buffers for their targets.</summary>
        sealed class CellSet : IDisposable
        {
            public int N, Faces, Inflow;
            public int[] Meta = [];          // 3N: cell, inward neighbour, class | normal bits
            public int[] Sten = [];          // 8N: slot in Need (-1 = none)
            public float[] Wts = [];         // 8N
            public float[] Sigma = [];       // N
            public float[] Tgt = [];         // 11N SoA
            // Coarse cell indices whose moments this set reads. A far-field ring or Fixed cell is stored as ~index:
            // the coarse run writes those into its current buffer at the START of a step, so between steps
            // the current buffer holds a stale copy and the live one is in the spare buffer.
            public int[] Need = [];
            public int[] NeedNb = [];        // 6 per Need entry: slot of the -x,+x,-y,+y,-z,+z neighbour (-1 none)
            public float[] Mom = [];         // 4 x Need SoA: rho, ux, uy, uz
            public float[] Grad = [];        // 6 x Need SoA: Sxx, Syy, Szz, Sxy, Sxz, Syz (per coarse cell)
            public MemoryBuffer1D<int, Stride1D.Dense>? DMeta, DNeed;
            public MemoryBuffer1D<float, Stride1D.Dense>? DTgt, DMom;
            // Whole refresh on the device (both lattices on the GPU): no host round trip per coarse step.
            public bool OnDevice;
            public MemoryBuffer1D<int, Stride1D.Dense>? DNeedNb, DSten;
            public MemoryBuffer1D<float, Stride1D.Dense>? DGrad, DWts, DSigma;

            public void Dispose()
            {
                DMeta?.Dispose(); DNeed?.Dispose(); DTgt?.Dispose(); DMom?.Dispose();
                DNeedNb?.Dispose(); DSten?.Dispose(); DGrad?.Dispose(); DWts?.Dispose(); DSigma?.Dispose();
                DMeta = null; DNeed = null; DTgt = null; DMom = null;
                DNeedNb = null; DSten = null; DGrad = null; DWts = null; DSigma = null;
            }
        }

        const string SpongeWarnTag = "inside the fine box's sponge band";

        /// <summary>Grid warning for every open opening and device outlet that reaches into the sponge band
        /// (or crosses a driven face): the box copies the coarse flow there. Replaces the previous pass's lines.</summary>
        void WarnSpongeBand()
        {
            var g = Grid;
            g.Warnings.RemoveAll(w => w.Contains(SpongeWarnTag, StringComparison.Ordinal));
            float band = SpongeWidth;
            var lo = g.Origin; var hi = g.Origin + new Vector3(g.Nx, g.Ny, g.Nz) * g.Dx;
            string[] faceNames = ["-X", "+X", "-Y", "+Y", "bottom", "top"];
            // Closest driven face to the part of [a, b] inside the box: (distance in metres, face name).
            (float Dist, string Face) Nearest(Vector3 a, Vector3 b)
            {
                a = Vector3.Max(a, lo); b = Vector3.Min(b, hi);
                Span<float> d = [a.X - lo.X, hi.X - b.X, a.Y - lo.Y, hi.Y - b.Y,
                    _nestBottom ? a.Z - lo.Z : float.MaxValue, _nestTop ? hi.Z - b.Z : float.MaxValue];
                int best = 0;
                for (int i = 1; i < 6; i++) if (d[i] < d[best]) best = i;
                return (MathF.Max(0f, d[best]), faceNames[best]);
            }
            string Advice(float dist) => $"grow the box by at least {band - dist + g.Dx:0.#} m on that side";

            foreach (var o in _coarse.NestModel.Openings)
            {
                if (!o.IsOpen) continue;
                var n = new Vector3(o.Normal.X, o.Normal.Y, 0f);
                if (n.LengthSquared() < 1e-6f) n = Vector3.UnitX; else n = Vector3.Normalize(n);
                var t = new Vector3(-n.Y, n.X, 0f);
                var half = new Vector3(MathF.Abs(t.X) * o.Width + MathF.Abs(n.X) * o.Depth,
                    MathF.Abs(t.Y) * o.Width + MathF.Abs(n.Y) * o.Depth, o.Height) * 0.5f;
                var a = o.Center - half; var b = o.Center + half;
                if (b.X <= lo.X || a.X >= hi.X || b.Y <= lo.Y || a.Y >= hi.Y || b.Z <= lo.Z || a.Z >= hi.Z) continue;
                var (dist, face) = Nearest(a, b);
                if (dist < band)
                    g.Warnings.Add($"Opening {(o.Name.Length > 0 ? o.Name : o.Id)} is {SpongeWarnTag} ({dist:0.##} m from the {face} face, " +
                        $"band {band:0.##} m): the box copies the coarse flow there, so it adds little accuracy at this opening - {Advice(dist)}");
            }
            foreach (var dev in g.Devices)
            {
                float best = float.MaxValue; string bestFace = "";
                for (int k = 0; k < dev.OutletCount; k++)
                {
                    int i = dev.Cells[k];
                    var c = g.CellCenter(i % g.Nx, i / g.Nx % g.Ny, i / (g.Nx * g.Ny));
                    var h = new Vector3(0.5f * g.Dx);
                    var (dist, face) = Nearest(c - h, c + h);
                    if (dist < best) { best = dist; bestFace = face; }
                }
                if (best < band)
                    g.Warnings.Add($"Device {dev.SourceName}: its outlet is {SpongeWarnTag} ({best:0.##} m from the {bestFace} face, " +
                        $"band {band:0.##} m): the box copies the coarse flow there - {Advice(best)}");
            }
        }

        static int Info(int cls, int nx, int ny, int nz) => cls | ((nx + 1) << 2) | ((ny + 1) << 4) | ((nz + 1) << 6);

        /// <summary>Driven face cells and the sponge band behind them.</summary>
        CellSet BuildBoundarySet()
        {
            var g = _fine.NestGrid;
            int Nx = g.Nx, Ny = g.Ny, Nz = g.Nz;
            var cells = new List<(int Idx, int Nb, int Info, float Sigma)>();
            int faces = 0;
            for (int z = 0; z < Nz; z++)
            for (int y = 0; y < Ny; y++)
            for (int x = 0; x < Nx; x++)
            {
                int idx = g.Index(x, y, z);
                if (g.Cell[idx] != VoxelGrid.Air) continue;   // walls stay walls; Fixed patches belong to the fine run
                int d = Math.Min(Math.Min(x, Nx - 1 - x), Math.Min(y, Ny - 1 - y));
                if (_nestTop) d = Math.Min(d, Nz - 1 - z);
                if (_nestBottom) d = Math.Min(d, z);
                if (d > _sponge) continue;
                if (d == 0)
                {
                    int nx = x == 0 ? -1 : x == Nx - 1 ? 1 : 0, ny = y == 0 ? -1 : y == Ny - 1 ? 1 : 0;
                    int nz = _nestTop && z == Nz - 1 ? 1 : _nestBottom && z == 0 ? -1 : 0;
                    int ix = Math.Clamp(x - nx, 0, Nx - 1), iy = Math.Clamp(y - ny, 0, Ny - 1), iz = Math.Clamp(z - nz, 0, Nz - 1);
                    int nb = g.Index(ix, iy, iz);
                    if (g.Cell[nb] == VoxelGrid.Solid) nb = idx;   // nothing to extrapolate from: hold the face value
                    cells.Add((idx, nb, Info(NcInflow, nx, ny, nz), 1f));
                    faces++;
                }
                else
                {
                    float r = (_sponge + 1f - d) / (_sponge + 1f);
                    cells.Add((idx, idx, Info(NcSponge, 0, 0, 0), NestedSpongeMax * r * r));
                }
            }
            // Faces first: the counts above and Refresh's classification rely on it.
            cells.Sort((a, b) => ((a.Info & 3) == NcSponge ? 1 : 0).CompareTo((b.Info & 3) == NcSponge ? 1 : 0));
            var set = Build(cells);
            set.Faces = faces;
            return set;
        }

        /// <summary>Starts every fine air cell from the interpolated coarse state instead of rest, so the box
        /// does not have to spin up from nothing while the coarse run is already developed.</summary>
        void WarmStart()
        {
            var g = _fine.NestGrid;
            var cells = new List<(int Idx, int Nb, int Info, float Sigma)>();
            for (int i = 0; i < g.Count; i++)
                if (g.Cell[i] == VoxelGrid.Air) cells.Add((i, i, Info(NcInflow, 0, 0, 0), 1f));
            using var set = Build(cells);
            Refresh(set, classify: false);
            Apply(set, next: false);
            Apply(set, next: true);
        }

        CellSet Build(List<(int Idx, int Nb, int Info, float Sigma)> cells)
        {
            var g = _fine.NestGrid; var cg = _coarse.NestGrid;
            int n = cells.Count;
            var set = new CellSet
            {
                N = n, Meta = new int[3 * n], Sten = new int[8 * n], Wts = new float[8 * n], Sigma = new float[n],
                Tgt = new float[11 * n],
            };
            var slotOf = new Dictionary<int, int>();
            var need = new List<int>();
            int Slot(int ci)
            {
                if (!slotOf.TryGetValue(ci, out int s)) { s = need.Count; slotOf[ci] = s; need.Add(ci); }
                return s;
            }

            for (int k = 0; k < n; k++)
            {
                var (idx, nb, info, sigma) = cells[k];
                set.Meta[k] = idx; set.Meta[n + k] = nb; set.Meta[2 * n + k] = info; set.Sigma[k] = sigma;
                int x = idx % g.Nx, y = idx / g.Nx % g.Ny, z = idx / (g.Nx * g.Ny);
                var c = (g.CellCenter(x, y, z) - cg.Origin) / cg.Dx - new Vector3(0.5f);
                int i0 = Math.Clamp((int)MathF.Floor(c.X), 0, cg.Nx - 2);
                int j0 = Math.Clamp((int)MathF.Floor(c.Y), 0, cg.Ny - 2);
                int k0 = Math.Clamp((int)MathF.Floor(c.Z), 0, cg.Nz - 2);
                float fx = Math.Clamp(c.X - i0, 0f, 1f), fy = Math.Clamp(c.Y - j0, 0f, 1f), fz = Math.Clamp(c.Z - k0, 0f, 1f);
                float wSum = 0f;
                for (int corner = 0; corner < 8; corner++)
                {
                    int a = corner & 1, b = (corner >> 1) & 1, e = (corner >> 2) & 1;
                    int ci = cg.Index(i0 + a, j0 + b, k0 + e);
                    float w = (a == 1 ? fx : 1f - fx) * (b == 1 ? fy : 1f - fy) * (e == 1 ? fz : 1f - fz);
                    if (cg.Cell[ci] == VoxelGrid.Solid || w <= 0f) { set.Sten[8 * k + corner] = -1; continue; }
                    set.Sten[8 * k + corner] = Slot(ci);
                    set.Wts[8 * k + corner] = w;
                    wSum += w;
                }
                if (wSum > 1e-6f) for (int corner = 0; corner < 8; corner++) set.Wts[8 * k + corner] /= wSum;
            }

            // Gradient neighbours of every stencil cell (air only; one-sided where a neighbour is missing).
            int stencilCells = need.Count;
            var nbs = new List<int>();
            for (int s = 0; s < stencilCells; s++)
            {
                int ci = need[s];
                int x = ci % cg.Nx, y = ci / cg.Nx % cg.Ny, z = ci / (cg.Nx * cg.Ny);
                for (int dir = 0; dir < 6; dir++)
                {
                    int ax = dir >> 1, sgn = (dir & 1) == 0 ? -1 : 1;
                    int xx = x + (ax == 0 ? sgn : 0), yy = y + (ax == 1 ? sgn : 0), zz = z + (ax == 2 ? sgn : 0);
                    if (xx < 0 || xx >= cg.Nx || yy < 0 || yy >= cg.Ny || zz < 0 || zz >= cg.Nz) { nbs.Add(-1); continue; }
                    int ni = cg.Index(xx, yy, zz);
                    nbs.Add(cg.Cell[ni] == VoxelGrid.Solid ? -1 : Slot(ni));
                }
            }
            set.Need = need.Select(ci =>
            {
                int x = ci % cg.Nx, y = ci / cg.Nx % cg.Ny;
                bool boundary = x == 0 || y == 0 || x == cg.Nx - 1 || y == cg.Ny - 1 || cg.Cell[ci] == VoxelGrid.Fixed;
                return boundary ? ~ci : ci;
            }).ToArray();
            set.NeedNb = new int[6 * set.Need.Length];
            Array.Fill(set.NeedNb, -1);
            nbs.CopyTo(0, set.NeedNb, 0, nbs.Count);   // halo cells (added last) keep -1: their gradient is unused
            set.Mom = new float[4 * set.Need.Length];
            set.Grad = new float[6 * set.Need.Length];

            if (_coarse.NestGpu && set.Need.Length > 0)
            {
                var acc = _coarse.NestAcc!;
                set.DNeed = acc.Allocate1D(set.Need);
                set.DMom = acc.Allocate1D<float>(set.Mom.Length);
            }
            if (_fine.NestGpu && n > 0)
            {
                var acc = _fine.NestAcc!;
                set.DMeta = acc.Allocate1D(set.Meta);
                set.DTgt = acc.Allocate1D<float>(set.Tgt.Length);
            }
            if (_coarse.NestGpu && _fine.NestGpu && n > 0 && set.Need.Length > 0 && _coarse.NestAcc == _fine.NestAcc)
            {
                var acc = _fine.NestAcc!;
                set.OnDevice = true;
                set.DNeedNb = acc.Allocate1D(set.NeedNb);
                set.DGrad = acc.Allocate1D<float>(set.Grad.Length);
                set.DSten = acc.Allocate1D(set.Sten);
                set.DWts = acc.Allocate1D(set.Wts);
                set.DSigma = acc.Allocate1D(set.Sigma);
            }
            return set;
        }

        // ---- per coarse step ---------------------------------------------------------------------

        /// <summary>Coarse moments -> strain rate -> per fine cell target (rho, u, Pi_neq, sigma), and the
        /// inflow/outflow class of every face cell from the sign of the coarse velocity on its normal.</summary>
        void Refresh(CellSet set, bool classify = true)
        {
            int m = set.Need.Length, n = set.N;
            if (n == 0) return;
            if (set.OnDevice)
            {
                // Moments -> strain rate -> targets + classification, queued on the stream the coarse and fine
                // steps use: ordered without a sync and never touching the host.
                var spareBuf = _coarse.Steps > 0 ? _coarse.NestDNxt! : _coarse.NestDCur!;
                _nestedMomentsKernel!(m, set.DNeed!.View, _coarse.NestDCur!.View, spareBuf.View, _coarse.NestGrid.Count, set.DMom!.View, m);
                _nestedGradKernel!(m, set.DMom.View, set.DNeedNb!.View, set.DGrad!.View, m);
                _nestedTargetKernel!(n, set.DSten!.View, set.DWts!.View, set.DMom.View, set.DGrad.View, set.DSigma!.View,
                    set.DMeta!.View, set.DTgt!.View, m, n, (1f / 3f) * (1f - _fine.NestTau0), classify ? 1 : 0);
                return;
            }
            if (m > 0)
            {
                if (_coarse.NestGpu)
                {
                    var acc = _coarse.NestAcc!;
                    // Before the coarse run's first step its boundary cells are only in the current buffer.
                    var spareBuf = _coarse.Steps > 0 ? _coarse.NestDNxt! : _coarse.NestDCur!;
                    _nestedMomentsKernel!(m, set.DNeed!.View, _coarse.NestDCur!.View, spareBuf.View, _coarse.NestGrid.Count, set.DMom!.View, m);
                    acc.Synchronize();
                    set.DMom.View.CopyToCPU(set.Mom);
                }
                else
                {
                    var cur = _coarse.NestCur; var spare = _coarse.Steps > 0 ? _coarse.NestNxt : cur; var need = set.Need; var mom = set.Mom;
                    Parallel.For(0, m, s =>
                    {
                        int e = need[s];
                        var src = e < 0 ? spare : cur;
                        int b = (e < 0 ? ~e : e) * Q;
                        float rho = 0f, mx = 0f, my = 0f, mz = 0f;
                        for (int q = 0; q < Q; q++)
                        {
                            float f = src[b + q];
                            rho += f; mx += f * Cx[q]; my += f * Cy[q]; mz += f * Cz[q];
                        }
                        mom[s] = rho; mom[m + s] = mx / rho; mom[2 * m + s] = my / rho; mom[3 * m + s] = mz / rho;
                    });
                }

                var M = set.Mom; var G = set.Grad; var nbArr = set.NeedNb;
                Parallel.For(0, m, s =>
                {
                    // d u_b / d x_a, per coarse cell, central where both neighbours exist.
                    Span<float> du = stackalloc float[9];
                    for (int a = 0; a < 3; a++)
                    {
                        int lo = nbArr[6 * s + 2 * a], hi = nbArr[6 * s + 2 * a + 1];
                        for (int bq = 0; bq < 3; bq++)
                        {
                            float u0 = M[(1 + bq) * m + s];
                            float v = lo >= 0 && hi >= 0 ? 0.5f * (M[(1 + bq) * m + hi] - M[(1 + bq) * m + lo])
                                    : hi >= 0 ? M[(1 + bq) * m + hi] - u0
                                    : lo >= 0 ? u0 - M[(1 + bq) * m + lo] : 0f;
                            du[3 * a + bq] = v;
                        }
                    }
                    G[s] = du[0]; G[m + s] = du[4]; G[2 * m + s] = du[8];
                    G[3 * m + s] = 0.5f * (du[1] + du[3]); G[4 * m + s] = 0.5f * (du[2] + du[6]); G[5 * m + s] = 0.5f * (du[5] + du[7]);
                });
            }

            // Post-collision momentum flux of the fine cell: Pi* = -2 rho cs^2 (tau_f - 1) S_f, with the fine
            // strain rate S_f = S_c / 2 (the same velocity over half the cell) -- the standard
            // (tau_f / 2 tau_c) rescaling of the coarse non-equilibrium part, written post-collision because
            // the populations stored between steps are post-collision ones.
            float piScale = (1f / 3f) * (1f - _fine.NestTau0);
            var sten = set.Sten; var wts = set.Wts; var tgt = set.Tgt; var meta = set.Meta; var sig = set.Sigma;
            var mm = set.Mom; var gg = set.Grad;
            int inflow = 0;
            Parallel.For(0, n, () => 0, (k, _, local) =>
            {
                float rho = 0f, ux = 0f, uy = 0f, uz = 0f, sxx = 0f, syy = 0f, szz = 0f, sxy = 0f, sxz = 0f, syz = 0f, wSum = 0f;
                for (int c = 0; c < 8; c++)
                {
                    int s = sten[8 * k + c];
                    if (s < 0) continue;
                    float w = wts[8 * k + c];
                    wSum += w;
                    rho += w * mm[s]; ux += w * mm[m + s]; uy += w * mm[2 * m + s]; uz += w * mm[3 * m + s];
                    sxx += w * gg[s]; syy += w * gg[m + s]; szz += w * gg[2 * m + s];
                    sxy += w * gg[3 * m + s]; sxz += w * gg[4 * m + s]; syz += w * gg[5 * m + s];
                }
                if (wSum <= 1e-6f) { rho = 1f; ux = uy = uz = sxx = syy = szz = sxy = sxz = syz = 0f; }
                float p = piScale * rho;
                tgt[k] = rho; tgt[n + k] = ux; tgt[2 * n + k] = uy; tgt[3 * n + k] = uz;
                tgt[4 * n + k] = p * sxx; tgt[5 * n + k] = p * syy; tgt[6 * n + k] = p * szz;
                tgt[7 * n + k] = p * sxy; tgt[8 * n + k] = p * sxz; tgt[9 * n + k] = p * syz;
                tgt[10 * n + k] = sig[k];
                if (classify)
                {
                    int info = meta[2 * n + k];
                    if ((info & 3) != NcSponge)
                    {
                        int nx = ((info >> 2) & 3) - 1, ny = ((info >> 4) & 3) - 1, nz = ((info >> 6) & 3) - 1;
                        bool isIn = ux * nx + uy * ny + uz * nz < 0f;
                        meta[2 * n + k] = (info & ~3) | (isIn ? NcInflow : NcOutflow);
                        if (isIn) local++;
                    }
                }
                return local;
            }, local => Interlocked.Add(ref inflow, local));
            set.Inflow = inflow;

            if (_fine.NestGpu)
            {
                set.DTgt!.View.CopyFromCPU(tgt);
                set.DMeta!.View.CopyFromCPU(meta);
            }
        }

        /// <summary>Writes the set's cells into the fine lattice's current populations (or, for the warm
        /// start, its spare buffer too).</summary>
        void Apply(CellSet set, bool next)
        {
            int n = set.N;
            if (n == 0) return;
            if (_fine.NestGpu)
            {
                var cur = next ? _fine.NestDNxt! : _fine.NestDCur!;
                // P17-02: outflow faces read their inward (sponge) neighbour, so write the faces before the sponge, never in one launch.
                if (set.Faces > 0) _nestedCellKernel!(set.Faces, set.DMeta!.View, set.DTgt!.View, cur.View, Grid.Count, n, 0);
                if (n > set.Faces) _nestedCellKernel!(n - set.Faces, set.DMeta!.View, set.DTgt!.View, cur.View, Grid.Count, n, set.Faces);
                return;   // same stream as the fine step that follows: ordered without a sync
            }
            var arr = next ? _fine.NestNxt : _fine.NestCur;
            var meta = set.Meta; var tgt = set.Tgt;
            void Body(int k)
            {
                int idx = meta[k], nb = meta[n + k], info = meta[2 * n + k];
                int cls = info & 3;
                float nx = ((info >> 2) & 3) - 1, ny = ((info >> 4) & 3) - 1, nz = ((info >> 6) & 3) - 1;
                float bx = 0f, by = 0f, bz = 0f;
                if (cls == NcOutflow) NeighborMacroscopic(arr, nb, out bx, out by, out bz);
                int b = idx * Q;
                F19 own = cls == NcSponge
                    ? new F19(arr[b], arr[b + 1], arr[b + 2], arr[b + 3], arr[b + 4], arr[b + 5], arr[b + 6], arr[b + 7], arr[b + 8],
                        arr[b + 9], arr[b + 10], arr[b + 11], arr[b + 12], arr[b + 13], arr[b + 14], arr[b + 15], arr[b + 16], arr[b + 17], arr[b + 18])
                    : default;
                F19 f = NestedCell(own, cls, tgt[k], tgt[n + k], tgt[2 * n + k], tgt[3 * n + k], tgt[4 * n + k], tgt[5 * n + k],
                    tgt[6 * n + k], tgt[7 * n + k], tgt[8 * n + k], tgt[9 * n + k], tgt[10 * n + k], bx, by, bz, nx, ny, nz);
                WriteAoS(arr, idx, f);
            }
            Parallel.For(0, set.Faces, Body);   // P17-02: faces first, they read the pre-blend sponge
            Parallel.For(set.Faces, n, Body);
        }
    }
}

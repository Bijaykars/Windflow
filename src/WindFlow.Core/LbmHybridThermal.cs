using ILGPU;
using ILGPU.Runtime;

namespace WindFlow.Core;

// ThermalScheme.Hybrid (CPU and GPU, the GPU twin is below): the D3Q19 LBM keeps the flow, temperature is a cell-centred finite-volume energy equation
//   d(rho T)/dt + div(rho u T) = div(rho kappa grad T) + rho q,   kappa = alpha_mol + nu_t / Pr_t   (no diffusivity floor)
// on the LBM grid, same dt, explicit Euler, with MASS-CONSISTENT transport: every D3Q19 link A -> D (18 per cell) is an FV face whose
// mass flux is the LBM's own, m = f*_q(A) - f*_qbar(D) (post-collision populations, the RoomFlux link-flux convention; links into a
// solid carry nothing, which is what the bounce-back plus the wall mass correction in GatherBoundary do). Then
//   rho_new(A) = rho*(A) - sum m            (exactly the density the next gather builds)
//   T_new      = (rho* T - sum m T_face + diffusion + rho* q) / rho_new
// so a uniform T stays uniform to round-off whatever the (non-solenoidal) lattice flow does, and sum(rho T) is conserved in a sealed
// room. The earlier face-averaged-velocity form lost this: its -T div u source (u from node averages, not link fluxes) grew a 5 K
// anomaly in the quiescent sealed room 19d. T_face is a second-order MUSCL (van Leer, TVD) value along the link, upwind by the sign of
// m, with the cell one further along the link; the advected quantity is T - tRef (tRef = outdoor T, the Boussinesq-relevant part).
// Diffusion is central over the 6 axis faces with the face-mean kappa and density. Walls adiabatic (6e: Correlation Surface faces too;
// their convection is a source in q, LbmSolver.SurfaceConvection; a Resolved Surface face is Dirichlet at half a cell), ring/Fixed cells hold T. The Guo buoyancy force, the device cooling,
// the Surface convection and the held-room source (6j) are RunStepThermal's (the flow pass, run with tHyb set). Lallemand and Luo 2003 (JCP 184:406); Feng, Boivin, Sagaut et al. (hybrid recursive-regularized LBM + FV).

public static partial class LbmSolver
{
    const float HybridMaxOutflow = 0.5f;     // sum of the outgoing link mass fluxes / rho (the CFL number of the scheme)
    const float HybridMaxDiffusion = 1f;     // 6 kappa dt/dx^2 must stay below this
    const int HybridROff = 0, HybridKOff = 1, HybridQOff = 2, HybridNOff = 3;   // planes of the hyb array (each Count long): rho*, kappa, q, rho_new

    static readonly int[] HCx = Cx.ToArray(), HCy = Cy.ToArray(), HCz = Cz.ToArray(), HOpp = Opp.ToArray(), HMirrorZ = MirrorZ.ToArray();

    sealed class HybridStats
    {
        public float[] MinT = [], MaxT = [], MaxOut = [], MaxKappa = [];
        public double[] Heat = [], ProbeOut = [];
        public HybridStats(int rows)
        { MinT = new float[rows]; MaxT = new float[rows]; MaxOut = new float[rows]; MaxKappa = new float[rows]; Heat = new double[rows]; ProbeOut = new double[rows]; }
    }

    /// <summary>Ring T for the FV scheme, the same semantics as <see cref="SetBoundaryThermalRingCpu"/>: outdoor T on inflow and
    /// open faces, zero-gradient (inward neighbour) elsewhere. Fixed cells keep their initial T.</summary>
    static void SetBoundaryThermalRingHybrid(float[] t, BoundaryList bl, float tOut)
    {
        int ringCount = bl.Index.Length - bl.FixedCount;
        Parallel.For(0, ringCount, k =>
        {
            byte cls = bl.Class[k];
            t[bl.Index[k]] = IsInflow(cls) || cls == BcOpen ? tOut : t[bl.NeighborSlot[k]];
        });
    }

    /// <summary>A cell the flow pass has just collided (its post-collision populations are in nxt); every other non-solid cell (ring,
    /// Fixed, the open z planes) holds its boundary state in cur.</summary>
    static bool HybridFresh(VoxelGrid grid, byte c, int x, int y, int z) =>
        c == VoxelGrid.Air && x >= 1 && x <= grid.Nx - 2 && y >= 1 && y <= grid.Ny - 2
        && !(z == 0 && grid.OpenZMin) && !(z == grid.Nz - 1 && grid.OpenZMax);

    /// <summary>rho* = sum of the post-collision populations of every non-solid cell, into the first plane of <paramref name="h"/>.</summary>
    static void HybridDensity(VoxelGrid grid, float[] cur, float[] nxt, float[] h)
    {
        int Nx = grid.Nx, Ny = grid.Ny, count = grid.Count;
        Parallel.For(0, count, i =>
        {
            byte c = grid.Cell[i];
            if (c == VoxelGrid.Solid) return;
            int x = i % Nx, y = i / Nx % Ny, z = i / (Nx * Ny);
            var src = HybridFresh(grid, c, x, y, z) ? nxt : cur;
            int b = i * Q;
            float r = 0f;
            for (int q = 0; q < Q; q++) r += src[b + q];
            h[HybridROff * count + i] = r;
        });
    }

    /// <summary>van Leer MUSCL face value of theta between the upwind cell (tUp), the downwind cell (tDn) and the cell beyond the upwind
    /// one (tUU, only when <paramref name="hasUU"/>; without it the face is first-order).</summary>
    static float HybridFaceTheta(float tUp, float tDn, float tUU, bool hasUU)
    {
        if (!hasUU) return tUp;
        float a = tUp - tUU, d = tDn - tUp;
        return a * d > 0f ? tUp + a * d / (a + d) : tUp;
    }

    /// <summary>One explicit Euler step of the energy equation over the interior air cells (see the header). Per-row extrema go to
    /// <paramref name="st"/> for the stability assertions and the diagnostics. <paramref name="probe"/> (a Sandbox energy-balance hook, null
    /// otherwise): the advective plus diffusive flux of rho (T - tRef) leaving the cells it marks, per row into st.ProbeOut.</summary>
    /// <paramref name="surfaceFixed"/>/<paramref name="surfaceTemp"/>: BuildThermal's Surface codes; a Resolved face (SurfaceResolvedBit) is a
    /// Dirichlet wall at half a cell (pre-6e), Correlation faces are adiabatic here (their exchange is in q).
    static void RunStepHybridEnergy(VoxelGrid grid, byte[] nearBoundary, float[] cur, float[] nxt, float[] tc, float[] tn, float[] h,
        int[] surfaceFixed, float[] surfaceTemp, HybridStats st, float tRef, bool[]? probe = null)
    {
        int Nx = grid.Nx, Ny = grid.Ny, Nz = grid.Nz, count = grid.Count, nxny = Nx * Ny;
        if (Ny <= 2 || Nx <= 2) return;
        var cell = grid.Cell;
        int rows = Nz * (Ny - 2);
        Parallel.For(0, rows, rowIdx =>
        {
            int y = 1 + rowIdx % (Ny - 2);
            int z = rowIdx / (Ny - 2);
            st.MinT[rowIdx] = float.MaxValue; st.MaxT[rowIdx] = float.MinValue; st.MaxOut[rowIdx] = 0f; st.MaxKappa[rowIdx] = 0f; st.Heat[rowIdx] = 0;
            st.ProbeOut[rowIdx] = 0;
            if ((z == 0 && grid.OpenZMin) || (z == Nz - 1 && grid.OpenZMax)) return;
            int rowBase = Nx * (y + Ny * z);
            float minT = float.MaxValue, maxT = float.MinValue, maxOut = 0f, maxK = 0f, bad = 0f; double heat = 0, probeOut = 0;
            for (int x = 1; x < Nx - 1; x++)
            {
                int i = x + rowBase;
                if (cell[i] != VoxelGrid.Air) continue;
                float rhoA = h[HybridROff * count + i], kA = h[HybridKOff * count + i], q = h[HybridQOff * count + i];
                float tA = tc[i], thA = tA - tRef;
                int bA = i * Q;
                bool pr = probe != null && probe[i];

                // ---- advection along the 18 links (a link through the free-slip z lid is the in-plane link the gather wraps it to) ----
                float sumM = 0f, outM = 0f, adv = 0f;
                for (int p = 1; p < Q; p++)
                {
                    int cx = HCx[p], cy = HCy[p], cz = HCz[p], pb = HOpp[p];
                    int zD = z + cz;
                    bool lid = zD < 0 || zD >= Nz;
                    if (lid) { if (cx == 0 && cy == 0) continue; cz = 0; zD = z; pb = HMirrorZ[pb]; }
                    int xD = x + cx, yD = y + cy;
                    int D = xD + Nx * (yD + Ny * zD);
                    byte cD = cell[D];
                    if (cD == VoxelGrid.Solid || !lid && LinkCut(nearBoundary, count, i, pb)) continue;   // 6a: a cut link carries no flux
                    float fOut = nxt[bA + p], arrive;
                    if (HybridFresh(grid, cD, xD, yD, zD)) arrive = nxt[D * Q + pb];
                    else if (!lid && cD == VoxelGrid.Fixed && (nearBoundary[D] & 1) != 0)
                        arrive = SupplyBounce(fOut, cur[D * Q + pb], cur[D * Q + p], nearBoundary[D]);    // RW-10: the shared gather, so Return / Fan patches act as in the flow solver
                    else arrive = cur[D * Q + pb];
                    float m = fOut - arrive;
                    sumM += m;
                    float thD = tc[D] - tRef, thF;
                    if (m > 0f)
                    {
                        outM += m;
                        int xF = x - cx, yF = y - cy, zF = z - cz;
                        bool has = xF >= 0 && xF < Nx && yF >= 0 && yF < Ny && zF >= 0 && zF < Nz && cell[xF + Nx * (yF + Ny * zF)] != VoxelGrid.Solid
                            && (lid || !LinkCut(nearBoundary, count, i, p));
                        thF = HybridFaceTheta(thA, thD, has ? tc[xF + Nx * (yF + Ny * zF)] - tRef : 0f, has);
                    }
                    else
                    {
                        int xF = xD + cx, yF = yD + cy, zF = zD + cz;
                        bool has = xF >= 0 && xF < Nx && yF >= 0 && yF < Ny && zF >= 0 && zF < Nz && cell[xF + Nx * (yF + Ny * zF)] != VoxelGrid.Solid
                            && (lid || !LinkCut(nearBoundary, count, D, pb));
                        thF = HybridFaceTheta(thD, thA, has ? tc[xF + Nx * (yF + Ny * zF)] - tRef : 0f, has);
                    }
                    adv += m * thF;
                    if (pr && !probe![D]) probeOut += m * thF;
                }

                // ---- diffusion over the 6 axis faces (central, face-mean kappa and rho; adiabatic walls; Dirichlet half-cell on Resolved Surface faces) ----
                int sf = surfaceFixed[i], faces = (sf & SurfaceResolvedBit) != 0 ? sf & 63 : 0;
                float diff = 0f;
                for (int a = 0; a < 6; a++)
                {
                    int pa = 1 + a;    // D3Q19 directions 1..6 are the six axis links
                    int zD = z + HCz[pa];
                    if (zD < 0 || zD >= Nz) continue;
                    int D = x + HCx[pa] + Nx * (y + HCy[pa] + Ny * zD);
                    byte cD = cell[D];
                    if (cD == VoxelGrid.Solid) { if ((faces >> a & 1) != 0) diff += rhoA * 2f * kA * (surfaceTemp[i] - tA); continue; }
                    if (LinkCut(nearBoundary, count, i, HOpp[pa]))   // 6a: a cut face is adiabatic, unless a Resolved Surface face (RY-07)
                    { if ((faces >> a & 1) != 0) diff += rhoA * 2f * kA * (surfaceTemp[i] - tA); continue; }
                    float kD = h[HybridKOff * count + D];
                    float kf = cD != VoxelGrid.Air ? kA : 0.5f * (kA + kD);
                    float dF = 0.5f * (rhoA + h[HybridROff * count + D]) * kf * (tc[D] - tA);
                    diff += dF;
                    if (pr && !probe![D]) probeOut -= dF;
                }

                float rhoNew = rhoA - sumM;
                float tNew = tRef + (rhoA * thA - adv + diff + rhoA * q) / rhoNew;
                h[HybridNOff * count + i] = rhoNew;
                tn[i] = tNew; bad += tNew - tNew;   // NaN / Inf poison it (min and max skip NaN)
                if (tNew < minT) minT = tNew; if (tNew > maxT) maxT = tNew;
                float o = outM / rhoA; if (o > maxOut) maxOut = o;
                if (kA > maxK) maxK = kA;
                heat += rhoA * q;
            }
            if (float.IsNaN(bad)) minT = float.NaN;
            st.MinT[rowIdx] = minT; st.MaxT[rowIdx] = maxT; st.MaxOut[rowIdx] = maxOut; st.MaxKappa[rowIdx] = maxK; st.Heat[rowIdx] = heat;
            st.ProbeOut[rowIdx] = probeOut;
        });
    }

    // ======================================================================================
    // GPU path (ILGPU): the same scheme in single precision, SoA populations. The flow pass is StepKernelThermal with flag bit 2
    // (it writes rho*, kappa and q for the fresh cells instead of colliding a D3Q7 lattice); then HybridDensityKernel adds rho* for the
    // non-fresh cells and HybridEnergyKernel is RunStepHybridEnergy, one thread per cell. Stability limits and diagnostics live in a
    // small int buffer ("st"): floats as order-preserving int keys, running extrema updated by atomics only when a cell beats the
    // current value (rare), read back at the existing sync cadence.
    // ======================================================================================

    const int HybridStFlags = 0, HybridStOut = 1, HybridStDiff = 2, HybridStMin = 3, HybridStMax = 4, HybridStHeat = 5;   // st slots (HybridStHeat is a float)

    /// <summary>Order-preserving int key of a float (a signed int compare orders like the float compare); its own inverse.</summary>
    static int HybridKey(float f) { int b = unchecked((int)Interop.FloatAsInt(f)); return b ^ ((b >> 31) & 0x7fffffff); }
    static float HybridUnkey(int k) => BitConverter.Int32BitsToSingle(k ^ ((k >> 31) & 0x7fffffff));

    static bool _gpuHybridChecked;
    static readonly object GpuHybridLock = new();
    static Action<Index1D, ArrayView<int>, ArrayView<byte>, ArrayView<int>, ArrayView<float>, float>? _hybridRingKernel;
    static Action<Index1D, ArrayView<byte>, ArrayView<float>, ArrayView<float>, GridDims>? _hybridDensityKernel;
    static Action<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<int>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
        ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>, GridDims, float>? _hybridEnergyKernel;
    static Action<Index1D, ArrayView<int>, ArrayView<float>, ArrayView<float>>? _hybridGatherTKernel;
    static Action<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<float>, ArrayView<float>, ArrayView<float>>? _hybridResetKernel;

    /// <summary>Lazily JITs the Hybrid kernels (and the shared thermal flow kernel, without the D3Q7 ones) the first time a Hybrid GPU run
    /// happens -- same reasoning as <see cref="EnsureGpuThermal"/>.</summary>
    static void EnsureGpuHybrid()
    {
        if (_gpuHybridChecked) return;
        lock (GpuHybridLock)
        {
            if (_gpuHybridChecked) return;
            var accel = _accel!;
            _stepKernelThermal ??= accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<int>, ArrayView<float>, ArrayView<float>,
                ArrayView<float>, ArrayView<float>, GridDims, ThermalParams, ArrayView<float>, byte, ArrayView<float>>(StepKernelThermal);
            _hybridRingKernel = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>, ArrayView<byte>, ArrayView<int>, ArrayView<float>, float>(HybridRingKernel);
            _hybridDensityKernel = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<float>, ArrayView<float>, GridDims>(HybridDensityKernel);
            _hybridEnergyKernel = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<int>, ArrayView<float>, ArrayView<float>,
                ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>, GridDims, float>(HybridEnergyKernel);
            _hybridGatherTKernel = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>, ArrayView<float>, ArrayView<float>>(HybridGatherTKernel);
            _hybridResetKernel = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<float>, ArrayView<float>, ArrayView<float>>(HybridResetKernel);
            _gpuHybridChecked = true;
        }
    }

    /// <summary>GPU twin of <see cref="SetBoundaryThermalRingHybrid"/> (launched with the ring entry count).</summary>
    static void HybridRingKernel(Index1D k, ArrayView<int> bIndex, ArrayView<byte> bClass, ArrayView<int> bNeighborSlot, ArrayView<float> t, float tOut)
    {
        byte cls = bClass[k];
        t[bIndex[k]] = IsInflow(cls) || cls == BcOpen ? tOut : t[bNeighborSlot[k]];
    }

    static void HybridGatherTKernel(Index1D k, ArrayView<int> idxs, ArrayView<float> t, ArrayView<float> outT) => outT[k] = t[idxs[k]];

    /// <summary>Cells that were Solid and are now Air (UpdateCells) restart at T0, in both T buffers.</summary>
    static void HybridResetKernel(Index1D i, ArrayView<byte> newCell, ArrayView<byte> oldCell, ArrayView<float> tc, ArrayView<float> tn, ArrayView<float> t0)
    {
        if (newCell[i] != VoxelGrid.Air || oldCell[i] != VoxelGrid.Solid) return;
        tc[i] = t0[i]; tn[i] = t0[i];
    }

    /// <summary>GPU twin of <see cref="HybridFresh"/>.</summary>
    static bool HybridFreshDev(byte c, int x, int y, int z, int Nx, int Ny, int Nz, int openZMin, int openZMax) =>
        c == VoxelGrid.Air && x >= 1 && x <= Nx - 2 && y >= 1 && y <= Ny - 2 && !(z == 0 && openZMin != 0) && !(z == Nz - 1 && openZMax != 0);

    /// <summary>rho* of the NON-fresh non-solid cells (ring, Fixed, open z planes) from cur; the fresh cells' rho* was written by the flow kernel.</summary>
    static void HybridDensityKernel(Index1D i, ArrayView<byte> cellType, ArrayView<float> cur, ArrayView<float> h, GridDims dims)
    {
        byte c = cellType[i];
        if (c == VoxelGrid.Solid) return;
        int Nx = dims.Nx, Ny = dims.Ny, count = dims.Count;
        int x = i % Nx; int t = i / Nx; int y = t % Ny; int z = t / Ny;
        if (HybridFreshDev(c, x, y, z, Nx, Ny, dims.Nz, dims.OpenZMin, dims.OpenZMax)) return;
        float r = 0f;
        for (int q = 0; q < 19; q++) r += cur[q * count + i];
        h[i] = r;
    }

    /// <summary>GPU twin of <see cref="RunStepHybridEnergy"/>: one thread per cell, same link and face rules, limiter and gauge.
    /// <paramref name="cellData"/> is the thermal step's (the Surface code as a float at [count, 2count), surfaceTemp at [2count, 3count));
    /// <paramref name="st"/> the stability / diagnostics buffer (see the section header).</summary>
    static void HybridEnergyKernel(Index1D i, ArrayView<byte> cellType, ArrayView<byte> nearBoundary, ArrayView<int> dir,
        ArrayView<float> cur, ArrayView<float> nxt, ArrayView<float> tc, ArrayView<float> tn, ArrayView<float> h,
        ArrayView<float> cellData, ArrayView<int> st, GridDims dims, float tRef)
    {
        int idx = i;
        if (cellType[idx] != VoxelGrid.Air) return;
        int Nx = dims.Nx, Ny = dims.Ny, Nz = dims.Nz, count = dims.Count;
        int x = idx % Nx; int tq = idx / Nx; int y = tq % Ny; int z = tq / Ny;
        if (x == 0 || x == Nx - 1 || y == 0 || y == Ny - 1 || (z == 0 && dims.OpenZMin != 0) || (z == Nz - 1 && dims.OpenZMax != 0)) return;

        float rhoA = h[HybridROff * count + idx], kA = h[HybridKOff * count + idx], q = h[HybridQOff * count + idx];
        float tA = tc[idx], thA = tA - tRef;

        float sumM = 0f, outM = 0f, adv = 0f;
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
                arrive = SupplyBounce(fOut, cur[pb * count + D], cur[p * count + D], nearBoundary[D]);    // RW-10: the shared gather, so Return / Fan patches act as in the flow solver
            else arrive = cur[pb * count + D];
            float m = fOut - arrive;
            sumM += m;
            float thD = tc[D] - tRef, thF;
            if (m > 0f)
            {
                outM += m;
                int xF = x - cx, yF = y - cy, zF = z - cz;
                bool has = xF >= 0 && xF < Nx && yF >= 0 && yF < Ny && zF >= 0 && zF < Nz && cellType[xF + Nx * (yF + Ny * zF)] != VoxelGrid.Solid
                    && (lid || !LinkCutSoA(nearBoundary, count, idx, p));
                thF = HybridFaceTheta(thA, thD, has ? tc[xF + Nx * (yF + Ny * zF)] - tRef : 0f, has);
            }
            else
            {
                int xF = xD + cx, yF = yD + cy, zF = zD + cz;
                bool has = xF >= 0 && xF < Nx && yF >= 0 && yF < Ny && zF >= 0 && zF < Nz && cellType[xF + Nx * (yF + Ny * zF)] != VoxelGrid.Solid
                    && (lid || !LinkCutSoA(nearBoundary, count, D, pb));
                thF = HybridFaceTheta(thD, thA, has ? tc[xF + Nx * (yF + Ny * zF)] - tRef : 0f, has);
            }
            adv += m * thF;
        }

        int sf = (int)cellData[count + idx], faces = (sf & SurfaceResolvedBit) != 0 ? sf & 63 : 0;
        float diff = 0f;
        for (int a = 0; a < 6; a++)
        {
            int pa = 1 + a;
            int zD = z + dir[38 + pa];
            if (zD < 0 || zD >= Nz) continue;
            int D = x + dir[pa] + Nx * (y + dir[19 + pa] + Ny * zD);
            byte cD = cellType[D];
            if (cD == VoxelGrid.Solid) { if ((faces >> a & 1) != 0) diff += rhoA * 2f * kA * (cellData[2 * count + idx] - tA); continue; }
            if (LinkCutSoA(nearBoundary, count, idx, dir[57 + pa])) { if ((faces >> a & 1) != 0) diff += rhoA * 2f * kA * (cellData[2 * count + idx] - tA); continue; }
            float kD = h[HybridKOff * count + D];
            float kf = cD != VoxelGrid.Air ? kA : 0.5f * (kA + kD);
            diff += 0.5f * (rhoA + h[HybridROff * count + D]) * kf * (tc[D] - tA);
        }

        float rhoNew = rhoA - sumM;
        float tNew = tRef + (rhoA * thA - adv + diff + rhoA * q) / rhoNew;
        h[HybridNOff * count + idx] = rhoNew;
        tn[idx] = tNew;

        // stability limits and diagnostics: running extrema (atomics only when this cell beats the stored value), heat sum, non-finite flag
        if (tNew - tNew != 0f) st[HybridStFlags] = 1;
        else
        {
            int kT = HybridKey(tNew);
            if (kT < st[HybridStMin]) Atomic.Min(ref st[HybridStMin], kT);
            if (kT > st[HybridStMax]) Atomic.Max(ref st[HybridStMax], kT);
        }
        int kO = HybridKey(outM / rhoA);
        if (kO > st[HybridStOut]) Atomic.Max(ref st[HybridStOut], kO);
        int kD6 = HybridKey(6f * kA);
        if (kD6 > st[HybridStDiff]) Atomic.Max(ref st[HybridStDiff], kD6);
        if (q != 0f) Atomic.Add(ref st.Cast<float>()[HybridStHeat], rhoA * q);
    }

    public sealed partial class LbmSession
    {
        MemoryBuffer1D<float, Stride1D.Dense>? _dTCur, _dTNxt, _dHyb;   // GPU Hybrid: T (double-buffered) and the planes of the hyb array
        MemoryBuffer1D<int, Stride1D.Dense>? _dHybStat;

        HybridStats _hstats = new(0);
        float _hybMinT = float.MaxValue, _hybMaxT = float.MinValue, _hybInitMinT = float.MaxValue, _hybMaxCfl, _hybMaxDiff;
        double _hybInjected;

        // ---- diagnostics of ThermalScheme.Hybrid (all 0 / unset for the Lbm scheme) ----
        /// <summary>Running minimum / maximum of T (deg C) over the updated interior air cells, every step since the start.</summary>
        public float HybridMinT => _hybMinT;
        public float HybridMaxT => _hybMaxT;
        /// <summary>Minimum of the initial T over all non-solid cells (the reference for "no undershoot").</summary>
        public float HybridInitialMinT => _hybInitMinT;
        /// <summary>Running maxima of the outgoing-link mass fraction and of 6 kappa dt/dx^2 (limits 0.5 and 1: exceeding either throws).</summary>
        public float HybridCfl => _hybMaxCfl;
        public float HybridDiffusionNumber => _hybMaxDiff;
        /// <summary>Sum over cells and steps of rho*q (K per cell, device cooling included): for a sealed adiabatic room,
        /// sum(rho T) over its cells must rise by this much (rho c_p V cancels on the uniform lattice).</summary>
        public double HybridInjectedKelvinCells => _hybInjected;

        /// <summary>Sandbox energy-balance hook (CPU Hybrid only): cells to watch, and the running sum over steps of the advective plus
        /// diffusive flux of rho (T - T_ref) leaving them (lattice units, K per cell; times rho c_p dx^3 / dt = W per step).</summary>
        internal bool[]? EnergyProbe;
        internal double EnergyProbeOut;

        /// <summary>Sandbox check hook: sum of rho_new * T over the cells <paramref name="inside"/> selects (interior air cells), the quantity the
        /// scheme conserves exactly; for a sealed adiabatic room it rises by <see cref="HybridInjectedKelvinCells"/>. Before the first step: sum of T0.</summary>
        public double HybridEnergy(Func<int, bool> inside)
        {
            double sum = 0;
            var (tc, h) = HybridHost();
            for (int i = 0; i < _grid.Count; i++)
                if (_grid.Cell[i] == VoxelGrid.Air && inside(i)) sum += (_steps > 0 ? h[HybridNOff * _grid.Count + i] : 1f) * tc[i];
            return sum;
        }

        /// <summary>T and the hyb planes on the host (the GPU backend downloads them).</summary>
        (float[] T, float[] H) HybridHost()
        {
            if (!_useGpu) return (_tCur, _hyb);
            _acc!.Synchronize();
            return (_dTCur!.View.GetAsArray1D(), _dHyb!.View.GetAsArray1D());
        }

        /// <summary>Sum of rho_new over the same cells (see <see cref="HybridEnergy"/>), so a caller can form the energy above a reference T.</summary>
        public double HybridMassSum(Func<int, bool> inside)
        {
            double sum = 0;
            var h = _steps > 0 ? HybridHost().H : [];
            for (int i = 0; i < _grid.Count; i++)
                if (_grid.Cell[i] == VoxelGrid.Air && inside(i)) sum += _steps > 0 ? h[HybridNOff * _grid.Count + i] : 1f;
            return sum;
        }

        void InitHybridGpu()
        {
            var accel = _acc!; var grid = _grid;
            int count = grid.Count;
            var hostT = new float[count]; var hostH = new float[4 * count];
            for (int i = 0; i < count; i++)
                if (grid.Cell[i] != VoxelGrid.Solid) { hostT[i] = _T0[i]; hostH[HybridKOff * count + i] = _alphaLat; }   // as InitHybridCpu
            _dTCur = accel.Allocate1D(hostT); _dHyb = accel.Allocate1D(hostH);
            _dTNxt = accel.Allocate1D<float>(count);
            _dHybStat = accel.Allocate1D<int>(8);
            int n = _bl.Index.Length, ringCount = n - _bl.FixedCount;
            if (ringCount > 0) _hybridRingKernel!(ringCount, _dBIndex!.View, _dBClass!.View, _dBNeighborSlot!.View, _dTCur.View, _tRef);
            _dTNxt.View.CopyFrom(_dTCur.View);
            accel.Synchronize();
            var t0 = _dTCur.View.GetAsArray1D();
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < count; i++)
                if (grid.Cell[i] != VoxelGrid.Solid) { lo = MathF.Min(lo, t0[i]); hi = MathF.Max(hi, t0[i]); }
            _hybInitMinT = _hybMinT = lo; _hybMaxT = hi;
            var st = new int[8]; st[HybridStMin] = HybridKey(lo); st[HybridStMax] = HybridKey(hi);
            _dHybStat.View.CopyFromCPU(st);
        }

        void DisposeHybridGpu()
        {
            _dTCur?.Dispose(); _dTNxt?.Dispose(); _dHyb?.Dispose(); _dHybStat?.Dispose();
            _dTCur = null; _dTNxt = null; _dHyb = null; _dHybStat = null;
        }

        void StepGpuHybrid(int steps, CancellationToken ct)
        {
            var accel = _acc!;
            int count = _grid.Count, n = _bl.Index.Length, ringCount = n - _bl.FixedCount;
            var tp = KernelThermalParams();
            for (int s = 0; s < steps; s++)
            {
                int step = (int)_steps;
                float ramp = MathF.Min(1f, step / (float)RampSteps);
                if (SupplyStep()) _dBVel!.View.CopyFromCPU(_bl.Vel);
                WindTurbulenceGpu(step);
                if (n > 0)
                    _boundaryKernel!(n, _dBIndex!.View, _dBClass!.View, _dBVel!.View, _dBNxNy!.View, _dBNeighborSlot!.View,
                        _dCur!.View, count, ramp, n);
                if (ringCount > 0)
                    _hybridRingKernel!(ringCount, _dBIndex!.View, _dBClass!.View, _dBNeighborSlot!.View, _dTCur!.View, _tRef);

                bool accumulate = step >= _averageFromStep;
                if (accumulate) _avgSamples++;

                tp.Ramp = MathF.Min(1f, step / MathF.Max(1f, _warmupSteps / 2f));
                if (_devUnit.Length > 0 && step % DeviceUnitCadence == 0) UpdateDeviceUnits();
                HoldStep(step, accumulate);
                // Flow pass (bit 2: T in, rho*/kappa/q out), rho* of the non-fresh cells, then the energy pass.
                _stepKernelThermal!(count, _dCellType!.View, _dNearBoundary!.View, _dDir!.View, _dCur!.View, _dNxt!.View,
                    _dTCur!.View, _dHyb!.View, _dims, tp, _dCellData!.View,
                    (byte)((accumulate ? 1 : 0) | (_hasDevices ? 2 : 0) | 4), _dSums!.View);
                _hybridDensityKernel!(count, _dCellType.View, _dCur.View, _dHyb.View, _dims);
                _hybridEnergyKernel!(count, _dCellType.View, _dNearBoundary.View, _dDir.View, _dCur.View, _dNxt.View,
                    _dTCur.View, _dTNxt!.View, _dHyb.View, _dCellData.View, _dHybStat!.View, _dims, _tRef);
                AgeStep(accumulate);
                if (accumulate) AccumulateRoomFluxGpu();
                (_dCur, _dNxt) = (_dNxt, _dCur);
                (_dTCur, _dTNxt) = (_dTNxt, _dTCur);
                if (accumulate && _avgSamples - _foldedSamples >= FoldSteps) FoldSums();   // P17-01

                if ((step + 1) % GpuSyncCadence == 0)
                {
                    accel.Synchronize();
                    PullHybridStats(step);
                    ct.ThrowIfCancellationRequested();
                }
                if (step % DivergenceCheckInterval == 0 && _sampleIdx.Length > 0)
                    CheckDivergenceGpu(FullDivergenceStep(step));
                if (_snapIdx < _snapSteps.Length && step == _snapSteps[_snapIdx])
                {
                    accel.Synchronize();
                    _snapshots.Add(CaptureGpu(_source, _dCur!.View.GetAsArray1D(), count, _velScale, null, (step - _warmupSteps + 1) * _dt,
                        _dTCur.View.GetAsArray1D()));
                    _snapIdx++;
                }
                _steps++;
            }
            accel.Synchronize();
            PullHybridStats((int)_steps - 1);
        }

        /// <summary>Reads the device stability / diagnostics buffer (after a sync): the same assertions as <see cref="CheckHybridStep"/> on the
        /// running extrema (so a breach is reported at the next sync, not the exact step), and the injected heat since the last call.</summary>
        void PullHybridStats(int step)
        {
            var st = _dHybStat!.GetAsArray1D();
            float maxOut = HybridUnkey(st[HybridStOut]), diff = HybridUnkey(st[HybridStDiff]);
            if (st[HybridStFlags] != 0)
                throw new InvalidOperationException($"Thermal solver diverged (hybrid scheme, GPU, by step {step}): non-finite temperature");
            if (!(maxOut < HybridMaxOutflow))
                throw new InvalidOperationException($"Hybrid thermal stability limit exceeded by step {step}: outgoing link mass fraction {maxOut:F3} >= {HybridMaxOutflow}");
            if (!(diff < HybridMaxDiffusion))
                throw new InvalidOperationException($"Hybrid thermal stability limit exceeded by step {step}: 6 kappa dt/dx^2 = {diff:F3} >= {HybridMaxDiffusion}");
            _hybMinT = HybridUnkey(st[HybridStMin]); _hybMaxT = HybridUnkey(st[HybridStMax]);
            _hybMaxCfl = maxOut; _hybMaxDiff = diff;
            _hybInjected += BitConverter.Int32BitsToSingle(st[HybridStHeat]);
            _dHybStat!.View.SubView(HybridStHeat, 1).MemSetToZero();
        }

        void InitHybridCpu(F19 feqInit)
        {
            var grid = _grid;
            int count = grid.Count;
            var cur = _cur; var T0 = _T0;
            _tCur = new float[count]; _tNxt = new float[count]; _hyb = new float[4 * count];
            _hstats = new HybridStats(grid.Nz * Math.Max(grid.Ny - 2, 0));
            var tc = _tCur; var h = _hyb; float alpha = _alphaLat;
            Parallel.For(0, count, i =>
            {
                if (grid.Cell[i] == VoxelGrid.Solid) return;
                WriteAoS(cur, i, feqInit);
                tc[i] = T0[i];
                h[HybridKOff * count + i] = alpha;   // ring / Fixed cells keep the molecular value, Air cells overwrite it every step
            });
            SetBoundaryCpu(cur, _bl, 0f);
            SetBoundaryThermalRingHybrid(tc, _bl, _tRef);
            Array.Copy(tc, _tNxt, count);
            float lo = float.MaxValue;
            for (int i = 0; i < count; i++) if (grid.Cell[i] != VoxelGrid.Solid && tc[i] < lo) lo = tc[i];
            _hybInitMinT = _hybMinT = lo;
        }

        void StepCpuHybrid(int steps, CancellationToken ct)
        {
            var grid = _grid;
            for (int s = 0; s < steps; s++)
            {
                int step = (int)_steps;
                if (step % ProgressCadence == 0) ct.ThrowIfCancellationRequested();

                float ramp = MathF.Min(1f, step / (float)RampSteps);
                float heatRamp = MathF.Min(1f, step / MathF.Max(1f, _warmupSteps / 2f));   // as StepCpuThermal
                SupplyStep(); WindTurbulenceStep(step);
                SetBoundaryCpu(_cur, _bl, ramp);
                SetBoundaryThermalRingHybrid(_tCur, _bl, _tRef);

                bool accumulate = step >= _averageFromStep;
                if (accumulate) _avgSamples++;

                if (_devUnit.Length > 0 && step % DeviceUnitCadence == 0) UpdateDeviceUnits();
                HoldStep(step, accumulate);

                // Flow pass (collision with the Guo buoyancy force from T), recording kappa and q per air cell for the energy pass.
                RunStepThermal(grid, _nearBoundary, _cur, _nxt, _curG, _nxtG, _tau0, _alphaLat, _betaB, _gLat, _tRef,
                    _heatRate, _surfaceFixed, _surfaceTemp, _surfaceCoef, _dt / (RhoCp * _dx), _velScale, _holdRoomOf, _holdQ, _dt, heatRamp, accumulate,
                    _sumRho, _sumUx, _sumUy, _sumUz, _sumNuT, _sumT, _hasDevices ? _devData : null, _devUnit, _instant, _hyb, _tCur, _spongeFaces, _spongeCells);
                HybridDensity(grid, _cur, _nxt, _hyb);
                RunStepHybridEnergy(grid, _nearBoundary, _cur, _nxt, _tCur, _tNxt, _hyb, _surfaceFixed, _surfaceTemp, _hstats, _tRef, EnergyProbe);
                CheckHybridStep(step);
                AgeStep(accumulate);
                if (accumulate) AccumulateRoomFluxCpu();
                (_cur, _nxt) = (_nxt, _cur);
                (_tCur, _tNxt) = (_tNxt, _tCur);

                if (step % DivergenceCheckInterval == 0) CheckDivergence(grid, _cur, FullDivergenceStep(step));

                if (_snapIdx < _snapSteps.Length && step == _snapSteps[_snapIdx])
                {
                    _snapshots.Add(CaptureCpu(_source, _cur, _velScale, null, (step - _warmupSteps + 1) * _dt, _tCur));
                    _snapIdx++;
                }
                _steps++;
            }
        }

        /// <summary>Reduces the per-row extrema of the energy pass: asserts the explicit-scheme limits (reports, never clamps) and
        /// keeps the running diagnostics.</summary>
        void CheckHybridStep(int step)
        {
            var st = _hstats;
            float minT = float.MaxValue, maxT = float.MinValue, maxOut = 0f, maxK = 0f; double heat = 0;
            if (EnergyProbe != null) for (int r = 0; r < st.ProbeOut.Length; r++) EnergyProbeOut += st.ProbeOut[r];
            for (int r = 0; r < st.MinT.Length; r++)
            {
                if (float.IsNaN(st.MinT[r])) minT = float.NaN; else if (st.MinT[r] < minT) minT = st.MinT[r];
                if (st.MaxT[r] > maxT) maxT = st.MaxT[r];
                if (st.MaxOut[r] > maxOut) maxOut = st.MaxOut[r];
                if (st.MaxKappa[r] > maxK) maxK = st.MaxKappa[r];
                heat += st.Heat[r];
            }
            if (float.IsNaN(minT) || float.IsNaN(maxT) || float.IsInfinity(minT) || float.IsInfinity(maxT))
                throw new InvalidOperationException($"Thermal solver diverged (hybrid scheme, step {step}): non-finite temperature");
            float diff = 6f * maxK;
            if (maxOut >= HybridMaxOutflow)
                throw new InvalidOperationException($"Hybrid thermal stability limit exceeded at step {step}: outgoing link mass fraction {maxOut:F3} >= {HybridMaxOutflow}");
            if (diff >= HybridMaxDiffusion)
                throw new InvalidOperationException($"Hybrid thermal stability limit exceeded at step {step}: 6 kappa dt/dx^2 = {diff:F3} >= {HybridMaxDiffusion}");
            if (minT < _hybMinT) _hybMinT = minT;
            if (maxT > _hybMaxT) _hybMaxT = maxT;
            if (maxOut > _hybMaxCfl) _hybMaxCfl = maxOut;
            if (diff > _hybMaxDiff) _hybMaxDiff = diff;
            _hybInjected += heat;
        }
    }
}

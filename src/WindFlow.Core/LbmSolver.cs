using System.Diagnostics;
using System.Linq;
using System.Numerics;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime.OpenCL;

namespace WindFlow.Core;

/// <summary>D3Q19 lattice Boltzmann solver: regularized BGK + Smagorinsky, halfway bounce-back on solids,
/// free-slip top/bottom, ramped far-field ring as the wind boundary condition, Fixed-velocity source cells,
/// a z-dependent wind profile, and a CPU or GPU (ILGPU: CUDA, else OpenCL) backend.
/// The per-cell math (<see cref="Collide"/>, <see cref="ComputeFeq"/>) is a pure function of 19 populations
/// held in <see cref="F19"/> (no Span/array) -- the CPU hot path and every GPU kernel call it unchanged.
/// Everything else (gather, boundary update, memory layout) is backend-specific: CPU keeps AoS
/// (cur[i*Q+q]) for its unrolled Parallel.For hot path; GPU uses SoA (cur[q*Count+i]) for coalescing.</summary>
public static partial class LbmSolver
{
    // ---- D3Q19 lattice ----------------------------------------------------------------------
    static ReadOnlySpan<int> Cx => new[] { 0, 1, -1, 0, 0, 0, 0, 1, -1, 1, -1, 1, -1, 1, -1, 0, 0, 0, 0 };
    static ReadOnlySpan<int> Cy => new[] { 0, 0, 0, 1, -1, 0, 0, 1, -1, -1, 1, 0, 0, 0, 0, 1, -1, 1, -1 };
    static ReadOnlySpan<int> Cz => new[] { 0, 0, 0, 0, 0, 1, -1, 0, 0, 0, 0, 1, -1, -1, 1, 1, -1, -1, 1 };
    static ReadOnlySpan<int> Opp => new[] { 0, 2, 1, 4, 3, 6, 5, 8, 7, 10, 9, 12, 11, 14, 13, 16, 15, 18, 17 };
    // Same (cx,cy), negated cz -- used by the free-slip top/bottom boundary.
    static ReadOnlySpan<int> MirrorZ => new[] { 0, 1, 2, 3, 4, 6, 5, 7, 8, 9, 10, 13, 14, 11, 12, 17, 18, 15, 16 };
    const int Q = 19;

    /// <summary>19 lattice populations as plain fields (no Span/array): the one representation both the
    /// CPU AoS path and every ILGPU kernel can pass around and construct without violating ILGPU's
    /// "no Span/stackalloc/pointers in kernel code" rule.</summary>
    struct F19
    {
        public float F0, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, F13, F14, F15, F16, F17, F18;
        public F19(float f0, float f1, float f2, float f3, float f4, float f5, float f6, float f7, float f8, float f9,
            float f10, float f11, float f12, float f13, float f14, float f15, float f16, float f17, float f18)
        {
            F0 = f0; F1 = f1; F2 = f2; F3 = f3; F4 = f4; F5 = f5; F6 = f6; F7 = f7; F8 = f8; F9 = f9;
            F10 = f10; F11 = f11; F12 = f12; F13 = f13; F14 = f14; F15 = f15; F16 = f16; F17 = f17; F18 = f18;
        }
    }

    /// <summary>Grid shape, passed by value into GPU kernels (kernels can't touch a VoxelGrid reference).
    /// Must be `public`, not the class-nested default (`private`): ILGPU's CUDA argument mapper builds a
    /// host-side struct via Reflection.Emit that touches this type's fields from a dynamically-generated
    /// assembly, which throws TypeLoadException ("Access is denied") against a private nested type -- that
    /// TypeLoadException, not an sm_120/Blackwell PTX-target problem, is why CUDA silently fell back to
    /// OpenCL here (a trivial no-struct kernel JITs and runs fine on this card/driver).
    /// SpongeFaces/SpongeCells (6b, P13b-08): the outflow sponge (bits 0-3, <see cref="SpongeTau"/>) and RZ-01's absorbing layer on the outflow
    /// and inflow (bits 4-7) faces (<see cref="SpongeAbsorb"/>); 0 = none (every grid but a wind session's).</summary>
    public struct GridDims { public int Nx, Ny, Nz, Count, OpenZMin, OpenZMax, SpongeFaces, SpongeCells; }
    /// <summary>Wall-model kernel arguments: NuP = physical viscosity in lattice units (0 = model off), NuW = the node's lattice
    /// viscosity incl. the floor, ForceX/Periodic = Sandbox test hooks (see <see cref="RunOptions.TestBodyForceX"/>).</summary>
    public struct WallModelArgs { public float NuP, NuW, ForceX; public int Periodic; }

    // ---- tuning knobs --------------------------------------------------------------------------
    const float ULat = 0.08f;                 // lattice velocity magnitude assigned to uMax (see Solve)
    const float Cs = 0.16f;                    // Smagorinsky constant
    internal const float NuLatMin = 5e-4f;      // default stability floor for lattice viscosity (RunOptions.ViscosityFloor)
    const float NuPhysAir = 1.5e-5f;            // m^2/s, kinematic viscosity of air at ~20C
    const float Sqrt2 = 1.4142135f;
    const float WarmupFlowThroughs = 1.0f;
    const float AverageFlowThroughs = 0.3f;
    const int RampSteps = 300;                  // steps to ramp the inlet from 0 to full speed
    const int MaxSteps = 20000;
    const int ProgressCadence = 50;
    const int DivergenceCheckInterval = 200;
    const float RhoMin = 0.5f, RhoMax = 2f;      // divergence guard band around rho0=1
    const int DivergenceSamples = 2000;
    /// <summary>P17-06: every this-many-th divergence check scans EVERY air cell (CPU) / runs a whole-field kernel (GPU), not just the ~2000 samples.</summary>
    const int DivergenceFullEvery = 10;
    static bool FullDivergenceStep(int step) => step % (DivergenceCheckInterval * DivergenceFullEvery) == 0;
    const float ProfileRefHeight = 10f, ProfileFloor = 0.05f;   // wind profile u(z) = U*max((zc-zGround)/10,0.05)^a
    // ponytail: uMax for a Watts-only heat source (BuildThermal has no explicit dTmax to anchor the
    // convective-scaling dt on) is a plume velocity estimate decoupled from the actual local dT a cell
    // reaches -- in a sealed room (no opening to vent the heat) that local dT is unbounded over the run,
    // so betaB*dT*gLat can grow arbitrarily even though gLat itself is small. Cap the Guo force in lattice
    // units instead of re-deriving dt: self-consistent cases (uMax already built from the same dTmax that
    // drives the force, e.g. the differentially-heated cavity or the indoor/outdoor stack scene) sit far
    // under this ceiling already, so the cap only ever engages for the pathological unbounded-heating case.
    // Measured 2026-10-09 on check 19a (sealed 500 W room, 3 filling-box turnovers): the cap engages in 0.00 % of room
    // cells at every stable floor (it needs |T - tRef| > 52 K there; the room peaks ~16 K above), and switching it off at
    // the stable floors changes nothing; it only matters once the thermal lattice is already unstable (floor 0.505: the
    // run diverges without the cap; floor 0: 2.2 % of cells capped at step 7000, then divergence). Kept.
    const float FLatMax = 0.01f;
    // Floor on the D3Q7 relaxation time tauG (alpha_lat = (tauG - 1/2) / 4, since c_s^2 = 1/4 for w0 = 1/4, w1 = 1/8).
    // The thermal lattice has no Smagorinsky floor of its own the way tau0 has NuLatMin: in near-quiescent air
    // (nuTLat ~ 0) tauG collapses to ~0.5, i.e. zero numerical diffusivity. Raised from 0.55 to 0.53 by a STABILITY-only
    // rule (the smallest value at which checks 19a, 19d, stack, comfort and the cavities all stay free of NaN, negative
    // g populations and checkerboard noise (|staggered amplitude| < 1 % of dT_max) and keep passing). Measured on the
    // BGK lattice: 0.52 still has no negative g but check 19d fails (spurious 0.019 m/s), 0.51 has negative g and a 3.4 K
    // spurious stratification in the quiescent room, 0.505 and below diverge. TRT (see GMagicLambda) did not move that
    // limit. A floor this close to 1/2 is only the plume-resolution limit of a 0.27 m grid, not a physical diffusivity:
    // at 0.53, alpha_floor = 0.0075 lat = 0.014 m2/s on the heat scene (dx 0.268 m, dt 39 ms), ~650 x molecular air.
    const float TauGMin = 0.53f;
    /// <summary>GPU bytes per cell as allocated (P17-05, recomputed from the allocations and left to be measured on the card): isothermal 221 + 17 (6k)
    /// (cell type 1, flags + 19 wall-link bytes 20, cur + nxt 152, sums 20, live read-back 12, device plane 16), default Hybrid thermal 273 + 8 (6j) + 17.
    /// The one figure for <see cref="CheckGpuMemory"/>, the App's AccurateBudget and HighAccuracy (the env-var D3Q7 scheme needs 313).</summary>
    public const long GpuBytesPerCell = 238, GpuBytesPerCellThermal = 298;   // 6j: +4 the held-room plane, +8 per held cell for the gather (4 per grid cell while half the grid is held; 70 % held = 5.6, unbudgeted). 6k: +17 the online age (eddy-viscosity plane 4, tau double buffer 8, batch sum 4, cell mask 1)
    /// <summary>Most cells a grid may have: the kernels index populations as 18 x count + idx, flags as 19 x count + i (&lt; 20 x count), all in int (P17-04).
    /// RX-09a: the flag array holds 1 + 19 bytes per cell, and .NET arrays stop at <see cref="Array.MaxLength"/> (a little under int.MaxValue).</summary>
    public static readonly int MaxCells = Math.Min(int.MaxValue / 20, Array.MaxLength / (1 + Q));
    const float GpuMemFractionLimit = 0.75f;
    /// <summary>The guard an Ultra session runs under (its plan keeps at most 0.85 of the card; 0.88 leaves the planner's rounding room). Every other mode keeps 0.75.</summary>
    internal const float UltraGpuMemFractionLimit = 0.88f;
    const int GpuSyncCadence = 50;
    // LbmSession.SetWind: ULat is assigned to the speed scale the session started at, so a live wind change
    // can only push the lattice velocity up by this factor before the fixed tau0/dt stop being a safe
    // scaling. Faster than that and the caller has to Start again (see LbmSession.SetWind).
    const float WindChangeFactor = 1.25f;

    // ---- body-force devices (VoxelGrid.DeviceRegion) --------------------------------------------
    // A device drives ordinary AIR cells with a Guo body force toward a target velocity,
    //     F = rho * (u_target - u) / tau_relax,
    // instead of imposing the velocity on Fixed cells. The force only redistributes the momentum of air
    // that is already there, so the device moves air without creating or destroying any (an actuator-disc
    // / momentum-source model). tau_relax is a few steps: stiff enough that the cells reach their target
    // (the relaxation cannot overshoot for tau_relax >= 0.5, so stiffness costs no stability), loose enough
    // that the surrounding pressure field still pushes back the way it does on a real fan.
    const float DeviceRelaxSteps = 1f;         // tau_relax, in steps
    const float DeviceFLatMax = 4f * ULat;     // per-component cap on the device force, lattice units
    // A conditioning unit (split AC, cassette) takes air in at its return grille and hands it back at
    // SupplyTemperature, so the heat it removes is fixed by ITS OWN airflow:
    //     P = rho*cp*Q*(T_return - T_supply),
    // spread evenly over the outlet cells as a temperature drop of k1*(T_return - T_supply) per step with
    // k1 = Q*dt/V_outlet. Two things fall out of that form, and both are why it replaced the earlier
    // "relax the outlet hard toward T_supply" version:
    //   * a parcel crossing the outlet region in its residence time V_outlet/Q is cooled by exactly
    //     (T_return - T_supply), i.e. it leaves at T_supply, with no gain to tune;
    //   * the unit can never remove more than its own airflow carries. The relaxation form could: its
    //     sink was rho*cp*gain*Q*(T_cell - T_supply), which is right only while the outlet cells are
    //     refreshed by the device's own flow. On a coarse grid they exchange with the room far faster
    //     than that (turbulent mixing, not advection), T_cell stays well above T_supply, and the reported
    //     duty came out up to `gain` times too large -- 3682 W measured for a 0.146 m3/s unit across 3 K,
    //     against the 528 W its own stream can carry.
    // The per-step drop is clamped so a cell is never taken below T_supply; a choked outlet then removes
    // less than the rating, which is correct.
    const float DeviceNoUnit = -1f;            // unit-index slot: this cell conditions nothing
    const int DeviceUnitCadence = 50;          // steps between refreshes of each unit's return temperature

    // ---- GPU device management: one cached Context + Accelerator per process ------------------
    static readonly object GpuLock = new();
    static bool _gpuChecked;
    static Context? _ctx;
    static Accelerator? _accel;
    static string _gpuKind = "";
    static string? _cudaFailReason;      // short reason CUDA was rejected, or null if it simply wasn't tried/found
    public static string? GpuFallbackNote { get; private set; }

    static Action<Index1D, ArrayView<byte>, ArrayView<float>, int>? _initKernel;
    static Action<Index1D, ArrayView<int>, ArrayView<byte>, ArrayView<float>, ArrayView<sbyte>, ArrayView<int>,
        ArrayView<float>, int, float, int>? _boundaryKernel;
    static Action<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<int>, ArrayView<float>, ArrayView<float>,
        GridDims, float, byte, ArrayView<float>>? _stepKernel;
    static Action<Index1D, ArrayView<int>, ArrayView<float>, int, ArrayView<float>>? _divergenceKernel;
    static Action<Index1D, ArrayView<byte>, ArrayView<float>, GridDims, ArrayView<int>>? _divergenceAllKernel;

    static readonly int[] DirLookupHost = BuildDirLookup();
    static int[] BuildDirLookup()
    {
        var arr = new int[95];
        for (int q = 0; q < Q; q++)
        {
            arr[q] = Cx[q]; arr[19 + q] = Cy[q]; arr[38 + q] = Cz[q]; arr[57 + q] = Opp[q]; arr[76 + q] = MirrorZ[q];
        }
        return arr;
    }

    static void EnsureGpu()
    {
        if (_gpuChecked) return;
        lock (GpuLock)
        {
            if (_gpuChecked) return;
            // Selection rule: CUDA if it JITs and runs; else OpenCL, preferring the device with the most
            // memory (discrete over integrated) and never the CPU device; else no GPU.
            if (!TryCreate(cuda: true))
            {
                if (TryCreate(cuda: false))
                    GpuFallbackNote = _cudaFailReason != null
                        ? $"CUDA unavailable ({_cudaFailReason}); using OpenCL."
                        : "No CUDA device found; using OpenCL.";
                else   // P17-07: no GPU at all; every run that asked for one says so (status line, results note, PDF, headless)
                    GpuFallbackNote = $"GPU not available ({_cudaFailReason ?? "no GPU device found"}); running on the CPU — this is much slower";
            }
            _gpuChecked = true;
        }
    }

    /// <summary>P17-07: true while an accelerator is cached (a check reads it before and after <see cref="ResetGpu"/>).</summary>
    public static bool GpuDeviceCached => _accel != null;

    /// <summary>P17-07: drops the cached accelerator, its context and every lazily JITted kernel flag, so the next <see cref="GpuAvailable"/> probes and creates the
    /// device again. Called after a device loss or any accelerator exception: a lost device stays cached otherwise and fails every later run.</summary>
    public static void ResetGpu()
    {
        lock (GpuLock)
        {
            if (_gpuSessions > 0) { _resetPending = true; return; }   // RW-02: another live session still uses the accelerator; the last one to close resets it
            ResetGpuCore();
        }
    }

    // RW-02: GPU sessions alive on the cached accelerator, and a reset asked for while some were.
    static int _gpuSessions;
    static bool _resetPending;
    static void AddGpuSession() { lock (GpuLock) _gpuSessions++; }
    static void ReleaseGpuSession()
    {
        lock (GpuLock)
        {
            if (_gpuSessions > 0) _gpuSessions--;
            if (_gpuSessions == 0 && _resetPending) ResetGpuCore();
        }
    }

    static void ResetGpuCore()
    {
        _resetPending = false;
        {
            try { _accel?.Dispose(); } catch { /* a lost device may refuse to dispose */ }
            try { _ctx?.Dispose(); } catch { /* same */ }
            _accel = null; _ctx = null; _gpuKind = ""; _cudaFailReason = null; GpuFallbackNote = null;
            _gpuChecked = _gpuAgeChecked = _gpuHybridChecked = _gpuNestedChecked = _gpuThermalChecked = false;
            _gpuDeviceChecked = _gpuLiveChecked = _gpuWmChecked = _gpuTuChecked = false;
        }
    }

    /// <summary>P17-07: test hook in the GPU stepping loop (a check throws a fake accelerator exception from it); null in the app.</summary>
    public static Action? GpuStepTestHook;
    /// <summary>RU-11: test hook at the start of a GPU session's allocation (a restart inside an app edit opens a session, it does not step one); null in the app.</summary>
    public static Action? GpuInitTestHook;

    /// <summary>P17-07: the reason text for a GPU failure (one line).</summary>
    public static string GpuFaultReason(Exception ex) => ShortReason(ex is GpuFaultException { InnerException: { } inner } ? inner : ex);

    /// <summary>RW-02: a failure that came out of a GPU call (session construction on the device, a GPU step, the field readback, a nested step), carrying the
    /// original exception. The only exception <see cref="IsGpuFault"/> accepts.</summary>
    public sealed class GpuFaultException(Exception inner) : Exception(inner.Message, inner);

    /// <summary>P17-07: the GPU ran out of memory at allocation. Not a device loss: no CPU fallback, the user lowers the quality or the domain.</summary>
    public sealed class GpuOutOfMemoryException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

    static bool IsGpuOutOfMemory(Exception ex) =>
        ex is OutOfMemoryException || ex.Message.Contains("out of memory", StringComparison.OrdinalIgnoreCase);

    /// <summary>P17-07, RW-02: an exception that means the accelerator (not the model) failed, decided by origin: only a <see cref="GpuFaultException"/>, which the
    /// GPU calls raise. Any other exception (a bad model, a bug in the analysis) surfaces as itself.</summary>
    public static bool IsGpuFault(Exception ex) => ex is GpuFaultException;

    /// <summary>RW-02: what a GPU call may rethrow as a <see cref="GpuFaultException"/>: anything except a cancel, the solver's own divergence, an out-of-memory
    /// (which has its own message) and a fault already wrapped.</summary>
    internal static bool IsGpuCallFailure(Exception ex) =>
        ex is not (OperationCanceledException or GpuOutOfMemoryException or GpuFaultException or PassTimeLimitException) &&
        !(ex is InvalidOperationException && ex.Message.Contains("diverged", StringComparison.OrdinalIgnoreCase));

    /// <summary>Tries every candidate device for the given backend, in preference order, until one
    /// creates an accelerator and JITs all four kernels. CUDA candidates are tried in device-enumeration
    /// order (ILGPU only reports real CUDA-capable devices); OpenCL candidates exclude CPU-type devices
    /// and are tried in descending memory order (discrete GPU over an integrated one).</summary>
    static bool TryCreate(bool cuda)
    {
        // ponytail: test seam for the no-GPU path (GR-F, P17-07): WINDFLOW_NO_GPU=1 hides every device; the real ways (no driver) cannot be forced on a machine that has one.
        if (System.Environment.GetEnvironmentVariable("WINDFLOW_NO_GPU") == "1") { if (cuda) _cudaFailReason = "disabled by WINDFLOW_NO_GPU=1"; return false; }
        Context? ctx;
        try
        {
            ctx = cuda ? Context.Create(b => b.Cuda()) : Context.Create(b => b.OpenCL());
        }
        catch (Exception ex)
        {
            if (cuda) _cudaFailReason = ShortReason(ex);
            return false;
        }

        // P17-07 (GR-F): only real CUDA / OpenCL devices. With CUDA_VISIBLE_DEVICES=-1 the context still lists ILGPU's own CPU emulator, which used to be accepted as
        // "CUDA · CPUAccelerator" and ran the GPU path on it, orders of magnitude slower than the CPU backend.
        var candidates = cuda
            ? ctx.Devices.Where(d => d is CudaDevice).ToList()
            : ctx.Devices.Where(d => d is CLDevice cl && cl.DeviceType != CLDeviceType.CL_DEVICE_TYPE_CPU)
                         .OrderByDescending(d => d.MemorySize).ToList();

        if (candidates.Count == 0)
        {
            if (cuda) _cudaFailReason = "no CUDA devices found";
            ctx.Dispose();
            return false;
        }

        Exception? lastEx = null;
        foreach (var dev in candidates)
        {
            Accelerator? accel = null;
            try
            {
                accel = dev is CudaDevice cudaDev
                    ? cudaDev.CreateCudaAccelerator(ctx, CudaAcceleratorFlags.ScheduleBlockingSync)
                    : dev.CreateAccelerator(ctx);

                // Loading a kernel compiles it immediately (PTX/OpenCL JIT) -- this is also the probe
                // that catches a device/driver combination the JIT can't target.
                var initK = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<float>, int>(InitKernel);
                var boundaryK = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>, ArrayView<byte>, ArrayView<float>,
                    ArrayView<sbyte>, ArrayView<int>, ArrayView<float>, int, float, int>(BoundaryKernel);
                var stepK = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<int>,
                    ArrayView<float>, ArrayView<float>, GridDims, float, byte, ArrayView<float>>(StepKernel);
                var divK = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>, ArrayView<float>, int, ArrayView<float>>(DivergenceKernel);
                var divAllK = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<float>, GridDims, ArrayView<int>>(DivergenceAllKernel);

                _ctx = ctx; _accel = accel; _gpuKind = cuda ? "CUDA" : "OpenCL";
                _initKernel = initK; _boundaryKernel = boundaryK; _stepKernel = stepK; _divergenceKernel = divK; _divergenceAllKernel = divAllK;
                return true;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                accel?.Dispose();
            }
        }

        if (cuda) _cudaFailReason = lastEx != null ? ShortReason(lastEx) : "unknown error";
        ctx.Dispose();
        return false;
    }

    static string ShortReason(Exception ex)
    {
        string msg = $"{ex.GetType().Name}: {ex.Message}";
        return msg.Length > 140 ? msg[..140] + "…" : msg;
    }

    public static bool GpuAvailable { get { EnsureGpu(); return _accel != null; } }

    /// <summary>Total memory of the solver's GPU in bytes; 0 with no GPU.</summary>
    public static long GpuMemoryBytes { get { EnsureGpu(); return _accel?.MemorySize ?? 0; } }

    public static string GpuName
    {
        get
        {
            EnsureGpu();
            if (_accel == null) return "";
            return _gpuKind == "OpenCL" && _cudaFailReason != null
                ? $"OpenCL · {_accel.Name} (CUDA unavailable: {_cudaFailReason})"
                : $"{_gpuKind} · {_accel.Name}";
        }
    }

    // ---- entry point ----------------------------------------------------------------------------

    /// <summary>Wrapper for callers with no BuildingModel (e.g. hand-built test grids): identical to the
    /// real Solve below but with no thermal/heat-source data available.</summary>
    public static FlowField Solve(VoxelGrid grid, Vector2 wind, RunOptions options, IProgress<double>? progress = null, CancellationToken ct = default)
        => Solve(grid, new BuildingModel(), wind, options, progress, ct);

    /// <summary>Runs a complete solve: open a <see cref="LbmSession"/>, step it to
    /// <see cref="LbmSession.PlannedSteps"/> (reporting progress every <see cref="ProgressCadence"/> steps,
    /// exactly as the in-loop report used to), then take the averaged field. There is only this one code
    /// path -- a live session and a batch solve execute the same steps in the same order.</summary>
    public static FlowField Solve(VoxelGrid grid, BuildingModel model, Vector2 wind, RunOptions options, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var session = Start(grid, model, wind, options, options.WarmStart);
        int total = session.PlannedSteps;
        for (int done = 0; done < total; done += ProgressCadence)
        {
            progress?.Report((double)done / total);
            session.Step(Math.Min(ProgressCadence, total - done), ct);
        }
        return session.ToField();
    }

    /// <summary>Opens a live solver session: all the setup <see cref="Solve"/> does (lattice scaling,
    /// boundary ring, thermal fields, CPU arrays or GPU buffers), left resident so the caller can advance
    /// it a chunk at a time and read the field out between chunks. Dispose it to free GPU memory.
    /// <paramref name="warmStart"/>: Preview-to-Refine warm start (PLAN_V2 2.2, isothermal only) -- seeds
    /// every Air cell's populations at equilibrium for this field's time-averaged velocity there, trilinearly
    /// interpolated onto <paramref name="grid"/>, instead of rest. Null (the default) is a cold start,
    /// unchanged from before this option existed.</summary>
    public static LbmSession Start(VoxelGrid grid, BuildingModel model, Vector2 wind, RunOptions options, FlowField? warmStart = null)
        => new(grid, model, wind, options, warmStart: warmStart);

    /// <summary>Largest effective source target, including the supply patch's drawn/carved area correction.</summary>
    public static float RequiredSourceSpeed(VoxelGrid grid, BuildingModel model)
    {
        float peak = 0;
        var areas = SupplyAreaRatios(grid,model);
        foreach (var fc in grid.FixedCells) peak = MathF.Max(peak,fc.Velocity.Length()*areas.GetValueOrDefault(fc.Supply,1f));
        foreach (var device in grid.Devices) foreach (var v in device.Target) peak=MathF.Max(peak,v.Length());
        return peak;
    }

    /// <summary>6c: the most cells a grid may have on this backend, the VRAM guard of the Full domain: the <see cref="CheckGpuMemory"/>
    /// budget over the one bytes-per-cell figure (P17-05) on the GPU, <see cref="MaxCells"/> on the CPU and in any case.</summary>
    public static long CellLimit(bool gpu, bool thermal, bool ultra = false)
    {
        if (!gpu || !GpuAvailable) return MaxCells;
        long budget = (long)(GpuMemoryBytes * (ultra ? UltraGpuMemFractionLimit : GpuMemFractionLimit));
        return Math.Min(MaxCells, budget / (thermal ? GpuBytesPerCellThermal : GpuBytesPerCell));
    }

    static void CheckGpuMemory(VoxelGrid grid, bool thermal, int roomFaces, float limit = GpuMemFractionLimit)
    {
        long needed = (long)grid.Count * (thermal ? GpuBytesPerCellThermal : GpuBytesPerCell) + 28L * roomFaces;
        long budget = (long)(_accel!.MemorySize * limit);
        if (needed > budget)
            throw new GpuOutOfMemoryException($"Grid too large for this GPU ({_accel.MemorySize / 1e9:F1} GB): use a lower quality level or a smaller domain");
    }

    // ---- Preview-to-Refine warm start (PLAN_V2 2.2) ------------------------------------------------
    // Host-side only (built once at session construction, even for the GPU backend -- see InitGpu), so no
    // kernel changes: trilinearly sample a (possibly coarser, possibly differently-originated) field's
    // time-averaged velocity at a physical point, for seeding the new grid's equilibrium populations.

    static Vector3 CellCenter(VoxelGrid g, int i)
    {
        int xy = g.Nx * g.Ny;
        int z = i / xy, rem = i % xy, y = rem / g.Nx, x = rem % g.Nx;
        return g.Origin + new Vector3((x + 0.5f) * g.Dx, (y + 0.5f) * g.Dx, (z + 0.5f) * g.Dx);
    }

    static Vector3 SampleVelocity(FlowField src, Vector3 p)
    {
        var g = src.Grid;
        float fx = Math.Clamp((p.X - g.Origin.X) / g.Dx - 0.5f, 0f, g.Nx - 1.001f);
        float fy = Math.Clamp((p.Y - g.Origin.Y) / g.Dx - 0.5f, 0f, g.Ny - 1.001f);
        float fz = Math.Clamp((p.Z - g.Origin.Z) / g.Dx - 0.5f, 0f, g.Nz - 1.001f);
        int x0 = (int)fx, y0 = (int)fy, z0 = (int)fz;
        int x1 = Math.Min(x0 + 1, g.Nx - 1), y1 = Math.Min(y0 + 1, g.Ny - 1), z1 = Math.Min(z0 + 1, g.Nz - 1);
        float tx = fx - x0, ty = fy - y0, tz = fz - z0;
        Vector3 V(int xi, int yi, int zi) { int idx = g.Index(xi, yi, zi); return new Vector3(src.Ux[idx], src.Uy[idx], src.Uz[idx]); }
        var c00 = Vector3.Lerp(V(x0, y0, z0), V(x1, y0, z0), tx);
        var c10 = Vector3.Lerp(V(x0, y1, z0), V(x1, y1, z0), tx);
        var c01 = Vector3.Lerp(V(x0, y0, z1), V(x1, y0, z1), tx);
        var c11 = Vector3.Lerp(V(x0, y1, z1), V(x1, y1, z1), tx);
        return Vector3.Lerp(Vector3.Lerp(c00, c10, ty), Vector3.Lerp(c01, c11, ty), tz);
    }

    // ---- shared math: identical for CPU and every GPU kernel -------------------------------------

    static F19 ComputeFeq(float rho, float ux, float uy, float uz)
    {
        float usq = ux * ux + uy * uy + uz * uz;
        float baseTerm = 1f - 1.5f * usq;
        const float W0 = 1f / 3, W1 = 1f / 18, W2 = 1f / 36;
        float rw0 = rho * W0, rw1 = rho * W1, rw2 = rho * W2;

        float eq0 = rw0 * baseTerm;
        float eqX = rw1 * (baseTerm + 4.5f * ux * ux), linX = rw1 * 3f * ux;
        float eqY = rw1 * (baseTerm + 4.5f * uy * uy), linY = rw1 * 3f * uy;
        float eqZ = rw1 * (baseTerm + 4.5f * uz * uz), linZ = rw1 * 3f * uz;
        float f1 = eqX + linX, f2 = eqX - linX, f3 = eqY + linY, f4 = eqY - linY, f5 = eqZ + linZ, f6 = eqZ - linZ;

        float uxy = ux + uy, dxy = ux - uy, uxz = ux + uz, dxz = ux - uz, uyz = uy + uz, dyz = uy - uz;
        float eqXYp = rw2 * (baseTerm + 4.5f * uxy * uxy), linXYp = rw2 * 3f * uxy;
        float eqXYm = rw2 * (baseTerm + 4.5f * dxy * dxy), linXYm = rw2 * 3f * dxy;
        float eqXZp = rw2 * (baseTerm + 4.5f * uxz * uxz), linXZp = rw2 * 3f * uxz;
        float eqXZm = rw2 * (baseTerm + 4.5f * dxz * dxz), linXZm = rw2 * 3f * dxz;
        float eqYZp = rw2 * (baseTerm + 4.5f * uyz * uyz), linYZp = rw2 * 3f * uyz;
        float eqYZm = rw2 * (baseTerm + 4.5f * dyz * dyz), linYZm = rw2 * 3f * dyz;
        float f7 = eqXYp + linXYp, f8 = eqXYp - linXYp, f9 = eqXYm + linXYm, f10 = eqXYm - linXYm;
        float f11 = eqXZp + linXZp, f12 = eqXZp - linXZp, f13 = eqXZm + linXZm, f14 = eqXZm - linXZm;
        float f15 = eqYZp + linYZp, f16 = eqYZp - linYZp, f17 = eqYZm + linYZm, f18 = eqYZm - linYZm;

        return new F19(eq0, f1, f2, f3, f4, f5, f6, f7, f8, f9, f10, f11, f12, f13, f14, f15, f16, f17, f18);
    }

    /// <summary>Non-equilibrium momentum flux Pi_neq = sum_q c c (f_q - feq_q), as Collide uses it.</summary>
    static void NeqStress(in F19 fin, in F19 feq, out float pixx, out float piyy, out float pizz, out float pixy, out float pixz, out float piyz)
    {
        pixx = (fin.F1 - feq.F1) + (fin.F2 - feq.F2) + (fin.F7 - feq.F7) + (fin.F8 - feq.F8) + (fin.F9 - feq.F9) + (fin.F10 - feq.F10) + (fin.F11 - feq.F11) + (fin.F12 - feq.F12) + (fin.F13 - feq.F13) + (fin.F14 - feq.F14);
        piyy = (fin.F3 - feq.F3) + (fin.F4 - feq.F4) + (fin.F7 - feq.F7) + (fin.F8 - feq.F8) + (fin.F9 - feq.F9) + (fin.F10 - feq.F10) + (fin.F15 - feq.F15) + (fin.F16 - feq.F16) + (fin.F17 - feq.F17) + (fin.F18 - feq.F18);
        pizz = (fin.F5 - feq.F5) + (fin.F6 - feq.F6) + (fin.F11 - feq.F11) + (fin.F12 - feq.F12) + (fin.F13 - feq.F13) + (fin.F14 - feq.F14) + (fin.F15 - feq.F15) + (fin.F16 - feq.F16) + (fin.F17 - feq.F17) + (fin.F18 - feq.F18);
        pixy = (fin.F7 - feq.F7) + (fin.F8 - feq.F8) - (fin.F9 - feq.F9) - (fin.F10 - feq.F10);
        pixz = (fin.F11 - feq.F11) + (fin.F12 - feq.F12) - (fin.F13 - feq.F13) - (fin.F14 - feq.F14);
        piyz = (fin.F15 - feq.F15) + (fin.F16 - feq.F16) - (fin.F17 - feq.F17) - (fin.F18 - feq.F18);
    }

    /// <summary>Smagorinsky relaxation time tau = (tau0 + sqrt(tau0^2 + 18 sqrt2 Cs^2 |Pi|_F / rho)) / 2 -- the one formula Collide and the
    /// wall model's nu_w share.</summary>
    static float SmagTau(float tau0, float pixx, float piyy, float pizz, float pixy, float pixz, float piyz, float invRho)
    {
        float qMag = MathF.Sqrt(pixx * pixx + piyy * piyy + pizz * pizz + 2f * (pixy * pixy + pixz * pixz + piyz * piyz));
        return 0.5f * (tau0 + MathF.Sqrt(tau0 * tau0 + 18f * Sqrt2 * Cs * Cs * qMag * invRho));
    }

    /// <summary>Wall-model node viscosity nu_w = (tau - 1/2)/3 (lattice units: laminar + floor + Smagorinsky) from one node's populations.</summary>
    static float SmagNuW(in F19 fin, float tau0)
    {
        float rho = fin.F0 + fin.F1 + fin.F2 + fin.F3 + fin.F4 + fin.F5 + fin.F6 + fin.F7 + fin.F8 + fin.F9 + fin.F10 + fin.F11 + fin.F12 + fin.F13 + fin.F14 + fin.F15 + fin.F16 + fin.F17 + fin.F18;
        float invRho = 1f / rho;
        float ux = (fin.F1 - fin.F2 + fin.F7 - fin.F8 + fin.F9 - fin.F10 + fin.F11 - fin.F12 + fin.F13 - fin.F14) * invRho;
        float uy = (fin.F3 - fin.F4 + fin.F7 - fin.F8 - fin.F9 + fin.F10 + fin.F15 - fin.F16 + fin.F17 - fin.F18) * invRho;
        float uz = (fin.F5 - fin.F6 + fin.F11 - fin.F12 - fin.F13 + fin.F14 + fin.F15 - fin.F16 - fin.F17 + fin.F18) * invRho;
        F19 feq = ComputeFeq(rho, ux, uy, uz);
        NeqStress(fin, feq, out float pixx, out float piyy, out float pizz, out float pixy, out float pixz, out float piyz);
        return (SmagTau(tau0, pixx, piyy, pizz, pixy, pixz, piyz, invRho) - 0.5f) / 3f;
    }

    /// <summary>Regularized BGK collision with a Smagorinsky sub-grid correction. Pure function of the 19
    /// gathered populations -- the one piece the CPU hot path and every GPU kernel call unchanged.
    /// <paramref name="tau"/> is the post-Smagorinsky relaxation time (tau0 plus the eddy part); every
    /// caller (thermal or not) also accumulates it so turbulence intensity (field.Tu) can be reported for
    /// every run, not just thermal ones -- this out-param doesn't change rho/u/fout at all.</summary>
    static void Collide(in F19 fin, float tau0, out F19 fout, out float rho, out float ux, out float uy, out float uz, out float tau)
    {
        float f0 = fin.F0, f1 = fin.F1, f2 = fin.F2, f3 = fin.F3, f4 = fin.F4, f5 = fin.F5, f6 = fin.F6,
              f7 = fin.F7, f8 = fin.F8, f9 = fin.F9, f10 = fin.F10, f11 = fin.F11, f12 = fin.F12, f13 = fin.F13,
              f14 = fin.F14, f15 = fin.F15, f16 = fin.F16, f17 = fin.F17, f18 = fin.F18;

        rho = f0 + f1 + f2 + f3 + f4 + f5 + f6 + f7 + f8 + f9 + f10 + f11 + f12 + f13 + f14 + f15 + f16 + f17 + f18;
        float invRho = 1f / rho;
        float mx = f1 - f2 + f7 - f8 + f9 - f10 + f11 - f12 + f13 - f14;
        float my = f3 - f4 + f7 - f8 - f9 + f10 + f15 - f16 + f17 - f18;
        float mz = f5 - f6 + f11 - f12 - f13 + f14 + f15 - f16 - f17 + f18;
        ux = mx * invRho; uy = my * invRho; uz = mz * invRho;

        F19 feq = ComputeFeq(rho, ux, uy, uz);

        NeqStress(fin, feq, out float pixx, out float piyy, out float pizz, out float pixy, out float pixz, out float piyz);

        tau = SmagTau(tau0, pixx, piyy, pizz, pixy, pixz, piyz, invRho);
        float relax = 1f - 1f / tau;
        float trace3 = (pixx + piyy + pizz) / 3f;

        const float W0 = 1f / 3, W1 = 1f / 18, W2 = 1f / 36;
        float neq0 = relax * 4.5f * W0 * -trace3;
        float neqX = relax * 4.5f * W1 * (pixx - trace3);
        float neqY = relax * 4.5f * W1 * (piyy - trace3);
        float neqZ = relax * 4.5f * W1 * (pizz - trace3);
        float neqXYp = relax * 4.5f * W2 * (pixx + piyy + 2f * pixy - trace3);
        float neqXYm = relax * 4.5f * W2 * (pixx + piyy - 2f * pixy - trace3);
        float neqXZp = relax * 4.5f * W2 * (pixx + pizz + 2f * pixz - trace3);
        float neqXZm = relax * 4.5f * W2 * (pixx + pizz - 2f * pixz - trace3);
        float neqYZp = relax * 4.5f * W2 * (piyy + pizz + 2f * piyz - trace3);
        float neqYZm = relax * 4.5f * W2 * (piyy + pizz - 2f * piyz - trace3);

        fout = new F19(
            feq.F0 + neq0, feq.F1 + neqX, feq.F2 + neqX, feq.F3 + neqY, feq.F4 + neqY, feq.F5 + neqZ, feq.F6 + neqZ,
            feq.F7 + neqXYp, feq.F8 + neqXYp, feq.F9 + neqXYm, feq.F10 + neqXYm, feq.F11 + neqXZp, feq.F12 + neqXZp,
            feq.F13 + neqXZm, feq.F14 + neqXZm, feq.F15 + neqYZp, feq.F16 + neqYZp, feq.F17 + neqYZm, feq.F18 + neqYZm);
    }

    /// <summary>Body-force device: F = rho * (u_target - u) / tau_relax = (rho*u_target - m) * rate, with
    /// rho and the momentum m taken from the cell's PRE-collision populations (the same state the force is
    /// meant to correct; <see cref="CollideForced"/> then applies Guo's half-force velocity shift on top).
    /// Each component is capped at <see cref="DeviceFLatMax"/> so a badly-scaled device cannot blow the
    /// lattice up in one step. Pure function of floats -- the CPU path and the GPU kernels share it.
    /// A NEGATIVE rate is an opening-throat cell of the calibrated opening model instead: a discharge
    /// (Forchheimer) loss on the through-flow only, F = -c rho |u_n| u_n n^ with c = -rate and u_n = (m.n^)/rho,
    /// see <see cref="OpeningLossCoefficient"/>. The target slots (unused, 0) carry the opening's horizontal unit
    /// normal n^ = (tx, ty, tz). The jet's transverse and vertical momentum is left alone, so the facade downwash
    /// survives; for straight-through flow (u = u_n) this is identical to the isotropic -c rho |u| u.</summary>
    static void DeviceForce(in F19 f, float tx, float ty, float tz, float rate,
        out float fx, out float fy, out float fz)
    {
        float rho = f.F0 + f.F1 + f.F2 + f.F3 + f.F4 + f.F5 + f.F6 + f.F7 + f.F8 + f.F9 + f.F10 + f.F11 + f.F12
                  + f.F13 + f.F14 + f.F15 + f.F16 + f.F17 + f.F18;
        float mx = f.F1 - f.F2 + f.F7 - f.F8 + f.F9 - f.F10 + f.F11 - f.F12 + f.F13 - f.F14;
        float my = f.F3 - f.F4 + f.F7 - f.F8 - f.F9 + f.F10 + f.F15 - f.F16 + f.F17 - f.F18;
        float mz = f.F5 - f.F6 + f.F11 - f.F12 - f.F13 + f.F14 + f.F15 - f.F16 - f.F17 + f.F18;
        if (rate < 0f)
        {
            // throat cell: (tx,ty,tz) = n^;  F = -c rho |u_n| u_n n^ = -c |m.n^| (m.n^) / rho * n^
            float mn = mx * tx + my * ty + mz * tz;
            float fn = rate * MathF.Abs(mn) * mn / rho;     // rate = -c
            fx = MathF.Min(MathF.Max(fn * tx, -DeviceFLatMax), DeviceFLatMax);
            fy = MathF.Min(MathF.Max(fn * ty, -DeviceFLatMax), DeviceFLatMax);
            fz = MathF.Min(MathF.Max(fn * tz, -DeviceFLatMax), DeviceFLatMax);
            return;
        }
        fx = MathF.Min(MathF.Max((rho * tx - mx) * rate, -DeviceFLatMax), DeviceFLatMax);
        fy = MathF.Min(MathF.Max((rho * ty - my) * rate, -DeviceFLatMax), DeviceFLatMax);
        fz = MathF.Min(MathF.Max((rho * tz - mz) * rate, -DeviceFLatMax), DeviceFLatMax);
    }

    /// <summary>Test probe: <see cref="DeviceForce"/> on the equilibrium populations of (rho, u).</summary>
    internal static (float Fx, float Fy, float Fz) DeviceForceProbe(float rho, float ux, float uy, float uz,
        float tx, float ty, float tz, float rate)
    {
        var f = ComputeFeq(rho, ux, uy, uz);
        DeviceForce(f, tx, ty, tz, rate, out float fx, out float fy, out float fz);
        return (fx, fy, fz);
    }

    /// <summary>Temperature the conditioning unit takes out of ONE outlet cell this step:
    /// k1 * (T_return - T_supply), never more than the cell has above the supply setpoint. k1 and the unit
    /// index come from the per-cell device data, the supply temperature and the current return difference
    /// from the tiny per-unit table (see <see cref="BuildDeviceData"/>). Returns 0 for a cell that
    /// conditions nothing, so the caller can apply it unconditionally. Pure floats: CPU and GPU share it.</summary>
    static float DeviceCooling(float k1, float unitIndex, float T, ArrayView<float> unit, int nUnits, int unitBase)
    {
        if (k1 <= 0f || unitIndex < 0f) return 0f;
        int u = (int)unitIndex;
        float supply = unit[unitBase + u], dT = unit[unitBase + nUnits + u];
        float drop = k1 * dT;
        float room = T - supply;
        if (room <= 0f) return 0f;
        return drop < room ? drop : room;
    }

    /// <summary>Host-array overload of the same formula (the CPU step loop keeps the unit table as a plain
    /// float[]); the two must stay identical.</summary>
    static float DeviceCooling(float k1, float unitIndex, float T, float[] unit, int nUnits)
    {
        if (k1 <= 0f || unitIndex < 0f || nUnits == 0) return 0f;
        int u = (int)unitIndex;
        float supply = unit[u], dT = unit[nUnits + u];
        float drop = k1 * dT;
        float room = T - supply;
        if (room <= 0f) return 0f;
        return drop < room ? drop : room;
    }

    // ---- Calibrated opening model (PLAN_V2 1.4b, RunOptions.OpeningModel) ------------------------------------
    // A sharp-edged opening a few cells across does not contract its jet on the lattice, so it discharges at
    // Cd_solver(n) ~0.71-0.75 instead of ~0.61 (scratch rig dbg3/dbg5 OrificeLab: neither the HRR collision nor 2x
    // nesting moves it). The calibrated model adds the missing loss as a sub-grid Forchheimer body force in the
    // opening's throat cells (the wall mid-plane layer, Analysis.ThroatCells), applied through the device Guo-force
    // path:  F = -K rho |u| u / (2 L_throat),  L_throat = Dx (one layer), so c = K/2 in lattice units, and
    //     K = 1/Cd_target^2 - 1/Cd_solver(n)^2
    // puts the loss in series with what the lattice resolves: dp = rho U^2/2 (1/Cd_solver^2 + K) = rho U^2/(2 Cd_target^2).
    // n = hydraulic diameter of the drawn opening in cells (the side of a square one).

    /// <summary>Cd_solver(n, t): the discharge coefficient this solver resolves for an isolated sharp square orifice n cells across in a wall
    /// t cells thick, measured on the product's own wall route (6d, P13a-03; re-measured RP-01). Linear in n between 4, 8 and 16 cells and in t between 0.5, 1, 2,
    /// 3 and 4 cells, held flat outside both ranges (a wall thinner than half a cell reads as half a cell).
    /// <para>Route: Sandbox rig `OrificeChecks.RunOrifice` (duct walls and plate as closed boxes, an open Opening carves the plate, built by
    /// Voxelizer.Build, so the walls are cut links), Resolved opening model, Core viscosity floor 5e-4. Each entry is the mean over where the hole sits
    /// in the grid (RP-01): eight phase triples (x-normal wall offset, y offset, z offset, in cells) from the fixed low-discrepancy set below, none on a tie
    /// (every value is 1/8 + k/4); 16 cells across: the four triples marked *. Offset p and 1 - p are mirror images (Cd agrees to 0.02 %, fixes/b6d/f1.txt), so the
    /// classes A = {0.125, 0.875}, B = {0.375, 0.625} take every (x, y, z) A/B pattern once, and each value appears twice per axis.
    /// <code>
    ///   triple   x       y       z
    ///   0*       0.125   0.875   0.125   (AAA)
    ///   1        0.875   0.125   0.375   (AAB)
    ///   2        0.125   0.625   0.875   (ABA)
    ///   3*       0.875   0.375   0.625   (ABB)
    ///   4        0.375   0.125   0.875   (BAA)
    ///   5*       0.625   0.875   0.625   (BAB)
    ///   6*       0.375   0.375   0.125   (BBA)
    ///   7        0.625   0.625   0.375   (BBB)
    /// </code>
    /// Rig only, never a validation case. Command `WindFlow.Sandbox.exe --orifice-run sweep:res:4 sweep:res:8 sweep:res:16` writes fixes/bRP/orifice-res-n4.csv,
    /// -n8.csv, -n16.csv. The spread between triples is NOT in the table (it is <see cref="CdScatter"/>): max deviation from the mean at n = 4 / 8 / 16 is
    /// 7-30 / 3-21 / 1.6-14 %.</para>
    /// <code>
    ///   t \ n   4       8       16
    ///   0.5     0.6037  0.6502  0.6624
    ///   1       0.5621  0.6271  0.6508
    ///   2       0.5059  0.6207  0.6665
    ///   3       0.4983  0.6368  0.6874
    ///   4       0.4823  0.6312  0.6873
    /// </code>
    /// The loss added on top is K = 1/Cd_target^2 - 1/Cd_solver^2 (see <see cref="OpeningLossK"/>), so where the lattice already resolves below the
    /// target it adds none.</summary>
    static readonly float[] CdSolverCells = { 4f, 8f, 16f };
    static readonly float[] CdSolverWalls = { 0.5f, 1f, 2f, 3f, 4f };
    // One block per viscosity floor (RunOptions.ViscosityFloor) the table was measured at, ascending; a floor between two blocks
    // interpolates in log(floor), one outside them takes the nearest block. Block[t][n].
    static readonly float[] CdSolverFloors = { 5e-4f };
    // 2026-10-19 (RP-01): `WindFlow.Sandbox.exe --orifice-run sweep:res:4 sweep:res:8 sweep:res:16` (fixes/bRP/orifice-res-n4.csv, -n8.csv, -n16.csv; summary rows).
    static readonly float[][][] CdSolverTable =
    {
        new[]
        {
            new[] { 0.6037f, 0.6502f, 0.6624f },
            new[] { 0.5621f, 0.6271f, 0.6508f },
            new[] { 0.5059f, 0.6207f, 0.6665f },
            new[] { 0.4983f, 0.6368f, 0.6874f },
            new[] { 0.4823f, 0.6312f, 0.6873f },
        },
    };
    internal const float DefaultOpeningCd = 0.61f;   // sharp-edged opening (Idelchik; ISO 5167 orifice at beta -> 0)

    internal static float CdSolver(float n, float t, float floor = NuLatMin)
    {
        int last = CdSolverFloors.Length - 1;
        if (floor <= CdSolverFloors[0]) return CdSolverRow(n, t, 0);
        if (floor >= CdSolverFloors[last]) return CdSolverRow(n, t, last);
        int r = 1;
        while (floor > CdSolverFloors[r]) r++;
        float s = MathF.Log(floor / CdSolverFloors[r - 1]) / MathF.Log(CdSolverFloors[r] / CdSolverFloors[r - 1]);
        return CdSolverRow(n, t, r - 1) + s * (CdSolverRow(n, t, r) - CdSolverRow(n, t, r - 1));
    }

    /// <summary>Linear interpolation of <paramref name="v"/> at <paramref name="x"/> on the ascending grid <paramref name="xs"/>, flat outside it.</summary>
    static float GridLerp(float[] xs, float x, Func<int, float> v)
    {
        if (x <= xs[0]) return v(0);
        for (int k = 1; k < xs.Length; k++)
            if (x <= xs[k])
            {
                float s = (x - xs[k - 1]) / (xs[k] - xs[k - 1]);
                return v(k - 1) + s * (v(k) - v(k - 1));
            }
        return v(xs.Length - 1);
    }

    static float CdSolverRow(float n, float t, int row) =>
        GridLerp(CdSolverWalls, t, i => GridLerp(CdSolverCells, n, k => CdSolverTable[row][i][k]));

    /// <summary>Placement scatter X(n, t) of <see cref="CdSolver"/>: the largest |Cd(triple) - phase mean| / phase mean over the phase triples the table was
    /// averaged over (Resolved model, same rig, same triples, fixes/bRP/orifice-res-n*.csv, summary rows; RP-01). Same bilinear interpolation and clamping.
    /// A hole's Cd depends on where it sits relative to the grid by up to +-X; the table holds the mean.</summary>
    static readonly float[][] CdScatterTable =
    {
        new[] { 0.301f, 0.211f, 0.143f },   // t = 0.5; n = 4, 8, 16
        new[] { 0.102f, 0.077f, 0.051f },   // t = 1
        new[] { 0.071f, 0.032f, 0.016f },   // t = 2
        new[] { 0.065f, 0.026f, 0.021f },   // t = 3
        new[] { 0.070f, 0.030f, 0.023f },   // t = 4
    };

    internal static float CdScatter(float n, float t) =>
        GridLerp(CdSolverWalls, t, i => GridLerp(CdSolverCells, n, k => CdScatterTable[i][k]));

    /// <summary>Wall thickness the opening sits in, in cells: the thickness the voxeliser measured from the geometry (RP-06, <see cref="Opening.MeasuredDepth"/>), else its
    /// Depth over Dx (1 when the opening gives none).</summary>
    internal static float OpeningWallCells(Opening o, float dx) => o.MeasuredDepth > 0f ? o.MeasuredDepth / dx : o.Depth > 0f ? o.Depth / dx : 1f;

    /// <summary>Cells across an opening: its hydraulic diameter 2WH/(W+H) over Dx.</summary>
    internal static float OpeningCells(Opening o, float dx) =>
        o.Width > 0f && o.Height > 0f ? 2f * o.Width * o.Height / (o.Width + o.Height) / dx : 0f;

    /// <summary>Loss coefficient K on the throat velocity head, &gt;= 0 (an opening already resolving below its target
    /// gets none: the model only adds loss).</summary>
    internal static float OpeningLossK(Opening o, float dx, float floor = NuLatMin)
    {
        float n = OpeningCells(o, dx);
        if (n <= 0f) return 0f;
        float target = o.DischargeCoefficient is float cd && cd > 0f ? cd : DefaultOpeningCd;
        float solver = CdSolver(n, OpeningWallCells(o, dx), floor);
        return MathF.Max(0f, 1f / (target * target) - 1f / (solver * solver));
    }

    /// <summary>6d (P13a-03): the Cd the lattice resolves for this opening (<see cref="CdSolver"/>) and the Cd it is asked for. Where the first is
    /// the lower, <see cref="OpeningLossK"/> is 0 (a negative loss is non-physical) and the opening flows about 1 - solver/target low.</summary>
    internal static (float Solver, float Target) OpeningResolvedCd(Opening o, float dx, float floor = NuLatMin)
    {
        float n = OpeningCells(o, dx);
        float target = o.DischargeCoefficient is float cd && cd > 0f ? cd : DefaultOpeningCd;
        return (n <= 0f ? target : CdSolver(n, OpeningWallCells(o, dx), floor), target);
    }

    /// <summary>Lattice coefficient c of F = -c rho |u| u for a throat one cell thick: K / (2 L_throat), L = 1 cell.</summary>
    static float OpeningLossCoefficient(float k) => 0.5f * k;

    /// <summary>Throat cells, their loss coefficient and the opening's horizontal unit normal (Nx, Ny; the loss acts
    /// along it only, see <see cref="DeviceForce"/>) for every opening, or null under Resolved. IsOpen is ignored
    /// here (a live session's model can lag the grid it is handed): a closed opening's throat is solid in the grid
    /// and drops out on the Air test.</summary>
    static List<(int Cell, float C, float Nx, float Ny)>? OpeningLossCells(VoxelGrid grid, BuildingModel model, OpeningModel mode, float floor)
    {
        if (mode != OpeningModel.Calibrated) return null;
        var list = new List<(int, float, float, float)>();
        foreach (var o in model.Openings)
        {
            float c = OpeningLossCoefficient(OpeningLossK(o, grid.Dx, floor));
            if (c <= 0f) continue;
            var n0 = new Vector3(o.Normal.X, o.Normal.Y, 0f);
            if (n0.LengthSquared() < 1e-8f) continue;       // no horizontal normal: ThroatCells is empty too
            var nh = Vector3.Normalize(n0);
            var open = new Opening { Center = o.Center, Normal = o.Normal, Width = o.Width, Height = o.Height, Depth = o.Depth, IsOpen = true };
            foreach (int idx in Analysis.ThroatCells(grid, open))
                if (grid.Cell[idx] == VoxelGrid.Air) list.Add((idx, c, nh.X, nh.Y));
        }
        return list;
    }

    static float Profile(float zc, float zGround, float a) =>
        a == 0f ? 1f : MathF.Pow(MathF.Max((zc - zGround) / ProfileRefHeight, ProfileFloor), a);

    // ---- boundary list: far-field ring (inflow/outflow/tangential) + Fixed source cells, unified -----
    // Built once per solve; consumed every step by SetBoundaryCpu (CPU) or BoundaryKernel (GPU), which
    // are the same branching logic over the same flat layout, just AoS vs SoA on the write side.

    const byte BcInflow = 0, BcOutflow = 1, BcTangential = 2, BcFixed = 3, BcOpen = 4, BcSupply = 5, BcLid = 6;
    // The pre-6b equilibrium inflow (feq(1, u)) and outflow (feq(1, u_nb), inflowing normal part dropped), kept for a grid with an open
    // top, i.e. a user simulation box (RunOptions.Domain v1): its faces may cut through the building, and a velocity inlet forcing the
    // wind through a cut wall into a room has no steady state (Sandbox FC4 and the 48b v1 box diverged with it). No sponge there either.
    const byte BcInflowEq = 7, BcOutflowEq = 8;
    static bool IsInflow(byte cls) => cls == BcInflow || cls == BcInflowEq;
    // The default wind-ring inflow is the 6b NEE inflow. WINDFLOW_INFLOW=EQ opts into feq(1, u), an experiment: it was tested as the default in
    // accuracy step 1 (OF-3) and reverted on 2026-10-28 because the cross-vent H40 validation case fails with it (EQ q 0.619 < 0.66 vs NEE q 0.746 PASS;
    // re-audit RA-04) and C1.2 failed its bound. Inflow only: outflow, tangential, lid and sponge are unchanged.
    // internal and not readonly so a self-check can flip it (BuildBoundaryList reads it at build time).
    /// <summary>Self-check hook: the ring's (BcInflow, BcInflowEq) cell counts for a unit wind on this grid, as the session would classify it.</summary>
    internal static (int Nee, int Eq) CountRingInflowClasses(VoxelGrid grid, Vector2 wind)
    {
        var bl = BuildBoundaryList(grid, wind, wind.Length(), wind.X, wind.Y, 0.2f, 1f, 0f);
        int nee = 0, eq = 0;
        for (int k = 0; k < bl.Class.Length - bl.FixedCount; k++) { if (bl.Class[k] == BcInflow) nee++; else if (bl.Class[k] == BcInflowEq) eq++; }
        return (nee, eq);
    }
    internal static bool NeeInflow = !string.Equals(System.Environment.GetEnvironmentVariable("WINDFLOW_INFLOW"), "EQ", StringComparison.OrdinalIgnoreCase);

    // ---- wind ring (6b: P13b-07, P13b-08) ---------------------------------------------------------------
    // The default inflow is the 6b NEE inflow described next; feq(1, u) (BcInflowEq, see NeeInflow) is an opt-in experiment (WINDFLOW_INFLOW=EQ),
    // reverted as the default on 2026-10-28 (RA-04).
    // Inflow faces: velocity BC by non-equilibrium extrapolation (Guo, Zheng & Shi 2002, Chin. Phys. 11:366),
    //     f = feq(rho_nb, u) + (f_nb - feq(rho_nb, u_nb)),
    // u = mean profile + the synthetic gust (WindTurbulence), rho from the inner neighbour, so a face-wide gust can build the
    // pressure that pushes the column instead of meeting rho = 1 pinned at both ends. Outflow faces: pressure BC rho = 1 by the
    // same extrapolation, u from the inner neighbour (inflowing normal part dropped, as before), plus a viscosity sponge
    // (SpongeTau). Tangential faces stay the equilibrium feq(1, mean profile u) they were: with every face density-free (free-slip
    // sides, as first briefed) nothing absorbs sound, and the start-up and gust energy formed a standing wave measured at 6-350x the
    // old rho' (fixes/b6/gate-6bc, FIXLOG 6b+6c); COST 732 allows the inflow profile on the lateral faces, which the Full domain puts
    // >= 5 H from the building. A wind more than 5.7 degrees off a grid axis has no tangential face, so the absorbing layer (SpongeAbsorb,
    // RZ-01) takes that role on every inflow and outflow face. The top of
    // the automatic domain (BcLid): the profile velocity at its height, normal velocity 0, rho by extrapolation (COST 732: a
    // free-slip lid lets the profile decay). f_nb is the neighbour's post-collision state, which is what cur holds when the ring
    // is written, so the ring carries the non-equilibrium part the bulk relaxed one step later.
    // Disclosure (6b gate, fixes/b6/gate-6bc): empty sample-house domain, Dx 0.5 m, EN L ~ 36 m. Two cells in, Iu reads 0.85 of the EN
    // target over a 30 L/U record but 1.04-1.07 over 120 L/U (a short record under-reads gusts the size of the domain); the building
    // location keeps 0.94-0.97 of the inlet value (old ring 0.91-0.93).
    /// <summary>Depth of the outflow sponge in cells (6b, P13b-08); capped at 10 % of the shorter plan side of the grid
    /// (<see cref="SpongeDepth"/>). The Full domain mode adds it to the downstream margin.</summary>
    public const int WindSpongeCells = 8;
    const float SpongeNuRatio = 10f;    // lattice viscosity at the face over the local one; a smooth (quadratic) ramp to 1 at the sponge's inner edge
    // RZ-01: density relaxation per step at the face (SpongeAbsorb), r^2 ramp like the viscosity, on the outflow AND the inflow faces (an
    // outflow layer alone took an oblique wind's standing wave only from rho' 1.3e-2 to 1.1e-2; with the inflow layer it is 5e-6, the old
    // ring's level). A numerical boundary constant, chosen on the empty-domain gate only (harness2 gate, winds from 270/250/225, sigma 0.02,
    // 0.05, 0.1: fixes/b6/rz); never fitted to a validation case.
    const float SpongeSigmaMax = 0.1f;

    // ---- Supply patches: link-wise velocity inlet + flux controller ------------------------------------
    // A Supply patch cell (FixedCell.Supply >= 0) is a boundary, not fluid. Every link from it into a fluid
    // cell x_f with c_i . u_w > 0 (pointing into the room) takes Ladd's moving-wall bounce-back (Ladd 1994,
    // J. Fluid Mech. 271), written for the pull gather with c_q = -c_i pointing from the patch into x_f:
    //     f_q(x_f, t+1) = f*_opp(x_f, t) - 2 w_i rho_w (c_i . u_w) / c_s^2 = f*_opp(x_f, t) + 6 w_q rho_w (c_q . u_w)
    // with rho_w = rho0 = 1. The patch cell itself holds feq(1, u_w), and feq_q - feq_opp = 6 w_q (c_q . u_w)
    // exactly (the quadratic terms cancel), so the gather reads the increment straight off the patch cell
    // (SupplyBounce) and needs no extra kernel argument. Links with c_q . u_w <= 0 (the side and back faces of
    // the patch) are plain bounce-back, a stationary no-slip face. rho0 rather than the local rho: the mass
    // each link injects is then exactly the prescribed flux, whatever the back-pressure, and the scheme stays
    // linear -- the local-rho form differs by O(Ma^2) ~ 0.2 % and would make the flux depend on the room
    // pressure.
    // The net population crossing a link per step is exactly that increment, so the delivered flux is known
    // host-side from the wall velocity (SupplyLoop) -- the same number the populations hold, on both backends.
    // Missing links (a patch edge against a wall, a thick or staircased patch, fewer cells than Width x Height)
    // make it fall short of the set flow, and a PI loop scales u_w until the windowed mean matches it.
    const int SupplyCadence = 20;               // steps per controller update
    const float SupplyKp = 0.25f, SupplyKi = 0.5f;   // on e = 1 - delivered/set, per update
    const float SupplyScaleMin = 0.8f, SupplyScaleMax = 1.6f;
    const float SupplyGainMax = 1.05f;          // per-update slew clamp on the scale (and 1/this downward)
    /// <summary>Ring face class on s = uW.n/|uW|: inflow below -this, outflow above +this, tangential between. Voxelizer's Full domain
    /// classifies its margins with the same number.</summary>
    internal const float RingClassThreshold = 0.1f;

    // Fixed (Return / Fan patch) cells are an equilibrium reset at rho0 = 1, like the wind-inflow ring and the Supply
    // wall. A boundary cell is never streamed into, so the "own rho" it used to read was only the echo of what this
    // update wrote two steps earlier -- and the other population buffer was never initialised, so on the CPU it read 0
    // (clamped to 0.9, flipping 0.9 <-> 1.0 every step) and on the GPU whatever the allocation held. A neighbour rho
    // (zero-gradient pressure) was rejected: in a sealed room with only a supply + return or a fan patch it removes the
    // only pressure anchor, and any carve mismatch between patch fluxes then drifts the room rho into the clamp.
    const float FixedCellRho = 1f;

    sealed class BoundaryList
    {
        public int[] Index = [];
        public byte[] Class = [];
        public float[] Vel = [];        // length 3n: [0,n)=velX [n,2n)=velY [2n,3n)=velZ (unramped, lattice units)
        public sbyte[] NxNy = [];       // length 2n: [0,n)=outward nx [n,2n)=ny (ring only, 0 for Fixed)
        public int[] NeighborSlot = []; // length 2n: [0,n)=inward-neighbour cell index (ring); [n,2n)=FixedCells slot (Fixed, else -1)
        public int FixedCount;
    }

    /// <summary>Builds the perimeter (x/y boundary, all z, ground layer skipped since it's Solid) plus
    /// Fixed source cells as one flat list. Ring class/target velocity follow the caller's spec: classify
    /// by s = wind.n/|wind| (corner columns pick the more inflow-like face); inflow and tangential faces
    /// get the z-dependent wind profile baked in (unramped -- the ramp is a per-step scalar multiply);
    /// outflow computes its target from the interior neighbour every step, so no velocity is baked here.
    /// With no ambient wind (mechanical-only ventilation, Fixed cells driving the flow instead), every
    /// ring cell is forced to <see cref="BcOpen"/> instead: a fully open boundary (feq(1, u_in), no normal
    /// clamp, no wind profile) so air can be drawn in or pushed out freely on any face, since there is no
    /// wind direction left to classify inflow/outflow/tangential by.
    /// <paramref name="lid"/> (6b): the top air layer (inside the ring) becomes <see cref="BcLid"/> entries carrying the profile velocity
    /// at its height; ring cells on that layer then read their neighbour one layer down, so no ring entry reads another entry's cell.</summary>
    static BoundaryList BuildBoundaryList(VoxelGrid grid, Vector2 wind, float windSpeed, float uWxFull, float uWyFull, float a, float latPerPhys, float zGround,
        bool lid = false, bool equilibrium = false)
    {
        int Nx = grid.Nx, Ny = grid.Ny, Nz = grid.Nz;
        float invWindSpeed = windSpeed > 1e-6f ? 1f / windSpeed : 0f;
        bool noWind = windSpeed <= 1e-6f;

        var idxs = new List<int>(); var clss = new List<byte>();
        var velX = new List<float>(); var velY = new List<float>(); var velZ = new List<float>();
        var nxL = new List<sbyte>(); var nyL = new List<sbyte>(); var neigh = new List<int>(); var slot = new List<int>();

        for (int y = 0; y < Ny; y++)
            for (int x = 0; x < Nx; x++)
            {
                bool onX = x == 0 || x == Nx - 1, onY = y == 0 || y == Ny - 1;
                if (!onX && !onY) continue;

                int nx = 0, ny = 0, neighNx, neighNy; float s;
                if (onX && onY)
                {
                    int nX = x == 0 ? -1 : 1, nY = y == 0 ? -1 : 1;
                    float sX = wind.X * nX * invWindSpeed, sY = wind.Y * nY * invWindSpeed;
                    if (sX <= sY) { nx = nX; s = sX; } else { ny = nY; s = sY; }
                    // The inward-neighbour lookup always steps the full diagonal at a corner (both nX and
                    // nY), landing on a genuine interior cell -- not just the single axis picked above for
                    // classification. Stepping only that one axis instead lands on the OTHER face's own
                    // ring cell, whose outflow value is itself derived from this same cell one step later:
                    // a 2-cell feedback loop with no viscous damping that blows tiny float noise up into
                    // NaN within ~5 steps once BOTH corner faces classify as outflow (only possible for
                    // near-45-degree wind -- cross/reversed/etc. never have an outflow-outflow corner, so
                    // this can't move their numbers, confirmed empirically: bit-identical cross Qeast
                    // before/after). ix/iy is only ever read for BcOutflow below, so widening it for the
                    // other classes at a corner is inert.
                    neighNx = nX; neighNy = nY;
                }
                else if (onX) { nx = x == 0 ? -1 : 1; s = wind.X * nx * invWindSpeed; neighNx = nx; neighNy = 0; }
                else { ny = y == 0 ? -1 : 1; s = wind.Y * ny * invWindSpeed; neighNx = 0; neighNy = ny; }

                byte cls = noWind ? BcOpen : s < -RingClassThreshold ? BcInflow : s > RingClassThreshold ? BcOutflow : BcTangential;
                if (equilibrium || !NeeInflow && cls == BcInflow) cls = cls == BcInflow ? BcInflowEq : cls == BcOutflow ? BcOutflowEq : cls;
                int ix = x - neighNx, iy = y - neighNy;

                float tangBaseX = 0f, tangBaseY = 0f;
                if (cls == BcTangential)
                {
                    float uWn = uWxFull * nx + uWyFull * ny;
                    tangBaseX = uWxFull - uWn * nx; tangBaseY = uWyFull - uWn * ny;
                }

                for (int z = 0; z < Nz; z++)
                {
                    int idx = grid.Index(x, y, z);
                    if (grid.Cell[idx] != VoxelGrid.Air) continue; // Fixed cells are appended once below

                    idxs.Add(idx); clss.Add(cls); nxL.Add((sbyte)nx); nyL.Add((sbyte)ny);
                    int iz = z == 0 && grid.OpenZMin ? 1 : z == Nz - 1 && (grid.OpenZMax || lid) ? Nz - 2 : z;
                    neigh.Add(grid.Index(ix, iy, iz)); slot.Add(-1);

                    if (cls == BcOutflow || cls == BcOutflowEq || cls == BcOpen) { velX.Add(0f); velY.Add(0f); velZ.Add(0f); continue; }
                    float p = Profile(grid.Origin.Z + (z + 0.5f) * grid.Dx, zGround, a);
                    if (IsInflow(cls)) { velX.Add(uWxFull * p); velY.Add(uWyFull * p); velZ.Add(0f); }
                    else { velX.Add(tangBaseX * p); velY.Add(tangBaseY * p); velZ.Add(0f); }
                }
            }

        if (lid && Nz > 2)
        {
            int z = Nz - 1;
            float p = Profile(grid.Origin.Z + (z + 0.5f) * grid.Dx, zGround, a);
            for (int y = 1; y < Ny - 1; y++) for (int x = 1; x < Nx - 1; x++)
            {
                int i = grid.Index(x, y, z);
                if (grid.Cell[i] != VoxelGrid.Air) continue;
                idxs.Add(i); clss.Add(BcLid); nxL.Add(0); nyL.Add(0);
                neigh.Add(grid.Index(x, y, z - 1)); slot.Add(-1);
                velX.Add(uWxFull * p); velY.Add(uWyFull * p); velZ.Add(0f);
            }
        }

        // Z faces of a standalone crop exchange freely; corners already belong to the X/Y ring.
        for (int side = 0; side < 2; side++)
        {
            if (!(side == 0 ? grid.OpenZMin : grid.OpenZMax)) continue;
            int z = side == 0 ? 0 : Nz - 1, iz = side == 0 ? 1 : Nz - 2;
            for (int y = 1; y < Ny - 1; y++) for (int x = 1; x < Nx - 1; x++)
            {
                int i = grid.Index(x, y, z);
                if (grid.Cell[i] != VoxelGrid.Air) continue;
                idxs.Add(i); clss.Add(BcOpen); nxL.Add(0); nyL.Add(0);
                neigh.Add(grid.Index(x,y,iz)); slot.Add(-1);
                velX.Add(0); velY.Add(0); velZ.Add(0);
            }
        }

        for (int k = 0; k < grid.FixedCells.Count; k++)
        {
            var fc = grid.FixedCells[k];
            idxs.Add(fc.Index); clss.Add(fc.Supply >= 0 ? BcSupply : BcFixed);
            velX.Add(fc.Velocity.X * latPerPhys); velY.Add(fc.Velocity.Y * latPerPhys); velZ.Add(fc.Velocity.Z * latPerPhys);
            nxL.Add(0); nyL.Add(0); neigh.Add(-1); slot.Add(k);
        }

        int n = idxs.Count;
        var vel = new float[3 * n]; var nxny = new sbyte[2 * n]; var neighSlot = new int[2 * n];
        for (int k = 0; k < n; k++)
        {
            vel[k] = velX[k]; vel[n + k] = velY[k]; vel[2 * n + k] = velZ[k];
            nxny[k] = nxL[k]; nxny[n + k] = nyL[k];
            neighSlot[k] = neigh[k]; neighSlot[n + k] = slot[k];
        }

        return new BoundaryList { Index = idxs.ToArray(), Class = clss.ToArray(), Vel = vel, NxNy = nxny, NeighborSlot = neighSlot, FixedCount = grid.FixedCells.Count };
    }

    /// <summary>The Fixed-cell tail of a boundary list with the far-field ring dropped: a nested fine box
    /// (see <see cref="NestedSession"/>) drives its own faces from the coarse run, so the wind ring must
    /// not overwrite them. 6b: the <see cref="BcLid"/> entries stay -- a box that reaches the domain top shares its lid
    /// (the coarse run's top layer is no longer free-slip, so the fine one must not be either).</summary>
    static BoundaryList FixedOnly(BoundaryList bl)
    {
        int n = bl.Index.Length, f = bl.FixedCount, r = n - f;
        var keep = Enumerable.Range(0, n).Where(k => k >= r || bl.Class[k] == BcLid).ToArray();
        int m = keep.Length;
        var vel = new float[3 * m]; var nxny = new sbyte[2 * m]; var slot = new int[2 * m];
        for (int j = 0; j < m; j++)
        {
            int k = keep[j];
            vel[j] = bl.Vel[k]; vel[m + j] = bl.Vel[n + k]; vel[2 * m + j] = bl.Vel[2 * n + k];
            nxny[j] = bl.NxNy[k]; nxny[m + j] = bl.NxNy[n + k];
            slot[j] = bl.NeighborSlot[k]; slot[m + j] = bl.NeighborSlot[n + k];
        }
        return new BoundaryList { Index = keep.Select(k => bl.Index[k]).ToArray(), Class = keep.Select(k => bl.Class[k]).ToArray(),
            Vel = vel, NxNy = nxny, NeighborSlot = slot, FixedCount = f };
    }

    /// <summary>One byte per cell, carrying both things the gather needs to know about walls:
    /// <list type="bullet">
    /// <item>bit 0: this AIR cell needs the checked gather (it has a Solid neighbour, or sits on the
    /// top/bottom z layer). Fixed cells are air for this purpose -- they always hold valid streamable
    /// populations (written every step by the boundary update), so a neighbour next to only Fixed and Air
    /// cells can still use the fast unchecked gather. A link-wise Supply patch cell is NOT air for this purpose:
    /// its air neighbours take the checked gather (SupplyBounce), and on the patch cell itself bit 0 is the
    /// marker the gather tests (only ever read off a Fixed cell).</item>
    /// <item>bits 1-7: the cell's distance to the nearest surface (<see cref="VoxelGrid.Distance"/>) in
    /// units of Dx/<see cref="DistQuant"/>, saturating at <see cref="DistUnknown"/> = "no wall near
    /// enough to place". Set on solid cells too -- interpolated bounce-back needs the pair. Packing it
    /// here rather than in its own array is what keeps every step kernel at the argument count ILGPU is
    /// known to resolve, and costs nothing: 1/32 of a cell is far finer than the wall position is
    /// actually known.</item></list></summary>
    static byte[] ComputeCellFlags(VoxelGrid grid)
    {
        int Nx = grid.Nx, Ny = grid.Ny, Nz = grid.Nz, count = grid.Count;
        var cell = grid.Cell;
        var dist = grid.Distance;
        bool hasDist = dist.Length == count;
        float scale = DistQuant / grid.Dx;
        var supply = SupplyMask(grid);
        var flag = new byte[checked(count + grid.WallLinks.Length)];
        if (grid.WallLinks.Length > 0) Array.Copy(grid.WallLinks, 0, flag, count, grid.WallLinks.Length);
        bool links = grid.WallLinks.Length == Q * count;
        Parallel.For(0, count, i =>
        {
            int packed = DistUnknown;
            if (hasDist)
            {
                float d = dist[i] * scale;
                packed = d >= DistUnknown ? DistUnknown : (int)d;
            }
            int near = 0;
            if (cell[i] == VoxelGrid.Air)
            {
                int x = i % Nx, y = i / Nx % Ny, z = i / (Nx * Ny);
                if (x == 0 || x == Nx - 1 || y == 0 || y == Ny - 1 || z == 0 || z == Nz - 1) near = 1;
                else
                    for (int q = 1; q < Q; q++)
                    {
                        int s = grid.Index(x - Cx[q], y - Cy[q], z - Cz[q]);
                        if (cell[s] == VoxelGrid.Solid || (supply != null && supply[s] != 0) || (links && flag[count + q * count + i] != 0)) { near = 1; break; }
                    }
            }
            else if (supply != null && supply[i] != 0) { flag[i] = (byte)(1 | ((supply[i] - 1) << 1)); return; }   // a link-wise patch cell: bit 0 marks it, bits 1-2 the mode (FixedCell.Mode)
            flag[i] = (byte)(near | (packed << 1));
        });
        return flag;
    }

    /// <summary>true on every Fixed cell that is a link-wise Supply patch cell (FixedCell.Supply &gt;= 0), or null
    /// when the grid has none.</summary>
    static byte[]? SupplyMask(VoxelGrid grid)
    {
        byte[]? m = null;
        foreach (var fc in grid.FixedCells)
        {
            if (fc.Supply < 0 || grid.Cell[fc.Index] != VoxelGrid.Fixed) continue;
            m ??= new byte[grid.Count];
            m[fc.Index] = (byte)(1 + fc.Mode);
        }
        return m;
    }

    /// <summary>Per Supply source (FixedCell.Supply), the drawn patch area Width x Height over the area of the cells
    /// the voxelizer actually carved for it. Voxel rounding makes a coarse patch up to ~2x too large or too small
    /// (0.6 m at 0.27 m cells carves 3 x 3 = 0.65 m2); the link inlet multiplies the nominal face velocity
    /// FlowRate/(W x H) by this ratio, so the carved face carries FlowRate before the flux controller corrects
    /// only what the links themselves lose. Sources with no usable size are left out (ratio 1).</summary>
    static Dictionary<int, float> SupplyAreaRatios(VoxelGrid grid, BuildingModel model)
    {
        var cells = new Dictionary<int, int>();
        foreach (var fc in grid.FixedCells)
            if (fc.Supply >= 0 && grid.Cell[fc.Index] == VoxelGrid.Fixed)
                cells[fc.Supply] = cells.TryGetValue(fc.Supply, out var c) ? c + 1 : 1;
        var ratio = new Dictionary<int, float>();
        foreach (var (src, count) in cells)
        {
            if (src >= model.Sources.Count) continue;
            var s = model.Sources[src];
            float drawn = s.Width * s.Height;
            if (drawn > 0f && count > 0) ratio[src] = drawn / (count * grid.Dx * grid.Dx);
        }
        return ratio;
    }

    /// <summary>The population a fluid cell pulls from a link-wise Supply patch cell along q: Ladd's moving-wall
    /// bounce-back, <paramref name="bb"/> (its own post-collision population heading into the patch) plus
    /// 6 w_q rho0 (c_q . u_w), which is <paramref name="fq"/> - <paramref name="fopp"/> of the patch cell's
    /// feq(1, u_w). A Supply (mode 0, bits 1-2 of <paramref name="patchFlag"/>) only adds on links carrying air into the room
    /// (c_q . u_w &gt; 0); the rest are a stationary no-slip face. A Return (mode 1) is the mirror: only links carrying air out
    /// (c_q . u_w &lt; 0) add (a negative amount). A Fan (mode 2) adds on both sides, so it moves air through itself and creates none.
    /// Shared by the CPU and GPU gathers.</summary>
    static float SupplyBounce(float bb, float fq, float fopp, int patchFlag)
    {
        float d = fq - fopp; int mode = (patchFlag >> 1) & 3;
        return bb + (mode == 0 ? MathF.Max(d, 0f) : mode == 1 ? MathF.Min(d, 0f) : d);
    }

    /// <summary>6a (P12-01): the link along which cell i pulls population q is a wall link (Solid neighbour or cut link, VoxelGrid.WallLinks);
    /// toward a neighbour that is not Solid a nonzero code is a cut link: a wall thinner than a cell between two fluid cells.</summary>
    static bool LinkCut(byte[] flags, int count, int i, int q) => flags.Length > count && flags[count + q * count + i] != 0;
    static bool LinkCutSoA(ArrayView<byte> flags, int count, int i, int q) => flags.Length > count && flags[count + q * count + i] != 0;

    const float DistQuant = 32f;                 // packed distance steps per Dx
    const float DistQuantInv = 1f / DistQuant;
    const int DistUnknown = 127;                 // saturated: no usable wall position for this cell

    /// <summary>Where the wall sits along the link between a fluid cell and its solid neighbour, as a
    /// fraction of the link: 0 = at the fluid node, 1 = at the solid node, 0.5 = halfway (which is what
    /// plain bounce-back assumes). The two distances are unsigned, so the fluid side is +d1 and the solid
    /// side -d2 and the zero crossing is at d1/(d1+d2). Returns -1 when either cell has no distance,
    /// which tells the caller to fall back to halfway bounce-back.</summary>
    static float LinkWallFraction(byte fluidFlags, byte solidFlags)
    {
        int pf = fluidFlags >> 1, ps = solidFlags >> 1;
        if (pf >= DistUnknown || ps >= DistUnknown) return -1f;
        float sum = (pf + ps) * DistQuantInv;
        return sum > 1e-6f ? pf * DistQuantInv / sum : 0.5f;
    }

    /// <summary>Samples rho across the domain for NaN or gross divergence (every air cell when <paramref name="full"/>, P17-06); throws if the run has blown up.</summary>
    static void CheckDivergence(VoxelGrid grid, float[] cur, bool full = false)
    {
        int count = grid.Count, Nx = grid.Nx, Ny = grid.Ny;
        int stride = full ? 1 : Math.Max(1, count / DivergenceSamples);
        for (int i = 0; i < count; i += stride)
        {
            if (grid.Cell[i] != VoxelGrid.Air) continue;
            int x = i % Nx, y = i / Nx % Ny;
            if (x == 0 || x == Nx - 1 || y == 0 || y == Ny - 1) continue; // ring is ramped, not physical
            float rho = 0f;
            int b = i * Q;
            for (int q = 0; q < Q; q++) rho += cur[b + q];
            if (float.IsNaN(rho) || rho < RhoMin || rho > RhoMax)
                throw new InvalidOperationException("Airflow solver diverged; try Refine quality or a lower wind speed");
        }
    }

    static int[] BuildDivergenceSampleIndices(VoxelGrid grid)
    {
        int count = grid.Count, Nx = grid.Nx, Ny = grid.Ny;
        int stride = Math.Max(1, count / DivergenceSamples);
        var list = new List<int>();
        for (int i = 0; i < count; i += stride)
        {
            if (grid.Cell[i] != VoxelGrid.Air) continue;
            int x = i % Nx, y = i / Nx % Ny;
            if (x == 0 || x == Nx - 1 || y == 0 || y == Ny - 1) continue;
            list.Add(i);
        }
        return list.ToArray();
    }

    // ======================================================================================
    // CPU path: AoS (cur[i*Q+q]), Parallel.For, unrolled per-direction gather offsets.
    // ======================================================================================

    static void WriteAoS(float[] arr, int i, in F19 v)
    {
        int b = i * Q;
        arr[b] = v.F0; arr[b + 1] = v.F1; arr[b + 2] = v.F2; arr[b + 3] = v.F3; arr[b + 4] = v.F4;
        arr[b + 5] = v.F5; arr[b + 6] = v.F6; arr[b + 7] = v.F7; arr[b + 8] = v.F8; arr[b + 9] = v.F9;
        arr[b + 10] = v.F10; arr[b + 11] = v.F11; arr[b + 12] = v.F12; arr[b + 13] = v.F13; arr[b + 14] = v.F14;
        arr[b + 15] = v.F15; arr[b + 16] = v.F16; arr[b + 17] = v.F17; arr[b + 18] = v.F18;
    }

    /// <summary>Host-side twin of the GPU's <see cref="WriteSoA"/>: population q of cell i at [q*count + i], for
    /// arrays built on the host and uploaded into a device population buffer.</summary>
    static void WriteSoAHost(float[] arr, int i, int count, in F19 v)
    {
        arr[i] = v.F0; arr[count + i] = v.F1; arr[2 * count + i] = v.F2; arr[3 * count + i] = v.F3; arr[4 * count + i] = v.F4;
        arr[5 * count + i] = v.F5; arr[6 * count + i] = v.F6; arr[7 * count + i] = v.F7; arr[8 * count + i] = v.F8; arr[9 * count + i] = v.F9;
        arr[10 * count + i] = v.F10; arr[11 * count + i] = v.F11; arr[12 * count + i] = v.F12; arr[13 * count + i] = v.F13; arr[14 * count + i] = v.F14;
        arr[15 * count + i] = v.F15; arr[16 * count + i] = v.F16; arr[17 * count + i] = v.F17; arr[18 * count + i] = v.F18;
    }

    static F19 ReadAoS(float[] arr, int i)
    {
        int b = i * Q;
        return new F19(arr[b], arr[b + 1], arr[b + 2], arr[b + 3], arr[b + 4], arr[b + 5], arr[b + 6], arr[b + 7], arr[b + 8], arr[b + 9],
            arr[b + 10], arr[b + 11], arr[b + 12], arr[b + 13], arr[b + 14], arr[b + 15], arr[b + 16], arr[b + 17], arr[b + 18]);
    }

    /// <summary>6b: the populations of one wind-ring cell (inflow, outflow, lid) from its inner neighbour's post-collision
    /// state <paramref name="fN"/>; (ux, uy, uz) is the ramped target velocity (inflow, lid), (nx, ny) the outward face normal. See the
    /// block comment at <see cref="WindSpongeCells"/>. A neighbour with no valid density (a solid cell) gives rest equilibrium.
    /// Shared by <see cref="SetBoundaryCpu"/> and <see cref="BoundaryKernel"/>.</summary>
    static F19 RingCell(in F19 fN, byte cls, float ux, float uy, float uz, float nx, float ny)
    {
        float rho = fN.F0 + fN.F1 + fN.F2 + fN.F3 + fN.F4 + fN.F5 + fN.F6 + fN.F7 + fN.F8 + fN.F9 + fN.F10 + fN.F11 + fN.F12 + fN.F13 + fN.F14 + fN.F15 + fN.F16 + fN.F17 + fN.F18;
        if (!(rho > RhoMin && rho < RhoMax)) return ComputeFeq(1f, 0f, 0f, 0f);
        float invRho = 1f / rho;
        float vx = (fN.F1 - fN.F2 + fN.F7 - fN.F8 + fN.F9 - fN.F10 + fN.F11 - fN.F12 + fN.F13 - fN.F14) * invRho;
        float vy = (fN.F3 - fN.F4 + fN.F7 - fN.F8 - fN.F9 + fN.F10 + fN.F15 - fN.F16 + fN.F17 - fN.F18) * invRho;
        float vz = (fN.F5 - fN.F6 + fN.F11 - fN.F12 - fN.F13 + fN.F14 + fN.F15 - fN.F16 - fN.F17 + fN.F18) * invRho;
        F19 eqN = ComputeFeq(rho, vx, vy, vz);
        F19 eqB;
        if (cls == BcOutflow)
        {
            float un = vx * nx + vy * ny;
            float ox = vx, oy = vy;
            if (un < 0f) { ox -= un * nx; oy -= un * ny; }
            eqB = ComputeFeq(1f, ox, oy, vz);
        }
        else eqB = ComputeFeq(rho, ux, uy, uz);   // inflow, lid
        return new F19(eqB.F0 + fN.F0 - eqN.F0, eqB.F1 + fN.F1 - eqN.F1, eqB.F2 + fN.F2 - eqN.F2, eqB.F3 + fN.F3 - eqN.F3,
            eqB.F4 + fN.F4 - eqN.F4, eqB.F5 + fN.F5 - eqN.F5, eqB.F6 + fN.F6 - eqN.F6, eqB.F7 + fN.F7 - eqN.F7, eqB.F8 + fN.F8 - eqN.F8,
            eqB.F9 + fN.F9 - eqN.F9, eqB.F10 + fN.F10 - eqN.F10, eqB.F11 + fN.F11 - eqN.F11, eqB.F12 + fN.F12 - eqN.F12,
            eqB.F13 + fN.F13 - eqN.F13, eqB.F14 + fN.F14 - eqN.F14, eqB.F15 + fN.F15 - eqN.F15, eqB.F16 + fN.F16 - eqN.F16,
            eqB.F17 + fN.F17 - eqN.F17, eqB.F18 + fN.F18 - eqN.F18);
    }

    /// <summary>Outflow faces of the ring for this wind as a bitmask (1 = x min, 2 = x max, 4 = y min, 8 = y max), classified exactly as
    /// <see cref="BuildBoundaryList"/> classifies them; 0 with no wind.</summary>
    internal static int OutflowFaces(Vector2 wind)
    {
        float s = wind.Length();
        if (s <= 1e-6f) return 0;
        float wx = wind.X / s, wy = wind.Y / s;
        return (-wx > RingClassThreshold ? 1 : 0) | (wx > RingClassThreshold ? 2 : 0) | (-wy > RingClassThreshold ? 4 : 0) | (wy > RingClassThreshold ? 8 : 0);
    }

    /// <summary>Sponge depth for a grid: <see cref="WindSpongeCells"/>, at most 10 % of the shorter plan side.</summary>
    static int SpongeDepth(VoxelGrid g) => Math.Min(WindSpongeCells, Math.Min(g.Nx, g.Ny) / 10);

    /// <summary>6b (P13b-08): the cell's base relaxation time inside the outflow sponge. Lattice viscosity (tau0 - 1/2)/3 times
    /// 1 + (<see cref="SpongeNuRatio"/> - 1) r^2, r = 1 - d/cells, d = cells from the nearest outflow face (the ring is d = 0); a
    /// viscosity ramp, not a velocity relaxation. tau0 outside the band or with no sponge (faces = 0). The Smagorinsky part adds on
    /// top as everywhere. CPU and every GPU step kernel call it.</summary>
    static float SpongeTau(float tau0, int x, int y, int Nx, int Ny, int faces, int cells)
    {
        faces &= 15;   // the outflow faces only (bits 4-7 are absorbing-only inflow faces, see SpongeAbsorb)
        if (faces == 0 || cells <= 0) return tau0;
        int d = cells;
        if ((faces & 1) != 0 && x < d) d = x;
        if ((faces & 2) != 0 && Nx - 1 - x < d) d = Nx - 1 - x;
        if ((faces & 4) != 0 && y < d) d = y;
        if ((faces & 8) != 0 && Ny - 1 - y < d) d = Ny - 1 - y;
        if (d >= cells) return tau0;
        float r = (cells - d) / (float)cells;
        return 0.5f + (tau0 - 0.5f) * (1f + (SpongeNuRatio - 1f) * r * r);
    }

    /// <summary>RZ-01: the density-relaxation absorbing layer, in the same band and with the same r as <see cref="SpongeTau"/>, on the outflow
    /// faces (bits 0-3 of <paramref name="faces"/>) and on any face in bits 4-7. Post-collision f_i += sigma (feq_i(1, u) - feq_i(rho, u)) =
    /// sigma (1 - rho) feq_i(1, u), sigma = <see cref="SpongeSigmaMax"/> r^2: rho relaxes toward 1, u (the cell's own) is not forced. It is
    /// what absorbs the start-up and gust sound when no face of the ring is an equilibrium face (any wind more than 5.7 degrees off a grid
    /// axis). CPU and every GPU step kernel call it right after the collision.</summary>
    static F19 SpongeAbsorb(in F19 f, float rho, float ux, float uy, float uz, int x, int y, int Nx, int Ny, int faces, int cells)
    {
        int m = (faces | (faces >> 4)) & 15;
        if (m == 0 || cells <= 0) return f;
        int d = cells;
        if ((m & 1) != 0 && x < d) d = x;
        if ((m & 2) != 0 && Nx - 1 - x < d) d = Nx - 1 - x;
        if ((m & 4) != 0 && y < d) d = y;
        if ((m & 8) != 0 && Ny - 1 - y < d) d = Ny - 1 - y;
        if (d >= cells) return f;
        float r = (cells - d) / (float)cells;
        float a = SpongeSigmaMax * r * r * (1f - rho);
        F19 e = ComputeFeq(1f, ux, uy, uz);
        return new F19(f.F0 + a * e.F0, f.F1 + a * e.F1, f.F2 + a * e.F2, f.F3 + a * e.F3, f.F4 + a * e.F4, f.F5 + a * e.F5, f.F6 + a * e.F6,
            f.F7 + a * e.F7, f.F8 + a * e.F8, f.F9 + a * e.F9, f.F10 + a * e.F10, f.F11 + a * e.F11, f.F12 + a * e.F12, f.F13 + a * e.F13,
            f.F14 + a * e.F14, f.F15 + a * e.F15, f.F16 + a * e.F16, f.F17 + a * e.F17, f.F18 + a * e.F18);
    }

    /// <summary>Boundary update (ring + Fixed cells), never streamed/collided -- this is their only update.
    /// Inflow, outflow and lid: <see cref="RingCell"/> (6b). Tangential: feq(1, mean profile tangential u), as before 6b.
    /// Open (no ambient wind, mechanical sources only): feq(1, u_in) from the interior neighbour, with no backflow
    /// clamp -- air may be drawn in or pushed out on any ring face.
    /// Supply (link-wise inlet): feq(1, u_w), read by the neighbours' gather as the moving-wall increment.
    /// Fixed (Return and Fan patches): feq(rho0 = 1, u_fixed), see <see cref="FixedCellRho"/>.</summary>
    static void SetBoundaryCpu(float[] cur, BoundaryList bl, float ramp)
    {
        int n = bl.Index.Length;
        Parallel.For(0, n, k =>
        {
            int idx = bl.Index[k]; byte cls = bl.Class[k];
            float velX = bl.Vel[k], velY = bl.Vel[n + k], velZ = bl.Vel[2 * n + k];
            F19 feq;
            if (cls == BcInflow || cls == BcOutflow || cls == BcLid)
            {
                feq = RingCell(ReadAoS(cur, bl.NeighborSlot[k]), cls, velX * ramp, velY * ramp, velZ * ramp, bl.NxNy[k], bl.NxNy[n + k]);
            }
            else if (cls == BcTangential || cls == BcInflowEq) feq = ComputeFeq(1f, velX * ramp, velY * ramp, velZ * ramp);
            else if (cls == BcOpen || cls == BcOutflowEq)
            {
                NeighborMacroscopic(cur, bl.NeighborSlot[k], out float ux, out float uy, out float uz);
                if (cls == BcOutflowEq)
                {
                    float nx = bl.NxNy[k], ny = bl.NxNy[n + k], un = ux * nx + uy * ny;
                    if (un < 0f) { ux -= un * nx; uy -= un * ny; }
                }
                feq = ComputeFeq(1f, ux, uy, uz);
            }
            else if (cls == BcSupply)
            {
                // Link-wise inlet: the gather reads the wall increment off these populations (SupplyBounce).
                feq = ComputeFeq(1f, velX, velY, velZ);
            }
            else feq = ComputeFeq(FixedCellRho, velX, velY, velZ);
            WriteAoS(cur, idx, feq);
        });
    }

    /// <summary>One LBM step over all interior (non-ring) air cells: pull-stream from <paramref name="cur"/>,
    /// collide, write post-collision populations into <paramref name="nxt"/>.
    /// <param name="dev">Device data from <see cref="BuildDeviceData"/>, or null/empty when the grid has no
    /// body-force devices -- in which case every cell takes the plain <see cref="Collide"/> exactly as
    /// before. A device cell collides with <see cref="CollideForced"/>, which is bit-identical to Collide
    /// at zero force, so nothing outside a device region can move.</param></summary>
    static void RunStep(VoxelGrid grid, byte[] nearBoundary, float[] cur, float[] nxt, float tau0, bool accumulate,
        double[] sumRho, double[] sumUx, double[] sumUy, double[] sumUz, double[] sumNuT, float[]? dev, float[] instant,
        float[]? wm = null, float wmNuP = 0f, float wmNuW = 0f, float forceX = 0f, int spongeFaces = 0, int spongeCells = 0)
    {
        int Nx = grid.Nx, Ny = grid.Ny, Nz = grid.Nz;
        if (Ny <= 2 || Nx <= 2) return;
        var cell = grid.Cell;
        int rows = Nz * (Ny - 2);
        bool hasDev = dev is { Length: > 0 };
        int count = grid.Count;

        int nxny = Nx * Ny;
        int o1 = -1, o2 = 1, o3 = -Nx, o4 = Nx, o5 = -nxny, o6 = nxny,
            o7 = -1 - Nx, o8 = 1 + Nx, o9 = -1 + Nx, o10 = 1 - Nx,
            o11 = -1 - nxny, o12 = 1 + nxny, o13 = -1 + nxny, o14 = 1 - nxny,
            o15 = -Nx - nxny, o16 = Nx + nxny, o17 = -Nx + nxny, o18 = Nx - nxny;

        Parallel.For(0, rows, rowIdx =>
        {
            int y = 1 + rowIdx % (Ny - 2);
            int z = rowIdx / (Ny - 2);
            if ((z == 0 && grid.OpenZMin) || (z == Nz - 1 && grid.OpenZMax)) return;
            int rowBase = Nx * (y + Ny * z);

            for (int x = 1; x < Nx - 1; x++)
            {
                int i = x + rowBase;
                if (cell[i] != VoxelGrid.Air) continue; // Solid or Fixed: neither streamed nor collided

                F19 fin = (nearBoundary[i] & 1) != 0
                    ? GatherBoundary(grid, cur, cell, nearBoundary, x, y, z, i, wm, wmNuP, wmNuW)
                    : GatherPlain(cur, i, o1, o2, o3, o4, o5, o6, o7, o8, o9, o10, o11, o12, o13, o14, o15, o16, o17, o18);

                float t0 = SpongeTau(tau0, x, y, Nx, Ny, spongeFaces, spongeCells);   // 6b: the outflow sponge
                F19 fout; float rho, ux, uy, uz, tau;
                if (hasDev && dev![3 * count + i] != 0f)
                {
                    DeviceForce(fin, dev[i], dev[count + i], dev[2 * count + i], dev[3 * count + i],
                        out float fx, out float fy, out float fz);
                    CollideForced(fin, t0, fx, fy, fz, out fout, out rho, out ux, out uy, out uz, out tau);
                }
                else if (forceX != 0f) CollideForced(fin, t0, forceX, 0f, 0f, out fout, out rho, out ux, out uy, out uz, out tau);
                else Collide(fin, t0, out fout, out rho, out ux, out uy, out uz, out tau);
                if (spongeFaces != 0) fout = SpongeAbsorb(fout, rho, ux, uy, uz, x, y, Nx, Ny, spongeFaces, spongeCells);   // RZ-01
                WriteAoS(nxt, i, fout);

                if (accumulate)
                {
                    if (instant.Length > 0) instant[i] = (tau - t0) / 3f;   // 6k: this step's eddy viscosity, read by the online age
                    sumRho[i] += rho - 1f; sumUx[i] += ux; sumUy[i] += uy; sumUz[i] += uz; sumNuT[i] += (tau - t0) / 3f;
                }
            }
        });
    }

    /// <summary>Sandbox test hook: x-periodic ghost columns, x = 0 takes the populations of Nx-2 and x = Nx-1 those of column 1.</summary>
    static void PeriodicXCpu(VoxelGrid grid, float[] cur)
    {
        for (int z = 0; z < grid.Nz; z++) for (int y = 0; y < grid.Ny; y++)
        {
            Array.Copy(cur, grid.Index(grid.Nx - 2, y, z) * Q, cur, grid.Index(0, y, z) * Q, Q);
            Array.Copy(cur, grid.Index(1, y, z) * Q, cur, grid.Index(grid.Nx - 1, y, z) * Q, Q);
        }
    }

    /// <summary>Branch-free gather for a cell whose 19 neighbours are all guaranteed streamable (Air or
    /// Fixed) and in range: a direct read per direction, offset by a precomputed neighbour-index delta.</summary>
    static F19 GatherPlain(float[] cur, int i,
        int o1, int o2, int o3, int o4, int o5, int o6, int o7, int o8, int o9, int o10,
        int o11, int o12, int o13, int o14, int o15, int o16, int o17, int o18) => new(
        cur[i * Q],
        cur[(i + o1) * Q + 1], cur[(i + o2) * Q + 2], cur[(i + o3) * Q + 3], cur[(i + o4) * Q + 4],
        cur[(i + o5) * Q + 5], cur[(i + o6) * Q + 6], cur[(i + o7) * Q + 7], cur[(i + o8) * Q + 8],
        cur[(i + o9) * Q + 9], cur[(i + o10) * Q + 10], cur[(i + o11) * Q + 11], cur[(i + o12) * Q + 12],
        cur[(i + o13) * Q + 13], cur[(i + o14) * Q + 14], cur[(i + o15) * Q + 15], cur[(i + o16) * Q + 16],
        cur[(i + o17) * Q + 17], cur[(i + o18) * Q + 18]);

    /// <summary>Checked gather for a cell flagged by <see cref="ComputeNearBoundary"/>: halfway bounce-back
    /// where the neighbour is Solid, moving-wall bounce-back off a link-wise Supply patch cell (<see cref="SupplyBounce"/>;
    /// Air and other Fixed neighbours stream normally -- a Fixed cell always
    /// carries valid populations, written every step by <see cref="SetBoundaryCpu"/>), free-slip reflection
    /// where the z-neighbour falls outside the domain.</summary>
    static F19 GatherBoundary(VoxelGrid grid, float[] cur, byte[] cell, byte[] flags, int x, int y, int z, int i,
        float[]? wm = null, float wmNuP = 0f, float wmNuW = 0f)
    {
        int Nz = grid.Nz;
        Span<float> fin = stackalloc float[Q];
        float usx = 0f, usy = 0f, usz = 0f, wmRho = 1f;
        bool wmOn = wm != null && wmNuP > 0f;
        if (wmOn) WallSlipUpdate(grid, cur, cell, flags, x, y, z, i, wm!, wmNuP, wmNuW, out usx, out usy, out usz, out wmRho);
        float wallMass = 0f;   // mass the interpolated wall links returned beyond what they received
        for (int q = 0; q < Q; q++)
        {
            int sz = z - Cz[q];
            int sx = x - Cx[q], sy = y - Cy[q];
            if (sz < 0 || sz >= Nz)
            {
                int sIdx = grid.Index(sx, sy, z);
                fin[q] = cell[sIdx] == VoxelGrid.Solid ? cur[i * Q + Opp[q]] : cur[sIdx * Q + MirrorZ[q]];
            }
            else
            {
                int sIdx = grid.Index(sx, sy, sz);
                byte sc = cell[sIdx];
                if (sc == VoxelGrid.Solid || LinkCut(flags, grid.Count, i, q))   // 6a: a cut link bounces like a Solid neighbour
                {
                    float wv = WallBounce(grid, cur, cell, flags, i, sIdx, q, x, y, z);
                    if (wmOn) wv += WallMoveTerm(LinkQw(grid, flags, i, sIdx, q), Cx[q] * usx + Cy[q] * usy + Cz[q] * usz, wmRho, Cx[q] * Cx[q] + Cy[q] * Cy[q] + Cz[q] * Cz[q]);
                    fin[q] = wv;
                    wallMass += wv - cur[i * Q + Opp[q]];
                }
                else
                    fin[q] = sc == VoxelGrid.Fixed && (flags[sIdx] & 1) != 0
                        ? SupplyBounce(cur[i * Q + Opp[q]], cur[sIdx * Q + q], cur[sIdx * Q + Opp[q]], flags[sIdx])
                        : cur[sIdx * Q + q];
            }
        }
        // Linear interpolated bounce-back is not mass conserving: a node whose wall-normal velocity is nonzero gets back
        // (1 - 1/2qw) (f_q - f_opp) more or less than it sent. At the geometric qw ~ 0.9 of a thin wall that is a source of
        // ~15 % of the supply flow at a nozzle mouth. The rest population has c = 0, so putting the difference there keeps the
        // node's mass and every momentum moment unchanged. GPU twin: GatherBoundarySoA.
        fin[0] -= wallMass;
        return new F19(fin[0], fin[1], fin[2], fin[3], fin[4], fin[5], fin[6], fin[7], fin[8], fin[9],
            fin[10], fin[11], fin[12], fin[13], fin[14], fin[15], fin[16], fin[17], fin[18]);
    }

    /// <summary>The population coming back off a wall, by Bouzidi-Firdaouss-Lallemand linear interpolated
    /// bounce-back. With the wall at a fraction qw of the link from the fluid node (<see
    /// cref="LinkWallFraction"/>), the reflected population is a linear interpolation that puts the no-slip
    /// surface exactly where the geometry has it instead of always halfway between the two cell centres:
    /// <code>
    ///   qw &gt;= 1/2 : f = (1/2qw) f_post(i, opp) + (1 - 1/2qw) f_post(i, q)
    ///   qw &lt;  1/2 : f = 2qw   f_post(i, opp) + (1 - 2qw)    f_post(i + c_q, opp)
    /// </code>
    /// Both forms are convex combinations, so neither can amplify, and both collapse to plain halfway
    /// bounce-back at qw = 1/2. The qw &lt; 1/2 branch reads the fluid cell one step further from the wall;
    /// where that is missing (another wall, or the domain edge) it falls back to halfway, as it does when
    /// either cell has no distance. 6a: a wall thinner than a cell is a cut link between two fluid cells (<see cref="LinkCut"/>): both
    /// sides bounce off it with their own qw, nothing streams across it, and the second node is not read across another cut link.</summary>
    static float WallBounce(VoxelGrid grid, float[] cur, byte[] cell, byte[] flags, int i, int sIdx, int q, int x, int y, int z)
    {
        float bb = cur[i * Q + Opp[q]];
        int encoded = flags.Length > grid.Count ? flags[grid.Count + q * grid.Count + i] : 0;
        float qw = flags.Length > grid.Count ? (encoded == 0 ? -1f : (encoded - 1) / 254f) : LinkWallFraction(flags[i], flags[sIdx]);
        if (qw < 0f) return bb;
        if (qw >= 0.5f)
        {
            float a = 0.5f / qw;
            return a * bb + (1f - a) * cur[i * Q + q];
        }
        int fx = x + Cx[q], fy = y + Cy[q], fz = z + Cz[q];
        if (fx < 0 || fx >= grid.Nx || fy < 0 || fy >= grid.Ny || fz < 0 || fz >= grid.Nz) return bb;
        int fIdx = grid.Index(fx, fy, fz);
        if (cell[fIdx] == VoxelGrid.Solid || LinkCut(flags, grid.Count, i, Opp[q])) return bb;   // 6a: no second node behind a cut link either
        return 2f * qw * bb + (1f - 2f * qw) * cur[fIdx * Q + Opp[q]];
    }

    /// <summary>Sandbox WM2a: x-momentum of the populations <see cref="GatherBoundary"/> hands the collision at (x, y, z); pass wm = null
    /// for plain bounce-back. <paramref name="flags"/> may be one byte per cell (distance unknown, halfway walls).</summary>
    internal static float GatherMomentumX(VoxelGrid grid, float[] cur, byte[] flags, int x, int y, int z, float[]? wm, float nuP, float nuW)
    {
        var f = GatherBoundary(grid, cur, grid.Cell, flags, x, y, z, grid.Index(x, y, z), wm, nuP, nuW);
        return f.F1 - f.F2 + f.F7 - f.F8 + f.F9 - f.F10 + f.F11 - f.F12 + f.F13 - f.F14;
    }

    // ---- Equilibrium wall function: Werner-Wengle law as a tangential slip velocity (RunOptions.WallModel) ----
    // Han, Ooka and Kikumoto, Build. Environ. 195 (2021) 107764. Each wall node carries one smoothed slip vector u_s (wm, 3 floats
    // per cell, dense only when the model is on); the Bouzidi moving-wall term below imposes it on every wall link of the node.
    // Sign: fin[q] arrives from the wall along c_q = c_ibar, the reflection of the outgoing i = Opp[q] (c_i = -c_q). The standard
    // moving-wall correction f_ibar += -2 w rho (c_i . u_w)/cs^2 therefore reads +2 w rho (c_q . u_s)/cs^2 = 6 w rho (c_q . u_s):
    // a wall at -x with u_s = +x feeds extra +x momentum (q = 1, c = +x), i.e. drags the fluid toward +x. Bouzidi scales it by
    // 1/(2 qw) for qw >= 1/2 and by 1 for qw < 1/2 and for the halfway fallback.
    const float WwA = 8.3f, WwB = 1f / 7f, WwSwitch = 11.81f * 11.81f, WmRelax = 0.1f, WmMinDist = 0.25f;

    const float WwAInv78 = 0.15696639f;   // A^(-7/8) with A = 8.3
    static float Root8(float x) => MathF.Sqrt(MathF.Sqrt(MathF.Sqrt(x)));

    /// <summary>Link wall fraction as <see cref="WallBounce"/> decodes it; negative = unknown (halfway).</summary>
    static float LinkQw(VoxelGrid grid, byte[] flags, int i, int sIdx, int q)
    {
        if (flags.Length <= grid.Count) return LinkWallFraction(flags[i], flags[sIdx]);
        int encoded = flags[grid.Count + q * grid.Count + i];
        return encoded == 0 ? -1f : (encoded - 1) / 254f;
    }

    /// <summary>Moving-wall addition to a reflected population: <paramref name="cu"/> = c_q . u_s, <paramref name="c2"/> = |c_q|^2 (1 or 2).</summary>
    static float WallMoveTerm(float qw, float cu, float rho, int c2) =>
        (qw >= 0.5f ? 0.5f / qw : 1f) * 6f * (c2 == 1 ? 1f / 18f : 1f / 36f) * rho * cu;

    /// <summary>Werner-Wengle slip target for a node with wall normal n (unit, pointing into the fluid), wall distance d (cells),
    /// velocity u (lattice): u_s* = max(0, U - u_tau^2 d / nuW) along the tangential direction, |u_s*| <= U. nuP is the PHYSICAL
    /// viscosity in lattice units; nuW the node's total lattice viscosity nu0 + nu_t = (tau - 1/2)/3 (floor and Smagorinsky included,
    /// see <see cref="SmagNuW"/>).</summary>
    static void WallSlipTarget(float ux, float uy, float uz, float nx, float ny, float nz, float d, float nuP, float nuW,
        out float tx, out float ty, out float tz)
    {
        float un = ux * nx + uy * ny + uz * nz;
        float vx = ux - un * nx, vy = uy - un * ny, vz = uz - un * nz;
        float U = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
        tx = 0f; ty = 0f; tz = 0f;
        if (U < 1e-7f) return;
        float ut = U * d / nuP <= WwSwitch
            ? MathF.Sqrt(nuP * U / d)
            : WwAInv78 * U / (Root8(U) * Root8(d / nuP));   // = (U / (A (d/nu)^B))^(1/(1+B)) = A^-7/8 U^7/8 (d/nu)^-1/8; ILGPU has no Pow/Log/Exp without its Algorithms library
        float us = MathF.Min(MathF.Max(0f, U - ut * ut * d / nuW), U);
        float s = us / U;
        tx = vx * s; ty = vy * s; tz = vz * s;
    }

    /// <summary>Updates the node's smoothed slip vector (u_s += 0.1 (u_s* - u_s)) and returns it with the node density. The wall normal
    /// is -normalize(sum of node-to-wall link directions) over the links that hit Solid, which at a concave corner is the bisector;
    /// d = min over those links of qw |c . n| (qw = 1/2 where unknown), clamped to >= 0.25 cell. GPU twin: <see cref="WallSlipUpdateSoA"/>.</summary>
    static void WallSlipUpdate(VoxelGrid grid, float[] cur, byte[] cell, byte[] flags, int x, int y, int z, int i,
        float[] wm, float nuP, float nuW, out float usx, out float usy, out float usz, out float rho)
    {
        int Nz = grid.Nz;
        float sx = 0f, sy = 0f, sz = 0f;
        for (int q = 1; q < Q; q++)
        {
            int z0 = z - Cz[q];
            if (z0 < 0 || z0 >= Nz || cell[grid.Index(x - Cx[q], y - Cy[q], z0)] != VoxelGrid.Solid && !LinkCut(flags, grid.Count, i, q)) continue;
            sx += Cx[q]; sy += Cy[q]; sz += Cz[q];   // fin[q] arrives along +c_q, so c_q already points from the wall into the fluid
        }
        int b = i * Q; rho = 0f; float mx = 0f, my = 0f, mz = 0f;
        for (int k = 0; k < Q; k++) { float f = cur[b + k]; rho += f; mx += Cx[k] * f; my += Cy[k] * f; mz += Cz[k] * f; }
        float nl = MathF.Sqrt(sx * sx + sy * sy + sz * sz), tx = 0f, ty = 0f, tz = 0f;
        if (nl > 1e-6f)
        {
            float nx = sx / nl, ny = sy / nl, nz = sz / nl, d = float.MaxValue;
            for (int q = 1; q < Q; q++)
            {
                int z0 = z - Cz[q];
                if (z0 < 0 || z0 >= Nz) continue;
                int sIdx = grid.Index(x - Cx[q], y - Cy[q], z0);
                if (cell[sIdx] != VoxelGrid.Solid && !LinkCut(flags, grid.Count, i, q)) continue;
                float qw = LinkQw(grid, flags, i, sIdx, q);
                d = MathF.Min(d, (qw < 0f ? 0.5f : qw) * MathF.Abs(Cx[q] * nx + Cy[q] * ny + Cz[q] * nz));
            }
            // nu_w = nu0 + nu_t (Han et al.): Smagorinsky nu_t from this node's own populations as stored in cur, i.e. the previous step's
            // post-collision state (Collide sees the post-streaming gather, which is not formed yet and needs the slip it is about to get).
            // Post-collision f_neq = (1 - 1/tau) f_neq(pre), |1 - 1/tau| ~ 0.9-1 at tau ~ 0.5-0.55, so |Pi| is read ~5 % low at most.
            var fc = new F19(cur[b], cur[b + 1], cur[b + 2], cur[b + 3], cur[b + 4], cur[b + 5], cur[b + 6], cur[b + 7], cur[b + 8], cur[b + 9],
                cur[b + 10], cur[b + 11], cur[b + 12], cur[b + 13], cur[b + 14], cur[b + 15], cur[b + 16], cur[b + 17], cur[b + 18]);
            WallSlipTarget(mx / rho, my / rho, mz / rho, nx, ny, nz, MathF.Max(d, WmMinDist), nuP, SmagNuW(fc, 3f * nuW + 0.5f), out tx, out ty, out tz);
        }
        usx = wm[3 * i] += WmRelax * (tx - wm[3 * i]);
        usy = wm[3 * i + 1] += WmRelax * (ty - wm[3 * i + 1]);
        usz = wm[3 * i + 2] += WmRelax * (tz - wm[3 * i + 2]);
    }

    /// <summary>rho/u of a cell from its (pre-collision) populations -- used to read the pressure-outlet
    /// ring cells' interior neighbour. Same macroscopic-moment formulas as <see cref="Collide"/>.</summary>
    static void NeighborMacroscopic(float[] cur, int idx, out float ux, out float uy, out float uz)
    {
        int b = idx * Q;
        float f0 = cur[b], f1 = cur[b + 1], f2 = cur[b + 2], f3 = cur[b + 3], f4 = cur[b + 4], f5 = cur[b + 5], f6 = cur[b + 6],
              f7 = cur[b + 7], f8 = cur[b + 8], f9 = cur[b + 9], f10 = cur[b + 10], f11 = cur[b + 11], f12 = cur[b + 12], f13 = cur[b + 13],
              f14 = cur[b + 14], f15 = cur[b + 15], f16 = cur[b + 16], f17 = cur[b + 17], f18 = cur[b + 18];
        float rho = f0 + f1 + f2 + f3 + f4 + f5 + f6 + f7 + f8 + f9 + f10 + f11 + f12 + f13 + f14 + f15 + f16 + f17 + f18;
        float invRho = 1f / rho;
        float mx = f1 - f2 + f7 - f8 + f9 - f10 + f11 - f12 + f13 - f14;
        float my = f3 - f4 + f7 - f8 - f9 + f10 + f15 - f16 + f17 - f18;
        float mz = f5 - f6 + f11 - f12 - f13 + f14 + f15 - f16 - f17 + f18;
        ux = mx * invRho; uy = my * invRho; uz = mz * invRho;
    }

    // ======================================================================================
    // Thermal: D3Q7 temperature lattice g, advected by the flow, Guo (2002) buoyancy forcing on the
    // D3Q19 momentum lattice. Shares ComputeFeq/Collide's grouping trick (a +/-direction pair shares an
    // "even" term, differs by an "odd" one) for both ComputeGeq and the forcing term S_i, and reuses the
    // plain/checked F19 gathers unchanged -- only G7 needs its own (6 face-neighbours, no diagonals).
    // ======================================================================================

    static ReadOnlySpan<int> Gcx => new[] { 0, 1, -1, 0, 0, 0, 0 };
    static ReadOnlySpan<int> Gcy => new[] { 0, 0, 0, 1, -1, 0, 0 };
    static ReadOnlySpan<int> Gcz => new[] { 0, 0, 0, 0, 0, 1, -1 };
    static ReadOnlySpan<int> GOpp => new[] { 0, 2, 1, 4, 3, 6, 5 };
    static ReadOnlySpan<int> GMirrorZ => new[] { 0, 1, 2, 3, 4, 6, 5 };
    const int Q7 = 7;
    const float Pr = 0.71f;                 // laminar Prandtl number of air
    const float TurbPr = 0.9f;              // turbulent Prandtl number
    const float CkTurb = 0.094f;             // one-equation SGS k = (nu_t/(Ck*dx))^2
    const float GAccel = 9.81f;
    const float RhoCp = 1.2f * 1005f;        // J/(m3 K), air at ~20C
    const float PeopleWatts = 70f;
    const float ThermalWarmupFlowThroughs = 4f;   // buoyancy-driven cavities need several turnovers to settle,
    const float ThermalAverageFlowThroughs = 1f;  // not the ~1 a wind-driven external flow needs.
    const int ThermalMaxSteps = 60000;
    const float ThermalRiseK = 0.5f;              // bulk temperature rise a Watts-driven scene must reach
    const float ThermalDevMinSeconds = 60f;       // before its run is long enough to have developed thermally
    const float ThermalDevMaxSeconds = 300f;

    static readonly int[] GDirLookupHost = BuildGDirLookup();
    static int[] BuildGDirLookup()
    {
        var arr = new int[35];
        for (int q = 0; q < Q7; q++)
        { arr[q] = Gcx[q]; arr[7 + q] = Gcy[q]; arr[14 + q] = Gcz[q]; arr[21 + q] = GOpp[q]; arr[28 + q] = GMirrorZ[q]; }
        return arr;
    }

    struct G7
    {
        public float G0, G1, G2, G3, G4, G5, G6;
        public G7(float g0, float g1, float g2, float g3, float g4, float g5, float g6)
        { G0 = g0; G1 = g1; G2 = g2; G3 = g3; G4 = g4; G5 = g5; G6 = g6; }
    }

    // D3Q7 with w0 = 1/4, w1 = 1/8: c_s^2 = sum_i w_i c_ix^2 = 2/8 = 1/4 (NOT the D3Q19's 1/3), so the equilibrium
    // carries c.u/c_s^2 = 4 c.u and the relaxation time is tau = alpha/c_s^2 + 1/2 = 4 alpha + 1/2.
    // (The equilibrium and tauG used 3 = 1/c_s^2 of the D3Q19 before 2026-10-09: the temperature was advected at 0.75 u with
    // 0.75 of the intended diffusivity -- the same Peclet number, so steady results such as the cavity Nu are unchanged,
    // but every thermal transient ran 25 % slow against the flow.)
    const float GInvCs2 = 4f;

    static G7 ComputeGeq(float T, float ux, float uy, float uz)
    {
        const float W0 = 0.25f, W1 = 0.125f;
        float w1T = W1 * T;
        return new G7(W0 * T, w1T * (1f + GInvCs2 * ux), w1T * (1f - GInvCs2 * ux), w1T * (1f + GInvCs2 * uy), w1T * (1f - GInvCs2 * uy),
            w1T * (1f + GInvCs2 * uz), w1T * (1f - GInvCs2 * uz));
    }

    static float SumG(in G7 g) => g.G0 + g.G1 + g.G2 + g.G3 + g.G4 + g.G5 + g.G6;

    // Magic parameter Lambda = (tauOdd - 1/2)(tauEven - 1/2) of the two-relaxation-time (TRT) collision, Ginzburg 2005 (Adv.
    // Water Resour. 28:1171) and Servan-Camas & Tsai 2008 (31:1113). 0 = plain BGK (one tau for every population),
    // which is what ships. Lambda = 1/4 was implemented in this one function (so CPU and GPU share it) and measured on
    // 2026-10-09 NOT to help: on the heat scene it had negative g populations at floor 0.55 (71 k), diverged at floors
    // 0.52 and 0.7, and failed 19d at 0.52; Lambda = 1/6 diverged at 0.55; Lambda = 1/12 was stable at 0.55 but had
    // negative g (193) and failed 19d at 0.52. BGK stays stable down to 0.53 (see TauGMin), so TRT is switched off rather
    // than deleted.
    const float GMagicLambda = 0f;

    /// <summary>Relaxation toward geq (BGK, or TRT when <see cref="GMagicLambda"/> is set), plus a volumetric
    /// source spread across all 7 populations by weight (so the population sum -- macroscopic T -- rises by
    /// exactly <paramref name="heatDeltaT"/> this step). The odd (flux) part relaxes with tauG, which sets the
    /// diffusivity alpha = c_s^2 (tauG - 1/2); the even part relaxes with the TRT partner of tauG.</summary>
    static G7 CollideG(in G7 gin, float T, float ux, float uy, float uz, float tauG, float heatDeltaT)
    {
        G7 geq = ComputeGeq(T, ux, uy, uz);
        float wo = 1f / tauG;
        float we = GMagicLambda > 0f ? 1f / (0.5f + GMagicLambda / (tauG - 0.5f)) : wo;
        const float W0 = 0.25f, W1 = 0.125f;
        float d0 = gin.G0 - geq.G0;
        float e1 = gin.G1 - geq.G1, e2 = gin.G2 - geq.G2, e3 = gin.G3 - geq.G3, e4 = gin.G4 - geq.G4, e5 = gin.G5 - geq.G5, e6 = gin.G6 - geq.G6;
        float p1 = 0.5f * (e1 + e2), m1 = 0.5f * (e1 - e2), p3 = 0.5f * (e3 + e4), m3 = 0.5f * (e3 - e4), p5 = 0.5f * (e5 + e6), m5 = 0.5f * (e5 - e6);
        return new G7(
            gin.G0 - d0 * we + W0 * heatDeltaT,
            gin.G1 - we * p1 - wo * m1 + W1 * heatDeltaT,
            gin.G2 - we * p1 + wo * m1 + W1 * heatDeltaT,
            gin.G3 - we * p3 - wo * m3 + W1 * heatDeltaT,
            gin.G4 - we * p3 + wo * m3 + W1 * heatDeltaT,
            gin.G5 - we * p5 - wo * m5 + W1 * heatDeltaT,
            gin.G6 - we * p5 + wo * m5 + W1 * heatDeltaT);
    }

    /// <summary>Regularized-BGK D3Q19 collision plus Guo, Zheng & Shi (2002) forcing: u picks up the
    /// standard half-force correction, and a forcing term S_i is added post-collision. Otherwise identical
    /// to <see cref="Collide"/> (same Smagorinsky tau, same grouping) -- kept separate so the non-thermal
    /// hot path (<see cref="Collide"/>, <see cref="RunStep"/>, <see cref="StepKernel"/>) is untouched.
    /// rho is taken as ~1 in the force itself (weakly compressible LBM, standard Boussinesq practice).</summary>
    static void CollideForced(in F19 fin, float tau0, float fx, float fy, float fz,
        out F19 fout, out float rho, out float ux, out float uy, out float uz, out float tau)
    {
        float f0 = fin.F0, f1 = fin.F1, f2 = fin.F2, f3 = fin.F3, f4 = fin.F4, f5 = fin.F5, f6 = fin.F6,
              f7 = fin.F7, f8 = fin.F8, f9 = fin.F9, f10 = fin.F10, f11 = fin.F11, f12 = fin.F12, f13 = fin.F13,
              f14 = fin.F14, f15 = fin.F15, f16 = fin.F16, f17 = fin.F17, f18 = fin.F18;

        rho = f0 + f1 + f2 + f3 + f4 + f5 + f6 + f7 + f8 + f9 + f10 + f11 + f12 + f13 + f14 + f15 + f16 + f17 + f18;
        float invRho = 1f / rho;
        float mx = f1 - f2 + f7 - f8 + f9 - f10 + f11 - f12 + f13 - f14;
        float my = f3 - f4 + f7 - f8 - f9 + f10 + f15 - f16 + f17 - f18;
        float mz = f5 - f6 + f11 - f12 - f13 + f14 + f15 - f16 - f17 + f18;
        ux = (mx + 0.5f * fx) * invRho; uy = (my + 0.5f * fy) * invRho; uz = (mz + 0.5f * fz) * invRho;

        F19 feq = ComputeFeq(rho, ux, uy, uz);

        float pixx = (f1 - feq.F1) + (f2 - feq.F2) + (f7 - feq.F7) + (f8 - feq.F8) + (f9 - feq.F9) + (f10 - feq.F10) + (f11 - feq.F11) + (f12 - feq.F12) + (f13 - feq.F13) + (f14 - feq.F14);
        float piyy = (f3 - feq.F3) + (f4 - feq.F4) + (f7 - feq.F7) + (f8 - feq.F8) + (f9 - feq.F9) + (f10 - feq.F10) + (f15 - feq.F15) + (f16 - feq.F16) + (f17 - feq.F17) + (f18 - feq.F18);
        float pizz = (f5 - feq.F5) + (f6 - feq.F6) + (f11 - feq.F11) + (f12 - feq.F12) + (f13 - feq.F13) + (f14 - feq.F14) + (f15 - feq.F15) + (f16 - feq.F16) + (f17 - feq.F17) + (f18 - feq.F18);
        float pixy = (f7 - feq.F7) + (f8 - feq.F8) - (f9 - feq.F9) - (f10 - feq.F10);
        float pixz = (f11 - feq.F11) + (f12 - feq.F12) - (f13 - feq.F13) - (f14 - feq.F14);
        float piyz = (f15 - feq.F15) + (f16 - feq.F16) - (f17 - feq.F17) - (f18 - feq.F18);

        float qMag = MathF.Sqrt(pixx * pixx + piyy * piyy + pizz * pizz + 2f * (pixy * pixy + pixz * pixz + piyz * piyz));
        tau = 0.5f * (tau0 + MathF.Sqrt(tau0 * tau0 + 18f * Sqrt2 * Cs * Cs * qMag * invRho));
        float relax = 1f - 1f / tau;
        float trace3 = (pixx + piyy + pizz) / 3f;

        const float W0 = 1f / 3, W1 = 1f / 18, W2 = 1f / 36;
        float neq0 = relax * 4.5f * W0 * -trace3;
        float neqX = relax * 4.5f * W1 * (pixx - trace3);
        float neqY = relax * 4.5f * W1 * (piyy - trace3);
        float neqZ = relax * 4.5f * W1 * (pizz - trace3);
        float neqXYp = relax * 4.5f * W2 * (pixx + piyy + 2f * pixy - trace3);
        float neqXYm = relax * 4.5f * W2 * (pixx + piyy - 2f * pixy - trace3);
        float neqXZp = relax * 4.5f * W2 * (pixx + pizz + 2f * pixz - trace3);
        float neqXZm = relax * 4.5f * W2 * (pixx + pizz - 2f * pixz - trace3);
        float neqYZp = relax * 4.5f * W2 * (piyy + pizz + 2f * piyz - trace3);
        float neqYZm = relax * 4.5f * W2 * (piyy + pizz - 2f * piyz - trace3);

        // Guo forcing S_i = (1 - 1/(2 tau)) w_i [3 c.F + 9 (c.u)(c.F) - 3 u.F]: same +/- pair grouping as
        // ComputeFeq (an "even" part shared by a direction and its opposite, an "odd" part that flips sign).
        //
        // The ODD part -- the only piece that carries momentum -- gets 1/2, NOT Guo's (1 - 1/(2 tau)).
        // Guo's coefficient is derived for the plain BGK collision f_post = f - (f - feq)/tau + S, whose
        // first moment is  m - (1/tau)(m - rho*u) + sw*F = m + F/(2 tau) + (1 - 1/(2 tau))F = m + F: the
        // F/(2 tau) comes from relaxing the (m - rho*u) = -F/2 first moment of f_neq. This collision is
        // REGULARIZED (Latt & Chopard): f_post = feq(rho,u) + relax*P2(Pi_neq) + S rebuilds f_neq from the
        // second moment only, and P2 is symmetric in +/-c, so its first moment is exactly 0 -- the
        // F/(2 tau) contribution is discarded. With Guo's sw on the odd part the delivered momentum is
        // therefore rho*u + sw*F = m + F*(3/2 - 1/(2 tau)), i.e. F*0.503 at the tau0 ~ 0.5015 of a
        // building-scale room (and 1.5 F as tau -> inf). Coefficient 1/2 restores m_post = m + F exactly
        // at every tau: rho*u already carries the +F/2 of the velocity shift, so the source only has to
        // supply the other half. The even part keeps sw -- it contributes only to the second moment
        // (sum c.c.S = sw(u F + F u)), which is where Guo's tau-dependence genuinely belongs, and the odd
        // part contributes nothing there (third-order odd moment of symmetric weights = 0), nor to mass
        // (sum S_odd = 0). Measured: the vertical hydrostatic gradient of a sealed still room with air 10 K
        // above the reference was 0.5032 x rho*g*beta*dT before this change (the predicted
        // 3/2 - 1/(2*0.5015) = 0.50299) and matches to <1% after it.
        float sw = 1f - 1f / (2f * tau);
        const float swOdd = 0.5f;
        float uF = ux * fx + uy * fy + uz * fz;
        float cEvenX = W1 * (9f * ux * fx - 3f * uF), cOddX = W1 * 3f * fx;
        float cEvenY = W1 * (9f * uy * fy - 3f * uF), cOddY = W1 * 3f * fy;
        float cEvenZ = W1 * (9f * uz * fz - 3f * uF), cOddZ = W1 * 3f * fz;
        float uxy = ux + uy, dxy = ux - uy, fxy = fx + fy, dfxy = fx - fy;
        float uxz = ux + uz, dxz = ux - uz, fxz = fx + fz, dfxz = fx - fz;
        float uyz = uy + uz, dyz = uy - uz, fyz = fy + fz, dfyz = fy - fz;
        float cEvenXYp = W2 * (9f * uxy * fxy - 3f * uF), cOddXYp = W2 * 3f * fxy;
        float cEvenXYm = W2 * (9f * dxy * dfxy - 3f * uF), cOddXYm = W2 * 3f * dfxy;
        float cEvenXZp = W2 * (9f * uxz * fxz - 3f * uF), cOddXZp = W2 * 3f * fxz;
        float cEvenXZm = W2 * (9f * dxz * dfxz - 3f * uF), cOddXZm = W2 * 3f * dfxz;
        float cEvenYZp = W2 * (9f * uyz * fyz - 3f * uF), cOddYZp = W2 * 3f * fyz;
        float cEvenYZm = W2 * (9f * dyz * dfyz - 3f * uF), cOddYZm = W2 * 3f * dfyz;
        float s0 = sw * W0 * -3f * uF;

        fout = new F19(
            feq.F0 + neq0 + s0,
            feq.F1 + neqX + sw * cEvenX + swOdd * cOddX, feq.F2 + neqX + sw * cEvenX - swOdd * cOddX,
            feq.F3 + neqY + sw * cEvenY + swOdd * cOddY, feq.F4 + neqY + sw * cEvenY - swOdd * cOddY,
            feq.F5 + neqZ + sw * cEvenZ + swOdd * cOddZ, feq.F6 + neqZ + sw * cEvenZ - swOdd * cOddZ,
            feq.F7 + neqXYp + sw * cEvenXYp + swOdd * cOddXYp, feq.F8 + neqXYp + sw * cEvenXYp - swOdd * cOddXYp,
            feq.F9 + neqXYm + sw * cEvenXYm + swOdd * cOddXYm, feq.F10 + neqXYm + sw * cEvenXYm - swOdd * cOddXYm,
            feq.F11 + neqXZp + sw * cEvenXZp + swOdd * cOddXZp, feq.F12 + neqXZp + sw * cEvenXZp - swOdd * cOddXZp,
            feq.F13 + neqXZm + sw * cEvenXZm + swOdd * cOddXZm, feq.F14 + neqXZm + sw * cEvenXZm - swOdd * cOddXZm,
            feq.F15 + neqYZp + sw * cEvenYZp + swOdd * cOddYZp, feq.F16 + neqYZp + sw * cEvenYZp - swOdd * cOddYZp,
            feq.F17 + neqYZm + sw * cEvenYZm + swOdd * cOddYZm, feq.F18 + neqYZm + sw * cEvenYZm - swOdd * cOddYZm);
    }

    static void WriteAoSG(float[] arr, int i, in G7 v)
    {
        int b = i * Q7;
        arr[b] = v.G0; arr[b + 1] = v.G1; arr[b + 2] = v.G2; arr[b + 3] = v.G3; arr[b + 4] = v.G4; arr[b + 5] = v.G5; arr[b + 6] = v.G6;
    }

    static G7 GatherPlainG(float[] curG, int i, int o1, int o2, int o3, int o4, int o5, int o6) => new(
        curG[i * Q7], curG[(i + o1) * Q7 + 1], curG[(i + o2) * Q7 + 2], curG[(i + o3) * Q7 + 3],
        curG[(i + o4) * Q7 + 4], curG[(i + o5) * Q7 + 5], curG[(i + o6) * Q7 + 6]);

    /// <summary>Weight of D3Q7 direction q (0.25 rest, 0.125 the six face links) -- the anti-bounce-back coefficient below.</summary>
    static float GWeight(int q) => q == 0 ? 0.25f : 0.125f;

    /// <summary>Walls are adiabatic (plain bounce-back). <paramref name="faces"/>: the Resolved Surface faces of this fluid cell
    /// (<see cref="SurfaceExchange.Resolved"/>; 0 under Correlation, whose exchange is a source, <see cref="SurfaceConvection"/>); bit b set =
    /// the face toward +x,-x,+y,-y,+z,-z (b = 0..5) is a Dirichlet wall at <paramref name="tWall"/>, done with anti-bounce-back (Ginzburg):
    /// g_q(fluid) = -g_opp(fluid) + 2*w_q*T_wall, the wall halfway between the cell centres as for the momentum wall (P14-02). The same holds on a
    /// cut link (RY-07: a wall thinner than a cell).</param>
    static G7 GatherBoundaryG(VoxelGrid grid, float[] curG, byte[] cell, byte[] flags, int x, int y, int z, int i, int faces, float tWall)
    {
        int Nz = grid.Nz;
        Span<float> g = stackalloc float[Q7];
        for (int q = 0; q < Q7; q++)
        {
            int sz = z - Gcz[q]; int sx = x - Gcx[q], sy = y - Gcy[q];
            if (sz < 0 || sz >= Nz)
            {
                int sIdx = grid.Index(sx, sy, z);
                g[q] = cell[sIdx] != VoxelGrid.Solid ? curG[sIdx * Q7 + GMirrorZ[q]]
                     : (faces >> (GOpp[q] - 1) & 1) != 0 ? 2f * GWeight(q) * tWall - curG[i * Q7 + GOpp[q]] : curG[i * Q7 + GOpp[q]];
            }
            else
            {
                int sIdx = grid.Index(sx, sy, sz);
                // 6a: D3Q7 directions 1..6 are the six D3Q19 axis links; a cut one bounces back (adiabatic unless a Resolved Surface face)
                g[q] = cell[sIdx] != VoxelGrid.Solid
                     ? (q > 0 && LinkCut(flags, grid.Count, i, q)
                         ? (faces >> (GOpp[q] - 1) & 1) != 0 ? 2f * GWeight(q) * tWall - curG[i * Q7 + GOpp[q]] : curG[i * Q7 + GOpp[q]]
                         : curG[sIdx * Q7 + q])
                     : (faces >> (GOpp[q] - 1) & 1) != 0 ? 2f * GWeight(q) * tWall - curG[i * Q7 + GOpp[q]] : curG[i * Q7 + GOpp[q]];
            }
        }
        return new G7(g[0], g[1], g[2], g[3], g[4], g[5], g[6]);
    }

    /// <summary>Far-field T ring: Dirichlet Env.OutdoorTemperature on inflow faces (using that face's ramped-
    /// off wind velocity for the equilibrium) AND on open faces (<see cref="BcOpen"/> -- the whole ring on a
    /// zero-wind thermal run, e.g. the sealed "heat"/"stack" scenes: with no ambient wind there is no
    /// meaningful outflow direction to justify a zero-gradient/Neumann condition, and leaving the *entire*
    /// boundary floating gives a closed system no fixed reference at all -- any numerical drift in the bulk
    /// then has nothing to relax back to and can wander/amplify over the thousands of steps a buoyancy run
    /// needs. Physically the open ring is just far-field outdoor air, uniform and quiescent, regardless of
    /// local flow direction right at that cell). Zero-gradient (copy the inward neighbour's T) remains on
    /// outflow and tangential faces, where the ring genuinely reflects air that just left the domain. Only
    /// the ring portion of <paramref name="bl"/> (indices [0, n-FixedCount)) -- Fixed (mechanical) cells are
    /// left at their initial T, see <see cref="BuildThermal"/>.</summary>
    static void SetBoundaryThermalRingCpu(float[] curG, BoundaryList bl, float tOut)
    {
        int n = bl.Index.Length;
        int ringCount = n - bl.FixedCount;
        Parallel.For(0, ringCount, k =>
        {
            int idx = bl.Index[k]; byte cls = bl.Class[k];
            G7 geq;
            if (IsInflow(cls))
                geq = ComputeGeq(tOut, bl.Vel[k], bl.Vel[n + k], bl.Vel[2 * n + k]);
            else if (cls == BcOpen)
                geq = ComputeGeq(tOut, 0f, 0f, 0f);
            else
            {
                int b = bl.NeighborSlot[k] * Q7;
                float tN = curG[b] + curG[b + 1] + curG[b + 2] + curG[b + 3] + curG[b + 4] + curG[b + 5] + curG[b + 6];
                geq = ComputeGeq(tN, 0f, 0f, 0f);
            }
            WriteAoSG(curG, idx, geq);
        });
    }

    static int[] EvenSteps(int start, int end, int n)
    {
        if (n <= 0 || end <= start + 1) return [];
        var arr = new int[n];
        for (int k = 0; k < n; k++) arr[k] = start + (int)((long)(end - start - 1) * k / Math.Max(1, n - 1));
        return arr;
    }

    /// <summary>Snapshot at the current (post-swap) state. ponytail: 5M cells x 4 arrays x 2 bytes (System.Half)
    /// = 40 MB per velocity snapshot (+13 MB more when thermal); SnapshotCount is the caller's memory
    /// budget knob.</summary>
    static Snapshot CaptureCpu(VoxelGrid grid, float[] cur, float velScale, float[]? curG, float time, float[]? tHyb = null)
    {
        int count = grid.Count;
        var ux = new System.Half[count]; var uy = new System.Half[count]; var uz = new System.Half[count];
        System.Half[] t = curG != null || tHyb != null ? new System.Half[count] : [];
        Parallel.For(0, count, i =>
        {
            if (grid.Cell[i] == VoxelGrid.Solid) return;
            NeighborMacroscopic(cur, i, out float vx, out float vy, out float vz);
            ux[i] = (System.Half)(vx * velScale); uy[i] = (System.Half)(vy * velScale); uz[i] = (System.Half)(vz * velScale);
            if (tHyb != null) t[i] = (System.Half)tHyb[i];
            else if (curG != null)
            {
                int b = i * Q7;
                t[i] = (System.Half)(curG[b] + curG[b + 1] + curG[b + 2] + curG[b + 3] + curG[b + 4] + curG[b + 5] + curG[b + 6]);
            }
        });
        return new Snapshot { Time = time, Ux = ux, Uy = uy, Uz = uz, T = t };
    }

    /// <summary>Box -> Air-cell-index list for a HeatSource: every Air cell whose centre falls inside the
    /// (Center, Width x Depth x Height) box.</summary>
    static List<int> BoxCells(VoxelGrid grid, Vector3 center, float w, float d, float h)
    {
        var list = new List<int>();
        var lo = center - new Vector3(w, d, h) * 0.5f;
        var hi = center + new Vector3(w, d, h) * 0.5f;
        int x0 = Math.Max(0, (int)((lo.X - grid.Origin.X) / grid.Dx)), x1 = Math.Min(grid.Nx - 1, (int)((hi.X - grid.Origin.X) / grid.Dx) + 1);
        int y0 = Math.Max(0, (int)((lo.Y - grid.Origin.Y) / grid.Dx)), y1 = Math.Min(grid.Ny - 1, (int)((hi.Y - grid.Origin.Y) / grid.Dx) + 1);
        int z0 = Math.Max(0, (int)((lo.Z - grid.Origin.Z) / grid.Dx)), z1 = Math.Min(grid.Nz - 1, (int)((hi.Z - grid.Origin.Z) / grid.Dx) + 1);
        for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int idx = grid.Index(x, y, z);
                    if (grid.Cell[idx] != VoxelGrid.Air) continue;
                    var c = grid.CellCenter(x, y, z);
                    if (c.X >= lo.X && c.X <= hi.X && c.Y >= lo.Y && c.Y <= hi.Y && c.Z >= lo.Z && c.Z <= hi.Z) list.Add(idx);
                }
        return list;
    }

    /// <summary>Surface cell code (BuildThermal's SurfaceFixed): bits 0-5 the faces, bit 6 set = Resolved (a Dirichlet wall in the D3Q7 gather /
    /// Hybrid diffusion; no convection source), the source's index into SurfaceCoef from bit 7.</summary>
    const int SurfaceResolvedBit = 64, SurfaceSourceShift = 7;

    /// <summary>Floats per Surface table entry: [aV, aU, cS] of the cell's first group, then (RT-03) up to two more groups, each [face mask, T_s, aV, aU, cS]
    /// (mask 0 = none), for a cell whose faces belong to several Surface sources (a corner of two boxes). A source's own entry has no extra group.</summary>
    const int SurfaceStride = 13;

    static bool BoxHasSolid(VoxelGrid grid, Vector3 lo, Vector3 hi)
    {
        int x0 = Math.Max(0, (int)((lo.X - grid.Origin.X) / grid.Dx)), x1 = Math.Min(grid.Nx - 1, (int)((hi.X - grid.Origin.X) / grid.Dx) + 1);
        int y0 = Math.Max(0, (int)((lo.Y - grid.Origin.Y) / grid.Dx)), y1 = Math.Min(grid.Ny - 1, (int)((hi.Y - grid.Origin.Y) / grid.Dx) + 1);
        int z0 = Math.Max(0, (int)((lo.Z - grid.Origin.Z) / grid.Dx)), z1 = Math.Min(grid.Nz - 1, (int)((hi.Z - grid.Origin.Z) / grid.Dx) + 1);
        for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    if (grid.Cell[grid.Index(x, y, z)] != VoxelGrid.Solid) continue;
                    var c = grid.CellCenter(x, y, z);
                    if (c.X >= lo.X && c.X <= hi.X && c.Y >= lo.Y && c.Y <= hi.Y && c.Z >= lo.Z && c.Z <= hi.Z) return true;
                }
        return false;
    }

    /// <summary>Bit b (b = 0..5: +x, -x, +y, -y, +z, -z) is set when that face of the LBM-fluid cell <paramref name="idx"/> is a face a
    /// Surface HeatSource with the box [lo, hi] holds: (a) the neighbour is Solid and its centre lies inside the box (P14-02), or (b, RY-07)
    /// the axis link to the neighbour is cut (a wall or slab thinner than a cell, 6a) and the crossing point lies inside the box.
    /// <paramref name="sideSpecific"/> (RT-02 / RU-06, Correlation): a face counts when the box contains the face's wall-surface point, the crossing the voxeliser
    /// recorded for the link (the true surface, whatever the grid; the midpoint between the two centres only where none is recorded), under one half-open rule
    /// (<see cref="FaceInBox"/>: the box must cross the surface, and across the face the cell centre is lo &lt;= p &lt; hi), so a wall is held only on the side the box
    /// was drawn on and the held area does not depend on the grid offset. The Solid cell centre no longer has to be in the box. Resolved keeps the pre-6e rule (the solid centre in the box, P14-02).
    /// <paramref name="solidFaces"/> / <paramref name="cutFaces"/>: the faces that are a wall of either kind at all, inside the box or not.</summary>
    static int SurfaceFaceMask(VoxelGrid grid, int idx, Vector3 lo, Vector3 hi, bool sideSpecific, out int solidFaces, out int cutFaces)
    {
        int x = idx % grid.Nx, y = idx / grid.Nx % grid.Ny, z = idx / (grid.Nx * grid.Ny), mask = 0;
        bool links = grid.WallLinks.Length == Q * grid.Count;
        var a = grid.CellCenter(x, y, z);
        solidFaces = 0; cutFaces = 0;
        for (int b = 0; b < 6; b++)
        {
            int p = b + 1, sx = x + Gcx[p], sy = y + Gcy[p], sz = z + Gcz[p];
            if (sx < 0 || sx >= grid.Nx || sy < 0 || sy >= grid.Ny || sz < 0 || sz >= grid.Nz) continue;
            Vector3 c;
            // the link this cell pulls from the neighbour (q = opp(p)) carries the crossing as a fraction of Dx from this cell's centre
            int code = links ? grid.WallLinks[GOpp[p] * grid.Count + idx] : 0;
            if (grid.Cell[grid.Index(sx, sy, sz)] == VoxelGrid.Solid)
            {
                c = grid.CellCenter(sx, sy, sz); solidFaces |= 1 << b;
                if (sideSpecific)   // RU-06: the wall surface point on this side, tested half-open
                {
                    var surface = code != 0 ? a + new Vector3(Gcx[p], Gcy[p], Gcz[p]) * ((code - 1) / 254f * grid.Dx) : (a + c) * 0.5f;
                    if (FaceInBox(surface, b >> 1, lo, hi)) mask |= 1 << b;
                    continue;
                }
            }
            else
            {
                if (code == 0) continue;
                c = a + new Vector3(Gcx[p], Gcy[p], Gcz[p]) * ((code - 1) / 254f * grid.Dx);
                cutFaces |= 1 << b;
                if (sideSpecific) { if (FaceInBox(c, b >> 1, lo, hi)) mask |= 1 << b; continue; }
            }
            if (InBox(c, lo, hi)) mask |= 1 << b;
        }
        return mask;
    }

    static bool InBox(Vector3 c, Vector3 lo, Vector3 hi) => c.X >= lo.X && c.X <= hi.X && c.Y >= lo.Y && c.Y <= hi.Y && c.Z >= lo.Z && c.Z <= hi.Z;

    /// <summary>RU-06: a face's wall-surface point <paramref name="p"/> (normal along <paramref name="axis"/>: 0 x, 1 y, 2 z) is in the box when the box CROSSES the surface
    /// (lo &lt; p &lt; hi along the normal: a box that only touches a wall holds nothing of it, nor of the perpendicular walls it stands flush against) and, across the face,
    /// lo &lt;= p &lt; hi (half-open: a cell centre exactly on a shared box plane belongs to one box, and a box N cells across holds N, whatever the grid offset).</summary>
    static bool FaceInBox(Vector3 p, int axis, Vector3 lo, Vector3 hi)
    {
        bool X = axis == 0 ? p.X > lo.X && p.X < hi.X : p.X >= lo.X && p.X < hi.X;
        bool Y = axis == 1 ? p.Y > lo.Y && p.Y < hi.Y : p.Y >= lo.Y && p.Y < hi.Y;
        bool Z = axis == 2 ? p.Z > lo.Z && p.Z < hi.Z : p.Z >= lo.Z && p.Z < hi.Z;
        return X && Y && Z;
    }

    // ---- 6e (P14-03): Surface HeatSource faces exchange heat by a convection correlation, applied as a source in the fluid cell ----
    // h = (h_n^3 + h_f^3)^(1/3), the mixed-convection blend of Beausoleil-Morrison (2000) as documented in the EnergyPlus Engineering
    // Reference (23.2, Inside Heat Balance: "Beausoleil-Morrison Mixed Assisted Wall"). h_n: Alamdari and Hammond (1983), the coefficients
    // as given there ("Alamdari Hammond Vertical Wall / Unstable Horizontal / Stable Horizontal"; checked 2026-10-13, they match):
    //   vertical            h_n = {[1.5 (|dT|/H)^(1/4)]^6 + [1.23 |dT|^(1/3)]^6}^(1/6)
    //   buoyant horizontal  h_n = {[1.4 (|dT|/D)^(1/4)]^6 + [1.63 |dT|^(1/3)]^6}^(1/6)   (hot floor, cold ceiling)
    //   stable horizontal   h_n = 0.6 (|dT|/D^2)^(1/5)                                      (hot ceiling, cold floor)
    // with H the Surface box height and D = 4A/P of its plan (EnergyPlus takes the zone's ceiling height and floor). h_f = 3.8 |u_t|, the
    // velocity term of McAdams (1954) h = 5.7 + 3.8 V as cast in SI by Palyvos (2008) (EnergyPlus Engineering Reference, Outside Surface
    // Heat Balance, "McAdams"); its constant is natural convection, which h_n supplies here. u_t = the first fluid cell's speed along the face.
    const float ForcedConvectionPerSpeed = 3.8f;   // W/(m2 K) per m/s
    const float AhVert6 = 1.23f * 1.23f * 1.23f * 1.23f * 1.23f * 1.23f, AhUnstable6 = 1.63f * 1.63f * 1.63f * 1.63f * 1.63f * 1.63f;

    /// <summary>x^(1/n) for x &gt;= 0 (0 otherwise): an exponent-halving bit seed refined by Newton steps. Kernels have no MathF.Pow here,
    /// so the CPU uses the same routine and both backends round alike.</summary>
    static float RootN(float x, int n)
    {
        if (!(x > 0f)) return 0f;
        int bits = unchecked((int)Interop.FloatAsInt(x));
        float y = Interop.IntAsFloat(unchecked((uint)((bits - 0x3F800000) / n + 0x3F800000)));
        for (int k = 0; k < 5; k++)
        {
            float p = y;
            for (int j = 2; j < n; j++) p *= y;   // y^(n-1)
            y = ((n - 1) * y + x / p) / n;
        }
        return y;
    }

    /// <summary>Per-source constants of the natural-convection correlations: [aV, aU, cS] with h_n(vertical) = (aV |dT|^1.5 + 1.23^6 dT^2)^(1/6),
    /// h_n(buoyant horizontal) = (aU |dT|^1.5 + 1.63^6 dT^2)^(1/6), h_n(stable) = cS |dT|^(1/5) (see the section comment).</summary>
    static (float AV, float AU, float CS) SurfaceCoefficients(HeatSource h, float dx)
    {
        float height = MathF.Max(h.Height, dx), w = MathF.Max(h.Width, dx), d = MathF.Max(h.Depth, dx);
        float dh = MathF.Max(2f * w * d / (w + d), dx);   // 4A/P of the box plan
        return (1.5f * 1.5f * 1.5f * 1.5f * 1.5f * 1.5f / MathF.Pow(height, 1.5f), 1.4f * 1.4f * 1.4f * 1.4f * 1.4f * 1.4f / MathF.Pow(dh, 1.5f),
                0.6f / MathF.Pow(dh, 0.4f));
    }

    /// <summary>Convective coefficient, W/(m2 K), of one Surface face. <paramref name="dT"/> = T_surface - T_air (the first fluid cell);
    /// <paramref name="orient"/> 0 = vertical face, 1 = the surface is above the cell (ceiling), 2 = below it (floor); <paramref name="uTan"/>
    /// the cell's speed along the face, m/s.</summary>
    static float SurfaceH(float dT, int orient, float uTan, float aV, float aU, float cS)
    {
        float d = MathF.Abs(dT), hn;
        if (orient == 0) hn = RootN(aV * d * MathF.Sqrt(d) + AhVert6 * d * d, 6);
        else if ((orient == 2) == (dT > 0f)) hn = RootN(aU * d * MathF.Sqrt(d) + AhUnstable6 * d * d, 6);   // hot floor or cold ceiling: buoyant
        else hn = cS * RootN(d, 5);
        float hf = ForcedConvectionPerSpeed * uTan;
        return RootN(hn * hn * hn + hf * hf * hf, 3);
    }

    /// <summary>Temperature change, K per step, that the Surface faces (<paramref name="faces"/>, bits as <see cref="SurfaceFaceMask"/>) at
    /// <paramref name="tWall"/> give a fluid cell at <paramref name="T"/> with lattice velocity (ux, uy, uz): sum_faces h A (T_s - T) dt /
    /// (rho c_p V) = sum h (T_s - T) <paramref name="perStep"/> with perStep = dt / (rho c_p dx). Explicit; the factor is capped at 1 so one
    /// step never carries the cell past T_s. aV, aU, cS: the source's <see cref="SurfaceCoefficients"/>.</summary>
    static float SurfaceConvection(int faces, float tWall, float T, float ux, float uy, float uz, float velScale, float perStep,
        float aV, float aU, float cS)
    {
        float dT = tWall - T, sum = 0f;
        float ux2 = ux * ux, uy2 = uy * uy, uz2 = uz * uz;
        for (int b = 0; b < 6; b++)
        {
            if ((faces >> b & 1) == 0) continue;
            int orient = b < 4 ? 0 : b == 4 ? 1 : 2;
            float ut2 = b < 2 ? uy2 + uz2 : b < 4 ? ux2 + uz2 : ux2 + uy2;
            sum += SurfaceH(dT, orient, MathF.Sqrt(ut2) * velScale, aV, aU, cS);
        }
        return MathF.Min(sum * perStep, 1f) * dT;
    }

    /// <summary>The cell lies in the absorbing band of the ring (<see cref="SpongeAbsorb"/>'s band): the Surface and held-temperature sources
    /// (6e, 6j) do not act there.</summary>
    static bool InSponge(int x, int y, int Nx, int Ny, int faces, int cells)
    {
        int m = (faces | (faces >> 4)) & 15;
        if (m == 0 || cells <= 0) return false;
        return (m & 1) != 0 && x < cells || (m & 2) != 0 && Nx - 1 - x < cells || (m & 4) != 0 && y < cells || (m & 8) != 0 && Ny - 1 - y < cells;
    }

    /// <summary>Room.Occupants' occupied zone: the room's cells (<paramref name="mask"/>, <see cref="Analysis.RoomMasks"/>: outline plus the OF-1 band, the cells its ACH and
    /// means use), 0.1-1.8 m above ZMin.</summary>
    static List<int> RoomOccupiedCells(VoxelGrid grid, Room room, bool[] mask)
    {
        var list = new List<int>();
        if (room.Loops.Count == 0) return list;
        float zLo = room.ZMin + 0.1f, zHi = room.ZMin + 1.8f;
        int z0 = Math.Max(0, (int)((zLo - grid.Origin.Z) / grid.Dx)), z1 = Math.Min(grid.Nz - 1, (int)((zHi - grid.Origin.Z) / grid.Dx) + 1);
        int layer = grid.Nx * grid.Ny;
        for (int z = z0; z <= z1; z++)
        {
            float cz = grid.Origin.Z + (z + 0.5f) * grid.Dx;
            if (cz < zLo || cz > zHi) continue;
            for (int i = z * layer; i < (z + 1) * layer; i++)
                if (mask[i] && grid.Cell[i] == VoxelGrid.Air) list.Add(i);
        }
        return list;
    }

    /// <summary>Dense per-cell device data, SoA, empty when the grid has no devices:
    /// [0,c)=target ux, [c,2c)=uy, [2c,3c)=uz (LATTICE units), [3c,4c)=1/tau_relax (0 = not a device cell);
    /// thermal runs get two more, [4c,5c)=k1 = Q*dt/V_outlet (the per-step temperature drop per kelvin of
    /// the unit's own return-to-supply difference; 0 = this cell conditions nothing) and [5c,6c)=which
    /// conditioning unit the cell belongs to, as a float (<see cref="DeviceNoUnit"/> = none). Dense rather
    /// than a sparse list because the GPU step kernel is one thread per cell and has to answer "is this a
    /// device cell?" without a search; it costs 16 bytes/cell (24 thermal) and is only allocated when a
    /// device exists.
    /// <para>The per-unit numbers that DO change during a run live in a tiny side table
    /// (<c>Unit</c>: supply temperature per unit, then the current T_return - T_supply per unit), so a
    /// refresh uploads a handful of floats instead of the dense array. <c>UnitCells</c>/<c>UnitStart</c>
    /// are the cells whose temperature is averaged to get each unit's return temperature: the room air
    /// its return grille draws from (<see cref="DeviceRegion.GatherCells"/>), else the grille itself, or --
    /// for a unit with no return placed -- its own outlet cells, which makes the duty self-referential but
    /// keeps it bounded.</para></summary>
    static (float[] Data, int[] UnitCells, int[] UnitStart, float[] Unit) BuildDeviceData(
        VoxelGrid grid, float latPerPhys, float dtSeconds, bool thermal, List<(int Cell, float C, float Nx, float Ny)>? openingLoss = null,
        List<(int Cell, float W)>? plume = null)
    {
        if (grid.Devices.Count == 0 && (openingLoss == null || openingLoss.Count == 0) && (plume == null || plume.Count == 0)) return ([], [], [], []);
        int count = grid.Count, slots = thermal ? 6 : 4;
        var data = new float[count * slots];
        if (thermal) Array.Fill(data, DeviceNoUnit, 5 * count, count);

        float cellVolume = grid.Dx * grid.Dx * grid.Dx;
        var unitCells = new List<int>();
        var unitStart = new List<int> { 0 };
        var supplyT = new List<float>();

        foreach (var d in grid.Devices)
        {
            bool conditions = thermal && !float.IsNaN(d.SupplyTemperature) && d.OutletCount > 0;
            int unit = -1;
            float k1 = 0f;
            if (conditions)
            {
                unit = supplyT.Count;
                supplyT.Add(d.SupplyTemperature);
                k1 = d.FlowRate * dtSeconds / MathF.Max(d.OutletCount * cellVolume, 1e-9f);
                if (d.GatherCells.Length > 0)
                    unitCells.AddRange(d.GatherCells);
                else if (d.Cells.Length > d.OutletCount)
                    for (int i = d.OutletCount; i < d.Cells.Length; i++) unitCells.Add(d.Cells[i]);
                else
                    for (int i = 0; i < d.OutletCount; i++) unitCells.Add(d.Cells[i]);
                unitStart.Add(unitCells.Count);
            }

            for (int i = 0; i < d.Cells.Length; i++)
            {
                int idx = d.Cells[i];
                var v = d.Target[i] * latPerPhys;
                data[idx] = v.X; data[count + idx] = v.Y; data[2 * count + idx] = v.Z;
                data[3 * count + idx] = 1f / DeviceRelaxSteps;
                // Only the outlet cells condition the air; the return cells take it at whatever
                // temperature the room delivers, which is what sets the duty.
                if (conditions && i < d.OutletCount)
                {
                    data[4 * count + idx] = k1;
                    data[5 * count + idx] = unit;
                }
            }
        }

        // Calibrated opening model: throat cells carry the loss as a negative rate (see DeviceForce) and the opening's
        // horizontal unit normal in the target slots. A cell a device already drives keeps the device.
        if (openingLoss != null)
            foreach (var (idx, c, nx, ny) in openingLoss)
                if (data[3 * count + idx] == 0f)
                {
                    data[3 * count + idx] = -c;
                    data[idx] = nx; data[count + idx] = ny; data[2 * count + idx] = 0f;
                }

        // Plume model: the cells above a sub-grid heat source are driven toward the plume's vertical velocity (same device force, mass neutral).
        if (plume != null)
            foreach (var (idx, w) in plume)
                if (data[3 * count + idx] == 0f)
                {
                    data[3 * count + idx] = 1f / DeviceRelaxSteps;
                    data[2 * count + idx] = w * latPerPhys;
                }

        var unitTable = new float[2 * supplyT.Count];       // [0,n) = supply T, [n,2n) = T_return - T_supply
        for (int u = 0; u < supplyT.Count; u++) unitTable[u] = supplyT[u];
        return (data, unitCells.ToArray(), unitStart.ToArray(), unitTable);
    }

    /// <summary>Initial T (indoor inside the building's plan footprint and storey height range, outdoor
    /// elsewhere), per-cell volumetric heat rate (K/s, from HeatSource People/Equipment/Lighting boxes and
    /// Room.Occupants), and Surface HeatSource cells: fluid cells with a 6-bit mask of their Surface faces (<see cref="SurfaceFaceMask"/>) in
    /// bits 0-5, <see cref="SurfaceResolvedBit"/> when <paramref name="resolved"/>, and the entry's index into SurfaceCoef (<see cref="SurfaceStride"/> floats each,
    /// <see cref="SurfaceCoefficients"/>) from bit 7; 0 = not a Surface cell. Resolved keeps the pre-6e face rule for Solid faces (only box cells,
    /// P14-02) and adds the RY-07 cut faces. UnheldSurfaces: Surface sources that hold no face at all (RX-04); each gets a grid warning and is
    /// left out of the dT scaling.</summary>
    static (float[] T0, float[] HeatRate, int[] SurfaceFixed, float[] SurfaceTemp, bool[] IsIndoor, HashSet<HeatSource> UnheldSurfaces, float[] SurfaceCoef) BuildThermal(VoxelGrid grid, BuildingModel model, bool resolved)
    {
        int count = grid.Count;
        var T0 = new float[count]; var heatRate = new float[count];
        var surfFixed = new int[count]; var surfTemp = new float[count]; var isIndoor = new bool[count];
        var unheld = new HashSet<HeatSource>();
        var surfCoef = new List<float>();
        var groups = new Dictionary<int, List<(int Src, int Mask, float T)>>();   // RT-03: cells claimed by more than one Surface source (Correlation)
        var surfacesHeld = new List<(HeatSource H, int Src, float T)>();           // RU-07: the Surface sources that held a face when their turn came
        var plumes = PlumeModel ? PlumeSources(grid, model) : null;                // opt-in sub-grid plume model (LbmPlume.cs)

        var (bMin, bMax) = Voxelizer.PlanBounds(model);
        float zLo = model.Storeys.Count > 0 ? model.Storeys.Min(s => s.Elevation) : model.GroundZ;
        float zHi = model.Storeys.Count > 0 ? model.Storeys.Max(s => s.Elevation + s.Height) : model.GroundZ + 3f;
        float tOut = model.Env.OutdoorTemperature, tIn = model.Env.IndoorTemperature;

        Parallel.For(0, count, i =>
        {
            if (grid.Cell[i] == VoxelGrid.Solid) return;
            int x = i % grid.Nx, y = i / grid.Nx % grid.Ny, z = i / (grid.Nx * grid.Ny);
            var c = grid.CellCenter(x, y, z);
            bool inside = c.X >= bMin.X && c.X <= bMax.X && c.Y >= bMin.Y && c.Y <= bMax.Y && c.Z >= zLo && c.Z <= zHi;
            T0[i] = inside ? tIn : tOut;
            isIndoor[i] = inside;
        });

        foreach (var h in model.Heat)
        {
            if (!h.Enabled) continue;
            var cells = BoxCells(grid, h.Center, h.Width, h.Depth, h.Height);
            if (cells.Count == 0 && h.Kind != HeatKind.Surface) continue;
            if (h.Kind == HeatKind.Surface)
            {
                float ts = h.SurfaceTemperature;
                if (float.IsNaN(ts)) continue;
                // P14-02 / RY-07: only the faces whose solid cell centre, or whose cut-link crossing, lies inside the box; a fluid cell that
                // also touches a wall outside the box keeps that wall adiabatic. A cell just outside the box can own a crossing inside it.
                var lo = h.Center - new Vector3(h.Width, h.Depth, h.Height) * 0.5f;
                var hi = h.Center + new Vector3(h.Width, h.Depth, h.Height) * 0.5f;
                int held = 0, solidNear = 0, cutNear = 0, src = surfCoef.Count / SurfaceStride;
                var inBox = resolved ? new HashSet<int>(cells) : null;
                foreach (var idx in BoxCells(grid, h.Center, h.Width + 2f * grid.Dx, h.Depth + 2f * grid.Dx, h.Height + 2f * grid.Dx))
                {
                    int mask = SurfaceFaceMask(grid, idx, lo, hi, !resolved, out int solidFaces, out int cutFaces);
                    solidNear |= solidFaces; cutNear |= cutFaces;
                    if (inBox != null && !inBox.Contains(idx)) mask &= ~solidFaces;   // Resolved: P14-02's Solid faces of box cells only
                    if (mask == 0) continue;
                    held++;
                    int old = surfFixed[idx];
                    if (old == 0) { surfFixed[idx] = mask | (resolved ? SurfaceResolvedBit : 0) | src << SurfaceSourceShift; surfTemp[idx] = ts; }
                    else if (resolved)
                    {
                        // Resolved: one wall temperature per cell. Equal temperatures keep both sources' faces (RT-03); different ones: the last wins, as before 6e.
                        if (surfTemp[idx] == ts) mask |= old & 63;
                        surfFixed[idx] = mask | SurfaceResolvedBit | src << SurfaceSourceShift; surfTemp[idx] = ts;
                    }
                    else
                    {
                        // RT-03: a second source on a cell keeps its own faces next to the first source's; a face both claim goes to the later source
                        if (!groups.TryGetValue(idx, out var g)) groups[idx] = g = [(old >> SurfaceSourceShift, old & 63, surfTemp[idx])];
                        for (int k = 0; k < g.Count; k++) g[k] = (g[k].Src, g[k].Mask & ~mask, g[k].T);
                        g.RemoveAll(e => e.Mask == 0);
                        g.Add((src, mask, ts));
                        surfFixed[idx] = g[0].Mask | g[0].Src << SurfaceSourceShift; surfTemp[idx] = g[0].T;
                    }
                }
                if (held > 0)
                {
                    var (aV, aU, cS) = SurfaceCoefficients(h, grid.Dx);
                    surfacesHeld.Add((h, src, ts));
                    surfCoef.Add(aV); surfCoef.Add(aU); surfCoef.Add(cS);
                    for (int k = 3; k < SurfaceStride; k++) surfCoef.Add(0f);
                }
                else
                {
                    // RX-04: a box that holds no face exchanges nothing; say so, with advice that can work for the surface beside it.
                    unheld.Add(h);
                    string note = solidNear == 0 && cutNear == 0
                        ? BoxHasSolid(grid, lo, hi)   // RT-05
                            ? $"Surface {h.Name}: its box lies inside the solid; nothing touches air, so nothing is held at {ts:0.#} °C. Extend the box to the surface the air touches."
                            : $"Surface {h.Name}: its box touches no wall, floor or ceiling, so nothing is held at {ts:0.#} °C. Draw the box across the surface it stands for."
                        : cutNear != 0
                            ? $"Surface {h.Name}: its box stops short of the surface beside it, so nothing is held at {ts:0.#} °C. That surface is thinner than a cell here: draw the box across its face."
                            : resolved
                                ? $"Surface {h.Name}: its box stops short of the wall/slab beside it, so nothing is held at {ts:0.#} °C. Extend the box into the wall/slab by up to one cell ({grid.Dx:0.##} m)."
                                : $"Surface {h.Name}: its box does not reach the wall/slab surface beside it, so nothing is held at {ts:0.#} °C. Draw the box across that surface: it must contain the surface the air touches (the nearest is within {grid.Dx:0.##} m of the box).";   // RU-06
                    if (!grid.Warnings.Contains(note)) grid.Warnings.Add(note);
                }
            }
            else
            {
                float watts = h.Kind == HeatKind.People && h.Watts <= 0f ? PeopleWatts * h.People : h.Watts;
                float vBox = cells.Count * grid.Dx * grid.Dx * grid.Dx;
                float q = vBox > 0f ? watts / (RhoCp * vBox) : 0f;
                if (plumes != null && plumes.TryGetValue(h, out var plume) && plume.Applied) { foreach (var idx in plume.Cells) heatRate[idx] += plume.Q; continue; }   // plume model: same total, released above the box
                foreach (var idx in cells) heatRate[idx] += q;
            }
        }

        // RU-07: a Surface that held faces when its turn came but lost every one to later boxes over the same wall holds nothing now: say so, and leave it out of the dT scaling
        if (!resolved && surfacesHeld.Count > 1)
        {
            var finalFaces = new int[surfCoef.Count / SurfaceStride];
            for (int idx = 0; idx < count; idx++)
            {
                if (surfFixed[idx] == 0) continue;
                if (groups.TryGetValue(idx, out var g)) foreach (var e in g) finalFaces[e.Src]++;
                else finalFaces[surfFixed[idx] >> SurfaceSourceShift]++;
            }
            foreach (var (h, src, ts) in surfacesHeld)
            {
                if (finalFaces[src] > 0) continue;
                unheld.Add(h);
                string note = $"Surface {h.Name}: the other Surface boxes over the same wall hold all of its faces, so nothing is held at {ts:0.#} °C. Remove the duplicate or draw the boxes apart.";
                if (!grid.Warnings.Contains(note)) grid.Warnings.Add(note);
            }
        }

        // RT-03: one table entry per distinct combination of sources on a cell (first group = the cell's code and surfTemp)
        var combos = new Dictionary<(int, int, int, int, int), int>();
        bool dropped = false;
        foreach (var (idx, g) in groups)
        {
            if (g.Count > 3) { g.RemoveRange(3, g.Count - 3); dropped = true; }
            surfFixed[idx] = g[0].Mask | g[0].Src << SurfaceSourceShift; surfTemp[idx] = g[0].T;
            if (g.Count == 1) continue;
            var key = (g[0].Src, g[1].Src, g.Count > 2 ? g[2].Src : -1, g[1].Mask, g.Count > 2 ? g[2].Mask : 0);
            if (!combos.TryGetValue(key, out int entry))
            {
                entry = surfCoef.Count / SurfaceStride; combos[key] = entry;
                for (int k = 0; k < 3; k++) surfCoef.Add(surfCoef[g[0].Src * SurfaceStride + k]);
                for (int j = 1; j < 3; j++)
                    if (j < g.Count) { surfCoef.Add(g[j].Mask); surfCoef.Add(g[j].T); for (int k = 0; k < 3; k++) surfCoef.Add(surfCoef[g[j].Src * SurfaceStride + k]); }
                    else for (int k = 0; k < 5; k++) surfCoef.Add(0f);
            }
            surfFixed[idx] = g[0].Mask | entry << SurfaceSourceShift;
        }
        if (dropped)
        {
            const string note = "Surface boxes: more than three share some cells; the faces of the earliest boxes are held, the rest are left adiabatic there.";
            if (!grid.Warnings.Contains(note)) grid.Warnings.Add(note);
        }

        bool[][]? roomMasks = null;   // OF-1: built on the first room with occupants
        foreach (var room in model.Rooms)
        {
            if (room.Occupants <= 0) continue;
            roomMasks ??= Analysis.RoomMasks(grid, model, out _);
            var cells = RoomOccupiedCells(grid, room, roomMasks[model.Rooms.IndexOf(room)]);
            if (cells.Count == 0) continue;
            float vZone = cells.Count * grid.Dx * grid.Dx * grid.Dx;
            float q = PeopleWatts * room.Occupants / (RhoCp * vZone);
            foreach (var idx in cells) heatRate[idx] += q;
        }

        return (T0, heatRate, surfFixed, surfTemp, isIndoor, unheld, surfCoef.ToArray());
    }

    /// <summary>One thermal LBM step over interior air cells: streams+collides both lattices together (the
    /// eddy diffusivity alpha_t needed for tau_g comes straight out of this cell's own F-collision, so both
    /// have to happen in the same pass). The per-step heat of a cell (K) is its volumetric sources, minus device cooling, plus (6e) the
    /// convection of its Surface faces (<see cref="SurfaceConvection"/>, surfaceFixed/surfaceTemp/surfaceCoef from <see cref="BuildThermal"/>,
    /// surfacePerStep = dt / (rho c_p dx), velScale m/s per lattice unit) and (6j) its room's held-temperature source (holdQ[holdRoom[i]], K per
    /// step; holdRoom empty = free-running). Neither new source acts in the absorbing band (<see cref="InSponge"/>).</summary>
    static void RunStepThermal(VoxelGrid grid, byte[] nearBoundary, float[] cur, float[] nxt, float[] curG, float[] nxtG,
        float tau0, float alphaLatMolecular, float betaB, float gLat, float tRef, float[] heatRate, int[] surfaceFixed,
        float[] surfaceTemp, float[] surfaceCoef, float surfacePerStep, float velScale, int[] holdRoom, float[] holdQ, float dtSeconds, float ramp, bool accumulate,
        double[] sumRho, double[] sumUx, double[] sumUy, double[] sumUz, double[] sumNuT, double[] sumT,
        float[]? dev, float[] devUnit, float[] instant, float[]? hyb = null, float[]? tHyb = null, int spongeFaces = 0, int spongeCells = 0)
    {
        bool hold = holdRoom.Length > 0;
        int Nx = grid.Nx, Ny = grid.Ny, Nz = grid.Nz;
        if (Ny <= 2 || Nx <= 2) return;
        var cell = grid.Cell;
        int rows = Nz * (Ny - 2);
        bool hasDev = dev is { Length: > 0 };
        bool hybrid = tHyb != null;   // Hybrid scheme (LbmHybridThermal.cs): T comes from tHyb, no D3Q7; kappa and q go to hyb for the FV pass
        int count = grid.Count;
        int nxny = Nx * Ny;
        int o1 = -1, o2 = 1, o3 = -Nx, o4 = Nx, o5 = -nxny, o6 = nxny,
            o7 = -1 - Nx, o8 = 1 + Nx, o9 = -1 + Nx, o10 = 1 - Nx,
            o11 = -1 - nxny, o12 = 1 + nxny, o13 = -1 + nxny, o14 = 1 - nxny,
            o15 = -Nx - nxny, o16 = Nx + nxny, o17 = -Nx + nxny, o18 = Nx - nxny;

        Parallel.For(0, rows, rowIdx =>
        {
            int y = 1 + rowIdx % (Ny - 2);
            int z = rowIdx / (Ny - 2);
            if ((z == 0 && grid.OpenZMin) || (z == Nz - 1 && grid.OpenZMax)) return;
            int rowBase = Nx * (y + Ny * z);

            for (int x = 1; x < Nx - 1; x++)
            {
                int i = x + rowBase;
                if (cell[i] != VoxelGrid.Air) continue;

                bool nb = (nearBoundary[i] & 1) != 0;
                int surf = surfaceFixed[i];
                F19 fin = nb ? GatherBoundary(grid, cur, cell, nearBoundary, x, y, z, i)
                             : GatherPlain(cur, i, o1, o2, o3, o4, o5, o6, o7, o8, o9, o10, o11, o12, o13, o14, o15, o16, o17, o18);
                G7 gin = default; float T;
                if (hybrid) T = tHyb![i];
                else
                {
                    gin = nb ? GatherBoundaryG(grid, curG, cell, nearBoundary, x, y, z, i, (surf & SurfaceResolvedBit) != 0 ? surf & 63 : 0, surfaceTemp[i])
                             : GatherPlainG(curG, i, o1, o2, o3, o4, o5, o6);
                    T = SumG(gin);
                }
                // Boussinesq, warm rises: +beta*(T-Tref)*g, capped -- see FLatMax.
                float Fz = MathF.Min(MathF.Max(betaB * (T - tRef) * gLat, -FLatMax), FLatMax);

                float dfx = 0f, dfy = 0f, dfz = 0f, heat = heatRate[i] * dtSeconds * ramp;
                if (hasDev && dev![3 * count + i] != 0f)
                {
                    DeviceForce(fin, dev[i], dev[count + i], dev[2 * count + i], dev[3 * count + i], out dfx, out dfy, out dfz);
                    heat -= DeviceCooling(dev[4 * count + i], dev[5 * count + i], T, devUnit, devUnit.Length / 2);
                }

                float t0 = SpongeTau(tau0, x, y, Nx, Ny, spongeFaces, spongeCells);   // 6b: the outflow sponge
                CollideForced(fin, t0, dfx, dfy, Fz + dfz, out F19 fout, out float rho, out float ux, out float uy, out float uz, out float tau);
                if (spongeFaces != 0) fout = SpongeAbsorb(fout, rho, ux, uy, uz, x, y, Nx, Ny, spongeFaces, spongeCells);   // RZ-01
                WriteAoS(nxt, i, fout);

                int conv = (surf & SurfaceResolvedBit) == 0 ? surf & 63 : 0;
                if ((conv != 0 || hold && holdRoom[i] >= 0) && !InSponge(x, y, Nx, Ny, spongeFaces, spongeCells))
                {
                    if (hold && holdRoom[i] >= 0) heat += holdQ[holdRoom[i]];   // 6j: the room's ideal heater/cooler
                    if (conv != 0)   // 6e: Surface faces by correlation
                    {
                        int c3 = SurfaceStride * (surf >> SurfaceSourceShift);
                        heat += SurfaceConvection(conv, surfaceTemp[i], T, ux, uy, uz, velScale, surfacePerStep,
                            surfaceCoef[c3], surfaceCoef[c3 + 1], surfaceCoef[c3 + 2]);
                        if (surfaceCoef[c3 + 3] != 0f)   // RT-03: further groups of faces, other sources
                            heat += SurfaceConvection((int)surfaceCoef[c3 + 3], surfaceCoef[c3 + 4], T, ux, uy, uz, velScale, surfacePerStep,
                                surfaceCoef[c3 + 5], surfaceCoef[c3 + 6], surfaceCoef[c3 + 7]);
                        if (surfaceCoef[c3 + 8] != 0f)
                            heat += SurfaceConvection((int)surfaceCoef[c3 + 8], surfaceCoef[c3 + 9], T, ux, uy, uz, velScale, surfacePerStep,
                                surfaceCoef[c3 + 10], surfaceCoef[c3 + 11], surfaceCoef[c3 + 12]);
                    }
                }

                float nuTLat = (tau - t0) / 3f;
                if (hybrid)
                {
                    hyb![count + i] = alphaLatMolecular + nuTLat / TurbPr; hyb[2 * count + i] = heat;
                }
                else
                {
                    float tauG = MathF.Max(GInvCs2 * (alphaLatMolecular + nuTLat / TurbPr) + 0.5f, TauGMin);
                    G7 gout = CollideG(gin, T, ux, uy, uz, tauG, heat);
                    WriteAoSG(nxtG, i, gout);
                }

                if (accumulate)
                {
                    if (instant.Length > 0) instant[i] = nuTLat;   // 6k: this step's eddy viscosity, read by the online age
                    sumRho[i] += rho - 1f; sumUx[i] += ux; sumUy[i] += uy; sumUz[i] += uz; sumNuT[i] += nuTLat; sumT[i] += T;
                }
            }
        });
    }

    // ======================================================================================
    // GPU path (ILGPU): SoA (cur[q*Count+i]) for coalescing. Kernels call the same Collide/ComputeFeq
    // as the CPU path; only the gather/scatter addressing differs.
    // ponytail: the near-boundary gather loops q=0..18 through an uploaded 19-entry direction table
    // (ArrayView<int>, not the CPU's hand-unrolled statements -- ILGPU kernels can't touch a C# array or
    // Span directly) instead of a 19-way hand unroll; simpler and correct, costs some MLUPS vs. a fully
    // unrolled kernel. Upgrade path: unroll if GPU MLUPS becomes the bottleneck.
    // ======================================================================================

    /// <summary>Snapshot from a host-copied SoA <c>cur</c> buffer (and optionally <c>curG</c>) -- same
    /// output as <see cref="CaptureCpu"/>, just reading device-shaped memory that's already come back.</summary>
    static Snapshot CaptureGpu(VoxelGrid grid, float[] hostCur, int count, float velScale, float[]? hostCurG, float time, float[]? hostT = null)
    {
        var ux = new System.Half[count]; var uy = new System.Half[count]; var uz = new System.Half[count];
        System.Half[] t = hostCurG != null || hostT != null ? new System.Half[count] : [];
        Parallel.For(0, count, i =>
        {
            if (grid.Cell[i] == VoxelGrid.Solid) return;
            float f0 = hostCur[i], f1 = hostCur[count + i], f2 = hostCur[2 * count + i], f3 = hostCur[3 * count + i], f4 = hostCur[4 * count + i],
                  f5 = hostCur[5 * count + i], f6 = hostCur[6 * count + i], f7 = hostCur[7 * count + i], f8 = hostCur[8 * count + i], f9 = hostCur[9 * count + i],
                  f10 = hostCur[10 * count + i], f11 = hostCur[11 * count + i], f12 = hostCur[12 * count + i], f13 = hostCur[13 * count + i], f14 = hostCur[14 * count + i],
                  f15 = hostCur[15 * count + i], f16 = hostCur[16 * count + i], f17 = hostCur[17 * count + i], f18 = hostCur[18 * count + i];
            float rho = f0 + f1 + f2 + f3 + f4 + f5 + f6 + f7 + f8 + f9 + f10 + f11 + f12 + f13 + f14 + f15 + f16 + f17 + f18;
            float invRho = 1f / rho;
            float mx = f1 - f2 + f7 - f8 + f9 - f10 + f11 - f12 + f13 - f14;
            float my = f3 - f4 + f7 - f8 - f9 + f10 + f15 - f16 + f17 - f18;
            float mz = f5 - f6 + f11 - f12 - f13 + f14 + f15 - f16 - f17 + f18;
            ux[i] = (System.Half)(mx * invRho * velScale); uy[i] = (System.Half)(my * invRho * velScale); uz[i] = (System.Half)(mz * invRho * velScale);
            if (hostT != null) t[i] = (System.Half)hostT[i];
            if (hostCurG != null)
            {
                float t0 = hostCurG[i], t1 = hostCurG[count + i], t2 = hostCurG[2 * count + i], t3 = hostCurG[3 * count + i],
                      t4 = hostCurG[4 * count + i], t5 = hostCurG[5 * count + i], t6 = hostCurG[6 * count + i];
                t[i] = (System.Half)(t0 + t1 + t2 + t3 + t4 + t5 + t6);
            }
        });
        return new Snapshot { Time = time, Ux = ux, Uy = uy, Uz = uz, T = t };
    }

    /// <summary>Scalar thermal constants bundled into one struct (must be `public`, same TypeLoadException
    /// trap as <see cref="GridDims"/>) to keep the combined step kernel's parameter count low.
    /// 6e/6j: VelScale (m/s per lattice unit) and SurfacePerStep (dt / (rho c_p dx)) for the Surface convection; SurfaceTable, HoldPlane and
    /// HoldTable are offsets into cellData (see BuildCellData), HoldPlane -1 = free-running.</summary>
    public struct ThermalParams { public float Tau0, AlphaLatMolecular, BetaB, GLat, TRef, DtSeconds, Ramp, VelScale, SurfacePerStep; public int DevUnits, SurfaceTable, HoldPlane, HoldTable; }

    static bool _gpuThermalChecked;
    static readonly object GpuThermalLock = new();
    static Action<Index1D, ArrayView<byte>, ArrayView<float>, ArrayView<float>, int>? _initKernelG;
    static Action<Index1D, ArrayView<int>, ArrayView<byte>, ArrayView<float>, ArrayView<int>, ArrayView<float>, int, float, int>? _boundaryKernelThermalG;
    static Action<Index1D, ArrayView<int>, ArrayView<float>, int, ArrayView<float>>? _gatherTemperatureKernel;
    // ArrayView<int> dir carries BOTH direction tables concatenated (F's 95 entries at [0,95), G's 35 at
    // [95,130)) and ArrayView<float> cellData carries heatRate/surfaceFixed(as 0/1)/surfaceTemp concatenated
    // (each count long) -- kept as two combined arrays instead of five separate ones so this stays well
    // under whatever arity ILGPU's kernel-loader extension methods actually support (16 params -- dir and
    // gdir as separate ArrayView<int> plus the 3 separate per-cell float/byte arrays -- silently failed to
    // resolve LoadAutoGroupedStreamKernel; the existing non-thermal kernels all sit at <= 12 params).
    static Action<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<int>, ArrayView<float>, ArrayView<float>,
        ArrayView<float>, ArrayView<float>, GridDims, ThermalParams, ArrayView<float>, byte, ArrayView<float>>? _stepKernelThermal;

    /// <summary>Lazily JITs the thermal kernels the first time a thermal GPU run happens -- kept out of
    /// <see cref="EnsureGpu"/>/<see cref="TryCreate"/> (the non-thermal device-selection probe) so a device
    /// that can run the ordinary flow kernels but chokes on the thermal ones can't break wind-only runs.</summary>
    static void EnsureGpuThermal()
    {
        if (_gpuThermalChecked) return;
        lock (GpuThermalLock)
        {
            if (_gpuThermalChecked) return;
            var accel = _accel!;
            _initKernelG = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<float>, ArrayView<float>, int>(InitKernelG);
            _boundaryKernelThermalG = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>, ArrayView<byte>, ArrayView<float>, ArrayView<int>, ArrayView<float>, int, float, int>(BoundaryKernelThermalG);
            _gatherTemperatureKernel = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>, ArrayView<float>, int, ArrayView<float>>(GatherTemperatureKernel);
            _stepKernelThermal = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<int>, ArrayView<float>, ArrayView<float>,
                ArrayView<float>, ArrayView<float>, GridDims, ThermalParams, ArrayView<float>, byte, ArrayView<float>>(StepKernelThermal);
            _gpuThermalChecked = true;
        }
    }

    // -- thermal kernels --

    /// <summary>Temperature of a handful of named cells, so the host can read each conditioning unit's
    /// return grille every <see cref="DeviceUnitCadence"/> steps without dragging the whole thermal
    /// lattice back across the bus.</summary>
    static void GatherTemperatureKernel(Index1D k, ArrayView<int> idxs, ArrayView<float> curG, int count, ArrayView<float> outT)
    {
        int i = idxs[k];
        outT[k] = curG[i] + curG[count + i] + curG[2 * count + i] + curG[3 * count + i]
                + curG[4 * count + i] + curG[5 * count + i] + curG[6 * count + i];
    }

    static void InitKernelG(Index1D i, ArrayView<byte> cellType, ArrayView<float> curG, ArrayView<float> t0, int count)
    {
        if (cellType[i] == VoxelGrid.Solid) return;
        WriteSoAG(curG, i, count, ComputeGeq(t0[i], 0f, 0f, 0f));
    }

    /// <summary>Ring-only T boundary (launched with <c>ringCount</c> threads -- the first entries of every
    /// <paramref name="bIndex"/>/<paramref name="bClass"/>/<paramref name="bVel"/>/<paramref name="bNeighborSlot"/>
    /// array, same layout <see cref="SetBoundaryThermalRingCpu"/> reads). Fixed (mechanical) cells are
    /// outside this range and keep their initial T, same shortcut as the CPU path.</summary>
    static void BoundaryKernelThermalG(Index1D k, ArrayView<int> bIndex, ArrayView<byte> bClass, ArrayView<float> bVel,
        ArrayView<int> bNeighborSlot, ArrayView<float> curG, int count, float tOut, int n)
    {
        int idx = bIndex[k]; byte cls = bClass[k];
        G7 geq;
        if (IsInflow(cls)) geq = ComputeGeq(tOut, bVel[k], bVel[n + k], bVel[2 * n + k]);
        else if (cls == BcOpen) geq = ComputeGeq(tOut, 0f, 0f, 0f);
        else
        {
            G7 gN = ReadSoAG(curG, bNeighborSlot[k], count);
            geq = ComputeGeq(SumG(gN), 0f, 0f, 0f);
        }
        WriteSoAG(curG, idx, count, geq);
    }

    /// <summary><paramref name="dir"/> carries both direction tables concatenated (F's 95 entries at
    /// [0,95), G's 35 at [95,130), see <see cref="EnsureGpuThermal"/>); <paramref name="cellData"/> carries
    /// heatRate/surfaceFixed(as 0/1)/surfaceTemp concatenated (each count long), followed by
    /// <see cref="BuildDeviceData"/>'s six device slots when bit 1 of <paramref name="flags"/> is set --
    /// all combined purely to keep this kernel's parameter count low enough for ILGPU's kernel loader
    /// (16 separate views silently fail to resolve; this one sits at 13 arguments, as it did before
    /// devices existed). <paramref name="flags"/>: bit 0 = accumulate the time average, bit 1 = the grid
    /// has body-force devices.</summary>
    static void StepKernelThermal(Index1D i, ArrayView<byte> cellType, ArrayView<byte> nearBoundary, ArrayView<int> dir,
        ArrayView<float> cur, ArrayView<float> nxt, ArrayView<float> curG, ArrayView<float> nxtG,
        GridDims dims, ThermalParams tp, ArrayView<float> cellData, byte flags, ArrayView<float> sums)
    {
        int idx = i;
        if (cellType[idx] != VoxelGrid.Air) return;
        int Nx = dims.Nx, Ny = dims.Ny, Nz = dims.Nz, count = dims.Count;
        int x = idx % Nx; int t = idx / Nx; int y = t % Ny; int z = t / Ny;
        if (x == 0 || x == Nx - 1 || y == 0 || y == Ny - 1 || (z == 0 && dims.OpenZMin != 0) || (z == Nz - 1 && dims.OpenZMax != 0)) return;

        bool nb = (nearBoundary[idx] & 1) != 0;
        int surf = (int)cellData[count + idx];
        F19 fin = nb ? GatherBoundarySoA(cellType, nearBoundary, cur, Nx, Ny, Nz, count, x, y, z, idx, dir) : GatherPlainSoA(cur, count, idx, Nx, Ny);
        // bit 2 = ThermalScheme.Hybrid (LbmHybridThermal.cs): curG is then the T plane and nxtG the hyb planes (rho*, kappa, q), no D3Q7
        bool hybrid = (flags & 4) != 0;
        G7 gin = default; float T;
        if (hybrid) T = curG[idx];
        else
        {
            gin = nb ? GatherBoundarySoAG(cellType, nearBoundary, curG, Nx, Ny, Nz, count, x, y, z, idx, dir,
                    (surf & SurfaceResolvedBit) != 0 ? surf & 63 : 0, cellData[2 * count + idx])
                : GatherPlainSoAG(curG, count, idx, Nx, Ny);
            T = SumG(gin);
        }
        float Fz = MathF.Min(MathF.Max(tp.BetaB * (T - tp.TRef) * tp.GLat, -FLatMax), FLatMax);

        float dfx = 0f, dfy = 0f, dfz = 0f, heat = cellData[idx] * tp.DtSeconds * tp.Ramp;
        if ((flags & 2) != 0)
        {
            int d0 = 3 * count;   // device slots start after heatRate/surfaceFixed/surfaceTemp
            if (cellData[d0 + 3 * count + idx] != 0f)
            {
                DeviceForce(fin, cellData[d0 + idx], cellData[d0 + count + idx], cellData[d0 + 2 * count + idx],
                    cellData[d0 + 3 * count + idx], out dfx, out dfy, out dfz);
                // The per-unit table is the tail of cellData (see BuildCellData), so this kernel keeps
                // its 13 arguments -- ILGPU stops resolving the loader well before 16.
                heat -= DeviceCooling(cellData[d0 + 4 * count + idx], cellData[d0 + 5 * count + idx], T,
                    cellData, tp.DevUnits, 9 * count);
            }
        }

        float t0 = SpongeTau(tp.Tau0, x, y, Nx, Ny, dims.SpongeFaces, dims.SpongeCells);   // 6b: the outflow sponge
        CollideForced(fin, t0, dfx, dfy, Fz + dfz, out F19 fout, out float rho, out float ux, out float uy, out float uz, out float tau);
        if (dims.SpongeFaces != 0) fout = SpongeAbsorb(fout, rho, ux, uy, uz, x, y, Nx, Ny, dims.SpongeFaces, dims.SpongeCells);   // RZ-01
        WriteSoA(nxt, idx, count, fout);

        // 6e / 6j: Surface convection and the room's held-temperature source, as RunStepThermal (cellData layout: BuildCellData)
        int conv = (surf & SurfaceResolvedBit) == 0 ? surf & 63 : 0;
        int room = tp.HoldPlane >= 0 ? (int)cellData[tp.HoldPlane + idx] : -1;
        if ((conv != 0 || room >= 0) && !InSponge(x, y, Nx, Ny, dims.SpongeFaces, dims.SpongeCells))
        {
            if (room >= 0) heat += cellData[tp.HoldTable + room];
            if (conv != 0)
            {
                int c3 = tp.SurfaceTable + SurfaceStride * (surf >> SurfaceSourceShift);
                heat += SurfaceConvection(conv, cellData[2 * count + idx], T, ux, uy, uz, tp.VelScale, tp.SurfacePerStep,
                    cellData[c3], cellData[c3 + 1], cellData[c3 + 2]);
                if (cellData[c3 + 3] != 0f)   // RT-03: further groups of faces, other sources
                    heat += SurfaceConvection((int)cellData[c3 + 3], cellData[c3 + 4], T, ux, uy, uz, tp.VelScale, tp.SurfacePerStep,
                        cellData[c3 + 5], cellData[c3 + 6], cellData[c3 + 7]);
                if (cellData[c3 + 8] != 0f)
                    heat += SurfaceConvection((int)cellData[c3 + 8], cellData[c3 + 9], T, ux, uy, uz, tp.VelScale, tp.SurfacePerStep,
                        cellData[c3 + 10], cellData[c3 + 11], cellData[c3 + 12]);
            }
        }

        float nuTLat = (tau - t0) / 3f;
        if (hybrid)
        {
            // rho* = sum of the post-collision populations (same left-to-right order as the CPU's HybridDensity), kappa and q for the FV pass
            nxtG[idx] = fout.F0 + fout.F1 + fout.F2 + fout.F3 + fout.F4 + fout.F5 + fout.F6 + fout.F7 + fout.F8 + fout.F9
                + fout.F10 + fout.F11 + fout.F12 + fout.F13 + fout.F14 + fout.F15 + fout.F16 + fout.F17 + fout.F18;
            nxtG[count + idx] = tp.AlphaLatMolecular + nuTLat / TurbPr; nxtG[2 * count + idx] = heat;
        }
        else
        {
            float tauG = MathF.Max(GInvCs2 * (tp.AlphaLatMolecular + nuTLat / TurbPr) + 0.5f, TauGMin);
            G7 gout = CollideG(gin, T, ux, uy, uz, tauG, heat);
            WriteSoAG(nxtG, idx, count, gout);
        }

        if ((flags & 1) != 0)
        {
            sums[idx] += rho - 1f; sums[count + idx] += ux; sums[2 * count + idx] += uy; sums[3 * count + idx] += uz;
            sums[4 * count + idx] += nuTLat; sums[5 * count + idx] += T;
            if (sums.Length > 6 * count) sums[6 * count + idx] = nuTLat;   // 6k: this step's eddy viscosity (the extra plane exists only with RunOptions.OnlineAge)
        }
    }

    static G7 ReadSoAG(ArrayView<float> arr, int idx, int count) => new(
        arr[idx], arr[count + idx], arr[2 * count + idx], arr[3 * count + idx], arr[4 * count + idx], arr[5 * count + idx], arr[6 * count + idx]);

    static void WriteSoAG(ArrayView<float> arr, int idx, int count, in G7 v)
    {
        arr[idx] = v.G0; arr[count + idx] = v.G1; arr[2 * count + idx] = v.G2; arr[3 * count + idx] = v.G3;
        arr[4 * count + idx] = v.G4; arr[5 * count + idx] = v.G5; arr[6 * count + idx] = v.G6;
    }

    static G7 GatherPlainSoAG(ArrayView<float> curG, int count, int i, int Nx, int Ny)
    {
        int nxny = Nx * Ny;
        int o1 = -1, o2 = 1, o3 = -Nx, o4 = Nx, o5 = -nxny, o6 = nxny;
        return new G7(curG[i], curG[1 * count + i + o1], curG[2 * count + i + o2], curG[3 * count + i + o3],
            curG[4 * count + i + o4], curG[5 * count + i + o5], curG[6 * count + i + o6]);
    }

    /// <summary><paramref name="dir"/> is the combined F+G direction table (see <see cref="StepKernelThermal"/>);
    /// G's own 5 sub-tables start at offset 95. <paramref name="faces"/>/<paramref name="tWall"/>: the same Resolved Surface faces as
    /// <see cref="GatherBoundaryG"/> (CPU) -- must stay in lockstep with it.</summary>
    static G7 GatherBoundarySoAG(ArrayView<byte> cellType, ArrayView<byte> flags, ArrayView<float> curG, int Nx, int Ny, int Nz, int count,
        int x, int y, int z, int i, ArrayView<int> dir, int faces, float tWall)
    {
        const int Off = 95;
        G7 result = default;
        for (int q = 0; q < Q7; q++)
        {
            int cx = dir[Off + q], cy = dir[Off + 7 + q], cz = dir[Off + 14 + q], opp = dir[Off + 21 + q], mirror = dir[Off + 28 + q];
            int sz = z - cz, sx = x - cx, sy = y - cy;
            float val;
            if (sz < 0 || sz >= Nz)
            {
                int sIdx = sx + Nx * (sy + Ny * z);
                val = cellType[sIdx] != VoxelGrid.Solid ? ReadComponentG(curG, sIdx, count, mirror)
                    : (faces >> (opp - 1) & 1) != 0 ? 2f * GWeight(q) * tWall - ReadComponentG(curG, i, count, opp) : ReadComponentG(curG, i, count, opp);
            }
            else
            {
                int sIdx = sx + Nx * (sy + Ny * sz);
                val = cellType[sIdx] != VoxelGrid.Solid
                    ? (q > 0 && LinkCutSoA(flags, count, i, q)
                        ? (faces >> (opp - 1) & 1) != 0 ? 2f * GWeight(q) * tWall - ReadComponentG(curG, i, count, opp) : ReadComponentG(curG, i, count, opp)
                        : ReadComponentG(curG, sIdx, count, q))
                    : (faces >> (opp - 1) & 1) != 0 ? 2f * GWeight(q) * tWall - ReadComponentG(curG, i, count, opp) : ReadComponentG(curG, i, count, opp);
            }
            SetComponentG(ref result, q, val);
        }
        return result;
    }

    static float ReadComponentG(ArrayView<float> arr, int idx, int count, int q) => arr[q * count + idx];

    static void SetComponentG(ref G7 v, int q, float val)
    {
        switch (q)
        {
            case 0: v.G0 = val; break; case 1: v.G1 = val; break; case 2: v.G2 = val; break;
            case 3: v.G3 = val; break; case 4: v.G4 = val; break; case 5: v.G5 = val; break;
            default: v.G6 = val; break;
        }
    }

    // -- kernels --

    static void InitKernel(Index1D i, ArrayView<byte> cellType, ArrayView<float> cur, int count)
    {
        if (cellType[i] != VoxelGrid.Air) return;
        WriteSoA(cur, i, count, ComputeFeq(1f, 0f, 0f, 0f));
    }

    static void BoundaryKernel(Index1D k, ArrayView<int> bIndex, ArrayView<byte> bClass, ArrayView<float> bVel,
        ArrayView<sbyte> bNxNy, ArrayView<int> bNeighborSlot, ArrayView<float> cur, int count, float ramp, int n)
    {
        int idx = bIndex[k]; byte cls = bClass[k];
        float velX = bVel[k], velY = bVel[n + k], velZ = bVel[2 * n + k];
        F19 feq;
        if (cls == BcInflow || cls == BcOutflow || cls == BcLid)
        {
            feq = RingCell(ReadSoA(cur, bNeighborSlot[k], count), cls, velX * ramp, velY * ramp, velZ * ramp, bNxNy[k], bNxNy[n + k]);
        }
        else if (cls == BcTangential || cls == BcInflowEq) feq = ComputeFeq(1f, velX * ramp, velY * ramp, velZ * ramp);
        else if (cls == BcOpen || cls == BcOutflowEq)
        {
            NeighborMacroSoA(cur, bNeighborSlot[k], count, out float ux, out float uy, out float uz);
            if (cls == BcOutflowEq)
            {
                float nx = bNxNy[k], ny = bNxNy[n + k], un = ux * nx + uy * ny;
                if (un < 0f) { ux -= un * nx; uy -= un * ny; }
            }
            feq = ComputeFeq(1f, ux, uy, uz);
        }
        else if (cls == BcSupply)
        {
            feq = ComputeFeq(1f, velX, velY, velZ);
        }
        else feq = ComputeFeq(FixedCellRho, velX, velY, velZ);
        WriteSoA(cur, idx, count, feq);
    }

    static void StepKernel(Index1D i, ArrayView<byte> cellType, ArrayView<byte> nearBoundary, ArrayView<int> dir,
        ArrayView<float> cur, ArrayView<float> nxt, GridDims dims, float tau0, byte accumulate, ArrayView<float> sums)
    {
        int idx = i;
        if (cellType[idx] != VoxelGrid.Air) return;
        int Nx = dims.Nx, Ny = dims.Ny, Nz = dims.Nz, count = dims.Count;
        int x = idx % Nx; int t = idx / Nx; int y = t % Ny; int z = t / Ny;
        if (x == 0 || x == Nx - 1 || y == 0 || y == Ny - 1 || (z == 0 && dims.OpenZMin != 0) || (z == Nz - 1 && dims.OpenZMax != 0)) return;

        F19 fin = (nearBoundary[idx] & 1) != 0
            ? GatherBoundarySoA(cellType, nearBoundary, cur, Nx, Ny, Nz, count, x, y, z, idx, dir)
            : GatherPlainSoA(cur, count, idx, Nx, Ny);

        float t0 = SpongeTau(tau0, x, y, Nx, Ny, dims.SpongeFaces, dims.SpongeCells);   // 6b: the outflow sponge
        Collide(fin, t0, out F19 fout, out float rho, out float ux, out float uy, out float uz, out float tau);
        if (dims.SpongeFaces != 0) fout = SpongeAbsorb(fout, rho, ux, uy, uz, x, y, Nx, Ny, dims.SpongeFaces, dims.SpongeCells);   // RZ-01
        WriteSoA(nxt, idx, count, fout);

        if (accumulate != 0)
        {
            sums[idx] += rho - 1f; sums[count + idx] += ux; sums[2 * count + idx] += uy; sums[3 * count + idx] += uz;
            sums[4 * count + idx] += (tau - t0) / 3f;
            if (sums.Length > 5 * count) sums[5 * count + idx] = (tau - t0) / 3f;   // 6k: this step's eddy viscosity (the extra plane exists only with RunOptions.OnlineAge)
        }
    }

    static void DivergenceKernel(Index1D k, ArrayView<int> sampleIdx, ArrayView<float> cur, int count, ArrayView<float> outRho)
    {
        outRho[k] = SumSoA(cur, sampleIdx[k], count);
    }

    /// <summary>P17-06: the whole-field twin of <see cref="DivergenceKernel"/>: every non-ring air cell, rho NaN or outside the band sets bad[0] = 1
    /// (all writers store the same value, so no atomic is needed).</summary>
    static void DivergenceAllKernel(Index1D i, ArrayView<byte> cellType, ArrayView<float> cur, GridDims dims, ArrayView<int> bad)
    {
        int idx = i;
        if (cellType[idx] != VoxelGrid.Air) return;
        int x = idx % dims.Nx, y = idx / dims.Nx % dims.Ny;
        if (x == 0 || x == dims.Nx - 1 || y == 0 || y == dims.Ny - 1) return;
        float rho = SumSoA(cur, idx, dims.Count);
        if (!(rho >= RhoMin && rho <= RhoMax)) bad[0] = 1;   // true for NaN too
    }

    // -- device kernel (non-thermal runs with body-force devices; JITted lazily, see EnsureGpuDevice) --

    static bool _gpuDeviceChecked;
    static readonly object GpuDeviceLock = new();
    static Action<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<int>, ArrayView<float>, ArrayView<float>,
        GridDims, float, byte, ArrayView<float>, ArrayView<float>>? _stepKernelDevice;

    /// <summary>Lazily JITs the device step kernel -- same reasoning as <see cref="EnsureGpuThermal"/>:
    /// <see cref="StepKernel"/>, the wind-only hot path, stays exactly as it was and never pays for the
    /// device branch (registers included).</summary>
    static void EnsureGpuDevice()
    {
        if (_gpuDeviceChecked) return;
        lock (GpuDeviceLock)
        {
            if (_gpuDeviceChecked) return;
            _stepKernelDevice = _accel!.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<int>,
                ArrayView<float>, ArrayView<float>, GridDims, float, byte, ArrayView<float>, ArrayView<float>>(StepKernelDevice);
            _gpuDeviceChecked = true;
        }
    }

    /// <summary><see cref="StepKernel"/> plus the body-force devices of <see cref="BuildDeviceData"/>
    /// (<paramref name="devData"/>, four slots per cell). Must stay in lockstep with the CPU's
    /// <see cref="RunStep"/>.</summary>
    static void StepKernelDevice(Index1D i, ArrayView<byte> cellType, ArrayView<byte> nearBoundary, ArrayView<int> dir,
        ArrayView<float> cur, ArrayView<float> nxt, GridDims dims, float tau0, byte accumulate, ArrayView<float> sums,
        ArrayView<float> devData)
    {
        int idx = i;
        if (cellType[idx] != VoxelGrid.Air) return;
        int Nx = dims.Nx, Ny = dims.Ny, Nz = dims.Nz, count = dims.Count;
        int x = idx % Nx; int t = idx / Nx; int y = t % Ny; int z = t / Ny;
        if (x == 0 || x == Nx - 1 || y == 0 || y == Ny - 1 || (z == 0 && dims.OpenZMin != 0) || (z == Nz - 1 && dims.OpenZMax != 0)) return;

        F19 fin = (nearBoundary[idx] & 1) != 0
            ? GatherBoundarySoA(cellType, nearBoundary, cur, Nx, Ny, Nz, count, x, y, z, idx, dir)
            : GatherPlainSoA(cur, count, idx, Nx, Ny);

        tau0 = SpongeTau(tau0, x, y, Nx, Ny, dims.SpongeFaces, dims.SpongeCells);   // 6b: the outflow sponge (the cell's own base tau from here on)
        F19 fout; float rho, ux, uy, uz, tau;
        if (devData[3 * count + idx] != 0f)
        {
            DeviceForce(fin, devData[idx], devData[count + idx], devData[2 * count + idx], devData[3 * count + idx],
                out float fx, out float fy, out float fz);
            CollideForced(fin, tau0, fx, fy, fz, out fout, out rho, out ux, out uy, out uz, out tau);
        }
        else Collide(fin, tau0, out fout, out rho, out ux, out uy, out uz, out tau);
        if (dims.SpongeFaces != 0) fout = SpongeAbsorb(fout, rho, ux, uy, uz, x, y, Nx, Ny, dims.SpongeFaces, dims.SpongeCells);   // RZ-01
        WriteSoA(nxt, idx, count, fout);

        if (accumulate != 0)
        {
            sums[idx] += rho - 1f; sums[count + idx] += ux; sums[2 * count + idx] += uy; sums[3 * count + idx] += uz;
            sums[4 * count + idx] += (tau - tau0) / 3f;
            if (sums.Length > 5 * count) sums[5 * count + idx] = (tau - tau0) / 3f;   // 6k: this step's eddy viscosity (the extra plane exists only with RunOptions.OnlineAge)
        }
    }

    // -- live-session kernels (JITted lazily, see EnsureGpuLive: a batch Solve never loads them) --

    static bool _gpuLiveChecked;
    static readonly object GpuLiveLock = new();
    static Action<Index1D, ArrayView<byte>, ArrayView<float>, int, float, ArrayView<float>, ArrayView<float>, ArrayView<float>>? _velocityKernel;
    static Action<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
        ArrayView<float>, ArrayView<float>, int, byte>? _resetNewAirKernel;

    /// <summary>Lazily JITs the two kernels only a live <see cref="LbmSession"/> needs (instantaneous
    /// velocity read-back and the newly-air re-initialisation of <see cref="LbmSession.UpdateCells"/>) --
    /// same reasoning as <see cref="EnsureGpuThermal"/>: kept out of the device-selection probe so they
    /// cannot break an ordinary solve.</summary>
    static void EnsureGpuLive()
    {
        if (_gpuLiveChecked) return;
        lock (GpuLiveLock)
        {
            if (_gpuLiveChecked) return;
            var accel = _accel!;
            _velocityKernel = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<float>, int, float,
                ArrayView<float>, ArrayView<float>, ArrayView<float>>(VelocityKernel);
            _resetNewAirKernel = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<float>,
                ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, byte>(ResetNewAirKernel);
            _gpuLiveChecked = true;
        }
    }

    /// <summary>Instantaneous velocity of every cell in m/s, written as three dense device arrays so the
    /// host read-back is one copy per component instead of dragging all 19 populations across the bus.
    /// Solids are 0 and Fixed cells come out at their imposed velocity for free (their populations are
    /// feq(rho, u_fixed)), i.e. the same conventions as <see cref="FlowField.Ux"/>.</summary>
    static void VelocityKernel(Index1D i, ArrayView<byte> cellType, ArrayView<float> cur, int count, float velScale,
        ArrayView<float> ux, ArrayView<float> uy, ArrayView<float> uz)
    {
        if (cellType[i] == VoxelGrid.Solid) { ux[i] = 0f; uy[i] = 0f; uz[i] = 0f; return; }
        NeighborMacroSoA(cur, i, count, out float vx, out float vy, out float vz);
        ux[i] = vx * velScale; uy[i] = vy * velScale; uz[i] = vz * velScale;
    }

    /// <summary>Cells that were Solid and are now Air (an opening just opened) start from rest equilibrium:
    /// feq(1, 0) on the momentum lattice and geq(T0, 0) on the thermal one. Everything else keeps the state
    /// it had, so the rest of the flow warm-starts. When <paramref name="thermal"/> is 0 the caller passes
    /// <paramref name="cur"/> itself for curG/nxtG/t0 (unused views must still be valid).</summary>
    static void ResetNewAirKernel(Index1D i, ArrayView<byte> newCell, ArrayView<byte> oldCell, ArrayView<float> cur,
        ArrayView<float> nxt, ArrayView<float> curG, ArrayView<float> nxtG, ArrayView<float> t0, int count, byte thermal)
    {
        if (newCell[i] != VoxelGrid.Air || oldCell[i] != VoxelGrid.Solid) return;
        F19 feq = ComputeFeq(1f, 0f, 0f, 0f);
        WriteSoA(cur, i, count, feq); WriteSoA(nxt, i, count, feq);
        if (thermal != 0)
        {
            G7 geq = ComputeGeq(t0[i], 0f, 0f, 0f);
            WriteSoAG(curG, i, count, geq); WriteSoAG(nxtG, i, count, geq);
        }
    }

    // -- SoA helpers, shared by the kernels above --

    static F19 ReadSoA(ArrayView<float> arr, int idx, int count) => new(
        arr[idx], arr[count + idx], arr[2 * count + idx], arr[3 * count + idx], arr[4 * count + idx],
        arr[5 * count + idx], arr[6 * count + idx], arr[7 * count + idx], arr[8 * count + idx], arr[9 * count + idx],
        arr[10 * count + idx], arr[11 * count + idx], arr[12 * count + idx], arr[13 * count + idx], arr[14 * count + idx],
        arr[15 * count + idx], arr[16 * count + idx], arr[17 * count + idx], arr[18 * count + idx]);

    static void WriteSoA(ArrayView<float> arr, int idx, int count, in F19 v)
    {
        arr[idx] = v.F0; arr[count + idx] = v.F1; arr[2 * count + idx] = v.F2; arr[3 * count + idx] = v.F3; arr[4 * count + idx] = v.F4;
        arr[5 * count + idx] = v.F5; arr[6 * count + idx] = v.F6; arr[7 * count + idx] = v.F7; arr[8 * count + idx] = v.F8; arr[9 * count + idx] = v.F9;
        arr[10 * count + idx] = v.F10; arr[11 * count + idx] = v.F11; arr[12 * count + idx] = v.F12; arr[13 * count + idx] = v.F13; arr[14 * count + idx] = v.F14;
        arr[15 * count + idx] = v.F15; arr[16 * count + idx] = v.F16; arr[17 * count + idx] = v.F17; arr[18 * count + idx] = v.F18;
    }

    static float SumSoA(ArrayView<float> arr, int idx, int count)
    {
        float s = 0f;
        for (int q = 0; q < Q; q++) s += arr[q * count + idx];
        return s;
    }

    static void NeighborMacroSoA(ArrayView<float> arr, int idx, int count, out float ux, out float uy, out float uz)
    {
        F19 f = ReadSoA(arr, idx, count);
        float rho = f.F0 + f.F1 + f.F2 + f.F3 + f.F4 + f.F5 + f.F6 + f.F7 + f.F8 + f.F9 + f.F10 + f.F11 + f.F12 + f.F13 + f.F14 + f.F15 + f.F16 + f.F17 + f.F18;
        float invRho = 1f / rho;
        float mx = f.F1 - f.F2 + f.F7 - f.F8 + f.F9 - f.F10 + f.F11 - f.F12 + f.F13 - f.F14;
        float my = f.F3 - f.F4 + f.F7 - f.F8 - f.F9 + f.F10 + f.F15 - f.F16 + f.F17 - f.F18;
        float mz = f.F5 - f.F6 + f.F11 - f.F12 - f.F13 + f.F14 + f.F15 - f.F16 - f.F17 + f.F18;
        ux = mx * invRho; uy = my * invRho; uz = mz * invRho;
    }

    static float ReadComponent(ArrayView<float> arr, int idx, int count, int q) => arr[q * count + idx];

    static void SetComponent(ref F19 v, int q, float val)
    {
        switch (q)
        {
            case 0: v.F0 = val; break; case 1: v.F1 = val; break; case 2: v.F2 = val; break;
            case 3: v.F3 = val; break; case 4: v.F4 = val; break; case 5: v.F5 = val; break;
            case 6: v.F6 = val; break; case 7: v.F7 = val; break; case 8: v.F8 = val; break;
            case 9: v.F9 = val; break; case 10: v.F10 = val; break; case 11: v.F11 = val; break;
            case 12: v.F12 = val; break; case 13: v.F13 = val; break; case 14: v.F14 = val; break;
            case 15: v.F15 = val; break; case 16: v.F16 = val; break; case 17: v.F17 = val; break;
            default: v.F18 = val; break;
        }
    }

    static F19 GatherPlainSoA(ArrayView<float> cur, int count, int i, int Nx, int Ny)
    {
        int nxny = Nx * Ny;
        int o1 = -1, o2 = 1, o3 = -Nx, o4 = Nx, o5 = -nxny, o6 = nxny,
            o7 = -1 - Nx, o8 = 1 + Nx, o9 = -1 + Nx, o10 = 1 - Nx,
            o11 = -1 - nxny, o12 = 1 + nxny, o13 = -1 + nxny, o14 = 1 - nxny,
            o15 = -Nx - nxny, o16 = Nx + nxny, o17 = -Nx + nxny, o18 = Nx - nxny;
        return new F19(
            cur[i],
            cur[1 * count + i + o1], cur[2 * count + i + o2], cur[3 * count + i + o3], cur[4 * count + i + o4],
            cur[5 * count + i + o5], cur[6 * count + i + o6], cur[7 * count + i + o7], cur[8 * count + i + o8],
            cur[9 * count + i + o9], cur[10 * count + i + o10], cur[11 * count + i + o11], cur[12 * count + i + o12],
            cur[13 * count + i + o13], cur[14 * count + i + o14], cur[15 * count + i + o15], cur[16 * count + i + o16],
            cur[17 * count + i + o17], cur[18 * count + i + o18]);
    }

    /// <summary>Checked gather for the GPU: same rule as the CPU's <see cref="GatherBoundary"/> (bounce-back
    /// off Solid, SupplyBounce off a Supply patch cell; Air and other Fixed neighbours stream normally), but reads the direction table from device
    /// memory instead of a compile-time Span, since kernels can't touch a C# array directly.</summary>
    static F19 GatherBoundarySoA(ArrayView<byte> cellType, ArrayView<byte> flags, ArrayView<float> cur,
        int Nx, int Ny, int Nz, int count, int x, int y, int z, int i, ArrayView<int> dir)
    {
        F19 result = default;
        float wallMass = 0f;   // see GatherBoundary: interpolated wall links' mass excess goes to the rest population
        for (int q = 0; q < 19; q++)
        {
            int cx = dir[q], cy = dir[19 + q], cz = dir[38 + q], opp = dir[57 + q], mirror = dir[76 + q];
            int sz = z - cz, sx = x - cx, sy = y - cy;
            float val;
            if (sz < 0 || sz >= Nz)
            {
                int sIdx = sx + Nx * (sy + Ny * z);
                val = cellType[sIdx] == VoxelGrid.Solid ? ReadComponent(cur, i, count, opp) : ReadComponent(cur, sIdx, count, mirror);
            }
            else
            {
                int sIdx = sx + Nx * (sy + Ny * sz);
                byte sc = cellType[sIdx];
                if (sc == VoxelGrid.Solid || LinkCutSoA(flags, count, i, q))
                {
                    val = WallBounceSoA(cellType, flags, cur, Nx, Ny, Nz, count, i, sIdx, q, opp, cx, cy, cz, x, y, z);
                    wallMass += val - ReadComponent(cur, i, count, opp);
                }
                else
                    val = sc == VoxelGrid.Fixed && (flags[sIdx] & 1) != 0
                        ? SupplyBounce(ReadComponent(cur, i, count, opp), ReadComponent(cur, sIdx, count, q), ReadComponent(cur, sIdx, count, opp), flags[sIdx])
                        : ReadComponent(cur, sIdx, count, q);
            }
            SetComponent(ref result, q, val);
        }
        result.F0 -= wallMass;
        return result;
    }

    // ---- wall-model GPU twins (GatherBoundary / WallSlipUpdate with RunOptions.WallModel on); lazily JITted, see EnsureGpuWm ----

    static float LinkQwSoA(ArrayView<byte> flags, int count, int i, int sIdx, int q)
    {
        if (flags.Length <= count) return LinkWallFraction(flags[i], flags[sIdx]);
        int encoded = flags[count + q * count + i];
        return encoded == 0 ? -1f : (encoded - 1) / 254f;
    }

    /// <summary>GPU twin of <see cref="WallSlipUpdate"/>.</summary>
    static void WallSlipUpdateSoA(ArrayView<byte> cellType, ArrayView<byte> flags, ArrayView<float> cur, int Nx, int Ny, int Nz,
        int count, int x, int y, int z, int i, ArrayView<int> dir, ArrayView<float> wm, float nuP, float nuW,
        out float usx, out float usy, out float usz, out float rho)
    {
        float sx = 0f, sy = 0f, sz = 0f;
        for (int q = 1; q < 19; q++)
        {
            int cx = dir[q], cy = dir[19 + q], cz = dir[38 + q];
            int z0 = z - cz;
            if (z0 < 0 || z0 >= Nz || cellType[(x - cx) + Nx * ((y - cy) + Ny * z0)] != VoxelGrid.Solid && !LinkCutSoA(flags, count, i, q)) continue;
            sx += cx; sy += cy; sz += cz;
        }
        rho = 0f; float mx = 0f, my = 0f, mz = 0f;
        for (int k = 0; k < 19; k++)
        {
            float f = ReadComponent(cur, i, count, k);
            rho += f; mx += dir[k] * f; my += dir[19 + k] * f; mz += dir[38 + k] * f;
        }
        float nl = MathF.Sqrt(sx * sx + sy * sy + sz * sz), tx = 0f, ty = 0f, tz = 0f;
        if (nl > 1e-6f)
        {
            float nx = sx / nl, ny = sy / nl, nz = sz / nl, d = float.MaxValue;
            for (int q = 1; q < 19; q++)
            {
                int cx = dir[q], cy = dir[19 + q], cz = dir[38 + q];
                int z0 = z - cz;
                if (z0 < 0 || z0 >= Nz) continue;
                int sIdx = (x - cx) + Nx * ((y - cy) + Ny * z0);
                if (cellType[sIdx] != VoxelGrid.Solid && !LinkCutSoA(flags, count, i, q)) continue;
                float qw = LinkQwSoA(flags, count, i, sIdx, q);
                d = MathF.Min(d, (qw < 0f ? 0.5f : qw) * MathF.Abs(cx * nx + cy * ny + cz * nz));
            }
            // nu_w incl. Smagorinsky nu_t from the node's stored (previous post-collision) populations; see WallSlipUpdate.
            F19 fc = default;
            for (int k = 0; k < 19; k++) SetComponent(ref fc, k, ReadComponent(cur, i, count, k));
            WallSlipTarget(mx / rho, my / rho, mz / rho, nx, ny, nz, MathF.Max(d, WmMinDist), nuP, SmagNuW(fc, 3f * nuW + 0.5f), out tx, out ty, out tz);
        }
        float ax = wm[3 * i] + WmRelax * (tx - wm[3 * i]);
        float ay = wm[3 * i + 1] + WmRelax * (ty - wm[3 * i + 1]);
        float az = wm[3 * i + 2] + WmRelax * (tz - wm[3 * i + 2]);
        wm[3 * i] = ax; wm[3 * i + 1] = ay; wm[3 * i + 2] = az;
        usx = ax; usy = ay; usz = az;
    }

    /// <summary><see cref="GatherBoundarySoA"/> plus the wall model's moving-wall term (a separate copy so the default kernels are untouched).</summary>
    static F19 GatherBoundarySoAWm(ArrayView<byte> cellType, ArrayView<byte> flags, ArrayView<float> cur,
        int Nx, int Ny, int Nz, int count, int x, int y, int z, int i, ArrayView<int> dir, ArrayView<float> wm, WallModelArgs wa)
    {
        F19 result = default;
        float wallMass = 0f;
        bool wmOn = wa.NuP > 0f;
        float usx = 0f, usy = 0f, usz = 0f, wmRho = 1f;
        if (wmOn) WallSlipUpdateSoA(cellType, flags, cur, Nx, Ny, Nz, count, x, y, z, i, dir, wm, wa.NuP, wa.NuW, out usx, out usy, out usz, out wmRho);
        for (int q = 0; q < 19; q++)
        {
            int cx = dir[q], cy = dir[19 + q], cz = dir[38 + q], opp = dir[57 + q], mirror = dir[76 + q];
            int sz = z - cz, sx = x - cx, sy = y - cy;
            float val;
            if (sz < 0 || sz >= Nz)
            {
                int sIdx = sx + Nx * (sy + Ny * z);
                val = cellType[sIdx] == VoxelGrid.Solid ? ReadComponent(cur, i, count, opp) : ReadComponent(cur, sIdx, count, mirror);
            }
            else
            {
                int sIdx = sx + Nx * (sy + Ny * sz);
                byte sc = cellType[sIdx];
                if (sc == VoxelGrid.Solid || LinkCutSoA(flags, count, i, q))
                {
                    val = WallBounceSoA(cellType, flags, cur, Nx, Ny, Nz, count, i, sIdx, q, opp, cx, cy, cz, x, y, z);
                    if (wmOn) val += WallMoveTerm(LinkQwSoA(flags, count, i, sIdx, q), cx * usx + cy * usy + cz * usz, wmRho, cx * cx + cy * cy + cz * cz);
                    wallMass += val - ReadComponent(cur, i, count, opp);
                }
                else
                    val = sc == VoxelGrid.Fixed && (flags[sIdx] & 1) != 0
                        ? SupplyBounce(ReadComponent(cur, i, count, opp), ReadComponent(cur, sIdx, count, q), ReadComponent(cur, sIdx, count, opp), flags[sIdx])
                        : ReadComponent(cur, sIdx, count, q);
            }
            SetComponent(ref result, q, val);
        }
        result.F0 -= wallMass;
        return result;
    }

    static bool _gpuWmChecked;
    static readonly object GpuWmLock = new();
    static Action<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<int>, ArrayView<float>, ArrayView<float>,
        GridDims, float, byte, ArrayView<float>, ArrayView<float>, ArrayView<float>, WallModelArgs>? _stepKernelWm;
    static Action<Index1D, ArrayView<float>, GridDims>? _periodicXKernel;

    static void EnsureGpuWm()
    {
        if (_gpuWmChecked) return;
        lock (GpuWmLock)
        {
            if (_gpuWmChecked) return;
            _stepKernelWm = _accel!.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<int>,
                ArrayView<float>, ArrayView<float>, GridDims, float, byte, ArrayView<float>, ArrayView<float>, ArrayView<float>, WallModelArgs>(StepKernelWm);
            _periodicXKernel = _accel!.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, GridDims>(PeriodicXKernel);
            _gpuWmChecked = true;
        }
    }

    /// <summary>One thread per (y, z) row: ghost columns x = 0 and Nx-1 take the populations of columns Nx-2 and 1 (period Nx - 2).</summary>
    static void PeriodicXKernel(Index1D k, ArrayView<float> cur, GridDims dims)
    {
        int Nx = dims.Nx, count = dims.Count, row = Nx * k;
        for (int q = 0; q < 19; q++)
        {
            cur[q * count + row] = cur[q * count + row + Nx - 2];
            cur[q * count + row + Nx - 1] = cur[q * count + row + 1];
        }
    }

    static bool _gpuTuChecked;
    static readonly object GpuTuLock = new();
    static Action<Index1D, ArrayView<int>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
        ArrayView<float>, ArrayView<float>, int, int, float, float>? _windTuKernel;

    static void EnsureGpuWindTu()
    {
        if (_gpuTuChecked) return;
        lock (GpuTuLock)
        {
            if (_gpuTuChecked) return;
            _windTuKernel = _accel!.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>, ArrayView<float>, ArrayView<float>,
                ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, float, float>(WindTuKernel);
            _gpuTuChecked = true;
        }
    }

    /// <summary>One thread per inflow-ring entry: the Kraichnan/Smirnov mode sum of <see cref="WindTurbulenceField.Eval"/> with the
    /// time part of every phase supplied per step (tph, reduced on the host in double). Writes the ring velocity in place.
    /// md = nine blocks of `modes` floats (kx ky kz px py pz qx qy qz). Lockstep with the host WindTurbulenceStep.</summary>
    static void WindTuKernel(Index1D e, ArrayView<int> kk, ArrayView<float> bs, ArrayView<float> ps, ArrayView<float> sg,
        ArrayView<float> md, ArrayView<float> tph, ArrayView<float> vel, int n, int modes, float sx, float sy)
    {
        // P13b-02: the isotropic field is evaluated in the stream frame (x along the wind), where its components are mapped
        // below; the time phase convects along that frame's x only (TimePhases(uc, 0, ...)).
        float x = ps[3 * e] * sx + ps[3 * e + 1] * sy, y = -ps[3 * e] * sy + ps[3 * e + 1] * sx, z = ps[3 * e + 2];
        float ax = 0f, ay = 0f, az = 0f;
        for (int m = 0; m < modes; m++)
        {
            float ph = md[m] * x + md[modes + m] * y + md[2 * modes + m] * z + tph[m];
            float cs = MathF.Cos(ph), sn = MathF.Sin(ph);
            ax += md[3 * modes + m] * cs + md[6 * modes + m] * sn;
            ay += md[4 * modes + m] * cs + md[7 * modes + m] * sn;
            az += md[5 * modes + m] * cs + md[8 * modes + m] * sn;
        }
        float bx = bs[3 * e], by = bs[3 * e + 1], u = MathF.Sqrt(bx * bx + by * by);
        float us = sg[3 * e] * u * ax, ul = sg[3 * e + 1] * u * ay, uw = sg[3 * e + 2] * u * az;
        int k = kk[e];
        vel[k] = bx + us * sx - ul * sy;
        vel[n + k] = by + us * sy + ul * sx;
        vel[2 * n + k] = uw;
    }

    /// <summary><see cref="StepKernelDevice"/> with the wall model and the Sandbox body-force hook; devData is a dummy (Length &lt;= 3 count)
    /// when the grid has no devices. Lockstep with the CPU's <see cref="RunStep"/>.</summary>
    static void StepKernelWm(Index1D i, ArrayView<byte> cellType, ArrayView<byte> nearBoundary, ArrayView<int> dir,
        ArrayView<float> cur, ArrayView<float> nxt, GridDims dims, float tau0, byte accumulate, ArrayView<float> sums,
        ArrayView<float> devData, ArrayView<float> wm, WallModelArgs wa)
    {
        int idx = i;
        if (cellType[idx] != VoxelGrid.Air) return;
        int Nx = dims.Nx, Ny = dims.Ny, Nz = dims.Nz, count = dims.Count;
        int x = idx % Nx; int t = idx / Nx; int y = t % Ny; int z = t / Ny;
        if (x == 0 || x == Nx - 1 || y == 0 || y == Ny - 1 || (z == 0 && dims.OpenZMin != 0) || (z == Nz - 1 && dims.OpenZMax != 0)) return;

        F19 fin = (nearBoundary[idx] & 1) != 0
            ? GatherBoundarySoAWm(cellType, nearBoundary, cur, Nx, Ny, Nz, count, x, y, z, idx, dir, wm, wa)
            : GatherPlainSoA(cur, count, idx, Nx, Ny);

        tau0 = SpongeTau(tau0, x, y, Nx, Ny, dims.SpongeFaces, dims.SpongeCells);   // 6b: the outflow sponge (the cell's own base tau from here on)
        F19 fout; float rho, ux, uy, uz, tau;
        if (devData.Length > 3 * count && devData[3 * count + idx] != 0f)
        {
            DeviceForce(fin, devData[idx], devData[count + idx], devData[2 * count + idx], devData[3 * count + idx],
                out float fx, out float fy, out float fz);
            CollideForced(fin, tau0, fx, fy, fz, out fout, out rho, out ux, out uy, out uz, out tau);
        }
        else if (wa.ForceX != 0f) CollideForced(fin, tau0, wa.ForceX, 0f, 0f, out fout, out rho, out ux, out uy, out uz, out tau);
        else Collide(fin, tau0, out fout, out rho, out ux, out uy, out uz, out tau);
        if (dims.SpongeFaces != 0) fout = SpongeAbsorb(fout, rho, ux, uy, uz, x, y, Nx, Ny, dims.SpongeFaces, dims.SpongeCells);   // RZ-01
        WriteSoA(nxt, idx, count, fout);

        if (accumulate != 0)
        {
            sums[idx] += rho - 1f; sums[count + idx] += ux; sums[2 * count + idx] += uy; sums[3 * count + idx] += uz;
            sums[4 * count + idx] += (tau - tau0) / 3f;
            if (sums.Length > 5 * count) sums[5 * count + idx] = (tau - tau0) / 3f;   // 6k: this step's eddy viscosity (the extra plane exists only with RunOptions.OnlineAge)
        }
    }

    /// <summary>GPU twin of <see cref="WallBounce"/>; the two must stay in lockstep.</summary>
    static float WallBounceSoA(ArrayView<byte> cellType, ArrayView<byte> flags, ArrayView<float> cur,
        int Nx, int Ny, int Nz, int count, int i, int sIdx, int q, int opp, int cx, int cy, int cz, int x, int y, int z)
    {
        float bb = ReadComponent(cur, i, count, opp);
        int encoded = flags.Length > count ? flags[count + q * count + i] : 0;
        float qw = flags.Length > count ? (encoded == 0 ? -1f : (encoded - 1) / 254f) : LinkWallFraction(flags[i], flags[sIdx]);
        if (qw < 0f) return bb;
        if (qw >= 0.5f)
        {
            float a = 0.5f / qw;
            return a * bb + (1f - a) * ReadComponent(cur, i, count, q);
        }
        int fx = x + cx, fy = y + cy, fz = z + cz;
        if (fx < 0 || fx >= Nx || fy < 0 || fy >= Ny || fz < 0 || fz >= Nz) return bb;
        int fIdx = fx + Nx * (fy + Ny * fz);
        if (cellType[fIdx] == VoxelGrid.Solid || LinkCutSoA(flags, count, i, opp)) return bb;
        return 2f * qw * bb + (1f - 2f * qw) * ReadComponent(cur, fIdx, count, opp);
    }

    // ======================================================================================
    // Live session: the one implementation of "set up, step, read out". Solve() is a thin wrapper over it.
    // ======================================================================================

    /// <summary>A solve whose state stays resident between <see cref="Step"/> calls, so a desktop app can
    /// keep the solver advancing on a worker thread, pull the instantaneous velocity field at 5-15 Hz with
    /// <see cref="ReadVelocity"/> (allocation-free), turn the wind with <see cref="SetWind"/> and open or
    /// close a window with <see cref="UpdateCells"/> without throwing the flow away. <see cref="ToField"/>
    /// produces exactly what <see cref="Solve"/> returns.
    /// <para>THREAD-SAFETY: a session is used from ONE thread at a time -- there are no locks inside. A
    /// solver thread plus a UI thread is fine as long as the owner serialises the calls (e.g. do the
    /// ReadVelocity on the solver thread right after Step and publish the arrays); Step running concurrently
    /// with ReadVelocity/ToField/SetWind/UpdateCells is not supported. Two GPU sessions in one process also
    /// share the single process-wide accelerator and its default stream, so they must not step at the same
    /// time either.</para>
    /// <para>Averaging: <see cref="ToField"/> reports the mean over every step past the warm-up. SetWind and
    /// UpdateCells change the problem, so both reset that accumulator (the populations -- the flow itself --
    /// are kept); the field right after a change therefore averages very few samples.</para></summary>
    public sealed partial class LbmSession : IDisposable
    {
        // ---- inputs and the lattice scaling derived from them (fixed for the session's lifetime) ----
        // 6a (P12-01): _grid is what every LBM step reads, the cell-centre mask (VoxelGrid.LbmView); _source is the caller's grid with the
        // conservative Cell mask, which everything handed back for analysis keeps (ToField, the Read* outputs, snapshots, the room plans).
        VoxelGrid _grid, _source;
        bool _gpuCounted;   // RW-02: this session is counted in the cached accelerator's live-session count
        BuildingModel _model;
        Vector2 _wind;
        readonly bool _thermal, _useGpu, _inert, _nestedRing, _hybrid;   // _hybrid: ThermalScheme.Hybrid, see LbmHybridThermal.cs
        /// <summary>P17-07: this session steps on the GPU (a failure from it is a device failure, see <see cref="IsGpuFault"/>).</summary>
        public bool OnGpu => _useGpu;
        readonly string _backend = "";
        readonly float _dx, _profileExp, _profileTop, _latPerPhys, _dt, _tau0, _nuLat, _gLat, _alphaLat, _betaB, _tRef, _velScale, _pScale;
        readonly int _totalSteps, _warmupSteps;
        long _averageFromStep;
        // Preview-to-Refine warm start (PLAN_V2 2.2): non-null only for an isothermal run given one -- seeds
        // InitCpu/InitGpu's initial populations from this field's velocity instead of rest.
        readonly FlowField? _warmStart;
        readonly int _icSeed;                    // RunOptions.IcSeed (measurement seam): cold isothermal start only
        // RunOptions.MaxDegreeOfParallelism, applied only to the read-out passes (ToField): the step loop's
        // own Parallel.For is the throughput hot path and stays unthrottled.
        readonly ParallelOptions _readOutPo = new();

        // ---- run state ----
        BoundaryList _bl = new();
        int _spongeFaces, _spongeCells;   // 6b: the outflow sponge of the current ring (BuildRing), mirrored into _dims for the GPU
        byte[] _nearBoundary = [];
        float[] _T0 = [], _heatRate = [], _surfaceTemp = [], _surfaceCoef = [];
        int[] _surfaceFixed = [];
        // 6j (P14-01): held indoor temperature (RunOptions.HoldIndoorTemperature). Per controlled room (a room with an outline and air
        // cells, in model order): its cells, the PI state and the source the flow pass adds (K per step); _holdRoomOf per cell (-1 none).
        bool _hold, _surfaceResolved;
        float _holdSet;
        Room[] _holdRooms = [];
        int[][] _holdCells = [], _holdMeasure = [];   // per room: the source cells, and the cells whose mean is held
        int[] _holdRoomOf = [], _holdAll = [];
        float[] _holdQ = [], _holdT = [];
        double[] _holdWatts = [], _holdInt = [], _holdKp = [], _holdTau = [], _holdMax = [], _holdDutySum = [];
        long _holdDutySteps;
        MemoryBuffer1D<int, Stride1D.Dense>? _dHoldAll;
        MemoryBuffer1D<float, Stride1D.Dense>? _dHoldT;
        // Body-force devices: dense per-cell data (see BuildDeviceData), empty when the grid has none,
        // plus the tiny per-conditioning-unit table (supply temperature, current return difference) and the
        // cells each unit averages to get that difference.
        float[] _devData = [], _devUnit = [];
        int[] _unitCells = [], _unitStart = [];
        bool _hasDevices;
        OpeningModel _openingModel;
        float _nuFloor = NuLatMin;
        // Wall model (RunOptions.WallModel): smoothed slip vector per cell (3 floats, dense, only allocated when on). _wmPath = the
        // step goes through the wall-model kernel/arguments (model on, or a Sandbox test hook).
        bool _wmOn, _wmPath;
        WallModelArgs _wma;
        float[] _wm = [];
        MemoryBuffer1D<float, Stride1D.Dense>? _dWm;
        WindTurbulence? _windTu;       // RunOptions.WindTurbulence; null = off
        WindTurbulenceField? _tuField;
        BoundaryList? _tuFor;           // the boundary list the cached inflow entries below belong to
        int[] _tuK = [];                // ring entries of class BcInflow
        float[] _tuBase = [];           // their unperturbed velocity (3 per entry), lattice units
        float[] _tuPos = [];            // their lattice position (3 per entry), cells
        float[] _tuSig = [];            // their sigma_u/U, sigma_v/U, sigma_w/U (3 per entry)
        float _tuUc;                    // mean inflow speed of the entries (Taylor convection speed), lattice units
        BoundaryList? _tuGpuFor;        // the boundary list the device copies below belong to
        MemoryBuffer1D<int, Stride1D.Dense>? _dTuK;
        MemoryBuffer1D<float, Stride1D.Dense>? _dTuBase, _dTuPos, _dTuSig, _dTuModes, _dTuPh;
        float[] _tuPh = [];
        float _perturb;                 // RunOptions.SupplyPerturbation, rms fraction; 0 = off. Global override of every supply's own Tu
        bool _supplyTuPerSource;        // RunOptions.SupplyTuPerSource: each supply uses its AirSource.TurbulenceIntensity
        bool _anySupplyTu;              // some supply loop has Tu > 0 (set by BuildSupplyLoops)
        int _perturbSeed;
        double[] _sumRho = [], _sumUx = [], _sumUy = [], _sumUz = [], _sumNuT = [], _sumT = [];
        readonly List<Snapshot> _snapshots = new();
        int[] _snapSteps = [];
        int _snapIdx, _avgSamples, _foldedSamples;   // _foldedSamples: samples already folded from the device float sums into the host doubles (P17-01)
        long _steps;
        double _elapsed, _mlups;
        bool _disposed;
        List<SupplyLoop> _supply = new();

        // ---- CPU backend ----
        float[] _cur = [], _nxt = [], _curG = [], _nxtG = [];
        float[] _tCur = [], _tNxt = [], _hyb = [];   // ThermalScheme.Hybrid: T (double-buffered) and per-cell rho*,kappa,q for the FV pass

        // ---- GPU backend ----
        Accelerator? _acc;
        GridDims _dims;
        int[] _sampleIdx = [];
        MemoryBuffer1D<byte, Stride1D.Dense>? _dCellType, _dNearBoundary, _dBClass;
        MemoryBuffer1D<sbyte, Stride1D.Dense>? _dBNxNy;
        MemoryBuffer1D<int, Stride1D.Dense>? _dDir, _dBIndex, _dBNeighborSlot, _dSampleIdx, _dBad;
        MemoryBuffer1D<float, Stride1D.Dense>? _dCur, _dNxt, _dCurG, _dNxtG, _dSums, _dBVel,
            _dT0, _dCellData, _dSampleRho, _dUx, _dUy, _dUz, _dDevData, _dUnitT;
        MemoryBuffer1D<int, Stride1D.Dense>? _dUnitCells;

        /// <summary>Total steps a full <see cref="Solve"/> of this problem runs (warm-up + averaging).</summary>
        public int PlannedSteps => _totalSteps;
        /// <summary>Steps of warm-up before <see cref="ToField"/>'s time-average starts accumulating, so a
        /// caller can work out which physical interval that average actually covers.</summary>
        public int WarmupSteps => _warmupSteps;
        /// <summary>Steps advanced so far.</summary>
        public long Steps => _steps;
        /// <summary>Physical time simulated so far, seconds.</summary>
        public double SimSeconds => _steps * (double)_dt;
        /// <summary>Sandbox check: first supply patch's face statistics from the live wall velocities, as fractions of
        /// the mean wall speed: weighted mean and rms of the axial deviation and of the two tangential components.</summary>
        internal float[] SupplyFaceStats()
        {
            if (_supply.Count == 0) return [];
            var loop = _supply[0]; var bl = _bl; int n = bl.Index.Length, m = loop.Entries.Length;
            var mean = new double[3]; var sq = new double[3]; double sw = 0, swT = 0, speed = 0;   // swT: the entries that carry the tangential streams (RP-02: not the rim)
            for (int e = 0; e < m; e++)
            {
                int k = loop.Entries[e]; double w = loop.EntryUnit[e];
                float bx = loop.Base[3 * e], by = loop.Base[3 * e + 1], bz = loop.Base[3 * e + 2];
                float len = MathF.Sqrt(bx * bx + by * by + bz * bz); if (len < 1e-12f) continue;
                float vx = bl.Vel[k], vy = bl.Vel[n + k], vz = bl.Vel[2 * n + k];
                double ax = (vx * bx + vy * by + vz * bz) / len - loop.Scale * len;
                double t1 = vx * loop.Tan1[3 * e] + vy * loop.Tan1[3 * e + 1] + vz * loop.Tan1[3 * e + 2];
                double t2 = vx * loop.Tan2[3 * e] + vy * loop.Tan2[3 * e + 1] + vz * loop.Tan2[3 * e + 2];
                mean[0] += w * ax; sq[0] += w * ax * ax;
                if (!loop.Rim[e]) { mean[1] += w * t1; mean[2] += w * t2; sq[1] += w * t1 * t1; sq[2] += w * t2 * t2; swT += w; }
                sw += w; speed += w * loop.Scale * len;
            }
            if (sw <= 0 || speed <= 0) return [];
            double u = speed / sw, tw = Math.Max(swT, 1e-30);
            return [(float)(mean[0] / sw / u), (float)(mean[1] / tw / u), (float)(mean[2] / tw / u),
                    (float)(Math.Sqrt(sq[0] / sw) / u), (float)(Math.Sqrt(sq[1] / tw) / u), (float)(Math.Sqrt(sq[2] / tw) / u)];
        }

        /// <summary>Backend actually in use, e.g. "CUDA · NVIDIA GeForce RTX 5070 Ti".</summary>
        public string Backend => _backend;
        /// <summary>Throughput of the most recent <see cref="Step"/> call, million cell updates per second.</summary>
        public double Mlups => _mlups;
        /// <summary>Time one lattice step advances, seconds. 0 for an inert (no-flow-driver) session.</summary>
        public float TimeStepSeconds => _dt;
        /// <summary>Kinematic viscosity in lattice units (NuPhysAir * dt / dx^2, floored at <see cref="ViscosityFloor"/>).
        /// 0 for an inert session.</summary>
        public float LatticeViscosity => _nuLat;
        /// <summary>Lattice velocity units per m/s (ULat / uMax) -- multiply a physical velocity by this to get
        /// the lattice-unit velocity the solver actually runs at. 0 for an inert session.</summary>
        public float VelocityScale => _latPerPhys;
        /// <summary>The BGK stability floor on lattice viscosity (<see cref="RunOptions.ViscosityFloor"/>) actually in effect
        /// for this session -- <see cref="LatticeViscosity"/> never reads below this.</summary>
        public float ViscosityFloor => _nuFloor;

        /// <param name="uMaxOverride">&gt; 0: use this velocity scale (m/s per ULat) instead of deriving one,
        /// and never go inert -- a nested fine box takes its coarse run's scale so that dt halves with Dx and
        /// lattice velocities and densities carry over unchanged.</param>
        /// <param name="nestedRing">Drop the far-field wind ring (Fixed patches stay): the caller writes the
        /// domain faces itself, before every step.</param>
        /// <param name="warmStart">Preview-to-Refine warm start, see <see cref="LbmSolver.Start"/>.</param>
        internal LbmSession(VoxelGrid grid, BuildingModel model, Vector2 wind, RunOptions options,
            float uMaxOverride = 0f, bool nestedRing = false, FlowField? warmStart = null)
        {
            _source = grid; _grid = grid = grid.LbmView(); _model = model; _wind = wind; _nestedRing = nestedRing;
            _warmStart = warmStart != null && !options.Thermal ? warmStart : null;
            _icSeed = options.IcSeed;
            if ((long)grid.Nx * grid.Ny * grid.Nz > MaxCells)
                throw new InvalidOperationException($"This grid has {(long)grid.Nx * grid.Ny * grid.Nz / 1e6:0.#} million cells; the solver can address at most {MaxCells / 1e6:0.#} million. Use a coarser cell size.");
            int count = grid.Count;

            float windSpeed = wind.Length();
            float maxFixedSpeed = 0f;
            // A Supply patch runs at the face velocity that carries its FlowRate through the cells actually
            // carved (SupplyAreaRatios), which is what the lattice has to be scaled for.
            var supplyArea = SupplyAreaRatios(grid, model);
            foreach (var fc in grid.FixedCells)
                maxFixedSpeed = MathF.Max(maxFixedSpeed, fc.Velocity.Length() * (supplyArea.TryGetValue(fc.Supply, out var ar) ? ar : 1f));
            // A body-force device drives the flow just as a Fixed patch does, so its fastest target is part
            // of the velocity scale (and counts as a flow driver below) -- miss it and ULat is assigned to
            // something slower, putting the device's own cells past the BGK stability ceiling.
            float maxDeviceSpeed = 0f;
            foreach (var d in grid.Devices)
                foreach (var v in d.Target) maxDeviceSpeed = MathF.Max(maxDeviceSpeed, v.Length());

            _thermal = options.Thermal;
            _hold = _thermal && options.HoldIndoorTemperature;
            _surfaceResolved = options.SurfaceExchange == SurfaceExchange.Resolved;
            _holdSet = model.Env.IndoorTemperature;
            if (options.MaxDegreeOfParallelism is int mdop && mdop > 0) _readOutPo.MaxDegreeOfParallelism = mdop;
            // Buoyancy alone (no wind, no Fixed cells) can still drive real flow -- an indoor/outdoor
            // temperature split, an enabled heat source, or occupants all count as "heat-driven" and must not
            // hit the early return below (which is otherwise correct for a genuinely still, source-free domain).
            bool heatDriven = _thermal && (model.Heat.Exists(h => h.Enabled) || model.Rooms.Exists(r => r.Occupants > 0)
                || MathF.Abs(model.Env.IndoorTemperature - model.Env.OutdoorTemperature) > 1e-6f);

            if (windSpeed < 1e-6f && maxFixedSpeed < 1e-6f && maxDeviceSpeed < 1e-6f && !heatDriven && uMaxOverride <= 0f)
            {
                // Nothing can drive a flow: no lattice, no steps, a zero field. Step() is a no-op.
                _inert = true;
                _backend = $"CPU · {System.Environment.ProcessorCount} threads";
                return;
            }

            // WindFlow.Core.Backend, not this class's own Backend property.
            _useGpu = options.Backend switch
            {
                WindFlow.Core.Backend.Gpu => GpuAvailable ? true : throw new InvalidOperationException("No GPU found"),
                WindFlow.Core.Backend.Cpu => false,
                _ => GpuAvailable,
            };
            // P17-07: a run that asked for a GPU and has none says so where its notes go (results, PDF, headless); a restart after a loss carries its own note.
            if (options.Backend != WindFlow.Core.Backend.Cpu && GpuFallbackNote is { } noGpu && !grid.Warnings.Contains(noGpu)) grid.Warnings.Add(noGpu);
            if (options.GpuLossNote is { } lost && !grid.Warnings.Contains(lost)) grid.Warnings.Add(lost);
            _hybrid = _thermal && options.ThermalScheme == ThermalScheme.Hybrid;
            _ageWanted = options.OnlineAge;   // 6k: the eddy-viscosity plane (GPU: one more sums plane, CPU: _instant) the online age reads
            if (_ageWanted && !_useGpu) _instant = new float[count];

            float dx = _dx = grid.Dx;
            float zGround = model.GroundZ;
            float zTop = grid.Origin.Z + (grid.Nz - 0.5f) * dx;
            _profileExp = options.WindProfileExponent;
            _profileTop = Profile(zTop, zGround, _profileExp);
            // uMax: the fastest speed anywhere in the domain (wind profile at the top, a faster mechanical
            // source, or -- when it's the largest of the three -- the natural-convection velocity scale
            // sqrt(g*beta*dTmax*H) below) -- ULat is assigned to it so every lattice velocity in the run stays
            // below the BGK stability ceiling, whichever effect turns out to dominate.
            float uMax = MathF.Max(MathF.Max(MathF.Max(windSpeed * _profileTop, maxFixedSpeed), maxDeviceSpeed), 1e-6f);

            _tRef = model.Env.OutdoorTemperature;
            float dtMaxAbs = 0f;
            bool[] isIndoor = [];
            if (_thermal)
            {
                (_T0, _heatRate, _surfaceFixed, _surfaceTemp, isIndoor, var unheldSurfaces, _surfaceCoef) = BuildThermal(grid, model, _surfaceResolved);
                _betaB = 1f / (_tRef + 273.15f);
                // buildH: the storeys' own height, not the padded domain height (which includes sky margin
                // above the roof for the wind profile even on a zero-wind run) -- a much more representative
                // convective length scale for a stack/room-height buoyancy problem.
                float buildH = model.Storeys.Count > 0
                    ? MathF.Max(model.Storeys.Max(s => s.Elevation + s.Height) - model.Storeys.Min(s => s.Elevation), dx)
                    : MathF.Max((grid.Nz - 1) * dx, dx);

                dtMaxAbs = MathF.Abs(model.Env.IndoorTemperature - _tRef);
                foreach (var h in model.Heat)
                    if (h.Enabled && h.Kind == HeatKind.Surface && !float.IsNaN(h.SurfaceTemperature) && !unheldSurfaces.Contains(h))   // RX-04
                        dtMaxAbs = MathF.Max(dtMaxAbs, MathF.Abs(h.SurfaceTemperature - _tRef));

                // Volumetric (People/Equipment/Lighting) sources have no explicit temperature, so dtMaxAbs alone
                // (indoor/outdoor split + Surface sources only) can stay ~0 even though they still heat the air
                // and drive real convection -- e.g. a sealed room + equipment box with indoor == outdoor.
                // Tried: a Deardorff/plume convective scale U ~ (g*beta*q''*H/(rho*cp))^(1/3) off the building's
                // real plan footprint (Voxelizer.PlanBounds) instead of grid.Nx*grid.Ny*dx*dx (which also counts
                // the >= 8 m sky/side margin the voxelizer always pads the domain with -- the original diluted
                // area was the zero-wind divergence bug). That fixed the dilution, and is the physically better
                // reference (the run's total simulated time is a FIXED number of convective turnovers of uMax,
                // so an unrealistically fast uMax undersimulates real seconds -- see the "Thermal LBM trap" note
                // in the implement agent's memory, dT=0.01 K in the "heat" scene instead of the physical
                // >= 0.3 K), but at gLat ~ 2.6 (vs ~0.056 with the 3 K floor below) it reintroduces a *different*
                // divergence within ~800 steps: once local dT exceeds ~1 K (routine near a 500 W box) FLatMax
                // saturates, and a *sustained* capped force integrated over hundreds of steps at tau0 ~ 0.5015
                // (near-inviscid) still pumps velocity past the RhoMin/RhoMax bound -- capping the per-step
                // force bounds the force, not the cumulative momentum a near-inviscid lattice keeps all of.
                // Properly fixing that needs either real per-step viscous relief (a higher NuLatMin, which risks
                // the cavity-benchmark Nu accuracy it's tuned against) or a different scaling approach entirely;
                // left as a known accuracy gap rather than reintroducing a stability regression -- see this
                // implement pass's report/ledger. Floor dtMaxAbs at a conservative, empirically-stable 3 K
                // plume estimate instead, whenever any Watts-emitting source (or occupant) is enabled.
                bool anyWatts = model.Heat.Exists(h => h.Enabled && h.Kind != HeatKind.Surface
                        && (h.Watts > 1e-6f || (h.Kind == HeatKind.People && h.People > 0)))
                    || model.Rooms.Exists(r => r.Occupants > 0);
                if (anyWatts) dtMaxAbs = MathF.Max(dtMaxAbs, 3f);

                if (dtMaxAbs > 1e-6f)
                    uMax = MathF.Max(uMax, MathF.Sqrt(GAccel * _betaB * dtMaxAbs * buildH));
            }

            if (uMaxOverride > 0f) uMax = uMaxOverride;
            _latPerPhys = ULat / uMax;
            _dt = ULat * dx / uMax;
            _nuFloor = options.ViscosityFloor > 0f ? options.ViscosityFloor : NuLatMin;
            _nuLat = MathF.Max(NuPhysAir * _dt / (dx * dx), _nuFloor);
            _tau0 = 0.5f + 3f * _nuLat;
            // Wall model: NuP is the PHYSICAL viscosity (before the floor), NuW the floored one the node actually relaxes with.
            _wmOn = options.WallModel == WallModel.WernerWengle && !_thermal;
            _wma = new WallModelArgs { NuP = _wmOn ? NuPhysAir * _dt / (dx * dx) : 0f, NuW = _nuLat,
                ForceX = _thermal ? 0f : options.TestBodyForceX, Periodic = !_thermal && options.TestPeriodicX ? 1 : 0 };
            _wmPath = _wmOn || _wma.ForceX != 0f || _wma.Periodic != 0;
            _gLat =GAccel * _dt * _dt / dx;
            _alphaLat = (NuPhysAir / Pr) * _dt / (dx * dx);
            _velScale = uMax / ULat;
            _pScale = (1f / 3f) * (dx / _dt) * (dx / _dt) * 1.2f;
            if (_thermal && System.Environment.GetEnvironmentVariable("WF_THERMAL_DEBUG") == "1")
                Console.Error.WriteLine($"[dbg0] Nx={grid.Nx} Ny={grid.Ny} Nz={grid.Nz} dx={dx:F4} uMax={uMax:F6} dt={_dt:F6} tau0={_tau0:F6} gLat={_gLat:F6} alphaLatMolecular={_alphaLat:F6} betaB={_betaB:F6} dtMaxAbs={dtMaxAbs:F4}");

            int L = Math.Max(grid.Nx, grid.Ny);
            float flowThrough = L / ULat;
            // Buoyancy-driven cavities/rooms need several turnovers of the (now buoyancy-aware) uMax to settle
            // into a steady Nusselt number, not the ~1 flow-through a wind-driven external flow needs.
            float warmupMul = _thermal ? ThermalWarmupFlowThroughs : WarmupFlowThroughs;
            float avgMul = _thermal ? ThermalAverageFlowThroughs : AverageFlowThroughs;
            int stepCap = options.Ultra != null ? int.MaxValue / 4 : _thermal ? ThermalMaxSteps : MaxSteps;   // Ultra: the plain-solve ceiling would cut the settle of a very large grid short
            int warmupSteps = (int)Math.Ceiling(warmupMul * flowThrough);
            int averageSteps = (int)Math.Ceiling(avgMul * flowThrough);
            _totalSteps = Math.Min(warmupSteps + averageSteps, stepCap);
            _warmupSteps = Math.Min(warmupSteps, _totalSteps);

            // Watts/People sources inject a slow, continuous volumetric heat rate (K/s = P/(rho*cp*V)) every
            // step; the turnover-count budget above is tuned for wind/buoyancy-driven AIRFLOW to develop, not
            // for that heat to actually raise the room's mean temperature by a visible amount, which can take
            // 1-2 REAL minutes even though uMax/dt (deliberately, see FLatMax's comment -- do not change this
            // to "fix" it, it diverges) only covers a handful of convective turnovers of physical time. Extend
            // (never shorten -- with real wind or a real indoor/outdoor split the turnover count above already
            // dominates, so this only ever raises _totalSteps) so SimSeconds reaches a thermal development
            // time: the time for the room-mean T to rise TargetRiseK at the injected rate, from a bulk
            // energy balance t = rho*cp*V*dT/P. (An earlier pass needed 3x this to get the sandbox's "heat"
            // scene to stratify by the 0.3 K its check wants; that was a symptom of the regularized-collision
            // force deficit -- see swOdd in CollideForced -- which made every buoyant scene's effective
            // gravity ~0.503 g. With the force correct, 0.5 K of bulk rise is enough again.)
            if (_thermal)
            {
                float totalWatts = 0f;
                foreach (var h in model.Heat)
                    if (h.Enabled && h.Kind != HeatKind.Surface)
                        totalWatts += h.Kind == HeatKind.People && h.Watts <= 0f ? PeopleWatts * h.People : h.Watts;
                foreach (var room in model.Rooms)
                    if (room.Occupants > 0) totalWatts += PeopleWatts * room.Occupants;
                if (totalWatts > 1e-6f)
                {
                    // The building's own indoor air (BuildThermal's isIndoor), not every Air cell in the
                    // grid: that would also count the >= 8 m padded exterior domain, inflating the "room"
                    // volume by ~50x for a typical scene and pinning thermalDevSeconds at the clamp's upper
                    // bound regardless of the actual room size.
                    long indoorAirCells = 0;
                    for (int i = 0; i < count; i++) if (isIndoor[i] && grid.Cell[i] == VoxelGrid.Air) indoorAirCells++;
                    float analysedVolume = MathF.Max(indoorAirCells * dx * dx * dx, dx * dx * dx);
                    float thermalDevSeconds = Math.Clamp(ThermalRiseK * RhoCp * analysedVolume / totalWatts,
                        ThermalDevMinSeconds, ThermalDevMaxSeconds);
                    int thermalSteps = (int)Math.Ceiling(thermalDevSeconds / _dt);
                    _totalSteps = Math.Min(Math.Max(_totalSteps, thermalSteps), stepCap);
                    _warmupSteps = Math.Min(_warmupSteps, _totalSteps);
                }
            }

            _nearBoundary = ComputeCellFlags(grid);
            BuildRing(grid, wind);
            if (_thermal) BuildHold();
            _perturb = MathF.Max(0f, options.SupplyPerturbation);
            _supplyTuPerSource = options.SupplyTuPerSource;
            _perturbSeed = options.PerturbationSeed;
            if (options.WindTurbulence is { Active: true } wt)
            {
                _windTu = wt;
                _tuField = new WindTurbulenceField(wt.Modes, wt.LengthScale / dx, options.PerturbationSeed);
            }
            _supply = BuildSupplyLoops(null);
            _openingModel = options.OpeningModel;
            (_devData, _unitCells, _unitStart, _devUnit) = BuildDeviceData(grid, _latPerPhys, _dt, _thermal,
                OpeningLossCells(grid, model, _openingModel, _nuFloor), _thermal ? PlumeDriveCells(grid, model) : null);
            _hasDevices = _devData.Length > 0;

            _sumRho = new double[count]; _sumUx = new double[count]; _sumUy = new double[count]; _sumUz = new double[count];
            _sumNuT = new double[count]; _sumT = _thermal ? new double[count] : [];
            SetupRoomFlux();
            _averageFromStep = _warmupSteps;
            _snapSteps = options.SnapshotCount > 0 ? EvenSteps(_warmupSteps, _totalSteps, options.SnapshotCount) : [];

            if (_useGpu)
            {
                try   // RW-02: a failure from the device calls is a GPU fault (the run restarts on the CPU); the memory check's own out-of-memory is not
                {
                    EnsureGpu();
                    if (_accel == null) throw new InvalidOperationException("No GPU found");
                    CheckGpuMemory(grid, _thermal, _fluxPlan.Faces.Length, options.Ultra != null ? UltraGpuMemFractionLimit : GpuMemFractionLimit);
                    if (_hybrid) EnsureGpuHybrid(); else if (_thermal) EnsureGpuThermal();
                    _acc = _accel;
                    try { GpuInitTestHook?.Invoke(); InitGpu(); }
                    catch (Exception ex) when (IsGpuOutOfMemory(ex))   // P17-07: say so and suggest less, never a silent CPU run
                    { throw new GpuOutOfMemoryException($"The GPU ran out of memory ({ShortReason(ex)}). Choose a lower quality level or a smaller domain.", ex); }
                    _backend = $"{_gpuKind} · {_acc.Name}";
                }
                catch (Exception ex) when (IsGpuCallFailure(ex)) { throw new GpuFaultException(ex); }
                AddGpuSession(); _gpuCounted = true;
            }
            else
            {
                InitCpu();
                _backend = $"CPU · {System.Environment.ProcessorCount} threads";
            }
        }

        // ---- initial state ---------------------------------------------------------------------

        /// <summary>The IC-seed seam: per Air cell (cell-index order) the initial velocity perturbation, 3 floats a cell in lattice units, each uniform in
        /// +-1e-3 x ULat from System.Random(seed); null when off. Built host-side once so the CPU and GPU starts are the same field.</summary>
        float[]? IcPerturbation()
        {
            if (_icSeed <= 0) return null;
            var du = new float[_grid.Count * 3];
            var rng = new Random(_icSeed);
            float a = 1e-3f * ULat;
            for (int i = 0; i < _grid.Count; i++)
            {
                if (_grid.Cell[i] != VoxelGrid.Air) continue;
                for (int c = 0; c < 3; c++) du[3 * i + c] = (float)((2 * rng.NextDouble() - 1) * a);
            }
            return du;
        }

        void InitCpu()
        {
            var grid = _grid;
            int count = grid.Count;
            _cur = new float[count * Q]; _nxt = new float[count * Q];
            var cur = _cur;
            F19 feqInit = ComputeFeq(1f, 0f, 0f, 0f);
            if (_hybrid)
            {
                InitHybridCpu(feqInit);
                UpdateDeviceUnits();
                UpdateHold(0f);
            }
            else if (_thermal)
            {
                _curG = new float[count * Q7]; _nxtG = new float[count * Q7];
                var curG = _curG; var T0 = _T0;
                Parallel.For(0, count, i =>
                {
                    if (grid.Cell[i] == VoxelGrid.Solid) return;
                    WriteAoS(cur, i, feqInit);
                    WriteAoSG(curG, i, ComputeGeq(T0[i], 0f, 0f, 0f));
                });
                SetBoundaryCpu(cur, _bl, 0f);
                SetBoundaryThermalRingCpu(curG, _bl, _tRef);
                UpdateDeviceUnits();
                UpdateHold(0f);
            }
            else if (_warmStart != null)
            {
                // Seed at equilibrium for the coarser run's velocity there instead of rest: the flow only
                // has to relax onto this grid's finer detail, not develop from nothing.
                var warm = _warmStart; float latPerPhys = _latPerPhys;
                Parallel.For(0, count, i =>
                {
                    if (grid.Cell[i] != VoxelGrid.Air) return;
                    var u = SampleVelocity(warm, CellCenter(grid, i));
                    WriteAoS(cur, i, ComputeFeq(1f, u.X * latPerPhys, u.Y * latPerPhys, u.Z * latPerPhys));
                });
                SetBoundaryCpu(cur, _bl, 0f);
            }
            else
            {
                var du = IcPerturbation();
                Parallel.For(0, count, i =>
                {
                    if (grid.Cell[i] != VoxelGrid.Air) return;
                    WriteAoS(cur, i, du == null ? feqInit : ComputeFeq(1f, du[3 * i], du[3 * i + 1], du[3 * i + 2]));
                });
                SetBoundaryCpu(cur, _bl, 0f);
            }
            // Boundary cells (ring, Fixed, Supply) are written into whichever buffer is current and never streamed,
            // so the other buffer must start as a copy: after each swap the boundary cells there are read (live
            // velocity, snapshots, the thermal gather off Fixed cells) before this step's update rewrites them.
            Array.Copy(_cur, _nxt, _cur.Length);
            if (_thermal && !_hybrid) Array.Copy(_curG, _nxtG, _curG.Length);
            if (_wmOn) _wm = new float[3 * count];
        }

        void InitGpu()
        {
            var accel = _acc!;
            var grid = _grid;
            int count = grid.Count;
            _dims = new GridDims { Nx = grid.Nx, Ny = grid.Ny, Nz = grid.Nz, Count = count, OpenZMin = grid.OpenZMin ? 1 : 0, OpenZMax = grid.OpenZMax ? 1 : 0,
                SpongeFaces = _spongeFaces, SpongeCells = _spongeCells };

            _dCellType = accel.Allocate1D(grid.Cell);
            _dNearBoundary = accel.Allocate1D(_nearBoundary);
            _dCur = accel.Allocate1D<float>((long)count * Q);
            _dNxt = accel.Allocate1D<float>((long)count * Q);
            _dSums = accel.Allocate1D<float>((long)count * SumPlanes);   // rho,ux,uy,uz,nuT[,T]
            _dSums.View.MemSetToZero();
            AllocateRoomFluxGpu();

            if (_hybrid)
            {
                // no D3Q7 buffers: T and the FV planes are allocated by InitHybridGpu
                _dDir = accel.Allocate1D(DirLookupHost);
                _dT0 = accel.Allocate1D(_T0);
                _dCellData = accel.Allocate1D(BuildCellData());
            }
            else if (_thermal)
            {
                _dCurG = accel.Allocate1D<float>((long)count * Q7);
                _dNxtG = accel.Allocate1D<float>((long)count * Q7);
                var combinedDir = new int[DirLookupHost.Length + GDirLookupHost.Length];
                Array.Copy(DirLookupHost, combinedDir, DirLookupHost.Length);
                Array.Copy(GDirLookupHost, 0, combinedDir, DirLookupHost.Length, GDirLookupHost.Length);
                _dDir = accel.Allocate1D(combinedDir);
                _dT0 = accel.Allocate1D(_T0);
                _dCellData = accel.Allocate1D(BuildCellData());
            }
            else _dDir = accel.Allocate1D(DirLookupHost);

            // A thermal run carries the device slots inside cellData (see BuildCellData); a wind-only run
            // needs its own buffer and the device step kernel.
            if (_hasDevices && !_thermal)
            {
                EnsureGpuDevice();
                _dDevData = accel.Allocate1D(_devData);
            }
            if (_wmPath)
            {
                EnsureGpuWm();
                _dWm = accel.Allocate1D<float>(_wmOn ? 3L * count : 1L);
                _dWm.View.MemSetToZero();
                if (!_hasDevices) _dDevData = accel.Allocate1D<float>(1L);   // dummy: StepKernelWm reads devData only when Length > 3 count
            }
            if (_unitCells.Length > 0)
            {
                _dUnitCells = accel.Allocate1D(_unitCells);
                _dUnitT = accel.Allocate1D<float>(_unitCells.Length);
            }
            if (_thermal) AllocateHoldGpu();

            AllocateBoundaryGpu();

            _sampleIdx = BuildDivergenceSampleIndices(grid);
            _dSampleIdx = accel.Allocate1D(_sampleIdx.Length > 0 ? _sampleIdx : new[] { 0 });
            _dSampleRho = accel.Allocate1D<float>(Math.Max(1, _sampleIdx.Length));

            int n = _bl.Index.Length, ringCount = n - _bl.FixedCount;
            if (_warmStart != null)
            {
                // Built host-side and uploaded rather than a new GPU kernel (see LbmSolver.SampleVelocity) --
                // this only runs once, at session construction.
                var hostCur = new float[count * Q];
                var warm = _warmStart; float latPerPhys = _latPerPhys; var g = grid;
                Parallel.For(0, count, i =>
                {
                    if (g.Cell[i] != VoxelGrid.Air) return;
                    var u = SampleVelocity(warm, CellCenter(g, i));
                    // SoA, the layout _dCur holds (InitKernel's WriteSoA): [q*count + i], not the CPU's AoS [i*Q + q].
                    WriteSoAHost(hostCur, i, count, ComputeFeq(1f, u.X * latPerPhys, u.Y * latPerPhys, u.Z * latPerPhys));
                });
                _dCur.View.CopyFromCPU(hostCur);
            }
            else if (!_thermal && !_hybrid && IcPerturbation() is { } du)
            {
                // IC-seed seam: the same host-built perturbation as InitCpu, uploaded SoA like the warm start above.
                var hostCur = new float[count * Q];
                var g = grid;
                Parallel.For(0, count, i =>
                {
                    if (g.Cell[i] != VoxelGrid.Air) return;
                    WriteSoAHost(hostCur, i, count, ComputeFeq(1f, du[3 * i], du[3 * i + 1], du[3 * i + 2]));
                });
                _dCur.View.CopyFromCPU(hostCur);
            }
            else _initKernel!(count, _dCellType.View, _dCur.View, count);
            if (_thermal && !_hybrid) _initKernelG!(count, _dCellType.View, _dCurG!.View, _dT0!.View, count);
            if (n > 0)
                _boundaryKernel!(n, _dBIndex!.View, _dBClass!.View, _dBVel!.View, _dBNxNy!.View, _dBNeighborSlot!.View,
                    _dCur.View, count, 0f, n);
            if (_thermal && !_hybrid && ringCount > 0)
                _boundaryKernelThermalG!(ringCount, _dBIndex!.View, _dBClass!.View, _dBVel!.View, _dBNeighborSlot!.View,
                    _dCurG!.View, count, _tRef, n);
            if (_hybrid) InitHybridGpu();
            // Same as InitCpu: the second buffer starts as a copy (an ILGPU allocation is not zeroed).
            _dNxt.View.CopyFrom(_dCur.View);
            if (_thermal && !_hybrid) _dNxtG!.View.CopyFrom(_dCurG!.View);
            accel.Synchronize();
            if (_thermal) { UpdateDeviceUnits(); UpdateHold(0f); }
        }

        /// <summary>cellData: [0,count)=heatRate, [count,2count)=surfaceFixed (the Surface face mask and source index, <see cref="BuildThermal"/>,
        /// exact in a float), [2count,3count)=surfaceTemp, then <see cref="BuildDeviceData"/>'s six device slots at [3count, 9count) and the
        /// unit table when the grid has devices; then (6j, held runs) the controlled room of every cell as a float (-1 none, count long) and
        /// the per-room source (K per step), then (6e) the Surface table, <see cref="SurfaceStride"/> floats an entry. One buffer, because StepKernelThermal is
        /// already at ILGPU's comfortable argument limit (see its doc comment); the offsets go to the kernel in ThermalParams.</summary>
        float[] BuildCellData()
        {
            int count = _grid.Count;
            int tail = count * (_hasDevices ? 9 : 3) + (_hasDevices ? _devUnit.Length : 0);
            bool hold = _holdRooms.Length > 0;
            _cdHoldPlane = hold ? tail : -1;
            _cdHoldTable = tail + (hold ? count : 0);
            _cdSurfTable = _cdHoldTable + _holdQ.Length;
            var cellData = new float[_cdSurfTable + _surfaceCoef.Length];
            Array.Copy(_heatRate, 0, cellData, 0, count);
            for (int i = 0; i < count; i++) cellData[count + i] = _surfaceFixed[i];
            Array.Copy(_surfaceTemp, 0, cellData, 2 * count, count);
            if (_hasDevices)
            {
                Array.Copy(_devData, 0, cellData, 3 * count, _devData.Length);
                Array.Copy(_devUnit, 0, cellData, 9 * count, _devUnit.Length);
            }
            if (hold)
            {
                for (int i = 0; i < count; i++) cellData[_cdHoldPlane + i] = _holdRoomOf[i];
                Array.Copy(_holdQ, 0, cellData, _cdHoldTable, _holdQ.Length);
            }
            Array.Copy(_surfaceCoef, 0, cellData, _cdSurfTable, _surfaceCoef.Length);
            return cellData;
        }

        /// <summary>The thermal kernel's constants (6e/6j additions: Surface convection scale and the cellData offsets).</summary>
        ThermalParams KernelThermalParams() => new()
        {
            Tau0 = _tau0, AlphaLatMolecular = _alphaLat, BetaB = _betaB, GLat = _gLat, TRef = _tRef, DtSeconds = _dt,
            DevUnits = _devUnit.Length / 2, VelScale = _velScale, SurfacePerStep = _dt / (RhoCp * _dx),
            SurfaceTable = _cdSurfTable, HoldPlane = _cdHoldPlane, HoldTable = _cdHoldTable,
        };

        /// <summary>(Re)uploads the boundary list. Its length changes whenever the ring or the Fixed-cell set
        /// changes, so these buffers are reallocated rather than overwritten.</summary>
        void AllocateBoundaryGpu()
        {
            var accel = _acc!;
            _dBIndex?.Dispose(); _dBClass?.Dispose(); _dBVel?.Dispose(); _dBNxNy?.Dispose();
            _dBNeighborSlot?.Dispose();
            // A fully sealed domain (every ring cell solid, no Fixed cells -- e.g. a hand-built room driven
            // only by a device or by buoyancy) has an EMPTY boundary list, and ILGPU throws on a zero-length
            // allocation. One dummy element each; every kernel launch over them is already guarded by
            // n > 0 / ringCount > 0, so nothing ever reads them.
            _dBIndex = accel.Allocate1D(_bl.Index.Length > 0 ? _bl.Index : new int[1]);
            _dBClass = accel.Allocate1D(_bl.Class.Length > 0 ? _bl.Class : new byte[1]);
            _dBVel = accel.Allocate1D(_bl.Vel.Length > 0 ? _bl.Vel : new float[1]);
            _dBNxNy = accel.Allocate1D(_bl.NxNy.Length > 0 ? _bl.NxNy : new sbyte[1]);
            _dBNeighborSlot = accel.Allocate1D(_bl.NeighborSlot.Length > 0 ? _bl.NeighborSlot : new int[1]);
        }

        // ---- stepping --------------------------------------------------------------------------

        /// <summary>Advances the solve by <paramref name="steps"/> steps. Throws the same
        /// InvalidOperationException ("Airflow solver diverged...") a batch solve throws if the run blows up,
        /// and OperationCanceledException on <paramref name="ct"/>.</summary>
        public void Step(int steps, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_inert || steps <= 0) return;
            ct.ThrowIfCancellationRequested();

            var sw = Stopwatch.StartNew();
            if (_useGpu)
            {
                try   // RW-02
                {
                    GpuStepTestHook?.Invoke();
                    if (_thermal) StepGpuThermal(steps, ct); else StepGpu(steps, ct);
                }
                catch (Exception ex) when (IsGpuCallFailure(ex)) { throw new GpuFaultException(ex); }
            }
            else
            {
                if (_thermal) StepCpuThermal(steps, ct); else StepCpu(steps, ct);
            }
            sw.Stop();

            double seconds = sw.Elapsed.TotalSeconds;
            _elapsed += seconds;
            if (seconds > 0) _mlups = (double)_grid.Count * steps / seconds / 1e6;
        }

        /// <summary>Nested boxes: <see cref="Step"/> without the closing device synchronisation on the
        /// isothermal GPU path, so a nested coarse step queues its coarse step, refresh and two fine steps on
        /// one stream with no host round trip. The periodic GpuSyncCadence and divergence syncs still happen.
        /// Timing (Mlups) is left to the caller.</summary>
        internal void StepQueued(int steps, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_inert || steps <= 0) return;
            if (_useGpu && !_thermal)
            {
                ct.ThrowIfCancellationRequested();
                try { StepGpu(steps, ct, finalSync: false); }
                catch (Exception ex) when (IsGpuCallFailure(ex)) { throw new GpuFaultException(ex); }   // RW-02
            }
            else Step(steps, ct);
        }

        void StepCpu(int steps, CancellationToken ct)
        {
            var grid = _grid;
            for (int s = 0; s < steps; s++)
            {
                int step = (int)_steps;
                if (step % ProgressCadence == 0) ct.ThrowIfCancellationRequested();

                float ramp = MathF.Min(1f, step / (float)RampSteps);
                SupplyStep(); WindTurbulenceStep(step);
                SetBoundaryCpu(_cur, _bl, ramp);
                if (_wma.Periodic != 0) PeriodicXCpu(grid, _cur);

                bool accumulate = step >= _averageFromStep;
                if (accumulate) _avgSamples++;

                RunStep(grid, _nearBoundary, _cur, _nxt, _tau0, accumulate, _sumRho, _sumUx, _sumUy, _sumUz, _sumNuT,
                    _hasDevices ? _devData : null, _instant, _wmOn ? _wm : null, _wma.NuP, _wma.NuW, _wma.ForceX, _spongeFaces, _spongeCells);
                AgeStep(accumulate);
                if (accumulate) AccumulateRoomFluxCpu();
                (_cur, _nxt) = (_nxt, _cur);

                if (step % DivergenceCheckInterval == 0)
                    CheckDivergence(grid, _cur, FullDivergenceStep(step));

                if (_snapIdx < _snapSteps.Length && step == _snapSteps[_snapIdx])
                {
                    _snapshots.Add(CaptureCpu(_source, _cur, _velScale, null, (step - _warmupSteps + 1) * _dt));
                    _snapIdx++;
                }
                _steps++;
            }
        }

        void StepCpuThermal(int steps, CancellationToken ct)
        {
            if (_hybrid) { StepCpuHybrid(steps, ct); return; }
            var grid = _grid;
            for (int s = 0; s < steps; s++)
            {
                int step = (int)_steps;
                if (step % ProgressCadence == 0) ct.ThrowIfCancellationRequested();

                float ramp = MathF.Min(1f, step / (float)RampSteps);
                // Heat sources ramp in over half the warmup phase (much slower than the wind ramp): injecting
                // the full Watts instantly, before any convective current has had time to develop, spikes the
                // source cells' own T (and so the Boussinesq force there) far out of the weakly-compressible
                // regime in just a few steps and diverges.
                float heatRamp = MathF.Min(1f, step / MathF.Max(1f, _warmupSteps / 2f));
                SupplyStep(); WindTurbulenceStep(step);
                SetBoundaryCpu(_cur, _bl, ramp);
                SetBoundaryThermalRingCpu(_curG, _bl, _tRef);

                bool accumulate = step >= _averageFromStep;
                if (accumulate) _avgSamples++;

                if (_devUnit.Length > 0 && step % DeviceUnitCadence == 0) UpdateDeviceUnits();
                HoldStep(step, accumulate);

                RunStepThermal(grid, _nearBoundary, _cur, _nxt, _curG, _nxtG, _tau0, _alphaLat, _betaB, _gLat, _tRef,
                    _heatRate, _surfaceFixed, _surfaceTemp, _surfaceCoef, _dt / (RhoCp * _dx), _velScale, _holdRoomOf, _holdQ, _dt, heatRamp, accumulate,
                    _sumRho, _sumUx, _sumUy, _sumUz, _sumNuT, _sumT, _hasDevices ? _devData : null, _devUnit, _instant, spongeFaces: _spongeFaces, spongeCells: _spongeCells);
                AgeStep(accumulate);
                if (accumulate) AccumulateRoomFluxCpu();
                (_cur, _nxt) = (_nxt, _cur);
                (_curG, _nxtG) = (_nxtG, _curG);

                if (step % DivergenceCheckInterval == 0 || (step < 20 && System.Environment.GetEnvironmentVariable("WF_THERMAL_DEBUG") == "1"))
                {
                    if (System.Environment.GetEnvironmentVariable("WF_THERMAL_DEBUG") == "1")
                    {
                        float maxT = float.MinValue, minT = float.MaxValue, maxSpeed = 0f;
                        int minI = -1, maxI = -1;
                        for (int ii = 0; ii < grid.Count; ii++)
                        {
                            if (grid.Cell[ii] == VoxelGrid.Solid) continue;
                            int b = ii * Q7;
                            float t = _curG[b] + _curG[b + 1] + _curG[b + 2] + _curG[b + 3] + _curG[b + 4] + _curG[b + 5] + _curG[b + 6];
                            if (t > maxT) { maxT = t; maxI = ii; } if (t < minT) { minT = t; minI = ii; }
                            if (grid.Cell[ii] == VoxelGrid.Air)
                            {
                                NeighborMacroscopic(_cur, ii, out float vx, out float vy, out float vz);
                                float sp = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
                                if (sp > maxSpeed) maxSpeed = sp;
                            }
                        }
                        string Loc(int idx) => idx < 0 ? "?" : $"({idx % grid.Nx},{idx / grid.Nx % grid.Ny},{idx / (grid.Nx * grid.Ny)}) cell={grid.Cell[idx]}";
                        Console.Error.WriteLine($"[dbg] step={step} heatRamp={heatRamp:F3} minT={minT:F2}@{Loc(minI)} maxT={maxT:F2}@{Loc(maxI)} maxSpeedLat={maxSpeed:F4}");
                    }
                    CheckDivergence(grid, _cur, FullDivergenceStep(step));
                }

                if (_snapIdx < _snapSteps.Length && step == _snapSteps[_snapIdx])
                {
                    _snapshots.Add(CaptureCpu(_source, _cur, _velScale, _curG, (step - _warmupSteps + 1) * _dt));
                    _snapIdx++;
                }
                _steps++;
            }
        }

        void StepGpu(int steps, CancellationToken ct, bool finalSync = true)
        {
            var accel = _acc!;
            int count = _grid.Count, n = _bl.Index.Length;
            for (int s = 0; s < steps; s++)
            {
                int step = (int)_steps;
                float ramp = MathF.Min(1f, step / (float)RampSteps);
                if (SupplyStep()) _dBVel!.View.CopyFromCPU(_bl.Vel);
                WindTurbulenceGpu(step);
                if (n > 0)
                    _boundaryKernel!(n, _dBIndex!.View, _dBClass!.View, _dBVel!.View, _dBNxNy!.View, _dBNeighborSlot!.View,
                        _dCur!.View, count, ramp, n);

                bool accumulate = step >= _averageFromStep;
                if (accumulate) _avgSamples++;

                if (_wmPath)
                {
                    if (_wma.Periodic != 0) _periodicXKernel!(_grid.Ny * _grid.Nz, _dCur!.View, _dims);
                    _stepKernelWm!(count, _dCellType!.View, _dNearBoundary!.View, _dDir!.View, _dCur!.View, _dNxt!.View,
                        _dims, _tau0, (byte)(accumulate ? 1 : 0), _dSums!.View, _dDevData!.View, _dWm!.View, _wma);
                }
                else if (_hasDevices)
                    _stepKernelDevice!(count, _dCellType!.View, _dNearBoundary!.View, _dDir!.View, _dCur!.View, _dNxt!.View,
                        _dims, _tau0, (byte)(accumulate ? 1 : 0), _dSums!.View, _dDevData!.View);
                else
                    _stepKernel!(count, _dCellType!.View, _dNearBoundary!.View, _dDir!.View, _dCur!.View, _dNxt!.View,
                        _dims, _tau0, (byte)(accumulate ? 1 : 0), _dSums!.View);
                AgeStep(accumulate);
                if (accumulate) AccumulateRoomFluxGpu();
                (_dCur, _dNxt) = (_dNxt, _dCur);
                if (accumulate && _avgSamples - _foldedSamples >= FoldSteps) FoldSums();   // P17-01: keep each float32 window short

                if ((step + 1) % GpuSyncCadence == 0)
                {
                    accel.Synchronize();
                    ct.ThrowIfCancellationRequested();
                }
                if (step % DivergenceCheckInterval == 0 && _sampleIdx.Length > 0)
                    CheckDivergenceGpu(FullDivergenceStep(step));
                if (_snapIdx < _snapSteps.Length && step == _snapSteps[_snapIdx])
                {
                    accel.Synchronize();
                    _snapshots.Add(CaptureGpu(_source, _dCur!.View.GetAsArray1D(), count, _velScale, null, (step - _warmupSteps + 1) * _dt));
                    _snapIdx++;
                }
                _steps++;
            }
            if (finalSync) accel.Synchronize();
        }

        void StepGpuThermal(int steps, CancellationToken ct)
        {
            if (_hybrid) { StepGpuHybrid(steps, ct); return; }
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
                    _boundaryKernelThermalG!(ringCount, _dBIndex!.View, _dBClass!.View, _dBVel!.View, _dBNeighborSlot!.View,
                        _dCurG!.View, count, _tRef, n);

                bool accumulate = step >= _averageFromStep;
                if (accumulate) _avgSamples++;

                tp.Ramp = MathF.Min(1f, step / MathF.Max(1f, _warmupSteps / 2f));
                if (_devUnit.Length > 0 && step % DeviceUnitCadence == 0) UpdateDeviceUnits();
                HoldStep(step, accumulate);
                _stepKernelThermal!(count, _dCellType!.View, _dNearBoundary!.View, _dDir!.View, _dCur!.View, _dNxt!.View,
                    _dCurG!.View, _dNxtG!.View, _dims, tp, _dCellData!.View,
                    (byte)((accumulate ? 1 : 0) | (_hasDevices ? 2 : 0)), _dSums!.View);
                AgeStep(accumulate);
                if (accumulate) AccumulateRoomFluxGpu();
                (_dCur, _dNxt) = (_dNxt, _dCur);
                (_dCurG, _dNxtG) = (_dNxtG, _dCurG);
                if (accumulate && _avgSamples - _foldedSamples >= FoldSteps) FoldSums();   // P17-01

                if ((step + 1) % GpuSyncCadence == 0)
                {
                    accel.Synchronize();
                    ct.ThrowIfCancellationRequested();
                }
                if (step % DivergenceCheckInterval == 0 && _sampleIdx.Length > 0)
                    CheckDivergenceGpu(FullDivergenceStep(step));
                if (_snapIdx < _snapSteps.Length && step == _snapSteps[_snapIdx])
                {
                    accel.Synchronize();
                    _snapshots.Add(CaptureGpu(_source, _dCur!.View.GetAsArray1D(), count, _velScale,
                        _dCurG!.View.GetAsArray1D(), (step - _warmupSteps + 1) * _dt));
                    _snapIdx++;
                }
                _steps++;
            }
            accel.Synchronize();
        }

        /// <summary>Refreshes each conditioning unit's return-to-supply difference from the current
        /// temperature at its return grille, and (on the GPU) pushes the handful of floats that changed to
        /// the tail of the cellData buffer. Called at start-up and every <see cref="DeviceUnitCadence"/>
        /// steps: a room temperature moves on a scale of many seconds, so 50 steps is far finer than it
        /// needs, and the cost is one small gather kernel plus a copy of 2 x (units) floats.</summary>
        void UpdateDeviceUnits()
        {
            int nUnits = _devUnit.Length / 2;
            if (nUnits == 0) return;
            int count = _grid.Count;

            float[] cellT;
            if (_useGpu)
            {
                var accel = _acc!;
                if (_hybrid) _hybridGatherTKernel!(_unitCells.Length, _dUnitCells!.View, _dTCur!.View, _dUnitT!.View);
                else _gatherTemperatureKernel!(_unitCells.Length, _dUnitCells!.View, _dCurG!.View, count, _dUnitT!.View);
                accel.Synchronize();
                cellT = _dUnitT!.GetAsArray1D();
            }
            else
            {
                cellT = new float[_unitCells.Length];
                for (int k = 0; k < _unitCells.Length; k++)
                {
                    int b = _unitCells[k] * Q7;
                    cellT[k] = _hybrid ? _tCur[_unitCells[k]]
                        : _curG[b] + _curG[b + 1] + _curG[b + 2] + _curG[b + 3] + _curG[b + 4] + _curG[b + 5] + _curG[b + 6];
                }
            }

            for (int u = 0; u < nUnits; u++)
            {
                int from = _unitStart[u], to = _unitStart[u + 1];
                if (to <= from) { _devUnit[nUnits + u] = 0f; continue; }
                double sum = 0;
                for (int k = from; k < to; k++) sum += cellT[k];
                // A unit cannot heat the room: nothing to do once the return is at or below the setpoint.
                _devUnit[nUnits + u] = MathF.Max((float)(sum / (to - from)) - _devUnit[u], 0f);
            }

            if (_useGpu)
                _dCellData!.View.SubView(9 * (long)count, _devUnit.Length).CopyFromCPU(_devUnit);
        }

        void CheckDivergenceGpu(bool full = false)
        {
            _divergenceKernel!(_sampleIdx.Length, _dSampleIdx!.View, _dCur!.View, _grid.Count, _dSampleRho!.View);
            if (full)
            {
                _dBad ??= _acc!.Allocate1D<int>(1);
                _dBad.View.MemSetToZero();
                _divergenceAllKernel!(_grid.Count, _dCellType!.View, _dCur.View, _dims, _dBad.View);   // P17-06: every air cell, at the coarse cadence
            }
            _acc!.Synchronize();
            foreach (var rho in _dSampleRho!.GetAsArray1D())
                if (float.IsNaN(rho) || rho < RhoMin || rho > RhoMax)
                    throw new InvalidOperationException("Airflow solver diverged; try Refine quality or a lower wind speed");
            if (full && _dBad!.GetAsArray1D()[0] != 0)
                throw new InvalidOperationException("Airflow solver diverged; try Refine quality or a lower wind speed");
        }

        // ---- reading out -----------------------------------------------------------------------

        /// <summary>Copies the CURRENT instantaneous velocity (m/s, same units, layout and conventions as
        /// <see cref="FlowField.Ux"/>: 0 in solids, the imposed velocity in Fixed cells) into three
        /// caller-owned arrays of at least <c>grid.Count</c> floats -- a 10 Hz reader allocates nothing.
        /// On the GPU this is one kernel plus one device-to-host copy per component.</summary>
        public void ReadVelocity(float[] ux, float[] uy, float[] uz)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int count = _grid.Count;
            if (ux.Length < count || uy.Length < count || uz.Length < count)
                throw new ArgumentException($"ReadVelocity needs three arrays of at least grid.Count ({count}) floats");

            if (_inert)
            {
                Array.Clear(ux, 0, count); Array.Clear(uy, 0, count); Array.Clear(uz, 0, count);
                return;
            }

            if (_useGpu)
            {
                var accel = _acc!;
                EnsureGpuLive();
                _dUx ??= accel.Allocate1D<float>(count);
                _dUy ??= accel.Allocate1D<float>(count);
                _dUz ??= accel.Allocate1D<float>(count);
                _velocityKernel!(count, _dCellType!.View, _dCur!.View, count, _velScale, _dUx.View, _dUy.View, _dUz.View);
                accel.Synchronize();
                _dUx.View.CopyToCPU(ref ux[0], count);
                _dUy.View.CopyToCPU(ref uy[0], count);
                _dUz.View.CopyToCPU(ref uz[0], count);
                var air = _source.LbmAir; var src = _source.Cell;   // the kernel reads the LBM mask: zero what Cell calls Solid, as below
                if (air.Length == count) Parallel.For(0, count, i => { if (air[i] && src[i] == VoxelGrid.Solid) { ux[i] = 0f; uy[i] = 0f; uz[i] = 0f; } });
                return;
            }

            var grid = _source; var cur = _cur; float velScale = _velScale;
            Parallel.For(0, count, i =>
            {
                if (grid.Cell[i] == VoxelGrid.Solid) { ux[i] = 0f; uy[i] = 0f; uz[i] = 0f; return; }
                NeighborMacroscopic(cur, i, out float vx, out float vy, out float vz);
                ux[i] = vx * velScale; uy[i] = vy * velScale; uz[i] = vz * velScale;
            });
        }

        /// <summary>Instantaneous (not time-averaged) temperature, same idea as <see cref="ReadVelocity"/> --
        /// lets a caller sample how T develops mid-run (e.g. to check whether a scene has reached a thermal
        /// steady state before trusting <see cref="ToField"/>'s average). 0 outside <see cref="_thermal"/>
        /// runs and in Solid cells. No dedicated GPU kernel: cheap enough to pull the raw SoA buffer back and
        /// sum its 7 components on the CPU.</summary>
        public void ReadTemperature(float[] t)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int count = _grid.Count;
            if (t.Length < count) throw new ArgumentException($"ReadTemperature needs an array of at least grid.Count ({count}) floats");
            if (_inert || !_thermal) { Array.Clear(t, 0, count); return; }

            var grid = _source;
            if (_useGpu && _hybrid)
            {
                var hostT = _dTCur!.View.GetAsArray1D();
                Parallel.For(0, count, i => t[i] = grid.Cell[i] == VoxelGrid.Solid ? 0f : hostT[i]);
                return;
            }
            if (_useGpu)
            {
                var hostG = _dCurG!.View.GetAsArray1D();
                Parallel.For(0, count, i =>
                {
                    if (grid.Cell[i] == VoxelGrid.Solid) { t[i] = 0f; return; }
                    t[i] = hostG[i] + hostG[count + i] + hostG[2 * count + i] + hostG[3 * count + i]
                         + hostG[4 * count + i] + hostG[5 * count + i] + hostG[6 * count + i];
                });
                return;
            }
            if (_hybrid)
            {
                var tc = _tCur;
                Parallel.For(0, count, i => t[i] = grid.Cell[i] == VoxelGrid.Solid ? 0f : tc[i]);
                return;
            }
            var curG = _curG;
            Parallel.For(0, count, i =>
            {
                if (grid.Cell[i] == VoxelGrid.Solid) { t[i] = 0f; return; }
                int b = i * Q7;
                t[i] = curG[b] + curG[b + 1] + curG[b + 2] + curG[b + 3] + curG[b + 4] + curG[b + 5] + curG[b + 6];
            });
        }

        /// <summary>Instantaneous lattice density (sum of the 19 populations of the current buffer) per cell, 0 in
        /// Solid cells -- every non-solid cell, including the ring and Fixed/Supply boundary cells. A diagnostic
        /// (check 57): the GPU path pulls the whole population buffer back.</summary>
        public void ReadDensity(float[] rho)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int count = _grid.Count;
            if (rho.Length < count) throw new ArgumentException($"ReadDensity needs an array of at least grid.Count ({count}) floats");
            if (_inert) { Array.Clear(rho, 0, count); return; }
            var grid = _source;
            if (_useGpu)
            {
                _acc!.Synchronize();
                var h = _dCur!.View.GetAsArray1D();
                Parallel.For(0, count, i =>
                {
                    float r = 0f;
                    if (grid.Cell[i] != VoxelGrid.Solid) for (int q = 0; q < Q; q++) r += h[q * count + i];
                    rho[i] = r;
                });
                return;
            }
            var cur = _cur;
            Parallel.For(0, count, i =>
            {
                float r = 0f;
                if (grid.Cell[i] != VoxelGrid.Solid) for (int q = 0; q < Q; q++) r += cur[i * Q + q];
                rho[i] = r;
            });
        }

        /// <summary>The time-averaged field, exactly as <see cref="Solve"/> returns it (P, T, Tu, snapshots).
        /// Safe to call repeatedly; on the GPU each call pulls the accumulators back from the device.</summary>
        public FlowField ToField()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var grid = _source;
            int count = grid.Count;
            var field = new FlowField
            {
                Grid = grid.ForAnalysis(), Ux = new float[count], Uy = new float[count], Uz = new float[count], P = new float[count],
                Backend = _backend,
            };
            if (_inert) return field;

            if (_useGpu)
            {
                try { FoldSums(); ReadRoomFlux(field); ReadHold(field); }   // RW-02: the device readback
                catch (Exception ex) when (IsGpuCallFailure(ex)) { throw new GpuFaultException(ex); }
            }
            else { ReadRoomFlux(field); ReadHold(field); }
            int avgSamples = Math.Max(1, _avgSamples);
            var sumRho = _sumRho; var sumUx = _sumUx; var sumUy = _sumUy; var sumUz = _sumUz;
            float velScale = _velScale, pScale = _pScale;

            Parallel.For(0, count, _readOutPo, i =>
            {
                if (grid.Cell[i] != VoxelGrid.Air) return;
                float meanDensityExcess = (float)(sumRho[i] / avgSamples);
                field.Ux[i] = (float)(sumUx[i] / avgSamples) * velScale;
                field.Uy[i] = (float)(sumUy[i] / avgSamples) * velScale;
                field.Uz[i] = (float)(sumUz[i] / avgSamples) * velScale;
                field.P[i] = meanDensityExcess * pScale;
            });

            // Ring cells never accumulate averages (they're never streamed/collided); copy their inward
            // neighbour's averaged field instead of leaving them at 0 so the output has no dark border.
            int ringCount = _bl.Index.Length - _bl.FixedCount;
            for (int k = 0; k < ringCount; k++)
            {
                int idx = _bl.Index[k], ni = _bl.NeighborSlot[k];
                field.Ux[idx] = field.Ux[ni]; field.Uy[idx] = field.Uy[ni]; field.Uz[idx] = field.Uz[ni]; field.P[idx] = field.P[ni];
            }

            // Fixed cells: output is the imposed velocity and the pressure of the imposed density (FixedCellRho).
            for (int k = 0; k < grid.FixedCells.Count; k++)
            {
                var fc = grid.FixedCells[k];
                field.Ux[fc.Index] = fc.Velocity.X; field.Uy[fc.Index] = fc.Velocity.Y; field.Uz[fc.Index] = fc.Velocity.Z;
                field.P[fc.Index] = (FixedCellRho - 1f) * pScale;
            }
            // Supply patches: report the velocity that carries the flux the links actually deliver (so
            // Analysis' mean-u x Width x Height reads the delivered flow), plus each controller's state.
            ReportSupplies(field);

            // Turbulence intensity: computed for every real run (not just thermal ones), from the same
            // Smagorinsky eddy viscosity the collision already produces (RunStep/StepKernel accumulate it
            // into sumNuT unconditionally). k ~ (nu_t / (Ck*dx))^2 (one-equation SGS closure).
            field.Tu = new float[count];
            float diffScale = _dx * _dx / _dt; // lattice diffusivity -> physical (m^2/s)
            float ckDx = CkTurb * _dx;
            var sumNuT = _sumNuT;
            Parallel.For(0, count, _readOutPo, i =>
            {
                if (grid.Cell[i] != VoxelGrid.Air) return;
                float nuTPhys = (float)(sumNuT[i] / avgSamples) * diffScale;
                float k = MathF.Pow(nuTPhys / ckDx, 2f);
                float speed = MathF.Sqrt(field.Ux[i] * field.Ux[i] + field.Uy[i] * field.Uy[i] + field.Uz[i] * field.Uz[i]);
                field.Tu[i] = Math.Clamp(MathF.Sqrt(2f * k / 3f) / MathF.Max(speed, 0.05f), 0f, 1f);
            });

            if (_thermal)
            {
                field.T = new float[count];
                var sumT = _sumT;
                Parallel.For(0, count, _readOutPo, i =>
                {
                    if (grid.Cell[i] == VoxelGrid.Solid) return;
                    field.T[i] = (float)(sumT[i] / avgSamples);
                });
                for (int k = 0; k < ringCount; k++)
                {
                    int idx = _bl.Index[k], ni = _bl.NeighborSlot[k];
                    field.T[idx] = field.T[ni];
                }
                // Fixed (mechanical) cells: not part of the thermal step loop (ponytail: see BuildThermal /
                // SetBoundaryThermalRingCpu) -- report their initial T rather than a stale zero.
                for (int k = 0; k < grid.FixedCells.Count; k++)
                {
                    var fc = grid.FixedCells[k];
                    field.T[fc.Index] = _T0[fc.Index];
                }
            }

            field.Age = ReadAge();   // 6k: the online age's time mean over the samples since the last reset ([] when it is not running)
            MeasureDevices(field);
            field.Snapshots = _snapshots;
            field.Steps = (int)_steps;
            field.Seconds = _elapsed;
            return field;
        }

        /// <summary>One Supply patch's flux controller (see SupplyCadence).</summary>
        sealed class SupplyLoop
        {
            public int Source;
            public byte Mode;               // FixedCell.Mode: 0 Supply, 1 Return (sink: links counted where c . u_w < 0), 2 Fan
            public int[] Entries = [];      // boundary-list entries holding this patch's cells
            public int[] Cells = [];        // grid index per entry
            public float[] Base = [];       // unscaled lattice wall velocity per entry, x/y/z interleaved
            public int Links;               // links from the patch cells into room air with c . u_w > 0
            public double Unit;             // flux those links deliver at Scale 1, lattice cell volumes per step
            public double Set;              // set FlowRate, same units
            public float AreaRatio = 1f;    // drawn Width x Height / carved cells' area: feed-forward on the wall velocity
            public float Scale = 1f, Integral, Gain = 1f;
            public double Window;           // delivered flux summed over the current window
            public int WindowSteps, Updates;
            public float[] EntryUnit = [];  // per entry, its links' share of Unit (weights of the mass-neutral perturbation)
            public float[] Pert = [];       // per entry, current perturbation factor - 1 (RunOptions.SupplyPerturbation)
            public float[] PertT1 = [], PertT2 = [];   // per entry, current tangential perturbation as a fraction of the entry's wall speed
            public int[] LinkMask = [];                // per entry, bit q set where the link counted in Unit
            public bool[] Rim = [];                    // RP-02: per entry, a room-bound link lies in the patch plane (an air cell beside the patch): no tangential fluctuation there
            public float[] Tan1 = [], Tan2 = [];       // per entry, unit tangent vectors (x/y/z interleaved) perpendicular to the wall velocity
            public float Period = 1f;       // perturbation keyframe spacing, steps: the patch's D / U in lattice units
            public float Transfer = 1f;     // RP-08: SupplyTuTransfer at this patch's width in cells
            public float Tu;                // this supply's isotropic Tu (global SupplyPerturbation, else the source's own); 0 = off
        }

        /// <summary>Groups the boundary list's BcSupply entries by owning source, counts their room-bound links and
        /// the flux those carry at scale 1, and writes each loop's current scale into <see cref="BoundaryList.Vel"/>.
        /// Called whenever the boundary list is rebuilt; a source already under control keeps its scale and integral.</summary>
        List<SupplyLoop> BuildSupplyLoops(List<SupplyLoop>? previous)
        {
            var loops = new List<SupplyLoop>();
            var grid = _grid; var bl = _bl;
            int n = bl.Index.Length, r = n - bl.FixedCount;
            if (bl.FixedCount != grid.FixedCells.Count) return loops;
            int Nx = grid.Nx, Ny = grid.Ny, Nz = grid.Nz;
            var bySource = new Dictionary<int, (List<int> E, List<int> C)>();
            for (int k = r; k < n; k++)
            {
                if (bl.Class[k] != BcSupply) continue;
                int src = grid.FixedCells[k - r].Supply;
                if (!bySource.TryGetValue(src, out var lists)) bySource[src] = lists = (new List<int>(), new List<int>());
                lists.E.Add(k); lists.C.Add(bl.Index[k]);
            }
            float cellArea = _dx * _dx;
            var area = SupplyAreaRatios(grid, _model);
            Span<int> ext = stackalloc int[3];
            foreach (var (src, lists) in bySource)
            {
                var loop = new SupplyLoop { Source = src, Entries = lists.E.ToArray(), Cells = lists.C.ToArray() };
                loop.Mode = grid.FixedCells[lists.E[0] - r].Mode;
                float sgn = loop.Mode == 1 ? -1f : 1f;   // a Return's links carry air out: count them with the sign flipped
                loop.AreaRatio = area.TryGetValue(src, out var ar) ? ar : 1f;
                loop.Base = new float[3 * loop.Entries.Length];
                loop.EntryUnit = new float[loop.Entries.Length];
                loop.Pert = new float[loop.Entries.Length];
                loop.PertT1 = new float[loop.Entries.Length]; loop.PertT2 = new float[loop.Entries.Length];
                loop.LinkMask = new int[loop.Entries.Length]; loop.Rim = new bool[loop.Entries.Length];
                loop.Tan1 = new float[3 * loop.Entries.Length]; loop.Tan2 = new float[3 * loop.Entries.Length];
                double nominal = 0;
                int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
                for (int e = 0; e < loop.Entries.Length; e++)
                {
                    int k = loop.Entries[e], idx = loop.Cells[e];
                    float vx = bl.Vel[k] * loop.AreaRatio, vy = bl.Vel[n + k] * loop.AreaRatio, vz = bl.Vel[2 * n + k] * loop.AreaRatio;
                    loop.Base[3 * e] = vx; loop.Base[3 * e + 1] = vy; loop.Base[3 * e + 2] = vz;
                    TangentFrame(vx, vy, vz, loop.Tan1, loop.Tan2, 3 * e);
                    float speed = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
                    nominal += speed;
                    int x = idx % Nx, y = idx / Nx % Ny, z = idx / (Nx * Ny);
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
                    for (int q = 1; q < Q; q++)
                    {
                        float cu = sgn * (Cx[q] * vx + Cy[q] * vy + Cz[q] * vz);
                        int tx = x + Cx[q], ty = y + Cy[q], tz = z + Cz[q];
                        // Only targets the step actually streams into: interior (non-ring) air cells.
                        if (tx < 1 || tx > Nx - 2 || ty < 1 || ty > Ny - 2 || tz < 0 || tz > Nz - 1) continue;
                        if (grid.Cell[grid.Index(tx, ty, tz)] != VoxelGrid.Air) continue;
                        if (LinkCut(_nearBoundary, grid.Count, grid.Index(tx, ty, tz), q)) continue;   // 6a: the target bounces off a cut link instead
                        loop.LinkMask[e] |= 1 << q;   // every room-bound link, including c . u_w <= 0 ones a tangential perturbation can switch on
                        // RP-02: an in-plane link into air (c . u_w ~ 0) leaves the patch's footprint; a tangential fluctuation would rectify into a sideways outflow there.
                        if (MathF.Abs(Cx[q] * vx + Cy[q] * vy + Cz[q] * vz) <= 0.1f * MathF.Sqrt(Cx[q] * Cx[q] + Cy[q] * Cy[q] + Cz[q] * Cz[q]) * speed) loop.Rim[e] = true;
                        if (cu <= 0f) continue;
                        loop.Links++;
                        double lu = 6.0 * (q <= 6 ? 1.0 / 18.0 : 1.0 / 36.0) * cu;
                        loop.Unit += lu; loop.EntryUnit[e] += (float)lu;
                    }
                }
                // D: the patch's smaller in-face extent (the thickness along the flow axis drops out as the smallest
                // of the three); U: its mean wall speed. Both in cells / steps, so D / U is in steps.
                ext[0] = maxX - minX + 1; ext[1] = maxY - minY + 1; ext[2] = maxZ - minZ + 1;
                ext.Sort();
                float meanU = loop.Entries.Length > 0 ? (float)(nominal / loop.Entries.Length) : 0f;
                loop.Period = meanU > 1e-6f ? MathF.Max(1f, ext[1] / meanU) : 1f;
                float setPhys = src >= 0 && src < _model.Sources.Count ? grid.PatchFlow(src, _model.Sources[src].FlowRate) : 0f;   // RW-01: a sealed room's balanced flow
                loop.Set = setPhys > 0f ? setPhys * _latPerPhys / cellArea : nominal;
                var old = previous?.Find(p => p.Source == src);
                if (old != null) { loop.Scale = old.Scale; loop.Integral = old.Integral; loop.Gain = old.Gain; loop.Updates = old.Updates; }
                loop.Tu = SupplyTu(loop, src, ext[1]);
                loop.Transfer = SupplyTuTransfer(ext[1]);
                if (loop.Tu > 0f && ext[1] < 4 && src >= 0 && src < _model.Sources.Count)   // RP follow-up 3: n = 3 delivers about 0.5x the request, n = 2 axial only (FIXLOG Batch RP)
                {
                    string tuNote = $"Source {_model.Sources[src].Name}: its supply patch is only {ext[1]} cells across (under 4), so the turbulence it delivers is below the requested {loop.Tu * 100f:0} %; use a finer quality level or a larger patch.";
                    if (!_source.Warnings.Contains(tuNote)) _source.Warnings.Add(tuNote);
                }
                else if (loop.Tu == 0f && ext[1] < 2 && loop.Mode == 0 && _perturb <= 0f && _supplyTuPerSource && src >= 0 && src < _model.Sources.Count &&
                         (_model.Sources[src].TurbulenceIntensity ?? HighAccuracy.DefaultSupplyTu(_model.Sources[src])) > 0f)   // RW-10c: gated to nothing by the sub-grid rule: say so
                {
                    string tuNote = $"Source {_model.Sources[src].Name}: its supply patch is only {ext[1]} cells across, too narrow for turbulence; no fluctuation applied.";
                    if (!_source.Warnings.Contains(tuNote)) _source.Warnings.Add(tuNote);
                }
                if (loop.Tu > 0f) _anySupplyTu = true;
                if (loop.Tu > 0f) Perturb(loop, _steps);
                ApplySupplyScale(loop);
                loops.Add(loop);
            }
            return loops;
        }

        void ApplySupplyScale(SupplyLoop loop)
        {
            var bl = _bl; int n = bl.Index.Length;
            for (int e = 0; e < loop.Entries.Length; e++)
            {
                int k = loop.Entries[e];
                float s = loop.Scale * (1f + loop.Pert[e]);
                float t1 = loop.PertT1[e], t2 = loop.PertT2[e];   // tangential speed / |wall speed|, 0 unless perturbed
                float sp = MathF.Sqrt(loop.Base[3 * e] * loop.Base[3 * e] + loop.Base[3 * e + 1] * loop.Base[3 * e + 1] + loop.Base[3 * e + 2] * loop.Base[3 * e + 2]) * loop.Scale;
                bl.Vel[k] = loop.Base[3 * e] * s + sp * (t1 * loop.Tan1[3 * e] + t2 * loop.Tan2[3 * e]);
                bl.Vel[n + k] = loop.Base[3 * e + 1] * s + sp * (t1 * loop.Tan1[3 * e + 1] + t2 * loop.Tan2[3 * e + 1]);
                bl.Vel[2 * n + k] = loop.Base[3 * e + 2] * s + sp * (t1 * loop.Tan1[3 * e + 2] + t2 * loop.Tan2[3 * e + 2]);
            }
            if (loop.Tu <= 0f) return;
            // The wall increment is rectified per link (SupplyBounce keeps only c . u_w > 0), so a tangential velocity adds
            // flux through the c_normal = 0 links (and shifts the diagonals' share) that a mean-zero field does not cancel.
            // Recompute the exact delivered flux and rescale every wall velocity so it stays Scale x Unit: the flux the
            // controller assumes. Flux is positively homogeneous in the velocity, so one factor does it (it is ~1 - Tu^2).
            double flux = 0;
            for (int e = 0; e < loop.Entries.Length; e++)
            {
                int k = loop.Entries[e], mask = loop.LinkMask[e];
                float vx = bl.Vel[k], vy = bl.Vel[n + k], vz = bl.Vel[2 * n + k];
                for (int q = 1; q < Q; q++)
                {
                    if ((mask >> q & 1) == 0) continue;
                    float cu = Cx[q] * vx + Cy[q] * vy + Cz[q] * vz;
                    if (cu > 0f) flux += 6.0 * (q <= 6 ? 1.0 / 18.0 : 1.0 / 36.0) * cu;
                }
            }
            if (flux <= 0) return;
            float lambda = (float)(loop.Scale * loop.Unit / flux);
            for (int e = 0; e < loop.Entries.Length; e++)
            {
                int k = loop.Entries[e];
                bl.Vel[k] *= lambda; bl.Vel[n + k] *= lambda; bl.Vel[2 * n + k] *= lambda;
            }
        }

        // ---- Supply-face perturbation (RunOptions.SupplyPerturbation) ----
        // Each entry's wall speed is scaled by 1 + p_e(t). Keyframes of spatially correlated noise (FaceNoise: a hash of seed, source, keyframe,
        // lattice corner -- no RNG state, so CPU, GPU and a rebuilt loop list all see the same sequence) sit one D/U apart and
        // are blended linearly between. Every blended field is projected onto sum_e w_e p_e = 0 with w_e the entry's
        // link flux at scale 1 (EntryUnit) -- the flux each entry delivers is exactly (1 + p_e) w_e Scale, so the
        // patch delivers Scale x Unit whatever p is and the flux controller never sees it -- then scaled to the
        // requested w-weighted rms. Only the speed is perturbed, never the direction, so the set of injecting links
        // (c . u_w > 0) and hence Unit stay fixed.

        /// <summary>Steps between perturbation updates: an eighth of the keyframe spacing.</summary>
        static int PerturbCadence(SupplyLoop loop) => Math.Max(1, (int)MathF.Round(loop.Period / 8f));

        /// <summary>The Tu a supply loop runs with. The global RunOptions.SupplyPerturbation, when set, overrides every
        /// supply (the validation harnesses use it). Otherwise, with RunOptions.SupplyTuPerSource, the source's own
        /// AirSource.TurbulenceIntensity. A turbulence length scale below 2 cells carries no resolved eddy (Nyquist), so
        /// a per-source Tu on a patch under 2 cells across injects nothing: l0 is the patch's smaller in-face extent
        /// (the correlation scale the keyframes use is D/U), <paramref name="faceCells"/> in cells. The global override
        /// is not gated, to keep the validated 55g/55h behaviour exactly.</summary>
        float SupplyTu(SupplyLoop loop, int src, int faceCells)
        {
            if (loop.Mode != 0) return 0f;   // a Return or Fan patch carries no inflow turbulence
            if (_perturb > 0f) return _perturb;
            if (!_supplyTuPerSource || src < 0 || src >= _model.Sources.Count) return 0f;
            var source = _model.Sources[src];
            float tu = source.TurbulenceIntensity ?? HighAccuracy.DefaultSupplyTu(source);
            return tu > 0f && faceCells >= 2 ? tu : 0f;
        }

        void Perturb(SupplyLoop loop, long step)
        {
            int m = loop.Pert.Length;
            if (m < 2) return;
            // Stream 0 is the axial speed factor; streams 1 and 2 are two independent tangential velocities (fractions of
            // the entry's wall speed), same keyframes, same weighted-rms normalisation. SupplyPerturbation is the
            // isotropic Tu = sqrt((u'^2 + v'^2 + w'^2) / 3) / U, so each of the three components has rms Tu x U.
            // Tangential wall velocity does not change the normal flux, and each field is projected to zero w-mean.
            PerturbStream(loop, step, 0, loop.Pert);
            PerturbStream(loop, step, 1, loop.PertT1);
            PerturbStream(loop, step, 2, loop.PertT2);
        }

        void PerturbStream(SupplyLoop loop, long step, int stream, float[] pert)
        {
            int m = pert.Length;
            double t = step / (double)loop.Period;
            long k = (long)Math.Floor(t);
            float s = (float)(t - k);
            // Stream 0 keeps the original hash arguments exactly; the tangential streams offset the source id.
            int src = stream == 0 ? loop.Source : loop.Source + stream * 0x2545F491;
            double sw = 0, mean = 0;
            int nx = _grid.Nx, ny = _grid.Ny;
            // RP-02: the tangential streams act on the patch's interior entries only, whose every link stays over the patch; a rim entry (an in-plane link into the air beside
            // the patch) keeps the axial fluctuation alone. The weights and the rms below run over the entries that carry the stream, so the face rms is the requested one.
            bool Skip(int e) => stream != 0 && loop.Rim[e];
            for (int e = 0; e < m; e++)
            {
                if (Skip(e)) { pert[e] = 0f; continue; }
                int ci = loop.Cells[e];
                float px = ci % nx, py = ci / nx % ny, pz = ci / (nx * ny);
                float a = FaceNoise(_perturbSeed, src, k, px, py, pz), b = FaceNoise(_perturbSeed, src, k + 1, px, py, pz);
                pert[e] = (1f - s) * a + s * b;
                sw += loop.EntryUnit[e]; mean += loop.EntryUnit[e] * pert[e];
            }
            if (sw <= 0) { Array.Clear(pert); return; }
            mean /= sw;
            double v2 = 0;
            for (int e = 0; e < m; e++) { if (Skip(e)) continue; pert[e] -= (float)mean; v2 += loop.EntryUnit[e] * pert[e] * pert[e]; }
            float gain = v2 > 1e-20 ? loop.Transfer * loop.Tu / (float)Math.Sqrt(v2 / sw) : 0f;
            // Clamp the axial factor at -0.5 so a wall speed never reverses; the noise is bounded (peak ~2.5 sigma after
            // blending) and the face rms is SupplyTuTransfer x Tu, so the clamp can bind above Tu ~ 0.2 / SupplyTuTransfer (RP-10). The patch flux is
            // restored by lambda (ApplySupplyScale), not by the projection above.
            for (int e = 0; e < m; e++) pert[e] = stream == 0 ? MathF.Max(-0.5f, gain * pert[e]) : gain * pert[e];
        }

        /// <summary>Two unit vectors perpendicular to (vx, vy, vz) and to each other, written at offset o.</summary>
        static void TangentFrame(float vx, float vy, float vz, float[] t1, float[] t2, int o)
        {
            float len = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
            if (len < 1e-12f) return;
            float dx = vx / len, dy = vy / len, dz = vz / len;
            // cross with the axis the direction is least aligned with
            float ax = 0, ay = 0, az = 0;
            if (MathF.Abs(dx) <= MathF.Abs(dy) && MathF.Abs(dx) <= MathF.Abs(dz)) ax = 1; else if (MathF.Abs(dy) <= MathF.Abs(dz)) ay = 1; else az = 1;
            float ux = ay * dz - az * dy, uy = az * dx - ax * dz, uz = ax * dy - ay * dx;
            float ul = MathF.Sqrt(ux * ux + uy * uy + uz * uz);
            ux /= ul; uy /= ul; uz /= ul;
            t1[o] = ux; t1[o + 1] = uy; t1[o + 2] = uz;
            t2[o] = dy * uz - dz * uy; t2[o + 1] = dz * ux - dx * uz; t2[o + 2] = dx * uy - dy * ux;
        }

        /// <summary>6i (P13a-04): correlation length of the face fluctuation, cells. White (1-cell) noise is high-wavenumber: the lattice viscosity removed
        /// about half of the requested Tu within the first cell (delivered 0.097 at a request of 0.19, n = 6). Value noise on a lattice of this spacing
        /// carries the face's lowest wavenumbers; the keyframes are unchanged. Chosen on the duct rig (Sandbox `--tu-run`, fixes/b6d/tuL2.txt, tuL3.txt,
        /// tuL4.txt): delivered / requested Tu at 1 cell over n = 4, 6, 8 and requests 0.19, 0.10 spans 0.65-0.80 with a 2-cell lattice, 0.71-0.83 with 3, 0.74-0.79 with 4,
        /// the narrowest, so one transfer factor (<see cref="SupplyTuTransfer"/>) can bring every case inside +-10 %.</summary>
        const float SupplyNoiseCells = 4f;

        /// <summary>6i, RP-08: the factor on the face fluctuation that makes the Tu delivered at the first cell the Tu requested, by the patch's width in cells (its smaller in-face
        /// extent), linear between the knots and clamped to the first and last outside them (the rig measured n = 4 to 16 only; a patch of 2-3 cells delivers less, see RP-08 in
        /// FIXLOG). The face rms is this x Tu; the fluid sees Tu. Rig-measured on the duct of Sandbox `--tu-run` after RP-02 (the tangential fluctuation acts on the interior
        /// entries only, so the share of rim entries, which falls as 1/n, sets the need): delivered / requested at 1 cell with the old 1.30 was 0.63 / 0.92 / 0.99 / 1.12 / 1.07 at
        /// n = 4 / 6 / 8 / 12 / 16 (requests 0.19 and 0.10 agree to 0.02), so the factors are 1.30 divided by those. Never fitted to a validation case.</summary>
        internal static float SupplyTuTransfer(float faceCells) => GridLerp(SupplyTuCells, faceCells, k => SupplyTuFactors[k]);
        static readonly float[] SupplyTuCells = { 4f, 6f, 8f, 12f, 16f };
        static readonly float[] SupplyTuFactors = { 2.06f, 1.41f, 1.31f, 1.17f, 1.22f };

        /// <summary>Smoothly interpolated value noise at the cell position (px, py, pz), lattice spacing <see cref="SupplyNoiseCells"/>, from
        /// <see cref="PerturbNoise"/> at the lattice corners. Uniform in [-1, 1] at the corners, spatially correlated over ~SupplyNoiseCells.</summary>
        static float FaceNoise(int seed, int source, long key, float px, float py, float pz)
        {
            float gx = px / SupplyNoiseCells, gy = py / SupplyNoiseCells, gz = pz / SupplyNoiseCells;
            int ix = (int)MathF.Floor(gx), iy = (int)MathF.Floor(gy), iz = (int)MathF.Floor(gz);
            float fx = gx - ix, fy = gy - iy, fz = gz - iz;
            fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy); fz = fz * fz * (3f - 2f * fz);
            float C(int i, int j, int k) => PerturbNoise(seed, source, key, unchecked((ix + i) * 73856093 ^ (iy + j) * 19349663 ^ (iz + k) * 83492791));
            float x00 = C(0, 0, 0) + fx * (C(1, 0, 0) - C(0, 0, 0)), x10 = C(0, 1, 0) + fx * (C(1, 1, 0) - C(0, 1, 0));
            float x01 = C(0, 0, 1) + fx * (C(1, 0, 1) - C(0, 0, 1)), x11 = C(0, 1, 1) + fx * (C(1, 1, 1) - C(0, 1, 1));
            float y0 = x00 + fy * (x10 - x00), y1 = x01 + fy * (x11 - x01);
            return y0 + fz * (y1 - y0);
        }

        /// <summary>Uniform in [-1, 1), a pure function of its arguments (SplitMix64 finaliser).</summary>
        static float PerturbNoise(int seed, int source, long key, int entry)
        {
            ulong h = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)source * 0xBF58476D1CE4E5B9UL
                    ^ (ulong)key * 0x94D049BB133111EBUL ^ (ulong)(uint)entry * 0xD6E8FEB86659FD93UL;
            h ^= h >> 30; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 27; h *= 0x94D049BB133111EBUL; h ^= h >> 31;
            return (h >> 40) * (2f / (1 << 24)) - 1f;
        }

        /// <summary>One step of every Supply flux controller, called once per lattice step before the boundary
        /// update. Accumulates the flux the patch links deliver (Scale x Unit: the net population each link passes
        /// per step, see SupplyBounce), and every <see cref="SupplyCadence"/> steps moves the scale by a PI step on
        /// e = 1 - windowMean/set, slew-clamped to <see cref="SupplyGainMax"/> per update and bounded to
        /// [<see cref="SupplyScaleMin"/>, <see cref="SupplyScaleMax"/>], with back-calculated anti-windup. Pure
        /// host arithmetic, so CPU and GPU runs take the identical scale sequence. Returns true when a wall
        /// velocity changed (the GPU caller then re-uploads the boundary velocities).</summary>
        /// <summary>RunOptions.WindTurbulence: rewrites the velocity of the wind-inflow ring entries of the host boundary
        /// list (base profile + synthetic fluctuation at this step). Host-side, so the CPU kernel reads it directly
        /// and the GPU copies the list to the device afterwards. Returns true when the list changed.</summary>
        bool WindTurbulenceStep(long step)
        {
            if (_windTu == null || _tuField == null) return false;
            var bl = _bl; int n = bl.Index.Length;
            EnsureTuCache();
            int cnt = _tuK.Length; if (cnt == 0) return false;
            // Taylor: convect at the mean inflow speed; stream direction from the wind vector.
            float uc = _tuUc, wl = _wind.Length();
            if (wl < 1e-6f) return false;
            float sx = _wind.X / wl, sy = _wind.Y / wl;
            float t = step;
            var tu = _tuField; var kk = _tuK; var bs = _tuBase; var ps = _tuPos; var sg = _tuSig; var vel = bl.Vel;
            Parallel.For(0, cnt, e =>
            {
                // P13b-02: evaluate in the stream frame (x along the wind) so the rotated vector below stays divergence-free
                // and the streamwise scale stays L11 for every wind direction; convection is along that x only.
                tu.Eval(ps[3 * e] * sx + ps[3 * e + 1] * sy, -ps[3 * e] * sy + ps[3 * e + 1] * sx, ps[3 * e + 2], t, uc, 0f, out float gx, out float gy, out float gz);
                float bx = bs[3 * e], by = bs[3 * e + 1], u = MathF.Sqrt(bx * bx + by * by);
                float us = sg[3 * e] * u * gx, ul = sg[3 * e + 1] * u * gy, uw = sg[3 * e + 2] * u * gz;
                int k = kk[e];
                vel[k] = bx + us * sx - ul * sy;
                vel[n + k] = by + us * sy + ul * sx;
                vel[2 * n + k] = uw;
            });
            return true;
        }

        /// <summary>GPU twin of <see cref="WindTurbulenceStep"/>: the mode sum runs on the device and writes the ring velocity
        /// straight into the boundary-velocity buffer (no host copy). Per step only the per-mode time phases (n floats) go up.
        /// Call after any host re-upload of the boundary velocities, which would overwrite the ring entries.</summary>
        void WindTurbulenceGpu(long step)
        {
            if (_windTu == null || _tuField == null) return;
            var bl = _bl; int n = bl.Index.Length;
            EnsureTuCache();
            int cnt = _tuK.Length; if (cnt == 0) return;
            float wl = _wind.Length();
            if (wl < 1e-6f) return;
            var acc = _acc!;
            if (!ReferenceEquals(_tuGpuFor, bl) || _dTuK == null)
            {
                _dTuK?.Dispose(); _dTuBase?.Dispose(); _dTuPos?.Dispose(); _dTuSig?.Dispose(); _dTuModes?.Dispose(); _dTuPh?.Dispose();
                _dTuK = acc.Allocate1D(_tuK); _dTuBase = acc.Allocate1D(_tuBase); _dTuPos = acc.Allocate1D(_tuPos);
                _dTuSig = acc.Allocate1D(_tuSig); _dTuModes = acc.Allocate1D(_tuField.PackModes());
                _tuPh = new float[_tuField.Count]; _dTuPh = acc.Allocate1D(_tuPh);
                _tuGpuFor = bl;
            }
            EnsureGpuWindTu();
            float sx = _wind.X / wl, sy = _wind.Y / wl;
            _tuField.TimePhases((double)_tuUc, 0.0, step, _tuPh);   // stream frame, see WindTuKernel (P13b-02)
            _dTuPh!.View.CopyFromCPU(_tuPh);
            _windTuKernel!(cnt, _dTuK.View, _dTuBase!.View, _dTuPos!.View, _dTuSig!.View, _dTuModes!.View, _dTuPh.View,
                _dBVel!.View, n, _tuField.Count, sx, sy);
        }

        /// <summary>Sandbox hook: the ring velocity (3 per entry, lattice units) after the host and, on a GPU session, the device
        /// turbulence at <paramref name="step"/>, plus the mean inflow speed. Not for production.</summary>
        internal (float[] Host, float[]? Gpu, float URef) TurbulenceCompare(long step)
        {
            if (_windTu == null) return (Array.Empty<float>(), null, 0f);
            WindTurbulenceStep(step);
            EnsureTuCache();
            int n = _bl.Index.Length, m = _tuK.Length;
            float[] Pick(float[] v) { var r = new float[3 * m]; for (int e = 0; e < m; e++) for (int c = 0; c < 3; c++) r[3 * e + c] = v[c * n + _tuK[e]]; return r; }
            var host = Pick(_bl.Vel);
            float[]? gpu = null;
            if (_useGpu)
            {
                WindTurbulenceGpu(step);
                _acc!.Synchronize();
                gpu = Pick(_dBVel!.View.GetAsArray1D());
            }
            return (host, gpu, _tuUc);
        }

        void EnsureTuCache()
        {
            var bl = _bl; int n = bl.Index.Length;
            if (!ReferenceEquals(_tuFor, bl))
            {
                var ks = new List<int>();
                for (int k = 0; k < n - bl.FixedCount; k++) if (IsInflow(bl.Class[k])) ks.Add(k);
                _tuK = ks.ToArray(); int m = _tuK.Length;
                _tuBase = new float[3 * m]; _tuPos = new float[3 * m]; _tuSig = new float[3 * m];
                for (int e = 0; e < m; e++)
                {
                    int k = _tuK[e], idx = bl.Index[k];
                    int x = idx % _grid.Nx, y = idx / _grid.Nx % _grid.Ny, z = idx / (_grid.Nx * _grid.Ny);
                    _tuBase[3 * e] = bl.Vel[k]; _tuBase[3 * e + 1] = bl.Vel[n + k]; _tuBase[3 * e + 2] = bl.Vel[2 * n + k];
                    _tuPos[3 * e] = x; _tuPos[3 * e + 1] = y; _tuPos[3 * e + 2] = z;
                    float zAbove = _grid.Origin.Z + (z + 0.5f) * _grid.Dx - _model.GroundZ;
                    float iu = _windTu!.IuAt(zAbove);
                    _tuSig[3 * e] = iu; _tuSig[3 * e + 1] = iu * _windTu.IvRatio; _tuSig[3 * e + 2] = iu * _windTu.IwRatio;
                }
                _tuFor = bl;
                int cnt = _tuK.Length;
                double sum = 0; for (int e = 0; e < cnt; e++) sum += Math.Sqrt(_tuBase[3 * e] * _tuBase[3 * e] + _tuBase[3 * e + 1] * _tuBase[3 * e + 1]);
                _tuUc = cnt > 0 ? (float)(sum / cnt) : 0f;
                // tau = L / sigma_u (steps) at the ring entry nearest TimeRefHeight: sigma_u = Iu x |U| of that entry, lattice units.
                float zr = _windTu!.TimeRefHeight > 0f ? _windTu.TimeRefHeight : _windTu.RefHeight;
                double best = double.MaxValue, sigU = 0;
                for (int e = 0; e < cnt; e++)
                {
                    double d = Math.Abs(_tuPos[3 * e + 2] * (double)_grid.Dx + _grid.Origin.Z + 0.5 * _grid.Dx - _model.GroundZ - zr);
                    if (d < best) { best = d; sigU = _tuSig[3 * e] * Math.Sqrt(_tuBase[3 * e] * _tuBase[3 * e] + _tuBase[3 * e + 1] * _tuBase[3 * e + 1]); }
                }
                _tuField!.SetTimeScale(sigU > 0 ? _windTu.LengthScale / _grid.Dx / sigU : 0);
            }
        }

        bool SupplyStep()
        {
            bool changed = false;
            foreach (var loop in _supply)
            {
                loop.Window += loop.Scale * loop.Unit; loop.WindowSteps++;
                if (loop.WindowSteps < SupplyCadence) continue;
                double delivered = loop.Window / loop.WindowSteps;
                loop.Window = 0; loop.WindowSteps = 0;
                if (loop.Set <= 0 || loop.Unit <= 0) continue;
                float e = (float)(1.0 - delivered / loop.Set);
                float integral = loop.Integral + SupplyKi * e;
                float target = Math.Clamp(1f + integral + SupplyKp * e, SupplyScaleMin, SupplyScaleMax);
                float next = Math.Clamp(target, loop.Scale / SupplyGainMax, loop.Scale * SupplyGainMax);
                loop.Integral = next - 1f - SupplyKp * e;   // back-calculation: the integral the applied scale implies
                loop.Gain = next / loop.Scale;
                loop.Updates++;
                if (next != loop.Scale) { loop.Scale = next; ApplySupplyScale(loop); changed = true; }
            }
            if (_anySupplyTu)
                foreach (var loop in _supply)
                    if (loop.Tu > 0f && _steps % PerturbCadence(loop) == 0) { Perturb(loop, _steps); ApplySupplyScale(loop); changed = true; }
            return changed;
        }

        void ReportSupplies(FlowField field)
        {
            float toPhys = _dx * _dx / MathF.Max(_latPerPhys, 1e-12f);
            foreach (var loop in _supply)
            {
                double delivered = loop.Scale * loop.Unit;
                float ratio = loop.Set > 0 ? (float)(delivered / loop.Set) : 0f;
                for (int e = 0; e < loop.Cells.Length; e++)
                {
                    int idx = loop.Cells[e];
                    field.Ux[idx] *= ratio; field.Uy[idx] *= ratio; field.Uz[idx] *= ratio;
                }
                field.Supplies.Add(new SupplyResult
                {
                    SourceIndex = loop.Source, SetFlowRate = (float)(loop.Set * toPhys), FlowRate = (float)(delivered * toPhys),
                    Scale = loop.Scale, Gain = loop.Gain, Updates = loop.Updates, Cells = loop.Cells.Length, Links = loop.Links,
                    AreaRatio = loop.AreaRatio,
                });
            }
        }

        /// <summary>What each body-force device actually achieved on the averaged field: the volume flow
        /// through its outlet cells (the flux of the mean velocity across the outlet face, along its normal --
        /// along each cell's own target direction only for a cassette -- divided by the number of cell layers the outlet is thick, since every layer carries the same
        /// flow), and for a conditioning unit the temperature its return grille sees and the sensible duty
        /// Q = rho*cp*V*(T_return - T_supply) that implies.</summary>
        void MeasureDevices(FlowField field)
        {
            float cellArea = _dx * _dx;
            foreach (var d in _grid.Devices)
            {
                var res = new DeviceResult
                {
                    SourceId = d.SourceId, SourceName = d.SourceName, SourceIndex = d.SourceIndex,
                    Kind = d.Kind, SetFlowRate = d.FlowRate,
                    SupplyTemperature = d.SupplyTemperature, Cells = d.Cells.Length,
                };
                double flux = 0;
                for (int i = 0; i < d.OutletCount; i++)
                {
                    var target = d.Target[i];
                    float len = target.Length();
                    if (len <= 1e-9f) continue;
                    int idx = d.Cells[i];
                    var axis = d.Normal.LengthSquared() > 0.5f ? d.Normal : target / len;     // P13a-01: face flux, not the tilted jet's
                    flux += Vector3.Dot(new Vector3(field.Ux[idx], field.Uy[idx], field.Uz[idx]), axis) * cellArea;
                }
                res.FlowRate = (float)(flux / Math.Max(1, d.OutletLayers));

                if (_thermal && field.T.Length == _grid.Count && !float.IsNaN(d.SupplyTemperature))
                {
                    // Averaged over exactly the cells the unit meters itself against (its return grille,
                    // or its own outlet when no return could be placed -- see BuildDeviceData), and over
                    // exactly ToField's averaging window, which ResetAveraging restarts. Mixing windows
                    // here is what made an app run report 3682 W beside a 17 C return and 959 W beside a
                    // 19.3 C one: the duty was following the run's history, the temperature the window.
                    int unitOf = -1;
                    for (int i = 0; i < d.OutletCount && unitOf < 0; i++)
                    {
                        float u = _devData[5 * _grid.Count + d.Cells[i]];
                        if (u >= 0f) unitOf = (int)u;
                    }
                    if (unitOf >= 0)
                    {
                        int from = _unitStart[unitOf], to = _unitStart[unitOf + 1];
                        double sum = 0;
                        for (int k = from; k < to; k++) sum += field.T[_unitCells[k]];
                        if (to > from) res.ReturnTemperature = (float)(sum / (to - from));
                    }
                    // Sensible duty, from the unit's own two measurements and nothing else:
                    //     Q = rho * cp * V * (T_return - T_supply),
                    // with V the flow measured through its outlet cells just above and T_return the window
                    // average just computed. That is the number an engineer reads off a unit, it matches
                    // what the lattice actually takes out (the outlet sink is set to this same power every
                    // DeviceUnitCadence steps), and the two quantities the caller sees can no longer
                    // disagree. A unit whose return is colder than its supply is doing nothing, not
                    // heating: floor it at zero.
                    if (!float.IsNaN(res.ReturnTemperature))
                        res.HeatRemovedWatts = MathF.Max(
                            RhoCp * MathF.Abs(res.FlowRate) * (res.ReturnTemperature - d.SupplyTemperature), 0f);
                }
                field.Devices.Add(res);
            }
        }

        /// <summary>The device sums are float32, one add per step: a long window rounds each increment to ulp(S) and biases the mean
        /// (-0.9 % at 1e6 samples for u = 0.002, P17-01). They are folded into the host doubles every this many samples.</summary>
        const int FoldSteps = 8192;

        /// <summary>Device accumulators -> ADDED to the host double sum arrays ToField averages (SoA: [0,count)=rho,
        /// [count,2count)=ux, ... [5count,6count)=T when thermal), then the device sums are cleared (P17-01).</summary>
        void FoldSums()
        {
            int count = _grid.Count;
            var hSums = _dSums!.GetAsArray1D();
            for (int i = 0; i < count; i++)
            {
                _sumRho[i] += hSums[i]; _sumUx[i] += hSums[count + i]; _sumUy[i] += hSums[2 * count + i];
                _sumUz[i] += hSums[3 * count + i]; _sumNuT[i] += hSums[4 * count + i];
            }
            if (_thermal)
                for (int i = 0; i < count; i++) _sumT[i] += hSums[5 * count + i];
            _dSums!.View.MemSetToZero();
            _foldedSamples = _avgSamples;
        }

        // ---- live edits ------------------------------------------------------------------------

        /// <summary>The boundary list for <paramref name="g"/> and <paramref name="wind"/> (a nested fine box keeps only its Fixed tail),
        /// plus 6b's top lid (a wind run on a grid whose top is not open; a nested box keeps it where it reaches the domain top) and outflow
        /// sponge (not on a nested fine box, which has its own).</summary>
        void BuildRing(VoxelGrid g, Vector2 wind)
        {
            float speed = wind.Length();
            _bl = BuildBoundaryList(g, wind, speed, wind.X * _latPerPhys, wind.Y * _latPerPhys, _profileExp, _latPerPhys, _model.GroundZ,
                lid: !g.OpenZMax && speed > 1e-6f, equilibrium: g.OpenZMax);
            if (_nestedRing) _bl = FixedOnly(_bl);
            _spongeFaces = _nestedRing || g.OpenZMax ? 0 : OutflowFaces(wind) | OutflowFaces(-wind) << 4;   // RZ-01: bits 4-7 = inflow faces (absorbing layer only)
            _spongeCells = _spongeFaces == 0 ? 0 : SpongeDepth(g);
            _dims.SpongeFaces = _spongeFaces; _dims.SpongeCells = _spongeCells;
        }

        /// <summary>Changes the inflow while the solve runs: the boundary ring is reclassified and its
        /// target velocities rebuilt (wind profile included) against the new vector.
        /// <para>LIMITATION: the lattice scaling (dt, tau0, the physical-to-lattice velocity factor and so
        /// SimSeconds) is fixed when the session starts, so the new speed may not exceed
        /// <see cref="MaxWindSpeed"/> -- <c>WindChangeFactor</c> x the speed the lattice was scaled for.
        /// A faster wind needs a new <see cref="Start"/>. Slower is always allowed (including 0, which
        /// switches the whole ring to the open/no-wind boundary). The change is applied at full strength
        /// immediately -- the inlet ramp only covers the first <see cref="RampSteps"/> steps of a run.</para></summary>
        public void SetWind(Vector2 wind)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_inert) throw new InvalidOperationException("This session has no flow drivers (no wind, no sources, no heat); call Start again with the new wind");
            float speed = wind.Length();
            if (speed > MaxWindSpeed)
                throw new InvalidOperationException(
                    $"SetWind: {speed:F2} m/s is above this session's ceiling of {MaxWindSpeed:F2} m/s " +
                    $"({WindChangeFactor:F2}x the speed its lattice was scaled for); call Start again to restart at the new speed");

            _wind = wind;
            int spongeFaces0 = _spongeFaces, spongeCells0 = _spongeCells;
            BuildRing(_grid, wind);
            _supply = BuildSupplyLoops(_supply);
            if (_useGpu) AllocateBoundaryGpu();
            RefreshHoldAfterRing(_spongeFaces != spongeFaces0 || _spongeCells != spongeCells0);
            ResetAveraging();
        }

        /// <summary>The fastest wind <see cref="SetWind"/> accepts without restarting the session.</summary>
        public float MaxWindSpeed => WindChangeFactor * ULat / MathF.Max(_profileTop * _latPerPhys, 1e-12f);

        /// <summary>The largest physical speed, m/s, this session's fixed lattice scaling can carry anywhere:
        /// WindChangeFactor x the uMax it was started with, so the wind at the top of the profile (the limit
        /// behind MaxWindSpeed), a Fixed patch or a device's peak target speed after UpdateCells must stay at
        /// or below it -- anything faster needs a new Start.</summary>
        public float MaxSpeed => WindChangeFactor * ULat / MathF.Max(_latPerPhys, 1e-12f);

        /// <summary>Swaps in new cell flags (an opening was opened or closed) on the SAME domain: Nx/Ny/Nz,
        /// Dx and Origin must match. Cells that were Solid and are now Air start from rest equilibrium
        /// (rho = 1, u = 0, T = the same indoor/outdoor split the thermal init uses); every other cell keeps
        /// its populations, so the flow warm-starts instead of restarting. The session adopts
        /// <paramref name="newGrid"/> (it is what <see cref="ToField"/> reports).</summary>
        public void UpdateCells(VoxelGrid newGrid, BuildingModel? model = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var old = _grid;
            if (newGrid.Nx != old.Nx || newGrid.Ny != old.Ny || newGrid.Nz != old.Nz
                || newGrid.Dx != old.Dx || newGrid.Origin != old.Origin)
                throw new ArgumentException(
                    $"UpdateCells needs the same domain: got {newGrid.Nx}x{newGrid.Ny}x{newGrid.Nz} dx={newGrid.Dx} origin={newGrid.Origin}, " +
                    $"session has {old.Nx}x{old.Ny}x{old.Nz} dx={old.Dx} origin={old.Origin}", nameof(newGrid));
            if (_inert) throw new InvalidOperationException("This session has no flow drivers (no wind, no sources, no heat); call Start again with the new grid");

            var nextModel = model ?? _model;
            if (RequiredSourceSpeed(newGrid,nextModel) > MaxSpeed + 1e-6f)
                throw new InvalidOperationException("Updated source speed exceeds the session's lattice scale; restart the session");
            if (_thermal && model != null && !ReferenceEquals(model,_model))
                throw new InvalidOperationException("Thermal model edits require a restart to recompute buoyancy and lattice scaling");
            int count = old.Count;
            var oldCell = old.Cell;
            _source = newGrid; _grid = newGrid = newGrid.LbmView();
            if (model != null) _model = model;
            _dims.OpenZMin = newGrid.OpenZMin ? 1 : 0; _dims.OpenZMax = newGrid.OpenZMax ? 1 : 0;
            _nearBoundary = ComputeCellFlags(newGrid);
            if (_thermal) (_T0, _heatRate, _surfaceFixed, _surfaceTemp, _, _, _surfaceCoef) = BuildThermal(newGrid, _model, _surfaceResolved);
            BuildRing(newGrid, _wind);
            if (_thermal) BuildHold();
            _supply = BuildSupplyLoops(model == null ? _supply : null);   // a model edit rebuilds the loops from scratch (controller scale/integral restart); geometry-only keeps them
            (_devData, _unitCells, _unitStart, _devUnit) = BuildDeviceData(newGrid, _latPerPhys, _dt, _thermal,
                OpeningLossCells(newGrid, _model, _openingModel, _nuFloor), _thermal ? PlumeDriveCells(newGrid, _model) : null);
            _hasDevices = _devData.Length > 0;

            SetupRoomFlux();

            if (_useGpu)
            {
                var accel = _acc!;
                EnsureGpuLive();
                var dNewCell = accel.Allocate1D(newGrid.Cell);
                if (_thermal) _dT0!.View.CopyFromCPU(_T0);   // before the reset: a cell that was Solid held T0 = 0 in the old buffer
                bool g7 = _thermal && !_hybrid;
                _resetNewAirKernel!(count, dNewCell.View, _dCellType!.View, _dCur!.View, _dNxt!.View,
                    g7 ? _dCurG!.View : _dCur!.View, g7 ? _dNxtG!.View : _dCur!.View,
                    g7 ? _dT0!.View : _dCur!.View, count, (byte)(g7 ? 1 : 0));
                if (_hybrid) _hybridResetKernel!(count, dNewCell.View, _dCellType!.View, _dTCur!.View, _dTNxt!.View, _dT0!.View);
                accel.Synchronize();
                _dCellType!.Dispose();
                _dCellType = dNewCell;
                _dNearBoundary!.Dispose();
                _dNearBoundary = accel.Allocate1D(_nearBoundary);
                _dSums!.Dispose(); _dSums = accel.Allocate1D<float>((long)count * SumPlanes);
                AllocateRoomFluxGpu();
                // The device set (and so these buffers' length) can change with the grid, so reallocate
                // rather than copy into them -- same reason AllocateBoundaryGpu reallocates.
                if (_thermal)
                {
                    _dCellData!.Dispose();
                    _dCellData = accel.Allocate1D(BuildCellData());
                    AllocateHoldGpu();
                    _dUnitCells?.Dispose(); _dUnitT?.Dispose();
                    _dUnitCells = null; _dUnitT = null;
                    if (_unitCells.Length > 0)
                    {
                        _dUnitCells = accel.Allocate1D(_unitCells);
                        _dUnitT = accel.Allocate1D<float>(_unitCells.Length);
                        UpdateDeviceUnits();
                    }
                }
                else if (_hasDevices)
                {
                    EnsureGpuDevice();
                    _dDevData?.Dispose();
                    _dDevData = accel.Allocate1D(_devData);
                }
                else if (_wmPath)
                {
                    _dDevData?.Dispose();
                    _dDevData = accel.Allocate1D<float>(1L);   // dummy, see InitGpu
                }
                if (_wmOn) _dWm!.View.MemSetToZero();   // wall nodes change with the cells: restart the slip state
                AllocateBoundaryGpu();
                accel.Synchronize();
            }
            else
            {
                if (_wmOn) Array.Clear(_wm);
                if (_thermal && _devUnit.Length > 0) UpdateDeviceUnits();
                var newCell = newGrid.Cell;
                var cur = _cur; var nxt = _nxt; var curG = _curG; var nxtG = _nxtG;
                bool thermal = _thermal, hybrid = _hybrid; var T0 = _T0; var tCur = _tCur; var tNxt = _tNxt;
                F19 feq = ComputeFeq(1f, 0f, 0f, 0f);
                Parallel.For(0, count, i =>
                {
                    if (newCell[i] != VoxelGrid.Air || oldCell[i] != VoxelGrid.Solid) return;
                    WriteAoS(cur, i, feq); WriteAoS(nxt, i, feq);
                    if (hybrid) { tCur[i] = T0[i]; tNxt[i] = T0[i]; }
                    else if (thermal)
                    {
                        G7 geq = ComputeGeq(T0[i], 0f, 0f, 0f);
                        WriteAoSG(curG, i, geq); WriteAoSG(nxtG, i, geq);
                    }
                });
            }
            if (_thermal) UpdateHold(0f);

            ResetAveraging();
        }

        /// <summary>Throws away the running time-average (not the flow): the samples taken before a
        /// SetWind/UpdateCells belong to a different problem, and <see cref="SetWind"/>/<see cref="UpdateCells"/>
        /// therefore call this themselves. Public so a host that changes the problem some other way -- or that
        /// simply wants <see cref="ToField"/> to describe the regime from NOW on rather than a mean over the
        /// last minute of a different one -- can restart the accumulation without restarting the solve.
        /// <para>The next <see cref="ToField"/> reports the mean of however few samples have been taken
        /// since; with 0 samples every Air cell reads back exactly 0.</para></summary>
        public void ResetAveraging(bool startNow = false)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _avgSamples = 0; _foldedSamples = 0;
            ResetAge();
            Array.Clear(_holdDutySum); _holdDutySteps = 0;
            if (startNow) _averageFromStep = _steps;
            Array.Clear(_fluxSums);
            _dFluxSums?.View.MemSetToZero();
            Array.Clear(_opSums);
            if (_inert) return;   // no lattice, no accumulators (an inert session never allocated _dSums)
            if (_useGpu) _dSums!.View.MemSetToZero();
            Array.Clear(_sumRho); Array.Clear(_sumUx); Array.Clear(_sumUy); Array.Clear(_sumUz); Array.Clear(_sumNuT);   // the host doubles hold the folded GPU sums too (P17-01)
            if (_thermal) Array.Clear(_sumT);
        }

        // ---- teardown --------------------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            try { DisposeBuffers(); }
            finally { if (_gpuCounted) { _gpuCounted = false; ReleaseGpuSession(); } }   // RW-02: the last session to close performs a pending ResetGpu
        }

        void DisposeBuffers()
        {
            _disposed = true;
            if (_hybrid && System.Environment.GetEnvironmentVariable("WF_HYBRID_DIAG") == "1")
                Console.Error.WriteLine($"[hybrid] steps={_steps} minT={_hybMinT:F4} (initial {_hybInitMinT:F4}) maxT={_hybMaxT:F3} cfl={_hybMaxCfl:F3} 6k={_hybMaxDiff:F3}");
            _cur = []; _nxt = []; _curG = []; _nxtG = []; _tCur = []; _tNxt = []; _hyb = []; _instant = []; _fluxSums = [];
            _dFluxMeta?.Dispose(); _dFluxSums?.Dispose(); _dFluxSeg?.Dispose();
            if (!_useGpu) return;
            DisposeAge();
            DisposeHybridGpu();
            _dHoldAll?.Dispose(); _dHoldT?.Dispose();
            _dCellType?.Dispose(); _dNearBoundary?.Dispose(); _dBClass?.Dispose(); _dBNxNy?.Dispose();
            _dDir?.Dispose(); _dBIndex?.Dispose(); _dBNeighborSlot?.Dispose(); _dSampleIdx?.Dispose(); _dBad?.Dispose();
            _dCur?.Dispose(); _dNxt?.Dispose(); _dCurG?.Dispose(); _dNxtG?.Dispose(); _dSums?.Dispose();
            _dBVel?.Dispose(); _dT0?.Dispose(); _dCellData?.Dispose(); _dSampleRho?.Dispose();
            _dUx?.Dispose(); _dUy?.Dispose(); _dUz?.Dispose(); _dDevData?.Dispose();
            _dUnitCells?.Dispose(); _dUnitT?.Dispose(); _dWm?.Dispose(); _dWm = null;
            _dTuK?.Dispose(); _dTuBase?.Dispose(); _dTuPos?.Dispose(); _dTuSig?.Dispose(); _dTuModes?.Dispose(); _dTuPh?.Dispose();
            _dTuK = null; _dTuBase = null; _dTuPos = null; _dTuSig = null; _dTuModes = null; _dTuPh = null;
            _dCellType = null; _dNearBoundary = null; _dBClass = null; _dBNxNy = null;
            _dDir = null; _dBIndex = null; _dBNeighborSlot = null; _dSampleIdx = null; _dBad = null;
            _dCur = null; _dNxt = null; _dCurG = null; _dNxtG = null; _dSums = null;
            _dBVel = null; _dT0 = null; _dCellData = null; _dSampleRho = null;
            _dUx = null; _dUy = null; _dUz = null; _dDevData = null; _dUnitCells = null; _dUnitT = null;
        }
    }
}

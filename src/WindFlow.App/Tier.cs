using System.Diagnostics;
using System.Numerics;
using Vortice.DXGI;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>What this machine gets: the live solver's cell budget, the particle count and the idle frame cap.</summary>
public sealed record TierChoice(string Name, int TargetCells, int Particles, float FrameCap,
    double ProbeMlups, double VramGb, string Adapter, string Source, int AccurateCells);

/// <summary>PLAN_V2 2.4. Once per session, ~2 s: step a live LbmSession on the sample house at a small grid and
/// read its throughput, read the render adapter's dedicated VRAM from DXGI, and pick a row of <see cref="Table"/>.
/// The live solver waits for the answer on its own thread (never the UI), so the probe never shares the GPU with
/// a real run.</summary>
public static class StartupTier
{
    /// <summary>The ladder, best first. The solver half of a row (cells) applies when the probe reaches
    /// <c>MinMlups</c>; the viewport half (particles, idle frame cap) when the render adapter has <c>MinVramGb</c> of
    /// its own memory. The probe runs a ~425k-cell grid, so it reads below the solver's peak on a 1.5M grid.
    /// Measured on the RTX 5070 Ti, 2026-09-28: 1530-2665 MLUPS depending on how busy another process kept the GPU,
    /// and 679 once while a validation run held it at 99 %; the high threshold sits below that so a busy card is
    /// not demoted. Particles follow the visual ceiling set from screenshots of the sample house
    /// (<see cref="ParticleAuto.VisualCeiling"/>): more than that adds clutter, not information.
    /// <para>Accurate: the whole-domain grid of the slower, reportable pass (Accurate.cs), about 3.3x the Preview
    /// cells (1.5x finer cells) on every row, so the pass takes tens of seconds at 60 % GPU rather than minutes.
    /// Clamped at run time to what VRAM holds next to the idle Preview session (AccurateBudget.VramCap).</para></summary>
    public static readonly (string Name, double MinMlups, double MinVramGb, int Cells, int Particles, float Fps, int Accurate)[] Table =
    [
        // Discrete GPU, CUDA-class: today's Preview budget (Simulation.TargetCells Preview/GPU), full particles.
        ("high", 600, 3.5, 1_500_000, ParticleAuto.VisualCeiling, 60, 5_000_000),
        // Mid discrete or a strong OpenCL part: two thirds of the cells, a thinner cloud.
        ("medium", 200, 1.5, 1_000_000, 60_000, 60, 3_000_000),
        // Integrated / OpenCL: a coarse grid and a 30 fps ceiling, which keeps an iGPU above 15 fps.
        ("low", 40, 0, 500_000, 30_000, 30, 1_200_000),
        // CPU solver (no usable GPU): Core's CPU Preview budget.
        ("minimal", 0, 0, 250_000, 30_000, 30, 600_000),
    ];

    const int ProbeCells = 400_000;
    const double ProbeSeconds = 1.6;

    static Task<TierChoice>? current;

    /// <summary>The session's tier, started on first use. <paramref name="injectMlups"/> (--tier-probe) skips the
    /// measurement and feeds that number to the table instead, for testing a tier this machine is not.</summary>
    public static Task<TierChoice> Begin() => current ??= Task.Run(Measure);

    /// <summary>Solver thread: block until the probe is done (it owns the GPU for its ~2 s), at most 20 s.</summary>
    public static void WaitReady(CancellationToken ct = default)
    {
        try { current?.Wait(TimeSpan.FromSeconds(20), ct); }
        catch (OperationCanceledException) { throw; }       // S4-01: Stop() ends the wait; the solver loop treats it as the stop it is
        catch { /* a failed probe already logged itself */ }
    }

    public static TierChoice? Chosen => current is { IsCompletedSuccessfully: true } t ? t.Result : null;

    /// <summary>The live solver's cell budget: the tier's for a GPU Preview run, Core's preset otherwise. Called on
    /// the solver thread; waits for the probe (at most a few seconds, and only on the first run).</summary>
    public static int CellBudget(Quality quality, bool gpu, CancellationToken ct = default)
    {
        if (quality != Quality.Preview || !gpu || current == null) return Simulation.TargetCells(quality, gpu);
        try { return current.Wait(TimeSpan.FromSeconds(20), ct) ? current.Result.TargetCells : Simulation.TargetCells(quality, gpu); }
        catch (OperationCanceledException) { throw; }
        catch { return Simulation.TargetCells(quality, gpu); }
    }

    /// <summary>The Accurate pass's tier budget before the VRAM clamp; the CPU row when there is no usable GPU.</summary>
    public static int AccurateCells(bool gpu) =>
        !gpu ? Table[^1].Accurate : Chosen?.AccurateCells ?? Table[0].Accurate;

    /// <summary>The solver and the viewport can be different GPUs (CUDA picks the discrete card even when the
    /// window renders on the integrated one, --adapter), so the two halves of a row are picked separately: the
    /// cell budget from the solver probe's MLUPS, the particles and frame cap from the render adapter's VRAM.</summary>
    public static TierChoice Pick(double mlups, double vramGb, string adapter, string source)
    {
        var solver = Table.FirstOrDefault(r => mlups >= r.MinMlups, Table[^1]);
        var view = Table.FirstOrDefault(r => vramGb >= r.MinVramGb, Table[^1]);
        // A slow solver on the same card as the viewport is also a slow viewport: never render above the solver row.
        if (Array.IndexOf(Table, view) < Array.IndexOf(Table, solver)) view = solver;
        string name = solver.Name == view.Name ? solver.Name : $"{solver.Name} solver, {view.Name} viewport";
        return new TierChoice(name, solver.Cells, view.Particles, view.Fps, mlups, vramGb, adapter, source, solver.Accurate);
    }

    static TierChoice Measure()
    {
        var (adapter, vramGb) = RenderAdapter();
        TierChoice tier;
        if (Options.TierProbe > 0)
        {
            tier = Pick(Options.TierProbe, vramGb, adapter, "injected by --tier-probe");
        }
        else
        {
            double mlups = 0;
            string source;
            try { mlups = Probe(out source); }
            catch (Exception e)
            {
                source = $"probe failed ({e.GetType().Name}: {e.Message})";
                Log.Warn("tier: " + source);
            }
            tier = Pick(mlups, vramGb, adapter, source);
        }
        Log.Info($"tier: {tier.Name} · probe {tier.ProbeMlups:0} MLUPS ({tier.Source}) · render adapter {tier.Adapter}, " +
                 $"{tier.VramGb:0.0} GB dedicated · -> {tier.TargetCells / 1000:N0}k cells, {tier.Particles / 1000}k particles, " +
                 $"{tier.FrameCap:0} fps idle cap");
        return tier;
    }

    /// <summary>~2 s: voxelise the sample house at <see cref="ProbeCells"/>, warm up, then time whole bursts with a
    /// wall clock (Step ends in a synchronise, so this is the real rate, not a kernel-only figure).</summary>
    static double Probe(out string source)
    {
        var total = Stopwatch.StartNew();
        var model = SampleBuilding.Build();
        var options = new RunOptions { Quality = Quality.Preview, Backend = WindFlow.Core.Backend.Auto };
        var grid = Voxelizer.Build(model, ProbeCells, Math.Max(2, System.Environment.ProcessorCount - 2));
        using var session = LbmSolver.Start(grid, model, new Vector2(4, 0), options);
        session.Step(20);                                   // first-touch allocations and caches
        long steps = 0;
        int burst = 10;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < ProbeSeconds)
        {
            session.Step(burst);
            steps += burst;
            if (sw.Elapsed.TotalSeconds < 0.2) burst = Math.Min(burst * 2, 400);
        }
        double seconds = sw.Elapsed.TotalSeconds;
        double mlups = (double)grid.Count * steps / seconds / 1e6;
        source = $"{session.Backend}, {grid.Count / 1000}k cells x {steps} steps in {seconds:0.00} s, {total.Elapsed.TotalSeconds:0.0} s total";
        return mlups;
    }

    /// <summary>The adapter the viewport will render on (same rule as the renderer: --adapter, else high
    /// performance) and its dedicated VRAM. Integrated parts report little or none.</summary>
    static (string Name, double VramGb) RenderAdapter()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
            using var adapter = Viewport.Renderer.PickAdapter(factory, Options.Adapter);
            if (adapter == null) return ("default adapter", 0);
            var d = adapter.Description1;
            return (d.Description.Trim(), (ulong)d.DedicatedVideoMemory / (1024.0 * 1024 * 1024));
        }
        catch (Exception e)
        {
            return ($"unknown ({e.GetType().Name})", 0);
        }
    }
}

/// <summary>Runtime particle budget: start at the tier's count, step down when the viewport cannot hold its frame
/// cap, step back up after a long stretch of headroom. No GPU, no clock of its own: the status tick feeds it fps
/// samples, so --selfcheck can drive it with fake ones.
/// <para>Hysteresis: down after <see cref="DownSeconds"/> below 90 % of the cap; up only after
/// <see cref="UpSeconds"/> at the cap, and that wait doubles (to 160 s) every time a step up to the same count had
/// to be taken back, so a borderline machine settles instead of see-sawing.</para></summary>
public sealed class ParticleAuto
{
    /// <summary>Set from screenshots of the sample house at 60k / 100k / 250k (2026-09-28): see the tier table.</summary>
    public const int VisualCeiling = 100_000;
    /// <summary>The ladder. Auto stops at <see cref="VisualCeiling"/>; a locked result raises the ceiling to the
    /// measured GPU limit (<see cref="SetCeiling"/>), which is what the steps above 250k are for.</summary>
    public static readonly int[] Steps = [30_000, 60_000, 100_000, 150_000, 250_000, 400_000, 600_000, 1_000_000, 1_500_000, 2_000_000];
    public const double DownSeconds = 3, UpSeconds = 10, UpMaxSeconds = 160;
    public const float LowFraction = 0.90f, HeadroomFraction = 0.97f;

    int ceiling;
    readonly int[] failures = new int[Steps.Length];
    int index, upTo = -1;
    double lowFor, highFor, clock, upAt;
    float lastCap;

    public ParticleAuto(int start)
    {
        ceiling = Math.Max(0, Array.FindLastIndex(Steps, s => s <= VisualCeiling));
        index = Math.Min(Nearest(start), ceiling);
    }

    public int Count => Steps[index];

    /// <summary>The largest step at or below <paramref name="count"/> becomes the ceiling; the count follows it down
    /// at once, and <paramref name="jump"/> takes it straight up to the new ceiling (Lock: the limit was measured,
    /// so there is nothing to probe for).</summary>
    public void SetCeiling(int count, bool jump)
    {
        ceiling = Math.Max(0, Array.FindLastIndex(Steps, s => s <= count));
        if (index > ceiling || jump) index = ceiling;
        lowFor = highFor = 0;
    }

    static int Nearest(int count)
    {
        int best = 0;
        for (int i = 0; i < Steps.Length; i++)
            if (Math.Abs(Steps[i] - count) < Math.Abs(Steps[best] - count)) best = i;
        return best;
    }

    /// <summary>One sample: <paramref name="dt"/> seconds at <paramref name="fps"/> under a cap of
    /// <paramref name="cap"/> (0 = uncapped, where nothing is judged). Returns the new count, or 0 when unchanged;
    /// <paramref name="reason"/> says why for the log.</summary>
    public int Update(double dt, float fps, float cap, out string reason)
    {
        reason = "";
        clock += dt;
        if (cap <= 0 || fps <= 0 || cap != lastCap)
        {
            lastCap = cap;          // a new cap (solving vs idle vs background) is a new question: start over
            lowFor = highFor = 0;
            return 0;
        }
        if (fps < LowFraction * cap) { lowFor += dt; highFor = 0; }
        else if (fps >= HeadroomFraction * cap) { highFor += dt; lowFor = 0; }
        else { lowFor = 0; highFor = 0; }

        if (lowFor >= DownSeconds && index > 0)
        {
            reason = $"{fps:0.#} fps under {LowFraction * 100:0} % of the {cap:0} fps cap for {lowFor:0.#} s";
            // A step up taken back within 30 s: that count is borderline here, so wait twice as long next time.
            if (index == upTo && clock - upAt < 30) failures[index] = Math.Min(failures[index] + 1, 4);
            index--;
            lowFor = highFor = 0;
            return Count;
        }
        double wait = Math.Min(UpSeconds * Math.Pow(2, failures[Math.Min(index + 1, Steps.Length - 1)]), UpMaxSeconds);
        if (highFor >= wait && index < ceiling)
        {
            reason = $"{fps:0.#} fps at the {cap:0} fps cap for {highFor:0.#} s";
            index++;
            upTo = index;
            upAt = clock;
            lowFor = highFor = 0;
            return Count;
        }
        return 0;
    }
}

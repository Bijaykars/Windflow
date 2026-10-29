using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using WindFlow.App.Viewport;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>Drives a <see cref="LbmSolver.LbmSession"/> on its own thread and publishes the instantaneous
/// velocity field to the particle system about ten times a second, so the flow develops from rest on screen.
/// The UI thread only enqueues commands and reads the volatile counters below.
/// One thread touches the session: every command is applied between bursts, never concurrently.</summary>
public sealed class LiveSolver : IDisposable
{
    public enum Mode { Stopped, Starting, Running, Paused, Failed }

    const double MaxBurstMs = 80;      // an analysis pull longer than twice this moves stats to pause-only
    // GPU budget governor (PLAN_V2 2.1): bursts aim at BurstTargetMs and are clamped to BurstCapMs, then the
    // thread sleeps so busy / (busy + idle) matches GpuTargetPercent. Short bursts also leave the renderer
    // a gap every ~12 ms, which is what the old frame-time back-off was for.
    const double BurstTargetMs = 9;
    const double BurstCapMs = 12;
    const double MinPublishInterval = 1 / 12.0;   // a field upload costs the RENDER gpu; 12 Hz is plenty
    const double AccuratePublishInterval = 0.25;  // the Accurate pass's averaging-phase publish: 4 Hz
    // Every pass allocates a fresh FlowField (four arrays the size of the grid) inside Core, which lands on
    // the Large Object Heap; five seconds keeps that off the frame-time budget.
    const double AnalysisPeriod = 5.0;

    // ---- published to the UI thread ----
    public volatile Mode State = Mode.Stopped;
    public volatile string Backend = "";
    public volatile string Error = "";
    /// <summary>P17-07: why this run is on the CPU when it asked for a GPU (none found, or the device stopped and the run was restarted); "" otherwise. Shown in the status line.</summary>
    public volatile string GpuNote = "";
    bool gpuRestarted;                       // P17-07: this run has used its one restart on the CPU
    public volatile string Trust = "";
    public volatile string RoomLine = "";
    /// <summary>Room id -> the name the panel shows (RoomNames: "Room 2 (west, 43 m²)", renames), set by the UI; a
    /// snapshot replaced whole, so the stats thread can read it. The status line names rooms the same way.</summary>
    public volatile IReadOnlyDictionary<string, string>? RoomNames;
    string NameOf(Room r) => RoomNames is { } names && names.TryGetValue(r.Id, out var n) ? n : r.Name;
    string NameOfId(string id) => RoomNames is { } names && names.TryGetValue(id, out var n) ? n : model.Rooms.FirstOrDefault(r => r.Id == id)?.Name ?? id;
    public volatile int Cells;
    public volatile float Dx;                       // metres per cell, so the UI can show resolution
    public volatile float SimSeconds, Mlups, FieldHz, BurstMs, LastCommandMs;
    /// <summary>How much physical time the running time-average covers, and how much it needs before the
    /// ledger is worth showing: one flow-through of the building (or of the simulation box) at the current
    /// wind speed. Core resets the average itself on SetWind/UpdateCells, so this is the whole story.</summary>
    public volatile float AverageSeconds, AverageTarget;
    public volatile bool HoldWaiting;               // a hold is waiting for a full averaging window
    /// <summary>What the active simulation box did to the model: which rooms its faces cut through, how many
    /// rooms it dropped, how much of the boundary ring is air. <see cref="BoxAudit.None"/> without a box.</summary>
    public volatile BoxAudit Audit = BoxAudit.None;
    /// <summary>A nested fine box is running: the coarse whole-building session drives a Dx/2 session on the
    /// box (LbmSolver.StartNested), and the published field, the rooms and openings inside the box and the
    /// probe come from the fine one. False without a box, in v1 mode ("Box is the whole domain") and when
    /// the run is thermal (nesting is isothermal only).</summary>
    public volatile bool Nested;
    public volatile int FineCells;
    public volatile float FineDx, FineMlups, CoarseMlups;
    /// <summary>The nested box's sponge (blend) band depth in metres, and whether its bottom and top faces are driven
    /// (a box standing on the ground or reaching the domain top has no band there). 0 / false without a box.</summary>
    public volatile float SpongeWidth;
    public volatile bool NestBottom, NestTop;
    /// <summary>Core's notes on the fine box grid (a face partly inside the building, a face closed off).</summary>
    public volatile string[] NestNotes = [];
    /// <summary>C4: set when the averaged grid was clamped to graphics memory (Envelope.BudgetClampNote); empty otherwise. Shown with the Accurate result's notes.</summary>
    public volatile string GridNote = "";
    /// <summary>Why a box asked for as nested is running as v1 (a thermal run), or "".</summary>
    public volatile string BoxModeNote = "";
    /// <summary>The fine field's own analysis pass, for probes inside <see cref="FineBox"/>.</summary>
    public volatile SimulationResult? FineStats;
    /// <summary>The fine box as solved (snapped outward to coarse cells), null when not nested.</summary>
    public DomainBox? FineBox
    {
        get => System.Threading.Volatile.Read(ref fineBox) is DomainBox b ? b : null;
        private set => System.Threading.Volatile.Write(ref fineBox, value is { } b ? b : null);
    }

    object? fineBox;
    /// <summary>The box the solver is actually running: a rejected box never gets here, so the UI can put the
    /// gizmo back where the solve really is.</summary>
    public DomainBox? ActiveDomain
    {
        // DomainBox is a struct, so Nullable<DomainBox> cannot be volatile: publish it boxed instead.
        get => System.Threading.Volatile.Read(ref activeDomain) is DomainBox b ? b : null;
        private set => System.Threading.Volatile.Write(ref activeDomain, value is { } b ? b : null);
    }

    object? activeDomain;
    /// <summary>Why the last box was refused, and a counter the UI polls to notice it happened.</summary>
    public volatile string BoxNote = "";
    public volatile int BoxRejects;
    public volatile bool Stale;
    public volatile bool AnalysisOnPauseOnly;
    public volatile int Publishes, Pauses, Bursts;
    public volatile float AnalysisMs;
    public volatile string LastEvent = "";
    public volatile int CommandFailures;
    /// <summary>Name of the model the solver side is running (its snapshot), for headless checks.</summary>
    public string ModelName => Volatile.Read(ref model).Name;
    /// <summary>Set while a batch job (sweep, annual) owns the device: the paused session must not keep
    /// re-analysing a model whose openings that job is flipping.</summary>
    public volatile bool SuspendAnalysis;

    /// <summary>Share of wall time the solver may keep the GPU busy, 5-100. Read before every burst.</summary>
    public volatile int GpuTargetPercent = 60;
    /// <summary>Measured over the last second: busy / wall (host-timed Step + field readback, both of which
    /// synchronise), lattice steps per second, sim seconds per wall second, and the solver thread's own CPU
    /// time per wall second (1 = one core flat out).</summary>
    public volatile float Duty, StepsPerSecond, SimRate, SolverCpu;

    // ---- Preview, then Accurate (Accurate.cs) ----
    // The Accurate pass REPLACES the stepping: the Preview session stays resident but idle while a finer whole-domain
    // session, warm-started from Preview's average, takes the GPU, and the particles follow it as it develops.
    // Running both would halve each on the one GPU and one solver thread; keeping Preview resident lets an edit
    // warm-start it at once instead of from rest.
    public readonly AccurateTracker Accurate = new();
    /// <summary>Set by the UI: the Expert cell budget for the next Accurate pass, 0 = auto (startup tier).</summary>
    public volatile int AccurateCellsTyped;
    /// <summary>The recipe of the next Accurate pass and, for High, the focus room (Id or Name) to put a 2:1 fine box around.</summary>
    public volatile AccurateMode AccurateModeWanted;
    /// <summary>Set by the UI (Expert, Settings): the wall treatment every session opened from now on runs with. Starts at Core's
    /// default (None, or Werner-Wengle under env WINDFLOW_WALL_MODEL=ww); experimental, isothermal only (a thermal run ignores it).
    /// Applied through Start and Reconfigure, like Thermal.</summary>
    public volatile WallModel WallModelWanted = new RunOptions().WallModel;
    /// <summary>6j (P14-01), set by the UI: thermal sessions opened from now on hold each room at the indoor temperature with an ideal
    /// heater/cooler (RunOptions.HoldIndoorTemperature); false = free-running. Applied through Start and Reconfigure, like Thermal.</summary>
    public volatile bool HoldIndoorWanted = new RunOptions().HoldIndoorTemperature;
    /// <summary>Set by the UI (Expert, Settings): the wind domain every grid built from now on uses (6c, RunOptions.DomainMode).
    /// Compact by default; Full is sized for the wind direction, so a direction change rebuilds the grid. Applied through Start and
    /// Reconfigure.</summary>
    public volatile DomainMode DomainModeWanted = DomainMode.Compact;
    volatile string? accurateFocus;
    public string? AccurateFocusRoom => accurateFocus;
    /// <summary>UI entry point of High Accuracy: <see cref="AccurateMode.High"/> = the Accurate pass plus per-supply turbulence,
    /// EN 1991-1-4 approach turbulence and, with <paramref name="focusRoomId"/> (the selected or isolated room), a 2:1 fine box around
    /// it (<see cref="HighAccuracy.Plan"/>: sponge-aware margin, VRAM check; thermal or an unfit box = no box, with a warning in
    /// <see cref="AccuratePlan"/>). <see cref="AccurateMode.Standard"/> is the pass as before. Takes effect on the next pass; a pass
    /// already running or finished with another recipe is dropped and the Preview-then-Accurate cycle restarts. Safe before Start.</summary>
    public void SetAccurateMode(AccurateMode mode, string? focusRoomId = null)
    {
        if (string.IsNullOrWhiteSpace(focusRoomId)) focusRoomId = null;
        bool changed = mode != AccurateModeWanted || !string.Equals(focusRoomId, accurateFocus, StringComparison.Ordinal);
        if (changed && mode == AccurateMode.Ultra) { ultraFaults = 0; ultraOnlyOwn = false; ultraRunId = Guid.NewGuid().ToString("N"); }
        AccurateModeWanted = mode;
        accurateFocus = focusRoomId;
        if (changed && Accurate.Phase is AccuratePhase.Settling or AccuratePhase.Averaging or AccuratePhase.Finishing or AccuratePhase.Accurate)
            commands.Enqueue(new AccurateModeCommand(Stopwatch.GetTimestamp()));
    }
    public volatile int AccurateCells;
    public volatile float AccurateDx;
    /// <summary>The finished Accurate analysis, null while the result on screen is Preview.</summary>
    public volatile SimulationResult? AccurateStats;
    /// <summary>Wall seconds from the last start or edit to a settled Preview, and from the Accurate pass's start to
    /// its finished numbers. NaN until measured in the current cycle.</summary>
    public volatile float PreviewSettleSeconds = float.NaN, AccuratePassSeconds = float.NaN;
    /// <summary>Last pass: wall seconds of stepping (open to averaged), its seed and its step plan, for the timings.</summary>
    public volatile float AccurateStepSeconds = float.NaN;
    public string AccuratePlan { get; private set; } = "";

    /// <summary>True when <paramref name="r"/> is the finished Accurate result (a hold of it included).</summary>
    public bool IsAccurate(SimulationResult? r) => r != null && ReferenceEquals(r, AccurateStats);

    readonly ParticleSystem particles;
    readonly ConcurrentQueue<Command> commands = new();
    // Core honours MaxDegreeOfParallelism in ToField, Analysis.Analyze and Voxelizer.Build. Those three
    // used to saturate every core for 100-400 ms at a time, which the render thread felt as a 1% low.
    static readonly int AnalysisWorkers = Math.Max(2, System.Environment.ProcessorCount - 2);

    RunOptions options = new()
    {
        Quality = Quality.Preview,
        Backend = WindFlow.Core.Backend.Auto,
        MaxDegreeOfParallelism = AnalysisWorkers,
    };

    Thread? thread;
    volatile bool stop;
    CancellationTokenSource cancel = new();

    BuildingModel model = new();
    Vector2 wind;
    int cellTarget;
    /// <summary>The whole-building cell budget, for predicting a v1 box's cell size.</summary>
    public int CellTarget => cellTarget;

    abstract record Command(long Issued);
    // Geometry and Config carry the model as it was when the UI issued them. The solver thread only ever reads
    // these snapshots, never the UI's live BuildingModel, which the UI keeps editing (devices, openings,
    // occupants) while a 300-400 ms voxelisation is running on this side.
    sealed record GeometryCommand(long Issued, string Why, BuildingModel Model) : Command(Issued);
    sealed record WindChanged(long Issued, Vector2 Wind) : Command(Issued);
    sealed record PauseToggle(long Issued, bool Paused) : Command(Issued);
    sealed record HoldCommand(long Issued, int Slot) : Command(Issued);
    sealed record ConfigCommand(long Issued, bool Thermal, BuildingModel Model, OpeningModel? OpeningModel = null) : Command(Issued);
    sealed record DomainCommand(long Issued, DomainBox? Domain, int Seq, bool V1) : Command(Issued);
    sealed record AccurateModeCommand(long Issued) : Command(Issued);

    public LiveSolver(ParticleSystem particles)
    {
        this.particles = particles;
    }

    /// <summary>Wall seconds until the running average covers one flow-through at the current pace; 0 once it
    /// does, NaN before there is a pace to extrapolate from.</summary>
    public double SecondsToConverged()
    {
        double left = AverageTarget - AverageSeconds;
        if (left <= 0) return 0;
        double rate = SimRate;
        return rate > 1e-4 ? left / rate : double.NaN;
    }

    // ---------------------------------------------------------------- UI thread API

    public bool Active => thread is { IsAlive: true };

    /// <summary>Opens a session. <paramref name="thermal"/> is applied here rather than through
    /// <see cref="Reconfigure"/>: RunOptions survives a Stop, so a run started after the toggle was flipped
    /// (in either direction) must not inherit the previous one.</summary>
    public void Start(BuildingModel building, Vector2 windVector, bool thermal = false, DomainBox? box = null,
        bool boxV1 = false, OpeningModel openingModel = OpeningModel.Calibrated)
    {
        Stop();
        ultraFaults = 0; ultraOnlyOwn = false; ultraRunId = Guid.NewGuid().ToString("N");   // a new session is a new Ultra run
        domain = box;
        this.boxV1 = boxV1;
        options = new RunOptions
        {
            Quality = options.Quality,
            Backend = gpuRestarted ? WindFlow.Core.Backend.Auto : options.Backend,   // P17-07: a later run tries the GPU again
            WindProfileExponent = options.WindProfileExponent,
            TargetCells = options.TargetCells,
            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
            Thermal = thermal,
            OpeningModel = openingModel,
            WallModel = WallModelWanted,
            DomainMode = DomainModeWanted,
            HoldIndoorTemperature = HoldIndoorWanted,
        };
        options.Domain = CoarseDomain;
        ThermalActive = thermal;
        model = Snapshot(building);
        // A new session is a new model: nothing held or waiting belongs to it.
        HoldA = HoldB = null;
        Stats = null;
        Trust = RoomLine = "";
        pendingHold = -1;
        HoldWaiting = false;
        HoldError = "";
        HoldVersion++;
        // A new session starts a new Preview-then-Accurate cycle (the solver thread is stopped here).
        Accurate.Edit();
        AccurateStats = null;
        PreviewSettleSeconds = AccuratePassSeconds = float.NaN;
        cycleClock.Restart();
        wind = windVector;
        stop = false;
        cancel = new CancellationTokenSource();
        Error = "";
        GpuNote = "";
        gpuRestarted = false;
        State = Mode.Starting;
        Log.Info($"session start requested: {building.Name} · wind ({windVector.X:0.##}, {windVector.Y:0.##}) = {windVector.Length():0.##} m/s · " +
                 $"thermal {thermal} · {(box is { } b ? $"box {b.Min} -> {b.Max}{(boxV1 ? " (v1)" : "")}" : "whole building")} · " +
                 $"{building.Sources.Count} devices");
        thread = new Thread(Loop) { Name = "WindFlow solver", IsBackground = true };
        thread.Start();
    }

    /// <summary><paramref name="closing"/>: the window is closing, so the wait for the solver thread is bounded (5 s); a thread that has not stopped by then finishes in the background
    /// (a checkpoint write in flight is a temp file the rename never reached, so nothing half-written is left behind).</summary>
    public void Stop(bool closing = false)
    {
        averageEpoch = statsPublication.Advance();
        ageCache = null; ageEpoch = -1;
        stop = true;
        cancel.Cancel();
        // S4-01: never fall through while the old thread is alive. Start() resets the fields that thread still reads and would
        // run a second thread on the same session. The waits inside the thread take this token, so it usually leaves at once;
        // the first run after launch can sit in the kernel JIT or the voxeliser for a few seconds, and the UI waits for it.
        if (closing && thread is { } closingThread && closingThread != Thread.CurrentThread)
        {
            if (!closingThread.Join(5000)) Log.Warn("solver thread still stopping after 5 s; the window closes and it finishes in the background");
        }
        else if (thread is { } old && old != Thread.CurrentThread && !old.Join(8000))
        {
            Log.Warn("solver thread still stopping after 8 s (kernel JIT, voxelising or the GPU); waiting for it rather than starting a second one");
            old.Join();
        }
        thread = null;
        State = Mode.Stopped;
    }

    /// <summary>S4-01 check: solver loops running now, and the most that ever ran at once (must stay 1).</summary>
    internal static int LoopsAlive, LoopsPeak;

    public void SetPaused(bool paused) => commands.Enqueue(new PauseToggle(Stopwatch.GetTimestamp(), paused));

    /// <summary>The caller has already flipped <see cref="Opening.IsOpen"/> and rebuilt the scene; this
    /// re-voxelises and warm-starts the session on the new cells.</summary>
    public void GeometryChanged(BuildingModel current, string why = "geometry")
    {
        Stale = true;
        commands.Enqueue(new GeometryCommand(Stopwatch.GetTimestamp(), why, Snapshot(current)));
    }

    /// <summary>A deep copy the solver side can read while the UI keeps editing the original. Everything
    /// the UI edits (openings, rooms, devices, environment, heat) goes through the model JSON round trip,
    /// the same format a saved model uses; the obstacle meshes are shared, because nothing edits a mesh
    /// after the model is built or imported and they are by far the largest part of it.
    /// Call on the thread that owns <paramref name="source"/> (the UI thread).</summary>
    public static BuildingModel Snapshot(BuildingModel source)
    {
        var shell = new BuildingModel
        {
            Name = source.Name,
            NorthDeg = source.NorthDeg,
            GroundZ = source.GroundZ,
            Storeys = source.Storeys,
            Openings = source.Openings,
            Rooms = source.Rooms,
            Sources = source.Sources,
            Env = source.Env,
            Heat = source.Heat,
            Contaminants = source.Contaminants,
        };
        var copy = BuildingModel.FromJson(shell.ToJson());
        copy.Obstacles = source.Obstacles;
        return copy;
    }

    /// <summary>Snapshot the live state into hold A (slot 0) or B (slot 1) as a completed SimulationResult.
    /// The work happens on the solver thread between two bursts, which is the only thread allowed to call
    /// ToField; that is the brief pause the user sees. Reading it back: HoldA/HoldB plus HoldVersion.</summary>
    public void Hold(int slot) => commands.Enqueue(new HoldCommand(Stopwatch.GetTimestamp(), slot));

    public volatile SimulationResult? HoldA, HoldB;
    public volatile int HoldVersion;
    public volatile string HoldError = "";

    /// <summary>Turns the temperature lattice on or off, and picks up edits to BuildingModel.Env or
    /// Room.Occupants. All three are read when the session opens, so this restarts it on the solver thread
    /// (same command path as a geometry change; the flow starts again from rest).</summary>
    public void Reconfigure(bool thermal, BuildingModel current, OpeningModel? openingModel = null)
    {
        Stale = true;
        commands.Enqueue(new ConfigCommand(Stopwatch.GetTimestamp(), thermal, Snapshot(current), openingModel));
    }

    /// <summary>True once a session is actually stepping the thermal lattice.</summary>
    public volatile bool ThermalActive;

    /// <summary>The simulation box to solve inside, or null for the whole building plus its wind-tunnel
    /// margin. Nested (the default): the whole-building session keeps running and a fine session on the box
    /// is opened (or replaced) beside it, warm-started from the coarse flow. <paramref name="v1"/>: the box is
    /// the whole domain with open faces, which re-voxelises and restarts. The UI debounces the drag first.</summary>
    public void SetDomain(DomainBox? box, bool v1 = false)
    {
        Stale = true;
        int seq = Interlocked.Increment(ref latestDomainSeq);
        commands.Enqueue(new DomainCommand(Stopwatch.GetTimestamp(), box, seq, v1));
    }

    /// <summary>Every SetDomain gets a sequence number; a refusal publishes the number it refused. Several box
    /// changes can be queued, so the UI only puts the gizmo back when the refused one is the newest it asked for.</summary>
    public int LatestDomainSeq => Volatile.Read(ref latestDomainSeq);
    int latestDomainSeq;
    public volatile int RejectedDomainSeq;

    public void SetWind(Vector2 windVector)
    {
        Stale = true;
        commands.Enqueue(new WindChanged(Stopwatch.GetTimestamp(), windVector));
    }

    public void Dispose() => Stop(closing: true);

    // ---------------------------------------------------------------- solver thread

    LbmSolver.LbmSession? session;
    VoxelGrid grid = new();
    float[] ux = [], uy = [], uz = [];
    readonly Half[]?[] texelPool = new Half[]?[3];
    int poolSlot;
    FieldVolume? lastVolume;
    bool geometryDirty;
    double stepsI;              // governor: the integral part of the burst length, in steps (0 = re-estimate)
    long stepsKey;              // work per step when stepsI was sized; a new grid or lattice re-estimates
    double msPerStep;           // last measured
    double dutyDebtMs;          // governor: sleep owed (+) or overslept (-), carried into the next cycle
    DomainBox? domain;
    bool boxV1;                 // the box is the whole domain (v1) instead of a nested fine box
    LbmSolver.NestedSession? nest;
    float[] fux = [], fuy = [], fuz = [];
    NestedComposite? composite; // rebuilt on the next publish after any change to either grid
    long averageStart;          // step index the running average started at
    float averageTarget;        // seconds of flow-through it needs to be trustworthy
    int pendingHold = -1;

    /// <summary>A box nests unless v1 was asked for, or the run is thermal (StartNested is isothermal only).</summary>
    bool NestedMode => domain != null && !boxV1 && !options.Thermal;

    /// <summary>The domain the coarse session is voxelised on: the whole building when the box is nested.</summary>
    DomainBox? CoarseDomain => NestedMode ? null : domain;

    public const string ThermalV1Note = "Thermal runs cannot nest a fine box yet (isothermal only), so the box " +
        "is the whole domain (v1) and its faces are open boundaries.";

    /// <summary>Opens the fine box on the running coarse session (replacing any previous one). A short probe
    /// catches a box Core refuses or that blows up. Returns false, with nothing attached, when it failed.</summary>
    bool AttachNest()
    {
        DetachNest();
        BoxModeNote = domain != null && !boxV1 && options.Thermal ? ThermalV1Note : "";
        if (!NestedMode || session == null || domain is not { } box) return true;
        var clock = Stopwatch.StartNew();
        try
        {
            nest = LbmSolver.StartNested(session, box, options, driveCoarse: true);
            nest.Step(NestProbeSteps, cancel.Token);
        }
        catch (OperationCanceledException) { DetachNest(); throw; }
        catch (Exception e)
        {
            Log.Warn($"nested box {box.Min} -> {box.Max} refused: {e.GetType().Name}: {e.Message}");
            DetachNest();
            nestFailure = e.Message;
            return false;
        }
        var fine = nest.Grid;
        if (fux.Length < fine.Count) { fux = new float[fine.Count]; fuy = new float[fine.Count]; fuz = new float[fine.Count]; }
        // A fresh box needs its own averaging window; the coarse ledger restarts with it so the two match.
        nest.ResetAveraging();
        MarkAverageReset(false);
        PublishNestState();
        geometryDirty = true;
        lastVolume = null;
        Log.Info($"nested box opened in {clock.ElapsedMilliseconds} ms: requested {box.Min} -> {box.Max}, solved " +
                 $"{nest.Box.Min} -> {nest.Box.Max} · {fine.Nx}x{fine.Ny}x{fine.Nz} = {fine.Count / 1000.0:0.#}k cells @ " +
                 $"{fine.Dx * 1000:0} mm · faces {nest.FaceCells} ({nest.InflowCells} inflow) · sponge {nest.SpongeCells} · " +
                 $"notes {fine.Warnings.Count}");
        return true;
    }

    string nestFailure = "";
    const int NestProbeSteps = 100;     // 200 fine steps: past Core's 200-step divergence check

    void DetachNest()
    {
        var previous = nest;
        nest = null;
        composite = null;
        try { previous?.Dispose(); } catch (Exception e) { Log.Warn($"nested box dispose: {e.Message}"); }
        if (previous != null) { geometryDirty = true; lastVolume = null; }
        PublishNestState();
    }

    void PublishNestState()
    {
        Nested = nest != null;
        FineBox = nest?.Box;
        FineCells = nest?.FineCells ?? 0;
        FineDx = nest?.Grid.Dx ?? 0;
        SpongeWidth = nest?.SpongeWidth ?? 0;
        // Core's own rule (LbmSolver.StartNested): bottom driven above the ground, top driven below the domain top.
        NestBottom = nest != null && nest.Box.Min.Z > model.GroundZ + 1e-3f;
        NestTop = nest != null && MathF.Abs(nest.Box.Max.Z - (grid.Origin.Z + grid.Nz * grid.Dx)) > 0.25f * grid.Dx;
        NestNotes = nest != null ? [.. nest.Grid.Warnings] : [];
        if (nest == null) { FineMlups = 0; FineStats = null; }
    }

    /// <summary>Core starts averaging at WarmupSteps and resets it on SetWind/UpdateCells (and whenever we
    /// call ResetAveraging), so the app tracks where the current window began rather than guessing.</summary>
    void MarkAverageReset(bool fromWarmup)
    {
        averageEpoch = statsPublication.Advance();
        ageCache = null; ageEpoch = -1; // old fields cannot publish into this window
        if (session is not { } s) return;
        averageStart = fromWarmup ? s.WarmupSteps : Math.Max(s.Steps, s.WarmupSteps);
        averageTarget = FlowThroughSeconds();
        UpdateAverageWindow();
    }

    /// <summary>One flow-through: the building (or the box, whichever is smaller) divided by the wind speed.
    /// Clamped so a still day does not ask for an hour and a gale does not trust 0.2 s of samples.</summary>
    float FlowThroughSeconds()
    {
        float length = 8f;
        try
        {
            var (pmin, pmax) = Voxelizer.PlanBounds(model);
            length = MathF.Max(pmax.X - pmin.X, pmax.Y - pmin.Y);
        }
        catch (ArgumentException) { /* nothing to simulate: the default is fine */ }
        // Nested, the ledger is the whole building, so the window is the building flow-through.
        if (CoarseDomain is { } b) length = MathF.Min(length, MathF.Max(b.Size.X, b.Size.Y));
        return Math.Clamp(length / MathF.Max(wind.Length(), 0.25f), 2f, 60f);
    }

    void UpdateAverageWindow()
    {
        if (session is not { } s) { AverageSeconds = 0; return; }
        double dtStep = s.Steps > 0 ? s.SimSeconds / s.Steps : 0;
        AverageSeconds = (float)(Math.Max(0, s.Steps - averageStart) * dtStep);
        AverageTarget = averageTarget;
    }

    void Loop()
    {
        int alive = Interlocked.Increment(ref LoopsAlive);
        int peak;
        while (alive > (peak = LoopsPeak) && Interlocked.CompareExchange(ref LoopsPeak, alive, peak) != peak) { }
        try
        {
            try { RunSession(); }
            catch (Exception e) when (e is not OperationCanceledException && !stop && AccurateModeWanted == AccurateMode.Ultra && ultraRun != null && LbmSolver.IsGpuFault(e))
            {
                // Ultra never falls back to the CPU (a run this fine would not finish there): one retry on the GPU, from the last checkpoint; the second fault stops it.
                ultraNoSave = true;
                string dir = ultraRun.Dir;
                bool kept = ultraRun.HasSavedFiles;
                DisposeAccurate();
                try { nest?.Dispose(); } catch { /* already gone */ }
                nest = null;
                try { session?.Dispose(); } catch { /* already gone */ }
                session = null;
                string reason = LbmSolver.GpuFaultReason(e);
                if (++ultraFaults >= 2)
                {
                    string msg = UltraDriver.UltraFaultMessage(reason, kept, dir);
                    Log.Error("ultra: " + msg, e);
                    Error = msg;
                    Accurate.Off(msg);
                    throw;
                }
                Log.Warn($"ultra: the GPU stopped responding ({reason}); retrying once on the GPU" + (kept ? " from the last checkpoint" : " from the start (no checkpoint yet)"));
                LbmSolver.ResetGpu();
                ultraOnlyOwn = true;   // the retry resumes only a checkpoint THIS run wrote
                Accurate.Edit();
                AccurateStats = null;
                PreviewSettleSeconds = AccuratePassSeconds = float.NaN;
                cycleClock.Restart();
                Nested = false;
                FineBox = null;
                State = Mode.Starting;
                try { RunSession(); }
                catch (Exception again) when (again is not OperationCanceledException && !stop && LbmSolver.IsGpuFault(again))
                {
                    string msg2 = UltraDriver.UltraFaultMessage(LbmSolver.GpuFaultReason(again), Directory.Exists(dir), dir);   // a second fault in the same run: the folder and how to resume
                    Log.Error("ultra: " + msg2, again);
                    Error = msg2;
                    Accurate.Off(msg2);
                    throw;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException && !stop && !gpuRestarted && options.Backend != WindFlow.Core.Backend.Cpu)
            {
                // P17-07: a GPU failure (init, a device loss, any accelerator exception while stepping or reading back) restarts this run ONCE on the CPU, through
                // AveragedPass's policy (clears the cached device, switches the options, words the message). The Preview and its Accurate pass both start over there;
                // a failure on the CPU, an out-of-memory, a divergence or a cancel is not this and ends the run as before.
                ultraNoSave = true;   // a faulted device's state is never saved
                DisposeAccurate();
                try { nest?.Dispose(); } catch { /* already gone */ }
                nest = null;
                bool wasGpu = session?.OnGpu ?? true;
                try { session?.Dispose(); } catch { /* already gone */ }
                session = null;
                if (!AveragedPass.TryGpuRestart(options, wasGpu, e, out string note)) throw;
                gpuRestarted = true;
                if (Error.StartsWith("command failed", StringComparison.Ordinal)) Error = "";   // RW-10a: the edit's Restart fell back to the CPU and runs; the failure text is stale
                GpuNote = note;
                Log.Warn(note);
                Accurate.Edit();
                AccurateStats = null;
                PreviewSettleSeconds = AccuratePassSeconds = float.NaN;
                cycleClock.Restart();
                Nested = false;
                FineBox = null;
                State = Mode.Starting;
                RunSession();
            }
        }
        catch (OperationCanceledException) { /* Stop() */ }
        catch (Exception e)
        {
            // Diverged, out of memory, GPU gone: say so and leave the app usable.
            Log.Error($"solver thread stopped at sim t {SimSeconds:0.0} s", e);
            Error =e.Message.Length > 160 ? e.Message[..160] : e.Message;
            State = Mode.Failed;
            Accurate.Fail();
        }
        finally
        {
            DisposeAccurate();
            try { nest?.Dispose(); } catch { /* already gone */ }
            nest = null;
            try { session?.Dispose(); } catch { /* already gone */ }
            session = null;
            Nested = false;
            FineBox = null;
            if (State != Mode.Failed) State = Mode.Stopped;
            Duty = StepsPerSecond = SimRate = 0;
            PreciseSleep.Release();
            Interlocked.Decrement(ref LoopsAlive);
        }
    }

    /// <summary>P17-07: the GPU note of the running session: the restart message after a loss, else the GPU fallback note (no GPU found, or CUDA unavailable and OpenCL used) when the run asked for a GPU.</summary>
    void UpdateGpuNote() =>
        GpuNote = options.GpuLossNote ?? (options.Backend != WindFlow.Core.Backend.Cpu ? LbmSolver.GpuFallbackNote ?? "" : "");

    /// <summary>One run of the solver thread: open the session and step it until stopped. A GPU failure leaves it by exception (see <see cref="Loop"/>).</summary>
    void RunSession()
    {
        {
            OpenSession();
            State = Mode.Running;
            var publishClock = Stopwatch.StartNew();
            var analysisClock = Stopwatch.StartNew();
            double lastPublish = 0;

            while (!stop)
            {
                if (previewReleased && accSession == null) ReopenPreview();   // an Ultra pass ended or failed: the Preview comes back before anything reads it
                DrainCommands();
                if (stop) break;
                if (session == null)
                {
                    // A restart threw (out of memory when the thermal lattice was added, a lost device):
                    // say so instead of dereferencing a session that no longer exists.
                    if (Error.Length == 0) Error = "the solver session could not be restarted";
                    State = Mode.Failed;
                    return;
                }

                if (State == Mode.Paused)
                {
                    Duty = StepsPerSecond = SimRate = 0;
                    winBusy = winWall = 0;
                    winSteps = 0;
                    winSim = -1;
                    if (!SuspendAnalysis && analysisClock.Elapsed.TotalSeconds > 0.5)
                    {
                        Analyse();
                        analysisClock.Restart();
                    }
                    if (Accurate.Phase == AccuratePhase.Finishing) AccurateTick();
                    if (accPass != null && accPausedSince == 0) accPausedSince = Stopwatch.GetTimestamp();   // RU-02: a pause is the only time the pass's cap does not count
                    Thread.Sleep(25);
                    continue;
                }
                if (accPausedSince != 0) { accPausedSeconds += (Stopwatch.GetTimestamp() - accPausedSince) / (double)Stopwatch.Frequency; accPausedSince = 0; }

                if (Accurate.Phase is AccuratePhase.Finishing or AccuratePhase.Accurate)
                {
                    // Accurate pass done (or its numbers being worked out): nothing steps, the GPU only draws. The
                    // particles fly through the averaged fine field; the next edit wakes the Preview session.
                    AccurateTick();
                    if (Accurate.Phase == AccuratePhase.Accurate && ScalarWanted != accScalarShown && AccurateStats is { } shown)
                        RepublishAccurateScalar(shown);             // the slice switched on (or over) after the pass landed
                    Duty = StepsPerSecond = SimRate = 0;
                    winBusy = winWall = 0;
                    winSteps = 0;
                    winSim = -1;
                    Thread.Sleep(25);
                    continue;
                }

                Bursts++;
                var sw = Stopwatch.StartNew();      // the governor cycle: burst, readback, publish, sleep
                int steps = BurstSteps();
                if (accSession != null)
                {
                    if (!StepAccurate(steps)) continue;     // it diverged: dropped, Preview carries on
                    Mlups = (float)accSession.Mlups;
                }
                else if (nest != null)
                {
                    // One coarse step drives two fine ones (driveCoarse): the total is the lattice work per burst.
                    nest.Step(steps, cancel.Token);
                    double seconds = Math.Max(sw.Elapsed.TotalSeconds, 1e-6);
                    Mlups = (float)((double)(Cells + 2L * nest.FineCells) * steps / seconds / 1e6);
                    FineMlups = (float)nest.Mlups;
                }
                else
                {
                    session!.Step(steps, cancel.Token);
                    Mlups = (float)session.Mlups;
                }
                double stepMs = sw.Elapsed.TotalMilliseconds;
                CoarseMlups = (float)(session?.Mlups ?? accSession!.Mlups);   // Ultra: the Preview session is released
                SimSeconds = (float)(accSession?.SimSeconds ?? session.SimSeconds);

                // Publishing is capped independently of the burst length: when the renderer is slow the bursts
                // get short, and without this the solver would upload a new field every 13 ms and make the
                // frame time worse still.
                double now = publishClock.Elapsed.TotalSeconds;
                double readMs = 0;
                // A cold-started Accurate session is still air for its first flow-through: the particles stay on the
                // settled Preview field until it is averaging, then follow the fine field.
                // The Accurate pass publishes at 4 Hz (a publish there costs ~30 % of wall time at 12 Hz).
                bool publishDue = now - lastPublish >= (accSession != null ? AccuratePublishInterval : MinPublishInterval) || lastPublish <= 0;
                if (accSession != null && (Accurate.Phase != AccuratePhase.Averaging)) publishDue = false;
                if (publishDue)
                {
                    double readStart = sw.Elapsed.TotalMilliseconds;
                    if (accSession != null) accSession.ReadVelocity(aux, auy, auz);
                    else
                    {
                        session.ReadVelocity(ux, uy, uz);
                        nest?.ReadVelocity(fux, fuy, fuz);
                    }
                    readMs = sw.Elapsed.TotalMilliseconds - readStart;   // a synchronising copy: GPU-busy time
                    Publish();
                    if (lastPublish > 0) FieldHz = (float)(0.7 * FieldHz + 0.3 / Math.Max(now - lastPublish, 1e-4));
                    lastPublish = now;
                }
                BurstMs = (float)stepMs;
                AdaptBurst(stepMs, steps);
                Govern(sw, stepMs + readMs, steps);
                UpdateAverageWindow();
                if (pendingHold >= 0 && AverageSeconds >= AverageTarget)
                {
                    int slot = pendingHold;
                    pendingHold = -1;
                    HoldWaiting = false;
                    ApplyHold(slot);
                }
                AccurateTick();

                if (!AnalysisOnPauseOnly && !SuspendAnalysis && analysisClock.Elapsed.TotalSeconds > AnalysisPeriod)
                {
                    Analyse();
                    analysisClock.Restart();
                }
            }
        }
    }

    // ---------------------------------------------------------------- Preview, then Accurate

    LbmSolver.LbmSession? accSession;
    LbmSolver.NestedSession? accNest;        // High with a focus room: the 2:1 fine box driven by accSession
    RunOptions? accOptions;                  // the options of the running pass: the recipe's options (Standard: the live ones) copied, with OnlineAge on (6k)
    AveragedPass? accPass;                   // 6f: the pass itself (Core): settle, batches, cap, stop rule, landing, the age; the app only steps it and shows it
    VoxelGrid? accGrid;
    float[] aux = [], auy = [], auz = [];
    double accFinishEstimate = double.NaN;
    double accAnalysisMsPerCell;             // measured by the last finished pass (CPU contention and all)
    bool accWarm;
    readonly Stopwatch cycleClock = new(), accClock = new();
    long accPausedSince;                     // RU-02: Stopwatch timestamp the current pause began (0 = not paused during a pass)
    double accPausedSeconds;                 // RU-02: the pauses of this pass so far, seconds
    volatile int settledEpoch = -1;
    volatile AccurateFinished? accFinished;

    // ---- Ultra (long run with checkpoints; see Core/Ultra.cs) ----
    /// <summary>Set by the UI: the Ultra time limit in hours (6 / 12 / 20 / 24).</summary>
    public volatile int UltraCapHours = Ultra.DefaultCapHours;
    /// <summary>Set by the UI before the pass starts (the solver thread never waits on the UI): what to do with a saved run that matches. Valid only for the hash it names.</summary>
    public volatile UltraChoice? UltraChoiceWanted;
    string ultraRunId = Guid.NewGuid().ToString("N");   // this run's checkpoints carry it; a GPU-fault retry resumes only these
    bool ultraOnlyOwn;                                  // set by the retry
    UltraChoice? ultraChoiceNow;                        // the choice that applies to the pass being opened
    string ultraHashNow = "";
    /// <summary>The plan of the Ultra pass that last opened (or would open): the UI's dialog and the labels read it.</summary>
    public volatile UltraPlan? UltraPlanShown;
    UltraRun? ultraRun;
    UltraKeepAwake? ultraAwake;
    bool previewReleased;                    // the Preview session's GPU buffers are freed while an Ultra pass runs; ReopenPreview brings it back

    /// <summary>Ultra only: frees the Preview session (and its fine box) so the finest grid can use its memory. Solver thread.</summary>
    void ReleasePreview()
    {
        if (previewReleased || session == null) return;
        DetachNest();
        var previous = session;
        session = null;
        try { previous.Dispose(); } catch (Exception e) { Log.Warn($"ultra: Preview release: {e.Message}"); }
        previewReleased = true;
        Log.Info("ultra: the Preview session's GPU memory is released while Ultra runs; the Preview restarts afterwards");
    }

    /// <summary>Brings the Preview back after an Ultra pass ended, failed or was cancelled by an edit (Restart opens the session and the fine box again). Solver thread.</summary>
    void ReopenPreview()
    {
        if (!previewReleased) return;
        previewReleased = false;
        Restart();
        Log.Info("ultra: the Preview restarted");
    }
    double ultraCarried;                     // wall seconds an Ultra pass resumed with (counted against its cap)
    bool ultraNoSave;                        // a GPU fault: never write the state of a faulted device
    int ultraFaults;                         // GPU faults of this Ultra run (the second one stops it)

    /// <summary>The Ultra plan for the current model, wind and GPU, for the confirm dialog (any thread; nothing is started).</summary>
    public UltraPlan PlanUltra(BuildingModel forModel, Vector2 forWind, double capHours, bool allowMeasuring = true)
    {
        bool gpu = LbmSolver.GpuAvailable;
        double mlups = Mlups > 0 ? Mlups : StartupTier.Chosen?.ProbeMlups ?? 0;
        int previewCells = previewReleased ? 0 : Cells > 0 ? Cells : StartupTier.Chosen?.TargetCells ?? 0;
        long free = 0;
        if (gpu && HeadlessSolve.TryFreeVramBytes(out long fv)) free = fv + (session != null ? (long)Cells * LbmSolver.GpuBytesPerCell : 0);   // measured now; the resident Preview session is released when Ultra starts, so its share counts as free
        return Ultra.Plan(forModel, forWind, StartupTier.AccurateCells(gpu), gpu ? LbmSolver.GpuMemoryBytes : 0, previewCells, mlups, mlups > 0, gpu ? LbmSolver.GpuName : "no GPU", capHours,
            measuringSpeed: allowMeasuring && mlups <= 0 && SpeedProbePending, freeVramBytes: free);
    }

    /// <summary>The plan for resuming a saved run (its own grid); see <see cref="PlanUltra"/>.</summary>
    public UltraPlan PlanUltraResume(BuildingModel forModel, Vector2 forWind, UltraManifest saved, double capHours, bool allowMeasuring = true)
    {
        bool gpu = LbmSolver.GpuAvailable;
        double mlups = Mlups > 0 ? Mlups : StartupTier.Chosen?.ProbeMlups ?? 0;
        int previewCells = previewReleased ? 0 : Cells > 0 ? Cells : StartupTier.Chosen?.TargetCells ?? 0;
        long free = 0;
        if (gpu && HeadlessSolve.TryFreeVramBytes(out long fv)) free = fv + (session != null ? (long)Cells * LbmSolver.GpuBytesPerCell : 0);
        return Ultra.PlanResume(forModel, forWind, saved, gpu ? LbmSolver.GpuMemoryBytes : 0, previewCells, mlups, mlups > 0, gpu ? LbmSolver.GpuName : "no GPU", capHours,
            measuringSpeed: allowMeasuring && mlups <= 0 && SpeedProbePending, freeVramBytes: free);
    }

    /// <summary>The startup speed probe has not delivered yet (and has not failed).</summary>
    public static bool SpeedProbePending => StartupTier.Chosen == null && !StartupTier.Begin().IsCompleted;

    sealed record AccurateFinished(int Cycle, SimulationResult Result, FieldVolume Volume, double AnalysisMs, UltraRun? Run = null);

    /// <summary>Wind, opening, device, environment, box: anything that changes the flow. Pause and hold do not.</summary>
    static bool EditCancelsAccurate(Command c) => c is GeometryCommand or WindChanged or ConfigCommand or DomainCommand or AccurateModeCommand;

    /// <summary>Whole-building runs only: with a simulation box on, the box is already the finer grid.</summary>
    const string AccurateBoxNote = "The averaged pass runs on the whole building; the simulation box is the finer grid here";

    /// <summary>Hard cap on a Standard Accurate pass's stepping wall time (paused time excluded), seconds: 10 minutes. A pass
    /// that reaches it lands what it has (the mean of the batches kept so far) and the log and the room table say the
    /// aim was not met. Converged (every room's ACH and online air age settled), Diverged, this cap or 3 x the cap on the raw clock end the pass
    /// (RU-02: the cap is real elapsed time minus pauses). A High pass uses <see cref="HighAccuracy.SnapCapMinutes"/> of
    /// <see cref="Settings.HighAccuracyCapMinutes"/> instead (<see cref="AccurateCapThisPass"/>); the env override wins for both.</summary>
    public static readonly double AccurateCapSeconds = AveragedPass.CapSecondsFor(false, null);   // 6f: the rule (and the WINDFLOW_ACCURATE_CAP_S override) is AveragedPass's

    /// <summary>The stepping-time cap of the running (or last) Accurate pass, seconds; set when the pass opens.</summary>
    public volatile float AccurateCapThisPass = (float)AccurateCapSeconds;

    // The convergence-driven average is Core's AveragedPass (accPass). The solver thread steps the session in governor bursts and calls Tick after each.
    /// <summary>The finished pass's convergence in one line per room (measurement and the log).</summary>
    public volatile string AccurateConvergenceText = "";
    public volatile int AccurateStepsTaken;

    /// <summary>P17-06: why a dropped averaged pass says it was dropped. Only the solver's own divergence exception says "diverged" (out of memory, a lost
    /// device or a refused grid do not).</summary>
    public static string AccurateFailureText(Exception e) =>
        e is InvalidOperationException && e.Message.Contains("diverged", StringComparison.OrdinalIgnoreCase)
            ? "the averaged pass diverged, so the Preview numbers stay"
            : $"the averaged pass failed ({(e.Message.Length > 80 ? e.Message[..80] : e.Message)}), so the Preview numbers stay";

    /// <summary>One burst of the Accurate session. A pass that blows up is dropped with a note and the Preview
    /// session, which kept its state, carries on; it must never take the whole solver down.</summary>
    bool StepAccurate(int steps)
    {
        try
        {
            if (accNest != null) accNest.Step(steps, cancel.Token);     // one coarse step drives two fine ones
            else accSession!.Step(steps, cancel.Token);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            if ((accOptions?.Ultra != null || !gpuRestarted) && options.Backend != WindFlow.Core.Backend.Cpu && accSession?.OnGpu == true && LbmSolver.IsGpuFault(e)) { ultraNoSave = true; throw; }   // P17-07: the Loop restarts the run on the CPU; Ultra: on the GPU, once, from its checkpoint
            Log.Warn($"accurate: the {AccurateCells / 1000}k-cell pass failed at step {accSession?.Steps} ({e.GetType().Name}: {e.Message}); Preview stays");
            ultraNoSave = true;
            DisposeAccurate();
            // P17-06: name the real cause; only the solver's own divergence exception says "diverged" (out of memory, a lost device, a refused grid do not).
            Accurate.Off(AccurateFailureText(e));
            geometryDirty = true;
            lastVolume = null;
            stepsKey = 0;
            ResetGovernorWindow();
            return false;
        }
    }

    void DisposeAccurate()
    {
        if (ultraRun != null)
        {
            try
            {
                // a stop: only if the pass is exactly at a batch boundary right now; never waits for a batch, never writes a failed or half-restored session (UltraRun.Trusted)
                if (!ultraNoSave && accPass is { Done: false } p && accSession != null) ultraRun.TryWrite(accSession, p, accClock.Elapsed.TotalSeconds + ultraCarried, "stopped", CancellationToken.None, wait: false);
            }
            catch (Exception e) { Log.Warn($"ultra: no checkpoint on stop ({e.Message})"); }
            ultraRun = null;
        }
        ultraAwake?.Dispose(); ultraAwake = null; ultraCarried = 0; ultraNoSave = false;
        var s = accSession;
        var n = accNest;
        accSession = null;
        accNest = null;
        accPass = null;
        accGrid = null;
        try { n?.Dispose(); } catch (Exception e) { Log.Warn($"accurate nested box dispose: {e.Message}"); }
        try { s?.Dispose(); } catch (Exception e) { Log.Warn($"accurate session dispose: {e.Message}"); }
    }

    /// <summary>Solver thread: an edit arrived. Whatever the pass was doing is dropped and the Preview session,
    /// which kept its state, carries on with the edit applied.</summary>
    void CancelAccurate(string why)
    {
        var was = Accurate.Phase;
        bool hadSession = accSession != null;
        DisposeAccurate();
        Accurate.Edit();
        accFinished = null;
        AccurateStats = null;
        accVolume = null;
        accScalarShown = -1;
        PreviewSettleSeconds = AccuratePassSeconds = float.NaN;
        cycleClock.Restart();
        if (hadSession || was is AccuratePhase.Finishing or AccuratePhase.Accurate)
        {
            Log.Info($"accurate: {was} cancelled by {why} after {accClock.Elapsed.TotalSeconds:0.0} s; back to Preview");
            // The particles were on the fine grid: the next publish rebuilds the volume from the Preview grid.
            geometryDirty = true;
            lastVolume = null;
            stepsKey = 0;
            ResetGovernorWindow();
        }
    }

    void ResetGovernorWindow()
    {
        winBusy = winWall = 0;
        winSteps = 0;
        winSim = -1;
        StepsPerSecond = SimRate = 0;
    }

    /// <summary>Solver thread, once per cycle of the loop: starts the pass once Preview has settled, moves the bar
    /// while it steps, hands the finished analysis to the UI.</summary>
    void AccurateTick()
    {
        switch (Accurate.Phase)
        {
            case AccuratePhase.Preview:
            {
                if (State != Mode.Running || session == null) return;
                if (domain != null || nest != null) { Accurate.Off(AccurateBoxNote); return; }
                if (AverageSeconds < AverageTarget || Stale || !commands.IsEmpty || pendingHold >= 0) return;
                if (float.IsNaN(PreviewSettleSeconds))
                {
                    PreviewSettleSeconds = (float)cycleClock.Elapsed.TotalSeconds;
                    Log.Info($"preview settled {PreviewSettleSeconds:0.0} s after the last change · {Cells / 1000.0:0.#}k cells @ " +
                             $"{Dx * 1000:0} mm · averaged {AverageSeconds:0.0}/{AverageTarget:0.0} s");
                }
                // Wait for the settled Preview numbers (the provisional ones stay on screen during the pass). The pull
                // is forced even where analysis is "on pause only" (a slow ToField under GPU contention): the Preview
                // session is about to go idle, so one slow pull costs nothing and the pass never runs number-less.
                if (settledEpoch != averageEpoch)
                {
                    if (!statsBusy && !SuspendAnalysis) Analyse(force: true);
                    return;
                }
                StartAccurate();
                return;
            }
            case AccuratePhase.Settling or AccuratePhase.Averaging:
            {
                if (accSession is not { } s || accPass is not { } pass) return;
                pass.Tick(accClock.Elapsed.TotalSeconds + ultraCarried, accPausedSeconds, cancel.Token);   // RU-01: the token reaches the steady age solve; RU-02: real time minus pauses (Ultra: plus what a resumed run had used)
                if (ultraRun is { Due: true } && !pass.Done) ultraRun.TryWrite(s, pass, accClock.Elapsed.TotalSeconds + ultraCarried, "timer", cancel.Token);   // IO errors are handled inside (backs off, then stops checkpointing)
                if (pass.Done)
                {
                    if (pass.TimeLimitBeforeAveraging)   // RU-05: the cap ran out inside the settle: nothing to land
                    {
                        string noAvg = AccurateModeWanted == AccurateMode.High ? AveragedPass.NoAveragingNote : AveragedPass.NoAveragingNoteStandard;   // RW-10b
                        Log.Warn("accurate: " + noAvg + "; Preview stays");
                        DisposeAccurate();
                        Accurate.Off(noAvg);
                        geometryDirty = true; lastVolume = null; stepsKey = 0; ResetGovernorWindow();
                        return;
                    }
                    if (pass.Reason == ConvergenceStopReason.Diverged)
                    {
                        Log.Warn("accurate: a batch's ACH was not finite; Preview stays");
                        DisposeAccurate();
                        Accurate.Off("the averaged pass diverged, so the Preview numbers stay");
                        geometryDirty = true; lastVolume = null; stepsKey = 0; ResetGovernorWindow();
                        return;
                    }
                    FinishAccurate();
                    return;
                }
                double batchSec = StepsPerSecond > 1e-3 && pass.BatchSteps > 0 ? pass.BatchSteps / StepsPerSecond : double.NaN;
                double tol = pass.RelativeTolerance;
                string detail = pass.BatchCount > 0 && double.IsFinite(pass.WorstRatio)
                    ? $"batch {pass.BatchCount} · worst room ±{pass.WorstRatio * tol * 100:0} % (aim {tol * 100:0} %)" : "";
                Accurate.UpdateConverging(s.Steps, pass.SettleSteps, pass.BatchCount, pass.TransientBatches, pass.MinBatches, pass.WorstRatio, batchSec,
                    AccurateCapThisPass - pass.SteppingSeconds, accFinishEstimate, detail);
                return;
            }
            case AccuratePhase.Finishing:
            {
                if (accFinished is not { } done) return;
                accFinished = null;
                if (done.Cycle != Accurate.Cycle) return;           // an edit got there first
                particles.SetField(done.Volume);
                lastVolume = null;                                   // the pool's texels are not the ones on screen
                accVolume = done.Volume;
                RepublishAccurateScalar(done.Result);
                geometryDirty = true;
                var ledger = done.Result.Ledger;
                Trust = ledger.In <= 0 && ledger.Out <= 0 ? "Averaged result"
                    : ledger.IsPhysical ? $"Averaged result · ledger ok {ledger.Residual * 100:0.#}%"
                    : $"Averaged result · ledger {ledger.Residual * 100:0.#}% ⚠";
                RoomResult? biggest = null;
                foreach (var room in done.Result.Rooms)
                    if (biggest == null || room.Volume > biggest.Volume) biggest = room;
                RoomLine = biggest == null ? "" : $"{NameOf(biggest.Room)} {Standards.AchText(biggest)}";
                AccurateStats = done.Result;
                Stats = done.Result;
                FineStats = null;
                StatsVersion++;
                AccuratePassSeconds = (float)accClock.Elapsed.TotalSeconds;
                accAnalysisMsPerCell = done.AnalysisMs / Math.Max(1, done.Result.Field.Grid.Count);
                Accurate.Done();
                done.Run?.Delete();   // the result is on screen: the saved run has done its job (a failed analysis or a stale epoch never gets here, so the checkpoints stay)
                Log.Info($"accurate: done in {AccuratePassSeconds:0.0} s (analysis {done.AnalysisMs:0} ms) · {AccurateCells / 1000.0:0.#}k cells @ " +
                         $"{AccurateDx * 1000:0} mm · {Trust} · {RoomLine} · landed " +
                         string.Join(" · ", done.Result.Rooms.Select(r => $"{NameOf(r.Room)} fresh {Standards.FreshText(r)} total {r.Ach:0.00}")));
                return;
            }
        }
    }

    FieldVolume? accVolume;                  // the finished pass's particle volume, the slice's target shape
    int accScalarShown = -1;                 // ScalarWanted the slice last showed from the Accurate result

    /// <summary>The pressure / temperature slice from the finished Accurate field, on the fine volume the particles
    /// now fly through: otherwise it keeps the last Preview scalar on a grid that is no longer on screen.</summary>
    void RepublishAccurateScalar(SimulationResult result)
    {
        accScalarShown = ScalarWanted;
        if (accVolume == null) return;
        try { PublishScalar(result, null, null, accVolume); }
        catch (Exception e) { Log.Warn($"accurate: slice republish failed ({e.GetType().Name}: {e.Message})"); }
    }

    /// <summary>Voxelise the whole building at the Accurate budget and open a session on it, warm-started from the
    /// Preview average (Core: LbmSolver.Start warmStart, isothermal only; a thermal run starts from rest). The Preview
    /// session stays resident and idle. Anything that fails leaves Preview running and says why.</summary>
    string highNote = "";
    string ultraOomNote = "";
    void StartAccurate()
    {
        var clock = Stopwatch.StartNew();
        bool gpu = !session!.Backend.Contains("CPU", StringComparison.OrdinalIgnoreCase) && LbmSolver.GpuAvailable;
        double vram = gpu ? StartupTier.Chosen?.VramGb ?? 0 : 0;
        var (cells, clamped) = AccurateBudget.Resolve(AccurateCellsTyped, StartupTier.AccurateCells(gpu), vram, grid.Count, options.Thermal);
        // RZ-02: like with like. Both budgets set Dx on the Compact domain (a Full grid holds ~10x more cells at the same Dx), so the
        // Accurate budget is compared with the Preview's cell target, not with the Preview grid; grid.Count stays only in the VRAM cap.
        var mode = AccurateModeWanted;      // one read: the recipe, the plan and the cap below must agree if the mode flips mid-startup
        UltraPlan? uplan = null;
        if (mode == AccurateMode.Ultra)
        {
            if (options.Thermal) { Accurate.Off(Ultra.ToggleTooltipThermal); return; }
            if (!gpu) { Accurate.Off("Ultra needs a GPU"); return; }
            ReleasePreview();   // the loop reopens it if Ultra does not start, and after the pass
            try
            {
                var (hash0, _) = UltraCheckpoint.HashOf(model, wind, AccurateRecipe.Options(mode, options, model, null));
                var choice = UltraChoiceWanted is { } c0 && c0.Hash == hash0 ? c0 : null;   // a choice made for another model or settings does not apply
                ultraChoiceNow = choice; ultraHashNow = hash0;
                uplan = choice is { Decision: UltraDecision.Resume, Found: { } saved } ? PlanUltraResume(model, wind, saved, UltraCapHours, allowMeasuring: false) : PlanUltra(model, wind, UltraCapHours, allowMeasuring: false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.Warn($"ultra: could not prepare ({e.GetType().Name}: {e.Message}); Preview stays");
                Accurate.Off("Ultra could not be prepared: " + e.Message);
                return;
            }
            UltraPlanShown = uplan;
            if (!uplan.Feasible) { Accurate.Off(uplan.Refusal); Log.Warn("ultra: " + uplan.Refusal); return; }
            if (uplan.DiskShort) { Accurate.Off(uplan.DiskWarning); Log.Warn("ultra: " + uplan.DiskWarning); return; }
            foreach (var line in uplan.Lines()) Log.Info("ultra: " + line);
            cells = (int)Math.Min(int.MaxValue, uplan.Cells);
        }
        if (uplan == null && cells < cellTarget * AccurateBudget.MinOverPreview)
        {
            Accurate.Off("not enough GPU memory for a finer grid");
            Log.Warn($"accurate: skipped, budget {cells / 1000}k cells is under {AccurateBudget.MinOverPreview}x the Preview budget ({cellTarget / 1000}k)");
            return;
        }
        FlowField? seed = null;
        try
        {
            // Warm start (Core, PLAN_V2 2.2): the fine grid starts from the Preview average instead of still air. The
            // GPU seed is SoA since Core's WriteSoAHost fix (Sandbox 56a/56b); isothermal only, Core ignores it thermal.
            // ponytail: off by measurement (2026-09-28): the seed needs ~4x settle to wash out, so warm 4x took longer
            // than cold 2x and scattered more (cold 2x within 5-9 % of a 4x/4x reference). Revisit if Core's seed washes
            // out faster.
            bool warmStartWorks = false;
            if (Options.AccurateStart == "warm") warmStartWorks = true;          // measurement hook only
            if (Options.AccurateStart == "cold") warmStartWorks = false;         // measurement hook only
            var warm = options.Thermal || !warmStartWorks || session == null ? null : session.ToField();
            seed = warm;
            var g = uplan != null
                ? Voxelizer.Build(model, uplan.BuildCells, AnalysisWorkers, null, minDx: Ultra.MinDx, mode: uplan.Domain, wind: wind, maxCells: uplan.CellCap, fixedAutoDx: uplan.Resuming ? uplan.Dx : 0f)   // Ultra: its own Dx and domain (Ultra.Plan); a resumed run rebuilds the saved grid exactly
                : BuildGrid(cells, null, AccurateBudget.VramCap(vram, grid.Count, options.Thermal));
            // An edit that arrived during the voxelisation wins: drop this grid, DrainCommands restarts the cycle.
            if (!commands.IsEmpty || stop) return;
            HighAccuracyPlan? plan = null;
            if (mode == AccurateMode.High)
                plan = HighAccuracy.Plan(model, gpu ? HeadlessSolve.FreeVramBytes() : 0, cells, accurateFocus, options.Thermal);
            var accO = AccurateRecipe.Options(mode, options, model, plan?.FineBox).Clone();   // a copy: the live (Preview) options keep OnlineAge off
            if (uplan != null)
            {
                accO.DomainMode = uplan.Domain;
                accO.Ultra = new UltraSettings
                {
                    Log = s => Log.Info("ultra: " + s),
                    Decision = ultraOnlyOwn ? UltraDecision.Auto : ultraChoiceNow?.Decision ?? UltraDecision.Skip,   // no choice for this run: new run, nothing deleted
                    DecisionHash = ultraHashNow, Found = ultraChoiceNow?.Found, OnlyOwnRun = ultraOnlyOwn, RunId = ultraRunId, FixedDx = uplan.Resuming ? uplan.Dx : 0f,
                };
            }
            // Online age: High lands it everywhere (steady age rides along), Standard per room where it settled (AccurateRecipe.Options, AveragedPass.PerRoom).
            try { accSession = LbmSolver.Start(g, model, wind, accO, warm); }
            catch (Exception oom) when (uplan != null && !uplan.Resuming && UltraDriver.IsOutOfMemory(oom))
            {
                // Out of memory at Start: once, on a grid with 85 % of the cells; the plan text says so. A saved run's grid cannot change, so that is not retried.
                float dx2 = Ultra.OomRetryDx(g.Dx);
                string note = $"the GPU ran out of memory at Start: retrying with 85 % of the cells (cell size {g.Dx * 1000:0.#} -> {dx2 * 1000:0.#} mm)";
                Log.Warn("ultra: " + note);
                LbmSolver.ResetGpu();
                g = Voxelizer.Build(model, int.MaxValue, AnalysisWorkers, null, minDx: Ultra.MinDx, mode: uplan.Domain, wind: wind, maxCells: 0, fixedAutoDx: dx2);
                g.Warnings.Add(note);
                ultraOomNote = " · " + note;
                accSession = LbmSolver.Start(g, model, wind, accO, warm);
            }
            accOptions = accO;
            accGrid = g;
            accWarm = warm != null;
            highNote = ultraOomNote;
            ultraOomNote = "";
            if (plan != null)
            {
                highNote = " · HIGH: Tu per supply + EN 1991-1-4 approach turbulence" + (options.DomainMode == DomainMode.Full ? " (the fine-box and memory plan assume the Compact domain)" : "") + ", " +
                    (plan.FineBox is { } fb ? $"fine box {plan.FineDx * 1000:0.#} mm around {plan.FocusRoomName} ({plan.FineCells / 1e6:0.###}M cells)" : "no fine box") +
                    (plan.Warnings.Count > 0 ? " (" + string.Join("; ", plan.Warnings) + ")" : "");
                if (plan.FineBox is { } box && !accWarm)
                {
                    try { accNest = LbmSolver.StartNested(accSession, box, accO, driveCoarse: true); }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
                    {
                        Log.Warn($"accurate: fine box refused ({e.Message}): the whole building is solved at the coarse cell size");
                        highNote += $" (fine box refused: {e.Message})";
                    }
                }
            }
        }
        catch (OperationCanceledException) { DisposeAccurate(); throw; }
        catch (Exception e)
        {
            DisposeAccurate();
            Log.Warn($"accurate: could not open the {cells / 1000}k-cell pass ({e.GetType().Name}: {e.Message})");
            Accurate.Off("the finer grid could not be opened (see the log)");
            return;
        }
        var fine = accGrid;
        if (aux.Length < fine.Count) { aux = new float[fine.Count]; auy = new float[fine.Count]; auz = new float[fine.Count]; }
        AccurateCapThisPass = (float)(AccurateCapForTest ?? (uplan != null
            ? (double.TryParse(System.Environment.GetEnvironmentVariable("WINDFLOW_ACCURATE_CAP_S"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double envCap) && envCap >= 30 ? envCap : UltraCapHours * 3600.0)
            : AveragedPass.CapSecondsFor(mode == AccurateMode.High, mode == AccurateMode.High ? Settings.Load().HighAccuracyCapMinutes : null)));
        // 6f: the pass is Core's. A cold pass settles first (a multiple of the session's warm-up), a warm seed starts its batches at once; the batch length is
        // planned from the rooms' V/Q at the end of the settle, and the monitor's start-up truncation drops what a seed still carries.
        accPass = new AveragedPass(new LbmPassSession(accSession, accNest), model, wind, accOptions!, new PassSettings
        {
            CapSeconds = AccurateCapThisPass,
            SettleFactor = Options.AccurateSettle > 0 ? Options.AccurateSettle : PassSettings.SettleFactorFor(options.Thermal, false),
            BatchScale = Options.AccurateAverage > 0 ? (float)Options.AccurateAverage : 1f,
            Seed = seed,
            RawClockFactor = uplan != null ? Ultra.RawClockFactor : 3,
            Publisher = uplan != null ? null : () => { int e = statsPublication.Current; return a => statsPublication.Publish(e, a); },   // a batch of an older model is dropped (Ultra: delivered at once, so a checkpoint can take every finished batch)
            OnStatus = Accurate.Note,                                                                                // RU-01: "computing air age…" during the settle-end solve
        });
        ultraCarried = 0; ultraNoSave = false;
        if (uplan != null)
        {
            ultraAwake = new UltraKeepAwake();   // this thread (the solver thread) keeps the PC awake until DisposeAccurate
            try
            {
                ultraRun = new UltraRun(accGrid!, model, wind, accOptions!, accOptions!.Ultra!, accSession!, s => Log.Info("ultra: " + s));
                ultraCarried = ultraRun.TryResume(accSession!, accPass, accGrid!, cancel.Token);
            }
            catch (OperationCanceledException) { ultraNoSave = true; DisposeAccurate(); throw; }
            catch (Exception e)   // only Ultra fails: Preview keeps running. A failed resume never saves (UltraRun turned writing off), and ultraNoSave covers any other setup error.
            {
                ultraNoSave = true;
                Log.Warn("ultra: " + e.Message);
                DisposeAccurate();
                Accurate.Off(e is UltraStoppedException ? e.Message : "Ultra could not start its saved-progress setup: " + e.Message);
                return;
            }
        }
        AccurateConvergenceText = "";
        // The finishing analysis (rooms + air age on the fine grid): the last measured one, per cell. Unknown on
        // the first pass of a launch: the bar then says the room numbers come after the time it shows.
        accFinishEstimate = accAnalysisMsPerCell > 0 ? accAnalysisMsPerCell * fine.Count / 1000.0 : double.NaN;
        AccurateCells = fine.Count;
        AccurateDx = fine.Dx;
        GridNote = clamped ? Envelope.BudgetClampNote(AccurateCellsTyped > 0 ? AccurateCellsTyped : StartupTier.AccurateCells(gpu), cells, fine.Dx) : "";
        // RZ-02: a Full domain the VRAM guard cut back says so with the result (Voxelizer's own note, e.g. "... the Compact domain was used").
        if (fine.Warnings.FirstOrDefault(w => w.StartsWith("Full wind domain", StringComparison.Ordinal)) is { } cut)
            GridNote = GridNote.Length > 0 ? GridNote + " " + cut : cut;
        AccuratePlan = $"{(accWarm ? "warm" : "cold")} · {accPass.PlanText}" + highNote;
        Accurate.Begin();
        accClock.Restart();
        accPausedSince = 0; accPausedSeconds = 0;
        geometryDirty = true;
        lastVolume = null;
        stepsKey = 0;                       // re-size the governor's bursts for the bigger grid
        ResetGovernorWindow();
        Log.Info($"accurate: opened in {clock.ElapsedMilliseconds} ms · {fine.Nx}x{fine.Ny}x{fine.Nz} = {fine.Count / 1000.0:0.#}k cells @ " +
                 $"{fine.Dx * 1000:0} mm (budget {cells / 1000}k{(clamped ? ", clamped to VRAM" : "")}, preview {grid.Count / 1000}k @ {grid.Dx * 1000:0} mm) · " +
                 $"{(accWarm ? "warm start from the Preview average" : "cold start")} · {accPass.PlanText} · " +
                 $"{accSession.TimeStepSeconds * 1000:0.00} ms per step · " +
                 $"finish estimate {accFinishEstimate:0.0} s");
    }

    /// <summary>The average is complete: land it on this thread (the session is single-threaded; the kept-batch mean, or one pull when fewer than two
    /// batches ended), free the fine session's GPU memory, and work out the age (online laid over the steady solve) and the rooms on a background thread.</summary>
    void FinishAccurate()
    {
        var pass = accPass!;
        var finishedRun = ultraRun;
        ultraFaults = 0; ultraOnlyOwn = false;
        float ultraDx = accOptions?.Ultra != null ? accGrid?.Dx ?? 0f : 0f;
        int stepsTaken = (int)accSession!.Steps;
        var outcome = pass.Land();
        var report = outcome.Report;
        AccurateStepsTaken = stepsTaken;
        DisposeAccurate();
        Accurate.Finishing(accFinishEstimate);
        int cycle = Accurate.Cycle;
        int epoch = statsPublication.Current;
        var m = model;
        var w = wind;
        var o = accOptions ?? options;
        AccurateStepSeconds = (float)accClock.Elapsed.TotalSeconds;
        Log.Info($"accurate: averaged in {accClock.Elapsed.TotalSeconds:0.0} s · working out the rooms on {outcome.Field.Grid.Count / 1000}k cells");
        new Thread(() =>
        {
            try
            {
                var timer = Stopwatch.StartNew();
                pass.SolveScalars(outcome);
                AccurateConvergenceText = outcome.Summary(NameOfId);
                Log.Info($"accurate: {AccurateConvergenceText}");
                var result = AnalyseFields(m, outcome.Field, outcome.Fine, w, o, out _, outcome.Scalars);
                result.Convergence = report;
                report.UltraDx = ultraDx;
                var volume = FieldVolume.Build(result);
                var ms = timer.Elapsed.TotalMilliseconds;
                statsPublication.Publish(epoch, () => accFinished = new AccurateFinished(cycle, result, volume, ms, finishedRun));   // an older model's result is dropped
            }
            catch (Exception e)
            {
                Log.Error("accurate: analysis failed", e);
                statsPublication.Publish(epoch, () => { if (cycle == Accurate.Cycle) Accurate.Off("the averaged numbers could not be worked out (see the log)"); });
            }
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "WindFlow accurate" }.Start();
    }

    /// <summary>The message a sealed box gets, in the solver and in the UI.</summary>
    public const string SealedBoxNote = "Box is sealed by walls: enlarge it so at least one face is in open air.";

    /// <summary>A box that cannot be solved at all. Measured cause on the sample house: an open face sitting
    /// in, or within one cell of, a wall. The previous domain is restored before this is shown.</summary>
    public const string DivergedBoxNote = "This box cannot be solved — the flow diverged immediately, so the " +
        "previous domain is back. Move the faces outside the building, or well clear of the nearest wall.";

    /// <summary>The trust badge when a box face runs through indoor air, and the fix that goes with it.</summary>
    public const string BoxCutBadge = "box cuts indoor air — flows are not reliable";
    public const string BoxCutHint = "Move the box faces outside the building, or well clear of the walls.";

    /// <summary>6c: Voxelizer.Build with this session's wind domain (<see cref="RunOptions.DomainMode"/>) for the current wind; a box ignores
    /// it. <paramref name="maxCells"/>: the Full domain's VRAM guard, 0 = what the solver card holds for one grid.</summary>
    VoxelGrid BuildGrid(int cells, DomainBox? box, long maxCells = 0) =>
        Voxelizer.Build(model, cells, AnalysisWorkers, box, mode: options.DomainMode, wind: wind,
            maxCells: options.DomainMode != DomainMode.Full ? 0
                : maxCells > 0 ? maxCells : LbmSolver.CellLimit(options.Backend != WindFlow.Core.Backend.Cpu && LbmSolver.GpuAvailable, options.Thermal));

    /// <summary>Voxelise for a candidate box and audit it. Returns null when the box is sealed (an open ring
    /// on solid cells): the solver diverges within one burst on such a grid, so it must never be started.</summary>
    VoxelGrid? TryBuild(DomainBox? box, out BoxAudit audit)
    {
        var next = BuildGrid(cellTarget, box);
        audit = box == null ? BoxAudit.None : BoxAudit.Inspect(next, model);
        return audit.IsSealed ? null : next;
    }

    /// <summary>S4-01 check only: runs first on the solver thread when a session opens, to hold it past the 8 s join the old Stop() gave up on.</summary>
    internal Action? OpenSessionHook;

    void OpenSession()
    {
        OpenSessionHook?.Invoke();
        // The startup probe first: LbmSolver.EnsureGpu marks the GPU "checked" before its accelerator exists, so
        // a second thread asking while the probe is still creating it reads "no GPU" and runs on the CPU.
        StartupTier.WaitReady(cancel.Token);
        bool gpu = options.Backend != WindFlow.Core.Backend.Cpu && LbmSolver.GpuAvailable;   // P17-07: after a loss the budget is the CPU's
        cellTarget = options.TargetCells ?? StartupTier.CellBudget(options.Quality, gpu, cancel.Token);
        options.Domain = CoarseDomain;
        if (TryBuild(CoarseDomain, out var audit) is { } built) grid = built;
        else
        {
            // Nothing to fall back to at open time: drop the box and run the whole building, saying why.
            BoxNote = SealedBoxNote;
            RejectedDomainSeq = LatestDomainSeq;
            BoxRejects++;
            domain = null;
            options.Domain = null;
            grid = BuildGrid(cellTarget, null);
            audit = BoxAudit.None;
        }
        Audit = audit;
        session = LbmSolver.Start(grid, model, wind, options);
        if (CoarseDomain != null && !ProbeSolvable())
        {
            // Nothing to fall back to at open time: drop the box and run the whole building, saying why.
            BoxNote = DivergedBoxNote;
            RejectedDomainSeq = LatestDomainSeq;
            BoxRejects++;
            domain = null;
            options.Domain = null;
            Audit = BoxAudit.None;
            grid = BuildGrid(cellTarget, null);
            var stale = session;
            session = null;
            stale?.Dispose();
            session = LbmSolver.Start(grid, model, wind, options);
        }
        Backend = session.Backend;
        previewReleased = false;
        UpdateGpuNote();
        ThermalActive = options.Thermal;
        Cells = grid.Count;
        Dx = grid.Dx;
        LogSession("opened");
        MarkAverageReset(true);
        if (ux.Length < grid.Count) { ux = new float[grid.Count]; uy = new float[grid.Count]; uz = new float[grid.Count]; }
        geometryDirty = true;
        lastVolume = null;
        if (!AttachNest())
        {
            // Nothing to fall back to at open time: keep the whole-building run and say why.
            RejectDomainChange(NestFailedNote + nestFailure, LatestDomainSeq);
            domain = null;
        }
        ActiveDomain = domain;
    }

    public const string NestFailedNote = "The fine box could not be opened, so the whole building is solved " +
        "without it: ";

    /// <summary>Steps a short probe to find out whether this grid can be solved at all. A box whose open face
    /// lands in (or one cell from) a wall blows up inside the first burst, and Core checks divergence every
    /// 200 steps, so 400 steps — a few tens of milliseconds — is enough to catch it. Measured on the sample
    /// house: a +X face at x = 5.5 m (in the partition) and at 5.7 m (one cell past it) both diverge, while
    /// 5.3 m and 6.4 m solve. No geometric test separated those four, hence a probe rather than a predictor.
    /// </summary>
    bool ProbeSolvable()
    {
        try
        {
            session!.Step(ProbeSteps, cancel.Token);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            Log.Warn($"probe: {ProbeSteps} steps on the new grid failed ({e.GetType().Name}: {e.Message})");
            return false;
        }
    }

    const int ProbeSteps = 400;

    /// <summary>Steps for the next burst: the PI controller's current value, clamped so the burst stays under
    /// <see cref="BurstCapMs"/> at the last measured cost per step. Re-estimated from the throughput when the
    /// work per step changes (new grid, nested box, thermal lattice).</summary>
    int BurstSteps()
    {
        long work = accGrid != null && accSession != null ? accGrid.Count + (accNest != null ? 2L * accNest.FineCells : 0)      // the Accurate pass steps alone
            : Cells + (nest != null ? 2L * nest.FineCells : 0);             // cell updates per (coarse) step
        long key = work * 2 + (options.Thermal ? 1 : 0);
        if (key != stepsKey || stepsI <= 0)
        {
            stepsKey = key;
            // Core's Mlups is from the previous lattice after a restart: start low and let the controller climb.
            double mlups = Math.Max(Mlups, 1);
            stepsI = Math.Clamp(0.5 * mlups * 1e6 * (BurstTargetMs / 1000.0) / Math.Max(work, 1), 1, 20000);
            msPerStep = 0;
        }
        double steps = stepsI * Math.Exp(burstP);
        if (msPerStep > 0) steps = Math.Min(steps, BurstCapMs / msPerStep);
        return (int)Math.Clamp(steps, 1, 20000);
    }

    double burstP;              // governor: the proportional part, as a log factor on stepsI

    /// <summary>PI controller on the burst length, in the log domain so the gains are independent of grid
    /// size: the error is ln(target / measured), the integral is stepsI itself (updated multiplicatively).</summary>
    void AdaptBurst(double measuredMs, int steps)
    {
        if (steps <= 0 || measuredMs <= 0) return;
        msPerStep = measuredMs / steps;
        double e = Math.Clamp(Math.Log(BurstTargetMs / Math.Max(measuredMs, 0.05)), -2, 2);
        stepsI = Math.Clamp(stepsI * Math.Exp(0.35 * e), 1, 20000);
        burstP = 0.3 * e;
    }

    /// <summary>Sleeps so the GPU-busy share of this cycle matches <see cref="GpuTargetPercent"/>. Busy is the
    /// host-timed Step (which ends in a synchronise) plus the synchronising readback; everything else in the
    /// cycle (publish, command handling) already counts as idle. Oversleeping is carried into the next cycle
    /// (the integral term), so timer granularity does not bias the measured duty.</summary>
    void Govern(Stopwatch cycle, double busyMs, int steps)
    {
        double target = Math.Clamp(GpuTargetPercent, 5, 100) / 100.0;
        double wantMs = busyMs / target;
        if (target < 0.999)
        {
            double sleepMs = wantMs - cycle.Elapsed.TotalMilliseconds + dutyDebtMs;
            if (sleepMs > 0.3) PreciseSleep.Wait(Math.Min(sleepMs, 250));
        }
        double actualMs = cycle.Elapsed.TotalMilliseconds;
        dutyDebtMs = target < 0.999 ? Math.Clamp(dutyDebtMs + wantMs - actualMs, -30, 30) : 0;

        winBusy += busyMs;
        winWall += actualMs;
        winSteps += steps;
        if (winWall < 1000) return;
        long cpu = PreciseSleep.ThreadCpuTicks();
        double simNow = accSession?.SimSeconds ?? session?.SimSeconds ?? 0;
        if (winSim >= 0 && winCpu > 0)
        {
            SimRate = (float)Math.Max(0, (simNow - winSim) / (winWall / 1000));
            SolverCpu = (float)((cpu - winCpu) / 1e4 / winWall);   // 100 ns ticks -> ms, per wall ms
        }
        Duty = (float)(winBusy / winWall);
        StepsPerSecond = (float)(winSteps / (winWall / 1000));
        if (++govWindows % 5 == 1)
            Log.Info($"governor: target {GpuTargetPercent} % · duty {Duty * 100:0.0} % · {StepsPerSecond:0} steps/s · " +
                     $"burst {busyMs:0.0} ms busy / {steps} steps (I {stepsI:0.#}, {msPerStep:0.000} ms/step) · " +
                     $"solver CPU {SolverCpu * 100:0} % · sim {SimRate:0.00} s/s");
        winSim = simNow;
        winCpu = cpu;
        winBusy = winWall = 0;
        winSteps = 0;
    }

    double winBusy, winWall, winSim = -1;
    long winSteps, winCpu;
    int govWindows;

    void Publish()
    {
        // A new averaging epoch is a new pass (run start, restart, wind or model change): its first rescale snaps to
        // the p98, and for its first 5 s of wall time the scale refreshes every 5th publish instead of every 20th.
        bool snap = false;
        if (passEpoch != averageEpoch) { passEpoch = averageEpoch; passClock.Restart(); passPublishes = 0; snap = true; }
        bool rescale = geometryDirty || lastVolume == null || snap ||
                       (passClock.Elapsed.TotalSeconds < 5 ? passPublishes % 5 == 0 : publishCount % 20 == 0);
        passPublishes++;
        var texels = texelPool[poolSlot];
        FieldVolume volume;
        if (accSession != null && accGrid != null)
            volume = FieldVolume.Build(accGrid, model, aux, auy, auz, geometryDirty ? null : lastVolume, texels, rescale, snap);
        else if (nest != null)
        {
            // Fine texels inside the box, coarse outside: one volume, so nothing downstream needs two grids.
            if (composite == null || composite.Coarse != grid || composite.Fine != nest.Grid)
            {
                var clock = Stopwatch.StartNew();
                composite = NestedComposite.Build(grid, nest.Grid, model);
                geometryDirty = true;
                var cg = composite.Grid;
                Log.Info($"nested composite {cg.Nx}x{cg.Ny}x{cg.Nz} @ {cg.Dx * 1000:0} mm built in {clock.ElapsedMilliseconds} ms");
            }
            composite.Fill(ux, uy, uz, fux, fuy, fuz);
            volume = FieldVolume.Build(composite.Grid, model, composite.Ux, composite.Uy, composite.Uz,
                geometryDirty ? null : lastVolume, texels, geometryDirty || rescale, snap);
        }
        else volume = FieldVolume.Build(grid, model, ux, uy, uz, geometryDirty ? null : lastVolume, texels, rescale, snap);
        texelPool[poolSlot] = volume.Texels;
        poolSlot = (poolSlot + 1) % texelPool.Length;
        lastVolume = volume;
        geometryDirty = false;
        publishCount++;
        Publishes = publishCount;
        particles.SetField(volume);
        if (RecordPublications)
        {
            var published = accSession != null && accGrid != null ? accGrid : nest != null && composite != null ? composite.Grid : grid;
            lock (PublicationLog) PublicationLog.Add((statsPublication.Current, published));
        }
    }

    int publishCount, passEpoch = -1, passPublishes;
    readonly Stopwatch passClock = Stopwatch.StartNew();

    // --selfcheck hooks: the CPU backend at a small cell budget (call before Start), and a log of which grid each
    // Publish() put on the particle path under which publication epoch.
    internal void UseCpuForTest(int cells) { options.Backend = WindFlow.Core.Backend.Cpu; options.TargetCells = cells; }
    /// <summary>SelfCheck hook (RZ-02): the Accurate pass's wall cap, seconds; null = the normal rule.</summary>
    internal double? AccurateCapForTest;
    internal volatile bool RecordPublications;
    internal readonly List<(int Epoch, VoxelGrid Grid)> PublicationLog = [];
    internal int PublicationEpochNow => statsPublication.Current;

    void DrainCommands()
    {
        while (commands.TryDequeue(out var command))
        {
            long applyStart = Stopwatch.GetTimestamp();
            try
            {
                // Every flow-changing edit cancels the Accurate pass (running or finished) and restarts the cycle
                // from Preview; pause and hold do not change the flow.
                if (EditCancelsAccurate(command)) { CancelAccurate(Describe(command)); ReopenPreview(); }
                switch (command)
                {
                    case PauseToggle p:
                        Pauses++;
                        LastEvent = $"pause({p.Paused}) at {SimSeconds:0.0}s";
                        State = p.Paused ? Mode.Paused : Mode.Running;
                        break;
                    case GeometryCommand g:
                        // Each command carries the whole model, so a newer queued geometry change supersedes
                        // this one: a device drag or a burst of edits costs one re-voxelisation, not ten.
                        int skipped = 0;
                        while (commands.TryPeek(out var next) && next is GeometryCommand newer && commands.TryDequeue(out _))
                        {
                            g = newer;
                            skipped++;
                        }
                        if (skipped > 0) Log.Info($"geometry: {skipped} queued change(s) superseded by {g.Why}");
                        command = g;
                        ApplyGeometry(g.Model);
                        break;
                    case WindChanged w:
                        ApplyWind(w.Wind);
                        break;
                    case HoldCommand h:
                        // A hold is a published result: if the average is younger than a full flow-through,
                        // throw it away and take the snapshot when a clean window has accumulated.
                        if (session is not { } held) break;
                        if (Accurate.Phase == AccuratePhase.Accurate && AccurateStats is { } accurateHeld)
                        {
                            // The finished Accurate result is the one worth holding: no second analysis needed.
                            if (h.Slot == 0) HoldA = accurateHeld; else HoldB = accurateHeld;
                            HoldError = "";
                            HoldVersion++;
                            break;
                        }
                        if (pendingHold >= 0)
                        {
                            // One waiting slot: a second request would silently replace the first.
                            HoldError = $"hold {(pendingHold == 0 ? "A" : "B")} is still waiting for a clean average; try again when it lands";
                            HoldVersion++;
                            break;
                        }
                        if (AverageSeconds < AverageTarget)
                        {
                            if (nest != null) nest.ResetAveraging();     // both lattices: one window
                            else held.ResetAveraging();
                            MarkAverageReset(false);
                            pendingHold = h.Slot;
                            HoldWaiting = true;
                        }
                        else ApplyHold(h.Slot);
                        break;
                    case DomainCommand d:
                        ApplyDomain(d.Domain, d.Seq, d.V1);
                        break;
                    case ConfigCommand c:
                        model = c.Model;
                        bool wasV1Grid = options.Domain != null;
                        bool domainModeChanged = options.DomainMode != DomainModeWanted;
                        options = new RunOptions
                        {
                            Quality = options.Quality,
                            Backend = options.Backend,
                            WindProfileExponent = options.WindProfileExponent,
                            TargetCells = options.TargetCells,
                            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                            Thermal = c.Thermal,
                            OpeningModel = c.OpeningModel ?? options.OpeningModel,
                            WallModel = WallModelWanted,
                            DomainMode = DomainModeWanted,
                            HoldIndoorTemperature = HoldIndoorWanted,
                        };
                        // Thermal on or off can switch a box between nested and v1, which is a different
                        // coarse grid: the domain path rebuilds whichever one the mode now needs.
                        if (domain != null && wasV1Grid != (CoarseDomain != null))
                            ApplyDomain(domain, LatestDomainSeq, boxV1);
                        else
                        {
                            options.Domain = CoarseDomain;
                            if (domainModeChanged && CoarseDomain == null) grid = BuildGrid(cellTarget, null);   // 6c: a new wind domain
                            Restart();
                        }
                        break;
                }
                LastCommandMs = (float)((Stopwatch.GetTimestamp() - command.Issued) * 1000.0 / Stopwatch.Frequency);
                Stale = false;
                if (Error.StartsWith("command failed", StringComparison.Ordinal)) Error = "";
                Log.Info($"command {Describe(command)} applied in {Log.Ms(applyStart)} ({LastCommandMs:0} ms after it was issued)");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                CommandFailures++;
                Log.Error($"command {Describe(command)} failed after {Log.Ms(applyStart)}", e);
                Error = "command failed: " + e.Message;
                Stale = false;
                // A throw part-way through UpdateCells/SetWind leaves the session in an unknown state: start
                // it again on the last grid that worked. If that throws too, session stays null and the loop
                // reports Failed, which is the status path the UI already handles.
                if (command is GeometryCommand or WindChanged or DomainCommand or ConfigCommand)
                {
                    try { Restart(); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception again)
                    {
                        // RU-11: a GPU fault inside the restart is the run's one CPU fallback (Loop -> AveragedPass.TryGpuRestart), not "could not restart"
                        if (!gpuRestarted && options.Backend != WindFlow.Core.Backend.Cpu && !stop && LbmSolver.IsGpuFault(again)) throw;
                        Log.Error("restart after the failed command also failed", again);
                        Error = "command failed and the solver could not restart: " + again.Message;
                    }
                }
            }
        }
    }

    static string Describe(Command command) => command switch
    {
        GeometryCommand g => $"Geometry({g.Why})",
        WindChanged w => $"Wind(({w.Wind.X:0.##}, {w.Wind.Y:0.##}) = {w.Wind.Length():0.##} m/s)",
        PauseToggle p => $"Pause({p.Paused})",
        HoldCommand h => $"Hold({(h.Slot == 0 ? "A" : "B")})",
        ConfigCommand c => $"Config(thermal={c.Thermal})",
        DomainCommand d => $"Domain({(d.Domain is { } b ? $"{b.Min} -> {b.Max}{(d.V1 ? " v1" : " nested")}" : "whole building")})",
        _ => command.GetType().Name,
    };

    /// <summary>One line per session (re)start: what the lattice was scaled for decides which later edits it
    /// can absorb, so this is the line to read first when a command misbehaves.</summary>
    void LogSession(string what)
    {
        if (session is not { } s) return;
        Log.Info($"session {what}: {s.Backend} · {grid.Nx}x{grid.Ny}x{grid.Nz} = {grid.Count / 1000.0:0.#}k cells @ {grid.Dx * 1000:0} mm · " +
                 $"wind {wind.Length():0.##} m/s · MaxWindSpeed {s.MaxWindSpeed:0.##} m/s · thermal {options.Thermal} · " +
                 $"devices {grid.Devices.Count} (peak target {PeakDeviceSpeed(grid):0.##} m/s) · " +
                 $"{(CoarseDomain is { } b ? $"box {b.Min} -> {b.Max} (v1)" : "whole building")}");
    }

    /// <summary>The fastest per-cell target velocity any device drives, in m/s: the number Core folds into uMax.</summary>
    static float PeakDeviceSpeed(VoxelGrid g)
    {
        float peak = 0;
        foreach (var d in g.Devices)
            foreach (var v in d.Target) peak = MathF.Max(peak, v.Length());
        return peak;
    }

    /// <summary>Switch the solve to a new simulation box (or back to the whole building).
    /// <para>Nested (the default): the coarse session stays the whole building and keeps its flow; only the fine
    /// session on the box is replaced, warm-started from it. A box Core refuses (or that blows up in the
    /// probe) is undone and the previous box, if any, attached again.</para>
    /// <para>v1: the box is the whole domain. Two ways out: a box sealed by walls never gets a session at all,
    /// and a box that diverges inside its probe is undone, the previous domain restarted and the reason
    /// published. Either way the app keeps solving something.</para></summary>
    void ApplyDomain(DomainBox? box, int seq, bool v1)
    {
        averageEpoch = statsPublication.Advance();
        ageCache = null; ageEpoch = -1;
        CancelPendingHold();
        var previousDomain = domain;
        bool previousV1 = boxV1;
        var previousGrid = grid;
        var previousAudit = Audit;
        var previousCoarseDomain = options.Domain;

        domain = box;
        boxV1 = v1;
        if (NestedMode)
        {
            if (options.Domain != null)
            {
                // Coming from a v1 box: the coarse run has to become the whole building first.
                options.Domain = null;
                grid = BuildGrid(cellTarget, null);
                Audit = BoxAudit.None;
                Restart();              // attaches the box itself
                if (nest != null) { BoxNote = ""; ActiveDomain = domain; return; }
            }
            else
            {
                Audit = BoxAudit.None;
                if (AttachNest()) { BoxNote = ""; ActiveDomain = domain; return; }
            }
            string why = nestFailure;
            domain = previousDomain;
            boxV1 = previousV1;
            if (previousCoarseDomain != null)
            {
                options.Domain = previousCoarseDomain;
                grid = previousGrid;
                Audit = previousAudit;
                Restart();
            }
            else if (!AttachNest()) domain = null;
            ActiveDomain = domain;
            RejectDomainChange(NestFailedNote + why, seq);
            return;
        }

        DetachNest();
        BoxModeNote = box != null && !v1 && options.Thermal ? ThermalV1Note : "";
        if (box == null && options.Domain == null && session != null)
        {
            // Box off while the coarse run already is the whole building: dropping the fine box is enough.
            Audit = BoxAudit.None;
            BoxNote = "";
            ActiveDomain = null;
            MarkAverageReset(false);    // the rooms in the box change source: start a fresh window
            session.ResetAveraging();
            return;
        }
        if (TryBuild(box, out var audit) is not { } nextGrid)
        {
            domain = previousDomain;
            boxV1 = previousV1;
            if (previousDomain != null && previousCoarseDomain == null && !AttachNest()) domain = null;
            RejectDomainChange(SealedBoxNote, seq);
            return;
        }

        options.Domain = box;
        grid = nextGrid;
        Audit = audit;
        Restart();
        if (box == null || ProbeSolvable())
        {
            BoxNote = "";
            ActiveDomain = domain;
            return;
        }

        domain = previousDomain;
        boxV1 = previousV1;
        options.Domain = previousCoarseDomain;
        grid = previousGrid;
        Audit = previousAudit;
        Restart();
        ActiveDomain = domain;
        RejectDomainChange(DivergedBoxNote, seq);
    }

    /// <summary>A pending hold was asked for on the model as it was; any change to the problem cancels it.</summary>
    void CancelPendingHold()
    {
        if (pendingHold < 0) return;
        Log.Info($"hold {(pendingHold == 0 ? "A" : "B")} cancelled: the model changed while it was waiting");
        pendingHold = -1;
        HoldWaiting = false;
        HoldError = "cancelled: model changed";
        HoldVersion++;
    }

    void ApplyGeometry(BuildingModel nextModel)
    {
        averageEpoch = statsPublication.Advance();
        ageCache = null; ageEpoch = -1;
        CancelPendingHold();
        var previousModel = model;
        var previousGrid = grid;
        var previousAudit = Audit;
        model = nextModel;
        var next = BuildGrid(cellTarget, CoarseDomain);
        // An opening toggle changes which cells on a box face are air, so the cut/dropped reading is re-taken.
        Audit = CoarseDomain == null ? BoxAudit.None : BoxAudit.Inspect(next, model);
        if (CoarseDomain != null && Audit.IsSealed)
        {
            // Same rule as ApplyDomain: a sealed box is never solved. Keep the geometry that still works.
            model = previousModel;
            Audit = previousAudit;
            RejectDomainChange(SealedGeometryNote, LatestDomainSeq);
            return;
        }
        // The lattice (dt, velocity scale) was sized at Start for the fastest air then in the model; Core says
        // how fast it can go (MaxSpeed). A device edit past that is the SetWind case: restart, do not warm-start.
        float peak = LbmSolver.RequiredSourceSpeed(next,model), ceiling = session!.MaxSpeed;
        bool sameDomain = next.Nx == grid.Nx && next.Ny == grid.Ny && next.Nz == grid.Nz &&
                          Math.Abs(next.Dx - grid.Dx) < 1e-6f && next.Origin == grid.Origin;
        if (sameDomain && (peak > ceiling || options.Thermal || previousModel.GroundZ != model.GroundZ))
        {
            Log.Info($"model edit requires new scaling (source peak {peak:0.##}, ceiling {ceiling:0.##}, thermal {options.Thermal}): restarting");
            grid = next;
            Restart();
        }
        else if (sameDomain)
        {
            session.UpdateCells(next, model);       // warm start: the flow keeps its state (Core reset the average)
            grid = next;
            Dx = grid.Dx;
            if (nest != null)
            {
                // Same box, new cell flags: Core rebuilds the driven faces and resets the fine average too.
                nest.UpdateCells(nest.BuildGrid(model), model: model);
                composite = null;
                PublishNestState();
            }
            MarkAverageReset(false);
        }
        else
        {
            grid = next;
            Restart();
        }
        geometryDirty = true;
        // A box can go unsolvable through a geometry change as well as a box change (an opening shut on a face):
        // probe it the way ApplyDomain does and put the previous geometry back if it blows up.
        if (CoarseDomain != null && !ProbeSolvable())
        {
            model = previousModel;
            grid = previousGrid;
            Audit = previousAudit;
            Restart();
            RejectDomainChange(DivergedGeometryNote, LatestDomainSeq);
        }
    }

    public const string SealedGeometryNote = "That change seals the simulation box, so the solve kept the previous " +
        "geometry. Enlarge the box so at least one face is in open air.";
    public const string DivergedGeometryNote = "That change made the box unsolvable (the flow diverged), so the " +
        "solve kept the previous geometry. Move the box faces well clear of the walls.";

    void RejectDomainChange(string note, int seq)
    {
        Log.Warn("box: " + note);
        BoxNote = note;
        RejectedDomainSeq = seq;
        BoxRejects++;
    }

    /// <summary>ToField + Analyze inline, so the held result is a complete SimulationResult (field, rooms,
    /// openings, ledger) that Viz and Report can draw exactly like a batch run. Costs one burst of latency.</summary>
    void ApplyHold(int slot)
    {
        if (session == null) return;
        try
        {
            var field = session.ToField();
            var held = AnalyseFields(model, field, nest?.ToField(), wind, options, out _,
                ageEpoch == averageEpoch ? ageCache : null);
            if (slot == 0) HoldA = held; else HoldB = held;
            HoldError = "";
            HoldVersion++;
            LastEvent = $"hold {(slot == 0 ? "A" : "B")} at {SimSeconds:0.0}s";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            Log.Error($"hold {(slot == 0 ? "A" : "B")} failed", e);
            HoldError = e.Message;
            HoldVersion++;      // the UI polls this: without the bump the strip stays on "holding..."
        }
    }

    void ApplyWind(Vector2 next)
    {
        averageEpoch = statsPublication.Advance();
        ageCache = null; ageEpoch = -1;
        CancelPendingHold();
        var before = wind;
        wind = next;
        // 6c: the Full domain is sized for the wind direction; a new direction gets a new grid.
        if (options.DomainMode == DomainMode.Full && CoarseDomain == null && next.Length() > 1e-3f &&
            (before.Length() <= 1e-3f || Vector2.Dot(Vector2.Normalize(before), Vector2.Normalize(next)) < 0.9999f))
        {
            grid = BuildGrid(cellTarget, null);
            Restart();
            return;
        }
        try
        {
            session!.SetWind(next);          // Core resets the coarse average for us
            nest?.ResetAveraging();          // and this resets the fine average (and the coarse one again)
            MarkAverageReset(false);
        }
        catch (InvalidOperationException e)
        {
            // above MaxWindSpeed: the lattice must be rescaled
            Log.Info($"wind {next.Length():0.##} m/s needs a restart: {e.Message}");
            Restart();
        }
    }

    void Restart()
    {
        CancelPendingHold();

        // The fine box reads the coarse lattice: it goes first, and comes back on the new session below.
        DetachNest();
        // Null first: if Start throws, the loop must see "no session", not step a disposed one.
        var previous = session;
        session = null;
        previous?.Dispose();
        session = LbmSolver.Start(grid, model, wind, options);
        Backend = session.Backend;
        previewReleased = false;
        UpdateGpuNote();
        ThermalActive = options.Thermal;
        SimSeconds = 0;
        Mlups = 0;
        Dx = grid.Dx;
        MarkAverageReset(true);              // the thermal lattice is slower: re-measure instead of sizing a burst from the old rate
        Cells = grid.Count;
        LogSession("restarted");
        if (ux.Length < grid.Count) { ux = new float[grid.Count]; uy = new float[grid.Count]; uz = new float[grid.Count]; }
        geometryDirty = true;
        lastVolume = null;
        if (!AttachNest())
        {
            domain = null;
            ActiveDomain = null;
            RejectDomainChange(NestFailedNote + nestFailure, LatestDomainSeq);
        }
    }

    /// <summary>Analyses the coarse field; nested, the fine one too, and lets it answer for the devices inside the
    /// box by two fine cells (an opening on the box face itself has no fine air on its outer side). Room ACH,
    /// opening flows, the ledger, the field and everything else stay the whole-building (coarse) ones, which is
    /// what the trust gate and the reports are about: the fine box sharpens the local field view only (batch 5,
    /// FIXLOG Batch 6l: against the uniform fine run the box read w-hall-e 14.7 % / Hall ACH 12.7 % off, the
    /// whole-building run 11.1 % / 3.3 %).</summary>
    internal static SimulationResult AnalyseFields(BuildingModel m, FlowField coarseField, FlowField? fineField, Vector2 w,
        RunOptions o, out SimulationResult? fine, ScalarFields? scalars = null)
    {
        var result = Analysis.Analyze(m, coarseField, w, o);
        // Air age (and CO2/tracer) from ScalarSolver on this same coarse grid: Finish turns it into MeanAge per room.
        if (scalars != null && scalars.Age.Length == coarseField.Grid.Count) result.Scalars = scalars;
        Analysis.Finish(result);
        fine = null;
        if (fineField == null) return result;
        fine = Analysis.Analyze(m, fineField, w, o);
        Analysis.Finish(fine);
        var fg = fineField.Grid;
        float inset = 2 * fg.Dx;
        for (int i = 0; i < result.Sources.Count; i++)
        {
            var src = result.Sources[i].Source;
            if (!NestedComposite.Inside(fg, src.Center, inset)) continue;
            foreach (var f in fine.Sources) if (f.Source.Id == src.Id) { result.Sources[i] = f; Analysis.DropDeviceMovedNote(result, src); break; }   // RW-08
        }
        return result;
    }

    /// <summary>Which analysis answers a probe at <paramref name="p"/>: the fine pass inside the box.</summary>
    public SimulationResult? ProbeSource(Vector3 p, SimulationResult? whole) =>
        FineStats is { } fine && NestedComposite.Inside(fine.Field.Grid, p) ? fine : whole;

    /// <summary>Room statistics and the mass ledger from the time-averaged field. Only <c>ToField</c> has to
    /// happen on this thread (the session is single-threaded); the analysis itself runs on a background thread
    /// at below-normal priority so neither publishing nor the renderer waits for it.</summary>
    void Analyse(bool force = false)
    {
        // While the Accurate pass runs the Preview session is idle and its settled numbers already stand; once it
        // is done its analysis is the result. Either way a Preview pass here would only repeat or overwrite.
        if (session == null || statsBusy || Accurate.Running || Accurate.Phase == AccuratePhase.Accurate) return;
        int epoch = statsPublication.Current;
        var sw = Stopwatch.StartNew();
        var field = session.ToField();
        var fineField = nest?.ToField();
        float pullMs = (float)sw.Elapsed.TotalMilliseconds;
        if (pullMs > MaxBurstMs * 2 && State == Mode.Running && !force) { AnalysisOnPauseOnly = true; return; }

        statsBusy = true;
        bool settled = AverageSeconds >= AverageTarget, running = State == Mode.Running;
        // Air age is a slow all-core solve (1.5-90 s measured, CPU contention included) and the room table's headline
        // number, fresh-air ACH, is 3600 / age (D1): without it the rows read "—" with a pending dot. So the Preview
        // pass solves it too, once the average has settled, even with an Accurate pass to follow (which therefore
        // starts after it; the added time is reported in FIXLOG).
        bool ageWanted = true;
        var snapshotModel = model;
        var snapshotWind = wind;
        var snapshotOptions = options;
        var snapshotComposite = composite;      // the mapping is immutable; the solver may swap in a new one
        new Thread(() =>
        {
            try
            {
                var timer = Stopwatch.StartNew();
                var scalars = AirAge(snapshotModel, field, snapshotOptions, epoch, settled && ageWanted, running);
                var result = AnalyseFields(snapshotModel, field, fineField, snapshotWind, snapshotOptions, out var fine, scalars);
                statsPublication.Publish(epoch, () =>
                {
                if (stop) return;
                FineStats = fine;
                // Independent of ScalarWanted: the UI enables its Temp button off this, and PublishScalar
                // only runs once that button is on (chicken and egg otherwise).
                TemperatureAvailable = result.Field.T.Length == result.Field.Grid.Count;
                AnalysisMs = pullMs + (float)timer.Elapsed.TotalMilliseconds;
                PublishScalar(result, fine, snapshotComposite);
                Report(result);
                });
            }
            catch (Exception e)
            {
                Log.Error("stats pass failed", e);
                statsPublication.Publish(epoch, () => Error = "stats failed: " + e.Message);
            }
            finally
            {
                // The Accurate pass waits for this settled pass; a failed one must not hold it back for ever.
                if (settled) statsPublication.Publish(epoch, () => settledEpoch = epoch);
                statsBusy = false;
            }
        })
        { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "WindFlow stats" }.Start();
    }

    volatile bool statsBusy;

    /// <summary>How long the last air-age solve took, in ms (0 before the first).</summary>
    public volatile float AgeMs;
    /// <summary>Wall-clock seconds between air-age refreshes while the averaged flow keeps improving.</summary>
    public const float AgeRefreshSeconds = 60;

    readonly PublicationEpoch statsPublication = new();
    int averageEpoch;                        // bumped by MarkAverageReset (solver thread)
    volatile ScalarFields? ageCache;         // written by the stats thread, read by ApplyHold
    volatile int ageEpoch = -1;
    readonly Stopwatch ageClock = new();

    /// <summary>Mean age of air for the room table. ScalarSolver costs ~4.5 s of every core on the 1.5 M-cell
    /// sample house (4000-sweep cap, measured 2026-09-28 with the GPU shared), against a 5 s stats cadence, so it
    /// cannot run every pass: it runs once the average has settled (the age of an unsettled field is noise), then
    /// again every <see cref="AgeRefreshSeconds"/> while the solver runs. In between, and while paused, the last
    /// solve is reused; a wind, geometry or box change (a new averaging epoch) drops it until the next settle.
    /// ponytail: a cold start every time; a warm start from the last age field would cut the sweeps, but
    /// ScalarSolver.Solve has no initial-guess parameter (Core change, reported).</summary>
    ScalarFields? AirAge(BuildingModel m, FlowField field, RunOptions o, int epoch, bool settled, bool running)
    {
        var cached = ageCache;
        if (cached != null && (ageEpoch != epoch || cached.Age.Length != field.Grid.Count)) cached = null;
        bool due = settled && (cached == null || (running && ageClock.Elapsed.TotalSeconds >= AgeRefreshSeconds));
        if (!due) return cached;

        var sw = Stopwatch.StartNew();
        var s = SolveAge(m, field, o);
        statsPublication.Publish(epoch, () =>
        {
            AgeMs = (float)sw.Elapsed.TotalMilliseconds;
            ageCache = s;
            ageEpoch = epoch;
            ageClock.Restart();
        });
        Log.Info($"air age: {AgeMs:0} ms on {field.Grid.Count / 1000}k cells (epoch {epoch})");
        return s;
    }

    /// <summary>Air age (and CO2/tracer) on <paramref name="field"/>. ScalarSolver appends its notes (iteration cap,
    /// "no steady state" caps) to the grid on every solve, and the live grid is shared by every pass: the previous
    /// solve's notes go first, so each appears once. Every air-age solve in the app goes through here (selfcheck too).</summary>
    internal static ScalarFields SolveAge(BuildingModel m, FlowField field, RunOptions o)
    {
        return ScalarSolver.Solve(m, field, o);   // RU-03: Solve strips the previous solve's notes itself
    }

    void Report(SimulationResult result)
    {
        // A slow Preview pass landing after the Accurate one must not replace it.
        if (Accurate.Phase == AccuratePhase.Accurate) return;
        var ledger = result.Ledger;
        if (ledger.In <= 0 && ledger.Out <= 0)
        {
            // Two very different situations produce an empty ledger. Before the averaging phase starts
            // ToField returns zeros and there is genuinely nothing yet. But a building with no OPEN
            // openings — an imported mesh has none at all, by definition — can never fill the ledger, and
            // saying "averaging…" for ever is a lie that also strands anything waiting on the trust gate.
            bool measurable = false;
            foreach (var o in model.Openings) if (o.IsOpen) { measurable = true; break; }
            Trust = measurable ? "averaging…" : "no open openings to measure";
            RoomLine = "";
            if (!measurable)
            {
                // The room statistics are still real and still worth showing; only the mass balance is not.
                RoomResult? big = null;
                foreach (var room in result.Rooms) if (big == null || room.Volume > big.Volume) big = room;
                RoomLine = big == null ? "" : $"{NameOf(big.Room)} {Standards.AchText(big)}";
                Stats = result;
                StatsVersion++;
            }
            return;
        }
        // Core resets the average on every change, so the only question is whether enough of the new regime
        // has accumulated yet. One flow-through of the building at this wind speed is the bar.
        if (AverageSeconds < AverageTarget)
        {
            Trust = $"averaging… {AverageSeconds:0.0}/{AverageTarget:0.0} s";
            Stats = result;
            StatsVersion++;
            return;
        }
        // A box face across a room makes that room's air a free boundary: the ledger can read "ok" and the
        // flows still be meaningless, so this overrides it rather than sitting beside it.
        Trust = Audit.CutsIndoorAir
            ? BoxCutBadge
            : ledger.IsPhysical
                ? $"ledger ok {ledger.Residual * 100:0.#}%"
                : $"ledger {ledger.Residual * 100:0.#}% ⚠";

        RoomResult? biggest = null;
        foreach (var room in result.Rooms)
            if (biggest == null || room.Volume > biggest.Volume) biggest = room;
        RoomLine = biggest == null ? "" : $"{NameOf(biggest.Room)} {Standards.AchText(biggest)}";
        Stats = result;
        StatsVersion++;
    }

    /// <summary>Which scalar the slice wants: 0 none, 1 pressure, 2 temperature. Set by the UI.</summary>
    public volatile int ScalarWanted;
    public volatile bool TemperatureAvailable;

    /// <summary>Downsamples the requested scalar onto the live volume's shape and hands it to the renderer.
    /// Reuses one buffer, so a 5 s cadence allocates nothing after the first pass.</summary>
    void PublishScalar(SimulationResult result, SimulationResult? fine, NestedComposite? comp, FieldVolume? target = null)
    {
        var field = result.Field;
        // Before any early return: the Temp button enables off this, and this method only runs once that
        // button has been used.
        TemperatureAvailable = field.T.Length == field.Grid.Count;
        // A Preview pass still in flight must not paint over the Accurate slice.
        if (target == null && Accurate.Phase == AccuratePhase.Accurate) return;
        int want = ScalarWanted;
        var volume = target ?? lastVolume;
        if (volume == null || want == 0) return;
        var source = want == 2 && TemperatureAvailable ? field.T : field.P;
        if (source.Length != field.Grid.Count) return;
        var sourceGrid = field.Grid;
        if (fine != null && comp != null)
        {
            // Nested runs are isothermal, so this is pressure: composed the same way as the velocity volume.
            if (comp.Coarse != field.Grid || comp.Fine != fine.Field.Grid || comp.Grid.Nx != volume.Nx ||
                comp.Grid.Ny != volume.Ny || comp.Grid.Nz != volume.Nz) return;     // a newer grid is on its way
            if (composedScalar == null || composedScalar.Length != comp.Grid.Count) composedScalar = new float[comp.Grid.Count];
            comp.FillScalar(field.P, fine.Field.P, composedScalar);
            source = composedScalar;
            sourceGrid = comp.Grid;
            want = 1;
        }

        // Pressure is already relative to the far field, but temperature is absolute: fed to the diverging
        // ramp raw it would paint every air cell the same red. Publish T minus the mean of the air cells and
        // tell the UI what that reference was, so the slice reads as warmer/cooler than the room average.
        if (want == 2)
        {
            double sum = 0;
            int n = 0;
            var grid = field.Grid;
            for (int i = 0; i < source.Length; i++)
            {
                if (grid.Cell[i] == VoxelGrid.Solid) continue;
                sum += source[i];
                n++;
            }
            float reference = n > 0 ? (float)(sum / n) : 0f;
            if (relativeBuffer == null || relativeBuffer.Length != source.Length)
                relativeBuffer = new float[source.Length];
            for (int i = 0; i < source.Length; i++) relativeBuffer[i] = source[i] - reference;
            ScalarReferenceC = reference;
            source = relativeBuffer;
        }
        else ScalarReferenceC = 0;

        int need = volume.Nx * volume.Ny * volume.Nz;
        if (scalarBuffer == null || scalarBuffer.Length != need) scalarBuffer = new Half[need];
        volume.DownsampleScalar(sourceGrid, source, scalarBuffer, out float p98);
        particles.SetScalarVolume(scalarBuffer, volume.Nx, volume.Ny, volume.Nz, p98);
        ScalarPublished = $"{(target != null ? "Accurate" : "Preview")} {(want == 2 ? "temperature" : "pressure")} from " +
                          $"{sourceGrid.Count / 1000}k cells onto {volume.Nx}x{volume.Ny}x{volume.Nz}";
    }

    /// <summary>What the slice last showed, for the shot sidecars.</summary>
    public volatile string ScalarPublished = "";

    Half[]? scalarBuffer;
    float[]? relativeBuffer, composedScalar;

    /// <summary>The value the temperature slice diverges about (°C); 0 for pressure.</summary>
    public volatile float ScalarReferenceC;

    /// <summary>Latest completed analysis pass, for the results panel and the probe. Replaced wholesale, so a
    /// reader only needs the reference; <see cref="StatsVersion"/> says when it changed.</summary>
    public volatile SimulationResult? Stats;
    public volatile int StatsVersion;

    /// <summary>Throughput under the governor: measured steps/s x cell updates per step. <see cref="Mlups"/> is
    /// the burst rate (what the GPU does while it is busy), so at a 50 % budget it overstates the real rate by
    /// ~2x; the peak is shown alongside only when the two differ noticeably.</summary>
    public float EffectiveMlups => StepsPerSecond * (Accurate.Running ? AccurateCells : Cells + (Nested ? 2f * FineCells : 0f)) / 1e6f;

    public string RateText()
    {
        float eff = EffectiveMlups, peak = Mlups;
        if (eff <= 0) return $"{peak:0} MLUPS";
        return eff < 0.9f * peak ? $"{eff:0} MLUPS (peak {peak:0})" : $"{eff:0} MLUPS";
    }

    public string StatusLine()
    {
        if (State == Mode.Failed) return "solver stopped: " + Error;
        if (!Active) return "";
        string head = State == Mode.Paused ? "paused" : "live";
        string trust = string.IsNullOrEmpty(Trust) ? "" : "  ·  " + Trust;
        string room = string.IsNullOrEmpty(RoomLine) ? "" : "  ·  " + RoomLine;
        string note = (AnalysisOnPauseOnly ? "  ·  stats on pause" : "") + (ThermalActive ? "  ·  thermal" : "") +
                      (Accurate.Phase == AccuratePhase.Accurate ? "  ·  " + WindFlow.Report.ResultStatus.StripText(AccurateStats?.Convergence, AccurateCells / 1e6) : "");   // S6-03: a tick only when Settled
        // The status strip is repainted every tick, so a refused box has to live here or it flashes past.
        string box = BoxNote.Length == 0 ? "" : "  ·  " + BoxNote;
        if (Error.Length > 0) box += "  ·  " + Error;
        if (GpuNote.Length > 0) box += "  ·  " + GpuNote;   // P17-07
        string grids = Nested && FineBox is { } fb
            ? $"box {fb.Size.X:0.#}×{fb.Size.Y:0.#}×{fb.Size.Z:0.#} m · fine {FineDx * 1000:0} mm · {FineCells / 1000.0:0.#}k cells · " +
              $"coarse {Dx * 1000:0} mm"
            : Accurate.Running
                ? $"Accurate {AccurateCells / 1000.0:0.#}k cells @ {AccurateDx * 1000:0} mm (Preview {Cells / 1000.0:0.#}k idle)"
                : $"{Cells / 1000.0:0.#}k cells @ {Dx * 1000:0} mm";
        return $"{head} · {Backend} · {grids} · t {SimSeconds:0.0} s · " +
               $"{RateText()} · field {FieldHz:0.#} Hz{trust}{room}{note}{box}" +
               (Stale ? "  ·  applying…" : "");
    }
}

/// <summary>Sub-millisecond sleeps for the GPU governor. Thread.Sleep rounds up to the 15.6 ms system tick
/// unless something raised the timer resolution, which would turn a 3 ms idle gap into 15 ms; a
/// high-resolution waitable timer (Windows 10 1803+) sleeps to within ~0.5 ms without spinning.</summary>
static class PreciseSleep
{
    [System.ThreadStatic] static nint timer;
    [System.ThreadStatic] static bool tried;

    public static void Wait(double ms)
    {
        if (!tried)
        {
            tried = true;
            timer = CreateWaitableTimerExW(0, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        }
        if (timer != 0)
        {
            long due = -(long)(ms * 10_000);          // relative, 100 ns units
            if (SetWaitableTimer(timer, ref due, 0, 0, 0, false))
            {
                WaitForSingleObject(timer, uint.MaxValue);
                return;
            }
        }
        Thread.Sleep(Math.Max(1, (int)Math.Round(ms)));
    }

    public static void Release()
    {
        if (timer != 0) CloseHandle(timer);
        timer = 0;
        tried = false;
    }

    /// <summary>User + kernel time of the calling thread, 100 ns units.</summary>
    public static long ThreadCpuTicks() =>
        GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user) ? kernel + user : 0;

    const uint CreateWaitableTimerHighResolution = 0x2, TimerAllAccess = 0x1F0003;

    [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);
    [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true)]
    static extern bool SetWaitableTimer(nint timer, ref long due, int period, nint completion, nint arg, bool resume);
    [System.Runtime.InteropServices.DllImport("kernel32")]
    static extern uint WaitForSingleObject(nint handle, uint ms);
    [System.Runtime.InteropServices.DllImport("kernel32")]
    static extern nint GetCurrentThread();
    [System.Runtime.InteropServices.DllImport("kernel32")]
    static extern bool CloseHandle(nint handle);
    [System.Runtime.InteropServices.DllImport("kernel32")]
    static extern bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);
}

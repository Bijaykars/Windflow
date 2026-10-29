using System.Diagnostics;
using System.Numerics;

namespace WindFlow.Core;

// 6f (S3-11) + 6k (P14-06): the Accurate averaged pass, UI-independent. The app's LiveSolver and the headless --solve (Simulation.Run with
// RunOptions.AutoConverge) both run THIS class, so settle, batch length, cap, stop rule, landing, the age pass and the result status are one rule:
//   settle  : SettleFactor x the session's warm-up steps (cold 2, warm seed 4 and no settle at all when seeded, thermal 1); the last fifth of it is a
//             short averaging window, pulled at its end for the plan and the age warm start
//   batches : one batch = Clamp(longest room V/Q, building flow-through, 4 x it) simulated seconds (>= 50 steps), V/Q from that settle-end window
//   age     : the frozen steady age on the settle-end mean flow warm-starts the online age (LbmSession.StartOnlineAge); every batch's room means feed a
//             second monitor, whose spread is the fresh-air ACH range; the landed age is the time mean over the kept batches
//   stop    : ConvergenceMonitor (99 % half-width + stationarity, MinTotalBatches 16, MinBatches 5): Converged or Diverged end the pass; so does the time cap
//             (CapSeconds of real elapsed time since the pass started, settle included, minus the user's pauses only; RU-02) or 3 x the cap
//             on the raw clock, whichever comes first (no batch-count stop, RW-01): it lands what the kept batches hold (a cap inside the settle lands nothing, RU-05)
//   landing : the equal-weight mean of the batches the monitor keeps (BatchWindowMean), the unfinished window if fewer than 2 ended
// The pass does not step: the caller steps the session (the app in governor bursts, Run in 100-step bursts) and calls Tick after each burst.

/// <summary>What the pass steps and reads: a session (and, for a High pass, its nested fine box), or a fake in a check.</summary>
public interface IPassSession
{
    int WarmupSteps { get; }
    long Steps { get; }
    float TimeStepSeconds { get; }
    void Step(int n, CancellationToken ct);
    /// <summary>Restarts the average at the current step.</summary>
    void ResetAveraging();
    /// <summary>The current average window: the coarse field and, with a nested box, the fine one.</summary>
    (FlowField Coarse, FlowField? Fine) Pull();
    /// <summary>Starts the online age on the coarse lattice from the frozen steady age; false (with a reason) when the session cannot.</summary>
    bool StartOnlineAge(float[] frozen, out string note);
}

/// <summary>An <see cref="LbmSolver.LbmSession"/> as the pass sees it; with <paramref name="nest"/> the nested session drives the coarse one.</summary>
public sealed class LbmPassSession(LbmSolver.LbmSession session, LbmSolver.NestedSession? nest = null) : IPassSession
{
    public int WarmupSteps => session.WarmupSteps;
    public long Steps => session.Steps;
    public float TimeStepSeconds => session.TimeStepSeconds;
    public void Step(int n, CancellationToken ct) { if (nest != null) nest.Step(n, ct); else session.Step(n, ct); }
    public void ResetAveraging() { if (nest != null) nest.ResetAveraging(true); else session.ResetAveraging(true); }
    public (FlowField, FlowField?) Pull() => (session.ToField(), nest?.ToField());
    public bool StartOnlineAge(float[] frozen, out string note)
    {
        bool ok = session.StartOnlineAge(frozen);
        note = session.OnlineAgeNote;
        return ok;
    }
}

public sealed class PassSettings
{
    /// <summary>Settle as a multiple of the session's warm-up steps: a warm seed washes out slowly (4), a cold isothermal start (2), a thermal pass keeps
    /// Core's own thermal warm-up (1). Measured on the sample house 2026-09-28c (LiveSolver history): cold 2x within 5-9 % on every room, 1x 33-64 % high.</summary>
    public const double ColdSettleFactor = 2.0, WarmSettleFactor = 4.0, ThermalSettleFactor = 1.0;
    public static double SettleFactorFor(bool thermal, bool warm) => thermal ? ThermalSettleFactor : warm ? WarmSettleFactor : ColdSettleFactor;

    /// <summary>Stepping seconds (settle included) after which the pass lands what its kept batches hold.</summary>
    public double CapSeconds = 600;
    /// <summary>The raw-clock ceiling as a multiple of <see cref="CapSeconds"/> (3 for Standard and High); Ultra raises it so a long run is not cut short by pauses or a sleeping PC.</summary>
    public double RawClockFactor = 3;
    public double SettleFactor = ColdSettleFactor;
    /// <summary>Measurement hook: scales the batch length (the app's <c>--accurate-average</c>).</summary>
    public float BatchScale = 1f;
    /// <summary>A warm seed (the settled Preview average): no settle, the seed is the plan's probe and the age's warm start.</summary>
    public FlowField? Seed;
    /// <summary>Run each batch's analysis on the stepping thread (a check needs batch boundaries that do not depend on thread timing).</summary>
    public bool AnalyseInline;
    /// <summary>Hands a finished batch analysis to the caller's thread: called on the stepping thread when a batch starts, it returns the function that
    /// delivers the batch later (the app binds it to its publication epoch). Default: deliver at once.</summary>
    public Func<Action<Action>>? Publisher;
    /// <summary>Test seam: replaces the batch analysis (room ACH and room mean age per batch).</summary>
    public Func<FlowField, FlowField?, PassBatch>? Analyse;
    /// <summary>Test seam: replaces the settle-end probe analysis (the longest room's V/Q, seconds).</summary>
    public Func<FlowField, float>? Probe;
    /// <summary>RU-01: a one-line status while the pass works on the stepping thread (the steady age solve); called on that thread.</summary>
    public Action<string>? OnStatus;
    /// <summary>Measurement seam WINDFLOW_FIXED_BATCHES=s,n (null = off): s batches are discarded after the normal settle, the next n kept, and the pass stops right
    /// after batch s+n with <see cref="ConvergenceStopReason.Fixed"/>: no convergence stop, no power-of-two truncation. The wall cap still applies (then Capped).</summary>
    public (int Settle, int Kept)? FixedBatches = ParseFixedBatches(System.Environment.GetEnvironmentVariable("WINDFLOW_FIXED_BATCHES"));
    /// <summary>Measurement seam WINDFLOW_GROUP_AGES=k (0 = off): needs <see cref="FixedBatches"/>. The n kept batches are split into k equal consecutive groups and the
    /// pass reports, per room and for the building, sigma = SD of the k group values / sqrt(k) (<see cref="ConvergenceReport.GroupSigma"/>).</summary>
    public int GroupAges = int.TryParse(System.Environment.GetEnvironmentVariable("WINDFLOW_GROUP_AGES"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var gk) && gk > 0 ? gk : 0;

    public static (int Settle, int Kept)? ParseFixedBatches(string? text)
    {
        var parts = (text ?? "").Split(',');
        return parts.Length == 2 && int.TryParse(parts[0], out int s) && int.TryParse(parts[1], out int n) && s >= 0 && n >= 1 ? (s, n) : null;
    }
}

/// <summary>RU-05: the cap ran out before the pass had averaged anything; there is no flow to land. Not a GPU fault (<see cref="LbmSolver.IsGpuFault"/>).</summary>
public sealed class PassTimeLimitException(string message) : Exception(message);

/// <summary>One finished batch: the pulled window and its rooms' ACH and mean age.</summary>
public sealed record PassBatch(FlowField Field, FlowField? Fine, List<(string RoomId, float Ach)> Rooms, List<(string RoomId, float Age)> Ages);

/// <summary>What the pass lands: the mean flow, the scalars (age online or steady), the convergence report with its age label.</summary>
public sealed class PassOutcome
{
    public FlowField Field = new();
    public FlowField? Fine;
    public ScalarFields Scalars = new();
    public ConvergenceReport Report = new();
    public bool AgeOnline;
    /// <summary>How the landing was taken: "mean of last 8 of 16 batches" or the unfinished-window fallback.</summary>
    public string Landed = "";
    public int Kept;
    public string Summary(Func<string, string>? nameOf = null)
    {
        var st = Report.Status;
        return $"{Report.Status.Reason} · {Report.Steps} steps · {Report.Batches} batches (transient {ConvergenceMonitor.TruncationFor(Report.Batches)}, kept {Kept}) · lands {Landed} · {Report.AgeLabel} · " +
            (st.Rooms.Count == 0 ? "no CI" : string.Join(" · ", st.Rooms.Select(r =>
                $"{nameOf?.Invoke(r.RoomId) ?? r.RoomId} {r.Mean:0.00} total-exchange ACH ±{r.HalfWidth:0.00} (tol {r.Tolerance:0.00}, varies ±{r.RelSd * 100:0} % batch-to-batch{(r.Converged ? "" : " NOT MET")})")));
    }
}

public sealed class AveragedPass
{
    public const string AgeOnlineLabel = "age: online (time-averaged)", AgeSteadyLabel = "age: steady on mean flow";
    /// <summary>AGE-FIX: the one disclosure for the steady age (UI tooltip, headless text, PDF footnote); the steady age has no resolved turbulent scalar flux (P14-06, AGE-1).</summary>
    public const string AgeSteadyNote = "Fresh-air ACH comes from the steady age of air on the averaged flow. It can differ from the age followed through time by about 30 % either way per room: it reads fresh air lower in slowly recirculating rooms and higher in fast-flushed ones.";
    /// <summary>AGE-HIGH: the disclosure for a High (gusty wind) result, whose fresh-air ACH is the online age and which carries the steady age beside it.</summary>
    public const string AgeGustyNote = "In gusty wind the headline fresh-air ACH is the age of air followed through the gusts. The steady-age value (steady age on the averaged flow) ignores gust mixing and usually reads lower, up to about 3 times, but not in every room.";
    /// <summary>Standard (per-room estimator): the disclosure when the headline fresh-air ACH is the online age (followed through time, with the turbulent mixing) in the rooms where it settled.</summary>
    public const string AgeStandardNote = "Fresh-air ACH is the age of air followed through time, with the turbulent mixing, in each room whose air age settled.";
    /// <summary>Standard: added when some rooms had not settled and landed the steady age (marked \"steady age\" in the status), followed by <see cref="AgeSteadyNote"/>.</summary>
    public const string AgeStandardMixedNote = "Rooms marked \"steady age\" had not settled within the time budget and use the steady age instead.";
    /// <summary>What fresh-air ACH = 3600 / mean air age means (Etheridge &amp; Sandberg 1996; Hurraß 2026); one sentence, appended wherever the formula is explained.</summary>
    public const string FreshAchMeaning = "It equals an air-change rate only when the room's air is fully mixed; otherwise it is the room's mean-age equivalent (Etheridge & Sandberg 1996; Hurraß 2026).";
    /// <summary>The grid-sensitivity disclosure (queue W2: single rooms move up to about 35 %, the building about 20 %, when the cell size changes). Rides on the same note as <see cref="AgeSteadyNote"/>
    /// (ResultStatus.AgeNote: UI tooltip, headless text); the PDF footnote prints it directly, as it applies to the online age too.</summary>
    public const string GridSensitivityNote = "Per-room numbers depend on the cell size: a small change in grid size can move a single room by up to about 35 % and the whole building by about 20 %. Compare design options on the same grid.";
    /// <summary>RU-01: the status while the settle-end steady age solve runs.</summary>
    public const string ComputingAgeStatus = "computing air age…";
    /// <summary>RU-05: why a pass that ran out of time during its settle lands nothing.</summary>
    public const string NoAveragingBase = "time limit reached before averaging started";
    public const string NoAveragingNote = NoAveragingBase + "; increase the time limit";
    /// <summary>RW-10b: the app's Standard pass has no time-limit setting (only High does): it names the remedy Standard has.</summary>
    public const string NoAveragingNoteStandard = NoAveragingBase + "; use High or a coarser cell";

    readonly IPassSession _s;
    readonly BuildingModel _model;
    readonly Vector2 _wind;
    readonly RunOptions _o;
    readonly PassSettings _cfg;
    readonly ConvergenceMonitor _monitor = new(), _ageMonitor = new() { AchFloor = 1f };   // the age's series is in seconds: a 1 s floor (the monitor's ACH floor is for rooms at ~0)
    readonly BatchWindowMean _meanC, _meanF;
    readonly (int S, int N)? _fixed;                 // FixedBatches seam
    readonly int _groupK, _groupM;                   // GroupAges seam: k groups of m consecutive kept batches (0 = off)
    readonly List<BatchWindowMean> _groups = [];     // each group mean flow, a clone-seeded window of its own
    readonly List<List<List<(string RoomId, float Ach)>>> _groupRooms = [];
    int _taken;                                      // batches delivered so far
    string _groupNote = "";
    readonly long _probeFrom;
    bool _timeLimitHit;                              // RW-01: a time limit (cap or 3 x ceiling) ended the pass
    bool _averaging, _probeReset, _ageOnline, _done, _finished, _agePending, _pending;   // _pending: a batch analysis is in flight or delivered but not yet taken
    long _batchEnd = long.MaxValue, _ageProbeAt;
    double _lastTick, _stepping;
    volatile PassBatch? _batch;
    volatile bool _busy;
    Thread? _worker;                                 // RU-04: the batch analysis in flight, so Land can cancel and join it
    CancellationTokenSource? _workCts;
    float[] _warmAge = [];
    HashSet<string> _ageSealed = [];   // rooms whose age series sits at the 24 h cap (sealed, or no cells): out of the "N of M rooms" count
    string _ageNote = "";

    public AveragedPass(IPassSession session, BuildingModel model, Vector2 wind, RunOptions options, PassSettings cfg)
    {
        _s = session; _model = model; _wind = wind; _o = options; _cfg = cfg;
        _fixed = cfg.FixedBatches;
        _meanC = new(_fixed?.S ?? -1); _meanF = new(_fixed?.S ?? -1);
        if (_fixed is { } fx) { _monitor.FixedDrop = fx.S; _ageMonitor.FixedDrop = fx.S; }
        if (cfg.GroupAges > 0)
        {
            if (_fixed is not { } f2) _groupNote = $"group sigma (k={cfg.GroupAges}): not run (WINDFLOW_GROUP_AGES needs WINDFLOW_FIXED_BATCHES, so the kept count is known up front)";
            else if (cfg.GroupAges < 2 || f2.N < cfg.GroupAges) _groupNote = $"group sigma (k={cfg.GroupAges}): not run (needs k >= 2 and at least k kept batches; kept {f2.N})";
            else
            {
                _groupK = cfg.GroupAges; _groupM = f2.N / _groupK;
                for (int g = 0; g < _groupK; g++) { _groups.Add(new BatchWindowMean(0)); _groupRooms.Add([]); }
                int dropped = f2.N - _groupK * _groupM;
                _groupNote = $"group sigma (k={_groupK}): {_groupM} batches a group" + (dropped > 0 ? $"; {f2.N} kept batches do not divide by {_groupK}, the last {dropped} dropped from the last group" : "");
            }
        }
        SettleSteps = cfg.Seed != null ? 0 : (long)Math.Ceiling(cfg.SettleFactor * session.WarmupSteps);
        _probeFrom = SettleSteps > 0 ? SettleSteps - Math.Max(50, SettleSteps / 5) : 0;
        if (_probeFrom < 0) _probeFrom = 0;
    }

    /// <summary>Steps of settle before the first batch (0 for a seeded pass).</summary>
    public long SettleSteps { get; }
    /// <summary>Steps of one batch; 0 until the settle has ended and the plan is made.</summary>
    public long BatchSteps { get; private set; }
    /// <summary>The plan's inputs (seconds), known once the settle has ended: the longest room's V/Q and the building flow-through.</summary>
    public float LongestRoomSeconds { get; private set; }
    public float BuildingFlowThroughSeconds { get; private set; }
    public bool Averaging => _averaging;
    public bool Done => _done;
    public ConvergenceStopReason Reason { get; private set; } = ConvergenceStopReason.Running;
    public int BatchCount => _monitor.BatchCount;
    public int TransientBatches => _monitor.TransientBatches;
    public int MinBatches => _monitor.MinBatches;
    public float RelativeTolerance => _monitor.RelativeTolerance;
    /// <summary>The worst room's stop half-width over its tolerance (1 = converged); NaN before a CI exists.</summary>
    public double WorstRatio { get; private set; } = double.NaN;
    public ConvergenceStatus? LastStatus { get; private set; }
    /// <summary>Real elapsed seconds since the pass started, minus the user's pauses (RU-02: a slow tick counts in full).</summary>
    public double SteppingSeconds => _stepping;
    /// <summary>RU-05: the pass ended on its cap before it had started averaging; <see cref="Land"/> has no flow to give.</summary>
    public bool TimeLimitBeforeAveraging { get; private set; }
    public bool AgeOnline => _ageOnline;
    /// <summary>The per-room choice (Standard): the room's online age lands only when its age series settled by the batch-6k rule (<paramref name="age"/> is the age monitor's row for the room) and has a usable range.</summary>
    public static bool OnlineAgeSettled(RoomConvergence? age) => age is { BatchCount: >= 2, Mean: > 0, Converged: true } && float.IsFinite(age.HalfWidth);
    /// <summary>The batch-6k gate (High): a room is settled only when its age is too, and the pass does not stop on an unsettled age. Returns the worst age width / tolerance.</summary>
    public static double GateByAge(ConvergenceStatus status, ConvergenceStatus ages)
    {
        double ageWorst = 0;
        foreach (var rc in status.Rooms)
        {
            var a = ages.Rooms.FirstOrDefault(x => x.RoomId == rc.RoomId);
            if (a == null) continue;
            rc.Converged &= a.Converged; rc.Stationary &= a.Stationary;
            ageWorst = Math.Max(ageWorst, a.Tolerance > 0 ? a.StopHalfWidth / a.Tolerance : double.PositiveInfinity);
        }
        if (status.Reason == ConvergenceStopReason.Converged && !status.Rooms.All(r => r.Converged)) { status.Converged = false; status.Reason = ConvergenceStopReason.Running; }
        return ageWorst;
    }
    /// <summary>Standard asks for the online age per room (<see cref="RunOptions.OnlineAgePerRoom"/>); High lands it everywhere.</summary>
    bool PerRoom => _o.OnlineAge && _o.OnlineAgePerRoom;

    /// <summary>The plan in words, for the log and the sidecar.</summary>
    public string PlanText => $"settle {SettleSteps} steps ({SettleSteps / Math.Max(1.0, _s.WarmupSteps):0.##}x warm-up) + batches of " +
        (BatchSteps > 0 ? $"{BatchSteps} steps (longest room V/Q {LongestRoomSeconds:0.#} s, building flow-through {BuildingFlowThroughSeconds:0.#} s)" : "1-4 building flow-throughs (planned at the end of the settle)") +
        $" until every room's ACH (and, when it runs online, air age) CI <= {RelativeTolerance * 100:0} % (cap {_cfg.CapSeconds:0} s)";

    /// <summary>Steps until the pass next needs a Tick to act (the end of the settle window, the settle, a batch); a caller that wants crisp batch ends steps no further.</summary>
    public long StepsToEvent
    {
        get
        {
            long steps = _s.Steps;
            long next = !_averaging ? (_probeReset || _probeFrom <= 0 ? SettleSteps : _probeFrom) : _agePending ? Math.Min(_ageProbeAt, _batchEnd) : _batchEnd;
            return next > steps ? next - steps : 100;   // past a batch end with its analysis still running: the batch runs on
        }
    }

    /// <summary>The building's flow-through, seconds: the longer plan side over the wind speed, clamped to 2..60 s so a still day does not ask for an hour.</summary>
    public static float FlowThroughSeconds(BuildingModel model, Vector2 wind, float? boxSide = null)
    {
        float length = 8f;
        try
        {
            var (pmin, pmax) = Voxelizer.PlanBounds(model);
            length = MathF.Max(pmax.X - pmin.X, pmax.Y - pmin.Y);
        }
        catch (ArgumentException) { /* nothing to simulate: the default is fine */ }
        if (boxSide is float b) length = MathF.Min(length, b);
        return Math.Clamp(length / MathF.Max(wind.Length(), 0.25f), 2f, 60f);
    }

    /// <summary>The stepping-time cap: the env override <c>WINDFLOW_ACCURATE_CAP_S</c> (>= 30 s, measurement only), else <paramref name="capMinutes"/> snapped to
    /// 5/10/20/30 (High Accuracy's choices) for a High pass or when given, else the Standard 10 minutes.</summary>
    public static double CapSecondsFor(bool high, int? capMinutes)
    {
        if (double.TryParse(System.Environment.GetEnvironmentVariable("WINDFLOW_ACCURATE_CAP_S"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double env) && env >= 30) return env;
        if (capMinutes is int m && (high || m > 0)) return HighAccuracy.SnapCapMinutes(m) * 60.0;
        return high ? HighAccuracy.DefaultCapMinutes * 60.0 : 600.0;
    }

    /// <summary>P17-07: the one GPU-failure policy of the run drivers (headless <see cref="Simulation.Run"/> and the app's LiveSolver). When a run that was on the
    /// GPU (<paramref name="onGpu"/>) throws an accelerator exception (<see cref="LbmSolver.IsGpuFault"/>; a cancel, a divergence and an out-of-memory are not),
    /// this clears the cached device (<see cref="LbmSolver.ResetGpu"/>), switches <paramref name="options"/> to the CPU, records the message in
    /// <see cref="RunOptions.GpuLossNote"/> and returns true: the caller restarts the same run, once (a run already on the CPU never returns true).</summary>
    public static bool TryGpuRestart(RunOptions options, bool onGpu, Exception e, out string note)
    {
        note = "";
        if (!onGpu || options.Backend == Backend.Cpu || !LbmSolver.IsGpuFault(e)) return false;
        LbmSolver.ResetGpu();
        note = $"The GPU stopped responding ({LbmSolver.GpuFaultReason(e)}). Restarting this run on the CPU.";
        options.Backend = Backend.Cpu;
        options.GpuLossNote = note;
        Console.Error.WriteLine("[GPU] " + note);
        return true;
    }

    /// <summary>One look after a burst of steps: ends the settle and plans the batches, takes a finished batch analysis, decides to stop, starts the next
    /// batch's analysis. <paramref name="wallSeconds"/>: a clock the caller owns, real elapsed seconds from the pass's start; <paramref name="pausedSeconds"/>:
    /// the part of it the user paused (the only time the cap does not count). <paramref name="ct"/> reaches the steady age solve and the probe analysis (RU-01).</summary>
    public void Tick(double wallSeconds, double pausedSeconds = 0, CancellationToken ct = default)
    {
        if (_done) return;
        long steps = _s.Steps;
        _lastTick = wallSeconds;
        _stepping = Math.Max(0, wallSeconds - pausedSeconds);
        bool timeUp = _stepping >= _cfg.CapSeconds || wallSeconds >= _cfg.RawClockFactor * _cfg.CapSeconds;   // RU-02: the cap, and 3 x it on the raw clock as the ceiling
        if (!_averaging)
        {
            if (timeUp) { TimeLimitBeforeAveraging = true; _timeLimitHit = true; Reason = ConvergenceStopReason.Capped; _done = true; return; }   // RU-05
            if (!_probeReset && _probeFrom > 0 && steps >= _probeFrom && steps < SettleSteps) { _s.ResetAveraging(); _probeReset = true; }
            if (steps < SettleSteps) return;
            BeginAveraging(steps, ct);
        }
        else if (_agePending && steps >= _ageProbeAt) StartSeededAge(steps, ct);
        if (_batch is { } b)
        {
            _batch = null;
            TakeBatch(b);
        }
        bool capped = timeUp;   // RW-01: no batch-count stop; the real-time cap and the 3 x raw ceiling bound the pass
        if (Reason is ConvergenceStopReason.Converged or ConvergenceStopReason.Diverged or ConvergenceStopReason.Capped or ConvergenceStopReason.Fixed || capped)
        {
            if (Reason == ConvergenceStopReason.Running) { Reason = ConvergenceStopReason.Capped; _timeLimitHit = true; }
            _done = true;
            return;
        }
        if (steps >= _batchEnd && !_busy) EndBatch(steps);
    }

    /// <summary>The settle is over: the short window it ended with is the plan's probe (batch length from the rooms' V/Q) and the age's warm start. A seeded
    /// pass has only the seed (on the Preview grid) for the plan: its age warm start waits for a short probe window of the session itself (RU-08).</summary>
    void BeginAveraging(long steps, CancellationToken ct)
    {
        var settled = _cfg.Seed ?? _s.Pull().Coarse;
        float longest = 0f;
        if (_cfg.Probe != null) longest = _cfg.Probe(settled);
        else
            foreach (var r in Analysis.Analyze(_model, settled, _wind, _o, ct, portal: false).Rooms)
                if (r.Inflow > 1e-6f) longest = MathF.Max(longest, r.VoxelVolume / r.Inflow);   // the solved air's transport time, not the nominal volume's
        float ft = FlowThroughSeconds(_model, _wind);
        LongestRoomSeconds = longest; BuildingFlowThroughSeconds = ft;
        BatchSteps = Math.Max(50, (long)Math.Ceiling(Math.Clamp(longest, ft, 4 * ft) * _cfg.BatchScale / Math.Max(_s.TimeStepSeconds, 1e-6f)));
        if (!_o.OnlineAge) _ageNote = "online age not requested";
        else if (_cfg.Seed != null) { _agePending = true; _ageProbeAt = steps + Math.Max(50, BatchSteps / 5); }
        else StartAge(settled, ct);
        _s.ResetAveraging();
        _batchEnd = steps + BatchSteps;
        _averaging = true;
    }

    /// <summary>RU-08: the seeded pass's age warm start, on a window of the session's own grid (at least 50 steps since the pass began).</summary>
    void StartSeededAge(long steps, CancellationToken ct)
    {
        _agePending = false;
        StartAge(_s.Pull().Coarse, ct);
        _s.ResetAveraging();
        _batchEnd = steps + BatchSteps;
    }

    /// <summary>The frozen steady age on <paramref name="settled"/> warm-starts the online age. The solve runs on the stepping thread: it takes the pass's token
    /// (every sweep checks it, RU-01), says so while it runs, and leaves none of its notes behind (RU-03: the landed age is the online one).</summary>
    void StartAge(FlowField settled, CancellationToken ct)
    {
        try
        {
            _cfg.OnStatus?.Invoke(ComputingAgeStatus);
            var frozen = ScalarSolver.Solve(_model, settled, _o, ct: ct, ageOnly: true).Age;
            ScalarSolver.StripNotes(settled.Grid.Warnings);   // RU-03: the warm-start solve's notes are not the landed result's
            _warmAge = frozen;
            _ageOnline = _s.StartOnlineAge(frozen, out _ageNote);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _ageOnline = false; _ageNote = e.Message;
        }
        finally { _cfg.OnStatus?.Invoke(""); }
    }

    /// <summary>The batch just ended: pull its mean, restart the average, and work its rooms out. Never more than one analysis in flight.</summary>
    void EndBatch(long steps)
    {
        var (coarse, fine) = _s.Pull();
        _s.ResetAveraging();
        _batchEnd = steps + BatchSteps;
        _busy = true; _pending = true;
        var deliver = _cfg.Publisher?.Invoke() ?? (a => a());
        var cts = _workCts = new CancellationTokenSource();
        void Work()
        {
            try { var done = AnalyseBatch(coarse, fine, cts.Token); deliver(() => _batch = done); }
            catch (OperationCanceledException) { /* Land: the pass is over, this batch is not wanted */ }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[AveragedPass] batch analysis failed: {e.Message}");
                deliver(() => _batch = new PassBatch(coarse, fine, [("", float.NaN)], []));
            }
            finally { _busy = false; }
        }
        if (_cfg.AnalyseInline) Work();
        else (_worker = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "WindFlow accurate batch" }).Start();
    }

    PassBatch AnalyseBatch(FlowField coarse, FlowField? fine, CancellationToken ct)
    {
        if (_cfg.Analyse != null) return _cfg.Analyse(coarse, fine);
        var warnings = coarse.Grid.Warnings;
        int n0 = warnings.Count;
        bool online = _ageOnline && coarse.Age.Length == coarse.Grid.Count && _warmAge.Length == coarse.Grid.Count;
        // With a fine box the rooms inside it come from the fine field (what the CI is about), as in the final analysis; the age is the coarse grid's.
        var result = Simulation.AnalyzeNested(_model, coarse, fine, _wind, _o, out _, finish: online, scalars: online ? new ScalarFields { Age = MergeAge(coarse.Age, _warmAge) } : null, ct: ct, portal: false);   // the portal numbers wait for the landed analysis
        if (warnings.Count > n0) warnings.RemoveRange(n0, warnings.Count - n0);   // Analyze appends its notes every pass
        return new PassBatch(coarse, fine, result.Rooms.Select(r => (r.Room.Id, r.Ach)).ToList(),
            online ? result.Rooms.Where(r => float.IsFinite(r.VolumeMeanAge)).Select(r => (r.Room.Id, r.VolumeMeanAge)).ToList() : []);
    }

    /// <summary>A room whose age series sits at the solver's 24 h cap (a sealed room, or one with no cells): no air reaches it, so it has no online-vs-steady age to count.</summary>
    internal static bool AgeSeriesSealed(RoomConvergence? age) => age is { BatchCount: >= 1 } && age.Mean >= ScalarSolver.AgeCapSeconds * 0.999f;

    /// <summary>The "N of M rooms" counts for the age label: sealed rooms (<paramref name="sealedIds"/>) are in neither.</summary>
    internal static (int Online, int Rooms) OnlineRoomCount(IReadOnlyList<RoomConvergence> rooms, ISet<string> sealedIds)
    {
        var counted = rooms.Where(r => !sealedIds.Contains(r.RoomId)).ToList();
        return (counted.Count(r => !r.AgeSteady), counted.Count);
    }

    /// <summary>Standard: true when some room landed its online age. An empty or missing status means every room steady (nothing to land the online age in).</summary>
    internal static bool AnyOnlineRoom(ConvergenceStatus? status) => status != null && status.Rooms.Any(r => !r.AgeSteady);

    /// <summary>Lays the steady age over <paramref name="merged"/> on each steady room's mask, except on cells that are also in an online-landed room's mask (those keep the online age).</summary>
    internal static void MergeSteadyRooms(float[] merged, float[] steady, IReadOnlyList<(bool Steady, bool[] Mask)> rooms)
    {
        var onlineCells = new bool[merged.Length];
        foreach (var (isSteady, mask) in rooms)
            if (!isSteady) for (int i = 0; i < merged.Length; i++) onlineCells[i] |= mask[i];
        foreach (var (isSteady, mask) in rooms)
            if (isSteady)
                for (int i = 0; i < merged.Length; i++) if (mask[i] && !onlineCells[i] && float.IsFinite(steady[i])) merged[i] = steady[i];
    }

    /// <summary>The online age where the scalar is solved, the steady value where it is held (outdoors NaN, fresh air 0, sealed pockets at the cap).</summary>
    static float[] MergeAge(float[] online, float[] steady)
    {
        var a = (float[])steady.Clone();
        for (int i = 0; i < a.Length; i++) if (float.IsFinite(online[i])) a[i] = online[i];
        return a;
    }

    /// <summary>Measurement seam WINDFLOW_BATCH_LOG=1 (accuracy plan W3a A4): one stderr line per batch with each room's ACH and mean age.</summary>
    static readonly bool BatchLog = System.Environment.GetEnvironmentVariable("WINDFLOW_BATCH_LOG") == "1";

    void TakeBatch(PassBatch b)
    {
        _taken++; _pending = false;
        if (BatchLog)
            Console.Error.WriteLine(FormattableString.Invariant($"[batch] {_taken} ach {string.Join(' ', b.Rooms.Select(r => $"{r.RoomId}={r.Ach:F3}"))} age {string.Join(' ', b.Ages.Select(r => $"{r.RoomId}={r.Age:F2}"))}"));
        _monitor.AddBatch(b.Rooms);
        var status = _monitor.Evaluate();
        if (status.Reason != ConvergenceStopReason.Diverged)
        {
            if (_groupK > 0)   // GroupAges seam: the kept batch joins its group mean (a clone seeds the group, before the landed mean may take the batch over)
            {
                int i = _taken - 1 - _fixed!.Value.S, gi = i / _groupM;
                if (i >= 0 && gi < _groupK)
                {
                    _groups[gi].Add(i % _groupM == 0 ? FlowFieldMean.Clone(b.Field) : b.Field);
                    _groupRooms[gi].Add(b.Rooms);
                }
            }
            _meanC.Add(b.Field);
            if (b.Fine != null) _meanF.Add(b.Fine);
        }
        double ageWorst = 0;
        if (b.Ages.Count > 0)
        {
            _ageMonitor.AddBatch(b.Ages);
            // The online age remembers many batches (a room's age is minutes, a batch seconds) and climbs from the steady warm start to its own level over several
            // flush times: a room is settled only when its air age is too (same rule: 99 % half-width under 5 %, the two halves of the kept batches agreeing), so the
            // pass never lands a still-climbing age as settled; the flow's own numbers are unchanged.
            // Standard (PerRoom): the age series is only recorded here; it never gates stopping or a room's Settled status (those stay as without the online age), Land picks the estimator per room.
            if (!PerRoom) ageWorst = GateByAge(status, _ageMonitor.Evaluate());
        }
        // ponytail: diagnostic only, off by default -- WINDFLOW_PASS_DEBUG=1 prints each batch's room ACH and mean air age (seconds)
        if (System.Environment.GetEnvironmentVariable("WINDFLOW_PASS_DEBUG") == "1")
            Console.Error.WriteLine($"[AveragedPass] batch {_monitor.BatchCount} at step {_s.Steps}: ach " + string.Join(" ", b.Rooms.Select(r => $"{r.RoomId}={r.Ach:0.00}")) +
                " | age " + string.Join(" ", b.Ages.Select(r => $"{r.RoomId}={r.Age:0.0}")));
        double worst = 0;
        foreach (var r in status.Rooms) worst = Math.Max(worst, r.Tolerance > 0 ? r.StopHalfWidth / r.Tolerance : double.PositiveInfinity);   // the width the stop rule tests (P16-08)
        WorstRatio = status.Rooms.Count == 0 ? double.NaN : Math.Max(worst, ageWorst);
        Reason = status.Reason == ConvergenceStopReason.Capped ? ConvergenceStopReason.Running : status.Reason;   // RW-01: the monitor's batch-count Capped is not a stop here; only a time limit is
        if (_fixed is { } fx)   // FixedBatches seam: no convergence stop; the pass ends right after batch s+n
        {
            if (Reason == ConvergenceStopReason.Converged) Reason = ConvergenceStopReason.Running;
            if (Reason == ConvergenceStopReason.Running && _taken >= fx.S + fx.N) Reason = ConvergenceStopReason.Fixed;
        }
        LastStatus = status;
    }

    // ---- Ultra checkpoints (see UltraCheckpoint) ----

    /// <summary>True when a checkpoint can be written right now, which is only at a batch boundary (the session's window just restarted, nothing pending) or
    /// early in the settle. At a boundary it waits for the batch analysis in flight and takes its result first, so the monitors hold every finished batch.</summary>
    public bool TryQuiesce(bool wait = true)
    {
        if (_done || _finished || _fixed != null || _groupK > 0) return false;
        long steps = _s.Steps;
        if (!_averaging) return !_probeReset && _probeFrom > 0 && steps < _probeFrom;
        if (_agePending || steps != _batchEnd - BatchSteps) return false;
        if (_pending)
        {
            if (!wait) return false;   // a stop never waits for the batch analysis
            _worker?.Join();
            if (_batch is not { } b) return false;
            _batch = null;
            TakeBatch(b);
        }
        return !_done && Reason is ConvergenceStopReason.Running;
    }

    const int PassMagic = 0x57465031;   // "WFP1"

    /// <summary>The pass's own state at a <see cref="TryQuiesce"/> point.</summary>
    public void WriteState(BinaryWriter w)
    {
        w.Write(PassMagic);
        w.Write(_averaging); w.Write(_probeReset); w.Write(_ageOnline);
        w.Write(_batchEnd); w.Write(BatchSteps); w.Write(LongestRoomSeconds); w.Write(BuildingFlowThroughSeconds);
        w.Write(_taken); w.Write(_timeLimitHit); w.Write(_lastTick); w.Write(_stepping); w.Write(WorstRatio); w.Write((int)Reason);
        w.Write(_ageNote); CkptIo.Write(w, _warmAge);
        _monitor.WriteState(w); _ageMonitor.WriteState(w);
        _meanC.WriteState(w); _meanF.WriteState(w);
    }

    /// <summary>Loads <see cref="WriteState"/> into a fresh pass over a session already restored to the same step.</summary>
    public void ReadState(BinaryReader r, VoxelGrid grid)
    {
        if (r.ReadInt32() != PassMagic) throw new InvalidDataException("not a pass state");
        _averaging = r.ReadBoolean(); _probeReset = r.ReadBoolean(); _ageOnline = r.ReadBoolean();
        _batchEnd = r.ReadInt64(); BatchSteps = r.ReadInt64(); LongestRoomSeconds = r.ReadSingle(); BuildingFlowThroughSeconds = r.ReadSingle();
        _taken = r.ReadInt32(); _timeLimitHit = r.ReadBoolean(); _lastTick = r.ReadDouble(); _stepping = r.ReadDouble(); WorstRatio = r.ReadDouble(); Reason = (ConvergenceStopReason)r.ReadInt32();
        _ageNote = r.ReadString(); _warmAge = CkptIo.ReadFloats(r);
        _monitor.ReadState(r); _ageMonitor.ReadState(r);
        _meanC.ReadState(r, grid, _model); _meanF.ReadState(r, grid, _model);
        _agePending = false; _pending = false;
        if (_monitor.BatchCount > 0)
        {
            var status = _monitor.Evaluate();
            if (status.Reason != ConvergenceStopReason.Diverged && !PerRoom && _ageMonitor.BatchCount > 0) GateByAge(status, _ageMonitor.Evaluate());
            LastStatus = status;
        }
    }

    /// <summary>The stepping seconds the cap has counted so far (what a checkpoint stores as the wall time used).</summary>
    public double WallUsedSeconds => _lastTick;

    /// <summary>Lands the pass (call once it is Done, or to land early; cheap, on the stepping thread): the kept-batch mean flow and the report. Pulls the session
    /// when fewer than two batches ended. The scalars come from <see cref="SolveScalars"/>, which the app runs off the stepping thread.</summary>
    public PassOutcome Land()
    {
        if (_finished) throw new InvalidOperationException("the pass has landed already");
        _finished = true;
        // RU-04: a batch analysis still running would share the grid's warnings with the final analysis: cancel it and wait for it to leave.
        _workCts?.Cancel();
        _worker?.Join();
        FlowField? field = _meanC.Mean, fine = _meanF.Mean;
        int kept = _meanC.Batches - _meanC.Dropped;
        string landed = field != null ? $"mean of last {kept} of {_meanC.Batches} batches" : "the unfinished window (fewer than 2 batches ended)";
        if (field == null) { (field, fine) = _s.Pull(); kept = 0; }
        var status = LastStatus ?? new ConvergenceStatus();
        status.Reason = Reason == ConvergenceStopReason.Running ? ConvergenceStopReason.Capped : Reason;
        status.Converged = status.Reason == ConvergenceStopReason.Converged;
        var ages = _ageMonitor.BatchCount > 0 ? _ageMonitor.Evaluate() : null;
        _ageSealed = ages?.Rooms.Where(AgeSeriesSealed).Select(x => x.RoomId).ToHashSet() ?? [];
        foreach (var rc in status.Rooms)
        {
            var a = ages?.Rooms.FirstOrDefault(x => x.RoomId == rc.RoomId);
            bool usable = a is { BatchCount: >= 2, Mean: > 0 } && float.IsFinite(a.HalfWidth);
            // Standard: the online age lands in a room only where that room's age settled (the batch-6k rule, unchanged); the others land the steady age.
            if (PerRoom && _ageOnline) rc.AgeSteady = !OnlineAgeSettled(a);
            if (!usable || rc.AgeSteady) continue;
            float k = 3600f / (a!.Mean * a.Mean);   // fresh ACH = 3600 / age: the delta method on the batch means of the age
            rc.FreshHalfWidth = a.HalfWidth * k;
            rc.FreshStopHalfWidth = float.IsFinite(a.StopHalfWidth) ? a.StopHalfWidth * k : float.NaN;
        }
        var report = new ConvergenceReport
        {
            Status = status, Batches = _monitor.BatchCount, Steps = _s.Steps, WallSeconds = _lastTick, WallCapHit = _timeLimitHit,
            FlowThroughSeconds = LongestRoomSeconds, FlowThroughSteps = (int)Math.Min(int.MaxValue, (long)Math.Ceiling(LongestRoomSeconds / Math.Max(_s.TimeStepSeconds, 1e-6f))),
            BatchSteps = (int)Math.Min(int.MaxValue, BatchSteps),
        };
        return new PassOutcome { Field = field, Fine = fine, Report = report, Landed = landed, Kept = kept };
    }

    /// <summary>The age pass on the landed flow (seconds on a big grid): the steady solve (CO2, tracer and the fallback age) and, when the online age ran and
    /// landed, its time mean laid over the steady age where the scalar was solved. Sets the report's age label, so the result says which age it carries.</summary>
    public void SolveScalars(PassOutcome outcome, CancellationToken ct = default)
    {
        var field = outcome.Field;
        var scalars = ScalarSolver.Solve(_model, field, _o, ct: ct);
        bool online = _ageOnline && field.Age.Length == field.Grid.Count && field.Age.Any(float.IsFinite);
        var rooms = outcome.Report.Status.Rooms;
        if (PerRoom && _ageOnline && !online) foreach (var r in rooms) r.AgeSteady = true;   // the landing is fully steady (no online field): every room says so, uniformly
        if (online && PerRoom)
        {
            // Standard: each room that did not settle takes the steady age on its own cells (the steady solve holds the rest of the grid either way); no steady value rides along.
            var merged = MergeAge(field.Age, scalars.Age);
            var steadyIds = rooms.Where(r => r.AgeSteady).Select(r => r.RoomId).ToHashSet();
            if (steadyIds.Count > 0)
            {
                var masks = Analysis.RoomMasks(field.Grid, _model, out _);
                MergeSteadyRooms(merged, scalars.Age, Enumerable.Range(0, Math.Min(_model.Rooms.Count, masks.Length)).Select(k => (steadyIds.Contains(_model.Rooms[k].Id), masks[k])).ToList());
            }
            online = AnyOnlineRoom(outcome.Report.Status);   // every room steady (or no room status at all): the plain steady result
            if (online) scalars = new ScalarFields { Co2 = scalars.Co2, Contaminant = scalars.Contaminant, Age = merged, OnlineAge = steadyIds.Count > 0 ? MergeAge(field.Age, scalars.Age) : [] };   // the raw online field rides along for the comparison column
        }
        else if (online) scalars = new ScalarFields { Co2 = scalars.Co2, Contaminant = scalars.Contaminant, Age = MergeAge(field.Age, scalars.Age), SteadyAge = scalars.Age };   // AGE-HIGH: the steady solve rides along as the conservative estimate
        outcome.Scalars = scalars; outcome.AgeOnline = online;
        outcome.Report.AgeOnline = online;
        outcome.Report.AgePerRoom = PerRoom;
        string why = _ageNote.Length > 0 ? _ageNote : _ageOnline ? "no batch landed it" : "";
        int nSteady = rooms.Count(r => r.AgeSteady);
        var (nOn, nCounted) = OnlineRoomCount(rooms, _ageSealed);
        outcome.Report.AgeLabel = online && PerRoom && nSteady > 0 ? $"age: online (time-averaged) in {nOn} of {nCounted} rooms, steady on mean flow in the rest"
            : !online && PerRoom && _ageOnline && rooms.Count > 0 && nSteady == rooms.Count ? $"{AgeSteadyLabel} (online age not settled in any room)"
            : online ? AgeOnlineLabel : _o.OnlineAge && why.Length > 0 ? $"{AgeSteadyLabel} (online age unavailable: {why})" : AgeSteadyLabel;
        SolveGroups(outcome.Report, ct);
    }

    /// <summary>GroupAges seam: on each group mean flow the same steady age solve and room/building fresh-air computation the landing uses; sigma per metric =
    /// sample SD of the k group values / sqrt(k). Fresh air from the group steady age; total exchange = the mean of the room batch values in the group.
    /// Reads only the group clones, so the landed numbers are untouched.</summary>
    void SolveGroups(ConvergenceReport report, CancellationToken ct)
    {
        report.GroupSigmaNote = _groupNote;
        report.GroupK = _groupK;
        if (_groupK == 0) return;
        if (_groups.Any(g => g.Batches < _groupM)) { report.GroupSigmaNote += $"; not run: only {_taken - _fixed!.Value.S} kept batches ended"; return; }
        var fresh = new List<Dictionary<string, double>>(); var building = new List<double>();
        var names = new Dictionary<string, string>();
        for (int g = 0; g < _groupK; g++)
        {
            var f = _groups[g].Window!;
            var warnings = f.Grid.Warnings; int n0 = warnings.Count;
            var scalars = ScalarSolver.Solve(_model, f, _o, ct: ct, ageOnly: true);
            var res = Analysis.Analyze(_model, f, _wind, _o, ct, portal: false);
            res.Scalars = scalars; Analysis.Finish(res);
            if (warnings.Count > n0) warnings.RemoveRange(n0, warnings.Count - n0);
            fresh.Add(res.Rooms.ToDictionary(r => r.Room.Id, r => (double)r.FreshAch));
            foreach (var r in res.Rooms) names[r.Room.Id] = r.Room.Name.Length > 0 ? r.Room.Name : r.Room.Id;
            building.Add(res.Ledger.FreshAch);
        }
        static (double Mean, double Sigma) Stat(IReadOnlyList<double> v)
        {
            double m = v.Average(), ss = v.Sum(x => (x - m) * (x - m));
            return (m, Math.Sqrt(ss / (v.Count - 1)) / Math.Sqrt(v.Count));
        }
        foreach (var (id, name) in names)
        {
            var (fm, fs) = Stat(fresh.Select(d => d.TryGetValue(id, out var x) ? x : double.NaN).ToList());
            var totals = _groupRooms.Select(batches => batches.Average(rooms => rooms.Where(r => r.RoomId == id).Select(r => (double)r.Ach).DefaultIfEmpty(double.NaN).First())).ToList();
            var (tm, ts) = Stat(totals);
            report.GroupSigma.Add(new GroupSigmaRow(name, fm, fs, tm, ts));
        }
        var (bm, bs) = Stat(building);
        report.GroupSigma.Add(new GroupSigmaRow("Building", bm, bs, double.NaN, double.NaN));
    }

    /// <summary><see cref="Land"/> then <see cref="SolveScalars"/>.</summary>
    public PassOutcome Finish(CancellationToken ct = default)
    {
        var outcome = Land();
        SolveScalars(outcome, ct);
        return outcome;
    }

    /// <summary>Headless: steps <paramref name="session"/> in 100-step bursts, ticking after each, until the pass stops, then lands it (and, unless
    /// <paramref name="solveScalars"/> is off, works the age out).</summary>
    public static PassOutcome Run(IPassSession session, BuildingModel model, Vector2 wind, RunOptions options, PassSettings cfg,
        CancellationToken ct = default, IProgress<double>? progress = null, bool solveScalars = true)
    {
        var pass = new AveragedPass(session, model, wind, options, cfg);
        var wall = Stopwatch.StartNew();
        while (!pass.Done)
        {
            ct.ThrowIfCancellationRequested();
            session.Step((int)Math.Clamp(pass.StepsToEvent, 1, 100), ct);
            pass.Tick(wall.Elapsed.TotalSeconds, 0, ct);
            progress?.Report(Math.Clamp(pass.SteppingSeconds / Math.Max(cfg.CapSeconds, 1), 0, 0.95));
        }
        if (pass.TimeLimitBeforeAveraging) throw new PassTimeLimitException(NoAveragingNote);   // RU-05: nothing to land
        if (solveScalars) return pass.Finish(ct);
        return pass.Land();
    }
}

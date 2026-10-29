using System.Diagnostics;
using System.Globalization;
using System.Text;
using Vortice.DXGI;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>--solve: one whole-building run with no window, so a quality level can be measured. Usage:
/// <code>WindFlow.App.exe --solve --quality preview|accurate|high [--focus roomId|name] [--wind 4,270] [--cap 10] [--domain compact|full] [--cells n] [--thermal [--free-running]] [model.json] [--solve-out path]</code>
/// <c>--thermal</c>: a thermal run with the model's indoor/outdoor temperatures; each room is held at the indoor temperature by an ideal
/// heater/cooler and its time-mean duty is listed (6j); <c>--free-running</c>: nothing holds it (the indoor temperature only seeds the start).
/// <c>--domain</c>: the wind domain (6c, default compact); <c>--cells</c>: the cell budget that sets Dx (preview: as given; accurate and high: the app's AccurateBudget, the tier's row or this, clamped to the card's memory).
/// <c>--focus</c> (high only): a 2:1 fine box around that room; none = the whole building at Accurate's Dx.
/// <c>--wind speed,fromDegrees</c> (default 4,270); <c>--cap</c> is the wall-time cap in minutes (5/10/20/30, High Accuracy and
/// Accurate; default 10). 6f: accurate and high run <see cref="AveragedPass"/>, the pass the app's Accurate runs (settle, batch length, cap, stop rule, landing, age), and the
/// accurate cell budget is the app's <see cref="AccurateBudget"/> (VRAM clamp included; no Preview session is resident here). Writes out/solve-&lt;quality&gt;.txt and prints the same text. <c>--quality high</c> is
/// <see cref="HighAccuracy"/>: its plan (Dx, cells, memory, warnings), then the run and a per-room table.</summary>
public static class HeadlessSolve
{
    public static int Run()
    {
        string q = Options.QualityName;
        BuildingModel model;
        try { model = Options.ModelPath is { } p ? ModelValidation.Load(p, Import.ImportGuard.MaxJsonBytes) : SampleBuilding.Build(); }   // RA-11
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Console.Error.WriteLine($"--solve: cannot use {Options.ModelPath}: {ex.Message}");
            return 2;
        }
        try { return Solve(model, q); }
        catch (Exception ex)   // RC-04: whatever the solver setup or run throws on a model that passed validation: a message and exit 2, never an unhandled crash
        {
            Console.Error.WriteLine($"--solve: cannot solve {Options.ModelPath ?? "the sample house"}: {ex.Message}");
            return 2;
        }
    }

    /// <summary>--compare: run Solve quietly and keep the result instead of printing and writing the text file.</summary>
    internal sealed class Capture { public SimulationResult? Result; }

    internal static int Solve(BuildingModel model, string q, Capture? cap = null)
    {
        var wind = SolverRun.WindVector(Options.WindFrom, Options.WindSpeed, model.NorthDeg);
        bool gpu = LbmSolver.GpuAvailable;
        var sb = new StringBuilder();
        void Line(string s) { sb.AppendLine(s); if (cap == null) Console.WriteLine(s); }

        if (Options.DomainName is not ("compact" or "full"))   // RZ-10: an unknown domain must not run as the Compact arm of a measurement
        {
            Console.Error.WriteLine($"--domain {Options.DomainName}: expected compact or full");
            return 2;
        }
        if (q == "ultra" && Options.SolveThermal)
        {
            Console.Error.WriteLine("--quality ultra --thermal: Ultra runs without thermal (wind only). Drop --thermal, or use --quality accurate or high for a thermal run.");
            return 2;
        }
        if (q == "ultra" && !gpu)
        {
            Console.Error.WriteLine("--quality ultra needs a GPU (a run this fine on the CPU would not finish); none was found.");
            return 2;
        }
        var options = new RunOptions { Backend = Backend.Auto, AgeOfAir = true, DomainMode = Options.DomainName == "full" ? DomainMode.Full : DomainMode.Compact };   // the calibrated opening model, the app's default; the age pass is the fresh-air ACH
        options.Thermal = Options.SolveThermal;
        options.HoldIndoorTemperature = !Options.FreeRunning;
        if (Options.FreeRunning && !Options.SolveThermal) Console.Error.WriteLine("--free-running has no effect without --thermal (the run is isothermal).");   // RT-10a
        // 6f: the app's budget rule, the same function: the tier's row (or --cells), clamped to the card's memory. No Preview session is resident in a headless run.
        double vramGb = gpu ? LbmSolver.GpuMemoryBytes / (1024.0 * 1024 * 1024) : 0;
        var (accurateCells, accurateClamped) = AccurateBudget.Resolve(Options.Cells, StartupTier.AccurateCells(gpu), vramGb, 0, Options.SolveThermal);
        HighAccuracyPlan? plan = null;
        UltraPlan? uplan = null;
        int capMinutes = HighAccuracy.SnapCapMinutes(Options.CapMinutes);
        double capSeconds = AveragedPass.CapSecondsFor(q == "high", Options.CapMinutes);   // the one cap rule (env override WINDFLOW_ACCURATE_CAP_S included)
        switch (q)
        {
            case "preview":
                options.Quality = Quality.Preview;
                options.TargetCells = Options.Cells > 0 ? Options.Cells : Simulation.TargetCells(Quality.Preview, gpu);
                break;
            case "accurate":
                options.Quality = Quality.Refine;
                options.TargetCells = accurateCells;
                options.AutoConverge = true;
                options.OnlineAge = true; options.OnlineAgePerRoom = true;   // Standard: the online age, landed per room where it settled
                options.WallCapSeconds = capSeconds;
                break;
            case "high":
                options.DomainMode = DomainMode.Compact;   // batch 5: HighAccuracy.Plan sizes the grid and box on the Compact domain; --domain full is not applied to High
                plan = HighAccuracy.Plan(model, FreeVramBytes(), accurateCells, Options.FocusRoom, options.Thermal);
                HighAccuracy.Configure(options, model, plan, capMinutes);
                options.WallCapSeconds = capSeconds;
                break;
            case "ultra":
                {
                    // Ultra: the Standard pipeline on the finest grid the openings need and the GPU holds; the cap is minutes on the command line (not snapped), default 20 h.
                    StartupTier.Begin();
                    try { StartupTier.Begin().Wait(TimeSpan.FromSeconds(30)); } catch (Exception) { /* a failed probe falls back to the constant below */ }
                    double mlups = StartupTier.Chosen?.ProbeMlups ?? 0;
                    capSeconds = double.TryParse(System.Environment.GetEnvironmentVariable("WINDFLOW_ACCURATE_CAP_S"), NumberStyles.Float, CultureInfo.InvariantCulture, out double envCap) && envCap >= 30
                        ? envCap : Options.CapGiven ? Options.CapMinutes * 60.0 : Ultra.DefaultCapHours * 3600.0;
                    long ultraVram = double.TryParse(System.Environment.GetEnvironmentVariable("WINDFLOW_ULTRA_VRAM_GB"), NumberStyles.Float, CultureInfo.InvariantCulture, out double vgb) && vgb > 0 ? (long)(vgb * 1024 * 1024 * 1024) : LbmSolver.GpuMemoryBytes;   // ponytail: test seam, plans Ultra for a smaller card so a smoke test fits next to other GPU users
                    var us = new UltraSettings { Fresh = Options.Fresh, NoCheckpoints = Options.NoCheckpoints, Log = s => Console.WriteLine("[ultra] " + s) };
                    long freeVram = 0;
                    if (System.Environment.GetEnvironmentVariable("WINDFLOW_ULTRA_VRAM_GB") == null && TryFreeVramBytes(out long fv)) freeVram = fv;   // the test seam plans for a smaller card with the 0.6 rule
                    UltraProbe? probe = Options.NoCheckpoints ? null : UltraCheckpoint.Probe(model, wind, options, us);   // a saved run decides the grid (it is rebuilt exactly)
                    uplan = probe?.Found != null && !Options.Fresh
                        ? Ultra.PlanResume(model, wind, probe.Found, ultraVram, 0, mlups, mlups > 0, LbmSolver.GpuName, capSeconds / 3600.0, freeVramBytes: freeVram)
                        : Ultra.Plan(model, wind, accurateCells, ultraVram, 0, mlups, mlups > 0, LbmSolver.GpuName, capSeconds / 3600.0, freeVramBytes: freeVram);
                    if (!uplan.Feasible) { Console.Error.WriteLine("--quality ultra: " + uplan.Refusal + (uplan.Resuming ? " Run again with --fresh to start over." : "")); return 2; }
                    if (uplan.DiskShort && !Options.NoCheckpoints)
                    {
                        Console.Error.WriteLine("--quality ultra: " + uplan.DiskWarning + " Or run with --no-checkpoints (the run then cannot be resumed).");
                        return 2;
                    }
                    us.Found = probe?.Found; us.FixedDx = uplan.Resuming ? uplan.Dx : 0f;
                    options.Quality = Quality.Refine;
                    options.TargetCells = uplan.BuildCells;
                    options.DomainMode = uplan.Domain;
                    options.MinDx = Ultra.MinDx;
                    options.AutoConverge = true;
                    options.OnlineAge = true; options.OnlineAgePerRoom = true;
                    options.WallCapSeconds = capSeconds;
                    options.Ultra = us;
                    break;
                }
            default:
                Console.Error.WriteLine($"--quality {q}: expected preview, accurate, high or ultra");
                return 2;
        }

        // RU-12: after a GPU fault the run restarts on the CPU with the CPU tier's cell budget (the app's rule: StartupTier / AccurateBudget), not the GPU-sized one.
        string replanNote = "";
        int replanCells = 0;   // the CPU re-plan works on a clone of the options: keep its figure for the header
        options.CpuReplan = o =>
        {
            int gpuCells = o.TargetCells ?? 0;
            var (cpuCells, _) = AccurateBudget.Resolve(Options.Cells, StartupTier.AccurateCells(false), 0, 0, Options.SolveThermal);
            if (q == "preview") o.TargetCells = Options.Cells > 0 ? Options.Cells : Simulation.TargetCells(Quality.Preview, false);
            else if (q is "accurate" or "ultra") o.TargetCells = cpuCells;
            else { HighAccuracy.Configure(o, model, HighAccuracy.Plan(model, 0, cpuCells, Options.FocusRoom, o.Thermal), capMinutes); o.WallCapSeconds = capSeconds; }
            replanCells = o.TargetCells ?? 0;
            replanNote = $"the GPU stopped and the run restarted on the CPU: cell budget re-planned for the CPU, {gpuCells / 1e6:0.##}M -> {o.TargetCells / 1e6:0.##}M cells";
        };
        // ponytail: test seam (RU-11/12, like WINDFLOW_NO_GPU): throws a fake accelerator fault from the GPU's 3rd stepping call; the real fault cannot be forced.
        if (System.Environment.GetEnvironmentVariable("WINDFLOW_FAKE_GPU_FAULT") == "1")
        {
            int fakeCalls = 0;
            LbmSolver.GpuStepTestHook = () => { if (++fakeCalls == 3) throw new InvalidOperationException("fake: CUDA_ERROR_LAUNCH_FAILED"); };
        }

        Line($"WindFlow headless solve · quality {q} · model {(Options.ModelPath ?? "sample house")} · wind {Options.WindSpeed:0.##} m/s from {Options.WindFrom:0}° · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line($"backend {(gpu ? LbmSolver.GpuName : "CPU")}");
        var envSet = System.Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("WINDFLOW_", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k, StringComparer.Ordinal).ToList();   // RA-18: a leaked test seam must show in the header
        if (envSet.Count > 0) Line("env: " + string.Join(", ", envSet.Select(k => $"{k}={System.Environment.GetEnvironmentVariable(k)}")));
        int backendEnd = sb.Length;
        if (LbmSolver.GpuFallbackNote is { } noGpu) Line(noGpu);   // P17-07
        // RZ-10: the effective domain and cell budget before the run; --cells only sets preview/accurate (high plans its own grid).
        string domainText = $"domain {options.DomainMode}{(q == "high" ? " (High runs use the Compact domain" + (Options.DomainName == "full" ? "; --domain full is not applied" : "") + ")" : "")}";
        string budgetLine = $"{domainText} · cell budget {(options.TargetCells is int tc ? $"{tc / 1e6:0.##}M" : "from the High plan")}" +
             (Options.Cells > 0 && q == "preview" ? $" (--cells {Options.Cells})" : "") +
             (q != "preview" ? $" (the app's Accurate budget{(q == "high" ? ", the coarse grid the High plan starts from" : "")}: {(Options.Cells > 0 ? $"--cells {Options.Cells}" : "the tier's row")}{(accurateClamped ? $", clamped to {accurateCells / 1e6:0.##}M by {vramGb:0.#} GB of GPU memory" : "")})" : "");
        Line(budgetLine);
        if (options.Thermal)
            Line($"thermal indoor {model.Env.IndoorTemperature:0.#} °C · outdoor {model.Env.OutdoorTemperature:0.#} °C · " +
                 (options.HoldIndoorTemperature ? "rooms held at the indoor temperature (ideal heating/cooling)" : "free-running (indoor temperature not held)"));
        if (uplan != null)
        {
            foreach (var ul in uplan.Lines()) Line(ul);
            Line($"cap {capSeconds / 3600:0.##} h ({capSeconds / 60:0.#} min){(Options.Fresh ? " · --fresh: any checkpoint is ignored" : " · a matching checkpoint is resumed automatically")}");
        }
        if (q is not ("preview" or "ultra") && capMinutes != Options.CapMinutes)
            Line($"       --cap {Options.CapMinutes} min rounded to {capMinutes} min (the cap is 5, 10, 20 or 30 minutes, as in the app)");   // P16-10c
        if (plan != null)
        {
            Line($"plan   {plan}");
            Line($"       Tu per supply on (wind-model off), approach turbulence EN 1991-1-4 L {options.WindTurbulence?.LengthScale:0.#} m, cap {options.WallCapSeconds / 60:0.#} min");
        }

        var wall = Stopwatch.StartNew();
        SimulationResult result;
        // RU-12: the header says so when the run restarted on the CPU (the backend line it replaces was written before the run)
        void RestartHeader()
        {
            if (replanNote.Length == 0) return;
            // batch 5: the "cell budget" line was written before the run with the GPU figure; after a CPU replan it shows the CPU figure
            sb.Replace(budgetLine, $"{domainText} · cell budget {replanCells / 1e6:0.##}M (re-planned for the CPU after the GPU fault)");
            sb.Insert(backendEnd, $"backend CPU (restarted after a GPU fault; was {(gpu ? LbmSolver.GpuName : "none")}): {replanNote}{System.Environment.NewLine}");
            Console.WriteLine(replanNote);
        }
        var ultraCts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, ev) => { ev.Cancel = true; ultraCts.Cancel(); Console.Error.WriteLine("[ultra] stop requested: finishing safely"); };
        if (uplan != null) Console.CancelKeyPress += onCancel;
        try { result = Simulation.Run(model, wind, options, null, ultraCts.Token); }
        catch (UltraStoppedException e)
        {
            Line($"Ultra stopped: {e.Message}");
            return 4;
        }
        catch (OperationCanceledException) when (uplan != null)
        {
            Line($"Ultra stopped on request. {options.Ultra?.Note} The checkpoint (if one was written) is in {options.Ultra?.CheckpointDir}; run the same command again to resume.");
            return 4;
        }
        catch (PassTimeLimitException e)   // RU-05: the cap ran out inside the settle: nothing was averaged, no numbers
        {
            RestartHeader();
            Line($"no result: {e.Message}");
            if (cap != null) { Console.Error.WriteLine($"--compare: no result: {e.Message}"); return 3; }
            Directory.CreateDirectory(Path.Combine(Options.RepoRoot(), "out"));
            File.WriteAllText(Options.SolveOut ?? Path.Combine(Options.RepoRoot(), "out", $"solve-{q}.txt"), sb.ToString());
            return 3;
        }
        wall.Stop();
        if (cap != null) { cap.Result = result; return 0; }
        if (gpu && result.Field.Backend.StartsWith("CPU", StringComparison.Ordinal))
        {
            Line("note: the GPU stopped and the run was restarted on the CPU; the figures below are the CPU run's (see the grid notes)");   // P17-07
            RestartHeader();
        }
        var g = result.Field.Grid;
        var conv = result.Convergence;
        double seconds = conv?.WallSeconds ?? result.Field.Seconds;
        long steps = conv?.Steps ?? result.Field.Steps;
        var fg = result.FineField?.Grid;
        long totalCells = g.Count + (fg?.Count ?? 0);
        Line($"grid   coarse Dx {g.Dx * 1000:0.#} mm · {g.Count / 1e6:0.##}M cells ({g.Nx}x{g.Ny}x{g.Nz})" +
             (fg != null ? $" · fine Dx {fg.Dx * 1000:0.#} mm · {fg.Count / 1e6:0.###}M cells ({fg.Nx}x{fg.Ny}x{fg.Nz})" : " · no fine box") +
             $" · est. memory {totalCells * (HighAccuracy.BytesPerCell) / 1e9:0.##} GB");
        Line($"domain {options.DomainMode} · {g.Nx * g.Dx:0.#} x {g.Ny * g.Dx:0.#} x {(g.Nz - 1) * g.Dx:0.#} m · blockage {(Envelope.Blockage(g, wind) is float b ? $"{b * 100:0.0} %" : "—")}");   // 6c
        var gridNotes = g.Warnings.Concat(fg?.Warnings ?? new List<string>()).ToList();
        foreach (var w in gridNotes.Take(8).Concat(gridNotes.Skip(8).Where(w => w.Contains("not recognised as openings", StringComparison.Ordinal) || w.Contains("too small for this cell size", StringComparison.Ordinal)))) Line($"       grid note: {w}");   // IMP-A D10 / IMP-C: never cut off
        Line($"run    {steps} steps · {wall.Elapsed.TotalSeconds:0.#} s wall (stepping + per-batch analysis {seconds:0.#} s) · {steps / Math.Max(seconds, 1e-9):0} steps/s · effective {(g.Count + 2.0 * (fg?.Count ?? 0)) * steps / Math.Max(seconds, 1e-9) / 1e6:0} MLUPS");
        if (conv != null)
            Line($"       {conv.Status.Reason} after {conv.Batches} batches (transient dropped by the monitor)" + (conv.WallCapHit ? $" · wall cap {options.WallCapSeconds / 60:0.#} min hit" : "") + $" · {conv.AgeLabel}");
        if (conv is { UltraDx: > 0 }) Line("result quality: " + WindFlow.Report.ResultStatus.ReportLine(conv));
        if (conv is { GroupSigmaNote.Length: > 0 })   // WINDFLOW_GROUP_AGES seam: sigma = SD of the k group values / sqrt(k)
        {
            Line("       " + conv.GroupSigmaNote);
            foreach (var row in conv.GroupSigma) Line("       " + row.Line(conv.GroupK));
        }
        if (conv is { BatchSteps: > 0 })
        {
            double stepsPerSecond = steps / Math.Max(seconds, 1e-9), batchMin = conv.BatchSteps / stepsPerSecond / 60;
            Line($"       batches of {conv.BatchSteps} steps (longest room V/Q {conv.FlowThroughSeconds:0} s) ~ {batchMin:0.#} min of wall at {stepsPerSecond:0} steps/s; " +
                 $"settle {PassSettings.SettleFactorFor(options.Thermal, false):0.#}x the session warm-up, then at least {new ConvergenceMonitor().MinBatches} batches kept ~ {new ConvergenceMonitor().MinBatches * batchMin:0} min");
            double rangeMin = ConvergenceMonitor.MinTotalBatches * batchMin;
            if (options.WallCapSeconds > 0 && rangeMin > options.WallCapSeconds / 60)   // P16-10b: say it up front, not as a surprise "± —" below
                Line($"       note: {ConvergenceMonitor.MinTotalBatches} batches (the least for a range) need ~{rangeMin:0} min of batches at this speed, the cap is {options.WallCapSeconds / 60:0.#} min: " +
                     "expect \"± —\" (too short for ranges); raise --cap or the cell budget");
        }
        Line($"report wind line: {WindFlow.Report.ReportWind.Text(result.Wind, result.Model.NorthDeg)}");   // P13b-05: what the PDF's Key numbers print for this run
        Line($"ledger residual {result.Ledger.Residual * 100:0.#} % ({(result.Ledger.IsPhysical ? "physical" : "NOT physical")})");
        Line($"building fresh-air rate {result.Ledger.FreshAch:0.0} ACH (3600 / volume-weighted mean air age over {result.Ledger.VolumeText})" +   // RW-03: names the sealed rooms left out
             (WindFlow.Report.ResultStatus.HasConservative(conv) ? $" · steady-age value {result.Ledger.ConservativeFreshAch:0.0} ACH (steady age on the averaged flow, ignores gust mixing)" : ""));   // AGE-HIGH
        Line("");
        Line($"room                 fresh-air ACH{(WindFlow.Report.ResultStatus.FreshHasRange(conv) ? " ± (averaging range, excludes slow age drift)" : "              ")}{(WindFlow.Report.ResultStatus.HasConservative(conv) ? "   Fresh-air (steady age)" : "")}   total exchange ACH (batch mean ± 99% stop width if settled, else 95% CI)   rel ±   batches  status                              grid   age occ.zone  air-change eff.  thru-open ACH  net outdoor ACH  unexpl. %");
        foreach (var r in result.Rooms)
        {
            var rc = conv?.Status.Rooms.FirstOrDefault(x => x.RoomId == r.Room.Id);
            // 6k: the fresh-air ± from the room's per-batch online air-age means (the steady-age result scales the total exchange's range instead); the app's FreshHalfWidth.
            bool tooShort = rc != null && !r.AgeSealed && WindFlow.Report.ResultStatus.FreshHasRange(conv, r.Room.Id) && WindFlow.Report.ResultStatus.TooShort(conv, r.Room.Id);   // P16-10a: the app's rule, "± —" with no number
            float fresh95 = rc != null && !r.AgeSealed ? WindFlow.Report.ResultStatus.FreshHalfWidth(conv, r) : float.NaN;
            string half = tooShort ? " ± —" : float.IsFinite(fresh95) ? $" ± {fresh95:0.0}" : "";
            string fresh = (Standards.FreshText(r) + half).PadLeft(22);
            string ach = rc != null
                ? r.AgeSealed ? $"{rc.Mean,7:0.0} (sealed room: no range)  {rc.BatchCount,7}"   // S6-17: a sealed room is "—", not "0.0 ± 0.0"
                : tooShort ? $"{rc.Mean,7:0.0} ± —                {rc.BatchCount,7}"
                : $"{rc.Mean,7:0.0} ± {WindFlow.Report.ResultStatus.Shown(rc),-6:0.0}  {(rc.Mean > 0 ? WindFlow.Report.ResultStatus.Shown(rc) / rc.Mean * 100 : double.NaN),5:0.0} %  {rc.BatchCount,7}"
                : $"{r.Ach,7:0.0} (last window)                 -";
            string status = (conv == null ? "fixed-length run" : ConvergenceReport.RoomStatus(rc)) + (r.TooFewCells ? " " + RoomResult.TooFewCellsLabel : "");   // IMP-C
            string age = float.IsNaN(r.MeanAge) ? "—" : r.AgeSealed ? "sealed" : $"{r.MeanAge / 60:0.0}";
            string ace = float.IsNaN(r.AirChangeEffectiveness) ? "—" : $"{r.AirChangeEffectiveness:0.00}";
            string cons = WindFlow.Report.ResultStatus.HasConservative(conv) ? "   " + (r.AgeSealed ? "sealed" : float.IsNaN(r.ConservativeFreshAch) ? "—" : r.ConservativeFreshAch.ToString("0.0", CultureInfo.InvariantCulture)).PadLeft(24) : "";   // AGE-HIGH: High only
            Line($"{r.Room.Name,-20} {fresh}{cons}   {ach}  {status,-34}  {(r.FromFineBox ? "fine" : "coarse"),-6} {age,7}  {ace,6}  {Portal(r.ThroughOpeningAch, "0.0"),13}  {Portal(r.NetOutdoorAch, "0.0"),15}  {Portal(r.UnexplainedShare * 100f, "0"),9}");
        }
        Line("(thru-open = through openings = air entering the room through its listed windows and doors (fixed planes, not the ragged room boundary). net outdoor = per exterior opening in minus out, floored at 0, summed. unexpl. % = share of all air entering the room that is not through its openings' planes. '—' = no opening plane fluxes.)");
        Line("(fresh-air ACH = 3600 / mean air age, the room's headline number. " + AveragedPass.FreshAchMeaning + " '—' = age not solved. A room marked 'steady age' landed the steady age because its online air age had not settled (no fresh-air ± for it). The fresh-air ± (averaging range, excludes slow age drift) is the room's 99 % stop width (95 % if not settled) of its per-batch time-averaged air age when the age is online; a steady-age result has none (the note above says so). " +
             "Total exchange = every cubic metre entering the room volume per hour incl. air from other rooms and through plain-air faces; its ± is the range around the mean of the kept batch means: the 99 % stop width for a room that settled, the 95 % CI otherwise (as the app shows). " +
             "Air-change efficiency (50 % = fully mixed) is shown only for a room fed >= 90 % straight from outside.)");
        if (WindFlow.Report.ResultStatus.AgeNote(conv) is { Length: > 0 } ageNote)
            Line($"note: {ageNote} ({WindFlow.Report.ResultStatus.AgeLine(conv)}{(WindFlow.Report.ResultStatus.FreshHasRange(conv) ? "" : "; no fresh-air ± is given")})");   // AGE-FIX: the one disclosure
        if (conv is { AgeOnline: true })
        {
            // 6k: what the online age changed against the steady solve on the SAME landed mean flow (the number the old pass reported).
            var steady = ScalarSolver.Solve(result.Model, result.Field, options, ageOnly: true);
            var cmp = Analysis.Analyze(result.Model, result.Field, wind, options);
            cmp.Scalars = steady; Analysis.Finish(cmp);
            Line("");
            Line("room                 volume-mean air age s: online (time-averaged)   steady on the same mean flow   steady / online      occupied-zone age s: online   steady   steady / online");
            bool anyStar = false;
            foreach (var r in result.Rooms)
            {
                var c = cmp.Rooms.FirstOrDefault(x => x.Room.Id == r.Room.Id);
                if (c == null || r.AgeSealed || float.IsNaN(r.VolumeMeanAge) || float.IsNaN(c.VolumeMeanAge)) { Line($"{r.Room.Name,-20} {(r.AgeSealed ? "sealed" : "—")}"); continue; }
                // a room that landed the steady age shows its raw online value, starred (comparison only; the headline is the landed value)
                bool st = WindFlow.Report.ResultStatus.AgeSteadyRoom(conv, r.Room.Id);
                float vol = st ? r.OnlineAgeRaw : r.VolumeMeanAge, occ = st ? r.OnlineMeanAgeRaw : r.MeanAge;
                if (st) anyStar = true;
                string Cell(float v, int w) => float.IsNaN(v) ? "—".PadLeft(w) : (v.ToString("0.0", CultureInfo.InvariantCulture) + (st ? "*" : "")).PadLeft(w);
                string Ratio(float v, float steady, int w) => float.IsNaN(v) ? "—".PadLeft(w) : (steady / v).ToString("0.000", CultureInfo.InvariantCulture).PadLeft(w);
                Line($"{r.Room.Name,-20} {Cell(vol, 28)}   {c.VolumeMeanAge,27:0.0}   {Ratio(vol, c.VolumeMeanAge, 15)}      {Cell(occ, 28)}   {c.MeanAge,6:0.0}   {Ratio(occ, c.MeanAge, 15)}");
            }
            if (anyStar) Line("* online age not settled in this run (not used for the headline); shown for comparison");
            Line($"building fresh-air rate: online {result.Ledger.FreshAch:0.0} ACH · steady on the same mean flow {cmp.Ledger.FreshAch:0.0} ACH");
        }
        if (options.Thermal)
        {
            Line("");
            Line("room                 temp °C (occupied zone)   held-temperature duty W (+ heating, - cooling; time mean)");
            foreach (var r in result.Rooms)
                Line($"{r.Room.Name,-20} {(float.IsNaN(r.MeanTemperature) ? "—" : r.MeanTemperature.ToString("0.0", CultureInfo.InvariantCulture)),23}   " +
                     (float.IsNaN(r.HoldDutyWatts) ? "— (not held)" : r.HoldDutyWatts.ToString("+0;-0;0", CultureInfo.InvariantCulture)));
            Line("(walls are adiabatic: the duty covers ventilation, occupants, equipment and Surface sources only, no fabric loss)");
        }

        string dir = Path.Combine(Options.RepoRoot(), "out");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Options.SolveOut ?? Path.Combine(dir, $"solve-{q}.txt"), sb.ToString());
        if (options.Ultra is { CheckpointDir: { } ckDir } && !Options.NoCheckpoints) UltraCheckpoint.Delete(ckDir, s => Console.WriteLine("[ultra] " + s));   // the result is on disk: the saved run has done its job
        return 0;
    }

    static string Portal(float v, string f) => float.IsNaN(v) ? "—" : v.ToString(f, CultureInfo.InvariantCulture);

    /// <summary>Free dedicated VRAM of the render adapter (DXGI budget minus current usage, this process included), falling back to
    /// the solver card's total when DXGI cannot say. Bytes; 0 when there is no GPU.</summary>
    /// <summary>Free dedicated VRAM measured now through DXGI, or false when there is no trustworthy figure (no adapter, the render adapter is not the solver's card, or the call failed);
    /// then Ultra keeps its 0.6 x total rule.</summary>
    public static bool TryFreeVramBytes(out long free)
    {
        free = 0;
        long total = LbmSolver.GpuMemoryBytes;
        if (total <= 0) return false;
        try
        {
            using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
            using var adapter = Viewport.Renderer.PickAdapter(factory, Options.Adapter);
            if (adapter != null && adapter.QueryInterfaceOrNull<IDXGIAdapter3>() is { } a3)
            {
                using (a3)
                {
                    var info = a3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
                    long budget = (long)info.Budget, used = (long)info.CurrentUsage;
                    if (budget <= 0 || Math.Abs((double)budget / total - 1.0) > 0.15) return false;   // another adapter's figure
                    free = Math.Min(total, Math.Max(0, budget - used));
                    return free > 0;
                }
            }
        }
        catch (Exception e) { Log.Warn($"free VRAM query failed ({e.GetType().Name}): Ultra keeps its 0.6 x total memory rule"); }
        return false;
    }

    public static long FreeVramBytes()
    {
        long total = LbmSolver.GpuMemoryBytes;
        if (total <= 0) return 0;
        try
        {
            using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
            using var adapter = Viewport.Renderer.PickAdapter(factory, Options.Adapter);
            if (adapter != null && adapter.QueryInterfaceOrNull<IDXGIAdapter3>() is { } a3)
            {
                using (a3)
                {
                    var info = a3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
                    long free = (long)info.Budget - (long)info.CurrentUsage;
                    if (free > 0) return Math.Min(total, free);
                }
            }
        }
        catch (Exception e) { Log.Warn($"free VRAM query failed ({e.GetType().Name}); using the card total"); }
        return total;
    }
}

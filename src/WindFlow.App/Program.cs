// UseWPF switches this project to the WindowsDesktop SDK, whose implicit usings drop System.IO.
global using System.IO;
using System.Globalization;
using System.Numerics;
using Avalonia;
using Velopack;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>Entry point and command line. Usage:
/// <code>WindFlow.App.exe [model.json] [--adapter &lt;substring&gt;] [--bench &lt;seconds&gt;]</code></summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack hooks the install/update/uninstall entry points before any UI exists.
        // The update feed (GitHub Releases) is used by UpdateCheck.cs.
        VelopackApp.Build().Run();
        Thread.CurrentThread.Name ??= "UI";
        Log.Init();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal($"unhandled exception (terminating={e.IsTerminating})", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("unobserved task exception", e.Exception);
            e.SetObserved();
        };
        Options.Parse(args);
        if (args.Contains("--selfcheck")) return SelfCheck.Run();
        if (Options.ImportTest != null) return ImportTest.Run(Options.ImportTest);
        if (Options.Solve) return HeadlessSolve.Run();
        if (Options.CompareRequested) return HeadlessCompare.Run();
        if (Options.MethodPdf != null)
        {
            // The designed 2-page explainer ships next to the exe (WindFlow.App.csproj); --method-pdf copies it.
            string shipped = Path.Combine(AppContext.BaseDirectory, "how-windflow-works.pdf");
            if (!File.Exists(shipped))
            {
                Console.Error.WriteLine($"how-windflow-works.pdf is missing next to the exe: {shipped}");
                return 2;
            }
            File.Copy(shipped, Options.MethodPdf, overwrite: true);
            Log.Info($"method PDF copied to {Options.MethodPdf}");
            return 0;
        }
        if (Options.ExportSample != null)
        {
            // The sample house as a .windflow.json, for tools/livelink-fake-revit.ps1 and anyone scripting the app.
            File.WriteAllText(Options.ExportSample, SampleBuilding.Build().ToJson());
            Log.Info($"sample model exported to {Options.ExportSample}");
            return 0;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}

/// <summary>Parsed command line. One static bag; the app is too small for anything else.</summary>
public static class Options
{
    public static string? ModelPath;     // positional argument
    public static string? Adapter;       // --adapter <substring of the DXGI description>
    public static double BenchSeconds;   // --bench <seconds>, 0 = interactive
    public static int Particles;         // --particles <n>: fixed count, no auto-adjust; 0 = startup tier + auto
    public static bool FakeLive;         // --fakelive: republish a perturbed field at 10 Hz (hitch test)
    public static string View = "speed"; // --view speed|still
    public static bool Live;             // --live: run the stepping solver instead of a batch solve
    public static string? Shot;          // --shot <path>: save one full-window screenshot and exit
    public static bool Empty;            // --empty: start with no model (for the empty-state screenshot)
    public static string ShotMode = "";  // --shotmode box|xray: what to set up before --shot fires
    public static string? Theme;         // --theme light|dark: this run's appearance (not saved; a shot in either variant)
    public static string? Isolate;       // --isolate <room id or name>: isolate that room before a shell --shot fires
    public static int TourStep;          // --tour-step <n>: with --shotmode tour, photograph only step n (1-based); 0 = every step
    public static double TurntableSeconds;   // --export-turntable <seconds> <path>
    public static string? TurntablePath;
    public static string? MethodPdf;         // --method-pdf <path>: copy the shipped "How WindFlow works" PDF to <path> and exit (no window, no solve)
    public static string? ReportPath;        // --report <path>: write one PDF report headlessly and exit
    public static int Occupants = 6;         // --occupants <n>: people the thermal check puts in the test room
    public static bool Twice;                // --twice: run the turntable export twice in one process
    public static DomainBox? Box;            // --box xmin,ymin,zmin,xmax,ymax,zmax: solve inside this box
    public static bool BoxV1;                // --box-v1: the box is the whole domain (open faces) instead of nested
    public static bool DevicesDemo;          // --devices-demo: switch the sample's devices on and photograph them
    public static string EditDevice = "";    // --edit-device "<name>" field=value: a live-bench inspector edit
    public static string EditAssign = "";
    public static bool SwapImport;           // --swap-import: import the test OBJ mid live bench
    public static double DeviceStress;       // --device-stress <seconds>: a device edit every 200 ms mid-bench
    public static string? ImportTest;        // --import-test <file>: run the import pipeline with the dialog defaults and print a summary
    public static bool UpdateNow;            // --update-now: installed copy only; check, download and apply an update once, no prompt (test)
    public static bool LiveLink;             // --livelink: listen for Revit on the pipe for this run (not persisted)
    public static string? ExportSample;      // --export-sample <path>: write the sample house JSON and exit
    public static int GpuUse;                // --gpu <percent>: GPU budget for this run (not persisted), 0 = setting
    public static double TierProbe;          // --tier-probe <mlups>: skip the startup probe, pick the tier from this
    public static float FpsInject;           // --fps-inject <fps>: feed the particle auto-adjust this fps (test only)
    public static bool Solve;                // --solve: one headless whole-building run (HeadlessSolve), no window
    public static string QualityName = "accurate";   // --quality preview|accurate|high|ultra (high = HighAccuracy, ultra = the long finest-grid run), with --solve
    public static double WindSpeed = 4, WindFrom = 270;   // --wind <m/s>,<from degrees>, with --solve
    public static bool CapGiven;             // --cap was on the command line (Ultra: minutes, not snapped; otherwise its default is 20 h)
    public static bool NoCheckpoints;        // --no-checkpoints: with --quality ultra, run without writing checkpoints (also skips the free-disk check)
    public static bool Fresh;                // --fresh: with --quality ultra, ignore any checkpoint and start a new run
    public static int CapMinutes = HighAccuracy.DefaultCapMinutes;   // --cap <minutes>: wall-time cap, snapped to 5/10/20/30
    public static string? FocusRoom;         // --focus <room id|name>: with --solve --quality high, a 2:1 fine box around this room
    public static string? SolveOut;         // --solve-out <path>: where --solve writes its text (default out/solve-<quality>.txt)
    public static string DomainName = "compact";   // --domain compact|full: the wind domain of --solve (6c, RunOptions.DomainMode); HeadlessSolve rejects anything else
    public static int Cells;                 // --cells <n>: with --solve preview|accurate, the cell budget that sets Dx (0 = the quality's own)
    public static bool SolveThermal;         // --thermal: with --solve, a thermal run (the model's Env; rooms held at its indoor temperature, 6j)
    public static bool FreeRunning;          // --free-running: with --solve --thermal, nothing holds the indoor temperature (RunOptions.HoldIndoorTemperature off)
    public static bool CompareRequested;     // any --compare token, so a missing path is an error and not a silent GUI start
    public static string? CompareA, CompareB;   // --compare <A.json> <B.json>: paired design comparison (HeadlessCompare)
    public static string OffsetsText = "4";
    public static int Offsets = 4;           // --offsets <k> (2-8): grid offsets for --compare
    public static string? CompareOut;        // --compare-out <file>: CSV of the comparison
    public static string AccurateModeName = "";   // --accurate-mode standard|high: the Accurate pass recipe for --shotmode accurate-time (with --focus <room>)
    public static string AccurateStart = "";// --accurate-start cold|warm: force the Accurate seed (measurement only)
    public static double AccurateSettle;     // --accurate-settle <x>: Accurate settle = x * WarmupSteps (measurement only), 0 = rule
    public static double AccurateAverage;    // --accurate-average <x>: Accurate averaging window x the rule (measurement only), 0 = rule

    public static void Parse(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "--adapter" && i + 1 < args.Length) Adapter = args[++i];
            else if (a is "--bench" && i + 1 < args.Length && double.TryParse(args[++i], out double s)) BenchSeconds = s;
            else if (a is "--particles" && i + 1 < args.Length && int.TryParse(args[++i], out int n)) Particles = n;
            else if (a is "--fakelive") FakeLive = true;
            else if (a is "--live") Live = true;
            else if (a is "--shot" && i + 1 < args.Length) Shot = args[++i];
            else if (a is "--empty") Empty = true;
            else if (a is "--shotmode" && i + 1 < args.Length) ShotMode = args[++i].ToLowerInvariant();
            else if (a is "--theme" && i + 1 < args.Length) Theme = args[++i];
            else if (a is "--isolate" && i + 1 < args.Length) Isolate = args[++i];
            else if (a is "--tour-step" && i + 1 < args.Length && int.TryParse(args[++i], out int ts)) TourStep = ts;
            else if (a is "--report" && i + 1 < args.Length) ReportPath = args[++i];
            else if (a is "--method-pdf" && i + 1 < args.Length) MethodPdf = args[++i];
            else if (a is "--occupants" && i + 1 < args.Length && int.TryParse(args[++i], out int p)) Occupants = p;
            else if (a is "--twice") Twice = true;
            else if (a is "--box" && i + 1 < args.Length) Box = ParseBox(args[++i]);
            else if (a is "--box-v1") BoxV1 = true;
            else if (a is "--devices-demo") DevicesDemo = true;
            else if (a is "--swap-import") SwapImport = true;
            else if (a is "--livelink") LiveLink = true;
            else if (a is "--update-now") UpdateNow = true;
            else if (a is "--import-test" && i + 1 < args.Length) ImportTest = args[++i];
            else if (a is "--export-sample" && i + 1 < args.Length) ExportSample = args[++i];
            else if (a is "--gpu" && i + 1 < args.Length && int.TryParse(args[++i], out int g)) GpuUse = Math.Clamp(g, 5, 100);
            else if (a is "--tier-probe" && i + 1 < args.Length && double.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out double tp)) TierProbe = tp;
            else if (a is "--fps-inject" && i + 1 < args.Length && float.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out float fi)) FpsInject = fi;
            else if (a is "--device-stress" && i + 1 < args.Length && double.TryParse(args[++i], out double ds)) DeviceStress = ds;
            else if (a is "--edit-device" && i + 2 < args.Length)
            {
                EditDevice = args[++i];
                EditAssign = args[++i];
            }
            else if (a is "--export-turntable" && i + 2 < args.Length &&
                     double.TryParse(args[i + 1], out double seconds))
            {
                TurntableSeconds = seconds;
                TurntablePath = args[i + 2];
                i += 2;
            }
            else if (a is "--view" && i + 1 < args.Length) View = args[++i].ToLowerInvariant();
            else if (a is "--solve") Solve = true;
            else if (a is "--compare")
            {
                CompareRequested = true;
                if (i + 2 < args.Length && !args[i + 1].StartsWith("--") && !args[i + 2].StartsWith("--")) { CompareA = args[++i]; CompareB = args[++i]; }
            }
            else if (a is "--offsets") { OffsetsText = i + 1 < args.Length ? args[++i] : ""; Offsets = int.TryParse(OffsetsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ko) ? ko : -1; }
            else if (a is "--compare-out" && i + 1 < args.Length) CompareOut = args[++i];
            else if (a is "--thermal") SolveThermal = true;
            else if (a is "--free-running") FreeRunning = true;
            else if (a is "--quality" && i + 1 < args.Length)
            {
                string qn = args[++i].ToLowerInvariant();
                QualityName = qn == "highaccuracy" ? "high" : qn;
            }
            else if (a is "--cap" && i + 1 < args.Length && int.TryParse(args[++i], out int cap)) { CapMinutes = cap; CapGiven = true; }
            else if (a is "--fresh") Fresh = true;
            else if (a is "--no-checkpoints") NoCheckpoints = true;
            else if (a is "--focus" && i + 1 < args.Length) FocusRoom = args[++i];
            else if (a is "--solve-out" && i + 1 < args.Length) SolveOut = args[++i];
            else if (a is "--domain" && i + 1 < args.Length) DomainName = args[++i].ToLowerInvariant();
            else if (a is "--cells" && i + 1 < args.Length && int.TryParse(args[++i], out int nc)) Cells = nc;
            else if (a is "--wind" && i + 1 < args.Length)
            {
                var w = args[++i].Split(',');
                if (w.Length == 2 && double.TryParse(w[0], System.Globalization.CultureInfo.InvariantCulture, out double ws) &&
                    double.TryParse(w[1], System.Globalization.CultureInfo.InvariantCulture, out double wf)) { WindSpeed = ws; WindFrom = wf; }
            }
            else if (a is "--accurate-mode" && i + 1 < args.Length) AccurateModeName = args[++i].ToLowerInvariant();
            else if (a is "--accurate-start" && i + 1 < args.Length) AccurateStart = args[++i].ToLowerInvariant();
            else if (a is "--accurate-average" && i + 1 < args.Length && double.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out double av)) AccurateAverage = av;
            else if (a is "--accurate-settle" && i + 1 < args.Length && double.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out double st)) AccurateSettle = st;
            else if (!a.StartsWith('-')) ModelPath = a;
        }
    }

    /// <summary>"xmin,ymin,zmin,xmax,ymax,zmax" in model metres; null when it does not parse or is inverted.</summary>
    static DomainBox? ParseBox(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 6) return null;
        var v = new float[6];
        for (int i = 0; i < 6; i++)
            if (!float.TryParse(parts[i], System.Globalization.CultureInfo.InvariantCulture, out v[i])) return null;
        var box = new DomainBox(new Vector3(v[0], v[1], v[2]), new Vector3(v[3], v[4], v[5]));
        return box.IsValid ? box : null;
    }

    /// <summary>Repository root (the folder holding WindFlow.sln), so bench output lands in out/ next to the
    /// sandbox's. Falls back to the working directory when the app runs from an installed location.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "WindFlow.sln"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}

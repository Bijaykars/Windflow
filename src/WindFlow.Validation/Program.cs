using System.Globalization;

namespace WindFlow.Validation;

/// <summary>Measured-room validation harness (PLAN_V2 1.4).
/// <code>
/// dotnet run -c Release --project src/WindFlow.Validation -- selftest          metric formulas on synthetic data
/// dotnet run -c Release --project src/WindFlow.Validation -- list              known cases
/// dotnet run -c Release --project src/WindFlow.Validation -- annex20-2d [--slot-cells 4] [--warmup T] [--blocks N]
/// dotnet run -c Release --project src/WindFlow.Validation -- cross-vent [--h-cells 20|40|60] [--opening-model resolved|calibrated]
/// dotnet run -c Release --project src/WindFlow.Validation -- tracer-tb16 [--configs A,B,C,D,E] [--h-cells 40] [--domain guide|smoke] [--wind-tu measured|none]
/// dotnet run -c Release --project src/WindFlow.Validation -- all
/// dotnet run -c Release --project src/WindFlow.Validation -- report            rebuild the VALIDATION.md section only
/// </code>
/// Exit code 0 = ran (metrics may still FAIL their band -- that is a result, not an error), 1 = error or selftest failure.</summary>
public static class Program
{
    static readonly ValidationCase[] Cases = [new Annex20TwoD(), new CrossVentTB15(), new Minibat()];   // minibat-iso [--nozzle-cells 4|5|6]

    [STAThread]
    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length == 0 || args[0] is "-h" or "--help") { Usage(); return 1; }
        string repo = FindRepo();
        var opts = new Dictionary<string, string>();
        for (int i = 1; i + 1 < args.Length; i += 2)
            if (args[i].StartsWith("--")) opts[args[i][2..]] = args[i + 1];

        switch (args[0])
        {
            case "selftest": return Metrics.SelfTest();
            case "list": foreach (var c in Cases) Console.WriteLine($"{c.Id,-14} {c.Title} -- {c.Citation}"); Console.WriteLine($"{"tracer-tb16",-14} {TracerTB16.Title} (own runner, not part of `all`)"); return 0;
            case "report": Report.UpdateValidationMd(repo); Console.WriteLine("VALIDATION.md section rebuilt"); return 0;
        }
        if (Metrics.SelfTest() != 0) return 1;
        if (args[0] == "tracer-tb16") return TracerTB16.Run(repo, opts);
        var run = args[0] == "all" ? Cases : Cases.Where(c => c.Id == args[0]).ToArray();
        if (run.Length == 0) { Console.Error.WriteLine($"unknown case '{args[0]}'"); Usage(); return 1; }

        foreach (var c in run)
        {
            Console.WriteLine($"== {c.Id}: {c.Title}");
            var setup = c.Build(Path.Combine(repo, "data", "validation", c.Id), opts);
            // Diagnostic overrides (a quick look, not a reportable run): --warmup T, --blocks N.
            // --opening-model resolved|calibrated (default: RunOptions' own, Calibrated). Resolved runs get a "-resolved" folder and title suffix.
            if (opts.TryGetValue("opening-model", out var om))
            {
                if (!Enum.TryParse<WindFlow.Core.OpeningModel>(om, true, out var omv)) { Console.Error.WriteLine("--opening-model must be resolved or calibrated"); return 1; }
                setup.Options.OpeningModel = omv;
                if (omv == WindFlow.Core.OpeningModel.Resolved) { setup.OutSuffix += "-resolved"; setup.TitleSuffix += " [opening model: Resolved]"; }
            }
            setup.Info["opening_model"] = setup.Options.OpeningModel.ToString();
            if (opts.TryGetValue("warmup", out var wu)) setup.WarmupT = double.Parse(wu, CultureInfo.InvariantCulture);
            if (opts.TryGetValue("blocks", out var nb)) setup.MinBlocks = setup.MaxBlocks = int.Parse(nb, CultureInfo.InvariantCulture);
            var result = Runner.Run(setup);
            var outDir = Path.Combine(repo, "out", "validation", c.Id + setup.OutSuffix);
            var j = Report.Write(outDir, c, result);
            var m = j["overall"]!;
            Console.WriteLine($"  {c.Id}: FAC2 {m["FAC2"]} (W rule {m["FAC2_W"]}), FB {m["FB"]}, NMSE {m["NMSE"]}, q {m["q"]} -> {((bool)j["pass"]! ? "PASS" : "FAIL")}  " +
                $"({result.WallSeconds / 60:0.0} min, {result.Blocks} blocks, converged {result.Converged})");
            Console.WriteLine($"  wrote {outDir}");
        }
        Report.UpdateValidationMd(repo);
        Console.WriteLine("VALIDATION.md section updated");
        return 0;
    }

    static void Usage() => Console.WriteLine("usage: WindFlow.Validation <selftest | list | report | all | <case> [--slot-cells N] [--h-cells N] [--warmup T] [--blocks N]>\ncases: " +
        string.Join(", ", Cases.Select(c => c.Id)));

    static string FindRepo()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, System.Environment.CurrentDirectory })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "WindFlow.sln"))) return d.FullName;
        return System.Environment.CurrentDirectory;
    }
}

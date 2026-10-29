using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using WindFlow.Core;

namespace WindFlow.Validation;

/// <summary>Tracer-gas ventilation rate of five single-zone cross-ventilated buildings (Tominaga &amp; Blocken 2016, J. Wind Eng. Ind. Aerodyn. 155:74-88,
/// configurations A-E, Table 1). Same 1:100 model as the 2015 case (W = D = 1.25 H, H = 0.16 m, one 0.092 x 0.036 m opening in each of the windward and
/// leeward facades, wind normal to them, power law 0.25, UH = 4.3 m/s) and the same one-cell-wall building (<see cref="CrossVentTB15.MakeBuilding"/>);
/// the configurations differ only in the opening heights (Fig. 2; centres at 40 / 80 / 114 mm, text of Sect. 2.1):
/// A top-top, B top-bottom, C bottom-bottom, D bottom-top, E centre-centre (inlet-outlet, windward-leeward).
/// <para>Measured (Sect. 2.3): constant injection of a tracer just inside the inlet, concentration sampled just upstream of the outlet, 60 s means,
/// Q = q / c, +-7 %. Here: the same, with the online passive scalar of <c>LbmAge.cs</c> (LBM mass-flux advection, resolved unsteady flow, eddy diffusivity)
/// driven by a constant LINE source (the inlet's centre height, ow cells wide, two cells inside the windward wall) and c the time-mean over the cells just inside
/// the leeward opening (two layers, the opening's footprint). Also reported: the throat-plane u.n flux the TB15 harness uses.
/// Pass band (fixed 2026-10-28, ACCURACY_PLAN 6 row V1): each configuration within +-20 % of the measured Q/(UH A_in); A ranked first by the tracer rate.
/// There is deliberately no "B second" clause (B 0.27 and E 0.25 differ by less than the +-7 % on each).</para>
/// <para>NOT modelled: the 8 x 8 mm floor gas outlet (Fig. 1b; sealed here, as in the velocity runs), the tracer tube (0 mm), molecular diffusivity of ethylene
/// (the scalar uses the air value), and the sampling positions of Sect. 2.3 (the paper's text only says "slightly upstream of the outlet"; the layout of
/// positions is not given in the preprint).</para></summary>
public static class TracerTB16
{
    public const string Title = "Tracer-gas ventilation rate, single-zone cross-ventilation A-E (Tominaga-Blocken 2016)";
    public const string Citation = "Tominaga & Blocken 2016, J. Wind Eng. Ind. Aerodyn. 155:74-88 (wind tunnel, constant-injection tracer gas), Table 1";
    const double Hm = 0.16, ReExp = 45000, Band = 0.20;
    const float NuAir = 1.5e-5f, D = 0.1f;

    /// <summary>Opening centre heights (mm at model scale: 40 bottom, 80 centre, 114 top; Sect. 2.1 text) and the measured Q/(UH A_inlet), Table 1.</summary>
    public sealed record Config(char Id, int InZmm, int OutZmm, double Measured)
    {
        public string Layout => $"{Name(InZmm)}-{Name(OutZmm)}";
        static string Name(int z) => z == 40 ? "bottom" : z == 80 ? "centre" : "top";
    }

    public static readonly Config[] Configs =
    [
        new('A', 114, 114, 0.37), new('B', 114, 40, 0.27), new('C', 40, 40, 0.23), new('D', 40, 114, 0.22), new('E', 80, 80, 0.25),
    ];
    const double MeasuredRel = 0.07;

    /// <summary>Q = q / c: injection rate over the time-mean concentration at the outlet (the paper's formula).</summary>
    internal static double Ventilation(double q, double cMean) => q / cMean;

    internal static bool Within(double ratio) => Math.Abs(ratio - 1) <= Band + 1e-9;

    /// <summary>Opening cells and centre heights (cells above the floor) for configuration <paramref name="c"/> at H = <paramref name="n"/> cells.
    /// 0.575 H x 0.225 H (0.092 x 0.036 m of 0.16 m); the width gets the parity of the building width so the carve is symmetric about W/2; an odd
    /// opening height has its centre on a half cell (an odd height centred on a cell face carves one extra row).</summary>
    public readonly record struct Geo(int N, int NW, int OW, int OH, double ZcIn, double ZcOut);

    internal static Geo Geometry(Config c, int n)
    {
        int nW = n * 5 / 4;
        int oh = (int)Math.Round(0.225 * n, MidpointRounding.AwayFromZero);
        int ow = (int)Math.Round(0.575 * n, MidpointRounding.AwayFromZero);
        if ((ow - nW) % 2 != 0) ow++;
        double Zc(int zMm)
        {
            double z = zMm / 160.0 * n;
            return oh % 2 == 1 ? Math.Round(z - 0.5, MidpointRounding.AwayFromZero) + 0.5 : Math.Round(z, MidpointRounding.AwayFromZero);
        }
        return new Geo(n, nW, ow, oh, Zc(c.InZmm), Zc(c.OutZmm));
    }

    /// <summary>Cell sets on a built grid: windward / leeward throat cells, the line-source cells and the sampling cells (all must be non-empty air).</summary>
    internal sealed record Sets(int ThroatIn, int ThroatOut, List<int> Source, List<int> Sample);

    internal static Sets FindSets(VoxelGrid g, BuildingModel model, Geo geo)
    {
        float W = geo.NW * D, zin = (float)geo.ZcIn * D, zout = (float)geo.ZcOut * D, tol = 1e-3f * D;
        List<int> Layer(float wx, float zc, float hz)
        {
            var l = new List<int>();
            int ix = (int)MathF.Floor((wx - g.Origin.X) / g.Dx);
            for (int z = 0; z < g.Nz; z++) for (int y = 0; y < g.Ny; y++)
            {
                var c = g.CellCenter(ix, y, z);
                int i = g.Index(ix, y, z);
                if (g.Cell[i] == VoxelGrid.Air && MathF.Abs(c.Y - W / 2) <= geo.OW * D / 2 - tol && MathF.Abs(c.Z - zc) <= hz) l.Add(i);
            }
            return l;
        }
        // Source: the layer two cells inside the windward wall (x = 2.5 Dx), the 1-2 cell rows touching the inlet's centre height, the opening's width.
        var src = Layer(2.5f * D, zin, 0.5f * D + tol);
        // Sampling: the two layers just inside the leeward wall (W - 1.5 Dx, W - 2.5 Dx), the outlet opening's own footprint.
        var smp = Layer(W - 1.5f * D, zout, geo.OH * D / 2 - tol).Concat(Layer(W - 2.5f * D, zout, geo.OH * D / 2 - tol)).ToList();
        return new Sets(Analysis.ThroatCells(g, model.Openings[0]).Count, Analysis.ThroatCells(g, model.Openings[1]).Count, src, smp);
    }

    /// <summary>Builds one configuration's run. args: h-cells (default 40: H = 4 m, plan V1), domain guide|smoke, wind-tu measured|none, warmup, settle, blocks.</summary>
    public static CaseSetup Build(Config c, Dictionary<string, string> args)
    {
        int n = args.TryGetValue("h-cells", out var hc) ? int.Parse(hc, CultureInfo.InvariantCulture) : 40;
        if (n < 10 || n % 10 != 0) throw new ArgumentException("--h-cells must be a multiple of 10 (>= 10); the plan's V1 run is 40");
        string domainKind = args.TryGetValue("domain", out var dk) ? dk : "guide";
        if (domainKind is not ("guide" or "smoke" or "auto")) throw new ArgumentException("--domain must be guide, auto or smoke");
        string tu = args.TryGetValue("wind-tu", out var wt) ? wt : "measured";
        if (tu is not ("measured" or "none")) throw new ArgumentException("--wind-tu must be measured or none");
        var geo = Geometry(c, n);
        float H = n * D, W = geo.NW * D;
        if (geo.ZcIn - geo.OH / 2.0 < 1 || geo.ZcOut - geo.OH / 2.0 < 1 || Math.Max(geo.ZcIn, geo.ZcOut) + geo.OH / 2.0 > n - 1)
            throw new ArgumentException("an opening does not fit between the floor and the roof cell at this --h-cells");
        float UH = (float)(ReExp * NuAir / H);
        float U10 = UH * MathF.Pow(10f / H, 0.25f);
        double aIn = geo.OW * geo.OH * (double)D * D;
        var model = CrossVentTB15.MakeBuilding($"Tominaga-Blocken 2016 configuration {c.Id} ({c.Layout}), H = {n} cells", n, geo.NW, geo.OW, geo.OH, (float)geo.ZcIn * D, (float)geo.ZcOut * D, false, D);

        var options = new RunOptions { Backend = Backend.Auto, WindProfileExponent = 0.25f, OnlineAge = true };
        if (tu == "measured") options.WindTurbulence = CrossVentTB15.MeasuredTurbulence(H);
        // guide: COST 732 / AIJ best-practice margins (5 H up, 5 H sides, 15 H down, 6 H sky), as the cross-vent default. smoke: a small box for CPU pipeline checks only.
        // auto: the cross-vent --domain auto box at h-cells 40 (its H40 PASS): 4 H upstream, sides and downstream, 4.5 H high. guide at h-cells 40
        // needs 92 M cells (17.1 GB), over a 16 GB GPU.
        if (domainKind == "guide") CrossVentTB15.BoxDomain(options, W, D, 5 * H, 5 * H, 15 * H, 6 * H);
        else if (domainKind == "auto") CrossVentTB15.BoxDomain(options, W, D, 4 * H, 4 * H, 4 * H, 4.5f * H);
        else CrossVentTB15.BoxDomain(options, W, D, 2 * H, 2 * H, 4 * H, 3 * H);

        double Arg(string k, double def) => args.TryGetValue(k, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : def;
        var setup = new CaseSetup
        {
            Model = model, Options = options, Wind = new Vector2(U10, 0), TimeScale = H / UH, ProductRing = true,
            WarmupT = Arg("warmup", 40), BlockT = 10, MinBlocks = 6, MaxBlocks = 12, ConvergeLag = 3, ConvergeTol = 0.005,
        };
        if (args.TryGetValue("blocks", out var nb)) setup.MinBlocks = setup.MaxBlocks = int.Parse(nb, CultureInfo.InvariantCulture);
        double settleT = Arg("settle", 100);
        setup.OutSuffix = (domainKind == "smoke" ? "-smoke" : domainKind == "auto" ? "-auto" : "") + (n != 40 ? $"-h{n}" : "") + (tu == "none" ? "-notu" : "");

        Sets? sets = null;
        const double q = 1.0;   // tracer units per second; the scalar is linear, only q / c matters
        var info = setup.Info;
        info["run_settings"] = $"warmup {setup.WarmupT:0.##}, settle {settleT:0.##}, blocks {(args.TryGetValue("blocks", out var nbs) ? nbs : "auto")}, h-cells {n}, domain {domainKind}";   // table.md merges only config files with the same settings
        info["config"] = c.Id.ToString(); info["layout"] = c.Layout; info["h_cells"] = n; info["H_m"] = (double)H; info["UH_mps"] = (double)UH;
        info["opening_cells"] = $"{geo.OW} wide x {geo.OH} high (paper 0.575 H x 0.225 H = {0.575 * n:0.##} x {0.225 * n:0.##} cells)";
        info["open_centre_in_cells"] = geo.ZcIn; info["open_centre_out_cells"] = geo.ZcOut;
        info["open_centre_in_over_H"] = geo.ZcIn / n; info["open_centre_out_over_H"] = geo.ZcOut / n;
        info["A_in_m2"] = aIn; info["tracer_q"] = q;
        setup.InspectGrid = (g, inf) =>
        {
            if (Math.Abs(g.Dx - D) > 1e-6f) throw new InvalidOperationException($"Dx = {g.Dx}, expected {D}");
            sets = FindSets(g, model, geo);
            int expect = geo.OW * geo.OH;
            inf["grid"] = $"{g.Nx} x {g.Ny} x {g.Nz}"; inf["cells"] = g.Count; inf["Dx_m"] = g.Dx;
            inf["throat_cells_in"] = sets.ThroatIn; inf["throat_cells_out"] = sets.ThroatOut;
            inf["source_cells"] = sets.Source.Count; inf["sample_cells"] = sets.Sample.Count;
            Console.WriteLine($"  config {c.Id} ({c.Layout}): throat cells {sets.ThroatIn} / {sets.ThroatOut} (expect {expect}), source cells {sets.Source.Count}, sample cells {sets.Sample.Count}");
            if (sets.ThroatIn != expect || sets.ThroatOut != expect || sets.Source.Count == 0 || sets.Sample.Count == 0)
                throw new InvalidOperationException("grid does not match the intended geometry (see the counts above)");
        };

        // The scalar starts after the flow warm-up: mask 1 on every air cell (outdoors included: the plume leaves and may be re-entrained through the
        // inlet, as in the open wind tunnel; 0 on the inflow ring), zero initial concentration, a constant source of q split over the line's cells.
        setup.AfterWarmup = (session, g) =>
        {
            var mask = new byte[g.Count];
            for (int i = 0; i < mask.Length; i++) if (g.Cell[i] == VoxelGrid.Air) mask[i] = 1;
            var src = new float[g.Count];
            double cellV = Math.Pow(g.Dx, 3);
            foreach (int i in sets!.Source) src[i] = (float)(q * session.TimeStepSeconds / (cellV * sets.Source.Count));
            session.StartOnlineScalar(new float[g.Count], mask, src);
            session.ResetAveraging(true);   // the scalar only steps while the flow average accumulates
            int steps = (int)Math.Ceiling(settleT * setup.TimeScale / session.TimeStepSeconds);
            Console.WriteLine($"  tracer on: line source {sets.Source.Count} cells, settle {settleT} H/UH = {steps:N0} steps");
            for (int done = 0; done < steps; done += 2000) session.Step(Math.Min(2000, steps - done));
        };

        double Flow(FlowField f, Opening o) { double s = 0; foreach (int i in Analysis.ThroatCells(f.Grid, o)) s += f.Ux[i]; return s * D * D; }
        double Mean(FlowField f)
        {
            if (f.Age.Length == 0) throw new InvalidOperationException("the tracer field is empty (the online scalar is not running), so there is no outlet concentration");
            double s = 0; int k = 0;
            foreach (int i in sets!.Sample) { float a = f.Age[i]; if (float.IsFinite(a)) { s += a; k++; } }
            if (k == 0) throw new InvalidOperationException("no finite tracer concentration at the sampling cells");
            return s / k;
        }
        // Scalar 0 (also the convergence quantity): the block's Q_tracer / (UH A_in). Scalar 1: the block-mean concentration (the headline Q uses the mean of it).
        setup.BlockScalars.Add(("Q_tracer_block_over_UH_A", f => Ventilation(q, Mean(f)) / (UH * aIn)));
        setup.BlockScalars.Add(("c_outlet_mean", Mean));
        setup.BlockScalars.Add(("c_outlet_fluxweighted", f =>
        {
            double s = 0, w = 0;
            foreach (int i in sets!.Sample) { float a = f.Age[i], u = f.Ux[i]; if (float.IsFinite(a) && u > 0) { s += a * u; w += u; } }
            return w > 0 ? s / w : double.NaN;
        }));
        setup.BlockScalars.Add(("Q_in_over_UH_A", f => Flow(f, model.Openings[0]) / (UH * aIn)));
        setup.BlockScalars.Add(("Q_out_over_UH_A", f => Flow(f, model.Openings[1]) / (UH * aIn)));
        setup.FlowSummary = inf => $"tracer {Fmt(inf, "Q_tracer_over_UH_A")} vs measured {c.Measured:0.00}";
        return setup;
    }

    static string Fmt(Dictionary<string, object> inf, string k) => inf.TryGetValue(k, out var v) && v is double d ? d.ToString("0.000", CultureInfo.InvariantCulture) : "?";

    /// <summary>One configuration's result (metrics file config-X.json).</summary>
    public sealed record ConfigResult(string Id, string Layout, double QTracer, double QTracerStdErr, double QVelocityIn, double QVelocityOut, double Measured,
        double Ratio, bool Pass, double CMean, double CFluxWeighted, int Blocks, bool Converged, string Grid, double WallMinutes, string Run);

    public static ConfigResult Result(Config c, CaseRun run)
    {
        var inf = run.Setup.Info;
        double V(string k) => (double)inf[k];
        double uh = V("UH_mps"), aIn = V("A_in_m2");
        double cMean = V("c_outlet_mean");
        double qt = Ventilation(1.0, cMean) / (uh * aIn);
        double rel = V("Q_tracer_block_over_UH_A_stderr") / V("Q_tracer_block_over_UH_A");   // block scatter, carried onto the headline
        double ratio = qt / c.Measured;
        return new ConfigResult(c.Id.ToString(), c.Layout, qt, qt * rel, V("Q_in_over_UH_A"), Math.Abs(V("Q_out_over_UH_A")), c.Measured, ratio, Within(ratio),
            cMean, V("c_outlet_fluxweighted"), run.Blocks, run.Converged, (string)inf["grid"], run.WallSeconds / 60,
            $"{run.Backend}, {run.SimSeconds / run.Setup.TimeScale:0} H/UH, {run.TotalSteps} steps, {run.Mlups:0} MLUPS");
    }

    /// <summary>Markdown table of whatever configurations are given, the within-band count and the A-ranked-first check (n/a unless all five are present).</summary>
    public static string Table(IReadOnlyList<ConfigResult> rs)
    {
        var sb = new StringBuilder();
        string F(double x) => x.ToString("0.000", CultureInfo.InvariantCulture);
        sb.AppendLine("| config | inlet-outlet | Q_tracer/(UH A_in) | Q_velocity/(UH A_in) | measured (+-7 %) | tracer / measured | +-20 % band |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in rs.OrderBy(r => r.Id))
            sb.AppendLine($"| {r.Id} | {r.Layout} | {F(r.QTracer)} (+-{F(r.QTracerStdErr)}) | {F(r.QVelocityIn)} (out {F(r.QVelocityOut)}) | {r.Measured:0.00} | {r.Ratio:0.00} | {(r.Pass ? "PASS" : "FAIL")} |");
        sb.AppendLine();
        sb.AppendLine($"Within +-20 %: {rs.Count(r => r.Pass)} of {rs.Count}.");
        sb.AppendLine($"A ranked first by the tracer rate: {RankedFirst(rs, "A")}.");
        sb.AppendLine($"V1 verdict: {Verdict(rs)}.");
        return sb.ToString();
    }

    internal static string RankedFirst(IReadOnlyList<ConfigResult> rs, string id)
    {
        if (rs.Count < Configs.Length) return "n/a (not all five configurations run)";
        var a = rs.First(r => r.Id == id);
        return rs.All(r => r.Id == id || r.QTracer < a.QTracer) ? "yes" : "NO";
    }

    internal static string Verdict(IReadOnlyList<ConfigResult> rs) =>
        rs.Count < Configs.Length ? "incomplete" : rs.All(r => r.Pass) && RankedFirst(rs, "A") == "yes" ? "PASS" : "FAIL";

    public static int Run(string repo, Dictionary<string, string> opts)
    {
        var wanted = opts.TryGetValue("configs", out var cs) ? cs.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToUpperInvariant()).ToArray()
                                                              : Configs.Select(c => c.Id.ToString()).ToArray();
        if (wanted.Length == 0) { Console.Error.WriteLine("tracer-tb16: --configs is empty; give configurations A-E, e.g. --configs A,B"); return 2; }
        var run = wanted.Select(w => Configs.FirstOrDefault(c => c.Id.ToString() == w) ?? throw new ArgumentException($"unknown configuration '{w}' (A-E)")).ToArray();
        string outDir = null!, currentSettings = "";
        foreach (var c in run)
        {
            Console.WriteLine($"== tracer-tb16 {c.Id}: {Title}");
            var setup = Build(c, opts);
            currentSettings = (string)setup.Info["run_settings"];
            outDir = Path.Combine(repo, "out", "validation", "tracer-tb16" + setup.OutSuffix);
            var r = Runner.Run(setup);
            var res = Result(c, r);
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, $"config-{c.Id}.json"), JsonSerializer.Serialize(new { result = res, info = setup.Info }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  {c.Id}: Q_tracer {res.QTracer:0.000} Q_velocity {res.QVelocityIn:0.000} measured {c.Measured:0.00} ratio {res.Ratio:0.00} -> {(res.Pass ? "PASS" : "FAIL")} " +
                $"({res.WallMinutes:0.0} min, {res.Blocks} blocks, converged {res.Converged})");
        }
        // The table covers every configuration file in the folder that was run with the same settings as this run, so configurations run in separate invocations still add up.
        var all = new List<ConfigResult>();
        foreach (var f in Directory.GetFiles(outDir, "config-*.json"))
            using (var doc = JsonDocument.Parse(File.ReadAllText(f)))
            {
                string? theirs = doc.RootElement.TryGetProperty("info", out var inf) && inf.TryGetProperty("run_settings", out var rsv) ? rsv.GetString() : null;
                if (theirs == currentSettings) all.Add(doc.RootElement.GetProperty("result").Deserialize<ConfigResult>()!);
                else Console.WriteLine($"  {Path.GetFileName(f)} left out of the table: run settings differ ({theirs ?? "not recorded"} vs {currentSettings})");
            }
        var table = Table(all);
        File.WriteAllText(Path.Combine(outDir, "table.md"), table);
        Console.WriteLine(table);
        Console.WriteLine($"  wrote {outDir} (VALIDATION.md not touched)");
        return 0;
    }

    /// <summary>Q = q / c arithmetic, the pass logic and, on a real voxelised grid at H = 10 cells, the five opening layouts.</summary>
    internal static void SelfTest(Action<string, double, double> check)
    {
        check("tracer Q = q / c", Ventilation(2.0, 8.0), 0.25);
        double uh = 4.3, a = 3.3e-3;
        check("tracer Q/(UH A) from q, c", Ventilation(0.002, 0.01) / (uh * a), (0.002 / 0.01) / (uh * a));
        check("band: +19 % in", Within(1.19) ? 1 : 0, 1);
        check("band: +21 % out", Within(1.21) ? 1 : 0, 0);
        check("band: -19.9 % in", Within(0.801) ? 1 : 0, 1);
        check("band: -21 % out", Within(0.79) ? 1 : 0, 0);
        ConfigResult R(string id, double q) => new(id, "", q, 0, 0, 0, 0.3, 0, true, 0, 0, 0, true, "", 0, "");
        var five = Configs.Select(c => R(c.Id.ToString(), c.Id == 'A' ? 0.40 : 0.30)).ToList();
        check("A ranked first: A highest", RankedFirst(five, "A") == "yes" ? 1 : 0, 1);
        five[2] = R("C", 0.41);
        check("A ranked first: C above A", RankedFirst(five, "A") == "NO" ? 1 : 0, 1);
        check("A ranked first: fewer than five is n/a", RankedFirst(five.Take(3).ToList(), "A").StartsWith("n/a") ? 1 : 0, 1);

        // Geometry at the plan's H = 40: 24 x 9 cells (23 widened to the building width's parity), centres 28.5 / 20.5 / 10.5 cells (114 / 80 / 40 mm).
        var g40 = Configs.Select(c => Geometry(c, 40)).ToArray();
        check("n=40 opening height cells", g40.Select(g => g.OH).Distinct().Single(), 9);
        check("n=40 opening width cells", g40.Select(g => g.OW).Distinct().Single(), 24);
        double[] wantIn = [28.5, 28.5, 10.5, 10.5, 20.5], wantOut = [28.5, 10.5, 10.5, 28.5, 20.5];
        for (int i = 0; i < Configs.Length; i++)
        {
            check($"n=40 config {Configs[i].Id} inlet centre cells", g40[i].ZcIn, wantIn[i]);
            check($"n=40 config {Configs[i].Id} outlet centre cells", g40[i].ZcOut, wantOut[i]);
        }

        // Real grid at H = 10 cells in a small box: each config carves exactly ow x oh cells in both facades; the source and sampling cells are air.
        foreach (var c in Configs)
        {
            var geo = Geometry(c, 10);
            float H = geo.N * D, W = geo.NW * D;
            var model = CrossVentTB15.MakeBuilding("selftest", geo.N, geo.NW, geo.OW, geo.OH, (float)geo.ZcIn * D, (float)geo.ZcOut * D, false, D);
            var o = new RunOptions();
            CrossVentTB15.BoxDomain(o, W, D, 2 * H, 2 * H, 3 * H, 3 * H);
            var grid = Voxelizer.Build(model, o.TargetCells!.Value, null, o.Domain, minDx: 0.1f);
            var s = FindSets(grid, model, geo);
            int expect = geo.OW * geo.OH;
            check($"config {c.Id} (H=10) windward throat cells = ow x oh", s.ThroatIn, expect);
            check($"config {c.Id} (H=10) leeward throat cells = ow x oh", s.ThroatOut, expect);
            check($"config {c.Id} (H=10) source cells = ow x (1-2 rows)", s.Source.Count, geo.OW * (geo.OH % 2 == 1 ? 1 : 2));
            check($"config {c.Id} (H=10) sample cells = 2 layers x opening footprint", s.Sample.Count, 2 * expect);
        }
    }
}

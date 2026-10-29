using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindFlow.Validation;

/// <summary>Writes out/validation/&lt;case&gt;/ (metrics.json, one PNG per profile) and regenerates the
/// "Measured-room cases" section of VALIDATION.md from every metrics.json found under out/validation.</summary>
public static class Report
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
    static string F(double v, string fmt = "0.000") => double.IsFinite(v) ? v.ToString(fmt, CultureInfo.InvariantCulture) : "n/a";

    public static JsonObject Write(string outDir, ValidationCase c, CaseRun run)
    {
        Directory.CreateDirectory(outDir);
        var s = run.Setup;
        var allO = new List<double>(); var allP = new List<double>();
        var lines = new JsonArray();
        var lineBreakdown = new List<string>();
        var regionObs = new Dictionary<string, List<double>>();
        var regionPred = new Dictionary<string, List<double>>();
        for (int li = 0; li < s.Lines.Count; li++)
        {
            var line = s.Lines[li];
            var obs = line.Measured.Select(m => m.Obs).ToList();
            var pred = run.PredMeasured[li].ToList();
            if (line.InOverall) { allO.AddRange(obs); allP.AddRange(pred); }
            var m = Metrics.Compute(obs, pred, s.W);
            var qb = Metrics.QBreakdown(obs, pred, s.W);
            lineBreakdown.Add($"{line.Name} q {F(m.Q, "0.00")} ({qb.Miss} of {qb.N} miss: {qb.SignMiss} sign, {qb.MagMiss} magnitude)");
            if (s.RegionOf != null)
                for (int k = 0; k < obs.Count; k++)
                {
                    string reg = s.RegionOf(li, line.Measured[k]);
                    if (!regionObs.TryGetValue(reg, out var lo)) regionObs[reg] = lo = new();
                    if (!regionPred.TryGetValue(reg, out var lp)) regionPred[reg] = lp = new();
                    lo.Add(obs[k]); lp.Add(pred[k]);
                }
            var pts = new JsonArray();
            for (int k = 0; k < obs.Count; k++)
                pts.Add(new JsonObject { ["pos"] = line.Measured[k].Pos, ["obs"] = obs[k], ["bar"] = Num(line.Measured[k].Bar), ["pred"] = Num(pred[k]) });
            string png = $"{Safe(line.Name)}.png";
            string sub = $"{c.Title}{s.TitleSuffix}. FAC2 {F(m.Fac2, "0.00")} (with W rule {F(m.Fac2W, "0.00")}), FB {F(m.Fb, "0.00")}, NMSE {F(m.Nmse, "0.00")}, q {F(m.Q, "0.00")} (D 0.25, W {s.W}). Dx {run.Grid.Dx * 1000:0} mm, {run.Blocks} averaging blocks.";
            Plot.Profile(Path.Combine(outDir, png), line, run.PredMeasured[li], run.PredDense[li], sub);
            lines.Add(new JsonObject { ["name"] = line.Name, ["in_overall"] = line.InOverall, ["png"] = png, ["metrics"] = MetricsNode(m), ["points"] = pts });
        }
        var all = Metrics.Compute(allO, allP, s.W);
        if (run.Slice != null)
            Plot.Slice(Path.Combine(outDir, "field.png"), run.Slice, s.SliceScale, s.SliceMax01, s.SliceTitle + s.TitleSuffix,
                $"mean of {run.Blocks} blocks, sampled every {run.SliceDx * 1000:0} mm; Dx {run.Grid.Dx * 1000:0} mm", s.SliceUnit);
        var root = new JsonObject
        {
            ["case"] = c.Id + s.OutSuffix, ["title"] = c.Title + s.TitleSuffix, ["citation"] = c.Citation, ["quantities"] = c.Quantities,
            ["date"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["band"] = Metrics.Band, ["D"] = Metrics.D, ["W"] = s.W, ["W_note"] = s.WNote,
            ["overall"] = MetricsNode(all), ["pass"] = Metrics.Passes(all),
            ["flow_error"] = s.FlowSummary != null ? s.FlowSummary(s.Info) : s.Info.TryGetValue("outlet_over_supply", out var oos) && oos is double r
                ? $"no measured flow; outlet carries {(r - 1) * 100:+0;-0}% vs the nominal supply" : "n/a (no measured flow rate)",
            ["run"] = new JsonObject
            {
                ["backend"] = run.Backend, ["dt_s"] = run.Dt, ["warmup_steps"] = run.WarmupSteps, ["total_steps"] = run.TotalSteps,
                ["sim_seconds"] = run.SimSeconds, ["dimensionless_time"] = run.SimSeconds / s.TimeScale,
                ["warmup_T"] = s.WarmupT, ["block_T"] = s.BlockT, ["blocks"] = run.Blocks, ["converged"] = run.Converged,
                ["converge_rule"] = $"RMS change of the cumulative block-mean compared quantity at the measured points over the last {s.ConvergeLag} blocks < {s.ConvergeTol}, >= {s.MinBlocks} blocks",
                ["convergence_history"] = new JsonArray(run.ConvergenceHistory.Select(v => (JsonNode?)Num(v)).ToArray()),
                ["max_block_stderr"] = run.MaxStdErr, ["wall_seconds"] = run.WallSeconds, ["last_mlups"] = run.Mlups,
            },
            ["setup"] = JsonSerializer.SerializeToNode(s.Info, Json),
            ["lines"] = lines,
        };
        if (s.Note != null) root["note"] = s.Note(run);
        if (allO.Count > 0)
        {
            var overallQb = Metrics.QBreakdown(allO, allP, s.W);
            var qbLines = new List<string>
            {
                $"q (hit-rate) misses: {overallQb.Miss} of {overallQb.N} points overall, {overallQb.SignMiss} sign errors (opposite-sign O/P outside W), {overallQb.MagMiss} magnitude errors.",
                "By profile: " + string.Join("; ", lineBreakdown) + ".",
            };
            if (regionObs.Count > 0)
            {
                var regionStats = regionObs.Keys.OrderByDescending(k => regionObs[k].Count).Select(k =>
                {
                    var ro = regionObs[k]; var rp = regionPred[k];
                    var rm = Metrics.Compute(ro, rp, s.W);
                    var rb = Metrics.QBreakdown(ro, rp, s.W);
                    double bias = ro.Sum(Math.Abs) > 0 ? (rp.Sum(Math.Abs) - ro.Sum(Math.Abs)) / ro.Sum(Math.Abs) : double.NaN;
                    return (k, rb, text: $"{k} (n={rb.N}): FAC2 {F(rm.Fac2, "0.00")}, q {F(rm.Q, "0.00")} ({rb.Miss} miss: {rb.SignMiss} sign, {rb.MagMiss} magnitude), " +
                        $"mean|pred| {(double.IsFinite(bias) ? $"{bias * 100:+0;-0}% vs mean|obs|" : "n/a")}");
                }).ToList();
                qbLines.Add("By region: " + string.Join("; ", regionStats.Select(r => r.text)) + ".");
                var worst = regionStats.Where(r => r.rb.Miss > 0).OrderByDescending(r => r.rb.Miss).FirstOrDefault();
                if (worst.k != null) qbLines.Add($"Misses concentrate in {worst.k}, carrying {worst.rb.Miss} of the {overallQb.Miss} overall q misses.");
            }
            root["q_breakdown"] = string.Join(" ", qbLines);
        }
        File.WriteAllText(Path.Combine(outDir, "metrics.json"), root.ToJsonString(Json));
        return root;
    }

    static JsonNode? Num(double v) => double.IsFinite(v) ? JsonValue.Create(v) : null;
    static JsonObject MetricsNode(Metrics.Result m) => new()
    { ["n"] = m.N, ["FAC2"] = Num(m.Fac2), ["FAC2_W"] = Num(m.Fac2W), ["FB"] = Num(m.Fb), ["NMSE"] = Num(m.Nmse), ["q"] = Num(m.Q), ["pass"] = Metrics.Passes(m) };
    static string Safe(string s) => new(s.Select(ch => char.IsLetterOrDigit(ch) ? ch : ch == '=' ? '_' : '-').ToArray());

    const string Begin = "<!-- measured-room-cases:begin (generated by src/WindFlow.Validation, do not edit by hand) -->";
    const string End = "<!-- measured-room-cases:end -->";

    /// <summary>Regenerates the section between the markers (inserted before "## 3. How to reproduce" the first time).</summary>
    public static void UpdateValidationMd(string repo)
    {
        string md = Path.Combine(repo, "VALIDATION.md");
        var sb = new StringBuilder();
        sb.AppendLine(Begin);
        sb.AppendLine("## Measured-room cases");
        sb.AppendLine();
        sb.AppendLine("Comparison against measured rooms, run by `src/WindFlow.Validation`. Data, citations and terms of use are in");
        sb.AppendLine("`data/validation/<case>/SOURCE.md`; data still needed from the user is listed in `data/validation/NEEDED.md`.");
        sb.AppendLine("These are **baseline** numbers on the solver as it is today (before the PLAN_V2 1.1-1.3 accuracy work);");
        sb.AppendLine("nothing on this page may be read as \"validated against measured rooms\" until PLAN_V2 gate 1.5 passes.");
        sb.AppendLine();
        sb.AppendLine("| Case | Opening model | Source | Quantities | Points | FAC2 | FAC2 (W rule) | FB | NMSE | q | Flow error | Band | Result | Grid Dx | Run time |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        var dir = Path.Combine(repo, "out", "validation");
        var notes = new List<string>();
        var diagDir = Path.Combine(dir, "cross-vent-diag");
        var allFiles = (Directory.Exists(dir) ? Directory.GetFiles(dir, "metrics.json", SearchOption.AllDirectories) : [])
            .Where(x => !x.StartsWith(diagDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList();
        // "-pre-f1" folders are the same case run before the F1 link-wise supply inlet landed: keep them out of the
        // table as their own rows (they used to duplicate the case's title) and instead fold each into a single
        // "before F1" comparison row on its matching case, keyed by folder name minus the "-pre-f1" suffix.
        const string PreF1Suffix = "-pre-f1";
        string FolderName(string f) => Path.GetFileName(Path.GetDirectoryName(f))!;
        var preF1 = allFiles.Where(f => FolderName(f).EndsWith(PreF1Suffix, StringComparison.Ordinal))
            .ToDictionary(f => FolderName(f)[..^PreF1Suffix.Length], f => JsonNode.Parse(File.ReadAllText(f))!.AsObject());
        foreach (var f in allFiles.Where(f => !FolderName(f).EndsWith(PreF1Suffix, StringComparison.Ordinal))
                     .OrderBy(x => FolderName(x), StringComparer.Ordinal))
        {
            var j = JsonNode.Parse(File.ReadAllText(f))!.AsObject();
            string dx = j["setup"]?["Dx_m"] is JsonNode dn ? $"{dn.GetValue<double>() * 1000:0} mm" : "?";
            string cells = j["setup"]?["cells"] is JsonNode cn ? $", {cn.GetValue<double>() / 1e6:0.00} M cells" : "";
            double wall = j["run"]!["wall_seconds"]!.GetValue<double>();
            string time = $"{wall / 60:0.0} min";
            // Runs older than the RunOptions.OpeningModel switch had only the resolved-throat model.
            string om = j["setup"]?["opening_model"]?.ToString() is { } omn ? omn : "Resolved (run predates the option)";
            void Row(string label, JsonNode m, bool bold)
            {
                string B(string x) => bold ? $"**{x}**" : x;
                sb.AppendLine($"| {B(label)} | {(bold ? om : "")} | {(bold ? j["citation"] : "")} | {(bold ? j["quantities"] : "")} | {m["n"]} | {(m["FAC2_W"] != null ? Cell(m["FAC2"]) : "re-run pending")} | {Cell(m["FAC2_W"] ?? m["FAC2"])} | {Cell(m["FB"])} | {Cell(m["NMSE"])} | {Cell(m["q"])} | " +
                    $"{(bold ? j["flow_error"] : "")} | {(bold ? Metrics.Band.Replace("|", @"\|") : "")} | {B(m["pass"]!.GetValue<bool>() ? "PASS" : "FAIL")} | {(bold ? dx + cells : "")} | {(bold ? time : "")} |");
            }
            Row($"{j["title"]} -- all points", j["overall"]!, true);
            foreach (var l in j["lines"]!.AsArray())
                Row($"&nbsp;&nbsp;line {l!["name"]}{(l["in_overall"]?.GetValue<bool>() == false ? " (not in the overall)" : "")}", l["metrics"]!, false);
            if (preF1.TryGetValue(FolderName(f), out var pj))
                Row("&nbsp;&nbsp;before F1 (link-wise supply inlet)", pj["overall"]!, false);

            if (j["note"] is JsonNode note)
            {
                notes.Add(note.GetValue<string>());
                if (j["q_breakdown"] is JsonNode qb0) notes.Add("  - q breakdown: " + qb0.GetValue<string>());
                if (j["case"]?.GetValue<string>() == "cross-vent") notes.AddRange(CrossVentDiag(repo, diagDir));
                continue;
            }
            var st = j["setup"]!; var r = j["run"]!;
            notes.Add($"- **{j["title"]}** (run {j["date"]}, {r["backend"]}): {st["scale"]}. Discretised geometry L/H = {G(st["L_over_H"])}, " +
                $"h/H = {G(st["h_over_H"], "0.0000")}, t/H = {G(st["t_over_H"], "0.0000")}, W/H = {G(st["W_over_H"])}; {st["cells_across_slot"]} cells across the supply slot; " +
                $"grid {st["grid"]}; compared on the {st["plane"]}. Inlet: {st["inlet"]}, u0 = {G(st["u0_mps"])} m/s; outlet: open Opening to an " +
                $"outdoor plenum, zero wind. Lattice viscosity is held at the solver floor (NuLatMin), so the effective Reynolds number " +
                $"before the Smagorinsky term is about {G(st["Re_effective_before_LES"], "0")}, not 5000. Run: warm-up {r["warmup_T"]} H/u0, then " +
                $"{r["blocks"]} blocks of {r["block_T"]} H/u0 ({G(r["dimensionless_time"], "0")} H/u0 in total, {r["total_steps"]} steps); " +
                $"{(r["converged"]!.GetValue<bool>() ? "converged" : "NOT converged")} by the rule: {r["converge_rule"]}; largest block-mean standard error " +
                $"{G(r["max_block_stderr"])} u0. Outlet/supply flow in the last block: {G(st["outlet_over_supply"])}{PlaneRange(st["plane_flux_over_supply"])}. {j["W_note"]}. Plots: `out/validation/{j["case"]}/*.png`.");
            if (j["q_breakdown"] is JsonNode qb1) notes.Add("  - q breakdown: " + qb1.GetValue<string>());
        }
        sb.AppendLine();
        sb.AppendLine("Metrics: FAC2, FB and NMSE after Chang & Hanna (2004); hit rate q after VDI 3783-9 / COST 732 with D = 0.25 and an");
        sb.AppendLine("absolute tolerance W per case. FAC2 is the plain fraction with 0.5 <= P/O <= 2 (van Hooff et al. 2017 eq. 7; R2-03); the column FAC2 (W rule) also counts pairs with both |O| and |P| below W as hits (Hanna & Chang 2012), which is what earlier runs reported as FAC2. Velocity");
        sb.AppendLine("components change sign, so FB uses magnitudes (2(mean|O| - mean|P|)/(mean|O| + mean|P|)) and NMSE divides the signed");
        sb.AppendLine("squared error by mean|O| x mean|P|; FAC2 and q use signed values (opposite signs miss; for q and the W-rule FAC2 unless both are inside W).");
        sb.AppendLine("Band: the PLAN_V2 criteria; NMSE <= 1.5 is Chang & Hanna's acceptance value.");
        sb.AppendLine();
        foreach (var n in notes) sb.AppendLine(n);
        sb.AppendLine();
        sb.AppendLine("Reproduce: `dotnet run -c Release --project src/WindFlow.Validation -- all` (or `-- <case>`, `-- selftest`, `-- list`).");
        sb.AppendLine(End);

        string text = File.ReadAllText(md);
        int b = text.IndexOf(Begin, StringComparison.Ordinal), e = text.IndexOf(End, StringComparison.Ordinal);
        string section = sb.ToString().Replace("\r\n", "\n");
        if (b >= 0 && e > b) text = text[..b] + section.TrimEnd('\n') + text[(e + End.Length)..];
        else
        {
            int at = text.IndexOf("## 3. How to reproduce", StringComparison.Ordinal);
            text = at >= 0 ? text[..at] + section + "\n" + text[at..] : text.TrimEnd() + "\n\n" + section;
        }
        File.WriteAllText(md, text);
    }

    /// <summary>The cross-ventilation diagnostic runs (out/validation/cross-vent-diag/&lt;run&gt;/metrics.json) as one
    /// table, written to table.md / table.csv there and returned as VALIDATION.md lines, followed by the
    /// hand-written reading in data/validation/cross-vent/diagnosis.md. Cd envelope = Q/(UH A) / sqrt(dCp_sealed / 2)
    /// with dCp_sealed the patch dCp of the sealed run on the same grid and domain.</summary>
    static List<string> CrossVentDiag(string repo, string diagDir)
    {
        var outp = new List<string>();
        if (!Directory.Exists(diagDir)) return outp;
        var runs = Directory.GetFiles(diagDir, "metrics.json", SearchOption.AllDirectories)
            .Select(f => (name: Path.GetFileName(Path.GetDirectoryName(f))!, j: JsonNode.Parse(File.ReadAllText(f))!.AsObject()))
            .OrderBy(r => r.name, StringComparer.Ordinal).ToList();
        if (runs.Count == 0) return outp;
        static double D(JsonNode? n) => n == null ? double.NaN : n.GetValueKind() == JsonValueKind.Number ? n.GetValue<double>()
            : double.TryParse(n.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
        string Key(JsonNode st) => $"{st["diag_domain"]}-n{st["h_cells"]}-oh{st["open_h_cells"]}";
        var sealedDcp = runs.Where(r => r.j["setup"]!["diag_sealed"]!.GetValue<bool>())
            .ToDictionary(r => Key(r.j["setup"]!), r => D(r.j["setup"]!["dCp_patch"]));
        string[] head = ["run", "domain", "blockage", "H cells", "opening h x w cells", "sealed", "Cp ww centre", "Cp ww patch", "Cp lw centre", "Cp lw patch",
            "Cp room", "dCp patch", "Cd windward", "Cd leeward", "Cd series", "Cd envelope", "Q/(UH A)", "FAC2", "FB", "wall min"];
        var rows = new List<string[]>();
        foreach (var (name, j) in runs)
        {
            var st = j["setup"]!; var ov = j["overall"]!;
            bool sealedRun = st["diag_sealed"]!.GetValue<bool>();
            double q = D(st["Q_in_over_UH_A"]);
            double env = !sealedRun && sealedDcp.TryGetValue(Key(st), out var sd) && sd > 0 ? q / Math.Sqrt(sd / 2) : double.NaN;
            rows.Add([name, st["diag_domain"]!.ToString() == "guide" ? "guideline" : "4 H", F(D(st["blockage"]), "0.0%"), st["h_cells"]!.ToString(), $"{st["open_h_cells"]} x {st["open_w_cells"]}",
                sealedRun ? "yes" : "no", F(D(st["Cp_ww_centre"])), F(D(st["Cp_ww_patch"])), F(D(st["Cp_lw_centre"])), F(D(st["Cp_lw_patch"])),
                F(D(st["Cp_room"])), F(D(st["dCp_patch"])), F(D(st["Cd_windward"])), F(D(st["Cd_leeward"])), F(D(st["Cd_series"])), F(env),
                sealedRun ? "n/a" : F(q), F(D(ov["FAC2"]), "0.00"), F(D(ov["FB"]), "0.00"), F(D(j["run"]!["wall_seconds"]) / 60, "0.0")]);
        }
        var md = new List<string>
        {
            "",
            "  Cross-ventilation diagnostics (`out/validation/cross-vent-diag/table.md`; Cp = (P - P_ref)/(0.5 rho UH^2), P_ref the approach static",
            "  pressure on 5 x 5 cells at x = -3 H, z = H; centre = the cells touching the opening centre, patch = the opening footprint, both 1-2 cells",
            "  outside the facade; Cd from Q/(UH A) = Cd sqrt(dCp) per opening (outside patch vs room mean) and Cd sqrt(dCp/2) in series;",
            "  Cd envelope uses the sealed-body dCp of the same domain; FAC2/FB of the sealed runs compare a shut room and mean nothing):",
            "",
            "  | " + string.Join(" | ", head) + " |",
            "  |" + string.Concat(Enumerable.Repeat("---|", head.Length)),
        };
        md.AddRange(rows.Select(r => "  | " + string.Join(" | ", r) + " |"));
        Directory.CreateDirectory(diagDir);
        File.WriteAllLines(Path.Combine(diagDir, "table.md"), md.Skip(6).Select(l => l.TrimStart()));
        File.WriteAllLines(Path.Combine(diagDir, "table.csv"), new[] { string.Join(",", head) }.Concat(rows.Select(r => string.Join(",", r.Select(c => c.Contains(',') ? $"\"{c}\"" : c)))));
        string reading = Path.Combine(repo, "data", "validation", "cross-vent", "diagnosis.md");
        if (File.Exists(reading)) { md.Add(""); md.AddRange(File.ReadAllLines(reading).Select(l => l.Length > 0 ? "  " + l : l)); }
        return md;
    }

    static string PlaneRange(JsonNode? planes)
    {
        if (planes is not JsonObject o || o.Count == 0) return "";
        var v = o.Select(kv => kv.Value!.GetValue<double>()).ToArray();
        return $" (room cross-sections {v.Min():0.000}-{v.Max():0.000}: the deficit is at the supply patch, not a leak)";
    }

    static string Cell(JsonNode? n) => n == null ? "n/a" : F(n.GetValue<double>(), "0.00");
    static string G(JsonNode? n, string fmt = "0.###") => n == null ? "?" : F(n.GetValue<double>(), fmt);
}

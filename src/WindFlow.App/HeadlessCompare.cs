using System.Globalization;
using System.Numerics;
using System.Text;
using WindFlow.Core;
using WindFlow.Report;

namespace WindFlow.App;

/// <summary>One metric of one room (or the building) over the offset pairs.</summary>
public sealed record PairStat(int N, double MeanA, double MeanB, double MeanDeltaPct, double MinDeltaPct, double MaxDeltaPct, int Wins, string Verdict);

/// <summary>Pure aggregation and the offset set of --compare, so --selfcheck can test it without a solve.</summary>
public static class PairedStats
{
    public const double VerdictPct = 5;

    /// <summary>Sub-cell grid offsets in cell units (x, y only: Voxelizer.OriginShiftCells has no z). The first four are stratified, the rest fill in.</summary>
    public static readonly Vector2[] Offsets =
    [
        new(0, 0), new(.5f, .5f), new(.5f, 0), new(0, .5f), new(.25f, .75f), new(.75f, .25f), new(.25f, .25f), new(.75f, .75f),
    ];

    /// <summary>Pairs (A, B) from the same grid offset. A pair with A not > 0 or a non-finite value is left out. Verdict: "better" / "worse" when every
    /// pair has the same sign and |mean delta| is at least <see cref="VerdictPct"/>, else "unclear" ("n/a" with no usable pair).</summary>
    public static PairStat Aggregate(IReadOnlyList<(double A, double B)> pairs)
    {
        var p = pairs.Where(x => double.IsFinite(x.A) && double.IsFinite(x.B) && x.A > 0).ToList();
        if (p.Count == 0) return new PairStat(0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, 0, "n/a");
        var d = p.Select(x => (x.B - x.A) / x.A * 100).ToList();
        int wins = p.Count(x => x.B > x.A), losses = p.Count(x => x.B < x.A);
        double mean = d.Average();
        string v = (wins == p.Count || losses == p.Count) && Math.Abs(mean) >= VerdictPct ? (mean > 0 ? "better" : "worse") : "unclear";
        return new PairStat(p.Count, p.Average(x => x.A), p.Average(x => x.B), mean, d.Min(), d.Max(), wins, v);
    }
}

/// <summary>--compare A.windflow.json B.windflow.json [--offsets K] [--compare-out file] [the --solve options]: the same K grid offsets for both designs,
/// A then B at each, and per room the difference B - A with its spread. Grid placement moves one room by up to ~35 %, mostly the same for both designs,
/// so it partly cancels in the pairwise difference (measured 2026-10-28, sample house, 4 offsets, 5 min runs: paired spread 4-91 % of the
/// unpaired spread, median about 60 %; the sign held in every pair where |delta| > 15 %; audits/2026-10-28-fixes/gpu-q1b/paired-analysis.txt).</summary>
public static class HeadlessCompare
{
    sealed record RunVals(string Id, string Name, double Fresh, double Total, double Volume, bool SteadyAge);
    sealed record Run1(List<RunVals> Rooms, double Fresh, double Total, float Dx, Vector3 Origin, string Backend)
    {
        /// <summary>The age estimator of the whole run: "online", "steady" or "mixed" (Standard: some rooms landed the online age, some the steady one).</summary>
        public string AgeMethod => Rooms.All(r => r.SteadyAge) && Rooms.Count > 0 ? "steady" : Rooms.Any(r => r.SteadyAge) ? "mixed" : "online";
        public bool SameGrid(Run1 o) => Math.Abs(Dx - o.Dx) <= 1e-6f * Dx && (Origin - o.Origin).Length() <= 1e-3f * Dx && Backend == o.Backend;
    }

    public static int Run()
    {
        if (Options.CompareA == null || Options.CompareB == null) { Console.Error.WriteLine("--compare: expected two model paths: --compare A.windflow.json B.windflow.json"); return 2; }
        int k = Options.Offsets;
        if (k < 2 || k > PairedStats.Offsets.Length) { Console.Error.WriteLine($"--offsets {Options.OffsetsText}: expected a whole number from 2 to {PairedStats.Offsets.Length}"); return 2; }
        if (Options.DomainName == "full") { Console.Error.WriteLine("--compare: --domain full is not supported (the offsets only shift the automatic grid, which the full domain does not use)"); return 2; }
        if (Options.QualityName == "high") { Console.Error.WriteLine("--compare: --quality high is not supported (High plans its own grid and box, so the offsets would not apply)"); return 2; }
        BuildingModel a, b;
        try
        {
            a = ModelValidation.Load(Options.CompareA!, Import.ImportGuard.MaxJsonBytes);
            b = ModelValidation.Load(Options.CompareB!, Import.ImportGuard.MaxJsonBytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Console.Error.WriteLine($"--compare: cannot use the models: {ex.Message}");
            return 2;
        }
        var runsA = new List<Run1>(); var runsB = new List<Run1>();
        var saved = Voxelizer.OriginShiftCells;
        try
        {
            for (int i = 0; i < k; i++)
            {
                Voxelizer.OriginShiftCells = PairedStats.Offsets[i];
                Console.WriteLine($"offset {i + 1}/{k}: ({PairedStats.Offsets[i].X:0.##}, {PairedStats.Offsets[i].Y:0.##}) cells");
                foreach (var (model, list, tag) in new[] { (a, runsA, "A"), (b, runsB, "B") })
                {
                    var cap = new HeadlessSolve.Capture();
                    int rc = HeadlessSolve.Solve(model, Options.QualityName, cap);
                    if (rc != 0 || cap.Result == null) { Console.Error.WriteLine($"--compare: design {tag} at offset {i + 1} did not solve (exit {rc})"); return rc != 0 ? rc : 2; }
                    var run = Extract(cap.Result);
                    list.Add(run);
                    Console.WriteLine($"  {tag}: Dx {run.Dx * 1000:0.##} mm · origin ({run.Origin.X:0.###}, {run.Origin.Y:0.###}, {run.Origin.Z:0.###}) m · backend {run.Backend} · age {run.AgeMethod}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"--compare: cannot solve: {ex.Message}");
            return 2;
        }
        finally { Voxelizer.OriginShiftCells = saved; }

        // Match rooms by Id, else by (unique) Name; the rest are listed apart.
        var roomsA = runsA[0].Rooms; var roomsB = runsB[0].Rooms;
        var matched = new List<(RunVals A, RunVals B)>(); var onlyA = roomsA.ToList(); var onlyB = new List<RunVals>();
        foreach (var rb in roomsB)
        {
            var ra = onlyA.FirstOrDefault(x => x.Id == rb.Id) ?? (onlyA.Count(x => x.Name == rb.Name) == 1 && roomsB.Count(x => x.Name == rb.Name) == 1 ? onlyA.First(x => x.Name == rb.Name) : null);
            if (ra == null) onlyB.Add(rb); else { matched.Add((ra, rb)); onlyA.Remove(ra); }
        }

        // A pair is usable only when A and B ran on the same Dx, grid origin and backend; fresh-air ACH also needs the same age estimator in the room.
        var gridOk = Enumerable.Range(0, k).Select(i => runsA[i].SameGrid(runsB[i])).ToArray();
        int gridDropped = gridOk.Count(x => !x);
        var notes = new List<string>();
        int methodTotal = 0, missingTotal = 0;
        if (gridDropped > 0) notes.Add($"{gridDropped} of {k} pairs dropped: A and B differ in Dx, grid origin or backend");
        const double NaN = double.NaN;
        var rows = new List<(string Name, string Metric, PairStat S)>();
        (RunVals? A, RunVals? B) RoomPair(int i, RunVals ra, RunVals rb) => (runsA[i].Rooms.FirstOrDefault(x => x.Id == ra.Id), runsB[i].Rooms.FirstOrDefault(x => x.Id == rb.Id));
        PairStat RoomStat(RunVals ra, RunVals rb, Func<RunVals, double> f, bool fresh) =>
            PairedStats.Aggregate(Enumerable.Range(0, k).Select(i =>
            {
                var (xa, xb) = RoomPair(i, ra, rb);
                if (!gridOk[i] || xa == null || xb == null || (fresh && xa.SteadyAge != xb.SteadyAge)) return (NaN, NaN);
                return (f(xa), f(xb));
            }).ToList());
        foreach (var (ra, rb) in matched)
        {
            string n = ra.Name == rb.Name ? ra.Name : $"{ra.Name} / {rb.Name}";
            int missing = Enumerable.Range(0, k).Count(i => gridOk[i] && (RoomPair(i, ra, rb).A == null || RoomPair(i, ra, rb).B == null));
            int method = Enumerable.Range(0, k).Count(i => gridOk[i] && RoomPair(i, ra, rb) is ({ } xa, { } xb) && xa.SteadyAge != xb.SteadyAge);
            missingTotal += missing; methodTotal += method;
            if (missing > 0) notes.Add($"{n}: {missing} pairs dropped: room missing in a run");
            if (method > 0) notes.Add($"{n}: {method} pairs dropped: different age method (fresh-air ACH only; total exchange is unaffected)");
            rows.Add((n, "fresh-air ACH", RoomStat(ra, rb, x => x.Fresh, true)));
            rows.Add((n, "total exchange", RoomStat(ra, rb, x => x.Total, false)));
        }
        // Building fresh-air: n/a for a pair when either run mixed estimators (rooms on different estimators cannot be summed) or the two runs used different ones.
        bool BuildingFreshOk(int i) => runsA[i].AgeMethod != "mixed" && runsA[i].AgeMethod == runsB[i].AgeMethod;
        int buildingMethod = Enumerable.Range(0, k).Count(i => gridOk[i] && !BuildingFreshOk(i));
        if (buildingMethod > 0) notes.Add($"BUILDING: {buildingMethod} pairs dropped: different age method (a run mixed estimators, or A and B used different ones; fresh-air ACH only)");
        notes.Insert(0, $"pairs dropped: {gridDropped} of {k} (Dx, origin or backend differ) · {methodTotal} room pairs (different age method) · {missingTotal} room pairs (room missing in a run) · {buildingMethod} building pairs (different age method)");
        PairStat Stat(Func<Run1, double> f, bool fresh) =>
            PairedStats.Aggregate(Enumerable.Range(0, k).Select(i => gridOk[i] && (!fresh || BuildingFreshOk(i)) ? (f(runsA[i]), f(runsB[i])) : (NaN, NaN)).ToList());
        rows.Add(("BUILDING", "fresh-air ACH", Stat(r => r.Fresh, true)));
        rows.Add(("BUILDING", "total exchange", Stat(r => r.Total, false)));

        void Line(string s) => Console.WriteLine(s);
        Line("");
        Line($"WindFlow paired comparison · A {Options.CompareA} · B {Options.CompareB} · quality {Options.QualityName} · {k} grid offsets (x,y, in cells) · wind {Options.WindSpeed:0.##} m/s from {Options.WindFrom:0}°");
        Line("Both designs run on the same grid offsets. Delta % is B vs A, averaged over the offset pairs; min..max is its spread over the pairs; wins = pairs where B > A.");
        Line("A verdict of better/worse needs every pair to agree and |mean delta| >= 5 %; anything else is unclear. Building total exchange is the volume-weighted mean of the room values.");
        Line("");
        Line($"{"room",-24} {"metric",-14} {"A mean",8} {"B mean",8} {"delta %",8} {"min %",8} {"max %",8}  wins  verdict");
        const string sgn = "+0.0;-0.0;0.0";
        foreach (var (n, m, s) in rows)
            Line($"{n,-24} {m,-14} {F(s.MeanA),8} {F(s.MeanB),8} {F(s.MeanDeltaPct, sgn),8} {F(s.MinDeltaPct, sgn),8} {F(s.MaxDeltaPct, sgn),8}  {s.Wins}/{s.N}   {s.Verdict}");
        if (onlyA.Count > 0) Line($"only in A (not compared): {string.Join(", ", onlyA.Select(x => x.Name))}");
        if (onlyB.Count > 0) Line($"only in B (not compared): {string.Join(", ", onlyB.Select(x => x.Name))}");
        foreach (var n in notes) Line(n);

        if (Options.CompareOut is { } path)
        {
            var csv = new StringBuilder("room,metric,a_mean,b_mean,delta_pct_mean,delta_pct_min,delta_pct_max,wins,pairs,verdict\r\n");
            string C(double v) => double.IsFinite(v) ? v.ToString("0.####", CultureInfo.InvariantCulture) : "";
            string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
            foreach (var (n, m, s) in rows)
                csv.Append($"{Q(n)},{m},{C(s.MeanA)},{C(s.MeanB)},{C(s.MeanDeltaPct)},{C(s.MinDeltaPct)},{C(s.MaxDeltaPct)},{s.Wins},{s.N},{s.Verdict}\r\n");
            foreach (var x in onlyA) csv.Append($"{Q(x.Name)},only in A,,,,,,,,\r\n");
            foreach (var x in onlyB) csv.Append($"{Q(x.Name)},only in B,,,,,,,,\r\n");
            File.WriteAllText(path, csv.ToString());
        }
        return 0;
    }

    static string F(double v, string fmt = "0.0") => double.IsFinite(v) ? v.ToString(fmt, CultureInfo.InvariantCulture) : "—";

    static Run1 Extract(SimulationResult res)
    {
        var conv = res.Convergence;
        var rooms = new List<RunVals>();
        foreach (var r in res.Rooms)
        {
            var rc = conv?.Status.Rooms.FirstOrDefault(x => x.RoomId == r.Room.Id);
            var steady = ResultStatus.AgeSteadyRoom(conv, r.Room.Id);
            rooms.Add(new RunVals(r.Room.Id, r.Room.Name, r.AgeSealed ? double.NaN : r.FreshAch, rc != null ? rc.Mean : r.Ach, r.Volume, steady));
        }
        var ok = rooms.Where(x => double.IsFinite(x.Total)).ToList();
        double vol = ok.Sum(x => x.Volume);
        var g = res.Field.Grid;
        return new Run1(rooms, res.Ledger.FreshAch, vol > 0 ? ok.Sum(x => x.Total * x.Volume) / vol : double.NaN, g.Dx, g.Origin, res.Field.Backend.StartsWith("CPU", StringComparison.Ordinal) ? "CPU" : "GPU");
    }
}

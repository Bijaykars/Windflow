using System.Numerics;

namespace WindFlow.App;

public static partial class SelfCheck
{
    static void Checks_Paired(List<(string Name, bool Ok, string Detail)> feature)
    {
        var better = PairedStats.Aggregate([(2.0, 2.2), (2.0, 2.3), (4.0, 4.4), (1.0, 1.2)]);
        feature.Add(("paired: all pairs agree and mean delta >= 5 % is better, wins 4/4",
            better.Verdict == "better" && better.Wins == 4 && better.N == 4 && Math.Abs(better.MeanDeltaPct - 13.75) < 1e-6 && Math.Abs(better.MinDeltaPct - 10) < 1e-6 && Math.Abs(better.MaxDeltaPct - 20) < 1e-6,
            $"{better.Verdict} {better.MeanDeltaPct:0.00} % [{better.MinDeltaPct:0.0}, {better.MaxDeltaPct:0.0}] wins {better.Wins}/{better.N}"));
        var worse = PairedStats.Aggregate([(2.0, 1.5), (3.0, 2.0)]);
        feature.Add(("paired: all pairs lower is worse", worse.Verdict == "worse" && worse.Wins == 0, $"{worse.Verdict} {worse.MeanDeltaPct:0.0} %"));
        var mixed = PairedStats.Aggregate([(2.0, 2.6), (2.0, 1.9), (2.0, 2.5)]);
        feature.Add(("paired: mixed signs are unclear even with a large mean", mixed.Verdict == "unclear" && mixed.Wins == 2, $"{mixed.Verdict} wins {mixed.Wins}/{mixed.N}"));
        var small = PairedStats.Aggregate([(2.0, 2.02), (2.0, 2.04)]);
        feature.Add(("paired: all agree but under 5 % is unclear", small.Verdict == "unclear", $"{small.Verdict} {small.MeanDeltaPct:0.0} %"));
        var skip = PairedStats.Aggregate([(0.0, 1.0), (double.NaN, 1.0), (2.0, 3.0)]);
        var none = PairedStats.Aggregate([(0.0, 1.0)]);
        feature.Add(("paired: unusable pairs (A not > 0, NaN) are left out; none usable is n/a", skip.N == 1 && skip.Verdict == "better" && none.N == 0 && none.Verdict == "n/a", $"n {skip.N} {skip.Verdict}; {none.Verdict}"));
        var offs = PairedStats.Offsets;
        feature.Add(("paired: eight distinct offsets in [0,1), the first four stratified, (0,0) first",
            offs.Length == 8 && offs.Distinct().Count() == 8 && offs[0] == Vector2.Zero && offs[1] == new Vector2(.5f, .5f) && offs.All(o => o.X >= 0 && o.X < 1 && o.Y >= 0 && o.Y < 1),
            $"{offs.Length} offsets"));
    }
}

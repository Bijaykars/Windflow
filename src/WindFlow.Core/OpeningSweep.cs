using System.Linq;
using System.Numerics;

namespace WindFlow.Core;

public sealed class SweepEntry
{
    public bool[] Open = [];                        // state per candidate opening, same order as the ids
    public float TotalAch;                          // whole-building fresh-air rate: 3600 / volume-weighted mean age of air over all indoor room cells (P15-07; never a sum of room inflows); sealed rooms are left out (RW-03)
    public float SealedVolume;                      // RW-03: m3 of sealed (age-capped) indoor volume the rate leaves out; ranks first, less is better
    public List<string> SealedRooms = new();        // the rooms it belongs to, for the labels
    public string VolumeText => Analysis.VolumeText(SealedVolume, SealedRooms);   // what TotalAch averages over, for labels
    public Dictionary<string, float> RoomAch = new();   // by Room.Id: each room's fresh-air ACH (3600 / mean age; NaN when its age is not solved)
    public SimulationResult? Result;                // kept for the best few entries only
}

/// <summary>"Which windows should be open?" Tries every open/closed combination of up to 8 candidate openings
/// (256 runs) and ranks them. Feasible only because a Preview run takes well under a second on the GPU.</summary>
public static class OpeningSweep
{
    public const int MaxCandidates = 8;
    public const int KeepResults = 3;

    /// <summary>RW-03 ranking, negative when <paramref name="a"/> ranks above <paramref name="b"/>: less sealed indoor volume first (sealing a stale
    /// room drops it from the rate's mean and would otherwise raise the rate), then the higher building fresh-air rate.</summary>
    public static int Rank(SweepEntry a, SweepEntry b)
    {
        int bySealed = a.SealedVolume.CompareTo(b.SealedVolume);
        return bySealed != 0 ? bySealed : b.TotalAch.CompareTo(a.TotalAch);
    }

    public static List<SweepEntry> Run(BuildingModel model, Vector2 wind, IReadOnlyList<string> openingIds, RunOptions options,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (openingIds.Count > MaxCandidates)
            throw new ArgumentException($"At most {MaxCandidates} candidate openings are supported (got {openingIds.Count}).", nameof(openingIds));

        var candidates = openingIds
            .Select(id => model.Openings.FirstOrDefault(o => o.Id == id))
            .Where(o => o != null)
            .Select(o => o!)
            .ToList();
        int n = candidates.Count;
        options = options.Clone();
        options.AgeOfAir = true;   // P15-07: the building rate and the per-room ordering come from the age of air, solved per combination
        var original = candidates.Select(o => o.IsOpen).ToArray();

        int combos = 1 << n;
        var entries = new List<SweepEntry>(combos);
        var kept = new List<SweepEntry>(KeepResults);   // bounded top-KeepResults by Rank seen so far

        try
        {
            // mask = combos-1 (all bits set = all open) runs first, giving a quick upper bound; descend to
            // mask = 0 (all closed) last.
            for (int mask = combos - 1; mask >= 0; mask--)
            {
                ct.ThrowIfCancellationRequested();

                var open = new bool[n];
                for (int b = 0; b < n; b++)
                {
                    bool isOpen = (mask & (1 << b)) != 0;
                    open[b] = isOpen;
                    candidates[b].IsOpen = isOpen;
                }

                var result = Simulation.Run(model, wind, options, null, ct);

                var roomAch = new Dictionary<string, float>();
                foreach (var rr in result.Rooms) roomAch[rr.Room.Id] = rr.FreshAch;

                var entry = new SweepEntry
                {
                    Open = open,
                    TotalAch = result.Ledger.FreshAch,   // D3 / P15-03
                    SealedVolume = result.Ledger.SealedVolume,   // RW-03
                    SealedRooms = result.Ledger.SealedRooms,
                    RoomAch = roomAch,
                    Result = result,
                };
                entries.Add(entry);

                // Bounded top-KeepResults by Rank: null the Result of whichever kept entry falls out,
                // so peak memory holds at most KeepResults full flow fields instead of all `combos`.
                if (KeepResults <= 0)
                {
                    entry.Result = null;
                }
                else if (kept.Count < KeepResults)
                {
                    kept.Add(entry);
                }
                else
                {
                    int worst = 0;
                    for (int k = 1; k < kept.Count; k++)
                        if (Rank(kept[k], kept[worst]) > 0) worst = k;

                    if (Rank(entry, kept[worst]) < 0)
                    {
                        kept[worst].Result = null;
                        kept[worst] = entry;
                    }
                    else
                    {
                        entry.Result = null;
                    }
                }

                progress?.Report((double)(combos - mask) / combos);
            }
        }
        finally
        {
            for (int i = 0; i < n; i++) candidates[i].IsOpen = original[i];
        }

        entries.Sort(Rank);

        return entries;
    }
}

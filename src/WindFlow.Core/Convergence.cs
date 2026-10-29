// PLAN_V2 2.2 "smarter convergence": stop a run once every analysed room's air-change rate has settled,
// instead of after a fixed step count. Small and Core-only by design -- both Simulation.Run (RunOptions.AutoConverge)
// and a live app loop (a future lane's job) can drive the same ConvergenceMonitor.
//
// Method: classic batch means, e.g. Law & Kelton, "Simulation Modeling and Analysis" (3rd ed., 2000) ch. 9.4 --
// split the post-warm-up run into batches long enough that consecutive batch MEANS are roughly independent
// even though the raw per-step signal is heavily autocorrelated, then treat the kept batch means as (approximately)
// normal and apply the Student-t confidence interval to them, widened for the batch means' residual lag-1
// autocorrelation. The start-up transient is cut by power-of-two truncation and checked by a split-half stationarity test.

namespace WindFlow.Core;

public enum ConvergenceStopReason
{
    Running,     // still collecting batches
    Converged,   // every analysed room's half-width < its tolerance and its kept batches are stationary
    Capped,      // hit MinBatches..MaxBatches without converging (or the session's own hard step cap)
    Diverged,    // a batch mean was NaN/Infinity, or blew through the plausible ACH ceiling
    Fixed,       // WINDFLOW_FIXED_BATCHES=s,n (measurement seam): the pass ran s discarded + n kept batches and stopped, no convergence rule
}

/// <summary>One analysed room's batch-means statistics at the moment <see cref="ConvergenceMonitor.Evaluate"/>
/// was called.</summary>
public sealed class RoomConvergence
{
    public string RoomId = "";
    public int BatchCount;      // batches KEPT after the start-up truncation (the others were dropped)
    public float Mean;          // ACH, mean of the kept batch means
    public float HalfWidth;     // ACH, 95% Student-t half-width of that mean, inflated for batch autocorrelation
    public float StopHalfWidth; // ACH, the 99 % half-width the stop rule tested: shown for a Converged room (optional stopping, audit P16-01)
    public float Tolerance;     // ACH, the band HalfWidth had to fall under (relative, floored -- see AchFloor)
    public bool Converged;      // the 99 % half-width (not the reported 95 % one) < Tolerance and Stationary, with at least MinBatches kept
    public bool Stationary;     // the first and second half of the kept batches agree within max(Tolerance, 2 x HalfWidth)
    public float RelSd;         // sd / |mean| of the kept batch means: the "batch-to-batch variation" the UI shows
    public float Rho;           // lag-1 autocorrelation of all the batch means, clamped to [0, 0.9] (0 under 8 batches)
    /// <summary>6k: the fresh-air ACH half-widths (95 % and the 99 % stop one) from the room's per-batch online air-age means, by the delta
    /// method on 3600 / age; NaN when the pass had no online age (the room then shows no fresh-air range of its own).</summary>
    public float FreshHalfWidth = float.NaN, FreshStopHalfWidth = float.NaN;
    /// <summary>Standard (per-room estimator): true when this room's online age had not settled and the room landed the steady age instead (no fresh-air ±).</summary>
    public bool AgeSteady;
}

public sealed class ConvergenceStatus
{
    public bool Converged;
    public ConvergenceStopReason Reason = ConvergenceStopReason.Running;
    public List<RoomConvergence> Rooms = new();
}

/// <summary>One line of the group-noise seam: a room's (or the building's, Total NaN) fresh-air ACH and total-exchange ACH as the mean of the k group values and sigma = SD / sqrt(k).</summary>
public sealed record GroupSigmaRow(string Name, double Fresh, double FreshSigma, double Total, double TotalSigma)
{
    public static string Pct(double sigma, double mean) => mean != 0 && double.IsFinite(mean) ? (100 * sigma / Math.Abs(mean)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " %" : "—";
    public string Line(int k)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        static string N(double x, IFormatProvider ci) => double.IsFinite(x) ? x.ToString("0.00", ci) : "—";
        string t = double.IsNaN(Total) ? "" : $" total {N(TotalSigma, ci)} ACH ({Pct(TotalSigma, Total)})";
        return $"group sigma (k={k}): {Name} fresh {N(FreshSigma, ci)} ACH ({Pct(FreshSigma, Fresh)}){t}";
    }
}

/// <summary>How a self-stopping run ended (<see cref="SimulationResult.Convergence"/>). A room whose
/// <see cref="RoomConvergence.Converged"/> is false when the run stopped is "capped, not settled": the cap (wall time, step
/// cap or batch cap) arrived before its ACH confidence interval reached the target. Per room, not per run.</summary>
public sealed class ConvergenceReport
{
    public ConvergenceStatus Status = new();
    public int Batches;
    /// <summary>Ultra runs: the cell size (m) of the grid the result came from, 0 for every other quality. The labels name the quality from it.</summary>
    public float UltraDx;
    public long Steps;
    public double WallSeconds;
    /// <summary>RunOptions.WallCapSeconds ended the run.</summary>
    public bool WallCapHit;
    /// <summary>The plan the run worked to (0 until the probe analysis ran): the longest room's flow-through V/Q, in seconds and steps,
    /// and the steps of one batch (>= one flow-through). Warm-up is WarmupFlowThroughs x FlowThroughSteps on top of the fixed one.</summary>
    public float FlowThroughSeconds;
    public int FlowThroughSteps, BatchSteps;
    /// <summary>6k: true when the age of air in the result is the time-averaged online scalar, false when it is the steady solve on the mean flow.</summary>
    public bool AgeOnline;
    /// <summary>"age: online (time-averaged)" or "age: steady on mean flow" (with the reason when the online one was not available).</summary>
    public string AgeLabel = "";
    /// <summary>Standard: the age estimator is chosen per room (online where that room's age settled, else steady, <see cref="RoomConvergence.AgeSteady"/>); High lands the online age everywhere.</summary>
    public bool AgePerRoom;
    /// <summary>WINDFLOW_GROUP_AGES seam: per room (and the building) the SD of the k group values over sqrt(k); empty when the seam is off.</summary>
    public List<GroupSigmaRow> GroupSigma = new();
    public int GroupK;
    /// <summary>The seam's one-line note (k, batches per group, batches dropped from the last group, or why it did not run).</summary>
    public string GroupSigmaNote = "";

    /// <summary>"settled" or "capped, not settled" for one room (a room with no batch statistics yet is not settled).</summary>
    public static string RoomStatus(RoomConvergence? r) => (r is { Converged: true } ? "settled" : "capped, not settled") + (r is { AgeSteady: true } ? " " + SteadyMarker : "");

    /// <summary>The short marker on a room whose fresh-air ACH is the steady age (its online age had not settled).</summary>
    public const string SteadyMarker = "· steady age";
}

/// <summary>Batch-means convergence monitor over one or more rooms' air-change rate (ACH). Feed it one batch
/// mean per room per call to <see cref="AddBatch"/> (see <see cref="ConvergencePlan"/> for picking the warm-up
/// and batch length), then call <see cref="Evaluate"/>. The monitor holds no reference to the solver -- it is
/// pure bookkeeping over the numbers the caller hands it, so it works identically from a batch <see
/// cref="Simulation.Run"/> loop or a live app tick.</summary>
public sealed class ConvergenceMonitor
{
    /// <summary>Half-width must fall under this fraction of the room's mean ACH (5%, PLAN_V2 2.2).</summary>
    public float RelativeTolerance = 0.05f;

    /// <summary>Absolute floor on the tolerance, ACH. A room near zero ACH (a sealed room, a dead zone) would
    /// otherwise need an impossibly tight relative band (5% of 0.02 ACH is noise-level), so the band actually
    /// applied is max(RelativeTolerance * |mean|, AchFloor). 0.2 ACH is about half of ASHRAE 62.2's minimum
    /// whole-building mechanical rate (~0.35 ACH) -- distinguishing ACH differences finer than that has no
    /// practical ventilation meaning.</summary>
    public float AchFloor = 0.2f;

    /// <summary>At least this many batches before convergence can be declared (a t-interval on fewer than a
    /// handful of samples is not trustworthy).</summary>
    public int MinBatches = 5;

    /// <summary>Total batches (kept + truncated) before Converged can be declared: rho-hat is forced to 0 below 8 batches, and a simulated sequential rule missed the band 15.7 % of the time at n = 7 (5 kept).</summary>
    public const int MinTotalBatches = 16;

    /// <summary>Hard cap: stop (Capped) at this many batches even if some room never converges.</summary>
    public int MaxBatches = 60;

    readonly Dictionary<string, List<float>> _batches = new();
    bool _diverged;

    /// <summary>Leading batches thrown away as start-up transient: <see cref="TruncationFor"/> of the batches so far.</summary>
    public int TransientBatches => Truncation(BatchCount);

    /// <summary>WINDFLOW_FIXED_BATCHES seam: when non-negative, exactly this many leading batches are dropped (instead of <see cref="TruncationFor"/>).</summary>
    public int FixedDrop = -1;
    int Truncation(int n) => FixedDrop >= 0 ? Math.Min(FixedDrop, n) : TruncationFor(n);

    /// <summary>Start-up truncation for <paramref name="n"/> batches: 0 for n &lt; 2, else the largest power of two &lt;= n/2
    /// (n = 2,3 -> 1; 4-7 -> 2; 8-15 -> 4; 16-31 -> 8). The kept batches are the rest, n - d.</summary>
    // ponytail: power-of-two truncation discards 25-50 % of batches so the field mean needs only 2 snapshots; MSER with finer snapshots if run time matters
    public static int TruncationFor(int n) => n < 2 ? 0 : 1 << (31 - System.Numerics.BitOperations.LeadingZeroCount((uint)(n / 2)));

    /// <summary>Records one batch mean per room. <paramref name="roomBatchMeans"/> is (RoomId, batch-mean ACH)
    /// for every analysed room -- call once per batch, after the solver has advanced one batch's worth of
    /// steps with the time-average reset at the start of that batch (each batch mean must describe only its
    /// own interval, not a cumulative average, or consecutive "batch means" are not independent samples).</summary>
    public void AddBatch(IEnumerable<(string RoomId, float Ach)> roomBatchMeans)
    {
        foreach (var (id, ach) in roomBatchMeans)
        {
            if (float.IsNaN(ach) || float.IsInfinity(ach)) _diverged = true;
            if (!_batches.TryGetValue(id, out var list)) _batches[id] = list = new List<float>();
            list.Add(ach);
        }
    }

    public int BatchCount => _batches.Count == 0 ? 0 : _batches.Values.Max(l => l.Count);

    /// <summary>Ultra checkpoint: the batch series per room.</summary>
    internal void WriteState(BinaryWriter w)
    {
        w.Write(_diverged); w.Write(_batches.Count);
        foreach (var (id, list) in _batches) { w.Write(id); CkptIo.Write(w, list.ToArray()); }
    }

    internal void ReadState(BinaryReader r)
    {
        _diverged = r.ReadBoolean(); _batches.Clear();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) { string id = r.ReadString(); _batches[id] = new List<float>(CkptIo.ReadFloats(r)); }
    }

    public ConvergenceStatus Evaluate()
    {
        var status = new ConvergenceStatus();
        if (_diverged)
        {
            status.Reason = ConvergenceStopReason.Diverged;
            return status;
        }

        // Start-up transient: drop the first d(n) batches (TruncationFor) and keep the rest, so a slow start-up cannot
        // leak into the mean and the cut moves only when n doubles. Per room: mean and sd over the kept batches; the CI
        // is the Student-t one inflated by sqrt((1+rho)/(1-rho)) for the lag-1 autocorrelation of the batch means; and
        // the room is stationary when the two halves of the kept batches agree within max(tolerance, 2 x half-width).
        bool allConverged = _batches.Count > 0;
        foreach (var (id, full) in _batches)
        {
            int d = Math.Min(Truncation(BatchCount), full.Count);
            var list = full.GetRange(d, full.Count - d);
            int kept = list.Count;
            float mean = kept == 0 ? 0f : list.Average();
            double s = StdDev(list, mean);
            // Marriott & Pope 1954: the lag-1 estimate is biased low by about (1+3rho)/n, so add it back; Student-t df from the effective sample size.
            double r1 = full.Count < 8 ? 0 : Lag1(full), rho = full.Count < 8 ? 0 : Math.Clamp(r1 + (1 + 3 * r1) / full.Count, 0.0, 0.9);
            int df = Math.Max(1, (int)Math.Floor(kept * (1 - rho) / (1 + rho)) - 1);
            float halfWidth = kept < 2 ? float.PositiveInfinity : (float)(TCritical95(df) * s / Math.Sqrt(kept) * Math.Sqrt((1 + rho) / (1 - rho)));
            float tolerance = MathF.Max(RelativeTolerance * MathF.Abs(mean), AchFloor);
            bool stationary = false;
            if (kept >= 2)
            {
                int h = kept / 2;
                float diff = MathF.Abs(list.Take(h).Average() - list.Skip(h).Average());
                stationary = diff <= MathF.Max(tolerance, 2 * halfWidth);
            }
            // Stop on the 99 % half-width while the 95 % one is what is reported: optional stopping inflates the miss rate at the stop (54b(d) experiment 2026-10-09, candidate A).
            double stopHalfWidth = kept < 2 ? double.PositiveInfinity : TCritical995(df) * s / Math.Sqrt(kept) * Math.Sqrt((1 + rho) / (1 - rho));
            bool converged = BatchCount >= MinTotalBatches && kept >= MinBatches && stopHalfWidth < tolerance && stationary;
            allConverged &= converged;
            status.Rooms.Add(new RoomConvergence
            {
                RoomId = id, BatchCount = kept, Mean = mean, HalfWidth = halfWidth, StopHalfWidth = (float)stopHalfWidth, Tolerance = tolerance, Converged = converged,
                // a sealed room (all zero) varies by nothing
                RelSd = mean == 0f ? (s == 0 ? 0f : float.PositiveInfinity) : (float)(s / Math.Abs(mean)),
                Rho = (float)rho, Stationary = stationary,
            });
        }

        if (allConverged) { status.Converged = true; status.Reason = ConvergenceStopReason.Converged; }
        else if (BatchCount >= MaxBatches) { status.Converged = false; status.Reason = ConvergenceStopReason.Capped; }
        else status.Reason = ConvergenceStopReason.Running;
        return status;
    }

    /// <summary>Sample standard deviation (n-1); 0 for fewer than two samples.</summary>
    static double StdDev(List<float> samples, float mean)
    {
        if (samples.Count < 2) return 0;
        double ss = 0;
        foreach (var v in samples) { double d = v - mean; ss += d * d; }
        return Math.Sqrt(ss / (samples.Count - 1));
    }

    /// <summary>Lag-1 autocorrelation of a series about its own mean; 0 for a constant series.</summary>
    static double Lag1(List<float> x)
    {
        double m = x.Average(), num = 0, den = 0;
        for (int i = 0; i < x.Count; i++)
        {
            double a = x[i] - m;
            den += a * a;
            if (i > 0) num += a * (x[i - 1] - m);
        }
        return den > 0 ? num / den : 0;
    }

    // Two-sided 97.5th percentile of Student's t, degrees of freedom 1..30 (standard statistical tables);
    // past df = 30 the Cornish-Fisher approximation t ~ z + (z^3 + z) / (4 df) is used (2.028 at df 35, 2.000 at df 60).
    static readonly double[] T95 =
    {
        12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
        2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
        2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042,
    };
    // Two-sided 99th percentile (t at 0.995), df 1..30, standard tables; Cornish-Fisher to second order past that.
    static readonly double[] T995 =
    {
        63.657, 9.925, 5.841, 4.604, 4.032, 3.707, 3.499, 3.355, 3.250, 3.169,
        3.106, 3.055, 3.012, 2.977, 2.947, 2.921, 2.898, 2.878, 2.861, 2.845,
        2.831, 2.819, 2.807, 2.797, 2.787, 2.779, 2.771, 2.763, 2.756, 2.750,
    };
    static double TCritical995(int df)
    {
        if (df >= 1 && df <= T995.Length) return T995[df - 1];
        const double z = 2.575829;
        return z + (z * z * z + z) / (4.0 * df) + (5 * Math.Pow(z, 5) + 16 * z * z * z + 3 * z) / (96.0 * df * df);
    }
    static double TCritical95(int df)
    {
        if (df >= 1 && df <= T95.Length) return T95[df - 1];
        const double z = 1.959964;
        return z + (z * z * z + z) / (4.0 * df);
    }
}

/// <summary>Picks the warm-up length and batch length a <see cref="ConvergenceMonitor"/> run uses, from each
/// analysed room's own flow-through time V_room / Q_room (PLAN_V2 2.2).</summary>
public static class ConvergencePlan
{
    /// <summary>Cold-start warm-up = the largest analysed room's flow-through time (V/Q), in steps of length dt; falls
    /// back to <paramref name="fallbackSteps"/> (the solver's existing fixed warm-up) when no room has real
    /// through-flow (Q ~ 0 everywhere -- a sealed building, a still-air scene).</summary>
    public static int WarmupSteps(IEnumerable<(float VolumeM3, float FlowM3S)> rooms, float dt, int fallbackSteps)
    {
        float longest = 0f;
        foreach (var (v, q) in rooms)
            if (q > 1e-6f) longest = MathF.Max(longest, v / q);
        return longest > 0f && dt > 0f ? Math.Max(1, (int)MathF.Ceiling(longest / dt)) : fallbackSteps;
    }

    /// <summary>Batch length = at least one flow-through time of the same (largest) room, so consecutive
    /// batches see roughly independent turnovers of the room's air rather than overlapping ones; floored at
    /// <paramref name="minSteps"/> so a very fast-flushing room still gets enough samples per batch to average
    /// out step-to-step turbulent noise.</summary>
    public static int BatchSteps(float flowThroughSeconds, float dt, int fallbackSteps, int minSteps = 50)
    {
        if (flowThroughSeconds <= 0f || dt <= 0f) return Math.Max(minSteps, fallbackSteps);
        return Math.Max(minSteps, (int)MathF.Ceiling(flowThroughSeconds / dt));
    }
}

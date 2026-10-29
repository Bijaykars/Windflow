using WindFlow.Core;

namespace WindFlow.Report;

/// <summary>What the numbers on screen and in the PDF are, in plain words, from the convergence report the pass left on
/// <see cref="SimulationResult.Convergence"/>. One mapping for the room table, the pass strip and the report cover, so a run
/// that stopped on its time budget can never read "Settled" anywhere. Null (no self-stopping pass behind the numbers) is Preview.</summary>
public static class ResultStatus
{
    public const string Preview = "Preview";
    public const string Settled = "Settled";
    public const string TimeLimited = "Time limit reached: ranges shown";
    public const string Diverged = "Did not settle: no result";
    public const string TooShortText = "Time limit reached: too short for ranges, use a longer budget";
    public const string TooShortTip = "run too short to estimate a range";

    /// <summary>A pass that ended with fewer than <see cref="ConvergenceMonitor.MinTotalBatches"/> batches in all, or a room with
    /// fewer than <c>MinBatches</c> kept: its 95 % t-interval (df of 1 to 3) is absurdly wide, so no range is shown. A Converged
    /// pass never is (the monitor needs both counts to converge). Null (Preview) is not "too short", it has no range at all.</summary>
    public static bool TooShort(ConvergenceReport? c) =>
        c != null && c.Status.Reason != ConvergenceStopReason.Converged &&
        (c.Batches < ConvergenceMonitor.MinTotalBatches || c.Status.Rooms.Any(r => r.BatchCount < new ConvergenceMonitor().MinBatches));

    /// <summary>The same rule for one room: the pass-wide batch count, and this room's kept batches.</summary>
    public static bool TooShort(ConvergenceReport? c, string roomId) =>
        c != null && c.Status.Reason != ConvergenceStopReason.Converged &&
        (c.Batches < ConvergenceMonitor.MinTotalBatches ||
         c.Status.Rooms.Any(r => r.RoomId == roomId && r.BatchCount < new ConvergenceMonitor().MinBatches));

    /// <summary>The short status after the Accurate pass lands. Only <see cref="ConvergenceStopReason.Converged"/> is Settled;
    /// a pass still "Running" when it was cut off is time-limited too.</summary>
    public static string Text(ConvergenceReport? c) => c == null ? Preview : c.Status.Reason switch
    {
        ConvergenceStopReason.Converged => Settled,
        ConvergenceStopReason.Diverged => Diverged,
        _ => TooShort(c) ? TooShortText : TimeLimited,
    };

    /// <summary>The cover-page line of the report.</summary>
    public static string ReportLine(ConvergenceReport? c) => c == null
        ? "Preview: a fast, coarse run, good for direction only"
        : c.UltraDx > 0 ? Ultra.ResultLabel(c.UltraDx) + " " + ReportLineBody(c) : ReportLineBody(c);

    static string ReportLineBody(ConvergenceReport c) =>
        c.Status.Reason == ConvergenceStopReason.Converged
            ? "Fine grid, settled: every room's air exchange is steady to within its aim of 5 % or 0.2 ACH" +
              (c.AgeOnline ? "; the fresh-air ± (averaging range, excludes slow age drift) comes from the room's per-batch air age" + (c.Status.Rooms.Any(r => r.AgeSteady) ? " (none for rooms marked steady age)" : "") : "; fresh-air ACH has no ± (steady age)") + " (averaging only, not a check against measurements)"
            : TooShort(c)
                ? "Fine grid, time-limited: too short for ranges, use a longer budget"
                : "Fine grid, time-limited: stopped at its time budget; see the ± column";

    /// <summary>"Preview", "Fine grid, steady wind (Standard)" or "Fine grid, gusty wind (High)": what the numbers are (S6-07: "Accurate"
    /// implied a check against measurements). The live options keep Quality = Preview even for an averaged pass, so the pass (a
    /// convergence report) decides, and High is the pass that ran with per-supply turbulence. With no pass behind the numbers it is the
    /// options' own quality (Preview, or Refine for a headless/add-in run).</summary>
    public static string QualityLabel(SimulationResult r) => r.Convergence == null
        ? r.Options.Quality.ToString()
        : r.Convergence.UltraDx > 0 || r.Options.Ultra != null ? Ultra.ResultLabel(r.Convergence.UltraDx > 0 ? r.Convergence.UltraDx : r.Field.Grid.Dx)
        : r.Options.SupplyTuPerSource || r.Options.Quality == Quality.HighAccuracy ? "Fine grid, gusty wind (High)" : "Fine grid, steady wind (Standard)";

    /// <summary>True only for a pass that stopped on its own rule: every room Settled (S6-03). The tick, the green chip and "result ready"
    /// wording hang on this and nothing else.</summary>
    public static bool IsSettled(ConvergenceReport? c) => c != null && c.Status.Reason == ConvergenceStopReason.Converged;

    /// <summary>The landed averaged pass in the status strip: a tick only when Settled, the plain time-limit words otherwise (S6-03).</summary>
    public static string StripText(ConvergenceReport? c, double megaCells) => c is { UltraDx: > 0 }
        ? $"{Ultra.ResultLabel(c.UltraDx)} {(IsSettled(c) ? "Settled" : Text(c))} · {megaCells:0.#}M cells, GPU idle"
        : IsSettled(c)
        ? $"Averaged result ✓ settled · {megaCells:0.#}M cells, GPU idle"
        : $"Averaged result · {Text(c)} · {megaCells:0.#}M cells, GPU idle";

    /// <summary>The tag on the pass strip once an averaged pass has landed.</summary>
    public static string TagText(ConvergenceReport? c) => IsSettled(c) ? "SETTLED" : "TIME-LIMITED";

    /// <summary>"Averaged result, settled" or "Averaged result, time limit reached" for the trust head and the status line.</summary>
    public static string ResultName(ConvergenceReport? c) => c is { UltraDx: > 0 } ? $"Ultra result (Dx {c.UltraDx * 1000:0.#} mm; accuracy not separately validated), " + (IsSettled(c) ? "settled" : "time limit reached") : IsSettled(c) ? "Averaged result, settled" : "Averaged result, time limit reached";

    /// <summary>The room's 95 % half-width in ACH, or NaN when the report has none for it.</summary>
    public static float HalfWidth(ConvergenceReport? c, string roomId)
    {
        if (c == null) return float.NaN;
        foreach (var r in c.Status.Rooms) if (r.RoomId == roomId) return Shown(r);
        return float.NaN;
    }

    /// <summary>The half-width a room shows: for a Settled room the 99 % one the stop rule actually tested (the 95 % one under-covers after a
    /// data-dependent stop, P16-01), otherwise the 95 % averaging range.</summary>
    public static float Shown(RoomConvergence r) => r.Converged && float.IsFinite(r.StopHalfWidth) ? r.StopHalfWidth : r.HalfWidth;

    /// <summary>True when the room's averaged batches still drift (its two halves disagree) and it has enough batches for a range: the
    /// ± may then be too narrow (P16-03).</summary>
    public static bool Drifting(ConvergenceReport? c, string roomId) =>
        c != null && !TooShort(c, roomId) && c.Status.Rooms.Any(r => r.RoomId == roomId && !r.Stationary);

    /// <summary>The tooltip sentence for a room's ±: which range it is (99 % tested width for Settled, 95 % averaging range otherwise), what it
    /// excludes, and the drift warning when the batches have not levelled off.</summary>
    public static string RangeTip(RoomConvergence rc, float shown)
    {
        // 6k: with the online age the fresh-air range is the room's own per-batch spread of the time-averaged age, otherwise the total exchange's range scaled to it.
        string basis = float.IsFinite(rc.FreshHalfWidth) ? "averaging range (excludes slow age drift), from the batch-to-batch spread of the room's time-averaged air age" : "scaled from the room's flow statistics to the fresh-air ACH";
        string head = rc.Converged
            ? $"± {shown:0.0} approx.: the 99 % averaging range, the width the stop rule tested ({basis}; model error not included)."
            : $"± {shown:0.0} approx.: 95 % averaging range (how far longer averaging could still move this number; {basis}; model error not included); the aim was ±{rc.Tolerance:0.0} on the total exchange, not reached in the time budget.";
        return rc.Stationary ? head : head + "\n" + DriftTip;
    }

    /// <summary>The same warning for a row with no ± range (Standard): there is no range to be outside of.</summary>
    public const string DriftTipNoRange = "Still drifting: the room's value was still changing when the run ended, so it may move further with a longer run.";

    /// <summary>RA-03: the drift warning sentence for a row with a ± range.</summary>
    public const string DriftTip = "Still drifting: the first and second half of the averaged batches disagree, so the true value may lie outside this range.";

    /// <summary>RA-03: " (drifting)" for the PDF's fresh-air cell when the ± column is absent (Standard) and the room's batches drift; empty otherwise.</summary>
    /// <param name="cell">the fresh-air cell text so far: a "sealed" or "—" cell carries no number, so nothing is appended to it.</param>
    public static string PdfDriftSuffix(ConvergenceReport? c, string roomId, string cell = "") => !FreshHasRange(c, roomId) && Drifting(c, roomId) && cell != "—" && !cell.StartsWith("sealed", StringComparison.Ordinal) ? " (drifting)" : "";

    /// <summary>The "± approx." on a room's fresh-air ACH (D1, 6k): with the online age, the half-width of the room's per-batch air-age means in ACH (the 99 %
    /// one the stop rule tested for a Settled room, else the 95 % one: <see cref="RoomConvergence.FreshHalfWidth"/>); for a steady-age result the room's
    /// total-exchange half-width as a share of its mean, times the fresh-air ACH. NaN when there is no range or no age.</summary>
    public static float FreshHalfWidth(ConvergenceReport? c, RoomResult r)
    {
        if (c == null || !c.AgeOnline || float.IsNaN(r.FreshAch) || r.AgeSealed || AgeSteadyRoom(c, r.Room.Id)) return float.NaN;   // AGE-FIX: a steady-age fresh-air ACH has no batch range (never a scaled one)
        foreach (var rc in c.Status.Rooms)
        {
            if (rc.RoomId != r.Room.Id) continue;
            float own = rc.Converged && float.IsFinite(rc.FreshStopHalfWidth) ? rc.FreshStopHalfWidth : rc.FreshHalfWidth;
            if (float.IsFinite(own)) return own;
            return rc.Mean > 0 && float.IsFinite(Shown(rc)) ? Shown(rc) / rc.Mean * r.FreshAch : float.NaN;
        }
        return float.NaN;
    }

    /// <summary>AGE-FIX: true when the fresh-air ACH carries a ± (the online age's batch range); a steady-age result shows none, not even "± —".</summary>
    public static bool FreshHasRange(ConvergenceReport? c) => c is { AgeOnline: true };

    /// <summary>Standard: true when this room's fresh-air ACH is the steady age (its online age had not settled): no ±, and the row says "steady age".</summary>
    public static bool AgeSteadyRoom(ConvergenceReport? c, string roomId) => c is { AgePerRoom: true } && c.Status.Rooms.Any(r => r.RoomId == roomId && r.AgeSteady);

    /// <summary>The per-room form of <see cref="FreshHasRange(ConvergenceReport?)"/>: the report has a fresh-air ± and this room is not on the steady age.</summary>
    public static bool FreshHasRange(ConvergenceReport? c, string roomId) => FreshHasRange(c) && !AgeSteadyRoom(c, roomId);

    /// <summary>6k: which air age the numbers carry, in words: the report's own label, "age: steady on mean flow" for a Preview or an older report.</summary>
    public static string AgeLine(ConvergenceReport? c) => c == null || c.AgeLabel.Length == 0 ? AveragedPass.AgeSteadyLabel : c.AgeLabel;

    /// <summary>AGE-HIGH: true when the result carries the steady age beside the online one (the steady-age value) (High: the online age landed).</summary>
    public static bool HasConservative(ConvergenceReport? c) => c is { AgeOnline: true, AgePerRoom: false };

    /// <summary>AGE-FIX / AGE-HIGH: the disclosure that goes with a result: the steady-age bias note (Standard), or the gusty-wind note (High, online age), then the grid-sensitivity note (always).</summary>
    public static string AgeNote(ConvergenceReport? c) => (c switch
    {
        { AgeOnline: true, AgePerRoom: true } => AveragedPass.AgeStandardNote + (c.Status.Rooms.Any(r => r.AgeSteady) ? " " + AveragedPass.AgeStandardMixedNote + " " + AveragedPass.AgeSteadyNote : ""),
        { AgeOnline: true } => AveragedPass.AgeGustyNote,
        _ => AveragedPass.AgeSteadyNote,
    }) + " " + AveragedPass.GridSensitivityNote;

    /// <summary>"13.3 ±0.6": the ACH with its 95 % half-width, one decimal each; "~13.3" for a provisional (Preview) number; the
    /// mark (voxel-volume †) stays on the value. No ± when there is no finite half-width; "± —" for a run too short for a range.</summary>
    public static string AchCell(float ach, float halfWidth, bool provisional, string mark = "", bool tooShort = false, bool drifting = false)
    {
        string v = (provisional ? "~" : "") + ach.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + mark;
        if (tooShort && !provisional) return v + " ± —";
        string driftMark = drifting && !provisional ? " ⚠" : "";   // RA-03: the drift mark does not depend on a ± (Standard shows none)
        return float.IsFinite(halfWidth) && !provisional ? $"{v} ±{halfWidth.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}" + driftMark : v + driftMark;
    }
}

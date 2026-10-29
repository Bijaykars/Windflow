using WindFlow.Core;

namespace WindFlow.App;

/// <summary>Where the result on screen stands. Preview is the live coarse run; the three middle states are the
/// Accurate pass working; Accurate is its finished, reportable result. Off: no Accurate pass for this setup.</summary>
public enum AccuratePhase { Preview, Settling, Averaging, Finishing, Accurate, Off }

/// <summary>Which recipe the Accurate pass runs. Standard is the pass as it always was. High is the same pass (grid, settle,
/// batch rule, publication guard) plus per-supply inlet turbulence, EN 1991-1-4 approach-flow turbulence and, when a focus
/// room is given, a 2:1 nested fine box around it (<see cref="HighAccuracy.Plan"/>'s box).</summary>
public enum AccurateMode { Standard, High, Ultra }

public static class AccurateRecipe
{
    /// <summary>The options the Accurate pass runs with. Standard: a copy with OnlineAge on (the pass lands it per room where it settled). High: a copy with SupplyTuPerSource, the approach turbulence and, with a <paramref name="box"/>, FocusBox.
    /// Built at the pass start from the live options, so a ConfigCommand / Start that rebuilds them cannot drop the High settings.</summary>
    public static RunOptions Options(AccurateMode mode, RunOptions baseOptions, BuildingModel model, DomainBox? box)
    {
        var o = baseOptions.Clone();
        if (mode == AccurateMode.Ultra) { o.OnlineAge = true; o.OnlineAgePerRoom = true; o.MinDx = Ultra.MinDx; o.Ultra = new UltraSettings(); return o; }   // Ultra: the Standard pipeline (online age per room) on its own grid; LiveSolver fills the settings
        if (mode != AccurateMode.High) { o.OnlineAge = true; o.OnlineAgePerRoom = true; return o; }   // Standard: the online age too, landed per room where it settles (AveragedPass.PerRoom)
        o.SupplyTuPerSource = true;
        o.OnlineAge = true;   // AGE-HIGH
        o.WindTurbulence = HighAccuracy.ApproachTurbulence(model, o.WindProfileExponent);
        o.FocusBox = box;
        return o;
    }
}

/// <summary>"Preview, then Accurate" (PLAN_V2 naming row: Preview and Refine; the UI says Accurate). The state
/// machine and the progress arithmetic, with no solver and no clock of its own, so --selfcheck drives the same
/// object the solver thread does. Written by the solver thread only; the UI reads the volatile snapshot fields.
/// <para>Progress never decreases inside one pass (it is the max of every estimate so far); an edit starts a new
/// cycle, which is the only way back to 0. The ETA is remaining steps over the measured steps/s plus the measured
/// cost of the finishing analysis; NaN (shown as "…") until there is a rate to divide by.</para></summary>
public sealed class AccurateTracker
{
    /// <summary>Share of the bar the finishing analysis (ToField, rooms, air age on the fine grid) gets.</summary>
    public const double FinishShare = 0.08;

    volatile AccuratePhase phase = AccuratePhase.Preview;
    double progress, eta = double.NaN;
    volatile bool etaPartial;
    volatile int cycle;
    string offWhy = "";

    public AccuratePhase Phase => phase;
    public double Progress => Volatile.Read(ref progress);
    public double EtaSeconds => Volatile.Read(ref eta);
    /// <summary>The ETA covers the stepping only: the finishing analysis has not been measured yet.</summary>
    public bool EtaPartial => etaPartial;
    /// <summary>Bumped by every edit: a finishing analysis that started in an older cycle is thrown away.</summary>
    public int Cycle => cycle;

    volatile bool edited;
    /// <summary>Set by every Edit (a session start included), cleared when an Accurate result lands.</summary>
    public bool EditedSinceAccurate => edited;

    /// <summary>The rows read "settling…" instead of a number: the Preview value on screen is the first, unsettled window after an
    /// edit, Preview has stopped publishing (the Accurate pass holds the GPU) and the Accurate result has not landed.</summary>
    public static bool ShowSettling(bool statsFromPreview, bool accurateRunning, bool editedSinceAccurate) =>
        statsFromPreview && accurateRunning && editedSinceAccurate;
    public string OffWhy => Volatile.Read(ref offWhy);

    /// <summary>True while the Accurate pass holds the GPU (the preview session is idle).</summary>
    public bool Running => phase is AccuratePhase.Settling or AccuratePhase.Averaging or AccuratePhase.Finishing;

    /// <summary>The label every number on screen carries: only a finished pass is "Accurate".</summary>
    public string ResultLabel => phase == AccuratePhase.Accurate ? "Accurate" : "Preview";

    /// <summary>A flow-changing edit (wind, opening, device, environment, box): whatever the pass was doing is
    /// cancelled and the cycle starts again from Preview.</summary>
    public void Edit()
    {
        edited = true;
        phase = AccuratePhase.Preview;
        Volatile.Write(ref progress, 0);
        Volatile.Write(ref eta, double.NaN);
        detail = "";
        Volatile.Write(ref offWhy, "");
        cycle++;
    }

    /// <summary>RA-20: the solver thread died mid-pass; the phase returns to Preview (as after a Stop) so nothing keeps reading "settling…".</summary>
    public void Fail()
    {
        phase = AccuratePhase.Preview;
        Volatile.Write(ref progress, 0);
        Volatile.Write(ref eta, double.NaN);
        detail = "";
    }

    public void Begin()
    {
        Volatile.Write(ref progress, 0);
        Volatile.Write(ref eta, double.NaN);
        detail = "";
        Volatile.Write(ref offWhy, "");
        phase = AccuratePhase.Settling;
    }

    /// <summary>One reading from the stepping loop: <paramref name="steps"/> taken of <paramref name="settleSteps"/>
    /// + the averaging steps (<paramref name="totalSteps"/> in all), at <paramref name="stepsPerSecond"/> (0 = not
    /// measured yet), with the finishing analysis expected to take <paramref name="finishSeconds"/> (NaN = unknown).</summary>
    public void Update(long steps, long settleSteps, long totalSteps, double stepsPerSecond, double finishSeconds)
    {
        if (phase is not (AccuratePhase.Settling or AccuratePhase.Averaging)) return;
        phase = steps < settleSteps ? AccuratePhase.Settling : AccuratePhase.Averaging;
        double f = totalSteps > 0 ? Math.Clamp((double)steps / totalSteps, 0, 1) : 0;
        Raise(f * (1 - FinishShare));
        long left = Math.Max(0, totalSteps - steps);
        etaPartial = double.IsNaN(finishSeconds);
        Volatile.Write(ref eta, stepsPerSecond > 1e-3 ? left / stepsPerSecond + (etaPartial ? 0 : finishSeconds) : double.NaN);
    }

    /// <summary>Share of the bar the settle (before any batch exists) gets in a convergence-driven pass.</summary>
    public const double SettleShare = 0.25;

    volatile string detail = "";
    /// <summary>"batch 6 · worst room ±7 % (aim 5 %)" while a convergence-driven pass averages; empty otherwise.</summary>
    public string Detail => detail;
    /// <summary>RU-01: a status line while the solver thread works on something the bar cannot show (the steady age solve at the end of the settle).</summary>
    public void Note(string text) { if (phase is AccuratePhase.Settling or AccuratePhase.Averaging) detail = text; }

    /// <summary>Fraction of the averaging stage done, from the batch count and the worst room's CI-to-target ratio
    /// (half-width over tolerance, 1 = converged): half-widths shrink as 1/sqrt(n), so a ratio r asks for about
    /// r^2 times the kept batches; before a ratio exists (under two kept batches) it is MinBatches.
    /// Pure, so --selfcheck can feed it.</summary>
    public static double ConvergedFraction(int batches, int transient, double worstRatio, int minBatches, out double batchesNeeded)
    {
        int kept = Math.Max(0, batches - transient);
        double needKept = kept >= 2 && double.IsFinite(worstRatio) ? Math.Max(minBatches, kept * Math.Min(worstRatio * worstRatio, 400)) : minBatches;
        // The truncation d grows with n, so "transient + needKept" understates it: the smallest N whose kept count N - d(N) reaches needKept
        // (and that passes the monitor's n gate).
        int total = ConvergenceMonitor.MinTotalBatches;
        while (total - ConvergenceMonitor.TruncationFor(total) < needKept) total++;
        batchesNeeded = Math.Max(batches, total);
        return batches <= 0 ? 0 : Math.Clamp(batches / batchesNeeded, 0, 1);
    }

    /// <summary>One reading from a convergence-driven pass (Core's <see cref="ConvergenceMonitor"/>): settle steps
    /// first, then batches. The bar is the settle share, then batches over the batches still needed (from the worst
    /// room's CI); it only ever grows. The ETA is the batches still needed at the measured batch time, capped by the
    /// time left to the hard cap, plus the finishing analysis; NaN ("…") until a CI exists to extrapolate from.</summary>
    public void UpdateConverging(long steps, long settleSteps, int batches, int transient, int minBatches, double worstRatio,
        double batchSeconds, double capLeftSeconds, double finishSeconds, string detailText = "")
    {
        if (phase is not (AccuratePhase.Settling or AccuratePhase.Averaging)) return;
        phase = steps < settleSteps ? AccuratePhase.Settling : AccuratePhase.Averaging;
        etaPartial = double.IsNaN(finishSeconds);
        detail = detailText;
        if (phase == AccuratePhase.Settling)
        {
            Raise(SettleShare * Math.Clamp(settleSteps > 0 ? (double)steps / settleSteps : 1, 0, 1));
            Volatile.Write(ref eta, double.NaN);
            return;
        }
        double f = ConvergedFraction(batches, transient, worstRatio, minBatches, out double needed);
        Raise(SettleShare + (1 - SettleShare - FinishShare) * f);
        bool known = batches - transient >= 2 && double.IsFinite(worstRatio) && double.IsFinite(batchSeconds) && batchSeconds > 0;
        Volatile.Write(ref eta, known
            ? Math.Min((needed - batches) * batchSeconds, Math.Max(0, capLeftSeconds)) + (etaPartial ? 0 : finishSeconds)
            : double.NaN);
    }

    public void Finishing(double finishSeconds)
    {
        if (!Running) return;
        phase = AccuratePhase.Finishing;
        Raise(1 - FinishShare);
        etaPartial = false;
        Volatile.Write(ref eta, finishSeconds);
    }

    public void Done()
    {
        if (!Running) return;
        Raise(1);
        Volatile.Write(ref eta, 0);
        edited = false;
        phase = AccuratePhase.Accurate;
    }

    /// <summary>No Accurate pass for this setup (a box is on, the pass failed): the result stays Preview.</summary>
    public void Off(string why)
    {
        Volatile.Write(ref offWhy, why);
        Volatile.Write(ref eta, double.NaN);
        phase = AccuratePhase.Off;
    }

    void Raise(double f) { if (f > Volatile.Read(ref progress)) Volatile.Write(ref progress, f); }

    /// <summary>The one line under the bar, in plain words.</summary>
    public static string StageText(AccuratePhase phase, double progress, double eta, string offWhy, bool partial = false, string detail = "") => phase switch
    {
        AccuratePhase.Preview => "Preview · directional, updates as you edit",
        AccuratePhase.Settling => $"Averaged result · letting the air settle… · {Left(eta, partial)}",
        AccuratePhase.Averaging => $"Averaged result · averaging · {progress * 100:0} % · {Left(eta, partial)}" + (detail.Length > 0 ? $" · {detail}" : ""),
        AccuratePhase.Finishing => $"Averaged result · working out the room numbers… · {Left(eta)}",
        AccuratePhase.Accurate => "Averaged result",   // S6-03: the tick and "settled" come from ResultStatus.StripText, never from the phase alone
        _ => offWhy.Length > 0 ? $"Preview · {offWhy}" : "Preview",
    };

    /// <summary>"about 40 s left", rounded so it does not flicker; "…" whenever there is no honest number.
    /// <paramref name="partial"/>: the number covers the stepping only, the room numbers come after it.</summary>
    public static string Left(double seconds, bool partial = false)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "…";
        string then = partial ? ", then the room numbers" : "";
        if (seconds < 5) return partial ? "room numbers next" : "a few seconds left";
        if (seconds < 60) return $"about {Math.Ceiling(seconds / 5) * 5:0} s{then}" + (partial ? "" : " left");
        return $"about {Math.Ceiling(seconds / 60):0} min{then}" + (partial ? "" : " left");
    }
}

/// <summary>The Accurate pass's cell budget: the startup tier's row, or a number typed in Expert, clamped so the
/// preview session (kept resident, idle) and the fine one both fit the solver card's memory.</summary>
public static class AccurateBudget
{
    /// <summary>Core's GPU bytes per cell, the one figure (P17-05): <see cref="LbmSolver.GpuBytesPerCell"/> isothermal, <see cref="LbmSolver.GpuBytesPerCellThermal"/> thermal.</summary>
    public const long BytesPerCell = LbmSolver.GpuBytesPerCell, ThermalBytesPerCell = LbmSolver.GpuBytesPerCellThermal;

    /// <summary>Share of dedicated VRAM both sessions may take together; Core itself refuses one grid above 75 %.</summary>
    public const double VramShare = 0.6;

    /// <summary>Never below this many times the Preview grid: a finer grid that is barely finer is not worth the wait.</summary>
    public const double MinOverPreview = 1.5;

    /// <summary>Largest Accurate grid this card holds next to the idle preview session; <see cref="LbmSolver.MaxCells"/> when VRAM is unknown (P17-04).</summary>
    public static int VramCap(double vramGb, int previewCells, bool thermal)
    {
        if (vramGb <= 0) return LbmSolver.MaxCells;
        long per = thermal ? ThermalBytesPerCell : BytesPerCell;
        double bytes = vramGb * 1024 * 1024 * 1024 * VramShare - (double)previewCells * per;
        return (int)Math.Clamp(bytes / per, 0, LbmSolver.MaxCells);
    }

    /// <summary>The cells the pass will use: typed (0 = auto, the tier's row) and clamped to the VRAM cap.</summary>
    public static (int Cells, bool Clamped) Resolve(int typed, int tierCells, double vramGb, int previewCells, bool thermal)
    {
        int want = typed > 0 ? typed : tierCells;
        int cap = VramCap(vramGb, previewCells, thermal);
        return want > cap ? (cap, true) : (want, false);
    }

    /// <summary>RQ-04: a typed budget (Expert box) raised to <see cref="MinOverPreview"/> x the Preview TARGET (the Compact-equivalent budget, not the cell count of a Full wind
    /// grid, which is about 10x larger), then held under the VRAM cap, which wins when the two disagree.</summary>
    public static int Typed(int wanted, int previewTarget, int cap, out string note)
    {
        note = "";
        int floor = (int)(previewTarget * MinOverPreview);
        if (wanted < floor) { wanted = floor; note = $"raised to {Fmt(floor)}, 1.5x the Preview budget"; }
        if (wanted > cap) { wanted = cap; note = $"clamped to {Fmt(cap)}, what the GPU memory holds"; }
        return wanted;
    }

    /// <summary>"5.2M" / "800k".</summary>
    public static string Fmt(int cells) => cells >= 1_000_000 ? $"{cells / 1e6:0.#}M" : $"{cells / 1e3:0}k";
}

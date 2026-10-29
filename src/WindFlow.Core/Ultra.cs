using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace WindFlow.Core;

// Ultra quality: the Standard pipeline (Refine + AveragedPass with the online age) on a grid as fine as the openings need and the GPU holds, run for hours,
// with checkpoints at batch boundaries so a crash, a sleep or a closed app does not lose it. Isothermal only. No physics constant is touched here.

/// <summary>What the user (or the headless run) decided about a saved run that matches.</summary>
public enum UltraDecision
{
    /// <summary>Headless default: resume a matching checkpoint if there is one.</summary>
    Auto,
    Resume,
    /// <summary>Delete the saved run and begin again (done when the new run starts).</summary>
    StartOver,
    /// <summary>Begin a new run and leave whatever is saved alone (nothing matched, or the user never chose).</summary>
    Skip,
}

/// <summary>The window's choice, made before the pass starts so the solver thread never waits on the UI. Valid only for the run whose hash it names.</summary>
public sealed record UltraChoice(string Hash, UltraDecision Decision, UltraManifest? Found);

/// <summary>Ultra's per-run settings, carried on <see cref="RunOptions.Ultra"/> (null = not an Ultra run).</summary>
public sealed class UltraSettings
{
    /// <summary>Where checkpoints go; null = %LOCALAPPDATA%\WindFlow\checkpoints.</summary>
    public string? CheckpointRoot;
    /// <summary>Ignore any checkpoint, delete the saved run and start over (headless --fresh).</summary>
    public bool Fresh;
    /// <summary>Run without writing checkpoints (headless --no-checkpoints).</summary>
    public bool NoCheckpoints;
    /// <summary>Wall seconds between checkpoints. Env WINDFLOW_ULTRA_CKPT_S (&gt;= 10) overrides, for tests.</summary>
    public double CheckpointIntervalSeconds = 1200;
    public UltraDecision Decision = UltraDecision.Auto;
    /// <summary>The hash the <see cref="Decision"/> was made for; empty = any (headless).</summary>
    public string DecisionHash = "";
    /// <summary>The validated checkpoint the decision refers to (the window checked it on a background task).</summary>
    public UltraManifest? Found;
    /// <summary>A GPU-fault retry resumes only a checkpoint written by this run.</summary>
    public bool OnlyOwnRun;
    public string RunId = Guid.NewGuid().ToString("N");
    /// <summary>A resumed run rebuilds exactly the saved grid at this cell size (0 = a new grid from the plan).</summary>
    public float FixedDx;
    /// <summary>One line of progress or news (headless prints it; the app logs it).</summary>
    public Action<string>? Log;
    /// <summary>Set by the driver: the checkpoint folder in use and what happened to it.</summary>
    public string? CheckpointDir;
    public string Note = "";
    public double EffectiveInterval
    {
        get
        {
            if (double.TryParse(System.Environment.GetEnvironmentVariable("WINDFLOW_ULTRA_CKPT_S"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double e) && e >= 10) return e;
            return CheckpointIntervalSeconds;
        }
    }
}

/// <summary>The Ultra run stopped for a reason the user should read (a second GPU fault, a refused resume). Not a GPU fault: no CPU restart follows.</summary>
public sealed class UltraStoppedException(string message) : Exception(message);

/// <summary>A checkpoint's manifest (the small JSON beside the binary file).</summary>
public sealed class UltraManifest
{
    public int Sequence { get; set; }
    public string Hash { get; set; } = "";
    public Dictionary<string, string> HashInputs { get; set; } = new();
    public string AppVersion { get; set; } = "";
    /// <summary>The build that wrote it (Core's module id and the app's version); a different build cannot resume it.</summary>
    public string BuildId { get; set; } = "";
    /// <summary>The run that wrote it (a GPU-fault retry resumes only its own).</summary>
    public string RunId { get; set; } = "";
    /// <summary>The grid, stored here and not in the hash so a restart rebuilds exactly this one.</summary>
    public float Dx { get; set; }
    public long Nx { get; set; }
    public long Ny { get; set; }
    public long Nz { get; set; }
    public float OriginX { get; set; }
    public float OriginY { get; set; }
    public float OriginZ { get; set; }
    public string Domain { get; set; } = "";
    public long Cells { get; set; }
    public long Step { get; set; }
    public double SimSeconds { get; set; }
    public double WallUsedSeconds { get; set; }
    public int Batches { get; set; }
    public string Phase { get; set; } = "";
    public string DataFile { get; set; } = "";
    public long DataBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string Written { get; set; } = "";
    public string DoneText => TimeSpan.FromSeconds(WallUsedSeconds).ToString(@"hh\:mm");
}

/// <summary>The Ultra plan: the grid, the memory, the time and the notes, all decided before the run starts. Built by <see cref="Ultra.Plan"/> (or <see cref="Ultra.PlanResume"/>).</summary>
public sealed class UltraPlan
{
    public bool Feasible = true;
    public string Refusal = "";
    public float Dx, TargetDx, StandardDx, MemoryFitDx;
    public float? NarrowestOpening;
    public double CellsAcross;
    public bool TargetReached;
    public DomainMode Domain = DomainMode.Full;
    public long Cells;
    public long Nx, Ny, Nz;
    /// <summary>The cell budget to hand Voxelizer.Build so it lands on <see cref="Dx"/>.</summary>
    public int BuildCells;
    public long CellCap;
    public long GpuBytes, VramBytes;
    public long CheckpointBytes;
    /// <summary>Free space on the checkpoint drive (-1 = unknown), what Ultra needs there (2 checkpoints plus 1 GB), and whether it is short.</summary>
    public long DiskFreeBytes = -1, DiskNeededBytes;
    public bool DiskShort => DiskFreeBytes >= 0 && DiskFreeBytes < DiskNeededBytes;
    public double EstimatedHours, CapHours;
    public double Mlups;
    public bool MlupsMeasured;
    /// <summary>The startup speed probe is still running: the estimate line says so until it finishes.</summary>
    public bool MeasuringSpeed;
    /// <summary>The memory budget came from a measured free-VRAM figure (the dialog says it keeps 1.5 GB free), not the 0.6 fallback.</summary>
    public bool FreeKnown;
    public string GpuName = "";
    public string DomainNote = "";
    /// <summary>A saved run's grid: the plan rebuilds exactly that one.</summary>
    public bool Resuming;
    public UltraManifest? Manifest;
    public List<string> Notes = [];

    static double Gib(long bytes) => bytes / 1073741824.0;

    /// <summary>The estimated-time line (the window rewrites it when the speed probe finishes).</summary>
    public string EstimateText
    {
        get
        {
            if (MeasuringSpeed) return $"Estimated time on {GpuName}: measuring GPU speed…";
            string rate = MlupsMeasured ? $"measured {Mlups:#,0} MLUPS" : $"{Mlups:#,0} MLUPS assumed, not measured on this GPU";
            return $"Estimated time on {GpuName}: about {EstimatedHours:0.#} h ({rate}); this is an estimate. The run stops earlier if every room settles, and at the {CapHours:0.##} h limit at the latest.";
        }
    }

    /// <summary>The compact outdoor area stands in for the Full one (it did not fit): flows can read high.</summary>
    public bool CompactFallback => Domain == DomainMode.Compact && DomainNote.Contains("Full wind domain does not fit", StringComparison.Ordinal);

    string Friendly => GpuName.Replace("NVIDIA GeForce ", "", StringComparison.Ordinal).Replace("NVIDIA ", "", StringComparison.Ordinal);

    /// <summary>The dialog's big line: "About 4 h 20 min on your RTX 5070 Ti", or "measuring GPU speed…" until the probe is in.</summary>
    public string Headline
    {
        get
        {
            if (MeasuringSpeed) return "Measuring GPU speed…";
            var t = TimeSpan.FromHours(Math.Max(0.0, EstimatedHours));
            string time = t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";
            return $"About {time} on your {Friendly}";
        }
    }

    /// <summary>The three short lines under the headline, plus the compact-fallback line and the disk warning when they apply.</summary>
    public string[] ShortLines()
    {
        var l = new List<string>();
        string finer = StandardDx > 0 && StandardDx / Dx >= 1.05f ? $"{StandardDx / Dx:0.#}× finer than Standard" : "the same as Standard";
        if (Resuming) l.Add($"Resuming the saved run ({Manifest?.DoneText} done) on its own grid: {Dx * 1000:0.#} mm cells.");
        else if (NarrowestOpening != null)
            l.Add($"Grid: {Dx * 1000:0} mm cells, {finer}. The narrowest open window gets {CellsAcross:0} cells across " + (TargetReached ? $"({Ultra.CellsAcrossTarget} is the target)." : $"({Ultra.CellsAcrossTarget} is the target)."));
        else l.Add($"Grid: {Dx * 1000:0} mm cells, {finer}. No open windows or doors, so the cell size is half of Standard's.");
        l.Add($"Uses {Gib(GpuBytes):0.0} GB of {Gib(VramBytes):0.#} GB GPU memory ({(FreeKnown ? "keeps 1.5 GB free for Windows and other apps" : "keeps 40 % free")}) · needs {Gib(DiskNeededBytes):0} GB free disk for saved progress.");
        l.Add("Stops early when every room settles. Accuracy not separately validated.");
        if (CompactFallback) l.Add("Uses the compact outdoor area (Full doesn't fit), which can read flows high.");
        return l.ToArray();
    }

    /// <summary>The disk warning (empty when there is room): shown outside Details, and Start is disabled.</summary>
    public string DiskWarning => DiskShort ? $"Not enough disk space: {Gib(DiskNeededBytes):0.0} GB free is needed for saved progress (two checkpoints plus 1 GB), {Gib(DiskFreeBytes):0.0} GB is free. Free some space to start Ultra." : "";

    /// <summary>Everything else, for the collapsed Details.</summary>
    public string[] DetailLines()
    {
        var l = new List<string>();
        l.Add($"Cells: {Cells / 1e6:0.##}M ({Nx} x {Ny} x {Nz}), cell size {Dx * 1000:0.#} mm" + (StandardDx > 0 ? $" (Standard {StandardDx * 1000:0.#} mm)" : "") + $", {Domain} domain.");
        if (NarrowestOpening is float n && !Resuming) l.Add($"Narrowest open opening {n:0.00} m: target {TargetDx * 1000:0} mm for {Ultra.CellsAcrossTarget} cells across" + (TargetReached ? ", reached." : $"; your GPU fits {Dx * 1000:0} mm."));
        if (DomainNote.Length > 0) l.Add(DomainNote);
        string rate = MeasuringSpeed ? "being measured" : MlupsMeasured ? $"measured {Mlups:#,0} MLUPS" : $"{Mlups:#,0} MLUPS assumed, not measured on this GPU";
        l.Add($"Speed: {rate}. This is an estimate; the run stops at the {CapHours:0.##} h limit at the latest.");
        l.Add($"Saved progress: about {Gib(CheckpointBytes):0.0} GB per file, up to 3 at a time, in %LOCALAPPDATA%\\WindFlow\\checkpoints." + (DiskFreeBytes >= 0 ? $" Free on that drive: {Gib(DiskFreeBytes):0.0} GB." : ""));
        foreach (var note in Notes) l.Add(note);
        l.Add(Ultra.GridSentence);
        return l.ToArray();
    }

    public string[] Lines()
    {
        var l = new List<string>();
        if (Resuming && Manifest is { } m)
            l.Add($"Resuming the saved run: {m.DoneText} done, step {m.Step}, {m.Batches} batches, on the grid it was saved with.");
        else if (NarrowestOpening is float nOpen)
            l.Add($"Narrowest open opening: {nOpen:0.00} m. Target {TargetDx * 1000:0} mm ({Ultra.CellsAcrossTarget} cells across). " +
                  (TargetReached ? $"Target reached: {CellsAcross:0} cells across." : $"Your GPU fits: {Dx * 1000:0} mm ({CellsAcross:0} cells across)."));
        else
            l.Add($"No open openings: Standard's cell size / 2 = {TargetDx * 1000:0} mm. " + (TargetReached ? "Target reached." : $"Your GPU fits: {Dx * 1000:0} mm."));
        l.Add($"Ultra grid: {Cells / 1e6:0.##}M cells ({Nx} x {Ny} x {Nz}), cell size {Dx * 1000:0.#} mm" + (StandardDx > 0 ? $" (Standard: {StandardDx * 1000:0.#} mm)" : "") + $", domain {Domain}.");
        if (DomainNote.Length > 0) l.Add(DomainNote);
        l.Add($"GPU memory the run will use: about {Gib(GpuBytes):0.0} GB of {Gib(VramBytes):0.0} GB on {GpuName} ({(FreeKnown ? "free memory measured now, 1.5 GB kept free" : "free memory unknown, 40 % kept free")}).");
        l.Add(EstimateText);
        l.Add($"Disk for checkpoints: about {Gib(CheckpointBytes):0.0} GB each, up to 3 at a time ({Gib(3 * CheckpointBytes):0.0} GB), in %LOCALAPPDATA%\\WindFlow\\checkpoints." +
              (DiskFreeBytes >= 0 ? $" Free on that drive: {Gib(DiskFreeBytes):0.0} GB." : ""));
        if (DiskShort) l.Add($"NOT ENOUGH DISK SPACE: Ultra needs {Gib(DiskNeededBytes):0.0} GB free there (two checkpoints plus 1 GB), so it cannot start. Free some space, or run headless with --no-checkpoints.");
        foreach (var note in Notes) l.Add(note);
        l.Add(Ultra.GridSentence);
        l.Add(Ultra.AccuracyDisclosure);
        return l.ToArray();
    }
}

public static class Ultra
{
    public const float MinDx = 0.025f;
    public const int CellsAcrossTarget = 16;
    /// <summary>Share of dedicated VRAM an Ultra grid may take; the Core guard refuses above 75 %, so this is 20 % under it. No Preview session is resident in a headless run.</summary>
    public const double VramShare = 0.6;
    public static readonly int[] CapHourChoices = [6, 12, 20, 24];
    public const int DefaultCapHours = 20;
    public const double RawClockFactor = 10;
    public const double FallbackMlups = HighAccuracy.MlupsEstimate;
    public const string AccuracyDisclosure = "Ultra uses a finer grid; its accuracy has not been separately validated.";
    public const string GridSentence = "At 16 cells across, the measured opening flow is within about 1-3 % of its target, so Ultra does not go finer.";
    public const string ToggleTooltipThermal = "Ultra runs without thermal (wind only)";

    /// <summary>The label wherever the result quality is named.</summary>
    public static string ResultLabel(float dxMetres) => $"Ultra: finest grid this GPU fits (Dx {dxMetres * 1000:0.#} mm), long run; accuracy not separately validated.";

    /// <summary>Width of the narrowest OPEN opening, using each opening's shorter side; null when none is open.</summary>
    public static float? NarrowestOpen(BuildingModel model)
    {
        float? best = null;
        foreach (var o in model.Openings)
        {
            if (!o.IsOpen) continue;
            float w = MathF.Min(o.Width, o.Height);
            if (w > 1e-3f && (best == null || w < best)) best = w;
        }
        return best;
    }

    /// <summary>The pure Dx rule: the target (narrowest / 16, else Standard / 2) clamped to [MinDx, 1], then never coarser than the memory allows
    /// (<paramref name="memoryFitDx"/> is the finest Dx that fits), then never coarser than Standard's Dx.</summary>
    public static float ChooseDx(float? narrowest, float standardDx, float memoryFitDx, out float target)
    {
        target = Math.Clamp(narrowest is float n ? n / CellsAcrossTarget : standardDx / 2f, MinDx, 1f);
        float dx = MathF.Max(target, memoryFitDx);
        return MathF.Min(dx, standardDx);
    }

    /// <summary>Memory kept free for Windows and other apps when the free figure is known, and the most of the card an Ultra grid may take (Core's guard is 0.88 for Ultra, 0.75 otherwise).</summary>
    public const long HeadroomBytes = 3L << 29;   // 1.5 GiB
    public const double MaxShare = 0.85;
    /// <summary>The cell count an out-of-memory retry at Start falls back to, as a share of the planned count.</summary>
    public const double OomRetryShare = 0.85;
    /// <summary>The cell size that gives <see cref="OomRetryShare"/> of the cells (cells scale as 1 / Dx cubed).</summary>
    public static float OomRetryDx(float dx) => (float)(dx / Math.Cbrt(OomRetryShare));
    public static long OomRetryCells(long cells) => (long)(cells * OomRetryShare);

    /// <summary>Largest cell count the GPU holds for Ultra (isothermal, the online age included in Core's bytes per cell).
    /// With a measured <paramref name="freeBytes"/> (free VRAM now, whatever is resident included): min(free - 1.5 GiB, 0.85 x total). Without one: 0.6 x total after the Preview session's share.</summary>
    public static long CellCap(long vramBytes, int previewCells, long freeBytes = 0)
    {
        if (vramBytes <= 0) return LbmSolver.MaxCells;
        double bytes = freeBytes > 0
            ? Math.Min((double)freeBytes - HeadroomBytes, MaxShare * vramBytes)
            : vramBytes * VramShare - (double)previewCells * LbmSolver.GpuBytesPerCell;
        return (long)Math.Clamp(bytes / LbmSolver.GpuBytesPerCell, 0, LbmSolver.MaxCells);
    }

    /// <summary>Bytes of one checkpoint per cell: populations 76, age field 4 + mask 1, the warm age 4, two batch-mean windows of six floats.</summary>
    public const long CheckpointBytesPerCell = 76 + 5 + 4 + 2 * 24;

    /// <summary>The smallest Dx (not below <see cref="MinDx"/>) whose cell count is within <paramref name="cap"/>, by bisection on a count function that falls as Dx grows.</summary>
    public static float FinestDxWithin(Func<float, long> cells, long cap)
    {
        if (cells(1f) > cap) return 1f;
        if (cells(MinDx) <= cap) return MinDx;
        float lo = MinDx, hi = 1f;   // cells(lo) > cap, cells(hi) <= cap
        for (int i = 0; i < 48; i++)
        {
            float mid = MathF.Sqrt(lo * hi);
            if (cells(mid) <= cap) hi = mid; else lo = mid;
        }
        return hi;
    }

    static string ShortGpu(string name) => name.Contains('·') ? name[(name.LastIndexOf('·') + 1)..].Trim() : name;

    /// <param name="standardCells">The Standard cell budget (the app's Accurate tier row, or --cells), which fixes Standard's Dx.</param>
    /// <param name="vramBytes">Dedicated GPU memory (0 = no GPU: Ultra is refused).</param>
    public static UltraPlan Plan(BuildingModel model, Vector2 wind, int standardCells, long vramBytes, int previewCells, double mlups, bool mlupsMeasured,
        string gpuName, double capHours = DefaultCapHours, string? checkpointRoot = null, bool measuringSpeed = false, long freeVramBytes = 0)
    {
        var plan = new UltraPlan { VramBytes = vramBytes, GpuName = ShortGpu(gpuName), CapHours = capHours, MlupsMeasured = mlupsMeasured && mlups > 0, Mlups = mlups > 0 ? mlups : FallbackMlups, MeasuringSpeed = measuringSpeed, FreeKnown = freeVramBytes > 0 };
        if (vramBytes <= 0) { plan.Feasible = false; plan.Refusal = "Ultra needs a GPU with a known amount of memory."; return plan; }
        var (_, _, _, extW, extD, height) = Voxelizer.AutoDomain(model);
        double compactVol = (double)extW * extD * height;
        plan.StandardDx = Math.Clamp((float)Math.Cbrt(compactVol / Math.Max(1, standardCells)), 0.1f, 1f);
        plan.NarrowestOpening = NarrowestOpen(model);
        plan.CellCap = CellCap(vramBytes, previewCells, freeVramBytes);

        long CompactCells(float dx) => (long)Math.Ceiling(extW / dx) * (long)Math.Ceiling(extD / dx) * ((long)Math.Ceiling(height / dx) + 1);
        bool fullPossible = wind.Length() >= 1e-3f;
        long FullCells(float dx) => Voxelizer.PlanFull(model, wind, dx, 0).Cells;

        float fitFull = fullPossible ? FinestDxWithin(FullCells, plan.CellCap) : float.MaxValue;
        plan.Domain = DomainMode.Full;
        float dxChosen;
        if (fullPossible && fitFull <= plan.StandardDx)
        {
            plan.MemoryFitDx = fitFull;
            dxChosen = ChooseDx(plan.NarrowestOpening, plan.StandardDx, fitFull, out plan.TargetDx);
        }
        else
        {
            plan.Domain = DomainMode.Compact;
            float fitCompact = FinestDxWithin(CompactCells, plan.CellCap);
            plan.MemoryFitDx = fitCompact;
            dxChosen = ChooseDx(plan.NarrowestOpening, plan.StandardDx, fitCompact, out plan.TargetDx);
            plan.DomainNote = fullPossible
                ? $"The Full wind domain does not fit this GPU at a cell size finer than Standard's ({plan.StandardDx * 1000:0.#} mm); the Compact domain is used. Its blockage bias (about 11-14 % per room; an older run read 8-33 %) can exceed the opening-flow error Ultra is meant to fix."
                : "No wind: the Compact domain is used.";
            if (fitCompact > plan.StandardDx * 1.0001f) { plan.Feasible = false; plan.Refusal = "This GPU cannot hold a grid finer than Standard's, so Ultra would not be any finer."; }
        }
        plan.Dx = dxChosen;
        plan.TargetReached = plan.Dx <= plan.TargetDx * 1.001f;
        plan.CellsAcross = plan.NarrowestOpening is float nw ? nw / plan.Dx : double.NaN;
        if (plan.Dx >= plan.StandardDx * 0.999f && plan.Feasible) plan.Notes.Add("Ultra's cell size equals Standard's for this model: there is no finer grid to be had, so Ultra would only run longer.");
        Dimensions(plan, model, wind, extW, extD, height);
        Complete(plan, model, wind, compactVol, capHours, checkpointRoot);
        return plan;
    }

    /// <summary>The plan for resuming a saved run: its own grid and domain, exactly. Infeasible (with the reason) when that grid no longer fits in GPU memory.</summary>
    public static UltraPlan PlanResume(BuildingModel model, Vector2 wind, UltraManifest m, long vramBytes, int previewCells, double mlups, bool mlupsMeasured,
        string gpuName, double capHours = DefaultCapHours, string? checkpointRoot = null, bool measuringSpeed = false, long freeVramBytes = 0)
    {
        var plan = new UltraPlan
        {
            FreeKnown = freeVramBytes > 0,
            VramBytes = vramBytes, GpuName = ShortGpu(gpuName), CapHours = capHours, MlupsMeasured = mlupsMeasured && mlups > 0, Mlups = mlups > 0 ? mlups : FallbackMlups,
            MeasuringSpeed = measuringSpeed, Resuming = true, Manifest = m, Dx = m.Dx, Cells = m.Cells, Nx = m.Nx, Ny = m.Ny, Nz = m.Nz,
        };
        plan.Domain = Enum.TryParse(m.Domain, out DomainMode dm) ? dm : DomainMode.Compact;
        plan.CellCap = CellCap(vramBytes, previewCells, freeVramBytes);
        var (_, _, _, extW, extD, height) = Voxelizer.AutoDomain(model);
        double compactVol = (double)extW * extD * height;
        if (vramBytes <= 0) { plan.Feasible = false; plan.Refusal = "Ultra needs a GPU with a known amount of memory."; }
        else if (m.Cells > plan.CellCap)
        {
            plan.Feasible = false;
            plan.Refusal = $"The saved run's grid ({m.Cells / 1e6:0.#}M cells, {m.Dx * 1000:0.#} mm) no longer fits in this GPU's memory ({plan.CellCap / 1e6:0.#}M cells available now), so it cannot be resumed. Choose Start over.";
        }
        Complete(plan, model, wind, compactVol, capHours, checkpointRoot);
        return plan;
    }

    static void Dimensions(UltraPlan plan, BuildingModel model, Vector2 wind, float extW, float extD, float height)
    {
        if (plan.Domain == DomainMode.Full)
        {
            var p = Voxelizer.PlanFull(model, wind, plan.Dx, 0);
            var (min, max, _, _, _, _) = Voxelizer.AutoDomain(model);
            float W = max.X - min.X, D = max.Y - min.Y;
            plan.Nx = (long)Math.Ceiling((W + p.West + p.East) / plan.Dx);
            plan.Ny = (long)Math.Ceiling((D + p.South + p.North) / plan.Dx);
            plan.Cells = p.Cells; plan.Nz = plan.Cells / Math.Max(1, plan.Nx * plan.Ny);
        }
        else
        {
            plan.Nx = (long)Math.Ceiling(extW / plan.Dx); plan.Ny = (long)Math.Ceiling(extD / plan.Dx);
            plan.Nz = (long)Math.Ceiling(height / plan.Dx) + 1; plan.Cells = plan.Nx * plan.Ny * plan.Nz;
        }
    }

    static void Complete(UltraPlan plan, BuildingModel model, Vector2 wind, double compactVol, double capHours, string? checkpointRoot)
    {
        plan.BuildCells = (int)Math.Min(int.MaxValue, Math.Ceiling(compactVol / Math.Pow(plan.Dx, 3)));
        plan.GpuBytes = plan.Cells * LbmSolver.GpuBytesPerCell;
        plan.CheckpointBytes = plan.Cells * CheckpointBytesPerCell;
        plan.DiskNeededBytes = 2 * plan.CheckpointBytes + (1L << 30);
        try
        {
            string root = Path.GetFullPath(checkpointRoot ?? UltraCheckpoint.DefaultRoot);
            plan.DiskFreeBytes = new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
        }
        catch (Exception) { plan.DiskFreeBytes = -1; }
        plan.EstimatedHours = EstimateHours(model, wind, plan.Dx, plan.Nx, plan.Ny, plan.Cells, plan.Mlups, capHours);
    }

    /// <summary>Hours estimate: the settle (2 x the warm-up of one flow-through of the longest side at ULat 0.08) plus 160 batches (the low end of what rooms needed), each
    /// two building flow-throughs of simulated time, at dt = 0.08 Dx / uMax with uMax taken as 1.6 x the wind speed; steps x cells / MLUPS; never above the cap.</summary>
    public static double EstimateHours(BuildingModel model, Vector2 wind, float dx, long nx, long ny, long cells, double mlups, double capHours)
    {
        double uMax = Math.Max(1.6 * wind.Length(), 0.5);
        double dt = 0.08 * dx / uMax;
        double warm = Math.Ceiling(Math.Max(nx, ny) / 0.08);
        double ft = AveragedPass.FlowThroughSeconds(model, wind);
        double batchSteps = 2 * ft / dt;
        double steps = 2 * warm + 160 * batchSteps;
        double hours = steps * cells / (mlups * 1e6) / 3600.0;
        return Math.Min(hours, capHours);
    }

    /// <summary>Planning plus the resolved <see cref="Ultra.ChooseDx"/> inputs as one text block for the log.</summary>
    public static string Describe(UltraPlan p) => string.Join(System.Environment.NewLine, p.Lines());
}

/// <summary>Keeps the PC awake during a run: SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED) on the calling thread, cleared on dispose. The display and every power setting are left alone.</summary>
public sealed class UltraKeepAwake : IDisposable
{
    const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    readonly bool _on;
    public UltraKeepAwake()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { _on = SetThreadExecutionState(EsContinuous | EsSystemRequired) != 0; } catch (Exception) { _on = false; }
    }
    public bool Active => _on;
    public void Dispose()
    {
        if (!_on) return;
        try { SetThreadExecutionState(EsContinuous); } catch (Exception) { /* nothing to undo */ }
    }
}

/// <summary>What <see cref="UltraCheckpoint.Probe"/> found for a run: its hash and folder, and the newest valid checkpoint (null with the reason).</summary>
public sealed record UltraProbe(string Hash, string Dir, Dictionary<string, string> Inputs, UltraManifest? Found, string Why);

/// <summary>Checkpoint files for one Ultra run: <c>ckpt-N.bin</c> (state) and <c>ckpt-N.json</c> (manifest) in <c>&lt;root&gt;\&lt;hash&gt;\</c>. Written to a temp name and renamed;
/// the last two are kept. Nothing here throws on a missing, locked or unreadable folder except <see cref="Write"/>, whose caller handles a full disk.</summary>
public static class UltraCheckpoint
{
    public const int Keep = 2;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);
    public static string DefaultRoot => System.Environment.GetEnvironmentVariable("WINDFLOW_CHECKPOINT_DIR")
        ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "WindFlow", "checkpoints");
    public static string Root(UltraSettings u) => u.CheckpointRoot ?? DefaultRoot;

    public static string AppVersion => typeof(Ultra).Assembly.GetName().Version?.ToString() ?? "0";

    /// <summary>The build identity: Core's module id (new on every build, so conservative) and the running app's informational version. A checkpoint from another build is not resumed.</summary>
    public static string BuildId { get; } = typeof(Ultra).Module.ModuleVersionId.ToString("N") + "/" +
        (System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? AppVersion);

    /// <summary>The hash of what makes a checkpoint valid for a run: the model, the wind, the physics options, thermal off, the quality and the build. NOT the grid or the domain:
    /// those are stored in the manifest, and a restart rebuilds exactly that grid (so a GPU with less free memory today still resumes it, or says it cannot).</summary>
    public static (string Hash, Dictionary<string, string> Inputs) HashOf(BuildingModel model, Vector2 wind, RunOptions o)
    {
        var inputs = new Dictionary<string, string>
        {
            ["model"] = Hex(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(model.ToJson()))),
            ["wind"] = FormattableString.Invariant($"{wind.X:R},{wind.Y:R}"),
            ["quality"] = "ultra",
            ["thermal"] = o.Thermal.ToString(),
            ["physics"] = FormattableString.Invariant($"profile {o.WindProfileExponent:R} floor {o.ViscosityFloor:R} opening {o.OpeningModel} wall {o.WallModel} tuPerSource {o.SupplyTuPerSource} icSeed {o.IcSeed} perturb {o.SupplyPerturbation:R}"),
            ["build"] = BuildId,
        };
        var text = string.Join("\n", inputs.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value));
        return (Hex(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16], inputs);
    }

    static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    sealed class HashingStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { hash.AppendData(buffer, offset, count); inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { hash.AppendData(buffer); inner.Write(buffer); }
    }

    /// <summary>Writes the checkpoint described by <paramref name="meta"/> (its hash, run, grid and progress filled in; size, checksum and file name are set here): the body streams
    /// through a hashing, buffered file stream into a temp file, which is renamed once complete; then the manifest the same way; then older ones are pruned.
    /// On any failure the temp files are deleted and the exception goes to the caller (a full disk is an <see cref="IOException"/>).</summary>
    public static UltraManifest Write(string dir, int seq, UltraManifest meta, Action<BinaryWriter> body)
    {
        Directory.CreateDirectory(dir);
        string data = $"ckpt-{seq}.bin", tmp = Path.Combine(dir, data + ".tmp"), mtmp = Path.Combine(dir, $"ckpt-{seq}.json.tmp");
        try
        {
            using var hash256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size;
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var hs = new HashingStream(fs, hash256);
                using var w = new BinaryWriter(hs, System.Text.Encoding.UTF8, leaveOpen: true);
                w.Write(0x57464355); w.Write(2); w.Write(meta.Hash);
                body(w);
                w.Flush(); fs.Flush(true);
                size = fs.Length;
            }
            meta.Sequence = seq; meta.AppVersion = AppVersion; meta.BuildId = BuildId; meta.DataFile = data; meta.DataBytes = size;
            meta.Sha256 = Hex(hash256.GetHashAndReset()); meta.Written = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            File.Move(tmp, Path.Combine(dir, data), true);
            File.WriteAllText(mtmp, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(mtmp, Path.Combine(dir, $"ckpt-{seq}.json"), true);
        }
        catch
        {
            foreach (var t in new[] { tmp, mtmp }) try { File.Delete(t); } catch (Exception) { /* nothing more to do */ }
            throw;
        }
        Prune(dir);
        return meta;
    }

    static int SeqOf(string path) => int.TryParse(Path.GetFileNameWithoutExtension(path).Replace("ckpt-", ""), out int n) ? n : -1;

    static void Prune(string dir)
    {
        try
        {
            var seqs = Directory.GetFiles(dir, "ckpt-*.json").Select(SeqOf).Where(n => n >= 0).OrderByDescending(n => n).ToList();
            foreach (int n in seqs.Skip(Keep))
                foreach (var ext in new[] { "json", "bin" }) try { File.Delete(Path.Combine(dir, $"ckpt-{n}.{ext}")); } catch (Exception) { /* in use: next time */ }
            foreach (var t in Directory.GetFiles(dir, "*.tmp")) try { File.Delete(t); } catch (Exception) { }
        }
        catch (Exception) { /* a folder we cannot list is left as it is */ }
    }

    public static int NextSequence(string dir)
    {
        try { return !Directory.Exists(dir) ? 1 : Directory.GetFiles(dir, "ckpt-*.json").Select(SeqOf).DefaultIfEmpty(0).Max() + 1; }
        catch (Exception) { return 1; }
    }

    /// <summary>The newest checkpoint whose manifest, size and checksum are intact and whose hash (and, with <paramref name="onlyRunId"/>, run) match; otherwise null and
    /// <paramref name="why"/> says what was wrong. IO and access errors are "invalid" with a reason, never an exception. The checksum pass honours <paramref name="ct"/>.</summary>
    public static UltraManifest? FindValid(string dir, string hash, out string why, CancellationToken ct = default, string? onlyRunId = null)
    {
        why = "no checkpoint found";
        try
        {
            if (!Directory.Exists(dir)) return null;
            foreach (int n in Directory.GetFiles(dir, "ckpt-*.json").Select(SeqOf).Where(n => n >= 0).OrderByDescending(n => n))
            {
                ct.ThrowIfCancellationRequested();
                string mp = Path.Combine(dir, $"ckpt-{n}.json");
                UltraManifest? m;
                try { m = JsonSerializer.Deserialize<UltraManifest>(File.ReadAllText(mp)); }
                catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { why = $"checkpoint {n}: the manifest is unreadable"; continue; }
                if (m == null) { why = $"checkpoint {n}: the manifest is empty"; continue; }
                if (m.Hash != hash) { why = $"checkpoint {n}: it is for a different model or settings"; continue; }
                if (m.BuildId != BuildId) { why = $"checkpoint {n}: it was written by another build of WindFlow"; continue; }
                if (onlyRunId != null && m.RunId != onlyRunId) { why = $"checkpoint {n}: it belongs to an earlier run"; continue; }
                string bp = Path.Combine(dir, m.DataFile);
                try
                {
                    if (!File.Exists(bp)) { why = $"checkpoint {n}: the data file is missing"; continue; }
                    if (new FileInfo(bp).Length != m.DataBytes) { why = $"checkpoint {n}: the data file has the wrong size (corrupt)"; continue; }
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using (var fs = new FileStream(bp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                    {
                        var buf = new byte[1 << 22]; int read;
                        while ((read = fs.Read(buf, 0, buf.Length)) > 0) { ct.ThrowIfCancellationRequested(); sha.AppendData(buf, 0, read); }
                    }
                    if (Hex(sha.GetHashAndReset()) != m.Sha256) { why = $"checkpoint {n}: the checksum does not match (corrupt)"; continue; }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { why = $"checkpoint {n}: the file cannot be read ({e.Message})"; continue; }
                why = "";
                return m;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { why = $"the checkpoint folder cannot be read ({e.Message})"; }
        return null;
    }

    /// <summary>Opens a checkpoint's state after its header; <paramref name="hash"/> is checked again.</summary>
    public static BinaryReader Open(string dir, UltraManifest m, string hash, out FileStream fs)
    {
        fs = new FileStream(Path.Combine(dir, m.DataFile), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var r = new BinaryReader(fs);
        if (r.ReadInt32() != 0x57464355 || r.ReadInt32() != 2 || r.ReadString() != hash) throw new InvalidDataException("the checkpoint header does not match");
        return r;
    }

    /// <summary>Removes a run's folder. Never throws: a failure is logged and the folder is left for the age cleanup.</summary>
    public static void Delete(string dir, Action<string>? log = null)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception e)
        {
            string msg = $"could not delete the checkpoint folder {dir} ({e.GetType().Name}: {e.Message}); it will be cleaned up later";
            try { log?.Invoke(msg); Console.Error.WriteLine("[ultra] " + msg); } catch (Exception) { /* a logger must not break the delete */ }
        }
    }

    /// <summary>At Ultra start: removes checkpoint folders whose newest manifest is older than 14 days or was written by another build, and logs each. Touches only subfolders of
    /// <paramref name="root"/> that hold ckpt-*.json, and nothing else. Never throws.</summary>
    public static void Cleanup(string root, Action<string>? log)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.GetDirectories(root))
            {
                try
                {
                    var jsons = Directory.GetFiles(dir, "ckpt-*.json");
                    if (jsons.Length == 0) continue;
                    var newest = jsons.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
                    string why = "";
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(newest) > MaxAge) why = "older than 14 days";
                    else
                    {
                        UltraManifest? m = null;
                        try { m = JsonSerializer.Deserialize<UltraManifest>(File.ReadAllText(newest)); } catch (Exception) { /* unreadable: left alone */ }
                        if (m != null && m.BuildId != BuildId) why = "written by another build of WindFlow";
                    }
                    if (why.Length == 0) continue;
                    log?.Invoke($"deleting the old saved run {Path.GetFileName(dir)} ({why})");
                    Delete(dir, log);
                }
                catch (Exception) { /* one folder must not stop the rest */ }
            }
        }
        catch (Exception) { /* a root we cannot list is left alone */ }
    }

    /// <summary>The checkpoint question for a run, answered without a grid: cleans up old runs, then finds the newest valid matching checkpoint (honours <paramref name="ct"/>).</summary>
    public static UltraProbe Probe(BuildingModel model, Vector2 wind, RunOptions o, UltraSettings u, CancellationToken ct = default)
    {
        var root = Root(u);
        Cleanup(root, u.Log);
        var (hash, inputs) = HashOf(model, wind, o);
        string dir = Path.Combine(root, hash);
        var found = FindValid(dir, hash, out string why, ct, u.OnlyOwnRun ? u.RunId : null);
        return new UltraProbe(hash, dir, inputs, found, why);
    }
}

/// <summary>One Ultra run's checkpoint bookkeeping, shared by the headless <see cref="UltraDriver"/> and the app's LiveSolver: the hash and folder, resume, the timer, writing.
/// A run writes only when its session was started fresh or fully restored in this run (<see cref="Trusted"/>); a failed resume turns writing off for good.</summary>
public sealed class UltraRun
{
    public readonly string Dir, Hash;
    readonly Dictionary<string, string> _inputs;
    readonly UltraSettings _u;
    readonly Action<string> _log;
    readonly VoxelGrid _grid;
    readonly string _domain;
    readonly Stopwatch _since = Stopwatch.StartNew();
    int _seq, _failures;
    /// <summary>False when the session holds state Ultra does not serialise (the reason is in <see cref="Refusal"/>), checkpoints were switched off, or writing failed three times.</summary>
    public bool Enabled { get; private set; }
    public string Refusal { get; private set; } = "";
    /// <summary>The session is fresh or was restored completely: only then may it be written.</summary>
    public bool Trusted { get; private set; } = true;

    public UltraRun(VoxelGrid grid, BuildingModel model, Vector2 wind, RunOptions o, UltraSettings u, LbmSolver.LbmSession session, Action<string> log)
    {
        _u = u; _log = log; _grid = grid; _domain = o.DomainMode.ToString();
        (Hash, _inputs) = UltraCheckpoint.HashOf(model, wind, o);
        Dir = Path.Combine(UltraCheckpoint.Root(u), Hash);
        u.CheckpointDir = Dir;
        Refusal = session.CheckpointRefusal;
        Enabled = Refusal.Length == 0 && !u.NoCheckpoints;
        if (u.NoCheckpoints) { u.Note = "checkpoints are off (--no-checkpoints): the run cannot be resumed"; log(u.Note); }
        else if (Refusal.Length > 0) { u.Note = $"resume not supported with {Refusal}; the run will start over if it is interrupted"; log(u.Note); }
        _seq = UltraCheckpoint.NextSequence(Dir);
    }

    /// <summary>Loads the chosen checkpoint into the fresh session and pass; returns the wall seconds the cap has already counted (0 when nothing was loaded). A load that fails
    /// switches writing off before anything can be saved from the half-loaded session, then stops the run with the reason (the checkpoint stays).</summary>
    public double TryResume(LbmSolver.LbmSession session, AveragedPass pass, VoxelGrid grid, CancellationToken ct = default)
    {
        if (!Enabled) return 0;
        bool mine = _u.DecisionHash.Length == 0 || _u.DecisionHash == Hash;
        if (_u.Fresh || _u.Decision == UltraDecision.StartOver && mine)
        {
            _log("start over: the saved run is deleted");
            UltraCheckpoint.Delete(Dir, _log);
            _seq = 1;
            return 0;
        }
        if (_u.Decision == UltraDecision.Skip || !mine) return 0;
        UltraManifest? m; string why = "";
        if (_u.Found is { } f && f.Hash == Hash && !_u.OnlyOwnRun) m = f;
        else m = UltraCheckpoint.FindValid(Dir, Hash, out why, ct, _u.OnlyOwnRun ? _u.RunId : null);
        if (m == null)
        {
            bool any = false;
            try { any = Directory.Exists(Dir) && Directory.GetFiles(Dir, "ckpt-*.json").Length > 0; } catch (Exception) { /* no folder: nothing to say */ }
            if (any && !_u.OnlyOwnRun) { _u.Note = $"starting fresh: {why}"; _log(_u.Note); }
            return 0;
        }
        Trusted = false;   // from here until the load completes, nothing may be written
        try
        {
            if (m.Nx != grid.Nx || m.Ny != grid.Ny || m.Nz != grid.Nz || MathF.Abs(m.Dx - grid.Dx) > 1e-7f * m.Dx || m.OriginX != grid.Origin.X || m.OriginY != grid.Origin.Y || m.OriginZ != grid.Origin.Z)
                throw new InvalidDataException("the grid rebuilt for the saved run is not the saved grid");
            using var r = UltraCheckpoint.Open(Dir, m, Hash, out var fs);
            using (fs) { session.ReadState(r, ct); pass.ReadState(r, grid); }
        }
        catch (OperationCanceledException) { Enabled = false; throw; }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Enabled = false;   // the half-loaded session must never be saved, and the good checkpoint must not be pruned by a new one
            throw new UltraStoppedException($"The saved run could not be loaded ({e.Message}). It is kept in {Dir}; start Ultra over (the Start over button, or --fresh) to begin again.");
        }
        Trusted = true;
        _u.Note = $"resumed from checkpoint {m.Sequence}: {m.DoneText} done, step {session.Steps}, {m.Batches} batches";
        _log(_u.Note);
        _since.Restart();
        return pass.WallUsedSeconds;
    }

    /// <summary>True when the timer says a checkpoint is due.</summary>
    public bool Due => Enabled && Trusted && _since.Elapsed.TotalSeconds >= _u.EffectiveInterval;

    /// <summary>Writes a checkpoint if the pass is at a point where one is valid (see <see cref="AveragedPass.TryQuiesce"/>); false when it is not, or the write failed.
    /// A failed write (a full disk) deletes its temp files, restarts the interval timer and is logged; the third in a row switches checkpoints off for this run with a note.
    /// <paramref name="wait"/> false (a stop) never waits for the batch analysis.</summary>
    public bool TryWrite(LbmSolver.LbmSession session, AveragedPass pass, double usedSeconds, string why, CancellationToken ct = default, bool wait = true)
    {
        if (!Enabled || !Trusted || !pass.TryQuiesce(wait)) return false;
        var sw = Stopwatch.StartNew();
        try
        {
            var meta = new UltraManifest
            {
                Hash = Hash, HashInputs = _inputs, RunId = _u.RunId, Dx = _grid.Dx, Nx = _grid.Nx, Ny = _grid.Ny, Nz = _grid.Nz,
                OriginX = _grid.Origin.X, OriginY = _grid.Origin.Y, OriginZ = _grid.Origin.Z, Domain = _domain, Cells = _grid.Count,
                Step = session.Steps, SimSeconds = session.SimSeconds, WallUsedSeconds = usedSeconds, Batches = pass.BatchCount, Phase = pass.Averaging ? "averaging" : "settle",
            };
            var m = UltraCheckpoint.Write(Dir, _seq++, meta, w =>
            {
                session.WriteState(w, ct);
                pass.WriteState(w);
            });
            _failures = 0;
            _since.Restart();
            _log($"checkpoint {m.Sequence} ({why}): step {m.Step}, {pass.BatchCount} batches, {TimeSpan.FromSeconds(usedSeconds):hh\\:mm} of the run, {m.DataBytes / 1073741824.0:0.00} GB in {sw.Elapsed.TotalSeconds:0.#} s -> {Dir}");
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _since.Restart();
            _log($"checkpoint not written ({e.Message}); trying again in {_u.EffectiveInterval / 60:0.#} min");
            if (++_failures >= 3)
            {
                Enabled = false;
                _u.Note = "checkpoints stopped after 3 failed writes (disk full?): the run goes on but cannot be resumed from here";
                _log(_u.Note);
            }
            return false;
        }
    }

    /// <summary>Any checkpoint files in this run's folder (no checksum pass).</summary>
    public bool HasSavedFiles { get { try { return Directory.Exists(Dir) && Directory.GetFiles(Dir, "ckpt-*.json").Length > 0; } catch (Exception) { return false; } } }

    /// <summary>A normal end, after the result is on disk or published: nothing is left to resume. Never throws.</summary>
    public void Delete() => UltraCheckpoint.Delete(Dir, _log);
}

/// <summary>The Ultra run loop: AveragedPass.Run plus keep-awake, checkpoints at batch boundaries, resume, and a GPU-fault policy that never falls back to the CPU.</summary>
public static class UltraDriver
{
    public static PassOutcome Run(VoxelGrid grid, BuildingModel model, Vector2 wind, RunOptions o, PassSettings cfg, UltraSettings u, CancellationToken ct, IProgress<double>? progress)
    {
        void Log(string s) { u.Log?.Invoke(s); Console.Error.WriteLine("[ultra] " + s); }
        using var awake = new UltraKeepAwake();
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                try { return RunOnce(grid, model, wind, o, cfg, u, ct, progress, Log); }
                catch (Exception oom) when (IsOutOfMemory(oom) && attempt == 1 && u.FixedDx <= 0f && !ct.IsCancellationRequested)
                {
                    // Out of memory at Start: once, on a grid with 85 % of the cells (a saved run's grid cannot be changed, so it is not retried).
                    float dx2 = Ultra.OomRetryDx(grid.Dx);
                    u.Note = $"the GPU ran out of memory at Start: retrying with 85 % of the cells (cell size {grid.Dx * 1000:0.#} -> {dx2 * 1000:0.#} mm)";
                    Log(u.Note);
                    LbmSolver.ResetGpu();
                    grid = Voxelizer.Build(model, int.MaxValue, null, null, minDx: Ultra.MinDx, mode: o.DomainMode, wind: wind, maxCells: 0, fixedAutoDx: dx2);
                    grid.Warnings.Add(u.Note);
                    return RunOnce(grid, model, wind, o, cfg, u, ct, progress, Log);
                }
            }
            catch (Exception e) when (LbmSolver.IsGpuFault(e) && !ct.IsCancellationRequested)
            {
                string reason = LbmSolver.GpuFaultReason(e);
                string dir = u.CheckpointDir ?? "";
                bool kept = dir.Length > 0 && UltraCheckpoint.FindValid(dir, Path.GetFileName(dir), out _, default, u.RunId) != null;
                if (attempt >= 2)
                {
                    string msg = UltraFaultMessage(reason, dir.Length > 0 && Directory.Exists(dir), dir);
                    Log(msg);
                    throw new UltraStoppedException(msg);
                }
                Log($"The GPU stopped responding ({reason}). Retrying once on the GPU" + (kept ? " from this run's last checkpoint." : " from the start (this run had written no checkpoint yet)."));
                LbmSolver.ResetGpu();
                u.Fresh = false; u.Decision = UltraDecision.Auto; u.DecisionHash = ""; u.Found = null; u.OnlyOwnRun = true;   // the retry resumes only what THIS run wrote
            }
        }
    }

    /// <summary>The solver's own guard, or the driver's allocation failing for lack of memory.</summary>
    public static bool IsOutOfMemory(Exception e) => e is LbmSolver.GpuOutOfMemoryException || e is OutOfMemoryException || e.InnerException is OutOfMemoryException || e.Message.Contains("out of memory", StringComparison.OrdinalIgnoreCase);

    /// <summary>The message after a second GPU fault.</summary>
    public static string UltraFaultMessage(string reason, bool kept, string dir) =>
        $"The GPU stopped responding twice ({reason}). The Ultra run has stopped" + (kept
            ? $"; the checkpoint is kept in {dir} and the run can be resumed later by starting Ultra again on the same model."
            : "; no checkpoint had been written yet, so there is nothing to resume.");

    static PassOutcome RunOnce(VoxelGrid grid, BuildingModel model, Vector2 wind, RunOptions o, PassSettings cfg, UltraSettings u, CancellationToken ct, IProgress<double>? progress, Action<string> log)
    {
        using var session = LbmSolver.Start(grid, model, wind, o, null);
        if (session.WarmupSteps <= 0 && session.PlannedSteps <= 0) return new PassOutcome { Field = session.ToField() };
        var run = new UltraRun(grid, model, wind, o, u, session, log);
        var pass = new AveragedPass(new LbmPassSession(session), model, wind, o, cfg);
        double carried = run.TryResume(session, pass, grid, ct);
        var wall = Stopwatch.StartNew();
        var lastLog = Stopwatch.StartNew();
        double Used() => carried + wall.Elapsed.TotalSeconds;
        pass.Tick(Used(), 0, ct);   // a resumed pass first re-reads the clock (and may already be at its cap)
        try
        {
            while (!pass.Done)
            {
                ct.ThrowIfCancellationRequested();
                session.Step((int)Math.Clamp(pass.StepsToEvent, 1, 100), ct);
                pass.Tick(Used(), 0, ct);
                progress?.Report(Math.Clamp(pass.SteppingSeconds / Math.Max(cfg.CapSeconds, 1), 0, 0.95));
                if (!pass.Done && run.Due) run.TryWrite(session, pass, Used(), "timer", ct);
                if (lastLog.Elapsed.TotalSeconds >= 60)
                {
                    lastLog.Restart();
                    log($"{TimeSpan.FromSeconds(Used()):hh\\:mm\\:ss} · step {session.Steps} · {pass.BatchCount} batches · {session.Mlups:#,0} MLUPS");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A graceful stop: written only if the pass is exactly at a batch boundary right now (never waiting for a batch); mid-batch the last periodic checkpoint stays.
            if (run.Enabled && !run.TryWrite(session, pass, Used(), "stopped", CancellationToken.None, wait: false)) log("stopped mid-batch: the last checkpoint is kept; the partial batch is dropped on resume");
            throw;
        }
        if (pass.TimeLimitBeforeAveraging) throw new PassTimeLimitException(AveragedPass.NoAveragingNote);
        var outcome = pass.Finish(ct);
        outcome.Report.UltraDx = grid.Dx;
        return outcome;   // the checkpoints stay until the caller has the result written (HeadlessSolve deletes them then)
    }
}

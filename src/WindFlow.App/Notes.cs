using System.Text.RegularExpressions;

namespace WindFlow.App;

/// <summary>Basic's "N notes": one plain-language sentence per solver note. The full technical text stays in
/// Expert and goes to the log. Every pattern matches a warning Core or LiveSolver actually writes (Analysis.cs,
/// Voxelizer.cs, LbmNested.cs, LiveSolver.cs); anything unrecognised falls back to a neutral sentence rather than
/// leaking engineering text into Basic.</summary>
public static class Notes
{
    /// <summary>The one note every calibrated result carries (Basic says the plain sentence below; Expert the technical one).</summary>
    public const string CalibratedTechnical = "Opening model: calibrated. A sub-grid loss makes an isolated window or door discharge at its Cd (0.61 unless set on the row), because the grid is coarse.";
    public const string CalibratedPlain = "Windows and doors use a calibrated discharge model (Cd 0.61) because the grid is coarse";

    static readonly (Regex Pattern, Func<Match, string> Say)[] Rules =
    [
        (R(@"^Opening model: calibrated"),
            _ => CalibratedPlain),
        (R(@"^Opening (.+?): effective discharge coefficient"),
            m => $"{m.Groups[1].Value}: the air here sweeps past rather than through; treat its flow as rough."),
        (R(@"^Opening (.+?) is inside the fine box's sponge band"),
            m => $"{m.Groups[1].Value} sits close to the box edge, so the box adds little accuracy there."),
        (R(@"^Room (.+?) outline crosses open air"),
            m => $"{m.Groups[1].Value} is open to a larger space, so its air changes count only air through openings."),
        (R(@"^(.+?): \d+ % of its air exchange passes through gaps that are not recognised as openings"),
            m => $"{m.Groups[1].Value} has holes in its walls that the model does not list as openings; air also moves through them, but the air-change figure leaves it out."),
        (R(@"^Room (.+?): no air cells"),
            m => $"{m.Groups[1].Value} has no air in the model; check its walls and floor."),
        (R(@"^(?:Source|Device) (.+?): its patch is smaller than one cell"),
            m => $"{m.Groups[1].Value} is smaller than one grid cell at this cell size, so it does nothing; use a finer quality level or make it larger."),
        (R(@"^Device (.+?) moved (\d+)% of its set flow"),
            m => $"{m.Groups[1].Value} moves only about {m.Groups[2].Value} % of its rated air here; the grid is too coarse around it."),
        (R(@"^Device (.+?): only .* clear air on the intake"),
            m => $"{m.Groups[1].Value} is starved of air on its intake side; give it more room."),
        (R(@"^Device (.+?): the fan disc is only"),
            m => $"{m.Groups[1].Value} is small for this grid, so its airflow is approximate."),
        (R(@"^Device (.+?): no air at its location"),
            m => $"{m.Groups[1].Value} sits inside a wall or floor, so it does nothing."),
        (R(@"^Source (.+?): no air at its location"),
            m => $"{m.Groups[1].Value} sits inside a wall or floor, so it does nothing."),
        (R(@"^Device (.+?): a solid surface sits"),
            m => $"{m.Groups[1].Value} faces a solid surface, so it cannot remove air."),
        (R(@"^Device (.+?): (the return grille|no room air in front|no air at the return|the cassette)"),
            m => $"{m.Groups[1].Value}: its air return is placed awkwardly, so its cooling figure is rough."),
        (R(@"^Device (.+?): its outlet is inside"),
            m => $"{m.Groups[1].Value} sits close to the box edge, so the box adds little accuracy there."),
        (R(@"^Device (.+?): "),
            m => $"{m.Groups[1].Value} may not be modelled well at this grid size."),
        (R(@"^Opening Normal orientation could not be verified"),
            _ => "Every opening seems to flow the same way, so which ones bring air in is uncertain."),
        (R(@"^Simulation box"),
            _ => "The simulation box edges are partly inside the building, so its flow is approximate."),
        (R(@"^\d+ rooms? outside the box"),
            _ => "Some rooms are outside the simulation box and are not in the list."),
        (R(@"^Box is sealed|^This box cannot be solved"),
            _ => "The simulation box cannot be solved as drawn; the whole building is solved instead."),
        (R(@"^Thermal runs cannot nest"),
            _ => "With temperature on, the box is solved on its own, which is less accurate at its edges."),
        (R(@"^(Age of air|Contaminant) has no steady state"),
            _ => "Part of the building gets no fresh air at all."),
        (R(@"^(\d+) model elements? (had|have) no usable geometry"),
            m => $"{m.Groups[1].Value} element{(m.Groups[1].Value == "1" ? "" : "s")} from Revit had no geometry and {(m.Groups[1].Value == "1" ? "is" : "are")} missing from the model, so a wall, slab or roof may be absent."),
        (R(@"^(\d+) air terminals? had no readable duct connector"),
            m => $"{m.Groups[1].Value} air terminal{(m.Groups[1].Value == "1" ? "" : "s")} from Revit {(m.Groups[1].Value == "1" ? "was" : "were")} assumed to supply air downward; check any return or exhaust grille."),
        (R(@"^(\d+) rooms? had an upper limit below the storey's top"),
            m => $"{m.Groups[1].Value} room{(m.Groups[1].Value == "1" ? "" : "s")} from Revit had a ceiling limit lower than the storey, and {(m.Groups[1].Value == "1" ? "was" : "were")} raised to the next level above."),
        (R(@"^Opening (.+?) is only [\d.,]+ cells across at"),
            m => $"{m.Groups[1].Value} is narrower than 4 grid cells, so its flow is unreliable; use a finer grid."),
        (R(@"^Opening (.+?) is [\d.,]+ cells across in Preview"),
            m => $"{m.Groups[1].Value} is rounded to the Preview grid (about one cell row); use Standard or High for its flow."),
        (R(@"^Opening (.+?): (no open cells in its throat|a solid layer still blocks)"),
            m => $"{m.Groups[1].Value} is blocked at this grid, so no air passes through it."),
        (R(@"^Room (.+?): gust-driven"),
            m => $"{m.Groups[1].Value} is ventilated mainly by gusts; Standard can read low, so run High to compare."),
        (R(@"^Wall model on"),
            _ => "The experimental wall model was on for this result."),
        (R(@"^Wind blockage"),
            _ => "The building is large for its wind-tunnel domain, so facade pressures and air changes can be biased."),
        (R(@"^Full wind domain"),
            _ => "The full wind domain was cut back to fit graphics memory; Expert shows which margins."),
        (R(@"^Grid size limited by graphics memory"),
            _ => "The grid is limited by graphics memory, so openings resolve less well than the setting suggests."),
        (R(@"^Room (.+?): Supply \(.*do not balance in this sealed room"),
            m => $"{m.Groups[1].Value} is sealed but its supply and extract differ, so WindFlow balanced them. Add a leakage path or transfer opening for a realistic result."),
        (R(@"^Room (.+?): .* supplies no outdoor air in this sealed room"),
            m => $"{m.Groups[1].Value} is sealed, so its diffuser only stirs the room's air and brings in no outdoor air. Add a leakage path or transfer opening."),
        // Batch 5 (RY-05, 6d/RP, 6e/6j, S, P17-07, RU follow-ups 1 and RP follow-up 3): the notes written since the table above.
        (R(@"^(.+?) connects only through a gap narrower"),
            m => $"{m.Groups[1].Value} joins the rest of the building only through a very narrow gap at this cell size, so its air age cannot be computed and shows as sealed; use a finer quality level or widen the gap."),
        (R(@"^Opening (.+?): the lattice resolves this opening at Cd .*flow here may be about (\d+) % low"),
            m => $"{m.Groups[1].Value} is small for this grid, so about {m.Groups[2].Value} % less air passes through it than its size says; use a finer quality level."),
        (R(@"^Opening (.+?): the opening's discharge coefficient is uncertain by about ±(\d+) %"),
            m => $"How much air passes {m.Groups[1].Value} depends on where it falls on the grid, by up to about ±{m.Groups[2].Value} %; a finer quality level narrows that."),
        (R(@"^Opening (.+?): wall thickness taken from the geometry"),
            m => $"{m.Groups[1].Value}: its wall thickness was read from the model, not from the drawn depth."),
        (R(@"^(.+?): room air held at .*\(stratification\)"),
            m => $"{m.Groups[1].Value} is held at its set temperature on average, but the air where people sit is a different temperature because warm air rises."),
        (R(@"^(.+?) is conditioned by its (.+?); the ideal hold is off there"),
            m => $"{m.Groups[1].Value} has its own air conditioner, so the ideal temperature hold is switched off there."),
        (R(@"^(CUDA unavailable|No CUDA device found)"),
            _ => "No usable NVIDIA CUDA graphics card was found, so OpenCL was used."),
        (R(@"^GPU not available"),
            _ => "No graphics card could be used, so this ran on the CPU and is much slower."),
        (R(@"^The GPU stopped responding"),
            _ => "The graphics card stopped responding, so this run was restarted on the CPU and is slower."),
        (R(@"^Source (.+?): its supply patch is only (\d+) cells across, too narrow"),
            m => $"{m.Groups[1].Value}'s supply is only {m.Groups[2].Value} cells across, too narrow for turbulence, so none was applied; use a finer quality level."),
        (R(@"^Source (.+?): its supply patch is only (\d+) cells across"),
            m => $"{m.Groups[1].Value}'s supply is only {m.Groups[2].Value} cells across, so the turbulence it delivers is below what you asked for; use a finer quality level."),
        (R(@"^Averaging: \d+ non-finite"),
            _ => "Some cells held invalid numbers (NaN) in a batch; they were left out of the time average, so the average is biased or missing there."),
        (R(@"^Age of air not converged"),
            _ => "The air-age figures did not fully settle, so they may read a little low."),
    ];

    static Regex R(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Plain(string technical)
    {
        foreach (var (pattern, say) in Rules)
            if (pattern.Match(technical) is { Success: true } m) return say(m);
        return "The solver flagged part of this result as approximate; Expert shows the details.";
    }

    /// <summary>Every note for one result, once each and in order: <paramref name="extra"/> (box and nest notes)
    /// first, then the grid's warnings, then the ledger's (Core copies its grid warnings into the ledger notes too,
    /// and the ledger's own information lines are not warnings).</summary>
    public static List<string> Gather(IEnumerable<string> extra, WindFlow.Core.SimulationResult stats)
    {
        var notes = new List<string>();
        foreach (var n in extra.Concat(stats.Field.Grid.Warnings).Concat(stats.Ledger.Notes))
            if (n.Length > 0 && !IsInformation(n) && !notes.Contains(n)) notes.Add(n);
        return notes;
    }

    /// <summary>The mass ledger's own notes include three information lines that are not warnings; Basic does not
    /// count them.</summary>
    public static bool IsInformation(string note) =>
        note.StartsWith("Inflow ", StringComparison.Ordinal) ||
        note.Contains("opening(s) in the balance", StringComparison.Ordinal) ||
        note.Contains("device(s) in the balance", StringComparison.Ordinal);
}

using System.Numerics;

namespace WindFlow.Core;

/// <summary>Batch 3c (C4): the validated envelope, said out loud. Each function returns the notes for one way a result can sit outside what
/// VALIDATION.md checked, as grid warnings (they reach the notes list, the log, the headless output and the PDF). They are pure functions of the
/// grid, the model and the options so a check can trigger every one without a solve. <see cref="Apply"/> re-derives them on every analysis.</summary>
public static class Envelope
{
    /// <summary>Openings narrower (or lower) than this many cells are not validated (VALIDATION.md: the Cd check is skipped below it too).</summary>
    public const float MinCellsAcross = 4f;
    /// <summary>Preview opening sizes are quantised to the cell grid: up to this many cells across the +-1 row is a large share of the opening.</summary>
    public const float PreviewQuantisedMaxCells = 6f;
    /// <summary>COST 732 / Franke et al. 2007 blockage guideline (frontal area over the domain cross-section).</summary>
    public const float BlockageLimit = 0.03f;

    // The prefixes Apply owns: every earlier note with one of these is dropped before the new ones are added.
    static readonly string[] OwnedFragments =
    [
        " cells across at ", " too small for this cell size", ": no open cells in its throat", ": a solid layer still blocks it", ": gust-driven", "Wall model on (experimental)", "Wind blockage ",
    ];

    static string Name(Opening o) => o.Name.Length > 0 ? o.Name : o.Id;

    /// <summary>Re-derive the always-on envelope notes into the grid's warnings (the Preview-only and budget notes are the App's, it knows which pass it shows).</summary>
    /// <returns>IMP-C: the rooms under <see cref="MinRoomCells"/> cells across at this grid (<see cref="ResolutionGuard"/>), for the results table.</returns>
    public static HashSet<Room> Apply(VoxelGrid g, BuildingModel model, Vector2 wind, RunOptions options)
    {
        g.Warnings.RemoveAll(w => OwnedFragments.Any(f => w.Contains(f, StringComparison.Ordinal)));
        g.Warnings.AddRange(NarrowOpenings(g, model.Openings));
        g.Warnings.AddRange(ResolutionGuard(g, model, out var tooSmall));
        g.Warnings.AddRange(BlockedOpenings(g, model.Openings));
        g.Warnings.AddRange(GustDrivenRooms(g, model, wind, options));
        if (WallModelOn(options)) g.Warnings.Add(WallModelNote);
        if (Blockage(g, wind) is { } b && b > BlockageLimit)
            g.Warnings.Add(BlockageNote(b, fromFull: g.Warnings.Any(w => w.StartsWith("Full wind domain", StringComparison.Ordinal))));
        return tooSmall;
    }

    /// <summary>IMP-C: a room needs about this many cells across (its widest inscribed square) for its numbers to mean anything.</summary>
    public const float MinRoomCells = 3f;
    /// <summary>IMP-C2: a suggested cell budget over this many times the current grid is not offered as a number.</summary>
    public const double MaxBudgetRatio = 4.0;

    /// <summary>IMP-C: warn, never refuse. Rooms under <see cref="MinRoomCells"/> cells across and open openings under <see cref="MinCellsAcross"/> at this grid: one note with
    /// the counts and, from the rooms only, the cell budget that gives the narrowest unreliable room 3 cells (this grid's cells over the cube of the ratio), capped (IMP-C2).
    /// A room's width is its widest axis-aligned inscribed square, measured on a raster a quarter of a cell fine.</summary>
    public static List<string> ResolutionGuard(VoxelGrid g, BuildingModel model, out HashSet<Room> tooSmall, long? cellLimit = null)
    {
        // ponytail: auto-raise the budget when it fits the GPU; needs plan-time VRAM check
        tooSmall = new HashSet<Room>(ReferenceEqualityComparer.Instance);
        bool anyAnalysed = model.Storeys.Any(st => st.Analysed);
        double needDx = double.MaxValue;
        foreach (var room in model.Rooms)
        {
            if (anyAnalysed && model.StoreyOf(room) is not { Analysed: true }) continue;
            float width = RoomWidth(room, g.Dx);
            if (width >= MinRoomCells * g.Dx) continue;
            tooSmall.Add(room);
            if (width > 0f) needDx = Math.Min(needDx, width / MinRoomCells);
        }
        int openings = 0;
        foreach (var o in model.Openings)
        {
            if (!o.IsOpen) continue;
            if (MathF.Min(o.Width, o.Height) / g.Dx < MinCellsAcross) openings++;
        }
        var notes = new List<string>();
        if (tooSmall.Count + openings == 0) return notes;
        string openNote = openings == 0 ? "" : $"{openings} opening{(openings == 1 ? " is" : "s are")} narrower than {MinCellsAcross:0} cells";
        if (tooSmall.Count == 0)
        {
            notes.Add($"{openNote} and too small for this cell size; their flow is unreliable.");
            return notes;
        }
        // IMP-C2: the budget follows the rooms only (one narrow or phantom opening would set it); more than 4x the current grid, or past the GPU plan, gets no number.
        string budget = "";
        if (needDx < double.MaxValue)
        {
            double want = g.Count * Math.Pow(g.Dx / needDx, 3);
            budget = want > MaxBudgetRatio * g.Count || want > (cellLimit ?? LbmSolver.CellLimit(true, false))
                ? " This building is too detailed to resolve every room at a size this computer can run; results for the marked rooms are unreliable."
                : $" A cell budget of about {want / 1e6:0.#} M cells would give every marked room about {MinRoomCells:0} cells across.";
        }
        notes.Add($"{tooSmall.Count} room{(tooSmall.Count == 1 ? " is" : "s are")} too small for this cell size (rooms need about {MinRoomCells:0} cells across); their results are unreliable.{budget}" +
                  (openings > 0 ? $" {openNote}." : ""));
        return notes;
    }

    /// <summary>Metres across the widest axis-aligned square inside the room's loops (largest-square dynamic programme on a raster of cell/4, capped near 2 M raster cells).</summary>
    static float RoomWidth(Room room, float dx)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var loop in room.Loops) foreach (var p in loop) { x0 = MathF.Min(x0, p.X); x1 = MathF.Max(x1, p.X); y0 = MathF.Min(y0, p.Y); y1 = MathF.Max(y1, p.Y); }
        if (x1 <= x0 || y1 <= y0) return 0f;
        float h = MathF.Max(dx / 4f, MathF.Sqrt((x1 - x0) * (y1 - y0) / 2e6f));
        int w = (int)MathF.Ceiling((x1 - x0) / h), d = (int)MathF.Ceiling((y1 - y0) / h), best = 0;
        var prev = new int[w + 1]; var cur = new int[w + 1];
        for (int j = 0; j < d; j++)
        {
            for (int i = 0; i < w; i++)
            {
                cur[i + 1] = Analysis.InsideLoops(room.Loops, new Vector2(x0 + (i + 0.5f) * h, y0 + (j + 0.5f) * h)) ? Math.Min(Math.Min(prev[i + 1], cur[i]), prev[i]) + 1 : 0;
                best = Math.Max(best, cur[i + 1]);
            }
            (prev, cur) = (cur, prev);
        }
        return best * h;
    }

    /// <summary>An open opening under <see cref="MinCellsAcross"/> cells across (its smaller side) at this grid.</summary>
    public static List<string> NarrowOpenings(VoxelGrid g, IEnumerable<Opening> openings)
    {
        var notes = new List<string>();
        foreach (var o in openings)
        {
            if (!o.IsOpen) continue;
            float cells = MathF.Min(o.Width, o.Height) / g.Dx;
            if (cells < MinCellsAcross)
                notes.Add($"Opening {Name(o)} is only {cells:0.#} cells across at {g.Dx * 1000:0} mm cells: below the {MinCellsAcross:0} cells VALIDATION.md checked, " +
                          "so its flow is unreliable; use a finer grid for this opening.");
        }
        return notes;
    }

    /// <summary>Preview only: an opening 4 to 6 cells across has its size quantised to the grid (the carve is cell-centred), a +-1 row error of 15 to 25 %.</summary>
    public static List<string> PreviewOpenings(VoxelGrid g, IEnumerable<Opening> openings)
    {
        var notes = new List<string>();
        foreach (var o in openings)
        {
            if (!o.IsOpen) continue;
            float cells = MathF.Min(o.Width, o.Height) / g.Dx;
            if (cells >= MinCellsAcross && cells <= PreviewQuantisedMaxCells)
                notes.Add($"Opening {Name(o)} is {cells:0.#} cells across in Preview: size quantised to the grid: ±1 cell row; use Standard or High for this opening's flow.");
        }
        return notes;
    }

    /// <summary>An open opening that passes no air: no open cell in its throat plane, or a solid layer left across it after the carve (P12-05 / RX-05: a wall over 1 m
    /// thick, which the carve-through leaves alone, or an opening outside its wall).</summary>
    public static List<string> BlockedOpenings(VoxelGrid g, IEnumerable<Opening> openings)
    {
        var notes = new List<string>();
        foreach (var o in openings)
        {
            if (!o.IsOpen) continue;
            var throat = Analysis.ThroatCells(g, o);
            if (throat.Count == 0)
                notes.Add($"Opening {Name(o)}: no open cells in its throat at {g.Dx * 1000:0} mm cells, so no air passes through it (it is smaller than a cell, or lies outside the solved grid).");
            else if (!ConnectsBothSides(g, o, throat))
                notes.Add($"Opening {Name(o)}: a solid layer still blocks it at {g.Dx * 1000:0} mm cells (the wall here is over 1 m thick, or the opening lies outside its wall), so no air passes through it.");
        }
        return notes;
    }

    /// <summary>True when at least one throat column is air all the way along the normal, on both sides, through the wall (|e| from the opening's centre under
    /// Depth/2 + half a cell) and in the first cell beyond it. That cell lies outside the wall slab (0.05 cell of margin for faces on a cell plane), so a solid
    /// there means the carve left a layer of wall across the hole.</summary>
    static bool ConnectsBothSides(VoxelGrid g, Opening o, List<int> throat)
    {
        var n = new Vector3(o.Normal.X, o.Normal.Y, 0);
        if (n.LengthSquared() < 1e-8f) return true;      // no horizontal normal: nothing was carved to check
        n = Vector3.Normalize(n);
        float beyond = o.Depth * 0.5f + 0.55f * g.Dx;
        foreach (int idx in throat)
        {
            int x = idx % g.Nx, y = (idx / g.Nx) % g.Ny, z = idx / (g.Nx * g.Ny);
            var c = g.CellCenter(x, y, z);
            float e0 = Vector3.Dot(c - o.Center, n);
            bool clear = true;
            for (int side = -1; side <= 1 && clear; side += 2)
                for (int k = 1; k <= 8 && clear; k++)
                {
                    var p = c + n * (side * k * g.Dx) - g.Origin;
                    int px = (int)MathF.Floor(p.X / g.Dx), py = (int)MathF.Floor(p.Y / g.Dx), pz = (int)MathF.Floor(p.Z / g.Dx);
                    if (px < 0 || py < 0 || pz < 0 || px >= g.Nx || py >= g.Ny || pz >= g.Nz) break;   // off the grid: open air
                    if (g.Cell[g.Index(px, py, pz)] == VoxelGrid.Solid) clear = false;
                    else if (MathF.Abs(e0 + side * k * g.Dx) >= beyond) break;                           // the first cell outside the wall was air
                }
            if (clear) return true;
        }
        return false;
    }

    /// <summary>Rooms ventilated mainly by gusts: every open outside opening on one facade, or every one on a wall parallel to the wind. Only under steady wind (no
    /// approach turbulence), where such a room reads low (S6-02: Living 13.3 ACH steady against 20-23.5 gusty).</summary>
    public static List<string> GustDrivenRooms(VoxelGrid g, BuildingModel model, Vector2 wind, RunOptions options)
    {
        var notes = new List<string>();
        if (options.WindTurbulence != null || wind.Length() < 0.1f) return notes;
        var w = Vector2.Normalize(wind);
        bool anyAnalysed = model.Storeys.Any(s => s.Analysed);
        foreach (var room in model.Rooms)
        {
            if (anyAnalysed && model.StoreyOf(room) is not { Analysed: true }) continue;
            var normals = new List<Vector2>();
            foreach (var o in model.Openings)
            {
                if (!o.IsOpen || !Analysis.IsExteriorOpening(model, o, g.Dx)) continue;
                var n0 = new Vector2(o.Normal.X, o.Normal.Y);
                if (n0.LengthSquared() < 1e-8f) continue;
                var n = Vector2.Normalize(n0);
                float off = o.Depth * 0.5f + 1.5f * g.Dx;
                bool inside = false;
                foreach (float s in new[] { 1f, -1f })
                {
                    var p = o.Center + new Vector3(n.X, n.Y, 0) * (s * off);
                    inside |= p.Z >= room.ZMin && p.Z <= room.ZMax && Analysis.InsideLoops(room.Loops, new Vector2(p.X, p.Y));
                }
                if (inside) normals.Add(n);
            }
            if (normals.Count == 0) continue;
            bool oneFacade = normals.All(n => Vector2.Dot(n, normals[0]) > 0.7f);
            bool parallel = normals.All(n => MathF.Abs(Vector2.Dot(n, w)) < 0.5f);
            if (!oneFacade && !parallel) continue;
            string where = oneFacade ? "on one facade" : "on walls parallel to the wind";
            string name = room.Name.Length > 0 ? room.Name : room.Id;
            notes.Add($"Room {name}: gust-driven. Its outside openings are only {where}, so it is ventilated mainly by gusts that a steady wind does not have: " +
                      "Standard can read low here; run High (gusty wind) to compare.");
        }
        return notes;
    }

    public const string WallModelNote = "Wall model on (experimental): this result used the Werner-Wengle wall function, which gave mixed results that change with the grid on the measured test rooms.";

    /// <summary>The wall model is read by the solver on an isothermal run only (Core ignores it with Thermal on).</summary>
    public static bool WallModelOn(RunOptions options) => options.WallModel != WallModel.None && !options.Thermal;

    /// <summary>Frontal area of the building over the domain cross-section for this wind (P13b-03), or null when the grid is not the automatic building domain
    /// (a hand-built grid, a user simulation box) or the wind is still.</summary>
    public static float? Blockage(VoxelGrid g, Vector2 wind)
    {
        if (g.BuildingSize.Z <= 0f) return null;
        return Blockage(g.BuildingSize, g.Nx * g.Dx, g.Ny * g.Dx, (g.Nz - 1) * g.Dx, wind);
    }

    /// <summary>The one blockage formula (6c: the Full domain sizes its margins with it): the building's frontal area
    /// (plan <paramref name="building"/>.X x .Y seen along the wind, times its height .Z) over the domain section across the wind
    /// (<paramref name="domainX"/> x <paramref name="domainY"/> plan, <paramref name="domainHeight"/> of air above the ground). Null with no wind.</summary>
    public static float? Blockage(Vector3 building, float domainX, float domainY, float domainHeight, Vector2 wind)
    {
        if (wind.Length() < 1e-3f) return null;
        var w = Vector2.Normalize(wind);
        float front = (building.X * MathF.Abs(w.Y) + building.Y * MathF.Abs(w.X)) * building.Z;
        float section = (domainX * MathF.Abs(w.Y) + domainY * MathF.Abs(w.X)) * domainHeight;
        return section > 0f ? front / section : null;
    }

    /// <summary>RZ-07: <paramref name="fromFull"/> = the grid came from a Full plan the graphics-memory guard cut back (its "Full wind domain"
    /// note is on the grid), which must not point at Full as the remedy.</summary>
    public static string BlockageNote(float blockage, bool fromFull = false) => fromFull
        ? $"Wind blockage {blockage * 100:0.#} % (guideline {BlockageLimit * 100:0} %): the Full wind domain had to be cut back to fit graphics memory, so the walls of " +
          "the wind tunnel still squeeze the flow and facade pressures and air changes can read high."
        : $"Wind blockage {blockage * 100:0.#} % (guideline {BlockageLimit * 100:0} %). This wind domain is compact (blockage {blockage * 100:0.#} %). In a test on the sample house, " +
          "room air exchange read about 11-14 % higher per room than the Full domain (newest test; an older run read 8-33 %). For a final check use Domain: Full (Settings, Expert).";

    /// <summary>The graphics-memory clamp on the averaged grid: asked for more cells than the card holds next to the Preview grid.</summary>
    public static string BudgetClampNote(int wantedCells, int cells, float dx) =>
        $"Grid size limited by graphics memory: {cells / 1e6:0.#}M cells at {dx * 1000:0} mm, not the {wantedCells / 1e6:0.#}M asked for; openings and jets resolve less well than the setting suggests.";
}

using System.Numerics;
using WindFlow.Core;

namespace WindFlow.App.Import;

/// <summary>Gives a raw mesh its rooms back, so ACH and the room table work on an imported building.
/// <para>Method: voxelise the model with Core's own <see cref="Voxelizer"/> at a fixed 50 or 100 mm (<see cref="DetectionDx"/>; this does NOT re-implement the
/// voxelizer — it reads the <see cref="VoxelGrid.Cell"/> array it produces), then at each storey's 1.1 m
/// plane take the air that is UNDER COVER — a cell with solid material somewhere above it inside the
/// model's own bounding box — and split it into connected regions. Each region's boundary becomes a Room
/// loop.</para>
/// <para><b>Why "under cover" and not "unreachable from outside".</b> The obvious definition of indoors is
/// air the outdoor flood fill cannot reach. Measured on the sample house, that finds exactly ONE room out
/// of seven: every other room has an open window or door, so the fill walks straight in and the whole
/// interior reads as outdoor air. A roof, not a seal, is what makes a room. The outdoor fill is still run,
/// but only to drop air that has nothing above it at all.</para>
/// <para>What it cannot do: a covered porch or a carport counts as a room, a mezzanine reads as part of the
/// space below it, a room with no ceiling modelled is missed, and the outline is rectilinear at the voxel
/// resolution so a diagonal wall comes out as a staircase. Rooms are statistics here, not geometry, so that
/// is good enough for ACH — but it is why the dialog calls them detected, not imported.</para></summary>
public static class RoomDetect
{
    /// <summary>IMP-B D1: the detection cell size, fixed, never derived from a cell budget (a whole-building budget lost every room under about
    /// 10 m2 on a big building): 50 mm when the building's plan is under <see cref="SmallFootprintM2"/>, else 100 mm. Detection reads a tight
    /// grid (the building plus <see cref="Voxelizer.DetectionMargin"/>), so the cell count is plan area / dx^2 x height / dx; the bytes are one per
    /// cell for the grid plus about 12 per cell at the peak of the flood fills, and both scale with 1/dx^3. Always at or under the 150 mm cap.</summary>
    public const float FineDx = 0.05f, StandardDx = 0.10f, SmallFootprintM2 = 400f;

    public static float DetectionDx(BuildingModel model)
    {
        var (min, max) = Voxelizer.PlanBounds(model);
        float dx = (max.X - min.X) * (max.Y - min.Y) < SmallFootprintM2 ? FineDx : StandardDx;
        // IMP-C: about 12 B a cell at the flood peak (the Clinic: 59 M cells, 1.4 GB). Over the cap, coarsen 50 -> 100 -> 150 mm.
        while (dx < CoarsestDx && Voxelizer.DetectionCells(model, dx) > MaxDetectionCells) dx = dx < StandardDx ? StandardDx : CoarsestDx;
        return dx;
    }

    /// <summary>IMP-C: detection never allocates more than this many cells; <see cref="CoarsestDx"/> is the 150 mm cap.</summary>
    public const long MaxDetectionCells = 150_000_000;
    public const float CoarsestDx = 0.15f;

    /// <summary>How far a room's outline is kept off the walls (it was one cell, 250-257 mm, on the sample house and FZK that detection was calibrated on).</summary>
    public const float InsetM = 0.25f;

    /// <summary>Detection height above each storey's floor: the breathing zone, and above furniture.</summary>
    public const float PlaneHeight = 1.1f;

    /// <summary>S3-27: an open-sky courtyard or atrium becomes an inner loop only when it is at least this many metres across (4 cells of 250 mm).</summary>
    public const float MinCourtyardM = 1f;

    /// <summary>Ignore puddles: a region smaller than this is a cupboard, a wall cavity or voxel noise.</summary>
    public const float MinAreaM2 = 3f;

    /// <summary>After the inset, a piece smaller than this is a leftover sliver of a larger region, not a room.</summary>
    public const float MinPartM2 = 1f;

    public static DetectedRooms Detect(BuildingModel model, int? maxDop = null)
    {
        // Unsealed: the enclosure seal turns every air cell the outside cannot reach, and no Room loop seeds,
        // into Solid, and an imported mesh has no Room loops yet, so a sealed grid would erase the very
        // interiors being looked for (measured on a glTF round trip of the sample house: every column through
        // the interior came back 100 % solid).
        // 6a addendum: the detection-only touched-cell grid (Voxelizer.BuildDetection), which detection was calibrated on; never a solver grid.
        float detectDx = DetectionDx(model);
        var grid = Voxelizer.BuildDetection(model, detectDx, maxDop);   // IMP-B D1: a fixed fine cell size
        int inset = Math.Max(1, (int)MathF.Round(InsetM / grid.Dx));
        // IMP-B: the dead outdoor fill that stood here (its result was never read) is gone: at 100 mm it was a 4-byte array of the whole grid, 237 MB on the Clinic.
        int kTop = TopLayer(grid, model);
        var topSolid = TopSolid(grid, kTop);

        var rooms = new List<Room>();
        var notes = new List<string>();
        var (bmin, bmax) = Voxelizer.PlanBounds(model);
        float baseDx = (bmax.X - bmin.X) * (bmax.Y - bmin.Y) < SmallFootprintM2 ? FineDx : StandardDx;
        if (detectDx > baseDx + 1e-6f)
            notes.Add($"Detection cells coarsened from {baseDx * 1000:0} to {detectDx * 1000:0} mm: the building is large and {baseDx * 1000:0} mm would need over {MaxDetectionCells / 1e6:0} M cells; " +
                      "small rooms and openings may be missed.");   // IMP-C
        var volumes = new List<(Room Room, float Outline)>();                  // D6: the old estimate, kept to say when it was off
        var cores = new List<(Room Room, Storey Storey, List<int> Cells)>();   // the inset core's plan cells, for the leak net
        int ordinal = 1;
        var storeys = model.Storeys.Count > 0
            ? model.Storeys
            : [new Storey { Id = "L1", Name = "Level 1", Elevation = model.GroundZ, Height = 3f, Analysed = true }];

        // PLAN_V2 4.2: holes in walls first (OpeningDetect), then rooms with every hole filled solid, so a doorway
        // no longer joins the two rooms it connects. The grid is put back before the holes look for their rooms.
        var coveredBy = new bool[storeys.Count][];
        var gaps = new List<OpeningDetect.Gap>();
        for (int s = 0; s < storeys.Count; s++)
        {
            int kp = (int)MathF.Floor((storeys[s].Elevation + PlaneHeight - grid.Origin.Z) / grid.Dx);
            if (kp < 0 || kp >= grid.Nz) continue;
            coveredBy[s] = CoveredAbove(grid, kp, kTop);
            gaps.AddRange(OpeningDetect.Find(grid, s, storeys[s].Name, kp, coveredBy[s], notes, Math.Max(0, (int)MathF.Floor((storeys[s].Elevation - grid.Origin.Z) / grid.Dx)), (int)MathF.Ceiling((storeys[s].Elevation + storeys[s].Height - grid.Origin.Z) / grid.Dx) - 1));
        }
        var original = grid.Cell;
        grid.Cell = (byte[])original.Clone();
        foreach (var gap in gaps)
            foreach (int i in gap.Cells) grid.Cell[i] = VoxelGrid.Solid;

        for (int si = 0; si < storeys.Count; si++)
        {
            var storey = storeys[si];
            float z = storey.Elevation + PlaneHeight;
            int k = (int)MathF.Floor((z - grid.Origin.Z) / grid.Dx);
            if (k < 0 || k >= grid.Nz) { notes.Add($"{storey.Name}: its 1.1 m plane is outside the grid"); continue; }

            var covered = coveredBy[si];
            var storeyRooms = new List<(Room Room, List<int> Columns)>();
            var openSky = new bool[covered.Length];   // air on the plane with nothing above it (S3-27)
            for (int f = 0; f < openSky.Length; f++)
                openSky[f] = !covered[f] && grid.Cell[grid.Index(f % grid.Nx, f / grid.Nx, k)] != VoxelGrid.Solid;
            var labels = new int[grid.Nx * grid.Ny];
            int regions = LabelPlane(grid, covered, k, labels);
            int kept = 0;
            // Diagnostic, kept because "0 rooms" has three very different causes: no air on the plane, air
            // that the outdoor fill reached (the building is open), or regions too small to keep.
            int planeAir = 0, planeIndoor = 0;
            for (int y = 0; y < grid.Ny; y++)
            for (int x = 0; x < grid.Nx; x++)
            {
                int i = grid.Index(x, y, k);
                if (grid.Cell[i] == VoxelGrid.Solid) continue;
                planeAir++;
                if (covered[y * grid.Nx + x]) planeIndoor++;
            }
            for (int region = 1; region <= regions; region++)
            {
                float area = 0;
                foreach (int label in labels) if (label == region) area += grid.Dx * grid.Dx;
                if (area < MinAreaM2) continue;
                // The one-cell inset can cut a region at a neck two cells wide (a doorway) or remove a narrow
                // strip entirely (a corridor). Every part that survives is traced; nothing is dropped silently.
                var parts = new int[labels.Length];
                int partCount = LabelMask(grid, Erode(grid, labels, region, inset), region, parts);
                if (partCount == 0)
                {
                    notes.Add($"{storey.Name}: a {area:0.#} m² region is at most {2 * inset * grid.Dx:0.##} m wide " +
                              "and vanished when inset from its walls — a corridor, not kept as a room");
                    continue;
                }
                if (partCount > 1)
                    notes.Add($"{storey.Name}: a {area:0.#} m² region splits into {partCount} parts at a neck {2 * inset * grid.Dx:0.##} m " +
                              "wide or less (a doorway?); each part became its own room");
                for (int part = 1; part <= partCount; part++)
                {
                    float partArea = 0;
                    foreach (int p in parts) if (p == part) partArea += grid.Dx * grid.Dx;
                    if (partArea < MinPartM2)
                    {
                        notes.Add($"{storey.Name}: dropped a {partArea:0.##} m² sliver left over after the inset");
                        continue;
                    }
                    var loop = Outline(grid, parts, part);
                    if (loop.Length < 4) { notes.Add($"{storey.Name}: a {partArea:0.#} m² part could not be outlined"); continue; }
                    var grown = GrownColumns(grid, labels, parts, region, part, inset);
                    float outlineVolume = grown.Count * grid.Dx * grid.Dx * storey.Height;   // D2: the un-eroded outline x storey height
                    var room = new Room
                    {
                        Id = partCount == 1 ? $"det-{storey.Id}-{region}" : $"det-{storey.Id}-{region}-{part}",
                        Name = $"Room {ordinal++}",
                        Level = storey.Id,
                        ZMin = storey.Elevation,
                        ZMax = storey.Elevation + storey.Height,
                        Occupants = 0,
                        VoxelDerived = true,
                        DetectedVolume = outlineVolume,   // D6: replaced below by the 3D air volume once every room of the storey exists
                        Loops = { loop },
                    };
                    volumes.Add((room, outlineVolume));
                    storeyRooms.Add((room, grown));
                    var core = new List<int>();
                    for (int f = 0; f < parts.Length; f++) if (parts[f] == part) core.Add(f);
                    cores.Add((room, storey, core));
                    room.Loops.AddRange(CourtyardLoops(grid, parts, part, openSky, Math.Max(1, (int)MathF.Ceiling(MinCourtyardM / grid.Dx))));   // S3-27: even-odd holes, so area, mask and flux surface exclude open sky
                    rooms.Add(room);
                    kept++;
                }
            }
            if (storeyRooms.Count > 0)
            {
                var volume = RoomVolumes(grid, storeyRooms.Select(r => r.Columns).ToList(), k, storey, topSolid);   // D6
                for (int r = 0; r < storeyRooms.Count; r++) storeyRooms[r].Room.DetectedVolume = volume[r];
            }
            // The counts stay in the note: "0 rooms" has three very different causes, and the numbers say
            // which — no air on the plane, nothing under cover, or regions below the area floor.
            notes.Add($"{storey.Name}: {kept} room{(kept == 1 ? "" : "s")} at z = {z:0.00} m " +
                      $"({planeIndoor} covered cells of {planeAir} air, {regions} regions)");
        }
        grid.Cell = original;
        var usedGaps = new List<OpeningDetect.Gap>();
        var openings = OpeningDetect.Resolve(grid, gaps, rooms, storeys, notes, model.Obstacles, usedGaps);   // D3: also puts the display name on every Room.Name

        foreach (var (room, outline) in volumes)
            if (outline > 0 && MathF.Abs(room.DetectedVolume - outline) > 0.1f * outline)
                notes.Add($"{room.Name}: volume measured up to the ceiling is {room.DetectedVolume:0} m³, " +
                          $"{(room.DetectedVolume / outline - 1) * 100:+0;-0} % against outline area × storey height ({outline:0} m³)");   // D6

        // Leak net (D4, cheap part): every detected opening closed, flood the air from outside; a room still reached has holes nothing recognised.
        var closed = (byte[])original.Clone();
        int layer = grid.Nx * grid.Ny;
        foreach (var gap in usedGaps)
            foreach (int i in gap.Cells)
            {
                closed[i] = VoxelGrid.Solid;
                // A door reaches the real floor top, which can lie a cell below the storey elevation the band starts at (FZK: 0.20 against 0.25): close that crack too.
                if (i / layer == gap.KFloor) for (int j = i - layer; j >= 0 && closed[j] != VoxelGrid.Solid; j -= layer) closed[j] = VoxelGrid.Solid;
            }
        grid.Cell = closed;
        var dist = FloodOutside(grid);
        grid.Cell = original;
        int plane = grid.Nx * grid.Ny;
        foreach (var (room, storey, cells) in cores)
        {
            int best = int.MaxValue;
            foreach (float h in LeakHeights)
            {
                if (h > storey.Height - 0.3f) continue;
                int kk = (int)MathF.Floor((storey.Elevation + h - grid.Origin.Z) / grid.Dx);
                if (kk < 0 || kk >= grid.Nz) continue;
                foreach (int f in cells) { if (topSolid[f] <= kk) continue; int d = dist[kk * plane + f]; if (d >= 0 && d < best) best = d; }   // IMP-B: air above the roof (a pitched roof's low side) is outdoors, not a leak
            }
            if (best == int.MaxValue) continue;
            // The first wavefront into the room is as wide as the gap it came through.
            int k0 = Math.Max(0, (int)MathF.Floor((storey.Elevation - grid.Origin.Z) / grid.Dx));
            int k1 = Math.Min(grid.Nz - 1, (int)MathF.Floor((storey.Elevation + storey.Height - grid.Origin.Z) / grid.Dx));
            int front = 0;
            for (int kk = k0; kk <= k1; kk++) foreach (int f in cells) if (topSolid[f] > kk && dist[kk * plane + f] == best) front++;
            notes.Add($"{room.Name} is open to the outside through gaps that were not recognised as openings (about {front * grid.Dx * grid.Dx:0.#} m²); " +
                      "results for it include that leakage.");
        }
        return new DetectedRooms(rooms, AggregateNotes(notes), grid.Dx, grid.Count, openings, grid.Origin);
    }

    /// <summary>Heights above each storey's floor where the leak net looks for outside air (the same three planes the real-world audit used).</summary>
    static readonly float[] LeakHeights = [0.4f, 1.1f, 2.0f];

    /// <summary>D7: three or more notes that read the same once their numbers are blanked become one: the first, then how many there were and
    /// where the others were (the text before the first colon or " is ").</summary>
    public static List<string> AggregateNotes(List<string> notes)
    {
        const int MinRepeats = 3;
        var key = (string n) => System.Text.RegularExpressions.Regex.Replace(n, @"\d+(\.\d+)?", "#");
        var counts = notes.GroupBy(key).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<string>();
        var done = new HashSet<string>();
        foreach (var n in notes)
        {
            var group = counts[key(n)];
            if (group.Count < MinRepeats) { result.Add(n); continue; }
            if (!done.Add(key(n))) continue;
            string Lead(string s)
            {
                int c = s.IndexOf(':'), i = s.IndexOf(" is ", StringComparison.Ordinal);
                int cut = c >= 0 && c < 40 ? c : i >= 0 && i < 40 ? i : Math.Min(s.Length, 20);
                return s[..cut];
            }
            result.Add($"{group[0]} ({group.Count} times: {string.Join(", ", group.Skip(1).Take(3).Select(Lead))}{(group.Count > 4 ? ", …" : "")})");
        }
        return result;
    }

    /// <summary>Air reachable from the domain boundary. Everything the solver calls air and this does not
    /// reach is enclosed by the building — that is the whole definition of "indoors" here.</summary>
    static int[] FloodOutside(VoxelGrid grid)
    {
        var outside = new int[grid.Count];   // cells from the domain boundary through air, -1 = not reached (the leak net reads the distance)
        Array.Fill(outside, -1);
        var queue = new Queue<int>();

        void Seed(int x, int y, int z)
        {
            int i = grid.Index(x, y, z);
            if (grid.Cell[i] == VoxelGrid.Solid || outside[i] >= 0) return;
            outside[i] = 0;
            queue.Enqueue(i);
        }

        for (int z = 0; z < grid.Nz; z++)
        for (int y = 0; y < grid.Ny; y++)
        {
            Seed(0, y, z);
            Seed(grid.Nx - 1, y, z);
        }
        for (int z = 0; z < grid.Nz; z++)
        for (int x = 0; x < grid.Nx; x++)
        {
            Seed(x, 0, z);
            Seed(x, grid.Ny - 1, z);
        }
        for (int y = 0; y < grid.Ny; y++)
        for (int x = 0; x < grid.Nx; x++)
        {
            Seed(x, y, 0);
            Seed(x, y, grid.Nz - 1);
        }

        int plane = grid.Nx * grid.Ny, src = 0;
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int z = i / plane, rem = i - z * plane, y = rem / grid.Nx, x = rem % grid.Nx;
            src = i;
            Step(x - 1, y, z); Step(x + 1, y, z);
            Step(x, y - 1, z); Step(x, y + 1, z);
            Step(x, y, z - 1); Step(x, y, z + 1);
        }
        return outside;

        void Step(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 || x >= grid.Nx || y >= grid.Ny || z >= grid.Nz) return;
            int i = grid.Index(x, y, z);
            if (outside[i] >= 0 || grid.Cell[i] == VoxelGrid.Solid) return;
            outside[i] = outside[src] + 1;
            queue.Enqueue(i);
        }
    }

    /// <summary>The grid layer the tallest piece of the model reaches. Above it there is only sky, so the
    /// roof search never has to look there.</summary>
    static int TopLayer(VoxelGrid grid, BuildingModel model)
    {
        float top = float.MinValue;
        foreach (var mesh in model.Obstacles)
            foreach (var v in mesh.Vertices) top = MathF.Max(top, v.Z);
        if (top == float.MinValue) return grid.Nz - 1;
        return Math.Clamp((int)MathF.Ceiling((top - grid.Origin.Z) / grid.Dx), 0, grid.Nz - 1);
    }

    /// <summary>Columns with solid material somewhere ABOVE this plane and below the top of the model: a
    /// roof, a slab or a ceiling. Looking above is the whole point — scanning the entire column instead
    /// finds the solid ground layer under every cell in the domain and calls the sky a room.</summary>
    static bool[] CoveredAbove(VoxelGrid grid, int k, int kTop)
    {
        var covered = new bool[grid.Nx * grid.Ny];
        for (int y = 0; y < grid.Ny; y++)
        for (int x = 0; x < grid.Nx; x++)
            for (int z = k + 1; z <= kTop; z++)
                if (grid.Cell[grid.Index(x, y, z)] == VoxelGrid.Solid) { covered[y * grid.Nx + x] = true; break; }
        return covered;
    }

    /// <summary>4-connected labelling of the indoor air on one horizontal plane. Returns the label count;
    /// <paramref name="labels"/> gets 0 for "not indoor air" and 1..n for the regions.</summary>
    static int LabelPlane(VoxelGrid grid, bool[] covered, int k, int[] labels)
    {
        int next = 0;
        var queue = new Queue<int>();
        for (int y = 0; y < grid.Ny; y++)
        for (int x = 0; x < grid.Nx; x++)
        {
            int flat = y * grid.Nx + x;
            if (labels[flat] != 0) continue;
            int i = grid.Index(x, y, k);
            if (grid.Cell[i] == VoxelGrid.Solid || !covered[flat]) continue;

            int label = ++next;
            labels[flat] = label;
            queue.Enqueue(flat);
            while (queue.Count > 0)
            {
                int f = queue.Dequeue();
                int fy = f / grid.Nx, fx = f - fy * grid.Nx;
                Visit(fx - 1, fy); Visit(fx + 1, fy); Visit(fx, fy - 1); Visit(fx, fy + 1);
            }

            void Visit(int x2, int y2)
            {
                if (x2 < 0 || y2 < 0 || x2 >= grid.Nx || y2 >= grid.Ny) return;
                int f2 = y2 * grid.Nx + x2;
                if (labels[f2] != 0) return;
                int i2 = grid.Index(x2, y2, k);
                if (grid.Cell[i2] == VoxelGrid.Solid || !covered[f2]) return;
                labels[f2] = label;
                queue.Enqueue(f2);
            }
        }
        return next;
    }

    /// <summary>4-connected components of the cells of <paramref name="mask"/> equal to <paramref name="value"/>,
    /// numbered 1..n into <paramref name="parts"/>. Returns n.</summary>
    static int LabelMask(VoxelGrid grid, int[] mask, int value, int[] parts)
    {
        int next = 0;
        var queue = new Queue<int>();
        for (int f0 = 0; f0 < mask.Length; f0++)
        {
            if (mask[f0] != value || parts[f0] != 0) continue;
            int label = ++next;
            parts[f0] = label;
            queue.Enqueue(f0);
            while (queue.Count > 0)
            {
                int f = queue.Dequeue();
                int fy = f / grid.Nx, fx = f - fy * grid.Nx;
                Visit(fx - 1, fy); Visit(fx + 1, fy); Visit(fx, fy - 1); Visit(fx, fy + 1);
            }

            void Visit(int x, int y)
            {
                if (x < 0 || y < 0 || x >= grid.Nx || y >= grid.Ny) return;
                int f2 = y * grid.Nx + x;
                if (mask[f2] != value || parts[f2] != 0) return;
                parts[f2] = label;
                queue.Enqueue(f2);
            }
        }
        return next;
    }

    /// <summary>Plan cells of one part grown back by the inset: its own cells plus the cells of its region within <paramref name="inset"/> cells
    /// (square neighbourhood) of it -- the band <see cref="Erode"/> took off. The room's ACH volume, so the eroded core only has to serve as the cell mask.</summary>
    static List<int> GrownColumns(VoxelGrid grid, int[] labels, int[] parts, int region, int part, int inset)
    {
        var cells = new List<int>();
        for (int y = 0; y < grid.Ny; y++)
        for (int x = 0; x < grid.Nx; x++)
        {
            int f = y * grid.Nx + x;
            if (labels[f] != region) continue;
            bool grown = parts[f] == part;
            for (int dy = -inset; dy <= inset && !grown; dy++)
            for (int dx = -inset; dx <= inset && !grown; dx++)
            {
                int nx = x + dx, ny = y + dy;
                grown = nx >= 0 && ny >= 0 && nx < grid.Nx && ny < grid.Ny && parts[ny * grid.Nx + nx] == part;
            }
            if (grown) cells.Add(f);
        }
        return cells;
    }

    /// <summary>Per plan cell, the highest layer (up to <paramref name="kTop"/>) holding solid material, -1 for none: air below it has a roof, slab or ceiling over it.</summary>
    static int[] TopSolid(VoxelGrid grid, int kTop)
    {
        var top = new int[grid.Nx * grid.Ny];
        for (int f = 0; f < top.Length; f++)
        {
            int z = kTop;
            while (z >= 0 && grid.Cell[z * top.Length + f] != VoxelGrid.Solid) z--;
            top[f] = z;
        }
        return top;
    }

    /// <summary>D6: each room's volume from its 3D air instead of outline area x storey height. The air cells of one storey that are under cover
    /// (solid somewhere above them) are shared out between the rooms of the storey by a simultaneous flood from every room's plane cells,
    /// so a pitched roof's low eaves, a coved ceiling and a ceiling lower than the storey top are measured as they are, and a hole nothing
    /// recognised cannot flood the outdoors (no cover there). The detection grid marks every cell a face touches as solid, so a wall, floor
    /// or ceiling face lies on average half a cell inside the solid cell that holds it: half a cell is added over every air-to-solid face.</summary>
    static float[] RoomVolumes(VoxelGrid grid, List<List<int>> seeds, int kPlane, Storey storey, int[] topSolid)
    {
        int nx = grid.Nx, ny = grid.Ny, plane = nx * ny;
        int kF = Math.Max(0, (int)MathF.Floor((storey.Elevation - grid.Origin.Z) / grid.Dx));
        int kT = Math.Min(grid.Nz - 1, (int)MathF.Ceiling((storey.Elevation + storey.Height - grid.Origin.Z) / grid.Dx) - 1);
        // A column the flood may enter: one a room already owns on the plane, or one where the roof comes down below the 1.1 m plane (the plane cell is
        // solid, or the highest solid is under it): a pitched roof's low eaves. Not open air under an overhang, which a hole nothing recognised would flood.
        var allowed = new bool[plane];
        foreach (var list in seeds) foreach (int f in list) allowed[f] = true;
        for (int f = 0; f < plane; f++)
            if (!allowed[f]) allowed[f] = grid.Cell[kPlane * plane + f] == VoxelGrid.Solid || topSolid[f] < kPlane;
        var owner = new int[grid.Count];
        var cells = new long[seeds.Count];
        var faces = new long[seeds.Count];
        var vertical = new double[seeds.Count];   // cells added at floors and ceilings, whose plane is known better than half a cell
        float storeyTop = storey.Elevation + storey.Height;
        float Bottom(int layer) => grid.Origin.Z + layer * grid.Dx;
        var queue = new Queue<int>();
        for (int r = 0; r < seeds.Count; r++)
            foreach (int f in seeds[r])
            {
                int i = kPlane * plane + f;
                if (grid.Cell[i] == VoxelGrid.Solid || owner[i] != 0) continue;
                owner[i] = r + 1;
                queue.Enqueue(i);
            }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int z = i / plane, f = i - z * plane, y = f / nx, x = f - y * nx, r = owner[i] - 1;
            cells[r]++;
            Visit(x - 1, y, z); Visit(x + 1, y, z); Visit(x, y - 1, z); Visit(x, y + 1, z); Visit(x, y, z - 1); Visit(x, y, z + 1);

            void Visit(int x2, int y2, int z2)
            {
                if (z2 > kT) return;
                if (x2 < 0 || y2 < 0 || x2 >= nx || y2 >= ny || z2 < 0) { faces[r]++; return; }
                int j = z2 * plane + y2 * nx + x2;
                if (grid.Cell[j] == VoxelGrid.Solid)
                {
                    if (z2 == z) faces[r]++;
                    else if (z2 < z)
                    {   // a floor: its top is the storey elevation, not somewhere inside the solid cell
                        if (z == kF) vertical[r] -= Math.Clamp((storey.Elevation - Bottom(z)) / grid.Dx, 0f, 1f);
                        else if (z == kF + 1) vertical[r] += Math.Clamp((Bottom(kF + 1) - storey.Elevation) / grid.Dx, 0f, 1f);
                        else vertical[r] += 0.5;
                    }
                    else   // a ceiling: the storey top when that lies in the solid cell, else the middle of it
                        vertical[r] += Bottom(z2) <= storeyTop && storeyTop <= Bottom(z2 + 1) ? (storeyTop - Bottom(z2)) / grid.Dx : 0.5;
                    return;
                }
                if (z2 < kF || owner[j] != 0 || z2 >= topSolid[y2 * nx + x2] || !allowed[y2 * nx + x2]) return;
                owner[j] = owner[i];
                queue.Enqueue(j);
            }
        }
        return seeds.Select((_, r) => (float)((cells[r] + 0.5 * faces[r] + vertical[r]) * grid.Dx * grid.Dx * grid.Dx)).ToArray();
    }

    /// <summary>Shrinks a region by <paramref name="inset"/> cells on every side (a square neighbourhood; the grid edge counts as outside). The traced
    /// outline would otherwise sit exactly on the wall faces, and Analysis integrates a room's flux across its loop: a voxel staircase riding the
    /// boundary counts air crossing it several times and reports an ACH in the hundreds. Every room in this repo's own scenes is inset off the
    /// wall faces for the same reason; this is that convention, applied automatically.</summary>
    static int[] Erode(VoxelGrid grid, int[] labels, int region, int inset)
    {
        int nx = grid.Nx, ny = grid.Ny, span = 2 * inset + 1;
        var along = new bool[labels.Length];   // the whole row window [x - inset, x + inset] is in the region
        var run = new int[Math.Max(nx, ny) + 1];
        for (int y = 0; y < ny; y++)
        {
            for (int x = 0; x < nx; x++) run[x + 1] = run[x] + (labels[y * nx + x] == region ? 1 : 0);
            for (int x = inset; x + inset < nx; x++) along[y * nx + x] = run[x + inset + 1] - run[x - inset] == span;
        }
        var eroded = new int[labels.Length];
        for (int x = 0; x < nx; x++)
        {
            for (int y = 0; y < ny; y++) run[y + 1] = run[y] + (along[y * nx + x] ? 1 : 0);
            for (int y = inset; y + inset < ny; y++)
                if (run[y + inset + 1] - run[y - inset] == span) eroded[y * nx + x] = region;
        }
        return eroded;
    }

    /// <summary>S3-27: inner loops for the open sky a part encloses. Cells the part does not own that the grid border cannot reach through
    /// non-part cells are enclosed by it; of those, the connected open-sky air (no slab or roof above it on the plane) that holds a
    /// <paramref name="minCells"/> square is a courtyard or atrium and is traced at its own edge (the courtyard side of its wall).
    /// An enclosed core, a column or a light-well slit stays inside the room.</summary>
    static List<Vector2[]> CourtyardLoops(VoxelGrid grid, int[] parts, int part, bool[] openSky, int minCells)
    {
        int nx = grid.Nx, ny = grid.Ny, n = nx * ny;
        var reached = new bool[n];
        var queue = new Queue<int>();
        void Seed(int x, int y)
        {
            int f = y * nx + x;
            if (parts[f] == part || reached[f]) return;
            reached[f] = true; queue.Enqueue(f);
        }
        for (int x = 0; x < nx; x++) { Seed(x, 0); Seed(x, ny - 1); }
        for (int y = 0; y < ny; y++) { Seed(0, y); Seed(nx - 1, y); }
        while (queue.Count > 0)
        {
            int f = queue.Dequeue();
            int fy = f / nx, fx = f - fy * nx;
            if (fx > 0) Seed(fx - 1, fy);
            if (fx < nx - 1) Seed(fx + 1, fy);
            if (fy > 0) Seed(fx, fy - 1);
            if (fy < ny - 1) Seed(fx, fy + 1);
        }

        var sky = new int[n];
        for (int f = 0; f < n; f++) sky[f] = !reached[f] && openSky[f] ? 1 : 0;
        var comps = new int[n];
        int count = LabelMask(grid, sky, 1, comps);
        if (count == 0) return [];

        var big = new bool[count + 1];
        for (int y = 0; y + minCells <= ny; y++)
        for (int x = 0; x + minCells <= nx; x++)
        {
            int c = comps[y * nx + x];
            if (c == 0 || big[c]) continue;
            bool square = true;
            for (int dy = 0; dy < minCells && square; dy++)
            for (int dx = 0; dx < minCells && square; dx++)
                square = comps[(y + dy) * nx + x + dx] == c;
            if (square) big[c] = true;
        }

        var loops = new List<Vector2[]>();
        for (int c = 1; c <= count; c++)
        {
            if (!big[c]) continue;
            var loop = Outline(grid, comps, c);
            if (loop.Length >= 4) loops.Add(loop);
        }
        return loops;
    }

    /// <summary>Traces the outer boundary of one labelled region by walking the cracks between cells, then
    /// drops collinear points. The result is a closed, axis-aligned polygon in world metres — a staircase
    /// around anything not aligned to the grid, which is the documented cost of this approach.</summary>
    static Vector2[] Outline(VoxelGrid grid, int[] labels, int region)
    {
        bool In(int x, int y) =>
            x >= 0 && y >= 0 && x < grid.Nx && y < grid.Ny && labels[y * grid.Nx + x] == region;

        // Start at the lowest-then-leftmost cell of the region; its bottom-left corner is on the hull.
        int sx = -1, sy = -1;
        for (int y = 0; y < grid.Ny && sy < 0; y++)
        for (int x = 0; x < grid.Nx; x++)
            if (In(x, y)) { sx = x; sy = y; break; }
        if (sx < 0) return [];

        // Crack following: walk the boundary keeping the region on the left. Direction 0 = +X, 1 = +Y,
        // 2 = -X, 3 = -Y, on the lattice of cell CORNERS.
        var corners = new List<(int X, int Y)>();
        int cx = sx, cy = sy, dir = 0;
        int guard = 8 * (grid.Nx * grid.Ny + 4);
        do
        {
            corners.Add((cx, cy));
            // Of the two cells straddling the edge we are about to walk, turn to keep the inside on the left.
            int left = (dir + 1) & 3, right = (dir + 3) & 3;
            if (EdgeOpen(cx, cy, left)) dir = left;
            else if (EdgeOpen(cx, cy, dir)) { }
            else if (EdgeOpen(cx, cy, right)) dir = right;
            else dir = (dir + 2) & 3;
            (cx, cy) = Step(cx, cy, dir);
        }
        while ((cx != sx || cy != sy) && --guard > 0);

        // An edge from corner (x, y) in direction d is on the boundary when exactly one of the two cells
        // sharing it is inside the region.
        bool EdgeOpen(int x, int y, int d)
        {
            var (nx, ny) = Step(x, y, d);
            return d switch
            {
                0 => In(x, y) != In(x, y - 1),              // walking +X: cells above and below the edge
                1 => In(x - 1, y) != In(x, y),              // walking +Y: cells left and right
                2 => In(nx, y - 1) != In(nx, y),
                _ => In(x - 1, ny) != In(x, ny),
            };
        }

        static (int, int) Step(int x, int y, int d) => d switch
        {
            0 => (x + 1, y),
            1 => (x, y + 1),
            2 => (x - 1, y),
            _ => (x, y - 1),
        };

        // Drop collinear corners: a 60-cell wall is one edge, not 60.
        var simple = new List<Vector2>(corners.Count);
        for (int i = 0; i < corners.Count; i++)
        {
            var prev = corners[(i - 1 + corners.Count) % corners.Count];
            var here = corners[i];
            var next = corners[(i + 1) % corners.Count];
            if ((here.X - prev.X) * (next.Y - here.Y) == (here.Y - prev.Y) * (next.X - here.X)) continue;
            simple.Add(new Vector2(grid.Origin.X + here.X * grid.Dx, grid.Origin.Y + here.Y * grid.Dx));
        }
        return simple.Count >= 4 ? simple.ToArray() : [];
    }
}

/// <summary>What detection found (rooms, and the openings OpeningDetect found in the walls between them), and
/// what it wants to say about it in the dialog.</summary>
public sealed record DetectedRooms(List<Room> Rooms, List<string> Notes, float Dx, int Cells, List<Opening> Openings, Vector3 Origin);

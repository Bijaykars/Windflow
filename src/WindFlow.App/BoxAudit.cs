using System.Numerics;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>What a simulation box did to the model, measured on the grid the box actually produced. The box
/// gizmo cannot answer either of these on its own:
/// <list type="bullet">
/// <item>Does a face cut through indoor air? Core leaves box faces as ordinary domain boundaries, so a face
/// across a room turns that room's air into free inflow/outflow and every flow near it is only as good as an
/// open boundary. Measured: a box around one bedroom whose +X face crossed the landing fed air in through
/// the bedroom door at 6x the whole-building rate, with the sign reversed.</item>
/// <item>Is the box sealed by walls? Faces that all land inside walls and slabs leave an open ring applied to
/// solid cells with no air path to the wind, and the solver diverges in the first burst.</item>
/// </list>
/// Rooms are tested geometrically (point in loop, plus the room's Z range) rather than by name.</summary>
public sealed record BoxAudit(float RingAirFraction, float[] FaceAir, string[] CutRoomIds, string[] CutRoomNames,
    int DroppedRooms, (Vector3 Min, Vector3 Max)[] CutBounds)
{
    public static readonly BoxAudit None = new(1f, [1, 1, 1, 1, 1, 1], [], [], 0, []);

    /// <summary>Air share of each face, in the order -X, +X, -Y, +Y, -Z, +Z.</summary>
    public string FaceReport() =>
        $"-X {FaceAir[0] * 100:0} +X {FaceAir[1] * 100:0} -Y {FaceAir[2] * 100:0} +Y {FaceAir[3] * 100:0} " +
        $"-Z {FaceAir[4] * 100:0} +Z {FaceAir[5] * 100:0} %";

    /// <summary>A box face runs through a room's air.</summary>
    public bool CutsIndoorAir => CutRoomIds.Length > 0;

    /// <summary>Below this share of the boundary ring in air the box is effectively sealed. 2 % is well under
    /// anything real (a single 1 m window on a 7 x 10 m box face is already ~1.5 % of the whole ring) and well
    /// over the sealed case, which measured 0 air ring cells.</summary>
    public const float SealedRingFraction = 0.02f;

    public bool IsSealed => RingAirFraction < SealedRingFraction;

    /// <summary>Is this point inside (or within half a metre of) a room a box face cuts? Openings sit in the
    /// wall plane rather than inside the room, hence the margin.</summary>
    public bool NearCutRoom(Vector3 p)
    {
        foreach (var (min, max) in CutBounds)
            if (p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z)
                return true;
        return false;
    }

    /// <summary>One pass over the grid's boundary ring for the cut test and the air fraction, then one small
    /// scan per room for the dropped count. Only worth calling when a box is active; a whole-building grid
    /// has its wind-tunnel margin between the ring and any room, so nothing would ever be flagged.</summary>
    public static BoxAudit Inspect(VoxelGrid grid, BuildingModel model)
    {
        if (grid.Count == 0 || model.Rooms.Count == 0) return None;
        var bounds = new (Vector3 Min, Vector3 Max)[model.Rooms.Count];
        for (int r = 0; r < model.Rooms.Count; r++) bounds[r] = Bounds(model.Rooms[r]);

        var cut = new bool[model.Rooms.Count];
        long ring = 0, ringAir = 0;
        var faceCells = new long[6];
        var faceAirCells = new long[6];
        for (int z = 0; z < grid.Nz; z++)
        for (int y = 0; y < grid.Ny; y++)
        for (int x = 0; x < grid.Nx; x++)
        {
            bool onRing = x == 0 || y == 0 || z == 0 || x == grid.Nx - 1 || y == grid.Ny - 1 || z == grid.Nz - 1;
            if (!onRing) continue;
            ring++;
            bool air = grid.Cell[grid.Index(x, y, z)] != VoxelGrid.Solid;
            if (air) ringAir++;
            // A cell can be on two faces (an edge); both faces count it, which is what "is this face open"
            // wants to know.
            Face(0, x == 0); Face(1, x == grid.Nx - 1);
            Face(2, y == 0); Face(3, y == grid.Ny - 1);
            Face(4, z == 0); Face(5, z == grid.Nz - 1);
            void Face(int f, bool on)
            {
                if (!on) return;
                faceCells[f]++;
                if (air) faceAirCells[f]++;
            }

            if (!air) continue;
            int room = RoomAt(model, bounds, grid.CellCenter(x, y, z));
            if (room >= 0) cut[room] = true;
        }
        var faceAir = new float[6];
        for (int f = 0; f < 6; f++) faceAir[f] = faceCells[f] > 0 ? (float)faceAirCells[f] / faceCells[f] : 0f;

        var ids = new List<string>();
        var names = new List<string>();
        var cutBounds = new List<(Vector3, Vector3)>();
        var pad = new Vector3(0.5f);
        for (int r = 0; r < cut.Length; r++)
        {
            if (!cut[r]) continue;
            ids.Add(model.Rooms[r].Id);
            names.Add(model.Rooms[r].Name);
            cutBounds.Add((bounds[r].Min - pad, bounds[r].Max + pad));
        }

        int dropped = 0;
        for (int r = 0; r < model.Rooms.Count; r++)
            if (!HasAir(grid, model.Rooms[r], bounds[r])) dropped++;

        return new BoxAudit(ring > 0 ? (float)ringAir / ring : 1f, faceAir, [.. ids], [.. names], dropped, [.. cutBounds]);
    }

    static (Vector3 Min, Vector3 Max) Bounds(Room room)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var loop in room.Loops)
            foreach (var p in loop)
            {
                minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
                minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
            }
        if (minX > maxX) return (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        return (new Vector3(minX, minY, room.ZMin), new Vector3(maxX, maxY, room.ZMax));
    }

    /// <summary>Index of the room containing this point, or -1 (no precomputed bounds: one-off lookups).</summary>
    public static int RoomIndex(BuildingModel model, Vector3 p)
    {
        var bounds = new (Vector3 Min, Vector3 Max)[model.Rooms.Count];
        for (int r = 0; r < bounds.Length; r++) bounds[r] = Bounds(model.Rooms[r]);
        return RoomAt(model, bounds, p);
    }

    /// <summary>Index of the room containing this point, or -1. Bounding box first, then the real loops.</summary>
    static int RoomAt(BuildingModel model, (Vector3 Min, Vector3 Max)[] bounds, Vector3 p)
    {
        for (int r = 0; r < model.Rooms.Count; r++)
        {
            var (min, max) = bounds[r];
            if (p.X < min.X || p.X > max.X || p.Y < min.Y || p.Y > max.Y || p.Z < min.Z || p.Z > max.Z) continue;
            if (InLoops(model.Rooms[r], p.X, p.Y)) return r;
        }
        return -1;
    }

    static bool InLoops(Room room, float x, float y)
    {
        bool inside = false;
        foreach (var loop in room.Loops)
        {
            for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
            {
                var a = loop[i];
                var b = loop[j];
                if (a.Y > y != b.Y > y && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>Does this room own at least one air cell in the grid? A box that excludes the room leaves an
    /// empty index range, so this is cheap for exactly the rooms that were dropped.</summary>
    static bool HasAir(VoxelGrid grid, Room room, (Vector3 Min, Vector3 Max) bounds)
    {
        if (bounds.Min.X > bounds.Max.X) return true;    // no footprint: not the box's doing
        int x0 = CellIndex(bounds.Min.X, grid.Origin.X, grid.Dx), x1 = CellIndex(bounds.Max.X, grid.Origin.X, grid.Dx);
        int y0 = CellIndex(bounds.Min.Y, grid.Origin.Y, grid.Dx), y1 = CellIndex(bounds.Max.Y, grid.Origin.Y, grid.Dx);
        int z0 = CellIndex(bounds.Min.Z, grid.Origin.Z, grid.Dx), z1 = CellIndex(bounds.Max.Z, grid.Origin.Z, grid.Dx);
        x0 = Math.Max(x0, 0); y0 = Math.Max(y0, 0); z0 = Math.Max(z0, 0);
        x1 = Math.Min(x1, grid.Nx - 1); y1 = Math.Min(y1, grid.Ny - 1); z1 = Math.Min(z1, grid.Nz - 1);
        for (int z = z0; z <= z1; z++)
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            if (grid.Cell[grid.Index(x, y, z)] == VoxelGrid.Solid) continue;
            var c = grid.CellCenter(x, y, z);
            if (InLoops(room, c.X, c.Y)) return true;
        }
        return false;
    }

    static int CellIndex(float world, float origin, float dx) => (int)MathF.Floor((world - origin) / dx);
}

/// <summary>The nested box's sponge (blend) band: the strip inside every driven face where the fine run is pulled
/// toward the coarse one. An opening or device outlet in it gets little from the finer grid. The same test Core's
/// NestedSession.WarnSpongeBand applies (extent clipped to the box, nearest driven face, bottom and top only when
/// the box is nested there), done here on the UI model so the rows can be tagged and the box grown to clear them.</summary>
public static class BlendBand
{
    static readonly string[] FaceNames = ["-X", "+X", "-Y", "+Y", "bottom", "top"];

    /// <summary>World extent of an opening (width along the wall, depth through it, height), as Core measures it.</summary>
    public static (Vector3 A, Vector3 B) Extent(Opening o)
    {
        var n = new Vector3(o.Normal.X, o.Normal.Y, 0f);
        n = n.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(n);
        var t = new Vector3(-n.Y, n.X, 0f);
        var half = new Vector3(MathF.Abs(t.X) * o.Width + MathF.Abs(n.X) * o.Depth,
            MathF.Abs(t.Y) * o.Width + MathF.Abs(n.Y) * o.Depth, o.Height) * 0.5f;
        return (o.Center - half, o.Center + half);
    }

    /// <summary>A device outlet's extent: its centre plus half its width/height on every axis (a bound, not the
    /// exact disc; Core tests the driven cells, which sit inside it).</summary>
    public static (Vector3 A, Vector3 B) Extent(AirSource s)
    {
        float r = MathF.Max(s.Radius, 0.5f * MathF.Max(s.Width, s.Height));
        var half = new Vector3(r, r, r);
        return (s.Center - half, s.Center + half);
    }

    /// <summary>Distance (m) from the part of [a, b] inside the box to the nearest driven face, and which face
    /// (0..5 = -X, +X, -Y, +Y, bottom, top); null when [a, b] misses the box.</summary>
    public static (float Dist, int Face)? Nearest(DomainBox box, Vector3 a, Vector3 b, bool bottom, bool top)
    {
        var lo = box.Min; var hi = box.Max;
        if (b.X <= lo.X || a.X >= hi.X || b.Y <= lo.Y || a.Y >= hi.Y || b.Z <= lo.Z || a.Z >= hi.Z) return null;
        a = Vector3.Max(a, lo); b = Vector3.Min(b, hi);
        Span<float> d = [a.X - lo.X, hi.X - b.X, a.Y - lo.Y, hi.Y - b.Y,
            bottom ? a.Z - lo.Z : float.MaxValue, top ? hi.Z - b.Z : float.MaxValue];
        int best = 0;
        for (int i = 1; i < 6; i++) if (d[i] < d[best]) best = i;
        return (MathF.Max(0f, d[best]), best);
    }

    public static string FaceName(int face) => FaceNames[Math.Clamp(face, 0, 5)];

    /// <summary>In the band: inside the box and nearer than <paramref name="band"/> to a driven face.</summary>
    public static bool Inside(DomainBox box, (Vector3 A, Vector3 B) e, float band, bool bottom, bool top) =>
        band > 0 && Nearest(box, e.A, e.B, bottom, top) is { } n && n.Dist < band;

    /// <summary>The box grown, face by face, until nothing in <paramref name="items"/> that touches it sits in the
    /// band: each offending face moves out so the item's far edge is a band plus one fine cell inside it. Growing can
    /// pull new items in, so it repeats (at most 8 rounds). The bottom never goes below the ground, where the box
    /// stops being nested anyway.
    /// ponytail: grows only; an item straddling a face might be better left outside by shrinking, not done.</summary>
    public static DomainBox Grow(DomainBox box, IReadOnlyList<(Vector3 A, Vector3 B)> items, float band, float dx,
        bool bottom, bool top, float groundZ)
    {
        var lo = box.Min; var hi = box.Max;
        float clear = band + dx;
        for (int round = 0; round < 8; round++)
        {
            bool moved = false;
            foreach (var (a, b) in items)
            {
                bool nestedBottom = bottom && lo.Z > groundZ + 1e-3f;
                if (Nearest(new DomainBox(lo, hi), a, b, nestedBottom, top) is not { } n || n.Dist >= band) continue;
                switch (n.Face)
                {
                    case 0: lo.X = MathF.Min(lo.X, a.X - clear); break;
                    case 1: hi.X = MathF.Max(hi.X, b.X + clear); break;
                    case 2: lo.Y = MathF.Min(lo.Y, a.Y - clear); break;
                    case 3: hi.Y = MathF.Max(hi.Y, b.Y + clear); break;
                    case 4: lo.Z = MathF.Max(groundZ, MathF.Min(lo.Z, a.Z - clear)); break;
                    default: hi.Z = MathF.Max(hi.Z, b.Z + clear); break;
                }
                moved = true;
            }
            if (!moved) break;
        }
        return new DomainBox(lo, hi);
    }
}

using System.Numerics;
using System.Runtime.InteropServices;
using WindFlow.Core;

namespace WindFlow.App.Viewport;

[StructLayout(LayoutKind.Sequential)]
public struct Vertex(Vector3 pos, Vector3 normal)
{
    public Vector3 Pos = pos;
    public Vector3 Nrm = normal;
    public const int Size = 24;
}

/// <summary>One draw range inside the single scene vertex buffer.</summary>
public readonly record struct DrawRange(int Start, int Count);

/// <summary>A room as a pickable floor, with the outline range that highlights just that one, and its volume
/// (the floor polygon extruded to the room height) as a translucent block plus the block's edges.</summary>
public readonly record struct RoomPick(int Index, string Name, List<Vector2[]> Loops, float ZMin, float ZMax,
    DrawRange Outline, Vector3 Center, float Radius, DrawRange Block = default, DrawRange BlockEdges = default);

/// <summary>An opening as a pickable quad, with the draw ranges that highlight just that one.</summary>
/// <summary>A device glyph as a pick target: picked in screen space against its projected centre, the same
/// way the box face handles are, so a distant fan is still clickable.</summary>
/// <summary><paramref name="On"/> is the device's Enabled flag: a switched-off device draws no halo.</summary>
/// <summary>A device as the picker and the gizmo see it. <paramref name="Normal"/> is the mount plane's normal
/// (the wall's for a wall unit, Z otherwise), <paramref name="FreeHeight"/> says a wall unit may slide up and
/// down, <paramref name="Rotatable"/> that R (and the yaw ring) turn it, <paramref name="Yaw"/> its compass heading.</summary>
public readonly record struct DevicePick(int Index, Vector3 Center, float Radius, bool On = true,
    Vector3 Normal = default, bool Rotatable = false, bool FreeHeight = false, float Yaw = 0f);

public readonly record struct PickQuad(int Index, Vector3 Center, Vector3 Normal, Vector3 Tangent,
    float HalfWidth, float HalfHeight, DrawRange Quad, DrawRange Lines);

/// <summary>CPU-side geometry for one BuildingModel: everything lands in one vertex buffer and is drawn as
/// ranges, so a whole building costs a handful of draw calls.</summary>
/// <summary>What the Rooms and Openings sections draw over the building while the user is choosing: the detected spaces that are not a room yet, and the
/// openings that were deleted (dashed outlines, pickable to restore them).</summary>
public sealed record SceneOverlay(IReadOnlyList<Room> Spaces, IReadOnlyList<Opening> Deleted);

public sealed class Scene
{
    public string Name = "";
    public Vertex[] Vertices = [];
    public DrawRange Ground, Walls, Edges, OpenQuads, ClosedQuads, OpenLines, ClosedLines, GridMinor, GridMajor, WindArrow;
    public DrawRange RoomOutlines, RoomBlocks, RoomBlockEdges, Probes, DeviceGlyphs, DeviceSelected, DeviceGhost;
    /// <summary>Glyphs of switched-off devices, drawn dimmed so they read as "off" rather than as a glitch.</summary>
    public DrawRange DeviceOff;
    public DevicePick[] DevicePicks = [];
    /// <summary>The placement ghost, so it gets the same dark halo as a placed glyph and reads over the particles.</summary>
    public DevicePick? GhostPick;
    public int Triangles, OpeningCount, OpeningsOpen;
    public Opening[] OpeningList = [];
    public PickQuad[] Picks = [];
    public RoomPick[] Rooms = [];
    /// <summary>The overlay's spaces as room picks (Index = position in <see cref="SceneOverlay.Spaces"/>), with their own ranges; the deleted openings' dashed outlines and pick quads.</summary>
    public RoomPick[] Spaces = [];
    public DrawRange SpaceOutlines, SpaceBlocks, SpaceBlockEdges, DeletedLines;
    public PickQuad[] DeletedPicks = [];
    public Vector3 Min, Max;
    public float GridFade = 120;
    public Vector3 WindHandle;
    public bool HasWind;

    public Vector3 Center => (Min + Max) * 0.5f;
    public float Radius => MathF.Max(0.5f, (Max - Min).Length() * 0.5f);

    /// <summary><paramref name="wind"/> is the velocity the air moves at, in model axes; a non-zero value
    /// draws the ground arrow upwind of the building.</summary>
    public static Scene Build(BuildingModel model, Vector2 wind = default, IReadOnlyList<Vector3>? probes = null,
        int selectedDevice = -1, AirSource? ghost = null, SceneOverlay? overlay = null)
    {
        var scene = new Scene { Name = string.IsNullOrWhiteSpace(model.Name) ? "model" : model.Name };
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var mesh in model.Obstacles)
            foreach (var v in mesh.Vertices) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        if (min.X > max.X) { min = new Vector3(-5, -5, 0); max = new Vector3(5, 5, 3); }
        scene.Min = min;
        scene.Max = max;

        // Flat normals: every triangle gets its own three vertices. Buildings are boxes; smooth normals would
        // only round the corners. The shader flips the normal toward the eye, so mesh winding does not matter.
        var ground = new List<Vertex>();
        var walls = new List<Vertex>();
        var edges = new List<Vertex>();
        int tris = 0;
        foreach (var mesh in model.Obstacles)
        {
            float meshTop = float.MinValue;
            foreach (var v in mesh.Vertices) meshTop = MathF.Max(meshTop, v.Z);
            var target = meshTop <= model.GroundZ + 0.05f ? ground : walls;
            var creases = new Dictionary<(Vector3, Vector3), (Vector3 Normal, bool Crease)>();
            for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
            {
                Vector3 a = mesh.Vertices[mesh.Indices[i]], b = mesh.Vertices[mesh.Indices[i + 1]], c = mesh.Vertices[mesh.Indices[i + 2]];
                var n = Vector3.Cross(b - a, c - a);
                n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
                target.Add(new Vertex(a, n));
                target.Add(new Vertex(b, n));
                target.Add(new Vertex(c, n));
                Crease(creases, a, b, n);
                Crease(creases, b, c, n);
                Crease(creases, c, a, n);
                tris++;
            }
            foreach (var ((a, b), state) in creases)
                if (state.Crease) Edge(edges, a, b, state.Normal);
        }
        scene.Triangles = tris;

        // Openings: a filled quad on the outward wall face plus an outline on both faces, and a per-opening
        // record so the viewport can hover and click them.
        var openQ = new List<Vertex>();
        var closedQ = new List<Vertex>();
        var openL = new List<Vertex>();
        var closedL = new List<Vertex>();
        scene.OpeningList = model.Openings.ToArray();
        var picks = new PickQuad[scene.OpeningList.Length];
        for (int oi = 0; oi < scene.OpeningList.Length; oi++)
        {
            var o = scene.OpeningList[oi];
            var n = o.Normal with { Z = 0 };
            n = n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) : Vector3.UnitX;
            var t = new Vector3(-n.Y, n.X, 0);                       // same tangent the voxelizer uses
            float halfW = MathF.Max(o.Width, 0.05f) * 0.5f, halfH = MathF.Max(o.Height, 0.05f) * 0.5f;
            var u = t * halfW;
            var v = new Vector3(0, 0, halfH);
            float off = MathF.Max(o.Depth, 0.02f) * 0.5f + 0.012f;   // sit just proud of each wall face
            var quads = o.IsOpen ? openQ : closedQ;
            var lines = o.IsOpen ? openL : closedL;
            int quadStart = quads.Count, lineStart = lines.Count;

            for (int s = -1; s <= 1; s += 2)
            {
                var c = o.Center + n * (off * s);
                Vector3 p00 = c - u - v, p10 = c + u - v, p11 = c + u + v, p01 = c - u + v;
                var nn = n * s;
                if (s > 0)
                {
                    quads.Add(new Vertex(p00, nn)); quads.Add(new Vertex(p10, nn)); quads.Add(new Vertex(p11, nn));
                    quads.Add(new Vertex(p00, nn)); quads.Add(new Vertex(p11, nn)); quads.Add(new Vertex(p01, nn));
                }
                Edge(lines, p00, p10, nn); Edge(lines, p10, p11, nn);
                Edge(lines, p11, p01, nn); Edge(lines, p01, p00, nn);
            }

            picks[oi] = new PickQuad(oi, o.Center, n, t, halfW, halfH,
                new DrawRange(quadStart, quads.Count - quadStart), new DrawRange(lineStart, lines.Count - lineStart));
            scene.OpeningCount++;
            if (o.IsOpen) scene.OpeningsOpen++;
        }

        // Ground grid, centred on the model, 1 m minor / 5 m major.
        var gridMinor = new List<Vertex>();
        var gridMajor = new List<Vertex>();
        float cx = MathF.Round((min.X + max.X) * 0.5f), cy = MathF.Round((min.Y + max.Y) * 0.5f);
        float half = MathF.Ceiling(MathF.Max(max.X - min.X, max.Y - min.Y) * 1.8f + 10f);
        float z = model.GroundZ + 0.006f;
        var up = Vector3.UnitZ;
        for (float i = -half; i <= half; i += 1)
        {
            var list = MathF.Abs(i % 5f) < 1e-3f ? gridMajor : gridMinor;
            Edge(list, new Vector3(cx + i, cy - half, z), new Vector3(cx + i, cy + half, z), up);
            Edge(list, new Vector3(cx - half, cy + i, z), new Vector3(cx + half, cy + i, z), up);
        }
        scene.GridFade = half * 1.25f;

        // Room floors: an outline per analysed room, so one room can be highlighted out of the batch.
        var roomLines = new List<Vertex>();
        var blocks = new List<Vertex>();
        var blockEdges = new List<Vertex>();
        var rooms = new List<RoomPick>();
        bool anyAnalysed = model.Storeys.Any(st => st.Analysed);
        for (int ri = 0; ri < model.Rooms.Count; ri++)
        {
            var room = model.Rooms[ri];
            if (room.Loops.Count == 0) continue;
            if (anyAnalysed && !string.IsNullOrEmpty(room.Level) && model.StoreyOf(room) is not { Analysed: true }) continue;

            rooms.Add(RoomGeometry(room, ri, roomLines, blocks, blockEdges));
        }

        var spaceLines = new List<Vertex>();
        var spaceBlocks = new List<Vertex>();
        var spaceEdges = new List<Vertex>();
        var spaces = new List<RoomPick>();
        var deletedLines = new List<Vertex>();
        var deletedPicks = new List<PickQuad>();
        if (overlay != null)
        {
            for (int si = 0; si < overlay.Spaces.Count; si++)
                if (overlay.Spaces[si].Loops.Count > 0) spaces.Add(RoomGeometry(overlay.Spaces[si], si, spaceLines, spaceBlocks, spaceEdges));
            for (int di = 0; di < overlay.Deleted.Count; di++)
            {
                var o = overlay.Deleted[di];
                var n = o.Normal with { Z = 0 };
                n = n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) : Vector3.UnitX;
                var t = new Vector3(-n.Y, n.X, 0);
                float halfW = MathF.Max(o.Width, 0.05f) * 0.5f, halfH = MathF.Max(o.Height, 0.05f) * 0.5f;
                float off = MathF.Max(o.Depth, 0.02f) * 0.5f + 0.02f;
                for (int side = -1; side <= 1; side += 2)
                {
                    var c = o.Center + n * (off * side);
                    Vector3 p00 = c - t * halfW - new Vector3(0, 0, halfH), p10 = c + t * halfW - new Vector3(0, 0, halfH);
                    Vector3 p11 = c + t * halfW + new Vector3(0, 0, halfH), p01 = c - t * halfW + new Vector3(0, 0, halfH);
                    Dashed(deletedLines, p00, p10, n * side); Dashed(deletedLines, p10, p11, n * side);
                    Dashed(deletedLines, p11, p01, n * side); Dashed(deletedLines, p01, p00, n * side);
                }
                deletedPicks.Add(new PickQuad(di, o.Center, n, t, halfW, halfH, default, default));
            }
        }

        // Probe pins: a small cross with a stem, so a pin reads at any camera angle.
        var pins = new List<Vertex>();
        if (probes != null)
            foreach (var pt in probes)
            {
                Edge(pins, pt - new Vector3(0.22f, 0, 0), pt + new Vector3(0.22f, 0, 0), Vector3.UnitZ);
                Edge(pins, pt - new Vector3(0, 0.22f, 0), pt + new Vector3(0, 0.22f, 0), Vector3.UnitZ);
                Edge(pins, pt, pt + new Vector3(0, 0, 0.55f), Vector3.UnitZ);
                Edge(pins, pt + new Vector3(0, 0, 0.55f), pt + new Vector3(0.12f, 0, 0.42f), Vector3.UnitZ);
                Edge(pins, pt + new Vector3(0, 0, 0.55f), pt + new Vector3(-0.12f, 0, 0.42f), Vector3.UnitZ);
            }

        // Device glyphs: the selected one is drawn in its own range so it can take the highlight colour,
        // and the placement ghost is a third range drawn only while the palette is placing.
        var devices = new List<Vertex>();
        var deviceSel = new List<Vertex>();
        var deviceGhost = new List<Vertex>();
        var deviceOff = new List<Vertex>();
        var devicePicks = new List<DevicePick>();
        for (int i = 0; i < model.Sources.Count; i++)
        {
            var src = model.Sources[i];
            var into = i == selectedDevice ? deviceSel : src.Enabled ? devices : deviceOff;
            int start = into.Count;
            DeviceGlyph(into, src);
            // The same mount rules the body drag uses (MainWindowDevices.OnDeviceDragBegan), so the gizmo
            // offers exactly the moves a drag will honour.
            var preset = Devices.PresetFor(src.Device);
            bool wall = preset.Surface == MountSurface.Wall;
            devicePicks.Add(new DevicePick(i, src.Center, DeviceGlyphRadius(src), src.Enabled,
                wall ? Devices.Horizontal(src.Direction) : Vector3.UnitZ, Devices.Rotatable(src.Device),
                wall && preset.MountHeight <= 0, Devices.Yaw(src.Direction)));
        }
        if (ghost != null)
        {
            DeviceGlyph(deviceGhost, ghost);
            scene.GhostPick = new DevicePick(-1, ghost.Center, DeviceGlyphRadius(ghost));
        }
        scene.DevicePicks = devicePicks.ToArray();

        var arrow = new List<Vertex>();
        scene.HasWind = BuildWindArrow(arrow, model, min, max, wind, out var windTip);
        scene.WindHandle = windTip;

        var all = new List<Vertex>(ground.Count + walls.Count + edges.Count + openQ.Count + closedQ.Count +
                                   openL.Count + closedL.Count + gridMinor.Count + gridMajor.Count + arrow.Count +
                                   roomLines.Count + blocks.Count + blockEdges.Count + pins.Count);
        scene.Ground = Append(all, ground);
        scene.Walls = Append(all, walls);
        scene.Edges = Append(all, edges);
        scene.OpenQuads = Append(all, openQ);
        scene.ClosedQuads = Append(all, closedQ);
        scene.OpenLines = Append(all, openL);
        scene.ClosedLines = Append(all, closedL);
        scene.GridMinor = Append(all, gridMinor);
        scene.GridMajor = Append(all, gridMajor);
        scene.WindArrow = Append(all, arrow);
        scene.RoomOutlines = Append(all, roomLines);
        scene.RoomBlocks = Append(all, blocks);
        scene.RoomBlockEdges = Append(all, blockEdges);
        scene.SpaceOutlines = Append(all, spaceLines);
        scene.SpaceBlocks = Append(all, spaceBlocks);
        scene.SpaceBlockEdges = Append(all, spaceEdges);
        scene.DeletedLines = Append(all, deletedLines);
        scene.DeletedPicks = deletedPicks.ToArray();
        scene.Probes = Append(all, pins);
        scene.DeviceGlyphs = Append(all, devices);
        scene.DeviceSelected = Append(all, deviceSel);
        scene.DeviceGhost = Append(all, deviceGhost);
        scene.DeviceOff = Append(all, deviceOff);
        scene.Vertices = all.ToArray();

        for (int i = 0; i < rooms.Count; i++)
            rooms[i] = rooms[i] with
            {
                Outline = new DrawRange(scene.RoomOutlines.Start + rooms[i].Outline.Start, rooms[i].Outline.Count),
                Block = new DrawRange(scene.RoomBlocks.Start + rooms[i].Block.Start, rooms[i].Block.Count),
                BlockEdges = new DrawRange(scene.RoomBlockEdges.Start + rooms[i].BlockEdges.Start, rooms[i].BlockEdges.Count),
            };
        scene.Rooms = rooms.ToArray();
        for (int i = 0; i < spaces.Count; i++)
            spaces[i] = spaces[i] with
            {
                Outline = new DrawRange(scene.SpaceOutlines.Start + spaces[i].Outline.Start, spaces[i].Outline.Count),
                Block = new DrawRange(scene.SpaceBlocks.Start + spaces[i].Block.Start, spaces[i].Block.Count),
                BlockEdges = new DrawRange(scene.SpaceBlockEdges.Start + spaces[i].BlockEdges.Start, spaces[i].BlockEdges.Count),
            };
        scene.Spaces = spaces.ToArray();

        // Per-opening ranges were recorded relative to their list; shift them into the combined buffer.
        for (int i = 0; i < picks.Length; i++)
        {
            bool open = scene.OpeningList[i].IsOpen;
            var q = open ? scene.OpenQuads : scene.ClosedQuads;
            var l = open ? scene.OpenLines : scene.ClosedLines;
            picks[i] = picks[i] with
            {
                Quad = new DrawRange(q.Start + picks[i].Quad.Start, picks[i].Quad.Count),
                Lines = new DrawRange(l.Start + picks[i].Lines.Start, picks[i].Lines.Count),
            };
        }
        scene.Picks = picks;
        return scene;
    }

    /// <summary>One room (or candidate space) as a pick: its outline lines at floor level, its extruded block and the block's edges, appended to the given lists (ranges relative to them).</summary>
    static RoomPick RoomGeometry(Room room, int index, List<Vertex> roomLines, List<Vertex> blocks, List<Vertex> blockEdges)
    {
        int start = roomLines.Count;
        float floorZ = room.ZMin + 0.03f;
        float rminX = float.MaxValue, rminY = float.MaxValue, rmaxX = float.MinValue, rmaxY = float.MinValue;
        foreach (var loop in room.Loops)
            for (int i = 0; i < loop.Length; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Length];
                Edge(roomLines, new Vector3(a.X, a.Y, floorZ), new Vector3(b.X, b.Y, floorZ), Vector3.UnitZ);
                rminX = MathF.Min(rminX, a.X); rmaxX = MathF.Max(rmaxX, a.X);
                rminY = MathF.Min(rminY, a.Y); rmaxY = MathF.Max(rmaxY, a.Y);
            }
        var centre = new Vector3((rminX + rmaxX) * 0.5f, (rminY + rmaxY) * 0.5f, (room.ZMin + room.ZMax) * 0.5f);
        float radius = 0.5f * new Vector3(rmaxX - rminX, rmaxY - rminY, room.ZMax - room.ZMin).Length();
        int blockStart = blocks.Count, edgeStart = blockEdges.Count;
        RoomBlock(blocks, blockEdges, room);
        return (new RoomPick(index, room.Name, room.Loops, room.ZMin, room.ZMax,
            new DrawRange(start, roomLines.Count - start), centre, MathF.Max(radius, 1f),
            new DrawRange(blockStart, blocks.Count - blockStart), new DrawRange(edgeStart, blockEdges.Count - edgeStart)));
    }

    /// <summary>A dashed line: 0.15 m dashes with 0.1 m gaps.</summary>
    static void Dashed(List<Vertex> list, Vector3 a, Vector3 b, Vector3 n)
    {
        float len = Vector3.Distance(a, b);
        if (len < 1e-4f) return;
        var d = (b - a) / len;
        for (float s = 0; s < len; s += 0.25f) Edge(list, a + d * s, a + d * MathF.Min(s + 0.15f, len), n);
    }

    /// <summary>A flat arrow on the ground upwind of the building, pointing the way the air moves.</summary>
    static bool BuildWindArrow(List<Vertex> arrow, BuildingModel model, Vector3 min, Vector3 max, Vector2 wind, out Vector3 tipOut)
    {
        tipOut = default;
        if (wind.LengthSquared() < 1e-6f) return false;
        var dir = Vector3.Normalize(new Vector3(wind.X, wind.Y, 0));
        var side = new Vector3(-dir.Y, dir.X, 0);
        var centre = (min + max) * 0.5f;
        // Sit just off the upwind face, whichever way the wind comes from, so the arrow stays in frame when
        // the camera is framed on the building.
        float reach = MathF.Abs(dir.X) * (max.X - min.X) * 0.5f + MathF.Abs(dir.Y) * (max.Y - min.Y) * 0.5f;
        float z = model.GroundZ + 0.03f;
        var tail = new Vector3(centre.X, centre.Y, z) - dir * (reach + 4.5f);
        var tip = tail + dir * 3.2f;
        var up = Vector3.UnitZ;

        Edge(arrow, tail, tip, up);
        Edge(arrow, tip, tip - dir * 1.1f + side * 0.7f, up);
        Edge(arrow, tip, tip - dir * 1.1f - side * 0.7f, up);
        for (int i = -1; i <= 1; i += 2)     // two short feathers, so it reads as a wind direction, not a line
        {
            var o = side * (0.5f * i);
            Edge(arrow, tail + o + dir * 0.5f, tip + o - dir * 1.3f, up);
        }
        tipOut = tip;
        return true;
    }

    /// <summary>Ray against a room floor: the horizontal plane just above ZMin, inside the room loops.</summary>
    public static bool HitRoom(in RoomPick r, Vector3 origin, Vector3 direction, out float distance)
    {
        distance = 0;
        if (MathF.Abs(direction.Z) < 1e-6f) return false;
        distance = (r.ZMin + 0.03f - origin.Z) / direction.Z;
        if (distance <= 0) return false;
        var hit = origin + direction * distance;
        return Inside(r.Loops, new Vector2(hit.X, hit.Y));
    }

    /// <summary>Even-odd fill, matching Room.Loops' documented rule.</summary>
    public static bool Inside(List<Vector2[]> loops, Vector2 p)
    {
        bool inside = false;
        foreach (var loop in loops)
            for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
                if (loop[i].Y > p.Y != loop[j].Y > p.Y &&
                    p.X < (loop[j].X - loop[i].X) * (p.Y - loop[i].Y) / (loop[j].Y - loop[i].Y) + loop[i].X)
                    inside = !inside;
        return inside;
    }

    /// <summary>Ray against an opening quad, in the plane through its centre. Returns the hit distance.</summary>
    public static bool Hit(in PickQuad q, Vector3 origin, Vector3 direction, out float distance)
    {
        distance = 0;
        float denom = Vector3.Dot(direction, q.Normal);
        if (MathF.Abs(denom) < 1e-6f) return false;
        distance = Vector3.Dot(q.Center - origin, q.Normal) / denom;
        if (distance <= 0) return false;
        var local = origin + direction * distance - q.Center;
        return MathF.Abs(Vector3.Dot(local, q.Tangent)) <= q.HalfWidth && MathF.Abs(local.Z) <= q.HalfHeight;
    }

    /// <summary>Collects the edges worth drawing as an outline: unshared edges and edges whose two faces meet
    /// at more than ~20°. On a box that is exactly the twelve corners, and it works on imported meshes too.</summary>
    static void Crease(Dictionary<(Vector3, Vector3), (Vector3 Normal, bool Crease)> map, Vector3 a, Vector3 b, Vector3 n)
    {
        var key = Before(a, b) ? (a, b) : (b, a);
        map[key] = map.TryGetValue(key, out var seen)
            ? (seen.Normal, Vector3.Dot(seen.Normal, n) < 0.94f)
            : (n, true);
    }

    static bool Before(Vector3 a, Vector3 b) =>
        a.X != b.X ? a.X < b.X : a.Y != b.Y ? a.Y < b.Y : a.Z < b.Z;

    /// <summary>Roughly how big the glyph is, for screen-space picking.</summary>
    public static float DeviceGlyphRadius(AirSource s) =>
        MathF.Max(0.25f, s.Radius > 0 ? s.Radius : MathF.Max(s.Width, s.Height) * 0.5f);

    /// <summary>One device as line art: fans get a disc plus an axis arrow, AC units a box with a tilted
    /// supply arrow and a short return arrow the other way, extracts an arrow into the wall.</summary>
    static void DeviceGlyph(List<Vertex> into, AirSource s)
    {
        var n = s.Direction.LengthSquared() > 1e-6f ? Vector3.Normalize(s.Direction) : -Vector3.UnitZ;
        var (t, b) = Basis(n);
        float r = DeviceGlyphRadius(s);
        switch (s.Device)
        {
            case DeviceKind.CeilingFan:
            case DeviceKind.PedestalFan:
                Disc(into, s.Center, t, b, r);
                // Three blades, so a stopped fan still reads as a fan.
                for (int k = 0; k < 3; k++)
                {
                    float a = k * MathF.Tau / 3f;
                    Edge(into, s.Center, s.Center + (t * MathF.Cos(a) + b * MathF.Sin(a)) * r, n);
                }
                Arrow(into, s.Center, n, r * 1.3f, t);
                break;
            case DeviceKind.SplitAc:
            case DeviceKind.CassetteAc:
                BoxOutline(into, s.Center, t, b, n, s.Width * 0.5f, s.Height * 0.5f, 0.12f);
                Arrow(into, s.Center, Tilt(n, t, b, s.LouvreAngle), MathF.Max(0.7f, r * 1.6f), t);
                // The linked return pulls the same flow back in, drawn short and the other way up.
                Arrow(into, s.Center + n * 0.12f + Vector3.UnitZ * 0.18f, -n, 0.3f, t);
                break;
            case DeviceKind.Diffuser:
                Disc(into, s.Center, t, b, r);
                Arrow(into, s.Center, n, MathF.Max(0.5f, r * 1.8f), t);
                break;
            default:    // ExhaustFan, Hood, Patch
                BoxOutline(into, s.Center, t, b, n, MathF.Max(s.Width, 0.2f) * 0.5f,
                    MathF.Max(s.Height, 0.2f) * 0.5f, 0.06f);
                Arrow(into, s.Center - n * 0.35f, n, 0.5f, t);
                break;
        }
    }

    /// <summary>Tilt a direction down by <paramref name="degrees"/> about the in-plane axis, the louvre.</summary>
    static Vector3 Tilt(Vector3 n, Vector3 t, Vector3 b, float degrees)
    {
        float a = Math.Clamp(degrees, 0f, 75f) * MathF.PI / 180f;
        var down = MathF.Abs(n.Z) > 0.9f ? t : -Vector3.UnitZ;
        return Vector3.Normalize(n * MathF.Cos(a) + down * MathF.Sin(a));
    }

    static (Vector3 T, Vector3 B) Basis(Vector3 n)
    {
        var up = MathF.Abs(n.Z) > 0.9f ? Vector3.UnitY : Vector3.UnitZ;
        var t = Vector3.Normalize(Vector3.Cross(up, n));
        return (t, Vector3.Cross(n, t));
    }

    static void Disc(List<Vertex> into, Vector3 c, Vector3 t, Vector3 b, float r)
    {
        const int Segments = 28;
        var prev = c + t * r;
        for (int i = 1; i <= Segments; i++)
        {
            float a = i * MathF.Tau / Segments;
            var p = c + (t * MathF.Cos(a) + b * MathF.Sin(a)) * r;
            Edge(into, prev, p, Vector3.UnitZ);
            prev = p;
        }
    }

    static void BoxOutline(List<Vertex> into, Vector3 c, Vector3 t, Vector3 b, Vector3 n, float hw, float hh, float depth)
    {
        for (int face = 0; face < 2; face++)
        {
            var o = c + n * (face == 0 ? 0f : -depth);
            var p0 = o + t * hw + b * hh;
            var p1 = o - t * hw + b * hh;
            var p2 = o - t * hw - b * hh;
            var p3 = o + t * hw - b * hh;
            Edge(into, p0, p1, n); Edge(into, p1, p2, n); Edge(into, p2, p3, n); Edge(into, p3, p0, n);
            if (face == 0)
                foreach (var corner in new[] { p0, p1, p2, p3 }) Edge(into, corner, corner - n * depth, n);
        }
    }

    static void Arrow(List<Vertex> into, Vector3 from, Vector3 dir, float length, Vector3 side)
    {
        var tip = from + dir * length;
        Edge(into, from, tip, dir);
        float head = length * 0.28f;
        Edge(into, tip, tip - dir * head + side * head * 0.5f, dir);
        Edge(into, tip, tip - dir * head - side * head * 0.5f, dir);
    }

    /// <summary>A room's volume: the outline loop (the largest) inset 3 cm so it never z-fights the wall faces it
    /// sits against, floor and ceiling caps by ear clipping, one quad per edge for the sides. Holes are drawn as
    /// sides only; their caps are not cut out.
    /// ponytail: floor polygon extruded, not the solver's voxel region (the UI has no voxel grid of its own); a
    /// mezzanine or a sloped ceiling reads as a flat-topped block.</summary>
    static void RoomBlock(List<Vertex> tris, List<Vertex> edges, Room room)
    {
        if (room.Loops.Count == 0) return;
        float z0 = room.ZMin + 0.03f, z1 = MathF.Max(room.ZMax - 0.03f, z0 + 0.1f);
        var outline = room.Loops.OrderByDescending(l => MathF.Abs(SignedArea(l))).First();
        foreach (var raw in room.Loops)
        {
            var loop = Inset(raw, raw == outline ? 0.03f : -0.03f);
            if (loop.Length < 3) continue;
            for (int i = 0; i < loop.Length; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Length];
                var d = b - a;
                var n = new Vector3(d.Y, -d.X, 0);
                n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitX;
                Vector3 a0 = new(a.X, a.Y, z0), b0 = new(b.X, b.Y, z0), a1 = new(a.X, a.Y, z1), b1 = new(b.X, b.Y, z1);
                tris.Add(new Vertex(a0, n)); tris.Add(new Vertex(b0, n)); tris.Add(new Vertex(b1, n));
                tris.Add(new Vertex(a0, n)); tris.Add(new Vertex(b1, n)); tris.Add(new Vertex(a1, n));
                Edge(edges, a0, b0, Vector3.UnitZ);
                Edge(edges, a1, b1, Vector3.UnitZ);
                Edge(edges, a0, a1, n);
            }
            if (raw != outline) continue;
            foreach (var (p, q, r) in EarClip(loop))
            {
                tris.Add(new Vertex(new Vector3(p, z0), -Vector3.UnitZ));
                tris.Add(new Vertex(new Vector3(q, z0), -Vector3.UnitZ));
                tris.Add(new Vertex(new Vector3(r, z0), -Vector3.UnitZ));
                tris.Add(new Vertex(new Vector3(p, z1), Vector3.UnitZ));
                tris.Add(new Vertex(new Vector3(q, z1), Vector3.UnitZ));
                tris.Add(new Vertex(new Vector3(r, z1), Vector3.UnitZ));
            }
        }
    }

    static float SignedArea(Vector2[] loop)
    {
        float a = 0;
        for (int i = 0; i < loop.Length; i++)
        {
            var p = loop[i];
            var q = loop[(i + 1) % loop.Length];
            a += p.X * q.Y - q.X * p.Y;
        }
        return a * 0.5f;
    }

    /// <summary>Moves every vertex <paramref name="d"/> metres inward (negative: outward) along its mitred
    /// bisector, capped at 3x so a sharp corner does not spike. Drops repeated points first.</summary>
    static Vector2[] Inset(Vector2[] loop, float d)
    {
        var pts = new List<Vector2>();
        foreach (var p in loop)
            if (pts.Count == 0 || Vector2.DistanceSquared(pts[^1], p) > 1e-8f) pts.Add(p);
        if (pts.Count > 1 && Vector2.DistanceSquared(pts[0], pts[^1]) < 1e-8f) pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 3) return [];
        float sign = SignedArea(pts.ToArray()) >= 0 ? 1 : -1;        // CCW: inward is the left normal
        var result = new Vector2[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            var prev = pts[(i + pts.Count - 1) % pts.Count];
            var cur = pts[i];
            var next = pts[(i + 1) % pts.Count];
            var e1 = Vector2.Normalize(cur - prev);
            var e2 = Vector2.Normalize(next - cur);
            var n1 = new Vector2(-e1.Y, e1.X) * sign;
            var n2 = new Vector2(-e2.Y, e2.X) * sign;
            var m = n1 + n2;
            if (m.LengthSquared() < 1e-8f) { result[i] = cur + n1 * d; continue; }
            m = Vector2.Normalize(m);
            float len = d / MathF.Max(Vector2.Dot(m, n1), 1f / 3f);
            result[i] = cur + m * len;
        }
        return result;
    }

    /// <summary>Ear clipping of a simple polygon (either winding). O(n^2), fine for room outlines of tens of
    /// vertices; a self-intersecting loop stops early and leaves the rest uncapped rather than hanging.</summary>
    static List<(Vector2, Vector2, Vector2)> EarClip(Vector2[] loop)
    {
        var result = new List<(Vector2, Vector2, Vector2)>();
        var idx = Enumerable.Range(0, loop.Length).ToList();
        float sign = SignedArea(loop) >= 0 ? 1 : -1;
        static float Cross(Vector2 a, Vector2 b, Vector2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        int guard = loop.Length * loop.Length + 8;
        while (idx.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            for (int i = 0; i < idx.Count; i++)
            {
                var a = loop[idx[(i + idx.Count - 1) % idx.Count]];
                var b = loop[idx[i]];
                var c = loop[idx[(i + 1) % idx.Count]];
                if (Cross(a, b, c) * sign <= 1e-9f) continue;          // reflex or collinear: not an ear
                bool empty = true;
                foreach (int j in idx)
                {
                    var p = loop[j];
                    if (p == a || p == b || p == c) continue;
                    if (Cross(a, b, p) * sign >= 0 && Cross(b, c, p) * sign >= 0 && Cross(c, a, p) * sign >= 0) { empty = false; break; }
                }
                if (!empty) continue;
                result.Add((a, b, c));
                idx.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped)
            {
                // Only collinear points left, or a bad loop: drop one collinear vertex and carry on.
                int drop = -1;
                for (int i = 0; i < idx.Count && drop < 0; i++)
                    if (MathF.Abs(Cross(loop[idx[(i + idx.Count - 1) % idx.Count]], loop[idx[i]], loop[idx[(i + 1) % idx.Count]])) < 1e-6f)
                        drop = i;
                if (drop < 0) break;
                idx.RemoveAt(drop);
            }
        }
        if (idx.Count == 3) result.Add((loop[idx[0]], loop[idx[1]], loop[idx[2]]));
        return result;
    }

    static void Edge(List<Vertex> list, Vector3 a, Vector3 b, Vector3 n)
    {
        list.Add(new Vertex(a, n));
        list.Add(new Vertex(b, n));
    }

    static DrawRange Append(List<Vertex> all, List<Vertex> part)
    {
        var range = new DrawRange(all.Count, part.Count);
        all.AddRange(part);
        return range;
    }
}

using System.Numerics;
using WindFlow.App.Import;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>Add, delete and merge rooms by hand. Pure model edits (like <see cref="OpeningEdit"/>), so --selfcheck drives the same code the Rooms section does.
/// A Room already holds a list of loops (even-odd), so a merged room is simply one room with all the source outlines; nothing in the saved JSON changes.
/// A deleted room's air stays in the model as unassigned indoor air: it is in no room, so no row reports it, and its openings stay.</summary>
public static class RoomEdit
{
    /// <summary>Id prefix of a hand-added room.</summary>
    public const string UserPrefix = "user-room-";

    /// <summary>What <see cref="Delete"/> removed, so <see cref="Restore"/> can put it back at its index.</summary>
    public sealed record Deleted(Room Room, int Index, List<(Opening Opening, OpeningSide A, OpeningSide B)> Sides);

    /// <summary>What <see cref="Merge"/> did: the new room and the originals with their indexes, so <see cref="Unmerge"/> can put them back.</summary>
    public sealed record Merged(Room Result, List<(Room Room, int Index)> Sources);

    public static Deleted Delete(BuildingModel model, Room room)
    {
        int index = model.Rooms.IndexOf(room);
        model.Rooms.Remove(room);
        // An opening that faced the deleted room now faces indoor air in no room (IMP-C's Indoor side), not the outdoors the loop test would read.
        var sides = new List<(Opening, OpeningSide, OpeningSide)>();
        foreach (var o in model.Openings)
        {
            var n = new Vector3(o.Normal.X, o.Normal.Y, 0);
            if (n.LengthSquared() < 1e-8f) continue;
            n = Vector3.Normalize(n);
            float reach = o.Depth * 0.5f + 0.3f;
            bool aIsPlus = MathF.Abs(n.X) >= MathF.Abs(n.Y) ? n.X < 0 : n.Y < 0;      // side A is the low-coordinate side along the normal's axis
            Vector3 pa = o.Center + n * (aIsPlus ? reach : -reach), pb = o.Center - n * (aIsPlus ? reach : -reach);
            bool inA = In(room, pa), inB = In(room, pb);
            if (!inA && !inB) continue;
            sides.Add((o, o.SideA, o.SideB));
            if (o.SideA == OpeningSide.Unknown || o.SideB == OpeningSide.Unknown)
            {
                o.SideA = inA ? OpeningSide.Indoor : Side(model, pa);
                o.SideB = inB ? OpeningSide.Indoor : Side(model, pb);
            }
            else
            {
                if (inA && o.SideA == OpeningSide.Room) o.SideA = OpeningSide.Indoor;
                if (inB && o.SideB == OpeningSide.Room) o.SideB = OpeningSide.Indoor;
            }
        }
        return new Deleted(room, index, sides);
    }

    public static void Restore(BuildingModel model, Deleted d)
    {
        model.Rooms.Insert(Math.Clamp(d.Index, 0, model.Rooms.Count), d.Room);
        foreach (var (o, a, b) in d.Sides) { o.SideA = a; o.SideB = b; }
    }

    static bool In(Room r, Vector3 q) => q.Z >= r.ZMin && q.Z <= r.ZMax && Devices.InLoops(r, q.X, q.Y);
    static OpeningSide Side(BuildingModel m, Vector3 q) => m.Rooms.Any(r => In(r, q)) ? OpeningSide.Room : OpeningSide.Outdoors;

    /// <summary>Null when the rooms can merge; else why not (the Merge button's tooltip). Needs 2 or more rooms on one storey.</summary>
    public static string? CannotMerge(IReadOnlyList<Room> rooms)
    {
        if (rooms.Count < 2) return "Select two or more rooms (Ctrl+click) to merge them.";
        var first = rooms[0];
        if (rooms.Any(r => MathF.Abs(r.ZMin - first.ZMin) > 0.05f))      // same floor height = same storey (Level strings differ: a hand-added room carries the storey Id, a Revit one its Name)
            return "These rooms are on different storeys: only rooms on the same storey can merge.";
        return null;
    }

    /// <summary>One room named and numbered like <paramref name="rooms"/>[0] (Id, Name, Level), with every source outline as a loop, the summed volume and the summed occupants.
    /// It takes the first room's place in the list; the others leave it. Null (with the reason) when <see cref="CannotMerge"/> says so.</summary>
    public static Merged? Merge(BuildingModel model, IReadOnlyList<Room> rooms, out string why)
    {
        why = CannotMerge(rooms) ?? "";
        if (why.Length > 0) return null;
        var first = rooms[0];
        bool derived = rooms.Any(Derived);
        var result = new Room
        {
            Id = first.Id, Name = first.Name, Level = first.Level,
            ZMin = rooms.Min(r => r.ZMin), ZMax = rooms.Max(r => r.ZMax),
            Occupants = rooms.Sum(r => r.Occupants),
            VoxelDerived = derived,
            DetectedVolume = derived ? rooms.Sum(r => Derived(r) ? r.DetectedVolume : RoomNames.FloorArea(r) * (r.ZMax - r.ZMin)) : 0f,
        };
        foreach (var r in rooms) foreach (var loop in r.Loops) result.Loops.Add((Vector2[])loop.Clone());
        var sources = rooms.Select(r => (Room: r, Index: model.Rooms.IndexOf(r))).ToList();
        foreach (var r in rooms.Skip(1)) model.Rooms.Remove(r);
        model.Rooms[model.Rooms.IndexOf(first)] = result;
        return new Merged(result, sources);
    }

    public static void Unmerge(BuildingModel model, Merged m)
    {
        model.Rooms.Remove(m.Result);
        foreach (var (room, index) in m.Sources.OrderBy(s => s.Index)) model.Rooms.Insert(Math.Clamp(index, 0, model.Rooms.Count), room);
    }

    static bool Derived(Room r) => r.VoxelDerived || r.Id.StartsWith("det-", StringComparison.Ordinal);

    /// <summary>The room a click makes. The ray meets each storey's detection plane (<see cref="RoomDetect.PlaneHeight"/> above its floor), nearest first; the first plane whose point
    /// is inside an existing room refuses ("already in …"), and the first inside a region <paramref name="detected"/> found (RoomDetect.Detect on this model, the importer's own detector)
    /// becomes the room: its outline, volume and storey, named "Room N". Null with the reason when the click is in open air or no enclosed space.</summary>
    public static Room? Create(BuildingModel model, DetectedRooms detected, Vector3 eye, Vector3 dir, out string why)
    {
        why = "that is not an enclosed space: click inside a space that has walls and a roof";
        if (MathF.Abs(dir.Z) < 1e-6f) return null;
        var storeys = model.Storeys.Count > 0 ? model.Storeys : [new Storey { Id = "L1", Name = "Level 1", Elevation = model.GroundZ, Height = 3f }];
        foreach (var (storey, p) in storeys.Select(s => (Storey: s, T: (s.Elevation + RoomDetect.PlaneHeight - eye.Z) / dir.Z)).Where(x => x.T > 0).OrderBy(x => x.T)
                                          .Select(x => (x.Storey, Point: eye + dir * x.T)))
        {
            float z = storey.Elevation + RoomDetect.PlaneHeight;
            var existing = model.Rooms.FirstOrDefault(r => z >= r.ZMin && z <= r.ZMax && Devices.InLoops(r, p.X, p.Y));
            if (existing != null) { why = $"that is already inside {existing.Name}: nothing added"; return null; }
            var found = detected.Rooms.FirstOrDefault(r => r.Level == storey.Id && Devices.InLoops(r, p.X, p.Y));
            if (found == null) continue;
            why = "";
            return CreateFrom(model, found, storey.Id);
        }
        return null;
    }

    /// <summary>The hand-added room for one detected region: its outline, volume and storey, named "Room N", appended to the model.</summary>
    public static Room CreateFrom(BuildingModel model, Room found, string level)
    {
        var ids = new HashSet<string>(model.Rooms.Select(r => r.Id));
        var names = new HashSet<string>(model.Rooms.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        int k = 1, n = model.Rooms.Count + 1;
        while (ids.Contains(UserPrefix + k)) k++;
        while (names.Contains($"Room {n}")) n++;
        var room = new Room
        {
            Id = UserPrefix + k, Name = $"Room {n}", Level = level, ZMin = found.ZMin, ZMax = found.ZMax,
            VoxelDerived = true, DetectedVolume = found.DetectedVolume,
        };
        foreach (var loop in found.Loops) room.Loops.Add((Vector2[])loop.Clone());
        model.Rooms.Add(room);
        return room;
    }

    static List<Storey> StoreysOf(BuildingModel model) =>
        model.Storeys.Count > 0 ? model.Storeys : [new Storey { Id = "L1", Name = "Level 1", Elevation = model.GroundZ, Height = 3f }];

    /// <summary>The detected enclosed spaces that are not a room yet: a detected region is a candidate unless more than half of it (sampled on a 0.3 m grid at the detection plane) lies in an existing room.</summary>
    public static List<Room> Candidates(BuildingModel model, DetectedRooms detected)
    {
        var list = new List<Room>();
        foreach (var d in detected.Rooms)
        {
            if (d.Loops.Count == 0) continue;
            float z = d.ZMin + RoomDetect.PlaneHeight;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var loop in d.Loops) foreach (var p in loop) { x0 = MathF.Min(x0, p.X); x1 = MathF.Max(x1, p.X); y0 = MathF.Min(y0, p.Y); y1 = MathF.Max(y1, p.Y); }
            float step = MathF.Max(0.3f, MathF.Sqrt((x1 - x0) * (y1 - y0) / 4000f));
            int inside = 0, covered = 0;
            for (float x = x0 + step * 0.5f; x < x1; x += step)
                for (float y = y0 + step * 0.5f; y < y1; y += step)
                {
                    if (!Devices.InLoops(d, x, y)) continue;
                    inside++;
                    if (model.Rooms.Any(r => z >= r.ZMin && z <= r.ZMax && Devices.InLoops(r, x, y))) covered++;
                }
            if (inside > 0 && covered * 2 <= inside) list.Add(d);
        }
        return list;
    }

    /// <summary>The candidate a ray means. The ray is tested against each storey at floor + 0.2 m, mid-height and ceiling - 0.2 m, so a click on a candidate's floor, walls or ceiling from
    /// inside all land; the nearest point along the ray inside a candidate of its storey wins. Null with why (the status line) when none does.</summary>
    public static Room? Resolve(BuildingModel model, IReadOnlyList<Room> candidates, Vector3 eye, Vector3 dir, out string why)
    {
        Room? best = null;
        float bestT = float.MaxValue;
        bool inRoom = false, inBuilding = false;
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var r in model.Rooms.Concat(candidates)) foreach (var loop in r.Loops) foreach (var p in loop) { x0 = MathF.Min(x0, p.X); x1 = MathF.Max(x1, p.X); y0 = MathF.Min(y0, p.Y); y1 = MathF.Max(y1, p.Y); }
        if (MathF.Abs(dir.Z) > 1e-6f)
            foreach (var s in StoreysOf(model))
            {
                float h = s.Height > 0 ? s.Height : 3f;
                foreach (float z in new[] { s.Elevation + 0.2f, s.Elevation + h * 0.5f, s.Elevation + h - 0.2f })
                {
                    float t = (z - eye.Z) / dir.Z;
                    if (t <= 0) continue;
                    var p = eye + dir * t;
                    foreach (var c in candidates)
                        if (t < bestT && c.Level == s.Id && Devices.InLoops(c, p.X, p.Y)) { best = c; bestT = t; }
                    if (model.Rooms.Any(r => z >= r.ZMin && z <= r.ZMax && Devices.InLoops(r, p.X, p.Y))) inRoom = true;
                    if (p.X >= x0 - 1f && p.X <= x1 + 1f && p.Y >= y0 - 1f && p.Y <= y1 + 1f) inBuilding = true;
                }
            }
        why = best != null ? "" : inRoom ? "That spot is already a room" : inBuilding ? "No enclosed space there" : "That is outside the building";
        return best;
    }
}

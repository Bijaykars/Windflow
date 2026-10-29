using System.Numerics;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>Room isolate, view only: the box the renderer clips walls, slices and trails to while one room is
/// isolated. The room's footprint AABB plus a margin wide enough to keep its own walls, slabs and opening quads and
/// a thin band of air beyond every opening, so inflow and outflow through windows and doors stay in the picture.
/// A pure function of the model: nothing here touches the solver or the published field.
/// ponytail: an AABB, so an L-shaped room shows a little of its neighbour. Upgrade path: a per-texel room-id
/// texture (FieldVolume.IndoorRoom) sampled in PSSlice and VSTrail.</summary>
public static class RoomIsolation
{
    /// <summary>Loop inset (~0.03 m) + a 0.25 m exterior wall + ~0.1 m of air beyond an opening.</summary>
    public const float MarginXY = 0.40f;
    /// <summary>A 0.2 m slab and a little.</summary>
    public const float MarginZ = 0.30f;

    public static (Vector3 Min, Vector3 Max)? Box(Room? room)
    {
        if (room == null || room.Loops.Count == 0) return null;
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var loop in room.Loops)
            foreach (var p in loop)
            {
                x0 = MathF.Min(x0, p.X); x1 = MathF.Max(x1, p.X);
                y0 = MathF.Min(y0, p.Y); y1 = MathF.Max(y1, p.Y);
            }
        if (x1 <= x0 || y1 <= y0) return null;
        return (new Vector3(x0 - MarginXY, y0 - MarginXY, room.ZMin - MarginZ),
                new Vector3(x1 + MarginXY, y1 + MarginXY, room.ZMax + MarginZ));
    }

    /// <summary>--isolate &lt;room&gt;: by id, then by name, case-insensitive, a prefix is enough. -1 when nothing matches.</summary>
    public static int Find(BuildingModel model, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return -1;
        text = text.Trim();
        int i = model.Rooms.FindIndex(r => r.Id.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (i < 0) i = model.Rooms.FindIndex(r => r.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (i < 0) i = model.Rooms.FindIndex(r => r.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase));
        return i;
    }
}

/// <summary>Plain-language titles for the slice panel: what is cut, where, and what to look for. Speed ranges are
/// described neutrally (a point speed on a cut is indicative, not a comfort verdict).</summary>
public static class SliceCaption
{
    public const int Speed = 0, Pressure = 1, Temperature = 2, Difference = 3;

    static string FieldName(int field) => field switch
    {
        Pressure => "Pressure",
        Temperature => "Temperature",
        Difference => "Air speed, B − A",
        _ => "Air speed",
    };

    /// <summary>"1.1 m (seated head height)", with the storey when the model has more than one.</summary>
    public static string Height(float z, BuildingModel model)
    {
        Storey? storey = null;
        foreach (var s in model.Storeys)
            if (s.Elevation <= z + 0.02f && (storey == null || s.Elevation > storey.Elevation)) storey = s;
        float h = z - (storey?.Elevation ?? model.GroundZ);
        string where = h < 0.35f ? "ankle height" : h < 0.85f ? "seated body height" : h < 1.35f ? "seated head height"
            : h < 1.9f ? "standing head height" : "above head height";
        string level = model.Storeys.Count > 1 && storey != null && storey.Name.Length > 0 ? $"{storey.Name}, " : "";
        return $"{z:0.0} m ({level}{where})";
    }

    /// <summary>Rooms whose footprint the vertical cut passes through, in panel order; empty when none.</summary>
    public static List<string> SectionRooms(BuildingModel model, int axis, float cut, Func<string, string> name, Func<string, int> rank)
    {
        var hit = new List<(int Rank, string Name)>();
        foreach (var room in model.Rooms)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var loop in room.Loops)
                foreach (var p in loop)
                {
                    float v = axis == 1 ? p.X : p.Y;
                    lo = MathF.Min(lo, v); hi = MathF.Max(hi, v);
                }
            if (lo <= cut && cut <= hi) hit.Add((rank(room.Id), name(room.Id)));
        }
        return hit.OrderBy(h => h.Rank).Select(h => h.Name).ToList();
    }

    public static string Title(int field, bool plan, bool section, float z, int axis, float cut, BuildingModel model,
        Func<string, string> name, Func<string, int> rank)
    {
        if (!plan && !section) return "No cut shown";
        var parts = new List<string>();
        if (plan) parts.Add($"plan at {Height(z, model)}");
        if (section)
        {
            var rooms = SectionRooms(model, axis, cut, name, rank);
            string through = rooms.Count == 0 ? $"at {(axis == 1 ? "x" : "y")} = {cut:0.0} m"
                : "through " + string.Join(", ", rooms.Take(3)) + (rooms.Count > 3 ? $" +{rooms.Count - 3}" : "");
            parts.Add($"section {through}");
        }
        return $"{FieldName(field)} · {string.Join(" + ", parts)}";
    }

    public static string Hint(int field, bool plan, bool section)
    {
        if (!plan && !section)
            return "Plan cuts the air horizontally at the height below; Section cuts it vertically along the line below. Colour is the air on that cut.";
        return field switch
        {
            Pressure => "Air moves from + to −. A big step across an opening is what drives the flow through it.",
            Temperature => "± around the mean of the room air. Warm air collects high and cool air low: look for layering.",
            Difference => "B minus A: + where B is faster, − where A is.",
            _ => "Brighter is faster, from 0 up to the legend's top. Look for the path from an open window to the way out, and the dark patches where air barely moves. Point speeds are indicative.",
        };
    }
}

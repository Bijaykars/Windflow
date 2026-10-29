using System.Numerics;
using System.Text.RegularExpressions;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>PLAN_V2 4.1: a name per room row that a user can tell apart from every other row.
/// <list type="bullet">
/// <item>A Revit/IFC name that is unique in the model is used as is.</item>
/// <item>Two or more rooms sharing a real name get "(north, 24 m²)" added: the 8-point compass direction of
/// the room centroid from the building footprint centre (+Y is north, the wind convention of
/// RevitExtractor.WindVector), "centre" when the room sits near the middle, and the floor area.</item>
/// <item>Rooms with no name of their own (the detector's placeholder "Room N", or no name at all) become plain
/// "Room N", numbered in display order, with no suffix; openings are likewise plain "Window N" / "Door N".</item>
/// </list>
/// Display order is stable: by storey (model order, i.e. elevation), then by position on plan, north to south
/// and west to east on a 0.5 m grid so a sub-cell shift in a live-link update does not reshuffle rows.</summary>
public static class RoomNames
{
    /// <summary>Offsets inside this share of the footprint half-size read as "centre".</summary>
    public const float CentreFraction = 0.25f;

    static readonly Regex Placeholder = new(@"^Room \d+$", RegexOptions.CultureInvariant);
    static readonly string[] Compass = ["north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west"];

    /// <summary>Rooms in display order, each with its unique display name.</summary>
    public static List<(Room Room, string Name)> Compute(BuildingModel model)
    {
        var (fMin, fMax) = Footprint(model);
        var centre = (fMin + fMax) * 0.5f;
        var half = Vector2.Max((fMax - fMin) * 0.5f, new Vector2(0.5f));
        var storeyOrder = new Dictionary<string, int>();
        for (int i = 0; i < model.Storeys.Count; i++) storeyOrder.TryAdd(model.Storeys[i].Id, i);
        for (int i = 0; i < model.Storeys.Count; i++) storeyOrder.TryAdd(model.Storeys[i].Name, i);   // RX-01: a Revit room's Level is the storey name

        var info = model.Rooms.Select((r, i) => (Room: r, Index: i, Centroid: Centroid(r), Area: FloorArea(r))).ToList();
        var ordered = info
            .OrderBy(x => storeyOrder.TryGetValue(x.Room.Level, out int s) ? s : int.MaxValue)
            .ThenBy(x => x.Room.ZMin)
            .ThenByDescending(x => MathF.Round(x.Centroid.Y * 2))     // north first
            .ThenBy(x => MathF.Round(x.Centroid.X * 2))               // then west to east
            .ThenBy(x => x.Room.Id, StringComparer.Ordinal)
            .ToList();

        string Where(Vector2 c, float area)
        {
            var d = (c - centre) / half;
            string dir = d.Length() < CentreFraction ? "centre" : Compass[Sector(d)];
            return $"{dir}, {area:0} m²";
        }

        var named = ordered.Where(x => !IsUnnamed(x.Room)).GroupBy(x => x.Room.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                           .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var result = new List<(Room, string)>(ordered.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (var x in ordered)
        {
            string name;
            if (IsUnnamed(x.Room)) name = $"Room {++n}";
            else
            {
                string baseName = x.Room.Name.Trim();
                name = named[baseName] > 1 ? $"{baseName} ({Where(x.Centroid, x.Area)})" : baseName;
            }
            // Two duplicates in the same direction with the same area (a stacked plan): number the later ones.
            string unique = name;
            for (int k = 2; !used.Add(unique); k++) unique = $"{name} #{k}";
            result.Add((x.Room, unique));
        }
        return result;
    }

    /// <summary>No name, or the mesh detector's placeholder that nobody has renamed.</summary>
    public static bool IsUnnamed(Room r) =>
        string.IsNullOrWhiteSpace(r.Name) ||
        (r.Id.StartsWith("det-", StringComparison.Ordinal) && Placeholder.IsMatch(r.Name.Trim()));

    /// <summary>0 = north, clockwise in 45° sectors, +Y north and +X east.</summary>
    static int Sector(Vector2 d)
    {
        float deg = MathF.Atan2(d.X, d.Y) * 180f / MathF.PI;      // bearing: 0 north, 90 east
        return (int)MathF.Floor(((deg + 360f + 22.5f) % 360f) / 45f) % 8;
    }

    /// <summary>Plan extent of the building, from the room loops: obstacle bounds would include the ground slab and
    /// site context, which pull the centre off the house.</summary>
    static (Vector2 Min, Vector2 Max) Footprint(BuildingModel model)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var r in model.Rooms)
            foreach (var loop in r.Loops)
                foreach (var p in loop) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
        if (min.X > max.X) return (Vector2.Zero, Vector2.Zero);
        return (min, max);
    }

    /// <summary>Signed shoelace area of one loop (positive counter-clockwise).</summary>
    static float Signed(Vector2[] loop)
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

    /// <summary>Floor area, even-odd like the analysis: a loop inside an odd number of others is a hole, so a merged room's separate outlines add up.</summary>
    public static float FloorArea(Room r)
    {
        float area = 0;
        for (int i = 0; i < r.Loops.Count; i++)
        {
            var p = r.Loops[i].Length > 0 ? r.Loops[i][0] : default;
            int depth = 0;
            for (int j = 0; j < r.Loops.Count; j++)
                if (j != i && Devices.InLoops(new Room { Loops = { r.Loops[j] } }, p.X, p.Y)) depth++;
            area += (depth % 2 == 0 ? 1 : -1) * MathF.Abs(Signed(r.Loops[i]));
        }
        return MathF.Max(0, area);
    }

    /// <summary>Area centroid of the outline loop (the largest), vertex mean when it is degenerate.</summary>
    public static Vector2 Centroid(Room r)
    {
        if (r.Loops.Count == 0) return Vector2.Zero;
        var loop = r.Loops.OrderByDescending(l => MathF.Abs(Signed(l))).First();
        float a = Signed(loop);
        if (MathF.Abs(a) < 1e-6f)
        {
            var m = Vector2.Zero;
            foreach (var p in loop) m += p;
            return loop.Length > 0 ? m / loop.Length : m;
        }
        float cx = 0, cy = 0;
        for (int i = 0; i < loop.Length; i++)
        {
            var p = loop[i];
            var q = loop[(i + 1) % loop.Length];
            float c = p.X * q.Y - q.X * p.Y;
            cx += (p.X + q.X) * c;
            cy += (p.Y + q.Y) * c;
        }
        return new Vector2(cx, cy) / (6 * a);
    }
}

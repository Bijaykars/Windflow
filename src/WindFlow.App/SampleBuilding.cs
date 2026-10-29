using System.Numerics;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>A small two-storey, seven-room house built in code, so the app is testable without Revit.
/// Conventions mirror src/WindFlow.Sandbox/Program.cs exactly (metres, Z up, GroundZ at the ground surface,
/// walls as solid boxes with no holes cut, openings as oriented boxes the voxelizer carves): the result can be
/// handed to Simulation.Run unchanged.</summary>
public static class SampleBuilding
{
    const float ExtT = 0.25f;   // exterior wall thickness
    const float IntT = 0.12f;   // interior wall thickness
    const float SlabT = 0.2f;
    const float L1 = 0f, L2 = 3f, Roof = 6f;   // storey elevations
    const float W = 12f, D = 8f;               // plan size, X by Y

    public static BuildingModel Build()
    {
        var m = new BuildingModel
        {
            Name = "Sample house",
            GroundZ = 0,
            Storeys =
            {
                new Storey { Id = "L1", Name = "Level 1", Elevation = L1, Height = 3, Analysed = true },
                new Storey { Id = "L2", Name = "Level 2", Elevation = L2, Height = 3, Analysed = true },
            },
        };

        // ---- shell: four exterior walls from the ground to the roof, ground slab, roof slab ----
        m.Obstacles.Add(Wall(0, 0, W, 0, ExtT, 0, Roof));   // south, y = 0
        m.Obstacles.Add(Wall(W, 0, W, D, ExtT, 0, Roof));   // east, x = 12
        m.Obstacles.Add(Wall(0, D, W, D, ExtT, 0, Roof));   // north, y = 8
        m.Obstacles.Add(Wall(0, 0, 0, D, ExtT, 0, Roof));   // west, x = 0
        m.Obstacles.Add(Box(new Vector3(-1, -1, -0.2f), new Vector3(W + 1, D + 1, 0)));          // ground slab
        m.Obstacles.Add(Box(new Vector3(0, 0, Roof), new Vector3(W, D, Roof + SlabT)));          // roof slab

        // ---- L1/L2 slab with a 1.8 x 2.5 m stair void at x 7.1..8.9, y 5..7.5 (over the hall, inside the landing; P12-03), as four strips ----
        float sz0 = L2, sz1 = L2 + SlabT;
        m.Obstacles.Add(Box(new Vector3(0, 0, sz0), new Vector3(W, 5f, sz1)));
        m.Obstacles.Add(Box(new Vector3(0, 7.5f, sz0), new Vector3(W, D, sz1)));
        m.Obstacles.Add(Box(new Vector3(0, 5f, sz0), new Vector3(7.1f, 7.5f, sz1)));
        m.Obstacles.Add(Box(new Vector3(8.9f, 5f, sz0), new Vector3(W, 7.5f, sz1)));

        // ---- L1 partitions: living (west), kitchen (south-east), hall + stair (north-east) ----
        m.Obstacles.Add(Wall(7, 0, 7, D, IntT, L1, L2));
        m.Obstacles.Add(Wall(7, 4.5f, W, 4.5f, IntT, L1, L2));

        // ---- L2 partitions: bedroom 1 (west), bedroom 2 (south-east), landing, bathroom ----
        m.Obstacles.Add(Wall(5.5f, 0, 5.5f, D, IntT, sz1, Roof));
        m.Obstacles.Add(Wall(5.5f, 4.5f, W, 4.5f, IntT, sz1, Roof));
        m.Obstacles.Add(Wall(9, 4.5f, 9, D, IntT, sz1, Roof));

        // ---- façade openings (windows at 1.5 m above each floor) ----
        float z1 = 1.5f, z2 = L2 + SlabT + 1.3f;
        m.Openings.Add(Win("w-liv-s", "Living south window", "L1", new Vector3(3.5f, 0, z1), -Vector3.UnitY, 1.6f, 1.3f, ExtT, true));
        m.Openings.Add(Win("w-liv-n", "Living north window", "L1", new Vector3(3.5f, D, z1), Vector3.UnitY, 1.6f, 1.3f, ExtT, true));
        m.Openings.Add(Win("w-liv-w", "Living west window", "L1", new Vector3(0, 4, z1), -Vector3.UnitX, 1.2f, 1.3f, ExtT, false));
        m.Openings.Add(Win("w-kit-s", "Kitchen window", "L1", new Vector3(9.5f, 0, z1), -Vector3.UnitY, 1.2f, 1.1f, ExtT, true));
        m.Openings.Add(Win("w-hall-e", "Hall window", "L1", new Vector3(W, 6.25f, z1), Vector3.UnitX, 1f, 1.1f, ExtT, true));
        m.Openings.Add(Door("d-front", "Front door", "L1", new Vector3(9.5f, D, 1.05f), Vector3.UnitY, 1f, 2.1f, ExtT, false));

        m.Openings.Add(Win("w-b1-s", "Bedroom 1 south window", "L2", new Vector3(2.5f, 0, z2), -Vector3.UnitY, 1.4f, 1.3f, ExtT, true));
        m.Openings.Add(Win("w-b1-n", "Bedroom 1 north window", "L2", new Vector3(2.5f, D, z2), Vector3.UnitY, 1.4f, 1.3f, ExtT, true));
        m.Openings.Add(Win("w-b1-w", "Bedroom 1 west window", "L2", new Vector3(0, 4, z2), -Vector3.UnitX, 1.2f, 1.3f, ExtT, true));
        m.Openings.Add(Win("w-b2-s", "Bedroom 2 window", "L2", new Vector3(9f, 0, z2), -Vector3.UnitY, 1.4f, 1.3f, ExtT, true));
        m.Openings.Add(Win("w-bath-e", "Bathroom window", "L2", new Vector3(W, 6.25f, z2), Vector3.UnitX, 0.7f, 0.7f, ExtT, false));

        // ---- interior doors ----
        m.Openings.Add(Door("d-liv-kit", "Living to kitchen", "L1", new Vector3(7, 2f, 1.05f), Vector3.UnitX, 0.9f, 2.1f, IntT, true));
        m.Openings.Add(Door("d-kit-hall", "Kitchen to hall", "L1", new Vector3(9f, 4.5f, 1.05f), Vector3.UnitY, 0.9f, 2.1f, IntT, true));
        m.Openings.Add(Door("d-b1", "Bedroom 1 door", "L2", new Vector3(5.5f, 6f, sz1 + 1.05f), Vector3.UnitX, 0.85f, 2.1f, IntT, true));
        m.Openings.Add(Door("d-b2", "Bedroom 2 door", "L2", new Vector3(7f, 4.5f, sz1 + 1.05f), Vector3.UnitY, 0.85f, 2.1f, IntT, true));
        m.Openings.Add(Door("d-bath", "Bathroom door", "L2", new Vector3(9f, 6f, sz1 + 1.05f), Vector3.UnitX, 0.8f, 2.1f, IntT, false));

        // ---- rooms (statistics only; loops are inset off the wall faces like the sandbox scenes) ----
        m.Rooms.Add(Room("living", "Living", "L1", 0.15f, 0.15f, 6.9f, 7.85f, L1, L2, 3));
        m.Rooms.Add(Room("kitchen", "Kitchen", "L1", 7.1f, 0.15f, 11.85f, 4.4f, L1, L2, 1));
        m.Rooms.Add(Room("hall", "Hall", "L1", 7.1f, 4.6f, 11.85f, 7.85f, L1, L2, 0));
        m.Rooms.Add(Room("bed1", "Bedroom 1", "L2", 0.15f, 0.15f, 5.4f, 7.85f, sz1, Roof, 2));
        m.Rooms.Add(Room("bed2", "Bedroom 2", "L2", 5.6f, 0.15f, 11.85f, 4.4f, sz1, Roof, 1));
        m.Rooms.Add(Room("landing", "Landing", "L2", 5.6f, 4.6f, 8.9f, 7.85f, sz1, Roof, 0));
        m.Rooms.Add(Room("bath", "Bathroom", "L2", 9.1f, 4.6f, 11.85f, 7.85f, sz1, Roof, 0));

        // ---- one device, so the palette has something to show on a fresh model ----
        // Off by default: the sample's headline numbers are wind-driven, and a running fan would move them.
        var fan = Devices.Create(Devices.PresetFor(DeviceKind.CeilingFan),
            new Vector3(3.5f, 4f, L2 - Devices.CeilingFanDrop), -Vector3.UnitZ, "L1", 1);
        fan.Id = "dev-ceilingfan-living";
        fan.Name = "Living ceiling fan";
        fan.Enabled = false;
        m.Sources.Add(fan);
        return m;
    }

    // ---- geometry helpers, copied from the sandbox so both build identical solids ----

    internal static Mesh Box(Vector3 min, Vector3 max)
    {
        Vector3[] v =
        [
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z),
        ];
        int[] idx =
        [
            0, 1, 2,  0, 2, 3,   // bottom (z = min)
            4, 6, 5,  4, 7, 6,   // top (z = max)
            0, 4, 5,  0, 5, 1,   // y = min
            1, 5, 6,  1, 6, 2,   // x = max
            2, 6, 7,  2, 7, 3,   // y = max
            3, 7, 4,  3, 4, 0,   // x = min
        ];
        return new Mesh { Id = Guid.NewGuid().ToString("N"), Vertices = v, Indices = idx };
    }

    /// <summary>Axis-aligned wall box centred on the segment; thickness is added perpendicular to it.</summary>
    static Mesh Wall(float x0, float y0, float x1, float y1, float thickness, float zMin, float zMax)
    {
        float minX = MathF.Min(x0, x1), maxX = MathF.Max(x0, x1);
        float minY = MathF.Min(y0, y1), maxY = MathF.Max(y0, y1);
        float half = thickness * 0.5f;
        if (MathF.Abs(x1 - x0) < 1e-6f) { minX -= half; maxX += half; }
        else { minY -= half; maxY += half; }
        return Box(new Vector3(minX, minY, zMin), new Vector3(maxX, maxY, zMax));
    }

    static Opening Win(string id, string name, string level, Vector3 c, Vector3 n, float w, float h, float depth, bool open) =>
        new() { Id = id, Name = name, Level = level, Kind = OpeningKind.Window, IsOpen = open, Center = c, Normal = n, Width = w, Height = h, Depth = depth };

    static Opening Door(string id, string name, string level, Vector3 c, Vector3 n, float w, float h, float depth, bool open) =>
        new() { Id = id, Name = name, Level = level, Kind = OpeningKind.Door, IsOpen = open, Center = c, Normal = n, Width = w, Height = h, Depth = depth };

    static Room Room(string id, string name, string level, float x0, float y0, float x1, float y1, float zMin, float zMax, int occupants) =>
        new()
        {
            Id = id, Name = name, Level = level, ZMin = zMin, ZMax = zMax, Occupants = occupants,
            Loops = { new Vector2[] { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) } },
        };
}

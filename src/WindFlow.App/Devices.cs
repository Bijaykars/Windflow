using System.Numerics;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>Which surface a device mounts on. Placement snaps to that surface of the room under the cursor;
/// sloped and curved surfaces are not supported (the sample and the Revit export are axis-aligned boxes, and
/// a tilted mount would need a full normal from the mesh rather than a room loop).</summary>
public enum MountSurface { Ceiling, Wall, Floor }

/// <summary>One tile in the device palette: what the user picks, and the defaults it lands with. The numbers
/// are hidden behind the inspector's Details disclosure and stay editable there.</summary>
public sealed record DevicePreset(
    DeviceKind Kind, SourceKind Flow, MountSurface Surface, string Label, string Blurb,
    float FlowRate, float Radius, float Swirl, float SupplyTemperature, float Louvre,
    float Width, float Height, float MountHeight = 0f, bool NeedsExteriorOpening = false, float CeilingDrop = 0.08f);

public static class Devices
{
    /// <summary>The palette, in the order the tiles show it: named, ordinary domestic hardware.
    /// Ceiling fan: 1.2 m (48 in) sweep on a low setting, 0.9 m³/s. Split AC: a 1.5 ton (5.3 kW) wall unit,
    /// ~0.26 m³/s (about 550 cfm) on high, the usual indoor-unit rating for that size. Bath exhaust: 25 L/s,
    /// ASHRAE 62.2's 50 cfm intermittent bathroom extract.</summary>
    public static readonly DevicePreset[] Presets =
    [
        new(DeviceKind.CeilingFan, SourceKind.Fan, MountSurface.Ceiling, "Ceiling fan 1.2 m",
            "1.2 m sweep on the ceiling, downdraught with swirl", 0.9f, 0.6f, 0.3f, float.NaN, 0f, 1.2f, 1.2f,
            CeilingDrop: CeilingFanDrop),
        new(DeviceKind.PedestalFan, SourceKind.Fan, MountSurface.Floor, "Pedestal fan",
            "400 mm head at 1.2 m on the floor, blows horizontally (R turns it)", 0.35f, 0.2f, 0.1f, float.NaN, 0f,
            0.4f, 0.4f, 1.2f),
        new(DeviceKind.SplitAc, SourceKind.Fan, MountSurface.Wall, "Split AC 1.5 ton",
            "5.3 kW wall unit at 2.1 m, linked return, 14 °C supply", 0.26f, 0f, 0f, 14f, 30f, 0.9f, 0.12f, 2.1f),
        new(DeviceKind.CassetteAc, SourceKind.Fan, MountSurface.Ceiling, "Cassette AC",
            "Four-way ceiling cassette around a central return", 0.25f, 0f, 0f, 14f, 45f, 0.6f, 0.6f),
        new(DeviceKind.ExhaustFan, SourceKind.Return, MountSurface.Wall, "Exhaust fan (bath)",
            "25 L/s extract to outside; sits in an open exterior window or vent", 0.025f, 0f, 0f, float.NaN, 0f,
            0.25f, 0.25f, 2.1f, NeedsExteriorOpening: true),
        new(DeviceKind.Hood, SourceKind.Return, MountSurface.Wall, "Kitchen hood",
            "Extract hood with a 0.4 m capture depth", 0.12f, 0.4f, 0f, float.NaN, 0f, 0.8f, 0.5f, 1.9f,
            NeedsExteriorOpening: true),
        new(DeviceKind.Diffuser, SourceKind.Supply, MountSurface.Ceiling, "Ceiling diffuser",
            "Ceiling diffuser, 20° rim spread; moves room air; adds no fresh air", 0.12f, 0f, 0f, float.NaN, 20f, 0.3f, 0.3f),
    ];

    /// <summary>What a device IS, without the preset's size: the row subtitle and new device names use this, so a
    /// split AC edited to 0.4 m³/s does not keep calling itself "1.5 ton".</summary>
    public static string KindName(DeviceKind kind) => kind switch
    {
        DeviceKind.CeilingFan => "Ceiling fan",
        DeviceKind.PedestalFan => "Pedestal fan",
        DeviceKind.SplitAc => "Split AC",
        DeviceKind.CassetteAc => "Cassette AC",
        DeviceKind.ExhaustFan => "Exhaust fan",
        DeviceKind.Hood => "Hood",
        DeviceKind.Diffuser => "Diffuser",
        _ => kind.ToString(),
    };

    // ---------------------------------------------------------------- resolution (PLAN_V2 3.3)

    /// <summary>PLAN_V2 3.3: a fan disc under 8 cells across needs a finer grid; an outlet or grille is held to the
    /// same 8 cells an opening needs to be "coarse is enough".</summary>
    public const float MinCellsAcross = 8f;

    /// <summary>The span the grid has to resolve: the disc diameter for a ceiling or pedestal fan, else the short
    /// side of the outlet (a split AC's 0.12 m slot, not its 0.9 m length).</summary>
    public static float ResolvedSpan(AirSource s) => IsDisc(s) ? 2f * s.Radius : MathF.Min(s.Width, s.Height);

    static bool IsDisc(AirSource s) => s.Device is DeviceKind.CeilingFan or DeviceKind.PedestalFan && s.Radius > 0;

    /// <summary>Cells across the device at cell size <paramref name="dx"/> (m); NaN when there is no grid yet.</summary>
    public static float CellsAcross(AirSource s, float dx) => dx > 0 ? ResolvedSpan(s) / dx : float.NaN;

    /// <summary>True when the device is under <see cref="MinCellsAcross"/> cells at this cell size. False with no
    /// grid (dx 0): nothing to judge yet.</summary>
    public static bool NeedsRefine(AirSource s, float dx) => dx > 0 && CellsAcross(s, dx) < MinCellsAcross;

    /// <summary>The indicator text and its explanation, in the words the Devices tab uses.</summary>
    public static (string Label, string Why) Resolution(AirSource s, float dx)
    {
        if (!(dx > 0)) return ("resolution —", "Run the solve to check this device against the grid.");
        float cells = CellsAcross(s, dx), span = ResolvedSpan(s);
        string what = IsDisc(s) ? $"{span:0.##} m disc" : $"{span * 1000:0} mm outlet";
        return NeedsRefine(s, dx)
            ? ("refine needed", $"The {what} is {cells:0.#} cells across at {dx * 1000:0} mm cells; it needs " +
                                $"{MinCellsAcross:0} (cells of {span / MinCellsAcross * 1000:0} mm or less). " +
                                "Put a simulation box around this room for a finer grid.")
            : ("resolution OK", $"The {what} is {cells:0.#} cells across at {dx * 1000:0} mm cells (needs {MinCellsAcross:0}).");
    }

    // ---------------------------------------------------------------- one-click box for "refine needed"

    /// <summary>Margin around a device's outlet, in its own widths (the brief: 1-2 disc or outlet widths), before
    /// the blend-band clearance goes on top.</summary>
    public const float BoxMarginWidths = 1.5f;

    /// <summary>Fine cells in Core's nested sponge band (LbmNested.NestedSpongeDefault, mirrored): the band is
    /// (cells + 1) x the fine Dx deep (NestedSession.SpongeWidth).</summary>
    public const int NestedSpongeCells = 8;

    /// <summary>The device's width for the margin: the disc diameter, else the outlet's long side.</summary>
    public static float BoxWidth(AirSource s) => IsDisc(s) ? 2f * s.Radius : MathF.Max(s.Width, s.Height);

    /// <summary>Box around <paramref name="devices"/>: each outlet's extent plus <see cref="BoxMarginWidths"/> of its
    /// width, plus the Grow-box rule's clearance (blend band + one fine cell, <see cref="BlendBand.Grow"/>) so no
    /// outlet sits in the band. The bottom stops at the ground, as Grow's does.</summary>
    public static DomainBox DeviceBox(IEnumerable<AirSource> devices, float band, float fineDx, float groundZ)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        foreach (var s in devices)
        {
            var (a, b) = BlendBand.Extent(s);
            var pad = new Vector3(BoxMarginWidths * BoxWidth(s) + band + fineDx);
            lo = Vector3.Min(lo, a - pad);
            hi = Vector3.Max(hi, b + pad);
        }
        lo.Z = MathF.Max(lo.Z, groundZ);
        return new DomainBox(lo, hi);
    }

    /// <summary>The cell size a box will solve at: nested boxes run at half the coarse Dx (Core's fixed ratio); a
    /// box that is the whole domain (v1, or any thermal run) spreads the cell budget over the box, as
    /// Voxelizer.BuildDomain does (0.1 m floor, 1 m cap).</summary>
    public static float BoxDx(float coarseDx, bool nested, int cellTarget, DomainBox box)
    {
        if (nested) return 0.5f * coarseDx;
        var size = box.Size;
        return cellTarget > 0 ? Math.Clamp(MathF.Cbrt(size.X * size.Y * size.Z / cellTarget), 0.1f, 1f) : coarseDx;
    }

    /// <summary>The plain answer for a device no box on this machine can resolve (a 120 mm split-AC slot needs
    /// 15 mm cells): what it means for the numbers, instead of a red chip that no click can fix.</summary>
    public static string TooSmallWhy(AirSource s, float boxDx, bool inBox)
    {
        float span = ResolvedSpan(s), cells = span / boxDx;
        string what = IsDisc(s) ? $"{span:0.##} m disc" : $"{span * 1000:0} mm outlet";
        return $"The {what} is {cells:0.#} cells across {(inBox ? "in the box's" : "even in a simulation box's")} " +
               $"{boxDx * 1000:0} mm cells; {MinCellsAcross:0} cells would need {span / MinCellsAcross * 1000:0} mm cells, finer " +
               "than this machine can run over a room. The device still runs and its measured flow is in the row; " +
               "treat the shape of its jet close to the outlet as approximate.";
    }

    /// <summary>R: the step one key press turns a floor fan by, degrees clockwise seen from above.</summary>
    public const float RotateStep = 45f;

    /// <summary>R turns a device only where it has a free heading: a floor fan. A ceiling device faces down and a
    /// wall device faces the room; spinning either would unmount it.</summary>
    /// <summary>The inlet-turbulence field acts only on link-wise supply patches (Revit air terminals); the palette
    /// devices are body-force regions and ignore it, so the inspector hides the field for them (P13a-02).</summary>
    public static bool TurbulenceApplies(AirSource s) => s.Device == DeviceKind.Patch && s.Kind == SourceKind.Supply;

    public static bool Rotatable(DeviceKind kind) => PresetFor(kind).Surface == MountSurface.Floor;

    /// <summary>A ceiling fan hangs on a downrod: 0.08 m under the slab its intake has no air to draw on and
    /// Core flags it choked (it wants 3 cells of clear air above the disc).</summary>
    public const float CeilingFanDrop = 0.35f;

    public static DevicePreset PresetFor(DeviceKind kind)
    {
        foreach (var p in Presets) if (p.Kind == kind) return p;
        return Presets[0];
    }

    /// <summary>The direction air moves, per Core's convention: a supply or a fan blows along Direction, a
    /// return draws air INTO the grille so Direction points at the wall. <paramref name="inward"/> is the
    /// surface normal pointing into the room.</summary>
    public static Vector3 FlowDirection(DevicePreset preset, Vector3 inward) => preset.Flow == SourceKind.Return
        ? -inward
        : inward;

    /// <summary>A new source from a preset at a placed position. Ids are unique per model so a later edit or
    /// a JSON round trip can find it again.</summary>
    public static AirSource Create(DevicePreset preset, Vector3 center, Vector3 inward, string level, int ordinal)
    {
        // A floor-standing fan blows horizontally: the floor normal would aim it at the ceiling.
        var direction = preset.Surface == MountSurface.Floor ? Horizontal(inward) : FlowDirection(preset, inward);
        var s = new AirSource
        {
            Id = $"dev-{preset.Kind.ToString().ToLowerInvariant()}-{ordinal}",
            Name = ordinal <= 1 ? KindName(preset.Kind) : $"{KindName(preset.Kind)} {ordinal}",
            Level = level,
            Kind = preset.Flow,
            Device = preset.Kind,
            Enabled = true,
            Center = center,
            Direction = direction,
            Width = preset.Width,
            Height = preset.Height,
            FlowRate = preset.FlowRate,
            Radius = preset.Radius,
            Swirl = preset.Swirl,
            SupplyTemperature = preset.SupplyTemperature,
            LouvreAngle = preset.Louvre,
        };
        return s;
    }

    /// <summary>The next free ordinal for this kind: one past the highest in use, so a remove followed by an
    /// add can never hand out an id that is still taken (a count would).</summary>
    public static int NextOrdinal(BuildingModel model, DeviceKind kind)
    {
        string prefix = $"dev-{kind.ToString().ToLowerInvariant()}-";
        int highest = 0;
        foreach (var s in model.Sources)
        {
            if (s.Device != kind) continue;
            highest = Math.Max(highest, 1);
            if (s.Id.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(s.Id[prefix.Length..], out int n))
                highest = Math.Max(highest, n);
        }
        int next = highest + 1;
        while (model.Sources.Any(s => s.Id == prefix + next)) next++;
        return next;
    }

    /// <summary>The horizontal part of a vector, unit length; +Y (north) when it has none.</summary>
    public static Vector3 Horizontal(Vector3 v)
    {
        var flat = new Vector3(v.X, v.Y, 0);
        return flat.LengthSquared() > 1e-6f ? Vector3.Normalize(flat) : Vector3.UnitY;
    }

    /// <summary>Compass yaw of a horizontal direction, degrees, 0 = +Y (north), clockwise. The inspector edits
    /// this rather than a vector.</summary>
    public static float Yaw(Vector3 d)
    {
        float yaw = MathF.Atan2(d.X, d.Y) * 180f / MathF.PI;
        return yaw < 0 ? yaw + 360f : yaw;
    }

    /// <summary>Yaw back to a unit vector, keeping the original Z component (a tilted split-AC jet keeps its
    /// tilt when the user spins it).</summary>
    public static Vector3 FromYaw(float yawDeg, float z)
    {
        // A vertical vector has no yaw to keep: spinning it would always hand back (0, 0, 1). Treat it as flat.
        if (MathF.Abs(z) > 0.99f) z = 0f;
        float r = yawDeg * MathF.PI / 180f;
        var flat = new Vector2(MathF.Sin(r), MathF.Cos(r));
        float horizontal = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        return new Vector3(flat.X * horizontal, flat.Y * horizontal, z);
    }

    // ---------------------------------------------------------------- placement

    /// <summary>Where a click lands for a device of this preset: the nearest room surface of the requested
    /// kind along the ray. Null when the ray misses every room.</summary>
    public static DevicePlacement? Pick(BuildingModel model, Vector3 eye, Vector3 dir, DevicePreset preset) =>
        preset.Surface switch
        {
            MountSurface.Ceiling => PickHorizontal(model, eye, dir, ceiling: true, preset),
            MountSurface.Floor => PickHorizontal(model, eye, dir, ceiling: false, preset),
            _ => PickWall(model, eye, dir, preset),
        };

    static DevicePlacement? PickHorizontal(BuildingModel model, Vector3 eye, Vector3 dir, bool ceiling,
        DevicePreset preset)
    {
        if (MathF.Abs(dir.Z) < 1e-6f) return null;
        DevicePlacement? best = null;
        float bestT = float.MaxValue;
        foreach (var room in model.Rooms)
        {
            if (room.Loops.Count == 0) continue;
            // 0.08 m off the slab: the device's cells must be air, and the voxelizer's ceiling cell is solid.
            float z = ceiling ? room.ZMax - preset.CeilingDrop : room.ZMin + 0.08f;
            float t = (z - eye.Z) / dir.Z;
            if (t <= 0 || t >= bestT) continue;
            var p = eye + dir * t;
            if (!InLoops(room, p.X, p.Y)) continue;
            bestT = t;
            float mount = ceiling ? z : MathF.Min(room.ZMax - 0.2f, room.ZMin + preset.MountHeight);
            // Floor devices get the view direction, flattened, as the way they face: blowing away from the
            // viewer, horizontally. Ceiling devices face down.
            best = new DevicePlacement(new Vector3(p.X, p.Y, mount), ceiling ? -Vector3.UnitZ : Horizontal(dir),
                room, "");
        }
        return best;
    }

    /// <summary>Ray against the vertical planes of every room loop edge. The normal returned points into the
    /// room, which is what a supply or a fan blows along.</summary>
    static DevicePlacement? PickWall(BuildingModel model, Vector3 eye, Vector3 dir, DevicePreset preset)
    {
        DevicePlacement? best = null;
        float bestT = float.MaxValue;
        foreach (var room in model.Rooms)
            foreach (var loop in room.Loops)
                for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
                {
                    var a = loop[j];
                    var b = loop[i];
                    var edge = b - a;
                    float len = edge.Length();
                    if (len < 1e-4f) continue;
                    var n2 = new Vector2(-edge.Y, edge.X) / len;
                    float denom = n2.X * dir.X + n2.Y * dir.Y;
                    if (MathF.Abs(denom) < 1e-6f) continue;
                    float t = (n2.X * (a.X - eye.X) + n2.Y * (a.Y - eye.Y)) / denom;
                    if (t <= 0 || t >= bestT) continue;
                    var p = eye + dir * t;
                    // Inside the segment, and clear of the floor and the ceiling.
                    float along = Vector2.Dot(new Vector2(p.X, p.Y) - a, edge / len);
                    if (along < 0.15f || along > len - 0.15f) continue;
                    if (p.Z < room.ZMin + 0.3f || p.Z > room.ZMax - 0.2f) continue;

                    // Winding is not guaranteed, so decide "inward" by testing a point just off the face.
                    var inward = new Vector3(n2.X, n2.Y, 0);
                    if (!InLoops(room, p.X + inward.X * 0.12f, p.Y + inward.Y * 0.12f)) inward = -inward;
                    bestT = t;
                    float mount = Math.Clamp(preset.MountHeight <= 0 ? p.Z - room.ZMin : preset.MountHeight,
                        0.3f, MathF.Max(0.3f, room.ZMax - room.ZMin - 0.25f));
                    // Set back 60 mm from the face so every driven cell is air, not the wall itself.
                    var centre = new Vector3(p.X, p.Y, room.ZMin + mount) + inward * 0.06f;
                    best = new DevicePlacement(centre, inward, room, "");
                }
        if (best is { } hit && preset.NeedsExteriorOpening && !InOpenExteriorOpening(model, hit.Center))
            return hit with { Warning = "an extract has to sit in an OPEN exterior opening (a window or vent) — this spot has none" };
        return best;
    }

    /// <summary>An extract fan has to discharge somewhere: Core drives air along Direction, and without a hole
    /// in the wall right there it just piles up against it. So the device must sit inside the rectangle of an
    /// open opening in its own wall plane, and that opening must lead outside (no room on one side of it).</summary>
    public static bool InOpenExteriorOpening(BuildingModel model, Vector3 p)
    {
        foreach (var o in model.Openings)
        {
            if (!o.IsOpen || o.Normal.LengthSquared() < 1e-6f) continue;
            var n = Horizontal(o.Normal);
            var d = p - o.Center;
            float through = Vector3.Dot(d, n);                          // across the wall
            float along = Vector3.Dot(d, new Vector3(-n.Y, n.X, 0));    // along the wall
            if (MathF.Abs(through) > o.Depth * 0.5f + 0.25f) continue;
            if (MathF.Abs(along) > o.Width * 0.5f || MathF.Abs(d.Z) > o.Height * 0.5f) continue;
            if (IsExterior(model, o)) return true;
        }
        return false;
    }

    /// <summary>An opening leads outside when the point 0.3 m past either face is in no room.</summary>
    public static bool IsExterior(BuildingModel model, Opening o)
    {
        var n = Horizontal(o.Normal);
        float reach = o.Depth * 0.5f + 0.3f;
        foreach (float side in new[] { 1f, -1f })
        {
            var q = o.Center + n * side * reach;
            if (!model.Rooms.Any(r => q.Z >= r.ZMin && q.Z <= r.ZMax && InLoops(r, q.X, q.Y))) return true;
        }
        return false;
    }

    public static bool InLoops(Room room, float x, float y)
    {
        bool inside = false;
        foreach (var loop in room.Loops)
            for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
            {
                var a = loop[i];
                var b = loop[j];
                if (a.Y > y != b.Y > y && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
        return inside;
    }
}

/// <summary>A resolved placement: where the device goes, which way the surface faces into the room, and the
/// room it landed in. <see cref="Warning"/> is non-empty when the spot is legal to click but poor.</summary>
public readonly record struct DevicePlacement(Vector3 Center, Vector3 Inward, Room Room, string Warning);

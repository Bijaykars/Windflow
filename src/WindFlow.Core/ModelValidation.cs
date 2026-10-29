using System.Numerics;

namespace WindFlow.Core;

/// <summary>RA-11: what the voxeliser and the scene builder would choke on, checked before a model from outside (a file, the Revit live link) is used.
/// One rule for every entry point.</summary>
public static class ModelValidation
{
    /// <summary>Reads a model file: refuses one over <paramref name="maxBytes"/> before parsing it, then parses and <see cref="Validate"/>s it.
    /// Throws <see cref="InvalidDataException"/> with a short reason naming the bad field.</summary>
    public static BuildingModel Load(string path, long maxBytes)
    {
        long size = new FileInfo(path).Length;
        if (size > maxBytes) throw new InvalidDataException($"the file is {size >> 20} MB, over the {maxBytes >> 20} MB limit");
        BuildingModel? model;
        try { model = BuildingModel.FromJson(File.ReadAllText(path)); }
        catch (System.Text.Json.JsonException ex) { throw new InvalidDataException($"not a valid model file: {ex.Message}"); }
        if (model == null) throw new InvalidDataException("the file holds no model (json is null)");
        if (Validate(model) is { } problem) throw new InvalidDataException(problem);
        return model;
    }

    /// <summary>RC-04: plausibility bounds, as input validation (not physics): a coordinate beyond +-100 km or a size beyond 1 km is a corrupt file, not a building.</summary>
    public const float MaxCoord = 1e5f, MaxSize = 1e3f;

    /// <summary>Null when the model is usable, else the first problem found. A field that is NaN for "not set" (SupplyTemperature, ReturnCenter, SurfaceTemperature) may be NaN but not infinite.</summary>
    public static string? Validate(BuildingModel m)
    {
        static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        static bool Coord(float v) => float.IsFinite(v) && MathF.Abs(v) <= MaxCoord;
        static bool Coord3(Vector3 v) => Coord(v.X) && Coord(v.Y) && Coord(v.Z);
        static bool Pos(float v) => float.IsFinite(v) && v > 0 && v <= MaxSize;          // a size: (0, 1e3] m
        static bool PosOrZero(float v) => float.IsFinite(v) && v >= 0 && v <= MaxSize;   // a size the code documents 0 for (default / none)
        static bool NotInf(float v) => !float.IsInfinity(v);                              // "NaN = unset" fields
        static bool Small3(Vector3 v) => MathF.Abs(v.X) <= MaxSize && MathF.Abs(v.Y) <= MaxSize && MathF.Abs(v.Z) <= MaxSize;
        if (m.Obstacles == null || m.Openings == null || m.Rooms == null || m.Sources == null || m.Storeys == null ||
            m.Env == null || m.Heat == null || m.Contaminants == null || m.Warnings == null)
            return "model is missing a required list";
        if (m.Warnings.Any(w => w == null)) return "Warnings holds a null entry";
        if (m.Obstacles.Count == 0) return "model has no geometry (no obstacles)";
        if (!Coord(m.GroundZ)) return $"GroundZ {m.GroundZ} is not within +-{MaxCoord:0} m";
        if (!float.IsFinite(m.NorthDeg) || MathF.Abs(m.NorthDeg) > 720f) return "NorthDeg is not a plausible angle";
        foreach (var s in m.Storeys)
        {
            if (s == null || s.Id == null || s.Name == null) return "a storey is null or has a null Id or Name";
            if (!Coord(s.Elevation) || !PosOrZero(s.Height)) return $"storey {s.Id}: Elevation or Height out of range";
        }
        foreach (var mesh in m.Obstacles)
        {
            if (mesh?.Vertices == null || mesh.Indices == null) return "an obstacle mesh is empty";
            if (mesh.Id == null) return "an obstacle mesh has a null Id";
            if (mesh.Indices.Length % 3 != 0) return $"mesh {mesh.Id}: index count is not a multiple of 3";
            foreach (var v in mesh.Vertices) if (!Finite(v)) return $"mesh {mesh.Id}: non-finite vertex";
            foreach (var v in mesh.Vertices) if (!Coord3(v)) return $"mesh {mesh.Id}: a vertex is beyond +-{MaxCoord:0} m";
            foreach (int i in mesh.Indices)
                if ((uint)i >= (uint)mesh.Vertices.Length) return $"mesh {mesh.Id}: index {i} out of range (the mesh has {mesh.Vertices.Length} vertices)";
        }
        foreach (var o in m.Openings)
        {
            if (o == null || o.Id == null || o.Name == null || o.Level == null) return $"opening {o?.Id}: null Id, Name or Level";
            if (!Finite(o.Center) || !Finite(o.Normal) || !float.IsFinite(o.Width) || !float.IsFinite(o.Height) || !float.IsFinite(o.Depth))
                return $"opening {o.Id}: non-finite geometry (Center, Normal, Width, Height or Depth)";
            if (!Coord3(o.Center) || !Small3(o.Normal)) return $"opening {o.Id}: Center beyond +-{MaxCoord:0} m or an implausible Normal";
            if (!Pos(o.Width) || !Pos(o.Height) || !PosOrZero(o.Depth)) return $"opening {o.Id}: Width or Height outside (0, {MaxSize:0}] m, or Depth outside [0, {MaxSize:0}] m";
            if (!Enum.IsDefined(o.Kind) || !Enum.IsDefined(o.SideA) || !Enum.IsDefined(o.SideB)) return $"opening {o.Id}: Kind, SideA or SideB is not a defined value";
            if (o.DischargeCoefficient is { } cd && (!float.IsFinite(cd) || cd < 0 || cd > 10)) return $"opening {o.Id}: DischargeCoefficient out of range";
        }
        foreach (var s in m.Sources)
        {
            if (s == null || s.Id == null || s.Name == null || s.Level == null) return $"device {s?.Id}: null Id, Name or Level";
            if (!Finite(s.Center) || !Finite(s.Direction) || !float.IsFinite(s.Width) || !float.IsFinite(s.Height) || !float.IsFinite(s.FlowRate))
                return $"device {s.Id}: non-finite geometry or flow";
            if (!float.IsFinite(s.Radius) || !float.IsFinite(s.ReturnWidth) || !float.IsFinite(s.ReturnHeight) || !float.IsFinite(s.Swirl) || !float.IsFinite(s.LouvreAngle) || !float.IsFinite(s.Thrust))
                return $"device {s.Id}: non-finite Radius, ReturnWidth, ReturnHeight, Swirl, LouvreAngle or Thrust";
            if (!Coord3(s.Center) || !Small3(s.Direction)) return $"device {s.Id}: Center beyond +-{MaxCoord:0} m or an implausible Direction";
            if (!PosOrZero(s.Width) || !PosOrZero(s.Height) || !PosOrZero(s.Radius) || s.ReturnWidth > MaxSize || s.ReturnHeight > MaxSize)
                return $"device {s.Id}: Width, Height, Radius or the return size out of range (0 to {MaxSize:0} m)";
            if (MathF.Abs(s.FlowRate) > 1e6f || MathF.Abs(s.Thrust) > 1e6f || MathF.Abs(s.Swirl) > 10f || MathF.Abs(s.LouvreAngle) > 360f)
                return $"device {s.Id}: FlowRate, Thrust, Swirl or LouvreAngle implausibly large";
            if (!Enum.IsDefined(s.Kind) || !Enum.IsDefined(s.Device)) return $"device {s.Id}: Kind or Device is not a defined value";
            // SupplyTemperature and ReturnCenter are NaN when "not set": NaN passes, +-Infinity does not
            if (!NotInf(s.SupplyTemperature) || !NotInf(s.ReturnCenter.X) || !NotInf(s.ReturnCenter.Y) || !NotInf(s.ReturnCenter.Z) ||
                (s.TurbulenceIntensity is { } tu && (!float.IsFinite(tu) || tu < 0 || tu > 10)))
                return $"device {s.Id}: an infinite SupplyTemperature or ReturnCenter, or a TurbulenceIntensity out of range";
        }
        foreach (var h in m.Heat)
        {
            if (h == null || h.Id == null || h.Name == null || h.Level == null) return $"heat source {h?.Id}: null Id, Name or Level";
            if (!Finite(h.Center) || !float.IsFinite(h.Width) || !float.IsFinite(h.Depth) || !float.IsFinite(h.Height) || !float.IsFinite(h.Watts))
                return $"heat source {h.Id}: non-finite Center, size or Watts";
            if (!Coord3(h.Center) || !PosOrZero(h.Width) || !PosOrZero(h.Depth) || !PosOrZero(h.Height) || MathF.Abs(h.Watts) > 1e9f)
                return $"heat source {h.Id}: Center beyond +-{MaxCoord:0} m, a size outside 0 to {MaxSize:0} m, or implausible Watts";
            if (!Enum.IsDefined(h.Kind) || h.People < 0 || h.People > 100000 || !NotInf(h.SurfaceTemperature))   // SurfaceTemperature is NaN when "not set"
                return $"heat source {h.Id}: Kind not a defined value, People out of range, or an infinite SurfaceTemperature";
        }
        foreach (var c in m.Contaminants)
        {
            if (c == null || c.Id == null || c.Name == null || c.Level == null) return $"contaminant source {c?.Id}: null Id, Name or Level";
            if (!Finite(c.Center) || !float.IsFinite(c.Rate)) return $"contaminant source {c.Id}: non-finite Center or Rate";
            if (!Coord3(c.Center) || MathF.Abs(c.Rate) > 1e12f) return $"contaminant source {c.Id}: Center beyond +-{MaxCoord:0} m or an implausible Rate";
        }
        var env = m.Env;
        if (!float.IsFinite(env.OutdoorTemperature) || !float.IsFinite(env.IndoorTemperature) || !float.IsFinite(env.RelativeHumidity) || !float.IsFinite(env.OutdoorCo2))
            return "Env: a non-finite temperature, humidity or CO2 value";
        if (env.OutdoorTemperature is < -100f or > 100f || env.IndoorTemperature is < -100f or > 100f || env.RelativeHumidity is < 0f or > 100f || env.OutdoorCo2 is < 0f or > 1e6f)
            return "Env: a temperature, humidity or CO2 value outside a plausible range";
        foreach (var r in m.Rooms)
        {
            if (r == null || string.IsNullOrEmpty(r.Id) || string.IsNullOrEmpty(r.Name) || r.Level == null) return $"room {r?.Id}: empty or null Id, Name or Level";
            if (r.Loops == null) return $"room {r.Id}: no loops";
            if (!float.IsFinite(r.ZMin) || !float.IsFinite(r.ZMax)) return $"room {r.Id}: non-finite ZMin or ZMax";
            if (!Coord(r.ZMin) || !Coord(r.ZMax) || r.Occupants < 0 || r.Occupants > 100000) return $"room {r.Id}: ZMin or ZMax beyond +-{MaxCoord:0} m, or Occupants out of range";
            foreach (var loop in r.Loops)
            {
                if (loop == null || loop.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y))) return $"room {r.Id}: non-finite loop vertex";
                if (loop.Any(p => !Coord(p.X) || !Coord(p.Y))) return $"room {r.Id}: a loop vertex is beyond +-{MaxCoord:0} m";
            }
        }
        return null;
    }
}

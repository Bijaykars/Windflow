using System.IO;          // not in implicit usings for WPF projects
using System.Numerics;
using Autodesk.Revit.DB;
using WindFlow.Core;
using Mesh = WindFlow.Core.Mesh;
using Opening = WindFlow.Core.Opening;
using Room = WindFlow.Core.Room;
using DBOpening = Autodesk.Revit.DB.Opening;
using RvtRoom = Autodesk.Revit.DB.Architecture.Room;

namespace WindFlow.Revit;

/// <summary>A storey in metres, internal-origin elevations.</summary>
public sealed record LevelInfo(long Id, string Name, float Elevation, float Height)
{
    public bool Overlaps(float zMin, float zMax) => zMax > Elevation + 0.05f && zMin < Elevation + Height - 0.05f;
}

/// <summary>Lightweight opening row for the UI. ZMin/ZMax (metres) decide which storeys it belongs to.</summary>
public sealed record OpeningInfo(string UniqueId, string Name, string Mark, OpeningKind Kind, string LevelName, long HostId, float ZMin, float ZMax);

public sealed record RoomInfo(Room Room, float BaseZ);

public sealed record SelectedRoomInfo(string UniqueId, string Name, float LevelZ);

/// <summary>One row of the Advanced "People &amp; heat" table: the room, and how many people it starts with.</summary>
public sealed record RoomPeopleInfo(string UniqueId, string Name, string LevelName, long LevelId, float AreaM2, int DefaultOccupants);

public static class RevitExtractor
{
    const double FtToM = 0.3048;
    const double CubicFtPerSecToM3S = 0.028316846;
    static float M(double ft) => (float)(ft * FtToM);
    static double Ft(double m) => m / FtToM;
    static Vector3 P(XYZ p) => new(M(p.X), M(p.Y), M(p.Z));          // point, feet -> metres
    static Vector3 D(XYZ d) => new((float)d.X, (float)d.Y, (float)d.Z); // direction, unitless

    /// <summary>Anything that blocks air. Walls carry their own openings cut out, slabs and roofs close the storeys.</summary>
    static readonly BuiltInCategory[] StructureCategories =
    {
        BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Ceilings,
        BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming,
        BuiltInCategory.OST_Stairs, BuiltInCategory.OST_CurtainWallPanels, BuiltInCategory.OST_CurtainWallMullions,
    };
    static readonly BuiltInCategory[] FurnitureCategories =
    {
        BuiltInCategory.OST_Furniture, BuiltInCategory.OST_FurnitureSystems, BuiltInCategory.OST_Casework,
        BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_PlumbingFixtures,
        BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_GenericModel,
    };
    static readonly BuiltInCategory[] DoorWindowCategories = { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows };

    const float MinElementSize = 0.25f;     // metres of bounding-box diagonal
    const int MaxTrianglesPerElement = 20_000;

    // ponytail: host model only. Linked models need RevitLinkInstance.GetTotalTransform applied to their elements.

    // Level.ProjectElevation (relative to the internal origin) is used rather than Level.Elevation (relative to the
    // project base / survey point), because element geometry comes back in internal coordinates.
    public static List<LevelInfo> Levels(Document doc)
    {
        var all = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
        var stories = all.Where(l => l.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY)?.AsInteger() == 1).ToList();
        if (stories.Count == 0) stories = all;

        var result = new List<LevelInfo>();
        for (int i = 0; i < stories.Count; i++)
        {
            float z = M(stories[i].ProjectElevation);
            var next = stories.Skip(i + 1).FirstOrDefault(n => M(n.ProjectElevation) > z + 0.1f);
            float height = next != null ? M(next.ProjectElevation) - z : 3.0f;
            result.Add(new LevelInfo(stories[i].Id.Value, stories[i].Name, z, height));
        }
        return result;
    }

    /// <summary>The storey whose [Elevation, Elevation + Height) contains z (metres), else the nearest one.
    /// Everything is placed by elevation, never by LevelId: rooms, doors and terminals are often hosted on levels
    /// that are not flagged as building storeys (parapet, ceiling, "T.O. slab").</summary>
    public static LevelInfo? LevelAt(IReadOnlyList<LevelInfo> levels, float z) =>
        levels.FirstOrDefault(l => Gap(l.Elevation, l.Height, z) == 0)
        ?? levels.OrderBy(l => Gap(l.Elevation, l.Height, z)).FirstOrDefault();

    static Storey? StoreyAt(BuildingModel model, float z) =>
        model.Storeys.FirstOrDefault(s => Gap(s.Elevation, s.Height, z) == 0)
        ?? model.Storeys.OrderBy(s => Gap(s.Elevation, s.Height, z)).FirstOrDefault();

    /// <summary>0 when z is inside [elevation, elevation + height), else the distance to that range.</summary>
    static float Gap(float elevation, float height, float z) =>
        z < elevation - 0.01f ? elevation - z
        : z >= elevation + height - 0.01f ? z - (elevation + height) + 0.01f
        : 0;

    public static float? ViewLevelZ(View? view) => view?.GenLevel is Level l ? M(l.ProjectElevation) : null;

    public static SelectedRoomInfo? SelectedRoom(Autodesk.Revit.UI.UIDocument uidoc)
    {
        var doc = uidoc.Document;
        foreach (var id in uidoc.Selection.GetElementIds())
            if (doc.GetElement(id) is RvtRoom r && RoomBaseZ(doc, r) is { } baseZ)
                return new SelectedRoomInfo(r.UniqueId, r.Name, baseZ);   // the storey is picked from this elevation
        return null;
    }

    /// <summary>The room's floor: its level's elevation plus the room's base offset. Null when the level is gone.</summary>
    static float? RoomBaseZ(Document doc, RvtRoom r) =>
        doc.GetElement(r.LevelId) is Level l ? M(l.ProjectElevation) + M(Param(r, BuiltInParameter.ROOM_LOWER_OFFSET)) : null;

    // ---- the building ----

    /// <summary>The whole building: obstacles from every storey, openings everywhere (closed on unanalysed storeys),
    /// rooms and air sources on the analysed ones.</summary>
    public static BuildingModel ExtractBuilding(Document doc, ISet<long> analysedLevelIds,
        IReadOnlyDictionary<string, bool> openStates, IReadOnlyList<AirSource> manualSources, bool includeFurniture,
        out int skippedRooms)
    {
        var levels = Levels(doc);
        var model = new BuildingModel
        {
            Name = string.IsNullOrEmpty(doc.Title) ? "Building" : doc.Title,
            GroundZ = levels.Count > 0 ? levels[0].Elevation : 0,
            NorthDeg = NorthDeg(doc),      // P13b-04 / S3-09: where true north points in these (internal) model axes
        };
        foreach (var l in levels)
            model.Storeys.Add(new Storey
            {
                Id = l.Id.ToString(), Name = l.Name, Elevation = l.Elevation, Height = l.Height,
                Analysed = analysedLevelIds.Contains(l.Id),
            });

        var droppedGeometry = new List<string>();
        AddObstacles(doc, model, includeFurniture, droppedGeometry);
        if (droppedGeometry.Count > 0) model.Warnings.Add(ExtractionNotes.DroppedGeometry(droppedGeometry));      // S4-06

        bool IsOpen(string id) => !openStates.TryGetValue(id, out var open) || open;
        void AddOpening(Opening? o)
        {
            if (o == null) return;
            var storey = StoreyAt(model, o.Center.Z);
            o.Level = storey?.Name ?? "";
            o.IsOpen = storey is { Analysed: true } && IsOpen(o.Id);   // unanalysed storeys are sealed
            OrientOutward(doc, o);
            model.Openings.Add(o);
        }
        foreach (var fi in DoorsAndWindows(doc)) AddOpening(FromInstance(doc, fi));
        foreach (var op in WallOpenings(doc)) AddOpening(FromWallOpening(op));

        var rooms = Rooms(doc, out int lostRooms);
        var raisedTops = new List<string>();
        var allLevelZ = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Select(l => M(l.ProjectElevation)).ToList();   // RX-07: story or not
        foreach (var r in rooms)
        {
            var storey = StoreyAt(model, r.BaseZ);   // by elevation: a room's level is often not a building storey
            if (storey == null) { lostRooms++; continue; }
            if (!storey.Analysed) continue;
            r.Room.Level = storey.Name;
            // P15-05: the room's upper limit (Revit default 8 ft) may sit below the ceiling. The top storey's height is a 3.0 m default, not measured: leave it.
            if (storey != model.Storeys[^1])
            {
                float top = ExtractionNotes.RoomTop(r.Room.ZMax, r.BaseZ, storey.Elevation, storey.Height, allLevelZ);
                if (top > r.Room.ZMax + 0.01f) { r.Room.ZMax = top; raisedTops.Add(r.Room.Name); }
            }
            model.Rooms.Add(r.Room);
        }
        if (raisedTops.Count > 0) model.Warnings.Add(ExtractionNotes.RaisedRoomTops(raisedTops));
        skippedRooms = lostRooms;

        var defaultedTerminals = new List<string>();
        foreach (var source in AirTerminals(doc, levels, out _, defaultedTerminals))
        {
            var level = LevelAt(levels, source.Center.Z);
            if (level != null && analysedLevelIds.Contains(level.Id)) model.Sources.Add(source);
        }
        foreach (var manual in manualSources)
            model.Sources.Add(new AirSource
            {
                Id = manual.Id, Name = manual.Name, Kind = manual.Kind, Enabled = manual.Enabled,
                Center = manual.Center, Direction = manual.Direction, Width = manual.Width, Height = manual.Height,
                FlowRate = manual.FlowRate,
                Level = string.IsNullOrEmpty(manual.Level) ? StoreyAt(model, manual.Center.Z)?.Name ?? "" : manual.Level,
            });

        if (defaultedTerminals.Count > 0) model.Warnings.Add(ExtractionNotes.DefaultedTerminals(defaultedTerminals));   // S4-06
        AddAdvanced(model);
        WriteDebugJson(model);
        return model;
    }

    /// <summary>Outdoor/indoor conditions, heat sources and tracers used to come from the Advanced panel, now
    /// removed with the rest of the WPF UI (the app is where those get edited). This exporter ships
    /// whole-building defaults so BuildingModel.Env always has a value; Heat/Contaminants stay empty.</summary>
    static void AddAdvanced(BuildingModel model)
    {
        model.Env = new WindFlow.Core.Environment();
    }

    /// <summary>Rooms as the People table shows them, so the user can override the occupant count per room.</summary>
    public static List<RoomPeopleInfo> RoomPeople(Document doc, IReadOnlyList<LevelInfo> levels)
    {
        var result = new List<RoomPeopleInfo>();
        foreach (var r in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
                     .WhereElementIsNotElementType().OfType<RvtRoom>())
        {
            if (r.Location == null || r.Area <= 0) continue;
            if (RoomBaseZ(doc, r) is not { } baseZ) continue;
            var level = LevelAt(levels, baseZ);
            result.Add(new RoomPeopleInfo(r.UniqueId, r.Name, level?.Name ?? "", level?.Id ?? -1, AreaM2(r), DefaultOccupants(r)));
        }
        return result.OrderBy(x => x.LevelName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static float AreaM2(RvtRoom r) => (float)(r.Area * FtToM * FtToM);

    /// <summary>The room under a picked point (feet, internal coordinates), so a lighting load can default to
    /// 10 W/m² of it. Null outside any room.</summary>
    // untested: GetRoomAtPoint wants a point inside the room volume, so the caller lifts it off the floor first.
    public static (string Name, float AreaM2)? RoomAt(Document doc, XYZ point)
    {
        try { return doc.GetRoomAtPoint(point) is RvtRoom r && r.Area > 0 ? (r.Name, AreaM2(r)) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>People in a room before the user edits it: a numeric occupancy parameter if the project carries one,
    /// else one person per 10 m².</summary>
    // untested: which of these parameter names a real project uses. ROOM_OCCUPANCY ("Occupancy") is a text field in
    // Revit, so it is parsed rather than read as a number, and anything unparseable falls through to the area rule.
    static int DefaultOccupants(RvtRoom r)
    {
        foreach (var name in new[] { "Number of People", "Occupant Count", "Occupancy" })
        {
            var p = r.LookupParameter(name);
            if (p is not { HasValue: true }) continue;
            int n = p.StorageType switch
            {
                StorageType.Integer => p.AsInteger(),
                StorageType.Double => (int)Math.Round(p.AsDouble()),
                StorageType.String => int.TryParse(p.AsString(), out var s) ? s : 0,
                _ => 0,
            };
            if (n > 0) return n;
        }
        return Math.Max(0, (int)Math.Round(AreaM2(r) / 10.0));
    }

    static void AddObstacles(Document doc, BuildingModel model, bool includeFurniture, List<string> dropped)
    {
        Collect(StructureCategories, ViewDetailLevel.Fine, false);
        if (includeFurniture) Collect(FurnitureCategories, ViewDetailLevel.Coarse, true);

        void Collect(BuiltInCategory[] categories, ViewDetailLevel detail, bool furniture)
        {
            const double Far = 1e5;   // feet, "infinite" in plan and upwards: roofs above the top storey count too
            var outline = new Outline(new XYZ(-Far, -Far, Ft(model.GroundZ - 0.05)), new XYZ(Far, Far, Far));
            var options = new Options { DetailLevel = detail };
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType()
                         .WherePasses(new ElementMulticategoryFilter(categories))
                         .WherePasses(new BoundingBoxIntersectsFilter(outline)))
            {
                try
                {
                    if (e.get_BoundingBox(null) is not { } bb) continue;
                    if (M((bb.Max - bb.Min).GetLength()) < MinElementSize) continue;   // knobs, trim, tiny fittings
                    if (e.get_Geometry(options) is not { } ge) continue;
                    var mesh = ToMesh(e.UniqueId, ge, bb);
                    if (mesh.Indices.Length > 0) { mesh.Furniture = furniture; model.Obstacles.Add(mesh); }   // RX-05: voxelized after the opening carve
                }
                catch (Exception) { dropped.Add($"{e.Name} (id {e.Id})"); }     // S4-06: left out, and said so (ExtractionNotes.DroppedGeometry)
            }
        }
    }

    /// <summary>Air terminals as source patches, in model order. Terminals without a flow parameter are counted, not guessed at.</summary>
    public static List<AirSource> AirTerminals(Document doc, IReadOnlyList<LevelInfo> levels, out int skipped, List<string>? defaulted = null)
    {
        int skippedCount = 0;
        var list = new List<AirSource>();
        foreach (var fi in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_DuctTerminal)
                     .WhereElementIsNotElementType().OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
        {
            var bb = fi.get_BoundingBox(null);
            Vector3? centre = fi.Location is LocationPoint lp ? P(lp.Point) : bb != null ? P((bb.Min + bb.Max) / 2) : null;
            if (centre is not { } center) continue;

            double flow = Param(fi, BuiltInParameter.RBS_DUCT_FLOW_PARAM);   // internal units: cubic feet per second
            if (flow <= 0) { skippedCount++; continue; }

            var (kind, direction) = TerminalFlow(fi, out bool read);
            if (!read) defaulted?.Add(DisplayName(fi));      // S4-06: a guessed direction is a warning, not silence
            float width = 0.4f, height = 0.4f;
            if (bb != null)
            {
                width = Math.Clamp(M(bb.Max.X - bb.Min.X), 0.15f, 1.2f);
                height = Math.Clamp(M(bb.Max.Y - bb.Min.Y), 0.15f, 1.2f);
            }
            list.Add(new AirSource
            {
                Id = fi.UniqueId, Name = DisplayName(fi), Kind = kind, Center = center, Direction = direction,
                Width = width, Height = height, FlowRate = (float)(flow * CubicFtPerSecToM3S),
                Level = LevelAt(levels, center.Z)?.Name ?? "",
                Enabled = true,
            });
        }
        skipped = skippedCount;
        return list;
    }

    /// <summary>RevitAPI.xml says only "the coordinate system's origin is the location of the connector and the
    /// Z-axis is normal to the connector"; the established convention is that BasisZ points out of the terminal
    /// into the duct. So supply air travels along -BasisZ (out of the diffuser) and return air along +BasisZ (into it).</summary>
    // untested: the BasisZ outward convention, and families whose connector is modelled the other way round.
    static (SourceKind Kind, Vector3 Direction) TerminalFlow(FamilyInstance fi, out bool read)
    {
        read = false;
        try
        {
            var manager = fi.MEPModel?.ConnectorManager;
            if (manager != null)
                foreach (Connector c in manager.Connectors)
                {
                    if (c.Domain != Domain.DomainHvac) continue;
                    var kind = c.DuctSystemType switch
                    {
                        Autodesk.Revit.DB.Mechanical.DuctSystemType.SupplyAir => SourceKind.Supply,
                        Autodesk.Revit.DB.Mechanical.DuctSystemType.ReturnAir
                            or Autodesk.Revit.DB.Mechanical.DuctSystemType.ExhaustAir => SourceKind.Return,
                        _ => SourceKind.Supply,
                    };
                    var basisZ = c.CoordinateSystem.BasisZ;
                    var d = D(kind == SourceKind.Supply ? basisZ.Negate() : basisZ);
                    if (d.LengthSquared() > 1e-8f) { read = true; return (kind, Vector3.Normalize(d)); }
                }
        }
        catch (Exception) { /* logical connectors and odd families fall through to the default below, and are reported */ }
        return (SourceKind.Supply, new Vector3(0, 0, -1));   // a diffuser blowing down
    }

    public static List<RoomInfo> Rooms(Document doc, out int unresolved)
    {
        int lost = 0;
        var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
        var result = new List<RoomInfo>();
        foreach (var r in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<RvtRoom>())
        {
            if (r.Location == null || r.Area <= 0) continue;
            if (RoomBaseZ(doc, r) is not { } baseZ) { lost++; continue; }   // its level was deleted
            var room = new Room { Id = r.UniqueId, Name = r.Name, ZMin = baseZ };
            room.ZMax = room.ZMin + M(r.UnboundedHeight);
            room.Occupants = DefaultOccupants(r);
            var loops = r.GetBoundarySegments(options);
            if (loops != null)
                foreach (var loop in loops)
                {
                    var pts = new List<Vector2>();
                    foreach (var seg in loop)
                    {
                        var t = seg.GetCurve().Tessellate();
                        for (int i = 0; i < t.Count - 1; i++) pts.Add(new Vector2(M(t[i].X), M(t[i].Y)));   // last point = next curve's first
                    }
                    if (pts.Count >= 3) room.Loops.Add(pts.ToArray());
                }
            result.Add(new RoomInfo(room, baseZ));
        }
        unresolved = lost;
        return result;
    }

    public static List<OpeningInfo> Openings(Document doc)
    {
        var result = new List<OpeningInfo>();
        foreach (var fi in DoorsAndWindows(doc))
        {
            if (fi.get_BoundingBox(null) is not { } bb) continue;
            result.Add(new OpeningInfo(fi.UniqueId, DisplayName(fi), Mark(fi), KindOf(fi), LevelName(doc, fi.LevelId, fi.Host),
                fi.Host?.Id.Value ?? -1, M(bb.Min.Z), M(bb.Max.Z)));
        }
        foreach (var op in WallOpenings(doc))
        {
            if (op.get_BoundingBox(null) is not { } bb) continue;
            result.Add(new OpeningInfo(op.UniqueId, "Wall opening", Mark(op), OpeningKind.Opening, LevelName(doc, op.LevelId, op.Host),
                op.Host.Id.Value, M(bb.Min.Z), M(bb.Max.Z)));
        }
        return result.OrderBy(o => o.ZMin).ThenBy(o => o.Name).ThenBy(o => o.Mark).ToList();
    }

    /// <summary>Wind velocity in model axes. compassFromDeg is where the wind comes FROM (0 = N, 90 = E, clockwise,
    /// true north), so the air moves toward the opposite bearing.</summary>
    public static Vector2 WindVector(Document doc, double compassFromDeg, double speed) =>
        TrueNorthToModel(doc, compassFromDeg + 180) * (float)speed;

    /// <summary>Unit vector in model axes pointing TOWARD the true-north bearing compassDeg (0 = N, 90 = E, clockwise).
    /// The wind picker and the manual air sources share this, so "wind from N" and "fan toward N" agree on a rotated project.</summary>
    public static Vector2 TrueNorthToModel(Document doc, double compassDeg)
    {
        double th = compassDeg * Math.PI / 180;
        // True-north frame: X = east, Y = north.
        var trueDir = new XYZ(Math.Sin(th), Math.Cos(th), 0);

        // Shared coordinates are the true-north frame. ProjectLocation.GetTotalTransform() maps shared -> internal
        // coordinates (the same direction as RevitLinkInstance.GetTotalTransform maps link -> host; the documented
        // internal -> shared idiom is GetTotalTransform().Inverse.OfPoint). So OfVector takes a true-north direction into
        // model axes. This is used instead of hand-rotating by ProjectPosition.Angle, whose sign ("angle from true north",
        // -PI..PI) is easy to get backwards.
        // untested: if the in-model wind arrow points the wrong way on a rotated project, use .Inverse here.
        XYZ modelDir = trueDir;
        try
        {
            if (doc.ActiveProjectLocation is { } loc) modelDir = loc.GetTotalTransform().OfVector(trueDir);
        }
        catch (Exception) { /* no location: project north == true north */ }

        var flat = new Vector2((float)modelDir.X, (float)modelDir.Y);
        return flat.LengthSquared() < 1e-12f ? new Vector2(0, 1) : Vector2.Normalize(flat);
    }

    /// <summary>Where true north points in model axes; the UI caches it so source rows can convert without a Document.</summary>
    public static Vector2 TrueNorthInModel(Document doc) => TrueNorthToModel(doc, 0);

    /// <summary><see cref="BuildingModel.NorthDeg"/>: the angle of <see cref="TrueNorthInModel"/>, degrees clockwise from model +Y.
    /// 0 when project north is true north. ponytail: the GetTotalTransform direction (see TrueNorthToModel) is still untested on a
    /// rotated project; if the app's arrow points the wrong way, negate this.</summary>
    public static float NorthDeg(Document doc)
    {
        var n = TrueNorthInModel(doc);
        return (float)(Math.Atan2(n.X, n.Y) * 180 / Math.PI);
    }

    // ---- doors, windows, wall openings ----

    static IEnumerable<FamilyInstance> DoorsAndWindows(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
            .WherePasses(new ElementMulticategoryFilter(DoorWindowCategories))
            .Cast<FamilyInstance>();
    // Every door/window is kept: skipping one leaves its slot as a permanent hole (doors/windows are not obstacles).
    // Non-wall hosts (curtain panels, unhosted, odd hosts) are sized by bounding box.

    /// <summary>The wall an opening sits in: its host, or the host of its curtain-panel host.</summary>
    static Wall? HostWall(FamilyInstance fi) => fi.Host as Wall ?? (fi.Host as FamilyInstance)?.Host as Wall;

    // untested: OfClass(typeof(Opening)) collecting wall rectangular openings.
    static IEnumerable<DBOpening> WallOpenings(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(DBOpening)).Cast<DBOpening>().Where(o => o.Host is Wall && o.IsRectBoundary);

    static Opening? FromInstance(Document doc, FamilyInstance fi)
    {
        var wall = HostWall(fi);
        var normal = Flat(fi.FacingOrientation) ?? (wall != null ? Flat(wall.Orientation) : null) ?? XYZ.BasisY;
        float depth = wall != null ? M(wall.Width) : 0;
        var o = new Opening
        {
            Id = fi.UniqueId, Name = DisplayName(fi), Kind = KindOf(fi),
            Normal = D(normal), Depth = depth > 0 ? depth : 0.1f,   // curtain walls can report Width 0
        };

        // Size parameters only for instances hosted directly in a wall; everything else uses the bounding box below.
        double w = 0, h = 0;
        if (fi.Host is Wall)
        {
            w = Size(fi, BuiltInParameter.FAMILY_ROUGH_WIDTH_PARAM, BuiltInParameter.FAMILY_WIDTH_PARAM);
            h = Size(fi, BuiltInParameter.FAMILY_ROUGH_HEIGHT_PARAM, BuiltInParameter.FAMILY_HEIGHT_PARAM);
        }
        if (w > 0 && h > 0 && wall != null && fi.Location is LocationPoint lp)
        {
            double sill = Param(fi, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);   // 0 when missing (doors)
            // untested: assumes the hosted LocationPoint lies mid-wall (wall location line = wall centreline).
            o.Width = M(w);
            o.Height = M(h);
            o.Center = new Vector3(M(lp.Point.X), M(lp.Point.Y), M(BaseZ(doc, fi, wall) + sill + h / 2));
            return o;
        }

        // Fallback for curtain-wall doors and odd families: the bounding box measured along HandOrientation and Z.
        // untested: axis-aligned box on a skewed wall overstates width by roughly the wall depth.
        if (fi.get_BoundingBox(null) is not { } bb) return null;
        var hand = Flat(fi.HandOrientation) ?? new XYZ(-normal.Y, normal.X, 0);
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var x in new[] { bb.Min.X, bb.Max.X })
            foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
            {
                double d = x * hand.X + y * hand.Y;
                lo = Math.Min(lo, d); hi = Math.Max(hi, d);
            }
        o.Width = M(hi - lo);
        o.Height = M(bb.Max.Z - bb.Min.Z);
        o.Center = P((bb.Min + bb.Max) / 2);
        return o.Width > 0 && o.Height > 0 ? o : null;
    }

    static Opening? FromWallOpening(DBOpening op)
    {
        if (op.Host is not Wall wall || !op.IsRectBoundary) return null;
        var rect = op.BoundaryRect;
        if (rect == null || rect.Count < 2) return null;
        XYZ a = rect[0], b = rect[1];
        // untested: BoundaryRect corners lie in the wall's location plane (mid-wall for centreline walls).
        double w = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        double h = Math.Abs(b.Z - a.Z);
        if (w <= 0 || h <= 0) return null;
        return new Opening
        {
            Id = op.UniqueId, Name = "Wall opening", Kind = OpeningKind.Opening,
            Center = P((a + b) / 2), Normal = D(Flat(wall.Orientation) ?? XYZ.BasisY),
            Width = M(w), Height = M(h), Depth = M(wall.Width),
        };
    }

    /// <summary>Door FacingOrientation follows the swing, not the exterior. When exactly one side of the opening is
    /// inside a Revit room, flip Normal so it points away from that room (Core: "pointing outside when known").</summary>
    // untested: GetRoomAtPoint just above the opening's bottom edge, half a wall + 30 cm either side.
    static void OrientOutward(Document doc, Opening o)
    {
        try
        {
            float off = o.Depth / 2 + 0.3f, z = o.Center.Z - o.Height / 2 + 0.2f;
            XYZ Side(float s) => new(Ft(o.Center.X + o.Normal.X * s), Ft(o.Center.Y + o.Normal.Y * s), Ft(z));
            bool front = doc.GetRoomAtPoint(Side(off)) != null, back = doc.GetRoomAtPoint(Side(-off)) != null;
            if (front && !back) o.Normal = -o.Normal;
        }
        catch (Exception) { /* keep the family's facing */ }
    }

    static double BaseZ(Document doc, FamilyInstance fi, Wall wall)
    {
        if (doc.GetElement(fi.LevelId) is Level l) return l.ProjectElevation;
        var wallLevel = doc.GetElement(wall.LevelId) as Level;
        return (wallLevel?.ProjectElevation ?? 0) + Param(wall, BuiltInParameter.WALL_BASE_OFFSET);
    }

    /// <summary>Type rough size if > 0, else the nominal size on the instance, then on the type. Feet.</summary>
    static double Size(FamilyInstance fi, BuiltInParameter rough, BuiltInParameter nominal)
    {
        double v = Param(fi.Symbol, rough);
        if (v > 0) return v;
        v = Param(fi, nominal);
        return v > 0 ? v : Param(fi.Symbol, nominal);
    }

    static double Param(Element? e, BuiltInParameter bip) =>
        e?.get_Parameter(bip) is { HasValue: true, StorageType: StorageType.Double } p ? p.AsDouble() : 0;

    static OpeningKind KindOf(FamilyInstance fi) =>
        fi.Category?.BuiltInCategory == BuiltInCategory.OST_Doors ? OpeningKind.Door : OpeningKind.Window;

    static string DisplayName(FamilyInstance fi) => $"{fi.Symbol.FamilyName}: {fi.Symbol.Name}";

    static string Mark(Element e) => e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";

    static string LevelName(Document doc, ElementId levelId, Element? host) =>
        (doc.GetElement(levelId) as Level)?.Name ?? (host != null ? (doc.GetElement(host.LevelId) as Level)?.Name : null) ?? "";

    static XYZ? Flat(XYZ? d)
    {
        if (d == null) return null;
        var f = new XYZ(d.X, d.Y, 0);
        return f.GetLength() < 1e-9 ? null : f.Normalize();
    }

    // ---- geometry ----

    static Mesh ToMesh(string id, GeometryElement ge, BoundingBoxXYZ bb)
    {
        var verts = new List<Vector3>();
        var indices = new List<int>();
        foreach (var solid in Solids(ge)) AddSolid(solid, verts, indices);
        // ponytail: a stair or a detailed sofa can tessellate into six figures of triangles and the voxelizer only
        // needs the volume it blocks, so anything huge becomes its bounding box.
        if (indices.Count / 3 > MaxTrianglesPerElement) return BoxMesh(id, bb);
        return new Mesh { Id = id, Vertices = verts.ToArray(), Indices = indices.ToArray() };
    }

    // The corners go through bb.Transform, so a box that belongs to a rotated instance lands where the element is.
    // untested: a mirrored transform flips the triangle winding; the voxelizer only tests triangle-cell overlap, so it does not care.
    static Mesh BoxMesh(string id, BoundingBoxXYZ bb)
    {
        var transform = bb.Transform;
        var v = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var corner = new XYZ((i & 1) == 0 ? bb.Min.X : bb.Max.X, (i & 2) == 0 ? bb.Min.Y : bb.Max.Y, (i & 4) == 0 ? bb.Min.Z : bb.Max.Z);
            v[i] = P(transform != null ? transform.OfPoint(corner) : corner);
        }
        int[] idx =
        {
            0,1,3, 0,3,2,   // bottom
            4,7,5, 4,6,7,   // top
            0,5,1, 0,4,5,   // -Y
            2,3,7, 2,7,6,   // +Y
            0,2,6, 0,6,4,   // -X
            1,5,7, 1,7,3,   // +X
        };
        return new Mesh { Id = id, Vertices = v, Indices = idx };
    }

    static IEnumerable<Solid> Solids(GeometryElement ge)
    {
        foreach (var g in ge)
        {
            if (g is Solid s && s.Volume > 0) yield return s;
            else if (g is GeometryInstance gi && gi.GetInstanceGeometry() is { } inner)
                foreach (var x in Solids(inner)) yield return x;
        }
    }

    static void AddSolid(Solid solid, List<Vector3> verts, List<int> indices)
    {
        int startV = verts.Count, startI = indices.Count;
        try
        {
            var t = SolidUtils.TessellateSolidOrShell(solid, new SolidOrShellTessellationControls { LevelOfDetail = 0.2 });
            for (int c = 0; c < t.ShellComponentCount; c++)
            {
                var comp = t.GetShellComponent(c);
                int b = verts.Count;
                for (int i = 0; i < comp.VertexCount; i++) verts.Add(P(comp.GetVertex(i)));
                for (int i = 0; i < comp.TriangleCount; i++)
                {
                    var tri = comp.GetTriangle(i);
                    indices.Add(b + tri.VertexIndex0); indices.Add(b + tri.VertexIndex1); indices.Add(b + tri.VertexIndex2);
                }
            }
            return;
        }
        catch (Exception)
        {
            verts.RemoveRange(startV, verts.Count - startV);
            indices.RemoveRange(startI, indices.Count - startI);
        }

        foreach (Face face in solid.Faces)
        {
            if (face.Triangulate() is not { } m) continue;
            int b = verts.Count;
            foreach (var v in m.Vertices) verts.Add(P(v));
            for (int i = 0; i < m.NumTriangles; i++)
            {
                var tri = m.get_Triangle(i);
                indices.Add(b + (int)tri.get_Index(0)); indices.Add(b + (int)tri.get_Index(1)); indices.Add(b + (int)tri.get_Index(2));
            }
        }
    }

    static void WriteDebugJson(BuildingModel model)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "WindFlow");
            Directory.CreateDirectory(dir);
            var invalid = Path.GetInvalidFileNameChars();
            var name = new string(model.Name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
            File.WriteAllText(Path.Combine(dir, (name.Length > 0 ? name : "building") + ".json"), model.ToJson());
        }
        catch (Exception) { /* debug output only */ }
    }
}

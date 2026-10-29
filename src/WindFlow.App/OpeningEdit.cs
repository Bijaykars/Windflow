using System.Numerics;
using WindFlow.App.Import;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>Add and delete an opening by hand (a phantom detection, or a missed window). Pure model edits, so --selfcheck drives the same code the
/// Openings section does. The carve itself is the voxelizer's (Voxelizer.ApplyOpening), unchanged: an added opening is an ordinary
/// <see cref="Opening"/>, saved and solved like a detected one.</summary>
public static class OpeningEdit
{
    /// <summary>Id prefix of a hand-added opening: the row shows the Width / Height / Sill / Type editors for these.</summary>
    public const string UserPrefix = "user-open-";
    /// <summary>Id prefix of the solid that keeps a deleted opening's hole wall-like (see <see cref="Delete"/>).</summary>
    public const string FillPrefix = "fill-";
    /// <summary>RC-02: Id prefix of the record of a deleted opening's ORIGINAL fill box ("fillsrc-&lt;id&gt;"): a mesh with the box's 8 vertices and no triangles, so it is saved with the model, blocks nothing and is never carved.
    /// <see cref="RebuildFills"/> cuts the fill's solid pieces from it afresh after every edit.</summary>
    public const string FillSourcePrefix = "fillsrc-";

    public const float DefaultWidth = 1.2f, DefaultHeight = 1.2f, DefaultSill = 0.9f;
    const float MinSize = 0.2f, Margin = 0.05f, HeadClearance = 0.2f;

    /// <summary>"1.2", "0.9": metres as the size boxes show them.</summary>
    public static string Text(float v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    public static bool IsUser(Opening o) => o.Id.StartsWith(UserPrefix, StringComparison.Ordinal);
    /// <summary>Still the generated "Window N" / "Door N" name (not renamed).</summary>
    public static bool IsAutoName(Opening o) => (o.Name.StartsWith("Window ", StringComparison.Ordinal) && int.TryParse(o.Name.AsSpan(7), out _)) || (o.Name.StartsWith("Door ", StringComparison.Ordinal) && int.TryParse(o.Name.AsSpan(5), out _));

    /// <summary>A wall surface under the cursor: the point on its face and the face's horizontal unit normal, turned toward the viewer.</summary>
    public readonly record struct WallHit(Vector3 Point, Vector3 Normal);

    /// <summary>What <see cref="Delete"/> removed, so <see cref="Restore"/> can put it back.</summary>
    public sealed record Deleted(Opening Opening, int Index, Mesh? Fill);

    /// <summary>The first wall under the ray: the nearest obstacle triangle must be a near-vertical face. Null when the ray hits nothing or hits a floor, roof or slab first.</summary>
    public static WallHit? PickWall(BuildingModel model, Vector3 eye, Vector3 dir)
    {
        if (Cast(model, eye, dir, float.MaxValue, out float t, out var normal) && MathF.Abs(normal.Z) < 0.3f)
        {
            var n = Vector3.Normalize(new Vector3(normal.X, normal.Y, 0));
            if (Vector3.Dot(n, dir) > 0) n = -n;   // toward the viewer, whatever the mesh's winding
            return new WallHit(eye + dir * t, n);
        }
        return null;
    }

    /// <summary>A new window on the wall <paramref name="hit"/>: <see cref="DefaultWidth"/> x <see cref="DefaultHeight"/> m, sill <see cref="DefaultSill"/> m above the floor
    /// of the storey the point is in, centred on the point along the wall, all clamped inside the wall (shrunk when the wall is narrower or the storey lower).
    /// Depth is the wall's thickness measured through it, up to <see cref="OpeningDetect.MaxThicknessM"/> (IMP-B). Always a window, always open; Cd left at the default
    /// like a detected window. SideA / SideB stay Unknown, so the ledger classifies it by room loops exactly as it does an undetected opening
    /// (Analysis.IsExteriorOpening). Null (with the reason) when the wall has no room for 0.2 m x 0.2 m.</summary>
    public static Opening? Add(BuildingModel model, WallHit hit, out string why)
    {
        why = "";
        var n = hit.Normal;
        var t = new Vector3(-n.Y, n.X, 0);

        // Thickness: from just inside the clicked face, the next near-vertical face along -n is the far side of the wall.
        float depth = 0.2f;
        if (Cast(model, hit.Point - n * 0.005f, -n, OpeningDetect.MaxThicknessM, out float tf, out var farN) &&
            MathF.Abs(farN.Z) < 0.3f && MathF.Abs(Vector3.Dot(new Vector3(farN.X, farN.Y, 0), n)) > 0.7f)
            depth = MathF.Min(tf + 0.005f, OpeningDetect.MaxThicknessM);
        var mid = hit.Point - n * (depth * 0.5f);

        // Vertical: the storey the click is in; the head stays HeadClearance under its ceiling.
        Storey? storey = null;
        foreach (var s in model.Storeys) if (s.Elevation <= hit.Point.Z + 0.05f && (storey == null || s.Elevation > storey.Elevation)) storey = s;
        float floor = storey?.Elevation ?? model.GroundZ;
        float avail = (storey is { Height: > 0 } ? storey.Height : 3f) - HeadClearance;
        float h = MathF.Min(DefaultHeight, avail - 0.05f);
        float sill = MathF.Min(DefaultSill, avail - h);
        if (h < MinSize) { why = "the storey is too low for a window there"; return null; }

        // Horizontal: free run along the wall each side of the click, at the window's mid height, less a margin.
        float zMid = floor + sill + h * 0.5f;
        var probe = new Vector3(mid.X, mid.Y, zMid);
        float left = Free(model, probe, -t) - Margin, right = Free(model, probe, t) - Margin;
        float w = MathF.Min(DefaultWidth, left + right);
        if (w < MinSize) { why = "no room for a window there: too close to the end of the wall"; return null; }
        float off = Math.Clamp(0f, -left + w * 0.5f, right - w * 0.5f);

        // Outward normal when it can be told: the side that is inside a room is the inside.
        var centre = new Vector3(mid.X, mid.Y, zMid) + t * off;
        float reach = depth * 0.5f + 0.3f;
        if (InRoom(model, centre + n * reach) && !InRoom(model, centre - n * reach)) n = -n;

        var ids = new HashSet<string>(model.Openings.Select(o => o.Id));
        int k = 1;
        while (ids.Contains(UserPrefix + k)) k++;
        var opening = new Opening
        {
            Id = UserPrefix + k, Name = NextName(model, OpeningKind.Window), Level = storey?.Id ?? "", Kind = OpeningKind.Window, IsOpen = true,
            Center = centre, Normal = n, Width = w, Height = h, Depth = depth,
        };
        model.Openings.Add(opening);
        RebuildFills(model);
        return opening;
    }

    /// <summary>"Window 3": the kind's word and 1 + the highest number already used by a "Window N" name, so names stay unique.</summary>
    public static string NextName(BuildingModel model, OpeningKind kind)
    {
        string word = kind == OpeningKind.Door ? "Door" : "Window";
        int top = 0;
        foreach (var o in model.Openings)
            if (o.Name.StartsWith(word + " ", StringComparison.Ordinal) && int.TryParse(o.Name.AsSpan(word.Length + 1), out int v) && v > top) top = v;
        return $"{word} {top + 1}";
    }

    static bool IsFill(Mesh m) => m.Id.StartsWith(FillPrefix, StringComparison.Ordinal);

    /// <summary>True for the fill solid of the opening <paramref name="id"/>, or a piece <see cref="CarveFills"/> left of it (Id "fill-&lt;id&gt;" or "fill-&lt;id&gt;~k").</summary>
    static bool IsFillOf(Mesh m, string id) => m.Id == FillPrefix + id || m.Id.StartsWith(FillPrefix + id + "~", StringComparison.Ordinal);

    /// <summary>A new opening over the fill of a deleted one carves through it: every fill solid the opening's footprint overlaps (in the opening's own frame: along the wall, up, and through it)
    /// is replaced by up to four boxes that keep what the opening does not cover (below, above, left, right), named "fill-&lt;id&gt;~k" so Restore of the deleted opening still finds them.
    /// A fill the footprint only touches, or one on another wall (normals not within about 14 degrees), is left alone. Obstacles is replaced by a new list, never mutated.</summary>
    public static void CarveFills(BuildingModel model, Opening o)
    {
        var n0 = new Vector3(o.Normal.X, o.Normal.Y, 0);
        if (n0.LengthSquared() < 1e-8f || !model.Obstacles.Any(IsFill)) return;
        var n = Vector3.Normalize(n0);
        var t = new Vector3(-n.Y, n.X, 0);
        float hw = o.Width * 0.5f, hh = o.Height * 0.5f;
        var result = new List<Mesh>();
        bool changed = false;
        foreach (var m in model.Obstacles)
        {
            if (!IsFill(m) || m.Vertices.Length == 0) { result.Add(m); continue; }
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);       // the fill's extent in the opening's frame: x through the wall, y along it, z up
            foreach (var v in m.Vertices)
            {
                var d = v - o.Center;
                var l = new Vector3(Vector3.Dot(d, n), Vector3.Dot(d, t), d.Z);
                lo = Vector3.Min(lo, l); hi = Vector3.Max(hi, l);
            }
            // overlap with positive volume, and on this wall: the fill sits within a wall thickness of the opening's plane
            const float eps = 0.01f;
            bool overlap = lo.X < o.Depth * 0.5f + 0.3f && hi.X > -o.Depth * 0.5f - 0.3f && lo.Y < hw - eps && hi.Y > -hw + eps && lo.Z < hh - eps && hi.Z > -hh + eps;
            if (!overlap || hi.X - lo.X > 1f) { result.Add(m); continue; }      // no overlap, or not thin through the wall (a fill on a crossing wall)
            changed = true;
            int k = 1;
            void Piece(float y0, float y1, float z0, float z1)
            {
                if (y1 - y0 < 0.02f || z1 - z0 < 0.02f) return;
                var box = FrameBox(o, n, t, lo.X, hi.X, y0, y1, z0, z1);
                box.Id = m.Id + "~" + k++;
                result.Add(box);
            }
            Piece(lo.Y, hi.Y, lo.Z, MathF.Min(-hh, hi.Z));                                   // below the opening, full width
            Piece(lo.Y, hi.Y, MathF.Max(hh, lo.Z), hi.Z);                                    // above it
            float zb = MathF.Max(lo.Z, -hh), zt = MathF.Min(hi.Z, hh);
            Piece(lo.Y, MathF.Min(-hw, hi.Y), zb, zt);                                       // left of it, over its height
            Piece(MathF.Max(hw, lo.Y), hi.Y, zb, zt);                                        // right of it
        }
        if (changed) model.Obstacles = result;
    }

    /// <summary>A box in the opening's frame about its centre: x through the wall (along <paramref name="n"/>), y along it (<paramref name="t"/>), z up.</summary>
    static Mesh FrameBox(Opening o, Vector3 n, Vector3 t, float x0, float x1, float y0, float y1, float z0, float z1)
    {
        var box = SampleBuilding.Box(new Vector3(-0.5f), new Vector3(0.5f));
        for (int i = 0; i < box.Vertices.Length; i++)
        {
            var p = box.Vertices[i];
            box.Vertices[i] = o.Center + n * (x0 + (p.X + 0.5f) * (x1 - x0)) + t * (y0 + (p.Y + 0.5f) * (y1 - y0)) + new Vector3(0, 0, z0 + (p.Z + 0.5f) * (z1 - z0));
        }
        return box;
    }

    /// <summary>RC-02 / RC-09: every deleted opening's fill is its original box (<see cref="FillSourcePrefix"/>) minus every LISTED opening that overlaps it: the pieces are cut afresh from the source, so a resize, a delete or a
    /// Restore of any opening reopens or recloses exactly its own rectangle and never leaves a hole or a fill over a listed opening. A fill with no source (a model saved before this) is only carved further.</summary>
    static void RebuildFills(BuildingModel model)
    {
        var sources = model.Obstacles.Where(IsFillSource).ToList();
        if (sources.Count > 0)
        {
            var ids = sources.Select(x => x.Id[FillSourcePrefix.Length..]).ToList();
            var list = model.Obstacles.Where(m => !ids.Any(id => IsFillOf(m, id))).ToList();
            var boxIndices = SampleBuilding.Box(new Vector3(-0.5f), new Vector3(0.5f)).Indices;
            for (int i = 0; i < sources.Count; i++)
                list.Add(new Mesh { Id = FillPrefix + ids[i], Vertices = (Vector3[])sources[i].Vertices.Clone(), Indices = (int[])boxIndices.Clone() });
            model.Obstacles = list;    // a new list, never mutated (snapshots enumerate the old one)
        }
        foreach (var op in model.Openings) CarveFills(model, op);
    }

    static bool IsFillSource(Mesh m) => m.Id.StartsWith(FillSourcePrefix, StringComparison.Ordinal);

    /// <summary>The storey an opening belongs to: by its Level (a hand-added one carries the storey's Id, a Revit one may carry its Name), else the highest floor at or below its lowest edge.</summary>
    static Storey? StoreyOf(BuildingModel model, Opening o)
    {
        if (o.Level.Length > 0 && model.Storeys.FirstOrDefault(s => s.Id == o.Level || s.Name == o.Level) is { } named) return named;
        float z = o.Center.Z - o.Height * 0.5f;
        Storey? best = null;
        foreach (var s in model.Storeys) if (s.Elevation <= z + 0.05f && (best == null || s.Elevation > best.Elevation)) best = s;
        return best;
    }

    /// <summary>The opening's sill: its lowest edge above the floor of the storey it sits in (its own storey, so a sill typed near the ceiling is not read from the next floor up).</summary>
    public static float Sill(BuildingModel model, Opening o) => o.Center.Z - o.Height * 0.5f - (StoreyOf(model, o)?.Elevation ?? model.GroundZ);

    /// <summary>The most a head may rise above its floor: the storey height less <see cref="HeadClearance"/> (3 m storey when the model does not say), as <see cref="Add"/> sizes a new one.</summary>
    static float HeadRoom(BuildingModel model, Opening o) => (StoreyOf(model, o) is { Height: > 0 } st ? st.Height : 3f) - HeadClearance;

    /// <summary>Sets Sill, Width or Height, keeping the other two. Width 0.2 m up to the free run of wall about the opening's centre, Height 0.2 to 6 m and Sill 0 to 3 m with the head
    /// (sill + height) under the storey's head room; false (nothing changed) outside that.</summary>
    public static bool Set(BuildingModel model, Opening o, string field, float value)
    {
        if (!float.IsFinite(value)) return false;
        float sill = Sill(model, o), z0 = o.Center.Z - o.Height * 0.5f, head = HeadRoom(model, o);
        switch (field)
        {
            case "Width" when value is >= MinSize and <= 6f && value <= MaxWidth(model, o): o.Width = value; break;
            case "Height" when value is >= MinSize and <= 6f && sill + value <= head: o.Center = o.Center with { Z = z0 + value * 0.5f }; o.Height = value; break;   // the sill stays
            case "Sill" when value is >= 0f and <= 3f && value + o.Height <= head: o.Center = o.Center with { Z = o.Center.Z + (value - sill) }; break;
            default: return false;
        }
        RebuildFills(model);   // RC-02: the fill's pieces are cut again from its original box around every listed opening
        return true;
    }

    /// <summary>The widest the opening can get about its own centre: twice the shorter wall run to either side, measured from just past its current edges (the jamb of a real hole
    /// in the mesh is not a wall end), less <see cref="Margin"/>. 6 m when the walls give no end (a bare model).</summary>
    public static float MaxWidth(BuildingModel model, Opening o)
    {
        var n0 = new Vector3(o.Normal.X, o.Normal.Y, 0);
        if (n0.LengthSquared() < 1e-8f) return 6f;
        var n = Vector3.Normalize(n0);
        var t = new Vector3(-n.Y, n.X, 0);
        float half = o.Width * 0.5f + 0.02f;
        float left = half + Free(model, o.Center - t * half, -t), right = half + Free(model, o.Center + t * half, t);
        return MathF.Min(6f, 2 * MathF.Min(left, right) - 2 * Margin);
    }

    /// <summary>Removes the opening. Its hole then acts as wall, exactly as a closed opening does: a solid box of the opening's footprint
    /// (<see cref="FillPrefix"/> + id) is added to the obstacles, so a real hole in an imported mesh is sealed and a phantom on an intact wall is unchanged.
    /// A closed opening is filled the same way; an open one is no longer carved.</summary>
    public static Deleted Delete(BuildingModel model, Opening o)
    {
        int index = model.Openings.IndexOf(o);
        model.Openings.Remove(o);
        Mesh? fill = null;
        var n0 = new Vector3(o.Normal.X, o.Normal.Y, 0);
        if (n0.LengthSquared() > 1e-8f)
        {
            var n = Vector3.Normalize(n0);
            var t = new Vector3(-n.Y, n.X, 0);
            fill = SampleBuilding.Box(new Vector3(-0.5f), new Vector3(0.5f));
            for (int i = 0; i < fill.Vertices.Length; i++)
            {
                var p = fill.Vertices[i];   // x along the normal, y along the wall, z up: (n, t, z) is right-handed, so the winding survives
                fill.Vertices[i] = o.Center + n * (p.X * MathF.Max(o.Depth, 0.05f)) + t * (p.Y * o.Width) + new Vector3(0, 0, p.Z * o.Height);
            }
            fill.Id = FillPrefix + o.Id;
            var source = new Mesh { Id = FillSourcePrefix + o.Id, Vertices = (Vector3[])fill.Vertices.Clone() };
            model.Obstacles = [.. model.Obstacles, fill, source];    // a new list: the solver, Accurate and sweep snapshots enumerate the old one while they voxelise
            RebuildFills(model);
        }
        return new Deleted(o, index, fill);
    }

    /// <summary>Puts a deleted opening back where it was in the list, and takes its fill out.</summary>
    public static void Restore(BuildingModel model, Deleted d)
    {
        if (d.Fill != null) model.Obstacles = model.Obstacles.Where(m => !IsFillOf(m, d.Opening.Id) && m.Id != FillSourcePrefix + d.Opening.Id).ToList();   // a new list, as Delete's; the fill, the pieces a later Add carved it into, and its source box
        model.Openings.Insert(Math.Clamp(d.Index, 0, model.Openings.Count), d.Opening);
        RebuildFills(model);   // RC-09: the restored rectangle is carved out of any other fill that covers it
    }

    static bool InRoom(BuildingModel m, Vector3 q) => m.Rooms.Any(r => q.Z >= r.ZMin && q.Z <= r.ZMax && Devices.InLoops(r, q.X, q.Y));

    /// <summary>Distance along the horizontal ray to the first obstacle face, 50 m when none. A fill solid is not a wall end: the run is read through it, so a window can be put back where one was deleted.</summary>
    static float Free(BuildingModel model, Vector3 from, Vector3 dir) => Cast(model, from, dir, 50f, out float t, out _, skipFill: true) ? t : 50f;

    /// <summary>Nearest obstacle triangle along the ray (two-sided Moller-Trumbore, furniture ignored): its distance and unit geometric normal.</summary>
    static bool Cast(BuildingModel model, Vector3 o, Vector3 d, float maxT, out float best, out Vector3 normal, bool skipFill = false)
    {
        best = maxT;
        normal = default;
        bool found = false;
        foreach (var mesh in model.Obstacles)
        {
            if (mesh.Furniture || (skipFill && IsFill(mesh))) continue;
            var v = mesh.Vertices;
            var ix = mesh.Indices;
            for (int i = 0; i + 2 < ix.Length; i += 3)
            {
                var a = v[ix[i]];
                var e1 = v[ix[i + 1]] - a;
                var e2 = v[ix[i + 2]] - a;
                var p = Vector3.Cross(d, e2);
                float det = Vector3.Dot(e1, p);
                if (MathF.Abs(det) < 1e-9f) continue;
                float inv = 1f / det;
                var s = o - a;
                float u = Vector3.Dot(s, p) * inv;
                if (u < 0f || u > 1f) continue;
                var q = Vector3.Cross(s, e1);
                float w = Vector3.Dot(d, q) * inv;
                if (w < 0f || u + w > 1f) continue;
                float t = Vector3.Dot(e2, q) * inv;
                if (t <= 1e-4f || t >= best) continue;
                best = t;
                normal = Vector3.Normalize(Vector3.Cross(e1, e2));
                found = true;
            }
        }
        return found;
    }
}

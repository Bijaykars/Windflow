using System.Numerics;
using System.Text;
using WindFlow.Core;

namespace WindFlow.App.Import;

/// <summary>Writes a BuildingModel's obstacles as a minimal .gltf — one mesh per obstacle, positions and
/// indices only, buffer embedded as a base64 data URI so there is no sidecar .bin.
/// <para>This exists to test the import path against geometry whose right answer is known: export the
/// sample house, import it back, and the bounding box and storey guess have to come out the same. It is not
/// a general exporter (no materials, no normals, no scene graph) and is not offered in the UI.</para>
/// <para>glTF is Y-up by specification, so the model's Z-up coordinates are rotated on the way out — which
/// is what makes the round trip exercise <see cref="MeshImport"/>'s Y-up conversion rather than bypassing
/// it.</para></summary>
public static class GltfWriter
{
    public static void Write(BuildingModel model, string path)
    {
        var buffer = new List<byte>();
        var meshes = new StringBuilder();
        var accessors = new StringBuilder();
        var views = new StringBuilder();
        var nodes = new StringBuilder();
        int accessor = 0, view = 0, meshIndex = 0;

        foreach (var mesh in model.Obstacles)
        {
            if (mesh.Vertices.Length == 0 || mesh.Indices.Length == 0) continue;

            // ---- positions: Z-up model -> Y-up glTF, (x, y, z) -> (x, z, -y) ----
            int posOffset = buffer.Count;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var v in mesh.Vertices)
            {
                var g = new Vector3(v.X, v.Z, -v.Y);
                min = Vector3.Min(min, g);
                max = Vector3.Max(max, g);
                buffer.AddRange(BitConverter.GetBytes(g.X));
                buffer.AddRange(BitConverter.GetBytes(g.Y));
                buffer.AddRange(BitConverter.GetBytes(g.Z));
            }
            int posLength = buffer.Count - posOffset;
            Pad(buffer);

            // ---- indices as uint32 ----
            int idxOffset = buffer.Count;
            foreach (int i in mesh.Indices) buffer.AddRange(BitConverter.GetBytes((uint)i));
            int idxLength = buffer.Count - idxOffset;
            Pad(buffer);

            views.Append(view > 0 ? "," : "");
            views.Append($"{{\"buffer\":0,\"byteOffset\":{posOffset},\"byteLength\":{posLength},\"target\":34962}},");
            views.Append($"{{\"buffer\":0,\"byteOffset\":{idxOffset},\"byteLength\":{idxLength},\"target\":34963}}");
            int posView = view, idxView = view + 1;
            view += 2;

            accessors.Append(accessor > 0 ? "," : "");
            accessors.Append($"{{\"bufferView\":{posView},\"componentType\":5126,\"count\":{mesh.Vertices.Length}," +
                $"\"type\":\"VEC3\",\"min\":[{F(min.X)},{F(min.Y)},{F(min.Z)}],\"max\":[{F(max.X)},{F(max.Y)},{F(max.Z)}]}},");
            accessors.Append($"{{\"bufferView\":{idxView},\"componentType\":5125,\"count\":{mesh.Indices.Length},\"type\":\"SCALAR\"}}");
            int posAccessor = accessor, idxAccessor = accessor + 1;
            accessor += 2;

            meshes.Append(meshIndex > 0 ? "," : "");
            meshes.Append($"{{\"primitives\":[{{\"attributes\":{{\"POSITION\":{posAccessor}}},\"indices\":{idxAccessor},\"mode\":4}}]}}");
            nodes.Append(meshIndex > 0 ? "," : "");
            nodes.Append($"{{\"mesh\":{meshIndex}}}");
            meshIndex++;
        }

        string uri = "data:application/octet-stream;base64," + Convert.ToBase64String(buffer.ToArray());
        var json = new StringBuilder();
        json.Append("{\"asset\":{\"version\":\"2.0\",\"generator\":\"WindFlow.App GltfWriter\"},");
        json.Append("\"scene\":0,\"scenes\":[{\"nodes\":[");
        for (int i = 0; i < meshIndex; i++) json.Append(i > 0 ? "," : "").Append(i);
        json.Append("]}],");
        json.Append($"\"nodes\":[{nodes}],\"meshes\":[{meshes}],\"accessors\":[{accessors}],");
        json.Append($"\"bufferViews\":[{views}],\"buffers\":[{{\"byteLength\":{buffer.Count},\"uri\":\"{uri}\"}}]}}");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, json.ToString());
    }

    /// <summary>glTF requires every bufferView to start on a 4-byte boundary for its component type.</summary>
    static void Pad(List<byte> buffer)
    {
        while (buffer.Count % 4 != 0) buffer.Add(0);
    }

    static string F(float v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The model as a mesh file would carry it: every opening cut as a real hole through the boxes it
    /// crosses, open or closed (a mesh has no closed state; the hole is the rough opening), and no Opening objects
    /// left. <paramref name="extraSolids"/> are added afterwards (a mullion for the merge test). This is what makes
    /// the opening round trip a test of <see cref="OpeningDetect"/>: the detector sees holes, not metadata.
    /// <para>Only axis-aligned box obstacles (the eight-corner meshes SampleBuilding and the sandbox build) are cut,
    /// and only by openings whose normal is along X or Y; anything else is copied unchanged. Each cut is a box
    /// subtraction into at most six boxes, so the output stays watertight boxes.</para></summary>
    public static BuildingModel WithHoles(BuildingModel model, IEnumerable<(Vector3 Min, Vector3 Max)>? extraSolids = null)
    {
        var holes = new List<(Vector3 Min, Vector3 Max)>();
        foreach (var o in model.Openings)
        {
            var n = Vector3.Normalize(new Vector3(o.Normal.X, o.Normal.Y, 0));
            bool alongX = MathF.Abs(n.X) > 0.999f, alongY = MathF.Abs(n.Y) > 0.999f;
            if (!alongX && !alongY) continue;
            // Through the wall with 50 mm to spare either side, so the hole always pierces both faces.
            float halfD = o.Depth * 0.5f + 0.05f, halfW = o.Width * 0.5f, halfH = o.Height * 0.5f;
            var half = alongX ? new Vector3(halfD, halfW, halfH) : new Vector3(halfW, halfD, halfH);
            holes.Add((o.Center - half, o.Center + half));
        }

        var boxes = new List<(Vector3 Min, Vector3 Max)>();
        var copy = new BuildingModel { Name = model.Name, GroundZ = model.GroundZ, Storeys = model.Storeys };
        foreach (var mesh in model.Obstacles)
        {
            if (!IsBox(mesh, out var box)) { copy.Obstacles.Add(mesh); continue; }
            var pieces = new List<(Vector3 Min, Vector3 Max)> { box };
            foreach (var hole in holes)
                pieces = pieces.SelectMany(p => Subtract(p, hole)).ToList();
            boxes.AddRange(pieces);
        }
        if (extraSolids != null) boxes.AddRange(extraSolids);
        foreach (var (min, max) in boxes) copy.Obstacles.Add(SampleBuilding.Box(min, max));
        return copy;
    }

    static bool IsBox(Mesh mesh, out (Vector3 Min, Vector3 Max) box)
    {
        box = default;
        if (mesh.Vertices.Length != 8) return false;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in mesh.Vertices) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        foreach (var v in mesh.Vertices)
            if ((v.X != min.X && v.X != max.X) || (v.Y != min.Y && v.Y != max.Y) || (v.Z != min.Z && v.Z != max.Z)) return false;
        box = (min, max);
        return true;
    }

    /// <summary>Box a minus box b, as up to six boxes: below and above b, then the two sides in Y, then in X.</summary>
    static IEnumerable<(Vector3 Min, Vector3 Max)> Subtract((Vector3 Min, Vector3 Max) a, (Vector3 Min, Vector3 Max) b)
    {
        const float Eps = 1e-5f;
        if (b.Min.X >= a.Max.X - Eps || b.Max.X <= a.Min.X + Eps || b.Min.Y >= a.Max.Y - Eps || b.Max.Y <= a.Min.Y + Eps ||
            b.Min.Z >= a.Max.Z - Eps || b.Max.Z <= a.Min.Z + Eps)
        {
            yield return a;
            yield break;
        }
        var lo = Vector3.Max(a.Min, b.Min);
        var hi = Vector3.Min(a.Max, b.Max);
        if (a.Min.Z < lo.Z) yield return (a.Min, new Vector3(a.Max.X, a.Max.Y, lo.Z));
        if (hi.Z < a.Max.Z) yield return (new Vector3(a.Min.X, a.Min.Y, hi.Z), a.Max);
        if (a.Min.Y < lo.Y) yield return (new Vector3(a.Min.X, a.Min.Y, lo.Z), new Vector3(a.Max.X, lo.Y, hi.Z));
        if (hi.Y < a.Max.Y) yield return (new Vector3(a.Min.X, hi.Y, lo.Z), new Vector3(a.Max.X, a.Max.Y, hi.Z));
        if (a.Min.X < lo.X) yield return (new Vector3(a.Min.X, lo.Y, lo.Z), new Vector3(lo.X, hi.Y, hi.Z));
        if (hi.X < a.Max.X) yield return (new Vector3(hi.X, lo.Y, lo.Z), new Vector3(a.Max.X, hi.Y, hi.Z));
    }

    /// <summary>A second test file in a different format and a different shape: a simple box house with a
    /// pitched roof, written as OBJ in metres, Y-up (the OBJ convention). Nothing about it comes from the
    /// sample building, so it tests the importer rather than the round trip.</summary>
    public static void WriteTestHouseObj(string path)
    {
        // 8 x 6 m on plan, 2.6 m eaves, ridge at 4.4 m, 0.25 m walls, one internal partition.
        const float W = 8f, D = 6f, Eaves = 2.6f, Ridge = 4.4f, T = 0.25f;
        var v = new List<Vector3>();
        var faces = new List<int[]>();

        void Box(Vector3 lo, Vector3 hi)
        {
            int b = v.Count + 1;                       // OBJ indices are 1-based
            v.Add(new Vector3(lo.X, lo.Y, lo.Z)); v.Add(new Vector3(hi.X, lo.Y, lo.Z));
            v.Add(new Vector3(hi.X, lo.Y, hi.Z)); v.Add(new Vector3(lo.X, lo.Y, hi.Z));
            v.Add(new Vector3(lo.X, hi.Y, lo.Z)); v.Add(new Vector3(hi.X, hi.Y, lo.Z));
            v.Add(new Vector3(hi.X, hi.Y, hi.Z)); v.Add(new Vector3(lo.X, hi.Y, hi.Z));
            int[][] quads =
            [
                [b, b + 1, b + 2, b + 3], [b + 5, b + 4, b + 7, b + 6],
                [b + 4, b + 5, b + 1, b], [b + 3, b + 2, b + 6, b + 7],
                [b + 1, b + 5, b + 6, b + 2], [b + 4, b, b + 3, b + 7],
            ];
            foreach (var q in quads) faces.Add(q);
        }

        // OBJ is written Y-up: (x, height, depth). A mesh file has no "openings" — a window is a HOLE in
        // the geometry, so the walls are built as segments around one. Without holes the house is sealed,
        // nothing can ventilate it and the ACH that comes back is meaningless.
        void WallX(float z0, float z1, float holeA, float holeB)      // wall spanning X, hole in X
        {
            const float Sill = 0.9f, Head = 2.0f;
            Box(new Vector3(0, 0, z0), new Vector3(W, Sill, z1));                     // under the opening
            Box(new Vector3(0, Head, z0), new Vector3(W, Eaves, z1));                 // over it
            Box(new Vector3(0, Sill, z0), new Vector3(holeA, Head, z1));              // beside it
            Box(new Vector3(holeB, Sill, z0), new Vector3(W, Head, z1));
        }

        WallX(0, T, 2.5f, 3.7f);                                        // south wall, 1.2 m window
        WallX(D - T, D, 4.5f, 5.7f);                                    // north wall, 1.2 m window
        Box(new Vector3(0, 0, 0), new Vector3(T, Eaves, D));            // west wall
        Box(new Vector3(W - T, 0, 0), new Vector3(W, Eaves, D));        // east wall
        Box(new Vector3(0, -0.2f, 0), new Vector3(W, 0, D));            // ground slab

        // Internal partition with a doorway, so the two rooms are connected the way a real plan is.
        const float PartA = 4.5f, PartB = 4.62f, DoorA = 2.0f, DoorB = 2.9f, DoorTop = 2.1f;
        Box(new Vector3(PartA, 0, T), new Vector3(PartB, Eaves, DoorA));
        Box(new Vector3(PartA, 0, DoorB), new Vector3(PartB, Eaves, D - T));
        Box(new Vector3(PartA, DoorTop, DoorA), new Vector3(PartB, Eaves, DoorB));

        // Pitched roof: two sloping quads meeting at a ridge running along X, plus two gable triangles.
        int r = v.Count + 1;
        v.Add(new Vector3(0, Eaves, 0));       v.Add(new Vector3(W, Eaves, 0));
        v.Add(new Vector3(0, Ridge, D / 2));   v.Add(new Vector3(W, Ridge, D / 2));
        v.Add(new Vector3(0, Eaves, D));       v.Add(new Vector3(W, Eaves, D));
        faces.Add([r, r + 1, r + 3, r + 2]);            // south pitch
        faces.Add([r + 2, r + 3, r + 5, r + 4]);        // north pitch
        faces.Add([r, r + 2, r + 4]);                   // west gable
        faces.Add([r + 1, r + 5, r + 3]);               // east gable

        var text = new StringBuilder();
        text.AppendLine("# WindFlow test house: 8 x 6 m, pitched roof, two rooms joined by a doorway,");
        text.AppendLine("# one 1.2 m window in each of the south and north walls. Metres, Y-up.");
        text.AppendLine("o TestHouse");
        foreach (var p in v)
            text.AppendLine($"v {F(p.X)} {F(p.Y)} {F(p.Z)}");
        foreach (var f in faces)
            text.AppendLine("f " + string.Join(' ', f));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, text.ToString());
    }
}

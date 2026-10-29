using System.Text;
using System.Text.Json;

namespace WindFlow.App.Import;

/// <summary>B8 / S3-07: looks at a file's own header before Assimp (native, in-process: a stack overflow or a multi-GB
/// allocation there kills the app and the user's unsaved work) is allowed near it. Rejects with a plain message,
/// never throws anything but <see cref="InvalidDataException"/>.
/// <para>glTF / GLB: the JSON must not nest deeper than <see cref="MaxJsonDepth"/> (a 400 KB file of '[' overflowed Assimp's stack)
/// and no accessor may declare more than <see cref="MaxVertices"/> elements; a GLB's declared total and chunk lengths must fit the file.
/// PLY: the element counts times the smallest possible record size must fit the file, and the vertex count is capped
/// (a 200-byte header declared billions of vertices). OBJ has no header to check; its size is bounded by the file.
/// RW-09: PreTransformVertices bakes a copy of a mesh for every node that uses it, so the vertices and indices are also counted
/// over the expanded node tree (a few KB of JSON can reuse one mesh from 10 000 nodes) and capped in total.
/// RW-10 / batch 4: every buffer's declared <c>byteLength</c> must match what is actually there -- the decoded size of a base64 data URI, the length of the external
/// .bin file next to the .gltf (RX-10: longer is fine, only shorter is refused), the BIN chunk of a GLB (up to 3 bytes of alignment padding allowed) -- because Assimp reads the accessors against the declared length.
/// Not covered: formats whose headers carry no counts (OBJ, FBX, STL, 3DS, DAE).</para></summary>
public static class ImportGuard
{
    public const int MaxJsonDepth = 64;
    public const long MaxVertices = 50_000_000;
    /// <summary>RW-09: vertices and indices over every node instance of a glTF scene (what Assimp bakes), and the node visits it takes to count them.</summary>
    public const long MaxTotalVertices = 50_000_000, MaxTotalIndices = 150_000_000, MaxNodeVisits = 2_000_000;
    /// <summary>Largest glTF JSON read into memory. A GLB's JSON chunk and a .gltf with external buffers are KB to a few MB;
    /// only a .gltf with base64 buffers embedded gets near this.</summary>
    public const long MaxJsonBytes = 256L << 20;

    public static void Check(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string name = Path.GetFileName(path);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            switch (ext)
            {
                case ".gltf": CheckGltf(fs, name, Path.GetDirectoryName(Path.GetFullPath(path)) ?? ""); break;
                case ".glb": CheckGlb(fs, name); break;
                case ".ply": CheckPly(fs, name); break;
            }
        }
        catch (IOException) { }                    // unreadable: Assimp reports it in its own words
        catch (UnauthorizedAccessException) { }
    }

    static void CheckGltf(FileStream fs, string name, string dir)
    {
        if (fs.Length > MaxJsonBytes) throw Bad(name, $"the glTF JSON is {fs.Length >> 20} MB, over the {MaxJsonBytes >> 20} MB limit (use a .glb or external .bin buffers)");
        var bytes = new byte[fs.Length];
        fs.ReadExactly(bytes);
        CheckBuffers(CheckJson(bytes, name), name, dir, -1);
    }

    static void CheckGlb(FileStream fs, string name)
    {
        var head = new byte[20];
        if (fs.Length < 20 || fs.Read(head, 0, 20) < 20 || Encoding.ASCII.GetString(head, 0, 4) != "glTF")
            throw Bad(name, "it is not a valid GLB (missing 'glTF' header)");
        long declared = BitConverter.ToUInt32(head, 8), chunk = BitConverter.ToUInt32(head, 12);
        if (declared > fs.Length) throw Bad(name, $"the header declares {declared:N0} bytes but the file has {fs.Length:N0} (truncated or corrupt)");
        if (BitConverter.ToUInt32(head, 16) != 0x4E4F534A) throw Bad(name, "the first GLB chunk is not JSON");
        if (chunk > fs.Length - 20) throw Bad(name, $"the JSON chunk declares {chunk:N0} bytes but only {fs.Length - 20:N0} follow (corrupt)");
        if (chunk > MaxJsonBytes) throw Bad(name, $"the JSON chunk is {chunk >> 20} MB, over the {MaxJsonBytes >> 20} MB limit");
        var json = new byte[chunk];
        fs.ReadExactly(json);
        var buffers = CheckJson(json, name);
        // later chunks (BIN): each declared length must fit what is left
        long pos = 20 + chunk, binLength = -1;
        var h = new byte[8];
        while (pos + 8 <= declared)
        {
            fs.Position = pos;
            if (fs.Read(h, 0, 8) < 8) break;
            long len = BitConverter.ToUInt32(h, 0);
            if (len > fs.Length - pos - 8) throw Bad(name, $"a GLB chunk declares {len:N0} bytes but only {fs.Length - pos - 8:N0} follow (corrupt)");
            if (binLength < 0 && BitConverter.ToUInt32(h, 4) == 0x004E4942) binLength = len;   // the first BIN chunk is buffer 0
            pos += 8 + len;
        }
        CheckBuffers(buffers, name, null, binLength);
    }

    /// <summary>Each buffer's declared byteLength against its actual size (up to 3 bytes of 4-byte alignment padding longer is fine, shorter is not).
    /// <paramref name="dir"/> resolves a relative .bin (null for a GLB, whose uri-less buffer 0 is the BIN chunk of <paramref name="binLength"/> bytes, -1 = none).</summary>
    static void CheckBuffers(List<(string? Uri, long Length)> buffers, string name, string? dir, long binLength)
    {
        for (int i = 0; i < buffers.Count; i++)
        {
            var (uri, declared) = buffers[i];
            if (declared < 0) continue;                // no usable byteLength: nothing to compare
            long actual; string what; bool external = false;
            if (uri == null) { if (dir != null || i != 0 || binLength < 0) continue; actual = binLength; what = "BIN chunk"; }
            else if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = uri.IndexOf(',');
                if (comma < 0 || !uri.AsSpan(0, comma).Contains(";base64", StringComparison.OrdinalIgnoreCase)) continue;
                int pad = uri.EndsWith("==", StringComparison.Ordinal) ? 2 : uri.EndsWith('=') ? 1 : 0;
                actual = Math.Max(0, (uri.Length - comma - 1) / 4 * 3 - pad); what = "embedded data";
            }
            else
            {
                if (dir == null || uri.Contains("://", StringComparison.Ordinal)) continue;
                string full;
                try { full = Path.GetFullPath(Path.Combine(dir, Uri.UnescapeDataString(uri))); } catch (Exception) { continue; }
                if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue;   // outside the folder or missing: Assimp's own message
                actual = new FileInfo(full).Length; what = $"file {Path.GetFileName(full)}"; external = true;
            }
            if (actual < declared || (!external && actual - declared > 3))
                throw Bad(name, $"buffer {i} declares {declared:N0} bytes but its {what} holds {actual:N0} (corrupt or hostile)");
        }
    }

    /// <summary>Depth first (the stack-overflow case), then the accessor element counts. Returns the buffers' (uri, byteLength) for <see cref="CheckBuffers"/>.</summary>
    static List<(string? Uri, long Length)> CheckJson(byte[] bytes, string name)
    {
        var buffers = new List<(string? Uri, long Length)>();
        var span = bytes.AsMemory();
        if (span.Length >= 3 && span.Span[0] == 0xEF && span.Span[1] == 0xBB && span.Span[2] == 0xBF) span = span[3..];   // System.Text.Json refuses a BOM
        try
        {
            using var doc = JsonDocument.Parse(span, new JsonDocumentOptions { MaxDepth = MaxJsonDepth, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("accessors", out var acc) && acc.ValueKind == JsonValueKind.Array)
                foreach (var a in acc.EnumerateArray())
                    if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number &&
                        c.TryGetInt64(out long n) && n > MaxVertices)
                        throw Bad(name, $"an accessor declares {n:N0} elements, over the {MaxVertices:N0} limit");
            if (doc.RootElement.ValueKind == JsonValueKind.Object) CheckExpanded(doc.RootElement, name);
            if (Has(doc.RootElement, "buffers", out var bufs))
                foreach (var b in bufs.EnumerateArray())
                    buffers.Add((b.ValueKind == JsonValueKind.Object && b.TryGetProperty("uri", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null,
                        b.ValueKind == JsonValueKind.Object && b.TryGetProperty("byteLength", out var bl) && bl.ValueKind == JsonValueKind.Number && bl.TryGetInt64(out long len) ? len : -1));
        }
        catch (JsonException ex) when (ex.Message.Contains("depth", StringComparison.OrdinalIgnoreCase))
        {
            throw Bad(name, $"its JSON is nested deeper than {MaxJsonDepth} levels (a crafted file; real glTF is under 20)");
        }
        catch (JsonException) { }                  // not valid JSON otherwise: Assimp's own message is as good
        return buffers;
    }

    /// <summary>RW-09 / RX-06: the vertices and indices Assimp will bake, summed over every node instance Assimp imports, against <see cref="MaxTotalVertices"/> /
    /// <see cref="MaxTotalIndices"/>; stops at the first excess. Assimp calls ImportNode once per entry of <c>scenes[scene].nodes</c> (a root listed N times is baked N times)
    /// and once per child entry, so the walk starts at those entries, repeats counted; only a file with no scene falls back to the nodes nobody lists as a child.
    /// A node reached on its own ancestor path is a cycle (native recursion in Assimp) and is refused.</summary>
    static void CheckExpanded(JsonElement root, string name)
    {
        if (!Has(root, "nodes", out var nodes) || !Has(root, "meshes", out var meshes)) return;
        // (JsonElement's array indexer walks the array; lists make the lookups O(1))
        var accessorList = Has(root, "accessors", out var accessors) ? accessors.EnumerateArray().ToList() : new List<JsonElement>();
        var nodeList = nodes.EnumerateArray().ToList(); var meshList = meshes.EnumerateArray().ToList();
        long Count(int accessor) =>
            accessor >= 0 && accessor < accessorList.Count && accessorList[accessor].ValueKind == JsonValueKind.Object &&
            accessorList[accessor].TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out long n) ? Math.Max(n, 0) : 0;
        int nodeCount = nodeList.Count, meshCount = meshList.Count;
        var meshVerts = new long[meshCount]; var meshIdx = new long[meshCount];
        for (int m = 0; m < meshCount; m++)
        {
            if (!Has(meshList[m], "primitives", out var prims)) continue;
            foreach (var pr in prims.EnumerateArray())
            {
                if (pr.ValueKind != JsonValueKind.Object) continue;
                if (pr.TryGetProperty("attributes", out var at) && at.ValueKind == JsonValueKind.Object && at.TryGetProperty("POSITION", out var pos)) meshVerts[m] += Count(Index(pos, name, "a primitive's POSITION accessor"));
                if (pr.TryGetProperty("indices", out var ix)) meshIdx[m] += Count(Index(ix, name, "a primitive's indices accessor"));
            }
        }
        // The roots, in Assimp's order: the scene's node list (repeats kept), else the nodes that are nobody's child.
        var roots = new List<int>();
        int sceneIndex = 0;
        if (root.TryGetProperty("scene", out var sj)) sceneIndex = Index(sj, name, "the default scene");
        if (Has(root, "scenes", out var scenes) && sceneIndex >= 0 && sceneIndex < scenes.GetArrayLength())
        {
            var scene = scenes[sceneIndex];
            if (Has(scene, "nodes", out var sn)) foreach (var e in sn.EnumerateArray()) roots.Add(Index(e, name, "a scene's node"));
        }
        else
        {
            var isChild = new bool[nodeCount];
            for (int i = 0; i < nodeCount; i++)
                if (Has(nodeList[i], "children", out var ch))
                    foreach (var c in ch.EnumerateArray()) { int ci = Index(c, name, "a node's child"); if (ci >= 0 && ci < nodeCount) isChild[ci] = true; }
            for (int i = 0; i < nodeCount; i++) if (!isChild[i]) roots.Add(i);
        }
        var kids = new List<int>?[nodeCount];
        List<int> Kids(int i)
        {
            if (kids[i] is { } known) return known;
            var list = new List<int>();
            if (Has(nodeList[i], "children", out var ch))
                foreach (var c in ch.EnumerateArray()) { int ci = Index(c, name, "a node's child"); if (ci >= 0 && ci < nodeCount) list.Add(ci); }
            return kids[i] = list;
        }
        long verts = 0, idxs = 0, visits = 0;
        var onPath = new bool[nodeCount];
        var frames = new Stack<(int Node, List<int> Kids, int Next)>();
        void Enter(int i)
        {
            if (onPath[i]) throw Bad(name, "its scene graph loops back on itself (a node is its own ancestor)");
            if (++visits > MaxNodeVisits) throw Bad(name, $"its scene graph reaches over {MaxNodeVisits:N0} node instances (a node tree that reuses nodes, or lists a node many times)");
            var node = nodeList[i];
            if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("mesh", out var mj))
            {
                int mi = Index(mj, name, "a node's mesh");
                if (mi >= 0 && mi < meshCount)
                {
                    verts += meshVerts[mi]; idxs += meshIdx[mi];
                    if (verts > MaxTotalVertices || idxs > MaxTotalIndices)
                        throw Bad(name, $"its nodes reuse meshes so the scene expands to over {MaxTotalVertices:N0} vertices ({MaxTotalIndices:N0} indices) once every node's copy is baked");
                }
            }
            onPath[i] = true;
            frames.Push((i, node.ValueKind == JsonValueKind.Object ? Kids(i) : [], 0));
        }
        foreach (int r in roots)
        {
            if (r < 0 || r >= nodeCount) continue;
            Enter(r);
            while (frames.Count > 0)
            {
                var (n, list, next) = frames.Pop();
                if (next >= list.Count) { onPath[n] = false; continue; }
                frames.Push((n, list, next + 1));
                Enter(list[next]);
            }
        }
    }

    /// <summary>An index or accessor number: anything else (null, a string, 1.5) is a malformed file (RX-10), not a raw .NET exception.</summary>
    static int Index(JsonElement e, string name, string what)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out int v)) throw Bad(name, $"its glTF is malformed ({what} is not an integer)");
        return v;
    }

    static bool Has(JsonElement parent, string key, out JsonElement array)
    {
        array = default;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out array) && array.ValueKind == JsonValueKind.Array;
    }

    static void CheckPly(FileStream fs, string name)
    {
        var buf = new byte[Math.Min(fs.Length, 65536)];
        int got = fs.Read(buf, 0, buf.Length);
        string text = Encoding.Latin1.GetString(buf, 0, got);
        int end = text.IndexOf("end_header", StringComparison.Ordinal);
        if (!text.StartsWith("ply", StringComparison.Ordinal) || end < 0) return;   // Assimp's reader says what is wrong
        int bodyStart = text.IndexOf('\n', end) + 1;
        bool ascii = true;
        long minBody = 0, vertices = 0;
        string[] lines = text[..end].Split('\n');
        string? element = null; long count = 0; long recordMin = 0; int props = 0;
        void Close()
        {
            if (element == null) return;
            minBody += count * (ascii ? Math.Max(1, 2 * props) : recordMin);
            if (element == "vertex") vertices = count;
        }
        foreach (var raw in lines)
        {
            var w = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (w.Length == 0) continue;
            if (w[0] == "format" && w.Length > 1) ascii = w[1] == "ascii";
            else if (w[0] == "element" && w.Length >= 3)
            {
                Close();
                element = w[1]; props = 0; recordMin = 0;
                if (!long.TryParse(w[2], out count) || count < 0) throw Bad(name, $"element '{w[1]}' declares a count that is not a number");
            }
            else if (w[0] == "property" && w.Length >= 3)
            {
                props++;
                // list: the count type is always present; its items may be none. Scalar: its full size.
                recordMin += w[1] == "list" ? (w.Length > 2 ? PlySize(w[2]) : 1) : PlySize(w[1]);
            }
        }
        Close();
        if (vertices > MaxVertices) throw Bad(name, $"it declares {vertices:N0} vertices, over the {MaxVertices:N0} limit");
        long body = fs.Length - bodyStart;
        if (minBody > body)
            throw Bad(name, $"its header declares at least {minBody:N0} bytes of data but only {Math.Max(body, 0):N0} follow (corrupt or hostile)");
    }

    static int PlySize(string type) => type switch
    {
        "char" or "int8" or "uchar" or "uint8" => 1,
        "short" or "int16" or "ushort" or "uint16" => 2,
        "double" or "float64" => 8,
        _ => 4,
    };

    static InvalidDataException Bad(string name, string why) => new($"{name} was not opened: {why}.");
}

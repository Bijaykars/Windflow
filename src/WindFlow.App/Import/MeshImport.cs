using System.Numerics;
using Silk.NET.Assimp;
using WindFlow.Core;
using AiMesh = Silk.NET.Assimp.Mesh;
using CoreMesh = WindFlow.Core.Mesh;

namespace WindFlow.App.Import;

/// <summary>Plain geometry import: OBJ, glTF/GLB, FBX, STL, 3DS, DAE through Assimp. What comes back is a
/// pile of triangles and nothing else — no openings, no rooms, no storey data — because that is all a mesh
/// file carries. The dialog says so, and "Detect rooms" (see <see cref="RoomDetect"/>) is how a raw mesh
/// gets room statistics back.
/// <para>Packages: <c>Silk.NET.Assimp</c> 2.23.0 (MIT, .NET Foundation) over <c>Ultz.Native.Assimp</c> 6.0.2
/// (the Assimp binary itself, BSD-3-Clause). Both are in THIRD-PARTY-NOTICES.txt.</para></summary>
public static class MeshImport
{
    public static readonly string[] Extensions =
        [".obj", ".gltf", ".glb", ".fbx", ".stl", ".3ds", ".dae", ".ply"];

    public static bool CanImport(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Reads the file into triangles in its own units and axes. Node transforms are baked
    /// (PreTransformVertices), so a scene graph collapses to one flat list the way a "plain mesh import"
    /// wants — at the cost of losing instancing, which this app has no use for.</summary>
    public static unsafe ImportedGeometry Load(string path)
    {
        ImportGuard.Check(path);   // B8 / S3-07: refuse a hostile header before the native importer sees it
        using var api = Assimp.GetApi();
        uint flags = (uint)(PostProcessSteps.Triangulate | PostProcessSteps.PreTransformVertices |
                            PostProcessSteps.JoinIdenticalVertices | PostProcessSteps.RemoveComponent |
                            PostProcessSteps.FindDegenerates | PostProcessSteps.FindInvalidData |
                            PostProcessSteps.SortByPrimitiveType);
        var scene = api.ImportFile(path, flags);
        if (scene == null)
            throw new InvalidDataException($"Assimp could not read {Path.GetFileName(path)}: {api.GetErrorStringS()}");

        try
        {
            // Inside the try: a scene without a root still has to be released.
            if (scene->MRootNode == null)
                throw new InvalidDataException($"Assimp could not read {Path.GetFileName(path)}: {api.GetErrorStringS()}");
            var meshes = new List<CoreMesh>();
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            int triangles = 0;
            var paneIds = new HashSet<string>();
            var paneMaterials = new HashSet<uint>();
            var paneCache = new Dictionary<uint, bool>();

            for (uint m = 0; m < scene->MNumMeshes; m++)
            {
                AiMesh* mesh = scene->MMeshes[m];
                if (mesh == null || mesh->MNumVertices == 0 || mesh->MVertices == null) continue;
                // SortByPrimitiveType leaves points and lines in their own meshes; they carry no volume.
                if ((mesh->MPrimitiveTypes & (uint)PrimitiveType.Triangle) == 0) continue;

                var vertices = new Vector3[mesh->MNumVertices];
                for (uint v = 0; v < mesh->MNumVertices; v++)
                {
                    var p = mesh->MVertices[v];
                    vertices[v] = new Vector3(p.X, p.Y, p.Z);
                    min = Vector3.Min(min, vertices[v]);
                    max = Vector3.Max(max, vertices[v]);
                }

                var indices = new List<int>((int)mesh->MNumFaces * 3);
                for (uint f = 0; f < mesh->MNumFaces; f++)
                {
                    var face = mesh->MFaces[f];
                    if (face.MNumIndices != 3) continue;         // Triangulate should have handled this
                    indices.Add((int)face.MIndices[0]);
                    indices.Add((int)face.MIndices[1]);
                    indices.Add((int)face.MIndices[2]);
                }
                if (indices.Count == 0) continue;
                triangles += indices.Count / 3;
                var id = Guid.NewGuid().ToString("N");
                // Panes: PreTransformVertices has merged the meshes per material, so the material is the unit to judge.
                uint mi = mesh->MMaterialIndex;
                if (!paneCache.TryGetValue(mi, out bool isPane))
                    paneCache[mi] = isPane = mi < scene->MNumMaterials && IsPaneMaterial(scene->MMaterials[mi]);
                if (isPane) { paneIds.Add(id); paneMaterials.Add(mi); }
                meshes.Add(new CoreMesh
                {
                    Id = id,
                    Vertices = vertices,
                    Indices = indices.ToArray(),
                });
            }

            if (meshes.Count == 0) throw new InvalidDataException(
                $"{Path.GetFileName(path)} has no triangles Assimp could read (points and lines are ignored).");
            // A coordinate past float range (1e39 reads as infinity) or a NaN would voxelise into a NaN grid and
            // a blank viewport. Refuse the file and say why.
            if (!float.IsFinite(min.X) || !float.IsFinite(min.Y) || !float.IsFinite(min.Z) ||
                !float.IsFinite(max.X) || !float.IsFinite(max.Y) || !float.IsFinite(max.Z) ||
                meshes.Any(mesh => mesh.Vertices.Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))))
                throw new InvalidDataException(
                    $"{Path.GetFileName(path)} has vertex coordinates that are not finite numbers (infinite or NaN); " +
                    "fix or re-export the file.");
            return new ImportedGeometry(Path.GetFileName(path), meshes, min, max, triangles, DeclaredMetres(path, scene),
                paneIds, paneMaterials.Count, WrittenByIfcOpenShell(path), WrittenByAutodeskAtf(path));
        }
        finally { api.ReleaseImport(scene); }
    }

    // ---------------------------------------------------------------- panes

    static readonly HashSet<string> PaneWords = new(["ifcwindow", "window", "windows", "glass", "glazing", "pane", "panes"], StringComparer.Ordinal);
    static readonly System.Text.RegularExpressions.Regex NonLetters = new(@"[^\p{L}]+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    static readonly System.Text.RegularExpressions.Regex GlassInsulation =
        new(@"glass[\s_\-]*(wool|fibre|fiber)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Curtain-wall panels (IfcPlate, IfcCurtainWall, "curtain") are walls: neither the name test nor the opacity test removes them.</summary>
    static bool IsCurtainName(string name) =>
        name.Contains("curtain", StringComparison.OrdinalIgnoreCase) || name.Contains("ifcplate", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a material name with the whole word window, glass, glazing or pane (names split on non-letters, so "fibreglass" and "GFRC" do not match;
    /// "glass wool" is insulation). Doors are not matched: they stay as modelled. Curtain-wall names never match.</summary>
    public static bool IsPaneName(string name) =>
        !IsCurtainName(name) &&
        NonLetters.Split(GlassInsulation.Replace(name, " ").ToLowerInvariant()).Any(PaneWords.Contains);

    /// <summary>A pane material is transparent (opacity under 1: OBJ "d", glTF baseColor alpha) or has a window/glass name; a curtain-wall name is never one.</summary>
    static unsafe bool IsPaneMaterial(Material* mat)
    {
        if (mat == null) return false;
        string name = "";
        float opacity = 1f;
        for (uint i = 0; i < mat->MNumProperties; i++)
        {
            var prop = mat->MProperties[i];
            if (prop == null || prop->MData == null) continue;
            string key = System.Text.Encoding.UTF8.GetString(prop->MKey.Data, (int)Math.Min(prop->MKey.Length, 1024u));
            if (key == "$mat.opacity" && prop->MType == PropertyTypeInfo.Float && prop->MDataLength >= 4) opacity = *(float*)prop->MData;
            else if (key == "?mat.name" && prop->MType == PropertyTypeInfo.String && prop->MDataLength >= 4)
                name = System.Text.Encoding.UTF8.GetString(prop->MData + 4, (int)Math.Min(*(uint*)prop->MData, prop->MDataLength - 4));
        }
        return !IsCurtainName(name) && (opacity < 0.999f || IsPaneName(name));
    }

    /// <summary>IfcConvert writes its OBJ Z-up; its first line is "# File generated by IfcOpenShell ...". Read only that line.</summary>
    static bool WrittenByIfcOpenShell(string path)
    {
        if (!path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)) return false;
        try { using var r = new StreamReader(path); return (r.ReadLine() ?? "").Contains("IfcOpenShell", StringComparison.OrdinalIgnoreCase); }
        catch (IOException) { return false; }
    }

    // ---------------------------------------------------------------- units

    /// <summary>S3-05: the metres per file unit the FILE declares, 0 when it declares none. glTF/GLB are metres by specification; an FBX
    /// carries its unit in the scene metadata as "UnitScaleFactor" (centimetres per unit: 1 = cm, 100 = m, 0.1 = mm), which Assimp
    /// reports but does not apply. OBJ, STL, PLY, 3DS and DAE say nothing reliable.</summary>
    static unsafe float DeclaredMetres(string path, Silk.NET.Assimp.Scene* scene)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".gltf" or ".glb") return 1f;
        if (ext != ".fbx" || scene->MMetaData == null) return 0f;
        var md = scene->MMetaData;
        for (uint i = 0; i < md->MNumProperties; i++)
        {
            var key = md->MKeys[i];
            string name = System.Text.Encoding.UTF8.GetString(md->MKeys[i].Data, (int)Math.Min(key.Length, 1024u));
            if (name != "UnitScaleFactor") continue;
            var entry = md->MValues[i];
            double cmPerUnit = entry.MType switch
            {
                MetadataType.Double => *(double*)entry.MData,
                MetadataType.Float => *(float*)entry.MData,
                MetadataType.Int32 => *(int*)entry.MData,
                _ => 0,
            };
            return cmPerUnit > 0 && double.IsFinite(cmPerUnit) ? (float)(cmPerUnit / 100.0) : 0f;
        }
        return 0f;
    }

    /// <summary>The unit choices the dialog offers, and the metres-per-unit each one means.</summary>
    public static readonly (string Label, float Metres)[] Units =
    [
        ("metres", 1f), ("centimetres", 0.01f), ("millimetres", 0.001f), ("feet", 0.3048f), ("inches", 0.0254f),
    ];

    /// <summary>Revit's OBJ (Autodesk ATF) is feet and Z-up; its first line is "# WaveFront *.obj file (generated by Autodesk ATF)". Read only that line.</summary>
    static bool WrittenByAutodeskAtf(string path)
    {
        if (!path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)) return false;
        try { using var r = new StreamReader(path); return (r.ReadLine() ?? "").Contains("generated by Autodesk ATF", StringComparison.OrdinalIgnoreCase); }
        catch (IOException) { return false; }
    }

    /// <summary>The unit a file declares (see <see cref="DeclaredMetres"/>) as an index into <see cref="Units"/>, else -1 (nothing declared,
    /// or a unit the dialog does not offer, in which case the size guess still applies).</summary>
    public static int DeclaredUnit(ImportedGeometry g)
    {
        if (g.DeclaredMetres > 0)
            for (int i = 0; i < Units.Length; i++)
                if (MathF.Abs(Units[i].Metres / g.DeclaredMetres - 1f) < 0.02f) return i;
        return -1;
    }

    /// <summary>The unit for a loaded file: what it declares (glTF metres, FBX UnitScaleFactor), else <see cref="GuessUnit(Vector3, Vector3, bool)"/>.</summary>
    public static int GuessUnit(ImportedGeometry g) =>
        DeclaredUnit(g) is var d and >= 0 ? d : g.WrittenByAutodeskAtf ? 3 : GuessUnit(g.Min, g.Max, DefaultYUp(g.FileName));   // 3 = feet

    /// <summary>A guess at the file's units from how big the model is, because the formats that say nothing (OBJ, STL, PLY, 3DS) say nothing at all.
    /// <para>The test is the largest HORIZONTAL dimension (S3-05; with <paramref name="yUp"/> the horizontal axes are X and Z, else X and Y,
    /// so a tall tower in centimetres is not read as metres): a building is a few metres to a couple of hundred.
    /// Anything much larger is a smaller unit read as metres. Feet and inches cannot be told from metres by
    /// size alone — a 12 m house is also a plausible 12 ft shed — so they are only ever offered, never
    /// guessed.</para></summary>
    public static int GuessUnit(Vector3 min, Vector3 max, bool yUp = true)
    {
        var size = max - min;
        float span = MathF.Max(size.X, yUp ? size.Z : size.Y);
        if (span > 5000f) return 2;          // millimetres
        if (span > 500f) return 1;           // centimetres
        return 0;                            // metres
    }

    // ---------------------------------------------------------------- building

    /// <summary>Applies the dialog's choices and produces something the solver can voxelise: scaled and
    /// rotated triangles as obstacles, GroundZ at the bounding-box floor, and the storeys the user settled
    /// on. No openings and no rooms — see the class remarks.</summary>
    public static BuildingModel Build(ImportedGeometry geometry, ImportSettings settings)
    {
        var model = new BuildingModel { Name = Path.GetFileNameWithoutExtension(geometry.FileName) };
        var obstacles = new List<CoreMesh>(geometry.Meshes.Count);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        // Glass that seals a window opening hides it from the opening detector; drop it (never everything: a model that is only glass stays).
        bool dropPanes = settings.RemovePanes && geometry.PaneMeshIds.Count > 0 && geometry.PaneMeshIds.Count < geometry.Meshes.Count;
        double paneArea = 0;
        foreach (var mesh in geometry.Meshes)
        {
            if (dropPanes && geometry.PaneMeshIds.Contains(mesh.Id))
            {
                for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
                    paneArea += Vector3.Cross(mesh.Vertices[mesh.Indices[t + 1]] - mesh.Vertices[mesh.Indices[t]], mesh.Vertices[mesh.Indices[t + 2]] - mesh.Vertices[mesh.Indices[t]]).Length() * 0.5 * settings.MetresPerUnit * settings.MetresPerUnit;
                continue;
            }
            var vertices = new Vector3[mesh.Vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = Transform(mesh.Vertices[i], settings);
                min = Vector3.Min(min, vertices[i]);
                max = Vector3.Max(max, vertices[i]);
            }
            obstacles.Add(new CoreMesh { Id = mesh.Id, Vertices = vertices, Indices = mesh.Indices });
        }

        if (dropPanes)
            settings.Notes.Add($"removed {paneArea:0.#} m² of glass ({geometry.PaneMaterials} transparent material{(geometry.PaneMaterials == 1 ? "" : "s")}) so windows stay open (untick \"Remove glass panes\" to keep them)");

        // Drop the model onto z = 0 so the solver's ground plane and the camera framing behave, and so two
        // files imported one after another are comparable. A geo-referenced export (map coordinates, often hundreds of km)
        // is also centred in plan: ModelValidation refuses coordinates beyond ±1e5 m (RC-04), and floats lose precision there.
        // Models already near the origin keep their plan position, so their grid placement (and results) do not move.
        var centre = (min + max) * 0.5f;
        bool farAway = MathF.Abs(centre.X) > 1e4f || MathF.Abs(centre.Y) > 1e4f;
        var shift = new Vector3(farAway ? -centre.X : 0, farAway ? -centre.Y : 0, -min.Z);
        if (farAway)
            settings.Notes.Add($"the model sat {MathF.Sqrt(centre.X * centre.X + centre.Y * centre.Y) / 1000:0.#} km from the file's origin (map coordinates) and was moved to the origin; " +
                               "export with local coordinates if positions look rounded");
        foreach (var mesh in obstacles)
            for (int i = 0; i < mesh.Vertices.Length; i++) mesh.Vertices[i] += shift;
        max += shift;
        min += shift;

        model.Obstacles.AddRange(obstacles);
        model.GroundZ = 0;

        var storeys = settings.Storeys.Count > 0 ? settings.Storeys : [new StoreyPlan(0, max.Z - min.Z)];
        for (int i = 0; i < storeys.Count; i++)
            model.Storeys.Add(new Storey
            {
                Id = $"L{i + 1}",
                Name = $"Level {i + 1}",
                Elevation = storeys[i].Elevation,
                Height = storeys[i].Height,
                Analysed = true,
            });
        return model;
    }

    /// <summary>Unit scale, then Y-up to Z-up if asked. glTF is Y-up by specification; FBX and OBJ are
    /// Y-up by overwhelming convention (both DCC defaults), which is why the dialog defaults the toggle on
    /// for those and off for STL/DAE, where Z-up is as likely.</summary>
    static Vector3 Transform(Vector3 v, ImportSettings s)
    {
        v *= s.MetresPerUnit;
        // (x, y, z)_src -> (x, -z, y)_model keeps the model right-handed and the handedness of the footprint.
        return s.YUpToZUp ? new Vector3(v.X, -v.Z, v.Y) : v;
    }

    public static bool DefaultYUp(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".gltf" or ".glb" or ".fbx" or ".obj";

    /// <summary>D5: the up axis to start from. glTF, FBX, STL and DAE keep <see cref="DefaultYUp"/>. An OBJ says nothing, and IfcConvert writes Z-up,
    /// so both readings are tried: the one whose storeys are 2.2-6 m high (the top one may be a roof space, up to 12 m unless it is the only one) is taken; when both are, Z-up only if it stands the building
    /// clearly lower (under 0.75 of the Y-up height; a wrong axis turns a floor plan's depth into height); otherwise Y-up. <paramref name="reason"/>
    /// says why Z-up won, "" otherwise.</summary>
    public static bool GuessYUp(ImportedGeometry g, float metresPerUnit, out string reason)
    {
        reason = "";
        if (Path.GetExtension(g.FileName).ToLowerInvariant() != ".obj") return DefaultYUp(g.FileName);
        if (g.WrittenByAutodeskAtf) { reason = "the file was written by Autodesk ATF (Revit), whose OBJ is Z-up"; return false; }
        if (g.WrittenByIfcOpenShell) { reason = "the file was written by IfcOpenShell (IfcConvert), whose OBJ is Z-up"; return false; }
        (bool Plausible, float Height) Probe(bool yUp)
        {
            var plans = GuessStoreys(g.Meshes, new ImportSettings { MetresPerUnit = metresPerUnit, YUpToZUp = yUp });
            float height = (yUp ? g.Max.Y - g.Min.Y : g.Max.Z - g.Min.Z) * metresPerUnit;
            return (plans.Count > 0 && plans.SkipLast(1).All(p => p.Height is >= 2.2f and <= 6f) && plans[^1].Height <= (plans.Count == 1 ? 6f : 12f) && plans[0].Height >= 2.2f, height);   // the top one is a roof space and may be taller or lower
        }
        var (yOk, yHeight) = Probe(true);
        var (zOk, zHeight) = Probe(false);
        if (zOk && !yOk) { reason = "Y-up gives storey heights outside 2.2-6 m"; return false; }
        if (zOk && yOk && zHeight < 0.75f * yHeight)
        { reason = $"both give plausible storeys, Z-up makes the building {zHeight:0.#} m tall against {yHeight:0.#} m"; return false; }
        return true;
    }

    // ---------------------------------------------------------------- storeys

    /// <summary>S3-05 plausibility hint: a storey height outside 2.2-6 m is far more likely a wrong unit or up axis than a real storey. Empty when every height is plausible.</summary>
    public static string StoreyHeightNote(IEnumerable<StoreyPlan> storeys)
    {
        var odd = storeys.Where(s => s.Height < 2.2f || s.Height > 6f).Select(s => s.Height.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture)).ToList();
        return odd.Count == 0 ? "" : $" Storey height note: the height{(odd.Count > 1 ? "s" : "")} {string.Join(", ", odd)} m {(odd.Count > 1 ? "are" : "is")} outside the usual 2.2-6 m for a storey; check the units, the up axis and the storey list.";
    }

    /// <summary>Guesses storey elevations from horizontal slab-like triangle clusters: every UPWARD-facing triangle whose
    /// normal is within 12° of vertical contributes its area to a 100 mm histogram of its height, and the
    /// peaks that carry a real share of the footprint are floors. Faces below the lowest wall base or beyond the walls' footprint
    /// (terrain, a site surface) are left out and said so in <see cref="ImportSettings.Notes"/> (D2, D12).
    /// <para>This finds flat floor and roof planes. It cannot find a storey with no modelled slab (a
    /// single-shell massing model), and it cannot tell a floor from a large flat roof or a terrace — hence
    /// the dialog lets the user edit the result.</para></summary>
    public static List<StoreyPlan> GuessStoreys(IReadOnlyList<CoreMesh> meshes, ImportSettings settings)
    {
        const float Bin = 0.1f;
        settings.Notes.Clear();
        // D2: only UPWARD-facing horizontal faces are floor tops (a slab's soffit and a footing's underside face down). Their centroids wait here
        // until the walls' footprint and lowest base are known, so a terrain or a footing can be left out.
        var flats = new List<(int Bin, float Area, float X, float Y)>();
        var wallMin = new Vector2(float.MaxValue);
        var wallMax = new Vector2(float.MinValue);
        float wallBase = float.MaxValue, minZ = float.MaxValue, maxZ = float.MinValue;

        foreach (var mesh in meshes)
            for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
            {
                var a = Transform(mesh.Vertices[mesh.Indices[i]], settings);
                var b = Transform(mesh.Vertices[mesh.Indices[i + 1]], settings);
                var c = Transform(mesh.Vertices[mesh.Indices[i + 2]], settings);
                float lo = MathF.Min(a.Z, MathF.Min(b.Z, c.Z));
                minZ = MathF.Min(minZ, lo);
                maxZ = MathF.Max(maxZ, MathF.Max(a.Z, MathF.Max(b.Z, c.Z)));
                var cross = Vector3.Cross(b - a, c - a);
                float twice = cross.Length();
                if (twice < 1e-9f) continue;
                float nz = cross.Z / twice;
                if (MathF.Abs(nz) < 0.2f)                                // a wall face
                {
                    wallBase = MathF.Min(wallBase, lo);
                    foreach (var p in new[] { a, b, c }) { wallMin = Vector2.Min(wallMin, new Vector2(p.X, p.Y)); wallMax = Vector2.Max(wallMax, new Vector2(p.X, p.Y)); }
                    continue;
                }
                if (nz < 0.978f) continue;                               // not up, or > 12° off horizontal
                float z = (a.Z + b.Z + c.Z) / 3f;
                flats.Add(((int)MathF.Floor(z / Bin), twice * 0.5f, (a.X + b.X + c.X) / 3f, (a.Y + b.Y + c.Y) / 3f));
            }

        var plans = new List<StoreyPlan>();
        if (flats.Count == 0 || minZ > maxZ) return plans;

        // D2/D12: ground that is not part of the building: floor-facing surface below the lowest wall base (the ground under a footing) or
        // beyond the walls' footprint (a terrain or site surface) says nothing about storeys, and a large one would drown the real floors.
        bool haveWalls = wallBase != float.MaxValue;
        float margin = haveWalls ? MathF.Max(1f, 0.05f * MathF.Max(wallMax.X - wallMin.X, wallMax.Y - wallMin.Y)) : 0f;
        var area = new Dictionary<int, float>();
        float totalFlat = 0, ignoredBelow = 0, ignoredOutside = 0;
        foreach (var f in flats)
        {
            if (haveWalls && (f.Bin + 1) * Bin <= wallBase) { ignoredBelow += f.Area; continue; }
            if (haveWalls && (f.X < wallMin.X - margin || f.X > wallMax.X + margin || f.Y < wallMin.Y - margin || f.Y > wallMax.Y + margin))
            { ignoredOutside += f.Area; continue; }
            area[f.Bin] = area.GetValueOrDefault(f.Bin) + f.Area;
            totalFlat += f.Area;
        }
        if (ignoredBelow + ignoredOutside >= 0.1f * (totalFlat + ignoredBelow + ignoredOutside) && ignoredBelow + ignoredOutside > 5f)
            settings.Notes.Add($"ground/site surface ignored: {ignoredOutside + ignoredBelow:0} m² of upward-facing flat surface " +
                               $"({ignoredOutside:0} m² beyond the walls' footprint, {ignoredBelow:0} m² below the lowest wall base) was not used to guess the storeys");
        if (totalFlat <= 0) return plans;

        // A slab has to carry at least 8 % of all the horizontal area in the model to count as a floor.
        var peaks = area.Where(kv => kv.Value >= totalFlat * 0.08f)
                        .Select(kv => (Z: (kv.Key + 0.5f) * Bin - minZ, Area: kv.Value))
                        .OrderBy(p => p.Z)
                        .ToList();
        // Merge levels closer than 1.5 m (a floor and the furniture on it, a floor and a footing step) into the one with the most area: the
        // real floor, never the first (lowest) one of the band.
        var levels = new List<float>();
        for (int i = 0; i < peaks.Count;)
        {
            var best = peaks[i];
            int j = i + 1;
            for (; j < peaks.Count && peaks[j].Z - peaks[i].Z <= 1.5f; j++)
                if (peaks[j].Area > best.Area) best = peaks[j];
            levels.Add(best.Z);
            i = j;
        }
        if (levels.Count == 0 || levels[0] > 0.5f) levels.Insert(0, 0f);

        float top = maxZ - minZ;
        for (int i = 0; i < levels.Count; i++)
        {
            float next = i + 1 < levels.Count ? levels[i + 1] : top;
            float height = next - levels[i];
            if (height < 1.8f) continue;            // not a habitable storey; probably a roof plane
            plans.Add(new StoreyPlan(levels[i], height));
        }
        if (plans.Count == 0) plans.Add(new StoreyPlan(0, MathF.Max(top, 2.4f)));
        return plans;
    }
}

/// <summary>Triangles as they came out of the file, before units or axes are applied.</summary>
public sealed record ImportedGeometry(string FileName, List<CoreMesh> Meshes, Vector3 Min, Vector3 Max, int Triangles, float DeclaredMetres = 0f,
    HashSet<string>? PaneIds = null, int PaneMaterials = 0, bool WrittenByIfcOpenShell = false, bool WrittenByAutodeskAtf = false)
{
    /// <summary>Ids of the meshes whose material is transparent or named window/glass (see <see cref="MeshImport.IsPaneName"/>).</summary>
    public HashSet<string> PaneMeshIds { get; } = PaneIds ?? [];
}

/// <summary>One storey the user accepted, in metres above the model's own floor.</summary>
public readonly record struct StoreyPlan(float Elevation, float Height);

/// <summary>Everything the import dialog decides.</summary>
public sealed class ImportSettings
{
    public float MetresPerUnit = 1f;
    public bool YUpToZUp = true;
    public bool DetectRooms = true;
    /// <summary>Drop transparent / window / door / glass materials so panes do not seal the openings.</summary>
    public bool RemovePanes = true;
    public List<StoreyPlan> Storeys = new();
    /// <summary>What the last <see cref="MeshImport.GuessStoreys"/> left out (D12), for the import note.</summary>
    public List<string> Notes = new();
    /// <summary>Why the dialog started on Z-up, "" if it did not (D5).</summary>
    public string UpAxisReason = "";
}

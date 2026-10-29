using System.Diagnostics;
using System.Numerics;
using Avalonia.Controls;
using WindFlow.App.Import;
using WindFlow.App.Viewport;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>"Open model…" for plain geometry: Assimp reads the triangles, the dialog supplies everything the
/// file cannot say (units, up axis, storeys), and detection optionally puts rooms back so ACH works, and finds
/// the holes in the walls as openings (PLAN_V2 4.2, <see cref="OpeningDetect"/>) so they can be closed and
/// opened like Revit ones. The note under the panel says which openings are detected, not imported.</summary>
public partial class MainWindow
{
    /// <summary>Set when the model on screen came from a mesh file: no openings exist, so the panel and the
    /// bench both have to stop promising them.</summary>
    bool importedGeometry;
    string importNote = "";

    async Task ImportGeometry(string path)
    {
        if (!Automated && !await ConfirmLargeImport(path)) { StatusText.Text = "import cancelled"; return; }
        ImportedGeometry imported;
        try
        {
            StatusText.Text = $"reading {Path.GetFileName(path)}…";
            imported = await Task.Run(() => MeshImport.Load(path));
        }
        catch (Exception e)
        {
            StatusText.Text = "import failed: " + e.Message;
            return;
        }

        var dialog = new ImportDialog(imported);
        if (await dialog.ShowDialog<bool>(this) != true) { StatusText.Text = "import cancelled"; return; }
        ApplyImport(imported, dialog.Settings);
    }

    /// <summary>Assimp reads the whole file in-process, so a file too big for the free RAM can stall Windows or kill the app with the
    /// user's unsaved work (a 3.2 GB Revit OBJ, 8.5 GB free). Asks before reading one whose rough footprint is over the free physical memory.</summary>
    // ponytail: footprint = 4x the file size is a guess (OBJ text to floats, per-corner vertices, the managed copy), not measured; time a peak working set to tighten it.
    async Task<bool> ConfirmLargeImport(string path)
    {
        long need = new FileInfo(path).Length * 4, free = FreePhysicalMemory();
        if (free <= 0 || need < free) return true;
        static string Gb(long b) => $"{b / 1e9:0.0} GB";
        var message = new TextBlock
        {
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = $"{Path.GetFileName(path)} is {Gb(need / 4)}. Reading it may need about {Gb(need)} of memory and only {Gb(free)} is free, " +
                   "so Windows may slow to a crawl or WindFlow may close and lose unsaved work.\n\n" +
                   "Airflow needs walls, floors, roofs and openings, not detail. From Revit, export with Coarse detail and hide furniture, " +
                   "casework, planting, entourage, lighting, MEP and railings, or use a section box to export part of the building.",
        };
        message.Classes.Add("rowSub");
        var title = new TextBlock { Text = "LARGE MODEL" };
        title.Classes.Add("section");
        var cancel = new Button { Content = "Cancel", IsDefault = true, IsCancel = true };
        var open = new Button { Content = "Open anyway" };
        cancel.Classes.Add("tool"); open.Classes.Add("tool");
        var dialog = new Window
        {
            Title = "Large model", Width = 440, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = FontFamily,
            Background = this.FindResource("Panel") as Avalonia.Media.IBrush, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20), Spacing = 12,
                Children =
                {
                    title, message,
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, open } },
                },
            },
        };
        cancel.Click += (_, _) => dialog.Close(false);
        open.Click += (_, _) => dialog.Close(true);
        return await dialog.ShowDialog<bool>(this);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MemoryStatusEx { public uint Length, Load; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }

    [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    /// <summary>Free physical memory in bytes, or 0 when Windows will not say.</summary>
    static long FreePhysicalMemory()
    {
        var s = new MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref s) ? (long)s.AvailPhys : 0;
    }

    /// <summary>The note under the viewport after an import; <paramref name="detectNotes"/> (S3-04) is what the detector says about rooms and openings it dropped or could not look for.</summary>
    public static string ImportNote(int meshes, int storeys, string rooms, int openings, string detectNotes) =>
        $"Imported geometry: {meshes} meshes, {storeys} storey{(storeys == 1 ? "" : "s")}, {rooms}. " +
        (openings > 0
            ? $"{openings} opening{(openings == 1 ? " was" : "s were")} found as holes in the walls and " +
              "classified by size and sill height; each starts open and can be closed like any other. " +
              "Transparent glass panes are removed on import unless that box was unticked; a closed opaque door or an opaque pane is not a hole and is not found."
            : "No holes were found in the walls, so there is nothing to open or close and the flow " +
              "depends on the gaps the geometry leaves.") + detectNotes;

    /// <summary>The one neutral line shown under the picker; the full <see cref="ImportNote"/> is its tooltip.</summary>
    static string ShortImportNote(string rooms, IEnumerable<Opening> all)
    {
        int openings = all.Count();
        int doors = all.Count(o => o.Kind == OpeningKind.Door), windows = all.Count(o => o.Kind == OpeningKind.Window);
        return $"Imported: {rooms}, {openings} opening{(openings == 1 ? "" : "s")} ({doors} door{(doors == 1 ? "" : "s")}, {windows} window{(windows == 1 ? "" : "s")}). Hover for details.";
    }

    void ApplyImport(ImportedGeometry imported, ImportSettings settings)
    {
        var sw = Stopwatch.StartNew();
        var built = MeshImport.Build(imported, settings);
        string rooms = "no rooms", detectNotes = "";
        int openings = 0;
        if (settings.DetectRooms)
        {
            try
            {
                // A fixed cell size (IMP-B D1, was a fixed budget in S3-03): 50 mm under 400 m2 of plan, else 100 mm.
                var detected = RoomDetect.Detect(built, System.Environment.ProcessorCount - 1);
                detectNotes = detected.Notes.Count == 0 ? "" : " Detection notes: " + string.Join(" | ", detected.Notes) + ".";   // S3-04
                built.Rooms.AddRange(detected.Rooms);
                built.Openings.AddRange(detected.Openings);
                openings = detected.Openings.Count;
                rooms = detected.Rooms.Count == 0
                    ? "no enclosed regions found"
                    : $"{detected.Rooms.Count} room{(detected.Rooms.Count == 1 ? "" : "s")} detected at {detected.Dx * 1000:0} mm";
            }
            catch (Exception e) { rooms = "room detection failed: " + e.Message; }
        }

        detectNotes += MeshImport.StoreyHeightNote(settings.Storeys);   // S3-05
        // D5: always say which way was taken as up; D12: and what the storey guess left out.
        detectNotes += $" Up axis assumed: {(settings.YUpToZUp ? "Y-up (the file's Y is height)" : "Z-up (the file's Z is height)")}" +
                       (settings.YUpToZUp || settings.UpAxisReason.Length == 0 ? "" : $", because {settings.UpAxisReason}") + ".";
        if (settings.Notes.Count > 0) detectNotes += " " + string.Join(" ", settings.Notes.Select(n => n + "."));
        importedGeometry = true;
        importNote = ImportNote(built.Obstacles.Count, built.Storeys.Count, rooms, openings, detectNotes);
        ShowModel(built);
        Log.Info("import note: " + importNote);
        ShowImportNote(ShortImportNote(rooms, built.Openings), importNote);
        StatusText.Text = $"imported {imported.FileName} · {built.Obstacles.Count} meshes · {rooms} · {openings} openings · " +
                          $"{sw.ElapsedMilliseconds} ms — press Run to solve";
    }

    // ---------------------------------------------------------------- headless verification

    /// <summary>--shotmode import|importlive: writes two test files (a glTF round trip of the sample house
    /// and a hand-built OBJ), imports one, and photographs either the dialog or a live solve of the result.
    /// Both paths write a sidecar with the numbers, which is the only way to check a round trip really
    /// round-tripped.</summary>
    /// <summary>--swap-import in a live bench: take hold A, then import the test OBJ through ApplyImport while
    /// the solver runs. Afterwards the solver must be on the imported model and no hold may survive.</summary>
    async Task SwapImportStep()
    {
        if (live is not { Active: true } solver) { editNote = "swap: solver not running"; return; }
        solver.Hold(0);
        for (int i = 0; i < 100 && solver.HoldA == null && !solver.HoldWaiting; i++) await Task.Delay(50);
        await Task.Delay(300);
        PollHolds();
        string before = $"before: solver model {solver.ModelName}, hold A {(holdA != null ? "held" : solver.HoldWaiting ? "waiting" : "none")}";
        Log.Info("swap: " + before);

        string objPath = Path.Combine(Options.RepoRoot(), "out", "test-house.obj");
        Directory.CreateDirectory(Path.GetDirectoryName(objPath)!);
        GltfWriter.WriteTestHouseObj(objPath);
        var g = MeshImport.Load(objPath);
        var s = new ImportSettings
        {
            MetresPerUnit = MeshImport.Units[MeshImport.GuessUnit(g)].Metres,
            YUpToZUp = MeshImport.DefaultYUp(g.FileName),
        };
        s.Storeys = MeshImport.GuessStoreys(g.Meshes, s);
        ApplyImport(g, s);
        for (int i = 0; i < 200 && (solver.ModelName != model.Name || solver.State != LiveSolver.Mode.Running); i++)
            await Task.Delay(50);
        PollHolds();
        editNote = $"{before} · after: UI model {model.Name}, solver model {solver.ModelName} ({solver.State}, " +
                   $"{solver.Cells / 1000.0:0.#}k cells), UI holds A {(holdA == null ? "none" : "HELD")} B {(holdB == null ? "none" : "HELD")}, " +
                   $"solver holds A {(solver.HoldA == null ? "none" : "HELD")} B {(solver.HoldB == null ? "none" : "HELD")} " +
                   $"waiting {solver.HoldWaiting}, devices {model.Sources.Count}, selected {selectedDevice}";
        Log.Info("swap: " + editNote);
    }

    async Task ImportShotSequence(string path)
    {
        ApplyMode(true);
        Viewport3D.Renderer.InputEnabled = false;
        string dir = Path.Combine(Options.RepoRoot(), "out");
        Directory.CreateDirectory(dir);

        // 1. write both test files. The glTF is the sample house exported Y-up, so importing it back has a
        //    known right answer; the OBJ is independent geometry in a different format.
        string gltfPath = Path.Combine(dir, "sample-house.gltf");
        string objPath = Path.Combine(dir, "test-house.obj");
        var sample = SampleBuilding.Build();
        GltfWriter.Write(sample, gltfPath);
        GltfWriter.WriteTestHouseObj(objPath);

        var lines = new List<string>();
        var sampleBounds = MeshBounds(sample.Obstacles);
        lines.Add($"source    sample house: {sample.Obstacles.Count} meshes, bbox " +
                  $"{(sampleBounds.Max - sampleBounds.Min).X:0.00} x {(sampleBounds.Max - sampleBounds.Min).Y:0.00} x " +
                  $"{(sampleBounds.Max - sampleBounds.Min).Z:0.00} m");
        lines.Add($"written   {gltfPath} ({new FileInfo(gltfPath).Length / 1024.0:0.#} kB), " +
                  $"{objPath} ({new FileInfo(objPath).Length / 1024.0:0.#} kB)");

        // Control: the SAME detector on the sample house that was never exported. If this finds rooms and
        // the imported copy does not, the import is at fault; if neither does, the detector is.
        var control = RoomDetect.Detect(sample, System.Environment.ProcessorCount - 1);
        lines.Add($"control   un-imported sample house: {control.Rooms.Count} rooms at {control.Dx * 1000:0} mm · " +
                  string.Join(" | ", control.Notes));

        // 2. read both back and report what the importer made of them.
        foreach (string file in new[] { gltfPath, objPath })
        {
            try
            {
                var g = MeshImport.Load(file);
                var s = new ImportSettings
                {
                    MetresPerUnit = MeshImport.Units[MeshImport.GuessUnit(g)].Metres,
                    YUpToZUp = MeshImport.DefaultYUp(g.FileName),
                };
                s.Storeys = MeshImport.GuessStoreys(g.Meshes, s);
                var built = MeshImport.Build(g, s);
                var b = MeshBounds(built.Obstacles);
                lines.Add($"import    {g.FileName}: {g.Meshes.Count} meshes, {g.Triangles:N0} triangles · " +
                          $"unit {MeshImport.Units[MeshImport.GuessUnit(g)].Label} · yup {s.YUpToZUp} · " +
                          $"bbox {(b.Max - b.Min).X:0.00} x {(b.Max - b.Min).Y:0.00} x {(b.Max - b.Min).Z:0.00} m");
                lines.Add($"storeys   {g.FileName}: " + (s.Storeys.Count == 0 ? "none guessed"
                    : string.Join(" | ", s.Storeys.Select(x => $"{x.Elevation:0.00}+{x.Height:0.00} m"))));
                var det = RoomDetect.Detect(built, System.Environment.ProcessorCount - 1);
                lines.Add($"rooms     {g.FileName}: {det.Rooms.Count} detected at {det.Dx * 1000:0} mm " +
                          $"({det.Cells / 1000.0:0.#}k cells) · " + string.Join(" | ", det.Notes));
                foreach (var r in det.Rooms)
                    lines.Add($"room      {g.FileName}: {r.Name} on {r.Level}, {r.Loops[0].Length} corners, " +
                              $"{PolygonArea(r.Loops[0]):0.0} m² × {r.ZMax - r.ZMin:0.0} m");
            }
            catch (Exception e) { lines.Add($"import    {Path.GetFileName(file)} FAILED: {e.Message}"); }
        }

        // 3. the shot itself.
        string wanted = Options.ShotMode == "importlive" ? objPath : gltfPath;
        var geometry = MeshImport.Load(wanted);
        if (Options.ShotMode == "import")
        {
            var dialog = new ImportDialog(geometry);
            dialog.Show(this);
            await Task.Delay(1200);
            SaveWindowShot(dialog, path);
            dialog.Close();
            lines.Add($"shot      import dialog for {geometry.FileName}");
        }
        else
        {
            var settings = new ImportSettings
            {
                MetresPerUnit = MeshImport.Units[MeshImport.GuessUnit(geometry)].Metres,
                YUpToZUp = MeshImport.DefaultYUp(geometry.FileName),
                DetectRooms = true,
            };
            settings.Storeys = MeshImport.GuessStoreys(geometry.Meshes, settings);
            ApplyImport(geometry, settings);
            StartLive();
            bool ready = await WaitForAverage(200);
            Viewport3D.Renderer.Walls = WallMode.Ghost;
            await Task.Delay(2000);
            RefreshPanel();
            PanelScroll.Offset = new Avalonia.Vector(0, 0);
            await Task.Delay(700);
            await SaveShell(path);
            lines.Add($"live      {geometry.FileName}: {(ready ? "averaged" : "TIMED OUT")} " +
                      $"{live?.AverageSeconds:0.0}/{live?.AverageTarget:0.0} s · {live?.Cells / 1000.0:0.#}k cells @ " +
                      $"{live?.Dx * 1000:0} mm · {live?.Mlups:0} MLUPS · {Viewport3D.Renderer.Fps:0.0} fps · {live?.Trust}");
            foreach (var r in live?.Stats?.Rooms ?? [])
                lines.Add($"solved    {r.Room.Name}: {Standards.FreshText(r)} fresh-air ACH · total exchange {r.Ach:0.00} ACH · inflow {r.Inflow:0.000} · exchange {r.InternalExchange:0.000} m³/s · " +
                          $"table shows \"{Standards.RoomValue(r)}\" · {r.MeanSpeed:0.00} m/s · {r.Volume:0.0} m³");
            lines.Add($"openings  {model.Openings.Count} detected: " + string.Join(" | ", model.Openings.Select(o => o.Name)));
        }
        File.AppendAllLines(Path.ChangeExtension(path, ".txt"), lines);
        Close();
    }

    /// <summary>--shotmode mesh-openings: the sample house written as a glTF with its openings cut as real holes,
    /// imported through ApplyImport (rooms and openings detected), solved live, one detected window selected so
    /// the highlight shows, the opening list scrolled into view. The sidecar carries the round-trip table.</summary>
    async Task MeshOpeningsShotSequence(string path)
    {
        ApplyMode(true);
        Viewport3D.Renderer.InputEnabled = false;
        string dir = Path.Combine(Options.RepoRoot(), "out");
        Directory.CreateDirectory(dir);
        string gltfPath = Path.Combine(dir, "sample-house-holes.gltf");
        GltfWriter.Write(GltfWriter.WithHoles(SampleBuilding.Build()), gltfPath);

        var lines = new List<string>();
        var trip = SelfCheck.OpeningRoundTrip(null);
        lines.Add($"roundtrip {(trip.Ok ? "PASS" : "FAIL")} {trip.Summary}");
        lines.AddRange(trip.Table);

        var geometry = MeshImport.Load(gltfPath);
        var settings = new ImportSettings
        {
            MetresPerUnit = MeshImport.Units[MeshImport.GuessUnit(geometry)].Metres,
            YUpToZUp = MeshImport.DefaultYUp(geometry.FileName),
            DetectRooms = true,
        };
        settings.Storeys = MeshImport.GuessStoreys(geometry.Meshes, settings);
        ApplyImport(geometry, settings);
        StartLive();
        bool ready = await WaitForAverage(200);
        // Highlight the first detected window (the row and the quad in the viewport), as a row click does.
        int pick = model.Openings.FindIndex(o => o.Kind == OpeningKind.Window);
        Viewport3D.Renderer.SelectedRoom = -1;
        Viewport3D.Renderer.SelectedOpening = pick;
        statsVersion = -1;
        RefreshPanel();
        await Task.Delay(1500);
        OpeningListView.BringIntoView();
        await Task.Delay(700);
        await SaveShell(path);
        lines.Add($"live      {geometry.FileName}: {(ready ? "averaged" : "TIMED OUT")} {live?.AverageSeconds:0.0}/{live?.AverageTarget:0.0} s · " +
                  $"{live?.Cells / 1000.0:0.#}k cells @ {live?.Dx * 1000:0} mm · {live?.Mlups:0} MLUPS · {live?.Trust}");
        lines.Add($"selected  {(pick >= 0 ? model.Openings[pick].Name : "none")}");
        foreach (var f in live?.Stats?.Openings ?? [])
            lines.Add($"opening   {f.Opening.Name,-40} {f.Opening.Kind,-7} {(f.Opening.IsOpen ? "open" : "closed")} " +
                      $"{f.FlowRate:+0.000;-0.000;0.000} m³/s");
        foreach (var r in live?.Stats?.Rooms ?? [])
            lines.Add($"solved    {DisplayName(r.Room.Id, r.Room.Name)}: {Standards.FreshText(r)} fresh-air ACH · total exchange {r.Ach:0.00} ACH · {r.Volume:0.0} m³");
        File.WriteAllLines(Path.ChangeExtension(path, ".txt"), lines);
        Close();
    }

    static (Vector3 Min, Vector3 Max) MeshBounds(List<WindFlow.Core.Mesh> meshes)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var m in meshes)
            foreach (var v in m.Vertices) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        return (min, max);
    }

    static float PolygonArea(Vector2[] loop)
    {
        float twice = 0;
        for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
            twice += loop[j].X * loop[i].Y - loop[i].X * loop[j].Y;
        return MathF.Abs(twice) * 0.5f;
    }
}

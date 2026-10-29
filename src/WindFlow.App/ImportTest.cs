using System.Text;
using WindFlow.App.Import;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>--import-test &lt;file&gt;: the import dialog's pipeline with its default choices (declared or guessed unit, guessed up axis, both
/// remove options on, rooms detected), no window and no solve. The summary goes to stdout and out\import-test.txt.</summary>
static class ImportTest
{
    public static int Run(string path)
    {
        var sb = new StringBuilder();
        void Line(string s) { sb.AppendLine(s); Console.WriteLine(s); }
        try
        {
            var g = MeshImport.Load(path);
            int unit = MeshImport.GuessUnit(g);
            bool declared = MeshImport.DeclaredUnit(g) >= 0;
            var s = new ImportSettings { MetresPerUnit = MeshImport.Units[unit].Metres, DetectRooms = true, RemovePanes = true, RemoveComponents = true };
            s.YUpToZUp = MeshImport.GuessYUp(g, s.MetresPerUnit, out string upWhy);
            s.Storeys = MeshImport.GuessStoreys(g.Meshes, s);
            Line($"file        {g.FileName} · {g.Meshes.Count} meshes · {g.Triangles} triangles");
            Line($"unit        {MeshImport.Units[unit].Label} ({(declared ? "declared by the file" : g.WrittenByAutodeskAtf ? "Autodesk ATF" : "guessed from the size")}{(g.WrittenBySketchUp ? ", SketchUp" : "")}) · up axis {(s.YUpToZUp ? "Y-up" : "Z-up")}{(upWhy.Length > 0 ? " (" + upWhy + ")" : "")}");
            var built = MeshImport.Build(g, s);
            Line($"meshes      {g.Meshes.Count} in, {built.Obstacles.Count} kept");
            var kept = built.Obstacles.Select(m => m.Id).ToHashSet();
            foreach (var grp in g.Meshes.Where(m => !kept.Contains(m.Id)).GroupBy(m => (g.PaneMeshIds.Contains(m.Id) && !(g.MeshNames.TryGetValue(m.Id, out var nm) && MeshImport.IsComponentName(nm)) ? "pane " : "component ") + (g.MeshNames.TryGetValue(m.Id, out var n) ? n.Trim() : "(unnamed)")))
                Line($"  removed   {grp.Key} ({grp.Count()} mesh{(grp.Count() == 1 ? "" : "es")})");
            foreach (var l in s.Log.Where(l => l.StartsWith("component kept", StringComparison.Ordinal))) Line("  " + l);
            foreach (var n in s.Notes) Line("  note      " + n);
            Line($"storeys     {string.Join(" · ", built.Storeys.Select(st => $"{st.Name} z {st.Elevation:0.##} h {st.Height:0.##} m"))}");
            var d = RoomDetect.Detect(built, System.Environment.ProcessorCount - 1);
            Line($"rooms       {d.Rooms.Count} at {d.Dx * 1000:0} mm");
            foreach (var r in d.Rooms) Line($"  {r.Name} · {r.Level}");
            Line($"openings    {d.Openings.Count}: " + string.Join(", ", d.Openings.GroupBy(o => o.Kind).Select(k => $"{k.Count()} {k.Key}")));
            foreach (var o in d.Openings.OrderBy(o => o.Kind))
                Line($"  {o.Kind,-7} {o.Name} · {o.Width:0.00} x {o.Height:0.00} m · centre ({o.Center.X:0.0}, {o.Center.Y:0.0}, {o.Center.Z:0.0})");
            foreach (var n in d.Notes) Line("  detect    " + n);
        }
        catch (Exception e) { Line("FAILED " + e.Message); File.WriteAllText(OutFile(), sb.ToString()); return 1; }
        File.WriteAllText(OutFile(), sb.ToString());
        return 0;
    }

    static string OutFile()
    {
        Directory.CreateDirectory("out");
        return Path.Combine("out", "import-test.txt");
    }
}

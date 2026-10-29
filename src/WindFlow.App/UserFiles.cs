using WindFlow.Core;

namespace WindFlow.App;

/// <summary>Where the user's own files go and how a model is saved and compared (S4-02, S4-04).</summary>
public static class UserFiles
{
    /// <summary>S4-04: the folder the PDF, video and model dialogs start in: Documents\WindFlow, created on first use. Never the install
    /// folder or the working directory (Velopack replaces <c>current</c> on every update, so a report saved there would vanish).
    /// Falls back to Documents itself, then the temp folder, when the folder cannot be created. <paramref name="documents"/> is for the self-check.</summary>
    public static string DefaultFolder(string? documents = null)
    {
        string docs = documents ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(docs)) docs = Path.GetTempPath();
        string folder = Path.Combine(docs, "WindFlow");
        try { Directory.CreateDirectory(folder); return folder; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Log.Warn($"could not create {folder}: {ex.Message}; using {docs}");
            return Directory.Exists(docs) ? docs : Path.GetTempPath();
        }
    }

    /// <summary>The model as the file would hold it, minus the obstacle meshes: nothing in the app edits those (an imported mesh is
    /// loaded or built, never changed), and serialising a million triangles on every close would be slow.
    /// ponytail: a model whose only change is its meshes reads as unchanged; none of the app's edit paths touch them.</summary>
    public static string Fingerprint(BuildingModel m) => new BuildingModel
    {
        Name = m.Name, NorthDeg = m.NorthDeg, GroundZ = m.GroundZ, Storeys = m.Storeys, Obstacles = [], Openings = m.Openings, Rooms = m.Rooms,
        Sources = m.Sources, Env = m.Env, Heat = m.Heat, Contaminants = m.Contaminants,
    }.ToJson();

    /// <summary>The same serializer the headless <c>--solve</c>, <c>--export-sample</c> and "Open model…" use.</summary>
    public static void SaveModel(BuildingModel m, string path) => File.WriteAllText(path, m.ToJson());

    public static BuildingModel OpenModel(string path) => ModelValidation.Load(path, Import.ImportGuard.MaxJsonBytes);   // RA-11: size, finite numbers and indices checked before the model is used
}

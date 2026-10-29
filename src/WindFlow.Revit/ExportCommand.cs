using System.IO;          // not in implicit usings for WPF projects
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WindFlow.Core;

namespace WindFlow.Revit;

/// <summary>Extraction shared by ExportCommand and OpenInAppCommand: the whole building, every storey analysed,
/// no per-opening/per-room overrides (there is no panel left to set them -- the app is where that happens now).</summary>
static class Exporter
{
    public static BuildingModel ExtractWhole(Document doc, out int skippedRooms)
    {
        var levels = RevitExtractor.Levels(doc);
        var analysedIds = levels.Select(l => l.Id).ToHashSet();
        return RevitExtractor.ExtractBuilding(doc, analysedIds, new Dictionary<string, bool>(),
            new List<AirSource>(), includeFurniture: true, out skippedRooms);
    }

    public static string Summary(BuildingModel model) =>
        $"{model.Storeys.Count} storeys, {model.Obstacles.Count} meshes, {model.Openings.Count} openings, " +
        $"{model.Rooms.Count} rooms, {model.Sources.Count} air terminals.";

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return cleaned.Length > 0 ? cleaned : "building";
    }
}

/// <summary>Ribbon button "Export to WindFlow": extracts the whole building and writes a .windflow.json file
/// next to the Revit document, asking with a save dialog.</summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class ExportCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var doc = commandData.Application.ActiveUIDocument?.Document;
        if (doc == null) { message = "Open a Revit project first."; return Result.Failed; }

        try
        {
            var model = Exporter.ExtractWhole(doc, out _);
            var folder = string.IsNullOrEmpty(doc.PathName)
                ? Path.GetTempPath()
                : Path.GetDirectoryName(doc.PathName) is { Length: > 0 } d ? d : Path.GetTempPath();

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "WindFlow model (*.windflow.json)|*.windflow.json",
                DefaultExt = ".windflow.json",
                InitialDirectory = folder,
                FileName = Exporter.SafeFileName(model.Name) + ".windflow.json",
            };
            if (dialog.ShowDialog() != true) return Result.Cancelled;

            File.WriteAllText(dialog.FileName, model.ToJson());
            TaskDialog.Show("WindFlow export", $"{Exporter.Summary(model)}\n\nSaved to:\n{dialog.FileName}");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}

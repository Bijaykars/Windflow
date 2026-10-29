using System.Diagnostics;
using System.IO;          // not in implicit usings for WPF projects
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;

namespace WindFlow.Revit;

/// <summary>Ribbon button "Open in WindFlow": extracts the whole building to a per-user cache file and launches
/// the standalone app on it. No live link back to Revit yet -- this is a one-shot hand-off.
/// ponytail: a named-pipe delta push (Revit edit -> app re-voxelises just the changed region) is the natural
/// upgrade once the app's live solver can accept a partial model update; not built here.</summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class OpenInAppCommand : IExternalCommand
{
    const string WindFlowAppKey = "WindFlowApp=";

    public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
    {
        var doc = commandData.Application.ActiveUIDocument?.Document;
        if (doc == null) { message = "Open a Revit project first."; return Result.Failed; }

        var appPath = FindApp();
        if (appPath == null)
        {
            TaskDialog.Show("WindFlow", "The WindFlow app is not installed. Looked for it at:\n\n" +
                $"{VelopackPath()}\n\n(or a \"{WindFlowAppKey}<path>\" line in)\n{ConfigPath()}");
            return Result.Cancelled;
        }

        try
        {
            var model = Exporter.ExtractWhole(doc, out _);
            var dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "WindFlow", "export");
            Directory.CreateDirectory(dir);
            var jsonPath = Path.Combine(dir, Exporter.SafeFileName(model.Name) + ".windflow.json");
            File.WriteAllText(jsonPath, model.ToJson());

            Process.Start(new ProcessStartInfo(appPath, $"\"{jsonPath}\"") { UseShellExecute = true });
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }

    static string VelopackPath() => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "WindFlow", "current", "WindFlow.App.exe");

    static string ConfigPath() => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", "2026", "WindFlow", "windflow.config");

    /// <summary>Velopack install first, then a "WindFlowApp=&lt;path&gt;" line in the add-in's own config file, else null.</summary>
    static string? FindApp()
    {
        var velopack = VelopackPath();
        if (File.Exists(velopack)) return velopack;

        var configPath = ConfigPath();
        if (!File.Exists(configPath)) return null;
        foreach (var line in File.ReadAllLines(configPath))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(WindFlowAppKey, StringComparison.OrdinalIgnoreCase)) continue;
            var path = trimmed[WindFlowAppKey.Length..].Trim();
            if (File.Exists(path)) return path;
        }
        return null;
    }
}

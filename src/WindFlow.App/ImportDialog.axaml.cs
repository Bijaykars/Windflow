using System.Globalization;
using System.Numerics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using WindFlow.App.Import;

namespace WindFlow.App;

/// <summary>Everything a mesh file cannot tell us: its units, which way is up, where the storeys are, and
/// whether to go looking for rooms. The bounding box in metres updates as the unit and axis choices change,
/// which is the fastest way to see that a choice is wrong — a house is 6-30 m across, not 6000.</summary>
public partial class ImportDialog : Window
{
    ImportedGeometry? geometry;
    readonly ImportSettings settings = new();

    public ImportDialog()
    {
        InitializeComponent();       // the generated method: AvaloniaXamlLoader leaves x:Name fields null
        UnitBox.ItemsSource = MeshImport.Units.Select(u => u.Label).ToArray();
        UnitBox.SelectedIndex = 0;
    }

    public ImportDialog(ImportedGeometry imported) : this()
    {
        geometry = imported;
        FileText.Text = imported.FileName;
        CountsText.Text = $"{imported.Meshes.Count:N0} mesh{(imported.Meshes.Count == 1 ? "" : "es")} · " +
                          $"{imported.Triangles:N0} triangles";

        int guess = MeshImport.GuessUnit(imported);
        UnitBox.SelectedIndex = guess;
        GuessText.Text = MeshImport.DeclaredUnit(imported) >= 0 ? $"{MeshImport.Units[guess].Label}, as the file declares"   // S3-05
            : imported.WrittenByAutodeskAtf ? "from the file (Autodesk ATF / Revit: feet)"
            : guess == 0 ? "guessed from the size" : $"guessed {MeshImport.Units[guess].Label} from the size";
        YUpBox.IsChecked = MeshImport.GuessYUp(imported, MeshImport.Units[guess].Metres, out string upReason);   // D5
        settings.UpAxisReason = upReason;
        Refresh();
    }

    public ImportSettings Settings => settings;

    void OnSettingChanged(object? sender, RoutedEventArgs e) => Refresh();
    void OnSettingChanged(object? sender, SelectionChangedEventArgs e) => Refresh();

    /// <summary>Re-applies the current choices to the bounding box and the storey guess. Cheap: the storey
    /// pass walks the triangles once, and an imported building is tens of thousands of them at most.</summary>
    void Refresh()
    {
        if (geometry is not { } g) return;
        settings.MetresPerUnit = MeshImport.Units[Math.Max(UnitBox.SelectedIndex, 0)].Metres;
        settings.YUpToZUp = YUpBox.IsChecked == true;

        var (min, max) = Bounds(g, settings);
        var size = max - min;
        BoundsText.Text = $"{size.X:0.00} × {size.Y:0.00} × {size.Z:0.00} m";
        BoundsHint.Text = size.Z is > 1.8f and < 200f && size.X is > 1f and < 1000f
            ? "plausible for a building"
            : "that does not look like a building — check the units and the up axis";

        var guessed = MeshImport.GuessStoreys(g.Meshes, settings);
        StoreyHint.Text = guessed.Count switch
        {
            0 => "No slab-like surfaces found; the whole height is treated as one storey.",
            1 => "One storey found from the flat surfaces. Edit below if that is wrong.",
            _ => $"{guessed.Count} storeys found from the flat surfaces. Edit below if that is wrong.",
        };
        StoreyBox.Text = string.Join(System.Environment.NewLine,
            guessed.Select(s => $"{s.Elevation.ToString("0.##", CultureInfo.InvariantCulture)}, " +
                                $"{s.Height.ToString("0.##", CultureInfo.InvariantCulture)}"));
    }

    static (Vector3 Min, Vector3 Max) Bounds(ImportedGeometry g, ImportSettings s)
    {
        var min = g.Min * s.MetresPerUnit;
        var max = g.Max * s.MetresPerUnit;
        if (!s.YUpToZUp) return (Vector3.Min(min, max), Vector3.Max(min, max));
        // (x, y, z) -> (x, -z, y): recompute the extremes rather than rotating the corners naively.
        var a = new Vector3(min.X, -max.Z, min.Y);
        var b = new Vector3(max.X, -min.Z, max.Y);
        return (Vector3.Min(a, b), Vector3.Max(a, b));
    }

    /// <summary>One storey per line, "elevation, height" in metres. A line that does not parse is skipped
    /// rather than rejected: the user is editing a guess, not filling in a form.</summary>
    List<StoreyPlan> ParseStoreys()
    {
        var plans = new List<StoreyPlan>();
        foreach (var line in (StoreyBox.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            // S3-08: NaN and infinity are refused (the comma here separates the two fields, so it is not a decimal mark).
            if (!NumInput.TryParse(parts[0], out double elevation)) continue;
            if (!NumInput.TryParse(parts[1], out double height)) continue;
            if (height <= 0.2f) continue;
            plans.Add(new StoreyPlan((float)elevation, (float)height));
        }
        return plans;
    }

    void OnImport(object? sender, RoutedEventArgs e)
    {
        settings.MetresPerUnit = MeshImport.Units[Math.Max(UnitBox.SelectedIndex, 0)].Metres;
        settings.YUpToZUp = YUpBox.IsChecked == true;
        settings.DetectRooms = DetectBox.IsChecked == true;
        settings.RemovePanes = PaneBox.IsChecked == true;
        settings.Storeys = ParseStoreys();
        Close(true);
    }

    void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

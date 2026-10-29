using System.Text.Json;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>What the app remembers between runs: the mode a user chose, the window they sized, and the model
/// they had open. Kept in %LOCALAPPDATA%\\WindFlow so an installed copy and a dev build share nothing else.</summary>
public sealed class Settings
{
    public string Mode { get; set; } = "Basic";      // Basic is the default for a fresh install
    public double WindowWidth { get; set; } = 1680;
    public double WindowHeight { get; set; } = 940;
    public string LastModel { get; set; } = "";
    public string LastEpw { get; set; } = "";
    public bool ReopenLast { get; set; }             // off by default: a fresh launch lands on the empty state
    public string ProjectName { get; set; } = "";     // defaults for the report dialog
    public string Author { get; set; } = "";
    public bool LiveLink { get; set; }               // Advanced: listen for the Revit add-in on the named pipe
    public int GpuUse { get; set; } = 60;            // percent of the GPU the solver may keep busy (25-100)
    public int ParticleCount { get; set; }           // 0 = Auto (startup tier + auto-adjust); > 0 pins the count
    public int UltraCapHours { get; set; } = Ultra.DefaultCapHours;   // Ultra time limit: 6, 12, 20 or 24 hours
    public int HighAccuracyCapMinutes { get; set; } = HighAccuracy.DefaultCapMinutes;   // High Accuracy wall-time cap: 5, 10, 20 or 30 (HighAccuracy.SnapCapMinutes)
    public int AccurateCells { get; set; }         // Expert: Accurate-pass cell budget; 0 = auto (startup tier)
    public bool TourSeen { get; set; }             // the first-run tour was finished or skipped once (Tour.cs); replay from Settings

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Folder =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "WindFlow");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    /// <summary>Expert mode. "Advanced" is what settings files written before the rename say.</summary>
    public bool Advanced => string.Equals(Mode, "Expert", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Mode, "Advanced", StringComparison.OrdinalIgnoreCase);

    /// <summary>Automated runs (--shot, --bench, --report, turntable) read the settings but never write them back,
    /// so a screenshot run cannot flip the user's saved mode or window size.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool ReadOnly { get; set; }

    public static Settings Load(bool readOnly = false)
    {
        Settings s;
        try
        {
            s = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings()
                : new Settings();
        }
        catch
        {
            s = new Settings();   // a corrupt settings file must never stop the app from opening
        }
        s.ReadOnly = readOnly;
        return s;
    }

    public void Save()
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch
        {
            // Read-only profile or a locked file: the app works fine without persistence.
        }
    }
}

using System.Globalization;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>The discharge coefficient of a window or door row (Expert): the presets, the parsing, and the write to
/// <see cref="Opening.DischargeCoefficient"/> the calibrated opening model (RunOptions.OpeningModel) reads. Pure, so
/// --selfcheck drives the same code the row does.
/// <para>Only the sharp-edged 0.61 is the solver's own default (Core, LbmSolver.DefaultOpeningCd). The other three
/// are typical textbook-range values that this project has NOT checked against a source, so the UI calls them
/// "typical" and cites nothing.</para></summary>
public static class OpeningCd
{
    /// <summary>Core's default when an opening sets none (mirrors LbmSolver.DefaultOpeningCd, which is internal).</summary>
    public const float Default = 0.61f;

    public const float Min = 0.05f, Max = 1f;

    public static readonly (string Name, float Cd, bool Typical)[] Presets =
    [
        ("sharp-edged opening", 0.61f, false),
        ("open door", 0.65f, true),
        ("top-hung window", 0.4f, true),
        ("louvre", 0.3f, true),
    ];

    /// <summary>What the model will use for this opening.</summary>
    public static float Effective(Opening o) => o.DischargeCoefficient is float c && c > 0f ? c : Default;

    /// <summary>"0.61" / "0.4".</summary>
    public static string Text(float cd) => cd.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The preset a value matches (within 0.005), as the words after the number: "(sharp-edged opening)",
    /// "(open door, typical)", or "(custom)".</summary>
    public static string Name(float cd)
    {
        foreach (var (name, value, typical) in Presets)
            if (MathF.Abs(cd - value) < 0.005f) return typical ? $"({name}, typical)" : $"({name})";
        return "(custom)";
    }

    /// <summary>"0.4", "Cd 0.4", "0,4": a number between <see cref="Min"/> and <see cref="Max"/>; anything else is refused
    /// (a coefficient of 0 or above 1 is not an opening).</summary>
    public static bool TryParse(string? text, out float cd)
    {
        cd = 0;
        string t = (text ?? "").Trim();
        if (t.StartsWith("Cd", StringComparison.OrdinalIgnoreCase)) t = t[2..].Trim();
        if (!NumInput.TryParse(t, out double d)) return false;       // S3-08: one separator, finite
        float v = (float)d;
        if (v < Min || v > Max) return false;
        cd = v;
        return true;
    }

    /// <summary>Writes the coefficient (the default is stored as "none", so a saved model stays clean). True when it
    /// changed what the model will use, which is a flow-changing edit.</summary>
    public static bool Apply(Opening o, float cd)
    {
        float before = Effective(o);
        o.DischargeCoefficient = MathF.Abs(cd - Default) < 1e-4f ? null : cd;
        return MathF.Abs(Effective(o) - before) > 1e-6f;
    }

    /// <summary>Windows and doors carry a coefficient; a plain vent opening is left alone.</summary>
    public static bool Editable(Opening o) => o.Kind is OpeningKind.Window or OpeningKind.Door;
}

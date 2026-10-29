using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace WindFlow.App;

/// <summary>The one parser for every number a user types into a box (S3-08). '.' or ',' is the decimal separator,
/// a string with two or more separators ("1,2,3", "1,000.5") is refused rather than read as a thousands group, and
/// NaN / infinity never get through. A refused box keeps its last valid value and is marked with the "invalid"
/// class (red border, App.axaml) and a tooltip that names the range. Pure apart from <see cref="Mark"/>, so
/// --selfcheck drives the same parser.</summary>
public static class NumInput
{
    /// <summary>"4.5" and "4,5" both give 4.5; null when the text is not one finite number.</summary>
    public static bool TryParse(string? text, out double v)
    {
        v = 0;
        string t = (text ?? "").Trim();
        if (t.Length == 0) return false;
        int separators = 0;
        foreach (char c in t) if (c is '.' or ',') separators++;
        if (separators > 1 || IsAmbiguousThousands(t)) return false;
        if (!double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
            !double.IsFinite(parsed)) return false;
        v = parsed;
        return true;
    }

    /// <summary>"1,000" and "12,500": one comma and exactly three digits after it, one to three before. A German user means 1.0 and 12.5, an
    /// English one 1000 and 12500; either reading stored silently would be wrong by a factor of 1000, so it is refused (RV-10).</summary>
    public static bool IsAmbiguousThousands(string? text)
    {
        string t = (text ?? "").Trim().TrimStart('+', '-');
        int comma = t.IndexOf(',');
        if (comma < 1 || comma > 3 || t.Length != comma + 4 || t.IndexOf(',', comma + 1) >= 0) return false;
        for (int i = 0; i < t.Length; i++) if (i != comma && !char.IsAsciiDigit(t[i])) return false;
        return true;
    }

    /// <summary>As <see cref="TryParse(string?, out double)"/>, and the value must lie in [min, max].</summary>
    public static bool TryParse(string? text, double min, double max, out double v)
    {
        if (TryParse(text, out v) && v >= min && v <= max) return true;
        v = 0;
        return false;
    }

    /// <summary>Any angle into [0, 360).</summary>
    public static double WrapDegrees(double degrees)
    {
        double d = degrees % 360.0;
        if (d < 0) d += 360.0;
        return d == 0.0 || d >= 360.0 ? 0.0 : d;     // also turns -0 into 0
    }

    /// <summary>Parses <paramref name="box"/> against [min, max] and marks it invalid (or clears the mark). The caller
    /// keeps its last valid value when this returns false.</summary>
    public static bool Check(TextBox box, string what, double min, double max, string unit, out double v)
    {
        bool ok = TryParse(box.Text, min, max, out v);
        Mark(box, ok ? null : IsAmbiguousThousands(box.Text)
            ? $"{what}: \"{box.Text!.Trim()}\" could mean a thousand or a decimal: use 1000 or 1.0. The last valid value is kept."
            : $"{what}: type a number from {min:0.##} to {max:0.##}{unit}. The last valid value is kept.");
        return ok;
    }

    static readonly ConditionalWeakTable<Control, StrongBox<object?>> originalTips = new();

    /// <summary>A problem text marks the control invalid (class "invalid" plus that tooltip); null clears the mark and
    /// puts the control's own tooltip back.</summary>
    public static void Mark(Control box, string? problem)
    {
        if (problem != null)
        {
            if (!originalTips.TryGetValue(box, out _)) originalTips.Add(box, new StrongBox<object?>(ToolTip.GetTip(box)));
            ToolTip.SetTip(box, problem);
            if (!box.Classes.Contains("invalid")) box.Classes.Add("invalid");
        }
        else if (originalTips.TryGetValue(box, out var original))
        {
            ToolTip.SetTip(box, original.Value);
            originalTips.Remove(box);
            box.Classes.Remove("invalid");
        }
    }
}

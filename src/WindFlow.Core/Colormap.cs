namespace WindFlow.Core;

/// <summary>The one colour scale every view uses, so the plan image, the section and the Revit view agree.</summary>
public static class Colormap
{
    // Speed: dark blue (still) -> blue -> cyan -> yellow -> red (outdoor wind speed and above)
    static readonly (byte R, byte G, byte B)[] SpeedStops =
    {
        (0, 0, 130), (0, 90, 255), (0, 220, 220), (250, 230, 0), (220, 30, 0),
    };

    // Pressure: blue (suction) -> white (far-field) -> red (positive)
    static readonly (byte R, byte G, byte B)[] PressureStops =
    {
        (30, 60, 200), (245, 245, 245), (200, 30, 30),
    };

    // Temperature: cold blue -> white -> warm red, diverging about the centre of the range.
    static readonly (byte R, byte G, byte B)[] TemperatureStops =
    {
        (20, 70, 190), (120, 175, 240), (248, 248, 245), (245, 165, 90), (190, 25, 20),
    };

    // CO2: white (outdoor) -> yellow -> orange -> purple (stuffy).
    static readonly (byte R, byte G, byte B)[] Co2Stops =
    {
        (252, 252, 248), (250, 235, 130), (245, 170, 50), (205, 85, 65), (110, 35, 140),
    };

    // Mean age of air: light -> dark teal (old air).
    static readonly (byte R, byte G, byte B)[] AgeStops =
    {
        (232, 249, 247), (150, 222, 216), (45, 162, 160), (15, 96, 106), (8, 45, 60),
    };

    // Contaminant: white -> magenta -> deep violet.
    static readonly (byte R, byte G, byte B)[] ContaminantStops =
    {
        (252, 252, 250), (248, 190, 225), (230, 90, 190), (160, 20, 130), (70, 5, 70),
    };

    // PPD: near-white (5 % best case) -> yellow -> red.
    static readonly (byte R, byte G, byte B)[] PpdStops =
    {
        (240, 248, 240), (214, 234, 160), (250, 220, 110), (240, 150, 60), (188, 28, 28),
    };

    // Draught rate: white (still) -> blue -> deep blue (cold draught).
    static readonly (byte R, byte G, byte B)[] DraughtStops =
    {
        (248, 250, 252), (185, 215, 240), (95, 160, 220), (35, 90, 180), (15, 35, 110),
    };

    // Q-criterion: paper -> amber -> dark red (strong vortex core).
    static readonly (byte R, byte G, byte B)[] QStops =
    {
        (250, 248, 242), (250, 205, 120), (240, 140, 45), (190, 60, 20), (90, 15, 10),
    };

    // PMV: blue (cold) through a green comfort band at |PMV| <= 0.5 to red (warm). The stop positions are
    // explicit so the band edges land exactly on ±0.5 of the −3..+3 scale.
    static readonly (float P, byte R, byte G, byte B)[] PmvStops =
    {
        (0f, 25, 45, 150), (0.25f, 70, 135, 230), (0.40f, 175, 215, 240),
        (0.4167f, 70, 180, 100), (0.5833f, 70, 180, 100),
        (0.60f, 250, 225, 155), (0.78f, 240, 140, 55), (1f, 185, 25, 20),
    };

    /// <summary>t in [0,1] = speed / ColorMax, clamped.</summary>
    public static (byte R, byte G, byte B) Speed(float t) => Lerp(SpeedStops, t);

    /// <summary>t in [0,1] = 0.5 + 0.5 * p / pMax, clamped.</summary>
    public static (byte R, byte G, byte B) Pressure(float t) => Lerp(PressureStops, t);

    /// <summary>Generic blue → white → red diverging ramp (same stops as Pressure): t = 0.5 + 0.5 * v / vMax.
    /// Used for any signed quantity, e.g. a B − A difference image.</summary>
    public static (byte R, byte G, byte B) Diverging(float t) => Lerp(PressureStops, t);

    /// <summary>t in [0,1] over the temperature range, 0.5 = the centre (outdoor temperature).</summary>
    public static (byte R, byte G, byte B) Temperature(float t) => Lerp(TemperatureStops, t);

    /// <summary>t in [0,1] = (ppm − outdoor) / (max − outdoor), clamped.</summary>
    public static (byte R, byte G, byte B) Co2(float t) => Lerp(Co2Stops, t);

    /// <summary>t in [0,1] = age / ageMax, clamped.</summary>
    public static (byte R, byte G, byte B) Age(float t) => Lerp(AgeStops, t);

    /// <summary>t in [0,1]; callers use sqrt(c / cMax) so low concentrations stay readable.</summary>
    public static (byte R, byte G, byte B) Contaminant(float t) => Lerp(ContaminantStops, t);

    /// <summary>t in [0,1] = (pmv + 3) / 6, clamped. Green over the |PMV| ≤ 0.5 comfort band.</summary>
    public static (byte R, byte G, byte B) Pmv(float t) => LerpAt(PmvStops, t);

    /// <summary>t in [0,1] = (ppd − 5) / 95, clamped.</summary>
    public static (byte R, byte G, byte B) Ppd(float t) => Lerp(PpdStops, t);

    /// <summary>t in [0,1] = dr / 40 %, clamped.</summary>
    public static (byte R, byte G, byte B) DraughtRate(float t) => Lerp(DraughtStops, t);

    /// <summary>t in [0,1] = q / qMax over positive Q only, clamped.</summary>
    public static (byte R, byte G, byte B) Q(float t) => Lerp(QStops, t);

    static (byte R, byte G, byte B) LerpAt((float P, byte R, byte G, byte B)[] stops, float t)
    {
        t = Math.Clamp(float.IsNaN(t) ? 0 : t, 0f, 1f);
        int i = 0;
        while (i < stops.Length - 2 && t > stops[i + 1].P) i++;
        var a = stops[i]; var b = stops[i + 1];
        float span = b.P - a.P;
        float f = span <= 1e-6f ? 0f : Math.Clamp((t - a.P) / span, 0f, 1f);
        return ((byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f));
    }

    static (byte R, byte G, byte B) Lerp((byte R, byte G, byte B)[] stops, float t)
    {
        t = Math.Clamp(float.IsNaN(t) ? 0 : t, 0f, 1f);
        float s = t * (stops.Length - 1);
        int i = Math.Min((int)MathF.Floor(s), stops.Length - 2);
        float f = s - i;
        var a = stops[i]; var b = stops[i + 1];
        return ((byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f));
    }
}

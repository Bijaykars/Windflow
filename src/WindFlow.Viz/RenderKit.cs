using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindFlow.Core;

namespace WindFlow.Viz;

/// <summary>Maps world coordinates of one image area to pixels. u = horizontal world axis (right),
/// v = vertical world axis (up). Plan: u = X, v = Y. Section: u = distance along the cut, v = Z.</summary>
internal sealed class Frame
{
    public double Ppm;
    public double UMin, VMax;
    public double X0, Y0;       // top-left pixel of the image area
    public int W, H;            // image area size, pixels

    public double UMax => UMin + W / Ppm;
    public double VMin => VMax - H / Ppm;
    public Rect Area => new(X0, Y0, W, H);

    public Point Px(double u, double v) => new(X0 + (u - UMin) * Ppm, Y0 + (VMax - v) * Ppm);
    public double Px(double metres) => metres * Ppm;
    public double U(double px) => UMin + px / Ppm;
    public double V(double py) => VMax - py / Ppm;

    public static Frame Create(double ppm, double uMin, double uMax, double vMin, double vMax, double x0, double y0)
    {
        var f = new Frame { Ppm = ppm, UMin = uMin, X0 = x0, Y0 = y0 };
        f.W = Math.Max(16, (int)Math.Ceiling((uMax - uMin) * ppm));
        f.H = Math.Max(16, (int)Math.Ceiling((vMax - vMin) * ppm));
        f.VMax = vMin + f.H / ppm;      // grow upwards so the requested range always fits
        return f;
    }
}

/// <summary>Everything one scalar view needs: where the numbers come from, how they map to colour, and how
/// the legend reads. One per rendered image, built by <see cref="For"/>; the plan, the section, the
/// difference image and the legend all go through it so they can never disagree.</summary>
internal sealed class FieldScale
{
    public FieldKind Kind;
    public string Label = "", Unit = "";
    public string Note = "";                        // extra line under the legend bar
    public float Lo, Hi;
    public bool Sqrt;                               // colour ∝ sqrt(t): low concentrations stay readable
    public bool Available = true;                   // false: the source array is empty -> "no data" panel
    public string Message = "";
    public string TickFormat = "0.0";
    public Func<float, (byte R, byte G, byte B)> Ramp = Colormap.Speed;
    public Func<Vector3, float> Sample = _ => float.NaN;
    public float[] Levels = [];                     // fixed contour levels (empty = 8 auto levels)
    public float? Mark;                             // value drawn as a line across the legend bar (DR 20 %)
    public (float Lo, float Hi)? Band;              // bracketed value band (PMV ±0.5)

    /// <summary>Value -> position along the ramp, 0..1 (NaN stays NaN so the pixel drops out).</summary>
    public float Norm(float v)
    {
        if (float.IsNaN(v)) return float.NaN;
        float t = Math.Clamp((v - Lo) / MathF.Max(1e-9f, Hi - Lo), 0f, 1f);
        return Sqrt ? MathF.Sqrt(t) : t;
    }

    /// <summary>Inverse of Norm, for legend ticks placed evenly along the bar.</summary>
    public float ValueAt(double t) => Lo + (Hi - Lo) * (float)(Sqrt ? t * t : t);

    public string Tick(float v) => v.ToString(TickFormat, CultureInfo.InvariantCulture)
        + (Unit.Length > 0 ? " " + Unit : "");

    /// <param name="layerZ">plan cut height, for a per-layer pressure range; null = whole grid</param>
    /// <param name="snap">when set, Speed and Temperature come from this snapshot instead of the average</param>
    public static FieldScale For(SimulationResult r, FieldKind kind, float? layerZ, Snapshot? snap)
    {
        var field = r.Field;
        var g = field.Grid;
        var s = new FieldScale { Kind = kind };

        switch (kind)
        {
            case FieldKind.Speed:
                s.Label = "Speed"; s.Unit = "m/s";
                s.Lo = 0; s.Hi = MathF.Max(0.05f, r.ColorMax);
                s.Ramp = RenderKit.SpeedRamp;
                s.Sample = snap == null
                    ? p => Analysis.SampleVelocity(field, p).Length()
                    : p => RenderKit.SampleVelocity(g, snap, p).Length();
                break;

            case FieldKind.Pressure:
            {
                float pMax = RenderKit.PressureMax(field, layerZ);
                s.Label = "Pressure"; s.Unit = "Pa";
                s.Lo = -pMax; s.Hi = pMax;
                s.Ramp = Colormap.Pressure;
                s.Sample = p => RenderKit.SamplePressure(field, p);
                break;
            }

            case FieldKind.Temperature:
            {
                bool useSnap = snap != null && snap.T.Length >= g.Count;
                var t = useSnap ? null : field.T;
                s.Label = "Temperature"; s.Unit = "°C"; s.TickFormat = "0.0";
                s.Ramp = Colormap.Temperature;
                if (!useSnap && t!.Length < g.Count)
                {
                    s.Available = false;
                    s.Message = "No temperature field · run with Thermal enabled";
                    break;
                }
                float centre = r.Model.Env.OutdoorTemperature;
                float lo, hi;
                if (useSnap)
                {
                    lo = RenderKit.PercentileIndoor(r, snap!.T, 0.02f);
                    hi = RenderKit.PercentileIndoor(r, snap.T, 0.98f);
                    s.Sample = p => RenderKit.SampleTrilinear(g, i => (float)snap.T[i], p);
                }
                else
                {
                    lo = RenderKit.PercentileIndoor(r, t!, 0.02f);
                    hi = RenderKit.PercentileIndoor(r, t!, 0.98f);
                    s.Sample = p => RenderKit.SampleTrilinear(g, i => t![i], p);
                }
                float widen = 5f;
                if (!float.IsNaN(lo)) widen = MathF.Max(widen, MathF.Max(MathF.Abs(hi - centre), MathF.Abs(centre - lo)));
                s.Lo = centre - widen; s.Hi = centre + widen;
                s.Levels = RenderKit.StepLevels(s.Lo, s.Hi, 1f);     // iso-lines every 1 K
                break;
            }

            case FieldKind.Co2:
            {
                var a = r.Scalars.Co2;
                s.Label = "CO₂"; s.Unit = "ppm"; s.TickFormat = "0";
                s.Ramp = Colormap.Co2;
                if (a.Length < g.Count) { s.Available = false; s.Message = "No CO₂ field · run with Air quality enabled"; break; }
                float p98 = RenderKit.PercentileIndoor(r, a, 0.98f);
                s.Lo = r.Model.Env.OutdoorCo2;
                s.Hi = MathF.Max(1200f, float.IsNaN(p98) ? 0f : p98);
                if (s.Hi <= s.Lo) s.Hi = s.Lo + 100f;
                s.Sample = p => RenderKit.SampleTrilinear(g, i => a[i], p);
                break;
            }

            case FieldKind.Age:
            {
                var a = r.Scalars.Age;
                s.Label = "Age of air"; s.Unit = "s"; s.TickFormat = "0";
                s.Ramp = Colormap.Age;
                if (a.Length < g.Count) { s.Available = false; s.Message = "No age-of-air field · run with Air quality enabled"; break; }
                float p98 = RenderKit.PercentileIndoor(r, a, 0.98f);
                s.Lo = 0; s.Hi = float.IsNaN(p98) || p98 <= 0 ? 1f : p98;
                s.Sample = p => RenderKit.SampleTrilinear(g, i => a[i], p);
                break;
            }

            case FieldKind.Contaminant:
            {
                var a = r.Scalars.Contaminant;
                s.Label = "Contaminant"; s.Unit = "u/m³";
                s.Ramp = Colormap.Contaminant; s.Sqrt = true;
                s.Note = "√ colour scale";
                if (a.Length < g.Count) { s.Available = false; s.Message = "No contaminant field · run with Air quality enabled"; break; }
                float p98 = RenderKit.PercentileIndoor(r, a, 0.98f);
                s.Lo = 0; s.Hi = float.IsNaN(p98) || p98 <= 0 ? 1f : p98;
                s.TickFormat = s.Hi >= 100 ? "0" : s.Hi >= 10 ? "0.0" : "0.00";
                s.Sample = p => RenderKit.SampleTrilinear(g, i => a[i], p);
                break;
            }

            case FieldKind.Pmv:
            {
                var a = r.Comfort.Pmv;
                s.Label = "PMV"; s.Unit = ""; s.TickFormat = "0.0";
                s.Ramp = Colormap.Pmv;
                s.Lo = -3; s.Hi = 3;
                s.Band = (-0.5f, 0.5f);
                s.Note = "green band = comfortable, |PMV| ≤ 0.5";
                if (a.Length < g.Count) { s.Available = false; s.Message = "No PMV field · run with Comfort enabled"; break; }
                s.Sample = p => RenderKit.SampleTrilinear(g, i => a[i], p);
                break;
            }

            case FieldKind.Ppd:
            {
                var a = r.Comfort.Ppd;
                s.Label = "PPD"; s.Unit = "%"; s.TickFormat = "0";
                s.Ramp = Colormap.Ppd;
                s.Lo = 5; s.Hi = 100;
                if (a.Length < g.Count) { s.Available = false; s.Message = "No PPD field · run with Comfort enabled"; break; }
                s.Sample = p => RenderKit.SampleTrilinear(g, i => a[i], p);
                break;
            }

            case FieldKind.DraughtRate:
            {
                var a = r.Comfort.Dr;
                s.Label = "Draught rate"; s.Unit = "%"; s.TickFormat = "0";
                s.Ramp = Colormap.DraughtRate;
                s.Lo = 0; s.Hi = 40;
                s.Mark = 20; s.Note = "20 % = ISO 7730 draught limit";
                if (a.Length < g.Count) { s.Available = false; s.Message = "No draught-rate field · run with Comfort enabled"; break; }
                s.Sample = p => RenderKit.SampleTrilinear(g, i => a[i], p);
                break;
            }

            case FieldKind.Q:
            {
                s.Label = "Q-criterion"; s.Unit = "1/s²";
                s.Ramp = Colormap.Q;
                float[]? q = null;
                try { q = Vorticity.Q(field); }
                catch (NotImplementedException) { }      // the vorticity pass has not landed yet
                if (q == null || q.Length < g.Count)
                {
                    s.Available = false;
                    s.Message = "Q-criterion not available in this build";
                    break;
                }
                float p98 = RenderKit.PercentileIndoor(r, q, 0.98f, positiveOnly: true);
                s.Lo = 0; s.Hi = float.IsNaN(p98) || p98 <= 0 ? 1f : p98;
                s.TickFormat = s.Hi >= 100 ? "0" : s.Hi >= 10 ? "0.0" : "0.00";
                s.Sample = p => { float v = RenderKit.SampleTrilinear(g, i => q[i], p); return float.IsNaN(v) ? v : MathF.Max(0f, v); };
                break;
            }
        }
        return s;
    }
}

/// <summary>Shared drawing primitives for the plan and section renderers: field rasterising, contours,
/// streamline and arrow glyphs, legend, title band, text with a halo.</summary>
internal static class RenderKit
{
    public const double TitleH = 34, LegendW = 110, Margin = 4.0;

    // Viridis, Matt Zucker's polynomial fit -- the same coefficients as WindFlow.App's Viewport/Shaders.cs
    // Viridis() and MainWindow.axaml.cs Palette.Viridis(), copied verbatim so the report/Viz images and the
    // live viewport agree on Speed colour (see HANDOFF.md). Do not tweak independently of those two.
    static readonly double[][] ViridisC =
    [
        [0.2777273272, 0.0054073445, 0.3340998053],
        [0.1050930431, 1.4046135299, 1.3845901626],
        [-0.3308618287, 0.2148475595, 0.0950951630],
        [-4.6342304990, -5.7991009734, -19.3324409563],
        [6.2282699363, 14.1799333668, 56.6905526007],
        [4.7763849977, -13.7451453777, -65.3530326334],
        [-5.4354558559, 4.6458526122, 26.3124352496],
    ];

    public static (byte R, byte G, byte B) Viridis(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        Span<double> rgb = stackalloc double[3];
        for (int c = 0; c < 3; c++)
        {
            double v = ViridisC[6][c];
            for (int i = 5; i >= 0; i--) v = v * t + ViridisC[i][c];
            rgb[c] = Math.Clamp(v, 0, 1);
        }
        return ((byte)(rgb[0] * 255), (byte)(rgb[1] * 255), (byte)(rgb[2] * 255));
    }

    /// <summary>Speed ramp used for Plan/Section/Frames and the legend: same 0.20-0.80 domain remap as
    /// the App's Palette.SpeedBrush, so the report and the on-screen viewport agree.</summary>
    public static (byte R, byte G, byte B) SpeedRamp(float t) => Viridis(0.20f + 0.80f * Math.Clamp(t, 0f, 1f));

    public static readonly Color Background = Color.FromRgb(0xFA, 0xFA, 0xF8);
    public static readonly Color SolidGrey = Color.FromRgb(0x3A, 0x3A, 0x3A);
    public static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)));
    public static readonly Brush Grey = Frozen(new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x76)));
    public static readonly Brush DarkGrey = Frozen(new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x48)));
    public static readonly Brush Paper = Frozen(new SolidColorBrush(Background));
    public static readonly Brush Halo = Frozen(new SolidColorBrush(Color.FromArgb(0xD0, 0x1A, 0x1A, 0x1A)));
    public static readonly Brush White = Frozen(new SolidColorBrush(Colors.White));
    public static readonly Brush Cyan = Frozen(new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)));
    public static readonly Brush Red = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)));
    public static readonly Brush Magenta = Frozen(new SolidColorBrush(Color.FromRgb(0xE0, 0x2D, 0xC8)));
    public static readonly Pen RoomPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(153, 0, 0, 0))), 1));
    public static readonly Pen FramePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC2))), 1));
    public static readonly Pen ContourPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(89, 0, 0, 0))), 0.8));
    // Streamlines and arrows are coloured with the same scale as the field underneath, so every glyph gets a
    // dark outline (0.5 px proud on each side); without it an orange arrow over an orange field vanishes.
    public static readonly Brush Shadow = Frozen(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)));
    public static readonly Pen LinePen = Frozen(new Pen(Shadow, 3.2)
    { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
    static readonly Pen HaloSolid = Frozen(new Pen(Brushes.Black, 3.2)
    { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });   // LinePen, opaque
    public static readonly Pen TickHalo = Frozen(new Pen(Shadow, 5.0));
    public static readonly Pen RectHalo = Frozen(new Pen(Shadow, 3.2));
    public static readonly Pen ShadowPen = Frozen(new Pen(Shadow, 2.4)
    { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    // ---- text ----

    public static readonly FontFamily Ui = new("Segoe UI, Tahoma, Arial, Global User Interface");

    public static FormattedText Text(string s, double size, Brush brush, bool semibold = false) => new(
        s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface(Ui, FontStyles.Normal, semibold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
        size, brush, 1.0)
    { TextAlignment = TextAlignment.Left };

    /// <summary>Text with a dark 1 px halo, so it stays legible over any field colour. at = top-left,
    /// or top-centre when centred.</summary>
    public static void HaloText(DrawingContext dc, string s, Point at, double size, Brush fill, bool centred = false, Brush? halo = null)
    {
        if (string.IsNullOrEmpty(s)) return;
        var ft = Text(s, size, fill);
        var origin = new Point(centred ? at.X - ft.Width * 0.5 : at.X, at.Y);
        var geo = ft.BuildGeometry(origin);
        geo.Freeze();
        var pen = new Pen(halo ?? Halo, 2.0) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geo);
        dc.DrawGeometry(fill, null, geo);
    }

    // ---- sampling ----

    /// <summary>Cell index containing p, or -1 when p is outside the grid.</summary>
    public static int CellAt(VoxelGrid g, Vector3 p)
    {
        int x = (int)MathF.Floor((p.X - g.Origin.X) / g.Dx);
        int y = (int)MathF.Floor((p.Y - g.Origin.Y) / g.Dx);
        int z = (int)MathF.Floor((p.Z - g.Origin.Z) / g.Dx);
        if (x < 0 || x >= g.Nx || y < 0 || y >= g.Ny || z < 0 || z >= g.Nz) return -1;
        return g.Index(x, y, z);
    }

    public static bool IsSolidAt(VoxelGrid g, Vector3 p)
    {
        int i = CellAt(g, p);
        return i >= 0 && g.Cell[i] == VoxelGrid.Solid;
    }

    /// <summary>Trilinear pressure, 0 inside solids. Analysis has no SamplePressure yet, so this mirrors
    /// Analysis.SampleVelocity.</summary>
    public static float SamplePressure(FlowField field, Vector3 p)
    {
        var g = field.Grid;
        Vector3 lp = (p - g.Origin) / g.Dx - new Vector3(0.5f);
        int x0 = (int)MathF.Floor(lp.X), y0 = (int)MathF.Floor(lp.Y), z0 = (int)MathF.Floor(lp.Z);
        float fx = lp.X - x0, fy = lp.Y - y0, fz = lp.Z - z0;

        float At(int x, int y, int z)
        {
            x = Math.Clamp(x, 0, g.Nx - 1);
            y = Math.Clamp(y, 0, g.Ny - 1);
            z = Math.Clamp(z, 0, g.Nz - 1);
            int idx = g.Index(x, y, z);
            return g.Cell[idx] != 0 ? 0f : field.P[idx];
        }

        float c00 = At(x0, y0, z0) + (At(x0 + 1, y0, z0) - At(x0, y0, z0)) * fx;
        float c10 = At(x0, y0 + 1, z0) + (At(x0 + 1, y0 + 1, z0) - At(x0, y0 + 1, z0)) * fx;
        float c01 = At(x0, y0, z0 + 1) + (At(x0 + 1, y0, z0 + 1) - At(x0, y0, z0 + 1)) * fx;
        float c11 = At(x0, y0 + 1, z0 + 1) + (At(x0 + 1, y0 + 1, z0 + 1) - At(x0, y0 + 1, z0 + 1)) * fx;
        float c0 = c00 + (c10 - c00) * fy, c1 = c01 + (c11 - c01) * fy;
        return c0 + (c1 - c0) * fz;
    }

    /// <summary>99th percentile of |P| over the air cells of one grid layer (or the whole grid when
    /// layerZ is null), never below 1 Pa.</summary>
    public static float PressureMax(FlowField field, float? layerZ)
    {
        var g = field.Grid;
        if (field.P.Length < g.Count) return 1f;
        var values = new List<float>(4096);
        if (layerZ is float z)
        {
            int zi = Math.Clamp((int)MathF.Floor((z - g.Origin.Z) / g.Dx), 0, g.Nz - 1);
            for (int y = 0; y < g.Ny; y++)
            for (int x = 0; x < g.Nx; x++)
            {
                int i = g.Index(x, y, zi);
                if (g.Cell[i] == 0) values.Add(MathF.Abs(field.P[i]));
            }
        }
        else
        {
            int stride = Math.Max(1, g.Count / 200_000);
            for (int i = 0; i < g.Count; i += stride)
                if (g.Cell[i] == 0) values.Add(MathF.Abs(field.P[i]));
        }
        if (values.Count == 0) return 1f;
        values.Sort();
        float p99 = values[Math.Min(values.Count - 1, (int)(values.Count * 0.99f))];
        return MathF.Max(1f, p99);
    }

    /// <summary>Trilinear sample of any cell-centred scalar, skipping solid and NaN corners and renormalising
    /// the weights (so a cell next to a wall is not pulled towards 0). NaN when all eight corners are out.</summary>
    public static float SampleTrilinear(VoxelGrid g, Func<int, float> get, Vector3 p)
    {
        Vector3 lp = (p - g.Origin) / g.Dx - new Vector3(0.5f);
        int x0 = (int)MathF.Floor(lp.X), y0 = (int)MathF.Floor(lp.Y), z0 = (int)MathF.Floor(lp.Z);
        float fx = lp.X - x0, fy = lp.Y - y0, fz = lp.Z - z0;
        float sum = 0, wsum = 0;
        for (int dz = 0; dz <= 1; dz++)
        for (int dy = 0; dy <= 1; dy++)
        for (int dx = 0; dx <= 1; dx++)
        {
            int x = Math.Clamp(x0 + dx, 0, g.Nx - 1);
            int y = Math.Clamp(y0 + dy, 0, g.Ny - 1);
            int z = Math.Clamp(z0 + dz, 0, g.Nz - 1);
            int idx = g.Index(x, y, z);
            if (g.Cell[idx] == VoxelGrid.Solid) continue;
            float v = get(idx);
            if (float.IsNaN(v)) continue;
            float w = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy) * (dz == 0 ? 1 - fz : fz);
            sum += w * v; wsum += w;
        }
        return wsum > 1e-6f ? sum / wsum : float.NaN;
    }

    /// <summary>Trilinear velocity of one snapshot, mirroring Analysis.SampleVelocity over the Half arrays
    /// (0 inside solids, the stored velocity everywhere else).</summary>
    public static Vector3 SampleVelocity(VoxelGrid g, Snapshot s, Vector3 p)
    {
        if (s.Ux.Length < g.Count) return Vector3.Zero;
        Vector3 lp = (p - g.Origin) / g.Dx - new Vector3(0.5f);
        int x0 = (int)MathF.Floor(lp.X), y0 = (int)MathF.Floor(lp.Y), z0 = (int)MathF.Floor(lp.Z);
        float fx = lp.X - x0, fy = lp.Y - y0, fz = lp.Z - z0;

        Vector3 At(int x, int y, int z)
        {
            x = Math.Clamp(x, 0, g.Nx - 1);
            y = Math.Clamp(y, 0, g.Ny - 1);
            z = Math.Clamp(z, 0, g.Nz - 1);
            int i = g.Index(x, y, z);
            return g.Cell[i] == VoxelGrid.Solid ? Vector3.Zero : new Vector3((float)s.Ux[i], (float)s.Uy[i], (float)s.Uz[i]);
        }

        var c00 = Vector3.Lerp(At(x0, y0, z0), At(x0 + 1, y0, z0), fx);
        var c10 = Vector3.Lerp(At(x0, y0 + 1, z0), At(x0 + 1, y0 + 1, z0), fx);
        var c01 = Vector3.Lerp(At(x0, y0, z0 + 1), At(x0 + 1, y0, z0 + 1), fx);
        var c11 = Vector3.Lerp(At(x0, y0 + 1, z0 + 1), At(x0 + 1, y0 + 1, z0 + 1), fx);
        return Vector3.Lerp(Vector3.Lerp(c00, c10, fy), Vector3.Lerp(c01, c11, fy), fz);
    }

    /// <summary>Quantile of a cell array over the air cells (strided for big grids), NaN when there is nothing
    /// to measure. positiveOnly keeps values &gt; 0 only (Q-criterion).</summary>
    public static float Percentile(VoxelGrid g, float[] a, float q, bool positiveOnly = false)
        => Percentile(g, i => a.Length > i ? a[i] : float.NaN, a.Length, q, positiveOnly);

    public static float Percentile(VoxelGrid g, Half[] a, float q, bool positiveOnly = false)
        => Percentile(g, i => a.Length > i ? (float)a[i] : float.NaN, a.Length, q, positiveOnly);

    static float Percentile(VoxelGrid g, Func<int, float> get, int length, float q, bool positiveOnly)
    {
        if (length < g.Count) return float.NaN;
        int stride = Math.Max(1, g.Count / 200_000);
        var values = new List<float>(4096);
        for (int i = 0; i < g.Count; i += stride)
        {
            if (g.Cell[i] == VoxelGrid.Solid) continue;
            float v = get(i);
            if (float.IsNaN(v) || float.IsInfinity(v)) continue;
            if (positiveOnly && v <= 0) continue;
            values.Add(v);
        }
        if (values.Count == 0) return float.NaN;
        values.Sort();
        return values[Math.Clamp((int)(values.Count * q), 0, values.Count - 1)];
    }

    // Cache of PercentileIndoor results, keyed by the source array (identity) + quantile + positiveOnly and
    // scoped to the SimulationResult's lifetime (a ConditionalWeakTable entry is collected with the result,
    // so long-lived sessions with many runs never accumulate stale grids). Frames() renders one plan per
    // snapshot from the same result, so this turns an O(cells) scan per frame into one.
    static readonly ConditionalWeakTable<SimulationResult, Dictionary<(object Array, float Q, bool PositiveOnly), float>> _indoorCache = new();

    /// <summary>Quantile of a cell array (or a snapshot's Half array) restricted to finite, non-solid cells
    /// whose centre lies inside one of the model's rooms (z within [ZMin, ZMax], xy inside its loops); with
    /// no rooms, falls back to cells inside <see cref="Voxelizer.PlanBounds"/>. This keeps the outdoor
    /// wind-tunnel margin — which can dominate a coarse "whole grid" percentile — out of the legend range.</summary>
    public static float PercentileIndoor(SimulationResult r, float[] a, float q, bool positiveOnly = false)
        => PercentileIndoor(r, a, i => a.Length > i ? a[i] : float.NaN, a.Length, q, positiveOnly);

    public static float PercentileIndoor(SimulationResult r, Half[] a, float q, bool positiveOnly = false)
        => PercentileIndoor(r, a, i => a.Length > i ? (float)a[i] : float.NaN, a.Length, q, positiveOnly);

    static float PercentileIndoor(SimulationResult r, object arrayKey, Func<int, float> get, int length, float q, bool positiveOnly)
    {
        var cache = _indoorCache.GetValue(r, _ => new());
        var key = (arrayKey, q, positiveOnly);
        if (cache.TryGetValue(key, out float cached)) return cached;
        float value = ComputeIndoorPercentile(r, get, length, q, positiveOnly);
        cache[key] = value;
        return value;
    }

    static float ComputeIndoorPercentile(SimulationResult r, Func<int, float> get, int length, float q, bool positiveOnly)
    {
        var g = r.Field.Grid;
        if (length < g.Count) return float.NaN;
        var rooms = r.Model.Rooms;
        bool useBounds = rooms.Count == 0;
        (Vector2 Min, Vector2 Max) bounds = useBounds ? Voxelizer.PlanBounds(r.Model) : default;

        int stride = Math.Max(1, g.Count / 200_000);
        int nxy = g.Nx * g.Ny;
        var values = new List<float>(4096);
        for (int i = 0; i < g.Count; i += stride)
        {
            if (g.Cell[i] == VoxelGrid.Solid) continue;
            float v = get(i);
            if (float.IsNaN(v) || float.IsInfinity(v)) continue;
            if (positiveOnly && v <= 0) continue;

            int z = i / nxy, rem = i % nxy, y = rem / g.Nx, x = rem % g.Nx;
            var c = g.CellCenter(x, y, z);
            bool inside = useBounds
                ? c.X >= bounds.Min.X && c.X <= bounds.Max.X && c.Y >= bounds.Min.Y && c.Y <= bounds.Max.Y
                : InsideAnyRoom(rooms, c);
            if (!inside) continue;
            values.Add(v);
        }
        if (values.Count == 0) return float.NaN;
        values.Sort();
        return values[Math.Clamp((int)(values.Count * q), 0, values.Count - 1)];
    }

    static bool InsideAnyRoom(List<Room> rooms, Vector3 c)
    {
        foreach (var room in rooms)
            if (c.Z >= room.ZMin && c.Z <= room.ZMax && InsideLoops(room.Loops, new Vector2(c.X, c.Y)))
                return true;
        return false;
    }

    // Even-odd point-in-polygon test, mirrors Analysis.InsideLoops (internal to WindFlow.Core, so
    // re-implemented here rather than exposed across the assembly boundary — WindFlow.Sandbox does the same).
    static bool InsideLoops(List<Vector2[]> loops, Vector2 p)
    {
        bool inside = false;
        foreach (var loop in loops)
        {
            bool hit = false;
            for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
            {
                var a = loop[i]; var b = loop[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    hit = !hit;
            }
            if (hit) inside = !inside;
        }
        return inside;
    }

    /// <summary>Levels at a fixed step (e.g. every 1 K), capped at 30 lines so a wide range stays readable.</summary>
    public static float[] StepLevels(float lo, float hi, float step)
    {
        if (!(hi > lo) || step <= 0) return [];
        while ((hi - lo) / step > 30) step *= 2;
        var list = new List<float>();
        for (float v = MathF.Ceiling(lo / step) * step; v < hi; v += step) list.Add(v);
        return list.ToArray();
    }

    // ---- field raster ----

    /// <summary>One pixel-resolution raster of the scalar field over the frame. Returns the bitmap and the
    /// per-pixel scalar (NaN in solids, outside the grid and where the field has no data) for the contour
    /// pass. NaN pixels are left fully transparent, so the paper colour shows through.</summary>
    public static WriteableBitmap RenderField(Frame f, Func<double, double, Vector3> map, VoxelGrid g,
        FieldScale scale, out float[] scalar)
        => Raster(f, map, g, p => scale.Sample(p), v => scale.Ramp(scale.Norm(v)), out scalar);

    /// <summary>Same raster, but the value is b − a at every pixel and the colour comes from a caller-supplied
    /// ramp over the already-known range.</summary>
    public static WriteableBitmap Raster(Frame f, Func<double, double, Vector3> map, VoxelGrid g,
        Func<Vector3, float> sample, Func<float, (byte R, byte G, byte B)> colour, out float[] scalar)
    {
        int w = f.W, h = f.H;
        var pixels = new int[w * h];
        var values = new float[w * h];
        int solid = unchecked((int)0xFF000000) | (SolidGrey.R << 16) | (SolidGrey.G << 8) | SolidGrey.B;

        System.Threading.Tasks.Parallel.For(0, h, py =>
        {
            double v = f.V(py + 0.5);
            int row = py * w;
            for (int px = 0; px < w; px++)
            {
                var p = map(f.U(px + 0.5), v);
                int cell = CellAt(g, p);
                if (cell < 0) { pixels[row + px] = 0; values[row + px] = float.NaN; continue; }
                if (g.Cell[cell] == VoxelGrid.Solid) { pixels[row + px] = solid; values[row + px] = float.NaN; continue; }

                float q = sample(p);
                values[row + px] = q;
                if (float.IsNaN(q)) { pixels[row + px] = 0; continue; }     // no data here: transparent
                var c = colour(q);
                pixels[row + px] = unchecked((int)0xFF000000) | (c.R << 16) | (c.G << 8) | c.B;
            }
        });

        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        bmp.Freeze();
        scalar = values;
        return bmp;
    }

    /// <summary>Difference raster: one pass for the values, then a symmetric range from the 98th percentile of
    /// |value| (never 0), then the blue → white → red diverging ramp centred on 0.</summary>
    public static WriteableBitmap RenderDifference(Frame f, Func<double, double, Vector3> map, VoxelGrid g,
        Func<Vector3, float> difference, out float[] scalar, out float range)
    {
        int w = f.W, h = f.H;
        var values = new float[w * h];
        System.Threading.Tasks.Parallel.For(0, h, py =>
        {
            double v = f.V(py + 0.5);
            int row = py * w;
            for (int px = 0; px < w; px++)
            {
                var p = map(f.U(px + 0.5), v);
                int cell = CellAt(g, p);
                values[row + px] = cell < 0 || g.Cell[cell] == VoxelGrid.Solid ? float.NaN : difference(p);
            }
        });

        var magnitudes = new List<float>(values.Length / 4 + 16);
        for (int i = 0; i < values.Length; i += 3)
            if (!float.IsNaN(values[i]) && !float.IsInfinity(values[i])) magnitudes.Add(MathF.Abs(values[i]));
        magnitudes.Sort();
        range = magnitudes.Count == 0 ? 1f : magnitudes[Math.Min(magnitudes.Count - 1, (int)(magnitudes.Count * 0.98f))];
        if (!(range > 1e-9f)) range = 1e-6f;

        float inv = 0.5f / range;
        int solid = unchecked((int)0xFF000000) | (SolidGrey.R << 16) | (SolidGrey.G << 8) | SolidGrey.B;
        var pixels = new int[w * h];
        System.Threading.Tasks.Parallel.For(0, h, py =>
        {
            double vy = f.V(py + 0.5);
            int row = py * w;
            for (int px = 0; px < w; px++)
            {
                float q = values[row + px];
                if (float.IsNaN(q))
                {
                    var p = map(f.U(px + 0.5), vy);
                    int cell = CellAt(g, p);
                    pixels[row + px] = cell >= 0 && g.Cell[cell] == VoxelGrid.Solid ? solid : 0;
                    continue;
                }
                var c = Colormap.Diverging(0.5f + q * inv);
                pixels[row + px] = unchecked((int)0xFF000000) | (c.R << 16) | (c.G << 8) | c.B;
            }
        });

        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        bmp.Freeze();
        scalar = values;
        return bmp;
    }

    /// <summary>Grey panel for a field the run did not compute. The walls are still drawn, so the plan stays
    /// readable; the message goes on top of every overlay via <see cref="NoDataLabel"/>.</summary>
    public static void NoData(DrawingContext dc, Frame f, Func<double, double, Vector3> map, VoxelGrid g)
    {
        var fill = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE2)));
        dc.DrawRectangle(fill, null, f.Area);
        var solids = Raster(f, map, g, _ => float.NaN, _ => (SolidGrey.R, SolidGrey.G, SolidGrey.B), out _);
        dc.DrawImage(solids, f.Area);
    }

    /// <summary>Plate with the "why is this empty" message, drawn last so nothing crosses it.</summary>
    public static void NoDataLabel(DrawingContext dc, Frame f, string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        var ft = Text(message, 12, DarkGrey, semibold: true);
        double w = Math.Min(f.W - 16, ft.Width + 20), h = ft.Height + 10;
        var rect = new Rect(f.X0 + (f.W - w) * 0.5, f.Y0 + 14, w, h);
        dc.DrawRoundedRectangle(Frozen(new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF))),
            Frozen(new Pen(Grey, 1)), rect, 4, 4);
        dc.DrawText(ft, new Point(rect.X + 10, rect.Y + 5));
    }

    /// <summary>Marching squares at 8 evenly spaced levels between lo and hi.</summary>
    public static void DrawContours(DrawingContext dc, Frame f, float[] scalar, float lo, float hi)
    {
        var levels = new float[8];
        for (int k = 1; k <= 8; k++) levels[k - 1] = lo + (hi - lo) * k / 9f;
        DrawContours(dc, f, scalar, levels);
    }

    /// <summary>Marching squares at the given levels (e.g. temperature iso-lines every 1 K).</summary>
    public static void DrawContours(DrawingContext dc, Frame f, float[] scalar, float[] levels)
    {
        if (levels.Length == 0) return;
        int w = f.W, h = f.H, step = 3;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            foreach (float level in levels)
            {
                // A uniform region (outdoor air at exactly the far-field temperature) still varies by a few
                // ULPs after interpolation, which speckles the image with tiny closed loops when a level lands
                // on it. Anything flatter than this is not a real gradient.
                float flat = MathF.Max(1e-6f, MathF.Abs(level) * 1e-5f);
                for (int y = 0; y + step < h; y += step)
                for (int x = 0; x + step < w; x += step)
                {
                    float a = scalar[y * w + x];                      // top-left
                    float b = scalar[y * w + x + step];               // top-right
                    float c = scalar[(y + step) * w + x + step];      // bottom-right
                    float d = scalar[(y + step) * w + x];             // bottom-left
                    if (float.IsNaN(a) || float.IsNaN(b) || float.IsNaN(c) || float.IsNaN(d)) continue;
                    if (MathF.Max(MathF.Max(a, b), MathF.Max(c, d)) - MathF.Min(MathF.Min(a, b), MathF.Min(c, d)) <= flat) continue;

                    int code = (a > level ? 1 : 0) | (b > level ? 2 : 0) | (c > level ? 4 : 0) | (d > level ? 8 : 0);
                    if (code == 0 || code == 15) continue;

                    double x0 = f.X0 + x + 0.5, y0 = f.Y0 + y + 0.5, x1 = x0 + step, y1 = y0 + step;
                    Point Top() => new(x0 + step * Frac(a, b, level), y0);
                    Point Right() => new(x1, y0 + step * Frac(b, c, level));
                    Point Bottom() => new(x0 + step * Frac(d, c, level), y1);
                    Point Left() => new(x0, y0 + step * Frac(a, d, level));

                    switch (code)
                    {
                        case 1: case 14: Seg(ctx, Left(), Top()); break;
                        case 2: case 13: Seg(ctx, Top(), Right()); break;
                        case 3: case 12: Seg(ctx, Left(), Right()); break;
                        case 4: case 11: Seg(ctx, Right(), Bottom()); break;
                        case 6: case 9: Seg(ctx, Top(), Bottom()); break;
                        case 7: case 8: Seg(ctx, Left(), Bottom()); break;
                        case 5: Seg(ctx, Left(), Top()); Seg(ctx, Right(), Bottom()); break;
                        case 10: Seg(ctx, Top(), Right()); Seg(ctx, Left(), Bottom()); break;
                    }
                }
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, ContourPen, geo);

        static double Frac(float p, float q, float level) => Math.Clamp(Math.Abs(q - p) < 1e-9f ? 0.5 : (level - p) / (q - p), 0, 1);
        static void Seg(StreamGeometryContext ctx, Point p, Point q) { ctx.BeginFigure(p, false, false); ctx.LineTo(q, true, false); }
    }

    // ---- glyphs ----

    public sealed class PenCache
    {
        readonly Dictionary<int, Pen> _pens = new();
        readonly double _width;
        public PenCache(double width) => _width = width;

        public Pen Get(float t)
        {
            int key = Math.Clamp((int)(t * 96f), 0, 96);
            if (_pens.TryGetValue(key, out var pen)) return pen;
            var (r, g, b) = SpeedRamp(key / 96f);
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            pen = new Pen(brush, _width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            _pens[key] = pen;
            return pen;
        }
    }

    /// <summary>Filled triangle head of length len at p pointing along the unit vector (dx, dy).</summary>
    public static void Head(DrawingContext dc, Point p, double dx, double dy, double len, Brush fill)
    {
        double nx = -dy, ny = dx, half = len * 0.42;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(p, true, true);
            ctx.LineTo(new Point(p.X - dx * len + nx * half, p.Y - dy * len + ny * half), false, false);
            ctx.LineTo(new Point(p.X - dx * len - nx * half, p.Y - dy * len - ny * half), false, false);
        }
        geo.Freeze();
        dc.DrawGeometry(fill, null, geo);
    }

    /// <summary>Shaft + head, from p0 to p1. outline = dark halo underneath, for glyphs drawn over the field.</summary>
    public static void Arrow(DrawingContext dc, Point p0, Point p1, Pen shaft, Brush fill, double headLen, bool outline = false)
    {
        double dx = p1.X - p0.X, dy = p1.Y - p0.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.4) return;
        dx /= len; dy /= len;
        headLen = Math.Min(headLen, len * 0.8);
        var neck = new Point(p1.X - dx * headLen * 0.6, p1.Y - dy * headLen * 0.6);
        if (outline)
        {
            dc.DrawLine(ShadowPen, p0, neck);
            Head(dc, new Point(p1.X + dx * 0.5, p1.Y + dy * 0.5), dx, dy, headLen + 1.3, Shadow);
        }
        dc.DrawLine(shaft, p0, neck);
        Head(dc, p1, dx, dy, headLen, fill);
    }

    /// <summary>Streamlines: a soft dark outline under anti-aliased polylines coloured per segment by speed,
    /// with small arrowheads every arrowStepPx of arc length.</summary>
    public static void FlowLines(DrawingContext dc, List<(Point[] Pts, float[] Speeds)> lines, float colorMax, double arrowStepPx)
    {
        if (lines.Count == 0) return;
        // The outline goes down in short open pieces, not as one geometry holding every line: WPF widens a
        // stroked geometry into one fill and resolves every self-crossing, so a single outline of ~100 looping
        // traces cost seconds to minutes in RenderTargetBitmap (the report's slow section). Pieces overlap one
        // point so the halo stays continuous. They are drawn opaque inside one opacity layer at the Shadow alpha,
        // so crossings and joins do not double-darken the way overlapping translucent strokes would.
        const int piece = 24;
        dc.PushOpacity(Shadow is SolidColorBrush sb ? sb.Color.A / 255.0 : 0.43);
        foreach (var (pts, _) in lines)
            for (int start = 0; start + 1 < pts.Length; start += piece)
            {
                int end = Math.Min(pts.Length - 1, start + piece);
                var outline = new StreamGeometry();
                using (var ctx = outline.Open())
                {
                    ctx.BeginFigure(pts[start], false, false);
                    ctx.PolyLineTo(pts[(start + 1)..(end + 1)], true, true);
                }
                outline.Freeze();
                dc.DrawGeometry(null, HaloSolid, outline);
            }
        dc.Pop();

        var pens = new PenCache(1.6);
        float inv = 1f / MathF.Max(1e-6f, colorMax);
        foreach (var (pts, speeds) in lines)
        {
            double since = arrowStepPx * 0.55;
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                float t = 0.5f * (speeds[i] + speeds[i + 1]) * inv;
                var pen = pens.Get(t);
                dc.DrawLine(pen, pts[i], pts[i + 1]);

                double dx = pts[i + 1].X - pts[i].X, dy = pts[i + 1].Y - pts[i].Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-6) continue;
                since += len;
                if (since >= arrowStepPx && i > 0)
                {
                    since = 0;
                    Head(dc, pts[i + 1], dx / len, dy / len, 6.0, pen.Brush);
                }
            }
        }
    }

    // ---- chrome ----

    /// <summary>Title band across the top plus the run footer, right-aligned in the same band, and (top right)
    /// the mass-balance pill when the ledger has anything to say.</summary>
    public static void TitleBand(DrawingContext dc, double totalW, string title, FlowField field, MassLedger? ledger = null)
    {
        dc.DrawRectangle(Paper, null, new Rect(0, 0, totalW, TitleH));

        var g = field.Grid;
        string footer = string.Format(CultureInfo.InvariantCulture,
            "{0}×{1}×{2} cells · {3:0.00} m · {4} · {5} steps · {6:0.0} s",
            g.Nx, g.Ny, g.Nz, g.Dx, string.IsNullOrEmpty(field.Backend) ? "—" : field.Backend, field.Steps, field.Seconds);
        var ft = Text(footer, 9, Grey);
        dc.DrawText(ft, new Point(Math.Max(12, totalW - 12 - ft.Width), TitleH - ft.Height - 4));

        double reserve = ft.Width;
        if (ledger != null && (ledger.In != 0 || ledger.Out != 0))
            reserve = Math.Max(reserve, LedgerPill(dc, totalW - 12, 3, ledger));

        if (!string.IsNullOrWhiteSpace(title))
        {
            var tt = Text(title, 13, Ink, semibold: true);
            tt.MaxTextWidth = Math.Max(40, totalW - reserve - 36);
            tt.MaxLineCount = 1;
            tt.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(tt, new Point(12, 7));
        }
        dc.DrawLine(FramePen, new Point(0, TitleH - 0.5), new Point(totalW, TitleH - 0.5));
    }

    /// <summary>Mass-balance pill, right edge at rightX. Green when the ledger passed its own trust gate,
    /// amber with the residual when it did not. Returns the width drawn.</summary>
    static double LedgerPill(DrawingContext dc, double rightX, double y, MassLedger ledger)
    {
        int pct = (int)MathF.Round(Math.Clamp(ledger.Residual, 0f, 9.99f) * 100f);
        bool ok = ledger.IsPhysical;
        string text = ok ? $"mass balance OK · {pct} %" : $"check: residual {pct} %";
        var fill = Frozen(new SolidColorBrush(ok ? Color.FromRgb(0xE4, 0xF5, 0xE4) : Color.FromRgb(0xFD, 0xF1, 0xD8)));
        var edge = Frozen(new Pen(Frozen(new SolidColorBrush(ok ? Color.FromRgb(0x2E, 0x7D, 0x32) : Color.FromRgb(0xB0, 0x6A, 0x00))), 1));
        var ink = Frozen(new SolidColorBrush(ok ? Color.FromRgb(0x1B, 0x5E, 0x20) : Color.FromRgb(0x82, 0x4C, 0x00)));

        var ftp = Text(text, 9, ink, semibold: true);
        double w = ftp.Width + 16, h = ftp.Height + 4;
        var rect = new Rect(rightX - w, y, w, h);
        dc.DrawRoundedRectangle(fill, edge, rect, h * 0.5, h * 0.5);
        dc.DrawEllipse(edge.Brush, null, new Point(rect.X + 7, y + h * 0.5), 2.6, 2.6);
        dc.DrawText(ftp, new Point(rect.X + 13, y + 2));
        return w;
    }

    /// <summary>Right-hand legend strip: vertical colour bar with five labelled ticks, plus the field's own
    /// marks (PMV comfort band bracket, draught-rate limit line) and a note line under the bar.</summary>
    public static void Legend(DrawingContext dc, double totalW, double totalH, FieldScale s)
    {
        double barX = totalW - LegendW + 14, barW = 18;
        double top = TitleH + 30, bottom = totalH - (s.Note.Length > 0 ? 42 : 26);
        double h = Math.Max(40, bottom - top);

        var head = Text(s.Label, 10, Ink, semibold: true);
        head.MaxTextWidth = LegendW - 18;
        head.MaxLineCount = 1;
        head.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(head, new Point(barX - 6, TitleH + 10));

        if (!s.Available)
        {
            var nd = Text("no data", 9, Grey);
            nd.MaxTextWidth = LegendW - 18;
            dc.DrawText(nd, new Point(barX - 6, TitleH + 26));
            return;
        }

        int rows = (int)Math.Ceiling(h);
        for (int i = 0; i < rows; i++)
        {
            float t = 1f - (float)i / (rows - 1);
            var (r, g, b) = s.Ramp(t);
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            dc.DrawRectangle(brush, null, new Rect(barX, top + i, barW, 1.02));
        }
        dc.DrawRectangle(null, FramePen, new Rect(barX, top, barW, h));

        for (int k = 0; k <= 4; k++)
        {
            double frac = k / 4.0;
            double y = top + h * (1 - frac);
            dc.DrawLine(FramePen, new Point(barX + barW, y), new Point(barX + barW + 4, y));
            var ftk = Text(s.Tick(s.ValueAt(frac)), 9, DarkGrey);
            dc.DrawText(ftk, new Point(barX + barW + 7, y - ftk.Height * 0.5));
        }

        if (s.Mark is float mark)
        {
            double y = top + h * (1 - s.Norm(mark));
            var pen = Frozen(new Pen(Ink, 1.6));
            dc.DrawLine(pen, new Point(barX - 3, y), new Point(barX + barW + 3, y));
        }

        if (s.Band.HasValue)
        {
            var (lo, hi) = s.Band.Value;
            double y0 = top + h * (1 - s.Norm(hi)), y1 = top + h * (1 - s.Norm(lo));
            double x = barX - 6;
            var pen = Frozen(new Pen(Ink, 1.4));
            dc.DrawLine(pen, new Point(x, y0), new Point(x, y1));
            dc.DrawLine(pen, new Point(x, y0), new Point(barX - 1, y0));
            dc.DrawLine(pen, new Point(x, y1), new Point(barX - 1, y1));
        }

        if (s.Note.Length > 0)
        {
            var note = Text(s.Note, 8, Grey);
            note.MaxTextWidth = LegendW - 16;
            note.MaxLineCount = 3;
            dc.DrawText(note, new Point(totalW - LegendW + 8, bottom + 6));
        }
    }

    /// <summary>Probe crosshair plus a small value box, kept inside the image area.</summary>
    public static void ValueBox(DrawingContext dc, Frame f, Point at, IReadOnlyList<string> lines)
    {
        var pen = Frozen(new Pen(Ink, 1.2));
        dc.DrawLine(TickHalo, new Point(at.X - 9, at.Y), new Point(at.X + 9, at.Y));
        dc.DrawLine(TickHalo, new Point(at.X, at.Y - 9), new Point(at.X, at.Y + 9));
        dc.DrawLine(pen, new Point(at.X - 9, at.Y), new Point(at.X + 9, at.Y));
        dc.DrawLine(pen, new Point(at.X, at.Y - 9), new Point(at.X, at.Y + 9));
        dc.DrawEllipse(null, Frozen(new Pen(White, 1.4)), at, 4.2, 4.2);
        if (lines.Count == 0) return;

        var texts = lines.Select(l => Text(l, 9.5, Ink)).ToList();
        double w = texts.Max(t => t.Width) + 14, hh = texts.Sum(t => t.Height) + 10;
        double x = at.X + 14, y = at.Y - 8;
        if (x + w > f.X0 + f.W - 4) x = at.X - 14 - w;
        if (y + hh > f.Y0 + f.H - 4) y = f.Y0 + f.H - 4 - hh;
        if (y < f.Y0 + 4) y = f.Y0 + 4;
        if (x < f.X0 + 4) x = f.X0 + 4;

        var fill = Frozen(new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)));
        dc.DrawRoundedRectangle(fill, Frozen(new Pen(DarkGrey, 1)), new Rect(x, y, w, hh), 4, 4);
        double ty = y + 5;
        foreach (var t in texts) { dc.DrawText(t, new Point(x + 7, ty)); ty += t.Height; }
    }

    /// <summary>North arrow at the top-right of the image area. It points to TRUE north: <paramref name="northDeg"/> is
    /// BuildingModel.NorthDeg, degrees clockwise from the plan's up (+Y) (P13b-04).</summary>
    public static void North(DrawingContext dc, Frame f, float northDeg = 0)
    {
        double x = f.X0 + f.W - 22, y = f.Y0 + 16;
        double ux = Math.Sin(northDeg * Math.PI / 180), uy = -Math.Cos(northDeg * Math.PI / 180);   // pixel direction, y down
        var pen = new Pen(DarkGrey, 1.4); pen.Freeze();
        dc.DrawLine(pen, new Point(x - 8 * ux, y + 8 - 8 * uy), new Point(x + 8 * ux, y + 8 + 8 * uy));
        Head(dc, new Point(x + 9 * ux, y + 8 + 9 * uy), ux, uy, 7, DarkGrey);
        HaloText(dc, "N", new Point(x, y + 17), 11, DarkGrey, centred: true, halo: White);
    }

    /// <summary>Wind arrow at the top-left of the image area, pointing the way the air moves.</summary>
    public static void Wind(DrawingContext dc, Frame f, Vector2 wind)
    {
        if (wind.LengthSquared() < 1e-8f) return;
        var d = Vector2.Normalize(wind);
        double cx = f.X0 + 44, cy = f.Y0 + 40, len = 50;
        // +Y is up in the image, so the pixel direction flips Y.
        var p0 = new Point(cx - d.X * len * 0.5, cy + d.Y * len * 0.5);
        var p1 = new Point(cx + d.X * len * 0.5, cy - d.Y * len * 0.5);
        var pen = new Pen(DarkGrey, 2.4) { StartLineCap = PenLineCap.Round }; pen.Freeze();
        Arrow(dc, p0, p1, pen, DarkGrey, 11);
        HaloText(dc, string.Format(CultureInfo.InvariantCulture, "wind {0:0.0} m/s", wind.Length()),
            new Point(cx, cy + 22), 9, DarkGrey, centred: true, halo: White);
    }
}

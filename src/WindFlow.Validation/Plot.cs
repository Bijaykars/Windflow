using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WPoint = System.Windows.Point;

namespace WindFlow.Validation;

/// <summary>Profile plot to PNG with WPF (DrawingVisual + RenderTargetBitmap): measured points with bars,
/// the simulated line, axes, ticks, labels and a legend. Must run on an STA thread (Program.Main is [STAThread]).</summary>
public static class Plot
{
    const int Wpx = 900, Hpx = 700, ML = 90, MR = 30, MT = 60, MB = 110;

    public static void Profile(string path, ProfileLine line, double[] predMeasured, double[] predDense, string subtitle)
    {
        // Data ranges: position axis and value axis.
        var posAll = line.Dense.Select(p => p.Pos).Concat(line.Measured.Select(m => m.Pos)).ToArray();
        var valAll = line.Measured.SelectMany(m => new[] { m.Obs - Bar(m), m.Obs + Bar(m) }).Concat(predDense).Where(double.IsFinite).ToArray();
        double pMin = posAll.Min(), pMax = posAll.Max();
        double vMin = Math.Min(valAll.Min(), 0), vMax = Math.Max(valAll.Max(), 0);
        (vMin, vMax) = Nice(vMin, vMax);
        (pMin, pMax) = Nice(pMin, pMax);

        // Map (pos, val) to pixels; vertical profiles put the position on the vertical axis.
        double axX0 = ML, axX1 = Wpx - MR, axY0 = Hpx - MB, axY1 = MT;
        double hMin = line.Vertical ? vMin : pMin, hMax = line.Vertical ? vMax : pMax;
        double yMin = line.Vertical ? pMin : vMin, yMax = line.Vertical ? pMax : vMax;
        WPoint Px(double pos, double val)
        {
            double hx = line.Vertical ? val : pos, vy = line.Vertical ? pos : val;
            return new WPoint(axX0 + (hx - hMin) / (hMax - hMin) * (axX1 - axX0), axY0 - (vy - yMin) / (yMax - yMin) * (axY0 - axY1));
        }

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Wpx, Hpx));
            var grid = new Pen(new SolidColorBrush(Color.FromRgb(225, 225, 225)), 1);
            var axis = new Pen(Brushes.Black, 1.2);
            // Ticks and grid.
            foreach (double tx in Ticks(hMin, hMax))
            {
                double x = axX0 + (tx - hMin) / (hMax - hMin) * (axX1 - axX0);
                dc.DrawLine(grid, new WPoint(x, axY1), new WPoint(x, axY0));
                Text(dc, Fmt(tx), x, axY0 + 6, 13, center: true);
            }
            foreach (double ty in Ticks(yMin, yMax))
            {
                double y = axY0 - (ty - yMin) / (yMax - yMin) * (axY0 - axY1);
                dc.DrawLine(grid, new WPoint(axX0, y), new WPoint(axX1, y));
                Text(dc, Fmt(ty), axX0 - 8, y - 9, 13, right: true);
            }
            // Zero line of the value axis.
            var zeroPen = new Pen(Brushes.Gray, 1) { DashStyle = DashStyles.Dash };
            if (line.Vertical) { var a = Px(pMin, 0); var b = Px(pMax, 0); dc.DrawLine(zeroPen, a, b); }
            else { var a = Px(pMin, 0); var b = Px(pMax, 0); dc.DrawLine(zeroPen, a, b); }
            dc.DrawRectangle(null, axis, new Rect(new WPoint(axX0, axY1), new WPoint(axX1, axY0)));

            // Simulated line.
            var simPen = new Pen(new SolidColorBrush(Color.FromRgb(200, 40, 40)), 2.2);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                bool started = false;
                for (int k = 0; k < line.Dense.Count; k++)
                {
                    if (!double.IsFinite(predDense[k])) { started = false; continue; }
                    var p = Px(line.Dense[k].Pos, predDense[k]);
                    if (!started) { g.BeginFigure(p, false, false); started = true; } else g.LineTo(p, true, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, simPen, geo);

            // Measured points with bars.
            var barPen = new Pen(new SolidColorBrush(Color.FromRgb(90, 110, 170)), 1);
            var ptFill = new SolidColorBrush(Color.FromRgb(30, 70, 160));
            foreach (var m in line.Measured)
            {
                double b = Bar(m);
                if (b > 0)
                {
                    var a1 = Px(m.Pos, m.Obs - b); var a2 = Px(m.Pos, m.Obs + b);
                    dc.DrawLine(barPen, a1, a2);
                    var cap = line.Vertical ? new Vector(0, 4) : new Vector(4, 0);
                    dc.DrawLine(barPen, a1 - cap, a1 + cap); dc.DrawLine(barPen, a2 - cap, a2 + cap);
                }
                dc.DrawEllipse(ptFill, null, Px(m.Pos, m.Obs), 4, 4);
            }
            // Simulated values at the measured points (small open squares).
            var sqPen = new Pen(new SolidColorBrush(Color.FromRgb(200, 40, 40)), 1.2);
            for (int k = 0; k < line.Measured.Count; k++)
            {
                if (!double.IsFinite(predMeasured[k])) continue;
                var p = Px(line.Measured[k].Pos, predMeasured[k]);
                dc.DrawRectangle(null, sqPen, new Rect(p.X - 3, p.Y - 3, 6, 6));
            }

            // Labels.
            Text(dc, line.Title, Wpx / 2.0, 12, 18, center: true, bold: true);
            Text(dc, subtitle, Wpx / 2.0, 36, 12, center: true);
            Text(dc, line.Vertical ? line.ValueLabel : line.PosLabel, (axX0 + axX1) / 2, axY0 + 26, 14, center: true);
            var rot = new RotateTransform(-90, 22, (axY0 + axY1) / 2);
            dc.PushTransform(rot);
            Text(dc, line.Vertical ? line.PosLabel : line.ValueLabel, 22, (axY0 + axY1) / 2 - 10, 14, center: true);
            dc.Pop();

            // Legend.
            double ly = Hpx - 60;
            dc.DrawEllipse(ptFill, null, new WPoint(ML + 10, ly + 8), 4, 4);
            Text(dc, line.MeasuredLabel, ML + 22, ly, 12);
            dc.DrawLine(simPen, new WPoint(ML + 420, ly + 8), new WPoint(ML + 450, ly + 8));
            dc.DrawRectangle(null, sqPen, new Rect(ML + 432, ly + 5, 6, 6));
            Text(dc, "WindFlow (time average; squares at the measured points)", ML + 458, ly, 12);
            Text(dc, line.BarLabel, ML + 22, ly + 22, 12);
        }

        var rtb = new RenderTargetBitmap(Wpx, Hpx, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>Block-averaged in-plane speed |(u, w)| / scale on an x-z slice (Core's speed colour ramp, 0..vMax)
    /// with direction arrows on a coarse subsample, so the recirculation pattern can be read at a glance.</summary>
    public static void Slice(string path, System.Numerics.Vector3[,] v, float scale, float vMax, string title, string subtitle, string unit = "u0")
    {
        int nx = v.GetLength(0), nz = v.GetLength(1);
        const int ml = 20, mr = 20, mt = 60, mb = 50;
        double px = Math.Min(1400.0 / nx, 600.0 / nz);
        int w = (int)(nx * px) + ml + mr, h = (int)(nz * px) + mt + mb;
        var bmp = new WriteableBitmap(nx, nz, 96, 96, PixelFormats.Bgra32, null);
        var buf = new byte[nx * nz * 4];
        for (int i = 0; i < nx; i++)
            for (int k = 0; k < nz; k++)
            {
                var c = v[i, k];
                float sp = MathF.Sqrt(c.X * c.X + c.Z * c.Z) / scale;
                var (r, g, b) = WindFlow.Core.Colormap.Speed(Math.Clamp(sp / vMax, 0f, 1f));
                int o = ((nz - 1 - k) * nx + i) * 4;
                buf[o] = b; buf[o + 1] = g; buf[o + 2] = r; buf[o + 3] = 255;
            }
        bmp.WritePixels(new Int32Rect(0, 0, nx, nz), buf, nx * 4, 0);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.NearestNeighbor);
            dc.DrawImage(bmp, new Rect(ml, mt, nx * px, nz * px));
            var pen = new Pen(Brushes.White, 1.1);
            int stepA = Math.Max(1, nz / 16);
            for (int i = stepA / 2; i < nx; i += stepA)
                for (int k = stepA / 2; k < nz; k += stepA)
                {
                    var c = v[i, k];
                    float sp = MathF.Sqrt(c.X * c.X + c.Z * c.Z);
                    if (sp < 1e-6f) continue;
                    double len = stepA * px * 0.8, dx = c.X / sp * len, dz = -c.Z / sp * len;
                    var a = new WPoint(ml + (i + 0.5) * px - dx / 2, mt + (nz - k - 0.5) * px - dz / 2);
                    var b = new WPoint(a.X + dx, a.Y + dz);
                    dc.DrawLine(pen, a, b);
                    var back = new Vector(-dx, -dz); back.Normalize(); back *= len * 0.35;
                    var side = new Vector(-back.Y, back.X) * 0.5;
                    dc.DrawLine(pen, b, b + back + side); dc.DrawLine(pen, b, b + back - side);
                }
            Text(dc, title, w / 2.0, 10, 17, center: true, bold: true);
            Text(dc, subtitle, w / 2.0, 34, 12, center: true);
            Text(dc, $"colour: in-plane speed / {unit}, 0 (dark) to {vMax:0.##} (bright, saturates); arrows: direction only", w / 2.0, h - mb + 12, 12, center: true);
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    static double Bar(MeasuredPoint m) => double.IsFinite(m.Bar) ? m.Bar : 0;

    static void Text(DrawingContext dc, string s, double x, double y, double size, bool center = false, bool right = false, bool bold = false)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, Brushes.Black, 1.0);
        double dx = center ? -ft.Width / 2 : right ? -ft.Width : 0;
        dc.DrawText(ft, new WPoint(x + dx, y));
    }

    static string Fmt(double v) => Math.Abs(v) < 1e-9 ? "0" : v.ToString("0.##", CultureInfo.InvariantCulture);

    static double Step(double lo, double hi)
    {
        double raw = (hi - lo) / 8, mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / mag;
        return (n < 1.5 ? 1 : n < 3 ? 2 : n < 7 ? 5 : 10) * mag;
    }

    static (double, double) Nice(double lo, double hi)
    {
        if (hi - lo < 1e-9) { lo -= 0.5; hi += 0.5; }
        double s = Step(lo, hi);
        return (Math.Floor(lo / s) * s, Math.Ceiling(hi / s) * s);
    }

    static IEnumerable<double> Ticks(double lo, double hi)
    {
        double s = Step(lo, hi);
        for (double v = Math.Ceiling(lo / s - 1e-9) * s; v <= hi + 1e-9; v += s) yield return v;
    }
}

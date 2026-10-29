using System.Numerics;
using System.Windows.Media.Imaging;      // WPF imaging: the language WindFlow.Viz and WindFlow.Report speak
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using WindFlow.Core;
using WindFlow.Report;
using WindFlow.Viz;
using VizRenderer = WindFlow.Viz.Renderer;

namespace WindFlow.App;

/// <summary>The Advanced-mode workflows that only need Core/Viz/Report: compare, the wind rose and the PDF
/// report. No UI state lives here — the window owns that and calls these from a background task.
/// Everything in this file is safe off the UI thread except <see cref="DrawRose"/>.</summary>
public static class Advanced
{
    // ---------------------------------------------------------------- compare (B − A)

    /// <summary>Same voxel grid, so the two runs can be subtracted cell by cell. Same test Viz uses.</summary>
    public static bool SameGrid(SimulationResult a, SimulationResult b)
    {
        var ga = a.Field.Grid;
        var gb = b.Field.Grid;
        return ga.Nx == gb.Nx && ga.Ny == gb.Ny && ga.Nz == gb.Nz
            && MathF.Abs(ga.Dx - gb.Dx) < 1e-6f && Vector3.Distance(ga.Origin, gb.Origin) < 1e-4f;
    }

    /// <summary>Why the two holds cannot be compared, in the words of the thing the user changed.</summary>
    public static string GridMismatch(SimulationResult a, SimulationResult b)
    {
        var ga = a.Field.Grid;
        var gb = b.Field.Grid;
        return $"A is {ga.Nx}x{ga.Ny}x{ga.Nz} at {ga.Dx * 1000:0} mm, B is {gb.Nx}x{gb.Ny}x{gb.Nz} at {gb.Dx * 1000:0} mm. " +
               "The solver rebuilt the grid between the two holds (the model bounds changed), so the cells do not " +
               "line up. Hold A and B again without moving geometry, then compare.";
    }

    /// <summary>|u(B)| − |u(A)| per cell, ready for FieldVolume.DownsampleScalar. Solid cells read 0 in both
    /// fields, so they subtract to 0 and the downsampler skips them anyway.</summary>
    public static float[] SpeedDifference(SimulationResult a, SimulationResult b)
    {
        var fa = a.Field;
        var fb = b.Field;
        int n = Math.Min(fa.Ux.Length, fb.Ux.Length);
        var diff = new float[n];
        for (int i = 0; i < n; i++)
        {
            float sa = MathF.Sqrt(fa.Ux[i] * fa.Ux[i] + fa.Uy[i] * fa.Uy[i] + fa.Uz[i] * fa.Uz[i]);
            float sb = MathF.Sqrt(fb.Ux[i] * fb.Ux[i] + fb.Uy[i] * fb.Uy[i] + fb.Uz[i] * fb.Uz[i]);
            diff[i] = sb - sa;
        }
        return diff;
    }

    // ---------------------------------------------------------------- wind rose

    /// <summary>One of the 16 compass sectors, with every speed band of that sector folded together.</summary>
    public sealed record RoseSector(int Sector, float DirectionDeg, float MeanSpeed, int Hours, float Share);

    static readonly string[] Names =
        ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"];

    public static string Bearing(float degrees) =>
        float.IsNaN(degrees) ? "—" : Names[((int)MathF.Round(degrees / 22.5f) % 16 + 16) % 16];

    /// <summary>WindRose.Build returns one bin per sector and speed band (plus a calm bin at sector −1); the rose
    /// draws one wedge per sector, so fold the bands into hours and an hours-weighted mean speed.</summary>
    public static List<RoseSector> Sectors(IReadOnlyList<WindBin> bins)
    {
        int total = 0;
        foreach (var b in bins) total += b.Hours;
        var list = new List<RoseSector>(16);
        for (int s = 0; s < 16; s++)
        {
            int hours = 0;
            double speedHours = 0, dirSum = 0;
            int parts = 0;
            foreach (var b in bins)
            {
                if (b.Sector != s) continue;
                hours += b.Hours;
                speedHours += (double)b.SpeedMid * b.Hours;
                dirSum += b.DirectionDeg;
                parts++;
            }
            float dir = parts > 0 ? (float)(dirSum / parts) : s * 22.5f;
            list.Add(new RoseSector(s, dir, hours > 0 ? (float)(speedHours / hours) : 0f, hours,
                total > 0 ? (float)hours / total : 0f));
        }
        return list;
    }

    /// <summary>Sixteen clickable wedges drawn straight into a Canvas: radius and fill both read hours, so the
    /// prevailing wind is the long bright arm. North is up, east is right, matching the model axes.
    /// ponytail: a polygon per wedge rather than an arc segment — 7 points is smooth enough at 130 px and needs
    /// no arc-flag arithmetic. UI thread only.</summary>
    public static void DrawRose(Canvas canvas, IReadOnlyList<RoseSector> sectors, Action<RoseSector> onClick)
    {
        canvas.Children.Clear();
        double size = canvas.Width > 0 ? canvas.Width : 132;
        double cx = size / 2, cy = size / 2, radius = size / 2 - 8;
        int peak = 1;
        foreach (var s in sectors) peak = Math.Max(peak, s.Hours);

        canvas.Children.Add(new Avalonia.Controls.Shapes.Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x46)),
            StrokeThickness = 1,
            [Canvas.LeftProperty] = cx - radius,
            [Canvas.TopProperty] = cy - radius,
        });

        foreach (var s in sectors)
        {
            double frac = Math.Sqrt(s.Hours / (double)peak);
            double r = radius * (0.16 + 0.84 * frac);      // a calm sector still leaves something to click
            var points = new List<Point> { new(cx, cy) };
            for (int k = 0; k <= 6; k++)
            {
                double deg = s.DirectionDeg - 11.25 + 22.5 * k / 6.0;
                double rad = deg * Math.PI / 180;
                points.Add(new Point(cx + Math.Sin(rad) * r, cy - Math.Cos(rad) * r));
            }
            var wedge = new Avalonia.Controls.Shapes.Polygon
            {
                Points = points,
                Fill = new SolidColorBrush(Palette.Viridis(0.18 + 0.78 * frac), 0.34 + 0.56 * frac),
                Stroke = new SolidColorBrush(Color.FromRgb(0x0E, 0x14, 0x1D)),
                StrokeThickness = 0.8,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            var sector = s;
            ToolTip.SetTip(wedge, $"{Bearing(s.DirectionDeg)} · {s.DirectionDeg:0}° · {s.Hours:N0} h " +
                                  $"({s.Share * 100:0.#} %) · mean {s.MeanSpeed:0.0} m/s\nClick to set the live wind to this sector.");
            wedge.PointerPressed += (_, e) => { e.Handled = true; onClick(sector); };
            canvas.Children.Add(wedge);
        }

        foreach (var (label, dx, dy) in new (string, double, double)[]
                 { ("N", 0, -1), ("E", 1, 0), ("S", 0, 1), ("W", -1, 0) })
            canvas.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB0, 0xC6)),
                [Canvas.LeftProperty] = cx + dx * (radius + 1) - (dx < 0 ? 9 : dx > 0 ? -2 : 3),
                [Canvas.TopProperty] = cy + dy * (radius + 1) - (dy < 0 ? 11 : dy > 0 ? -1 : 6),
            });
    }

    // ---------------------------------------------------------------- PDF report

    /// <summary>Plan at 1.1 m of every analysed storey, one long section, and the compare difference when a
    /// hold pair is active; then the PDF. Runs on a background thread: Viz documents that DrawingVisual and
    /// RenderTargetBitmap need no Dispatcher, and the bitmaps it returns are frozen. progress gets a short
    /// "what is being drawn (k of n)" line before each step; it is called on this (background) thread.</summary>
    public static int BuildReport(SimulationResult r, SimulationResult? compareA,
        string path, ReportOptions options, Action<string>? progress = null, BitmapSource? cover = null)
    {
        string windLabel = ReportWind.Text(r.Wind, r.Model.NorthDeg);      // the solved wind, not the Wind boxes (P13b-05)
        var images = new List<(string Title, BitmapSource Image)>();
        var storeys = Storeys(r);
        bool difference = compareA != null && SameGrid(compareA, r);
        int steps = storeys.Count + 2 + (difference ? 1 : 0), step = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timings = new List<string>();
        void Next(string what)
        {
            if (step > 0) { timings.Add($"{sw.ElapsedMilliseconds}"); sw.Restart(); }
            progress?.Invoke($"{what} ({++step} of {steps})");
        }

        foreach (var (level, z) in storeys)
        {
            Next($"drawing the plan of {(level.Length > 0 ? level : "the building")}");
            images.Add(($"Plan · {(level.Length > 0 ? level : "building")} · {z:0.0} m",
                VizRenderer.Plan(r, new PlanOptions
                {
                    Z = z,
                    Level = storeys.Count > 1 ? level : null,
                    Field = FieldKind.Speed,
                    Title = $"{(level.Length > 0 ? level : r.Model.Name)} · {z:0.0} m · wind {windLabel}",
                })));
        }

        Next("drawing the section");
        var (min, max) = Voxelizer.PlanBounds(r.Model);
        float midY = (min.Y + max.Y) * 0.5f;
        images.Add(("Section · long axis", VizRenderer.Section(r, new Vector2(min.X, midY), new Vector2(max.X, midY),
            new SectionOptions { Field = FieldKind.Speed, Title = $"Section east–west · wind {windLabel}" })));

        if (difference)
        {
            Next("drawing the difference");
            images.Add(("Difference · B − A", VizRenderer.Difference(compareA!, r, new PlanOptions
            {
                Z = storeys[0].Z,
                Field = FieldKind.Speed,
                Title = $"{storeys[0].Z:0.0} m · held A vs B",
            })));
        }

        Next("writing the PDF");
        ReportBuilder.Build(r, images, path, options, cover);
        timings.Add($"{sw.ElapsedMilliseconds}");
        Log.Info($"report timing ms (plans…, section, [difference,] pdf): {string.Join(" / ", timings)}");
        return images.Count;
    }

    /// <summary>The analysed storeys and the cut height for each: 1.1 m above the lowest floor of that level,
    /// which is the height the plan images are specified at. One entry when the model has no levels.</summary>
    public static List<(string Level, float Z)> Storeys(SimulationResult r)
    {
        var lowest = new Dictionary<string, float>();
        var order = new List<string>();
        foreach (var room in r.Rooms)
        {
            string level = room.Room.Level ?? "";
            if (lowest.TryGetValue(level, out float z)) lowest[level] = MathF.Min(z, room.Room.ZMin);
            else { lowest[level] = room.Room.ZMin; order.Add(level); }
        }
        if (order.Count == 0) return [("", r.Model.GroundZ + 1.1f)];
        order.Sort((a, b) => lowest[a].CompareTo(lowest[b]));
        return order.Select(level => (level, lowest[level] + 1.1f)).ToList();
    }

    /// <summary>Page count straight out of the file: PDFsharp cannot rasterise a page, so a byte scan for the
    /// page objects is how a headless check proves the PDF is really a multi-page document.
    /// ponytail: counts "/Type /Page" objects, which is exactly what a writer emits; it would miscount a
    /// hand-crafted PDF that hides them in an object stream.</summary>
    public static int PdfPageCount(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int count = 0;
        foreach (var needle in new[] { "/Type /Page", "/Type/Page" })
        {
            var probe = System.Text.Encoding.ASCII.GetBytes(needle);
            for (int i = 0; i + probe.Length < bytes.Length; i++)
            {
                bool hit = true;
                for (int k = 0; k < probe.Length && hit; k++) hit = bytes[i + k] == probe[k];
                if (hit && bytes[i + probe.Length] != (byte)'s') count++;   // /Pages is the tree, not a page
            }
        }
        return count;
    }
}

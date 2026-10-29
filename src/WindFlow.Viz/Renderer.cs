using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindFlow.Core;

namespace WindFlow.Viz;

/// <summary>What the coloured raster shows. Speed/Pressure come from the time-averaged flow field (or a
/// snapshot); Temperature needs RunOptions.Thermal, CO2/Age/Contaminant need AirQuality, PMV/PPD/DraughtRate
/// need Comfort, and Q comes from Vorticity.Q. A field the run did not compute renders as a "no data" panel
/// rather than a misleading image.</summary>
public enum FieldKind { Speed, Pressure, Temperature, Co2, Age, Contaminant, Pmv, Ppd, DraughtRate, Q }

public sealed class PlanOptions
{
    public float Z = 1.1f;                  // absolute model Z of the horizontal cut
    public float PixelsPerMetre = 40;
    public FieldKind Field = FieldKind.Speed;
    public bool Streamlines = true, Arrows = false, Contours = false, Labels = true;
    public string Title = "";               // e.g. "Level 1 · 1.1 m · wind SW 3 m/s"
    public string? Level;                   // limit room labels / building outline to this storey (null = all analysed)

    /// <summary>−1 = the time average. Otherwise the index into FlowField.Snapshots to draw: Speed and
    /// Temperature (and the arrow glyphs) come from that instant and the title band gains "t = 12.4 s".
    /// Every other field only exists as a time average and ignores this. Out-of-range falls back to the average.</summary>
    public int SnapshotIndex = -1;

    /// <summary>Model-space point to probe: a crosshair plus a box with everything Analysis.Probe knows there.</summary>
    public Vector3? Probe;
}

public sealed class SectionOptions
{
    public float PixelsPerMetre = 40;
    public FieldKind Field = FieldKind.Speed;
    public bool Streamlines = true, Arrows = true, Labels = true;
    public string Title = "";
    public float? ZMin, ZMax;               // vertical extent; null = the analysed storeys plus 1 m
    public int SnapshotIndex = -1;          // as PlanOptions.SnapshotIndex
}

/// <summary>Report images: a horizontal plan slice and a vertical section, drawn with WPF
/// (DrawingVisual -> RenderTargetBitmap) so text and lines are anti-aliased.
/// Thread affinity: verified to work both from a [STAThread] Main and from a Task.Run worker
/// (MTA) thread — RenderTargetBitmap, DrawingVisual and FormattedText need no Dispatcher pump, so no
/// internal STA thread is used. The returned bitmaps are frozen and safe to hand to any thread.</summary>
public static class Renderer
{
    /// <summary>Horizontal cut at o.Z, north (+Y) up, with legend. Frozen bitmap, safe to use from any thread.</summary>
    public static BitmapSource Plan(SimulationResult r, PlanOptions o)
    {
        var (min, max) = Voxelizer.PlanBounds(r.Model);
        float m = (float)RenderKit.Margin;
        var f = Frame.Create(o.PixelsPerMetre, min.X - m, max.X + m, min.Y - m, max.Y + m, 0, RenderKit.TitleH);
        double totalW = f.W + RenderKit.LegendW, totalH = RenderKit.TitleH + f.H;

        var snap = SnapshotOf(r.Field, o.SnapshotIndex);
        var scale = FieldScale.For(r, o.Field, o.Z, snap);
        float colorMax = MathF.Max(0.05f, r.ColorMax);
        float z = o.Z;
        var velocity = Velocity(r.Field, snap);

        var visual = new DrawingVisual();
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(RenderKit.Paper, null, new Rect(0, 0, totalW, totalH));
            dc.PushClip(new RectangleGeometry(f.Area));

            if (scale.Available)
            {
                var field = RenderKit.RenderField(f, (u, v) => new Vector3((float)u, (float)v, z),
                    r.Field.Grid, scale, out var scalar);
                dc.DrawImage(field, f.Area);
                if (o.Contours)
                {
                    if (scale.Levels.Length > 0) { RenderKit.DrawContours(dc, f, scalar, scale.Levels); scale.Note = IsoNote(o.Field); }
                    else RenderKit.DrawContours(dc, f, scalar, scale.Lo, scale.Hi);
                }
            }
            else RenderKit.NoData(dc, f, (u, v) => new Vector3((float)u, (float)v, z), r.Field.Grid);

            if (o.Arrows) PlanArrows(dc, r, f, z, colorMax, velocity);
            if (o.Streamlines) PlanStreamlines(dc, r, f, z, colorMax);
            if (o.Labels) PlanOverlays(dc, r, f, o);
            if (o.Probe is Vector3 probe) PlanProbe(dc, r, f, probe);
            if (!scale.Available) RenderKit.NoDataLabel(dc, f, scale.Message);

            RenderKit.Wind(dc, f, r.Wind);
            RenderKit.North(dc, f, r.Model.NorthDeg);
            dc.Pop();

            dc.DrawRectangle(null, RenderKit.FramePen, f.Area);
            RenderKit.Legend(dc, totalW, totalH, scale);
            RenderKit.TitleBand(dc, totalW, WithTime(o.Title, snap), r.Field, r.Ledger);
        }
        return Rasterise(visual, (int)totalW, (int)totalH);
    }

    /// <summary>field(b) − field(a) on the same plan cut: blue where b is lower, red where it is higher, with
    /// a symmetric range from the 98th percentile of |difference|. Both runs must share a grid.</summary>
    public static BitmapSource Difference(SimulationResult a, SimulationResult b, PlanOptions o)
    {
        var ga = a.Field.Grid;
        var gb = b.Field.Grid;
        if (ga.Nx != gb.Nx || ga.Ny != gb.Ny || ga.Nz != gb.Nz
            || MathF.Abs(ga.Dx - gb.Dx) > 1e-6f || Vector3.Distance(ga.Origin, gb.Origin) > 1e-4f)
            throw new ArgumentException("Compare requires the same grid; run both at the same quality");

        var (min, max) = Voxelizer.PlanBounds(b.Model);
        float m = (float)RenderKit.Margin;
        var f = Frame.Create(o.PixelsPerMetre, min.X - m, max.X + m, min.Y - m, max.Y + m, 0, RenderKit.TitleH);
        double totalW = f.W + RenderKit.LegendW, totalH = RenderKit.TitleH + f.H;

        var sa = FieldScale.For(a, o.Field, o.Z, null);
        var sb = FieldScale.For(b, o.Field, o.Z, null);
        float z = o.Z;
        string title = string.Format(CultureInfo.InvariantCulture, "B − A · {0}", sb.Label);
        if (!string.IsNullOrWhiteSpace(o.Title)) title = o.Title + " · " + title;

        var visual = new DrawingVisual();
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(RenderKit.Paper, null, new Rect(0, 0, totalW, totalH));
            dc.PushClip(new RectangleGeometry(f.Area));

            var legend = new FieldScale
            {
                Kind = o.Field, Label = "Δ " + sb.Label, Unit = sb.Unit, TickFormat = sb.TickFormat,
                Ramp = Colormap.Diverging, Note = "B − A, blue = lower in B",
                Available = sa.Available && sb.Available,
                Message = sa.Available ? sb.Message : sa.Message,
            };

            if (legend.Available)
            {
                var img = RenderKit.RenderDifference(f, (u, v) => new Vector3((float)u, (float)v, z), gb,
                    p => sb.Sample(p) - sa.Sample(p), out var scalar, out float range);
                dc.DrawImage(img, f.Area);
                legend.Lo = -range; legend.Hi = range;
                if (o.Contours) RenderKit.DrawContours(dc, f, scalar, -range, range);
            }
            else RenderKit.NoData(dc, f, (u, v) => new Vector3((float)u, (float)v, z), gb);

            if (o.Labels) DifferenceOverlays(dc, a, b, f, o);
            if (!legend.Available) RenderKit.NoDataLabel(dc, f, legend.Message);
            RenderKit.North(dc, f, b.Model.NorthDeg);
            dc.Pop();

            dc.DrawRectangle(null, RenderKit.FramePen, f.Area);
            RenderKit.Legend(dc, totalW, totalH, legend);
            RenderKit.TitleBand(dc, totalW, title, b.Field, b.Ledger);
        }
        return Rasterise(visual, (int)totalW, (int)totalH);
    }

    /// <summary>One plan per snapshot, in time order; empty when the run stored none.</summary>
    public static IReadOnlyList<BitmapSource> Frames(SimulationResult r, PlanOptions o)
    {
        var frames = new List<BitmapSource>();
        for (int i = 0; i < r.Field.Snapshots.Count; i++) frames.Add(Plan(r, Copy(o, i)));
        return frames;
    }

    static PlanOptions Copy(PlanOptions o, int snapshotIndex) => new()
    {
        Z = o.Z, PixelsPerMetre = o.PixelsPerMetre, Field = o.Field,
        Streamlines = o.Streamlines, Arrows = o.Arrows, Contours = o.Contours, Labels = o.Labels,
        Title = o.Title, Level = o.Level, Probe = o.Probe, SnapshotIndex = snapshotIndex,
    };

    /// <summary>Animated GIF from frames of the same size, written with WPF's own GifBitmapEncoder.
    /// That encoder stores no timing, so the bytes are post-processed: a NETSCAPE2.0 application extension
    /// (infinite loop) after the global colour table and a Graphic Control Extension carrying the delay in
    /// front of every image descriptor.</summary>
    public static void SaveGif(IReadOnlyList<BitmapSource> frames, string path, int delayMs)
    {
        if (frames == null || frames.Count == 0) throw new ArgumentException("No frames to save", nameof(frames));
        var encoder = new GifBitmapEncoder();
        foreach (var frame in frames) encoder.Frames.Add(BitmapFrame.Create(frame));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);

        var bytes = GifTiming(buffer.ToArray(), delayMs);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Walks the GIF block structure (never a blind byte scan: 0x2C also occurs inside pixel data)
    /// and rewrites it with looping and per-frame delay.</summary>
    static byte[] GifTiming(byte[] gif, int delayMs)
    {
        int cs = Math.Clamp((int)Math.Round(delayMs / 10.0), 1, 0xFFFF);   // GIF delays are in 1/100 s
        if (gif.Length < 14 || gif[0] != 'G' || gif[1] != 'I' || gif[2] != 'F') return gif;

        var o = new MemoryStream(gif.Length + 64);
        o.Write(gif, 0, 13);                        // header (6) + logical screen descriptor (7)
        int i = 13;
        byte screen = gif[10];
        if ((screen & 0x80) != 0) { int n = 3 << ((screen & 7) + 1); o.Write(gif, i, n); i += n; }   // global colour table

        o.Write(new byte[] { 0x21, 0xFF, 0x0B, (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C',
            (byte)'A', (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0', 0x03, 0x01, 0x00, 0x00, 0x00 });

        bool closed = false;
        while (i < gif.Length)
        {
            byte block = gif[i];
            if (block == 0x3B) { closed = true; o.WriteByte(0x3B); break; }
            if (block == 0x21)                      // extension: 0x21, label, sub-blocks, 0x00
            {
                int start = i;
                byte label = gif[i + 1];
                i += 2;
                while (i < gif.Length && gif[i] != 0) i += gif[i] + 1;
                i++;
                if (label != 0xF9) o.Write(gif, start, Math.Min(i, gif.Length) - start);   // drop old GCEs
                continue;
            }
            if (block == 0x2C)                      // image descriptor: 0x2C + 9 bytes
            {
                o.Write(new byte[] { 0x21, 0xF9, 0x04, 0x00, (byte)(cs & 0xFF), (byte)(cs >> 8), 0x00, 0x00 });
                int start = i;
                byte packed = gif[i + 9];
                i += 10;
                if ((packed & 0x80) != 0) i += 3 << ((packed & 7) + 1);   // local colour table
                i++;                                                     // LZW minimum code size
                while (i < gif.Length && gif[i] != 0) i += gif[i] + 1;
                i++;
                o.Write(gif, start, Math.Min(i, gif.Length) - start);
                continue;
            }
            break;                                  // unexpected byte: stop rewriting, close cleanly
        }
        if (!closed) o.WriteByte(0x3B);
        return o.ToArray();
    }

    static Snapshot? SnapshotOf(FlowField field, int index)
        => index >= 0 && index < field.Snapshots.Count ? field.Snapshots[index] : null;

    static Func<Vector3, Vector3> Velocity(FlowField field, Snapshot? snap)
        => snap == null || snap.Ux.Length < field.Grid.Count
            ? p => Analysis.SampleVelocity(field, p)
            : p => RenderKit.SampleVelocity(field.Grid, snap, p);

    static string WithTime(string title, Snapshot? snap)
    {
        if (snap == null) return title;
        string t = string.Format(CultureInfo.InvariantCulture, "t = {0:0.0} s", snap.Time);
        return string.IsNullOrWhiteSpace(title) ? t : title + " · " + t;
    }

    static string IsoNote(FieldKind kind) => kind == FieldKind.Temperature ? "iso-lines every 1 K" : "";

    /// <summary>Vertical cut along the line a→b (model XY), a on the left, Z up, with legend.</summary>
    public static BitmapSource Section(SimulationResult r, Vector2 a, Vector2 b, SectionOptions o)
    {
        var d = b - a;
        float length = d.Length();
        if (length < 1e-4f) { d = new Vector2(1, 0); length = 1; }
        var t = d / length;

        var analysed = r.Model.Storeys.Where(s => s.Analysed).ToList();
        float zLo = o.ZMin ?? (analysed.Count > 0 ? analysed.Min(s => s.Elevation) - 0.5f : r.Model.GroundZ);
        float zHi = o.ZMax ?? (analysed.Count > 0 ? analysed.Max(s => s.Elevation + s.Height) + 1.5f : r.Model.GroundZ + 4f);
        if (zHi - zLo < 1f) zHi = zLo + 1f;

        var f = Frame.Create(o.PixelsPerMetre, 0, length, zLo, zHi, 0, RenderKit.TitleH);
        double totalW = f.W + RenderKit.LegendW, totalH = RenderKit.TitleH + f.H;

        var snap = SnapshotOf(r.Field, o.SnapshotIndex);
        var scale = FieldScale.For(r, o.Field, null, snap);
        float colorMax = MathF.Max(0.05f, r.ColorMax);
        var velocity = Velocity(r.Field, snap);
        Vector3 World(double u, double v) => new(a.X + t.X * (float)u, a.Y + t.Y * (float)u, (float)v);

        var visual = new DrawingVisual();
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(RenderKit.Paper, null, new Rect(0, 0, totalW, totalH));
            dc.PushClip(new RectangleGeometry(f.Area));

            if (scale.Available)
            {
                var field = RenderKit.RenderField(f, World, r.Field.Grid, scale, out var scalar);
                dc.DrawImage(field, f.Area);
                // A section of a warm room is mostly read off its stratification, so temperature always gets
                // its 1 K iso-lines (the plan keeps them behind PlanOptions.Contours).
                if (scale.Levels.Length > 0)
                {
                    RenderKit.DrawContours(dc, f, scalar, scale.Levels);
                    scale.Note = IsoNote(o.Field);
                }
            }
            else RenderKit.NoData(dc, f, World, r.Field.Grid);

            if (o.Arrows) SectionArrows(dc, r, f, World, t, colorMax, velocity);
            if (o.Streamlines) SectionStreamlines(dc, r, f, World, t, colorMax);
            if (o.Labels) SectionOverlays(dc, r, f, a, t, length);
            if (!scale.Available) RenderKit.NoDataLabel(dc, f, scale.Message);
            dc.Pop();

            dc.DrawRectangle(null, RenderKit.FramePen, f.Area);
            RenderKit.Legend(dc, totalW, totalH, scale);
            RenderKit.TitleBand(dc, totalW, WithTime(o.Title, snap), r.Field, r.Ledger);
        }
        return Rasterise(visual, (int)totalW, (int)totalH);
    }

    public static void SavePng(BitmapSource bitmap, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        encoder.Save(stream);
    }

    static BitmapSource Rasterise(DrawingVisual visual, int w, int h)
    {
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    // ---------------- plan layers ----------------

    static void PlanStreamlines(DrawingContext dc, SimulationResult r, Frame f, float z, float colorMax)
    {
        const float band = 0.7f;
        var lines = new List<(Point[], float[])>();
        foreach (var s in r.Streamlines)
        {
            if (s.Points.Length < 2) continue;
            var run = new List<Point>();
            var spd = new List<float>();
            for (int i = 0; i < s.Points.Length; i++)
            {
                var p = s.Points[i];
                if (MathF.Abs(p.Z - z) <= band)
                {
                    run.Add(f.Px(p.X, p.Y));
                    spd.Add(i < s.Speeds.Length ? s.Speeds[i] : 0f);
                }
                else
                {
                    if (run.Count >= 2) lines.Add((run.ToArray(), spd.ToArray()));
                    run.Clear(); spd.Clear();
                }
            }
            if (run.Count >= 2) lines.Add((run.ToArray(), spd.ToArray()));
        }
        RenderKit.FlowLines(dc, lines, colorMax, 2.0 * f.Ppm);
    }

    static void PlanArrows(DrawingContext dc, SimulationResult r, Frame f, float z, float colorMax,
        Func<Vector3, Vector3> velocity)
    {
        var g = r.Field.Grid;
        var pens = new RenderKit.PenCache(1.4);
        for (double y = Snap(f.VMin, 0.6); y <= f.VMax; y += 0.6)
        for (double x = Snap(f.UMin, 0.6); x <= f.UMax; x += 0.6)
        {
            var p = new Vector3((float)x, (float)y, z);
            int cell = RenderKit.CellAt(g, p);
            if (cell < 0 || g.Cell[cell] == VoxelGrid.Solid) continue;
            var v = velocity(p);
            var flat = new Vector2(v.X, v.Y);
            float speed = flat.Length();
            if (speed < 0.03f) continue;
            DrawFieldArrow(dc, f, x, y, flat / speed, speed, colorMax, pens, 0.55);
        }
    }

    static void DrawFieldArrow(DrawingContext dc, Frame f, double u, double v, Vector2 dir, float speed,
        float colorMax, RenderKit.PenCache pens, double maxMetres)
    {
        double len = f.Px(Math.Clamp(speed / colorMax, 0.2, 1.0) * maxMetres);
        var c = f.Px(u, v);
        var p0 = new Point(c.X - dir.X * len * 0.5, c.Y + dir.Y * len * 0.5);
        var p1 = new Point(c.X + dir.X * len * 0.5, c.Y - dir.Y * len * 0.5);
        var pen = pens.Get(speed / colorMax);
        RenderKit.Arrow(dc, p0, p1, pen, pen.Brush, Math.Min(6.0, len * 0.55), outline: true);
    }

    static double Snap(double lo, double step) => Math.Ceiling(lo / step) * step;

    static void PlanOverlays(DrawingContext dc, SimulationResult r, Frame f, PlanOptions o)
    {
        DrawRooms(dc, r.Model, f, o.Level, room =>
        {
            var rr = RoomResultFor(r, room);
            // Fresh-air ACH (from the air age); a room without one is labelled by name only, never with its total exchange.
            return rr != null && !float.IsNaN(rr.FreshAch) && !rr.AgeSealed && !rr.ExchangeOnly
                ? string.Format(CultureInfo.InvariantCulture, "{0} · {1:0.#} fresh-air ACH", room.Name, rr.FreshAch)
                : room.Name;
        });
        PlanDevices(dc, r, f, o);
    }

    static RoomResult? RoomResultFor(SimulationResult r, Room room) => r.Rooms.FirstOrDefault(
        x => ReferenceEquals(x.Room, room)
             || (x.Room.Id.Length > 0 && x.Room.Id == room.Id)
             || (x.Room.Id.Length == 0 && room.Id.Length == 0 && x.Room.Name == room.Name));

    /// <summary>Room outlines with a caller-supplied label (ACH on a plan, ΔACH on a difference image).</summary>
    static void DrawRooms(DrawingContext dc, BuildingModel model, Frame f, string? level, Func<Room, string> label)
    {
        foreach (var room in VisibleRooms(model, level))
        {
            Vector2[]? biggest = null;
            double bestArea = 0;
            foreach (var loop in room.Loops)
            {
                if (loop.Length < 3) continue;
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(f.Px(loop[0].X, loop[0].Y), false, true);
                    ctx.PolyLineTo(loop.Skip(1).Select(p => f.Px(p.X, p.Y)).ToArray(), true, true);
                }
                geo.Freeze();
                dc.DrawGeometry(null, RenderKit.RoomPen, geo);
                double area = Math.Abs(SignedArea(loop));
                if (area > bestArea) { bestArea = area; biggest = loop; }
            }
            if (biggest == null) continue;

            var c = Centroid(biggest);
            var at = f.Px(c.X, c.Y);
            RenderKit.HaloText(dc, label(room), new Point(at.X, at.Y - 7), 11, RenderKit.White, centred: true);
        }
    }

    /// <summary>Openings and mechanical sources near the cut.</summary>
    static void PlanDevices(DrawingContext dc, SimulationResult r, Frame f, PlanOptions o)
    {
        foreach (var op in r.Model.Openings)
        {
            if (MathF.Abs(op.Center.Z - o.Z) > 1.6f) continue;
            var n = new Vector2(op.Normal.X, op.Normal.Y);
            if (n.LengthSquared() < 1e-8f) n = new Vector2(1, 0);
            n = Vector2.Normalize(n);
            var tangent = new Vector2(-n.Y, n.X);
            float half = MathF.Max(0.2f, op.Width * 0.5f);
            var a = f.Px(op.Center.X - tangent.X * half, op.Center.Y - tangent.Y * half);
            var b = f.Px(op.Center.X + tangent.X * half, op.Center.Y + tangent.Y * half);
            var brush = op.IsOpen ? RenderKit.Cyan : RenderKit.Red;
            var pen = new Pen(brush, 3) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            pen.Freeze();
            dc.DrawLine(RenderKit.TickHalo, a, b);
            dc.DrawLine(pen, a, b);
            if (!string.IsNullOrEmpty(op.Name))
            {
                var mid = f.Px(op.Center.X + n.X * 0.35f, op.Center.Y + n.Y * 0.35f);
                RenderKit.HaloText(dc, op.Name, new Point(mid.X, mid.Y - 6), 9, RenderKit.White, centred: true);
            }
        }

        foreach (var s in r.Model.Sources)
        {
            if (MathF.Abs(s.Center.Z - o.Z) > 1.6f) continue;
            var dir = s.Direction.LengthSquared() > 1e-8f ? Vector3.Normalize(s.Direction) : new Vector3(0, 0, -1);
            bool horizontalPatch = MathF.Abs(dir.Z) > 0.7f;       // patch lies flat: Width x Height show in plan
            float w = horizontalPatch ? MathF.Max(0.2f, s.Width) : 0.4f;
            float h = horizontalPatch ? MathF.Max(0.2f, s.Height) : 0.4f;
            var tl = f.Px(s.Center.X - w * 0.5f, s.Center.Y + h * 0.5f);
            var br = f.Px(s.Center.X + w * 0.5f, s.Center.Y - h * 0.5f);
            var fill = new SolidColorBrush(Color.FromArgb(70, 0xE0, 0x2D, 0xC8)); fill.Freeze();
            var pen = new Pen(RenderKit.Magenta, 1.6); pen.Freeze();
            dc.DrawRectangle(fill, RenderKit.RectHalo, new Rect(tl, br));
            dc.DrawRectangle(null, pen, new Rect(tl, br));

            var c = f.Px(s.Center.X, s.Center.Y);
            var flat = new Vector2(dir.X, dir.Y);
            if (flat.Length() > 0.15f)
            {
                flat = Vector2.Normalize(flat);
                double len = f.Px(0.7);
                RenderKit.Arrow(dc, c, new Point(c.X + flat.X * len, c.Y - flat.Y * len), pen, RenderKit.Magenta, 7);
            }
            else
            {
                dc.DrawEllipse(RenderKit.Magenta, null, c, 2.2, 2.2);
                RenderKit.HaloText(dc, dir.Z < 0 ? "▼" : "▲", new Point(c.X, c.Y + 3), 11, RenderKit.Magenta, centred: true, halo: RenderKit.White);
            }
            if (!string.IsNullOrEmpty(s.Name))
                RenderKit.HaloText(dc, s.Name, new Point(c.X, br.Y + 3), 9, RenderKit.White, centred: true);
        }
    }

    /// <summary>Crosshair at the probe point plus a box with every finite value Analysis.Probe reports there.
    /// The crosshair is drawn even when the probe itself is unavailable, so the point stays visible.</summary>
    static void PlanProbe(DrawingContext dc, SimulationResult r, Frame f, Vector3 p)
    {
        var lines = new List<string>();
        try
        {
            var v = Analysis.Probe(r, p);
            if (!string.IsNullOrEmpty(v.Room)) lines.Add(v.Room);
            Add(lines, "speed", v.Speed, "0.00", "m/s");
            Add(lines, "p", v.Pressure, "0.0", "Pa");
            Add(lines, "T", v.Temperature, "0.0", "°C");
            Add(lines, "CO₂", v.Co2, "0", "ppm");
            Add(lines, "age", v.Age, "0", "s");
            Add(lines, "PMV", v.Pmv, "0.00", "");
            Add(lines, "DR", v.Dr, "0", "%");
        }
        catch (NotImplementedException) { lines.Clear(); }   // the probe has not landed yet: crosshair only

        RenderKit.ValueBox(dc, f, f.Px(p.X, p.Y), lines);

        static void Add(List<string> lines, string name, float value, string format, string unit)
        {
            if (!float.IsFinite(value)) return;
            string text = value.ToString(format, CultureInfo.InvariantCulture);
            lines.Add(unit.Length > 0 ? $"{name}  {text} {unit}" : $"{name}  {text}");
        }
    }

    /// <summary>Room outlines labelled with the change in fresh-air air changes per hour, B − A.</summary>
    static void DifferenceOverlays(DrawingContext dc, SimulationResult a, SimulationResult b, Frame f, PlanOptions o)
    {
        DrawRooms(dc, b.Model, f, o.Level, room =>
        {
            var rb = RoomResultFor(b, room);
            var ra = a.Rooms.FirstOrDefault(x => (x.Room.Id.Length > 0 && x.Room.Id == room.Id)
                                                 || x.Room.Name == room.Name);
            if (rb == null || ra == null || float.IsNaN(rb.FreshAch) || float.IsNaN(ra.FreshAch)) return room.Name;
            return string.Format(CultureInfo.InvariantCulture, "{0} {1:+0.0;−0.0;0} fresh-air ACH", room.Name, rb.FreshAch - ra.FreshAch);
        });
        PlanDevices(dc, b, f, o);
    }

    static IEnumerable<Room> VisibleRooms(BuildingModel model, string? level)
    {
        if (!string.IsNullOrEmpty(level))
            return model.Rooms.Where(x => string.Equals(x.Level, level, StringComparison.OrdinalIgnoreCase));

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in model.Storeys.Where(s => s.Analysed))
        {
            if (s.Id.Length > 0) keys.Add(s.Id);
            if (s.Name.Length > 0) keys.Add(s.Name);
        }
        if (keys.Count == 0) return model.Rooms;
        return model.Rooms.Where(x => x.Level.Length == 0 || keys.Contains(x.Level));
    }

    static double SignedArea(Vector2[] loop)
    {
        double s = 0;
        for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
            s += (double)loop[j].X * loop[i].Y - (double)loop[i].X * loop[j].Y;
        return s * 0.5;
    }

    static Vector2 Centroid(Vector2[] loop)
    {
        double area = SignedArea(loop);
        if (Math.Abs(area) < 1e-9)
        {
            var sum = Vector2.Zero;
            foreach (var p in loop) sum += p;
            return sum / Math.Max(1, loop.Length);
        }
        double cx = 0, cy = 0;
        for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
        {
            double cross = (double)loop[j].X * loop[i].Y - (double)loop[i].X * loop[j].Y;
            cx += (loop[j].X + loop[i].X) * cross;
            cy += (loop[j].Y + loop[i].Y) * cross;
        }
        return new Vector2((float)(cx / (6 * area)), (float)(cy / (6 * area)));
    }

    // ---------------- section layers ----------------

    static void SectionArrows(DrawingContext dc, SimulationResult r, Frame f, Func<double, double, Vector3> world,
        Vector2 t, float colorMax, Func<Vector3, Vector3> velocity)
    {
        var g = r.Field.Grid;
        var pens = new RenderKit.PenCache(1.4);
        for (double v = Snap(f.VMin, 0.5); v <= f.VMax; v += 0.5)
        for (double u = Snap(f.UMin, 0.5); u <= f.UMax; u += 0.5)
        {
            var p = world(u, v);
            int cell = RenderKit.CellAt(g, p);
            if (cell < 0 || g.Cell[cell] == VoxelGrid.Solid) continue;
            var vel = velocity(p);
            var plane = new Vector2(vel.X * t.X + vel.Y * t.Y, vel.Z);
            float speed = plane.Length();
            if (speed < 0.03f) continue;
            DrawFieldArrow(dc, f, u, v, plane / speed, speed, colorMax, pens, 0.45);
        }
    }

    static void SectionStreamlines(DrawingContext dc, SimulationResult r, Frame f, Func<double, double, Vector3> world,
        Vector2 t, float colorMax)
    {
        var g = r.Field.Grid;
        var traced = new List<(Point[], float[])>();

        for (double v0 = Snap(f.VMin, 0.5); v0 <= f.VMax; v0 += 0.5)
        for (double u0 = Snap(f.UMin, 0.5); u0 <= f.UMax; u0 += 0.5)
        {
            var seed = world(u0, v0);
            int cell = RenderKit.CellAt(g, seed);
            if (cell < 0 || g.Cell[cell] == VoxelGrid.Solid) continue;

            var back = TracePlane(r.Field, f, world, t, u0, v0, -1);
            var fwd = TracePlane(r.Field, f, world, t, u0, v0, +1);
            if (back.Count + fwd.Count < 6) continue;

            var pts = new List<Point>(back.Count + fwd.Count + 1);
            var spd = new List<float>(pts.Capacity);
            for (int i = back.Count - 1; i >= 0; i--) { pts.Add(f.Px(back[i].U, back[i].V)); spd.Add(back[i].Speed); }
            pts.Add(f.Px(u0, v0));
            spd.Add(Analysis.SampleVelocity(r.Field, seed).Length());
            foreach (var s in fwd) { pts.Add(f.Px(s.U, s.V)); spd.Add(s.Speed); }
            traced.Add((pts.ToArray(), spd.ToArray()));
        }

        var kept = traced.OrderByDescending(l => l.Item1.Length).Take(120).ToList();
        RenderKit.FlowLines(dc, kept, colorMax, 2.0 * f.Ppm);
    }

    /// <summary>RK4 trace inside the cut plane using the in-plane velocity (u·t, u_z).</summary>
    static List<(double U, double V, float Speed)> TracePlane(FlowField field, Frame f, Func<double, double, Vector3> world,
        Vector2 t, double u, double v, double sign)
    {
        const double ds = 0.15;
        var result = new List<(double, double, float)>();
        var g = field.Grid;

        Vector2 Dir(double uu, double vv)
        {
            var vel = Analysis.SampleVelocity(field, world(uu, vv));
            var plane = new Vector2(vel.X * t.X + vel.Y * t.Y, vel.Z);
            float s = plane.Length();
            return s > 1e-9f ? plane * (float)(sign / s) : Vector2.Zero;
        }

        for (int step = 0; step < 600; step++)
        {
            var k1 = Dir(u, v);
            if (k1 == Vector2.Zero) break;
            var k2 = Dir(u + k1.X * ds * 0.5, v + k1.Y * ds * 0.5);
            var k3 = Dir(u + k2.X * ds * 0.5, v + k2.Y * ds * 0.5);
            var k4 = Dir(u + k3.X * ds, v + k3.Y * ds);
            double nu = u + (k1.X + 2 * k2.X + 2 * k3.X + k4.X) * ds / 6.0;
            double nv = v + (k1.Y + 2 * k2.Y + 2 * k3.Y + k4.Y) * ds / 6.0;

            if (nu < f.UMin || nu > f.UMax || nv < f.VMin || nv > f.VMax) break;
            var p = world(nu, nv);
            int cell = RenderKit.CellAt(g, p);
            if (cell < 0 || g.Cell[cell] == VoxelGrid.Solid) break;
            var vel = Analysis.SampleVelocity(field, p);
            if (new Vector2(vel.X * t.X + vel.Y * t.Y, vel.Z).Length() < 0.02f) break;

            result.Add((nu, nv, vel.Length()));
            u = nu; v = nv;
        }
        return result;
    }

    static void SectionOverlays(DrawingContext dc, SimulationResult r, Frame f, Vector2 a, Vector2 t, float length)
    {
        var dash = new Pen(new SolidColorBrush(Color.FromArgb(120, 0x22, 0x22, 0x22)), 0.9)
        { DashStyle = new DashStyle(new double[] { 5, 4 }, 0) };
        dash.Freeze();

        foreach (var s in r.Model.Storeys)
        {
            if (s.Elevation < f.VMin || s.Elevation > f.VMax) continue;
            var p0 = f.Px(f.UMin, s.Elevation);
            var p1 = f.Px(f.UMax, s.Elevation);
            dc.DrawLine(dash, p0, p1);
            if (!string.IsNullOrEmpty(s.Name))
                RenderKit.HaloText(dc, s.Name, new Point(p0.X + 6, p0.Y - 13), 9, RenderKit.White);
        }

        double Along(Vector3 c) => (c.X - a.X) * t.X + (c.Y - a.Y) * t.Y;
        double Off(Vector3 c) => Math.Abs((c.X - a.X) * -t.Y + (c.Y - a.Y) * t.X);

        foreach (var op in r.Model.Openings)
        {
            if (Off(op.Center) > 0.5) continue;
            double u = Along(op.Center);
            if (u < -0.5 || u > length + 0.5) continue;
            var n = new Vector2(op.Normal.X, op.Normal.Y);
            bool alongCut = n.LengthSquared() > 1e-8f && Math.Abs(Vector2.Dot(Vector2.Normalize(n), t)) > 0.7;
            double w = alongCut ? Math.Max(0.2f, op.Width) : 0.12;
            double h = Math.Max(0.2f, op.Height);
            var brush = op.IsOpen ? RenderKit.Cyan : RenderKit.Red;
            var pen = new Pen(brush, 1.6); pen.Freeze();
            var fill = new SolidColorBrush(Color.FromArgb(60, ((SolidColorBrush)brush).Color.R,
                ((SolidColorBrush)brush).Color.G, ((SolidColorBrush)brush).Color.B)); fill.Freeze();
            var rect = new Rect(f.Px(u - w * 0.5, op.Center.Z + h * 0.5), f.Px(u + w * 0.5, op.Center.Z - h * 0.5));
            dc.DrawRectangle(fill, RenderKit.RectHalo, rect);
            dc.DrawRectangle(null, pen, rect);
            if (!string.IsNullOrEmpty(op.Name))
                RenderKit.HaloText(dc, op.Name, new Point(rect.X + rect.Width * 0.5, rect.Y - 13), 9, RenderKit.White, centred: true);
        }

        foreach (var s in r.Model.Sources)
        {
            if (Off(s.Center) > 0.5) continue;
            double u = Along(s.Center);
            if (u < -0.5 || u > length + 0.5) continue;
            double w = Math.Max(0.3f, s.Width), h = Math.Max(0.15f, s.Height);
            var dir = s.Direction.LengthSquared() > 1e-8f ? Vector3.Normalize(s.Direction) : new Vector3(0, 0, -1);
            if (MathF.Abs(dir.Z) > 0.7f) h = 0.15;               // flat patch seen edge-on
            var fill = new SolidColorBrush(Color.FromArgb(70, 0xE0, 0x2D, 0xC8)); fill.Freeze();
            var pen = new Pen(RenderKit.Magenta, 1.6); pen.Freeze();
            var rect = new Rect(f.Px(u - w * 0.5, s.Center.Z + h * 0.5), f.Px(u + w * 0.5, s.Center.Z - h * 0.5));
            dc.DrawRectangle(fill, RenderKit.RectHalo, rect);
            dc.DrawRectangle(null, pen, rect);

            var c = new Point(rect.X + rect.Width * 0.5, rect.Y + rect.Height * 0.5);
            var plane = new Vector2(dir.X * t.X + dir.Y * t.Y, dir.Z);
            if (plane.Length() > 0.15f)
            {
                plane = Vector2.Normalize(plane);
                double len = f.Px(0.6);
                RenderKit.Arrow(dc, c, new Point(c.X + plane.X * len, c.Y - plane.Y * len), pen, RenderKit.Magenta, 7);
            }
            if (!string.IsNullOrEmpty(s.Name))
                RenderKit.HaloText(dc, s.Name, new Point(c.X, rect.Y - 13), 9, RenderKit.White, centred: true);
        }
    }
}

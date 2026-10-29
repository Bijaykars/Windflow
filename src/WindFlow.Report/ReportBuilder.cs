using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using WindFlow.Core;

namespace WindFlow.Report;

public sealed class ReportOptions
{
    public string ProjectName = "";
    public string Author = "";
    public string Scenario = "";        // e.g. "SW wind 3 m/s, all windows open"
    public bool IncludeStandardsNote = true;   // ASHRAE 55 / 62.1 reference values, worded as guidance not certification
}

/// <summary>A4 portrait PDF builder. PDFsharp 6's default (Core) build ships with no font resolver at all --
/// not even <see cref="GlobalFontSettings.UseWindowsFontsUnderWindows"/>'s built-in resolver knows "Segoe UI",
/// only a handful of classic names like Arial -- so <see cref="SystemFontResolver"/> below reads Segoe UI
/// (falling back to Arial if it were ever missing) straight out of the Windows Fonts folder.</summary>
public static class ReportBuilder
{
    /// <summary>A4 portrait PDF: cover (project, scenario, date, backend, grid), key numbers, the supplied images
    /// with captions, room/opening/source tables, comfort and air-quality summaries when present, ledger/warnings.</summary>
    public static void Build(SimulationResult result, IReadOnlyList<(string Title, BitmapSource Image)> images, string path, ReportOptions options, BitmapSource? cover = null)
    {
        GlobalFontSettings.FontResolver ??= new SystemFontResolver();

        var doc = new PdfDocument();
        doc.Info.Title = "WindFlow airflow study";
        doc.Info.Author = options.Author;
        doc.Info.Subject = options.ProjectName;

        // Streams backing the page images must outlive doc.Save (PdfSharp reads pixel data lazily).
        var ownedStreams = new List<MemoryStream>();
        try
        {
            var l = new Layout(doc);

            DrawCoverPage(l, result, options, cover, ownedStreams);

            int figure = 1;
            foreach (var (title, image) in images)
                DrawImagePage(l, title, image, figure++, ownedStreams);

            DrawRoomsTable(l, result);
            DrawOpeningsTable(l, result);
            DrawSourcesTable(l, result);
            DrawNotes(l, result);

            l.Gfx.Dispose(); // release the last page's writer before DrawFooters reopens every page

            DrawFooters(doc);

            doc.Save(path);
        }
        finally
        {
            foreach (var ms in ownedStreams) ms.Dispose();
        }
    }

    // ---------------------------------------------------------------- layout helper

    /// <summary>Cursor + page breaks. No framework: just "where am I" and "start a new A4 page".</summary>
    sealed class Layout
    {
        public const double MarginL = 44, MarginR = 44, MarginT = 46, MarginB = 50;

        readonly PdfDocument _doc;
        public PdfPage Page = null!;
        public XGraphics Gfx = null!;
        public double Y;

        public Layout(PdfDocument doc) { _doc = doc; NewPage(); }

        public double Width => Page.Width.Point - MarginL - MarginR;
        public double PageBottom => Page.Height.Point - MarginB;

        public void NewPage()
        {
            Gfx?.Dispose();
            Page = _doc.AddPage();
            Page.Size = PdfSharp.PageSize.A4;
            Gfx = XGraphics.FromPdfPage(Page);
            Y = MarginT;
        }

        public void EnsureSpace(double h)
        {
            if (Y + h > PageBottom) NewPage();
        }
    }

    // ---------------------------------------------------------------- palette / type

    static readonly XColor Ink = XColor.FromArgb(35, 35, 38);
    static readonly XColor Muted = XColor.FromArgb(120, 120, 126);
    static readonly XColor Faint = XColor.FromArgb(236, 237, 239);
    static readonly XColor Stripe = XColor.FromArgb(248, 248, 249);
    static readonly XColor Rule = XColor.FromArgb(205, 207, 212);
    static readonly XColor Accent = XColor.FromArgb(28, 90, 138);
    static readonly XColor Good = XColor.FromArgb(28, 122, 64);
    static readonly XColor GoodBg = XColor.FromArgb(231, 245, 235);
    static readonly XColor Warn = XColor.FromArgb(163, 104, 10);
    static readonly XColor WarnBg = XColor.FromArgb(252, 240, 217);

    const string Fam = "Segoe UI";
    static readonly XFont FTitle = new(Fam, 23, XFontStyleEx.Bold);
    static readonly XFont FSubtitle = new(Fam, 14, XFontStyleEx.Regular);
    static readonly XFont FMeta = new(Fam, 9.5, XFontStyleEx.Regular);
    static readonly XFont FH1 = new(Fam, 14, XFontStyleEx.Bold);
    static readonly XFont FH2 = new(Fam, 10.5, XFontStyleEx.Bold);
    static readonly XFont FLabel = new(Fam, 9, XFontStyleEx.Bold);
    static readonly XFont FBody = new(Fam, 9.5, XFontStyleEx.Regular);
    static readonly XFont FCaption = new(Fam, 9, XFontStyleEx.Italic);
    static readonly XFont FTiny = new(Fam, 7.6, XFontStyleEx.Regular);
    static readonly XFont FTinyBold = new(Fam, 7.6, XFontStyleEx.Bold);
    static readonly XFont FFooter = new(Fam, 7.5, XFontStyleEx.Regular);

    static void Text(Layout l, string s, XFont f, XColor c, double x, double y, double w, double h, XStringFormat? fmt = null)
        => l.Gfx.DrawString(s, f, new XSolidBrush(c), new XRect(x, y, w, h), fmt ?? XStringFormats.TopLeft);

    static void Rule2(Layout l, double y)
        => l.Gfx.DrawLine(new XPen(Rule, 0.75), Layout.MarginL, y, Layout.MarginL + l.Width, y);

    /// <summary>Shortens text with a trailing "…" until it fits maxWidth, so long room/opening names can't
    /// overrun into the next table column.</summary>
    static string TruncateToFit(XGraphics gfx, string s, XFont font, double maxWidth)
    {
        if (gfx.MeasureString(s, font).Width <= maxWidth) return s;
        for (int len = s.Length - 1; len > 0; len--)
        {
            string candidate = s[..len] + "…";
            if (gfx.MeasureString(candidate, font).Width <= maxWidth) return candidate;
        }
        return "…";
    }

    /// <summary>Word-wraps <paramref name="s"/> to <paramref name="maxWidth"/> points (DrawString does not wrap); a single word wider than that is cut with "…".</summary>
    static List<string> WrapToWidth(XGraphics gfx, string s, XFont font, double maxWidth)
    {
        var lines = new List<string>();
        string line = "";
        foreach (string word in s.Split(' '))
        {
            string trial = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && gfx.MeasureString(trial, font).Width > maxWidth) { lines.Add(TruncateToFit(gfx, line, font, maxWidth)); line = word; }
            else line = trial;
        }
        if (line.Length > 0) lines.Add(TruncateToFit(gfx, line, font, maxWidth));
        return lines.Count > 0 ? lines : [""];
    }

    // ---------------------------------------------------------------- grid warnings, grouped

    static readonly System.Text.RegularExpressions.Regex ReResolved = new(@"^Opening (.+?): the lattice resolves this opening at Cd ([\d.]+), below the requested ([\d.]+); flow here may be about (\d+) % low$");   // Analysis.NoteOpeningCd
    static readonly System.Text.RegularExpressions.Regex ReScatter = new(@"^Opening (.+?): the opening's discharge coefficient is uncertain by about ±(\d+) % at this cell size; a finer quality level reduces it$");   // Analysis.OpeningCdScatterNote
    static readonly System.Text.RegularExpressions.Regex ReCells = new(@"^Opening (.+?) is only ([\d.,]+) cells across at (\d+) mm cells: below the (\d+) cells VALIDATION\.md checked, so its flow is unreliable; use a finer grid for this opening\.$");   // Envelope.ResolutionGuard
    static readonly System.Text.RegularExpressions.Regex ReWall = new(@"^Opening (.+?): wall thickness taken from the geometry \(([\d.]+) m; the drawn depth says ([\d.]+) m\)$");   // Analysis.NoteOpeningCd
    static readonly System.Text.RegularExpressions.Regex ReGust = new(@"^Room (.+?): gust-driven\. Its outside openings are only (on one facade|on walls parallel to the wind), so it is ventilated mainly by gusts that a steady wind does not have: (Standard can read low here; run High \(gusty wind\) to compare\.)$");   // Envelope (gust-driven rooms)

    static readonly System.Text.RegularExpressions.Regex ReNarrow = new(@"^\d+ openings? (?:is|are) narrower than \d+ cells and too small for this cell size; their flow is unreliable\.$");   // Envelope.ResolutionGuard

    /// <summary>"a" when every value is the same, else "min-max"; values keep the text the producer wrote.</summary>
    static string Span(IEnumerable<string> values)
    {
        var v = values.Select(s => (N: double.Parse(s.Replace(",", ""), CultureInfo.InvariantCulture), S: s)).ToList();
        var lo = v.MinBy(t => t.N); var hi = v.MaxBy(t => t.N);
        return lo.N == hi.N ? lo.S : $"{lo.S}-{hi.S}";
    }

    /// <summary>Collapses the per-opening repeats (same exact templates the producers write) into one line each, at the position of the first; a template seen once, and everything else, stays as written.</summary>
    static List<string> GroupWarnings(IEnumerable<string> warnings)
    {
        var all = warnings.ToList();
        var groups = new (System.Text.RegularExpressions.Regex Re, Func<List<System.Text.RegularExpressions.Match>, string> Line)[]
        {
            (ReResolved, m => $"{m.Count} openings: the grid resolves Cd {Span(m.Select(x => x.Groups[2].Value))} instead of {Span(m.Select(x => x.Groups[3].Value))}, so flow through them may be {Span(m.Select(x => x.Groups[4].Value))} % low."),
            (ReScatter, m => $"{m.Count} openings: discharge coefficient uncertain by about ±{Span(m.Select(x => x.Groups[2].Value))} % at this cell size; a finer quality level reduces it."),
            (ReCells, m => $"{m.Count} openings are only {Span(m.Select(x => x.Groups[2].Value))} cells across at {m[0].Groups[3].Value} mm cells, below the {m[0].Groups[4].Value} cells VALIDATION.md checked, so their flow is unreliable; use a finer grid: {string.Join(", ", m.Select(x => x.Groups[1].Value))}."),
            (ReWall, m => $"{m.Count} openings: wall thickness taken from the geometry, not the drawn depth: {string.Join(", ", m.Select(x => x.Groups[1].Value))}."),
            (ReGust, m => $"{m.Count} rooms are gust-driven: {string.Join(", ", m.Select(x => $"{x.Groups[1].Value} ({x.Groups[2].Value})"))}. Their outside openings are only on those walls, so they are ventilated mainly by gusts that a steady wind does not have: {m[0].Groups[3].Value}"),
        };
        if (all.Any(w => ReCells.IsMatch(w))) all.RemoveAll(w => ReNarrow.IsMatch(w));   // the per-name line already says it
        var result = new List<string>();
        var done = new HashSet<int>();
        for (int i = 0; i < all.Count; i++)
        {
            int g = Array.FindIndex(groups, t => t.Re.IsMatch(all[i]));
            if (g < 0) { result.Add(all[i]); continue; }
            if (!done.Add(g)) continue;
            var hits = all.Select(w => groups[g].Re.Match(w)).Where(m => m.Success).ToList();
            result.Add(hits.Count == 1 ? all[i] : groups[g].Line(hits));
        }
        return result;
    }

    // ---------------------------------------------------------------- number formatting

    static string F1(float v) => float.IsNaN(v) ? "–" : v.ToString("0.0", CultureInfo.InvariantCulture);
    static string F2(float v) => float.IsNaN(v) ? "–" : v.ToString("0.00", CultureInfo.InvariantCulture);
    static string F0(float v) => float.IsNaN(v) ? "–" : v.ToString("0", CultureInfo.InvariantCulture);
    /// <summary>The fresh-air ACH as the report prints it: one decimal, "sealed" at the air-age cap, "—" while there is no age.</summary>
    static string FreshCell(RoomResult r) => (float.IsNaN(r.FreshAch) ? "—" : r.AgeSealed ? "sealed" : F1(r.FreshAch)) + (r.TooFewCells ? " †" : "");   // IMP-C: † = unreliable (too few cells)
    static string FreshWithUnit(RoomResult r) => r.AgeSealed ? "sealed (no air path)" : FreshCell(r) + " fresh-air ACH";
    static string Pct(float fraction) => float.IsNaN(fraction) ? "–" : (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + " %";

    static string FmtSeconds(double s) => s >= 60
        ? $"{(int)(s / 60)}m {(int)(s % 60)}s"
        : $"{s:0.0}s";

    // ---------------------------------------------------------------- cover page

    static void DrawCoverPage(Layout l, SimulationResult result, ReportOptions options, BitmapSource? cover, List<MemoryStream> ownedStreams)
    {
        double x = Layout.MarginL, w = l.Width;

        Text(l, "WindFlow airflow study", FTitle, Accent, x, l.Y, w, 30);
        l.Y += 34;

        string project = string.IsNullOrWhiteSpace(options.ProjectName) ? "Untitled project" : options.ProjectName;
        Text(l, project, FSubtitle, Ink, x, l.Y, w, 20);
        l.Y += 22;

        if (!string.IsNullOrWhiteSpace(options.Scenario))
        {
            Text(l, options.Scenario, FBody, Muted, x, l.Y, w, 14);
            l.Y += 16;
        }

        string author = string.IsNullOrWhiteSpace(options.Author) ? "" : $"Prepared by {options.Author} · ";
        Text(l, $"{author}{DateTime.Now:d MMMM yyyy}", FMeta, Muted, x, l.Y, w, 14);
        l.Y += 22;

        Rule2(l, l.Y);
        l.Y += 18;

        Text(l, "Summary", FH1, Ink, x, l.Y, w, 18);
        l.Y += 22;
        foreach (string part in WrapToWidth(l.Gfx, SummaryText(result), FBody, w)) { Text(l, part, FBody, Ink, x, l.Y, w, 13); l.Y += 13; }
        l.Y += 10;

        if (cover != null)   // the 3D view of the building with the airflow, when the caller has a viewport
        {
            var xImage = ImageFromBitmapSource(cover, ownedStreams, jpeg: true);
            double imgH = Math.Min(230, w * xImage.PixelHeight / (double)xImage.PixelWidth), imgW = imgH * xImage.PixelWidth / (double)xImage.PixelHeight;
            l.Gfx.DrawImage(xImage, x + (w - imgW) / 2, l.Y, imgW, imgH);
            l.Y += imgH + 12;
        }

        Text(l, "Key numbers", FH1, Ink, x, l.Y, w, 18);
        l.Y += 24;

        var field = result.Field;
        var rooms = result.Rooms;

        var lines = new List<(string Label, string Value)>
        {
            ("Result", ResultStatus.ReportLine(result.Convergence)),
            ("Wind", $"{ReportWind.Text(result.Wind, result.Model.NorthDeg)} at 10 m height"),
            ("Quality / backend", $"{ResultStatus.QualityLabel(result)} · {(string.IsNullOrEmpty(field.Backend) ? "–" : field.Backend)}"),
            ("Grid", $"{field.Grid.Nx} × {field.Grid.Ny} × {field.Grid.Nz} cells, {field.Grid.Dx:0.00} m"),
            ("Steps / solve time", $"{field.Steps:N0} steps · {FmtSeconds(field.Seconds)}"),
            ("Rooms analysed", $"{rooms.Count}"),
        };
        if (rooms.Count > 0)
        {
            // Ranked by fresh-air ACH (from the air age). A room with no age yet, and a room loop drawn through open air (the UI
            // shows "–" for it), are left out rather than ranked on a number that is not theirs.
            var ranked = rooms.Where(r => !float.IsNaN(r.FreshAch) && !r.ExchangeOnly).ToList();
            if (ranked.Count > 0)
            {
                var best = ranked.Aggregate((a, b) => b.FreshAch > a.FreshAch ? b : a);
                var worst = ranked.Aggregate((a, b) => b.FreshAch < a.FreshAch ? b : a);
                lines.Add(("Best ventilated", $"{best.Room.Name} — {FreshWithUnit(best)}"));
                lines.Add(("Least ventilated", $"{worst.Room.Name} — {FreshWithUnit(worst)}"));
            }
            else lines.Add(("Best / least ventilated", "— (air age not solved for any room)"));
        }
        lines.Add(("Building fresh-air rate", float.IsNaN(result.Ledger.FreshAch) ? "— (air age not solved)" : $"{F1(result.Ledger.FreshAch)} ACH (3600 ÷ mean air age over {result.Ledger.VolumeText})"));
        if (result.Convergence != null) lines.Add(("Air age used", ResultStatus.AgeLine(result.Convergence)));   // 6k: online (time-averaged) or the steady solve on the mean flow
        lines.Add(("Total inflow", $"{result.Ledger.In:0.00} m³/s ({result.Ledger.In * 3600:0} m³/h)"));

        double labelW = 150, rowH = 16;
        foreach (var (label, value) in lines)
        {
            var wrapped = WrapToWidth(l.Gfx, value, FBody, w - labelW);
            l.EnsureSpace(rowH * wrapped.Count);
            Text(l, label, FLabel, Muted, x, l.Y, labelW, rowH);
            foreach (var part in wrapped) { Text(l, part, FBody, Ink, x + labelW, l.Y, w - labelW, rowH); l.Y += rowH; }
        }
        l.Y += 8;

        // Mass balance ledger line: the trust gate the UI shows too, never silent.
        var ledger = result.Ledger;
        double residualPct = ledger.Residual * 100;
        bool ok = ledger.IsPhysical;
        var boxColor = ok ? GoodBg : WarnBg;
        var textColor = ok ? Good : Warn;
        string headline = ok
            ? $"Mass balance OK, residual {residualPct:0} %"
            : $"Mass balance warning, residual {residualPct:0} %";

        // Grid.Warnings are folded into ledger.Notes already (Analysis.Finish); skip duplicates here since the
        // "Grid warnings" section below prints them too.
        const int maxBoxNotes = 6;
        var boxNotes = new List<string>();
        if (!ok)
            foreach (var note in ledger.Notes)
                if (!field.Grid.Warnings.Contains(note))
                    boxNotes.Add(note);
        int shownNotes = Math.Min(boxNotes.Count, maxBoxNotes);
        bool moreNotes = boxNotes.Count > maxBoxNotes;

        double boxH = 22 + (ok ? 0 : shownNotes * 12 + (moreNotes ? 12 : 0));
        l.EnsureSpace(boxH + 10);
        l.Gfx.DrawRectangle(new XSolidBrush(boxColor), x, l.Y, w, boxH);
        Text(l, headline, FH2, textColor, x + 10, l.Y + 4, w - 20, 16);
        double noteY = l.Y + 20;
        if (!ok)
        {
            for (int i = 0; i < shownNotes; i++)
            {
                Text(l, "• " + boxNotes[i], FTiny, textColor, x + 10, noteY, w - 20, 12);
                noteY += 12;
            }
            if (moreNotes)
                Text(l, $"… and {boxNotes.Count - shownNotes} more", FTiny, textColor, x + 10, noteY, w - 20, 12);
        }
        l.Y += boxH + 12;

        if (field.Grid.Warnings.Count > 0)
        {
            Text(l, "See Notes at the end for where this result is less certain.", FCaption, Muted, x, l.Y - 6, w, 12);
            l.Y += 10;
        }
    }

    /// <summary>The run's notes (voxelizer and analysis warnings), at the end of the report: plain text, grouped, not an alarm.</summary>
    static void DrawNotes(Layout l, SimulationResult result)
    {
        var field = result.Field;
        double x = Layout.MarginL, w = l.Width;
        if (field.Grid.Warnings.Count > 0)
        {
            l.EnsureSpace(70);
            l.Y += 6;
            Text(l, "Notes", FH2, Ink, x, l.Y, w, 14);
            l.Y += 16;
            Text(l, "These notes say where the result is less certain. They do not mean the result is wrong.", FCaption, Muted, x, l.Y, w, 12);
            l.Y += 15;
            foreach (var warning in GroupWarnings(field.Grid.Warnings))
            {
                var wrapped = WrapToWidth(l.Gfx, "• " + warning, FTiny, w - 8);
                for (int i = 0; i < wrapped.Count; i++)
                {
                    l.EnsureSpace(11);
                    Text(l, i == 0 ? wrapped[i] : "  " + wrapped[i], FTiny, Ink, x, l.Y, w, 11);
                    l.Y += 11;
                }
                l.Y += 2;
            }
        }
    }

    /// <summary>The summary paragraph: facts from the result only (wind, open openings, the building's fresh-air rate, best and least ventilated room, rooms with no air path, the result's status).</summary>
    static string SummaryText(SimulationResult result)
    {
        var parts = new List<string>();
        var rooms = result.Rooms;
        int open = result.Openings.Count(o => o.Opening.IsOpen), total = result.Openings.Count;
        string lead = $"With a wind of {ReportWind.Text(result.Wind, result.Model.NorthDeg)}" + (total > 0 ? $" and {open} of {total} openings open" : "");
        parts.Add(float.IsNaN(result.Ledger.FreshAch) ? lead + ", the building's fresh-air rate is not solved (the air age is missing)."
                                                       : lead + $", the building gets {F1(result.Ledger.FreshAch)} fresh-air air changes per hour.");
        var ranked = rooms.Where(r => !float.IsNaN(r.FreshAch) && !r.ExchangeOnly && !r.AgeSealed).ToList();
        if (rooms.Count == 0) parts.Add("No rooms were analysed.");
        else if (ranked.Count >= 2)
        {
            var best = ranked.Aggregate((a, b) => b.FreshAch > a.FreshAch ? b : a);
            var worst = ranked.Aggregate((a, b) => b.FreshAch < a.FreshAch ? b : a);
            parts.Add($"{best.Room.Name} is the best ventilated room ({F1(best.FreshAch)}) and {worst.Room.Name} the least ({F1(worst.FreshAch)}).");
        }
        else if (ranked.Count == 1) parts.Add($"{ranked[0].Room.Name} is the only room with a solved air age; its fresh-air rate is {F1(ranked[0].FreshAch)} air changes per hour.");
        else parts.Add("No room has a solved air age yet, so none is ranked (\"—\" in the Rooms table).");
        var sealedNames = rooms.Where(r => r.AgeSealed).Select(r => r.Room.Name).ToList();
        if (sealedNames.Count > 0) parts.Add($"{string.Join(", ", sealedNames)} {(sealedNames.Count == 1 ? "has" : "have")} no air path with the current openings.");
        var c = result.Convergence;
        parts.Add(c == null ? "This is a Preview result: use it for direction only." : $"This is a fine-grid result; its status is {ResultStatus.Text(c).ToLowerInvariant()}.");
        return string.Join(" ", parts);
    }

    // ---------------------------------------------------------------- image pages

    static void DrawImagePage(Layout l, string title, BitmapSource image, int figureNumber, List<MemoryStream> ownedStreams)
    {
        l.NewPage();
        double x = Layout.MarginL, w = l.Width;

        Text(l, title, FH1, Ink, x, l.Y, w, 20);
        l.Y += 26;

        var xImage = ImageFromBitmapSource(image, ownedStreams);
        double imgW = w;
        double imgH = imgW * xImage.PixelHeight / (double)xImage.PixelWidth;
        double maxH = l.PageBottom - l.Y - 26; // leave room for the caption below
        if (imgH > maxH)
        {
            imgH = maxH;
            imgW = imgH * xImage.PixelWidth / (double)xImage.PixelHeight;
        }
        double imgX = x + (w - imgW) / 2;
        l.Gfx.DrawImage(xImage, imgX, l.Y, imgW, imgH);
        l.Y += imgH + 8;

        Text(l, $"Figure {figureNumber}: {title}", FCaption, Muted, x, l.Y, w, 24, XStringFormats.TopCenter);
        l.Y += 24;
    }

    static XImage ImageFromBitmapSource(BitmapSource src, List<MemoryStream> ownedStreams, bool jpeg = false)
    {
        var ms = new MemoryStream();
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 85 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        encoder.Save(ms);
        ms.Position = 0;
        ownedStreams.Add(ms); // kept alive until after doc.Save
        return XImage.FromStream(ms);
    }

    // ---------------------------------------------------------------- tables

    static void DrawRoomsTable(Layout l, SimulationResult result)
    {
        var rooms = result.Rooms;
        if (rooms.Count == 0) return;

        bool HasAny(Func<RoomResult, float> sel) => rooms.Any(r => !float.IsNaN(sel(r)));

        // ± is the half-width the averaged pass left per room (ResultStatus.Shown); a Preview result has none and shows "–".
        var headers = new List<string> { "Level", "Name", "Vol m³", "Voxel m³", "Nominal", "Fresh-air ACH", "±", "Total exch.", "Thru open.", "Speed m/s", "Stagnant %" };
        var weights = new List<double> { 32, 50, 36, 40, 44, 52, 24, 46, 42, 42, 48 };   // column widths in points (a header is about 6 pt wider than its text)
        var right = new List<bool> { false, false, true, true, true, true, true, true, true, true, true };
        var cols = new List<Func<RoomResult, string>>
        {
            r => r.Room.Level,
            r => r.Room.Name,
            r => F0(r.Volume),
            r => F0(r.VoxelVolume),
            r => r.VolumeIsNominal ? "yes" : "no",
            r => r.ExchangeOnly ? "—" : FreshCell(r) + (result.Convergence is { AgeOnline: true } && ResultStatus.AgeSteadyRoom(result.Convergence, r.Room.Id) && !r.AgeSealed && !float.IsNaN(r.FreshAch) ? " (steady)" : "") + ResultStatus.PdfDriftSuffix(result.Convergence, r.Room.Id, FreshCell(r)),   // RA-03: a Standard row has no ± column to carry the drift note
            r => ResultStatus.TooShort(result.Convergence, r.Room.Id) ? "—" : F1(ResultStatus.FreshHalfWidth(result.Convergence, r)) + (ResultStatus.Drifting(result.Convergence, r.Room.Id) ? " (drifting)" : ""),   // P16-03
            r => F1(r.Ach),
            r => float.IsNaN(r.ThroughOpeningAch) ? "—" : F1(r.ThroughOpeningAch),   // portal step 1
            r => F1(r.MeanSpeed),
            r => Pct(r.StagnantFraction),
        };

        if (!ResultStatus.FreshHasRange(result.Convergence)) { headers.RemoveAt(6); weights.RemoveAt(6); right.RemoveAt(6); cols.RemoveAt(6); }   // AGE-FIX: a steady age has no fresh-air ± column
        if (ResultStatus.HasConservative(result.Convergence))   // AGE-HIGH: High only, the steady age on the averaged flow beside the online fresh-air ACH
        {
            headers.Insert(7, "Fresh-air (steady age)"); weights.Insert(7, 70); right.Insert(7, true);
            cols.Insert(7, r => r.ExchangeOnly || float.IsNaN(r.ConservativeFreshAch) ? "—" : r.AgeSealed ? "sealed" : F1(r.ConservativeFreshAch));
        }

        void AddAdvanced(string header, double weight, Func<RoomResult, float> sel, Func<float, string> fmt, bool sealedAge = false)
        {
            if (!HasAny(sel)) return;
            headers.Add(header); weights.Add(weight); right.Add(true);
            cols.Add(r => sealedAge && r.AgeSealed ? "sealed" : fmt(sel(r)));   // D1: the 24 h solver cap is not an age
        }

        AddAdvanced("Temp °C", 40, r => r.MeanTemperature, F1);
        // 6j: held-temperature heater (+) / cooler (-), time mean. RT-06: a free-running thermal run shows "—", and the legend says which.
        bool held = HasAny(r => r.HoldDutyWatts), freeRunning = !held && result.Options.Thermal && !result.Options.HoldIndoorTemperature && HasAny(r => r.MeanTemperature);
        if (freeRunning) { headers.Add("Duty W"); weights.Add(40); right.Add(true); cols.Add(_ => "—"); }
        else AddAdvanced("Duty W", 40, r => r.HoldDutyWatts, F0);
        AddAdvanced("CO2 ppm", 40, r => r.MeanCo2, F0);
        AddAdvanced("Age occ. min", 46, r => r.MeanAge / 60f, F1, sealedAge: true);          // minutes, as the panel shows it
        AddAdvanced("Air-change eff.", 58, r => r.AirChangeEffectiveness, F2);
        AddAdvanced("Comfort %", 46, r => r.ComfortFraction, Pct);
        AddAdvanced("Draught %", 46, r => r.DraughtFraction, Pct);

        var rows = rooms.Select(r => cols.Select(c => c(r)).ToArray()).ToList();
        DrawTable(l, "Rooms", headers.ToArray(), weights.ToArray(), right.ToArray(), rows, fresh: true);

        // Two short lines under the table; the definitions are in the shipped how-windflow-works.pdf.
        var footnote = new List<string>
        {
            "Fresh-air ACH = 3600 ÷ the room's mean age of air. Total exch. = all air entering the room, including from other rooms.",
            "Definitions and limits: see How WindFlow works (how-windflow-works.pdf).",
        };
        if (rooms.Any(r => r.TooFewCells)) footnote.Add("† " + RoomResult.TooFewCellsLabel + ": the room is under 3 cells across at this cell size; its numbers are not to be trusted.");
        if (held) footnote.Add(FormattableString.Invariant($"Duty W: + = heating, − = cooling; rooms held at {result.Model.Env.IndoorTemperature:0.#} °C (room average)."));
        else if (freeRunning) footnote.Add("Duty W: — = free-running; nothing holds the room temperature.");
        l.EnsureSpace(footnote.Count * 11 + 4);
        foreach (string line in footnote)
        {
            Text(l, line, FTiny, Muted, Layout.MarginL, l.Y, l.Width, 11);
            l.Y += 11;
        }
        l.Y += 8;
    }

    /// <summary>Breaks <paramref name="text"/> at spaces into lines of at most <paramref name="max"/> characters.</summary>
    static List<string> WrapNote(string text, int max)
    {
        var lines = new List<string>();
        var line = "";
        foreach (string word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > max) { lines.Add(line); line = word; }
            else line = line.Length == 0 ? word : line + " " + word;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    static void DrawOpeningsTable(Layout l, SimulationResult result)
    {
        var openings = result.Openings;
        if (openings.Count == 0) return;

        var headers = new[] { "Name", "Level", "Kind", "Status", "In m³/s", "Out m³/s" };
        var weights = new[] { 0.28, 0.14, 0.14, 0.14, 0.15, 0.15 };
        var right = new[] { false, false, false, false, true, true };

        var rows = openings.Select(o =>
        {
            // Opening.Normal points outside when known, and FlowRate is positive along +Normal,
            // so a positive flow is outgoing and a negative flow is incoming.
            double outFlow = Math.Max(o.FlowRate, 0);
            double inFlow = Math.Max(-o.FlowRate, 0);
            return new[]
            {
                o.Opening.Name,
                o.Opening.Level,
                o.Opening.Kind.ToString(),
                o.Opening.IsOpen ? "Open" : "Closed",
                inFlow.ToString("0.00", CultureInfo.InvariantCulture),
                outFlow.ToString("0.00", CultureInfo.InvariantCulture),
            };
        }).ToList();

        DrawTable(l, "Openings", headers, weights, right, rows);
    }

    static void DrawSourcesTable(Layout l, SimulationResult result)
    {
        var sources = result.Sources;
        if (sources.Count == 0) return;

        var headers = new[] { "Name", "Kind", "Set m³/h", "Measured m³/h" };
        var weights = new[] { 0.40, 0.20, 0.20, 0.20 };
        var right = new[] { false, false, true, true };

        var rows = sources.Select(s => new[]
        {
            s.Source.Name,
            s.Source.Kind.ToString(),
            F0(s.Source.FlowRate * 3600),
            s.Source.Enabled ? F0(s.FlowRate * 3600) : "—",   // a switched-off device moves nothing and is not measured (was "0")
        }).ToList();

        DrawTable(l, "Sources", headers, weights, right, rows);
        if (sources.Any(s => !s.Source.Enabled))
        {
            l.Y -= 12;
            Text(l, "— = the device is switched off, so nothing is measured.", FTiny, Muted, Layout.MarginL, l.Y, l.Width, 11);
            l.Y += 22;
        }
    }

    /// <summary>Generic auto-paginating table with a repeated header row. No framework, just the one shape
    /// every table here needs.</summary>
    static void DrawTable(Layout l, string title, string[] headers, double[] weights, bool[] rightAlign, List<string[]> rows, bool fresh = false)
    {
        // a table starts a new page only when asked to (the first one) or when it would not fit whole on this one
        if (fresh || l.Y + 26 + 16 + 14 + rows.Count * 14 + 18 > l.PageBottom) l.NewPage();
        double x0 = Layout.MarginL, totalW = l.Width;
        Text(l, title, FH1, Ink, x0, l.Y, totalW, 20);
        l.Y += 26;

        double sumW = weights.Sum();
        var widths = weights.Select(wt => wt / sumW * totalW).ToArray();
        const double rowH = 14, headerLine = 10;
        // a header wider than its column wraps onto more lines instead of running under its neighbour
        var headerLines = headers.Select((h, c) => WrapToWidth(l.Gfx, h, FTinyBold, widths[c] - 6)).ToArray();
        double headerH = Math.Max(16, headerLines.Max(h => h.Count) * headerLine + 6);

        void DrawHeader()
        {
            l.EnsureSpace(headerH + rowH);
            double x = x0;
            l.Gfx.DrawRectangle(new XSolidBrush(Faint), x0, l.Y, totalW, headerH);
            for (int c = 0; c < headers.Length; c++)
            {
                var fmt = rightAlign[c] ? XStringFormats.CenterRight : XStringFormats.CenterLeft;
                double y = l.Y + (headerH - headerLines[c].Count * headerLine) / 2;
                foreach (var part in headerLines[c]) { Text(l, part, FTinyBold, Ink, x + 3, y, widths[c] - 6, headerLine, fmt); y += headerLine; }
                x += widths[c];
            }
            l.Y += headerH;
        }

        DrawHeader();
        bool alt = false;
        foreach (var row in rows)
        {
            if (l.Y + rowH > l.PageBottom)
            {
                l.NewPage();
                DrawHeader();
            }
            double x = x0;
            if (alt) l.Gfx.DrawRectangle(new XSolidBrush(Stripe), x0, l.Y, totalW, rowH);
            for (int c = 0; c < row.Length; c++)
            {
                var fmt = rightAlign[c] ? XStringFormats.CenterRight : XStringFormats.CenterLeft;
                string cellText = rightAlign[c] ? row[c] : TruncateToFit(l.Gfx, row[c], FTiny, widths[c] - 6);
                Text(l, cellText, FTiny, Ink, x + 3, l.Y, widths[c] - 6, rowH, fmt);
                x += widths[c];
            }
            l.Y += rowH;
            alt = !alt;
        }
        Rule2(l, l.Y);
        l.Y += 18;
    }

    // ---------------------------------------------------------------- footer

    static void DrawFooters(PdfDocument doc)
    {
        int total = doc.PageCount;
        for (int i = 0; i < total; i++)
        {
            var page = doc.Pages[i];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            double w = page.Width.Point, h = page.Height.Point;
            gfx.DrawLine(new XPen(Rule, 0.5), Layout.MarginL, h - 32, w - Layout.MarginR, h - 32);
            // C1 / S5-05: the same disclaimer as the app's About screen and the README, wrapped to the page width.
            double width = w - Layout.MarginL - Layout.MarginR;
            var line = new System.Text.StringBuilder();
            double y = h - 29;
            foreach (var word in $"{Disclaimer.Text} · page {i + 1}/{total}".Split(' '))
            {
                string trial = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && gfx.MeasureString(trial, FFooter).Width > width)
                {
                    gfx.DrawString(line.ToString(), FFooter, new XSolidBrush(Muted), new XRect(Layout.MarginL, y, width, 9), XStringFormats.TopCenter);
                    y += 9;
                    line.Clear();
                    trial = word;
                }
                line.Clear().Append(trial);
            }
            gfx.DrawString(line.ToString(), FFooter, new XSolidBrush(Muted), new XRect(Layout.MarginL, y, width, 9), XStringFormats.TopCenter);
        }
    }
}

/// <summary>Reads Segoe UI straight out of the Windows Fonts folder, falling back to Arial for any style
/// that isn't installed. PDFsharp 6's Core build has no font resolver of its own to fall back on.</summary>
sealed class SystemFontResolver : IFontResolver
{
    static readonly string FontsDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Fonts);
    readonly Dictionary<string, byte[]> _cache = new();

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        string primary = (isBold, isItalic) switch
        {
            (true, true) => "segoeuiz.ttf",
            (true, false) => "segoeuib.ttf",
            (false, true) => "segoeuii.ttf",
            _ => "segoeui.ttf",
        };
        string fallback = (isBold, isItalic) switch
        {
            (true, true) => "arialbi.ttf",
            (true, false) => "arialbd.ttf",
            (false, true) => "ariali.ttf",
            _ => "arial.ttf",
        };
        if (File.Exists(Path.Combine(FontsDir, primary)))
            return new FontResolverInfo(primary);
        if (File.Exists(Path.Combine(FontsDir, fallback)))
            return new FontResolverInfo(fallback);

        throw new InvalidOperationException(
            $"Neither '{primary}' nor its fallback '{fallback}' was found in the Windows fonts directory ({FontsDir}).");
    }

    public byte[] GetFont(string faceName)
    {
        if (_cache.TryGetValue(faceName, out var bytes)) return bytes;
        bytes = File.ReadAllBytes(Path.Combine(FontsDir, faceName));
        _cache[faceName] = bytes;
        return bytes;
    }
}

/// <summary>The solved wind as the report words it. A class of its own: ReportBuilder's static fields load fonts,
/// which --selfcheck and --solve must not need.</summary>
public static class ReportWind
{
    static readonly string[] Compass =
        ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"];

    /// <summary>"4.0 m/s from 270° (W)": the solved wind (SimulationResult.Wind, the direction the air moves toward in
    /// model axes) as the true compass bearing it comes FROM, the way the Wind boxes take it (P13b-05); northDeg is
    /// BuildingModel.NorthDeg, the clockwise angle from model +Y to true north (P13b-04).</summary>
    public static string Text(System.Numerics.Vector2 wind, float northDeg = 0)
    {
        if (wind.Length() < 1e-6f) return "0.0 m/s (no wind)";
        int from = (int)Math.Round(((Math.Atan2(-wind.X, -wind.Y) * 180 / Math.PI - northDeg) % 360 + 720) % 360) % 360;
        return string.Create(CultureInfo.InvariantCulture, $"{wind.Length():0.0} m/s from {from}° ({Compass[(int)Math.Round(from / 22.5) % 16]})");
    }
}

/// <summary>The one disclaimer wording, shown in the PDF footer (every page, the cover included), the app's About
/// section and the README (S5-05). Its own class so the app and --selfcheck can read it without ReportBuilder's font statics.</summary>
public static class Disclaimer
{
    public const string Text = "WindFlow is a design-stage airflow tool. Its results are estimates with stated uncertainty, not a substitute for a ventilation engineer or a code-compliance calculation.";

    /// <summary>P11-01: the lattice viscosity floor, in words (README, VALIDATION.md and the About section say the same).</summary>
    public const string ViscosityFloor = "The solver holds its viscosity at a stability floor, so on a building-scale grid the simulated air is roughly 150 to 850 times more viscous than real air and jets through windows and supplies behave like laminar jets. Treat absolute air changes and jet reach as indicative.";

    /// <summary>Batch 5: the sized limits in one plain paragraph (docs/LIMITATIONS.md has each with its source; the About section shows this).</summary>
    public const string KnownLimits = "Known limits, sized (docs/LIMITATIONS.md): moving the viscosity floor from 5e-4 to 1e-4 changed the sample house's room air changes by -15 to -30 %. The default Compact wind domain reads room air exchange likely about 11-14 % higher per room than the Full domain (newest test; an older run read 8-33 %); use Domain: Full for a final check. Wind at 1 m above the smooth ground is about 54 % under the standard profile. A 4-cell opening passes less air than its size says, and its flow depends on where it falls on the grid by up to about 30 %. Walls are adiabatic. Heat stratification from small sources reads about 25-40 % low. Fresh-air ACH from the steady age of air can differ from the age followed through time by about 30 % either way per room. A small change in grid size can move a single room by up to about 35 % and the whole building by about 20 %. The fine box sharpens the local view only: room and opening numbers come from the whole-building run.";
}

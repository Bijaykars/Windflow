using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;

namespace WindFlow.App;

/// <summary>Dark (default) or Light chrome around the same dark viewport: Settings → Appearance, or --theme for a shot.
/// The tokens live in App.axaml's ThemeDictionaries, so applying a theme is one property on the Application.</summary>
public static class Appearance
{
    public static bool IsLight(string? name) => string.Equals(name, "Light", StringComparison.OrdinalIgnoreCase);

    public static void Apply(string? name)
    {
        if (Application.Current is { } app) app.RequestedThemeVariant = IsLight(name) ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    public static IBrush Brush(string key) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var v) == true && v is IBrush b ? b : Brushes.Magenta;
}

/// <summary>A Lucide icon (Icons.axaml): the 24-unit stroke geometry scaled to the icon size, drawn in the inherited
/// Foreground, so a ghost button's hover colour reaches its icon with no extra style.</summary>
public sealed class Icon : Viewbox
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));
    public static readonly StyledProperty<double> StrokeProperty = AvaloniaProperty.Register<Icon, double>(nameof(Stroke), 2);
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();

    readonly Path path = new()
    {
        Width = 24, Height = 24, Stretch = Stretch.None, StrokeThickness = 2,
        StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
    };

    public Icon()
    {
        Stretch = Stretch.Uniform;
        Child = path;
        path.Bind(Path.DataProperty, this.GetObservable(DataProperty));
        path.Bind(Path.StrokeThicknessProperty, this.GetObservable(StrokeProperty));
        path.Bind(Path.StrokeProperty, this.GetObservable(ForegroundProperty));
    }

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
}

/// <summary>The Run button's icon from its text, so the code that sets Content = "Run" / "Pause" / "Resume" / "Done" /
/// "Cancel" / "Cancelling…" stays as it is.</summary>
public sealed class RunIcon : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value as string switch
        {
            "Pause" => "IconPause",
            "Done" => "IconCheck",
            "Cancel" or "Cancelling…" => "IconX",
            _ => "IconPlay",
        };
        return Application.Current?.TryGetResource(key, null, out var g) == true ? g : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The wind pop-over's compass: a ring with the cardinal letters and a needle from the centre to where the wind
/// comes from. Dragging turns the needle in 5° steps (relative to the press bearing, so nothing jumps on the first
/// pixel); the value goes to the existing direction box through <see cref="Changed"/>.</summary>
public sealed class CompassDial : Control
{
    public static readonly StyledProperty<double> DegreesProperty = AvaloniaProperty.Register<CompassDial, double>(nameof(Degrees), 270);
    public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<CompassDial, IBrush?>(nameof(Accent));
    public static readonly StyledProperty<IBrush?> RingProperty = AvaloniaProperty.Register<CompassDial, IBrush?>(nameof(Ring));
    public static readonly StyledProperty<IBrush?> FaceProperty = AvaloniaProperty.Register<CompassDial, IBrush?>(nameof(Face));
    public static readonly StyledProperty<IBrush?> LabelProperty = AvaloniaProperty.Register<CompassDial, IBrush?>(nameof(Label));

    public double Degrees { get => GetValue(DegreesProperty); set => SetValue(DegreesProperty, value); }
    public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public IBrush? Ring { get => GetValue(RingProperty); set => SetValue(RingProperty, value); }
    public IBrush? Face { get => GetValue(FaceProperty); set => SetValue(FaceProperty, value); }
    public IBrush? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>The user turned the needle: degrees clockwise from north, snapped to 5°.</summary>
    public event Action<double>? Changed;

    static CompassDial()
    {
        AffectsRender<CompassDial>(DegreesProperty, AccentProperty, RingProperty, FaceProperty, LabelProperty);
    }

    public CompassDial()
    {
        Width = 150;
        Height = 150;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>N, NE, E … for a bearing, 45° sectors.</summary>
    public static string Cardinal(double degrees)
    {
        string[] names = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        return names[(int)Math.Round(((degrees % 360 + 360) % 360) / 45) % 8];
    }

    double pressBearing, pressDegrees;
    bool dragging;

    double BearingAt(Point p)
    {
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        return (Math.Atan2(p.X - c.X, -(p.Y - c.Y)) * 180 / Math.PI + 360) % 360;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        pressBearing = BearingAt(e.GetPosition(this));
        pressDegrees = Degrees;
        dragging = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!dragging) return;
        double d = pressDegrees + BearingAt(e.GetPosition(this)) - pressBearing;
        d = Math.Round(((d % 360) + 360) % 360 / 5) * 5 % 360;
        if (Math.Abs(d - Degrees) > 0.01) { Degrees = d; Changed?.Invoke(d); }
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height, r = Math.Min(w, h) / 2 - 1;
        var c = new Point(w / 2, h / 2);
        var ring = new Pen(Ring ?? Brushes.Gray, 1);
        ctx.DrawEllipse(Face, ring, c, r, r);
        // ticks every 15°, longer every 45°
        for (int a = 0; a < 360; a += 15)
        {
            double rad = a * Math.PI / 180, len = a % 45 == 0 ? 7 : 4;
            var p1 = new Point(c.X + Math.Sin(rad) * (r - 2), c.Y - Math.Cos(rad) * (r - 2));
            var p2 = new Point(c.X + Math.Sin(rad) * (r - 2 - len), c.Y - Math.Cos(rad) * (r - 2 - len));
            ctx.DrawLine(ring, p1, p2);
        }
        var tf = new Typeface("Segoe UI Variable Text, Segoe UI");
        string[] names = ["N", "E", "S", "W"];
        for (int i = 0; i < 4; i++)
        {
            double rad = i * 90 * Math.PI / 180;
            bool hit = Math.Abs(((Degrees - i * 90) % 360 + 540) % 360 - 180) < 22.5;
            var text = new FormattedText(names[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 11, hit ? Accent : Label);
            var at = new Point(c.X + Math.Sin(rad) * (r - 19) - text.Width / 2, c.Y - Math.Cos(rad) * (r - 19) - text.Height / 2);
            ctx.DrawText(text, at);
        }
        // needle: centre → source of the wind, arrow head at the rim
        double nr = Degrees * Math.PI / 180;
        var tip = new Point(c.X + Math.Sin(nr) * (r - 28), c.Y - Math.Cos(nr) * (r - 28));
        ctx.DrawLine(new Pen(Accent ?? Brushes.DeepSkyBlue, 2, lineCap: PenLineCap.Round), c, tip);
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            var ahead = new Point(c.X + Math.Sin(nr) * (r - 18), c.Y - Math.Cos(nr) * (r - 18));
            var side = new Vector(Math.Cos(nr) * 5, Math.Sin(nr) * 5);
            g.BeginFigure(ahead, true);
            g.LineTo(tip - side);
            g.LineTo(tip + side);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(Accent, null, head);
        ctx.DrawEllipse(Accent, null, c, 4, 4);
    }
}

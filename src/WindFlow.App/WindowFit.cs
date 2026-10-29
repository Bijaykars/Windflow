namespace WindFlow.App;

/// <summary>S4-03: keeps the window (and so the 328 DIP results column on its right) on a screen it fits. Sizes are DIPs, the work area
/// is in DIPs too (pixels / RenderScaling): at 150 % on a 1920x1080 display the work area is 1280 DIP wide, not 1920.</summary>
public static class WindowFit
{
    /// <summary>The smallest width the window may be given. The old 1460 was wider than the 1280 DIP work area of a 1080p laptop at 150 %.</summary>
    public const double MinWidth = 1180, MinHeight = 620;

    /// <summary>Size and top-left for a window that wants <paramref name="w"/> x <paramref name="h"/> at <paramref name="x"/>, <paramref name="y"/> inside the work
    /// area; the minimum shrinks below <see cref="MinWidth"/> / <see cref="MinHeight"/> only on a screen smaller than that.</summary>
    public static (double W, double H, double X, double Y, double MinW, double MinH) Fit(
        double w, double h, double x, double y, double workX, double workY, double workW, double workH)
    {
        double minW = Math.Min(MinWidth, workW), minH = Math.Min(MinHeight, workH);
        w = Math.Clamp(w, minW, workW);
        h = Math.Clamp(h, minH, workH);
        x = Math.Clamp(x, workX, workX + workW - w);
        y = Math.Clamp(y, workY, workY + workH - h);
        return (w, h, x, y, minW, minH);
    }
}

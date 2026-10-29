using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace WindFlow.App;

/// <summary>The window chrome: the merged title bar (drag, double-click), the project and help menus, the workflow
/// steps, the panel tabs, the wind pill with its compass pop-over, and the Dark / Light appearance. Nothing here
/// changes what the app does: every handler ends in a control or a method that already existed.</summary>
public partial class MainWindow
{
    // ---------------------------------------------------------------- title bar

    /// <summary>The empty title-bar area moves the window; a double-click there toggles maximised. Buttons inside the bar
    /// handle their own presses first, so this only sees the bar itself and its plain text.</summary>
    void OnTitleBarPress(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Control c && (c is Button or ToggleButton || c.FindAncestorOfType<Button>() != null || c.FindAncestorOfType<ToggleButton>() != null)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; return; }
        BeginMoveDrag(e);
    }

    void OnCaptionMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void OnCaptionMaximize(object? sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void OnCaptionClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>A menu row was clicked: close the flyout it lives in (the row's own Click ran first).</summary>
    void OnMenuItemClick(object? sender, RoutedEventArgs e)
    {
        ProjectMenuButton.Flyout?.Hide();
        HelpButton.Flyout?.Hide();
    }

    /// <summary>What the project menu button reads: the model's name, or "No model".</summary>
    void SetProjectName(string? name) => ProjectMenuText.Text = string.IsNullOrWhiteSpace(name) || name == "-" ? "No model" : name;

    // ---------------------------------------------------------------- workflow steps (title bar)

    /// <summary>Model → Wind → Simulate → Results → Report: each step opens what already exists.</summary>
    void OnWorkflow(object? sender, RoutedEventArgs e)
    {
        switch ((sender as Control)?.Tag as string)
        {
            case "0": if (EmptyState.IsVisible) ProjectMenuButton.Flyout?.ShowAt(ProjectMenuButton); else SelectTab(TabRooms); break;
            case "1": if (!EmptyState.IsVisible) OnWindPill(null, e); break;
            case "2": if (!EmptyState.IsVisible) SelectTab(TabView); break;
            case "3": if (!EmptyState.IsVisible) SelectTab(TabRooms); break;
            case "4": if (!EmptyState.IsVisible) OnExportReport(null, e); break;
        }
    }

    /// <summary>The step marks follow the state: a tick for what is done, the accent on where you are (status tick).</summary>
    void UpdateWorkflow()
    {
        bool model = !EmptyState.IsVisible;
        bool running = TourLiveRunning;
        bool results = running && live?.Stats != null;
        int current = !model ? 0 : !running ? 1 : !results ? 2 : 3;
        Button[] steps = [WfModel, WfWind, WfSimulate, WfResults, WfReport];
        for (int i = 0; i < steps.Length; i++)
        {
            steps[i].Classes.Set("done", i < current);
            steps[i].Classes.Set("on", i == current);
        }
    }

    // ---------------------------------------------------------------- panel tabs

    void OnSectionTab(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton t && int.TryParse(t.Tag as string, out int tab)) SelectTab(tab);
    }

    /// <summary>ShowTab calls this: one tab checked, the others not (a click on the checked tab keeps it checked).</summary>
    void ShowSectionTab(int tab)
    {
        ToggleButton[] tabs = [SectionRoomsTab, SectionOpeningsTab, SectionViewTab, SectionDevicesTab, SectionStudiesTab];
        for (int i = 0; i < tabs.Length; i++) tabs[i].IsChecked = i == tab;
    }

    // ---------------------------------------------------------------- wind pill and its pop-over

    bool syncingWind;
    DateTime windClosedAt = DateTime.MinValue;

    void OnWindPill(object? sender, RoutedEventArgs e)
    {
        // The press that light-dismissed the pop-over is the same click that lands here: do not reopen it.
        if (!WindPopup.IsOpen && DateTime.UtcNow - windClosedAt < TimeSpan.FromMilliseconds(250)) return;
        if (WindPopup.IsOpen) { WindPopup.IsOpen = false; return; }
        SyncWindPill();
        WindPopup.IsOpen = true;
        WindDial.Focus();
    }

    /// <summary>The dial turned: the direction box takes the value, exactly as a drag of the arrow in the view does.</summary>
    void OnWindDial(double degrees)
    {
        if (syncingWind) return;
        WindDirBox.Text = degrees.ToString("0");
    }

    void OnWindSpeedSlider(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (syncingWind || WindSpeedBox == null) return;
        WindSpeedBox.Text = e.NewValue.ToString("0.#");
    }

    /// <summary>The pill, the dial, the slider and the cardinal letter follow the two boxes (the status tick calls this
    /// whenever their text changes, whoever changed it: typing, the arrow drag, the live link, a sweep).</summary>
    void SyncWindPill()
    {
        if (WindPillText == null) return;
        syncingWind = true;
        try
        {
            double from = NumInput.TryParse(WindDirBox.Text, out double d) ? ((d % 360) + 360) % 360 : windFromValid;
            double speed = NumInput.TryParse(WindSpeedBox.Text, out double s) ? Math.Clamp(s, 0, 40) : windSpeedValid;
            string card = CompassDial.Cardinal(from);
            WindPillText.Text = $"{card} {from:0}° · {speed:0.#} m/s";
            WindCardinalText.Text = $"clockwise from north · {card}";
            if (Math.Abs(WindDial.Degrees - from) > 0.01) WindDial.Degrees = from;
            if (Math.Abs(WindSpeedSlider.Value - speed) > 0.01) WindSpeedSlider.Value = speed;
        }
        finally { syncingWind = false; }
    }

    // ---------------------------------------------------------------- appearance

    void OnTheme(object? sender, RoutedEventArgs e)
    {
        string theme = (sender as Control)?.Tag as string ?? "Dark";
        settings.Theme = theme;
        settings.Save();
        ApplyTheme(theme);
        UpdateLegend();                                   // the speed bar follows the trail gain of the new palette
    }

    void ApplyTheme(string theme)
    {
        Appearance.Apply(theme);
        bool light = Appearance.IsLight(theme);
        ThemeDarkToggle.IsChecked = !light;
        ThemeLightToggle.IsChecked = light;
        if (Viewport3D?.Renderer is { } renderer) renderer.LightTheme = light;   // the 3D view goes light too
    }
}

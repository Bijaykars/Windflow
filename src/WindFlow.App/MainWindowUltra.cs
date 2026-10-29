using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WindFlow.Core;

namespace WindFlow.App;

// Ultra quality, window side: the compact confirm dialog (headline, three short lines, a collapsed Details), the resume question, and the time-limit buttons.
// Everything that waits (the saved-run check, the speed probe) runs off the UI thread, and the solver thread is never asked to wait on the UI: the decision is made
// here, before the pass starts, and handed over (LiveSolver.UltraChoiceWanted).
public partial class MainWindow
{
    readonly CancellationTokenSource ultraUiCts = new();   // cancelled when the window closes: the saved-run check stops with it
    UltraChoice? ultraChoice;                               // what the user chose for the next Ultra pass (null = nothing to resume or delete)

    static readonly Avalonia.Media.IBrush UltraWarn = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E8A838"));

    Window UltraDialog(string heading, IEnumerable<Control> body, Button no, Button yes, int width)
    {
        var title = new TextBlock { Text = heading };
        title.Classes.Add("section");
        no.Classes.Add("tool"); yes.Classes.Add("tool");
        var panel = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 10 };
        panel.Children.Add(title);
        foreach (var c in body) panel.Children.Add(c);
        panel.Children.Add(new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { no, yes } });
        return new Window
        {
            Title = heading, Width = width, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = FontFamily,
            Background = this.FindResource("Panel") as Avalonia.Media.IBrush, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
            Content = panel,
        };
    }

    static TextBlock Line(string text, bool warn = false)
    {
        var t = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        t.Classes.Add("rowSub");
        if (warn) t.Foreground = UltraWarn;
        return t;
    }

    /// <summary>The Ultra options as the pass will see them, for the saved-run hash: the app's physics settings with the Ultra recipe.</summary>
    RunOptions UltraHashOptions() =>
        AccurateRecipe.Options(AccurateMode.Ultra, new RunOptions { OpeningModel = openingModelSetting, WallModel = live?.WallModelWanted ?? new RunOptions().WallModel }, model, null);

    /// <summary>The plan for the model on screen (the window's own, loaded or not, the live solve running, paused or not started) and the wind box as it reads now.
    /// With nothing to simulate it is an infeasible plan whose refusal says so, so the dialog still opens. <paramref name="saved"/>: plan the grid of a saved run instead.</summary>
    UltraPlan PlanUltraFromWindow(UltraManifest? saved = null, bool allowMeasuring = true)
    {
        try
        {
            var wind = ReadWind();
            if (live == null) throw new InvalidOperationException("the solver is not available");
            return saved != null ? live.PlanUltraResume(model, wind, saved, settings.UltraCapHours, allowMeasuring) : live.PlanUltra(model, wind, settings.UltraCapHours, allowMeasuring);
        }
        catch (ArgumentException ex)   // Voxelizer: nothing to simulate
        {
            return new UltraPlan { Feasible = false, Refusal = "Ultra needs a building to simulate: " + ex.Message + ". Load a model or the sample first." };
        }
    }

    /// <summary>The compact confirm dialog. <paramref name="refresh"/> rewrites it for a new plan (the speed probe finished); <paramref name="details"/> is the collapsed expander.</summary>
    Window BuildUltraConfirm(UltraPlan plan, out Action<UltraPlan> refresh, out Expander? details)
    {
        var cancel = new Button { Content = "Cancel", IsDefault = true, IsCancel = true };
        if (!plan.Feasible)
        {
            var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true };
            var refuse = UltraDialog("Ultra is not available", [Line(plan.Refusal)], new Button { IsVisible = false }, ok, 480);
            ok.Click += (_, _) => refuse.Close(false);
            refresh = _ => { };
            details = null;
            return refuse;
        }
        var start = new Button { Content = "Start" };
        var headline = new TextBlock { FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var shortBox = new StackPanel { Spacing = 6 };
        var warnBox = new StackPanel { Spacing = 6 };
        var detailBox = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(0, 6, 0, 0) };
        var expander = new Expander { Header = "Details", IsExpanded = false, Content = detailBox };
        void Fill(UltraPlan p)
        {
            headline.Text = p.Headline;
            shortBox.Children.Clear(); warnBox.Children.Clear(); detailBox.Children.Clear();
            var lines = p.ShortLines();
            for (int i = 0; i < lines.Length; i++)
            {
                bool fallbackLine = p.CompactFallback && i == lines.Length - 1;
                (fallbackLine ? warnBox : shortBox).Children.Add(Line(lines[i], fallbackLine));
            }
            if (p.DiskShort) warnBox.Children.Add(Line(p.DiskWarning, warn: true));
            foreach (var d in p.DetailLines()) detailBox.Children.Add(Line(d));
            start.IsEnabled = !p.DiskShort;
        }
        Fill(plan);
        refresh = Fill;
        details = expander;
        var dialog = UltraDialog("Ultra run", [headline, shortBox, warnBox, expander], cancel, start, 520);
        cancel.Click += (_, _) => dialog.Close(false);
        start.Click += (_, _) => dialog.Close(true);
        return dialog;
    }

    /// <summary>Start or Cancel. While the startup speed probe is still running the headline says so and updates when it finishes (a fallback only if no result ever arrives).</summary>
    async Task<bool> ConfirmUltra(UltraPlan plan, Func<UltraPlan>? replan = null)
    {
        var dialog = BuildUltraConfirm(plan, out var refresh, out _);
        if (plan.MeasuringSpeed && replan != null)
            _ = Task.Run(async () =>
            {
                try { await Task.WhenAny(StartupTier.Begin(), Task.Delay(30000)); } catch (Exception) { /* a failed probe: the fallback below */ }
                Dispatcher.UIThread.Post(() => { try { refresh(replan()); } catch (Exception) { /* the dialog was closed meanwhile */ } });
            });
        return await dialog.ShowDialog<bool>(this);
    }

    /// <summary>"Resume from hh:mm done" (1), "Start over" (2), or closed (0). With <paramref name="canResume"/> false (the saved grid no longer fits) only Start over is offered.</summary>
    Window BuildResumeDialog(UltraManifest m, bool canResume = true, string note = "")
    {
        var over = new Button { Content = "Start over" };
        var resume = new Button { Content = $"Resume from {m.DoneText} done", IsDefault = true, IsEnabled = canResume };
        var lines = new List<Control>
        {
            Line($"Written {m.Written}: {m.DoneText} (hh:mm) of the run is done, step {m.Step}, {m.Batches} batches."),
            Line("Resume continues from there. Start over deletes the saved run."),
        };
        if (note.Length > 0) lines.Add(Line(note, warn: true));
        var dialog = UltraDialog("A saved Ultra run was found", lines, over, resume, 480);
        over.Click += (_, _) => dialog.Close(2);
        resume.Click += (_, _) => dialog.Close(1);
        return dialog;
    }

    /// <summary>Everything that happens when Ultra is clicked, before the pass starts: the plan, the saved-run check (off the UI thread, cancellable), the resume question and the estimate.</summary>
    async Task<(bool Go, UltraChoice? Choice)> UltraStartFlow()
    {
        var plan = PlanUltraFromWindow();
        if (!plan.Feasible) { await ConfirmUltra(plan); return (false, null); }
        StatusText.Text = "Ultra: checking for a saved run…";
        var snapshot = BuildingModel.FromJson(model.ToJson());
        var wind = ReadWind();
        var hashOptions = UltraHashOptions();
        UltraProbe probe;
        try { probe = await Task.Run(() => UltraCheckpoint.Probe(snapshot, wind, hashOptions, new UltraSettings { Log = s => Log.Info("ultra: " + s) }, ultraUiCts.Token), ultraUiCts.Token); }
        catch (OperationCanceledException) { return (false, null); }
        finally { StatusText.Text = ""; }
        UltraChoice? choice;
        Func<UltraPlan> replan = () => PlanUltraFromWindow(allowMeasuring: false);
        if (probe.Found != null)
        {
            var resumePlan = PlanUltraFromWindow(probe.Found);
            int pick = await BuildResumeDialog(probe.Found, resumePlan.Feasible, resumePlan.Feasible ? "" : resumePlan.Refusal).ShowDialog<int>(this);
            if (pick == 0) return (false, null);
            if (pick == 1) { plan = resumePlan; choice = new UltraChoice(probe.Hash, UltraDecision.Resume, probe.Found); replan = () => PlanUltraFromWindow(probe.Found, allowMeasuring: false); }
            else choice = new UltraChoice(probe.Hash, UltraDecision.StartOver, null);
        }
        else choice = new UltraChoice(probe.Hash, UltraDecision.Skip, null);
        bool go = await ConfirmUltra(plan, replan);
        return (go, go ? choice : null);
    }

    void OnUltraCap(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && int.TryParse(tag, out int hours) && Ultra.CapHourChoices.Contains(hours))
        {
            settings.UltraCapHours = hours;
            settings.Save();
            if (live != null) live.UltraCapHours = hours;
            Log.Info($"accurate: Ultra time limit {hours} h");
        }
        ShowAccurateControls();
    }

    void SaveWindowPng(Window w, string path)
    {
        double scale = w.RenderScaling;
        int pw = Math.Max(8, (int)(w.Bounds.Width * scale)), ph = Math.Max(8, (int)(w.Bounds.Height * scale));
        using var target = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(pw, ph), new Avalonia.Vector(96 * scale, 96 * scale));
        target.Render(w);
        var bgra = new byte[pw * ph * 4];
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
        target.CopyPixels(new Avalonia.PixelRect(0, 0, pw, ph), pin.AddrOfPinnedObject(), bgra.Length, pw * 4);
        pin.Free();
        var rgba = new byte[pw * ph * 4];
        for (int i = 0; i < pw * ph; i++) { rgba[i * 4] = bgra[i * 4 + 2]; rgba[i * 4 + 1] = bgra[i * 4 + 1]; rgba[i * 4 + 2] = bgra[i * 4]; rgba[i * 4 + 3] = 255; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Bench.WritePng(path, pw, ph, rgba, pw * 4);
    }

    /// <summary>--shotmode ultra-dialog | ultra-resume: sample loaded, nothing running; the Ultra toggle is clicked through the real handler (or the resume dialog opened with a made-up
    /// manifest) and saved as PNGs next to &lt;path&gt;: the window, the dialog with Details collapsed and (ultra-dialog) with Details expanded.</summary>
    async Task UltraShotSequence(string path, bool resume)
    {
        Viewport3D.Renderer.InputEnabled = false;
        await Task.Delay(1500);
        string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path));
        Window dialog;
        Expander? details = null;
        if (resume)
        {
            var m = new UltraManifest { Sequence = 3, Step = 412_000, Batches = 41, WallUsedSeconds = 7 * 3600 + 25 * 60, Written = DateTime.Now.AddMinutes(-12).ToString("yyyy-MM-dd HH:mm:ss") };
            dialog = BuildResumeDialog(m);
            dialog.Show(this);
        }
        else
        {
            AccurateUltraToggle.IsChecked = true;
            OnAccurateMode(AccurateUltraToggle, new RoutedEventArgs());   // the real click path: plans from the window's model and opens the dialog
            for (int i = 0; i < 100 && OwnedWindows.Count == 0; i++) await Task.Delay(100);
            dialog = OwnedWindows.FirstOrDefault() ?? throw new InvalidOperationException("the Ultra confirm dialog did not open");
            details = (dialog.Content as StackPanel)?.Children.OfType<Expander>().FirstOrDefault();
            for (int i = 0; i < 60 && FindText(dialog, "Measuring GPU speed"); i++) await Task.Delay(500);   // the probe finishes and the headline updates
        }
        await Task.Delay(800);
        SaveWindowPng(dialog, resume ? stem + "-dialog.png" : stem + "-dialog-collapsed.png");
        if (details != null)
        {
            details.IsExpanded = true;
            await Task.Delay(800);
            SaveWindowPng(dialog, stem + "-dialog-expanded.png");
        }
        shotNote = $"mode {(resume ? "ultra-resume" : "ultra-dialog")} · dialog open {dialog.IsVisible} · Ultra toggle checked {AccurateUltraToggle.IsChecked}";
        await SaveShell(path);
        dialog.Close(resume ? 0 : false);
        await Task.Delay(300);
        File.AppendAllText(Path.ChangeExtension(path, ".txt"), $"after Cancel: Ultra checked {AccurateUltraToggle.IsChecked} · Standard checked {AccurateStandardToggle.IsChecked} · mode {live?.AccurateModeWanted}{System.Environment.NewLine}");
        Close();
    }

    static bool FindText(Window w, string text) =>
        w.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text != null && t.Text.Contains(text, StringComparison.Ordinal));
}

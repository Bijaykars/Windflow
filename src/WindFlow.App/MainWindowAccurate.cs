using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using WindFlow.Core;
using WindFlow.Report;

namespace WindFlow.App;

/// <summary>"Preview, then Accurate" in the shell: the strip above the trust line (which result the numbers are, a
/// calm bar while the Accurate pass works, its stage in plain words), the Accurate grid line in Settings, and the
/// headless shot that photographs the pass half-way and finished. The state machine itself is Accurate.cs.</summary>
public partial class MainWindow
{
    int passCycle = -1;
    double passShown;
    AccuratePhase passPhase = AccuratePhase.Preview;

    /// <summary>Status tick: follow the solver's tracker. The bar only ever grows inside one cycle (the tracker
    /// already guarantees it; this guards the transition too); a new cycle, i.e. an edit, is the only way back.</summary>
    void UpdatePass()
    {
        if (live is not { } s || !(s.Active || s.State == LiveSolver.Mode.Failed) || EmptyState.IsVisible)
        {
            PassPanel.IsVisible = false;
            return;
        }
        var t = s.Accurate;
        var phase = t.Phase;
        if (t.Cycle != passCycle) { passCycle = t.Cycle; passShown = 0; }
        if (phase != passPhase)
        {
            passPhase = phase;
            statsVersion = -1;              // the rows change look (provisional or solid) with the state
        }
        bool working = t.Running;
        bool done = phase == AccuratePhase.Accurate;
        passShown = Math.Max(passShown, done ? 1 : working ? t.Progress : 0);

        // The landed pass's stop reason, from the status objects it left on the result (never from its log text).
        var convergence = done ? s.AccurateStats?.Convergence : null;
        bool capped = done && convergence?.Status.Reason != ConvergenceStopReason.Converged;
        string recipe = s.AccurateModeWanted == AccurateMode.High ? "High" : s.AccurateModeWanted == AccurateMode.Ultra ? "Ultra" : "Standard";

        PassPanel.IsVisible = true;
        PassTagText.Text = done ? ResultStatus.TagText(convergence) : "PREVIEW";   // S6-03 / S6-07
        PassTag.Classes.Set("done", done);
        PassTag.Classes.Set("capped", capped);
        PassTrack.IsVisible = working || done;
        PassFill.Classes.Set("working", working && s.State == LiveSolver.Mode.Running);
        PassFill.Classes.Set("done", done);
        PassFill.Classes.Set("capped", capped);   // RW-06: a time-limited pass is amber, not the settled green
        PassFill.RenderTransform = TransformOperations.Parse(
            $"scaleX({passShown.ToString("0.###", CultureInfo.InvariantCulture)})");

        string text;
        if (done && convergence is { UltraDx: > 0 }) text = $"{ResultStatus.StripText(convergence, s.AccurateCells / 1e6)}";
        else if (done) text = $"{recipe} · {ResultStatus.Text(convergence)} · {s.AccurateCells / 1e6:0.0}M cells at {s.AccurateDx * 1000:0} mm";
        else if (working && s.State == LiveSolver.Mode.Paused) text = $"Averaged result · paused at {passShown * 100:0} %";
        else if (working) text = AccurateTracker.StageText(phase, passShown, t.EtaSeconds, "", t.EtaPartial, t.Detail);
        else if (phase == AccuratePhase.Off) text = $"directional · {t.OffWhy}";
        else if (locked) text = "directional · locked before the averaged pass";
        else text = s.Stats == null || s.Trust.StartsWith("averaging", StringComparison.Ordinal) || s.Stale
            ? "directional · the averaged pass starts once the air settles"
            : "directional · starting the averaged pass…";
        PassText.Text = text;
        ToolTip.SetTip(PassPanel, done
            ? (capped
                ? (ResultStatus.TooShort(convergence)
                    ? "Time-limited, too short for ranges: the pass stopped before it had enough batches, so the rooms show ± —. " +
                      "The numbers are the mean of the batches so far; use a longer budget. "
                    : "Time-limited: the pass stopped at its time budget before every room's range closed. The numbers are the " +
                      "mean of the averaged batches and each room's ± is its 95 % averaging range; a longer budget narrows it. ")
                : "Settled: the finer whole-building grid, averaged until every room's air exchange was steady to within its aim " +
                  "(5 % or 0.2 ACH; the model's own number, not a check against measurements). ") +
              "Export PDF and Lock use it. Any change to wind, openings, devices or the environment goes back to Preview."
            : "Preview: the fast live run, good for direction. Once it settles, the averaged pass starts on its own on a " +
              "finer grid; the numbers turn solid when it lands. Any change restarts the cycle.");
    }

    // ---------------------------------------------------------------- Accurate controls (Basic and Expert, above the pass strip)

    /// <summary>The focus room captured when "Fine box" was switched on (Id), so clicking other rooms later does not move the box
    /// and restart the pass. Null: no box.</summary>
    string? focusRoomId;
    object? ultraTip;                   // the XAML tooltip of the Ultra toggle, kept to restore after Thermal goes off
    object? focusBoxTip;                // the XAML tooltip, kept to restore after Thermal goes off

    void InitAccurateControls()
    {
        settings.HighAccuracyCapMinutes = HighAccuracy.SnapCapMinutes(settings.HighAccuracyCapMinutes);
        ShowAccurateControls();
    }

    AccurateMode shownMode = AccurateMode.Standard;   // the mode the toggles last settled on (what Cancel in the Ultra dialog goes back to)

    /// <summary>Standard / High / Ultra are a radio set: clicking the checked one keeps it. Ultra first shows its estimate and asks.</summary>
    async void OnAccurateMode(object? sender, RoutedEventArgs e)
    {
        var picked = ReferenceEquals(sender, AccurateUltraToggle) ? AccurateMode.Ultra : ReferenceEquals(sender, AccurateHighToggle) ? AccurateMode.High : AccurateMode.Standard;
        if (picked == AccurateMode.Ultra && shownMode != AccurateMode.Ultra)
        {
            if (thermalWanted) { SetModeToggles(shownMode); StatusText.Text = Ultra.ToggleTooltipThermal; return; }
            bool go;
            try { var flow = await UltraStartFlow(); go = flow.Go; ultraChoice = flow.Choice; }
            catch (Exception ex)   // never an unhandled exception on the UI thread
            {
                Log.Warn($"ultra: the confirm dialog failed ({ex.GetType().Name}: {ex.Message})");
                StatusText.Text = "Ultra: could not prepare the estimate (" + ex.Message + ")";
                go = false;
            }
            if (!go) { SetModeToggles(shownMode); ShowAccurateControls(); return; }
        }
        else if (picked == AccurateMode.Ultra) picked = AccurateMode.Ultra;   // clicking the checked one keeps it
        SetModeToggles(picked);
        if (picked != AccurateMode.High) { FocusBoxToggle.IsChecked = false; focusRoomId = null; }
        ApplyAccurateControls();
    }

    void SetModeToggles(AccurateMode m)
    {
        AccurateHighToggle.IsChecked = m == AccurateMode.High;
        AccurateUltraToggle.IsChecked = m == AccurateMode.Ultra;
        AccurateStandardToggle.IsChecked = m == AccurateMode.Standard;
    }

    AccurateMode ModeFromToggles() => AccurateUltraToggle.IsChecked == true ? AccurateMode.Ultra : AccurateHighToggle.IsChecked == true ? AccurateMode.High : AccurateMode.Standard;

    void OnAccurateCap(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && int.TryParse(tag, out int minutes))
        {
            settings.HighAccuracyCapMinutes = HighAccuracy.SnapCapMinutes(minutes);
            settings.Save();                                   // LiveSolver reads the setting when the next High pass opens
            Log.Info($"accurate: High time budget {settings.HighAccuracyCapMinutes} min");
        }
        ShowAccurateControls();
    }

    /// <summary>On: capture the room selected or isolated right now as the focus; nothing selected keeps it off and says so.</summary>
    void OnFocusBox(object? sender, RoutedEventArgs e)
    {
        if (FocusBoxToggle.IsChecked == true)
        {
            int index = Viewport3D.Renderer.SelectedRoom;
            if ((uint)index >= (uint)model.Rooms.Count)
            {
                FocusBoxToggle.IsChecked = false;
                StatusText.Text = "Fine box: click a room in the list or the view first, then switch it on.";
                return;
            }
            focusRoomId = model.Rooms[index].Id;
        }
        else focusRoomId = null;
        ApplyAccurateControls();
    }

    /// <summary>Push the choice to the solver (safe before Start; a running or finished pass with another recipe restarts) and repaint.</summary>
    void ApplyAccurateControls()
    {
        var mode = ModeFromToggles();
        shownMode = mode;
        if (live != null) { live.UltraCapHours = settings.UltraCapHours; live.UltraChoiceWanted = mode == AccurateMode.Ultra ? ultraChoice : null; }
        live?.SetAccurateMode(mode, mode == AccurateMode.High ? focusRoomId : null);
        ShowAccurateControls();
    }

    void ShowAccurateControls()
    {
        if (thermalWanted && AccurateUltraToggle.IsChecked == true) { SetModeToggles(AccurateMode.Standard); shownMode = AccurateMode.Standard; live?.SetAccurateMode(AccurateMode.Standard, null); }   // Ultra is wind only
        bool high = AccurateHighToggle.IsChecked == true, ultra = AccurateUltraToggle.IsChecked == true;
        AccurateUltraToggle.IsEnabled = !thermalWanted;
        ToolTip.SetTip(AccurateUltraToggle, thermalWanted ? Ultra.ToggleTooltipThermal : ultraTip ??= ToolTip.GetTip(AccurateUltraToggle));
        UltraCapGroup.IsVisible = ultra;
        int ucap = settings.UltraCapHours;
        UCap6Toggle.IsChecked = ucap == 6; UCap12Toggle.IsChecked = ucap == 12; UCap20Toggle.IsChecked = ucap == 20; UCap24Toggle.IsChecked = ucap == 24;
        HighCapGroup.IsVisible = high;
        FocusBoxToggle.IsVisible = high;
        AccurateModeNote.IsVisible = high;            // Standard keeps the footer to one row; the tooltips carry the long form
        int cap = settings.HighAccuracyCapMinutes;
        Cap5Toggle.IsChecked = cap == 5;
        Cap10Toggle.IsChecked = cap == 10;
        Cap20Toggle.IsChecked = cap == 20;
        Cap30Toggle.IsChecked = cap == 30;
        var focus = focusRoomId == null ? null : model.Rooms.FirstOrDefault(r => r.Id == focusRoomId);
        FocusBoxToggle.IsChecked = focus != null;
        FocusBoxToggle.Content = focus != null ? $"Fine box on {DisplayName(focus.Id, focus.Name)}" : "Fine box on the selected room";
        // The nested box is isothermal only (the plan drops it under Thermal): greyed out with the reason, like the wall model.
        focusBoxTip ??= ToolTip.GetTip(FocusBoxToggle);
        FocusBoxToggle.IsEnabled = !thermalWanted;
        ToolTip.SetTip(FocusBoxToggle, thermalWanted ? "Not available while Thermal is on: the fine box is isothermal only. Switch Thermal off to use it." : focusBoxTip);
        AccurateModeNote.Text = high
            ? $"High (gusty wind): gusts and supply turbulence, {cap} min budget; numbers vary, shown with ranges. Standard: steady wind, up to {LiveSolver.AccurateCapSeconds / 60:0.#} min."
            : $"Standard: steady wind, up to {LiveSolver.AccurateCapSeconds / 60:0.#} min (large models end time-limited). High (gusty wind): gusts and supply turbulence; numbers vary, shown with ranges.";
    }

    // ---------------------------------------------------------------- Settings: Accurate grid

    string accurateNote = "";

    void InitAccurateSetting()
    {
        if (live != null) live.AccurateCellsTyped = Math.Max(0, settings.AccurateCells);
        ShowAccurateSetting(force: true);
    }

    void OnAccurateKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitAccurate(AccurateBox.Text); e.Handled = true; }
    }

    void OnAccurateLost(object? sender, RoutedEventArgs e) => CommitAccurate(AccurateBox.Text);

    /// <summary>The budget the next Accurate pass gets on this machine: typed or the tier's, clamped to VRAM.</summary>
    (int Cells, bool Clamped, int Cap) AccurateResolved(int typed)
    {
        var tier = StartupTier.Chosen;
        bool gpu = tier == null || tier.ProbeMlups > 0 && !tier.Source.Contains("CPU", StringComparison.OrdinalIgnoreCase);
        int previewCells = live is { Cells: > 0 } l ? l.Cells : tier?.TargetCells ?? 1_500_000;
        double vram = gpu ? tier?.VramGb ?? 0 : 0;
        var (cells, clamped) = AccurateBudget.Resolve(typed, StartupTier.AccurateCells(gpu), vram, previewCells, thermalWanted);
        return (cells, clamped, AccurateBudget.VramCap(vram, previewCells, thermalWanted));
    }

    /// <summary>Expert: a number ("4M", "2500k", "3000000") fixes the budget, clamped to what VRAM holds; empty or
    /// "auto" hands it back to the tier. Applies to the next Accurate pass.</summary>
    void CommitAccurate(string? typed)
    {
        string text = (typed ?? "").Trim().Replace(",", "").Replace(" ", "");
        accurateNote = "";
        int wanted;
        if (text.Length == 0 || text.Equals("auto", StringComparison.OrdinalIgnoreCase)) wanted = 0;
        else
        {
            double scale = 1;
            if (text.EndsWith('k') || text.EndsWith('K')) { scale = 1e3; text = text[..^1]; }
            else if (text.EndsWith('m') || text.EndsWith('M')) { scale = 1e6; text = text[..^1]; }
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v <= 0 || double.IsNaN(v))
            {
                accurateNote = "type a cell count, or Auto";
                ShowAccurateSetting(force: true);
                return;
            }
            wanted = (int)Math.Min(int.MaxValue, Math.Round(v * scale));
            var (_, _, cap) = AccurateResolved(wanted);
            wanted = AccurateBudget.Typed(wanted, live is { CellTarget: > 0 } t ? t.CellTarget : StartupTier.Chosen?.TargetCells ?? 1_500_000, cap, out accurateNote);   // RQ-04: the floor is on the Preview target, not a Full grid's count
        }
        if (wanted != settings.AccurateCells)
            Log.Info($"accurate: grid setting {(wanted > 0 ? AccurateBudget.Fmt(wanted) : "auto")}{(accurateNote.Length > 0 ? $" ({accurateNote})" : "")}");
        settings.AccurateCells = wanted;
        settings.Save();
        if (live != null) live.AccurateCellsTyped = wanted;
        if (accurateNote.Length == 0) accurateNote = "applies to the next averaged pass";
        ShowAccurateSetting(force: true);
    }

    void ShowAccurateSetting(bool force = false)
    {
        int typed = Math.Max(0, settings.AccurateCells);
        var (cells, clamped, _) = AccurateResolved(typed);
        string source = typed > 0 ? "fixed" : StartupTier.Chosen == null ? "auto (measuring…)" : "auto";
        AccurateText.Text = $"{source} · {AccurateBudget.Fmt(cells)} cells{(clamped ? " (VRAM)" : "")}";
        if (force || !AccurateBox.IsKeyboardFocusWithin) AccurateBox.Text = typed > 0 ? AccurateBudget.Fmt(typed) : "Auto";
        AccurateNote.Text = accurateNote;
    }

    // ---------------------------------------------------------------- headless shot

    /// <summary>--shotmode accurate (with --gpu 60 for the reported timings): Basic, sample house, Run. Photograph the
    /// Accurate pass half-way (the bar and the provisional rows), then make an edit mid-pass (wind 270 -> 265) and check
    /// it cancels, then let the new cycle finish and photograph the solid numbers. The sidecar carries the timings,
    /// cell counts, every progress sample (never decreasing inside a cycle) and the labels read off the window.</summary>
    async Task AccurateShotSequence(string path)
    {
        ApplyMode(false);
        Viewport3D.Renderer.InputEnabled = false;
        string donePath = Path.GetFileName(path).Contains("progress", StringComparison.Ordinal)
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileName(path).Replace("progress", "done"))
            : Path.ChangeExtension(path, null) + "-done.png";
        var lines = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        StartLive();
        var s = live!;
        string Row() => RoomList.ItemsSource is IEnumerable<RoomRow> rows && rows.FirstOrDefault() is { } first
            ? $"{first.Name} ACH \"{first.Ach}\" provisional {first.Provisional}" : "(no rows)";
        string Labels() => $"tag \"{PassTagText.Text}\" · text \"{PassText.Text}\" · trust \"{TrustHead.Text}\" · rooms tag " +
                           $"{(RoomsStateTag.IsVisible ? "preview" : "hidden")} · first row {Row()}";

        // Progress samples, one list per cycle: the bar must never go back inside one.
        var samples = new List<(int Cycle, double T, AccuratePhase Phase, double Progress, double Eta)>();
        async Task<bool> WaitFor(Func<bool> done, double seconds)
        {
            var until = clock.Elapsed.TotalSeconds + seconds;
            while (clock.Elapsed.TotalSeconds < until)
            {
                var t = s.Accurate;
                samples.Add((t.Cycle, clock.Elapsed.TotalSeconds, t.Phase, t.Progress, t.EtaSeconds));
                if (done()) return true;
                await Task.Delay(250);
            }
            return false;
        }

        bool started = await WaitFor(() => s.Accurate.Running, 240);
        lines.Add($"preview    settled {s.PreviewSettleSeconds:0.0} s after Run · {s.Cells / 1000.0:0.#}k cells @ {s.Dx * 1000:0} mm · " +
                  $"{s.RateText()} · Accurate {(started ? "started" : "DID NOT START")} at {clock.Elapsed.TotalSeconds:0.0} s");
        bool half = started && await WaitFor(() => s.Accurate.Phase == AccuratePhase.Averaging && s.Accurate.Progress >= 0.35, 300);
        await Task.Delay(1200);             // one more tick: the bar's transition catches up
        statsVersion = -1;
        RefreshPanel();
        await Task.Delay(300);
        var mid = s.Accurate;
        lines.Add($"mid-pass   {(half ? "" : "TIMED OUT ")}{mid.Phase} {mid.Progress * 100:0.0} % · ETA {AccurateTracker.Left(mid.EtaSeconds)} " +
                  $"({mid.EtaSeconds:0.0} s) · {s.AccurateCells / 1000.0:0.#}k cells @ {s.AccurateDx * 1000:0} mm · {s.RateText()} · " +
                  $"{s.StepsPerSecond:0} steps/s · duty {s.Duty * 100:0} %");
        lines.Add($"labels     {Labels()}");
        shotNote = "accurate pass mid-way · " + Labels();
        await SaveShell(path);
        File.AppendAllLines(Path.ChangeExtension(path, ".txt"), lines);

        // An edit mid-pass: the wind box, through the same debounced path typing uses.
        int cycleBefore = s.Accurate.Cycle;
        double editAt = clock.Elapsed.TotalSeconds;
        WindDirBox.Text = "265";
        bool cancelled = await WaitFor(() => s.Accurate.Cycle != cycleBefore && !s.Accurate.Running, 10);
        lines.Add($"edit       wind 270 -> 265 at {editAt:0.0} s: {(cancelled ? "cancelled" : "NOT CANCELLED")} in " +
                  $"{clock.Elapsed.TotalSeconds - editAt:0.0} s · phase {s.Accurate.Phase} · progress {s.Accurate.Progress * 100:0} % · " +
                  $"accurate result {(s.AccurateStats == null ? "cleared" : "STILL SET")} · labels {Labels()}");

        bool restarted = await WaitFor(() => s.Accurate.Running, 240);
        double accurateFrom = clock.Elapsed.TotalSeconds;
        lines.Add($"preview    re-settled {s.PreviewSettleSeconds:0.0} s after the edit (warm start) · Accurate {(restarted ? "restarted" : "DID NOT RESTART")}");
        double firstEta = double.NaN;
        await WaitFor(() => !double.IsNaN(s.Accurate.EtaSeconds) || !s.Accurate.Running, 30);
        firstEta = s.Accurate.EtaSeconds;
        double etaAt = clock.Elapsed.TotalSeconds;
        bool finished = restarted && await WaitFor(() => s.Accurate.Phase is AccuratePhase.Accurate or AccuratePhase.Off, 600);
        lines.Add($"accurate   {(finished ? s.Accurate.Phase.ToString() : "TIMED OUT")} · pass {s.AccuratePassSeconds:0.0} s (Accurate start to numbers) · " +
                  $"{s.AccurateCells / 1000.0:0.#}k cells @ {s.AccurateDx * 1000:0} mm · first ETA {firstEta:0.0} s at +{etaAt - accurateFrom:0.0} s, " +
                  $"actual {clock.Elapsed.TotalSeconds - etaAt:0.0} s · {s.Trust}");
        await Task.Delay(1500);
        statsVersion = -1;
        RefreshPanel();
        await Task.Delay(1200);
        lines.Add($"labels     {Labels()}");
        lines.Add($"report     source {(IsPreviewResult(ReportSource()!) ? "Preview" : "Accurate")} · lock would hold " +
                  $"{(live?.IsAccurate(live.Stats) == true ? "the Accurate result" : "a Preview result")}");
        foreach (var r in s.Stats?.Rooms ?? [])
            lines.Add($"room       {r.Room.Name,-12} {Standards.FreshText(r)} fresh-air ACH · total {r.Ach:0.00} · still {r.StagnantFraction * 100:0} % · age {AgeText(r.MeanAge)}");

        // Monotonic inside each cycle.
        int backwards = 0;
        foreach (var group in samples.GroupBy(x => x.Cycle))
        {
            double last = 0;
            foreach (var x in group) { if (x.Progress < last - 1e-9) backwards++; last = Math.Max(last, x.Progress); }
        }
        lines.Add($"progress   {samples.Count} samples over {samples.Select(x => x.Cycle).Distinct().Count()} cycles · " +
                  $"{backwards} backward steps inside a cycle");
        foreach (var x in samples.Where((_, i) => i % 8 == 0))
            lines.Add($"  sample   c{x.Cycle} t {x.T,6:0.0} s  {x.Phase,-9} {x.Progress * 100,5:0.0} %  ETA {AccurateTracker.Left(x.Eta)}");
        shotNote = "accurate done · " + Labels();
        await SaveShell(donePath);
        File.AppendAllLines(Path.ChangeExtension(donePath, ".txt"), lines);
        Close();
    }
    /// <summary>--shotmode accurate-time [--accurate-start cold|warm] [--accurate-settle x] --gpu 60: one Run to a
    /// finished Accurate pass, no edits. The sidecar carries the pass timings (stepping, analysis, total), the step
    /// plan, the GPU contention seen at the start, and every room's numbers, for the cold-versus-warm table.</summary>
    async Task AccurateTimeSequence(string path)
    {
        ApplyMode(false);
        Viewport3D.Renderer.InputEnabled = false;
        var lines = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        StartLive();
        var s = live!;
        SetSliceField(1);                   // pressure slice on from the start: Preview publishes it, Accurate must replace it
        if (Options.AccurateModeName == "high")
        {
            // Through the panel's own controls, so the shot shows them: High, and Fine box on the --focus room when given.
            AccurateHighToggle.IsChecked = true;
            OnAccurateMode(AccurateHighToggle, new RoutedEventArgs());
            if (Options.FocusRoom is { } focusName && RoomIsolation.Find(model, focusName) is var focusIndex and >= 0)
            {
                SelectRoom(focusIndex, frame: false);
                FocusBoxToggle.IsChecked = true;
                OnFocusBox(FocusBoxToggle, new RoutedEventArgs());
            }
        }
        async Task<bool> WaitFor(Func<bool> done, double seconds)
        {
            var until = clock.Elapsed.TotalSeconds + seconds;
            while (clock.Elapsed.TotalSeconds < until) { if (done()) return true; await Task.Delay(250); }
            return false;
        }
        bool started = await WaitFor(() => s.Accurate.Running, 300);
        lines.Add($"slice      before the pass: {s.ScalarPublished}");
        double startAt = clock.Elapsed.TotalSeconds;
        bool finished = started && await WaitFor(() => s.Accurate.Phase is AccuratePhase.Accurate or AccuratePhase.Off, Math.Max(900, s.AccurateCapThisPass + 400));
        await Task.Delay(1500);
        statsVersion = -1;
        RefreshPanel();
        await Task.Delay(800);
        lines.Add($"slice      after the pass: {s.ScalarPublished}");
        SetSliceField(0);
        await Task.Delay(600);
        string mark = s.ScalarPublished = "(cleared by the shot)";
        SetSliceField(1);
        await Task.Delay(1000);
        lines.Add($"slice      off, then on again after the pass: {(s.ScalarPublished == mark ? "NOT REPUBLISHED" : s.ScalarPublished)}");
        lines.Add($"run button \"{RunButton.Content}\" enabled {RunButton.IsEnabled} · tip \"{ToolTip.GetTip(RunButton)}\"");
        lines.Add($"converge   {s.AccurateConvergenceText}");
        lines.Add($"plan       {s.AccuratePlan} · start override \"{Options.AccurateStart}\" settle override {Options.AccurateSettle:0.##}");
        lines.Add($"timing     preview settle {s.PreviewSettleSeconds:0.0} s · stepping {s.AccurateStepSeconds:0.0} s · " +
                  $"total {s.AccuratePassSeconds:0.0} s · {(finished ? s.Accurate.Phase.ToString() : "TIMED OUT")} · started at {startAt:0.0} s · " +
                  $"{s.AccurateCells / 1000.0:0.#}k cells @ {s.AccurateDx * 1000:0} mm · {s.Trust}");
        foreach (var r in s.Stats?.Rooms ?? [])
            lines.Add($"room       {r.Room.Name,-12} {Standards.FreshText(r)} fresh-air ACH · total {r.Ach:0.000} ACH · inflow {r.Inflow:0.0000} m3/s · still {r.StagnantFraction * 100:0} %{(r.FromFineBox ? " · fine box" : "")}");
        // What the panel says about the landed pass: the status line, the chip's tone and every row's ACH cell (± after Accurate).
        var conv = s.AccurateStats?.Convergence;
        lines.Add($"status     pass \"{PassText.Text}\" · chip {(PassTag.Classes.Contains("capped") ? "capped" : PassTag.Classes.Contains("done") ? "done" : "preview")} · " +
                  $"reason {conv?.Status.Reason.ToString() ?? "none"} · rows " +
                  string.Join(" | ", (RoomList.ItemsSource as IEnumerable<RoomRow>)?.Select(r => $"{r.Name} \"{r.Ach}\"") ?? []));
        shotNote = "accurate-time · " + s.AccuratePlan;
        await SaveShell(path);
        // The same landed result in Expert: the rows' second line carries the batch-to-batch variation.
        ApplyMode(true);
        SelectTab(TabView);
        statsVersion = -1;
        RefreshPanel();
        await Task.Delay(800);
        lines.Add($"expert     second line " + string.Join(" | ", (RoomList.ItemsSource as IEnumerable<RoomRow>)?.Take(2).Select(r => $"{r.Name}: {r.Sub}") ?? []));
        await SaveShell(Path.ChangeExtension(path, null) + "-expert.png");
        // And the PDF of it (its own file beside the shot): the ± column, the status line and the ACH footnote.
        if (ReportSource() is { } reported)
        {
            string pdf = Path.ChangeExtension(path, ".pdf");
            await WriteReport(reported, pdf, new ReportOptions { ProjectName = "Sample house", Scenario = ScenarioLine(reported) });
            lines.Add($"report     {pdf} · {(IsPreviewResult(reported) ? "Preview" : "Accurate")} · {ResultStatus.ReportLine(reported.Convergence)}");
        }
        File.AppendAllLines(Path.ChangeExtension(path, ".txt"), lines);
        Close();
    }
}

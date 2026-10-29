using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using WindFlow.App.Viewport;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>PLAN_V2 4.3/4.4: the shell around the viewport. Basic and Expert (driven by <see cref="UiTable"/>), the
/// Expert tabs, the Settings popover, the one trust line with its notes, the particle-count setting with its
/// measured limit, and Lock result.</summary>
public partial class MainWindow
{
    (UiRule Rule, Control Control)[]? ui;
    bool locked;
    int panelTab;
    bool notesOpen;
    List<string> technicalNotes = [];
    readonly HashSet<string> seenNotes = new(StringComparer.Ordinal);
    readonly Dictionary<Control, object?> lockTips = new();
    DateTime settingsClosedAt = DateTime.MinValue;

    // ---------------------------------------------------------------- Basic / Expert

    void OnBasicMode(object? sender, RoutedEventArgs e) => ApplyMode(false);

    void OnExpertMode(object? sender, RoutedEventArgs e) => ApplyMode(true);

    /// <summary>Basic is its own product: load, wind, openings, run, see the air, the rooms, still air, PDF. Expert
    /// adds everything else, organised in tabs. What is visible where is <see cref="UiTable"/>, nothing here.</summary>
    void ApplyMode(bool expert)
    {
        settings.Mode = expert ? "Expert" : "Basic";
        settings.Save();
        BasicToggle.IsChecked = !expert;
        ExpertToggle.IsChecked = expert;
        advancedMode = expert;
        ui ??= UiTable.Resolve(this);
        // only the Expert-only rows: the others are shown by their own state (the box buttons, thermal-only rows), not by the mode
        foreach (var (rule, control) in ui) if (rule.ExpertOnly) control.IsVisible = UiTable.Visible(rule, expert);
        ShowTab(expert || panelTab < UiTable.BasicSections.Length ? panelTab : TabRooms);
        statsVersion = -1;                  // repaint the rows: Expert adds the air-age column and the sweep ticks
        if (!expert)
        {
            // Basic has no Panel toggle, so the panel is always there once a model is.
            PanelToggle.IsChecked = true;
            if (!EmptyState.IsVisible) ResultsPanel.IsVisible = true;
            ExitCompare();
            CancelPlacement();
            SelectDevice(-1);
            var renderer = Viewport3D.Renderer;
            if (renderer.Walls == WallMode.Xray) { renderer.Walls = WallMode.Ghost; GhostToggle.Content = "Ghost"; }
            if (renderer.ShowBox) { BoxToggle.IsChecked = false; OnBoxToggle(null, null!); }
            // The slice stays (it is in both modes now); Basic colours it by air speed only.
            if (renderer.Particles.SliceField != 0) SetSliceField(0);
            Viewport3D.ProbeMode = false;
            ProbeToggle.IsChecked = false;
        }
        RefreshDevices();
        RefreshNotesView();
        RefreshPanel();
    }

    // Panel sections, in the order of UiTable.Sections (the picker's items). Rooms is the default on open.
    const int TabRooms = 0, TabOpenings = 1, TabView = 2, TabDevices = 3, TabStudies = 4;

    /// <summary>The section picker: Rooms, Openings, View, then Devices and Studies in Expert.</summary>
    void SelectTab(int tab)
    {
        panelTab = tab;
        ShowTab(tab);
    }

    void ShowTab(int tab)
    {
        ShowSectionTab(tab);
        // View is the old Results pane (still air, slice, environment) with Expert's display and probe controls under it.
        ResultsPane.IsVisible = tab == TabView;
        ViewPane.IsVisible = tab == TabView;
        RoomsPane.IsVisible = tab == TabRooms;
        OpeningsPane.IsVisible = tab == TabOpenings;
        DevicesPane.IsVisible = tab == TabDevices;
        StudiesPane.IsVisible = tab == TabStudies;
        PanelScroll.Offset = new Vector(0, 0);
        if (tab == TabRooms) EnsureSpaces();
        if (model.Obstacles.Count > 0 && (spaces != null || addingOpening)) RebuildScene();       // the candidate outlines show with the Rooms section only
    }

    /// <summary>The import warning shows under the picker, whatever the section; null hides it.</summary>
    void ShowImportNote(string? text, string? full = null)
    {
        if (text != null) ImportNoteText.Text = text;
        ToolTip.SetTip(ImportNoteText, full);
        ImportNoteText.IsVisible = text != null;
    }

    // ---------------------------------------------------------------- settings popover

    void OnSettings(object? sender, RoutedEventArgs e)
    {
        // The press that light-dismissed the popover is the same click that lands here: do not reopen it.
        if (!SettingsPopup.IsOpen && DateTime.UtcNow - settingsClosedAt < TimeSpan.FromMilliseconds(250)) return;
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
        if (SettingsPopup.IsOpen) ShowAccurateSetting();       // the Preview grid is known once a run has opened
    }

    // ---------------------------------------------------------------- trust line + notes

    void OnNotes(object? sender, RoutedEventArgs e)
    {
        notesOpen = NotesToggle.IsChecked == true;
        RefreshNotesView();
    }

    /// <summary>No analysis pass yet: say what is happening instead of an empty line.</summary>
    void RefreshTrustIdle()
    {
        var state = live?.State ?? LiveSolver.Mode.Stopped;
        TrustHead.Text = state switch
        {
            LiveSolver.Mode.Starting or LiveSolver.Mode.Running or LiveSolver.Mode.Paused => "Solving · first numbers in a few seconds",
            LiveSolver.Mode.Failed => "The solver stopped · see the status line",
            _ => "Not solved yet",
        };
        TrustChip.Background = state == LiveSolver.Mode.Failed ? RoomBad : Muted;
        TrustDetail.Text = "";
        if (technicalNotes.Count > 0) { technicalNotes = []; RefreshNotesView(); }
    }

    /// <summary>One line from the trust and ledger state the solver already publishes. No confidence grade
    /// (PLAN_V2 3.2 waits for solver work): only what the mass balance and the averaging window say.</summary>
    void RefreshTrust(LiveSolver solver, SimulationResult stats)
    {
        var ledger = stats.Ledger;
        var audit = solver.Audit;
        bool accurate = solver.IsAccurate(stats);
        bool averaging = !accurate && solver.Trust.StartsWith("averaging", StringComparison.Ordinal);
        bool cut = audit.CutsIndoorAir && !averaging;
        bool settled = accurate && WindFlow.Report.ResultStatus.IsSettled(stats.Convergence);
        string label = accurate ? WindFlow.Report.ResultStatus.ResultName(stats.Convergence) : "Preview";
        TrustHead.Text = cut ? "The box cuts through a room"
            : averaging ? $"Settling · {Math.Min(solver.AverageSeconds, solver.AverageTarget):0} of {solver.AverageTarget:0} s averaged"
            : ledger.IsPhysical ? $"{label} · mass balance holds" : $"{label} · mass balance is off";
        // Amber, not red, for the box: the solve is healthy, it is the question being asked that is wrong.
        TrustChip.Background = cut || averaging || (accurate && !settled) ? RoomWeak : ledger.IsPhysical ? RoomGood : RoomBad;   // S6-03: green only for a Settled pass
        TrustDetail.Text = cut
            ? $"The box faces cut through {string.Join(", ", audit.CutRoomNames)}, so that air enters and " +
              $"leaves across an open boundary instead of the building. {LiveSolver.BoxCutHint}"
            : averaging
                ? $"{solver.AverageSeconds:0.0} s of {solver.AverageTarget:0.0} s (one flow-through) since the last change · " +
                  $"in {ledger.In:0.00} m³/s · out {ledger.Out:0.00} m³/s"
                : accurate
                    ? $"in {ledger.In:0.00} m³/s · out {ledger.Out:0.00} m³/s · residual {ledger.Residual * 100:0.#} % · " +
                      $"averaged pass on {solver.AccurateCells / 1e6:0.0#}M cells @ {solver.AccurateDx * 1000:0} mm"
                    : $"in {ledger.In:0.00} m³/s · out {ledger.Out:0.00} m³/s · residual {ledger.Residual * 100:0.#} % · " +
                      $"averaged over {solver.AverageSeconds:0.0} s";

        // Everything the solver wants said, once each (Notes.Gather).
        var extra = new List<string>();
        if (audit.DroppedRooms > 0)
            extra.Add($"{audit.DroppedRooms} room{(audit.DroppedRooms == 1 ? "" : "s")} outside the box: no air cells, " +
                      "so they are not in the tables above.");
        extra.AddRange(model.Warnings);          // S4-06: what the Revit exporter dropped or guessed
        extra.Add(solver.BoxNote);
        extra.Add(solver.BoxModeNote);
        extra.AddRange(solver.NestNotes);
        if (accurate) extra.Add(solver.GridNote);                                            // C4: the grid was clamped to graphics memory
        else extra.AddRange(Envelope.PreviewOpenings(stats.Field.Grid, model.Openings));      // C4: 4-6 cells across in Preview is quantised to the grid
        // Calibrated is the default and the reason window and door flows are trustworthy on a coarse grid: say so once.
        if (openingModelSetting == OpeningModel.Calibrated && stats.Openings.Any(o => o.Opening.IsOpen && OpeningCd.Editable(o.Opening)))
            extra.Add(Notes.CalibratedTechnical);
        var notes = Notes.Gather(extra, stats);
        // The full technical text reaches the log once per model and subject: the numbers inside a note move on
        // every pass, so the key is its plain sentence (one per opening, room or device).
        foreach (var n in notes)
            if (seenNotes.Add(Notes.Plain(n))) Log.Warn("solver note: " + n);
        if (!notes.SequenceEqual(technicalNotes))
        {
            technicalNotes = notes;
            RefreshNotesView();
        }
    }

    /// <summary>Basic: "N notes", one plain sentence each. Expert: the same notes in full technical text.</summary>
    void RefreshNotesView()
    {
        var shown = advancedMode ? technicalNotes : technicalNotes.Select(Notes.Plain).Distinct().ToList();
        int n = shown.Count;
        NotesToggle.IsVisible = n > 0;
        NotesToggle.IsChecked = notesOpen;
        NotesToggle.Content = $"{n} note{(n == 1 ? "" : "s")} {(notesOpen ? "▾" : "▸")}";
        NotesScroll.IsVisible = notesOpen && n > 0;
        NotesList.ItemsSource = shown;
    }

    // ---------------------------------------------------------------- particle count (Settings)

    int manualParticles;           // 0 = Auto; > 0 = typed in Settings (or --particles)
    int particleLimit;             // measured: most particles the GPU draws at the display refresh, 0 = not yet
    double limitEstimate;
    string particleNote = "";
    bool particleNoteWarn;
    DateTime limitLoggedAt = DateTime.MinValue;
    static readonly int MonitorHz = Win32.RefreshRate();

    /// <summary>Share of one refresh interval the particle limit may fill, leaving room for the rest of the frame
    /// and for timing noise.</summary>
    const double LimitBudget = 0.8;

    /// <summary>Particle buffers are 288 bytes each (32 state + 16 trail points x 16): never more than 2M, and never
    /// more than 15 % of the render adapter's own memory.</summary>
    static int HardParticleCap()
    {
        double vram = StartupTier.Chosen?.VramGb ?? 0;
        int byVram = vram > 0 ? (int)Math.Min(int.MaxValue, vram * 1e9 * 0.15 / 288) : 250_000;
        return Math.Clamp(byVram, 30_000, 2_000_000);
    }

    void InitParticleSetting()
    {
        manualParticles = Options.Particles > 0 ? Options.Particles : Math.Max(0, settings.ParticleCount);
        if (manualParticles > 0) SetParticleCount(manualParticles, Options.Particles > 0 ? "--particles" : "pinned in Settings");
        ShowParticleSetting();
    }

    void OnParticleKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitParticles(ParticleBox.Text); e.Handled = true; }
    }

    void OnParticleLost(object? sender, RoutedEventArgs e) => CommitParticles(ParticleBox.Text);

    void OnParticleAuto(object? sender, RoutedEventArgs e) => CommitParticles("");

    /// <summary>A number pins the count (auto-adjust off) up to the measured limit; empty or "auto" hands it back to
    /// the startup tier + auto-adjust. "250k" and "1.5M" are understood.</summary>
    void CommitParticles(string? typed)
    {
        string text = (typed ?? "").Trim().Replace(",", "").Replace(" ", "");
        particleNote = "";
        particleNoteWarn = false;
        if (Options.Particles > 0)
        {
            particleNote = "fixed by --particles for this run";
            ShowParticleSetting();
            return;
        }
        int wanted;
        if (text.Length == 0 || text.Equals("auto", StringComparison.OrdinalIgnoreCase)) wanted = 0;
        else
        {
            double scale = 1;
            if (text.EndsWith('k') || text.EndsWith('K')) { scale = 1e3; text = text[..^1]; }
            else if (text.EndsWith('m') || text.EndsWith('M')) { scale = 1e6; text = text[..^1]; }
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v <= 0 || double.IsNaN(v))
            {
                particleNote = "type a number, or leave it empty for Auto";
                particleNoteWarn = true;
                ShowParticleSetting(force: true);
                return;
            }
            wanted = (int)Math.Min(int.MaxValue, Math.Round(v * scale));
            int cap = particleLimit > 0 ? Math.Min(particleLimit, HardParticleCap()) : HardParticleCap();
            if (wanted > cap)
            {
                particleNote = particleLimit > 0 ? $"clamped to {Fmt(cap)}, the limit measured on this GPU"
                                                 : $"clamped to {Fmt(cap)} (limit not measured yet)";
                particleNoteWarn = true;
                wanted = cap;
            }
            if (wanted < 5_000)
            {
                particleNote = "raised to 5k, the fewest that still show the flow";
                particleNoteWarn = true;
                wanted = 5_000;
            }
        }
        if (wanted != manualParticles) Log.Info($"particles: setting {(wanted > 0 ? Fmt(wanted) : "Auto")}" +
                                                (particleNote.Length > 0 ? $" ({particleNote})" : ""));
        manualParticles = wanted;
        settings.ParticleCount = wanted;
        settings.Save();
        if (wanted > 0) SetParticleCount(wanted, "typed in Settings");
        else if (particleAuto != null)
        {
            particleAuto.SetCeiling(locked && particleLimit > 0 ? particleLimit : ParticleAuto.VisualCeiling, jump: locked);
            SetParticleCount(particleAuto.Count, "Auto in Settings");
        }
        ShowParticleSetting(force: true);
    }

    void ShowParticleSetting(bool force = false)
    {
        ParticleAutoButton.IsVisible = manualParticles > 0 && Options.Particles <= 0;   // already Auto: nothing to press
        if (force || !ParticleBox.IsKeyboardFocusWithin)
            ParticleBox.Text = manualParticles > 0 ? manualParticles.ToString("N0", CultureInfo.InvariantCulture) : "Auto";
        string limit = particleLimit > 0 ? $"your GPU: up to ~{Fmt(particleLimit)} at {MonitorHz} fps" : "your GPU: measuring…";
        string state = manualParticles > 0 ? $"fixed at {Fmt(manualParticles)}" : $"auto · {Fmt(particleCount)} now";
        ParticleLimitText.Text = particleNote.Length > 0 ? $"{particleNote} · {limit}" : $"{state} · {limit}";
        ParticleLimitText.Foreground = particleNoteWarn ? RoomWeak : Muted;
    }

    static string Fmt(int n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.##}M" : $"{n / 1000.0:0.#}k";

    static int RoundLimit(double n) => n < 100_000 ? (int)(Math.Round(n / 5_000) * 5_000)
        : n < 1_000_000 ? (int)(Math.Round(n / 10_000) * 10_000) : (int)(Math.Round(n / 100_000) * 100_000);

    /// <summary>Status tick. The particle limit is measured, not guessed: GPU timestamps around the particle pass
    /// (advection + trail draw) give its cost per particle at the current count, and the limit is where that cost,
    /// extrapolated linearly, fills <see cref="LimitBudget"/> of a refresh interval next to everything else the
    /// frame draws. Samples taken while the solver runs share the GPU with it and read high, so once there is an
    /// estimate only solver-idle samples (paused, locked, not running) refine it. Logged when it moves.</summary>
    void UpdateParticleLimit()
    {
        var r = Viewport3D.Renderer;
        int timed = r.TimedParticles;
        float particleMs = r.ParticleGpuMs, frameMs = r.FrameGpuMs;
        bool solving = live is { State: LiveSolver.Mode.Running };
        if (timed > 0 && particleMs > 0.02f && (!solving || limitEstimate <= 0))
        {
            double budget = 1000.0 / MonitorHz * LimitBudget;
            double other = Math.Max(0, frameMs - particleMs);
            double n = timed * Math.Max(0, budget - other) / particleMs;
            limitEstimate = limitEstimate <= 0 || solvingEstimate != solving ? n : 0.9 * limitEstimate + 0.1 * n;
            bool fresh = lockJumpPending && !solving;
            solvingEstimate = solving;
            int rounded = Math.Clamp(RoundLimit(limitEstimate), 30_000, HardParticleCap());
            bool moved = rounded != particleLimit;
            if (moved)
            {
                bool log = particleLimit == 0 || Math.Abs(rounded - particleLimit) > 0.15 * particleLimit ||
                           DateTime.UtcNow - limitLoggedAt > TimeSpan.FromMinutes(1);
                particleLimit = rounded;
                if (log)
                {
                    limitLoggedAt = DateTime.UtcNow;
                    Log.Info($"particle limit: {Fmt(timed)} particles cost {particleMs:0.000} ms of GPU (advection + trails, " +
                             $"timestamps) in a {frameMs:0.00} ms frame{(solving ? " while the solver shares the GPU" : "")}; " +
                             $"budget {LimitBudget * 100:0} % of {1000.0 / MonitorHz:0.0} ms at {MonitorHz} Hz minus {other:0.00} ms " +
                             $"for the rest of the frame -> linear extrapolation ~{Fmt(rounded)} (cap {Fmt(HardParticleCap())})");
                }
            }
            // Locked + Auto: the measured limit is the ceiling. The first solver-idle reading after Lock raises the
            // particles to it at once; later readings only move the ceiling and auto-adjust follows the fps.
            if (locked && manualParticles == 0 && particleAuto != null && (fresh || moved))
            {
                lockJumpPending = false;
                particleAuto.SetCeiling(particleLimit, jump: fresh);
                if (particleAuto.Count != particleCount)
                    SetParticleCount(particleAuto.Count, fresh ? "locked: up to the measured limit" : "locked: the measured limit moved");
            }
        }
        ShowParticleSetting();
    }

    bool solvingEstimate, lockJumpPending;

    // ---------------------------------------------------------------- lock result

    void OnLock(object? sender, RoutedEventArgs e) => SetLocked(LockToggle.IsChecked == true);

    DateTime lockedAt;

    /// <summary>Lock: pause the solver (LiveSolver's own Pause, so Unlock resumes from its state, not from rest) and
    /// stop its analysis pulls, so the GPU does nothing but draw; the particles keep flowing through the averaged
    /// field of the last analysis pass (Core's ToField average, already held as LiveSolver.Stats: no second store);
    /// every flow control in <see cref="UiTable"/> is disabled; the frame cap lifts to the display refresh and Auto
    /// may raise the particles to the measured limit.</summary>
    void SetLocked(bool on)
    {
        if (on && !locked)
        {
            string? refuse = live is not { Active: true } s ? "press Run first: Lock keeps a solved result"
                : s.State == LiveSolver.Mode.Failed ? "the solver stopped: there is no result to lock"
                : s.Stats == null ? "wait for the first numbers, then lock"
                : jobBusy ? "wait for the sweep or annual run to finish, then lock"
                : null;
            if (refuse != null)
            {
                LockToggle.IsChecked = false;
                StatusText.Text = refuse;
                return;
            }
        }
        if (on == locked) { LockToggle.IsChecked = on; return; }

        var solver = live!;
        if (on)
        {
            ExitCompare();                  // Compare resumes the solver on its way out: leave it first
            CancelPlacement();
            FocusManager?.Focus(null);      // a half-typed wind or temperature stays as it was
        }
        locked = on;
        if (on)
        {
            solver.SuspendAnalysis = true;
            solver.SetPaused(true);
            lockedAt = DateTime.UtcNow;
            Log.Info($"result locked at sim t {solver.SimSeconds:0.0} s · averaged {solver.AverageSeconds:0.0}/{solver.AverageTarget:0.0} s · " +
                     $"{solver.Trust} · particles {Fmt(particleCount)} · limit {(particleLimit > 0 ? Fmt(particleLimit) : "not measured")}");
            if (solver.Stats is { } held) _ = ShowLockedField(held, solver.Nested ? solver.FineStats : null);
            // The limit so far was measured with the solver sharing the GPU; take a fresh, solver-idle reading
            // first and raise the particles to it then (UpdateParticleLimit), rather than jumping on a stale one.
            limitEstimate = 0;
            lockJumpPending = true;
            StatusText.Text = "Result locked · viewing only. The solver is stopped; unlock to change anything.";
        }
        else
        {
            solver.SuspendAnalysis = false;
            solver.SetPaused(false);        // the session kept its state: this carries on, it does not restart
            Log.Info($"result unlocked after {(DateTime.UtcNow - lockedAt).TotalSeconds:0} s: solver resumes at sim t {solver.SimSeconds:0.0} s");
            if (manualParticles == 0 && particleAuto != null)
            {
                particleAuto.SetCeiling(ParticleAuto.VisualCeiling, jump: false);
                SetParticleCount(particleAuto.Count, "unlocked");
            }
            StatusText.Text = "unlocked · the solver carries on from where it stopped";
        }
        Viewport3D.Locked = on;
        ApplyLockUi();
        statsVersion = -1;
        RefreshPanel();
        ShowParticleSetting();
    }

    /// <summary>The averaged field goes to the particles once the solver has really paused, so a live publish
    /// still in flight cannot land on top of it.</summary>
    /// <summary>Locked particles fly through the averaged field, not the last instantaneous one. With a simulation
    /// box that is the same composite the live view shows (fine texels in the box, coarse outside), built from the
    /// two averaged fields of the last analysis pass on a mapping of its own: the solver's composite is not touched.</summary>
    async Task ShowLockedField(SimulationResult held, SimulationResult? fine)
    {
        for (int i = 0; i < 40 && live is { State: not LiveSolver.Mode.Paused }; i++) await Task.Delay(25);
        var volume = await Task.Run(() =>
        {
            if (fine == null) return FieldVolume.Build(held);
            var c = NestedComposite.Build(held.Field.Grid, fine.Field.Grid, held.Model);
            c.Fill(held.Field.Ux, held.Field.Uy, held.Field.Uz, fine.Field.Ux, fine.Field.Uy, fine.Field.Uz);
            return FieldVolume.Build(c.Grid, held.Model, c.Ux, c.Uy, c.Uz, null, null, true);
        });
        if (locked) Viewport3D.Renderer.Particles.SetField(volume);
        lockedFieldNote = $"averaged {(fine != null ? "coarse + fine (box) composite" : "field")} {volume.Nx}x{volume.Ny}x{volume.Nz} " +
                          $"@ {volume.Dx * 1000:0} mm, set {(locked ? "yes" : "no (unlocked first)")}";
        Log.Info("locked view: " + lockedFieldNote);
    }

    string lockedFieldNote = "";

    void ApplyLockUi()
    {
        ui ??= UiTable.Resolve(this);
        foreach (var (rule, control) in ui)
        {
            if (!rule.ChangesFlow) continue;
            control.IsEnabled = UiTable.Enabled(rule, locked);
            SetLockTip(control, locked);
        }
        LockToggle.IsChecked = locked;
        LockGlyph.Text = locked ? "" : "";
        LockText.Text = locked ? "Locked · viewing only" : "Lock";
        LockText.IsVisible = locked;                         // the icon alone until it is a state worth reading
        ToolTip.SetTip(LockToggle, locked
            ? "Viewing only: the solver is stopped and the GPU only draws. Camera, Speed/Still air, slice, room " +
              "highlight and Export PDF still work. Click to unlock and carry on solving from here."
            : "Lock this result: the solver stops so the GPU is free, the air keeps flowing through the averaged " +
              "field, and anything that would change the flow is greyed out. Click again to unlock.");
    }

    /// <summary>Greyed controls say why. Avalonia hides tooltips on disabled controls unless asked
    /// (ShowOnDisabled). Item rows are left alone: their tooltips are bindings.</summary>
    void SetLockTip(Control control, bool on)
    {
        if (on)
        {
            if (!lockTips.ContainsKey(control)) lockTips[control] = ToolTip.GetTip(control);
            ToolTip.SetTip(control, UiTable.LockedWhy);
            ToolTip.SetShowOnDisabled(control, true);
        }
        else if (lockTips.Remove(control, out var tip))
        {
            ToolTip.SetTip(control, tip);
        }
        if (control is ItemsControl) return;
        foreach (var child in control.GetLogicalChildren().OfType<Control>()) SetLockTip(child, on);
    }

    /// <summary>The status strip in Basic: what the solver is doing, in words, and nothing about lattices.</summary>
    string BasicStatusLine(LiveSolver solver)
    {
        if (solver.State == LiveSolver.Mode.Failed) return "the solver stopped: " + solver.Error;
        if (locked) return $"result locked · {solver.SimSeconds:0} s of air simulated · the GPU only draws";
        string state = solver.State switch
        {
            LiveSolver.Mode.Starting => "starting the solver",
            LiveSolver.Mode.Paused => "paused",
            _ => "solving",
        };
        if (solver.State == LiveSolver.Mode.Starting) return state + "…";
        string where = solver.Backend.Contains("CPU", StringComparison.OrdinalIgnoreCase) ? "on the CPU" : "on the GPU";
        if (solver.GpuNote.Length > 0) where += " · " + solver.GpuNote;   // P17-07
        if (solver.Accurate.Phase == AccuratePhase.Accurate)
            return $"Averaged result ready ({WindFlow.Report.ResultStatus.Text(solver.AccurateStats?.Convergence)}) · {solver.AccurateCells / 1e6:0.0}M cells · the GPU is idle until you change something";
        if (solver.Accurate.Running)
            return $"{(solver.State == LiveSolver.Mode.Paused ? "paused" : "working on the averaged result")} · " +
                   $"{solver.AccurateCells / 1e6:0.0}M cells {where} · the Preview numbers stay until it lands";
        double eta = solver.SecondsToConverged();
        string settle = solver.Trust.StartsWith("averaging", StringComparison.Ordinal)
            ? (double.IsNaN(eta) || eta <= 0 ? " · averaging" : $" · first average in ~{Math.Max(1, eta):0} s")   // S6-12: one flow-through window, no convergence test
            : " · averaged";
        return $"{state} · {solver.SimSeconds:0} s of air simulated{settle} · {where}";
    }

    static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(0x75, 0x83, 0x9A));

    // ---------------------------------------------------------------- headless shots for this shell

    /// <summary>--shotmode basic | basic-notes | expert | settings | locked: solve the sample house live, pick the
    /// Kitchen row, then set up the named state and photograph the whole window (the popover included).</summary>
    async Task ShellShotSequence(string path, string mode)
    {
        ApplyMode(mode is "expert" or "openings-add" or "device-presets" or "device-place" or "device-gizmo" or "slice-expert" or "picker-expert");
        Viewport3D.Renderer.InputEnabled = false;   // keep stray keys and wheels out of a headless run
        if (mode is "openings-prerun" or "picker-basic" or "picker-expert")
        {
            // Before any Run: the lists come from the model alone. picker-*: the panel's tabs (Basic three, Expert five).
            SelectTab(mode == "openings-prerun" ? TabOpenings : TabRooms);
            await Task.Delay(1200);
            shotNote = $"mode {mode} · before any run · rooms {(RoomList.ItemsSource as IEnumerable<object>)?.Count()} rows · openings " +
                       string.Join(" | ", (OpeningListView.ItemsSource as IEnumerable<OpeningRow>)?.Select(o => $"{o.Name} \"{o.Flow}\"") ?? []) +
                       $" · tabs {string.Join(", ", new[] { SectionRoomsTab, SectionOpeningsTab, SectionViewTab, SectionDevicesTab, SectionStudiesTab }.Where(t => t.IsEffectivelyVisible).Select(t => t.Content))}";
            await SaveShell(path);
            Close();
            return;
        }
        StartLive();
        await Task.Delay(6000);
        // Wait for a settled average (the trust line reads "Mass balance holds"), not a fixed time: a low GPU
        // budget in Settings makes the first average arrive later.
        for (int i = 0; i < 90 && (live?.Stats == null || live.Trust.StartsWith("averaging", StringComparison.Ordinal)); i++)
            await Task.Delay(500);
        int index = model.Rooms.FindIndex(r => r.Id == "kitchen");
        if (index >= 0) SelectRoom(index, frame: false);
        var lines = new List<string>();
        if (mode is "slice-basic" or "slice-expert")
        {
            // The plan cut at seated head height, coloured by speed, with its title and legend in the panel.
            SetSliceRanges();
            SliceHToggle.IsChecked = true;
            OnSliceH(null, null!);
            lines.Add($"slice     title \"{SliceTitle.Text}\" · legend {SliceLegendMin.Text}..{SliceLegendMax.Text} · hint \"{SliceHint.Text}\"");
        }
        if (Options.Isolate is { } wanted)
        {
            // --isolate <room>: view only. The room table before and after must read the same.
            string Table() => string.Join(" | ", live?.Stats?.Rooms.Select(r => $"{r.Room.Name} {r.Ach:0.00} {r.MeanSpeed:0.000}") ?? []);
            string before = Table();
            int iso = RoomIsolation.Find(model, wanted);
            if (iso >= 0) SetIsolated(iso);
            await Task.Delay(1500);
            lines.Add($"isolate   {wanted} -> {(iso >= 0 ? model.Rooms[iso].Name : "not found")} · box {Viewport3D.Renderer.Isolation?.Min} {Viewport3D.Renderer.Isolation?.Max} · " +
                      $"chip \"{IsolateChipText.Text}\" visible {IsolateChip.IsVisible} · solver {live?.State} · table unchanged {before == Table()}");
        }
        string Sample(string label)
        {
            var r = Viewport3D.Renderer;
            return $"{label,-9} fps {r.Fps:0.0} · frame {r.FrameMs:0.0} ms · cap {(r.MaxFps > 0 ? $"{r.MaxFps:0}" : $"vsync ({MonitorHz} Hz)")} · " +
                   $"particles {Fmt(r.Particles.Count)} · particle pass {r.ParticleGpuMs:0.000} ms GPU for {Fmt(r.TimedParticles)} · " +
                   $"limit {(particleLimit > 0 ? Fmt(particleLimit) : "-")} · solver {live?.State} sim t {live?.SimSeconds:0.0} s · " +
                   $"duty {live?.Duty * 100:0} %";
        }
        switch (mode)
        {
            case "basic-notes":
                notesOpen = true;
                RefreshNotesView();
                break;
            case "expert":
                SelectTab(TabView);
                break;
            case "basic-fresh":
            {
                // The air-age solve runs after the settle and takes seconds: wait for it to land in Stats.
                var clock = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 120 && live?.Stats?.Rooms.All(r => float.IsNaN(r.MeanAge)) != false; i++)
                    await Task.Delay(500);
                lines.Add($"air age   solve {live?.AgeMs:0} ms · landed {clock.Elapsed.TotalSeconds:0.0} s after the settle · " +
                          string.Join(", ", live?.Stats?.Rooms.Select(r => $"{r.Room.Name} {AgeText(r.MeanAge)} ({r.MeanAge:0} s, fresh-air ACH {Standards.FreshText(r)}, total {r.Ach:0.0})") ?? []));
                lines.Add(Sample("with age"));
                break;
            }
            case "settings":
                SettingsPopup.IsOpen = true;
                break;
            case "menu":
                ProjectMenuButton.Flyout?.ShowAt(ProjectMenuButton);
                break;
            case "wind":
                OnWindPill(null, null!);
                break;
            case "openings-add":
            case "openings-add-basic":
            {
                // The Openings section after a hand-added window: the same pick, add and re-solve the viewport click runs, from the south
                // wall of the Living room (x 5.4 m, 1.5 m up), then wait for the restarted solve to list it with a flow number.
                SelectTab(TabOpenings);
                if (OpeningEdit.PickWall(model, new System.Numerics.Vector3(5.4f, -10f, 1.5f), System.Numerics.Vector3.UnitY) is { } hit &&
                    OpeningEdit.Add(model, hit, out _) is { } added)
                {
                    SelectOpening(model.Openings.Count - 1);
                    revealAdded = true;
                    ApplyOpeningChange($"{added.Name} added");
                    string Flux() => live?.Stats?.Openings.FirstOrDefault(o => o.Opening.Id == added.Id) is { } f ? $"{f.FlowRate:+0.000;-0.000} m³/s" : "none";
                    for (int i = 0; i < 120 && live?.Stats?.Openings.Any(o => o.Opening.Id == added.Id) != true; i++)
                        await Task.Delay(500);
                    statsVersion = -1;
                    RefreshPanel();
                    var row = (OpeningListView.ItemsSource as IEnumerable<OpeningRow>)?.FirstOrDefault(o => o.Index == model.Openings.Count - 1);
                    lines.Add($"openings  added {added.Name} · {added.Width:0.##} x {added.Height:0.##} m, sill {OpeningEdit.Sill(model, added):0.##} m, depth {added.Depth:0.###} m · " +
                              $"listed by the solver {live?.Stats?.Openings.Any(o => o.Opening.Id == added.Id)} · flux {Flux()} · row \"{row?.Flow}\" · " +
                              $"inspector \"{OpeningInspTitle.Text}\" {InspWidth.Text} x {InspHeight.Text} sill {InspSill.Text} state \"{OpeningInspState.Text}\"");
                }
                break;
            }
            case "rooms-merge":
            {
                // Kitchen selected (above), Ctrl+click on the Living row: two rooms selected, the inspector offers the merge.
                ToggleMultiRoom(model.Rooms.FindIndex(r => r.Id == "living"));
                await Task.Delay(1200);
                lines.Add($"merge     selected {string.Join(" + ", MultiRooms().Select(r => r.Name))} · inspector \"{MergeTitle.Text}\" button \"{MergeButton.Content}\" enabled {MergeButton.IsEnabled} · hint \"{KeyHints.Text}\"");
                break;
            }
            case "rooms-add":
            {
                // Delete the Kitchen through the real path, wait for the solve to drop it, then click where it was: Add room, the detector, the new row.
                int k = model.Rooms.FindIndex(r => r.Id == "kitchen");
                DeleteRoom(k);
                for (int i = 0; i < 120 && live?.Stats?.Rooms.Any(r => r.Room.Id == "kitchen") != false; i++) await Task.Delay(500);
                lines.Add($"add room  kitchen deleted · rows {model.Rooms.Count} · in the solve {live?.Stats?.Rooms.Any(r => r.Room.Id == "kitchen")}");
                OnRoomAdd(null, null!);
                for (int i = 0; i < 240 && spaces == null; i++) await Task.Delay(500);      // the spaces are found once (MainWindowSpaces); the click resolves against them
                lines.Add($"add room  armed · button \"{RoomAddButton.Content}\" · status \"{StatusText.Text}\"");
                if (Viewport3D.Renderer.Project(new System.Numerics.Vector3(9.5f, 2.3f, 1.1f), out var at))
                {
                    PlaceRoom((int)at.X, (int)at.Y);
                    for (int i = 0; i < 240 && !model.Rooms.Any(r => r.Id.StartsWith(RoomEdit.UserPrefix, StringComparison.Ordinal)); i++) await Task.Delay(500);
                    lines.Add($"add room  click at ({at.X:0}, {at.Y:0}) · status \"{StatusText.Text}\"");
                }
                var added = model.Rooms.FirstOrDefault(r => r.Id.StartsWith(RoomEdit.UserPrefix, StringComparison.Ordinal));
                for (int i = 0; added != null && i < 120 && live?.Stats?.Rooms.Any(r => r.Room.Id == added.Id) != true; i++) await Task.Delay(500);
                lines.Add($"add room  {added?.Name ?? "NONE"} · {(added == null ? 0 : RoomNames.FloorArea(added)):0.0} m² (drawn Kitchen 20.2) · volume {added?.DetectedVolume:0.0} m³ · listed by the solver {(added == null ? null : live?.Stats?.Rooms.Any(r => r.Room.Id == added.Id))} · undo visible {StatusUndoButton.IsVisible}");
                break;
            }
            case "rooms-candidates":
            {
                // The Rooms section after the Kitchen is deleted: "Spaces without a room" lists its space; the row is picked (violet in 3D, camera on it).
                SelectTab(TabRooms);
                DeleteRoom(model.Rooms.FindIndex(r => r.Id == "kitchen"));
                for (int i = 0; i < 120 && live?.Stats?.Rooms.Any(r => r.Room.Id == "kitchen") != false; i++) await Task.Delay(500);
                for (int i = 0; i < 240 && spaces == null; i++) await Task.Delay(500);
                int at = spaces?.FindIndex(c => Devices.InLoops(c, 9.5f, 2.3f)) ?? -1;
                if (at >= 0) PickSpace(at);
                await Task.Delay(1500);
                lines.Add($"spaces    {spaces?.Count} listed · header \"{SpacesHeader.Text}\" · picked {at} {(at >= 0 ? SpaceTitle(spaces![at]) : "")} · box visible {SpacesBox.IsVisible} · status \"{StatusText.Text}\"");
                break;
            }
            case "openings-restore":
            {
                // Add opening armed after the Living south window was deleted: its dashed outline in 3D and the "Deleted: … Restore" row under the list.
                SelectTab(TabOpenings);
                DeleteOpening(model.Openings.FindIndex(o => o.Id == "w-liv-s"));
                for (int i = 0; i < 120 && live?.Stats?.Openings.Any(o => o.Opening.Id == "w-liv-s") != false; i++) await Task.Delay(500);
                OnOpeningAdd(null, null!);
                lock (Viewport3D.Renderer.Camera.Sync)
                {
                    var cam = Viewport3D.Renderer.Camera;
                    cam.Target = new System.Numerics.Vector3(3.5f, 0f, 1.6f);
                    cam.Distance = 7f;
                    cam.Yaw = -MathF.PI / 2 + 0.35f;
                    cam.Pitch = 0.2f;
                }
                await Task.Delay(1500);
                lines.Add($"restore   armed · deleted rows {DeletedBox.IsVisible} {string.Join(" | ", (DeletedList.ItemsSource as IEnumerable<DeletedRow>)?.Select(d => d.Label) ?? [])} · status \"{StatusText.Text}\"");
                break;
            }
            case "device-presets":
            {
                // The palette, the sample fan selected with its numbers folded, and a split AC beside it.
                SelectTab(TabDevices);
                var bed1 = model.Rooms.FirstOrDefault(r => r.Id == "bed1");
                if (bed1 != null && !model.Sources.Any(s => s.Device == DeviceKind.SplitAc))
                {
                    var ac = Devices.Create(Devices.PresetFor(DeviceKind.SplitAc),
                        new System.Numerics.Vector3(0.45f, 4f, bed1.ZMin + Devices.PresetFor(DeviceKind.SplitAc).MountHeight),
                        System.Numerics.Vector3.UnitX, bed1.Level, 1);
                    model.Sources.Add(ac);
                    ApplyDeviceChange($"{ac.Name} added in {bed1.Name}");
                }
                SelectDevice(model.Sources.FindIndex(s => s.Device == DeviceKind.CeilingFan));
                detailsOpen = true;                 // the inspector's numbers (flow, swirl, supply, turbulence) open for the shot
                await Task.Delay(1500);
                RefreshDevices();
                lines.Add("devices   " + string.Join(" / ", model.Sources.Select(s =>
                    $"{s.Name}: {Devices.Resolution(s, CellSizeAt(s.Center)).Label} ({Devices.CellsAcross(s, CellSizeAt(s.Center)):0.0} cells at {CellSizeAt(s.Center) * 1000:0} mm)")));
                break;
            }
            case "device-gizmo":
            {
                // A pedestal fan on Bedroom 1's floor, selected: its move square, both floor arrows and the yaw ring.
                SelectTab(TabDevices);
                var bed1 = model.Rooms.FirstOrDefault(r => r.Id == "bed1");
                if (bed1 != null && bed1.Loops.Count > 0)
                {
                    var loop = bed1.Loops[0];
                    var preset = Devices.PresetFor(DeviceKind.PedestalFan);
                    var centre = new System.Numerics.Vector3(loop.Average(p => p.X), loop.Average(p => p.Y), bed1.ZMin + preset.MountHeight);
                    var fan = Devices.Create(preset, centre, System.Numerics.Vector3.UnitZ, bed1.Level, Devices.NextOrdinal(model, preset.Kind));
                    model.Sources.Add(fan);
                    ApplyDeviceChange($"{fan.Name} added in {bed1.Name}");
                    SelectDevice(model.Sources.Count - 1);
                }
                await Task.Delay(1500);
                var handles = WindFlow.App.Viewport.Gizmos.Build(Viewport3D.Renderer);
                lines.Add($"gizmo     selected {selectedDevice} · handles {string.Join(",", handles.Select(h => $"{h.Id}:{h.Shape}"))}");
                break;
            }
            case "device-place":
            {
                // Arm the ceiling fan tile and put the cursor over Bedroom 2: the ghost sits on its ceiling.
                SelectTab(TabDevices);
                var preset = Devices.PresetFor(DeviceKind.CeilingFan);
                BeginPlacement(preset);
                var bed2 = model.Rooms.FirstOrDefault(r => r.Id == "bed2");
                if (bed2 != null && bed2.Loops.Count > 0)
                {
                    var loop = bed2.Loops[0];
                    var c = new System.Numerics.Vector2(loop.Average(p => p.X), loop.Average(p => p.Y));
                    if (Viewport3D.Renderer.Project(new System.Numerics.Vector3(c.X, c.Y, bed2.ZMax - Devices.CeilingFanDrop), out var at))
                        OnDeviceHovered((int)at.X, (int)at.Y);
                }
                await Task.Delay(800);
                lines.Add($"placing   {preset.Label} · ghost {(placeGhost != null ? $"at {placeGhost.Center}" : "NONE")} · hint \"{DevicesHint.Text}\"");
                break;
            }
            case "pause":
            {
                for (int i = 0; i < 120 && live!.Publishes == 0; i++) await Task.Delay(500);
                for (int i = 0; i < 20; i++) await Task.Delay(500);     // let the particles fill in
                string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path));
                async Task<byte[]?> Grab(string tag)
                {
                    shotNote = $"mode pause · {tag} · solver {live?.State} frozen {Viewport3D.Renderer.Particles.Frozen}";
                    var rr = Viewport3D.Renderer;
                    rr.RequestShot();
                    byte[]? px = null;
                    for (int i = 0; i < 150 && px == null; i++) { await Task.Delay(20); px = rr.TakeShot(out _); }
                    await SaveShell(stem + "-" + tag + ".png");
                    return px;
                }
                live!.SetPaused(true);
                for (int i = 0; i < 100 && live.State != LiveSolver.Mode.Paused; i++) await Task.Delay(50);
                await Task.Delay(1500);                                  // the status tick sets the freeze, the last advected frame lands
                var a = await Grab("paused-1");
                await Task.Delay(1000);
                var b = await Grab("paused-2");
                live.SetPaused(false);
                await Task.Delay(1500);
                // Viewport readback only (no UI text); a pixel counts as changed when a channel moves by more than 16.
                static int Changed(byte[]? x, byte[]? y)
                {
                    if (x == null || y == null) return -1;
                    int n = 0;
                    for (int i = 0; i + 3 < Math.Min(x.Length, y.Length); i += 4)
                        if (Math.Abs(x[i] - y[i]) > 16 || Math.Abs(x[i + 1] - y[i + 1]) > 16 || Math.Abs(x[i + 2] - y[i + 2]) > 16) n++;
                    return n;
                }
                var c = await Grab("resumed");
                lines.Add($"pause     frozen {Viewport3D.Renderer.Particles.Frozen} · frames 1 vs 2 (1 s apart, paused): {(Changed(a, b) == 0 ? "identical while paused" : "DIFFERENT while paused")} ({Changed(a, b)} px changed)");
                lines.Add($"resume    paused frame vs after resume: {(Changed(b, c) == 0 ? "identical after resume (FAIL)" : "different")} ({Changed(b, c)} px changed) · solver {live.State} · publishes {live.Publishes}");
                File.AppendAllLines(Path.ChangeExtension(stem + "-resumed.png", ".txt"), lines);
                Close();
                return;
            }
            case "locked":
            {
                lines.Add(Sample("unlocked"));
                double simBefore = live?.SimSeconds ?? 0;
                SetLocked(true);
                await Task.Delay(9000);
                lines.Add(Sample("locked"));
                lines.Add($"locked    wind box enabled {WindGroup.IsEffectivelyEnabled} · run enabled {RunButton.IsEffectivelyEnabled} · " +
                          $"PDF enabled {ReportButton.IsEffectivelyEnabled} · still-air enabled {StillModeToggle.IsEffectivelyEnabled} · " +
                          $"sim t held {simBefore:0.0} -> {live?.SimSeconds:0.0} s");
                shotNote = string.Join(" | ", lines);
                await SaveShell(path);
                SetLocked(false);
                await Task.Delay(6000);
                lines.Add(Sample("unlocked"));
                lines.Add($"resume    sim t {live?.SimSeconds:0.0} s after unlock (was {simBefore:0.0} s at lock: carried on, not restarted)");
                File.AppendAllLines(Path.ChangeExtension(path, ".txt"), lines);
                Close();
                return;
            }
        }
        await Task.Delay(1800);
        statsVersion = -1;
        RefreshPanel();
        await Task.Delay(400);
        if (mode is "slice-basic" or "slice-expert") { SliceSection.BringIntoView(); await Task.Delay(300); }
        shotNote = $"mode {mode} · {Sample("now")} · notes {technicalNotes.Count}: " +
                   string.Join(" / ", (advancedMode ? technicalNotes : technicalNotes.Select(Notes.Plain).Distinct()).Take(6));
        await SaveShell(path);
        if (lines.Count > 0) File.AppendAllLines(Path.ChangeExtension(path, ".txt"), lines);
        Close();
    }

    /// <summary>--shotmode walk (with --empty): the Basic flow end to end through the same handlers the buttons
    /// call. Sample, Run, change the wind, close a window with its row switch, rename a room, Still air, Export PDF
    /// (the dialog and the file picker are skipped: WriteReport is what runs after them). Each step goes to the
    /// sidecar with what the window said back.</summary>
    async Task WalkSequence(string path)
    {
        var steps = new List<string>();
        void Step(string what) => steps.Add($"{what,-24} -> {StatusText.Text}");
        Viewport3D.Renderer.InputEnabled = false;   // the user's mouse over the window must not drag the wind arrow
        ApplyMode(false);
        await Task.Delay(800);
        steps.Add($"start                    empty state {EmptyState.IsVisible}, panel {ResultsPanel.IsVisible}");
        OnSampleBuilding(null, null!);
        await Task.Delay(800);
        Step("Sample");
        OnRun(null, null!);
        await Task.Delay(6000);
        for (int i = 0; i < 90 && (live?.Stats == null || live.Trust.StartsWith("averaging", StringComparison.Ordinal)); i++)
            await Task.Delay(500);
        Step($"Run ({RunButton.Content})");
        steps.Add($"  trust                  {TrustHead.Text} · {technicalNotes.Count} notes");
        WindDirBox.Text = "200";
        WindSpeedBox.Text = "5";
        await Task.Delay(8000);
        Step($"wind {WindDirBox.Text}° {WindSpeedBox.Text} m/s");
        steps.Add($"  trust                  {TrustHead.Text}");
        var window = model.Openings.FirstOrDefault(o => o.Kind == OpeningKind.Window && o.IsOpen);
        if (window != null) ToggleOpening(window);           // what the row switch calls
        Step($"switch {window?.Name}");
        await Task.Delay(8000);
        steps.Add($"  after                  {TrustHead.Text} · {window?.Name} open {window?.IsOpen}");
        int kitchen = model.Rooms.FindIndex(r => r.Id == "kitchen");
        if (kitchen >= 0)
        {
            SelectRoom(kitchen, frame: false);
            BeginRename(kitchen);
            await Task.Delay(500);
            CommitRename("Kitchen / dining");
            await Task.Delay(500);
        }
        Step("rename Kitchen");
        OnStillMode(null, null!);
        await Task.Delay(3000);
        Step("Still air");
        var pdf = Path.ChangeExtension(Path.GetFullPath(path), ".pdf");
        if (ReportSource() is { } source)
        {
            var writing = WriteReport(source, pdf, new WindFlow.Report.ReportOptions
            {
                ProjectName = model.Name,
                Author = "WindFlow walk",
                Scenario = ScenarioLine(source),
            });
            // UI responsiveness while the report draws: the longest gap between 16 ms UI-thread wake-ups, and
            // every distinct progress line the status strip showed.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double worstGap = 0, last = 0;
            var seen = new List<string>();
            while (!writing.IsCompleted)
            {
                await Task.Delay(16);
                double now = clock.Elapsed.TotalMilliseconds;
                worstGap = Math.Max(worstGap, now - last);
                last = now;
                if (StatusText.Text is { } s && s.StartsWith("report", StringComparison.Ordinal) && !seen.Contains(s)) seen.Add(s);
            }
            await writing;
            steps.Add($"  ui                     worst UI-thread gap {worstGap:0} ms over {clock.Elapsed.TotalSeconds:0.0} s");
            foreach (var s in seen) steps.Add($"  progress               {s}");
        }
        Step("Export PDF");
        steps.Add($"  pdf                    {pdf} · {(File.Exists(pdf) ? new FileInfo(pdf).Length : 0):N0} bytes");
        shotNote = "walk";
        await SaveShell(path);
        File.AppendAllLines(Path.ChangeExtension(path, ".txt"), steps);
        Close();
    }

    /// <summary>SaveShell: the popover is its own top-level window, so the window render misses it. Render its root
    /// and blend it in at its screen offset from the window.</summary>
    void PastePopup(byte[] rgba, int w, int h, double scale)
    {
        if (SettingsPopup.IsOpen) PastePopup(rgba, w, h, scale, SettingsCard);
        if (TourPopup.IsOpen && TourPopup.Child is { } tourLayer) PastePopup(rgba, w, h, scale, tourLayer);   // the guided tour (Tour.cs)
        if (WindPopup.IsOpen) PastePopup(rgba, w, h, scale, WindCard);                                    // the wind pop-over
        if (ProjectMenuButton.Flyout is { IsOpen: true }) PastePopup(rgba, w, h, scale, ProjectMenuCard);   // the project menu
        if (HelpButton.Flyout is { IsOpen: true }) PastePopup(rgba, w, h, scale, HelpMenuCard);
    }

    void PastePopup(byte[] rgba, int w, int h, double scale, Control content)
    {
        if (TopLevel.GetTopLevel(content) is not { } popupRoot || popupRoot == this) return;
        var at = popupRoot.PointToScreen(new Point(0, 0));
        var origin = this.PointToScreen(new Point(0, 0));
        int pw = Math.Max(1, (int)(popupRoot.Bounds.Width * scale)), ph = Math.Max(1, (int)(popupRoot.Bounds.Height * scale));
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(pw, ph), new Vector(96 * scale, 96 * scale));
        bitmap.Render(popupRoot);
        var bgra = new byte[pw * ph * 4];
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
        bitmap.CopyPixels(new PixelRect(0, 0, pw, ph), pin.AddrOfPinnedObject(), bgra.Length, pw * 4);
        pin.Free();
        int x0 = at.X - origin.X, y0 = at.Y - origin.Y;
        for (int y = 0; y < ph; y++)
        for (int x = 0; x < pw; x++)
        {
            int dx = x0 + x, dy = y0 + y;
            if (dx < 0 || dy < 0 || dx >= w || dy >= h) continue;
            int s = (y * pw + x) * 4, d = (dy * w + dx) * 4;
            int a = bgra[s + 3];
            if (a == 0) continue;
            // Premultiplied BGRA over opaque RGBA.
            rgba[d] = (byte)Math.Min(255, bgra[s + 2] + rgba[d] * (255 - a) / 255);
            rgba[d + 1] = (byte)Math.Min(255, bgra[s + 1] + rgba[d + 1] * (255 - a) / 255);
            rgba[d + 2] = (byte)Math.Min(255, bgra[s] + rgba[d + 2] * (255 - a) / 255);
        }
    }
}

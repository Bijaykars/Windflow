using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WindFlow.App.Viewport;
using WindFlow.Core;
using WindFlow.Report;

namespace WindFlow.App;

public partial class MainWindow : Window
{
    readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly DispatcherTimer liveTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly System.Diagnostics.Stopwatch liveClock = new();

    LiveSolver? live;
    bool advancedMode, thermalShown;
    /// <summary>Which question the room chips answer. Air quality and cooling differ by an order of
    /// magnitude in ACH, so one set of thresholds cannot serve both; Standards.cs carries the citations.</summary>
    AchIntent achIntent = AchIntent.AirQuality;
    BuildingModel model = new();
    /// <summary>S4-02: the model as last shown or saved (<see cref="UserFiles.Fingerprint"/>); the model differs from it = unsaved edits.</summary>
    string? cleanFingerprint;
    bool closeConfirmed;
    /// <summary>A bench, shot, report or turntable run: nobody is there to answer a prompt.</summary>
    static bool Automated => Options.BenchSeconds > 0 || !string.IsNullOrEmpty(Options.Shot) || Options.TurntablePath != null ||
                             !string.IsNullOrEmpty(Options.ReportPath);
    bool ModelDirty => cleanFingerprint != null && UserFiles.Fingerprint(model) != cleanFingerprint;
    SolverRun? run;
    SimulationResult? result;
    FieldVolume? volume;
    string solverLine = "";
    // The startup tier sets this (Tier.cs) and ParticleAuto moves it at runtime; --particles pins it.
    int particleCount = Options.Particles > 0 ? Options.Particles : 60_000;
    ParticleAuto? particleAuto;
    bool stale, countAdjusted, benchWritten, benchStarted, liveToggled, sliceRangesPending = true, boxResetPending = true;
    DateTime liveToggleAt = DateTime.MaxValue;
    string liveToggleName = "";
    DateTime benchDeadline = DateTime.MaxValue;
    // Trust badge samples for the bench file: the badge must read "averaging…" while the window since the
    // last reset is shorter than one flow-through, so it is recorded early as well as at the end.
    DateTime benchStartedAt = DateTime.MaxValue;
    string badgeEarly = "", badgeAfterReset = "";
    int badgeStatsVersion = -1;
    Vector2 windVector;
    string lastWindText = "";

    public MainWindow()
    {
        InitializeComponent();
        Viewport3D.GhostChanged += ghost => GhostToggle.IsChecked = ghost;
        Viewport3D.ParticlesChanged += on => ParticleToggle.IsChecked = on;
        Viewport3D.ViewChanged += ApplyView;
        Viewport3D.OpeningToggled += ToggleOpening;
        Viewport3D.RoomPicked += OnRoomPicked;
        Viewport3D.Escaped += () => { if (placing != null || addingOpening || addingRoom) CancelPlacement(); else ClearSelection(); };
        Viewport3D.DeleteRequested += DeleteSelection;
        Viewport3D.ProbePlaced += AddProbe;
        Viewport3D.SliceToggled += () => { SliceHToggle.IsChecked = !(SliceHToggle.IsChecked == true); OnSliceH(null, null!); };
        Viewport3D.ProbeModeChanged += on => ProbeToggle.IsChecked = on;
        Viewport3D.SliceDragged += SyncSliceSliders;
        Viewport3D.WindDragged += SetWindFromDrag;
        Viewport3D.WallModeCycled += CycleWalls;
        SliceLegend.Background = Palette.SpeedBrush();
        live = new LiveSolver(Viewport3D.Renderer.Particles);
        statusTimer.Tick += OnStatusTick;
        liveTimer.Tick += OnLiveTick;
        Opened += OnOpened;
        // Closing while solving must not hang: Stop() cancels the token, joins the solver thread and
        // disposes the session (which frees the GPU buffers) before the window tears the renderer down.
        Closing += (_, e) =>
        {
            if (!closeConfirmed && !Automated && ModelDirty)       // S4-02: ask first; nothing below runs until the answer is in
            {
                e.Cancel = true;
                _ = ConfirmThenClose();
                return;
            }
            statusTimer.Stop();
            liveTimer.Stop();
            exportTimer?.Stop();
            ultraUiCts.Cancel();   // a saved-run check in flight stops with the window
            run?.Cancel();
            jobCancel?.Cancel();
            recorder.Dispose();
            liveLink?.Dispose();
            live?.Dispose();
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;
            settings.Save();
        };
        if (settings.WindowWidth > 800 && settings.WindowHeight > 500)
        {
            Width = settings.WindowWidth;
            Height = settings.WindowHeight;
        }
        KeyDown += OnKeyDown;
        SettingsPopup.Closed += (_, _) => settingsClosedAt = DateTime.UtcNow;
        var appVersion = typeof(MainWindow).Assembly.GetName().Version;
        AboutVersionText.Text = $"WindFlow {(appVersion != null ? $"{appVersion.Major}.{appVersion.Minor}.{appVersion.Build}" : "")}".TrimEnd();
        AboutDisclaimerText.Text = WindFlow.Report.Disclaimer.Text + "\n\n" + WindFlow.Report.Disclaimer.ViscosityFloor + "\n\n" + WindFlow.Report.Disclaimer.KnownLimits;      // S5-05: one wording for the app, the README and the PDF footer; P11-01: the viscosity floor
        StartupTier.Begin().ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) Dispatcher.UIThread.Post(() => ApplyTier(t.Result));
        });
        InitDevices();
        ApplyView(ParticleView.Speed);
    }

    /// <summary>S4-03: the saved size is restored as-is on any monitor, and the old 1460 DIP minimum was wider than the 1280 DIP work area of a
    /// 1080p laptop at 150 %, which put the results column off screen. Pull the size, the minimum and the position inside the work area.</summary>
    void FitToScreen()
    {
        if (Automated || (Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen) return;
        double s = screen.Scaling > 0 ? screen.Scaling : 1, w = Width, h = Height;
        var wa = screen.WorkingArea;
        // 16 / 40 DIP: the window frame and title bar around the client size
        var fit = WindowFit.Fit(w, h, Position.X / s, Position.Y / s, wa.X / s, wa.Y / s, wa.Width / s - 16, wa.Height / s - 40);
        MinWidth = fit.MinW; MinHeight = fit.MinH; Width = fit.W; Height = fit.H;
        Position = new Avalonia.PixelPoint((int)(fit.X * s), (int)(fit.Y * s));
        if (Math.Abs(fit.W - w) > 1 || Math.Abs(fit.H - h) > 1) Log.Info($"window fitted to the work area: {w:0}x{h:0} -> {fit.W:0}x{fit.H:0} DIP at scale {s:0.##}");
    }

    async void OnOpened(object? sender, EventArgs e)
    {
        FitToScreen();
        if (TryGetPlatformHandle() is { } handle) Win32.UseDarkTitleBar(handle.Handle);
        ApplyMode(settings.Advanced);
        ReopenLastToggle.IsChecked = settings.ReopenLast;
        InitGovernor();
        InitParticleSetting();
        InitAccurateSetting();
        InitAccurateControls();
        if (settings.LiveLink || Options.LiveLink) StartLiveLink();
        BoxV1Toggle.IsChecked = Options.BoxV1;       // default off: the box nests in the whole-building run
        if (!string.IsNullOrEmpty(Options.ModelPath)) Load(Options.ModelPath);
        else if (Options.Empty) ShowEmpty();
        else if (settings.ReopenLast && settings.LastModel.Length > 0 && File.Exists(settings.LastModel) &&
                 Options.BenchSeconds <= 0 && string.IsNullOrEmpty(Options.Shot) &&
                 string.IsNullOrEmpty(Options.ReportPath) && Options.TurntablePath == null)
            Load(settings.LastModel);
        else if (Options.BenchSeconds > 0 || !string.IsNullOrEmpty(Options.Shot) ||
                 Options.TurntablePath != null || !string.IsNullOrEmpty(Options.ReportPath))
            ShowModel(SampleBuilding.Build());
        else ShowEmpty();
        // First launch: the tour, once the first layout has placed its targets. Never in a headless run.
        if (!settings.TourSeen && !Automated) Dispatcher.UIThread.Post(() => StartTour(), DispatcherPriority.Background);

        statusTimer.Start();
        Viewport3D.GroundZ = model.GroundZ;
        if (Options.BenchSeconds > 0 || !string.IsNullOrEmpty(Options.Shot) || Options.TurntablePath != null ||
            !string.IsNullOrEmpty(Options.ReportPath))
            Log.Info($"headless sequence: bench {Options.BenchSeconds} s live {Options.Live} · shot {Options.Shot ?? "-"} " +
                     $"mode {(Options.ShotMode.Length > 0 ? Options.ShotMode : "-")} · view {Options.View} · devices-demo {Options.DevicesDemo} · " +
                     $"edit-device {(Options.EditDevice.Length > 0 ? Options.EditDevice + " " + Options.EditAssign : "-")} · " +
                     $"turntable {Options.TurntablePath ?? "-"} · report {Options.ReportPath ?? "-"}");
        if (Options.TurntableSeconds > 0 && Options.TurntablePath != null)
        {
            await TurntableSequence(Options.TurntableSeconds, Options.TurntablePath);
            return;
        }
        if (!string.IsNullOrEmpty(Options.ReportPath))
        {
            await ReportSequence(Options.ReportPath);
            return;
        }
        if (!string.IsNullOrEmpty(Options.Shot))
        {
            if (Options.ShotMode == "walk") { await WalkSequence(Options.Shot); return; }
            if (Options.Empty) { await Task.Delay(1200); await SaveShell(Options.Shot); Close(); return; }
            if (Options.ShotMode == "advanced") { await AdvancedShotSequence(Options.Shot); return; }
            if (Options.ShotMode == "thermal") { await ThermalShotSequence(Options.Shot); return; }
            if (Options.ShotMode == "boxsolve") { await BoxSolveShotSequence(Options.Shot); return; }
            if (Options.ShotMode is "import" or "importlive") { await ImportShotSequence(Options.Shot); return; }
            if (Options.ShotMode == "mesh-openings") { await MeshOpeningsShotSequence(Options.Shot); return; }
            if (Options.ShotMode is "ultra-dialog" or "ultra-resume") { await UltraShotSequence(Options.Shot, Options.ShotMode == "ultra-resume"); return; }
            if (Options.ShotMode == "accurate") { await AccurateShotSequence(Options.Shot); return; }
            if (Options.ShotMode == "accurate-time") { await AccurateTimeSequence(Options.Shot); return; }
            if (Options.ShotMode == "device-boxed") { await DeviceBoxedShotSequence(Options.Shot); return; }
            if (Options.DevicesDemo) { await DevicesDemoSequence(Options.Shot); return; }
            if (Options.ShotMode == "compare") { await CompareShotSequence(Options.Shot); return; }
            if (Options.ShotMode == "tour") { await TourShotSequence(Options.Shot); return; }
            if (Options.ShotMode is "basic" or "basic-notes" or "basic-fresh" or "device-presets" or "device-place" or "device-gizmo" or "expert" or "settings" or "locked" or "openings-add"
                or "slice-basic" or "slice-expert" or "openings-add-basic" or "openings-prerun" or "picker-basic" or "picker-expert" or "rooms-merge" or "rooms-add" or "rooms-candidates" or "openings-restore")
            {
                await ShellShotSequence(Options.Shot, Options.ShotMode);
                return;
            }
            if (Options.ShotMode == "rooms") { await RoomsShotSequence(Options.Shot); return; }
            await ShotSequence(Options.Shot);
            return;
        }
        if (Options.BenchSeconds <= 0) return;

        // Bench: solve first, then orbit with particles for the requested seconds.
        if (Options.View == "still") ApplyView(ParticleView.Still);
        benchDeadline = DateTime.UtcNow.AddSeconds(Options.BenchSeconds + 240);   // the solver dominates this

        if (Options.Live)
        {
            if (Options.DevicesDemo) EnableDemoDevices();
            ApplyOptionBox();                  // --box: the bench solves inside the box from the first step
            // Live bench: start the stepping solver, let the flow develop, then close a windward window
            // through the same command path a click uses and watch the jet disappear.
            StartLive();
            if (Options.ShotMode == "box")
            {
                BoxToggle.IsChecked = true;
                OnBoxToggle(null, null!);
                ClipToggle.IsChecked = true;
                OnClipToggle(null, null!);
            }
            benchDeadline = DateTime.UtcNow.AddSeconds(Options.BenchSeconds + 60);
            benchStarted = true;
            benchStartedAt = DateTime.UtcNow;
            benchCpuStart = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            liveToggleAt = DateTime.UtcNow.AddSeconds(Options.BenchSeconds * 0.5);
            Viewport3D.Renderer.BeginBench(Options.BenchSeconds,
                Options.BenchSeconds * 0.5 - 0.35, Options.BenchSeconds - 0.15);
            return;
        }

        await Solve();
        if (Options.FakeLive) StartFakeLive();
        benchDeadline = DateTime.UtcNow.AddSeconds(Options.BenchSeconds + 30);
        benchStarted = true;
        Viewport3D.Renderer.BeginBench(Options.BenchSeconds, Math.Min(5.5, Options.BenchSeconds * 0.9));
    }

    // ---------------------------------------------------------------- model

    /// <summary>Nothing loaded yet: hide the native viewport (it cannot be drawn over) and offer a way in.</summary>
    void ShowEmpty()
    {
        model = new BuildingModel();
        cleanFingerprint = UserFiles.Fingerprint(model);
        SaveModelButton.IsEnabled = false;
        EmptyState.IsVisible = true;
        Viewport3D.IsVisible = false;
        ResultsPanel.IsVisible = false;
        StatusText.Text = "";
        ModelText.Text = "-";
    }

    void ShowModel(BuildingModel building, bool frameCamera = true, bool liveResend = false)
    {
        if (locked) SetLocked(false);      // a new model is a new result; nothing locked survives it
        // A mesh import sets these again right after calling this; anything else is a real model with openings.
        importedGeometry = false;
        ShowImportNote(null);
        EmptyState.IsVisible = false;
        Viewport3D.IsVisible = true;
        ResultsPanel.IsVisible = PanelToggle.IsChecked == true;
        // A live-link swap of the same document keeps the picked room or opening, found again by id.
        var renderer0 = Viewport3D.Renderer;
        string? keepRoom = (uint)renderer0.SelectedRoom < (uint)model.Rooms.Count ? model.Rooms[renderer0.SelectedRoom].Id : null;
        string? keepOpening = (uint)renderer0.SelectedOpening < (uint)model.Openings.Count ? model.Openings[renderer0.SelectedOpening].Id : null;
        bool sameDoc = building.Name == model.Name;
        model = building;
        renderer0.SelectedRoom = sameDoc && keepRoom != null ? building.Rooms.FindIndex(r => r.Id == keepRoom) : -1;
        renderer0.SelectedOpening = sameDoc && keepOpening != null ? building.Openings.FindIndex(o => o.Id == keepOpening) : -1;
        PrepareRoomNames(building, liveResend);
        Log.Info($"model shown: {building.Name} · {building.Rooms.Count} rooms · {building.Openings.Count} openings · " +
                 $"{building.Sources.Count} devices · {building.Obstacles.Count} obstacles");
        result = null;
        volume = null;
        solverLine = "";
        stale = false;
        probes.Clear();
        Viewport3D.Renderer.SetScene(Scene.Build(building, ReadWind()), frameCamera);
        Viewport3D.GroundZ = building.GroundZ;
        sliceRangesPending = true;
        boxResetPending = true;
        IndoorBox.Text = building.Env.IndoorTemperature.ToString("0.#");
        OutdoorBox.Text = building.Env.OutdoorTemperature.ToString("0.#");
        Viewport3D.Renderer.Status = "";
        StatusText.Text = "press Run to solve";

        // Nothing from the previous model survives: its holds, compare, box, sweep ticks and device selection
        // all refer to objects and ids that no longer exist. A running solve follows the new model.
        ExitCompare();
        holdA = holdB = null;
        RefreshCompareState();
        appliedDomain = null;
        sweepPicks.Clear();
        sweepEntries = null;
        sweepIds = [];
        SweepList.ItemsSource = null;
        UpdateSweepHint();
        CancelPlacement();
        ClearUndo();
        SelectDevice(-1);
        statsVersion = -1;
        expandedRooms.Clear();
        autoExpanded = null;
        RefreshPanel();                      // the rooms and openings are listed from the model before any Run
        seenNotes.Clear();
        if (live is { Active: true })
        {
            Log.Info("model changed while solving: restarting the live solver on the new model");
            StartLive();                  // Start drops the solver's holds and any pending one
        }
        else if (live is { State: LiveSolver.Mode.Failed } failed) failed.Stop();
        cleanFingerprint = UserFiles.Fingerprint(model);       // S4-02: what is on screen now is the saved state
        SaveModelButton.IsEnabled = true;
    }

    void Load(string path)
    {
        try
        {
            Log.Info($"model load: {path}");
            var loaded = UserFiles.OpenModel(path);
            if (string.IsNullOrWhiteSpace(loaded.Name)) loaded.Name = Path.GetFileNameWithoutExtension(path);
            ShowModel(loaded);
            settings.LastModel = path;
            settings.Save();
        }
        catch (Exception ex)
        {
            Log.Error($"model load failed: {path}", ex);
            StatusText.Text = $"could not open {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    async void OnOpenModel(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscard("opening another model")) return;     // S4-02
        var meshPatterns = Import.MeshImport.Extensions.Select(e => "*" + e).ToArray();
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a model",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Model or geometry") { Patterns = [.. meshPatterns, "*.json"] },
                new FilePickerFileType("WindFlow model") { Patterns = ["*.json"] },
                new FilePickerFileType("Geometry (OBJ, glTF, FBX, STL, 3DS, DAE)") { Patterns = meshPatterns },
            ],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        if (Import.MeshImport.CanImport(path)) await ImportGeometry(path);
        else Load(path);
    }

    async void OnSampleBuilding(object? sender, RoutedEventArgs e)
    {
        if (await ConfirmDiscard("loading the sample")) ShowModel(SampleBuilding.Build());     // S4-02
    }

    /// <summary>S4-02: false when the user cancelled. Save / Discard / Cancel when the model has unsaved edits; a headless run never asks.</summary>
    async Task<bool> ConfirmDiscard(string action)
    {
        if (Automated || !ModelDirty) return true;
        var choice = await new UnsavedDialog(model.Name, action).ShowDialog<string?>(this);
        return choice == "discard" || (choice == "save" && await SaveModelAs());
    }

    async Task ConfirmThenClose()
    {
        if (!await ConfirmDiscard("closing")) return;
        closeConfirmed = true;
        Close();
    }

    async void OnSaveModel(object? sender, RoutedEventArgs e) => await SaveModelAs();

    /// <summary>"Save model…": the model (devices, openings, Cd overrides, occupants, names, environment) as a <c>.windflow.json</c>, the
    /// file "Open model…" and <c>--solve</c> read. True when a file was written.</summary>
    async Task<bool> SaveModelAs()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the model",
            SuggestedFileName = (model.Name.Length > 0 ? model.Name : "windflow") + ".windflow.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("WindFlow model") { Patterns = ["*.json"] }],
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(UserFiles.DefaultFolder()),
        });
        if (file?.TryGetLocalPath() is not { } path) return false;
        try
        {
            UserFiles.SaveModel(model, path);
        }
        catch (Exception ex)
        {
            Log.Error($"model save failed: {path}", ex);
            StatusText.Text = $"could not save {Path.GetFileName(path)}: {ex.Message}";
            return false;
        }
        cleanFingerprint = UserFiles.Fingerprint(model);
        settings.LastModel = path;
        settings.Save();
        Log.Info($"model saved: {path}");
        StatusText.Text = $"saved {Path.GetFileName(path)}";
        return true;
    }

    /// <summary>Clicking an opening in the viewport flips it and rebuilds the geometry in place. The flow on
    /// screen no longer matches the model, which the status strip says out loud until the next solve.</summary>
    void ToggleOpening(Opening opening)
    {
        if (locked)
        {
            StatusText.Text = $"{opening.Name}: the result is locked — unlock (top bar) to open or close windows";
            return;
        }
        ClearUndo();
        opening.IsOpen = !opening.IsOpen;
        Log.Info($"opening {opening.Name} {(opening.IsOpen ? "opened" : "closed")}");
        Viewport3D.Renderer.SetScene(Scene.Build(model, windVector, probes, overlay: Overlay()), frameCamera: false);
        if (live is { Active: true })
        {
            live.GeometryChanged(model, $"opening {opening.Name}");     // re-voxelise and warm-start; STALE clears when it lands
            StatusText.Text = $"{opening.Name} {(opening.IsOpen ? "opened" : "closed")} — applying…";
        }
        else
        {
            if (result != null) stale = true;
            StatusText.Text = $"{opening.Name} {(opening.IsOpen ? "opened" : "closed")} — flow is stale, press Run";
        }
        RepaintRows();                                                  // the switch flips now, from the model; the flow column follows the next pass
    }

    // ---------------------------------------------------------------- solver

    /// <summary>The Accurate pass has landed: nothing steps and the GPU is idle until the next edit, so there is
    /// nothing to pause. The Run button says "Done" and rests until an edit starts the next cycle.</summary>
    bool RunDone => live is { Active: true } s && s.Accurate.Phase == AccuratePhase.Accurate;

    const string RunTip = "Start the solver (Space pauses and resumes).";
    const string RunDoneTip = "Done: the averaged result has landed (see the pass strip: Settled, or time limit reached) and the GPU is idle. Change the wind, an opening, " +
                              "a device or the environment to run again.";

    /// <summary>Status tick: the Run button's label, enabled state and tip tell the truth about the solver.</summary>
    void UpdateRunButton(LiveSolver live)
    {
        bool done = RunDone;
        RunButton.Content = locked ? "Run" : done ? "Done" : live.State switch
        {
            LiveSolver.Mode.Running => "Pause",
            LiveSolver.Mode.Paused => "Resume",
            _ => "Run",
        };
        if (locked) return;                                  // ApplyLockUi owns enabled and the tip while locked
        if (RunButton.IsEnabled == !done) return;
        RunButton.IsEnabled = !done;
        ToolTip.SetTip(RunButton, done ? RunDoneTip : RunTip);
        ToolTip.SetShowOnDisabled(RunButton, true);
    }

    void OnRun(object? sender, RoutedEventArgs e)
    {
        if (live is not { } solver || RunDone) return;
        switch (solver.State)
        {
            case LiveSolver.Mode.Running: solver.SetPaused(true); break;
            case LiveSolver.Mode.Paused: solver.SetPaused(false); break;
            default: StartLive(); break;
        }
    }

    void StartLive()
    {
        if (live is not { } solver || model.Obstacles.Count == 0) { StatusText.Text = "load a model first"; return; }
        windVector = ReadWind();
        NormaliseWindBoxes();
        stale = false;
        solver.Start(model, windVector, thermalWanted, appliedDomain, BoxV1Toggle.IsChecked == true, openingModelSetting);
        var renderer = Viewport3D.Renderer;
        renderer.Particles.RequestCount(particleCount);
        renderer.Particles.Enabled = true;
        renderer.Ghost = true;
        ParticleToggle.IsChecked = true;
        GhostToggle.IsChecked = true;
        StatusText.Text = "starting solver…";
    }

    async Task Solve()
    {
        if (run is { Running: true })
        {
            run.Cancel();
            RunButton.Content = "Cancelling…";
            return;
        }
        if (model.Obstacles.Count == 0) { StatusText.Text = "load a model first"; return; }

        var (fromDeg, speed) = ReadWindInputs();
        NormaliseWindBoxes();
        windVector = SolverRun.WindVector(fromDeg, speed, model.NorthDeg);

        run = new SolverRun();
        RunButton.Content = "Cancel";
        SolveProgress.IsVisible = true;
        SolveProgress.Value = 0;
        StatusText.Text = "solving…";
        var progress = new Progress<double>(p =>
        {
            SolveProgress.Value = p * 100;
            StatusText.Text = $"solving… {p * 100:0}%";
        });

        try
        {
            run.Start(model, windVector, progress);
            var solved = await run.Task!;
            result = solved;
            solverLine = SolverRun.Summary(solved);
            StatusText.Text = solverLine;
            stale = false;

            var previous = volume;
            volume = await Task.Run(() => FieldVolume.Build(solved, previous));
            var renderer = Viewport3D.Renderer;
            renderer.Particles.SetField(volume);
            renderer.Particles.RequestCount(particleCount);
            renderer.Particles.Enabled = true;
            renderer.Ghost = true;
            ParticleToggle.IsChecked = true;
            GhostToggle.IsChecked = true;
            UpdateLegend();
        }
        catch (OperationCanceledException)
        {
            solverLine = "solve cancelled";
            StatusText.Text = solverLine;
        }
        catch (Exception ex)
        {
            Log.Error("batch solve failed", ex);
            solverLine = $"solve failed: {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}";
            StatusText.Text = solverLine;
        }
        finally
        {
            RunButton.Content = "Run";
            SolveProgress.IsVisible = false;
        }
    }

    // S3-08: the last wind the boxes held that parsed; a rejected entry marks its box and leaves these alone.
    double windFromValid = 270, windSpeedValid = 4;

    /// <summary>Direction (any number, wrapped to [0, 360)) and speed (0-40 m/s) from the boxes. Text that does not
    /// parse marks its box invalid and the last valid value is returned instead.</summary>
    (double FromDeg, double Speed) ReadWindInputs()
    {
        if (NumInput.TryParse(WindDirBox.Text, out double dir))
        {
            windFromValid = NumInput.WrapDegrees(dir);
            NumInput.Mark(WindDirBox, null);
        }
        else NumInput.Mark(WindDirBox, "Wind direction: type a number of degrees clockwise from north (any value is wrapped to 0-360). The last valid value is kept.");
        NumInput.Check(WindSpeedBox, "Wind speed", 0, 40, " m/s", out double speed);
        if (!WindSpeedBox.Classes.Contains("invalid")) windSpeedValid = speed;
        return (windFromValid, windSpeedValid);
    }

    /// <summary>Writes the parsed wind back into the boxes ("4,5" becomes "4.5", 450 becomes 90); a box that was
    /// rejected keeps its text and its red mark.</summary>
    void NormaliseWindBoxes()
    {
        if (!WindDirBox.Classes.Contains("invalid")) WindDirBox.Text = windFromValid.ToString("0.#");
        if (!WindSpeedBox.Classes.Contains("invalid")) WindSpeedBox.Text = windSpeedValid.ToString("0.#");
    }

    Vector2 ReadWind() { var (from, speed) = ReadWindInputs(); return SolverRun.WindVector(from, speed, model.NorthDeg); }

    // ---------------------------------------------------------------- fake live solver

    /// <summary>--fakelive: republish the solved field at 10 Hz with a slow synthetic gust, which is exactly
    /// the traffic pattern the phase-2 stepping solver will produce. Verifies the staging ring, the A/B
    /// cross-fade and the "no respawn on swap" rule under load.</summary>
    void StartFakeLive()
    {
        if (volume == null) return;
        liveClock.Restart();
        liveTimer.Start();
    }

    readonly FieldVolume?[] liveSlots = new FieldVolume?[2];
    int liveSlot;
    bool liveBusy;

    void OnLiveTick(object? sender, EventArgs e)
    {
        if (volume is not { } baseField || liveBusy) return;
        liveBusy = true;
        float t = (float)liveClock.Elapsed.TotalSeconds;
        int slot = liveSlot;
        liveSlot ^= 1;
        // Off the UI thread and onto a rotating pair of buffers, which is how the live solver should publish.
        Task.Run(() => baseField.Perturb(t, liveSlots[slot])).ContinueWith(task =>
        {
            liveSlots[slot] = task.Result;
            Viewport3D.Renderer.Particles.SetField(task.Result);
            liveBusy = false;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---------------------------------------------------------------- view controls

    void OnGhostClick(object? sender, RoutedEventArgs e) => Viewport3D.Renderer.Ghost = GhostToggle.IsChecked == true;

    void OnParticlesClick(object? sender, RoutedEventArgs e) =>
        Viewport3D.Renderer.Particles.Enabled = ParticleToggle.IsChecked == true;

    void OnSpeedMode(object? sender, RoutedEventArgs e) => ApplyView(ParticleView.Speed);

    void OnStillMode(object? sender, RoutedEventArgs e) => ApplyView(ParticleView.Still);

    void ApplyView(ParticleView view)
    {
        Viewport3D.Renderer.Particles.View = view;
        SpeedModeToggle.IsChecked = view == ParticleView.Speed;
        StillModeToggle.IsChecked = view == ParticleView.Still;
        StillSection.IsVisible = view == ParticleView.Still;
        UpdateLegend();
    }

    void OnStillThreshold(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (Viewport3D is null || StillText is null) return;     // fires during InitializeComponent
        float threshold = (float)Math.Round(e.NewValue, 2);
        Viewport3D.Renderer.Particles.StillThreshold = threshold;
        StillText.Text = $"{threshold:0.00} m/s";
        // The rooms' still % follows the slider: repaint on the render thread's next count, not 2 s later.
        roomStillShown = Viewport3D.Renderer.Particles.RoomStillVersion - StillRepaintEvery + 1;
        UpdateLegend();
    }

    void OnStillParticles(object? sender, RoutedEventArgs e) =>
        Viewport3D.Renderer.Particles.StillParticles = StillParticlesToggle.IsChecked == true;

    /// <summary>A room's still share at the Still-air threshold, from the render thread's count over the live
    /// field; Core's own StagnantFraction (0.1 m/s, occupied zone, time average) until the first count lands.</summary>
    float StillFraction(RoomResult r)
    {
        var still = Viewport3D.Renderer.Particles.RoomStill;
        int index = IndexOfRoom(r.Room.Id);
        return (uint)index < (uint)still.Length && !float.IsNaN(still[index]) ? still[index] : r.StagnantFraction;
    }

    /// <summary>Both legends read the same number the particles normalise to: the live indoor p98.</summary>
    void UpdateLegend()
    {
        var particles = Viewport3D.Renderer.Particles;
        bool still = particles.View == ParticleView.Still;
        float top = particles.Ready ? particles.SpeedMax : volume?.SpeedMax ?? 0;
        LegendBar.Background = still ? Palette.StillBrush() : Palette.SpeedBrush();
        LegendTitle.Text = still ? "STILL AIR" : "SPEED";
        LegendMin.Text = still ? "0" : "0";
        LegendMax.Text = still ? $"< {particles.StillThreshold:0.00} m/s" : top > 0 ? $"{top:0.00} m/s" : "— m/s";
        // Quarter ticks under the speed ramp, so a colour in the picture can be read back as a number.
        LegendTicks.IsVisible = !still && top > 0;
        (LegendTick1.Text, LegendTick2.Text, LegendTick3.Text) = ($"{top * 0.25f:0.00}", $"{top * 0.5f:0.00}", $"{top * 0.75f:0.00}");
        UpdateSliceLegend();
    }

    void OnSpeedChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (Viewport3D is not null) Viewport3D.Renderer.Particles.TimeScale = (float)e.NewValue;
        if (SpeedSliderText is not null) SpeedSliderText.Text = $"{e.NewValue:0}×";
    }

    /// <summary>Del, from the window or from the 3D view: the Ctrl+click set, else the selected room, else the selected opening.</summary>
    void DeleteSelection()
    {
        if (multiRooms.Count >= 2) DeleteRooms(MultiRooms());
        else if (Viewport3D.Renderer.SelectedRoom >= 0) DeleteRoom(Viewport3D.Renderer.SelectedRoom);
        else DeleteSelectedOpening();
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // A headless run takes foreground focus, so whatever the user types next arrives here: a stray V or
        // Space used to flip the view mode or pause the solver halfway through a measured sequence.
        if (Options.BenchSeconds > 0 || !string.IsNullOrEmpty(Options.Shot) ||
            !string.IsNullOrEmpty(Options.ReportPath)) return;
        // Esc, one step per press: leave placement, then show all rooms again, then clear the selection.
        if (e.Key == Key.Escape && (placing != null || addingOpening || addingRoom)) { CancelPlacement(); StatusText.Text = "placement cancelled"; return; }
        if (e.Key == Key.Escape && e.Source is not TextBox) { if (isolateOn) SetIsolated(-1); else ClearSelection(); return; }
        if (e.Source is TextBox) return;   // S4-13: typing a room name ("Garage 2") must not toggle the ghost or pause the solver
        if (e.Key == Key.Delete)
        {
            DeleteSelection();
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
        {
            if (e.Source is Slider or ComboBox or ComboBoxItem) return;   // their own arrow keys
            MoveSelection(e.Key);
            e.Handled = true;
            return;
        }
        // The viewport HWND handles these itself when it has focus; this is the path for when the shell does.
        switch (e.Key)
        {
            case Key.G:
                GhostToggle.IsChecked = !(GhostToggle.IsChecked == true);
                Viewport3D.Renderer.Ghost = GhostToggle.IsChecked == true;
                GhostToggle.Content = "Ghost";
                break;
            case Key.X:
                CycleWalls();
                break;
            case Key.P:
                ParticleToggle.IsChecked = !(ParticleToggle.IsChecked == true);
                Viewport3D.Renderer.Particles.Enabled = ParticleToggle.IsChecked == true;
                break;
            case Key.V:
                ApplyView(Viewport3D.Renderer.Particles.View == ParticleView.Speed
                    ? ParticleView.Still : ParticleView.Speed);
                break;
            case Key.F:
                FrameSelection();
                break;
            case Key.R:
                RotateDevice();
                break;
            case Key.Space:
                if (live is { Active: true } s2 && !locked && !RunDone) s2.SetPaused(s2.State == LiveSolver.Mode.Running);
                e.Handled = true;
                break;
        }
    }

    // ---------------------------------------------------------------- results panel

    int statsVersion = -1;

    /// <summary>Mean age of air as an architect reads it: minutes since the air came in from outside. "…" while it
    /// is still being worked out; anything past an hour is simply "> 1 h" (a closed room: no fresh air at all).</summary>
    public static string AgeText(float seconds) =>
        float.IsNaN(seconds) ? "…"
        : seconds >= 3600 ? "> 1 h"
        : seconds < 600 ? $"{seconds / 60:0.0} min"
        : $"{seconds / 60:0} min";

    /// <summary>The lists come from the model, so they are complete before the first Run; the solver's numbers fill in
    /// where it has them and read "—" before. The inspector repaints from the same rows.</summary>
    void RefreshPanel()
    {
        EnsureSpaces();                                             // the Rooms section's candidate spaces (cheap unless they are wanted and unknown)
        if (live is not { } solver || solver.Stats is not { } stats)
        {
            RefreshTrustIdle();
            if (statsVersion == IdleRows) return;                   // the model's rows are up; nothing new to paint
            if (ListBoxBeingEdited()) return;
            DropListFocus();
            statsVersion = IdleRows;
            BuildRows(null, null);
            return;
        }
        // The still % comes from the render thread's count, which lands between analysis passes.
        int stillVersion = Viewport3D.Renderer.Particles.RoomStillVersion;
        bool settling = AccurateTracker.ShowSettling(!solver.IsAccurate(stats), solver.Accurate.Running, solver.Accurate.EditedSinceAccurate);
        if (solver.StatsVersion == statsVersion && stillVersion - roomStillShown < StillRepaintEvery && settling == settlingShown) return;
        // A rename box is being edited: repainting the list would throw the half-typed name away.
        if (ListBoxBeingEdited()) return;
        DropListFocus();
        statsVersion = solver.StatsVersion;
        roomStillShown = stillVersion;
        settlingShown = settling;
        BuildRows(solver, stats);
    }

    bool settlingShown;
    const string SettlingText = "settling…";

    /// <summary>Only a TextBox in the lists (the rename box) is worth protecting from a repaint. A button or tick box the user just clicked
    /// (switch, ×, caret, pick) also holds keyboard focus, and used to freeze the lists until the focus moved on.</summary>
    bool ListBoxBeingEdited() =>
        (RoomList.IsKeyboardFocusWithin || OpeningListView.IsKeyboardFocusWithin) && FocusManager?.GetFocusedElement() is TextBox;

    /// <summary>Takes focus off a clicked button before the rows are replaced (RefreshDevices does the same): the row holding it is torn down by the swap.</summary>
    void DropListFocus()
    {
        if (RoomList.IsKeyboardFocusWithin || OpeningListView.IsKeyboardFocusWithin) FocusManager?.Focus(null);
    }

    /// <summary>A click changed what the rows show (an opening flipped): repaint them NOW, not when the next analysis pass lands (seconds on the CPU,
    /// and never before the first pass or Run). A rename in progress is committed first, outside the swap, as its LostFocus would be anyway.</summary>
    void RepaintRows()
    {
        if (ListBoxBeingEdited()) FocusManager?.Focus(null);      // LostFocus -> CommitRename posts its own repaint
        statsVersion = -1;
        RefreshPanel();
    }

    /// <summary>statsVersion while the rows show the model alone (no result yet); -1 forces a repaint on the next call.</summary>
    const int IdleRows = -2;

    /// <summary>The rows the lists show now (room rows with their expanded children; the flat opening rows), for the
    /// inspector and the arrow keys.</summary>
    List<object> roomRows = [];
    OpeningRow[] openingRows = [];

    void BuildRows(LiveSolver? solver, SimulationResult? stats)
    {
        // A simulation box face across a room makes that room's air a free boundary; the rows it touches say so.
        var audit = stats == null ? null : solver!.Audit;
        // Preview numbers are provisional (muted, "~"); only the finished Accurate pass makes them solid.
        bool provisional = stats == null || !solver!.IsAccurate(stats);
        bool settling = stats != null && AccurateTracker.ShowSettling(provisional, solver!.Accurate.Running, solver.Accurate.EditedSinceAccurate);
        bool updating = stats != null && solver!.Stale;
        RoomsStateTag.IsVisible = OpeningsStateTag.IsVisible = stats != null && (provisional || updating);
        RoomsStateText.Text = OpeningsStateText.Text = updating ? "updating…" : "preview";
        // The landed Accurate pass leaves its per-room convergence on the result: the ± on the ACH cell and, in Expert, the
        // batch-to-batch variation on the second line. Preview has none, so its rows stay "~" and plain.
        var convergence = provisional ? null : stats!.Convergence;
        int selectedRoom = Viewport3D.Renderer.SelectedRoom, selectedOpening = Viewport3D.Renderer.SelectedOpening;

        // ---- openings: one row per model opening; the flow from the stats when the solver has this opening
        var openings = new OpeningRow[model.Openings.Count];
        // The nested box's blend band (Expert, box on): which open openings sit in it, and whether Grow box can help.
        var fineBox = solver is { Nested: true } ? solver.FineBox : null;
        float band = solver?.SpongeWidth ?? 0;
        bool bandBottom = solver?.NestBottom ?? false, bandTop = solver?.NestTop ?? false;
        int inBand = 0;
        for (int index = 0; index < model.Openings.Count; index++)
        {
            var op = model.Openings[index];
            var f = stats?.Openings.FirstOrDefault(x => x.Opening.Id == op.Id);
            // The UI's own model says whether it is open: the stats pass may predate a toggle.
            bool open = op.IsOpen;
            string arrow = f == null || MathF.Abs(f.FlowRate) < 0.001f ? "" : f.FlowRate > 0 ? " out ↗" : " in ↘";
            string flow = !open ? "closed" : f == null ? "—" : settling ? SettlingText : $"{MathF.Abs(f.FlowRate):0.00} m³/s{arrow}";
            var extent = BlendBand.Extent(op);
            bool blend = open && fineBox is { } fb && BlendBand.Inside(fb, extent, band, bandBottom, bandTop);
            string blendTip = "";
            if (blend && BlendBand.Nearest(fineBox!.Value, extent.A, extent.B, bandBottom, bandTop) is { } near)
            {
                inBand++;
                blendTip = $"{near.Dist:0.##} m from the box's {BlendBand.FaceName(near.Face)} face, inside its {band:0.##} m blend " +
                           "band: the fine grid is pulled toward the coarse run there, so this flow is close to the whole-building one. " +
                           "Grow box (viewport header) moves the faces clear.";
            }
            var rooms = RoomsOf(op);
            openings[index] = new OpeningRow(
                index,
                op.Name,
                $"{op.Kind.ToString().ToLowerInvariant()} · {(open ? "open" : "closed")}{(open && arrow.Length > 0 ? " ·" + arrow : "")}",
                flow,
                open ? OpeningOpen : OpeningShut,
                sweepPicks.Contains(index),
                advancedMode,
                audit?.NearCutRoom(op.Center) ?? false,
                index == selectedOpening,
                open,
                locked,
                blend,
                blendTip,
                OpeningCd.Editable(op),
                OpeningCd.Text(OpeningCd.Effective(op)),
                OpeningCd.Name(OpeningCd.Effective(op)),
                OpeningEdit.IsUser(op),
                OpeningEdit.Text(op.Width),
                OpeningEdit.Text(op.Height),
                OpeningEdit.Text(OpeningEdit.Sill(model, op)),
                op.Kind == OpeningKind.Door ? 1 : 0,
                rooms.Count == 0 ? "no room" : string.Join(" · ", rooms.Take(2).Select(x => DisplayName(x.Id, x.Name))),
                op.Level,
                op.Kind.ToString().ToLowerInvariant());
        }

        // ---- rooms: one row per model room in display order, its openings as child rows while expanded
        var rows = new List<object>();
        foreach (int roomIndex in Enumerable.Range(0, model.Rooms.Count).OrderBy(i => RoomRank(model.Rooms[i].Id)))
        {
            var room = model.Rooms[roomIndex];
            var r = stats?.Rooms.FirstOrDefault(x => x.Room.Id == room.Id);
            var mine = RoomOpenings(room);
            bool selected = roomIndex == selectedRoom;
            bool picked = selected || multiRooms.Contains(room.Id);   // the row reads selected for the Ctrl+click set too
            bool expanded = expandedRooms.Contains(room.Id) && mine.Count > 0;
            if (r == null)
            {
                // No result for this room yet (nothing run, or the room is outside the box): the name, and dashes.
                rows.Add(new RoomRow(roomIndex, DisplayName(room.Id, room.Name), "—", "—", "—", "", "", RoomExchange,
                    stats == null ? "Run to fill in these columns." : "Outside the simulation box: no air cells here.",
                    ShowSub: advancedMode, Selected: picked, Renaming: room.Id == renamingId, Isolated: IsIsolated(roomIndex),
                    Storey: room.Level, OpeningCount: mine.Count, Expanded: expanded));
            }
            else
            {
                // Four readings, one per column: how much fresh air the room gets (fresh-air ACH from the age of air, and the dot
                // judges it), how much of the room it leaves still, how fresh it is, and one comfort flag. A room loop in open air
                // has no meaningful ACH: it shows a dash, a neutral dot and says why. No age yet: a dash and a pending dot, never
                // the total exchange standing in for it.
                bool exchange = Standards.ExchangeOnly(r);
                bool pending = float.IsNaN(r.FreshAch);
                var chip = Standards.Verdict(r, achIntent) switch
                {
                    AchBand.Good => RoomGood,
                    AchBand.Weak => RoomWeak,
                    AchBand.Poor => RoomBad,
                    _ => RoomExchange,       // pending (no air age yet) or an open-air loop: neutral
                };
                float still = StillFraction(r);
                var (flag, flagTip) = Standards.ComfortFlag(r, still, achIntent);
                bool hasT = !float.IsNaN(r.MeanTemperature);
                // Expert keeps the second line: storey, speed, temperature, and the exchange for an open-air room.
                string duty = float.IsNaN(r.HoldDutyWatts) ? "" : $"{(r.HoldDutyWatts >= 0 ? "heating" : "cooling")} {MathF.Abs(r.HoldDutyWatts):0} W";   // 6j
                string detail = $"{r.Room.Level} · {r.MeanSpeed:0.00} m/s" + (hasT ? $" · {r.MeanTemperature:0.0} °C" : "") + (duty.Length > 0 ? $" · {duty}" : "") +
                                (exchange ? $" · exchange {r.InternalExchange:0.0#} m³/s" : "") +
                                $" · total exchange {r.Ach:0.0} ACH" +
                                $" · gross in {r.Inflow:0.0#} / out {r.Outflow:0.0#} m³/s · net {r.NetFlux:+0.0#;-0.0#;0} m³/s" +
                                (r.VolumeIsNominal ? "" : " · exchange on voxel volume");
                string rowTip = exchange ? Standards.ExchangeTip : Standards.Tooltip(achIntent);
                rowTip += $"\nTotal air exchange (all air entering the room volume, incl. from other rooms): {r.Ach:0.0} ACH.";
                if (!float.IsNaN(r.ThroughOpeningInflow))   // portal step 1
                    rowTip += $"\nThrough openings (air entering the room through its listed windows and doors; fixed planes, not the ragged room boundary): {r.ThroughOpeningAch:0.0} ACH · net outdoor air through exterior openings {r.NetOutdoorAch:0.0} ACH · {r.UnexplainedShare * 100f:0} % of the air entering is not through an opening.";
                if (duty.Length > 0) rowTip += $"\nHeld at the indoor temperature: the ideal {duty} (time mean; walls adiabatic, so this is ventilation and gains only).";
                if (!r.VolumeIsNominal) rowTip += $"\n† The total exchange uses the voxel volume. {r.VolumeNote}";
                if (pending && !exchange) rowTip += $"\n— Fresh-air ACH needs the air age, solved once the flow has settled; no verdict until then.";
                else if (r.AgeSealed) rowTip += $"\nSealed (no air path): the air age sits at its 24 h cap, so no fresh air reaches this room.";
                if (r.TooFewCells) rowTip += $"\n{RoomResult.TooFewCellsLabel}: under {Envelope.MinRoomCells:0} cells across at this cell size; raise the cell budget.";   // IMP-C
                var conv = convergence?.Status.Rooms.FirstOrDefault(c => c.RoomId == r.Room.Id);
                // ± approx.: the room's total-exchange range, as a share of its mean, applied to the fresh-air number (no per-batch age yet).
                float halfWidth = ResultStatus.FreshHalfWidth(convergence, r);
                bool tooShort = !exchange && !r.AgeSealed && ResultStatus.FreshHasRange(convergence, r.Room.Id) && ResultStatus.TooShort(convergence, r.Room.Id);   // AGE-FIX: no fresh-air ± on a steady age; a sealed room reads "sealed", with no range to be short of (S6-17)
                bool drifting = !tooShort && conv != null && ResultStatus.Drifting(convergence, r.Room.Id);
                if (convergence != null && !exchange)
                {
                    rowTip += $"\n{ResultStatus.AgeLine(convergence)}.";   // 6k: which air age the fresh-air ACH is from
                    if (ResultStatus.AgeSteadyRoom(convergence, r.Room.Id)) { rowTip += "\nThis room: steady age (its online air age had not settled), no fresh-air ±."; detail += " · steady age"; }
                    if (!r.AgeSealed && !pending) rowTip += $"\nFresh-air ACH = 3600 / the room's mean air age. {AveragedPass.FreshAchMeaning}";   // #8
                    if (ResultStatus.AgeNote(convergence).Length > 0) rowTip += $"\n{ResultStatus.AgeNote(convergence)}";   // AGE-FIX: the one disclosure
                    if (ResultStatus.HasConservative(convergence) && !r.AgeSealed && float.IsFinite(r.ConservativeFreshAch)) rowTip += $"\nSteady-age value {r.ConservativeFreshAch:0.0} ACH (steady age on the averaged flow, ignores gust mixing).";   // AGE-HIGH
                }
                if (tooShort) rowTip += $"\n± —: {ResultStatus.TooShortTip}.";
                else if (conv != null && float.IsFinite(halfWidth))
                {
                    rowTip += "\n" + ResultStatus.RangeTip(conv, halfWidth);   // P16-01 / P16-03: the 99 % width a Settled room tested, the drift warning
                    if (float.IsFinite(conv.RelSd)) detail += $" · varies ±{conv.RelSd * 100:0} % batch-to-batch";
                }
                if (drifting && !float.IsFinite(halfWidth)) rowTip += "\n" + ResultStatus.DriftTipNoRange;   // RA-03: Standard has no ±, the drift warning still shows
                if (conv != null && !ResultStatus.FreshHasRange(convergence, r.Room.Id) && float.IsFinite(conv.RelSd) && !tooShort && !float.IsFinite(halfWidth)) detail += $" · varies ±{conv.RelSd * 100:0} % batch-to-batch";   // AGE-FIX: the total exchange's spread stays
                // The inspector's verdict line: the chip's tooltip written out, or why there is none yet.
                string verdict = flag.Length > 0 ? $"{flag} · {flagTip}" : exchange ? Standards.ExchangeTip : pending ? "no verdict until the air age lands" : "";
                rows.Add(new RoomRow(
                    roomIndex,
                    DisplayName(r.Room.Id, r.Room.Name),
                    settling ? SettlingText : (exchange ? "–" : pending || r.AgeSealed ? Standards.FreshText(r) : ResultStatus.AchCell(r.FreshAch, halfWidth, provisional, "", tooShort, drifting)) + (r.TooFewCells ? $" ({RoomResult.TooFewCellsLabel})" : ""),
                    settling ? SettlingText : $"{still * 100:0} %",
                    settling ? SettlingText : AgeText(r.MeanAge),
                    flag,
                    flagTip,
                    chip,
                    rowTip,
                    detail,
                    advancedMode,
                    room.Occupants,
                    advancedMode && !locked && selected,
                    audit!.CutRoomIds.Contains(r.Room.Id),
                    picked,
                    r.Room.Id == renamingId,
                    provisional,
                    IsIsolated(roomIndex),
                    room.Level,
                    $"{r.MeanSpeed:0.00} m/s",
                    mine.Count,
                    expanded,
                    verdict));
            }
            if (expanded) rows.AddRange(mine.Select(i => openings[i] with { Child = true }));
        }
        if (stats != null) RefreshDevices(fromTick: true);          // measured device flow comes from the same pass
        roomRows = rows;
        openingRows = openings;
        RoomList.ItemsSource = rows;
        OpeningListView.ItemsSource = openings;
        RefreshSpaceList();
        RefreshDeletedRows();
        // Air age: the live pass solves it once the average has settled (LiveSolver.AirAge); Expert's column reads "…"
        // until it lands, and the inspector's figure shows it in both modes.
        RoomsNote.Text = model.Rooms.Count == 0 ? (stats == null ? "No rooms yet." : "No rooms in the analysed storeys.")
            : stats == null ? "Run to fill in these columns."
            : "Dot: " + Standards.Summary(achIntent) +
              (audit!.DroppedRooms > 0 ? $" · {audit.DroppedRooms} room{(audit.DroppedRooms == 1 ? "" : "s")} outside the box" : "") +
              " · Fresh-air ACH = 3600 ÷ mean air age: outdoor air, with air from other rooms credited at its real age. The total exchange (all air entering the room, from other rooms too) is on the Expert line.";
        if (revealAdded && openings.Any(r => r.Selected && r.Added)) { revealAdded = false; Avalonia.Threading.Dispatcher.UIThread.Post(() => PanelScroll.ScrollToEnd(), Avalonia.Threading.DispatcherPriority.Background); }   // a new row is last
        RefreshInspector();
        if (stats == null) return;
        RefreshTrust(solver!, stats);
        BoxResetButton.IsVisible = audit!.CutsIndoorAir || audit.DroppedRooms > 0 || solver!.BoxNote.Length > 0;
        BoxGrowButton.IsVisible = inBand > 0;
        BoxGrowButton.Content = inBand > 0 ? $"Grow box ({inBand})" : "Grow box";
    }

    /// <summary>The openings in a room's walls: either side of the opening, just past the wall face, lies in the room
    /// (the same loop test the ledger's exterior classification makes for an opening whose sides are unknown).</summary>
    List<int> RoomOpenings(Room room)
    {
        var list = new List<int>();
        for (int i = 0; i < model.Openings.Count; i++)
            if (RoomsOf(model.Openings[i]).Contains(room)) list.Add(i);
        return list;
    }

    /// <summary>The rooms an opening serves (one for a window, two for an internal door), by the loop test above.</summary>
    List<Room> RoomsOf(Opening o)
    {
        var rooms = new List<Room>();
        var n = new Vector3(o.Normal.X, o.Normal.Y, 0);
        if (n.LengthSquared() < 1e-8f) return rooms;
        n = Vector3.Normalize(n);
        float reach = o.Depth * 0.5f + 0.3f;
        Vector3 a = o.Center + n * reach, b = o.Center - n * reach;
        foreach (var r in model.Rooms)
            if (Inside(r, a) || Inside(r, b)) rooms.Add(r);
        return rooms;
        static bool Inside(Room r, Vector3 q) => q.Z >= r.ZMin && q.Z <= r.ZMax && Devices.InLoops(r, q.X, q.Y);
    }

    // ---------------------------------------------------------------- inspector

    /// <summary>Rooms the user opened in the outliner, by id; the selected room opens on its own (SelectRoom) and closes
    /// again when the selection moves on, unless the user opened it with the caret.</summary>
    readonly HashSet<string> expandedRooms = new(StringComparer.Ordinal);
    string? autoExpanded;
    bool syncingInspector;

    /// <summary>The properties area under the list: the selected room or opening from the rows just built, whichever
    /// section is showing; the status strip's key hints follow it. A field being typed in keeps its text.</summary>
    void RefreshInspector()
    {
        int selRoom = Viewport3D.Renderer.SelectedRoom, selOpening = Viewport3D.Renderer.SelectedOpening;
        var room = selRoom >= 0 ? roomRows.OfType<RoomRow>().FirstOrDefault(r => r.Index == selRoom) : null;
        var op = (uint)selOpening < (uint)openingRows.Length ? openingRows[selOpening] : null;
        var multi = MultiRooms();
        bool merging = multi.Count >= 2;
        InspectorEmpty.IsVisible = room == null && op == null && !merging;
        MergeInspector.IsVisible = merging;
        if (merging)
        {
            string? cannot = RoomEdit.CannotMerge(multi);
            MergeTitle.Text = $"{multi.Count} rooms selected";
            MergeSub.Text = string.Join(" · ", multi.Select(r => DisplayName(r.Id, r.Name)));
            MergeButton.Content = $"Merge {multi.Count} rooms";
            MergeButton.IsEnabled = cannot == null;
            ToolTip.SetTip(MergeButton, cannot ?? $"One room named {DisplayName(multi[0].Id, multi[0].Name)}: all {multi.Count} outlines, the volumes and the people added up. Undo in the status strip until the next edit.");
        }
        RoomInspector.IsVisible = room != null;
        OpeningInspector.IsVisible = op != null;
        syncingInspector = true;
        if (room != null)
        {
            RoomInspTitle.Text = room.Name;
            RoomInspSub.Text = $"room · {room.Storey} · {room.OpeningCount} opening{(room.OpeningCount == 1 ? "" : "s")}";
            InspIsolateButton.IsChecked = room.Isolated;
            InspIsolateButton.Content = room.Isolated ? "Show all" : "Isolate";
            RoomInspAch.Text = room.Ach;
            RoomInspAchNote.Text = room.Provisional && room.Ach != "—" && room.Ach != SettlingText ? "preview" : "";
            RoomInspStill.Text = room.Still.Replace(" %", "", StringComparison.Ordinal);
            int cut = room.Age.IndexOf(' ');
            RoomInspAge.Text = cut > 0 ? room.Age[..cut] : room.Age;
            RoomInspAgeUnit.Text = cut > 0 ? room.Age[(cut + 1)..] : "";
            RoomInspSpeed.Text = room.Speed.Length > 0 ? room.Speed + " mean in the occupied zone" : "—";
            RoomInspVerdict.Text = room.Verdict.Length > 0 ? room.Verdict : "—";
            RoomInspDetail.Text = room.Sub;
            if (!RoomInspPeople.IsFocused)       // a box being typed in keeps the room it was opened on: the Tag is what it commits to
            {
                RoomInspPeople.Tag = room.Index;
                RoomInspPeople.Text = room.Occupants.ToString(); NumInput.Mark(RoomInspPeople, null);
            }
        }
        if (op != null)
        {
            OpeningInspTitle.Text = op.Name;
            OpeningInspSub.Text = $"{op.Kind} · {op.RoomName}{(op.Storey.Length > 0 ? $" · {op.Storey}" : "")}{(op.Added ? "" : " · detected")}";
            foreach (var (box, text) in new[] { (InspWidth, op.WidthText), (InspHeight, op.HeightText), (InspSill, op.SillText) })
            {
                box.IsReadOnly = !op.Added;
                if (!box.IsFocused) { box.Text = text; NumInput.Mark(box, null); }
            }
            InspType.IsEnabled = op.Added;
            InspType.SelectedIndex = op.TypeIndex;
            InspSwitch.DataContext = op;
            InspSwitch.Classes.Set("on", op.Open);
            InspSwitch.Classes.Set("locked", op.Locked);
            InspKnob.Background = op.Chip;
            InspKnob.HorizontalAlignment = op.Knob;
            OpeningInspState.Text = op.Open ? (op.Flow == "—" ? "Open" : $"Open · {op.Flow}") : "Closed";
            OpeningInspectorExpert.IsVisible = advancedMode && op.CdEditable;
            if (!InspCd.IsFocused) { InspCd.Text = op.CdText; NumInput.Mark(InspCd, null); }
            InspCdName.Text = op.CdName;
        }
        syncingInspector = false;
        KeyHints.Text = addingOpening || addingRoom ? "Esc  cancel"
            : merging ? "Ctrl+click  add or remove a room   ·   Del  delete them   ·   Esc  clear selection"
            : room != null ? $"F  frame {room.Name}   ·   Del  delete it   ·   Esc  clear selection"
            : op != null ? $"F  frame {op.Name}   ·   Del  delete it   ·   Esc  clear selection"
            : model.Rooms.Count > 0 ? "F  frame the building" : "";
    }

    /// <summary>The outliner's caret: show or hide the room's openings; the selection does not move.</summary>
    void OnRoomCaret(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index } || (uint)index >= (uint)model.Rooms.Count) return;
        string id = model.Rooms[index].Id;
        if (!expandedRooms.Remove(id)) expandedRooms.Add(id);
        if (autoExpanded == id) autoExpanded = null;       // now the user's choice, whichever way it went
        statsVersion = -1;
        RefreshPanel();
    }

    /// <summary>F, and the inspector's Frame: the camera on the selection, or on the whole building with none.</summary>
    void OnFrameSelection(object? sender, RoutedEventArgs e) => FrameSelection();

    void FrameSelection()
    {
        var r = Viewport3D.Renderer;
        if (r.SelectedRoom >= 0) r.FrameRoom(r.SelectedRoom);
        else if (r.SelectedOpening >= 0) r.FrameOpening(r.SelectedOpening);
        else r.FrameModel();
    }

    /// <summary>↑ ↓ walk the list the section shows (rooms with their expanded openings, or the flat openings); → opens the
    /// selected room's openings, ← closes them.</summary>
    void MoveSelection(Key key)
    {
        var r = Viewport3D.Renderer;
        if (key is Key.Right or Key.Left)
        {
            if ((uint)r.SelectedRoom >= (uint)model.Rooms.Count) return;
            string id = model.Rooms[r.SelectedRoom].Id;
            if (key == Key.Right) expandedRooms.Add(id); else expandedRooms.Remove(id);
            statsVersion = -1;
            RefreshPanel();
            return;
        }
        int step = key == Key.Down ? 1 : -1;
        if (panelTab == TabOpenings)
        {
            if (openingRows.Length == 0) return;
            int next = Math.Clamp((r.SelectedOpening < 0 ? (step > 0 ? -1 : openingRows.Length) : r.SelectedOpening) + step, 0, openingRows.Length - 1);
            SelectOpening(next);
        }
        else if (panelTab == TabRooms)
        {
            if (roomRows.Count == 0) return;
            int at = roomRows.FindIndex(x => x is RoomRow { Selected: true } || x is OpeningRow { Selected: true });
            int next = Math.Clamp((at < 0 ? (step > 0 ? -1 : roomRows.Count) : at) + step, 0, roomRows.Count - 1);
            if (roomRows[next] is RoomRow room) { SelectRoom(room.Index, frame: false); return; }
            if (roomRows[next] is OpeningRow op) SelectOpening(op.Index);
        }
        else return;
        statsVersion = -1;
        RefreshPanel();
    }

    int roomStillShown;
    const int StillRepaintEvery = 20;       // ~2 s at the 10 Hz field rate: the rows are read, not watched

    /// <summary>Results reference the solver's model snapshot, not the UI's objects: match by Id.</summary>
    int IndexOfOpening(string id) => model.Openings.FindIndex(o => o.Id == id);

    int IndexOfRoom(string id)
    {
        for (int i = 0; i < model.Rooms.Count; i++) if (model.Rooms[i].Id == id) return i;
        return -1;
    }

    /// <summary>Switch the room chips between the two questions. Both sets of thresholds and their sources
    /// are in Standards.cs; the tooltip on every row changes with it.</summary>
    void OnAchIntent(object? sender, RoutedEventArgs e)
    {
        achIntent = AchIntentToggle.IsChecked == true ? AchIntent.Cooling : AchIntent.AirQuality;
        AchIntentToggle.Content = achIntent == AchIntent.Cooling ? "judged for cooling ▾" : "judged for air quality ▾";
        statsVersion = -1;              // force the rows to repaint with the new bands
        RefreshPanel();
    }

    /// <summary>Off by default: a fresh launch lands on the empty state rather than a model the user may
    /// have finished with. The path itself is always remembered.</summary>
    void OnReopenLast(object? sender, RoutedEventArgs e)
    {
        settings.ReopenLast = ReopenLastToggle.IsChecked == true;
        settings.Save();
        StatusText.Text = settings.ReopenLast
            ? $"WindFlow will reopen {(settings.LastModel.Length > 0 ? Path.GetFileName(settings.LastModel) : "the last model")} next time"
            : "WindFlow will start on the empty state next time";
    }

    // ---------------------------------------------------------------- video export

    async void OnExportTurntable(object? sender, RoutedEventArgs e)
    {
        var path = await AskVideoPath("turntable.mp4");
        if (path != null) StartExport(path, 12);
    }

    async void OnExportRecord(object? sender, RoutedEventArgs e)
    {
        if (recorder.Recording) { FinishExport(); return; }
        var path = await AskVideoPath("recording.mp4");
        if (path != null) StartExport(path, 0);
    }

    async Task<string?> AskVideoPath(string suggested)
    {
        var folder = UserFiles.DefaultFolder();     // S4-04
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save video",
            SuggestedFileName = suggested,
            DefaultExtension = "mp4",
            FileTypeChoices = [new FilePickerFileType("MP4 video") { Patterns = ["*.mp4"] }],
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(folder),
        });
        return file?.TryGetLocalPath();
    }

    void StartExport(string path, double turntableSeconds)
    {
        var renderer = Viewport3D.Renderer;
        if (renderer.Width < 16 || renderer.Height < 16) { StatusText.Text = "viewport is not ready"; return; }
        Log.Info($"export start: {path} · {renderer.Width}x{renderer.Height} · turntable {turntableSeconds:0.#} s");
        recorder.Start(path, renderer.Width, renderer.Height, renderer.ReadbackPitch, 30);
        renderer.BeginRecording(recorder, turntableSeconds);
        RecordMenuItem.Header = "Stop recording";
        StatusText.Text = turntableSeconds > 0
            ? $"recording a {turntableSeconds:0} s turntable to {Path.GetFileName(path)}…"
            : $"recording to {Path.GetFileName(path)}… Export ▸ Stop to finish";

        exportTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        exportTimer.Tick -= OnExportTick;
        exportTimer.Tick += OnExportTick;
        exportTimer.Start();
    }

    void OnExportTick(object? sender, EventArgs e)
    {
        if (Viewport3D.Renderer.RecordingFinished) FinishExport();
        else if (recorder.Recording)
            StatusText.Text = $"recording… {recorder.FramesWritten} frames" +
                              (recorder.FramesDropped > 0 ? $", {recorder.FramesDropped} dropped" : "");
    }

    void FinishExport()
    {
        exportTimer?.Stop();
        Viewport3D.Renderer.EndRecording();
        recorder.Stop();
        RecordMenuItem.Header = "Record video";
        exportDone = true;
        Log.Info($"export done: {recorder.Path} · {recorder.FramesWritten} frames, {recorder.FramesDropped} dropped · error {recorder.Error ?? "none"}");
        StatusText.Text = recorder.Error != null
            ? "export failed: " + recorder.Error
            : $"wrote {Path.GetFileName(recorder.Path)} — {recorder.FramesWritten} frames at {recorder.Fps} fps" +
              $", {recorder.FramesDropped} dropped, {new FileInfo(recorder.Path).Length / 1024:N0} kB";
    }

    bool exportDone;

    /// <summary>--export-turntable: solve live for a few seconds, orbit once while recording, then verify the
    /// file by decoding it back and exit. With --twice it exports a second time in the same process, which is
    /// what caught the recorder queue being completed for good by the first Stop().</summary>
    async Task TurntableSequence(double seconds, string path)
    {
        StartLive();
        await Task.Delay(9000);
        var lines = await ExportOnce(seconds, path);
        if (Options.Twice)
        {
            lines.Add("");
            lines.Add("second export in the same process:");
            lines.AddRange(await ExportOnce(seconds, Path.ChangeExtension(path, ".second.mp4")));
            var r = Viewport3D.Renderer;
            lines.Add($"renderer  fps {r.Fps:0.0} phase {r.Phase} status {(r.Status.Length == 0 ? "ok" : r.Status)}");
        }
        File.WriteAllLines(Path.ChangeExtension(path, ".txt"), lines);
        Close();
    }

    async Task<List<string>> ExportOnce(double seconds, string path)
    {
        exportDone = false;
        StartExport(path, seconds);
        for (int i = 0; i < 2000 && !exportDone; i++) await Task.Delay(50);
        await Task.Delay(500);

        var lines = new List<string>
        {
            $"path      {path}",
            $"size      {(File.Exists(path) ? new FileInfo(path).Length : 0):N0} bytes",
            $"encoder   Media Foundation H.264 (OS), {recorder.Width}x{recorder.Height} at {recorder.Fps} fps",
            $"frames    {recorder.FramesWritten} written, {recorder.FramesDropped} dropped",
            $"expected  {seconds:0} s x {recorder.Fps} fps = {seconds * recorder.Fps:0} frames",
            $"error     {recorder.Error ?? "none"}",
        };
        try
        {
            string framePng = Path.ChangeExtension(path, ".frame.png");
            var probe = VideoRecorder.ExtractFrame(path, seconds / 2, framePng);
            lines.Add($"decoded   {probe.Frames} frames, last timestamp {probe.LastSeconds:0.00} s, {probe.Width}x{probe.Height}");
            lines.Add($"midframe  {framePng}");
        }
        catch (Exception ex)
        {
            lines.Add("decode failed: " + ex.Message);
        }
        return lines;
    }

    void OnTogglePanel(object? sender, RoutedEventArgs e) =>
        ResultsPanel.IsVisible = PanelToggle.IsChecked == true;

    // ---------------------------------------------------------------- slice and probe

    void OnSliceH(object? sender, RoutedEventArgs e)
    {
        var particles = Viewport3D.Renderer.Particles;
        particles.SliceHorizontal = SliceHToggle.IsChecked == true;
        particles.SliceZ = (float)SliceZSlider.Value;      // the slider may never have raised a change yet
        particles.SliceOther = (float)SliceOtherSlider.Value;
        UpdateSliceLegend();
    }

    /// <summary>The field buttons and the height slider only show on a cut: with none on, turn Plan on so the click does something.</summary>
    void EnsureSlice()
    {
        if (SliceHToggle.IsChecked == true || SliceVToggle.IsChecked == true) return;
        SliceHToggle.IsChecked = true;
        OnSliceH(null, new RoutedEventArgs());
    }

    void OnSliceV(object? sender, RoutedEventArgs e)
    {
        Viewport3D.Renderer.Particles.SliceVertical = SliceVToggle.IsChecked == true;
        UpdateSliceLegend();
    }

    void OnSliceAxis(object? sender, RoutedEventArgs e)
    {
        var particles = Viewport3D.Renderer.Particles;
        particles.SliceAxis = particles.SliceAxis == 1 ? 2 : 1;
        SetSliceRanges();
    }

    void OnSliceZ(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (Viewport3D is null) return;
        Viewport3D.Renderer.Particles.SliceZ = (float)e.NewValue;
        SliceZText.Text = $"{e.NewValue:0.0} m";
        if (SliceZSlider.IsPointerOver || SliceZSlider.IsFocused) EnsureSlice();      // the user's drag, not a range reset
        if (SliceTitle != null) UpdateSliceCaption();
    }

    void OnSliceOther(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (Viewport3D is null) return;
        Viewport3D.Renderer.Particles.SliceOther = (float)e.NewValue;
        SliceOtherText.Text = $"{e.NewValue:0.0} m";
        if (SliceTitle != null) UpdateSliceCaption();
    }

    /// <summary>Slider ranges follow the model, so the handles always cover the building.</summary>
    void SetSliceRanges()
    {
        var particles = Viewport3D.Renderer.Particles;
        var min = particles.FocusMin;
        var max = particles.FocusMax;
        if (max.Z - min.Z < 0.5f) return;
        SliceZSlider.Minimum = Math.Round(min.Z, 1);
        SliceZSlider.Maximum = Math.Round(max.Z, 1);
        bool xAxis = particles.SliceAxis == 1;
        SliceOtherSlider.Minimum = Math.Round(xAxis ? min.X : min.Y, 1);
        SliceOtherSlider.Maximum = Math.Round(xAxis ? max.X : max.Y, 1);
        if (particles.SliceOther < SliceOtherSlider.Minimum || particles.SliceOther > SliceOtherSlider.Maximum)
            SliceOtherSlider.Value = (SliceOtherSlider.Minimum + SliceOtherSlider.Maximum) * 0.5;
        // Default the plan cut to head height on the lowest analysed storey, not to the ground slab.
        if (SliceZSlider.Value <= SliceZSlider.Minimum + 0.01)
            SliceZSlider.Value = Math.Min(SliceZSlider.Maximum, model.GroundZ + 1.1);
        particles.SliceZ = (float)SliceZSlider.Value;
        particles.SliceOther = (float)SliceOtherSlider.Value;
    }

    /// <summary>The slice title and hint: what field, which cut, where, and what to look for (SliceCaption).</summary>
    void UpdateSliceCaption()
    {
        var p = Viewport3D.Renderer.Particles;
        int field = p.SliceField;
        SliceTitle.Text = SliceCaption.Title(field, p.SliceHorizontal, p.SliceVertical, p.SliceZ, p.SliceAxis, p.SliceOther,
            model, id => DisplayName(id, id), RoomRank);
        SliceHint.Text = SliceCaption.Hint(field, p.SliceHorizontal, p.SliceVertical);
        SliceLegendRow.IsVisible = p.SliceHorizontal || p.SliceVertical;   // no scale under "No cut shown"
    }

    void UpdateSliceLegend()
    {
        UpdateSliceCaption();
        var particles = Viewport3D.Renderer.Particles;
        if (particles.SliceField == 0)
        {
            SliceLegend.Background = Palette.SpeedBrush();
            SliceLegendMin.Text = "0";
            SliceLegendMax.Text = particles.SpeedMax > 0 ? $"{particles.SpeedMax:0.00} m/s" : "— m/s";
            float top = particles.SpeedMax;
            SliceTicks.IsVisible = top > 0;
            (SliceTick1.Text, SliceTick2.Text, SliceTick3.Text) = ($"{top * 0.25f:0.00}", $"{top * 0.5f:0.00}", $"{top * 0.75f:0.00}");
            return;
        }
        SliceTicks.IsVisible = false;      // the diverging ramps keep their ± end labels only
        SliceLegend.Background = Palette.DivergingBrush();
        float half = particles.HasScalars ? particles.ScalarScale : 0;
        string digits = particles.SliceField == SliceFieldDifference ? "0.00" : "0.#";
        // Temperature is published relative to the mean of the air cells, so the legend says what zero is.
        string unit = particles.SliceField switch
        {
            2 => $"K @ {live?.ScalarReferenceC ?? 0:0.0} °C",
            SliceFieldDifference => "m/s  B−A",
            _ => "Pa",
        };
        SliceLegendMin.Text = half > 0 ? $"-{half.ToString(digits)}" : "-";
        SliceLegendMax.Text = half > 0 ? $"+{half.ToString(digits)} {unit}" : $"— {unit}";
    }

    void OnFieldSpeed(object? sender, RoutedEventArgs e) { ExitCompare(); SetSliceField(0); EnsureSlice(); }

    void OnFieldPressure(object? sender, RoutedEventArgs e) { ExitCompare(); SetSliceField(1); EnsureSlice(); }

    void OnFieldTemp(object? sender, RoutedEventArgs e) { ExitCompare(); SetSliceField(2); }

    void SetSliceField(int field)
    {
        Viewport3D.Renderer.Particles.SliceField = field;
        if (live is { } solver) solver.ScalarWanted = field;
        FieldSpeedToggle.IsChecked = field == 0;
        FieldPressureToggle.IsChecked = field == 1;
        FieldTempToggle.IsChecked = field == 2;
        UpdateSliceLegend();
    }

    void OnProbeToggle(object? sender, RoutedEventArgs e) => Viewport3D.ProbeMode = ProbeToggle.IsChecked == true;

    void OnProbeClear(object? sender, RoutedEventArgs e)
    {
        probes.Clear();
        ProbeList.ItemsSource = null;
        Viewport3D.Renderer.SetScene(Scene.Build(model, windVector, probes, overlay: Overlay()), frameCamera: false);
    }

    readonly List<Vector3> probes = new();
    readonly Settings settings = Settings.Load(readOnly: Automated);
    readonly VideoRecorder recorder = new();
    DispatcherTimer? exportTimer;

    /// <summary>The simulation box the user has drawn. The live solver does not consume it yet; this is the
    /// state the next step reads (min/max in model metres, building bounds + 1 m to start with).</summary>
    public SimBox SimBox
    {
        get { var (min, max) = Viewport3D.Renderer.Box; return new SimBox(min, max); }
    }

    Vector3 lastBoxMin, lastBoxMax;
    DateTime boxChangedAt = DateTime.MaxValue;
    DomainBox? appliedDomain;

    /// <summary>The section box is also the simulation domain: re-voxelising costs ~400 ms and restarts the
    /// flow, so a drag is debounced and only the box the user settled on reaches the solver.</summary>
    void TrackBoxDomain()
    {
        if (live is not { Active: true } solver) return;
        var r = Viewport3D.Renderer;
        var (min, max) = r.Box;
        bool on = r.ShowBox && max.X > min.X;

        if (!on)
        {
            if (appliedDomain != null)
            {
                appliedDomain = null;
                solver.SetDomain(null);
                StatusText.Text = "simulation box off — solving the whole building again";
            }
            return;
        }
        if (min != lastBoxMin || max != lastBoxMax)
        {
            lastBoxMin = min;
            lastBoxMax = max;
            boxChangedAt = DateTime.UtcNow;      // still dragging
            return;
        }
        if (DateTime.UtcNow - boxChangedAt < BoxDebounce) return;
        boxChangedAt = DateTime.MaxValue;

        var wanted = new DomainBox(min, max);
        if (!wanted.IsValid || (appliedDomain is { } a && a == wanted)) return;
        appliedDomain = wanted;
        solver.SetDomain(wanted, BoxV1Toggle.IsChecked == true);
        var size = wanted.Size;
        StatusText.Text = $"solving inside the box {size.X:0.#}×{size.Y:0.#}×{size.Z:0.#} m — re-voxelising…";
    }

    static readonly TimeSpan BoxDebounce = TimeSpan.FromSeconds(0.5);

    /// <summary>The blend band's inner edge for the viewport: the solved box inset by the band on every driven face
    /// (sides always; bottom and top only where the box is nested there).</summary>
    void UpdateSpongeBand()
    {
        BandBox? band = null;
        if (live is { Nested: true } ls && ls.FineBox is { } fb && ls.SpongeWidth > 0)
        {
            float w = ls.SpongeWidth;
            band = new BandBox(fb.Min + new Vector3(w, w, ls.NestBottom ? w : 0), fb.Max - new Vector3(w, w, ls.NestTop ? w : 0));
        }
        if (band != Viewport3D.Renderer.SpongeBand) Viewport3D.Renderer.SpongeBand = band;
    }

    /// <summary>--box: put the renderer box where the command line asked and hand it straight to the solver,
    /// so a bench or a shot starts on the box instead of restarting into it.</summary>
    bool ApplyOptionBox()
    {
        if (Options.Box is not { } box) return false;
        boxResetPending = false;             // the pending reset would overwrite it on the next tick
        Viewport3D.Renderer.SetBox(box.Min, box.Max);
        BoxToggle.IsChecked = true;
        OnBoxToggle(null, null!);
        lastBoxMin = box.Min;
        lastBoxMax = box.Max;
        boxChangedAt = DateTime.MaxValue;
        appliedDomain = box;
        return true;
    }

    int boxRejects = -1;

    /// <summary>The solver refused a sealed box and kept the domain it was already running: put the gizmo
    /// back on that domain so the picture and the solve agree, and offer the way out.</summary>
    void PollBoxRejected()
    {
        if (live is not { } solver) return;
        // A diverged box solve leaves the thread dead: the only useful action left is dropping the box.
        if (solver.State == LiveSolver.Mode.Failed && (appliedDomain != null || solver.ActiveDomain != null))
            BoxResetButton.IsVisible = true;
        if (boxRejects < 0) { boxRejects = solver.BoxRejects; return; }
        if (solver.BoxRejects == boxRejects) return;
        boxRejects = solver.BoxRejects;
        // A newer box change is already queued behind the refused one: that one decides where the gizmo goes.
        if (solver.RejectedDomainSeq != solver.LatestDomainSeq)
        {
            Log.Info($"box: refusal of change #{solver.RejectedDomainSeq} ignored, #{solver.LatestDomainSeq} is queued");
            return;
        }

        appliedDomain = solver.ActiveDomain;
        boxChangedAt = DateTime.MaxValue;
        if (solver.ActiveDomain is { } active) Viewport3D.Renderer.SetBox(active.Min, active.Max);
        else
        {
            BoxToggle.IsChecked = false;
            ClipToggle.IsChecked = false;
            Viewport3D.Renderer.ShowBox = false;
            Viewport3D.Renderer.ClipToBox = false;
            ResetBox();
        }
        BoxResetButton.IsVisible = true;
        StatusText.Text = solver.BoxNote;
    }

    /// <summary>Drop the box and go back to the whole building. Also the way out of a diverged box solve:
    /// the solver thread is gone by then, so the session is started again rather than handed a command.</summary>
    void OnBoxReset(object? sender, RoutedEventArgs e)
    {
        appliedDomain = null;
        boxChangedAt = DateTime.MaxValue;
        BoxToggle.IsChecked = false;
        ClipToggle.IsChecked = false;
        var renderer = Viewport3D.Renderer;
        renderer.ShowBox = false;
        renderer.ClipToBox = false;
        ResetBox();
        BoxResetButton.IsVisible = false;
        statsVersion = -1;                       // the panel must repaint even if no new pass has landed
        if (live is not { } solver) return;
        if (solver.Active && solver.State != LiveSolver.Mode.Failed) solver.SetDomain(null);
        else StartLive();
        StatusText.Text = "solving the whole building again";
    }

    /// <summary>Grow box: every open opening and enabled device touching the solved box, cleared of the blend band
    /// face by face (BlendBand.Grow). Setting the gizmo box is enough: TrackBoxDomain hands it to the solver after
    /// the usual debounce, exactly as if the user had dragged the faces out.</summary>
    void OnBoxGrow(object? sender, RoutedEventArgs e)
    {
        if (locked || live is not { Nested: true } solver || solver.FineBox is not { } box) return;
        var items = model.Openings.Where(o => o.IsOpen).Select(BlendBand.Extent)
            .Concat(model.Sources.Where(s => s.Enabled).Select(BlendBand.Extent)).ToList();
        var grown = BlendBand.Grow(box, items, solver.SpongeWidth, solver.FineDx, solver.NestBottom, solver.NestTop, model.GroundZ);
        if (grown == box) { StatusText.Text = "nothing in the blend band to clear"; return; }
        Viewport3D.Renderer.SetBox(grown.Min, grown.Max);
        var d = grown.Size - box.Size;
        StatusText.Text = $"growing the box by {d.X:0.#} × {d.Y:0.#} × {d.Z:0.#} m to clear the blend band — re-voxelising…";
        Log.Info($"grow box: {box.Min} -> {box.Max} grown to {grown.Min} -> {grown.Max} (band {solver.SpongeWidth:0.##} m)");
    }

    void ResetBox()
    {
        if (Viewport3D.Renderer.CurrentScene is not { } scene) return;
        var pad = new Vector3(1f);
        Viewport3D.Renderer.SetBox(scene.Min - pad, scene.Max + pad);
    }

    /// <summary>Advanced: run the box the v1 way (the box is the whole domain, open faces) for comparison with
    /// the nested fine box. Re-applies the current box in the new mode.</summary>
    void OnBoxV1(object? sender, RoutedEventArgs e)
    {
        if (live is not { Active: true } solver || appliedDomain is not { } box) return;
        solver.SetDomain(box, BoxV1Toggle.IsChecked == true);
        StatusText.Text = BoxV1Toggle.IsChecked == true
            ? "box is the whole domain (v1): open faces, re-voxelising…"
            : "box nested in the whole-building run: opening the fine box…";
    }

    void OnBoxToggle(object? sender, RoutedEventArgs e)
    {
        var renderer = Viewport3D.Renderer;
        renderer.ShowBox = BoxToggle.IsChecked == true;
        var (min, max) = renderer.Box;
        if (renderer.ShowBox && max.X <= min.X) ResetBox();
        if (!renderer.ShowBox) { ClipToggle.IsChecked = false; renderer.ClipToBox = false; }
    }

    void OnClipToggle(object? sender, RoutedEventArgs e)
    {
        var renderer = Viewport3D.Renderer;
        renderer.ClipToBox = ClipToggle.IsChecked == true;
        if (renderer.ClipToBox && !renderer.ShowBox)
        {
            BoxToggle.IsChecked = true;
            renderer.ShowBox = true;
            var (min, max) = renderer.Box;
            if (max.X <= min.X) ResetBox();
        }
    }

    /// <summary>X cycles Clay to Ghost to X-ray: solid, translucent, then edges only.</summary>
    void CycleWalls()
    {
        var renderer = Viewport3D.Renderer;
        renderer.Walls = renderer.Walls switch
        {
            WallMode.Clay => WallMode.Ghost,
            WallMode.Ghost => WallMode.Xray,
            _ => WallMode.Clay,
        };
        GhostToggle.IsChecked = renderer.Walls != WallMode.Clay;
        GhostToggle.Content = renderer.Walls == WallMode.Xray ? "X-ray" : "Ghost";
    }

    void SyncSliceSliders()
    {
        var particles = Viewport3D.Renderer.Particles;
        if (Math.Abs(SliceZSlider.Value - particles.SliceZ) > 0.005) SliceZSlider.Value = particles.SliceZ;
        if (Math.Abs(SliceOtherSlider.Value - particles.SliceOther) > 0.005) SliceOtherSlider.Value = particles.SliceOther;
    }

    void SetWindFromDrag(double fromDegrees)
    {
        // The drag works in model axes; the box takes true bearings (P13b-04).
        if (!locked) WindDirBox.Text = (((fromDegrees - model.NorthDeg) % 360 + 360) % 360).ToString("0");
    }

    void AddProbe(Vector3 point)
    {
        if (probes.Count >= 5) probes.RemoveAt(0);
        probes.Add(point);
        Viewport3D.Renderer.SetScene(Scene.Build(model, windVector, probes, overlay: Overlay()), frameCamera: false);
        RefreshProbes();
    }

    void RefreshProbes()
    {
        var rows = new List<ProbeRow>();
        var stats = live?.Stats;
        for (int i = 0; i < probes.Count; i++)
        {
            var p = probes[i];
            string values;
            if (stats != null)
            {
                // Inside a nested box the fine pass answers, outside it the whole-building one.
                var source = live?.ProbeSource(p, stats) ?? stats;
                var v = Analysis.Probe(source, p);
                string temp = float.IsNaN(v.Temperature) ? "" : $" · {v.Temperature:0.0} °C";
                string room = string.IsNullOrEmpty(v.Room) ? "outside" : v.Room;
                // Preview publishes an instantaneous field; the landed Accurate pass is the batch mean. Say which.
                string basis = live?.IsAccurate(stats) == true ? "averaged" : "live";
                values = $"{basis} · {v.Speed:0.00} m/s · ({v.Velocity.X:0.0}, {v.Velocity.Y:0.0}, {v.Velocity.Z:0.0}) · " +
                         $"{v.Pressure:0.0} Pa{temp} · {room}{(source != stats ? " · fine box" : "")}";
            }
            else values = "waiting for the first analysis pass";
            rows.Add(new ProbeRow($"pin {i + 1}   x {p.X:0.0}  y {p.Y:0.0}  z {p.Z:0.0}", values));
        }
        ProbeList.ItemsSource = rows;
        ProbeHint.Text = probes.Count == 0 ? "B, then click the slice or a floor."
            : "Up to five pins; Clear removes them. Point speeds are indicative: one cell of the grid, live in Preview, the batch mean after the averaged pass.";
    }

    // ---------------------------------------------------------------- full-window screenshot

    /// <summary>Renders the Avalonia tree to a bitmap and pastes the viewport's back buffer into the hole the
    /// child HWND leaves, which is the only way to review the whole shell from a headless run.</summary>
    async Task SaveShell(string path)
    {
        var renderer = Viewport3D.Renderer;
        byte[]? viewport = null;
        int viewportPitch = 0;
        if (Viewport3D.IsVisible) renderer.RequestShot();
        for (int i = 0; i < 150 && viewport == null && Viewport3D.IsVisible; i++)
        {
            await Task.Delay(20);
            viewport = renderer.TakeShot(out viewportPitch);
        }

        double scale = RenderScaling;
        int w = Math.Max(8, (int)(Bounds.Width * scale)), h = Math.Max(8, (int)(Bounds.Height * scale));
        using var target = new RenderTargetBitmap(new PixelSize(w, h), new Avalonia.Vector(96 * scale, 96 * scale));
        target.Render(this);

        var bgra = new byte[w * h * 4];
        var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        target.CopyPixels(new PixelRect(0, 0, w, h), pin.AddrOfPinnedObject(), bgra.Length, w * 4);
        pin.Free();

        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4] = bgra[i * 4 + 2];
            rgba[i * 4 + 1] = bgra[i * 4 + 1];
            rgba[i * 4 + 2] = bgra[i * 4];
            rgba[i * 4 + 3] = 255;
        }

        if (viewport != null && Viewport3D.IsVisible && Viewport3D.TranslatePoint(new Point(0, 0), this) is { } at)
        {
            int x0 = (int)(at.X * scale), y0 = (int)(at.Y * scale);
            for (int y = 0; y < renderer.Height; y++)
            {
                int dy = y0 + y;
                if (dy < 0 || dy >= h) continue;
                for (int x = 0; x < renderer.Width; x++)
                {
                    int dx = x0 + x;
                    if (dx < 0 || dx >= w) continue;
                    int src = y * viewportPitch + x * 4, dst = (dy * w + dx) * 4;
                    rgba[dst] = viewport[src];
                    rgba[dst + 1] = viewport[src + 1];
                    rgba[dst + 2] = viewport[src + 2];
                    rgba[dst + 3] = 255;
                }
            }
        }

        PastePopup(rgba, w, h, scale);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Bench.WritePng(path, w, h, rgba, w * 4);
        // Sidecar: if the viewport did not contribute, say why rather than shipping a blank picture.
        var notes = new List<string>
        {
            $"viewport {renderer.Width}x{renderer.Height} fps {renderer.Fps:0.0} blit {viewport != null}",
            $"status {renderer.Status} phase {renderer.Phase}",
            $"live {live?.StatusLine()}",
            $"box {SimBox.Min} {SimBox.Max}",
            $"control {Viewport3D.Bounds} visible {Viewport3D.IsVisible} panel {ResultsPanel.IsVisible} window {Bounds}",
            $"note {shotNote}",
            $"cloud {renderer.Particles.CloudCount} sprites below {renderer.Particles.StillThreshold:0.00} m/s · rooms still " +
            string.Join(", ", model.Rooms.Select((room, i) => $"{room.Name} " +
                (i < renderer.Particles.RoomStill.Length ? $"{renderer.Particles.RoomStill[i] * 100:0}%" : "-"))),
        };
        File.WriteAllLines(Path.ChangeExtension(path, ".txt"), notes);
        Log.Info($"shot written: {path} ({w}x{h}, viewport blit {viewport != null})");
    }

    string shotNote = "";
    static readonly TimeSpan LogPathShown = TimeSpan.FromSeconds(30);

    /// <summary>THIRD-PARTY-NOTICES.txt sits next to the exe (WindFlow.App.csproj copies it; S5-01).</summary>
    void OnOpenLicences(object? sender, RoutedEventArgs e) => OpenShipped("THIRD-PARTY-NOTICES.txt", "licence notices");

    /// <summary>RA-40: LICENSE.txt (the repo's LICENSE, MIT) sits next to the exe as well (WindFlow.App.csproj copies it).</summary>
    void OnOpenLicence(object? sender, RoutedEventArgs e) => OpenShipped("LICENSE.txt", "licence");

    void OnOpenMethod(object? sender, RoutedEventArgs e) => OpenShipped("how-windflow-works.pdf", "How WindFlow works PDF");
    void OnOpenTechRef(object? sender, RoutedEventArgs e) => OpenShipped("WindFlow-Technical-Reference.pdf", "Technical Reference PDF");

    const string IssuesUrl = "https://github.com/Bijaykars/Windflow/issues";

    void OnContact(object? sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(IssuesUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("could not open the contact page", ex); StatusText.Text = $"contact: {IssuesUrl}"; }
    }

    void OpenShipped(string file, string what)
    {
        string notices = Path.Combine(AppContext.BaseDirectory, file);
        try
        {
            if (!File.Exists(notices)) { StatusText.Text = $"{what} not found: {notices}"; return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(notices) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"could not open the {what}", ex);
            StatusText.Text = $"{what}: {notices}";
        }
    }

    void OnOpenLogFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.Folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("could not open the log folder", ex);
            StatusText.Text = $"log folder: {Log.Folder}";
        }
    }

    /// <summary>--shot: solve live for a few seconds, turn the tools on, take one picture, quit.</summary>
    async Task ShotSequence(string path)
    {
        // The shot window takes foreground focus, so the mouse the user happens to move over it reaches the
        // viewport: that is what dragged the hovered BoxZMax handle and lost this shot its 3.2 m clip extent.
        Viewport3D.Renderer.InputEnabled = false;
        StartLive();
        await Task.Delay(9000);
        SetSliceRanges();
        if (Options.ShotMode == "xray")
        {
            Viewport3D.Renderer.Walls = WallMode.Xray;
            GhostToggle.Content = "X-ray";
        }
        else if (Options.ShotMode == "box")
        {
            // Box on, clipped to the lower storey, with a section cut through it and one handle hovered.
            // The pending reset has to be taken off the table first: OnStatusTick calls ResetBox() as soon as
            // the render thread reports a scene, which lands AFTER this branch when the scene upload is slow
            // and puts the box back to the full model bounds.
            string boxDiagnostic = $"boxreset pending {boxResetPending} scene {Viewport3D.Renderer.CurrentScene != null}";
            boxResetPending = false;
            BoxToggle.IsChecked = true;
            OnBoxToggle(null, null!);
            var (bmin, bmax) = Viewport3D.Renderer.Box;
            if (bmax.X <= bmin.X) { ResetBox(); (bmin, bmax) = Viewport3D.Renderer.Box; }
            Viewport3D.Renderer.SetBox(bmin, new Vector3(bmax.X, bmax.Y, model.GroundZ + 3.2f));
            shotNote = boxDiagnostic + $" · set {Viewport3D.Renderer.Box.Min} {Viewport3D.Renderer.Box.Max}";
            ClipToggle.IsChecked = true;
            OnClipToggle(null, null!);
            SliceVToggle.IsChecked = true;
            OnSliceV(null, null!);
            Viewport3D.Renderer.HoverHandle = Gizmos.BoxZMax;
            Viewport3D.Renderer.Walls = WallMode.Ghost;
        }
        else if (Options.View == "still")
        {
            // The still-air map on its own: no slice over it, so the cloud is the whole picture.
            ApplyView(ParticleView.Still);
            shotNote = "still view";
        }
        else
        {
            SliceHToggle.IsChecked = true;
            OnSliceH(null, null!);
            SetSliceField(1);      // pressure, so the scalar volume path is in the picture
        }
        var biggest = live?.Stats?.Rooms.OrderByDescending(r => r.Volume).FirstOrDefault();
        if (biggest != null)
        {
            int index = IndexOfRoom(biggest.Room.Id);
            Viewport3D.Renderer.SelectedRoom = index;
            var room = model.Rooms[Math.Max(index, 0)];
            float cx = 0, cy = 0;
            var loop = room.Loops[0];
            foreach (var pt in loop) { cx += pt.X; cy += pt.Y; }
            AddProbe(new Vector3(cx / loop.Length, cy / loop.Length, room.ZMin + 1.1f));
        }
        await Task.Delay(6000);    // one analysis pass has to land before the pressure volume exists
        RefreshPanel();
        RefreshProbes();
        await Task.Delay(500);
        await SaveShell(path);
        Close();
    }

    // ---------------------------------------------------------------- status strip

    void OnStatusTick(object? sender, EventArgs e)
    {
        var r = Viewport3D.Renderer;
        ModelText.Text = r.ModelName;
        string particles = r.Particles.Ready && r.Particles.Enabled
            ? $" · {Format(r.Particles.Count)} particles"
            : "";
        string box = "";
        if (r.ShowBox)
        {
            var (bmin, bmax) = r.Box;
            var size = bmax - bmin;
            box = $" · box {size.X:0.0}×{size.Y:0.0}×{size.Z:0.0} m{(r.ClipToBox ? " clipped" : "")}";
        }
        GeometryText.Text = $"{r.Triangles:N0} triangles · {r.OpeningsOpen}/{r.OpeningsTotal} open{particles}{box} · {r.Width}×{r.Height}";
        PerfText.Text = r.Fps > 0 ? $"{r.FrameMs:0.0} ms · {r.Fps:0} fps" : "starting…";
        if (live is { Active: true } or { State: LiveSolver.Mode.Failed })
        {
            UpdateRunButton(live);
            // Basic says what is happening in words; Expert keeps the solver line (backend, cells, MLUPS).
            string liveLine = advancedMode ? live.StatusLine() : BasicStatusLine(live);
            if (!string.IsNullOrEmpty(liveLine)) solverLine = liveLine;
        }
        else if (!locked && !RunButton.IsEnabled)
        {
            RunButton.IsEnabled = true;                     // the session that was "Done" has gone (new model, stop)
            ToolTip.SetTip(RunButton, RunTip);
        }

        if (reportProgress != null) StatusText.Text = reportProgress;
        else if (!string.IsNullOrEmpty(r.Status)) StatusText.Text = r.Status;
        else if (placing == null && DeviceLabel() is { Length: > 0 } deviceLabel)
            StatusText.Text = $"{deviceLabel}  ·  {solverLine}";
        else if (live is { Active: true } && r.HoverRoom >= 0 && RoomLabel(r.HoverRoom) is { } roomLabel)
            StatusText.Text = $"{roomLabel}  ·  {solverLine}";
        else if (live is { Active: true } && r.OpeningAt(r.HoverOpening) is { } liveHover)
            StatusText.Text = $"{liveHover.Name} · {(liveHover.IsOpen ? "open" : "closed")} — click to toggle  ·  {solverLine}";
        else if (live is { Active: true } or { State: LiveSolver.Mode.Failed }) StatusText.Text = solverLine;
        else if (r.OpeningAt(r.HoverOpening) is { } hovered)
            StatusText.Text = $"{hovered.Kind} · {hovered.Name} · {(hovered.IsOpen ? "open" : "closed")} — click to toggle";
        else if (stale && !string.IsNullOrEmpty(solverLine)) StatusText.Text = solverLine + "  ·  STALE, press Run";
        // Any logged error keeps the log path on the strip for a while, so there is something to send.
        if (Log.FilePath.Length > 0 && DateTime.UtcNow - Log.LastErrorUtc < LogPathShown &&
            StatusText.Text?.Contains(Log.FilePath, StringComparison.Ordinal) != true)
            StatusText.Text = $"{StatusText.Text}  ·  error logged: {Log.FilePath}";

        UpdateGovernor();
        UpdatePass();
        UpdateLiveLinkStatus();
        PollHolds();
        PollBoxRejected();
        TrackDeviceDrag();
        if (advancedMode && !locked) TrackBoxDomain();
        UpdateSpongeBand();
        if (thermalShown != live is { ThermalActive: true, Active: true })
        {
            thermalShown = live is { ThermalActive: true, Active: true };
            RefreshThermalState();          // "starting..." becomes "on" when the restarted session lands
        }
        RefreshPanel();
        if (live is { } ls2 && FieldTempToggle.IsEnabled != ls2.TemperatureAvailable)
            FieldTempToggle.IsEnabled = ls2.TemperatureAvailable;
        if (r.Particles.Ready) UpdateLegend();   // both legends track the live p98 and the scalar scale
        if (sliceRangesPending && r.Particles.Ready) { sliceRangesPending = false; SetSliceRanges(); UpdateSliceLegend(); }
        if (boxResetPending && r.CurrentScene != null) { boxResetPending = false; ResetBox(); }

        // The wind arrow follows the inputs, debounced through this tick so typing does not rebuild geometry
        // on every keystroke.
        string windText = $"{WindDirBox.Text}|{WindSpeedBox.Text}";
        if (windText != lastWindText)
        {
            lastWindText = windText;
            windVector = ReadWind();
            if (model.Obstacles.Count > 0)
                r.SetScene(Scene.Build(model, windVector, probes, selectedDevice, placeGhost, Overlay()), frameCamera: false);
            if (live is { Active: true } windLive) windLive.SetWind(windVector);   // debounced by this tick
        }

        // First frame after the adapter is known: an integrated GPU draws half the trail (the ring is still 16
        // long), which is pure fill saved. The particle count itself comes from the startup tier (Tier.cs).
        if (!countAdjusted && !string.IsNullOrEmpty(r.AdapterName))
        {
            countAdjusted = true;
            if (r.AdapterIsIntegrated) r.Particles.DrawnTrail = 8;
        }
        AdjustParticles();
        UpdateParticleLimit();

        if (Options.Live && !liveToggled && DateTime.UtcNow > liveToggleAt)
        {
            // --edit-device replaces the window toggle, so the measured device flow is not mixed with it.
            if (Options.LiveLink) liveToggled = true;          // the fake Revit client does the toggling
            else if (Options.SwapImport) { liveToggled = true; _ = SwapImportStep(); }
            else if (Options.DeviceStress > 0) { liveToggled = true; _ = DeviceStressStep(Options.DeviceStress); }
            else if (Options.EditDevice.Length > 0) { liveToggled = true; _ = EditDeviceStep(Options.EditDevice, Options.EditAssign); }
            else ToggleWindwardWindow();
        }

        // Badge at 6 s: the state the earlier short bench reported, kept so a long run shows both ends.
        if (badgeEarly.Length == 0 && live is { } early && (DateTime.UtcNow - benchStartedAt).TotalSeconds >= 6)
            badgeEarly = Badge(early);

        // The toggle re-voxelises and Core resets the time-average, so the first analysis pass after it is
        // the one that has to read "averaging…": this records whichever branch it actually took.
        if (liveToggled && badgeAfterReset.Length == 0 && live is { Stats: not null } reset)
        {
            if (badgeStatsVersion < 0) badgeStatsVersion = reset.StatsVersion;
            else if (reset.StatsVersion != badgeStatsVersion) badgeAfterReset = Badge(reset);
        }

        if (benchWritten || Options.BenchSeconds <= 0) return;
        if (r.BenchOutput is { } bench)
        {
            benchWritten = true;
            string[]? liveLines = null;
            if (Options.Live && live is { } ls)
            {
                var lines = new List<string>
                {
                    $"live          {ls.Backend} · {ls.Cells / 1000.0:0.#}k cells @ {ls.Dx * 1000:0} mm · {ls.Mlups:0} MLUPS · " +
                    $"field {ls.FieldHz:0.#} Hz · sim t {ls.SimSeconds:0.0} s",
                    $"domain        {(Options.Box is { } b ? $"box {b.Min} -> {b.Max} ({b.Size.X:0.#}×{b.Size.Y:0.#}×{b.Size.Z:0.#} m) · {(ls.Nested ? "nested" : "v1 (box = whole domain)")}" : "whole building + wind-tunnel margin")}",
                    Options.SwapImport ? $"swap          {editNote}"
                        : Options.DeviceStress > 0
                        ? $"stress        {editNote} · {ls.CommandFailures} command failures in the whole run"
                        : Options.EditDevice.Length > 0
                        ? $"edit          {editNote} · before: {editBefore} · applied {ls.LastCommandMs:0} ms after Enter"
                        : $"toggle        {liveToggleName} closed, applied {ls.LastCommandMs:0} ms after the click",
                    $"stats         {(ls.AnalysisOnPauseOnly ? "on pause only" : "background thread, every 5 s")}, {ls.AnalysisMs:0} ms per pass · {ls.Trust} · {ls.RoomLine}",
                    $"badge 6s      {(badgeEarly.Length > 0 ? badgeEarly : "(run shorter than 6 s)")}",
                    $"box audit     ring air {ls.Audit.RingAirFraction * 100:0.#} % · " +
                    $"cuts {(ls.Audit.CutsIndoorAir ? string.Join(", ", ls.Audit.CutRoomNames) : "nothing")} · " +
                    $"{ls.Audit.DroppedRooms} rooms dropped · faces {ls.Audit.FaceReport()}{(ls.BoxNote.Length > 0 ? " · " + ls.BoxNote : "")}",
                    $"badge reset   {(badgeAfterReset.Length > 0 ? badgeAfterReset : "(no pass after the toggle)")}",
                    $"badge end     {Badge(ls)}",
                    $"debug         state={ls.State} bursts={ls.Bursts} publishes={ls.Publishes} pauses={ls.Pauses} last={ls.LastEvent}",
                };
                lines.AddRange(GovernorLines(ls));
                lines.AddRange(NestLines(ls));
                foreach (var w in ls.Stats?.Field.Grid.Warnings ?? [])
                    if (!w.StartsWith("Device ", StringComparison.Ordinal)) lines.Add($"gridwarn      {w}");
                // Openings table from the last analysis pass: the box-versus-whole-building comparison needs
                // the per-opening flows, not just the room ACH.
                foreach (var f in ls.Stats?.Openings ?? [])
                {
                    if (!f.Opening.IsOpen) continue;
                    lines.Add($"opening       {f.Opening.Name,-26} {f.FlowRate:+0.000;-0.000;0.000} m³/s · " +
                              $"{f.MeanSpeed:+0.00;-0.00;0.00} m/s{(ls.FineStats?.Openings.Contains(f) == true ? " · fine" : "")}");
                }
                foreach (var s in model.Sources)
                {
                    var f = ls.Stats?.Sources.FirstOrDefault(x => x.Source.Id == s.Id);
                    lines.Add($"device        {s.Name,-26} {s.Device} · {(s.Enabled ? "on" : "off")} · " +
                              $"set {s.FlowRate:0.000} · measured {(f != null ? MathF.Abs(f.FlowRate) : float.NaN):0.000} m³/s · " +
                              $"duty {(f?.HeatRemovedWatts ?? 0):0} W");
                    // The text the "coarse" tooltip shows, so a headless run can be read without hovering.
                    foreach (var w in ls.Stats?.Field.Grid.Warnings ?? [])
                        if (w.StartsWith($"Device {s.Name}:", StringComparison.Ordinal))
                            lines.Add($"devwarn       {w}");
                }
                foreach (var room in ls.Stats?.Rooms ?? [])
                    lines.Add($"room          {room.Room.Name,-26} {Standards.FreshText(room)} fresh-air ACH · total {room.Ach:0.00} ACH · {room.MeanSpeed:0.00} m/s · {room.Volume:0.0} m³ (voxel {room.VoxelVolume:0.0}, nominal {(room.VolumeIsNominal ? "yes" : "no")}) · " +
                              $"inflow {room.Inflow:0.000} · outflow {room.Outflow:0.000} · net {room.NetFlux:0.000} · exchange {room.InternalExchange:0.000} m³/s · shows \"{Standards.RoomValue(room)}\"" +
                              (ls.FineStats?.Rooms.Contains(room) == true ? " · fine" : ""));
                liveLines = [.. lines];
            }
            Log.Info($"bench done: {bench.FrameMs.Length} frames on {bench.Adapter}");
            Console.Write(Bench.Write(bench, r.ModelName, r.Triangles, Options.BenchSeconds, solverLine,
                $"{bench.Particles:N0} x {ParticleSystem.TrailLength}-point world-space trails [{bench.ParticleInfo}]",
                liveLines));
            Close();
        }
        else if (DateTime.UtcNow > benchDeadline)
        {
            benchWritten = true;
            Log.Error($"bench FAILED: deadline passed · renderer status {r.Status} · {StatusText.Text}");
            Bench.WriteFailure(r.AdapterName, benchStarted ? r.Status : "solver did not finish: " + StatusText.Text);
            Close();
        }
    }

    /// <summary>Headless lines for a nested box: both grids, the throughput, and Core notes on the fine grid.</summary>
    internal static List<string> NestLines(LiveSolver ls, string label = "")
    {
        var lines = new List<string>();
        string Head(string key) => label.Length > 0 ? $"{label,-9} {key,-8}" : $"{key,-13}";
        if (!ls.Nested || ls.FineBox is not { } fb)
        {
            if (ls.BoxModeNote.Length > 0) lines.Add($"{Head("boxmode")} {ls.BoxModeNote}");
            return lines;
        }
        lines.Add($"{Head("nested")} solved box {fb.Min} -> {fb.Max} ({fb.Size.X:0.##}×{fb.Size.Y:0.##}×{fb.Size.Z:0.##} m)");
        lines.Add($"{Head("fine")} {ls.FineCells / 1000.0:0.#}k cells @ {ls.FineDx * 1000:0} mm · {ls.FineMlups:0} MLUPS " +
                  "(Core: fine-cell updates per second, coarse step included)");
        lines.Add($"{Head("coarse")} {ls.Cells / 1000.0:0.#}k cells @ {ls.Dx * 1000:0} mm · {ls.CoarseMlups:0} MLUPS (one coarse Step(1), timed by Core)");
        lines.Add($"{Head("total")} {ls.Mlups:0} MLUPS (coarse + 2 x fine cell updates per coarse step / wall time) · field {ls.FieldHz:0.#} Hz");
        foreach (var n in ls.NestNotes) lines.Add($"{Head("boxnote")} {n}");
        return lines;
    }

    /// <summary>The trust badge exactly as the status strip shows it, plus the averaging window behind it.</summary>
    static string Badge(LiveSolver ls) =>
        $"\"{(ls.Trust.Length == 0 ? "(no analysis pass yet)" : ls.Trust)}\" · " +
        $"averaged {ls.AverageSeconds:0.0}/{ls.AverageTarget:0.0} s of sim time · sim t {ls.SimSeconds:0.0} s";

    /// <summary>Bench helper: shut the largest open window on the windward facade, through the click path.</summary>
    void ToggleWindwardWindow()
    {
        liveToggled = true;
        Opening? best = null;
        foreach (var o in model.Openings)
        {
            if (!o.IsOpen || o.Kind != OpeningKind.Window) continue;
            var n = new Vector2(o.Normal.X, o.Normal.Y);
            if (n.LengthSquared() < 1e-6f || windVector.LengthSquared() < 1e-6f) continue;
            // Genuinely facing into the wind, not a side facade that only ties at zero.
            if (Vector2.Dot(Vector2.Normalize(n), Vector2.Normalize(windVector)) > -0.35f) continue;
            if (best == null || o.Width * o.Height > best.Width * best.Height) best = o;
        }
        if (best == null) return;
        liveToggleName = best.Name;
        ToggleOpening(best);
    }

    /// <summary>Name and ACH of a hovered room, from the last analysis pass.</summary>
    string? RoomLabel(int index)
    {
        if ((uint)index >= (uint)model.Rooms.Count) return null;
        var room = model.Rooms[index];
        var stats = live?.Stats;
        if (stats != null)
            foreach (var r in stats.Rooms)
                if (r.Room.Id == room.Id)
                    return $"{DisplayName(room.Id, room.Name)} · {Standards.AchText(r)} · {r.MeanSpeed:0.00} m/s · {StillFraction(r) * 100:0} % still";
        return DisplayName(room.Id, room.Name);
    }

    static readonly IBrush RoomGood = new SolidColorBrush(Color.FromRgb(0x5A, 0xD1, 0x9A));
    static readonly IBrush RoomWeak = new SolidColorBrush(Color.FromRgb(0xE0, 0xB1, 0x62));
    static readonly IBrush RoomBad = new SolidColorBrush(Color.FromRgb(0xE0, 0x73, 0x6A));
    static readonly IBrush RoomExchange = new SolidColorBrush(Color.FromRgb(0x8A, 0x96, 0xA8));
    static readonly IBrush OpeningOpen = new SolidColorBrush(Color.FromRgb(0x4D, 0xB8, 0xFF));
    static readonly IBrush OpeningShut = new SolidColorBrush(Color.FromRgb(0xC0, 0x45, 0x3F));

    static string Format(int n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M" : $"{n / 1000}k";
}

/// <summary>The user's simulation box. Visual only today; the live solver will voxelise inside it.</summary>
public readonly record struct SimBox(Vector3 Min, Vector3 Max)
{
    public Vector3 Size => Max - Min;
}

/// <summary>One room in the outliner; Storey, Speed, OpeningCount and Verdict feed the inspector, Expanded the caret.</summary>
public sealed record RoomRow(int Index, string Name, string Ach, string Still, string Age, string Flag, string FlagTip,
    IBrush Chip, string Tip, string Sub = "", bool ShowSub = false, int Occupants = 0, bool Editable = false,
    bool Cut = false, bool Selected = false, bool Renaming = false, bool Provisional = false, bool Isolated = false,
    string Storey = "", string Speed = "", int OpeningCount = 0, bool Expanded = false, string Verdict = "")
{
    public bool HasFlag => Flag.Length > 0;
    /// <summary>Stuffy and draughty are problems; uneven and open air are observations.</summary>
    public bool FlagWarm => Flag is "stuffy" or "draughty";
    /// <summary>The air-age column is Expert's (ShowSub is advancedMode); the inspector shows the age in both modes.</summary>
    public bool HasAge => Age.Length > 0 && ShowSub;
    public bool HasOpenings => OpeningCount > 0;
    public string Caret => Expanded ? "▾" : "▸";
}
/// <summary>One opening: a line in the Openings list, or (Child) under its room in the outliner. Added = hand-added, so its size and type are editable.</summary>
public sealed record OpeningRow(int Index, string Name, string Sub, string Flow, IBrush Chip,
    bool Pick, bool Sweepable, bool Cut = false, bool Selected = false, bool Open = true, bool Locked = false,
    bool Blend = false, string BlendTip = "", bool CdEditable = false, string CdText = "", string CdName = "",
    bool Added = false, string WidthText = "", string HeightText = "", string SillText = "", int TypeIndex = 0,
    string RoomName = "", string Storey = "", string Kind = "", bool Child = false)
{
    /// <summary>The row's open/closed switch: knob right and lit when open.</summary>
    public Avalonia.Layout.HorizontalAlignment Knob => Open ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left;
    public string SwitchTip => Locked ? UiTable.LockedWhy
        : Open ? "Open: click to close it. The live solver warm-starts on the new cells." : "Closed: click to open it.";
}
public sealed record CompareRow(string Name, string A, string B, string Delta, IBrush DeltaBrush);
public sealed record ProbeRow(string Name, string Sub);

/// <summary>The particle colour scales, mirrored from the HLSL in Shaders.ParticleSource so the legend and the
/// viewport agree. WindFlow.Viz renders the report plates with the same viridis coefficients (RenderKit.Viridis):
/// keep them byte-identical.</summary>
public static class Palette
{
    static readonly double[][] C =
    [
        [0.2777273272, 0.0054073445, 0.3340998053],
        [0.1050930431, 1.4046135299, 1.3845901626],
        [-0.3308618287, 0.2148475595, 0.0950951630],
        [-4.6342304990, -5.7991009734, -19.3324409563],
        [6.2282699363, 14.1799333668, 56.6905526007],
        [4.7763849977, -13.7451453777, -65.3530326334],
        [-5.4354558559, 4.6458526122, 26.3124352496],
    ];

    public static Color Viridis(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var rgb = new double[3];
        for (int c = 0; c < 3; c++)
        {
            double v = C[6][c];
            for (int i = 5; i >= 0; i--) v = v * t + C[i][c];
            rgb[c] = Math.Clamp(v, 0, 1);
        }
        return Color.FromRgb((byte)(rgb[0] * 255), (byte)(rgb[1] * 255), (byte)(rgb[2] * 255));
    }

    /// <summary>The still-air cloud, mirrored from VSCloud: bright amber at rest (left) to a dim ember at the
    /// threshold (right), where the drawing stops.</summary>
    public static Color Still(double t)
    {
        double s = 1 - Math.Clamp(t, 0, 1), k = 0.30 + 0.70 * s;
        return Color.FromRgb((byte)(Math.Min(1, (0.62 + 0.38 * s) * (0.55 + 0.45 * k)) * 255),
                             (byte)(Math.Min(1, (0.26 + 0.46 * s) * (0.55 + 0.45 * k)) * 255),
                             (byte)(Math.Min(1, (0.06 + 0.24 * s) * (0.55 + 0.45 * k)) * 255));
    }

    public static IBrush SpeedBrush() => Ramp(t => Viridis(0.20 + 0.80 * t));

    public static IBrush StillBrush() => Ramp(Still);


    /// <summary>Pressure and temperature: the same diverging ramp the slice shader uses.</summary>
    public static IBrush DivergingBrush() => Ramp(t =>
    {
        double x = t * 2 - 1;
        double[] cold = [0.20, 0.52, 0.92], mid = [0.86, 0.88, 0.92], warm = [0.94, 0.42, 0.30];
        var to = x < 0 ? cold : warm;
        double k = Math.Abs(x);
        return Color.FromRgb((byte)((mid[0] + (to[0] - mid[0]) * k) * 255),
                             (byte)((mid[1] + (to[1] - mid[1]) * k) * 255),
                             (byte)((mid[2] + (to[2] - mid[2]) * k) * 255));
    });

    static IBrush Ramp(Func<double, Color> map)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(1, 0, Avalonia.RelativeUnit.Relative),
        };
        for (int i = 0; i <= 16; i++) brush.GradientStops.Add(new GradientStop(map(i / 16.0), i / 16.0));
        return brush;
    }
}

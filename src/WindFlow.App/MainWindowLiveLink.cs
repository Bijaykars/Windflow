using Avalonia.Controls;
using Avalonia.Interactivity;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>The Revit live link (docs/LIVELINK.md) and the GPU budget governor (PLAN_V2 2.1): the window side of
/// <see cref="LiveLinkServer"/> and of <see cref="LiveSolver.GpuTargetPercent"/>.</summary>
public partial class MainWindow
{
    LiveLinkServer? liveLink;
    bool liveLinkModel;          // the model on screen arrived over the live link
    bool gpuDirty;               // the slider moved since the last settings save
    TimeSpan benchCpuStart;      // process CPU time when a live bench started

    // ---------------------------------------------------------------- live link

    void OnLiveLink(object? sender, RoutedEventArgs e)
    {
        settings.LiveLink = LiveLinkToggle.IsChecked == true;
        settings.Save();
        if (settings.LiveLink) StartLiveLink();
        else StopLiveLink();
    }

    void StartLiveLink()
    {
        liveLink ??= new LiveLinkServer { Apply = ApplyLiveLink };
        liveLink.Start();
        LiveLinkToggle.IsChecked = true;
    }

    void StopLiveLink()
    {
        liveLink?.Stop();
        LiveLinkToggle.IsChecked = false;
    }

    /// <summary>UI thread. A model goes through the same ShowModel a file open uses; an opening through the
    /// same ToggleOpening a click uses. Returns the ack's error text, or null.</summary>
    string? ApplyLiveLink(LiveLinkServer.Message message)
    {
        switch (message.Type)
        {
            case "model" when locked:
            case "opening" when locked:
                StatusText.Text = "Revit sent a change, but the result is locked: unlock to follow Revit again";
                return "the WindFlow result is locked (viewing only); unlock it in WindFlow to apply changes";
            case "model":
            {
                var next = message.Model!;
                // Devices placed in the app are not in Revit: keep them across Revit's re-sends of the same
                // document, unless Revit now carries something with the same id.
                int kept = 0;
                if (liveLinkModel)
                    foreach (var device in model.Sources)
                        if (device.Id.StartsWith("dev-", StringComparison.Ordinal) && next.Sources.All(s => s.Id != device.Id))
                        {
                            next.Sources.Add(device);
                            kept++;
                        }
                bool hadModel = model.Obstacles.Count > 0;
                ShowModel(next, frameCamera: !hadModel, liveResend: liveLinkModel);
                liveLinkModel = true;
                liveLink!.Doc = message.Doc.Length > 0 ? message.Doc : next.Name;
                if (kept > 0) Log.Info($"live link: kept {kept} app-added device(s) on the new model");
                StatusText.Text = $"Revit model {liveLink.Doc} loaded{(live is { Active: true } ? " — solver restarted" : "")}";
                return null;
            }
            case "opening":
            {
                var opening = model.Openings.FirstOrDefault(o => o.Id == message.OpeningId);
                if (opening == null) return $"no opening with id {message.OpeningId} in the current model";
                if (opening.IsOpen != message.IsOpen) ToggleOpening(opening);
                return null;
            }
            case "close":
                liveLink!.Status = "live link: Revit closed the document";
                StatusText.Text = "Revit closed the document — the model stays as it was";
                return null;
            default:
                return null;
        }
    }

    /// <summary>Asks Revit to select an element the user picked from a panel row. No-op when nothing is connected.</summary>
    void SendSelect(string id)
    {
        if (liveLink is { Connected: true } link && id.Length > 0) link.SendSelect([id]);
    }

    void UpdateLiveLinkStatus()
    {
        var link = liveLink;
        bool on = link is { Running: true } || link?.Status.Contains("closed", StringComparison.Ordinal) == true;
        LiveLinkText.IsVisible = on;
        if (!on) return;
        LiveLinkText.Text = link!.Connected && !link.Status.Contains("closed", StringComparison.Ordinal)
            ? $"·  live link: connected{(link.Doc.Length > 0 ? " · " + link.Doc : "")}"
            : "·  " + link.Status;
    }

    // ---------------------------------------------------------------- GPU budget

    void InitGovernor()
    {
        int percent = Options.GpuUse > 0 ? Options.GpuUse : Math.Clamp(settings.GpuUse, 25, 100);
        GpuSlider.Value = percent;               // fires OnGpuUse
        ApplyGpuUse(percent, save: false);
    }

    void OnGpuUse(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (GpuText == null) return;              // fires during InitializeComponent
        ApplyGpuUse((int)Math.Round(e.NewValue), save: true);
    }

    void ApplyGpuUse(int percent, bool save)
    {
        GpuText.Text = $"{percent} %";
        if (live != null) live.GpuTargetPercent = percent;
        if (save && Options.GpuUse <= 0 && settings.GpuUse != percent)
        {
            settings.GpuUse = percent;
            gpuDirty = true;                      // written by the status tick: a drag is dozens of events
        }
    }

    /// <summary>Status tick: the viewport frame cap and the measured-duty readout.</summary>
    void UpdateGovernor()
    {
        if (gpuDirty) { gpuDirty = false; settings.Save(); }
        var r = Viewport3D.Renderer;
        bool headless = Options.BenchSeconds > 0 || !string.IsNullOrEmpty(Options.Shot);
        // Headless runs ignore focus: the user is working in another window while they run.
        bool background = WindowState == WindowState.Minimized || (!IsActive && !headless);
        bool solving = live is { State: LiveSolver.Mode.Running };
        float idle = StartupTier.Chosen?.FrameCap ?? 60;      // the startup tier's ceiling
        // Locked: nothing is solving, so the only limit left is the display (Present waits for vsync).
        float cap = background ? 5 : locked ? 0 : solving ? Math.Min(30, idle) : idle;
        // Video export runs on its own timeline; the pre-governor fps benches measure the uncapped renderer.
        if (r.Recorder != null || (Options.BenchSeconds > 0 && Options.GpuUse <= 0)) cap = 0;
        r.MaxFps = cap;

        if (live is not { } ls || !ls.Active) { GpuNote.Text = "applies while solving"; return; }
        if (locked) { GpuNote.Text = "result locked: the solver is stopped, the GPU only draws"; return; }
        if (ls.State == LiveSolver.Mode.Paused) { GpuNote.Text = "paused — the GPU is idle"; return; }
        if (ls.StepsPerSecond <= 0) { GpuNote.Text = "measuring…"; return; }
        double eta = ls.SecondsToConverged();
        string converged = eta == 0 ? "first average ready"
            : double.IsNaN(eta) ? "first average in —"
            : eta < 90 ? $"first average in ~{eta:0} s"
            : $"first average in ~{eta / 60:0} min";   // S6-12: one flow-through window, no convergence test
        GpuNote.Text = $"measured {ls.Duty * 100:0} % · {ls.StepsPerSecond:N0} steps/s · {converged}";
    }

    // ---------------------------------------------------------------- startup tier + particle budget

    /// <summary>UI thread, once the probe lands: the particle count (unless --particles pinned it) and the
    /// SETTINGS readout. The cell budget is read by the solver thread itself (StartupTier.CellBudget).</summary>
    void ApplyTier(TierChoice tier)
    {
        TierText.Text = $"{tier.Name} · {tier.TargetCells / 1e6:0.#}M cells · {tier.Particles / 1000}k particles · {tier.FrameCap:0} fps";
        TierNote.Text = $"measured {tier.ProbeMlups:N0} MLUPS · {tier.Adapter}, {tier.VramGb:0.#} GB";
        ShowAccurateSetting();
        ToolTip.SetTip(TierRow, "Picked once per launch from a two-second solver run and the graphics card's own " +
            $"memory: {tier.Source}. Particles then follow the frame rate: fewer when the viewport cannot hold its " +
            "frame cap for 3 s, more again after 10 s of headroom.");
        particleAuto = new ParticleAuto(tier.Particles);
        if (Options.Particles > 0) { TierText.Text += " (particles fixed by --particles)"; return; }
        if (manualParticles > 0 || settings.ParticleCount > 0) return;      // pinned in Settings: auto stays off
        SetParticleCount(particleAuto.Count, $"startup tier {tier.Name}");
    }

    void SetParticleCount(int count, string why)
    {
        if (count == particleCount) return;
        Log.Info($"particles: {particleCount / 1000}k -> {count / 1000}k ({why})");
        particleCount = count;
        var particles = Viewport3D.Renderer.Particles;
        if (particles.Ready || live is { Active: true }) particles.RequestCount(count);
    }

    DateTime lastAutoSample = DateTime.MinValue;

    /// <summary>Status tick: feed the viewport fps to the particle auto-adjust. Only while particles are on
    /// screen, the window is not recording, and a frame cap is set (an uncapped bench has no target).</summary>
    void AdjustParticles()
    {
        var now = DateTime.UtcNow;
        double dt = lastAutoSample == DateTime.MinValue ? 0 : Math.Min((now - lastAutoSample).TotalSeconds, 1);
        lastAutoSample = now;
        var r = Viewport3D.Renderer;
        if (particleAuto == null || manualParticles > 0 || Options.Particles > 0) return;
        if (!r.Particles.Ready || !r.Particles.Enabled || r.Recorder != null || dt <= 0) return;
        float fps = Options.FpsInject > 0 ? Options.FpsInject : r.Fps;
        // Locked, the renderer runs at vsync (cap 0): judge against the display refresh instead.
        int next = particleAuto.Update(dt, fps, locked ? MonitorHz : r.MaxFps, out string reason);
        if (next > 0) SetParticleCount(next, reason + (Options.FpsInject > 0 ? " [--fps-inject]" : ""));
    }

    /// <summary>Bench lines for the governor and the live link.</summary>
    List<string> GovernorLines(LiveSolver ls)
    {
        var lines = new List<string>
        {
            $"gpu budget    target {ls.GpuTargetPercent} % · measured duty {ls.Duty * 100:0.0} % · {ls.StepsPerSecond:0} steps/s · " +
            $"burst {ls.BurstMs:0.0} ms · solver thread CPU {ls.SolverCpu * 100:0} % of a core · " +
            $"viewport cap {Viewport3D.Renderer.MaxFps:0} fps · {GpuNote.Text}",
        };
        double wall = (DateTime.UtcNow - benchStartedAt).TotalSeconds;
        double cpu = (System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - benchCpuStart).TotalSeconds;
        if (wall > 0 && wall < 1e5)
            lines.Add($"app cpu       {cpu / wall * 100:0} % of one core ({cpu / wall / System.Environment.ProcessorCount * 100:0.0} % of " +
                      $"{System.Environment.ProcessorCount} logical CPUs) over the {wall:0} s since the solver started · viewport {Viewport3D.Renderer.Fps:0} fps");
        if (liveLink != null)
            lines.Add($"live link     {liveLink.Status} · connected {liveLink.Connected} · doc \"{liveLink.Doc}\" · " +
                      $"received {liveLink.Received} · acked {liveLink.Acked} · errors {liveLink.Errors} · {liveLink.Counts()}");
        return lines;
    }
}

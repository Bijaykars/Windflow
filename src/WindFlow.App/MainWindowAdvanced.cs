using System.Numerics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WindFlow.App.Viewport;
using WindFlow.Core;
using WindFlow.Report;

namespace WindFlow.App;

/// <summary>Advanced-mode workflows: hold/compare, the opening sweep, the annual study and the PDF report.
/// All four pause the live solver first — there is one GPU and one solver session, and a batch run must not
/// fight the live one for it. The long jobs share one cancellation token and the <see cref="jobBusy"/> flag,
/// so only one runs at a time.</summary>
public partial class MainWindow
{
    SimulationResult? holdA, holdB;
    int holdVersion = -1;
    bool comparing, compareShown;

    List<SweepEntry>? sweepEntries;
    List<string> sweepIds = [];
    readonly HashSet<int> sweepPicks = [];

    List<WindBin>? windBins;

    CancellationTokenSource? jobCancel;
    bool jobBusy;

    bool thermalWanted, rankByRoom;

    static RunOptions JobOptions() => new() { Quality = Quality.Preview, Backend = Backend.Auto, OpeningModel = openingModelSetting, DomainMode = domainModeSetting };   // RZ-06: the Settings wind domain too
    /// <summary>The Settings wind domain (6c), for the background jobs as well as the live solver.</summary>
    internal static DomainMode domainModeSetting = DomainMode.Compact;

    /// <summary>Site / weather-station wind speed at 10 m (P13b-06): the station is open terrain (z0 0.05 m, assumed; the EPW
    /// does not say), the site is the terrain the profile exponent stands for.</summary>
    static float EpwSiteFactor() => WindRose.SiteSpeedFactor(HighAccuracy.Z0FromExponent(JobOptions().WindProfileExponent));

    // ---------------------------------------------------------------- thermal and the environment

    void OnThermal(object? sender, RoutedEventArgs e) => ApplyThermal(ThermalToggle.IsChecked == true);

    /// <summary>RunOptions.Thermal is read when the session opens, so this goes through the command path and
    /// the flow starts again from rest. Expect roughly half the MLUPS: the D3Q7 temperature lattice is a
    /// second lattice plus a buoyancy force.</summary>
    void ApplyThermal(bool on)
    {
        thermalWanted = on;
        ThermalToggle.IsChecked = on;
        CommitEnvironment(restart: false);          // the boxes are the source of truth for model.Env
        if (!on && Viewport3D.Renderer.Particles.SliceField == 2) SetSliceField(0);
        if (live is { Active: true } solver)
        {
            solver.Reconfigure(on, model);
            StatusText.Text = on
                ? "thermal on — restarting with the temperature lattice, the flow develops again from rest"
                : "thermal off — restarting the isothermal solve";
        }
        else StatusText.Text = on ? "thermal on — press Run" : "thermal off";
        RefreshThermalState();
    }

    void RefreshThermalState()
    {
        bool active = live is { ThermalActive: true, Active: true };
        ThermalState.Text = !thermalWanted ? "isothermal" : active ? "thermal on" : "thermal starting…";
        // One row while thermal is off; the note only when it is on and there is something to know.
        ThermalNote.IsVisible = thermalWanted;
        HoldIndoorToggle.IsVisible = thermalWanted;
        ThermalNote.Text = "Beta: heat transport is checked against textbook cases, not yet against measured rooms. Occupants are " +
                           "70 W each and the indoor/outdoor difference drives buoyancy. " +
                           (HoldIndoorToggle.IsChecked == true
                               ? "Each room is held at the indoor temperature by an ideal heater/cooler; its duty (W, + heating) is in the room rows and the report. Walls are adiabatic (no U-values). "
                               : "Free-running: the indoor temperature is only the starting air temperature; nothing holds it, so rooms drift toward outdoor air plus occupant gains during the run. ") +
                           "The solver steps more slowly with the second lattice.";
        // The wall model is isothermal only (Core ignores it on a thermal run): greyed out, not silently dropped.
        WallModelToggle.IsEnabled = !thermalWanted;
        ShowAccurateControls();                      // the fine box is isothermal only too
    }

    /// <summary>6j (P14-01): RunOptions.HoldIndoorTemperature is read when a session opens, like Thermal, so a change restarts a thermal
    /// run through Reconfigure. On by default (not persisted).</summary>
    void OnHoldIndoor(object? sender, RoutedEventArgs e)
    {
        bool on = HoldIndoorToggle.IsChecked == true;
        if (live is { } solver)
        {
            solver.HoldIndoorWanted = on;
            Log.Info($"hold indoor temperature: {on}");
            if (solver.Active && thermalWanted)
            {
                solver.Reconfigure(true, model);
                StatusText.Text = on ? "holding the indoor temperature — restarting the thermal solve" : "free-running — restarting the thermal solve";
            }
        }
        RefreshThermalState();
    }

    // ---------------------------------------------------------------- wall model (Expert, Settings, experimental)

    /// <summary>RunOptions.WallModel is read when a session opens, like Thermal, so this goes through Reconfigure and the flow
    /// starts again from rest. Off by default on every launch (not persisted): experimental.</summary>
    void OnWallModel(object? sender, RoutedEventArgs e)
    {
        bool on = WallModelToggle.IsChecked == true;
        if (live is not { } solver) return;
        solver.WallModelWanted = on ? WallModel.WernerWengle : WallModel.None;
        Log.Info($"wall model: {solver.WallModelWanted}");
        if (solver.Active)
        {
            solver.Reconfigure(thermalWanted, model);
            StatusText.Text = on ? "wall model on (experimental) — restarting the isothermal solve" : "wall model off — restarting the solve";
        }
        else StatusText.Text = on ? "wall model on (experimental) — press Run" : "wall model off";
    }

    /// <summary>6c: RunOptions.DomainMode (Compact by default, Full = COST 732 margins) is read when a grid is built, so this goes
    /// through Reconfigure like the wall model. Not persisted.</summary>
    void OnDomainMode(object? sender, RoutedEventArgs e)
    {
        bool full = DomainModeToggle.IsChecked == true;
        domainModeSetting = full ? DomainMode.Full : DomainMode.Compact;
        if (live is not { } solver) return;
        solver.DomainModeWanted = full ? DomainMode.Full : DomainMode.Compact;
        Log.Info($"wind domain: {solver.DomainModeWanted}");
        if (solver.Active)
        {
            solver.Reconfigure(thermalWanted, model);
            StatusText.Text = full ? "full wind domain — rebuilding the grid and restarting the solve" : "compact wind domain — restarting the solve";
        }
        else StatusText.Text = full ? "full wind domain — press Run" : "compact wind domain";
    }

    void OnEnvironment(object? sender, RoutedEventArgs e) => CommitEnvironment(restart: true);

    void OnEnvironmentKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitEnvironment(restart: true);
    }

    void CommitEnvironment(bool restart)
    {
        // S3-08: a rejected box is marked and the model keeps its last valid temperature.
        float indoor = model.Env.IndoorTemperature, outdoor = model.Env.OutdoorTemperature;
        if (NumInput.Check(IndoorBox, "Indoor temperature", -30, 60, " °C", out double inT)) indoor = (float)inT;
        if (NumInput.Check(OutdoorBox, "Outdoor temperature", -40, 60, " °C", out double outT)) outdoor = (float)outT;
        if (!IndoorBox.Classes.Contains("invalid")) IndoorBox.Text = indoor.ToString("0.#");
        if (!OutdoorBox.Classes.Contains("invalid")) OutdoorBox.Text = outdoor.ToString("0.#");
        bool changed = MathF.Abs(model.Env.IndoorTemperature - indoor) > 0.01f ||
                       MathF.Abs(model.Env.OutdoorTemperature - outdoor) > 0.01f;
        model.Env.IndoorTemperature = indoor;
        model.Env.OutdoorTemperature = outdoor;
        if (!changed || !restart) return;
        if (thermalWanted && live is { Active: true } solver)
        {
            solver.Reconfigure(true, model);
            StatusText.Text = $"environment {indoor:0.#} / {outdoor:0.#} °C — restarting the thermal solve";
        }
        else StatusText.Text = $"environment {indoor:0.#} / {outdoor:0.#} °C — applied when Thermal is on";
    }

    void OnOccupants(object? sender, RoutedEventArgs e) => CommitOccupants(sender);

    void OnOccupantsKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitOccupants(sender);
    }

    void CommitOccupants(object? sender)
    {
        if (sender is not TextBox box || box.Tag is not int index) return;
        if ((uint)index >= (uint)model.Rooms.Count) return;
        var room = model.Rooms[index];
        int people = room.Occupants;
        if (NumInput.Check(box, "People", 0, 1000, "", out double typed)) people = (int)Math.Round(typed);   // S3-08
        if (!box.Classes.Contains("invalid")) box.Text = people.ToString();
        if (people == room.Occupants) return;
        room.Occupants = people;
        statsVersion = -1;
        if (thermalWanted && live is { Active: true } solver)
        {
            solver.Reconfigure(true, model);
            StatusText.Text = $"{room.Name}: {people} people ({people * 70} W) — restarting the thermal solve";
        }
        else StatusText.Text = $"{room.Name}: {people} people — 70 W each once Thermal is on";
    }

    // ---------------------------------------------------------------- hold A / hold B / compare

    void OnHoldA(object? sender, RoutedEventArgs e) => Hold(0);

    void OnHoldB(object? sender, RoutedEventArgs e) => Hold(1);

    /// <summary>The snapshot itself happens on the solver thread (only it may call ToField); this just asks.</summary>
    void Hold(int slot)
    {
        if (live is not { Active: true } solver)
        {
            CompareNote.Text = "Press Run first: holding freezes a live state.";
            return;
        }
        solver.Hold(slot);
        // Core throws the average away if it is younger than a flow-through, so a hold can take a few
        // seconds of simulated time before it lands. Say so rather than looking stuck.
        StatusText.Text = solver.AverageSeconds < solver.AverageTarget
            ? $"holding {(slot == 0 ? "A" : "B")} — averaging a clean {solver.AverageTarget:0.0} s window first…"
            : $"holding {(slot == 0 ? "A" : "B")} — averaging the live field…";
    }

    /// <summary>Polled from the status tick: the solver thread publishes holds through HoldVersion.</summary>
    void PollHolds()
    {
        if (live is not { } solver || solver.HoldVersion == holdVersion) return;
        holdVersion = solver.HoldVersion;
        holdA = solver.HoldA;
        holdB = solver.HoldB;
        RefreshCompareState();
        StatusText.Text = solver.HoldError.Length > 0 ? "hold failed: " + solver.HoldError : "held.";
    }

    void RefreshCompareState()
    {
        CompareState.Text = $"A {(holdA == null ? "—" : "held")} · B {(holdB == null ? "—" : "held")}";
        bool pair = holdA is not null && holdB is not null;
        CompareToggle.IsEnabled = pair;
        if (live is { } solver && solver.HoldError.Length > 0)
        {
            CompareNote.Text = "The snapshot failed: " + solver.HoldError;
            return;
        }
        if (!pair)
        {
            CompareNote.Text = holdA == null && holdB == null
                ? "Hold A, change something, hold B, then Compare."
                : "One state held. Change a window or the wind, let it settle, then hold the other.";
            return;
        }
        if (!Advanced.SameGrid(holdA!, holdB!))
        {
            CompareToggle.IsEnabled = false;
            CompareNote.Text = Advanced.GridMismatch(holdA!, holdB!);
            return;
        }
        var g = holdA!.Field.Grid;
        CompareNote.Text = comparing
            ? "B − A: blue where B is slower, red where it is faster. The tables read deltas."
            : $"Ready: both on {g.Nx}×{g.Ny}×{g.Nz} cells at {g.Dx * 1000:0} mm.";
    }

    async void OnCompare(object? sender, RoutedEventArgs e)
    {
        if (CompareToggle.IsChecked != true) { ExitCompare(); return; }
        if (holdA is not { } a || holdB is not { } b)
        {
            CompareToggle.IsChecked = false;
            RefreshCompareState();
            return;
        }
        if (!Advanced.SameGrid(a, b))
        {
            CompareToggle.IsChecked = false;
            StatusText.Text = "cannot compare: the two holds are on different grids";
            RefreshCompareState();
            return;
        }

        comparing = true;
        compareShown = false;
        // Freeze the picture and stop the live publisher from overwriting the difference volume.
        if (live is { Active: true } solver) { solver.SetPaused(true); solver.ScalarWanted = 0; }
        StatusText.Text = "building the difference field…";

        var built = await Task.Run(() =>
        {
            var volume = FieldVolume.Build(b);                       // B is what the particles show
            var diff = Advanced.SpeedDifference(a, b);               // |u(B)| − |u(A)| per cell
            var scalars = new Half[volume.Nx * volume.Ny * volume.Nz];
            volume.DownsampleScalar(b.Field.Grid, diff, scalars, out float p98);
            return (volume, scalars, p98);
        });

        var particles = Viewport3D.Renderer.Particles;
        particles.SetField(built.volume);
        particles.SetScalarVolume(built.scalars, built.volume.Nx, built.volume.Ny, built.volume.Nz,
            MathF.Max(built.p98, 0.05f));
        particles.SliceField = SliceFieldDifference;                 // the diverging ramp, on the difference volume
        if (SliceHToggle.IsChecked != true)
        {
            SliceHToggle.IsChecked = true;
            OnSliceH(null, null!);
        }
        FieldSpeedToggle.IsChecked = false;
        FieldPressureToggle.IsChecked = false;
        FieldTempToggle.IsChecked = false;
        UpdateSliceLegend();
        RefreshCompareState();
        RefreshCompareTables();
        compareShown = true;
        StatusText.Text = $"compare: B − A · ±{MathF.Max(built.p98, 0.05f):0.00} m/s on the slice · Compare again to return to live";
    }

    /// <summary>Slice field 3: the compare difference. 0/1/2 are speed/pressure/temperature and come from the
    /// live solver; 3 is uploaded once by <see cref="OnCompare"/> and never republished.</summary>
    const int SliceFieldDifference = 3;

    void ExitCompare()
    {
        if (!comparing) return;
        comparing = false;
        compareShown = false;
        CompareToggle.IsChecked = false;
        CompareList.ItemsSource = null;
        CompareHead.IsVisible = false;
        SetSliceField(FieldPressureToggle.IsChecked == true ? 1 : FieldTempToggle.IsChecked == true ? 2 : 0);
        if (live is { Active: true } solver) solver.SetPaused(false);
        RefreshCompareState();
        StatusText.Text = "back to live";
    }

    /// <summary>Compare: one row per room, A, B and B − A in air changes per hour, in the Studies tab. The live
    /// Results tables keep showing the (paused) live state.</summary>
    void RefreshCompareTables()
    {
        if (holdA is not { } a || holdB is not { } b) return;
        var bRooms = new Dictionary<string, RoomResult>();
        foreach (var r in b.Rooms) bRooms[r.Room.Id] = r;
        var rows = new List<CompareRow>();
        foreach (var r in a.Rooms.OrderBy(x => RoomRank(x.Room.Id)))
        {
            if (!bRooms.TryGetValue(r.Room.Id, out var rb)) continue;
            // Fresh-air ACH on both sides; a side whose air age is not solved shows a dash and no verdict (never the total exchange).
            float d = rb.FreshAch - r.FreshAch;
            bool known = !float.IsNaN(d);
            rows.Add(new CompareRow(DisplayName(r.Room.Id, r.Room.Name), Standards.FreshText(r), Standards.FreshText(rb),
                known ? $"{d:+0.0;-0.0;0.0}" : "—", !known ? RoomExchange : d > 0.2f ? RoomGood : d < -0.2f ? RoomBad : RoomExchange));
        }
        CompareList.ItemsSource = rows;
        CompareHead.IsVisible = rows.Count > 0;
    }

    // ---------------------------------------------------------------- opening sweep

    void OnOpeningPick(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || box.Tag is not int index) return;
        if (sweepPicks.Contains(index)) sweepPicks.Remove(index);
        else if (sweepPicks.Count >= OpeningSweep.MaxCandidates)
        {
            box.IsChecked = false;
            SweepHint.Text = $"At most {OpeningSweep.MaxCandidates} openings: that is 2^8 = 256 runs already.";
            return;
        }
        else sweepPicks.Add(index);
        UpdateSweepHint();
    }

    /// <summary>Only the tick count, so it never overwrites the ranking note a finished sweep left behind.</summary>
    void UpdateSweepHint()
    {
        int n = sweepPicks.Count;
        SweepButton.IsEnabled = n > 0 && !jobBusy;
        if (sweepEntries is not null) return;
        SweepHint.Text = n == 0
            ? "Tick up to 8 openings above, then Sweep."
            : $"{n} ticked · {1 << n} combinations to solve at Preview quality.";
    }

    void OnRankOverall(object? sender, RoutedEventArgs e) => SetRank(false);

    void OnRankRoom(object? sender, RoutedEventArgs e) => SetRank(true);

    /// <summary>Core solves and returns the list ordered by the overall ACH of the analysed rooms, which is why
    /// the best single room is often not first. Re-sorting by the target room is pure client-side work on the
    /// same runs.</summary>
    void SetRank(bool byRoom)
    {
        rankByRoom = byRoom;
        RankOverallToggle.IsChecked = !byRoom;
        RankRoomToggle.IsChecked = byRoom;
        var target = TargetRoom();
        RankNote.Text = byRoom
            ? $"Re-sorted by {target?.Name ?? "the selected room"}; Core solved and ranked by the building's fresh-air rate."
            : $"Ranked by sealed volume first (less is better), then by the building's fresh-air rate (3600 / mean air age over {sweepEntries?.FirstOrDefault()?.VolumeText ?? "all indoor volume"}).";   // RW-03
        if (sweepEntries is not null) ShowSweep(sweepEntries);
    }

    void OnSweep(object? sender, RoutedEventArgs e) => _ = RunSweep();

    void OnJobCancel(object? sender, RoutedEventArgs e) => jobCancel?.Cancel();

    async Task RunSweep()
    {
        if (jobBusy) return;
        var ids = new List<string>();
        foreach (int i in sweepPicks.OrderBy(i => i))
            if ((uint)i < (uint)model.Openings.Count) ids.Add(model.Openings[i].Id);
        if (ids.Count == 0) { SweepHint.Text = "Tick the openings to try first."; return; }

        // OpeningSweep flips Opening.IsOpen on the model it is given, from this background task: hand it a
        // copy, never the UI's own objects. Its results are keyed by opening Id, which the copy keeps.
        var building = LiveSolver.Snapshot(model);
        var wind = windVector;
        var options = JobOptions();
        BeginJob();
        SweepProgress.IsVisible = true;
        SweepProgress.Value = 0;
        SweepCancelButton.IsVisible = true;
        SweepNote.Text = $"{1 << ids.Count} runs";
        StatusText.Text = $"sweeping {1 << ids.Count} combinations…";
        try
        {
            var progress = new Progress<double>(v =>
            {
                SweepProgress.Value = Math.Clamp(v, 0, 1) * 100;
                StatusText.Text = $"sweeping… {v * 100:0} % of {1 << ids.Count} runs";
            });
            var entries = await Task.Run(
                () => OpeningSweep.Run(building, wind, ids, options, progress, jobCancel!.Token), jobCancel!.Token);
            sweepEntries = entries;
            sweepIds = ids;
            ShowSweep(entries);
            StatusText.Text = entries.Count > 0
                ? $"sweep done · best {entries[0].TotalAch:0.#} fresh-air ACH for the building · click a row to apply it"
                : "sweep returned nothing";
        }
        catch (OperationCanceledException) { StatusText.Text = "sweep cancelled"; }
        catch (Exception ex) { StatusText.Text = "sweep failed: " + ex.Message; }
        finally
        {
            SweepProgress.IsVisible = false;
            SweepCancelButton.IsVisible = false;
            EndJob();
        }
    }

    /// <summary>The room the ranking is about: the selected room, else the biggest one the solver knows.</summary>
    Room? TargetRoom()
    {
        int selected = Viewport3D.Renderer.SelectedRoom;
        if ((uint)selected < (uint)model.Rooms.Count) return model.Rooms[selected];
        RoomResult? biggest = null;
        foreach (var r in live?.Stats?.Rooms ?? [])
            if (biggest == null || r.Volume > biggest.Volume) biggest = r;
        // Stats hold the solver's snapshot of the room: hand back the UI's own object, which is the one to edit.
        return (biggest != null ? model.Rooms.FirstOrDefault(x => x.Id == biggest.Room.Id) : null) ?? model.Rooms.FirstOrDefault();
    }

    void ShowSweep(List<SweepEntry> entries)
    {
        var target = TargetRoom();
        var ranked = rankByRoom && target != null
            ? entries.OrderByDescending(x => x.RoomAch.TryGetValue(target.Id, out float a) ? a : float.MinValue).ToList()
            : entries;
        var rows = new List<SweepRow>();
        for (int i = 0; i < ranked.Count && i < 10; i++)
        {
            var entry = ranked[i];
            string chips = string.Concat(entry.Open.Select(o => o ? "●" : "○"));
            string targetAch = target != null && entry.RoomAch.TryGetValue(target.Id, out float ach)
                ? $"{ach:0.0}" : "—";
            // Core keeps the flow field of the best three entries only, so mean speed is known for those.
            string speed = entry.Result is { } kept ? $"{MeanSpeed(kept):0.00} m/s" : "no field kept";
            var detail = new List<string>();
            for (int k = 0; k < sweepIds.Count && k < entry.Open.Length; k++)
            {
                var opening = model.Openings.FirstOrDefault(o => o.Id == sweepIds[k]);
                detail.Add($"{opening?.Name ?? sweepIds[k]}: {(entry.Open[k] ? "open" : "closed")}");
            }
            rows.Add(new SweepRow(entries.IndexOf(entry), chips,
                $"{i + 1}. {(target != null ? target.Name : "whole building")} {targetAch} fresh-air ACH",
                $"{speed} · {entry.TotalAch:0.#} fresh-air ACH, building{(entry.SealedVolume > 0f ? $", over {entry.VolumeText}" : "")}",
                $"{entry.TotalAch:0.#} ACH",
                string.Join("\n", detail) + "\nClick to apply this combination to the model."));
        }
        SweepList.ItemsSource = rows;
        SweepNote.Text = $"{entries.Count} runs · top {rows.Count}";
        SweepHint.Text = target != null
            ? $"The ACH in each row is {target.Name}'s fresh-air ACH (3600 / mean air age); the number on the right is the building's fresh-air rate (3600 / the mean air age over {ranked.FirstOrDefault()?.VolumeText ?? "all indoor volume"}), which Core ranks by after the sealed volume (less is better)."
            : $"The number on the right is the building's fresh-air rate (3600 / the mean air age over {ranked.FirstOrDefault()?.VolumeText ?? "all indoor volume"}), which Core ranks by after the sealed volume (less is better).";
    }

    static float MeanSpeed(SimulationResult r)
    {
        double sum = 0, volume = 0;
        foreach (var room in r.Rooms) { sum += (double)room.MeanSpeed * room.Volume; volume += room.Volume; }
        return volume > 0 ? (float)(sum / volume) : 0f;
    }

    /// <summary>Applying a row goes through the same path a click on an opening uses: flip the model, rebuild
    /// the scene, tell the live solver its geometry changed, and let it warm-start.</summary>
    void OnSweepRow(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: SweepRow row } || sweepEntries is null) return;
        if ((uint)row.Index >= (uint)sweepEntries.Count) return;
        var entry = sweepEntries[row.Index];
        for (int i = 0; i < sweepIds.Count && i < entry.Open.Length; i++)
        {
            var opening = model.Openings.FirstOrDefault(o => o.Id == sweepIds[i]);
            if (opening != null) opening.IsOpen = entry.Open[i];
        }
        Viewport3D.Renderer.SetScene(Scene.Build(model, windVector, probes, overlay: Overlay()), frameCamera: false);
        if (live is { Active: true } solver)
        {
            solver.GeometryChanged(model, "sweep entry");
            solver.SetPaused(false);
        }
        statsVersion = -1;
        StatusText.Text = $"applied {row.Chips} — {entry.TotalAch:0.#} building fresh-air ACH in the sweep; the live solver is warm-starting";
    }

    // ---------------------------------------------------------------- annual study

    async void OnLoadEpw(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open an EnergyPlus weather file",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("EnergyPlus weather") { Patterns = ["*.epw"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await LoadEpw(path);
    }

    async Task LoadEpw(string path)
    {
        try
        {
            var loaded = await Task.Run(() => Epw.Load(path));
            windBins = WindRose.Build(loaded, 16);
            var sectors = Advanced.Sectors(windBins);
            Advanced.DrawRose(RoseCanvas, sectors, SetWindFromSector);
            RoseCanvas.IsVisible = true;
            AnnualButton.IsEnabled = !jobBusy;

            int calm = 0, runs = 0;
            foreach (var b in windBins) { if (b.Sector < 0) calm += b.Hours; else runs++; }
            var top = sectors.OrderByDescending(s => s.Hours).First();
            string city = loaded.City.Length > 0 ? loaded.City : Path.GetFileNameWithoutExtension(path);
            EpwText.Text = $"{city} · {loaded.Hours.Count:N0} hours · prevailing {Advanced.Bearing(top.DirectionDeg)} " +
                           $"{top.MeanSpeed:0.0} m/s ({top.Share * 100:0} % of the year) · {calm:N0} h calm · {runs} bins to run";
            settings.LastEpw = path;
            settings.Save();
        }
        catch (Exception ex)
        {
            EpwText.Text = "could not read that weather file: " + ex.Message;
        }
    }

    /// <summary>Clicking a rose sector puts the live view on that wind: the sector centre, its mean speed. The
    /// status tick picks the boxes up and pushes them to the solver, debounced like any typed change.</summary>
    void SetWindFromSector(Advanced.RoseSector sector)
    {
        // P13b-06: the sector's speed is the weather station's; the live wind is the site's.
        float site = MathF.Max(sector.MeanSpeed * EpwSiteFactor(), 0.5f);
        WindDirBox.Text = sector.DirectionDeg.ToString("0");
        WindSpeedBox.Text = site.ToString("0.0");
        StatusText.Text = $"wind set to {Advanced.Bearing(sector.DirectionDeg)} {sector.DirectionDeg:0}° " +
                          $"{site:0.0} m/s ({sector.Hours:N0} h a year; weather-station speed {sector.MeanSpeed:0.0} m/s scaled to this site)";
    }

    void OnRunAnnual(object? sender, RoutedEventArgs e) => _ = RunAnnual(0);

    /// <summary>One Preview run per non-calm wind bin. <paramref name="maxBins"/> &gt; 0 keeps only that many
    /// busiest bins, which is how the headless check exercises the path without solving all 64.</summary>
    async Task RunAnnual(int maxBins)
    {
        if (jobBusy || windBins is null) return;
        var use = windBins;
        if (maxBins > 0)
            use = windBins.Where(b => b.Sector >= 0).OrderByDescending(b => b.Hours).Take(maxBins)
                          .Concat(windBins.Where(b => b.Sector < 0)).ToList();
        int runs = use.Count(b => b.Sector >= 0);
        if (runs == 0) { EpwText.Text = "that weather file has no windy hours to run"; return; }

        var building = LiveSolver.Snapshot(model);       // same rule as the sweep: a background job gets a copy
        var options = JobOptions();
        float siteFactor = EpwSiteFactor();   // P13b-06: EPW speeds are the weather station's, the run needs the site's
        BeginJob();
        AnnualProgress.IsVisible = true;
        AnnualProgress.Value = 0;
        AnnualCancelButton.IsVisible = true;
        StatusText.Text = $"annual study: {runs} runs…";
        try
        {
            var progress = new Progress<double>(v =>
            {
                AnnualProgress.Value = Math.Clamp(v, 0, 1) * 100;
                StatusText.Text = $"annual study… {v * 100:0} % of {runs} runs";
            });
            // The threshold the room chips call good (fresh-air ACH, Standards.AirQualityGood), applied to the building's fresh-air rate.
            var study = await Task.Run(() => Annual.Run(building, use,
                bin => SolverRun.WindVector(bin.DirectionDeg, bin.SpeedMid * siteFactor, building.NorthDeg), Standards.AirQualityGood, options, progress, jobCancel!.Token),
                jobCancel!.Token);
            ShowAnnual(study, runs);
            StatusText.Text = $"annual study done · {runs} runs · {study.CalmHours:N0} calm hours";
        }
        catch (OperationCanceledException) { StatusText.Text = "annual study cancelled"; }
        catch (Exception ex) { StatusText.Text = "annual study failed: " + ex.Message; }
        finally
        {
            AnnualProgress.IsVisible = false;
            AnnualCancelButton.IsVisible = false;
            EndJob();
        }
    }

    void ShowAnnual(AnnualResult study, int runs)
    {
        // The building row is the fresh-air rate from the air age solved per wind bin (3600 / mean age over all indoor volume); a room row is
        // the outdoor air entering THAT room straight from outside (air via other rooms is not in it).
        var rows = new List<AnnualRow>
        {
            new AnnualRow("Whole building",
                $"fresh-air rate from the air age, mean {study.BuildingMeanFreshAch:0.0} ACH over {study.VolumeText}",   // RW-03: names the sealed rooms it leaves out
                $"{study.BuildingVentilatedShare * 100:0} %"),
        };
        foreach (var r in study.Rooms.OrderByDescending(r => r.VentilatedShare))
            rows.Add(new AnnualRow(
                DisplayName(r.Room.Id, r.Room.Name),
                $"direct outdoor air, mean {r.MeanAch:0.0} ACH · best {Advanced.Bearing(r.BestDirectionDeg)} · worst {Advanced.Bearing(r.WorstDirectionDeg)}",
                $"{r.VentilatedShare * 100:0} %"));
        AnnualList.ItemsSource = rows;
        EpwText.Text += $"\n{runs} runs done · share of hours at or above {Standards.AirQualityGood:0.#} ACH of outdoor air (building row: fresh-air rate from the air age per wind bin; room rows: direct outdoor air), {study.CalmHours:N0} calm hours counted as unventilated";
    }

    // ---------------------------------------------------------------- PDF report

    /// <summary>What the report is about: the compared state if Compare is on, else the live analysis, else
    /// whichever snapshot exists.</summary>
    SimulationResult? ReportSource() => comparing ? holdB : live?.AccurateStats ?? live?.Stats ?? holdB ?? holdA ?? result;

    /// <summary>Anything but the finished Accurate pass is a Preview result, and the report says so.</summary>
    bool IsPreviewResult(SimulationResult r) => live?.IsAccurate(r) != true;

    string ScenarioLine(SimulationResult r)
    {
        int open = 0;
        foreach (var o in r.Model.Openings) if (o.IsOpen) open++;
        return $"wind {ReportWind.Text(r.Wind, r.Model.NorthDeg)}, {open} of {r.Model.Openings.Count} openings open" +
               (comparing ? ", against held state A" : "");
    }

    async void OnExportReport(object? sender, RoutedEventArgs e)
    {
        if (ReportSource() is not { } r)
        {
            StatusText.Text = "No analysis yet: press Run and give the solver a few seconds, then Export PDF.";
            return;
        }
        var dialog = new ReportDialog(settings, model.Name, ScenarioLine(r));
        if (await dialog.ShowDialog<bool>(this) != true) return;
        settings.ProjectName = dialog.ProjectName;
        settings.Author = dialog.Author;
        settings.Save();

        var folder = UserFiles.DefaultFolder();     // S4-04
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the report",
            SuggestedFileName = (model.Name.Length > 0 ? model.Name : "windflow") + (IsPreviewResult(r) ? " (preview)" : "") + ".pdf",
            DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType("PDF report") { Patterns = ["*.pdf"] }],
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(folder),
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        await WriteReport(r, path, new ReportOptions
        {
            ProjectName = dialog.ProjectName,
            Author = dialog.Author,
            Scenario = dialog.Scenario,
        });
    }

    /// <summary>Viz draws with WPF (DrawingVisual into a RenderTargetBitmap) and documents that it needs no
    /// Dispatcher, so the whole report is built on the pool and the UI keeps rendering.</summary>
    async Task WriteReport(SimulationResult r, string path, ReportOptions options)
    {
        // The room names the panel shows (RoomNames.cs, renames included), not the solver snapshot ones.
        var compareBase = comparing && holdA != null ? WithDisplayNames(holdA) : null;
        // A Preview result says so on the cover (the scenario line under the title), whatever the dialog held.
        if (IsPreviewResult(r) && !options.Scenario.StartsWith(PreviewCover, StringComparison.Ordinal))
            options.Scenario = options.Scenario.Length > 0 ? $"{PreviewCover} · {options.Scenario}" : PreviewCover;
        r = WithDisplayNames(r);
        ReportButton.IsEnabled = false;
        ReportButton.Content = "Exporting…";
        reportProgress = "report · starting…";
        StatusText.Text = reportProgress;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // The pool thread only posts; the status tick shows reportProgress ahead of the solver line.
        void Progress(string what) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (reportProgress == null) return;              // finished before this post ran
            reportProgress = $"report · {what}…";
            StatusText.Text = reportProgress;
        });
        try
        {
            var cover = await CaptureCover();
            int plates = await Task.Run(() => Advanced.BuildReport(r, compareBase, path, options, Progress, cover));
            double seconds = clock.Elapsed.TotalSeconds;
            long bytes = new FileInfo(path).Length;
            reportLine = $"wrote {Path.GetFileName(path)} — {plates} images, " +
                         $"{Advanced.PdfPageCount(path)} pages, {bytes / 1024:N0} kB in {seconds:0.0} s";
            StatusText.Text = reportLine;
            Log.Info("report " + reportLine + $" · rooms {string.Join(", ", r.Rooms.Select(x => x.Room.Name))}");
        }
        catch (Exception ex)
        {
            reportLine = "report failed: " + ex.Message;
            Log.Error("report failed", ex);
            StatusText.Text = reportLine;
        }
        finally
        {
            reportProgress = null;
            ReportButton.Content = "Export PDF";
            ReportButton.IsEnabled = true;
        }
    }

    string reportLine = "";
    const string PreviewCover = "PREVIEW result (fast, directional; not the averaged pass)";
    string? reportProgress;          // non-null while a report is being drawn; the status tick shows it

    /// <summary>A shallow copy of a result whose rooms carry the display names (RoomNames.cs + renames). The
    /// result's own Room objects belong to the solver's snapshot, which is shared read-only, so they are copied
    /// rather than renamed in place.</summary>
    SimulationResult WithDisplayNames(SimulationResult r)
    {
        var copy = (SimulationResult)ShallowCopy(r);
        var building = (BuildingModel)ShallowCopy(r.Model);
        var byId = new Dictionary<string, Room>(StringComparer.Ordinal);
        Room Named(Room room)
        {
            if (byId.TryGetValue(room.Id, out var done)) return done;
            var named = (Room)ShallowCopy(room);
            named.Name = DisplayName(room.Id, room.Name);
            byId[room.Id] = named;
            return named;
        }
        building.Rooms = r.Model.Rooms.Select(Named).ToList();
        copy.Model = building;
        copy.Rooms = r.Rooms.Select(rr =>
        {
            var row = (RoomResult)ShallowCopy(rr);
            row.Room = Named(rr.Room);
            return row;
        }).ToList();
        return copy;
    }

    static readonly System.Reflection.MethodInfo MemberwiseCloneMethod = typeof(object).GetMethod("MemberwiseClone",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    static object ShallowCopy(object o) => MemberwiseCloneMethod.Invoke(o, null)!;

    // ---------------------------------------------------------------- job plumbing

    void BeginJob()
    {
        jobBusy = true;
        jobCancel = new CancellationTokenSource();
        // A batch run wants the whole GPU, and the paused live solver would otherwise keep re-analysing the
        // model the sweep is flipping openings on.
        if (live is { Active: true } solver) { solver.SetPaused(true); solver.SuspendAnalysis = true; }
        SweepButton.IsEnabled = false;
        AnnualButton.IsEnabled = false;
        ReportButton.IsEnabled = false;
        HoldAButton.IsEnabled = false;
        HoldBButton.IsEnabled = false;
    }

    void EndJob()
    {
        jobBusy = false;
        jobCancel?.Dispose();
        jobCancel = null;
        if (live is { Active: true } solver)
        {
            solver.SuspendAnalysis = false;
            if (!comparing) solver.SetPaused(false);
        }
        AnnualButton.IsEnabled = windBins is not null;
        ReportButton.IsEnabled = true;
        HoldAButton.IsEnabled = true;
        HoldBButton.IsEnabled = true;
        UpdateSweepHint();
    }

    // ---------------------------------------------------------------- headless checks

    /// <summary>--shotmode advanced: solve live, load a weather file, sweep three windows, run a two-bin annual,
    /// then photograph the panel scrolled to the sweep results and the rose.</summary>
    async Task AdvancedShotSequence(string path)
    {
        ApplyMode(true);
        Viewport3D.Renderer.InputEnabled = false;   // keep stray keys and wheels out of a headless run
        StartLive();
        await Task.Delay(9000);
        SetSliceRanges();

        var epwPath = Path.Combine(Options.RepoRoot(), "out", "synthetic.epw");
        if (File.Exists(epwPath)) await LoadEpw(epwPath);

        foreach (var opening in model.Openings.Where(o => o.Kind == OpeningKind.Window).Take(3))
            sweepPicks.Add(model.Openings.IndexOf(opening));
        UpdateSweepHint();
        statsVersion = -1;
        RefreshPanel();
        await RunSweep();
        if (windBins is not null) await RunAnnual(2);

        // The panel is taller than the window: bring the sweep and the rose into view for the picture.
        SelectTab(TabStudies);
        await Task.Delay(300);
        PanelScroll.Offset = new Avalonia.Vector(0, 200);
        await Task.Delay(700);
        await SaveShell(path);
        Close();
    }

    /// <summary>--shotmode compare: hold A, shut the windward window, hold B, and photograph B − A.</summary>
    async Task CompareShotSequence(string path)
    {
        ApplyMode(true);
        Viewport3D.Renderer.InputEnabled = false;   // keep stray keys and wheels out of a headless run
        StartLive();
        await Task.Delay(11000);
        SetSliceRanges();
        Hold(0);
        await Task.Delay(2500);
        ToggleWindwardWindow();
        await Task.Delay(11000);
        Hold(1);
        await Task.Delay(2500);
        PollHolds();
        CompareToggle.IsChecked = true;
        OnCompare(null, null!);
        for (int i = 0; i < 100 && !compareShown; i++) await Task.Delay(100);
        SelectTab(TabStudies);
        await Task.Delay(700);
        await SaveShell(path);

        // While Compare is on the report gains the difference plate, so check that branch here too.
        if (holdB is { } b)
        {
            string pdf = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "app-report-compare.pdf");
            await WriteReport(b, pdf, new ReportOptions
            {
                ProjectName = model.Name,
                Author = "WindFlow headless check",
                Scenario = ScenarioLine(b),
            });
            File.AppendAllText(Path.ChangeExtension(path, ".txt"),
                "compare " + CompareNote.Text + "\n" + "report " + reportLine + "\n");
        }
        Close();
    }

    /// <summary>--report &lt;path&gt;: solve live, photograph the details dialog, write the PDF, then write a
    /// sidecar with the size and the page count (PDFsharp cannot rasterise a page, so the count comes from a
    /// byte scan of the page objects).</summary>
    async Task ReportSequence(string path)
    {
        ApplyMode(true);
        Viewport3D.Renderer.InputEnabled = false;   // keep stray keys and wheels out of a headless run
        StartLive();
        await Task.Delay(15000);
        if (ReportSource() is not { } r)
        {
            File.WriteAllText(Path.ChangeExtension(path, ".txt"), "no analysis was available to report\n");
            Close();
            return;
        }

        var dialog = new ReportDialog(settings, model.Name, ScenarioLine(r));
        dialog.Show(this);                       // shown, not modal, so the check can photograph and close it
        await Task.Delay(900);
        string shot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "app-report-dialog.png");
        SaveWindowShot(dialog, shot);
        var options = new ReportOptions
        {
            ProjectName = dialog.ProjectName,
            Author = dialog.Author.Length > 0 ? dialog.Author : "WindFlow headless check",
            Scenario = dialog.Scenario,
        };
        dialog.Close();

        var lines = new List<string> { $"source    {(live?.Stats != null ? "live analysis" : "held snapshot")}" };
        try
        {
            var named = WithDisplayNames(r);
            var cover = await CaptureCover();
            int plates = await Task.Run(() => Advanced.BuildReport(named, null, path, options, null, cover));
            long bytes = new FileInfo(path).Length;
            lines.Add($"path      {path}");
            lines.Add($"images    {plates} ({string.Join(", ", Advanced.Storeys(r).Select(s => $"plan {s.Level} at {s.Z:0.0} m"))}, section)");
            lines.Add($"size      {bytes:N0} bytes ({bytes / 1024:N0} kB), over 50 kB: {bytes > 50 * 1024}");
            lines.Add($"pages     {Advanced.PdfPageCount(path)} (byte scan for /Type /Page)");
            lines.Add($"dialog    {shot}");
            lines.Add($"scenario  {options.Scenario}");
        }
        catch (Exception ex)
        {
            lines.Add("report FAILED: " + ex);
        }
        File.WriteAllLines(Path.ChangeExtension(path, ".txt"), lines);
        Close();
    }

    /// <summary>--shotmode thermal: measure the isothermal rate, put people in the biggest room, turn the
    /// temperature lattice on, wait for the first thermal average, and photograph a temperature section
    /// through that room. Writes both MLUPS figures into the sidecar.</summary>
    async Task ThermalShotSequence(string path)
    {
        ApplyMode(true);
        Viewport3D.Renderer.InputEnabled = false;
        StartLive();
        await Task.Delay(14000);
        float mlupsOff = live?.Mlups ?? 0, fpsOff = Viewport3D.Renderer.Fps;
        string backend = live?.Backend ?? "";

        RoomResult? biggest = null;
        foreach (var r in live?.Stats?.Rooms ?? []) if (biggest == null || r.Volume > biggest.Volume) biggest = r;
        var room = biggest != null ? model.Rooms.FirstOrDefault(x => x.Id == biggest.Room.Id) : model.Rooms.FirstOrDefault();
        if (room == null) { await SaveShell(path); Close(); return; }
        room.Occupants = Options.Occupants;
        Viewport3D.Renderer.SelectedRoom = model.Rooms.IndexOf(room);
        statsVersion = -1;

        ThermalToggle.IsChecked = true;
        ApplyThermal(true);
        // The thermal budget is four flow-throughs of warmup before ToField averages anything at all.
        bool ready = false;
        for (int i = 0; i < 600 && !ready; i++)
        {
            await Task.Delay(500);
            ready = live is { ThermalActive: true, Stats: { } s } &&
                    s.Rooms.Any(r => !float.IsNaN(r.MeanTemperature) && r.MeanTemperature > 1f);
        }
        float mlupsOn = live?.Mlups ?? 0;

        SetSliceRanges();
        SliceVToggle.IsChecked = true;
        OnSliceV(null, null!);
        bool tempButton = FieldTempToggle.IsEnabled;    // enabled by the status tick off TemperatureAvailable
        SetSliceField(2);                       // temperature; this is what makes the solver publish the volume
        // Cut the section through the middle of the occupied room.
        float cx = 0, cy = 0;
        var loop = room.Loops[0];
        foreach (var pt in loop) { cx += pt.X; cy += pt.Y; }
        cx /= loop.Length;
        cy /= loop.Length;
        var particles = Viewport3D.Renderer.Particles;
        SliceOtherSlider.Value = Math.Clamp(particles.SliceAxis == 1 ? cx : cy,
            SliceOtherSlider.Minimum, SliceOtherSlider.Maximum);
        Viewport3D.Renderer.Walls = WallMode.Xray;
        GhostToggle.Content = "X-ray";
        await Task.Delay(14000);                // one more analysis pass: the temperature volume is published there
        RefreshPanel();
        RefreshProbes();
        PanelScroll.Offset = new Avalonia.Vector(0, 0);   // rooms (with the occupant boxes) and ENVIRONMENT together
        await Task.Delay(700);
        await SaveShell(path);

        var stats = live?.Stats;
        var occupied = stats?.Rooms.FirstOrDefault(r => r.Room.Id == room.Id);
        var lines = new List<string>
        {
            $"thermal   wanted {thermalWanted} active {live?.ThermalActive} ready {ready} available {live?.TemperatureAvailable}",
            $"tempbtn   enabled before the slice was switched: {tempButton} (nothing clicked Pressure first)",
            $"mlups     {mlupsOff:0} isothermal -> {mlupsOn:0} thermal ({(mlupsOff > 0 ? mlupsOn / mlupsOff : 0):0.00}x) on {backend}",
            $"fps       {fpsOff:0.0} isothermal -> {Viewport3D.Renderer.Fps:0.0} thermal",
            $"room      {room.Name} · {room.Occupants} people ({room.Occupants * 70} W) · " +
            $"mean {(occupied != null ? occupied.MeanTemperature : float.NaN):0.00} °C",
            $"env       indoor {model.Env.IndoorTemperature:0.#} °C · outdoor {model.Env.OutdoorTemperature:0.#} °C",
            $"slice     section axis {particles.SliceAxis} at {particles.SliceOther:0.0} m · field {particles.SliceField} · " +
            $"scale ±{particles.ScalarScale:0.000} K about {live?.ScalarReferenceC:0.00} °C",
            $"rooms     {string.Join(", ", stats?.Rooms.Select(r => $"{r.Room.Name} {r.MeanTemperature:0.00} °C") ?? [])}",
        };
        File.AppendAllLines(Path.ChangeExtension(path, ".txt"), lines);
        Close();
    }

    /// <summary>--devices-demo: switch the sample's ceiling fan on, add a split AC to the target bedroom,
    /// turn the thermal lattice on so the AC has a supply temperature to deliver, solve live and photograph
    /// it. Writes the set and measured flows plus the cooling duty next to the PNG.</summary>
    async Task DevicesDemoSequence(string path)
    {
        ApplyMode(true);
        Viewport3D.Renderer.InputEnabled = false;

        // 1. the sample's own ceiling fan, which ships disabled.
        var fan = model.Sources.FirstOrDefault(s => s.Device == DeviceKind.CeilingFan);
        if (fan != null) fan.Enabled = true;

        // 2. a split AC on Bedroom 1's west wall, blowing east into the room.
        var bedroom = model.Rooms.FirstOrDefault(r => r.Name.Contains("Bedroom 1", StringComparison.OrdinalIgnoreCase))
                      ?? model.Rooms.FirstOrDefault();
        if (bedroom != null)
        {
            var preset = Devices.PresetFor(DeviceKind.SplitAc);
            // 0.45 m off the west wall face so every driven cell is air at this grid's Dx.
            var centre = new Vector3(0.45f, 4f, bedroom.ZMin + preset.MountHeight);
            var ac = Devices.Create(preset, centre, Vector3.UnitX, bedroom.Level, 1);
            ac.Name = "Bedroom 1 split AC";
            model.Sources.Add(ac);
        }
        selectedDevice = model.Sources.Count - 1;
        Viewport3D.Renderer.SelectedDevice = selectedDevice;
        RebuildScene();
        RefreshDevices();

        // 3. thermal on, so SupplyTemperature and HeatRemovedWatts mean something, then solve.
        ThermalToggle.IsChecked = true;
        ApplyThermal(true);
        StartLive();
        bool ready = await WaitForAverage(200);

        // Frame the bedroom so both glyphs are in shot, and ghost the walls so the plumes read through them.
        if (bedroom != null) Viewport3D.Renderer.SelectedRoom = model.Rooms.IndexOf(bedroom);
        Viewport3D.Renderer.Walls = WallMode.Ghost;
        await Task.Delay(2500);
        RefreshPanel();
        RefreshDevices();
        PanelScroll.Offset = new Avalonia.Vector(0, 0);
        await Task.Delay(700);
        await SaveShell(path);

        var stats = live?.Stats;
        var lines = new List<string>
        {
            $"devices   {model.Sources.Count} in the model · thermal {live?.ThermalActive} · " +
            $"{(ready ? "averaged" : "TIMED OUT")} {live?.AverageSeconds:0.0}/{live?.AverageTarget:0.0} s",
            $"solver    {live?.Cells / 1000.0:0.#}k cells @ {live?.Dx * 1000:0} mm · {live?.Mlups:0} MLUPS · " +
            $"field {live?.FieldHz:0.#} Hz · {Viewport3D.Renderer.Fps:0.0} fps · sim t {live?.SimSeconds:0.0} s",
        };
        foreach (var s in model.Sources)
        {
            var f = stats?.Sources.FirstOrDefault(x => x.Source.Id == s.Id);
            lines.Add($"device    {s.Name,-22} {s.Device} · {(s.Enabled ? "on" : "off")} · set {s.FlowRate:0.000} m3/s · " +
                      $"measured {(f != null ? MathF.Abs(f.FlowRate) : float.NaN):0.000} m3/s · " +
                      $"duty {(f?.HeatRemovedWatts ?? 0):0} W · return {(f?.ReturnTemperature ?? float.NaN):0.0} C");
            lines.Add($"device    {s.Name,-22} centre {s.Center} dir {s.Direction} louvre {s.LouvreAngle:0} deg " +
                      $"swirl {s.Swirl:0.00} radius {s.Radius:0.00} supply {s.SupplyTemperature:0.#} C");
        }
        foreach (var r in stats?.Rooms ?? [])
            lines.Add($"room      {r.Room.Name,-22} {Standards.FreshText(r)} fresh-air ACH · total exchange {r.Ach:0.0} ACH · {r.MeanSpeed:0.00} m/s · {r.MeanTemperature:0.00} C");
        lines.Add($"particles {Viewport3D.Renderer.Particles.Diagnostic}");
        File.AppendAllLines(Path.ChangeExtension(path, ".txt"), lines);
        Close();
    }

    /// <summary>--shotmode boxsolve: solve the whole building, record the target room, then put a simulation
    /// box around that room and solve again. Reports both resolutions and both sets of opening flows, which is
    /// the only way to tell whether the box is a resolution win or a different problem.</summary>
    async Task BoxSolveShotSequence(string path)
    {
        ApplyMode(true);
        Viewport3D.Renderer.InputEnabled = false;
        StartLive();
        bool wholeReady = await WaitForAverage(150);

        // With --box the box decides which room is being studied: pick the largest room whose footprint sits
        // inside it, so the flows reported are the ones the box was drawn around. Without a box, the biggest
        // room in the building is the interesting one.
        Room? room = null;
        if (Options.Box is { } asked)
        {
            float best = -1;
            foreach (var r in live?.Stats?.Rooms ?? [])
            {
                var candidate = model.Rooms.FirstOrDefault(x => x.Id == r.Room.Id);
                if (candidate == null || !InsideBox(candidate, asked) || r.Volume <= best) continue;
                best = r.Volume;
                room = candidate;
            }
            room ??= model.Rooms.FirstOrDefault(m => InsideBox(m, asked));
        }
        if (room == null)
        {
            RoomResult? biggest = null;
            foreach (var r in live?.Stats?.Rooms ?? []) if (biggest == null || r.Volume > biggest.Volume) biggest = r;
            room = biggest != null ? model.Rooms.FirstOrDefault(x => x.Id == biggest.Room.Id) : model.Rooms.FirstOrDefault();
        }
        if (room == null) { await SaveShell(path); Close(); return; }
        Viewport3D.Renderer.SelectedRoom = model.Rooms.IndexOf(room);

        var whole = BoxSnapshot("whole", room, wholeReady);
        float dxWhole = live?.Dx ?? 0;

        var box = Options.Box ?? BoxAroundRoom(room);
        boxResetPending = false;
        Viewport3D.Renderer.SetBox(box.Min, box.Max);
        BoxToggle.IsChecked = true;
        OnBoxToggle(null, null!);
        lastBoxMin = box.Min;
        lastBoxMax = box.Max;
        boxChangedAt = DateTime.MaxValue;
        appliedDomain = box;
        int version = live!.StatsVersion;
        live.SetDomain(box, BoxV1Toggle.IsChecked == true);
        await Task.Delay(1500);
        // Two fresh passes: the first may still describe the tail of the old regime.
        bool boxReady = await WaitForAverage(180, version + 1);
        var inBox = BoxSnapshot("box", room, boxReady);
        float dxBox = live is { Nested: true } nl ? nl.FineDx : live?.Dx ?? 0;

        SetSliceRanges();
        SliceHToggle.IsChecked = true;
        OnSliceH(null, null!);
        Viewport3D.Renderer.Walls = WallMode.Ghost;
        await Task.Delay(1500);
        RefreshPanel();
        PanelScroll.Offset = new Avalonia.Vector(0, 0);
        await Task.Delay(700);
        await SaveShell(path);

        var lines = new List<string>();
        var size = box.Size;
        lines.Add($"box       {box.Min} -> {box.Max} ({size.X:0.#}×{size.Y:0.#}×{size.Z:0.#} m) around {room.Name}");
        lines.AddRange(whole.Lines);
        lines.AddRange(inBox.Lines);
        lines.Add($"dx        {dxWhole * 1000:0} mm whole-building -> {dxBox * 1000:0} mm in the box " +
                  $"({(dxBox > 0 ? dxWhole / dxBox : 0):0.00}x finer)");
        foreach (var (name, qWhole) in whole.Flows)
        {
            float qBox = inBox.Flows.TryGetValue(name, out float q) ? q : float.NaN;
            float ratio = MathF.Abs(qWhole) > 1e-4f ? MathF.Abs(qBox) / MathF.Abs(qWhole) : float.NaN;
            lines.Add($"flow      {name}: {qWhole:+0.000;-0.000;0.000} -> {qBox:+0.000;-0.000;0.000} m³/s " +
                      $"({(ratio - 1) * 100:+0;-0;0} %)");
        }

        // Grow box: clear the blend band, then count Core's own sponge-band notes again on the grown box.
        static int SpongeNotes(string[] notes) => notes.Count(n => n.Contains("sponge band", StringComparison.Ordinal));
        statsVersion = -1;
        RefreshPanel();
        int notesBefore = SpongeNotes(live!.NestNotes);
        string growLabel = BoxGrowButton.IsVisible ? BoxGrowButton.Content?.ToString() ?? "" : "hidden";
        var boxBefore = live.FineBox;
        if (BoxGrowButton.IsVisible)
        {
            OnBoxGrow(null, null!);
            await Task.Delay(1500);
            await WaitForAverage(180, live.StatsVersion + 1);
            statsVersion = -1;
            RefreshPanel();
            await Task.Delay(700);
            await SaveShell(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
                Path.GetFileNameWithoutExtension(path) + "-grown.png"));
        }
        lines.Add($"grow      button \"{growLabel}\" · {notesBefore} sponge-band notes on {boxBefore?.Min} -> {boxBefore?.Max} · " +
                  $"after: {SpongeNotes(live.NestNotes)} notes on {live.FineBox?.Min} -> {live.FineBox?.Max} · " +
                  $"button now {(BoxGrowButton.IsVisible ? BoxGrowButton.Content : "hidden")} · band {live.SpongeWidth:0.##} m");

        // Lock with the box on: the particles must switch to the averaged composite, not keep the last live field.
        lockedFieldNote = "";
        SetLocked(true);
        for (int i = 0; i < 40 && lockedFieldNote.Length == 0; i++) await Task.Delay(100);
        await Task.Delay(1500);
        await SaveShell(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
            Path.GetFileNameWithoutExtension(path) + "-locked.png"));
        lines.Add($"locked    {(lockedFieldNote.Length > 0 ? lockedFieldNote : "NO locked field within 4 s")}");
        File.AppendAllLines(Path.ChangeExtension(path, ".txt"), lines);
        Close();
    }

    /// <summary>Is this room's footprint centre, at mid height, inside the box?</summary>
    static bool InsideBox(Room room, DomainBox box)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var loop in room.Loops)
            foreach (var p in loop)
            {
                minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
                minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
            }
        if (minX > maxX) return false;
        var c = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (room.ZMin + room.ZMax) * 0.5f);
        return c.X >= box.Min.X && c.X <= box.Max.X && c.Y >= box.Min.Y && c.Y <= box.Max.Y &&
               c.Z >= box.Min.Z && c.Z <= box.Max.Z;
    }

    /// <summary>A box around one room: its footprint plus 1.5 m of outdoor air on every side (so the windward
    /// wall and its openings are inside), from the ground to just above its ceiling.</summary>
    static DomainBox BoxAroundRoom(Room room)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var loop in room.Loops)
            foreach (var p in loop)
            {
                minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
                minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
            }
        const float margin = 1.5f;
        return new DomainBox(
            new Vector3(minX - margin, minY - margin, room.ZMin - 1.2f),
            new Vector3(maxX + margin, maxY + margin, room.ZMax + 0.8f));
    }

    (List<string> Lines, Dictionary<string, float> Flows) BoxSnapshot(string label, Room room, bool ready)
    {
        var stats = live?.Stats;
        var flows = new Dictionary<string, float>();
        var lines = new List<string>
        {
            $"{label,-9} {(live?.Cells ?? 0) / 1000.0:0.#}k cells @ {(live?.Dx ?? 0) * 1000:0} mm · " +
            $"averaged {live?.AverageSeconds:0.0}/{live?.AverageTarget:0.0} s · trust {live?.Trust} · " +
            $"{(ready ? "ready" : "TIMED OUT")}",
            $"{label,-9} {live?.Mlups:0} MLUPS · field {live?.FieldHz:0.#} Hz · {Viewport3D.Renderer.Fps:0.0} fps · " +
            $"sim t {live?.SimSeconds:0.0} s",
            $"{label,-9} particles: {Viewport3D.Renderer.Particles.Diagnostic}",
            $"{label,-9} audit: ring air {(live?.Audit ?? BoxAudit.None).RingAirFraction * 100:0.#} % · cuts " +
            $"{((live?.Audit ?? BoxAudit.None).CutsIndoorAir ? string.Join(", ", live!.Audit.CutRoomNames) : "nothing")} · " +
            $"{(live?.Audit ?? BoxAudit.None).DroppedRooms} rooms dropped · badge \"{live?.Trust}\"",
        };
        if (live != null) lines.AddRange(NestLines(live, label));
        var rr = stats?.Rooms.FirstOrDefault(r => r.Room.Id == room.Id);
        lines.Add(rr == null
            ? $"{label,-9} {room.Name}: no air cells"
            : $"{label,-9} {room.Name}: {Standards.FreshText(rr)} fresh-air ACH · total exchange {rr.Ach:0.0} ACH · {rr.MeanSpeed:0.00} m/s · {rr.Volume:0.0} m³ · " +
              $"{rr.StagnantFraction * 100:0} % still{(live?.FineStats?.Rooms.Contains(rr) == true ? " · fine" : "")}");
        foreach (var f in stats?.Openings ?? [])
        {
            if (!f.Opening.Name.Contains(room.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (!f.Opening.IsOpen) continue;
            flows[f.Opening.Name] = f.FlowRate;
            lines.Add($"{label,-9} {f.Opening.Name}: {f.FlowRate:+0.000;-0.000;0.000} m³/s · {f.MeanSpeed:+0.00;-0.00;0.00} m/s" +
                      (live?.FineStats?.Openings.Contains(f) == true ? " · fine" : ""));
        }
        var warnings = stats?.Field.Grid.Warnings ?? [];
        if (warnings.Count > 0) lines.Add($"{label,-9} warnings: {string.Join(" | ", warnings)}");
        return (lines, flows);
    }

    /// <summary>Waits until the running average covers a full flow-through (the trust gate), or gives up.</summary>
    async Task<bool> WaitForAverage(int seconds, int afterVersion = -1)
    {
        for (int i = 0; i < seconds * 4; i++)
        {
            await Task.Delay(250);
            // The version gate matters after a restart: Stats still holds the PREVIOUS session's analysis,
            // which would pass the average gate instantly and report the old grid as if it were the new one.
            if (live is { Stats: not null } s && s.StatsVersion > afterVersion && s.AverageTarget > 0 &&
                s.AverageSeconds >= s.AverageTarget && !s.Trust.StartsWith("averaging")) return true;
            // A model with no open openings never fills the mass ledger, so the trust gate can never close;
            // the room statistics are still valid once the window is long enough.
            if (live is { Stats: not null } n && n.StatsVersion > afterVersion && n.AverageTarget > 0 &&
                n.AverageSeconds >= n.AverageTarget && n.Trust.StartsWith("no open")) return true;
        }
        return false;
    }

    /// <summary>The report's cover picture: the 3D viewport alone, framed on the whole building (the F view) with the flow showing, one back-buffer readback
    /// downscaled to 1100 px wide. The camera goes back where it was. Null when the viewport is hidden or gave nothing.</summary>
    async Task<System.Windows.Media.Imaging.BitmapSource?> CaptureCover()
    {
        var renderer = Viewport3D.Renderer;
        if (!Viewport3D.IsVisible || renderer.Width < 64 || renderer.Height < 64) return null;
        var cam = renderer.Camera;
        (Vector3 Target, float Distance, float Yaw, float Pitch) saved;
        lock (cam.Sync) saved = (cam.Target, cam.Distance, cam.Yaw, cam.Pitch);
        try
        {
            renderer.FrameModel();
            await Task.Delay(400);
            renderer.TakeShot(out _);                     // drop any older readback
            renderer.RequestShot();
            byte[]? px = null; int pitch = 0;
            for (int i = 0; i < 150 && px == null; i++) { await Task.Delay(20); px = renderer.TakeShot(out pitch); }
            if (px == null) return null;
            int w = renderer.Width, h = renderer.Height;
            var bgr = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int s = y * pitch + x * 4, d = (y * w + x) * 4;
                    bgr[d] = px[s + 2]; bgr[d + 1] = px[s + 1]; bgr[d + 2] = px[s]; bgr[d + 3] = 255;
                }
            System.Windows.Media.Imaging.BitmapSource bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, bgr, w * 4);
            if (w > 1100) bmp = new System.Windows.Media.Imaging.TransformedBitmap(bmp, new System.Windows.Media.ScaleTransform(1100.0 / w, 1100.0 / w));
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) { Log.Info("report cover skipped: " + ex.Message); return null; }
        finally { lock (cam.Sync) (cam.Target, cam.Distance, cam.Yaw, cam.Pitch) = saved; }
    }

    /// <summary>A plain Avalonia window (no child HWND in it) straight to a PNG.</summary>
    void SaveWindowShot(Window window, string path)
    {
        double scale = window.RenderScaling;
        int w = Math.Max(8, (int)(window.Bounds.Width * scale)), h = Math.Max(8, (int)(window.Bounds.Height * scale));
        using var target = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new Avalonia.PixelSize(w, h), new Avalonia.Vector(96 * scale, 96 * scale));
        target.Render(window);

        var bgra = new byte[w * h * 4];
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
        target.CopyPixels(new Avalonia.PixelRect(0, 0, w, h), pin.AddrOfPinnedObject(), bgra.Length, w * 4);
        pin.Free();

        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4] = bgra[i * 4 + 2];
            rgba[i * 4 + 1] = bgra[i * 4 + 1];
            rgba[i * 4 + 2] = bgra[i * 4];
            rgba[i * 4 + 3] = 255;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Bench.WritePng(path, w, h, rgba, w * 4);
    }
}

public sealed record SweepRow(int Index, string Chips, string Name, string Sub, string Ach, string Tip);
public sealed record AnnualRow(string Name, string Sub, string Share);

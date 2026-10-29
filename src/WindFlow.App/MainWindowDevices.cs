using System.Numerics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using WindFlow.App.Viewport;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>The device palette: the DEVICES panel section, in-viewport placement and the glyph selection.
/// Every edit goes through the same command path an opening toggle uses — Core reads devices in
/// <c>Voxelizer.Build</c> and <c>LbmSession.UpdateCells</c> rebuilds its device data on the same domain, so a
/// device change warm-starts the running flow instead of restarting it. Supply temperature only does anything
/// with Thermal on, which the panel says rather than silently restarting the solve.</summary>
public partial class MainWindow
{
    DevicePreset? placing;                 // the palette entry waiting for a click in the viewport
    AirSource? placeGhost;                 // what the ghost currently looks like
    int selectedDevice = -1;
    DateTime devicesChangedAt = DateTime.MaxValue;
    static readonly TimeSpan DeviceDragDebounce = TimeSpan.FromSeconds(0.4);

    void InitDevices()
    {
        // One tile per preset: a line icon and the preset name. A click arms placement (the ghost follows the
        // cursor on the matching surface); a second click on the same tile, or Esc, disarms it.
        foreach (var preset in Devices.Presets)
        {
            var tile = new Button
            {
                Classes = { "tool", "tile" },
                Tag = preset,
                Content = new StackPanel
                {
                    Spacing = 4,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Children =
                    {
                        new Avalonia.Controls.Shapes.Path
                        {
                            Data = Geometry.Parse(DeviceIcon(preset.Kind)),
                            StrokeThickness = 1.5,           // colour from the Button.tile Path style
                            StrokeLineCap = PenLineCap.Round,
                            StrokeJoin = PenLineJoin.Round,
                            Width = 22,
                            Height = 22,
                            Stretch = Stretch.Uniform,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        },
                        new TextBlock
                        {
                            Text = preset.Label,
                            FontSize = 10.5,
                            TextWrapping = TextWrapping.Wrap,
                            TextAlignment = TextAlignment.Center,
                        },
                    },
                },
            };
            string where = preset.Surface switch
            {
                MountSurface.Ceiling => "on a ceiling",
                MountSurface.Floor => "on a floor",
                _ => "on a wall",
            };
            ToolTip.SetTip(tile, $"{preset.Label}: {preset.Blurb}.\nClick, then click {where} in the viewport. Esc cancels.");
            tile.Click += OnDeviceAdd;
            DevicePalette.Children.Add(tile);
        }
        Viewport3D.DeviceHovered += OnDeviceHovered;
        Viewport3D.DevicePlaced += OnDevicePlaced;
        Viewport3D.DevicePicked += OnDevicePicked;
        Viewport3D.DeviceDragged += OnDeviceDragged;
        Viewport3D.DeviceDragBegan += OnDeviceDragBegan;
        Viewport3D.DeviceYawDragged += OnDeviceYawDragged;
        Viewport3D.Rotated += RotateDevice;
    }

    /// <summary>Line icons on a 24-unit grid, drawn for this palette in the stroke style of the shell's other
    /// icons (1.5 px, round caps). Each shows the mount surface as the long bar.</summary>
    static string DeviceIcon(DeviceKind kind) => kind switch
    {
        // ceiling bar, downrod, blades either side of the hub, a downward draught
        DeviceKind.CeilingFan => "M6 2.5H18 M12 2.5V7 M2.5 9H9.5 M14.5 9H21.5 M14 9A2 2 0 1 1 10 9A2 2 0 1 1 14 9 " +
                                 "M8 14V20 M16 14V20 M12 13V21 M6.5 18.5L8 20L9.5 18.5 M14.5 18.5L16 20L17.5 18.5",
        // round head, hub, pole and base
        DeviceKind.PedestalFan => "M18 8A6 6 0 1 1 6 8A6 6 0 1 1 18 8 M13 8A1 1 0 1 1 11 8A1 1 0 1 1 13 8 M12 14V21 M7.5 21.5H16.5",
        // wall unit with its louvre and three jets
        DeviceKind.SplitAc => "M2.5 4.5H21.5V11.5H2.5Z M5.5 9H18.5 M7 15L5.5 20 M12 15V20.5 M17 15L18.5 20",
        // square cassette on the ceiling bar, a return in the middle, four throws
        DeviceKind.CassetteAc => "M2 2.5H22 M5.5 5.5H18.5V12H5.5Z M9.5 7.5H14.5V10H9.5Z M4 15L2.5 19 M9 15V20 M15 15V20 M20 15L21.5 19",
        // grille on the wall with the air leaving through it
        DeviceKind.ExhaustFan => "M2.5 3V21 M5.5 6.5H13.5V17.5H5.5Z M13 12A3.5 3.5 0 1 1 6 12A3.5 3.5 0 1 1 13 12 " +
                                 "M16 12H22 M19.5 9.5L22 12L19.5 14.5",
        // hood canopy, flue, rising capture
        DeviceKind.Hood => "M3 15H21L17 9H7Z M10 9V3H14V9 M8 20V17.5 M12 21V17.5 M16 20V17.5",
        // ceiling bar, square grille with rings, spread
        _ => "M2 2.5H22 M6 5.5H18V12H6Z M8.5 7.5H15.5V10H8.5Z M6 15L3 20 M12 15V21 M18 15L21 20",
    };

    // ---------------------------------------------------------------- the list

    /// <summary>Repaints the DEVICES rows. Cheap enough for the status tick: a model has a handful of
    /// devices, and the measured numbers only change when a new analysis pass lands.</summary>
    void RefreshDevices() => RefreshDevices(fromTick: false);

    /// <summary>Replacing ItemsSource tears down the row that holds the focused inspector box, and that box's
    /// LostFocus used to run OnDeviceField -> ApplyDeviceChange -> RefreshDevices INSIDE the first swap. The
    /// re-entrant swap left Avalonia's ContentPresenter half-cleared and it threw a NullReferenceException on
    /// the UI thread (the "window died" crash after a flow edit). So: the status tick never repaints under a
    /// focused box, and any other repaint takes focus off the list first, under a flag the handler honours.</summary>
    void RefreshDevices(bool fromTick)
    {
        if (refreshingDevices) return;
        bool focused = DeviceList.IsKeyboardFocusWithin;
        if (fromTick && focused) return;           // the user is typing in the inspector
        refreshingDevices = true;
        try
        {
            if (focused) FocusManager?.Focus(null, NavigationMethod.Unspecified, KeyModifiers.None);  // LostFocus fires here, outside the swap, and is ignored
            RepaintDevices();
        }
        finally { refreshingDevices = false; }
    }

    bool refreshingDevices;

    void RepaintDevices()
    {
        var rows = new List<DeviceRow>();
        var measured = live?.Stats?.Sources;
        // Core reports per-device grid problems as warnings prefixed "Device <name>:". Anything that talks
        // about cells, Dx or a choked intake is an under-resolution complaint; the text carries the numbers
        // (and, once Core adds it, the achieved/set ratio), so it goes straight into the tooltip.
        var warnings = live?.Stats?.Field.Grid.Warnings ?? [];
        // Inside a nested box the fine grid's own notes judge the device: the coarse grid's "only 4.7 cells" is about
        // the cells the box replaced there.
        var fineWarnings = live is { Nested: true } ? live.FineStats?.Field.Grid.Warnings : null;
        bool anyCoarse = false;
        for (int i = 0; i < model.Sources.Count; i++)
        {
            var s = model.Sources[i];
            var flow = measured?.FirstOrDefault(f => f.Source.Id == s.Id);
            string kind = Devices.KindName(s.Device);
            string sub = $"{kind} · set {s.FlowRate:0.00} m³/s";
            if (flow != null && s.Enabled) sub += $" · measured {MathF.Abs(flow.FlowRate):0.00}";
            if (flow is { HeatRemovedWatts: > 1f }) sub += $" · {flow.HeatRemovedWatts:0} W cooling";
            else if (s.Device is DeviceKind.SplitAc or DeviceKind.CassetteAc && !float.IsNaN(s.SupplyTemperature))
                sub += live is { ThermalActive: true } ? " · supply " + s.SupplyTemperature.ToString("0.#") + " °C"
                                                       : " · supply T needs Thermal on";
            if (!string.IsNullOrEmpty(s.Level)) sub += $" · {s.Level}";

            bool inBox = InSolvedBox(s.Center);
            var mine = (inBox && fineWarnings != null ? fineWarnings : warnings)
                .Where(w => w.StartsWith($"Device {s.Name}:", StringComparison.Ordinal)).ToArray();
            bool coarse = mine.Any(w => w.Contains("cells", StringComparison.OrdinalIgnoreCase) ||
                                        w.Contains("resolved", StringComparison.OrdinalIgnoreCase) ||
                                        w.Contains("choked", StringComparison.OrdinalIgnoreCase) ||
                                        w.Contains("at this Dx", StringComparison.OrdinalIgnoreCase));
            // PLAN_V2 3.3 on the live cell size, and Core's own under-resolution notes on top of it.
            float dx = CellSizeAt(s.Center);
            var (resLabel, resWhy) = Devices.Resolution(s, dx);
            bool refine = Devices.NeedsRefine(s, dx) || coarse;
            if (refine && !resLabel.StartsWith("refine", StringComparison.Ordinal)) resLabel = "refine needed";
            // One-click box: a red chip is only red when a box can fix it. Inside a solved box that resolves it the
            // chip says so; one no box on this machine can resolve (a 120 mm split-AC slot) says that instead.
            bool boxable = false;
            if (dx > 0 && !refine && inBox) resLabel = "resolution OK (box)";
            else if (dx > 0 && refine && s.Enabled)
            {
                float boxDx = inBox ? dx : PredictedBoxDx([s]);
                if (Devices.NeedsRefine(s, boxDx))
                {
                    resLabel = "too small to resolve";
                    resWhy = Devices.TooSmallWhy(s, boxDx, inBox);
                    refine = false;
                }
                else if (!inBox && !locked)
                {
                    boxable = true;
                    resWhy += System.Environment.NewLine + $"Click to put a simulation box around it ({boxDx * 1000:0} mm cells" +
                              ", with the other flagged devices in the same room).";
                }
            }
            if (mine.Length > 0) resWhy += System.Environment.NewLine + string.Join(System.Environment.NewLine, mine);
            anyCoarse |= boxable;
            bool details = advancedMode && i == selectedDevice && detailsOpen;

            rows.Add(new DeviceRow(
                i, s.Name, sub,
                $"{(s.Enabled ? MathF.Abs(flow?.FlowRate ?? s.FlowRate) : 0f):0.00} m³/s",
                s.Enabled ? (flow != null ? RoomGood : RoomWeak) : RoomBad,
                !s.Enabled,
                advancedMode && i == selectedDevice,
                s.Enabled ? "Turn off" : "Turn on",
                s.FlowRate.ToString("0.00"),
                (s.Radius > 0 ? s.Radius : 0f).ToString("0.00"),
                s.Swirl.ToString("0.00"),
                float.IsNaN(s.SupplyTemperature) ? "—" : s.SupplyTemperature.ToString("0.#"),
                s.TurbulenceIntensity is { } tu ? (tu * 100).ToString("0") : "—",
                s.LouvreAngle.ToString("0"),
                Devices.Yaw(s.Direction).ToString("0"),
                "Click the row to select it; the glyph highlights in the viewport. Drag the glyph to slide " +
                "it along its ceiling, wall or floor" + (Devices.Rotatable(s.Device) ? ", R turns it" : "") +
                ". Changes warm-start the running solve.",
                refine, resLabel, resWhy, details, details ? "Details ▾" : "Details ▸", boxable, Devices.TurbulenceApplies(s)));
        }
        DeviceList.ItemsSource = rows;
        DevicesEmpty.IsVisible = rows.Count == 0;
        DevicesNote.Text = rows.Count == 0 ? "" : $"{rows.Count} · m³/s";
        if (anyCoarse && placing == null)
        {
            DevicesHint.Text = "Click a red \"refine needed\" chip to put a simulation box around that device.";
            HintTone(warn: true);
            DevicesHint.IsVisible = true;
        }
        else if (placing == null && DevicesHint.Text.StartsWith("Click a red", StringComparison.Ordinal))
            DevicesHint.IsVisible = false;
    }

    /// <summary>The device sits inside the box the solver is running (the nested fine box, or a v1 box domain).</summary>
    bool InSolvedBox(Vector3 p)
    {
        if (live is not { } ls) return false;
        if ((ls.Nested ? ls.FineBox : ls.ActiveDomain) is not { } b) return false;
        return p.X >= b.Min.X && p.Y >= b.Min.Y && p.Z >= b.Min.Z && p.X <= b.Max.X && p.Y <= b.Max.Y && p.Z <= b.Max.Z;
    }

    /// <summary>A box nests (half the coarse cell) unless the run is thermal or Expert asked for v1.</summary>
    bool BoxWillNest => !thermalWanted && BoxV1Toggle.IsChecked != true;

    /// <summary>The box a click would make and the cell size it would solve at. Nested: the blend band is the
    /// running nest's own, else Core's default depth for the predicted fine cell. v1 has no band.</summary>
    (DomainBox Box, float Dx) PlanDeviceBox(IReadOnlyList<AirSource> devices)
    {
        float coarse = live?.Dx ?? 0;
        if (!BoxWillNest)
        {
            var whole = Devices.DeviceBox(devices, 0f, 0f, model.GroundZ);
            return (whole, Devices.BoxDx(coarse, false, live?.CellTarget ?? 0, whole));
        }
        float fine = 0.5f * coarse;
        float band = live is { Nested: true, SpongeWidth: > 0 } ls ? ls.SpongeWidth : (Devices.NestedSpongeCells + 1) * fine;
        return (Devices.DeviceBox(devices, band, fine, model.GroundZ), fine);
    }

    float PredictedBoxDx(IReadOnlyList<AirSource> devices) => PlanDeviceBox(devices).Dx;

    /// <summary>The "refine needed" chip: a simulation box around that device, or around every flagged device in
    /// the same room (margins: Devices.DeviceBox). The gizmo takes the box and TrackBoxDomain hands it to the solver
    /// after the usual debounce, exactly as a drag would; the chips re-judge on the box's own cells when it lands.</summary>
    void OnRefineChip(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: DeviceRow row } || !row.Boxable) return;
        e.Handled = true;                                   // not a row click: the selection stays
        BoxDevice(row.Index);
    }

    void BoxDevice(int index)
    {
        if (locked || live is not { Active: true } || (uint)index >= (uint)model.Sources.Count) return;
        var device = model.Sources[index];
        int room = RoomOfDevice(device);
        var flagged = (DeviceList.ItemsSource as IEnumerable<DeviceRow> ?? []).Where(r => r.Boxable).Select(r => r.Index).ToHashSet();
        var group = model.Sources.Where((s, i) => i == index || flagged.Contains(i) && room >= 0 && RoomOfDevice(s) == room).ToList();
        var (box, dx) = PlanDeviceBox(group);
        Viewport3D.Renderer.SetBox(box.Min, box.Max);
        if (BoxToggle.IsChecked != true) { BoxToggle.IsChecked = true; OnBoxToggle(null, null!); }
        var size = box.Size;
        string who = group.Count == 1 ? device.Name : $"{group.Count} devices in {model.Rooms[room].Name}";
        StatusText.Text = $"box around {who}: {size.X:0.#}×{size.Y:0.#}×{size.Z:0.#} m at {dx * 1000:0} mm cells — re-voxelising…";
        Log.Info($"device box: {string.Join(", ", group.Select(s => s.Name))} -> {box.Min} .. {box.Max} ({dx * 1000:0} mm cells, " +
                 $"{(BoxWillNest ? "nested" : "v1")})");
    }

    /// <summary>Room index of a device: its centre nudged 0.3 m along its outflow, so a wall or ceiling mount (whose
    /// centre sits on the room outline) still lands inside the room it serves. -1 when none.</summary>
    int RoomOfDevice(AirSource s)
    {
        var dir = s.Direction.LengthSquared() > 1e-6f ? Vector3.Normalize(s.Direction) : Vector3.Zero;
        int room = BoxAudit.RoomIndex(model, s.Center + 0.3f * dir);
        return room >= 0 ? room : BoxAudit.RoomIndex(model, s.Center);
    }

    /// <summary>--shotmode device-boxed: Expert, sample house, its ceiling fan on and a split AC on a wall of the same
    /// room, solve, then press the fan's "refine needed" chip (the same BoxDevice the click runs) and photograph the
    /// Devices tab once the box has solved. The sidecar has every chip label and tip before and after, the box, and
    /// the cell sizes, so "resolution OK (box)" and "too small to resolve" are checked, not just seen.</summary>
    async Task DeviceBoxedShotSequence(string path)
    {
        ApplyMode(true);
        Viewport3D.Renderer.InputEnabled = false;
        var lines = new List<string>();
        var fan = model.Sources.FirstOrDefault(s => s.Device == DeviceKind.CeilingFan);
        if (fan == null) { lines.Add("NO CEILING FAN in the sample"); File.WriteAllLines(Path.ChangeExtension(path, ".txt"), lines); Close(); return; }
        fan.Enabled = true;
        int roomIndex = RoomOfDevice(fan);
        if (roomIndex >= 0)
        {
            // A split AC on the room's west wall, 0.45 m in so every driven cell is air, blowing east.
            var room = model.Rooms[roomIndex];
            float minX = room.Loops.SelectMany(l => l).Min(q => q.X);
            float minY = room.Loops.SelectMany(l => l).Min(q => q.Y), maxY = room.Loops.SelectMany(l => l).Max(q => q.Y);
            var preset = Devices.PresetFor(DeviceKind.SplitAc);
            var centre = new Vector3(minX + 0.45f, 0.5f * (minY + maxY) + 1f, room.ZMin + preset.MountHeight);
            var ac = Devices.Create(preset, centre, Vector3.UnitX, room.Level, 1);
            ac.Name = $"{room.Name} split AC";
            model.Sources.Add(ac);
        }
        RebuildScene();
        RefreshDevices();
        SelectTab(TabDevices);
        StartLive();
        bool ready = await WaitForAverage(200);
        RefreshDevices();
        await Task.Delay(300);
        string Chips() => string.Join(" | ", (DeviceList.ItemsSource as IEnumerable<DeviceRow> ?? [])
            .Select(r => $"{r.Name}: \"{r.ResLabel}\"{(r.Boxable ? " (clickable)" : "")}{(r.Refine ? " red" : "")}"));
        lines.Add($"whole     {(ready ? "averaged" : "TIMED OUT")} · {live?.Cells / 1000.0:0.#}k cells @ {live?.Dx * 1000:0} mm · " +
                  $"room {(roomIndex >= 0 ? model.Rooms[roomIndex].Name : "none")} · trust \"{live?.Trust}\"");
        lines.Add($"before    {Chips()}");
        foreach (var r in DeviceList.ItemsSource as IEnumerable<DeviceRow> ?? [])
            lines.Add($"  tip     {r.Name}: {r.ResWhy.Replace(System.Environment.NewLine, " / ")}");

        int fanIndex = model.Sources.IndexOf(fan);
        var planned = PlanDeviceBox([fan]);
        BoxDevice(fanIndex);
        lines.Add($"click     {StatusText.Text}");
        // Wait for the nested box to open and average (the gizmo debounce, then the solver re-opens the fine session).
        bool nested = false;
        for (int i = 0; i < 400 && !nested; i++) { await Task.Delay(250); nested = live is { Nested: true, FineBox: not null }; }
        int version = live?.StatsVersion ?? -1;
        bool boxReady = nested && await WaitForAverage(200, version);
        RefreshDevices();
        RefreshPanel();
        await Task.Delay(700);
        var fb = live?.FineBox;
        lines.Add($"box       planned {planned.Box.Min} .. {planned.Box.Max} at {planned.Dx * 1000:0} mm · solved " +
                  $"{(fb is { } b ? $"{b.Min} .. {b.Max}" : "NONE")} · fine {live?.FineDx * 1000:0} mm · band {live?.SpongeWidth:0.00} m · " +
                  $"{(boxReady ? "averaged" : "TIMED OUT")} · trust \"{live?.Trust}\" · notes {string.Join(" / ", live?.NestNotes ?? [])}");
        lines.Add($"after     {Chips()}");
        foreach (var r in DeviceList.ItemsSource as IEnumerable<DeviceRow> ?? [])
            lines.Add($"  tip     {r.Name}: {r.ResWhy.Replace(System.Environment.NewLine, " / ")}");
        SelectDevice(fanIndex);
        Viewport3D.Renderer.Walls = WallMode.Ghost;
        if (roomIndex >= 0) Viewport3D.Renderer.SelectedRoom = roomIndex;
        await Task.Delay(1500);
        shotNote = "device boxed · " + Chips();
        await SaveShell(path);
        File.AppendAllLines(Path.ChangeExtension(path, ".txt"), lines);
        Close();
    }

    void OnDeviceRow(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: DeviceRow row }) return;
        SelectDevice(row.Index == selectedDevice ? -1 : row.Index);
        if (selectedDevice >= 0 && selectedDevice < model.Sources.Count) SendSelect(model.Sources[selectedDevice].Id);
    }

    bool detailsOpen;                       // the selected device's numbers are shown (Details disclosure)

    void OnDeviceDetails(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: int index } || index != selectedDevice) return;
        detailsOpen = !detailsOpen;
        RefreshDevices();
    }

    void SelectDevice(int index)
    {
        if (index != selectedDevice) detailsOpen = false;     // a new selection opens with the numbers folded
        selectedDevice = index;
        Viewport3D.Renderer.SelectedDevice = index;
        RebuildScene();
        RefreshDevices();
    }

    // ---------------------------------------------------------------- placement

    void OnDeviceAdd(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DevicePreset preset }) return;
        if (placing == preset) { CancelPlacement(); StatusText.Text = "placement cancelled"; return; }
        BeginPlacement(preset);
    }

    void BeginPlacement(DevicePreset preset)
    {
        EndAddOpening();
        EndAddRoom();
        placing = preset;
        placeGhost = null;
        placeYaw = 0;
        Viewport3D.DevicePlaceMode = true;
        MarkPaletteTile();
        string where = preset.Surface switch
        {
            MountSurface.Ceiling => "a ceiling",
            MountSurface.Floor => "a floor",
            _ => "a wall",
        };
        DevicesHint.Text = $"Placing {preset.Label}: move over {where} inside a room and click." +
                           (Devices.Rotatable(preset.Kind) ? " R turns it." : "") + " Esc cancels.";
        HintTone(warn: false);
        DevicesHint.IsVisible = true;
        StatusText.Text = DevicesHint.Text;
    }

    void CancelPlacement()
    {
        EndAddOpening();
        EndAddRoom();
        placing = null;
        placeGhost = null;
        Viewport3D.DevicePlaceMode = false;
        DevicesHint.IsVisible = false;
        MarkPaletteTile();
        RebuildScene();
    }

    /// <summary>The armed tile reads as pressed (accent) until the device is dropped or placement is cancelled.</summary>
    void MarkPaletteTile()
    {
        foreach (var c in DevicePalette.Children)
            if (c is Button b) b.Classes.Set("placing", placing != null && ReferenceEquals(b.Tag, placing));
    }

    /// <summary>Placement instructions read in the muted panel colour; only a real problem (a refused spot, an
    /// under-resolved device) takes the warning colour.</summary>
    void HintTone(bool warn)
    {
        if (warn && this.TryFindResource("Warning", out var brush) && brush is IBrush b) DevicesHint.Foreground = b;
        else if (!warn && this.TryFindResource("TextMuted", out var muted) && muted is IBrush m) DevicesHint.Foreground = m;
    }

    float placeYaw;                         // R presses while placing, degrees added to the floor fan heading
    int hoverPx = -1, hoverPy = -1;         // last cursor position in the viewport, so R can redraw the ghost

    /// <summary>The cell size that will resolve a device at <paramref name="p"/>: the fine box's inside the
    /// simulation box, the whole-building grid elsewhere; 0 before the first solve.</summary>
    float CellSizeAt(Vector3 p)
    {
        if (live is not { } ls || ls.Dx <= 0) return 0;
        if (ls.FineDx > 0 && ls.ActiveDomain is { } box &&
            p.X >= box.Min.X && p.Y >= box.Min.Y && p.Z >= box.Min.Z && p.X <= box.Max.X && p.Y <= box.Max.Y && p.Z <= box.Max.Z)
            return ls.FineDx;
        return ls.Dx;
    }

    void OnDeviceHovered(int px, int py)
    {
        hoverPx = px;
        hoverPy = py;
        if (addingRoom) { HoverSpaceAt(px, py); return; }
        if (placing is not { } preset || !Viewport3D.Renderer.ScreenRay(px, py, out var eye, out var dir)) return;
        if (Devices.Pick(model, eye, dir, preset) is not { } hit)
        {
            if (placeGhost == null) return;
            placeGhost = null;
            RebuildScene();
            return;
        }
        // Only rebuild the vertex buffer when the ghost has actually moved: a mouse move is 100+ events a
        // second and Scene.Build walks every mesh.
        if (placeGhost != null && Vector3.Distance(placeGhost.Center, hit.Center) < 0.05f) return;
        placeGhost = PlacedDevice(preset, hit, 0);
        var (res, why) = Devices.Resolution(placeGhost, CellSizeAt(hit.Center));
        HintTone(warn: hit.Warning.Length > 0);
        DevicesHint.Text = hit.Warning.Length > 0
            ? $"{preset.Label} in {hit.Room.Name} — {hit.Warning}"
            : $"{preset.Label} in {hit.Room.Name} · {res}. Click to drop it" +
              (Devices.Rotatable(preset.Kind) ? ", R turns it" : "") + ", Esc cancels.";
        ToolTip.SetTip(DevicesHint, why);
        RebuildScene();
    }

    /// <summary>A device from the preset at the picked spot, with the R turns applied to a floor fan.</summary>
    AirSource PlacedDevice(DevicePreset preset, DevicePlacement hit, int ordinal)
    {
        var device = Devices.Create(preset, hit.Center, hit.Inward, hit.Room.Level, ordinal);
        if (Devices.Rotatable(preset.Kind) && placeYaw != 0)
            device.Direction = Devices.FromYaw(Devices.Yaw(device.Direction) + placeYaw, device.Direction.Z);
        return device;
    }

    /// <summary>R: while placing, turns the ghost; otherwise turns the selected floor fan. A ceiling or wall
    /// device has no free heading, so R says so instead of unmounting it.</summary>
    void RotateDevice()
    {
        if (locked) return;
        if (placing is { } preset)
        {
            if (!Devices.Rotatable(preset.Kind))
            {
                StatusText.Text = $"{preset.Label} faces the room from its {preset.Surface.ToString().ToLowerInvariant()}; R turns floor fans";
                return;
            }
            placeYaw = (placeYaw + Devices.RotateStep) % 360f;
            placeGhost = null;                                    // force the redraw at the same spot
            if (hoverPx >= 0) OnDeviceHovered(hoverPx, hoverPy);
            return;
        }
        if ((uint)selectedDevice >= (uint)model.Sources.Count) return;
        var s = model.Sources[selectedDevice];
        if (!Devices.Rotatable(s.Device))
        {
            StatusText.Text = $"{s.Name} faces the room from its mount; R turns floor fans";
            return;
        }
        s.Direction = Devices.FromYaw(Devices.Yaw(s.Direction) + Devices.RotateStep, s.Direction.Z);
        ApplyDeviceChange($"{s.Name} turned to {Devices.Yaw(s.Direction):0}°");
    }

    void OnDevicePlaced(int px, int py)
    {
        if (addingOpening) { PlaceOpening(px, py); return; }
        if (addingRoom) { PlaceRoom(px, py); return; }
        if (placing is not { } preset || !Viewport3D.Renderer.ScreenRay(px, py, out var eye, out var dir)) return;
        if (Devices.Pick(model, eye, dir, preset) is not { } hit)
        {
            StatusText.Text = "nothing to mount on there — click inside a room";
            return;
        }
        int ordinal = Devices.NextOrdinal(model, preset.Kind);
        var device = PlacedDevice(preset, hit, ordinal);
        model.Sources.Add(device);
        selectedDevice = model.Sources.Count - 1;
        Viewport3D.Renderer.SelectedDevice = selectedDevice;
        placing = null;
        placeGhost = null;
        Viewport3D.DevicePlaceMode = false;
        MarkPaletteTile();
        DevicesHint.IsVisible = hit.Warning.Length > 0;
        DevicesHint.Text = hit.Warning.Length > 0 ? $"{device.Name}: {hit.Warning}" : "";
        HintTone(warn: true);
        ApplyDeviceChange($"{device.Name} added in {hit.Room.Name}");
    }

    void OnDevicePicked(int index) => Avalonia.Threading.Dispatcher.UIThread.Post(() => SelectDevice(index));

    // A drag slides the device within its own mount plane and its own room: a horizontal plane at its height
    // for ceiling and floor devices, the wall plane through it for wall devices. It never re-picks another
    // surface, so Direction, Level and the mount height do not change.
    int dragIndex = -1;
    Room? dragRoom;
    Vector3 dragNormal, dragOffset, dragStart;
    Vector3? dragAxis;     // a gizmo arrow: the slide is held to this line through dragStart
    bool dragWall;

    void OnDeviceDragBegan(int index, int px, int py) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        dragIndex = -1;
        if ((uint)index >= (uint)model.Sources.Count) return;
        var s = model.Sources[index];
        dragAxis = Viewport3D.DeviceDragAxis;
        dragStart = s.Center;
        dragWall = Devices.PresetFor(s.Device).Surface == MountSurface.Wall;
        var flat = new Vector3(s.Direction.X, s.Direction.Y, 0);
        dragNormal = dragWall && flat.LengthSquared() > 1e-6f ? Vector3.Normalize(flat) : Vector3.UnitZ;
        dragRoom = model.Rooms.FirstOrDefault(r => s.Center.Z >= r.ZMin - 0.05f && s.Center.Z <= r.ZMax + 0.05f &&
                                                   Devices.InLoops(r, s.Center.X - (dragWall ? RoomSide(s) * dragNormal.X * 0.12f : 0),
                                                                      s.Center.Y - (dragWall ? RoomSide(s) * dragNormal.Y * 0.12f : 0)));
        if (dragRoom == null || DragPlaneHit(s.Center, px, py) is not { } grab) return;
        dragOffset = s.Center - grab;
        dragIndex = index;
    });

    /// <summary>-1 when Direction points into the room (supply, fan), +1 when it points at the wall (return):
    /// multiplied into Direction it gives the step from the device towards the wall.</summary>
    static float RoomSide(AirSource s) => s.Kind == SourceKind.Return ? 1f : -1f;

    Vector3? DragPlaneHit(Vector3 planePoint, int px, int py)
    {
        if (!Viewport3D.Renderer.ScreenRay(px, py, out var eye, out var dir)) return null;
        float denom = Vector3.Dot(dir, dragNormal);
        if (MathF.Abs(denom) < 1e-5f) return null;
        float t = Vector3.Dot(planePoint - eye, dragNormal) / denom;
        return t > 0 ? eye + dir * t : null;
    }

    void OnDeviceDragged(int index, int px, int py) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (index != dragIndex || (uint)index >= (uint)model.Sources.Count || dragRoom is not { } room) return;
        var s = model.Sources[index];
        if (DragPlaneHit(s.Center, px, py) is not { } hit) return;
        var next = hit + dragOffset;
        if (dragAxis is { } axis) next = dragStart + axis * Vector3.Dot(next - dragStart, axis);
        if (dragWall)
        {
            // Keep a mounted height (split AC, exhaust, hood) and stay clear of floor and ceiling otherwise.
            if (Devices.PresetFor(s.Device).MountHeight > 0) next.Z = s.Center.Z;
            next.Z = Math.Clamp(next.Z, room.ZMin + 0.3f, room.ZMax - 0.2f);
            // The point just off the wall on the room side must still be in this room: past a corner it is not.
            var probe = next - RoomSide(s) * dragNormal * 0.12f;
            if (!Devices.InLoops(room, probe.X, probe.Y)) return;
        }
        else
        {
            next.Z = s.Center.Z;
            if (!Devices.InLoops(room, next.X, next.Y)) return;
        }
        s.Center = next;
        // The solver restart waits for the drag to settle, the picture does not.
        devicesChangedAt = DateTime.UtcNow;
        RebuildScene();
    });

    /// <summary>The yaw ring: the same turn R makes, continuous, on the slide's settle-then-restart path.</summary>
    void OnDeviceYawDragged(int index, float yaw) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (locked || (uint)index >= (uint)model.Sources.Count) return;
        var s = model.Sources[index];
        if (!Devices.Rotatable(s.Device)) return;
        s.Direction = Devices.FromYaw(yaw, s.Direction.Z);
        devicesChangedAt = DateTime.UtcNow;
        RebuildScene();
    });

    // ---------------------------------------------------------------- inspector

    void OnDeviceFieldKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is Control c) OnDeviceField(c, new RoutedEventArgs());
    }

    void OnDeviceField(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: string field } box) return;
        // A LostFocus raised while the rows are being rebuilt belongs to a box that is going away; its value
        // was applied by the Enter that started the rebuild (see RefreshDevices).
        if (refreshingDevices) return;
        if ((uint)selectedDevice >= (uint)model.Sources.Count) return;
        var s = model.Sources[selectedDevice];
        string text = box.Text ?? "";
        // S3-08: NumInput takes '.' or ',' as the decimal mark and refuses NaN, infinity and out-of-range numbers;
        // a refused box keeps the stored value and is marked invalid (the rows are not rebuilt, so the mark stays).
        var (lo, hi, unit) = DeviceRange(field);
        bool blank = text.Trim() is "" or "—" or "-";
        bool parsed = NumInput.TryParse(text, lo, hi, out double dv);
        float v = (float)dv;
        string before = FieldValue(s, field);
        switch (field)
        {
            case "flow" or "radius" or "swirl" or "louvre" when parsed:
                switch (field)
                {
                    case "flow": s.FlowRate = v; break;
                    case "radius": s.Radius = v; break;
                    case "swirl": s.Swirl = v; break;
                    default: s.LouvreAngle = v; break;
                }
                break;
            case "supply" when parsed || blank:
                s.SupplyTemperature = parsed ? v : float.NaN;
                break;
            case "turb" when parsed || blank:
                // Typed as a percent, stored as a fraction; blank hands it back to the preset's default (null).
                s.TurbulenceIntensity = parsed ? v / 100f : null;
                break;
            case "yaw" when parsed: s.Direction = Devices.FromYaw((float)NumInput.WrapDegrees(dv), s.Direction.Z); break;
            default:
                Log.Info($"device edit rejected: {s.Name} · {field} = \"{text}\" (kept {before})");
                NumInput.Mark(box, $"{field}: type a number from {lo:0.##} to {hi:0.##}{unit}. The stored value ({before}) is kept.");
                StatusText.Text = $"{s.Name}: {field} \"{text}\" rejected, needs a number from {lo:0.##} to {hi:0.##}{unit}";
                return;
        }
        NumInput.Mark(box, null);
        string after = FieldValue(s, field);
        if (after == before)
        {
            // Tabbing through the inspector fires LostFocus on every box; an unchanged value must not
            // re-voxelise the model and throw the running average away.
            RefreshDevices();
            return;
        }
        Log.Info($"device edit: {s.Name} · {field} {before} -> {after}{(after != text.Trim() ? $" (typed \"{text}\", clamped)" : "")}");
        ApplyDeviceChange($"{s.Name}: {field} = {after}");
    }

    // A 2.4 m disc moving 5 m³/s is already a large HVLS fan; beyond that the edit is a typo, not a device.
    const float MaxDeviceFlow = 5f, MaxDeviceRadius = 1.5f;

    /// <summary>What each inspector box accepts (S3-08); a number outside it is refused, not clamped.</summary>
    static (double Min, double Max, string Unit) DeviceRange(string field) => field switch
    {
        "flow" => (0, MaxDeviceFlow, " m³/s"),
        "radius" => (0, MaxDeviceRadius, " m"),
        "swirl" => (0, 1, ""),
        "supply" => (5, 40, " °C"),
        "turb" => (0, 50, " %"),
        "louvre" => (0, 75, "°"),
        _ => (-1e6, 1e6, "°"),                      // yaw: any angle, wrapped to [0, 360)
    };

    static string FieldValue(AirSource s, string field) => field switch
    {
        "flow" => s.FlowRate.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        "radius" => s.Radius.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        "swirl" => s.Swirl.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        "supply" => s.SupplyTemperature.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
        "turb" => s.TurbulenceIntensity is { } tu ? (tu * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "default",
        "louvre" => s.LouvreAngle.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
        "yaw" => Devices.Yaw(s.Direction).ToString("0", System.Globalization.CultureInfo.InvariantCulture),
        _ => "?",
    };

    void OnDeviceToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: int index } || (uint)index >= (uint)model.Sources.Count) return;
        var s = model.Sources[index];
        s.Enabled = !s.Enabled;
        ApplyDeviceChange($"{s.Name} {(s.Enabled ? "on" : "off")}");
    }

    void OnDeviceRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: int index } || (uint)index >= (uint)model.Sources.Count) return;
        string name = model.Sources[index].Name;
        model.Sources.RemoveAt(index);
        selectedDevice = -1;
        Viewport3D.Renderer.SelectedDevice = -1;
        ApplyDeviceChange($"{name} removed");
    }

    /// <summary>One place every device edit funnels through: redraw, tell the solver, repaint the rows.
    /// The grid dimensions never change (a device does not move the domain), so Core takes the warm-start
    /// path in UpdateCells and rebuilds its device data there rather than restarting the flow from rest.</summary>
    void ApplyDeviceChange(string what)
    {
        devicesChangedAt = DateTime.MaxValue;
        ClearUndo();
        Log.Info("device change: " + what);
        RebuildScene();
        RefreshDevices();
        if (live is { Active: true } solver) solver.GeometryChanged(model, "device: " + what);
        else stale = true;
        StatusText.Text = what;
    }

    /// <summary>--devices-demo for the bench path: the sample's fan on, a split AC on Bedroom 1's west wall,
    /// thermal on so the AC's supply temperature is actually delivered. Shared with the shot sequence.</summary>
    void EnableDemoDevices()
    {
        var fan = model.Sources.FirstOrDefault(s => s.Device == DeviceKind.CeilingFan);
        if (fan != null) fan.Enabled = true;
        var bedroom = model.Rooms.FirstOrDefault(r => r.Name.Contains("Bedroom 1", StringComparison.OrdinalIgnoreCase));
        if (bedroom != null && !model.Sources.Any(s => s.Device == DeviceKind.SplitAc))
        {
            var preset = Devices.PresetFor(DeviceKind.SplitAc);
            var ac = Devices.Create(preset, new Vector3(0.45f, 4f, bedroom.ZMin + preset.MountHeight),
                Vector3.UnitX, bedroom.Level, 1);
            ac.Name = "Bedroom 1 split AC";
            model.Sources.Add(ac);
        }
        thermalWanted = true;
        ThermalToggle.IsChecked = true;
        RebuildScene();
        RefreshDevices();
    }

    /// <summary>The label the status strip shows for the device under the cursor, or the selected one when
    /// nothing is hovered: the glyph alone cannot say what it is or what it is moving.</summary>
    string DeviceLabel()
    {
        int index = Viewport3D.Renderer.HoverDevice >= 0 ? Viewport3D.Renderer.HoverDevice : selectedDevice;
        if ((uint)index >= (uint)model.Sources.Count) return "";
        var s = model.Sources[index];
        var f = live?.Stats?.Sources.FirstOrDefault(x => x.Source.Id == s.Id);
        string measured = f != null && s.Enabled ? $" · {MathF.Abs(f.FlowRate):0.00} m³/s measured" : "";
        string duty = f is { HeatRemovedWatts: > 1f } ? $" · {f.HeatRemovedWatts:0} W" : "";
        return $"{s.Name} · {Devices.PresetFor(s.Device).Label} · set {s.FlowRate:0.00} m³/s{measured}{duty}" +
               (s.Enabled ? "" : " · off");
    }

    /// <summary>--edit-device "name" field=value in a live bench: the exact inspector path a user takes. The
    /// row is selected, its real TextBox focused and filled, and Enter is raised on it, so the key handler,
    /// the list repaint and the LostFocus that repaint causes all run as they do under a keyboard.</summary>
    async Task EditDeviceStep(string name, string assign)
    {
        int index = model.Sources.FindIndex(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var parts = assign.Split('=', 2, StringSplitOptions.TrimEntries);
        if (index < 0 || parts.Length != 2)
        {
            editNote = $"no device \"{name}\" (or bad \"{assign}\"); devices: {string.Join(", ", model.Sources.Select(s => s.Name))}";
            Log.Warn("edit-device: " + editNote);
            return;
        }
        var source = model.Sources[index];
        var before = live?.Stats?.Sources.FirstOrDefault(f => f.Source.Id == source.Id);
        editBefore = $"set {source.FlowRate:0.000} · measured {(before != null ? MathF.Abs(before.FlowRate) : float.NaN):0.000} m³/s";
        Log.Info($"edit-device: {source.Name} before the edit: {editBefore}");

        ApplyMode(true);
        SelectTab(TabDevices);                            // the inspector lives on the Devices tab
        SelectDevice(index);
        detailsOpen = true;                      // the numbers sit behind Details: open it, as a user would
        RefreshDevices();
        TextBox? box = null;
        for (int i = 0; i < 50 && box == null; i++)
        {
            await Task.Delay(40);
            box = DeviceList.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(t => t.Tag as string == parts[0] && t.IsEffectivelyVisible);
        }
        if (box == null)
        {
            editNote = $"inspector field \"{parts[0]}\" never appeared";
            Log.Warn("edit-device: " + editNote);
            return;
        }
        box.Focus();
        await Task.Delay(100);
        box.Text = parts[1];
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });
        editNote = $"{source.Name} {parts[0]} -> {parts[1]} through the inspector";
        Log.Info("edit-device: " + editNote);
    }

    string editNote = "", editBefore = "";

    /// <summary>--device-stress seconds in a live bench: a device edit every 200 ms through ApplyDeviceChange,
    /// cycling flow, add, move and remove, so the UI mutates model.Sources while the solver is voxelising.</summary>
    async Task DeviceStressStep(double seconds)
    {
        var fan = model.Sources.FirstOrDefault(s => s.Device == DeviceKind.CeilingFan);
        if (fan == null) { editNote = "device stress: no ceiling fan in the model"; Log.Warn(editNote); return; }
        var preset = Devices.PresetFor(DeviceKind.CeilingFan);
        var home = fan.Center;
        int failuresBefore = live?.CommandFailures ?? 0, edits = 0;
        AirSource? extra = null;
        Log.Info($"device stress: {seconds:0.#} s, one edit every 200 ms");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int k = 0; clock.Elapsed.TotalSeconds < seconds; k++, edits++)
        {
            switch (k % 4)
            {
                case 0:
                    fan.FlowRate = fan.FlowRate < 1.2f ? 1.5f : 0.9f;
                    ApplyDeviceChange($"stress {k}: fan flow {fan.FlowRate:0.0}");
                    break;
                case 1:
                    extra = Devices.Create(preset, home + new Vector3(-1.2f, 0, 0), -Vector3.UnitZ, fan.Level, 9);
                    extra.Name = "Stress fan";
                    model.Sources.Add(extra);
                    ApplyDeviceChange($"stress {k}: add");
                    break;
                case 2:
                    fan.Center = home + new Vector3(k % 8 == 2 ? 0.3f : -0.3f, 0, 0);
                    ApplyDeviceChange($"stress {k}: move");
                    break;
                default:
                    if (extra != null) model.Sources.Remove(extra);
                    extra = null;
                    ApplyDeviceChange($"stress {k}: remove");
                    break;
            }
            await Task.Delay(200);
        }
        if (extra != null) { model.Sources.Remove(extra); ApplyDeviceChange("stress: final remove"); }
        fan.Center = home;
        int failures = (live?.CommandFailures ?? 0) - failuresBefore;
        editNote = $"device stress: {edits} edits in {clock.Elapsed.TotalSeconds:0.0} s · {failures} command failures";
        Log.Info(editNote);
    }

    void RebuildScene()
    {
        if (model.Obstacles.Count == 0) return;
        Viewport3D.Renderer.SetScene(Scene.Build(model, windVector, probes, selectedDevice, placeGhost, Overlay()),
            frameCamera: false);
    }

    /// <summary>Called from the status tick: applies a device drag once the pointer has been still for a
    /// moment, the same debounce the simulation box uses.</summary>
    void TrackDeviceDrag()
    {
        if (DateTime.UtcNow - devicesChangedAt < DeviceDragDebounce) return;
        if (devicesChangedAt == DateTime.MaxValue) return;
        devicesChangedAt = DateTime.MaxValue;
        if ((uint)selectedDevice < (uint)model.Sources.Count)
            Log.Info($"device moved: {model.Sources[selectedDevice].Name} -> {model.Sources[selectedDevice].Center}");
        if (live is { Active: true } solver) solver.GeometryChanged(model, "device drag");
        RefreshDevices();
    }
}

public sealed record DeviceRow(int Index, string Name, string Sub, string Value, IBrush Chip, bool Off,
    bool Editing, string ToggleLabel, string Flow, string Radius, string Swirl, string Supply, string Turb, string Louvre,
    string Yaw, string Tip, bool Refine = false, string ResLabel = "", string ResWhy = "",
    bool ShowDetails = false, string DetailsLabel = "Details ▸", bool Boxable = false, bool TurbVisible = false);

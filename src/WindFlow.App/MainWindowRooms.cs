using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WindFlow.App.Viewport;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>PLAN_V2 4.1: rooms a user can tell apart. Display names and order (RoomNames), rename in place,
/// click a row to see the room's volume (click again or Esc to clear), click a floor to select its row, and the
/// same select-to-highlight for openings.
/// Renames live in memory on the loaded model (Room.Name) and in <see cref="renames"/> by room id, so a live-link
/// model swap of the same document keeps them. There is no project file to persist them to.</summary>
public partial class MainWindow
{
    readonly Dictionary<string, string> roomDisplay = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> roomRank = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> renames = new(StringComparer.Ordinal);
    string renamesModel = "";
    string renamingId = "";
    bool renameCommitting;

    /// <summary>ShowModel: carry renames over only for a live-link re-send of the same document; a model the user
    /// loads (file, sample, import) starts clean even if it shares a name and room ids with the previous one.
    /// Then name every room.</summary>
    void PrepareRoomNames(BuildingModel building, bool liveResend)
    {
        CarryRenames(renames, ref renamesModel, building, liveResend);
        renamingId = "";
        RecomputeRoomNames();
        // A new model: the old room's box means nothing here.
        isolateOn = false;
        if (Viewport3D?.Renderer is { } r) r.Isolation = null;
        if (IsolateChip != null) IsolateChip.IsVisible = false;
    }

    internal static void CarryRenames(Dictionary<string, string> renames, ref string renamesModel, BuildingModel building, bool liveResend)
    {
        if (!liveResend || building.Name != renamesModel) renames.Clear();
        renamesModel = building.Name;
        foreach (var room in building.Rooms)
            if (renames.TryGetValue(room.Id, out var name)) room.Name = name;
    }

    void RecomputeRoomNames()
    {
        roomDisplay.Clear();
        roomRank.Clear();
        var ordered = RoomNames.Compute(model);
        for (int i = 0; i < ordered.Count; i++)
        {
            roomDisplay[ordered[i].Room.Id] = ordered[i].Name;
            roomRank[ordered[i].Room.Id] = i;
        }
        if (live != null) live.RoomNames = new Dictionary<string, string>(roomDisplay, StringComparer.Ordinal);
    }

    string DisplayName(string id, string fallback) => roomDisplay.TryGetValue(id, out var n) ? n : fallback;

    int RoomRank(string id) => roomRank.TryGetValue(id, out int r) ? r : int.MaxValue;

    // ---------------------------------------------------------------- rename

    void BeginRename(int index)
    {
        if ((uint)index >= (uint)model.Rooms.Count || comparing) return;
        renamingId = model.Rooms[index].Id;
        statsVersion = -1;
        RefreshPanel();
    }

    /// <summary>The row's box appears already focused with the name selected, like a file rename.</summary>
    void OnRenameAttached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
            Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); });
    }

    void OnRenameKey(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Enter) { CommitRename(box.Text); e.Handled = true; }
        else if (e.Key == Key.Escape) { CommitRename(null); e.Handled = true; }
    }

    /// <summary>Clicking elsewhere keeps what was typed (Esc is the only way to throw it away).</summary>
    void OnRenameLost(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && renamingId.Length > 0) CommitRename(box.Text);
    }

    /// <summary>null cancels. Posted, not inline: the repaint tears down the row whose TextBox raised this event,
    /// and doing that inside its own LostFocus/KeyDown re-enters Avalonia's ContentPresenter (see MEMORY).</summary>
    void CommitRename(string? text)
    {
        if (renameCommitting || renamingId.Length == 0) return;
        renameCommitting = true;
        string id = renamingId;
        renamingId = "";
        text = text?.Trim();
        var room = model.Rooms.FirstOrDefault(r => r.Id == id);
        if (room != null && !string.IsNullOrEmpty(text) && text != DisplayName(id, room.Name))
        {
            Log.Info($"room renamed: {id} \"{room.Name}\" -> \"{text}\"");
            room.Name = text;
            renames[id] = text;
            RecomputeRoomNames();
        }
        Dispatcher.UIThread.Post(() =>
        {
            FocusManager?.Focus(null);
            statsVersion = -1;
            renameCommitting = false;
            RefreshPanel();
        });
    }

    // ---------------------------------------------------------------- selection

    // RENAME-02: the first press of a double-click selects and repaints the list, and the new rows cannot be hit until the
    // next frame. A second press that arrives before it (a fast or machine double-click, or a busy UI thread) lands on the
    // list itself and never reaches OnRoomRow, so OnRoomListPress finishes the double-click for the row just pressed.
    int lastRowPress = -1;
    ulong lastRowPressAt, rowPressHandledAt;
    Avalonia.Point lastRowPressPos;
    int clearToken;

    void OnRoomRow(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: RoomRow row }) return;
        rowPressHandledAt = e.Timestamp;
        if (e.Source is TextBox) return;                       // clicks inside the rename or people box
        if (e.Source is Avalonia.Visual v && v.FindAncestorOfType<Button>(includeSelf: true) != null) return;   // the Isolate link
        clearToken++;                                          // a press cancels a pending "click again to clear"
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) { ToggleMultiRoom(row.Index); return; }
        if (e.ClickCount >= 2) { lastRowPress = -1; SelectRoom(row.Index, frame: false); BeginRename(row.Index); return; }
        lastRowPress = row.Index; lastRowPressAt = e.Timestamp; lastRowPressPos = e.GetPosition(RoomList);
        if (row.Index == Viewport3D.Renderer.SelectedRoom) { DeferClear(row.Index); return; }
        SelectRoom(row.Index);
        if ((uint)row.Index < (uint)model.Rooms.Count) SendSelect(model.Rooms[row.Index].Id);
    }

    /// <summary>A click on the selected row clears it, but only once the double-click time has passed with no second press:
    /// a double-click on a selected row renames it and keeps it selected instead of clearing and re-selecting it.</summary>
    void DeferClear(int index)
    {
        int token = clearToken;
        var wait = DoubleClickTime();
        DispatcherTimer.RunOnce(() =>
        {
            if (token == clearToken && Viewport3D.Renderer.SelectedRoom == index) ClearSelection();
        }, wait);
    }

    TimeSpan DoubleClickTime() => Avalonia.Application.Current?.PlatformSettings?.GetDoubleTapTime(PointerType.Mouse) ?? TimeSpan.FromMilliseconds(500);

    void OnRoomListPress(object? sender, PointerPressedEventArgs e)
    {
        if (e.Timestamp == rowPressHandledAt) return;          // the row got it
        var pos = e.GetPosition(RoomList);
        var wait = DoubleClickTime();
        if (!LostDoubleClick(e.ClickCount, e.Timestamp, lastRowPress, lastRowPressAt, wait.TotalMilliseconds,
                Math.Abs(pos.X - lastRowPressPos.X) + Math.Abs(pos.Y - lastRowPressPos.Y))) return;
        int index = lastRowPress;
        lastRowPress = -1;
        clearToken++;
        SelectRoom(index, frame: false);
        BeginRename(index);
    }

    /// <summary>True for a second press (click count 2+) shortly after, and next to, the press on a room row that no row
    /// handler saw, i.e. the double-click's second half fell on the list while its rows were being replaced.</summary>
    internal static bool LostDoubleClick(int clickCount, ulong now, int lastRow, ulong lastAt, double windowMs, double distance) =>
        clickCount >= 2 && lastRow >= 0 && now >= lastAt && now - lastAt <= windowMs && distance <= 8;

    void OnOpeningRow(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: OpeningRow row } || (uint)row.Index >= (uint)model.Openings.Count) return;
        if (e.Source is Avalonia.Visual src && src.FindAncestorOfType<Button>(includeSelf: true) != null) return;   // the bin
        var r = Viewport3D.Renderer;
        if (r.SelectedOpening == row.Index) { ClearSelection(); return; }
        ClearMulti();
        r.SelectedRoom = -1;
        r.SelectedOpening = row.Index;
        ApplyIsolation();
        statsVersion = -1;
        RefreshPanel();
        SendSelect(model.Openings[row.Index].Id);
    }

    /// <summary>The switch on an opening row: open or close it, the same command a click on it in the viewport
    /// sends. Handled here so the row underneath does not also select.</summary>
    void OnOpeningSwitch(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control { DataContext: OpeningRow row } && (uint)row.Index < (uint)model.Openings.Count)
            ToggleOpening(model.Openings[row.Index]);
    }

    // ---------------------------------------------------------------- Expert: discharge coefficient of a window or door

    void OnCdBox(object? sender, RoutedEventArgs e) { CommitCd(sender); inspTarget = null; }

    void OnCdKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitCd(sender);
        e.Handled = true;
    }

    /// <summary>The inspector's Cd box: it edits the opening it was opened on.</summary>
    void CommitCd(object? sender)
    {
        if (sender is not TextBox box || InspTarget() is not { } op) return;
        if (!locked) ClearUndo();
        if (locked || !OpeningCd.TryParse(box.Text, out float cd))
        {
            if (!locked)
            {
                StatusText.Text = $"{op.Name}: Cd must be a number from {OpeningCd.Min:0.##} to {OpeningCd.Max:0.##}";
                NumInput.Mark(box, $"{StatusText.Text}. The last valid value ({OpeningCd.Text(OpeningCd.Effective(op))}) is kept.");   // S3-08
                return;
            }
            box.Text = OpeningCd.Text(OpeningCd.Effective(op));
            return;
        }
        NumInput.Mark(box, null);
        box.Text = OpeningCd.Text(cd);
        SetOpeningCd(op, cd);
        if (!ReferenceEquals(op, SelectedOpening())) RefreshInspector();     // the selection moved on while the box had focus: it shows that one again
    }

    /// <summary>A Cd edit changes the flow, so it goes down the Reconfigure path: the session restarts on the new model
    /// (Core's UpdateCells keeps the model the session opened with, so a warm-started geometry change would not see the
    /// new coefficient) and an Accurate pass in progress is cancelled.</summary>
    void SetOpeningCd(Opening op, float cd)
    {
        if (locked || !OpeningCd.Apply(op, cd)) return;
        Log.Info($"opening {op.Name}: Cd {OpeningCd.Text(cd)} {OpeningCd.Name(cd)}");
        statsVersion = -1;
        if (live is { Active: true } solver)
        {
            solver.Reconfigure(thermalWanted, model);
            StatusText.Text = $"{op.Name}: Cd {OpeningCd.Text(cd)} — restarting the solve";
        }
        else
        {
            if (result != null) stale = true;
            StatusText.Text = $"{op.Name}: Cd {OpeningCd.Text(cd)} — flow is stale, press Run";
        }
    }

    void OnCdPresets(object? sender, RoutedEventArgs e)
    {
        int index = Viewport3D.Renderer.SelectedOpening;
        if (sender is not Button button || (uint)index >= (uint)model.Openings.Count || locked) return;
        var op = model.Openings[index];
        var flyout = new MenuFlyout();
        foreach (var (name, cd, typical) in OpeningCd.Presets)
        {
            float value = cd;
            var item = new MenuItem { Header = $"{name} · {OpeningCd.Text(cd)}{(typical ? " (typical)" : "")}" };
            item.Click += (_, _) => SetOpeningCd(op, value);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(button);
    }

    /// <summary>Expert, Settings: Calibrated (the default) or Resolved opening model. Flow-changing: the session restarts.</summary>
    void OnOpeningModel(object? sender, RoutedEventArgs e)
    {
        openingModelSetting = OpeningModelToggle.IsChecked == true ? OpeningModel.Resolved : OpeningModel.Calibrated;
        OpeningModelToggle.Content = $"Opening model: {openingModelSetting}";
        Log.Info($"opening model {openingModelSetting}");
        statsVersion = -1;
        if (live is { Active: true } solver)
        {
            solver.Reconfigure(thermalWanted, model, openingModelSetting);
            StatusText.Text = $"opening model {openingModelSetting.ToString().ToLowerInvariant()} — restarting the solve";
        }
        else StatusText.Text = $"opening model {openingModelSetting.ToString().ToLowerInvariant()} — applies when you press Run";
    }

    /// <summary>Also read by the Compare, Sweep and Annual jobs, so a study answers the same question the live view does.</summary>
    internal static OpeningModel openingModelSetting = OpeningModel.Calibrated;

    void SelectRoom(int index) => SelectRoom(index, frame: true);

    /// <summary>A row click frames the room; a click on its floor in the viewport does not move the camera.</summary>
    void SelectRoom(int index, bool frame)
    {
        var r = Viewport3D.Renderer;
        ClearMulti();
        r.SelectedOpening = -1;
        r.SelectedRoom = index;
        if (frame) r.FrameRoom(index);
        // The outliner opens the selected room to its openings, and closes the one it opened before.
        if ((uint)index < (uint)model.Rooms.Count)
        {
            string id = model.Rooms[index].Id;
            if (autoExpanded != null && autoExpanded != id) expandedRooms.Remove(autoExpanded);
            autoExpanded = expandedRooms.Add(id) ? id : autoExpanded == id ? id : null;
        }
        ApplyIsolation();    // isolation follows the selection
        statsVersion = -1;   // repaint the rows so the selection reads back
        RefreshPanel();
    }

    // ---------------------------------------------------------------- isolate (view only)

    /// <summary>Isolate is a view mode on the selected room: the renderer clips everything to that room's box
    /// (<see cref="RoomIsolation"/>). The solver, the published field and the numbers are untouched. Ways out:
    /// the row's "Show all", the chip in the top bar, Esc, or clearing the selection.</summary>
    bool isolateOn;

    bool IsIsolated(int index) => isolateOn && index >= 0 && index == Viewport3D.Renderer.SelectedRoom;

    /// <summary>The inspector's Isolate toggle: isolate the selected room (framing it), or Show all when it already is.</summary>
    void OnIsolateRow(object? sender, RoutedEventArgs e)
    {
        int index = Viewport3D.Renderer.SelectedRoom;
        if ((uint)index >= (uint)model.Rooms.Count) return;
        SetIsolated(IsIsolated(index) ? -1 : index);
    }

    /// <summary>The "Isolated: Hall ✕" chip on the viewport header.</summary>
    void OnIsolateChip(object? sender, RoutedEventArgs e) => SetIsolated(-1);

    /// <summary>index &lt; 0 shows the whole building again and keeps the selection.</summary>
    void SetIsolated(int index)
    {
        if (index >= 0)
        {
            isolateOn = true;
            SelectRoom(index, frame: true);                       // applies the box and repaints
            StatusText.Text = $"Isolated {DisplayName(model.Rooms[index].Id, model.Rooms[index].Name)}: view only, the flow is unchanged. Esc or ✕ shows all rooms.";
            Log.Info($"room isolated: {model.Rooms[index].Id}");
            return;
        }
        if (!isolateOn) return;
        isolateOn = false;
        ApplyIsolation();
        statsVersion = -1;
        RefreshPanel();
    }

    /// <summary>Pushes the current isolation state to the renderer and the chip. Safe to call from any UI path.</summary>
    void ApplyIsolation()
    {
        var r = Viewport3D.Renderer;
        int index = r.SelectedRoom;
        if (isolateOn && ((uint)index >= (uint)model.Rooms.Count)) isolateOn = false;
        var box = isolateOn ? RoomIsolation.Box(model.Rooms[index]) : null;
        if (box is { } b) r.Isolation = new Renderer.IsolationBox(b.Min, b.Max);
        else { r.Isolation = null; isolateOn = false; }
        IsolateChip.IsVisible = isolateOn;
        if (isolateOn) IsolateChipText.Text = $"Isolated: {DisplayName(model.Rooms[index].Id, model.Rooms[index].Name)}";
    }

    void OnRoomPicked(int index)
    {
        if (ViewportHost.CtrlDown) { if (panelTab != TabRooms) SelectTab(TabRooms); ToggleMultiRoom(index); return; }
        if (index == Viewport3D.Renderer.SelectedRoom) { ClearSelection(); return; }
        if (panelTab != TabRooms) SelectTab(TabRooms);   // the picked room's row lives in the Rooms section
        SelectRoom(index, frame: false);
        if ((uint)index < (uint)model.Rooms.Count) SendSelect(model.Rooms[index].Id);
    }

    /// <summary>--shotmode rooms: solve live, then pick the Kitchen row so its volume shows in the whole-house view.</summary>
    async Task RoomsShotSequence(string path)
    {
        Viewport3D.Renderer.InputEnabled = false;   // keep stray keys and wheels out of a headless run
        StartLive();
        await Task.Delay(9000);
        int index = model.Rooms.FindIndex(r => r.Id == "kitchen");
        if (index < 0) index = 0;
        SelectRoom(index, frame: false);
        // Exercise the rename path the way a double-click does: open the box, let it take focus, press Enter.
        BeginRename(index);
        await Task.Delay(600);
        bool boxFocused = RoomList.IsKeyboardFocusWithin;
        CommitRename("Kitchen / dining");
        await Task.Delay(600);
        shotNote = $"rename box focused {boxFocused} · rooms: selected {DisplayName(model.Rooms[index].Id, model.Rooms[index].Name)} · " +
                   string.Join(" | ", model.Rooms.Select(r => DisplayName(r.Id, r.Name)));
        await Task.Delay(1500);
        await SaveShell(path);
        Close();
    }

    /// <summary>Esc (window or viewport) and a second click on the selected row.</summary>
    void ClearSelection()
    {
        var r = Viewport3D.Renderer;
        if (r.SelectedRoom < 0 && r.SelectedOpening < 0 && multiRooms.Count == 0) return;
        ClearMulti();
        r.SelectedRoom = -1;
        r.SelectedOpening = -1;
        isolateOn = false;
        ApplyIsolation();
        statsVersion = -1;
        RefreshPanel();
    }
}

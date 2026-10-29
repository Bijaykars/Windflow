using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WindFlow.App.Viewport;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>Add and delete an opening by hand (the model edits are OpeningEdit's). Add reuses the device placement click path
/// (Viewport3D.DevicePlaceMode -> OnDevicePlaced); delete has no app-wide undo to join, so one deleted opening is kept for the
/// status strip's Undo until the next edit. Everything here hangs off OpeningAddButton, StatusUndoButton, the row's × and the inspector's size boxes.</summary>
public partial class MainWindow
{
    bool addingOpening;                    // "Add opening" is armed: the next wall click places a window
    OpeningEdit.Deleted? undoDelete;       // the last deleted opening, until the next edit
    bool revealAdded;                      // scroll the list to the added row once the solve lists it

    // ---------------------------------------------------------------- add

    void OnOpeningAdd(object? sender, RoutedEventArgs e)
    {
        if (locked) { StatusText.Text = UiTable.LockedWhy; return; }
        if (addingOpening) { CancelPlacement(); StatusText.Text = "placement cancelled"; return; }
        CancelPlacement();
        addingOpening = true;
        Viewport3D.DevicePlaceMode = true;
        OpeningAddButton.Classes.Set("placing", true);      // the button itself says what to do next
        OpeningAddButton.Content = "Click a wall · Esc";
        StatusText.Text = DeletedOpenings().Count > 0 ? "Click a wall to place the opening, or an orange dashed outline to restore a deleted one · Esc cancels" : "Click a wall to place the opening · Esc cancels";
        KeyHints.Text = "Esc  cancel";
        RebuildScene();                                      // the deleted openings' dashed outlines
        RefreshDeletedRows();
        if (DeletedBox.IsVisible) Avalonia.Threading.Dispatcher.UIThread.Post(() => PanelScroll.ScrollToEnd(), Avalonia.Threading.DispatcherPriority.Background);   // the Restore rows sit under a long list
    }

    /// <summary>Leaves the add mode (no-op when not in it); CancelPlacement and BeginPlacement call it so the two modes never overlap.</summary>
    void EndAddOpening()
    {
        if (!addingOpening) return;
        addingOpening = false;
        Viewport3D.DevicePlaceMode = false;
        OpeningAddButton.Classes.Set("placing", false);
        OpeningAddButton.Content = "+ Add opening";
        RefreshDeletedRows();
        RefreshInspector();                                  // the key hints go back to the selection's
    }

    void PlaceOpening(int px, int py)
    {
        if (locked) { StatusText.Text = UiTable.LockedWhy; return; }
        if (!Viewport3D.Renderer.ScreenRay(px, py, out var eye, out var dir)) return;
        var wall = OpeningEdit.PickWall(model, eye, dir);
        int deletedAt = Viewport3D.Renderer.PickDeletedAt(px, py, out float deletedT);      // a deleted opening's outline, unless a wall stands well in front of it
        if (deletedAt >= 0 && (wall is not { } w || deletedT <= System.Numerics.Vector3.Distance(eye, w.Point) + 0.6f)) { RestoreDeleted(deletedAt); return; }
        if (wall is not { } hit) { StatusText.Text = "that is not a wall: click a wall (Esc cancels)"; return; }
        if (OpeningEdit.Add(model, hit, out string why) is not { } opening) { StatusText.Text = why; return; }
        EndAddOpening();
        ClearUndo();
        SelectOpening(model.Openings.Count - 1);
        revealAdded = true;
        ApplyOpeningChange($"{opening.Name} added");
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { InspWidth.Focus(); InspWidth.SelectAll(); });   // the next keystrokes set its size
    }

    // ---------------------------------------------------------------- delete and undo

    /// <summary>The row's × (Tag = its index) or the inspector's Delete (the selected opening).</summary>
    void OnOpeningDelete(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        DeleteOpening(sender is Button { Tag: int index } ? index : Viewport3D.Renderer.SelectedOpening);
    }

    void DeleteSelectedOpening() => DeleteOpening(Viewport3D.Renderer.SelectedOpening);

    void DeleteOpening(int index)
    {
        if ((uint)index >= (uint)model.Openings.Count) return;
        if (locked) { StatusText.Text = UiTable.LockedWhy; return; }
        ClearUndo();
        var d = OpeningEdit.Delete(model, model.Openings[index]);
        ShiftOpeningIndexes(index, -1);
        undoDelete = d;
        DeletedOpenings().Add(d);                            // kept for the session (RestoreDeleted), unlike the one-step Undo
        StatusUndoButton.IsVisible = true;
        ApplyOpeningChange("Opening deleted");
    }

    void OnOpeningUndo(object? sender, RoutedEventArgs e)
    {
        if (locked) return;
        if (undoRoom is { } roomUndo) { ClearUndo(); ApplyRoomChange(roomUndo()); return; }     // a room edit's inverse (MainWindowRoomEdit)
        if (undoDelete is not { } d) return;
        ClearUndo();
        DeletedOpenings().Remove(d);
        OpeningEdit.Restore(model, d);
        ShiftOpeningIndexes(d.Index, +1);
        ApplyOpeningChange($"{d.Opening.Name} restored");
    }

    // ---------------------------------------------------------------- restore a deleted opening (session)

    /// <summary>Every opening deleted in this session, for the model shown (a new model starts a new list). Session-only: the records are not saved with the model.</summary>
    readonly List<OpeningEdit.Deleted> deletedLog = [];
    BuildingModel? deletedLogModel;

    List<OpeningEdit.Deleted> DeletedOpenings()
    {
        if (!ReferenceEquals(deletedLogModel, model)) { deletedLog.Clear(); deletedLogModel = model; }
        return deletedLog;
    }

    /// <summary>The scene overlay: the candidate spaces while the Rooms section shows or Add room is armed, the deleted openings' outlines while Add opening is armed; null when neither.</summary>
    SceneOverlay? Overlay()
    {
        var sp = (addingRoom || RoomsPane.IsVisible) && spaces is { Count: > 0 } && ReferenceEquals(spacesModel, model) ? spaces : null;
        var del = addingOpening ? DeletedOpenings() : null;
        if (sp == null && del is not { Count: > 0 }) return null;
        return new SceneOverlay(sp ?? [], del?.Select(x => x.Opening).ToList() ?? []);
    }

    /// <summary>"Deleted: name · Restore" rows under the Openings list while Add opening is armed.</summary>
    void RefreshDeletedRows()
    {
        var log = DeletedOpenings();
        DeletedList.ItemsSource = addingOpening ? log.Select((d, i) => new DeletedRow(i, "Deleted: " + d.Opening.Name)).ToList() : null;
        DeletedBox.IsVisible = addingOpening && log.Count > 0;
    }

    void OnRestoreDeleted(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: int index }) RestoreDeleted(index);
    }

    /// <summary>Brings the original opening back (same object: size, type and Cd as it was) and takes its fill out, whatever was edited since. Stays armed so several can come back.</summary>
    void RestoreDeleted(int index)
    {
        var log = DeletedOpenings();
        if ((uint)index >= (uint)log.Count) return;
        if (locked) { StatusText.Text = UiTable.LockedWhy; return; }
        var d = log[index];
        log.RemoveAt(index);
        ClearUndo();
        if (!model.Openings.Contains(d.Opening))
        {
            OpeningEdit.Restore(model, d);
            int at = model.Openings.IndexOf(d.Opening);
            ShiftOpeningIndexes(at, +1);
            SelectOpening(at);
        }
        ApplyOpeningChange($"{d.Opening.Name} restored");
    }

    /// <summary>The next edit makes the kept deletion unrestorable (the model has moved on).</summary>
    void ClearUndo()
    {
        undoDelete = null;
        undoRoom = null;
        StatusUndoButton.IsVisible = false;
    }

    /// <summary>Indexes into model.Openings that the UI holds (the selection, the sweep ticks) after one at <paramref name="at"/> left (-1) or came back (+1).</summary>
    void ShiftOpeningIndexes(int at, int by)
    {
        var r = Viewport3D.Renderer;
        r.SelectedOpening = by < 0 ? (r.SelectedOpening == at ? -1 : r.SelectedOpening > at ? r.SelectedOpening - 1 : r.SelectedOpening)
                                   : (r.SelectedOpening >= at ? r.SelectedOpening + 1 : r.SelectedOpening);
        var shifted = sweepPicks.Where(i => by > 0 || i != at).Select(i => i >= at + (by > 0 ? 0 : 1) ? i + by : i).ToList();
        sweepPicks.Clear();
        foreach (int i in shifted) sweepPicks.Add(i);
        UpdateSweepHint();
    }

    // ---------------------------------------------------------------- size and type of an added opening

    /// <summary>The opening an inspector box (Width / Height / Sill / Cd) was opened on. The selection can move while a box still has focus (a click on a row does not take it);
    /// the commit on blur must still land on the opening whose number was typed, not the new selection.</summary>
    Opening? inspTarget;

    void OnInspBoxFocus(object? sender, FocusChangedEventArgs e) => inspTarget = SelectedOpening();

    /// <summary>The box's target while committing: the opening it was opened on, if it is still in the model, else the selection.</summary>
    Opening? InspTarget() => inspTarget is { } t && model.Openings.Contains(t) ? t : SelectedOpening();

    Opening? SelectedOpening() =>
        (uint)Viewport3D.Renderer.SelectedOpening < (uint)model.Openings.Count ? model.Openings[Viewport3D.Renderer.SelectedOpening] : null;

    void OnOpeningDimBox(object? sender, RoutedEventArgs e) { CommitDim(sender); inspTarget = null; }

    void OnOpeningDimKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitDim(sender);
        e.Handled = true;
    }

    /// <summary>The inspector's Width / Height / Sill box (Tag names the field) edits the opening it was opened on; a detected one is read-only.</summary>
    void CommitDim(object? sender)
    {
        if (sender is not TextBox { Tag: string field, IsReadOnly: false } box || InspTarget() is not { } op || locked) return;
        (double lo, double hi) = field == "Sill" ? (0d, 3d) : (0.2d, 6d);
        if (!NumInput.Check(box, field, lo, hi, " m", out double v)) return;
        float before = field switch { "Width" => op.Width, "Height" => op.Height, _ => OpeningEdit.Sill(model, op) };
        if (MathF.Abs((float)v - before) > 1e-4f)
        {
            if (OpeningEdit.Set(model, op, field, (float)v))
            {
                ClearUndo();
                ApplyOpeningChange($"{op.Name}: {field.ToLowerInvariant()} {OpeningEdit.Text((float)v)} m");
            }
            else
            {
                string why = $"{op.Name}: {OpeningEdit.Text((float)v)} m {field.ToLowerInvariant()} does not fit (the free wall, or the storey height)";
                StatusText.Text = why;
                NumInput.Mark(box, why + ". The last valid value is kept.");
                return;
            }
        }
        if (ReferenceEquals(op, SelectedOpening()))
            box.Text = OpeningEdit.Text(field switch { "Width" => op.Width, "Height" => op.Height, _ => OpeningEdit.Sill(model, op) });
        else RefreshInspector();            // the box now shows another opening: repaint it from that one
    }

    void OnOpeningType(object? sender, SelectionChangedEventArgs e)
    {
        int index = Viewport3D.Renderer.SelectedOpening;
        if (syncingInspector || sender is not ComboBox box || (uint)index >= (uint)model.Openings.Count || locked) return;
        var op = model.Openings[index];
        var kind = box.SelectedIndex == 1 ? OpeningKind.Door : OpeningKind.Window;
        if (op.Kind == kind || !OpeningEdit.IsUser(op)) return;
        ClearUndo();
        bool auto = OpeningEdit.IsAutoName(op);
        op.Kind = kind;
        if (auto) op.Name = OpeningEdit.NextName(model, kind);   // "Window 2" -> "Door 1"
        ApplyOpeningChange($"{op.Name}: now a {kind.ToString().ToLowerInvariant()}");
    }

    // ---------------------------------------------------------------- shared

    void SelectOpening(int index)
    {
        var r = Viewport3D.Renderer;
        ClearMulti();
        r.SelectedRoom = -1;
        r.SelectedOpening = index;
        ApplyIsolation();
    }

    /// <summary>The model's openings changed (added, deleted, restored, resized): the shape of the scene and of the solve changed. The
    /// same path as opening or closing a window: GeometryChanged re-voxelises on the new model and Core's UpdateCells rebuilds the opening
    /// flux plan from it (SetupRoomFlux). Reconfigure would restart on the old grid, where the new hole is still wall and reads 0 m³/s.</summary>
    void ApplyOpeningChange(string what)
    {
        Log.Info("opening change: " + what);
        statsVersion = -1;
        InvalidateSpaces();                                  // the detected spaces were found on the old model
        RebuildScene();
        RefreshDeletedRows();
        if (live is { Active: true } solver)
        {
            solver.GeometryChanged(model, "opening: " + what);
            StatusText.Text = what + " · applying…";
        }
        else
        {
            if (result != null) stale = true;
            StatusText.Text = what + " · flow is stale, press Run";
        }
        RefreshPanel();
    }
}

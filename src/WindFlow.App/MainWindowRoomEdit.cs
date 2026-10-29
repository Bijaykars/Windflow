using Avalonia.Controls;
using Avalonia.Interactivity;
using WindFlow.App.Import;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>Add, delete and merge rooms by hand (the model edits are RoomEdit's), the way openings do it (MainWindowOpenings): Add reuses the device placement click
/// (Viewport3D.DevicePlaceMode -> OnDevicePlaced; the spaces it can make are MainWindowSpaces'), the status strip's Undo restores the last edit until the next one, and every edit goes down the
/// GeometryChanged path (ApplyOpeningChange), which re-voxelises and rebuilds the room masks and the room-flux plan. Ctrl+click (rows or the 3D view)
/// builds the multi-selection a merge works on.</summary>
public partial class MainWindow
{
    bool addingRoom;                    // "Add room" is armed: the next click inside a space makes it a room
    Func<string>? undoRoom;             // the last room edit's inverse (returns the status text), until the next edit
    readonly List<string> multiRooms = [];   // ids of the Ctrl+click selection, first-selected first

    // ---------------------------------------------------------------- add

    void OnRoomAdd(object? sender, RoutedEventArgs e)
    {
        if (locked) { StatusText.Text = UiTable.LockedWhy; return; }
        if (addingRoom) { CancelPlacement(); StatusText.Text = "placement cancelled"; return; }
        CancelPlacement();
        addingRoom = true;
        Viewport3D.DevicePlaceMode = true;
        RoomAddButton.Classes.Set("placing", true);
        RoomAddButton.Content = "Click inside a space · Esc";
        StatusText.Text = "Click inside a space to make it a room · Esc to cancel";
        KeyHints.Text = "Esc  cancel";
        EnsureSpaces();                 // the spaces are found once and kept (MainWindowSpaces); they are drawn and listed while this is armed
        RebuildScene();
    }

    /// <summary>Leaves the add mode (no-op when not in it); CancelPlacement and BeginPlacement call it so the modes never overlap.</summary>
    void EndAddRoom()
    {
        if (!addingRoom) return;
        addingRoom = false;
        Viewport3D.DevicePlaceMode = false;
        RoomAddButton.Classes.Set("placing", false);
        RoomAddButton.Content = "+ Add room";
        Viewport3D.Renderer.HoverSpace = -1;
        RefreshSpaceList();
        RefreshInspector();
    }

    /// <summary>The click: resolve the ray against the cached candidates (RoomEdit.Resolve: floor, walls or ceiling from inside all count) and make the one it lands in a room.
    /// The candidates come from MainWindowSpaces (found once, off the UI thread), so a click costs nothing; if they are not found yet it says so.</summary>
    void PlaceRoom(int px, int py)
    {
        if (locked) { StatusText.Text = UiTable.LockedWhy; return; }
        if (!Viewport3D.Renderer.ScreenRay(px, py, out var eye, out var dir)) return;
        if (spaces == null || !ReferenceEquals(spacesModel, model)) { StatusText.Text = findingSpaces ? FindingText + " one moment" : FindingText; EnsureSpaces(); return; }
        if (RoomEdit.Resolve(model, spaces, eye, dir, out string why) is not { } candidate) { StatusText.Text = why + " (Esc cancels)"; return; }
        CreateRoomFromSpace(candidate);
    }

    // ---------------------------------------------------------------- delete

    /// <summary>The row's × (Tag = its index) or the inspector's Delete (the selected room).</summary>
    void OnRoomDelete(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        DeleteRoom(sender is Button { Tag: int index } ? index : Viewport3D.Renderer.SelectedRoom);
    }

    void DeleteRoom(int index) => DeleteRooms((uint)index < (uint)model.Rooms.Count ? [model.Rooms[index]] : []);

    /// <summary>Deletes every room in the list as one edit: one status line, one Undo that restores them all.</summary>
    void DeleteRooms(List<Room> rooms)
    {
        if (rooms.Count == 0) return;
        if (locked) { StatusText.Text = UiTable.LockedWhy; return; }
        ClearUndo();
        string name = rooms.Count == 1 ? DisplayName(rooms[0].Id, rooms[0].Name) : $"{rooms.Count} rooms";
        var done = rooms.Select(r => RoomEdit.Delete(model, r)).ToList();
        ClearRoomSelection();
        undoRoom = () =>
        {
            for (int i = done.Count - 1; i >= 0; i--) RoomEdit.Restore(model, done[i]);
            if (done.Count == 1) SelectRoom(model.Rooms.IndexOf(done[0].Room), frame: false);
            else ClearRoomSelection();      // the restored rooms moved every later index: a held selection would name another room
            return $"{name} restored";
        };
        StatusUndoButton.IsVisible = true;
        ApplyRoomChange($"{name} deleted");
    }

    // ---------------------------------------------------------------- merge

    /// <summary>The Ctrl+click selection as rooms, first-selected first.</summary>
    List<Room> MultiRooms() => multiRooms.Select(id => model.Rooms.FirstOrDefault(r => r.Id == id)).OfType<Room>().ToList();

    /// <summary>Ctrl+click on a room row or floor: add it to the selection (starting from the selected room), or take it out again.</summary>
    void ToggleMultiRoom(int index)
    {
        if ((uint)index >= (uint)model.Rooms.Count) return;
        var r = Viewport3D.Renderer;
        if (multiRooms.Count == 0 && (uint)r.SelectedRoom < (uint)model.Rooms.Count) multiRooms.Add(model.Rooms[r.SelectedRoom].Id);
        string id = model.Rooms[index].Id;
        if (!multiRooms.Remove(id)) multiRooms.Add(id);
        if (multiRooms.Count == 0) { ClearSelection(); return; }
        if (multiRooms.Count == 1)
        {
            int only = model.Rooms.FindIndex(x => x.Id == multiRooms[0]);
            multiRooms.Clear();
            SelectRoom(only, frame: false);
            return;
        }
        r.SelectedOpening = -1;
        r.SelectedRoom = -1;
        r.ExtraRooms = MultiRooms().Select(x => model.Rooms.IndexOf(x)).ToArray();
        ApplyIsolation();
        statsVersion = -1;
        RefreshPanel();
    }

    /// <summary>Forgets the multi-selection (SelectRoom, SelectOpening and ClearSelection call it).</summary>
    void ClearMulti()
    {
        ClearSpaceSelection();
        multiRooms.Clear();
        Viewport3D.Renderer.ExtraRooms = [];
    }

    /// <summary>After a delete, or an undo of an add: nothing selected, nothing isolated.</summary>
    void ClearRoomSelection()
    {
        ClearMulti();
        Viewport3D.Renderer.SelectedRoom = -1;
        isolateOn = false;
        ApplyIsolation();
    }

    void OnRoomMerge(object? sender, RoutedEventArgs e)
    {
        if (locked) { StatusText.Text = UiTable.LockedWhy; return; }
        var rooms = MultiRooms();
        if (RoomEdit.CannotMerge(rooms) is { } cannot) { StatusText.Text = cannot; return; }
        ClearUndo();
        string name = DisplayName(rooms[0].Id, rooms[0].Name);
        if (RoomEdit.Merge(model, rooms, out string why) is not { } merged) { StatusText.Text = why; return; }
        ClearMulti();
        undoRoom = () =>
        {
            RoomEdit.Unmerge(model, merged);
            ClearRoomSelection();
            return $"{rooms.Count} rooms restored";
        };
        StatusUndoButton.IsVisible = true;
        SelectRoom(model.Rooms.IndexOf(merged.Result), frame: false);
        ApplyRoomChange($"{rooms.Count} rooms merged into {name}");
    }

    // ---------------------------------------------------------------- shared

    /// <summary>The model's rooms changed (added, deleted, merged, undone): names and order first, then the same GeometryChanged path an opening edit takes,
    /// which re-voxelises (the enclosure seal reads the room loops) and rebuilds the room masks and the opening flux plan.</summary>
    void ApplyRoomChange(string what)
    {
        RecomputeRoomNames();
        ApplyOpeningChange(what);
    }
}

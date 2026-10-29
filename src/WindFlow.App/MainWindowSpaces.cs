using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WindFlow.App.Import;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>The Rooms section's "Spaces without a room": the importer's detector (RoomDetect) runs ONCE per model state on a snapshot off the UI thread, whenever the Rooms section shows or Add room is
/// armed, and the enclosed spaces that are not a room yet (RoomEdit.Candidates) are kept until the model changes. They are listed with an Add button (the no-aiming way), drawn in 3D
/// (Scene overlay, the picked one in the violet selection style), and Add room's click and hover resolve against them (RoomEdit.Resolve): no detection per click or hover.
/// Plan view: the 3D viewport has no top-down mode that follows a storey (Plan is a report image only), so placing does not switch views.</summary>
public partial class MainWindow
{
    List<Room>? spaces;                    // the candidates for spacesModel at spacesGen; null = not found yet
    BuildingModel? spacesModel;
    int spacesGen;                         // bumped by every model edit: a detection that started before it is dropped
    bool findingSpaces, spacesFailed;
    int spaceSelected = -1;                // index into spaces: the row picked in the list
    const string FindingText = "Finding spaces…";

    /// <summary>The model changed (any opening or room edit, an undo): the candidates are stale. The next RefreshPanel finds them again if the Rooms section shows.</summary>
    void InvalidateSpaces()
    {
        spacesGen++;
        spaces = null;
        spacesFailed = false;
        ClearSpaceSelection();
    }

    void ClearSpaceSelection()
    {
        spaceSelected = -1;
        Viewport3D.Renderer.SelectedSpace = -1;
        Viewport3D.Renderer.HoverSpace = -1;
    }

    /// <summary>Detect the candidates when they are wanted (Rooms section showing, or Add room armed) and not known for this model state. Off the UI thread; cheap when nothing is needed.</summary>
    async void EnsureSpaces()
    {
        if (!(addingRoom || RoomsPane.IsVisible) || findingSpaces || model.Obstacles.Count == 0) return;
        if (!ReferenceEquals(spacesModel, model)) { spacesModel = model; InvalidateSpaces(); }
        if (spaces != null || spacesFailed) return;
        findingSpaces = true;
        int gen = spacesGen;
        var origin = model;
        StatusText.Text = FindingText;
        RefreshSpaceList();
        List<Room>? found = null;
        try
        {
            var snapshot = LiveSolver.Snapshot(model);      // the detector voxelises for seconds while edits go on: it reads its own copy, as the solver does
            var detected = await Task.Run(() => RoomDetect.Detect(snapshot));
            found = RoomEdit.Candidates(model, detected);
        }
        catch (Exception ex)
        {
            Log.Info("find spaces: detection failed: " + ex.Message);
            if (ReferenceEquals(origin, model) && gen == spacesGen) { spacesFailed = true; StatusText.Text = "could not look for spaces: " + ex.Message; }
        }
        finally { findingSpaces = false; }
        if (!ReferenceEquals(origin, model) || gen != spacesGen)
        {
            RefreshSpaceList();
            EnsureSpaces();                                  // the model moved on while this ran: look again on the new state
            return;
        }
        if (found != null)
        {
            spaces = found;
            Log.Info($"find spaces: {found.Count} without a room");
            if (StatusText.Text == FindingText)
                StatusText.Text = addingRoom ? (found.Count > 0 ? "Click inside a space, or Add one from the list · Esc to cancel" : "No enclosed space without a room · Esc to cancel") : "";
        }
        RebuildScene();
        RefreshSpaceList();
    }

    /// <summary>The candidate under the cursor while placing: the violet-free hover highlight and the status line (what a click does, or why it would not).</summary>
    void HoverSpaceAt(int px, int py)
    {
        var r = Viewport3D.Renderer;
        if (spaces == null || !r.ScreenRay(px, py, out var eye, out var dir)) return;
        var c = RoomEdit.Resolve(model, spaces, eye, dir, out string why);
        r.HoverSpace = c == null ? -1 : spaces.IndexOf(c);
        StatusText.Text = c == null ? why + " · Esc cancels" : $"Click to add this {SpaceTitle(c)} as a room · Esc cancels";
    }

    string SpaceTitle(Room c)
    {
        string storey = model.Storeys.FirstOrDefault(s => s.Id == c.Level)?.Name ?? c.Level;
        return $"{(storey.Length > 0 ? storey + " · " : "")}{RoomNames.FloorArea(c):0.#} m²";
    }

    void RefreshSpaceList()
    {
        bool known = spaces != null && ReferenceEquals(spacesModel, model);
        int n = known ? spaces!.Count : 0;
        SpacesBox.IsVisible = findingSpaces || n > 0 || (known && addingRoom);
        SpacesHeader.Text = findingSpaces && !known ? FindingText : $"Spaces without a room ({n})";
        SpaceList.ItemsSource = known ? spaces!.Select((c, i) => new SpaceRow(i, SpaceTitle(c), i == spaceSelected)).ToList() : null;
    }

    /// <summary>A row of the list: pick it (violet in 3D, camera frames it); a second click clears.</summary>
    void OnSpaceRow(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: int index }) return;
        e.Handled = true;
        PickSpace(index);
    }

    void PickSpace(int index)
    {
        if (spaces == null || (uint)index >= (uint)spaces.Count) return;
        if (spaceSelected == index) { ClearSpaceSelection(); RefreshSpaceList(); RebuildScene(); return; }
        ClearSelection();
        spaceSelected = index;
        Viewport3D.Renderer.SelectedSpace = index;
        Viewport3D.Renderer.FrameSpace(index);
        StatusText.Text = $"{SpaceTitle(spaces[index])}: Add makes it a room";
        RefreshSpaceList();
    }

    void OnSpaceAdd(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: int index } && spaces != null && (uint)index < (uint)spaces.Count) CreateRoomFromSpace(spaces[index]);
    }

    /// <summary>The one place a candidate becomes a room (the list's Add and the placement click): one status line, one Undo.</summary>
    void CreateRoomFromSpace(Room candidate)
    {
        if (locked) { StatusText.Text = UiTable.LockedWhy; return; }
        var room = RoomEdit.CreateFrom(model, candidate, candidate.Level);
        EndAddRoom();
        ClearUndo();
        undoRoom = () =>
        {
            model.Rooms.Remove(room);
            ClearRoomSelection();
            return $"{room.Name} removed";
        };
        StatusUndoButton.IsVisible = true;
        SelectRoom(model.Rooms.IndexOf(room), frame: false);
        ApplyRoomChange($"{room.Name} added");
    }
}

public sealed record SpaceRow(int Index, string Title, bool Selected);
public sealed record DeletedRow(int Index, string Label);

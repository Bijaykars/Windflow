namespace WindFlow.Core;

/// <summary>S4-06: the sentences for what an exporter had to drop or guess, so the app can show them (<see cref="BuildingModel.Warnings"/>). The Revit
/// add-in cannot run headless, so the wording lives here where it can be tested.</summary>
public static class ExtractionNotes
{
    const int ShowIds = 5;

    static string List(IReadOnlyList<string> items) =>
        string.Join(", ", items.Take(ShowIds)) + (items.Count > ShowIds ? $" and {items.Count - ShowIds} more" : "");

    /// <summary>Elements whose geometry Revit would not give: they are missing from the obstacles, so a wall, slab or roof may be absent.</summary>
    public static string DroppedGeometry(IReadOnlyList<string> ids) =>
        $"{ids.Count} model element{(ids.Count == 1 ? "" : "s")} had no usable geometry and {(ids.Count == 1 ? "was" : "were")} left out of the airflow model " +
        $"(a missing wall, slab or roof changes the result): {List(ids)}";

    /// <summary>P15-05: a Revit room's top is its upper limit (default 8 ft = 2.44 m above the level), whatever the ceiling is. A limit below the storey's
    /// top shrinks the nominal volume and turns the ceiling into plain-air faces (measured +23 % ACH at a 2.44 m limit under a 3.0 m ceiling), so the top is raised
    /// to the storey's top (next level's elevation, which includes the slab: a few percent of nominal volume over the true floor-to-ceiling, which the add-in cannot read).
    /// RX-07: but never past any Revit level above the room's base, story or not (a mezzanine at 3 m under a 6 m storey caps the room at 3 m), so it cannot reach into
    /// a disconnected space. <paramref name="levelElevations"/>: every level of the document, metres.</summary>
    public static float RoomTop(float roomZMax, float roomBaseZ, float storeyElevation, float storeyHeight, IEnumerable<float> levelElevations)
    {
        if (storeyHeight <= 0f) return roomZMax;
        float top = storeyElevation + storeyHeight;
        // RY-02: only a level above the room's current top can cap it; a level the room already spans must not cancel the raise
        foreach (float e in levelElevations) if (e > roomBaseZ + 0.1f && e > roomZMax - 0.01f && e < top) top = e;   // 0.1 m: the same "next level" tolerance as RevitExtractor.Levels
        return MathF.Max(roomZMax, top);
    }

    /// <summary>Rooms whose upper limit sat below their storey's top and was raised to it (<see cref="RoomTop"/>).</summary>
    public static string RaisedRoomTops(IReadOnlyList<string> names) =>
        $"{names.Count} room{(names.Count == 1 ? "" : "s")} had an upper limit below the storey's top, so {(names.Count == 1 ? "it was" : "they were")} " +
        $"raised to the next level above (a low room top shrinks the volume and opens the ceiling to the air above): {List(names)}";

    /// <summary>Air terminals whose duct connector could not be read: each is taken as a supply blowing down, which is wrong for a return or exhaust grille.</summary>
    public static string DefaultedTerminals(IReadOnlyList<string> names) =>
        $"{names.Count} air terminal{(names.Count == 1 ? "" : "s")} had no readable duct connector, so {(names.Count == 1 ? "it was" : "they were")} " +
        $"assumed to be a supply blowing down (a return or exhaust grille would blow air in): {List(names)}";
}

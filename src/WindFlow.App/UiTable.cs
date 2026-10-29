using Avalonia.Controls;

namespace WindFlow.App;

/// <summary>One row of <see cref="UiTable"/>: a named control (usually a container) in MainWindow.axaml, whether it
/// exists only in Expert, and whether it changes the flow (disabled while the result is locked).</summary>
public readonly record struct UiRule(string Name, bool ExpertOnly, bool ChangesFlow);

/// <summary>PLAN_V2 4.3/4.4: the single source of truth for what Basic hides and what Lock disables. ApplyMode and
/// SetLocked drive the window from it, and --selfcheck reads the same rows against MainWindow.axaml, so the rule and
/// the UI cannot drift apart. Rows name containers where they can: a control inside an Expert-only container is
/// hidden with it, and a control inside a flow-changing container is disabled with it (Avalonia ANDs IsEnabled down
/// the tree), which leaves the controls' own IsVisible/IsEnabled free for the code that already manages them.</summary>
public static class UiTable
{
    public static readonly UiRule[] Rules =
    [
        // Expert only.
        new("ExpertViewTools", ExpertOnly: true, ChangesFlow: false),    // Box, Clip, Reset box
        new("ExportMenuButton", ExpertOnly: true, ChangesFlow: false),   // video export
        new("PanelToggle", ExpertOnly: true, ChangesFlow: false),
        new("ExpertPanes", ExpertOnly: true, ChangesFlow: false),        // Devices, View, Studies
        new("TrustDetail", ExpertOnly: true, ChangesFlow: false),        // in/out/residual numbers
        new("BoxV1Row", ExpertOnly: true, ChangesFlow: true),            // Settings: box is the whole domain (v1)
        new("GeometryText", ExpertOnly: true, ChangesFlow: false),       // status strip: triangles, cells, viewport size
        new("PerfText", ExpertOnly: true, ChangesFlow: false),           // status strip: frame time
        new("AccurateEditRow", ExpertOnly: true, ChangesFlow: true),     // Settings: typed Accurate cell budget
        new("OpeningModelRow", ExpertOnly: true, ChangesFlow: true),     // Settings: Calibrated / Resolved opening model
        new("WallModelRow", ExpertOnly: true, ChangesFlow: true),        // Settings: experimental wall model (isothermal only)
        new("DomainModeRow", ExpertOnly: true, ChangesFlow: true),       // Settings: Compact / Full (COST 732) wind domain (6c)
        new("SliceExpertControls", ExpertOnly: true, ChangesFlow: false), // slice field (pressure, temperature) and X/Y swap
        new("AccurateControls", ExpertOnly: false, ChangesFlow: true),   // Standard / High recipe, High's time budget and fine box
        new("RoomInspectorExpert", ExpertOnly: true, ChangesFlow: true),    // inspector: total exchange, people, Add opening to this room
        new("OpeningInspectorExpert", ExpertOnly: true, ChangesFlow: true), // inspector: Cd and its presets
        new("OpeningInspectorEdit", ExpertOnly: false, ChangesFlow: true),  // inspector: size, type, open/closed of the selected opening
        new("InspDeleteButton", ExpertOnly: false, ChangesFlow: true),      // inspector: delete the selected opening

        // Both modes, disabled while locked: anything that would change the flow on screen.
        new("OpenModelButton", ExpertOnly: false, ChangesFlow: true),
        new("SampleButton", ExpertOnly: false, ChangesFlow: true),
        new("WindGroup", ExpertOnly: false, ChangesFlow: true),
        new("RunButton", ExpertOnly: false, ChangesFlow: true),
        new("EnvironmentControls", ExpertOnly: false, ChangesFlow: true),
        new("HoldIndoorToggle", ExpertOnly: false, ChangesFlow: true),   // 6j: held indoor temperature (thermal only)
        new("DevicesPane", ExpertOnly: false, ChangesFlow: true),
        new("BoxToggle", ExpertOnly: false, ChangesFlow: true),
        new("BoxResetButton", ExpertOnly: false, ChangesFlow: true),
        new("BoxGrowButton", ExpertOnly: false, ChangesFlow: true),
        new("CompareActions", ExpertOnly: false, ChangesFlow: true),
        new("SweepActions", ExpertOnly: false, ChangesFlow: true),
        new("SweepList", ExpertOnly: false, ChangesFlow: true),
        new("AnnualActions", ExpertOnly: false, ChangesFlow: true),
        new("RoseCanvas", ExpertOnly: false, ChangesFlow: true),
        new("LiveLinkToggle", ExpertOnly: false, ChangesFlow: true),
        new("OpeningAddButton", ExpertOnly: false, ChangesFlow: true),
        new("RoomAddButton", ExpertOnly: false, ChangesFlow: true),
        new("RoomInspDeleteButton", ExpertOnly: false, ChangesFlow: true),  // inspector: delete the selected room
        new("MergeInspector", ExpertOnly: false, ChangesFlow: true),        // inspector: merge the Ctrl+click selection (RoomEdit.CannotMerge enables the button)
    ];

    /// <summary>The section picker's items: Basic gets the first three, Expert all five (SectionPicker, MainWindow). Rooms is the default on open.</summary>
    public static readonly string[] Sections = ["Rooms", "Openings", "View", "Devices", "Studies"];
    public static readonly string[] BasicSections = Sections[..3];

    /// <summary>The tooltip every flow control shows while it is greyed out.</summary>
    public const string LockedWhy = "Result locked: this would change the flow. Unlock (top bar) to edit again.";

    /// <summary>Interactive controls Basic is allowed to show, by x:Name, or by Content for an unnamed one. The
    /// selfcheck fails on any Basic-visible button, toggle, box, slider or check box missing from this list, so
    /// adding a control to Basic is a deliberate act (PLAN_V2 4.3: nothing else is visible).</summary>
    public static readonly string[] BasicControls =
    [
        "OpenModelButton", "SaveModelButton", "SampleButton", "WindDirBox", "WindSpeedBox", "RunButton", "LockToggle",
        "SpeedModeToggle", "StillModeToggle", "ReportButton", "BasicToggle", "ExpertToggle", "HelpButton", "GearButton",
        // panel
        "StillSlider", "StillParticlesToggle", "AchIntentToggle", "ThermalToggle", "IndoorBox", "OutdoorBox", "HoldIndoorToggle",
        "NotesToggle", "SectionPicker", "IsolateChip", "SliceHToggle", "SliceVToggle", "SliceZSlider", "SliceOtherSlider",
        "AccurateStandardToggle", "AccurateHighToggle", "AccurateUltraToggle", "UCap6Toggle", "UCap12Toggle", "UCap20Toggle", "UCap24Toggle", "Cap5Toggle", "Cap10Toggle", "Cap20Toggle", "Cap30Toggle", "FocusBoxToggle", "OpeningAddButton", "RoomAddButton", "StatusUndoButton",
        // inspector (the selected room or opening)
        "InspIsolateButton", "InspFrameButton", "InspOpeningFrameButton", "InspDeleteButton", "InspWidth", "InspHeight", "InspSill", "InspType", "RoomInspDeleteButton", "MergeButton",
        // settings popover
        "GpuSlider", "ParticleBox", "ParticleAutoButton", "ReopenLastToggle", "LiveLinkToggle", "LogFolderButton", "MethodButton", "TechRefButton", "TourButton", "LicenceButton", "LicencesButton",
        // empty state (no model yet)
        "EmptySampleButton", "Open model…", "EmptyTourButton",
    ];

    /// <summary>Controls PLAN_V2 4.4 puts in Expert only; each must be hidden in Basic through the table.</summary>
    public static readonly string[] ExpertControls =
    [
        "BoxToggle", "ClipToggle", "BoxResetButton", "BoxGrowButton", "BoxV1Toggle", "DevicePalette", "DeviceList", "GhostToggle",
        "ParticleToggle", "SpeedSlider", "FieldPressureToggle", "FieldTempToggle", "ProbeToggle",
        "HoldAButton", "HoldBButton", "CompareToggle", "SweepButton", "AnnualButton", "ExportMenuButton", "PanelToggle",
        "TrustDetail", "AccurateBox", "OpeningModelToggle", "WallModelToggle", "DomainModeToggle", "RoomInspPeople", "InspCd",
    ];

    /// <summary>Controls that change the flow; each must be disabled while locked through the table.</summary>
    public static readonly string[] FlowControls =
    [
        "OpenModelButton", "SampleButton", "WindDirBox", "WindSpeedBox", "RunButton", "ThermalToggle", "IndoorBox", "HoldIndoorToggle",
        "OutdoorBox", "DevicePalette", "DeviceList", "BoxToggle", "BoxResetButton", "BoxGrowButton", "BoxV1Toggle", "HoldAButton",
        "HoldBButton", "CompareToggle", "SweepButton", "SweepList", "AnnualButton", "Load EPW...", "RoseCanvas",
        "LiveLinkToggle", "AccurateBox", "OpeningModelToggle", "WallModelToggle", "DomainModeToggle",
        "AccurateStandardToggle", "AccurateHighToggle", "AccurateUltraToggle", "FocusBoxToggle", "OpeningAddButton", "RoomAddButton", "RoomInspDeleteButton", "MergeButton",
        "InspDeleteButton", "InspWidth", "InspHeight", "InspSill", "InspType", "InspCd", "RoomInspPeople",
    ];

    /// <summary>View controls that must stay live while locked (PDF export, the view, slice, highlight, unlock).</summary>
    public static readonly string[] ViewControls =
    [
        "LockToggle", "SpeedModeToggle", "StillModeToggle", "ReportButton", "ClipToggle", "SliceHToggle",
        "SliceVToggle", "ProbeToggle", "GhostToggle", "ParticleToggle", "StillSlider", "AchIntentToggle", "HelpButton", "GearButton",
        "BasicToggle", "ExpertToggle", "NotesToggle", "RoomList", "OpeningListView", "InspIsolateButton", "InspFrameButton", "InspOpeningFrameButton",
    ];

    public static bool Visible(UiRule rule, bool expert) => !rule.ExpertOnly || expert;

    public static bool Enabled(UiRule rule, bool locked) => !(rule.ChangesFlow && locked);

    /// <summary>Resolves every row once; a missing name is a build-time mistake and throws on first use.</summary>
    public static (UiRule Rule, Control Control)[] Resolve(Control root)
    {
        var found = new (UiRule, Control)[Rules.Length];
        for (int i = 0; i < Rules.Length; i++)
            found[i] = (Rules[i], root.FindControl<Control>(Rules[i].Name)
                ?? throw new InvalidOperationException($"UiTable: no control named {Rules[i].Name} in the window"));
        return found;
    }
}

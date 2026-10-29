using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace WindFlow.App;

/// <summary>One coach mark: a title, one to three plain sentences, and the controls it points at (none = a centred card).
/// A target that is hidden, collapsed or scrolled away at that moment falls back to the centred card. <paramref name="Enter"/>
/// may change the VIEW (panel section, cut, mode) so the step can show the real thing; the tour puts the view back when it
/// ends. An action step (<paramref name="ActionHint"/> + <paramref name="Done"/>) lets presses through the ring to the ringed
/// control only and advances by itself when <paramref name="Done"/> turns true. <paramref name="ClickThrough"/> alone lets
/// the user try the ringed controls while Next stays available.
/// <paramref name="Ready"/> defers <paramref name="Enter"/> until it turns true (the cut waits for the first field); <paramref name="Pressable"/>
/// can close the ring again once the action is under way (a second press on Run would pause it).</summary>
sealed record TourStep(string Title, string Body, Func<Control[]> Targets, Action? Enter = null,
                       Func<string>? ActionHint = null, Func<bool>? Done = null, bool ClickThrough = false,
                       Func<bool>? Ready = null, Func<bool>? Pressable = null);

/// <summary>The first-run guided tour. Twelve coach marks in a full-window popup (a popup is its own top-level window, so
/// it floats above the native D3D12 viewport, which nothing inside the window can cover). Shows once when the window
/// first opens (Settings.TourSeen), never in a headless run; replayed from Settings or the empty state. Hands-on: the user
/// loads the sample and starts the run through the ring; the tour never changes the model, the wind or a saved setting.</summary>
public partial class MainWindow
{
    TourOverlay? tour;
    int tourIndex;
    TourStep? tourStep;        // the step on screen (its Done predicate is checked by tourTimer)
    bool tourRinged;           // the step on screen pointed at a control (sidecar evidence for --shotmode tour)
    TourState tourSaved;       // the view as the user had it when the tour opened; put back by EndTour
    string tourRestoreNote = "";
    DateTime tourActionSince;
    readonly DispatcherTimer tourTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    static readonly TimeSpan TourActionPatience = TimeSpan.FromSeconds(15);   // then Next comes back so the tour never sticks

    /// <summary>What the tour may change and must put back: mode, panel section and the view toggles.</summary>
    readonly record struct TourState(bool Expert, int Tab, bool Still, bool Plan, bool Section)
    {
        public override string ToString() => $"{(Expert ? "Expert" : "Basic")} · section {UiTable.Sections[Math.Clamp(Tab, 0, UiTable.Sections.Length - 1)]} · {(Still ? "Still air" : "Speed")} · plan {(Plan ? "on" : "off")} · section cut {(Section ? "on" : "off")}";
    }

    TourState TourStateNow() => new(advancedMode, panelTab, StillModeToggle.IsChecked == true, SliceHToggle.IsChecked == true, SliceVToggle.IsChecked == true);

    bool TourLiveRunning => live is { State: LiveSolver.Mode.Running or LiveSolver.Mode.Paused };
    /// <summary>The run is going and the first velocity field has reached the particles (LiveSolver.Publishes, set right after
    /// particles.SetField): from here the Plan cut has real colour, not the flat sheet.</summary>
    bool TourFieldReady => TourLiveRunning && live!.Publishes > 0;
    bool tourEntered;          // the step on screen has run its Enter (deferred while its Ready predicate is false)

    TourStep[] TourSteps() =>
    [
        new("Welcome to WindFlow",
            "WindFlow shows how wind moves fresh air through each room of a building, through the windows and doors in your model. " +
            (EmptyState.IsVisible ? "This tour takes about two minutes: you load the sample house, start the air, and see where everything is. "
                                  : "This tour takes about two minutes and points out where things are on a real run. ") +
            "The five steps in the title bar, Model to Report, show where you are.",
            () => []),
        EmptyState.IsVisible
            ? new("Load a building",
                  "Click Sample building to load a two-storey house to try things on. " +
                  "Your own models open from the project menu at the top left (the button that names the model): a Revit export (.json) or plain geometry: OBJ, glTF, FBX, STL, 3DS, DAE or PLY.",
                  () => [EmptySampleButton], ActionHint: () => "Click Sample building to continue", Done: () => !EmptyState.IsVisible)
            : new("The project menu",
                  "This button names the model and opens the project menu: Open model\u2026 (Ctrl+O), Save model\u2026 (Ctrl+S), the sample house, " +
                  "and the exports: the PDF report and, in Expert, the videos. Open reads a Revit export (.json) or plain geometry: OBJ, glTF, FBX, STL, 3DS, DAE or PLY.",
                  () => [ProjectMenuButton]),
        new("Set the wind",
            "The wind pill reads where the wind comes from and how fast it blows: W 270\u00b0 is a wind from the west at 4 m/s, measured at 10 m height. " +
            "Click it for a compass you can turn, a speed slider and the exact numbers. You can also drag the arrow in the view.",
            () => [WindGroup]),
        new("Start the air",
            "Click Run to start the simulation. The view fills with moving particles: that is the air. " +
            "The preview is for looking and trying ideas; the room numbers firm up in a longer pass, described in a moment. Space pauses and resumes.",
            () => [RunButton], ActionHint: () => TourLiveRunning ? "Starting the air\u2026" : "Click Run to continue",
            Done: () => TourFieldReady, Pressable: () => !TourLiveRunning),
        new("Ways to look at the air",
            "Speed colours the moving air by how fast it goes; Still air maps the dead spots in amber instead. " +
            "Plan and Section cut through the air like a floor plan or a building section, coloured by the air at that cut. " +
            "Try them: these four buttons work through the ring.",
            () => [SpeedModeToggle, StillModeToggle, SliceHToggle, SliceVToggle],
            Enter: () => TourSet(SliceHToggle, true), Ready: () => TourFieldReady, ClickThrough: true),
        new("The rooms",
            "WindFlow finds the rooms in your model and lists them in the Rooms tab; after a run each shows its fresh-air result. " +
            "Click a room for its figures and a verdict in the box below, or Isolate to see that room on its own.",
            () => [RoomsPane], Enter: () => TourTab(TabRooms)),
        new("Openings: windows and doors",
            "The Openings tab lists every window and door. The switch opens or closes one, and the arrow says which way the air goes. " +
            "+ Add opening on the command bar, and the box below the list, add, resize or delete them. Door or window is only a label; the air treats both the same.",
            () => [SectionOpeningsTab, OpeningsPane], Enter: () => TourTab(TabOpenings)),
        new("How exact the numbers are",
            "Once the preview settles, a longer pass starts by itself; its progress shows at the bottom of the panel. Standard: a steady wind, up to 10 minutes. " +
            "High: a gusty wind, so each room shows a range, within a time budget you pick. " +
            "Ultra: the finest grid your graphics card fits, run for hours and saved as it goes; it asks before it starts.",
            () => [AccurateControls]),
        new("Export the report",
            "Export PDF writes the report: the room table, a plan of every analysed storey at 1.1 m, and a long section through the building. " +
            "It is also the last step, Report, in the title bar.",
            () => [ReportButton]),
        new("Basic, Expert and Settings",
            "Basic keeps to loading, wind, openings, running, rooms and the PDF. Expert adds two panel tabs, Devices and Studies, shown next. " +
            "The ? button opens the \u201cHow WindFlow works\u201d PDF, the Technical Reference and this tour again; the gear opens Settings: dark or light panels, and how much of the graphics card to use.",
            () => [BasicToggle, HelpButton, GearButton]),
        new("Devices (Expert)",
            "Ceiling and pedestal fans, split and cassette air conditioners, an exhaust fan, a kitchen hood and a ceiling diffuser. " +
            "Pick a tile, then click where it goes in a room; each one pushes or pulls air in the run.",
            () => [DevicesPane], Enter: () => { TourSetMode(true); TourTab(TabDevices); }),
        new("Studies (Expert)",
            "Compare holds two versions, A and B, and shows the change room by room. " +
            "Best windows tries combinations of up to 8 openings you tick and ranks them. " +
            "Annual runs a whole year of wind from a weather file (EPW).",
            () => [StudiesPane], Enter: () => { TourSetMode(true); TourTab(TabStudies); }),
    ];

    /// <summary>Settings and the empty state: replay the tour from the first step.</summary>
    void OnTakeTour(object? sender, RoutedEventArgs e) => StartTour();

    void StartTour(int step = 0)
    {
        SettingsPopup.IsOpen = false;
        if (tour != null && tour.Theme != Application.Current?.ActualThemeVariant) { TourPopup.Child = null; tour = null; }   // rebuilt in the new colours
        if (tour == null)
        {
            tour = new TourOverlay();
            tour.Back += () => { if (tourIndex > 0) TourGoTo(tourIndex - 1); };
            tour.Next += TourNext;
            tour.Skip += () => EndTour("skipped");
            TourPopup.Child = tour;
            TourPopup.PlacementTarget = (Control)Content!;
            tourTimer.Tick += TourTick;
            // the cutout and the card follow the window: recompute after the layout pass that a resize triggers
            SizeChanged += (_, _) => { if (TourPopup.IsOpen) Dispatcher.UIThread.Post(ShowTourStep, DispatcherPriority.Background); };
        }
        if (!TourPopup.IsOpen)
        {
            tourSaved = TourStateNow();
            Log.Info($"tour shown from step {step + 1} · first launch {!settings.TourSeen} · view {tourSaved}");
            TourPopup.IsOpen = true;
        }
        TourGoTo(step);
    }

    void TourNext()
    {
        if (tourIndex + 1 < TourSteps().Length) TourGoTo(tourIndex + 1);
        else EndTour("done");
    }

    /// <summary>Enter a step: its view change first, then the ring and the card over the result.</summary>
    void TourGoTo(int index)
    {
        tourTimer.Stop();
        tourIndex = Math.Clamp(index, 0, TourSteps().Length - 1);
        tourStep = TourSteps()[tourIndex];
        tourEntered = tourStep.Enter == null || (tourStep.Ready?.Invoke() ?? true);
        if (tourEntered) tourStep.Enter?.Invoke();
        // the view change above lays out on the next pass; ring and card are placed after it
        Dispatcher.UIThread.Post(() =>
        {
            ShowTourStep();
            tourActionSince = DateTime.UtcNow;
            if (!tourEntered || (tourStep?.Done is { } done && !done())) tourTimer.Start();
            tour?.FocusNext();
        }, DispatcherPriority.Background);
    }

    /// <summary>Every 250 ms while a step waits: a deferred Enter runs once its Ready turns true (and the ring is re-placed over the
    /// result); an action step advances when Done turns true, its hint and ring follow the state meanwhile, and after the
    /// patience Next comes back.</summary>
    void TourTick(object? sender, EventArgs e)
    {
        if (tour == null || !TourPopup.IsOpen || tourStep is not { } step) { tourTimer.Stop(); return; }
        if (!tourEntered && (step.Ready?.Invoke() ?? true))
        {
            tourEntered = true;
            step.Enter?.Invoke();
            Dispatcher.UIThread.Post(ShowTourStep, DispatcherPriority.Background);
        }
        bool pending = step.Done is { } done && !done();
        if (step.Done != null && !pending) { tourTimer.Stop(); TourNext(); return; }
        if (pending)
        {
            tour.SetAction(step.ActionHint?.Invoke(), step.Pressable?.Invoke() ?? true);
            if (DateTime.UtcNow - tourActionSince > TourActionPatience) { tourTimer.Stop(); tour.ShowNext(); return; }
        }
        if (tourEntered && !pending) tourTimer.Stop();
    }

    void EndTour(string how)
    {
        tourTimer.Stop();
        TourPopup.IsOpen = false;
        var changed = TourStateNow();
        TourRestore(tourSaved);
        tourRestoreNote = $"tour ended at step {tourIndex + 1} ({how}) · view during tour {changed} · restored to {TourStateNow()}" +
                          $" · saved mode {settings.Mode}{(TourLiveRunning ? " · run left running" : "")}";
        Log.Info(tourRestoreNote);
        if (Automated) return;      // a screenshot run must not flip the user's first-launch flag
        settings.TourSeen = true;
        settings.Save();
    }

    void ShowTourStep()
    {
        if (tour == null || tourStep is not { } step) return;
        var root = (Control)Content!;
        var rects = TourTargetRects(root, step.Targets());
        tourRinged = rects.Count > 0;
        bool pending = step.Done is { } done && !done();     // an action already taken (a run already going) reads as a plain step
        tour.Show(root.Bounds.Size, rects, step.Title, step.Body, $"{tourIndex + 1} / {TourSteps().Length}",
                  first: tourIndex == 0, last: tourIndex == TourSteps().Length - 1,
                  actionHint: pending ? step.ActionHint?.Invoke() : null,
                  clickThrough: (pending && (step.Pressable?.Invoke() ?? true)) || step.ClickThrough);
    }

    /// <summary>Each visible target with its on-screen rect in root (window) coordinates, clipped to every scroll viewer around it;
    /// empty when none is visible right now (hidden by the mode, no model, or scrolled away).</summary>
    static List<(Control Control, Rect Rect)> TourTargetRects(Control root, Control[] targets)
    {
        var found = new List<(Control, Rect)>();
        var screen = new Rect(root.Bounds.Size);
        foreach (var c in targets)
        {
            if (!c.IsEffectivelyVisible || !c.IsAttachedToVisualTree() || c.Bounds.Width <= 0 || c.Bounds.Height <= 0) continue;
            if (c.TranslatePoint(new Point(0, 0), root) is not { } at) continue;
            var r = new Rect(at, c.Bounds.Size);
            foreach (var sv in c.GetVisualAncestors().OfType<ScrollViewer>())
                if (sv.TranslatePoint(new Point(0, 0), root) is { } svAt) r = r.Intersect(new Rect(svAt, sv.Bounds.Size));
            r = r.Intersect(screen);
            if (r.Width < 4 || r.Height < 4) continue;
            found.Add((c, r));
        }
        return found;
    }

    // ---------------------------------------------------------------- view changes the tour makes, and undoes

    /// <summary>Mode for the tour only: the UI flips, the saved Mode does not (ApplyMode writes it; a close mid-tour keeps the user's).</summary>
    void TourSetMode(bool expert)
    {
        if (advancedMode == expert) return;
        string mode = settings.Mode;
        bool readOnly = settings.ReadOnly;
        settings.ReadOnly = true;
        ApplyMode(expert);
        settings.Mode = mode;
        settings.ReadOnly = readOnly;
    }

    void TourTab(int tab)
    {
        if (panelTab != tab) SelectTab(tab);
    }

    void TourRestore(TourState s)
    {
        TourSetMode(s.Expert);
        TourTab(s.Tab);
        if ((StillModeToggle.IsChecked == true) != s.Still) TourOverlay.Press(s.Still ? StillModeToggle : SpeedModeToggle);
        TourSet(SliceHToggle, s.Plan);
        TourSet(SliceVToggle, s.Section);
    }

    static void TourSet(ToggleButton t, bool on)
    {
        if ((t.IsChecked == true) != on) TourOverlay.Press(t);
    }

    /// <summary>--shot &lt;path&gt; --shotmode tour [--tour-step n]: Basic, from the empty state; the sample is shown before step 3 and the
    /// preview run started before step 5, as the user's own clicks would. Every step lands as &lt;stem&gt;01..12.png (or only step n at
    /// &lt;path&gt;), then the tour is skipped and &lt;stem&gt;-after-skip.png shows the view put back. The sidecar says what each step ringed.</summary>
    async Task TourShotSequence(string path)
    {
        ApplyMode(false);
        Viewport3D.Renderer.InputEnabled = false;
        ShowEmpty();
        await Task.Delay(1200);
        var steps = TourSteps();
        int only = Math.Clamp(Options.TourStep, 0, steps.Length), last = only > 0 ? only : steps.Length;
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        bool sampleShown = false, started = false;
        for (int i = 0; i < last; i++)
        {
            if (i >= 2 && !sampleShown) { ShowModel(SampleBuilding.Build()); sampleShown = true; await Task.Delay(1200); }
            if (i >= 4 && !started)
            {
                StartLive();
                started = true;
                for (int k = 0; k < 120 && !TourFieldReady; k++) await Task.Delay(250);   // the first field, so the cut has colour
                await Task.Delay(1500);                                                   // its upload and a few frames
            }
            if (only > 0 && i + 1 < only) continue;
            StartTour(i);
            await Task.Delay(900);
            shotNote = $"tour step {i + 1}/{steps.Length} \"{steps[i].Title}\" · {(tourRinged ? "ringed" : "centred, target not visible")}" +
                       $"{(tourStep?.ActionHint != null && tourStep.Done?.Invoke() == false ? " · action step, Next hidden" : "")} · card {tour!.CardRect} · view {TourStateNow()}";
            await SaveShell(only > 0 ? path : Path.Combine(dir, $"{stem}{i + 1:00}{ext}"));
        }
        EndTour("skipped");
        await Task.Delay(900);
        shotNote = tourRestoreNote;
        await SaveShell(Path.Combine(dir, $"{stem}-after-skip{ext}"));
        Close();
    }
}

/// <summary>The coach-mark layer: a dim over the whole window with a rounded cutout, a thin accent ring on the cutout and the
/// callout card beside it. Built from the app's own tokens (the card surface and border, the button classes, the link
/// style), read when the overlay is built, so it follows Dark / Light; nothing animates, nothing glows. A press inside the ring reaches the ringed control when the step allows it.</summary>
sealed class TourOverlay : Canvas
{
    const double Pad = 6, Gap = 12, Edge = 12, CardWidth = 320, WelcomeWidth = 400;

    readonly Avalonia.Controls.Shapes.Path dim = new() { Fill = Appearance.Brush("TourDim") };
    readonly Border ring = new()
    {
        BorderBrush = Appearance.Brush("Accent"), BorderThickness = new Thickness(1.5),
        CornerRadius = new CornerRadius(8), IsHitTestVisible = false,
    };
    readonly Border card = new()
    {
        Width = CardWidth, Background = Appearance.Brush("SurfaceRaised"), BorderBrush = Appearance.Brush("Border"),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 14, 18, 16),
    };
    readonly TextBlock title = new() { FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock counter = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    readonly TextBlock body = new() { FontSize = 12.5, LineHeight = 18, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    readonly TextBlock hint = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    readonly Button back = new() { Content = "Back" }, next = new() { Content = "Next" }, skip = new() { Content = "Skip tour" };
    List<(Control Control, Rect Rect)> ringed = [], pressable = [];

    public event Action? Back, Next, Skip;

    /// <summary>The theme the brushes were read under (StartTour rebuilds the overlay when it changes).</summary>
    public readonly Avalonia.Styling.ThemeVariant? Theme = Application.Current?.ActualThemeVariant;

    /// <summary>Where the card landed, window coordinates (shot sidecar).</summary>
    public Rect CardRect => new(GetLeft(card), GetTop(card), card.Bounds.Width, card.Bounds.Height);

    public TourOverlay()
    {
        Background = Brushes.Transparent;    // every press lands on the overlay; the card's buttons say what happens
        Focusable = true;
        title.Foreground = body.Foreground = Appearance.Brush("Text");
        counter.Foreground = hint.Foreground = Appearance.Brush("TextMuted");
        back.Classes.Add("secondary");
        next.Classes.Add("tool"); next.Classes.Add("accent"); next.MinHeight = 32;
        skip.Classes.Add("link");
        skip.VerticalAlignment = VerticalAlignment.Center;
        back.Click += (_, _) => Back?.Invoke();
        next.Click += (_, _) => Next?.Invoke();
        skip.Click += (_, _) => Skip?.Invoke();

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(title);
        Grid.SetColumn(counter, 1);
        head.Children.Add(counter);
        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(0, 14, 0, 0) };
        foot.Children.Add(skip);
        Grid.SetColumn(back, 2); back.Margin = new Thickness(0, 0, 6, 0);
        foot.Children.Add(back);
        Grid.SetColumn(next, 3);
        foot.Children.Add(next);
        Grid.SetColumn(hint, 3);
        foot.Children.Add(hint);
        card.Child = new StackPanel { Children = { head, body, foot } };
        Children.Add(dim);
        Children.Add(ring);
        Children.Add(card);
    }

    public void FocusNext()
    {
        if (card.IsKeyboardFocusWithin) return;
        if (next.IsVisible) next.Focus(); else Focus();   // Esc and Back still answer while an action step waits
    }

    /// <summary>An action step that did not happen within the patience: Next is offered so the tour never sticks.</summary>
    public void ShowNext()
    {
        hint.IsVisible = false;
        next.IsVisible = true;
        FocusNext();
    }

    /// <summary>An action step under way: the hint follows the state, and the ring can stop taking presses.</summary>
    public void SetAction(string? actionHint, bool clickThrough)
    {
        if (actionHint != null) hint.Text = actionHint;
        pressable = clickThrough ? ringed : [];
    }

    public void Show(Size size, List<(Control Control, Rect Rect)> targets, string titleText, string bodyText, string counterText,
                     bool first, bool last, string? actionHint, bool clickThrough)
    {
        Width = size.Width;
        Height = size.Height;
        title.Text = titleText;
        body.Text = bodyText;
        counter.Text = counterText;
        back.IsVisible = !first;
        next.Content = first ? "Start" : last ? "Done" : "Next";
        hint.Text = actionHint ?? "";
        hint.IsVisible = actionHint != null;
        next.IsVisible = actionHint == null;
        ringed = targets;
        pressable = clickThrough ? targets : [];
        Cursor = Cursor.Default;

        Rect? target = null;
        foreach (var (_, r) in targets) target = target is { } u ? u.Union(r) : r;
        card.Width = target == null ? WelcomeWidth : CardWidth;

        var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { new RectangleGeometry(new Rect(size)) } };
        Rect hole = default;
        if (target is { } t)
        {
            hole = t.Inflate(Pad);
            geometry.Children.Add(new RectangleGeometry(hole, 8, 8));
            SetLeft(ring, hole.X); SetTop(ring, hole.Y);
            ring.Width = hole.Width; ring.Height = hole.Height;
        }
        ring.IsVisible = target != null;
        dim.Data = geometry;

        card.Measure(new Size(card.Width, double.PositiveInfinity));
        var cs = card.DesiredSize;
        double w = size.Width, h = size.Height;
        Point pos;
        if (target == null) pos = new Point((w - cs.Width) / 2, (h - cs.Height) / 2);
        else
        {
            var below = new Point(hole.Center.X - cs.Width / 2, hole.Bottom + Gap);
            var above = new Point(hole.Center.X - cs.Width / 2, hole.Top - Gap - cs.Height);
            var left = new Point(hole.Left - Gap - cs.Width, hole.Center.Y - cs.Height / 2);
            var right = new Point(hole.Right + Gap, hole.Center.Y - cs.Height / 2);
            // top bar: below; the side panel: beside it, so the card does not cover the panel it talks about
            Point[] order = hole.Bottom < 96 ? [below, left, right, above] : hole.Center.X > w * 0.7 ? [left, below, above, right] : [below, above, right, left];
            bool Fits(Point p) => p.X >= Edge && p.Y >= Edge && p.X + cs.Width <= w - Edge && p.Y + cs.Height <= h - Edge;
            pos = order.FirstOrDefault(Fits, order[0]);
        }
        SetLeft(card, Math.Clamp(pos.X, Edge, Math.Max(Edge, w - cs.Width - Edge)));
        SetTop(card, Math.Clamp(pos.Y, Edge, Math.Max(Edge, h - cs.Height - Edge)));
    }

    /// <summary>The same path as a real click: a toggle flips first (ToggleButton.OnClick toggles before raising Click), then
    /// the control's Click handlers run with the control as sender.</summary>
    public static void Press(Control c)
    {
        if (c is ToggleButton t) t.IsChecked = t.IsChecked != true;
        if (c is Button b) b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    Control? PressableAt(Point p) => pressable.FirstOrDefault(x => x.Rect.Contains(p)).Control;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !card.IsPointerOver && PressableAt(e.GetPosition(this)) is { } c)
        {
            Press(c);
            e.Handled = true;
            return;
        }
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        Cursor = !card.IsPointerOver && PressableAt(e.GetPosition(this)) != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        base.OnPointerMoved(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Enter on a focused button is the button's own click (handled before it gets here)
        switch (e.Key)
        {
            case Key.Escape: Skip?.Invoke(); e.Handled = true; return;
            case Key.Right or Key.Enter or Key.Space: if (next.IsVisible) Next?.Invoke(); e.Handled = true; return;
            case Key.Left: if (back.IsVisible) Back?.Invoke(); e.Handled = true; return;
        }
        base.OnKeyDown(e);
    }
}

using System.Collections.Concurrent;
using System.IO;              // not in implicit usings for WPF projects
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using WindFlow.Core;

namespace WindFlow.Revit;

/// <summary>Ribbon button "Live link": toggles the named-pipe connection to WindFlow.App on/off for the active
/// document. See <see cref="LiveLink"/> for the actual protocol/thread machinery.</summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class LiveLinkToggleCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var doc = commandData.Application.ActiveUIDocument?.Document;
        if (doc == null) { message = "Open a Revit project first."; return Result.Failed; }

        LiveLink.Toggle(LiveLinkButton!, doc);
        return Result.Succeeded;
    }

    internal static PushButton? LiveLinkButton;
}

/// <summary>Live link to the standalone WindFlow.App over a named pipe. See docs/LIVELINK.md for the wire
/// protocol. This class owns all the state; the ribbon button just calls <see cref="Toggle"/>.
/// Threading: Revit callbacks (DocumentChanged/ViewActivated/DocumentClosing) run on Revit's API thread and only
/// ever schedule work; the actual re-extraction runs on that same thread via an <see cref="ExternalEvent"/>
/// (required -- BuildingModel extraction touches the Revit document). The pipe itself is served on its own
/// background thread so a slow/dead app never blocks Revit.</summary>
static class LiveLink
{
    const string PipeName = "WindFlow.LiveLink";

    static ExternalEvent? _extractEvent;
    static ExtractHandler? _extractHandler;
    static ExternalEvent? _selectEvent;
    static SelectHandler? _selectHandler;
    static ExternalEvent? _statusEvent;
    static StatusHandler? _statusHandler;

    static UIControlledApplication? _uiApp;
    static Document? _doc;
    static Thread? _pipeThread;
    static CancellationTokenSource? _cts;
    static BlockingCollection<string>? _outbox;   // one pipe-thread lifetime per Start()
    static System.Threading.Timer? _debounce;
    static volatile bool _pendingFull;

    public static bool Enabled { get; private set; }

    /// <summary>Called once from App.OnStartup. Subscribes the (cheap, always-on) Revit event hooks and creates
    /// the two ExternalEvents; none of this does anything until <see cref="Toggle"/> turns the link on.</summary>
    public static void Initialize(UIControlledApplication uiApp)
    {
        _uiApp = uiApp;
        _extractHandler = new ExtractHandler();
        _extractEvent = ExternalEvent.Create(_extractHandler);
        _selectHandler = new SelectHandler();
        _selectEvent = ExternalEvent.Create(_selectHandler);
        _statusHandler = new StatusHandler();
        _statusEvent = ExternalEvent.Create(_statusHandler);

        uiApp.ControlledApplication.DocumentChanged += OnDocumentChanged;
        uiApp.ControlledApplication.DocumentClosing += OnDocumentClosing;
        uiApp.ViewActivated += OnViewActivated;
    }

    public static void Shutdown()
    {
        if (Enabled) Stop();
    }

    /// <summary>Ribbon button handler. Flips the link on/off and updates the button text.</summary>
    public static void Toggle(PushButton button, Document doc)
    {
        if (Enabled) { Stop(); button.ItemText = "Live link\n(off)"; return; }
        _doc = doc;
        _statusButton = button;
        button.ItemText = "Live link\n(connecting)";
        Start();
    }

    static PushButton? _statusButton;
    /// <summary>Ribbon buttons are Revit UI objects and must only be touched from the API thread; the pipe thread
    /// hands the text over via an ExternalEvent rather than writing to <see cref="_statusButton"/> directly.</summary>
    static void SetStatus(string text)
    {
        if (_statusHandler == null) return;
        _statusHandler.Text = text;
        try { _statusEvent?.Raise(); } catch { /* app shutting down */ }
    }

    sealed class StatusHandler : IExternalEventHandler
    {
        public volatile string Text = "";
        public void Execute(UIApplication app)
        {
            try { if (_statusButton != null) _statusButton.ItemText = "Live link\n" + Text; } catch { /* button disposed with the doc */ }
        }
        public string GetName() => "WindFlow live link status";
    }

    static void Start()
    {
        Enabled = true;
        _pendingFull = false;
        _cts = new CancellationTokenSource();
        _outbox = new BlockingCollection<string>();
        _debounce = new System.Threading.Timer(_ => RequestExtract(), null, Timeout.Infinite, Timeout.Infinite);
        _pipeThread = new Thread(() => PipeLoop(_cts.Token)) { IsBackground = true, Name = "WindFlow.LiveLink" };
        _pipeThread.Start();
        ScheduleFullExtract();
    }

    static void Stop()
    {
        Enabled = false;
        _debounce?.Dispose();
        _debounce = null;
        _cts?.Cancel();
        _outbox?.CompleteAdding();
        _pipeThread?.Join(TimeSpan.FromSeconds(2));
        _cts?.Dispose();
        _cts = null;
        _outbox = null;
        _pipeThread = null;
    }

    // ---- Revit-side event hooks (API thread) ----

    static void OnViewActivated(object? sender, ViewActivatedEventArgs e)
    {
        if (!Enabled) return;
        if (e.Document != _doc) return;
        ScheduleFullExtract();
    }

    static void OnDocumentClosing(object? sender, DocumentClosingEventArgs e)
    {
        if (!Enabled || e.Document != _doc) return;
        Enqueue("{\"type\":\"close\"}");
        Stop();
        SetStatus("(waiting for WindFlow)");
    }

    static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (!Enabled || e.GetDocument() != _doc) return;

        var added = e.GetAddedElementIds();
        var deleted = e.GetDeletedElementIds();
        var modified = e.GetModifiedElementIds();

        if (added.Count == 0 && deleted.Count == 0 && modified.Count == 1 && TryCheapOpeningToggle(_doc!, modified.First(), out var msg))
        {
            Enqueue(msg);
            return;
        }
        ScheduleFullExtract();
    }

    /// <summary>Best-effort: recognise a single door/window family instance whose only change is an
    /// open/closed-style yes-no instance parameter (name containing "open"), and report it as the cheap
    /// "opening" message instead of a full re-extraction. Anything else (geometry, host wall, a parameter we
    /// don't recognise) falls back to the caller re-extracting the whole model.</summary>
    static bool TryCheapOpeningToggle(Document doc, ElementId id, out string message)
    {
        message = "";
        var elem = doc.GetElement(id);
        if (elem is not FamilyInstance fi) return false;
        var cat = fi.Category?.BuiltInCategory;
        if (cat != BuiltInCategory.OST_Doors && cat != BuiltInCategory.OST_Windows) return false;

        foreach (Parameter p in fi.Parameters)
        {
            if (p.StorageType != StorageType.Integer) continue;
            if (p.Definition.Name.IndexOf("open", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var isOpen = p.AsInteger() != 0;
            message = $"{{\"type\":\"opening\",\"id\":\"{JsonEscape(fi.UniqueId)}\",\"isOpen\":{(isOpen ? "true" : "false")}}}";
            return true;
        }
        return false;
    }

    static void ScheduleFullExtract()
    {
        _pendingFull = true;
        _debounce?.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }

    static void RequestExtract()
    {
        if (!Enabled || !_pendingFull) return;
        _pendingFull = false;
        _extractEvent?.Raise();
    }

    /// <summary>Runs on Revit's API thread (ExternalEvent). Re-extracts the whole building and hands the JSON
    /// to the pipe thread.</summary>
    sealed class ExtractHandler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            if (!Enabled || _doc == null) return;
            try
            {
                var model = Exporter.ExtractWhole(_doc, out _);
                var payload = new StringBuilder(model.Name.Length + 64);
                payload.Append("{\"type\":\"model\",\"doc\":\"").Append(JsonEscape(model.Name)).Append("\",\"json\":")
                       .Append(JsonSerializer.Serialize(model.ToJson()))   // embed as an escaped JSON string
                       .Append('}');
                Enqueue(payload.ToString());
            }
            catch
            {
                // Extraction can legitimately fail mid-edit (a half-finished sketch, an open transaction group);
                // the next debounced change will retry. Nothing to report back over the pipe for a transient miss.
            }
        }

        public string GetName() => "WindFlow live link extract";
    }

    /// <summary>Runs on Revit's API thread. Selects elements by UniqueId, requested by the app's "select" message.</summary>
    sealed class SelectHandler : IExternalEventHandler
    {
        public List<string> PendingIds = new();

        public void Execute(UIApplication app)
        {
            if (_doc == null) return;
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null || uidoc.Document != _doc) return;
            var ids = new List<ElementId>();
            foreach (var uid in PendingIds)
            {
                var e = _doc.GetElement(uid);
                if (e != null) ids.Add(e.Id);
            }
            if (ids.Count > 0) uidoc.Selection.SetElementIds(ids);
        }

        public string GetName() => "WindFlow live link select";
    }

    static void Enqueue(string line)
    {
        try { _outbox?.Add(line); } catch (InvalidOperationException) { /* CompleteAdding raced with a stop */ }
    }

    static string JsonEscape(string s) => JsonSerializer.Serialize(s)[1..^1];

    // ---- Pipe thread ----

    static void PipeLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);   // S4-05: only a server owned by this user
            try
            {
                SetStatus("(waiting for WindFlow)");
                pipe.Connect(5000);
                SetStatus("(connected)");

                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

                var readTask = Task.Run(() => ReadIncoming(reader, ct), ct);
                var lastPing = DateTime.UtcNow;

                // Send the initial full model immediately on connect.
                ScheduleFullExtract();

                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    if (_outbox!.TryTake(out var line, 250))
                        writer.WriteLine(line);

                    if ((DateTime.UtcNow - lastPing).TotalSeconds >= 5)
                    {
                        writer.WriteLine("{\"type\":\"ping\"}");
                        lastPing = DateTime.UtcNow;
                    }
                    if (readTask.IsCompleted) break;   // remote closed / read error
                }
            }
            catch (OperationCanceledException) { }
            catch { /* pipe not present yet, or dropped mid-session -- retry below */ }

            if (ct.IsCancellationRequested) return;
            SetStatus("(waiting for WindFlow)");
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
        }
    }

    static void ReadIncoming(StreamReader reader, CancellationToken ct)
    {
        try
        {
            string? line;
            while (!ct.IsCancellationRequested && (line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                HandleIncoming(line);
            }
        }
        catch { /* pipe dropped; the write loop notices pipe.IsConnected / readTask.IsCompleted */ }
    }

    static void HandleIncoming(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var type = doc.RootElement.GetProperty("type").GetString();
            switch (type)
            {
                case "select":
                    if (_selectHandler != null && doc.RootElement.TryGetProperty("ids", out var idsEl))
                    {
                        _selectHandler.PendingIds = idsEl.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
                        _selectEvent?.Raise();
                    }
                    break;
                case "ack":
                    break; // nothing to do; acks exist for the app's own bookkeeping
            }
        }
        catch (JsonException) { /* ignore a malformed line rather than tearing down the link */ }
    }
}

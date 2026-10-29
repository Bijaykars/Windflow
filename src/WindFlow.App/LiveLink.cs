using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>App side of the Revit live link (docs/LIVELINK.md): a named-pipe SERVER on its own thread, one
/// client at a time, newline-delimited JSON. Parsing and validation run on the pipe thread; applying a message
/// runs on the UI thread through <see cref="Apply"/>, which the window wires to its normal load/click paths.
/// Every received message is acked with the app's own sequence number (count since connect), plus an
/// <c>error</c> field when it could not be applied.</summary>
public sealed class LiveLinkServer : IDisposable
{
    public const string PipeName = "WindFlow.LiveLink";
    /// <summary>Largest message accepted. A whole-building model is a few MB; anything this big is a bug or
    /// an abuse of the pipe, and reading it unbounded would take the app's memory with it.</summary>
    public const int MaxMessageBytes = 64 * 1024 * 1024;

    /// <summary>A parsed message handed to the UI thread. <see cref="Model"/> is set for "model".</summary>
    public sealed record Message(string Type, int Seq, int Bytes, string Doc, BuildingModel? Model,
        string OpeningId, bool IsOpen);

    /// <summary>Runs on the UI thread; returns null on success or the error text for the ack.</summary>
    public required Func<Message, string?> Apply { get; init; }

    public volatile string Status = "live link: off";
    public volatile bool Connected;
    public volatile string Doc = "";
    public volatile int Received, Acked, Errors;
    readonly Dictionary<string, int> counts = new();

    Thread? thread;
    CancellationTokenSource? cancel;
    NamedPipeServerStream? pipe;
    readonly object writeLock = new();

    public bool Running => thread is { IsAlive: true };

    /// <summary>S4-05: the server end of the pipe, for this user on this machine only. Three things, because each alone leaves a gap (audit probe,
    /// <c>phase4/pipeprobe</c>): the pipe is created with PIPE_REJECT_REMOTE_CLIENTS (a client opening it as 127.0.0.1 or from another machine,
    /// which .NET's own constructor does not set), its DACL allows only the current user (the default has Everyone / Anonymous read), and
    /// the NETWORK SID is denied. .NET offers no way to set the first flag, so the handle comes from CreateNamedPipe.</summary>
    internal static NamedPipeServerStream CreateServer(string name)
    {
        var sec = new System.IO.Pipes.PipeSecurity();
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        // the Deny first: the DACL is read in order, and the same user's SID is also in a network token
        sec.AddAccessRule(new System.IO.Pipes.PipeAccessRule(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.NetworkSid, null),
            System.IO.Pipes.PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Deny));
        sec.AddAccessRule(new System.IO.Pipes.PipeAccessRule(me, System.IO.Pipes.PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        byte[] sd = sec.GetSecurityDescriptorBinaryForm();
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(sd, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = System.Runtime.InteropServices.Marshal.SizeOf<SecurityAttributes>(), SecurityDescriptor = pinned.AddrOfPinnedObject() };
            const uint DuplexOverlapped = 0x00000003 | 0x40000000;                               // PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED
            const uint ByteWaitRejectRemote = 0x00000000 | 0x00000000 | 0x00000008;              // PIPE_TYPE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, DuplexOverlapped, ByteWaitRejectRemote, 1, 65536, 65536, 0, ref attributes);
            if (handle.IsInvalid)
                throw new IOException($"CreateNamedPipe failed: Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");   // 231 = all pipe instances are busy
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally { pinned.Free(); }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct SecurityAttributes { public int Length; public nint SecurityDescriptor; public int InheritHandle; }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern Microsoft.Win32.SafeHandles.SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeOutMs, ref SecurityAttributes attributes);

    public void Start()
    {
        if (Running) return;
        cancel = new CancellationTokenSource();
        var ct = cancel.Token;
        thread = new Thread(() => Loop(ct)) { IsBackground = true, Name = "LiveLink" };
        thread.Start();
        Log.Info($"live link: server starting on \\\\.\\pipe\\{PipeName}");
    }

    public void Stop()
    {
        if (cancel == null) return;
        cancel.Cancel();
        try { pipe?.Dispose(); } catch { /* unblocks a pending read */ }
        thread?.Join(2000);
        thread = null;
        cancel.Dispose();
        cancel = null;
        Connected = false;
        Status = "live link: off";
        Log.Info("live link: server stopped");
    }

    public void Dispose() => Stop();

    /// <summary>Per-type message counts since start, for the bench file.</summary>
    public string Counts()
    {
        lock (counts) return counts.Count == 0 ? "none" : string.Join(" · ", counts.Select(kv => $"{kv.Key} {kv.Value}"));
    }

    /// <summary>Ask Revit to select these elements. No-op when nothing is connected.</summary>
    public void SendSelect(IEnumerable<string> ids)
    {
        var list = ids.Where(id => id.Length > 0).ToArray();
        if (list.Length == 0 || !Connected) return;
        if (Send(JsonSerializer.Serialize(new { type = "select", ids = list })))
            Log.Info($"live link: select sent ({string.Join(", ", list)})");
    }

    bool Send(string line)
    {
        lock (writeLock)
        {
            try
            {
                if (pipe is not { IsConnected: true } p) return false;
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                p.Write(bytes, 0, bytes.Length);
                p.Flush();
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"live link: write failed: {ex.Message}");
                return false;
            }
        }
    }

    void Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? p = null;
            try
            {
                // One instance: a second Revit gets "all pipe instances are busy" and retries on its 5 s cycle.
                p = CreateServer(PipeName);
                lock (writeLock) pipe = p;
                if (!Status.Contains("closed", StringComparison.Ordinal)) Status = "live link: waiting for Revit";
                p.WaitForConnectionAsync(ct).GetAwaiter().GetResult();
                Connected = true;
                Status = "live link: connected";
                Log.Info("live link: Revit connected");
                ReadMessages(p, ct);
                Log.Info("live link: Revit disconnected");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Warn($"live link: pipe error, restarting the listener: {ex.Message}");
                ct.WaitHandle.WaitOne(500);          // a broken pipe must not spin
            }
            catch { /* Stop() disposed the pipe under a read */ }
            finally
            {
                Connected = false;
                lock (writeLock) pipe = null;
                try { p?.Dispose(); } catch { }
            }
            if (!ct.IsCancellationRequested && !Status.Contains("closed", StringComparison.Ordinal))
                Status = "live link: waiting for Revit";
        }
    }

    void ReadMessages(NamedPipeServerStream p, CancellationToken ct)
    {
        int seq = 0;
        var line = new MemoryStream();
        bool overflow = false;
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested && p.IsConnected)
        {
            int n = p.Read(buffer, 0, buffer.Length);
            if (n <= 0) return;                      // client closed its end
            int start = 0;
            for (int i = 0; i < n; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                if (!overflow) line.Write(buffer, start, i - start);
                start = i + 1;
                if (overflow || line.Length > 0) Handle(++seq, line, overflow);
                line.SetLength(0);
                overflow = false;
            }
            if (overflow) continue;
            if (line.Length + (n - start) > MaxMessageBytes)
            {
                overflow = true;                     // drop the rest of this line, ack it with an error
                line.SetLength(0);
            }
            else line.Write(buffer, start, n - start);
        }
    }

    void Handle(int seq, MemoryStream raw, bool overflow)
    {
        int bytes = (int)Math.Min(raw.Length, int.MaxValue);
        Received++;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string type = "?";
        string? error = null;
        try
        {
            if (overflow) throw new InvalidDataException($"message larger than {MaxMessageBytes / (1024 * 1024)} MB, dropped");
            var span = new ReadOnlySpan<byte>(raw.GetBuffer(), 0, (int)raw.Length);
            if (span.Length > 0 && span[^1] == (byte)'\r') span = span[..^1];
            using var doc = JsonDocument.Parse(span.ToArray());
            var root = doc.RootElement;
            type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "?" : "?";
            Message message = type switch
            {
                "model" => ParseModel(root, seq, bytes),
                "opening" => new Message(type, seq, bytes, "", null,
                    root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "",
                    root.TryGetProperty("isOpen", out var open) && open.ValueKind == JsonValueKind.True),
                "ping" or "close" => new Message(type, seq, bytes, "", null, "", false),
                _ => throw new InvalidDataException($"unknown message type '{type}'"),
            };
            if (message.Type == "opening" && message.OpeningId.Length == 0)
                throw new InvalidDataException("opening message without an id");
            if (message.Type == "opening" && root.TryGetProperty("isOpen", out var io) &&
                io.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("opening message without a boolean isOpen");
            if (message.Type != "ping")
            {
                // The window's apply path touches controls: marshal, and wait so the ack reports the outcome.
                error = Dispatcher.UIThread.InvokeAsync(() => SafeApply(message)).GetTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException
                                       or FormatException or NotSupportedException or ArgumentException)
        {
            error = ex.Message;
        }
        clock.Stop();
        lock (counts) counts[type] = counts.GetValueOrDefault(type) + 1;
        if (error != null)
        {
            Errors++;
            Log.Warn($"live link: #{seq} {type} {bytes:N0} B REJECTED in {clock.Elapsed.TotalMilliseconds:0} ms: {error}");
        }
        else Log.Info($"live link: #{seq} {type} {bytes:N0} B applied in {clock.Elapsed.TotalMilliseconds:0} ms");

        string ack = error == null
            ? JsonSerializer.Serialize(new { type = "ack", seq })
            : JsonSerializer.Serialize(new { type = "ack", seq, error });
        if (Send(ack)) Acked++;
    }

    string? SafeApply(Message message)
    {
        try { return Apply(message); }
        catch (Exception ex)
        {
            Log.Error($"live link: applying {message.Type} #{message.Seq} failed", ex);
            return ex.Message;
        }
    }

    static Message ParseModel(JsonElement root, int seq, int bytes)
    {
        string doc = root.TryGetProperty("doc", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
        if (!root.TryGetProperty("json", out var j) || j.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("model message without a json string");
        var model = BuildingModel.FromJson(j.GetString() ?? "")
                    ?? throw new InvalidDataException("model json is null");
        if (Validate(model) is { } problem) throw new InvalidDataException(problem);
        // The exporter's own sentences go straight onto the screen: a handful, each bounded (S4-06; the line itself is capped at MaxMessageBytes).
        model.Warnings = (model.Warnings ?? []).Where(w => !string.IsNullOrWhiteSpace(w)).Take(20).Select(w => w.Length > 600 ? w[..600] : w).ToList();
        if (string.IsNullOrWhiteSpace(model.Name)) model.Name = doc.Length > 0 ? doc : "Revit model";
        return new Message("model", seq, bytes, doc, model, "", false);
    }

    /// <summary>What the voxeliser and the scene builder would choke on, checked before the model reaches
    /// the UI. Null when the model is usable (the shared rule, <see cref="ModelValidation.Validate"/>).</summary>
    public static string? Validate(BuildingModel m) => ModelValidation.Validate(m);
}

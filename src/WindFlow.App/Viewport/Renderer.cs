using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using D3D12 = Vortice.Direct3D12.D3D12;

namespace WindFlow.App.Viewport;

/// <summary>Direct3D 12 renderer for one child HWND. Everything GPU happens on its own thread; the UI thread
/// only pushes a scene, reads statistics and nudges the camera.</summary>
public sealed class Renderer
{
    const int FrameCount = 3;
    const int SampleCount = 4;               // 4x MSAA is mandatory for every D3D12 render-target format
    const Format BackFormat = Format.R8G8B8A8_UNorm;
    const Format DepthFormat = Format.D32_Float;

    // ---- shared with the UI thread ----
    public readonly Camera Camera = new();
    public readonly ParticleSystem Particles = new();
    public volatile WallMode Walls = WallMode.Clay;
    public volatile bool ShowBox, ClipToBox;
    /// <summary>The inner edge of the nested box's blend band, drawn faintly inside the box outline; null = none.</summary>
    public volatile BandBox? SpongeBand;
    public volatile int HoverHandle = -1, ActiveHandle = -1;
    Vector3 boxMin, boxMax;

    /// <summary>The simulation box, in metres. Written from the UI thread, read on the render thread.</summary>
    /// <summary>Kept so the top-bar toggle and the G key stay one-liners; X cycles all three modes.</summary>
    public bool Ghost
    {
        get => Walls == WallMode.Ghost;
        set => Walls = value ? WallMode.Ghost : WallMode.Clay;
    }

    public void SetBox(Vector3 min, Vector3 max)
    {
        lock (Camera.Sync) { boxMin = min; boxMax = max; }
    }

    public (Vector3 Min, Vector3 Max) Box
    {
        get { lock (Camera.Sync) return (boxMin, boxMax); }
    }
    public volatile string AdapterName = "";
    public volatile bool AdapterIsIntegrated;
    public volatile string Status = "";
    public volatile string Phase = "not started";
    public volatile string ModelName = "-";
    public volatile int Triangles, OpeningsTotal, OpeningsOpen;
    public volatile int HoverOpening = -1;   // index into the scene's opening list, -1 = none
    public volatile int HoverRoom = -1;      // index into BuildingModel.Rooms, -1 = none
    public volatile int SelectedRoom = -1;
    /// <summary>The Rooms section's candidate spaces (indexes into the scene overlay's Spaces): the one picked in the list, and the one under the cursor while placing. -1 for none.</summary>
    public volatile int SelectedSpace = -1, HoverSpace = -1;
    /// <summary>The other rooms of a Ctrl+click multi-selection (indexes into BuildingModel.Rooms), drawn like the selected one. One reference, replaced whole.</summary>
    public volatile int[] ExtraRooms = [];
    /// <summary>Index into the scene's opening list (= BuildingModel.Openings), highlighted as a filled quad.</summary>
    public volatile int SelectedOpening = -1;
    /// <summary>Room isolate (view only): walls, slices, cloud and trails are clipped to this box; null shows the
    /// whole building. Intersected with the section box when Clip is on. One reference, so a frame never sees
    /// half of a new box.</summary>
    public sealed record IsolationBox(Vector3 Min, Vector3 Max);
    IsolationBox? isolation;
    public IsolationBox? Isolation
    {
        get => Volatile.Read(ref isolation);
        set => Volatile.Write(ref isolation, value);
    }
    /// <summary>False during a --bench run: the window takes foreground focus, and a stray scroll or keypress
    /// from whatever the user is doing would otherwise move the camera and ruin a reproducible measurement.</summary>
    public volatile bool InputEnabled = true;
    public volatile float FrameMs, Fps;
    /// <summary>Frame cap set by the window (30 while solving, 60 idle, 5 unfocused); 0 = vsync only.</summary>
    public volatile float MaxFps;
    /// <summary>GPU timestamps, smoothed: the particle pass (advection + trail draw) and the whole frame, in ms, for
    /// <see cref="TimedParticles"/> particles (0 when the last timed frame drew none). The window extrapolates the
    /// particle limit from these (MainWindowShell.UpdateParticleLimit).</summary>
    public volatile float ParticleGpuMs, FrameGpuMs;
    public volatile int TimedParticles;
    public volatile int Width = 1, Height = 1;
    public volatile BenchResult? BenchOutput;   // published when a --bench run finishes
    volatile bool shotRequest;
    volatile byte[]? shotPixels;

    /// <summary>Set to a started recorder to capture the back buffer on a fixed timeline. The render thread
    /// clears it and raises <see cref="RecordingFinished"/> when a turntable completes.</summary>
    public volatile VideoRecorder? Recorder;
    public volatile float TurntableSeconds;
    public volatile bool RecordingFinished;
    const int RecordRing = 3;
    readonly ID3D12Resource?[] recordBuffers = new ID3D12Resource?[RecordRing];
    readonly ulong[] recordFences = new ulong[RecordRing];
    readonly bool[] recordPending = new bool[RecordRing];
    int recordSlot, recordPitch, recordHeight;
    double recordClock, recordNext;

    /// <summary>Asks the render thread for one back-buffer readback; <see cref="TakeShot"/> collects it.</summary>
    public void RequestShot() => shotRequest = true;

    public byte[]? TakeShot(out int pitch)
    {
        pitch = readbackPitch;
        var pixels = shotPixels;
        if (pixels != null) shotPixels = null;
        return pixels;
    }

    Scene? pendingScene, scene;
    volatile bool stop;
    Thread? thread;
    IntPtr hwnd;

    // ---- render-thread only ----
    IDXGIFactory4? factory;
    ID3D12Device? device;
    ID3D12CommandQueue? queue;
    IDXGISwapChain3? swapChain;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly ID3D12Resource?[] backBuffers = new ID3D12Resource?[FrameCount];
    readonly ID3D12CommandAllocator?[] allocators = new ID3D12CommandAllocator?[FrameCount];
    readonly ulong[] fenceValues = new ulong[FrameCount];
    ID3D12GraphicsCommandList? cmd;
    ID3D12DescriptorHeap? rtvHeap, dsvHeap;
    ID3D12Resource? msaaColor, depth, vertexBuffer, readback;
    ID3D12RootSignature? rootSignature;
    ID3D12PipelineState? psoClay, psoGhost, psoFlat, psoLine, psoBackground, psoHandle, psoBoxEdges, psoGlyphHalo;
    ID3D12Fence? fence;
    AutoResetEvent? fenceEvent;
    VertexBufferView vbv;
    uint rtvSize;
    int frameIndex, width = 1, height = 1;
    bool allowTearing;
    // Five timestamps per frame slot: frame start, after particle advection, before and after the trail draw,
    // frame end. Resolved into a readback buffer and read when that slot comes round again (its fence has passed).
    const int TimeSlots = 5;
    ID3D12QueryHeap? timeHeap;
    ID3D12Resource? timeReadback;
    double timeFreq;
    readonly int[] timeParticles = new int[FrameCount];
    readonly bool[] timePending = new bool[FrameCount];
    int readbackPitch;

    /// <summary>Row pitch of a back-buffer readback, which the video recorder needs to unpack frames.</summary>
    public int ReadbackPitch => readbackPitch;
    long frameCounter;
    int blindFrames;

    double benchSeconds, benchCaptureAt, benchCaptureAt2, benchElapsed;
    volatile List<float>? benchFrames;   // volatile write publishes benchSeconds to the render thread
    bool capture, captured, captured2;
    int captureSlot;
    byte[] capturedPixels = [], capturedPixels2 = [];

    // Design tokens, linear-light (the pixel shader gamma-encodes on the way out).
    static readonly Color4 Clear = new(0.039f, 0.051f, 0.071f, 1f);           // #0A0D12, already encoded
    static readonly Vector4 WallColor = Linear(0x99A5B4), GroundColor = Linear(0x333B46);
    static readonly Vector4 GhostColor = Linear(0x9FC4DE, 0.5f);
    static readonly Vector4 EdgeClay = Linear(0x0C1219, 0.45f), EdgeGhost = Linear(0xBFD9EC, 0.5f);
    static readonly Vector4 OpenFill = Linear(0x4DB8FF, 0.32f), OpenLine = Linear(0x8FD6FF, 0.95f);
    static readonly Vector4 ShutFill = Linear(0xC0453F, 0.24f), ShutLine = Linear(0xE06A62, 0.85f);
    static readonly Vector4 GridMinor = Linear(0x243448, 0.6f), GridMajor = Linear(0x33506E, 0.8f);
    static readonly Vector4 HoverFill = Linear(0x9BDCFF, 0.42f), HoverLine = Linear(0xEAF7FF, 1f);
    static readonly Vector4 WindColor = Linear(0x4DB8FF, 0.75f);
    static readonly Vector4 XrayColor = Linear(0x8FB4D0, 0.14f), EdgeXray = Linear(0xDCEBF7, 0.8f);
    static readonly Vector4 BoxColor = Linear(0x8CFFE0, 1f), BandColor = Linear(0x8CFFE0, 0.22f);
    static readonly Vector4 HandleIdle = Linear(0x9FE8D6, 0.85f), HandleHover = Linear(0xFFFFFF, 0.95f);
    static readonly Vector4 HandleActive = Linear(0xFFD166, 1f);
    static readonly Vector4 RoomLine = Linear(0x2C4055, 0.5f), RoomHover = Linear(0x7FD4FF, 0.85f);
    static readonly Vector4 SpaceLine = Linear(0x7FD4FF, 0.55f), DeletedLine = Linear(0xFFB454, 0.95f);
    // Selection: one warm accent that nothing else in the scene uses (viridis particles, blue ghost walls and
    // openings, red shut openings, amber still air), so the picked room or opening reads at a glance.
    static readonly Vector4 SelectFill = Linear(0xE08CFF, 0.30f), SelectLine = Linear(0xF2C6FF, 0.95f),
        SelectQuad = Linear(0xE08CFF, 0.55f);
    static readonly Vector4 RoomSelected = Linear(0xFFFFFF, 0.95f), ProbeColor = Linear(0xFFD166, 0.95f);
    static readonly Vector4 DeviceColor = Linear(0x7FD8F0, 0.85f), DeviceHover = Linear(0xBFEEFF, 0.95f),
        DeviceSelectedColor = Linear(0xFFFFFF, 0.98f), DeviceGhostColor = Linear(0x5AD19A, 0.98f),
        DeviceHalo = Linear(0x070B11, 0.82f),
        // A switched-off device: the same line art at ~30 % so it reads as "off", and no dark halo behind it.
        DeviceOffColor = Linear(0x7FD8F0, 0.28f);

    /// <summary>Device the pointer is over, and the one the panel has selected; -1 for none.</summary>
    public int HoverDevice = -1, SelectedDevice = -1;

    /// <summary>Nearest device glyph within 22 screen pixels of the pointer, or -1. Screen-space like the box
    /// handles, so the pick and the paint agree at any distance.</summary>
    public int PickDeviceAt(int px, int py)
    {
        var s = Volatile.Read(ref scene);
        if (s == null || s.DevicePicks.Length == 0) return -1;
        int best = -1;
        float bestD = 22f;
        foreach (var d in s.DevicePicks)
        {
            if (!Project(d.Center, out var screen)) continue;
            float dist = Vector2.Distance(screen, new Vector2(px, py));
            if (dist >= bestD) continue;
            bestD = dist;
            best = d.Index;
        }
        return best;
    }
    static readonly Vector4 SunDirection = new(0.42f, 0.62f, -0.66f, 0f);     // direction the light travels
    // Outdoor air is context, not the subject: a low alpha ceiling keeps the eye inside the rooms.
    const float ParticleBrightness = 1.15f, ParticleOutdoorDim = 0.07f, ParticleAlpha = 0.52f;

    [StructLayout(LayoutKind.Sequential)]
    struct SceneConstants
    {
        public Matrix4x4 ViewProj;
        public Vector4 Eye;
        public Vector4 Albedo;
        public Vector4 Light;
        public Vector4 ClipMin, ClipMax, BoxMin, BoxMax;
        public const int Dwords = 44;
    }

    // ---------------------------------------------------------------- public API

    /// <summary>Publish new geometry. <paramref name="frameCamera"/> is false for in-place rebuilds (an
    /// opening toggled, the wind arrow moved) so the view does not jump under the user.</summary>
    public void SetScene(Scene s, bool frameCamera = true)
    {
        ModelName = s.Name;
        Triangles = s.Triangles;
        OpeningsTotal = s.OpeningCount;
        OpeningsOpen = s.OpeningsOpen;
        HoverOpening = -1;
        HoverRoom = -1;
        if (frameCamera) lock (Camera.Sync) Camera.Frame(s.Center, s.Radius);
        Volatile.Write(ref pendingScene, s);
    }

    /// <summary>Ray-picks a room floor from a viewport pixel; returns the BuildingModel.Rooms index.</summary>
    public int PickRoomAt(int px, int py)
    {
        var s = Volatile.Read(ref scene);
        if (s == null || s.Rooms.Length == 0) return -1;
        if (!Ray(px, py, out var eye, out var dir)) return -1;
        int best = -1;
        float bestT = float.MaxValue;
        foreach (var room in s.Rooms)
            if (Scene.HitRoom(room, eye, dir, out float t) && t < bestT) { bestT = t; best = room.Index; }
        return best;
    }

    /// <summary>Where a click lands for a probe: the horizontal slice when it is on, else the hovered room's
    /// floor, else the ground.</summary>
    public Vector3? ProbePoint(int px, int py, float groundZ)
    {
        var s = Volatile.Read(ref scene);
        if (s == null || !Ray(px, py, out var eye, out var dir)) return null;
        float planeZ = groundZ;
        if (Particles.SliceHorizontal) planeZ = Particles.SliceZ;
        else
        {
            int room = PickRoomAt(px, py);
            foreach (var r in s.Rooms) if (r.Index == room) planeZ = r.ZMin + 1.1f;
        }
        if (MathF.Abs(dir.Z) < 1e-6f) return null;
        float t = (planeZ - eye.Z) / dir.Z;
        return t > 0 ? eye + dir * t : null;
    }

    public Scene? CurrentScene => Volatile.Read(ref scene);

    /// <summary>World point to viewport pixels; false when it is behind the camera.</summary>
    public bool Project(Vector3 world, out Vector2 screen)
    {
        screen = default;
        int w = Math.Max(Width, 1), h = Math.Max(Height, 1);
        Matrix4x4 viewProj;
        lock (Camera.Sync) viewProj = Camera.ViewProjection(w / (float)h);
        var clip = Vector4.Transform(new Vector4(world, 1f), viewProj);
        if (clip.W <= 1e-6f) return false;
        screen = new Vector2((clip.X / clip.W * 0.5f + 0.5f) * w, (0.5f - clip.Y / clip.W * 0.5f) * h);
        return true;
    }

    public bool ScreenRay(int px, int py, out Vector3 eye, out Vector3 direction) => Ray(px, py, out eye, out direction);

    bool Ray(int px, int py, out Vector3 eye, out Vector3 direction)
    {
        eye = default;
        direction = default;
        int w = Math.Max(Width, 1), h = Math.Max(Height, 1);
        Matrix4x4 viewProj;
        lock (Camera.Sync)
        {
            viewProj = Camera.ViewProjection(w / (float)h);
            eye = Camera.Eye;
        }
        if (!Matrix4x4.Invert(viewProj, out var inv)) return false;
        float ndcX = px / (float)w * 2f - 1f, ndcY = 1f - py / (float)h * 2f;
        var far = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), inv);
        if (MathF.Abs(far.W) < 1e-9f) return false;
        direction = Vector3.Normalize(new Vector3(far.X, far.Y, far.Z) / far.W - eye);
        return true;
    }

    /// <summary>Puts the camera on one room.</summary>
    public void FrameRoom(int roomIndex)
    {
        var s = Volatile.Read(ref scene);
        if (s == null) return;
        foreach (var r in s.Rooms)
            if (r.Index == roomIndex) { lock (Camera.Sync) Camera.Frame(r.Center, r.Radius * 2.1f); return; }
    }

    /// <summary>Puts the camera on one opening (F, or the inspector's Frame).</summary>
    public void FrameOpening(int index)
    {
        if (OpeningAt(index) is { } o) lock (Camera.Sync) Camera.Frame(o.Center, MathF.Max(o.Width, o.Height) * 2.5f);
    }

    public WindFlow.Core.Opening? OpeningAt(int index)
    {
        var s = Volatile.Read(ref scene);
        return s != null && (uint)index < (uint)s.OpeningList.Length ? s.OpeningList[index] : null;
    }

    /// <summary>Ray-picks an opening from a viewport pixel. UI thread; reads the camera under its lock.</summary>
    public int PickAt(int px, int py)
    {
        var s = Volatile.Read(ref scene);
        if (s == null || s.Picks.Length == 0) return -1;
        if (!Ray(px, py, out var eye, out var dir)) return -1;
        int best = -1;
        float bestT = float.MaxValue;
        for (int i = 0; i < s.Picks.Length; i++)
            if (Scene.Hit(s.Picks[i], eye, dir, out float t) && t < bestT) { bestT = t; best = i; }
        return best;
    }

    /// <summary>Ray-picks a deleted opening's dashed outline from a viewport pixel (index into the overlay's Deleted list), or -1.</summary>
    public int PickDeletedAt(int px, int py, out float distance)
    {
        distance = 0;
        var s = Volatile.Read(ref scene);
        if (s == null || s.DeletedPicks.Length == 0 || !Ray(px, py, out var eye, out var dir)) return -1;
        int best = -1;
        float bestT = float.MaxValue;
        for (int i = 0; i < s.DeletedPicks.Length; i++)
            if (Scene.Hit(s.DeletedPicks[i], eye, dir, out float t) && t < bestT) { bestT = t; best = i; }
        distance = bestT;
        return best;
    }

    /// <summary>Puts the camera on one candidate space (the Rooms list's Spaces without a room).</summary>
    public void FrameSpace(int index)
    {
        var s = Volatile.Read(ref scene);
        if (s == null) return;
        foreach (var r in s.Spaces)
            if (r.Index == index) { lock (Camera.Sync) Camera.Frame(r.Center, r.Radius * 2.1f); return; }
    }

    public void FrameModel()
    {
        var s = Volatile.Read(ref scene) ?? Volatile.Read(ref pendingScene);
        if (s != null) lock (Camera.Sync) Camera.Frame(s.Center, s.Radius);
    }

    /// <summary>Records frame times for <paramref name="seconds"/> while orbiting, grabbing the back buffer at
    /// <paramref name="captureAt"/> so the screenshot lands mid-orbit with developed trails.</summary>
    public void BeginBench(double seconds, double captureAt, double captureAt2 = -1)
    {
        benchSeconds = seconds;
        benchCaptureAt = Math.Clamp(captureAt, 0.1, seconds);
        benchCaptureAt2 = captureAt2 < 0 ? -1 : Math.Clamp(captureAt2, benchCaptureAt, seconds);
        captured2 = benchCaptureAt2 < 0;
        benchElapsed = 0;
        benchFrames = new List<float>(4096);
        InputEnabled = false;
    }

    public void Start(IntPtr windowHandle)
    {
        // Avalonia can create the native control more than once (re-parenting, a visibility flip). Starting a
        // second render thread on the same device leaves the first one spinning on a destroyed HWND, which
        // looks exactly like a dead viewport, so adopt the new window instead.
        if (thread != null && !Stop())
        {
            // The old thread is still inside Shutdown(); adopting the window now would let it dispose the
            // particle system the new thread has just rebuilt on the new device.
            Status = "The previous viewport thread has not finished shutting down; this window was not adopted.";
            return;
        }
        hwnd = windowHandle;
        stop = false;
        Phase = "thread starting";
        thread = new Thread(RenderThread) { Name = "WindFlow D3D12", IsBackground = true };
        thread.Start();
    }

    /// <summary>Asks the render thread to finish and waits for it. False means it did not exit in time and
    /// still owns the device; the caller must not start a second one.</summary>
    public bool Stop()
    {
        stop = true;
        var running = thread;
        if (running == null) return true;
        if (!running.Join(4000)) return false;
        thread = null;
        return true;
    }

    // ---------------------------------------------------------------- render thread

    void RenderThread()
    {
        try
        {
            Initialize();
        }
        catch (Exception e)
        {
            Status = "Direct3D 12 start-up failed: " + e.Message;
            Log.Error("render thread: Direct3D 12 start-up failed", e);
            Shutdown();
            return;
        }

        double last = clock.Elapsed.TotalSeconds, statWindow = 0;
        int statFrames = 0;
        while (!stop)
        {
            double now = clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Clamp(now - last, 0, 0.25);
            last = now;

            try
            {
                var incoming = Interlocked.Exchange(ref pendingScene, null);
                if (incoming != null) UploadScene(incoming);
                if (!Resize())
                {
                    if (++blindFrames == 120) Status = "Viewport window has no client area (handle lost).";
                    Thread.Sleep(16);
                    continue;
                }
                if (blindFrames > 0)
                {
                    // The client area came back (un-minimised): clear the message, or the status strip keeps
                    // showing it for ever -- it has priority over everything else.
                    blindFrames = 0;
                    if (Status.StartsWith("Viewport window has no client area")) Status = "";
                }

                if (benchFrames != null) AdvanceBench(dt);
                AdvanceRecording(dt);
                RenderFrame(dt);

                var present = swapChain!.Present(benchFrames != null ? 0u : 1u,
                    benchFrames != null && allowTearing ? PresentFlags.AllowTearing : PresentFlags.None);
                if (present.Failure) { OnDeviceLost(present.Code); break; }
                MoveToNextFrame();
                frameCounter++;
                // The cap is measured from the previous frame's start, so it holds with or without vsync.
                float cap = MaxFps;
                if (cap > 0)
                {
                    double wait = now + 1.0 / cap - clock.Elapsed.TotalSeconds;
                    if (wait > 0.0005) PreciseSleep.Wait(wait * 1000);
                }

                DrainRecordedFrames();
                if (capture) ReadCapture();
                if (benchFrames != null && benchElapsed >= benchSeconds) { FinishBench(); break; }
            }
            catch (Exception e)
            {
                Status = "Renderer stopped: " + e.Message;
                Log.Error($"render thread stopped in phase {Phase}", e);
                break;
            }

            statWindow += dt;
            statFrames++;
            if (statWindow >= 0.25)
            {
                Fps = (float)(statFrames / statWindow);
                FrameMs = (float)(statWindow * 1000 / statFrames);
                statWindow = 0;
                statFrames = 0;
            }
        }
        PreciseSleep.Release();
        Shutdown();
    }

    /// <summary>Decides whether this frame goes into the video, and spins a turntable while it does.</summary>
    void AdvanceRecording(float dt)
    {
        recordThisFrame = false;
        var recorder = Recorder;
        if (recorder == null) return;

        double turntable = TurntableSeconds;
        if (turntable > 0)
        {
            lock (Camera.Sync) Camera.Yaw += (float)(dt * 2 * Math.PI / turntable);
            if (recordClock >= turntable)
            {
                Recorder = null;
                RecordingFinished = true;
                return;
            }
        }

        recordClock += dt;
        if (recordClock < recordNext) return;
        recordNext += 1.0 / Math.Max(recorder.Fps, 1);
        if (recordNext < recordClock) recordNext = recordClock;   // never try to catch up a long stall
        recordThisFrame = true;
    }

    bool recordThisFrame;

    /// <summary>Maps any readback slot the GPU has finished with and hands it to the encoder. Two frames of
    /// latency, no WaitForGpu, so recording never stalls the render loop.</summary>
    void DrainRecordedFrames()
    {
        var recorder = Recorder;
        ulong completed = fence?.CompletedValue ?? 0;
        for (int i = 0; i < RecordRing; i++)
        {
            if (!recordPending[i] || recordFences[i] > completed) continue;
            recordPending[i] = false;
            if (recorder == null || recordBuffers[i] == null) continue;
            var span = recordBuffers[i]!.Map<byte>(0, recordPitch * recordHeight);
            recorder.Submit(span);
            recordBuffers[i]!.Unmap(0, null);
        }
    }

    void EnsureRecordBuffers()
    {
        if (recordPitch == readbackPitch && recordHeight == height && recordBuffers[0] != null) return;
        for (int i = 0; i < RecordRing; i++) { recordBuffers[i]?.Dispose(); recordBuffers[i] = null; recordPending[i] = false; }
        recordPitch = readbackPitch;
        recordHeight = height;
        for (int i = 0; i < RecordRing; i++)
            recordBuffers[i] = device!.CreateCommittedResource(HeapProperties.ReadbackHeapProperties, HeapFlags.None,
                ResourceDescription.Buffer((ulong)(recordPitch * recordHeight), ResourceFlags.None, 0),
                ResourceStates.CopyDest, null);
    }

    /// <summary>Starts a recording. The caller owns the recorder and stops it once
    /// <see cref="RecordingFinished"/> comes back true (turntable) or the user stops it.</summary>
    public void BeginRecording(VideoRecorder recorder, double turntableSeconds)
    {
        recordClock = 0;
        recordNext = 0;
        RecordingFinished = false;
        TurntableSeconds = (float)turntableSeconds;
        Recorder = recorder;
    }

    public void EndRecording()
    {
        Recorder = null;
        TurntableSeconds = 0;
    }

    void AdvanceBench(float dt)
    {
        benchElapsed += dt;
        // Turn at whatever rate puts the capture frame at the same camera angle whatever the run length.
        // A two-capture run is an A/B comparison, so the camera holds still and only the flow changes.
        if (benchCaptureAt2 < 0)
            lock (Camera.Sync) Camera.Yaw += dt * (float)(1.55 / Math.Max(benchCaptureAt, 0.5));
        if (benchElapsed > 0.5) benchFrames!.Add(dt * 1000f);
        if (!captured && benchElapsed >= benchCaptureAt) { capture = true; captureSlot = 0; }
        else if (captured && !captured2 && benchElapsed >= benchCaptureAt2) { capture = true; captureSlot = 1; }
    }

    void ReadCapture()
    {
        capture = false;
        if (captureSlot == 0) captured = true;
        else if (captureSlot == 1) captured2 = true;
        WaitForGpu("capture");
        if (readback == null) return;
        var span = readback.Map<byte>(0, readbackPitch * height);
        var pixels = span.ToArray();
        readback.Unmap(0, null);
        if (captureSlot == 0) capturedPixels = pixels;
        else if (captureSlot == 1) capturedPixels2 = pixels;
        else shotPixels = pixels;
    }

    void FinishBench()
    {
        if (!captured || !captured2)
        {
            WaitForGpu("capture-fallback");
            capture = true;
            captureSlot = captured ? 1 : 0;
            RenderFrame(0);
            swapChain!.Present(0, PresentFlags.None);
            ReadCapture();
        }
        // Read the particle counters here: Shutdown() releases them the moment this loop breaks.
        BenchOutput = new BenchResult(AdapterName, width, height, benchFrames?.ToArray() ?? [], capturedPixels,
            readbackPitch, Particles.Count, Particles.Diagnostic, capturedPixels2);
        benchFrames = null;
    }

    void OnDeviceLost(int code)
    {
        int reason = device?.DeviceRemovedReason.Code ?? 0;
        Status = $"GPU device lost (0x{code:X8}, reason 0x{reason:X8}). Restart WindFlow to recover.";
        Log.Error("render thread: " + Status);
    }

    // ---------------------------------------------------------------- setup

    void Initialize()
    {
        bool debugLayer = false;
#if DEBUG
        if (D3D12.D3D12GetDebugInterface(out Vortice.Direct3D12.Debug.ID3D12Debug? dbg).Success && dbg != null)
        {
            dbg.EnableDebugLayer();
            dbg.Dispose();
            debugLayer = true;
        }
#endif
        Phase = "factory";
        factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(debugLayer);
        using var adapter = PickAdapter(factory, Options.Adapter);
        AdapterName = adapter?.Description1.Description.Trim() ?? "unknown adapter";
        // Integrated parts report almost no dedicated video memory; they get the smaller particle budget.
        AdapterIsIntegrated = adapter != null && (ulong)adapter.Description1.DedicatedVideoMemory < 1024UL * 1024 * 1024;
        device = D3D12.D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_11_0);

        queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        queue.GetTimestampFrequency(out ulong frequency);
        timeFreq = frequency;
        timeHeap = device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(QueryHeapType.Timestamp, (uint)(FrameCount * TimeSlots)));
        timeReadback = device.CreateCommittedResource(HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(FrameCount * TimeSlots * 8), ResourceFlags.None, 0), ResourceStates.CopyDest, null);
        (width, height) = Win32.ClientSize(hwnd);
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);

        var desc = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = BackFormat,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = FrameCount,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            Flags = SwapChainFlags.AllowTearing,
        };
        IDXGISwapChain1 sc;
        try
        {
            sc = factory.CreateSwapChainForHwnd(queue, hwnd, desc);
            allowTearing = true;
        }
        catch
        {
            desc.Flags = SwapChainFlags.None;
            sc = factory.CreateSwapChainForHwnd(queue, hwnd, desc);
            allowTearing = false;
        }
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
        swapChain = sc.QueryInterface<IDXGISwapChain3>();
        sc.Dispose();
        frameIndex = (int)swapChain.CurrentBackBufferIndex;

        rtvSize = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        rtvHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, FrameCount + 1));
        dsvHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));

        for (int i = 0; i < FrameCount; i++) allocators[i] = device.CreateCommandAllocator(CommandListType.Direct);
        cmd = device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, allocators[0]!, null);
        cmd.Close();
        fence = device.CreateFence(0, FenceFlags.None);
        fenceEvent = new AutoResetEvent(false);
        fenceValues[frameIndex] = 1;

        Phase = "pipeline";
        CreatePipeline();
        Phase = "particles";
        Particles.Initialize(device, BackFormat, DepthFormat, SampleCount);
        Phase = "sized";
        CreateSizedResources();
        Width = width;
        Phase = "running";
        Height = height;
    }

    internal static IDXGIAdapter1? PickAdapter(IDXGIFactory4 f, string? wanted)
    {
        using var f6 = f.QueryInterface<IDXGIFactory6>();
        var preference = string.IsNullOrEmpty(wanted) ? GpuPreference.HighPerformance : GpuPreference.Unspecified;
        for (uint i = 0; i < 32; i++)
        {
            if (f6.EnumAdapterByGpuPreference(i, preference, out IDXGIAdapter1? a).Failure || a == null) break;
            var d = a.Description1;
            bool software = (d.Flags & AdapterFlags.Software) != 0;
            bool match = string.IsNullOrEmpty(wanted) ||
                         d.Description.Contains(wanted, StringComparison.OrdinalIgnoreCase);
            if (!software && match) return a;
            a.Dispose();
        }
        return null;   // let D3D12CreateDevice pick the default adapter
    }

    void CreatePipeline()
    {
        var rs = new RootSignatureDescription1
        {
            Flags = RootSignatureFlags.AllowInputAssemblerInputLayout,
            Parameters = [new RootParameter1(new RootConstants(0, 0, SceneConstants.Dwords), ShaderVisibility.All)],
        };
        rootSignature = device!.CreateRootSignature(rs);

        var vs = Compile("VSMain", Shaders.Profile5Vs);
        var psClay = Compile("PSClay", Shaders.Profile5Ps);
        var psGhost = Compile("PSGhost", Shaders.Profile5Ps);
        var psFlat = Compile("PSFlat", Shaders.Profile5Ps);

        var layout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0));

        // Cull nothing: exported meshes are not reliably wound and ghost mode needs back faces anyway.
        var raster = RasterizerDescription.CullNone;
        raster.MultisampleEnable = true;
        var premultiplied = new BlendDescription(Blend.One, Blend.InverseSourceAlpha);

        psoClay = MakePso(vs, psClay, layout, raster, BlendDescription.Opaque,
            new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.LessEqual), PrimitiveTopologyType.Triangle);
        psoGhost = MakePso(vs, psGhost, layout, raster, premultiplied,
            new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual), PrimitiveTopologyType.Triangle);
        psoFlat = MakePso(vs, psFlat, layout, raster, premultiplied,
            new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual), PrimitiveTopologyType.Triangle);
        psoLine = MakePso(vs, psFlat, layout, raster, premultiplied,
            new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual), PrimitiveTopologyType.Line);
        // Same billboard as a handle, but depth-tested: a device behind a wall must not shine through it.
        psoGlyphHalo = MakePso(Compile("VSHandle", Shaders.Profile5Vs), Compile("PSHandle", Shaders.Profile5Ps),
            layout, raster, premultiplied,
            new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual),
            PrimitiveTopologyType.Triangle);
        psoHandle = MakePso(Compile("VSGizmo", Shaders.Profile5Vs), Compile("PSGizmo", Shaders.Profile5Ps),
            new InputLayoutDescription([]), raster, premultiplied,
            new DepthStencilDescription(false, DepthWriteMask.Zero, ComparisonFunction.Always),
            PrimitiveTopologyType.Triangle);
        // The box outline generates its own vertices from BoxMin/BoxMax: with psoLine (VSMain) it drew the
        // first 24 vertices of the scene buffer instead, and VSBoxEdges was never compiled at all.
        psoBoxEdges = MakePso(Compile("VSBoxEdges", Shaders.Profile5Vs), psFlat,
            new InputLayoutDescription([]), raster, premultiplied,
            new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual),
            PrimitiveTopologyType.Line);
        psoBackground = MakePso(Compile("VSBackground", Shaders.Profile5Vs), Compile("PSBackground", Shaders.Profile5Ps),
            new InputLayoutDescription([]), raster, BlendDescription.Opaque,
            DepthStencilDescription.None, PrimitiveTopologyType.Triangle);
    }

    ReadOnlyMemory<byte> Compile(string entry, string profile) { Phase = "compile " + entry; return CompileShader(Shaders.Source, entry, profile); }

    public static ReadOnlyMemory<byte> CompileShader(string source, string entry, string profile)
    {
        var result = Vortice.D3DCompiler.Compiler.Compile(source, entry, "windflow.hlsl", profile,
            out var blob, out var errors);
        if (result.Failure || blob == null)
            throw new InvalidOperationException($"{entry}: {errors?.AsString() ?? result.Description}");
        var bytes = blob.AsBytes();
        blob.Dispose();
        errors?.Dispose();
        return bytes;
    }

    ID3D12PipelineState MakePso(ReadOnlyMemory<byte> vs, ReadOnlyMemory<byte> ps, InputLayoutDescription layout,
        RasterizerDescription raster, BlendDescription blend, DepthStencilDescription depthStencil, PrimitiveTopologyType topology) =>
        device!.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = rootSignature,
            VertexShader = vs,
            PixelShader = ps,
            InputLayout = layout,
            RasterizerState = raster,
            BlendState = blend,
            DepthStencilState = depthStencil,
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = topology,
            RenderTargetFormats = [BackFormat],
            DepthStencilFormat = DepthFormat,
            SampleDescription = new SampleDescription(SampleCount, 0),
        });

    /// <summary>Back buffers, the MSAA target and the depth buffer; also used after every resize.</summary>
    void CreateSizedResources()
    {
        var rtv = rtvHeap!.GetCPUDescriptorHandleForHeapStart();
        for (int i = 0; i < FrameCount; i++)
        {
            backBuffers[i] = swapChain!.GetBuffer<ID3D12Resource>((uint)i);
            device!.CreateRenderTargetView(backBuffers[i], null, rtv + (int)(i * rtvSize));
        }

        var colorDesc = ResourceDescription.Texture2D(BackFormat, (uint)width, (uint)height, 1, 1, SampleCount, 0,
            ResourceFlags.AllowRenderTarget, TextureLayout.Unknown, 0);
        msaaColor = device!.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None, colorDesc,
            ResourceStates.RenderTarget, new ClearValue(BackFormat, Clear));
        device.CreateRenderTargetView(msaaColor, null, MsaaRtv);

        var depthDesc = ResourceDescription.Texture2D(DepthFormat, (uint)width, (uint)height, 1, 1, SampleCount, 0,
            ResourceFlags.AllowDepthStencil, TextureLayout.Unknown, 0);
        depth = device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None, depthDesc,
            ResourceStates.DepthWrite, new ClearValue(DepthFormat, 1f, 0));
        device.CreateDepthStencilView(depth, null, dsvHeap!.GetCPUDescriptorHandleForHeapStart());

        readback?.Dispose();
        readbackPitch = (width * 4 + 255) & ~255;
        readback = device.CreateCommittedResource(HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(readbackPitch * height), ResourceFlags.None, 0), ResourceStates.CopyDest, null);
    }

    CpuDescriptorHandle MsaaRtv => rtvHeap!.GetCPUDescriptorHandleForHeapStart() + (int)(FrameCount * rtvSize);
    CpuDescriptorHandle Dsv => dsvHeap!.GetCPUDescriptorHandleForHeapStart();

    /// <summary>Polls the HWND each frame instead of listening for WM_SIZE: one call, no cross-thread ordering.</summary>
    bool Resize()
    {
        var (w, h) = Win32.ClientSize(hwnd);
        if (w <= 0 || h <= 0) return false;
        if (w == width && h == height) return true;

        WaitForGpu("resize");
        for (int i = 0; i < FrameCount; i++) { backBuffers[i]?.Dispose(); backBuffers[i] = null; }
        msaaColor?.Dispose();
        depth?.Dispose();
        width = w;
        height = h;
        swapChain!.ResizeBuffers(FrameCount, (uint)w, (uint)h, BackFormat,
            allowTearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None).CheckError();
        frameIndex = (int)swapChain.CurrentBackBufferIndex;
        CreateSizedResources();
        Width = w;
        Height = h;
        return true;
    }

    void UploadScene(Scene incoming)
    {
        WaitForGpu("upload-begin");
        vertexBuffer?.Dispose();
        vertexBuffer = null;
        scene = incoming;
        int count = incoming.Vertices.Length;
        if (count == 0) return;

        ulong bytes = (ulong)count * Vertex.Size;
        using var upload = device!.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(bytes, ResourceFlags.None, 0), ResourceStates.GenericRead, null);
        var dst = upload.Map<Vertex>(0, count);
        incoming.Vertices.CopyTo(dst);
        upload.Unmap(0, null);

        vertexBuffer = device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(bytes, ResourceFlags.None, 0), ResourceStates.Common, null);

        allocators[frameIndex]!.Reset();
        cmd!.Reset(allocators[frameIndex]!, null);
        cmd.ResourceBarrierTransition(vertexBuffer, ResourceStates.Common, ResourceStates.CopyDest);
        cmd.CopyBufferRegion(vertexBuffer, 0, upload, 0, bytes);
        cmd.ResourceBarrierTransition(vertexBuffer, ResourceStates.CopyDest, ResourceStates.VertexAndConstantBuffer);
        cmd.Close();
        queue!.ExecuteCommandList(cmd);
        WaitForGpu("upload-end");
        vbv = new VertexBufferView(vertexBuffer.GPUVirtualAddress, (uint)bytes, Vertex.Size);
    }

    // ---------------------------------------------------------------- frame

    void RenderFrame(float dt)
    {
        var allocator = allocators[frameIndex]!;
        allocator.Reset();
        cmd!.Reset(allocator, null);

        if (shotRequest && !capture) { capture = true; captureSlot = 2; shotRequest = false; }
        if (timePending[frameIndex]) ReadTimes(frameIndex);
        int tq = frameIndex * TimeSlots;
        cmd.EndQuery(timeHeap!, QueryType.Timestamp, (uint)tq);
        Particles.Update(cmd, dt, clock.Elapsed.TotalSeconds, fenceValues[frameIndex], fence!.CompletedValue);
        cmd.EndQuery(timeHeap!, QueryType.Timestamp, (uint)(tq + 1));
        bool trailsTimed = false;

        cmd.RSSetViewport(0f, 0f, width, height, 0f, 1f);
        cmd.RSSetScissorRect(width, height);
        cmd.OMSetRenderTargets(MsaaRtv, Dsv);
        cmd.ClearRenderTargetView(MsaaRtv, Clear);
        cmd.ClearDepthStencilView(Dsv, ClearFlags.Depth, 1f, 0);
        cmd.SetGraphicsRootSignature(rootSignature);

        cmd.SetPipelineState(psoBackground!);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.DrawInstanced(3, 1, 0, 0);

        var s = scene;
        if (s != null && vertexBuffer != null)
        {
            Matrix4x4 viewProj, view;
            Vector3 eye;
            lock (Camera.Sync)
            {
                Camera.SceneRadius = s.Radius;
                viewProj = Camera.ViewProjection(width / (float)height);
                view = Camera.View;
                eye = Camera.Eye;
            }
            cmd.IASetVertexBuffers(0, vbv);

            var (bMin, bMax) = Box;
            bool clip = ClipToBox && bMax.X > bMin.X;
            // Room isolate: the same clip every surface shader already honours, intersected with the section box.
            var iso = Isolation;
            Vector3 cMin = bMin, cMax = bMax;
            if (iso != null)
            {
                cMin = clip ? Vector3.Max(bMin, iso.Min) : iso.Min;
                cMax = clip ? Vector3.Min(bMax, iso.Max) : iso.Max;
                clip = true;
            }
            var constants = new SceneConstants
            {
                ViewProj = viewProj,
                Light = SunDirection,
                ClipMin = new Vector4(cMin, clip ? 1f : 0f),
                ClipMax = new Vector4(cMax, 0f),
                BoxMin = new Vector4(bMin, 0f),
                BoxMax = new Vector4(bMax, 0f),
            };
            Particles.ClipMin = constants.ClipMin;
            Particles.ClipMax = constants.ClipMax;
            // Trails are not cut by the section box (by design), only by an isolated room.
            Particles.TrailClipMin = new Vector4(cMin, iso != null ? 1f : 0f);
            Particles.TrailClipMax = new Vector4(cMax, 0f);
            Draw(ref constants, eye, s.GridMinor, psoLine!, PrimitiveTopology.LineList, GridMinor, s.GridFade);
            Draw(ref constants, eye, s.GridMajor, psoLine!, PrimitiveTopology.LineList, GridMajor, s.GridFade);
            Draw(ref constants, eye, s.Ground, psoClay!, PrimitiveTopology.TriangleList, GroundColor, 0);
            // Clay, Ghost, X-ray: same geometry, three readings of it.
            var mode = Walls;
            if (mode != WallMode.Xray)
                Draw(ref constants, eye, s.Walls, mode == WallMode.Ghost ? psoGhost! : psoClay!,
                    PrimitiveTopology.TriangleList, mode == WallMode.Ghost ? GhostColor : WallColor, 0);
            else
                Draw(ref constants, eye, s.Walls, psoGhost!, PrimitiveTopology.TriangleList, XrayColor, 0);
            Draw(ref constants, eye, s.Edges, psoLine!, PrimitiveTopology.LineList,
                mode == WallMode.Clay ? EdgeClay : mode == WallMode.Ghost ? EdgeGhost : EdgeXray, 0);
            Draw(ref constants, eye, s.OpenQuads, psoFlat!, PrimitiveTopology.TriangleList, OpenFill, 0);
            Draw(ref constants, eye, s.ClosedQuads, psoFlat!, PrimitiveTopology.TriangleList, ShutFill, 0);
            Draw(ref constants, eye, s.OpenLines, psoLine!, PrimitiveTopology.LineList, OpenLine, 0);
            Draw(ref constants, eye, s.ClosedLines, psoLine!, PrimitiveTopology.LineList, ShutLine, 0);
            Draw(ref constants, eye, s.WindArrow, psoLine!, PrimitiveTopology.LineList, WindColor, 0);

            Draw(ref constants, eye, s.RoomOutlines, psoLine!, PrimitiveTopology.LineList, RoomLine, 0);
            Draw(ref constants, eye, s.SpaceOutlines, psoLine!, PrimitiveTopology.LineList, SpaceLine, 0);
            Draw(ref constants, eye, s.DeletedLines, psoLine!, PrimitiveTopology.LineList, DeletedLine, 0);
            int selectedSpace = SelectedSpace, hoverSpace = HoverSpace;
            foreach (var sp in s.Spaces)
            {
                if (sp.Index == selectedSpace && iso == null) Draw(ref constants, eye, sp.Block, psoGhost!, PrimitiveTopology.TriangleList, SelectFill, 0);
                else if (sp.Index == hoverSpace) Draw(ref constants, eye, sp.BlockEdges, psoLine!, PrimitiveTopology.LineList, RoomHover, 0);
            }
            int selectedRoom = SelectedRoom;
            var extraRooms = ExtraRooms;
            RoomPick? selected = null;
            foreach (var room in s.Rooms)
            {
                if (extraRooms.Contains(room.Index) && iso == null)
                    Draw(ref constants, eye, room.Block, psoGhost!, PrimitiveTopology.TriangleList, SelectFill, 0);
                if (room.Index == selectedRoom) selected = room;
                else if (room.Index == HoverRoom)
                    Draw(ref constants, eye, room.Outline, psoLine!, PrimitiveTopology.LineList, RoomHover, 0);
            }
            // The picked room as a volume: ghost-shaded (fresnel, so the silhouette is brighter than the faces),
            // depth-tested against clay walls, drawn before the particles so the trails stay on top of it.
            // Isolated, the room is the whole picture: no tinted volume over its air, only the edges after the trails.
            if (selected is { } sel && iso == null)
                Draw(ref constants, eye, sel.Block, psoGhost!, PrimitiveTopology.TriangleList, SelectFill, 0);
            int selectedOpening = SelectedOpening;
            if ((uint)selectedOpening < (uint)s.Picks.Length)
                Draw(ref constants, eye, s.Picks[selectedOpening].Quad, psoFlat!, PrimitiveTopology.TriangleList, SelectQuad, 0);
            Draw(ref constants, eye, s.Probes, psoLine!, PrimitiveTopology.LineList, ProbeColor, 0);

            int hover = HoverOpening;
            if ((uint)hover < (uint)s.Picks.Length)
            {
                Draw(ref constants, eye, s.Picks[hover].Quad, psoFlat!, PrimitiveTopology.TriangleList, HoverFill, 0);
                Draw(ref constants, eye, s.Picks[hover].Lines, psoLine!, PrimitiveTopology.LineList, HoverLine, 0);
            }
            if (ShowBox && bMax.X > bMin.X)
            {
                constants.Eye = new Vector4(eye, 0);
                constants.Albedo = BoxColor;
                constants.ClipMin = new Vector4(bMin, 0f);      // the outline is not clipped by its own box
                unsafe { cmd.SetGraphicsRoot32BitConstants(0, SceneConstants.Dwords, (IntPtr)(&constants), 0); }
                cmd.SetPipelineState(psoBoxEdges!);
                cmd.IASetPrimitiveTopology(PrimitiveTopology.LineList);
                cmd.DrawInstanced(24, 1, 0, 0);
                if (SpongeBand is { } band)
                {
                    // Same generated edges, on the band's inner box, in a faint tint of the box colour.
                    constants.BoxMin = new Vector4(band.Min, 0f);
                    constants.BoxMax = new Vector4(band.Max, 0f);
                    constants.Albedo = BandColor;
                    unsafe { cmd.SetGraphicsRoot32BitConstants(0, SceneConstants.Dwords, (IntPtr)(&constants), 0); }
                    cmd.DrawInstanced(24, 1, 0, 0);
                    constants.BoxMin = new Vector4(bMin, 0f);
                    constants.BoxMax = new Vector4(bMax, 0f);
                }
                constants.ClipMin = new Vector4(cMin, clip ? 1f : 0f);
            }
            Particles.DrawSlices(cmd, viewProj, fenceValues[frameIndex]);
            Particles.DrawCloud(cmd, viewProj, new Vector3(view.M11, view.M21, view.M31),
                new Vector3(view.M12, view.M22, view.M32), fenceValues[frameIndex]);
            cmd.EndQuery(timeHeap!, QueryType.Timestamp, (uint)(tq + 2));
            Particles.Draw(cmd, viewProj, ParticleBrightness, ParticleOutdoorDim, ParticleAlpha, width);
            cmd.EndQuery(timeHeap!, QueryType.Timestamp, (uint)(tq + 3));
            trailsTimed = true;
            // Selection edges after the particles, so the room's outline is never lost in a dense cloud.
            cmd.SetGraphicsRootSignature(rootSignature);
            cmd.IASetVertexBuffers(0, vbv);
            if (selected is { } selEdges)
                Draw(ref constants, eye, selEdges.BlockEdges, psoLine!, PrimitiveTopology.LineList, SelectLine, 0);
            foreach (var sp in s.Spaces)
                if (sp.Index == selectedSpace) Draw(ref constants, eye, sp.BlockEdges, psoLine!, PrimitiveTopology.LineList, SelectLine, 0);
            if ((uint)selectedOpening < (uint)s.Picks.Length)
                Draw(ref constants, eye, s.Picks[selectedOpening].Lines, psoLine!, PrimitiveTopology.LineList, SelectLine, 0);
            DrawDeviceGlyphs(cmd, ref constants, eye, s);
            DrawHandles(cmd, ref constants, viewProj);
        }

        if (!trailsTimed)
        {
            cmd.EndQuery(timeHeap!, QueryType.Timestamp, (uint)(tq + 2));
            cmd.EndQuery(timeHeap!, QueryType.Timestamp, (uint)(tq + 3));
        }
        var back = backBuffers[frameIndex]!;
        cmd.ResourceBarrierTransition(msaaColor!, ResourceStates.RenderTarget, ResourceStates.ResolveSource);
        cmd.ResourceBarrierTransition(back, ResourceStates.Common, ResourceStates.ResolveDest);
        cmd.ResolveSubresource(back, 0, msaaColor!, 0, BackFormat);
        bool recordNow = recordThisFrame && Recorder != null;
        if (recordNow) EnsureRecordBuffers();
        if (capture || recordNow)
        {
            cmd.ResourceBarrierTransition(back, ResourceStates.ResolveDest, ResourceStates.CopySource);
            var footprint = new PlacedSubresourceFootPrint
            {
                Offset = 0,
                Footprint = new SubresourceFootPrint(BackFormat, (uint)width, (uint)height, 1, (uint)readbackPitch),
            };
            if (capture)
                cmd.CopyTextureRegion(new TextureCopyLocation(readback!, footprint), 0, 0, 0,
                    new TextureCopyLocation(back, 0), null);
            if (recordNow && recordBuffers[recordSlot] is { } target)
            {
                cmd.CopyTextureRegion(new TextureCopyLocation(target, footprint), 0, 0, 0,
                    new TextureCopyLocation(back, 0), null);
                recordPending[recordSlot] = true;
                recordFences[recordSlot] = fenceValues[frameIndex];
                recordSlot = (recordSlot + 1) % RecordRing;
            }
            cmd.ResourceBarrierTransition(back, ResourceStates.CopySource, ResourceStates.Common);
        }
        else
        {
            cmd.ResourceBarrierTransition(back, ResourceStates.ResolveDest, ResourceStates.Common);
        }
        cmd.ResourceBarrierTransition(msaaColor!, ResourceStates.ResolveSource, ResourceStates.RenderTarget);
        cmd.EndQuery(timeHeap!, QueryType.Timestamp, (uint)(tq + 4));
        cmd.ResolveQueryData(timeHeap!, QueryType.Timestamp, (uint)tq, TimeSlots, timeReadback!, (ulong)(tq * 8));
        timePending[frameIndex] = true;
        // Only a frame that really advected and drew trails in the Speed view says anything about their cost.
        var p = Particles;
        timeParticles[frameIndex] = trailsTimed && p.Ready && p.Enabled && !p.Waiting && p.View == ParticleView.Speed ? p.Count : 0;
        cmd.Close();
        queue!.ExecuteCommandList(cmd);
    }

    /// <summary>Render thread, at the start of a frame whose slot has completed on the GPU: fold that slot's
    /// timestamps into the smoothed particle and frame times.</summary>
    void ReadTimes(int slot)
    {
        timePending[slot] = false;
        if (timeReadback == null || timeFreq <= 0) return;
        var all = timeReadback.Map<ulong>(0, FrameCount * TimeSlots);
        var t = all.Slice(slot * TimeSlots, TimeSlots).ToArray();
        timeReadback.Unmap(0, null);
        if (t[4] <= t[0] || t[1] < t[0] || t[3] < t[2]) return;       // a slot never written, or a reset clock
        double scale = 1000.0 / timeFreq;
        float particleMs = (float)(((t[1] - t[0]) + (t[3] - t[2])) * scale);
        float frameMs = (float)((t[4] - t[0]) * scale);
        int count = timeParticles[slot];
        if (count <= 0) return;
        if (count != TimedParticles) { ParticleGpuMs = particleMs; FrameGpuMs = frameMs; TimedParticles = count; return; }
        ParticleGpuMs = 0.9f * ParticleGpuMs + 0.1f * particleMs;
        FrameGpuMs = 0.9f * FrameGpuMs + 0.1f * frameMs;
    }

    /// <summary>Devices go in last, after the particles, so a glyph is never buried under 250k trails — but
    /// with the depth test still on, so it is hidden by a wall in front of it. Each one gets a dark
    /// screen-space plate first (the halo) and its bright line art on top, which is the same read-on-anything
    /// trick the opening quads use: a dark fill behind a bright outline. The plate also sets the size floor:
    /// it is never smaller than <see cref="GlyphMinPixels"/> however far away the device is.</summary>
    const float GlyphMinPixels = 17f;

    unsafe void DrawDeviceGlyphs(ID3D12GraphicsCommandList cmd, ref SceneConstants constants, Vector3 eye, Scene s)
    {
        if (s.DevicePicks.Length == 0 && s.DeviceGhost.Count == 0) return;
        // The particle and slice passes bind their own root signatures; take ours back.
        cmd.SetGraphicsRootSignature(rootSignature);

        if (s.DevicePicks.Length > 0 || s.GhostPick != null)
        {
            cmd.SetPipelineState(psoGlyphHalo!);
            cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            foreach (var d in s.GhostPick is { } g ? [.. s.DevicePicks, g] : s.DevicePicks)
            {
                // Off devices get no halo unless the pointer or the panel singles them out.
                if (!d.On && d.Index != SelectedDevice && d.Index != HoverDevice) continue;
                float radius = MathF.Max(GlyphMinPixels, ScreenExtent(d.Center, d.Radius) + 5f);
                if (d.Index == SelectedDevice || d.Index == HoverDevice) radius *= 1.15f;
                constants.Eye = new Vector4(d.Center, 0);
                constants.Light = new Vector4(2f * radius / Math.Max(width, 1), 2f * radius / Math.Max(height, 1), 0, 0);
                constants.Albedo = DeviceHalo;
                fixed (SceneConstants* p = &constants) cmd.SetGraphicsRoot32BitConstants(0, SceneConstants.Dwords, (IntPtr)p, 0);
                cmd.DrawInstanced(6, 1, 0, 0);
            }
            constants.Light = SunDirection;
        }

        Draw(ref constants, eye, s.DeviceOff, psoLine!, PrimitiveTopology.LineList, DeviceOffColor, 0);
        Draw(ref constants, eye, s.DeviceGlyphs, psoLine!, PrimitiveTopology.LineList, DeviceColor, 0);
        Draw(ref constants, eye, s.DeviceSelected, psoLine!, PrimitiveTopology.LineList, DeviceSelectedColor, 0);
        Draw(ref constants, eye, s.DeviceGhost, psoLine!, PrimitiveTopology.LineList, DeviceGhostColor, 0);
    }

    /// <summary>Half-extent of a world-space radius about a point, in pixels. Sampled on all three axes
    /// because the glyph's own plane is not known here and the largest one is what has to be covered.</summary>
    float ScreenExtent(Vector3 center, float worldRadius)
    {
        if (!Project(center, out var mid)) return GlyphMinPixels;
        float best = 0;
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            if (Project(center + axis * worldRadius, out var edge))
                best = MathF.Max(best, Vector2.Distance(mid, edge));
        return best;
    }

    /// <summary>Drag handles as constant-pixel gizmos (arrow, ring, square), drawn last and depth-test free so a
    /// handle is always reachable. The layout comes from <see cref="Gizmos.Layout"/>, the same function the
    /// picker tests, so the grabbable area is exactly the painted one. Each gizmo is drawn twice: a dark,
    /// fattened copy and then the stroke, so it reads on a yellow jet as well as on the ink background.</summary>
    unsafe void DrawHandles(ID3D12GraphicsCommandList cmd, ref SceneConstants constants, Matrix4x4 viewProj)
    {
        var handles = Gizmos.Build(this);
        if (handles.Length == 0) return;
        // The particle and slice passes bind their own root signatures; take ours back before setting constants.
        cmd.SetGraphicsRootSignature(rootSignature);
        cmd.SetPipelineState(psoHandle!);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        float toNdcX = 2f / Math.Max(width, 1), toNdcY = -2f / Math.Max(height, 1);
        const float HaloGrow = 1.6f;
        foreach (var handle in handles)
        {
            if (!Gizmos.Layout(this, handle, out var lay)) continue;
            bool active = handle.Id == ActiveHandle, hover = handle.Id == HoverHandle;
            // The one under the cursor thickens and brightens; it never grows, so the pick stays the paint.
            float stroke = active || hover ? 1.6f : 1.2f;
            var shape = handle.Shape;
            uint count = (uint)Gizmos.VertexCount(shape);
            bool knob = !float.IsNaN(handle.Knob);
            constants.Eye = new Vector4(handle.Position, handle.Gap);
            for (int pass = 0; pass < 2; pass++)
            {
                bool halo = pass == 0;
                float grow = halo ? HaloGrow : 0f;
                float extra = shape == HandleShape.Square ? (halo ? 0.55f : 0.22f) : knob ? handle.Knob : 0f;
                constants.Light = new Vector4(toNdcX, toNdcY, (float)(int)shape, stroke + grow);
                constants.ClipMin = new Vector4(lay.U.X, lay.U.Y, grow, 0f);
                constants.ClipMax = new Vector4(lay.V.X, lay.V.Y, extra, knob ? 1f : 0f);
                constants.Albedo = halo ? DeviceHalo : active ? HandleActive : hover ? HandleHover : HandleIdle;
                fixed (SceneConstants* p = &constants)
                    cmd.SetGraphicsRoot32BitConstants(0, SceneConstants.Dwords, (IntPtr)p, 0);
                cmd.DrawInstanced(count, 1, 0, 0);
            }
        }
        constants.Light = SunDirection;
        constants.ClipMin = new Vector4(0, 0, 0, 0);
        constants.ClipMax = new Vector4(0, 0, 0, 0);
    }

    unsafe void Draw(ref SceneConstants constants, Vector3 eye, DrawRange range, ID3D12PipelineState pso,
        PrimitiveTopology topology, Vector4 albedo, float fade)
    {
        if (range.Count == 0) return;
        constants.Eye = new Vector4(eye, fade);
        constants.Albedo = albedo;
        cmd!.SetPipelineState(pso);
        cmd.IASetPrimitiveTopology(topology);
        fixed (SceneConstants* p = &constants)
            cmd.SetGraphicsRoot32BitConstants(0, SceneConstants.Dwords, (IntPtr)p, 0);
        cmd.DrawInstanced((uint)range.Count, 1, (uint)range.Start, 0);
    }

    void MoveToNextFrame()
    {
        ulong current = fenceValues[frameIndex];
        queue!.Signal(fence!, current);
        frameIndex = (int)swapChain!.CurrentBackBufferIndex;
        if (fence!.CompletedValue < fenceValues[frameIndex])
        {
            fence.SetEventOnCompletion(fenceValues[frameIndex], fenceEvent!);
            Wait("next-frame");
        }
        fenceValues[frameIndex] = current + 1;
    }

    void WaitForGpu(string waitSite = "idle")
    {
        if (queue == null || fence == null) return;
        ulong value = fenceValues[frameIndex];
        queue.Signal(fence, value);
        fence.SetEventOnCompletion(value, fenceEvent!);
        Wait(waitSite);
        // Every slot is idle now, so they all share the next value. Without this, a resize (which resets
        // frameIndex to the swap chain's index) would make a later frame signal a *lower* value than one
        // already signalled, and whichever frame was waiting on the higher value would never be woken.
        for (int i = 0; i < FrameCount; i++) fenceValues[i] = value + 1;
    }

    /// <summary>A fence that never signals means the device is gone or hung; surface that instead of blocking
    /// the render thread for ever.</summary>
    void Wait(string where)
    {
        if (!fenceEvent!.WaitOne(5000))
            throw new TimeoutException($"the GPU did not signal within 5 s at {where} " +
                $"(frame {frameCounter}, index {frameIndex}, value {fenceValues[frameIndex]}, " +
                $"completed {fence?.CompletedValue}, removal 0x{device?.DeviceRemovedReason.Code ?? 0:X8})");
    }

    // ---------------------------------------------------------------- teardown

    void Shutdown()
    {
        try { WaitForGpu(); } catch { /* device already gone */ }

        Particles.Dispose();
        readback?.Dispose();
        for (int i = 0; i < RecordRing; i++) { recordBuffers[i]?.Dispose(); recordBuffers[i] = null; }
        vertexBuffer?.Dispose();
        depth?.Dispose();
        msaaColor?.Dispose();
        for (int i = 0; i < FrameCount; i++) { backBuffers[i]?.Dispose(); backBuffers[i] = null; }
        psoClay?.Dispose();
        psoGhost?.Dispose();
        psoFlat?.Dispose();
        psoLine?.Dispose();
        psoBoxEdges?.Dispose();
        psoGlyphHalo?.Dispose();
        psoHandle?.Dispose();
        psoBackground?.Dispose();
        timeHeap?.Dispose();
        timeReadback?.Dispose();
        timeHeap = null;
        timeReadback = null;
        rootSignature?.Dispose();
        cmd?.Dispose();
        for (int i = 0; i < FrameCount; i++) { allocators[i]?.Dispose(); allocators[i] = null; }
        rtvHeap?.Dispose();
        dsvHeap?.Dispose();
        fence?.Dispose();
        fenceEvent?.Dispose();
        swapChain?.Dispose();
        queue?.Dispose();
        device?.Dispose();
        factory?.Dispose();
        readback = null; vertexBuffer = null; depth = null; msaaColor = null;
        psoClay = psoGhost = psoFlat = psoLine = psoBackground = psoHandle = psoBoxEdges = psoGlyphHalo = null;
        rootSignature = null; cmd = null; rtvHeap = dsvHeap = null; fence = null; fenceEvent = null;
        swapChain = null; queue = null; device = null; factory = null;

#if DEBUG
        if (DXGI.DXGIGetDebugInterface1(out Vortice.DXGI.Debug.IDXGIDebug1? dxgiDebug).Success && dxgiDebug != null)
        {
            dxgiDebug.ReportLiveObjects(new Guid("e48ae283-da80-490b-87e6-43e9a9cfda08"),   // DXGI_DEBUG_ALL
                Vortice.DXGI.Debug.ReportLiveObjectFlags.Summary | Vortice.DXGI.Debug.ReportLiveObjectFlags.IgnoreInternal);
            dxgiDebug.Dispose();
        }
#endif
    }

    static Vector4 Linear(uint rgb, float alpha = 1f)
    {
        static float S(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        return new Vector4(S(((rgb >> 16) & 0xFF) / 255f), S(((rgb >> 8) & 0xFF) / 255f), S((rgb & 0xFF) / 255f), alpha);
    }
}

/// <summary>What a --bench run measured, handed back to the UI thread to be written out.</summary>
public sealed record BenchResult(string Adapter, int Width, int Height, float[] FrameMs, byte[] Pixels,
    int RowPitch, int Particles, string ParticleInfo, byte[] Pixels2);

/// <summary>An axis-aligned box handed to the render thread in one reference (a Vector3 pair could tear).</summary>
public sealed record BandBox(Vector3 Min, Vector3 Max);

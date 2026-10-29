using System.Numerics;
using Avalonia.Controls;
using Avalonia.Platform;

namespace WindFlow.App.Viewport;

/// <summary>The D3D12 viewport, embedded as a child HWND through Avalonia's NativeControlHost with a DXGI
/// flip-model swap chain.
/// ponytail: a child HWND is an airspace island — Avalonia controls cannot be drawn on top of it, so overlays
/// (legend, gizmo labels, tooltips) must live outside the viewport rectangle until this moves to Avalonia's
/// composition GPU interop (ICompositionGpuInterop + a shared D3D11-on-12 texture), which keeps the same
/// renderer and only swaps the presentation path. Chosen for phase 1 because it is ~80 lines, has no
/// composition-backend dependency, and gives an unthrottled flip chain with exact DPI-correct sizing.</summary>
public sealed class ViewportHost : NativeControlHost
{
    const string ClassName = "WindFlowViewport";
    static readonly Dictionary<IntPtr, ViewportHost> Hosts = new();
    static Win32.WndProc? classProc;   // must outlive every window of the class
    static bool classRegistered;

    public readonly Renderer Renderer = new();

    /// <summary>Raised on the UI thread when the G key toggled ghost mode inside the viewport.</summary>
    public event Action<bool>? GhostChanged;

    /// <summary>Raised on the UI thread when the P key toggled the particles inside the viewport.</summary>
    public event Action<bool>? ParticlesChanged;

    /// <summary>Raised on the UI thread when the V key switched the particle colouring.</summary>
    public event Action<ParticleView>? ViewChanged;

    /// <summary>Raised on the UI thread when an opening was clicked in the viewport. The live solver driver
    /// subscribes here: the handler owns flipping <see cref="WindFlow.Core.Opening.IsOpen"/> and rebuilding
    /// the scene, so a warm restart can be queued from the same place.</summary>
    public event Action<WindFlow.Core.Opening>? OpeningToggled;

    /// <summary>A room floor was clicked (index into BuildingModel.Rooms).</summary>
    public event Action<int>? RoomPicked;
    /// <summary>True while Ctrl is held (a Ctrl+click on a room floor adds it to the selection).</summary>
    public static bool CtrlDown => Win32.GetKeyState(0x11) < 0;
    /// <summary>Esc with the viewport focused: clear the room/opening selection (or cancel a placement).</summary>
    public event Action? Escaped;
    /// <summary>Delete with the viewport focused: the window deletes the selected opening.</summary>
    public event Action? DeleteRequested;
    /// <summary>R with the viewport focused: turn the device being placed, or the selected one.</summary>
    public event Action? Rotated;

    /// <summary>A probe pin was dropped at this world point.</summary>
    public event Action<System.Numerics.Vector3>? ProbePlaced;

    /// <summary>S and B, so the panel toggles stay in step with the keyboard.</summary>
    public event Action? SliceToggled;
    public event Action<bool>? ProbeModeChanged;

    /// <summary>Set by the shell: where a probe click lands when no room or slice is under the cursor.</summary>
    public float GroundZ;

    public bool ProbeMode;

    /// <summary>Placing a device from the palette: moves report the cursor so the window can draw a ghost on
    /// the surface under it, and a click drops the device there. Same shape as <see cref="ProbeMode"/>.</summary>
    public bool DevicePlaceMode;
    /// <summary>A locked result (MainWindow.SetLocked): the camera, slice handles, probes and selection stay live;
    /// anything that would change the flow (opening clicks, the wind arrow, box handles, device drags) is ignored.</summary>
    public volatile bool Locked;
    public event Action<int, int>? DeviceHovered;
    public event Action<int, int>? DevicePlaced;
    public event Action<int>? DevicePicked;
    /// <summary>Dragging an existing device: the window re-runs the surface pick, so it slides along the
    /// ceiling or wall it is mounted on rather than through the air.</summary>
    public event Action<int, int, int>? DeviceDragged;
    /// <summary>Raised once, when a press on a device has moved past the 4 px click threshold: index and the
    /// PRESS position, so the handler can keep the grab offset. DeviceDragged only follows after this.</summary>
    public event Action<int, int, int>? DeviceDragBegan;
    /// <summary>Set for the drag that DeviceDragBegan announces: the world axis a gizmo arrow constrains it
    /// to, or null for a free slide in the mount plane (the body or the square).</summary>
    public Vector3? DeviceDragAxis;
    /// <summary>The selected device's yaw ring is being dragged: index and the new compass heading in degrees.</summary>
    public event Action<int, float>? DeviceYawDragged;

    /// <summary>A gizmo drag changed something the shell owns: slice position, box, or wind bearing.</summary>
    public event Action? SliceDragged;
    public event Action? BoxDragged;
    public event Action<double>? WindDragged;
    public event Action? WallModeCycled;

    IntPtr hwnd;
    int lastX, lastY, downX, downY;
    int dragButton;   // 0 none, 1 orbit, 2 pan, 3 gizmo handle, 4 device
    int draggingDevice = -1;
    bool deviceDragStarted;
    // A ring drag is relative: the value it showed when grabbed, and the cursor's bearing about the pivot then.
    double turnValue0, turnAngle0;
    bool turnRelative;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        EnsureClass();
        hwnd = Win32.CreateWindowEx(0, ClassName, null,
            Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPSIBLINGS | Win32.WS_CLIPCHILDREN,
            0, 0, 1, 1, parent.Handle, IntPtr.Zero, Win32.GetModuleHandle(null), IntPtr.Zero);
        Hosts[hwnd] = this;
        Renderer.Start(hwnd);
        return new PlatformHandle(hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Renderer.Stop();
        if (hwnd != IntPtr.Zero)
        {
            Hosts.Remove(hwnd);
            Win32.DestroyWindow(hwnd);
            hwnd = IntPtr.Zero;
        }
    }

    /// <summary>Applies one frame of a handle drag. Every handle is constrained: an axis, the ground plane,
    /// or a rotation about the building centre, so a drag can never move something in a way the user cannot
    /// predict from the handle they grabbed.</summary>
    void DragHandle(int x, int y)
    {
        int id = Renderer.ActiveHandle;
        if (id < 0 || !Gizmos.TryFind(Renderer, id, out var handle)) return;
        var particles = Renderer.Particles;
        var (min, max) = Renderer.Box;

        switch (id)
        {
            case Gizmos.SliceZ:
                if (Gizmos.AxisDrag(Renderer, x, y, handle.Position, Vector3.UnitZ, out float dz))
                {
                    particles.SliceZ = Math.Clamp(handle.Position.Z + dz, particles.FocusMin.Z, particles.FocusMax.Z);
                    SliceDragged?.Invoke();
                }
                return;

            case Gizmos.SliceCut:
            {
                var axis = particles.SliceAxis == 1 ? Vector3.UnitX : Vector3.UnitY;
                if (Gizmos.AxisDrag(Renderer, x, y, handle.Position, axis, out float d))
                {
                    float current = particles.SliceAxis == 1 ? handle.Position.X : handle.Position.Y;
                    float lo = particles.SliceAxis == 1 ? particles.FocusMin.X : particles.FocusMin.Y;
                    float hi = particles.SliceAxis == 1 ? particles.FocusMax.X : particles.FocusMax.Y;
                    particles.SliceOther = Math.Clamp(current + d, lo, hi);
                    SliceDragged?.Invoke();
                }
                return;
            }

            case Gizmos.Wind:
            {
                if (Renderer.CurrentScene is not { } scene) return;
                // The handle sits upwind, so the bearing it points at IS the direction the wind comes from. The
                // ring can be grabbed anywhere along it, so the drag is relative: the bearing swings by as much
                // as the cursor has swung about the building, and nothing jumps on the first pixel.
                var pivot = new Vector3(scene.Center.X, scene.Center.Y, handle.Position.Z);
                if (Gizmos.Bearing(Renderer, x, y, pivot) is not { } cursor) return;
                double from = turnRelative ? turnValue0 + (cursor - turnAngle0) : cursor;
                WindDragged?.Invoke(((from % 360.0) + 360.0) % 360.0);
                return;
            }

            case Gizmos.DevYaw:
            {
                if (Gizmos.Bearing(Renderer, x, y, handle.Position) is not { } cursor) return;
                double yaw = turnRelative ? turnValue0 + (cursor - turnAngle0) : cursor;
                DeviceYawDragged?.Invoke(Renderer.SelectedDevice, (float)(((yaw % 360.0) + 360.0) % 360.0));
                return;
            }

            case Gizmos.BoxCentre:
            {
                if (!Gizmos.PlaneDrag(Renderer, x, y, handle.Position.Z, out var point)) return;
                var half = (max - min) * 0.5f;
                var centre = new Vector3(point.X, point.Y, handle.Position.Z);
                Renderer.SetBox(centre - half, centre + half);
                BoxDragged?.Invoke();
                return;
            }
        }

        // Box faces: slide one face along its own axis, never past the opposite face.
        const float MinSize = 0.5f;
        var faceAxis = id is Gizmos.BoxXMin or Gizmos.BoxXMax ? Vector3.UnitX
            : id is Gizmos.BoxYMin or Gizmos.BoxYMax ? Vector3.UnitY : Vector3.UnitZ;
        if (!Gizmos.AxisDrag(Renderer, x, y, handle.Position, faceAxis, out float delta)) return;
        switch (id)
        {
            case Gizmos.BoxXMin: min.X = MathF.Min(handle.Position.X + delta, max.X - MinSize); break;
            case Gizmos.BoxXMax: max.X = MathF.Max(handle.Position.X + delta, min.X + MinSize); break;
            case Gizmos.BoxYMin: min.Y = MathF.Min(handle.Position.Y + delta, max.Y - MinSize); break;
            case Gizmos.BoxYMax: max.Y = MathF.Max(handle.Position.Y + delta, min.Y + MinSize); break;
            case Gizmos.BoxZMin: min.Z = MathF.Min(handle.Position.Z + delta, max.Z - MinSize); break;
            case Gizmos.BoxZMax: max.Z = MathF.Max(handle.Position.Z + delta, min.Z + MinSize); break;
        }
        Renderer.SetBox(min, max);
        BoxDragged?.Invoke();
    }

    /// <summary>A ring was grabbed: remember the value it shows and the cursor's bearing about its pivot, so the
    /// drag turns by the cursor's swing instead of snapping to wherever the ring was grabbed.</summary>
    void BeginTurn(int id, int x, int y)
    {
        turnRelative = false;
        if (id is not (Gizmos.Wind or Gizmos.DevYaw)) return;
        if (!Gizmos.TryFind(Renderer, id, out var handle) || Renderer.CurrentScene is not { } scene) return;
        Vector3 pivot;
        if (id == Gizmos.Wind)
        {
            pivot = new Vector3(scene.Center.X, scene.Center.Y, handle.Position.Z);
            turnValue0 = Math.Atan2(handle.Position.X - pivot.X, handle.Position.Y - pivot.Y) * 180.0 / Math.PI;
        }
        else
        {
            pivot = handle.Position;
            foreach (var d in scene.DevicePicks)
                if (d.Index == Renderer.SelectedDevice) turnValue0 = d.Yaw;
        }
        if (Gizmos.Bearing(Renderer, x, y, pivot) is { } angle) { turnAngle0 = angle; turnRelative = true; }
    }

    void Clicked(int x, int y)
    {
        if (ProbeMode)
        {
            if (Renderer.ProbePoint(x, y, GroundZ) is { } point) ProbePlaced?.Invoke(point);
            return;
        }
        if (DevicePlaceMode) { DevicePlaced?.Invoke(x, y); return; }
        int device = Locked ? -1 : Renderer.PickDeviceAt(x, y);
        if (device >= 0) { DevicePicked?.Invoke(device); return; }
        if (Renderer.OpeningAt(Renderer.PickAt(x, y)) is { } opening) { OpeningToggled?.Invoke(opening); return; }   // the window refuses it while locked, and says why
        int room = Renderer.PickRoomAt(x, y);
        if (room >= 0) RoomPicked?.Invoke(room);
    }

    static void EnsureClass()
    {
        if (classRegistered) return;
        classProc = Proc;
        var cls = new Win32.WndClassEx
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.WndClassEx>(),
            style = 0,
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(classProc),
            hInstance = Win32.GetModuleHandle(null),
            hCursor = Win32.LoadCursor(IntPtr.Zero, 32512),   // IDC_ARROW
            hbrBackground = IntPtr.Zero,
            lpszClassName = ClassName,
        };
        if (Win32.RegisterClassEx(ref cls) == 0)
            throw new InvalidOperationException("Could not register the viewport window class");
        classRegistered = true;
    }

    // Runs on the UI thread: Avalonia pumps this child window's messages for us.
    static IntPtr Proc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Hosts.TryGetValue(hWnd, out var host) && host.Handle(hWnd, msg, wParam, lParam, out var result))
            return result;
        return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    bool Handle(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (!Renderer.InputEnabled && msg is not (Win32.WM_ERASEBKGND or Win32.WM_PAINT)) return false;
        switch (msg)
        {
            case Win32.WM_ERASEBKGND:
                result = 1;                      // the render thread owns every pixel
                return true;
            case Win32.WM_PAINT:
                Win32.ValidateRect(hWnd, IntPtr.Zero);
                return true;

            case Win32.WM_LBUTTONDOWN:
            case Win32.WM_RBUTTONDOWN:
            case Win32.WM_MBUTTONDOWN:
                lastX = downX = Win32.LoWord(lParam);
                lastY = downY = Win32.HiWord(lParam);
                // A handle under the cursor takes the drag; the camera must not orbit at the same time.
                int grabbed = msg == Win32.WM_LBUTTONDOWN ? Gizmos.Pick(Renderer, lastX, lastY) : -1;
                if (Locked && grabbed is not (Gizmos.SliceZ or Gizmos.SliceCut)) grabbed = -1;
                // The device gizmo's square and arrows ARE the body drag, with a constraint: same path, same events.
                bool deviceHandle = (grabbed is Gizmos.DevMove or Gizmos.DevU or Gizmos.DevV) && Renderer.SelectedDevice >= 0;
                int overDevice = msg == Win32.WM_LBUTTONDOWN && grabbed < 0 && !DevicePlaceMode && !Locked
                    ? Renderer.PickDeviceAt(lastX, lastY) : -1;
                if (deviceHandle)
                {
                    Renderer.ActiveHandle = grabbed;
                    DeviceDragAxis = grabbed != Gizmos.DevMove && Gizmos.TryFind(Renderer, grabbed, out var arrow)
                        ? arrow.Direction : null;
                    draggingDevice = Renderer.SelectedDevice;
                    dragButton = 4;
                    deviceDragStarted = false;
                }
                else if (grabbed >= 0)
                {
                    Renderer.ActiveHandle = grabbed;
                    dragButton = 3;
                    BeginTurn(grabbed, lastX, lastY);
                }
                // A device under the cursor takes the drag, so the camera cannot orbit while it slides.
                else if (overDevice >= 0) { DeviceDragAxis = null; draggingDevice = overDevice; dragButton = 4; deviceDragStarted = false; }
                else dragButton = msg == Win32.WM_LBUTTONDOWN ? 1 : 2;
                Win32.SetCapture(hWnd);
                Win32.SetFocus(hWnd);            // so the wheel and G/F reach us
                return true;

            case Win32.WM_LBUTTONUP:
            case Win32.WM_RBUTTONUP:
            case Win32.WM_MBUTTONUP:
            {
                int x = Win32.LoWord(lParam), y = Win32.HiWord(lParam);
                bool click = msg == Win32.WM_LBUTTONUP && dragButton is not (3 or 4) &&
                             Math.Abs(x - downX) < 4 && Math.Abs(y - downY) < 4;
                if (dragButton is 3 or 4) Renderer.ActiveHandle = -1;
                if (dragButton == 4)
                {
                    // A press with no movement is a select, not a drag.
                    if (!deviceDragStarted) DevicePicked?.Invoke(draggingDevice);
                    draggingDevice = -1;
                }
                dragButton = 0;
                Win32.ReleaseCapture();
                if (click) Clicked(x, y);
                return true;
            }

            case Win32.WM_MOUSEMOVE:
            {
                int x = Win32.LoWord(lParam), y = Win32.HiWord(lParam);
                int dx = x - lastX, dy = y - lastY;
                lastX = x;
                lastY = y;
                if (dragButton == 4)
                {
                    // A click must not nudge the device: nothing moves until the press has travelled 4 px.
                    if (!deviceDragStarted)
                    {
                        if (Math.Abs(x - downX) < 4 && Math.Abs(y - downY) < 4) return true;
                        deviceDragStarted = true;
                        DeviceDragBegan?.Invoke(draggingDevice, downX, downY);
                    }
                    DeviceDragged?.Invoke(draggingDevice, x, y);
                }
                else if (dragButton == 3) DragHandle(x, y);
                else if (dragButton == 1) lock (Renderer.Camera.Sync) Renderer.Camera.Orbit(dx, dy);
                else if (dragButton == 2) lock (Renderer.Camera.Sync) Renderer.Camera.Pan(dx, dy, Math.Max(Renderer.Height, 1));
                else
                {
                    if (DevicePlaceMode) { DeviceHovered?.Invoke(x, y); return true; }
                    int handle = Gizmos.Pick(Renderer, x, y);
                    Renderer.HoverHandle = handle;
                    int dev = handle >= 0 ? -1 : Renderer.PickDeviceAt(x, y);
                    Renderer.HoverDevice = dev;
                    int op = handle >= 0 || dev >= 0 ? -1 : Renderer.PickAt(x, y);
                    Renderer.HoverOpening = op;
                    Renderer.HoverRoom = handle >= 0 || dev >= 0 || op >= 0 ? -1 : Renderer.PickRoomAt(x, y);
                }
                return true;
            }

            case Win32.WM_MOUSEWHEEL:
                lock (Renderer.Camera.Sync) Renderer.Camera.Zoom(Win32.HiWord(wParam));
                return true;

            case Win32.WM_KEYDOWN:
                switch ((int)wParam)
                {
                    case 0x47:                    // G
                        Renderer.Ghost = !Renderer.Ghost;
                        GhostChanged?.Invoke(Renderer.Ghost);
                        return true;
                    case 0x50:                    // P
                        Renderer.Particles.Enabled = !Renderer.Particles.Enabled;
                        ParticlesChanged?.Invoke(Renderer.Particles.Enabled);
                        return true;
                    case 0x56:                    // V
                        Renderer.Particles.View = Renderer.Particles.View == ParticleView.Speed
                            ? ParticleView.Still : ParticleView.Speed;
                        ViewChanged?.Invoke(Renderer.Particles.View);
                        return true;
                    case 0x58:                    // X
                        WallModeCycled?.Invoke();
                        return true;
                    case 0x53:                    // S
                        SliceToggled?.Invoke();
                        return true;
                    case 0x42:                    // B
                        ProbeMode = !ProbeMode;
                        ProbeModeChanged?.Invoke(ProbeMode);
                        return true;
                    case 0x46:                    // F
                        Renderer.FrameModel();
                        return true;
                    case 0x1B:                    // Esc
                        Escaped?.Invoke();
                        return true;
                    case 0x52:                    // R
                        Rotated?.Invoke();
                        return true;
                    case 0x2E:                    // Delete
                        DeleteRequested?.Invoke();
                        return true;
                }
                return false;
        }
        return false;
    }
}

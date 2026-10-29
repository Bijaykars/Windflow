using System.Runtime.InteropServices;

namespace WindFlow.App.Viewport;

/// <summary>The few Win32 calls the child-HWND viewport needs. Nothing here is Avalonia-specific.</summary>
internal static class Win32
{
    public const int WM_DESTROY = 0x0002, WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014;
    public const int WM_KEYDOWN = 0x0100, WM_MOUSEMOVE = 0x0200, WM_MOUSEWHEEL = 0x020A;
    public const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    public const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;

    public const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;
    public const uint WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WndClassEx
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WndClassEx cls);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", CharSet = CharSet.Unicode)]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int vKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ValidateRect(IntPtr hWnd, IntPtr rect);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "LoadCursorW", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    /// <summary>Paints the title bar dark on Windows 10 1809+; a light caption on a black app looks broken.</summary>
    public static void UseDarkTitleBar(IntPtr hWnd)
    {
        int on = 1;
        if (DwmSetWindowAttribute(hWnd, 20, ref on, sizeof(int)) != 0)   // DWMWA_USE_IMMERSIVE_DARK_MODE
            DwmSetWindowAttribute(hWnd, 19, ref on, sizeof(int));        // pre-20H1 attribute number
    }

    public static int LoWord(IntPtr value) => (short)((long)value & 0xFFFF);
    public static int HiWord(IntPtr value) => (short)(((long)value >> 16) & 0xFFFF);

    /// <summary>Client size in physical pixels (Avalonia sizes the child window for us, DPI included).</summary>
    public static (int Width, int Height) ClientSize(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !GetClientRect(hWnd, out var r)) return (0, 0);
        return (Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string? device, int mode, ref DevMode devMode);

    /// <summary>The primary display refresh rate in Hz (60 when Windows will not say; 0 and 1 mean "default").
    /// ponytail: primary display only; a window on a second monitor with another rate would need
    /// MonitorFromWindow + GetMonitorInfo for the device name.</summary>
    public static int RefreshRate()
    {
        var mode = new DevMode { DeviceName = "", FormName = "" };
        mode.Size = (short)Marshal.SizeOf<DevMode>();
        return EnumDisplaySettings(null, -1, ref mode) && mode.DisplayFrequency > 1 ? mode.DisplayFrequency : 60;
    }
}

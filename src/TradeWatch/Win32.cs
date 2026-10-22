using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace MtgoTradeAutomations.TradeWatch;

internal static class Win32
{
    public static Dictionary<string, IntPtr> TopLevelWindows(int pid)
    {
        var windows = new Dictionary<string, IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid && IsWindowVisible(hwnd))
            {
                var buffer = new StringBuilder(256);
                GetWindowText(hwnd, buffer, buffer.Capacity);
                windows[buffer.ToString()] = hwnd;
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    /// <summary>The top-level window under a screen point (physical pixels).</summary>
    public static IntPtr RootWindowAt(Point point) => GetAncestor(WindowFromPoint(new POINT { X = point.X, Y = point.Y }), GaRoot);

    public static Point CursorPosition()
    {
        GetCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    public static void MouseMove(Point point)
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = Math.Max(2, GetSystemMetrics(SmCxVirtualScreen));
        var height = Math.Max(2, GetSystemMetrics(SmCyVirtualScreen));
        Send(MouseInput(
            (int)Math.Round((point.X - left) * 65535.0 / (width - 1)),
            (int)Math.Round((point.Y - top) * 65535.0 / (height - 1)),
            MouseMoveFlag | MouseAbsolute | MouseVirtualDesk));
    }

    public static void MouseLeft(bool down) => Send(MouseInput(0, 0, down ? MouseLeftDown : MouseLeftUp));

    public static void Key(ushort virtualKey, bool down) => Send(new INPUT
    {
        Type = InputKeyboard,
        U = new InputUnion { Ki = new KEYBDINPUT { Vk = virtualKey, Flags = down ? 0u : KeyUp } },
    });

    public const ushort VkControl = 0x11;
    public const ushort VkA = 0x41;

    private static INPUT MouseInput(int dx, int dy, uint flags) => new()
    {
        Type = InputMouse,
        U = new InputUnion { Mi = new MOUSEINPUT { Dx = dx, Dy = dy, Flags = flags } },
    };

    private static void Send(INPUT input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1)
        {
            throw new InvalidOperationException($"SendInput failed: {Marshal.GetLastWin32Error()}");
        }
    }

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseMoveFlag = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseVirtualDesk = 0x4000;
    private const uint MouseAbsolute = 0x8000;
    private const uint KeyUp = 0x0002;
    private const uint GaRoot = 2;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    // Per-monitor v2: UIA rectangles, cursor and SendInput all use physical pixels.
    public static readonly IntPtr DpiAwarenessPerMonitorV2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mi;
        [FieldOffset(0)] public KEYBDINPUT Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}

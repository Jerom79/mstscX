using System.Runtime.InteropServices;

namespace mstscX.WinUI;

// Keep the existing HWND and OverlappedPresenter when WinUI fullscreen transitions
// block in an outer RDP session. The ActiveX host and native drag loop stay in place.
internal sealed class BorderlessSessionWindow
{
    private const int StyleIndex = -16;
    private const int FrameStyles = 0x00CF0000; // WS_OVERLAPPEDWINDOW
    private const int Maximized = 0x01000000;
    private const int MinimizedOrMaximized = 0x21000000;
    private const uint FrameChangedWithoutActivation = 0x0034;
    private readonly nint window;
    private readonly int originalStyle;
    private readonly Rect originalBounds;
    private readonly Rect normalBounds;

    private BorderlessSessionWindow(nint window, int style, Rect originalBounds, Rect normalBounds)
    {
        this.window = window;
        originalStyle = style;
        this.originalBounds = originalBounds;
        this.normalBounds = normalBounds;
    }

    internal static bool IsRemoteSession => GetSystemMetrics(0x1000) != 0; // SM_REMOTESESSION

    internal static BorderlessSessionWindow? TryEnter(nint window)
    {
        var placement = new WindowPlacement { Size = (uint)Marshal.SizeOf<WindowPlacement>() };
        var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetWindowPlacement(window, ref placement) ||
            !GetWindowRect(window, out var originalBounds) ||
            !GetMonitorInfo(MonitorFromWindow(window, 2), ref monitor))
            return null;

        int style = GetWindowLong(window, StyleIndex);
        var normalBounds = originalBounds;
        if ((style & MinimizedOrMaximized) != 0)
        {
            normalBounds = placement.NormalPosition;
            const int extendedStyle = -20;
            const int toolWindow = 0x0080;
            if ((GetWindowLong(window, extendedStyle) & toolWindow) == 0)
            {
                // WINDOWPLACEMENT is in workspace coordinates for regular top-level windows.
                // Convert it before using SetWindowPos, including top/left taskbars.
                int x = monitor.Work.Left - monitor.Bounds.Left;
                int y = monitor.Work.Top - monitor.Bounds.Top;
                normalBounds.Left += x;
                normalBounds.Right += x;
                normalBounds.Top += y;
                normalBounds.Bottom += y;
            }
        }
        if (normalBounds.Right <= normalBounds.Left || normalBounds.Bottom <= normalBounds.Top)
            return null;

        var state = new BorderlessSessionWindow(window, style, originalBounds, normalBounds);
        FullScreenTransitionTrace.Write("Enter borderless session window begin");
        if (SetStyle(window, style & ~(FrameStyles | MinimizedOrMaximized)))
        {
            var bounds = monitor.Bounds;
            if (SetWindowPos(window, 0, bounds.Left, bounds.Top, bounds.Right - bounds.Left,
                bounds.Bottom - bounds.Top, FrameChangedWithoutActivation))
            {
                FullScreenTransitionTrace.Write("Enter borderless session window end");
                return state;
            }
            FullScreenTransitionTrace.Write($"Borderless window positioning failed error={Marshal.GetLastPInvokeError()}");
            state.Restore(forceRestored: false);
        }
        return null;
    }

    internal bool Restore(bool forceRestored)
    {
        if (!IsWindow(window))
            return false;

        FullScreenTransitionTrace.Write("Restore borderless window frame begin");
        int style = (GetWindowLong(window, StyleIndex) & ~(FrameStyles | MinimizedOrMaximized)) |
            (originalStyle & FrameStyles);
        if (!SetStyle(window, style))
            return false;
        FullScreenTransitionTrace.Write("Restore borderless window frame end");

        // SetWindowPlacement itself blocked for 8.1 seconds in the remote-machine trace.
        // The HWND is already visible/restored: resize it without a ShowWindow state change.
        // This also restores Windows' normal-position bookkeeping before optionally maximizing.
        FullScreenTransitionTrace.Write("Restore normal window bounds via SetWindowPos begin");
        bool positioned = Position(normalBounds);
        FullScreenTransitionTrace.Write($"Restore normal window bounds via SetWindowPos end success={positioned}");
        if (!positioned || forceRestored || (originalStyle & Maximized) == 0)
            return positioned;

        FullScreenTransitionTrace.Write("Restore maximized window bounds begin");
        bool maximized = SetStyle(window, style | Maximized) && Position(originalBounds);
        FullScreenTransitionTrace.Write($"Restore maximized window bounds end success={maximized}");
        return maximized;
    }

    private bool Position(Rect bounds) => SetWindowPos(window, 0, bounds.Left, bounds.Top,
        bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
        FrameChangedWithoutActivation | 0x0400); // SWP_NOSENDCHANGING, as in the native drag handoff.

    private static bool SetStyle(nint window, int style)
    {
        Marshal.SetLastPInvokeError(0);
        int previous = SetWindowLong(window, StyleIndex, style);
        int error = Marshal.GetLastPInvokeError();
        if (previous != 0 || error == 0)
            return true;
        FullScreenTransitionTrace.Write($"Set session window style failed error={error}");
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint Size; public Rect Bounds, Work; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public uint Size, Flags, ShowCommand;
        public Point MinPosition, MaxPosition;
        public Rect NormalPosition;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(nint window, ref WindowPlacement placement);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect bounds);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}

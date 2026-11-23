using System.Runtime.InteropServices;
using Windows.Graphics;

namespace mstscX.WinUI;

// The same Windows caption move loop as the WinUI path, using HWND geometry only.
// No AppWindow wrapper is attached to the plain Win32 session window.
internal static class NativeSessionWindowDrag
{
    internal static bool PositionUnderPointer(nint window, PointInt32 fallback)
    {
        if (!GetWindowRect(window, out var bounds))
            return false;
        var point = GetCursorPos(out var current) ? current : fallback;
        return SetWindowPos(window, 0, point.X - (bounds.Right - bounds.Left) / 2,
            point.Y - TitleOffset(window), 0, 0, 0x0415);
    }

    internal static void Continue(nint window, Action finished, Action snapToFullScreen)
    {
        try
        {
            if (!IsWindow(window) || !FullScreenPanelDrag.IsPrimaryMouseButtonDown || !GetCursorPos(out var cursor))
            {
                FullScreenTransitionTrace.Write("Win32 pointer released before handoff");
                return;
            }
            FullScreenTransitionTrace.Write("Win32 native caption move begin");
            SubclassProc align = AlignTitle;
            bool installed = SetWindowSubclass(window, align, 0x57445247, 0);
            try
            {
                // DefWindowProc enters Windows' normal modal move loop until release.
                DefWindowProc(window, 0x00A1, 2, (cursor.X & 0xffff) | (cursor.Y << 16));
            }
            finally
            {
                if (installed && IsWindow(window))
                    RemoveWindowSubclass(window, align, 0x57445247);
                GC.KeepAlive(align);
            }
            FullScreenTransitionTrace.Write("Win32 native caption move end");
        }
        finally
        {
            FullScreenTransitionTrace.FinishDrag();
            finished();
        }
        if (IsWindow(window) && !FullScreenPanelDrag.IsPrimaryMouseButtonDown && IsPointerAtTop())
            snapToFullScreen();
    }

    internal static bool IsPointerAtTop()
    {
        if (!GetCursorPos(out var pointer))
            return false;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(MonitorFromPoint(pointer, 2), ref monitor) &&
            pointer.Y >= monitor.Bounds.Top && pointer.Y < monitor.Bounds.Top + 8;
    }

    private static nint AlignTitle(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        nint result = DefSubclassProc(window, message, wParam, lParam);
        if (message == 0x0216 && lParam != 0 && GetCursorPos(out var pointer))
        {
            var rect = Marshal.PtrToStructure<Rect>(lParam);
            int height = rect.Bottom - rect.Top;
            rect.Top = pointer.Y - TitleOffset(window);
            rect.Bottom = rect.Top + height;
            Marshal.StructureToPtr(rect, lParam, false);
            return 1;
        }
        return result;
    }

    private static int TitleOffset(nint window)
    {
        var title = new TitleBarInfo { Size = (uint)Marshal.SizeOf<TitleBarInfo>() };
        if (GetWindowRect(window, out var bounds) && GetTitleBarInfo(window, ref title) &&
            (title.State & 0x8000) == 0 && title.Bounds.Bottom > title.Bounds.Top)
            return title.Bounds.Top - bounds.Top + (title.Bounds.Bottom - title.Bounds.Top) / 2;
        uint dpi = GetDpiForWindow(window);
        return GetSystemMetricsForDpi(4, dpi) / 2 + GetSystemMetricsForDpi(33, dpi) + GetSystemMetricsForDpi(92, dpi);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Bounds, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct TitleBarInfo
    {
        public uint Size;
        public Rect Bounds;
        public uint State, Reserved, Minimize, Maximize, Help, Close;
    }
    private delegate nint SubclassProc(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointInt32 point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetTitleBarInfo(nint window, ref TitleBarInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(PointInt32 point, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);
}

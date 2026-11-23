using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace mstscX.WinUI;

internal sealed class FullScreenPanelDrag
{
    private const int DragThreshold = 4;
    private const int TopSnapHeight = 8;
    private const int SwapMouseButtons = 23;
    private const uint NearestMonitor = 2;
    private const uint MoveWithoutActivation = 0x0001 | 0x0004 | 0x0010 | 0x0400;
    private PointInt32? start;
    private (PointInt32 Point, long Time)? lastClick;
    private bool moved;

    internal bool IsCaptured => start.HasValue;
    internal static bool IsPrimaryMouseButtonDown =>
        GetAsyncKeyState(GetSystemMetrics(SwapMouseButtons) != 0 ? 2 : 1) < 0;

    internal void Begin(PointInt32 point)
    {
        start = point;
        moved = false;
    }

    internal bool TryLeaveTopEdge(PointInt32 point, int panelBottom)
    {
        if (start is not { } beginning)
            return false;

        moved |= Math.Abs(point.X - beginning.X) >= DragThreshold || Math.Abs(point.Y - beginning.Y) >= DragThreshold;
        if (!moved)
            return false;

        if (point.Y < panelBottom)
            return false;

        Cancel();
        return true;
    }

    internal bool TryGetHorizontalOffset(PointInt32 point, out int offset)
    {
        offset = start is { } beginning ? point.X - beginning.X : 0;
        return moved && start.HasValue;
    }

    internal bool Finish(PointInt32 point, int panelBottom, out PointInt32? dropPosition)
    {
        dropPosition = null;
        if (TryLeaveTopEdge(point, panelBottom))
        {
            dropPosition = point;
            return true;
        }

        start = null;
        if (moved)
        {
            lastClick = null;
            return false;
        }

        long now = Environment.TickCount64;
        if (lastClick is { } click && now - click.Time <= GetDoubleClickTime() &&
            Math.Abs(point.X - click.Point.X) < DragThreshold && Math.Abs(point.Y - click.Point.Y) < DragThreshold)
        {
            lastClick = null;
            return true;
        }

        lastClick = (point, now);
        return false;
    }

    internal void Cancel()
    {
        start = null;
        lastClick = null;
        moved = false;
    }

    internal static bool PositionUnderPointer(AppWindow window, PointInt32 startPoint)
    {
        nint handle = Microsoft.UI.Win32Interop.GetWindowFromWindowId(window.Id);
        if (!IsWindow(handle) ||
            window.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored })
            return false;

        var pointer = GetCursorPos(out var cursor) ? cursor : startPoint;
        var position = GetTitlePosition(window, pointer, GetTitleCenterOffset(window, handle));
        FullScreenTransitionTrace.Write("Prepare owner SetWindowPos begin");
        bool positioned = SetWindowPos(handle, 0, position.X, position.Y, 0, 0, MoveWithoutActivation);
        FullScreenTransitionTrace.Write("Prepare owner SetWindowPos end");
        return positioned;
    }

    internal static void ContinueAtPointer(AppWindow window, PointInt32 startPoint, bool positioned,
        Action dragFinished, Action snapToFullScreen)
    {
        FullScreenTransitionTrace.Write("ContinueAtPointer begin");
        nint handle = Microsoft.UI.Win32Interop.GetWindowFromWindowId(window.Id);
        int titleCenterOffset = 0;
        try
        {
            if (!IsWindow(handle) ||
                window.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored })
                return;

            titleCenterOffset = GetTitleCenterOffset(window, handle);
            if (!IsPrimaryMouseButtonDown)
            {
                FullScreenTransitionTrace.Write("Pointer released before handoff");
                if (!positioned && TryGetWorkArea(MonitorFromPoint(startPoint, NearestMonitor), out var work))
                    MoveInsideWorkArea(window, work, GetTitlePosition(window, startPoint, titleCenterOffset));
                return;
            }

            var cursor = GetCursorPos(out var current) ? current : startPoint;
            if (!positioned)
            {
                var position = GetTitlePosition(window, cursor, titleCenterOffset);
                FullScreenTransitionTrace.Write("Initial SetWindowPos drag begin");
                SetWindowPos(handle, 0, position.X, position.Y, 0, 0, MoveWithoutActivation);
                FullScreenTransitionTrace.Write("Initial SetWindowPos drag end");
            }
            if (IsPrimaryMouseButtonDown)
            {
                const uint leftButtonDownOnFrame = 0x00A1;
                const int caption = 2;
                int coordinates = (cursor.X & 0xffff) | (cursor.Y << 16);
                FullScreenTransitionTrace.Write("Native caption move begin");
                SubclassProc alignTitle = AlignTitleDuringMove;
                bool trackingTitle = SetWindowSubclass(handle, alignTitle, 1, 0);
                try
                {
                    DefWindowProc(handle, leftButtonDownOnFrame, caption, coordinates);
                }
                finally
                {
                    if (trackingTitle)
                        RemoveWindowSubclass(handle, alignTitle, 1);
                }
                FullScreenTransitionTrace.Write("Native caption move end");
            }
        }
        finally
        {
            FullScreenTransitionTrace.FinishDrag();
            dragFinished();
        }

        if (IsWindow(handle) && !IsPrimaryMouseButtonDown && GetCursorPos(out var drop) &&
            DisplayArea.GetFromPoint(drop, DisplayAreaFallback.Nearest) is { } display &&
            drop.Y < display.OuterBounds.Y + TopSnapHeight)
            snapToFullScreen();

        nint AlignTitleDuringMove(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
        {
            const uint moving = 0x0216;
            nint result = DefSubclassProc(hwnd, message, wParam, lParam);
            if (message != moving || lParam == 0 || !GetCursorPos(out var pointer))
                return result;

            var rect = Marshal.PtrToStructure<Rect>(lParam);
            int height = rect.Bottom - rect.Top;
            rect.Top = pointer.Y - titleCenterOffset;
            rect.Bottom = rect.Top + height;
            Marshal.StructureToPtr(rect, lParam, false);
            return 1;
        }
    }

    private static PointInt32 GetTitlePosition(AppWindow window, PointInt32 point, int titleCenterOffset) =>
        new(point.X - window.Size.Width / 2, point.Y - titleCenterOffset);

    private static int GetTitleCenterOffset(AppWindow window, nint handle)
    {
        if (GetWindowRect(handle, out var bounds))
        {
            var info = new TitleBarInfo { Size = (uint)Marshal.SizeOf<TitleBarInfo>() };
            if (GetTitleBarInfo(handle, ref info) && (info.State & 0x8000) == 0 &&
                info.Bounds.Top >= bounds.Top && info.Bounds.Bottom > info.Bounds.Top &&
                info.Bounds.Bottom <= bounds.Bottom)
                return (info.Bounds.Top - bounds.Top) + (info.Bounds.Bottom - info.Bounds.Top) / 2;

            const uint extendedFrameBounds = 9;
            if (DwmGetWindowAttribute(handle, extendedFrameBounds, out var frame, Marshal.SizeOf<Rect>()) >= 0)
                return Math.Max(0, frame.Top - bounds.Top) + Math.Max(16, window.TitleBar.Height / 2);
        }

        return Math.Max(16, window.TitleBar.Height / 2);
    }

    internal static void EnsureWindowVisible(AppWindow window, nint displayWindow = 0)
    {
        if (window.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored })
            return;

        nint monitor = displayWindow == 0 ? GetWindowMonitor(window) : MonitorFromWindow(displayWindow, NearestMonitor);
        if (TryGetWorkArea(monitor, out var work))
            MoveInsideWorkArea(window, work, window.Position);
    }

    internal static void PlaceNewWindowOnOwnerDisplay(AppWindow window, AppWindow owner)
    {
        if (window.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored })
            return;

        nint monitor = GetWindowMonitor(owner);
        if (!TryGetWorkArea(monitor, out var work))
            return;

        var size = window.Size;
        var position = GetWindowMonitor(window) == monitor ? window.Position :
            new PointInt32(work.X + (work.Width - size.Width) / 2, work.Y + (work.Height - size.Height) / 2);
        MoveInsideWorkArea(window, work, position);
    }

    private static nint GetWindowMonitor(AppWindow window) =>
        MonitorFromWindow(Microsoft.UI.Win32Interop.GetWindowFromWindowId(window.Id), NearestMonitor);

    private static bool TryGetWorkArea(nint monitor, out RectInt32 work)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != 0 && GetMonitorInfo(monitor, ref info))
        {
            // Win32 reports the work area in virtual-screen coordinates, including the monitor offset.
            var rect = info.Work;
            work = new RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            return true;
        }

        work = default;
        return false;
    }

    private static void MoveInsideWorkArea(AppWindow window, RectInt32 work, PointInt32 position)
    {
        var size = window.Size;
        if (size.Width <= 0 || size.Height <= 0 || work.Width <= 0 || work.Height <= 0)
            return;

        int width = Math.Min(size.Width, work.Width);
        int height = Math.Min(size.Height, work.Height);
        int x = Math.Clamp(position.X, work.X, work.X + work.Width - width);
        int y = Math.Clamp(position.Y, work.Y, work.Y + work.Height - height);
        if (width != size.Width || height != size.Height)
            window.MoveAndResize(new RectInt32(x, y, width, height));
        else if (x != window.Position.X || y != window.Position.Y)
            window.Move(new PointInt32(x, y));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Bounds, Work; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TitleBarInfo
    {
        public uint Size;
        public Rect Bounds;
        public uint State, Reserved, Minimize, Maximize, Help, Close;
    }

    private delegate nint SubclassProc(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(PointInt32 point, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointInt32 point);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", ExactSpelling = true)]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out Rect bounds);

    [DllImport("user32.dll")]
    private static extern bool GetTitleBarInfo(nint window, ref TitleBarInfo info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, uint attribute, out Rect value, int size);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);
}

using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.Graphics;

namespace mstscX.WinUI;

// The menu uses WinUI pointer input. Do not hand that gesture to a nested
// Win32 caption loop: its button-up message may be consumed by WinUI.
internal static class MainWindowMenuDrag
{
    internal static void Start(Microsoft.UI.Xaml.Window owner, PointInt32 point)
    {
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        NativeSessionWindowDrag.PositionUnderPointer(handle, point);
        if (!FullScreenPanelDrag.IsPrimaryMouseButtonDown) return;

        var timer = owner.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.Tick += Move;
        owner.Closed += Closed;
        timer.Start();

        void Stop()
        {
            timer.Stop();
            timer.Tick -= Move;
            owner.Closed -= Closed;
        }

        void Closed(object sender, Microsoft.UI.Xaml.WindowEventArgs args) => Stop();

        void Move(DispatcherQueueTimer sender, object args)
        {
            // Check the physical button state before every move, even if the
            // original pointer capture was lost while restoring the window.
            if (!IsWindow(handle) || !FullScreenPanelDrag.IsPrimaryMouseButtonDown ||
                GetForegroundWindow() != handle)
            {
                Stop();
                return;
            }
            NativeSessionWindowDrag.PositionUnderPointer(handle, point);
        }
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}

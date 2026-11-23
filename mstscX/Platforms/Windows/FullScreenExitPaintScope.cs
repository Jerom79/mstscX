using System.Runtime.InteropServices;

namespace mstscX.WinUI;

internal sealed class FullScreenExitPaintScope : IDisposable
{
    private const nuint PaintSubclassId = 0x524445;
    private const uint TransitionsForcedDisabled = 3;
    private readonly List<nint> windows = [];
    private readonly SubclassProc windowProcedure;
    private readonly nint fullscreen;
    private readonly bool restoreTransitions;
    private int backgroundErasesSkipped;

    internal FullScreenExitPaintScope(nint fullscreen, nint host)
    {
        this.fullscreen = fullscreen;
        windowProcedure = WindowProcedure;

        if (fullscreen != 0)
        {
            restoreTransitions = DwmGetWindowAttribute(fullscreen, TransitionsForcedDisabled, out int previous, sizeof(int)) >= 0 &&
                previous == 0 && DwmSetWindowAttribute(fullscreen, TransitionsForcedDisabled, 1, sizeof(int)) >= 0;
            WatchTree(fullscreen);
        }
        WatchTree(host);
    }

    private void WatchTree(nint window)
    {
        if (window == 0)
            return;

        if (SetWindowSubclass(window, windowProcedure, PaintSubclassId, 0))
            windows.Add(window);
        for (nint child = GetWindow(window, 5); child != 0; child = GetWindow(child, 2))
            WatchTree(child);
    }

    private nint WindowProcedure(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        const uint eraseBackground = 0x0014;
        if (message == eraseBackground)
        {
            // Keep the last surface while ActiveX switches its output window.
            backgroundErasesSkipped++;
            return 1;
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        foreach (nint window in windows)
            RemoveWindowSubclass(window, windowProcedure, PaintSubclassId);
        if (restoreTransitions)
            DwmSetWindowAttribute(fullscreen, TransitionsForcedDisabled, 0, sizeof(int));
        FullScreenTransitionTrace.Write($"RDP exit background erases skipped {backgroundErasesSkipped}");
    }

    private delegate nint SubclassProc(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, in int value, int size);
}

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace mstscX.WinUI;

// Connection container only. RAIL owns the visible application windows; never
// show this host or give it a taskbar button/remote desktop surface.
internal sealed class NativeRemoteAppHost : IDisposable
{
    internal nint Handle { get; private set; }

    internal NativeRemoteAppHost(nint placementWindow)
    {
        GetWindowRect(placementWindow, out var placement);
        Handle = CreateWindowEx(0x80, "STATIC", null, 0x86000000,
            placement.Left, placement.Top, 1, 1, 0, 0, 0, 0);
        if (Handle == 0)
            throw new Win32Exception();
    }

    public void Dispose()
    {
        if (Handle == 0)
            return;
        DestroyWindow(Handle);
        Handle = 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string? name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
}

#if DEBUG
using System.Runtime.InteropServices;

namespace mstscX.WinUI;

// Geometry only: no window titles, credentials or remote application contents.
internal sealed class RemoteAppDisplayTrace
{
    private static readonly object LogLock = new();
    private readonly Dictionary<nint, string> windows = [];

    internal void Observe(nint session, nint window)
    {
        if (!GetWindowRect(window, out var rect))
            return;

        string state = $"window={window:X} monitor={MonitorFromWindow(window, 2):X} " +
            $"rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}) " +
            $"maximized={IsZoomed(window)} minimized={IsIconic(window)}";
        if (windows.TryGetValue(window, out string? previous) && previous == state)
            return;

        windows[window] = state;
        Write(session, state);
    }

    internal void Forget(nint window) => windows.Remove(window);

    internal static void Write(nint session, string message)
    {
        try
        {
            lock (LogLock)
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "remoteapp-display.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} PID={Environment.ProcessId} session={session:X} " +
                    message + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
}
#endif

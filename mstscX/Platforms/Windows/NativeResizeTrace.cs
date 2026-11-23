using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace mstscX.WinUI;

// Temporary diagnostics for the resize stall in an outer RDP session. Observe the
// existing handlers without changing messages, geometry, painting or mouse capture.
internal sealed class NativeResizeTrace : IDisposable
{
    private const nuint SubclassId = 0x5244535A;
    private const int MaximumWindows = 96;
    private readonly nint owner;
    private readonly uint threadId = GetCurrentThreadId();
    private readonly SubclassProc procedure;
    private readonly NativeWaitTrace waitTrace;
    private readonly Dictionary<nint, string> windows = [];
    private int generation = -1;
    private int detailBudget, ownerBudget, slowBudget, sequence, depth;

    internal NativeResizeTrace(nint owner)
    {
        this.owner = owner;
        procedure = OnMessage;
        waitTrace = new NativeWaitTrace(threadId);
        int visited = 0;
        WatchTree(owner, 0, ref visited, describe: false);
    }

    private void WatchTree(nint window, int level, ref int visited, bool describe)
    {
        if (window == 0 || level > 8 || visited++ >= MaximumWindows)
            return;

        uint windowThread = GetWindowThreadProcessId(window, out _);
        var className = new StringBuilder(256);
        GetClassName(window, className, className.Capacity);
        string label = $"HWND=0x{window:X} class={className} nativeThread={windowThread}";
        // The subclass helpers cannot be used across threads, even in the same process.
        if (windowThread == threadId && !windows.ContainsKey(window) &&
            SetWindowSubclass(window, procedure, SubclassId, 0))
            windows.Add(window, label);
        if (describe)
            FullScreenTransitionTrace.Write($"Resize window {label} observed={windows.ContainsKey(window)} " +
                $"style=0x{GetWindowLong(window, -16):X8} exStyle=0x{GetWindowLong(window, -20):X8}");

        for (nint child = GetWindow(window, 5); child != 0 && visited < MaximumWindows; child = GetWindow(child, 2))
            WatchTree(child, level + 1, ref visited, describe);
    }

    private nint OnMessage(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        const uint enterSizeMove = 0x0231;
        const uint destroy = 0x0082;
        if (message == destroy)
        {
            windows.Remove(window);
            RemoveWindowSubclass(window, procedure, SubclassId);
            return DefSubclassProc(window, message, wParam, lParam);
        }

        if (window == owner && message == enterSizeMove)
        {
            if (!FullScreenTransitionTrace.IsActive)
                FullScreenTransitionTrace.Begin("Native window move/resize", capturePaint: false);
            generation = -1;
        }
        if (!FullScreenTransitionTrace.IsActive)
            return DefSubclassProc(window, message, wParam, lParam);

        if (generation != FullScreenTransitionTrace.Generation)
        {
            generation = FullScreenTransitionTrace.Generation;
            detailBudget = 192;
            ownerBudget = 64;
            slowBudget = 64;
            FullScreenTransitionTrace.Write($"Native resize trace OS={Environment.OSVersion.VersionString} nativeThread={threadId}");
            int visited = 0;
            WatchTree(owner, 0, ref visited, describe: true);
        }

        string? name = message switch
        {
            0x0003 => "WM_MOVE",
            0x0005 => "WM_SIZE",
            0x000F => "WM_PAINT",
            0x0014 => "WM_ERASEBKGND",
            0x0024 => "WM_GETMINMAXINFO",
            0x0046 => "WM_WINDOWPOSCHANGING",
            0x0047 => "WM_WINDOWPOSCHANGED",
            0x007C => "WM_STYLECHANGING",
            0x007D => "WM_STYLECHANGED",
            0x0083 => "WM_NCCALCSIZE",
            0x0085 => "WM_NCPAINT",
            0x0088 => "WM_SYNCPAINT",
            0x0214 => "WM_SIZING",
            0x0231 => "WM_ENTERSIZEMOVE",
            0x0232 => "WM_EXITSIZEMOVE",
            _ => null
        };
        // Keep the owner resize/frame messages visible after chatty caption children
        // exhaust the general budget. These were the actual 8-12 second stalls.
        bool ownerResize = window == owner && message is 0x0005 or 0x0047 or 0x0085;
        bool detailed = name is not null && (detailBudget > 0 || (ownerResize && ownerBudget > 0));
        int call = ++sequence;
        int nesting = depth++;
        string label = windows.TryGetValue(window, out var known) ? known : $"HWND=0x{window:X}";
        if (detailed)
        {
            if (detailBudget > 0)
                detailBudget--;
            else
                ownerBudget--;
            FullScreenTransitionTrace.Write($"Native message begin call={call} depth={nesting} {label} {name}");
        }
        long started = Stopwatch.GetTimestamp();
        var waiting = waitTrace.Enter(call, message, label, generation, started);
        try
        {
            return DefSubclassProc(window, message, wParam, lParam);
        }
        finally
        {
            waitTrace.Leave(waiting);
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            depth--;
            if (detailed)
                FullScreenTransitionTrace.Write($"Native message end call={call} depth={nesting} {label} {name} elapsedMs={elapsed:F3}");
            else if (elapsed >= 50 && slowBudget-- > 0)
                FullScreenTransitionTrace.Write($"Native slow message call={call} depth={nesting} {label} " +
                    $"message=0x{message:X4} elapsedMs={elapsed:F3}");
        }
    }

    public void Dispose()
    {
        waitTrace.Dispose();
        foreach (nint window in windows.Keys)
            RemoveWindowSubclass(window, procedure, SubclassId);
        windows.Clear();
        GC.KeepAlive(procedure);
    }

    private delegate nint SubclassProc(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data);

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace mstscX.WinUI;

internal static class FullScreenTransitionTrace
{
    // Temporary Release diagnostics for the nested-RDP fullscreen exit hang.
    // Remove this instrumentation after the fix is verified on the affected machine.
    private static long traceUntil;
    internal static bool IsActive => Environment.TickCount64 < traceUntil;
    internal static int Generation { get; private set; }
    private const long MaximumLogBytes = 4 * 1024 * 1024;
    private static readonly Channel<string> PendingLines = Channel.CreateBounded<string>(new BoundedChannelOptions(512)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest,
        AllowSynchronousContinuations = false
    });
    private static readonly Task LogWriter = Task.Run(WriteLogAsync);
#if DEBUG
    private static bool observePaint;
    private const nuint PaintTraceSubclass = 0x524450;
    private static readonly SubclassProc WindowProcedure = TraceWindowMessage;
    private static readonly Dictionary<nint, string> WindowLabels = [];
    private static readonly List<(long Time, string Window, string Message, nint Detail)> WindowMessages = [];
    private static long windowTraceUntil;
    private static int windowTraceGeneration;
    private static FullScreenVisualTrace? visualTrace;
#endif

    internal static void Begin(string trigger = "Drag exit", bool capturePaint = true)
    {
        traceUntil = Environment.TickCount64 + 120_000;
        Generation++;
#if DEBUG
        observePaint = capturePaint;
#endif
        Write($"=== {trigger} PID={Environment.ProcessId} remoteSessionMetric={GetSystemMetrics(0x1000)} " +
            $"monitors={GetSystemMetrics(80)} ===");
    }

    internal static void EnsureStarted(string trigger)
    {
        if (Environment.TickCount64 >= traceUntil)
            Begin(trigger, capturePaint: false);
    }

    internal static void Write(string step)
    {
        long now = Environment.TickCount64;
        if (now >= traceUntil)
            return;

        Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {now} PID={Environment.ProcessId} " +
            $"T{Environment.CurrentManagedThreadId} {step}{Environment.NewLine}");
    }

    // Never wait for disk I/O on the UI/ActiveX thread. The writer continues even if
    // a native window operation blocks that thread, and also works in installed Release builds.
    internal static void Append(string lines) => PendingLines.Writer.TryWrite(lines);

    private static async Task WriteLogAsync()
    {
        while (await PendingLines.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            try
            {
#if DEBUG
                string directory = AppContext.BaseDirectory;
#else
                string directory = Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory, "Logs");
#endif
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "fullscreen-transition.log");
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumLogBytes)
                    File.Move(path, path + ".previous", overwrite: true);

                using var writer = new StreamWriter(path, append: true);
                while (PendingLines.Reader.TryRead(out string? lines))
                    await writer.WriteAsync(lines).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Drop an unwritable batch instead of retrying in a tight loop.
                while (PendingLines.Reader.TryRead(out _)) { }
                Debug.WriteLine(ex);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [Conditional("DEBUG")]
    internal static void ObserveWindows(nint owner, nint host, nint fullscreen)
    {
#if DEBUG
        long now = Environment.TickCount64;
        if (!observePaint || now >= traceUntil)
            return;

        FlushWindowMessages();
        windowTraceUntil = now + 60_000;
        windowTraceGeneration++;
        WatchTree(host, "RDP host", 3);
        if (fullscreen != owner)
            WatchTree(fullscreen, "RDP fullscreen", 3);
        WatchTree(owner, "WinUI owner", 2);
        visualTrace = new FullScreenVisualTrace(host, fullscreen);
        Write($"Observing paint and brightness through the end of the drag ({WindowLabels.Count} HWNDs)");
        ScheduleFlush(60_000);
#endif
    }

    [Conditional("DEBUG")]
    internal static void MarkWindowed()
    {
#if DEBUG
        visualTrace?.MarkWindowed();
#endif
    }

    [Conditional("DEBUG")]
    internal static void FinishDrag()
    {
#if DEBUG
        if (WindowLabels.Count == 0 && visualTrace is null)
            return;

        windowTraceUntil = Environment.TickCount64 + 750;
        Write("Observe drag ended; sampling final frames for 750 ms");
        ScheduleFlush(750);
#endif
    }

#if DEBUG
    private static void ScheduleFlush(int milliseconds)
    {
        int generation = windowTraceGeneration;
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _ = Task.Delay(milliseconds).ContinueWith(_ => dispatcher.TryEnqueue(() =>
        {
            if (generation == windowTraceGeneration)
                FlushWindowMessages();
        }), TaskScheduler.Default);
    }

    private static void WatchTree(nint window, string role, int depth)
    {
        if (window == 0)
            return;

        Watch(window, role);
        if (depth == 0)
            return;
        for (nint child = GetWindow(window, 5); child != 0; child = GetWindow(child, 2))
            WatchTree(child, $"{role}/{child:X}", depth - 1);
    }

    private static void Watch(nint window, string role)
    {
        if (window != 0 && !WindowLabels.ContainsKey(window) &&
            SetWindowSubclass(window, WindowProcedure, PaintTraceSubclass, 0))
        {
            WindowLabels.Add(window, role);
            WindowMessages.Add((Environment.TickCount64, role, "initial visibility", IsWindowVisible(window) ? 1 : 0));
        }
    }

    private static nint TraceWindowMessage(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        string? name = message switch
        {
            0x0002 => "WM_DESTROY",
            0x0005 => "WM_SIZE",
            0x000F => "WM_PAINT",
            0x0014 => "WM_ERASEBKGND",
            0x0018 => "WM_SHOWWINDOW",
            0x0047 => "WM_WINDOWPOSCHANGED",
            0x0085 => "WM_NCPAINT",
            0x0082 => "WM_NCDESTROY",
            _ => null
        };
        if (name is not null && Environment.TickCount64 <= windowTraceUntil && WindowMessages.Count < 3000 &&
            WindowLabels.TryGetValue(window, out var role))
        {
            nint detail = message is 0x0005 or 0x0018 ? wParam : 0;
            if (message == 0x0047 && lParam != 0)
                detail = Marshal.ReadInt32(lParam, 2 * nint.Size + 4 * sizeof(int));
            // Omit ordinary child-window repositioning. Keep paint and visibility changes.
            if (message != 0x0047 || (detail & 0x00C0) != 0 ||
                (role == "WinUI owner" && (detail & 0x0002) == 0))
                WindowMessages.Add((Environment.TickCount64, role, name, detail));
        }
        if (message == 0x0082)
        {
            WindowLabels.Remove(window);
            RemoveWindowSubclass(window, WindowProcedure, PaintTraceSubclass);
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private static void FlushWindowMessages()
    {
        visualTrace?.Stop();
        visualTrace = null;
        foreach (nint window in WindowLabels.Keys)
            RemoveWindowSubclass(window, WindowProcedure, PaintTraceSubclass);
        WindowLabels.Clear();
        if (WindowMessages.Count != 0)
        {
            var lines = new System.Text.StringBuilder();
            foreach (var entry in WindowMessages)
                lines.AppendLine($"Paint @{entry.Time} {entry.Window} {entry.Message} {entry.Detail}");
            Append(lines.ToString());
        }
        WindowMessages.Clear();
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

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);
#endif
}

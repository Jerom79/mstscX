using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace mstscX.WinUI;

// Temporary, read-only companion to NativeResizeTrace. WCT runs on a worker so
// diagnostics cannot hold up painting, the native move loop or an ActiveX callback.
internal sealed class NativeWaitTrace : IDisposable
{
    private readonly uint threadId;
    private readonly System.Threading.Timer timer;
    private Frame? active;
    private int disposed, sampling;
    private int generation = -1, remaining;
    private long lastSample;
    private static readonly Lazy<bool> ComCallbacks = new(RegisterComCallbacks);

    internal NativeWaitTrace(uint threadId)
    {
        this.threadId = threadId;
        timer = new System.Threading.Timer(Sample, null, 500, 500);
    }

    internal Frame Enter(int call, uint message, string label, int traceGeneration, long started)
    {
        var frame = new Frame(call, message, label, traceGeneration, started, Volatile.Read(ref active));
        Volatile.Write(ref active, frame);
        return frame;
    }

    internal void Leave(Frame frame) => Volatile.Write(ref active, Volatile.Read(ref disposed) == 0 ? frame.Parent : null);

    private void Sample(object? state)
    {
        if (Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref sampling, 1) != 0)
            return;
        try
        {
            var frame = Volatile.Read(ref active);
            // Caption dragging and SC_MOVE/SC_SIZE can legitimately remain inside
            // DefWindowProc for as long as the user holds the mouse button.
            if (frame is null || frame.Message is 0x00A1 or 0x0112 ||
                !FullScreenTransitionTrace.IsActive || Stopwatch.GetElapsedTime(frame.Started).TotalMilliseconds < 1000)
                return;
            if (generation != frame.Generation)
            {
                generation = frame.Generation;
                remaining = 24;
                lastSample = 0;
            }
            long now = Environment.TickCount64;
            if (remaining <= 0 || now - lastSample < 2000)
                return;
            remaining--;
            lastSample = now;
            double elapsed = Stopwatch.GetElapsedTime(frame.Started).TotalMilliseconds;
            string chain = ReadChain(threadId);
            if (Volatile.Read(ref disposed) == 0)
                FullScreenTransitionTrace.Write($"Native wait sample call={frame.Call} {frame.Label} " +
                    $"message=0x{frame.Message:X4} handlerElapsedMs={elapsed:F0} " +
                    $"stillInside={ReferenceEquals(frame, Volatile.Read(ref active))} {chain}");
        }
        catch (Exception ex) when (ex is ExternalException or DllNotFoundException or EntryPointNotFoundException)
        {
            FullScreenTransitionTrace.Write($"Native wait sampling unavailable error=0x{ex.HResult:X8}");
        }
        finally
        {
            Volatile.Write(ref sampling, 0);
        }
    }

    private static string ReadChain(uint thread)
    {
        bool comCallbacks = ComCallbacks.Value;
        nint session = OpenThreadWaitChainSession(0, 0);
        if (session == 0)
            return $"WCT openError={Marshal.GetLastPInvokeError()}";
        try
        {
            var nodes = new WaitNode[16]; // WCT_MAX_NODE_COUNT
            uint count = (uint)nodes.Length;
            // Observe this process; include the first external dependency, without
            // elevating privileges or following chains through other processes.
            bool success = GetThreadWaitChain(session, 0, 0, thread, ref count, nodes, out bool cycle);
            int error = success ? 0 : Marshal.GetLastPInvokeError();
            if (!success && error is not (234 or 565)) // MORE_DATA / TOO_MANY_THREADS still return a valid chain.
                return $"WCT error={error} comCallbacks={comCallbacks}";
            var result = new StringBuilder($"WCT cycle={cycle} error={error} comCallbacks={comCallbacks} nodes={count}");
            for (int i = 0; i < Math.Min(count, (uint)nodes.Length); i++)
            {
                var node = nodes[i];
                result.Append($" -> {node.Type}({node.Status}");
                if (node.Type == ObjectType.Thread)
                    result.Append($",pid={node.ProcessId},tid={node.ThreadId},wait={node.WaitTime},switches={node.ContextSwitches}");
                result.Append(')');
            }
            // No object names, window titles, memory dumps or session contents.
            return result.ToString();
        }
        finally
        {
            CloseThreadWaitChainSession(session);
        }
    }

    private static bool RegisterComCallbacks()
    {
        // WCT stores these function pointers. Keep the module loaded for process
        // lifetime, as required by RegisterWaitChainCOMCallback.
        nint module = LoadLibraryEx("ole32.dll", 0, 0x0800); // LOAD_LIBRARY_SEARCH_SYSTEM32
        if (module == 0)
            return false;
        nint call = GetProcAddress(module, "CoGetCallState");
        nint activation = GetProcAddress(module, "CoGetActivationState");
        if (call == 0 || activation == 0)
        {
            FreeLibrary(module);
            return false;
        }
        RegisterWaitChainCOMCallback(call, activation);
        return true;
    }

    public void Dispose()
    {
        Volatile.Write(ref disposed, 1);
        timer.Dispose(); // Do not wait for an in-flight worker on the UI thread.
        Volatile.Write(ref active, null);
    }

    internal sealed record Frame(int Call, uint Message, string Label, int Generation, long Started, Frame? Parent);

    private enum ObjectType
    {
        CriticalSection = 1, SendMessage, Mutex, Alpc, Com, ThreadWait, ProcessWait,
        Thread, ComActivation, Unknown, SocketIo, SmbIo
    }

    private enum ObjectStatus
    {
        NoAccess = 1, Running, Blocked, PidOnly, PidOnlyRpcss, Owned, NotOwned, Abandoned, Unknown, Error
    }

    // WAITCHAIN_NODE_INFO: two DWORD enums + an 8-byte-aligned 272-byte union
    // (WCHAR ObjectName[128], LARGE_INTEGER Timeout, BOOL Alertable). We only
    // read ThreadObject fields, and deliberately do not marshal object names.
    [StructLayout(LayoutKind.Explicit, Size = 280)]
    private struct WaitNode
    {
        [FieldOffset(0)] public ObjectType Type;
        [FieldOffset(4)] public ObjectStatus Status;
        [FieldOffset(8)] public uint ProcessId;
        [FieldOffset(12)] public uint ThreadId;
        [FieldOffset(16)] public uint WaitTime;
        [FieldOffset(20)] public uint ContextSwitches;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern nint OpenThreadWaitChainSession(uint flags, nint callback);
    [DllImport("advapi32.dll")]
    private static extern void CloseThreadWaitChainSession(nint session);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadWaitChain(nint session, nuint context, uint flags, uint thread,
        ref uint count, [Out] WaitNode[] nodes, [MarshalAs(UnmanagedType.Bool)] out bool cycle);
    [DllImport("advapi32.dll")]
    private static extern void RegisterWaitChainCOMCallback(nint callState, nint activationState);
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode)]
    private static extern nint LoadLibraryEx(string name, nint file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(nint module);
}

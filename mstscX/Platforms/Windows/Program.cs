using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace mstscX.WinUI;

internal static class Program
{
    // Stable Windows identity shared with releases before the mstscX rename.
    private const string InstanceKey = "MSTSC_MAUI.RdpClient";
#if DEBUG
    internal const string AppId = "MSTSC_MAUI.RdpClient.Debug";
#else
    internal const string AppId = InstanceKey;
#endif
    internal const string ConnectArgument = "--connect-profile";
    private static readonly ConcurrentQueue<(Guid? Profile, string? File)> Activations = new();
    private static bool processingActivations;
    private static Action? dispatchActivations;

    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        SetCurrentProcessExplicitAppUserModelID(AppId);

        var current = AppInstance.GetCurrent();
        current.Activated += (_, activation) =>
        {
            if (activation.Data is ILaunchActivatedEventArgs launch)
                QueueActivation(ParseArguments(launch.Arguments));
            else if (activation.Data is IFileActivatedEventArgs files)
                foreach (var file in files.Files) QueueActivation([file.Path]);
        };
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!instance.IsCurrent)
        {
            AllowSetForegroundWindow(instance.ProcessId);
            var activation = current.GetActivatedEventArgs();
            Task.Run(async () => await instance.RedirectActivationToAsync(activation)).GetAwaiter().GetResult();
            return;
        }

        // AppInstance scopes its registry to the app identity. Share the guard across build paths.
        using var instanceGuard = new Mutex(false, @"Local\MSTSC_MAUI.RdpClient", out bool firstInstance);
        if (!firstInstance)
            return;

        if (args.Length != 0)
            QueueActivation(args);

        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            new App();
        });
    }

    internal static void HandleActivations(DispatcherQueue dispatcher, Action<Guid?> activate, Func<string, Task> openFile)
    {
        Volatile.Write(ref dispatchActivations, () => dispatcher.TryEnqueue(async () =>
        {
            if (processingActivations) return;
            processingActivations = true;
            try
            {
                while (Activations.TryDequeue(out var request))
                    if (request.File is { } path) await openFile(path);
                    else activate(request.Profile);
            }
            finally { processingActivations = false; }
        }));
        dispatchActivations();
    }

    private static void QueueActivation(string[] args)
    {
        Guid? profileId = null;
        int index = Array.IndexOf(args, ConnectArgument);
        if (index >= 0 && index + 1 < args.Length && Guid.TryParseExact(args[index + 1], "D", out Guid id))
            profileId = id;
        string? file = args.FirstOrDefault(arg => Path.GetExtension(arg).Equals(".rdpx", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(arg).Equals(".rdp", StringComparison.OrdinalIgnoreCase));
        Activations.Enqueue((profileId, file));
        Volatile.Read(ref dispatchActivations)?.Invoke();
    }

    private static string[] ParseArguments(string commandLine)
    {
        nint pointer = CommandLineToArgvW("app.exe " + commandLine, out int count);
        if (pointer == 0) return [];
        try
        {
            return Enumerable.Range(1, count - 1)
                .Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * nint.Size)) ?? "").ToArray();
        }
        finally { LocalFree(pointer); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}

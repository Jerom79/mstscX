using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace mstscX.WinUI;

// Thread-local WH_KEYBOARD sees keys before the ActiveX accelerator consumes them.
internal sealed class MainWindowFullScreenKey : IDisposable
{
    private readonly HookProc procedure;
    private nint hook;
    private bool pressed;

    internal MainWindowFullScreenKey(nint owner, DispatcherQueue dispatcher, Func<nint, bool> allowed, Action toggle)
    {
        procedure = (code, key, data) =>
        {
            if (code == 0 && key == 0x7A) // HC_ACTION, VK_F11
            {
                long flags = data.ToInt64();
                bool released = (flags & 0x80000000L) != 0;
                // A key-up can go to another thread after focus changes.
                if (!released && (flags & 0x40000000L) == 0) pressed = false;
                if (pressed)
                {
                    if (released) pressed = false;
                    return 1;
                }
                if (!released && (flags & 0x40000000L) == 0 && IsWindowEnabled(owner) &&
                    (GetKeyState(0x10) & 0x8000) == 0 && (GetKeyState(0x11) & 0x8000) == 0 &&
                    (GetKeyState(0x12) & 0x8000) == 0 && allowed(GetForegroundWindow()))
                {
                    pressed = dispatcher.TryEnqueue(() =>
                    {
                        if (IsWindowEnabled(owner) && allowed(GetForegroundWindow())) toggle();
                    });
                    if (pressed) return 1;
                }
            }
            return CallNextHookEx(hook, code, key, data);
        };
        hook = SetWindowsHookEx(2, procedure, 0, GetCurrentThreadId());
    }

    public void Dispose()
    {
        if (hook != 0) UnhookWindowsHookEx(hook);
        hook = 0;
    }

    private delegate nint HookProc(int code, nint key, nint data);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW")] private static extern nint SetWindowsHookEx(int kind, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint key, nint data);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}

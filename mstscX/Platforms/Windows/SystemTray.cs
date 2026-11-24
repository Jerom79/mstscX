using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using mstscX.Models;
using mstscX.Services;

namespace mstscX.WinUI;

// A separate native owner lets the tray menu work while the main window is hidden.
internal sealed class SystemTray : IDisposable
{
    private const uint CallbackMessage = 0x8451;
    private readonly DispatcherQueue dispatcher;
    private readonly Func<IReadOnlyList<ServerProfile>> profiles;
    private readonly Action show;
    private readonly Action<Guid> connect;
    private readonly Action exit;
    private readonly SubclassProc callback;
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private nint window;
    private nint icon;
    private bool visible;
    private bool disposed;
    private bool menuOpen;

    internal SystemTray(DispatcherQueue dispatcher, Func<IReadOnlyList<ServerProfile>> profiles,
        Action show, Action<Guid> connect, Action exit)
    {
        this.dispatcher = dispatcher;
        this.profiles = profiles;
        this.show = show;
        this.connect = connect;
        this.exit = exit;
        callback = WindowProc;
        window = CreateWindowEx(0x80, "STATIC", "mstscX Tray", 0x80000000,
            -32000, -32000, 1, 1, 0, 0, GetModuleHandle(null), 0);
        if (window != 0 && !SetWindowSubclass(window, callback, 1, 0))
        {
            DestroyWindow(window);
            window = 0;
        }
        icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "appicon.ico"), 1,
            GetSystemMetrics(49), GetSystemMetrics(50), 0x10);
    }

    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 1,
        Flags = 1 | 2 | 4 | 0x80, Callback = CallbackMessage, Icon = icon,
        Tip = "mstscX", Info = "", InfoTitle = "", Version = 4
    };

    internal bool EnsureVisible()
    {
        if (disposed || window == 0 || icon == 0) return false;
        if (visible) return true;
        var data = Data();
        visible = Shell_NotifyIcon(0, ref data);
        if (visible) Shell_NotifyIcon(4, ref data);
        return visible;
    }

    private nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x2C && menuOpen) // WM_MEASUREITEM
        {
            var item = Marshal.PtrToStructure<MeasureItem>(lParam);
            if (item.Type == 1 && menuRows.TryGetValue(item.Id, out var row))
            {
                item.Width = (uint)menuWidth;
                item.Height = (uint)Px(row.Header ? 30 : 36);
                Marshal.StructureToPtr(item, lParam, false);
                return 1;
            }
        }
        if (message == 0x2B && menuOpen && DrawMenuItem(lParam)) return 1;
        if (message == taskbarCreated)
        {
            visible = false;
            EnsureVisible();
        }
        else if (message == CallbackMessage)
        {
            uint notification = (uint)((long)lParam & 0xffff);
            if (notification is 0x400 or 0x401 or 0x203)
                dispatcher.TryEnqueue(() => { if (!disposed) show(); });
            else if (notification is 0x7b or 0x205)
                ShowMenu();
            return 0;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private sealed record MenuRow(string Text, char Glyph, nint Icon = 0, bool Active = false, bool Header = false);
    private readonly Dictionary<uint, MenuRow> menuRows = [];
    private nint menuFont, glyphFont, menuBrush, hoverBrush;
    private uint menuText, mutedText;
    private double menuScale = 1;
    private int menuWidth;
    private int Px(double value) => (int)Math.Round(value * menuScale);

    private void ShowMenu()
    {
        if (disposed || menuOpen) return;
        menuOpen = true;
        var items = profiles().ToArray();
        nint menu = CreatePopupMenu();
        uint command = 0;
        try
        {
            GetCursorPos(out var point);
            // Put the hidden menu owner on the cursor's monitor before querying DPI.
            SetWindowPos(window, 0, point.X, point.Y, 1, 1, 0x14);
            menuScale = Math.Max(1, GetDpiForWindow(window) / 96d);
            bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
            menuText = dark ? 0xFFFFFFu : 0x202020u;
            mutedText = dark ? 0xDAC4B4u : 0x7E6552u;
            menuBrush = CreateSolidBrush(dark ? 0x251B14u : 0xFDFAF8u);
            hoverBrush = CreateSolidBrush(dark ? 0x493526u : 0xF4E5D5u);
            menuFont = CreateFont(-Px(14), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            glyphFont = CreateFont(-Px(18), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe MDL2 Assets");
            var info = new MenuInfo { Size = (uint)Marshal.SizeOf<MenuInfo>(), Mask = 2 | 0x10,
                Background = menuBrush, Style = 0x80000000 }; // MNS_NOCHECK: draw our own active mark.
            SetMenuInfo(menu, ref info);
            Add(1, new(L.T("Открыть mstscX"), '\uE8A7'));
            AddGroup(false, 3, "RDP");
            AddGroup(true, 4, "RemoteApp");
            AppendMenu(menu, 0x800, 0, null);
            Add(2, new(L.T("Закрыть приложение"), '\uE8BB'));
            menuWidth = Px(240);
            nint dc = GetDC(window);
            nint previous = SelectObject(dc, menuFont);
            foreach (var row in menuRows.Values)
            {
                var rect = new Rect();
                DrawText(dc, row.Text, row.Text.Length, ref rect, 0x420); // CALCRECT | NOPREFIX
                menuWidth = Math.Max(menuWidth, rect.Right + Px(88));
            }
            SelectObject(dc, previous);
            ReleaseDC(window, dc);
            menuWidth = Math.Min(menuWidth, Px(480));
            ShowWindow(window, 4);
            SetForegroundWindow(window);
            command = TrackPopupMenuEx(menu, 0x102, point.X, point.Y, window, 0);
            PostMessage(window, 0, 0, 0);

            void AddGroup(bool remoteApp, uint id, string title)
            {
                var group = items.Select((profile, index) => (profile, index))
                    .Where(entry => entry.profile.RemoteApp.Enabled == remoteApp).ToArray();
                if (group.Length == 0) return;
                AppendMenu(menu, 0x800, 0, null);
                nint destination = menu;
                if (group.Length > 4)
                {
                    destination = CreatePopupMenu();
                    SetMenuInfo(destination, ref info);
                    Add(id, new(title, remoteApp ? '\uE8A7' : '\uE7F4'));
                    var submenu = new MenuItemInfo
                    {
                        Size = (uint)Marshal.SizeOf<MenuItemInfo>(), Mask = 4, Submenu = destination
                    };
                    SetMenuItemInfo(menu, id, false, ref submenu);
                }
                foreach (var (profile, index) in group)
                {
                    string name = remoteApp ? "remote_app_card" : "server_card";
                    if (dark) name += "_dark";
                    nint image = remoteApp && profile.RemoteAppIconFile is { } path ? LoadProfileIcon(path) : 0;
                    if (image == 0)
                        image = LoadProfileIcon(Path.Combine(AppContext.BaseDirectory, "Images", $"{name}.scale-400.png"));
                    Add((uint)index + 100, new(profile.Name, remoteApp ? '\uE8A7' : '\uE7F4', image, profile.IsSessionActive), destination);
                }
            }
            void Add(uint id, MenuRow row, nint target = 0)
            {
                if (target == 0) target = menu;
                menuRows.Add(id, row);
                // Keep the actual label in the native menu for accessibility and keyboard navigation.
                AppendMenu(target, row.Header ? 3u : 0u, id, row.Text.Replace("&", "&&"));
                var item = new MenuItemInfo { Size = (uint)Marshal.SizeOf<MenuItemInfo>(), Mask = 0x100, Type = 0x100 };
                SetMenuItemInfo(target, id, false, ref item);
            }
        }
        finally
        {
            ShowWindow(window, 0);
            DestroyMenu(menu);
            foreach (var row in menuRows.Values) if (row.Icon != 0) DestroyIcon(row.Icon);
            menuRows.Clear();
            foreach (nint resource in new[] { menuFont, glyphFont, menuBrush, hoverBrush })
                if (resource != 0) DeleteObject(resource);
            menuFont = glyphFont = menuBrush = hoverBrush = 0;
            menuOpen = false;
        }
        if (command == 0) return;
        dispatcher.TryEnqueue(() =>
        {
            if (disposed) return;
            if (command == 1) show();
            else if (command == 2) exit();
            else if (command >= 100 && command - 100 < items.Length) connect(items[command - 100].Id);
        });
    }

    private nint LoadProfileIcon(string path)
    {
        try
        {
            byte[] png = File.ReadAllBytes(path);
            return CreateIconFromResourceEx(png, (uint)png.Length, true, 0x30000, Px(22), Px(22), 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Tray icon: {ex.Message}");
            return 0;
        }
    }

    private bool DrawMenuItem(nint pointer)
    {
        var item = Marshal.PtrToStructure<DrawItem>(pointer);
        if (item.Type != 1 || !menuRows.TryGetValue(item.Id, out var row)) return false;
        FillRect(item.Dc, ref item.Rect, (item.State & 1) != 0 && !row.Header ? hoverBrush : menuBrush);
        SetBkMode(item.Dc, 1);
        SetTextColor(item.Dc, row.Header ? mutedText : menuText);
        nint previous = SelectObject(item.Dc, glyphFont);
        var iconRect = new Rect { Left = item.Rect.Left + Px(12), Top = item.Rect.Top,
            Right = item.Rect.Left + Px(36), Bottom = item.Rect.Bottom };
        if (row.Icon != 0)
            DrawIconEx(item.Dc, iconRect.Left, item.Rect.Top + (item.Rect.Bottom - item.Rect.Top - Px(22)) / 2,
                row.Icon, Px(22), Px(22), 0, 0, 3);
        else DrawText(item.Dc, row.Glyph.ToString(), 1, ref iconRect, 0x25);
        if (row.Active)
        {
            var check = item.Rect;
            check.Left = check.Right - Px(32);
            DrawText(item.Dc, "\uE73E", 1, ref check, 0x25);
        }
        SelectObject(item.Dc, menuFont);
        var text = item.Rect;
        text.Left += Px(46);
        text.Right -= Px(36);
        DrawText(item.Dc, row.Text, row.Text.Length, ref text, 0x8824); // ellipsis, no mnemonic interpretation
        SelectObject(item.Dc, previous);
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (visible) { var data = Data(); Shell_NotifyIcon(2, ref data); }
        if (window != 0) { RemoveWindowSubclass(window, callback, 1); DestroyWindow(window); window = 0; }
        if (icon != 0) { DestroyIcon(icon); icon = 0; }
        visible = false;
    }

    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [StructLayout(LayoutKind.Sequential)] private struct MeasureItem
    {
        public uint Type, ControlId, Id, Width, Height; public nuint Data;
    }
    [StructLayout(LayoutKind.Sequential)] private struct DrawItem
    {
        public uint Type, ControlId, Id, Action, State; public nint Window, Dc; public Rect Rect; public nuint Data;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MenuInfo
    {
        public uint Size, Mask, Style, MaxHeight; public nint Background; public uint HelpId; public nuint Data;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MenuItemInfo
    {
        public uint Size, Mask, Type, State, Id; public nint Submenu, Checked, Unchecked;
        public nuint Data; public nint Text; public uint TextLength; public nint Bitmap;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string file, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetMenuItemInfo(nint menu, uint item, bool byPosition, ref MenuItemInfo info);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateFont(int h, int w, int e, int o, int weight, uint italic, uint underline, uint strike, uint charset, uint output, uint clip, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawText(nint dc, string text, int length, ref Rect rect, uint format);
    [DllImport("user32.dll")] private static extern bool SetMenuInfo(nint menu, ref MenuInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref Rect rect, nint brush);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
    [DllImport("user32.dll")] private static extern nint CreateIconFromResourceEx(byte[] bits, uint size, bool icon, uint version, int width, int height, uint flags);
}

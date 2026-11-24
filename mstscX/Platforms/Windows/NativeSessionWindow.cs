using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using mstscX.Models;
using mstscX.Services;
using Windows.Graphics;

namespace mstscX.WinUI;

// An independent Win32 top-level HWND. It shares the application's STA message
// loop, but has no MAUI page, XAML surface, AppWindow or WinUI presenter.
internal sealed class NativeSessionWindow
{
    private const string WindowClass = "mstscX.NativeSessionWindow";
    private const uint WindowStyle = 0x06CF0000; // OVERLAPPEDWINDOW | CLIPCHILDREN | CLIPSIBLINGS
    private const int ReconnectButtonId = 101;
    private static readonly WindowProc Procedure = DispatchMessage;
    private static readonly Dictionary<nint, NativeSessionWindow> Windows = [];
    private static ushort classAtom;
    // Registered class outlives individual sessions. Keep its icons alive for
    // Explorer's class-icon fallback, including when the window has no frame.
    private static nint classIcon, classSmallIcon;
    private readonly DispatcherQueue dispatcher;
    private readonly DispatcherQueueTimer timer;
    private readonly string address;
    private readonly ServerProfile profile;
    private readonly Func<Task<string?>> readPassword;
    private SessionWindowLayout layout;
    private RdpSession? session;
    private FullScreenSessionBar? panel;
    private BorderlessSessionWindow? fullScreen;
    private nint window, notice, noticeImage, noticeIcon, font, background;
    private nint errorImage, errorIcon, reconnectButton;
    private bool connectionFailed, reconnectPending, reconnectFullScreen, connectionFullScreen;
    private readonly List<nint> ownedIcons = [];
    private string serverName;
    private string status = global::mstscX.Services.L.T("Подключение…");
    private bool connected, wasConnected, closing, changingLayout, switchingMonitors;
    private bool nativeFullScreen, panelDrag, pendingMaximize, restoreToNormal;
    private bool movingWindow;
    private bool darkTheme;
    private uint backgroundColor, textColor;
    internal bool HasEnded { get; private set; }
    private bool IsFullScreen => fullScreen is not null || nativeFullScreen;
    private string Title => $"{serverName} - [{RdpEndpoint.FormatForDisplay(address)}]";

    internal event Action? Connected;
    internal event Action<string?>? ReconnectStatusChanged;
    internal event Action<string?>? Ended;
    internal event Action? Closed;

    internal NativeSessionWindow(nint placementOwner, DispatcherQueue dispatcher, string address,
        ServerProfile profile, SessionWindowLayout layout, Func<Task<string?>> readPassword)
    {
        this.dispatcher = dispatcher;
        this.address = address;
        this.profile = profile;
        this.layout = layout;
        this.readPassword = readPassword;
        serverName = profile.Name;
        timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        timer.Tick += OnTimer;
        try
        {
            EnsureWindowClass();
            var monitor = GetMonitor(placementOwner);
            uint dpi = GetDpiForWindow(placementOwner);
            double scale = Math.Max(96, dpi) / 96d;
            int width = Math.Clamp((int)Math.Round(layout.Width * scale), 400, Math.Max(400, monitor.Work.Width));
            int height = Math.Clamp((int)Math.Round(layout.Height * scale), 300, Math.Max(300, monitor.Work.Height));
            window = CreateWindowEx(0x00040000, WindowClass, serverName, WindowStyle,
                monitor.Work.Left + (monitor.Work.Width - width) / 2,
                monitor.Work.Top + (monitor.Work.Height - height) / 2,
                width, height, 0, 0, GetModuleHandle(null), 0);
            if (window == 0)
                throw new Win32Exception();
            Windows.Add(window, this);
            for (int size = 0; size <= 1; size++)
            {
                // Own both icon handles; WinUI's borrowed WM_GETICON handle may
                // be replaced when its title bar or DPI changes.
                nint icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "appicon.ico"), 1,
                    Scale(size == 0 ? 16 : 32), Scale(size == 0 ? 16 : 32), 0x0010);
                if (icon == 0)
                {
                    nint inherited = SendMessage(placementOwner, 0x007F, size, 0);
                    if (inherited != 0) icon = CopyIcon(inherited);
                }
                if (icon != 0)
                {
                    ownedIcons.Add(icon);
                    SendMessage(window, 0x0080, size, icon);
                }
            }
            notice = CreateChild("EDIT", "", 0x00200845, 0); // centered, multiline, read-only, vertical scrolling for long errors
            noticeImage = CreateChild("STATIC", "", 0x0043, 0); // SS_ICON | SS_REALSIZECONTROL
            errorImage = CreateChild("STATIC", "", 0x0803, 0);
            reconnectButton = CreateChild("BUTTON", L.T("Переподключить"), 0x0001000B, ReconnectButtonId); // tab stop, owner draw
            UpdateThemeAndFont();
            TaskbarServers.SetSessionWindowIdentity(window, profile, useApplicationIconFile: true);
            UpdateStatus();
            UpdateLayout();
        }
        catch
        {
            CloseConnection();
            throw;
        }
    }

    internal void ShowAndConnect(string? password)
    {
        if (closing)
            return;
        ShowWindow(window, 5);
        timer.Start();
        bool startFullScreen = layout.FullScreen;
        // The initial saved mode is applied after login; ordinary size bookkeeping
        // must not overwrite it while the window is being created.
        layout = layout with { FullScreen = false };
        StartConnection(password, startFullScreen && profile.Display.UseMultimon, startFullScreen);
    }

    private void StartConnection(string? password, bool multipleMonitors, bool enterFullScreen)
    {
        if (closing)
            return;
        HasEnded = false;
        connected = false;
        wasConnected = false;
        connectionFullScreen = enterFullScreen;
        connectionFailed = false;
        status = global::mstscX.Services.L.T("Подключение…");
        UpdateStatus();
        try
        {
            FullScreenTransitionTrace.EnsureStarted("Win32 session connection");
            FullScreenTransitionTrace.Write($"Win32 session host HWND=0x{window:X} xamlComposition=false");
            var current = new RdpSession(window, GetViewportBounds(), useXamlComposition: false);
            session = current;
            current.SetViewportVisible(false);
            current.Connected += () => dispatcher.TryEnqueue(() =>
            {
                if (closing || session != current)
                    return;
                connected = wasConnected = true;
                status = global::mstscX.Services.L.T("Подключено");
                UpdateStatus();
                UpdateLayout();
                current.SetViewportVisible(true);
                current.ShowSession();
                ReconnectStatusChanged?.Invoke(null);
                Connected?.Invoke();
                if (enterFullScreen)
                    EnterFullScreen();
                else
                    current.ActivateViewport();
            });
            current.Disconnected += error => dispatcher.TryEnqueue(() =>
            {
                if (!closing && session == current)
                    EndConnection(error);
            });
            current.ReconnectStatusChanged += message => dispatcher.TryEnqueue(() =>
            {
                if (closing || session != current)
                    return;
                status = message ?? global::mstscX.Services.L.T("Подключено");
                UpdateStatus();
                ReconnectStatusChanged?.Invoke(message);
            });
            current.HostedFullScreenRequested += entered => dispatcher.TryEnqueue(() =>
            {
                if (closing || switchingMonitors || session != current || !connected) return;
                if (entered) EnterFullScreen();
                else ReturnToWindow();
            });
            current.NativeFullScreenChanged += entered => dispatcher.TryEnqueue(() =>
            {
                if (closing || switchingMonitors || session != current || !connected)
                    return;
                if (entered && !IsFullScreen)
                    AttachNativeFullScreen(current, attachOnly: true);
                else if (!entered && nativeFullScreen)
                    ReturnToWindow();
            });
            current.Connect(address, profile, password, singleMonitor: !multipleMonitors);
        }
        catch (Exception ex) when (ex is ExternalException or Win32Exception or ArgumentException or
            DllNotFoundException or EntryPointNotFoundException)
        {
            EndConnection(global::mstscX.Services.L.F("Не удалось запустить RDP (0x{0:X8}). {1}", ex.HResult, ex.Message));
        }
    }

    private void EnterFullScreen()
    {
        if (closing || !connected || switchingMonitors || IsFullScreen || IsIconic(window) || session is not { } current)
            return;
        FullScreenTransitionTrace.EnsureStarted("Win32 enter fullscreen");
        if (profile.Display.UseMultimon && RdpSession.HasMultipleMonitors && !current.UsesMultipleMonitors)
        {
            _ = ChangeMonitorModeAsync(current, true);
            return;
        }
        if (current.UsesMultipleMonitors && AttachNativeFullScreen(current))
            return;
        RememberSize();
        restoreToNormal = IsZoomed(window);
        changingLayout = true;
        SetTransitionsDisabled(true);
        try
        {
            fullScreen = BorderlessSessionWindow.TryEnter(window);
            if (fullScreen is null)
                return;
            layout = layout with { FullScreen = true };
            SetCornerPreference(1);
            panel = new FullScreenSessionBar(window, dispatcher, profile.Id, Title, serverName,
                ReturnToWindow, RequestCloseConnection, Minimize);
        }
        finally
        {
            changingLayout = false;
            UpdateLayout();
            SetTransitionsDisabled(false);
        }
        current.SetHostedFullScreen(true);
        current.ActivateViewport();
        FullScreenTransitionTrace.Write("Win32 enter fullscreen end");
    }

    private bool AttachNativeFullScreen(RdpSession current, bool attachOnly = false)
    {
        if (!current.TryEnterNativeFullScreen(profile.Id, () => (Title, serverName),
            ReturnToWindow, RequestCloseConnection, Minimize, attachOnly))
            return false;
        nativeFullScreen = true;
        layout = layout with { FullScreen = true };
        return true;
    }

    private void ReturnToWindow(PointInt32? drop = null)
    {
        if (closing || switchingMonitors || !IsFullScreen)
            return;
        FullScreenTransitionTrace.EnsureStarted("Win32 return to window");
        FullScreenTransitionTrace.Write($"Win32 return begin dragging={drop.HasValue} multimon={session?.UsesMultipleMonitors}");
        if (session is { UsesMultipleMonitors: true } multi)
        {
            _ = ChangeMonitorModeAsync(multi, false, drop);
            return;
        }
        var current = session;
        current?.TraceFullScreenExitWindows(hosted: fullScreen is not null);
        if (drop.HasValue)
            current?.DeferWindowedDisplayUpdate(true);
        RestoreWindow(drop.HasValue);
        if (drop is { } position)
            ContinueDrag(position);
        else
            current?.ActivateViewport();
        FullScreenTransitionTrace.Write("Win32 return end");
    }

    private void RestoreWindow(bool dragging)
    {
        session?.SetHostedFullScreen(false);
        changingLayout = true;
        SetTransitionsDisabled(true);
        try
        {
            if (nativeFullScreen)
                session?.TryLeaveNativeFullScreen();
            nativeFullScreen = false;
            panel?.Dispose();
            panel = null;
            fullScreen?.Restore(dragging || restoreToNormal);
            fullScreen = null;
            layout = layout with { FullScreen = false };
            SetCornerPreference(0);
        }
        finally
        {
            changingLayout = false;
            UpdateLayout();
            SetTransitionsDisabled(false);
        }
        FullScreenTransitionTrace.MarkWindowed();
    }

    private void ContinueDrag(PointInt32 position)
    {
        var current = session;
        panelDrag = true;
        NativeSessionWindowDrag.PositionUnderPointer(window, position);
        // Leave the panel's callback before entering the modal caption move loop.
        if (!dispatcher.TryEnqueue(() =>
        {
            if (closing || IsFullScreen)
            {
                Finish();
                return;
            }
            NativeSessionWindowDrag.Continue(window, Finish, EnterFullScreen);
        }))
            Finish();

        void Finish()
        {
            panelDrag = false;
            if (session == current)
                current?.DeferWindowedDisplayUpdate(false);
            RememberSize();
        }
    }

    private async Task ChangeMonitorModeAsync(RdpSession current, bool multipleMonitors, PointInt32? drop = null)
    {
        if (switchingMonitors || closing || session != current)
            return;
        switchingMonitors = true;
        try
        {
            string? password = null;
            try { password = await readPassword(); }
            catch (Exception ex) when (ex is FormatException or UnauthorizedAccessException or COMException or ArgumentException)
            { System.Diagnostics.Debug.WriteLine(ex); }
            if (closing || session != current)
                return;
            status = global::mstscX.Services.L.T("Переподключение для смены режима экрана…");
            ReconnectStatusChanged?.Invoke(status);
            connected = false;
            session = null;
            // As in SessionPage, release a multimon desktop rather than reparent it.
            current.Dispose(disconnect: false);
            nativeFullScreen = false;
            RestoreWindow(drop.HasValue);
            UpdateStatus();
            await Task.Yield();
            if (closing)
                return;
            switchingMonitors = false;
            if (drop is { } position)
                ContinueDrag(position);
            StartConnection(password, multipleMonitors, multipleMonitors);
        }
        catch (ExternalException ex)
        {
            EndConnection(global::mstscX.Services.L.F("Не удалось сменить режим экрана (0x{0:X8}). {1}", ex.HResult, ex.Message));
        }
        finally
        {
            switchingMonitors = false;
        }
    }

    private void Minimize()
    {
        if (closing)
            return;
        if (nativeFullScreen && session?.TryLeaveNativeFullScreen() == true)
        {
            nativeFullScreen = false;
            // Reattach the multi-monitor fullscreen surface when restored.
            pendingNativeRestore = true;
        }
        ShowWindow(window, 6);
        panel?.Update();
    }

    private bool pendingNativeRestore;

    internal void Activate()
    {
        if (closing)
            return;
        if (session?.TryActivateFullScreenWindow() != true)
            RdpSession.TryActivateWindow(window);
        if (!IsFullScreen)
            session?.ActivateViewport();
    }

    internal void UpdateServerName(string name, ServerProfile? identity = null)
    {
        serverName = name;
        SetWindowText(window, name);
        UpdateStatus();
        panel?.UpdateTitle(Title, name);
        session?.UpdateNativeFullScreenTitle(Title, name);
        TaskbarServers.SetSessionWindowIdentity(window, identity ?? profile, useApplicationIconFile: true);
    }

    private void EndConnection(string? error)
    {
        if (closing || HasEnded)
            return;
        if (wasConnected)
            SessionWindowSettings.Save(profile, layout);
        reconnectFullScreen = IsFullScreen || (!wasConnected && connectionFullScreen);
        connectionFailed = error is not null;
        HasEnded = true;
        connected = false;
        pendingNativeRestore = false;
        var previous = session;
        session = null;
        previous?.Dispose(disconnect: false);
        nativeFullScreen = false;
        RestoreWindow(false);
        status = error ?? global::mstscX.Services.L.T("Подключение завершено.");
        UpdateStatus();
        Ended?.Invoke(error);
    }

    private async void Reconnect()
    {
        if (closing || !HasEnded || !connectionFailed || reconnectPending) return;
        reconnectPending = true;
        EnableWindow(reconnectButton, false);
        try
        {
            string? password = null;
            try { password = await readPassword(); }
            catch (Exception ex) when (ex is FormatException or UnauthorizedAccessException or COMException or ArgumentException)
            { System.Diagnostics.Debug.WriteLine(ex); }
            if (closing) return;
            // With no saved password, the regular RDP credential dialog prompts the user.
            StartConnection(password, reconnectFullScreen && profile.Display.UseMultimon, reconnectFullScreen);
            if (!HasEnded) ReconnectStatusChanged?.Invoke(status);
        }
        finally
        {
            reconnectPending = false;
            if (!closing) EnableWindow(reconnectButton, true);
        }
    }

    internal async void RequestCloseConnection()
    {
        if (!closing && (HasEnded || await ConnectionCloseConfirmation.ConfirmAsync(window, serverName)))
            CloseConnection();
    }

    internal void CloseConnection()
    {
        if (closing)
            return;
        closing = true;
        if (wasConnected && !HasEnded)
        {
            RememberSize();
            SessionWindowSettings.Save(profile, layout);
        }
        HasEnded = true;
        timer.Stop();
        timer.Tick -= OnTimer;
        panel?.Dispose();
        panel = null;
        var previous = session;
        session = null;
        try { previous?.Dispose(disconnect: false); }
        finally
        {
            if (window != 0)
            {
                TaskbarServers.ClearSessionWindowIdentity(window);
                DestroyWindow(window);
                Windows.Remove(window);
                window = 0;
            }
            if (font != 0) DeleteObject(font);
            if (background != 0) DeleteObject(background);
            if (noticeIcon != 0) DestroyIcon(noticeIcon);
            if (errorIcon != 0) DestroyIcon(errorIcon);
            errorIcon = 0;
            noticeIcon = 0;
            foreach (nint icon in ownedIcons) DestroyIcon(icon);
            ownedIcons.Clear();
            font = background = 0;
            Closed?.Invoke();
            Closed = null;
            Ended = null;
            Connected = null;
            ReconnectStatusChanged = null;
        }
    }

    private void OnTimer(DispatcherQueueTimer sender, object args)
    {
        if (darkTheme != (Application.Current?.RequestedTheme == AppTheme.Dark))
            UpdateThemeAndFont();
        panel?.Update();
        if (pendingNativeRestore && !IsIconic(window) && session is { } current)
        {
            pendingNativeRestore = false;
            AttachNativeFullScreen(current);
        }
        if (pendingMaximize && !FullScreenPanelDrag.IsPrimaryMouseButtonDown)
        {
            pendingMaximize = false;
            EnterFullScreen();
        }
    }

    private void UpdateStatus()
    {
        SetWindowText(notice, global::mstscX.Services.L.F("Адрес: {0}\r\n\r\n{1}", RdpEndpoint.FormatForDisplay(address), status));
        ShowWindow(notice, connected ? 0 : 4);
        ShowWindow(noticeImage, connected || noticeIcon == 0 ? 0 : 4);
        ShowWindow(errorImage, connectionFailed && errorIcon != 0 ? 4 : 0);
        ShowWindow(reconnectButton, HasEnded && connectionFailed ? 4 : 0);
        UpdateLayout();
    }

    private (int X, int Y, int Width, int Height) GetViewportBounds()
    {
        GetClientRect(window, out var area);
        const int top = 0;
        return (0, top, Math.Max(1, area.Width), Math.Max(1, area.Height - top));
    }

    private void UpdateLayout()
    {
        if (changingLayout || window == 0 || notice == 0 || IsIconic(window))
            return;
        changingLayout = true;
        try
        {
            var area = GetViewportBounds();
            int imageSize = Scale(area.Height < Scale(450) ? 48 : 96);
            int errorSize = connectionFailed ? Scale(56) : 0;
            int buttonHeight = connectionFailed ? Scale(44) : 0;
            int gap = Scale(12);
            int textWidth = Math.Max(1, Math.Min(Scale(640), area.Width - Scale(48)));
            var textRect = new Rect { Right = textWidth };
            nint dc = GetDC(notice);
            int textHeight;
            try
            {
                nint oldFont = SelectObject(dc, font);
                DrawText(dc, L.F("Адрес: {0}\r\n\r\n{1}", RdpEndpoint.FormatForDisplay(address), status), -1, ref textRect, 0xC50); // CALCRECT | WORDBREAK | NOPREFIX
                SelectObject(dc, oldFont);
                textHeight = Math.Max(Scale(60), textRect.Height + Scale(8));
            }
            finally { ReleaseDC(notice, dc); }
            int fixedHeight = imageSize + errorSize + buttonHeight + gap * (connectionFailed ? 3 : 1);
            textHeight = Math.Min(textHeight, Math.Max(Scale(40), area.Height - fixedHeight - Scale(24)));
            int top = Math.Max(Scale(12), (area.Height - fixedHeight - textHeight) / 2);
            Place(noticeImage, (area.Width - imageSize) / 2, top, imageSize, imageSize);
            int textTop = top + imageSize + gap;
            if (connectionFailed)
            {
                Place(errorImage, (area.Width - errorSize) / 2, textTop, errorSize, errorSize);
                textTop += errorSize + gap;
            }
            Place(notice, (area.Width - textWidth) / 2, textTop, textWidth, textHeight);
            Place(reconnectButton, (area.Width - Scale(180)) / 2, textTop + textHeight + gap, Scale(180), Scale(44));
            session?.Resize(area);
        }
        finally { changingLayout = false; }
    }

    private void RememberSize()
    {
        if (window == 0 || IsFullScreen || pendingNativeRestore || changingLayout || IsZoomed(window) || IsIconic(window) ||
            !GetWindowRect(window, out var rect))
            return;
        double scale = Math.Max(96, GetDpiForWindow(window)) / 96d;
        layout = layout with { Width = rect.Width / scale, Height = rect.Height / scale };
    }

    private int Scale(int value) => (int)Math.Round(value * Math.Max(96, GetDpiForWindow(window)) / 96d);

    private void UpdateThemeAndFont()
    {
        bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        darkTheme = dark;
        UpdateNoticeIcon();
        backgroundColor = dark ? 0x00251B14u : 0x00FDFAF8u;
        textColor = dark ? 0x00FFFFFFu : 0x00202020u;
        nint oldBrush = background;
        background = CreateSolidBrush(backgroundColor);
        if (oldBrush != 0) DeleteObject(oldBrush);
        nint oldFont = font;
        font = CreateFont(-Scale(13), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        SendMessage(notice, 0x0030, font, 1);
        SendMessage(reconnectButton, 0x0030, font, 1);
        if (oldFont != 0) DeleteObject(oldFont);
        int darkMode = dark ? 1 : 0;
        DwmSetWindowAttribute(window, 20, in darkMode, sizeof(int));
        InvalidateRect(window, 0, true);
    }

    private void UpdateNoticeIcon()
    {
        string name = darkTheme ? "server_card_dark" : "server_card";
        string path = Path.Combine(AppContext.BaseDirectory, "Images", $"{name}.scale-400.png");
        nint icon = 0;
        try
        {
            // PNG-backed icon resources preserve transparency without a XAML host.
            byte[] png = File.ReadAllBytes(path);
            icon = CreateIconFromResourceEx(png, (uint)png.Length, true, 0x00030000, Scale(96), Scale(96), 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Session notice icon: {ex.Message}");
        }
        if (icon == 0)
            icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "appicon.ico"), 1, Scale(96), Scale(96), 0x0010);
        SendMessage(noticeImage, 0x0170, icon, 0); // STM_SETICON
        if (noticeIcon != 0) DestroyIcon(noticeIcon);
        noticeIcon = icon;
        ShowWindow(noticeImage, connected || icon == 0 ? 0 : 4);
        nint nextErrorIcon = 0;
        try
        {
            byte[] png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Images", "rdp_error.scale-400.png"));
            nextErrorIcon = CreateIconFromResourceEx(png, (uint)png.Length, true, 0x00030000, Scale(56), Scale(56), 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Debug.WriteLine($"Session error icon: {ex.Message}"); }
        SendMessage(errorImage, 0x0170, nextErrorIcon, 0);
        if (errorIcon != 0) DestroyIcon(errorIcon);
        errorIcon = nextErrorIcon;
        ShowWindow(errorImage, connectionFailed && errorIcon != 0 ? 4 : 0);
    }

    private void SetTransitionsDisabled(bool disabled)
    {
        int value = disabled ? 1 : 0;
        DwmSetWindowAttribute(window, 3, in value, sizeof(int));
    }

    private void SetCornerPreference(int value) => DwmSetWindowAttribute(window, 33, in value, sizeof(int));

    private nint CreateChild(string kind, string text, uint style, int id)
    {
        nint child = CreateWindowEx(0, kind, text, 0x50000000 | style, 0, 0, 1, 1, window, id, 0, 0);
        return child != 0 ? child : throw new Win32Exception();
    }

    private static void Place(nint child, int x, int y, int width, int height) =>
        SetWindowPos(child, 0, x, y, width, height, 0x0014);

    private nint OnMessage(uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case 0x0111 when lParam == reconnectButton && (wParam.ToInt64() & 0xffff) == ReconnectButtonId:
                dispatcher.TryEnqueue(Reconnect);
                return 0;
            case 0x002B: // WM_DRAWITEM
                var item = Marshal.PtrToStructure<DrawItem>(lParam);
                if (item.Window == reconnectButton)
                {
                    FillRect(item.Dc, ref item.Bounds, background);
                    bool disabled = (item.State & 4) != 0;
                    uint color = disabled ? 0x00606060u : (item.State & 1) != 0 ? 0x00D09868u : 0x00F8CCA0u;
                    nint brush = CreateSolidBrush(color);
                    nint region = CreateRoundRectRgn(0, 0, item.Bounds.Width, item.Bounds.Height, Scale(16), Scale(16));
                    FillRgn(item.Dc, region, brush);
                    DeleteObject(region);
                    DeleteObject(brush);
                    SetBkMode(item.Dc, 1);
                    SetTextColor(item.Dc, disabled ? 0x00CCCCCCu : 0x00202020u);
                    nint oldFont = SelectObject(item.Dc, font);
                    DrawText(item.Dc, L.T("Переподключить"), -1, ref item.Bounds, 0x825);
                    SelectObject(item.Dc, oldFont);
                    if ((item.State & 0x10) != 0) DrawFocusRect(item.Dc, ref item.Bounds);
                    return 1;
                }
                break;
            case 0x0010: // WM_CLOSE: dispose ActiveX after leaving its native callbacks.
                dispatcher.TryEnqueue(RequestCloseConnection);
                return 0;
            case 0x0112: // SC_MAXIMIZE includes caption double click. Wait for mouse release.
                if ((wParam.ToInt64() & 0xfff0) == 0xf030 && connected)
                {
                    pendingMaximize = true;
                    return 0;
                }
                break;
            case 0x0005: // WM_SIZE
                if (wParam != 1 && !changingLayout)
                {
                    UpdateLayout();
                    RememberSize();
                    if (wParam == 2 && connected && !IsFullScreen) pendingMaximize = true;
                }
                break;
            case 0x0024: // WM_GETMINMAXINFO
                if (lParam != 0)
                {
                    var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                    limits.MinTrack = new PointInt32(Scale(400), Scale(300));
                    Marshal.StructureToPtr(limits, lParam, false);
                }
                return 0;
            case 0x0216: // WM_MOVING
                movingWindow = true;
                break;
            case 0x0231:
                movingWindow = false;
                break;
            case 0x0232: // Snap only after the user releases the mouse.
                if (movingWindow && !panelDrag && !FullScreenPanelDrag.IsPrimaryMouseButtonDown && NativeSessionWindowDrag.IsPointerAtTop())
                    dispatcher.TryEnqueue(EnterFullScreen);
                movingWindow = false;
                RememberSize();
                break;
            case 0x02E0: // WM_DPICHANGED
                UpdateThemeAndFont();
                if (fullScreen is not null) FitMonitor();
                else if (lParam != 0)
                {
                    var bounds = Marshal.PtrToStructure<Rect>(lParam);
                    SetWindowPos(window, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0014);
                }
                UpdateLayout();
                return 0;
            case 0x007E: // WM_DISPLAYCHANGE
                if (fullScreen is not null) FitMonitor();
                break;
            case 0x031A: // WM_THEMECHANGED
                if (notice != 0) UpdateThemeAndFont();
                break;
            case 0x0138: // WM_CTLCOLORSTATIC
                SetTextColor(wParam, textColor);
                SetBkColor(wParam, backgroundColor);
                return background;
            case 0x0014: // WM_ERASEBKGND, clipped by WS_CLIPCHILDREN.
                if (background != 0 && GetClientRect(window, out var area))
                {
                    FillRect(wParam, ref area, background);
                    return 1;
                }
                break;
            case 0x0082: // External destruction: release the managed connection after this callback.
                if (!closing) dispatcher.TryEnqueue(CloseConnection);
                break;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private void FitMonitor()
    {
        var bounds = GetMonitor(window).Bounds;
        SetWindowPos(window, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0014);
    }

    private static MonitorInfo GetMonitor(nint window)
    {
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref monitor))
            throw new Win32Exception();
        return monitor;
    }

    private static nint DispatchMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (!Windows.TryGetValue(window, out var instance))
            return DefWindowProc(window, message, wParam, lParam);
        try { return instance.OnMessage(message, wParam, lParam); }
        catch (Exception ex)
        {
            // Managed exceptions must not cross the unmanaged window-procedure boundary.
            System.Diagnostics.Debug.WriteLine(ex);
            FullScreenTransitionTrace.Write($"Win32 window handler failed message=0x{message:X4} error=0x{ex.HResult:X8}");
            instance.dispatcher.TryEnqueue(() => instance.EndConnection(global::mstscX.Services.L.F("Ошибка окна подключения (0x{0:X8}). {1}", ex.HResult, ex.Message)));
            return DefWindowProc(window, message, wParam, lParam);
        }
    }

    private static void EnsureWindowClass()
    {
        if (classAtom != 0)
            return;
        string iconFile = Path.Combine(AppContext.BaseDirectory, "appicon.ico");
        classIcon = LoadImage(0, iconFile, 1, 32, 32, 0x0010);
        classSmallIcon = LoadImage(0, iconFile, 1, 16, 16, 0x0010);
        var definition = new WindowClassInfo
        {
            Size = (uint)Marshal.SizeOf<WindowClassInfo>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
            Instance = GetModuleHandle(null),
            Icon = classIcon,
            SmallIcon = classSmallIcon,
            Cursor = LoadCursor(0, 32512),
            ClassName = WindowClass
        };
        classAtom = RegisterClassEx(ref definition);
        if (classAtom == 0)
        {
            if (classIcon != 0) DestroyIcon(classIcon);
            if (classSmallIcon != 0) DestroyIcon(classSmallIcon);
            classIcon = classSmallIcon = 0;
            throw new Win32Exception();
        }
    }

    private delegate nint WindowProc(nint window, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] private struct DrawItem
    {
        public uint Type, Id, ItemId, Action, State;
        public nint Window, Dc;
        public Rect Bounds;
        public nuint Data;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect
    {
        public int Left, Top, Right, Bottom;
        internal readonly int Width => Right - Left;
        internal readonly int Height => Bottom - Top;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Bounds, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public PointInt32 Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClassInfo
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
        public nint SmallIcon;
    }
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClassInfo info);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint exStyle, string className, string? text, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(nint window, string text);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool EnableWindow(nint window, bool enabled);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref Rect rect, nint brush);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint window, nint rect, bool erase);
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")] private static extern nint LoadCursor(nint instance, nint name);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint CopyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint CreateIconFromResourceEx(byte[] bits, uint size, [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int width, int height, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] private static extern uint SetBkColor(nint dc, uint color);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern bool FillRgn(nint dc, nint region, nint brush);
    [DllImport("user32.dll")] private static extern bool DrawFocusRect(nint dc, ref Rect rect);
    [DllImport("user32.dll", EntryPoint = "DrawTextW", CharSet = CharSet.Unicode)] private static extern int DrawText(nint dc, string text, int length, ref Rect rect, uint flags);
    [DllImport("gdi32.dll", EntryPoint = "CreateFontW", CharSet = CharSet.Unicode)] private static extern nint CreateFont(int height, int width, int escape, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitch, string face);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, in int value, int size);
}

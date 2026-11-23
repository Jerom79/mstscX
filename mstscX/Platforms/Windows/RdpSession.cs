using System.ComponentModel;
using System.Runtime.InteropServices;
using mstscX.Models;
using Microsoft.UI.Dispatching;

namespace mstscX.WinUI;

internal sealed class RdpSession : IDisposable
{
    private const string ControlClass = "{A0C63C30-F08D-4AB4-907C-34905D770C7D}";
    private const uint ChildWindowStyle = 0x40000000 | 0x10000000 | 0x04000000 | 0x02000000;
    private const uint ShowWithoutActivating = 0x0010 | 0x0040;
    private const uint NoRedraw = 0x0008;
    private const uint RedrawChildrenWithoutErase = 0x0001 | 0x0020 | 0x0080;
    private const int ExtendedStyleIndex = -20;
    private const int NoRedirectionBitmap = 0x00200000;
    private const int LoginCompleteEvent = 3;
    private const int DisconnectedEvent = 4;
    private const int EnterFullScreenEvent = 5;
    private const int LeaveFullScreenEvent = 6;
    private const int RequestFullScreenEvent = 8;
    private const int RequestWindowedEvent = 9;
    private const int RemoteProgramResultEvent = 20;
    private const int RemoteWindowDisplayedEvent = 29;
    private const int AutoReconnectedEvent = 33;
    private const int AutoReconnectingEvent = 34;
    private const int MaxReconnectAttempts = 5;
    private const int VirtualScreenX = 76;
    private const int VirtualScreenY = 77;
    private const int VirtualScreenWidth = 78;
    private const int VirtualScreenHeight = 79;
    private const int MonitorCount = 80;
    private const int RemoteAppShellIconDisplayed = 2;
    private const int LocalDisconnect = 1;
    private const int RemoteDisconnectByUser = 2;
    private const uint VisibleWindowStyle = 0x10000000;
    private const uint SkipServerAuthentication = 0;
    private const uint PromptOnServerAuthenticationFailure = 2;
    private static readonly Guid EventsInterface = new("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6");

    private readonly Action onConnected;
    private readonly Action<int> onDisconnected;
    private readonly Action onEnterFullScreen;
    private readonly Action onLeaveFullScreen;
    private readonly Action onRequestFullScreen;
    private readonly Action onRequestWindowed;
    private readonly Action<int> onFatalError;
    private readonly Action onAutoReconnected;
    private readonly Action<int, bool, int, int> onAutoReconnecting;
    private readonly Action<string, int, bool> onRemoteProgramResult;
    private readonly Action<bool, object, int> onRemoteWindowDisplayed;
    private nint hostWindow;
    private readonly nint parentWindow;
    private NativeRemoteAppHost? nativeRemoteAppHost;
    private readonly bool useXamlComposition;
    private readonly (int Width, int Height)? remoteAppWorkArea;
    private readonly HashSet<nint> clippedCompositionWindows = [];
    private readonly List<nint> remoteWindows = [];
#if DEBUG
    private readonly RemoteAppDisplayTrace remoteAppDisplayTrace = new();
#endif
    private IRdpClient? client;
    private (int X, int Y, int Width, int Height) bounds;
    private bool connectedSubscribed;
    private bool disconnectedSubscribed;
    private bool enterFullScreenSubscribed;
    private bool leaveFullScreenSubscribed;
    private bool requestFullScreenSubscribed;
    private bool requestWindowedSubscribed;
    private bool containerHandlesFullScreen;
    private bool updatingHostedFullScreen;
    private bool fatalSubscribed;
    private bool autoReconnectedSubscribed;
    private bool autoReconnectingSubscribed;
    private int? reconnectReason;
    private int reconnectAttempt;
    private int reconnectLimit;
    private bool remoteProgramResultSubscribed;
    private bool remoteWindowDisplayedSubscribed;
    private RdpRemoteAppOptions? remoteApp;
    private bool oleInitialized;
    private readonly DispatcherQueueTimer resizeTimer;
    private DispatcherQueueTimer? remoteWindowTimer;
    private DispatcherQueueTimer? nativeFullScreenTimer;
    private FullScreenSessionBar? nativeFullScreenBar;
    private FullScreenExitPaintScope? fullScreenExitPaint;
    private NativeResizeTrace? nativeResizeTrace;
#if DEBUG
    private DispatcherQueueTimer? simulatedReconnectTimer;
    private bool simulateReconnect;
#endif
    private bool sawRemoteAppWindow;
    private int missingRemoteWindowChecks;
    private bool loggedIn;
    private bool viewportVisible = true;
    private bool useFixedResolution;
    private bool windowedDisplayUpdateDeferred;
    internal static bool HasMultipleMonitors => GetSystemMetrics(MonitorCount) >= 2;
    public bool UsesMultipleMonitors { get; private set; }

    public RdpSession(nint parentWindow, (int X, int Y, int Width, int Height) initialBounds,
        (int Width, int Height)? remoteAppWorkArea = null, bool useXamlComposition = true,
        bool useNativeRemoteAppHost = false)
    {
        if (useNativeRemoteAppHost && remoteAppWorkArea is null)
            throw new ArgumentException(global::mstscX.Services.L.T("Хост Win32 для RemoteApp требует рабочую область приложения."), nameof(remoteAppWorkArea));
        this.parentWindow = parentWindow;
        this.useXamlComposition = useXamlComposition && !useNativeRemoteAppHost;
        this.remoteAppWorkArea = remoteAppWorkArea;
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        resizeTimer = dispatcher.CreateTimer();
        resizeTimer.Interval = TimeSpan.FromMilliseconds(250);
        resizeTimer.IsRepeating = false;
        resizeTimer.Tick += (_, _) => UpdateRemoteDisplay();
        onConnected = () =>
        {
            FullScreenTransitionTrace.Write("RDP login complete");
            if (remoteApp is { } app && !IsRemoteAppStarted)
            {
                TraceRemoteAppDisplay("login complete");
                dispatcher.TryEnqueue(() => StartRemoteApp(app));
            }
            else if (remoteApp is null && reconnectReason is null)
            {
                Connected?.Invoke();
#if DEBUG
                StartSimulatedReconnect(dispatcher);
#endif
            }
        };
        onDisconnected = ReportDisconnection;
        onEnterFullScreen = () => { if (!containerHandlesFullScreen) NativeFullScreenChanged?.Invoke(true); };
        onLeaveFullScreen = () => { if (!containerHandlesFullScreen) NativeFullScreenChanged?.Invoke(false); };
        onRequestFullScreen = () =>
        {
            if (containerHandlesFullScreen && !updatingHostedFullScreen) HostedFullScreenRequested?.Invoke(true);
        };
        onRequestWindowed = () =>
        {
            if (containerHandlesFullScreen && !updatingHostedFullScreen) HostedFullScreenRequested?.Invoke(false);
        };
        onFatalError = code =>
        {
            remoteWindowTimer?.Stop();
            Disconnected?.Invoke(WithReconnectFailure(global::mstscX.Services.L.F("Ошибка RDP (код {0}).", code)));
        };
        onAutoReconnecting = (reason, networkAvailable, attempt, limit) =>
        {
            reconnectReason = reason;
            reconnectAttempt = attempt;
            reconnectLimit = limit;
            missingRemoteWindowChecks = 0;
            string networkStatus = networkAvailable ? "" : global::mstscX.Services.L.T(" Сеть недоступна.");
            ReconnectStatusChanged?.Invoke(global::mstscX.Services.L.F("Восстанавливаем соединение. {0}{1}", FormatReconnectAttempt(), networkStatus));
        };
        onAutoReconnected = () =>
        {
            reconnectReason = null;
            TraceRemoteAppDisplay("reconnected");
            ReconnectStatusChanged?.Invoke(null);
        };
        onRemoteProgramResult = (_, result, _) =>
        {
            if (result == 0)
            {
                IsRemoteAppStarted = true;
                TraceRemoteAppDisplay("program started");
                Connected?.Invoke();
            }
            else
                Disconnected?.Invoke(global::mstscX.Services.L.F("Сервер не запустил RemoteApp (код {0}).", result));
        };
        onRemoteWindowDisplayed = TrackRemoteAppWindow;

        try
        {
            if (useNativeRemoteAppHost)
            {
                nativeRemoteAppHost = new NativeRemoteAppHost(parentWindow);
                this.parentWindow = parentWindow = nativeRemoteAppHost.Handle;
            }
            if (remoteAppWorkArea is not null)
            {
                FullScreenTransitionTrace.EnsureStarted("RemoteApp host setup");
                FullScreenTransitionTrace.Write($"RemoteApp host mode={(useNativeRemoteAppHost ? "Win32" : "WinUI")} HWND=0x{parentWindow:X}");
            }
            Marshal.ThrowExceptionForHR(OleInitialize(0));
            oleInitialized = true;

            // ATL in-place activates the control using its host's current client rectangle.
            bounds = initialBounds;
            // A local session can need a top-level GDI surface for the RDP child.
            if (remoteAppWorkArea is null)
                EnableNativeChildRendering();

            uint hostStyle = remoteAppWorkArea is null ? ChildWindowStyle : ChildWindowStyle & ~VisibleWindowStyle;
            hostWindow = CreateWindowEx(0, "Static", null, hostStyle,
                bounds.X, bounds.Y, bounds.Width, bounds.Height, parentWindow, 0, 0, 0);
            if (hostWindow == 0)
                throw new Win32Exception();

            int result = AtlAxCreateControlEx(ControlClass, hostWindow, 0, 0,
                out nint control, Guid.Empty, 0);
            try
            {
                Marshal.ThrowExceptionForHR(result);
                client = (IRdpClient)Marshal.GetObjectForIUnknown(control);
            }
            finally
            {
                if (control != 0)
                    Marshal.Release(control);
            }

            ComEventsHelper.Combine(client, EventsInterface, LoginCompleteEvent, onConnected);
            connectedSubscribed = true;
            ComEventsHelper.Combine(client, EventsInterface, DisconnectedEvent, onDisconnected);
            disconnectedSubscribed = true;
            if (remoteAppWorkArea is null)
            {
                ComEventsHelper.Combine(client, EventsInterface, EnterFullScreenEvent, onEnterFullScreen);
                enterFullScreenSubscribed = true;
                ComEventsHelper.Combine(client, EventsInterface, LeaveFullScreenEvent, onLeaveFullScreen);
                leaveFullScreenSubscribed = true;
                ComEventsHelper.Combine(client, EventsInterface, RequestFullScreenEvent, onRequestFullScreen);
                requestFullScreenSubscribed = true;
                ComEventsHelper.Combine(client, EventsInterface, RequestWindowedEvent, onRequestWindowed);
                requestWindowedSubscribed = true;
            }
            ComEventsHelper.Combine(client, EventsInterface, 10, onFatalError);
            fatalSubscribed = true;
            ComEventsHelper.Combine(client, EventsInterface, AutoReconnectedEvent, onAutoReconnected);
            autoReconnectedSubscribed = true;
            ComEventsHelper.Combine(client, EventsInterface, AutoReconnectingEvent, onAutoReconnecting);
            autoReconnectingSubscribed = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public event Action? Connected;
    public event Action<string?>? Disconnected;
    public event Action<bool>? NativeFullScreenChanged;
    public event Action<bool>? HostedFullScreenRequested;
    public event Action<string?>? ReconnectStatusChanged;
    public bool IsRemoteAppStarted { get; private set; }
    public bool ReconnectAttemptsExhausted => reconnectReason.HasValue &&
        reconnectAttempt >= (reconnectLimit > 0 ? reconnectLimit : MaxReconnectAttempts);

#if DEBUG
    private void StartSimulatedReconnect(DispatcherQueue dispatcher)
    {
        if (!simulateReconnect || simulatedReconnectTimer is not null)
            return;

        const int socketClosed = 2308;
        const int connectionTimedOut = 264;
        bool succeeds = string.Equals(Environment.GetEnvironmentVariable("mstscX_DEBUG_RECONNECT_RESULT"),
            "success", StringComparison.OrdinalIgnoreCase);
        int simulatedAttempts = succeeds ? 2 : MaxReconnectAttempts;
        int attempt = 0;
        var timer = dispatcher.CreateTimer();
        simulatedReconnectTimer = timer;
        timer.Interval = TimeSpan.FromSeconds(8);
        timer.Tick += (_, _) =>
        {
            if (++attempt <= simulatedAttempts)
            {
                onAutoReconnecting(socketClosed, false, attempt, MaxReconnectAttempts);
                return;
            }

            timer.Stop();
            if (succeeds)
                onAutoReconnected();
            else
                onDisconnected(connectionTimedOut);
        };
        timer.Start();
    }
#endif

    public void Connect(string address, ServerProfile? profile, string? password, bool singleMonitor = false)
    {
        var rdp = client ?? throw new ObjectDisposedException(nameof(RdpSession));
        if (!RdpEndpoint.TryParse(address, out var endpoint))
            throw new ArgumentException(global::mstscX.Services.L.T("Некорректный адрес RDP."), nameof(address));
        rdp.Server = endpoint.Host;
#if DEBUG
        simulateReconnect = endpoint.Host.Equals(Environment.GetEnvironmentVariable("mstscX_DEBUG_RECONNECT_HOST"),
            StringComparison.OrdinalIgnoreCase);
#endif
        bool isRemoteApp = remoteAppWorkArea is not null;
        UsesMultipleMonitors = !isRemoteApp && !singleMonitor && profile?.Display.UseMultimon == true &&
            HasMultipleMonitors;
        containerHandlesFullScreen = !isRemoteApp && !UsesMultipleMonitors;
        if (remoteAppWorkArea is { } workArea)
        {
            // RAIL windows use the display workspace, independently of the ActiveX host rectangle.
            bool multipleDisplays = HasMultipleMonitors;
            int width = multipleDisplays ? GetSystemMetrics(VirtualScreenWidth) : workArea.Width;
            int height = multipleDisplays ? GetSystemMetrics(VirtualScreenHeight) : workArea.Height;
            rdp.DesktopWidth = Math.Clamp(width, 200, 8192) & ~1;
            rdp.DesktopHeight = Math.Clamp(height, 200, 8192);
            useFixedResolution = true;
        }
        else
        {
            (int Width, int Height) fixedSize = profile?.Display.Resolution switch
            {
                1 => (1024, 768),
                2 => (1280, 720),
                3 => (1600, 900),
                4 => (1920, 1080),
                5 => (2560, 1440),
                _ => (0, 0)
            };
            useFixedResolution = fixedSize.Width != 0;
            rdp.DesktopWidth = useFixedResolution ? fixedSize.Width : Math.Clamp(bounds.Width, 200, 4096);
            rdp.DesktopHeight = useFixedResolution ? fixedSize.Height : Math.Clamp(bounds.Height, 200, 2048);
            useFixedResolution |= UsesMultipleMonitors;
        }

        if (profile is not null)
        {
            rdp.ColorDepth = profile.Display.Colors switch { 1 => 24, 2 => 16, 3 => 15, _ => 32 };
            string[] account = profile.UserName.Split('\\', 2);
            if (account.Length == 2)
                rdp.Domain = account[0];
            rdp.UserName = account[^1];
        }

        var settings = rdp.AdvancedSettings;
        try
        {
            settings.RDPPort = endpoint.Port;
            settings.EnableAutoReconnect = true;
            settings.MaxReconnectAttempts = MaxReconnectAttempts;
            settings.EnableCredSspSupport = true;
            settings.AuthenticationLevel = profile?.TrustServer == true
                ? SkipServerAuthentication
                : PromptOnServerAuthenticationFailure;
            settings.SmartSizing = !isRemoteApp;
            settings.GrabFocusOnConnect = !isRemoteApp;
            settings.DisplayConnectionBar = false;
            if (isRemoteApp || containerHandlesFullScreen)
            {
                // The host owns single-monitor fullscreen geometry. Notify ActiveX of that
                // mode without letting it move the viewport into a separate native window.
                // RemoteApp likewise keeps its desktop in the hidden RAIL container.
                settings.ContainerHandledFullScreen = 1;
            }
            if (profile?.Display is { } display)
            {
                if (display.ConnectionType != 0)
                    settings.NetworkConnectionType = (uint)display.ConnectionType;
                settings.PerformanceFlags = (display.DisableWallpaper ? 0x01 : 0) |
                    (display.DisableFullWindowDrag ? 0x02 : 0) |
                    (display.DisableMenuAnimations ? 0x04 : 0) |
                    (display.FontSmoothing ? 0x80 : 0);
                settings.VideoPlaybackMode = display.VideoPlayback ? 1u : 0u;
            }
            if (profile?.Resources is { } resources)
            {
                settings.AudioRedirectionMode = (uint)resources.AudioPlayback;
                settings.AudioQualityMode = (uint)resources.AudioQuality;
                settings.AudioCaptureRedirectionMode = resources.AudioRecording;
                settings.RedirectPrinters = resources.Printers;
                settings.RedirectClipboard = resources.Clipboard;
                settings.RedirectDrives = resources.EffectiveDriveTargets.Length > 0;
                settings.RedirectPorts = resources.Ports;
                settings.RedirectSmartCards = resources.SmartCards;
                settings.RedirectDevices = resources.Devices;
                settings.RedirectPosDevices = resources.PointOfSaleDevices;

                var secured = rdp.SecuredSettings;
                try { secured.KeyboardHookMode = resources.Keyboard; }
                finally { Marshal.ReleaseComObject(secured); }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(settings);
        }

        var credentials = (IRdpCredentialSettings)rdp;
        if (containerHandlesFullScreen)
            credentials.ConnectionBarText = profile?.Name ?? endpoint.Host;
        if (UsesMultipleMonitors)
        {
            credentials.UseMultimon = true;
            credentials.ConnectionBarText = profile!.Name;
        }
        credentials.DisableConnectionBar = true;
        if (profile?.Resources.EffectiveDriveTargets is { Length: > 0 } driveTargets)
            ConfigureRedirectedDrives(credentials, driveTargets);
        credentials.AllowPromptingForCredentials = true;
        credentials.PromptForCredentials = string.IsNullOrEmpty(password);
        credentials.PromptForCredsOnClient = true;
        if (!string.IsNullOrEmpty(password))
            ((IRdpPasswordSettings)rdp).ClearTextPassword = password;

        if (profile?.RemoteApp is { Enabled: true } app)
        {
            if (string.IsNullOrWhiteSpace(app.Program))
                throw new COMException(global::mstscX.Services.L.T("Укажите опубликованное приложение RemoteApp в профиле."));

            remoteApp = app;
            ComEventsHelper.Combine(rdp, EventsInterface, RemoteProgramResultEvent, onRemoteProgramResult);
            remoteProgramResultSubscribed = true;
            ComEventsHelper.Combine(rdp, EventsInterface, RemoteWindowDisplayedEvent, onRemoteWindowDisplayed);
            remoteWindowDisplayedSubscribed = true;
            var remoteProgram = rdp.RemoteProgram;
            try { remoteProgram.RemoteProgramMode = true; }
            finally { Marshal.ReleaseComObject(remoteProgram); }

            // A wide single desktop does not describe the monitor boundaries to the server.
            // Send the real layout so RAIL maximizes on the application's current monitor.
            credentials.UseMultimon = HasMultipleMonitors;
            TraceRemoteAppDisplay("configured");
        }

        if (profile is not null)
            ConfigureGateway(rdp, profile.Gateway);

        rdp.Connect();
    }

    internal void SetHostedFullScreen(bool fullScreen)
    {
        if (!containerHandlesFullScreen || client is not { Connected: 1 } rdp)
            return;
        try
        {
            if (rdp.FullScreen == fullScreen) return;
            // KeyboardHookMode=2 follows the control's FullScreen flag, not the size
            // or presenter of its parent. ContainerHandledFullScreen keeps HWND layout
            // and dragging in the host while ActiveX handles Windows shortcuts itself.
            updatingHostedFullScreen = true;
            FullScreenTransitionTrace.Write($"RDP hosted fullscreen sync begin value={fullScreen}");
            rdp.FullScreen = fullScreen;
            FullScreenTransitionTrace.Write($"RDP hosted fullscreen sync end actual={rdp.FullScreen}");
        }
        catch (COMException ex)
        {
            FullScreenTransitionTrace.Write($"RDP hosted fullscreen sync failed error=0x{ex.HResult:X8}");
        }
        finally { updatingHostedFullScreen = false; }
    }

    public bool TryEnterNativeFullScreen(Guid profileId, Func<(string Title, string ServerName)> getTitle,
        Action<Windows.Graphics.PointInt32?> exit, Action disconnect, Action minimize, bool attachOnly = false,
        bool returnToMainWindow = false)
    {
        if (containerHandlesFullScreen || client is not { Connected: 1 } rdp)
            return false;

        try
        {
            StopFullScreenExitPaint();
            uint thread = GetWindowThreadProcessId(parentWindow, out _);
            bool alreadyFullScreen = rdp.FullScreen;
            if (attachOnly && !alreadyFullScreen)
                return false;

            StopNativeFullScreenPanel();
            var before = alreadyFullScreen ? [] : GetVisibleActiveXWindows(thread).ToHashSet();
            if (!alreadyFullScreen)
                rdp.FullScreen = true;
            if (!rdp.FullScreen)
                return false;
            resizeTimer.Stop();

            // ActiveX can lay out its fullscreen window again while completing login.
            // Keep the window on the virtual desktop until ActiveX leaves fullscreen.
            int pendingTicks = 0;
            nint fullScreenWindow = 0;
            Windows.Graphics.RectInt32? singleDisplayBounds = null;
            (int Width, int Height) fullScreenClientSize = default;
            int displayUpdateAttempts = 0;
            long nextDisplayUpdate = 0;
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            var timer = dispatcher.CreateTimer();
            nativeFullScreenTimer = timer;
            timer.Interval = TimeSpan.FromMilliseconds(50);
            timer.Tick += (_, _) =>
            {
                if (fullScreenWindow == 0)
                {
                    var candidates = GetVisibleActiveXWindows(thread).Except(before).Take(2).ToArray();
                    if (candidates.Length == 1)
                        fullScreenWindow = candidates[0];
                    else if (++pendingTicks >= 60)
                    {
                        if (UsesMultipleMonitors)
                            StopNativeFullScreenPanel();
                        else
                            TryLeaveNativeFullScreen();
                    }
                }
                if (fullScreenWindow == 0)
                    return;

                if (IsIconic(fullScreenWindow))
                {
                    nativeFullScreenBar?.Update();
                    return;
                }

                if (!IsWindowVisible(fullScreenWindow) || !GetWindowRect(fullScreenWindow, out var rect))
                {
                    StopNativeFullScreenPanel();
                    return;
                }

                if (UsesMultipleMonitors && HasMultipleMonitors)
                {
                    int x = GetSystemMetrics(VirtualScreenX);
                    int y = GetSystemMetrics(VirtualScreenY);
                    int width = GetSystemMetrics(VirtualScreenWidth);
                    int height = GetSystemMetrics(VirtualScreenHeight);
                    const uint noZOrderOrActivation = 0x0014;
                    if (rect.Left != x || rect.Top != y || rect.Right != x + width || rect.Bottom != y + height)
                        SetWindowPos(fullScreenWindow, 0, x, y, width, height, noZOrderOrActivation);
                }
                else if (!UsesMultipleMonitors)
                {
                    var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(fullScreenWindow);
                    singleDisplayBounds ??= Microsoft.UI.Windowing.DisplayArea
                        .GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest)?.OuterBounds;
                    if (singleDisplayBounds is { } display &&
                        (rect.Left != display.X || rect.Top != display.Y ||
                         rect.Right != display.X + display.Width || rect.Bottom != display.Y + display.Height))
                    {
                        const uint noZOrderOrActivation = 0x0014;
                        SetWindowPos(fullScreenWindow, 0, display.X, display.Y, display.Width, display.Height, noZOrderOrActivation);
                    }
                }

                if (nativeFullScreenBar is null)
                {
                    if (!UsesMultipleMonitors)
                    {
                        var ownerId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(parentWindow);
                        if (Microsoft.UI.Windowing.AppWindow.GetFromWindowId(ownerId) is { } owner)
                            FullScreenPanelDrag.EnsureWindowVisible(owner, fullScreenWindow);
                    }
                    var (title, serverName) = getTitle();
                    nativeFullScreenBar = new FullScreenSessionBar(fullScreenWindow, dispatcher, profileId, title, serverName, exit, disconnect, minimize, returnToMainWindow);
                }
                if (singleDisplayBounds.HasValue && !useFixedResolution &&
                    GetClientRect(fullScreenWindow, out var clientRect) &&
                    clientRect.Right >= 200 && clientRect.Bottom >= 200)
                {
                    var clientSize = (clientRect.Right, clientRect.Bottom);
                    long now = Environment.TickCount64;
                    if (clientSize != fullScreenClientSize)
                    {
                        fullScreenClientSize = clientSize;
                        displayUpdateAttempts = 0;
                        // ActiveX can resize its client again after its fullscreen window appears.
                        nextDisplayUpdate = now + 250;
                    }
                    else if (displayUpdateAttempts < 3 && now >= nextDisplayUpdate)
                    {
                        displayUpdateAttempts++;
                        if (UpdateRemoteDisplay(fullScreenClientSize.Width, fullScreenClientSize.Height))
                            displayUpdateAttempts = 3;
                        nextDisplayUpdate = now + 500;
                    }
                }
                nativeFullScreenBar.Update();
            };
            timer.Start();
            return true;
        }
        catch (COMException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return false;
        }
    }

    internal void UpdateNativeFullScreenTitle(string title, string serverName) =>
        nativeFullScreenBar?.UpdateTitle(title, serverName);

    [System.Diagnostics.Conditional("DEBUG")]
    internal void TraceFullScreenExitWindows(bool hosted = false) =>
        FullScreenTransitionTrace.ObserveWindows(parentWindow, hostWindow, hosted ? parentWindow : nativeFullScreenBar?.Owner ?? 0);

    public bool TryLeaveNativeFullScreen()
    {
        if (client is not { } rdp)
            return false;

        try
        {
            FullScreenTransitionTrace.Write("RDP FullScreen getter begin");
            bool wasFullScreen = rdp.FullScreen;
            FullScreenTransitionTrace.Write("RDP FullScreen getter end");
            if (wasFullScreen)
            {
                FullScreenTransitionTrace.Write("Prepare RDP fullscreen exit paint begin");
                StopFullScreenExitPaint();
                var paint = new FullScreenExitPaintScope(nativeFullScreenBar?.Owner ?? 0, hostWindow);
                fullScreenExitPaint = paint;
                FullScreenTransitionTrace.Write("Prepare RDP fullscreen exit paint end");
                try
                {
                    FullScreenTransitionTrace.Write("RDP FullScreen=false begin");
                    rdp.FullScreen = false;
                    FullScreenTransitionTrace.Write("RDP FullScreen=false end");
                }
                catch
                {
                    StopFullScreenExitPaint();
                    throw;
                }
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                // ActiveX can invalidate the old render window again after returning from put_FullScreen.
                _ = Task.Delay(500).ContinueWith(_ => dispatcher.TryEnqueue(() =>
                {
                    if (fullScreenExitPaint == paint)
                        StopFullScreenExitPaint();
                }), TaskScheduler.Default);
                FullScreenTransitionTrace.MarkWindowed();
            }
            FullScreenTransitionTrace.Write("RDP FullScreen verification begin");
            if (rdp.FullScreen)
            {
                FullScreenTransitionTrace.Write("RDP still fullscreen");
                StopFullScreenExitPaint();
                return false;
            }
            FullScreenTransitionTrace.Write("RDP FullScreen verification end");

            FullScreenTransitionTrace.Write("Dispose fullscreen panel begin");
            StopNativeFullScreenPanel();
            FullScreenTransitionTrace.Write("Dispose fullscreen panel end");
            if (!useFixedResolution && !windowedDisplayUpdateDeferred)
                resizeTimer.Start();
            return true;
        }
        catch (COMException ex)
        {
            FullScreenTransitionTrace.Write("Leave RDP fullscreen failed");
            System.Diagnostics.Debug.WriteLine(ex);
            return false;
        }
    }

    private void StopNativeFullScreenPanel()
    {
        nativeFullScreenTimer?.Stop();
        nativeFullScreenTimer = null;
        nativeFullScreenBar?.Dispose();
        nativeFullScreenBar = null;
    }

    private void StopFullScreenExitPaint()
    {
        fullScreenExitPaint?.Dispose();
        fullScreenExitPaint = null;
    }

    private static IEnumerable<nint> GetVisibleActiveXWindows(uint thread)
    {
        for (nint window = FindWindowEx(0, 0, "UIMainClass", null); window != 0;
             window = FindWindowEx(0, window, "UIMainClass", null))
        {
            if (GetWindowThreadProcessId(window, out _) == thread && IsWindowVisible(window))
                yield return window;
        }
    }

    private static void ConfigureRedirectedDrives(IRdpCredentialSettings settings, string targets)
    {
        if (targets == "*")
        {
            settings.RedirectDynamicDrives = true;
            return;
        }

        var selected = targets.Split(';').ToHashSet(StringComparer.OrdinalIgnoreCase);
        settings.RedirectDynamicDrives = selected.Contains(RdpLocalResources.DynamicDrives);
        var drives = settings.DriveCollection;
        try
        {
            drives.RescanDrives(false);
            uint count = drives.DriveCount;
            for (uint index = 0; index < count; index++)
            {
                var drive = drives.GetDrive(index);
                try
                {
                    // ActiveX includes the terminator in the returned BSTR length.
                    drive.RedirectionState = selected.Contains(RdpLocalResources.NormalizeDriveTargets(drive.Name.TrimEnd('\0')));
                }
                finally
                {
                    Marshal.ReleaseComObject(drive);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(drives);
        }
    }

    private static void ConfigureGateway(IRdpClient rdp, RdpGatewayOptions gateway)
    {
        var transport = rdp.TransportSettings;
        try
        {
            if (gateway.UsesConfiguredGateway)
            {
                if (gateway.CredentialSource == 5)
                    throw new COMException(global::mstscX.Services.L.T("Этот шлюз требует веб-аутентификации во внешнем RDP-клиенте."));

                transport.GatewayProfileUsageMethod = gateway.UseExplicitProfile ? 1u : 0u;
                if (!string.IsNullOrWhiteSpace(gateway.HostName))
                    transport.GatewayHostname = gateway.HostName;
                transport.GatewayCredsSource = (uint)gateway.CredentialSource;
                transport.GatewayCredSharing = gateway.ShareCredentials ? 1u : 0u;
            }

            transport.GatewayUsageMethod = (uint)gateway.UsageMethod;
        }
        finally
        {
            Marshal.ReleaseComObject(transport);
        }
    }

    private void TrackRemoteAppWindow(bool displayed, object handle, int attribute)
    {
        if (attribute == RemoteAppShellIconDisplayed)
            return;

        nint window = handle switch
        {
            nint pointer => pointer,
            long value => (nint)value,
            ulong value => unchecked((nint)value),
            int value => (nint)value,
            uint value => (nint)value,
            _ => 0
        };
        if (window == 0)
            return;

        if (displayed)
            RegisterRemoteAppWindow(window);
        else if (!displayed && !IsWindow(window))
            ForgetRemoteAppWindow(window);
    }

    private void RegisterRemoteAppWindow(nint window)
    {
        if (!remoteWindows.Contains(window))
            remoteWindows.Add(window);
#if DEBUG
        remoteAppDisplayTrace.Observe(hostWindow, window);
#endif
    }

    private void ForgetRemoteAppWindow(nint window)
    {
        remoteWindows.Remove(window);
#if DEBUG
        remoteAppDisplayTrace.Forget(window);
#endif
    }

    public void WatchRemoteAppWindows(Func<bool> allowProcessFallback)
    {
        if (remoteWindowTimer is not null)
            return;

        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        remoteWindowTimer = timer;
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            if (reconnectReason.HasValue)
                return;

            for (int index = remoteWindows.Count - 1; index >= 0; index--)
            {
                if (!IsWindow(remoteWindows[index]))
                    ForgetRemoteAppWindow(remoteWindows[index]);
#if DEBUG
                else
                    remoteAppDisplayTrace.Observe(hostWindow, remoteWindows[index]);
#endif
            }
            if (allowProcessFallback())
            {
                foreach (nint window in GetProcessRemoteAppWindows())
                {
                    if (IsWindowVisible(window) || IsIconic(window))
                        RegisterRemoteAppWindow(window);
                }
            }

            if (remoteWindows.Any(window => IsWindowVisible(window) || IsIconic(window)))
            {
                sawRemoteAppWindow = true;
                missingRemoteWindowChecks = 0;
                return;
            }

            if (sawRemoteAppWindow && ++missingRemoteWindowChecks >= 3)
            {
                timer.Stop();
                Disconnected?.Invoke(null);
            }
        };
        timer.Start();
    }

    public bool TryActivateRemoteAppWindow(bool allowProcessFallback)
    {
        for (int index = remoteWindows.Count - 1; index >= 0; index--)
        {
            nint window = remoteWindows[index];
            if (!IsWindow(window))
            {
                ForgetRemoteAppWindow(window);
                continue;
            }

            if (TryActivateWindow(window))
                return true;
        }

        if (!allowProcessFallback)
            return false;

        // Some ActiveX versions do not deliver the HWND through the COM event sink.
        // Only search by process when there is exactly one RemoteApp session.
        return GetProcessRemoteAppWindows().Any(TryActivateWindow);
    }

    private static IEnumerable<nint> GetProcessRemoteAppWindows()
    {
        nint candidate = 0;
        while ((candidate = FindWindowEx(0, candidate, "RAIL_WINDOW", null)) != 0)
        {
            GetWindowThreadProcessId(candidate, out uint processId);
            if (processId == (uint)Environment.ProcessId)
                yield return candidate;
        }
    }

    internal bool TryActivateFullScreenWindow() =>
        nativeFullScreenBar is { } bar && TryActivateWindow(bar.Owner);

    internal static bool TryActivateWindow(nint window)
    {
        nint root = GetAncestor(window, 2); // GA_ROOT
        if (root != 0)
            window = root;
        if (IsIconic(window))
            ShowWindow(window, 9); // SW_RESTORE
        else if (!IsWindowVisible(window))
            return false;

        return SetForegroundWindow(window);
    }

    private void StartRemoteApp(RdpRemoteAppOptions app)
    {
        if (client is not { } rdp)
            return;

        try
        {
            var remoteProgram = rdp.RemoteProgram;
            try
            {
                remoteProgram.ServerStartProgram(app.Program, null, app.WorkingDirectory, true, app.Arguments, true);
            }
            finally
            {
                Marshal.ReleaseComObject(remoteProgram);
            }
        }
        catch (COMException ex)
        {
            Disconnected?.Invoke(global::mstscX.Services.L.F("Не удалось запустить RemoteApp (0x{0:X8}). {1}", ex.HResult, ex.Message));
        }
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void TraceRemoteAppDisplay(string stage)
    {
#if DEBUG
        if (remoteAppWorkArea is null || client is not { } rdp)
            return;

        try
        {
            var display = (IRdpCredentialSettings)rdp;
            var program = rdp.RemoteProgram;
            bool railMode;
            try { railMode = program.RemoteProgramMode; }
            finally { Marshal.ReleaseComObject(program); }
            RemoteAppDisplayTrace.Write(hostWindow, $"{stage}: localMonitors={GetSystemMetrics(MonitorCount)} " +
                $"useMultimon={display.UseMultimon} railMode={railMode} fullScreen={rdp.FullScreen}");
            if (rdp.Connected == 1)
                RemoteAppDisplayTrace.Write(hostWindow, $"{stage}: remoteMonitors={display.RemoteMonitorCount} " +
                    $"layoutMatches={display.RemoteMonitorLayoutMatchesLocal}");
        }
        catch (COMException ex)
        {
            RemoteAppDisplayTrace.Write(hostWindow, $"{stage}: display information unavailable 0x{ex.HResult:X8}");
        }
#endif
    }

    internal void PrepareWindowedViewport((int X, int Y, int Width, int Height) nextBounds)
    {
        if (hostWindow == 0 || !viewportVisible)
            return;

        FullScreenTransitionTrace.Write("Prepare windowed viewport begin");
        if (nextBounds == bounds)
            UpdateCompositionRegions();
        else
            Resize(nextBounds);
        FullScreenTransitionTrace.Write("Prepare windowed viewport end");
    }

    public void Resize((int X, int Y, int Width, int Height) nextBounds, bool refreshComposition = false)
    {
        if (hostWindow == 0 || !viewportVisible)
            return;

        refreshComposition &= loggedIn;
        bool sizeChanged = nextBounds.Width != bounds.Width || nextBounds.Height != bounds.Height;
        if (nextBounds != bounds)
            FullScreenTransitionTrace.Write("Resize RDP host HWND begin");
        bool viewportChanged = nextBounds != bounds && SetWindowPos(hostWindow, 0, nextBounds.X, nextBounds.Y,
                nextBounds.Width, nextBounds.Height, ShowWithoutActivating | (refreshComposition ? NoRedraw : 0));
        if (nextBounds != bounds)
            FullScreenTransitionTrace.Write("Resize RDP host HWND end");
        if (viewportChanged)
            bounds = nextBounds;

        if (!loggedIn)
            return;

        if (viewportChanged && sizeChanged && !windowedDisplayUpdateDeferred)
        {
            resizeTimer.Stop();
            resizeTimer.Start();
        }
        if (viewportChanged || refreshComposition)
        {
            FullScreenTransitionTrace.Write("Update WinUI composition regions begin");
            UpdateCompositionRegions(redraw: !refreshComposition);
            FullScreenTransitionTrace.Write("Update WinUI composition regions end");
        }
        if (refreshComposition)
        {
            FullScreenTransitionTrace.Write("Invalidate parent redraw begin");
            RedrawWindow(parentWindow, 0, 0, RedrawChildrenWithoutErase);
            FullScreenTransitionTrace.Write("Invalidate parent redraw end");
        }
    }

    public void ShowSession()
    {
        loggedIn = true;
        if (!viewportVisible)
            return;

        if (BorderlessSessionWindow.IsRemoteSession)
            nativeResizeTrace ??= new NativeResizeTrace(parentWindow);

        // The XAML composition surface otherwise covers the native child at this rectangle.
        EnableNativeChildRendering();
        SetWindowPos(hostWindow, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, ShowWithoutActivating);
        UpdateCompositionRegions();
        RedrawWindow(hostWindow, 0, 0, 0x0081);
        if (!windowedDisplayUpdateDeferred)
            resizeTimer.Start();
    }

    public void SetViewportVisible(bool visible)
    {
        if (viewportVisible == visible || hostWindow == 0)
            return;

        viewportVisible = visible;
        if (visible)
        {
            ShowWindow(hostWindow, 4); // SW_SHOWNOACTIVATE
            if (loggedIn)
                ShowSession();
        }
        else
        {
            resizeTimer.Stop();
            ShowWindow(hostWindow, 0); // SW_HIDE
            RestoreCompositionRegions();
        }
    }

    private void RestoreCompositionRegions()
    {
        foreach (nint compositionWindow in clippedCompositionWindows)
            SetWindowRgn(compositionWindow, 0, true);
        clippedCompositionWindows.Clear();
    }

    public void ActivateViewport()
    {
        if (!loggedIn || !viewportVisible || hostWindow == 0)
            return;

        EnableNativeChildRendering();
        SetWindowPos(hostWindow, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, ShowWithoutActivating);
        UpdateCompositionRegions();
        RedrawWindow(hostWindow, 0, 0, 0x0081);
        nint controlWindow = GetWindow(hostWindow, 5); // GW_CHILD
        SetFocus(controlWindow != 0 ? controlWindow : hostWindow);
    }

    private void EnableNativeChildRendering()
    {
        if (!useXamlComposition)
            return;
        int style = GetWindowLong(parentWindow, ExtendedStyleIndex);
        if (BorderlessSessionWindow.IsRemoteSession)
        {
            // The remote-machine trace stalls inside WinUI's WM_NCPAINT and
            // WM_WINDOWPOSCHANGED, before the RDP host is resized. Keep WinUI's
            // original composition mode in an outer RDP session instead of forcing
            // a second, GDI-backed top-level surface. Child viewport clipping stays
            // in UpdateCompositionRegions; no HWND or mouse-capture changes here.
            FullScreenTransitionTrace.EnsureStarted("Remote session rendering setup");
            FullScreenTransitionTrace.Write($"Keep WinUI parent rendering mode in remote session " +
                $"HWND=0x{parentWindow:X} exStyle=0x{style:X8}");
            return;
        }
        if ((style & NoRedirectionBitmap) == 0)
            return;

        SetWindowLong(parentWindow, ExtendedStyleIndex, style & ~NoRedirectionBitmap);
        SetWindowPos(parentWindow, 0, 0, 0, 0, 0, 0x0037);
    }

    internal void DeferWindowedDisplayUpdate(bool defer)
    {
        if (windowedDisplayUpdateDeferred == defer)
            return;

        windowedDisplayUpdateDeferred = defer;
        resizeTimer.Stop();
        // SmartSizing continues to use the current desktop after a drag. The next
        // actual resize can renegotiate it, avoiding a second redraw on mouse-up.
        FullScreenTransitionTrace.Write(defer ? "Windowed display update deferred" : "Windowed display update resumed");
    }

    private void UpdateRemoteDisplay()
    {
        if (nativeFullScreenTimer is null && !windowedDisplayUpdateDeferred)
            UpdateRemoteDisplay(bounds.Width, bounds.Height);
    }

    private bool UpdateRemoteDisplay(int displayWidth, int displayHeight)
    {
        if (useFixedResolution || client is not { } rdp || displayWidth < 200 || displayHeight < 200)
            return false;

        // The RDP display-control channel requires an even desktop width.
        uint width = (uint)(Math.Min(displayWidth, 8192) & ~1);
        uint height = (uint)Math.Min(displayHeight, 8192);
        try
        {
            FullScreenTransitionTrace.Write("RDP UpdateSessionDisplaySettings begin");
            rdp.UpdateSessionDisplaySettings(width, height, 0, 0, 0, 100, 100);
            FullScreenTransitionTrace.Write("RDP UpdateSessionDisplaySettings end");
            return true;
        }
        catch (COMException ex)
        {
            FullScreenTransitionTrace.Write("RDP UpdateSessionDisplaySettings failed");
            // Older hosts can continue using the client's SmartSizing.
            System.Diagnostics.Debug.WriteLine($"RDP display update failed: {ex}");
            return false;
        }
    }

    private void UpdateCompositionRegions(bool redraw = true)
    {
        if (!useXamlComposition)
            return;
        if (!GetWindowRect(hostWindow, out var viewportRect))
            return;

        // A custom title bar can add another bridge to the main window.
        // Clip every bridge that actually overlaps the RDP viewport.
        nint bridge = 0;
        while ((bridge = FindWindowEx(parentWindow, bridge, "Microsoft.UI.Content.DesktopChildSiteBridge", null)) != 0)
        {
            if (GetWindowRect(bridge, out var rect) &&
                rect.Left < viewportRect.Right && rect.Right > viewportRect.Left &&
                rect.Top < viewportRect.Bottom && rect.Bottom > viewportRect.Top)
            {
                UpdateCompositionRegion(bridge, rect, viewportRect, redraw);
            }
            else if (clippedCompositionWindows.Remove(bridge))
            {
                SetWindowRgn(bridge, 0, redraw);
            }
        }
    }

    private void UpdateCompositionRegion(nint compositionWindow, WindowRect compositionRect, WindowRect viewportRect, bool redraw)
    {
        nint surface = CreateRectRgn(0, 0, compositionRect.Right - compositionRect.Left,
            compositionRect.Bottom - compositionRect.Top);
        nint viewport = CreateRectRgn(viewportRect.Left - compositionRect.Left,
            viewportRect.Top - compositionRect.Top, viewportRect.Right - compositionRect.Left,
            viewportRect.Bottom - compositionRect.Top);

        // SetWindowRgn takes ownership of the region when it succeeds.
        if (surface != 0 && viewport != 0 &&
            CombineRgn(surface, surface, viewport, 4) != 0 &&
            SetWindowRgn(compositionWindow, surface, redraw) != 0)
        {
            clippedCompositionWindows.Add(compositionWindow);
            surface = 0;
        }

        if (surface != 0)
            DeleteObject(surface);
        if (viewport != 0)
            DeleteObject(viewport);
    }

    private void ReportDisconnection(int reason)
    {
        remoteWindowTimer?.Stop();
        string? description = null;
        uint extendedReason = 0;
        if (client is { } rdp)
        {
            try
            {
                extendedReason = rdp.ExtendedDisconnectReason;
                if ((reason == RemoteDisconnectByUser || (reason == LocalDisconnect && reconnectReason is null)) &&
                    extendedReason is 0 or 1 or 2 or 11 or 12)
                {
                    Disconnected?.Invoke(null);
                    return;
                }

                description = rdp.GetErrorDescription((uint)reason, extendedReason);
            }
            catch (COMException)
            {
                // Preserve the numeric reason if the control cannot translate it.
            }
        }

        string codes = extendedReason == 0 ? $"{reason}" : global::mstscX.Services.L.F("{0}, дополнительный код {1}", reason, extendedReason);
        string message = global::mstscX.Services.L.F("Соединение завершено (код {0}).", codes);
        if (!string.IsNullOrWhiteSpace(description))
            message += $" {description}";
        Disconnected?.Invoke(WithReconnectFailure(message));
    }

    private string FormatReconnectAttempt() => reconnectLimit > 0
        ? global::mstscX.Services.L.F("Попытка {0} из {1}.", reconnectAttempt, reconnectLimit)
        : global::mstscX.Services.L.F("Попытка {0}.", reconnectAttempt);

    private string WithReconnectFailure(string message) => reconnectReason is { } reason
        ? global::mstscX.Services.L.F("Не удалось восстановить соединение. {0} Причина обрыва RDP: {1}. {2}", FormatReconnectAttempt(), reason, message)
        : message;

    public void Dispose() => Dispose(disconnect: true);

    public void Dispose(bool disconnect)
    {
        FullScreenTransitionTrace.Write($"RDP dispose begin disconnect={disconnect}");
        nativeResizeTrace?.Dispose();
        nativeResizeTrace = null;
        StopFullScreenExitPaint();
        StopNativeFullScreenPanel();
#if DEBUG
        simulatedReconnectTimer?.Stop();
#endif
        resizeTimer.Stop();
        remoteWindowTimer?.Stop();
        try
        {
            if (client is { } rdp)
            {
                client = null;
                try
                {
                    FullScreenTransitionTrace.Write("Unsubscribe ActiveX events begin");
                    if (connectedSubscribed)
                        ComEventsHelper.Remove(rdp, EventsInterface, LoginCompleteEvent, onConnected);
                    if (disconnectedSubscribed)
                        ComEventsHelper.Remove(rdp, EventsInterface, DisconnectedEvent, onDisconnected);
                    if (enterFullScreenSubscribed)
                        ComEventsHelper.Remove(rdp, EventsInterface, EnterFullScreenEvent, onEnterFullScreen);
                    if (leaveFullScreenSubscribed)
                        ComEventsHelper.Remove(rdp, EventsInterface, LeaveFullScreenEvent, onLeaveFullScreen);
                    if (requestFullScreenSubscribed)
                        ComEventsHelper.Remove(rdp, EventsInterface, RequestFullScreenEvent, onRequestFullScreen);
                    if (requestWindowedSubscribed)
                        ComEventsHelper.Remove(rdp, EventsInterface, RequestWindowedEvent, onRequestWindowed);
                    if (fatalSubscribed)
                        ComEventsHelper.Remove(rdp, EventsInterface, 10, onFatalError);
                    if (autoReconnectedSubscribed)
                        ComEventsHelper.Remove(rdp, EventsInterface, AutoReconnectedEvent, onAutoReconnected);
                    if (autoReconnectingSubscribed)
                        ComEventsHelper.Remove(rdp, EventsInterface, AutoReconnectingEvent, onAutoReconnecting);
                    if (remoteProgramResultSubscribed)
                        ComEventsHelper.Remove(rdp, EventsInterface, RemoteProgramResultEvent, onRemoteProgramResult);
                    if (remoteWindowDisplayedSubscribed)
                        ComEventsHelper.Remove(rdp, EventsInterface, RemoteWindowDisplayedEvent, onRemoteWindowDisplayed);
                    FullScreenTransitionTrace.Write("Unsubscribe ActiveX events end");

                    if (disconnect && rdp.Connected != 0)
                    {
                        FullScreenTransitionTrace.Write("ActiveX Disconnect begin");
                        rdp.Disconnect();
                        FullScreenTransitionTrace.Write("ActiveX Disconnect end");
                    }
                }
                catch (COMException)
                {
                    // Destroying the host also tears down an in-progress connection.
                }
                finally
                {
                    FullScreenTransitionTrace.Write("Release ActiveX COM object begin");
                    Marshal.ReleaseComObject(rdp);
                    FullScreenTransitionTrace.Write("Release ActiveX COM object end");
                }
            }
        }
        finally
        {
            remoteWindows.Clear();
            FullScreenTransitionTrace.Write("Restore composition regions begin");
            RestoreCompositionRegions();
            FullScreenTransitionTrace.Write("Restore composition regions end");

            if (hostWindow != 0)
            {
                FullScreenTransitionTrace.Write("Destroy RDP host window begin");
                DestroyWindow(hostWindow);
                FullScreenTransitionTrace.Write("Destroy RDP host window end");
                hostWindow = 0;
            }

            nativeRemoteAppHost?.Dispose();
            nativeRemoteAppHost = null;
            if (oleInitialized)
            {
                oleInitialized = false;
                OleUninitialize();
            }
            FullScreenTransitionTrace.Write("RDP dispose end");
        }
    }

    [ComImport, Guid("7ED92C39-EB38-4927-A70A-708AC5A59321"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IRdpClient
    {
        [DispId(1)] string Server { set; }
        [DispId(2)] string Domain { set; }
        [DispId(3)] string UserName { set; }
        [DispId(6)] short Connected { get; }
        [DispId(12)] int DesktopWidth { set; }
        [DispId(13)] int DesktopHeight { set; }
        [DispId(30)] void Connect();
        [DispId(31)] void Disconnect();
        [DispId(103)] uint ExtendedDisconnectReason { get; }
        [DispId(104)] bool FullScreen { get; set; }
        [DispId(100)] int ColorDepth { set; }
        [DispId(102)] IRdpSecuredSettings SecuredSettings { get; }
        [DispId(503)] string GetErrorDescription(uint reason, uint extendedReason);
        [DispId(504)] IRdpRemoteProgram RemoteProgram { get; }
        [DispId(506)] IRdpGatewayTransportSettings TransportSettings { get; }
        [DispId(701)] IRdpAdvancedSettings AdvancedSettings { get; }
        [DispId(802)] void UpdateSessionDisplaySettings(uint width, uint height,
            uint physicalWidth, uint physicalHeight, uint orientation, uint desktopScale, uint deviceScale);
    }

    [ComImport, Guid("67341688-D606-4C73-A5D2-2E0489009319"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IRdpGatewayTransportSettings
    {
        [DispId(210)] string GatewayHostname { set; }
        [DispId(211)] uint GatewayUsageMethod { set; }
        [DispId(212)] uint GatewayProfileUsageMethod { set; }
        [DispId(213)] uint GatewayCredsSource { set; }
        [DispId(222)] uint GatewayCredSharing { set; }
    }

    [ComImport, Guid("FDD029F9-467A-4C49-8529-64B521DBD1B4"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IRdpRemoteProgram
    {
        [DispId(200)] bool RemoteProgramMode { get; set; }
        [DispId(201)] void ServerStartProgram(string executablePath, string? filePath, string workingDirectory,
            bool expandWorkingDirectoryOnServer, string arguments, bool expandArgumentsOnServer);
    }

    [ComImport, Guid("89ACB528-2557-4D16-8625-226A30E97E9A"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IRdpAdvancedSettings
    {
        [DispId(173)] int ContainerHandledFullScreen { get; set; }
        [DispId(17)] bool EnableCredSspSupport { set; }
        [DispId(184)] bool SmartSizing { set; }
        [DispId(187)] bool DisplayConnectionBar { set; }
        [DispId(189)] bool GrabFocusOnConnect { set; }
        [DispId(206)] bool EnableAutoReconnect { set; }
        [DispId(207)] int MaxReconnectAttempts { set; }
        [DispId(212)] uint AuthenticationLevel { set; }
        [DispId(108)] int RDPPort { set; }
        [DispId(191)] bool RedirectDrives { set; }
        [DispId(192)] bool RedirectPrinters { set; }
        [DispId(193)] bool RedirectPorts { set; }
        [DispId(194)] bool RedirectSmartCards { set; }
        [DispId(213)] bool RedirectClipboard { set; }
        [DispId(215)] uint AudioRedirectionMode { set; }
        [DispId(218)] bool RedirectDevices { set; }
        [DispId(219)] bool RedirectPosDevices { set; }
        [DispId(228)] bool AudioCaptureRedirectionMode { set; }
        [DispId(200)] int PerformanceFlags { set; }
        [DispId(229)] uint VideoPlaybackMode { set; }
        [DispId(233)] uint AudioQualityMode { set; }
        [DispId(235)] uint NetworkConnectionType { set; }
    }

    [ComImport, Guid("605BEFCF-39C1-45CC-A811-068FB7BE346D"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IRdpSecuredSettings
    {
        [DispId(4)] int KeyboardHookMode { set; }
    }

    [ComImport, Guid("2F079C4C-87B2-4AFD-97AB-20CDB43038AE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRdpPasswordSettings
    {
        string ClearTextPassword { [param: MarshalAs(UnmanagedType.BStr)] set; }
    }

    [ComImport, Guid("4F6996D5-D7B1-412C-B0FF-063718566907"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRdpCredentialSettings
    {
        // Offsets in IMsRdpClientNonScriptable5, excluding IUnknown.
        void _VtblGap1_16();
        bool PromptForCredentials { [param: MarshalAs(UnmanagedType.VariantBool)] set; }
        void _VtblGap2_5();
        bool RedirectDynamicDrives { [param: MarshalAs(UnmanagedType.VariantBool)] set; }
        void _VtblGap3_4();
        IRdpDriveCollection DriveCollection { get; }
        void _VtblGap4_4();
        string ConnectionBarText { [param: MarshalAs(UnmanagedType.BStr)] set; }
        void _VtblGap7_11();
        bool PromptForCredsOnClient { [param: MarshalAs(UnmanagedType.VariantBool)] set; }
        void _VtblGap5_5();
        bool UseMultimon
        {
            [param: MarshalAs(UnmanagedType.VariantBool)] set;
            [return: MarshalAs(UnmanagedType.VariantBool)] get;
        }
        uint RemoteMonitorCount { get; }
        void GetRemoteMonitorsBoundingBox(out int left, out int top, out int right, out int bottom);
        bool RemoteMonitorLayoutMatchesLocal { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool DisableConnectionBar { [param: MarshalAs(UnmanagedType.VariantBool)] set; }
        void _VtblGap8_4();
        bool AllowPromptingForCredentials { [param: MarshalAs(UnmanagedType.VariantBool)] set; }
    }

    [ComImport, Guid("7FF17599-DA2C-4677-AD35-F60C04FE1585"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRdpDriveCollection
    {
        void RescanDrives([MarshalAs(UnmanagedType.VariantBool)] bool redirectNewDrives);
        IRdpDrive GetDrive(uint index);
        uint DriveCount { get; }
    }

    [ComImport, Guid("D28B5458-F694-47A8-8E61-40356A767E46"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRdpDrive
    {
        string Name { [return: MarshalAs(UnmanagedType.BStr)] get; }
        bool RedirectionState { [param: MarshalAs(UnmanagedType.VariantBool)] set; }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int OleInitialize(nint reserved);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void OleUninitialize();

    [DllImport("atl.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AtlAxCreateControlEx(string controlId, nint window, nint stream,
        nint container, out nint control, in Guid eventInterface, nint eventSink);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string? title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint window, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(nint window, nint updateRect, nint updateRegion, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out WindowRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out WindowRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);

    [DllImport("user32.dll")]
    private static extern int ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(nint destination, nint first, nint second, int mode);


    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);
}

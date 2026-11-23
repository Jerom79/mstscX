using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using mstscX.Models;
using mstscX.Services;
using mstscX.ViewModels;
using mstscX.WinUI;
using WinRT.Interop;
using Windows.Graphics;
using NativeElement = Microsoft.UI.Xaml.FrameworkElement;
using NativeWindow = Microsoft.UI.Xaml.Window;

namespace mstscX.Views;

public partial class SessionPage : ContentView
{
    private RdpSession? session;
    private NativeElement? viewport;
    private AppWindow? fullScreenWindow;
    private BorderlessSessionWindow? borderlessFullScreenWindow;
    private AppWindow? nativeFullScreenOwner;
    private bool restoreFullScreenAfterMinimize;
    private AppWindow? sessionWindow;
    private OverlappedPresenterState? lastWindowState;
    private bool restoreOnFullScreenExit;
    private bool closingWindow;
    private bool switchingMonitorMode;
    private bool changingWindowLayout;
    private bool preparingNativeFullScreenExit;
    private bool sessionBoundsUpdateQueued;
    private bool refreshSessionComposition;
    private SessionWindowLayout windowLayout;
    private bool sessionWasConnected;
    private FullScreenSessionBar? fullScreenBar;
    private readonly IDispatcherTimer panelTimer;
    private readonly IDispatcherTimer maximizeTimer;
    private readonly SessionViewModel viewModel;
    private readonly string address;
    private string serverName;
    private string SessionTitle => $"{serverName} - [{address}]";
    private readonly ServerProfile profile;
    private readonly Func<Task<string?>> readPassword;
    private readonly Action? onSessionConnected;
    private readonly Action<string?>? onReconnectStatusChanged;
    private readonly Func<Microsoft.Maui.Controls.Window?> getWindow;
    private readonly Action closeSession;
    private readonly Action<bool>? setEmbeddedFullScreen;
    private Action<string?>? onSessionEnded;
    private NativeElement? pendingViewport;
    private bool viewportVisible = true;
    internal bool HasEnded { get; private set; }

    internal SessionPage(string address, ServerProfile profile, SessionWindowLayout initialLayout, string? password,
        Func<Task<string?>> readPassword,
        Func<Microsoft.Maui.Controls.Window?> getWindow, Action closeSession,
        Action<bool>? setEmbeddedFullScreen = null, Action<string?>? onSessionEnded = null,
        Action? onSessionConnected = null, Action<string?>? onReconnectStatusChanged = null)
    {
        InitializeComponent();
        this.address = address;
        this.profile = profile;
        serverName = profile.Name;
        windowLayout = initialLayout with { FullScreen = false };
        ConnectionTitle.Text = SessionTitle;
        SessionEndpoint.Text = $"Адрес: {RdpEndpoint.FormatForDisplay(address)}";
        this.readPassword = readPassword;
        this.onSessionConnected = onSessionConnected;
        this.onReconnectStatusChanged = onReconnectStatusChanged;
        this.getWindow = getWindow;
        this.closeSession = closeSession;
        this.setEmbeddedFullScreen = setEmbeddedFullScreen;
        this.onSessionEnded = onSessionEnded;
        viewModel = new SessionViewModel(setEmbeddedFullScreen is null ? $"[{address}]" : address);
        BindingContext = viewModel;
        SetWindowedLayout();
        panelTimer = Dispatcher.CreateTimer();
        panelTimer.Interval = TimeSpan.FromMilliseconds(50);
        panelTimer.Tick += (_, _) => fullScreenBar?.Update();
        maximizeTimer = Dispatcher.CreateTimer();
        maximizeTimer.Interval = TimeSpan.FromMilliseconds(16);
        maximizeTimer.Tick += (_, _) => CompleteWindowMaximize();
        Loaded += StartSession;
        Unloaded += (_, _) =>
        {
            if (pendingViewport is not null)
                pendingViewport.LayoutUpdated -= StartWhenSized;
            pendingViewport = null;
            password = null;
            EndSession();
        };

        void StartSession(object? sender, EventArgs args)
        {
            Loaded -= StartSession;
            pendingViewport = SessionArea.Handler?.PlatformView as NativeElement;
            if (pendingViewport is not null)
                pendingViewport.LayoutUpdated += StartWhenSized;
            Dispatcher.Dispatch(() => StartWhenSized(null, EventArgs.Empty));
        }

        void StartWhenSized(object? sender, object args)
        {
            if (!IsLoaded || HasEnded || !viewportVisible ||
                pendingViewport is not { XamlRoot: not null, ActualWidth: >= 1, ActualHeight: >= 1 } element ||
                getWindow()?.Handler?.PlatformView is not NativeWindow)
                return;

            // Start OLE once the native layout callback has finished.
            if (!element.DispatcherQueue.TryEnqueue(() =>
            {
                if (IsLoaded && !HasEnded)
                    Connect(password, initialLayout.FullScreen && profile.Display.UseMultimon, initialLayout.FullScreen);
                password = null;
            }))
                return;

            element.LayoutUpdated -= StartWhenSized;
            pendingViewport = null;
        }
    }

    private void Connect(string? password, bool useMultipleMonitors, bool enterFullScreen)
    {
        if (SessionArea.Handler?.PlatformView is not NativeElement element ||
            element.XamlRoot is not { } root || element.ActualWidth < 1 || element.ActualHeight < 1 ||
            getWindow()?.Handler?.PlatformView is not NativeWindow window)
        {
            ReportFailure("Область удалённого рабочего стола пока недоступна.");
            return;
        }

        try
        {
            if (setEmbeddedFullScreen is null)
                FullScreenPanelDrag.EnsureWindowVisible(window.AppWindow);
            var nextSession = new RdpSession(WindowNative.GetWindowHandle(window), GetSessionBounds(element, root));
            session = nextSession;
            sessionWindow = window.AppWindow;
            if (setEmbeddedFullScreen is null)
                sessionWindow.Closing += OnSessionWindowClosing;
            viewport = element;
            viewport.LayoutUpdated += OnViewportLayoutUpdated;

            // Defer UI changes and disposal until the ActiveX callback returns.
            nextSession.Connected += () => element.DispatcherQueue.TryEnqueue(() =>
            {
                if (session != nextSession)
                    return;

                viewModel.ReportConnected();
                sessionWasConnected = true;
                onReconnectStatusChanged?.Invoke(null);
                onSessionConnected?.Invoke();
                nextSession.ShowSession();
                if (sessionWindow is null)
                    return;
                lastWindowState = (sessionWindow.Presenter as OverlappedPresenter)?.State;
                if (setEmbeddedFullScreen is null)
                    RememberWindowSize(sessionWindow);
                sessionWindow.Changed += OnSessionWindowChanged;
                if (viewportVisible && enterFullScreen && fullScreenWindow is null)
                    EnterFullScreen(sessionWindow);
            });
            nextSession.Disconnected += message => element.DispatcherQueue.TryEnqueue(() =>
            {
                if (session != nextSession)
                    return;

                if (message is not null)
                {
                    bool closeExternalWindow = setEmbeddedFullScreen is null && nextSession.ReconnectAttemptsExhausted;
                    ReportFailure(message);
                    if (closeExternalWindow)
                        closeSession();
                }
                else
                {
                    EndSession();
                    viewModel.ReportDisconnected("Подключение завершено.");
                }
            });
            nextSession.ReconnectStatusChanged += message => element.DispatcherQueue.TryEnqueue(() =>
            {
                if (session == nextSession)
                {
                    viewModel.ReportReconnectStatus(message);
                    onReconnectStatusChanged?.Invoke(message);
                }
            });
            nextSession.NativeFullScreenChanged += entered => element.DispatcherQueue.TryEnqueue(() =>
            {
                if (HasEnded || switchingMonitorMode || session != nextSession || sessionWindow is not { } owner)
                    return;

                if (entered && nativeFullScreenOwner is null && fullScreenWindow is null)
                    TryEnterNativeFullScreen(nextSession, owner, restoreOnExit: false, attachOnly: true);
                else if (!entered && nativeFullScreenOwner is not null)
                    ReturnToWindow();
            });

            UpdateSessionBounds();
            nextSession.SetViewportVisible(viewportVisible);
            nextSession.Connect(address, profile, password, singleMonitor: !useMultipleMonitors);
        }
        catch (Exception ex) when (ex is ExternalException or DllNotFoundException or EntryPointNotFoundException)
        {
            ReportFailure($"Не удалось запустить RDP (0x{ex.HResult:X8}). {ex.Message}");
        }
    }

    private void Disconnect(object? sender, EventArgs args) => CloseConnection();

    internal async void CloseConnection()
    {
        if (closingWindow)
            return;

        nint owner = getWindow()?.Handler?.PlatformView is NativeWindow nativeWindow
            ? WindowNative.GetWindowHandle(nativeWindow) : 0;
        if (!HasEnded && !await ConnectionCloseConfirmation.ConfirmAsync(owner, serverName))
            return;

        if (closingWindow) return;

        closingWindow = true;
        EndSession();
        closeSession();
    }

    internal void SetEmbeddedVisible(bool visible)
    {
        viewportVisible = visible;
        session?.SetViewportVisible(visible);
    }

    internal void ActivateEmbeddedSession()
    {
        UpdateSessionBounds();
        if (!TryActivateFullScreenWindow())
            session?.ActivateViewport();
    }

    internal bool TryActivateFullScreenWindow()
    {
        if (borderlessFullScreenWindow is not null && fullScreenWindow is not null &&
            getWindow()?.Handler?.PlatformView is NativeWindow window)
        {
            // The borderless window still has an OverlappedPresenter. Activate it here
            // so the regular-window path does not clamp it to the monitor's work area.
            RdpSession.TryActivateWindow(WindowNative.GetWindowHandle(window));
            window.Activate();
            return true;
        }
        return session?.TryActivateFullScreenWindow() == true;
    }

    private void OnViewportLayoutUpdated(object? sender, object e) => QueueSessionBoundsUpdate();

    private void OnSessionWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        if (closingWindow || getWindow()?.Handler?.PlatformView is not NativeWindow window)
            return;

        // Leave the native close callback before tearing down its ActiveX child.
        if (!window.DispatcherQueue.TryEnqueue(async () =>
        {
            if (!HasEnded && !await ConnectionCloseConfirmation.ConfirmAsync(WindowNative.GetWindowHandle(window), serverName))
                return;
            if (closingWindow) return;
            closingWindow = true;
            EndSession();
            window.Close();
        }))
        {
            closingWindow = false;
            args.Cancel = false;
        }
    }

    private void OnSessionWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (sender.Presenter is not OverlappedPresenter presenter)
            return;

        var previousState = lastWindowState;
        lastWindowState = presenter.State;
        if (restoreFullScreenAfterMinimize && previousState == OverlappedPresenterState.Minimized &&
            presenter.State != OverlappedPresenterState.Minimized)
        {
            restoreFullScreenAfterMinimize = false;
            if (viewportVisible && getWindow()?.Handler?.PlatformView is NativeWindow nativeWindow)
                nativeWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!HasEnded && viewportVisible && sessionWindow == sender &&
                        sender.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Minimized })
                        EnterFullScreen(sender, restoreOnFullScreenExit);
                });
            return;
        }
        if (setEmbeddedFullScreen is not null)
            return;

        if (args.DidSizeChange)
            RememberWindowSize(sender);
        if (!changingWindowLayout && fullScreenWindow is null &&
            previousState == OverlappedPresenterState.Restored &&
            presenter.State == OverlappedPresenterState.Maximized &&
            getWindow()?.Handler?.PlatformView is NativeWindow window)
        {
            // The maximize button and a double-click on the native title bar use the same transition.
            window.DispatcherQueue.TryEnqueue(() =>
            {
                if (sessionWindow == sender)
                    CompleteWindowMaximize();
            });
        }
    }

    private void RememberWindowSize(AppWindow window)
    {
        if (changingWindowLayout || windowLayout.FullScreen ||
            window.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored } ||
            viewport?.XamlRoot is not { } root)
            return;

        var size = window.Size;
        if (size.Width > 0 && size.Height > 0)
            windowLayout = windowLayout with
            {
                Width = size.Width / root.RasterizationScale,
                Height = size.Height / root.RasterizationScale
            };
    }

    private void CompleteWindowMaximize()
    {
        if (HasEnded || switchingMonitorMode || fullScreenWindow is not null || nativeFullScreenOwner is not null ||
            sessionWindow is not { Presenter: OverlappedPresenter { State: OverlappedPresenterState.Maximized } } window)
        {
            maximizeTimer.Stop();
            return;
        }

        if (FullScreenPanelDrag.IsPrimaryMouseButtonDown)
        {
            if (!maximizeTimer.IsRunning)
                maximizeTimer.Start();
            return;
        }

        maximizeTimer.Stop();
        EnterFullScreen(window, restoreOnExit: true);
    }

    private void OnToggleFullScreen(object? sender, EventArgs e)
    {
        if (getWindow()?.Handler?.PlatformView is not NativeWindow window)
            return;

        window.DispatcherQueue.TryEnqueue(() =>
        {
            if (HasEnded || switchingMonitorMode)
                return;
            if (fullScreenWindow is not null || nativeFullScreenOwner is not null)
                ReturnToWindow();
            else
                EnterFullScreen(window.AppWindow);
        });
    }

    private void EnterFullScreen(AppWindow window, bool restoreOnExit = false)
    {
        if (session is { } current)
        {
            if (profile.Display.UseMultimon && RdpSession.HasMultipleMonitors && !current.UsesMultipleMonitors)
            {
                _ = ChangeMonitorModeAsync(current, useMultipleMonitors: true, restoreWindow: restoreOnExit);
                return;
            }
            // With one monitor the existing host can fill the display without moving the
            // remote video to an ActiveX fullscreen HWND and back.
            if (current.UsesMultipleMonitors && TryEnterNativeFullScreen(current, window, restoreOnExit))
                return;
        }

        fullScreenWindow = window;
        restoreOnFullScreenExit = restoreOnExit;
        UpdateWindowModeLayout(() =>
        {
            // The remote-machine trace showed 12.7 s in SetPresenter(Default) plus
            // 8.1 s in OverlappedPresenter.Restore. Avoid both transitions for a separate
            // session window when the application itself runs over RDP.
            if (setEmbeddedFullScreen is null && BorderlessSessionWindow.IsRemoteSession &&
                window.Presenter is OverlappedPresenter)
                borderlessFullScreenWindow = BorderlessSessionWindow.TryEnter(
                    Microsoft.UI.Win32Interop.GetWindowFromWindowId(window.Id));
            if (borderlessFullScreenWindow is null)
                window.SetPresenter(AppWindowPresenterKind.FullScreen);
            windowLayout = windowLayout with { FullScreen = true };
            setEmbeddedFullScreen?.Invoke(true);
            ConnectionBar.IsVisible = false;
            Grid.SetRow(SessionArea, 0);
            Grid.SetRowSpan(SessionArea, 2);
        });
        if (getWindow()?.Handler?.PlatformView is NativeWindow nativeWindow)
        {
            fullScreenBar = new FullScreenSessionBar(WindowNative.GetWindowHandle(nativeWindow),
                nativeWindow.DispatcherQueue, profile.Id, SessionTitle, serverName, ReturnToWindow, CloseConnection,
                MinimizeHostedFullScreen, returnToMainWindow: setEmbeddedFullScreen is not null);
            panelTimer.Start();
        }
    }

    internal void UpdateServerName(string name)
    {
        serverName = name;
        ConnectionTitle.Text = SessionTitle;
        fullScreenBar?.UpdateTitle(SessionTitle, serverName);
        session?.UpdateNativeFullScreenTitle(SessionTitle, serverName);
    }

    private bool TryEnterNativeFullScreen(RdpSession current, AppWindow owner, bool restoreOnExit, bool attachOnly = false)
    {
        if (!current.TryEnterNativeFullScreen(profile.Id, () => (SessionTitle, serverName), ReturnToWindow, CloseConnection,
            MinimizeNativeFullScreen, attachOnly, returnToMainWindow: setEmbeddedFullScreen is not null))
            return false;

        nativeFullScreenOwner = owner;
        restoreOnFullScreenExit = restoreOnExit;
        windowLayout = windowLayout with { FullScreen = true };
        return true;
    }

    private void MinimizeNativeFullScreen()
    {
        if (setEmbeddedFullScreen is not null)
        {
            ReturnToWindow();
            return;
        }
        if (nativeFullScreenOwner is not { Presenter: OverlappedPresenter presenter } ||
            session?.TryLeaveNativeFullScreen() != true)
            return;

        nativeFullScreenOwner = null;
        MinimizeFullScreenWindow(presenter);
    }

    private void MinimizeHostedFullScreen()
    {
        if (setEmbeddedFullScreen is not null)
        {
            ReturnToWindow();
            return;
        }
        if (fullScreenWindow is not { } window)
            return;

        bool restoreOnExit = restoreOnFullScreenExit;
        ExitFullScreen();
        if (window.Presenter is not OverlappedPresenter presenter)
            return;

        restoreOnFullScreenExit = restoreOnExit;
        windowLayout = windowLayout with { FullScreen = true };
        MinimizeFullScreenWindow(presenter);
    }

    private void MinimizeFullScreenWindow(OverlappedPresenter presenter)
    {
        restoreFullScreenAfterMinimize = true;
        presenter.Minimize();
        lastWindowState = presenter.State;
    }

    private void ReturnToWindow(PointInt32? drop = null)
    {
        FullScreenTransitionTrace.EnsureStarted("Return to window");
        FullScreenTransitionTrace.Write($"ReturnToWindow begin hosted={fullScreenWindow is not null} " +
            $"borderless={borderlessFullScreenWindow is not null} native={nativeFullScreenOwner is not null} " +
            $"multimon={session?.UsesMultipleMonitors} dragging={drop.HasValue}");
        if (HasEnded || switchingMonitorMode || preparingNativeFullScreenExit)
        {
            FullScreenTransitionTrace.Write($"ReturnToWindow skipped ended={HasEnded} " +
                $"switchingMonitors={switchingMonitorMode} preparingExit={preparingNativeFullScreenExit}");
            return;
        }

        var currentSession = session;
        bool deferDisplayUpdate = drop.HasValue && currentSession is { UsesMultipleMonitors: false };
        if (deferDisplayUpdate)
            currentSession?.DeferWindowedDisplayUpdate(true);

        if (fullScreenWindow is not null)
        {
            FullScreenTransitionTrace.Write("Hosted fullscreen exit");
            currentSession?.TraceFullScreenExitWindows(hosted: true);
            ExitFullScreen(drop);
        }
        else if (session is { UsesMultipleMonitors: true } current)
        {
            FullScreenTransitionTrace.Write("Multi-monitor fullscreen exit");
            _ = ChangeMonitorModeAsync(current, useMultipleMonitors: false, drop: drop);
        }
        else if (nativeFullScreenOwner is { } owner && currentSession is not null)
        {
            currentSession.TraceFullScreenExitWindows();
            preparingNativeFullScreenExit = true;
            bool positioned = false;
            UpdateWindowModeLayout(() =>
            {
                // Restore the owner while the native fullscreen window still covers it.
                RestoreWindowPresenter(owner, drop.HasValue);
                positioned = drop is { } position &&
                    FullScreenPanelDrag.PositionUnderPointer(owner, position);
                if (!drop.HasValue)
                    FullScreenPanelDrag.EnsureWindowVisible(owner);
            });

            // Let WinUI lay out the restored window before ActiveX returns to its host.
            FullScreenTransitionTrace.Write("Queue native fullscreen exit after layout");
            if (viewport is not { } element ||
                !element.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FinishNativeExit))
                FinishNativeExit();

            void FinishNativeExit()
            {
                FullScreenTransitionTrace.Write("Run queued native fullscreen exit begin");
                try
                {
                    if (HasEnded || currentSession != session || nativeFullScreenOwner != owner)
                        return;

                    UpdateWindowModeLayout(() =>
                    {
                        if (viewport?.XamlRoot is { } root)
                        {
                            var bounds = GetSessionBounds(viewport, root);
                            if (bounds.Width > 0 && bounds.Height > 0)
                                currentSession.PrepareWindowedViewport(bounds);
                        }

                        if (currentSession.TryLeaveNativeFullScreen())
                        {
                            nativeFullScreenOwner = null;
                            CompleteWindowRestore(owner, drop, positioned);
                        }
                    }, refreshComposition: false);

                    if (nativeFullScreenOwner is null && setEmbeddedFullScreen is null)
                        RememberWindowSize(owner);
                    else if (nativeFullScreenOwner is not null && deferDisplayUpdate)
                        currentSession.DeferWindowedDisplayUpdate(false);
                }
                finally
                {
                    preparingNativeFullScreenExit = false;
                    FullScreenTransitionTrace.Write("Run queued native fullscreen exit end");
                }
            }
        }
        else if (deferDisplayUpdate)
        {
            currentSession?.DeferWindowedDisplayUpdate(false);
        }
        // Temporary probe: distinguish a blocked UI thread from starvation of the low-priority resize.
        if (viewport is { } currentViewport)
        {
            FullScreenTransitionTrace.Write("Queue normal-priority exit probe");
            currentViewport.DispatcherQueue.TryEnqueue(() =>
                FullScreenTransitionTrace.Write("Run normal-priority exit probe"));
        }
        FullScreenTransitionTrace.Write("ReturnToWindow end");
    }

    private async Task ChangeMonitorModeAsync(RdpSession current, bool useMultipleMonitors, bool restoreWindow = false,
        PointInt32? drop = null)
    {
        if (HasEnded || switchingMonitorMode || session != current)
            return;

        switchingMonitorMode = true;
        FullScreenTransitionTrace.Write($"Change monitor mode begin multimon={useMultipleMonitors}");
        try
        {
            string? password = null;
            FullScreenTransitionTrace.Write("Read saved credential begin");
            try { password = await readPassword(); }
            catch (Exception ex) when (ex is FormatException or UnauthorizedAccessException or COMException or ArgumentException)
            {
                // Let ActiveX prompt if the saved password cannot be read.
                System.Diagnostics.Debug.WriteLine(ex);
            }
            FullScreenTransitionTrace.Write("Read saved credential end");
            if (HasEnded || session != current)
                return;

            const string status = "Переподключение для смены режима экрана…";
            FullScreenTransitionTrace.Write("Report monitor reconnect status begin");
            viewModel.ReportDisconnected(status);
            onReconnectStatusChanged?.Invoke(status);
            FullScreenTransitionTrace.Write("Report monitor reconnect status end");
            if (restoreWindow && sessionWindow?.Presenter is OverlappedPresenter presenter)
                presenter.Restore();

            DetachSessionHandlers();
            session = null;
            // Release the fullscreen control instead of asking it to reparent a multi-monitor desktop.
            FullScreenTransitionTrace.Write("Dispose multi-monitor RDP host begin");
            current.Dispose(disconnect: false);
            FullScreenTransitionTrace.Write("Dispose multi-monitor RDP host end");
            await Task.Yield();
            if (!HasEnded)
            {
                if (!useMultipleMonitors && getWindow()?.Handler?.PlatformView is NativeWindow window)
                {
                    windowLayout = windowLayout with { FullScreen = false };
                    if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized } maximized)
                        maximized.Restore();

                    if (drop is { } position)
                        ContinueWindowDrag(window.AppWindow, position);
                    else
                        FullScreenPanelDrag.EnsureWindowVisible(window.AppWindow);
                }
                FullScreenTransitionTrace.Write("Start replacement RDP connection begin");
                Connect(password, useMultipleMonitors, enterFullScreen: useMultipleMonitors);
                FullScreenTransitionTrace.Write("Start replacement RDP connection end");
            }
        }
        catch (ExternalException ex)
        {
            if (!HasEnded)
                ReportFailure($"Не удалось сменить режим экрана (0x{ex.HResult:X8}). {ex.Message}");
        }
        finally
        {
            switchingMonitorMode = false;
            FullScreenTransitionTrace.Write("Change monitor mode end");
        }
    }

    private void ExitFullScreen(PointInt32? drop = null)
    {
        if (fullScreenWindow is not { } window)
            return;

        UpdateWindowModeLayout(() =>
        {
            StopFullScreenBar();
            bool nativeRestore = borderlessFullScreenWindow?.Restore(restoreOnFullScreenExit || drop.HasValue) == true;
            borderlessFullScreenWindow = null;
            if (!nativeRestore)
            {
                FullScreenTransitionTrace.Write("Set windowed presenter begin");
                window.SetPresenter(AppWindowPresenterKind.Default);
                FullScreenTransitionTrace.Write("Set windowed presenter end");
            }
            setEmbeddedFullScreen?.Invoke(false);
            fullScreenWindow = null;
            FullScreenTransitionTrace.Write("Set windowed layout begin");
            SetWindowedLayout();
            FullScreenTransitionTrace.Write("Set windowed layout end");
            if (!nativeRestore)
                RestoreWindowPresenter(window, drop.HasValue);
            else
                FullScreenTransitionTrace.Write($"Borderless restore presenter state={(window.Presenter as OverlappedPresenter)?.State}");
            bool positioned = drop is { } position &&
                FullScreenPanelDrag.PositionUnderPointer(window, position);
            if (!drop.HasValue)
            {
                FullScreenTransitionTrace.Write("Ensure restored window visible begin");
                FullScreenPanelDrag.EnsureWindowVisible(window);
                FullScreenTransitionTrace.Write("Ensure restored window visible end");
            }
            FullScreenTransitionTrace.MarkWindowed();
            CompleteWindowRestore(window, drop, positioned);
        }, refreshComposition: false);

        if (setEmbeddedFullScreen is null)
            RememberWindowSize(window);
    }

    private void RestoreWindowPresenter(AppWindow window, bool dragging)
    {
        if ((restoreOnFullScreenExit || dragging) &&
            window.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored } presenter)
        {
            FullScreenTransitionTrace.Write("Restore overlapped presenter begin");
            presenter.Restore();
            FullScreenTransitionTrace.Write("Restore overlapped presenter end");
        }
    }

    private void CompleteWindowRestore(AppWindow window, PointInt32? drop, bool positioned = false)
    {
        lastWindowState = (window.Presenter as OverlappedPresenter)?.State;
        restoreOnFullScreenExit = false;
        windowLayout = windowLayout with { FullScreen = false };
        if (drop is { } position)
        {
            var current = session;
            Dispatcher.Dispatch(() =>
            {
                FullScreenTransitionTrace.Write("Run queued pointer handoff");
                if (!HasEnded && sessionWindow == window && !windowLayout.FullScreen)
                    ContinueWindowDrag(window, position, positioned);
                else if (session == current && !HasEnded)
                    current?.DeferWindowedDisplayUpdate(false);
                FullScreenTransitionTrace.Write("Pointer handoff callback end");
            });
        }
    }

    private void UpdateWindowModeLayout(Action update, bool refreshComposition = true)
    {
        FullScreenTransitionTrace.Write("Update window mode layout begin");
        const uint transitionsForcedDisabled = 3;
        nint handle = getWindow()?.Handler?.PlatformView is NativeWindow window
            ? WindowNative.GetWindowHandle(window)
            : 0;
        FullScreenTransitionTrace.Write("Disable DWM transitions begin");
        bool transitionsSuppressed = handle != 0 &&
            DwmSetWindowAttribute(handle, transitionsForcedDisabled, 1, sizeof(int)) >= 0;
        FullScreenTransitionTrace.Write("Disable DWM transitions end");
        changingWindowLayout = true;
        try
        {
            update();
        }
        finally
        {
            changingWindowLayout = false;
            if (transitionsSuppressed)
            {
                FullScreenTransitionTrace.Write("Restore DWM transitions begin");
                DwmSetWindowAttribute(handle, transitionsForcedDisabled, 0, sizeof(int));
                FullScreenTransitionTrace.Write("Restore DWM transitions end");
            }
        }
        QueueSessionBoundsUpdate(refreshComposition);
        FullScreenTransitionTrace.Write("Update window mode layout end");
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, in int value, uint size);

    private void ContinueWindowDrag(AppWindow window, PointInt32 position, bool positioned = false)
    {
        var current = session;
        FullScreenPanelDrag.ContinueAtPointer(window, position, positioned, () =>
        {
            if (!HasEnded && session == current)
                current?.DeferWindowedDisplayUpdate(false);
        }, () =>
        {
            if (!HasEnded && !switchingMonitorMode && viewModel.IsConnected && sessionWindow == window)
                EnterFullScreen(window);
        });
    }

    private void SetWindowedLayout()
    {
        bool embedded = setEmbeddedFullScreen is not null;
        Grid.SetRowSpan(SessionArea, 1);
        Grid.SetRow(SessionArea, 1);
        ConnectionBar.IsVisible = embedded;
    }

    private void StopFullScreenBar()
    {
        FullScreenTransitionTrace.Write("Stop hosted fullscreen panel begin");
        panelTimer.Stop();
        fullScreenBar?.Dispose();
        fullScreenBar = null;
        FullScreenTransitionTrace.Write("Stop hosted fullscreen panel end");
    }

    private void QueueSessionBoundsUpdate(bool refreshComposition = false)
    {
        if (changingWindowLayout || nativeFullScreenOwner is not null || viewport is not { } element)
            return;

        refreshSessionComposition |= refreshComposition;
        if (sessionBoundsUpdateQueued)
            return;

        FullScreenTransitionTrace.Write("Queue session bounds update");
        sessionBoundsUpdateQueued = element.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            FullScreenTransitionTrace.Write("Run queued session bounds update begin");
            sessionBoundsUpdateQueued = false;
            bool refresh = refreshSessionComposition;
            refreshSessionComposition = false;
            if (viewport == element)
                UpdateSessionBounds(refresh);
            FullScreenTransitionTrace.Write("Run queued session bounds update end");
        });
    }

    private void UpdateSessionBounds(bool refreshComposition = false)
    {
        if (changingWindowLayout || nativeFullScreenOwner is not null || session is null || viewport?.XamlRoot is not { } root)
            return;

        var bounds = GetSessionBounds(viewport, root);
        if (bounds.Width > 0 && bounds.Height > 0)
            session.Resize(bounds, refreshComposition);
    }

    private static (int X, int Y, int Width, int Height) GetSessionBounds(
        NativeElement element, Microsoft.UI.Xaml.XamlRoot root)
    {
        // WinUI lays out in DIPs. The child HWND is positioned in client pixels.
        var origin = element.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point());
        double scale = root.RasterizationScale;
        int left = (int)Math.Round(origin.X * scale);
        int top = (int)Math.Round(origin.Y * scale);
        int right = (int)Math.Round((origin.X + element.ActualWidth) * scale);
        int bottom = (int)Math.Round((origin.Y + element.ActualHeight) * scale);
        return (left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private void ReportFailure(string message)
    {
        EndSession(message);
        viewModel.ReportDisconnected(message);
    }

    private void DetachSessionHandlers()
    {
        restoreFullScreenAfterMinimize = false;
        nativeFullScreenOwner = null;
        maximizeTimer.Stop();
        if (sessionWindow is not null)
        {
            sessionWindow.Changed -= OnSessionWindowChanged;
            sessionWindow.Closing -= OnSessionWindowClosing;
            sessionWindow = null;
            lastWindowState = null;
        }

        if (viewport is not null)
        {
            viewport.LayoutUpdated -= OnViewportLayoutUpdated;
            viewport = null;
        }
    }

    internal void EndSession(string? error = null, bool disconnect = true)
    {
        if (HasEnded)
            return;

        HasEnded = true;
        if (sessionWasConnected)
            SessionWindowSettings.Save(profile, windowLayout);
        var notifyEnded = onSessionEnded;
        onSessionEnded = null;
        DetachSessionHandlers();

        var previousSession = session;
        session = null;
        try
        {
            if (closingWindow && setEmbeddedFullScreen is null)
            {
                StopFullScreenBar();
                fullScreenWindow = null;
                borderlessFullScreenWindow = null;
            }
            else
            {
                ExitFullScreen();
            }

            notifyEnded?.Invoke(error);
        }
        finally
        {
            previousSession?.Dispose(disconnect: disconnect && !closingWindow);
        }
    }
}

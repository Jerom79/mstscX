using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using mstscX.Services;
using Windows.Graphics;

namespace mstscX.WinUI;

internal sealed class FullScreenSessionBar(nint owner, DispatcherQueue dispatcher, Guid profileId, string title, string serverName,
    Action<PointInt32?> exit, Action disconnect, Action? minimizeSession = null,
    bool returnToMainWindow = false) : IDisposable
{
    private const int ExitButton = 1;
    private const int DisconnectButton = 2;
    private const int GripButton = 3;
    private const int PinButton = 4;
    private const int MinimizeButton = 5;
    private const int CompactButton = 6;
    private const double CompactScale = 0.9;
    private const int HideDelayMilliseconds = 3_000;
    private const int SlideDurationMilliseconds = 170;
    private const uint NoActivateOrZOrder = 0x0014;
    private nint window;
    private nint surface;
    private nint grip;
    private nint background;
    private nint pin;
    private nint compactToggle, minimize, restore, close;
    private nint pressedBrush;
    private nint font;
    private nint iconFont;
    private nint monitor;
    private uint textColor, backgroundColor;
    private int width, height, left, top;
    private int expandedWidth, compactWidth, padding, buttonWidth;
    private int minimumLeft, maximumLeft, rightEdge, dragStartLeft;
    private double horizontalPosition = SessionWindowSettings.LoadPanelPosition(profileId);
    private bool positionChanged;
    private long? hideAt;
    private int titleInset;
    private int buttonInset, buttonCorner;
    private int cornerDiameter, shadowBlur, shadowOffset;
    private int hiddenPixels, slideFrom, slideTarget;
    private long slideStarted;
    private DispatcherQueueTimer? slideTimer;
    private readonly FullScreenPanelDrag drag = new();
    private SubclassProc? windowProcedure;
    private bool commandPending;
    private SessionPanelState panelState = SessionWindowSettings.LoadPanelState(profileId);
    private string sessionTitle = title;
    private string sessionServerName = serverName;
    private string PanelTitle => panelState.Compact ? sessionServerName : sessionTitle;
    private string PinLabel => panelState.Pinned ? "Открепить панель" : "Закрепить панель";
    private string CompactLabel => panelState.Compact ? "Расширить панель" : "Уменьшить панель";

    internal void Update()
    {
        if (drag.IsCaptured && GetCursorPos(out var captured))
        {
            hideAt = null;
            var point = new PointInt32(captured.X, captured.Y);
            if (!FullScreenPanelDrag.IsPrimaryMouseButtonDown)
                FinishDrag(point);
            else
                TrackDrag(point);
            return;
        }

        if (!IsWindowVisible(owner) || IsIconic(owner) ||
            !GetCursorPos(out var cursor) || !GetWindowRect(owner, out var desktop))
        {
            Hide();
            return;
        }

        nint currentMonitor = MonitorFromWindow(owner, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        int topEdge = desktop.Top;
        bool hasMonitor = currentMonitor != 0 && GetMonitorInfo(currentMonitor, ref info);
        bool atTop = hasMonitor &&
            desktop.Contains(cursor) && info.Bounds.Contains(cursor) &&
            cursor.Y < (topEdge = Math.Max(info.Bounds.Top, desktop.Top)) + 3;
        if (hasMonitor && !commandPending && (panelState.Pinned || (atTop && CanShowAtEdge(cursor, topEdge))))
        {
            hideAt = null;
            minimumLeft = Math.Max(desktop.Left, info.Bounds.Left);
            rightEdge = Math.Min(desktop.Right, info.Bounds.Right);
            int y = Math.Max(desktop.Top, info.Bounds.Top);
            if (window == 0 && !Create())
                return;

            maximumLeft = Math.Max(minimumLeft, rightEdge - width);
            bool visible = IsWindowVisible(window);
            int nextLeft = minimumLeft + (int)Math.Round(horizontalPosition * (maximumLeft - minimumLeft));
            if (panelState.Compact && ViewSettings.CompactPanelAtPointer)
            {
                if (atTop && (!visible || IsSlidingOut || currentMonitor != monitor))
                    nextLeft = Math.Clamp(cursor.X - width / 2, minimumLeft, maximumLeft);
                else if (visible && currentMonitor == monitor)
                    nextLeft = Math.Clamp(left, minimumLeft, maximumLeft);
                // Cursor-based reveals do not overwrite the saved position. Only
                // an explicit horizontal drag changes the per-server preference.
            }
            if (!visible || IsSlidingOut || currentMonitor != monitor || nextLeft != left || y != top)
            {
                left = nextLeft;
                top = y;
                monitor = currentMonitor;
                ShowPanel();
            }
            return;
        }

        if (window == 0 || !IsWindowVisible(window))
            return;

        if (cursor.X >= left && cursor.X < left + width && cursor.Y >= top && cursor.Y < top + height - hiddenPixels)
        {
            hideAt = null;
            if (IsSlidingOut)
                ShowPanel();
            return;
        }

        long now = Environment.TickCount64;
        hideAt ??= now + HideDelayMilliseconds;
        if (now >= hideAt.Value && !IsSlidingOut)
            StartSlide(show: false);
    }

    private bool CanShowAtEdge(Point cursor, int topEdge)
    {
        nint focused = GetForegroundWindow();
        if (focused == owner || (window != 0 && focused == window))
            return true;

        if (IsSessionWindowAt(cursor))
            return true;

        // Windows can reserve the first few pixels of a monitor for shell hit testing.
        cursor.Y = topEdge + 6;
        return IsSessionWindowAt(cursor);
    }

    private bool IsSessionWindowAt(Point cursor)
    {
        const uint rootWindow = 2;
        nint target = WindowFromPoint(cursor);
        if (target == 0)
            return false;

        nint root = GetAncestor(target, rootWindow);
        return root == owner || (window != 0 && root == window);
    }

    private bool Create()
    {
        const uint toolWindowWithoutActivation = 0x08000080;
        const uint popup = 0x82000000; // WS_POPUP | WS_CLIPCHILDREN
        // STATIC without SS_NOTIFY returns HTTRANSPARENT, allowing input routing (including
        // an outer RDP session) to skip this host. Only the decorative surface is click-through.
        const uint notifyMouseInput = 0x0100; // SS_NOTIFY
        window = CreateWindowEx(toolWindowWithoutActivation, "STATIC", "", popup | notifyMouseInput,
            0, 0, 0, 0, owner, 0, 0, 0);
        if (window == 0)
            return false;

        windowProcedure = HandleMessage;
        if (!SetWindowSubclass(window, windowProcedure, 1, 0))
        {
            Dispose();
            return false;
        }

        UpdateMetrics();
        var app = Microsoft.Maui.Controls.Application.Current;
        bool dark = app?.RequestedTheme == AppTheme.Dark;
        backgroundColor = dark ? 0x00202020u : 0x00F3F3F3u;
        background = CreateSolidBrush(backgroundColor);
        pressedBrush = CreateSolidBrush(dark ? 0x002F2F2Fu : 0x00E7E7E7u);
        textColor = dark ? 0x00FFFFFFu : 0x00202020u;
        width = GetPanelWidth();
        hiddenPixels = 0;
        CreateSurface(cornerDiameter, backgroundColor);

        nint Button(int id, string label) =>
            CreateWindowEx(0, "BUTTON", label, 0x4000000B, 0, 1, 0, height - 2, window, id, 0, 0);

        pin = Button(PinButton, PinLabel);
        compactToggle = Button(CompactButton, CompactLabel);
        grip = Button(GripButton, PanelTitle);
        minimize = Button(MinimizeButton, "Свернуть");
        restore = Button(ExitButton, "Выйти из полноэкранного режима");
        close = Button(DisconnectButton, "Отключиться");
        if (pin != 0 && compactToggle != 0 && minimize != 0 && restore != 0 && close != 0 &&
            grip != 0 && SetWindowSubclass(grip, windowProcedure, 1, 0))
        {
            LayoutControls();
            return true;
        }

        Dispose();
        return false;
    }

    private void UpdateMetrics()
    {
        uint dpi = GetDpiForWindow(owner);
        double dpiScale = (dpi == 0 ? 96 : dpi) / 96.0;
        double scale = dpiScale * (panelState.Compact ? CompactScale : 1);
        int Scale(int size) => Math.Max(1, (int)Math.Round(size * scale));

        padding = Scale(10);
        buttonWidth = Scale(panelState.Compact ? 24 : 48);
        buttonInset = Scale(3);
        buttonCorner = Scale(12);
        expandedWidth = (int)Math.Round(665 * dpiScale);
        height = Scale(32);
        cornerDiameter = Scale(16);
        shadowBlur = Scale(8);
        shadowOffset = Scale(2);

        if (font != 0) DeleteObject(font);
        if (iconFont != 0) DeleteObject(iconFont);
        font = CreateFont(-Scale(13), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        iconFont = CreateFont(-Scale(16), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe MDL2 Assets");
        UpdateCompactWidth();
    }

    private int GetPanelWidth() => panelState.Compact ? Math.Min(compactWidth, rightEdge - minimumLeft) : expandedWidth;

    private void UpdateCompactWidth() => compactWidth = 3 * padding + 4 * buttonWidth + MeasureTitle();

    private int MeasureTitle()
    {
        nint dc = CreateCompatibleDC(0);
        if (dc == 0)
            return expandedWidth / 4;

        nint previousFont = font != 0 ? SelectObject(dc, font) : 0;
        try
        {
            return GetTextExtentPoint32(dc, sessionServerName, sessionServerName.Length, out var size) ? size.Width : expandedWidth / 4;
        }
        finally
        {
            if (previousFont is not (0 or -1))
                SelectObject(dc, previousFont);
            DeleteDC(dc);
        }
    }

    private void LayoutControls()
    {
        int actionWidth = buttonWidth;
        int actionsStart = width - padding - (panelState.Compact ? 2 : 3) * actionWidth;
        int titleStart = padding + 2 * actionWidth;
        titleInset = panelState.Compact ? 0 : actionWidth;
        LayoutButton(pin, padding, actionWidth);
        LayoutButton(compactToggle, padding + actionWidth, actionWidth);
        LayoutButton(grip, titleStart, actionsStart - titleStart);
        if (panelState.Compact && returnToMainWindow)
            ShowWindow(minimize, 0);
        else
            LayoutButton(minimize, actionsStart, actionWidth);
        LayoutButton(close, width - padding - actionWidth, actionWidth);
        if (panelState.Compact)
        {
            if (returnToMainWindow)
            {
                SetWindowText(restore, "Вернуться в основное окно");
                LayoutButton(restore, actionsStart, actionWidth);
            }
            else
                ShowWindow(restore, 0);
        }
        else
        {
            LayoutButton(restore, actionsStart + actionWidth, actionWidth);
        }
    }

    private void LayoutButton(nint button, int x, int buttonSize) =>
        SetWindowPos(button, 0, x, 1, buttonSize, height - 2, NoActivateOrZOrder | 0x0040);

    private void ToggleCompact()
    {
        panelState = panelState with { Compact = !panelState.Compact };
        SessionWindowSettings.SavePanelState(profileId, panelState);
        StopSlide();
        hiddenPixels = 0;
        UpdateMetrics();
        SetWindowText(compactToggle, CompactLabel);
        SetWindowText(grip, PanelTitle);
        ResizePanel();
    }

    internal void UpdateTitle(string newTitle, string newServerName)
    {
        sessionTitle = newTitle;
        sessionServerName = newServerName;
        if (window == 0)
            return;

        SetWindowText(grip, PanelTitle);
        InvalidateRect(grip, 0, false);
        UpdateCompactWidth();
        if (panelState.Compact && width != GetPanelWidth())
            ResizePanel();
    }

    private void ResizePanel()
    {
        int center = left + width / 2;
        width = GetPanelWidth();
        maximumLeft = Math.Max(minimumLeft, rightEdge - width);
        left = panelState.Compact && ViewSettings.CompactPanelAtPointer && IsWindowVisible(window)
            ? Math.Clamp(center - width / 2, minimumLeft, maximumLeft)
            : minimumLeft + (int)Math.Round(horizontalPosition * (maximumLeft - minimumLeft));
        LayoutControls();
        if (surface != 0 && !DrawSurface(cornerDiameter, backgroundColor))
        {
            DestroyWindow(surface);
            surface = 0;
        }
        ClipPanel(hiddenPixels);
        if (IsWindowVisible(window))
            PositionPanel(hiddenPixels);
    }

    private nint HandleMessage(nint handle, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        const uint eraseBackground = 0x0014;
        const uint drawItem = 0x002B;
        const uint command = 0x0111;
        if (handle == window && message == 0x0021) // WM_MOUSEACTIVATE, also forwarded by child buttons
            return 3; // MA_NOACTIVATE: keep focus in the session without discarding the click.
        if (handle == window && message == 0x0082) // WM_NCDESTROY
            window = 0;
        if (handle == window && message == eraseBackground && GetClientRect(handle, out var rect))
        {
            FillRect(wParam, ref rect, background);
            return 1;
        }
        if (handle == window && message == 0x000F) // WM_PAINT
        {
            nint dc = BeginPaint(handle, out var paint);
            FillRect(dc, ref paint.Bounds, background);
            EndPaint(handle, ref paint);
            return 0;
        }
        if (handle == grip && message is 0x0201 or 0x0203 && !commandPending)
        {
            if (slideTimer is not null)
            {
                StopSlide();
                PositionPanel(0);
            }
            dragStartLeft = left;
            drag.Begin(GetMouseMessagePosition());
            SetCapture(handle);
            return 0;
        }
        if (handle == grip && message == 0x0200 && drag.IsCaptured) // WM_MOUSEMOVE
        {
            TrackDrag(GetMouseMessagePosition());
            return 0;
        }
        if (handle == grip && message == 0x0202 && drag.IsCaptured)
        {
            FinishDrag(GetMouseMessagePosition());
            return 0;
        }
        if (message == drawItem && wParam is ExitButton or DisconnectButton or GripButton or PinButton or MinimizeButton or CompactButton)
        {
            var item = Marshal.PtrToStructure<DrawItem>(lParam);
            bool highlighted = (item.State & 1) != 0 || (wParam == PinButton && panelState.Pinned);
            FillRect(item.Dc, ref item.Bounds, background);
            if (highlighted)
                DrawButtonHighlight(item.Dc, item.Bounds);
            if (wParam == GripButton)
            {
                item.Bounds.Left += titleInset;
                DrawLabel(item.Dc, font, PanelTitle, ref item.Bounds);
                return 1;
            }
            if (wParam == CompactButton)
            {
                // Use the two horizontal Segoe MDL2 chevrons as a single resize icon.
                int offset = Math.Max(1, (item.Bounds.Bottom - item.Bounds.Top) / 4);
                var leftGlyph = item.Bounds;
                var rightGlyph = item.Bounds;
                leftGlyph.Left -= offset;
                leftGlyph.Right -= offset;
                rightGlyph.Left += offset;
                rightGlyph.Right += offset;
                DrawLabel(item.Dc, iconFont, panelState.Compact ? "\uE76B" : "\uE76C", ref leftGlyph);
                DrawLabel(item.Dc, iconFont, panelState.Compact ? "\uE76C" : "\uE76B", ref rightGlyph);
                return 1;
            }
            string icon = wParam switch
            {
                PinButton => panelState.Pinned ? "\uE718" : "\uE77A",
                MinimizeButton => "\uE921",
                ExitButton => "\uE923",
                _ => "\uE8BB"
            };
            DrawLabel(item.Dc, iconFont, icon, ref item.Bounds);
            return 1;
        }
        if (message == command && lParam == pin && wParam == PinButton)
        {
            panelState = panelState with { Pinned = !panelState.Pinned };
            SessionWindowSettings.SavePanelState(profileId, panelState);
            SetWindowText(pin, PinLabel);
            InvalidateRect(pin, 0, false);
            return 0;
        }
        if (message == command && lParam == compactToggle && wParam == CompactButton)
        {
            ToggleCompact();
            return 0;
        }
        if (message == command && lParam != 0 && wParam is ExitButton or DisconnectButton or MinimizeButton)
        {
            if (!commandPending)
            {
                FullScreenTransitionTrace.Begin($"Panel command {wParam}", capturePaint: false);
                FullScreenTransitionTrace.Write("Queue panel command");
                commandPending = dispatcher.TryEnqueue(() =>
                {
                    FullScreenTransitionTrace.Write("Run queued panel command begin");
                    if (window != 0)
                    {
                        if (wParam == ExitButton)
                            exit(null);
                        else if (wParam == DisconnectButton)
                            disconnect();
                        else
                        {
                            if (minimizeSession is null)
                                ShowWindow(owner, 6); // SW_MINIMIZE
                            else
                                minimizeSession();
                            commandPending = false;
                        }
                    }
                    commandPending = false; // A disconnect confirmation may have been cancelled.
                    FullScreenTransitionTrace.Write("Run queued panel command end");
                });
                if (commandPending)
                {
                    FullScreenTransitionTrace.Write("Hide command panel begin");
                    Hide();
                    FullScreenTransitionTrace.Write("Hide command panel end");
                }
                else
                    FullScreenTransitionTrace.Write("Panel command queue unavailable");
            }
            return 0;
        }
        return DefSubclassProc(handle, message, wParam, lParam);
    }

    private static PointInt32 GetMouseMessagePosition()
    {
        uint position = GetMessagePos();
        return new PointInt32(unchecked((short)position), unchecked((short)(position >> 16)));
    }

    private void TrackDrag(PointInt32 point)
    {
        if (drag.TryLeaveTopEdge(point, top + height))
        {
            CompleteDrag(leave: true, point);
            return;
        }

        if (maximumLeft > minimumLeft && drag.TryGetHorizontalOffset(point, out int offset))
        {
            int nextLeft = Math.Clamp(dragStartLeft + offset, minimumLeft, maximumLeft);
            if (nextLeft == left)
                return;

            left = nextLeft;
            horizontalPosition = (double)(left - minimumLeft) / (maximumLeft - minimumLeft);
            positionChanged = true;
            ShowPanel();
        }
    }

    private void FinishDrag(PointInt32 point)
    {
        bool leave = drag.Finish(point, top + height, out var drop);
        CompleteDrag(leave, drop);
    }

    private void CompleteDrag(bool leave, PointInt32? drop)
    {
        if (leave)
            FullScreenTransitionTrace.Begin(drop.HasValue ? "Drag exit" : "Panel double-click exit",
                capturePaint: drop.HasValue);

        FullScreenTransitionTrace.Write("Capture release begin");
        if (GetCapture() == grip)
            ReleaseCapture();
        FullScreenTransitionTrace.Write("Capture release end");
        FullScreenTransitionTrace.Write("Save panel position begin");
        SavePosition();
        FullScreenTransitionTrace.Write("Save panel position end");
        if (leave && !commandPending)
        {
            FullScreenTransitionTrace.Write("Queue drag exit");
            commandPending = dispatcher.TryEnqueue(() =>
            {
                FullScreenTransitionTrace.Write("Run queued drag exit");
                if (window != 0)
                    exit(drop);
            });
            if (commandPending)
            {
                FullScreenTransitionTrace.Write("Hide panel begin");
                Hide();
                FullScreenTransitionTrace.Write("Hide panel end");
            }
        }
    }

    private void DrawLabel(nint dc, nint labelFont, string label, ref Rect bounds)
    {
        var previousFont = labelFont != 0 ? SelectObject(dc, labelFont) : 0;
        SetBkMode(dc, 1);
        SetTextColor(dc, textColor);
        DrawText(dc, label, -1, ref bounds, 0x8825);
        if (previousFont != 0)
            SelectObject(dc, previousFont);
    }

    private void DrawButtonHighlight(nint dc, Rect bounds)
    {
        nint highlight = CreateRoundRectRgn(bounds.Left + buttonInset, bounds.Top + buttonInset,
            bounds.Right - buttonInset, bounds.Bottom - buttonInset, buttonCorner, buttonCorner);
        if (highlight == 0)
            return;

        FillRgn(dc, highlight, pressedBrush);
        DeleteObject(highlight);
    }

    private void Hide()
    {
        StopSlide();
        hideAt = null;
        if (window != 0 && IsWindowVisible(window))
            ShowWindow(window, 0);
        if (surface != 0 && IsWindowVisible(surface))
            ShowWindow(surface, 0);
    }

    private void ShowPanel()
    {
        if (IsWindowVisible(window))
        {
            PositionPanel(hiddenPixels);
            if (IsSlidingOut)
                StartSlide(show: true);
            return;
        }

        StartSlide(show: true);
    }

    private bool IsSlidingOut => slideTimer is not null && slideTarget > 0;

    private void StartSlide(bool show)
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            if (show)
                PositionPanel(0);
            else
                Hide();
            return;
        }

        if (show && !IsWindowVisible(window))
            PositionPanel(height - 1);

        slideFrom = hiddenPixels;
        slideTarget = show ? 0 : height - 1;
        slideStarted = Environment.TickCount64;
        if (slideTimer is null)
        {
            slideTimer = dispatcher.CreateTimer();
            slideTimer.Interval = TimeSpan.FromMilliseconds(16);
            slideTimer.Tick += OnSlideTick;
            slideTimer.Start();
        }
    }

    private void OnSlideTick(DispatcherQueueTimer timer, object args)
    {
        if (slideTimer != timer)
            return;

        if (window == 0)
        {
            StopSlide();
            return;
        }

        double progress = Math.Min(1, (Environment.TickCount64 - slideStarted) / (double)SlideDurationMilliseconds);
        if (progress >= 1)
        {
            if (slideTarget > 0)
                Hide();
            else
            {
                StopSlide();
                PositionPanel(0);
            }
            return;
        }

        double eased = slideTarget == 0 ? 1 - Math.Pow(1 - progress, 3) : progress * progress;
        PositionPanel(slideFrom + (int)Math.Round((slideTarget - slideFrom) * eased));
    }

    private void StopSlide()
    {
        if (slideTimer is not { } timer)
            return;

        timer.Stop();
        timer.Tick -= OnSlideTick;
        slideTimer = null;
    }

    private void PositionPanel(int offset)
    {
        const uint showWindow = 0x0040;
        const uint noActivate = 0x0010;
        if (offset > hiddenPixels)
            ClipPanel(offset);

        SetWindowPos(window, 0, left, top - offset, width, height, NoActivateOrZOrder | showWindow);
        if (surface != 0)
            SetWindowPos(surface, window, left - shadowBlur, top - offset, SurfaceWidth, SurfaceHeight, noActivate | showWindow);

        if (offset < hiddenPixels)
            ClipPanel(offset);
        hiddenPixels = offset;
    }

    private void ClipPanel(int offset)
    {
        if (surface != 0)
        {
            int inset = (cornerDiameter + 1) / 2;
            SetPanelRegion(window, CreateRectRgn(inset, offset, width - inset, height));
            if (offset == 0)
                SetWindowRgn(surface, 0, true);
            else
                SetPanelRegion(surface, CreateRectRgn(0, offset, SurfaceWidth, SurfaceHeight));
            return;
        }

        // Extend the rounded region above the window so only its bottom corners remain visible.
        nint rounded = CreateRoundRectRgn(0, -cornerDiameter, width, height, cornerDiameter, cornerDiameter);
        if (rounded != 0)
        {
            nint visible = CreateRectRgn(0, offset, width, height);
            if (visible != 0)
            {
                CombineRgn(rounded, rounded, visible, 1); // RGN_AND
                DeleteObject(visible);
            }
        }
        SetPanelRegion(window, rounded);
    }

    private static void SetPanelRegion(nint handle, nint region)
    {
        if (region != 0 && SetWindowRgn(handle, region, true) == 0)
            DeleteObject(region);
    }

    private void CreateSurface(int diameter, uint color)
    {
        const uint layeredMouseTransparentToolWindow = 0x080800A0;
        const uint popup = 0x80000000;
        surface = CreateWindowEx(layeredMouseTransparentToolWindow, "STATIC", "", popup,
            0, 0, SurfaceWidth, SurfaceHeight, owner, 0, 0, 0);
        int inset = (diameter + 1) / 2;
        if (surface != 0 && DrawSurface(diameter, color))
        {
            // The layered background supplies both corners. The input window is opaque only in the straight middle.
            nint interior = CreateRectRgn(inset, 0, width - inset, height);
            if (interior != 0 && SetWindowRgn(window, interior, false) != 0)
                return;
            if (interior != 0)
                DeleteObject(interior);
        }

        if (surface != 0)
        {
            DestroyWindow(surface);
            surface = 0;
        }
        ClipPanel(0);
    }

    private int SurfaceWidth => width + 2 * shadowBlur;
    private int SurfaceHeight => height + shadowOffset + shadowBlur;

    private bool DrawSurface(int diameter, uint color)
    {
        nint dc = CreateCompatibleDC(0);
        if (dc == 0)
            return false;

        nint bitmap = 0;
        nint previous = 0;
        try
        {
            var header = new BitmapHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapHeader>(),
                Width = SurfaceWidth, Height = -SurfaceHeight, Planes = 1, BitsPerPixel = 32
            };
            bitmap = CreateDIBSection(dc, in header, 0, out nint pixels, 0, 0);
            if (bitmap == 0 || pixels == 0)
                return false;

            previous = SelectObject(dc, bitmap);
            if (previous is 0 or -1)
                return false;

            int stride = SurfaceWidth;
            int[] data = new int[stride * SurfaceHeight];
            int opaque = Premultiply(color, 255);
            double radius = diameter / 2.0;
            for (int y = 0; y < SurfaceHeight; ++y)
            {
                if (y < height)
                    Array.Fill(data, opaque, y * stride + shadowBlur, width);
                for (int x = 0; x < shadowBlur; ++x)
                {
                    int alpha = ShadowAlpha(x - shadowBlur + 0.5, y + 0.5, radius);
                    data[y * stride + x] = data[y * stride + stride - 1 - x] = alpha << 24;
                }
                if (y >= height)
                {
                    for (int x = 0; x < width; ++x)
                        data[y * stride + shadowBlur + x] = ShadowAlpha(x + 0.5, y + 0.5, radius) << 24;
                }
            }
            int corner = (diameter + 1) / 2;
            const int samples = 8;
            for (int y = 0; y < corner; y++)
            for (int x = 0; x < corner; x++)
            {
                int inside = 0;
                for (int dy = 0; dy < samples; dy++)
                for (int dx = 0; dx < samples; dx++)
                {
                    double horizontal = radius - x - (dx + 0.5) / samples;
                    double vertical = radius - y - (dy + 0.5) / samples;
                    if (horizontal * horizontal + vertical * vertical <= radius * radius)
                        inside++;
                }

                int opacity = (inside * 255 + samples * samples / 2) / (samples * samples);
                int shadow = ShadowAlpha(x + 0.5, height - y - 0.5, radius);
                int pixel = Premultiply(color, opacity) | ((shadow * (255 - opacity) / 255 + opacity) << 24);
                int oppositeRow = (height - 1 - y) * stride + shadowBlur;
                data[oppositeRow + x] = data[oppositeRow + width - 1 - x] = pixel;
            }
            Marshal.Copy(data, 0, pixels, data.Length);

            var size = new SizeInt32(SurfaceWidth, SurfaceHeight);
            var source = new Point();
            var blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = 1 };
            return UpdateLayeredWindow(surface, 0, 0, in size, dc, in source, 0, in blend, 2);
        }
        finally
        {
            if (previous is not (0 or -1))
                SelectObject(dc, previous);
            if (bitmap != 0)
                DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    private int ShadowAlpha(double x, double y, double radius)
    {
        // The lower corners of the shadow follow the lower corners of the panel.
        double horizontal = Math.Max(0, Math.Max(radius - x, x - (width - radius)));
        double vertical = Math.Max(0, y - (height - radius + shadowOffset));
        double distance = Math.Max(0, Math.Sqrt(horizontal * horizontal + vertical * vertical) - radius) / shadowBlur;
        return (int)Math.Round(105 * Math.Exp(-4.5 * distance * distance));
    }

    private static int Premultiply(uint color, int alpha)
    {
        int red = (int)(color & 255) * alpha / 255;
        int green = (int)((color >> 8) & 255) * alpha / 255;
        int blue = (int)((color >> 16) & 255) * alpha / 255;
        return (alpha << 24) | (red << 16) | (green << 8) | blue;
    }

    internal nint Owner => owner;

    private void SavePosition()
    {
        if (!positionChanged)
            return;

        SessionWindowSettings.SavePanelPosition(profileId, horizontalPosition);
        positionChanged = false;
    }

    public void Dispose()
    {
        FullScreenTransitionTrace.Write("Panel dispose begin");
        StopSlide();
        FullScreenTransitionTrace.Write("Panel save position begin");
        SavePosition();
        FullScreenTransitionTrace.Write("Panel save position end");
        drag.Cancel();
        if (window != 0)
        {
            FullScreenTransitionTrace.Write("Destroy panel input window begin");
            DestroyWindow(window);
            FullScreenTransitionTrace.Write("Destroy panel input window end");
            window = 0;
        }
        if (surface != 0)
        {
            FullScreenTransitionTrace.Write("Destroy panel layered surface begin");
            DestroyWindow(surface);
            FullScreenTransitionTrace.Write("Destroy panel layered surface end");
            surface = 0;
        }
        if (font != 0) DeleteObject(font);
        if (background != 0) DeleteObject(background);
        if (pressedBrush != 0) DeleteObject(pressedBrush);
        if (iconFont != 0) DeleteObject(iconFont);
        font = iconFont = background = pressedBrush = 0;
        grip = pin = compactToggle = minimize = restore = close = 0;
        windowProcedure = null;
        FullScreenTransitionTrace.Write("Panel dispose end");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly bool Contains(Point point) =>
            point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Bounds, Work; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitsPerPixel;
        public uint Compression, ImageSize;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ImportantColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte Operation, Flags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DrawItem
    {
        public uint Type, Id, Item, Action, State;
        public nint Window, Dc;
        public Rect Bounds;
        public nuint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Paint
    {
        public nint Dc;
        public int Erase;
        public Rect Bounds;
        public int Restore, IncUpdate;
        private long reserved1, reserved2, reserved3, reserved4;
    }

    private delegate nint SubclassProc(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern uint GetMessagePos();
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref Rect rect, nint brush);
    [DllImport("user32.dll")] private static extern nint BeginPaint(nint window, out Paint paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(nint window, ref Paint paint);
    [DllImport("user32.dll", EntryPoint = "DrawTextW", CharSet = CharSet.Unicode)] private static extern int DrawText(nint dc, string text, int length, ref Rect rect, uint format);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint handle);
    [DllImport("gdi32.dll", EntryPoint = "GetTextExtentPoint32W", CharSet = CharSet.Unicode)]
    private static extern bool GetTextExtentPoint32(nint dc, string text, int length, out SizeInt32 size);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(nint destination, nint first, nint second, int mode);
    [DllImport("gdi32.dll")] private static extern bool FillRgn(nint dc, nint region, nint brush);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, in BitmapHeader header, uint usage, out nint bits, nint section, uint offset);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(nint window, nint screenDc, nint position,
        in SizeInt32 size, nint sourceDc, in Point source, uint key, in BlendFunction blend, uint flags);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint window, nint rect, bool erase);
    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(nint window, string text);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll", EntryPoint = "CreateFontW", CharSet = CharSet.Unicode)] private static extern nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
}

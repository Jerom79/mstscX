using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Border = Microsoft.UI.Xaml.Controls.Border;
using MenuBar = Microsoft.UI.Xaml.Controls.MenuBar;
using Thickness = Microsoft.UI.Xaml.Thickness;
using Brush = Microsoft.UI.Xaml.Media.Brush;
using GridLength = Microsoft.UI.Xaml.GridLength;
using GridUnitType = Microsoft.UI.Xaml.GridUnitType;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace mstscX.WinUI;

// An owned top-level window avoids the main XAML bridge region cut out for native RDP.
internal sealed class MainFullScreenMenu : IDisposable
{
    private const double PanelHeight = 32;
    private readonly Action releaseMenus;
    private bool disposed, closed;
    private readonly MenuBar menu = new();
    private readonly Microsoft.UI.Xaml.Window overlay;
    private readonly nint overlayHandle;
    private readonly SubclassProc frameProcedure;
    private bool visible;
    internal bool OwnsWindow(nint handle) => handle == overlayHandle;
    private readonly Border panel;
    private readonly Border separator;
    private readonly DispatcherQueueTimer timer;
    private readonly DispatcherQueueTimer slideTimer;
    private double shownFraction, slideFrom, slideTarget;
    private long slideStarted;
    private readonly nint window;
    private readonly Func<bool> isFullScreen;
    private DateTime lastPointerInside;
    private bool dragging;

    internal MainFullScreenMenu(Microsoft.UI.Xaml.Controls.MenuBarItem[] items, Action releaseMenus,
        DispatcherQueue dispatcher, nint window, Func<bool> isFullScreen,
        Action toggleNavigation, Func<bool> isNavigationCollapsed,
        Action exitFullScreen, Action maximize, Action close, Action<Windows.Graphics.PointInt32> dragOut)
    {
        this.releaseMenus = releaseMenus;
        this.window = window;
        this.isFullScreen = isFullScreen;
        foreach (var item in items)
        {
            item.Height = PanelHeight;
            item.MinHeight = 0;
            item.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center;
            item.VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center;
            item.Padding = new Thickness(12, 0, 12, 0);
            menu.Items.Add(item);
        }
        var toggle = TitleNavigationButton.Create(toggleNavigation, isNavigationCollapsed);
        var contents = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
        };
        contents.Children.Add(toggle);
        contents.Children.Add(menu);
        var controls = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
        };
        controls.Children.Add(CreateButton("\uE73F", global::mstscX.Services.L.T("Выйти из полноэкранного режима"), exitFullScreen));
        controls.Children.Add(CreateButton("\uE921", global::mstscX.Services.L.T("Свернуть"), () => ShowWindow(window, 6)));
        controls.Children.Add(CreateButton("\uE922", global::mstscX.Services.L.T("Развернуть окно"), maximize));
        controls.Children.Add(CreateButton("\uE8BB", global::mstscX.Services.L.T("Закрыть"), close));
        var layout = new Microsoft.UI.Xaml.Controls.Grid();
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var title = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = "mstscX",
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(12, 0, 12, 0)
        };
        var grip = new Microsoft.UI.Xaml.Controls.Grid
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
        };
        grip.Children.Add(title);
        double dragStartY = 0;
        grip.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(panel);
            if (!point.Properties.IsLeftButtonPressed) return;
            dragging = grip.CapturePointer(e.Pointer);
            dragStartY = point.Position.Y;
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var point = e.GetCurrentPoint(panel);
            if (!point.Properties.IsLeftButtonPressed) { grip.ReleasePointerCaptures(); dragging = false; return; }
            if (point.Position.Y < panel.ActualHeight || point.Position.Y - dragStartY < 4) return;
            dragging = false;
            grip.ReleasePointerCaptures();
            if (GetCursorPos(out var cursor))
            {
                var drop = new Windows.Graphics.PointInt32(cursor.X, cursor.Y);
                overlay.DispatcherQueue.TryEnqueue(() => { if (!disposed && !closed) dragOut(drop); });
            }
            e.Handled = true;
        };
        grip.PointerReleased += (_, _) => { dragging = false; grip.ReleasePointerCaptures(); };
        grip.PointerCaptureLost += (_, _) => dragging = false;
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(grip, 1);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(controls, 2);
        layout.Children.Add(contents);
        layout.Children.Add(grip);
        layout.Children.Add(controls);
        separator = new Border
        {
            Height = 1,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Bottom,
            IsHitTestVisible = false
        };
        Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(separator, 3);
        layout.Children.Add(separator);
        menu.Height = PanelHeight;
        menu.MinHeight = 0;
        menu.VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center;
        menu.Padding = new Thickness(0);
        panel = new Border
        {
            Child = layout, Padding = new Thickness(8, 0, 0, 0),
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch
        };
        panel.Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        overlay = new Microsoft.UI.Xaml.Window { Content = panel };
        overlayHandle = WinRT.Interop.WindowNative.GetWindowHandle(overlay);
        frameProcedure = (handle, message, wParam, lParam, id, data) =>
        {
            if (message == 0x0083) return 0; // WM_NCCALCSIZE: whole window is client area.
            if (message == 0x0082) RemoveWindowSubclass(handle, frameProcedure!, id);
            return DefSubclassProc(handle, message, wParam, lParam);
        };
        SetWindowSubclass(overlayHandle, frameProcedure, 1, 0);
        SetWindowLongPtr(overlayHandle, -8, window);
        var presenter = Microsoft.UI.Windowing.OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        overlay.AppWindow.SetPresenter(presenter);
        presenter.SetBorderAndTitleBar(false, false);
        // Suppress the native bright outline; draw only our themed separator.
        uint noBorder = 0xFFFFFFFE;
        DwmSetWindowAttribute(overlayHandle, 34, ref noBorder, sizeof(uint));
        long style = GetWindowLongPtr(overlayHandle, -16).ToInt64();
        SetWindowLongPtr(overlayHandle, -16, (nint)(style & ~0x00C40000L));
        // WinUI's overlay can retain the classic sunken client edge even when
        // the presenter and DWM border are disabled (also visible on Windows 10).
        long extendedStyle = GetWindowLongPtr(overlayHandle, -20).ToInt64();
        SetWindowLongPtr(overlayHandle, -20, (nint)(extendedStyle & ~0x00020301L));
        SetWindowPos(overlayHandle, 0, 0, 0, 0, 0, 0x0037);
        overlay.AppWindow.IsShownInSwitchers = false;
        PositionPanel();
        overlay.Closed += (_, _) => closed = true;
        slideTimer = dispatcher.CreateTimer();
        slideTimer.Interval = TimeSpan.FromMilliseconds(16);
        slideTimer.Tick += OnSlideTick;
        timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        timer.Tick += OnTick;
        timer.Start();
    }

    private static Microsoft.UI.Xaml.Controls.Button CreateButton(string glyph, string hint, Action action)
    {
        var button = new Microsoft.UI.Xaml.Controls.Button
        {
            Width = 44, Height = 32,
            Content = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = glyph, FontSize = 14 }
        };
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, hint);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, hint);
        button.Click += (_, _) => action();
        return button;
    }

    private void SetVisible(bool show, bool animate = true)
    {
        if (closed || (visible == show && animate)) return;
        visible = show;
        slideTimer.Stop();
        slideFrom = shownFraction;
        slideTarget = show ? 1 : 0;
        if (animate && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            if (show)
            {
                PositionPanel();
                overlay.AppWindow.Show(false);
            }
            slideStarted = Environment.TickCount64;
            slideTimer.Start();
            return;
        }
        shownFraction = slideTarget;
        FinishVisibility(show);
    }

    private void OnSlideTick(DispatcherQueueTimer sender, object args)
    {
        if (disposed || closed) { slideTimer.Stop(); return; }
        double progress = Math.Min(1, (Environment.TickCount64 - slideStarted) / 170d);
        double eased = slideTarget == 1 ? 1 - Math.Pow(1 - progress, 3) : progress * progress;
        shownFraction = slideFrom + (slideTarget - slideFrom) * eased;
        PositionPanel();
        if (progress >= 1)
        {
            slideTimer.Stop();
            FinishVisibility(visible);
        }
    }

    private void FinishVisibility(bool show)
    {
        if (show)
        {
            PositionPanel();
            overlay.AppWindow.Show(false);
        }
        else
        {
            bool focused = GetForegroundWindow() == overlayHandle;
            overlay.AppWindow.Hide();
            if (focused) SetForegroundWindow(window);
        }
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (disposed || closed) { timer.Stop(); return; }
        nint foreground = GetForegroundWindow();
        if (!isFullScreen() || IsIconic(window) ||
            (foreground != window && foreground != overlayHandle) ||
            !GetCursorPos(out var point) || !ScreenToClient(window, ref point))
        {
            SetVisible(false, animate: false);
            return;
        }
        double scale = Math.Max(96, GetDpiForWindow(window)) / 96d;
        GetClientRect(window, out var bounds);
        int height = (int)Math.Ceiling(PanelHeight * scale);
        PositionPanel();
        if (dragging) { lastPointerInside = DateTime.UtcNow; return; }
        bool dark = Microsoft.Maui.Controls.Application.Current?.RequestedTheme == AppTheme.Dark;
        var theme = dark ? ElementTheme.Dark : ElementTheme.Light;
        if (panel.RequestedTheme != theme)
        {
            separator.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(dark
                ? Windows.UI.Color.FromArgb(255, 74, 80, 91)
                : Windows.UI.Color.FromArgb(255, 213, 217, 226));
            panel.RequestedTheme = theme;
            panel.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(dark
                ? Windows.UI.Color.FromArgb(255, 20, 27, 37) : Windows.UI.Color.FromArgb(255, 248, 250, 253));
        }
        bool inside = point.X >= 0 && point.X < bounds.Right && point.Y >= 0 &&
            point.Y <= (visible ? height : 3 * scale);
        if (inside)
        {
            lastPointerInside = DateTime.UtcNow;
            SetVisible(true);
        }
        else if (visible)
        {
            bool flyoutOpen = panel.XamlRoot is { } menuRoot &&
                VisualTreeHelper.GetOpenPopupsForXamlRoot(menuRoot).Any(candidate => candidate.IsOpen);
            if (flyoutOpen) lastPointerInside = DateTime.UtcNow;
            else if (DateTime.UtcNow - lastPointerInside > TimeSpan.FromMilliseconds(1_500))
                SetVisible(false);
        }
    }

    private void PositionPanel()
    {
        if (!GetClientRect(window, out var bounds) || bounds.Right <= 0) return;
        var origin = new Point();
        if (!ClientToScreen(window, ref origin)) return;
        // Never derive the HWND height from its stretched XAML content: that
        // feeds the initial/default window height back into subsequent layouts.
        double scale = Math.Max(96, GetDpiForWindow(window)) / 96d;
        int height = (int)Math.Ceiling(PanelHeight * scale);
        int actualHeight = Math.Max(height, overlay.AppWindow.Size.Height);
        int hidden = (int)Math.Round(actualHeight * (1 - shownFraction));
        // Clip the hidden part so it cannot appear on a monitor above this one.
        nint region = CreateRectRgn(0, hidden, bounds.Right, actualHeight);
        if (SetWindowRgn(overlayHandle, region, true) == 0) DeleteObject(region);
        var target = new Windows.Graphics.RectInt32(origin.X, origin.Y - hidden, bounds.Right, height);
        if (overlay.AppWindow.Position.X != target.X || overlay.AppWindow.Position.Y != target.Y ||
            overlay.AppWindow.Size.Width != target.Width || overlay.AppWindow.Size.Height != target.Height)
            // AppWindow applies an overlapped-window minimum height, leaving an
            // empty strip below this 32-DIP borderless menu. Size its HWND directly.
            SetWindowPos(overlayHandle, 0, target.X, target.Y, target.Width, target.Height, 0x0014);
    }

    public void Dispose()
    {
        if (disposed) return;
        Release();
        // Do not tear down a flyout's visual tree from its own Click callback.
        overlay.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!closed) overlay.Close();
        });
    }

    internal void CloseBeforeOwner()
    {
        Release();
        if (!closed) overlay.Close();
    }

    private void Release()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        timer.Tick -= OnTick;
        slideTimer.Stop();
        slideTimer.Tick -= OnSlideTick;
        releaseMenus();
        SetVisible(false, animate: false);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref uint value, int size);
    private delegate nint SubclassProc(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint window, ref Point point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
}

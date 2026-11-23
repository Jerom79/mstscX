using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Grid = Microsoft.UI.Xaml.Controls.Grid;
using Button = Microsoft.UI.Xaml.Controls.Button;
using Thickness = Microsoft.UI.Xaml.Thickness;
using SolidColorBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;
using KeyboardAccelerator = Microsoft.UI.Xaml.Input.KeyboardAccelerator;

namespace mstscX.WinUI;

internal static class ConnectionCloseConfirmation
{
    private const string Key = "Connections.WarnBeforeClosing";
    private static bool showing;
    internal static bool Enabled
    {
        get => Preferences.Default.Get(Key, true);
        set => Preferences.Default.Set(Key, value);
    }

    internal static async Task<bool> ConfirmAsync(nint owner, string serverName)
    {
        if (!Enabled) return true;
        if (showing) return false;
        showing = true;
        Microsoft.UI.Xaml.Window? dialog = null;
        bool ownerDisabled = false;
        bool dialogClosed = false;
        EventHandler<AppThemeChangedEventArgs>? themeChanged = null;
        var app = Microsoft.Maui.Controls.Application.Current;
        try
        {
            nint active = GetForegroundWindow();
            GetWindowThreadProcessId(active, out uint process);
            if (process == Environment.ProcessId) owner = active;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool accepted = false;
            dialog = new Microsoft.UI.Xaml.Window { Title = "Закрытие соединения" };
            var content = new Grid { Padding = new Thickness(24), RowSpacing = 20 };
            content.RowDefinitions.Add(new() { Height = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
            content.RowDefinitions.Add(new() { Height = Microsoft.UI.Xaml.GridLength.Auto });
            var text = new TextBlock
            {
                Text = $"Отключиться от сервера «{serverName}»?",
                FontSize = 18,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
            };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right,
                Spacing = 8
            };
            var disconnect = new Button { Content = "Отключить", MinWidth = 120 };
            var cancel = new Button { Content = "Отмена", MinWidth = 100 };
            if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accentStyle) &&
                accentStyle is Microsoft.UI.Xaml.Style style)
                disconnect.Style = style;
            buttons.Children.Add(disconnect);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 1);
            content.Children.Add(text);
            content.Children.Add(buttons);
            dialog.Content = content;
            nint handle = WinRT.Interop.WindowNative.GetWindowHandle(dialog);
            if (IsWindow(owner)) SetWindowLongPtr(handle, -8, owner); // owned dialog, including native fullscreen RDP
            var presenter = OverlappedPresenter.CreateForDialog();
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            dialog.AppWindow.SetPresenter(presenter);
            dialog.AppWindow.IsShownInSwitchers = false;
            string icon = Path.Combine(AppContext.BaseDirectory, "appicon.ico");
            if (File.Exists(icon)) dialog.AppWindow.SetIcon(icon);
            void ApplyTheme()
            {
                bool dark = app?.RequestedTheme == AppTheme.Dark;
                content.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
                content.Background = new SolidColorBrush(dark
                    ? Windows.UI.Color.FromArgb(255, 20, 27, 37) : Windows.UI.Color.FromArgb(255, 248, 250, 253));
                text.Foreground = new SolidColorBrush(dark
                    ? Windows.UI.Color.FromArgb(255, 231, 239, 252) : Windows.UI.Color.FromArgb(255, 25, 44, 73));
                int value = dark ? 1 : 0;
                DwmSetWindowAttribute(handle, 20, in value, sizeof(int));
            }
            ApplyTheme();
            themeChanged = (_, _) => ApplyTheme();
            if (app is not null) app.RequestedThemeChanged += themeChanged;
            double scale = Math.Max(96, GetDpiForWindow(owner)) / 96d;
            var area = DisplayArea.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(owner), DisplayAreaFallback.Primary).WorkArea;
            int width = Math.Min(area.Width, (int)Math.Round(480 * scale));
            int height = Math.Min(area.Height, (int)Math.Round(220 * scale));
            GetWindowRect(owner, out var bounds);
            int x = Math.Clamp(bounds.Left + (bounds.Right - bounds.Left - width) / 2, area.X, area.X + area.Width - width);
            int y = Math.Clamp(bounds.Top + (bounds.Bottom - bounds.Top - height) / 2, area.Y, area.Y + area.Height - height);
            dialog.AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
            var ownedDialog = dialog;
            disconnect.Click += (_, _) => { accepted = true; ownedDialog.Close(); };
            cancel.Click += (_, _) => ownedDialog.Close();
            var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
            escape.Invoked += (_, args) => { args.Handled = true; ownedDialog.Close(); };
            content.KeyboardAccelerators.Add(escape);
            content.Loaded += (_, _) => cancel.Focus(FocusState.Programmatic);
            dialog.Closed += (_, _) => { dialogClosed = true; completion.TrySetResult(accepted); };
            if (IsWindow(owner) && IsWindowEnabled(owner))
            {
                EnableWindow(owner, false);
                ownerDisabled = true;
            }
            dialog.Activate();
            return await completion.Task;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine($"Connection close dialog: {ex}");
            return false;
        }
        finally
        {
            if (app is not null && themeChanged is not null) app.RequestedThemeChanged -= themeChanged;
            if (ownerDisabled && IsWindow(owner))
            {
                EnableWindow(owner, true);
                SetForegroundWindow(owner);
            }
            showing = false;
            if (!dialogClosed) dialog?.Close();
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern bool EnableWindow(nint window, bool enabled);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, in int value, int size);
}

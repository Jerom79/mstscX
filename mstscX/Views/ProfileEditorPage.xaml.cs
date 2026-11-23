using System.Runtime.InteropServices;
using mstscX.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;
using NativeControls = Microsoft.UI.Xaml.Controls;

namespace mstscX.Views;

public partial class ProfileEditorPage : ContentPage
{
    private readonly MainViewModel viewModel;
    private readonly Microsoft.Maui.Controls.Window? owner;
    private AppWindow? editorAppWindow;
    private bool closeInProgress;

    public ProfileEditorPage(MainViewModel viewModel, Microsoft.Maui.Controls.Window? owner)
    {
        this.viewModel = viewModel;
        this.owner = owner;
        InitializeComponent();
        BindingContext = viewModel;
        Loaded += (_, _) =>
        {
            if (editorAppWindow is null && Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window window)
            {
                editorAppWindow = window.AppWindow;
                editorAppWindow.Closing += OnEditorWindowClosing;
            }
        };
        Unloaded += (_, _) =>
        {
            if (editorAppWindow is { } window)
                window.Closing -= OnEditorWindowClosing;
            editorAppWindow = null;
        };
    }

    private void OnSectionButtonHandlerChanged(object? sender, EventArgs e)
    {
        if (sender is Microsoft.Maui.Controls.Button { Handler.PlatformView: Microsoft.UI.Xaml.Controls.Button button })
            button.HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
    }

    private void OnEditorFieldChanged(object? sender, EventArgs e) =>
        Dispatcher.Dispatch(viewModel.RefreshProfileEditorTitle);

    private async void OnShowPublishedAppsClicked(object? sender, EventArgs e)
    {
        if (RemoteAppPathEntry.Handler?.PlatformView is not Microsoft.UI.Xaml.FrameworkElement anchor)
            return;

        var menu = new NativeControls.MenuFlyout();
        var entries = new List<(Services.PublishedRemoteApp App, NativeControls.MenuFlyoutItem Item)>();
        foreach (var app in viewModel.PublishedRemoteApps)
        {
            var item = new NativeControls.MenuFlyoutItem
            {
                Text = app.DisplayName,
                Icon = new NativeControls.SymbolIcon(NativeControls.Symbol.Remote)
            };
            item.Click += (_, _) => viewModel.SelectedPublishedRemoteApp = app;
            menu.Items.Add(item);
            entries.Add((app, item));
        }

        if (entries.Count == 0)
            return;

        bool isOpen = true;
        menu.Closed += (_, _) => isOpen = false;
        menu.ShowAt(anchor, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft
        });

        foreach (var (app, item) in entries)
        {
            if (!isOpen)
                break;

            try
            {
                if (await viewModel.GetPublishedAppIconAsync(app) is not { } png || !isOpen)
                    continue;

                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(CryptographicBuffer.CreateFromByteArray(png));
                stream.Seek(0);
                var image = new BitmapImage { DecodePixelWidth = 16 };
                await image.SetSourceAsync(stream);
                if (isOpen)
                    item.Icon = new NativeControls.ImageIcon { Source = image, Width = 16, Height = 16 };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }
    }

    private async void OnCloseClicked(object? sender, EventArgs e) => await RequestCloseAsync();

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window editor)
            return;

        await viewModel.SaveProfileAsync();
        editor.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!viewModel.IsEditing || !ReferenceEquals(Window?.Handler?.PlatformView, editor) ||
                owner?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window mainWindow)
                return;

            if (GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(mainWindow))
                editor.Activate();
        });
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private void OnEditorWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!viewModel.IsEditing)
            return;

        args.Cancel = true;
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window window)
            window.DispatcherQueue.TryEnqueue(async () => await RequestCloseAsync());
    }

    private async Task RequestCloseAsync()
    {
        if (closeInProgress || !viewModel.CanEdit || !viewModel.IsEditing)
            return;

        closeInProgress = true;
        try
        {
            if (viewModel.ProfileNeedsSave)
            {
                if (Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window
                    { Content: Microsoft.UI.Xaml.FrameworkElement { XamlRoot: { } root } })
                    return;

                var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
                {
                    XamlRoot = root,
                    Title = global::mstscX.Services.L.T("Сохранить изменения?"),
                    PrimaryButtonText = global::mstscX.Services.L.T("Да"),
                    SecondaryButtonText = global::mstscX.Services.L.T("Нет"),
                    DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary
                };
                var choice = await dialog.ShowAsync();
                if (choice == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                {
                    if (!await viewModel.SaveProfileAsync())
                        return;
                }
                else if (choice != Microsoft.UI.Xaml.Controls.ContentDialogResult.Secondary)
                {
                    return;
                }
            }

            viewModel.CloseEditor();
        }
        finally
        {
            closeInProgress = false;
        }
    }
}

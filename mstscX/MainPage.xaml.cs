using mstscX.ViewModels;
using mstscX.Models;
using Microsoft.Windows.Storage.Pickers;
using NativeDragOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation;
using NativeElement = Microsoft.UI.Xaml.FrameworkElement;
using MenuSymbol = Microsoft.UI.Xaml.Controls.Symbol;
using SymbolIcon = Microsoft.UI.Xaml.Controls.SymbolIcon;

namespace mstscX;

public partial class MainPage : ContentPage
{
    private const string ServerPanelWidthKey = "ServerPanelWidth";
    private double serverPanelWidth = Preferences.Default.Get(ServerPanelWidthKey, 0d);
    private const double SplitterWidth = 7;
    private const string NavigationCollapsedKey = "MainMenuCollapsed";
    private const string CatalogViewKey = "MainMenuCatalogView";
    private const string ServerCardDragFormat = "mstscX.ServerCard";
    private static string PowerShellMenuText => global::mstscX.Services.L.T("Открыть Windows PowerShell");
    private readonly MainViewModel viewModel;
    private NativeElement? importHost;
    private bool rdpFileOperationInProgress;
    private ServerProfile? draggedProfile;
    private Microsoft.UI.Windowing.AppWindowPresenter? mainWindowPresenter;
    private Microsoft.UI.Xaml.Input.KeyboardAccelerator? fullScreenAccelerator;
    private WinUI.TitleMenuBar? mainTitleMenu;
    private WinUI.MainFullScreenMenu? mainFullScreenMenu;
    private Action? releaseMainMenus;

    private void UpdateMainFullScreenMenu()
    {
        mainFullScreenMenu?.Dispose();
        mainFullScreenMenu = null;
        if (Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow) return;
        bool fullScreen = !embeddedFullScreen && mainWindowPresenter is not null &&
            nativeWindow.AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
        if (fullScreen)
        {
            var menus = CreateMainMenus(fullScreen: true);
            mainFullScreenMenu = new WinUI.MainFullScreenMenu(menus.Items, menus.Dispose, nativeWindow.DispatcherQueue,
                WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow),
                () => !embeddedFullScreen && nativeWindow.AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen,
                () => OnToggleNavigation(null, EventArgs.Empty), () => isNavigationCollapsed,
                ToggleMainWindowFullScreen, () =>
                {
                    ToggleMainWindowFullScreen();
                    if (nativeWindow.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                        presenter.Maximize();
                }, RequestMainWindowClose, point =>
                {
                    ToggleMainWindowFullScreen();
                    if (nativeWindow.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                        presenter.Restore();
                    nativeWindow.Activate();
                    WinUI.MainWindowMenuDrag.Start(nativeWindow, point);
                });
        }
    }

    private void ToggleMainWindowFullScreen()
    {
        // The embedded RDP session manages its own fullscreen window and toolbar.
        if (embeddedFullScreen || Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow)
            return;
        var appWindow = nativeWindow.AppWindow;
        if (appWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen)
        {
            if (mainWindowPresenter is { } presenter)
                appWindow.SetPresenter(presenter);
            else
                appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped);
            mainWindowPresenter = null;
        }
        else
        {
            mainWindowPresenter = appWindow.Presenter;
            appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
        }
        UpdateMainFullScreenMenu();
    }

    private void OnImportHostChanged(object? sender, EventArgs e)
    {
        if (importHost is not null)
        {
            importHost.Unloaded -= OnMainHostUnloaded;
            mainFullScreenMenu?.Dispose();
            mainFullScreenMenu = null;
            if (fullScreenAccelerator is not null)
                importHost.KeyboardAccelerators.Remove(fullScreenAccelerator);
            importHost.DragOver -= OnRdpDragOver;
            importHost.Drop -= OnRdpDrop;
        }

        importHost = (sender as VisualElement)?.Handler?.PlatformView as NativeElement;
        if (importHost is null)
            return;

        importHost.AllowDrop = true;
        importHost.Unloaded += OnMainHostUnloaded;
        fullScreenAccelerator ??= new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.F11
        };
        fullScreenAccelerator.Invoked -= OnMainFullScreenAccelerator;
        fullScreenAccelerator.Invoked += OnMainFullScreenAccelerator;
        importHost.KeyboardAccelerators.Add(fullScreenAccelerator);
        importHost.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        importHost.DragOver += OnRdpDragOver;
        importHost.Drop += OnRdpDrop;
    }

    private void OnMainHostUnloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs args)
    {
        mainFullScreenMenu?.Dispose();
        mainFullScreenMenu = null;
    }

    private void OnMainFullScreenAccelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (embeddedFullScreen) return;
        ToggleMainWindowFullScreen();
        args.Handled = true;
    }

    private void OnRdpDragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        e.AcceptedOperation = !rdpFileOperationInProgress && e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)
            ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
            : Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        e.Handled = true;
    }

    private async void OnRdpDrop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        e.Handled = true;
        if (rdpFileOperationInProgress || !e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            return;

        var deferral = e.GetDeferral();
        rdpFileOperationInProgress = true;
        ServerProfile? imported = null;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var files = items.OfType<Windows.Storage.StorageFile>()
                .Where(file => file.FileType.Equals(".rdp", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (files.Length != 1)
            {
                viewModel.ReportFileStatus(global::mstscX.Services.L.T("Перетащите один файл .rdp."));
                return;
            }

            var file = files[0];
            await using var stream = await file.OpenStreamForReadAsync();
            imported = await Services.RdpFileImport.ReadAsync(stream, Path.GetFileNameWithoutExtension(file.Name));
        }
        catch (FormatException ex)
        {
            viewModel.ReportFileStatus(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            viewModel.ReportFileStatus(global::mstscX.Services.L.T("Не удалось прочитать файл RDP."));
        }
        finally
        {
            rdpFileOperationInProgress = false;
            deferral.Complete();
        }

        if (imported is null)
            return;

        rdpFileOperationInProgress = true;
        try
        {
            await OpenImportedProfileAsync(imported);
        }
        finally
        {
            rdpFileOperationInProgress = false;
        }
    }

    private async Task OpenImportedProfileAsync(ServerProfile imported)
    {
        if (!viewModel.CanImportProfile())
            return;

        await ReplaceServerListAsync([imported]);
    }

    private async Task PickRdpFileAsync()
    {
        if (rdpFileOperationInProgress || Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window window ||
            !viewModel.CanImportProfile())
            return;

        rdpFileOperationInProgress = true;
        try
        {
            var picker = new FileOpenPicker(window.AppWindow.Id) { FileTypeFilter = { ".rdp" } };
            if (await picker.PickSingleFileAsync() is not { } file)
                return;

            await using var stream = File.OpenRead(file.Path);
            var profile = await Services.RdpFileImport.ReadAsync(stream, Path.GetFileNameWithoutExtension(file.Path));
            await OpenImportedProfileAsync(profile);
        }
        catch (FormatException ex)
        {
            viewModel.ReportFileStatus(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            viewModel.ReportFileStatus(global::mstscX.Services.L.T("Не удалось открыть файл RDP."));
        }
        finally
        {
            rdpFileOperationInProgress = false;
        }
    }

    private async Task SaveRdpFileAsAsync()
    {
        if (rdpFileOperationInProgress || viewModel.SelectedProfile is not { } profile ||
            Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window window)
            return;

        rdpFileOperationInProgress = true;
        try
        {
            string contents = Services.RdpFileExport.FormatProfile(profile);
            string name = string.Join("_", profile.Name.Split(Path.GetInvalidFileNameChars())).Trim(' ', '.');
            var picker = new FileSavePicker(window.AppWindow.Id)
            {
                SuggestedFileName = name.Length == 0 ? global::mstscX.Services.L.T("Подключение") : name,
                FileTypeChoices = { { global::mstscX.Services.L.T("Подключение к удалённому рабочему столу"), new List<string> { ".rdp" } } },
                DefaultFileExtension = ".rdp"
            };
            if (await picker.PickSaveFileAsync() is not { } file)
                return;

            if (!Path.GetExtension(file.Path).Equals(".rdp", StringComparison.OrdinalIgnoreCase))
                throw new FormatException(global::mstscX.Services.L.T("Укажите имя файла с расширением .rdp."));

            await Services.RdpFileExport.WriteAsync(file.Path, contents);
            viewModel.ReportFileStatus(global::mstscX.Services.L.F("Файл «{0}» сохранён.", Path.GetFileName(file.Path)));
        }
        catch (FormatException ex)
        {
            viewModel.ReportFileStatus(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            viewModel.ReportFileStatus(global::mstscX.Services.L.T("Не удалось сохранить файл RDP."));
        }
        finally
        {
            rdpFileOperationInProgress = false;
        }
    }
    private readonly Windows.UI.ViewManagement.UISettings systemColors = new();
    private readonly Views.ThemePreview systemPreview = new(false);
    private readonly List<Microsoft.UI.Xaml.Controls.RadioMenuFlyoutItem[]> themeMenuGroups = [];
    private bool isNavigationCollapsed;
    private Button? activeNavigation;

    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        InitializeLanguagePicker();
        this.viewModel = viewModel;
        BindingContext = viewModel;
        InitializeNavigationAutoHide();
        CompactPanelAtPointerSwitch.IsToggled = Services.ViewSettings.CompactPanelAtPointer;
        WarnBeforeClosingSwitch.IsToggled = WinUI.ConnectionCloseConfirmation.Enabled;
        SystemThemePreview.Drawable = systemPreview;
        LightThemePreview.Drawable = new Views.ThemePreview(false);
        DarkThemePreview.Drawable = new Views.ThemePreview(true);
        Loaded += (_, _) =>
        {
            systemColors.ColorValuesChanged += OnSystemColorsChanged;
            RefreshThemePreviews();
        };
        Unloaded += (_, _) =>
        {
            systemColors.ColorValuesChanged -= OnSystemColorsChanged;
        };
        isNavigationCollapsed = Preferences.Default.Get(NavigationCollapsedKey, false);
        ApplyNavigationLayout();
        ShowSettings(false);
        SetCatalogView((CatalogView)Math.Clamp(Preferences.Default.Get(CatalogViewKey, 0), 0, 2));
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.ConnectRequested += Connect;
        viewModel.ConnectionFailed += ShowConnectionError;
        viewModel.RemoteAppIconChanged += () => WinUI.TaskbarServers.Refresh(viewModel.Profiles);
        viewModel.ProfileRenamed += UpdateOpenSessionName;
        viewModel.ActivateExistingSession = TryActivateExistingSession;
        Loaded += InitializeTaskbar;
    }

    private void UpdateOpenSessionName(ServerProfile profile)
    {
        nativeSessions.GetValueOrDefault(profile.Id)?.UpdateServerName(profile.Name, profile);
        if (embeddedSessions.TryGetValue(profile.Id, out var embedded))
            embedded.UpdateServerName(profile.Name);

    }

    private void InitializeTaskbar(object? sender, EventArgs e)
    {
        if (Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow)
            return;

        Loaded -= InitializeTaskbar;
        WinUI.TaskbarServers.Refresh(viewModel.Profiles);
        WinUI.Program.HandleActivations(nativeWindow.DispatcherQueue, ActivateTaskbarProfile, OpenServerFileAsync);
    }

    private void ActivateTaskbarProfile(Guid? id)
    {
        if (confirmingMainWindowClose || mainWindowCloseApproved)
            return;

        if (id is { } profileId && viewModel.Profiles.FirstOrDefault(profile => profile.Id == profileId) is { } profile)
        {
            ShowSettings(false);
            viewModel.SelectedProfile = profile;
            if (!TryActivateExistingSession(profileId))
            {
                ActivateSessionWindow(Window);
                viewModel.ConnectProfileCommand.Execute(null);
            }
            return;
        }

        ActivateSessionWindow(Window);
        if (id.HasValue)
            viewModel.ReportFileStatus(global::mstscX.Services.L.T("Этот профиль больше не существует."));
    }

    private enum CatalogView { List, SmallCards, LargeCards }

    private CatalogView catalogView;
    private Microsoft.Maui.Controls.Window? editorWindow;
    private void OnShowServers(object? sender, EventArgs e) => ShowSettings(false);

    private void OnShowSettings(object? sender, EventArgs e) => ShowSettings(true);

    private void OnToggleNavigation(object? sender, EventArgs e)
    {
        if (navigationHidden)
        {
            SetNavigationHidden(false);
            ScheduleNavigationHide();
            return;
        }
        isNavigationCollapsed = !isNavigationCollapsed;
        Preferences.Default.Set(NavigationCollapsedKey, isNavigationCollapsed);
        ApplyNavigationLayout();
    }

    private void ApplyNavigationLayout()
    {
        NavigationPane.WidthRequest = isNavigationCollapsed ? 68 : 200;
        UpdateNavigationAutoHideLayout();
        UpdateServerPanelWidth();
        ServersNavigationLabel.IsVisible = SettingsNavigationLabel.IsVisible = !isNavigationCollapsed;
        ServersNavigationContent.HorizontalOptions = SettingsNavigationContent.HorizontalOptions =
            isNavigationCollapsed ? LayoutOptions.Center : LayoutOptions.Start;
        ServersNavigationContent.Margin = SettingsNavigationContent.Margin =
            new Thickness(isNavigationCollapsed ? 0 : 14, 0);
        AlignNavigationButton(ServersNavigation);
        AlignNavigationButton(SettingsNavigation);
        DisconnectAllNavigationLabel.IsVisible = ExitNavigationLabel.IsVisible = !isNavigationCollapsed;
        DisconnectAllNavigationContent.HorizontalOptions = ExitNavigationContent.HorizontalOptions =
            isNavigationCollapsed ? LayoutOptions.Center : LayoutOptions.Start;
        DisconnectAllNavigationContent.Margin = ExitNavigationContent.Margin =
            new Thickness(isNavigationCollapsed ? 0 : 14, 0);
        AlignNavigationButton(DisconnectAllNavigation);
        AlignNavigationButton(ExitNavigation);

    }


    private void OnNavigationButtonHandlerChanged(object? sender, EventArgs e)
    {
        if (sender is Button button)
            AlignNavigationButton(button);
    }

    private void AlignNavigationButton(Button button)
    {
        if (button.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.Button nativeButton)
        {
            nativeButton.HorizontalContentAlignment = isNavigationCollapsed
                ? Microsoft.UI.Xaml.HorizontalAlignment.Center
                : Microsoft.UI.Xaml.HorizontalAlignment.Left;
        }
    }

    private void ShowSettings(bool show)
    {
        Button next = show ? SettingsNavigation : ServersNavigation;
        if (activeNavigation == next)
            return;

        if (activeNavigation is { } previous)
        {
            previous.RemoveBinding(VisualElement.BackgroundColorProperty);
            previous.BackgroundColor = Colors.Transparent;
            previous.FontAttributes = FontAttributes.None;
        }

        next.SetAppThemeColor(VisualElement.BackgroundColorProperty,
            Color.FromArgb("#D8E8FF"), Color.FromArgb("#29446E"));
        next.FontAttributes = FontAttributes.Bold;
        activeNavigation = next;
        ServersNavigationLabel.FontAttributes = show ? FontAttributes.None : FontAttributes.Bold;
        SettingsNavigationLabel.FontAttributes = show ? FontAttributes.Bold : FontAttributes.None;
        SessionLayout.IsVisible = !show;
        SettingsPanel.IsVisible = show;
        UpdateSelectedSession();
    }

    private void OnCompactPanelAtPointerToggled(object? sender, ToggledEventArgs e) =>
        Services.ViewSettings.SetCompactPanelAtPointer(e.Value);

    private void OnWarnBeforeClosingToggled(object? sender, ToggledEventArgs e) =>
        WinUI.ConnectionCloseConfirmation.Enabled = e.Value;



    private void OnThemeSelected(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: string parameter } &&
            int.TryParse(parameter, out int index) && index is >= 0 and <= 2)
            SelectTheme(index);
    }

    private void SelectTheme(int index)
    {
        if (Application.Current is not { } app)
            return;

        Services.ThemeSettings.Save(app, index);
        RefreshThemePreviews();
    }

    private void OnSystemColorsChanged(Windows.UI.ViewManagement.UISettings sender, object args) =>
        Dispatcher.Dispatch(RefreshThemePreviews);

    private void RefreshThemePreviews()
    {
        var background = systemColors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
        systemPreview.IsDark = 299 * background.R + 587 * background.G + 114 * background.B < 128_000;
        SystemThemePreview.Invalidate();

        int selected = Services.ThemeSettings.SelectedIndex;
        foreach (var group in themeMenuGroups)
            for (int index = 0; index < group.Length; index++)
                group[index].IsChecked = index == selected;

        Color accent = Color.FromArgb("#4788DC");
        SystemThemeRing.Stroke = selected == 0 ? accent : Colors.Transparent;
        LightThemeRing.Stroke = selected == 1 ? accent : Colors.Transparent;
        DarkThemeRing.Stroke = selected == 2 ? accent : Colors.Transparent;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ProfileGroups))
        {
            UpdateCatalogGroupStyle();
            WinUI.TaskbarServers.Refresh(viewModel.Profiles);
        }

        if (e.PropertyName == nameof(MainViewModel.SelectedProfile))
            UpdateSelectedSession();

        if (e.PropertyName == nameof(MainViewModel.ProfileEditorTitle) && editorWindow is { } editor)
            editor.Title = viewModel.ProfileEditorTitle;

        if (e.PropertyName != nameof(MainViewModel.IsEditing))
            return;

        if (viewModel.IsEditing)
        {
            var window = new Microsoft.Maui.Controls.Window(new Views.ProfileEditorPage(viewModel, Window))
            {
                Title = viewModel.ProfileEditorTitle,
                Width = 740,
                Height = 680
            };
            editorWindow = window;
            window.Destroying += (_, _) =>
            {
                if (editorWindow != window)
                    return;

                editorWindow = null;
                viewModel.CloseEditor();
            };
            Application.Current!.OpenWindow(window);
        }
        else if (editorWindow is { } window)
        {
            editorWindow = null;
            Application.Current!.CloseWindow(window);
        }
    }

    private void OnCatalogHandlerChanged(object? sender, EventArgs e)
    {
        if (sender is VisualElement { Handler.PlatformView: NativeElement element })
            element.ContextFlyout = CreateCatalogMenu();
    }

    private void OnCatalogHostHandlerChanged(object? sender, EventArgs e)
    {
        if (sender is VisualElement { Handler.PlatformView: NativeElement element })
            element.ContextFlyout = CreateCatalogMenu();
    }

    private void OnCardHandlerChanged(object? sender, EventArgs e)
    {
        if (sender is not VisualElement { Handler.PlatformView: NativeElement element } card)
            return;

        UpdateCardTooltip(card);
        card.BindingContextChanged -= OnCardBindingContextChanged;
        card.BindingContextChanged += OnCardBindingContextChanged;

        // WinUI can handle the press for dragging before CollectionView selects the item.
        element.AddHandler(Microsoft.UI.Xaml.UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, args) =>
            {
                var pointer = args.GetCurrentPoint(element);
                if (pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse &&
                    pointer.Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed &&
                    card.BindingContext is ServerProfile profile)
                {
                    viewModel.SelectedProfile = profile;
                }
            }), handledEventsToo: true);

        element.CanDrag = true;
        element.AllowDrop = true;
        element.DragStarting += (_, args) =>
        {
            if (card.BindingContext is not ServerProfile profile || !viewModel.CanMoveProfile(profile))
            {
                args.Cancel = true;
                return;
            }

            draggedProfile = profile;
            args.Data.SetData(ServerCardDragFormat, true);
            args.Data.RequestedOperation = NativeDragOperation.Move;
            args.AllowedOperations = NativeDragOperation.Move;
        };
        element.DropCompleted += (_, _) => draggedProfile = null;
        element.DragOver += (_, args) =>
        {
            if (!args.DataView.Contains(ServerCardDragFormat))
                return;

            args.Handled = true;
            args.AcceptedOperation = NativeDragOperation.None;
            if (draggedProfile is null || card.BindingContext is not ServerProfile target ||
                ReferenceEquals(target, draggedProfile) || target.RemoteApp.Enabled != draggedProfile.RemoteApp.Enabled)
                return;

            args.AcceptedOperation = NativeDragOperation.Move;
            args.DragUIOverride.Caption = InsertAfterCard(element, args)
                ? global::mstscX.Services.L.F("После «{0}»", target.Name)
                : global::mstscX.Services.L.F("Перед «{0}»", target.Name);
        };
        element.Drop += (_, args) =>
        {
            if (!args.DataView.Contains(ServerCardDragFormat))
                return;

            args.Handled = true;
            args.AcceptedOperation = draggedProfile is { } source && card.BindingContext is ServerProfile target &&
                                     viewModel.MoveProfile(source, target, InsertAfterCard(element, args))
                ? NativeDragOperation.Move
                : NativeDragOperation.None;
        };

        element.DoubleTapped += (_, args) =>
        {
            if (args.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse ||
                card.BindingContext is ServerProfile { IsRenaming: true })
                return;

            for (var source = args.OriginalSource as Microsoft.UI.Xaml.DependencyObject;
                 source is not null && source != element;
                 source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source))
            {
                if (source is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
                    return;
            }

            args.Handled = true;
            ExecuteCardCommand(card, viewModel.ConnectProfileCommand);
        };
        var menu = CreateCatalogMenu();
        menu.Items.Insert(0, CreateMenuItem(global::mstscX.Services.L.T("Подключиться"), MenuSymbol.Remote, () =>
            ExecuteCardCommand(card, viewModel.ConnectProfileCommand)));
        menu.Items.Insert(1, CreateMenuItem(global::mstscX.Services.L.T("Редактировать"), MenuSymbol.Edit, () =>
            ExecuteCardCommand(card, viewModel.EditProfileCommand)));
        menu.Items.Insert(2, CreateMenuItem(global::mstscX.Services.L.T("Переименовать"), MenuSymbol.Rename, () =>
        {
            if (card.BindingContext is ServerProfile profile)
                element.DispatcherQueue.TryEnqueue(() => viewModel.BeginInlineRename(profile));
        }));
        menu.Items.Insert(3, CreateMenuItem(global::mstscX.Services.L.T("Удалить"), MenuSymbol.Delete, async () =>
        {
            if (card.BindingContext is ServerProfile profile)
                await ConfirmDeleteProfileAsync(profile);
        }));
        menu.Items.Insert(4, new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
        var powerShell = CreatePowerShellMenuItem(() => card.BindingContext is ServerProfile { RemoteApp.Enabled: false } profile
            ? OpenPowerShellAsync(profile) : Task.CompletedTask);
        menu.Items.Insert(1, powerShell);
        menu.Opening += (_, _) => powerShell.Visibility =
            card.BindingContext is ServerProfile { RemoteApp.Enabled: false }
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;
        element.ContextFlyout = menu;
    }

    private void OnInlineRenameCommit(object? sender, EventArgs e)
    {
        if (sender is Views.ServerCardName { BindingContext: ServerProfile profile } editor)
            viewModel.TryRenameProfile(profile, editor.RenameText);
    }

    private void OnInlineRenameCancel(object? sender, EventArgs e)
    {
        if (sender is Views.ServerCardName { BindingContext: ServerProfile profile })
            viewModel.CancelInlineRename(profile);
    }

    private static void OnCardBindingContextChanged(object? sender, EventArgs e)
    {
        if (sender is VisualElement card)
            UpdateCardTooltip(card);
    }

    private static void UpdateCardTooltip(VisualElement card)
    {
        if (card.BindingContext is not ServerProfile profile)
        {
            card.ClearValue(ToolTipProperties.TextProperty);
            return;
        }

        string login = string.IsNullOrWhiteSpace(profile.UserName) ? global::mstscX.Services.L.T("не указан") : profile.UserName;
        ToolTipProperties.SetText(card, global::mstscX.Services.L.F("{0}\nАдрес: {1}\nЛогин: {2}", profile.Name, RdpEndpoint.FormatForDisplay(profile.Address), login));
    }

    private bool InsertAfterCard(NativeElement card, Microsoft.UI.Xaml.DragEventArgs args)
    {
        var position = args.GetPosition(card);
        return ServerCatalog.ItemsLayout is GridItemsLayout { Span: > 1 }
            ? position.X >= card.ActualWidth / 2
            : position.Y >= card.ActualHeight / 2;
    }

    private void ExecuteCardCommand(VisualElement card, System.Windows.Input.ICommand command)
    {
        if (card.BindingContext is not ServerProfile profile)
            return;

        viewModel.SelectedProfile = profile;
        if (command.CanExecute(null))
            command.Execute(null);
    }

    private void OnEditCardClicked(object? sender, EventArgs e)
    {
        if (sender is ImageButton button)
            ExecuteCardCommand(button, viewModel.EditProfileCommand);
    }

    private void OnDisconnectCardClicked(object? sender, EventArgs e)
    {
        if (sender is not ImageButton { BindingContext: ServerProfile { IsSessionActive: true } profile } ||
            nativeMainWindow is not { } window)
            return;

        window.DispatcherQueue.TryEnqueue(() =>
        {
            if (remoteAppSessions.ContainsKey(profile.Id))
                RequestCloseRemoteAppSession(profile);
            else if (embeddedSessions.TryGetValue(profile.Id, out var embedded))
                embedded.CloseConnection();
            else if (nativeSessions.TryGetValue(profile.Id, out var native))
                native.RequestCloseConnection();
        });
    }

    private Microsoft.UI.Xaml.Controls.MenuFlyout CreateCatalogMenu()
    {
        var menu = new Microsoft.UI.Xaml.Controls.MenuFlyout();
        menu.Items.Add(CreateMenuItem(global::mstscX.Services.L.T("Добавить сервер"), MenuSymbol.Add, () =>
        {
            if (viewModel.NewProfileCommand.CanExecute(null))
                viewModel.NewProfileCommand.Execute(null);
        }));
        var viewMenu = new Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem
        {
            Text = global::mstscX.Services.L.T("Вид"),
            Icon = new SymbolIcon(MenuSymbol.View)
        };
        AddCatalogViewItems(viewMenu.Items);
        menu.Items.Add(viewMenu);
        return menu;
    }

    private (Microsoft.UI.Xaml.Controls.MenuBarItem[] Items, Action Dispose) CreateMainMenus(bool fullScreen)
    {
        var fileMenu = new Microsoft.UI.Xaml.Controls.MenuBarItem { Title = global::mstscX.Services.L.T("Файл") };
        var open = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = global::mstscX.Services.L.T("Открыть RDP соединение…"),
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE838" } // FolderOpen
        };
        open.Click += async (_, _) => await PickRdpFileAsync();
        fileMenu.Items.Add(open);
        var saveAs = CreateMenuItem(global::mstscX.Services.L.T("Сохранить RDP соединение…"), MenuSymbol.Save, async () => await SaveRdpFileAsAsync());
        fileMenu.Items.Add(saveAs);
        fileMenu.Items.Add(CreateMenuItem(global::mstscX.Services.L.T("Открыть список…"), MenuSymbol.OpenFile, async () => await PickServerListAsync()));
        fileMenu.Items.Add(CreateMenuItem(global::mstscX.Services.L.T("Сохранить список…"), MenuSymbol.Save, async () => await SaveServerListAsync()));
        fileMenu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
        fileMenu.Items.Add(CreateMenuItem(global::mstscX.Services.L.T("Закрыть"), MenuSymbol.Cancel, RequestMainWindowClose));

        var viewMenu = new Microsoft.UI.Xaml.Controls.MenuBarItem { Title = global::mstscX.Services.L.T("Вид") };
        var fullScreenItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = fullScreen ? global::mstscX.Services.L.T("Выйти из полноэкранного режима") : global::mstscX.Services.L.T("Полноэкранный режим"),
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE740" },
            KeyboardAcceleratorTextOverride = "F11"
        };
        fullScreenItem.Click += (_, _) => ToggleMainWindowFullScreen();
        viewMenu.Items.Add(fullScreenItem);
        viewMenu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
        AddCatalogViewItems(viewMenu.Items);
        viewMenu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
        var themeMenu = new Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem
        {
            Text = global::mstscX.Services.L.T("Тема приложения"),
            Icon = new SymbolIcon(MenuSymbol.Setting)
        };
        (string Name, Microsoft.UI.Xaml.Controls.IconElement Icon)[] themes =
        [
            (global::mstscX.Services.L.T("Системная"), new SymbolIcon(MenuSymbol.Remote)),
            (global::mstscX.Services.L.T("Светлая"), new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE706" }), // Brightness
            (global::mstscX.Services.L.T("Тёмная"), new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE708" }) // QuietHours
        ];
        int selectedTheme = Services.ThemeSettings.SelectedIndex;
        var themeMenuItems = new Microsoft.UI.Xaml.Controls.RadioMenuFlyoutItem[themes.Length];
        themeMenuGroups.Add(themeMenuItems);
        for (int index = 0; index < themes.Length; index++)
        {
            int themeIndex = index;
            var item = new Microsoft.UI.Xaml.Controls.RadioMenuFlyoutItem
            {
                Text = themes[index].Name,
                Icon = themes[index].Icon,
                GroupName = "ApplicationTheme",
                IsChecked = index == selectedTheme
            };
            item.Click += (_, _) => SelectTheme(themeIndex);
            themeMenuItems[index] = item;
            themeMenu.Items.Add(item);
        }
        viewMenu.Items.Add(themeMenu);

        var editMenu = new Microsoft.UI.Xaml.Controls.MenuBarItem { Title = global::mstscX.Services.L.T("Правка") };
        editMenu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = global::mstscX.Services.L.T("Добавить"),
            Icon = new SymbolIcon(MenuSymbol.Add),
            Command = viewModel.NewProfileCommand
        });
        var separator = new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator();
        var connect = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = global::mstscX.Services.L.T("Подключиться"),
            Icon = new SymbolIcon(MenuSymbol.Remote),
            Command = viewModel.ConnectProfileCommand
        };
        var edit = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = global::mstscX.Services.L.T("Редактировать"),
            Icon = new SymbolIcon(MenuSymbol.Edit),
            Command = viewModel.EditProfileCommand
        };
        var delete = CreateMenuItem(global::mstscX.Services.L.T("Удалить"), MenuSymbol.Delete, async () =>
        {
            if (viewModel.SelectedProfile is { } selected)
                await ConfirmDeleteProfileAsync(selected);
        });
        var powerShell = CreatePowerShellMenuItem(() => OpenPowerShellAsync(viewModel.SelectedProfile));
        editMenu.Items.Add(separator);
        editMenu.Items.Add(connect);
        editMenu.Items.Add(powerShell);
        editMenu.Items.Add(edit);
        editMenu.Items.Add(delete);

        void UpdateEditMenu()
        {
            var profile = viewModel.SelectedProfile;
            saveAs.IsEnabled = profile is not null;
            powerShell.Text = profile is null ? PowerShellMenuText : $"{PowerShellMenuText} — {profile.Name}";
            powerShell.Visibility = profile?.RemoteApp.Enabled == true
                ? Microsoft.UI.Xaml.Visibility.Collapsed
                : Microsoft.UI.Xaml.Visibility.Visible;
            var visibility = profile is null
                ? Microsoft.UI.Xaml.Visibility.Collapsed
                : Microsoft.UI.Xaml.Visibility.Visible;
            separator.Visibility = visibility;
            connect.Visibility = visibility;
            edit.Visibility = visibility;
            delete.Visibility = visibility;
            delete.IsEnabled = profile is not null && viewModel.CanDelete(profile);
        }

        System.ComponentModel.PropertyChangedEventHandler updateMenu = (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.SelectedProfile) or nameof(MainViewModel.IsEditing) or nameof(MainViewModel.CanEdit))
                UpdateEditMenu();
        };
        viewModel.PropertyChanged += updateMenu;
        UpdateEditMenu();
        var helpMenu = new Microsoft.UI.Xaml.Controls.MenuBarItem { Title = global::mstscX.Services.L.T("Справка") };
        helpMenu.Items.Add(CreateMenuItem(global::mstscX.Services.L.T("О программе"), MenuSymbol.Help, async () => await ShowAboutAsync()));
        return ([fileMenu, viewMenu, editMenu, helpMenu], () =>
        {
            viewModel.PropertyChanged -= updateMenu;
            themeMenuGroups.Remove(themeMenuItems);
        });
    }

    internal TitleBar CreateTitleBar()
    {
        releaseMainMenus?.Invoke();
        var menus = CreateMainMenus(fullScreen: false);
        releaseMainMenus = menus.Dispose;
        var title = new Label
        {
            Text = "mstscX",
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1
        };
        var toggle = new WinUI.TitleNavigationButton(() => OnToggleNavigation(null, EventArgs.Empty), () => isNavigationCollapsed)
        {
            WidthRequest = 36,
            HeightRequest = 32,
            VerticalOptions = LayoutOptions.Center
        };
        var fullScreenButton = new WinUI.TitleFullScreenButton(ToggleMainWindowFullScreen)
        {
            WidthRequest = 46, HeightRequest = 32, VerticalOptions = LayoutOptions.Center
        };
        mainTitleMenu = new WinUI.TitleMenuBar(menus.Items);
        return new TitleBar
        {
            HeightRequest = 38,
            LeadingContent = new HorizontalStackLayout
            {
                Children = { toggle, mainTitleMenu }
            },
            Content = title,
            TrailingContent = fullScreenButton
        };
    }

    private async Task ConfirmDeleteProfileAsync(ServerProfile profile)
    {
        if (viewModel.CanDelete(profile) &&
            await DisplayAlertAsync(global::mstscX.Services.L.T("Удалить сервер?"), global::mstscX.Services.L.F("Удалить профиль «{0}»?", profile.Name), global::mstscX.Services.L.T("Удалить"), global::mstscX.Services.L.T("Отмена")))
        {
            viewModel.DeleteProfile(profile);
            if (!viewModel.Profiles.Contains(profile))
                CloseRemoteAppSession(profile.Id);
        }
    }

    private void AddCatalogViewItems(IList<Microsoft.UI.Xaml.Controls.MenuFlyoutItemBase> items)
    {
        items.Add(CreateMenuItem(global::mstscX.Services.L.T("Список"), MenuSymbol.List, () => SelectCatalogView(CatalogView.List)));
        items.Add(CreateMenuItem(global::mstscX.Services.L.T("Мелкие карточки"), MenuSymbol.AllApps, () => SelectCatalogView(CatalogView.SmallCards)));
        items.Add(CreateMenuItem(global::mstscX.Services.L.T("Крупные карточки"), MenuSymbol.ViewAll, () => SelectCatalogView(CatalogView.LargeCards)));
    }

    private void SelectCatalogView(CatalogView view)
    {
        ShowSettings(false);
        SetCatalogView(view);
    }

    private static Microsoft.UI.Xaml.Controls.MenuFlyoutItem CreateMenuItem(string text, MenuSymbol icon, Action action)
    {
        var item = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = text, Icon = new SymbolIcon(icon) };
        item.Click += (_, _) => action();
        return item;
    }

    private static Microsoft.UI.Xaml.Controls.MenuFlyoutItem CreatePowerShellMenuItem(Func<Task> launch)
    {
        var item = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = PowerShellMenuText,
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE756" } // CommandPrompt
        };
        item.Click += async (_, _) => await launch();
        return item;
    }

    private bool openingPowerShell;

    private async Task OpenPowerShellAsync(ServerProfile? profile)
    {
        if (profile?.RemoteApp.Enabled == true || openingPowerShell)
            return;

        openingPowerShell = true;
        try
        {
            string? password = null;
            if (profile is not null)
            {
                string? untrustedHost = Services.WindowsPowerShell.GetUntrustedHost(profile);
                if (untrustedHost is not null)
                {
                    bool add = await DisplayAlertAsync(global::mstscX.Services.L.T("Подключение Windows PowerShell"),
                        global::mstscX.Services.L.F("Добавить сервер «{0}» ({1}) в TrustedHosts этого компьютера?\n\n", profile.Name, untrustedHost) +
                        global::mstscX.Services.L.T("Это разрешит подключение WinRM по HTTP с учётными данными сервера. ") +
                        global::mstscX.Services.L.T("Добавляйте только серверы, которым доверяете. Существующий список будет сохранён. ") +
                        global::mstscX.Services.L.T("Windows запросит права администратора; при необходимости будет запущена локальная служба WinRM."),
                        global::mstscX.Services.L.T("Добавить и подключиться"), global::mstscX.Services.L.T("Отмена"));
                    if (!add || !await Services.WindowsPowerShell.AddTrustedHostAsync(untrustedHost))
                        return;
                }
                try { password = await viewModel.ReadSessionPasswordAsync(profile); }
                catch (Exception ex) when (ex is FormatException or UnauthorizedAccessException or
                    System.Runtime.InteropServices.COMException or ArgumentException)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            }
            await Services.WindowsPowerShell.OpenAsync(profile, password);
        }
        catch (Exception ex) when (ex is FormatException or System.ComponentModel.Win32Exception or
            InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await DisplayAlertAsync("Windows PowerShell", ex.Message, global::mstscX.Services.L.T("ОК"));
        }
        finally { openingPowerShell = false; }
    }

    private void SetCatalogView(CatalogView view)
    {
        catalogView = view;
        Preferences.Default.Set(CatalogViewKey, (int)view);
        UpdateCatalogGroupStyle();
        ServerCatalog.ItemTemplate = (DataTemplate)Resources[view switch
        {
            CatalogView.SmallCards => "SmallServerCard",
            CatalogView.LargeCards => "LargeServerCard",
            _ => "ServerListItem"
        }];
        UpdateCatalogLayout();
    }

    private void UpdateCatalogGroupStyle()
    {
        (double iconSize, double fontSize) = catalogView switch
        {
            CatalogView.SmallCards => (36, 18),
            CatalogView.LargeCards => (52, 26),
            _ => (28, 18)
        };
        foreach (var group in viewModel.ProfileGroups)
            group.SetHeaderStyle(iconSize, fontSize);
    }

    private void OnCatalogSizeChanged(object? sender, EventArgs e) => UpdateCatalogLayout();

    private void UpdateCatalogLayout()
    {
        if (catalogView == CatalogView.List)
        {
            if (ServerCatalog.ItemsLayout is not LinearItemsLayout)
                ServerCatalog.ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical) { ItemSpacing = 8 };
            return;
        }

        double cardWidth = catalogView == CatalogView.SmallCards ? 200 : 320;
        int columns = Math.Max(1, (int)((ServerCatalog.Width + 8) / (cardWidth + 8)));
        if (ServerCatalog.ItemsLayout is GridItemsLayout layout)
            layout.Span = columns;
        else
            ServerCatalog.ItemsLayout = new GridItemsLayout(columns, ItemsLayoutOrientation.Vertical)
            {
                HorizontalItemSpacing = 8,
                VerticalItemSpacing = 8
            };
    }

    private readonly Dictionary<Guid, WinUI.NativeSessionWindow> nativeSessions = [];
    private readonly Dictionary<Guid, Views.SessionPage> embeddedSessions = [];
    private readonly Dictionary<Guid, WinUI.RdpSession> remoteAppSessions = [];
    private readonly Dictionary<Guid, (string Address, string Message)> connectionErrors = [];
    private readonly Dictionary<Guid, string> reconnectStatuses = [];
    private Microsoft.Maui.Controls.Window? remoteAppOwner;
    private Microsoft.UI.Xaml.Window? nativeMainWindow;
    private Views.SessionPage? visibleEmbeddedSession;
    private bool embeddedFullScreen;
    private bool confirmingMainWindowClose;
    private bool mainWindowCloseApproved;
    private WinUI.MainWindowPlacement? mainWindowPlacement;
    private WinUI.MainWindowFullScreenKey? mainWindowFullScreenKey;

    internal void WatchMainWindowClosing(Microsoft.Maui.Controls.Window window)
    {
        window.HandlerChanged += (_, _) => AttachWindow();
        window.Created += (_, _) => { AttachWindow(); mainWindowPlacement?.Restore(); };
        window.Destroying += (_, _) => DetachWindow();
        AttachWindow();

        void AttachWindow()
        {
            var nativeWindow = window.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            if (nativeMainWindow == nativeWindow)
                return;

            DetachWindow();
            nativeMainWindow = nativeWindow;
            if (nativeMainWindow is { } owner)
            {
                owner.AppWindow.Closing += OnMainWindowClosing;
                mainWindowPlacement = new WinUI.MainWindowPlacement(owner.AppWindow,
                    () => embeddedFullScreen || mainWindowPresenter is not null);
                nint handle = WinRT.Interop.WindowNative.GetWindowHandle(owner);
                mainWindowFullScreenKey = new WinUI.MainWindowFullScreenKey(handle, owner.DispatcherQueue,
                    foreground => !embeddedFullScreen && (foreground == handle || mainFullScreenMenu?.OwnsWindow(foreground) == true),
                    ToggleMainWindowFullScreen);
            }
        }

        void DetachWindow()
        {
            mainWindowFullScreenKey?.Dispose();
            mainWindowFullScreenKey = null;
            mainWindowPlacement?.Dispose();
            mainWindowPlacement = null;
            if (nativeMainWindow is { } owner)
                owner.AppWindow.Closing -= OnMainWindowClosing;
            nativeMainWindow = null;
        }
    }

    private void OnMainWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (mainWindowCloseApproved)
        {
            mainWindowPlacement?.Save();
            return;
        }

        args.Cancel = true;
        RequestMainWindowClose();
    }

    private int OpenSessionCount => nativeSessions.Count + embeddedSessions.Count + remoteAppSessions.Count;

    private void RequestMainWindowClose()
    {
        if (mainWindowCloseApproved || confirmingMainWindowClose ||
            nativeMainWindow is not { } owner)
            return;

        confirmingMainWindowClose = owner.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                int sessionCount = OpenSessionCount;
                visibleEmbeddedSession?.SetEmbeddedVisible(false);
                if (sessionCount > 0 && !await DisplayAlertAsync(global::mstscX.Services.L.T("Закрыть приложение?"), global::mstscX.Services.L.F("Открытых сеансов: {0}. При закрытии они будут отключены.", sessionCount),
                    global::mstscX.Services.L.T("Отключить и закрыть"), global::mstscX.Services.L.T("Отмена")))
                    return;

                mainWindowCloseApproved = true;
                mainWindowPlacement?.Save();
                // This callback is already outside a menu Click handler. Close
                // the owned WinUI window before its owner shuts down the queue.
                mainFullScreenMenu?.CloseBeforeOwner();
                mainFullScreenMenu = null;

                foreach (var session in embeddedSessions.Values.ToArray())
                    session.EndSession(disconnect: false);
                foreach (var session in nativeSessions.Values.ToArray())
                    session.CloseConnection();
                embeddedSessions.Clear();
                OnRemoteAppOwnerDestroying(null, EventArgs.Empty);
                owner.Close();
            }
            finally
            {
                confirmingMainWindowClose = false;
                if (!mainWindowCloseApproved)
                    UpdateSelectedSession();
            }
        });
    }

    private void UpdateSelectedSession()
    {
        bool showServers = activeNavigation == ServersNavigation;
        Guid? selected = viewModel.SelectedProfile?.Id;
        var nextSession = embeddedFullScreen ? visibleEmbeddedSession :
            showServers && selected is { } embeddedId ? embeddedSessions.GetValueOrDefault(embeddedId) : null;
        bool showNotice = !embeddedFullScreen && showServers;
        var remoteApp = showNotice && selected is { } remoteId ? remoteAppSessions.GetValueOrDefault(remoteId) : null;
        bool showExternal = showNotice && selected is { } externalId &&
            nativeSessions.GetValueOrDefault(externalId) is { HasEnded: false };

        if (showExternal)
            nextSession = null;

        if (visibleEmbeddedSession != nextSession && visibleEmbeddedSession is { } previous)
        {
            previous.SetEmbeddedVisible(false);
            previous.IsVisible = false;
        }

        visibleEmbeddedSession = nextSession;
        RdpPanel.IsVisible = showServers || embeddedFullScreen;
        EmbeddedSessionHost.IsVisible = nextSession is not null;
        string? reconnectStatus = selected is { } selectedId ? reconnectStatuses.GetValueOrDefault(selectedId) : null;
        (string Address, string Message)? connectionError =
            selected is { } errorId && connectionErrors.TryGetValue(errorId, out var error) ? error : null;
        bool isActive = viewModel.SelectedProfile?.IsSessionActive == true;
        SessionNotice.IsVisible = showServers && !embeddedFullScreen && nextSession is null;
        SessionElementIcon.BindingContext = viewModel.SelectedProfile;
        SessionElementIconHost.BindingContext = viewModel.SelectedProfile;
        SessionElementIconHost.IsVisible = selected.HasValue;
        SessionErrorIcon.IsVisible = reconnectStatus is not null ||
            (!showExternal && remoteApp is null && connectionError is not null);
        DisconnectedSessionActions.IsVisible = SessionNotice.IsVisible &&
            selected.HasValue && !showExternal && remoteApp is null;
        ActivateExternalSession.IsVisible = showExternal || remoteApp?.IsRemoteAppStarted == true;
        ActivateExternalSession.Text = remoteApp is null ? global::mstscX.Services.L.T("Перейти к подключению") : global::mstscX.Services.L.T("Перейти к приложению");
        DisconnectRemoteApp.IsVisible = remoteApp is not null;
        SessionMessage.Text = reconnectStatus ?? (remoteApp is not null
            ? remoteApp.IsRemoteAppStarted ? global::mstscX.Services.L.T("Удалённое приложение запущено.") : global::mstscX.Services.L.T("Запуск удалённого приложения…") :
            showExternal ? isActive ? global::mstscX.Services.L.T("Подключение открыто в отдельном окне.") : global::mstscX.Services.L.T("Подключение в отдельном окне…") :
            selected is null ? global::mstscX.Services.L.T("Выберите сервер.") :
            connectionError?.Message ?? global::mstscX.Services.L.T("Нет подключения."));
        string? address = connectionError?.Address;
        if (showExternal || remoteApp is not null)
            address = isActive && reconnectStatus is null ? null : viewModel.SelectedProfile?.Address;
        SessionNoticeEndpoint.IsVisible = address is not null;
        SessionNoticeEndpoint.Text = address is null ? null : global::mstscX.Services.L.F("Адрес: {0}", RdpEndpoint.FormatForDisplay(address));
        UpdateServerPanelWidth();

        if (nextSession is not null)
        {
            nextSession.IsVisible = true;
            nextSession.SetEmbeddedVisible(!confirmingMainWindowClose);
        }
    }

    private void OnMainLayoutSizeChanged(object? sender, EventArgs e) => UpdateServerPanelWidth();

    private void UpdateServerPanelWidth()
    {
        bool show = activeNavigation == ServersNavigation && !embeddedFullScreen;
        ServerPanelSplitter.IsVisible = show;
        MainLayout.ColumnDefinitions[2].Width = new GridLength(show ? SplitterWidth : 0);
        if (!show)
        {
            MainLayout.ColumnDefinitions[1].Width = new GridLength(0);
            return;
        }

        double available = ServerPanelAvailableWidth;
        double requested = serverPanelWidth > 0 ? serverPanelWidth : available * 2 / 15;
        double width = ClampServerPanelWidth(requested, available);
        MainLayout.ColumnDefinitions[1].Width = new GridLength(serverPanelWidth > 0 ? width :
            Math.Min(width * 1.10, available - Math.Min(220, available / 2)));
    }

    private double ServerPanelAvailableWidth =>
        Math.Max(0, MainLayout.Width - NavigationHost.WidthRequest - SplitterWidth);

    private static double ClampServerPanelWidth(double requested, double available)
    {
        double maximum = Math.Max(0, available - Math.Min(220, available / 2));
        return Math.Clamp(requested, Math.Min(100, maximum), maximum);
    }

    private void OnServerPanelDragStarted(object? sender, EventArgs e) =>
        serverPanelWidth = SessionLayout.Width;

    private void OnServerPanelDragDelta(object? sender, double delta)
    {
        serverPanelWidth = ClampServerPanelWidth(serverPanelWidth + delta, ServerPanelAvailableWidth);
        UpdateServerPanelWidth();
        MainLayout.InvalidateMeasure();
    }

    private void OnServerPanelDragCompleted(object? sender, EventArgs e) =>
        Preferences.Default.Set(ServerPanelWidthKey, serverPanelWidth);

    private void OnActivateSelectedSession(object? sender, EventArgs e)
    {
        if (viewModel.SelectedProfile is { } profile)
            TryActivateExistingSession(profile.Id);
    }

    private bool TryActivateExistingSession(Guid profileId)
    {
        if (nativeSessions.TryGetValue(profileId, out var native))
        {
            if (!native.HasEnded)
            {
                native.Activate();
                return true;
            }
            native.CloseConnection();
        }
        if (remoteAppSessions.TryGetValue(profileId, out var remoteApp))
        {
            if (remoteApp.IsRemoteAppStarted && !remoteApp.TryActivateRemoteAppWindow(remoteAppSessions.Count == 1))
                SessionMessage.Text = global::mstscX.Services.L.T("Окно RemoteApp пока недоступно. Проверьте, открыто ли приложение.");
            return true;
        }


        if (embeddedSessions.TryGetValue(profileId, out var embeddedSession) && !embeddedSession.HasEnded)
        {
            ShowSettings(false);
            ActivateSessionWindow(Window);
            embeddedSession.ActivateEmbeddedSession();
            return true;
        }

        return false;
    }

    private void Connect(string address, ServerProfile profile, string? password)
    {
        // Leave the WinUI menu or double-tap callback before opening another HWND.
        if (Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow ||
            !nativeWindow.DispatcherQueue.TryEnqueue(() => OpenSession(address, profile, password)))
        {
            ShowConnectionError(profile, global::mstscX.Services.L.T("Не удалось открыть окно подключения."));
        }
    }

    private void OpenSession(string address, ServerProfile profile, string? password)
    {
        if (Window is not { Handler.PlatformView: Microsoft.UI.Xaml.Window nativeWindow } owner)
            return;

        Guid profileId = profile.Id;
        if (TryActivateExistingSession(profileId))
            return;

        connectionErrors.Remove(profileId);
        reconnectStatuses.Remove(profileId);
        if (embeddedSessions.TryGetValue(profileId, out var endedSession))
            CloseEmbeddedSession(profileId, endedSession);

        if (profile.RemoteApp.Enabled)
        {
            OpenRemoteAppSession(owner, nativeWindow, address, profile, password);
            return;
        }

        var windowLayout = Services.SessionWindowSettings.Load(profile);
        if (!profile.OpenInSeparateWindow)
        {
            ShowSettings(false);
            Views.SessionPage? session = null;
            session = new Views.SessionPage(address, profile, windowLayout, password, () => viewModel.ReadSessionPasswordAsync(profile),
                () => Window, () =>
                {
                    if (embeddedSessions.TryGetValue(profileId, out var current) && current == session)
                        CloseEmbeddedSession(profileId, current);
                }, SetEmbeddedFullScreen, error =>
                {
                    if (!embeddedSessions.TryGetValue(profileId, out var ended) || ended != session)
                        return;

                    if (error is not null)
                        connectionErrors[profileId] = (address, error);
                    CloseEmbeddedSession(profileId, ended);
                }, onSessionConnected: () => ReportSessionActive(profileId),
                onReconnectStatusChanged: message => ReportReconnectStatus(profileId, message))
            {
                IsVisible = false
            };
            session.SetEmbeddedVisible(false);
            embeddedSessions.Add(profileId, session);
            EmbeddedSessionHost.Children.Add(session);
            UpdateSelectedSession();
            return;
        }

        OpenNativeSession(nativeWindow, address, profile, windowLayout, password);
    }

    private void OpenNativeSession(Microsoft.UI.Xaml.Window owner, string address, ServerProfile profile,
        Services.SessionWindowLayout layout, string? password)
    {
        WinUI.NativeSessionWindow? window = null;
        try
        {
            window = new WinUI.NativeSessionWindow(WinRT.Interop.WindowNative.GetWindowHandle(owner),
                owner.DispatcherQueue, address, profile, layout, () => viewModel.ReadSessionPasswordAsync(profile));
            var current = window;
            window.Connected += () => ReportSessionActive(profile.Id);
            window.ReconnectStatusChanged += message => ReportReconnectStatus(profile.Id, message);
            window.Ended += error =>
            {
                if (error is not null)
                    connectionErrors[profile.Id] = (address, error);
                reconnectStatuses.Remove(profile.Id);
                viewModel.SetSessionActive(profile.Id, false);
                UpdateSelectedSession();
            };
            window.Closed += () =>
            {
                if (nativeSessions.GetValueOrDefault(profile.Id) != current)
                    return;
                nativeSessions.Remove(profile.Id);
                reconnectStatuses.Remove(profile.Id);
                viewModel.SetSessionActive(profile.Id, false);
                UpdateSelectedSession();
            };
            nativeSessions.Add(profile.Id, window);
            window.ShowAndConnect(password);
            UpdateSelectedSession();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or
            System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException or
            DllNotFoundException or EntryPointNotFoundException)
        {
            window?.CloseConnection();
            ShowConnectionError(profile, global::mstscX.Services.L.F("Не удалось открыть окно Win32 (0x{0:X8}). {1}", ex.HResult, ex.Message));
        }
    }

    private void OpenRemoteAppSession(Microsoft.Maui.Controls.Window owner, Microsoft.UI.Xaml.Window nativeWindow,
        string address, ServerProfile profile, string? password)
    {
        WinUI.RdpSession? session = null;
        try
        {
            var workArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(nativeWindow.AppWindow.Id,
                Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
            session = new WinUI.RdpSession(WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow),
                (0, 0, 1, 1), (workArea.Width, workArea.Height),
                useNativeRemoteAppHost: true);
            if (remoteAppOwner is null)
            {
                remoteAppOwner = owner;
                owner.Destroying += OnRemoteAppOwnerDestroying;
            }

            remoteAppSessions.Add(profile.Id, session);
            string? iconPassword = profile.HasRemoteAppIcon ? null : password;
            session.Connected += () => nativeWindow.DispatcherQueue.TryEnqueue(() =>
            {
                if (remoteAppSessions.GetValueOrDefault(profile.Id) == session)
                {
                    session.WatchRemoteAppWindows(() => remoteAppSessions.Count == 1);
                    viewModel.SetSessionActive(profile.Id, true);
                    UpdateSelectedSession();
                    if (iconPassword is { } credentialPassword)
                    {
                        iconPassword = null;
                        _ = viewModel.LoadRemoteAppIconOnConnectAsync(profile, credentialPassword);
                    }
                }
            });
            session.Disconnected += message => nativeWindow.DispatcherQueue.TryEnqueue(() =>
            {
                if (remoteAppSessions.GetValueOrDefault(profile.Id) == session)
                {
                    if (message is not null)
                        connectionErrors[profile.Id] = (address, message);
                    CloseRemoteAppSession(profile.Id);
                }
            });
            session.ReconnectStatusChanged += message => nativeWindow.DispatcherQueue.TryEnqueue(() =>
            {
                if (remoteAppSessions.GetValueOrDefault(profile.Id) == session)
                    ReportReconnectStatus(profile.Id, message);
            });
            session.Connect(address, profile, password);
            UpdateSelectedSession();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            remoteAppSessions.Remove(profile.Id);
            session?.Dispose();
            ShowConnectionError(profile, global::mstscX.Services.L.F("Не удалось запустить RemoteApp (0x{0:X8}). {1}", ex.HResult, ex.Message));
        }
    }

    private void OnDisconnectRemoteApp(object? sender, EventArgs e)
    {
        if (viewModel.SelectedProfile is { } profile)
            RequestCloseRemoteAppSession(profile);
    }

    private async void RequestCloseRemoteAppSession(ServerProfile profile)
    {
        if (!remoteAppSessions.ContainsKey(profile.Id)) return;
        nint owner = nativeMainWindow is { } window ? WinRT.Interop.WindowNative.GetWindowHandle(window) : 0;
        if (await WinUI.ConnectionCloseConfirmation.ConfirmAsync(owner, profile.Name))
            CloseRemoteAppSession(profile.Id);
    }

    private void CloseRemoteAppSession(Guid profileId)
    {
        if (!remoteAppSessions.Remove(profileId, out var session))
            return;

        reconnectStatuses.Remove(profileId);
        viewModel.SetSessionActive(profileId, false);
        UpdateSelectedSession();
        session.Dispose();
    }

    private void OnRemoteAppOwnerDestroying(object? sender, EventArgs e)
    {
        if (remoteAppOwner is { } owner)
            owner.Destroying -= OnRemoteAppOwnerDestroying;
        remoteAppOwner = null;

        var sessions = remoteAppSessions.Values.ToArray();
        remoteAppSessions.Clear();
        foreach (var session in sessions)
            session.Dispose(disconnect: !mainWindowCloseApproved);
    }

    private static void ActivateSessionWindow(Microsoft.Maui.Controls.Window? window)
    {
        if (window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow)
            return;

        WinUI.RdpSession.TryActivateWindow(WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow));
        WinUI.FullScreenPanelDrag.EnsureWindowVisible(nativeWindow.AppWindow);
        nativeWindow.Activate();
    }

    private void CloseEmbeddedSession(Guid profileId, Views.SessionPage session)
    {
        embeddedSessions.Remove(profileId);
        reconnectStatuses.Remove(profileId);
        viewModel.SetSessionActive(profileId, false);
        EmbeddedSessionHost.Children.Remove(session);
        UpdateSelectedSession();
    }

    private void ReportSessionActive(Guid profileId)
    {
        viewModel.SetSessionActive(profileId, true);
        if (viewModel.SelectedProfile?.Id == profileId)
            UpdateSelectedSession();
    }

    private void ReportReconnectStatus(Guid profileId, string? message)
    {
        if (message is null)
            reconnectStatuses.Remove(profileId);
        else
            reconnectStatuses[profileId] = message;

        if (viewModel.SelectedProfile?.Id == profileId)
            UpdateSelectedSession();
    }

    private void ShowConnectionError(ServerProfile profile, string message)
    {
        connectionErrors[profile.Id] = (profile.Address, message);
        UpdateSelectedSession();
    }

    private void SetEmbeddedFullScreen(bool fullScreen)
    {
        embeddedFullScreen = fullScreen;
        UpdateMainFullScreenMenu();
        RdpPanel.StrokeThickness = fullScreen ? 0 : 1;
        RdpPanel.StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle
        {
            CornerRadius = fullScreen ? 0 : 10
        };
        NavigationPane.IsVisible = !fullScreen;
        NavigationHost.IsVisible = !fullScreen;
        bool showSettings = activeNavigation == SettingsNavigation;
        SessionLayout.IsVisible = !fullScreen && !showSettings;
        SettingsPanel.IsVisible = !fullScreen && showSettings;
        UpdateSelectedSession();
    }
}

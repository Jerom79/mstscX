namespace mstscX;

public partial class MainPage
{
    private const string CloseToTrayKey = "View.CloseToTray";
    private bool closeToTray = Preferences.Default.Get(CloseToTrayKey, false);
    private bool hiddenToTray;
    private WinUI.SystemTray? systemTray;

    private void InitializeTraySetting() => CloseToTraySwitch.IsToggled = closeToTray;

    private void OnCloseToTrayToggled(object? sender, ToggledEventArgs e)
    {
        closeToTray = e.Value;
        Preferences.Default.Set(CloseToTrayKey, e.Value);
        if (e.Value)
            EnsureSystemTray();
        else
        {
            if (hiddenToTray) ShowMainFromTray();
            systemTray?.Dispose();
            systemTray = null;
        }
    }

    private bool EnsureSystemTray()
    {
        if (nativeMainWindow is not { } owner) return false;
        systemTray ??= new WinUI.SystemTray(owner.DispatcherQueue, () => viewModel.Profiles,
            ShowMainFromTray, id =>
            {
                ShowMainFromTray();
                ActivateTaskbarProfile(id);
            }, () =>
            {
                ShowMainFromTray();
                RequestMainWindowClose();
            });
        return systemTray.EnsureVisible();
    }

    private void RequestCaptionClose()
    {
        if (confirmingMainWindowClose || mainWindowCloseApproved) return;
        if (closeToTray && EnsureSystemTray() && nativeMainWindow is { } owner)
        {
            // Leave the fullscreen menu Click handler before destroying its window.
            owner.DispatcherQueue.TryEnqueue(() => HideMainToTray());
            return;
        }
        RequestMainWindowClose();
    }

    private void HideMainToTray()
    {
        if (!closeToTray || confirmingMainWindowClose || mainWindowCloseApproved ||
            nativeMainWindow is not { } owner || !EnsureSystemTray()) return;
        mainWindowPlacement?.Save();
        hiddenToTray = true;
        mainFullScreenMenu?.Dispose();
        mainFullScreenMenu = null;
        visibleEmbeddedSession?.SetEmbeddedVisible(false);
        owner.AppWindow.Hide();
    }

    private void ShowMainFromTray()
    {
        if (nativeMainWindow is not { } owner || mainWindowCloseApproved) return;
        bool wasHidden = hiddenToTray;
        hiddenToTray = false;
        owner.AppWindow.Show();
        ActivateSessionWindow(Window);
        if (wasHidden)
        {
            UpdateMainFullScreenMenu();
            UpdateSelectedSession();
        }
    }
}

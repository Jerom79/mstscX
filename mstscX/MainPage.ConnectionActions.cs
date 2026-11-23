namespace mstscX;

public partial class MainPage
{
    private bool closingAllConnections;

    private void OnExitApplicationClicked(object? sender, EventArgs e) => RequestMainWindowClose();

    private async void OnDisconnectAllClicked(object? sender, EventArgs e)
    {
        if (closingAllConnections || confirmingMainWindowClose || OpenSessionCount == 0) return;
        closingAllConnections = true;
        try
        {
            visibleEmbeddedSession?.SetEmbeddedVisible(false);
            if (WinUI.ConnectionCloseConfirmation.Enabled &&
                !await DisplayAlertAsync(global::mstscX.Services.L.T("Закрыть все соединения?"),
                    global::mstscX.Services.L.F("Открытых сеансов: {0}. Все они будут отключены.", OpenSessionCount), global::mstscX.Services.L.T("Отключить"), global::mstscX.Services.L.T("Отмена")))
                return;

            foreach (var session in embeddedSessions.Values.ToArray()) session.EndSession();
            foreach (var session in nativeSessions.Values.ToArray()) session.CloseConnection();
            foreach (var id in remoteAppSessions.Keys.ToArray()) CloseRemoteAppSession(id);
        }
        finally
        {
            closingAllConnections = false;
            UpdateSelectedSession();
        }
    }
}

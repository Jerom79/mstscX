using System.ComponentModel;

namespace mstscX.ViewModels;

public sealed class SessionViewModel(string displayAddress) : INotifyPropertyChanged
{
    private string status = "Подключение…";
    private string? reconnectStatus;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsConnected { get; private set; }
    public bool IsSessionNoticeVisible => !IsConnected;
    public string Status => reconnectStatus ?? status;
    public bool IsReconnecting => reconnectStatus is not null;

    internal void ReportConnected() => Update(true, $"Подключено к {displayAddress}");

    internal void ReportDisconnected(string message) => Update(false, message);

    internal void ReportReconnectStatus(string? message)
    {
        if (reconnectStatus == message)
            return;

        reconnectStatus = message;
        PropertyChanged?.Invoke(this, new(nameof(Status)));
        PropertyChanged?.Invoke(this, new(nameof(IsReconnecting)));
    }

    private void Update(bool connected, string status)
    {
        IsConnected = connected;
        this.status = status;
        reconnectStatus = null;
        PropertyChanged?.Invoke(this, new(nameof(IsConnected)));
        PropertyChanged?.Invoke(this, new(nameof(IsSessionNoticeVisible)));
        PropertyChanged?.Invoke(this, new(nameof(Status)));
        PropertyChanged?.Invoke(this, new(nameof(IsReconnecting)));
    }
}

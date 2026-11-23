using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;

namespace mstscX.Models;

public sealed class ServerProfileGroup(bool isRemoteApp, bool isExpanded, IEnumerable<ServerProfile> profiles, ICommand toggleCommand)
    : ObservableCollection<ServerProfile>(isExpanded ? profiles : [])
{
    public bool IsRemoteApp { get; } = isRemoteApp;
    public double HeaderIconSize { get; private set; } = 38;
    public double HeaderFontSize { get; private set; } = 20;
    public string Title => GetTitle(IsRemoteApp);
    internal static string GetTitle(bool isRemoteApp) => isRemoteApp ? "RemoteApp" : global::mstscX.Services.L.T("Удалённые рабочие столы");
    public bool IsExpanded { get; private set; } = isExpanded;
    public int ProfileCount { get; } = profiles.Count();
    public ICommand ToggleCommand { get; } = toggleCommand;
    public string ToggleDescription => IsExpanded
        ? global::mstscX.Services.L.F("Свернуть группу «{0}»", Title)
        : global::mstscX.Services.L.F("Развернуть группу «{0}» ({1})", Title, ProfileCount);

    internal void SetHeaderStyle(double iconSize, double fontSize)
    {
        if (HeaderIconSize == iconSize && HeaderFontSize == fontSize)
            return;

        HeaderIconSize = iconSize;
        HeaderFontSize = fontSize;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(HeaderIconSize)));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(HeaderFontSize)));
    }

    internal void SetExpanded(bool expanded, IEnumerable<ServerProfile> profiles)
    {
        IsExpanded = expanded;
        if (expanded)
        {
            foreach (var profile in profiles)
                Add(profile);
        }
        else
        {
            Clear();
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsExpanded)));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(ToggleDescription)));
    }
}

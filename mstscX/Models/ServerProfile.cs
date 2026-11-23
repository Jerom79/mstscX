using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace mstscX.Models;

public enum ServerCardState { Idle, Selected, Active }

public sealed class ServerProfile(string name, string address, bool trustServer) : INotifyPropertyChanged
{
    private bool isSelected;
    private bool isSessionActive;
    private bool isRenaming;
    private string? remoteAppIconFile;

    public event PropertyChangedEventHandler? PropertyChanged;

    [JsonInclude]
    public Guid Id { get; internal set; }
    public string Name { get; } = name;
    public string Address { get; } = address;
    public bool TrustServer { get; } = trustServer;
    public bool OpenInSeparateWindow { get; init; } = true;
    public string UserName { get; init; } = "";
    public string? EncryptedPassword { get; init; }
    public RdpDisplayOptions Display { get; init; } = new();
    public RdpLocalResources Resources { get; init; } = new();
    public RdpRemoteAppOptions RemoteApp { get; init; } = new();
    public RdpGatewayOptions Gateway { get; init; } = new();

    [JsonIgnore]
    public string? RemoteAppIconFile
    {
        get => remoteAppIconFile;
        internal set
        {
            if (remoteAppIconFile == value)
                return;

            remoteAppIconFile = value;
            PropertyChanged?.Invoke(this, new(nameof(RemoteAppIcon)));
            PropertyChanged?.Invoke(this, new(nameof(HasRemoteAppIcon)));
        }
    }

    [JsonIgnore]
    public bool HasRemoteAppIcon => RemoteApp.Enabled && RemoteAppIconFile is not null;

    [JsonIgnore]
    public ImageSource? RemoteAppIcon => RemoteAppIconFile is { } path
        ? ImageSource.FromStream(() => File.OpenRead(path)) : null;

    [JsonIgnore]
    public bool IsSelected { get => isSelected; internal set => SetFlag(ref isSelected, value); }

    [JsonIgnore]
    public bool IsSessionActive { get => isSessionActive; internal set => SetFlag(ref isSessionActive, value); }

    [JsonIgnore]
    public ServerCardState CardState => IsSelected ? ServerCardState.Selected :
        IsSessionActive ? ServerCardState.Active : ServerCardState.Idle;

    [JsonIgnore]
    public bool IsRenaming { get => isRenaming; internal set => SetFlag(ref isRenaming, value); }

    internal ServerProfile WithName(string newName) => new(newName, Address, TrustServer)
    {
        Id = Id,
        OpenInSeparateWindow = OpenInSeparateWindow,
        UserName = UserName,
        EncryptedPassword = EncryptedPassword,
        Display = Display,
        Resources = Resources,
        RemoteApp = RemoteApp,
        Gateway = Gateway,
        RemoteAppIconFile = RemoteAppIconFile,
        IsSessionActive = IsSessionActive
    };

    private void SetFlag(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(IsSelected) or nameof(IsSessionActive))
            PropertyChanged?.Invoke(this, new(nameof(CardState)));
    }

    public override string ToString() => $"{Name} ({Address})";
}

public sealed record RdpGatewayOptions
{
    public int UsageMethod { get; init; }
    public string HostName { get; init; } = "";
    public bool UseExplicitProfile { get; init; } = true;
    public int CredentialSource { get; init; }
    public bool ShareCredentials { get; init; }

    internal bool UsesConfiguredGateway => UsageMethod is 1 or 2;
    internal bool HasValidHost => RdpEndpoint.TryParse(HostName, out _);

    internal bool Matches(RdpGatewayOptions other)
    {
        if (UsageMethod is 0 or 4)
            return other.UsageMethod is 0 or 4;

        return UsageMethod == other.UsageMethod &&
            (!UsesConfiguredGateway ||
             (string.Equals(HostName, other.HostName, StringComparison.OrdinalIgnoreCase) &&
              UseExplicitProfile == other.UseExplicitProfile && CredentialSource == other.CredentialSource &&
              ShareCredentials == other.ShareCredentials));
    }
}

public sealed record RdpRemoteAppOptions
{
    public bool Enabled { get; set; }
    public string Program { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string FeedAddress { get; set; } = "";
    public bool UseRdWeb { get; set; }
    public string IconKey { get; set; } = "";
}

public sealed record RdpDisplayOptions
{
    public int Resolution { get; set; }
    public int Colors { get; set; }
    public bool FullScreen { get; set; }
    public bool UseMultimon { get; set; }
    public int ConnectionType { get; set; }
    public bool DisableWallpaper { get; set; }
    public bool DisableFullWindowDrag { get; set; }
    public bool DisableMenuAnimations { get; set; }
    public bool FontSmoothing { get; set; }
    public bool VideoPlayback { get; set; } = true;
}

public sealed record RdpLocalResources
{
    public int AudioPlayback { get; set; }
    public bool AudioRecording { get; set; }
    public int Keyboard { get; set; } = 2;
    public bool Printers { get; set; } = true;
    public bool Clipboard { get; set; } = true;
    public bool Drives { get; set; }
    public string DriveTargets { get; set; } = "*";
    public int AudioQuality { get; set; }
    public bool Ports { get; set; }
    public bool SmartCards { get; set; }
    public bool Devices { get; set; }
    public bool PointOfSaleDevices { get; set; }

    internal const string DynamicDrives = "DynamicDrives";
    internal string EffectiveDriveTargets => Drives ? DriveTargets : "";

    internal static string NormalizeDriveTargets(string value)
    {
        var targets = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (targets.Contains("*", StringComparer.Ordinal))
            return "*";

        return string.Join(';', targets.Select(target =>
        {
            if (target.Equals(DynamicDrives, StringComparison.OrdinalIgnoreCase))
                return DynamicDrives;

            target = target.TrimEnd('\\');
            return target.Length == 2 && char.IsAsciiLetter(target[0]) && target[1] == ':'
                ? target.ToUpperInvariant() + "\\" : "";
        }).Where(target => target.Length > 0).Distinct().Order(StringComparer.Ordinal));
    }
}

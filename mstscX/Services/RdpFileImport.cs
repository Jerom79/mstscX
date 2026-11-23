using System.Text;
using mstscX.Models;

namespace mstscX.Services;

internal static class RdpFileImport
{
    public static bool MatchesProfile(ServerProfile imported, ServerProfile saved) =>
        RdpEndpoint.TryParse(imported.Address, out var importedEndpoint) &&
        RdpEndpoint.TryParse(saved.Address, out var savedEndpoint) &&
        importedEndpoint == savedEndpoint &&
        string.Equals(imported.UserName?.Trim() ?? "", saved.UserName?.Trim() ?? "", StringComparison.OrdinalIgnoreCase) &&
        (imported.Display with { FullScreen = imported.Display.FullScreen || imported.Display.UseMultimon }) ==
            (saved.Display with { FullScreen = saved.Display.FullScreen || saved.Display.UseMultimon }) &&
        (imported.Resources with { Drives = false, DriveTargets = "" }) ==
            (saved.Resources with { Drives = false, DriveTargets = "" }) &&
        imported.Resources.EffectiveDriveTargets == saved.Resources.EffectiveDriveTargets &&
        imported.Gateway.Matches(saved.Gateway) &&
        imported.RemoteApp.Enabled == saved.RemoteApp.Enabled &&
        (!imported.RemoteApp.Enabled ||
            (string.Equals(imported.RemoteApp.Program, saved.RemoteApp.Program, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(imported.RemoteApp.WorkingDirectory, saved.RemoteApp.WorkingDirectory, StringComparison.OrdinalIgnoreCase) &&
             imported.RemoteApp.Arguments == saved.RemoteApp.Arguments));

    public static async Task<ServerProfile> ReadAsync(Stream stream, string name)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadLineAsync() is { } line)
        {
            var parts = line.Split(':', 3);
            if (parts.Length == 3 && parts[1] is "s" or "i")
                settings[parts[0].Trim()] = parts[2].Trim();
        }

        string? Get(string key) => settings.GetValueOrDefault(key);
        int Number(string key, int fallback = 0) =>
            int.TryParse(Get(key), out int number) ? number : fallback;

        if (!RdpEndpoint.TryParse(Get("full address"), out var endpoint))
            throw new FormatException(global::mstscX.Services.L.T("В файле RDP нет корректного адреса подключения."));

        int port = Number("server port");
        if (endpoint.Port == RdpEndpoint.DefaultPort && port is > 0 and <= 65535)
            endpoint = endpoint with { Port = port };

        string user = Get("username") ?? "";
        if (Get("domain") is { Length: > 0 } domain && user.Length > 0 && !user.Contains('\\') && !user.Contains('@'))
            user = $"{domain}\\{user}";
        string driveTargets = Get("drivestoredirect") is { } drives
            ? RdpLocalResources.NormalizeDriveTargets(drives)
            : Number("redirectdrives") == 1 ? "*" : "";

        return new ServerProfile(name, endpoint.ToString(), false)
        {
            UserName = user,
            Gateway = new()
            {
                UsageMethod = Math.Clamp(Number("gatewayusagemethod"), 0, 4),
                HostName = Get("gatewayhostname") ?? "",
                UseExplicitProfile = Number("gatewayprofileusagemethod", 1) == 1,
                CredentialSource = Math.Clamp(Number("gatewaycredentialssource"), 0, 5),
                ShareCredentials = Number("promptcredentialonce", 1) == 1
            },
            RemoteApp = new()
            {
                Enabled = Number("remoteapplicationmode") == 1,
                Program = Get("remoteapplicationprogram") ?? "",
                Arguments = Get("remoteapplicationcmdline") ?? "",
                WorkingDirectory = Get("shell working directory") ?? ""
            },
            Display = new()
            {
                Resolution = (Number("desktopwidth"), Number("desktopheight")) switch
                {
                    (1024, 768) => 1, (1280, 720) => 2, (1600, 900) => 3,
                    (1920, 1080) => 4, (2560, 1440) => 5, _ => 0
                },
                Colors = Number("session bpp") switch { 24 => 1, 16 => 2, 15 => 3, _ => 0 },
                FullScreen = Number("screen mode id") == 2,
                UseMultimon = Number("use multimon") == 1,
                ConnectionType = Math.Clamp(Number("connection type"), 0, 7),
                DisableWallpaper = Number("disable wallpaper") == 1,
                DisableFullWindowDrag = Number("disable full window drag") == 1,
                DisableMenuAnimations = Number("disable menu anims") == 1,
                FontSmoothing = Number("allow font smoothing") == 1,
                VideoPlayback = Number("videoplaybackmode", 1) == 1
            },
            Resources = new()
            {
                AudioPlayback = Math.Clamp(Number("audiomode"), 0, 2),
                AudioRecording = Number("audiocapturemode") == 1,
                Keyboard = Math.Clamp(Number("keyboardhook", 2), 0, 2),
                Printers = Number("redirectprinters", 1) == 1,
                Clipboard = Number("redirectclipboard", 1) == 1,
                Drives = driveTargets.Length > 0,
                DriveTargets = driveTargets.Length > 0 ? driveTargets : "*",
                AudioQuality = Math.Clamp(Number("audioqualitymode"), 0, 2),
                Ports = Number("redirectcomports") == 1,
                SmartCards = Number("redirectsmartcards") == 1,
                Devices = Get("devicestoredirect") == "*",
                PointOfSaleDevices = Number("redirectposdevices") == 1
            }
        };
    }
}

using System.Globalization;
using System.Text;
using mstscX.Models;

namespace mstscX.Services;

internal static class RdpFileExport
{
    internal static string FormatProfile(ServerProfile profile)
    {
        if (!RdpEndpoint.TryParse(profile.Address, out var endpoint))
            throw new FormatException("В карточке указан некорректный адрес RDP.");

        var content = new StringBuilder();
        void Text(string key, string? value)
        {
            if (value?.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new FormatException($"Параметр RDP «{key}» содержит недопустимый перенос строки или нулевой символ.");

            content.Append(key).Append(":s:").Append(value).Append("\r\n");
        }

        void Number(string key, int value) =>
            content.Append(key).Append(":i:").Append(value.ToString(CultureInfo.InvariantCulture)).Append("\r\n");

        static int Flag(bool value) => value ? 1 : 0;

        string address = endpoint.Host.Contains(':') ? $"[{endpoint.Host}]" : endpoint.Host;
        if (endpoint.Port != RdpEndpoint.DefaultPort)
            address += $":{endpoint.Port.ToString(CultureInfo.InvariantCulture)}";

        Text("full address", address);
        Text("username", profile.UserName);
        var gateway = profile.Gateway;
        Text("gatewayhostname", gateway.HostName);
        Number("gatewayusagemethod", gateway.UsageMethod);
        Number("gatewayprofileusagemethod", Flag(gateway.UseExplicitProfile));
        Number("gatewaycredentialssource", gateway.CredentialSource);
        Number("promptcredentialonce", Flag(gateway.ShareCredentials));
        var display = profile.Display;
        Number("screen mode id", display.FullScreen || display.UseMultimon ? 2 : 1);
        Number("use multimon", Flag(display.UseMultimon));
        if (display.ConnectionType != 0)
            Number("connection type", display.ConnectionType);
        Number("disable wallpaper", Flag(display.DisableWallpaper));
        Number("disable full window drag", Flag(display.DisableFullWindowDrag));
        Number("disable menu anims", Flag(display.DisableMenuAnimations));
        Number("allow font smoothing", Flag(display.FontSmoothing));
        Number("videoplaybackmode", Flag(display.VideoPlayback));
        Number("session bpp", profile.Display.Colors switch { 1 => 24, 2 => 16, 3 => 15, _ => 32 });
        (int width, int height) = profile.Display.Resolution switch
        {
            1 => (1024, 768), 2 => (1280, 720), 3 => (1600, 900),
            4 => (1920, 1080), 5 => (2560, 1440), _ => (0, 0)
        };
        if (width != 0)
        {
            Number("desktopwidth", width);
            Number("desktopheight", height);
        }

        var resources = profile.Resources;
        Number("audiomode", resources.AudioPlayback);
        Number("audiocapturemode", Flag(resources.AudioRecording));
        Number("audioqualitymode", resources.AudioQuality);
        Number("keyboardhook", resources.Keyboard);
        Number("redirectprinters", Flag(resources.Printers));
        Number("redirectclipboard", Flag(resources.Clipboard));
        string driveTargets = resources.EffectiveDriveTargets;
        Number("redirectdrives", Flag(driveTargets == "*"));
        Text("drivestoredirect", driveTargets);
        Number("redirectcomports", Flag(resources.Ports));
        Number("redirectsmartcards", Flag(resources.SmartCards));
        Text("devicestoredirect", resources.Devices ? "*" : "");
        Number("redirectposdevices", Flag(resources.PointOfSaleDevices));

        var app = profile.RemoteApp;
        Number("remoteapplicationmode", Flag(app.Enabled));
        if (app.Enabled)
        {
            Text("remoteapplicationprogram", app.Program);
            Text("remoteapplicationcmdline", app.Arguments);
            Text("shell working directory", app.WorkingDirectory);
        }

        return content.ToString();
    }

    internal static async Task WriteAsync(string path, string content)
    {
        string temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, Encoding.Unicode);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }
    }
}

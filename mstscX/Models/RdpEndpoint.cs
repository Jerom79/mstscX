using System.Globalization;
using System.Net;

namespace mstscX.Models;

internal readonly record struct RdpEndpoint(string Host, int Port)
{
    public const int DefaultPort = 3389;

    public static bool TryParse(string? value, out RdpEndpoint endpoint)
    {
        endpoint = default;
        string host = value?.Trim() ?? "";
        int separator = host.IndexOf(':');
        string? portText = null;
        if (host.StartsWith('['))
        {
            int end = host.IndexOf(']');
            if (end < 0)
                return false;
            if (end + 1 < host.Length)
            {
                if (host[end + 1] != ':')
                    return false;
                portText = host[(end + 2)..];
            }
            host = host[1..end];
            if (Uri.CheckHostName(host) != UriHostNameType.IPv6)
                return false;
        }
        else if (separator >= 0 && separator == host.LastIndexOf(':'))
        {
            portText = host[(separator + 1)..];
            host = host[..separator];
        }

        int port = DefaultPort;
        if (portText is not null &&
            (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
            return false;

        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
            return false;

        host = IPAddress.TryParse(host, out var ip) ? ip.ToString() : host.TrimEnd('.').ToLowerInvariant();
        endpoint = new(host, port);
        return true;
    }

    public override string ToString() => Port == DefaultPort ? Host :
        Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    public static string FormatForDisplay(string address) =>
        TryParse(address, out var endpoint) ? endpoint.ToString() : address;
}

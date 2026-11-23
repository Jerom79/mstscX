using System.Net;
using System.Xml;
using System.Xml.Linq;
using mstscX.Models;

namespace mstscX.Services;

public sealed record PublishedRemoteApp(string Name, string Alias, Uri? RdpFile,
    string ExecutablePath = "", string RequiredArguments = "", byte[]? IconContents = null, Uri? IconUri = null,
    string IconPath = "", int IconIndex = 0)
{
    public string DisplayName => ExecutablePath.Length == 0 ? Name : $"{Name} — {ExecutablePath}";
}

internal static class RemoteAppFeed
{
    private static readonly XNamespace FeedNamespace = "http://schemas.microsoft.com/ts/2007/05/tswf";

    internal static Uri ResolveAddress(string? feedAddress, string? rdpAddress)
    {
        if (string.IsNullOrWhiteSpace(feedAddress) && RdpEndpoint.TryParse(rdpAddress, out var endpoint))
            return new UriBuilder(Uri.UriSchemeHttps, endpoint.Host) { Path = "/RDWeb/Feed/webfeed.aspx" }.Uri;

        if (Uri.TryCreate(feedAddress?.Trim(), UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0)
            return uri;

        throw new FormatException("Укажите HTTPS-адрес ленты RD Web или корректный адрес подключения.");
    }

    internal static async Task<IReadOnlyList<PublishedRemoteApp>> ListAsync(Uri feed, NetworkCredential credential)
    {
        using var client = CreateClient(feed, credential);
        using var request = new HttpRequestMessage(HttpMethod.Get, feed);
        request.Headers.Accept.ParseAdd("application/x-msts-radc+xml; radc_schema_version=2.0");
        using var response = await client.SendAsync(request);
        CheckResponse(response);
        await using var stream = await response.Content.ReadAsStreamAsync();
        return ReadApplications(feed, stream);
    }

    internal static IReadOnlyList<PublishedRemoteApp> ReadApplications(Uri feed, Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var root = XDocument.Load(reader).Root;
        if (root?.Name != FeedNamespace + "ResourceCollection")
            throw new FormatException("RD Web вернул страницу вместо ленты опубликованных приложений. Проверьте адрес и способ входа.");

        var apps = new List<PublishedRemoteApp>();
        foreach (var resource in root.Descendants(FeedNamespace + "Resource"))
        {
            if (!string.Equals((string?)resource.Attribute("Type"), "RemoteApp", StringComparison.OrdinalIgnoreCase))
                continue;

            string alias = ((string?)resource.Attribute("Alias"))?.Trim() ?? "";
            string name = ((string?)resource.Attribute("Title"))?.Trim() ?? "";
            var rdpFile = resource.Descendants(FeedNamespace + "ResourceFile")
                .Where(file => string.Equals((string?)file.Attribute("FileExtension"), ".rdp", StringComparison.OrdinalIgnoreCase))
                .Select(file => ResolveResourceFile(feed, (string?)file.Attribute("URL")))
                .FirstOrDefault(uri => uri is not null);
            if (alias.Length != 0 || rdpFile is not null)
            {
                var icons = resource.Element(FeedNamespace + "Icons");
                string[] iconNames = ["Icon64", "Icon48", "Icon32", "IconRaw", "Icon16", "Icon100", "Icon256"];
                var icon = iconNames.Select(element =>
                    ResolveResourceFile(feed, (string?)icons?.Element(FeedNamespace + element)?.Attribute("FileURL")))
                    .FirstOrDefault(uri => uri is not null);
                apps.Add(new PublishedRemoteApp(name.Length != 0 ? name : alias, alias, rdpFile, IconUri: icon));
            }
        }

        return apps.Distinct().OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal static async Task<byte[]?> ReadIconAsync(Uri feed, PublishedRemoteApp app, NetworkCredential credential)
    {
        if (app.IconUri is null)
            return null;

        using var client = CreateClient(feed, credential);
        using var response = await client.GetAsync(app.IconUri);
        CheckResponse(response);
        return await response.Content.ReadAsByteArrayAsync();
    }

    internal static async Task<byte[]?> FindIconAsync(Uri feed, string program, NetworkCredential credential)
    {
        if (!program.StartsWith("||", StringComparison.Ordinal))
            return null;

        string alias = program[2..];
        var apps = await ListAsync(feed, credential);
        var matches = apps.Where(app => string.Equals(app.Alias.TrimStart('|'), alias, StringComparison.OrdinalIgnoreCase) ||
            app.Alias.StartsWith(alias + "-", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? await ReadIconAsync(feed, matches[0], credential) : null;
    }

    internal static async Task<RdpRemoteAppOptions> ReadLaunchOptionsAsync(Uri feed, PublishedRemoteApp app, NetworkCredential credential)
    {
        if (app.RdpFile is null)
            return new RdpRemoteAppOptions { Enabled = true, Program = app.Alias.StartsWith("||") ? app.Alias : "||" + app.Alias };

        using var client = CreateClient(feed, credential);
        using var response = await client.GetAsync(app.RdpFile);
        CheckResponse(response);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var published = await RdpFileImport.ReadAsync(stream, app.Name);
        if (string.IsNullOrWhiteSpace(published.RemoteApp.Program))
            throw new FormatException("В файле опубликованного приложения нет псевдонима RemoteApp.");

        return published.RemoteApp with { Enabled = true };
    }

    private static Uri? ResolveResourceFile(Uri feed, string? url) =>
        !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(feed, url, out var file) && file.UserInfo.Length == 0 &&
        file.Scheme == feed.Scheme && file.IdnHost.Equals(feed.IdnHost, StringComparison.OrdinalIgnoreCase) && file.Port == feed.Port
            ? file : null;

    private static HttpClient CreateClient(Uri feed, NetworkCredential credential)
    {
        var origin = new Uri(feed.GetLeftPart(UriPartial.Authority));
        var credentials = new CredentialCache
        {
            { origin, "Negotiate", credential },
            { origin, "NTLM", credential },
            { origin, "Basic", credential }
        };

        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, Credentials = credentials })
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 4 * 1024 * 1024
        };
    }

    private static void CheckResponse(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new FormatException("RD Web отклонил вход. Проверьте учётную запись и доступ к ленте.");
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new FormatException("RD Web перенаправил запрос. Укажите прямой HTTPS-адрес ленты с поддержкой входа Windows или HTTP-аутентификации.");

        response.EnsureSuccessStatusCode();
    }
}

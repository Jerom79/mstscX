using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace mstscX.Services;

internal sealed record AppUpdate(Version Version, string FileName, Uri DownloadUrl, long Size, string Sha256);

internal static class AppUpdates
{
    private static readonly HttpClient Client = CreateClient();
    internal static Version CurrentVersion => typeof(AppUpdates).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("mstscX-UpdateCheck/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    internal static async Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await Client.GetAsync("https://api.github.com/repos/Jerom79/mstscX/releases/latest", timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(timeout.Token), CurrentVersion);
    }

    internal static AppUpdate? ParseRelease(string json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || version.Revision < 0 || version <= current) return null;
        string name = $"mstscX-Setup-{version}.exe";
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name || asset.GetProperty("state").GetString() != "uploaded") continue;
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Port != 443 || uri.UserInfo.Length != 0 ||
                uri.AbsolutePath != $"/Jerom79/mstscX/releases/download/{tag}/{name}" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                continue;
            long size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > 1024L * 1024 * 1024) continue;
            string digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
            string hash = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : "";
            if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$"))
            {
                string body = release.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                hash = Regex.Match(body, @"SHA-256:\s*([a-fA-F0-9]{64})\b").Groups[1].Value;
            }
            if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException(L.T("В выпуске отсутствует контрольная сумма установщика."));
            return new(version, name, uri, size, hash);
        }
        return null;
    }

    internal static async Task<string> DownloadAsync(AppUpdate update, string cacheDirectory,
        IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        var token = timeout.Token;
        string directory = Path.Combine(cacheDirectory, "Updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, update.FileName);
        string partial = path + ".part";
        try
        {
            using var response = await Client.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int previousPercent = -1;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) != 0)
                {
                    total += read;
                    if (total > update.Size) throw new InvalidDataException(L.T("Размер установщика не совпадает с опубликованным."));
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    int percent = (int)(total * 100 / update.Size);
                    if (percent != previousPercent) { progress.Report(percent / 100d); previousPercent = percent; }
                }
            }
            await VerifyAsync(update, partial, token);
            File.Move(partial, path);
            return path;
        }
        catch
        {
            try { File.Delete(partial); Directory.Delete(directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static async Task VerifyAsync(AppUpdate update, string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (file.Length != update.Size) throw new InvalidDataException(L.T("Размер установщика не совпадает с опубликованным."));
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
        if (!hash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("Контрольная сумма установщика не совпадает. Скачайте обновление повторно."));
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using mstscX.Services;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
string hash = new('a', 64);
string Release(string version = "1.0.0.12", bool prerelease = false, bool draft = false,
    string? url = null, string? name = null, string? digest = null, string body = "", long size = 100) =>
    JsonSerializer.Serialize(new { tag_name = "v" + version, prerelease, draft, body,
        assets = new[] { new { name = name ?? $"mstscX-Setup-{version}.exe", state = "uploaded", size,
            browser_download_url = url ?? $"https://github.com/Jerom79/mstscX/releases/download/v{version}/mstscX-Setup-{version}.exe",
            digest = digest ?? "sha256:" + hash } } });
var current = new Version(1, 0, 0, 11);
Check(AppUpdates.ParseRelease(Release(), current)?.Version == new Version(1, 0, 0, 12), "New release");
Check(AppUpdates.ParseRelease(Release("1.0.0.9"), current) is null, "No downgrade / numeric comparison");
Check(AppUpdates.ParseRelease(Release("1.0.0.11"), current) is null, "No equal version");
Check(AppUpdates.ParseRelease(Release(prerelease: true), current) is null, "No prerelease");
Check(AppUpdates.ParseRelease(Release(draft: true), current) is null, "No draft");
Check(AppUpdates.ParseRelease(Release(name: "mstscX.zip"), current) is null, "Installer only");
Check(AppUpdates.ParseRelease(Release(url: "https://example.com/setup.exe"), current) is null, "Reject external download");
Check(AppUpdates.ParseRelease(Release(url: "https://github.com/other/repo/releases/download/v1/setup.exe"), current) is null, "Reject other repository");
Check(AppUpdates.ParseRelease(Release(size: 0), current) is null, "Reject empty installer");
Check(AppUpdates.ParseRelease(Release(digest: "", body: "SHA-256: " + hash), current)?.Sha256 == hash, "Legacy release checksum");
bool rejected = false;
try { AppUpdates.ParseRelease(Release(digest: ""), current); } catch (InvalidDataException) { rejected = true; }
Check(rejected, "Checksum required");
string path = Path.GetTempFileName();
try
{
    byte[] data = [1, 2, 3, 4];
    await File.WriteAllBytesAsync(path, data);
    var update = new AppUpdate(new Version(1, 0, 0, 12), "test.exe", new Uri("https://github.com/"), data.Length,
        Convert.ToHexString(SHA256.HashData(data)));
    await AppUpdates.VerifyAsync(update, path, default);
    await File.WriteAllBytesAsync(path, [1, 2, 3, 5]);
    rejected = false;
    try { await AppUpdates.VerifyAsync(update, path, default); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "Reject corrupted download");
    await File.WriteAllBytesAsync(path, [1]);
    rejected = false;
    try { await AppUpdates.VerifyAsync(update, path, default); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "Reject truncated download");
}
finally { File.Delete(path); }
Console.WriteLine("Update checks passed: versions, release filtering, installer URLs, checksums and corruption.");

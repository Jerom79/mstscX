using mstscX.Models;
using mstscX.Services;

string directory = Path.Combine(Path.GetTempPath(), "rdpx-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
string path = Path.Combine(directory, "Список с пробелами.rdpx");
try
{
    var server = new ServerProfile("Бухгалтерия", "srv_buh:1111", true)
    {
        Id = Guid.NewGuid(), UserName = "domain\\user", EncryptedPassword = "encrypted-data",
        Display = new() { UseMultimon = true }, Resources = new() { Clipboard = false },
        RemoteApp = new() { Enabled = true, Program = "||app", Arguments = "/test" }
    };
    await RdpxFile.WriteAsync(path, [server]);
    Check((await File.ReadAllTextAsync(path)).Contains("MSTSC_MAUI.rdpx"), "Legacy format signature retained");
    var read = await RdpxFile.ReadAsync(path);
    Check(read.Count == 1 && read[0].Id == server.Id && read[0].Name == server.Name &&
        read[0].Address == server.Address && read[0].EncryptedPassword == server.EncryptedPassword &&
        read[0].Display.UseMultimon && !read[0].Resources.Clipboard && read[0].RemoteApp.Program == "||app", "Round trip");
    await RdpxFile.WriteAsync(path, []);
    Check((await RdpxFile.ReadAsync(path)).Count == 0, "Overwrite with empty list");
    await File.WriteAllTextAsync(path, """
        {"Format":"MSTSC_MAUI.rdpx","Version":1,"Servers":[{"Name":"Legacy server","Address":"server:1111","UserName":"domain\\user","EncryptedPassword":"existing-protected-password"}]}
        """);
    var legacy = await RdpxFile.ReadAsync(path);
    Check(legacy.Count == 1 && legacy[0].EncryptedPassword == "existing-protected-password" &&
        legacy[0].Address == "server:1111", "Read pre-rename server list without changing saved password");
    foreach (string invalid in new[] { "broken", "{}", "{\"Format\":\"MSTSC_MAUI.rdpx\",\"Version\":99,\"Servers\":[]}",
        "{\"Format\":\"MSTSC_MAUI.rdpx\",\"Version\":1,\"Servers\":[null]}" })
    {
        await File.WriteAllTextAsync(path, invalid);
        bool rejected = false;
        try { await RdpxFile.ReadAsync(path); }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException) { rejected = true; }
        Check(rejected, "Reject invalid document");
    }
    await RdpxFile.WriteAsync(path, [server, server]);
    bool duplicateRejected = false;
    try { await RdpxFile.ReadAsync(path); } catch (FormatException) { duplicateRejected = true; }
    Check(duplicateRejected, "Reject duplicate names");
    Console.WriteLine("RDPX checks passed.");
}
finally { Directory.Delete(directory, recursive: true); }

static void Check(bool value, string name) { if (!value) throw new Exception(name); }

// UI-only property is ignored by JSON; no MAUI runtime is needed for format tests.
public class ImageSource
{
    public static ImageSource FromStream(Func<Stream> stream) => new();
}

using System.Text.Json;
using mstscX.Models;

namespace mstscX.Services;

internal static class RdpxFile
{
    // Keep the on-disk signature compatible with existing exported server lists.
    internal sealed record Document(string Format, int Version, List<ServerProfile> Servers);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    internal static async Task<List<ServerProfile>> ReadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > 32 * 1024 * 1024) throw new FormatException(global::mstscX.Services.L.T("Файл списка слишком большой."));
        var document = await JsonSerializer.DeserializeAsync<Document>(stream, Options);
        if (document is not { Format: "MSTSC_MAUI.rdpx", Version: 1, Servers: not null })
            throw new FormatException(global::mstscX.Services.L.T("Неподдерживаемый формат или версия списка RDPX."));
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in document.Servers)
        {
            if (server is null || string.IsNullOrWhiteSpace(server.Name) || !names.Add(server.Name.Trim()) ||
                !RdpEndpoint.TryParse(server.Address, out _) || server.Display is null ||
                server.Resources is null || server.RemoteApp is null || server.Gateway is null)
                throw new FormatException(global::mstscX.Services.L.T("Список содержит некорректные или повторяющиеся профили."));
            if (server.Id == Guid.Empty || !ids.Add(server.Id))
            {
                server.Id = Guid.NewGuid();
                ids.Add(server.Id);
            }
        }
        return document.Servers;
    }

    internal static async Task WriteAsync(string path, IReadOnlyList<ServerProfile> profiles)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary,
                JsonSerializer.Serialize(new Document("MSTSC_MAUI.rdpx", 1, [.. profiles]), Options));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

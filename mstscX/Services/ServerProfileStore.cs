using System.Text.Json;
using mstscX.Models;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace mstscX.Services;

public sealed class ServerProfileStore(IPreferences preferences)
{
    private const string ProfilesKey = "RdpServerProfiles";

    public List<ServerProfile> Load() =>
        JsonSerializer.Deserialize<List<ServerProfile>>(preferences.Get(ProfilesKey, "[]")) ?? [];

    public void Save(IReadOnlyList<ServerProfile> profiles) =>
        preferences.Set(ProfilesKey, JsonSerializer.Serialize(profiles));

    internal bool IsGroupExpanded(bool isRemoteApp) => preferences.Get(GroupExpansionKey(isRemoteApp), true);

    internal void SaveGroupExpansion(bool isRemoteApp, bool expanded) =>
        preferences.Set(GroupExpansionKey(isRemoteApp), expanded);

    private static string GroupExpansionKey(bool isRemoteApp) =>
        isRemoteApp ? "RemoteAppGroupExpanded" : "RemoteDesktopGroupExpanded";

    public async Task<string> ProtectPasswordAsync(string password)
    {
        var protector = new DataProtectionProvider("LOCAL=user");
        var bytes = CryptographicBuffer.ConvertStringToBinary(password, BinaryStringEncoding.Utf8);
        return CryptographicBuffer.EncodeToBase64String(await protector.ProtectAsync(bytes));
    }

    public async Task<string?> ReadPasswordAsync(ServerProfile profile)
    {
        if (profile.EncryptedPassword is not { Length: > 0 } encrypted)
            return null;

        var protector = new DataProtectionProvider();
        var bytes = await protector.UnprotectAsync(CryptographicBuffer.DecodeFromBase64String(encrypted));
        return CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, bytes);
    }
}

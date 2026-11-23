using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace mstscX.Services;

// Russian source strings are stable resource keys; user data is never translated.
internal static class L
{
    private static readonly Dictionary<string, string> English = Load();
    private static readonly Dictionary<string, string> XamlKeys = English.Keys.ToDictionary(Key, value => value);
    internal static string Language { get; set; } = ResolveLanguage("system", CultureInfo.CurrentUICulture);

    internal static string ResolveLanguage(string choice, CultureInfo systemCulture) => choice switch
    {
        "ru" => "ru",
        "en" => "en",
        _ => systemCulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en"
    };

    internal static string T(string source) => Language == "en" && English.TryGetValue(source, out var value) ? value : source;
    internal static string F(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(format), args);
    internal static string FromKey(string key) => T(XamlKeys.TryGetValue(key, out var source)
        ? source : throw new InvalidOperationException($"Missing localization resource: {key}"));
    internal static string Key(string source) => "S" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..12];

    private static Dictionary<string, string> Load()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("mstscX.Localization.en.json")
            ?? throw new InvalidOperationException("Missing localization catalog.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using mstscX.Services;

void Check(bool value, string message) { if (!value) throw new Exception(message); }
foreach (string culture in new[] { "ru", "ru-RU", "ru-BY", "en-US", "de-DE", "fr-FR", "zh-CN" })
{
    var system = CultureInfo.GetCultureInfo(culture);
    Check(L.ResolveLanguage("system", system) == (culture.StartsWith("ru") ? "ru" : "en"), "System language: " + culture);
    Check(L.ResolveLanguage("ru", system) == "ru" && L.ResolveLanguage("en", system) == "en", "Explicit override");
    Check(L.ResolveLanguage("invalid", system) == L.ResolveLanguage("system", system), "Invalid preference fallback");
}
using var stream = typeof(L).Assembly.GetManifestResourceStream("mstscX.Localization.en.json")!;
var catalog = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
var keys = new HashSet<string>();
foreach (var (ru, en) in catalog)
{
    Check(!string.IsNullOrWhiteSpace(en), "Empty translation: " + ru);
    Check(!Regex.IsMatch(en, "[А-Яа-яЁё]"), "Untranslated English: " + ru);
    Check(keys.Add(L.Key(ru)), "Duplicate XAML key");
    var placeholders = Regex.Matches(ru, @"\{\d+(?:,[^}:]+)?(?::[^}]+)?\}").Select(m => m.Value).Order().ToArray();
    var translated = Regex.Matches(en, @"\{\d+(?:,[^}:]+)?(?::[^}]+)?\}").Select(m => m.Value).Order().ToArray();
    Check(placeholders.SequenceEqual(translated), "Format placeholders differ: " + ru);
    L.Language = "en";
    Check(L.T(ru) == en && L.FromKey(L.Key(ru)) == en, "English lookup: " + ru);
    L.Language = "ru";
    Check(L.T(ru) == ru && L.FromKey(L.Key(ru)) == ru, "Russian lookup: " + ru);
}
L.Language = "en";
Check(L.F("Не удалось запустить RDP (0x{0:X8}). {1}", 5, "details") == "Could not start RDP (0x00000005). details", "Formatted error");
Check(L.T("User-defined server name") == "User-defined server name", "Unknown text preserved");
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../mstscX"));
foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
    .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
{
    foreach (Match match in Regex.Matches(File.ReadAllText(file), @"\bKey=(?:""|)(S[A-F0-9]{12})"))
        Check(keys.Contains(match.Groups[1].Value), "Missing XAML resource in " + file);
    var doc = XDocument.Load(file);
    Check(!doc.Descendants().Attributes().Any(a => Regex.IsMatch(a.Value, "[А-Яа-яЁё]")), "Unlocalized XAML attribute: " + file);
    Check(!doc.Descendants().Any(e => e.Name.LocalName == "String" && Regex.IsMatch(e.Value, "[А-Яа-яЁё]")), "Unlocalized picker: " + file);
}
Console.WriteLine($"Localization checks passed: {catalog.Count} translations, cultures, formatting and XAML keys.");

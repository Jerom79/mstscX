using System.Globalization;
using System.Runtime.InteropServices;

namespace mstscX.Services;

internal static class LanguageSettings
{
    private const string PreferenceKey = "ApplicationLanguage";
    private static readonly string[] Choices = ["system", "ru", "en"];
    internal static int SelectedIndex => Math.Max(0, Array.IndexOf(Choices, Preferences.Default.Get(PreferenceKey, "system")));
    internal static void Initialize()
    {
        // Match Setup: use the Windows display language, not regional number/date settings.
        CultureInfo systemCulture;
        try { systemCulture = CultureInfo.GetCultureInfo(GetUserDefaultUILanguage()); }
        catch (CultureNotFoundException) { systemCulture = CultureInfo.CurrentUICulture; }
        L.Language = L.ResolveLanguage(Choices[SelectedIndex], systemCulture);
    }
    internal static void Save(int index)
    {
        if (index >= 0 && index < Choices.Length) Preferences.Default.Set(PreferenceKey, Choices[index]);
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();
}

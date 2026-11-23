namespace mstscX.Services;

internal static class ThemeSettings
{
    private const string Key = "ApplicationTheme";

    public static int SelectedIndex => Math.Clamp(Preferences.Default.Get(Key, 0), 0, 2);

    public static void Apply(Application application, int index)
    {
        application.UserAppTheme = index switch
        {
            1 => AppTheme.Light,
            2 => AppTheme.Dark,
            _ => AppTheme.Unspecified
        };
    }

    public static void Save(Application application, int index)
    {
        Preferences.Default.Set(Key, index);
        Apply(application, index);
    }
}

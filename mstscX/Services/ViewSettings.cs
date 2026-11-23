namespace mstscX.Services;

internal static class ViewSettings
{
    private const string CompactPanelAtPointerKey = "View.CompactPanelAtPointer";
    // Read preferences once: native fullscreen panels poll this value on their UI timer.
    internal static bool CompactPanelAtPointer { get; private set; } =
        Preferences.Default.Get(CompactPanelAtPointerKey, false);

    internal static void SetCompactPanelAtPointer(bool enabled)
    {
        if (CompactPanelAtPointer == enabled)
            return;

        Preferences.Default.Set(CompactPanelAtPointerKey, enabled);
        CompactPanelAtPointer = enabled;
    }
}

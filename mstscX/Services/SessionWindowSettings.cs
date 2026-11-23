using System.Runtime.InteropServices;
using System.Text.Json;
using mstscX.Models;

namespace mstscX.Services;

internal readonly record struct SessionWindowLayout(bool FullScreen, double Width, double Height);
internal readonly record struct SessionPanelState(bool Compact, bool Pinned);

internal static class SessionWindowSettings
{
    internal static SessionWindowLayout Load(ServerProfile profile)
    {
        bool fullScreen = profile.OpenInSeparateWindow && (profile.Display.FullScreen || profile.Display.UseMultimon);
        string saved = Preferences.Default.Get(GetKey(profile), "");
        if (saved.Length > 0)
        {
            try
            {
                var state = JsonSerializer.Deserialize<SavedLayout>(saved);
                bool sameDisplayOptions = state.ConfiguredFullScreen == profile.Display.FullScreen &&
                    state.ConfiguredUseMultimon == profile.Display.UseMultimon;
                return new(profile.OpenInSeparateWindow && (sameDisplayOptions ? state.Layout.FullScreen : fullScreen),
                    state.Layout.Width > 0 ? state.Layout.Width : 1280,
                    state.Layout.Height > 0 ? state.Layout.Height : 800);
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        return new(fullScreen, 1280, 800);
    }

    internal static void Save(ServerProfile profile, SessionWindowLayout layout)
    {
        if (!profile.OpenInSeparateWindow)
            layout = layout with { FullScreen = false };
        try
        {
            var state = new SavedLayout(layout, profile.Display.FullScreen, profile.Display.UseMultimon);
            Preferences.Default.Set(GetKey(profile), JsonSerializer.Serialize(state));
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    internal static double LoadPanelPosition(Guid profileId)
    {
        double position = Preferences.Default.Get(GetPanelPositionKey(profileId), 0.5);
        return double.IsFinite(position) ? Math.Clamp(position, 0, 1) : 0.5;
    }

    internal static void SavePanelPosition(Guid profileId, double position)
    {
        try
        {
            Preferences.Default.Set(GetPanelPositionKey(profileId), position);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private static string GetPanelPositionKey(Guid profileId) => $"RdpSessionPanelPosition.{profileId:N}";

    internal static SessionPanelState LoadPanelState(Guid profileId) =>
        Preferences.Default.Get(GetPanelStateKey(profileId), "") switch
        {
            "compact" => new(Compact: true, Pinned: false),
            "pinned" => new(Compact: false, Pinned: true),
            "compact-pinned" => new(Compact: true, Pinned: true),
            _ => default
        };

    internal static void SavePanelState(Guid profileId, SessionPanelState state)
    {
        try
        {
            string value = state switch
            {
                (true, true) => "compact-pinned",
                (true, false) => "compact",
                (false, true) => "pinned",
                _ => ""
            };
            Preferences.Default.Set(GetPanelStateKey(profileId), value);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private static string GetPanelStateKey(Guid profileId) => $"RdpSessionPanelState.{profileId:N}";

    private static string GetKey(ServerProfile profile) => $"RdpSessionWindow.{profile.Id:N}";

    private readonly record struct SavedLayout(SessionWindowLayout Layout, bool ConfiguredFullScreen,
        bool ConfiguredUseMultimon);
}

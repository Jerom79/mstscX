using System.Text.Json;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace mstscX.WinUI;

internal sealed class MainWindowPlacement : IDisposable
{
    private const string Key = "MainWindow.Placement";
    private readonly AppWindow window;
    private readonly Func<bool> transientMode;
    private SavedPlacement state;
    private bool restored;

    internal MainWindowPlacement(AppWindow window, Func<bool> transientMode)
    {
        this.window = window;
        this.transientMode = transientMode;
        Capture();
        window.Changed += OnChanged;
    }

    internal void Restore()
    {
        if (restored) return;
        restored = true;
        try
        {
            string json = Preferences.Default.Get(Key, "");
            if (json.Length == 0) return;
            var saved = JsonSerializer.Deserialize<SavedPlacement>(json);
            if (saved.Width < 200 || saved.Height < 150) return;
            var rect = new RectInt32(saved.X, saved.Y, saved.Width, saved.Height);
            var area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.Nearest).WorkArea;
            rect.Width = Math.Min(rect.Width, area.Width);
            rect.Height = Math.Min(rect.Height, area.Height);
            rect.X = Math.Clamp(rect.X, area.X, area.X + area.Width - rect.Width);
            rect.Y = Math.Clamp(rect.Y, area.Y, area.Y + area.Height - rect.Height);
            window.MoveAndResize(rect);
            Capture();
            if (saved.Maximized && window.Presenter is OverlappedPresenter presenter)
                presenter.Maximize();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void OnChanged(AppWindow sender, AppWindowChangedEventArgs args) => Capture();

    private void Capture()
    {
        // Keep normal bounds while fullscreen, minimized or displaying a fullscreen RDP session.
        if (transientMode() || window.Presenter is not OverlappedPresenter presenter) return;
        if (presenter.State == OverlappedPresenterState.Maximized)
            state = state with { Maximized = true };
        else if (presenter.State == OverlappedPresenterState.Restored && window.Size.Width > 0 && window.Size.Height > 0)
            state = new(window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height, false);
    }

    internal void Save()
    {
        Capture();
        if (state.Width <= 0 || state.Height <= 0) return;
        try { Preferences.Default.Set(Key, JsonSerializer.Serialize(state)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    public void Dispose() => window.Changed -= OnChanged;
    private readonly record struct SavedPlacement(int X, int Y, int Width, int Height, bool Maximized);
}

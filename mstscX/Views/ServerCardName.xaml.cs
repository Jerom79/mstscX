using System.ComponentModel;
using mstscX.Models;
using NativeTextBox = Microsoft.UI.Xaml.Controls.TextBox;

namespace mstscX.Views;

public partial class ServerCardName : ContentView
{
    public static readonly BindableProperty NameFontSizeProperty = BindableProperty.Create(
        nameof(NameFontSize), typeof(double), typeof(ServerCardName), 14d,
        propertyChanged: (sender, _, value) => ((ServerCardName)sender).NameLabel.FontSize = (double)value);

    private NativeTextBox? nativeEntry;

    public ServerCardName()
    {
        InitializeComponent();
        Loaded += (_, _) => FocusRenameEntry();
    }

    public double NameFontSize { get => (double)GetValue(NameFontSizeProperty); set => SetValue(NameFontSizeProperty, value); }
    internal string? RenameText => RenameEntry.Text;
    public event EventHandler? CommitRequested;
    public event EventHandler? CancelRequested;

    private void OnRenameEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsVisible))
            FocusRenameEntry();
    }

    private void FocusRenameEntry()
    {
        if (BindingContext is not ServerProfile { IsRenaming: true } profile)
            return;

        Dispatcher.Dispatch(() =>
        {
            if (!ReferenceEquals(BindingContext, profile) || !profile.IsRenaming || !RenameEntry.IsVisible)
                return;

            RenameEntry.Text = profile.Name;
            if (RenameEntry.Focus())
                nativeEntry?.SelectAll();
        });
    }

    private void OnRenameEntryHandlerChanged(object? sender, EventArgs e)
    {
        if (nativeEntry is not null)
            nativeEntry.KeyDown -= OnRenameKeyDown;
        nativeEntry = (sender as Entry)?.Handler?.PlatformView as NativeTextBox;
        if (nativeEntry is not null)
            nativeEntry.KeyDown += OnRenameKeyDown;
    }

    private void OnRenameKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape)
            return;

        e.Handled = true;
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnRenameEntryCompleted(object? sender, EventArgs e) => CommitRequested?.Invoke(this, EventArgs.Empty);
}

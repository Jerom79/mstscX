namespace mstscX;

public partial class MainPage
{
    private bool showingAbout;

    private async Task ShowAboutAsync()
    {
        if (showingAbout || nativeMainWindow?.Content is not Microsoft.UI.Xaml.FrameworkElement root)
            return;
        showingAbout = true;
        visibleEmbeddedSession?.SetEmbeddedVisible(false);
        try
        {
            var content = new Microsoft.UI.Xaml.Controls.StackPanel
            {
                Spacing = 12,
                MinWidth = 280,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch
            };
            content.Children.Add(new Microsoft.UI.Xaml.Controls.Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                    new Uri(Path.Combine(AppContext.BaseDirectory, "appicon.ico"))),
                Width = 96,
                Height = 96,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
                Margin = new Microsoft.UI.Xaml.Thickness(0, 4, 0, 8)
            });
            void AddText(string text, double size)
            {
                content.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Text = text,
                    FontSize = size,
                    TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                });
            }
            AddText("mstscX", 24);
            AddText(global::mstscX.Services.L.F("Версия: {0}", typeof(App).Assembly.GetName().Version), 14);
            AddText(global::mstscX.Services.L.T("Автор: Jeronimo"), 14);
            AddText(global::mstscX.Services.L.T("Сделано с помощью Codex"), 14);
            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                XamlRoot = root.XamlRoot,
                RequestedTheme = Application.Current?.RequestedTheme == AppTheme.Dark
                    ? Microsoft.UI.Xaml.ElementTheme.Dark : Microsoft.UI.Xaml.ElementTheme.Light,
                Title = global::mstscX.Services.L.T("О программе"),
                Content = content,
                CloseButtonText = global::mstscX.Services.L.T("ОК"),
                DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close
            };
            await dialog.ShowAsync();
        }
        finally
        {
            showingAbout = false;
            UpdateSelectedSession();
        }
    }
}

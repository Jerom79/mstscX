namespace mstscX;

public partial class MainPage
{
    private bool languagePickerReady;

    private void InitializeLanguagePicker()
    {
        LanguagePicker.ItemsSource = new[] { Services.L.T("Системный язык"), "Русский", "English" };
        LanguagePicker.SelectedIndex = Services.LanguageSettings.SelectedIndex;
        languagePickerReady = true;
    }

    private async void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!languagePickerReady || LanguagePicker.SelectedIndex < 0) return;
        Services.LanguageSettings.Save(LanguagePicker.SelectedIndex);
        visibleEmbeddedSession?.SetEmbeddedVisible(false);
        try
        {
            await DisplayAlertAsync(Services.L.T("Язык приложения"),
                Services.L.T("Новый язык будет применён при следующем запуске приложения. Открытые подключения не прерываются."), Services.L.T("ОК"));
        }
        finally { UpdateSelectedSession(); }
    }
}

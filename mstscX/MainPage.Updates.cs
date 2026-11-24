using System.Diagnostics;
using mstscX.Services;

namespace mstscX;

public partial class MainPage
{
    private CancellationTokenSource? updateLifetime;
    private CancellationTokenSource? updateDownload = null;
    private AppUpdate? availableUpdate;
    private string? updateInstaller;
    private string? dismissedUpdate;
    private bool checkingUpdate, downloadingUpdate = false;

    private void StartUpdateChecks()
    {
        if (updateLifetime is not null) return;
        updateLifetime = new();
        _ = RunUpdateChecksAsync(updateLifetime.Token);
    }

    private async Task RunUpdateChecksAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            while (!token.IsCancellationRequested)
            {
                await CheckUpdatesAsync(false, token);
                await Task.Delay(TimeSpan.FromHours(6), token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void StopUpdateChecks()
    {
        updateLifetime?.Cancel();
        updateLifetime?.Dispose();
        updateLifetime = null;
        updateDownload?.Cancel();
    }

    private async Task CheckUpdatesAsync(bool manual, CancellationToken token = default)
    {
        if (checkingUpdate || downloadingUpdate || mainWindowCloseApproved) return;
        checkingUpdate = true;
        if (manual)
        {
            ShowSettings(false);
            UpdateNotice.IsVisible = true;
            UpdateNoticeText.Text = L.T("Проверка обновлений…");
            UpdateAction.IsVisible = false;
        }
        try
        {
#if DEBUG
            // Temporary visual preview; never included in Release builds.
            await Task.Delay(200, token);
            var current = AppUpdates.CurrentVersion;
            var previewVersion = new Version(current.Major, current.Minor, current.Build, current.Revision + 1);
            var update = new AppUpdate(previewVersion, "preview.exe", new Uri("https://github.com/Jerom79/mstscX"), 0, "");
#else
            var update = await AppUpdates.CheckAsync(token);
#endif
            if (mainWindowCloseApproved || token.IsCancellationRequested) return;
            if (update is not null)
            {
                if (availableUpdate?.Version != update.Version) updateInstaller = null;
                availableUpdate = update;
                if (manual || dismissedUpdate != update.Version.ToString()) ShowUpdateAvailable();
            }
            else if (manual)
            {
                availableUpdate = null;
                UpdateNoticeText.Text = L.T("Установлена последняя версия.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            if (manual && !mainWindowCloseApproved && !token.IsCancellationRequested)
                UpdateNoticeText.Text = L.T("Не удалось проверить обновления. Повторите позже.");
            Debug.WriteLine($"Update check: {ex.Message}");
        }
        finally { checkingUpdate = false; }
    }

    private void ShowUpdateAvailable()
    {
        if (availableUpdate is not { } update) return;
        UpdateNoticeText.Text = L.F("Доступна версия {0}", update.Version);
        UpdateAction.Text = updateInstaller is null ? L.T("Скачать обновление") : L.T("Установить и закрыть");
        UpdateAction.IsVisible = true;
        UpdateAction.IsEnabled = true;
        UpdateNotice.IsVisible = true;
    }

    private void OnDismissUpdate(object? sender, EventArgs e)
    {
        if (downloadingUpdate) updateDownload?.Cancel();
        dismissedUpdate = availableUpdate?.Version.ToString();
        UpdateNotice.IsVisible = false;
    }

    private async void OnUpdateAction(object? sender, EventArgs e)
    {
#if DEBUG
        // Preview data must never reach the real downloader or installer.
        await Task.CompletedTask;
        UpdateNoticeText.Text = L.T("Демонстрация обновления: скачивание и установка отключены.");
#else
        if (availableUpdate is not { } update || downloadingUpdate || checkingUpdate) return;
        downloadingUpdate = true;
        UpdateAction.IsEnabled = false;
        updateDownload = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime?.Token ?? CancellationToken.None);
        try
        {
            if (updateInstaller is null)
            {
                UpdateProgress.IsVisible = true;
                UpdateProgress.Progress = 0;
                var progress = new Progress<double>(value =>
                {
                    UpdateProgress.Progress = value;
                    UpdateNoticeText.Text = L.F("Скачивание обновления: {0}%", (int)(value * 100));
                });
                updateInstaller = await AppUpdates.DownloadAsync(update, FileSystem.CacheDirectory, progress, updateDownload.Token);
                ShowUpdateAvailable();
            }
            else
            {
                await AppUpdates.VerifyAsync(update, updateInstaller, updateDownload.Token);
                string installer = updateInstaller;
                // Use the normal close confirmation and bypass close-to-tray only after approval.
                RequestMainWindowClose(() =>
                {
                    using var process = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
                    if (process is null) throw new InvalidOperationException(L.T("Не удалось запустить установщик."));
                });
            }
        }
        catch (OperationCanceledException)
        {
            if (!mainWindowCloseApproved && UpdateNotice.IsVisible)
                UpdateNoticeText.Text = L.T("Загрузка отменена или превышено время ожидания. Попробуйте снова.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            updateInstaller = null;
            UpdateNoticeText.Text = L.F("Не удалось подготовить обновление: {0}", ex.Message);
            UpdateAction.Text = L.T("Скачать обновление");
        }
        finally
        {
            updateDownload.Dispose();
            updateDownload = null;
            downloadingUpdate = false;
            UpdateAction.IsEnabled = true;
            UpdateProgress.IsVisible = false;
        }
#endif
    }
}

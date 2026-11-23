using Microsoft.Windows.Storage.Pickers;
using mstscX.Models;

namespace mstscX;

public partial class MainPage
{
    private async Task ReplaceServerListAsync(List<ServerProfile> profiles)
    {
        if (!viewModel.CanImportProfile()) return;
        visibleEmbeddedSession?.SetEmbeddedVisible(false);
        try
        {
            if ((viewModel.Profiles.Count > 0 || OpenSessionCount > 0) &&
                !await DisplayAlertAsync("Заменить список серверов?",
                    "Текущие данные серверов будут удалены и заменены данными из файла. Открытые соединения будут отключены. Продолжить?",
                    "Заменить", "Отмена")) return;
            // Persist the validated replacement before disconnecting the old sessions.
            viewModel.ReplaceProfiles(profiles);
            foreach (var session in embeddedSessions.Values.ToArray()) session.EndSession();
            foreach (var session in nativeSessions.Values.ToArray()) session.CloseConnection();
            foreach (var id in remoteAppSessions.Keys.ToArray()) CloseRemoteAppSession(id);
            connectionErrors.Clear();
            reconnectStatuses.Clear();
            ShowSettings(false);
            viewModel.ReportFileStatus($"Загружено серверов: {profiles.Count}.");
        }
        finally { UpdateSelectedSession(); }
    }

    private async Task PickServerListAsync()
    {
        if (rdpFileOperationInProgress || !viewModel.CanImportProfile() || nativeMainWindow is not { } owner) return;
        var picker = new FileOpenPicker(owner.AppWindow.Id) { FileTypeFilter = { ".rdpx" } };
        try
        {
            if (await picker.PickSingleFileAsync() is { } file) await OpenServerFileAsync(file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { viewModel.ReportFileStatus("Не удалось открыть список серверов."); }
    }

    private async Task OpenServerFileAsync(string path)
    {
        ActivateSessionWindow(Window);
        if (rdpFileOperationInProgress || !viewModel.CanImportProfile()) return;
        rdpFileOperationInProgress = true;
        try
        {
            List<ServerProfile> profiles;
            if (Path.GetExtension(path).Equals(".rdpx", StringComparison.OrdinalIgnoreCase))
                profiles = await Services.RdpxFile.ReadAsync(path);
            else if (Path.GetExtension(path).Equals(".rdp", StringComparison.OrdinalIgnoreCase))
            {
                await using var stream = File.OpenRead(path);
                profiles = [await Services.RdpFileImport.ReadAsync(stream, Path.GetFileNameWithoutExtension(path))];
            }
            else throw new FormatException("Поддерживаются файлы .rdp и .rdpx.");
            await ReplaceServerListAsync(profiles);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or IOException or
            UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        { viewModel.ReportFileStatus($"Не удалось открыть файл: {ex.Message}"); }
        finally { rdpFileOperationInProgress = false; }
    }

    private async Task SaveServerListAsync()
    {
        if (rdpFileOperationInProgress || nativeMainWindow is not { } owner) return;
        rdpFileOperationInProgress = true;
        try
        {
            var picker = new FileSavePicker(owner.AppWindow.Id)
            {
                SuggestedFileName = "Серверы",
                DefaultFileExtension = ".rdpx",
                FileTypeChoices = { { "Список серверов", new List<string> { ".rdpx" } } }
            };
            if (await picker.PickSaveFileAsync() is not { } file) return;
            if (!Path.GetExtension(file.Path).Equals(".rdpx", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Укажите расширение .rdpx.");
            await Services.RdpxFile.WriteAsync(file.Path, viewModel.Profiles);
            viewModel.ReportFileStatus("Список серверов сохранён.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Runtime.InteropServices.COMException)
        { viewModel.ReportFileStatus($"Не удалось сохранить список: {ex.Message}"); }
        finally { rdpFileOperationInProgress = false; }
    }
}

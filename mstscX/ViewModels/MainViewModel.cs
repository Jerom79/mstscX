using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml;
using System.Windows.Input;
using mstscX.Models;
using mstscX.Services;

namespace mstscX.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ServerProfileStore store;
    private readonly Command connectProfileCommand;
    private readonly Command editProfileCommand;
    private List<ServerProfile> profiles = [];
    private ServerProfile? selectedProfile;
    private ServerProfile? inlineRenameProfile;
    private ServerProfile? editingProfile;
    private EditorSnapshot? editorSnapshot;
    private bool editorStartedAsNew;
    private string? profileName;
    private string profileNameError = "";
    private string? profileAddress;
    private string status = "";
    private int statusVersion;
    private bool trustProfile;
    private bool openInSeparateWindow = true;
    private bool isEditing;
    private bool isBusy;
    private int editorTab;
    private string? profileUserName;
    private string? profilePassword;
    private bool removePassword;
    private RdpDisplayOptions display = new();
    private RdpLocalResources resources = new();
    private RdpRemoteAppOptions remoteApp = new();
    private RdpGatewayOptions gateway = new();
    private IReadOnlyList<PublishedRemoteApp> publishedRemoteApps = [];
    private PublishedRemoteApp? selectedPublishedRemoteApp;
    private string remoteAppFeedStatus = "";
    private Uri? loadedRemoteAppFeed;
    private byte[]? draftRemoteAppIcon;
    private readonly Dictionary<PublishedRemoteApp, byte[]> publishedAppIcons = [];

    public MainViewModel(ServerProfileStore store)
    {
        this.store = store;
        connectProfileCommand = new Command(async () => await ConnectAsync(SelectedProfile),
            () => SelectedProfile is not null);
        editProfileCommand = new Command(() => EditProfile(SelectedProfile));
        NewProfileCommand = new Command(() => EditProfile(null));
        SelectEditorTabCommand = new Command<string>(tab => EditorTab = int.Parse(tab));
        LoadPublishedRemoteAppsCommand = new Command(async () => await LoadPublishedRemoteAppsAsync());
        ToggleProfileGroupCommand = new Command<ServerProfileGroup>(ToggleProfileGroup);

        try
        {
            profiles = store.Load();
            var ids = new HashSet<Guid>();
            bool needsSave = false;
            foreach (var profile in profiles)
            {
                profile.RemoteAppIconFile = RemoteAppIconCache.GetExistingPath(profile.RemoteApp.IconKey);
                if (profile.Id != Guid.Empty && ids.Add(profile.Id))
                    continue;

                profile.Id = Guid.NewGuid();
                ids.Add(profile.Id);
                needsSave = true;
            }

            if (needsSave)
                store.Save(profiles);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or COMException)
        {
            Status = profiles.Count == 0 ? "Не удалось прочитать сохранённые профили." :
                "Не удалось сохранить идентификаторы профилей.";
        }

        RefreshProfileGroups();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal event Action<string, ServerProfile, string?>? ConnectRequested;
    internal event Action<ServerProfile, string>? ConnectionFailed;
    internal event Action? RemoteAppIconChanged;
    internal event Action<ServerProfile>? ProfileRenamed;
    internal Func<Guid, bool>? ActivateExistingSession { get; set; }

    public ICommand ConnectProfileCommand => connectProfileCommand;
    public ICommand EditProfileCommand => editProfileCommand;
    public ICommand NewProfileCommand { get; }
    public ICommand SelectEditorTabCommand { get; }
    public ICommand LoadPublishedRemoteAppsCommand { get; }
    private ICommand ToggleProfileGroupCommand { get; }

    public IReadOnlyList<ServerProfile> Profiles => profiles;
    public IReadOnlyList<ServerProfileGroup> ProfileGroups { get; private set; } = [];

    private void RefreshProfileGroups()
    {
        ProfileGroups = profiles.GroupBy(profile => profile.RemoteApp.Enabled)
            .OrderBy(group => group.Key)
            .Select(group => new ServerProfileGroup(group.Key, store.IsGroupExpanded(group.Key), group, ToggleProfileGroupCommand))
            .ToArray();
        PropertyChanged?.Invoke(this, new(nameof(ProfileGroups)));
    }

    private void ToggleProfileGroup(ServerProfileGroup? group)
    {
        if (group is null || !ProfileGroups.Contains(group))
            return;

        bool expanded = !group.IsExpanded;
        try
        {
            store.SaveGroupExpansion(group.IsRemoteApp, expanded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            Status = "Не удалось сохранить состояние группы.";
            return;
        }

        if (!expanded && SelectedProfile is { } selected && group.Contains(selected))
            SelectedProfile = null;
        if (!expanded && inlineRenameProfile is { } rename && group.Contains(rename))
            CancelInlineRename(rename);

        group.SetExpanded(expanded, profiles.Where(profile => profile.RemoteApp.Enabled == group.IsRemoteApp));
    }

    internal bool CanMoveProfile(ServerProfile profile) =>
        !isBusy && !IsEditing && inlineRenameProfile is null &&
        ProfileGroups.Any(group => group.IsRemoteApp == profile.RemoteApp.Enabled && group.Count > 1);

    internal bool MoveProfile(ServerProfile source, ServerProfile target, bool insertAfter)
    {
        if (!CanMoveProfile(source) || source.RemoteApp.Enabled != target.RemoteApp.Enabled)
            return false;

        List<ServerProfile> reordered = [.. profiles];
        int sourceIndex = reordered.FindIndex(profile => ReferenceEquals(profile, source));
        int targetIndex = reordered.FindIndex(profile => ReferenceEquals(profile, target));
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
            return false;

        int destination = targetIndex + (insertAfter ? 1 : 0);
        if (sourceIndex < destination)
            destination--;
        if (sourceIndex == destination)
            return true;

        reordered.RemoveAt(sourceIndex);
        reordered.Insert(destination, source);
        try
        {
            store.Save(reordered);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            Status = "Не удалось сохранить порядок серверов.";
            return false;
        }

        var group = ProfileGroups.Single(group => group.IsRemoteApp == source.RemoteApp.Enabled);
        int groupDestination = reordered.Take(destination).Count(profile => profile.RemoteApp.Enabled == group.IsRemoteApp);
        profiles = reordered;
        group.Move(group.IndexOf(source), groupDestination);
        return true;
    }

    internal bool CanDelete(ServerProfile profile) => !isBusy && !IsEditing && profiles.Contains(profile);

    internal void BeginInlineRename(ServerProfile profile)
    {
        if (!CanDelete(profile) || ReferenceEquals(inlineRenameProfile, profile))
            return;

        if (inlineRenameProfile is { } previous)
            CancelInlineRename(previous);
        SelectedProfile = profile;
        inlineRenameProfile = profile;
        profile.IsRenaming = true;
    }

    internal void CancelInlineRename(ServerProfile profile)
    {
        if (!ReferenceEquals(inlineRenameProfile, profile))
            return;

        inlineRenameProfile = null;
        profile.IsRenaming = false;
    }

    internal bool TryRenameProfile(ServerProfile profile, string? value)
    {
        if (!ReferenceEquals(inlineRenameProfile, profile) || !CanDelete(profile))
            return false;

        string name = value?.Trim() ?? "";
        if (name.Length == 0 || IsDuplicateProfileName(name, profile.Id))
        {
            Status = name.Length == 0 ? "Укажите наименование сервера." : "Профиль с таким наименованием уже существует.";
            return false;
        }

        if (name == profile.Name)
        {
            CancelInlineRename(profile);
            return true;
        }

        var updated = profile.WithName(name);
        List<ServerProfile> renamed = [.. profiles];
        renamed[renamed.IndexOf(profile)] = updated;
        try
        {
            store.Save(renamed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            Status = "Не удалось переименовать профиль сервера.";
            return false;
        }

        var selection = SelectedProfile;
        CancelInlineRename(profile);
        profiles = renamed;
        RefreshProfileGroups();
        SelectedProfile = ReferenceEquals(selection, profile) ? updated : selection;
        ProfileRenamed?.Invoke(updated);
        Status = $"Профиль переименован в «{name}».";
        return true;
    }

    private bool IsDuplicateProfileName(string name, Guid? excludedId) => profiles.Any(profile => profile.Id != excludedId &&
        string.Equals(profile.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase));

    internal void DeleteProfile(ServerProfile profile)
    {
        if (!CanDelete(profile))
            return;

        var remaining = profiles.Where(item => item != profile).ToList();
        try
        {
            store.Save(remaining);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            Status = "Не удалось удалить профиль сервера.";
            return;
        }

        var selection = SelectedProfile;
        CancelInlineRename(profile);
        profiles = remaining;
        RefreshProfileGroups();
        SelectedProfile = selection == profile ? null : selection;
        Status = $"Профиль «{profile.Name}» удалён.";
    }

    public ServerProfile? SelectedProfile
    {
        get => selectedProfile;
        set
        {
            if (ReferenceEquals(selectedProfile, value))
                return;

            if (selectedProfile is { } previous)
                previous.IsSelected = false;
            if (value is not null)
                value.IsSelected = true;
            Set(ref selectedProfile, value);
            connectProfileCommand.ChangeCanExecute();
        }
    }

    internal void SetSessionActive(Guid profileId, bool active)
    {
        if (profiles.Find(profile => profile.Id == profileId) is { } profile)
            profile.IsSessionActive = active;
    }

    internal async Task LoadRemoteAppIconOnConnectAsync(ServerProfile profile, string? password)
    {
        if (profile.HasRemoteAppIcon || !profile.RemoteApp.Enabled || string.IsNullOrWhiteSpace(profile.UserName) ||
            string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(profile.RemoteApp.Program))
            return;

        string program = profile.RemoteApp.Program.Trim().Trim('"');
        var credential = CreateDiscoveryCredential(profile.UserName, password);
        try
        {
            byte[]? png = null;
            if (profile.RemoteApp.UseRdWeb)
            {
                try
                {
                    var feed = RemoteAppFeed.ResolveAddress(profile.RemoteApp.FeedAddress, profile.Address);
                    png = await RemoteAppIconCache.DecodeAsync(await RemoteAppFeed.FindIconAsync(feed, program, credential));
                }
                catch (Exception ex) when (ex is FormatException or HttpRequestException or OperationCanceledException or XmlException or IOException)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            }

            png ??= await RemoteAppIconCache.DecodeAsync(await RemoteAppInventory.FindIconAsync(profile.Address, program, credential));
            if (png is null)
                return;

            string key = RemoteAppIconCache.GetKey(png);
            await RemoteAppIconCache.SaveAsync(key, png);
            var current = profiles.Find(item => item.Id == profile.Id);
            if (current is null || current.HasRemoteAppIcon || !current.RemoteApp.Enabled || editingProfile?.Id == current.Id ||
                !string.Equals(current.Address, profile.Address, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current.RemoteApp.Program, profile.RemoteApp.Program, StringComparison.OrdinalIgnoreCase))
                return;

            string previousKey = current.RemoteApp.IconKey;
            current.RemoteApp.IconKey = key;
            try { store.Save(profiles); }
            catch
            {
                current.RemoteApp.IconKey = previousKey;
                throw;
            }

            current.RemoteAppIconFile = RemoteAppIconCache.GetExistingPath(key);
            RemoteAppIconChanged?.Invoke();
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or COMException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    public string? ProfileName
    {
        get => profileName;
        set
        {
            Set(ref profileName, value);
            ProfileNameError = "";
        }
    }

    public string ProfileNameError { get => profileNameError; private set => Set(ref profileNameError, value); }
    public string? ProfileAddress
    {
        get => profileAddress;
        set
        {
            if (!string.Equals(profileAddress?.Trim(), value?.Trim(), StringComparison.OrdinalIgnoreCase))
                TrustProfile = false;
            Set(ref profileAddress, value);
            PropertyChanged?.Invoke(this, new(nameof(HasSavedPassword)));
            if (!RemoteAppUseRdWeb || string.IsNullOrWhiteSpace(RemoteApp.FeedAddress))
                ResetPublishedRemoteApps();
            if (IsEditing)
                RestoreSavedRemoteAppIcon();
        }
    }
    public string Status
    {
        get => status;
        private set
        {
            int version = ++statusVersion;
            Set(ref status, value);
            if (value.Length != 0)
                _ = ClearStatusAfterDelayAsync(version);
        }
    }

    private async Task ClearStatusAfterDelayAsync(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(15));
        if (statusVersion == version)
            Status = string.Empty;
    }
    public bool TrustProfile { get => trustProfile; set => Set(ref trustProfile, value); }
    public bool OpenInSeparateWindow { get => openInSeparateWindow; set => Set(ref openInSeparateWindow, value); }
    public bool IsEditing { get => isEditing; private set => Set(ref isEditing, value); }
    public bool CanEdit => !isBusy;
    public bool CanSaveProfile => IsEditing && CanEdit && (ProfileHasChanges ||
        (editingProfile is null && !string.IsNullOrWhiteSpace(ProfileName) && !string.IsNullOrWhiteSpace(ProfileAddress)));
    internal bool ProfileNeedsSave => editingProfile is null || ProfileHasChanges;
    private bool ProfileHasChanges => editorSnapshot is not null &&
        (editorSnapshot != CaptureEditorSnapshot() || !string.IsNullOrEmpty(ProfilePassword) || RemovePassword);

    public string ProfileEditorTitle
    {
        get
        {
            string title = editingProfile is null ? "Новая" : editorStartedAsNew
                ? $"Профиль сервера - {editingProfile.Name}"
                : $"Профиль сервера: {editingProfile.Name}";
            return ProfileHasChanges ? $"{title} *" : title;
        }
    }

    internal void RefreshProfileEditorTitle()
    {
        PropertyChanged?.Invoke(this, new(nameof(ProfileEditorTitle)));
        PropertyChanged?.Invoke(this, new(nameof(CanSaveProfile)));
    }

    private sealed record EditorSnapshot(string? Name, string? Address, string? Login, bool TrustServer,
        bool OpenSeparately, RdpDisplayOptions Display, RdpLocalResources Resources, RdpRemoteAppOptions RemoteApp,
        RdpGatewayOptions Gateway);

    private EditorSnapshot CaptureEditorSnapshot() => new(ProfileName ?? "", ProfileAddress ?? "", ProfileUserName ?? "",
        TrustProfile, OpenInSeparateWindow, Display with { }, Resources with { }, RemoteApp with { }, gateway);
    public string? ProfileUserName { get => profileUserName; set => Set(ref profileUserName, value); }
    public string? ProfilePassword { get => profilePassword; set => Set(ref profilePassword, value); }
    public bool RemovePassword { get => removePassword; set => Set(ref removePassword, value); }
    public bool HasSavedPassword => editingProfile?.EncryptedPassword is not null &&
        string.Equals(ProfileAddress?.Trim(), editingProfile.Address, StringComparison.OrdinalIgnoreCase);
    public RdpDisplayOptions Display { get => display; private set => Set(ref display, value); }
    public RdpLocalResources Resources { get => resources; private set => Set(ref resources, value); }
    public IReadOnlyList<RdpDriveChoice> DriveChoices { get; private set; } = [];
    public bool RedirectAllDrives
    {
        get => Resources.DriveTargets == "*";
        set
        {
            if (RedirectAllDrives == value)
                return;

            Resources.DriveTargets = value ? "*" : "";
            LoadDriveChoices();
            RefreshProfileEditorTitle();
        }
    }

    public bool RedirectNewDrives
    {
        get => Resources.DriveTargets.Split(';').Contains(RdpLocalResources.DynamicDrives);
        set => UpdateDriveSelection(RdpLocalResources.DynamicDrives, value);
    }

    private void LoadDriveChoices()
    {
        var selected = Resources.DriveTargets.Split(';');
        DriveChoices = Directory.GetLogicalDrives()
            .Concat(selected.Where(drive => drive.EndsWith('\\')))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(drive => new RdpDriveChoice(drive, selected.Contains(drive, StringComparer.OrdinalIgnoreCase), UpdateDriveSelection))
            .ToArray();
        PropertyChanged?.Invoke(this, new(nameof(DriveChoices)));
        PropertyChanged?.Invoke(this, new(nameof(RedirectAllDrives)));
        PropertyChanged?.Invoke(this, new(nameof(RedirectNewDrives)));
    }

    private void UpdateDriveSelection(string target, bool enabled)
    {
        if (RedirectAllDrives)
            return;

        var selected = Resources.DriveTargets.Split(';').ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!(enabled ? selected.Add(target) : selected.Remove(target)))
            return;

        Resources.DriveTargets = RdpLocalResources.NormalizeDriveTargets(string.Join(';', selected));
        if (target == RdpLocalResources.DynamicDrives)
            PropertyChanged?.Invoke(this, new(nameof(RedirectNewDrives)));
        RefreshProfileEditorTitle();
    }
    public RdpRemoteAppOptions RemoteApp
    {
        get => remoteApp;
        private set
        {
            Set(ref remoteApp, value);
            PropertyChanged?.Invoke(this, new(nameof(RemoteAppEnabled)));
            PropertyChanged?.Invoke(this, new(nameof(RemoteAppUseRdWeb)));
            PropertyChanged?.Invoke(this, new(nameof(RemoteAppProgram)));
            RefreshRemoteAppIcon();
        }
    }

    public bool RemoteAppEnabled
    {
        get => RemoteApp.Enabled;
        set
        {
            if (RemoteApp.Enabled == value)
                return;

            RemoteApp.Enabled = value;
            PropertyChanged?.Invoke(this, new(nameof(RemoteAppEnabled)));
        }
    }
    public bool ShowGeneral => editorTab == 0;
    public bool ShowDisplay => editorTab == 1;
    public bool ShowResources => editorTab == 2;
    public bool ShowRemoteApp => editorTab == 3;
    public bool ShowGateway => editorTab == 4;
    public bool ShowGatewayFields => gateway.UsesConfiguredGateway;

    public IReadOnlyList<string> GatewayUsageMethods { get; } =
        ["Без шлюза", "Всегда через шлюз", "Через шлюз, если прямое подключение недоступно",
         "Системные настройки", "Без шлюза (обход локальных адресов)"];

    public IReadOnlyList<string> GatewayCredentialSources { get; } =
        ["Запросить пароль (NTLM)", "Смарт-карта", "Текущая учётная запись Windows",
         "Запросить пароль (базовая проверка)", "Выбрать при подключении", "Веб-аутентификация (внешний клиент)"];

    public int GatewayUsageMethod
    {
        get => gateway.UsageMethod;
        set
        {
            if (value >= 0 && SetGateway(gateway with { UsageMethod = value }))
                PropertyChanged?.Invoke(this, new(nameof(ShowGatewayFields)));
        }
    }

    public string GatewayHostName { get => gateway.HostName; set => SetGateway(gateway with { HostName = value ?? "" }); }
    public bool GatewayUseExplicitProfile { get => gateway.UseExplicitProfile; set => SetGateway(gateway with { UseExplicitProfile = value }); }
    public bool GatewayShareCredentials { get => gateway.ShareCredentials; set => SetGateway(gateway with { ShareCredentials = value }); }
    public int GatewayCredentialSource
    {
        get => gateway.CredentialSource;
        set
        {
            if (value >= 0)
                SetGateway(gateway with { CredentialSource = value });
        }
    }

    private bool SetGateway(RdpGatewayOptions options, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        if (gateway == options)
            return false;

        gateway = options;
        PropertyChanged?.Invoke(this, new(property));
        RefreshProfileEditorTitle();
        return true;
    }

    private void LoadGateway(RdpGatewayOptions options)
    {
        gateway = options;
        PropertyChanged?.Invoke(this, new(nameof(GatewayUsageMethod)));
        PropertyChanged?.Invoke(this, new(nameof(GatewayHostName)));
        PropertyChanged?.Invoke(this, new(nameof(GatewayUseExplicitProfile)));
        PropertyChanged?.Invoke(this, new(nameof(GatewayShareCredentials)));
        PropertyChanged?.Invoke(this, new(nameof(GatewayCredentialSource)));
        PropertyChanged?.Invoke(this, new(nameof(ShowGatewayFields)));
    }

    public string RemoteAppProgram
    {
        get => RemoteApp.Program;
        set
        {
            if (RemoteApp.Program == value)
                return;

            bool sameProgram = string.Equals(RemoteApp.Program?.Trim(), value?.Trim(), StringComparison.OrdinalIgnoreCase);
            RemoteApp.Program = value ?? "";
            PropertyChanged?.Invoke(this, new(nameof(RemoteAppProgram)));
            if (!sameProgram)
            {
                SelectedPublishedRemoteApp = null;
                RestoreSavedRemoteAppIcon();
            }
        }
    }

    public ImageSource? ProfileRemoteAppIcon => draftRemoteAppIcon is { } png
        ? ImageSource.FromStream(() => new MemoryStream(png, writable: false))
        : RemoteAppIconCache.GetExistingPath(RemoteApp.IconKey) is { } path
            ? ImageSource.FromStream(() => File.OpenRead(path)) : null;

    public bool HasProfileRemoteAppIcon => draftRemoteAppIcon is not null || RemoteAppIconCache.GetExistingPath(RemoteApp.IconKey) is not null;

    private void RefreshRemoteAppIcon()
    {
        PropertyChanged?.Invoke(this, new(nameof(ProfileRemoteAppIcon)));
        PropertyChanged?.Invoke(this, new(nameof(HasProfileRemoteAppIcon)));
    }

    private void RestoreSavedRemoteAppIcon()
    {
        draftRemoteAppIcon = null;
        RemoteApp.IconKey = editingProfile is { } saved &&
            string.Equals(saved.Address?.Trim(), ProfileAddress?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(saved.RemoteApp.Program?.Trim(), RemoteApp.Program?.Trim(), StringComparison.OrdinalIgnoreCase)
                ? saved.RemoteApp.IconKey : "";
        RefreshRemoteAppIcon();
    }

    public bool RemoteAppUseRdWeb
    {
        get => RemoteApp.UseRdWeb;
        set
        {
            if (RemoteApp.UseRdWeb == value)
                return;

            RemoteApp.UseRdWeb = value;
            PropertyChanged?.Invoke(this, new(nameof(RemoteAppUseRdWeb)));
            ResetPublishedRemoteApps();
            RefreshProfileEditorTitle();
        }
    }

    public string RemoteAppFeedAddress
    {
        get => RemoteApp.FeedAddress;
        set
        {
            if (RemoteApp.FeedAddress == value)
                return;

            RemoteApp.FeedAddress = value;
            PropertyChanged?.Invoke(this, new(nameof(RemoteAppFeedAddress)));
            ResetPublishedRemoteApps();
        }
    }

    public IReadOnlyList<PublishedRemoteApp> PublishedRemoteApps
    {
        get => publishedRemoteApps;
        private set
        {
            Set(ref publishedRemoteApps, value);
            PropertyChanged?.Invoke(this, new(nameof(HasPublishedRemoteApps)));
        }
    }

    public bool HasPublishedRemoteApps => PublishedRemoteApps.Count > 0;
    public string RemoteAppFeedStatus { get => remoteAppFeedStatus; private set => Set(ref remoteAppFeedStatus, value); }

    public PublishedRemoteApp? SelectedPublishedRemoteApp
    {
        get => selectedPublishedRemoteApp;
        set
        {
            if (ReferenceEquals(selectedPublishedRemoteApp, value))
                return;

            Set(ref selectedPublishedRemoteApp, value);
            if (value is not null)
                _ = ApplyPublishedRemoteAppAsync(value);
        }
    }

    private int EditorTab
    {
        set
        {
            editorTab = value;
            PropertyChanged?.Invoke(this, new(nameof(ShowGeneral)));
            PropertyChanged?.Invoke(this, new(nameof(ShowDisplay)));
            PropertyChanged?.Invoke(this, new(nameof(ShowResources)));
            PropertyChanged?.Invoke(this, new(nameof(ShowRemoteApp)));
            PropertyChanged?.Invoke(this, new(nameof(ShowGateway)));
        }
    }

    private void SetBusy(bool value)
    {
        isBusy = value;
        PropertyChanged?.Invoke(this, new(nameof(CanEdit)));
        PropertyChanged?.Invoke(this, new(nameof(CanSaveProfile)));
    }

    private void ResetPublishedRemoteApps()
    {
        loadedRemoteAppFeed = null;
        SelectedPublishedRemoteApp = null;
        PublishedRemoteApps = [];
        publishedAppIcons.Clear();
        RemoteAppFeedStatus = "";
    }

    internal async Task<byte[]?> GetPublishedAppIconAsync(PublishedRemoteApp app)
    {
        if (publishedAppIcons.TryGetValue(app, out var cached))
            return cached;

        var png = await RemoteAppIconCache.DecodeAsync(app.IconContents);
        if (png is null && app.ExecutablePath.Length != 0 &&
            string.Equals(app.ExecutablePath, RemoteApp.Program?.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase))
        {
            png = draftRemoteAppIcon;
            if (png is null && RemoteAppIconCache.GetExistingPath(RemoteApp.IconKey) is { } iconPath)
                png = await File.ReadAllBytesAsync(iconPath);
        }

        if (png is not null && PublishedRemoteApps.Contains(app))
            publishedAppIcons[app] = png;

        return png;
    }

    private async Task<NetworkCredential> GetRemoteAppDiscoveryCredentialAsync()
    {
        string login = ProfileUserName?.Trim() ?? "";
        string? password = ProfilePassword;
        if (string.IsNullOrEmpty(password) && !RemovePassword && HasSavedPassword && editingProfile is { } saved &&
            string.Equals(login, saved.UserName, StringComparison.OrdinalIgnoreCase))
        {
            password = await store.ReadPasswordAsync(saved);
        }

        if (login.Length == 0 || string.IsNullOrEmpty(password))
            throw new FormatException("Для загрузки приложений укажите логин и пароль карточки в разделе «Общие».");

        return CreateDiscoveryCredential(login, password);
    }

    private static NetworkCredential CreateDiscoveryCredential(string login, string password)
    {
        string[] account = login.Trim().Split('\\', 2);
        return account.Length == 2 ? new NetworkCredential(account[1], password, account[0]) : new NetworkCredential(login, password);
    }

    private async Task LoadPublishedRemoteAppsAsync()
    {
        if (isBusy || !IsEditing || !RemoteAppEnabled)
            return;

        ResetPublishedRemoteApps();
        RemoteAppFeedStatus = "Загрузка опубликованных приложений…";
        SetBusy(true);
        try
        {
            var credential = await GetRemoteAppDiscoveryCredentialAsync();
            var feed = RemoteAppUseRdWeb ? RemoteAppFeed.ResolveAddress(RemoteAppFeedAddress, ProfileAddress) : null;
            var apps = feed is null
                ? await RemoteAppInventory.ListAsync(ProfileAddress, credential)
                : await RemoteAppFeed.ListAsync(feed, credential);
            if (!IsEditing)
                return;

            loadedRemoteAppFeed = feed;
            PublishedRemoteApps = apps;
            RemoteAppFeedStatus = apps.Count == 0 ? "Опубликованные приложения не найдены." : "Выберите приложение из списка.";
        }
        catch (Exception ex) when (ex is FormatException or HttpRequestException or OperationCanceledException or XmlException or COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (IsEditing)
                RemoteAppFeedStatus = $"Не удалось загрузить приложения. {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ApplyPublishedRemoteAppAsync(PublishedRemoteApp app)
    {
        if (isBusy || !IsEditing || !PublishedRemoteApps.Contains(app))
            return;

        RemoteAppFeedStatus = "Загрузка параметров приложения…";
        SetBusy(true);
        try
        {
            RdpRemoteAppOptions options;
            byte[]? icon = app.IconContents;
            if (app.ExecutablePath.Length != 0)
            {
                options = RemoteApp with { Enabled = true, Program = app.ExecutablePath, Arguments = app.RequiredArguments, WorkingDirectory = "" };
            }
            else if (loadedRemoteAppFeed is { } feed)
            {
                var credential = await GetRemoteAppDiscoveryCredentialAsync();
                options = (await RemoteAppFeed.ReadLaunchOptionsAsync(feed, app, credential)) with
                {
                    FeedAddress = RemoteAppFeedAddress,
                    UseRdWeb = true
                };
                try { icon = await RemoteAppFeed.ReadIconAsync(feed, app, credential); }
                catch (Exception ex) when (ex is FormatException or HttpRequestException or OperationCanceledException or IOException)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            }
            else
            {
                return;
            }

            var png = await RemoteAppIconCache.DecodeAsync(icon);
            if (png is null && app.ExecutablePath.Length != 0)
            {
                var credential = await GetRemoteAppDiscoveryCredentialAsync();
                var extractedIcon = await RemoteAppInventory.ReadIconAsync(ProfileAddress, app, credential);
                png = await RemoteAppIconCache.DecodeAsync(extractedIcon);
            }
            if (!IsEditing)
                return;

            ApplyPublishedRemoteApp(app, options, png);
        }
        catch (Exception ex) when (ex is FormatException or HttpRequestException or OperationCanceledException or COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (IsEditing)
            {
                SelectedPublishedRemoteApp = null;
                RemoteAppFeedStatus = $"Не удалось загрузить параметры приложения. {ex.Message}";
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyPublishedRemoteApp(PublishedRemoteApp app, RdpRemoteAppOptions options, byte[]? png)
    {
        if (png is not null)
        {
            publishedAppIcons[app] = png;
            draftRemoteAppIcon = png;
            options.IconKey = RemoteAppIconCache.GetKey(png);
        }
        else if (string.Equals(RemoteApp.Program, options.Program, StringComparison.OrdinalIgnoreCase))
        {
            options.IconKey = RemoteApp.IconKey;
        }
        else
        {
            draftRemoteAppIcon = null;
            options.IconKey = "";
        }

        RemoteApp = options;
        if (string.IsNullOrWhiteSpace(ProfileName))
            ProfileName = app.Name;
        RefreshProfileEditorTitle();
        RemoteAppFeedStatus = HasProfileRemoteAppIcon ? $"Выбрано: {app.Name}" : $"Выбрано: {app.Name}. Сервер не предоставил доступную иконку.";
    }

    internal Task<string?> ReadSessionPasswordAsync(ServerProfile profile) => store.ReadPasswordAsync(profile);

    private async Task ConnectAsync(ServerProfile? profile)
    {
        if (isBusy || profile is null)
            return;

        if (ActivateExistingSession?.Invoke(profile.Id) == true)
            return;

        if (!RdpEndpoint.TryParse(profile.Address, out var endpoint))
        {
            ConnectionFailed?.Invoke(profile, "Укажите корректный адрес сервера в профиле.");
            return;
        }

        string address = endpoint.ToString();
        string? password;
        try
        {
            password = await ReadSessionPasswordAsync(profile);
        }
        catch (Exception ex) when (ex is FormatException or UnauthorizedAccessException or COMException or ArgumentException)
        {
            ConnectionFailed?.Invoke(profile,
                $"Не удалось расшифровать пароль для «{profile.Name}». Замените или удалите его в профиле.");
            return;
        }

        ConnectRequested?.Invoke(address, profile, password);
    }

    internal void ReportFileStatus(string message) => Status = message;

    internal void ReplaceProfiles(List<ServerProfile> imported)
    {
        foreach (var profile in imported)
            if (profile.Id == Guid.Empty) profile.Id = Guid.NewGuid();
        store.Save(imported);
        SelectedProfile = null;
        profiles = imported;
        foreach (var profile in profiles)
            profile.RemoteAppIconFile = RemoteAppIconCache.GetExistingPath(profile.RemoteApp.IconKey);
        RefreshProfileGroups();
        PropertyChanged?.Invoke(this, new(nameof(Profiles)));
        SelectedProfile = profiles.FirstOrDefault();
        RemoteAppIconChanged?.Invoke();
    }

    internal bool CanImportProfile()
    {
        if (isBusy || IsEditing)
        {
            Status = "Завершите редактирование текущего профиля перед импортом.";
            return false;
        }

        return true;
    }

    internal ServerProfile[] FindImportMatches(ServerProfile imported) =>
        profiles.Where(profile => RdpFileImport.MatchesProfile(imported, profile)).ToArray();

    internal void OpenImportedProfile(ServerProfile imported, ServerProfile? existing)
    {
        if (!CanImportProfile() || (existing is not null && !profiles.Contains(existing)))
            return;

        Status = "";
        if (existing is not null)
            SelectedProfile = existing;
        EditProfile(existing, imported);
    }

    private void EditProfile(ServerProfile? profile, ServerProfile? imported = null)
    {
        if (isBusy || IsEditing)
            return;

        if (inlineRenameProfile is { } rename)
            CancelInlineRename(rename);
        editingProfile = profile;
        editorStartedAsNew = profile is null;
        profile ??= imported;
        ProfileName = profile?.Name ?? string.Empty;
        ProfileAddress = profile?.Address ?? string.Empty;
        TrustProfile = profile?.TrustServer ?? false;
        OpenInSeparateWindow = profile?.OpenInSeparateWindow ?? true;
        ProfileUserName = profile?.UserName ?? "";
        ProfilePassword = null;
        RemovePassword = false;
        Display = profile is null ? new() : profile.Display with { };
        Resources = profile is null ? new() : profile.Resources with { };
        LoadDriveChoices();
        LoadGateway(profile?.Gateway ?? new());
        draftRemoteAppIcon = null;
        RemoteApp = profile is null ? new() : profile.RemoteApp with { };
        PropertyChanged?.Invoke(this, new(nameof(RemoteAppFeedAddress)));
        ResetPublishedRemoteApps();
        Status = "";
        EditorTab = 0;
        PropertyChanged?.Invoke(this, new(nameof(HasSavedPassword)));
        editorSnapshot = CaptureEditorSnapshot();
        IsEditing = true;
    }

    internal void CloseEditor()
    {
        IsEditing = false;
        ProfilePassword = null;
        draftRemoteAppIcon = null;
        editingProfile = null;
        editorSnapshot = null;
    }

    internal async Task<bool> SaveProfileAsync()
    {
        if (isBusy || !IsEditing)
            return false;

        string name = ProfileName?.Trim() ?? string.Empty;
        string host = ProfileAddress?.Trim() ?? string.Empty;
        if (IsDuplicateProfileName(name, editingProfile?.Id))
        {
            EditorTab = 0;
            ProfileNameError = "Профиль с таким наименованием уже существует.";
            return false;
        }

        if (name.Length == 0 || !RdpEndpoint.TryParse(host, out _))
        {
            Status = "Укажите название и корректный адрес сервера.";
            return false;
        }

        if (gateway.UsesConfiguredGateway && (gateway.UseExplicitProfile || gateway.HostName.Length > 0) && !gateway.HasValidHost)
        {
            EditorTab = 4;
            Status = "Укажите корректный адрес шлюза RD.";
            return false;
        }

        if (RemoteApp.Enabled && string.IsNullOrWhiteSpace(RemoteApp.Program))
        {
            EditorTab = 3;
            Status = "Укажите псевдоним или путь к опубликованному приложению RemoteApp.";
            return false;
        }

        SetBusy(true);
        try
        {
            bool sameHost = string.Equals(host, editingProfile?.Address, StringComparison.OrdinalIgnoreCase);
            string? encrypted = RemovePassword || !sameHost ? null : editingProfile?.EncryptedPassword;
            if (ProfilePassword is { Length: > 0 } password)
                encrypted = await store.ProtectPasswordAsync(password);

            if (draftRemoteAppIcon is { } png)
                await RemoteAppIconCache.SaveAsync(RemoteApp.IconKey, png);

            if (!IsEditing)
                return false;

            var profile = new ServerProfile(name, host, TrustProfile)
            {
                Id = editingProfile?.Id ?? Guid.NewGuid(),
                IsSessionActive = editingProfile?.IsSessionActive ?? false,
                OpenInSeparateWindow = OpenInSeparateWindow,
                UserName = ProfileUserName?.Trim() ?? "",
                EncryptedPassword = encrypted,
                Display = Display with { },
                Resources = Resources with { },
                Gateway = gateway with { HostName = gateway.HostName.Trim() },
                RemoteApp = RemoteApp with
                {
                    Program = RemoteApp.Program?.Trim() ?? "",
                    Arguments = RemoteApp.Arguments?.Trim() ?? "",
                    WorkingDirectory = RemoteApp.WorkingDirectory?.Trim() ?? "",
                    FeedAddress = RemoteApp.FeedAddress?.Trim() ?? ""
                }
            };
            List<ServerProfile> updated = [.. profiles];
            profile.RemoteAppIconFile = RemoteAppIconCache.GetExistingPath(profile.RemoteApp.IconKey);
            int index = editingProfile is null ? -1 : updated.IndexOf(editingProfile);
            if (index < 0)
                updated.Add(profile);
            else
                updated[index] = profile;

            bool renamed = index >= 0 && editingProfile!.Name != profile.Name;
            store.Save(updated);
            profiles = updated;
            RefreshProfileGroups();
            SelectedProfile = profile;
            editingProfile = profile;
            ProfileName = profile.Name;
            ProfileAddress = profile.Address;
            ProfileUserName = profile.UserName;
            ProfilePassword = null;
            RemovePassword = false;
            LoadGateway(profile.Gateway);
            draftRemoteAppIcon = null;
            RemoteApp = profile.RemoteApp with { };
            PropertyChanged?.Invoke(this, new(nameof(RemoteAppFeedAddress)));
            PropertyChanged?.Invoke(this, new(nameof(HasSavedPassword)));
            editorSnapshot = CaptureEditorSnapshot();
            RefreshProfileEditorTitle();
            Status = $"Профиль «{name}» сохранён.";
            if (renamed)
                ProfileRenamed?.Invoke(profile);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            Status = "Не удалось сохранить профиль сервера.";
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

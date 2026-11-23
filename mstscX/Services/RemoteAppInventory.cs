using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using mstscX.Models;

namespace mstscX.Services;

internal static class RemoteAppInventory
{
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int LogonFailed = unchecked((int)0x8007052E);
    private const int RpcUnavailable = unchecked((int)0x800706BA);
    private const string SessionHostNamespace = @"root\cimv2\TerminalServices";

    internal static Task<IReadOnlyList<PublishedRemoteApp>> ListAsync(string? address, NetworkCredential credential)
    {
        if (!RdpEndpoint.TryParse(address, out var endpoint))
            throw new FormatException(global::mstscX.Services.L.T("Укажите корректный адрес сервера в разделе «Общие»."));

        return Task.Run(() => List(endpoint.Host, credential));
    }

    private static IReadOnlyList<PublishedRemoteApp> List(string host, NetworkCredential credential)
    {
        const string broker = @"root\cimv2\rdms";
        var failures = new List<(string Namespace, int Code, Exception Error)>();

        IReadOnlyList<PublishedRemoteApp>? TryQuery(string wmiNamespace, string className, string name, string alias, string path)
        {
            try
            {
                return Query(host, credential, wmiNamespace, className, name, alias, path);
            }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
            {
                int code = ex is ManagementException wmiError ? (int)wmiError.ErrorCode : ex.HResult;
                if (code == LogonFailed)
                    throw new FormatException(global::mstscX.Services.L.T("Сервер не принял логин или пароль карточки (0x8007052E)."), ex);
                if (code == RpcUnavailable)
                    throw new FormatException(global::mstscX.Services.L.T("Сервер не отвечает на запрос WMI/RPC (0x800706BA)."), ex);

                failures.Add((wmiNamespace, code, ex));
                return null;
            }
        }

        var hostApps = TryQuery(SessionHostNamespace, "Win32_TSPublishedApplication", "Name", "Alias", "Path");
        if (hostApps is { Count: > 0 })
            return hostApps;

        var brokerApps = TryQuery(broker, "Win32_RDMSPublishedApplication", "DisplayName", "AppAlias", "AppPath");
        if (brokerApps is { Count: > 0 })
            return brokerApps;

        foreach (var (wmiNamespace, code, error) in failures)
        {
            if (code is AccessDenied or (int)ManagementStatus.AccessDenied or unchecked((int)0x80041062))
                throw new FormatException(global::mstscX.Services.L.F("Учётной записи карточки отказано в чтении каталога WMI {0} (0x{1:X8}).", wmiNamespace, code), error);

            if (code is not ((int)ManagementStatus.InvalidNamespace or (int)ManagementStatus.InvalidClass or (int)ManagementStatus.ProviderNotFound))
                throw new FormatException(global::mstscX.Services.L.F("Не удалось прочитать каталог WMI {0} (0x{1:X8}). {2}", wmiNamespace, code, error.Message), error);
        }

        return hostApps ?? brokerApps ?? throw new FormatException(global::mstscX.Services.L.T("Сервер не предоставляет WMI-каталог RemoteApp (RDS)."));
    }

    internal static Task<byte[]?> ReadIconAsync(string? address, PublishedRemoteApp app, NetworkCredential credential)
    {
        if (!RdpEndpoint.TryParse(address, out var endpoint))
            return Task.FromResult<byte[]?>(null);

        return Task.Run(() => ReadIcon(endpoint.Host, app, credential));
    }

    internal static async Task<byte[]?> FindIconAsync(string address, string program, NetworkCredential credential)
    {
        if (!program.StartsWith("||", StringComparison.Ordinal))
            return await ReadIconAsync(address, new PublishedRemoteApp("", "", null, program), credential);

        var apps = await ListAsync(address, credential);
        var matches = apps.Where(app => string.Equals(app.Alias.TrimStart('|'), program.TrimStart('|'),
            StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matches.Length != 1)
            return null;

        var match = matches[0];
        return match.IconContents ?? await ReadIconAsync(address, match, credential);
    }

    private static byte[]? ReadIcon(string host, PublishedRemoteApp app, NetworkCredential credential)
    {
        (string Path, int Index)[] sources = [(app.IconPath, app.IconIndex), (app.ExecutablePath, 0)];
        try
        {
            using var iconProvider = new ManagementClass(CreateScope(host, SessionHostNamespace, credential),
                new ManagementPath("Win32_TSGetIcon"), null);
            foreach (var (path, index) in sources.Distinct())
            {
                string localPath = path.Trim().Trim('"');
                // GetIcon runs on the server. Do not ask it to open a network share or a device path.
                if (localPath.Length < 4 || !char.IsAsciiLetter(localPath[0]) || localPath[1] != ':' ||
                    localPath[2] is not ('\\' or '/') || localPath.AsSpan(2).Contains(':'))
                    continue;

                using var parameters = iconProvider.GetMethodParameters("GetIcon");
                parameters["FilePath"] = localPath;
                parameters["Index"] = index;
                using var result = iconProvider.InvokeMethod("GetIcon", parameters, new InvokeMethodOptions
                {
                    Timeout = TimeSpan.FromSeconds(15)
                });
                if (result is not null && result["ReturnValue"] is uint resultCode && resultCode == 0 &&
                    result["IconContents"] is byte[] { Length: > 0 and <= RemoteAppIconCache.MaxSourceSize } icon)
                    return icon;
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        return null;
    }

    private static IReadOnlyList<PublishedRemoteApp> Query(string host, NetworkCredential credential,
        string wmiNamespace, string className, string nameField, string aliasField, string pathField)
    {
        var scope = CreateScope(host, wmiNamespace, credential);
        var query = new ObjectQuery($"SELECT {nameField}, {aliasField}, {pathField}, CommandLineSetting, RequiredCommandLine, IconContents, IconPath, IconIndex FROM {className}");
        using var searcher = new ManagementObjectSearcher(scope, query, new System.Management.EnumerationOptions
        {
            ReturnImmediately = true,
            Rewindable = false,
            Timeout = TimeSpan.FromSeconds(15)
        });
        using var rows = searcher.Get();

        var apps = new List<PublishedRemoteApp>();
        foreach (ManagementBaseObject row in rows)
        {
            using (row)
            {
                string Read(string field) => Convert.ToString(row[field])?.Trim() ?? "";
                string path = Read(pathField);
                if (path.Length == 0)
                    continue;

                string name = Read(nameField);
                string arguments = Read("CommandLineSetting") == "2" ? Read("RequiredCommandLine") : "";
                apps.Add(new PublishedRemoteApp(name.Length == 0 ? Path.GetFileName(path) : name,
                    Read(aliasField), null, path, arguments,
                    row["IconContents"] is byte[] { Length: > 0 and <= RemoteAppIconCache.MaxSourceSize } icon ? icon : null,
                    IconPath: Read("IconPath"), IconIndex: row["IconIndex"] is int index ? index : 0));
            }
        }

        return apps.DistinctBy(app => (app.Name, app.Alias, app.ExecutablePath, app.RequiredArguments))
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static ManagementScope CreateScope(string host, string wmiNamespace, NetworkCredential credential) =>
        new($@"\\{host}\{wmiNamespace}", new ConnectionOptions
        {
            Username = string.IsNullOrEmpty(credential.Domain) ? credential.UserName : $"{credential.Domain}\\{credential.UserName}",
            Password = credential.Password,
            Authentication = AuthenticationLevel.PacketPrivacy,
            Impersonation = ImpersonationLevel.Impersonate
        });
}

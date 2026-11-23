using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using mstscX.Models;

namespace mstscX.Services;

internal static class WindowsPowerShell
{
    internal static string? GetUntrustedHost(ServerProfile profile)
    {
        if (!RdpEndpoint.TryParse(profile.Address, out var endpoint))
            throw new FormatException("Укажите корректный адрес сервера в профиле.");
        return IsExplicitlyTrustedHost(endpoint.Host) ? null : endpoint.Host;
    }

    internal static async Task<bool> AddTrustedHostAsync(string host)
    {
        // Never allow a profile to introduce a wildcard or additional list entries.
        if (string.IsNullOrWhiteSpace(host) || host.IndexOfAny(['*', '?', ',']) >= 0)
            throw new FormatException("Укажите конкретное имя или IP-адрес сервера.");
        string hostData = Convert.ToBase64String(Encoding.UTF8.GetBytes(host));
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                $target = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{hostData}}'))
                if (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WinRM\Client' -Name TrustedHosts -ErrorAction SilentlyContinue) { exit 2 }
                Import-Module "$PSHOME\Modules\Microsoft.WSMan.Management\Microsoft.WSMan.Management.psd1"
                Start-Service WinRM
                $path = 'WSMan:\localhost\Client\TrustedHosts'
                $current = [string](Get-Item -LiteralPath $path).Value
                $entries = @($current.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
                if ($entries -notcontains $target) {
                    $updated = if ([string]::IsNullOrWhiteSpace($current)) { $target } else { $current + ',' + $target }
                    Set-Item -LiteralPath $path -Value $updated -Force
                }
                exit 0
            }
            catch { exit 1 }
            """;
        var launch = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        launch.ArgumentList.Add("-NoProfile");
        launch.ArgumentList.Add("-NonInteractive");
        launch.ArgumentList.Add("-EncodedCommand");
        launch.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        Process? process;
        try { process = Process.Start(launch); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; }
        using (process)
        {
            if (process is null) throw new InvalidOperationException("Не удалось запустить настройку TrustedHosts.");
            await process.WaitForExitAsync();
            if (process.ExitCode == 2)
                throw new InvalidOperationException("TrustedHosts управляется групповой политикой. Обратитесь к администратору компьютера.");
            if (process.ExitCode != 0 || !IsExplicitlyTrustedHost(host))
                throw new InvalidOperationException("Не удалось добавить сервер в TrustedHosts. Проверьте права администратора и доступность службы WinRM на этом компьютере.");
        }
        return true;
    }

    internal static async Task OpenAsync(ServerProfile? profile, string? password)
    {
        string executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var launch = new ProcessStartInfo(executable) { UseShellExecute = true };
        if (profile is null)
        {
            using var local = Process.Start(launch);
            return;
        }

        if (!RdpEndpoint.TryParse(profile.Address, out var endpoint))
            throw new FormatException("Укажите корректный адрес сервера в профиле.");

        string? pipeName = string.IsNullOrEmpty(password) ? null : $"mstscX.PowerShell.{Guid.NewGuid():N}";
        using var pipe = pipeName is null ? null : new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        string startup = CreateStartupCommand(endpoint.Host, profile.UserName.Trim(), pipeName);
        launch.ArgumentList.Add("-NoProfile");
        launch.ArgumentList.Add("-NoExit");
        launch.ArgumentList.Add("-EncodedCommand");
        launch.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(startup)));

        using var process = Process.Start(launch);
        if (pipe is not null && process is not null && password is not null)
            await SendPasswordAsync(pipe, process.Id, password);
    }

    private static async Task SendPasswordAsync(NamedPipeServerStream pipe, int processId, string password)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientId) || clientId != (uint)processId)
                return;

            byte[] buffer = Encoding.Unicode.GetBytes(password);
            try
            {
                await pipe.WriteAsync(BitConverter.GetBytes(buffer.Length), timeout.Token).ConfigureAwait(false);
                await pipe.WriteAsync(buffer, timeout.Token).ConfigureAwait(false);
                await pipe.ReadAsync(new byte[1], timeout.Token).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            Debug.WriteLine(ex);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        out uint clientProcessId);

    private static string CreateStartupCommand(string host, string userName, string? pipeName)
    {
        bool useHttps = IPAddress.TryParse(host, out _) && !IsExplicitlyTrustedHost(host);
        string transport = useHttps ? "HTTPS" : "HTTP";
        int port = useHttps ? 5986 : 5985;
        string useSsl = useHttps ? "-UseSSL" : "";

        // Pass profile values as data so that the console never interprets them as commands.
        string hostData = Convert.ToBase64String(Encoding.UTF8.GetBytes(host));
        string userData = Convert.ToBase64String(Encoding.UTF8.GetBytes(userName));
        return $$"""
            $computerName = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{hostData}}'))
            $userName = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{userData}}'))
            $message = "Вход на $computerName через WinRM ({{transport}}, порт {{port}})"
            $passwordPipe = '{{pipeName}}'
            Write-Host $message
            try {
                $savedPassword = if ($passwordPipe) {
                    $pipe = $null
                    $reader = $null
                    $bytes = $null
                    $characters = $null
                    try {
                        $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $passwordPipe, [IO.Pipes.PipeDirection]::InOut,
                            [IO.Pipes.PipeOptions]::None, [Security.Principal.TokenImpersonationLevel]::Identification)
                        $pipe.Connect(5000)
                        $reader = [IO.BinaryReader]::new($pipe)
                        $length = $reader.ReadInt32()
                        if ($length -le 0 -or $length -gt 1048576 -or $length % 2) { throw 'Неверный размер пароля.' }
                        $bytes = $reader.ReadBytes($length)
                        if ($bytes.Length -ne $length) { throw 'Не удалось прочитать пароль полностью.' }
                        $characters = [Text.Encoding]::Unicode.GetChars($bytes)
                        $secret = [Security.SecureString]::new()
                        foreach ($character in $characters) { $secret.AppendChar($character) }
                        $secret.MakeReadOnly()
                        $pipe.WriteByte(1)
                        $secret
                    }
                    catch { Write-Warning 'Сохранённый пароль недоступен. Введите его вручную.' }
                    finally {
                        if ($bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
                        if ($characters) { [Array]::Clear($characters, 0, $characters.Length) }
                        if ($reader) { $reader.Dispose() }
                        if ($pipe) { $pipe.Dispose() }
                    }
                }
                if ($savedPassword) {
                    if (!$userName) { $userName = Read-Host 'Имя пользователя' }
                    $credential = [Management.Automation.PSCredential]::new($userName, $savedPassword)
                }
                else {
                    $promptOptions = @{ Message = $message }
                    if ($userName) { $promptOptions.UserName = $userName }
                    $credential = Get-Credential @promptOptions
                }
                if ($credential) {
                    $session = New-PSSession -ComputerName $computerName -Credential $credential {{useSsl}} -ErrorAction Stop
                    try { $Host.PushRunspace($session.Runspace) }
                    catch { Remove-PSSession -Session $session; throw }
                }
            }
            catch { Write-Error $_.Exception.Message }
            finally {
                if ($secret) { $secret.Dispose() }
                Remove-Variable -Name credential, savedPassword, secret, character -ErrorAction SilentlyContinue
            }
            """;
    }

    private static bool IsExplicitlyTrustedHost(string host)
    {
        foreach (var (path, valueName) in new[]
        {
            (@"SOFTWARE\Policies\Microsoft\Windows\WinRM\Client", "TrustedHosts"),
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\WSMAN\Client", "trusted_hosts")
        })
        {
            string? hosts;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                hosts = key?.GetValue(valueName) as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                Debug.WriteLine(ex);
                return false;
            }
            if (hosts is null) continue;
            // Only a deliberately trusted, exact host permits HTTP for an IP.
            // Wildcards do not trigger an automatic transport change.
            return hosts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains(host, StringComparer.OrdinalIgnoreCase);
        }
        return false;
    }
}

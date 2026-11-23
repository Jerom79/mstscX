[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository = 'Jerom79/mstscX'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$installer = Get-Item -LiteralPath $InstallerPath
if ($installer.PSIsContainer -or $installer.Name -notmatch '^mstscX-Setup-(\d+\.\d+\.\d+\.\d+)\.exe$') {
    throw 'Укажите версионный EXE установщика mstscX.'
}
$version = $Matches[1]
$tag = 'v' + $version
$hash = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not $PSCmdlet.ShouldProcess("$Repository / $tag", "Опубликовать только $($installer.Name)")) { return }

$headers = @{}
$credentials = @{}
$oldPrompt = $env:GIT_TERMINAL_PROMPT
$oldInteractive = $env:GCM_INTERACTIVE
try {
    $token = $env:GH_TOKEN
    if (-not $token) { $token = $env:GITHUB_TOKEN }
    if (-not $token) {
        $env:GIT_TERMINAL_PROMPT = '0'
        $env:GCM_INTERACTIVE = 'Never'
        $lines = "protocol=https`nhost=github.com`n`n" | git credential fill
        if ($LASTEXITCODE -ne 0) { throw 'Настройте авторизацию GitHub в Git или переменную GH_TOKEN.' }
        foreach ($line in $lines) {
            $pair = $line -split '=', 2
            if ($pair.Length -eq 2) { $credentials[$pair[0]] = $pair[1] }
        }
        $token = $credentials['password']
    }
    if (-not $token) { throw 'Нет авторизации GitHub. Используйте Git Credential Manager или GH_TOKEN.' }
    $headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'User-Agent' = 'mstscX-release' }
    $base = "https://api.github.com/repos/$Repository"
    $release = $null
    try { $release = Invoke-RestMethod "$base/releases/tags/$tag" -Headers $headers }
    catch {
        if (-not $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 404) { throw }
    }
    if (-not $release) {
        # Do not push or commit source changes implicitly. Use the remote default branch.
        $repo = Invoke-RestMethod $base -Headers $headers
        $branch = [Uri]::EscapeDataString($repo.default_branch)
        $commit = Invoke-RestMethod "$base/commits/$branch" -Headers $headers
        $body = @{
            tag_name = $tag; target_commitish = $commit.sha; name = "mstscX $version"
            draft = $true; prerelease = $false
            body = "Установщик mstscX $version для Windows x64.`n`nСкачайте и запустите $($installer.Name). Настройки и сохранённые подключения при обновлении сохраняются.`n`nРазмещён только установщик, без архива приложения.`n`nSHA-256: $hash"
        } | ConvertTo-Json
        $release = Invoke-RestMethod "$base/releases" -Headers $headers -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($body))
    }
    $assets = @(Invoke-RestMethod "$base/releases/$($release.id)/assets" -Headers $headers | ForEach-Object { $_ })
    if ($assets.Count -gt 1 -or ($assets.Count -eq 1 -and $assets[0].name -ne $installer.Name)) {
        throw 'В релизе есть другие вложения. Они не изменены; проверьте релиз вручную.'
    }
    if ($assets.Count -eq 1) {
        $asset = $assets[0]
        $digest = $asset.PSObject.Properties['digest']
        if ($asset.state -ne 'uploaded' -or $asset.size -ne $installer.Length -or
            -not $digest -or $digest.Value -ne "sha256:$hash") {
            throw 'В релизе уже есть другой или незавершённый установщик. Автоматическая перезапись запрещена.'
        }
    }
    else {
        if (-not $release.draft) { throw 'Опубликованный релиз не содержит установщика; проверьте его вручную.' }
        $upload = $release.upload_url -replace '\{.*$', ''
        if (-not $upload.StartsWith('https://uploads.github.com/', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Неожиданный адрес загрузки GitHub.'
        }
        $asset = Invoke-RestMethod ($upload + '?name=' + [Uri]::EscapeDataString($installer.Name)) -Headers $headers -Method Post -InFile $installer.FullName -ContentType 'application/octet-stream' -TimeoutSec 600
        if ($asset.state -ne 'uploaded' -or $asset.size -ne $installer.Length) { throw 'Проверка загруженного EXE не пройдена.' }
    }
    if ($release.draft) {
        $release = Invoke-RestMethod "$base/releases/$($release.id)" -Headers $headers -Method Patch -ContentType 'application/json' -Body '{"draft":false}'
    }
    Write-Host "Установщик опубликован: $($release.html_url)"
}
finally {
    $headers.Clear()
    $credentials.Clear()
    $token = $null
    $lines = $null
    $env:GIT_TERMINAL_PROMPT = $oldPrompt
    $env:GCM_INTERACTIVE = $oldInteractive
}

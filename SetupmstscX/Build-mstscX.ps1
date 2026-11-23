[CmdletBinding(SupportsShouldProcess)]
param([string]$InnoCompiler, [switch]$SkipPublish)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$setupRoot = (Get-Item -LiteralPath $PSScriptRoot).FullName
$project = Join-Path (Split-Path -Parent $setupRoot) 'mstscX\mstscX.csproj'
$distribution = Join-Path $setupRoot 'Distrib'
$output = Join-Path $setupRoot 'Setup'

function Assert-LocalDirectory([string]$path) {
    $fullPath = [IO.Path]::GetFullPath($path)
    if (-not $fullPath.StartsWith($setupRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Папка находится вне каталога установщика: $fullPath"
    }

    if (-not (Test-Path -LiteralPath $fullPath)) { return }

    $pending = [Collections.Generic.Stack[IO.FileSystemInfo]]::new()
    $pending.Push((Get-Item -LiteralPath $fullPath -Force))
    while ($pending.Count -gt 0) {
        $item = $pending.Pop()
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Обнаружена ссылка или точка соединения: $($item.FullName)"
        }
        if ($item.PSIsContainer) {
            foreach ($child in Get-ChildItem -LiteralPath $item.FullName -Force) {
                $pending.Push($child)
            }
        }
    }
}

if ((Get-Item -LiteralPath $setupRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'Каталог установщика должен быть обычной папкой.'
}
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Проект приложения не найден: $project"
}

if (-not $InnoCompiler) {
    $command = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue
    $candidates = @(
        $(if ($command) { $command.Source })
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $InnoCompiler = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } |
        Select-Object -First 1
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
    throw 'Не найден ISCC.exe. Установите Inno Setup 6 или укажите путь через -InnoCompiler.'
}

if (-not $PSCmdlet.ShouldProcess($setupRoot, 'Опубликовать mstscX Release x64 и собрать установщик')) {
    return
}

$staging = Join-Path $setupRoot ('.Build-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $staging 'Distrib'
$compiled = Join-Path $staging 'Setup'
$previous = Join-Path $staging 'PreviousDistrib'
Assert-LocalDirectory $staging
$null = New-Item -Path $staging, $compiled -ItemType Directory

try {
    & dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish `
        -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=false `
        -p:PublishTrimmed=false -p:DebugType=None "-p:BuildShortcutPath=$staging\mstscX.lnk"
    if ($LASTEXITCODE -ne 0) { throw "Ошибка публикации mstscX ($LASTEXITCODE)." }

    Assert-LocalDirectory $publish
    $unusedImage = '^(?:WindowsAppRuntime|splashSplashScreen|appicon(?:Logo|SmallTile|MediumTile|LargeTile|WideTile|StoreLogo))(?:\.(?:scale|targetsize|altform)-.+)?\.png$'
    $unusedFiles = @(Get-ChildItem -LiteralPath $publish -File -Recurse | Where-Object {
        $_.Extension -in '.pdb', '.winmd', '.lib', '.exp', '.obj', '.ilk' -or
        ($_.DirectoryName -eq $publish -and ($_.Name -eq 'AboutAssets.txt' -or $_.Name -match $unusedImage))
    })
    foreach ($file in $unusedFiles) {
        Remove-Item -LiteralPath $file.FullName -Force
    }

    foreach ($file in @('mstscX.exe', 'mstscX.dll', 'mstscX.deps.json', 'mstscX.runtimeconfig.json',
        'Microsoft.WinUI.dll', 'coreclr.dll', 'hostfxr.dll', 'appicon.ico')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $file) -PathType Leaf)) {
            throw "В публикации отсутствует файл $file."
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $publish 'mstscX.pri') -PathType Leaf) -and
        -not (Test-Path -LiteralPath (Join-Path $publish 'resources.pri') -PathType Leaf)) {
        throw 'В публикации отсутствует индекс ресурсов приложения.'
    }

    & $InnoCompiler "/DDistribDir=$publish" "/O$compiled" (Join-Path $setupRoot 'mstscX.iss')
    if ($LASTEXITCODE -ne 0) { throw "Ошибка компиляции установщика ($LASTEXITCODE)." }
    $installers = @(Get-ChildItem -LiteralPath $compiled -Filter 'mstscX-Setup-*.exe' -File)
    if ($installers.Count -ne 1) { throw 'Компилятор не создал один установочный EXE.' }
    $installer = $installers[0]

    Assert-LocalDirectory $distribution
    Assert-LocalDirectory $output
    $null = [IO.Directory]::CreateDirectory($output)
    if (Test-Path -LiteralPath $distribution) {
        Move-Item -LiteralPath $distribution -Destination $previous
    }
    try {
        Move-Item -LiteralPath $publish -Destination $distribution
        $installerPath = Join-Path $output $installer.Name
        if ([IO.File]::Exists($installerPath)) {
            [IO.File]::Replace($installer.FullName, $installerPath, [NullString]::Value)
        }
        else {
            [IO.File]::Move($installer.FullName, $installerPath)
        }
    }
    catch {
        if (-not (Test-Path -LiteralPath $publish)) {
            Move-Item -LiteralPath $distribution -Destination $publish
        }
        if (Test-Path -LiteralPath $previous) {
            Move-Item -LiteralPath $previous -Destination $distribution
        }
        throw
    }
    # Remove previous releases only after the new installer is safely in Setup.
    # Include the old product name, but leave logs and unrelated files alone.
    Assert-LocalDirectory $output
    $oldInstallers = @(Get-ChildItem -LiteralPath $output -File | Where-Object {
        $_.Name -match '^(?:mstscX|MSTSC_MAUI)-Setup-\d+\.\d+\.\d+\.\d+\.exe$' -and
        $_.FullName -ne $installerPath
    })
    foreach ($oldInstaller in $oldInstallers) {
        Remove-Item -LiteralPath $oldInstaller.FullName -Force
    }
    Write-Host "Установщик готов: $installerPath"
    if (-not $SkipPublish) {
        try {
            & (Join-Path $setupRoot 'Publish-mstscX.ps1') -InstallerPath $installerPath
        }
        catch {
            throw "Установщик собран и сохранён в $installerPath, но публикация GitHub не удалась: $($_.Exception.Message)"
        }
    }
}
finally {
    if (Test-Path -LiteralPath $previous) {
        # Keep the old distribution if restoring it after a failed move also failed.
        if (-not (Test-Path -LiteralPath $distribution)) {
            Write-Warning "Предыдущий дистрибутив сохранён: $previous"
            $staging = $null
        }
    }
    if ($staging -and (Test-Path -LiteralPath $staging)) {
        Assert-LocalDirectory $staging
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}

param([string]$GameRoot)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding

function Resolve-GameRoot([string]$Candidate) {
    if (!$Candidate) { return $null }
    foreach ($path in @($Candidate, (Join-Path $Candidate 'Build'))) {
        if ((Test-Path -LiteralPath (Join-Path $path 'Aistalt.exe')) -and
            (Test-Path -LiteralPath (Join-Path $path 'Aistalt_Data\Managed\InfantGod.dll'))) {
            return (Resolve-Path -LiteralPath $path).Path
        }
    }
    return $null
}

$root = Resolve-GameRoot $GameRoot
if (!$root) {
    $steamPath = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if ($steamPath) {
        $libraries = @($steamPath)
        $vdf = Join-Path $steamPath 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($match in [regex]::Matches([IO.File]::ReadAllText($vdf), '"path"\s+"([^"]+)"')) {
                $libraries += $match.Groups[1].Value.Replace('\\','\')
            }
        }
        foreach ($library in ($libraries | Select-Object -Unique)) {
            $manifest = Join-Path $library 'steamapps\appmanifest_5141740.acf'
            if (Test-Path -LiteralPath $manifest) {
                $match = [regex]::Match([IO.File]::ReadAllText($manifest), '"installdir"\s+"([^"]+)"')
                if ($match.Success) { $root = Resolve-GameRoot (Join-Path $library ('steamapps\common\' + $match.Groups[1].Value)) }
                if ($root) { break }
            }
        }
    }
}
if (!$root) {
    $chosen = Read-Host '请输入包含 Aistalt.exe 的游戏 Build 文件夹完整路径'
    $root = Resolve-GameRoot $chosen.Trim('"')
}
if (!$root) { throw '没有找到《幼神 Demo》的游戏目录。未安装。' }

$payload = Join-Path $PSScriptRoot 'Payload'
$loader = Join-Path $root 'BepInEx\core\BepInEx.dll'
$files = @()
if (Test-Path -LiteralPath $loader) {
    $version = [Reflection.AssemblyName]::GetAssemblyName($loader).Version
    if ($version.Major -ne 5 -or !(Test-Path -LiteralPath (Join-Path $root 'BepInEx\core\0Harmony.dll'))) {
        throw '检测到不兼容的现有前置。本安装器不会覆盖它，请使用 BepInEx 5.x Mono x64。'
    }
    Write-Host '沿用现有 BepInEx，保留其他插件与配置。'
} else {
    if (Test-Path -LiteralPath (Join-Path $root 'winhttp.dll')) {
        throw '游戏目录已有其他 winhttp.dll，本安装器不会覆盖。请先确认现有加载器。'
    }
    $runtimeRoot = Join-Path $PSScriptRoot 'Runtime'
    foreach ($item in Get-ChildItem -LiteralPath $runtimeRoot -File -Recurse) {
        $files += [pscustomobject]@{ Source=$item.FullName; Relative=$item.FullName.Substring($runtimeRoot.Length+1) }
    }
}
foreach ($item in Get-ChildItem -LiteralPath $payload -File -Recurse) {
    $files += [pscustomobject]@{ Source=$item.FullName; Relative=$item.FullName.Substring($payload.Length+1) }
}

$backupRoot = Join-Path $root ('QuickSaveVisualFixBackup\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$recordPath = Join-Path $root 'BepInEx\config\QuickSaveVisualFix.install.json'
$previousRecords = @{}
if (Test-Path -LiteralPath $recordPath) {
    $oldInstall = [IO.File]::ReadAllText($recordPath) | ConvertFrom-Json
    foreach ($old in $oldInstall.files) { $previousRecords[$old.relative] = $old }
}
$record = @()
foreach ($file in $files) {
    $destination = [IO.Path]::GetFullPath((Join-Path $root $file.Relative))
    if (!$destination.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '安装路径超出游戏目录。' }
    $previous = $null
    if (Test-Path -LiteralPath $destination) {
        $previous = Join-Path $backupRoot $file.Relative
        New-Item -ItemType Directory -Force -Path (Split-Path $previous) | Out-Null
        Copy-Item -LiteralPath $destination -Destination $previous -Force
        if ($previousRecords.ContainsKey($file.Relative) -and
            (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $previousRecords[$file.Relative].sha256) {
            $previous = $previousRecords[$file.Relative].previous
        }
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    # Atomic replacement leaves a running Mono process mapped to the old file image.
    # Never overwrite the bytes of a DLL which the current game may still be using.
    $temporary = $destination + '.install-' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        Copy-Item -LiteralPath $file.Source -Destination $temporary
        if (Test-Path -LiteralPath $destination) {
            $mappedImageBackup = Join-Path $backupRoot ($file.Relative + '.old-image')
            New-Item -ItemType Directory -Force -Path (Split-Path $mappedImageBackup) | Out-Null
            [IO.File]::Replace($temporary,$destination,$mappedImageBackup)
        }
        else { [IO.File]::Move($temporary,$destination) }
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
    $record += [pscustomobject]@{relative=$file.Relative;sha256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash;previous=$previous}
}
New-Item -ItemType Directory -Force -Path (Split-Path $recordPath) | Out-Null
foreach ($item in $record) { $previousRecords[$item.relative] = $item }
@{version='1.0.1';files=@($previousRecords.Values)} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $recordPath -Encoding UTF8
Write-Host "安装完成：$root"
Write-Host '请重新启动游戏，在 Mod 列表勾选「快速存读档画面修复」，并打开游戏的加载 Mod 总开关。'
if ($oldInstall -and $oldInstall.version -eq '1.0.0') {
    Write-Host '1.0.0 已有快速档可继续使用；这次判定框补修无须重新存档。'
} else {
    Write-Host '初次启用请重新 F5 存档，以记录完整画面状态。'
}

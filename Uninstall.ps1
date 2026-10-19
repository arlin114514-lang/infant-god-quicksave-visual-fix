param([Parameter(Mandatory=$true)][string]$GameRoot)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $GameRoot).Path.TrimEnd('\')
if (!(Test-Path -LiteralPath (Join-Path $root 'Aistalt.exe'))) { throw '必须选择包含 Aistalt.exe 的 Build 目录。' }
$manifest = Join-Path $root 'BepInEx\config\QuickSaveVisualFix.install.json'
if (!(Test-Path -LiteralPath $manifest)) { throw '没有本修复包的安装记录。可在游戏 Mod 列表里停用它。' }
$records = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json
# Leave shared loader files in place so other mods continue to work.
foreach ($record in $records.files) {
    if ($record.relative -ne 'BepInEx\plugins\InfantGodQuickSaveBridge.dll' -and
        !$record.relative.StartsWith('Aistalt_Data\StreamingAssets\Mods\QuickSaveVisualFix\', [StringComparison]::OrdinalIgnoreCase)) { continue }
    $file = [IO.Path]::GetFullPath((Join-Path $root $record.relative))
    if (!$file.StartsWith($root+'\', [StringComparison]::OrdinalIgnoreCase)) { throw '卸载路径超出游戏目录。' }
    if (!(Test-Path -LiteralPath $file)) { continue }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $record.sha256) {
        Write-Host "文件已被更新，保留：$file"; continue
    }
    if ($record.previous -and (Test-Path -LiteralPath $record.previous)) {
        Copy-Item -LiteralPath $record.previous -Destination $file -Force
    } else { Remove-Item -LiteralPath $file }
}
$modRoot = [IO.Path]::GetFullPath((Join-Path $root 'Aistalt_Data\StreamingAssets\Mods\QuickSaveVisualFix'))
if ($modRoot.StartsWith($root+'\', [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $modRoot)) {
    $entries = @(Get-Item -LiteralPath $modRoot) + @(Get-ChildItem -LiteralPath $modRoot -Force -Recurse)
    $links = @($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
    $remaining = @($entries | Where-Object { !$_.PSIsContainer })
    if ($links.Count -eq 0 -and $remaining.Count -eq 0) { Remove-Item -LiteralPath $modRoot -Recurse -Force }
}
Write-Host '已移除本修复包的文件。存档、其他 Mod、共享 BepInEx 前置及备份均保留。请重新启动游戏。'

param([Parameter(Mandatory=$true)][string]$GameRoot)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$managed = Join-Path $GameRoot 'Aistalt_Data\Managed'
$core = Join-Path $GameRoot 'BepInEx\core'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$out = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$references = @('mscorlib.dll','System.dll','System.Core.dll') | ForEach-Object { Join-Path $framework $_ }
$references += @('netstandard.dll','InfantGod.dll','FairyGUI.dll','FairyGUI.Generated.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.JSONSerializeModule.dll','UnityEngine.IMGUIModule.dll','UnityEngine.InputLegacyModule.dll','spine-csharp.dll','spine-unity.dll','Newtonsoft.Json.dll','YarnSpinner.dll','YarnSpinner.Unity.dll') | ForEach-Object { Join-Path $managed $_ }
$references += @('BepInEx.dll','0Harmony.dll') | ForEach-Object { Join-Path $core $_ }
foreach ($reference in $references) { if (!(Test-Path -LiteralPath $reference)) { throw "Missing reference: $reference" } }
$commonArgs = @('/nologo','/noconfig','/nostdlib+','/target:library','/langversion:5','/optimize+','/debug-','/utf8output')
$commonArgs += $references | ForEach-Object { '/reference:' + $_ }
& $compiler @commonArgs ('/out:' + (Join-Path $out 'InfantGodQuickSaveFix.dll')) (Join-Path $projectRoot 'Entry.cs') (Join-Path $projectRoot 'VisualState.cs') (Join-Path $projectRoot 'PassiveChecks.cs')
if ($LASTEXITCODE) { throw 'Repair module build failed' }
& $compiler @commonArgs ('/out:' + (Join-Path $out 'InfantGodQuickSaveBridge.dll')) (Join-Path $projectRoot 'Bootstrap.cs')
if ($LASTEXITCODE) { throw 'Bridge build failed' }
Get-ChildItem -LiteralPath $out -Filter '*.dll' | Select-Object Name,Length

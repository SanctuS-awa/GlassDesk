[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PinSourceDirectory,
      [Parameter(Mandatory=$true)][string]$RuntimeDirectory,
      [Parameter(Mandatory=$true)][string]$PowerShellDirectory,
      [Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputDirectory){throw 'Fresh build directory required'}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$taskPins=[ordered]@{
 BETA_OUTER_SHA=(Join-Path $PinSourceDirectory 'r128-human-review-root\Invoke-R109GraphBaselineOuter.ps1')
 BETA_OWNER_SHA=(Join-Path $PinSourceDirectory 'r128-human-review-root\Invoke-R109GraphBaselineOwner.ps1')
 BETA_PRODUCER_ENTRY_SHA=(Join-Path $PinSourceDirectory 'r128-human-review-root\Start-R109CimProducer.ps1')
 BETA_PWSH_SHA=(Join-Path $PowerShellDirectory 'pwsh.exe')
 BETA_RUNTIME_EXE_SHA=(Join-Path $RuntimeDirectory 'GlassDesk.exe')
 BETA_RUNTIME_DLL_SHA=(Join-Path $RuntimeDirectory 'GlassDesk.dll')
 BETA_NATIVE_SHA=(Join-Path $RuntimeDirectory 'GlassDesk.Native.dll')
}
$taskHeader=[Collections.Generic.List[string]]::new()
$taskHeader.Add('#pragma once')
foreach($pin in $taskPins.GetEnumerator()){
 $hash=(Get-FileHash -LiteralPath $pin.Value -Algorithm SHA256).Hash
 if($hash -cnotmatch '^[A-F0-9]{64}$'){throw 'Invalid pin'}
 $taskHeader.Add(('#define {0} L"{1}"' -f $pin.Key,$hash))
}
[IO.File]::WriteAllLines((Join-Path $OutputDirectory 'PackagePins.h'),$taskHeader,[Text.UTF8Encoding]::new($false))
$taskVsWhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$taskVs=& $taskVsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$taskVs){throw 'MSVC compiler not found'}
& (Join-Path $taskVs 'Common7\Tools\Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\..\src\GlassDesk.App\Assets\GlassDesk.ico') -Destination (Join-Path $OutputDirectory 'GlassDesk.ico')
Set-Content -LiteralPath (Join-Path $OutputDirectory 'launcher.rc') -Value '1 ICON "GlassDesk.ico"' -Encoding ascii
Push-Location $OutputDirectory
try {
 & rc.exe /nologo /fo launcher.res launcher.rc
 if($LASTEXITCODE -ne 0){throw 'Icon compilation failed'}
 & cl.exe /nologo /std:c++20 /EHsc /O2 /W4 /WX /MT /guard:cf /utf-8 /permissive- /DUNICODE /D_UNICODE /I $OutputDirectory (Join-Path $PSScriptRoot 'main.cpp') launcher.res /Fe:GlassDesk.exe /link /SUBSYSTEM:WINDOWS /DYNAMICBASE /NXCOMPAT /guard:cf
 if($LASTEXITCODE -ne 0){throw 'Launcher compilation failed'}
} finally {Pop-Location}
Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'GlassDesk.exe') -Algorithm SHA256

[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
# CPU-only WinRT effect-description contract check. This does not create a
# Compositor/graphics device, load GlassDeskNative.dll, or open a GUI.
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskNative=Join-Path $taskRoot 'native/GlassDesk.Native'
$taskVsWhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$taskVs=& $taskVsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$taskVs){throw 'MSVC x64 build tools not found'}
& (Join-Path $taskVs 'Common7/Tools/Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
$taskOut=Join-Path $taskRoot ('artifacts/qa/system-ink-cpu-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskOut)|Out-Null
Push-Location -LiteralPath $taskOut
try {
    $taskName='SystemInkGraphCpuCheck'
    & cl.exe /nologo /std:c++20 /EHsc /W4 /WX /O2 /MT /utf-8 "/I$taskNative" (Join-Path $PSScriptRoot ($taskName+'.cpp')) "/Fe:$taskName.exe" /link windowsapp.lib dxguid.lib
    if($LASTEXITCODE -ne 0){throw "$taskName compilation failed"}
    & (Join-Path $taskOut ($taskName+'.exe'))
    if($LASTEXITCODE -ne 0){throw "$taskName checks failed"}
    Write-Output "CPU-only system ink graph check passed: $taskOut"
} finally {Pop-Location}

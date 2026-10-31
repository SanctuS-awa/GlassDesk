[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
# Pure CPU first-error/async-status tests. Never load the native DLL or create graphics.
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskNative=Join-Path $taskRoot 'native/GlassDesk.Native'
$taskVsWhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$taskVs=& $taskVsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$taskVs){throw 'MSVC x64 build tools not found'}
& (Join-Path $taskVs 'Common7/Tools/Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
$taskOut=Join-Path $taskRoot ('artifacts/qa/cleanup-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskOut)|Out-Null
Push-Location -LiteralPath $taskOut
try {
    & cl.exe /nologo /std:c++20 /EHsc /W4 /WX /O2 /MT /utf-8 "/I$taskNative" (Join-Path $PSScriptRoot 'CleanupResultCpuCheck.cpp') '/Fe:CleanupResultCpuCheck.exe'
    if($LASTEXITCODE -ne 0){throw 'Cleanup CPU checks compilation failed'}
    & (Join-Path $taskOut 'CleanupResultCpuCheck.exe')
    if($LASTEXITCODE -ne 0){throw 'Cleanup CPU checks failed'}
    & cl.exe /nologo /std:c++20 /EHsc /W4 /WX /O2 /MT /utf-8 "/I$taskNative" (Join-Path $PSScriptRoot 'AccessibilityShutdownCpuCheck.cpp') '/Fe:AccessibilityShutdownCpuCheck.exe' /link oleaut32.lib user32.lib uiautomationcore.lib
    if($LASTEXITCODE -ne 0){throw 'Accessibility shutdown CPU checks compilation failed'}
    & (Join-Path $taskOut 'AccessibilityShutdownCpuCheck.exe')
    if($LASTEXITCODE -ne 0){throw 'Accessibility shutdown CPU checks failed'}
    Write-Output "CPU-only checks passed: $taskOut"
} finally {Pop-Location}

[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
# Headless boundary check: a native ICompositionSurface marker stub exercises
# CsWinRT inspectable marshalling and native COM ref ownership. No Compositor,
# graphics device, GlassDesk candidate DLL, window, or desktop input is used.
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskNative='C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\cppwinrt'
if(!(Test-Path -LiteralPath $taskNative)){throw 'Windows SDK C++/WinRT headers not found'}
$taskVsWhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$taskVs=& $taskVsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$taskVs){throw 'MSVC x64 build tools not found'}
& (Join-Path $taskVs 'Common7/Tools/Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
$taskOut=Join-Path $taskRoot ('artifacts/qa/system-ink-interop-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskOut)|Out-Null
Push-Location -LiteralPath $taskOut
try {
    $taskProbe=Join-Path $PSScriptRoot 'SystemGlassInkInteropProbe.cpp'
    & cl.exe /nologo /std:c++20 /EHsc /W4 /WX /O2 /MT /LD /utf-8 "/I$taskNative" $taskProbe '/Fe:SystemGlassInkInteropProbe.dll' /link windowsapp.lib
    if($LASTEXITCODE -ne 0){throw 'SystemGlassInkInteropProbe compilation failed'}
    $taskProject=Join-Path $PSScriptRoot 'SystemGlassInkInteropManaged.csproj'
    $taskDll=Join-Path $taskOut 'SystemGlassInkInteropProbe.dll'
    & dotnet run --project $taskProject --configuration Release -- $taskDll
    if($LASTEXITCODE -ne 0){throw 'CsWinRT/native inspectable checks failed'}
    Write-Output "Headless CsWinRT/native inspectable check passed: $taskOut"
} finally {Pop-Location}

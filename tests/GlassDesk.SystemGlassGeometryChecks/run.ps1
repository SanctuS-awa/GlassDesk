[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
# Pure C++ metadata/mesh checks. No WinRT, graphics device, native DLL or GUI.
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskNative=Join-Path $taskRoot 'native/GlassDesk.Native'
$taskVsWhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$taskVs=& $taskVsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$taskVs){throw 'MSVC x64 build tools not found'}
& (Join-Path $taskVs 'Common7/Tools/Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
$taskOut=Join-Path $taskRoot ('artifacts/qa/systemglass-geometry-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskOut)|Out-Null
Push-Location -LiteralPath $taskOut
try {
    foreach($taskName in @('SystemGlassGeometryCpuCheck','DesktopSourceViewportCpuCheck','CanvasGrowthCpuCheck','SystemGlassLightProfileCpuCheck','ArtworkFailurePolicyCpuCheck','ShadowRasterProfileCpuCheck')) {
        & cl.exe /nologo /std:c++20 /EHsc /W4 /WX /O2 /MT /utf-8 "/I$taskNative" (Join-Path $PSScriptRoot ($taskName+'.cpp')) "/Fe:$taskName.exe"
        if($LASTEXITCODE -ne 0){throw "$taskName compilation failed"}
        & (Join-Path $taskOut ($taskName+'.exe'))
        if($LASTEXITCODE -ne 0){throw "$taskName checks failed"}
    }
    Write-Output "CPU-only checks passed: $taskOut"
} finally {Pop-Location}

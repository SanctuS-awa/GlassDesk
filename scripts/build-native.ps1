[CmdletBinding()]
param([string]$Configuration='Release',[switch]$BuildRegression,[string]$OutputDirectory='', [switch]$EnableSystemCompositorCandidate)
$ErrorActionPreference='Stop'
if ($Configuration -notin @('Release','Debug')) { throw 'Configuration must be Release or Debug.' }
$taskRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\','/')
& (Join-Path $PSScriptRoot 'verify-capture-free.ps1') -ProjectRoot $taskRoot
function Get-RootRelativePath([string]$Path){
    $taskFullPath=[IO.Path]::GetFullPath($Path)
    $taskRootPrefix=$taskRoot+[IO.Path]::DirectorySeparatorChar
    if(!$taskFullPath.StartsWith($taskRootPrefix,[StringComparison]::OrdinalIgnoreCase)){throw "Path is outside GlassDesk: $Path"}
    return $taskFullPath.Substring($taskRootPrefix.Length).Replace('\','/')
}
function Get-Sha256Hex([string]$Path){
    $taskAlgorithm=[Security.Cryptography.SHA256]::Create()
    $taskStream=$null
    try{
        $taskStream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        $taskDigest=$taskAlgorithm.ComputeHash($taskStream)
        return [BitConverter]::ToString($taskDigest).Replace('-','').ToLowerInvariant()
    }finally{
        if($taskStream){$taskStream.Dispose()}
        $taskAlgorithm.Dispose()
    }
}
# Keep the incident guard on every production desktop code path, including the
# managed/native bridge where new host setup code may be introduced. Match only
# the quarantined implementation names and header: ordinary UI transforms remain
# allowed, and this check does not initialize a graphics device.
$taskQuarantinePattern='\bUpdateDesktopLens\b|\bLensTransformEffect\b|\bCLSID_D2D12DAffineTransform\b|#\s*include\s*["<]LiquidOptics\.h[">]'
$taskDesktopSourceRoots=@('native/GlassDesk.Native','src/GlassDesk.NativeBridge','src/GlassDesk.App','src/GlassDesk.Core')
$taskDesktopExtensions=@('.cpp','.h','.hpp','.hlsl','.cs')
foreach($taskSourceRoot in $taskDesktopSourceRoots){
    $taskSourcePath=Join-Path $taskRoot $taskSourceRoot
    if(!(Test-Path -LiteralPath $taskSourcePath -PathType Container)){continue}
    foreach($taskSourceFile in (Get-ChildItem -LiteralPath $taskSourcePath -File -Recurse -Force | Where-Object {$_.Extension -in $taskDesktopExtensions -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]'})){
        if((Get-Content -LiteralPath $taskSourceFile.FullName -Raw) -match $taskQuarantinePattern){
            throw "Quarantined desktop backdrop transform source detected: $($taskSourceFile.FullName). Do not bypass this guard; read AGENTS.md."
        }
    }
}

# Every invocation gets a fresh candidate directory. Never write into the old
# artifacts/native or native-ui-preview locations, which may hold quarantined
# binaries. A caller-supplied path is limited to one new child of the candidate root.
$taskCandidateRoots=@(
    [IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts/native-candidates')).TrimEnd('\','/'),
    [IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts/native-desktop-candidate')).TrimEnd('\','/')
)
if(!$OutputDirectory){$OutputDirectory=Join-Path $taskCandidateRoots[0] ([Guid]::NewGuid().ToString('N'))}
$taskRequestedOutput=if([IO.Path]::IsPathRooted($OutputDirectory)){$OutputDirectory}else{Join-Path $taskRoot $OutputDirectory}
$taskOutput=[IO.Path]::GetFullPath($taskRequestedOutput).TrimEnd('\','/')
$taskOutputParent=[IO.Path]::GetDirectoryName($taskOutput)
$taskOutputName=[IO.Path]::GetFileName($taskOutput)
if(!($taskCandidateRoots | Where-Object {[StringComparer]::OrdinalIgnoreCase.Equals($taskOutputParent,$_)} ) -or $taskOutputName -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,62}[A-Za-z0-9])?$'){
    throw 'OutputDirectory must be a new, single-level candidate under artifacts/native-candidates or artifacts/native-desktop-candidate.'
}

function Get-ExistingPathAttributes([string]$Path){
    $taskAttributes=$null
    try{$taskAttributes=[IO.File]::GetAttributes($Path)}
    catch [IO.FileNotFoundException]{}
    catch [IO.DirectoryNotFoundException]{}
    $taskEntry=Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if($taskEntry -and ($taskEntry.Attributes -band [IO.FileAttributes]::ReparsePoint)){return $taskEntry.Attributes}
    if($null -ne $taskAttributes){return $taskAttributes}
    if($taskEntry){return $taskEntry.Attributes}
    return $null
}

function Get-NativeBuildInputRecords {
    $taskInputFiles=[Collections.Generic.List[IO.FileInfo]]::new()
    $taskSourceRoot=Join-Path $taskRoot 'native\GlassDesk.Native'
    $taskSourceExtensions=@('.c','.cc','.cpp','.cxx','.h','.hh','.hpp','.hxx','.inl','.hlsl','.rc','.def')
    foreach($taskSourceFile in (Get-ChildItem -LiteralPath $taskSourceRoot -File -Recurse -Force | Where-Object {$_.Extension -in $taskSourceExtensions -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]'})){
        $taskInputFiles.Add($taskSourceFile)
    }
    foreach($taskBuildScript in @('scripts/build-native.ps1','scripts/verify-native.ps1','scripts/verify-capture-free.ps1')){
        $taskInputFiles.Add((Get-Item -LiteralPath (Join-Path $taskRoot $taskBuildScript) -Force))
    }

    $taskRecords=[Collections.Generic.List[object]]::new()
    foreach($taskInputFile in ($taskInputFiles | Sort-Object FullName)){
        if($taskInputFile.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Native provenance input is a reparse point: $($taskInputFile.FullName)"}
        $taskRecords.Add([ordered]@{
            Path=(Get-RootRelativePath $taskInputFile.FullName)
            Sha256=(Get-Sha256Hex $taskInputFile.FullName)
        })
    }
    return $taskRecords.ToArray()
}

function Assert-SameInputRecords([object[]]$Before,[object[]]$After){
    if($Before.Count -ne $After.Count){throw 'Native source inputs changed during compilation; candidate provenance was not written.'}
    $taskBeforeByPath=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($taskRecord in $Before){$taskBeforeByPath.Add([string]$taskRecord.Path,[string]$taskRecord.Sha256)}
    foreach($taskRecord in $After){
        $taskBeforeHash=''
        if(!$taskBeforeByPath.TryGetValue([string]$taskRecord.Path,[ref]$taskBeforeHash) -or $taskBeforeHash -cne [string]$taskRecord.Sha256){
            throw "Native source input changed during compilation; candidate provenance was not written: $($taskRecord.Path)"
        }
    }
}

function Get-NativeOutputRecords {
    $taskOutputNames=[Collections.Generic.List[string]]::new()
    $taskOutputNames.Add('GlassDesk.Native.dll')
    if($BuildRegression){$taskOutputNames.Add('GlassDesk.NativeRegression.exe')}
    $taskRecords=[Collections.Generic.List[object]]::new()
    foreach($taskOutputName in $taskOutputNames){
        $taskOutputFile=Join-Path $taskOutput $taskOutputName
        if(!(Test-Path -LiteralPath $taskOutputFile -PathType Leaf) -or (Get-Item -LiteralPath $taskOutputFile -Force).Length -le 0){
            throw "Expected native candidate output is missing or empty: $taskOutputName"
        }
        if([IO.File]::GetAttributes($taskOutputFile) -band [IO.FileAttributes]::ReparsePoint){throw "Native candidate output is a reparse point: $taskOutputName"}
        $taskRecords.Add([ordered]@{
            Path=$taskOutputName
            Sha256=(Get-Sha256Hex $taskOutputFile)
        })
    }
    return $taskRecords.ToArray()
}

$taskCheckPath=$taskOutput
while($taskCheckPath -and ![StringComparer]::OrdinalIgnoreCase.Equals($taskCheckPath,$taskRoot)){
    $taskPathAttributes=Get-ExistingPathAttributes $taskCheckPath
    if($null -ne $taskPathAttributes -and ($taskPathAttributes -band [IO.FileAttributes]::ReparsePoint)){
        throw "Native candidate output passes through a reparse point: $taskCheckPath"
    }
    $taskCheckPath=[IO.Path]::GetDirectoryName($taskCheckPath)
}
$taskOutputAttributes=Get-ExistingPathAttributes $taskOutput
if($null -ne $taskOutputAttributes){throw "Native candidate output already exists; refusing to overwrite it: $taskOutput"}
$taskVsWhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$taskVs=& $taskVsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(-not $taskVs){throw 'MSVC x64 is required.'}
& (Join-Path $taskVs 'Common7\Tools\Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
if($env:WindowsSDKVersion.TrimEnd('\') -ne '10.0.26100.0'){throw 'Windows SDK 10.0.26100.0 must be selected in the developer environment.'}
if($null -eq (Get-ExistingPathAttributes $taskOutputParent)){New-Item -ItemType Directory -Path $taskOutputParent -Force | Out-Null}
$taskCheckPath=$taskOutputParent
while($taskCheckPath -and ![StringComparer]::OrdinalIgnoreCase.Equals($taskCheckPath,$taskRoot)){
    $taskPathAttributes=Get-ExistingPathAttributes $taskCheckPath
    if($null -ne $taskPathAttributes -and ($taskPathAttributes -band [IO.FileAttributes]::ReparsePoint)){throw "Native candidate output passes through a reparse point: $taskCheckPath"}
    $taskCheckPath=[IO.Path]::GetDirectoryName($taskCheckPath)
}
if($null -ne (Get-ExistingPathAttributes $taskOutput)){throw "Native candidate output appeared before creation; refusing to overwrite it: $taskOutput"}
New-Item -ItemType Directory -Path $taskOutput -ErrorAction Stop | Out-Null
$taskBuildInputs=Get-NativeBuildInputRecords
$taskSystemDefines=@(if($EnableSystemCompositorCandidate){'/DGD_SYSTEM_COMPOSITOR_CANDIDATE'})
$taskOptimize=if($Configuration -eq 'Release'){'/O2'}else{'/Od'}
Push-Location -LiteralPath $taskOutput
try {
    $taskShader=Join-Path $taskRoot 'native\GlassDesk.Native\PreviewGlass.hlsl'
    & fxc.exe /nologo /O3 /T vs_4_0 /E VS /Fh LocalGlassVS.h /Vn LocalGlassVertexShader $taskShader
    if($LASTEXITCODE -ne 0){throw 'Local glass vertex shader build failed.'}
    & fxc.exe /nologo /O3 /T ps_4_0 /E PS /Fh LocalGlassPS.h /Vn LocalGlassPixelShader $taskShader
    if($LASTEXITCODE -ne 0){throw 'Local glass pixel shader build failed.'}
    & fxc.exe /nologo /O3 /T ps_4_0 /E PSBlur /Fh LocalGlassBlurPS.h /Vn LocalGlassBlurPixelShader $taskShader
    if($LASTEXITCODE -ne 0){throw 'Local glass Gaussian shader build failed.'}
    $taskOwnedShader=Join-Path $taskRoot 'native\GlassDesk.Native\OwnedImageGlass.hlsl'
    & fxc.exe /nologo /O3 /T vs_4_0 /E VS /Fh OwnedImageGlassVS.h /Vn OwnedImageGlassVertexShader $taskOwnedShader
    if($LASTEXITCODE -ne 0){throw 'Owned-image glass vertex shader build failed.'}
    & fxc.exe /nologo /O3 /T ps_4_0 /E PS /Fh OwnedImageGlassPS.h /Vn OwnedImageGlassPixelShader $taskOwnedShader
    if($LASTEXITCODE -ne 0){throw 'Owned-image glass pixel shader build failed.'}
    & fxc.exe /nologo /O3 /T ps_4_0 /E PSBlur /Fh OwnedImageGlassBlurPS.h /Vn OwnedImageGlassBlurPixelShader $taskOwnedShader
    if($LASTEXITCODE -ne 0){throw 'Owned-image glass blur shader build failed.'}
    & cl.exe /nologo /std:c++20 /EHsc /W4 /WX $taskOptimize @taskSystemDefines /MT /LD /I $taskOutput /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /permissive- /utf-8 (Join-Path $taskRoot 'native\GlassDesk.Native\GlassDeskNative.cpp') (Join-Path $taskRoot 'native\GlassDesk.Native\DesktopSharedSource.cpp') (Join-Path $taskRoot 'native\GlassDesk.Native\SystemCompositorBootstrap.cpp') (Join-Path $taskRoot 'native\GlassDesk.Native\SystemGlassScene.cpp') (Join-Path $taskRoot 'native\GlassDesk.Native\SystemDesktopGlass.cpp') (Join-Path $taskRoot 'native\GlassDesk.Native\ProcessAudio.cpp') (Join-Path $taskRoot 'native\GlassDesk.Native\PreviewGlass.cpp') (Join-Path $taskRoot 'native\GlassDesk.Native\OwnedImageGlass.cpp') (Join-Path $taskRoot 'native\GlassDesk.Native\LiquidGlass.cpp') (Join-Path $taskRoot 'native\GlassDesk.Native\TypographyEngine.cpp') /Fe:GlassDesk.Native.dll /link /DYNAMICBASE /NXCOMPAT /guard:cf user32.lib gdi32.lib dwmapi.lib windowsapp.lib CoreMessaging.lib dxguid.lib d2d1.lib d3d11.lib dwrite.lib windowscodecs.lib ole32.lib oleaut32.lib uiautomationcore.lib mmdevapi.lib d3dcompiler.lib
    if($LASTEXITCODE -ne 0){throw 'GlassDesk.Native build failed.'}
    if($BuildRegression) {
        & cl.exe /nologo /std:c++20 /EHsc /W4 /WX $taskOptimize /MT /I $taskOutput /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /permissive- /utf-8 (Join-Path $taskRoot 'native\GlassDesk.Native\NativeRegression.cpp') (Join-Path $taskRoot 'native\GlassDesk.Native\OwnedImageGlass.cpp') (Join-Path $taskRoot 'native\GlassDesk.Native\LiquidGlass.cpp') (Join-Path $taskRoot 'native\GlassDesk.Native\TypographyEngine.cpp') /Fe:GlassDesk.NativeRegression.exe /link /SUBSYSTEM:CONSOLE /DYNAMICBASE /NXCOMPAT /guard:cf user32.lib gdi32.lib dwmapi.lib windowsapp.lib CoreMessaging.lib dxguid.lib d2d1.lib d3d11.lib dwrite.lib windowscodecs.lib ole32.lib oleaut32.lib uiautomationcore.lib mmdevapi.lib d3dcompiler.lib
        if($LASTEXITCODE -ne 0){throw 'GlassDesk native regression fixture build failed.'}
    }
} finally {Pop-Location}

$taskBuildInputsAfter=Get-NativeBuildInputRecords
Assert-SameInputRecords $taskBuildInputs $taskBuildInputsAfter
$taskManifestPath=Join-Path $taskOutput 'GlassDesk.Native.candidate.json'
$taskManifestTemporaryPath=$taskManifestPath+'.tmp'
if($null -ne (Get-ExistingPathAttributes $taskManifestPath) -or $null -ne (Get-ExistingPathAttributes $taskManifestTemporaryPath)){
    throw 'Native candidate provenance file already exists; refusing to overwrite it.'
}
$taskManifest=[ordered]@{
    SchemaVersion=1
    CandidateDirectory=(Get-RootRelativePath $taskOutput)
    Configuration=$Configuration
    RegressionIncluded=[bool]$BuildRegression
    SystemCompositorCandidateCompiled=[bool]$EnableSystemCompositorCandidate
    Inputs=$taskBuildInputsAfter
    Outputs=(Get-NativeOutputRecords)
}
$taskManifestJson=ConvertTo-Json -InputObject $taskManifest -Depth 5
$taskManifestBytes=[Text.UTF8Encoding]::new($false).GetBytes($taskManifestJson)
$taskManifestStream=[IO.File]::Open($taskManifestTemporaryPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try{$taskManifestStream.Write($taskManifestBytes,0,$taskManifestBytes.Length);$taskManifestStream.Flush($true)}finally{$taskManifestStream.Dispose()}
[IO.File]::Move($taskManifestTemporaryPath,$taskManifestPath)
Write-Output $taskOutput


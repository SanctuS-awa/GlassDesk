param([Parameter(Mandatory=$true)][string]$ProjectRoot)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath($ProjectRoot)
function Read-Source([string]$Path){Get-Content -LiteralPath (Join-Path $taskRoot $Path) -Raw}
$taskBuild=Read-Source 'scripts/build-native.ps1'
if($taskBuild -match 'CaptureWorker\.cpp|LiveGlassInput\.cpp|CaptureDiagnostic\.cpp|PresentationProbe\.cpp'){
    throw 'Screen-capture code is present in the native build source list.'
}
[xml]$taskBridge=Read-Source 'src/GlassDesk.NativeBridge/GlassDesk.NativeBridge.csproj'
foreach($taskItem in $taskBridge.Project.ItemGroup.Content){
    if([string]$taskItem.Include -match 'CaptureWorker|CaptureDiagnostic|PresentationProbe'){
        throw 'A capture executable is included in build/publish content.'
    }
}
$taskForbidden='Windows\.Graphics\.Capture|windows\.graphics\.capture|CreateForMonitor\s*\(|CreateFromVisual\s*\(|DuplicateOutput\s*\(|MagSetWindowSource\s*\(|MagSetImageScalingCallback\s*\(|CopyFromScreen\s*\(|#\s*include\s*"CaptureChannel\.h"'
$taskNativeSources=@('GlassDeskNative.cpp','LiquidGlass.cpp','OwnedImageGlass.cpp','PreviewGlass.cpp','ProcessAudio.cpp','TypographyEngine.cpp')
foreach($taskName in $taskNativeSources){
    if((Read-Source ('native/GlassDesk.Native/'+$taskName)) -match $taskForbidden){
        throw "Screen-capture API/reference in a compiled native source: $taskName"
    }
}
foreach($taskDirectory in @('src/GlassDesk.App','src/GlassDesk.NativeBridge','src/GlassDesk.Core','src/GlassDesk.Platform')){
    foreach($taskFile in Get-ChildItem -LiteralPath (Join-Path $taskRoot $taskDirectory) -Filter '*.cs' -Recurse -File | Where-Object FullName -notmatch '[\\/](bin|obj)[\\/]'){
        if((Get-Content -LiteralPath $taskFile.FullName -Raw) -match $taskForbidden){throw "Screen-capture API in managed product source: $($taskFile.FullName)"}
    }
}
foreach($taskName in @('CaptureWorker.cpp','CaptureDiagnostic.cpp','PresentationProbe.cpp','CaptureChannel.h')){
    if((Read-Source ('native/GlassDesk.Native/'+$taskName)) -notmatch '(?m)^#error Screen_capture_is_prohibited_by_the_GlassDesk_user_requirement\s*$'){
        throw "Historical capture source lacks its compile-time prohibition: $taskName"
    }
}
foreach($taskName in @('build-capture-diagnostics.ps1','check-liquid-gui.ps1')){
    $taskText=Read-Source ('scripts/'+$taskName)
    if($taskText -notmatch '(?m)^throw .*capture.*disabled|(?m)^throw .*screen.*prohibited'){
        throw "Historical capture QA script lacks a top-level refusal: $taskName"
    }
}
Write-Output 'Capture-free source/build checks passed. No graphics, screen reads or application startup performed. This is not an optical acceptance result.'

[CmdletBinding()]
param(
    [string]$CompilerPath,
    [switch]$SkipStress,
    [switch]$RunChecks,
    [string]$IndependentTestMachineName,
    [string]$CandidateId
)

$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\','/')
$taskVersion=([xml](Get-Content -LiteralPath (Join-Path $taskRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if(!$CandidateId){$CandidateId=[Guid]::NewGuid().ToString('N')}
if($CandidateId -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,54}[A-Za-z0-9])?$'){throw 'CandidateId must be 1-56 allowed characters and start and end with a letter or digit.'}

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

function Assert-IndependentTestMachine([string]$DeclaredName){
    if(!$DeclaredName){throw 'RunChecks requires -IndependentTestMachineName with the target machine name.'}
    $taskMachine=[Environment]::MachineName
    if([StringComparer]::OrdinalIgnoreCase.Equals($taskMachine,'SHENG-AWA')){throw 'Graphical and native checks are disabled on the incident host SHENG-AWA.'}
    if(![StringComparer]::OrdinalIgnoreCase.Equals($taskMachine,$DeclaredName)){throw "Declared test machine '$DeclaredName' does not match this machine '$taskMachine'. Run checks on the named independent test machine."}
}

if($RunChecks){Assert-IndependentTestMachine $IndependentTestMachineName}
elseif($IndependentTestMachineName){throw '-IndependentTestMachineName is only valid with -RunChecks.'}
if($SkipStress -and !$RunChecks){throw '-SkipStress only changes RunChecks; pass -RunChecks and -IndependentTestMachineName to execute checks.'}

$taskCandidateRoot=Join-Path $taskRoot "artifacts/release-candidates/$CandidateId"
$taskPublishPath=Join-Path $taskCandidateRoot 'publish'
$taskInstallerDirectory=Join-Path $taskCandidateRoot 'installer'
$taskNative=Join-Path $taskRoot "artifacts/native-candidates/release-$CandidateId"
foreach($taskCandidatePath in @($taskCandidateRoot,$taskPublishPath,$taskInstallerDirectory,$taskNative)){
    $taskResolved=[IO.Path]::GetFullPath($taskCandidatePath).TrimEnd('\','/')
    if(!$taskResolved.StartsWith($taskRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw "Candidate output is outside GlassDesk: $taskCandidatePath"}
    $taskCheck=$taskResolved
    while($taskCheck -and ![StringComparer]::OrdinalIgnoreCase.Equals($taskCheck,$taskRoot)){
        $taskAttributes=Get-ExistingPathAttributes $taskCheck
        if($null -ne $taskAttributes -and ($taskAttributes -band [IO.FileAttributes]::ReparsePoint)){throw "Candidate output passes through a reparse point: $taskCheck"}
        $taskCheck=[IO.Path]::GetDirectoryName($taskCheck)
    }
    if($null -ne (Get-ExistingPathAttributes $taskResolved)){throw "Candidate output already exists; refusing to overwrite it: $taskResolved"}
}

Push-Location -LiteralPath $taskRoot
try {
    $taskNativeArgs=@('-Configuration','Release','-OutputDirectory',$taskNative)
    if($RunChecks){$taskNativeArgs+='-BuildRegression'}
    & (Join-Path $PSScriptRoot 'build-native.ps1') @taskNativeArgs
    if($LASTEXITCODE -ne 0){throw 'Native compilation failed.'}

    New-Item -ItemType Directory -Path $taskCandidateRoot -ErrorAction Stop | Out-Null
    New-Item -ItemType Directory -Path $taskPublishPath -ErrorAction Stop | Out-Null
    New-Item -ItemType Directory -Path $taskInstallerDirectory -ErrorAction Stop | Out-Null
    $taskNativeProperty="-p:GlassDeskNativeDirectory=$taskNative"
    $taskSerial=@('-m:1','-p:BuildInParallel=false')

    & dotnet restore '.\src\GlassDesk.App\GlassDesk.App.csproj' -r win-x64 --locked-mode $taskNativeProperty @taskSerial
    if($LASTEXITCODE -ne 0){throw 'Locked dependency restore failed.'}
    $taskFixture='.\tests\Fixtures\LegacyPlugin\GlassDesk.LegacyPlugin.csproj'
    & dotnet restore $taskFixture -r win-x64 --locked-mode $taskNativeProperty @taskSerial
    if($LASTEXITCODE -ne 0){throw 'Locked legacy fixture restore failed.'}
    & dotnet build $taskFixture -c Release -r win-x64 --no-restore -p:AppendRuntimeIdentifierToOutputPath=false $taskNativeProperty @taskSerial
    if($LASTEXITCODE -ne 0){throw 'Legacy fixture build failed.'}
    foreach($taskProject in @('.\tests\GlassDesk.Core.Tests\GlassDesk.Core.Tests.csproj','.\tests\GlassDesk.PlatformChecks\GlassDesk.PlatformChecks.csproj','.\tests\GlassDesk.PluginChecks\GlassDesk.PluginChecks.csproj','.\tests\GlassDesk.AudioChecks\GlassDesk.AudioChecks.csproj')){
        & dotnet restore $taskProject -r win-x64 --locked-mode $taskNativeProperty @taskSerial
        if($LASTEXITCODE -ne 0){throw "Locked test restore failed: $taskProject"}
        & dotnet build $taskProject -c Release -r win-x64 --no-restore -p:AppendRuntimeIdentifierToOutputPath=false $taskNativeProperty @taskSerial
        if($LASTEXITCODE -ne 0){throw "Check build failed: $taskProject"}
        if($RunChecks){
            $taskRunArguments=@('run','--project',$taskProject,'-c','Release','-r','win-x64','--no-build','--no-restore','-p:AppendRuntimeIdentifierToOutputPath=false')
            if($SkipStress -and $taskProject -eq '.\tests\GlassDesk.Core.Tests\GlassDesk.Core.Tests.csproj'){$taskRunArguments+=@('--','--no-stress')}
            & dotnet @taskRunArguments
            if($LASTEXITCODE -ne 0){throw "Checks failed: $taskProject"}
        }
    }

    & dotnet publish '.\src\GlassDesk.App\GlassDesk.App.csproj' -c Release -r win-x64 --self-contained true --no-restore -o $taskPublishPath $taskNativeProperty -p:PublishSingleFile=false -p:PublishTrimmed=false @taskSerial
    if($LASTEXITCODE -ne 0){throw 'Self-contained publish failed.'}
    Copy-Item -LiteralPath '.\docs\user-guide.md' -Destination (Join-Path $taskPublishPath 'UserGuide.md')
    $taskPublicDocs=Join-Path $taskPublishPath 'docs'
    New-Item -ItemType Directory -Path $taskPublicDocs -Force | Out-Null
    foreach($taskDocument in @('index.html','showcase-maintenance.md','user-guide.md','privacy.md','known-issues.md')){
        Copy-Item -LiteralPath (Join-Path $taskRoot "docs/$taskDocument") -Destination $taskPublicDocs -Force
    }
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/images') -Destination $taskPublicDocs -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination $taskPublishPath -Force
    $taskRootGuide=Get-Content -LiteralPath (Join-Path $taskPublishPath 'UserGuide.md') -Raw
    $taskRootGuide.Replace('(privacy.md)','(docs/privacy.md)').Replace('(known-issues.md)','(docs/known-issues.md)') | Set-Content -LiteralPath (Join-Path $taskPublishPath 'UserGuide.md') -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $taskRoot 'THIRD-PARTY-NOTICES.md') -Destination $taskPublishPath
    Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE') -Destination $taskPublishPath
    Copy-Item -LiteralPath (Join-Path $taskRoot 'licenses') -Destination (Join-Path $taskPublishPath 'licenses') -Recurse

    if(!$CompilerPath){$CompilerPath=Join-Path $taskRoot '.tools\innosetup-7.1.0\ISCC.exe'}
    if(!(Test-Path -LiteralPath $CompilerPath -PathType Leaf)){throw 'Inno Setup 7.1.0 x64 ISCC.exe is required. Pass -CompilerPath or provision the documented portable compiler.'}
    & $CompilerPath --quiet "/DAppVersion=$taskVersion" "/DPublishDir=$taskPublishPath" "--output-dir=$taskInstallerDirectory" '.\installer\GlassDesk.iss'
    if($LASTEXITCODE -ne 0){throw 'Installer build failed.'}
    $taskInstaller=Join-Path $taskInstallerDirectory "GlassDesk-$taskVersion-win-x64-setup.exe"
    $taskHash=(Get-FileHash -LiteralPath $taskInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
    "$taskHash  $([IO.Path]::GetFileName($taskInstaller))" | Set-Content -LiteralPath (Join-Path $taskInstallerDirectory 'SHA256SUMS.txt') -Encoding utf8
    Write-Output $taskInstaller
} finally {Pop-Location}

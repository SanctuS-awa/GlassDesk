$ErrorActionPreference='Stop'
$taskSourceRoot=Split-Path -Parent $PSScriptRoot
$taskVersion=([xml](Get-Content -LiteralPath (Join-Path $taskSourceRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$taskArchiveName="GlassDesk-$taskVersion-source.zip"
$taskOutput=Join-Path $taskSourceRoot (Join-Path 'artifacts\installer' $taskArchiveName)
New-Item -ItemType Directory -Path (Split-Path -Parent $taskOutput) -Force | Out-Null
$taskFiles=Get-ChildItem -LiteralPath $taskSourceRoot -File -Recurse -Force | Where-Object {
    $taskRelative=[IO.Path]::GetRelativePath($taskSourceRoot,$_.FullName)
    $taskRelative -notmatch '(^|[\\/])(artifacts|\.tools|bin|obj|\.git|\.vs|__pycache__)([\\/]|$)'
}
$taskStream=[IO.File]::Open($taskOutput,[IO.FileMode]::Create,[IO.FileAccess]::Write)
$taskArchive=[IO.Compression.ZipArchive]::new($taskStream,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach($taskFile in ($taskFiles | Sort-Object FullName)) {
        $taskEntryName=[IO.Path]::GetRelativePath($taskSourceRoot,$taskFile.FullName).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskArchive,$taskFile.FullName,$taskEntryName,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {$taskArchive.Dispose();$taskStream.Dispose()}
$taskHash=(Get-FileHash -LiteralPath $taskOutput -Algorithm SHA256).Hash.ToLowerInvariant()
"$taskHash  $taskArchiveName" | Set-Content -LiteralPath ($taskOutput+'.sha256') -Encoding utf8
Write-Output $taskOutput


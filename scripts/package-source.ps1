$ErrorActionPreference='Stop'
$taskSourceRoot=Split-Path -Parent $PSScriptRoot
$taskOutput=Join-Path $taskSourceRoot 'artifacts\installer\GlassDesk-0.2.0-source.zip'
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
"$taskHash  GlassDesk-0.2.0-source.zip" | Set-Content -LiteralPath ($taskOutput+'.sha256') -Encoding utf8
Write-Output $taskOutput


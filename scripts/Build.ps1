param(
    [string]$DotnetPath = 'dotnet',
    [string]$OutputPath = '',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
if (-not $OutputPath) { $OutputPath = Join-Path $repo 'artifacts\publish' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (-not $SkipTests) {
    foreach ($project in @('DriveLedger.Tests', 'DriveLedger.NativeTests', 'DriveLedger.AppTests')) {
        & $DotnetPath run --project "tests/$project" -c Release
        if ($LASTEXITCODE -ne 0) { throw "$project failed" }
    }
}
& $DotnetPath publish src/DriveLedger.App -c Release -r win-x64 --self-contained true -o $OutputPath -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $OutputPath -Force
Get-ChildItem -LiteralPath (Join-Path $repo 'package') -File | Copy-Item -Destination $OutputPath -Force
# Archive sources without build outputs or private workspace state.
$sourceZip = Join-Path (Split-Path $OutputPath -Parent) 'DriveLedger-source.zip'
$files = Get-ChildItem -LiteralPath $repo -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](\.git|\.superpowers|artifacts|bin|obj)[\\/]' -and
    $_.FullName -notlike "$OutputPath\*" -and $_.Extension -ne '.db' -and $_.Name -ne '.git'
}
Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::Open($sourceZip, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($repo, $file.FullName).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative) | Out-Null
    }
} finally { $archive.Dispose(); $stream.Dispose() }
Write-Host "Published: $OutputPath"
Write-Host "Source archive: $sourceZip"

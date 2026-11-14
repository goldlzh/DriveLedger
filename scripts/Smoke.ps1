param([string]$AppPath = '', [int]$Seconds = 8)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $AppPath) { $AppPath = Join-Path $repo 'artifacts\publish\DriveLedger.App.exe' }
$fixture = Join-Path $repo ('artifacts\app-smoke\' + [guid]::NewGuid().ToString('N'))
$root = Join-Path $fixture 'root'
$data = Join-Path $fixture 'data'
New-Item -ItemType Directory -Path $root -Force | Out-Null
[IO.File]::WriteAllBytes((Join-Path $root 'baseline.bin'), [byte[]]::new(65536))
$pipe = 'DriveLedger.Fixture.' + [guid]::NewGuid().ToString('N')
function Invoke-App([string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($AppPath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "App exit: $($process.ExitCode)" }
}
$cold = Join-Path $fixture 'cold.json'
Invoke-App @('--diagnose', '--data-dir', $data, '--output', $cold)
if ((Get-Content -LiteralPath $cold -Raw | ConvertFrom-Json).DatabaseExists) { throw 'Cold diagnosis created database' }
if (Test-Path -LiteralPath $data) { throw 'Cold diagnosis created data directory' }
Invoke-App @('--collect', '--data-dir', $data, '--fixture-root', $root, '--pipe', $pipe, '--sample-seconds', '1', '--poll-seconds', '0.5', '--seconds', $Seconds.ToString())
$output = Join-Path $fixture 'diagnose.json'
Invoke-App @('--diagnose', '--data-dir', $data, '--fixture-root', $root, '--pipe', $pipe, '--output', $output)
$result = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
if (-not $result.Success -or -not $result.DatabaseExists -or $result.Report.Samples.Count -lt 2) { throw 'Fixture diagnosis missing samples' }
Write-Host "Smoke passed. Fixture: $fixture"
Write-Host "GUI: `"$AppPath`" --data-dir `"$data`" --fixture-root `"$root`" --pipe $pipe"

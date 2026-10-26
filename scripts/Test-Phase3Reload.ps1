param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]{1,48}$')][string]$RunId,
    [Parameter(Mandatory)][string]$Fixture
)
$ErrorActionPreference = 'Stop'
$probeWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$probeRecord = Get-Content -LiteralPath (Join-Path $probeWorkspace ".local/phase3-$RunId/host-process.json") -Raw | ConvertFrom-Json
$probeProcess = Get-Process -Id $probeRecord.id -ErrorAction Stop
if ($probeProcess.StartTime.ToUniversalTime().ToString('O') -ne $probeRecord.startedUtc -or $probeProcess.Path -ne (Join-Path $probeRecord.game 'PCBS2.exe')) { throw 'Recorded probe process does not match; left alone.' }
$probeRoot = [IO.Path]::GetFullPath((Join-Path $probeRecord.game "BepInEx/PCBS2Coop-diag/phase3/$RunId"))
$probeTarget = [IO.Path]::GetFullPath((Join-Path $probeRoot 'saves-host/ReloadFixture/autofreebuild.binary.zip'))
if (-not $probeTarget.StartsWith($probeRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture target escaped the disposable run.' }
if (-not (Test-Path -LiteralPath (Join-Path $probeRoot 'host/commands.txt'))) { throw 'Probe is not initialized.' }
$probeReadyDeadline = [DateTime]::UtcNow.AddSeconds(45)
while (-not (Test-Path -LiteralPath (Join-Path $probeRoot 'host/capture-001.txt')) -and [DateTime]::UtcNow -lt $probeReadyDeadline) { Start-Sleep -Milliseconds 250 }
if (-not (Test-Path -LiteralPath (Join-Path $probeRoot 'host/capture-001.txt'))) { throw 'Initial probe world did not become ready.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (-not (Test-Path -LiteralPath ($Fixture + '.coop'))) { throw 'Co-op fixtures need the matching native save and .coop sidecar from the current mod version; native-only fixtures cannot be resumed.' }
$probeZip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Fixture))
try {
    if ($probeZip.Entries.Count -ne 1 -or $probeZip.Entries[0].FullName -cne 'autofreebuild.binary') { throw 'Fixture must contain exactly autofreebuild.binary; native archive/entry names must match.' }
}
finally { $probeZip.Dispose() }
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($probeTarget)) -Force | Out-Null
Copy-Item -LiteralPath $Fixture -Destination $probeTarget -Force
Copy-Item -LiteralPath ($Fixture + '.coop') -Destination ($probeTarget + '.coop') -Force
$probeResultPath = Join-Path $probeRoot 'host/reload-result.json'
if (Test-Path -LiteralPath $probeResultPath) { Remove-Item -LiteralPath $probeResultPath }
Add-Content -LiteralPath (Join-Path $probeRoot 'host/commands.txt') -Value 'identity-fixture-resume'
$probeDeadline = [DateTime]::UtcNow.AddSeconds(45)
while (-not (Test-Path -LiteralPath $probeResultPath) -and [DateTime]::UtcNow -lt $probeDeadline) { Start-Sleep -Milliseconds 250 }
if (-not (Test-Path -LiteralPath $probeResultPath)) { throw 'No reload verdict arrived within 45 seconds.' }
$probeResult = Get-Content -LiteralPath $probeResultPath -Raw | ConvertFrom-Json
$probeResult | ConvertTo-Json
if (-not $probeResult.passed) { throw 'Paired Free Build reload did not restore the expected identities, locations and allocator.' }

param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]{1,48}$')][string]$RunId,
    [ValidateSet('host', 'join')][string]$Role = 'host',
    [string]$GameDir,
    [switch]$Deploy
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LocalPaths.ps1')
$probeGame = [IO.Path]::GetFullPath((Resolve-LocalPath -Value $GameDir -EnvName 'PCBS2_GAME_DIR' -PropName 'GameDir' -What 'game folder'))
$probeWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$probeRecordDir = Join-Path $probeWorkspace ".local/phase3-$RunId"
$probeEvidenceDir = Join-Path $probeGame "BepInEx/PCBS2Coop-diag/phase3/$RunId"
if (Test-Path -LiteralPath (Join-Path $probeRecordDir "$Role-process.json")) { throw 'That role already has a process record. Use a new run ID.' }
if (Test-Path -LiteralPath (Join-Path $probeEvidenceDir "saves-$Role")) { throw 'That role already has disposable saves. Use a new run ID.' }

# Refuse to deploy over, control or interfere with an owner's existing game.
$probeKnown = @()
if (Test-Path -LiteralPath $probeRecordDir) {
    $probeKnown = @(Get-ChildItem -LiteralPath $probeRecordDir -Filter '*-process.json' -File | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
}
$probeRunning = @(Get-Process PCBS2 -ErrorAction SilentlyContinue)
foreach ($probeProcess in $probeRunning) {
    $probeOwned = @($probeKnown | Where-Object { $_.id -eq $probeProcess.Id -and $_.startedUtc -eq $probeProcess.StartTime.ToUniversalTime().ToString('O') })
    if ($probeOwned.Count -ne 1) { throw "An unowned game instance is running (PID $($probeProcess.Id)). It was left alone." }
}
if ($Deploy -and $probeRunning.Count -gt 0) { throw 'Close only your owned probe instances before deploying; game DLLs may be locked.' }

New-Item -ItemType Directory -Path $probeRecordDir -Force | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $probeRecordDir 'owner-saves-before.json'))) {
    $probeSaveRoot = Join-Path $probeGame 'Saves'
    $probeHashes = @(if (Test-Path -LiteralPath $probeSaveRoot) {
        Get-ChildItem -LiteralPath $probeSaveRoot -File -Recurse | ForEach-Object {
            [pscustomobject]@{ name = $_.FullName.Substring($probeSaveRoot.Length); hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        }
    })
    ConvertTo-Json -InputObject $probeHashes -Depth 3 | Set-Content -LiteralPath (Join-Path $probeRecordDir 'owner-saves-before.json') -Encoding UTF8
}

$probeBuilt = Join-Path $probeWorkspace 'src/PCBS2Coop.Plugin/bin/Release/net6.0'
$probeInstalled = Join-Path $probeGame 'BepInEx/plugins/PCBS2Coop'
$probeFiles = @('PCBS2Coop.dll', 'PCBS2Coop.Network.dll', 'LiteNetLib.dll')
if ($Deploy) {
    $probeBackup = Join-Path $probeRecordDir 'plugin-before'
    if (Test-Path -LiteralPath $probeBackup) { throw 'A deployment backup already exists for this run. Use a fresh run ID.' }
    New-Item -ItemType Directory -Path $probeBackup -Force | Out-Null
    foreach ($probeFile in $probeFiles) {
        $probeOld = Join-Path $probeInstalled $probeFile
        if (Test-Path -LiteralPath $probeOld) { Copy-Item -LiteralPath $probeOld -Destination $probeBackup }
    }
    foreach ($probeFile in $probeFiles) { Copy-Item -LiteralPath (Join-Path $probeBuilt $probeFile) -Destination (Join-Path $probeInstalled $probeFile) -Force }
}
$probeDeployHashes = @(foreach ($probeFile in $probeFiles) {
    $probeBuildHash = (Get-FileHash -LiteralPath (Join-Path $probeBuilt $probeFile) -Algorithm SHA256).Hash
    $probeGameHash = (Get-FileHash -LiteralPath (Join-Path $probeInstalled $probeFile) -Algorithm SHA256).Hash
    if ($probeBuildHash -ne $probeGameHash) { throw "Deployed $probeFile differs from the build. Deploy before launching." }
    [pscustomobject]@{ file = $probeFile; hash = $probeGameHash }
})
ConvertTo-Json -InputObject $probeDeployHashes | Set-Content -LiteralPath (Join-Path $probeRecordDir 'deployed-hashes.json') -Encoding UTF8

$probeEnvironment = @{}
foreach ($probeName in @('PCBS2COOP_AUTOTEST', 'PCBS2COOP_PROBE', 'PCBS2COOP_PROBE_RUN')) { $probeEnvironment[$probeName] = [Environment]::GetEnvironmentVariable($probeName) }
try {
    $env:PCBS2COOP_AUTOTEST = $Role
    $env:PCBS2COOP_PROBE = '3'
    $env:PCBS2COOP_PROBE_RUN = $RunId
    # This is the interactive game under test, not a background helper window.
    $probeLaunched = Start-Process -FilePath (Join-Path $probeGame 'PCBS2.exe') -WorkingDirectory $probeGame -ArgumentList @('-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720') -PassThru
    $probeRecord = [pscustomobject]@{ id = $probeLaunched.Id; startedUtc = $probeLaunched.StartTime.ToUniversalTime().ToString('O'); game = $probeGame; run = $RunId; role = $Role }
    $probeRecord | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $probeRecordDir "$Role-process.json") -Encoding UTF8
    $probeRecord | ConvertTo-Json
}
finally {
    foreach ($probeName in $probeEnvironment.Keys) { [Environment]::SetEnvironmentVariable($probeName, $probeEnvironment[$probeName]) }
}

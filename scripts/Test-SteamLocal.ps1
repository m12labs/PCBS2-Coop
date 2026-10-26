# Single-account in-game proof. Does not send invites. Remote P2P/relay needs a separate manual two-PC run.
[CmdletBinding()]
param([string]$GameDir)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LocalPaths.ps1')
$steamGame = [IO.Path]::GetFullPath((Resolve-LocalPath -Value $GameDir -EnvName 'PCBS2_GAME_DIR' -PropName 'GameDir' -What 'game folder'))
$steamWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (@(Get-Process PCBS2 -ErrorAction SilentlyContinue).Count -ne 0) { throw 'A game is already running. It was left alone. Close it before running the isolated test.' }
$steamRun = Join-Path $steamWorkspace ('.local/steamworks/game-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $steamRun | Out-Null
function Get-OwnerSaveHashes {
    $saveRoot = Join-Path $steamGame 'Saves'
    @(if (Test-Path -LiteralPath $saveRoot) {
        Get-ChildItem -LiteralPath $saveRoot -Recurse -File | ForEach-Object {
            [pscustomobject]@{ name = $_.FullName.Substring($saveRoot.Length); hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        }
    })
}
$steamBefore = @(Get-OwnerSaveHashes)
ConvertTo-Json -InputObject $steamBefore | Set-Content -LiteralPath (Join-Path $steamRun 'saves-before.json') -Encoding UTF8
dotnet build (Join-Path $steamWorkspace 'PCBS2Coop.sln') -c Release -p:DeployToGame=false
if ($LASTEXITCODE -ne 0) { throw 'Build failed; no game launched.' }
$steamBuild = Join-Path $steamWorkspace 'src/PCBS2Coop.Plugin/bin/Release/net6.0'
$steamInstalled = Join-Path $steamGame 'BepInEx/plugins/PCBS2Coop'
$steamFiles = @('PCBS2Coop.dll', 'PCBS2Coop.Network.dll', 'LiteNetLib.dll', 'steam/PCBS2Coop.Steam.dll', 'steam/Facepunch.Steamworks.Win64.dll', 'steam/steam_api64.dll', 'steam/Facepunch.Steamworks.LICENSE', 'steam/Steamworks.NOTICES.md')
if (Test-Path -LiteralPath (Join-Path $steamBuild 'pcbs2coop-player.bundle')) { $steamFiles += 'pcbs2coop-player.bundle' }
foreach ($steamFile in $steamFiles) {
    $destination = Join-Path $steamInstalled $steamFile
    $backup = Join-Path $steamRun ('plugin-before/' + $steamFile)
    if (Test-Path -LiteralPath $destination) {
        New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($backup)) | Out-Null
        Copy-Item -LiteralPath $destination -Destination $backup
    }
    New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($destination)) | Out-Null
    $source = Join-Path $steamBuild $steamFile
    Copy-Item -LiteralPath $source -Destination $destination -Force
    if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) { throw "Deployment mismatch: $steamFile" }
}
$steamPrevious = @{}
foreach ($name in @('PCBS2COOP_STEAM_PROBE', 'PCBS2COOP_AUTOTEST', 'PCBS2COOP_PROBE', 'PCBS2COOP_PROBE_RUN')) { $steamPrevious[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $env:PCBS2COOP_AUTOTEST = $null
    $env:PCBS2COOP_PROBE = $null
    $env:PCBS2COOP_PROBE_RUN = $null
    $env:PCBS2COOP_STEAM_PROBE = '1'
    $steamOwned = Start-Process -FilePath (Join-Path $steamGame 'PCBS2.exe') -WorkingDirectory $steamGame -ArgumentList @('-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720') -PassThru
}
finally { foreach ($name in $steamPrevious.Keys) { [Environment]::SetEnvironmentVariable($name, $steamPrevious[$name]) } }
$steamStart = $steamOwned.StartTime.ToUniversalTime().ToString('O')
[pscustomobject]@{ id = $steamOwned.Id; startedUtc = $steamStart } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $steamRun 'process.json')
if (!$steamOwned.WaitForExit(300000)) {
    $verify = Get-Process -Id $steamOwned.Id -ErrorAction Stop
    if ($verify.StartTime.ToUniversalTime().ToString('O') -eq $steamStart -and $verify.Path -eq (Join-Path $steamGame 'PCBS2.exe')) { Stop-Process -InputObject $verify }
    throw 'Owned Steam probe timed out. See the game log.'
}
$steamAfter = @(Get-OwnerSaveHashes)
ConvertTo-Json -InputObject $steamAfter | Set-Content -LiteralPath (Join-Path $steamRun 'saves-after.json') -Encoding UTF8
$steamChanged = Compare-Object -ReferenceObject $steamBefore -DifferenceObject $steamAfter -Property name,hash
if ($steamChanged) { throw 'Owner save hashes changed during the isolated test. Review evidence before proceeding.' }
$steamLog = Join-Path $steamGame 'BepInEx/LogOutput.log'
Copy-Item -LiteralPath $steamLog -Destination (Join-Path $steamRun 'LogOutput.log')
$steamVerdict = @(Select-String -LiteralPath $steamLog -Pattern 'STEAM-PROBE: (PASSED|FAILED)')
$steamVerdict | ForEach-Object { Write-Host $_.Line }
if ($steamVerdict.Count -ne 1 -or $steamVerdict[0].Line -notmatch 'STEAM-PROBE: PASSED') { throw 'Steam probe failed; see captured log.' }
Write-Host 'Owner saves unchanged. Latest build remains installed; previous binaries are in the ignored run folder.'

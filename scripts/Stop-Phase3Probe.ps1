param([Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]{1,48}$')][string]$RunId,
    [ValidateSet('3', '4')][string]$Phase = '3')
$ErrorActionPreference = 'Stop'
$probeWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$probeRecordDir = Join-Path $probeWorkspace ".local/phase$Phase-$RunId"
$probeRecords = @(Get-ChildItem -LiteralPath $probeRecordDir -Filter '*-process.json' -File | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
if ($probeRecords.Count -eq 0) { throw 'No owned probe processes recorded.' }
foreach ($probeRecord in $probeRecords) {
    $probeProcess = Get-Process -Id $probeRecord.id -ErrorAction SilentlyContinue
    if ($probeProcess) {
        if ($probeProcess.ProcessName -ne 'PCBS2' -or $probeProcess.StartTime.ToUniversalTime().ToString('O') -ne $probeRecord.startedUtc -or $probeProcess.Path -ne (Join-Path $probeRecord.game 'PCBS2.exe')) {
            throw "Process identity no longer matches the owned probe record (PID $($probeRecord.id)); left alone."
        }
        Stop-Process -Id $probeProcess.Id
        if (-not $probeProcess.WaitForExit(10000)) { throw "Owned probe PID $($probeProcess.Id) did not exit; do not deploy over its DLLs." }
    }
}
$probeGame = $probeRecords[0].game
foreach ($probeLogName in @('LogOutput.log', 'LogOutput.1.log')) {
    $probeLog = Join-Path $probeGame "BepInEx/$probeLogName"
    $probeFirstStart = ($probeRecords | ForEach-Object { [DateTime]::Parse($_.startedUtc).ToUniversalTime() } | Sort-Object | Select-Object -First 1)
    if ((Test-Path -LiteralPath $probeLog) -and (Get-Item -LiteralPath $probeLog).LastWriteTimeUtc -ge $probeFirstStart) { Copy-Item -LiteralPath $probeLog -Destination $probeRecordDir -Force }
}
$probeSaveRoot = Join-Path $probeGame 'Saves'
$probeAfter = @(if (Test-Path -LiteralPath $probeSaveRoot) {
    Get-ChildItem -LiteralPath $probeSaveRoot -File -Recurse | ForEach-Object {
        [pscustomobject]@{ name = $_.FullName.Substring($probeSaveRoot.Length); hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }
})
ConvertTo-Json -InputObject $probeAfter -Depth 3 | Set-Content -LiteralPath (Join-Path $probeRecordDir 'owner-saves-after.json') -Encoding UTF8
$probeBefore = Get-Content -LiteralPath (Join-Path $probeRecordDir 'owner-saves-before.json') -Raw | ConvertFrom-Json
$probeBeforeKeys = @($probeBefore | ForEach-Object { $_.name + ':' + $_.hash } | Sort-Object)
$probeAfterKeys = @($probeAfter | ForEach-Object { $_.name + ':' + $_.hash } | Sort-Object)
$probeSame = ($probeBeforeKeys.Count -eq $probeAfterKeys.Count) -and (($probeBeforeKeys -join "`n") -ceq ($probeAfterKeys -join "`n"))
[pscustomobject]@{ run = $RunId; stoppedOwnedProcesses = $probeRecords.Count; ownerSaveFiles = $probeAfterKeys.Count; ownerSavesUnchanged = $probeSame } | ConvertTo-Json
if (-not $probeSame) { throw 'Owner save hashes differ; inspect the saved before/after manifests.' }

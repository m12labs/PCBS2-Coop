param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]{1,48}$')][string]$RunId,
    [ValidateSet('host', 'join')][string]$Role = 'host',
    [Parameter(Mandatory)][string[]]$Command
)
$ErrorActionPreference = 'Stop'
$probeWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$probeRecordFile = Join-Path $probeWorkspace ".local/phase4-$RunId/$Role-process.json"
$probeRecord = Get-Content -LiteralPath $probeRecordFile -Raw | ConvertFrom-Json
$probeProcess = Get-Process -Id $probeRecord.id -ErrorAction Stop
if ($probeRecord.run -ne $RunId -or $probeRecord.role -ne $Role -or $probeProcess.ProcessName -ne 'PCBS2' -or
    $probeProcess.StartTime.ToUniversalTime().ToString('O') -ne $probeRecord.startedUtc -or
    $probeProcess.Path -ne (Join-Path $probeRecord.game 'PCBS2.exe')) { throw 'Owned phase 4 process identity does not match.' }
$probeRoot = Join-Path $probeRecord.game "BepInEx/PCBS2Coop-diag/phase4/$RunId"
$probeCommands = Join-Path $probeRoot "$Role/commands.txt"
if (-not (Test-Path -LiteralPath (Join-Path $probeRoot "saves-$Role") -PathType Container) -or
    -not (Test-Path -LiteralPath $probeCommands -PathType Leaf)) { throw 'Probe has not established its disposable save folder and command file.' }
foreach ($probeCommand in $Command) {
    if ($probeCommand.Length -gt 256 -or $probeCommand -match '[\r\n]') { throw 'Each command must be one line, at most 256 characters.' }
}
Add-Content -LiteralPath $probeCommands -Value $Command -Encoding UTF8

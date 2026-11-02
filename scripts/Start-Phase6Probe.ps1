param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]{1,48}$')][string]$RunId,
    [ValidateSet('host', 'join')][string]$Role = 'host',
    [string]$GameDir,
    [switch]$Deploy,
    # Relaunch a host on this run's own disposable saves (fresh-process Continue).
    [switch]$Resume
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Start-Phase3Probe.ps1') -RunId $RunId -Role $Role -GameDir $GameDir -Deploy:$Deploy -Phase 6 -Resume:$Resume

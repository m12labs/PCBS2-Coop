param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]{1,48}$')][string]$RunId,
    [ValidateSet('host', 'join')][string]$Role = 'host',
    [string]$GameDir,
    [switch]$Deploy
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Start-Phase3Probe.ps1') -RunId $RunId -Role $Role -GameDir $GameDir -Deploy:$Deploy -Phase 6

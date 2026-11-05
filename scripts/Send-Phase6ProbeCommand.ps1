param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]{1,48}$')][string]$RunId,
    [ValidateSet('host', 'join')][string]$Role = 'host',
    [Parameter(Mandatory)][string[]]$Command
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Send-Phase4ProbeCommand.ps1') -RunId $RunId -Role $Role -Command $Command -Phase 6

param([Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]{1,48}$')][string]$RunId)
& (Join-Path $PSScriptRoot 'Stop-Phase3Probe.ps1') -RunId $RunId -Phase '4'

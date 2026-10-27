param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]{1,48}$')][string]$RunId,
    [ValidateSet('host', 'join')][string]$Role = 'host',
    [string]$GameDir,
    [switch]$Deploy,
    [switch]$Resume
)
& (Join-Path $PSScriptRoot 'Start-Phase3Probe.ps1') @PSBoundParameters -Phase '4'

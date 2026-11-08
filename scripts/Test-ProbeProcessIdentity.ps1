$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ProbeProcessIdentity.ps1')

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

$live = Get-Process -Id $PID -ErrorAction Stop
$record = [pscustomobject]@{
    id = [long]$live.Id
    startedUtc = $live.StartTime.ToUniversalTime().ToString('O', [Globalization.CultureInfo]::InvariantCulture)
    game = [IO.Path]::GetDirectoryName($live.Path)
}
# Exercise the same serialization boundary used by the launcher. PS 7 converts this field to
# DateTime; Windows PowerShell 5.1 leaves it as a string.
$roundTripped = $record | ConvertTo-Json | ConvertFrom-Json
$expectedPath = $live.Path

Assert-True (Test-ProbeProcessIdentity -Process $live -Record $roundTripped -ExpectedName $live.ProcessName -ExpectedPath $expectedPath) `
    'A live process identity failed after a JSON round trip.'

$wrongTime = [pscustomobject]@{ id = $roundTripped.id; startedUtc = (ConvertTo-ProbeUtcDateTimeOffset $roundTripped.startedUtc).AddSeconds(-1); game = $roundTripped.game }
Assert-True (-not (Test-ProbeProcessIdentity -Process $live -Record $wrongTime -ExpectedName $live.ProcessName -ExpectedPath $expectedPath)) `
    'A mismatched process start time was accepted.'

$oneTickOff = [pscustomobject]@{ id = $roundTripped.id; startedUtc = (ConvertTo-ProbeUtcDateTimeOffset $roundTripped.startedUtc).AddTicks(1); game = $roundTripped.game }
Assert-True (-not (Test-ProbeProcessIdentity -Process $live -Record $oneTickOff -ExpectedName $live.ProcessName -ExpectedPath $expectedPath)) `
    'A one-tick process start time mismatch was accepted.'

$wrongPid = [pscustomobject]@{ id = ([long]$roundTripped.id + 1); startedUtc = $roundTripped.startedUtc; game = $roundTripped.game }
Assert-True (-not (Test-ProbeProcessIdentity -Process $live -Record $wrongPid -ExpectedName $live.ProcessName -ExpectedPath $expectedPath)) `
    'A mismatched PID was accepted.'

Assert-True (-not (Test-ProbeProcessIdentity -Process $live -Record $roundTripped -ExpectedName $live.ProcessName -ExpectedPath ($expectedPath + '.other'))) `
    'A mismatched executable path was accepted.'

Assert-True (-not (Test-ProbeProcessIdentity -Process $null -Record $roundTripped -ExpectedName $live.ProcessName -ExpectedPath $expectedPath)) `
    'A missing process was accepted as an owned process.'

Write-Output "Probe process identity regression passed (PowerShell $($PSVersionTable.PSVersion))."

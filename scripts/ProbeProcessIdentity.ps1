# Shared identity checks for owned probe processes. ConvertFrom-Json returns startedUtc as a
# DateTime in PowerShell 7 but as a string in Windows PowerShell 5.1; compare parsed UTC ticks.
function ConvertTo-ProbeUtcDateTimeOffset {
    param([Parameter(Mandatory)]$Value)

    if ($Value -is [DateTimeOffset]) { return $Value.ToUniversalTime() }
    if ($Value -is [DateTime]) { return ([DateTimeOffset]$Value).ToUniversalTime() }

    $styles = [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal
    return [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture, $styles)
}

function Test-ProbeProcessIdentity {
    param(
        [AllowNull()]
        [Parameter(Mandatory)]$Process,
        [AllowNull()]
        [Parameter(Mandatory)]$Record,
        [Parameter(Mandatory)][string]$ExpectedName,
        [Parameter(Mandatory)][string]$ExpectedPath
    )

    if ($null -eq $Process -or $null -eq $Record) { return $false }
    if ([long]$Process.Id -ne [long]$Record.id) { return $false }
    if (-not [string]::Equals([string]$Process.ProcessName, $ExpectedName, [StringComparison]::OrdinalIgnoreCase)) { return $false }
    if (-not [string]::Equals([string]$Process.Path, $ExpectedPath, [StringComparison]::OrdinalIgnoreCase)) { return $false }

    try {
        $recordStart = ConvertTo-ProbeUtcDateTimeOffset -Value $Record.startedUtc
        $processStart = ([DateTimeOffset]$Process.StartTime).ToUniversalTime()
        return $recordStart.UtcDateTime.Ticks -eq $processStart.UtcDateTime.Ticks
    }
    catch { return $false }
}

# Machine-specific folders (game, Ghidra, Cpp2IL) are never written into the repository.
# Dot-source this file, then call Resolve-LocalPath. An explicit parameter wins, then an environment variable,
# then the git-ignored Directory.Build.local.props at the repository root (see Directory.Build.local.props.example).

function Resolve-LocalPath {
    param(
        [string]$Value,
        [Parameter(Mandatory)][string]$EnvName,
        [Parameter(Mandatory)][string]$PropName,
        [Parameter(Mandatory)][string]$What,
        [switch]$Optional
    )
    if ($Value) { return $Value }
    $fromEnv = [Environment]::GetEnvironmentVariable($EnvName)
    if ($fromEnv) { return $fromEnv }
    $props = Join-Path $PSScriptRoot '..\Directory.Build.local.props'
    if (Test-Path -LiteralPath $props) {
        $node = ([xml](Get-Content -LiteralPath $props -Raw)).SelectSingleNode("//*[local-name()='$PropName']")
        if ($node -and $node.InnerText.Trim()) { return $node.InnerText.Trim() }
    }
    if ($Optional) { return $null }
    throw "Set the $What`: pass it as a parameter, set the $EnvName environment variable, or add <$PropName> to Directory.Build.local.props (copy Directory.Build.local.props.example)."
}

[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $PSScriptRoot '..\.local\loader-install.json'
$install = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$root = (Resolve-Path -LiteralPath $install.GameDir).Path
if (Get-Process -Name 'PCBS2' -ErrorAction SilentlyContinue) { throw 'Close the game first.' }
foreach ($file in $install.Files) {
    $target = [IO.Path]::GetFullPath((Join-Path $root $file.Relative))
    if (!$target.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe manifest path.' }
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileHash -LiteralPath $target).Hash -ne $file.Hash) { throw "File changed since installation; inspect it: $target" }
        if ($file.Backup) { Copy-Item -LiteralPath $file.Backup -Destination $target -Force }
        else { Remove-Item -LiteralPath $target }
    }
}
Remove-Item -LiteralPath $manifestPath
Write-Host 'Loader files restored/removed. Generated interop, config, logs and plugins were kept in BepInEx\.'

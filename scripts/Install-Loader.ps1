[CmdletBinding()]
param([string]$GameDir = $env:PCBS2_GAME_DIR)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
if (!(Test-Path -LiteralPath (Join-Path $GameDir 'GameAssembly.dll'))) { throw 'Expected an IL2CPP game directory.' }
if (Get-Process -Name 'PCBS2' -ErrorAction SilentlyContinue) { throw 'Close the game before installing.' }

$version = '6.0.0-be.791+b782da4'
$expected = 'BB1C510909B69522733791F3A0817A487379A52DA6468A492F1C9C19B3EC6F9A'
$archive = Join-Path $repo ".local\downloads\BepInEx-Unity.IL2CPP-win-x64-$version.zip"
$staged = Join-Path $repo "artiffacts\downloads\BepInEx-Unity.IL2CPP-win-x64-$version.zip"
New-Item -ItemType Directory -Force -Path (Split-Path $archive) | Out-Null
if (!(Test-Path -LiteralPath $archive)) {
    if (Test-Path -LiteralPath $staged) { Copy-Item -LiteralPath $staged -Destination $archive }
    else { Invoke-WebRequest 'https://builds.bepinex.dev/projects/bepinex_be/791/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.791%2Bb782da4.zip' -OutFile $archive }
}
if ((Get-FileHash -LiteralPath $archive).Hash -ne $expected) { throw 'Loader archive SHA256 differs from recorded artifact.' }

$staging = Join-Path $repo '.local\loader'
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
Expand-Archive -LiteralPath $archive -DestinationPath $staging -Force

$manifestPath = Join-Path $repo '.local\loader-install.json'
if (Test-Path -LiteralPath $manifestPath) { throw 'Loader installation is already recorded. Uninstall before reinstalling.' }
if (Get-ChildItem -LiteralPath (Join-Path $GameDir 'BepInEx\core') -Filter 'BepInEx*.dll' -ErrorAction SilentlyContinue) { throw 'Existing BepInEx install found; uninstall it first.' }

$backup = Join-Path $repo '.local\loader-backup'
$records = @()
foreach ($file in Get-ChildItem -LiteralPath $staging -Recurse -File) {
    $relative = $file.FullName.Substring($staging.Length + 1)
    $target = Join-Path $GameDir $relative
    $prior = $null
    if (Test-Path -LiteralPath $target) {
        $prior = Join-Path $backup $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $prior) | Out-Null
        Copy-Item -LiteralPath $target -Destination $prior
    }
    $records += [PSCustomObject]@{ Relative = $relative; Backup = $prior; Hash = (Get-FileHash -LiteralPath $file.FullName).Hash }
}
# Save rollback information before touching the game folder.
@{ GameDir = $GameDir; Version = $version; ArchiveSHA256 = $expected; Files = $records } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
foreach ($file in Get-ChildItem -LiteralPath $staging -Recurse -File) {
    $target = Join-Path $GameDir $file.FullName.Substring($staging.Length + 1)
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
}
Write-Host "Installed BepInEx $version into $GameDir. Launch the game once to generate BepInEx\interop; check BepInEx\LogOutput.log."

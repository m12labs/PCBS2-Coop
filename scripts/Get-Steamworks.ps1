# Download only the official pinned release into the ignored local cache. No game files are changed.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$cache = Join-Path $PSScriptRoot '../.local/steamworks'
New-Item -ItemType Directory -Force $cache | Out-Null
$archive = Join-Path $cache 'Facepunch.Steamworks.2.5.2.zip'
$expected = '83EF0B8B07BD5545C3732C65011F0BAA9BF003CB53C2279C56397270368BCA22'
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest 'https://github.com/Facepunch/Facepunch.Steamworks/releases/download/2.5.2/Facepunch.Steamworks.2.5.2.zip' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'Steamworks release archive checksum mismatch.' }
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $cache '2.5.2') -Force
$files = @{
    'Facepunch.Steamworks.Win64.dll' = '11DE6033EC2009C418C23428C2E64D03C729B95691FB29BACC3B145AA4A4EAD5'
    'steam_api64.dll' = '670D654AA3255C5061CF0236A1CDBB2F5076CBD5AE44F613B6F4921558E83E2D'
}
foreach ($name in $files.Keys) {
    $path = Join-Path $cache "2.5.2/Release/net6.0/$name"
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $files[$name]) { throw "Steamworks dependency checksum mismatch: $name" }
}
Write-Host 'Facepunch.Steamworks 2.5.2 Win64/net6.0 verified and ready.'

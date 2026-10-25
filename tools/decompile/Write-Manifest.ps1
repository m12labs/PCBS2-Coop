param(
    [string]$GamePath,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\..\artiffacts\decompiled'),
    [string]$GhidraHome
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\scripts\LocalPaths.ps1')
$GamePath = Resolve-LocalPath $GamePath -EnvName 'PCBS2_GAME_DIR' -PropName 'GameDir' -What 'game folder'
$GhidraHome = Resolve-LocalPath $GhidraHome -EnvName 'GHIDRA_HOME' -PropName 'GhidraHome' -What 'Ghidra folder'
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$GamePath = [IO.Path]::GetFullPath($GamePath).TrimEnd('\')
$indexRoot = Join-Path $OutputRoot 'indexes'
New-Item -ItemType Directory -Path $indexRoot -Force | Out-Null
# Inventory shipped files only. Do not read user saves, launcher data, or exported PCs.
$files = @(Get-ChildItem -LiteralPath $GamePath -File)
$files += @(Get-ChildItem -LiteralPath (Join-Path $GamePath 'PCBS2_Data') -Recurse -File)
$files += @(Get-ChildItem -LiteralPath (Join-Path $GamePath 'D3D12') -Recurse -File -ErrorAction SilentlyContinue)
$files | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{
        Path = $_.FullName.Substring($GamePath.Length + 1).Replace('\', '/')
        Bytes = $_.Length
        LastWriteUtc = $_.LastWriteTimeUtc.ToString('o')
    }
} | Export-Csv -LiteralPath (Join-Path $indexRoot 'game-files.csv') -NoTypeInformation
$native = @($files | Where-Object Extension -In '.dll', '.exe' | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path = $_.FullName.Substring($GamePath.Length + 1).Replace('\', '/')
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$preserved = @(Get-ChildItem -LiteralPath (Join-Path $OutputRoot 'input') -File | Sort-Object Name | ForEach-Object {
    [ordered]@{ path = "input/$($_.Name)"; bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$header = New-Object byte[] 128
$stream = [IO.File]::OpenRead((Join-Path $GamePath 'PCBS2_Data\globalgamemanagers'))
try { [void]$stream.Read($header, 0, $header.Length) } finally { $stream.Dispose() }
$unityVersion = [regex]::Match([Text.Encoding]::ASCII.GetString($header), '\d{4}\.\d+\.\d+[abfp]\d+').Value
$toolVersion = (& (Join-Path $OutputRoot 'tools\Cpp2IL\Cpp2IL.exe') --version) -join "`n"
$metadataLog = [IO.File]::ReadAllText((Join-Path $OutputRoot 'logs\cpp2il-assemblies.log'))
$ghidraVersion = (Get-Content -LiteralPath (Join-Path $GhidraHome 'Ghidra\application.properties') | Where-Object { $_ -like 'application.version=*' }) -replace '^application.version=', ''
$manifest = [ordered]@{
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    gamePath = $GamePath
    unityVersion = $unityVersion
    backend = 'IL2CPP x86-64 Windows'
    metadataVersion = [regex]::Match($metadataLog, 'actual IL2CPP Metadata version ([^\r\n]+)').Groups[1].Value
    codeRegistration = [regex]::Match($metadataLog, 'codereg: (0x[0-9A-Fa-f]+)').Groups[1].Value
    metadataRegistration = [regex]::Match($metadataLog, 'metareg: (0x[0-9A-Fa-f]+)').Groups[1].Value
    buildGuid = ((Get-Content -LiteralPath (Join-Path $GamePath 'PCBS2_Data\boot.config') | Where-Object { $_ -like 'build-guid=*' }) -replace '^build-guid=', '')
    cpp2ilVersion = [regex]::Match($toolVersion, 'Version ([^\r\n]+)').Groups[1].Value
    cpp2ilSha256 = (Get-FileHash -LiteralPath (Join-Path $OutputRoot 'tools\Cpp2IL\Cpp2IL.exe')).Hash.ToLowerInvariant()
    ghidraVersion = $ghidraVersion
    preservedInputs = $preserved
    shippedNativeBinaries = $native
    scope = 'All retained IL2CPP assemblies and GameAssembly native functions. Native engine/vendor binaries are inventoried. Serialized assets are inventoried, not reconstructed as a Unity project.'
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputRoot 'manifest.json') -Encoding UTF8
$buildDatesOutput = Join-Path $OutputRoot 'input\BuildDates'
New-Item -ItemType Directory -Path $buildDatesOutput -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $GamePath 'PCBS2_Data\StreamingAssets\BuildDates') -File | Copy-Item -Destination $buildDatesOutput -Force
Write-Output "Manifest written; $($files.Count) shipped files inventoried, $($native.Count) native binaries hashed."

param([string]$OutputRoot = (Join-Path $PSScriptRoot '..\..\artiffacts\decompiled'))
$ErrorActionPreference = 'Stop'
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$nativeRoot = Join-Path $OutputRoot 'native\pseudocode'
$good = New-Object 'System.Collections.Generic.HashSet[ulong]'
$all = New-Object 'System.Collections.Generic.HashSet[ulong]'
$failed = New-Object 'System.Collections.Generic.List[string]'
$functions = 0
foreach ($line in [IO.File]::ReadLines((Join-Path $nativeRoot 'functions.tsv'))) {
    $row = $line.Split("`t")
    if ($row[0] -eq 'address') { continue }
    $address = [Convert]::ToUInt64($row[0], 16)
    [void]$all.Add($address)
    $functions++
    if ($row[2] -eq 'ok') {
        $file = Join-Path $nativeRoot $row[3]
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) { throw "Missing native output: $file" }
        [void]$good.Add($address)
    } else { $failed.Add($line) }
}
$reader = New-Object IO.BinaryReader([IO.File]::OpenRead((Join-Path $OutputRoot 'input\GameAssembly.dll')))
try {
    $reader.BaseStream.Position = 0x3C
    $peOffset = $reader.ReadInt32()
    $reader.BaseStream.Position = $peOffset
    if ($reader.ReadUInt32() -ne 0x4550) { throw 'Invalid PE signature.' }
    $reader.BaseStream.Position = $peOffset + 24
    if ($reader.ReadUInt16() -ne 0x20B) { throw 'Expected a 64-bit PE binary.' }
    $reader.BaseStream.Position = $peOffset + 48
    $imageBase = $reader.ReadUInt64()
} finally { $reader.Dispose() }
$addresses = New-Object 'System.Collections.Generic.HashSet[ulong]'
$gameAddresses = New-Object 'System.Collections.Generic.HashSet[ulong]'
$missing = New-Object 'System.Collections.Generic.List[string]'
$methodCount = 0
foreach ($line in [IO.File]::ReadLines((Join-Path $OutputRoot 'indexes\methods.tsv'))) {
    $row = $line.Split("`t")
    if ($row[0] -eq 'rva') { continue }
    $address = $imageBase + [Convert]::ToUInt64($row[0].Substring(2), 16)
    $methodCount++
    [void]$addresses.Add($address)
    if ($row[3] -eq 'Assembly-CSharp') { [void]$gameAddresses.Add($address) }
    if (-not $good.Contains($address)) { $missing.Add($line) }
}
$header = [IO.File]::ReadLines((Join-Path $OutputRoot 'indexes\methods.tsv')) | Select-Object -First 1
[IO.File]::WriteAllLines((Join-Path $OutputRoot 'indexes\methods-without-pseudocode.tsv'), @($header) + $missing.ToArray())
[IO.File]::WriteAllLines((Join-Path $OutputRoot 'indexes\failed-native-functions.tsv'), @("address`tname`tstatus`tfile`tdetail") + $failed.ToArray())
$covered = @($addresses | Where-Object { $good.Contains($_) }).Count
$gameCovered = @($gameAddresses | Where-Object { $good.Contains($_) }).Count
$report = [ordered]@{
    assemblyCount = @(Get-ChildItem -LiteralPath (Join-Path $OutputRoot 'metadata\assemblies') -Filter '*.dll' -File).Count
    imageBase = ('0x{0:X}' -f $imageBase)
    csharpFileCount = @(Get-ChildItem -LiteralPath (Join-Path $OutputRoot 'metadata\csharp') -Filter '*.cs' -Recurse -File).Count
    instructionFileCount = @(Get-ChildItem -LiteralPath (Join-Path $OutputRoot 'native\isil') -Filter '*.txt' -Recurse -File).Count
    addressedMethodEntries = $methodCount
    distinctMethodAddresses = $addresses.Count
    distinctMethodAddressesWithPseudocode = $covered
    distinctGameMethodAddresses = $gameAddresses.Count
    distinctGameMethodAddressesWithPseudocode = $gameCovered
    nativeFunctionCount = $functions
    nativeFunctionsDecompiled = $good.Count
    nativeFunctionsFailed = $failed.Count
    methodEntriesWithoutPseudocode = $missing.Count
}
$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot 'coverage.json') -Encoding UTF8
$report | ConvertTo-Json

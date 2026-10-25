param([string]$OutputRoot = (Join-Path $PSScriptRoot '..\..\artiffacts\decompiled'))
$ErrorActionPreference = 'Stop'
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$sourceRoot = Join-Path $OutputRoot 'metadata\csharp\DiffableCs'
$indexRoot = Join-Path $OutputRoot 'indexes'
New-Item -ItemType Directory -Path $indexRoot -Force | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
$methodIndex = Join-Path $indexRoot 'methods.tsv'
$writer = New-Object IO.StreamWriter(($methodIndex + '.tmp'), $false, $utf8)
$pattern = '(?m)^(?<indent>\t+)\[Address\(RVA = "(?<rva>0x[0-9A-Fa-f]+)", Offset = "(?<offset>0x[0-9A-Fa-f]+)", Length = "(?<length>0x[0-9A-Fa-f]+)"\)\]\r?\n(?:\t+\[[^\r\n]+\]\r?\n)*\t+(?<signature>[^\r\n]+)'
$counts = @{}
try {
    $writer.WriteLine("rva`tfile_offset`tlength`tassembly`ttype`tsignature`tcsharp_file`tmetadata_token")
    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -Filter '*.cs' -File) {
        $relative = $file.FullName.Substring($sourceRoot.Length + 1).Replace('\', '/')
        $parts = $relative.Split('/')
        $assembly = $parts[0]
        $outerType = $relative.Substring($assembly.Length + 1) -replace '\.cs$', ''
        $text = [IO.File]::ReadAllText($file.FullName)
        $declarations = [regex]::Matches($text, '(?m)^(?<indent>\t*)(?:(?:public|private|protected|internal|static|sealed|abstract|readonly|ref)\s+)*(?:class|struct|interface|enum)\s+(?<name>[^\s:{]+)')
        $declarationIndex = 0
        $nestedTypes = @{}
        foreach ($match in [regex]::Matches($text, $pattern)) {
            while ($declarationIndex -lt $declarations.Count -and $declarations[$declarationIndex].Index -lt $match.Index) {
                $declaration = $declarations[$declarationIndex++]
                $depth = $declaration.Groups['indent'].Length
                foreach ($key in @($nestedTypes.Keys)) { if ($key -ge $depth) { $nestedTypes.Remove($key) } }
                if ($depth -gt 0) { $nestedTypes[$depth] = $declaration.Groups['name'].Value }
            }
            $methodDepth = $match.Groups['indent'].Length
            foreach ($key in @($nestedTypes.Keys)) { if ($key -ge $methodDepth) { $nestedTypes.Remove($key) } }
            $type = $outerType
            foreach ($key in @($nestedTypes.Keys | Sort-Object)) { $type += '+' + $nestedTypes[$key] }
            $signature = $match.Groups['signature'].Value.Trim()
            $token = [regex]::Match($match.Value, '\[Token\(Token = "(0x[0-9A-Fa-f]+)"\)\]').Groups[1].Value
            $writer.WriteLine(($match.Groups['rva'].Value, $match.Groups['offset'].Value, $match.Groups['length'].Value, $assembly, $type, $signature, "metadata/csharp/DiffableCs/$relative", $token) -join "`t")
            $counts[$assembly]++
        }
    }
} finally { $writer.Dispose() }
Move-Item -LiteralPath ($methodIndex + '.tmp') -Destination $methodIndex -Force
$counts.GetEnumerator() | Sort-Object Name | ForEach-Object {
    [pscustomobject]@{ Assembly = $_.Key; AddressedMethods = $_.Value }
} | Export-Csv -LiteralPath (Join-Path $indexRoot 'assembly-coverage.csv') -NoTypeInformation

$candidateTypes = @('SimplePlayerMovement', 'WorkshopController', 'PlayerStatus', 'Inventory', 'ComponentPC', 'PartInstance', 'BenchSlot', 'Slot', 'Cable', 'CableInstance', 'SaveLoadSystem', 'SaveGame', 'ComputerSave', 'CareerStatus', 'Job', 'JobStatus', 'DayMonitor')
$writer = New-Object IO.StreamWriter((Join-Path $indexRoot 'multiplayer-entrypoints.tsv'), $false, $utf8)
try {
    foreach ($line in [IO.File]::ReadLines($methodIndex)) {
        $row = $line.Split("`t")
        if ($row[0] -eq 'rva' -or ($row[3] -eq 'Assembly-CSharp' -and $candidateTypes -contains ($row[4].Split('+')[0]))) { $writer.WriteLine($line) }
    }
} finally { $writer.Dispose() }

# IL2CPP's literal table stores (byte length, byte offset) pairs into UTF-8 data.
$metadata = [IO.File]::ReadAllBytes((Join-Path $OutputRoot 'input\global-metadata.dat'))
if ([BitConverter]::ToUInt32($metadata, 0) -ne [Convert]::ToUInt32('FAB11BAF', 16)) { throw 'Invalid IL2CPP metadata magic.' }
$literalOffset = [BitConverter]::ToInt32($metadata, 8)
$literalBytes = [BitConverter]::ToInt32($metadata, 12)
$dataOffset = [BitConverter]::ToInt32($metadata, 16)
$dataBytes = [BitConverter]::ToInt32($metadata, 20)
if ($literalOffset -lt 0 -or $literalBytes % 8 -ne 0 -or $literalOffset + $literalBytes -gt $metadata.Length -or $dataOffset + $dataBytes -gt $metadata.Length) { throw 'Invalid IL2CPP string table bounds.' }
$writer = New-Object IO.StreamWriter((Join-Path $indexRoot 'string-literals.jsonl'), $false, $utf8)
try {
    for ($i = 0; $i -lt $literalBytes / 8; $i++) {
        $length = [BitConverter]::ToUInt32($metadata, $literalOffset + 8 * $i)
        $offset = [BitConverter]::ToUInt32($metadata, $literalOffset + 8 * $i + 4)
        if ([long]$offset + $length -gt $dataBytes) { throw "Invalid literal $i bounds." }
        $value = [Text.Encoding]::UTF8.GetString($metadata, $dataOffset + $offset, $length)
        $writer.WriteLine(([ordered]@{ index = $i; value = $value } | ConvertTo-Json -Compress))
    }
} finally { $writer.Dispose() }
Write-Output "Indexed $((($counts.Values | Measure-Object -Sum).Sum)) addressed methods across $($counts.Count) assemblies and $($literalBytes / 8) string literals."

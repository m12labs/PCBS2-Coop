[CmdletBinding()]
param(
    [string]$GamePath,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$auditWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$auditOutput = Join-Path $auditWorkspace 'artiffacts/audit'
$auditDocRoot = Join-Path $auditWorkspace 'docs/audit'
$auditSources = Join-Path $auditWorkspace 'artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp'
$auditDocs = @(Get-ChildItem $auditDocRoot -Filter '*.md' -File) + @(
    Get-Item (Join-Path $auditWorkspace 'docs/sync-progress.md'), (Join-Path $auditWorkspace 'plan.md')
)
$auditGeneratedPaths = @(
    (Join-Path $auditOutput 'source-inventory.csv'),
    (Join-Path $auditOutput 'declared-state-inventory.csv')
)
if (-not $ValidateOnly -and -not (Test-Path -LiteralPath $auditSources -PathType Container)) {
    throw "Missing structural game export: $auditSources. Run the decompile workflow first."
}
$auditCitations = @{}
$auditLinkErrors = [System.Collections.Generic.List[string]]::new()
foreach ($auditDoc in $auditDocs) {
    $auditText = [IO.File]::ReadAllText($auditDoc.FullName)
    foreach ($auditMatch in [regex]::Matches($auditText, '\]\(([^)]+)\)')) {
        $auditTarget = $auditMatch.Groups[1].Value
        if ($auditTarget -match '^(https?://|#)') { continue }
        $auditParts = $auditTarget.Split('#', 2)
        $auditResolved = [IO.Path]::GetFullPath((Join-Path $auditDoc.DirectoryName ([Uri]::UnescapeDataString($auditParts[0]))))
        if (!(Test-Path -LiteralPath $auditResolved)) {
            # These two local reports are created later in a generation run.
            if ($ValidateOnly -or $auditGeneratedPaths -notcontains $auditResolved) {
                $auditLinkErrors.Add($auditDoc.Name + ': missing ' + $auditTarget)
            }
            continue
        }
        if ($auditParts.Count -gt 1 -and $auditParts[1] -match '^L([0-9]+)$') {
            $auditLineNumber = [int]$Matches[1]
            $auditLineCount = [IO.File]::ReadAllLines($auditResolved).Length
            if ($auditLineNumber -lt 1 -or $auditLineNumber -gt $auditLineCount) { $auditLinkErrors.Add($auditDoc.Name + ': line out of bounds ' + $auditTarget) }
        }
        $auditKey = $auditResolved.ToLowerInvariant()
        if (!$auditCitations.ContainsKey($auditKey)) { $auditCitations[$auditKey] = [System.Collections.Generic.HashSet[string]]::new() }
        [void]$auditCitations[$auditKey].Add($auditDoc.Name)
    }
}
if ($ValidateOnly) {
    [pscustomobject]@{Documents=$auditDocs.Count; BrokenLinks=$auditLinkErrors.Count; Errors=@($auditLinkErrors)} | ConvertTo-Json -Depth 5
    if ($auditLinkErrors.Count -gt 0) { throw 'Audit documentation contains broken local links.' }
    return
}
$auditInventory = [System.Collections.Generic.List[object]]::new()
$auditFields = [System.Collections.Generic.List[object]]::new()
$auditSourceCounter = 0
foreach ($auditFile in (Get-ChildItem $auditSources -Recurse -File -Filter '*.cs' | Sort-Object FullName)) {
    $auditSourceCounter++
    $auditSourceId = 'SRC-{0:D4}' -f $auditSourceCounter
    $auditLines = [IO.File]::ReadAllLines($auditFile.FullName)
    $auditRaw = [string]::Join("`n", $auditLines)
    $auditRelative = $auditFile.FullName.Substring($auditWorkspace.Length + 1).Replace('\','/')
    $auditSourceRelative = $auditFile.FullName.Substring($auditSources.Length + 1).Replace('\','/')
    $auditNamespace = if ($auditRaw -match '(?m)^namespace (.+);') { $Matches[1] } else { '(global)' }
    $auditKind = if ($auditRaw -match '(?m)^public .*?\b(class|struct|enum|interface|delegate)\b') { $Matches[1] } elseif ($auditRaw -match '(?m)^(?:internal|private).*?\b(class|struct|enum|interface|delegate)\b') { $Matches[1] } else { 'other' }
    $auditFamily = if ($auditSourceRelative -match '^(Rewired|I2|UnityStandardAssets|JoshH|cakeslice|ChromaSDK|UnityEngine|UnityCursorControl|Microsoft|System)/') { 'Vendor/UI/engine integration; reachability and side effects unreviewed' } elseif ($auditSourceRelative -match '^PCBS2/Onboarding/') { 'Onboarding transition family; individual native paths unreviewed' } elseif ($auditFile.BaseName -match 'Objective$') { 'Objective predicate/result family; see career report' } elseif ($auditFile.BaseName -match '^PartDesc|^PartsDatabase|Comparer$|Enum$|^Compatibility$') { 'Catalog/configuration family; mutable state must be distinguished' } elseif ($auditFile.BaseName -match 'Debug|Demo|Example') { 'Debug/demo reachability unverified; no gameplay exemption assumed' } elseif ($auditFile.BaseName -match 'Navigation|Scroller|ScrollRect|Tooltip|ToolTip|Localiz|Dropdown|DropDown|Button|UIGradient') { 'UI/input/presentation family; local candidate subject to side effects' } else { 'Game-state/integration candidate; consult reports or follow up' }
    $auditKey = $auditFile.FullName.ToLowerInvariant()
    $auditReports = if ($auditCitations.ContainsKey($auditKey)) { [string]::Join(';', ($auditCitations[$auditKey] | Sort-Object)) } else { '' }
    $auditReview = if ($auditReports) { 'Cited in documentation; review depth limited to explicit claims' } else { 'Automated declaration inventory only; individual manual review pending' }
    $auditFieldCount = 0
    $auditMethodCount = 0
    for ($auditIndex = 0; $auditIndex -lt $auditLines.Length; $auditIndex++) {
        $auditLine = $auditLines[$auditIndex]
        if ($auditLine -match '^(\t+)(public|private|protected|internal)\s+(.+); //Field offset: (.+)$') {
            $auditDepth = $Matches[1].Length
            $auditAccess = $Matches[2]
            $auditMember = $Matches[3]
            $auditOffset = ($Matches[4] -split ' \|\|', 2)[0]
            $auditBeforeValue = ($auditMember -split ' = ', 2)[0]
            $auditNameMatch = [regex]::Match($auditBeforeValue, '([^\s]+)$')
            $auditMemberName = $auditNameMatch.Value
            $auditType = $auditBeforeValue.Substring(0, $auditNameMatch.Index).Trim()
            $auditStorage = if ($auditType -match '\bconst\b') { 'constant/configuration' } elseif ($auditType -match '\bstatic\b') { 'static/shared-in-process' } else { 'instance' }
            $auditFields.Add([pscustomobject]@{source_id=$auditSourceId;source_line=($auditIndex+1);indent_depth=$auditDepth;access=$auditAccess;field_name=$auditMemberName;declared_type_and_modifiers=$auditType;storage_hint=$auditStorage;declared_offset=$auditOffset})
            $auditFieldCount++
        }
        if ($auditLine -match '^\t+(public|private|protected|internal).*\)\s*\{\s*\}') { $auditMethodCount++ }
    }
    $auditIsil = Join-Path $auditWorkspace ('artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/' + [IO.Path]::ChangeExtension($auditSourceRelative, 'txt'))
    $auditInventory.Add([pscustomobject]@{source_id=$auditSourceId;source_path=$auditRelative;namespace=$auditNamespace;file_type_name=$auditFile.BaseName;kind_hint=$auditKind;field_declarations_including_nested=$auditFieldCount;method_signatures_including_nested=$auditMethodCount;main_type_isil_file_present=(Test-Path -LiteralPath $auditIsil);triage_hint=$auditFamily;review_marker=$auditReview;report_references=$auditReports})
}
New-Item -ItemType Directory -Path $auditOutput -Force | Out-Null
$auditInventory | Export-Csv -LiteralPath (Join-Path $auditOutput 'source-inventory.csv') -NoTypeInformation -Encoding UTF8
$auditFields | Export-Csv -LiteralPath (Join-Path $auditOutput 'declared-state-inventory.csv') -NoTypeInformation -Encoding UTF8
$auditManifest = Get-Content (Join-Path $auditWorkspace 'artiffacts/decompiled/manifest.json') -Raw | ConvertFrom-Json
if (-not $GamePath) { $GamePath = $auditManifest.gamePath }
$auditBinaryPath = Join-Path $GamePath 'GameAssembly.dll'
$auditMetadataPath = Join-Path $GamePath 'PCBS2_Data/il2cpp_data/Metadata/global-metadata.dat'
$auditBinary = if (Test-Path -LiteralPath $auditBinaryPath) { (Get-FileHash -LiteralPath $auditBinaryPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
$auditMetadata = if (Test-Path -LiteralPath $auditMetadataPath) { (Get-FileHash -LiteralPath $auditMetadataPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
$auditResult = [pscustomobject]@{
    game_assembly_files = $auditInventory.Count
    field_declarations_including_nested = $auditFields.Count
    top_level_field_declarations = @($auditFields | Where-Object indent_depth -eq 1).Count
    cited_game_files = @($auditInventory | Where-Object report_references).Count
    declaration_only_files = @($auditInventory | Where-Object {!$_.report_references}).Count
    available_assembly_directories = @(Get-ChildItem (Join-Path $auditWorkspace 'artiffacts/decompiled/metadata/csharp/DiffableCs') -Directory).Count
    native_coverage_summary_present = Test-Path (Join-Path $auditWorkspace 'artiffacts/decompiled/coverage.json')
    live_binary_matches_preserved = if ($auditBinary) { $auditBinary -eq ($auditManifest.preservedInputs | Where-Object path -eq 'input/GameAssembly.dll').sha256 } else { $null }
    live_metadata_matches_preserved = if ($auditMetadata) { $auditMetadata -eq ($auditManifest.preservedInputs | Where-Object path -eq 'input/global-metadata.dat').sha256 } else { $null }
    factor_rows_by_report = @($auditDocs | ForEach-Object {
        $auditDocText = [IO.File]::ReadAllText($_.FullName)
        [pscustomobject]@{
            report = $_.Name
            factor_rows = [regex]::Matches($auditDocText, '(?m)^\| (HW|SW|CP|PW|SS)-[0-9]{3} \|').Count
        }
    })
    broken_links = $auditLinkErrors.Count
    link_errors = @($auditLinkErrors)
}
$auditResult | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $auditOutput 'inventory-summary.json') -Encoding UTF8
$auditResult | ConvertTo-Json -Depth 5
if ($auditLinkErrors.Count -gt 0) { throw 'Audit documentation contains broken local links.' }

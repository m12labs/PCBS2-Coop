param(
    [string]$GamePath,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\..\artiffacts\decompiled'),
    [string]$Cpp2ILPath,
    [string]$GhidraHome,
    [switch]$SkipMetadata,
    [switch]$SkipNative
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\scripts\LocalPaths.ps1')
$GamePath = Resolve-LocalPath $GamePath -EnvName 'PCBS2_GAME_DIR' -PropName 'GameDir' -What 'game folder'
$GhidraHome = Resolve-LocalPath $GhidraHome -EnvName 'GHIDRA_HOME' -PropName 'GhidraHome' -What 'Ghidra folder' -Optional:$SkipNative
$Cpp2ILPath = Resolve-LocalPath $Cpp2ILPath -EnvName 'CPP2IL_PATH' -PropName 'Cpp2ILPath' -What 'Cpp2IL path' -Optional
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$binary = Join-Path $GamePath 'GameAssembly.dll'
$metadata = Join-Path $GamePath 'PCBS2_Data\il2cpp_data\Metadata\global-metadata.dat'
foreach ($path in @($binary, $metadata)) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing game input: $path" } }
$tool = Join-Path $OutputRoot 'tools\Cpp2IL\Cpp2IL.exe'
$utility = Join-Path $GhidraHome 'Ghidra\Framework\Utility\lib\Utility.jar'
if (-not $SkipNative -and -not (Test-Path -LiteralPath $utility)) { throw "Missing Ghidra installation: $GhidraHome" }
foreach ($directory in @('input', 'logs', 'tools\Cpp2IL', 'tools\ghidra-home', 'tools\temp', 'ghidra')) {
    New-Item -ItemType Directory -Path (Join-Path $OutputRoot $directory) -Force | Out-Null
}
if ($Cpp2ILPath) { Copy-Item -LiteralPath $Cpp2ILPath -Destination $tool -Force }
if (-not (Test-Path -LiteralPath $tool)) { throw 'Pass -Cpp2ILPath pointing to Cpp2IL pre-release.21 or a compatible release.' }
# Prevent mixing native output from different game updates in a resumed export.
$savedBinary = Join-Path $OutputRoot 'input\GameAssembly.dll'
if ((Test-Path -LiteralPath (Join-Path $OutputRoot 'ghidra\PCBS2.gpr')) -and
    ((Get-FileHash -LiteralPath $binary).Hash -ne (Get-FileHash -LiteralPath $savedBinary).Hash -or
     (Get-FileHash -LiteralPath $metadata).Hash -ne (Get-FileHash -LiteralPath (Join-Path $OutputRoot 'input\global-metadata.dat')).Hash)) {
    throw 'Game build changed. Use a fresh -OutputRoot for this build.'
}
Copy-Item -LiteralPath $binary, $metadata -Destination (Join-Path $OutputRoot 'input') -Force
foreach ($name in @('ScriptingAssemblies.json', 'RuntimeInitializeOnLoads.json', 'boot.config', 'app.info')) {
    Copy-Item -LiteralPath (Join-Path $GamePath "PCBS2_Data\$name") -Destination (Join-Path $OutputRoot 'input') -Force
}
if (-not $SkipMetadata) {
    foreach ($format in @(
        @{ Id = 'dll_throw_null'; Directory = 'metadata\assemblies'; Log = 'cpp2il-assemblies.log'; Processor = $true },
        @{ Id = 'diffable-cs'; Directory = 'metadata\csharp'; Log = 'cpp2il-csharp.log'; Processor = $true },
        @{ Id = 'isil'; Directory = 'native\isil'; Log = 'cpp2il-isil.log'; Processor = $false }
    )) {
        Write-Host "Exporting $($format.Id)..."
        $toolArgs = @('--game-path', $GamePath, '--exe-name', 'PCBS2', '--output-as', $format.Id, '--output-to', (Join-Path $OutputRoot $format.Directory))
        if ($format.Processor) { $toolArgs += @('--use-processor', 'attributeinjector') }
        & $tool @toolArgs *> (Join-Path $OutputRoot "logs\$($format.Log)")
        if ($LASTEXITCODE -ne 0) { throw "Cpp2IL failed: $($format.Id). See logs." }
    }
}
& (Join-Path $PSScriptRoot 'Build-Index.ps1') -OutputRoot $OutputRoot
& (Join-Path $PSScriptRoot 'Write-Manifest.ps1') -GamePath $GamePath -OutputRoot $OutputRoot -GhidraHome $GhidraHome
if (-not $SkipNative) {
    $javaArgs = @(
        '-Xmx6G', '-XX:ParallelGCThreads=4', '-XX:CICompilerCount=4',
        '-Djava.system.class.loader=ghidra.GhidraClassLoader', '-Dfile.encoding=UTF8', '-Duser.language=en', '-Duser.country=US',
        "-Duser.home=$OutputRoot\tools\ghidra-home", "-Dapplication.settingsdir=$OutputRoot\tools\ghidra-home\settings",
        "-Dapplication.cachedir=$OutputRoot\tools\ghidra-home\cache", "-Dapplication.tempdir=$OutputRoot\tools\temp",
        "-Djava.io.tmpdir=$OutputRoot\tools\temp", '-cp', $utility,
        'ghidra.Ghidra', 'ghidra.app.util.headless.AnalyzeHeadless', (Join-Path $OutputRoot 'ghidra'), 'PCBS2'
    )
    if (Test-Path -LiteralPath (Join-Path $OutputRoot 'ghidra\PCBS2.gpr')) { $javaArgs += @('-process', 'GameAssembly.dll') }
    else { $javaArgs += @('-import', $savedBinary) }
    $javaArgs += @('-scriptPath', $PSScriptRoot, '-preScript', 'ImportIl2Cpp.java', (Join-Path $OutputRoot 'indexes\methods.tsv'),
        '-postScript', 'ExportNative.java', (Join-Path $OutputRoot 'native\pseudocode'), '-max-cpu', '8',
        '-log', (Join-Path $OutputRoot 'logs\ghidra-analysis.log'), '-scriptlog', (Join-Path $OutputRoot 'logs\ghidra-scripts.log'))
    Write-Host 'Analyzing and exporting all native functions (see logs\ghidra-console.log)...'
    $summary = Join-Path $OutputRoot 'native\pseudocode\summary.json'
    if (Test-Path -LiteralPath $summary) { Remove-Item -LiteralPath $summary }
    & java @javaArgs *> (Join-Path $OutputRoot 'logs\ghidra-console.log')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $OutputRoot 'native\pseudocode\summary.json'))) { throw 'Native export did not finish. See Ghidra logs.' }
}
if (Test-Path -LiteralPath (Join-Path $OutputRoot 'native\pseudocode\summary.json')) {
    & (Join-Path $PSScriptRoot 'Validate-Output.ps1') -OutputRoot $OutputRoot
}
Write-Host "Decompilation outputs: $OutputRoot"

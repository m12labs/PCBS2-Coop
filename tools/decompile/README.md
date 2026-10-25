# PCBS2 decompilation

For the process from an untouched IL2CPP installation, the reasons behind these steps, and this run's findings, read [IL2CPP decompilation from scratch](../../il2cpp-decompile.md). This page covers running the existing PCBS2 scripts.

The local analysis output lives at `artiffacts/decompiled/` (spelling intentional). The entire directory is ignored by Git. The scripts here contain the workflow; game binaries and recovered game code stay in the ignored directory.

Run from the repository root with PowerShell, .NET 8 or later, Java 21, [Cpp2IL](https://github.com/SamboyCoding/Cpp2IL), and [Ghidra](https://github.com/NationalSecurityAgency/ghidra):

```powershell
.\tools\decompile\Invoke-Decompile.ps1
```

The initial run uses the Cpp2IL executable already copied into the output directory. On another machine, provide `-Cpp2ILPath`, `-GamePath`, and `-GhidraHome`. The validated Cpp2IL version is `2022.1.0-pre-release.21`, commit `58fc404ac503f4e512055cafc48c03088fc6e224`; Ghidra is `12.0.1`.

Use `-SkipMetadata` to reuse the structural and instruction exports while rerunning native analysis. Use `-SkipNative` to regenerate those exports without Ghidra. A changed game binary or metadata requires a fresh ignored `-OutputRoot`; the script rejects mixing builds in an existing Ghidra project.

| Output | Contents |
| --- | --- |
| `input/` | Preserved binary, IL2CPP metadata, build identifiers, and runtime initialization descriptors |
| `manifest.json` | Build identity, tool versions, input hashes, and native dependency hashes |
| `metadata/assemblies/` | Reconstructed .NET metadata assemblies, with deliberately nonfunctional method bodies |
| `metadata/csharp/DiffableCs/` | C# structural views: types, fields, offsets, signatures, tokens, and method addresses |
| `native/isil/IsilDump/` | Native disassembly and Cpp2IL intermediate instructions, grouped by assembly and type |
| `native/pseudocode/` | Ghidra C-like function bodies, function status index, direct-call edges, and export summary |
| `ghidra/PCBS2.gpr` | Persistent native analysis project |
| `indexes/methods.tsv` | Native addresses linked to assembly, declaring type, signature, metadata token, and structural C# file |
| `indexes/multiplayer-entrypoints.tsv` | Candidate methods from player, world, parts, inventory, persistence, and career types for later investigation |
| `indexes/string-literals.jsonl` | Strings recovered from IL2CPP metadata |
| `indexes/game-files.csv` | Shipped code and asset file inventory |
| `coverage.json` | Counts and native pseudocode coverage, generated after native export |
| `indexes/methods-without-pseudocode.tsv` | Method aliases lacking a successful native function export |
| `logs/` | Tool output and analysis diagnostics |

The C# structural files have placeholder bodies. Read `native/pseudocode/` and `native/isil/` for implementations. Native pseudocode is a reconstruction: original local variable names, source formatting, and some types cannot be recovered. Stripped code is unavailable. The workflow covers retained IL2CPP game code; it inventories engine/vendor binaries and serialized assets rather than recreating a Unity editor project.

For a native address, add the RVA in `methods.tsv` to the image base `0x180000000`. Multiple methods can share an implementation. The method index preserves those aliases even when Ghidra uses one primary name.

Initial multiplayer investigation points in `Assembly-CSharp` include `SimplePlayerMovement`, `WorkshopController`, `PlayerStatus`, `Inventory`, `ComponentPC`, `PartInstance`, `BenchSlot`, `Slot`, `Cable`, `SaveLoadSystem`, `SaveGame`, and `ComputerSave`. These are navigation points for further investigation; runtime hook suitability and state synchronization behavior still need verification.

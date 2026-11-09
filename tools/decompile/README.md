# PCBS2 decompilation

For the process from an untouched IL2CPP installation, the reasons behind these steps, and this run's findings, read [IL2CPP decompilation from scratch](../../il2cpp-decompile.md). This page covers running the existing PCBS2 scripts.

The local analysis output lives at `artiffacts/decompiled/` (spelling intentional). The entire directory is ignored by Git. The scripts here contain the workflow; game binaries and recovered game code stay in the ignored directory.

Run from the repository root with PowerShell, .NET 8 or later, Java 21, [Cpp2IL](https://github.com/SamboyCoding/Cpp2IL), and [Ghidra](https://github.com/NationalSecurityAgency/ghidra):

```powershell
.\tools\decompile\Invoke-Decompile.ps1
```

The initial run uses the Cpp2IL executable already copied into the output directory. On another machine, provide `-Cpp2ILPath`, `-GamePath`, and `-GhidraHome`, or set them once in the git-ignored `Directory.Build.local.props` (`Cpp2ILPath`, `GameDir`, `GhidraHome`) or as `CPP2IL_PATH`, `PCBS2_GAME_DIR`, `GHIDRA_HOME`. The validated Cpp2IL version is `2022.1.0-pre-release.21`, commit `58fc404ac503f4e512055cafc48c03088fc6e224`; Ghidra is `12.0.1`.

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

## Tracing a method

Two read-only Python helpers read the preserved inputs when the ISIL listing is cut short or a call goes through a vtable slot or a delegate. They need Python 3.9 or later and `pip install capstone pefile`; checked with Capstone 5.0.3 and pefile 2024.8.26. Both default to `artiffacts/decompiled/` beside this repository and accept `--decompiled <folder>` for another output. Check first that the input hashes match `manifest.json`: slots, offsets and cells belong to one game build.

```powershell
python -B tools/decompile/trace_rva.py 0xE4CBA0              # disassemble a method by RVA or VA, call targets named from methods.tsv
python -B tools/decompile/trace_rva.py 0x1800029D0 0x90      # a helper without an index row needs a byte length
python -B tools/decompile/metadata_slots.py cell 0x1845EF918 # the method a delegate or listener cell points at
python -B tools/decompile/metadata_slots.py slots Job@0x2000177 37 IEmail:15  # vtable entries; pin a type by its DiffableCs token
```

To read a path, start with the type's `metadata/csharp/DiffableCs/` file for fields, offsets, enums and tokens. Then find the method's RVA in `methods.tsv` and read its `native/isil/IsilDump/` listing, using `trace_rva.py` for anything the listing omits.
- **Virtual call.** A call through `[klass+off]` uses slot `(off - vtable base) / 16`. The base is `0x138` in this build. Decode that slot on the receiver's real type, because overrides replace entries.
- **Interface call.** An interface call adds the interface's offset in the receiver type to the slot.
- **Delegates, listeners and lambdas.** These load a metadata cell at the next instruction's address plus the RIP-relative displacement. `metadata_slots.py cell` names the target.
- **Coroutines.** These run in the nested `<Method>d__N.MoveNext`.

Before hooking a method, search `methods.tsv` for every row with its exact RVA. More than one row means several methods share the native code, so a hook on it would fire for all of them.

## Investigation points

Initial multiplayer investigation points in `Assembly-CSharp` include `SimplePlayerMovement`, `WorkshopController`, `PlayerStatus`, `Inventory`, `ComponentPC`, `PartInstance`, `BenchSlot`, `Slot`, `Cable`, `SaveLoadSystem`, `SaveGame`, and `ComputerSave`. These are navigation points for further investigation; runtime hook suitability and state synchronization behavior still need verification.

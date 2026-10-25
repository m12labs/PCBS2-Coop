# Coverage and evidence ledger

October 25, 2026. This ledger distinguishes a broad synchronization audit from exhaustive proof of every game path. See [factor reports](README.md) and [remaining verification](open-questions-and-runtime-checks.md).

## Build identity

The installed native binary and metadata were hashed during this audit and **both match** the preserved [manifest](../../artiffacts/decompiled/manifest.json). Addresses and native observations therefore refer to this input pair:

| Item | Identity |
| --- | --- |
| Game installation | `<GameDir>` |
| Platform/backend | Windows x64, IL2CPP |
| Unity / metadata layout | `6000.2.6f2` / `31.1`, as recorded by the decompile manifest |
| Build GUID | `92d05e5a20c14d41aacbb9c6a804d5a3` |
| GameAssembly.dll SHA-256 | `646148bbbece7076064891df335668a770482810d91ba0fc7d3e5a4be5301aaf` |
| global-metadata.dat SHA-256 | `3104c9aac111e3a88a2df72f0a7420c8118391d2819daffcee0c58525adc10f3` |
| Cpp2IL | `2022.1.0-pre-release.21`, commit `58fc404ac503f4e512055cafc48c03088fc6e224` |
| Available structural assembly directories | 88 |
| Main game structural C# files | 1,598, including nested namespaces and vendor/example files within Assembly-CSharp |
| Main game files cited in the original audit reports | 303; citation establishes only the depth explicitly described by that report |
| Files with automatic inventory only in the original audit | 1,295; individual manual/native review remains open |

The GUID/version values come from the preserved manifest; the two hashes were independently compared against the installed files. Nothing here claims that a launcher version, DLC entitlement set or bundled asset catalog was validated across two machines.

## What was covered

All main-assembly structural files were enumerated and machine-scanned for declared fields and method signatures. Domain investigators read selected declarations and native excerpts; the reports identify those sources and qualify their depth. Whole-file citation does not imply every method was audited.

The source scan found **32,482 field declaration lines**, including **14,868 at the top type's indentation level**. This includes constants, UI object references, nested data records, compiler closures and coroutine temporaries. These numbers are not counts of network fields, mutable gameplay facts or required packets. Enum members/property accessors and runtime-created object graphs are not fully represented by that field-line pattern.

| Inventory | How to use it |
| --- | --- |
| [source-inventory.csv](../../artiffacts/audit/source-inventory.csv) | One row for every recovered Assembly-CSharp C# file: path, namespace, type-kind hint, field/method counts, main-type ISIL availability, triage hint, documentation references and explicit review marker. |
| [declared-state-inventory.csv](../../artiffacts/audit/declared-state-inventory.csv) | Declaration-level field facts, including nested fields, with `source_id`, line, indentation depth, access, name, type/modifiers, storage hint and offset. Join `source_id` to the source index for its path. This is an audit search aid, not a serialization contract. |

These generated inventories live locally in ignored `artiffacts/audit/`, alongside their summary. Recreate them with `./tools/audit/Build-Inventory.ps1`; see the [generator instructions](../../tools/audit/README.md). Handwritten reports remain in Git. A later run includes citations from the current tracker and plan, so citation counts can differ from the original audit snapshot above without changing the underlying source coverage claim.

`SRC-xxxx` IDs are ordered source-index references for this build snapshot, not runtime or network identity. Field rows are **declaration-only evidence** even when the containing file is cited by a report; only the explicit report findings carry a deeper review claim. No native bodies or static initializer byte blobs are copied into the inventory.

The source index marks files cited by reports separately from files that received only an automated declaration inventory. Triage hints are heuristic routing aids. A namespace or name suggesting vendor code, UI, an enum or a demo does **not** prove that it is unreachable, immutable or safe to ignore. Follow up the actual mutation/callback chain before excluding it. The field inventory deliberately preserves these candidates rather than discarding them.

The inventory covers Assembly-CSharp, not every field in all 88 framework/vendor assemblies. Relevant external boundaries were noted: radio/audio libraries, Unity rendering/input/animation, Rewired, platform services, file I/O and external PC sharing. Their full internal implementations are not multiplayer world state. If a native call crosses one of those boundaries, its application-level side effect still needs investigation.

## Declaration versus native completeness

Structural C# exports contain placeholder bodies. A saved field proves a representation exists; it does not prove when saving captures that field, which native method owns it, how a partially completed operation resumes or which setter is safe to call on a replica.

The [method index](../../artiffacts/decompiled/indexes/methods.tsv) maps addresses/signatures and preserves aliases. [functions.tsv](../../artiffacts/decompiled/native/pseudocode/functions.tsv) is available, but **`artiffacts/decompiled/coverage.json` was absent** at audit time. The presence of a Ghidra project, an index or some `.c` files is not evidence of complete main-game pseudocode recovery. This audit uses specifically identified ISIL excerpts without treating the export as complete.

Metadata fields and method names also do not prove that legacy, tutorial, debug or example code is reachable in the current shipped scenes. Examples: GameMode declares CAREER/FREEBUILD/NEWGAMEPLUS/MAINMENU, while names such as Endless or Binning appear elsewhere; that alone does not establish additional selectable modes. The same caution applies to prototype player controllers and unused animations.

## Running-game evidence

No new running-game tests were performed. The existing log at `<GameDir>/BepInEx/LogOutput.log` records a historical host menu/loopback/capsule run. The existing `PCBS2Coop-diag/scene-Stage_02_Workshop.txt` records a root hierarchy including `Player {CharacterController, Walk, ActivateTablet}`, Tablet and vehicle animator roots. These were read as historical artifacts; no log date or screenshot is promoted to current multiplayer acceptance.

The reports do not claim identical monitor pixels, valid hook points, working two-player construction, safe shared saves, all animation callbacks, all DLC variants or scene-specific reachability. [The verification backlog](open-questions-and-runtime-checks.md) makes those gaps concrete.

## Missing or incomplete evidence

`docs/audit/` did not exist and was created. The game, preserved binary/metadata, C# declarations, ISIL and indexes were present. No reconstructed Unity scene/prefab/Animator asset project was found under the inspected `artiffacts/` workspace. The shipped Addressables directory exists (`PCBS2_Data/StreamingAssets/aa` includes `catalog.bin`, `catalog.hash`, `settings.json`), but those shipped files are not an extracted asset project.

An asset pass needs to record exact scene/prefab identities, Animator controllers/layers/states/transitions/clip events, material and screen-render bindings, audio/video/GIF resources, part/connector variant topology and DLC-specific content. This audit neither created an extraction pipeline nor guessed missing object names. A future export can live in an ignored asset directory under `artiffacts/`; its precise path is not a prerequisite for reading these reports.

## Documentation validation

Final validation checks relative file targets, source line bounds, unique/contiguous factor IDs within each report, CSV row counts/source coverage and the audit-only change scope. It does not run the solution build because this repository's build can deploy to the game. Network/game tests were not necessary to verify documentation changes and were not run. The tracker records this audit pass separately from future native asset and live acceptance passes.

Original audit validation completed: **9 Markdown documents including the root tracker, 1,008 Markdown links, 318 unique/in-order factor rows, 1,598 source-index rows and 32,482 field rows**. No broken local targets, out-of-bounds source anchors, missing linked headings, table-column mismatches, duplicate/gapped factor IDs or orphan field-source IDs remained. The source-index row count matches the recovered main-assembly file count. Counts describe that audit snapshot's documentation integrity, not gameplay correctness. Use the generator's current validation result after documentation changes.

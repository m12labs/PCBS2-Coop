# Local audit inventories

Generate the source/field indexes from the local structural game export:

```powershell
.\tools\audit\Build-Inventory.ps1
```

The script locates the repository relative to itself. It reads `artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/`, the decompile manifest, audit Markdown, `plan.md` and `sync-progress.md`. It writes `source-inventory.csv`, `declared-state-inventory.csv` and `inventory-summary.json` under **`artiffacts/audit/`**, which is ignored by Git. Generated indexes stay local; handwritten audit reports and this generator are versioned.

The manifest supplies the game installation used for the optional binary/metadata hash comparison. Override it with `-GamePath '<game folder>'`. If the installation is unavailable, the summary records the live hash comparison as `null`; the structural inventory still runs. Missing decompile inputs produce an error. See the [decompile workflow](../decompile/README.md) to prepare them.

After generating the inventories, check local documentation file targets and source line bounds without regenerating:

```powershell
.\tools\audit\Build-Inventory.ps1 -ValidateOnly
```

Validation includes ignored local game evidence, so a source-only checkout needs its matching decompile and generated inventories first. Broken links return a failing result. These checks establish documentation integrity, not game behavior or native method coverage; see the [evidence ledger](../../docs/audit/coverage-and-evidence.md).

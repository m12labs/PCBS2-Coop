# Phase 10 focused audit: custom water cooling (and the LED strip finding for phase 11)

October 29, 2026. Pulled forward at the owner's request, after phase 5. Phases 6–9, 4f and 5d stay open. The owner tests every finished feature in game later. Static decompile evidence only. Phase order is in [plan.md](../../plan.md); feature status is in [sync-progress.md](../sync-progress.md).

## Owner decisions (October 29, 2026)

- **Show the result, not the process:** when a water-cooling change is finished, the other game reloads that PC from the finished record. No pipe-routing or fill animation is replayed. This covers pipes, coolant, and water blocks, pumps and reservoirs.
- **Scope:**
  - included: blocks, pumps and reservoirs (with the VRM, heat-sink and GPU-cooler slots), pipes, and coolant fill/drain;
  - not included: the water-cooling workbench.
- **Waiting:** while it runs, the other player waits on that PC. A refused action says "The other player is building water cooling on that PC."
- **LED strips:** strips only (colours and effects stay in phase 11). The owner confirmed the LED tool can't be opened in their game; see below.

## What the game does (native, decompile)

- **Pipes.**
  - `WorkingOnComputerState.PipingTick`'s long press `<>c__DisplayClass71_0.<PipingTick>b__0` (fields `<>4__this`, `connector`) opens `ConnectPipeState`.
  - Its long press on the other fitting (`<>c__DisplayClass11_0.<HandleInput>b__1`) opens `RoutePipeState(case, camera, from, to)`, which has node editing, corner removal and undo.
  - `OnAccept` blocks the pipe mesh into the voxel grid, then `GoToPreviousState`.
  - `OnUndo` restores with `WaterPipe.Load`.
- **Pipe removal.** `<>c__DisplayClass68_1.<DisassemblyTick>b__3` (field `pipe`) calls `Object.DestroyImmediate`, then `Case.UpdateGameStatus`.
- **Coolant.**
  - `FillLoopTick` calls `Motherboard.TurnOff`, then `WaterLoopController.FillLoop(comp, coolant)`, which runs a coroutine.
  - `DisassemblyTick` calls `WaterLoopController.EmptyLoop(comp)` to drain.
  - `IsAnimating` covers both coroutines.
- **Blocks, pumps and reservoirs** install and remove through the 4e slot paths: `RESERVOIR`, `VRM`, `HEAT_SINK` and `GPU_COOLER` slots, the part types `PUMP_RESERVOIR`, `CPU_BLOCK`, `WATER_COOLED_GPU`, `*_BLOCK`, `PUMP`, `RESERVOIR` and `RAM_HEATSINK`, and parts with `m_includesPipes`/`m_onLoop`.
- **Saved form.** `ComputerSave.pipesInstalled` (`WaterPipeSave`), `m_loops` (`WaterLoopSave`, with coolant/fill), `defaultPipeId`/`defaultPipeConnectorId`/`defaultCoolantId` and `LEDStripSaveList` are all in the PC record, so a reload rebuilds them.
- **The workbench.** `WaterCoolingState` opens from the tool bench callbacks. The same callback also opens case modding and delidding. None of these specialist benches is guarded in co-op yet (the phase 3 deferral); they are not part of this task.

### LED strips (phase 11 finding)

Nothing in this build creates `LEDStripState`: no `typeof(LEDStripState)` allocation and no constructor call. `GameController.SetLEDStripType` is called only from the LED classes, and the work state has no LED code. The strip-laying tool can't be opened (the owner confirmed it in game). Strips can still be in a loaded PC record, and they survive every path that keeps the record: moves, saves, 4e/5 edits and phase 10 reloads. Nothing more is synced; phase 11 keeps colours and effects.

## Implemented contracts (version 0.16)

- **Command 9 gesture `Water`** ([`ComponentEdits.cs`](../../src/PCBS2Coop.Network/ComponentEdits.cs)):
  - whole-PC claim; the `Begin` text is a label (`pipe`, `pipe removal`, `coolant fill <id>`, `coolant drain`, `install <sku>`, `removal <sku>`);
  - `Chunk` steps (editor only, in order, up to 15 KB each and 2 MB in all) carry the finished record. Plugin side: the editor's PC record from `NativePcCodec`, Deflate-compressed;
  - the `Commit` carries `Rebuild` (length, SHA-256, power) and is refused until every chunk has arrived and the hash matches;
  - the host's `Settle` is as in 4e. A refused action on that PC names water cooling.
- **Plugin** ([`NativeComponentEdits.Water.cs`](../../src/PCBS2Coop.Plugin/NativeComponentEdits.Water.cs)):
  - Hooks: `<PipingTick>b__0`, `<DisassemblyTick>b__3`, `WaterLoopController.FillLoop(ComponentPC, PartDesc)` and `EmptyLoop(ComponentPC)`. The 4e install/remove requests for water parts and slots route here.
  - The editor is followed until no tool state is open on that PC and nothing is animating (cables, coolant, latches) for one second. A record that didn't change cancels the edit.
  - The other game uses `NativeObjectState.ReloadOnBench`: leave work on that PC, `DestroyComputer`, then the game's bench loader with the record, bound to the same PC. If the editor's PC was on, it is turned on again.
  - The assembly rebind waits until the host's settle.
  - The host capture keeps part IDs by path for water edits, because a reload makes new native instances.

## Model evidence

**513/513** model/network checks. The new water checks cover:
- the whole-PC claim and the water wording in refusals;
- chunks out of order, a commit before the whole record, a commit without a record, a wrong hash, and chunks from the other player;
- both peers holding the record at commit;
- the settle allocating the new block's ID, carrying power and dropping the record;
- a record chunk on a non-water edit, and an oversized chunk.

Release build: 0 warnings, 0 errors. Deployed and checked identical. Nothing ran in game.

## Risks and gaps

- A refused coolant fill has already run the game's `Motherboard.TurnOff` on the refusing player's game (`FillLoopTick` turns the PC off before `FillLoop`). `/sync` repairs it.
- A reload starts with `loadCables: true`, so the bench's external cables come from the record. Like the phase 3 drop gap, check it in game.
- Same-SKU parts that change path in a water edit get new IDs. That is harmless, but the IDs churn.
- The workbench and the other specialist benches are not guarded.

## Acceptance cases (owner's game test)

1. Lay a pipe between two fittings, with corners. The other game shows the finished pipe after it is accepted.
2. Undo and Esc in the pipe tool: no change is shared, and the PC is free again.
3. Remove a pipe.
4. Fill the loop with coolant, then drain it.
5. Install and remove a CPU block, a GPU block, a pump/reservoir, and a VRM or RAM heatsink.
6. While someone builds water cooling, the other player is refused on that PC with the water message, and a different PC still works.
7. Water edits on a powered PC.
8. Late join and save/Continue after water edits; `/sync`.

# Phase 9 audit: cleaning, paste, pads and specialist bench tools

November 2, 2026. Started at the owner's request after phase 8 (0.21, commit `795fde8`). Working tree clean at the start. **Audit only:** no code, no game run, no deployment. Evidence labels: **N** native ISIL, **D** decompiled declarations, **M** this mod's source. Nothing here is a running-game result; the runtime checks the audit could not settle are listed under [Runtime checks](#runtime-checks-need-the-game).

## Scope (plan.md section 9)

Dust and cleaning, paste and Q-tip application/removal, the delid procedure (clamp, heat gun, scraper, varnish, silicone, liquid metal), thermal pads and the bench stages around them. Separate the saved outcome (surface, coverage, thermal effect) from tool gestures, particles, sounds and animation callbacks. New shared facts go into saves and rejoin at once. Exit: committed condition/coverage/thermal state and required tool presentation agree; callbacks commit once; cancel, pause and disconnect during each tool family release ownership and leave a coherent result.

The phase 3 deferral of the **specialist tool benches** (case modding, water cooling, delid) also lands here.

## What the game does

### Where each tool opens

| Tool | Opened from | Works on | Evidence |
| --- | --- | --- | --- |
| Thermal paste | Work mode `THERMAL_PASTE` (mode 3): `ThermalPasteTick` checks a CPU is installed and not blocked, then pushes `ThermalPasteState`. If paste is already on, it shows a message box instead. | The CPU of the PC on an ordinary bench | **N** [ThermalPasteTick](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/WorkingOnComputerState.txt#L8287); **D** [Mode enum](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/WorkingOnComputerState.cs#L502) |
| Q-tip (paste removal) | Work mode `THERMAL_PASTE_QTIP` (mode 8): `ThermalPasteQTipTick` pushes `ThermalPasteQTipState` | Same CPU | **N** [ThermalPasteQTipTick](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/WorkingOnComputerState.txt#L9938) |
| Vacuum (cleaning) | Work mode `CLEAN` (mode 4): `VacuumTick` while the button is held | Every dusty part of that PC | **N** [VacuumTick](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/WorkingOnComputerState.txt#L13586) |
| Delid | Tool bench (`BenchToolSlot` closures `b__2`/`b__3`, `GetInteractions` `b__0`) and `DeskLogic` | A loose CPU taken from the inventory | **N** [BenchToolSlot closures](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/BenchToolSlot_NestedType___c__DisplayClass37_0.txt#L988), [DeliddingState.Tick](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/DeliddingState.txt#L2656) |
| Water-cooling bench | Tool bench / `DeskLogic` → `WaterCoolingState` | Loose GPU, RAM, motherboard or radiator from the inventory | **N** [GetInteractions b__0](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/BenchToolSlot_NestedType___c__DisplayClass31_0.txt) |
| Thermal pads | Only inside the water-cooling bench: `TickAddPads` pushes `ThermalPadState` for a GPU or RAM | That loose GPU/RAM | **N** [TickAddPads](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/WaterCoolingState.txt#L12417); no other constructor caller |
| Case modding | Tool bench closure `b__0` → `CaseModdingState` | The PC on the tool bench | **N** [b__0](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/BenchToolSlot_NestedType___c__DisplayClass37_0.txt#L14) |

The paste and Q-tip states are also opened from inside the delid procedure (paste/varnish layers on the bare die) and the water-cooling bench (GPU die, CPU block). Each workstation has a main bench and a tool bench (`WorkStation.m_BenchToolSlot`, **M** [NativeWorkstations.cs:209](../../src/PCBS2Coop.Plugin/NativeWorkstations.cs#L209)); a tool bench has one type (`BenchToolSlot.CurrentBenchType`).

### What is saved

| Fact | Saved as | Evidence |
| --- | --- | --- |
| CPU paste on/off | `ComputerSave.thermalPasteApplied` (bool) in the PC record. The live value is `CPU.pastes[LID_PASTE].m_applied`; `CPU.IsThermalPasteApplied` reads it. The surface picture (metaballs, coverage, spread) is **not** saved: `Paste.OnBeforeSerialize` is empty. | **D** [ComputerSave.cs:873](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/ComputerSave.cs#L873); **N** [CPU.IsThermalPasteApplied](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CPU.txt), [Paste.SetApplied](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Paste.txt#L4179) (called from [ThermalPasteState.Tick](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/ThermalPasteState.txt#L3554)), [Paste.ClearPaste](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Paste.txt#L3590) (from [Q-tip OnStateExit](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/ThermalPasteQTipState.txt#L5532)) |
| Paste's effect | CPU temperatures read the paste flag (`GetCPUCooledTemp`, `ValidCPUThermalTransfer`). The cooler slot's blocking display also asks `CPU.IsThermalPasteApplied` (`Slot.ShowBlocking`). | **N** [GetCPUCooledTemp](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/ComputerSave.txt#L28972), [ValidCPUThermalTransfer](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/ComputerSave.txt#L46580), [Slot.ShowBlocking](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Slot.txt#L9176) |
| Dust | Per part `PartInstance.m_filth` (float), PC totals `CurrentDust`/`TotalDust`, `m_allowDust`. The 3D dust texture is rebuilt from the amount (`ComponentDust.RefreshDust`, random pattern), so only the amount matters. The vacuum writes back through `Case.UpdateDustInSave`. | **D** [PartInstance.cs:45](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/PartInstance.cs#L45), [ComputerSave.cs:1026](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/ComputerSave.cs#L1026); **N** [Case.UpdateDustInSave](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Case.txt#L35750) |
| Dust build-up | Only `WorkshopController.DoDustAccumulation`, called only from `CareerStatus.AdvanceDay`. **Free Build has no day change, so dust never builds up there**; a Free Build PC is only dusty if its record already had dust. | **N** [DoDustAccumulation](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/WorkshopController.txt#L15257), [AdvanceDay](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CareerStatus_NestedType__AdvanceDay_d__185.txt#L1361) |
| Case dirt | `CaseDirtEnabled` + `CaseDirtData` are a case-modding paint layer (phase 11), not vacuum dust. | **D** [ComputerSave.cs:897](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/ComputerSave.cs#L897) |
| Delid | `CpuPartInstance.m_Delidded` (bool), set in `DeliddingState.Tick`. The CPU leaves the inventory at the start (`RemoveItemFromInventory`) and goes back on any exit (`OnStateDestroy` → `AddItemToInventory`). The varnish/silicone/paste layers and heat amount are procedure state only. | **D** [CpuPartInstance.cs:14](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/CpuPartInstance.cs#L14); **N** [write](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/DeliddingState.txt#L10122), [remove](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/DeliddingState.txt#L6948), [OnStateDestroy](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/DeliddingState.txt#L1304) |
| Water-bench result | A combined inventory item: `GpuPartInstance.WaterCoolBlock`, `RamPartInstance.WaterCoolBlock`, motherboard/radiator variants. Pads and GPU paste are steps on the way; `ThermalPad.applied` is not saved anywhere. | **D** [GpuPartInstance.cs:23](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/GpuPartInstance.cs#L23), [RamPartInstance.cs:8](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/RamPartInstance.cs#L8); **N** [TickAddToInventory](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/WaterCoolingState.txt#L6011), [GetPIAndRemoveFromInventory](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/WaterCoolingState.txt#L47667) |

### Free Build inventory and these items

Free Build's `AddPartToInventory` skips an item that matches one already there (`PartInstance.IsSameAs`). `IsSameAs` compares the part ID and, per type, **the CPU's `m_Delidded`**, the GPU's and RAM's water block, and motherboard/radiator fields (**N** [IsSameAs](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/PartInstance.txt#L6939); phase 3 audit row "Free Build catalog reuse"). So a delidded CPU or a water-cooled GPU becomes **its own inventory entry**, next to the stock part with the same SKU.

## What the mod does today (0.21)

- **Nothing in phase 9 is hooked.** No plugin code references the paste, Q-tip, vacuum, delid, pad, water-bench or case-modding states (**M**, search of `src/`). The only tool-bench hooks are the phase 3 pickup/drop closures (`b__1`, `b__4`, `b__5`) in [NativePcTransfers.cs:275](../../src/PCBS2Coop.Plugin/NativePcTransfers.cs#L275), which have never run in game.
- **Activity.** `NativeActivity.FromState` counts only `WorkingOnComputerStateBase` and `LookingAtMonitorState` as working on a PC ([NativeActivity.cs:58](../../src/PCBS2Coop.Plugin/NativeActivity.cs#L58)). The paste/Q-tip states sit on top of a work state, so they count. The delid, water and case-modding states don't: a player using a tool bench is "walking" to the other game.
- **The save guard** (4c) compares slots, latches, screws, orientation, case membership and internal cables ([NativeAssembly.cs:240](../../src/PCBS2Coop.Plugin/NativeAssembly.cs#L240), [Signature](../../src/PCBS2Coop.Plugin/NativeAssembly.cs#L297)). It does not look at paste, dust or delid flags, so the host's capture adopts its own game's values without a word.
- **Inventory mirroring** (4e/5b) works by SKU counts ([NativeComponentEdits.Wiring.cs:81](../../src/PCBS2Coop.Plugin/NativeComponentEdits.Wiring.cs#L81)). A replayed install from the inventory takes **the first item with that SKU** ([NativeComponentEdits.cs:458](../../src/PCBS2Coop.Plugin/NativeComponentEdits.cs#L458)).
- **Existing building blocks:** the 10b `Water` gesture (editor's finished record in chunks, other game reloads the PC), the 6 `PcRepair` reload from the host's record, `NativePcCodec` (BinaryFormatter round trip of a `ComputerSave`), and the 4e follow/settle pipeline (command 9).

## Findings and gaps

### G1. Paste and Q-tip changes stay on one game

Applying or removing paste changes only the player's own game. The host's next save keeps the host's value; a guest's change disappears at the next `/sync`, join or Continue. While it lasts, the two games disagree on CPU temperatures. Since phase 7 the host's game decides failures, so **a guest who pastes a CPU the host's game sees as bare can get a CPU overheat shutdown on both games** when the PC runs. The paste picture is local anyway (not saved), so only the on/off flag needs sharing.

### G2. Cleaning stays on one game (rare in Free Build)

Same pattern for dust: the vacuum lowers `m_filth` on one game only. In Free Build, dust only exists on PCs that came with it (R3), so this mostly matters for career later. Live dust strokes would mean many small updates; the saved result is one number per part.

### G3. Delid and water-bench results don't reach the other game

Both benches take a part out of the inventory and put a different item back (delidded CPU, water-cooled GPU/RAM/board/radiator). The other game's inventory never gets it. Mirroring by SKU can't carry the difference, and the item's full data (delid flag, water block, silicon lottery) isn't sent.

### G4. Installing a "special" item picks the wrong one on the other game

Even with G3 fixed, a 4e install from the inventory replays with the first part of that SKU. If both a stock and a delidded CPU exist, the other game may install the stock one. The PC then differs in thermals and in its saved part, and the save guard doesn't notice. Removing such a part works if both games already agree.

### G5. Tool benches are unguarded

- No claim: both players can open the same tool bench at once, each on their own game.
- No activity: the PC on a tool bench can be picked up, stored or edited by the other player while it is being modded.
- Case modding (paint, stickers, themes, dirt) is phase 11 and changes the PC on one game only, with no refusal.
- A refused or interrupted bench session can't leave a half state behind, because nothing is shared yet; that has to stay true once results are shared (G7).

### G6. Saves already carry the outcomes

`thermalPasteApplied`, `m_filth`, dust totals, `m_Delidded` and water-block items are all in the host's game save, which the guest receives at join and `/sync`. Once both games agree live, persistence and rejoin need nothing new, as long as the save guard can tell when they disagree (see task 9h).

### G7. Mid-session save, join, pause and drop

- **Host saves or a guest joins while the host is in a tool session.** The host's capture reads the PC record as it is at that moment (paste flag mid-application, CPU out of the inventory mid-delid). Whether the capture is refused, waits or takes the half state is unknown (R6). The 4c guard refuses only an unplaced component preview.
- **Pause:** co-op never freezes the world (owner, October 25). `DeliddingState` has its own `PauseAllowed`/`PauseDisallowed` windows; nothing else in these tools depends on the pause.
- **Drop mid-session:** the session is local, so a dropped guest loses it with the rest of its game. A bench claim must be released by the existing departure cleanup.

### G8. Thermal pads only exist at the water-cooling bench

`ThermalPadState` has no other caller. Phase 10's owner decision left the water-cooling bench out. Pads can only be shared if the bench comes in (question 4).

## Factor dispositions

| Factor IDs | Disposition |
| --- | --- |
| HW-018 (paste flag), HW-019 (paste surface), HW-071 (surface not saved) | 9c: share the on/off result; the surface stays each game's own default picture, as after any load. |
| HW-057 (paste/Q-tip stroke progress, too-fast state) | Local tool state; never shared. |
| HW-017 (dust stroke vs committed cleanliness) | 9d: share the per-part dust amount when the vacuum stops. Strokes stay local. |
| PW-034 (dust build-up over days) | Career (phase 15–17): only `AdvanceDay` builds dust. Not reachable in Free Build. |
| HW-020 (delid layers), HW-056, HW-076 (delid rig/heat/procedure) | 9f: share the finished CPU (delid flag) as an inventory item. Procedure state local. |
| HW-021 (pads), HW-058 (pad tool) | 9g if the owner brings the water-cooling bench in (question 4); otherwise out of scope with that bench. |
| HW-078 (water-bench substate) | Local tool state; the bench result follows question 4. |
| PW-008, PW-026, PW-029 (activity, bench/tool assignment, WIP sign) | 9b: tool-bench claim and activity. |
| PW-058–061, PW-064 (tool animation stages, callbacks, pause windows, audio, dust particles) | Phase 13 presentation, unless question 7 asks for more. Callbacks that commit (paste `SetApplied`, Q-tip `ClearPaste`, delid flag write, inventory add) are the commit points in 9c/9d/9f. |
| SW-013, SW-014, SW-043 (thermal readings) | Follow automatically once the paste/delid/dust inputs agree; phase 7 already accepts small sensor noise. |
| Case modding (HW customization factors) | Phase 11; 9b refuses or leaves it per question 5. |

## Proposed tasks (final order after the owner's answers)

- **9a.** This audit and the owner's answers.
- **9b. Tool benches:** a host-ordered claim per tool bench (one player at a time), counted as activity on the PC on that bench, released on exit and departure. Refuse case modding in co-op until phase 11 (question 5). Settle the R6 mid-session capture rule.
- **9c. Paste and Q-tip:** when the paste or Q-tip tool closes on a PC, send the new paste flag through command 9 (new gesture); the host checks it like any edit on that PC; the other game sets the CPU's paste the game's own way (`Paste.SetApplied` / `ClearPaste`) or, if that fails, reloads the PC (`PcRepair`). The other player waits on that PC as in 4e.
- **9d. Cleaning:** when the vacuum stops, send each changed part's dust amount; the other game applies it (`SetFilth`, `RefreshDust`, `UpdateDustInSave`).
- **9e. Inventory items with their own data:** send a full part record (not only a SKU) for an item a bench puts into the inventory, add it on the other game, and make 4e installs and removals name the exact kind of item (delid flag, water block) so the replay picks a matching one. Keep the catalog binding consistent with these rows.
- **9f. Delid bench result** through 9e.
- **9g. Water-cooling bench result** through 9e (question 4), including pads and GPU paste as part of the finished item.
- **9h. Save guard and tests:** add the paste flag, delid flag and water-block identity of installed parts to the build profile, so anything still unshared shows as out of sync instead of being adopted silently. Model tests; Release build; version bump (the wire and save formats change, so 0.22).
- **9i. Two-game acceptance** (owner's pass, or owned runs if asked).

## Acceptance cases (two games)

1. Each player applies paste to a CPU, then removes it with the Q-tip; the other game shows the same paste state and the cooler can be fitted on both. Run the PC: no overheat on either game.
2. Paste on a powered or running PC is refused by the game itself (cooler on); check nothing is shared by a refused try.
3. Clean a dusty PC (needs a record with dust, R3); dust amounts match on both games, and after Continue and join.
4. Delid a CPU on a tool bench; the delidded CPU appears in both inventories; install it into a PC from each side; both games show it delidded (temps, part info).
5. (If question 4 = include) Water-cool a GPU at the bench with pads and paste; the finished GPU appears in both inventories and installs on both games.
6. While one player uses a tool bench, the other is refused at that bench and can't move the PC on it; a different bench still works. Leave the bench: the claim is released.
7. Esc halfway through each tool: nothing is shared; the CPU/GPU is back in the inventory on the editor's game only as the game leaves it.
8. Host saves, a guest joins, and the guest drops while a tool session runs (each tool family); afterwards both games agree, and `/sync` repairs anything left.
9. Case modding is refused with a notice (if question 5 = refuse).
10. Regression: 4e installs of stock parts, phase 8 saves/rejoin and `/sync` still work.

## Runtime checks (need the game)

- **R1.** Who writes `ComputerSave.thermalPasteApplied` and when (no native writer was found by name): apply paste, save, read the record.
- **R2.** Whether Free Build's `RemoveItemFromInventory` at delid start really removes a stock CPU, and what the inventory shows after the delidded CPU comes back.
- **R3.** Whether any Free Build PC (default workshop PCs, catalog cases) ever has dust, i.e. whether cleaning is reachable at all.
- **R4.** How a paste-applied CPU looks after a reload (the default paste picture), to confirm in-place apply looks the same.
- **R5.** Which tool benches the default workshop has (types) and whether `DeskLogic` opens them too.
- **R6.** What the host's capture does during each tool session (refuse, wait or take the half state).
- **R7.** What Esc at each delid stage leaves: is the CPU flagged delidded before the lid goes back on?

## Questions for the owner

1. **Paste and Q-tip on the other game:** (a, recommended) when the tool closes, the other game sets the CPU's paste on or off in place, with no PC reload; (b) reload the whole PC like water cooling (a running PC reboots on the other game); (c) replay the strokes live.
2. **Cleaning:** dust never builds up in Free Build (only when a career day passes), so the vacuum only matters for PCs that already have dust. (a, recommended) share each part's dust amount when the vacuum stops; (b) refuse cleaning in co-op until career co-op. Have you ever seen dust on a Free Build PC?
3. **Delid bench:** (a, recommended) result only: when the bench closes, the delidded CPU appears in both inventories; the other player doesn't see the procedure; (b) refuse delidding in co-op for now.
4. **Water-cooling bench** (left out in phase 10; it is the only place thermal pads and GPU paste happen): (a, recommended) bring it in as result only: the finished water-cooled GPU/RAM/board/radiator appears in both inventories; (b) refuse it in co-op with a notice; (c) leave it as it is now (works on one game only).
5. **Case-modding bench** (paint, stickers, themes; phase 11): (a, recommended) refuse it in co-op with a notice until phase 11; (b) leave it as it is now.
6. **One player per tool bench:** (a, recommended) while someone uses a tool bench, the other player is refused there and can't move the PC on it; (b) allow both at once.
7. **What the other player sees during a tool session:** (a, recommended) nothing new (the capsule stands at the bench); tool animations stay in phase 13; (b) a short label over the capsule ("delidding", "applying paste").
8. **Testing:** (a, recommended, as before) I code with model tests, then you test everything in game; (b) I also run short owned two-game checks myself first.

## Owner decisions (November 2, 2026)

1. **Paste and Q-tip:** (a) when the tool closes, the other game sets the paste in place, with no PC reload.
2. **Cleaning:** (b) refused in co-op until career co-op (dust only builds up when a career day passes).
3. **Delid table:** (a) result only, with a prompt when someone else uses the table ("Someone is using the delidding table.").
4. **Water-cooling table:** (a) brought in as result only, with the same kind of prompt. Thermal pads and GPU paste are part of the finished item.
5. **Case modding:** (a) refused in co-op with a notice until phase 11.
6. **One player per tool table:** yes; the PC on a table in use can't be moved either.
7. **What the other player sees:** (b) a label under the other player's name ("Delidding", "Water cooling", "Applying thermal paste").
8. **Testing:** as before: code and model tests first, then the owner tests in game.

## Implementation (0.22)

Version **0.22** (network messages only; 0.22 reads 0.21 saves, `CoopSavePair.SameFormat`).

- **Paste (9c)**, command 9 gesture `Paste` ([ComponentEdits.cs](../../src/PCBS2Coop.Network/ComponentEdits.cs)): a whole-PC claim (`Part` = `apply` or `remove`); the commit carries `EditResult.Pastes`, every paste of the PC by its path in the case and whether it is on (up to 32). The other player waiting on that PC is told "The other player is working on the thermal paste of that PC."
  - Plugin ([NativeComponentEdits.Paste.cs](../../src/PCBS2Coop.Plugin/NativeComponentEdits.Paste.cs)): the tool opens at once (`SetCurrentState` postfix); a shared PC asks the host; a refusal puts the pastes back and closes the tool next frame. The edit is followed while a paste or Q-tip state is open on that PC (up to ten minutes), then shared once the tool has been closed for a second; no change cancels it.
  - The other game: `Paste.SetDefaultPaste` (on) or `SetApplied(false)` + `ClearPaste` (off), then `Case.UpdateGameStatus`. If that fails, the 6 PC repair reloads the PC.
- **Cleaning (9d)**: `WorkingOnComputerState.VacuumTick` is skipped in co-op with the notice "Cleaning isn't shared in co-op yet; it comes with career co-op."
- **Tool tables (9b, 9f, 9g)**, command **15** `ToolBenchWorld` ([ToolBenches.cs](../../src/PCBS2Coop.Network/ToolBenches.cs)): `Use` (one player per table, one table per player), result `Chunk`s and `Leave` (length + SHA-256). A use belongs to an actor epoch, so a departed player's table is free. Uses aren't saved or in snapshots: a guest that joined during a session adopts the use from its next chunk or leave, and the host announces its own use again after the snapshot.
  - Plugin ([NativeToolBenches.cs](../../src/PCBS2Coop.Plugin/NativeToolBenches.cs)): `DeliddingState` / `WaterCoolingState` claim their `BenchToolSlot` when they open, and close again with the prompt if refused. The session ends at `OnStateDestroy` (or when the state leaves the state stack); after 0.75 s the inventory is compared with its start, by item kind. The result is the kinds that went and the items that came (each `PartInstance` serialized whole), compressed. The other game removes a matching item and adds the new ones (`RemoveItemFromInventory`, `AddItemToInventory`). A host snapshot during the host's own session restarts the comparison from that point, so a joining guest doesn't get the same change twice.
  - `CaseModdingState` is closed again with "Case modding isn't shared in co-op yet."
  - Picking up the PC on a table someone else uses, or dropping onto it, is refused with the same prompt (local check in the transfer hooks).
- **Item variants (9e)**: `PartVariants.Of` names what `IsSameAs` compares beyond the SKU (delidded; water block; motherboard CPU/VRM covers/bench water-cooled; radiator fans). Installs carry it (`EditStart.Variant`, `EditResult.Variant`); the replay installs a matching inventory item, or a copy of it when the editor's game took it from the catalog.
- **Inventory binding**: variant items are marked like removed panels and left out of the stock binding; a 0.21 binding (no marks) still matches.
- **Label (7b)**: `RemotePlayerView.SetActivity`, from `CoopController.RemoteActivity`.
- **Save guard (9h)**: not changed. Adding paste or delid flags to the build profile would change every saved profile, and the paste flag's save writer is still unknown (R1). Unshared paste paths therefore still aren't detected by the guard.

**Model evidence:** **654/654** checks (paste begin/refusal/commit/settle, variants, tool tables: claim, prompt, one table per player, chunks/hash, departure, mid-session join). Release build 0 warnings, 0 errors; deployed and hash-checked. **Nothing ran in game.**

### Runtime gaps at 0.22

- Everything above. In particular:
  - whether `SetDefaultPaste` shows a normal paste on the other game;
  - whether the Q-tip ever changes the paste's applied flag (statically only the paste tool calls `SetApplied`). If it doesn't, a Q-tip-only session shares nothing; the paste picture then differs, while the flag that drives temperatures still agrees;
  - whether closing a just-opened tool state with `GoToPreviousState` leaves the table clean;
  - whether the game saves delidded CPUs and water-cooled items in the Free Build inventory (R2). If it doesn't, a guest that joins later lacks them;
  - which benches the default workshop has (R5).
- The table's own tool type (`DeskFunctionChoice` switching a table between delid, water and modding) is not shared; each game keeps its own. Results don't depend on it.

### Owner test (0.22)

1. Each player puts paste on a CPU (work mode, paste tool), then takes it off with the Q-tip. The other game shows the paste on, then off; while it runs, the other player is refused on that PC with the paste message and sees "Applying thermal paste" under the name.
2. Run that PC on both games: no overheat when paste is on.
3. Cleaning mode shows the refusal notice.
4. Delid a CPU at the delidding table. The other player is refused at that table with the prompt and sees "Delidding". Afterwards the delidded CPU is in both inventories; install it from each side, and both games show it delidded.
5. Water-cool a GPU at the water-cooling table (pads, paste, block). The other player gets the prompt. The finished card is in both inventories and installs on both games.
6. Case modding shows the refusal notice.
7. Esc halfway through each tool, then join, save/Continue and `/sync`: both games agree afterwards.

### Owned paste and cleaning checks in 0.33

On November 5, 2026, the host completed the Q-tip wipe and syringe application on a workbench CPU using the real tools. Both native games showed the same off and on flags after the respective edits. The guest was refused on the same PC while the host held the paste claim. Selecting Compressed Air displayed the cleaning refusal. Guest paste gestures, the visible label and cooling under load still needed checks at the end of the run. [Run record](acceptance-2026-11-04.md#host-thermal-paste-and-cleaning-in-033)

The same day's 0.34 run verified the guest's completed Q-tip and paste syringe gestures. Both native games agreed on the live and saved paste flag after each gesture. The host saw “Applying thermal paste” under the guest's avatar while the tool was open. The shared pose uses the standing position kept by work mode while the native first-person controller is below the floor. Running this same PC after applying the paste still needs a test. [Run record](acceptance-2026-11-04.md#guest-thermal-paste-player-visibility-and-a-dry-loop-heat-failure-in-034)

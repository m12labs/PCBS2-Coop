# Phase 5 focused audit: electrical cables, accessories and physical power

October 27, 2026. Phase 5 started at the owner's request, after task 4e passed the owner's game test. Task 4f (combined phase 4 acceptance) is still open. This document holds the focused audit, the owner's decisions and the task evidence. Phase order and task status are in [plan.md](../../plan.md); feature status is in [sync-progress.md](../sync-progress.md).

## Evidence boundary

This audit is static: structural declarations and native ISIL in the preserved decompile (same `GameAssembly.dll`/metadata pair as the [phase 4 audit](phase-4.md#evidence-and-build-boundary)). No game was launched for it. Only the native excerpts named below support behavioral claims. Per the owner's workflow (code first, owner tests later), the runtime checks are the owner's game test of the finished tasks.

## What the game does (native, decompile)

### Plugging a cable in

- **Gesture.** In the work state's cabling mode, `WorkingOnComputerState.CablingTick` registers a long press on a connector. Its callback `<>c__DisplayClass70_0.<CablingTick>b__0` (field `connector`) constructs `ConnectCableState(case, camera, connector)` and makes it current.
- **Entering `ConnectCableState`** can already change the PC. `OnStateEnter` looks for free connectors that cable can reach. When a fan header has none, it may **install a PSU splitter by itself**:
  - `CableInstance.Uninstall` + `DestroyCable` on the cable already there;
  - `PartInstance.NewPartInstance("PSU_SPLITTER_3X6PIN"/"PSU_SPLITTER_3X8PIN")`;
  - `Slot.InstantiatePart` into one of `Case.psuSplitterSlots`;
  - `PowerSplitter.UpdateIDs(slot, true)` and `Case.RefreshPowerSplitterSlots`;
  - then a new `ConnectCableState` from the old cable's end connector.

  Otherwise it may show "splitter needed". So a shared cable edit must start at the long press, before this state exists.
- **Choosing the other end.** `HandleInput` raycasts connectors. A long press (`<>c__DisplayClass20_0.<HandleInput>b__0`) runs the `DoConnect` coroutine. That calls `Case.CreateCableAsync(cableType, start, end, connectorType, auto, materialIndices, overrideTransforms, updateGameState, onConnection, turnOn)` with the global cable choice (`GameController.GetCableType`/`GetCableConnectorType`), then `GoToPreviousState`.
- **Saved form.** `ComputerSave.cablesInstalled` holds `CableSave` entries: `m_from`/`m_to` connector IDs, `m_desc`, `m_connectorType`, `m_pinMaterialIndices` (custom wire colours), `m_isNonModularConnector`, `m_playerChoice` and route `m_points`. `psuSplitterIDs`/`powerAdapterIDs` hold the accessory parts per `Case.psuSplitterSlots`/`powerAdapterSlots` index. A live `CableInstance` carries the same `m_start`/`m_end`/`m_desc`/`m_connectorDesc`/pins and `Installing`/`Uninstalling` flags.
- **Hand-laid routes.** `CableLay`/`LayCableState`, and the `ConnectCableState` constructor overload that turns on cable-lay mode, have **no caller** in this build: `LayCableState` is never constructed, and every caller uses the plain constructor. "Custom cables" are cable items with custom wire colours, picked from the cable inventory category (`OnCurrentItemClicked`/`OnSecondaryItemClicked`). Route points are carried if a record has them, but no game path was found that makes one.

### Unplugging

- **Gesture.** `WorkingOnComputerState.RemoveCableTick` registers a long press on a plug. Its callback `<>c__DisplayClass67_0.<RemoveCableTick>b__0` (fields `<>4__this`, `cableEnd`) calls `Case.OnChangePart(cableEnd.gameObject, true, false)`, then `Plug.Unplug`. `Unplug` calls `SetOutline` and `CableInstance.Uninstall`, which is the unplug animation.
- `Fan` and the 4e removal coroutine (`Uninstall`) also call `Plug.Unplug`. The 4e replay already covers those.

### Power

- **Gestures.**
  - `WorkingOnComputerState.PowerButtonTick` (also `LEDStripState`) calls `PowerButton.Press`. That runs the `PressAnimation` coroutine, which calls `Motherboard.TurnOn` or `TurnOff` depending on `GetComputerRunning`.
  - The work HUD's power button (`WorkingOnComputerStateBase.PowerClicked`) calls `TurnOn`/`TurnOff` directly.
- **`Motherboard.TurnOn`** checks connections and the PSU switch (`Case.TogglePSUSwitch`) and `Case.CanPowerOn` (mains). It may call `Case.AutoConnect`, which connects the bench's external cables through `DoAutoConnect` → `Case.ConnectCables`, and can call `WorkingOnComputerState.SetDisassemblyMode`. A `DelayPowerOn` coroutine later runs `Motherboard.PowerOn`. That sets `computerRunning`, powers the parts, LEDs, LCDs and water loop (`Case.PowerOn`), calls `BenchSlot.GetComputer` and starts `ComputerSim.TurnOn`, which is the boot simulation (phase 6).
- **`TurnOff`** powers everything off, stops `ComputerSim`, turns peripherals off and calls `Case.PowerOff`, which may also call `AutoConnect`.
- **Persistence.** Power is **not saved**. `computerRunning` is a runtime field, and `ComputerSave` has no power flag, so a loaded PC starts off.
- `Case.OnPickup` powers a PC off when it is picked up (phase 3).

### Existing mod behavior at phase 5 start

- Nothing gates cabling or power in co-op. A hand-made cable change shows on one game only. The host's next save then refuses that PC: its 4c build profile hashes the internal cables ("unconfirmed … internal cable connections").
- The phase 3 known gap remains (runs H5c/H6b): a remote PC load plugs the bench's external cables in, then `SettleRemoteDrops` unplugs them to match the mover.

## Factor dispositions

| Factors | Phase 5 responsibility |
| --- | --- |
| HW-022–024 | **5b:** cable endpoints, type, connector, pins and route points, connected state, through shared cable edits. |
| HW-025 | **5b:** cable mesh and plug animation, replayed natively on the other game. |
| HW-026–027 | **5b:** custom-colour cable items (pins and connector types) are carried with the cable (owner decision). The cable colour app stays phase 11. |
| HW-028 | **5b disposition:** the hand route gesture has no reachable caller in this build. Route points in a record are carried as data. |
| HW-029 | Derived. Clips, grommets and desk routes follow the native cable. |
| HW-054 | **5b:** PSU splitter and adapter occupancy (`psuSplitterIDs`/`powerAdapterIDs`). The automatic splitter is replayed. Splitter/adapter slots join the 4e shared slot list. |
| HW-042, HW-046, HW-050 | **5c:** physical power on/off, PSU switch and powered parts/peripherals/LEDs, through a shared power edit and a shared `Powered` fact. |
| HW-047, HW-055 | **Phase 6:** boot/BIOS/OS screens. Owner decision: each game runs its own boot screen until then. |
| HW-049 | Phase 3g (peripheral swaps). The cables to them follow 5b/5c. |
| HW-072 | Phase 7: condition changes that cut power. |

## Owner decisions (October 27, 2026)

- **What the other game shows:** the game's own plug/unplug animation with the same cable, colours and route, as for 4e.
- **Power:** shared on/off. Both games power the PC together (fans, LEDs, sound), and each game runs its own boot screen until phase 6. BIOS/OS changes aren't shared yet.
- **Custom cables:** included. The cable carries its wire colours; the colour editor app stays in phase 11.
- **Carried over from 4e:**
  - while one player's change runs on a PC, the other player waits on that whole PC;
  - inventory mirrors;
  - the host's game is the record that is published.

## Design (tasks 5b–5c)

- **Same pipeline as 4e (command 9):** three new gestures, `Connect`, `Unplug` and `Power`. Each claims the whole PC (an edit claim with no part targets) because a cable edit can touch connectors on many parts and add a splitter.
- **Following the editor's edit.** The editor's game runs the game's own gesture after the host accepts it, and is followed until it settles:
  - for Connect, the cable state is gone;
  - for Unplug, the cable is gone;
  - for Power, the press animation and power-on delay are done;
  - in every case, no cable is animating or loading.

  The commit then carries what changed on that PC: cables removed (`from>to`), cables added (full `CableSave` data from a fresh record), splitter/adapter changes per slot index, inventory changes and the resulting power state. A change that changed nothing is cancelled.
- **The other game** applies that change with the game's own calls:
  - the power button press;
  - `OnChangePart` + `Plug.Unplug` for removed cables;
  - the splitter install sequence for added splitters;
  - `Case.CreateCableAsync` for added cables;
  - then the inventory difference.

  The host then settles as in 4e: it captures the PC, including its power state.
- **`PcFact.Powered`.** Only a PC on a bench can be powered. Leaving the bench clears it, matching `Case.OnPickup`. After a load (Continue, late join), each game turns on the PCs the shared world says are on.

## Risks and gaps

- `Motherboard.TurnOn` on the other game may change that player's work mode through `AutoConnect` → `SetDisassemblyMode`. To check in game.
- Removing a splitter or adapter by itself has no traced native path. Unsupported replays report a projection failure, so `/sync` repairs.
- The power state can change without a gesture: simulation shutdowns, OS shutdown, a failed delayed power-on. Those are phases 6–7. A shutdown on the host is captured at its next settle only.
- Each game runs its own `ComputerSim`, so screens, temperatures and boot timing can differ until phases 6–7.

## Implemented contracts (version 0.15)

- **Command 9 gestures** `Connect`, `Unplug` and `Power` ([`ComponentEdits.cs`](../../src/PCBS2Coop.Network/ComponentEdits.cs)):
  - `Begin` names no slot. Its text is the start connector, the cable key `from>to`, or `on`/`off`. The claim covers the whole PC: `ActivityWorld` accepts an edit claim with no targets, and while one is pending any other claim on that PC is refused.
  - `Commit` carries a `Wiring` change: cables removed and added (`CableFact`, the `CableSave` fields), splitter/adapter slot changes, inventory changes and the power state. Its limits are checked: counts, text, route points and duplicates.
  - `Settle` is the 4e host capture, now with `Powered` from the host's game. This applies to part edits too.
- **`PcFact.Powered`** is in the object state, snapshots and co-op saves. It is valid only on a bench, and `PcFact.At` clears it when the PC moves (transfers, lifecycle observation, departure recovery).
- **Plugin** ([`NativeComponentEdits.Wiring.cs`](../../src/PCBS2Coop.Plugin/NativeComponentEdits.Wiring.cs)):
  - Hooks: `<CablingTick>b__0`, `<RemoveCableTick>b__0`, `PowerButton.Press` and `WorkingOnComputerStateBase.PowerClicked`.
  - After acceptance, the stored gesture runs natively.
  - The editor is followed until no `ConnectCableState` is open, no cable is installing, uninstalling or loading, and the button and power-on delay are done, for one second. A change that changed nothing is cancelled.
  - The other game replays the change in this order: press, unplug, splitter install, `CreateCableAsync`, inventory, then a cable-set check. Anything it can't show is a projection failure, which `/sync` repairs.
  - After a load (first 90 seconds), each game turns on the PCs the shared world has on, once per PC.
  - Splitter and adapter slots are in the 4e shared slot list.

### Model evidence

**504/504** model/network checks, including the 4e suite and these new cable and power cases:
- slot-less begins, the power text and an unplug's key;
- the whole-PC waiting rule against part edits, latches, power and resource claims;
- a commit without a change, duplicate cables, an empty inventory line, broken route points and too many changes;
- the splitter getting a new ID at settle;
- power on, a pulled 24-pin turning the PC off, and a press with no effect cancelled;
- bench-only power and the round-trip.

Release build: 0 warnings, 0 errors. Deployed to the game folder and checked identical. Nothing ran in game.

## Acceptance cases (owner's game test after 5b–5c)

1. Plug in and unplug each standard cable type (24-pin, CPU 8-pin, GPU, SATA power/data, fan, front panel/USB headers where present), in both directions.
2. A fan header with no free connector: the game's automatic splitter is shown on both games.
3. A custom-colour cable: the same colours on both games.
4. Esc in the cable state with nothing connected: no change, and the PC is free again.
5. The waiting rule: the other player is refused while a cable state is open on that PC, and can still use a different PC.
6. Power on/off with the case button and the HUD button: both games power on, and external cables auto-connect the same way.
7. Pick up a powered PC: both games show it off.
8. Late join and host save/Continue with a powered PC: it is on again on both games.
9. A 4e component edit on a powered PC: both games end in the same power state.
10. `/sync` after any of these.

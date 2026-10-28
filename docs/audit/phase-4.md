# Phase 4 focused audit: standard component installation and removal

October 26, 2026. Phase 4 has begun with a focused static audit. **Component edits are not synchronized yet.** This document records implementation prerequisites and acceptance cases, not evidence that two-player assembly works. Phase order and task status belong in [plan.md](../../plan.md); feature status belongs in [sync-progress.md](../sync-progress.md).

## Evidence and build boundary

This pass read the current mod and selected structural declarations/native ISIL in the preserved decompile. Structural C# bodies are placeholders; only the specifically described native excerpts support behavioral claims. No game was launched, game save opened or changed, or plugin build/deployment run for this audit. The existing network/model acceptance suite passed **353/353** (`dotnet run --project tests/PCBS2Coop.Network.Tests -c Release --no-restore`, reported by the coordinating agent). That establishes the pre-change baseline, not component behavior.

Read-only hashes of installed inputs were compared with the [decompile manifest](../../artiffacts/decompiled/manifest.json), resolving `<GameDir>` through [LocalPaths.ps1](../../scripts/LocalPaths.ps1):

| Input | Result |
| --- | --- |
| `GameAssembly.dll` | SHA-256 `646148bbbece7076064891df335668a770482810d91ba0fc7d3e5a4be5301aaf`, matches preserved input. |
| `PCBS2_Data/il2cpp_data/Metadata/global-metadata.dat` | SHA-256 `3104c9aac111e3a88a2df72f0a7420c8118391d2819daffcee0c58525adc10f3`, matches preserved input. |
| `PCBS2_Data/boot.config` | Build GUID remains `92d05e5a20c14d41aacbb9c6a804d5a3`. File hash differs because the installed file lacks the preserved `single-instance=` line; the line comparison found no other difference. This is a launch-configuration difference, not a different inspected binary/metadata pair. |
| Addressables catalog | Installed `PCBS2_Data/StreamingAssets/aa/catalog.bin` exists, 1,829,565 bytes, SHA-256 `2692aa9596c464cbb3828dbc6bc4b9fa606c0d0abb4fdddb1446a3b70f5cfe4d`. This identifies the local catalog only; another machine and the referenced bundles were not verified. |

Unity `6000.2.6f2` and metadata layout `31.1` are recorded by the manifest. No reconstructed part-prefab/scene/Animator asset workspace was found in the inspected artifact tree; the shipped catalog does not establish prefab wiring. The earlier [coverage ledger](coverage-and-evidence.md) describes the same declaration/native/asset distinction.

## Scope and factor dispositions

Existing owner choices settle the product behavior: one shared Free Build world, host-ordered changes, guests wait for confirmation, concurrent work on different parts of one PC, conflicting edits and PC movement during work blocked, personal camera/input local, and every new fact included in snapshots/save/rejoin. No new product choice was uncovered. Selecting hooks and reconstruction order is an evidence task. Decisions and detailed scope are in [plan.md](../../plan.md).

Case creation/deletion and whole-PC movement remain phase 3 behavior. Phase 4 adds standard build/disassemble interactions inside those PCs. Destinations below do not permit a standard edit to silently destroy existing cable, software, water-loop, condition or customization facts. Prerequisite side effects must be inspected and preserved or included faithfully in the accepted component mutation.

Factor titles/source indexes are in [hardware-and-building.md](hardware-and-building.md), [player-workshop-and-presentation.md](player-workshop-and-presentation.md), and [session-and-consistency.md](session-and-consistency.md). Their original statuses are historical; current status remains in the tracker.

| Factors | Phase 4 responsibility / dependency |
| --- | --- |
| HW-001–003, HW-005, HW-007–009 | **4b/4c/4e:** Extend PC/part identity to empty/occupied slots, standard install/remove, generated/proxy parts, multi-slot coverage and native fit/blocker results. |
| HW-006 | **4b/4e:** identify held/preview/loading stages and their accepted result; provide the minimum operation feedback required by phase 4 without entering the observer's local input state. Detailed visual parity remains phase 13. |
| HW-004, HW-044, HW-065–066 | **4b/4c/4e:** Preserve installed orientation, offset, fan mounting/flip and associated clip/screw placement. Fan control, airflow simulation and dynamic reversal/spin remain phases 7/11; full animation parity is phase 13. |
| HW-010–013, HW-041, HW-068–070 | **4b/4c/4d:** Screws/standoffs, socket and RAM/PCI retention, covers/hinges: identify settled state, start/commit/cancel and native persistence. Minimum understandable feedback is phase 4; full clip timing is phase 13. |
| HW-014–015, HW-052 | **4b/4e:** Preserve condition/filth during replacement. New damage/simulation remains phase 7; maintenance remains phase 9. |
| HW-018–021, HW-056–058, HW-071 | **4b/4e integrity dependency:** Preserve paste, delid and pad-related native records when an ordinary part moves. Their tools/procedures are phase 9; no new maintenance synchronization is implied here. |
| HW-022–025, HW-028–035, HW-074–075, HW-078 | **4b/4e integrity dependency:** Preserve existing water/cable graphs and child references during ordinary edits. Electrical editing/power is phase 5, custom water editing is phase 10; native component autoconnection/removal side effects are a phase 4 integrity prerequisite. |
| HW-026–027, HW-036 | **4e integrity dependency:** Preserve custom connector/material/theme references during ordinary edits; their editing/content semantics remain phases 11–12. |
| HW-051, HW-053–054 | **4b/4c/4e:** Physical standard accessories/child parts, storage/M.2/PSU splitter/adapter occupancy and USB membership where the verified ordinary assembly path uses them. USB OS/install/clone payload behavior remains phase 6; electrical connection behavior is phase 5. |
| HW-073; SS-003 | **4b/4c/4e:** Fit/topology depend on catalog and actual prefabs. An existing compatibility warning is not proof a requested asset exists on the other peer. |
| PW-008–011, PW-013 | **4b–4e:** Per-actor work/target/gesture, pending operation, input gating and cancellation. Camera, hover and input coordinates stay personal. |
| PW-058–059, PW-065–066 | **4b/4d/4e:** Separate gameplay callbacks from minimum operation feedback; presentation must not commit twice; cancel/leave stops owned effects. Detailed clip/audio parity remains phase 13. |
| SS-005–006, SS-010–014, SS-016, SS-018–027 | **4c–4f:** Reuse session/generation/revision/main-thread foundations; extend resource identity, claims, canonical component deltas, reconstruction and callback suppression. |
| SS-029, SS-031–035, SS-038–042 | **4b–4f:** Background/pause, active-edit admission, coherent paired saves, reconnect/resume, payload validation/limits, diagnostics and fault/disconnect cleanup must cover component operations. |

## Native findings

### Slot setters do not implement the transaction

The broad audit named `Slot.SetComponent` / `RemoveComponent` as candidate commit methods. The focused read narrows that claim: [`RemoveComponent`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Slot.txt#L54190) delegates to `SetComponent(null)`; [`SetComponent`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Slot.txt#L54203) compares Unity object references and updates the backing component reference (offset 392). Its inspected ISIL does not settle inventory, validate fit, run the complete lifecycle or rebuild the case. Loading also sets slot components. These methods alone are insufficient authoritative edit hooks.

Installation crosses synchronous/deferred paths. Native [`InstantiatePart`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Slot.txt#L24709) and [`InstantiatePreloadedPart`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Slot.txt#L32629) contain paths into `InstallingPartState`; the [`InstallPartWhenLoaded` iterator](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Slot_NestedType__InstallPartWhenLoaded_d__176.txt#L43) constructs that state and calls `GameController.SetCurrentState` after loading. Intercepting only one asset-ready branch can miss another; replaying this path on an observer would enter its local state machine. The [`wrapper`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Slot.txt#L32515) is an iterator factory: investigate `MoveNext`, not only that factory's label.

The removal outer path is [`WorkingOnComputerState.Uninstall`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/WorkingOnComputerState.txt#L17435) and its [`iterator`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/WorkingOnComputerState_NestedType__Uninstall_d__72.txt#L43). The iterator includes case change calls, connector and bridge-related paths, and construction through `InstallingPartState.UninstallState`. A downstream refusal can therefore follow outer side effects. Exact branch ordering/rollback needs the owned probe; refusing the state constructor alone is not established as safe.

### Lifecycle mixes mechanics, settlement and presentation

[`InstallingPartState` declarations](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/InstallingPartState.cs#L149) include part/slot/case, temporary standoffs, screw/done-screw lists, orientation, acceptance, direction, automatic mode and upgrade-related fields. Declarations do not establish durable save coverage. Native [`StartInstallComponent`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/InstallingPartState.txt#L7077) includes screw setup/placement calculations. [`DoScrewsStep`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/InstallingPartState.txt#L21018) calls `Screw.Tighten` and advances into install/secondary-animation/uninstall branches. [`AnimateComponentStep`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/InstallingPartState.txt#L15298) includes `PartInstance.SetSlotCaseRelativePosition`; [`AnimateOutComponentStep`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/InstallingPartState.txt#L16956) includes `Case.PostChangePart` and local state-stack transitions.

[`OnStateDestroy`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/InstallingPartState.txt#L9592) has guarded paths calling `Case.ConnectCables`, `RebuildLEDs`, `RebuildSlots`, `FixupBlockers`, `CareerStatus.AddItemToInventory`, `PartInstance.SetFilth`, `PostDestroyPart`, `UpdateGameStatus` and GPU/PSU connector validity updates. The inspected inventory branch checks mode/item properties and existing catalog inventory before adding; it is not an unconditional new-item event. [`HandleExit`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/InstallingPartState.txt#L83) and direction/acceptance need probing to distinguish cancellation/success. A destructor name does not prove all invocations commit.

**Consequence:** reserve before any native edit, identify the accepted result once, and project remotely without opening the other player's input state or replaying inventory/autoconnect callbacks. Probe native auto-cabling upgrades: a component action can cross into phase 5 facts before ordinary cable editing is implemented. The crossings are evidenced; complete branch semantics and safe suppression/reproduction are still gaps.

### Compatibility exceeds type equality

[`CanFillWithItem`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Slot.txt#L21276) calls fit/component/case/motherboard paths. Further native entry points are [`CurrentBlockers`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Slot.txt#L13588), [`FitsSlot`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Slot.txt#L18344), [`IsUninstallingPathBlocked`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Slot.txt#L4638), [`CanInstallGPU`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Slot.txt#L50888), [`CanInstallFanOrRadiator`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Slot.txt#L51993) and [`ComponentPC.CanBeUninstalled`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/ComponentPC.txt#L12660). This pass establishes these paths and selected calls, not complete native predicate semantics.

The [`Slot` declarations](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/Slot.cs) include blockers, collision, proxy/parent-slot and placement information. Host checks need the correct bound live slot and canonical facts; type equality cannot authorize fit. Claim dependencies include affected neighboring slots, parent deletion, GPU coverage and relevant latch/blocker facts. Claiming the whole motherboard for each child edit would block accepted disjoint work; claiming only the clicked part misses an empty destination and changing dependencies.

### Pin, socket and screw-cover paths differ

[`Pin`](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/Pin.cs#L229) declares open/locked/animation state, child pins and connector/part blockers. Native [`TogglePin`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Pin.txt#L4963) checks blocking and starts animation; [`PopOpen`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Pin.txt#L5109) is another entry. `CPin` is the abstraction; sockets need not call base `Pin.TogglePin`.

For example, [`AM3Slot.TogglePin`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/AM3Slot.txt#L404) uses socket coroutine paths and distinct open/locked fields; [`LGASlot.TogglePin`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/LGASlot.txt#L6322) is separate and its `Update` includes pin-change notification. The [`CpuSocket` declaration](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/CpuSocket.cs) alone proves neither live reachability nor a hook.

Screw-operated covers are another path: [`ScrewPin.TogglePin`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/ScrewPin.txt#L24) starts `SimpleScrewState`; [`SimpleScrewState.Tick`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/SimpleScrewState.txt#L505) advances screws then calls `ScrewPin.PinToggle`, which calls base `Pin.TogglePin`. [`ScrewPin`](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/ScrewPin.cs) declares blocking screws. Guard the outer gesture and distinguish its accepted inner call from a second request.

[`Screw`](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/Screw.cs#L12) declares required standoffs, rotation/speed/offset and a distinction between component-affiliated and `ScrewPin` screws. Native [`StartInstall`, `StartUninstall`, `ForceTight`, `Tighten`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Screw.txt#L206) are separate stages. Do not turn each `Tighten` frame into a world command. Persistence/cancellation of partially completed screws is **not established** and must be probed, not discarded by assumption.

### Native save graph and mechanical persistence

[`ComputerSave`](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/ComputerSave.cs#L864) declares individual case/motherboard/CPU/cooler/PSU fields, typed storage/fan/radiator/PCI/RAM/USB/M.2 arrays, cover boolean arrays, bespoke case slots and `m_openPins`. [`PartInstance`](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/PartInstance.cs#L57) includes proxies, child fans, orientation/relative position/flip and generated-copy/auto-instantiated facts. [`GpuPartInstance`](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/GpuPartInstance.cs) and [`RamPartInstance`](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/RamPartInstance.cs) add nested relationships. Flat SKU lists cannot restore these graphs or distinguish identical parts.

Native [`SaveComputer`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/SaveLoadSystem.txt#L27581) captures live case slots; [`GetPartInstancesForSlots`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/SaveLoadSystem.txt#L28579) reads per-slot records. [`RecordOpenPins`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/SaveLoadSystem.txt#L28373) and [`OpenPins`](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/SaveLoadSystem.txt#L27193) are dedicated mechanical save/restore paths. [`GameObjectRelativePath`](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/GameObjectRelativePath.cs) stores a relative object path and resolves against a parent. These are mapping evidence, not proof hierarchy names are unique for every variant or sufficient network identity.

No separate durable screw-progress field was found in the inspected `ComputerSave`/`PartInstance` declarations. That negative result is limited to those representations. Compare pin/socket state, covers and partial screws before/after native save/load, with unique sibling/path discrimination and actual default/generated parts. Save capture is a mutation boundary: do not poll `GetComputer(true)` or carried-save capture for diagnostic progress; the existing phase 3 finding applies.

## Current mod integration gaps

The [object model](../../src/PCBS2Coop.Network/ObjectWorld.cs#L14) gives each part ID/SKU/native type and non-null native-field-path links. It supplies neither empty-slot identities nor independent mechanics. Preserve its aliases; add stable empty-slot/mechanical resources bound to the correct owning PC/part incarnation. Parent replacement invalidates descendants and stale requests without reusing IDs.

[`ActivityWorld.Validate`](../../src/PCBS2Coop.Network/ActorActivity.cs#L87) accepts edit targets only when they are existing part IDs; it rejects duplicate targets and intersects claims. [`Owns`](../../src/PCBS2Coop.Network/ActorActivity.cs#L42) already validates token/epoch/actor. Extend this contract to empty slots and mechanical/dependency resources. [`NativeActivity`](../../src/PCBS2Coop.Plugin/NativeActivity.cs#L23) observes the locally worked PC but acquires no component reservation. Remote operation state remains per actor and must not enter local camera/input/global controller fields.

[`CaptureHost`](../../src/PCBS2Coop.Plugin/NativeObjectState.cs#L511) currently reconciles native parts at capture: it keeps prior identity for matching field path/SKU/native type, allocates otherwise and retires missing IDs. That cannot distinguish same-SKU replacement in the same slot and can adopt unsupported host edits. Supported phase 4 identity must follow accepted part creation/transfer results; observation validates/projects and reports divergence instead of silently authorizing changes or overwriting concurrent edits. Initial baseline binding and genuine generated load growth retain explicit phase 3 boundaries.

[`GamePatches`](../../src/PCBS2Coop.Plugin/GamePatches.cs) currently has no standard component/mechanical interception. Its [`NativeCode` deduplication](../../src/PCBS2Coop.Plugin/GamePatches.cs#L47) is mandatory: merged IL2CPP native bodies are hooked once. [`NativeProjection`](../../src/PCBS2Coop.Plugin/NativeProjection.cs) and [phase 3 repair/error contracts](phase-3.md) still apply: accepted projection never resubmits; projection failure reports `ProjectionFailed`; tickets guard cancellation/replacement/generation; host saving never occurs inside `Apply`.

## Required asset and runtime evidence

Task 4b uses opt-in disposable self-test saves and explicit main-thread capture boundaries. Preserve the owner's game processes/saves. Existing phase 3 case-asset evidence does not establish motherboard/socket or every child-slot variant.

| Evidence case | Required observation |
| --- | --- |
| Empty case, motherboard, CPU, identical RAM pair | Live slot/parent to saved-field mapping before/after rebuild, distinct part IDs, reachable AM3/LGA/other socket families and retention state. |
| GPU, PCI covers, supported bridge/riser | Multi-slot coverage, neighboring conflicts, hidden/generated/proxy components, removal blockers, bridge effects and child identity. |
| HDD/SSD, M.2 plus heatsink/cover, PSU, ordinary accessories | Exact subtype/live-slot/save mapping, fit, screw/standoff dependencies, displaced children, splitter/adapter and accessory membership. |
| Air cooler, stock AIO, case fan with changed orientation | Coupled parts/fan subslots, mounting/flip persistence, generated defaults and minimum visuals. Do not infer a stock AIO is one independent part; custom loop editing remains phase 10. |
| USB physical membership | Ordinary insertion/removal versus cable/software side effects; replica callbacks must not run OS install/clone logic. |
| Cover/hinge/RAM/PCI pin, AM3/LGA socket, screw cover | Actual class, collider/target identity, blocker graph, open/locked/animating transitions, `SimpleScrewState`, save/reload result. |
| Each family: start/cancel, completion, leave, intermediate save | Callback order, inventory before/after, acceptance/direction interpretation, ownership release, partial mechanics and coherent snapshot boundary. |
| Cache hit/delayed asset; pending parent replacement | Synchronous/deferred completion, hook aliases, stale callback suppression and generated topology. |
| Native auto-screw/standoff/cabling upgrades | Timing-only preferences versus saved world effects, and safe projection without a second transaction. |

Inspect actual prefab slot arrays, parent/proxy edges, colliders/blockers, mechanics transforms, screw affiliations and selected animation/clip-event bindings. Enumerating instantiated real assets can close a specific mapping gap; if it cannot expose clip events, record that separately. A source enum or successful load does not establish category coverage. Record representative SKU/variant coverage and untested families.

## Recommended dependency order and acceptance

The six focused tasks in [plan.md](../../plan.md) fit these findings: **4a** static audit; **4b** owned native/asset probe; **4c** topology/resource identities, claims and persistence; **4d** mechanics; **4e** standard install/remove and required inventory/side effects; **4f** combined acceptance. Only the startup static audit is complete here; 4b evidence gates dependent hook choices. Wire/save changes require the single mod version bump before handing out a build. The full task outcomes, dependencies, work, acceptance and completion checks belong in the plan.

| Case | Required outcome |
| --- | --- |
| P4-01 — build/disassemble by either role | Every supported category agrees on IDs, parent/slot membership, orientation/mechanics and visible targets. Catalog inventory is neither wrongly consumed nor duplicated. |
| P4-02 — identical parts | Identical RAM/fan/storage items remain distinct; same-slot replacement has its intended new/transferred identity; removing one preserves its twin and graph aliases. |
| P4-03 — concurrent work | Independent targets on one PC and different PCs work concurrently. Same-slot, GPU coverage, parent/child and blocker conflicts resolve to one valid order without a blanket PC edit lock. |
| P4-04 — refusal before mutation | Invalid fit, blocked removal, occupied slot, missing asset, stale epoch/token/generation, retired identity and malformed payload change neither native/logical PC nor inventory; local input stays usable. |
| P4-05 — mechanics/interruption | Socket, latch, screw/standoff and cover families show accepted state on both peers. Cancel at each observed stage preserves/restores the evidenced result once; pause/focus loss/disconnect/leave releases claims/effects. |
| P4-06 — admission/save/resume | Pending-edit snapshots use the recorded coherent boundary and held/replayed revisions. Paired save, late join, guest rejoin, fresh host Continue and host-alone resume preserve topology/mechanics/identity without replaying inventory/software callbacks. |
| P4-07 — asynchronous faults | Warm/cold assets, parent replacement, canceled operation, generation change and departure cannot place obsolete parts. Duplicate/reordered requests commit once; projection faults mark out of sync and `/sync` restores the supported host result. |
| P4-08 — regression/integrity | Phase 3 PC creation/deletion, transfers, peripherals, paired saves, package/note refusal, chat and `/sync` still work. Standard edits preserve existing specialist/native payload fields. |

Network tests prove command/state invariants; native probes prove hook/asset behavior; owned two-instance runs prove both logical and visible application. These are different evidence levels. None of P4-01–08 has been executed for phase 4 in this pass.

## Task 4b: opt-in native and asset probe

October 27, 2026. **Status: complete** (normal-control checks N1–N8 and gap checks G1–G6 recorded; the owner deferred custom-loop and cable-accessory edits). The following is subsequent owned-game research, separate from the static pass above. Component synchronization remains unimplemented. A native diagnostic call is not normal-control or two-peer acceptance evidence.

### Probe and isolation

[`Phase4Probe`](../../src/PCBS2Coop.Plugin/Phase4Probe.cs) runs only with `PCBS2COOP_AUTOTEST=host|join` plus `PCBS2COOP_PROBE=4`, inside the shared probe driver's confirmed phase 4 disposable save root. Ordinary launches install none of these hooks. The [shared driver](../../src/PCBS2Coop.Plugin/Phase3Probe.cs) retains its phase 3 default and controls, waits for world readiness before consuming phase 4 commands, and records command provenance, native callback entry/exit, state transitions and explicit captures. No wire/save schema or mod version change is needed for this diagnostic-only work.

The [launch](../../scripts/Start-Phase4Probe.ps1), [command](../../scripts/Send-Phase4ProbeCommand.ps1) and [stop](../../scripts/Stop-Phase4Probe.ps1) scripts reuse the phase 3 process/saves protection: a fresh run ID, PID/start-time/executable checks, no deployment over a running game, built/deployed DLL hash equality, retained deployment backup and before/after hashes of the owner's `Saves` tree. Case fixtures refuse a guest and occupied/loading target benches. No owner's process is closed.

```powershell
dotnet build PCBS2Coop.sln -c Release -p:DeployToGame=false
.\scripts\Start-Phase4Probe.ps1 -RunId Example4b -Role host -Deploy
.\scripts\Send-Phase4ProbeCommand.ps1 -RunId Example4b -Command 'p4-report:baseline:save'
# Inspect reports before selecting the run-local pointers used by other commands.
.\scripts\Stop-Phase4Probe.ps1 -RunId Example4b
# Fresh-process Continue on the same run's disposable saves (host only, earlier process stopped):
.\scripts\Start-Phase4Probe.ps1 -RunId Example4b -Role host -Resume
```

`-Resume` sets `PCBS2COOP_PROBE_RESUME=1`: the redirect reuses only that run's `saves-host` folder (it must already hold a co-op save) and the self-test presses the menu's real **Continue** button instead of **New world**. The earlier process record is kept as `host-r<N>-process.json`, so the stop script still compares owner saves for every launch of the run.

Evidence lives in `<GameDir>/BepInEx/PCBS2Coop-diag/phase4/<RunId>/<role>/`; ownership, hashes and copied logs live in ignored `.local/phase4-<RunId>/`. Native pointers correlate observations within one process only. Never reuse them after a reload, parent replacement or another run.

| Command | Evidence boundary |
| --- | --- |
| `p4-report:<tag>` / `p4-report:<tag>:save` | Live census; `:save` explicitly invokes native PC capture and writes codec bytes. Polling state never saves a PC. Rows include empty slots, typed case/motherboard arrays, sibling-index paths, component references, part/save aliases, blockers, pins and screws. |
| `p4-catalog` / `p4-preload:<sku>` / `p4-fit:<sku>` | Native inventory listing, asynchronous asset preload and read-only per-empty-slot fit results with asset-ready status. A fit refusal is not automatically an incompatibility finding. |
| `p4-work:<bench>` | Invokes the bench's existing DEFAULT work interaction delegate. Bypasses pointer/reach input; records returned action names. |
| `p4-install:<slot pointer>:<sku>:auto|manual` | Fresh Free Build part, native fit check and `InstallingPartState` lifecycle. `auto` sets `ForceUpgrades` before entry; `manual` disables automatic flags on that diagnostic state only. Bypasses inventory/target gesture. |
| `p4-remove:<slot pointer>:auto|manual` | Native work-state uninstall coroutine; bypasses the caller's target/hold eligibility checks. This is lifecycle evidence, not complete removal validation. |
| `p4-cancel` | Calls the active install/remove state's `HandleExit`, then leaves the state when it returns true. The ISIL body always returns true (`mov al,1`), so the departure is the probe's own action, not a native "caller must leave" contract: normal Esc during the animation reaches `HandleExit` and the edit still completes (C4b01). A command after settlement does not prove cancellation. |
| `p4-accept` | Sets the active install state's orientation acceptance field. Explicit diagnostic shortcut; does not certify click/drag input. |
| `p4-input:on` / `p4-input:off` | Restores the recorded Rewired keyboard/mouse enabled flags or blocks them in this owned process. Input is blocked by default so unattended native traces cannot receive stray physical actions. Enable it before desktop-control testing. |
| `p4-pin:<pointer>` / `p4-screw:<pointer>:install|uninstall|tight|<delta>` | Supplemental mechanical calls. Direct screw changes do not certify the gesture/state machine. |
| `p4-case:<empty bench>:<case sku>` | Native empty-case fixture with prefab default fans; explicitly bypasses co-op PC creation. For mapping/installation research without a guest. |

### Controlled findings and corrections

- **B1/B2 (failed startup, retained):** `SimpleScrewState.OnStateDestroy` shares a native no-op stub with unrelated methods. One typed hook there caused native trampoline errors even though no second hook targeted the address. It is excluded; return-only `RET`/`RET 0` stubs are skipped too. Deduplication among already hooked methods alone is insufficient for such a stub. Both owned processes were stopped; owner save hashes were unchanged.
- **B3/B4/B5 (probe development, retained):** direct construction of `WorkingOnComputerState` omitted work-entry prerequisites and threw; the probe now invokes the actual bench interaction delegate. The populated default slots inspected had `Slot.part == null`; `Slot.GetComponent().GetPartInstance()` supplied their installed records and native-save aliases. A completed removal can leave a destroyed Unity component wrapper in the slot, so the census uses Unity null semantics before dereferencing it and records `destroyedComponent`. `Motherboard` is a separate component, so its arrays are enumerated separately. A one-second delayed RAM cancellation missed the short completed operation; it is explicitly **not** counted as a cancellation pass.
- **B6:** actual `InstallingPartState` entry was necessary to probe install lifecycle. The earlier direct `Slot.InstantiatePart` test merely restored a cached RAM component without entering that state; it did not certify an install/cancel path. B6 removed one `RAM_Gskill_29`, captured an empty `ramSlots[0]`, canceled a new installation in the same command-processing frame through `HandleExit`, and captured the empty destination after rollback to work mode. A subsequent automatic install produced a fresh native record at `ramSlots[0]`; the other three identical RAM records retained their pointers and paths. Callback/state trace includes initial direction `+1` for the canceled install, native reversal/teardown, inventory and case rebuild calls.
- **B6:** the two default PCs supplied AM3 and LGA socket components, duplicate same-SKU RAM/fans, a stock AIO with radiator/proxy and generated-copy aliases, and a custom loop. Typed case and motherboard arrays, empty entries, parent paths, mechanical objects and saved paths were captured on actual loaded assets. This does not establish every slot subtype or catalog variant.
- **B6 refusals:** SSD/HDD/M.2/USB attempts returned false from native `CanFillWithItem`; these left their destinations empty. Removing the right panel did not turn those attempts into successful installs. Asset readiness and full native fit branches require separate evidence; no category is declared supported from these refusals.

### Validation limits

The Windows computer-use connection returned `Computer Use native pipe is unavailable` on initial discovery, retry and fresh-session recovery. No replacement Windows input injector was used. Native calls and in-game screenshots are labelled as such; pointer/hold/drag input, normal inventory selection and remote visible application remain unverified. Gameplay hooks must still be tested through the normal outer gestures before dependent behavior is called supported.

The owner confirmed that Codex desktop control could not be restored and requested a handoff to Claude Opus 5.5 for those checks. Those checks then ran with normal input (see [desktop checks](#desktop-checks-with-normal-controls-c4b01c4b02)). Tasks 4c onward have not started.

### Subsequent native evidence

The named JSON reports and `events.jsonl` are under the run's `host/` evidence directory. Raw codec bytes accompany each explicit `:save` capture. Compact local summaries are retained under `.local/phase4-B7/` and `.local/phase4-B11/`.

| Category | Actual loaded-asset/native-call evidence | Remaining boundary |
| --- | --- | --- |
| Case/access | B7 and B11 loaded `Case_Kolink_4` into an empty bench with its default fans. B11 removed both side panels through the native uninstall lifecycle. | Normal gesture/eligibility, hinged/bespoke cover variants and parent-removal conflicts. |
| Motherboard/CPU | B11 committed `Motherboard_MSI_30` and `CPU_AMD_58` to `motherboardID`/`cpuID`; AM3 socket toggled through its native component. Baseline includes an LGA socket too. | Normal targeting, socket drag/animation completion, alternate socket and standoff branches. |
| RAM | B6 remove, early reverse cancellation and reinstall; other three same-SKU records unchanged. B11 installed two distinct `RAM_TeamGroup_41` records into indices 0 and 1. | Normal inventory/hold input, same-SKU replacement across reload and parent replacement during delayed load. |
| GPU | Baseline loaded GPU/PCI graphs, typed arrays and cover/blocker facts captured. | Fresh install/remove and multi-slot alias/coverage transaction still need direct observation. |
| SSD/HDD | B11 asset-ready installation committed `SSD_TeamGroup_11` to `storageSlots[3]` and `HDD_Seagate_1` to `[0]`. | Normal selection/removal and cold-load cancellation/conflicts. B6 refusals do not mean these categories are unsupported. |
| M.2 | B7 committed `M2_ADATA_10` to `m2Slots[1]`; paired exact reload passed and retained it. | Normal screw/cover/target input and other lengths/slot overrides. |
| PSU | B11 committed `PSU_EVGA_13` to `psuID`; native screws/standoffs recorded. | Normal hold/removal, fit variants and required cable side effects. |
| Air cooler | B11 preview had `m_accept=false` and no saved cooler; `HandleExit=true` plus caller departure removed it. Reinstall plus diagnostic acceptance committed `Air_Cooler_AMD_1` to `cpuCoolerID`. | Normal orientation/drag acceptance, rotation and alternate mount families. |
| Case fan | B11 committed `Case_Fan_ARCTIC_1` to `caseFanSlots[3]`; default fans at 0 and 5 retained. Position/orientation/flip/condition/filth captured. | Normal orientation/flip and removal; radiators/multi-fan aliases. |
| Stock AIO/custom loop | Both shipped PCs captured with their aliases, radiator/proxy/generated-copy references and component arrays. | Install/remove callbacks and cancellation not executed; baseline preservation is the current evidence. |
| Physical USB/accessories | B12 installed a fresh `USBDrive` through the asset-ready native lifecycle into `usbSlots[8]`; exact paired reload retained it as `USBDrivePartInstance`. | Normal insert/remove controls, other accessories and powered callbacks remain. Software/clone semantics belong to phase 6. |

`p4-install` now waits for the part's actual asset-ready callback and revalidates the original work state, slot occupancy, parent case and generation before the native fit check/state construction. A standalone preload can be released before a later command, so `HasLoadedPart` at an earlier time is insufficient. No native fit branch is bypassed. B11's successful SSD/HDD paths supersede the earlier asset-unready refusals. Delayed completion after cancel/parent replacement still needs its own negative check.

`ForceUpgrades` must be set before `InstallingPartState.OnStateEnter`; it automates screws/standoffs but does not accept orientation. `m_accept` is orientation acceptance, not a generic network commit flag. B6 demonstrated the reverse-tick cancellation branch; B11 demonstrated the immediate caller-departure branch. B8's earlier true-return cancel omitted caller departure and is retained as a probe defect, not a native rollback failure.

B11 reports `core`, `cooler-preview`, `cooler-cancelled`, `cooler-committed`, `fan`, `ssd` and `hdd` supply the category evidence above. `capture-003.png` visibly shows the unaccepted cooler preview and orientation arrows; it was generated in game and inspected, but no physical input was exercised. One early work command arrived before the asynchronous case fixture completed and was refused; waiting on `case-ready` fixed the sequencing. Commands queued before readiness can run together in one frame: wait for actual reports/callback events before dependent commands.

### Mechanical persistence and remaining checks

B7's paired `identity-save` → `identity-resume` check passed exact logical PC identities (three cases, no stored PC, no failure). M.2 survived. A directly loosened motherboard screw changed from rotation 180 before reload to 0 afterward: native save restoration does not preserve that intermediate angle in this fixture. This is a direct mechanical-call observation, not a normal partial-hold certification.

B7's plain RAM pin was saved open but appeared closed after reload. The trace includes native `Pin.PopOpen` followed by a separate `Pin.TogglePin`; physical input was not isolated then, so that run alone did not establish the cause.

**B12 clean repeat:** process-local Rewired keyboard and mouse stayed blocked throughout the run. The same unlocked plain DDR3 retention bracket was opened by `p4-pin`; after animation settled, `partial` recorded `open=true` and the matching name-only `m_openPins` path. Its motherboard screw was directly loosened to rotation 180. `identity-save` → `identity-resume` passed exact identity (`cases=2`, `stored=0`, no failure). `reloaded` recorded the same bracket closed and no saved-open path, screw rotation 0, and `USBDrive` still at `usbSlots[8]`. Trace: `Pin.PopOpen` at 126.90173 s, then `Pin.TogglePin` at 127.01338 s on the same reloaded bracket, with no operator pin command between them. The repeat rules out stray physical input for this observation. This is the current modded native-load sequence, not a proven universal pin contract or identified root cause: desktop checks must establish normal opening/partial-hold behavior and whether installed RAM causes that follow-up closure. Local `.local/phase4-B12/mechanical-summary.json` contains the comparison and filtered pin calls; raw reports/events remain in the B12 evidence directory.

Actual loaded animator bindings include `AMDT01_Anim_Graph` and `Intel_Type2_Anim_Graph`. Controller names/layer counts and mechanical hierarchy were captured; complete clip/event/normalized-time behavior was not inspected. Normal screw/standoff holds, plain/linked pins, AM3/LGA socket actions, cover variants, focus loss/pause/cancel and fresh-process restore need desktop-control evidence. No two-peer component acceptance case P4-01–08 has passed: there is no component synchronization adapter yet.

All owned runs B1–B12 are finalized and left the owner's 11 save files hash-identical; all their owned processes are closed. B11 had already exited at the turn interruption; the guarded stop finalized its evidence and save comparison. Probe startup/development failures remain recorded rather than silently discarded. The final diagnostic build was deployed/hash-checked for B12; it includes main-thread checks before callback/native access and clears one-shot install setup flags on exception/return.

The existing **353/353** model/network checks passed after probe integration; Release builds against real interop have **0 warnings, 0 errors**. These are regression checks, not component synchronization acceptance. Mod version remains **0.11**, and nothing has been committed or pushed.

### Desktop checks with normal controls (C4b01–C4b02)

October 26, 2026. One isolated host per run, Free Build fixture with bench 2 (`MSI MPG Gungnir 110R`, AM4, custom loop) and bench 3 (`be quiet! Pure Base 600`, LGA, stock AIO). C4b01 used ordinary Windows mouse/keyboard input from the agent. In C4b02 the owner performed most gestures by hand while the probe recorded, and the agent sent probe commands and some timed holds. Physical input was blocked (`p4-input:off`) during every reload comparison. These are single-game native observations: there is still no component adapter, and no P4-01–08 case is claimed.

Interaction rules observed for every category:

- **Select:** double-clicking an inventory card runs `ItemDisplay.InstallButtonClicked`. Inside that same call the game places a preview (`Slot.InstantiatePreloadedPart(..., preview=True)` + `SetComponent`) into **every** empty compatible slot it offers: both GPU slots (`Slot_GPU_T`/`_B`), both 2.5" bays, five empty fan mounts, two M.2 slots, all free USB ports, and the top and front radiator mounts for an AIO. A preview slot reads as occupied by a `PartInstance` in census/`GetComponent()` until it is torn down. Preview and commit records must be told apart by the creating call, not by slot contents.
- **Commit:** a left-button hold of about 0.31–0.34 s starts the edit. Shorter presses do nothing: 80 ms and 250 ms were refused with no native call. The commit replaces the preview with a fresh non-preview record, then runs `StartInstallComponent` → `InstallingPartState` → automatic screws/latches → `PostChangePart` → `ConnectCables`. Removal runs `WorkingOnComputerState.Uninstall`, disconnects attached cables (about 0.5 s for a GPU), then `InstallingPartState` unscrews and detaches. Once started, an edit finishes without the button held. A 387 ms GPU hold completed the full removal.
- **Toggle:** latches, M.2 covers and socket shields toggle on a single click (`Pin.TogglePin`/`LGASlot.TogglePin`, one native call per click). Holding on a blocked cover repeated `TogglePin` about every 0.18 s with no state change: a toggle call is an attempt, not a result.
- **Refusal:** blocked targets show an orange part and a red blocker. A press on a blocked install target makes **no** native call. While a part is held, its preview captures the pointer, so the player cannot click other mechanics until switching tool (which tears down and re-places the preview).
- **Inventory side effect:** opening the inventory on a case re-adds that case's removable parts (`CareerStatus.AddItemToInventory`), and every selection runs `RemoveItemFromInventory` over them. The owner confirmed nothing was lost: the case reassembled fully from Removed Components. Synchronization must not replay these list rebuilds.

| Check | Normal-control result | Remaining boundary |
| --- | --- | --- |
| N1 RAM | Clips must be opened (one click each) before a stick can be removed; with clips closed the stick shows red markers and a hold does nothing. Removing one `RAM_TeamGroup_41` left its three same-SKU twins' records unchanged on both benches. Install closes both clips automatically (`TogglePin` ×2). Removed RAM does not appear in Removed Components. On bench 3 the AIO tube blocked RAM install but not removal. | Same-SKU replacement across reload; parent replacement during a load. |
| N1 cancel | Release before the threshold refuses. Release or Esc after it does not cancel: Esc during the animation reaches `InstallingPartState.HandleExit` (always true, see the command table), the edit completes, and input stays usable in work mode. Esc while only holding a selected part leaves work mode, tears down the preview and triggers a Free Build autosave. | No native reverse-tick cancel is reachable by normal input in these paths; B6's reverse branch is diagnostic-only. |
| N2 screws | Screws and standoffs are never handled one at a time: every screwed part (side panel 2, GPU 2, PSU 4, fan 4, AIO radiator 12) is unscrewed/screwed automatically inside its edit. No partial screw angle is reachable by normal input, so B12's direct 180 → 0 reset has no gameplay counterpart. Focus loss (switching to another window mid-removal) did not interrupt; the game keeps running unfocused. | Standoffs sit under a motherboard blocked by the custom loop and were not reached. |
| N2 interruption | Esc during a GPU removal's cable phase (before `InstallingPartState`) leaves work mode with the card still installed but its PCIe power and HDMI cables disconnected ("Missing cable(s)"). The autosave captured that state and it survived save/reload. The owner repaired it with the Cable tool (three cable actions: `SetCurrentState` → `Case.OnChangePart` → `GoToPreviousState`). The probe's PC topology hash was identical to the fully cabled PC, so **cable connections are not in that fingerprint**. | Cable state must be synchronized and checked separately in 4c/4d. |
| N2 linked pins | With the GPU installed the M.2 cover (`M2_COVER_1`, `blockedBy` = `Slot_GPU_T`) refuses to open (GPU red, cover orange). After GPU removal it opens normally. Its `locked`/`blockedBy` fields still name the now-empty GPU slot, so the game evaluates blockers live from slot occupancy. With the cover open, the GPU install target is orange, the cover red, and the press is refused with no native call. | Other linked/bespoke cover families. |
| N3 RAM latches | Bench 2: slot 1 empty with both clips open, stick 2 one clip open, stick 3 both open, stick 4 closed. Same-process reload (`identity-resume`) and a **fresh-process Continue** (`-Resume`) gave the same result: `Pin.PopOpen` restored all five saved-open clips, then about 0.13 s after `LoadComputerToSlotAsync` returned, `Pin.TogglePin` closed exactly the three clips on **occupied** slots. Clips on the empty slot stayed open. The mod issued no pin call (it only toggles pins through `p4-pin`), and input was blocked. The caller is an indirect call not found by text search. | Native behavior to mirror rather than a defect: a latch is only persistently open on an empty slot. |
| N4 CPU/socket | LGA shield: one click opens, one click closes. CPU removal about 0.2 s, no screws. CPU selection previews into the socket and a hold installs (about 0.15 s, no orientation step). The latch toggled while the cooler was held. | AM3 socket was not exercised by normal input (bench 2's CPU is under the custom loop). |
| N4/N7 AIO | The stock AIO (pump, hoses, radiator) is **one** `PartInstance` occupying four slots: the CPU cooler slot and three front radiator slots. Removal first required removing the front-panel USB cables and front panel (shown red as blockers). Removal clears all four slots in one edit (12 radiator screws, about 3.5 s). On install, the radiator goes to the free mount nearest the aim: holding on the CPU chose the front mount, and aiming at the top mount showed it red (blocked by the rear fan). The commit places the radiator slot and fills the other three slots with `Proxy` components. No orientation prompt appeared. | Custom-loop edits and other accessories. |
| N5 GPU/PCI | GPU preview occupies `Slot_GPU_T` and `Slot_GPU_B` together. A fresh install screwed in two screws and reconnected cables automatically (about 0.67 s). On bench 3 a second GPU target turned red because the installed card's cover blocks the HDMI ports. The bench 2 PCI covers had already been removed, so no cover eligibility rule was exercised. | Required-cover rules with covers present. |
| N6 storage/PSU/USB | 2.5" SSD (behind the motherboard tray), M.2 (two connectors with **identical names** on the EVGA board, distinguishable only by sibling index) and a PSU swap all connected or disconnected every cable automatically. The physical USB stick took a hold to insert (about 0.14 s) and a hold to remove (about 0.2 s), with no orientation step. | Powered callbacks (phase 5) and software/clone effects (phase 6). |
| N6 fans | After the commit hold, the fan stays inside `InstallingPartState` running `OrientComponentStep`/`DoOrientation` every frame (about 4–5 s in the runs) while the wheel rotates it. The click that ends this step sets the final direction, so direction is known only at `PostChangePart`. The saved direction is the fan transform's local Y rotation: 0° exhaust and 180° intake on the rear mount. It survived save/reload; the free wheel tilt did not. `Fan.Direction` read `BACKWARD` for every fan and is not the intake/exhaust value. After a fan commit the game immediately re-previews the same SKU into the remaining empty mounts. | Other fan mounts' angle convention; radiator fan direction. |
| N8 delayed assets | Every normal selection placed its preview within the same `InstallButtonClicked` call (about 0.1–0.12 s after the click), including a RAM model not used earlier in the process. There is no gap in which normal input can cancel before placement; Esc after selection removed the preview cleanly and left the slot empty. | The obsolete-callback negative still needs a forced path (for example a remote request in 4d); `p4-install`'s asset-ready revalidation remains the probe's only cold path. |

Probe changes made during these runs (opt-in only): `-Resume` launch for fresh-process Continue; report rows `fans[]` with name, `Direction`, blow vector, local Euler angles and world forward; hooks on `InstallingPartState.OrientComponentStep`/`DoOrientation` and `Fan.FlipFanFlow`/`SingleSidedFanFlip`. A first build also hooked `Fan.set_Direction` and `Fan.CheckFanOrientation`. The setter is a one-instruction body shared with unrelated classes (it logged `PcUserSharingHud` receivers), and `CheckFanOrientation` runs every frame for every fan (about 200,000 events and 350 MB in minutes). Both were removed; the oversized log is retained as `events-upto-fanhooks.jsonl`. Release build 0 warnings/errors; **353/353** checks after these changes.

Evidence: C4b01 reports `baseline`, `n1-*`, `n1b-*`, `n1e-*`, `n1f-*` and screenshots `c00`–`c51`. C4b02 reports `n3-presave`, `n3-reloaded`, `n3f-presave`, `n3f-fresh`, `n2-*`, `n2b-*`, `n2c-*`, `n4-*`, `n56-done`, `n6-*`, `n6fan*`, `n8-*`, `usb-*` and screenshots `c00`–`c32`, under each run's `host/` directory and `.local/phase4-<RunId>/screens/`. Report numbers restart with each process. C4b01's stop check flagged owner-save changes that the owner's own co-op session made an hour after the probe's last event. The probe only wrote its disposable folder (`.local/phase4-C4b01/stop-note.txt`). Every C4b02 stop, including all four launches of the run, found the owner's 12 save files unchanged.

### Gap checks with normal controls (C4b03)

October 27, 2026. One isolated host, the same Free Build fixture plus an empty `Case_Kolink_4` (Kolink Stronghold) loaded onto bench 1 with `p4-case:1:Case_Kolink_4`. Its seven PCI covers were all fitted. The owner performed every gesture except G6. Each reload was a same-process `identity-resume` with physical input blocked, and every one passed its identity check. These remain single-game native observations; no P4-01–08 case is claimed.

| Check | Normal-control result | Remaining boundary |
| --- | --- | --- |
| G1 AM4 socket | On `Motherboard_GIGABYTE_20` the CPU lever is an `AM3Slot` pin. One click opens it (`TogglePin` → `OpenSocket`) and one click closes it (`TogglePin` only: `CloseSocket` never fired). Selecting `CPU_AMD_9` previews it into `Socket_AM4/Slot_CPU`. A hold installs it (about 0.15 s, no drag or alignment step). With the lever closed, install shows the CPU red and removal shows the CPU orange and the lever red. Both presses made **no** native call. Removal with the lever open is an ordinary 0.18 s edit. Reload with the lever closed restored it closed with no socket call. Reload with the lever **open** restored it open through one `AM3Slot.PopOpen`, and it was still open 5 s later. Unlike the N3 RAM clips, nothing closes an open lever over an installed CPU. | None for the stock AM4 lever. |
| G2 air cooler | Coolers tried: `Air_Cooler_Deepcool_2`, `Air_Cooler_bequiet_2` and `Air_Cooler_bequiet_7`. After the commit hold the cooler stays in `InstallingPartState` running `OrientComponentStep`/`DoOrientation` every frame (seconds to tens of seconds) while the player click-drags to rotate it. `m_accept` turns true while the button is down and back to false on release. The edit commits on a press held to the usual threshold (the final press lasted about 0.29 s). `m_dir` stayed 1. Esc and right-click during orientation each called `HandleExit` (13 calls) and did **not** cancel; the only way back is to commit, then remove. Only two positions exist: fan local Y 0° (exhaust toward the rear) and 180° (toward the front). Both survived reload, re-applied by `Fan.SingleSidedFanFlip` at load. No backplate, screw or cable step appeared. | None for single-tower air coolers; dual-fan or offset coolers were not tried. |
| G3 standoffs | Motherboard removal requires removing the cooler and the CPU first. Board removal unscrews the eight `screwStandoff*` (about 2 s) and then lifts the eight `Standoff(Clone)` with the board (about 2 s). Install reverses this: standoffs at commit start, then screws, about 4 s in all. Everything is automatic inside one edit, so no partial standoff state is reachable. All 16 rows reloaded at rotation and offset 0. The owner also swapped to `Motherboard_ASROCK_2` with the same pattern. | None. |
| G4 PCI covers | With all covers fitted, a GPU target turns red and points at the covers, and the press makes no native call. This case's cover lock is a `Pin` (`PCI_Cover`): one click opens it, and a hold slides out each cover as its own short removal (`PCI_Shield_02` and `_03`, about 0.13 s each, for the two-slot `GPU_MSI_8`). Then the lock is closed again. Removed covers go to the inventory. The GPU then installed in `Slot_GPU_T` with two automatic screws (about 0.65 s). After commit the game re-previewed the same SKU into `Slot_GPU_B` (as after a fan commit), and starting the next edit cleared it. GPU removal (about 0.7 s) leaves slots 2 and 3 open. Covers never return by themselves, and the open slots survived reload. | Other case cover mechanisms (screwed covers, bespoke brackets). |
| G5 accessories | 3.5" HDD (`HDD_Seagate_1`): the target is refused, with no native call, until the right side panel is removed (two screws, automatic). Then it installs in `Slot_3.5bay_03` with no screws (about 0.16 s). The custom loop (Piping tool, tubes/fittings, pump/reservoir, water block) and the cable accessories in the catalog (`CABLE_CONNECTORS` combs, `POWER_SPLITTER`, `POWER_ADAPTER`, `CABLE` styles) were **deferred by the owner**: the normal inventory (`1`) does not offer the cable accessories, and they likely belong to another tool or mode. | **Deferred dependency gap:** custom-loop edits and cable-management accessories need their own probe before a later phase supports them. |
| G6 obsolete callback | A cold RAM model (`RAM_Gskill_25`) loaded **synchronously**: `install-loading` and the commit happened in the same frame, so no disruption can land. A cold GPU (`GPU_EVGA_16`) loaded asynchronously in 3 frames (about 13 ms), which is too short for a hand or a separate keypress. The disruption was therefore sent in the same command batch. (A) `p4-install` of cold `GPU_PALIT_21` plus a same-frame RAM removal on the same board: `install-discarded` 3 frames later, the slot stayed empty and work mode resumed. (B) Cold `GPU_ZOTAC_21` plus a same-frame `p4-work:2`: `install-discarded`, both GPU slots stayed empty, and the player was working on bench 2. `p4-fit` reports cold GPUs as not fitting because the check needs the loaded model. | This tests the probe's own revalidation (work state, target, generation and occupancy are re-checked when the asset is ready), the pattern 4d must copy. A full parent removal (about 4 s) cannot finish inside a 13 ms load. Starting it changes the state and is caught the same way. Native `InstallPartWhenLoaded` was not forced. |

Evidence: C4b03 reports `baseline`, `g1-*`, `g2-*`, `g3-*`, `g4-*`, `g5-*` and `g6*-*` under the run's `host/` directory. The stop found the owner's 12 save files unchanged.

### Before 4b completes

The owner decided these gaps are checked by hand before 4b is marked complete. All six were run in C4b03 ([results](#gap-checks-with-normal-controls-c4b03)); the owner deferred G5's custom loop and cable accessories.

- [x] **G1** AM3/AM4 socket lever and CPU fit by normal input, with the lever state after reload.
- [x] **G2** Air cooler orientation by normal input: rotate, back out, accept, and the saved orientation. Earlier evidence used `p4-accept` only.
- [x] **G3** Standoffs during motherboard removal and install, and whether any partial state is reachable.
- [x] **G4** GPU install with PCI covers fitted: cover eligibility, refusal and where removed covers go.
- [x] **G5** Custom-loop (piping, reservoir/pump, water block) and other standard accessory edits. Untestable families are recorded as dependency gaps. HDD checked; custom loop and cable accessories deferred by the owner.
- [x] **G6** Forced obsolete-callback negative: a cold `p4-install` disrupted by leaving work or removing the parent must place nothing.

## Task 4c model foundation

October 27, 2026. Started at the owner's request after 4b completed. The **initial pass** implemented the Unity-free resource/claim/persistence layer, from a clean working tree, without launching or deploying the game. The owner then requested continuation; [native binding and capture validation](#task-4c-native-bindings-and-capture-guard) below supersedes that initial implementation boundary. Component edits remain unsynchronized.

### Implemented model contracts

- [`AssemblyResource`](../../src/PCBS2Coop.Network/AssemblyResources.cs) assigns a global, non-recycled `NetObjectId` to each slot or mechanical resource. A resource belongs to its PC and a **physical parent part ID**, with a binding discriminator and native type. The adapter must supply typed-array indices or relative hierarchy paths including sibling indices; a bare duplicate name (C4b02's M.2 example) is insufficient. That native discriminator mapping is not implemented by this pass.
- A slot records an optional physical occupant. Multiple slots may refer to one occupant (AIO/proxy coverage). Mechanics record settled open state, rotation and offset. These are model fields, not evidence that every native mechanic persists those values: 4b found automatic screws/standoffs and occupied RAM latch normalization. Native adapters must mirror those contracts rather than expose unsupported partial actions.
- Dependencies identify **shared read guards** on the same PC; `Coupled` identifies resources changed together and acquired exclusively. Claims expand across occupants, their coverage aliases, coupled resources and descendants of a targeted parent. A child does not acquire its parent, so independent slots on the same board remain independent, including when they both rely on the same unchanged panel. Editing that panel conflicts with its readers. Guards do not recursively acquire the guards of a different gesture. A parent removal acquires its complete subtree and conflicts in either acquisition order. Native fit and live blocker eligibility remain separate checks in 4d/4e.
- `ActivityWorld.Canonicalize` publishes the host's sorted expanded resources **and binding stamp**. Both peers check the stamp before applying the reservation. `Owns` checks actor epoch, acquisition token, PC, exact expanded resource set and current binding stamp immediately before later native work. The stamp includes the world, affected part IDs/native paths, parent incarnations and resource facts. A peer with different facts reports divergence instead of authorizing a different callback from the same mutation.
- Every accepted occupancy, mechanical or dependency change must advance its resource `Version`, even when returning to an earlier state. This rejects stale callbacks after an empty/filled/empty cycle. Replacing a same-SKU parent retires its old slot/mechanic IDs and allocates fresh IDs. A live ID cannot change object kind, PC, part SKU/type or resource binding; versions/tombstones/allocator cannot move backwards within a world.
- `ObjectWorld.Replace/Read` validates a complete candidate before publication: local parent/occupant/dependency references, acyclic ownership, unique parent-relative bindings, finite mechanical values, collection bounds, complete allocation accounting and immutable physical identities. Resource/dependency lists are copied before publication. Per-PC limit: 4096 resources; expanded claim limit: 512 resources. An oversized dependency closure is refused atomically.
- Object snapshots and activity state include resources, versions, read/write access, dependencies and binding stamps. The hash-matched native save and carrier payloads retain full native PC data; these fields are not reduced to the resource model. PC deletion/replacement retires resource IDs too. The initial pass only preserved an existing resource manifest; the continuation below adds native population and validation.

### Version and restoration boundary

The single mod version is **0.12** because object state, activity state and canonical edit reservations changed. `CoopSavePair.Decode` now explicitly refuses previous versions before invoking the incompatible world readers; the former 0.8–0.10 compatibility exception no longer applies. Existing files are untouched. A 0.12 test needs a new disposable baseline; older co-op worlds need the explicit phase 8 migration work. No historical format is silently reinterpreted or assigned new identities.

### Validation and remaining work

**Model/network evidence only:** `dotnet run --project tests/PCBS2Coop.Network.Tests -c Release --no-restore` passed **401/401** checks. Tests cover disjoint/contested resources, multi-slot AIO aliases, cover/latch guards, parent/child acquisition in both orders, actor replacement, exact claim revalidation, same-SKU parent replacement, state-version rollback, mutable caller lists, malformed/truncated candidates, forged/stale saved binding stamps, host/guest stamp disagreement, existing PC deletion, snapshot hold/replay and hash-matched paired-save reconstruction. The paired-save fixture uses synthetic native bytes; it does not prove the game loads native resource bindings. Release solution build against real generated interop passed **0 warnings, 0 errors**, using `-p:DeployToGame=false`. Local logs: `.local/phase4c-tests.log` and `.local/phase4c-build.log`.

The initial pass left these items **inside 4c**, addressed by the continuation below:

1. Populate and validate the host's slot/mechanic manifest from 4b's actual typed arrays and parent/prefab paths. Bind to local native objects without allocating by SKU or name, distinguish previews from committed occupancy, and check rebuild/parent replacement boundaries. Include stored/carried PCs and native load reconstruction.
2. Preserve and compare the native part graph, aliases, installed orientation and required side effects without reducing full native data to resource facts. Cable state needs a separate check: N2 proved an interrupted GPU removal can change connections while leaving the topology fingerprint identical. Custom-loop/cable-accessory editing remains the owner's deferred dependency gap.
3. Replace permissive save-time adoption of supported native component edits with validation against accepted mutations. Keep initial baseline capture, explicit generated load growth and `/sync` recovery boundaries distinct. The existing same-SKU/path capture heuristic is still present; this pass does **not** claim to fix native replacement detection.
4. Run native binding/capture checks in owned disposable game instances, then update 4c's completion evidence. P4-01–08 two-peer component acceptance remains pending for 4d–4f. No new UI/flow or synchronization scope decision was needed for the model foundation.

## Task 4c native bindings and capture guard

October 27, 2026. Continued at the owner's request. **Task 4c is complete (0.12).** No install/remove or mechanical gameplay adapter is claimed. Those host-ordered intents and projections remain tasks 4d/4e; P4-01–08 remain pending.

### Native implementation

- [`NativeAssembly`](../../src/PCBS2Coop.Plugin/NativeAssembly.cs) enumerates actual loaded `Slot`, `CPin` and `Screw` components on the main thread. A binding is relative to the nearest **mapped physical part**, with sibling and same-object component indices. It excludes the workshop, bench and owning prefab root name. Native component records resolve through the accepted `ComputerSave` field/alias graph; lookup never chooses an installed part by SKU or bare name.
- Built-in case pieces can save as boolean/list membership instead of standalone part records. Some have no `ComponentPC.Slot` backlink or no `PartInstance` at all; variants can share a prefab with another SKU. The adapter uses the **observed containing slot** and its native case-part type, recording presence as `IntegralPart`. The same representation records generated radiator fans that have no saved `FanId` record, beneath their known radiator's typed child slot. Saved standalone fan records retain their ordinary part IDs. These are explicit child-presence facts, not invented saved part records.
- `RAMSlot` is a blocker group, not a `Slot` subclass: references expand to its actual RAM slot and clips. RAM clips are coupled to the owning slot, and an installed component's `GetScrews()` supplies its actual affiliated screws. `m_requiresStandOff` usually references a shared **prefab asset**, so it is never treated as a live resource: automatic motherboard work guards the physical parent's actual standoff instances. Unknown blocker types/bindings fail explicitly.
- Initial generation capture allocates the first manifests before admission. Later first loads use host-only command type **6**, [`AssemblyManifest`](../../src/PCBS2Coop.Network/AssemblyManifest.cs): the host validates and allocates one whole manifest, and both peers receive identical IDs and profile facts. Repeated bindings cannot reallocate. Relative-path data is compressed under the existing command limit and capped at 1 MiB expanded before acceptance/decoding. Record-only PCs with no prior materialization retain an explicit unknown manifest until the host loads them; a loaded PC without a manifest cannot be saved with its slots treated as known.
- `Slot.InstantiatePreloadedPart` observation records preview creation, including nested creation context, rather than guessing from apparent slot occupancy. Native loading/edit states and previews prevent capture. A waiting guest's snapshot is deferred while a selection is active. This is observation and a capture barrier, not component synchronization.
- An existing manifest validates resource count, owner/binding/type, occupancy, integral presence, mechanical state and dependency/coupled edges. Local projection bindings also retain actual slot/occupant component instances: replacing a RAM stick with an identical SKU in the same slot cannot inherit the old identity at normal capture. After an accepted PC load/move, a settled explicit boundary validates and rebinds the new projection. No per-frame `SaveComputer` polling was added.
- `PcBuildProfile` separately hashes installed orientation/flip, native case membership and **internal cable connections** (endpoints, style/connector/material and native connection flags). Internal endpoints are scoped to connectors belonging to the case hierarchy; external bench connections remain phase 5. The profile travels in the object snapshot and manifest mutation. Cable route geometry and later electrical/simulation/software fields remain in the complete native save, not reduced to this hash; their live editing/validation stays in the owning later phases.
- Ordinary capture now validates accepted PC/part topology, aliases, manifests, native instances and build profiles. It refuses unbound native PCs, missing accepted PCs and unconfirmed changes before writing the save/sidecar; a stable divergence reports out of sync. Explicit first-load growth is limited to catalog PCs whose manifest is still unknown, so it cannot later adopt an arbitrary USB/component addition to a bound PC.
- `/sync` remains the explicit host-reference repair boundary. It clears old callback authority through accepted actor-presence changes before capturing a repaired baseline. Unchanged resources retain IDs; changed facts advance versions; replaced physical parts/parents receive fresh IDs and retire affected old IDs. Before saving, occupied RAM clips are closed synchronously to match the native reload contract proven in 4b N3. A normal save does not get these repair permissions.

### Running-game evidence

All actions below used named owned processes and disposable probe saves. Direct native calls and pinned accepted requests are **not ordinary-input component synchronization evidence**.

| Runs | Result |
| --- | --- |
| C4c01–C4c04 | Retained development failures identified integral panels, generated radiator fans, `RAMSlot` blocker groups and standoff-prefab references. Each run was finalized; owner saves unchanged. |
| C4c05 | Bound both default PCs (330 resources total); an unchanged paired save and same-process Continue passed exact object identity. An unconfirmed open RAM clip refused the save; native file and sidecar hashes stayed unchanged. This run predates the integrity-profile extension. |
| C4c06 | Owned host + guest, with integrity profiles. Late join bound both PCs and both peers had object hash `76D2D7E20DB3C05209103415CA661455C578DD15A392611D39FE8EA3DB885FF6`. A refused latch save left both files unchanged; `/sync` loaded generation 2, cleared both markers and restored the same hash. Host pickup/store, guest retrieve/drop and subsequent paired saves passed; the final two-peer object hashes still matched the baseline. Both owned processes closed, all 12 owner saves unchanged. |
| C4c07–C4c08 | Retained new-case failures exposed a shared-prefab integral piece without a usable `ComponentPC.Slot` backlink. Fixed by observed containing-slot mapping and treating unmapped structural ancestors as transparent, while actual occupants remain strict. Both runs finalized; owner saves unchanged. |
| C4c09 | Accepted catalog creation of `Case_Kolink_4` published 60 resources as the next host mutation (revision 4, allocator 457), then saved/stored it. Replaced `RAM_Gskill_29` in the same slot with a fresh same-SKU record: refreshed native topology matched, but the physical-instance guard refused the save. Native file, sidecar and object-model hashes stayed unchanged. `/sync` repaired with fresh RAM/mechanic IDs (allocator 459). Disconnecting internal cable `501` → `114` kept part topology valid but failed the build-profile check; both files stayed unchanged. `/sync` restored that host-reference state at generation 3. Run closed; all 12 owner saves unchanged. |
| C4c10–C4c11 | Preview fixture setup through `PopulatePlaceholders` produced no preview, so these runs do not count as preview-barrier passes. Both runs finalized; owner saves unchanged. |
| C4c12 | After caching an SSD asset, the diagnostic called the actual `InstantiatePreloadedPart` path with `Placeholder=true`; the tracked preview was present. Capture refused with “Put the selected component away before saving or joining”; native file and sidecar stayed unchanged. Diagnostic preview cleanup restored normal saving with native checks passing and no out-of-sync marker. Closed the process, then resumed that exact paired save in a fresh process: model hash `8D8CA98540645A53959D9572A0573046B134889FF24B1F2CD4259C2BAB7E25B1`, world ID and allocator 393 were unchanged, native bindings passed, and the marker remained clear. Both owned processes closed; all 12 owner saves unchanged. This direct-call fixture does not claim an ordinary inventory-click acceptance case. |

The actual captured default-board manifests also passed the model's disjoint-RAM check: a RAM edit writes its own physical part/slot/clips (and integral child mechanics), without reserving the whole motherboard. Local summaries are `.local/phase4-C4c06/manifest-summary.json`, `.local/phase4-C4c08/manifest-summary.json`, and the replacement/cable result files under `.local/phase4-C4c09/`. Raw reports/events and native saves remain under each named probe's evidence directory.

### Validation boundary

Current model/network suite: **418/418** checks, including shared-read-guard snapshot restoration, host-only first-load manifest publication with held/replayed mutations, integral presence and profile round-trip, same-SKU parent retirement, exact callback authority, explicit repair versions and rejection of a compressible oversized manifest before allocation. Release solution build against real interop: **0 warnings, 0 errors**. Latest logs are `.local/phase4c-tests-final.log` and `.local/phase4c-build.log`.

Preview-barrier and fresh-process results are recorded in `.local/phase4-C4c12/preview-result.json` and `fresh-result.json`; the first process's reports were retained under `pre-resume-host`. All owned C4c01–C4c12 runs are finalized, with owner-save hashes unchanged. No commit or push. The remaining broader category/variant, native commit/cancel, callback, inventory and presentation acceptance belongs to 4d–4f; custom-loop/cable-accessory editing remains the owner's explicitly deferred gap.

## Task 4d: host-ordered latches

October 27, 2026. Started at the owner's request after 4c was committed. **Status: implemented (version 0.13); model-tested and owner-checked in runs C4d01–C4d08** ([results](#owned-game-checks-c4d01c4d08-october-27-2026)). The scope grew to [case panels and an unshared-edit gate](#case-access-and-the-unshared-edit-gate-owner-decisions-october-27-2026). Other component install/remove is refused in co-op until 4e.

### Owner decisions (October 27, 2026)

- **Scope:** 4d covers every click-toggled latch: RAM clips, AM3/AM4 CPU levers, the LGA shield, M.2 covers, PCI cover locks, heatsink pins and hinged doors. Side/front panels and PCI shields are removals through the component edit lifecycle, so they belong to 4e. Screw-held covers (`ScrewPin`) are refused with a notice until a case that has one is probed. The default workshop and the C4b03 Kolink case have none.
- **Open clips over installed sticks:** follow the game. The native load closes them (4b N3), so they are live-only state. Before every host save and snapshot, the host closes them on both games. A PC that leaves its bench has them closed in the shared state, because its next load closes them natively. A clip on an empty slot stays open across saves and moves.

### Native findings this task depends on

All of these were read in the decompile; none is running-game evidence yet.

- **One gesture.** The player's latch click is `WorkingOnComputerState.PinTick`, and in the Cable tool `LayCableState.PinTick`. On a press, `PinTick` calls `IPin.TogglePin` directly, or for a latch whose `RespondsToLongClick` is true it registers a `GameController.DoAfterLongPress` callback (`<>c__DisplayClass51_0/32_0.<PinTick>b__0`) that calls `TogglePin` later. No other method dispatches `IPin.TogglePin`. `SaveLoadSystem.LoadComputerToSlotAsync`/`OpenPins` use `IsOpen`/`PopOpen`, and the vacuum's `GetPanelAsPin` only reads.
- **Toggle semantics.** `Pin.TogglePin` does nothing when `IsBlocked()` or its animation coroutine is still set; otherwise it starts `PinAnimation`. `AM3Slot.TogglePin` checks `IsBlocked`, then starts `OpenSocket` or `CloseSocket` unless it is animating. `LGASlot.TogglePin` checks `IsBlocked` and sets the opening/closing flags its `Update` animates. Every refusal is silent, which matches 4b's "a toggle call is an attempt, not a result". `IsBlocked` reads live state: blocking components, water connectors and USB blocking.
- **Direct state.** `Pin.Close()` closes instantly (it sets the transform and clears the open flag). `PopOpen()` opens through the animation enumerator without input. Neither socket class has a direct close.
- **Latch classes.** `CPin` subclasses are `Pin`, `ScrewPin : Pin`, `AM3Slot` and `LGASlot`; each declares its own `TogglePin`.
- **Internal toggles.** The game's internal `TogglePin` calls (an install closing its clips, the load's closure of clips over installed RAM) run outside `PinTick`, so they pass through unchanged.

### Implemented contracts

- [`MechanismWorld`](../../src/PCBS2Coop.Network/Mechanisms.cs), command type **7**:
  - **Toggle** names the actor epoch, PC, latch resource, the version the requester saw and the state asked for. The host refuses it in these cases:
    - the requester is not working on that PC, or the PC is not on a bench;
    - the resource is not a pin or socket, or is a linked latch (coupled child pins, none in the default workshop);
    - the version is stale ("That latch was just changed");
    - the PC has a pending move;
    - another player's edit claim covers the latch or a part it guards (that claim's holder may still toggle it);
    - the host's game reports the latch missing, moving, blocked, shown differently or screw-held.
  - **Settle** is host-only. It lists exact (PC, latch, version) rows of open latches over occupied slots and closes them. It is refused while any pending claim covers one of them.
  - On both peers, `Apply` repeats the model checks against the same revision; a difference is a divergence.
- [`AssemblyResource.LoadCloses`](../../src/PCBS2Coop.Network/AssemblyResources.cs): a slot lists the latches the native load closes while it is occupied. `NativeAssembly` fills it from the slot's `RAMSlot` clip groups, the exact set 4b N3 observed. It is validated (slots only, pin/socket members of the same PC, distinct) and serialized in object state and manifests. Changing it requires a version advance. `AssemblyResources.SettleLoad` closes them deterministically; `PcTransfers.Apply` uses it for every PC leaving a bench.
- [`NativeMechanisms`](../../src/PCBS2Coop.Plugin/NativeMechanisms.cs):
  - **Gesture boundary.** A Harmony prefix/finalizer on the two `PinTick`s and their long-press callbacks marks the gesture (each native function hooked once). Every `TogglePin` implementation is hooked once. A toggle inside a gesture that would do nothing natively (blocked or still moving) passes through. Otherwise it becomes a request and the native call is skipped. Repeats of a held button while that latch is pending or still being shown are dropped silently.
  - **Showing an accepted toggle.** Both games run the native animated toggle inside `NativeProjection.Run`. Where it is blocked on that game only, the state is set directly (`PopOpen`/`Close`). A toggle that can't be shown within 5 s reports `ProjectionFailed`. A newer accepted state replaces one still being shown, and a settle that arrives during an animation waits for it, then closes.
  - **Save and snapshot barrier.** A pending or still-moving latch makes `NativeAssembly.CanCapture` false and `CheckStable` refuse: snapshots wait, and a refused save is not reported as out of sync. The deferred drop autosave waits too. `BeforeHostSave` and `TakeSnapshot` call `SettleForCapture` before the capture. `/sync` keeps its existing native close (`PrepareReload`) and repair capture.
  - **Rebind check.** `NativeAssembly.Update` no longer faults when a freshly loaded PC is still running the native load's latch animation; it checks again later.
- Probe (opt-in, `PCBS2COOP_PROBE=3`/`4`): `latch-report` writes every latch's shared and native state, plus the object hash. `latch-request:<pc>:<latch>[:version[:open]]` submits a pinned request for race and stale cases (not gesture evidence).

### Model evidence

`dotnet run --project tests/PCBS2Coop.Network.Tests -c Release --no-restore`: **466/466** checks (26 in `MechanismTests`, 22 in `PanelTests`). They cover:

- requester and observer showing a toggle once;
- a contested lever and disjoint latches on one PC;
- refusals: not working, linked, a slot named as a latch, another PC, stale epoch;
- claim interaction (the other player's claim refuses, its holder is allowed);
- a host-native refusal, malformed and trailing payloads, duplicate delivery, hold/replay after the snapshot mark;
- settle: host-only, waits for a claim, closes only occupied-slot clips on both games, refuses a stale repeat;
- pickup settling clips, and a carried PC's latches refused;
- `LoadCloses` round-trip and validation.

Release solution build against real interop: **0 warnings, 0 errors** (deployment disabled). Version **0.13**: new command type and object/manifest field; older co-op pairs are refused until phase 8 migration.

### Planned owned game checks (as first planned; results below)

Two owned instances, disposable saves, companion-style (the owner clicks; Claude launches, reads `latch-report`/`summary-report` and logs).

| Check | What passes |
| --- | --- |
| D1 bind/join | Both games' `latch-report` and object hashes match after late join; no `[hooks]` error; startup logs which gesture/toggle functions share code. |
| D2 toggles by hand | Each player opens and closes a RAM clip, an M.2 cover, the LGA shield and an AM4 lever. The other game shows the animation, and both reports match. One toggle from the Cable tool. |
| D3 contested/stale | Both players click one lever together, or a pinned stale `latch-request`: one change lands and the other player sees the refusal notice. Repeated held clicks send one request. |
| D4 blocked | An M.2 cover under a GPU: no request, no change. A pinned request for a blocked latch is refused by the host. |
| D5 settle | Open a clip over an installed stick, then a host save, late join and pick up/drop: the clip closes on both games, a clip on an empty slot stays open, and the save/sidecar pair and Continue match. |
| D6 interruptions | A toggle during the other player's join, a disconnect mid-animation, fresh-process Continue and `/sync` after a forced fault; out-of-sync is reported only for real faults. |

### Risks to watch

- `PinTick` runs every frame in work mode; the prefix/finalizer only adjust a counter.
- The host as requester runs the native toggle re-entrantly inside its own intercepted `TogglePin` (accepted synchronously).
- A native autosave that does not go through the deferred path is refused while a latch moves (SAVE FAILED until the next save).
- An install's automatic clip closing is a component edit and stays unsynchronized until 4e, so captures still refuse it, as in 4c.
- `LoadCloses` covers only `RAMSlot` clip groups. Other `closePins` load behavior is unprobed.

### Case access and the unshared-edit gate (owner decisions, October 27, 2026)

The first game runs showed that RAM clips and M.2 covers are reached by taking a side panel off, and an unshared panel removal put the host out of sync (C4d01, C4d02). The owner then decided:

- **Case panels are shared in 4d.** This covers the integral parts in `CASE_PART_REQUIRED`/`CASE_PART_OPTIONAL` slots: side, front and top panels, filters and the drive bays some cases build in. PCI covers (`PCI_COVER`) stay in 4e.
- **Every other install or removal is refused in co-op** with a notice, before it starts, until 4e shares it.
- **Clips over installed sticks still close at every host save,** even while someone works on that PC (owner: keep as is).

Implementation:

- [`CasePanelWorld`](../../src/PCBS2Coop.Network/CasePanels.cs), command type **8**: remove, or refit with the slot's own panel SKU, naming the version the requester saw. Refused when:
  - the requester isn't working on that PC, or the slot isn't a panel slot (no `IntegralHome`);
  - the version is stale, or the SKU belongs to another slot;
  - something is mounted on the panel;
  - another player's claim covers the panel or anything it carries or guards, or the PC is moving;
  - the host's game can't do it (`CanBeUninstalled`, `IsUninstallingPathBlocked`, `CanFillWithItem`, panel asset not loaded).
- **Model.** `AssemblyResource.IntegralHome` (the panel SKU) and `Carries` (the screws, latches and slots physically on the panel, from `Transform.IsChildOf`). While a panel is off, what it carries is **dormant**: kept with its IDs, expected absent natively, restored on refit with fresh mechanical state and advanced versions. A latch on a removed panel can't be toggled. `/sync` repair keeps a removed panel's home and dormant resources.
- **Bindings.** Inside a fitted panel, paths use a fixed `@panel` token: a refitted panel is a new object named after its part prefab (`Lid_Left` at load, `Case_MSI_6` after a refit; C4d07).
- **Showing a change.** The requester re-runs the game's own long-press callback (animation, automatic screws, inventory) if its work state is unchanged. The other game changes the panel directly:
  - removal: `OnChangePart` → `SetComponentNull` → `DestroyImmediate` → `UpdateGameStatus`/`PostChangePart`, plus `AddItemToInventory`;
  - refit (the load path, `DoAutoInstantiation`): the part from Removed Components or a new instance → `InstantiatePreloadedPart(LoadingPC: true)` → `SetComponent` → `OnChangePart` → `RebuildSlots`/`FixupBlockers` → `UpdateGameStatus`/`PostChangePart`.
  - The PC is rebound only after the slot shows the result and no edit state is running (the requester's edit starts as a coroutine; C4d03), with a 20 s limit.
- **Gate** ([`NativeCaseAccess`](../../src/PCBS2Coop.Plugin/NativeCaseAccess.cs)): Harmony prefixes on the long-press callbacks, each native function hooked once.
  - `DisassemblyTick` b__0/1/2 (removal) and b__3 (water pipe): a panel removal becomes a request; anything else is refused.
  - `AssemblyTick` b__0 (install commit): a panel refit becomes a request; anything else is refused.
  - `ItemDisplay.InstallButtonClicked` while working on a case: only that case's removed panels can be selected. Peripheral swaps and case spawns are untouched.
- **Manifest fix (affects 4c).** `Slot.m_dynamicBlockedBy` and `m_dynamicComponentCollidesWith` are live caches the game fills only after someone works on that PC on that game. C4d06 reproduced a host save refused at bench 2's CPU slot: the host had captured an empty list at startup, and after work mode the water block appeared. They are no longer manifest guards. Live blockers are checked by the host's game when a request is validated; 4e needs its own live blocker check for component edits.
- **Mismatch messages** now name the differing field (shared vs native).

Version **0.13**: command types 7 and 8, `LoadCloses`, `IntegralHome`, `Carries` and the `@panel` binding change object state and manifests.

### Owned game checks C4d01–C4d08 (October 27, 2026)

Two owned instances on disposable saves. The owner did the in-game actions; Claude launched the instances and read the reports. The owner's 12 save files were unchanged at every stop.

| Run | Result |
| --- | --- |
| C4d01 | **D1 passed** (late join, identical object/activity hashes, 25 latches). Guest M.2 cover and RAM clip toggles shown on the host. Then the host's game removed bench 2's side panel by accident (unshared), and the save check refused it. |
| C4d02 | D1 passed again. **D2:** guest clip open/close and M.2 cover, and a host DDR3 bracket, all shown on both games. The Cable tool does not toggle RAM brackets (latches use the Remove tool). Panel removals on both games were unshared, so the host's save check refused them; this led to the scope change. |
| C4d03 | First panel build: the host's projection worked, but the guest rebound before its own animated removal ran (fixed: wait for the result). |
| C4d04 | **Guest panel removal shown on the host;** both clips; RAM removal and GPU selection refused with notices. A host save then failed at bench 2's CPU slot. |
| C4d05–C4d06 | Solo reproduction: projected removal and work mode alone passed; leaving work mode after working on bench 2 reproduced the failure, which named the dynamic-blocker guard (fixed above). C4d05 also locked the UI by starting probe work mode under the welcome dialog (probe setup error, not the mod). |
| C4d07 | With the fix, the same sequence saved cleanly with clips settled. **D3:** a stale latch request was refused, and a same-moment race on one cover produced one change and one refusal. **D4:** the cover under the GPU was refused by the host. The first refit then failed on the renamed panel object (fixed with `@panel`). |
| C4d08 | **Guest removed, refitted, removed and refitted the left panel; both clips; three refusals** (RAM, GPU), with no fault and matching hashes. Host removed bench 3's left panel and opened a bracket, then **`/sync`**: both games reloaded at generation 2 with matching hashes, the full assembly check passed on both, the panel stayed off with its carried resources kept, and the clips were closed. |

Not run, by owner decision (from now on, code first and the owner tests the whole feature later): a refit by the host after `/sync`, a disconnect mid-animation, fresh-process Continue with a panel off, held-button repeats, screw-held covers (none in the default workshop) and AM4/LGA levers by hand (blocked by the installed coolers).

## Task 4e: shared component installs and removals

### Owner decisions (October 27, 2026)

- **What the other player sees:** the game's own animations, replayed on their game ("directly copy the original game's actions … so users see same animations and style as single player"). A whole-PC reload was offered and declined.
- **Same PC while an edit runs:** the other player waits. Their edits, latch clicks and panel changes on that PC are refused with a notice until the edit finishes. Other PCs are unaffected. This replaces the earlier phase 4 rule that two players may edit different parts of one PC.
- **Inventory:** mirrored, as for 4d panels. Free Build catalog parts are never taken away.
- **Esc during a removal's cable step:** share whatever the editor's game ends with (the part stays, its cables are off).
- **Workflow:** code and model tests first. The owner then tests host and guest and reports issues. Custom loops and cable accessories stay deferred.

### Native findings this task depends on (decompile, unless marked as traced)

- **Gestures.** `DisassemblyTick` registers one of three long-press callbacks on `<>c__DisplayClass68_0`:
  - b__0, when the clicked component is a placeholder (`m_isPlaceholder`): destroys it, then constructs `InstallingPartState(case, camera, part, slot)` (an install);
  - b__1: the direct removal (`Case.OnChangePart(part, true, false)`, `Slot.HandleMGPUBridge(true)` for part types 6/24, `UninstallState`, `SetCurrentState`);
  - b__2: the `Uninstall` coroutine (unplug cables, wait, then the same removal).
  `AssemblyTick` b__0 is the install commit (`ClearPlaceholders`, the install constructor, `SetCurrentState`).
- **Replay engine** (`NativeEditReplay`, milestones 1–3 in this task): runs the native state **detached** (never current) and calls its `m_updateFn` each frame, with guards on `GoToPreviousState`/`GoToLastDifferentState`, `SetCurrentState`, `GetPreviousState`, input reads, the work selection list and the orientation step. Traced in game, runs C4e01–C4e05 (one owned host, owner watching):
  - a GPU removal (1.13 s) and reinstall (0.66 s, the same as a hand install) gave the original record back;
  - a fan replay with 180° matched its neighbours; `m_degrees` is the rotation about the fan's own axis.
- **Settlement.** `OnStateDestroy` connects cables only when the previous state is a work state in assembly mode. The replay passes a stand-in work state with the editor's tool mode, and the editor's cable choice around it. In Free Build the native settlement adds a removed part to the inventory only when it has no catalog entry, so the replay mirrors inventory by itself.

### Implemented contracts (version 0.14)

- **Command 9, `ComponentEditWorld`** ([`ComponentEdits.cs`](../../src/PCBS2Coop.Network/ComponentEdits.cs)):
  - `Begin` (editor): the slot and the version it saw, the gesture (`Install`, `Remove`, `RemoveDirect`), the SKU or the part being removed. The host checks the actor, the PC, the slot, its own game (`IEditNative.CheckHost`) and the waiting rule, then claims the PC for the editor in the same revision (`ActivityWorld.SetEditClaim`, stamped like a 4c claim).
  - `Commit` (editor): what its game ended with (`EditResult`: outcome `Done` or `Unplugged`, orientation angle, airflow flip, tool mode/selection, inventory source, cable type/connector/pins). The other game replays it.
  - `Settle` (host, once its own game shows the result): the PC captured from the host's game, compressed. A part keeps its ID while it is the same native instance, prefab resources keep theirs by binding (`AssemblyManifest.Repair`), removed IDs are retired and the claim is released. Validation dry-runs the full world check (`ObjectWorld.CheckReplace`), so a bad capture is refused instead of diverging.
  - `Cancel`: the editor drops an edit that never ran; the host drops one it can't settle. An editor that leaves mid-edit loses its claim; the host settles from its own game.
- **Waiting rule** in `CasePanelWorld`, `MechanismWorld` and `ComponentEditWorld.Busy`: another actor's pending edit claim on a PC blocks the whole PC ("The other player is changing a part of that PC.").
- **Plugin** ([`NativeComponentEdits`](../../src/PCBS2Coop.Plugin/NativeComponentEdits.cs)):
  - The 4d gate now sends every non-panel install/removal here. Shared slot types: motherboard, CPU, CPU cooler, RAM, PCI, PCI cover, storage, M.2, PSU, case fan, radiator, GPU proxy, USB. Reservoirs, water blocks (`m_includesPipes`), LED strips, power splitters/adapters, VRM and heat-sink slots stay refused.
  - The editor's stored callback runs after acceptance if its work state is unchanged; otherwise it cancels.
  - The edit is followed natively: `SetCurrentState` (its `InstallingPartState`), `OnStateDestroy` (result values) and the `Uninstall` coroutine's `MoveNext` (stopped before the part step = `Unplugged`).
  - The other game replays the edit (from its own inventory when the editor used an inventory item), after clearing its own previews on that PC.
  - The host remembers part instances at `Begin` and settles when the PC is still: no edit state, replay, preview or moving latch.
  - Every PC is rebound after its settle.
  - Saves and snapshots wait while any edit is pending (`NativeAssembly.CanCapture`/`CheckStable`). `NativeActivity` doesn't release a claim an edit holds.
- **Replay additions:** `Plan.Direct` (the b__1 removal). The editor's flip is applied to the part instance before install.

### Model evidence

**493/493** model/network checks (`ComponentEditTests`, plus the updated 4d latch/panel tests for the waiting rule): begin/commit/settle in both directions; the claim and the waiting rule for edits, latches, panels and moves; refusals (filled slot, wrong part, case panel, latch as slot, stale version, host-native refusal, a second edit, a commit by the other player, an install ending unplugged, an infinite angle, a guest settle); new IDs for a stick and its screw; retirement of a removed card and its screws; Esc mid-cable keeping the card; an integral PCI cover; cancel before/after commit; departure after commit; a settle that moves the PC or changes a kept part's identity (refused, no divergence); malformed/trailing bytes; a duplicated commit applied once; round-trip of the settled world. Release build: 0 warnings, 0 errors.

### Owner game test (October 27, 2026)

The owner ran this two-instance checklist as host and guest and reported it passed ("all is good to go"). No logs were collected for this report: GPU, RAM (clips), CPU (direct removal), air cooler (orientation), fans (intake/exhaust: is the flip carried?), AIO (radiator + pump, proxies), motherboard (standoffs, children), drives/M.2, PSU (cables), a PCI cover, USB, Esc during a GPU's cable step, inventory both ways, the waiting rule, late join and save/restart after edits, and `/sync`.

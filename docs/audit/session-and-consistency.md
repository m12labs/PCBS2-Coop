# Session, identity, and consistency audit

October 25, 2026. This is an audit of existing behavior and dependencies of possible future synchronization. No transport redesign, ownership model, UI flow or save policy is selected here.

## Existing source baseline

The only application message is [`MessageType.PlayerState`](../../src/PCBS2Coop.Network/Protocol.cs#L7), containing X/Y/Z, yaw and InWorkshop. [`CoopSession`](../../src/PCBS2Coop.Network/CoopSession.cs#L13) supports one host and one IPv4 client, accepts a mod-version connection key, polls on its caller's thread and sends with `DeliveryMethod.Sequenced`. This does not supply reliable world mutation delivery or a world snapshot.

[`CoopController.Update`](../../src/PCBS2Coop.Plugin/CoopController.cs#L67) polls the session, targets 20 Hz local transform sends and hides a remote view after two seconds without a fresh state. LocalPlayer.Find only returns workshop states with `InWorkshop = true`; leaving the workshop does not currently send a final explicit false state. [`CoopRunner.Tick`](../../src/PCBS2Coop.Plugin/CoopRunner.cs#L41) recognizes exactly `Stage_02_Workshop`, observes scene-name changes, and stops the local session on return to `Main_Menu`. It does not coordinate another player's travel or pause.

[`CoopDialog`](../../src/PCBS2Coop.Plugin/MultiplayerMenu.cs#L229) starts a new Free Build for the host after hosting and for the client after connecting. [`StartNewFreeBuild`](../../src/PCBS2Coop.Plugin/MultiplayerMenu.cs#L98) calls the game's PlayFree, including its overwrite prompt. This initializes separate local worlds. It is not a host world transfer, shared save or proof that each PC starts identically. The audit did not execute that flow.

[`RemotePlayerView`](../../src/PCBS2Coop.Plugin/RemotePlayerView.cs#L11) creates a capsule, visor and role label, removes colliders and interpolates position/yaw. It borrows an HDRP material/font locally. No gameplay object, part, inventory, save, app, animation or economic message was found in the current mod source. The existing [loopback test source](../../tests/PCBS2Coop.Network.Tests/Program.cs#L1) covers connection/basic state behavior; it is not world-sync acceptance and was not rerun for this documentation-only audit.

Evidence for game dependencies is in the linked domain reports. Their declarations identify state shape; only explicitly labeled native observations establish recovered behavior. The matrix below labels implications as audit inferences.

## Factor register

`Existing` means implemented in the inspected source, not freshly tested. `Partial` identifies a limited existing mechanism. `Missing` means no current application-level implementation found. All future behavior is subject to owner decisions; absence is not approval to implement it.

| ID | Factor | Current status | Audit implication / evidence |
| --- | --- | --- | --- |
| SS-001 | Direct IP host/join, role, one-peer capacity | Existing | CoopSession.Host/Join/OnConnectionRequest; no friend discovery/lobby carrier is present. |
| SS-002 | Mod-release compatibility | Existing | Protocol.ConnectionKey; matching this string does not compare game binaries or content. |
| SS-003 | Game binary, metadata, catalog, DLC/workshop and custom asset compatibility | Missing | Parts, slot/connector layouts and screen assets depend on content; [hardware](hardware-and-building.md), [screens](screens-software-and-simulation.md), [career](career-economy-and-persistence.md). |
| SS-004 | Connection failure/full/timeout reason and retry UI | Existing | CoopSession.OnPeerDisconnected, CoopDialog.Update; error handling is distinct from state recovery. |
| SS-005 | Remote actor timeout/removal and peer disconnect cleanup | Partial | CoopController.DestroyView; no held world objects, tool loops or operation ownership yet to release. |
| SS-006 | Reconnect transport versus restoring current world | Partial | Host keeps listening; new client receives subsequent player snapshots only, no world snapshot. |
| SS-007 | Host departure/session end/return-to-menu policy | Decision pending; Missing shared behavior | Local Stop is implemented; no coordinated client end/travel message. |
| SS-008 | Common mode, save, workshop, scene and content manifest | Missing | CoopRunner hardcodes one scene; [LevelLoadPersistency scene constants](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/LevelLoadPersistency.cs#L267) identify additional scenes. |
| SS-009 | Travel, load, restart, new game and shared-world reset | Missing | Menus start independent new Free Builds; no shared command or acceptance barrier. |
| SS-010 | Ready barrier for scene, parts, cables, screens and decorator async loads | Missing | Active scene name is insufficient; [player/workshop P5/P8/P9](player-workshop-and-presentation.md#evidence-groups). |
| SS-011 | World/session epoch to reject traffic from prior load/reset | Missing | PlayerState has no scene, tick, epoch or load identity. |
| SS-012 | Stable player identity and actor-to-operation attribution | Missing | Host/Client labels are presentation roles, not durable actors across reconnections. |
| SS-013 | Stable IDs for PC instances and duplicated same-SKU parts | Missing | [Hardware identity risks](hardware-and-building.md#identity-concurrency-and-restoration-risks); part catalog identity cannot distinguish identical RAM sticks. |
| SS-014 | Stable bench, slot, connector, cable/pipe endpoint and peripheral IDs | Missing | IDs must survive scene rebuild and link to the same logical PC. |
| SS-015 | Identity for jobs, orders, deliveries, emails, notes and sale listings | Missing | Economic event replay must refer to the original record; [career report](career-economy-and-persistence.md). |
| SS-016 | Ownership / who may request, validate and commit world changes | Decision pending; Missing | Host/client transport roles alone do not enforce mutation authority. |
| SS-017 | Shared versus personal wallet, inventory, progression, OS desktops and tablet | Decision pending | Audit inventories both possibilities; no existing sync covers either shared version. |
| SS-018 | Concurrent edits to one PC/part/cable/bench/window | Decision pending; Missing | Reserve/serialize/merge policy is not selected; conflicts can invalidate install/route/purchase preconditions. |
| SS-019 | Independent activities at separate benches | Missing | GameController/WorkshopController singleton activity fields must not become a global remote camera/state stack. |
| SS-020 | Request identity and exactly-once application | Missing | Checkout, sell, package opening, install, damage, job completion and callback replay can duplicate results. |
| SS-021 | Ordered, reliable delivery of accepted world mutations | Missing | Current sequenced transform stream can discard stale packets; a lost event cannot be repaired by a newer unrelated event. |
| SS-022 | Baseline snapshots, deltas, revisions and resync | Missing | Late join needs a coherent current state; no world serialization handshake or baseline ack exists. |
| SS-023 | Dependency-aware reconstruction | Missing | Parts require PC/slots; cables require endpoints; software requires correct PC/storage; displays require simulation and assets. |
| SS-024 | Creation/deletion, transfer and object incarnation | Missing | Delete/recreate under one scene path must not revive old commands; carried/benched/stored PCs remain one logical item. |
| SS-025 | Rollback/reject/cancel and stale async completion handling | Missing | Rejected command, interrupted load and superseded asset completion must release UI/tool locks and preserve item state. |
| SS-026 | Safe replica apply and gameplay callback suppression | Missing | Replaying native install/animation/BSOD methods can alter progression or emit a second mutation. |
| SS-027 | Main-thread Unity access and hook reentrancy | Existing poll discipline; future work pending | CoopRunner calls Poll via Update; future apply/callback code must preserve this. No hooks selected by this audit. |
| SS-028 | Shared simulation authority, run clock and random seeds/results | Decision pending; Missing | Different frame rates/loads/random samples can disagree on temperatures, scores, shutdown, timers and job outcomes. |
| SS-029 | Pause, focus loss, modal holds and animation clocks | Decision pending; Missing | [GameController pause ISIL](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/GameController.txt#L11563) changes a flag; inspect every simulation/animation clock consumer. |
| SS-030 | Time domains: simulation time, day/date, real time, clip phase, timeout | Missing shared model | WallClock and AnalogueClock have different drivers; DateTime.Now determines season in inspected code. Network freshness uses realtimeSinceStartup. |
| SS-031 | Admission while boot/benchmark/tool/paint/fill/install is active | Missing | Snapshot current stage and settled result; do not recreate operation by repeating purchases/callbacks. |
| SS-032 | Save ownership and separate co-op versus solo records | Decision pending; Missing | Existing game persistence does not implement session persistence. No save was opened or changed for this audit. |
| SS-033 | Consistent snapshot/save boundary across world and economy | Missing | Prevent saving a part both installed and in inventory, or cash deducted with no order/item. |
| SS-034 | Autosave/manual save/load/version migration and backup behavior | Missing co-op layer | [Career persistence audit](career-economy-and-persistence.md); current protocol has no disk/network schema for shared state. |
| SS-035 | Rejoin / fresh-process restore of actors, PCs, active runs and links | Missing | Transport reconnect is not enough; per-actor carried state and operation cleanup need policy. |
| SS-036 | Custom images/GIF/wallpapers/theme resources and local-path resolution | Decision pending; Missing | User paths and Unity textures are local references; content identity/availability matters to shared screen appearance. |
| SS-037 | External sharing/import/export, account unlocks and achievements | Decision pending; Missing | Do not duplicate platform/local-file side effects by replaying simulation events; [career](career-economy-and-persistence.md) and [software](screens-software-and-simulation.md). |
| SS-038 | Wire input validation, numeric bounds, lengths, unknown message handling | Partial | OnReceive switches on one byte then reads fixed floats/bool; no app-level world object validation, snapshot size budgets or schema negotiation. Finite/position-bound validation not present in PlayerState.Read. |
| SS-039 | Payload size, chunking, backpressure and prioritization | Missing for world sync | Full PC/save/image snapshots can be much larger than transforms; current protocol offers no such messages. Performance budget remains to measure. |
| SS-040 | Divergence detection and actionable diagnostics | Partial | Session logs/status/ping and opt-in AutoTest exist; no shared-state hashes, mutation history or per-PC mismatch reports. |
| SS-041 | Fault behavior under delayed, lost, duplicated and reordered traffic | Unverified for world sync | No world protocol yet. Existing transform test does not demonstrate conflict/idempotency/load behavior. |
| SS-042 | Clean shutdown/release of held objects, tool effects, UI focus and callbacks | Missing beyond capsule/session | Leaving a shared-world session must not retain another actor's reservation or looped sound. |

## Cross-domain transactions to audit as a whole

Each row names a dependency chain, not a chosen network packet or authority implementation.

| Trigger | Factors that must agree if shared | Failure to detect |
| --- | --- | --- |
| Pick up / bench / cupboard / delivery a PC | PC ID, holder/location, slot occupancy, job and note, peripherals/cables, simulation and software association | Duplicate PC, orphan job note, screen bound to wrong PC, stale powered instance. |
| Install or remove a component | Inventory provenance, instance and slot topology, screws/latches/blockers, cable/loop links, save representation, simulator recalculation, objective results | Part duplication, impossible topology, ghost component, stale benchmark/thermal result. |
| Route cable / pipe / LED strip | Owning PC, parent component/connector IDs, ordered nodes/frames, fitting/colour/material, commit/cancel, save records and visual rebuild | Wrong endpoints, cross-PC connection, incomplete mesh, lost edits after rejoin. |
| Apply paste / clean dust / delid / pad | Target instance, surface/amount/progress, tool phase/lock, animation callback, sound/effect, thermal/quality outcome | Cosmetic agreement but different cooling, repeated completion, stuck tool. |
| Power / reboot / install / clone OS | Electrical connections, running/boot/failure phase, software/storage/USB/BIOS config, app state, monitor/case LCD, fan/LED response | Display says running while simulation is off; one client boots a different image. |
| Run benchmark / OCCT / overclock | Parts, tuning config and effective values, chosen load/run ID/time, sensors/temperature/fans, crash reason, score, objectives | Different scores or BSOD, double reward, graph not matching accepted hardware. |
| Buy / ship / receive / open package | Catalog/price, cart/quantity, cash/payment, order ID/date/shipping option, package/parts, capacity/slots and progression | Duplicate charge or free part, different arrival day, package opens twice. |
| Complete / return / sell PC | Job/objective state, budget/deadline, PC ownership/location, listing price/status, reviews/reputation, cash/XP, emails | Money/XP awarded twice or job fulfilled using an already sold/moved PC. |
| Advance day / workshop upgrade | Calendar/date, orders/deliveries, jobs/deadlines, emails, recurring charges/unlocks, room scene/decorator, dust and save boundary | Date moves without associated transactions or scene restore uses prior-day state. |
| Save / load / reconnect | Mode/content, world epoch and revision, inventories, PC graphs/software, economy/progression, actor location, operations and UI-lock cleanup | Coherent-looking but mixed-time world, duplicate old command after load. |

## Required future evidence

Use controlled two-client cases for every dependency chain: ordinary action, both actors requesting at once, interrupted action, join during action, reconnect after action, save/load and fresh-process restoration. Include a rejected action and a missing-content case. Compare logical state as well as visible state; screenshots alone cannot establish inventory, save or exactly-once correctness.

Authority, shared scope, simultaneous-work policy, scene travel controls, pause and save ownership must be answered by the owner before implementation. This documentation pass can finish without choosing them. No friend/lobby transport, head model, remote UI layout, synchronization implementation or tests were added.

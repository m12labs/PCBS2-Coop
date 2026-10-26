# PCBS2 Co-op synchronization progress

**October 26, 2026 — experimental Steam connection, version 0.9.** Steam/LAN selection, private Steam lobby invites, P2P/relay transport and an in-game friends picker are implemented. Single-PC Steam initialization, hosting and UI have running-game evidence; two-PC invitation acceptance and actual relayed gameplay remain pending. Gameplay synchronization scope is unchanged. See the [implementation evidence and manual checklist](../steamworks.md#implementation-status).

**October 25, 2026 — task 3f complete, version 0.8.** [Atomic carry, bench and storage transfers](audit/phase-3.md#task-3f-atomic-carry-bench-and-storage-transfers) now persist correctly after rapid transfers. F11–F16 found the last two causes of failed saves:
- a load whose parts were all cached left its record in the game's load queue, which blocked the next save and resume;
- the game hides a workbench slot on pickup and shows it again only in its own drop handler. A PC the mod placed there, including the other player's drop, was invisible and saved its water pipes without their parts. The game then refused to load that save.

Completed load records are now cleared, and every mod placement shows the slot the way the game's drop handler does. F17 passed over the real network with both native checks passing:
- the F9 failure sequence and exact save/resume;
- drops onto a slot hidden on the other game, in both directions;
- a save while the guest carries, then rejoin;
- departure recovery, and retrieval of the recovered PC after rejoining;
- a final resume and rejoin.

Next: 3g.

**October 25, 2026 — task 3f, earlier runs (F1–F10).** Owned two-instance checks: ordinary pickup/drop/swap/store/retrieve, busy-PC refusal, package carry view, departure recovery, save/resume and rejoin. Pinned real-network competing pickups, drops and retrievals each produced one winner and one refusal. Runtime failures exposed and fixed stale cupboard callbacks, guest-created default fans missing from the host's stored graph, and an obsolete async load placing a PC after a newer transfer. F7 repeated the rapid-transfer failure successfully and verified replacement loads. **260/260** model/network checks pass; Release build is clean. Specialist hooks, forced projection failure/repair and disconnect before confirmation remain explicit 3h cases. F9 passed two save/resume checks, then a rapid replacement sequence left a native load pending and blocked persistence (resolved above).
**October 25, 2026 — task 3e complete, version 0.6.** [Shared Free Build inventory and PC creation/removal](audit/phase-3.md#task-3e-shared-free-build-inventory-and-pc-creationremoval): choosing a case in the inventory, switching an empty carried case, deleting an empty carried case at the in-world inventory and confirming a storage delete now ask the host, which assigns IDs and applies the change once on both peers. The Free Build catalog stays reusable (2,227 entries throughout). Refusals show on the requester's status line and leave the game UI as it was. Runs E3–E5/E7 verified host and guest creation of the same catalog case, switching, both deletion paths, admission while the host carries a new PC, guest rejoin with its case in hand, and save → Host Continue with exact identities; E1/E2/E3 failures and their fixes are recorded. **231/231** checks; clean build; deployed hashes match; original save hashes unchanged and all owned games closed. Until 3f, a guest that places or stores its spawned case natively can't spawn another (the host still records it as carried), and the other player doesn't see moved PCs. Next: 3f, atomic carry, bench and storage transfers.

**October 25, 2026 — task 3d complete, version 0.5.** [Per-player activity and resource ownership](audit/phase-3.md#task-3d-per-player-activity-and-resource-ownership) now synchronize work targets and pending claims with actor epochs, cancellation and host-ordered departure cleanup. Both players can work on one PC; distinct part claims coexist and overlapping claims are refused. D1–D3 verified independent/shared work, corrected active-PC pickup denial, cancellation/overlay behavior, rejoin and same/fresh-process save cleanup. **206/206** checks; clean build; final deployed hashes match; original save hashes unchanged and all owned games closed. Native PC operations still need accepted commands in 3e/3f; actual component commits remain phase 4. Version 0.5 saves require matching current-version sidecars; migration is phase 8. Next: 3e, shared Free Build inventory and PC creation/removal.

**October 25, 2026 — task 3c complete, version 0.4.** [Identity and paired persistence](audit/phase-3.md#task-3c-authoritative-identity-and-paired-persistence) now bind baseline PCs/parts/locations, preserve native aliases, persist allocator/tombstones and both carrier graphs, and restore absent carriers to shared storage. Host-canonical payload support is ready for gameplay commands. Network/model **181/181**; controlled native reload, ordinary storage/retrieval, fresh-process two-carrier recovery, guest admission while carrying and matching paired joined copies passed (C1–C7). Original save hashes unchanged; owned instances closed. Co-op saves require their matching `.coop` sidecar; pre-0.4 native-only co-op saves fail explicitly. Live PC actions remain unsynchronized. Next: 3d, per-player activity/conflicts.

**October 25, 2026 — task 3b complete.** [Controlled runtime evidence](audit/phase-3.md#task-3b-runtime-evidence) covers PC spawn/switch, populated transfers, storage/retrieval, deletion/cancel and monitor replacement. Fixed host/guest save selection after edited-world testing exposed that `ContinueFreeBuild` ignores the requested filename. Edited reload passed three times; guest bench/stored SKU topology matched. Build clean; network **141/141**; original save hashes unchanged and owned games closed. Package/note creation was not exposed by the audited default controls; alternate/imported states remain explicitly gated in 3g. No live PC synchronization or feature checkmark changes. Next: 3c, authoritative identity and persistence.

**October 25, 2026 — phase 3 started; task 3a complete.** The [phase 3 audit](audit/phase-3.md) traces PC/inventory/carry/bench identity and native transfer/save paths; [tasks 3a–3h](../plan.md#3-pcs-inventory-carrying-and-benches) put runtime hook evidence, authoritative identity/persistence and per-player ownership before gameplay transfers. Owner decision: an absent player's carried PC moves into shared cupboard storage, including save recovery. No phase 3 synchronization is implemented and no feature checkmarks change. Baseline network suite: **141/141** after the versioning change; no new game run or deployment during this audit.

**October 25, 2026 — phase 2 complete.** One shared Free Build world: the host loads a co-op world (New world / Continue) from `Saves\Coop`; a guest can drop in at any time and loads a snapshot of the host's current world, with changes made during its load held and applied once; host saves are copied to the guest; host or guest leaving is handled; nobody's pause stops the session. Evidence: [phase 2 audit](audit/phase-2.md#runtime-evidence) (network suite 142/142; two-instance runs S1–S3). The world is shared at join time only: live edits to parts, PCs and benches are not synchronized yet (phase 3 onward).

**October 25, 2026 — phase 1 complete.** Foundations only: join handshake with game/content identity, host-ordered world change pipeline with generations/revisions/duplicate and divergence handling, identity primitives, diagnostics and a save-isolated self test. Evidence: [phase 1 audit](audit/phase-1.md#runtime-evidence) (network suite 90/90; local two-instance runs R1 and R2). No game world state (parts, PCs, saves) is shared yet. The audit-pass notes below are kept as written.

**October 25, 2026 — audit only.** The broad static sync audit is documented under [docs/audit](audit/README.md). Three Luna subagents covered hardware/building, screens/software/simulation, and career/economy/persistence. The tables use Postal-style checkmarks. The implementation order and phase workflow are in [plan.md](../plan.md). No gameplay or networking implementation changed.

(Audit-pass text, before phases 1–2.) The mod remains the two-player LAN proof of concept: Host/Join from the main menu, separate Free Build worlds, and a remote capsule driven by position/yaw. **Parts, PCs, saves, inventory, software, economy and animations are not synchronized.** The checkmarks describe the implementation found in source; no fresh running-game acceptance was performed in this audit. Historical capsule/session evidence is described in the audit.

## Audit progress markers

- [x] Locate the game, preserved inputs, decompile/ISIL/indexes and Postal tracker reference.
- [x] Inspect the current mod/session protocol and remote-view implementation.
- [x] Audit player/workshop/animation/audio: **PW-001–075**.
- [x] Audit hardware/building with Luna: **HW-001–078**.
- [x] Audit screens/software/simulation with Luna: **SW-001–053**.
- [x] Audit career/economy/persistence with Luna: **CP-001–070**.
- [x] Audit session/identity/consistency: **SS-001–042**.
- [x] Reconcile overlap, evidence levels and cross-domain transactions.
- [x] Inventory all **1,598** main-game structural C# files and **32,482** field declaration lines, including nested/generated fields; distinguish automatic inventory from source-reviewed findings.
- [x] Compare installed binary/metadata hashes with the decompile: both match.
- [x] Validate documentation targets, source line bounds, factor IDs and inventory coverage.
- [ ] Finish individual manual/native review of source-index entries marked automatic-only and unresolved native branches.
- [ ] Inspect extracted scenes/prefabs/Animator clips/events/material/screen/audio assets: directory not found.
- [ ] Execute the [running-game backlog](audit/open-questions-and-runtime-checks.md#live-verification-backlog).
- [x] Record the owner's accepted scope decisions in [plan.md](../plan.md#owner-decisions); Free Build is first, career later.

There are **318 audit entries across five registers**, including related/overlapping entries and existing transport features. This is not a count of approved sync features or a multiplayer completion percentage. The broad static audit pass is complete; exhaustive source-path, asset and runtime verification remain open.

## Reading the status tables

Each feature has exactly one checkmark in **Not synced**, **Partially synced** or **Synced**, matching the Postal tracker style.

- **Synced:** the behavior described in the row is implemented and coordinated between peers. This does not claim fresh two-PC acceptance or coverage beyond that row.
- **Partially synced:** a stated subset is implemented; the comment identifies the remaining gap.
- **Not synced:** no implementation coordinates the listed behavior. Scope decisions that remain open are called out in comments; they are not counted as completed work.

Owner-approved personal preferences appear in the **Intentionally local** table. A completed audit or written plan does not change a feature's sync checkmark. Update a checkmark only with implementation evidence and record the relevant test evidence or remaining verification gap.

Since phase 1 the protocol (versioned only by the mod version, now 0.3) has `PlayerState(X, Y, Z, Yaw, InWorkshop)` on a sequenced stream plus `Welcome`, `WorldReset`, `Command`, `Mutation` and `CommandResult` on one reliable ordered stream; see [Protocol.cs](../src/PCBS2Coop.Network/Protocol.cs). The only world command is a diagnostic marker. The tables summarize detailed registers; follow report links for source evidence, small constituent factors, risks and acceptance cases.

## Session and world lifetime

Detailed evidence: [SS-001–042](audit/session-and-consistency.md).

| Feature | Not synced | Partially synced | Synced | Comments |
| --- | :---: | :---: | :---: | --- |
| Direct IPv4 host/join, custom port, one host/one client | | | ✅ | LiteNetLib session; no lobby/friend carrier. |
| Join handshake: mod version/game build/DLC/part catalog identity | | | ✅ | Phase 1b. Different mod version or game build refused with reason (the mod version is the only version); DLC/catalog differences join with a status-line warning. Game run R1 + loopback. |
| Connection failure/full/timeout/refusal messages | | | ✅ | Refusal reasons reach the guest's dialog (loopback-tested). |
| Install player ID | | | ✅ | Phase 1b/1d. Persistent per install (BepInEx config); labels stay Host/Client by owner decision. Not yet used for rejoin. |
| Main-menu Host/Join | | | ✅ | Phase 2: Host dialog has New world / Continue (one co-op world, confirm before replacing); Join connects and loads the host's world. Runs S1–S3. |
| Timeout/disconnect cleanup | | ✅ | | Capsule removal, pending-request abandonment and guest return to menu. Phase 3d adds host-ordered actor work/claim release and fresh rejoin epochs (D2). Phase 3f verifies committed carried-PC recovery to shared storage (F2); specialist tool cleanup follows its phase. |
| Reconnect / rejoin | | | ✅ | Phase 2: a rejoining guest loads a fresh snapshot of the host's current world (loopback + run S3 round 2). Fresh-process resume of live state: phase 8. |
| Common mode/save/workshop, travel/restart/day/load coordination | | ✅ | | Phase 2: Free Build in `Stage_02_Workshop`; host's world starts a generation once its load has settled (readiness barrier); guest enters only after its own load settles. Other workshops/career travel and day changes: phases 12, 15–17. |
| Snapshots, late join, deltas, revisions and resync | | ✅ | | Phase 2: drop-in join from a chunked, hashed snapshot (game save + mod state) with changes during the load held and applied once (S3: held 1). Game-state deltas after the snapshot arrive with each object family (phase 3+); in-world resync after divergence not yet. |
| Stable PC/part/slot/connector/job/order/operation IDs | | ✅ | | Phase 3c binds PC/part identities and workshop locations, persisting allocator/tombstones and native aliases. Phase 3d binds session activity and acquisition tokens to actor epochs. Connector/job/order identities remain later work. |
| Conflict resolution, reliable mutations, exactly-once outcomes | | ✅ | | Host-ordered reliable changes and duplicate/out-of-order/stale handling. Phase 3d adds shared work, exclusive part claims and movement exclusion of an active PC (206 checks; D1–D3). Phase 3e: PC creation/removal is validated and canonicalized by the host with refusals shown to the requester (231 checks; E3–E5). Phase 3f verifies atomic transfers and pinned competing requests over the real network (F5); later families retain their own validation. |
| Shared pause/time/simulation authority and host departure | | ✅ | | Phase 2: no shared pause (owner decision); the game keeps running in the background and sync continues during either player's pause (S3). Host departure returns the guest to the menu with the reason. Shared simulation time: phase 7. |
| Co-op save ownership/isolation and fresh-process recovery | | ✅ | | Host/guest co-op folders stay separate from solo saves. Phase 3c pairs native saves with persistent identity and full carrier graphs; 3d clears saved transient activity on host resume (C1–C7, D2/D3). Wider recovery/migration remains phase 8. |
| World diagnostics, divergence detection, large payload delivery | | ✅ | | Phase 1c/1f: change-hash divergence, counters, world summary, census dumps. Phase 2: chunked snapshot transfer up to 64 MiB (2.2 MB saves in practice). Images: phase 12. |

## Player, workshop, animation and sound

Detailed evidence: [PW-001–075](audit/player-workshop-and-presentation.md).

| Feature | Not synced | Partially synced | Synced | Comments |
| --- | :---: | :---: | :---: | --- |
| Player position and horizontal facing | | | ✅ | 20 Hz send target; interpolated remote capsule. |
| Workshop presence/remote visibility | | ✅ | | Flag/freshness gate; no scene identity/readiness or explicit exit snapshot. |
| Capsule/visor and Host/Client label | | | ✅ | No animation rig or player collision. |
| Look pitch, gait, remote activity/tool/target/pose | | ✅ | | Phase 3d shares actor work targets and resource claims. Head/body/tool animation, gait, pitch and visible activity poses remain later work. |
| PC/package/post-it pickup/carry/drop/store/spawn/despawn | | ✅ | | Phase 3e: catalog case spawn/switch, in-world carried-case delete and confirmed storage delete are host-approved changes applied once on both peers (E3–E5). Phase 3f adds host-approved pickup/drop/swap/store/retrieve, matching peer placement, a remote package box, races, departure recovery and stale-load suppression (F2/F5/F7, 0.8); packages/notes 3g. |
| Bench/tool/workstation assignment and independent activities | | ✅ | | Phase 3d verifies independent native work benches/cameras, shared-PC activity and part-claim conflicts. Remote activity stays outside native singleton fields. Live transfers/workstation associations are 3f/3g; specialist tools follow their own phase. |
| Job notes, package visuals, status/sale placards and ghost meshes | ✅ | | | Must follow underlying PC/job/location. |
| Doors, workshop travel/upgrade and room customization | ✅ | | | Furniture/material/prefab/collision/AR-preview-versus-commit. |
| Company name plate and generated textures | ✅ | | | Text, position, theme and material appearance. |
| Light switches, roof lights, ceiling-fan ramp/audio | ✅ | | | Animation/effect chain identified in native excerpts. |
| Seasons/decorations/clocks/traffic/ambience | ✅ | | | Full shared-world detail/phase parity selected; time/randomness/state paths remain unimplemented. |
| Tool/screw/latch/connector animations and completion callbacks | ✅ | | | Visible replay differs from committing an action once. |
| Tablet raise/lower/orientation/minimize/hidden transitions | ✅ | | | Callbacks, camera/input gates and remote representation. |
| Footsteps, tool sound one-shots/loops and dust particles | ✅ | | | Timing, source attachment and stop/cleanup. |
| Shared music/audio-reactive effects and photo/GIF content | ✅ | | | Shared track/effect phases and automatic required content transfer selected; personal preferences stay local. |

## Hardware and building

Detailed evidence: [HW-001–078](audit/hardware-and-building.md).

| Feature | Not synced | Partially synced | Synced | Comments |
| --- | :---: | :---: | :---: | --- |
| PC/case/part identity, topology, installed/removed/held state | | ✅ | | Phase 3c synchronizes persistent baseline IDs, native topology bindings and carrier facts at save/join. Phase 3e creates/retires PCs with host-assigned IDs and gives parts a native load adds (default case fans) IDs through the change stream before a capture. Phase 3f adds atomic movement and makes default fan identities explicit at catalog creation; live component edits remain phase 4. |
| Component variants, compatibility and blockers | ✅ | | | CPU/GPU/RAM/storage/M.2/PSU/cooling/accessory/adapter/splitter paths. |
| Screws/standoffs/socket/RAM/PCI locks/covers/hinges | ✅ | | | Settled state plus rotation/travel/callback phases. |
| Paste/Q-tip/dust/cleaning/delid/varnish/pads | ✅ | | | Surface/progress versus saved flags and thermal results. |
| Cable endpoints/connectors/custom colours/pins/routing | ✅ | | | Graph/node frames, commit/cancel and reconstruction. |
| Water loops/pipes/fittings/coolant/fill/drain/pump/run/direction | ✅ | | | Save graph and visible flow; no invented leak mechanic. |
| Case themes/paint/material/stickers/brush strokes/transforms | ✅ | | | Saved primitives, asset keys, DLC and customization IDs. |
| LEDs/RGB/strip nodes and fan curves/direction/flipping/spin/blur | ✅ | | | Config, simulation and visual phase differ. |
| Peripheral/desk/monitor/LCD assignment, plugs and power | ✅ | | | Correct PC/bench/component binding. |
| Condition/breakage/repair and powered/damage presentation | ✅ | | | Logical consequences versus visual damage evidence. |

## Computer screens, software and simulation

Detailed evidence: [SW-001–053](audit/screens-software-and-simulation.md).

| Feature | Not synced | Partially synced | Synced | Comments |
| --- | :---: | :---: | :---: | --- |
| Power/boot/BIOS/no-signal/install/OS/restart/BSOD phases | ✅ | | | Stage/result and screen output; duplicate callbacks matter. |
| BIOS pending/applied values, boot drive, CPU/GPU/RAM tuning | ✅ | | | Effective state can differ from editor selections. |
| Programs/files/folders/storage/junk/virus/OS clone/USB | ✅ | | | Canonical image and operation outcome, not only text. |
| Flat/curved monitor routing/aspect/resolution/display | ✅ | | | Local render/camera binding to the correct PC. |
| Case/component LCD text/graphs/sensors/time/power | ✅ | | | Data source, update clock/history and assignment. |
| Windows/z-order/taskbar/minimize/maximize/cursor/focus/typing | ✅ | | | Target: same visible desktop/apps; both players may provide input simultaneously. |
| Wallpaper/images/GIF capture/trim/frame/encode/save | ✅ | | | Static pipeline evidenced; assets/runtime formats unverified. |
| Cinebench/3DMark/OCCT progress/results/video/telemetry | ✅ | | | Run identity/clock, load accounting and graphs. |
| Fan/lighting/airflow/thermal/power/noise apps and overlays | ✅ | | | Configuration/simulation versus local editor/render view. |
| Temperature/load/voltage/wattage/throttle/failure schedules | ✅ | | | Random/frame-rate/load-request divergence. |
| RGB effect/flow direction/speed/phase/music/thermal inputs | ✅ | | | Settings and source signals versus derived frames. |
| Tablet camera feeds and app lifecycle | ✅ | | | Target: personal tablet per player with remotely visible physical screen/activity; camera/input isolation remains necessary. |
| Notes/music/minigames/tuning/catalogue/shop/decoration apps | ✅ | | | Per-app model/result side effects versus personal UI. |

## Career, economy, inventory and persistence

Detailed evidence: [CP-001–070](audit/career-economy-and-persistence.md).

| Feature | Not synced | Partially synced | Synced | Comments |
| --- | :---: | :---: | :---: | --- |
| Modes/config and generated/pregenerated jobs/PCs | ✅ | | | Type presence does not prove selectable shipped modes. |
| Jobs/objectives/subjobs/diagnosis/budget/deadline/collection | ✅ | | | 52 direct Objective subclasses inventoried; one settlement. |
| Inventory quantities/provenance/owned/stored/sale PCs | ✅ | | | Identity across install/carry/store/sell/load. |
| Shop stock/price/cart/discount/checkout/shipping/order | ✅ | | | Separate quote/commit/payment/delivery-option factors. |
| Delivery/package/items/opening/returns/refund boundaries | ✅ | | | Exactly-once transfers; undocumented mechanics not assumed. |
| Cash/overdraft/rent/utilities/bank charges/shares/ledger | ✅ | | | Individual payments/due states/source events. |
| XP/kudos/level/unlocks/reputation/reviews/achievements | ✅ | | | Shared/personal policy and platform effects separate. |
| PC Bay/market/retail/listings/prices/sales | ✅ | | | Offer/item identity, generation and ownership. |
| Calendar/day/events/email/read flags/notes/post-its | ✅ | | | Linked deadlines/bills/deliveries/generated records. |
| Tutorials/onboarding/hints and progression flags | ✅ | | | Actor UI versus shared progress and callbacks. |
| Manual/autosave/schema/codec/load/restore ordering | | ✅ | | Phase 3c pairs native saves with exact mod state/allocator/carrier graphs and validates before replay; joined copies retain both files. Missing/mismatched pairs fail explicitly. Phase 3e: created PCs keep their IDs/provenance through save, Host Continue and guest rejoin (E5, E7). Wider recovery/migration remains phase 8. |
| Export/import/local PC files/external sharing | ✅ | | | Target: required custom/imported-PC content transfers automatically in-session. External platform paths remain unverified and need separate scope if encountered. |

## Intentionally local

These personal preferences were accepted with the scope decisions in [plan.md](../plan.md#owner-decisions). Local preferences must not change shared world/simulation outcomes. Personal tablet state is not listed here because its screen/activity must be visible remotely.

| System | Personal behavior |
| --- | --- |
| Audio output volume/mute | Each player chooses their own listening levels; shared music track/playhead and reactive inputs still synchronize. |
| Graphics/detail/display settings | Each player chooses their own machine's rendering quality/window/display settings; simulated computer desktops remain shared. |
| Language | UI localization remains personal; synchronize underlying IDs/values instead of translated strings. |
| Keybindings/input sensitivity | Each player keeps their own controls. |
| Personal camera and guest menus | Cameras/input focus remain per player; guest menus do not pause the host. Host pause still freezes shared gameplay. |

## Current stopping point and evidence gaps

The broad static audit and documentation checks are finished. [Coverage](audit/coverage-and-evidence.md) distinguishes source-reviewed findings from automatic field enumeration; it does not claim every native method was read. Declarations and inspected ISIL match the installed binary/metadata.

`docs/audit/` was missing and has been created. **No extracted Unity scene/prefab/Animator asset directory was found**, and the final native `coverage.json` summary is absent. Shipped Addressables files and ISIL are present. Exact clip/event bindings, scene-specific screen/material assignments, DLC variants and live behavior remain open in the [verification backlog](audit/open-questions-and-runtime-checks.md).

No game instances were started or closed, no owner's saves or game files changed, and no build/deploy/commit/push performed. Existing logs/root diagnostics were read as historical evidence only.

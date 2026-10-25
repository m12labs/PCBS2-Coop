# PCBS2 Co-op synchronization progress

**October 25, 2026 — phase 2 complete.** One shared Free Build world: the host loads a co-op world (New world / Continue) from `Saves\Coop`; a guest can drop in at any time and loads a snapshot of the host's current world, with changes made during its load held and applied once; host saves are copied to the guest; host or guest leaving is handled; nobody's pause stops the session. Evidence: [phase 2 audit](docs/audit/phase-2.md#runtime-evidence) (network suite 142/142; two-instance runs S1–S3). The world is shared at join time only: live edits to parts, PCs and benches are not synchronized yet (phase 3 onward).

**October 25, 2026 — phase 1 complete.** Foundations only: join handshake with game/content identity, host-ordered world change pipeline with generations/revisions/duplicate and divergence handling, identity primitives, diagnostics and a save-isolated self test. Evidence: [phase 1 audit](docs/audit/phase-1.md#runtime-evidence) (network suite 90/90; local two-instance runs R1 and R2). No game world state (parts, PCs, saves) is shared yet. The audit-pass notes below are kept as written.

**October 25, 2026 — audit only.** The broad static sync audit is documented under [docs/audit](docs/audit/README.md). Three Luna subagents covered hardware/building, screens/software/simulation, and career/economy/persistence. The tables use Postal-style checkmarks. The implementation order and phase workflow are in [plan.md](plan.md). No gameplay or networking implementation changed.

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
- [ ] Execute the [running-game backlog](docs/audit/open-questions-and-runtime-checks.md#live-verification-backlog).
- [x] Record the owner's accepted scope decisions in [plan.md](plan.md#owner-decisions); Free Build is first, career later.

There are **318 audit entries across five registers**, including related/overlapping entries and existing transport features. This is not a count of approved sync features or a multiplayer completion percentage. The broad static audit pass is complete; exhaustive source-path, asset and runtime verification remain open.

## Reading the status tables

Each feature has exactly one checkmark in **Not synced**, **Partially synced** or **Synced**, matching the Postal tracker style.

- **Synced:** the behavior described in the row is implemented and coordinated between peers. This does not claim fresh two-PC acceptance or coverage beyond that row.
- **Partially synced:** a stated subset is implemented; the comment identifies the remaining gap.
- **Not synced:** no implementation coordinates the listed behavior. Scope decisions that remain open are called out in comments; they are not counted as completed work.

Owner-approved personal preferences appear in the **Intentionally local** table. A completed audit or written plan does not change a feature's sync checkmark. Update a checkmark only with implementation evidence and record the relevant test evidence or remaining verification gap.

Since phase 1 the protocol (versioned only by the mod version, now 0.3) has `PlayerState(X, Y, Z, Yaw, InWorkshop)` on a sequenced stream plus `Welcome`, `WorldReset`, `Command`, `Mutation` and `CommandResult` on one reliable ordered stream; see [Protocol.cs](src/PCBS2Coop.Network/Protocol.cs). The only world command is a diagnostic marker. The tables summarize detailed registers; follow report links for source evidence, small constituent factors, risks and acceptance cases.

## Session and world lifetime

Detailed evidence: [SS-001–042](docs/audit/session-and-consistency.md).

| Feature | Not synced | Partially synced | Synced | Comments |
| --- | :---: | :---: | :---: | --- |
| Direct IPv4 host/join, custom port, one host/one client | | | ✅ | LiteNetLib session; no lobby/friend carrier. |
| Join handshake: mod version/game build/DLC/part catalog identity | | | ✅ | Phase 1b. Different mod version or game build refused with reason (the mod version is the only version); DLC/catalog differences join with a status-line warning. Game run R1 + loopback. |
| Connection failure/full/timeout/refusal messages | | | ✅ | Refusal reasons reach the guest's dialog (loopback-tested). |
| Install player ID | | | ✅ | Phase 1b/1d. Persistent per install (BepInEx config); labels stay Host/Client by owner decision. Not yet used for rejoin. |
| Main-menu Host/Join | | | ✅ | Phase 2: Host dialog has New world / Continue (one co-op world, confirm before replacing); Join connects and loads the host's world. Runs S1–S3. |
| Timeout/disconnect cleanup | | ✅ | | Capsule removal; guest's pending requests abandoned; guest leaves the world and returns to the menu with the reason. No world reservations/tool cleanup yet (phase 3+). |
| Reconnect / rejoin | | | ✅ | Phase 2: a rejoining guest loads a fresh snapshot of the host's current world (loopback + run S3 round 2). Fresh-process resume of live state: phase 8. |
| Common mode/save/workshop, travel/restart/day/load coordination | | ✅ | | Phase 2: Free Build in `Stage_02_Workshop`; host's world starts a generation once its load has settled (readiness barrier); guest enters only after its own load settles. Other workshops/career travel and day changes: phases 12, 15–17. |
| Snapshots, late join, deltas, revisions and resync | | ✅ | | Phase 2: drop-in join from a chunked, hashed snapshot (game save + mod state) with changes during the load held and applied once (S3: held 1). Game-state deltas after the snapshot arrive with each object family (phase 3+); in-world resync after divergence not yet. |
| Stable PC/part/slot/connector/job/order/operation IDs | | ✅ | | Phase 1c/1d: operation IDs and host-allocated never-reused object IDs with tombstones. No game object is bound to an ID yet. |
| Conflict resolution, reliable mutations, exactly-once outcomes | | ✅ | | Phase 1c: host-ordered reliable changes, duplicate/out-of-order/stale handling, remembered outcomes. Per-object conflict rules arrive with each object family. |
| Shared pause/time/simulation authority and host departure | | ✅ | | Phase 2: no shared pause (owner decision); the game keeps running in the background and sync continues during either player's pause (S3). Host departure returns the guest to the menu with the reason. Shared simulation time: phase 7. |
| Co-op save ownership/isolation and fresh-process recovery | | ✅ | | Phase 2: host saves the co-op world in `Saves\Coop`; guest plays from `Saves\Coop\GuestSession` and keeps the latest host save in `Saves\Coop\Joined`; solo saves untouched (S1–S3). Mod state isn't saved yet, and a resumed world doesn't restore live state: phase 8. |
| World diagnostics, divergence detection, large payload delivery | | ✅ | | Phase 1c/1f: change-hash divergence, counters, world summary, census dumps. Phase 2: chunked snapshot transfer up to 64 MiB (2.2 MB saves in practice). Images: phase 12. |

## Player, workshop, animation and sound

Detailed evidence: [PW-001–075](docs/audit/player-workshop-and-presentation.md).

| Feature | Not synced | Partially synced | Synced | Comments |
| --- | :---: | :---: | :---: | --- |
| Player position and horizontal facing | | | ✅ | 20 Hz send target; interpolated remote capsule. |
| Workshop presence/remote visibility | | ✅ | | Flag/freshness gate; no scene identity/readiness or explicit exit snapshot. |
| Capsule/visor and Host/Client label | | | ✅ | No animation rig or player collision. |
| Look pitch, gait, remote activity/tool/target/pose | ✅ | | | No head/body/tool animation state. |
| PC/package/post-it pickup/carry/drop/store/spawn/despawn | ✅ | | | Includes contested transfer and physical attachment/slot occupancy. |
| Bench/tool/workstation assignment and independent activities | ✅ | | | Singleton selection/camera/carry state must be evaluated per actor. |
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

Detailed evidence: [HW-001–078](docs/audit/hardware-and-building.md).

| Feature | Not synced | Partially synced | Synced | Comments |
| --- | :---: | :---: | :---: | --- |
| PC/case/part identity, topology, installed/removed/held state | ✅ | | | Duplicate same-SKU instances and parent/slot relations. |
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

Detailed evidence: [SW-001–053](docs/audit/screens-software-and-simulation.md).

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

Detailed evidence: [CP-001–070](docs/audit/career-economy-and-persistence.md).

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
| Manual/autosave/schema/codec/load/restore ordering | ✅ | | | Local persistence is not a coherent multiplayer snapshot. |
| Export/import/local PC files/external sharing | ✅ | | | Target: required custom/imported-PC content transfers automatically in-session. External platform paths remain unverified and need separate scope if encountered. |

## Intentionally local

These personal preferences were accepted with the scope decisions in [plan.md](plan.md#owner-decisions). Local preferences must not change shared world/simulation outcomes. Personal tablet state is not listed here because its screen/activity must be visible remotely.

| System | Personal behavior |
| --- | --- |
| Audio output volume/mute | Each player chooses their own listening levels; shared music track/playhead and reactive inputs still synchronize. |
| Graphics/detail/display settings | Each player chooses their own machine's rendering quality/window/display settings; simulated computer desktops remain shared. |
| Language | UI localization remains personal; synchronize underlying IDs/values instead of translated strings. |
| Keybindings/input sensitivity | Each player keeps their own controls. |
| Personal camera and guest menus | Cameras/input focus remain per player; guest menus do not pause the host. Host pause still freezes shared gameplay. |

## Current stopping point and evidence gaps

The broad static audit and documentation checks are finished. [Coverage](docs/audit/coverage-and-evidence.md) distinguishes source-reviewed findings from automatic field enumeration; it does not claim every native method was read. Declarations and inspected ISIL match the installed binary/metadata.

`docs/audit/` was missing and has been created. **No extracted Unity scene/prefab/Animator asset directory was found**, and the final native `coverage.json` summary is absent. Shipped Addressables files and ISIL are present. Exact clip/event bindings, scene-specific screen/material assignments, DLC variants and live behavior remain open in the [verification backlog](docs/audit/open-questions-and-runtime-checks.md).

No game instances were started or closed, no owner's saves or game files changed, and no build/deploy/commit/push performed. Existing logs/root diagnostics were read as historical evidence only.

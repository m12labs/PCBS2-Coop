# PCBS2 Co-op implementation plan

Initial roadmap: October 25, 2026. **Free Build comes first; career starts after the Free Build acceptance phase.** This document orders future implementation. Creating the plan does not start a phase.

Implementation status belongs in [sync-progress.md](sync-progress.md). Source evidence and unresolved behavior belong in [docs/audit](docs/audit/README.md). This plan owns phase order, accepted scope decisions, focused work plans and phase completion records.

## Current position

- Broad static audit completed; asset/runtime verification and individual native-path gaps remain recorded in the audit.
- Current implementation: two-player LAN host/join and remote capsule position/yaw/presence. Shared-world implementation has not started.
- All phases below are **Not started**. No numbered phase has been audited or expanded into implementation substeps yet.
- Initial scope decisions were answered by the owner on October 25, 2026 and are recorded below. Character asset availability remains an evidence question for the avatar audit.

## Required workflow for every phase

1. **Audit the phase when it starts.** Read applicable `AGENTS.md`, check Git status, inspect the current implementation and the phase's relevant audit factors. Re-check native mutation/callback/save paths and the exact game/content build. Inspect the scene/prefab/animation/screen assets needed for that phase; obtain missing evidence or name the resulting gap. The earlier broad audit is a starting point, not a substitute for this focused audit.
2. **Record the focused audit** in `docs/audit/phase-N.md`, using the actual number, for example `docs/audit/phase-1.md`. Record source/native/runtime evidence separately, affected factor IDs, dependencies, state/event/derived/local boundaries, identity and conflict risks, persistence/rejoin consequences, owner decisions and concrete acceptance cases. Ask about newly uncovered product choices before implementing dependent behavior; continue independent investigation while waiting.
3. **Then expand only that phase** in this file into focused tasks: phase 1 gets **1a, 1b, 1c…**; phase 2 gets **2a, 2b, 2c…**, and so on. Determine their content and order from the focused audit. The initial numbered roadmap deliberately does not pre-author those detailed tasks.
4. **Give every focused task** an outcome, affected audit IDs, dependencies, concrete scope, relevant files/native entry points supported by evidence, validation cases and a checkable completion condition. Include the snapshot/save/rejoin and concurrent-action consequences of every new shared fact. Resolve relevant pending decisions before dependent code.
5. **Implement and verify in substep order.** Keep Unity/IL2CPP calls on the main thread. Run checks appropriate to the change, including logical state and visible results on both peers where applicable. Record what ran and what did not. Use owned test instances/disposable test saves; preserve the owner's processes and solo saves. Build against real generated interop. A build can deploy to the game, so choose deployment deliberately.
6. **Close each focused task with evidence.** Update its status, audit findings and the relevant sync-progress checkmarks. A completed audit, a written task or a passing network-only test does not prove a feature works in the game. If only part is implemented, mark the feature partially synced and describe the remaining gap.
7. **Close the parent phase only when its exit criteria pass.** Record completed substeps, test evidence, open limitations and the next phase. A phase with missing required runtime evidence remains in validation. Carry an item forward only with an explicit destination and reason; changes to product scope or priorities go to the owner. Then repeat the focused audit and subplanning workflow for the next phase.

Phase and task IDs stay stable. Append new substeps instead of renumbering completed work. If an audit discovers a missing prerequisite, document the dependency and adjust the active phase before implementation. Advance through phases in order; do not silently start a later feature while its prerequisite remains incomplete.

## Owner decisions

The owner confirmed these choices on October 25, 2026. Use this table as the current scope authority when older audit reports describe these questions as pending.

| Decision | Accepted behavior | Affected phases |
| --- | --- | --- |
| Initial players/workshop | Two players; current default Free Build workshop first. Other supported workshops follow after the baseline works. | 1–3, 12, 14 |
| World, saves, inventory and career | One shared co-op world; host owns co-op saves; shared inventory and later shared money/jobs/progression; solo saves remain separate. | 1–3, 8, 15–18 |
| Simultaneous PC work | Both players may work on different parts of the same PC. Block conflicting edits and pickup/store/sell or other movement of an actively used PC. Avoid a blanket one-player-per-PC lock. | 3–5, 9–11 |
| Computer desktop/input | Same visible desktop/apps for both players; both may provide input simultaneously. Consistent mutation ordering must preserve this capability rather than introduce one exclusive controller. | 6–7, 13 |
| Tablets | A personal tablet per player; show its physical screen/activity remotely. Each player's input/camera remains their own; tablet commands affecting shared PCs/world still mutate that shared model. | 6, 12–13 |
| Pause and travel | Host pause freezes both; guest menu remains personal; host controls load/workshop/day changes. | 2, 7–8, 12, 17 |
| Host departure/recovery | Host departure ends the session and returns the guest to the menu. Support host-save resume and full guest rejoin into the current world. Host migration is outside this roadmap. | 2, 8, 14, 18 |
| Presentation parity | Target all shared-world details, including ambient/music phases. Detailed avatars follow core Free Build functionality. Personal output volume does not change shared track/playhead/effect inputs. | 7, 11–14 |
| Player collision | Players pass through each other; preserve world/interaction validation without introducing player-body blocking. | 3, 13–14 |
| Custom content | Automatically transfer required custom wallpapers, GIFs, themes and imported-PC content within the co-op session; local paths alone are not content identity. | 6, 11–14, 18 |
| Personal preferences | Volume/mute, graphics/detail, language, keybindings and personal camera/input preferences remain local. Their values must not change shared simulation or event outcomes. | 2, 6–7, 12–14 |
| Avatar asset preference | Prefer a reusable existing game character/NPC model if the asset audit finds a suitable rig. No such rig is verified yet; do not invent one or treat a controller class as a mesh. Present evidenced reuse/fallback options if none is suitable. | 13 |

Any UI placement or flow not settled by these answers remains a phase-start question under the repository's working rules.

### Existing player/NPC model evidence

The historical workshop hierarchy identifies `Player {CharacterController, Walk, ActivateTablet}` with `Player/Main Camera`; it does not establish a humanoid mesh or animation rig. The current mod creates its own [capsule](src/PCBS2Coop.Plugin/RemotePlayerView.cs). A local main-assembly search found movement/controller and input-demo classes, not a confirmed reusable NPC/humanoid controller or skinned player body. [Walk](artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/Walk.cs) and [SimplePlayerMovement](artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/SimplePlayerMovement.cs) are movement code, not evidence of a character asset. Avatar IDs in reviews/jobs are not proof of a 3D character rig.

The extracted Unity asset directory is still missing. Phase 13 must inspect actual model/prefab/rig/animation assets before deciding whether an existing game character can be reused. This evidence does not justify claiming either that a reusable NPC exists or that none can exist in bundled assets. Reuse is the owner's preference, and creating a new character is not preselected.

## Numbered implementation phases

Order follows data integrity and dependencies first, usable standard Free Build next, specialist building/presentation next, then career. Save/load, late join and concurrency are obligations of each phase that adds state; their dedicated phases consolidate the earlier work rather than postpone those obligations.

### 1. Synchronization foundations and focused evidence

**Purpose:** establish the rules and identity needed for any shared-world operation.

**Scope:** audit existing transport and native load/mutation paths; apply the accepted owner decisions; define compatible game/mod/content identity, player/world/object/operation identity, world generations/revisions, command validation, ordering, reliable mutations, duplicate handling and main-thread application for two players. Distinguish per-player activity from global single-player controller fields. Establish useful diagnostics and a repeatable controlled test setup. Obtain the asset evidence needed by the next phase rather than requiring every asset in the game to be reconstructed upfront.

**Exit:** contracts and evidence are documented; the chosen message/identity foundations pass meaningful tests for stale/duplicate/invalid requests and world reset boundaries; the existing capsule/session path still works. Required evidence gaps and their phase owners are explicit.

**Audit starting points:** session/consistency register; player/workshop singleton-state risks; source/build coverage ledger.

### 2. One shared Free Build session and world lifecycle

**Purpose:** place players into the same logical Free Build world safely.

**Scope:** implement a shared host-owned default Free Build world with the accepted new/load/join flow, initial baseline, scene and async-content readiness, independent cameras/input, world generation changes, host pause, personal guest menus, host departure and failed-load recovery. Establish host-owned co-op saves isolated from solo saves before any shared-world write. Preserve supported baseline PC data while later phases add live mutations; unsupported state must be reported explicitly. Do not mistake launching two new local Free Builds for sharing one world.

**Exit:** both peers load the selected baseline and agree on world identity/readiness; one player's UI does not seize the other's controls; stale-world traffic is rejected; failed admission or departure leaves usable menus/worlds according to policy; solo saves are preserved.

**Audit starting points:** SS lifecycle/readiness/snapshots; PW scenes/doors/state; CP mode/save boundaries.

### 3. PCs, inventory, carrying and benches

**Purpose:** make shared objects and their locations reliable before component editing.

**Scope:** PC/case/part instance identity, shared inventory/provenance, spawn/delete, bench and storage occupancy, pickup/carry/drop/store/retrieve, packages and physical note associations where used in Free Build, peripherals/workstation binding and per-player operation ownership. Allow different-part work on one PC, block conflicting edits and movement/storage/sale of an actively used PC, and keep players physically pass-through.

**Exit:** transfers produce one logical instance in one location; contested actions neither duplicate nor lose items; independent benches remain independent; current locations and associations restore correctly through baseline sync and disconnect cleanup.

**Audit starting points:** PW carrying/benches/storage; HW identity/topology; CP inventory/StoredPC records.

### 4. Standard component installation and removal

**Purpose:** build and disassemble ordinary PCs together.

**Scope:** supported case/motherboard/CPU/RAM/GPU/storage/M.2/PSU/cooler/fan/accessory paths, parent/slot topology, compatibility/blockers, screws/standoffs/socket/RAM/PCI locks, covers/hinges and install/remove start/commit/cancel. Include the physical state and minimum visual feedback needed to understand each action; detailed parity is completed later.

**Exit:** two peers agree on parts, slot relations and mechanical state for every supported category, including duplicate identical parts; conflicting edits resolve according to policy; interrupted actions and restoration preserve inventory and topology.

**Audit starting points:** HW component/slot/screw/latch/blocker factors; PW target/tool activity; SS transaction/callback risks.

### 5. Electrical cables, peripherals and physical power

**Purpose:** connect an assembled PC and give both peers consistent electrical state.

**Scope:** ordinary/modular/daisy-chain/external cables and connectors, route nodes/frames, adapters/splitters, plug/unplug/cancel, monitor/keyboard/mouse and other supported peripherals, workbench connections, physical powered state and fan/LED activation inputs. Add control/cable configuration that is required for the standard build path.

**Exit:** cable graphs restore to the same endpoints and geometry; disconnects/conflicts do not leave dangling or cross-PC links; physical power and connected peripherals agree, with one logical outcome per action.

**Audit starting points:** HW cables/peripherals/power; PW workstation assignment; SW display association and power animation.

### 6. Boot, BIOS, OS and visible computer screens

**Purpose:** make the shared PC usable as a computer, including what observers see.

**Scope:** boot/off/no-signal/BIOS/OS/restart/install/failure phases; pending/applied BIOS settings and boot drive; software/files/folders/storage, program install/uninstall, USB/OS clone; monitor/case/component LCD binding, aspect/curved-display handling and shared desktop/app/taskbar/window state with simultaneous input from both players. Keep per-player input identity while ordering shared mutations consistently; do not substitute an exclusive desktop-control lock. Establish personal tablet models and remotely visible screen/activity. Include stock wallpaper content; automatic custom asset transfer is integrated in phase 12.

**Exit:** the same logical PC boots the same image/config and shows the agreed screen state on the correct displays; accepted input policy works with concurrent viewers; software operations settle once and restore without replaying installs or boot side effects.

**Audit starting points:** SW lifecycle/BIOS/OS/files/windows/monitor/LCD; HW display/power association; PW monitor/tablet/camera separation.

### 7. Simulation, tests, tuning and functional app results

**Purpose:** make shared hardware and software produce consistent meaningful outcomes.

**Scope:** effective clocks/voltage/memory timings, temperature/load/power/throttle/condition/failure, fan curves/control, airflow/thermal/power/noise tools, benchmark and OCCT run identity/progress/results, graph/sensor semantics, hardware-info and tuning app effects, relevant app/minigame outcomes where they mutate PC state. Tie fan/RGB/LCD readings to accepted simulation inputs. Determine random/time inputs from evidence and owner policy.

**Exit:** both peers agree on run/config/result and terminal failure state across supported load/tuning cases; overlapping apps balance their load requests; closing/rejoining/pause does not double-apply load or results; displays reflect the accepted PC model.

**Audit starting points:** SW benchmark/OCCT/tuning/telemetry/failure/app factors; HW condition/fan control; SS clocks and callback suppression.

### 8. Durable Free Build saves and complete core rejoin

**Purpose:** prove the standard build path survives leaving, saving and restarting.

**Scope:** coherent snapshots across all state implemented in phases 2–7; manual/autosave ownership, schema/version and safe restore; loaded-world admission, guest reconnect, host-alone resume, operation cleanup and full baseline/delta reconciliation. Add recovery tests for interrupted transfers, installs, boot and running tests. Every later phase must extend this persistence/restoration coverage when it adds state.

**Exit:** save/load, guest rejoin and fresh-process resume preserve the same supported PCs/inventory/software/config/results without duplication, stale-world events or interference with solo saves; active-operation behavior follows policy and is evidenced.

**Audit starting points:** SS snapshot/persistence/recovery; CP codec/save/load; every earlier phase's state inventory.

### 9. Cleaning, paste, pads and specialist bench tools

**Purpose:** synchronize maintenance and preparation that affect PC condition and cooling.

**Scope:** dust/cleaning and accumulation, paste/Q-tip application/removal, delid/clamp/heat/scrape/varnish/procedure state, thermal pads and related bench operation stages. Separate surface/progress/thermal outcome from tool gestures, particles, sounds and animation callbacks. Integrate new facts into saves and rejoin immediately.

**Exit:** committed condition/coverage/thermal state and required tool presentation agree; callbacks commit once; cancel/pause/disconnect during each tool family releases ownership and restores a coherent result.

**Audit starting points:** HW paste/dust/delid/pad/tool factors; PW animation callback/particle/audio factors; SW thermal consequences.

### 10. Custom water cooling and pipe routing

**Purpose:** support complete custom cooling builds after ordinary assembly is stable.

**Scope:** blocks/pump/reservoir/radiator relationships, generated slots, loop membership, pipe/fitting endpoints and nodes, routing/edit/cancel, coolant selection/fill/drain/run/direction and supported physical/thermal effects. Follow actual game evidence; names alone do not establish leak/air-block mechanics.

**Exit:** complete and interrupted loops have matching topology, fittings, coolant/run state and derived presentation; removal/conflicts do not corrupt graphs; saves/rejoin rebuild dependencies in a verified order.

**Audit starting points:** HW water graph/save/bench-tool factors; SW thermal/flow input consumers; SS reconstruction order.

### 11. PC customization, RGB and visual hardware configuration

**Purpose:** share the full PC appearance and its dynamic effects.

**Scope:** case paint/material/theme/stickers, saved primitives and brush strokes, position/rotation/scale/mirroring, customization identity/DLC resources, custom cable styles/materials/pins, LED strips/nodes/parent attachment, RGB effect modes/colours/direction/speed/phase, fan flipping/reverse-spin/blur and relevant custom asset references. Handle music/thermal effect inputs according to the accepted parity policy.

**Exit:** configuration and required visual output match during editing, after commit/cancel and after save/rejoin; customizations attach to the correct components; dynamic effects use the intended shared or personal inputs and clocks.

**Audit starting points:** HW customization/cable/LED/fan factors; SW GIF/content/lighting/music-driven effects; PW animation timing.

### 12. Workshop environment, remaining Free Build apps and content

**Purpose:** complete the playable Free Build space around the PCs.

**Scope:** decorator/furniture/desk/material/prefab changes, AR preview versus commit, company plate, doors/lights/ceiling fans, seasonal decorations/clocks/traffic/ambience as selected; remaining relevant tablet apps, notes/music/catalogue/customization views and peripheral variants. Implement the approved custom wallpaper/GIF/theme/PC import/export transfer boundary. Expand workshop/DLC support at the point approved by the owner, after the baseline workshop works.

**Exit:** room changes preserve PC/bench/input associations, async swaps cannot restore obsolete state, world presentation matches the chosen scope, content availability/fallbacks are defined, and all new supported facts survive save/rejoin.

**Audit starting points:** PW room/environment factors; SW remaining app/capture/content factors; HW catalog/peripheral variants; CP local theme/sharing boundaries.

### 13. Remote player detail, animations, screens and audio parity

**Purpose:** finish the small visible and audible details across supported Free Build actions.

**Scope:** audit existing character/NPC assets first and use a suitable existing rig if available; resolve a fallback with the owner if none is suitable. Implement approved body/head/gaze/tool poses while retaining pass-through players, activity/carry attachments, movement/footsteps, personal tablet physical display/orientation visible remotely, component/screw/latch/plug/tool clip phases, callback separation, boot/display/benchmark/GIF animations, particles, one-shot/looped audio and fan/RGB/ambient/music phase parity and cleanup. Audit real asset/controller/event bindings before choosing replay hooks.

**Exit:** every supported action has the required observer presentation; replicas do not trigger duplicate gameplay; pause/rejoin/cancel/despawn stop or catch up effects correctly. Personal settings and any intentionally local behavior match the recorded owner decisions.

**Audit starting points:** PW presentation register; HW physical transient effects; SW screen/graph/video/GIF and dynamic effect factors; extracted asset evidence.

### 14. Free Build completeness, faults and performance acceptance

**Purpose:** finish and prove Free Build before starting career.

**Scope:** reconcile every Free Build-relevant audit entry against implemented/tested/partial/intentionally-local/deferred disposition; exercise independent and same-PC work, late join, save/restart, content mismatches, failed loads, packet faults and disconnect at commit. Measure busy-world bandwidth/frame time/snapshot size and investigate observed bottlenecks. Complete the relevant V-01–V-19 cases with repeatable evidence, including a two-machine pass.

**Exit:** every required Free Build feature meets its acceptance cases and performance expectations; no unresolved integrity/restore blocker remains; approved deferrals/local choices are explicit; tracker and evidence agree. Required unrun native/two-machine cases keep the phase in validation. Only then move to career.

**Audit starting points:** all Free Build dispositions, source/asset coverage gaps and the verification backlog.

### 15. Shared career model, progression and mode initialization

**Purpose:** add career to the proven Free Build synchronization foundation.

**Scope:** re-audit actual career/New Game Plus reachability, initialization/migrations and owner scope decisions; mode/difficulty/content compatibility, career record identity, cash/overdraft/ledger/shares/XP/level/unlocks/reputation and owned inventory/PC provenance. Establish calendar/day identity and progression prerequisites needed by economic transactions. Extend the co-op persistence schema and restore coverage.

**Exit:** career starts/loads with one agreed logical model according to the chosen personal/shared boundaries; award/spend mutations are attributable and replay-safe; progression/unlocks and saved references agree across restore/rejoin.

**Audit starting points:** CP mode/finance/progression/inventory factors; SS ownership/schema; Free Build persistence contracts.

### 16. Career shopping, deliveries, markets and PC sales

**Purpose:** synchronize career transactions without losing or duplicating money and items.

**Scope:** stock/catalog/prices/discounts, carts/checkout/shipping/orders, transit/delivery scheduling/packages/opening/returns where evidenced, PC Bay/market offers, retail valuation/listings/sales and transaction-ledger effects. Integrate accepted career inventory/wallet semantics and prerequisite day/calendar behavior.

**Exit:** accepted transactions update quote/payment/order/item ownership exactly once; stale offers, simultaneous purchases, retries and interrupted delivery/opening restore coherently; displayed prices and records match the accepted model.

**Audit starting points:** CP commerce/delivery/market/retail/ledger factors; PW package/PC location; SS cross-domain settlement.

### 17. Career jobs, objectives, day flow and communications

**Purpose:** complete the actual career gameplay loop.

**Scope:** job generation/acceptance/subjobs/diagnosis/budget/deadline/results/collection, all relevant objective families and triggers, PC/note association, reward/review settlement, calendar/day changes/bills/penalties/deliveries, email/read/tag flags, notes, tutorials/onboarding/hints and workshop progression/travel. Include local/account achievement or sharing side effects only according to recorded policy and actual reachable behavior.

**Exit:** the supported full job/day loop converges on one outcome per transaction and progression event; day transitions coordinate deadlines/bills/deliveries/generation; UI follows the correct player/shared model; save/rejoin preserves the same career and operation state.

**Audit starting points:** CP job/objective/day/email/tutorial/achievement factors, including the objective-class inventory; earlier Free Build simulation/build/result events.

### 18. Career recovery and full co-op acceptance

**Purpose:** prove the complete supported game scope and close the implementation audit.

**Scope:** re-audit all CP/SS factors and remaining shared Free Build interactions under career; verify fresh-process resume, guest rejoin, migrations, day/transaction/job interruptions, mode/workshop changes and approved external-content/account boundaries. Exercise simultaneous low-funds purchases, last-item conflicts, repeated job settlement, saves during operations and long sessions across both machines. Record any unsupported legacy/DLC/reachability case explicitly rather than infer it works.

**Exit:** the intended career loop, persistence and presentation pass the documented acceptance matrix; every audit entry has an evidence-backed disposition and any later work is explicitly scoped; sync-progress reflects actual synced/partial/not-synced status.

**Audit starting points:** all domain reports, phase-focused audits and the full verification backlog.

## Coverage rule for focused subplans

The 318 audit entries are a source index for requirements, not 318 equally weighted implementation tasks. At the start of each phase, select exact factor IDs from the relevant reports and map each to a focused task or an explicit dependency/disposition. Source-index rows marked automatic-only still require investigation when their class/mutation path enters scope. Derived visuals and personal settings need explicit dispositions; they must not disappear between the broad audit and implementation.

Free Build acceptance checks all Free Build-relevant entries before phase 15. Final career acceptance checks the complete supported scope. Remaining evidence gaps are logged with an owner phase and cannot be presented as runtime-verified behavior.

## Focused task format — populate after that phase's audit

For phase **N**, append headings **Na**, **Nb**, **Nc** in dependency order under that phase after its focused audit. For example, the first phase produces **1a, 1b, 1c**, and the second produces **2a, 2b, 2c**. These are naming examples, not prewritten tasks.

Each task records:

- **Status:** Not started / Auditing / Ready / Implementing / Validating / Complete / Blocked by a named decision or evidence gap.
- **Outcome and audit IDs:** the concrete behavior and exact factors addressed.
- **Dependencies and decisions:** prerequisites and relevant accepted owner choices.
- **Scope and evidence:** affected files/objects/native paths and limits of what was verified.
- **Work:** the focused implementation steps produced by the audit.
- **Acceptance:** logical and visual cases, including conflict/interruption/restore where relevant.
- **Completion evidence:** changes, checks/results, remaining gaps and tracker rows updated.

Keep the current focused subplan and its evidence beside its numbered phase. Keep detailed investigation in the linked phase audit so the roadmap remains readable.

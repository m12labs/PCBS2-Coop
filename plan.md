# PCBS2 Co-op implementation plan

Initial roadmap: October 25, 2026. **Free Build comes first; career starts after the Free Build acceptance phase.** This document orders future implementation. Creating the plan does not start a phase.

Implementation status belongs in [sync-progress.md](sync-progress.md). Source evidence and unresolved behavior belong in [docs/audit](docs/audit/README.md). This plan owns phase order, accepted scope decisions, focused work plans and phase completion records.

## Current position

- Broad static audit completed; asset/runtime verification and individual native-path gaps remain recorded in the audit.
- Current implementation: two-player LAN host/join with an identity/compatibility handshake, a host-ordered world change pipeline (exercised only by diagnostic markers), and remote capsule position/yaw/presence. No game world state is shared yet.
- Phase 1 is **Complete** (closed October 25, 2026 on the owner's go-ahead; the visual capsule check moved to phase 2's first in-game run). Phase 2 is **Complete** (one shared Free Build world with drop-in join; see below). Phase 3 is next and needs its focused audit. Phases 3–18 are **Not started**.
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
| Pause and travel | During a co-op session the game runs in the background and nobody's pause menu (host or guest) freezes the shared world; sync keeps going (owner, October 25, 2026, replacing "host pause freezes both"). Guest menu remains personal; host controls load/workshop/day changes. | 2, 7–8, 12, 17 |
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

**Status: Complete.** Focused audit: [docs/audit/phase-1.md](docs/audit/phase-1.md). Owner decisions for this phase (October 25, 2026): the host orders all world changes; the guest waits for the host's confirmation; a different mod/protocol/game build is refused while DLC or part catalog differences only warn; players stay labelled Host/Client with a hidden install ID.

#### 1a. Focused audit and owner decisions

- **Status:** Complete.
- **Outcome and audit IDs:** phase evidence and contracts recorded; SS-002–SS-041 subset, PW-001–003/015, CP-024/025/028 (see the audit's factor table).
- **Dependencies and decisions:** the four phase 1 owner answers above.
- **Scope and evidence:** transport source; native `StartNewGame`, `ResetFreebuild`, pause, `SaveLoadSystem` statics; identity declarations (`PartInstance`, `ComputerSave`, `BenchSlot`); `boot.config`.
- **Work:** audit document; questions asked before any dependent code.
- **Acceptance:** every phase 1 factor has a disposition or named owner phase.
- **Completion evidence:** [phase-1.md](docs/audit/phase-1.md).

#### 1b. Join handshake and compatibility

- **Status:** Complete (loopback + one local two-instance run).
- **Outcome and audit IDs:** each peer sends `PeerIdentity` (install ID, mod, protocol, game version + build GUID, DLC list, part catalog hash/count); incompatible joins are refused with a readable reason, warnings are shown on the in-game status line. SS-002, SS-003, SS-004, SS-012, CP-028.
- **Dependencies and decisions:** mismatch policy decision.
- **Scope and evidence:** [Compatibility.cs](src/PCBS2Coop.Network/Compatibility.cs), [CoopSession.cs](src/PCBS2Coop.Network/CoopSession.cs), [GameInfo.cs](src/PCBS2Coop.Plugin/GameInfo.cs). Build GUID from `PCBS2_Data/boot.config`; DLC from `DlcManagerAssetBundles.Instance.LoadedDlcs`; catalog from `PartsDatabase.s_instance.m_parts`.
- **Work:** handshake in the connect request, `Welcome` from host, guest double-checks; install ID in BepInEx config.
- **Acceptance:** identical installs join without warnings; different build refused with reason on the guest; DLC difference joins with warnings on both; third player refused with reason.
- **Completion evidence:** network suite (90/90) and run R1. Different-build and DLC cases only in loopback (no second install available).

#### 1c. Host-ordered change pipeline

- **Status:** Complete (fault-tested in memory and over loopback; exercised in game with diagnostic markers).
- **Outcome and audit IDs:** `WorldSync`: operation IDs, host validation, revisions per world generation, reliable ordered delivery, duplicate/out-of-order handling with remembered outcomes, stale generation refusal/drop, gap and change-hash divergence detection, size limits. SS-011, SS-016, SS-018, SS-020, SS-021, SS-025 (partial), SS-038, SS-039 (partial), SS-041.
- **Dependencies and decisions:** host decides; wait for host OK.
- **Scope and evidence:** [WorldSync.cs](src/PCBS2Coop.Network/WorldSync.cs), [Protocol.cs](src/PCBS2Coop.Network/Protocol.cs).
- **Work:** pipeline, message types `Welcome`/`WorldReset`/`Command`/`Mutation`/`CommandResult`, diagnostic marker command.
- **Acceptance:** concurrent requests apply in one order on both peers; invalid/unknown/oversize/truncated/duplicate/stale requests change nothing and report once; lost or corrupted changes are detected.
- **Completion evidence:** network suite; run R1 (identical revision 10 and hash on both peers, invalid marker refused).

#### 1d. Object and player identity primitives

- **Status:** Complete (unit-tested; no game objects bound yet by design).
- **Outcome and audit IDs:** `PlayerId`, `ActorSlot`, `OperationId`, `NetObjectId`, `NetObjectIdAllocator`, `NetObjectRegistry<T>` with tombstones. SS-012, SS-013, SS-014, SS-024 (foundation).
- **Dependencies and decisions:** none beyond 1a.
- **Scope and evidence:** [Identity.cs](src/PCBS2Coop.Network/Identity.cs), [ObjectRegistry.cs](src/PCBS2Coop.Network/ObjectRegistry.cs). Game data has no instance IDs for parts/PCs/benches (audit).
- **Work:** types and registry. Binding PCs/parts/benches is phase 3/4; the allocator's next value must be saved with co-op worlds (phase 2/8).
- **Acceptance:** IDs never reused; deleted IDs resolve as retired; world reset clears.
- **Completion evidence:** network suite.

#### 1e. Plugin integration on the main thread

- **Status:** Complete.
- **Outcome and audit IDs:** session/world wired into `CoopController`; host starts a generation on entering the workshop (provisional until phase 2); position validation; out-of-sync and compatibility warnings on the status line; dialog waits for the full handshake. SS-011, SS-027, SS-038, SS-040.
- **Dependencies and decisions:** 1b–1d.
- **Scope and evidence:** [CoopController.cs](src/PCBS2Coop.Plugin/CoopController.cs), [CoopRunner.cs](src/PCBS2Coop.Plugin/CoopRunner.cs), [WorldHandlers.cs](src/PCBS2Coop.Plugin/WorldHandlers.cs), [MultiplayerMenu.cs](src/PCBS2Coop.Plugin/MultiplayerMenu.cs).
- **Work:** as above. Contract: `Apply` makes only the change; remote activity is never written into `GameController`/`WorkshopController` singleton fields.
- **Acceptance:** plugin builds against real interop; existing Host/Join/capsule flow unchanged.
- **Completion evidence:** Release build; run R1.

#### 1f. Diagnostics and controlled test setup

- **Status:** Complete.
- **Outcome and audit IDs:** self test isolates saves in a disposable folder and only auto-accepts overwrite prompts there; exchanges markers and logs a comparable world summary; opt-in census (`PCBS2COOP_DIAG=1` or self test) records identity, save paths, load/pause flags, bench slots and scene roots. SS-040; test-safety finding on the shared `Saves` folder.
- **Dependencies and decisions:** working rule 5 (owner's saves untouched).
- **Scope and evidence:** [AutoTest.cs](src/PCBS2Coop.Plugin/AutoTest.cs), [Diagnostics.cs](src/PCBS2Coop.Plugin/Diagnostics.cs).
- **Work:** as above.
- **Acceptance:** owner's save files unchanged by a self-test run; both peers log the same world summary.
- **Completion evidence:** run R1 (save hashes identical; census files written).

#### 1g. Phase validation

- **Status:** Complete.
- **Outcome and audit IDs:** exit criteria checked with recorded evidence.
- **Acceptance:** network suite passes; two-instance run passes on both peers; capsule visible to the other player; owner saves untouched.
- **Completion evidence:** network suite 90/90; runs R1 and R2 passed on both peers with identical world summaries; owner saves unchanged. Closed October 25, 2026 when the owner approved moving to phase 2. Carried forward: the visual check that the capsule renders (R2 logs show it spawned 1.3–2.7 m away but just outside the screenshot frame) moves to phase 2's first in-game run, which aims the camera at it; a two-machine run stays recommended and is owned by phase 2 validation. Next phase: phase 2.

### 2. One shared Free Build session and world lifecycle

**Purpose:** place players into the same logical Free Build world safely.

**Scope:** implement a shared host-owned default Free Build world with the accepted new/load/join flow, initial baseline, scene and async-content readiness, independent cameras/input, world generation changes, host pause, personal guest menus, host departure and failed-load recovery. Establish host-owned co-op saves isolated from solo saves before any shared-world write. Preserve supported baseline PC data while later phases add live mutations; unsupported state must be reported explicitly. Do not mistake launching two new local Free Builds for sharing one world.

**Exit:** both peers load the selected baseline and agree on world identity/readiness; one player's UI does not seize the other's controls; stale-world traffic is rejected; failed admission or departure leaves usable menus/worlds according to policy; solo saves are preserved.

**Audit starting points:** SS lifecycle/readiness/snapshots; PW scenes/doors/state; CP mode/save boundaries.

**Status: Complete.** Focused audit: [docs/audit/phase-2.md](docs/audit/phase-2.md). Owner decisions for this phase (October 25, 2026): drop-in join at any time with a copy of the host's current world; one co-op Free Build world (new or continue) in `Saves\Coop`; the guest keeps a separate joined copy; Host dialog gets New world / Continue; no shared pause during a session; guest menu personal.

#### 2a. Focused audit and owner decisions

- **Status:** Complete.
- **Outcome and audit IDs:** native save/load/new/continue/exit/pause paths and readiness signals established; decisions recorded. SS-006–SS-011, SS-022, SS-025–SS-026, SS-029, SS-032, SS-034–SS-035, SS-038–SS-039, SS-042, CP-001, CP-022, CP-024, CP-026, CP-055–CP-059, PW-003, PW-012, PW-031–PW-032.
- **Completion evidence:** [phase-2 audit](docs/audit/phase-2.md); probe run P1 (owner saves unchanged).

#### 2b. Co-op save folders

- **Status:** Complete.
- **Outcome and audit IDs:** co-op worlds never touch solo saves (SS-032, CP-022, CP-024, CP-059).
- **Dependencies and decisions:** `Saves\Coop`; guest copy in `Saves\Coop\Joined`.
- **Scope and evidence:** `SaveLoadSystem.s_saveDir` redirect (R: P1), shipped default build copy (N: PlayFree lambdas).
- **Work:** `CoopSaves` folder manager (host, guest session, joined copy, transfer); restore only on the main menu; default build copy; detect an existing co-op world.
- **Acceptance:** solo `Saves` files byte-identical after host and guest sessions; new co-op world has the default PCs.
- **Completion evidence:** `CoopSaves` ([CoopSaves.cs](src/PCBS2Coop.Plugin/CoopSaves.cs)); default build copied once; restore only on the main menu. S1–S3: owner `Saves` hashes unchanged; each instance wrote only inside its `Coop` folder; new co-op world has the default PCs (2 cases).

#### 2c. Snapshot transfer and admission (network layer)

- **Status:** Complete.
- **Outcome and audit IDs:** a guest enters the world only from a host snapshot; changes around the snapshot apply exactly once (SS-022, SS-038, SS-039, SS-041).
- **Work:** new messages (snapshot mark, file header, chunks, abort, guest status); bounded and hashed transfer; guest hold-and-replay of world messages after the mark; mod-state providers in the snapshot; host learns when the guest is in the world; host departure reason.
- **Acceptance:** network tests: late join with earlier changes, changes during transfer, corrupt/oversized/misnamed/out-of-order chunks, disconnect mid-transfer, rejoin, copy transfer.
- **Completion evidence:** [WorldTransfer.cs](src/PCBS2Coop.Network/WorldTransfer.cs), admission in `WorldSync`/`CoopSession`. Network suite 142/142 (all listed cases).

#### 2d. Host world: New world / Continue, snapshot capture, guest copy updates

- **Status:** Complete.
- **Outcome and audit IDs:** host picks the co-op world in the Host dialog; snapshots taken on demand; host saves sent to the guest as copies (SS-009, CP-026, CP-056).
- **Work:** dialog buttons and confirm; `CoopSaves` host mode; generation on world ready; snapshot via `CreateSaveGame` into the transfer folder with save-name restore; Harmony hook on `CreateSaveGame` for copies; wait while carrying.
- **Acceptance:** New/Continue load the co-op world; snapshot doesn't change the host's save name; host save updates the guest copy.
- **Completion evidence:** Host dialog New world / Continue with confirm; generation on world ready; snapshot via `CreateSaveGame` into `Coop/Transfer` with save name restored; [GamePatches.cs](src/PCBS2Coop.Plugin/GamePatches.cs) save hook (fires for native calls). S3: Continue enabled only after a co-op save and loads it; host save copied to the guest.

#### 2e. Guest world: receive, load, ready, hold/replay

- **Status:** Complete.
- **Outcome and audit IDs:** guest loads the host's world through `ContinueFreeBuild` from its session folder and reports ready (SS-010, SS-011, PW-032).
- **Work:** join dialog progress; write file after the hash check; readiness signal; complete admission; timeouts; status line.
- **Acceptance:** both peers' census match after admission; guest summary matches host summary.
- **Completion evidence:** [WorldReadiness.cs](src/PCBS2Coop.Plugin/WorldReadiness.cs); guest loads via `ContinueFreeBuild` from its session folder; join dialog progress. S1/S3: identical summaries; S3 held 1 change during the load; shared census identical.

#### 2f. Departure and failure recovery

- **Status:** Complete.
- **Outcome and audit IDs:** SS-007, SS-025, SS-042, PW-003.
- **Work:** host ends the session with a reason on menu/quit; guest returns to the menu and sees the reason; guest leaving keeps the host playing; failed transfer/load returns the guest to a usable menu; save folder restored on the menu.
- **Acceptance:** host to menu → guest at menu with reason; guest to menu → host keeps world and accepts a rejoin.
- **Completion evidence:** Host leaving → guest on the menu with "The host ended the session." (S1–S3); guest leaving → host keeps hosting (S3 round 2); load failure reason reaches the host (loopback); save folder restored on the menu for both. In-game load failure/timeout exercised only through the network layer.

#### 2g. Background and pause rule

- **Status:** Complete.
- **Outcome and audit IDs:** no shared pause; sync never stops (SS-029). `Application.runInBackground` is already True (R: P1); the mod keeps it on during a session.
- **Acceptance:** host pause during the self test: markers and positions still flow both ways.
- **Completion evidence:** `runInBackground` held on during a session; S3: 50 positions and all markers during a 3 s host pause.

#### 2h. Phase validation

- **Status:** Complete.
- **Acceptance:** network suite passes; two-instance run: late join, changes during transfer, host save → guest copy, host pause, departure; capsule visible in a screenshot (carried from 1g); owner saves untouched.
- **Completion evidence:** Network suite 142/142; runs S1–S3 passed on both peers; capsule visible in both S3 screenshots (closes the 1g carry-over); owner saves unchanged. Two-machine run remains recommended (phase 14 or the owner's own test). Next phase: 3, starting with its focused audit.

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

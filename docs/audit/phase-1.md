# Phase 1 focused audit: synchronization foundations

October 25, 2026. Focused audit for [plan phase 1](../../plan.md#1-synchronization-foundations-and-focused-evidence). It re-checks the transport, the native load/save/pause paths and the identities that later phases need, records the owner's phase 1 decisions, and defines the contracts implemented in tasks 1a–1g. The broad registers remain the source index: [session](session-and-consistency.md), [player/workshop](player-workshop-and-presentation.md), [career/persistence](career-economy-and-persistence.md), [coverage](coverage-and-evidence.md).

Evidence labels follow the [audit README](README.md): **M** mod source, **D** declaration, **N** native ISIL excerpt, **I** inference, **R** runtime-verified in a named run below. Nothing in this phase shares world state yet; it builds the rules and plumbing that the world phases use.

## Starting state (M)

- Git: the baseline (proof of concept, decompile tooling, broad audit and roadmap) is commits `c0190af` and `bb04fa1` (IDs after the October 25, 2026 history rewrite that removed local paths); no unrelated edits found. Phase 1 changes are on top of them, uncommitted.
- Transport: [CoopSession](../../src/PCBS2Coop.Network/CoopSession.cs) one host, one IPv4 guest, LiteNetLib 2.1.4. Before this phase the only message was `PlayerState` (sequenced), and the connection key was the mod version string.
- Plugin: `CoopRunner.Update` polls on the main thread; the remote capsule hides after two seconds without a state; returning to `Main_Menu` stops the session.
- Test setup: the opt-in two-instance self test auto-accepted the "New Game" overwrite prompt **in the game's real save folder** (see [Saves](#saves-and-test-isolation)).

## Build and content identity

| Item | Evidence | Finding |
| --- | --- | --- |
| Installed build vs decompile | N/D: [manifest](../../artiffacts/decompiled/manifest.json); `PCBS2_Data/boot.config` | `boot.config` has `build-guid=92d05e5a20c14d41aacbb9c6a804d5a3`, the GUID the decompile manifest records; binary/metadata hashes were matched in the broad audit. The mod reads this GUID at runtime, so it does not depend on Unity's `Application.buildGUID` being present in the IL2CPP build. |
| Game version string | N: `GameController` and `LogoSplash` call `Application.get_version` | Available to the mod as `Application.version`. |
| DLC packs | D: [DlcPacks](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/DlcPacks.cs), [DlcManagerAssetBundles](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/DlcManagerAssetBundles.cs) | Enum `DLCPack_PCBS1DecoratorTheme`, `DLCPack_FallGuys`, `NON_DLC`; runtime list `DlcManagerAssetBundles.Instance.LoadedDlcs`. |
| Part catalog | D: [PartsDatabase](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/PartsDatabase.cs) `s_instance`, `m_parts : Dictionary<string, PartDesc>` | Catalog identity = hash of the sorted part ID keys plus their count. Whether it is loaded on the main menu is a runtime question (census). |

## Native paths re-checked for world boundaries

| Path | Evidence | Finding / consequence |
| --- | --- | --- |
| `LevelLoadPersistency.StartNewGame(GameMode)` | N: [ISIL](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/LevelLoadPersistency.txt#L2064) | Returns immediately if `isLevelLoading` (0x90) is set, then sets it. `startFreebuild = (mode == 2)`. CAREER → `gameModeToLoad=1`, scene `Stage_02_Workshop_Shop_Rundown`; FREEBUILD → 2, `Stage_02_Workshop`; NEWGAMEPLUS → 3, `Stage_02_Workshop` (also clears tutorial hint lists). Subscribes `sceneLoaded` (not for NGP) and calls `NewLoadingScreen.LoadScene`. **I:** a world incarnation begins at this call or at a load, and `isLevelLoading` is the game's own re-entrancy guard; phase 2 should hook here rather than infer from scene names. |
| `LevelLoadPersistency.ResetFreebuild()` | N: [ISIL](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/LevelLoadPersistency.txt#L1730) | Sets `startFreebuild`, `resetFreebuild`, `gameModeToLoad=2`; if `s_saveDir + "/" + s_defaultFreeBuildSaveName` exists it loads that save through `LoadGameFromDir`, otherwise loads `Stage_02_Workshop` directly. **I:** "reset" can mean "load the default Free Build file", so the shared baseline in phase 2 must name the file it starts from. |
| Load entry points | D: [LevelLoadPersistency](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/LevelLoadPersistency.cs#L520) | `LoadGame`, `LoadGameFromDir`, `LoadGameFromSaveLoadMenu`, `LoadGameFromCachedSaves`, `LoadAfterMainMenu`, coroutine `LoadGameCoroutine`, `OnSceneLoaded`. All are world-generation boundaries. |
| Pause | N: [GameController ISIL](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/GameController.txt#L11563) | `PauseGame` sets `paused` (0x80) only when `allowPause` (0x70) is true; `UnpauseGame`/`SetPaused` write the flag. No timeScale change in these bodies. Clock consumers remain a phase 2 audit item. |
| Save folder | N: [SaveLoadSystem .cctor](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/SaveLoadSystem.txt#L31102) | `s_saveDir = "Saves"` (relative), `s_freeBuildSaveName = "freebuild.binary"`, `s_autoFreeBuildSaveName = "autofreebuild.binary"`, `s_defaultFreeBuildSaveName = "defaultfreebuild.binary"`, `s_SceneTransitionSaveName = "TEMP_SAVE_SCENE_TRANSITION.binary"`, `kCurrentSaveVersion = 31`. |
| Bench ordering in saves | D: [SaveLoadSystem](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/SaveLoadSystem.cs#L1005) `BenchSlotComparer`, `Comparison<BenchSlot>`, `Func<BenchSlot, Int32>` closures; [SaveGame](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/SaveGame.cs#L31) `perifsOnBenches : Dictionary<Int32, …>` | The game identifies benches in saves by a sort order / integer key, not by a stored ID. Candidate for bench identity in phase 3 (to verify against the census). |

## Identity findings

| Kind | Evidence | Finding |
| --- | --- | --- |
| Part instance | D: [PartInstance fields](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/PartInstance.cs#L33) | Only `m_partId` (catalog string), `m_decorationId`, `m_jobId`, condition and flags. **No per-instance ID.** Two identical RAM sticks are indistinguishable by game data. |
| PC | D: [ComputerSave](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/ComputerSave.cs#L858), [ComputerCareerStatus](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/ComputerCareerStatus.cs#L26) | No PC ID; `jobID` is a career link, not unique in Free Build. |
| Bench / slot | D: [BenchSlot](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/BenchSlot.cs#L148) | `Type` enum (SHOP_COMPUTER, WORKBENCH, STORAGE, DELIVERY, FOR_SALE, WORKBENCH_TOOL), `m_workStation`; no ID. Historical scene dump shows roots named `BenchSlot (n)` / `DeliverySlot (n)`. |
| Player | M | Host/Client labels were presentation roles only. |

**Consequence (I):** the mod must assign its own identity to shared objects. Unity references and catalog IDs can't serve as network identity.

## Owner decisions for phase 1 (October 25, 2026)

| Question | Answer | Effect |
| --- | --- | --- |
| Who orders world changes? | **Host decides.** | The guest sends requests; the host validates, numbers and forwards accepted changes. Same order on both peers. |
| What does the guest see before confirmation? | **Wait for host OK.** | A guest change takes effect when the host's confirmed change arrives (one round trip). No rollback machinery. Prediction can be revisited per action later only with a new decision. |
| Game/content mismatch | **Refuse a different game build; warn on DLC.** | Refused: different mod version, protocol version or game build. Allowed with a visible warning: DLC differences, part catalog differences, catalog not loaded, same install ID. |
| Player names | **Keep Host / Client.** | A hidden per-install player ID is added for later rejoin; labels don't change. |

These answers extend the plan's [owner decisions](../../plan.md#owner-decisions).

## Contracts defined in this phase

### Identity layers

| Layer | Type | Scope and rule |
| --- | --- | --- |
| Install | `PlayerId` (GUID) | Persistent per install, stored in BepInEx config `Identity.PlayerId`. Self-test instances use a temporary ID so two local copies differ. Exchanged at join; basis for rejoin (phase 8). |
| Seat | `ActorSlot` Host/Guest | Per session. Commands are attributed by the connection they arrive on, never by a field in the message. |
| Operation | `OperationId(actor, sequence)` | Sequence starts at 1 per session and only increases. The host deduplicates on it. |
| World object | `NetObjectId` (u64) | Allocated by the host only, never reused within a world (`NetObjectIdAllocator.Next` must be saved with the co-op world later). `NetObjectRegistry<T>` binds IDs to local objects, keeps tombstones for deleted IDs so commands naming them are refused, and clears at a world reset. |
| Catalog | `PartDesc` ID string | Content identity only. Never used as instance identity. |

### World generation, revision and ordering

- A **generation** is one incarnation of the shared world. Only the host starts one (`BeginGeneration`); it resets the revision to 0 and the change hash to its seed and tells the guest (`WorldReset`). In phase 1 the host starts a generation when it enters the workshop scene; phase 2 replaces that trigger with the shared new/load flow and readiness barrier.
- A **revision** numbers accepted changes within a generation. The guest applies only revision `last + 1`; anything else marks it out of sync.
- Each change carries the host's running FNV-1a **change hash**. A guest whose hash differs after applying is marked out of sync.
- Commands and changes stamped with another generation are refused (host) or dropped (guest). A guest's pending requests are abandoned (reported once) when the generation changes or the link drops.
- All reliable world messages share one LiteNetLib `ReliableOrdered` stream, so `Welcome`, `WorldReset`, changes and results keep their order. Player positions stay on the `Sequenced` stream.

### Command handling (host)

Checks, in order: duplicate/out-of-order sequence → world loaded (generation > 0) → generation matches → payload ≤ 16 KiB → known command type → the command's own validator. A refusal carries a reason (`StaleGeneration`, `OutOfOrder`, `UnknownCommand`, `Malformed`, `TooLarge`, `Invalid`, `NoWorld`) and text. The last 256 outcomes are remembered, so a repeated request gets its original answer without being applied again. Accepted guest requests are confirmed by their change; refusals by `CommandResult`.

### Main thread and re-entrancy

`CoopSession.Poll()` runs in `CoopRunner.Update`; validation and application happen inside it, on the main thread. The host's own requests are processed synchronously. A request submitted from inside `Apply` is queued and runs after the current change, never nested. Starting a generation from inside `Apply` is refused. **Contract for later phases:** `Apply` makes the change only; it must not trigger gameplay paths that would submit a second change (callback suppression, SS-026), and native calls it makes must be checked per phase.

### Per-player activity vs single-player controllers

`GameController` (selected item, camera/state stack, input focus) and `WorkshopController` (carried case/package/post-it, active bench) hold **one local actor's** activity ([player/workshop P4/P5](player-workshop-and-presentation.md#evidence-groups)). Rule for all phases: remote activity is modelled as per-actor records tied to `ActorSlot`/`PlayerId` and shared object IDs. It is never written into the local singleton fields, which would take over the local player's camera and controls.

### Wire validation

Unknown message types and messages arriving before the handshake are ignored and counted. Truncated or corrupt messages are caught (LiteNetLib 2.1.4 throws `InvalidOperationException` on short reads) and counted, never crash the session. Player positions must be finite and within ±10 km. Strings are length-capped; DLC lists ≤ 32 entries; warnings ≤ 16.

## Saves and test isolation

**N/R finding:** `s_saveDir` is the relative path `Saves`, resolved against the game's working directory: `<GameDir>\Saves`. Two local instances therefore share one save folder. The previous self test accepted the "New Game" overwrite prompt there; `autofreebuild.binary.zip` in that folder was last written 2026-10-24 23:43, during the earlier self-test session (I: likely written by it).

Phase 1 change: the self test redirects `SaveLoadSystem.s_saveDir` to `BepInEx/PCBS2Coop-diag/saves-<role>` (recreated empty each run) once the main menu exists, and the overwrite-prompt auto-accept only runs while that redirect is confirmed. Otherwise the prompt is left for a person. **Phase 2 consequence:** co-op saves must use their own folder and must not rely on the shared relative `Saves` path.

## Runtime evidence

### Network suite (run 2026-10-25, `dotnet run --project tests/PCBS2Coop.Network.Tests -c Release`)

**90/90 checks passed.** In-memory fault injection: ordering of concurrent host/guest requests, wait-for-confirmation, validator refusal with reason, over-long/unknown/unreadable/oversize/truncated requests, misrouted message types, duplicate requests (accepted and refused), out-of-order sequences, requests made for an older generation, changes stamped with an older generation, reset to an older generation, a lost change (gap), a corrupted change hash, re-entrant submits from `Apply`, guest-only generation start refused, link loss abandoning pending requests, rejoin sequence restart. Loopback over real UDP: handshake and player IDs, DLC difference joins with warnings on both sides, positions intact, NaN position dropped, request before a world is refused, 40 interleaved changes applied identically (same revision and hash), third player refused with the reason text, guest leave/rejoin adopting the host's world position, different game build refused with the reason shown to the guest, host stop, no-host failure. The reject-with-reason path (`ConnectionRequest.Reject(bytes)` → `DisconnectInfo.AdditionalData` on `ConnectionRejected`) works in LiteNetLib 2.1.4 despite its doc comment.

### Two-instance game run R1 (2026-10-25 01:38, build GUID `92d05e5a…`, mod 0.1.0, protocol 2)

Started by the agent: `PCBS2COOP_AUTOTEST=host` and `=join`, two local `PCBS2.exe` processes on 127.0.0.1:7777. Logs `BepInEx/LogOutput.log` (host) and `LogOutput.1.log` (join); census and screenshots in `BepInEx/PCBS2Coop-diag/`.

| Check | Result |
| --- | --- |
| Save isolation | **R:** both instances redirected `s_saveDir` from `Saves` to `BepInEx/PCBS2Coop-diag/saves-<role>` on the main menu. No "New Game" prompt appeared (previous runs showed it), so PlayFree's existing-save check followed the redirect. Owner's `Saves/*.zip` SHA-256 identical before and after. The `Saves` directory's own modification time changed at launch, before the redirect could run; no file in it changed. Neither disposable folder received a save during the ~50 s run, so autosave/manual-save paths were **not** exercised. |
| Identity | **R:** `Application.version` = `0.1` (not useful for telling patches apart; the build GUID is). Build GUID read from `boot.config` on both. DLC list empty. Part catalog loaded **on the main menu** already: 2816 parts, hash `822939a6afd9935b`, identical on both. Temporary player IDs `5b6ae741` (host) / `d0cab99a` (join). No compatibility warnings. |
| Handshake and world | **R:** host started generation 1 on entering `Stage_02_Workshop`; guest joined at generation 1, revision 0. |
| Change pipeline | **R:** five markers per side, submitted while joined. Both logs: `gen 1 rev 10 hash c1e9dcfe96d225d1`, markers `host-0…4, join-0…4` in the same order, divergence none, 0 malformed/unknown/duplicate. Guest's empty marker `G#6` refused by the host as `Invalid` with its reason. `AUTOTEST: PASSED` on both. |
| Existing capsule/session path | **R (logs):** "Spawned remote player" on both sides; position stream active (host received 227, guest 348 states, 0 invalid). The end screenshots happened to face away from the other player, so the capsule's appearance was **not** visually re-confirmed in R1. |
| Run R2 (same setup, self test now waits for the capsule) | **R:** both `PASSED`; identical summary to R1 (`gen 1 rev 10 hash c1e9dcfe96d225d1`, same marker order, 0 malformed/invalid). The remote capsule existed on both peers when the screenshots were taken, 1.3–2.7 m from the camera but 53–80° off the view centre, so it was outside or at the edge of the frame. **The capsule's appearance remains visually unconfirmed in phase 1 runs** (rendering code unchanged since the earlier proof-of-concept run). Owner's saves unchanged again; closing the game wrote no save to the disposable folders. |
| Census | **R:** workshop BenchSlot paths are unique (no duplicates): 6 benches `BenchSlot (1)…(6)`, each with a `WORKBENCH` slot and a `WORKBENCH_TOOL` slot under `Logic/WorkBench`; `ShopComputer/ComputerSlot/Slot` (SHOP_COMPUTER); 16 `DeliverySlot (n)/Slot` (7 active); 4 `Shop/ShopDisplay…/Slot` (FOR_SALE). 47 PostItSlots, 2 Cupboards, 1 WorkshopController, 0 Case and 0 Package eight seconds after a new Free Build loaded. `LevelLoadPersistency` after load: `gameModeToLoad=FREEBUILD`, `isLevelLoading=False`, `loadedFromMainMenu=True`. The **main menu** also has a `WorkshopController` and a `BenchSlot_LeftWall` bench (the menu showcase PC), so "a WorkshopController exists" does not mean "in a workshop". |

## Factor coverage

| Factor | Phase 1 disposition |
| --- | --- |
| SS-002, SS-003, CP-028 | Implemented: identity handshake; refuse mod/protocol/game build, warn DLC/catalog (1b). |
| SS-004 | Implemented: refusal reasons reach the guest's dialog (1b). |
| SS-011 | Implemented: world generations, stale refusal/drop (1c). Trigger is provisional until phase 2. |
| SS-012, PW-015 | Implemented: persistent install ID + session seat; labels unchanged by decision (1b, 1d). |
| SS-013, SS-014, SS-024 | Foundation: host-allocated, never-reused object IDs with tombstones (1d). Binding real PCs/parts/benches is phase 3/4; bench identity candidate recorded. |
| SS-016, SS-018 | Decided and implemented: host orders all changes (1c). Conflict rules per object family arrive with those phases. |
| SS-019 | Contract recorded (per-actor records, not singleton writes). Implementation with phase 3. |
| SS-020, SS-021, SS-041 | Implemented and fault-tested in memory: operation IDs, duplicates, reordering, loss, corruption, reliable ordered stream (1c, 1g). |
| SS-025 | Partial: refused/abandoned requests are reported to the requester; releasing UI/tool state belongs to each action family. |
| SS-026, SS-027 | Contract recorded; main-thread application implemented. Callback suppression is per phase. |
| SS-038 | Implemented: bounds, sizes, unknown/early message handling (1c, 1e). |
| SS-039 | Partial: 16 KiB payload cap; chunked large transfers deferred to phase 2 (snapshots) and phase 12 (images). |
| SS-040 | Implemented: change hash divergence detection, counters, world summary line, census dumps (1c, 1f). |
| SS-005, SS-006, PW-001–003 | Existing capsule/session path kept; checked by the self test (1g). |
| CP-024, CP-025, SS-032 | Finding only: the game's save folder is shared and relative; save format is not a wire format. Co-op saves are phase 2. |

## Evidence gaps and owners

| Gap | Owner phase |
| --- | --- |
| Readiness signals for a loaded world (scene loaded, `PartsDatabase` async PC/part/cable operations drained, decorator loads) | Phase 2 |
| Pause clock consumers beyond the `paused` flag | Phase 2 |
| Whether every save path (manual, auto, scene transition) follows `s_saveDir` | Phase 2 (co-op save isolation) |
| Stable bench identity: save comparator order vs scene path; any duplicate paths | Phase 3 |
| Binding `NetObjectId`s to PCs/parts/packages and duplicate identical parts | Phases 3–4 |
| Extracted scene/prefab/Animator assets | Owners as listed in the [backlog](open-questions-and-runtime-checks.md#missing-directories-and-asset-evidence) |
| Two-machine run (separate installs, real LAN) | Phase 14 acceptance; recommended earlier when convenient |

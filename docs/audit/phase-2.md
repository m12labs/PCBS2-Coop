# Phase 2 focused audit: one shared Free Build session and world lifecycle

October 25, 2026. Starting point: phase 1 complete (commit `cfe7e0a`, ID after the path-removal history rewrite), working tree clean. Game build GUID `92d05e5a20c14d41aacbb9c6a804d5a3`, `Application.version` 0.1, part catalog 2816 parts (hash `822939a6afd9935b`). Evidence labels: **N** native ISIL, **D** decompiled declarations, **R** observed in the running game (probe run P1 below), **I** inference.

## Before this phase

- The host's Host button starts a session and calls `NewMainMenu.PlayFree()`; the guest connects, then also calls `PlayFree()`. Each peer starts its **own** new Free Build. Nothing about the world is shared; the guest's world is whatever its own save folder holds.
- Both peers read and write the game's shared relative `Saves` folder. A guest pressing Join could overwrite its solo Free Build save (the overwrite prompt shows when a solo save exists).
- The host's workshop entry starts a world generation (phase 1 provisional trigger). The guest adopts the host's world position from Welcome without receiving the world.
- Leaving: the session stops on return to the main menu. A guest whose host leaves stays in its own workshop.

## Owner decisions (October 25, 2026)

| Question | Answer |
| --- | --- |
| When can the guest join? | **Drop in any time** the host is in its world. The guest receives a copy of the host's current world. The same mechanism serves rejoin. |
| What can the host start from? | **New or continue one co-op Free Build world**, like the game's single Free Build slot. Solo saves are never read or written. |
| Where do co-op saves live? | **`Saves\Coop`** next to the normal saves. |
| Guest saving | **The guest keeps a copy**: a separate "joined" file in the guest's `Saves\Coop` folder, updated at join and whenever the host saves. It never replaces the guest's own co-op world or solo saves. Using it (e.g. hosting from it) is a later menu option. |
| Host menu | **Two buttons in the Host dialog**: New world / Continue (Continue disabled without a co-op save). New world asks before replacing an existing co-op world. |
| Pause | **Replaces the earlier "host pause freezes both" decision.** During a session the game runs in the background and nobody's pause menu (host or guest) freezes the shared world; sync keeps going for everyone. Solo play is unchanged. |
| Guest menu | Personal; the world keeps running behind it. |

The plan's owner decision table is updated to match (Pause and travel row).

## Native paths

| Path | Evidence | Finding |
| --- | --- | --- |
| Save folder and names | N: [SaveLoadSystem .cctor](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/SaveLoadSystem.txt#L31102) | `s_saveDir = "Saves"`; `freebuild.binary`, `autofreebuild.binary`, `auto.binary`, `autoendless.binary`, `TEMP_SAVE_SCENE_TRANSITION.binary`, `defaultfreebuild.binary`; utility saves at `StreamingAssets/UtilitySaves`. |
| Save listing | N: [GetSaveGamesWindows](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/SaveLoadSystem.txt#L978) | `DirectoryInfo(s_saveDir).GetFiles()` with no search option (top level only), sorted newest first; skips `AchievementProgress`; compresses loose `.binary` into `.binary.zip` and keeps `BACKUP.zip` copies. **I:** subfolders of `Saves` are invisible to the solo save list. |
| Save write | N: [CreateSaveGame](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/SaveLoadSystem.txt#L4500) | Blocks on any pending save, collects data on the main thread (`CreateSaveGameInternal`), then writes on thread `PCBS2_SaveGame` to `s_saveDir + "/" + filename`. **R:** the path is fixed at call time, so `s_saveDir` can be restored right after the call; the file lands as `<name>.binary.zip` with one entry `<name>.binary`; `WaitingOnThread()` turns false when written. **R side effect:** it sets `LevelLoadPersistency.SaveName` and the opened save name to the new file. |
| Save entry points | N: [SaveCurrentGame/AutoSave/FreebuildSave](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/SaveLoadSystem.txt#L3177) | Save writes funnel into `CreateSaveGame`. `FreebuildSave` gates on mode 2 and empty hands; **phase 3b B1 correction:** the ordinary pause-menu New Save Game path still saves a carried PC. The carry restriction is not universal. Hooking `CreateSaveGame` sees actual writes. Autosave cadence remains unmeasured. |
| Continue Free Build | N: [NewMainMenu.ContinueFreeBuild](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/NewMainMenu.txt#L7595) | **Correction from phase 3b:** the string is tested for emptiness, not used as a filename. A nonempty string prefers the default Free Build before falling back to another Free Build save. The earlier redirected-folder probe established that a save loaded, but did not distinguish an edited save from the default. Host and guest now set `gameModeToLoad = FREEBUILD` and call `LevelLoadPersistency.LoadGameFromDir` with the exact name. Edited-world reload and guest evidence: [phase 3b](phase-3.md#exact-save-selection-regression). |
| New Free Build | N: [PlayFree](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/NewMainMenu.txt#L6170), [lambdas](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/NewMainMenu_NestedType___c__DisplayClass81_0.txt) | If a Free Build save exists → "New Game" prompt; yes → `ContinueFreeBuild(default)` when `defaultfreebuild` exists in `s_saveDir`, else `StartNewGame(FREEBUILD)`. **R:** in an empty folder the new world has no PCs (`cases=0`). The normal new Free Build loads the shipped default build, which the game copies into `Saves` as `defaultFreebuild.binary.zip` (same date as `StreamingAssets/UtilitySaves/defaultFreebuild.binary`, 2.5 MB). **Consequence:** the co-op folder needs a copy of the shipped default file so a new co-op world matches a normal new Free Build. |
| Return to menu | N: [ExitPanel.Exit](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/ExitPanel.txt#L836) | Sets PlayerPrefs "Come from game", calls `NewLoadingScreen.LoadScene("Main_Menu", …)`; no save call found in the body. **R:** `NewLoadingScreen.LoadScene("Main_Menu", false)` from the mod returns to a working menu. |
| Pause | N: [GameController](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/GameController.txt#L11563), callers `UIState`/`InventoryState.OnStateEnter` | **R:** `PauseGame()` sets `paused = True` and leaves `Time.timeScale = 1`; no `set_timeScale` call exists in Assembly-CSharp ISIL. `Application.runInBackground` is already **True** in this build. **I:** the mod's own update loop never stops for pause or focus loss; what the `paused` flag gates inside game systems belongs to the phases that share those systems (simulation, phase 7). |
| Readiness | D: `PartsDatabase.m_load{PC,Part,Plug,Cable,Prefab}Operations`, `SaveLoadSystem.SpawningPC`, `WorkshopController.IsCaseLoading`, `LevelLoadPersistency.isLevelLoading`/`LoadInProgress` | **R (empty world):** `isLevelLoading` is True when the scene activates and False ~3 s later; the operation lists stayed empty. The world with the default PCs is measured during implementation (2d). |

## Probe run P1 (October 25, 2026, one instance, disposable saves)

`PCBS2COOP_AUTOTEST=host PCBS2COOP_PROBE=1`, [Probe.cs](../../src/PCBS2Coop.Plugin/Probe.cs). Saves redirected to `BepInEx/PCBS2Coop-diag/saves-host`; probe save in `…/probe-save`. Results are quoted in the table above. Probe save of an empty world: 69 810 bytes zipped (397 091 raw). The owner's Free Build autosave is 2 226 427 bytes zipped. Owner `Saves` files hashed before and after: unchanged.

## Contracts introduced by this phase

- **Save folders.** While a co-op session owns a world, `s_saveDir` points at a co-op folder; it is put back only once the game is on the main menu again (never while a world is loaded, so autosaves can't land in the solo folder).
  - Host: `<saves>\Coop` (the co-op world slot, game file names unchanged). The game's default build is copied in once.
  - Guest: `<saves>\Coop\GuestSession` (throwaway, emptied at each join). The guest copy lives in `<saves>\Coop\Joined` (latest file only).
  - Host snapshots are written to `<saves>\Coop\Transfer` (emptied each time); the game's current save name is restored afterwards.
- **Admission.** A guest is connected (handshake) before it is *in the world*. It enters the world only by loading a host snapshot. Before that it can't submit changes and drops world changes. The host marks the snapshot position (generation, revision, change hash, mod state) on the ordered stream at the moment of capture; the file follows in chunks; the guest holds every world message after the mark and applies them once its world is ready. A guest that can't finish (bad file, timeout, load failure) leaves with a reason.
- **Snapshot wire format.** Snapshot mark → file header (name, size, SHA-256) → chunks (≤ 32 KiB, in order). Names limited to `[A-Za-z0-9_-]{1,48}.binary[.zip]`; size ≤ 64 MiB; the hash must match before anything is written to disk. Copy snapshots (guest copy updates) use the same messages without admission.
- **Readiness.** A peer's world is ready when the workshop scene is active, no level load is in progress, no PC/part/cable/prefab load operations are pending, no PC is spawning, no case is loading, no save is being written, and the player exists — continuously for 2 seconds.
- **World identity.** The host starts a generation when its world becomes ready. A world change by the host requires the main menu, which ends the session, so a guest that receives a new generation after admission leaves with "The host loaded a different world."
- **Departure.** Host leaving the world or game ends the session with a reason; the guest returns to the main menu and sees it. Guest leaving: the host keeps playing and listening; a rejoin gets a fresh snapshot.
- **Personal state.** Camera, input, menus, pause flag and settings stay local; nothing remote writes them.

## Factor coverage

| IDs | Disposition |
| --- | --- |
| SS-006, SS-009, SS-022 (baseline), SS-026 (load path), SS-035 (rejoin = new snapshot) | 2c, 2d, 2e. Live deltas of game state start in phase 3. |
| SS-007, SS-042, PW-003 | 2f (departure, cleanup, return to menu). |
| SS-008, PW-031 | Mode and scene fixed to Free Build `Stage_02_Workshop`; other workshops are phase 12. |
| SS-010, PW-032 | 2e readiness (scene + async operations). |
| SS-011 | 2e generation on world ready; stale/after-admission resets. |
| SS-025 | 2d/2f failed transfer or load leaves both sides usable. |
| SS-029, SS-030 | 2g: no shared pause; clock/simulation consumers of `paused` → phase 7. |
| SS-032, SS-034, CP-022, CP-024, CP-055–CP-059 | 2b save isolation; host owns co-op saves; guest copy. Save/load migration and fresh-process resume of live state → phase 8. |
| SS-038, SS-039 | 2c chunked, bounded, hashed snapshot transfer. Images → phase 12. |
| CP-001, CP-026 | Free Build mode only; career → phase 15. |
| PW-012 | Guest spawn position: measured in 2h (host's save carries its own player data). |
| PW-013, PW-071 | Personal; no change. |

## Acceptance cases

1. Host New world / Continue loads the co-op folder; the solo save list and files are untouched.
2. Guest joins after the host made changes (diagnostic markers): the guest's world summary matches the host's, including the markers made before the join.
3. Changes made while the guest is receiving or loading the snapshot are applied once, in order.
4. Host saves (manual or auto) → guest's joined copy updates; guest's own co-op and solo files untouched.
5. Bad snapshot (wrong hash, oversized, bad name, out-of-order chunk) is refused before anything is written.
6. Host leaves → guest returns to the main menu with the reason; guest leaves → host continues; guest rejoins → fresh snapshot.
7. Host pause: markers and positions keep flowing.
8. Both peers' census in the shared world: same PC count and bench occupancy.

## Evidence gaps

| Gap | Owner |
| --- | --- |
| Autosave cadence | Phase 8 (only matters for copy frequency here). |
| What the `paused` flag gates in game systems | Phase 7 (simulation). |
| Two separate machines | Phase 2 validation (recommended) / phase 14. |
| Carrying a PC while the guest joins (Free Build save refuses while carrying) | Phase 3 (carrying). Here: the snapshot waits until the host isn't carrying. |

## Runtime evidence

All runs: two local instances (`PCBS2COOP_AUTOTEST=host` / `join`), saves isolated under `BepInEx/PCBS2Coop-diag/saves-<role>` (co-op folders inside them), owner `Saves` hashed before and after every run: unchanged.

| Run | What ran | Result |
| --- | --- | --- |
| Network suite | `dotnet run --project tests/PCBS2Coop.Network.Tests -c Release` | **142/142**: late join with earlier changes, changes held during load and applied once, change already in the snapshot not applied twice, unknown/unreadable mod state refused, hold overflow, link loss mid-load, damaged/out-of-order/oversized/unsafely named snapshots refused, load supersedes a copy in flight, 3 MB snapshot over UDP, copy transfer, guest load failure reason reaches the host, host stop reason reaches the guest, rejoin from a fresh snapshot, plus all phase 1 cases (updated for admission). |
| S1 | Round 1 only: New world, late join, markers, host pause, host save, host departure | Both PASSED, identical summary `gen 1 rev 13 hash 78fa60beaac355a4`. Guest received the 3 pre-join markers through the snapshot; same PCs (2 cases, 2 occupied bench slots). Copy of `autofreebuild.binary.zip` reached the guest. Guest returned to the menu with "The host ended the session."; both save folders restored. Capsules overlapped (both players spawn at the saved player position), so no capsule screenshot. |
| S2 | Same plugin as S1 (the rebuilt DLL was not deployed because the S1 games still held it) | Same result as S1: repeatability. |
| S3 | Two rounds. Round 1 as S1 plus a host marker submitted while the guest was loading, guest walks away before both aim at each other. Round 2: host Continue, guest rejoins, guest leaves first. | Both PASSED, identical summary `gen 1 rev 14 hash 9fde003b9734d024`; the guest **held 1** change during its load and applied it once after. 50 positions received during the host's 3 s pause. Capsule visible in both screenshots (carried check from 1g). Shared census: bench slots identical; Case 2, PostItSlot 47, Cupboard 2 on both. Round 2: Continue offered only after a co-op save, loaded `autofreebuild.binary` (`WasLoadedFromSave` True, 2 cases); guest rejoined from a new snapshot; after the guest left the host kept hosting in its world. Files written: only under each instance's `Coop` folder. |

Additional runtime facts from these runs:

- **R:** Harmony postfixes on `SaveLoadSystem.CreateSaveGame` fire for the game's own native call path (`SaveCurrentGame` → `FreebuildSave` → `CreateSaveGame`). In Free Build `SaveCurrentGame` produced one save (`autofreebuild.binary`).
- **R:** A new co-op world from the shipped default build has the default PCs (2 cases on benches), matching a normal new Free Build.
- **R:** Both players spawn at the player position stored in the save (PW-012); they overlap until one moves. Players pass through each other (owner decision), so this is cosmetic.
- **R:** The game shows its "Welcome to Free Build!" popup on the host of a new world; it does not affect the session.
- **R:** After Continue, `LevelLoadPersistency.SaveName` still names the default build file (game behaviour; saves go to `autofreebuild.binary`).

## Phase 2 completion

Exit criteria: both peers load the host's world and agree on its identity and readiness (S1, S3); one player's menus and pause don't affect the other (host pause and popup in S3); stale-world traffic is rejected (network suite); failed admission and departure leave usable menus/worlds (network suite for load failure; S1/S3 for both departure directions); solo saves are preserved (hashes, folder listings). Remaining recommended evidence: a run on two separate machines (phase 14 acceptance or the owner's own test). In-game load failure/timeout paths were exercised only through the network layer.

## Versioning change (owner, October 25, 2026)

After phase 2 the separate network protocol number was removed. The mod version in `Directory.Build.props` is the only version: it is in the connect key and in `PeerIdentity`, and peers must match exactly. Each bump adds one (owner): 0.1, 0.2, 0.3, 0.4…. Phase 1 counts as 0.2 and phase 2 as **0.3**. BepInEx loaded the two-part form as `PCBS2 Coop 0.12.0` during the first version of this change (checked in game), so `0.3` loads as `0.3.0`. Network suite after the change: 141/141 (the protocol-mismatch case was removed; the mod-version mismatch case remains).

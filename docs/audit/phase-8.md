# Phase 8 audit: durable Free Build saves and complete core rejoin

**Status (November 2, 2026): owner decisions recorded; 8b–8f implemented in 0.21 and run in game in owned two-game runs p8a–p8l (results and fixes below, 624/624 model checks). Open: a world over 35 PCs, Steam rejoin, and the owner's own pass of 4f/5d/10b/7e.** The audit itself was done without starting the game (owner request). Evidence comes from the 0.20/0.21 source, the same game dump as phases 6–7 (Unity `6000.2.6f2`, build GUID `92d05e5a20c14d41aacbb9c6a804d5a3`), the network/model suite and owned runs' disposable saves under `<GameDir>\BepInEx\PCBS2Coop-diag`. Lines marked **(decompile)** were read from the dump and have not been checked in a running game.

## Scope (plan.md section 8)

Coherent snapshots across all state from phases 2–7; manual/autosave ownership, schema/version and safe restore; loaded-world admission, guest reconnect, host-alone resume, operation cleanup and full baseline/delta reconciliation; recovery tests for interrupted transfers, installs, boot and running tests. Exit: save/load, guest rejoin and fresh-process resume keep the same PCs, inventory, software, config and results without duplication, stale-world events or touching solo saves.

Audit factors: SS-006, SS-009, SS-020, SS-022, SS-025, SS-031–SS-035, SS-040, SS-042 ([session and consistency](session-and-consistency.md)); CP-022, CP-024–CP-026, CP-055–CP-059 ([career, economy and persistence](career-economy-and-persistence.md)). Career state (CP-001–CP-021, CP-030 onwards) stays with phases 15–18.

## What already works (0.20 baseline)

Earlier phases each carried their own save and rejoin obligations, so most of phase 8's ground is already covered:

- **Save pairing (3c).** Every host save is the game's native file plus a `.coop` sidecar (`CoopSavePair`). The sidecar holds the native file's length and SHA-256, the mod version, the world position (generation, revision, chain hash), every registered world state, and full records for up to two carried PCs. It is written to a temporary file, flushed and renamed after the game's save thread finishes. A native file without a matching sidecar is refused with a message, never loaded with new IDs.
- **Who saves (2b, 0.17).** Only the host writes co-op saves, into `Saves\Coop`. The game's own autosaves are skipped on both games. The host saves on an interval (Co-op Options, default 5 minutes, skipped when nothing changed), before its pause-menu Exit (waits up to 8 s), from the Save screen, and for `/sync`. A save waits while anything is still moving: an edit, latch, panel, PC load, software transfer, sync, snapshot or an out-of-sync state (`SaveBlocker`, `BeforeHostSave`).
- **Solo saves untouched.** `s_saveDir` points into `Saves\Coop` while a co-op world is loaded and is restored only on the main menu. Every owned run since phase 2 checked the owner's save hashes.
- **Late join and rejoin (2c–2e).** A guest always enters from a fresh host snapshot taken in the same frame as its world position. Changes made during the load are held and applied once. A guest that leaves can rejoin the same way.
- **Fresh-process resume (3c–3f).** Continue/Load restores IDs, allocator, tombstones and bindings from the sidecar. A PC carried by someone who isn't there goes to the shared cupboard with its IDs (owner decision). Owned runs C5–C7, E5, F17, G6 and H4 passed this, including a save while the guest carried a PC.
- **Interrupted work.**
  - A guest who leaves mid-edit loses its claim; the host settles the PC from its own game as it is (`NativeComponentEdits.UpdateHost`).
  - A pending pickup or transfer is abandoned or recovered to the cupboard (3f, H2).
  - Activity and claims saved in a file are reset by the generation start's presence changes, so no claim survives a reload.
- **Versions.** A co-op save loads only under the version that wrote it or one listed in `CoopSavePair.SameFormat` (0.20 reads 0.16–0.19). The owner's own co-op worlds in `Saves\Coop` are 0.16 and 0.17 and still load.
- **Repair.** `/sync` (3h) reloads both games from a fresh host save; `PcRepair` (phase 6) reloads one PC on the guest.

## State inventory (phases 2–7)

| State | Where it is kept | Fresh-process resume | Guest join / rejoin | Status |
| --- | --- | --- | --- | --- |
| PCs, parts, locations, allocator, tombstones | `objects` state + native save | Sidecar restore, bindings checked | Snapshot | Verified (3c, C3–C7) |
| Carried PCs | Sidecar carrier records | Absent carrier → cupboard | Guest's own carry spawned after admission | Verified (3e/3f, H4) |
| Activity and claims | `activity` state | Reset at generation start (by design) | Snapshot, then presence | Verified (3d) |
| Workstation peripherals | `workstations` state (optional) + native | Rebound at every generation start | Snapshot | Verified (3g, G6) |
| Slots, latches, screws, panels | `objects` model + native | Native load + bind; clips over sticks closed before saving (owner decision 4d) | Snapshot | Verified (4c/4d, N3 fresh process) |
| Component edits in flight | Not saved: saving waits | — | Snapshot waits; editor leaving → host settles | Code + model tests |
| Cables, splitters, power | Native PC record; `Powered` in the model | Each game powers those PCs on again after a load | Snapshot | 0.15; owner's full test (5d) pending |
| Water loops | Native PC record | Native load | Snapshot | 0.16; owner's full test (10b) pending |
| BIOS config, GPU Tuner values | Native PC record (`BiosConfig`) | Native load | Snapshot | 0.18–0.20 |
| Software, boot drives, benchmark results | Native PC record; in-flight transfer in `software` state (optional) | Saving waits while a transfer runs | Snapshot | 0.19 |
| Open apps and windows | Not saved (the game doesn't either) | PC boots to an empty desktop | Guest matches the host's open apps (phase 6) | As solo |
| Simulation: temperatures, loads, blue screen | Not saved (the game doesn't either) | — | A running test stays idle on the guest (decision 7.4) | As solo |
| Fan curves | Not saved (owner decision 7.5) | Lost, as in solo | Host re-sends them when the guest arrives | As decided |
| Remote player position | Not saved | — | Guest spawns at the host's saved player position | **Question 6** |
| Chat, out-of-sync marker | Session only | — | — | Fine |

## Findings and gaps

### G1. Co-op state fills up at about 35 built PCs

`Protocol.MaxModStateBytes` is 1 MB, for the whole world position state and for each state inside it. Measured with an offline decoder on a disposable test save from run usb-7 (`Co-op_World_1.binary.zip.coop`, the default workshop):

| Item | Size |
| --- | --- |
| Native save | 2.2 MB |
| World state | 60.7 KB, of which `objects` is 58.5 KB (2 PCs, 35 parts, 330 slots/latches/screws) |
| Per fully built PC | about 29 KB |
| Same state compressed (Deflate) | 8.8 KB (about 7× smaller) |

So a world with roughly 35 built PCs on benches and in the cupboard can no longer be saved, joined or synced. No cupboard limit was found in the game's declarations (decompile). The fix needs no product decision: compress the state in the sidecar and the snapshot mark, and raise the limit with a test at a realistic maximum. It changes the save format (version bump; older pairs still readable).

### G2. In-world Load and Free Build Reset are not guarded

**(decompile)** The pause menu opens the game's save screen for both Save and Load (`InGameMenu.ShowSaveMenu(bool IsSave)` → `SaveLoadMenu.Open`). Load goes through `LevelLoadPersistency.LoadGameFromSaveLoadMenu`. Free Build Options' Reset (`FreebuildOptions.OnReset` lambda → `LevelLoadPersistency.ResetFreebuild`) finds `defaultFreebuild` in the current save list and calls `LoadGameFromDir`. That file is in `Saves\Coop` while hosting.

The mod hooks neither path. Its native-load hook only checks a pair the mod itself started. It starts a new world generation only from the main menu or `/sync`.

- **On the host:** the workshop reloads under a shared model that no longer matches, and the guest isn't told.
- **On the guest:** it reloads from its session folder, off the shared stream.

Both would end out of sync at best. Questions 1–2 decide what happens instead; either way these paths must not run unguarded.

### G3. A guest's Save screen writes a useless file

The save hook lets a guest save. The file lands in `Saves\Coop\GuestSession`, which is emptied at the next join and never listed. The guest is told nothing (question 2).

### G4. One crash can leave the newest save unloadable, and Continue picks it anyway

The pair is published only after the game's save thread finishes. The pause-menu Exit waits for it. Closing the game during an interval save, a crash or a power cut can leave a new native file next to the old sidecar. The hash check then refuses that file, so its world can't be loaded from that file again.

`CoopSaves.WorldSaves()` lists any file that has a sidecar, without checking that the pair matches, and Continue takes the newest. Today a world's only autosave can be lost this way; the named save (if any) still loads. Fixes:

- write the autosave to two alternating files (or keep the previous pair as a backup);
- have Continue and the Load list check the pair and fall back to the newest one that reads.

How visible backups should be is question 4.

### G5. Save under a new name splits a world

The Save screen with a typed name writes `<name>.binary` plus its pair: a new world entry in the co-op list. The session's autosaves keep going to the original world's `--auto` file. This matches solo, where autosaves always go to the one Free Build autosave, but in co-op it gives two entries that each hold part of the session (question 3).

### G6. Rejoin after a dropped connection is manual

An unexpected disconnect puts the guest on the main menu with the reason; the guest must open Join again (question 5). The rejoin itself already works. The guest's player identity is stable per install (`Identity.PlayerId` in the BepInEx config). It spawns at the player position in the host's snapshot, which is the host's own position (question 6).

### G7. The guest's joined copy has no use yet

The guest keeps the host's latest save as a paired copy in `Saves\Coop\Joined` (phase 2 decision). Nothing reads it: it isn't in the guest's co-op list and the guest can't host it (question 7). Hosting it would be a separate world, not host migration (outside the roadmap).

### G8. Older formats

Pairs from 0.15 and earlier are refused with "older saves need migration". The owner's own worlds are 0.16/0.17 and still load (question 8). A save from a newer version is also refused; that is correct.

### G9. An out-of-sync host can't save on Exit

`SaveBlocker` refuses while the host's game is out of sync, so its Exit leaves without a final save. Up to one autosave interval of work can be lost. The host can run `/sync` first, so this needs no decision; the Exit notice should say why it didn't save.

### G10. Deferred owner tests overlap phase 8's exit

The owner's full tests for 4f, 5d, 10b and 7e are still open. Phase 8's exit asks for save, rejoin and resume evidence across the same features (question 9).

## Recovery paths to cover

Existing model tests already cover a disconnect during a snapshot transfer, held changes, duplicates and stale generations (phase 2), and abandoned claims and departed carriers (phase 3). Add, as model tests where the network layer can express them and owned runs where only the game can:

1. A pair write that never finished (native newer than the sidecar): Continue skips it, Load shows it as unreadable, and the world still loads from the previous pair.
2. Save while a guest edit, latch, cable or water change is in flight: the save waits, then contains the settled result once.
3. A guest leaving during an install, during its cable step and during a water reload: the host settles once; a rejoin shows the settled PC.
4. A guest leaving while a PC boots, sits in the BIOS or runs OCCT: nothing is held on the host; a rejoin shows the host's state (booted/desktop; the test idle on the guest).
5. Host Exit, then a fresh-process Continue with a powered PC, a changed BIOS setting, a GPU Tuner overclock and custom fan curves: the power, BIOS and tuner values come back; the curves don't (solo behaviour).
6. A guest that joins during a software transfer or a snapshot already in progress.
7. A state near the size limit after G1: save, join and `/sync` all work.
8. Pause-menu Load, Reset and Save on both games, as questions 1–3 decide.

## Proposed tasks (final order after the owner's answers)

- **8a** Focused audit (this report).
- **8b** Save integrity: compressed world state and a higher limit (G1); two alternating autosave files or a backup; Continue/Load check pairs and fall back (G4); Exit explains a refused save (G9). Version bump.
- **8c** In-world save, load and reset in co-op (G2, G3, G5), as questions 1–3 decide.
- **8d** Formats: clear messages for older and newer pairs; migration only if question 8 asks for it.
- **8e** Rejoin comfort (G6, G7), as questions 5–7 decide.
- **8f** Recovery tests: the list above, as model tests plus short owned runs.
- **8g** Two-game acceptance and phase close (question 9).

## Acceptance cases (two games)

1. New world: build, cable and power a PC, change the BIOS and GPU Tuner, install software. Then host Exit, fresh-process Continue and guest join. Everything matches on both games except open apps, running tests and fan curves, which follow solo behaviour.
2. The guest leaves and rejoins mid-session: no duplicate PCs or parts, and the same object hash on both games.
3. Save while each kind of change is in flight (edit, latch, cable, water, BIOS, software): the save waits and the reloaded world holds each result once.
4. Close the host's game during an interval save: the next Continue loads the previous good save, with a notice.
5. A world with more than 35 built PCs: save, join, `/sync`, Continue.
6. Pause-menu Load, Reset and Save on host and guest behave as decided.
7. Solo saves are byte-identical before and after.

## Questions for the owner

1. **Host's pause-menu Load Game and Free Build Reset during a session.** (a) *Recommended:* refuse with a notice ("Load or reset from the co-op menu"). (b) Allow it: like `/sync`, the guest automatically reloads into the new world.
2. **Guest's pause-menu Save, Load and Reset.** (a) *Recommended:* refuse with "Only the host saves the co-op world". (b) The guest's Save asks the host to save now; Load and Reset are refused.
3. **Host's Save screen with a new name.** (a) *Recommended:* keep it as a saved copy, as in solo; autosaves stay with the world being played. (b) The new name becomes the world being played, and autosaves follow it.
4. **Backups when the newest save can't be read.** (a) *Recommended:* keep the previous autosave as a hidden backup; Continue and Load use it automatically, with a notice. (b) Show the backup as its own entry in Load Game. (c) No backup.
5. **A guest whose connection drops.** (a) *Recommended:* a Rejoin button on the reason message. (b) Retry automatically for about 30 seconds, then the menu. (c) As now: the menu, rejoin by hand.
6. **Where a rejoining guest appears.** (a) *Recommended:* where they were when they left (the host remembers it per player while the world is loaded). (b) As now: at the host's position.
7. **The guest's copy of the host's world** (`Saves\Coop\Joined`). (a) *Recommended:* keep it as a hidden backup only, as now. (b) Show it in the guest's co-op Load Game so they can host it as their own world. (c) Stop keeping it.
8. **Co-op saves from 0.15 and earlier** (yours are 0.16/0.17 and still load). (a) *Recommended:* keep refusing them with a clear message. (b) Build a migration.
9. **Testing.** (a) *Recommended:* one combined two-game session at the end of phase 8 that also covers the deferred full tests 4f, 5d, 10b and 7e. (b) Keep those separate.

## Owner decisions (November 1, 2026)

1. **Host's pause-menu Load Game and Free Build Reset:** allowed. Like `/sync`, the guest reloads into the new world automatically (option b).
2. **Guest's pause-menu Save, Load and Reset:** refused with "Only the host saves and loads the co-op world."
3. **Host's Save screen with a new name:** a saved copy, as in solo; autosaves stay with the world being played.
4. **Backups:** the previous save is kept as a hidden backup; Continue and Load use it automatically, with a notice.
5. **Dropped guest:** a Rejoin button on the reason message.
6. **Rejoining guest's position:** where they were when they left.
7. **Guest's joined copy:** kept as a hidden backup only, as before.
8. **Co-op saves from 0.15 and earlier:** keep refusing them with a clear message.
9. **Testing:** one combined two-game session at the end of phase 8 that also covers the deferred tests 4f, 5d, 10b and 7e.

## Implementation (0.21)

- **Compressed world state (G1).** `Protocol.PackState`/`UnpackState` (Deflate). The state stays uncompressed in memory with a 16 MB limit (`MaxModStateBytes`); on the wire (snapshot mark) and on disk (sidecar layout `PC2PAIR2`) it is compressed, with a 1 MB limit (`MaxPackedStateBytes`). Expansion stops at the limit. Measured on the default workshop: about 7× smaller, so roughly 250 built PCs fit instead of 35. 0.21 still reads the uncompressed 0.16–0.20 layout (`SameFormat` adds 0.20).
- **Clear version messages (G8, decision 8).** `CoopSavePair.VersionProblem`: an older save says which version made it and which versions load; a newer one says to update the mod. The version is read before the layout, so a future layout still gets the version message.
- **Hidden backups (G4, decision 4).** `CoopSaveBackups` (network layer):
  - Before every host save into `Saves\Coop` (interval, Exit, Save screen, `/sync`, world switch), the pair about to be overwritten is copied to `Saves\Coop\Backup` if it still reads. An unreadable pair never replaces a good backup.
  - On the main menu, and before Continue, Load or a pause-menu Load, a save that doesn't read gets its backup back. The unreadable files are kept beside the backup as `.unreadable`. The menu says which world was restored.
  - Continue picks the newest save that reads. Deleting a world deletes its backup.
- **Exit without a save (G9).** The main menu says why the world wasn't saved, and suggests `/sync` first when the game was out of sync.
- **Pause menu (G2, G3, decisions 1–3)** (`CoopSaveMenus.cs`):
  - *Host Load* (`SaveLoadMenu.OnLoadSelected`) and *Free Build Reset* (`LevelLoadPersistency.ResetFreebuild`) start a world switch on the `/sync` path (`CoopController.StartWorldSwitch`). The current world is saved first if anything changed; a refused save is reported and the switch continues. The guest is told to leave and waits on its menu. The host loads the chosen paired save, or the default build for Reset, under the same world name. It starts a new generation, and the guest is admitted from a fresh snapshot. The chosen save must read with its pair, or it is refused with the reason.
  - *Guest* `NewSaveGame`, `OverwriteSave`, Load and Reset are refused with a notice. Every guest `CreateSaveGame` is skipped (logged).
  - *Save under a new name* is unchanged.
- **Rejoin (G6, decisions 5–6)** (`CoopRejoin.cs`):
  - *Rejoin button.* A guest that leaves the host's world for any reason except the host ending the session gets Rejoin on the reason message. It joins the same address and port, or the same Steam lobby.
  - *Position.* While a world is loaded, the host remembers where each guest player stood (from its position updates). When that player is in the world again (a rejoin or a `/sync`), the host sends it a `Spawn` message (message 14), and the guest moves its player there. A world switch forgets the positions; nothing is saved.
- **Model tests (8f), `PersistenceTests`:**
  - compression round trip, limits and a bounded expansion;
  - the new and old layouts, plus older, newer and unreadable versions;
  - backups: keep, an unreadable save not replacing the backup, restore with the unreadable copy kept, a lost `.coop`, no backup, delete, no temporary files left;
  - the spawn message (host only, valid positions, only after admission).
  - The transport test's large mark now uses incompressible data close to the packed limit.

## Game runs (November 2, 2026)

The owner asked for full testing without them at the computer. Owned runs p8a–p8m used two game instances on one PC over LAN, disposable saves, ordinary mouse and keyboard input, and the phase 6 probe (new commands: `summary-report`, `pose`, `save`, `world-load:<name>`, `world-reset`, `saves-report`, `dialog-report`, `exit`, `chat:<text>`; a probe host can now relaunch with `-Resume` and press Continue). A dropped connection was made by suspending one game's process for 12 s. The owner's saves were unchanged after every run, and there were no display driver events.

| Case | Result |
|---|---|
| Rejoin after a dropped connection (LAN) | Pass. The reason message has Rejoin; one click rejoins and the guest is moved back where it stood (position and facing exact). Worlds match. |
| Guest position after `/sync` and after a manual rejoin | Pass. Facing is kept (the game's camera doesn't undo the turn). A world switch forgets positions, as designed. |
| Guest leaves on purpose, host Exit | Pass. No message after a voluntary leave; "The host ended the session." without Rejoin. |
| Guest Save (new and overwrite), Load, Reset | Pass. Each is refused with its notice; the Save and Load screens stay open and the pause menu keeps working. |
| Host Save screen: new name | Pass. A paired copy ("copya") is written and the guest gets its copy. |
| Host pause-menu Load | Pass. The current world is saved first (with its backup), both games reload into the chosen save at the next generation, and the object hash equals the hash when that save was made. |
| Host Free Build Reset | Pass. Both games reload the default build under the same world. |
| Backup restore (newest pair mismatched by hand) | Pass. Continue puts the backup back byte for byte, keeps the bad pair as `.unreadable`, loads it, and the host sees "Loaded the previous save of ..." plus a chat line. |
| Host game closed during a real save | Pass. The game writes a temporary `.binary` before replacing the zip, so the last complete pair stays intact; on the next start the game moves the partial file to `<name>BACKUP.zip` (ignored by co-op) and Continue loads the previous save. A guest that lost the host gets Rejoin and rejoins the relaunched host. |
| Exit, fresh-process Continue with a powered PC and a GPU Tuner overclock, then a guest join | Pass. Power, desktop and tuner values (1750/1219/100%) come back on both games; object hash equals the one before Exit. |
| Edits then `/sync`, rejoin and Continue: side panel off, power off, GPU out, panel refit, panel off again, GPU in | Pass after the fixes below. Both games end with the same object hash after each step. |
| Desktop input replay | Pass after the fix below (clicks on the desktop replay on the guest). |

Bugs found and fixed in these runs (eight):

1. **Two hooks on one native function.** `SaveLoadMenu.NewSaveGame` and `OverwriteSave` are the same native code (same address); the startup check reported it. Only `NewSaveGame` is hooked now; it covers both buttons.
2. **Overwriting a co-op world from the Save screen wrote a new file named after its display name** ("Co-op World 1.binary"), splitting the world. `CoopSaves.RedirectShownName` sends it to the world's own file.
3. **A world became unloadable after a side panel came off.** A removed panel waits in the Free Build inventory (`Case_SKU/Panel`), but the game's save doesn't keep that entry, so after any reload the inventory hash differed: `/sync`, Continue and a guest join all failed. The inventory binding now leaves those entries out (`v2:` prefix); a 0.20 save is still checked the old way.
4. **A world became unloadable after a GPU came out.** `Slot.DynamicMustBeOpenPins`/`DynamicClosePins` are live caches (an install fills them, a removal leaves them, a fresh load doesn't rebuild them) and were recorded as shared structure. They are left out now, like `m_dynamicBlockedBy`; a slot whose saved links include them still validates (subset check).
5. **A panel refit after another edit went out of sync.** A capture while the panel was off dropped its slot's links to the panel's screws. A capture now keeps a removed panel slot's links to the resources it carries.
6. **Desktop replay error.** A replayed pointer over nothing wrote a null `RaycastResult` (a wrapped object in interop); an empty result is used instead.
7. **Guest-side PC reload after the guest's own install or panel gesture** (owner decision, November 2, 2026). The game's real gesture on the guest adds blockers to a neighbouring slot (`Slot.blockedBy`, here the lower GPU slot) that the host's replay and a fresh load don't, so the guest's check failed and the PC reloaded with "PC reloaded to avoid a desync" (leaving work mode). Slot blocker lists are no longer compared on the other game (they only size edit claims; the host's game checks each edit natively). Run p8m repeated the p8l sequence (panel off, GPU out, refit, panel off again, GPU in, all by the guest) with no reload, then `/sync`, Exit, Continue and a guest join with the same object hash throughout.
8. The restore notice is shown in the world after Continue/Load (not only on the main menu), and the dropped-connection message is readable ("The connection was lost: ..."), not `PeerNotFound`.

Found and left for the owner:

- A game that loads a world before any real keyboard or mouse input (the test harness's relaunched host) keeps a PC that was on stuck on its boot screen: `JobTutorialHintUi` looks up Rewired's last active controller and throws. Players always click first, so only test setups need one real click before Continue.
- The pause menu's save card shows "coopworld" (the snapshot file name) until the host's next save.

### Not checked in game yet

- The Rejoin button over Steam (needs two Steam accounts).
- A world with more than 35 built PCs (acceptance case 5); covered by model tests only.
- The Exit message when the world couldn't be saved (needs an out-of-sync or busy host at Exit).
- A dropped connection during an install's animation (the faster-install power-up finished the GPU removal before the drop); during a BIOS session or a running OCCT test.
- The owner's own pass of the deferred tests 4f, 5d, 10b and 7e (cables, water cooling, full builds in both directions).

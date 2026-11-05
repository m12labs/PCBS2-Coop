# Acceptance runs: November 4, 2026

This is an ongoing acceptance record, not a phase-completion claim. Local dates in this record use the owner's America/Chicago timezone; raw probe events use UTC. Steam is complete by the owner's November 4 confirmation and was not repeated.

## Baseline and evidence

- Started from `fb1287d`, version **0.24**, with an existing owner edit to `docs/needs_testing.md`. That edit is preserved separately from this work's commits.
- Release build against the installed game's real interop: passed, zero warnings/errors, using `--no-restore -m:1 -p:UseSharedCompilation=false`. The shared compiler failed without a C# diagnostic in this environment. A sandboxed deployment was denied; the authorized deployment outside the sandbox succeeded.
- Network/model suite: **706/706** passed with local UDP access. A sandboxed run failed the UDP handshake; it is not counted as a product failure.
- All game runs use the launch scripts' disposable `BepInEx/PCBS2Coop-diag/phase6/<run>/saves-<role>` folders. Launch records, deployed hashes, before/after owner-save hashes and copied logs are in `.local/phase6-<run>/` (ignored). Each launch verifies the three deployed managed dependency hashes against the Release output.
- Earlier results are retained as earlier results: phase 4's owner-confirmed component checklist/full build, phase 7's short owned simulation runs, phase 8's p8a–p8m, and phase 12's p12a–p12e. Those do not imply that every combined line in `needs_testing.md` passed.

## Run acceptance-a (0.24)

Two actual LAN game instances; ordinary desktop input for the tool interactions; probe commands only for positioning, reports and host Exit.

| Check | Observation | Extent |
| --- | --- | --- |
| Guest joins current host world | Guest admitted to generation 1 with both default PCs; both games showed connected status. | Join smoke check; other everyday gestures not exercised. |
| Host changes table to CPU delidding | Real `B` selector and CPU Delidding click; host showed the delidding tools. The later joining guest showed the same tools. | One switch and late-join path; all types/both actors remain to test. |
| Host uses delidding table; guest tries to use it | Host opened the real CPU inventory, selected an i3-6100, and entered the procedure. Guest click was refused with **"Someone is using the delidding table."** Both logs recorded the host's use. | Prompt passed. Name-label visibility, a complete delid, variant installation and persistence remain open. |
| Guest tries switching occupied table | Guest opened the real `B` selector and clicked Water Cooling. Same refusal prompt; selector stayed open, table stayed delidding. | Passed the busy-switch checklist case in this direction. |
| Cancel host delid before heating | Escape returned to walking; host log recorded leaving the table without an inventory change. | Early cancellation only; not evidence for cancellation halfway through the complete procedure. |
| Host Exit | Save-on-Exit wrote and paired the autosave, sent a copy, ended the session and returned both games to the menu. | Normal Exit only; unsavable Exit remains open. |
| Owner saves and cleanup | Stop script closed both recorded processes and reported **21 owner save files, byte-identical**. | Passed for this run. |

### Test harness defect found and fixed

The first attempt to launch the guest rejected the launcher's own host as an unowned instance. PowerShell 7.6 deserialized the JSON `startedUtc` field as `System.DateTime`; comparing it with an ISO string failed despite exactly matching process times. Commit `84206dd` normalizes both timestamps to UTC ticks and retains PID, executable path and process-name checks in start/stop/send/reload scripts.

Regression tests passed on PowerShell **7.6.5** and Windows PowerShell **5.1.26100.9818**, covering JSON round trips and rejection of wrong PID/path/start time (including one tick) and absent processes. The original game-launch failure was repeated successfully after the fix; the corrected stop script also recognized and closed both owned games.

## Remaining sequence

1. Remaining in-flight save and unsavable-Exit cases. The >35-PC case is complete below; fixture setup is distinct from build-gesture acceptance.
2. Complete table switching, specialist tools and their inventory variants, paste/Q-tip and refusal/cancel paths with native observation reports.
3. Building, cabling, power and custom-water gestures, concurrency, late join and persistence.
4. Lighting/Cable app, room decoration, real wallpaper browser and real cupboard-file import, with both actors and persistence as specified.
5. Screen/simulation regression, remaining save-interruption cases, final build/network suite, deployed hashes and owner-save verification.

Unavailable content already recorded by the audits: the LED-strip tool cannot open in this game build; no DLC is installed; Free Build has no seasonal-decoration switches. These are specific unavailable cases, not passes or reasons to skip other tests.

## Run acceptance-b: fixture setup

The stress fixture copies the default workshop's built PC 26 (18 logical parts, native record 1,165,140 bytes), using native serialization to make independent graphs. Each copy receives fresh IDs through accepted `Import` and goes into the actual cupboard through accepted `Store`. This is test setup for storage/persistence capacity; it does not pass component-install or real cupboard-file-import gesture cases.

The first fixture attempt exposed a harness wait error: `WorldReady` temporarily becomes false while the accepted carried PC loads. The import succeeded as PC 393; the fixture wrongly stopped. The wait now permits that transient state under its deadline, while rejecting a changed session/generation or lost save isolation. The host saved and exited; after a fresh-process Continue PC 393 was in the cupboard (the previous carrier's identity was absent), with clean native projection checks. All 21 owner-save hashes remained unchanged.

The corrected fixture made 36 further independent copies: **39 built PCs**, with **1,122,720 bytes** of object state, exceeding the old 1 MiB state limit. The host's native projection checks passed; the save pair was readable (44,709,486-byte native zip, 92,705-byte sidecar).

### Actual late-join failure (0.24)

The guest received and loaded the 39-PC snapshot, but the host had already sent two more admission snapshots at generation 1, revision 2883. When a later snapshot completed, the guest was already in the workshop and disconnected with **"The host's world arrived while this game wasn't on the main menu."** This fails the large-world acceptance case. Failure logs are retained in `.local/acceptance-20261104/stress-join-failure-{host,guest}.log`; the original event stream is retained under this run's diagnostic directory.

Cause: `GuestNeedsWorld` only checked the sender's local load queue. When all chunks had been submitted to the transport, that queue was empty before the guest received the last bytes and acknowledged Loading. The host then captured another admission snapshot. Commit `de2bee3` adds a pending-admission guard until receipt is acknowledged, reset on abort/session/generation changes. Loading acknowledges the received snapshot's generation, before the native load restores the guest's world. This is shared admission logic, not a Steam-specific change. Version **0.25** still reads **0.24** saves; no save-layout change.

The new delayed-receipt regression was run against the old session implementation: **two expected failures, 711 passes**. The completed fix passes **715/715** checks, including holding the guest in Loading before admission, without malformed acknowledgements. Release builds passed with zero warnings/errors.

### Fixed-build actual two-game result (0.25)

| Step | Observation |
| --- | --- |
| Fresh-process Continue of the 0.24 fixture | Host bound all 39 PCs, preserving IDs and allocator. |
| Late guest join | One admission snapshot, checksum verified; guest bound 39 PCs and remained connected. Both native PC/peripheral checks passed. |
| Guest panel edit | Real bench interaction and generated native removal callback removed PC 26's left panel through the production host order. Both games showed the result with clean checks. The first callback, queued in the same frame as entering work, was correctly refused before work activity arrived. |
| Host save and guest copy | Readable paired save; the guest received and retained the complete 44.8 MB bundled copy. |
| `/sync` | Both games reloaded into generation 2; one admission snapshot. 39 PCs, unchanged object hash, clean native PC/peripheral checks. |
| Host Exit, both processes closed, fresh-process Continue, guest joins | All 39 PCs and the panel edit persisted, with the same object hash and clean native checks on both games. |
| Cleanup | Both owned games closed. All 21 owner saves were byte-identical after every stop/restart. The completed host session logged zero invalid/unknown/malformed messages. |

The common post-edit object SHA-256 is `6930B67BD59D0EC8D20193503B68A31A93DBC1E4CC03A5AF051BDDCF9443C684`, including after sync and final Continue. Raw evidence: `.local/acceptance-20261104/stress-fixed-{sync,continue}-{host,guest}.log`, `stress-deployed-hashes.json`, the run's event streams and process/save-hash manifests. **The >35-built-PC acceptance case passed.** This is not completion of the other phase 8 checklist cases.

### Resume harness follow-up

Fresh processes originally replayed the run's old `commands.txt`, including Exit during main-menu setup. That attempt is a harness failure, not Continue evidence. Resume now skips the existing append-only command history and emits `commands-resumed`; the corrected rerun skipped 22 old host commands and executed none before new operator input. Phase 6 also permits the owned guest to relaunch into its own disposable folder and request a fresh host snapshot. The fixed Continue results above were obtained after this correction.

### Component probe coverage boundary

`gesture-*` commands run real game entry points, production requests and native editor/replay/settle paths. Reports observe native slots, parts, cables and paste. They explicitly bypass pointer reach/raycast/hold input; `gesture-install` also bypasses inventory selection/placeholder commit. These are actual-game checks at the recorded seam, distinct from manual UI coverage. A returned entry-point call alone is not a pass: acceptance requires host confirmation, the native result on both peers and clean projection checks.

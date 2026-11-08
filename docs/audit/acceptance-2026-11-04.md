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

## Run acceptance-c, walking power and powered pickup

On November 5, 2026, two guest bench `P` presses in version 0.25 reproduced a guest-only boot on default PC 213. The guest reached its desktop while the host PC remained off and no Power request was ordered. The three native walking bench callbacks called the motherboard directly, bypassing the case and HUD hooks. Red logs are retained as `.local/acceptance-20261104/walking-power-red-{host,guest}.log` and `walking-power-red-repeat-{host,guest}.log`.

Commit `db13358` adds the bench hooks in 0.26. Power can claim a bench PC while its actor is walking, and the claim still blocks movement and edits. Other edit gestures still require work mode. The new regression exposed two failures on 0.25. Release built with zero warnings or errors and all **719/719** automated checks passed in 0.26. All three deployed DLL hashes matched before launch. The 0.25 paired save loaded in 0.26.

| Actual two-game check | Result |
| --- | --- |
| Walking bench `P` | The guest's off (`G#2`) and on (`G#4`) requests settled. The native PCs were off after the first and reached their desktops after the second. The host aimed at an empty bench so the same keyboard input could not trigger a competing interaction. |
| Pick up a powered PC | At 09:48:43 UTC the guest used the real right-click pickup. The two virtual computers powered down and both peers recorded PC 213 in the guest's hands. Native checks were clean at revision 206 with common object hash `5C66F09D08FDB201C589B2C05A9BDE1845736D26E79B71EE8A0596ACA552A5E6`. The guest then dropped it back on bench 3 while it was off. |
| Case button | The guest called the native `PowerButton.Press` entry point in work mode (pointer selection bypassed). `G#9` booted both peers and `G#11` powered both off. |
| Work HUD button | Real clicks on the guest's Power button (`G#13`, `G#15`) booted both PCs to the desktop and powered them both off. The power commits matched the automatic monitor, keyboard, mouse and mains cable changes. |

The powered world was then saved as a readable pair. A fresh guest joined at generation 1, revision 275. The host stayed on the desktop, while the guest's native startup failed in `JobTutorialHintUi.SetSpriteAssetForText` and the automatic restart hit the same exception. At revision 296 both worlds still had a clean object check and the same object hash, while the guest's actual screen remained at Boot. This fails the 0.26 powered-join case. Logs: `.local/acceptance-20261104/cold-boot-red-{host,guest}.log`.

### Cold Continue and late join (0.27)

After the host's Exit paired the powered 0.26 save, a fresh host Continue hit the same boot exception. The native BIOS hint's atlas and text formatters both dereference Rewired's last active controller, which can be absent before physical input. An initial controller-fallback candidate did not resolve the host failure. The final 0.27 fix recovers from a native null reference in those two formatters within a co-op boot step, allowing the game to omit the prompt and continue native startup. The BIOS key path still runs, and 0.26 saves still load.

With the final build, a fresh host Continue loaded the 0.26 pair and reached the desktop at 09:59:58 UTC. OCCT was opened with the game's native `OS.Launch`. A separately launched guest joined the running host. Both PCs reported `powered=true`, `running=true`, `surface=Desktop`, and both native screen trees had an active OCCT window. At revision 36 both peers had clean projection checks and common object hash `9BCA60CEDC3D96BF474D39AD0A66B9FB33657BCB7B1CCBC64113A3C54DAC9BE3`, identical to the powered 0.26 save. Neither process had received a physical input event before boot. Neither log contained a startup exception or boot retry. Release built with zero warnings or errors, and the three installed DLLs matched their built hashes before each launch. Logs: `.local/acceptance-20261104/cold-boot-fixed-{host,guest}.log`.

### Cable edits, claim refusal and unsavable Exit (0.27)

- The guest invoked the native long-press callbacks on PC 213. The 24-pin cable (`115>007`) came off with the four desk leads (`G#2`), taking both PCs offline. The game automatically reconnected `115>007` (`G#4`). Two GPU 8-pin leads were removed (`G#6`, `G#8`) and reconnected by the game (`G#14`, `G#16`), using available PSU ports. The case-fan cable (`CASEFAN_MB1>MB_FAN_BOTTOM`) came off and went back on (`G#18`, `G#20`). Both games reported the same native wires. These callbacks bypass pointer reach and hold. CPU 8-pin, SATA and front panel/USB remain open.
- The guest opened the native HDMI chooser (`G#22`), with source `HDMI1` and target `HDMI`. The host's case-button action on PC 213 was refused with “The other player is changing a part of that PC.” The host powered PC 26 through the native button (`H#69`) and both games reached its desktop while the HDMI edit was held. A real guest Escape canceled `G#22`, released its claim and left PC 213's wire endpoints and cable SKUs identical to before the press. At revision 136 both native checks passed and the common object hash was `D0DBC844FB3DEE8468A4DD2C14DE454793707EB31907826D7319CF7C02861775`.
- With another HDMI choice held open (`G#24`), a host save request returned false with “a change is still being shown.” The actual `ExitPanel.Exit` entry point waited, then the main menu showed “The world wasn't saved when you left (a change is still being shown). It keeps its last save.” The preceding native zip and `.coop` file had identical SHA-256 hashes before and after Exit. This covers the cable save guard and unsavable-Exit notice. Other in-flight save cases remain open.

Logs: `.local/acceptance-20261104/cable-exit-{host,guest}.log`, plus the run's native events. The session stopped with zero invalid, unknown or malformed messages.

### GPU Tuner (0.27)

Both players entered PC 26's monitor through the actual game interaction. The host used the real core `+` and Apply controls (`H#130`), and the guest used Reset to Default (`G#9`). The guest then used `+` and Apply (`G#16`) and the host reset (`H#139`). Both open apps displayed 1726 MHz after each Apply and 1725 MHz after each Reset. The final native BIOS values were `1725/1219/100%` on both peers. All four actions were host-ordered live BIOS changes and both PCs stayed on the desktop. Preliminary direct field assignments skipped the slider's callback, so the actual UI sequence is the acceptance evidence.

### Fan Control and custom picture follow-up (0.27)

- PC 26's real Fan Control app was open on both games. The host applied Performance (`H#152`) and the guest applied Eco (`G#28`). The guest used Custom, Max RPM and Apply (`G#44`) to change all three GPU fans to the 11-point curve. Both native reports showed custom preset 3 and 11 points for each fan. A freshly launched guest joined the still-running host, received all six curves (`H#175`), and displayed the flat 2000 RPM portion of the curve and 2000 RPM for all three GPU fans.
- The guest then used Defaults and Apply (`G#117`). The host's open app showed system control again. The host applied its own Max RPM curve (`H#201`) and used Defaults and Apply (`H#210`). Final native reports agreed on system mode and the Balanced preset for all six fans.
- The host's native custom wallpaper apply produced picture `DC2CB81FE384` at 960×540. The late guest requested that picture from the host and loaded its bytes. Both peers reported the same shared hash and native screen texture dimensions. The guest then applied a separate 3840×2400 picture through the same native entry point. Both peers showed `A37491A5E479` and the matching screen texture. These calls bypass the browser picker. Continue remains to be checked.

### Real wallpaper picker (0.28)

- In 0.27 the wallpaper gallery skipped its Windows tile. The registered file did not exist, and the OS's own copy was readable. The existing fallback was used to share a selected Windows picture but the gallery could omit it before selection. Commit `f69429f` uses that same Windows file to create the missing preview for a shared PC's gallery. The existing native preview and the game's preview cleanup are retained. The 0.27 save format remains readable.
- A cold host Continue in 0.28 showed the 3840×2400 custom picture `A37491A5E479` from the 0.27 save. A fresh guest loaded the corresponding native software record. The Windows tile was present in both games, and a host click applied the 1920×1080 image `E09036FEA1A4` on both. Both games sampled that physical click on this machine. A separate guest click with the host parked at an empty bench confirmed guest-only publication of the same image.
- The host used the actual Custom tab and browser to select a 9.45 MB PNG. The game displayed “That wallpaper can't be used in co-op: it's larger than 8 MB.” and restored the previous Windows picture. Then the host picked the 960×540 test PNG and the guest picked a 3840×2400 JPEG through their real browsers. Both native screen textures matched the chosen image and content hash after each Apply (`DC2CB81FE384`, then `A37491A5E479`). The observer was away from the monitor for each browser apply.
- Host Exit completed the paired save. With the disposable JPEG renamed, a fresh host Continue and fresh guest join displayed `A37491A5E479` at 3840×2400. Both native projection checks were clean at revision 38. Logs from the picker run are in `.local/acceptance-20261104/wallpaper-picker-{host,guest}.log`.
- Release built with zero warnings or errors. All **719/719** automated checks passed. The installed DLLs matched their Release hashes before each start, and the stop script verified all 21 owner saves were byte-identical.

### Overlapping cable edits on separate PCs (0.28 → 0.29)

In 0.28, the host unplugged the CPU 8-pin on PC 213 while the guest unplugged the SATA data lead on PC 26. `H#43` settled on the guest before its cable replay completed. Assembly immediately compared the old wiring to the new host profile, reported “unconfirmed orientation, case membership or internal cable connections,” and queued a PC repair. The native replay subsequently finished. Reconnecting the two leads together reproduced the same premature repair (`H#116`). The repair restored agreement but replaced a PC that had been projecting correctly.

Commit `6f05b8c` makes assembly binding wait for local cable motion and queued or running wire replay. The 0.29 repeat loaded the same 0.28 world and overlapped both removals (`H#21`, `G#2`) and both reconnections (`H#26`, `G#4`). In each direction the guest received the host's settle before it finished showing the edit. At revision 28 both native CPU 8-pin and SATA data connections were absent. At revision 34 both games had `127>012` and `136>0332` and object hash `1E909F744FA003DAABF371F1BA11249386224D0DFD55B0A18635D6C0C5E9F2A1`. Neither peer requested repair or reported a projection fault. The host also removed and restored the front-panel lead (`501>114`) while the guest removed and restored SATA power (`122>0262`, then `0005>0262`). Both native cable lists and the object hash matched at revision 46.

Release built with zero warnings or errors and **719/719** checks passed. Raw logs and the two-game verification output are in `.local/acceptance-20261104/concurrent-wire-*`. The stop script encountered a reused historical PID belonging to a console process and stopped scanning. Commit `95e12e6` skips that record, stops the remaining owned instance and reports how many it actually stopped. The reused process kept its start time and all 21 owner saves matched.

### Custom water cooling in 0.29

- The guest drained PC 26's complete native loop (`G#10`). The host's save request during that change waited with “a change is still being shown.” The guest filled the powered-off loop with `WC_COOLANT_PURPLE_CLEAR` (`G#88`), then drained it again (`G#166`). Both native loop reports showed the same coolant and fill state after each action.
- The guest removed the pipe from the pump to the CPU block (`G#244`). Both games showed the same two surviving pipes, disconnected fittings and incomplete, empty loop. The host later saved the world and the guest verified the saved copy's checksum. The probe called native entry points and bypassed pointer reach and hold.
- While the guest held the native pipe chooser on PC 26 (`G#322`), the host received “The other player is building water cooling on that PC.” The host unplugged PC 213's CPU 8-pin (`H#113`) and both peers settled that edit while the water claim stayed open.
- After real Escape closed the chooser, neither PC had a new pipe. The guest still sent a 76-chunk record and the host unnecessarily reloaded PC 26. The cancellation case failed. Red logs are in `.local/acceptance-20261104/water-escape-red-{host,guest}.log`.

### USB replay, pipe cancellation and blue-screen recovery in 0.30

- In 0.29 the host connected the desk keyboard to the case USB port (`H#41`). The native tool offered `KEYBOARD` and saved `KEYBOARD_SRC>CASEUSB0`. The guest could not resolve the source and asked for PC repair. Commit `571e9a0` resolves the saved endpoint using the PC's own workstation connectors and their remote ends. In 0.30 the host unplugged (`H#5`) and reconnected (`H#8`) that lead. The guest finished both native replays without repair, with `Loose_Plastic_Black` on the same endpoints after reconnection. Both games passed projection at revision 93, with object hash `E899FB8607B294F9B2815DE5EB4AB388EE9203118B24898B7F71AE5437F03C3B`. The callbacks bypassed pointer reach and hold.
- Commit `1bd2b73` observes the native route Accept call. A pipe edit that leaves the tool without accepting a route cancels even when its serialized PC record changed. The guest's real Escape from fitting selection (`G#159`) released its claim with no finished record and the original two pipes remained. The guest selected Red Rigid Pipe in the real catalog, opened the route and used Undo and Escape (`G#161`). No record was sent and the guest could immediately start a new route. The drag and Undo geometry change is unverified.
- The accepted route still shared. The guest restored the missing pump-to-CPU connection with the default flexible pipe (`G#3`), later removed it (`G#81`), and clicked the native Accept button on a red rigid replacement (`G#163`). The host reloaded the finished PC and settled the same endpoints and node counts. The guest refilled the complete loop and the host showed the same coolant (`G#241`). The placement callbacks bypassed pointer reach and hold. The probe's saved-node coordinate read was unreliable, so corner geometry is still open.
- The host invoked the native simulation failure entry point on PC 213 (`GPU_HEAT`, unforced). The guest showed the host's matching reason. Both reported `bsod=true`. The host powered the PC off with its native case button (`H#282`). Both reported stopped and `bsod=false`. After the recovery boot, both reported `powered=true`, `running=true`, `bsod=false`, and `surface=Desktop`. This covers recovery after a native blue screen. Overclock/OCCT and naturally induced heat failures remain open.
- A cold 0.30 host Continue and a fresh guest loaded the 0.29 save. Each reported two pipes and the same incomplete, empty loop. After the rigid replacement and refill, the guest requested `/sync` and the host accepted it. At generation 2, revision 23, both games showed the replacement's four nodes, the complete full loop and `WC_COOLANT_PURPLE_CLEAR`. Projection checks passed and the object hash matched (`F60CA11C7811ADBB5C72EE49B6AB5EB2AC223691FAA2126621813F48F1944D36`).

Release built with zero warnings or errors, and **719/719** automated checks passed. Red USB logs are in `.local/acceptance-20261104/usb-source-red-{host,guest}.log`. The 0.30 runtime logs are in `.local/acceptance-20261104/usb-water-bsod-green-{host,guest}.log`.

### Powered coolant drain in 0.30

After `/sync`, the guest powered PC 26 through its case-button entry point (`G#320`). Both PCs reached the desktop with a complete, full loop. The guest clicked the visible reservoir in Remove mode and the native game displayed “Empty Pump Reservoir.” The guest shut down and committed the drain (`G#322`), then the host reloaded the PC with power off. Both native reports showed a complete, empty loop with no animation and both sims stopped. At generation 2, revision 298, native and projection checks passed and the object hash matched (`7E9A6EED6D98730F38267BD254B40DEA0FF165D80E7B1A6E29A74808A4A634D9`).

### Identical fans, powered part removal and contention in 0.30

- PC 213 was at the desktop on both games. The host removed its left panel (`H#548`), then removed the rear `Case_Fan_LianLi_4` (`H#549`). The guest played the native removal and both PCs powered down. Two identical top fans remained. In a fresh run from the saved fixture, the guest reinstalled the rear fan (`G#2`) and removed it again (`G#5`). Both native slot reports showed slot 265 empty and slots 267 and 269 still populated, with common object hash `1CDDC3DCF37E1AD09ADCA4926EAF52AD58C7083C22B71FD638D9BDB6403067DC` at revision 15. Both projection checks passed and the host saved a readable pair.
- While the guest's fan install held the claim, the host tried installing in the same slot and removing a different fan on the same PC. Both attempts got “The other player is changing a part of that PC.” The host save waited. After the guest's native accept flag, both games completed the install and reported all three fans at revision 10. The install probe bypassed inventory selection, placeholder commit and the Accept click. Real Escape after reaching orientation did not end the install, so the halfway-install cancellation case remains open.
- In a separate attempt (`G#402`), the guest's edit reached the 120-second timeout. The host's replay was aborted with transient assembly state, then its next save refused 158 native resources against 154 shared. The earlier good pair loaded in both fresh processes. Logs are retained as `.local/acceptance-20261104/fan-orientation-timeout-{host,guest}.log` and `.local/acceptance-20261104/fan-contention-{host,guest}.log`.

### Long fan orientation and blocked CPU removal in 0.31

The existing orientation hook now keeps the editor and the detached replay active while the native orientation step waits for a choice. The original timeouts still apply when an edit stops making progress outside that step. The 0.31 retest started from the same saved empty fan slot. At 175 seconds the guest was still in `InstallingPartState`, the host refused a competing removal and the save still waited. The guest accepted the orientation and the host's detached replay finished in 177.77 seconds. Both native reports showed the installed fan, clean projection, revision 9 and common hash `D11A1576620B0898232CEE723945DA9DDCBEF23CDDC7AC1D10F3E5E8BA92E851`. The host then saved and the guest received the copy. Release built cleanly and all **719/719** network checks passed. The 0.30 pair loaded in 0.31.

The host then requested the native CPU removal with the cooler still mounted (`H#6`). The host refused “Something is in the way of that part.” Both games kept `CPU_Intel_19` in slot 360 and the same object hash. The probe bypassed pointer reach and hold. Logs: `.local/acceptance-20261104/fan-orientation-031-{host,guest}.log`.

The host removed the rear fan, then started its own install (`H#12`). A fresh guest connected at revision 17 and waited for a snapshot while the native orientation was open. After the host accepted, it settled at revision 20 and sent the snapshot. Both reports showed the installed fan and common hash `B522DDC4EC65A655526780B1BE26F374E6C5B927FA99BCDF174A6E6C0B764154` with clean native checks. Both remote models spawned and each game logged the other player's plain chat message. The host saved the installed fan. Logs: `.local/acceptance-20261104/fan-late-join-031-{host,guest}.log`.

The two fresh native reports also read the retained `WC_PIPE_RIGID_RED` from the live `PipeNode` transforms on PC 26. The guest had accepted it in the 0.30 run. Both games showed the same `4>2` endpoints and the same four positions, with two internal bend fittings and three segments in different directions. The route with corners passed. Verification of a drag and Undo geometry reversal remains open.

### Interrupted native save in 0.31 and 0.32

- In 0.31, both games held the same 39-PC world. The owned host was stopped after the native raw save grew from 90,688 to 44,890,535 bytes. Both members of the previous pair stayed byte-identical. The game quarantined the partial write as `Co-op_World_1--autoBACKUP.zip`. Cold Continue loaded all 39 PCs and the surviving guest rejoined through its visible Rejoin button. The host never received a co-op recovery notice, so the combined checklist case failed.
- In 0.32, co-op moves an unpaired raw write into `Backup` when its matching zipped pair still reads. The ordinary co-op recovery path then reports the previous save. The automated regression failed in 0.31 and passed in 0.32. The Release build had zero warnings and errors, and all **722/722** checks passed.
- In the 0.32 repeat, the raw file grew from 90,688 to 28,851,019 bytes before the owned host was terminated. The game left a 44,890,535-byte raw file, and both members of the previous pair still matched their pre-write SHA-256 hashes. Cold Continue quarantined the raw file as `Backup/Co-op_World_1--auto.binary.unreadable`, loaded 39 PCs and displayed the previous-save notice. The explanatory message was also visible in the host's real chat panel.
- The surviving guest used its actual Rejoin button again. Both peers reported 39 PCs, clean native checks and the original object hash `6930B67BD59D0EC8D20193503B68A31A93DBC1E4CC03A5AF051BDDCF9443C684`. Each recovered pair member was hash-checked against its pre-write copy. The ignored interruption manifests and logs are under `.local/acceptance-20261104/interrupted-save-reference-*`.

### Assembly rebind fault and `/sync` in 0.32 and 0.33

Removing PC 213's front panel from the clean fixture reproduced an assembly profile failure. The native removal callback ran in Remove mode. The host's saved internal wiring lost `501>114`, while the guest's direct panel projection left the cable connected. The shared panel change carried only the panel state. Both games showed the panel off. The front-panel case remains open.

In 0.32, the failed rebind remained pending and `/sync` timed out waiting to save. Version 0.33 removes a reported rebind failure from the pending set and prevents it from reentering until repair. The next repeat reproduced the same cable mismatch, then `/sync` saved the host's native state and loaded both games at generation 2, revision 3. Both native reports showed the front panel and cable absent. The object hash matched (`838C59D3A972B6845F2A539664233E0CBD0B11DA4582AA6E5A8A8BA79C90438B`) and both projection checks were clean. The ignored logs use `front-panel-clean-red-*`, `panel-sync-stuck-*` and `panel-sync-fixed-*`.

### Host thermal paste and cleaning in 0.33

The host opened the Q-tip on PC 213, wiped the CPU with real mouse strokes until the game displayed “Residue successfully removed,” then closed the tool (`H#83`). At generation 2, revision 78, both the live CPU paste and native saved flag were off on both games. The guest received “The other player is working on the thermal paste of that PC” when it attempted native HUD power during the wipe. The power probe invoked the actual callback and bypassed its pointer input.

The host opened the Tools inventory, selected Compressed Air and saw “Cleaning isn't shared in co-op yet.” It then selected Thermal Paste and used the real syringe until the game displayed “Sufficient paste applied.” After the host closed the tool (`H#86`), both games reported the live and saved flag on at revision 81. The host autosave completed and the guest verified the copy. Guest tool results, the visible activity label and operation under load were still open at the end of this run.

### Guest thermal paste, player visibility and a dry-loop heat failure in 0.34

- A cold host Continue and a new guest loaded PC 213's applied paste from the 0.33 pair. The guest used the real Q-tip until the game reported success and exited the tool (`G#2`). At revision 12 both games reported the live and saved flag off. The guest then selected Thermal Paste from the real Tools inventory, applied it until the game confirmed sufficient coverage and closed the tool (`G#4`). At revision 25 both live and saved flags were on. Both object hashes matched (`32BC45F2E9692DD2C1F3EB01653EA59A682E1693D5625CF446D1CCA904F35B4D`) and both native checks passed. The host saved and the guest verified the copy.
- In 0.33 the local pose report placed the first-person controller at `y=-9.8` during paste work, where the host previously sent that pose to the guest. Native work mode remembers the standing position and moves its hidden controller down ten metres. In 0.34 the remote model stayed at `y=0.20` during both guest paste states. The host visibly read “Applying thermal paste” under “Client.” Both real workshop views showed the other player's avatar and name, and host and guest plain chat arrived on both games.
- PC 26 had a complete but empty custom water loop on both games. The host used its native case button (`H#4`), and the simulation failed at 184.6°C with `CPU_HEAT`. The guest logged its own matching predicted reason, waited for the host, then showed the same blue screen. After switching off, refilling with `WC_COOLANT_PURPLE_CLEAR` and booting again, both PCs reached the desktop at 45.8°C with no failure. This run used the native button and fill callbacks through the isolated probe. PC 26 already had paste and a fitted CPU block. The missing-cooler or missing-paste case still needs a separate run.

The ignored game logs use `paste-pose-034-*`. The pose check used the real camera and a Windows screenshot. The probe positioned the observer before the work gesture.

### Connected panels, camera overlap and refits in 0.34 and 0.35

- In the first 0.34 pose run, a standing remote avatar could fill the other player's work camera when the players' positions overlapped. Moving the other avatar away restored the work view. The final 0.34 build hides that model locally when it overlaps the active camera. The next run placed both players at the same bench reset position and entered the same PC. Both real work views showed the PC and HUD without the model blocking the camera.
- The clean front-panel fixture had the `501>114` lead and front panel on both games. In 0.34 each player's connected removal request got “Unplug the cables connected to that panel first.” The guest unplugged the lead (`G#7`), then removed the panel (`G#11`). Both native records had the panel and lead absent and both projections were clean at revision 9. The host selected the panel from Removed Components and refitted it (`H#7`). The host's native install automatically added `501>114` again, the guest's direct refit left it disconnected, and the host reported an assembly profile fault. The ignored red logs are `front-panel-refit-red-*`.
- Version 0.35 skips the native automatic cable connection only while an accepted shared panel install settles. The repeat from the same clean pair covered guest unplug/removal (`G#2`, `G#4`), host refit/removal (`H#6`, `H#7`), and guest refit (`G#5`). Both players selected their actual panel from Removed Components to create the native preview. The probe invoked the real long-press callback after selection. At revision 13 the panel was fitted, the lead was absent, and both native projections passed. Starting the regular cable tool from motherboard header `114` reconnected `501>114` (`G#8`). Both games agreed on the fitted panel and the original black lead at revision 18, with clean projections and object hash `C6C2985AC92EFFE973D6914625265E639B04DE074E462B327F14C490427674DC`.
- The 0.35 Release build had zero warnings and errors and passed **722/722** automated checks. The launchers checked the deployed DLL hashes and the repeat loaded the earlier save pair. Latches and RAM clips still need their follow-up test.

### Finished GPU and table contention in 0.35

The guest used the actual `B` menu to switch table 1 from Case Modding to Water Cooling (`G#11`). The host's table showed the same water-cooling equipment and both native table reports agreed. The guest selected the GTX 1070 Founders Edition from the game's inventory. When the host clicked the occupied table, its real HUD showed “Someone is using the water-cooling table.” Both reports named the guest as the holder, and the host's activity report showed “Water cooling.”

The guest removed the heatsink screws, removed and replaced all eight thermal pads, wiped the die until the game confirmed removal, applied sufficient paste, selected the compatible EKWB RGB block and tightened all six block screws. The last click put the card into the guest's inventory. It remained private until the guest closed the table's inventory. After closure at revision 835, each game had one `GpuPartInstance:GPU_NVIDIA_2|block EKWB_GPU_WC_BLOCK_2`, and neither game had a table claim. This used ordinary game controls throughout. Installation from either side and the other part types are separate cases.

### Both table selectors and the host's Deepcool design in 0.35

- Using the real `B` menu on table 1, the guest selected Water Cooling, CPU Delidding, Build Mode and Case Modding (`G#11`, `G#15`–`G#17`). The host then selected Water Cooling, CPU Delidding, Case Modding and Build Mode (`H#981`, `H#983`, `H#987`, `H#989`). For each change the other game logged applying the same tool to the same table. The selectors were operated one player at a time.
- The real catalog refused the host's Kolink Stronghold White selection (`H#1031`) with “Case template unavailable: The case component cannot be identified in its prefab.” The Deepcool MATREXX 55 worked in the same catalog (`H#1091`, PC 555). The host dropped it, picked it up again, switched the empty bench to Case Modding and dropped the case onto the tool bench. Both games showed that PC there. While the host used it, the guest's actual left click and right click both showed “Someone is using the case modding table.” The case stayed put and the guest visually read “Case modding” under the host's name.
- The host used the real Undo, Redo and Clear controls on an initial cyan pattern. Clear visibly removed the initial paint. The host then applied the Cityscape theme and two round stickers and added 90 multi-colour spray strokes. Both games visibly showed the same finish after the host closed the table. Both native reports gave design number 6, 96 layers and hash `EF97A154A2514220C9B3CB86B7E1EEFD89B323419E85F82FEC3761276308BC63` at revision 1713, with clean projections. The native design was 18,849 bytes before compression and 4,127 bytes after compression. The ignored evidence logs use `specialist-panel-035-*`. Individual Undo and Redo results and a separate guest design still needed checks at the end of this run.

### Guest case design and custom cable purchase in 0.36

- The Kolink Stronghold White catalog refusal in 0.35 came from reading case slots on its Addressables colour-variant wrapper. Commit `1d1cd11` follows the native `MaterialSwap` reference to the underlying prefab. In the owned 0.36 run both players spawned a white Kolink from their actual catalog. The guest's PC 683 included both default fans and dropped onto the Case Modding table. A separate profile mismatch appeared when inspecting the host's freshly spawned PC 619 in its remote-carrier state on the guest. It cleared after cold Continue put the case in the cupboard. The carried-state check is still under investigation.
- On November 6, 2026 the guest used the table's real controls. Undo removed the guest's magenta test mark, Redo restored it, and Clear removed it again. The guest then applied the sunset theme, a blue heart sticker and four yellow paint marks. Both games visibly rendered the same result after closing. PC 683 received design number 8, with 9 layers and hash `66057CA99A0406B27823A93AE73379A4D2C28A155BEC6138B2BB3ABF251769DF`. Both native reports also retained the original 96-layer Deepcool design number 6 and its original hash. Projection checks passed at revision 570. The host then dropped the Deepcool onto a separate regular bench.
- The guest created and bought **Coop Violet Gold** for $0 in the Free Build tablet. The native purchase shared 36 cable kinds in 3,655 bytes and the other inventory gained the six styles. Both players opened their real cable inventory and saw all six. The guest selected Flat Ribbon Plastic, unplugged PC 213's stock ATX lead (`G#11`), and connected its replacement (`G#13`). The host then selected Loose Plastic, unplugged the guest's lead (`H#807`) and connected the new one (`H#810`). Both installs used the native connect callback, with pointer reach and hold bypassed. The host and guest records matched after each install, including the pin indices `5,3,5,3,5,3,5,3` for the alternating coloured wires. Both projections passed at revisions 818 and 827. The ignored evidence logs use `case-design-036-*` and `cable-design-036-*`.
- Cold 0.36 Continue and a fresh guest loaded both design numbers, all 105 layers and the original design hashes across the two cases. Both games had the purchased set in the real cable inventory and PC 213 retained the Loose Plastic lead and the same pin indices. Both native projections passed at generation 1, revision 3, with matching object hash `73CFD9CA47F47A799525436D72339B3CDC02131E4496DC89CE0564647C4CA013`. Both also retained one GTX 1070 variant with the EKWB RGB block from the earlier water-bench run.

### Stock case carrier profile in 0.37

The 0.36 repeat reproduced the remote-carrier check on a new PC 762 before any move. Both copies held the same case SKU and two fans, but the guest's record had an empty PCI-cover array and both fans unflipped. The host's freshly captured record had seven fitted covers and both fans flipped. The host publishes the native build profile once it has a mesh. Commit `8ae88a8` gives the record-only carrier the same cover and fan defaults from the prefab, so it can match the profile immediately.

Both 0.37 games loaded the 0.36 save. A new host catalog click on the same white Kolink created PC 762 (`H#4`). While the host was still carrying it, both records had all seven covers and both flipped fans. Both profile and full native projection checks passed at revision 6, with matching object hash `0086DEEEFA7E90EC9782B4C056E5E89B10DCAE44675B4524C5CD91627D759010`. The host then stored PC 762 using the ordinary cupboard click (`H#6`), and the guest's cupboard displayed the second white Kolink. The Release build had zero warnings and errors and passed **722/722** automated checks. The ignored logs use `catalog-carrier-red-*` and `catalog-carrier-green-*`.

### Real cupboard file pickup in 0.37 and 0.38

- On November 6, 2026 the native exporter wrote two `.pcbs` fixtures from the painted test cases, using a one-pixel thumbnail to avoid the game's photo capture. The first-use tutorial only enables `LED_SHOWCASE_1.pcbs`. Each role's isolated `PCs` directory got a byte-identical copy of its own fixture under that name. The browser, duplicate confirmation and Pick Up were operated with real clicks.
- In 0.37 the host's imported Deepcool card appeared but Pick Up said “That stored PC has no co-op identity.” The next save refused a native PC created outside the accepted change stream. The first 0.38 candidate still refused that save after the duplicate confirmation. The native file path calls `SaveLoadSystem.AddComputerToStorage(ComputerSave)` when it regenerates the imported design, including from the delayed Yes callback.
- The revised 0.38 build keeps the file's record and design in the local cupboard view until the co-op pickup is accepted. The host could save after confirming the file and before Pick Up. Both games still had six shared PCs at that point. The host then picked up the file (`H#6`, imported PC 826), and both native projections agreed on the host carrier and a 96-layer design at revision 7. After an ordinary drop, both games rendered the same Cityscape and paint pattern. The guest then picked up the Deepcool and stored it (`G#2`), retrieved the earlier white Kolink 762 (`G#3`), and returned it to storage. The original case spawn, bench pickup and drop, cupboard store and retrieve all used normal game input. The ignored logs use `pc-file-red-*`, `pc-file-confirm-red-*` and `pc-file-green-*-first.log`.
- The guest selected its original Kolink file after Continue. It declined the first duplicate prompt, leaving the three existing cards. It accepted on retry, closed and reopened the cupboard, and picked up the local card (`G#3`, imported PC 890). Both projections passed while the guest held it, with the nine-layer design present on each game. Both visibly rendered the sunset, heart and yellow marks after the guest dropped it. The host picked it up (`H#5`) and stored it (`H#6`). The ignored logs use `pc-file-green-*-second.log`.
- The host saved both stored imports. All four source files matched their original SHA-256 before being moved out of the isolated file browser. Cold Continue and a fresh join restored both imported IDs in the cupboard with matching design hashes and 96 and 9 layers. Both games passed native projection with eight PCs, two imported, at generation 1, revision 3, with object hash `5394EFB48D068668EBADDF9382B7E480ED8C86286BB976DDF92127F3E00E2E61`. The Release build had zero warnings and errors and passed **722/722** automated checks. The 21 owner save files remained byte-identical after cleanup.

### Current workbench peripheral check in 0.38

On November 6, 2026 the guest right-clicked the monitor at workbench 2 and selected the DFL ExtraVision Green Edition (`DEFAULT_MONITOR_10`, `G#1`). Both games visibly showed the green bezel and stand. Native peripheral checks passed on both at revision 4, with peripheral hash `D2F0EDC4329CAB55AB8B312807D227D2E06BE7F5BE82C4F699473782826538C9`. Phase 3 retains the earlier real keyboard and mouse checks.

### Real Decorator and light controls in 0.38 and 0.39

On November 6, 2026 in `acceptance-d`, the guest used the actual tablet and chose Medieval Brick, PCBS1 Wood and Space Work Bench, clicking Apply To All after each. The two native decoration lists matched at `3B8D4D6B4C2A61C3` after the guest closed the app. The host then chose Rustic, Space and Future Work Bench and clicked Apply To All for each. Each player saw `Decorating` below the other's name. The selections affect the editor's rendering live. The peer receives the save-list picks and renders them after a workshop reload. Apply To All was a real click in every category. The native list still showed the four other workbenches as Contemporary.

In 0.38 the host's attempt to open the occupied app displayed the busy notice and threw a null reference when closing its unfinished native window. In 0.39 both real occupied attempts displayed the notice and returned to the tablet home. The prior close exception was absent. The 0.38 autosave loaded through cold Continue in 0.39 and a fresh guest joined. The actual views at bench 5 matched, including the Space wall panels and floor and the white and orange Future Work Bench. The initial restore listed the wall as Space with index 3. Once the Decorator resolved the selection, both reported `RUSTIC Concrete` at index 3 and matching native list hash `FE33E592977793E3`, while the wall in the rendered views still looked like Space. This comparison establishes that the two loaded rooms agree. The saved wall label and the room's rendered theme can differ.

Both players clicked the real hall switch and pressed the physical L key. Each game logged its own action and the other's corresponding transition. Both native lights and the shared fact followed, and both native ceiling fan animators went from speed 1 to 0 and back to 1. The guest pressed L once more to leave them off. A relaunched guest entered from snapshot 2 at generation 1, revision 15. Both games reported lights off and speeds `0,0`. The colour wheel and pulse remain open. Release built with zero warnings and errors and passed **722/722** automated checks.

The second guest also exposed a room-rendering mismatch at bench 3. It rendered the beige and tiled Rustic wall after the Decorator normalized its saved label, while the running host still showed the blue Space panels. Both reported native decoration hash `FE33E592977793E3`. The persistence checklist remains open because matching saved picks did not prove matching rendered rooms.

### OCCT after applying paste in 0.39

On November 6, 2026, both native records for PC 213 reported the applied paste and the installed SilverStone AIO. The actual host case-button entry point powered the PC (`H#10`), bypassing only the pointer input. Both games reached the desktop at 53.2°C with no throttle or blue screen. The host double-clicked the real OCCT icon and clicked ON in automatic mode. Both OCCT windows showed Finished at `00:00` with CPU Throttled set to No and a maximum CPU temperature around 65°C.

### Mirrored BIOS, OCCT failures and benchmarks in a 0.40 test build

- On November 6, 2026 in `acceptance-d`, the guest powered PC 213 on from the real monitor with P. An opt-in probe held the game's BIOS input for this PC, since the desktop tool could only tap the physical keys. The actual boot coroutine entered BIOS and the host followed. The guest selected CPU OC and increased x40 to x41. The host added seven increments, and both showed x48 at 1.20 V. The guest chose Apply and confirmed the restart (`G#40`). Both native simulations reached the desktop at x48. After the load tests, the host used the same held-input probe with its real P gesture. The guest followed into BIOS, and the host's real Factory Reset and confirmation (`H#166`) returned both desktops to x40.
- Each player opened OCCT and clicked its real ON button in separate automatic runs at 4,800 MHz and 1.20 V. Both runs showed the same clock-interrupt message on both games. The host logged exactly two `CPU_UNDER_VOLTAGE` failures in total. The guest suppressed each of its own simulated failures and applied the host's reason exactly once per run. The hexadecimal screen code differed by game. A guest power cycle between runs returned both to the desktop.
- The host installed a normal `USBDrive` in native motherboard slot 351 (`H#176`). The install command bypassed pointer targeting and inventory selection and ran the real edit and guest replay. A real guest double-click opened Add/Remove Programs locally while the host was in work mode. Its real 3DMark install landed only on the guest. The host's attempt to save started with its own old program list. The mirror hit the correct icon's `ClickCatcher` twice, but the host window stayed closed. The host then used the actual software manager to install 3DMark, Lighting and Cinebench. Both native lists gained all three applications.
- The host started the real Time Spy test and the guest started the real Cinebench R20 multi-core test. Both games rendered each run and displayed the same final scores. Time Spy reported **11,925** overall, **18,626** graphics and **3,925** CPU. Cinebench reported **1,703 cb**. The Release test build had zero warnings and errors and passed **722/722** automated checks.

### Guest software install while the host works in 0.40

The revised 0.40 desktop replay provides a temporary monitor state only while the game's `ProgramIcon.OnClick` runs. That native callback requires a monitor state before it opens a desktop app. In the owned retest, the host stayed in `DISASSEMBLY` on PC 213. The guest's real double-click opened Add/Remove Programs on both games. Its actual GPU Tuner install and 3DMark uninstall reached both native program lists. The guest closed and reopened the manager and reinstalled 3DMark using its real button. Both lists then contained `ADDPROGRAM`, `OCCT`, `LIGHTING`, `CINEBENCHR20`, `GPUTUNER` and `3DMARK`. The host's settled save paired successfully. That save began after the updated program list was already visible, so it does not establish the save-wait case. Ignored red logs are `workshop-and-desktop-040-red-host.log` and `workshop-and-desktop-040-red-guest.log`.

After actual Exit, cold Continue and fresh guest admission on the revised 0.40 build, both desktops contained the same six installed programs from that list.

### Decorator live and cold room retest in 0.40

The initial 0.40 candidate used the game's `UpdateOnlyMainRoom` and redrew Medieval walls on both games. The refresh also rewrote the peer's unrelated Space floor label and native scene-light calls were submitted as shared switch actions. The revised build preserves the selected native records after the asynchronous refresh and only shares the actual wall switch or the lights key. While the peer's room is still refreshing, capture waits.

The guest selected Space walls and a PCBS1 Wood floor through the real tablet. Both lists matched at `59C58850BCB0F09A` and retained the Future Work Bench. The host exited through the native Exit action, which wrote a paired save. Cold Continue and a fresh guest both reported the same hash and rendered the Space wall panels, wood floor and blue rug from the same cupboard position. The host then selected Futuristic walls and both games redrew them live. The native lists matched at `24BEDDFE7246CA2D`, with both lights on and both fan speeds `1,1`. Neither log contained a switch command during the theme edit. The guest clicked the real hall switch to turn both rooms off and the host pressed L to turn both on. The lights facts matched and both fan pairs went to `0,0` then `1,1`. No `AddressableFlag` objects or attached colour wheels were present in the checked Medieval, Space or Futuristic rooms. The real colour and pulse test remains open.

### Lighting HUD, native effects, presets and peripherals in 0.40 and 0.41

- In 0.40 a real host launch put the Lighting HUD over the guest's walking view, despite the guest remaining in `WalkingState`. `OS.Launch` creates that HUD in `WorkshopUI` before Lighting's camera guard. In 0.41 the real host launch left the guest at its desktop with no HUD, and the real guest launch left the host walking with no HUD. Both local launches opened the real app. The new hook is limited to Lighting's launch and the app's light edits still share.
- The host picked an orange cooler swatch and set the GPU to Rainbow, speed `3.7751987`, Downstream and Flow. Both games reported those values while running. The host saved Preset 1, changed the speed and direction, then loaded it and both native values returned. The host set both HyperX peripherals to magenta and used the advanced saturation slider. Both reported RGB `1,0.29719847,1`.
- The guest picked a green cooler swatch and adjusted the advanced brightness slider. Both reported green `0.87780154`. The guest changed the GPU to Rainbow, speed `2.884523`, Ping Pong and Walk, saved Preset 2, changed the effect, then loaded that preset. Both reported the restored values and both HyperX peripherals had the guest's blue `0,0,1` swatch. The PC remained running through the changes and the host did not have the HUD. The physical hue ring did not move in the attempted pointer gestures. The advanced saturation and brightness sliders did move.
- Cold Continue from the 0.40 save on 0.41 retained the host's orange cooler, GPU Rainbow settings and Preset 1. The two peripherals reverted to their factory colours on both games. After the 0.41 guest's blue edit, the host's real P-key power cycle kept blue on both peripherals in both games. Decoding the resulting isolated native save without instantiating its serialized types confirmed that both saved peripheral `m_LEDConfig` groups contain exactly `0,0,1`. The remaining loss is on workshop load. Release built with zero warnings and errors and passed **722/722** checks.

### Peripheral lighting after cold Continue in 0.42

On November 6, 2026 the host continued the same 0.41 save in a fresh process and a fresh guest joined. Both games restored one saved light for the HyperX keyboard and one for the HyperX mouse. Both native configurations reported RGB `0,0,1`. Both games rendered the blue keyboard and green cooler. The native GPU Rainbow, both presets and the active preset index matched. At revision 11 both games passed native projection with the same object, activity and peripheral hashes. The 0.42 Release build had zero warnings and errors and the network suite passed **722/722** checks.

The first 0.42 candidate found both peripheral configurations empty before the game registered its LEDs. The revised candidate installs the native saved configuration at that point and copies values in place if the live configuration already exists. The forced asynchronous load can then use the saved colours when it registers the LEDs.

During the same cold join, the first paired Rainbow samples were host `38.950924` seconds and guest `34.142628`. Five seconds later the gap was still about 4.8 seconds. After the periodic clock update at revision 12, the two samples were within 0.1 seconds. The early clock gap remains a separate failure.

### Rainbow clock on late join in 0.43

On November 6, 2026 the 0.43 host continued first. PC 213's saved Rainbow and blue peripherals were present and the guest joined later. The guest received a snapshot marked at revision 8 and entered with the host at revision 11. One second after the guest bound the workshop PCs, the running GPU clocks were `80.80195` and `80.74112` seconds. After adjusting for sample timestamps the difference was `0.058` seconds, within the mod's `0.15` second correction tolerance. The native GPU settings matched at Rainbow, speed `2.884523`, Ping Pong and Walk. The clock send now waits for admission so the transient command arrives after the snapshot mark.

Entering the local Lighting HUD for the next check briefly held the host's native clock at `82.085304` and the samples drifted. With both players walking again they matched within `0.011` seconds at revision 66. Physical taps and a drag on the advanced hue ring still did not move its selection. The hue-ring row stays open. Release built with zero warnings and errors and passed **722/722** checks.

### Walking monitor power in 0.44

On November 6, 2026 the host pressed P while walking and aiming at PC 213's monitor after the cooler was removed in 0.43. Its PC entered Boot and the guest stayed off. No Power edit was sent. The monitor's walking prompt uses `InteractableComputer.GetInteractions` callbacks `b__1` and `b__3`, which call the board directly. The 0.44 hook submits both through the existing Power edit.

The restored, cooled fixture loaded on both 0.44 games. The host switched off from that prompt (`H#32`) and the guest switched on from the same prompt (`G#6`). Both native reports showed the desktop and a running PC, with no divergence, projection fault or native check error at revision 42. Release built with zero warnings and errors.

### Missing CPU cooler at boot in 0.43 and 0.45

On November 6, 2026 the host ran the native removal callback for PC 213's CPU cooler slot. The two native slot and record reports agreed that `cpuCoolerID` was absent. A shared native case-button press started both PCs. In 0.43 the host displayed `CLOCK_WATCHDOG_TIMEOUT` and the guest stayed in Boot. The startup coroutine calls `VirtualComputer.BlueScreenOfDeath(CPU_HEAT)` directly, outside the simulation's existing `EmergencyShutdown` hook.

In 0.45 the same live removal and boot produced one host `CPU_HEAT` publication and one guest application. Both monitors displayed `CLOCK_WATCHDOG_TIMEOUT`. The native display generated its own hexadecimal code on each game. The host's startup path leaves `ComputerSim.BSODActive` clear. The guest's forced simulation call sets it when showing the host's result. Both native projection checks passed at revision 14. The removal and power probes called the actual game callbacks and skipped pointer targeting. Release built with zero warnings and errors and passed **722/722** checks.

A separate save from the 0.43 removal failed cold Continue with `Carried PC topology mismatch`. The CPU-side AIO proxy was removed, while its radiator-side entries and pipes remained. The failing pair is preserved separately. This heat test covers the live failure display, and the removal and cold-load path remains under investigation.

### One heat result per boot in 0.46

On November 6, 2026 the host booted PC 26 with its connected custom loop empty. In 0.45 the host published `CPU_HEAT` twice and the guest applied it once, then received a redundant report. In 0.46 two separate on and off cycles using the native case-button callback produced one host publication and one guest application on each boot. The matching reason is kept for one native simulation run and cleared when the PC starts again. The Release build had zero warnings and errors and passed **722/722** automated checks.

### Complete cooler removal and boot in 0.46

On November 6, 2026 the probe identified PC 213's SilverStone AIO root in front radiator slot 258. The other two radiator slots and the CPU cooler slot were proxies to that root. The host used the root's native removal callback (`H#21`). The guest reloaded the resulting PC. Both native records had zero SilverStone references and zero pipes and passed projection checks at revision 89.

The native case-button callback (`H#90`) booted the PC without its cooler. The host published `CPU_HEAT` once and the guest applied that failure once. Both simulation samples read 81.7°C, and both projection checks passed at revision 94. The probe bypassed pointer targeting for the removal and power press. This test used the complete AIO removal. The earlier CPU-slot-only save remains evidence of the separate proxy-removal bug.

### Two complete Kolink builds in 0.47

On November 7, 2026 the host started with a new stock black Kolink on bench 6 (PC 1000). The guest started with the same empty case on bench 5 (PC 1115), after using the game's actual Swap PC prompt and storing the swapped painted case. Both removed the side panels, fitted `Motherboard_MSI_30`, operated the AM4 lever for `CPU_AMD_58`, opened both clips and fitted `RAM_TeamGroup_41`, and installed `PSU_EVGA_13` and `Air_Cooler_AMD_1`. Each opened the PCI latch, removed the two covers needed for `GPU_NVIDIA_2`, and installed `SSD_TeamGroup_11` and a top `Case_Fan_ARCTIC_1`. The two stock fans remained. The host's blocked PCI cover removal was refused before the latch opened. The final native part paths, SKUs, orientations and flips matched for both PCs, with clean projection at revision 189.

The fixture and gestures used the native game edit and replay paths. The operator invoked the native callbacks and used the editor's accept flag at orientation, bypassing pointer targeting, part selection for stock items and orientation input. The actual swap and cupboard store used normal input.

The host opened a clip on empty RAM slot 4 (`H#73`) and asked the co-op save path to write during the same frame. It returned false and logged “a change is still being shown.” After the animation settled, the retry returned true and produced the native and sidecar pair.

### Save, Exit and cold Continue after both builds

The host used the game's Exit action after the explicit 0.47 save. Fresh 0.47 Continue refused PC 1000's motherboard assembly. Native installation had placed nine generated `Standoff(Clone)` objects directly under the board. The cold game recreated them under the board's `Screws` transform with different sibling indices. The nine prefab screws still had their original bindings.

The revised 0.48 build orders the generated standoffs in motherboard space and translates old keys before their IDs enter the receiving registry. Fresh Continue and a fresh guest loaded the same 0.47 pair. Each of the two builds retained all 11 native part paths, SKUs, orientations and flips. The saved empty RAM clip remained open on both games. All ten PCs bound with matching object hash `C124C91E718C0FC8E75381483635ED1821259D3532535CEF3CBA3699DCDB9D1B` at generation 1, revision 3. The host and guest had clean native object and peripheral checks. The Release build had zero warnings and errors and passed **722/722** automated checks. The 21 owner saves matched their original hashes after the owned processes closed for the rebuild.

### AIO proxy removal and reload

The guest's 0.47 attempt at the CPU-side proxy was refused while the main radiator mount was blocked by the front panel. The guest disconnected `501>114`, removed the panel and repeated the same native removal callback on proxy slot 359. The accepted water edit (`G#7`) removed all SilverStone parts and pipes from the guest. The host reloaded the result. Both native projections passed, and the guest's work view showed the bare CPU and empty radiator mount. The actual game callbacks bypassed pointer targeting and holding the mouse.

The same 0.47 save used for the two builds loaded in both fresh 0.48 games. PC 213's native part paths, SKUs, orientations and flips matched the records taken after removal, with zero AIO references and zero pipes.

### Installing the converted GTX 1070 in 0.48 and 0.49

On November 7, 2026 the guest selected the GTX 1070 with the EKWB RGB water block from the Graphics Cards inventory and saw the green preview in PC 1115. The isolated probe invoked the game's long-press callback for that exact preview. The 0.48 guest installed the card, but the host's detached `InstallingPartState` constructor threw and captured an incomplete assembly. The guest could not bind the resulting repair record. The game requests the block prefab before placing the local preview. Version 0.49 loads the variant's attached prefabs as well as its base prefab before constructing the remote replay.

The 0.49 retest began with fresh games and an isolated save from before the failing edit. The guest used the same inventory path and native callback. The host animated the result and both showed `GPU_NVIDIA_2` with `EKWB_GPU_WC_BLOCK_2` and four native connector entries for the card and block. At generation 1, revision 8, both games had clean projection and object hash `5035DAC3B66FEFE7741BEEE9AE23C0B1166740639E43381C03144015E5CC9B47`. The guest removed the complete card and block on both games. The host then selected and installed the same variant from the inventory. The guest replay finished, and both games had clean projection and object hash `1A21426AAAA02ADF686152A50B0AE4B8A06311FF7C5A86E7CCD5BCAB16C9234E` at revision 18. The probe supplied the callback for the inventory preview on each side, bypassing the physical hold. The Release build had zero warnings and errors and passed all 722 network checks.

Cold Continue and a fresh guest join restored the installed variant with all 146 assembly resources. Both games matched the same object hash at generation 1, revision 4, with clean native checks.

### Host and guest teardown in 0.48 and 0.49

The host stripped PC 1000 and the guest stripped PC 1115 on November 7, 2026. The game refused the added top fan while the Kolink dust filter blocked it. Both players removed the filter and then the fan through the native callbacks. The teardowns removed the GPU, cooler, CPU, RAM, PSU, SSD, added fan and motherboard. The CPU lever and RAM clips used the actual pin callbacks. The guest's final teardown also removed the cold-loaded EKWB card and its block. The probe supplied the native callback inputs and bypassed the physical pointer reach and hold.

Both games reported `Case_Kolink_4` and the two stock `Case_Fan_Generic_1` instances at `caseFanSlots[0]` and `caseFanSlots[5]` on each PC, with zero internal wires. The final native checks passed at generation 1, revision 49, with object hash `1312F32DC4971E6503CF0E89C6066B1C082CB90C9EF2C4B85626E05AD1409808` on both.

### Save during a software transfer and a BIOS restart in 0.49

On November 7, 2026 both games reached PC 213's desktop after the cooler and front panel were refitted and wired. The host used the actual Add/Remove Programs button to install System Info. The isolated probe called the real host save immediately after accepted software Begin `H#222` at revision 260 and before its chunks. The save guard reported “A software result is still being shared” and refused the write. After the two chunks completed, a normal save succeeded. Both games listed `HWINFO` and had boot-drive hash `A5731B15A39B0436`.

The host entered the native BIOS with the probe holding the BIOS key. The real ratio control changed the draft from x40 to x39 while both committed records still reported x40. A save started with the draft open. The host then used the real Apply and Yes buttons. The host accepted `H#259` and committed x39 before restarting. A save requested while the host reported the Boot surface started successfully. Both games reached the desktop with x39 and System Info. The exact pair written during Boot passed cold Continue and a fresh guest join, with the same BIOS values, software hash and native object hash `DAEBE91FD8D309739D5DFF652156771032CB2C3A26CC8FFDA3104D381B0CA45A`. The combined checklist wording calls for waiting during a BIOS change, so the row remains open even though the saved configuration matched.

### Tool table pickup, delid contention and halfway Escape in 0.49

On November 7, 2026 the guest right-clicked painted PC 683 on table 1 while the host used the Case Modding tool. The guest saw “Someone is using the case modding table.” Both games kept the host's claim and PC 683 on the tool slot. The same pickup succeeded after the host left the table and both games recorded the guest storing the case.

The guest selected CPU Delidding with the real bench menu. The host chose the stock i3-8350K from the actual CPU inventory. The guest's attempt to use the table showed “Someone is using the delidding table.” The guest backed up and saw “Delidding” under the host's name. The host reached native heat 100, ran the jig's two screws, lifted the IHS and cleaned the CPU. Escape left the incomplete scraper step open. After the scraper reported success, Escape closed it and then left the delid before varnish. Both games released the claim and their inventory variants were unchanged. The real inventory still showed the stock i3-8350K as “Delidded: no.”

The host then completed the full native procedure, including varnish, silicone, liquid metal, the IHS positioner and clamp. After selecting the finished CPU and closing the tool, both games had one `CPU_Intel_19` variant marked `delidded` at revision 1635. Tool clicks and all four material operations used real input. The isolated probe supplied the native held Action for heating and the three screws. The guest's first attempt got through liquid metal. The test processes later exited before the guest stored the CPU, so that attempt did not establish guest publication.

Cold Continue and a fresh guest both restored one delidded i3-8350K in their native inventories and a free Delidding table. Both games had clean native checks and object hash `4804A1178E0B09BED693DFEEAACE0454F2E83E09B29394CA424EA8AAF1F64BB4`.

### Simultaneous delid table entry and guest completion in 0.50

On November 7, 2026, both 0.49 games opened the native delid inventory from the same physical click while looking at table 1. The host accepted its own claim and refused the guest's request. The refused guest stayed in `InventoryState`, with `DeliddingState` below it. After the host left, the guest could select a processor and reach the heat gun from that stale inventory with no claim.

Version 0.50 unwinds the native child states through the refused table state. In a fresh two-game retest, the same interaction opened the guest's native inventory and the host refused `G#1`. At revision 91 the host still had its delid inventory and claim. The guest reported `WalkingState` with an empty state stack after the refusal. The Release build had zero warnings and errors and passed all 722 network checks.

After the host left the table, the guest entered normally and completed the native procedure on an i5-8600K. The guest used real clicks and strokes to select the tools, scrape the residue, apply varnish, silicone and liquid metal, refit the IHS, and collect the finished CPU. The isolated probe supplied the held Action for heating and the three screws. Escape from the returned inventory released the claim and published a 539-byte result. Both native inventories contained exactly one `CPU_Intel_17|delidded` and one `CPU_Intel_19|delidded` at revision 823. Both games had clean native checks and the same object hash as before the delid. The host's paired save succeeded.

### Installing delidded CPUs in 0.50

On November 7, 2026, the host powered off PC 213, removed its air cooler, opened the native socket shield and removed the stock CPU. The host chose the i3-8350K marked “Delidded: yes” from the actual CPU inventory. Both native sockets showed `CPU_Intel_19|delidded` at revision 890, with object hash `1D8B1EB44D887A79AE5FAAE47EC61104B3669C564A0451453B064EDDF0E7473B`.

The guest removed that CPU and chose the delidded i5-8600K from the same inventory category. Both native sockets showed `CPU_Intel_17|delidded` at revision 901, with object hash `FA1EB13E6BDD05EEB241AD24FAC8636A80EDA979506949E7238013B15C4BCCA9`. Neither install produced a native projection fault or divergence. The isolated probe supplied the native workbench callbacks, including the commit callback for each exact inventory preview.

### Rigid pipe geometry, Undo and Escape in 0.50

On November 7, 2026, the host opened a red rigid preview between PC 26's CPU outlet and radiator fitting. The isolated probe called the native fitting selection callbacks and read the preview's own node transforms. The host used the game's actual Bend tool. The native geometry changed from five nodes to six. The new internal node was at `(-0.14271443, 1.9992512, 0.20018518)` in case space. The real Undo button restored the five original positions and fitting flags exactly.

The host pressed Escape. Edit `H#1290` cancelled without a finished record. Both games kept just the existing `5>0` flexible route with 29 nodes. The rigid preview disappeared and fittings 1, 2, 3 and 4 were free again. Both games reported revision 1306 and the same object hash as when the preview opened, `43E0DC057C17B4917C05285C27F8ABC0073B76A097AA11F71A26F3B7F36F1A3C`, with no projection fault or divergence.

### CPU block and pump reload in 0.50 and 0.51

On November 7, 2026 the host removed PC 26's pipes, then removed its EKWB CPU block and pump and reinstalled the CPU block. Both games agreed on the native slots for each completed edit. Reinstalling the EKWB pump in 0.50 (`H#1608`) showed the pump on the host but put the guest into a rebind and repair loop. Both records held `reservoirSlots[0]` and `m_loops[0].m_components[0]`. The guest's graph also contained `reservoirSlots[0].m_generatedCopy`. The native bench loader creates this field when it deep-copies the water loop.

Version 0.51 keeps the reservoir slot and loop references in the accepted graph. A new binding leaves out the loader's reservoir copy link. Existing saves that already recorded that link keep it. The fix leaves the native save record intact.

Fresh Continue and a fresh guest loaded the exact 0.50 pair written after the failed reinstall. All ten PCs bound with clean checks. The host repeated the real pump removal and install callbacks. Both native slots showed `EKWB_WC_PUMP_4` after reinstall `H#84`, with no repair request or projection fault. The guest still had the extra native copy link. The common object hash at revision 162 was `431A5D2CE0D24E81F1E9BA434ED64D2172659D0E8C3663F73ADA806952B6F7C2`. The probe bypassed the physical pointer reach, hold and orientation confirmation. The Release build had zero warnings and errors and passed all 722 network checks. The 21 owner save hashes matched after both 0.50 test games closed.

### Delidding bench PC pickup applicability in 0.51

On November 7, 2026 the host opened the native Delidding bench on game 1.17.02. The guest carried a stock Kolink case (PC 1386). The target showed only the `B` refit prompt while the host held the claim and again after the host released it. Real left and right clicks at the bench left the case in the guest's hands and the Delidding slot empty in both states. The guest then stored the same case with the game's ordinary cupboard click. The probe spawned the stock fixture and positioned the players.

The native `BenchToolSlot.PopulateToolActions` Delidding branch offers entry to the loose CPU tool. The case pickup and swap actions belong to its Case Modding branch. A resident PC pickup from the Delidding bench is inapplicable in this build. The occupied Case Modding pickup refusal has its own passing result above.

### Halfway Escape from the RAM water-cooling tool in 0.51

On November 7, 2026 the guest selected two Team Group T-Force DARK DDR4 sticks in the real Water Cooling inventory. Real clicks removed all 16 old pads and placed all 16 replacements. Three of six native objectives were complete when the guest pressed Escape at the heatsink choice. The guest returned to walking and the log reported “session over; nothing changed.”

At revision 170 both native inventories had the same three variants as before, with no finished RAM item and no table claim. Both native object checks passed with hash `4F70879B2578665DCC063D8E08EC12409EFF59FE3BCBCD86972634B1D930BF8C`. The guest reopened the table and found the same stock DDR4 part in the catalog.

### RAM, X570 and radiator table results in 0.51

On November 7, 2026 the guest repeated the two-stick RAM procedure, selected the Alphacool DRAM heatsinks and X2 block, collected the assembly and closed the table. Both native inventories showed one `RAM_TeamGroup_28` with `ALPHACOOL_RAM_WC_BLOCK_1`. The host's first preview attempt on PC 26 lacked a valid placement with two DIMMs still installed. The case fit check remains open.

The guest removed the VRM cover from a GIGABYTE X570 AORUS ELITE WIFI at the table, installed a Ryzen 3 3100, applied sufficient paste and fitted the EKWB CPU-and-VRM monoblock. Both inventories received `Motherboard_GIGABYTE_27` with `CPU_AMD_51`, `EKWB_MB_WC_BLOCK_1` and the VRM covers off. The guest also completed the game's radiator fan workflow. Both inventories received `ALPHACOOL_WC_RADIATOR_1` with the explicit custom fan `Case_Fan_bequiet_5`. The guest used real table controls for both procedures.

The guest selected the exact converted board and radiator from the real inventory, then used the isolated commit callback for each preview on PC 1115. The host replayed both. The guest's other radiator previews were still present at the first readback. On November 8 a cold Continue and fresh guest join restored the X570, its Ryzen CPU and monoblock, and the top radiator with the actual Silent Wings fan in its native fan slot. Both games passed the native checks with object hash `CF51D7168A300237FCB74A8665094D22836ECC65F62148CEA4241846FAC89F57` at revision 3.

On November 8 the host opened all eight DIMM clips on the empty X570 and selected the exact Alphacool RAM variant. The game offered its two-stick assembly at the second and fourth sockets. The host installed into the second socket. Both games showed the kit in sockets 1 and 2, with the Alphacool block and its two native water fittings at revision 16. The guest opened the four relevant clips and used the native removal callback on each stick. Both slots were empty at revision 32. The guest then selected the same finished variant from the real inventory and installed it. After the remaining previews cleared, both cases passed their native checks with the complete kit and hash `BB95E8CCC826BA9702E174CFD404F360FD78C71C75D95D245FA9AD00328FF8AB` at revision 37.

The host also installed the exact converted X570 and radiator in a separate case, PC 1000. Both native graphs retained the Ryzen CPU, fitted EKWB monoblock, Alphacool radiator and actual Silent Wings fan. At revision 46 both games had clean native checks and hash `D29431B15106F88C335B4E924CDF33E0A9336A446569F4A981EB32B5DF2D5C89`. The workbench probes used the game's callbacks for the selected previews and the RAM removal gestures, bypassing the physical holds.

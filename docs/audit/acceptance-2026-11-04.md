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

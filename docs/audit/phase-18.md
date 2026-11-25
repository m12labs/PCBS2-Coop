# Phase 18 audit: Career recovery and acceptance

**Evidence:** static risk review. Use [the Career checklist](../career-testing.md#18-recovery-and-full-acceptance) for actual results. Free Build's phase 14 remains skipped for now. Its release checks have their own scope and status.

| Boundary | Invariant and evidence to collect |
| --- | --- |
| SS generation, order and duplicate handling, CP-055 | Run latency, loss, duplicate/reorder, stale-generation and disconnect cases against the Career handlers. Record one outcome per checkout, delivery claim, sale, reward and day transition. A guest loading a snapshot must catch up to the same revision and display the same result. |
| CP-024–028, 055–059, V-14–18 | Save/load and rejoin during mixed PC, economy and day activity. Inject interruption before acceptance, after reservation, after native work starts, after ordered settlement and during pair replacement. Recovery loads one complete accepted state or the last verified backup and releases abandoned claims. Record native and sidecar hashes and normalized ownership, ledger, day and job summaries. |
| CP-023, 027–028, 060–061, 069–070, V-17 | Check content and mode refusal for incompatible builds, unsupported Career modes, workshops and assets. Keep personal preferences and local file/account actions separate from shared game outcomes. Test that replay/rejoin does not award the other account's achievement or perform an upload. |
| All Career CP factors and reused HW/PW/SW paths, V-01–19 | Test the supported Career loop on two local instances and two machines. Use LAN and Steam admission/rejoin, two-way actor actions, simultaneous PC activity, busy multi-day play, stock/cash conflicts and a cold host restart. Record comparable state digests, visible outcomes, bandwidth, frame time and snapshot sizes. Run Free Build menu, hosting and save regressions. |

Classify each tested item as passed, partial, failed, out of the agreed first scope or unverified. A native symbol and a green network test are different kinds of evidence from a two-game pass. Record game/mod build, operator, fixture, transport, both actor outcomes and run logs for each result. Audit all Career factors against phases 15–18 before closure, including any unsupported modes, DLC paths or objectives surfaced during the run.

## Owner decisions for phase 18 (November 10, 2026)

- **Code sweep first:** 0.67 is 18a without a game launch: fault model tests for the Career actions, an outcome for every Career audit factor, and fixes for what the sweep finds. The in-game sweep (18b) follows with the owner.
- **Compatibility from now on:** the mod is unreleased and older co-op save versions aren't in use, so the sweep adds no checks for older save formats. Compatibility matters for saves made from now on.

## 0.67 implementation: fault and save recovery sweep (18a)

**Status:** coded with model tests on November 10, 2026. Release built with zero warnings or errors and all 930 automated checks passed. No game was launched. Nothing here is verified in the running game.

| Area | What 0.67 does |
| --- | --- |
| Career fault model tests | Every Career world runs on both games of one in-memory link. Cases: a job collect that arrives twice runs once; an order overtaken by a later request is refused and never placed late; a payment or tutorial step made before a host load is refused and dropped; a payment and a ready mark lost with the link never reach the host; an order and a job collect the host committed before the link dropped show on the rejoined guest from its snapshot, and trying again places or pays nothing more; a departing guest's package goes to a free bench once and its ready mark is taken back; a job held by the guest's accept stays held until the host's game has run it; a tutorial step done by both at once counts once; a late guest gets the same carts, packages, tutorials and revision and starts not ready. |
| Save during a client action | A host save no longer starts while a client's accepted job, bill or inbox action is still waiting for the host's game (`NativeJobs.Busy`). Otherwise that save would hold the world without the action. Shop, delivery and package actions already run when they are accepted. |
| Interrupted renovation | The shop renovation's save pair is marked to bind afresh. If the host's game stops before the renovated shop's first save, Continue or Load binds the shared IDs from the game, as the renovation's own reload does, and saves again once loaded. Before this, Continue refused that save because its pairing described the old shop's benches. |
| Save format | 0.67 writes one flag byte at the end of the pair. 0.67 reads 0.16–0.67 saves; a 0.66 pair reads without the flag. |

### Not covered by 0.67

- The in-game sweep (18b): the Career checklist on both games over LAN and Steam, two machines, busy multi-day play, a cold host restart, bandwidth, frame time and snapshot sizes, and the Free Build regression run.
- Interruptions inside the game's own code (a crash during the day iterator, the native checkout or the renovation's save) are checked only at the mod's boundaries: the last complete pair or its backup loads.
- The factors below marked *unverified* need evidence from the running game.

### Runtime checks for the gameplay sweep

1. Client accepts or collects a job and the host saves (Save screen) at once: the log shows `save waits: a job or e-mail action is still being run`, then the save; Continue shows the job's result.
2. Order the renovation, end the day, and close the host's game as soon as `[career] the shop renovation saved` appears. Continue: the log shows `is the shop renovation's save: its shared IDs are bound afresh`, the renovated shop loads, and a save follows. Client then joins.
3. Disconnect Client (close its game) right after it presses Buy, Collect or Pay: after Rejoin there is one order, one payment and one result.

## 0.96–0.99 guest menu return and visible Rejoin (18b)

**Status: focused fix passed in game.** Two isolated local Career instances, Epic game 1.17.02, loopback, run `career-074-a`, November 18. The 0.98 production fix passed the actual timeout/menu/Rejoin flow; 0.99 removes temporary tracing and keeps the same production behavior. Broader Career acceptance remains open.

The regression command `.local/Test-CareerRejoinMenu.ps1` verifies an admitted guest, suspends that owned process for 12 seconds, resumes it, waits for the native menu transition, then asserts that Main_Menu has no active loading flag, wipe camera or wipe image and offers Rejoin. The 0.96/0.97 run failed with all three loading indicators still true. Desktop captures independently showed the splash instead of the dialog. The temporary native coroutine trace stopped at LoadASync state 7: it was waiting for `LevelLoadPersistency.m_mainMenuPCLoaded`. Time advanced normally, no save thread was running, and lighting probes had settled. Normal guest Exit completed the transition, so the cause was narrowed to the menu-return setup; its first 5-second diagnostic was slightly premature, and the final regression allows 10 seconds.

The guest retained `LevelLoadPersistency.isNewStartObjectiveActive=true` after the Career snapshot load. Native ExitPanel.Exit clears that **scene-routing flag** before loading Main_Menu. The mod's direct return did not, so OnSceneLoaded took the opening-tutorial branch and skipped the menu PC setup that the loader waits for. The fix shares one LoadMenu helper across disconnect and the existing host/guest sync menu-return paths. It clears the same flag and lets the native loader finish; saved/shared tutorial progress is unchanged.

**0.98 green:** at22:26:40UTC the guest reported Main_Menu with loading=false, wipeCamera=false, wipeImage=false and the Rejoin dialog. Its trace confirmed the flag had been true before the helper cleared it, and the native load finished about 5 seconds after resume. The desktop operator saw the actual Rejoin button, clicked it once and observed the Client header back in the workshop with cash 105. Native logs confirm fresh snapshot admission, four bound PCs, 360 matched owned items and restored guest position. The two-game post-rejoin hash comparison was not collected before the interruption; do not claim that wider check passed. All 963 automated checks passed under 0.98. No Unity-free test reproduces the native scene loader; the live regression and desktop verification are the relevant seam.

Temporary `[DEBUG-rejoin-*]` tracing and its hook were removed for 0.99. Fix committed as `54f7f3d`. The final Release build is clean and all 963 checks pass; its persistence test now derives a future version because the old hard-coded 0.99 value became current. Both games are confirmed closed, and all 41 original save files remained byte-identical. Evidence: `.local/phase6-career-074-a/096-menu-red`, `097-stage7-red`, `097-native-exit-comparison`, `098-rejoin-pass`, `rejoin-menu-096-red.json`, `rejoin-menu-098-green.json`; build/test logs `.local/career-098-*` and `career-099-*`.

## 0.92–0.95 first-job settlement and recovery (18b)

**Status:** first virus job and cold restore passed; full Career cases remain open. Two isolated local instances, Epic game 1.17.02, direct loopback, run `career-074-a`, November 18, 2026. Native callback probes supplement real desktop controls and are identified below. Version 0.95 builds without warnings/errors; all **963 automated checks pass**.

The actual Email Collect action exposed two separate faults. First, native job-PC destruction left PC 720 in the accepted model because a cleared bench retained its old ComputerSave alias. Version 0.92 checks the actual case in the expected bench and waits until the frame after native Object.Destroy; pending removal checks also hold saves and End Day. Version 0.95 retains the original PC location during that wait, so an unrelated PC moved in the intervening frame keeps its new custody.

Second, cash agreed at 105 but the guest had two identical 120 ledger entries. Mirrored desktop input already runs inside NativeProjection; the job/inbox guards checked that broad scope first and therefore allowed the mirrored Collect to run locally. Version 0.93 checks desktop injection before projection, keeping job and mail actions on the acting game and the host's authoritative path. The real Collect repeat passed: both cash 105 / day 0 / kudos 150, exactly one ledger entry, four PCs, first-day tutorial COMPLETE 17, opening tutorial COMPLETE 27, matching native jobs/inventory/ledger and canonical revision 253 / chain `574B553E1BDB50E6`. Native projection checks were clean. Ledger digest on both was `E0BF860E511AAF8DBB587809EB9D1BA0F7C594A50877CB96AF43E1BD273F6FAE`.

The completed pair cold-restored under 0.94 with office power/OS available and all native/canonical comparisons passing. A 12-second suspension disconnected the owned guest while the host continued hosting. The guest reported a Rejoin button internally, but desktop capture showed a persistent loading screen after returning to the menu; the button was not clicked and its visual usability is unverified. Restarting only the guest admitted it to the still-live host: revision 215 / chain `23152703A0B06E14`, cash 105 / day 0, four stable PC identities and clean native checks. The guest's real bench pickup/drop callbacks moved PC 20 from bench 3 to 1 and back; strict comparisons passed while carried and after each drop (last revision 293 / chain `7C4C65A10C249968`). This certifies the callback path, not physical walking input. Host ExitPanel.Exit saved a paired world and visibly returned the host to its menu; the guest again showed the loading-screen symptom after disconnect.

End Day is **still unverified**. The tablet is natively locked in this checkpoint. Available door interactions call the tutorial route while RenameBusinessOnboarding is incomplete (currently inactive0); native GetInteractions selects DaySummary only once that tutorial is complete. Three showcase jobs remain. No tutorial progress or invisible End Day button was forced. The day-zero ready fix retains model-test coverage only. The 0.94 probe adds door aiming, tablet/day-screen diagnostics and a native End Day callback restricted to an actually visible enabled button.

The real Collect repeated successfully under 0.95: revision 305 / chain `4BBA7877B0BF7E8A`, one identical ledger entry on both peers, four PCs and clean native projection. The settlement fix is committed as `316c712`; earlier USB/day/probe work remains separate WIP. The completed 0.95 pair then cold-restored on both games with strict agreement at revision 39 / chain `4B1B9C42ECBA2531`. A second 12-second guest timeout reproduced the inaccessible Rejoin/loading-screen symptom. Final host Exit saved the checkpoint; both owned games were closed and all 41 original save files remained byte-identical. Remaining focused checks: exact guest front-port USB pickup after 0.90; active-preview `/sync`; software convergence after virus cleaning; guest menu/loading-screen recovery and the visible Rejoin button; showcase progression/business naming, then day-zero ready/End Day. Broader Career acceptance stays open.

Ignored evidence: `092-duplicate-ledger`, `093-collection-pass`, `094-disconnect-menu`, `094-rejoin-move-exit`, `ledger-092-live.json`, `ledger-093-pass.json`, `095-collection-pass`, `ledger-095-pass.json`; `.local/career-093-tests.log`, `career-094-tests.log`, `career-095-tests.log`. The completed 0.94 pair is archived before restoring the pre-collection fixture for the 0.95 repeat.

## 0.85–0.91 USB transfers and first virus job (18b)

**Status: in progress, November 17, 2026.** Same isolated `career-074-a` campaign, Epic 1.17.02, two local instances over loopback. The transfer regression first failed eight checks against 0.84; the expanded host/guest pickup, occupied-bench swap and snapshot cases now pass, with **963 total automated checks**. Version 0.91 builds cleanly and the seven deployment hashes match. Runtime evidence below distinguishes passed boundaries from remaining failures.

- **0.84 red reproduction:** native pickup/drop of boxed PC 720 alone, without any USB selection, made strict projection fail on `usbSlots[6]` and its generated copy. The ignored assertion helper now has `-StrictProjection`, which fails on a native object diagnostic rather than merely printing it.
- **0.85–0.87 correction:** the accepted transfer retires removed USB roots/copies, clears affected resource occupants with a new version and carries the host's predicted assembly profile, computed from a cloned native record. Both adapters get only the exact removed roots. Local departure invalidates cached physical bindings; capture prefers the actual carried PC over an empty bench's stale save alias. Both peers passed native checks while the host carried PC 720 (revision 23 / chain `9E380703C3643831`) and after drop (36 / `FB05081C531C1215`).
- **0.88 reusable USB replay:** a native USB-tool install keeps its owned template in inventory, so its installed copy has no consumed item ID. The guest previously treated that as an unowned part and repaired the PC. Replay now copies an existing owned plain USB template only for that native reusable path; a missing template, consumables and cloned-OS devices still require the original exact-source rules. The guided USB install at resource 878 replayed in 0.19 s without repair; both peers reached first-day CONNECT_CABLES (5), with clean native checks at revision 237 / chain `848A7AD13940FC26`. An earlier test-target mistake installed another USB in front slot 879; both became real shared facts, with no fabricated item or tutorial step.
- **0.88 recovery preparation:** `/sync` cancels uncommitted native inventory previews before checking capture stability, including a tutorial that gates Exit. The active-preview timeout case still needs its direct runtime repeat. Read-only reports now include native tutorial utility-job progress and selected owned item. The guide must reach INSTALL_PART after its highlighted category and item are selected, and its yellow target must be used.
- **0.89 cable/job progress:** the host connected real workstation MOUSE→MOBOUSB0, KEYBOARD→MOBOUSB1, HDMI→HDMI1 and POWER→MAINS through native endpoint choices. The PC booted, the host used the actual monitor UI to install Virus Scanner, scanned 715 infected files and cleared them to zero. Both games showed boot and virus objectives satisfied and first-day PICKUP_PC (11). Native callback probes bypass pointer/hold selection; app controls used desktop input. Workstation connector observations and a native cable-choice cancel helper were added to the isolated probe.
- **Guest return exposed a wider USB scope:** 0.89 removed both front USB devices during guest pickup, while the first correction predicted only motherboard-owned resource slots. The guest reported missing `usbSlots[12]`/`[13]`; its attempted bench repair was refused while carried, then timed out. The host retained the clean-virus result in the corridor and `/sync` recovered both games at generation 2. **0.90 extends retirement to all installed USB roots when a motherboard is fitted. The expanded model cases pass; its exact guest runtime repeat remains open.**
- **0.90 native jobs cache:** the live fingerprint comparison isolated the earlier jobs mismatch to `m_pcStatus` for jobs 2–4: the host had blank values while guest deserialization computed Missing pipe / Ready to boot. Inbox layout 2 carries the host's nullable status map explicitly, validates it against the detached graph, then publishes the lists. Layout 1 still reads. This preserves the host's visible result instead of dropping the diagnostic. A live post-change runtime comparison remains required.
- **0.91 powered restore:** after cold Continue at first-day HAND_IN_JOB (13), the office PC was black although shared `Powered` remained true. Both normal power gestures did nothing. Native `Motherboard.TurnOn` calls the tutorial's power gate; the first-day tutorial allows the office power button only before step 3, assuming it stays on afterward. The load-only restoration scope now lets that one already-powered PC pass the gate. Ordinary player input is unchanged. Runtime boot/collection check is underway.

Native UI icon formatting also emitted last-active-controller null-reference trampoline errors; observed tutorial/app progress continued. The guest repeatedly adopted the host's software record after virus cleaning before pickup; retain that log for a separate software convergence check. Full Career checklist rows remain open. End Day on day zero, reward settlement, remaining transfers, active-preview recovery and interruption/rejoin must be recorded separately before closing their cases.

Ignored evidence archives: `084-nov17-repro`, `085-usb-bindings`, `086-carried-record`, `087-usb-installed`, `088-usb-cables`, `089-virus-return-sync`, `090-office-power`; raw field difference `jobs-fingerprint-089-live.json`; test outputs `career-usb-red-tests.log`, `career-088-tests.log`, `career-090-tests.log`.

## 0.83–0.84 opening tutorial and USB transfer failure (18b)

**Status:** partial pass with an open runtime failure. Two isolated local instances, run `career-074-a`, November 17 UTC (November 16 local), Epic game 1.17.02, direct loopback, desktop operator with native probe callbacks where stated. Version 0.84 builds cleanly and all **943 automated checks pass**. Both owned games were closed at the stopping point; all 41 existing save files stayed byte-identical.

The host used the actual office monitor, Email and inbox controls to accept **Paint It Red**, select the accepted **Help needed with virus** job and press Go to PC. Both games reached `NewStartOnboarding.COMPLETE` (27). A complete paired save cold-restored under 0.84, retaining the campaign, day 0, cash -15, five PC identities, allocator 924 and 360 owned item IDs. The office monitor is peripheral index **23** (`ShopComputer/Peripherals/MonitorSlot`); index 0 is the empty first-workbench monitor. After COMPLETE, the first-day tutorial intentionally gates the office monitor until its virus job progresses. The old note/workbench showed no stale yellow guide outline in the inspected guest view; the delivery corridor was offscreen and is not certified.

The host picked boxed job PC 720 from delivery slot 7 using the available native selected ALTERNATIVE callback, then used the native workshop drop onto workbench 1. Both peers showed it on bench 1, with matching canonical state and native career digests. The first-day tutorial followed on both games to INSTALL_USB (4). **The transfer is not a full pass:** the explicit topology check reported 20 expected versus 18 native links, missing `usbSlots[6]` and `usbSlots[6].m_generatedCopy`. The host then selected the actual owned USB tool and held Action over a native USB target. Install at slot 879 was accepted as H#1250, failed in `NativeComponentEdits.Identities` before the edit could start, and correctly raised out-of-sync. This also confirms the work-mode Action hold reaches the real gesture; walking input remains unproven.

The native `Case.OnPickup` trace returns motherboard USB components to inventory and destroys them. `NativeObjectState.HoldRemote` already warns that pickup side effects can change the graph, but retains the prior logical part facts. The next edit and normal save require those facts to match. The failure therefore needs a host-ordered transfer/part reconciliation fix, including actual pickup/drop timing, generated-copy aliases, resources and exact Career inventory ownership. Do not bypass `CheckParts` or fabricate an item. Reproduce from the COMPLETE 27 boxed-PC fixture, compare after pickup and drop, and test both actor roles plus save/rejoin after the correction. No production correction for this failure has been applied yet.

A host `/sync` attempt while the required USB preview remained active timed out with `the world didn't settle for saving`. The tutorial also gated normal exit; Escape/input exit did not leave work and selecting Remove did not clear the preview. The failed state was archived, not treated as recovered or saved. The last readable COMPLETE 27 pair predates the failed pickup/install and is retained for repeat testing.

A separate native jobs digest difference appeared after the live 0.83 inbox changes despite matching canonical state. The 0.84 diagnostic adds the raw native fingerprint; both fresh processes then matched fingerprint/digest `AE4C2E96097ABD68337BCA4A392009ACCF4C6D86DB8EE96E88C8E63ED6CF2259`. This cold-load agreement does not prove the live discrepancy fixed. Reproduce a legitimate inbox change after the native monitor gate lifts and compare fields before modifying serialization.

Ignored local evidence: `083-nov17-complete27` (0.83 save/logs), `084-usb-fault` (both event streams, logs and the last 0.84 pair); raw fingerprint baseline `fingerprint-084-initial.json`; automated output `.local/career-084-tests.log`. C15-04, C17-01/C17-05/C17-06 and C18-01 have partial evidence; all full checklist cases remain open. Virus removal/reward, day-zero End Day, pause-menu Save and mid-job disconnect/rejoin are still unverified.

## 0.82 walking input and Career evidence (18b)

The desktop click driver began refusing input in the cursor-locked walking view (`foreground window did not report a process id`). Physical keyboard movement also reached both local games through background input. The opt-in probe now provides `input-hold:<button>:<seconds>` for one process's native action, secondary, menu, inventory, power, tablet or exit button. It supplies down/held/up frames for at most ten seconds and cancels when the native state changes; the game still performs targeting and routes its own interaction. `gesture-pickup:<pc>` is a separate native `BenchSlot.OnPickupPC` callback seam for a visible unpacked PC, explicitly bypassing pointer/reach input. These helpers do not certify physical click acceptance.

`career-report` now records native jobs/inbox/calendar and ledger digests, peripheral inventory, package positions and the day-ready flag, so the day and rejoin checks can compare native results as well as canonical world hashes. In 0.82 native pickup and under-desk drop advanced both peers to step 18, then native power started the office PC and its OS. Both peers matched revision 338 / chain `D2E407D5A20A995F`, including the added native digests. A readable 925,663-byte save pair was captured while walking. Walking input holds did not trigger: the native path uses `InputButton.ConsumeDown` directly through Rewired, outside the earlier IsDown/IsHeld hooks. 0.83 adds that probe-only hook and presents down on the next complete frame; the current native-selected target's available callback is also exposed as a clearly identified supplemental seam. Release builds cleanly; the run stopped at the owner's request with the powered office PC saved at step 18. All owned game processes were closed and all 41 existing save files remained unchanged. Monitor/email, the new 0.83 input seam, End Day and remaining movement/rejoin cases are unverified.

## 0.80–0.81 avatar shader and cabling (18b)

**Status:** 0.80 two-game shader comparison passed; the permanent 0.81 fix passed cold-launch visual checks on both games. Release builds with zero warnings/errors and all 943 checks pass. Run `career-074-a`, November 16.

The bundled flat material's three slots had no bound maps, opaque surface state, the intended colours and a shader reporting supported, yet the visible remote body showed room textures. At the same unobstructed guest camera position (about 1.6 m from the host), changing **only the shader asset** to the game's existing HDRP/Lit made the model clean solid blue with orange accents. 0.81 therefore copies the bundle's plain material defaults and assigns the game's compiled shader; no scene material values or textures are copied. The temporary shader-switch probe was removed after the comparison.

GPU cabling also passed in 0.80: the host connected all three native `PSU_ADAPTER_12_3X8PIN_45` leads, the guest showed the wiring, and both advanced to side-panel step 15 with matching revision 461 / chain `21215ECEF4165C4E`, cash, day and owned items. No projection/out-of-sync fault. Side-panel refit and all four native screws then completed on the host, replayed on the guest, and both reached step 17 after leaving work mode (revision 811 / chain `889F8630C43850EC`). A 925,633-byte native save and its paired sidecar completed and were copied to the guest; cold 0.81 host Continue and guest admission restored step 17, all 360 shared items, fitted panels and cables without a projection fault (revision 20 / chain `28F6808CF7C5279E`). The native cable callbacks bypassed pointer selection but retained the production claim, native connection state, replay and settlement.

## 0.79 slow panel fitting (18b)

**Status:** coded November 16; Release builds with zero warnings/errors and all **943 checks pass**. The isolated step-13 regression passed its long-wait condition in 0.79: host refit began at 18:35:46 UTC, and at 18:39:49 the host was still fitting its four native screws at step 14 while the guest correctly remained at step 13. Neither peer reported a projection/divergence fault. Completion also passed in 0.80: the host tightened all four actual native screws using timed Action holds, the guest replayed the refit, and both reached step 14 with matching revision 162 / chain `7FECE7D35C20FD7A` and no projection/out-of-sync fault. The refit-cache fix from 0.77 also ran without the earlier missing-reference exception.

In 0.78, the host began refitting the tutorial PCI lock but remained in the native fitting state. The guest correctly held step 14 at first, then logged `the other player's change took over 130 s to show here; following their step anyway` while the panel claim was still active. Hand-turned panel screws have no such deadline. The tutorial now keeps waiting while the other player's edit or replay is unfinished; existing edit failure and departure recovery remain responsible for ending that work. No native tutorial step is advanced by a timer.

Probe evidence now includes the active install's remaining and completed screws with viewport positions (0.80 uses the actual native install camera and precise client pixels) (PCI-lock screws are intentionally absent from the shared manifest), and remote renderer bounds, active flags, material colours and line-of-sight colliders. These distinguish test input problems and occluded avatars from projection or material failures.

## 0.78 cold restore with removed panels (18b)

**Status:** 0.78 Release built with zero warnings/errors; all **943 checks passed**. The previously failing fixture now cold-loads at tutorial step 13 on both host and guest, retaining all 360 remaining shared item IDs. Both peers matched revision 59 / chain `C3AB01C91D680AD8`; a second cold host launch and guest admission passed on November 16. Remaining gameplay checks are in progress.

The 0.76 step-13 save was readable and hash-matched, but 0.77 cold Continue failed identity admission: the native load fitted `Case_EVGA_6/PCI Plate` while the shared panel fact was empty. The live assembly updater already corrected this after individual PC reloads (0.69); the initial paired restore validated before that updater could run. `BindObserved` now uses the existing `MatchPanels` correction before assembly validation and recaptures the corrected native record at that explicit load boundary. Identity bindings are still published only after the complete map passes.

Runtime regression: the same fixture loaded in 0.78 with `matched: PCI_Plate removed`, preserved PC 595 and tutorial step 13, and reported no projection/out-of-sync fault. The native WalkingState feet were at Y 0.20; the saved host position was not below the floor. This does not yet close the separate work-state remote-position finding.

## 0.77 first-day readiness fix (18b)

**Status:** coded during the November 15 sweep; Release build has zero warnings/errors and all **943 checks pass**. Its first cold-resume attempt exposed the panel mismatch fixed in 0.78, before refit validation could run. The fixes below continue in 0.78.

The real starting Career reports `Calendar.GetToday() == 0`. The ready-check model rejected zero and also used it for “not ready.” Two new integration assertions failed against the actual guest command and snapshot path. A separate `HasReady` flag now distinguishes day-zero readiness from an absent mark; native callbacks use a nullable day to distinguish clearing it. The day state appends an optional boolean, so 0.76 and earlier states still read (zero absent, positive ready). New tests cover day-zero acceptance, snapshot restore, departure cleanup and both older state forms.

The 0.76 host also reached `InstallPCILock` without `pciSlot` / `pciLockID`: only the guest had run the earlier removal that caches them. Its native collision setup threw on the missing slot. Before native refit steps, 0.77 now binds the fixed tutorial PC's slot and SKU from the shared integral panel record, including the side-panel refit. This has no standalone Unity-free seam; the saved step-13 two-game fixture is its regression check.

The sweep also found that `/outlines` appended its focus report after a forty-entry cap; a busy PC filled the cap before the focus entries. Focus is now logged separately. `career-report` adds native work-state positions after a visual check found the remote worker below the floor. The report is diagnostic; no position fix is claimed yet. `gesture-install` now refuses Career because that older Free Build helper constructs a fresh part: Career testing must select the real owned item and commit its preview.

## 0.76 autonomous Career sweep (18b)

**Status:** in progress. Two local Windows instances over LAN, isolated run `career-074-a`, game 1.17.02 / Unity 6000.2.6f2. Release builds have zero warnings/errors; all 937 automated checks passed for 0.74–0.76. Desktop control uses Windows pointer input, with native probes explicitly identified below.

| Check | Evidence so far |
| --- | --- |
| C15-03, partial | 0.74 started a fresh standard Career and admitted the guest from a 925,992-byte hashed snapshot. Both games showed day 0, cash −15, the same tutorial and owned items. Object/peripheral hashes and revision 61 / chain `F2B7F2C3A92A4C3A` matched after the note step. |
| C17-05 / C17-06, partial | Guest read Uncle Tim's note using normal input; it disappeared on the host. Guest collected the tutorial GPU, picked up PC 595 from the delivery area, dropped it on workbench 1 and entered work mode. Both tutorials reached step 8. In 0.76 the host waited through all four guest side-panel screws, replayed removal in 1.28 s and only then followed step 9. Both games matched revision 79 / chain `1D2B65196E9AA67D`. The host then installed the actual delivered GPU into Slot_GPU_T through its inventory preview and native commit callback. Both stayed at step 13 for minutes: revision 453 / chain `EA9E48CE6C383F25`, and later revision 815 / chain `684D2DC1CD212EDB`, with equal native Career reports and PC/peripheral/activity hashes. GPU and automatic power adapter were present; no step 13→12 regression or out-of-sync. A native refit-cache warning and later outline checks remain open. |
| C15-04, partial | Host probe `save` wrote the isolated autosave and its matching sidecar and sent a copy to the guest. 0.76 cold Continue and guest rejoin restored PC 595 on workbench 1, tutorial step 8 and all 361 shared item IDs. |
| Save isolation | The first stop check confirmed all 41 owner save files byte-identical. 0.75's Career resume failed because its in-game setup still checked the Free Build folder; a manual Continue then opened an owner Career save. The instance was stopped immediately and all 41 files were still byte-identical. 0.76 validates the Career subfolder, redirects before validation, and quits an opt-in test process when setup fails. |
| Player material | 0.74 reproduced different borrowed templates: `Retro_Organiser` and `Gui_Letter_Yellow_Line`. 0.75 bundles its own opaque HDRP/Lit template (matching HDRP 17.2.0), which reopened in Unity with no maps and black emission; runtime visual verification remains open. |

Test plumbing: `Start-Phase6Probe.ps1 -World career` opts into Career New Game or Continue without changing the ordinary Free Build self test. `career-report` records native tutorial steps, day, cash, inventory and targets. `action-hold:<seconds>` is a test-only substitute for physical Action button down/hold/up, restricted to the current work/edit/screw state: the game still performs hit testing, long presses, screw movement and state transitions. It does not count as physical hold-input acceptance.

Boxed Career delivery PCs are intentionally hidden by the game on both peers; the generic `summary-report.objectsCheck` currently reports these as hidden. Record that diagnostic separately from an actual out-of-sync fault. PC 595 left its box normally and matched on both games at the workbench.

## 0.73 fixes from the sixth in-game sweep run (18b)

**Status:** coded November 12, 2026 after the sixth Career run (both games on 0.72; the Client did the job up to the GPU, the Host fitted the GPU). Release built with zero warnings or errors; all 937 automated checks passed. The GPU step regression passed in the 0.76 sweep above; yellow-outline acceptance remains in progress.

0.72 confirmed: the other game showed the side panel, PCI lock and PCI cover removals and the PCI lock refit with the game's own animation (`[replay] panel removal/refit at ...`); no out-of-sync.

| Finding | Cause | 0.73 |
| --- | --- | --- |
| After the Host fitted the GPU, the tutorial stayed on "install the GPU" on both games (owner: "didn't finish GPU install"). | At INSTALL_PCI the game puts the tutorial back to INSTALL_GPU when the first GPU slot is empty (`NewStartOnboarding.InstallPCILock`, 0x832300, sets progress 12). The Host's tutorial moved on as the GPU went in; the Client followed step 13 before it showed the install (in Career it waits for the commit), its game went back to 12, and the Host followed it back. | A game holds the other player's tutorial step while their part or panel change isn't shown here yet (`[tutorial] ... held until ...`), then follows it. If the tutorial still goes back within 3 s of following, it follows once more instead of moving the other player back. |
| Yellow lines stayed (owner). `/outlines` on the Host listed no yellow outline: only green and red part outlines on the PC on the bench. | The game's outline colours are -1 off, 0 orange, 1 green, 2 red, 3 yellow (tip) (`CustomOutline..cctor`); the leftover report had printed them as numbers. The yellow lines are not a `CustomOutline`. | `/outlines` names the colours, counts only shown outlines, finds yellow-looking materials by colour as well as by name, and lists what lies under the middle of the screen (renderer, material, shader). |

## 0.72 fixes from the fifth in-game sweep run (18b)

**Status:** coded November 12, 2026 after the fifth Career run (both games on 0.71; the Client did the job). Release built with zero warnings or errors; all 937 automated checks passed. Not yet run in game.

0.71 confirmed: no Career error flood; the step-end handlers ran (`ended step 5/6 the game's own way`); one second after each followed step, each game drew only that step's own targets (the PC, the desk).

| Finding | Cause | 0.72 |
| --- | --- | --- |
| Refitting the PCI lock put both games out of sync. The GPU's six `Heatsink/Screws/screwStandoff` screws left the GPU: the Host (direct refit) had them inside the lock, the Client (hand refit) had none. | The game makes a PCI card's bracket screws (`Slot.InstantiatePCIScrews`, `CodeReferenced/GenericPCIScrews`) and deletes those a fitted part covers (`Slot.RemoveContainedScrews`) only in `Slot.InstantiatePart` (a player's install), never in `InstantiatePreloadedPart` (a load or a direct refit). The screws therefore depend on how the part arrived. | Owner decision (November 12, 2026), both: bracket screws (`screwStandoff ...`) and a fitted PCI lock's screws are left out of the shared PC record (manifests written earlier still load; their entries are ignored), and the other game shows a finished panel change with the game's own install or removal (`[replay] panel refit at ...`), falling back to the direct change. |
| Yellow boxes still showed (owner). | Not found: the logs list no leftover outline one second after the steps. | `/outlines` in chat logs everything this game draws in outlines and the tutorial's yellow at that moment. |

Owner goal (November 12, 2026): a smooth co-op game without gaps; eventually both players work on the same PC at once (today one install or removal holds the whole PC).

## 0.71 fixes from the fourth in-game sweep run (18b)

**Status:** coded November 12, 2026 after the fourth Career run (both games on 0.70; the Client did the job). Release built with zero warnings or errors; all 937 automated checks passed. Not yet run in game.

| Finding | Cause | 0.71 |
| --- | --- | --- |
| The Client's log filled with `career could not follow the host's ... Case_EVGA_6/Left Lid ... isn't in this game's inventory` (about 330 errors). | A removed case panel waits in the inventory as `Case_SKU/Panel`; both games add their own, so the shared item lists disagreed. | Removed panels are not shared Career items; panel presence is the panel command's. |
| The delivery area's yellow box and other tutorial highlights stayed on the other game. | 0.69 and 0.70 cleaned up by guessing which visual was left (outline colours, stale outline entries); the leftovers are hidden by the step's own event handler (`NewStartOnboarding.OnDeliveryCollected`: a marker's `SetActive(false)` and a highlight's `enabled = false`). | Owner suggestion: when following the other player past the delivery or PC pickup step, the game's own handler runs here (its tutorial card stays with the player who did it). One second after each followed step the log names anything still drawn in the tutorial's yellow. |
| Refitting the PCI lock put both games out of sync (`manifest changed`, GPU `Heatsink/Screws/screwStandoff` resources gone, PCI lock screws added). | Open: fitting the lock appears to move the GPU's bracket screws into the lock on the Host, while the Client's screws differ again. | Not fixed. The mismatch now lists up to 24 resources each way. |

The 0.70 adapter fix worked: the Host's replay fitted the GPU's power adapter and both games agreed after the install.

## 0.70 fixes from the third in-game sweep run (18b)

**Status:** coded November 12, 2026 after the third Career run (both games ran 0.69; the Host did the first tutorial job). Release built with zero warnings or errors; all 937 automated checks passed. Not yet run in game.

| Finding | Cause | 0.70 |
| --- | --- | --- |
| The Client went out of sync after the Host's GPU install (`Carried PC topology mismatch ... missing [powerAdapterIDs[0]]`). | The game adds a GPU's power adapter in `Slot.InstantiatePreloadedPart` by starting a second install state on the installing player's game (not while a PC loads). A replay never enters that state, so the replaying game had no adapter. The 0.69 save refresh could not help. | After a replayed install settles, adapter slots with an automatic part are fitted the way a PC load fits them, with the case's adapter override (`[replay] ... fitted the power adapter`). |
| Flashing yellow outlines stayed in the air where the delivery box and the PC had been. | The game draws outlines from a static list (`CustomOutline.OutlineMeshDict`) that nothing clears when the object goes away; the co-op sync removed those objects. | Four times a second, outlines whose object is destroyed or hidden are dropped. |

The 0.69 note fix worked (the Client placed Uncle Tim's note and put it away when the Host read it), and the PCI covers waited for the Host's commit.

## 0.69 fixes from the second in-game sweep run (18b)

**Status:** coded November 10, 2026 after the second Career run on two games (Host Career, Client joined over LAN; the Client did the first tutorial job up to the GPU install). Release built with zero warnings or errors; all 937 automated checks passed. Not yet run in game.

What the run showed (logs of both games and the owner's report):

| Finding | Cause | 0.69 |
| --- | --- | --- |
| The Client went out of sync after the GPU install (`Carried PC topology mismatch ... extra [powerAdapterIDs[0]]`). | The game rebuilds `Case.powerAdapterSlots` when its own player installs a part (`Case.OnChangePart`), not when the other player's install is replayed. The Host replayed the Client's install and saved the PC without the GPU's power adapter, and the Host's record is the shared one. | In co-op every PC save (`SaveLoadSystem.SaveComputer`) first rebuilds the case's power adapter slots. |
| After the reload from the Host's record, the removed PCI lock came back fitted on the Client (`native assembly manifest changed ... native only [... PCI_Plate[7]/@panel/Screw ...]`). | The game's save keeps case panels as flags by position in the case's cached slot lists (`miscCaseSlots`, `optionalCaseSlots`); the PCI lock's flag didn't survive the round trip. | Panel presence is a shared fact: after a PC is loaded on either game, panels that differ from the shared world are removed or fitted to match (logged as `[panels] PC ... loaded with case panels that differ from the shared world`). |
| The PCI covers came off on the Host while the Client was still turning their screws (owner report). | Part removals and installs are replayed live on the other game; the replay's own screws are automatic. | Owner request (November 10, 2026), as for panels in 0.68: in Career the other game shows a part removal or install once the player doing it has committed it. Free Build keeps its live replay. |
| Yellow flashing outlines stayed on the other game after the PC and GPU pickups. | The outline is the game's `CustomOutline` in its action-tip colour, which outlives the action tip that 0.68 cleared. | Following a tutorial step also turns off every outline in the action-tip colour. |
| Uncle Tim's note was missing on the Client of a new Career until `/sync`, and stayed (with its yellow frame) on the Client after the Host read it. | The note belongs to the first tutorial's step code: it is placed on reload and put away once read, on the game whose player did the step. | Following the read-the-note step places the note if this game has none; following a later step puts it away. |

The 0.68 panel wait worked: the Host showed the side panel and the PCI lock coming off only once the Client had finished them.

### Runtime checks for 0.69

1. Host Career, Client joins at once. Both games show Uncle Tim's note. One player reads it: it goes away on both games.
2. One player picks up the tutorial PC and collects the GPU: no flashing outline stays on the other game.
3. One player removes the PCI covers, turning the screws slowly: the other game removes each cover only after the last screw is out.
4. One player installs the tutorial GPU: the other game shows it once the install is done, with no out-of-sync or `PC reloaded` notice. The other player's game installs it the same way when the roles are swapped.
5. Refit the PCI lock and the side panel and screw them in: no out-of-sync notice. If `native assembly manifest changed` appears, its `shared only` and `native only` lists go into the next fix.

## 0.68 fixes from the first in-game sweep run (18b)

**Status:** coded November 10, 2026 after the first Career run on two games (Host Career, Client joined over LAN, first tutorial job up to placing the PC under the desk). Release built with zero warnings or errors; all 937 automated checks passed. Not yet run in game.

What the run showed (logs of both games):

| Finding | Cause | 0.68 |
| --- | --- | --- |
| The Client reloaded the tutorial PC about once a second after the GPU install, until the game was closed (`Carried PC path mismatch`). | The GPU brought a power adapter (`PSU_ADAPTER_12_3X8PIN_45`). The game stores it in `ComputerSave.powerAdapterIDs` by its index in `Case.powerAdapterSlots`. `RefreshPowerAdapterSlots` takes the case's cached slots of that type, which can hold a destroyed slot, so the two games named the adapter by different indexes. | Destroyed slots are dropped from that list after the game builds it (logged as `[cables] ... dropped N destroyed power adapter slot(s)`). Adapters are named by their order among filled entries. A PC that still fails within 30 s of its reload from the host's record is reported out of sync once instead of reloading again. Both mismatch errors now name the paths or resources that differ. |
| The Host went out of sync after refitting the PCI lock (`native assembly manifest changed (124 native resources, 128 shared)`). | Not yet known: four shared resources were missing on the host's game after the Career refit with hand-turned screws. | The error now lists the missing and extra resources. Needs the next run. |
| The other game took the side panel off while the player removing it was still turning its screws (owner report). | A panel change was one host-ordered step: the other game showed it as soon as the host accepted the request. | Owner decision (November 10, 2026): the other game waits. The request holds the whole PC for the requester; when the requester's game shows the panel fully off or fully on and its gesture has ended, it sends a finish (`CasePanelWorld.Finish`). If the gesture stops with the panel as it was, the finish takes the change back. If the requester leaves first, the other game shows the shared world's panel. Saves and snapshots wait while a panel change runs. |
| Yellow outlines stayed on whichever game followed a tutorial step the other player did (the PC box's outline after it was picked up, the workbench). | The tutorial sync set the step directly; the step's own end, which turns its outlines off, ran on the other game only. | Following a step clears the tutorial's action tips and their outlines, the workbench highlights, case outlines and the delivery box highlight. A remote pickup turns the case's outline off before the case leaves this game. |
| The Client's tutorial failed entering work mode (`NewStartOnboarding.RemoveSidePanel`, null reference). | The step looked up a side panel the Host had already removed. | In co-op the tutorial's PC steps (`RemoveSidePanel`, `RemovePCILock`, `RemovePCICovers`, `InstallGPU`, `InstallPCILock`, `InstallSidePanel`, `CableGPU`) skip with one warning when they fail. |
| The startup log listed no Career hooks. | Wording only. | The hooks line names them. |

The "All done" message after the first job was correct (owner, November 10).

### Runtime checks for 0.68

1. Host Career, Client joins. One player removes the side panel and turns its screws slowly: the other game keeps the panel until the last screw is out and the panel is off, then removes it. The same for the refit.
2. Start removing a panel, then leave the screw step with the panel still on: both games keep the panel on and either player can work on the PC again.
3. Pick up the tutorial PC box on one game: the other game shows no outline where it stood, and no workbench highlight stays after the drop.
4. Install the tutorial GPU: no `Carried PC path mismatch` and no repeated `PC reloaded` notices. Look for `[cables] ... dropped ... power adapter slot(s)` in either log.
5. Refit the PCI lock and screw it in: no out-of-sync notice. If `native assembly manifest changed` appears, its `shared only` and `native only` lists go into the next fix.

## Career factor outcomes (18a)

Status words: **coded** (shared in the named version, not yet run in game), **host** (only the host's game runs it; the guest takes the result through updates or the day reload), **local** (stays on each game by decision), **outside scope** (not part of the first supported Career) and **unverified** (needs runtime evidence). The C-numbers are the [Career checklist](../career-testing.md) cases for the in-game sweep.

| Factors | Outcome | Evidence and checklist |
| --- | --- | --- |
| CP-001 | Coded 0.58: Host Career, new Career in the starting shop, mode-aware Continue and Load. New Game Plus and hard mode are refused with a reason. | [Phase 15](phase-15.md#058-implementation-mode-aware-host-join-and-saves-15b-and-the-two-host-rows-15d). C15-01–03 |
| CP-002, 063 | Coded 0.63: either player accepts, collects, quits or turns down a job; the host's game runs it and one result wins. Discard and permanent delete of old jobs 0.66. | [Phase 17b](phase-17.md#063-implementation-shared-jobs-and-the-inbox-17b), 18a fault tests. C17-01, C17-03 |
| CP-003, 062 | Coded 0.64: each game checks objectives; the host's result decides and the guest's Collect waits for it. | [Phase 17c](phase-17.md#064-implementation-objectives-and-cleaning-17c). C17-02 |
| CP-004 | Coded 0.63: acting on one part of a multi-job holds the whole multi-job. Deadlines move at the host's day end. | Job model tests. C17-01, C17-04 |
| CP-005, 039, 064 | Host: the host's game settles the job once; cash, kudos, review and records reach the guest as career and inbox updates (0.59, 0.63). | Career and job model tests. C17-03 |
| CP-006, 040, 067 | Host: job generation, story selection, generated PCs and daily caps run on the host's game; jobs reach the guest with the inbox update (0.63) and game-made PCs are adopted (0.62). | C17-01 |
| CP-007 | Coded 0.59: every owned item has a shared ID; installs and new PCs name the exact item. | Career model tests. C15-06 |
| CP-008 | Coded 0.62: PCs the host's game makes (PC Bay, job PCs, tutorials) are adopted; PC moves use the Free Build paths. | [Phase 16d](phase-16.md#062-implementation-pc-bay-shares-shop-front-prices-and-game-made-pcs-16d). C15-06, C16-05 |
| CP-009, 011 | Coded 0.61: delivery box, packages and job notes shared; a departed carrier's package returns to a bench. Scheduled deliveries come from the host's calendar. | [Phase 16c](phase-16.md#061-implementation-deliveries-packages-and-job-notes-16c), 18a fault tests. C16-03 |
| CP-010, 043–046, 049, 050 | Coded 0.60: shared carts per shop; the host's game runs the checkout once against the cart revision, total and delivery choice the guest saw. Cables buy through the PC's Cable app. | [Phase 16b](phase-16.md#060-implementation-shared-carts-checkout-and-part-sales-16b), 18a fault tests. C16-01, C16-02 |
| CP-012, 030, 031 | Coded 0.59: cash and the ledger's length and hash are shared. *Partial:* a ledger entry's later `m_collected` change isn't shared. | [Phase 15c](phase-15.md#059-implementation-shared-career-facts-and-owned-items-15c). C16-01 |
| CP-013, 037, 038, 041 | Coded 0.59: kudos and unlock flags are shared career facts; each game works out the level from kudos. | Career model tests |
| CP-014 | Coded 0.59 (rating) and 0.63 (reviews in the inbox update). | C17-03 |
| CP-015, 052 | Coded 0.62: PC Bay offers by ID; a stale or sold offer is refused. The host's PC Bay app must have been opened once. | C16-05 |
| CP-016, 053, 054, 068 | Coded 0.62 for listing prices. Host: sale rolls, valuation and settlement run at the host's day end; the guest sees them after the reload. | C16-05 |
| CP-017, 047, 048 | Host and unverified: prices and stock come from the host's game; the checkout compares the shared cart's total, not each catalog price. Both games show the same prices within a day. | C16-02 |
| CP-018, 066 | Coded 0.65: End Day needs both players ready. Host: the day change runs on the host's game and the guest reloads the host's world. | [Phase 17d](phase-17.md#065-implementation-end-day-bills-and-the-renovation-17d). C17-04 |
| CP-019 | Coded 0.63 and 0.66: the host's inbox follows on the guest; read marks, flags, tags, deletes and restores are shared. | [Phase 17e](phase-17.md#066-implementation-inbox-tutorials-and-achievements-17e). C17-06 |
| CP-020 | Coded 0.66: tutorial state and step shared, first move wins. Seen pop-ups and prompts stay local. | C17-05 |
| CP-021, 061 | Coded 0.66: achievements stay with the player who acted; business milestones count on both accounts. | C17-07 |
| CP-022 | Passed in 0.57 for Free Build. Regression run in 18b. | [Free Build checklist](../needs_testing.md). C18-04 |
| CP-023 | Outside scope: Endless and binning challenges have no co-op menu entry and no mode value. | Plan "First supported Career" |
| CP-024, 056 | Coded 0.58: the game's own autosaves are skipped on both games; the host saves on its interval, Exit, the Save screen, /sync and after a finished day. 0.67: a save waits for the client's running job actions. | C15-04, C18-02 |
| CP-025, 057 | Coded: the sidecar binds the native save's SHA-256 to the co-op state; a newer, older or mismatched pair is refused with a reason and its backup is offered. | Persistence and campaign model tests. C15-05, C18-02 |
| CP-026, 055, 058 | Coded: a joining guest holds world changes until it has loaded the host's snapshot of one revision; the host restores its pair at the start of a generation. Rejoin and stale requests in the 18a fault tests. | C15-04, C18-01 |
| CP-027, 069 | Unverified: PC files import through the Free Build shared path (phase 12). Whether Career offers import and whether its parts become owned items isn't checked. Export writes local files only. | C18-05 |
| CP-028 | Coded: the handshake refuses a different mod or game build and warns about DLC or catalog differences (phase 1). | C15-05 |
| CP-029 | Local: the Notes app isn't saved by the game. The workshop's job notes are shared (0.61). | C17-06 |
| CP-032, 035 | Host and unverified: overdraft and bank charges come from the host's game at payment and at the day end. No loan action was traced. | C17-04 |
| CP-033, 034 | Coded 0.65: either player pays rent and utility bills; the host's game runs the payment once. | C17-04 |
| CP-036 | Coded 0.62: either player buys shares; the host's game buys. | C16-05 |
| CP-042 | Coded 0.65: the renovation runs as a host world change and both games load the renovated shop. 0.67: an interrupted renovation's save binds afresh on Continue. | C17-08 |
| CP-051 | Unverified: no refund or return path was found reachable from the shop UI; nothing is shared for it. | C16-04 |
| CP-059 | Coded 0.58: separate Free Build and Career folders; the guest never saves the host's world and keeps joined copies apart. | C15-02 |
| CP-060 | Local and unverified: a Steam upload runs only on the game where it was pressed; whether a mirrored desktop click can reach it isn't checked. | C17-07 |
| CP-065 | Host: expiry and missed jobs change at the host's day end; the guest takes them with the reload. | C17-04 |
| CP-070 | Local: the theme pulse is a personal preference (owner, November 8). | |

# Phase 17 audit: jobs, day flow, onboarding and communications

**Evidence:** static review of 0.57 mod source, the [Career register](career-economy-and-persistence.md), and selected game traces. No new gameplay results. Phases 15–16 provide mode, ownership, inventory, delivery and persistence contracts.

| Factors | Evidence | Command, state or projection boundary |
| --- | --- | --- |
| CP-002, 004–006, 039–041, 063–067 | `CareerStatus.OnAccept(Job)`, `OnCollect(Job)`, `OnCollect(MultiJob)`, job and multijob positive actions, completion and expiry. Generation uses story selections and randomized jobs/PC inputs. [Accept trace](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CareerStatus.txt#L20406) · [Job collection](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CareerStatus.txt#L17346) · [Multijob collection](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CareerStatus.txt#L18070) | The host publishes generated jobs and stable parent/child, customer PC, package and note references. Either player can accept, act on or collect an eligible job. Resolve decline, quit, failure, expiry, finish and collect against expected state. Settle rewards, cash, XP, reviews, progression and released custody once against the job result. |
| CP-003–005, 062–065, HW and SW objective inputs | `Job` and `MultiJob` expose `UpdateCompletion`, `CheckExpiry` and `IsSuccess`. The broad audit inventories 52 declared `Objective` subclasses. [Objective inventory](career-economy-and-persistence.md) | Map each shipped objective template to its authoritative trigger and consumed facts, including hidden/optional objectives and multi-job aggregation. Reuse accepted PC edits, boot/software, benchmark/simulation, customization, tool and spend events. Evaluate outcome-affecting conditions on the host after their inputs settle. Publish native result/cache invalidation where clients need it. Record unreached classes separately. |
| CP-012, 018, 033–036, 065–067, HW-017, PW-034 | `CareerStatus.AdvanceDay` calls `Calendar.FinishDay`, `WorkshopController.DoDustAccumulation` and `GenerateTodaysOffers` in the native iterator. Phase 9 traced `WorkingOnComputerState.VacuumTick` to saved per-part filth through `Case.UpdateDustInSave`. [Day iterator](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CareerStatus_NestedType__AdvanceDay_d__185.txt#L1098) · [Cleaning audit](phase-9.md) | Only the host ends the day. Wait for accepted edits, checkout, delivery and collection to settle, then hold new conflicting work and run the transition once. Publish the next date and due bills, charges, expiry, deliveries, offers, jobs, email and dust together at a safe boundary. Use per-part filth for cleaning and accumulation. Derive dust visuals locally. Give saves and admissions a stable pre-day or post-day state. |
| CP-028, 042 | `CareerStatus.UpgradeShop` removes a day-end post-save callback and has a path through `CreateSaveGame`, `BlockForPendingSaves` and `LoadGameFromDir` targeting `Stage_02_Workshop`. The native load path also recognizes `Stage_02_Workshop_Shop_Rundown`. [Renovation trace](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CareerStatus.txt#L33344) · [Scene selection](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/LevelLoadPersistency.txt#L931) | The host controls the supported native shop renovation. Order the upgrade, payment and schedule against the campaign. Hold guest admission while the native save and scene change run. Advance to a new generation of the same campaign, rebind the workshop and let the guest load the host's renovated state. Retain a whole pre-upgrade pair until the new one can be verified. |
| CP-019–021, 029, 061, 070 | `CareerStatus.RegisterEmails`, `EmailApp.OnClickFlag`, tag and delete paths, `NotesAppNote`, `PostItSlot`, `Tutorial`, `OnboardingState` and `Achievements` supply persisted or local state. [Email flag trace](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/EmailApp.txt#L5906) · [Career register](career-economy-and-persistence.md) | Sync saved inbox messages, flags/tags and notes, including physical placement and job references. Return a departed carrier's physical note to shared staging with the same identity. Preserve personal scroll, selection and focus. Either player may complete an eligible shared onboarding step. Prompts and cameras are local, and the host owns native skips and scene transitions. Steam achievements and external uploads are personal account actions. Projections may update game state without invoking the other account's write APIs. |

Resolve exact end-day input callbacks, coroutine yield/commit boundaries, objective invalidation, note transfer callbacks, onboarding gates and account side effects against the current game build. Use source-backed cases for build, repair, cleaning, diagnostics, benchmark, software, tuning, budget, deadline and multi-job first, then expand the [test matrix](../career-testing.md#17-jobs-day-and-communications) from shipped objective data and runtime evidence.

## Owner decisions for phase 17 (November 9, 2026)

The owner answered the phase start questions. They are also in the [plan](../../plan.md#career-decisions-confirmed-november-8-2026):

- **Steps:** one version per step, stopping after each for review, code and model tests only. 0.63 is 17b: jobs plus the live inbox, job lists and calendar for the guest (job offers are inbox e-mails). 0.64 is 17c (objectives and cleaning), 0.65 is 17d (End Day and the shop renovation) and 0.66 is 17e (e-mail flags and tags, notes, onboarding and account effects).
- **Day change:** the guest keeps reloading the host's world after the host's day ends. 17d adds the settle gate, the ready check and the renovation, not a live day projection.
- **End Day:** the day ends only when both players are ready. The guest's End Day or Skip Work marks it ready and tells the host; the host's End Day waits with a notice.
- **Cleaning:** shared as a result, like thermal paste. The PC is reserved while one player vacuums.

## Job callback trace (17b)

Read from the game binary's disassembly; the ISIL dump truncates these methods. Field offsets are from the structural C# files.

| Path | What the game does | Consequence for co-op |
| --- | --- | --- |
| Inbox buttons | `IEmail.OnPositiveAction` / `OnNegativeAction` on a `Job` switch on `m_status` (0xEC). | Not hooked on the acting game. A click mirrored from a shared PC's desktop runs neither button on the other game. |
| Accept | `OnPositiveAction` (NEW/READ) calls `CanAccept()` (star rating and XP only), then the virtual `OnAccept()`. `Job.OnAccept` refuses a BUILD or PACKAGE job with a message box when `WorkshopController.AnyPostItSlotsFree()` is false. It then runs `Tutorial.OnAcceptJob` and tutorial pop-ups, and either sets IN_TRANSIT, `CareerStatus.OnAccept` and `Calendar.ScheduleJobDelivery`, or sets ACCEPTED, `CareerStatus.OnAccept` and `SpawnJobPostItAtAnySlot`. Last come objective `OnAccept`s and `GenerateJobTagForJob`. | The host runs `job.OnAccept()` for the guest after checking `CanAccept` and the free note slot. Its note and job tag reach the guest through 16c and 16b. |
| Collect | `OnPositiveAction` (FINISHED) drops objectives whose `m_type` is RETURN_PC (a multi-job does it on each part), shows `ShowResult()` (the invoice screen), then calls the virtual `OnCollected()` (vtable slot 0x388). `Job.OnCollected` sets COLLECTED and calls `CareerStatus.OnCollect`: cash, XP, review, `RemoveJob`, achievements, job tag and story lists. | The guest's game shows its own result screen; its `OnCollected` is sent to the host. The host drops the same objectives and runs `OnCollected`, with no result screen. |
| Quit | `OnNegativeAction` (accepted) runs `OnQuit`, whose `QuitJob` coroutine loads the PC's parts and computes its value. The confirmation's Yes (`<QuitJob>b__2`) charges `m_initialValue − Value` with `SpendCash(fee, false, UNKNOWN)` and leads to `DoQuit`. `DoQuit` sets `m_reviewDay` and REJECTED, calls `ShowResult()` and then `CareerStatus.OnQuit` (review and `RemoveJob`). | The guest's Yes sends the PC value its confirmation showed. The host charges the same fee and runs `DoQuit` with the result screen suppressed. A quit without a fee sends the initial value (no fee). |
| Turn down | The virtual `OnRejected()` calls `CareerStatus.OnReject` and sets REJECTED. No UI. | The host runs `OnRejected()` for the guest. |
| Job removal | `CareerStatus.RemoveJob` calls `SaveLoadSystem.RemoveJobItem`. That destroys the job's bench PC (`BenchSlot.DestroyComputer`), its packages and its stored PC, and clears its note slots. | The host compares its shared bench and storage PCs before and after, and shares each removed one (`PcRemoval.Game`). Packages and notes follow its benches (16c). |
| "Work on it" | `OnPositiveAction` (ACCEPTED) on a stored job PC calls `SaveLoadSystem.PickupComputerFromStorage` directly, then moves the player to the workbench. | In a Career, that pickup of a shared PC outside an accepted change becomes the shared retrieval. |
| Progress | `Job.UpdateCompletion` evaluates objectives against the job's PC or package and moves ACCEPTED↔FINISHED or EXPIRED. Its only side effects are sounds and achievements. | Each game may evaluate its own copy. The host's next update overwrites the guest's. 17c checks the objective inputs. |

## 0.63 implementation: shared jobs and the inbox (17b)

**Status:** coded with model tests on November 9, 2026. Release built with zero warnings or errors and all 852 automated checks passed. No game was launched. Nothing here is verified in the running game.

| Area | What 0.63 does |
| --- | --- |
| Model | Command 21 `JobWorld`, with the optional world state `jobs`. It holds each job's ID, status and multi-job, which the host binds at generation start. Host-only **Update**s (chunked, SHA-256 checked) carry the new job facts and the game's own lists. An **Act** (accept, collect, quit, turn down) is refused for an unknown job, a status the game's button doesn't allow, or a job (or any part of its multi-job) already acted on since the host's last update. One result wins a race. |
| Host inbox | Once a second the host builds a summary of the jobs (status, flags, objectives), e-mails, reviews, tags, calendar events, job tags and counters. When it differs, the host serializes 22 `CareerStatus.State` lists together with the game's serializer, so shared references stay shared, then compresses and sends them. Sharing pauses while the host's day changes. |
| Guest inbox | The guest's game replaces those lists (and the job counters), marks the calendar dirty, rebuilds open inboxes and puts the shared job baskets back on the new job tags. |
| Guest actions | Prefixes on `Job`/`MultiJob` `OnAccept`, `OnCollected`, `DoQuit` and `OnRejected`, and on the quit confirmation, send the action and skip the native call. The host checks its live job (status, `CanAccept`, a free note slot, the job's PC not carried or claimed). On the next frame its game runs the action without the result screen or tutorial pop-ups (`HostRunning`), and the result is shared at once. |
| Host's own actions | Run natively, as before. A collect, quit or turn-down waits with a notice while the job's PC is carried or claimed, or while the guest is handling that job. |
| Job PCs | `PcRemoval.Game` (host only, bench or storage): the host's game removed a shared PC by itself. The guest removes it from its bench or storage. A job's "work on it" pickup from storage goes through the shared retrieval. |

### Not covered by 0.63

- The host's game runs the guest's accept, so `Tutorial.OnAcceptJob`, achievements from collected jobs and other account effects happen on the host's account (17e). Only the immediate tutorial pop-ups are suppressed.
- The guest's e-mail read, flag, tag and delete changes stay on its game until the host's next inbox update replaces them (17e). Its own onboarding e-mails are replaced the same way.
- Objective inputs (cleaning is still refused; benchmark and boot status live on the PC record) are 17c. Day changes, expiry and End Day stay host-only (17d); the guest still reloads after the day.
- The inbox is sent whole after each change. Its size in a long career is a runtime check.

### Runtime checks for the gameplay sweep

1. The host's game serializes and the guest's game deserializes the 22-slot graph (`Il2CppReferenceArray<Object>` through `BinaryFormatter`). The guest's inbox, job tracker, calendar and reviews show the host's.
2. Inbox update size and time on a fresh career and after several days. The log line is `[jobs] shared N job(s) and the inbox (K KB)`.
3. A guest accept: the host's game accepts without a pop-up, the note or delivery appears on both games, and the guest's notice says "Job accepted."
4. A guest collect: the guest's result screen shows, the host pays once (cash, XP and ledger follow on the guest), and the job's PC leaves both games (`PcRemoval.Game`).
5. A guest quit with a fee charges the fee the confirmation showed on the host, once. A quit without a fee charges nothing.
6. A guest turning a job down: it leaves both inboxes.
7. Both players collecting one job at the same moment pay once, and the second sees a notice.
8. "Work on it" on a stored job PC moves the PC into the player's hands through the shared retrieval, on both games.
9. A mirrored inbox click on a shared PC's desktop opens nothing on the other game.
10. Startup logs no `[hooks]` error for the 16 new job hooks.

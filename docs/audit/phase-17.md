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
- Objective inputs and cleaning are 17c ([0.64](#064-implementation-objectives-and-cleaning-17c)). Day changes, expiry and End Day stay host-only (17d); the guest still reloads after the day.
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

## Owner decisions for 17c (November 9, 2026)

- **Objective checks:** each game keeps checking a job's objectives itself, so the guest sees ticks and hears the sounds at once. The host's result still decides: its inbox update replaces the guest's, and the guest's Collect waits with a notice while the host's game doesn't show the job finished.
- **Cleaning everywhere:** shared cleaning works in Free Build and Career. The vacuum is no longer refused in Free Build.

## Objective trace (17c)

Each game checks the objectives of the jobs whose PCs stand on its own benches, every frame: `BenchSlot.Update` → `UpdateStatus` → `JobStatus.UpdateJob` → the virtual `Job.UpdateCompletion` (class slot 42, through the vtable, so it doesn't show by name in ISIL). It finds the job's PC with `SaveLoadSystem.GetComputerForJob` (or the package with `GetPackageForJob`), runs `ComputerSave.CheckStatus`, then `Objective.UpdateSatisifiedBy` on each objective, and moves the job between ACCEPTED and FINISHED. A multi-job does the same for each part. So an objective agrees on both games once its input agrees.

| Objective types | What the check reads | Shared by |
| --- | --- | --- |
| Parts, upgrades, replacements, RAM, storage, multi-GPU, brands, case, lights, AIO, custom water cooling, coolant, loop colour, cables, custom cables, all new parts, faulty parts | The PC record's parts, cables and water loop | Component, cable and water edits (phases 4–5, 10) |
| Boot to OS, install software, clone OS, delete junk files, run scan | `ComputerCareerStatus.bootedToOS` and the PC's software; a scan checks the boot drive's software flag `m_needsScan` (through `GetBiosConfig`) | Mirrored desktop input and software sync (phase 6) |
| Run benchmark, 3DMark and Cinebench scores | `ran3DMarkBenchMark`, `last3DMarkBenchMark` and the score functions | Mirrored apps; the host's result is copied (phase 6) |
| Stable, case temperature, no thermal throttle, lower noise | Simulated failure times and temperatures | The host's simulation decides (phase 7) |
| Overclock, voltages, RAM timings, fan mode | The PC's BIOS settings | Host-ordered BIOS Apply (phase 6) |
| Thermal paste, delid | Pastes and the delid flag | Phase 9 |
| Paint, stickers, theme, customise on glass | The saved case design (`CustomCaseManager`) | Case modding result (phase 11) |
| Spend less than, meet budget | The PC's parts (`GetInvoiceValue`), not a running total | As parts |
| Time limit | The calendar day | The host's calendar (17b) |
| Procure parts | The job's package (`PackageCareerStatus`) | Packages (16c) |
| Open case | `ComputerSave.miscCaseSlots` (the saved case panels) | Case access (phase 4) |
| Diagnose | Whether the hidden repair objectives (replace part, run scan, clean, paste, faulty part; `IsHiddenByDiagnose`) are met | As those objectives |
| **Clean** | `CleanObjective.TestSatisifiedBy` → `RecalculateDustValues` → `ComputerSave.GetCurrentDust`/`GetTotalDust` | **New in 0.64** |
| Return PC | Removed by Collect (17b) | — |

The only objective input that wasn't shared was dust. Spend-type objectives keep no running total of their own: `SpendLessThanObjective.m_spent` is a cache that `GetInvoiceValue` recomputes from the PC's parts at each check (and `FixForVersion` for old saves).

## 0.64 implementation: objectives and cleaning (17c)

**Status:** coded with model tests on November 9, 2026. Release built with zero warnings or errors and all 860 automated checks passed. No game was launched. Nothing here is verified in the running game.

| Area | What 0.64 does |
| --- | --- |
| Cleaning model | Command 9 gesture `Clean` (whole-PC claim, part "clean"). Its commit carries each dusty part's `DustFact` (its dust component's place in the case and the part's `m_filth`) and the PC's two saved dust totals. A result without a part, with a part listed twice, or with a negative, infinite or oversized amount is refused. The other player sees "The other player is cleaning that PC." |
| The vacuum | `WorkingOnComputerState.VacuumTick` runs each frame in the cleaning mode. Its first tick on a shared PC asks the host; this player's vacuum works at once. A refusal puts the dust back and tries again after two seconds, with a notice at most every five. When the player leaves the cleaning mode, the editor waits a second, then sends the dust, or cancels if nothing changed. |
| The other game | Sets each changed part's dust (`PartInstance.SetFilth`), rebuilds its dust picture (`ComponentDust.RefreshDust`, a random pattern from the amount, as after a load) and sets the totals (`ComputerSave.SetDust`), which a cleaning objective reads. |
| Free Build | Cleaning works there too (owner decision). The 0.22 refusal is gone. |
| Collect | Each game keeps checking objectives (owner decision). A guest's Collect on a job its game shows finished, but the host's last update doesn't, shows a notice instead of the invoice screen. The host refuses such a collect with "The host's game doesn't show that job as finished yet". |
| Status | The other player's status reads "Cleaning" while they vacuum. |

### Not covered by 0.64

- Dust build-up when a day passes (`WorkshopController.DoDustAccumulation` in `CareerStatus.AdvanceDay`) runs on the host's game. The guest takes it with the day-end reload until 17d.
- Dust strokes and particles stay on the cleaning player's game. The other game's dust picture is a fresh pattern for the same amount.
- Objective sounds and objective achievements play on each game that sees the objective met. The host's game also plays them for the guest's work (17e looks at account effects).
- Expiry and deadlines change at the day end (17d).

### Runtime checks for the gameplay sweep

1. Vacuum a dusty PC as the guest: the host can't start a change on that PC meanwhile, and its status line says "Cleaning". After the guest leaves the cleaning mode, the host's PC shows the same dust amount (the picture may differ) and its cleaning objective ticks.
2. The same as the host.
3. Both players start the vacuum on one PC: the second sees "The other player is cleaning that PC." and its vacuum does nothing.
4. Enter the cleaning mode and leave without vacuuming: nothing is sent (log `[clean] edit … changed no dust; cancelling`).
5. Cleaning in Free Build on a PC that came with dust.
6. Save and rejoin after cleaning: both games keep the cleaned dust.
7. A job whose objectives the guest's game shows met but the host's doesn't: the guest's Collect shows the notice, not the invoice.
8. Build, boot, benchmark, BIOS and software objectives the guest completes tick on the host's game too (spot-check two or three job types).
9. Startup logs no `[hooks]` error.

## Owner decisions for 17d (November 10, 2026)

- **New change un-readies:** once Client presses End Day or Skip Work it is ready, until it starts something new (an edit, a PC or package moved, a tool table, decorating, a purchase, a job or e-mail action), leaves or rejoins. It then presses End Day again.
- **Host presses again:** the host's End Day or Skip Work is refused with a notice naming Client while Client isn't ready. When Client becomes ready the host is told and presses End Day again, through the game's own warnings and door walk.
- **Renovation by either player:** either player may accept the shop renovation e-mail. Client's accept is sent to the host, whose game pays and schedules it. The renovation itself runs on the host at the day end.
- **Bills by either player:** either player may pay a rent or utility bill. Client's Pay is sent to the host, whose game pays it once.

## Day and renovation trace (17d)

Read from the game binary's disassembly with `tools/decompile/trace_rva.py` and `metadata_slots.py`.

| Path | What the game does | Consequence for co-op |
| --- | --- | --- |
| End Day | `DaySummary.OnEndDay` (78C420) warns about an unbought basket or uncollected jobs, then its Yes (`<OnEndDay>b__29_0`) runs `EndDay`, which plays the door sound and calls `AdvanceDay(false)`. | Hooked at the button. The guest's marks it ready. The host's is refused early while it must wait. `AdvanceDay` keeps its prefix as a backstop. |
| Skip Work | `DaySummary.OnSkipWork` (78C680) is a tail jump to `AdvanceDay(true)`. | Hooked like End Day. |
| Day iterator | `CareerStatus.AdvanceDay` runs `Calendar.FinishDay`, `DoDustAccumulation` and `GenerateTodaysOffers`. It invokes `s_onDayEnd`, then `AutoSave` (skipped in co-op), then `s_onDayEndPostSave`. | The post-save callbacks still run in co-op. |
| Renovation offer | `ShopUpgradeEmail.OnPositiveAction` (677190) on a deleted e-mail only restores it. Otherwise it calls `SpendCash(m_cost)`, sets `scheduledShopUpgrade` and `m_paidForShopUpgrade`, adds `DoBuild` to `s_onDayEnd` and tells the onboarding (`OnShopUpgradeQueued`). | Either player, through the host (`JobWorld` step Mail). |
| Build | `ShopUpgradeEmail.DoBuild` sets `m_upgradedShop` and adds `CareerStatus.UpgradeShop` to `s_onDayEndPostSave`. | Before 0.65, co-op refused only `UpgradeShop`. The career was left marked upgraded in the old shop. |
| Renovation | `CareerStatus.UpgradeShop` (D29670) removes itself from the post-save list and adds `PostShopRennovation` to `SceneManager.sceneLoaded`. It updates the apps on the bench PCs and reschedules the rent. It then saves with `CreateSaveGame("", true, "")` and `BlockForPendingSaves`, and calls `LoadGameFromDir(save, "Stage_02_Workshop")`. | The host runs it as a world change (below). |
| After the load | `PostShopRennovation` removes itself and calls `PeripheralSlot.TrySpawnPerif` on every peripheral slot (the new workstations). | It waits for the workshop scene, because the co-op reload passes the main menu first. |
| Bills | `EmailBill.OnPositiveAction` (E40780) on a deleted e-mail only restores it. Otherwise it calls `PayRent` or `PayUtility` with `m_cost` (`SpendCash`, plus a thank-you e-mail) and removes the e-mail. | Either player, through the host. |

## 0.65 implementation: End Day, bills and the renovation (17d)

**Status:** coded with model tests on November 10, 2026. Release built with zero warnings or errors and all 890 automated checks passed. No game was launched. Nothing here is verified in the running game.

| Area | What 0.65 does |
| --- | --- |
| Ready model | Command 22 `DayWorld`. The guest's End Day or Skip Work sends Ready with the day its game shows. The host's game refuses it for another day, or while the guest has a claim or an edit running, carries a PC, a package or a note, uses a tool table or decorates. The mark is for that day only, and both games see it. |
| Taking it back | `DayWorld` sees every applied change in host order (`WorldSync.Observe`). The guest's own changes take the mark back, and so does its leaving or rejoining. Automatic requests don't count: missing pictures, repairs, results sent at the end of a change, desktop input and the lighting clock. |
| Host End Day | `EndDayBlocker` waits for edits, replays, tool tables, job and e-mail actions still running, a sync, Client still loading, and a ready Client. The host's press asks Client in chat (at most every 15 seconds) and shows "Waiting for Client to end the day. Press End Day again once Client is ready." When Client is ready, the host's chat and notice say so. A Client that isn't in the world isn't waited for. |
| Bills and the renovation offer | `JobWorld` step Mail names an unpaid bill or the renovation offer by its kind, the game's creation time stamp and its cost. The host's inbox update lists them. One action per e-mail until the next update. The host's game runs the e-mail's own button. If the money doesn't change hands, Client is told in chat and can try again. |
| Renovation | The host's game holds `UpgradeShop` when the day end queues it, and saves wait meanwhile. Once the day settles, it runs like /sync. Client leaves to its menu. `UpgradeShop` saves to the world's autosave, paired with the renovated scene, and its own load is skipped. The host returns to its menu and loads that save in `Stage_02_Workshop`. That load binds the shared IDs afresh from the game, as a new career does, because the bench keys include each bench's place in the scene. The host then saves the renovated world, and Client is admitted from a fresh snapshot. |
| Save format | 0.65 reads 0.16–0.65 saves. The job state gains an optional e-mail list and the day state is optional, so 0.63 and 0.64 careers load. |

### Not covered by 0.65

- The day change itself stays with the host's game. Client still takes the new day, dust build-up, expiry and new offers by reloading the host's world (phase 15c).
- The renovation's fresh binding drops co-op facts tied to the old shared IDs. Mainly that is the custom desktop pictures shared before the renovation: Client sees them again once the host sets them again. The lights and seasonal decoration are read from the host's game again.
- If the host's game stops between the renovation's save and the first save in the renovated workshop, Continue may refuse that save, because its pairing belongs to the old shop. The backup of the save from before the renovation is kept.
- The onboarding steps of the renovation (`OnShopUpgradeQueued`) and of bills run on the host's game only (17e).
- On a deleted bill or renovation e-mail, the button only restores it, and only on that game (17e: e-mail flags, tags and deletes).

### Runtime checks for the gameplay sweep

1. The host presses End Day while Client works: the press is refused with the "Waiting for Client" notice, and Client's chat asks it to end the day.
2. Client presses End Day: both chats say Client is ready, and Client's tablet doesn't walk out the door. The host presses End Day: the day ends, and Client reloads for the new day.
3. Client presses End Day, then picks up a PC or starts an edit: Client's notice says it's no longer ready, and the host's End Day waits again.
4. Client presses End Day while carrying a PC: refused with "put down the PC you're carrying first".
5. Skip Work on either side behaves as End Day.
6. Client pays a rent bill: the host's cash drops once, and both inboxes lose the bill. Both players press Pay together: it's paid once.
7. Client accepts the renovation e-mail: the host's chat says so, and the cash drops on both games.
8. End that day:
   - Client leaves to its menu.
   - The host saves, returns to its menu and loads the renovated workshop (log `[career] host loads … after the shop renovation`).
   - The new workstations get their peripherals (log `the game sets up its workstations`).
   - Client then joins the renovated workshop.
9. Save, exit and Continue the renovated career on both games: the host loads `Stage_02_Workshop` with the PCs, storage and cash it had.
10. Startup logs no `[hooks]` error for the five new hooks.

## Owner decisions for 17e (November 10, 2026)

The Career decisions of November 8 already said: saved inbox flags, tags and notes are shared, while selection, scroll and focus stay local; achievements belong to the account doing the action, and replication never writes to the other player's account. On November 10 the owner added:

- **Read marks shared:** when either player opens an e-mail, it is read for both.
- **Tutorials together:** the host and Client progress through the tutorials together. Whoever completes a step, it counts for both.
- **Milestones for both:** business milestones (cash, level, star rating, jobs completed) count on both accounts. Action achievements go only to the player who did the action, and the host's game never earns for Client's actions.

## Inbox, tutorial and achievement trace (17e)

| Path | What the game does | Consequence for co-op |
| --- | --- | --- |
| E-mail buttons | `Email.OnFlagAction`/`OnRemoveFlagAction` call `CareerStatus.FlagEmailMessage`/`UnflagEmailMessage` and set `m_flagged`. `Remove` calls `RemoveEmailMessage` (into Deleted). `OnNegativeAction` on a deleted e-mail calls `RemoveEmailMessagePermanent`. `Restore` calls `AddEmailMessage`. `AddTag`/`RemoveTag` edit the e-mail's `m_tags`. | All of these change lists the 0.63 inbox update already carries. The acting game sends the action; the host's game repeats it. |
| Read | `Email.OnRead` shares its code with four other methods (731CC0). `Job.OnRead` sets NEW to READ. | E-mails are seen after `EmailApp.OnClickRow`; jobs at `Job.OnRead`. |
| Job deletes | `Job.OnNegativeAction` switches on the status: READ calls `OnRejected` (slot 36), ACCEPTED and EXPIRED call `OnQuit` (38), COLLECTED and REJECTED call `DoDiscard` (40), DELETED calls `DoRemove` (41). | `DoDiscard` and `DoRemove` are new shared actions. The others have been job actions since 0.63. |
| Tags | `EmailApp.GenerateTag` calls `CareerStatus.AddTag(name, colour, core)`. | A new tag is a shared action. |
| Notes app | `NotesAppCloud` keeps its notes in memory; `SetDataFromSaveGame` has no callers, so the notes are never saved. | Left on each game. The shared notes are the physical job notes (0.61). |
| Tutorials | `JobTutorialHintUi.onboardingClasses` (Career) and `freebuildOnboardingClasses` hold every tutorial. Each has a state (`State` +0x10, set by `SetOnboardingActive`/`Complete`/`Inactive`, which only notify empty observers) and a `progress` enum, read and written by reflection (`getOnboardingProgress`, `GetEnumProperty`). Steps move in each type's event methods (for example `FirstDayProgressionOnboardings.SetFirstDayProgress` sets the step and shows the action-bar prompt). Button blocks (`AllowExitButton`, `AllowPowerButton`) read the step. | Shared by state and step (command 23). |
| Tutorial effects | Only `TabletOnboarding.OnTabletTickState` adds e-mails (the theme offers, `NewThemeEmail`). `AddItemToInventory` is called from `CWCGpuOnboardings.StartOnboarding`, `CloneOSOnboarding.OnJobAccepted`, `RepairPCOnboarding.SetupOnboarding` and `WaterCooledGPUOnboardings.OnLoadFromSave`. | The guest's theme offers go to the host's inbox. The item gifts start from job accepts, which run on the host. |
| Achievements | Every `Achievements.On…` entry reports to the platform. Milestones come from `AddCash`/`SpendCash`/`CollectCash` (`OnCash`), `AddKudos` (`OnLevel(GetZeroIndexedLevel(kudos))`), `AddReview` (`OnStarRating`) and `OnCollect` (`OnJobComplete(m_numberJobsCompleted)`). | Held while a game shows or runs the other player's action. Each game checks the milestones from the shared numbers. |

## 0.66 implementation: inbox, tutorials and achievements (17e)

**Status:** coded with model tests on November 10, 2026. Release built with zero warnings or errors and all 907 automated checks passed. No game was launched. Nothing here is verified in the running game.

| Area | What 0.66 does |
| --- | --- |
| Inbox actions | `JobWorld` step Inbox carries an `InboxAction`: read, flag, unflag, add or remove a tag, delete, erase, restore, a new tag, or a tutorial's e-mail. It names a job by its ID, or an e-mail by its list, its place and its time stamp. The acting game does it at once. Client's action is sent, and the host's game repeats it with the game's own method, checking the state Client saw first, so a repeated delete never erases. The host's next inbox update brings both games in line. Mirrored desktop clicks never act on the watching game, except opening an e-mail. |
| Fingerprint | The inbox fingerprint includes tag names, so a tag swap is shared. |
| Tutorial e-mails | On Client, the tablet tutorial's theme offers are serialized and added to the host's inbox instead of Client's. |
| Tutorials | Command 23 `OnboardingWorld` holds each tutorial's state and step. Four times a second each game sends a tutorial its own player moved, as a move from the shared value. The first move wins; the other game sets its tutorial the game's own way and clears that step's stale action-bar prompt. Free Build tutorials work the same way. |
| Achievements | `NativeAchievements.Guard` holds every `Achievements.On…` call while the game shows a projection, replays an edit, receives mirrored desktop input or runs Client's job, bill or renovation action. Each game in a Career calls `OnCash`, `OnLevel`, `OnStarRating` and `OnJobComplete` when the shared numbers change. |
| Save format | 0.66 reads 0.16–0.66 saves. The tutorial state is optional. |

### Not covered by 0.66

- When a tutorial step moves because of the other player, this game clears that step's prompt, but the new step's prompt only appears if that game's tutorial code shows it (often on its next event). Highlights a step turns on stay with the game that ran the step.
- A tutorial step whose own code changes the world runs only on the game whose player did the step. The traced ones are covered: theme offers go to the host, and item gifts start from job accepts on the host. `WaterCooledGPUOnboardings.OnLoadFromSave` still gives its part on each game's load.
- Tutorial pop-ups seen (`m_seenTutorial`, `m_seenJobTutorials`) are kept by each game until the next reload.
- Inbox buttons act on the acting game first. Until the host's next update (about a second), the two games can briefly differ.
- Achievement progress values the platform keeps aren't compared between accounts.

### Runtime checks for the gameplay sweep

1. Client flags, tags, deletes, restores and permanently deletes e-mails and old jobs. Within a second or two, the host's inbox shows the same, and after the day reload too.
2. Client opens a new e-mail: it's read on the host's game too.
3. Client makes a new tag and puts it on an e-mail: the host sees the tag.
4. Both delete the same e-mail at once: it ends in Deleted, not erased.
5. A new co-op career:
   - The host does the first tutorial step and Client's step moves too.
   - Client does the next step and the host's step moves (log `[tutorial] … follows the other player`).
   - The exit and power buttons the tutorial blocks free up on both games.
6. Client opens the tablet first in a new career: the theme offers appear in both inboxes (log `the tablet tutorial's e-mail goes to the host's inbox`).
7. Client collects a job: the host's log shows `[achievements] OnJobComplete held`. Both accounts get the jobs-completed milestone, and only the player who applied a sticker gets the sticker achievement.
8. Free Build: a tutorial step done by one player moves the other's.
9. Startup logs no `[hooks]` error for the new inbox, tutorial and achievement hooks.

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

## 0.73 fixes from the sixth in-game sweep run (18b)

**Status:** coded November 12, 2026 after the sixth Career run (both games on 0.72; the Client did the job up to the GPU, the Host fitted the GPU). Release built with zero warnings or errors; all 937 automated checks passed. Not yet run in game.

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

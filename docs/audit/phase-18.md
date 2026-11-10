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

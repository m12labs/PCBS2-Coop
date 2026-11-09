# Career co-op acceptance checklist

All cases are pending. Implement the known sync paths in phases 15–17, build against the installed game's interop, and run the model/network checks before the first full in-game pass. Run the gameplay cases on disposable co-op saves. Record the mod/game build, case, roles, transport, visible outcomes on both games, normalized state or save evidence, and result. Retest failures and add cases for any newly found native paths. [Roadmap](../plan.md#career-implementation-and-testing-order)

## 15. Start, save and join

- [ ] **C15-01 Menu.** Open Co-op from the game's main menu. Host Career, Host Free Build, Join a Friend, Co-op Options and Back fit with the title, extras and side card at 16:9, ultrawide and a small supported window. Hover, controller/keyboard focus, resize, cancel and Esc keep the expected row/card selected and clickable.
- [ ] **C15-02 Save selection.** Test no saves, only one mode's saves, and both modes with identically named saves. New, Continue, Load, delete confirmation and backup recovery stay in the selected mode. Solo Career and Free Build saves remain byte-identical.
- [ ] **C15-03 Career start.** Start one standard Career from Host Career. Confirm the native starting workshop, onboarding/day, cash, owned items and stable campaign ID. A guest joins the same Career snapshot and sees the same world with independent input. Join a Friend also admits a Free Build guest through the same entry.
- [ ] **C15-04 Exact restore.** Save two named Career states. Load each exact file, cold Continue the newest readable pair, `/sync` and rejoin. Compare campaign ID, mode, scene, day, ledger, progression, inventory and native PC identities on both games. Interrupted load returns to a usable menu with a reason.
- [ ] **C15-05 Compatibility.** Reject mismatched mode/run/scene, bad or wrong sidecar, and unavailable required content. Check known old Free Build pairs, Free Build menu and save paths after adding Career.
- [ ] **C15-06 Owned-item reuse.** Both players install/remove parts and use wiring, peripheral and tool paths. Two identical SKUs with different provenance or condition stay distinct. Race for the last item from different PCs and verify one outcome. A missing reserved item cannot turn into a new item. A job-owned PC keeps its customer relationship across movement and restore.

## 16. Shop, delivery and sale

- [ ] **C16-01 Cart and checkout.** Both actors update shared carts in reachable shops and buy immediate and scheduled items, including custom cables. Verify exactly one debit/order, correct stock/quote/discount/delivery fee, and exact goods. Search and scroll stay personal.
- [ ] **C16-02 Races and refusal.** Contest one checkout, the last stock item and insufficient funds. Change a cart or quote after confirmation opens and verify the old intent is refused. Retry the same operation. Check no extra charge, grant, order or silent substitution.
- [ ] **C16-03 Delivery.** Advance to the due day. Collect a scheduled order, carry and place the package, open it and check contents, condition and job references. Race to collect/open, and disconnect while carrying. Both peers see one package or one grant. The returned package remains reachable from shared staging.
- [ ] **C16-04 Recovery.** Disconnect and rejoin or cold Continue with an unpaid cart, paid transit order, arrived package and settled order. Where native returns/refunds are reachable, test both actors, a duplicate attempt and restore after the reversal.
- [ ] **C16-05 PC Bay and retail.** Buy a current offer and refuse a stale or sold offer. List and sell an eligible owned PC from both roles, refuse a move or sale of a PC in active use, and compare exact money, inventory, PC custody and sale/review records after rejoin.

## 17. Jobs, day and communications

- [ ] **C17-01 Job loop.** Both players accept and collect eligible jobs. Test accept contention, decline, abandon and expiry where reachable. Tie the PC, note and package to the correct customer through pickup, storage, handoff and rejoin.
- [ ] **C17-02 Objective coverage.** Use shipped jobs for builds/repairs, cleaning/paste/cooling, boot/software/diagnostics, benchmark/tuning, customization, budget/deadline and multi-job cases. Cover required, optional, hidden and failure outcomes. Record which actual job templates and objective triggers were exercised.
- [ ] **C17-03 Settlement.** Both players attempt to collect one finished job. Compare one cash/XP award, review and unlocked progression. Cold Continue and rejoin preserve the result. A failed or expired job awards exactly its native result.
- [ ] **C17-04 End Day.** The guest cannot advance the day. The host waits for accepted edits and transactions to finish. Verify one next-day outcome with its deadlines, bills/charges, due deliveries, generated offers/jobs, email and dust. Resume from a pre-day and a post-day pair. Interrupt the transition and verify one coherent restored day.
- [ ] **C17-05 Onboarding.** Each role completes a required step in a disposable Career. Both see shared progress, keep personal prompts/input and resume correctly. Test host skip and scene transition where the starting flow exposes them.
- [ ] **C17-06 Inbox and notes.** Both players see the same flags, tags and notes while selection, focus and scroll remain local. Move and recover a physical note with its job relation intact. Save and rejoin without duplicate or lost mail, notes or job actions.
- [ ] **C17-07 Account effects.** Local eligible Steam achievements and external upload actions stay with the initiating account. Replication, snapshot load and rejoin never repeat an account write on the other player.
- [ ] **C17-08 Shop renovation.** Trigger the native renovation with the host, confirm one charge and the intended day change, and verify both players arrive in the renovated workshop of the same campaign. Cold Continue, guest rejoin and an interrupted renovation preserve an intact pair, inventory and job links.

## 18. Recovery and full acceptance

- [ ] **C18-01 Fault injection.** Repeat checkout, delivery, sale, collect, day and PC editing with dropped/reordered packets, duplicate/stale operations and disconnect before acceptance, during native work and after settlement. Verify release of abandoned resources and exactly one durable result.
- [ ] **C18-02 Save recovery.** Interrupt native save/sidecar replacement, reject a corrupted or wrong-mode pair, and recover the last valid matching pair. Resume on a fresh host process, then rejoin. Compare balances, day, jobs, PC/part identities, claims, delivery state and hashes.
- [ ] **C18-03 Two machines.** Run fresh and continued Career with both actor roles over LAN and Steam. Exercise ordinary work across multiple busy days, content mismatch behavior, recovery and guest rejoin. Record bandwidth, frame time and snapshot size and investigate observed regressions.
- [ ] **C18-04 Free Build regression.** Recheck menu/hosting, paired saves, building, owned-item paths, cabling and `/sync` after the shared code changes. Record the evidence separately from the skipped Free Build release phase.
- [ ] **C18-05 Coverage and gaps.** Record a disposition for each Career-relevant audit factor and objective family. Recheck observed gaps and document intentionally deferred modes, workshops, content and runtime-only variants. Close only when the supported two-player loop and its recovery pass.

# Phase 18 audit: Career recovery and acceptance

**Evidence:** static risk review. Use [the Career checklist](../career-testing.md#18-recovery-and-full-acceptance) for actual results. Free Build's phase 14 remains skipped for now. Its release checks have their own scope and status.

| Boundary | Invariant and evidence to collect |
| --- | --- |
| SS generation, order and duplicate handling, CP-055 | Run latency, loss, duplicate/reorder, stale-generation and disconnect cases against the Career handlers. Record one outcome per checkout, delivery claim, sale, reward and day transition. A guest loading a snapshot must catch up to the same revision and display the same result. |
| CP-024–028, 055–059, V-14–18 | Save/load and rejoin during mixed PC, economy and day activity. Inject interruption before acceptance, after reservation, after native work starts, after ordered settlement and during pair replacement. Recovery loads one complete accepted state or the last verified backup and releases abandoned claims. Record native and sidecar hashes and normalized ownership, ledger, day and job summaries. |
| CP-023, 027–028, 060–061, 069–070, V-17 | Check content and mode refusal for incompatible builds, unsupported Career modes, workshops and assets. Keep personal preferences and local file/account actions separate from shared game outcomes. Test that replay/rejoin does not award the other account's achievement or perform an upload. |
| All Career CP factors and reused HW/PW/SW paths, V-01–19 | Test the supported Career loop on two local instances and two machines. Use LAN and Steam admission/rejoin, two-way actor actions, simultaneous PC activity, busy multi-day play, stock/cash conflicts and a cold host restart. Record comparable state digests, visible outcomes, bandwidth, frame time and snapshot sizes. Run Free Build menu, hosting and save regressions. |

Classify each tested item as passed, partial, failed, out of the agreed first scope or unverified. A native symbol and a green network test are different kinds of evidence from a two-game pass. Record game/mod build, operator, fixture, transport, both actor outcomes and run logs for each result. Audit all Career factors against phases 15–18 before closure, including any unsupported modes, DLC paths or objectives surfaced during the run.

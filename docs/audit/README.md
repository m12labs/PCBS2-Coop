# PCBS2 multiplayer synchronization audit

Audit date: **October 25, 2026**. This is a source-backed inventory of potential synchronization requirements across the game, including small visual and transient effects. It is not an implementation, approved feature scope, network design, or running-game certification.

Start with [sync-progress.md](../sync-progress.md) for implementation status and audit progress. Phases 1–2 now provide one shared Free Build snapshot, drop-in admission, host save copies and a remote capsule. Phase 3 adds host-approved PC creation/removal, per-player activity, and atomic carry/bench/storage transfers; component edits remain unsynchronized. The domain reports below retain their original broad-audit findings; focused phase reports record subsequent implementation and verification.

## Reports

| Report | Coverage |
| --- | --- |
| [Phase 1 focused audit](phase-1.md) | Synchronization foundations, identity, ordering, diagnostics and phase 1 evidence. |
| [Phase 2 focused audit](phase-2.md) | Shared Free Build lifecycle, snapshot admission, co-op saves and phase 2 evidence. |
| [Phase 3 focused audit](phase-3.md) | PC/inventory/carry/bench native paths; 3a/3b evidence and exact-save fix; 3c identity/paired persistence; 3d actor activity/conflicts; 3e creation/removal; 3f atomic transfers, async projection corrections, load-queue and hidden-bench persistence fixes, and owned two-instance verification. |
| [Phase 4 focused audit](phase-4.md) | Standard component transaction boundaries, slot/mechanical identity gaps, owned native/asset probe results, cancellation and persistence findings, and normal-control checks N1–N8 and G1–G6 (4b complete; custom loop and cable accessories deferred). Component synchronization is not implemented yet. |
| [Player, workshop and presentation](player-workshop-and-presentation.md) | Movement/activity, carrying, benches, notes/packages, workshop travel, decorations, lighting, ceiling fans, clocks, traffic, animation callbacks, audio and local-view candidates. |
| [Hardware and building](hardware-and-building.md) | Part instances/topology, screws/latches, tools, paste/pads/delid/dust, cables/pipes/coolant, case paint/stickers/themes, RGB/LEDs, fan direction/blur, peripherals, physical power and damage. |
| [Screens, software and simulation](screens-software-and-simulation.md) | Boot/BIOS/OS, files/programs/install/clone, monitors/case LCDs, desktop windows/input, wallpapers/GIFs, benchmarks/tuning/thermal/load/failure, tablet feeds, apps/minigames and audio-driven effects. |
| [Career, economy and persistence](career-economy-and-persistence.md) | Modes/jobs/objectives, inventory/provenance, purchases/delivery/sales, cash/progression/reviews, email/calendar/tutorials, saves and sharing boundaries. |
| [Session and consistency](session-and-consistency.md) | Existing protocol, identity, ownership/conflicts, snapshots/late join, scene readiness, operation replay, pause/time, persistence, content compatibility and cross-domain transactions. |
| [Coverage and source inventory](coverage-and-evidence.md) | Build identity, evidence levels, automated versus reviewed coverage, source index and field inventory. |
| [Open decisions and verification backlog](open-questions-and-runtime-checks.md) | Questions for implementation, missing asset evidence and concrete future runtime cases. |

The hardware, software and career reports were researched by three **Luna subagents**, then deepened and reconciled. The root pass covers player/workshop/session behavior, evidence boundaries, source inventories and documentation checks. The Postal project's `docs/sync-progress.md` supplied the feature/status-table convention; its implementation status, scope decisions and test results were not transferred to this project.

## Reading a finding

Each domain has stable factor IDs (`PW`, `HW`, `SW`, `CP`, `SS`). IDs are audit references, not network message IDs or a set of equally weighted completion milestones. A row can bundle closely related details, and cross-domain rows can overlap. Never use the total row count as a percentage of multiplayer completion.

The five registers contain **318 entries**: 75 player/workshop, 78 hardware/building, 53 screens/software, 70 career/persistence and 42 session/consistency. Some entries overlap or describe existing features; they are not 318 unique approved requirements.

| Evidence | Meaning |
| --- | --- |
| Mod source / M | Existing implementation inspected in this repository. Runtime coverage must be stated separately. |
| Declaration / D | Recovered type, field, enum, offset or method signature inspected. C# method bodies are placeholders. |
| Native / N | The specified disassembly/ISIL excerpt inspected. A method label alone establishes presence, not complete behavior. Unresolved calls/instructions remain limitations. |
| Inference / I | Multiplayer consequence inferred from the cited source shape or behavior. Not a runtime observation. |
| Historical runtime artifact | Existing log/scene dump read during this audit; not a fresh test and not evidence beyond what it records. |
| Runtime verified | Requires an identified, executed running-game check. No new finding received this label in this audit. |

`State` candidates are current/persisted facts; `event` candidates are operations or one-time transitions; `derived` candidates are reconstructed output; `local-pending` candidates may stay personal if the owner chooses. These are classifications for discussion. They do not select ownership, screen-sharing method, UI placement, save policy or synchronization scope.

The owner subsequently answered the initial scope questions while writing the [implementation plan](../../plan.md#owner-decisions) on October 25, 2026. That decision table supersedes pending-policy wording in these original source-audit reports; their evidence and missing-implementation findings remain valid. Each numbered phase begins with a focused audit and then receives its lettered implementation subplan.

**Missing** means that no sync path exists for that candidate in the inspected mod. **Existing** means present in source, not that every acceptance case passed. **Partial** names an explicit existing subset. **Decision pending** is independent of implementation status. No system is silently declared intentionally local.

## Source and access notes

Primary game evidence comes from the local [decompiled workspace](../../artiffacts/decompiled/README.md), preserved [build manifest](../../artiffacts/decompiled/manifest.json), structural game declarations, native ISIL, and method indexes. [The decompile workflow](../../il2cpp-decompile.md) explains the limitations. Local primary sources suffice for this audit; no web claims were needed.

Recovered proprietary code and binaries stay under ignored `artiffacts/`; the audit records field/symbol facts and links rather than copying method implementations. Links into that directory require the matching local decompile. The portable reports retain source type/member names and build hashes to identify their evidence after checkout elsewhere.

Generated source/field CSV inventories also live under ignored `artiffacts/audit/`. The versioned [inventory generator](../../tools/audit/README.md) recreates them from the local decompile; they are not part of the handwritten documentation commit.

No game was launched or controlled, no owner's process was closed, no save was opened/deserialized/changed, no game asset was modified, and no implementation/build/deploy/commit/push was performed. Existing game logs and the historical scene-root diagnostic were read only. Documentation validation checks files/links/IDs/inventory, not gameplay.

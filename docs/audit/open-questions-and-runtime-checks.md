# Open decisions and verification backlog

The initial design questions below were answered by the owner on October 25, 2026. The accepted choices are maintained in [plan.md](../../plan.md#owner-decisions), which is authoritative for implementation scope. The source/runtime evidence gaps remain open. A decision does not mark its implementation synced.

## Owner decisions

**Answered:** Q-01–Q-10 and the avatar timing/reuse preference in Q-11 are recorded in [the plan](../../plan.md#owner-decisions). Players pass through each other. The table preserves the original audit questions for traceability rather than treating them as still pending. A suitable reusable player/NPC model remains unverified; phase 13 audits those assets before selecting one. New UI/product questions discovered by a phase's focused audit go to the owner before dependent implementation.

| ID | Decision | Why it affects synchronization |
| --- | --- | --- |
| Q-01 | Which modes and workshops should a future shared-world implementation support? | The audit inventories career, Free Build and other code families; the existing proof of concept targets one Free Build workshop scene. |
| Q-02 | Are saves, wallet, inventory, jobs, progression and achievements shared, personal or host-owned? | Determines persistent identity, transactions, account side effects and what a reconnect restores. |
| Q-03 | Can two players work on the same PC, slot, cable route, paint surface or bench tool at once? | Determines conflict behavior, operation ownership and what a rejected action looks like. |
| Q-04 | Who may carry/store/sell a PC another player is using, or replace its desk/workshop? | Transfers can invalidate active tools, screen bindings and camera targets. |
| Q-05 | Do both players observe/control the same computer desktop and pointer, or use independent views of its software? | Desktop focus, window state, typing, application models and screen output require different scopes. |
| Q-06 | Is each tablet personal, and should its physical screen/activity be visible to the other player? | Tablet camera feeds and UI lifecycle differ from persistent PC/software state. |
| Q-07 | How should pause, focus loss, modal dialogs, day changes and scene travel affect both players? | Controls simulation time, tool completion, benchmark clocks and load/readiness holds. |
| Q-08 | Which ambient/cosmetic details need exact parity: clocks, traffic, random audio variants, music/RGB phase? | Some can be derived or stay local; this is not an automatically selected omission. |
| Q-09 | Should custom wallpapers, GIFs, themes and exported/imported PCs be transferable? | Local filenames are not content identity; a missing-file policy and resource limits are needed if shared. |
| Q-10 | What should happen when the host leaves, a guest reconnects or a save is restored mid-operation? | Requires a world lifetime and operation recovery policy beyond the current transport. |
| Q-11 | Should avatars gain a body/tool pose/collision, and what remote activity needs showing? | Current remote presentation is a collider-free capsule with yaw and a label; no native player body rig was verified. |

## Missing directories and asset evidence

The requested `docs/audit/` directory was missing and has been created. No essential code-decompile directory was missing. **An extracted Unity asset directory was not found** under the inspected workspace. The decompile manifest explicitly says assets are inventoried, not reconstructed. Exact Animator clips, prefab variants and monitor/material bindings therefore remain unverified.

The final native export coverage summary (`artiffacts/decompiled/coverage.json`) was also absent. ISIL is available and was used; this is a coverage-report gap rather than absence of the native game binary. Do not label the entire game natively recovered merely because functions.tsv exists.

| Asset follow-up | Specific evidence to record |
| --- | --- |
| Scene and prefab census | Every workshop/mode, root/object IDs, part/peripheral variants, dynamic factories and runtime instances. |
| Animator/clip census | Controller, layer, state/transition, parameter, clip identity/length/loop/speed, event timing and callback target for tools, fans, switches, panels, tablet and displays. |
| Material/screen census | Physical monitor/curved display/case LCD routes, cameras, render textures, aspect/UV mapping, powered/off/failure materials and shader properties. |
| Effects/audio/video census | Dust/tool particles, one-shot versus pooled loops, random clips, music-reactive effects, benchmark video and GIF frame pipeline assets. |
| Content/version census | Part catalog and DLC hashes, connector topology, decoration/sticker/theme resources, unavailable content/fallbacks and supported custom-file formats. |

## Live verification backlog

Every row is **Not run** by this audit. Source review identifies what to check; it does not satisfy these acceptance criteria.

| ID | Scenario | Evidence needed |
| --- | --- | --- |
| V-01 | Start/rejoin/leave from separate machines | Correct game/mod/content match, roles, failure paths, current world restoration and safe end behavior. |
| V-02 | Separate bench and tablet activities | Independent input/cameras with consistent target PC, held object and tool activity. |
| V-03 | Every component category, duplicate identical parts and variant case | Matching slot instance, blockers, screws/latches, power and saved topology after install/remove/load. |
| V-04 | Same-part/bench/slot/content conflicts | One accepted logical result, rejected action recovers, no duplication or stuck operation. |
| V-05 | Cable/pipe/LED graph creation and cancel | Same endpoints, bend/node frames, connectors/colour/material, graph membership and restore order. |
| V-06 | Paste, dust, pads, delid, paint and stickers | Both persistent state and visible coverage/transform agree; callbacks apply once. |
| V-07 | Fans, RGB, coolant and physical power | Correct direction/flipping, speed/blur, lighting/effect configuration, flow/run state and terminal power. |
| V-08 | Monitor, BIOS, desktop, curved screen and case LCD | Correct PC-to-screen association; agreed boot/failure/software/sensor state and intended observer/input policy. |
| V-09 | Install/uninstall/clone/USB and filesystem operations | Correct resulting image/files/storage and operation outcome; no duplicated progression effect. |
| V-10 | Benchmark, OCCT and tuning under thermal/power failure | One run identity/clock/result/failure; correct sensors, graphs, LEDs/fans and objective consequences. |
| V-11 | Shop/order/delivery/package/return/sale | Price/quantity/cash/order/item provenance are consistent and replay-safe across interruption/rejoin. |
| V-12 | Career objectives, emails, reviews and day progression | Single outcome for each job, date/deadline/charge/delivery/unlock transition, with intended personal/shared scope. |
| V-13 | Workshop/theme/light/season changes | Geometry/slots/colliders/probes/screens/fan ramps and decoration exclusivity agree after async loads. |
| V-14 | Pause/focus/load during every operation family | Correct time domain and callback/input/loop cleanup with no operation finishing twice. |
| V-15 | Late join into active tool, fill, boot, benchmark, decoration swap and pending order | Snapshot restores stage and relationships without replaying irreversible transactions. |
| V-16 | Manual/autosave/load/fresh-process host resume | Coherent PC/inventory/economy snapshot; identity preserved; no solo-save interference. |
| V-17 | Custom assets missing/different and partial DLC support | Defined content mismatch/fallback; no accidental path-based assumption. |
| V-18 | Packet loss/latency/reorder, duplicate requests and disconnect at commit | Convergence, exactly-once outcomes, cleanup and no old-world mutation after reload. |
| V-19 | Ambient animations, audio, traffic, clocks, photo/GIF capture | Compare intended parity and personal presentation with asset-backed scene evidence. |

Detailed per-factor checks appear in each domain report. Evidence should include build identity, scenario, observed logical state on both peers, visible outcome, relevant logs and pass/fail—not only a screenshot or method name.

# Phase 7 audit: simulation, tests, tuning and app results

**Status (November 1, 2026): implemented in 0.20, not tested in game by the owner.** Short owned two-game runs sim-1 to sim-4 passed and the owner's decisions are recorded (all match 0.20); the owner's full two-game test is deferred to a later day. Evidence comes from the same game dump as phase 6 (Unity `6000.2.6f2`, build GUID `92d05e5a20c14d41aacbb9c6a804d5a3`). Everything in "Decompile findings" was read from the ISIL/metadata dump and has not been checked in a running game unless a line says so. The owner's decisions are at the end.

## Scope (plan.md section 7)

Effective clocks/voltage/memory timings; temperature, load, power, throttling, condition and failure; fan curves and fan control; airflow/thermal/power/noise tools; benchmark and OCCT run identity, progress and results; sensor/graph meaning; hardware-info and tuning app effects; app/minigame outcomes that change PC state. Audit factors: SW-010–SW-016, SW-020–SW-022, SW-040–SW-047, SW-053 ([screens, software and simulation](screens-software-and-simulation.md)).

## Baseline before phase 7 (0.19)

- Both games run the same apps because every desktop click, drag, scroll and text change is mirrored (phase 6, command 11). Starting OCCT, 3DMark or Cinebench on one game also starts it on the other a few frames later.
- Each game runs its own `ComputerSim` for every powered PC. Nothing in the mod reads or writes the simulation.
- BIOS Apply/Factory Reset are host-ordered (command 10); the host's boot-drive software record, and with it the benchmark record, wins (command 12).
- Known 0.19 limits that belong here: Fan Control curve points are dragged through polled input and are not mirrored; live temperatures can differ slightly.

## Decompile findings

### The simulation (`ComputerSim`, one per `VirtualComputer`)

- **State.** `Dynamic` values (idle, load, current) for CPU/GPU/RAM/PSU/VRM temperatures, usage, wattage and voltage; `m_cpuLoad` (float), `m_gpuLoad`/`m_ramLoad` (counters, so overlapping apps add up), `m_storageLoad`; `m_timeUnderCPULoad`/`GPU`/`RAM`, `m_cpuThrottled`, `m_bsodActive`, `m_lockError`, `m_isRunning`. None of it is in the native save (power isn't saved either, phase 5).
- **Time.** `Update` integrates every `Dynamic` with `Time.deltaTime` and adds `deltaTime` to the under-load timers. No pause, `timeScale` or unscaled-time check in `ComputerSim`, `OCCTApp`, 3DMark, Cinebench, `Fan` or `AirflowSim`, so a paused game keeps simulating (matches the phase 2 no-shared-pause rule).
- **Randomness.** `Random.Range` only in the display getters `GetCpuInternalTemp(core)`, `GetGpuInternalTemp(gpu)` and `GetCaseInternalTemp` (there is a `GetGPUInternalTempNoRandom`), `PowerMonitorApp.GetWattage` and `AirflowSim.UpdateAirTemperature` (small noise on node air temperature). `Fan`'s `System.Random` only drives a faulty fan's wobble; `m_faulty` itself comes from the part. The failure checks below read the smoothed `Dynamic.m_current`, not the noisy getters.
- **Failures (`EmergencyShutdown(reason, force)`).** Call sites: `Update` (CPU at its shutdown temperature without valid thermal transfer → `CPU_HEAT`; under-load time past `ComputerSave.GetTimeToFailureUnderCPULoad` / `UnderGPULoad` / `MemTiming`; no cooler and not fully piped; water loop not full), `UpdateWattage` (power draw over the PSU) and `VerifyInitialWattage` (boot). The `GetTimeToFailure*` methods read only the BIOS config and parts (voltage requirements, RAM speed, GPU overclock factor, timing ranges): **no randomness**. Unless forced, it returns early when errors are locked, a BSOD is already active or the sim isn't running. It sets `m_bsodActive`, resets every fan's control data to defaults for heat reasons (CPU/GPU/RAM heat), calls `VirtualComputer.BlueScreenOfDeath(reason)` and zeroes the CPU and GPU loads.
- **`BlueScreenOfDeath`** invokes `OnBSOD`, `ComputerSim.ToggleLoadedOS`, `OS.OnShutdown`, hides the OS objects, shows the blue screen with the localized reason and calls `Achievements.OnBSOD`. The motherboard stays on; the player restarts it with the power button (a shared phase 5 gesture). The boot coroutine has its own deterministic BSOD (loop not full / unpowered parts) straight from the PC record.
- **Throttling.** At the CPU shutdown temperature with valid thermal transfer, `Update` sets `m_cpuThrottled`, a flag on the record and clamps the temperature (`Achievements.OnThermalThrottle`). Deterministic from the smoothed temperature.

**Consequence.** Both games compute the same outcome from the same PC and the same load timeline, but the load timeline differs by the mirroring delay (a frame or two on a LAN, up to 4 s when a click waits for the other game's PC to reach the same screen), frame-rate integration differs slightly, and a dropped click can leave a test running on one game only. A PC can therefore blue-screen on one game and keep running on the other, after which the mirrored desktops no longer match.

### Load sources

`ComputerSim.SetCPULoad`/`SetGPULoad`/`SetRAMLoad`/`SetStorageLoad`/`AllocateMemory` are called from `OCCTApp.SetLoad` (CPU+GPU+RAM), `ThreedMarkApp.DoBenchmarking`, `CinebenchR15` and its `CPUTestAnim`, `AddProgramInstall.ShowProgress` (storage), `OS` and `VirtualComputer` boot/restart. All are ordinary app flows reached through mirrored clicks. OCCT times its run with `DateTime` (`m_testStart`); automatic mode uses fixed 5/20/5 s phases. Closing an app (`OnDisable`) releases its load, so the open-apps reconcile also releases it.

### Benchmarks and results

3DMark and Cinebench contain no `Random` or `deltaTime` in their score paths; 3DMark's score uses `ComputerSim.GetTemperatureAdjustment`, which reads the airflow voxel grid (deterministic). Results land in the boot drive's software record, where the host's copy already wins (phase 6). Benchmark/overclock objectives, `AutoTuner` and `PCTuner` are only reached from career jobs (`OverclockObjective`, `FanModeObjective`): out of Free Build scope.

### Tuning apps

- **GPU Tuner.** `OnApply` writes `m_gpuClock`, `m_gpuMemClock` and `m_gpuVoltagePercent` straight into the PC's `BiosConfig` and calls the case's `AirflowSim.RefreshSimData`; `OnReset` writes the start values the same way. Neither restarts the PC. In 0.19 the mirrored click runs it on both games, each with its own slider values, bypassing the host-ordered BIOS command. `OnApply` shares native code with its own button lambda (`<OnEnable>b__42_0`): hook `OnApply` only.
- **Fan Control.** A fan's live curve is `Fan.CustomControlData` (`FanControlData`: preset, custom on/off, custom graph with sensor and points). `ApplySetting` copies the edited data onto the fans and refreshes the airflow sim. Presets, mode toggles, Apply and Defaults are ordinary buttons (mirrored); curve points are dragged with polled input (not mirrored). `FanControlDataSaveContainer` / `BuildSaveData` / `RestoreSaveData(fan, data)` serialize one fan; `RestoreSaveData` checks no ID. **Persistence gap:** the PC record's `FanControlDataSaveList` is written only by `ComputerSave.SaveFanControllerData`, whose only caller is the career `FanModeObjective`; loading reads it (`LoadFanControllerData`, matched by fan order). In Free Build the game therefore seems not to save Fan Control curves at all (runtime check needed).
- **RAMWizz, Will It Run, HWInfo, HW Monitor, Power Monitor, OCCT sensors, case LCD graphs.** Read-only views of the PC and its sim; nothing to share beyond the mirrored input. Their numbers carry the sensor noise above.
- **Airflow app** runs on the tablet (personal, phase 6 decision). **Noise tool** has no measured result (SW-053).

## State boundaries

| Fact | Kind | Owner in 0.20 |
| --- | --- | --- |
| Load requests (which tests run) | Event, from mirrored app input | Each game runs the app; no extra sync |
| Temperatures, clocks, usage, wattage, fan RPM, graphs | Derived, with noise | Each game's own sim (accepted difference, Q3) |
| Failure / BSOD and its reason | One-shot outcome | **Host** (new command 14 `Simulation`, step `Failure`) |
| Throttle flag | Derived | Each game |
| GPU Tuner values | PC BIOS config | **Host-ordered** BIOS command 10, new live mode (no restart) |
| Fan curves (live `FanControlData`) | PC config, live only in the game | **Host-ordered** command 14 step `Fans`, keyed by part path |
| Benchmark results | Software record | Host's record (phase 6) |
| Achievements | Local | Each game |

## Risks

- Fan identity: fans are matched by native part path plus the fan's index on that part. Two games that rebuilt a PC differently could disagree on the order of fans on one part (a radiator's fans); both run the same load path, so this is low risk.
- A guest whose PC isn't running when the host's failure arrives (still booting): the failure waits up to 10 s, then is dropped with a log line.
- Holding back the guest's own failures means a guest whose sim would fail first keeps running until the host's result arrives (normally within a frame or two).
- A guest that joins during a running test gets the open apps (phase 6) but the test isn't running there (Q4). The host's failure still reaches it.

## Acceptance cases (two games)

1. Host starts OCCT on an overclocked PC that fails under load; both games blue-screen once with the same reason; the guest log shows `[sim] failure ... from the host`.
2. The guest starts the test; same result (the guest's own failure is held back, the host's arrives).
3. Remove the CPU cooler's paste/cooler setup that causes `CPU_HEAT` (or an unpiped water loop); both fail the same way.
4. GPU Tuner: both players move the sliders, either presses Apply; the host applies once, both PCs show the same values in BIOS and GPU Tuner, no restart. Reset the same way.
5. Fan Control: either player edits a custom curve and presses Apply; the other game's fans and its Fan Control graph show the same curve. Presets and Defaults too.
6. Late join with a custom curve applied: the guest gets the curves.
7. Power the PC off and on after a BSOD (shared); both reach the desktop.
8. Evidence: `[sim]`, `[fans]`, `[bios]` lines.

## Implementation (0.20)

- **Command 14 `Simulation`** (`src/PCBS2Coop.Network/Simulation.cs`, plugin `NativeSimulation.cs`): steps `Failure` (host only, PC on a bench, reason 1–9) and `Fans` (a guest from that PC's monitor; the host any time). Nothing is saved.
- **Failures:** a hook on `ComputerSim.EmergencyShutdown`. Host: a call that turned `BSODActive` on is queued and submitted from `Update`. Guest: its own non-forced calls on a shared PC are held back (`[sim] ... would fail ...; waiting for the host's result`); the host's failure runs `EmergencyShutdown(reason, Force: true)` inside `NativeProjection` once that PC's sim runs (10 s wait, then dropped with a warning).
- **GPU Tuner:** `BiosChange.Live` (BIOS command 10). Hooks on `GPUTuner.OnApply` (also covers its button lambda) and `OnReset` put the config back on both games after the native call; the clicker's game requests the result, and the accepted change is written in place with `AirflowSim.RefreshSimData`, no restart.
- **Fan curves:** a postfix on `FanControlApp.ApplySetting` (not a replayed click) sends every controllable fan's curve 0.25 s later. A fan is keyed by its part's native path, or, when the record doesn't list that part object, by the nearest part above it that it does: a radiator's fans are separate part objects at run time (run sim-2), so they key on the radiator (`m_loops[0].m_components[1]#0..2` in sim-4). The other game restores them with `FanControlData.RestoreSaveData`, refreshes the airflow sim and an open Fan Control app (its selection backups too). The host re-sends every powered PC's curves when a guest presence arrives; the guest retries until that PC's sim runs (up to 150 s).
- **Quick clicks (phase 6 capture fix):** a press and release within one frame (low frame rate) were missed because capture polls the button state; capture now also checks `GetMouseButtonDown` and sends both.
- **Probe** (opt-in, `PCBS2COOP_PROBE=6`): `sim-report`, `sim-fail:<pc>:<reason>`, `programs:<pc>`, `launch:<pc>:<id>`, `tuner:<pc>:<core>:<mem>:<volt>`, `tuner-reset:<pc>`, `fans:<pc>:<preset>`, `fans-custom:<pc>`. App IDs seen: `GPUTUNER`, `FANCONTROL`, `OCCT`, `ADDPROGRAM`.

## Owned two-game runs (November 1, 2026, isolated saves, Windows-level screenshots only)

| Run | Result |
| --- | --- |
| sim-1 | Hooks installed without alias errors on both games. A host-side failure (probe, as the game would call it) blue-screened the guest's PC with the same reason. After a shared power cycle, a guest-side failure was held back and both PCs stayed on the desktop. GPU Tuner Apply on the host: both BIOS records 1900/1300/110%, no restart. OCCT ON by a real click on the host: both games ran the load with identical CPU temperatures (56.4 → 63.5 °C). A 90 ms synthetic tap was missed by capture (fixed above). Fan Control preset and custom mode from the host reached the guest's three GPU fans; the three radiator fans weren't matched (fixed in sim-3). |
| sim-2 | GPU Tuner Apply from the guest at the monitor: one host-ordered live change, both 1800/1250/105%. Real clicks in Fan Control on the host (select fan, Custom Fan Control, Apply): curves sent and applied on the guest. The guest's first mirrored click landed on another window because the probe-opened windows stack differently there; the result sync still matched the fans. |
| sim-3 | Script timing only (no result). |
| sim-4 | Late join: the host applied custom curves before the guest joined; the guest got all six fans' curves (GPU and radiator) once its PC came up. A GPU Tuner overclock to 3000/2600 made the host's GPU overheat at idle: the guest's own `GPU_HEAT` was held back, then the host's was shown; both blue-screened once and both games reset the fan curves the way the game does after a heat failure. |

Every run was stopped by `Stop-Phase6Probe.ps1`; all 21 owner save files were unchanged each time. No game closed unexpectedly and no display driver event was logged.

### Not checked in game yet

- A guest's GPU Tuner Reset, and a real mirrored click on GPU Tuner's Apply (the replayed click's put-back path).
- A quick click caught by the new within-one-frame path.
- Whether the game saves Fan Control curves in solo Free Build (decision 5 keeps solo behaviour either way).
- Cinebench and 3DMark result agreement (phase 6 already keeps the host's record).

## November 5, 2026 follow-up (0.27)

Run `acceptance-c` passed GPU Tuner Apply and Reset from either player through the actual app. Both games showed the changed and default values, with no restart. The same run passed real Fan Control presets, changes to a custom Max RPM curve, and Defaults from both sides. A fresh guest joined the running host and displayed the 11-point curve. [Run record](acceptance-2026-11-04.md#fan-control-and-custom-picture-follow-up-027)

In 0.30, a native `GPU_HEAT` failure from the host reached both games. A shared case-button power cycle cleared the blue screen and both games reached the desktop. [Recovery run](acceptance-2026-11-04.md#usb-replay-pipe-cancellation-and-blue-screen-recovery-in-030)

In 0.34, powering a PC with an empty custom water loop let the native simulation reach `CPU_HEAT` at 184.6°C. The guest suppressed its matching local failure and showed the host's blue screen. Refilling the loop and rebooting returned both games to the desktop at 45.8°C. [Dry-loop run](acceptance-2026-11-04.md#guest-thermal-paste-player-visibility-and-a-dry-loop-heat-failure-in-034)

In 0.43 the cooler-free startup reached a direct `VirtualComputer.BlueScreenOfDeath(CPU_HEAT)` on the host and the guest stayed in Boot. The 0.45 hook shares the startup result. Both games displayed the same failure text with the CPU cooler slot empty. [Run record](acceptance-2026-11-04.md#missing-cpu-cooler-at-boot-in-043-and-045)

An empty-loop boot in 0.45 could publish the startup and simulation reports for the same reason. In 0.46 the same reason is sent once per boot. Two successive dry-loop boots each produced one host publication and one guest application. [Run record](acceptance-2026-11-04.md#one-heat-result-per-boot-in-046)

The 0.46 host then removed the complete AIO from PC 213 and booted it. Both native graphs had zero AIO references and zero pipes. The host published `CPU_HEAT` once and the guest applied it once. [Run record](acceptance-2026-11-04.md#complete-cooler-removal-and-boot-in-046)

On November 6, 2026, the 0.40 test build showed one matching `CPU_UNDER_VOLTAGE` failure on each game when the host started OCCT and again when the guest started it. Time Spy and Cinebench R20 also finished with matching scores on both games. [Run record](acceptance-2026-11-04.md#mirrored-bios-occt-failures-and-benchmarks-in-a-040-test-build)

## Owner decisions (November 1, 2026)

The owner answered the seven audit questions; every answer matches what 0.20 implements.

1. **Failure authority:** the host's game decides when a PC fails; the guest holds back its own failures and copies the host's.
2. **Fan curves:** shared as the result when Apply (or a preset/Defaults) is pressed; the curve dragging itself is not mirrored.
3. **Live readings:** small differences in temperatures, clocks and fan speeds from the game's random sensor noise are accepted.
4. **Late join during a running test:** the guest's copy of the app stays idle; the outcome still follows the host.
5. **Fan curve saving:** solo behaviour for now (the game doesn't seem to save Fan Control curves in Free Build).
6. **GPU Tuner Apply/Reset:** wait for the host's OK (host-ordered, like BIOS Apply).
7. **Career-only tuning** (`AutoTuner`, `PCTuner`, overclock/fan objectives): left out until career co-op.

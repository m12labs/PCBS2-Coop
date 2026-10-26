# Player, workshop, animation, and audio audit

Static audit, October 25, 2026. This report inventories multiplayer consequences; it does not approve shared behavior or add it. See [scope and evidence rules](README.md) and the [progress tracker](../sync-progress.md).

`M` = mod source inspected; `D` = recovered declarations inspected; `N` = selected native disassembly/ISIL inspected; `I` = multiplayer requirement inferred from those sources. `S` = persistent/current state candidate, `E` = event candidate, `R` = reconstructable presentation candidate, `L?` = personal/local behavior awaiting owner decision. None of the `D/N/I` labels means a fresh running-game test.

All factors below are **Missing** in the mod except PW-001, PW-002, and PW-003, whose exact partial/existing coverage is stated. Combining several factors into one packet or leaving one local is a later design decision.

## Evidence groups

These groups identify the declarations actually inspected, including their fields and callable signatures. Referencing a group in the register refers to these sources, not a claim that every method implementation was recovered or reviewed.

| Ref | Sources and exact anchors | What the evidence establishes |
| --- | --- | --- |
| P1 | [Protocol.PlayerState](../../src/PCBS2Coop.Network/Protocol.cs#L15), [CoopController.Update / LocalPlayer.Find](../../src/PCBS2Coop.Plugin/CoopController.cs#L67), [RemotePlayerView](../../src/PCBS2Coop.Plugin/RemotePlayerView.cs#L11) | M: position, yaw, workshop flag; interpolated capsule/visor/name tag; remote collider removed. No pitch, action, carried-item, animator, or object state in the protocol. |
| P2 | [Walk](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/Walk.cs#L7), [MouseLook](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/MouseLook.cs#L18) | D: run enable, speed, movement, step timer/left-right/floor material, camera references; rotationX/Y, lock, sensitivity, tablet camera. N: [Walk movement](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Walk.txt#L1600) selects step sounds, calls positional audio and CharacterController.Move. |
| P3 | [WalkingState](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/WalkingState.cs#L117), [WorldInteraction](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/WorldInteraction.cs#L117), [AllWorldInteractions](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/AllWorldInteractions.cs#L33), [State](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/State.cs#L8), [UIState](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/UIState.cs#L9) | D: selected object, held interaction/long-press timer, action delegates, interaction blocking, state lifecycle, UI critical-message and back-action state. An Action delegate is process-local, not a stable network action ID. |
| P4 | [GameController fields](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/GameController.cs#L891), [state/camera/pause/safe-position APIs](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/GameController.cs#L1516), [GameState enum](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/PCBS/GameState.cs#L5) | D: one current state/previous-state stack, selected item, active camera, input focus, free camera, world time, safe-position stack. N: [PauseGame/UnpauseGame/SetPaused](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/GameController.txt#L11563) set a boolean; these routines alone do not establish universal timeScale-based freeze behavior. |
| P5 | [WorkshopController carry/world fields](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/WorkshopController.cs#L483), [pickup/drop/store/spawn APIs](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/WorkshopController.cs#L640), [WalkingState.UpdateCarriedTransform](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/WalkingState.cs#L206) | D: singular carried case, case status, package, package status, PostIt, active bench, player transform and office PC; asynchronous loading and external-cable delay. |
| P6 | [BenchSlot](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/BenchSlot.cs#L160), [DeskLogic](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/DeskLogic.cs#L79), [WorkingOnComputerStateBase](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/WorkingOnComputerStateBase.cs#L14), [WorkingOnPC](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/WorkingOnPC.cs#L8) | D: slot occupancy/status, real/ghost PC and package, colliders, sale display, dirty/load flags, camera claim/release, selected components, assembly/disassembly/cabling/piping/LED modes. |
| P7 | [PostIt](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/PostIt.cs#L37), [PostItSlot](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/PostItSlot.cs#L29), [Package](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/Package.cs#L55), [Cupboard](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/Cupboard.cs#L34) | D: note job association/text, slot/package association, swap/drop hooks, package contents/value APIs, storage interaction entry point. See career report for authoritative records. |
| P8 | [WorkshopDoor](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/WorkshopDoor.cs#L109), [LevelLoadPersistency](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/LevelLoadPersistency.cs#L267), [WorkshopManager](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/PCBS/WorkshopManager.cs#L71) | D: interior/exterior doors, entry point/rotation, last exit, different workshop scene names, async loading, selected workshop/bundle and DLC validity. A door is not necessarily an ordinary freely animated hinge. |
| P9 | [Decorator](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/Decorator.cs#L689), [DecoratorLink](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/DecoratorLink.cs#L8), [DecoratorListLink](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/DecoratorListLink.cs#L8), [BuildFurniture](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/BuildFurniture.cs#L7), [Furniture](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/Furniture.cs#L8) | D: mesh/material/prefab substitutions, linked objects, selected/saved indexes, IDs, themes, AR flags, queued async operations, cupboard build flag, purchases/order/save-list interfaces. |
| P10 | [DecoratorClippingHandler](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/DecoratorClippingHandler.cs#L8), [DecoratorClippingData](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/DecoratorClippingData.cs#L8), [CompanyNamePlate](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/CompanyNamePlate.cs#L192), [MaterialSwap](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/MaterialSwap.cs#L26) | D: bench clipping limits, company text rendered to texture with material channels/reflection probe, theme-based position and material/object/LED substitutions. |
| P11 | [LightSwitch](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/LightSwitch.cs#L188), [CeilingFan](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/CeilingFan.cs#L8), [CheckWorkshopLights](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/PCBS/CheckWorkshopLights.cs#L8), [LightManager](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/LightManager.cs#L264), [HeroLightController](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/HeroLightController.cs#L8) | D: switch animation, fan ramp coroutine, fan ignore flag/audio transform, profiles, culling/detail settings, case hero light. N: [LightSwitch.SetOn excerpt](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/LightSwitch.txt#L1015) includes roof lights, animator trigger, probe state and fan speed read; [roof light excerpt](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/LightSwitch.txt#L2255) sets `blightOn` and calls LightDetailHandler.TurnOn. |
| P12 | [DecorationSwitch](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/DecorationSwitch.cs#L30), [Christmas](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/ChristmasDecorationSwitch.cs#L9), [Halloween](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/HalloweenDecorationSwitch.cs#L9), [default switch](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/DefaultDecorationSwitch.cs#L9) | D: active decoration ID, mutually hiding active-decoration callback, object activation, colliders and profile changes. N: [FindCurrentSeason](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/GameController.txt#L1181) reads DateTime.Now; season cannot be assumed equal on two machines. |
| P13 | [WallClock](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/WallClock.cs#L7), [AnalogueClock](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/AnalogueClock.cs#L17), [Vehicles](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/Vehicles.cs#L8), [Vehicle](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/Vehicle.cs#L9), [VehicleColourChange](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/VehicleColourChange.cs#L8), [LeftVehicalTrigger](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/LeftVehicalTrigger.cs#L8) | D/N: [WallClock.Update](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/WallClock.txt#L108) integrates deltaTime; [AnalogueClock.Update](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/AnalogueClock.txt#L850) reads GameController world time. [Vehicles spawning](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Vehicles.txt#L232) calls random vehicle colour selection. Class presence does not prove visibility in every workshop. |
| P14 | [SoundPlayer](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/SoundPlayer.cs#L90), [PooledAudioSource](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/PooledAudioSource.cs#L9), [AmbientController](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/AmbientController.cs#L279), [AmbienceManager](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/AmbienceManager.cs#L8), [OutsideSoundEmitter](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/OutsideSoundEmitter.cs#L8), [MusicManager](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/MusicManager.cs#L255) | D: 2D/positional one-shots, continuous pools, stop/pitch/volume, source ownership, ambience fades, random sound variants; music track/source/playhead/user path/radio/amplitude. Local volume and a shared audible event are different concerns. |
| P15 | [Tablet](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/Tablet.cs#L66), [ActivateTablet](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/ActivateTablet.cs#L489), [TabletAnimEvents](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/TabletAnimEvents.cs#L30), [DelidAnimEvents](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/DelidAnimEvents.cs#L38), [WCAnimEvents](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/WCAnimEvents.cs#L8), [PowerOnOffAnims](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/PowerOnOffAnims.cs#L8) | D: orientation/raise/lower/minimize/hidden phase callbacks, pause permissions and tool end/audio actions. N: [power animation](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/PowerOnOffAnims.txt#L160) selects `tPowerOn`/`tPowerOff` from ComputerSim.IsRunning; [delid audio](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/DelidAnimEvents.txt#L855) includes both one-shots and continuous audio. |
| P16 | [BenchToolSlot](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/BenchToolSlot.cs#L169), [BenchToolHandler](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/BenchToolHandler.cs#L17), [CompleteAnimScript](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/CompleteAnimScript.cs#L9), [TickAnimScript](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/TickAnimScript.cs#L8), [TickAnimator](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/TickAnimator.cs#L9), [JobTutorialAnimation](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/JobTutorialAnimation.cs#L136), [AlphaTransition](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/AlphaTransition.cs#L7) | D: tool identity, transforms, work light, hidden meshes/dust particles; animator state exit/complete hooks; animated objective ticks/tutorial hints/fades. Their callbacks must be examined separately from their visual timing. |
| P17 | [PhotoModeCameraOrbit](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/PhotoModeCameraOrbit.cs#L31), [CinematicCameraControls](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/CinematicCameraControls.cs#L7), [FreeMovement](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/FreeMovement.cs#L8), [HiResScreenShots](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/HiResScreenShots.cs#L8), [ScreenshotMenu](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/ScreenshotMenu.cs#L624) | D: camera/lighting/fan/LED controls, item preview, file save and image-editing controls. Requires testing whether controls touch a preview clone or the shared PC; no such behavior is assumed here. |

## Factor register

### Player and interaction state

| ID | Factor / multiplayer consequence | Candidate representation | Evidence |
| --- | --- | --- | --- |
| PW-001 | World position, feet anchor, interpolation and first placement | S/R; **Existing** position stream, no motion tick or scene epoch | P1 M |
| PW-002 | Body horizontal facing | S/R; **Existing** yaw only | P1 M |
| PW-003 | Player appears/disappears in workshop, on timeout, disconnect and menu return | S; **Partial**, flag and two-second freshness gate exist; scene readiness/identity absent | P1 M; P8 D/I |
| PW-004 | Look pitch and remote gaze/head direction | S/R; no remote head rig currently exists | P1 M; P2 D/I |
| PW-005 | Standing/walking/running and movement speed | S/R; position alone loses action intent | P2 D/I |
| PW-006 | Character animation rig, idle/walk/run transitions and blend phase | R; capability/asset gap, no suitable game player body proven | P1 M; P2 D/I |
| PW-007 | Camera look versus body facing while orbiting a bench/monitor | S/R or L?; avoid rotating avatar from an unrelated camera | P2/P4/P6 D/I |
| PW-008 | Player activity: walking, assembling, uninstalling, cabling, piping, LED placement, delidding, tablet use, inspection | S/R; actor + target + mode, not raw State object | P4 GameState; P6/P15/P16 D/I |
| PW-009 | Selected interactable and intended target, reach and collision eligibility | E/S; target IDs and validation; hover highlight can be L? | P3/P5 D/I |
| PW-010 | Press/hold/release, long-press threshold and canceled interaction | E/S; distinguish start/commit/cancel | P3/P4 D/I |
| PW-011 | Active operation blocks, input focus and critical modal messages | S or L?; per-actor state versus shared resource lock | P3/P4 D/I |
| PW-012 | Safe player position, recovery/teleport, spawn entry door and spawn rotation | S/E; per-actor identity + world epoch | P4/P5/P8 D/I |
| PW-013 | Personal camera FOV, orbit/zoom/pan, sensitivity and cursor lock | L?; audit their side effects on activity/target | P2/P4/P6/P17 D/I |
| PW-014 | Collision with another player and interaction obstruction by remote avatar | Decision pending; current remote capsule has no collider | P1 M; P3 D/I |
| PW-015 | Avatar/player label identity beyond the fixed Host/Client role name | S/R; roles are not durable player identities | P1 M/I |

### Carried objects, benches, storage, and workshop travel

| ID | Factor / multiplayer consequence | Candidate representation | Evidence |
| --- | --- | --- | --- |
| PW-016 | Pick up a PC: remove from original slot and assign one carrier | E/S; atomic slot-to-carrier transfer | P5/P6 D/I |
| PW-017 | Carried PC transform/orientation/scale and carrying offset | S/R; attach to actor; do not use receiver's local camera | P5 UpdateCarriedTransform/GetCarryOffset D/I |
| PW-018 | Drop a PC: target bench, occupancy checks, transform and external connections | E/S; atomic carrier-to-slot transfer | P5 CanDropCase/DropCase; P6 SetCaseInSlot D/I |
| PW-019 | Simultaneous pickup of the same PC or drop into the same empty bench | E/S; conflict policy pending; never duplicate item identity | P5 singular carry fields; P6 occupancy D/I |
| PW-020 | Package pickup/carry/drop/open/empty and its visual contents/status | E/S/R; exactly-once content transfer | P5/P7 D/I |
| PW-021 | Post-it pickup/drop, note job identity, case/package/bench attachment | E/S; relation is separate from rendered text | P5/P7 D/I |
| PW-022 | Post-it title/from/background and world visibility following a transfer | R from job/note state, or S for custom content | P7 D/I |
| PW-023 | Store/retrieve PC in cupboard and removal of its physical scene instance | E/S; storage ID remains stable across unload/reload | P5 StoreCarriedCase/GetComputerStorageIDForJob; P7 D/I |
| PW-024 | Spawn/delete a case, whole PC, package or job note | E/S; object incarnation + factory/catalog ID | P5/P6/P7 D/I |
| PW-025 | Slot category/delivery subtype, spawning stage and unlocked availability | S/R; location IDs, not scene enumeration order | P6 D/I |
| PW-026 | Bench work mode/tool assignment and active PC association | S; simultaneous independent benches versus same bench pending | P5 m_activeBenchSlot; P6/P16 D/I |
| PW-027 | Packed/ghost versus real PC/package meshes and collider availability | R/S; presentation must follow actual object state | P6 D/I |
| PW-028 | Status placard, job overlay, sale price and display refresh/dirty state | R from accepted job/PC/sale state | P6 UpdatePostIt/UpdateSalePrice/SetDirty D/I |
| PW-029 | Bench tool transform changes, WIP sign, hidden meshes and task light | R from tool operation; do not hide globally for another player's private camera | P16 D/I |
| PW-030 | Workshop door interaction, exterior shop-door state and entry point | E/S/R; determine whether transition, toggle or visual-only per door | P5 ToggleShopDoor; P8 D/I |
| PW-031 | Workshop/room/scene choice, career upgrade travel and selected DLC workshop | S/E; full scene/content identity required | P8 D/I |
| PW-032 | Scene-load completion versus delayed PC/cable/material/decorator readiness | S; async completion barrier, not active-scene name alone | P5 LoadCablesAfterDelay; P8/P9 D/I |
| PW-033 | Initial office PC, office delivery slot and endless peripheral setup | S/R; singleton initialization must not create a second logical PC | P5 SetupOfficePC/SetUpEndlessPerifs D/I |
| PW-034 | Dust accumulation as time passes/day advances | S; avoid each peer applying accumulation to the same shared PC | P5 DoDustAccumulation D/I; hardware report |

### Room customization and small environmental effects

| ID | Factor / multiplayer consequence | Candidate representation | Evidence |
| --- | --- | --- | --- |
| PW-035 | Room theme and individual wall/floor/ceiling/desk material choice | S/R; stable decoration ID/index/theme | P9 D/I |
| PW-036 | Furniture/desk/cupboard existence, model replacement and unlock/build state | S/E/R; availability affects usable slots | P9 D/I |
| PW-037 | Linked decoration changes / apply to all versus one object | E/S; group revision, avoid partly applied theme | P9 ChangeAllMesh/ChangeSingleObjectMaterial D/I |
| PW-038 | Pending decoration basket/order, delivered versus applied state and discount | S or L?; distinct transaction and world state | P9 SaveCurrentDecOrder/RestoreFromSaveList D/I; career report |
| PW-039 | AR decoration preview versus committed main-room state | S/R or L?; preview must not accidentally become a shared purchase | P9 AR flags/UpdateToMainRoom D/I |
| PW-040 | Async decoration swaps, canceled operations and stale completion handlers | S/E; reject completion for an obsolete room/theme revision | P9 D/I |
| PW-041 | Changed room collision, occluders and peripheral clipping bounds | R; regenerate from accepted geometry; verify physics matches both peers | P5 UpdateOccluders; P10 D/I |
| PW-042 | Company name and company plate position/theme/material appearance | S/R; name feeds generated textures/reflection update | P10 D/I; career report |
| PW-043 | Wall light on/off state | S/E; one accepted state, not unversioned toggle messages | P4/P11 D/N/I |
| PW-044 | Wall-switch lever/trigger, delayed transition and sound | E/R; repair final pose on late join | P11 D/N/I |
| PW-045 | Roof-light animator bools, hidden/revealed objects and fixture state | R from accepted light state | P11 D/N/I |
| PW-046 | Ceiling fan speed ramp, stopped/running state and ignoresLightSwitch flag | S/R; retain ramp start time if exact visible phase is wanted | P11 D/N/I |
| PW-047 | Ceiling fan audio/pitch and source attachment | R; do not duplicate audio on event replay | P11/P14 D/I |
| PW-048 | Lighting profiles/probes/reflections following switch or room change | R; derived from world state, graphics quality remains L? | P11 D/N/I |
| PW-049 | Case hero light, bench task lights and other activity-dependent lighting | R or L?; determine world fixture versus local inspection helper | P11/P16 D/I |
| PW-050 | Seasonal decoration selected ID, object visibility and interaction collider | S/R or L?; selection can hide another decoration | P12 D/I |
| PW-051 | Season inferred from local computer date | S or L?; do not assume both calendars/time zones match | P12 N/I |
| PW-052 | Wall clock hand position and elapsed animation time | R from chosen clock; independent deltaTime integration drifts | P13 D/N/I |
| PW-053 | Analogue clock world-time display and ticking audio | R; distinct driver from WallClock | P4/P13 D/N/I |
| PW-054 | Road vehicle spawn/despawn, selected variant/colour, progress and animator phase | S/R or L?; ambient consistency decision pending | P13 D/N/I |
| PW-055 | Road trigger recycling and positional traffic audio | R or L?; scene occurrence must be confirmed | P13/P14 D/I |

### Animation, sound, and personal presentation

| ID | Factor / multiplayer consequence | Candidate representation | Evidence |
| --- | --- | --- | --- |
| PW-056 | Footstep event, left/right variant, floor material, cadence and source position | E/R or derived gait; not present in capsule stream | P2 D/N; P14 D/I |
| PW-057 | PC power-on/off animation triggers and final powered visual state | R from simulation running state; catch up without re-booting PC | P15 N/I |
| PW-058 | Tool animation identity, current stage, speed, loop and completion | S/E/R; no raw per-bone stream assumed | P15/P16 D/I; hardware report |
| PW-059 | Animation callback that commits a tool action | E; suppress duplicate gameplay callbacks on presentation replicas | P15 AnimEndAction/WCAnimEvents; P16 D/I |
| PW-060 | PauseAllowed/PauseDisallowed animation windows | S or L?; pause policy needs owner decision | P15 D/I |
| PW-061 | Tool animation one-shot audio and start/loop/stop cleanup | E/S/R; reconnect/pause/cancel must stop the correct source | P15 N; P14 D/I |
| PW-062 | Tablet raised/lowered/hidden/minimized/fullscreen state | S/R or L?; remote physical representation is not currently authored | P15 D/I |
| PW-063 | Tablet orientation flip, mid-rotation callbacks and exit/input gating | S/E/R or L?; preserve logical phase separately from camera | P15 D/I |
| PW-064 | Dust spray particles, task effects and emitter position | R from accepted tool action; persistent dust state belongs to hardware | P16 D/I |
| PW-065 | Screw/latch/connector/part travel and hand/tool action timing | R/E; specific physical state covered in hardware report | P4 GameState SimpleScrewState; P6/P15 D/I |
| PW-066 | Persistent sound loops versus one-shot sound replay, pooling and source destruction | S/E/R; pool index is not a network ID | P14 D/I |
| PW-067 | Random sound variant, pitch and elapsed loop phase | E/R or L?; exact acoustic parity decision pending | P14 SelectRandomSound/PooledAudioSource D/I |
| PW-068 | Workshop/shop ambience choice and fade when moving between spaces | R or L?; shared theme and each listener's location differ | P9/P14 D/I |
| PW-069 | Music source, track, play/pause, shuffle/loop and playhead | S/R or L?; user file/radio availability differs | P14 D/I; software report |
| PW-070 | Audio amplitude driving visible lighting reactions | S/R or L?; identical track name does not ensure identical radio/audio samples | P14 GetCurrentAmplitude D/I; software lighting audit |
| PW-071 | Personal volume/mute, graphics detail, language and input hints | L?; no automatic decision to copy user preferences | P2/P4/P11/P14 D/I |
| PW-072 | UI objective tick, level-up/tutorial/hint animations, fade progress and notifications | R or L?; persistent progression must not depend on replaying a cosmetic animation | P16 D/I; career report |
| PW-073 | Photo/free/cinematic camera activity and whether the remote player remains visible | S/R or L?; activity and local camera transform differ | P4/P17 D/I |
| PW-074 | Photo-mode fan/LED toggles and lighting edits | S/R if shared PC affected; L? if preview clone; behavior unverified | P17 D/I |
| PW-075 | Screenshot/GIF frame capture, image edits, output filenames and file writes | L? plus content transfer only if owner wants sharing | P17 D/I; software report |

## Critical dependencies and unresolved questions

The single-player controller has one carried item set, one active bench, one selection and one camera/state stack. Two players require separating **actor activity** from the **world object being changed**; reflecting the entire singleton onto the other player would couple their cameras and controls. This is an audit implication of P3-P6, not a chosen implementation strategy.

Stable identity must survive a PC moving between hands, bench, cupboard, sale/delivery slots and an asynchronous reload. The note, job, peripheral connections and inventory provenance must continue to refer to that PC. Rebuilding a mesh cannot be treated as creating a new logical PC. See [session/restoration dependencies](session-and-consistency.md).

Animation parity has two parts: visible clip/pose/time, and the callback that changes gameplay or releases a lock. Tool replicas must not commit the action a second time. Persistent final states also need a restoration path that does not replay a purchase, boot, package-opening or completion event. Actual clip event bindings and all scene-specific animation paths require the missing asset/runtime pass.

Owner decisions before implementation: whether players may work on one PC simultaneously; who can move an occupied PC/change a bench or workshop; whether player collision exists; whether tablet screens/music/seasonal ambience are personal; whether pause freezes tools/simulation for both; whether exact ambient car/clock phases matter. The audit deliberately leaves each open.

## Future running-game checks

No fresh checks below were executed. Use disposable co-op test saves/instances only after a separate testing task is authorized.

| Check | Acceptance evidence needed |
| --- | --- |
| Two independent activities | One player walks/uses tablet while the other rotates a PC, selects parts and opens monitor UI; neither loses camera/input state. |
| Contested transfers | Both pick up one PC/note/package, or drop into one slot; exactly one identity and one content set survive; rejected actor returns to a usable state. |
| Cross-bench operations | Install/remove/connect/tool action at one bench remains independent from a second bench, including animation callbacks and cancel. |
| Mid-action join/disconnect | Join during delid, dust spray, tablet rotation, PC power animation and fan ramp; final state and lock cleanup agree. |
| Room replacement | Apply linked theme/material/desk changes while another peer is inspecting; no stale callback restores obsolete geometry or hides their independent UI. |
| Light/season/clock | Toggle lights quickly, pause during fan ramp, join with a different system date, then inspect both clock implementations and decoration exclusivity. |
| Ambient presentation | Compare traffic instances/colour, footsteps, tool loops and shop/workshop ambience; decide which differences matter before requiring exact parity. |
| Safe position and travel | Enter/exit both door types, change workshop, recover from collision, disconnect during scene load; no client applies old-world transforms. |
| Photo mode | Establish whether fan/LED/light controls affect a clone or the actual PC, and whether screenshots include the remote actor. |

## Coverage limits

This file's explicit evidence groups are the source-reading ledger for this domain. They do not imply inspection of every type in the assembly. `SimplePlayerMovement` is not substituted for the actual `Walk` component: the historical scene dump identifies `Player {CharacterController, Walk, ActivateTablet}`. No native humanoid player animation rig was established. GameState names such as HBPCTurnOn are declarations, not proof that a particular legacy/tutorial state is reachable in the shipped workshop.

The existing `<GameDir>/BepInEx/PCBS2Coop-diag/scene-Stage_02_Workshop.txt` and `LogOutput.log` were read as **historical diagnostics**. The former lists Player/Main Camera, Tablet and animated vehicle roots; the latter records host menu clicks, a loopback peer and remote capsule creation. They were not generated by this audit and do not establish synchronized PCs or current two-PC acceptance. Original game processes, saves and assets were not changed.

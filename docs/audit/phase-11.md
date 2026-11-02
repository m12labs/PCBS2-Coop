# Phase 11 audit: PC customization, RGB and visual hardware

November 2, 2026. Started at the owner's request after phase 9 (0.22, commits `e42d683` and `6051950`). Working tree clean at the start. **Audit only:** no code, no game run, no deployment. Evidence labels: **N** native ISIL, **D** decompiled declarations, **M** this mod's source. Nothing here is a running-game result; what the audit could not settle is listed under [Runtime checks](#runtime-checks-need-the-game).

## Scope (plan.md section 11)

Case paint, themes and stickers (saved primitives and brush strokes, position, rotation, scale, mirroring), customization identity and DLC resources, custom cable styles and colours, LED strips, RGB effect modes, colours, direction, speed and phase, fan flip, reverse spin and blur, and custom asset references. Music and temperature inputs to effects follow the parity policy. Exit: configuration and visible result match during editing, after commit or cancel and after save and rejoin; customizations stay on the right PC; dynamic effects use the intended shared or personal inputs and clocks.

The phase 9 refusal of the **case-modding table** ("Case modding isn't shared in co-op yet.") lands here.

## What the game does

### Case modding (paint, stickers, themes)

| Fact | Where | Evidence |
| --- | --- | --- |
| Where it opens | The tool table set to case modding: `BenchToolSlot` closure `b__0` builds `CaseModdingState(table, camera)`. It works on the PC in that table's own bench slot (`ToolSlot.Slot`). The tool pages are theme, stickers, spray paint and eraser (`UIPageState`), with undo/redo per page. | **N** [BenchToolSlot b__0](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/BenchToolSlot_NestedType___c__DisplayClass37_0.txt#L14); **D** [CaseModdingState.cs](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/CaseModdingState.cs) |
| Input | The tool reads the mouse itself (`Input.GetMouseButton`, `InputModule.GetLocalCursorPos`, ray casts onto the case). A stroke is a run of brush points; a sticker is placed, scaled and rotated with held keys. | **N** [CaseModdingState.Tick](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CaseModdingState.txt) |
| What is saved | **Not in the PC record.** The PC record has only `isCustomised`, `CustomisationID` and `CustomThemeFileName`, plus coverage numbers for career job checks. The design itself is a `CustomCaseSaveContainer` (ID, `SavedPrims`, `AllowAlphaDraw`, version, DLC packs used) in a world-level list. Each `SavedCasePrim` is a sticker, brush stroke or theme: side, position, colour, image index and name, size, scale, rotation, mirroring, eraser flag, sticker group and the stroke's points. | **D** [ComputerSave.cs:888](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/ComputerSave.cs#L888), [CustomCaseSaveContainer.cs](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/CustomCaseSaveContainer.cs#L4), [SavedCasePrim.cs](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/SavedCasePrim.cs#L4) |
| The world list | `CustomCaseManager.CustomisationList` is the live list. The save writes a copy (`CreateSaveList` → `SaveGame.customCaseSaveList`) with `m_lastCustomisationID`; loading copies it back (`RestoreFromSaveList`). | **D** [CustomCaseManager.cs:226](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/CustomCaseManager.cs#L226), [SaveGame.cs:61](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/SaveGame.cs#L61), [SaveGame.cs:124](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/SaveGame.cs#L124); **N** [RestoreFromSaveList](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CustomCaseManager.txt#L1906), [LoadGame](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/SaveLoadSystem.txt#L12109) |
| The ID | `CaseCustomisation.AddToSaveGame`: on a case's first change it sets `isCustomised` and takes `CustomCaseManager.GetNewUID()`, a per-game counter (`m_lastID + 1`). After that it replaces the case's container in the list. | **N** [AddToSaveGame](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CaseCustomisation.txt#L21480), [GetNewUID](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CustomCaseManager.txt#L750) |
| When it writes | After every finished stroke, sticker, theme and undo (`CustomUndo.End`, `AddAction`, `RemoveAction`), and once more when the tool closes (`OnStateExit`). Leaving always keeps what is on the case; only the "clear" buttons remove it. | **N** [CustomUndo.End](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CaseModdingState_NestedType_CustomUndo.txt#L3371), [OnStateExit](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CaseModdingState.txt#L3346) |
| How a case shows it | `CaseCustomisation.Update` loads the case's container from the live list by ID (`FindCaseCustomisation`, `BuildPrimsFromSave`) and bakes it into render textures. | **N** [Update](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CaseCustomisation.txt#L11833), [LoadCustomisationsFromSave](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CaseCustomisation.txt#L22791) |
| Themes and stickers | Built-in assets (`CustomCaseManager.Themes`, `StickerGroups`), some from DLC packs (`CaseTheme.DlcPack`, `MissingDlcOnLoad`). The modding table loads no player image files. `CustomThemeFileName` is only used by Steam-shared PCs (`StoredPC`, `PcSharingSaveFile`), which belong to phase 12. | **D** [CaseTheme.cs](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/CaseTheme.cs); **N** search: no file reads in `CaseModUI`/`CaseModdingState` |
| Career-only parts | The theme and sticker IDs the tool writes to the job status (`SetCompSaveThemeId`, `UpdateComputerSaveStickerIDs`) and the coverage readback (`PerformTextureReadback`) are for career job objectives. | **N** [CaseModdingState](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CaseModdingState.txt) |
| Case dirt | `CaseDirtEnabled`/`CaseDirtData` in the PC record. `CaseCustomisation.EnableDirt` has no caller in this build. | **N** search |
| Unused moves | `CaseModdingState.TransferToMainSlot` and `Packup` (which move the case without the phase 3 hooks) have no caller in this build. | **N** search |

### RGB lighting

| Fact | Where | Evidence |
| --- | --- | --- |
| Where it is set | The **Lighting** app on the PC's desktop. Every control (effect, colour, swatch, direction, speed, colour mode, group animation) applies at once through `LightingApp.OnApply`; there is no separate Apply step. Reset rebuilds the defaults (`ResetComputerLEDs`). It has three presets, and also lists the workstation's peripherals (keyboard, mouse). | **N** [OnApply](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/LightingApp.txt#L8001), [Update](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/LightingApp.txt#L9810) |
| Colour wheel | `ColourWheel.Update` reads the mouse itself (`Input.GetMouseButton`, `GetLocalCursorPos`), like Fan Control's curve points. Phase 6 does not mirror it. | **N** [ColourWheel.Update](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/ColourWheel.txt#L5170) |
| Opening it | On its first open (`!initialised`), if the bench has an orbit camera, `LightingApp.OnEnable` claims the bench camera and pushes `LightingAppState` onto the game's state stack. It does this **without checking who is at the monitor**. | **N** [OnEnable](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/LightingApp.txt#L337) (ops 204–240, 402–449); **D** `BenchSlot.orbitCamera` 0x80, `LightingApp.initialised` 0x490 |
| What is saved | `LEDConfig` (colours 1 and 2, effect, speed, offset, flow sequence ID, direction, colour mode, music and temperature modes, enabled) in the PC's software record: `ComputerSoftware.m_LEDConfig` (an `LEDDriverConfig`, keyed per part LED) and `m_LEDConfigPresets`/`m_currentPresetIndex`. That record belongs to the **boot drive** (phase 6). Peripheral lights are in `PerifSaveData.m_LEDConfig` in the workstation save. | **D** [LEDConfig.cs](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/LEDConfig.cs), [ComputerSoftware.cs:97](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/ComputerSoftware.cs#L97), [PerifSaveData.cs:17](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/PerifSaveData.cs#L17) |
| What lights up | `LEDDriver.TurnOn` (from `ComputerSim.TurnOn`, the app's `UpdateLightList` and `FanClipController`) registers each LED with **the config objects inside the software record**. Later edits change those objects in place. | **N** [LEDDriver.TurnOn](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/LEDDriver.txt#L33), [ComputerSim.TurnOn](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/ComputerSim.txt#L1492) |
| Effect clock | `LEDDriver.m_time` adds `Time.deltaTime` every frame from power-on; breathe, rainbow, strobe, heartbeat and flow read it. A strobe-like effect also uses the driver's own `Random`. Music effects read the game's music level (`UpdateMusicEffect`); temperature effects read that game's sensors. | **D** [LEDDriver.cs:187](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/LEDDriver.cs#L187); **N** [LEDDriver.Update](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/LEDDriver.txt#L6234) |
| LED strips | The strip tool (`LEDStripState`) can't be opened in this build (phase 10 finding, owner-confirmed). Strips in a loaded record are kept. | [phase 10 audit](phase-10.md#led-strips-phase-11-finding) |

### Custom cables (the Cable app)

| Fact | Where | Evidence |
| --- | --- | --- |
| Designing | `CableApp.OnAddToCart` builds a new cable kind from the chosen style, colours and pins (`PartDesc.GenerateCableID`, `CreateCustomCable`) and **adds a new part to that game's parts database** (`PartsDatabase.AddNewPart`). Custom kinds are saved in `SaveGame.customCablesSaveList`. | **N** [OnAddToCart](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CableApp.txt#L9064); **D** [PartsDatabase.cs:1209](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/PartsDatabase.cs#L1209), [SaveGame.cs:64](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/SaveGame.cs#L64) |
| Buying | `CableApp.OnBuy`: in Free Build (`GameController.m_gameMode == FREEBUILD`) the cable set goes **straight into the inventory** (`AddItemToInventory`, `CreateRemainingCables`), with no delivery; in career it is a scheduled delivery. | **N** [OnBuy](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CableApp.txt#L6754) (op 249) |
| Where it runs | The app can run on a PC desktop or on the tablet (`m_onTablet`). Whether Free Build offers it is not established (R4). | **D** [CableApp.cs](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/CableApp.cs) |

### Fans

| Fact | Where | Evidence |
| --- | --- | --- |
| Flip | `PartInstance.m_flipped`, chosen while installing. Phase 4e already carries it (`EditStart.Flipped`, replay `SetFlipped`). No other native caller sets it. | **D** [PartInstance.cs:78](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/PartInstance.cs#L78); **M** [NativeComponentEdits.cs:482](../../src/PCBS2Coop.Plugin/NativeComponentEdits.cs#L482) |
| Reverse spin | `Fan.m_reverseSpin` is only read (`Fan.Update`); nothing writes it at runtime, so it comes from the fan's prefab. | **D** [Fan.cs:547](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/Fan.cs#L547); **N** [Fan.Update](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Fan.txt#L3140) |
| Blur and spin | Drawn from each game's fan speed (phase 7 curves). Presentation. | **D** `FanBlurData` |
| Fan RGB | Part of the LED settings above. | |

### Other visual details

- **Case LCD screens** play the game's own clips (`LCDDisplay.m_clips`) while powered. Nothing is chosen by the player or saved; presentation for phase 13.
- **The tool table's type** (delid, water cooling, case modding) is in `SaveGame.BenchToolSaveList` and is not shared (phase 9 note). Using the case-modding table needs that type.

## What the mod does today (0.22)

- **Case modding is refused** with "Case modding isn't shared in co-op yet." (phase 9, [NativeToolBenches.cs:88](../../src/PCBS2Coop.Plugin/NativeToolBenches.cs#L88)).
- **The customization list travels with the save.** A joining guest, `/sync` and Continue get the host's list and counter, because they load the host's native save. A PC reload (`PcRepair`, phase 10 water reloads) keeps the list and the record's ID. Deleting a stored PC removes its container as the game does ([NativePcLifecycle.cs:294](../../src/PCBS2Coop.Plugin/NativePcLifecycle.cs#L294)).
- **Lighting is not hooked.** Phase 6 mirrors the app's buttons, toggles and swatches, but not the colour wheel. Phase 6's software sync then gets in the way ([NativeSoftwareSync.cs](../../src/PCBS2Coop.Plugin/NativeSoftwareSync.cs)):
  - the host publishes the whole boot-drive software record, LED settings included, and the guest swaps its record for the host's;
  - the guest's LEDs keep the old config objects, so **the guest sees no change until that PC powers on again**;
  - a guest's own colour change is **overwritten in the record** by the host's, while its LEDs keep showing the guest's colour until the next power-on.
- **Custom cable kinds** reach the other game only through the save at join. Phase 5 looks the cable kind up by ID when replaying a plug-in, so a kind designed mid-session fails there with "cable type unknown here" ([NativeComponentEdits.Wiring.cs:356](../../src/PCBS2Coop.Plugin/NativeComponentEdits.Wiring.cs#L356)).
- **Fan flip** is carried by 4e installs. Nothing else in this phase is hooked.

## Findings and gaps

### G1. Two games hand out the same customization ID

After a join, both games have the same `m_lastID`. If each game customizes a different case for the first time, both take `m_lastID + 1`. When the results are shared, two PCs point at one design, and one PC shows the other's paint. Phase 11 has to let the host give out IDs (or remap one), and keep both counters at least as high as every ID in use.

### G2. Case designs are on one game only

Once case modding is let through, a design changes only the user's game. The host's next save keeps the host's design; a guest's design is gone after the next `/sync`, join or Continue. The design is in the world list, not the record, so the existing PC record transfers (moves, `PcRepair`, phase 10 reloads) don't carry it either.

### G3. A design can be large

A painted case can hold many brush strokes, each with its own list of points. Phase 9's tool-table result limit (1 MB, 15 KB chunks) may not be enough for a heavily painted case (R2). The design can be compressed: it is mostly repeated floats.

### G4. Lighting changes don't reach the other game properly

See "What the mod does today": the colour wheel isn't mirrored, the host's record replaces the guest's without re-lighting, and a guest's change is lost from the record. The two games can show different colours on the same PC indefinitely, and a later power-on can switch the guest's PC to colours it never chose.

### G5. Opening Lighting may take over the other player's view

A mirrored double-click (or phase 6's open-app matching) opens Lighting on the other game. On its first open, the app pushes `LightingAppState` there **whoever is at the monitor**, which would pull the other player into the lighting view. This is from the native code only (R1); the owner's phase 6 test may not have opened Lighting.

### G6. Effect timing differs

Each game's LED clock starts at its own power-on. A guest's PC powers on about 90 s after a load, so breathe, rainbow, strobe and flow effects are out of step on the two games, even with identical settings. Music effects follow each game's own music. Temperature effects follow each game's sensors, which phase 7 already accepts as slightly noisy.

### G7. Custom cable sets are on one game only

Buying a set in Free Build adds a new cable kind to the parts database and the set to the inventory on that game only. Installing one of those cables then fails on the other game (phase 5 lookup).

### G8. Tool table type and table claim

Case modding needs a table set to case modding. Switching a table's tool isn't shared, so the other game may show a delid table where this game has a modding table. The phase 9 claim (one player per table, "Someone is using the … table.", the label under the name) already covers delid and water cooling and can take case modding as a third tool.

### G9. Missing DLC

A sticker or theme from a DLC pack shows on a game without that pack as the game's own missing-DLC case (`MissingDlcOnLoad`). The owner has no DLC installed, so this can't be tested here.

## Factor dispositions

| Factor IDs | Disposition |
| --- | --- |
| HW-036–039, HW-061–063 (theme, paint, stickers, saved primitives, strokes, transforms, mirroring) | 11c: share the finished design (the case's container) when the modding tool closes. |
| HW-059, HW-060 (brush and sticker gestures, cursor) | Local tool state; never shared (question 1). |
| HW-077 (customization ID and save list) | 11b: host-given IDs and counters (G1). |
| HW-064 (DLC dependencies, missing assets) | Question 6; carried in the container either way. |
| HW-016 (case dirt) | Carried in the PC record; no game path changes it in this build. |
| HW-040 (custom geometry sides) | Derived from the case; nothing to share. |
| HW-042, HW-045, HW-050 (LED membership, colours and driver settings, peripheral lights), SW-012 (lighting part) | 11d: share the LED settings and re-light in place (question 2). |
| HW-067 (fan spin and blur), PW-070 (audio-driven lighting) | Presentation from shared inputs; effect clock per question 3; music follows each game's music until music is shared (phases 12–13). |
| HW-043 (LED strips) | Unreachable in this build (phase 10); records keep them. |
| HW-026, HW-027, SW-049 (custom cable kinds, colours, the Cable app) | 11f per question 4. Phase 5 already carries a cable's colours once both games know its kind. |
| HW-044, HW-065, HW-066 (fan install, direction, flip, reverse spin) | Already carried by 4e (flip) or fixed by the prefab (reverse spin). Nothing new. |
| PW-008, PW-029 (activity, bench tool, WIP sign) | 11c: case modding joins the phase 9 table claim and label. |
| PW-074 (photo-mode fan and LED toggles) | Personal view; phase 13 if needed. |
| Case LCD clips, `CustomThemeFileName`, Steam-shared PCs | Phase 13 (clips) and phase 12 (PC sharing and import). |

## Proposed tasks (final order after the owner's answers)

- **11a.** This audit and the owner's answers.
- **11b. Customization IDs:** the host gives out IDs. A case's first design is sent without an ID; the host picks one above both games' counters, and both games set the case's `CustomisationID`/`isCustomised` and raise their counter to it. Model tests for clashes and for a join in between.
- **11c. Case-modding table:** a third tool in command 15 (one player per table, "Someone is using the case modding table.", label "Case modding"). When the tool closes, the case's container (compressed, in chunks, hash-checked) goes to the other game. That game puts it in its live list, under the host's ID, and redraws the case (R3: the case's own reload from the list, or a reload of that PC from the table). A refusal or drop mid-session shares nothing. Lift the phase 9 refusal.
- **11d. Lighting result:** after a Lighting change settles (about 0.5 s, like Fan Control), send the PC's LED settings (per-LED config, presets, preset index) and the workstation's peripheral lights. The other game copies them into its existing config objects (`LEDConfig.SetValues`), updates flow timings, refreshes an open Lighting app, and re-lights if needed (R5). Teach phase 6's software check to treat an accepted LED change as settled, so it stops overwriting a guest's colours.
- **11e. Lighting opening on the other game:** when the app opens from the other player's mirrored click or from app matching, don't push `LightingAppState` there; the app shows on the monitor only (after R1 confirms the problem).
- **11f. Cable app** (question 4): when a set is bought, send its new cable kind (`PartDesc`) and the inventory items; the other game adds the kind (`PartsDatabase.AddCustomCable`) and the same items. Installing those cables then works through phase 5.
- **11g. Effect clock** (question 3): the host sends each powered PC's LED clock; the other game sets its driver's `m_time` to match (on change, on join and now and then).
- **11h. Tool table type** (question 5): share switching a table's tool, refused while someone uses that table.
- **11i. Tests and version:** model tests, Release build, version bump (wire and save formats change, so 0.23).
- **11j. Two-game acceptance** (the owner's test).

## Acceptance cases (two games)

1. Each player paints a case, adds stickers and a theme at the case-modding table, then closes it. The other game shows the same design. Save, Continue and a guest join show it again.
2. Both players customize a different, never-customized case one after the other; each PC keeps its own design on both games (the ID clash, G1).
3. Undo, redo and the clear buttons at the table, then close: the other game shows what was left on the case.
4. While one player uses the case-modding table, the other is refused there ("Someone is using the case modding table."), sees "Case modding" under the name, and can't move the PC on it.
5. A heavily painted case (many strokes) shares within the limit.
6. Each player changes colours (wheel and swatches), effects, speed and direction in Lighting on a running PC. The other game's LEDs change without a restart; power off and on, then Continue, keep the colours.
7. Lighting opened by one player doesn't change the other player's view.
8. Lighting presets save and load from either player; peripheral (keyboard, mouse) colours match.
9. Breathe or rainbow on a PC after a guest joins: the pulse is in step on both games (if question 3 = a).
10. (If question 4 = a) Buy a custom cable set; it is in both inventories; plug one of its cables in from each side.
11. Regression: phase 9 delid and water tables, 4e installs with fan flip, phase 6 desktop mirroring and software sync, `/sync`.

## Runtime checks (need the game)

- **R1.** Open Lighting on a PC from one game: does the other game's player get pulled into the lighting view? (Does the phase 6 owner test cover it?)
- **R2.** How large a painted case's container gets (bytes, after compression), for the chunk limit.
- **R3.** After its container is swapped in the live list, does a case redraw by itself (`CaseCustomisation.Update`), or does it need a reload of that PC?
- **R4.** Is the Cable app in Free Build (PC desktop or tablet), and does buying add the set to the inventory at once?
- **R5.** Does copying settings into the existing config objects re-light a running PC by itself, or is `LEDDriver.TurnOn` (or the app's light-list refresh) needed? Does that have side effects (power, fans)?
- **R6.** Are the LED setting keys the same on both games for the same PC (part path and index)?
- **R7.** Which tool tables the default workshop has, and whether a table can be switched to case modding in Free Build (overlaps phase 9 R5).

## Questions for the owner

1. **Case-modding table:** (a, recommended) result only: when the player closes the table, the other game shows the finished design; one player per table, with "Someone is using the case modding table." and "Case modding" under the name; (b) show each stroke and sticker live on the other game (much more traffic and work); (c) keep refusing case modding in co-op.
2. **Lighting app:** (a, recommended) share each change after a short pause; the other game's lights change in place with no restart, and if both players edit the same PC, the last change wins; (b) share only when the Lighting app closes; (c) one player at a time in Lighting on a PC.
3. **Animated lighting effects** (breathe, rainbow, strobe, flow): (a, recommended) keep them in step, following the host's timing; (b) each game runs its own timing (a guest that joins later can be well out of step). Either way, music-reactive lighting follows each game's own music until music is shared later, and temperature lighting follows each game's own sensors.
4. **Cable app** (custom cable sets): (a, recommended) share purchases: the bought set appears in both inventories and its cables work on both games; (b) refuse the Cable app in co-op for now. Have you seen the Cable app in Free Build, on a PC or on the tablet?
5. **Switching a tool table** between delid, water cooling and case modding: (a, recommended) share it, refused while someone is using that table; (b) leave it unshared (each game keeps its own table type).
6. **DLC stickers and themes** when only one player owns the DLC: (a, recommended) allow them; the game without the DLC shows the game's own missing-DLC handling and keeps the design intact; (b) refuse DLC stickers and themes in co-op unless both players have that DLC.
7. **Testing:** (a, recommended, as before) I code with model tests, then you test everything in game; (b) I also run short owned two-game checks myself first.

## Owner decisions (November 2, 2026)

1. **Case-modding table:** (a) result only: when the table closes, the other game shows the finished design; one player per table, with "Someone is using the case modding table." and "Case modding" under the name. Note for later: painting together on one case (live co-op painting) may come in a future phase.
2. **Lighting app:** (a) each change is shared after a short pause; the other game's lights change in place, with no restart; the last change wins.
3. **Animated effects:** (a) kept in step with the host's timing. Music-reactive lighting follows each game's own music until music is shared; temperature lighting follows each game's sensors.
4. **Cable app:** (a) purchases are shared: the set appears in both inventories and its cables work on both games.
5. **Tool table type:** (a) switching a table's tool is shared, refused while someone is using that table.
6. **DLC stickers and themes:** (a) allowed; the game without the DLC uses its own missing-DLC handling and keeps the design.
7. **Testing:** (a) code with model tests; the owner tests in game. Later the owner will ask for owned two-game checks of everything still unchecked, not now.

## Implementation (0.23)

Version **0.23** (network messages only; 0.23 reads 0.22 saves, `CoopSavePair.SameFormat`).

- **Case-modding table (11b, 11c)**, command **15** ([ToolBenches.cs](../../src/PCBS2Coop.Network/ToolBenches.cs)):
  - `BenchTool.CaseModding`; "Someone is using the case modding table."; "Case modding" under the user's name. The phase 9 refusal is gone.
  - `Leave` now names its tool. `ToolBenchWorld` is canonical: while accepting a case-modding leave with a result, the host adds the design number (`IToolBenchNative.DesignId`).
  - Plugin ([NativeToolBenches.Designs.cs](../../src/PCBS2Coop.Plugin/NativeToolBenches.Designs.cs)): the session remembers the PC on the table and its design's hash. After the tool closes, the case's `CustomCaseSaveContainer` is serialized by the game's own formatter, compressed and sent in chunks. Nothing is sent when the design didn't change, or when leaving only left an empty design on a case that had none (the game always saves one on exit).
  - Design numbers: the host keeps the PC's own number when no other PC there uses it; otherwise it gives one above every number it knows and the editor's own. Each game raises its counter (`m_lastID`) to it, moves any other PC that uses that number to a fresh one of its own (`Free`), and the editor renames its design if needed. The other game replaces the design in its list, sets `isCustomised`/`CustomisationID`, and reloads the PC on its bench (`ReloadOnBench`) so it is drawn. A PC that isn't on a bench there shows the design when it is next loaded.
- **Table switching (11h)**, command 15 step `Switch` ([NativeToolBenches.Switch.cs](../../src/PCBS2Coop.Plugin/NativeToolBenches.Switch.cs)):
  - The desk menu (`DeskFunctionChoice.SetUpNewTool`, and `ClearPreviousTool` for "work on the PC") is refused locally while the other player uses that table, and sends the new type (`BenchToolSlot.Type`, or 4 for none).
  - The other game repeats the menu's steps on that workstation (computer and monitor placement, `InitMonitorSlot`, `BenchToolSlot.SetTool`), without the menu's sound and `GoBack`.
  - A refused switch puts the table back on the requester's game.
- **Lighting (11d, 11e)**, command **16** `CustomizationWorld` ([Customization.cs](../../src/PCBS2Coop.Network/Customization.cs), [NativeCustomization.cs](../../src/PCBS2Coop.Plugin/NativeCustomization.cs)):
  - `Lights` carries up to 128 lights (group key, index, every saved `LEDConfig` value) for a PC's live lights (with the preset index), one preset, or one peripheral slot at its workstation.
  - Sending: the Lighting app's `OnApply`, `OnReset`, preset save/load and power button schedule a send 0.5 s later, only on the game of the player at that monitor and not for mirrored input. Each set is sent only when it differs from the last one sent.
  - The other game copies the values into its own light objects (lights read them every frame). It rebuilds with `LEDDriver.TurnOn` when a flow, effect, animation or on/off changed, or refreshes an open Lighting app with `UpdateLightList`.
  - Phase 6's software sync now keeps the live light objects when it adopts the host's record (`KeepLights`), so the lights follow and later edits are still saved.
  - Opening Lighting for someone else (mirrored input, open-app matching, or a monitor this player isn't at): `LightingApp.OnEnable` runs without its `BenchSlot.ClaimCamera` and `LightingAppState` push. `initialised` is reset so this player's own first open still sets up the view.
- **Effect clock (11g)**, command 16 `Clock`: the host sends each powered PC's `LEDDriver.m_time` when a guest arrives, when that PC's lights restart, and every 30 s. The guest keeps its clock within 0.15 s of the host's.
- **Cable app (11f)**, command 16 `Chunk`/`Purchase`:
  - `CableApp.OnBuy` on the buyer's game sends the new custom cable kinds (`PartDesc`, the game's formatter) and the inventory change (phase 9's format).
  - The other game registers the kinds (`AddNewPart`, `AddCustomCable`) and makes the same inventory change.
  - A mirrored Buy click does nothing on the other game.
- **Inventory binding**: custom cable items are marked as non-stock (like phase 9's variants), and a 0.22 binding still matches.

**Model evidence:** **677/677** checks (case modding: prompt, tool check, host design number, empty and unusable answers; switching: shared, refused while busy, unknown types; lights: all values, presets, peripherals, refusals, malformed values, a full batch within the payload limit; clock: host only; purchases: chunks, hash, once, a guest that missed part). Release build 0 warnings, 0 errors; deployed and hash-checked. **Nothing ran in game.**

### Not checked in game yet

- Everything above. In particular:
  - R1: whether a mirrored Lighting open really took over the other player's view before (the guard is in either way);
  - R3: whether reloading the PC on the case-modding table draws the design;
  - R2: how large a heavily painted case gets (limit 1 MB compressed);
  - R5: whether `LEDDriver.TurnOn` on a running PC has side effects;
  - R6: whether light group keys are the same on both games (lights that don't match are logged as "not on this PC here");
  - whether the other game's open Lighting app re-applies its own stale colour after a mirrored click (the editor's next send corrects it);
  - R4: whether the Cable app is in Free Build at all;
  - whether the game's Free Build save keeps bought cable sets;
  - whether repeating the desk menu's steps leaves the other game's workstation looking the same (computer and monitor placement).
- Live co-op painting on one case (owner note) is not part of this phase.

### Owner test (0.23)

1. Switch a table to case modding from each side; the other game's table switches too. While the other player uses it, switching is refused with the prompt.
2. Paint a case, add stickers and a theme, close the table. The other game shows the same design (that PC reloads on the table). Save, Continue and a guest join show it again.
3. Each player customizes a different, never-customized case one after the other; each keeps its own design on both games.
4. While one player uses the case-modding table, the other is refused there, sees "Case modding" under the name, and can't move that PC.
5. Lighting on a running PC from each side: colour wheel, swatches, effects, speed, direction, presets, peripherals. The other game's lights change without a restart; power off and on and Continue keep them.
6. Lighting opened by one player doesn't change the other player's view.
7. Breathe or rainbow after a guest joins: in step on both games.
8. If the Cable app is there: buy a custom cable set; it is in both inventories; plug one of its cables in from each side; Save and Continue.

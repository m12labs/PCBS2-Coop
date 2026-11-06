# Phase 12 audit: workshop environment, remaining Free Build apps and content

November 2, 2026. Started at the owner's request after phase 11 (0.23, commit `7115432`). Working tree clean at the start. **Audit only:** no code, no game run, no deployment. Evidence labels: **N** native ISIL, **D** decompiled declarations, **M** this mod's source. Nothing here is a running-game result; what the audit could not settle is listed under [Runtime checks](#runtime-checks-need-the-game).

## Scope (plan.md section 12)

The playable Free Build space around the PCs:
- decorating (materials, furniture, desks, theme), the AR preview versus the real room, the company plate;
- doors, lights, ceiling fans, seasonal decorations, clocks, traffic and ambience, as the owner selects;
- the remaining tablet apps (notes, music, catalogue, customization views) and peripheral variants;
- the custom content boundary: custom wallpapers, GIFs, themes and imported PCs;
- workshop and DLC support at the point the owner approves.

Exit: room changes keep PC, bench and input associations; async swaps can't restore stale state; presentation matches the chosen scope; content availability and fallbacks are defined; every new shared fact survives save and rejoin.

A standing owner decision applies (plan.md, "Custom content"): **custom wallpapers, GIFs, themes and imported-PC content are transferred automatically within the co-op session; local paths alone are not content identity.**

## What the game does

### Decorating the workshop

| Fact | Where | Evidence |
| --- | --- | --- |
| The tool | The tablet's **Customise Workshop** app (`CustomiseWorkShopApp`): pick an area, then a material, mesh or prefab (`OnClickMaterialSelect`), or "apply to all" (`ApplyToAll`); themes dropdown; a colour wheel for the workshop lights. It refuses unless the player is walking (`GiveNonWalkingError`). | **N** [OnClickMaterialSelect](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CustomiseWorkShopApp.txt), [ApplyToAll](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/CustomiseWorkShopApp.txt) |
| What changes | `Decorator` (singleton): `ChangeSingleObjectMaterial`, `ChangeAllObjectMaterials`, `ChangeMesh`, `ChangeAllMesh`, `ChangeWallDeskCeiling`, `LoadPrefab`. Meshes, materials and prefabs load **asynchronously** (Addressables; `Loading`, `OnLoadComplete`). A theme also changes ambient and chair audio (`ChangeAmbientAudio`), the company plate's position and scene occluders. | **D** [Decorator.cs](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/Decorator.cs) |
| AR preview | The app's calls pass `ForApp`/`UpdateAR` (true). `UpdateToMainRoom` is only called when a save is loaded (`RestoreFromSaveList`, the loading screen). Whether a Free Build change shows in the real room at once, or only after a reload, isn't settled statically (R1). | **N** callers of [UpdateToMainRoom](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Decorator.txt) |
| Saved | `SaveGame.decoratorSaveList` (`DecoratorListLink`: object ID, selection index, current index, type, theme, DLC flag), written by `Decorator.CreateSaveList`, loaded by `RestoreFromSaveList`. Career orders are separate (`decoratorSavedOrder`, `decTransaction`). | **D** [SaveGame.cs:55](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/SaveGame.cs#L55), [DecoratorListLink.cs](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/DecoratorListLink.cs) |
| Light colour | `ThemeManager.SetLightColor`, called from the colour wheel; saved as `themeManagerColour`/`themeManagerPulse` (plus the AMD theme colours). | **D** [SaveGame.cs:88](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/SaveGame.cs#L88); **N** ColourWheel `OnEnable` lambdas |
| Company plate | `CompanyNamePlate` renders a name to a texture and moves with the theme. The name looks career-only (`RenameBusinessOnboarding`); what Free Build shows is R4. | **D** [CompanyNamePlate.cs](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/CompanyNamePlate.cs) |

### Room fixtures and ambience

| Fact | Where | Evidence |
| --- | --- | --- |
| Lights | `LightSwitch.SetOn(on, animate, cachedTextures)` from its interaction (`b__20_0`), `Start`, `GameController.Update` (R7) and the water-cooling table (`ModifyCeilingLights`). It toggles roof lights, hidden/shown objects and the switch animation, and ramps the **ceiling fans** (`RampCeilingFanSpeeds`; a fan can ignore the switch). **Not saved.** | **N** [LightSwitch](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/LightSwitch.txt#L394) |
| Doors | `WorkshopDoor` has `DisableInFreebuild`. Not usable in Free Build. | **D** [WorkshopDoor.cs](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/WorkshopDoor.cs) |
| Seasonal decorations | Three switches in the room (`DefaultDecorationSwitch`, `ChristmasDecorationSwitch`, `HalloweenDecorationSwitch`, base `DecorationSwitch.OnClickSwitch`): one is active, the others hide. The active one is saved (`SaveGame.m_activeDecoration`). The season comes from each game's own date (`GameController.FindCurrentSeason`, `Season` = standard or Christmas). | **D** [DecorationSwitch.cs](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/DecorationSwitch.cs), [SaveGame.cs:52](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/SaveGame.cs#L52) |
| Clocks | `WallClock` turns its hands by `Time.deltaTime` from when its scene loaded; `AnalogueClock` reads the workshop's time. Both games show different times. | **N** [WallClock.Update](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/WallClock.txt) |
| Traffic, ambience | `Vehicles`/`Vehicle` spawn cars outside with their own randomness; `AmbienceManager` follows the room theme. | **D** |

### Remaining apps

| App | What it does | Evidence |
| --- | --- | --- |
| Music (tablet) | `MusicManager`: the game's soundtrack, the player's own music files, or internet radio. Every setting is a per-install preference (`PlayerPrefs` keys), not part of the save. | **D** [MusicManager.cs](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/MusicManager.cs) |
| Notes (tablet) | `NotesAppCloud` keeps notes in memory. Its `SetDataFromSaveGame` has **no caller**, so notes aren't saved. | **N** search |
| GIF / camera | `GifApp`/`GifEncoder` write `.gif` files to disk. Nothing in the world reads them, except that a player could pick one as a custom wallpaper. | **N** [GifEncoder](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/GifEncoder.txt) |
| Cable app, Lighting, Fan Control, BIOS, GPU Tuner | Done in phases 6, 7 and 11. | |
| Airflow and others | Personal tablet views (phase 6 decision: tablet use is personal). | |

### Custom wallpapers

| Fact | Where | Evidence |
| --- | --- | --- |
| Choosing | The Select Wallpaper app (on the PC's desktop) has stock wallpapers, the player's **Windows wallpaper** ("USER_WALLPAPER") and a **custom** tab, which is a file browser over the player's own disk (`FileExplorer`, `OnApplyCustomWallpaper` → `OS.SetCustomWallpaper(sprite, path, aspect)`). | **N** [OnApplyCustomWallpaper](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/SelectWallpaperApp.txt#L2479) |
| Saved | The PC's software record (`m_wallpaper`, `m_userWallpaper`, `m_preserveWallpaperAspect`): a resource name, "USER_WALLPAPER", or a **file path on the chooser's machine**. | **D** [ComputerSoftware.cs:88](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/ComputerSoftware.cs#L88) |
| Showing | `OS.GetWallPaperSprite`: "USER_WALLPAPER" reads **this machine's** Windows wallpaper from the registry (`UserWallPaper.Get`); a path is loaded only if the file exists here (`Tools.FileExists`, `LoadSpriteFromFilePath`). | **N** [GetWallPaperSprite](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/OS.txt#L10943), [UserWallPaper.Get](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/UserWallPaper.txt#L3) |

### Importing and exporting PCs

| Fact | Where | Evidence |
| --- | --- | --- |
| Local PC files | The cupboard (`Storage.Populate`) also lists PC files from the player's own folder ("Found Local PC"), each a `LocalPcSave` (the PC record and its case design), read with the game's formatter. Picking one up imports it. | **N** [Storage.Populate](../../artiffacts/decompiled/native/isil/IsilDump/Assembly-CSharp/Storage.txt); **D** [LocalPcSave.cs](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/LocalPcSave.cs) |
| Export | `StorageUserSharing.OnActualSave` writes a PC to a file (`PCFileSharing.ExportPcXorMethod`), with a captured image; Steam upload is `StoredPC.UploadToSteam`. Only the player's own disk or Steam account changes. | **D** [StorageUserSharing.cs](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/StorageUserSharing.cs), [PCFileSharing.cs](../../artiffacts/decompiled/metadata/csharp/DiffableCs/Assembly-CSharp/PCFileSharing.cs) |
| Shared-PC themes | Steam-shared PCs carry `CustomThemeFileName` (phase 11 finding); those arrive through import. | |

### DLC workshops

`DecoratorListLink.isDlcItem`/`SteamDlcPack` and the themes include DLC content. The owner has no DLC installed (phase 3), so none of it can be tested here.

## What the mod does today (0.23)

- **Nothing in phase 12 is shared.** No plugin code references `Decorator`, `CustomiseWorkShopApp`, `LightSwitch`, `DecorationSwitch`, `ThemeManager`, `MusicManager`, `NotesAppCloud`, the wallpaper app or `StorageUserSharing` (**M**, search of `src/`).
- **Joins and `/sync` load the host's save**, so the guest gets the host's decorations, light colour and seasonal decoration then. Lights, notes and music aren't saved, so each game keeps its own.
- **Importing is refused by accident:** a file PC picked up from the cupboard has no co-op identity, so the phase 3 retrieve hook says "That stored PC has no co-op identity." ([NativePcTransfers.cs:84](../../src/PCBS2Coop.Plugin/NativePcTransfers.cs#L84)). Exporting works and only writes the player's own file.
- **Wallpapers go through phase 6.** The wallpaper app's clicks are mirrored, and the host's software record (with its wallpaper value) replaces the guest's.
- Packages and physical notes are refused (phase 3g).

## Findings and gaps

### G1. Decorating changes one game only

A player's decorating changes only their own game. The host's save keeps the host's room; a guest's decorating is gone after the next `/sync`, join or Continue. The changes load asynchronously, so applying them on the other game has to wait for each load and must not let an older one finish last (PW-040). Room changes also move the company plate and occluders, so PC, bench and peripheral positions must stay as they are (to check, R8). Whether the app shows its result at once (R1) decides when a change is "done".

### G2. Lights, fans and seasonal decorations are per game

The light switch (with the ceiling-fan ramp) and the seasonal switches change one game only. Lights aren't saved, so after a join both games keep their own. The season comes from each game's own date, which can differ around midnight or between time zones.

### G3. Custom wallpapers can't work across two machines

- A custom wallpaper is a file path on the chooser's machine. The other game doesn't have the file and falls back.
- "USER_WALLPAPER" shows each player's own Windows wallpaper, so the two games show different pictures with the same setting.
- **The mirrored file browser picks a file from the other player's disk.** Phase 6 mirrors the click by position, and the other game's file list is that game's own folder. The other game may therefore apply an unrelated image of its own, and the host's software record then replaces the guest's (R3).

The standing decision asks for the image to be transferred.

### G4. Imported PCs

Importing is refused only by accident, with a technical message. The standing decision asks for imported PCs to work and to carry their content (the record, the case design, and a Steam PC's theme file).

### G5. Personal apps

Music, notes, GIF capture and the other tablet views are personal by design (phase 6's tablet decision). Notes aren't saved by the game itself. Music-reactive lighting (phase 11) follows each game's music until music is shared.

### G6. Ambient presentation drifts

Wall clocks, traffic and the ceiling-fan ramp phase differ between games. Phase 13 owns presentation parity.

## Factor dispositions

| Factor IDs | Disposition |
| --- | --- |
| PW-035–038, PW-040, SW-050, SW-051 (decorations, apply to all, async swaps, light colour) | 12b per question 1. Career orders and discounts (PW-038's basket) belong to career (phases 15–17). |
| PW-039 (AR preview versus room) | 12b: share the result only, never the preview (R1). |
| PW-041 (room collision and occluders) | Follows from the accepted decoration; R8. |
| PW-042 (company plate) | Moves with the theme; the name is career (R4). |
| PW-030 (doors) | Not usable in Free Build. |
| PW-043–046 (light switch, roof lights, fans) | 12c per question 2. |
| PW-047, PW-048 (fan audio, light probes) | Follow from the shared light state. |
| PW-050, PW-051 (seasonal decorations, season) | 12c per question 3. |
| PW-052–055, PW-068 (clocks, traffic, ambience) | Question 7: personal presentation for now, phase 13 later. Ambience follows the theme. |
| PW-069, PW-070, SW-019 (music, audio-driven lighting) | Question 4. |
| SW-018 (notes) | Question 7: personal. |
| SW-008, SW-048 (wallpapers, custom content identity) | 12d: transfer the image (standing decision; question 5 confirms the details). |
| SW-024–027, PW-075 (GIF capture and output) | Personal output; a GIF used as a wallpaper is covered by 12d. |
| SW-033, CP-027, CP-060, CP-069 (import, export, sharing) | 12e per question 6. Export and Steam upload stay personal. |
| CP-028, HW catalog/DLC (DLC workshops and items) | Not in this phase (no DLC to test); DLC items in a loaded save are kept as they are. |
| PW-074, PW-075 (photo mode) | Personal (phase 13 if anything). |

## Proposed tasks (final order after the owner's answers)

- **12a.** This audit and the owner's answers.
- **12b. Decorating:**
  - each applied change is host-ordered: the object or group, the new index and theme, the light colour;
  - the other game applies it through `Decorator`, waits for the async load and drops a stale one;
  - one player decorating at a time ("Someone is decorating the workshop."), with a label;
  - the save already carries the result.
- **12c. Lights and seasonal decorations:**
  - the light switch and seasonal switches are host-ordered, and the other game calls the game's own `SetOn` / `OnClickSwitch`;
  - the host sends its light state when a guest arrives (lights aren't saved);
  - the season follows the host's.
- **12d. Custom wallpapers:**
  - when a player sets a custom or Windows wallpaper on a PC, their game sends the image (size limit, chunked, by SHA-256);
  - each game keeps the image in a co-op content folder, and the host keeps it with the world for Continue and joins;
  - `OS.GetWallPaperSprite` shows the shared image for that PC;
  - the mirrored file-browser apply on the other game is skipped;
  - a missing image falls back to the stock wallpaper with a notice.
- **12e. Importing PCs:**
  - picking a file PC from the cupboard sends its record and case design to the host;
  - the host creates it as a new shared PC (origin `Imported`) in the importer's hands, and both games add the design under a host-given number (phase 11);
  - a Steam PC's theme file is sent like a wallpaper;
  - export stays personal.
- **12f. Music** (only if question 4 = b): share the game's own soundtrack track, play/pause and position. User files and radio stay personal.
- **12g. Tests and version:** model tests, Release build, version bump (0.24; the content folder may change the save pair).
- **12h. Two-game acceptance** (the owner's test).

## Acceptance cases (two games)

1. Each player decorates (one object, apply to all, a theme, the light colour). The other game's room changes the same way, PCs and benches stay where they are, and Save, Continue and a join keep it.
2. While one player decorates, the other is refused in the app with the prompt.
3. Either player flips the lights: both games change, ceiling fans ramp on both; a guest joining sees the host's light state.
4. Either player switches the seasonal decoration: both games show it, after Continue too.
5. Set a custom wallpaper (own file) and the Windows wallpaper on a PC, from each side: both games show the same picture, after Continue and a join too; a very large image is refused with a message.
6. Import a PC file from the cupboard, from each side: it arrives in the importer's hands on both games with its case design; store it, Continue, join.
7. Music, notes and GIF capture stay each player's own (if question 4 = a and question 7 = a).
8. Regression: phases 3–11, especially PC moves, the case-modding table and `/sync`.

## Runtime checks (need the game)

- **R1.** In Free Build, does picking a material in Customise Workshop change the real room at once, or only the app's AR view until something else happens?
- **R2.** Which tablet apps Free Build offers.
- **R3.** What the other game does today with a mirrored custom-wallpaper apply (which file it picks).
- **R4.** What the company plate shows in Free Build.
- **R5.** Where Steam-downloaded PCs appear (the same cupboard list?), and whether picking a file PC puts it in the hands or in the cupboard.
- **R6.** How `FindCurrentSeason` decides the season, and when the Halloween switch shows.
- **R7.** Whether `GameController.Update` switches lights by a key or a game event.
- **R8.** Whether a theme change moves anything PCs or peripherals depend on (bench positions, occluders).

## Questions for the owner

1. **Decorating the workshop** (materials, furniture, themes, light colour): (a, recommended) shared: each change you apply shows on the other game too, and only one player decorates at a time ("Someone is decorating the workshop."); (b) refuse decorating in co-op for now; (c) leave it as it is (one game only). Also: when you decorate in Free Build, does the room change right away?
2. **Light switch** (room lights and ceiling fans): (a, recommended) shared: either player flips them for both games; (b) each player has their own.
3. **Seasonal decorations** (default, Christmas, Halloween switches): (a, recommended) shared, and the season follows the host's date; (b) each player's own.
4. **Music:** (a, recommended) each player keeps their own music (their own files and radio can't be shared anyway); (b) share the game's own soundtrack (same track and position on both games), with own files and radio staying personal.
5. **Custom wallpapers** (the plan already says custom content is sent automatically): (a, recommended) when a player sets their own image or their Windows wallpaper on a PC, the picture is sent to the other game and kept with the co-op world; images over 8 MB are refused with a message; (b) refuse custom and Windows wallpapers in co-op, stock wallpapers only.
6. **Importing PCs from files** (the cupboard's local PCs, including Steam downloads): (a, recommended) shared: the imported PC arrives in the importer's hands on both games, with its paint job; exporting stays your own file; (b) keep importing refused in co-op, with a clear message.
7. **Small personal things** (wall clocks, traffic outside, the Notes app, GIF capture): (a, recommended) each player's own for now; matching clocks and traffic waits for phase 13; (b) share some of them now (say which).
8. **Testing:** (a, recommended, as before) I code with model tests, then you test in game; (b) I also run short owned two-game checks first.

## Owner decisions (November 2, 2026)

1. **Decorating:** (a) shared; each applied change shows on both games; one player decorates at a time ("Someone is decorating the workshop."). The question whether the room changes right away wasn't answered (the owner was away); R1 is settled in owned runs.
2. **Light switch:** (a) shared, lights and ceiling fans.
3. **Seasonal decorations:** (a) shared; the season follows the host's date.
4. **Music:** (a) each player's own.
5. **Custom wallpapers:** (a) the picture (own image or Windows wallpaper) is sent to the other game and kept with the co-op world; over 8 MB is refused with a message.
6. **Importing PCs:** (a) shared: the imported PC arrives in the importer's hands on both games with its paint job; export stays personal.
7. **Small personal things** (clocks, traffic, Notes, GIF capture): (a) each player's own for now.
8. **Testing:** (b) model tests, then my own owned two-game checks (the owner is away and said so).

## Implementation (0.24)

- **Command 17, `WorkshopWorld`** ([Workshop.cs](../../src/PCBS2Coop.Network/Workshop.cs)):
  - `Decorate` claims or frees decorating (one player at a time, "Someone is decorating the workshop.", "Decorating" under the name);
  - `Decor` carries the decorations that changed (type, object number, pick, theme, DLC pack) and the light colour;
  - `Lights` and `Decoration` are the light switch and a seasonal switch;
  - `Chunk`/`Content` send a picture or an imported PC by SHA-256 (8 MB at most); `Wallpaper` sets a PC's shared picture once the host has it; `Need` is a guest asking the host for content it lacks.
  - Its world state (optional, so 0.23 saves load) keeps the lights, the seasonal decoration, the host's season, who is decorating and each PC's shared picture.
- **PC import** is a new `PcLifecycle` action (`Import`, `IPcImportNative`): both games describe the PC from the same content, the host gives it IDs (origin `Imported`), and it arrives in the importer's hands.
- **Plugin:**
  - [NativeWorkshop.cs](../../src/PCBS2Coop.Plugin/NativeWorkshop.cs):
    - Opening and closing the Decorator app claims and frees decorating. While it is open, the game's own decoration list (`CreateSaveList`) and the light colour are compared every half second, and changes are sent.
    - The other game gives its matching objects (`ThemeManager.DecoratorLinks` by type and number) the same pick and theme, as the first step of the game's own restore does. The light colour is set as the colour wheel sets it.
    - Also hooked: `LightSwitch.SetOn` (the switch click and the lights key; the water-cooling table's dimming stays local) and `DecorationSwitch.OnClickSwitch`.
    - The host's season (`GameController.m_season`) is set on the guest when the state arrives and after `FindCurrentSeason`.
  - [NativeWorkshop.Content.cs](../../src/PCBS2Coop.Plugin/NativeWorkshop.Content.cs):
    - Each game keeps content in `Saves/Coop/Content/<SHA-256>.bin`; it is sent 32 chunks per frame.
    - `OS.GetWallPaperSprite` shows a PC's shared picture.
    - `OS.SetCustomWallpaper` and the Windows wallpaper (`SetWallpaper("USER_WALLPAPER")` while the wallpaper app is open) are shared. The Windows wallpaper falls back to Windows' own copy (`TranscodedWallpaper`) when the registry names a missing file.
    - A picture over 8 MB is refused, and the old wallpaper comes back.
    - The other player's mirrored custom-picture apply is skipped. Tablet wallpapers stay personal (no PC).
  - [NativePcLifecycle.Import.cs](../../src/PCBS2Coop.Plugin/NativePcLifecycle.Import.cs): picking up a PC file from the cupboard (a record that isn't in the save's storage list) sends the record and its case design, then asks for the import. Each game gives the design a fresh number of its own.
- Version **0.24**; 0.24 loads 0.23 co-op saves. **706/706** checks (29 new); Release 0/0; deployed.

## Owned two-game checks (November 2, 2026)

Owner decision 8 (b): runs p12a–p12e on disposable saves. Owner saves were unchanged after every run, and there were no display driver events. Probe commands call the game's own entry points (the light switch's `SetOn`, `OS.SetCustomWallpaper`, the import request), except where noted.

| Check | Result |
| --- | --- |
| Light switch, each side | Passed: both rooms' ceiling lamps go off and on. After a host-started `/sync` and after host Continue the lights are as the world had them. |
| Decorator app (real tablet clicks on the guest) | Passed: opening it gives the guest decorating, and the host's own open is refused with the prompt. Picking "Vintage Work Bench" leaves both games' decoration lists identical; closing frees decorating; Continue keeps the pick. |
| Light colour (probe, as the colour wheel) | Passed: same colour on both games. |
| Custom wallpaper, guest | Passed: the guest's picture shows on both screens (960×540), still after the file was deleted, and the host's software record carries it. |
| Picture over 8 MB | Passed: refused with a message; the old wallpaper comes back. |
| Windows wallpaper, host | Passed after a fix: both screens show it (1920×1080). |
| Rejoin without the picture | Passed: a dropped guest with its picture cache deleted asked the host, got the picture and showed it. |
| PC import (probe: a copy of PC 26's record through the import request) | Passed: the PC is in the guest's hands on both games (the host shows it carried) and goes onto a bench; the object state hashes match. `/sync` keeps it. |
| Seasonal switches | Not testable: the Free Build workshop has none (none found, inactive included). The guest took the host's season (standard). |

Fixed during the runs:
- A decoration's "selection" is its current pick, not a key: decorations are now keyed by type and object number.
- Running the game's restore mid-game loaded wrong pieces (the office desk turned into a work bench); picks are now set directly.
- The light colour isn't kept without the colour wheel attached; it is now set as the wheel sets it.
- The registry's Windows wallpaper path can be stale; Windows' own copy is used then.
- The chooser's own screen kept the stock picture for the Windows wallpaper.

## Runtime checks answered

November 5, 2026 follow-up in 0.28. The Windows picture tile had disappeared on a machine with a stale registry path and an existing Windows cached copy. The fix uses the cached picture for the missing gallery preview. Actual host and guest tile selections showed the same picture. Both real custom pickers shared distinct images. A cold Continue and join restored the guest's custom picture with its source file absent. The 9.45 MB fixture was refused while the previous wallpaper stayed. [Run record](acceptance-2026-11-04.md#real-wallpaper-picker-028)

- **R1:** the Decorator app changes its own preview and the saved decoration list at once. The decompile shows the room itself is updated only by a workshop load (`UpdateToMainRoom` from the loading screen and the restore), so a pick shows in the room after Continue, on both games alike. Not confirmed by eye: in p12d the bench-5 desk looked unchanged after Continue, and which desk object 0 is wasn't identified.
- **R2:** the Free Build tablet has Music Player, Photo Mode, Select Wallpaper, Part Ranking, Custom Cables (phase 11's Cable app), Decorator, RamWizz, Thermal and Airflow.
- **R6:** the season is Christmas when the SeasonalContent setting is on and the month is December, otherwise standard (no Halloween season). The Free Build workshop has no seasonal switches.
- **R7:** `GameController.Update` also switches the lights with a key; hooking `SetOn` covers both.
- **R3, R4, R5, R8:** not checked.

## Not checked in game yet

- A Steam-downloaded PC (its theme file isn't sent yet).
- A GIF as a wallpaper.
- Walls, floor, ceiling, posters and "Apply To All" in the Decorator; the colour wheel and pulse by hand.
- DLC decorations (none installed); the company plate.
- Seeing a pick in the room after Continue.

### Owned real cupboard file checks in 0.38

On November 6, 2026 both players used the actual cupboard file browser and Pick Up button on separate painted `.pcbs` fixtures. The native exporter supplied the test files with a placeholder thumbnail. In 0.37 the file card was added to native storage without a co-op identity. Version 0.38 stages the card and design locally until an accepted pickup. Saving with a staged card worked, a cancelled duplicate left the cupboard intact, and both imports kept their paint and identity through storage, cold Continue and a fresh join with the source files out of the browser. [Run record](acceptance-2026-11-04.md#real-cupboard-file-pickup-in-037-and-038)

## Owner test (0.24)

1. Each player: open the tablet (T) → Decorator, pick a wall, a floor and a desk, then Apply To All once. The other player trying to open Decorator meanwhile gets "Someone is decorating the workshop." and sees "Decorating" under your name.
2. Change the light colour (and pulse) in Decorator: the other game's lights change too.
3. Save, Continue, and have the guest join: both rooms show the same decorations.
4. Each player flips the light switch, and the lights key: both rooms and ceiling fans follow. Then a guest joins: same lights.
5. On a PC's desktop, Select Wallpaper → custom tab: pick your own picture, from each side; then your Windows wallpaper. Both screens show the same picture, also after Continue and a join. A very large picture (over 8 MB) is refused with a message.
6. Put a PC file in the game's local PC folder (or export one first), then pick it up from the cupboard, from each side: it arrives in your hands on both games with its paint job. Store it, Continue, join.
7. Music, notes and GIF capture stay each player's own.
8. Regression: PC moves, tool tables, Lighting, `/sync`.

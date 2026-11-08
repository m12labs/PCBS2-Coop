# Needs testing

What still needs a two-game test, by area. Each line is one thing to try with both players. Unless a line says otherwise, both games should show the same result afterwards. Details and expected results are in the phase audits linked in each heading.

Current build: **0.51**. Tick a line when it passes, and note the date if it fails.

Evidence review and current owned runs: [November 4 acceptance record](audit/acceptance-2026-11-04.md). Older passes below name their build and date; a partial earlier result does not pass a combined checklist case.

## Workbench: building a PC ([phase 4](audit/phase-4.md), 4f)

- [x] Build a full PC from an empty case (board, CPU, cooler, RAM, GPU, drives, PSU, fans), host and guest each (0.47, November 7, 2026. The host built PC 1000 and the guest built PC 1115. Both native part graphs matched for each build.)
- [x] Take a full PC apart again, host and guest each (0.48–0.49, November 7, 2026. Both stripped their builds to the case and two stock fans. Both games agreed on the remaining parts and zero internal cables.)
- [x] Two identical parts (RAM sticks, fans): remove one, the other stays (0.30, November 5, 2026: both top fans stayed when the identical rear fan was removed)
- [x] Both players work on different parts of the same PC at once: the second edit attempt is refused under the phase 4e waiting rule (0.30, November 5, 2026)
- [x] Both players go for the same slot at once: one wins, one gets a refusal (0.30, November 5, 2026)
- [x] Try a part that doesn't fit, or a blocked removal: refused, nothing changes (0.31, November 5, 2026: the installed cooler blocked CPU removal and both PCs kept the CPU)
- [x] Latches, CPU levers, RAM clips, case panels both ways (0.35 and 0.47, November 5 and 7, 2026. Both players removed and refitted the front panel. Each opened the PCI latch, operated the AM4 lever and opened both RAM clips. Both games agreed on the results.)
- [x] Esc halfway through an install and a removal (In 0.30, real Escape during fan orientation left the native editor open and the eventual install matched on both games. In 0.52 on November 8, 2026, real Escape while three GPU cables were animating out kept the GPU installed and unplugged the same leads on both games.)
- [x] Guest joins while the host is building (0.31, November 5, 2026: fresh guest waited through the host's fan install, then loaded the completed PC)
- [x] Save, Exit, Continue after a build (0.47–0.48, November 7, 2026. Cold Continue initially refused the motherboard standoffs. Both fresh 0.48 games loaded the same pair with both completed builds and the saved empty RAM clip.)

## Workbench: cables and power ([phase 5](audit/phase-5.md), 5d)

- [x] Plug in and unplug each cable type (24-pin, CPU 8-pin, GPU, SATA, fans, front panel/USB). Run acceptance-c checked 24-pin, GPU and fan in 0.27, CPU 8-pin, SATA data and power, and front panel in 0.29, and USB replay in 0.30 on November 5, 2026.
- [ ] Fan header with no free connector: automatic splitter shows on both games. The native automatic branches are for GPU and SATA power. In 0.51 the sixth GPU lead hit No More Power Slots. A manually fitted 3×8 splitter and two GPU leads matched. In 0.55 the native catalog refused the second RTX 3090 because the fixture's B550 Tomahawk lacks SLI support. Automatic insertion still needs an eligible native GPU or SATA power fixture.
- [x] Custom-colour cable: same colours on both games (0.36, November 6, 2026. Both ATX installs kept the same alternating violet and gold pin colours on both games.)
- [x] Esc in the cable tool with nothing connected: no change (0.27, November 5, 2026: real Escape in the guest's HDMI connector choice)
- [x] The other player is refused on that PC while a cable is being placed (0.27, November 5, 2026: host refused on PC 213 and could power PC 26)
- [x] Power on/off with the case button and the HUD button (0.26, November 5, 2026: native case-button callback and real HUD clicks, run acceptance-c)
- [x] Pick up a powered PC: off on both games (0.26, November 5, 2026: actual guest pickup, run acceptance-c)
- [x] Late join and Continue with a powered PC: on again on both games (0.27, November 5, 2026: cold host and guest both reached the desktop, run acceptance-c)
- [x] Part edit on a powered PC: same power state afterwards (0.30, November 5, 2026: rear fan removal shut down both PCs)

## Workbench: custom water cooling ([phase 10](audit/phase-10.md), 10b)

- [x] Lay a pipe with corners (November 5, 2026: guest accepted a red rigid pipe in 0.30, and both games reported the same four nodes and two bend fittings in 0.31)
- [x] Undo and Esc in the pipe tool: nothing shared (0.30 and 0.50, November 5 and 7, 2026. A real rigid bend added a sixth node. Undo restored all five original positions exactly. Escape discarded the preview and both games kept the same committed route.)
- [x] Remove a pipe (0.29, November 5, 2026, run acceptance-c)
- [x] Fill the loop with coolant, then drain it (0.29, November 5, 2026, PC powered off)
- [x] Install and remove a CPU block, GPU block, pump/reservoir, VRM or RAM heatsink (GPU in 0.49 and CPU block in 0.50. Both pump operations passed in 0.51 after the guest rebind fix. The exact Alphacool RAM heatsinks appeared in both native records after fitting the converted kit and after cold Continue and fresh join. Removing the heatsink-bearing DIMM removed both entries on both games in 0.51 on November 8, 2026.)
- [x] The other player gets the water-cooling refusal on that PC; another PC still works (0.29, November 5, 2026)
- [x] Water edits on a powered PC (0.30, November 5, 2026: real reservoir click shut down both PCs and drained both loops)
- [x] Late join and Continue after water edits (0.30, November 5, 2026: cold Continue and fresh guest retained a removed pipe, then `/sync` retained the replacement and coolant)

## Workbench: thermal paste ([phase 9](audit/phase-9.md))

- [x] Put paste on a CPU (paste tool), host and guest each (host in 0.33 and guest in 0.34 on November 5, 2026. Both games reported paste on after each real syringe application.)
- [x] Take paste off with the Q-tip, host and guest each (check whether the other game's paste goes away too). Both games reported paste off after the host's wipe in 0.33 and the guest's wipe in 0.34 on November 5, 2026.
- [x] The other player is refused on that PC and sees "Applying thermal paste" under the name (guest refusal in 0.33, host saw the guest's label in 0.34 on November 5, 2026)
- [x] Run the PC after pasting: no overheat on either game (0.39, November 6, 2026. The CPU pasted in the earlier owned run finished the real OCCT automatic test on both games.)
- [x] Cleaning mode shows "Cleaning isn't shared in co-op yet" (0.33, November 5, 2026. The host selected Compressed Air from the real Tools inventory.)

## Delid table ([phase 9](audit/phase-9.md))

- [x] Delid a CPU, host and guest each: the delidded CPU is in both inventories (0.49–0.50, November 7, 2026. The host finished an i3-8350K and the guest finished an i5-8600K. Both native inventories showed one of each delidded variant.)
- [x] Install the delidded CPU from each side: both games show it delidded (0.50, November 7, 2026. The host installed the delidded i3-8350K and the guest replaced it with the delidded i5-8600K. Both native sockets showed the selected variant after each edit.)
- [x] The other player gets "Someone is using the delidding table." and sees "Delidding" (0.49, November 7, 2026. The guest saw the refusal and the label over the host.)
- [ ] The PC on a table in use can't be picked up by the other player (Inapplicable in game 1.17.02. The native Delidding bench only accepts loose CPUs. The guest tried to place a carried case while the host held the table and after release in 0.51. The table stayed empty. The occupied Case Modding table pickup refusal passed in 0.49.)
- [x] Esc halfway through: nothing shared, CPU back in the inventory (0.49, November 7, 2026. The host exited after removing the lid and cleaning the CPU. Both inventories stayed unchanged and the stock i3-8350K was back in the host's CPU list.)
- [x] Save and Continue after a delid: the delidded CPU is still there, and the world loads (0.49, November 7, 2026. Cold Continue and a fresh guest both retained one delidded i3-8350K and the same world.)

## Water-cooling table ([phase 9](audit/phase-9.md))

- [x] Water-cool a GPU (pads, paste, block): the finished card is in both inventories (0.35, November 5, 2026. The guest completed the real GTX 1070 procedure with the EKWB RGB block.)
- [x] Install that card from each side (0.49, November 7, 2026. Host and guest each selected the converted GTX 1070 in the real inventory. Both native installs and remote replays kept its EKWB block and fittings.)
- [x] Same with RAM, a motherboard and a radiator (0.51, November 7–8, 2026. The three finished variants reached both inventories. Host and guest each installed the two-stick RAM, X570 with its Ryzen and VRM monoblock, and radiator with its custom fan. Both games matched after every install.)
- [x] The other player gets "Someone is using the water-cooling table." (0.35, November 5, 2026. The host saw the refusal while the guest worked on the GPU.)
- [x] Esc halfway through: nothing shared (0.51, November 7, 2026. The guest pressed Escape after removing and replacing all 16 RAM pads. Both native inventories stayed unchanged, the claim cleared, and the stock part remained available.)

## Tool tables: switching ([phase 11](audit/phase-11.md))

- [x] Switch a table to delidding, water cooling, case modding and back to the PC from each side: the other game's table switches too (0.35, November 5, 2026. Each player selected all four modes from the actual `B` menu.)
- [x] Switching a table the other player is using is refused with the prompt (0.24, November 4, 2026: guest's real selector refused while host used delidding; run acceptance-a)

## Case modding table ([phase 11](audit/phase-11.md))

- [x] Paint a case, add stickers and a theme, close the table, host and guest each: the other game shows the same design (November 5–6, 2026. Both games rendered the host's Cityscape Deepcool in 0.35 and the guest's sunset, blue heart and yellow paint in 0.36.)
- [x] Undo, redo and clear, then close: the other game shows what was left (0.36, November 6, 2026. The guest saw the test mark disappear, reappear and clear. Both games showed the finished design after closing.)
- [x] Customize two different new cases, one from each side: each keeps its own design on both games (0.36, November 6, 2026. Both games retained separate Deepcool and Kolink design numbers and hashes.)
- [x] The other player gets "Someone is using the case modding table." and sees "Case modding" (0.35, November 5, 2026. The guest saw the prompt and the label under the host's name.)
- [x] The PC on a table in use can't be picked up by the other player (case modding, 0.35, November 5, 2026. The guest's right-click pickup was refused.)
- [x] A heavily painted case still shares (0.35, November 5, 2026. Both games rendered the same 96-layer Deepcool design.)
- [x] Save, Continue and a guest join after case modding: designs still there (0.36, November 6, 2026. Both saved designs retained their numbers, layer counts and native hashes.)

## Lighting ([phase 11](audit/phase-11.md))

- [x] Change colours (wheel and swatches), effects, speed and direction on a running PC, host and guest each: the other game's lights change without a restart (Both shared real swatches, advanced sliders and GPU effect controls in 0.40–0.41. In 0.51 on November 8, 2026, the native hue ring changed the selected RAM from cyan to red on the host and to yellow-green on the guest. Both running PCs matched after each change. The isolated probe supplied the held action while the game read the real pointer.)
- [x] Save and load a preset from each side (0.40–0.41, November 6, 2026. Host Preset 1 and guest Preset 2 each restored distinct native Rainbow speed and direction on both games.)
- [x] Keyboard and mouse lighting from each side (0.40–0.41, November 6, 2026. The host's magenta and guest's blue reached both native peripherals during play.)
- [x] Opening Lighting doesn't change the other player's view (0.41, November 6, 2026. Both real launches kept the other player's HUD hidden and left their view alone.)
- [x] Breathe or rainbow after a guest joins: in step on both games (0.43, November 6, 2026. Rainbow settings matched and the native clocks were within 0.06 seconds one second after admission.)
- [x] Power off and on, then Continue: colours kept (0.41–0.42, November 6, 2026. The real power cycle kept the colours. The 0.42 cold Continue and fresh join restored the cooler, GPU effect, preset and both blue peripherals from the 0.41 save.)

## Cable app ([phase 11](audit/phase-11.md))

- [x] Is the Cable app in Free Build? Yes: Custom Cables on the tablet (November 2, 2026)
- [x] Buy a custom cable set: it is in both inventories (0.36, November 6, 2026. The guest checked out Coop Violet Gold, then both players saw all six styles in the cable inventory.)
- [x] Plug one of its cables in from each side (0.36, November 6, 2026. Both players selected a different style in the real inventory and used the native cable gesture on the same ATX connection.)
- [x] Save and Continue after buying (0.36, November 6, 2026. The host and fresh guest both had all six styles and the installed lead kept its pin colours.)

## Workshop: decorating and lights ([phase 12](audit/phase-12.md))

- [x] Decorator (tablet, T): pick a wall, a floor and a desk, then Apply To All, from each side; the other player is refused while you decorate and sees "Decorating" (0.38–0.39, November 6, 2026. Both made real picks and saw the activity label. On 0.39 both occupied attempts returned to the tablet home.)
- [x] Light colour and pulse in Decorator: the other game's lights follow (0.52, November 8, 2026. The stock MSI walls expose a physical Colour Picker. The host selected violet and Static, the guest selected green and Pulse, and the other room's native lights followed each change. The guest's attempt to open the panel while the host held it was refused.)
- [x] Save, Continue and a guest join after decorating: both rooms show the same decorations (0.40, November 6, 2026. Cold Continue and a fresh guest restored the same Space walls, PCBS1 floor and Future Work Bench selection. The live room refresh also passed.)
- [x] Light switch and the lights key, each side: both rooms and ceiling fans follow; a joining guest gets the same lights (0.39, November 6, 2026. Both clicked the real switch and pressed L. A fresh guest loaded with the lights off and both fans stopped.)

## Wallpapers and PC import ([phase 12](audit/phase-12.md))

- [x] Custom picture from the wallpaper app's custom tab, each side: both screens show it, after Continue and a join too (0.28, November 5, 2026: real picks from both sides and cold reload of the guest's picture with its source file absent)
- [x] Your Windows wallpaper, each side: both screens show the same picture (0.28, November 5, 2026: restored gallery tile and matching native textures, run acceptance-c)
- [x] A picture over 8 MB is refused with a message (0.28, November 5, 2026: real browser applied a 9.45 MB image, refused and previous wallpaper restored)
- [x] Pick up a PC file from the cupboard, each side: it arrives in your hands on both games with its paint job; store it, Continue, join (0.38, November 6, 2026. Both real file pickups and designs persisted in storage after the source files were moved out of the browser.)

## PC screens, BIOS and software ([phase 6](audit/phase-6.md), passed; quick check after updates)

- [x] Boot, BIOS changes and an app on the desktop still mirror both ways (0.40 test build, November 6, 2026. Both initiated BIOS entry, edited the ratio and applied or reset. Each started a real mirrored desktop test.)

## Tests, tuning and failures ([phase 7](audit/phase-7.md), 7e)

- [x] OCCT on an overclocked PC that fails: both games blue-screen once, same reason (host starts it, then the guest) (0.40 test build, November 6, 2026. Each started OCCT on PC 213 at 4,800 MHz and 1.20 V. Both games showed one `CPU_UNDER_VOLTAGE` failure per test.)
- [x] A PC overheating from a missing cooler or paste fails the same way on both (0.46, November 6, 2026. The complete AIO removal cleared every cooler and pipe reference. The next native boot produced one host `CPU_HEAT` result and the guest showed the same failure.)
- [x] GPU Tuner Apply and Reset from either player: same values, no restart (0.27, November 5, 2026: real slider and button interactions on both games)
- [x] Fan Control custom curve, presets and Defaults from either player (0.27, November 5, 2026: real controls on PC 26, run acceptance-c)
- [x] Late join with a custom fan curve applied (0.27, November 5, 2026: fresh guest restored the 11-point Max RPM curve and displayed 2000 RPM on all three GPU fans)
- [x] Power off and on after a blue screen: both reach the desktop (0.30, November 5, 2026: native GPU heat failure entry point and shared case-button power cycle)
- [x] Cinebench and 3DMark results match (0.40 test build, November 6, 2026. Both games scored 1,703 cb in Cinebench R20 and 11,925 in 3DMark Time Spy.)

## Saves and rejoin ([phase 8](audit/phase-8.md), 8g)

Substantial actual two-game evidence already exists in [phase 8's p8a–p8m runs](audit/phase-8.md#game-runs-november-2-2026). The >35-PC stress case passed in 0.25; the complete build/cable/water acceptance remains open.

- [x] New world: build, cable, power, BIOS, GPU Tuner, software; then Exit, Continue, guest joins: everything matches (November 8, 2026, 0.52–0.55. Fresh PC 392 restored with matching parts, paste, 11 cables, CPU x46.75, GPU 1684 MHz, power and installed apps in two cold games. Front USB replay and live app installation passed their follow-up checks.)
- [x] Guest drops (pull the network) and uses Rejoin: comes back where it stood, no duplicates (0.21, November 2, 2026: owned LAN runs, 12-second process suspension; exact position/facing and matching worlds)
- [x] Save waits for active component, latch, cable, water and software changes. BIOS saves use the last applied settings (Cable and Exit in 0.27, coolant drain in 0.29, fan install in 0.30, a moving RAM latch in 0.47, and an active System Info transfer in 0.49. The retries succeeded. In 0.51, cold Continue and a fresh guest restored x43 from the save made with x42 still in the BIOS editor. In 0.49, a save during the Apply restart restored the newly committed x39 and System Info on both games.)
- [x] Close the host's game during a save: next Continue loads the previous good save with a notice (0.32, November 5, 2026. The 39-PC pair stayed byte-identical, the host saw the notice, and the guest rejoined.)
- [x] A world with more than 35 built PCs: save, join, `/sync`, Continue (0.25, November 4, 2026: 39 built PCs, matching native checks and object hashes after every step; fixed the 0.24 duplicate-snapshot failure, run acceptance-b)
- [x] Host pause-menu Load and Reset reload both games; guest Save/Load/Reset are refused (0.21, November 2, 2026: owned two-game runs p8a–p8m)
- [ ] Rejoin over Steam (needs two Steam accounts)
- [x] Exit while the world can't be saved shows the message (0.27, November 5, 2026: guest held an HDMI edit open, host got the menu warning and the previous pair stayed byte-identical)

## Steam connection (version 0.9 onward)

Needs two PCs with two different Steam accounts, preferably on separate networks, both running the same build with its complete `steam/` folder. Use fresh co-op worlds and back up paired saves first. The single-PC check (`scripts/Test-SteamLocal.ps1`, no game running) already passes: it starts Steam under App ID 480, hosts, opens the friends list and closes the lobby without sending invites.

- [ ] Host picks Steam, starts a world, Esc → Invite Friends (friends list if the overlay doesn't open); guest accepts from the main menu, loads the world, both see each other
- [ ] Move, create/delete a case, pick up/drop/store/retrieve a PC, pause and switch focus while the other player moves
- [ ] Late join while the host makes changes; host save copy reaches the guest
- [ ] Guest leaves carrying a PC: host stays, PC goes to the cupboard; guest accepts a fresh invite and rejoins
- [ ] Host leaves: guest returns to the menu; host Continues and the guest joins again
- [ ] Accept an invite while already in a world: notice, that world isn't replaced
- [ ] Cancel create/join, accept twice, a stale/full/unrelated lobby, a different mod build: readable refusal, a new attempt still works
- [ ] Steam offline or lost mid-session, missing `steam/` folder, Steam turned off: LAN still works afterwards
- [ ] Friends list with no friends online; a failed invite reports a result
- [ ] Both logs: `[net] Steam route:` shows a relayed route (a direct or LAN link doesn't count), with snapshot size and time

## Everyday basics (quick regression after each update)

- [x] Host and guest join, see each other, chat works (0.34, November 5, 2026. Both avatars and names were visible and plain chat reached both games.)
- [x] Spawn, pick up, drop, store and retrieve PCs (0.36–0.38, November 6, 2026. Real catalog, bench and cupboard gestures kept the same PC identities on both games.)
- [x] Swap a workbench monitor, keyboard or mouse (0.38, November 6, 2026. The guest installed the Green Edition monitor with the real workbench controls and both games showed it.)
- [x] `/sync` repairs a world (0.33, November 5, 2026. Repaired a real assembly rebind fault and both games reloaded the same native state.)
- [x] Solo saves are unchanged after playing co-op (0.24/0.25, November 4, 2026: 21 files byte-identical after owned runs acceptance-a and acceptance-b, including every stress-run restart)

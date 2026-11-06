# Needs testing

What still needs a two-game test, by area. Each line is one thing to try with both players. Unless a line says otherwise, both games should show the same result afterwards. Details and expected results are in the phase audits linked in each heading.

Current build: **0.38**. Tick a line when it passes, and note the date if it fails.

Evidence review and current owned runs: [November 4 acceptance record](audit/acceptance-2026-11-04.md). Older passes below name their build and date; a partial earlier result does not pass a combined checklist case.

## Workbench: building a PC ([phase 4](audit/phase-4.md), 4f)

- [ ] Build a full PC from an empty case (board, CPU, cooler, RAM, GPU, drives, PSU, fans), host and guest each
- [ ] Take a full PC apart again, host and guest each
- [x] Two identical parts (RAM sticks, fans): remove one, the other stays (0.30, November 5, 2026: both top fans stayed when the identical rear fan was removed)
- [x] Both players work on different parts of the same PC at once: the second edit attempt is refused under the phase 4e waiting rule (0.30, November 5, 2026)
- [x] Both players go for the same slot at once: one wins, one gets a refusal (0.30, November 5, 2026)
- [x] Try a part that doesn't fit, or a blocked removal: refused, nothing changes (0.31, November 5, 2026: the installed cooler blocked CPU removal and both PCs kept the CPU)
- [ ] Latches, CPU levers, RAM clips, case panels both ways (0.35, November 5, 2026. Both players removed and refitted the front panel after unplugging it, then the guest reconnected the lead. Latch and clip checks remain.)
- [ ] Esc halfway through an install and a removal (phase 4 native checks found that Esc after install starts normally leaves it running. In 0.30 Esc at fan orientation also left the editor open. Removal retest remains.)
- [x] Guest joins while the host is building (0.31, November 5, 2026: fresh guest waited through the host's fan install, then loaded the completed PC)
- [ ] Save, Exit, Continue after a build

## Workbench: cables and power ([phase 5](audit/phase-5.md), 5d)

- [x] Plug in and unplug each cable type (24-pin, CPU 8-pin, GPU, SATA, fans, front panel/USB). Run acceptance-c checked 24-pin, GPU and fan in 0.27, CPU 8-pin, SATA data and power, and front panel in 0.29, and USB replay in 0.30 on November 5, 2026.
- [ ] Fan header with no free connector: automatic splitter shows on both games. Native auto-splitter branches are for GPU power and SATA power in this build. A fixture that exhausts eligible PSU connections is still needed.
- [x] Custom-colour cable: same colours on both games (0.36, November 6, 2026. Both ATX installs kept the same alternating violet and gold pin colours on both games.)
- [x] Esc in the cable tool with nothing connected: no change (0.27, November 5, 2026: real Escape in the guest's HDMI connector choice)
- [x] The other player is refused on that PC while a cable is being placed (0.27, November 5, 2026: host refused on PC 213 and could power PC 26)
- [x] Power on/off with the case button and the HUD button (0.26, November 5, 2026: native case-button callback and real HUD clicks, run acceptance-c)
- [x] Pick up a powered PC: off on both games (0.26, November 5, 2026: actual guest pickup, run acceptance-c)
- [x] Late join and Continue with a powered PC: on again on both games (0.27, November 5, 2026: cold host and guest both reached the desktop, run acceptance-c)
- [x] Part edit on a powered PC: same power state afterwards (0.30, November 5, 2026: rear fan removal shut down both PCs)

## Workbench: custom water cooling ([phase 10](audit/phase-10.md), 10b)

- [x] Lay a pipe with corners (November 5, 2026: guest accepted a red rigid pipe in 0.30, and both games reported the same four nodes and two bend fittings in 0.31)
- [ ] Undo and Esc in the pipe tool: nothing shared (0.30, November 5, 2026: Escape from fitting choice and from the rigid route after using Undo sent no record. Undo's geometry reversal still needs verification.)
- [x] Remove a pipe (0.29, November 5, 2026, run acceptance-c)
- [x] Fill the loop with coolant, then drain it (0.29, November 5, 2026, PC powered off)
- [ ] Install and remove a CPU block, GPU block, pump/reservoir, VRM or RAM heatsink
- [x] The other player gets the water-cooling refusal on that PC; another PC still works (0.29, November 5, 2026)
- [x] Water edits on a powered PC (0.30, November 5, 2026: real reservoir click shut down both PCs and drained both loops)
- [x] Late join and Continue after water edits (0.30, November 5, 2026: cold Continue and fresh guest retained a removed pipe, then `/sync` retained the replacement and coolant)

## Workbench: thermal paste ([phase 9](audit/phase-9.md))

- [x] Put paste on a CPU (paste tool), host and guest each (host in 0.33 and guest in 0.34 on November 5, 2026. Both games reported paste on after each real syringe application.)
- [x] Take paste off with the Q-tip, host and guest each (check whether the other game's paste goes away too). Both games reported paste off after the host's wipe in 0.33 and the guest's wipe in 0.34 on November 5, 2026.
- [x] The other player is refused on that PC and sees "Applying thermal paste" under the name (guest refusal in 0.33, host saw the guest's label in 0.34 on November 5, 2026)
- [ ] Run the PC after pasting: no overheat on either game
- [x] Cleaning mode shows "Cleaning isn't shared in co-op yet" (0.33, November 5, 2026. The host selected Compressed Air from the real Tools inventory.)

## Delid table ([phase 9](audit/phase-9.md))

- [ ] Delid a CPU, host and guest each: the delidded CPU is in both inventories
- [ ] Install the delidded CPU from each side: both games show it delidded
- [ ] The other player gets "Someone is using the delidding table." and sees "Delidding"
- [ ] The PC on a table in use can't be picked up by the other player
- [ ] Esc halfway through: nothing shared, CPU back in the inventory
- [ ] Save and Continue after a delid: the delidded CPU is still there, and the world loads

## Water-cooling table ([phase 9](audit/phase-9.md))

- [x] Water-cool a GPU (pads, paste, block): the finished card is in both inventories (0.35, November 5, 2026. The guest completed the real GTX 1070 procedure with the EKWB RGB block.)
- [ ] Install that card from each side
- [ ] Same with RAM, a motherboard and a radiator
- [x] The other player gets "Someone is using the water-cooling table." (0.35, November 5, 2026. The host saw the refusal while the guest worked on the GPU.)
- [ ] Esc halfway through: nothing shared

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

- [ ] Change colours (wheel and swatches), effects, speed and direction on a running PC, host and guest each: the other game's lights change without a restart
- [ ] Save and load a preset from each side
- [ ] Keyboard and mouse lighting from each side
- [ ] Opening Lighting doesn't change the other player's view
- [ ] Breathe or rainbow after a guest joins: in step on both games
- [ ] Power off and on, then Continue: colours kept

## Cable app ([phase 11](audit/phase-11.md))

- [x] Is the Cable app in Free Build? Yes: Custom Cables on the tablet (November 2, 2026)
- [x] Buy a custom cable set: it is in both inventories (0.36, November 6, 2026. The guest checked out Coop Violet Gold, then both players saw all six styles in the cable inventory.)
- [x] Plug one of its cables in from each side (0.36, November 6, 2026. Both players selected a different style in the real inventory and used the native cable gesture on the same ATX connection.)
- [x] Save and Continue after buying (0.36, November 6, 2026. The host and fresh guest both had all six styles and the installed lead kept its pin colours.)

## Workshop: decorating and lights ([phase 12](audit/phase-12.md))

- [ ] Decorator (tablet, T): pick a wall, a floor and a desk, then Apply To All, from each side; the other player is refused while you decorate and sees "Decorating"
- [ ] Light colour and pulse in Decorator: the other game's lights follow
- [ ] Save, Continue and a guest join after decorating: both rooms show the same decorations
- [ ] Light switch and the lights key, each side: both rooms and ceiling fans follow; a joining guest gets the same lights

## Wallpapers and PC import ([phase 12](audit/phase-12.md))

- [x] Custom picture from the wallpaper app's custom tab, each side: both screens show it, after Continue and a join too (0.28, November 5, 2026: real picks from both sides and cold reload of the guest's picture with its source file absent)
- [x] Your Windows wallpaper, each side: both screens show the same picture (0.28, November 5, 2026: restored gallery tile and matching native textures, run acceptance-c)
- [x] A picture over 8 MB is refused with a message (0.28, November 5, 2026: real browser applied a 9.45 MB image, refused and previous wallpaper restored)
- [x] Pick up a PC file from the cupboard, each side: it arrives in your hands on both games with its paint job; store it, Continue, join (0.38, November 6, 2026. Both real file pickups and designs persisted in storage after the source files were moved out of the browser.)

## PC screens, BIOS and software ([phase 6](audit/phase-6.md), passed; quick check after updates)

- [ ] Boot, BIOS changes and an app on the desktop still mirror both ways

## Tests, tuning and failures ([phase 7](audit/phase-7.md), 7e)

- [ ] OCCT on an overclocked PC that fails: both games blue-screen once, same reason (host starts it, then the guest)
- [ ] A PC overheating from a missing cooler or paste fails the same way on both (0.34, November 5, 2026. Booting PC 26 with an empty water loop caused the same native `CPU_HEAT` failure on both games. The missing-cooler or missing-paste case remains.)
- [x] GPU Tuner Apply and Reset from either player: same values, no restart (0.27, November 5, 2026: real slider and button interactions on both games)
- [x] Fan Control custom curve, presets and Defaults from either player (0.27, November 5, 2026: real controls on PC 26, run acceptance-c)
- [x] Late join with a custom fan curve applied (0.27, November 5, 2026: fresh guest restored the 11-point Max RPM curve and displayed 2000 RPM on all three GPU fans)
- [x] Power off and on after a blue screen: both reach the desktop (0.30, November 5, 2026: native GPU heat failure entry point and shared case-button power cycle)
- [ ] Cinebench and 3DMark results match

## Saves and rejoin ([phase 8](audit/phase-8.md), 8g)

Substantial actual two-game evidence already exists in [phase 8's p8a–p8m runs](audit/phase-8.md#game-runs-november-2-2026). The >35-PC stress case passed in 0.25; the complete build/cable/water acceptance remains open.

- [ ] New world: build, cable, power, BIOS, GPU Tuner, software; then Exit, Continue, guest joins: everything matches
- [x] Guest drops (pull the network) and uses Rejoin: comes back where it stood, no duplicates (0.21, November 2, 2026: owned LAN runs, 12-second process suspension; exact position/facing and matching worlds)
- [ ] Save while an edit, latch, cable, water, BIOS or software change runs: the save waits (November 5, 2026, run acceptance-c: cable guard and Exit wait in 0.27, coolant drain guard in 0.29, fan install guard in 0.30)
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
- [ ] Swap a workbench monitor, keyboard or mouse
- [x] `/sync` repairs a world (0.33, November 5, 2026. Repaired a real assembly rebind fault and both games reloaded the same native state.)
- [x] Solo saves are unchanged after playing co-op (0.24/0.25, November 4, 2026: 21 files byte-identical after owned runs acceptance-a and acceptance-b, including every stress-run restart)

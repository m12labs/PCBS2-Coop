# Needs testing

What still needs a two-game test, by area. Each line is one thing to try with both players. Unless a line says otherwise, both games should show the same result afterwards. Details and expected results are in the phase audits linked in each heading.

Current build: **0.27**. Tick a line when it passes, and note the date if it fails.

Evidence review and current owned runs: [November 4 acceptance record](audit/acceptance-2026-11-04.md). Older passes below name their build and date; a partial earlier result does not pass a combined checklist case.

## Workbench: building a PC ([phase 4](audit/phase-4.md), 4f)

- [ ] Build a full PC from an empty case (board, CPU, cooler, RAM, GPU, drives, PSU, fans), host and guest each
- [ ] Take a full PC apart again, host and guest each
- [ ] Two identical parts (RAM sticks, fans): remove one, the other stays
- [ ] Both players work on different parts of the same PC at once
- [ ] Both players go for the same slot at once: one wins, one gets a refusal
- [ ] Try a part that doesn't fit, or a blocked removal: refused, nothing changes
- [ ] Latches, CPU levers, RAM clips, case panels both ways
- [ ] Esc halfway through an install and a removal
- [ ] Guest joins while the host is building
- [ ] Save, Exit, Continue after a build

## Workbench: cables and power ([phase 5](audit/phase-5.md), 5d)

- [ ] Plug in and unplug each cable type (24-pin, CPU 8-pin, GPU, SATA, fans, front panel/USB). Run acceptance-c passed the 24-pin, GPU and case-fan native callbacks from the guest in 0.27.
- [ ] Fan header with no free connector: automatic splitter shows on both games
- [ ] Custom-colour cable: same colours on both games
- [x] Esc in the cable tool with nothing connected: no change (0.27, November 5, 2026: real Escape in the guest's HDMI connector choice)
- [x] The other player is refused on that PC while a cable is being placed (0.27, November 5, 2026: host refused on PC 213 and could power PC 26)
- [x] Power on/off with the case button and the HUD button (0.26, November 5, 2026: native case-button callback and real HUD clicks, run acceptance-c)
- [x] Pick up a powered PC: off on both games (0.26, November 5, 2026: actual guest pickup, run acceptance-c)
- [x] Late join and Continue with a powered PC: on again on both games (0.27, November 5, 2026: cold host and guest both reached the desktop, run acceptance-c)
- [ ] Part edit on a powered PC: same power state afterwards

## Workbench: custom water cooling ([phase 10](audit/phase-10.md), 10b)

- [ ] Lay a pipe with corners
- [ ] Undo and Esc in the pipe tool: nothing shared
- [ ] Remove a pipe
- [ ] Fill the loop with coolant, then drain it
- [ ] Install and remove a CPU block, GPU block, pump/reservoir, VRM or RAM heatsink
- [ ] The other player gets the water-cooling refusal on that PC; another PC still works
- [ ] Water edits on a powered PC
- [ ] Late join and Continue after water edits

## Workbench: thermal paste ([phase 9](audit/phase-9.md))

- [ ] Put paste on a CPU (paste tool), host and guest each
- [ ] Take paste off with the Q-tip, host and guest each (check whether the other game's paste goes away too)
- [ ] The other player is refused on that PC and sees "Applying thermal paste" under the name
- [ ] Run the PC after pasting: no overheat on either game
- [ ] Cleaning mode shows "Cleaning isn't shared in co-op yet"

## Delid table ([phase 9](audit/phase-9.md))

- [ ] Delid a CPU, host and guest each: the delidded CPU is in both inventories
- [ ] Install the delidded CPU from each side: both games show it delidded
- [ ] The other player gets "Someone is using the delidding table." and sees "Delidding"
- [ ] The PC on a table in use can't be picked up by the other player
- [ ] Esc halfway through: nothing shared, CPU back in the inventory
- [ ] Save and Continue after a delid: the delidded CPU is still there, and the world loads

## Water-cooling table ([phase 9](audit/phase-9.md))

- [ ] Water-cool a GPU (pads, paste, block): the finished card is in both inventories
- [ ] Install that card from each side
- [ ] Same with RAM, a motherboard and a radiator
- [ ] The other player gets "Someone is using the water-cooling table."
- [ ] Esc halfway through: nothing shared

## Tool tables: switching ([phase 11](audit/phase-11.md))

- [ ] Switch a table to delidding, water cooling, case modding and back to the PC from each side: the other game's table switches too
- [x] Switching a table the other player is using is refused with the prompt (0.24, November 4, 2026: guest's real selector refused while host used delidding; run acceptance-a)

## Case modding table ([phase 11](audit/phase-11.md))

- [ ] Paint a case, add stickers and a theme, close the table, host and guest each: the other game shows the same design
- [ ] Undo, redo and clear, then close: the other game shows what was left
- [ ] Customize two different new cases, one from each side: each keeps its own design on both games
- [ ] The other player gets "Someone is using the case modding table." and sees "Case modding"
- [ ] The PC on a table in use can't be picked up by the other player
- [ ] A heavily painted case still shares
- [ ] Save, Continue and a guest join after case modding: designs still there

## Lighting ([phase 11](audit/phase-11.md))

- [ ] Change colours (wheel and swatches), effects, speed and direction on a running PC, host and guest each: the other game's lights change without a restart
- [ ] Save and load a preset from each side
- [ ] Keyboard and mouse lighting from each side
- [ ] Opening Lighting doesn't change the other player's view
- [ ] Breathe or rainbow after a guest joins: in step on both games
- [ ] Power off and on, then Continue: colours kept

## Cable app ([phase 11](audit/phase-11.md))

- [x] Is the Cable app in Free Build? Yes: Custom Cables on the tablet (November 2, 2026)
- [ ] Buy a custom cable set: it is in both inventories
- [ ] Plug one of its cables in from each side
- [ ] Save and Continue after buying

## Workshop: decorating and lights ([phase 12](audit/phase-12.md))

- [ ] Decorator (tablet, T): pick a wall, a floor and a desk, then Apply To All, from each side; the other player is refused while you decorate and sees "Decorating"
- [ ] Light colour and pulse in Decorator: the other game's lights follow
- [ ] Save, Continue and a guest join after decorating: both rooms show the same decorations
- [ ] Light switch and the lights key, each side: both rooms and ceiling fans follow; a joining guest gets the same lights

## Wallpapers and PC import ([phase 12](audit/phase-12.md))

- [ ] Custom picture from the wallpaper app's custom tab, each side: both screens show it, after Continue and a join too
- [ ] Your Windows wallpaper, each side: both screens show the same picture
- [x] A picture over 8 MB is refused with a message (0.24, November 2, 2026: owned phase 12 check also verified restoration of the previous wallpaper)
- [ ] Pick up a PC file from the cupboard, each side: it arrives in your hands on both games with its paint job; store it, Continue, join

## PC screens, BIOS and software ([phase 6](audit/phase-6.md), passed; quick check after updates)

- [ ] Boot, BIOS changes and an app on the desktop still mirror both ways

## Tests, tuning and failures ([phase 7](audit/phase-7.md), 7e)

- [ ] OCCT on an overclocked PC that fails: both games blue-screen once, same reason (host starts it, then the guest)
- [ ] A PC overheating from a missing cooler or paste fails the same way on both
- [x] GPU Tuner Apply and Reset from either player: same values, no restart (0.27, November 5, 2026: real slider and button interactions on both games)
- [ ] Fan Control custom curve, presets and Defaults from either player
- [ ] Late join with a custom fan curve applied
- [ ] Power off and on after a blue screen: both reach the desktop
- [ ] Cinebench and 3DMark results match

## Saves and rejoin ([phase 8](audit/phase-8.md), 8g)

Substantial actual two-game evidence already exists in [phase 8's p8a–p8m runs](audit/phase-8.md#game-runs-november-2-2026). The >35-PC stress case passed in 0.25; the complete build/cable/water acceptance remains open.

- [ ] New world: build, cable, power, BIOS, GPU Tuner, software; then Exit, Continue, guest joins: everything matches
- [x] Guest drops (pull the network) and uses Rejoin: comes back where it stood, no duplicates (0.21, November 2, 2026: owned LAN runs, 12-second process suspension; exact position/facing and matching worlds)
- [ ] Save while an edit, latch, cable, water, BIOS or software change runs: the save waits (0.27, November 5, 2026: cable-only guard and Exit wait verified in run acceptance-c)
- [ ] Close the host's game during a save: next Continue loads the previous good save with a notice (0.21, November 2, 2026: actual interruption preserved the previous pair; the separate mismatched-pair run showed the notice)
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

- [ ] Host and guest join, see each other, chat works
- [ ] Spawn, pick up, drop, store and retrieve PCs
- [ ] Swap a workbench monitor, keyboard or mouse
- [ ] `/sync` repairs a world
- [x] Solo saves are unchanged after playing co-op (0.24/0.25, November 4, 2026: 21 files byte-identical after owned runs acceptance-a and acceptance-b, including every stress-run restart)

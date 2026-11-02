# Needs testing

What still needs a two-game test, by area. Each line is one thing to try with both players. Unless a line says otherwise, both games should show the same result afterwards. Details and expected results are in the phase audits linked in each heading.

Current build: **0.23**. Tick a line when it passes, and note the date if it fails.

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

- [ ] Plug in and unplug each cable type (24-pin, CPU 8-pin, GPU, SATA, fans, front panel/USB)
- [ ] Fan header with no free connector: automatic splitter shows on both games
- [ ] Custom-colour cable: same colours on both games
- [ ] Esc in the cable tool with nothing connected: no change
- [ ] The other player is refused on that PC while a cable is being placed
- [ ] Power on/off with the case button and the HUD button
- [ ] Pick up a powered PC: off on both games
- [ ] Late join and Continue with a powered PC: on again on both games
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
- [ ] Switching a table the other player is using is refused with the prompt

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

- [ ] Is the Cable app in Free Build (PC desktop or tablet)?
- [ ] Buy a custom cable set: it is in both inventories
- [ ] Plug one of its cables in from each side
- [ ] Save and Continue after buying

## PC screens, BIOS and software ([phase 6](audit/phase-6.md), passed; quick check after updates)

- [ ] Boot, BIOS changes and an app on the desktop still mirror both ways

## Tests, tuning and failures ([phase 7](audit/phase-7.md), 7e)

- [ ] OCCT on an overclocked PC that fails: both games blue-screen once, same reason (host starts it, then the guest)
- [ ] A PC overheating from a missing cooler or paste fails the same way on both
- [ ] GPU Tuner Apply and Reset from either player: same values, no restart
- [ ] Fan Control custom curve, presets and Defaults from either player
- [ ] Late join with a custom fan curve applied
- [ ] Power off and on after a blue screen: both reach the desktop
- [ ] Cinebench and 3DMark results match

## Saves and rejoin ([phase 8](audit/phase-8.md), 8g)

- [ ] New world: build, cable, power, BIOS, GPU Tuner, software; then Exit, Continue, guest joins: everything matches
- [ ] Guest drops (pull the network) and uses Rejoin: comes back where it stood, no duplicates
- [ ] Save while an edit, latch, cable, water, BIOS or software change runs: the save waits
- [ ] Close the host's game during a save: next Continue loads the previous good save with a notice
- [ ] A world with more than 35 built PCs: save, join, `/sync`, Continue
- [ ] Host pause-menu Load and Reset reload both games; guest Save/Load/Reset are refused
- [ ] Rejoin over Steam (needs two Steam accounts)
- [ ] Exit while the world can't be saved shows the message

## Everyday basics (quick regression after each update)

- [ ] Host and guest join, see each other, chat works
- [ ] Spawn, pick up, drop, store and retrieve PCs
- [ ] Swap a workbench monitor, keyboard or mouse
- [ ] `/sync` repairs a world
- [ ] Solo saves are unchanged after playing co-op

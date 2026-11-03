# PCBS2 Co-op

Build PCs together in **PC Building Simulator 2**. PCBS2 Co-op is an unofficial two-player mod that puts you and a friend in the same Free Build workshop over LAN or Steamworks when available.

Built for BepInEx 6 IL2CPP. **Current version: 0.24** (in development). Both players need the same mod version; a different version is refused when joining. 0.24 loads co-op saves made by 0.16 or later; older co-op saves are refused, so start a fresh co-op world if yours is older.

> **In development.** Most of Free Build is shared: building a PC part by part, cables and power, water cooling, the PC's screens, BIOS and benchmarks, thermal paste and the tool tables, case modding and lighting, and the workshop's decorations, lights and wallpapers. Saves, backups and rejoining after a dropped connection work too. Some of these have only been checked in short test runs and still need a full two-player test ([what's left to test](docs/needs_testing.md)). Career mode isn't started. See [what works](#sync-progress).

> **Unofficial fan project.** PCBS2 Co-op is not affiliated with the developers or publishers of PC Building Simulator 2. This repository contains no game code or game assets.

## Sync progress

A quick look at what's ready and what's next, as of version 0.24. See the [full sync checklist](docs/sync-progress.md) for individual features, the [roadmap](plan.md) for the order of work and [needs testing](docs/needs_testing.md) for the two-player tests still to run.

| Feature | Planned | Implemented, not tested | Partially synced | Synced | Comments |
| --- | :---: | :---: | :---: | :---: | --- |
| LAN Host / Join | | | | ✅ | Direct IP and port from the main menu. |
| Steam Invite / Join | | | | ✅ | Private Steam lobby invites (overlay or in-game friends list) and play over Steam relay. LAN stays available. |
| Join check | | | | ✅ | A different mod version or game build is refused with the reason; DLC or part catalog differences join with a warning. |
| Shared Free Build world | | | | ✅ | Host picks a new or saved co-op world; both players load the same workshop. |
| Joining an active session | | | | ✅ | Join any time; you get the host's current world, including changes made while you load. |
| Co-op saves and rejoin | | | | ✅ | Named co-op worlds kept apart from solo saves, with a host autosave and hidden backups of the last good save. The host's pause-menu Load and Reset reload both games; the guest can't save. A guest who drops gets a Rejoin button and comes back where it stood. Checked in test runs; the owner's full pass is next. |
| Leaving and pause | | | | ✅ | Host leaving sends the guest back to the menu with the reason; guest leaving lets the host keep playing. Either player's pause menu is personal; the session keeps running. |
| Seeing each other | | | ✅ | | An animated character (a capsule if the model can't load) with a name tag and what you're doing under it; a carried PC shows as a cardboard box. Detailed animations and sounds come later (phase 13). |
| Chat and /sync | | | | ✅ | Press Enter to chat. If a game shows "out of sync", `/sync` saves and reloads both games. |
| PCs, carrying, benches and storage | | | | ✅ | Creating, deleting, picking up, dropping, storing and retrieving PCs, and importing a PC file from the cupboard. If both players grab the same PC or bench, one wins and the other sees a short notice. A world with packages or notes can't be hosted yet. |
| Installing and removing parts | | | | ✅ | Both players can work on the same PC; each sees the other's installs with the game's animations. Latches, clips and case panels too. |
| Cables, power and peripherals | | | | ✅ | Plugging and unplugging cables (splitters and custom colours too), power buttons and workbench monitor/keyboard/mouse swaps. |
| Water cooling | | | | ✅ | Pipes, coolant fill and drain, water blocks, pumps and reservoirs. |
| Booting, BIOS and screens | | | ✅ | | Both games run the same desktop and apps from both players' clicks; BIOS changes are shared. Window positions, files and installed programs aren't shared yet. |
| Benchmarks, temperatures and tuning | | ✅ | | | Benchmarks run on both games, blue screens follow the host, GPU Tuner and Fan Control curves are shared. |
| Thermal paste and tool tables | | ✅ | | | Paste on and off, and the delidding and water-cooling tables (one player at a time; the result is shared). Cleaning is refused in co-op until career mode. |
| Case modding and lighting | | ✅ | | | The case-modding table shares its finished design; Lighting app settings, presets and effects are shared; Cable app purchases reach both inventories. The LED strip tool can't be opened in this game build. |
| Workshop and wallpapers | | | ✅ | | Decorating (one player at a time), the light switch, the season and its decorations, and custom and Windows wallpapers. Checked in test runs. |
| Remaining Free Build details | ✅ | | | | Phases 13–14: detailed player animations, sounds, remaining apps, faults and performance. |
| Career mode | ✅ | | | | Phases 15–18, after Free Build is complete. |

## Installation

There is no release download yet; build from source. You need Windows x64, your own copy of the game, PowerShell and the **.NET SDK 10**. The plugin targets .NET 6 for the loader's runtime.

The reference setup is **Unity 6000.2.6f2**, game **v1.17.02** (shown on the main menu), and **BepInEx 6 IL2CPP win-x64** pinned to `6.0.0-be.791+b782da4`. Other game builds need compatibility testing.

<details>
<summary>Build and install from source</summary>

### Path placeholders

All paths here are relative paths or placeholders for your own setup.

| Placeholder | Meaning |
| --- | --- |
| `<RepoDir>` | Your repository checkout. |
| `<GameDir>` | The game folder containing `PCBS2.exe`. |

### Tell the build where your game is

Copy `Directory.Build.local.props.example` to `Directory.Build.local.props` and set `GameDir`. That file is git-ignored, so your path never ends up in a commit. Setting the `PCBS2_GAME_DIR` environment variable or passing `-p:GameDir=...` works too.

### Build and install

```powershell
Set-Location '<RepoDir>'

# Once: install the pinned BepInEx build (checks the archive hash; refuses to replace an existing BepInEx).
.\scripts\Install-Loader.ps1

# Launch the game once and quit, so BepInEx generates BepInEx\interop.

# Fetch and verify the pinned Steamworks Win64 dependency into the ignored local cache.
.\scripts\Get-Steamworks.ps1

# Build; the plugin is copied into <GameDir>\BepInEx\plugins\PCBS2Coop.
dotnet build PCBS2Coop.sln -c Release
```

Close the game before building; Windows locks the plugin files while it runs. The build compiles against the interop assemblies BepInEx generates from your own game; decompiled dummy DLLs can't replace them.

To remove the loader again, run `.\scripts\Uninstall-Loader.ps1`. It restores or removes exactly the files the installer recorded.

</details>

### Verify the install

Start the game, then open `<GameDir>\BepInEx\LogOutput.log` and look for:

```text
[Info   :PCBS2 Coop] PCBS2 Coop 0.24 loaded.
```

The main menu also gains a **Co-op** entry above Options, and a GitHub icon with the mod version in the bottom row.

## Play together

Both players install the same version. On the main menu, click **Co-op**. The list switches to the co-op menu:

- **Host Co-op:** **New Game** names and starts a new co-op world, **Continue** opens the most recently saved one, **Load Game** lists them all (each world has its own autosave, shown as "(auto)").
- **Join a Friend:** enter the host's IPv4 address (`ipconfig` on the host's PC) and port (default `7777`) → **Connect**.
- **Co-op Options:** choose how you host, **Steam** or **LAN**, and the LAN port.
- **Steam:** both players open Steam and launch PCBS2. The host picks Steam in Co-op Options, hosts a world, then Esc → **Invite Friends**. The guest accepts the invite with PCBS2 open on its main menu. Use the in-game friends list if the overlay doesn't open.

LAN remains available when Steam isn't. Dependency and App ID notes are in [third-party/Steamworks.md](third-party/Steamworks.md).

The guest can join at any time while the host is in the workshop, and can leave and rejoin. For LAN, `127.0.0.1` only reaches the same PC; allow the game through Windows Firewall on your private network if prompted, or forward the UDP port for direct-IP play outside your LAN.

**Saves.** The host's co-op worlds are saved in `<GameDir>\Saves\Coop`, separate from solo Free Build saves. The host saves on an interval (Co-op Options, default 5 minutes), on Exit and from the Save screen; the game's own autosaves are skipped in co-op. If a save is interrupted, the next Continue loads the previous good one with a notice. The guest plays from a temporary folder, keeps a copy of the host's latest save in `Saves\Coop\Joined` and can't save, load or reset the world itself. Neither player's solo saves are read or written during a session.

**Pause and leaving.** The Esc menu is personal for both players; the session keeps running. The host's pause-menu Load Game and Reset reload both games. If the host leaves, the guest returns to the main menu with the reason. If the guest leaves, the host keeps playing, and a PC the guest was carrying goes to the storage cupboard. A guest whose connection drops gets a **Rejoin** button.

**Shared PCs.** Changes to PCs (creating, moving, building, cabling, BIOS and the rest) are checked by the host and then happen on both screens. Both players can work on different parts of the same PC at once. If the other player already took that PC, part, bench or table, your action is refused with a short notice on the co-op status line, and the other player's current activity shows under their name.

**Chat and /sync.** Press **Enter** in the workshop to type, **Enter** again to send and **Esc** to cancel; the game ignores your keys while you type. If a game can't show a change, a red **OUT OF SYNC** marker appears in its top-right corner. Type `/sync` to fix it: the host saves the world and both games reload it, which takes a short loading screen. A guest's `/sync` asks the host first; the host answers with `/yes` or `/no`. A PC the guest is carrying goes to the storage cupboard during the reload. `/help` lists the commands.

## Development and testing

```powershell
dotnet run --project tests/PCBS2Coop.Network.Tests -c Release   # network tests, no game needed
dotnet build PCBS2Coop.sln -c Release                           # build and deploy the plugin
```

An opt-in two-instance self test plays a full session on one PC (new world, late join, changes during the load, pause, saves, both players leaving, Continue and rejoin) using disposable save folders. Set `PCBS2COOP_AUTOTEST=host` for one game launch and `=join` for a second; see [AGENTS.md](AGENTS.md) for details. Phase 3 object checks use the opt-in probe scripts in `scripts/` (`Start-Phase3Probe.ps1`, `Stop-Phase3Probe.ps1`, `Test-Phase3Reload.ps1`); the commands and evidence are in the [phase 3 audit](docs/audit/phase-3.md).

Source layout: `src/PCBS2Coop.Network` contains the Unity-free session, handshake, host-ordered world changes and snapshot transfer over LiteNetLib; `src/PCBS2Coop.Plugin` contains the BepInEx/Unity side; `tests/PCBS2Coop.Network.Tests` contains executable network scenarios. Keep Unity calls on Unity's main thread.

Start with the [roadmap](plan.md), the [sync checklist](docs/sync-progress.md), the [two-game tests still to run](docs/needs_testing.md), the [player model plan](docs/playermodel.md), the [audit reports](docs/audit/README.md) and the [contributor notes](AGENTS.md). The [decompile workflow](il2cpp-decompile.md) describes how the research workspace is produced from your own installation; its output is never committed.

## License and disclaimer

[MIT](LICENSE). PC Building Simulator 2 and its assets belong to their respective owners. This project is unofficial and distributes no game files.

The remote player character is the [Base Character](https://shyr.itch.io/base-character) by Shyr, animated with clips from the [Universal Animation Library](https://quaternius.com/packs/universalanimationlibrary.html) by Quaternius; both are released under [CC0](https://creativecommons.org/publicdomain/zero/1.0/).

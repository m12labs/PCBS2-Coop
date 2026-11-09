# PCBS2 Co-op

Build PCs together in **PC Building Simulator 2**. PCBS2 Co-op is an unofficial two-player mod that puts you and a friend in the same Free Build workshop over LAN or Steamworks when available.

Built for BepInEx 6 IL2CPP. **Current version 0.57.** Both players need the same mod version. This build loads co-op saves written by versions 0.16 through 0.57. For an older save, start a fresh co-op world.

All 90 applicable Free Build gameplay checks have passed. The latest two-game runs verified automatic PSU splitters during installation, cold Continue and guest join. Phase 13 covers presentation polish and phase 14 covers release validation. Career starts at phase 15. [Gameplay checklist](docs/needs_testing.md) · [Roadmap](plan.md)

> **Unofficial fan project.** PCBS2 Co-op is not affiliated with the developers or publishers of PC Building Simulator 2. This repository contains no game code or game assets.

## Sync progress

The full tracker lists partial features, game-build limits and open validation. [Sync tracker](docs/sync-progress.md)

| Area | Shared in the current build |
| --- | --- |
| Sessions and saves | LAN, Steam invites, late join, Rejoin, named worlds, host autosaves and backups |
| Building | PC transfers and storage, native part animations, latches, case panels, cables, splitters, power and water cooling |
| PC software | BIOS, shared desktop input, installed programs, benchmarks, GPU Tuner, Fan Control and blue screens |
| Tools and workshop | Thermal paste, finished tool-table results, Lighting, Cable app purchases, Decorator, wallpapers and PC imports |
| Players | Animated avatars, activity labels, carried PC markers, chat and `/sync` |

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

In your repository checkout, install the pinned BepInEx loader. The script verifies the archive and preserves an existing install.

```powershell
Set-Location '<RepoDir>'
.\scripts\Install-Loader.ps1
```

Launch the game once and quit so BepInEx generates `BepInEx\interop`. Fetch and verify the pinned Steamworks dependency, then build and deploy the plugin to `<GameDir>\BepInEx\plugins\PCBS2Coop`.

```powershell
.\scripts\Get-Steamworks.ps1
dotnet build PCBS2Coop.sln -c Release
```

Close the game before building; Windows locks the plugin files while it runs. The build compiles against the interop assemblies BepInEx generates from your own game; decompiled dummy DLLs can't replace them.

To remove the loader again, run `.\scripts\Uninstall-Loader.ps1`. It restores or removes exactly the files the installer recorded.

</details>

### Verify the install

Start the game, then open `<GameDir>\BepInEx\LogOutput.log` and look for:

```text
[Info   :PCBS2 Coop] PCBS2 Coop 0.57 loaded.
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

See the [roadmap](plan.md), [sync tracker](docs/sync-progress.md), [gameplay acceptance](docs/needs_testing.md), [player model plan](docs/playermodel.md), [audit reports](docs/audit/README.md) and [contributor notes](AGENTS.md). The [decompile workflow](il2cpp-decompile.md) describes how to produce the local research workspace from your installation.

## License and disclaimer

[MIT](LICENSE). PC Building Simulator 2 and its assets belong to their respective owners. This project is unofficial and distributes no game files.

The remote player character is an adaptation of Mannequiny by GDQuest, Luciano Muñoz, and contributors under CC BY 4.0. Locomotion uses Quaternius's Universal Animation Library under CC0. [Asset credits and licenses](third-party/Mannequiny.NOTICE.md)

# PCBS2 Co-op

Build PCs together in **PC Building Simulator 2**. This unofficial co-op mod lets you share a Free Build workshop: build, test and tune PCs together.

[![GitHub stars](https://img.shields.io/github/stars/m12labs/PCBS2-Coop?style=flat)](https://github.com/m12labs/PCBS2-Coop/stargazers)
[![GitHub open issues](https://img.shields.io/github/issues/m12labs/PCBS2-Coop)](https://github.com/m12labs/PCBS2-Coop/issues)
[![GitHub last commit](https://img.shields.io/github/last-commit/m12labs/PCBS2-Coop)](https://github.com/m12labs/PCBS2-Coop/commits)

> **No public release yet.** There is no ready-to-install download. To try the development build, [build from source](#installation) using your own copy of the game.

**PCBS2 must be owned and launched through Epic Games Launcher.** The game is not sold on Steam. The mod's optional Steam integration makes joining and inviting friends easier; you still launch the game through Epic.

## At a glance

| Fact | Status |
| --- | --- |
| Players | **2 for now** (one host and one guest); goal: **up to 4 players** |
| Joining | **LAN/direct IP** or optional **Steam invites** |
| Epic Games invites | **Coming soon™** (planned) |
| Free Build gameplay | **[90/90 applicable checks passed](docs/needs_testing.md)** at version 0.57 |
| Automated checks | **[787 passed](docs/audit/phase-16.md#060-implementation-shared-carts-checkout-and-part-sales-16b)** for version 0.60 |
| Development version | **[0.60](Directory.Build.props)** |
| Reference game | **1.17.02 · Windows x64** |

Follow development in the [roadmap](plan.md).

## What's synced?

Free Build highlights below; see the **[full sync tracker](docs/sync-progress.md)** for detailed progress.

| Area | Shared in Free Build |
| --- | --- |
| Building | PCs, parts, storage, panels, latches, cables, power and water cooling |
| Software and tuning | BIOS, desktop input, programs, benchmarks, GPU Tuner, Fan Control and failures |
| Tools and customization | Peripherals, thermal paste, finished tool-table results, case designs, lighting and custom cables |
| Workshop | Decorator, wallpapers and PC imports |
| Sessions and players | Late join, Rejoin, saves, backups, animated avatars, chat and `/sync` |

**Career is in development and has not been tested in game.** The source includes hosting, saves, shared progression and inventory, shop carts, checkout and part sales. [Career checklist](docs/career-testing.md)

## Installation

Building from source requires Windows x64, your own copy of PCBS2 installed through **Epic Games Launcher**, PowerShell and the **.NET SDK 10**. The plugin uses BepInEx 6 IL2CPP and targets .NET 6 for the loader's runtime.

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

From your repository checkout:

```powershell
Set-Location '<RepoDir>'
Copy-Item .\Directory.Build.local.props.example .\Directory.Build.local.props
```

Edit `Directory.Build.local.props` and set `GameDir` to the folder containing `PCBS2.exe`. That file is git-ignored, so your path stays local. Setting the `PCBS2_GAME_DIR` environment variable or passing `-p:GameDir=...` works too.

### Build and install

In your repository checkout, install the pinned BepInEx loader. The script verifies the archive and preserves an existing install.

```powershell
.\scripts\Install-Loader.ps1
```

Launch the game once through Epic Games Launcher and quit so BepInEx generates `BepInEx\interop`. Fetch and verify the pinned Steamworks dependency, then build and deploy the plugin to `<GameDir>\BepInEx\plugins\PCBS2Coop`.

```powershell
.\scripts\Get-Steamworks.ps1
dotnet build PCBS2Coop.sln -c Release
```

Close the game before building; Windows locks the plugin files while it runs. The build compiles against the interop assemblies BepInEx generates from your own game; decompiled dummy DLLs can't replace them.

To remove the loader again, run `.\scripts\Uninstall-Loader.ps1`. It restores or removes exactly the files the installer recorded.

</details>

### Verify the install

Launch PCBS2 through Epic Games Launcher, then open `<GameDir>\BepInEx\LogOutput.log` and look for:

```text
[Info   :PCBS2 Coop] PCBS2 Coop 0.60 loaded.
```

The main menu also gains a **Co-op** entry above Options, and a GitHub icon with the mod version in the bottom row.

## Play together

Both players launch their own copy through **Epic Games Launcher** and need the same mod version and game build. On the main menu, click **Co-op**:

- **Host Free Build:** **New Game** names and starts a world, **Continue** opens the most recently saved one, and **Load Game** lists them all. Each world has its own autosave, shown as "(auto)".
- **Host Career:** the same New Game, Continue and Load Game choices for the Career mode under development.
- **Join a Friend:** enter the host's IPv4 address (`ipconfig` on the host's PC) and port (default `7777`) → **Connect**.
- **Co-op Options:** choose how you host, **Steam** or **LAN**, and the LAN port.

**Steam invites (optional).** Keep Steam running alongside the game on both PCs. The host picks Steam in Co-op Options, hosts a world, then Esc → **Invite Friends**. The guest accepts the invite with PCBS2 open on its main menu. Use the in-game friends list if the overlay doesn't open.

LAN/direct-IP joining works without Steam. Dependency and App ID notes are in [third-party/Steamworks.md](third-party/Steamworks.md).

The guest can join at any time while the host is in the workshop, and can leave and rejoin. For LAN, `127.0.0.1` only reaches the same PC; allow the game through Windows Firewall on your private network if prompted, or forward the UDP port for direct-IP play outside your LAN.

**Saves.** Free Build worlds use `<GameDir>\Saves\Coop`; Career worlds use `<GameDir>\Saves\Coop\Career`. Each native save needs its matching `.coop` sidecar. The 0.60 development build reads co-op pairs written by versions 0.16 through 0.60; for an older save, start a fresh co-op world.

The host saves on an interval (Co-op Options, default 5 minutes), on Exit and from the Save screen; the game's own autosaves are skipped in co-op. If a save is interrupted, Continue restores a readable backup or selects the newest readable save, with a notice. The guest uses a temporary folder and keeps the host's latest save in that mode's `Joined` subfolder. Guests can't save, load or reset the world. Solo saves stay separate.

**Pause and leaving.** The Esc menu is personal for both players; the session keeps running. The host's pause-menu Load Game and Reset reload both games. If the host leaves, the guest returns to the main menu with the reason. If the guest leaves, the host keeps playing, and a PC the guest was carrying goes to the storage cupboard. A guest whose connection drops gets a **Rejoin** button.

**Shared PCs.** The host checks PC changes, which then happen on both screens. You can work on separate PCs at the same time, and your teammate's activity shows under their name.

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

[MIT](LICENSE). PCBS2 Co-op is an unofficial fan project, not affiliated with the developers or publishers of PC Building Simulator 2. The game and its assets belong to their respective owners. This repository distributes no game code or game assets.

The remote player character is an adaptation of Mannequiny by GDQuest, Luciano Muñoz, and contributors under CC BY 4.0. Locomotion uses Quaternius's Universal Animation Library under CC0. [Asset credits and licenses](third-party/Mannequiny.NOTICE.md)

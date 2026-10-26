# PCBS2 Co-op

Build PCs together in **PC Building Simulator 2**. PCBS2 Co-op is an unofficial two-player mod that puts you and a friend in the same Free Build workshop over LAN or Steamworks when available.

Built for BepInEx 6 IL2CPP. **Current version: 0.11** (early development). Both players need the same mod version; a different version is refused when joining.

> **Early development.** You share one workshop, and whole PCs are shared: creating and deleting PCs and moving them between benches, your hands and the storage cupboard show up for both players. Building inside a PC isn't shared yet: installing or removing parts only changes your own screen. That is the next milestone. See [what works](#sync-progress).

> **Unofficial fan project.** PCBS2 Co-op is not affiliated with the developers or publishers of PC Building Simulator 2. This repository contains no game code or game assets.

## Sync progress

A quick look at what's ready and what's next. See the [full sync checklist](docs/sync-progress.md) for individual features and the [roadmap](plan.md) for the order of work.

| Feature | Planned | Partially synced | Synced | Comments |
| --- | :---: | :---: | :---: | --- |
| LAN Host / Join | | | ✅ | Direct IP and port from the main menu. |
| Steam Invite / Join | | ✅ | | Steamworks when available. |
| Join check | | | ✅ | A different mod version or game build is refused with the reason; DLC or part catalog differences join with a warning. |
| Shared Free Build world | | | ✅ | Host picks **New world** or **Continue**; both players load the same workshop. |
| Joining an active session | | | ✅ | Join or rejoin any time; you get the host's current world, including changes made while you load. |
| Co-op saves | | | ✅ | One co-op world, kept apart from your solo saves. The guest keeps a copy of the host's latest save. |
| Leaving | | | ✅ | Host leaving sends the guest back to the menu with the reason; guest leaving lets the host keep playing. |
| Pause | | | ✅ | Either player's pause menu is personal; the session keeps running. |
| Seeing each other | | ✅ | | Position and facing on a placeholder capsule; a PC the other player carries shows as a cardboard box. Character models and animations later. |
| Creating and deleting PCs | | | ✅ | Choosing a case from the inventory and deleting a PC are approved by the host and appear for both players. |
| Carrying, benches and storage | | ✅ | | Picking up, dropping, swapping, storing and retrieving PCs on workbenches are shared. If both players grab the same PC or bench, one wins and the other sees a short notice. A guest who leaves while carrying a PC puts it back in the cupboard. Tool benches come later (phase 9), desks and the shop front with career mode; a world with notes or packages can't be hosted yet. |
| Chat and /sync | | | ✅ | Press Enter to chat. If a game shows "out of sync", `/sync` saves and reloads both games. |
| Co-op saves of shared PCs | | | ✅ | Every PC keeps its identity and location through saves, Continue and rejoining, including a PC the guest was carrying. |
| Installing and removing parts | ✅ | | | Phase 4. |
| Cables, power and peripherals | ✅ | | | Phase 5. |
| Booting, BIOS, OS and screens | ✅ | | | Phase 6. |
| Benchmarks, temperatures and tuning | ✅ | | | Phase 7. |
| Cleaning, water cooling, RGB and customization | ✅ | | | Phases 9–11. |
| Career mode | ✅ | | | After Free Build is complete. |

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
[Info   :PCBS2 Coop] PCBS2 Coop 0.11 loaded.
```

The main menu also gains a **Multiplayer** panel below Free Build.

## Play together

Both players install the same version. On the main menu, open **Multiplayer**:

- **LAN:** select LAN inside Host or Join. Host → **New world** starts a fresh co-op workshop, or **Continue** loads your co-op world. Join → enter the host's IPv4 address (`ipconfig` on the host's PC) and port (default `7777`) → **Connect**.
- **Steamworks (experimental):** both players open Steam and launch PCBS2. The host selects Steam → **New world** / **Continue**, then Esc → **Invite Friends**. The guest accepts the invite with PCBS2 open on its main menu. Use the in-game friends list if the overlay doesn't open.

LAN remains available when Steamworks isn't. See the [Steamworks notes](steamworks.md) for setup and pending two-PC testing.

The guest can join at any time while the host is in the workshop, and can leave and rejoin. New world asks before replacing an existing co-op world. For LAN, `127.0.0.1` only reaches the same PC; allow the game through Windows Firewall on your private network if prompted, or forward the UDP port for direct-IP play outside your LAN.

**Saves.** The host's co-op world is saved in `<GameDir>\Saves\Coop`, separate from solo Free Build saves. The guest plays from a temporary folder and keeps a copy of the host's latest save in `Saves\Coop\Joined`. Neither player's solo saves are read or written during a session.

**Pause and leaving.** The Esc menu is personal for both players; the session keeps running. If the host leaves, the guest returns to the main menu with the reason. If the guest leaves, the host keeps playing, and a PC the guest was carrying goes to the storage cupboard.

**Shared PCs.** Choosing a case, deleting a PC and moving PCs between workbenches, your hands and the cupboard are checked by the host and then happen on both screens. If the other player already took that PC or bench, your action is refused with a short notice on the co-op status line. Two players can work on the same PC, but part changes inside it aren't shared yet.

**Chat and /sync.** Press **Enter** in the workshop to type, **Enter** again to send and **Esc** to cancel; the game ignores your keys while you type. If a game can't show a change, a red **OUT OF SYNC** marker appears in its top-right corner. Type `/sync` to fix it: the host saves the world and both games reload it, which takes a short loading screen. A guest's `/sync` asks the host first; the host answers with `/yes` or `/no`. A PC the guest is carrying goes to the storage cupboard during the reload. `/help` lists the commands.

## Development and testing

```powershell
dotnet run --project tests/PCBS2Coop.Network.Tests -c Release   # network tests, no game needed
dotnet build PCBS2Coop.sln -c Release                           # build and deploy the plugin
```

An opt-in two-instance self test plays a full session on one PC (new world, late join, changes during the load, pause, saves, both players leaving, Continue and rejoin) using disposable save folders. Set `PCBS2COOP_AUTOTEST=host` for one game launch and `=join` for a second; see [AGENTS.md](AGENTS.md) for details. Phase 3 object checks use the opt-in probe scripts in `scripts/` (`Start-Phase3Probe.ps1`, `Stop-Phase3Probe.ps1`, `Test-Phase3Reload.ps1`); the commands and evidence are in the [phase 3 audit](docs/audit/phase-3.md).

Source layout: `src/PCBS2Coop.Network` contains the Unity-free session, handshake, host-ordered world changes and snapshot transfer over LiteNetLib; `src/PCBS2Coop.Plugin` contains the BepInEx/Unity side; `tests/PCBS2Coop.Network.Tests` contains executable network scenarios. Keep Unity calls on Unity's main thread.

Start with the [roadmap](plan.md), the [sync checklist](docs/sync-progress.md), the [player model plan](docs/playermodel.md), the [audit reports](docs/audit/README.md) and the [contributor notes](AGENTS.md). The [decompile workflow](il2cpp-decompile.md) describes how the research workspace is produced from your own installation; its output is never committed.

## License and disclaimer

[MIT](LICENSE). PC Building Simulator 2 and its assets belong to their respective owners. This project is unofficial and distributes no game files.

The remote player character is the [Base Character](https://shyr.itch.io/base-character) by Shyr, animated with clips from the [Universal Animation Library](https://quaternius.com/packs/universalanimationlibrary.html) by Quaternius; both are released under [CC0](https://creativecommons.org/publicdomain/zero/1.0/).

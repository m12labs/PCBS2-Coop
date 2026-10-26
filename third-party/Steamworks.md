# Steamworks dependencies

Facepunch.Steamworks **2.5.2**, release commit `5a22fa2`, from the [official release](https://github.com/Facepunch/Facepunch.Steamworks/releases/tag/2.5.2).

`scripts/Get-Steamworks.ps1` downloads `Facepunch.Steamworks.2.5.2.zip` and selects only `Release/net6.0` Win64 files. SHA-256:

| File | SHA-256 |
| --- | --- |
| Release ZIP | `83ef0b8b07bd5545c3732c65011f0baa9bf003cb53c2279c56397270368bca22` |
| `Facepunch.Steamworks.Win64.dll` | `11de6033ec2009c418c23428c2e64d03c729b95691fb29bacc3b145aa4a4ead5` |
| `steam_api64.dll` | `670d654aa3255c5061cf0236a1cdbb2f5076cbd5ae44f613b6f4921558e83e2d` |

The binding is MIT licensed; see the adjacent `Facepunch.Steamworks.LICENSE`. The native Steamworks SDK library is Valve's and is subject to Valve's SDK/service terms; the binding's MIT license does not relicense it. App ID 480 is the Spacewar example and is used for initial testing. Public release terms/service support must be resolved separately; local technical testing is not evidence of permission to distribute an unrelated mod under that App ID.

Do not ship `steam_appid.txt` or replace a game's native Steam DLL. The mod initializes its own guarded context from its `steam/` folder, with a configurable App ID. See [Steamworks API documentation](https://partner.steamgames.com/doc/sdk/api) and the [Spacewar example](https://partner.steamgames.com/doc/sdk/api/example).

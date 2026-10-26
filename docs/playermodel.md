# Player model plan

Plan written October 25, 2026. Nothing here is implemented yet. It replaces the remote player's capsule with a rigged, animated character from Quaternius (CC0), shipped as a Unity AssetBundle next to the plugin DLL.

Owner decisions (October 25, 2026):

| Decision | Choice |
| --- | --- |
| Model source | Quaternius, CC0 |
| Unity project | In this repo, `unity/PCBS2CoopModels/`; Unity's generated folders are git-ignored |
| Built bundle | Committed, so `dotnet build` deploys it and nobody needs Unity just to build the mod |
| Look per player | One character for everyone, with one clothing color tinted per player. Nothing new goes over the network, so no version bump |

This feeds roadmap phase 13 in [plan.md](../plan.md) ("audit existing character/NPC assets first … resolve a fallback with the owner if none is suitable"). The audit below found no reusable character, and the owner chose this fallback.

## 1. Audit: what the game already has

Checked against the installed game (Unity 6000.2.6f2, HDRP 17.2.0). It is read-only evidence and no game file was changed.

| Question | Finding |
| --- | --- |
| DOTween or another tween library? | **No.** There is no `DG.Tweening`, DOTween, LeanTween, iTween or PrimeTween in the interop assemblies, `GameAssembly.dll` or `global-metadata.dat`. The only "tween" is the global `Tween.EaseInOut(float)` easing helper in Assembly-CSharp. |
| How does the game animate? | Unity `Animator` with authored clips (tablet, cars, ceiling fan, wall switch, delid tools, water-cooling parts). `UnityEngine.AnimationModule` is present. |
| Could we load DOTween ourselves? | Not as a drop-in DLL. It is built against Mono UnityEngine, and BepInEx IL2CPP plugins can only call the game through the generated interop assemblies. Use Animator clips for the body, and `Mathf`/`Lerp` (as the capsule already does) for small tweens. |
| Rigged human / NPC / hands in the base assets? | **None.** `level0-3`, `sharedassets0-3`, `resources.assets` and `globalgamemanagers.assets` contain no `SkinnedMeshRenderer`. Their one `Avatar` is `Tablet_SkeletonAvatar`, the tablet prop's rig. |
| In the 3,052 Addressables bundles (`StreamingAssets/aa`, 7.9 GB)? | **None.** There is 1 `SkinnedMeshRenderer`, the Threadripper CPU installer lid (`AMDT02_Anim_Graph`). The 50 `Avatar`s are automatic import rigs on PC parts, e.g. `MSH_ASUS_ROG_Strix_FlareAvatar` and `MSH_NZXT_N7_Z590Avatar`. The catalog has character *stickers* (`HD_Character_*_Sticker.tga`) but no character meshes. |
| Is the player a body? | No. `Player` is a `CharacterController` + `Walk` with a `Main Camera`; there is no first-person body or arms. |
| Will the engine run an imported character? | Yes. `SkinnedMeshRenderer`, `Animator`, `Avatar`, `AnimatorController` and `AnimationClip` all occur in shipped assets, so their native code is in the build. `UnityEngine.AssetBundleModule` is in the interop. |

Method: the serialized-file headers of every base asset file were parsed, and each bundle's header was decompressed (LZ4) in memory. Objects were then counted by Unity class ID (137 `SkinnedMeshRenderer`, 90 `Avatar`, 95 `Animator`, 74 `AnimationClip`), and the names of matching objects were read.

## 2. Asset choice

**Primary: [Ultimate Animated Character Pack](https://quaternius.com/packs/ultimatedanimatedcharacter.html)** (Quaternius, 2019). It is free and CC0, with 50+ clothed low-poly characters (FBX/OBJ/Blend) and animations included. The characters use flat-colored materials rather than texture maps, which makes per-slot tinting easy.

**Optional upgrade: [Universal Animation Library](https://quaternius.com/packs/universalanimationlibrary.html)** (2025, CC0; the free Standard tier has FBX/glTF). It has a richer locomotion set built on a "universal humanoid rig". Use it only if the chosen character maps cleanly to a Unity **Humanoid** avatar (step 4.2), because that is what lets clips from another rig retarget onto it.

Not chosen: [Universal Base Characters](https://quaternius.itch.io/universal-base-characters). Its itch page asks for payment, and the free base models are in underwear.

Rules:
- Keep the pack's license file next to the imported files (`Assets/ThirdParty/Quaternius/<Pack>/License.txt`). CC0 needs no credit, but add a line to the README credits anyway.
- Commit only the files actually used (one character FBX and any extra animation FBXs), not the whole pack.
- Never import game files (meshes, textures, prefabs from PCBS2) into the Unity project. The repo must not redistribute the game's assets.

## 3. Unity setup

### 3.1 Editor

1. Install **Unity Hub**.
2. Install editor **6000.2.6f2** (the game's exact version). Open the [Unity download archive](https://unity.com/releases/editor/archive), find 6000.2.6f2 and click *Install* to open it in Hub. No extra modules are needed: Windows standalone build support (Mono) comes with the Windows editor, and AssetBundles for `StandaloneWindows64` use it.
3. Do not upgrade the project to another editor version when Unity offers. A bundle from a different editor version may fail to load or misread its serialized data.

### 3.2 Project

Create the project as `unity/PCBS2CoopModels` in the repo:

1. In Hub: *New project* → editor 6000.2.6f2 → template **Universal 3D or 3D (Built-In Render Pipeline)** (the empty one, not a sample). Location: `<repo>/unity`; name: `PCBS2CoopModels`.
   - The HDRP sample template adds hundreds of MB of sample content, so start empty and add HDRP.
2. *Window → Package Manager* → *Unity Registry* → install **High Definition RP 17.2.0** (`com.unity.render-pipelines.high-definition`). Remove URP if the template added it.
3. Run *Window → Rendering → HDRP Wizard → Fix All*, then confirm `Packages/manifest.json` pins `"com.unity.render-pipelines.high-definition": "17.2.0"`.
4. *Edit → Project Settings*:
   - *Editor → Asset Serialization*: **Force Text**; *Version Control*: **Visible Meta Files** (both defaults; check them).
   - *Player → Other Settings → Color Space*: **Linear** (HDRP requires it).
5. Delete any sample scenes or assets the wizard created that the model doesn't need.

HDRP is mainly so the model previews close to how it looks in game. The plugin replaces every material at runtime anyway (section 6).

### 3.3 Git

Commit `Assets/`, `Packages/manifest.json`, `Packages/packages-lock.json` and `ProjectSettings/`. Add these to the root `.gitignore` when the project is created:

```gitignore
# Unity model project: generated folders.
/unity/*/Library/
/unity/*/Temp/
/unity/*/Logs/
/unity/*/UserSettings/
/unity/*/Build/
/unity/*/*.csproj
/unity/*/*.sln
```

The existing `*.log` and `**/obj/` rules already cover Unity logs and `obj/`. Git LFS isn't needed: one low-poly FBX and the bundle are a few MB.

### 3.4 Unity MCP

The owner drives the editor through an MCP bridge. Install **MCP for Unity** (CoplayDev) in this project only: *Package Manager → + → Add package from git URL* → `https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity`. Then follow its README (it needs Python and `uv`) to start the bridge and register it with the client. The package lands in `Packages/manifest.json`; that is fine to commit.

Ground rules for an agent working through the MCP:
- Work only inside `unity/PCBS2CoopModels`. Don't edit `src/`, `tests/` or root docs from the Unity side. The plugin code changes in section 7 are a separate task.
- Don't add C# runtime scripts to the prefab (section 5.4). Editor scripts under `Assets/Editor/` are fine.
- Build the bundle only through the build script in section 5.5.

## 4. Import the character

### 4.1 Pick and copy

1. Download the Ultimate Animated Character Pack from quaternius.com and extract it **outside the repo**.
2. Choose one casual or work outfit (preview the FBXs in Unity first if needed). Copy just that FBX and the license into `Assets/ThirdParty/Quaternius/UltimateAnimatedCharacter/`.

### 4.2 Import settings (select the FBX → Inspector)

**Model tab**
- *Scale Factor*: set it so the character is **1.8 m tall** (the capsule's `Height` in [RemotePlayerView.cs](../src/PCBS2Coop.Plugin/RemotePlayerView.cs)). Check this with a 1×1×1 cube or by reading the renderer bounds. Leave *Convert Units* on.
- *Import BlendShapes*, *Import Cameras*, *Import Lights*: off. *Read/Write*: off.

**Rig tab**
- *Animation Type*: **Humanoid**; *Avatar Definition*: *Create From This Model* → **Configure…**. Every required bone must be mapped (green); fix any missing ones by hand. Check the T-pose (*Pose → Enforce T-Pose*), then *Apply*.
- If Humanoid can't be mapped cleanly, use **Generic**. The pack's own clips still work on Generic, but the Universal Animation Library can't be retargeted onto it.

**Animation tab** (for each clip used)
- At minimum: an idle, a walk and a run (names vary by pack version; pick from the clip list).
- *Loop Time*: on for idle, walk and run.
- *Root Transform Rotation*, *Position (Y)* and *Position (XZ)*: **Bake Into Pose**. The plugin moves the root from network positions, so the clips must not move it.

**Materials tab**
- Write down each material slot's name and color (e.g. skin, hair, shirt, pants, shoes). These become the palette in section 5.3. Choose **one clothing slot** (normally the shirt or jacket) as the tint slot.

### 4.3 Optional: Universal Animation Library

Only with a Humanoid character. Import the UAL FBX into `Assets/ThirdParty/Quaternius/UniversalAnimationLibrary/` with *Rig → Humanoid*, *Avatar Definition → Create From This Model*. Use its idle, walk, jog, sprint and any carry or hold clips in place of the pack's. Keep only the clips you use, by importing a trimmed FBX or extracting the clips (*Ctrl+D* on a clip) and deleting the source.

## 5. Build the prefab and bundle

All under `Assets/PlayerModel/`.

### 5.1 Animator controller: `PlayerModel.controller`

- Parameters: `Speed` (float, m/s, ground speed) and `Carrying` (bool).
- **Base layer**: a 1D blend tree on `Speed` with idle at 0, walk at the game's walk speed and run at about 2× walk.
  - The game's speed is `Walk.walkSpeed` on the local `Player`. Log it once from the plugin to set the thresholds; don't guess.
- **Carry layer** (override, weight 1): an **Avatar Mask** with just the arms (and upper spine if needed). It holds a "hands forward holding a box" pose while `Carrying` is true and an empty state otherwise.
  - If neither pack has a carry clip, make one in the Animation window: a one-frame clip with the arm bones rotated forward. On a Humanoid rig, record it on the muscle curves.
- No root motion; no animation events (there are no scripts to receive them).

### 5.2 Prefab: `PlayerModel.prefab`

```
PlayerModel            Animator (PlayerModel.controller, the model's Avatar, Apply Root Motion off,
 │                              Culling Mode = Cull Update Transforms, Update Mode = Normal)
 ├─ <model mesh>       SkinnedMeshRenderer (Cast Shadows on, Update When Offscreen off)
 ├─ <armature>
 │   └─ …/Hand_R …
 └─ CarryAnchor        empty, about 1.05 m up and 0.45 m forward, where the carried-PC box sits
```

- Pivot at the feet, facing **+Z**, standing at the origin, 1.8 m tall.
- **No** colliders, rigidbodies, lights, cameras, audio sources or MonoBehaviours.
- `CarryAnchor` is optional. If the plugin can't find it, the box goes where it goes today.

### 5.3 Palette

Because the plugin replaces materials at runtime (section 6), colors travel as data. The build script writes a `TextAsset` named `PlayerModel.palette` into the bundle, one line per material slot of the model's renderer, in slot order:

```
# slot name r g b smoothness tint
0 Skin   0.86 0.67 0.52 0.30 0
1 Shirt  0.20 0.45 0.80 0.25 1
2 Pants  0.18 0.18 0.22 0.20 0
```

Exactly one row has `tint` 1. The build script reads these values from the imported materials (`_BaseColor`, or `_Color`/`_MainTex` average if needed), so nobody edits the file by hand.

### 5.4 Why no scripts or materials go in the bundle

- **Scripts**: a MonoBehaviour from this Unity project has no matching type in the game's IL2CPP build, so it would load as a missing script. Unity's built-in components (Animator, SkinnedMeshRenderer, Transform) are fine.
- **Materials and shaders**: a bundled HDRP/Lit material drags its own compiled copy of the shader into the bundle. That makes the bundle much bigger and risks a shader that doesn't match the game's. The capsule already solves this by copying an HDRP/Lit material that is in the scene (`MakeMaterial()`), and the model will do the same.

### 5.5 Build script: `Assets/Editor/BuildPlayerBundle.cs`

Menu item **PCBS2Coop → Build Player Bundle**, also runnable headless. It must:

1. Load `PlayerModel.prefab`, write `PlayerModel.palette` from its renderer's materials, and save a temporary copy of the prefab whose renderer `sharedMaterials` are all `null` (same slot count).
2. Call `BuildPipeline.BuildAssetBundles` with one explicit `AssetBundleBuild`:
   - bundle name `pcbs2coop-player.bundle`;
   - assets: the stripped prefab (as address `PlayerModel`) and `PlayerModel.palette`;
   - options `ChunkBasedCompression` (LZ4); keep type trees (don't pass `DisableWriteTypeTree`);
   - target `BuildTarget.StandaloneWindows64`;
   - output to `unity/PCBS2CoopModels/Build/` (ignored).
3. Copy only `pcbs2coop-player.bundle` to **`src/PCBS2Coop.Plugin/Assets/pcbs2coop-player.bundle`** (committed), delete the temporary prefab, and log the bundle size plus its assets.

Headless build, e.g. for the MCP agent or a check:

```powershell
& "<UnityEditor>\Unity.exe" -batchmode -quit -projectPath unity/PCBS2CoopModels -executeMethod BuildPlayerBundle.Build -logFile -
```

Rebuild and commit the bundle whenever the model, clips, controller or palette change. Commit the Unity sources in the same commit, so they never disagree.

## 6. Runtime design (plugin side)

**Loading** (new `PlayerModelAssets`, main thread, once per process):
- Path: `Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "pcbs2coop-player.bundle")`.
- `AssetBundle.LoadFromFile(path)`, then `bundle.LoadAsset("PlayerModel", Il2CppType.Of<GameObject>()).Cast<GameObject>()`; the same for the palette `TextAsset`.
- Keep the bundle loaded for the process lifetime (never `Unload`). Hold managed references, and set `HideFlags.DontUnloadUnusedAsset` on the prefab, because scene changes run `Resources.UnloadUnusedAssets`.
- Any failure (missing file, load error, missing palette) is logged once with the reason and returns null. The view then builds the capsule exactly as today.

**Building the view** (`RemotePlayerView.Create`):
- Instantiate the prefab under the existing root and remove any colliders defensively.
- For each palette row, set material slot `i` to `MakeMaterial()` with textures cleared, `_BaseColor` from the row and `_Smoothness`/`_Metallic` set the way `Tint()` does. On the `tint` row, use the existing per-player color (host orange, guest blue).
- Keep the name tag. Drop the visor, since the head shows facing. Parent the carried-PC box to `CarryAnchor` when present.

**Animating** (`RemotePlayerView.Update`):
- Ground speed = horizontal distance the smoothed root moved this frame ÷ `Time.deltaTime`, damped (e.g. the same `1 - exp(-k·dt)` factor) so network jitter doesn't flicker between idle and walk.
- `animator.SetFloat("Speed", speed)`. `SetCarrying` also calls `animator.SetBool("Carrying", carrying)`.
- No network or save changes: speed is derived from positions already received, and carrying from accepted facts. No version bump is needed for the swap itself.

**Deploying**: add `Assets/pcbs2coop-player.bundle` to `DeployFiles` in [PCBS2Coop.Plugin.csproj](../src/PCBS2Coop.Plugin/PCBS2Coop.Plugin.csproj). It then lands in `<GameDir>\BepInEx\plugins\PCBS2Coop\` with the DLLs. Release zips carry the same folder.

## 7. Implementation tasks (in order)

1. **Unity project**: section 3. Commit the empty HDRP project plus the `.gitignore` lines.
2. **Character import**: section 4. Record the chosen file, scale factor, rig type (Humanoid or Generic) and palette slots in this doc.
3. **Controller, prefab, palette, build script**: section 5. Commit the sources and the first bundle.
4. **Plugin loader and view**: section 6, with the capsule as fallback. Build against the real interop.
5. **Check in game**: section 8.
6. **Record evidence** in `docs/audit/phase-13.md` (or the active phase's audit) and update [plan.md](../plan.md) / [sync-progress.md](sync-progress.md).

## 8. Acceptance checks

- `LogOutput.log` shows the bundle loading with prefab and palette names; no missing-script or shader errors.
- In the two-instance self test (`PCBS2COOP_AUTOTEST`, see [AGENTS.md](../AGENTS.md)), both screenshots show the character instead of the capsule. AutoTest aims at the remote root position, so it doesn't depend on the capsule shape.
- Standing still plays idle; walking plays walk without sliding feet (tune the blend thresholds); no flicker at walking speed.
- Picking up a PC shows the carry pose with the box at the hands; putting it down clears both.
- Host and guest models show different tint colors; everything else matches the palette.
- Menu → world → menu → world again (host and guest): the model still appears, with no unloaded-asset errors.
- Delete the deployed bundle: the capsule appears and one warning is logged. Restore the bundle.
- No visible frame-time cost with two players (one skinned mesh of a few thousand triangles).

## 9. Risks and open points

| Risk | Handling |
| --- | --- |
| The pack's rig may not map cleanly to Humanoid | Fall back to Generic with the pack's own clips; skip the Universal Animation Library. |
| The pack may not include a carry pose | Author a one-frame arm pose clip (section 5.1). |
| The AssetBundle API through Il2CppInterop is untested in this mod | The module is present in the interop. Verify `LoadFromFile`/`LoadAsset` in the first in-game check before building more on it. |
| Editor version drift | Keep 6000.2.6f2. Re-check the game's Unity/HDRP version after game updates (`PCBS2_Data/resources.assets` lists `render-pipelines.high-definition@17.2.0`). |
| Clip names differ between pack versions | Pick clips in Unity and record the names chosen here; code refers only to the `Speed`/`Carrying` parameters, not to clip names. |

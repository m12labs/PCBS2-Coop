# Steamworks connection and invite plan

Research date: 2026-10-26. Status: implemented in version 0.9; local Steam/game checks passed, two-PC invitation and relay acceptance pending. The design below records the agreed implementation; current evidence and remaining checks follow.

## Implementation status

- Added a shared transport contract, preserving the default LiteNetLib LAN entry points and shared compatibility/admission/world pipeline.
- Added `PCBS2Coop.Steam`, using pinned Facepunch.Steamworks 2.5.2 Win64/.NET 6, manual main-thread callbacks, guarded native loading, private two-seat lobbies, lobby/session-bound hello, Steam NetworkingSockets P2P, bounded framing and queue retry, and sequenced movement.
- Added Steam/LAN controls, ready-host pause-menu invites and the scrollable friends fallback. App ID defaults to `480` and is configurable at the runtime boundary. Existing Epic paths were not changed.
- Version is now 0.9. Native/mod state layout is unchanged; 0.9 can read the exact 0.8 paired-save format, while older/unknown versions still fail. No identity or save migration to Steam IDs occurs.
- `scripts/Get-Steamworks.ps1` verifies the official release archive and the two chosen DLLs before use. Build output keeps optional Steam dependencies under `steam/`, away from any game-root native library. The upstream MIT notice is included; Valve SDK/service distribution terms remain a separate release consideration.

### Verified evidence

Automated checks: **290 network/model checks pass**, including the original 260 scenarios, oversized world-state framing, native-queue retry, bounded bulk/control queues, writer ownership, malformed fragments, stale movement, stalled transfers, shared framed-session admission, malformed-Welcome timeout and the 0.8 save reader. Release solution build succeeds with no warnings/errors.

Local Steam client smoke: App ID 480 initialization, friend enumeration, private lobby creation, P2P listener creation, readiness and cleanup passed. No invites were sent. This standalone harness runs with `dotnet run --project tests/PCBS2Coop.Steam.Smoke -c Release -- --steam`.

Running game: the actual BepInEx .NET 6 process initialized Steam under 480, hosted a fresh co-op world, reached world/lobby readiness, opened the native pause menu, opened the friends fallback, returned to the main menu and closed its lobby. Owner save hashes were unchanged. The overlay reported unavailable on this launch, so fallback is the tested local route. Screenshots exposed inherited canvas scaling, which was corrected with independent UI canvases. This probe sends no invites. Run `scripts/Test-SteamLocal.ps1` with no existing game process; it records backups/logs in ignored `.local/steamworks/game-*`, screenshots/results under `<GameDir>/BepInEx/PCBS2Coop-diag/steam/<run>/`, and leaves the latest build installed. It uses fresh disposable saves and a temporary player identity.

The two-PC test is intentionally pending for the owner. Local lobby/listener success does not prove invitation acceptance, relay eligibility/routing, native-world snapshots over WAN, or the overlay on another installation. Lifecycle race handling is implemented and source-inspected; cancellation/stale-invite and backend-loss behavior still require the manual checks below.

### Manual two-PC acceptance

Use two different Steam accounts and preferably separate networks. Install the same 0.9 build and its complete `steam/` folder on both machines. Keep both games open, use fresh test co-op worlds, and back up existing paired saves before testing.

1. Host selects Steam → New world and waits for the workshop. The guest stays on the main menu. Host Esc → Invite Friends; use the friends picker if the overlay does not appear. Guest accepts the received Steam lobby invite. Confirm world download/admission and that each player sees the other.
2. Move, create/delete a case, pick up/drop/store/retrieve a PC, pause and switch focus while the other peer moves. Confirm the existing shared actions remain consistent. Try a late join while the host makes changes and a host save copy.
3. Guest leaves while carrying a PC; confirm host stays in the world and recovers the PC to storage. Guest accepts a fresh invite and rejoins. Then host leaves; confirm guest returns to the main menu. Rehost/Continue and join again.
4. Accept while already in a solo/co-op world: expect a return-to-menu notice without replacing that world. Cancel create/join, accept twice, try a stale/full/unrelated lobby and a mismatched game/mod build; verify readable failure and that a new attempt still works.
5. Test Steam offline/backend loss, missing Steam dependency and disabled Steam. LAN must remain available for a later session. Check the fallback with no online friends and verify failed invites report a result.
6. Capture both BepInEx logs. Search `[net] Steam route:` for native route details, ping, pending reliable bytes and managed queue high-water mark. Include an actual relayed route; a direct link or LAN test is insufficient. Record snapshot size/time, failures and the mod/game/App IDs on both ends. Do not treat App ID 480 public distribution as approved by this test.

## Goal and agreed behavior

Add Steam lobby invitations and Steam P2P/relay as an alternative to the existing LAN connection. Start with App ID `480` (Spacewar), and centralize App ID selection so a future official PCBS2 Steam integration can use the proper ID after validation.

The owner confirmed these choices:

- Keep Epic's existing launch, login and ownership behavior exactly as it is. This work adds no entitlement checks and changes no Epic code.
- Steam carries gameplay and snapshots as well as invitations. Retain direct-IP LAN as a separate option.
- Put a Steam/LAN selector inside the existing Host and Join dialogs. Put Invite Friends in the host's in-game pause menu.
- Initially require both players to open PCBS2 normally before accepting a Steam invite. Launching a closed Epic game from an invite is outside this version.
- Use the native Steam invite overlay when available. Otherwise, Invite Friends opens an in-game Steam friends list that sends the same Steam lobby invitations.
- Preserve one host and one guest, host authority, current synchronization coverage, and existing co-op saves. This is a connection feature, not an expansion of what gets synchronized.

## Research findings and limits

### App ID and startup

Valve identifies `480` as the Spacewar example's App ID. It is a useful initial test target; the example documentation does not establish permission or long-term service guarantees for distributing an unrelated public mod under that ID. Treat public distribution under `480` as a separate release question, not as a technical guarantee. [Valve Spacewar example](https://partner.steamgames.com/doc/sdk/api/example)

Steam API initialization requires Steam running, an appropriate Steam license for the selected App ID, and matching Windows user/elevation context. Valve documents `steam_appid.txt` for development and says not to ship it with builds. `SteamAPI_RestartAppIfNecessary` can launch the Steam-installed app for an ID; it is unsuitable for launching Epic PCBS2 under `480`. [Steamworks API overview](https://partner.steamgames.com/doc/sdk/api)

**Implementation choice:** use Facepunch.Steamworks **2.5.2**, pinned rather than floating, as an ordinary managed .NET binding. Its Win64 project targets .NET 6 without a Unity dependency. Use `Facepunch.Steamworks.Win64.dll` and its matching `steam_api64.dll`; do not import its Unity example into the game. Retain license notices and record dependency provenance/hashes when packaging. [Pinned release](https://github.com/Facepunch/Facepunch.Steamworks/releases/tag/2.5.2), [Win64 project](https://github.com/Facepunch/Facepunch.Steamworks/blob/2.5.2/Facepunch.Steamworks/Facepunch.Steamworks.Win64.csproj)

The binding's `SteamClient.Init(appId, asyncCallbacks: false)` sets process-local Steam App ID variables. This avoids a required shipped `steam_appid.txt`; it does not resolve the distribution question above. Pump `SteamClient.RunCallbacks()` explicitly on the Unity main thread. [Pinned SteamClient source](https://github.com/Facepunch/Facepunch.Steamworks/blob/2.5.2/Facepunch.Steamworks/SteamClient.cs)

### Invitations and overlay

Steam lobbies support invitations through `InviteUserToLobby`, an overlay friend picker through `ActivateGameOverlayInviteDialog`, and acceptance through `GameLobbyJoinRequested_t` when the game is running. Closed-game acceptance follows the App ID's launch route with `+connect_lobby`; it does not establish a launch path to the Epic executable. [Steam matchmaking and lobbies](https://partner.steamgames.com/doc/features/multiplayer/matchmaking)

For a process started outside Steam, Valve says API initialization must occur before graphics-device initialization for overlay hooking. BepInEx plugin initialization may be too late in this game. API success and working invitations therefore do not prove overlay availability. The agreed friend-list fallback avoids making overlay injection a prerequisite. Do not add an early loader, launcher or graphics hook in this version. [Steam overlay requirements](https://partner.steamgames.com/doc/features/overlay)

### Networking

Use `ISteamNetworkingSockets` P2P through Steam identities, with relay access initialized early. A lobby is discovery/signaling; it does not turn the current LiteNetLib UDP socket into a Steam relay connection. Use the Steamworks-backed implementation, not the open-source GameNetworkingSockets build as a substitute for access to Valve's network. Actual relay operation under `480` remains a runtime acceptance gate. [Steam Datagram Relay](https://partner.steamgames.com/doc/features/multiplayer/steamdatagramrelay), [relay initialization/status API](https://partner.steamgames.com/doc/api/ISteamNetworkingUtils)

Steam reliable messages preserve ordering, but unreliable messages may arrive out of order. Sends can fail when the send queue is full. Implement these semantics explicitly rather than assuming equivalence to LiteNetLib's `Sequenced` delivery. [NetworkingSockets API](https://partner.steamgames.com/doc/api/ISteamNetworkingSockets)

The SDK header bundled with the pinned binding limits one sent message to 512 KiB; its default pending send buffer is also 512 KiB. The mod permits 1 MiB of mod state in a snapshot mark, even though file chunks are only 32 KiB. Fragmenting only the native save file would miss this case. [Pinned SDK networking header](https://github.com/Facepunch/Facepunch.Steamworks/blob/2.5.2/Generator/steam_sdk/steamnetworkingtypes.h), [current protocol limits](src/PCBS2Coop.Network/Protocol.cs)

**Binding-specific finding:** in Facepunch 2.5.2, the receive dispatch passes receive time and message number in the reverse order to the public callback parameter names. Do not use the labeled callback message number to implement movement sequencing. Use an explicit sequence in the mod's Steam envelope. This finding is from source inspection, not a reproduced game failure. [SocketManager](https://github.com/Facepunch/Facepunch.Steamworks/blob/2.5.2/Facepunch.Steamworks/Networking/SocketManager.cs), [ConnectionManager](https://github.com/Facepunch/Facepunch.Steamworks/blob/2.5.2/Facepunch.Steamworks/Networking/ConnectionManager.cs)

## Architecture and interfaces

The current [CoopSession](src/PCBS2Coop.Network/CoopSession.cs) directly owns LiteNetLib connection setup, admission, message delivery and disconnect handling. [CoopController](src/PCBS2Coop.Plugin/CoopController.cs) owns native world loading and snapshot creation. Preserve that separation while extracting the socket-specific parts.

| Component | Planned responsibility |
| --- | --- |
| Network transport contract | Unity-free `ISessionTransport`, transport connection events, owned packet bytes, reliable-ordered/latest-state delivery, ping, queue capacity, close reason and disposal. No Steam SDK types. |
| LiteNetLib adapter | Existing IPv4/port behavior, preconnection identity data, rejection reasons and delivery guarantees. Default for existing callers/tests. |
| New `PCBS2Coop.Steam` project | .NET 6 library referencing Network and the pinned binding. Own Steam runtime, lobby coordinator and P2P transport. No Unity references. |
| `CoopSession` | Shared compatibility validation, Welcome, world/snapshot messages, guest admission and link-loss semantics for either adapter. |
| Plugin/controller/UI | Select transport, run callbacks on the main thread, connect lobby events to the existing world lifecycle, and present controls/status. |

Keep existing `Host(int, ...)` and `Join(string, int, ...)` as LAN entry points. Add startup overloads accepting a configured transport adapter for Steam; the session owns that adapter's connection lifetime. Select one transport per session, only while stopped. Do not bridge LAN and Steam in one world or switch a live session automatically.

Expose a binding-free Steam service interface to the plugin: availability/failure reason, local Steam identity, create/join/leave lobby, invite acceptance, enumerate friends, send invitation and open invite overlay. Load the optional Steam implementation inside a guarded bootstrap so missing managed/native dependencies cannot prevent LAN startup. Keep LiteNetLib serializers in Network; this does not require replacing payload serialization.

The pinned binding provides these implementation entry points:

| Capability | Binding API |
| --- | --- |
| Create/join | `SteamMatchmaking.CreateLobbyAsync(2)`, then `Lobby.SetPrivate()`; guest uses `new Lobby(id).Join()` and checks its `RoomEnter` result. |
| Invite/fallback picker | `SteamFriends.GetFriends()`, `Lobby.InviteFriend(friend.Id)`. |
| Overlay/acceptance | `SteamUtils.IsOverlayEnabled`, `SteamFriends.OpenGameInviteOverlay(lobby.Id)`, `SteamFriends.OnGameLobbyJoinRequested`. |
| P2P | `SteamNetworkingSockets.CreateRelaySocket<T>(0)`, `ConnectRelay<T>(hostId, 0)`, `Connection.SendMessage`, and explicit manager `Receive()` calls. |

`CreateLobbyAsync` initially creates an invisible lobby; check the private-setting result before publishing readiness. Prefer `Lobby.Join()` because it returns the explicit entry result. Override the host manager's `OnConnecting` before use: its default implementation accepts connections. Use `Reliable | NoNagle` for ordered frames and `Unreliable | NoDelay | NoNagle` for movement. Copy received payloads before returning from callbacks because the managers release native messages afterward. [Pinned lobby implementation](https://github.com/Facepunch/Facepunch.Steamworks/blob/2.5.2/Facepunch.Steamworks/Structs/Lobby.cs), [matchmaking](https://github.com/Facepunch/Facepunch.Steamworks/blob/2.5.2/Facepunch.Steamworks/SteamMatchmaking.cs), [friends](https://github.com/Facepunch/Facepunch.Steamworks/blob/2.5.2/Facepunch.Steamworks/SteamFriends.cs), [P2P methods](https://github.com/Facepunch/Facepunch.Steamworks/blob/2.5.2/Facepunch.Steamworks/SteamNetworkingSockets.cs), [send methods](https://github.com/Facepunch/Facepunch.Steamworks/blob/2.5.2/Facepunch.Steamworks/Networking/Connection.cs)

### Steam runtime ownership

- Add BepInEx settings `Steam.Enabled = true` and `Steam.AppId = 480`; App ID changes require a game restart. Keep the selected connection method separately, defaulting to Steam when available and LAN otherwise.
- Attempt initialization at the earliest existing plugin initialization point after configuration/logging. Preserve any pre-existing app context. If an incompatible Steam context/native module is already active, disable this integration with a clear reason rather than overriding it.
- Resolve the matching x64 native library from the mod's dependency directory. Do not replace a game's existing root Steam DLL or permanently change global DLL search paths. A collision is a diagnosed unavailable state, not an invitation to overwrite files.
- Disable binding background callbacks. Pump callbacks and network receive queues from `CoopRunner` every frame, including menus, loading, pause and loss of focus. Async lobby completions must queue work back to this driver; they must not call Unity from a continuation thread. Route `Dispatch.OnException` to diagnostics so a failed callback cannot disappear silently.
- Leave lobbies and close session handles on stop. Keep the runtime alive for later invitations. Unsubscribe and shut it down on game exit only if this mod initialized and owns it.

### Shared handshake and wire behavior

- Factor identity/compatibility validation out of LiteNetLib's connection callback. LAN can retain its preconnection hello; Steam sends a bounded reliable hello after the authenticated socket connects. Both paths reach the same validation and Welcome logic.
- Steam hello identifies the lobby session and carries the existing connection key and `PeerIdentity`. Before accepting, require the remote Steam identity to be the expected member of the current lobby, and reserve only one guest slot. Being a lobby member does not skip compatibility checks.
- Use Steam IDs for routing/connection checks. Preserve the mod's durable `PlayerId`, actor epochs, operation IDs and object IDs; do not rewrite save identities to Steam IDs.
- All Welcome, world, snapshot and result messages share one reliable ordered stream. Keep the snapshot mark's order relative to mutations. Do not put snapshot/control messages on independently ordered lanes.
- Send player positions unreliably with no-delay behavior and a monotonically increasing 64-bit adapter sequence. Drop duplicate/older sequences before delivering `LatestState`. Reset sequence state per connection and never replay queued old positions after recovery.
- Add a small Steam-only envelope distinguishing hello, reliable fragments and latest-state payloads. Use the mod version as the compatibility version; introduce no separate protocol version. Validate envelope sizes/kinds before allocation or parsing.

### Fragmentation and backpressure

- Fragment every oversized reliable application message, including Welcome/world-state/snapshot marks, into frames no larger than 64 KiB including their header. Normal 32 KiB snapshot chunks fit directly.
- Include logical message ID, total length and fragment offset. Permit one in-progress reliable reassembly per connection, require contiguous offsets and consistent lengths, and cap a complete message at 2 MiB. Existing semantic limits, including the 1 MiB mod-state cap, still apply after reassembly.
- Own queued buffers: current writers are reused immediately after send calls. Copy or transfer ownership before returning. Release native receive buffers according to the binding's lifetime, and clear partial/queued data on every disconnect.
- Bound the managed reliable outbox to 4 MiB per connection. Pace snapshot pumping by available capacity, reserving 2 MiB for non-bulk traffic. Do not advance a snapshot offset or dequeue an envelope until the corresponding send is accepted.
- When the native send queue is full, keep its unsent head and retry on later polls without reordering or duplicating frames. Stop bulk production while backpressured. If non-bulk traffic cannot fit, fail the connection explicitly rather than dropping an ordered change.
- Use a 30-second connection/hello timeout and a 60-second no-progress timeout for queued reliable data or an incomplete reassembly. Retain the existing 180-second native guest-load timeout. Record timeout reasons; measure WAN transfer behavior before increasing limits.

## Lobby and user flows

Use a private, invite-only lobby with a two-member limit and virtual P2P port `0`. Steam automatically reassigns a departed lobby owner's position; the mod must retain the original host identity and end that session instead of interpreting this as host migration. Closing lobby admission also blocks invited users, so reopen it when the guest leaves and the host is ready. [Lobby types, ownership and admission](https://partner.steamgames.com/doc/api/ISteamMatchmaking)

Store only rendezvous data in lobby metadata: mod discriminator `com.macery12.pcbs2coop`, mod version, game version/build GUID, original host Steam ID, random per-host-session identifier and readiness. Keep metadata small and treat it as hints, not authority. Compare discriminator/session/host before connecting; recheck full compatibility in the handshake. Shared use of `480` makes rejecting unrelated lobbies necessary. Do not upload saves or world state to lobby metadata/chat.

### Hosting

1. In the existing Host dialog, select Steam or LAN. LAN keeps its port field; Steam hides it. New world/Continue and replacement confirmation retain their current meaning.
2. For Steam, asynchronously create the lobby and P2P listener before starting the normal co-op load. Keep the lobby nonjoinable until the world passes `WorldReadiness` and its generation starts. Show progress and allow cancellation.
3. On readiness, publish rendezvous metadata and enable admission. If lobby/listener setup fails, stay at the main menu with the reason. If world setup fails, stop/leave and use the existing save-folder restoration path.
4. Add Invite Friends to the host's pause menu, visible only for a ready Steam-hosted world. Disable it while the guest slot is occupied/reserved.
5. Prefer the Steam invite overlay when available. The fallback is a scrollable in-game list of Steam friends with display name, online state and Invite. Enable Invite for online friends, send through the lobby API, and show sent/failure status. Keep a Use in-game friends list action available if an apparently enabled overlay fails to appear.

The pause-menu attachment point must be checked in the actual runtime hierarchy. Declarations expose `InGameMenu.newButtons` and canvas groups, but inspection alone does not prove a safe cloned-button placement. Clone the game's visual style, remove copied actions and follow active/fade state. Keep the agreed placement in the existing pause menu.

### Joining

1. Both players launch PCBS2 normally with Steam running. Join's Steam tab explains that an accepted Steam invitation starts the connection; its LAN tab retains IP/port. This version adds no public lobby browser.
2. Handle acceptance only from an idle main menu. If the player is already hosting, joining, loading or in a solo/co-op world, show a return-to-menu message and require them to accept again. Do not leave an active world or replace its save implicitly.
3. Join the indicated lobby, check the rendezvous fields, capture its original host/session identity and connect to that Steam peer. Reject unrelated, full, no-longer-ready or replaced sessions with a readable reason.
4. Run the existing mod/game compatibility handshake and then the normal `GuestNeedsWorld` → host snapshot → guest native load → `CompleteAdmission` flow. Lobby entry or socket connection alone never means world admission.
5. Use a local attempt identifier to invalidate late lobby/join callbacks after cancellation, failure or a newer attempt. Leaving a failed attempt must not tear down a newer session.

### Departure and failures

- Guest leaves: close that connection, leave its lobby membership, run existing actor/claim cleanup and carried-PC recovery, and keep the original host/world alive. Reopen admission after cleanup.
- Host leaves, its original identity disappears, or lobby ownership changes away from it: guest leaves through the existing return-to-menu path. No host migration.
- A Steam API/backend loss stops the affected Steam session with a reason and releases lobby/connection state. LAN remains available for a later session; do not attempt an automatic mid-world transport switch.
- Steam unavailable, wrong App ID/license/context, missing dependency, refused hello, full lobby, failed relay route, transfer stall and malformed envelope each produce useful status. Reuse current co-op save isolation and restore folders on the normal menu path.
- Steam overlay/friends UI is local presentation. Keep pumping the session while the host's game is paused; do not introduce shared pause.

## Delivery stages and acceptance

1. **Prove the environment first.** On an isolated owned game run, initialize the pinned x64 binding under `480`, record active App ID, verify a lobby invitation reaches a second Steam account and acceptance returns to the running mod. Test the fallback friend list independently of overlay. Stop and document a concrete blocker if these APIs do not work in this process.
2. **Extract the transport seam.** Move LiteNetLib specifics behind the contract without changing LAN behavior. Run the current network scenarios before adding Steam routing.
3. **Implement Steam transport and lobby lifecycle.** Add shared hello validation, fragmentation, latest-state sequencing, queue bounds/cancellation and explicit close reasons. Connect them to existing admission and departure recovery.
4. **Integrate the approved UI and packaging.** Add the selector, pause-menu invitation entry and fallback friends list. Deploy the managed adapter/binding and matching x64 native dependency with notices and dependency hashes. Keep the dependency load optional.
5. **Accept on two machines/accounts and separate networks.** Verify P2P/relay, late join, snapshots, game actions, pause, departure and rejoin. A same-machine LAN or socket-pair test cannot prove Steam relay behavior. Do not reuse the existing two-instance test on one Steam account as proof of two independent Steam peers.
6. **Release deliberately.** Record service behavior and resolve the `480` distribution/dependency terms question before describing public distribution as supported. Bump the repository's single mod version before handing out an implementation build; use the next increment from the then-current version. Documentation alone needs no version bump.

### Test matrix

| Area | Required scenarios and expected result |
| --- | --- |
| Existing network suite | LAN loopback, compatibility, fault injection, snapshots, object/activity/lifecycle/transfers still pass. Default tests need neither Steam nor Unity. |
| Handshake | Matching identities admit once; mod/build mismatch, malformed hello and extra guest refuse with a reason. Steam lobby metadata cannot bypass validation. |
| Reliable delivery | Fault-injected saturation, retry, cancellation and disconnect preserve order or fail explicitly. Writer reuse does not corrupt queued bytes; native buffers do not leak. |
| Oversized messages | Maximum legal mod-state snapshot mark reassembles intact; frame/logical-size boundary cases pass. Negative/inconsistent lengths, skipped offsets and oversized claims fail before unbounded allocation. |
| Movement | Duplicate and reordered unreliable sequences never roll the remote position backward. Loss is tolerated; reconnect resets sequence tracking. |
| Lobby races | Cancel during create/join/connect, accept twice, stale invite, unrelated Spacewar lobby, full/reserved seat and original-host departure leave no leaked handles or unintended world loads. |
| Steam dependencies | Steam stopped/offline, wrong account context, missing/mismatched DLL and incompatible active App ID give a reason while LAN still starts. |
| Invite presentation | Overlay enabled/disabled and fallback friends list both send usable invites. Names render safely; no friends/offline friends/API failure show sensible states. |
| Owned game acceptance | Two accounts on separate networks connect without manual port forwarding; record relay/route status, ping, queue high-water mark, snapshot bytes/time and failures. Include an actual relayed route, not merely a direct connection. |
| World lifecycle | Late join during mutations, hashed snapshot/admission, host save copy, New world/Continue, pause/focus loss, guest carried-PC recovery, host departure and subsequent rejoin match existing contracts. |
| Safe setup | Existing self-test remains explicitly LAN by default. Steam tests opt in, isolate saves on both machines and close only test-owned processes. |

For implementation validation:

```powershell
dotnet run --project tests/PCBS2Coop.Network.Tests -c Release
dotnet build PCBS2Coop.sln -c Release -p:DeployToGame=false
```

Run isolated game acceptance separately after closing only test-owned instances, deploying intentionally and comparing deployed DLL hashes. Log transport, App ID, connection phase/end reason and routing/queue metrics. Do not log Epic credentials, login tokens or full snapshot contents. Keep evidence labeled as source-inspected, automated-test verified or running-game verified.

## Eventual official PCBS2 App ID

Keep the configured App ID at the runtime boundary; do not bake `480` into message handlers, gameplay or saves. A later change should replace the default only after all of these checks:

- Confirm the actual PCBS2 App ID and appropriate publisher-supported use/backend configuration. A future Steam listing is conditional, not a fact assumed by this document.
- Check whether Epic owners can initialize against that App ID. A Steam license requirement can prevent an Epic-only account from using it; changing a number does not grant that license. [Initialization requirements](https://partner.steamgames.com/doc/sdk/api)
- Inspect the future Steam build's native integration and callback/runtime ownership. Add a tested coexistence/reuse path rather than loading a competing SDK or shutting down the game's runtime.
- Repeat invites, fallback UI, relay and launch tests under the real context. Treat `480` and official-ID lobbies as separate populations; require peers to use the same selected App ID.
- Keep the current game-build compatibility check. If Epic and Steam builds have different GUIDs, cross-store play needs a separate evidence-backed compatibility decision; do not weaken the check as part of App ID migration.
- Keep Epic behavior unchanged. If Epic-only users cannot use the official Steam context, retain a clearly documented supported connection option rather than silently moving them into a broken configuration.

This makes the code ready to select the proper ID, while keeping licensing, publisher setup and future native-runtime coexistence explicit migration gates. No App ID switch requires rewriting co-op save identities or format solely because of the ID.

## Evidence boundary

The original plan combined repository source inspection, Valve documentation, pinned binding source and owner-approved choices. Implementation evidence is now recorded at the top of this file; claims of running-game verification are limited to those checks. Remote invites/relay remain pending. Existing Epic ownership behavior is accepted as the owner's baseline and was not audited or changed by this work.

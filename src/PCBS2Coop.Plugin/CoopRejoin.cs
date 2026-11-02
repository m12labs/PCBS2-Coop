using UnityEngine;
using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>
/// Getting a guest back in (owner decisions, phase 8):
/// <list type="bullet">
/// <item>A guest that leaves the host's world unexpectedly gets a Rejoin button on the reason message. It joins the
/// same address and port, or the same Steam lobby, again.</item>
/// <item>While a world is loaded, the host remembers where each guest player stood. When that player is in the world
/// again (a rejoin or a /sync), the host tells it, and the guest's game moves its player there instead of leaving it
/// at the host's saved position. Nothing of this is saved.</item>
/// </list>
/// </summary>
internal sealed partial class CoopController
{
    /// <summary>Where the guest last joined: a LAN address and port, or a Steam lobby.</summary>
    private sealed class JoinTarget
    {
        public JoinTarget(string address, int port, ulong lobby) { Address = address; Port = port; Lobby = lobby; }
        public string Address { get; }
        public int Port { get; }
        public ulong Lobby { get; }
    }

    private JoinTarget _lastJoin;
    private bool _rejoinOffered;
    /// <summary>Host: where each guest player last stood in this world.</summary>
    private readonly Dictionary<PlayerId, PlayerState> _guestPoses = new();
    /// <summary>Guest: where the host says this player stood; applied once the world is ready.</summary>
    private PlayerState? _spawnAt;

    /// <summary>The guest is leaving the host's world: offer to rejoin unless the host ended the session.</summary>
    private void OfferRejoin(string reason)
    {
        _rejoinOffered = Session.Role == Role.Client && _lastJoin != null &&
            !reason.StartsWith("The host ended", StringComparison.Ordinal) && !reason.StartsWith("The host closed", StringComparison.Ordinal);
    }

    /// <summary>The message shown on the main menu offers Rejoin. Read once.</summary>
    public bool TakeRejoinOffer() { var r = _rejoinOffered; _rejoinOffered = false; return r; }

    /// <summary>The last join was through Steam (its popup is the Steam joining one).</summary>
    public bool RejoinsSteam => _lastJoin?.Lobby != 0;
    public string RejoinLabel => _lastJoin == null ? "" : _lastJoin.Lobby != 0 ? "the host's Steam lobby" : $"{_lastJoin.Address}:{_lastJoin.Port}";

    /// <summary>Join the same host again. Call on the main menu.</summary>
    public bool Rejoin(out string error)
    {
        error = null;
        if (_lastJoin is not { } target) { error = "There is no earlier session to rejoin."; return false; }
        Plugin.Logger.LogInfo($"[world] rejoining {RejoinLabel}.");
        if (target.Lobby != 0) { OnSteamInvite(target.Lobby); return true; }
        return Join(target.Address, target.Port, out error);
    }

    private void OnGuestState(GuestState state)
    {
        if (Session.Role != Role.Host || state != GuestState.InWorld || Session.RemoteIdentity == null) return;
        if (!_guestPoses.TryGetValue(Session.RemoteIdentity.Player, out var at)) return;
        if (Session.SendSpawn(at)) Plugin.Logger.LogInfo($"[world] told the guest where it stood when it left ({at.X:F2}, {at.Y:F2}, {at.Z:F2}).");
    }

    private void UpdateRejoinPosition(bool inWorkshop)
    {
        if (Session.Role == Role.Host)
        {
            if (inWorkshop && Session.IsJoined && Session.GuestState == GuestState.InWorld && Session.RemoteIdentity != null &&
                _remote.InWorkshop && _lastRemoteAt >= 0 && Time.realtimeSinceStartup - _lastRemoteAt < RemoteTimeout)
                _guestPoses[Session.RemoteIdentity.Player] = _remote;
            return;
        }
        if (_spawnAt is not { } spawn || !inWorkshop || !_readiness.Ready || Session.World is not { Admitted: true }) return;
        _spawnAt = null;
        try
        {
            if (MoveLocalPlayer(spawn)) Plugin.Logger.LogInfo("[world] moved this player back to where it stood when it left.");
            else Plugin.Logger.LogWarning("[world] could not find this player to move it back.");
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[world] moving this player back failed: " + e.Message); }
    }

    /// <summary>Put the local player's feet at <paramref name="at"/>, facing its yaw.</summary>
    private static bool MoveLocalPlayer(PlayerState at)
    {
        if (LocalPlayer.Find() is not { } now || LocalPlayer.Controller is not { } cc || cc == null) return false;
        var root = cc.transform;
        var offset = new Vector3(now.X, now.Y, now.Z) - root.position;
        var enabled = cc.enabled;
        cc.enabled = false;
        root.position = new Vector3(at.X, at.Y, at.Z) - offset;
        root.Rotate(0f, Mathf.DeltaAngle(now.Yaw, at.Yaw), 0f, Space.World);
        cc.enabled = enabled;
        return true;
    }
}

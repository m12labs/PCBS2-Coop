using UnityEngine;
using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>Owns the network session, its world-change pipeline and the remote player view. Main thread only.</summary>
internal sealed class CoopController
{
    private const float SendInterval = 1f / 20f;
    private const float RemoteTimeout = 2f;

    private float _nextSend;
    private float _lastRemoteAt = -1;
    private PlayerState _remote;
    private RemotePlayerView _view;

    public CoopSession Session { get; } = new();
    public DiagnosticMarkerHandler Markers { get; private set; } = new();
    /// <summary>Reason the last client connection attempt failed, or null.</summary>
    public string LastFailure { get; private set; }
    public string PeerLabel => Session.Role == Role.Host ? "Client" : "Host";

    public CoopController()
    {
        Session.Log += (string m) => Plugin.Logger.LogInfo("[net] " + m);
        Session.PeerJoined += () => LastFailure = null;
        Session.PeerDisconnected += OnPeerDisconnected;
        Session.RemoteState += (PlayerState s) => { _remote = s; _lastRemoteAt = Time.realtimeSinceStartup; };
        Session.WorldCreated += OnWorldCreated;
    }

    public bool Host(int port, out string error)
    {
        Stop("Starting a new host session.");
        LastFailure = null;
        return Session.Host(port, GameInfo.Build(), out error);
    }

    public bool Join(string address, int port, out string error)
    {
        Stop("Starting a new join attempt.");
        LastFailure = null;
        return Session.Join(address, port, GameInfo.Build(), out error);
    }

    public void Stop(string why)
    {
        if (Session.IsRunning) Plugin.Logger.LogInfo($"Stopping session: {why}");
        Session.Stop();
        DestroyView();
        _lastRemoteAt = -1;
    }

    /// <summary>
    /// The host's world changed (it entered a workshop scene). Starts a new world generation so that requests and
    /// changes made for the previous world are refused. Phase 2 replaces this with the shared load/readiness flow.
    /// </summary>
    public void OnHostWorldEntered(string scene)
    {
        if (Session.Role != Role.Host || Session.World == null) return;
        Session.World.BeginGeneration("host entered " + scene);
    }

    /// <summary>Call every frame.</summary>
    public void Update(bool inWorkshop)
    {
        Session.Poll();
        if (!Session.IsJoined) { DestroyView(); return; }

        var local = inWorkshop ? LocalPlayer.Find() : null;
        if (local != null && Time.realtimeSinceStartup >= _nextSend)
        {
            _nextSend = Time.realtimeSinceStartup + SendInterval;
            Session.SendState(local.Value);
        }

        var fresh = _lastRemoteAt >= 0 && Time.realtimeSinceStartup - _lastRemoteAt < RemoteTimeout;
        if (inWorkshop && fresh && _remote.InWorkshop)
        {
            _view ??= RemotePlayerView.Create(PeerLabel);
            _view.SetTarget(_remote);
            _view.Update();
        }
        else DestroyView();
    }

    /// <summary>The other player's capsule is currently shown.</summary>
    public bool HasRemoteView => _view != null;

    /// <summary>Where the remote capsule is relative to the local camera, for test logs.</summary>
    public string RemoteViewReport
    {
        get
        {
            var cam = Camera.main;
            if (_view == null || cam == null) return "remote view: none";
            var pos = _view.Position;
            var to = pos - cam.transform.position;
            return $"remote view at {pos}, {to.magnitude:F1} m from camera, {Vector3.Angle(cam.transform.forward, to):F0}° off view centre";
        }
    }

    /// <summary>Short status suffix for problems the player should know about, or "".</summary>
    public string ProblemSummary
    {
        get
        {
            if (Session.World?.Divergence != null) return " · OUT OF SYNC (see log)";
            var w = Session.Warnings;
            if (!Session.IsJoined || w.Count == 0) return "";
            return " · Warning: " + w[0] + (w.Count > 1 ? $" (+{w.Count - 1} more in log)" : "");
        }
    }

    private void OnWorldCreated(WorldSync world)
    {
        Markers = new DiagnosticMarkerHandler();
        world.Register(CommandTypes.DiagnosticMarker, Markers);
        world.Rejected += (OperationId op, RejectReason reason, string text) => Plugin.Logger.LogWarning($"[world] {op} refused: {reason} {text}");
        world.Diverged += (string why) => Plugin.Logger.LogError($"[world] OUT OF SYNC: {why}");
        world.GenerationStarted += (uint gen, string why) => Plugin.Logger.LogInfo($"[world] generation {gen}: {why}");
    }

    private void OnPeerDisconnected(string reason)
    {
        DestroyView();
        if (Session.Role == Role.Client)
        {
            LastFailure = reason;
            Session.Stop();
        }
        // A host keeps listening so the client can reconnect.
    }

    private void DestroyView()
    {
        _view?.Destroy();
        _view = null;
    }
}

/// <summary>Reads the local workshop player: root "Player" (CharacterController) with a "Main Camera" child.</summary>
internal static class LocalPlayer
{
    private static CharacterController _cc;
    private static Transform _camera;

    public static PlayerState? Find()
    {
        if (_cc == null)
        {
            var go = GameObject.Find("Player");
            _cc = go != null ? go.GetComponent<CharacterController>() : null;
            _camera = _cc != null ? _cc.transform.Find("Main Camera") : null;
            if (_cc == null) return null;
        }
        var t = _cc.transform;
        var feet = t.TransformPoint(_cc.center) - Vector3.up * (_cc.height * 0.5f * t.lossyScale.y);
        var yaw = (_camera != null ? _camera : t).eulerAngles.y;
        return new PlayerState(feet.x, feet.y, feet.z, yaw, true);
    }

    public static Transform Root => _cc != null ? _cc.transform : null;
    public static CharacterController Controller => _cc;
}

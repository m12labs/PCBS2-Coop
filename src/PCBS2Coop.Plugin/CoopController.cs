using UnityEngine;
using PCBS2Coop.Network;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// Owns the network session, the shared world lifecycle and the remote player view. Main thread only.
/// <list type="bullet">
/// <item>Host: loads the co-op world (New world / Continue), starts a world generation once it is ready, sends a
/// snapshot to a guest that needs the world, and sends a copy after each of its saves.</item>
/// <item>Guest: connects on the main menu, receives the snapshot, loads it from its session folder and enters the
/// world once its load has settled. Leaving the host's world in any way returns it to the main menu.</item>
/// </list>
/// </summary>
internal sealed class CoopController
{
    private const float SendInterval = 1f / 20f;
    private const float RemoteTimeout = 2f;
    private const float GuestLoadTimeout = 180f;
    private const float SnapshotWriteTimeout = 60f;
    public const string SnapshotFileName = "coopworld.binary";

    private float _nextSend;
    private float _lastRemoteAt = -1;
    private PlayerState _remote;
    private RemotePlayerView _view;
    private readonly WorldReadiness _readiness = new();

    // Host
    private bool _hostWorldStarted;
    private string _hostWorldKind;
    private (uint Id, SnapshotPurpose Purpose, string Path, float Deadline)? _pendingFile;
    private readonly Queue<string> _copiesToSend = new();

    // Guest
    private bool _guestLoading;
    private float _guestLoadDeadline;
    private bool _leavingToMenu;
    private bool _runInBackgroundWas;
    private bool _runInBackgroundSet;

    public CoopSession Session { get; } = new();
    public DiagnosticMarkerHandler Markers { get; private set; } = new();
    /// <summary>Reason the last connection or world entry failed, or null. Shown on the main menu.</summary>
    public string LastFailure { get; private set; }
    /// <summary>A reason to show once the main menu is back (the guest left a world). Read with <see cref="TakeMenuMessage"/>.</summary>
    private string _menuMessage;
    public string TakeMenuMessage() { var m = _menuMessage; _menuMessage = null; return m; }
    public string PeerLabel => Session.Role == Role.Host ? "Client" : "Host";
    /// <summary>The local world finished loading (both roles).</summary>
    public bool WorldReady => _readiness.Ready;
    public string WorldWaiting => _readiness.Waiting;
    /// <summary>Guest: a world load from the host's snapshot has been started.</summary>
    public bool GuestLoadStarted { get; private set; }
    /// <summary>Guest: copies of the host's world received after host saves (for tests and logs).</summary>
    public int CopiesReceived { get; private set; }
    /// <summary>Host: a snapshot save is in progress; the save hook ignores it.</summary>
    internal static bool TakingSnapshot { get; private set; }
    internal static CoopController Instance { get; private set; }

    public CoopController()
    {
        Instance = this;
        Session.Log += (string m) => Plugin.Logger.LogInfo("[net] " + m);
        Session.PeerJoined += () => LastFailure = null;
        Session.PeerDisconnected += OnPeerDisconnected;
        Session.RemoteState += (PlayerState s) => { _remote = s; _lastRemoteAt = Time.realtimeSinceStartup; };
        Session.WorldCreated += OnWorldCreated;
        Session.SnapshotReceived += OnSnapshotReceived;
    }

    // ---------------- starting and stopping ----------------

    /// <summary>Host: start listening and load the co-op world (new or continued). Call on the main menu.</summary>
    public bool HostWorld(int port, bool continueWorld, out string error)
    {
        Stop(null, "Starting a new host session.");
        LastFailure = null;
        var menu = Object.FindObjectOfType<NewMainMenu>();
        if (menu == null) { error = "The main menu isn't ready."; return false; }
        if (!Session.Host(port, GameInfo.Build(), out error)) return false;
        BeginBackgroundRun();
        CoopSaves.EnterHost();
        _hostWorldKind = continueWorld ? "continued co-op world" : "new co-op world";

        string name;
        if (continueWorld)
        {
            var files = new[] { SaveLoadSystem.s_freeBuildSaveName, SaveLoadSystem.s_autoFreeBuildSaveName };
            name = CoopSaves.FindSaveName(f => files.Any(n => f.Equals(n, StringComparison.OrdinalIgnoreCase) || f.Equals(n + ".zip", StringComparison.OrdinalIgnoreCase)));
            if (name == null) { Fail("There is no co-op world to continue."); error = LastFailure; return false; }
        }
        else
        {
            name = CoopSaves.FindSaveName(f => f.StartsWith("defaultFreebuild.binary", StringComparison.OrdinalIgnoreCase));
            if (name == null)
            {
                Plugin.Logger.LogWarning("[world] the game's default build isn't in the co-op folder; starting through PlayFree.");
                menu.PlayFree();
                return true;
            }
        }
        Plugin.Logger.LogInfo($"[world] host loads the {_hostWorldKind} from '{CoopSaves.CoopDir}' ('{name}').");
        menu.ContinueFreeBuild(name);
        return true;

        void Fail(string why)
        {
            LastFailure = why;
            Stop(null, why);
            CoopSaves.Restore(why);
        }
    }

    public bool Join(string address, int port, out string error)
    {
        Stop(null, "Starting a new join attempt.");
        LastFailure = null;
        if (!Session.Join(address, port, GameInfo.Build(), out error)) return false;
        BeginBackgroundRun();
        return true;
    }

    /// <summary>End the session. <paramref name="toPeer"/> is shown to the other player (null = generic).</summary>
    public void Stop(string toPeer, string why)
    {
        if (Session.IsRunning) Plugin.Logger.LogInfo($"Stopping session: {why}");
        Session.Stop(toPeer);
        DestroyView();
        _lastRemoteAt = -1;
        _hostWorldStarted = false;
        _pendingFile = null;
        _copiesToSend.Clear();
        _guestLoading = false;
        GuestLoadStarted = false;
        EndBackgroundRun();
    }

    /// <summary>The main menu is showing: end any session and give the game its solo save folder back.</summary>
    public void OnMainMenu()
    {
        if (Session.IsRunning)
            Stop(Session.Role == Role.Host ? "The host ended the session." : "The other player left.", "Returned to the main menu.");
        _leavingToMenu = false;
        CoopSaves.Restore("main menu");
        _readiness.Reset();
    }

    public void OnQuit() =>
        Stop(Session.Role == Role.Host ? "The host closed the game." : "The other player closed the game.", "Game closing.");

    // ---------------- per frame ----------------

    public void Update(bool inWorkshop)
    {
        Session.Poll();
        _readiness.Update(inWorkshop);

        // A guest whose session ended while its load was starting must not stay in a world nobody shares.
        if (!Session.IsRunning && CoopSaves.Current == CoopSaves.Mode.Guest && inWorkshop && !_leavingToMenu)
            LeaveToMenu(LastFailure ?? "The co-op session ended.", failAdmission: false);

        if (Session.Role == Role.Host) UpdateHost();
        else if (Session.Role == Role.Client) UpdateGuest(inWorkshop);

        UpdateRemoteView(inWorkshop);
    }

    private void UpdateHost()
    {
        if (!_hostWorldStarted && _readiness.Ready && CoopSaves.Current == CoopSaves.Mode.Host)
        {
            _hostWorldStarted = true;
            Session.World!.BeginGeneration(_hostWorldKind ?? "co-op world");
        }

        if (_pendingFile is { } p) PollSnapshotFile(p);
        else if (Session.GuestNeedsWorld && _hostWorldStarted && _readiness.Ready && !IsCarrying()) TakeSnapshot();
        else if (_copiesToSend.Count > 0 && Session.GuestState == GuestState.InWorld && !SaveLoadSystem.WaitingOnThread()) SendCopy(_copiesToSend.Dequeue());
    }

    private void UpdateGuest(bool inWorkshop)
    {
        if (!_guestLoading) return;
        var now = Time.realtimeSinceStartup;
        if (inWorkshop && _readiness.Ready)
        {
            _guestLoading = false;
            if (Session.CompleteAdmission(out var error)) Plugin.Logger.LogInfo("[world] entered the host's world.");
            else LeaveToMenu("Could not enter the host's world: " + error, failAdmission: true);
        }
        else if (now > _guestLoadDeadline)
        {
            _guestLoading = false;
            LeaveToMenu($"Loading the host's world took too long (still waiting: {_readiness.Waiting}).", failAdmission: true);
        }
    }

    // ---------------- host: snapshots and copies ----------------

    private void TakeSnapshot()
    {
        var saves = Object.FindObjectOfType<SaveLoadSystem>();
        var level = Object.FindObjectOfType<LevelLoadPersistency>();
        if (saves == null) return;
        CoopSaves.Recreate(CoopSaves.TransferDir);
        var saveName = level?.SaveName;
        var opened = saves.GetOpenedSaveName();
        string path;
        TakingSnapshot = true;
        SaveLoadSystem.s_saveDir = CoopSaves.TransferDir;
        try { path = saves.CreateSaveGame(SnapshotFileName, false, ""); }
        finally
        {
            SaveLoadSystem.s_saveDir = CoopSaves.CoopDir;
            TakingSnapshot = false;
        }
        // Same frame as the save: the snapshot's place in the change stream matches what the save captured.
        var id = Session.BeginSnapshot(SnapshotPurpose.Load);
        // CreateSaveGame makes the snapshot the game's "current save"; put the real one back.
        if (level != null) level.SaveName = saveName;
        saves.SetOpenedSaveName(opened);
        _pendingFile = (id, SnapshotPurpose.Load, path, Time.realtimeSinceStartup + SnapshotWriteTimeout);
        Plugin.Logger.LogInfo($"[world] snapshot {id} saving to '{path}' (save name kept as '{saveName}').");
    }

    private void PollSnapshotFile((uint Id, SnapshotPurpose Purpose, string Path, float Deadline) p)
    {
        if (SaveLoadSystem.WaitingOnThread())
        {
            if (Time.realtimeSinceStartup > p.Deadline) { Session.AbortSnapshot(p.Id, "the save took too long"); _pendingFile = null; }
            return;
        }
        _pendingFile = null;
        var file = File.Exists(p.Path + ".zip") ? p.Path + ".zip" : p.Path;
        try
        {
            var bytes = File.ReadAllBytes(file);
            Session.SetSnapshotFile(p.Id, Path.GetFileName(file), bytes);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[world] snapshot {p.Id}: could not read '{file}': {e.Message}");
            Session.AbortSnapshot(p.Id, "the host could not read its save");
        }
    }

    /// <summary>Called by the save hook after any game save while hosting (not the snapshot saves).</summary>
    internal void OnHostSaved(string path)
    {
        if (Session.Role != Role.Host || string.IsNullOrEmpty(path)) return;
        Plugin.Logger.LogInfo($"[world] host saved '{path}'.");
        if (Session.GuestState == GuestState.InWorld) _copiesToSend.Enqueue(path);
    }

    private void SendCopy(string path)
    {
        var file = File.Exists(path + ".zip") ? path + ".zip" : path;
        var name = Path.GetFileName(file);
        if (!Protocol.IsValidSnapshotName(name) || !File.Exists(file)) { Plugin.Logger.LogWarning($"[world] not sending copy '{file}'."); return; }
        var id = Session.BeginSnapshot(SnapshotPurpose.Copy);
        Session.SetSnapshotFile(id, name, File.ReadAllBytes(file));
    }

    private static bool IsCarrying()
    {
        var wc = Object.FindObjectOfType<WorkshopController>();
        return wc != null && wc.HasOrLoadingCarriedCase();
    }

    // ---------------- guest: receiving and loading ----------------

    private void OnSnapshotReceived(Snapshot s)
    {
        try
        {
            if (s.Purpose == SnapshotPurpose.Copy)
            {
                CoopSaves.StoreJoinedCopy(s.FileName, s.File);
                CopiesReceived++;
                return;
            }
            var menu = Object.FindObjectOfType<NewMainMenu>();
            if (CoopRunner.Instance?.Scene != CoopRunner.MenuScene || menu == null)
            {
                LeaveToMenu("The host's world arrived while this game wasn't on the main menu.", failAdmission: true);
                return;
            }
            CoopSaves.EnterGuest();
            CoopSaves.WriteGuestWorld(s.FileName, s.File);
            CoopSaves.StoreJoinedCopy(s.FileName, s.File);
            var name = CoopSaves.FindSaveName(f => f.Equals(s.FileName, StringComparison.OrdinalIgnoreCase));
            if (name == null) { LeaveToMenu("The game could not read the host's world file.", failAdmission: true); return; }
            Plugin.Logger.LogInfo($"[world] loading the host's world '{name}' from '{CoopSaves.GuestSessionDir}'.");
            _guestLoading = true;
            GuestLoadStarted = true;
            _guestLoadDeadline = Time.realtimeSinceStartup + GuestLoadTimeout;
            _readiness.Reset();
            menu.ContinueFreeBuild(name);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e);
            LeaveToMenu("Could not load the host's world: " + e.Message, failAdmission: true);
        }
    }

    /// <summary>Guest: leave the host's world and show <paramref name="reason"/> on the main menu.</summary>
    private void LeaveToMenu(string reason, bool failAdmission)
    {
        Plugin.Logger.LogWarning("[world] leaving: " + reason);
        LastFailure = reason;
        _menuMessage = reason;
        if (failAdmission && Session.IsRunning) Session.FailAdmission(reason);
        Stop(null, reason);
        if (CoopRunner.Instance?.Scene == CoopRunner.MenuScene || _leavingToMenu) return;
        _leavingToMenu = true;
        var loader = Object.FindObjectOfType<NewLoadingScreen>();
        if (loader != null) loader.LoadScene(CoopRunner.MenuScene, false);
        else Plugin.Logger.LogError("[world] no loading screen found to return to the main menu.");
    }

    private void OnPeerDisconnected(string reason)
    {
        DestroyView();
        if (Session.Role == Role.Client)
        {
            var inWorld = CoopRunner.Instance?.Scene == CoopRunner.WorkshopScene || _guestLoading;
            if (inWorld) LeaveToMenu(reason, failAdmission: false);
            else { LastFailure = reason; Stop(null, reason); }
        }
        else Plugin.Logger.LogInfo($"[world] the guest left: {reason}. Still hosting.");
    }

    // ---------------- world setup ----------------

    private void OnWorldCreated(WorldSync world)
    {
        Markers = new DiagnosticMarkerHandler();
        world.Register(CommandTypes.DiagnosticMarker, Markers);
        world.RegisterState(Markers);
        world.Rejected += (OperationId op, RejectReason reason, string text) => Plugin.Logger.LogWarning($"[world] {op} refused: {reason} {text}");
        world.Diverged += (string why) => Plugin.Logger.LogError($"[world] OUT OF SYNC: {why}");
        world.GenerationStarted += (uint gen, string why) => Plugin.Logger.LogInfo($"[world] generation {gen}: {why}");
        world.WorldReplaced += (string why) => LeaveToMenu("The host loaded a different world.", failAdmission: false);
    }

    /// <summary>The game already runs in the background (runInBackground is on in this build); keep it so during a session.</summary>
    private void BeginBackgroundRun()
    {
        if (_runInBackgroundSet) return;
        _runInBackgroundWas = Application.runInBackground;
        _runInBackgroundSet = true;
        Application.runInBackground = true;
    }

    private void EndBackgroundRun()
    {
        if (!_runInBackgroundSet) return;
        Application.runInBackground = _runInBackgroundWas;
        _runInBackgroundSet = false;
    }

    // ---------------- remote player ----------------

    private void UpdateRemoteView(bool inWorkshop)
    {
        var inWorld = Session.IsJoined && Session.World is { Admitted: true } &&
                      (Session.Role == Role.Client || Session.GuestState == GuestState.InWorld);
        if (!inWorld) { DestroyView(); return; }

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
    public Vector3? RemoteViewPosition => _view?.Position;

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

    /// <summary>One-line session status for the workshop overlay.</summary>
    public string StatusLine
    {
        get
        {
            var s = Session;
            if (s.Role == Role.Host)
            {
                var head = $"Co-op host · port {s.Port}";
                if (!_hostWorldStarted) return head + " · loading world";
                if (!s.IsJoined) return head + " · waiting for Client";
                return head + (s.GuestState switch
                {
                    GuestState.InWorld => $" · Client in world ({s.PingMs} ms)",
                    GuestState.Loading => " · Client loading the world",
                    _ => s.SendingSnapshot ? " · sending world to Client" : " · Client connecting",
                });
            }
            if (s.Role == Role.Client)
                return s.GuestState == GuestState.InWorld ? $"Co-op client · in Host's world ({s.PingMs} ms)" : "Co-op client · loading Host's world";
            return "";
        }
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

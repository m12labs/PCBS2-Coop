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
    private readonly Queue<(string Path, SnapshotPosition Position, IReadOnlyList<CarrierPayload> Carriers)> _pairWrites = new();
    private (SnapshotPosition Position, IReadOnlyList<CarrierPayload> Carriers)? _saveCapture;
    private SaveMetadata _loadingPair;
    private (SnapshotPosition Position, IReadOnlyList<CarrierPayload> Carriers)? _snapshotPair;
    private string _saveError;

    // Guest
    private bool _guestLoading;
    private float _guestLoadDeadline;
    private bool _leavingToMenu;
    private bool _runInBackgroundWas;
    private bool _runInBackgroundSet;

    public CoopSession Session { get; } = new();
    public DiagnosticMarkerHandler Markers { get; private set; } = new();
    public NativeObjectState Objects { get; private set; } = new();
    public ActivityWorld Activity { get; private set; }
    private NativeActivity _nativeActivity;
    public NativePcLifecycle Lifecycle { get; private set; }
    public NativePcTransfers Transfers { get; private set; }
    private string _notice;
    private float _noticeUntil;
    public string ActiveNotice => _notice != null && Time.realtimeSinceStartup < _noticeUntil ? _notice : null;
    /// <summary>Reason the last connection or world entry failed, or null. Shown on the main menu.</summary>
    public string LastFailure { get; private set; }
    /// <summary>A reason to show once the main menu is back (the guest left a world). Read with <see cref="TakeMenuMessage"/>.</summary>
    private string _menuMessage;
    public string TakeMenuMessage() { var m = _menuMessage; _menuMessage = null; return m; }
    /// <summary>Show a short message on the workshop status line (for example, why the host refused a request).</summary>
    public void Notice(string text)
    {
        _notice = text; _noticeUntil = Time.realtimeSinceStartup + 6f;
        Plugin.Logger.LogWarning("[notice] " + text);
    }
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
        if (SteamBootstrap.Service != null)
        {
            SteamBootstrap.Service.InviteRequested += OnSteamInvite;
            SteamBootstrap.Service.Failed += OnSteamFailure;
        }
    }

    // ---------------- starting and stopping ----------------

    /// <summary>Host: start listening and load the co-op world (new or continued). Call on the main menu.</summary>
    public bool HostWorld(int port, bool continueWorld, out string error)
        => HostWorld(port, continueWorld, null, out error);

    private bool HostWorld(int port, bool continueWorld, ISessionTransport transport, out string error)
    {
        if (transport == null) Stop(null, "Starting a new host session.");
        LastFailure = null;
        if (!GamePatches.Ready) { error = "The co-op save/object hooks could not be installed. See the log."; return false; }
        var menu = Object.FindObjectOfType<NewMainMenu>();
        if (menu == null) { error = "The main menu isn't ready."; return false; }
        var local = GameInfo.Build();
        if (!(transport == null ? Session.Host(port, local, out error) : Session.Host(transport, local, out error))) return false;
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
        try
        {
            if (continueWorld) _loadingPair = CoopSavePair.ReadSidecar(CoopSaves.NativeFile(CoopSaves.CoopDir, name));
            LoadNamedFreeBuild(name); return true;
        }
        catch (Exception e)
        {
            Fail("Could not load the co-op world: " + e.Message);
            error = LastFailure;
            return false;
        }

        void Fail(string why)
        {
            LastFailure = why;
            Stop(null, why);
            CoopSaves.Restore(why);
        }
    }

    /// <summary>
    /// ContinueFreeBuild's string is a default-save switch, not a filename: a nonempty value prefers the shipped
    /// default save. Use the same lower-level entry point with our exact save instead, for host and guest alike.
    /// </summary>
    private static void LoadNamedFreeBuild(string name)
    {
        var level = Object.FindObjectOfType<LevelLoadPersistency>();
        if (level == null) throw new InvalidOperationException("The game's loading system isn't ready.");
        NewMainMenu.RefreshControllerInput();
        level.gameModeToLoad = GameMode.FREEBUILD;
        level.LoadGameFromDir(name, CoopRunner.WorkshopScene, false);
    }

    public bool Join(string address, int port, out string error)
    {
        Stop(null, "Starting a new join attempt.");
        LastFailure = null;
        if (!GamePatches.Ready) { error = "The co-op save/object hooks could not be installed. See the log."; return false; }
        if (!Session.Join(address, port, GameInfo.Build(), out error)) return false;
        BeginBackgroundRun();
        return true;
    }

    public bool SteamStarting => SteamBootstrap.Service?.Busy == true;
    public bool IsSteamHost => Session.Role == Role.Host && Session.TransportName == "Steam";

    public bool HostSteam(bool continueWorld, out string error)
    {
        Stop(null, "Preparing a Steam host session.");
        LastFailure = null;
        var steam = SteamBootstrap.Service;
        if (steam?.Available != true) { error = steam?.UnavailableReason ?? SteamBootstrap.Reason; return false; }
        if (!GamePatches.Ready || Object.FindObjectOfType<NewMainMenu>() == null)
        { error = "The main menu or co-op hooks are not ready."; return false; }
        steam.BeginHost(GameInfo.Build(), (ISessionTransport transport, string failure) =>
        {
            if (failure != null) { LastFailure = failure; return; }
            try
            {
                if (!IdleMainMenu) { transport?.Dispose(); steam.Cancel(); LastFailure = "Return to the main menu before hosting."; return; }
                if (!HostWorld(0, continueWorld, transport, out var why)) { LastFailure = why; steam.Cancel(); }
            }
            catch (Exception e) { LastFailure = "Could not start the Steam world: " + e.Message; Stop(null, LastFailure); CoopSaves.Restore(LastFailure); }
        });
        error = LastFailure;
        return error == null;
    }

    private bool IdleMainMenu => CoopRunner.Instance?.Scene == CoopRunner.MenuScene && !Session.IsRunning && !_guestLoading && !_leavingToMenu;
    private void OnSteamInvite(ulong lobby)
    {
        var steam = SteamBootstrap.Service;
        if (!IdleMainMenu || steam.Busy || steam.HasLobby)
        { Notice("Return to the main menu, then accept the Steam invite again."); return; }
        if (!GamePatches.Ready) { ShowSteamMessage("The co-op hooks are not ready. See the log."); return; }
        LastFailure = null;
        // Open first: opening an ordinary co-op dialog cancels previous attempts.
        CoopRunner.Instance.Menu?.Dialog.OpenSteamWaiting();
        steam.BeginJoin(lobby, GameInfo.Build(), (ISessionTransport transport, string failure) =>
        {
            if (failure != null) { LastFailure = failure; return; }
            try
            {
                if (!IdleMainMenu) { transport?.Dispose(); steam.Cancel(); LastFailure = "Return to the main menu and accept again."; return; }
                if (!Session.Join(transport, GameInfo.Build(), out var error)) { LastFailure = error; steam.Cancel(); return; }
                BeginBackgroundRun();
            }
            catch (Exception e) { LastFailure = "Could not join Steam session: " + e.Message; Stop(null, LastFailure); }
        });
    }
    private void ShowSteamMessage(string reason)
    {
        LastFailure = reason;
        if (CoopRunner.Instance?.Menu != null) CoopRunner.Instance.Menu.Dialog.OpenMessage(reason);
        else _menuMessage = reason;
    }
    private void OnSteamFailure(string reason)
    {
        if (Session.TransportName == "Steam" && Session.IsRunning) LeaveToMenu(reason, failAdmission: false);
        else if (IdleMainMenu) ShowSteamMessage(reason);
    }

    /// <summary>End the session. <paramref name="toPeer"/> is shown to the other player (null = generic).</summary>
    public void Stop(string toPeer, string why)
    {
        // Publish a completed pair before dropping pending state on a normal menu/quit path.
        if (_pairWrites.Count > 0 && !SaveLoadSystem.WaitingOnThread()) FinishPairWrites();
        if (Session.IsRunning) Plugin.Logger.LogInfo($"Stopping session: {why}");
        Session.Stop(toPeer);
        SteamBootstrap.Service?.Cancel();
        Objects.Reset();
        Activity?.Reset(); _nativeActivity = null; Lifecycle = null; Transfers = null; _notice = null; NativeProjection.Reset();
        DestroyView();
        _lastRemoteAt = -1;
        _hostWorldStarted = false;
        _pendingFile = null;
        _copiesToSend.Clear();
        _pairWrites.Clear(); _saveCapture = null; _loadingPair = null; _snapshotPair = null; _saveError = null;
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
        Objects.CleanupCompletedLoads();
        _readiness.Update(inWorkshop);

        // A guest whose session ended while its load was starting must not stay in a world nobody shares.
        if (!Session.IsRunning && CoopSaves.Current == CoopSaves.Mode.Guest && inWorkshop && !_leavingToMenu)
            LeaveToMenu(LastFailure ?? "The co-op session ended.", failAdmission: false);

        if (Session.Role == Role.Host) UpdateHost();
        else if (Session.Role == Role.Client) UpdateGuest(inWorkshop);
        SteamBootstrap.Service?.SetHostReady(IsSteamHost && _hostWorldStarted && WorldReady && Session.World is { Admitted: true });

        if (inWorkshop) { _nativeActivity?.Update(); Lifecycle?.Update(); Transfers?.Update(); NativeProjection.Update(); }

        UpdateRemoteView(inWorkshop);
    }

    private void UpdateHost()
    {
        if (!_hostWorldStarted && _readiness.Ready && CoopSaves.Current == CoopSaves.Mode.Host)
        {
            try
            {
                Session.World!.BeginGeneration(_hostWorldKind ?? "co-op world");
                if (_loadingPair != null)
                {
                    Session.World.RestoreState(_loadingPair.Position.State);
                    Objects.FinishHostRestore(Session.LocalIdentity.Player);
                    _loadingPair = null;
                }
                Objects.CaptureHost(Session.LocalIdentity.Player);
                // Saved work/reservations belong to the previous session. Preserve committed objects only.
                Session.World.Submit(CommandTypes.ActorActivity, ActivityWorld.Presence(ActorSlot.Guest, default), out _);
                Session.World.Submit(CommandTypes.ActorActivity, ActivityWorld.Presence(ActorSlot.Host, Session.LocalIdentity.Player), out _);
                _hostWorldStarted = true;
                Plugin.Logger.LogInfo($"[objects] host bound {Objects.Model.Pcs.Count} PCs, next ID {Objects.Model.NextId}, world {Objects.Model.WorldId}.");
            }
            catch (Exception e) { Plugin.Logger.LogError(e); LeaveToMenu("Could not bind co-op object identities: " + e.Message, false); return; }
        }

        if (_hostWorldStarted && Session.IsJoined && Activity.Get(ActorSlot.Guest)?.Player != Session.RemoteIdentity.Player)
            Session.World.Submit(CommandTypes.ActorActivity, ActivityWorld.Presence(ActorSlot.Guest, Session.RemoteIdentity.Player), out _);
        if (_pairWrites.Count > 0 && !SaveLoadSystem.WaitingOnThread()) FinishPairWrites();
        if (_pendingFile is { } p) PollSnapshotFile(p);
        else if (Session.GuestNeedsWorld && _hostWorldStarted && _readiness.Ready) TakeSnapshot();
        else if (_copiesToSend.Count > 0 && Session.GuestState == GuestState.InWorld && !SaveLoadSystem.WaitingOnThread()) SendCopy(_copiesToSend.Dequeue());
    }

    private void UpdateGuest(bool inWorkshop)
    {
        if (!_guestLoading) return;
        var now = Time.realtimeSinceStartup;
        if (inWorkshop && _readiness.Ready)
        {
            _guestLoading = false;
            if (Session.CompleteAdmission(out var error))
            {
                Objects.FinishGuestRestore(); _loadingPair = null;
                Plugin.Logger.LogInfo($"[world] entered the host's world with {Objects.Model.Pcs.Count} bound PCs.");
            }
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
        try
        {
            Lifecycle?.ExtendHost(); Lifecycle?.ObserveHost(force: true);
            Objects.CaptureHost(Session.LocalIdentity.Player);
            _saveCapture = (Session.World.CapturePosition(), Objects.CaptureCarriers());
        }
        catch (Exception e) { LeaveToMenu("Could not capture co-op identities: " + e.Message, false); return; }
        CoopSaves.Recreate(CoopSaves.TransferDir);
        var saveName = level?.SaveName;
        var opened = saves.GetOpenedSaveName();
        string path;
        TakingSnapshot = true;
        SaveLoadSystem.s_saveDir = CoopSaves.TransferDir;
        try
        {
            path = saves.CreateSaveGame(SnapshotFileName, false, "");
            if (string.IsNullOrEmpty(path)) throw new InvalidDataException("The native snapshot save did not start.");
        }
        catch (Exception e) { LeaveToMenu("Could not save the co-op snapshot: " + e.Message, false); return; }
        finally
        {
            SaveLoadSystem.s_saveDir = CoopSaves.CoopDir;
            TakingSnapshot = false;
            if (level != null) level.SaveName = saveName;
            saves.SetOpenedSaveName(opened);
        }
        // Same frame as the save: the snapshot's place in the change stream matches what the save captured.
        var id = Session.BeginSnapshot(SnapshotPurpose.Load);
        _snapshotPair = _saveCapture;
        _saveCapture = null;
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
            if (Time.realtimeSinceStartup > p.Deadline) { Session.AbortSnapshot(p.Id, "the save took too long"); _pendingFile = null; _snapshotPair = null; }
            return;
        }
        _pendingFile = null;
        var file = File.Exists(p.Path + ".zip") ? p.Path + ".zip" : p.Path;
        try
        {
            var bytes = File.ReadAllBytes(file);
            var pair = CoopSavePair.Create(bytes, _snapshotPair.Value.Position, _snapshotPair.Value.Carriers);
            CoopSavePair.WriteSidecar(file, bytes, pair);
            Session.SetSnapshotFile(p.Id, Path.GetFileName(file), CoopSavePair.Pack(bytes, pair));
            _snapshotPair = null;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[world] snapshot {p.Id}: could not read '{file}': {e.Message}");
            Session.AbortSnapshot(p.Id, "the host could not read its save");
            _snapshotPair = null;
        }
    }

    /// <summary>Called by the save hook after any game save while hosting (not the snapshot saves).</summary>
    internal void OnHostSaved(string path)
    {
        if (Session.Role != Role.Host || string.IsNullOrEmpty(path) || _saveCapture == null) return;
        Plugin.Logger.LogInfo($"[world] host saved '{path}'.");
        _pairWrites.Enqueue((path, _saveCapture.Value.Position, _saveCapture.Value.Carriers));
        _saveCapture = null;
    }

    internal bool BeforeHostSave()
    {
        if (TakingSnapshot || Session.Role != Role.Host || !Objects.Initialized) return true;
        try
        {
            if (SaveLoadSystem.WaitingOnThread() || _pairWrites.Count > 0 || _pendingFile != null)
                throw new InvalidOperationException("The previous co-op save is still being paired.");
            var s = Object.FindObjectOfType<SaveLoadSystem>();
            var wc = Object.FindObjectOfType<WorkshopController>();
            var db = PartsDatabase.s_instance;
            if (s == null || s.SpawningPC || wc == null || wc.IsCaseLoading ||
                (db?.m_loadPCOperations?.Count ?? 0) + (db?.m_loadPartOperations?.Count ?? 0) + (db?.m_loadPlugOperations?.Count ?? 0) +
                (db?.m_loadCableOperations?.Count ?? 0) + (db?.m_loadPrefabOperations?.Count ?? 0) > 0)
                throw new InvalidOperationException("Objects are still loading; wait before saving.");
            Lifecycle?.ExtendHost(); Lifecycle?.ObserveHost(force: true);
            Objects.CaptureHost(Session.LocalIdentity.Player);
            _saveCapture = (Session.World.CapturePosition(), Objects.CaptureCarriers());
            return true;
        }
        catch (Exception e)
        {
            _saveCapture = null; _saveError = LastFailure = "Co-op save was not written: " + e.Message;
            Plugin.Logger.LogError("[saves] " + LastFailure); return false;
        }
    }

    private void FinishPairWrites()
    {
        while (_pairWrites.Count > 0)
        {
            var p = _pairWrites.Dequeue();
            try
            {
                var file = File.Exists(p.Path + ".zip") ? p.Path + ".zip" : p.Path;
                var bytes = File.ReadAllBytes(file);
                CoopSavePair.WriteSidecar(file, bytes, CoopSavePair.Create(bytes, p.Position, p.Carriers));
                _saveError = null;
                Plugin.Logger.LogInfo($"[saves] paired identities with '{file}'.");
                if (Session.GuestState == GuestState.InWorld) _copiesToSend.Enqueue(file);
            }
            catch (Exception e) { _saveError = LastFailure = "The co-op save is incomplete: " + e.Message; Plugin.Logger.LogError("[saves] " + LastFailure); }
        }
    }

    internal bool PrepareNativeLoad(SaveGame save)
    {
        if (_loadingPair == null) return true;
        try { Objects.PrepareLoad(_loadingPair, save, Session.Role == Role.Host, Session.LocalIdentity.Player); return true; }
        catch (Exception e) { LeaveToMenu("Invalid paired co-op save: " + e.Message, Session.Role == Role.Client); return false; }
    }

    private void SendCopy(string path)
    {
        var file = File.Exists(path + ".zip") ? path + ".zip" : path;
        var name = Path.GetFileName(file);
        if (!Protocol.IsValidSnapshotName(name) || !File.Exists(file)) { Plugin.Logger.LogWarning($"[world] not sending copy '{file}'."); return; }
        try
        {
            var pair = CoopSavePair.ReadSidecar(file);
            var bytes = CoopSavePair.Pack(File.ReadAllBytes(file), pair);
            var id = Session.BeginSnapshot(SnapshotPurpose.Copy);
            Session.SetSnapshotFile(id, name, bytes);
        }
        catch (Exception e) { _saveError = LastFailure = "Could not send the paired co-op save: " + e.Message; Plugin.Logger.LogError("[saves] " + LastFailure); }
    }

    // ---------------- guest: receiving and loading ----------------

    private void OnSnapshotReceived(Snapshot s)
    {
        try
        {
            var pair = CoopSavePair.Unpack(s.File);
            if (s.Purpose == SnapshotPurpose.Copy)
            {
                CoopSaves.StoreJoinedCopy(s.FileName, pair.Native, pair.Metadata);
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
            if (s.Position == null || s.Position.Generation != pair.Metadata.Position.Generation || s.Position.Revision != pair.Metadata.Position.Revision ||
                s.Position.ChainHash != pair.Metadata.Position.ChainHash || !s.Position.State.AsSpan().SequenceEqual(pair.Metadata.Position.State))
                throw new InvalidDataException("Snapshot mark and paired save state do not match.");
            CoopSaves.WriteGuestWorld(s.FileName, pair.Native, pair.Metadata);
            CoopSaves.StoreJoinedCopy(s.FileName, pair.Native, pair.Metadata);
            _loadingPair = pair.Metadata;
            var name = CoopSaves.FindSaveName(f => f.Equals(s.FileName, StringComparison.OrdinalIgnoreCase));
            if (name == null) { LeaveToMenu("The game could not read the host's world file.", failAdmission: true); return; }
            Plugin.Logger.LogInfo($"[world] loading the host's world '{name}' from '{CoopSaves.GuestSessionDir}'.");
            _guestLoading = true;
            GuestLoadStarted = true;
            _guestLoadDeadline = Time.realtimeSinceStartup + GuestLoadTimeout;
            _readiness.Reset();
            LoadNamedFreeBuild(name);
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
        else
        {
            if (Session.World is { Admitted: true })
            {
                Session.World.Submit(CommandTypes.ActorActivity, ActivityWorld.Presence(ActorSlot.Guest, default), out _);
                Transfers?.RecoverDeparted(); // a PC the guest carried goes to the shared cupboard
            }
            Plugin.Logger.LogInfo($"[world] the guest left: {reason}. Still hosting.");
        }
    }

    // ---------------- world setup ----------------

    private void OnWorldCreated(WorldSync world)
    {
        Markers = new DiagnosticMarkerHandler();
        Objects = new NativeObjectState();
        Activity = new ActivityWorld(Objects.Model);
        _nativeActivity = new NativeActivity(this, world);
        Lifecycle = new NativePcLifecycle(this, world);
        Transfers = new NativePcTransfers(this, world);
        world.Register(CommandTypes.DiagnosticMarker, Markers);
        world.RegisterState(Markers);
        world.RegisterState(Objects);
        world.Register(CommandTypes.ActorActivity, Activity);
        world.RegisterState(Activity);
        world.Register(CommandTypes.PcLifecycle, Lifecycle.Handler);
        world.Register(CommandTypes.PcTransfer, Transfers.Handler);
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
            _view.SetCarrying(RemoteCarries);
            _view.Update();
        }
        else DestroyView();
    }

    /// <summary>The accepted facts say the other player carries a PC (derived, never read from native carry fields).</summary>
    public bool RemoteCarries
    {
        get
        {
            var remote = Session.Role == Role.Host ? ActorSlot.Guest : ActorSlot.Host;
            return Objects?.Initialized == true && Objects.Model.Pcs.Any(x => x.Location.Place == PcPlace.Carrier && x.Location.Actor == remote);
        }
    }
    public bool RemoteBoxShown => _view?.CarryShown == true;
    public string RemoteBoxLook => _view?.CarryLook ?? "none";

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
            return $"remote view at {pos}, {to.magnitude:F1} m from camera, {Vector3.Angle(cam.transform.forward, to):F0}° off view centre, body {_view.BodyLook}, ground speed {_view.GroundSpeed:F2} m/s";
        }
    }

    /// <summary>Short status suffix for problems the player should know about, or "".</summary>
    public string ProblemSummary
    {
        get
        {
            if (_notice != null && Time.realtimeSinceStartup < _noticeUntil) return " · " + _notice;
            if (_saveError != null) return " · SAVE FAILED (see log)";
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
                var head = s.TransportName == "Steam" ? "Co-op host · Steam" : $"Co-op host · LAN port {s.Port}";
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

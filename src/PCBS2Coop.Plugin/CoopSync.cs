using UnityEngine;
using PCBS2Coop.Network;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// Chat lines and the players' /sync (task 3h, owner decisions October 26, 2026). When a game can't show a change the
/// host accepted, a corner marker says so; either player types /sync (a guest's request needs the host's /yes).
/// <list type="number">
/// <item>The guest leaves the world and returns to its main menu while staying connected.</item>
/// <item>The host saves the co-op world (its own game is the reference), returns to its menu and continues that exact
/// save, starting a new world generation.</item>
/// <item>The guest, back on its menu, asks for the world and is admitted from a fresh snapshot.</item>
/// </list>
/// If the host's save fails, the host stays in its world and the guest reloads the host's current world instead.
/// A PC the guest carries goes to the shared cupboard on the reload, as when a guest leaves.
/// </summary>
internal sealed partial class CoopController
{
    public const float SyncRequestSeconds = 60f;
    private const float SyncSaveTimeout = 30f, SyncPairTimeout = 60f, SyncMenuTimeout = 60f, SyncLoadTimeout = 180f, GuestSyncTimeout = 300f;
    private const int MaxChatLines = 50;

    private enum SyncStep { None, Saving, Pairing, ToMenu, Loading }

    /// <summary>One line in the chat box.</summary>
    public readonly record struct ChatEntry(string Text, float At, bool System);

    private readonly List<ChatEntry> _chat = new();
    public IReadOnlyList<ChatEntry> ChatLines => _chat;

    // Host
    private SyncStep _sync;
    private float _syncDeadline, _syncMenuAt = -1;
    private float _syncRequestUntil = -1;
    private string _syncFile;
    private bool _syncSaving;
    private bool _syncGuestLeft;
    /// <summary>The sync is a world switch from the pause menu (Load or Free Build Reset): what to load, or null.</summary>
    private WorldSwitch _switch;
    private sealed class WorldSwitch
    {
        public WorldSwitch(string saveName, string file, string stem, bool reset) { SaveName = saveName; File = file; Stem = stem; Reset = reset; }
        public string SaveName { get; }
        public string File { get; }
        public string Stem { get; }
        public bool Reset { get; }
    }
    // Guest
    private bool _guestSync;
    private float _guestSyncDeadline, _guestSyncMenuAt = -1;
    // Both
    private bool _faultAnnounced;
    /// <summary>An accepted change this game could not show (the adapters catch the native failure), or null.</summary>
    private string _projectionFault;

    /// <summary>A sync is between leaving the world and getting it back; the main menu must not end the session.</summary>
    private bool SyncKeepsSession => _sync is SyncStep.ToMenu or SyncStep.Loading || _guestSync;
    public bool SyncRunning => _sync != SyncStep.None || _guestSync;
    public string SyncState => _sync != SyncStep.None ? _sync.ToString() : _guestSync ? "GuestReloading" : "None";
    public bool SyncRequestWaiting => _syncRequestUntil > Time.realtimeSinceStartup;

    /// <summary>Why this game is out of sync with the host's world, or null.</summary>
    public string OutOfSync => !Session.IsRunning ? null : Session.World?.Divergence ?? _projectionFault ?? (Objects.Initialized ? Objects.ProjectionFault : null);

    /// <summary>An adapter could not show an accepted change on this game (H4/H5: before 0.11 this was only a notice,
    /// so the game silently disagreed with the host). Out of sync until the next generation or admission.</summary>
    internal void ProjectionFailed(string what, Exception e)
    {
        Plugin.Logger.LogError($"[world] {what}: {e}");
        what = what.Replace("→", " to "); // the game's font has no arrow
        _projectionFault ??= what;
        Notice("A change couldn't be shown on this game. Type /sync to fix it."); // details: chat line and log
    }

    /// <summary>A new generation or admission replaces this game's world: earlier projection faults no longer apply.</summary>
    private void ClearProjectionFault() { _projectionFault = null; _faultAnnounced = false; }

    private string Me => Session.Role == Role.Host ? "Host" : "Client";

    public void AddChat(string text, bool system = true)
    {
        _chat.Add(new ChatEntry(text, Time.realtimeSinceStartup, system));
        if (_chat.Count > MaxChatLines) _chat.RemoveAt(0);
        Plugin.Logger.LogInfo("[chat] " + text);
    }

    private void HookChatAndSync()
    {
        Session.ChatReceived += (ChatLine line) => AddChat(line.Kind == ChatKind.Player ? $"{PeerLabel}: {line.Text}" : $"{PeerLabel} · {line.Text}", line.Kind == ChatKind.System);
        Session.SyncRequested += OnSyncRequested;
        Session.SyncDeclined += () => AddChat("Host declined the sync.");
        Session.SyncStarted += OnGuestSyncStarted;
    }

    private void ResetSync()
    {
        _sync = SyncStep.None; _syncMenuAt = -1; _syncRequestUntil = -1; _syncFile = null; _syncSaving = false; _syncGuestLeft = false; _switch = null;
        _guestSync = false; _guestSyncMenuAt = -1; _faultAnnounced = false; _projectionFault = null;
    }

    // ---------------- typed lines and commands ----------------

    /// <summary>What the chat box submits: a /command, or a line for the other player.</summary>
    public void SubmitChat(string text)
    {
        text = ChatLine.Clean(text);
        if (text.Length == 0) return;
        if (!text.StartsWith("/", StringComparison.Ordinal))
        {
            if (Session.SendChat(ChatKind.Player, text)) AddChat("You: " + text, system: false);
            else AddChat("Not connected to the other player.");
            return;
        }
        var command = text.Split(' ', 2)[0].ToLowerInvariant();
        switch (command)
        {
            case "/help":
                AddChat("/sync: save and reload both games to fix an out-of-sync world. A Client's request needs the host's /yes.");
                AddChat("/yes, /no: host only, answer a sync request.");
                break;
            case "/sync":
                if (Session.Role == Role.Host) StartSync("Host");
                else RequestSync();
                break;
            case "/yes":
                if (Session.Role != Role.Host) AddChat("Only the host answers sync requests.");
                else if (!SyncRequestWaiting) AddChat("No sync request is waiting.");
                else StartSync("Client");
                break;
            case "/no":
                if (Session.Role != Role.Host) AddChat("Only the host answers sync requests.");
                else if (!SyncRequestWaiting) AddChat("No sync request is waiting.");
                else { _syncRequestUntil = -1; Session.DeclineSync(); AddChat("Sync declined."); }
                break;
            default:
                AddChat($"Unknown command {command}. Type /help.");
                break;
        }
    }

    private void RequestSync()
    {
        if (Session.Role != Role.Client || !Session.IsJoined) { AddChat("Not connected to a host."); return; }
        if (_guestSync) { AddChat("A sync is already running."); return; }
        if (!Session.RequestSync()) { AddChat("You can sync once you're in the host's world."); return; }
        AddChat("Asked the host to sync both games.");
    }

    private void OnSyncRequested()
    {
        if (_sync != SyncStep.None) { Session.SendChat(ChatKind.System, "A sync is already running."); return; }
        _syncRequestUntil = Time.realtimeSinceStartup + SyncRequestSeconds;
        AddChat("Client asks to sync both games (save and reload). Type /yes to start or /no to decline.");
        Notice("Client asks to sync: press Enter, then type /yes or /no.");
    }

    // ---------------- out-of-sync marker ----------------

    private void AnnounceOutOfSync(string why)
    {
        if (_faultAnnounced) return;
        _faultAnnounced = true;
        AddChat($"This game is out of sync with the host's world ({why}). Type /sync to fix it.");
        Session.SendChat(ChatKind.System, $"{Me} is out of sync. Type /sync to fix it.");
    }

    private void UpdateOutOfSync()
    {
        if (_projectionFault != null) AnnounceOutOfSync(_projectionFault);
        else if (Objects.Initialized && Objects.ProjectionFault is { } fault) AnnounceOutOfSync(fault);
        else if (Session.World?.Divergence == null) _faultAnnounced = false;
    }

    // ---------------- host ----------------

    private void StartSync(string by)
    {
        string why = null;
        if (_sync != SyncStep.None) why = "a sync is already running";
        else if (!_hostWorldStarted || !CoopRunner.InWorkshop) why = "the world is still loading";
        else if (_pendingFile != null || (Session.IsJoined && Session.GuestState == GuestState.Loading)) why = "Client is still loading the world";
        if (why != null)
        {
            AddChat("Can't sync now: " + why + ".");
            if (by == "Client") Session.SendChat(ChatKind.System, "The host can't sync now: " + why + ".");
            return;
        }
        _syncRequestUntil = -1;
        _syncGuestLeft = Session.BeginResync(by == "Host" ? "the host started it" : "Client asked");
        _copiesToSend.Clear();
        _sync = SyncStep.Saving;
        _syncDeadline = Time.realtimeSinceStartup + SyncSaveTimeout;
        AddChat($"Sync started ({(by == "Host" ? "you" : "Client asked")}): saving and reloading both games.");
        Plugin.Logger.LogInfo($"[sync] started by {by}; guest told to leave: {_syncGuestLeft}.");
    }

    private void UpdateHostSync()
    {
        var now = Time.realtimeSinceStartup;
        if (_syncRequestUntil > 0 && now > _syncRequestUntil)
        {
            _syncRequestUntil = -1;
            AddChat("Client's sync request expired.");
            Session.SendChat(ChatKind.System, "The sync request expired.");
        }
        switch (_sync)
        {
            case SyncStep.Saving:
                if (now > _syncDeadline) { FailSync("the world didn't settle for saving"); return; }
                if (!_readiness.Ready || Workstations?.Settled == false || _pairWrites.Count > 0 || _pendingFile != null || SaveLoadSystem.WaitingOnThread()) return;
                if (!Objects.Assembly.PrepareReload()) return;
                if (_switch != null) SaveForSwitch(); else SaveForSync();
                return;
            case SyncStep.Pairing:
                if (now > _syncDeadline) { FailSync("the save took too long"); return; }
                if (_pairWrites.Count > 0 || SaveLoadSystem.WaitingOnThread()) return;
                if (_saveError != null) { FailSync(_saveError); return; }
                if (_syncFile != null)
                {
                    _syncFile = File.Exists(_syncFile + ".zip") ? _syncFile + ".zip" : _syncFile; // as the pair writer resolves it
                    if (!File.Exists(_syncFile + ".coop")) { FailSync("the co-op save wasn't paired"); return; }
                }
                _sync = SyncStep.ToMenu; _syncMenuAt = -1; _syncDeadline = now + SyncMenuTimeout;
                AddChat(_switch == null ? "Sync: saved. Reloading the world." : _switch.Reset ? "Resetting the world to the default build." : $"Loading \"{CoopSaves.DisplayName(_switch.File)}\".");
                var loader = Object.FindObjectOfType<NewLoadingScreen>();
                if (loader == null) { FailSync("no loading screen was found"); return; }
                loader.LoadScene(CoopRunner.MenuScene, false);
                return;
            case SyncStep.ToMenu:
                if (now > _syncDeadline) { LeaveToMenu("The sync could not return to the main menu.", false); return; }
                if (CoopRunner.Instance?.Scene != CoopRunner.MenuScene || CoopRunner.Instance.Menu == null) return;
                if (_syncMenuAt < 0) _syncMenuAt = now;
                if (now - _syncMenuAt < 2f) return; // let the menu finish initializing, as Host Continue does
                try { ReloadForSync(); }
                catch (Exception e) { Plugin.Logger.LogError(e); LeaveToMenu("The sync could not reload the co-op world: " + e.Message, false); return; }
                _sync = SyncStep.Loading; _syncDeadline = now + SyncLoadTimeout;
                return;
            case SyncStep.Loading:
                if (_hostWorldStarted)
                {
                    _sync = SyncStep.None;
                    _faultAnnounced = false;
                    var what = _switch == null ? "Sync: the world is reloaded." : _switch.Reset ? "The world is reset." : "The world is loaded.";
                    AddChat(Session.IsJoined ? what + " Sending it to Client." : what);
                    Plugin.Logger.LogInfo($"[sync] host reloaded at generation {Session.World.Generation} ({_hostWorldKind}).");
                    _switch = null;
                }
                else if (now > _syncDeadline) LeaveToMenu($"The sync's reload took too long (still waiting: {_readiness.Waiting}).", false);
                return;
        }
    }

    private void SaveForSync()
    {
        var saves = Object.FindObjectOfType<SaveLoadSystem>();
        if (saves == null) { FailSync("the save system isn't ready"); return; }
        string path = null;
        _saveError = null;
        // Repair can replace resource incarnations. Release the old world's callback authority through the
        // accepted stream before capturing the repaired baseline; stale claims must not enter its sidecar.
        Session.World.Submit(CommandTypes.ActorActivity, ActivityWorld.Presence(ActorSlot.Guest, default), out _);
        Session.World.Submit(CommandTypes.ActorActivity, ActivityWorld.Presence(ActorSlot.Host, Session.LocalIdentity.Player), out _);
        _syncSaving = true;
        try { path = saves.CreateSaveGame(CoopSaves.AutosaveName, false, ""); }
        catch (Exception e) { _saveError ??= e.Message; }
        finally { _syncSaving = false; }
        if (string.IsNullOrEmpty(path) || _saveError != null) { FailSync(_saveError ?? "the save did not start"); return; }
        _syncFile = path; // the file is written on a thread; Pairing resolves its final name
        _sync = SyncStep.Pairing;
        _syncDeadline = Time.realtimeSinceStartup + SyncPairTimeout;
        Plugin.Logger.LogInfo($"[sync] saving '{path}'.");
    }

    /// <summary>
    /// Host, from the pause menu (owner decision, phase 8): load another co-op save, or reset the world to the default
    /// build (<paramref name="saveName"/> null), without ending the session. It runs like /sync: the current world is
    /// saved first if anything changed, the guest leaves to its menu, the host loads the chosen save and starts a new
    /// generation, and the guest is admitted from a fresh snapshot. Returns why it can't start, or null.
    /// </summary>
    internal string StartWorldSwitch(string saveName)
    {
        if (Session.Role != Role.Host || CoopSaves.Current != CoopSaves.Mode.Host) return "you aren't hosting a co-op world";
        if (_sync != SyncStep.None) return "a sync is already running";
        if (!_hostWorldStarted || !_readiness.Ready || !CoopRunner.InWorkshop) return "the world is still loading";
        if (_pendingFile != null || (Session.IsJoined && Session.GuestState is GuestState.Loading or GuestState.Leaving)) return "Client is still loading the world";
        WorldSwitch target;
        if (saveName == null && CoopSaves.Kind != CoopGameMode.FreeBuild) return "only a Free Build world can be reset";
        if (saveName == null)
        {
            var name = CoopSaves.FindSaveName(f => f.StartsWith("defaultFreebuild.binary", StringComparison.OrdinalIgnoreCase));
            if (name == null) return "the game's default build isn't in the co-op folder";
            target = new WorldSwitch(name, name, CoopSaves.WorldStem, true);
        }
        else
        {
            string file = null;
            foreach (var info in SaveLoadSystem.GetSaveGamesWindows())
                if (info.m_name == saveName) { file = info.m_info?.Name; break; }
            if (file == null) return "that save isn't in the co-op folder";
            var path = CoopSaves.NativeFile(CoopSaves.CoopDir, file);
            if (CoopSaveBackups.Restore(path, CoopSaves.BackupDir, out var unreadable))
            {
                Plugin.Logger.LogWarning($"[saves] '{file}' didn't load ({unreadable}); its backup was put back and loads instead.");
                NoteRestored(new[] { CoopSaves.DisplayName(file) });
            }
            if (!CoopSaveBackups.Readable(path, CoopSaves.Kind, out var problem)) return "that save can't be loaded in co-op (" + problem.TrimEnd('.') + ")";
            if (NativeModeProblem(saveName, CoopSaves.Kind) is { } native) return "that save can't be loaded in co-op (" + native.TrimEnd('.') + ")";
            if (!ResolveStart(HostStart.Load(saveName, CoopSaves.Kind), out var stem, out _, out var error)) return error;
            target = new WorldSwitch(saveName, file, stem, false);
        }
        _syncRequestUntil = -1;
        _switch = target;
        _syncGuestLeft = Session.BeginResync(target.Reset ? "the host is resetting the world" : "the host is loading another world");
        _copiesToSend.Clear();
        _sync = SyncStep.Saving;
        _syncDeadline = Time.realtimeSinceStartup + SyncSaveTimeout;
        AddChat(target.Reset ? "Resetting the world: saving it first, then both games load the default build."
            : $"Loading \"{CoopSaves.DisplayName(target.File)}\": saving this world first, then both games load it.");
        Plugin.Logger.LogInfo($"[sync] world switch to '{target.SaveName}' (reset {target.Reset}); guest told to leave: {_syncGuestLeft}.");
        return null;
    }

    /// <summary>A world switch saves the world being left as an ordinary save (Exit does the same), unless nothing
    /// changed since the last save. A refused save doesn't stop the switch.</summary>
    private void SaveForSwitch()
    {
        _syncFile = null; _saveError = null;
        if (_savedRevision != Session.World.Revision)
        {
            var saves = Object.FindObjectOfType<SaveLoadSystem>();
            string path = null;
            try { path = saves?.CreateSaveGame(CoopSaves.AutosaveName, false, ""); }
            catch (Exception e) { _saveError ??= e.Message; }
            if (string.IsNullOrEmpty(path) || _saveError != null)
            {
                AddChat($"Couldn't save this world first ({(_saveError ?? OutOfSync ?? "the save was refused").TrimEnd('.')}); continuing without saving.");
                _saveError = null;
            }
            else _syncFile = path;
        }
        _sync = SyncStep.Pairing;
        _syncDeadline = Time.realtimeSinceStartup + SyncPairTimeout;
    }

    /// <summary>On the main menu after the sync save: continue that exact paired save, as Host Continue does, without
    /// ending the session. The next generation start restores its facts. A world switch loads its target instead.</summary>
    private void ReloadForSync()
    {
        if (_switch is { } target) { ReloadForSwitch(target); return; }
        var fileName = Path.GetFileName(_syncFile);
        var bare = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;
        var name = CoopSaves.FindSaveName(f => f.Equals(fileName, StringComparison.OrdinalIgnoreCase) || f.Equals(bare, StringComparison.OrdinalIgnoreCase))
                   ?? throw new FileNotFoundException("The saved world isn't in the save list.", fileName);
        _loadingPair = CoopSavePair.ReadSidecar(_syncFile);
        _hostWorldStarted = false;
        _hostWorldKind = "co-op world reloaded by /sync";
        _readiness.Reset();
        // Nothing from the old scene may bind into the reloaded one; the generation start resets the rest.
        Objects.Reset(); Activity?.Reset(); Workstations?.Reset(); ResetAutosave(); DestroyView();
        Plugin.Logger.LogInfo($"[sync] host continues '{name}' from '{CoopSaves.CoopDir}'.");
        LoadExact(name, _loadingPair.Campaign);
    }

    private void ReloadForSwitch(WorldSwitch target)
    {
        var name = CoopSaves.FindSaveName(f => f.Equals(target.File, StringComparison.OrdinalIgnoreCase)) ?? target.SaveName;
        _loadingPair = target.Reset ? null : CoopSavePair.ReadSidecar(CoopSaves.NativeFile(CoopSaves.CoopDir, target.File));
        if (_loadingPair?.Campaign.Problem(CoopSaves.Kind) is { } wrong) throw new InvalidDataException(wrong);
        CoopSaves.SwitchWorld(target.Stem);
        if (_loadingPair != null) CoopSaves.SetCampaign(_loadingPair.Campaign);
        _hostWorldStarted = false;
        _hostWorldKind = target.Reset ? "co-op world reset to the default build" : "co-op world loaded from the pause menu";
        _readiness.Reset();
        Objects.Reset(); Activity?.Reset(); Workstations?.Reset(); ResetAutosave(); DestroyView();
        _guestPoses.Clear(); // another world: where the guest stood no longer applies
        Plugin.Logger.LogInfo($"[sync] host loads '{name}' from '{CoopSaves.CoopDir}' ({_hostWorldKind}).");
        LoadExact(name, CoopSaves.Campaign);

    }

    private void FailSync(string why)
    {
        Plugin.Logger.LogWarning("[sync] failed: " + why);
        _sync = SyncStep.None;
        AddChat($"{(_switch == null ? "Sync" : "Loading the world")} failed: {why}." + (_syncGuestLeft ? " Client reloads your current world instead." : ""));
        _switch = null;
        Session.SendChat(ChatKind.System, $"The host's sync failed ({why}). Reloading the host's current world instead.");
        _syncGuestLeft = false;
    }

    // ---------------- guest ----------------

    private void OnGuestSyncStarted(string by)
    {
        _guestSync = true; _guestSyncMenuAt = -1;
        _guestSyncDeadline = Time.realtimeSinceStartup + GuestSyncTimeout;
        _guestLoading = false;
        AddChat(by.StartsWith("the host is", StringComparison.Ordinal)
            ? $"The host is {(by.Contains("resetting") ? "resetting the world" : "loading another world")}: returning to the menu, then loading it."
            : $"Host is syncing both games ({by}): returning to the menu, then loading the host's world.");
        Objects.Reset(); ResetAutosave(); DestroyView();
        if (CoopRunner.Instance?.Scene == CoopRunner.MenuScene) return;
        var loader = Object.FindObjectOfType<NewLoadingScreen>();
        if (loader != null) loader.LoadScene(CoopRunner.MenuScene, false);
        else LeaveToMenu("No loading screen was found for the sync.", false);
    }

    private void UpdateGuestSync()
    {
        if (!_guestSync || _guestLoading) return;
        var now = Time.realtimeSinceStartup;
        if (now > _guestSyncDeadline) { LeaveToMenu("The sync took too long.", false); return; }
        if (Session.GuestState != GuestState.Leaving || CoopRunner.Instance?.Scene != CoopRunner.MenuScene || CoopRunner.Instance.Menu == null) return;
        if (_guestSyncMenuAt < 0) _guestSyncMenuAt = now;
        if (now - _guestSyncMenuAt < 1f) return;
        if (Session.ReadyForWorld()) Plugin.Logger.LogInfo("[sync] guest back on its menu; asked the host for the world.");
    }

    private void OnGuestSyncAdmitted()
    {
        if (!_guestSync) return;
        _guestSync = false; _faultAnnounced = false;
        AddChat("Sync complete: you're back in the host's world.");
        Session.SendChat(ChatKind.System, "Client finished the sync.");
    }
}

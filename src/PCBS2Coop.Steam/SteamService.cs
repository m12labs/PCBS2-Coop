using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using PCBS2Coop.Network;
using Steamworks;
using Steamworks.Data;

namespace PCBS2Coop.Steam;

/// <summary>Owns only this mod's Steam runtime. Asynchronous call results are consumed on Poll's thread.</summary>
public sealed class SteamService : ISteamService
{
    public const string Discriminator = "com.macery12.pcbs2coop";
    private readonly Action<string> _log;
    private readonly string? _oldAppId, _oldGameId;
    private readonly List<Attempt> _attempts = new();
    private sealed class Attempt
    {
        public int Id;
        public ulong LobbyId;
        public Task<Lobby?>? Create;
        public Task<RoomEnter>? Join;
        public PeerIdentity Identity = null!;
        public Action<ISessionTransport?, string?> Completed = null!;
        public long Started = Environment.TickCount64;
        public bool Done => Create?.IsCompleted ?? Join!.IsCompleted;
    }
    private int _attemptId;
    private bool _ownsRuntime, _disposed, _host, _ready;
    private bool? _publishedReady, _publishedAdmission;
    private string? _failure, _pendingFailure;
    private long _nextCheck;
    private Lobby? _lobby;
    private SteamId _originalHost;
    private string _session = "";
    private SteamTransport? _transport;
    public uint AppId { get; }
    public bool Available => !_disposed && _ownsRuntime && SteamClient.IsValid && SteamClient.IsLoggedOn;
    public string UnavailableReason => _failure ?? (Available ? "" : "Steam is unavailable or offline. Open Steam, then restart PCBS2 if necessary.");
    public bool Busy { get; private set; }
    public bool HasLobby => _lobby.HasValue;
    public bool CanInvite => Available && _host && _ready && HasLobby && _transport is { Reserved: false } && _lobby!.Value.MemberCount == 1;
    internal SteamId LobbyId => _lobby?.Id ?? default;
    public event Action<ulong>? InviteRequested;
    public event Action<string>? Failed;

    public SteamService(uint appId, string dependencyDirectory, Action<string> log)
    {
        AppId = appId; _log = log;
        _oldAppId = Environment.GetEnvironmentVariable("SteamAppId");
        _oldGameId = Environment.GetEnvironmentVariable("SteamGameId");
        var attemptedInit = false;
        try
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new InvalidOperationException("Steam requires Windows x64.");
            if (appId == 0) throw new InvalidOperationException("Steam App ID must be nonzero.");
            if ((!string.IsNullOrEmpty(_oldAppId) && _oldAppId != appId.ToString()) ||
                (!string.IsNullOrEmpty(_oldGameId) && _oldGameId != appId.ToString()))
                throw new InvalidOperationException("A different Steam App ID is already active; the mod will not override it.");
            if (SteamClient.IsValid) throw new InvalidOperationException("A Steamworks runtime is already active; coexistence is not supported yet.");
            using (var process = Process.GetCurrentProcess())
                foreach (ProcessModule module in process.Modules)
                    if (string.Equals(module.ModuleName, "steam_api64.dll", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A native Steamworks module is already loaded; the mod will not replace it.");
            var nativePath = Path.GetFullPath(Path.Combine(dependencyDirectory, "steam_api64.dll"));
            NativeLibrary.SetDllImportResolver(typeof(SteamClient).Assembly, (name, assembly, path) =>
                name.Contains("steam_api64", StringComparison.OrdinalIgnoreCase) ? NativeLibrary.Load(nativePath) : IntPtr.Zero);
            attemptedInit = true;
            SteamClient.Init(appId, asyncCallbacks: false);
            _ownsRuntime = true;
            if ((uint)SteamClient.AppId != appId) throw new InvalidOperationException("Steam initialized with the wrong App ID.");
            SteamNetworkingUtils.InitRelayNetworkAccess();
            Dispatch.OnException += CallbackFailed;
            SteamFriends.OnGameLobbyJoinRequested += OnInvite;
            SteamUser.OnSteamServersDisconnected += BackendLost;
            _log($"Steam initialized: App ID {AppId}; manual callbacks; overlay={SteamUtils.IsOverlayEnabled}.");
        }
        catch (Exception e)
        {
            _failure = "Steam unavailable: " + e.Message;
            _log(_failure);
            // Init can throw after native initialization. We entered only with no active runtime.
            if (attemptedInit && SteamClient.IsValid) SteamClient.Shutdown();
            _ownsRuntime = false;
            RestoreEnvironment();
        }
    }

    private void CallbackFailed(Exception e) { _log("Steam callback failed: " + e); _pendingFailure ??= "A Steam callback failed. See the log."; }
    private void BackendLost() => _pendingFailure ??= "Steam lost its connection to the backend.";
    private void OnInvite(Lobby lobby, SteamId friend) { if (!_disposed) InviteRequested?.Invoke(lobby.Id); }

    public void BeginHost(PeerIdentity identity, Action<ISessionTransport?, string?> completed) => Begin(0, identity, completed);
    public void BeginJoin(ulong lobbyId, PeerIdentity identity, Action<ISessionTransport?, string?> completed)
    {
        if (lobbyId == 0) { completed(null, "Invalid Steam lobby invitation."); return; }
        Begin(lobbyId, identity, completed);
    }
    private void Begin(ulong lobbyId, PeerIdentity identity, Action<ISessionTransport?, string?> completed)
    {
        if (!Available) { completed(null, UnavailableReason); return; }
        if (Busy || HasLobby || _attempts.Count >= 8 || _attempts.Any(a => a.LobbyId == lobbyId))
        { completed(null, "A Steam lobby request is still finishing. Try again shortly."); return; }
        var attempt = new Attempt { Id = ++_attemptId, LobbyId = lobbyId, Identity = identity, Completed = completed };
        Busy = true;
        try
        {
            if (lobbyId == 0) attempt.Create = SteamMatchmaking.CreateLobbyAsync(2);
            else attempt.Join = new Lobby(lobbyId).Join();
            _attempts.Add(attempt);
        }
        catch (Exception e) { Busy = false; completed(null, e.Message); }
    }

    public void Poll()
    {
        if (_disposed || !_ownsRuntime) return;
        SteamClient.RunCallbacks();
        foreach (var attempt in _attempts.ToArray())
        {
            if (!attempt.Done)
            {
                if (attempt.Id == _attemptId && Environment.TickCount64 - attempt.Started > 30_000)
                {
                    ++_attemptId; Busy = false;
                    attempt.Completed(null, "Steam lobby request timed out after 30 seconds.");
                }
                continue;
            }
            _attempts.Remove(attempt);
            Lobby? result = null;
            string? error = null;
            try
            {
                if (attempt.Create != null) result = attempt.Create.GetAwaiter().GetResult();
                else if (attempt.Join!.GetAwaiter().GetResult() == RoomEnter.Success) result = new Lobby(attempt.LobbyId);
                else error = "Steam refused lobby entry: " + attempt.Join.Result;
                if (result == null) error ??= "Steam could not create the lobby.";
            }
            catch (Exception e) { error = "Steam lobby request failed: " + e.Message; }
            if (attempt.Id != _attemptId)
            {
                if (result.HasValue && result.Value.Id != LobbyId) result.Value.Leave();
                continue;
            }
            Busy = false;
            if (error != null) { attempt.Completed(null, error); continue; }
            _lobby = result;
            _host = attempt.Create != null; _ready = false; _publishedReady = null;
            try
            {
                var lobby = _lobby!.Value;
                if (_host)
                {
                    _originalHost = SteamClient.SteamId; _session = Guid.NewGuid().ToString("N");
                    if (!lobby.SetPrivate() || !lobby.SetJoinable(false)) throw new InvalidOperationException("Steam could not make the lobby private.");
                    var fields = new Dictionary<string, string>
                    {
                        ["mod"] = Discriminator, ["version"] = ModVersion.Value, ["host"] = _originalHost.ToString(),
                        ["session"] = _session, ["ready"] = "0", ["game"] = attempt.Identity.Game.Version,
                        ["build"] = attempt.Identity.Game.BuildGuid
                    };
                    foreach (var field in fields) if (!lobby.SetData(field.Key, field.Value)) throw new InvalidOperationException("Steam lobby metadata could not be published.");
                }
                else
                {
                    _session = lobby.GetData("session");
                    if (lobby.GetData("mod") != Discriminator || !Guid.TryParseExact(_session, "N", out _) ||
                        !ulong.TryParse(lobby.GetData("host"), out var host) || host == 0 || lobby.Owner.Id != (SteamId)host ||
                        host == SteamClient.SteamId || lobby.GetData("ready") != "1" || lobby.MemberCount != 2)
                        throw new InvalidOperationException("This invite is unrelated, full, stale, or the host is not ready.");
                    _originalHost = host;
                }
                _transport = new SteamTransport(this, _host ? Role.Host : Role.Client, _originalHost, _session);
                if (_host) _transport.PrepareHost();
                _log($"Steam lobby {(_host ? "created" : "joined")}; App ID {AppId}.");
                attempt.Completed(_transport, null);
            }
            catch (Exception e) { Cancel(); attempt.Completed(null, e.Message); }
        }
        if (HasLobby && Environment.TickCount64 >= _nextCheck)
        {
            _nextCheck = Environment.TickCount64 + 500;
            var lobby = _lobby!.Value;
            if (!SteamClient.IsLoggedOn || lobby.Owner.Id != _originalHost || !IsMember(_originalHost) || !IsMember(SteamClient.SteamId) ||
                lobby.GetData("session") != _session || lobby.GetData("mod") != Discriminator)
                _pendingFailure ??= "The original Steam host or lobby is no longer available.";
        }
        if (_pendingFailure != null)
        {
            var reason = _pendingFailure; _pendingFailure = null;
            Cancel();
            Failed?.Invoke(reason);
        }
    }

    internal bool IsMember(SteamId id) => _lobby.HasValue && _lobby.Value.Members.Any(member => member.Id == id);
    internal bool AcceptMember(SteamId id) => _host && _ready && id != SteamClient.SteamId && IsMember(id) && _lobby!.Value.Owner.Id == _originalHost;
    internal void ReserveGuest() => PublishReady(_ready, false);
    internal void GuestDropped() => PublishReady(_ready, false); // reopen only after the controller has performed world cleanup
    public void SetHostReady(bool ready)
    {
        _ready = ready && _host;
        PublishReady(_ready, _ready && _transport is { Reserved: false } && _lobby?.MemberCount == 1);
    }
    private void PublishReady(bool ready, bool admission)
    {
        if (!_host || !_lobby.HasValue || (_publishedReady == ready && _publishedAdmission == admission)) return;
        var lobby = _lobby.Value;
        if (!lobby.SetData("ready", ready ? "1" : "0") || !lobby.SetJoinable(admission))
            _pendingFailure ??= "Steam could not update lobby admission.";
        else { _publishedReady = ready; _publishedAdmission = admission; }
    }
    public bool OpenInviteOverlay()
    {
        if (!CanInvite || !SteamUtils.IsOverlayEnabled) return false;
        SteamFriends.OpenGameInviteOverlay(LobbyId); return true;
    }
    public IReadOnlyList<SteamFriend> GetFriends() => Available
        ? SteamFriends.GetFriends().Select(f => new SteamFriend(f.Id, f.Name, f.IsOnline)).OrderByDescending(f => f.Online).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray()
        : Array.Empty<SteamFriend>();
    public bool Invite(ulong friendId) => CanInvite && SteamFriends.GetFriends().Any(f => f.Id == (SteamId)friendId && f.IsOnline) && _lobby!.Value.InviteFriend(friendId);
    public void Cancel()
    {
        ++_attemptId; Busy = false; _ready = false; _publishedReady = null; _publishedAdmission = null;
        _transport?.Dispose(); _transport = null;
        if (_lobby.HasValue) { _lobby.Value.Leave(); _lobby = null; }
        _host = false;
    }
    private void RestoreEnvironment()
    {
        Environment.SetEnvironmentVariable("SteamAppId", _oldAppId);
        Environment.SetEnvironmentVariable("SteamGameId", _oldGameId);
    }
    public void Dispose()
    {
        if (_disposed) return;
        Cancel(); _disposed = true;
        if (!_ownsRuntime) return;
        SteamFriends.OnGameLobbyJoinRequested -= OnInvite;
        SteamUser.OnSteamServersDisconnected -= BackendLost;
        Dispatch.OnException -= CallbackFailed;
        SteamClient.Shutdown(); _ownsRuntime = false; RestoreEnvironment();
    }
}

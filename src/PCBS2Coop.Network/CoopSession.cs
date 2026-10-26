using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

public sealed class SessionStats
{
    public long PlayerStatesSent, PlayerStatesReceived, InvalidPlayerStates, UnknownMessages, MalformedMessages, RefusedJoins;
    public override string ToString() =>
        $"states sent {PlayerStatesSent}, received {PlayerStatesReceived}, invalid {InvalidPlayerStates}, unknown msgs {UnknownMessages}, malformed {MalformedMessages}, refused joins {RefusedJoins}";
}

/// <summary>
/// A two-player session over LAN or Steam: one host and one guest.
/// <para>Joining: the guest's connect request carries its <see cref="PeerIdentity"/>. The host refuses an incompatible
/// guest with a readable reason, otherwise accepts and sends Welcome (host identity, warnings, world position).
/// <see cref="PeerJoined"/> fires on both sides once that handshake is complete.</para>
/// <para>World changes go through <see cref="World"/> on one reliable ordered stream; player positions use a
/// separate sequenced stream that may drop stale packets.</para>
/// <para>Admission: a joined guest is not in the world until it loads a host snapshot. The host sends one when
/// <see cref="GuestNeedsWorld"/> (<see cref="BeginSnapshot"/>, then <see cref="SetSnapshotFile"/>); the guest gets
/// <see cref="SnapshotReceived"/>, loads it and calls <see cref="CompleteAdmission"/> or <see cref="FailAdmission"/>.</para>
/// Not thread-safe; call every member, including <see cref="Poll"/>, from one thread. Events are raised from inside <see cref="Poll"/>.
/// </summary>
public sealed class CoopSession : IDisposable
{
    private ISessionTransport? _transport;
    private readonly NetDataWriter _writer = new();
    private bool _joined;
    private string? _refusal;
    private SnapshotSender _sender;
    private readonly SnapshotReceiver _receiver = new();
    private uint _loadingTransfer;
    private readonly Func<long> _now;
    private long _joinStarted;
    private bool _handshakeExpired;

    /// <summary>Snapshot bytes sent per <see cref="Poll"/> (about 15 MB/s at 60 frames per second).</summary>
    public int SnapshotBytesPerPoll { get; set; } = 256 * 1024;

    public Role Role { get; private set; }
    public int Port { get; private set; }
    public bool IsRunning => Role != Role.None;
    /// <summary>Transport connection exists (the handshake may still be in progress).</summary>
    public bool HasPeer => _transport?.IsConnected == true;
    /// <summary>Handshake completed: identities exchanged and compatible.</summary>
    public bool IsJoined => HasPeer && _joined;
    public int PingMs => _transport?.PingMs ?? -1;

    public PeerIdentity? LocalIdentity { get; private set; }
    public PeerIdentity? RemoteIdentity { get; private set; }
    /// <summary>Warnings from the last successful join (DLC/catalog differences…). Empty when none.</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = Array.Empty<string>();
    /// <summary>World change pipeline for the running session; null when stopped.</summary>
    public WorldSync? World { get; private set; }
    public SessionStats Stats { get; private set; } = new();
    /// <summary>The guest's admission state (the host's view of its guest, or the guest's own).</summary>
    public GuestState GuestState { get; private set; }
    /// <summary>Host: the guest is connected but has no world, and no load snapshot is on its way.</summary>
    public bool GuestNeedsWorld => Role == Role.Host && IsJoined && World is { Admitted: true } && GuestState == GuestState.Connected && !_sender.LoadInProgress;
    /// <summary>Host: a snapshot transfer is queued or being sent.</summary>
    public bool SendingSnapshot => _sender.Queued > 0;
    /// <summary>Guest: progress of the snapshot being received (0..1), or -1.</summary>
    public float SnapshotProgress => _receiver.Active ? Math.Max(0, _receiver.Progress) : -1;

    /// <summary>Raised on both sides once the handshake completes.</summary>
    public event Action? PeerJoined;
    /// <summary>Raised when the peer leaves, a join is refused, or a connection attempt fails. The string is a readable reason.</summary>
    public event Action<string>? PeerDisconnected;
    public event Action<PlayerState>? RemoteState;
    public event Action<string>? Log;
    /// <summary>Raised when a new <see cref="World"/> is created (on Host/Join) so handlers can be registered.</summary>
    public event Action<WorldSync>? WorldCreated;
    /// <summary>Guest: a complete, verified snapshot arrived. For a load snapshot, load it and call CompleteAdmission/FailAdmission.</summary>
    public event Action<Snapshot>? SnapshotReceived;
    /// <summary>Raised when <see cref="GuestState"/> changes (both sides).</summary>
    public event Action<GuestState>? GuestStateChanged;
    /// <summary>A valid chat line arrived from the other player.</summary>
    public event Action<ChatLine>? ChatReceived;
    /// <summary>Host: the guest asked for a sync. Answer with <see cref="BeginResync"/> or <see cref="DeclineSync"/>.</summary>
    public event Action? SyncRequested;
    /// <summary>Guest: the host declined its sync request.</summary>
    public event Action? SyncDeclined;
    /// <summary>Guest: the host started a sync (the text says who asked). This guest has left the world
    /// (<see cref="GuestState.Leaving"/>); return to the menu and call <see cref="ReadyForWorld"/>.</summary>
    public event Action<string>? SyncStarted;

    public string TransportName => _transport?.Name ?? "LAN";

    public CoopSession() : this(() => Environment.TickCount64) { }
    internal CoopSession(Func<long> now)
    {
        _now = now;
        _sender = new SnapshotSender(SendReliable, SendBulk);
    }

    public bool Host(int port, PeerIdentity local, out string error) => Start(new LanTransport(port), Role.Host, port, local, out error);
    public bool Join(string address, int port, PeerIdentity local, out string error) => Start(new LanTransport(port, address), Role.Client, port, local, out error);
    public bool Host(ISessionTransport transport, PeerIdentity local, out string error) => Start(transport, Role.Host, 0, local, out error);
    public bool Join(ISessionTransport transport, PeerIdentity local, out string error) => Start(transport, Role.Client, 0, local, out error);

    private bool Start(ISessionTransport transport, Role role, int port, PeerIdentity local, out string error)
    {
        EnsureIdle();
        _transport = transport;
        transport.ValidateHello = ValidateHello;
        transport.Connected += OnPeerConnected;
        transport.Disconnected += OnPeerDisconnected;
        transport.Received += OnReceive;
        transport.Log += TransportLog;
        Begin(role, port, local);
        var hello = new NetDataWriter();
        hello.Put(Protocol.ConnectionKey);
        local.Write(hello);
        if (!transport.Start(role, hello.CopyData(), out error))
        {
            Stop();
            return false;
        }
        Log?.Invoke($"{role} started over {transport.Name} as {local}.");
        return true;
    }

    private void TransportLog(string message) => Log?.Invoke(message);

    public void Poll()
    {
        _transport?.Poll();
        if (Role == Role.Client && !_joined && !_handshakeExpired && _now() - _joinStarted > 30_000)
        {
            _handshakeExpired = true;
            _transport?.Disconnect("Compatibility handshake timed out after 30 seconds.");
        }
        if (Role == Role.Host && HasPeer) _sender.Pump(SnapshotBytesPerPoll);
    }
    // ---------------- snapshots ----------------

    /// <summary>
    /// Host: mark a snapshot at the current point of the change stream. Call it in the same frame as the game save
    /// that produces the file, then <see cref="SetSnapshotFile"/> once the file is written.
    /// </summary>
    public uint BeginSnapshot(SnapshotPurpose purpose)
    {
        if (Role != Role.Host || World == null) throw new InvalidOperationException("Only a running host sends snapshots.");
        if (!IsJoined) throw new InvalidOperationException("No guest to send a snapshot to.");
        var position = purpose == SnapshotPurpose.Load ? World.CapturePosition() : null;
        var id = _sender.Mark(purpose, position);
        Log?.Invoke(position != null
            ? $"Snapshot {id} marked at generation {position.Generation}, revision {position.Revision}."
            : $"Snapshot {id} (copy) marked.");
        return id;
    }

    public void SetSnapshotFile(uint id, string fileName, byte[] file)
    {
        _sender.SetFile(id, fileName, file);
        Log?.Invoke($"Snapshot {id}: sending '{fileName}' ({file.Length} bytes).");
    }

    public void AbortSnapshot(uint id, string reason)
    {
        _sender.Abort(id, reason);
        Log?.Invoke($"Snapshot {id} aborted: {reason}");
    }

    /// <summary>Guest: the load snapshot's game world is ready. Enters the world and applies the held changes.</summary>
    public bool CompleteAdmission(out string error)
    {
        error = "";
        if (Role != Role.Client || World == null || !IsJoined) { error = "Not connected to a host."; return false; }
        if (!World.Adopt(out error)) return false;
        SetGuestState(GuestState.InWorld);
        SendGuestStatus(GuestState.InWorld);
        return true;
    }

    /// <summary>Guest: the world couldn't be received or loaded. Leaves the session with the reason (the host sees it too).</summary>
    public void FailAdmission(string reason)
    {
        if (Role != Role.Client) return;
        Log?.Invoke("Could not enter the host's world: " + reason);
        World?.CancelHold();
        _receiver.Reset();
        SetGuestState(GuestState.Failed);
        _refusal = reason;
        _transport?.Disconnect("The guest could not load the world: " + reason);
    }

    // ---------------- chat and sync ----------------

    /// <summary>Send a chat line to the other player. False when not joined or the text is empty after cleaning.</summary>
    public bool SendChat(ChatKind kind, string text)
    {
        var line = new ChatLine(kind, ChatLine.Clean(text));
        if (!IsJoined || !line.IsValid) return false;
        _writer.Reset();
        line.Write(_writer);
        SendReliable(_writer);
        return true;
    }

    /// <summary>Guest in the world: ask the host to sync both games.</summary>
    public bool RequestSync()
    {
        if (Role != Role.Client || !IsJoined || GuestState != GuestState.InWorld) return false;
        SendSync(SyncAction.Request, "");
        return true;
    }

    /// <summary>Host: decline the guest's sync request.</summary>
    public bool DeclineSync()
    {
        if (Role != Role.Host || !IsJoined) return false;
        SendSync(SyncAction.Decline, "");
        return true;
    }

    /// <summary>
    /// Host: a sync starts. A guest in the world is told to leave it (<see cref="GuestState.Leaving"/>); no snapshot is
    /// offered until it is back on its menu and asks again. Returns false when there is no guest in the world (nothing
    /// to tell), or the guest is loading a snapshot (the caller should wait).
    /// </summary>
    public bool BeginResync(string by)
    {
        if (Role != Role.Host || !IsJoined || GuestState != GuestState.InWorld || _sender.LoadInProgress) return false;
        _sender.Clear(); // a copy queued for the old world is no longer useful
        SetGuestState(GuestState.Leaving);
        SendSync(SyncAction.Start, by);
        Log?.Invoke($"Sync started ({by}); the guest leaves the world.");
        return true;
    }

    /// <summary>Guest: back on the menu after a sync. The host sends the world again.</summary>
    public bool ReadyForWorld()
    {
        if (Role != Role.Client || !IsJoined || GuestState != GuestState.Leaving) return false;
        SetGuestState(GuestState.Connected);
        SendGuestStatus(GuestState.Connected);
        return true;
    }

    private void SendSync(SyncAction action, string text)
    {
        _writer.Reset();
        _writer.Put((byte)MessageType.Sync);
        _writer.Put((byte)action);
        _writer.Put(text ?? "", Protocol.MaxShortString);
        SendReliable(_writer);
    }

    private void OnChat(NetDataReader r)
    {
        var line = ChatLine.Read(r);
        if (r.AvailableBytes != 0 || !line.IsValid) { Stats.MalformedMessages++; return; }
        ChatReceived?.Invoke(line);
    }

    private void OnSync(NetDataReader r)
    {
        var action = (SyncAction)r.GetByte();
        var text = Protocol.ReadString(r);
        if (r.AvailableBytes != 0) { Stats.MalformedMessages++; return; }
        switch (action)
        {
            case SyncAction.Request when Role == Role.Host: SyncRequested?.Invoke(); return;
            case SyncAction.Decline when Role == Role.Client: SyncDeclined?.Invoke(); return;
            case SyncAction.Start when Role == Role.Client:
                World?.LeaveWorld("the host started a sync");
                _receiver.Reset();
                SetGuestState(GuestState.Leaving);
                Log?.Invoke($"The host started a sync ({text}).");
                SyncStarted?.Invoke(text);
                return;
            default: Stats.MalformedMessages++; return;
        }
    }

    private void SendGuestStatus(GuestState state)
    {
        _writer.Reset();
        _writer.Put((byte)MessageType.GuestStatus);
        _writer.Put((byte)state);
        _writer.Put(World?.Generation ?? 0);
        SendReliable(_writer);
    }

    private void SetGuestState(GuestState state)
    {
        if (GuestState == state) return;
        GuestState = state;
        GuestStateChanged?.Invoke(state);
    }

    private void OnSnapshotMessage(MessageType type, NetDataReader r)
    {
        if (Role != Role.Client) { Stats.MalformedMessages++; return; }
        string? error = null;
        switch (type)
        {
            case MessageType.SnapshotMark:
            {
                if (_receiver.Active && _receiver.Purpose == SnapshotPurpose.Load) World!.CancelHold();
                var position = _receiver.OnMark(r, out var id, out var purpose);
                if (position != null)
                {
                    _loadingTransfer = id;
                    World!.BeginHold(position);
                    if (GuestState == GuestState.InWorld) SetGuestState(GuestState.Connected);
                }
                Log?.Invoke($"Receiving snapshot {id} ({purpose}).");
                break;
            }
            case MessageType.SnapshotFile:
                error = _receiver.OnFile(r);
                break;
            case MessageType.SnapshotChunk:
                error = _receiver.OnChunk(r, out var done);
                if (done != null)
                {
                    Log?.Invoke($"Snapshot {done.TransferId} complete: '{done.FileName}', {done.File.Length} bytes, checksum OK.");
                    if (done.Purpose == SnapshotPurpose.Load)
                    {
                        SetGuestState(GuestState.Loading);
                        SendGuestStatus(GuestState.Loading);
                    }
                    SnapshotReceived?.Invoke(done);
                }
                break;
            case MessageType.SnapshotAbort:
            {
                var id = r.GetUInt();
                var reason = Protocol.ReadString(r);
                Log?.Invoke($"Host aborted snapshot {id}: {reason}");
                if (id == _loadingTransfer) World!.CancelHold();
                _receiver.Reset();
                break;
            }
        }
        if (error == null) return;
        var wasLoad = _receiver.Purpose == SnapshotPurpose.Load;
        _receiver.Reset();
        if (wasLoad) FailAdmission(error);
        else Log?.Invoke("Dropped a copy snapshot: " + error);
    }

    private void OnGuestStatus(NetDataReader r)
    {
        if (Role != Role.Host) { Stats.MalformedMessages++; return; }
        var state = (GuestState)r.GetByte();
        var gen = r.GetUInt();
        if (state == GuestState.Connected && GuestState == GuestState.Leaving)
        {
            Log?.Invoke("Guest is back on its menu after the sync and needs the world.");
            SetGuestState(GuestState.Connected);
            return;
        }
        if (state is not (GuestState.Loading or GuestState.InWorld)) { Stats.MalformedMessages++; return; }
        if (state == GuestState.InWorld && gen != World?.Generation)
        {
            Log?.Invoke($"Guest entered generation {gen}, but the world is at {World?.Generation}; it needs a new snapshot.");
            SetGuestState(GuestState.Connected);
            return;
        }
        Log?.Invoke(state == GuestState.InWorld ? $"Guest is in the world (generation {gen})." : "Guest is loading the world.");
        SetGuestState(state);
    }

    public void SendState(in PlayerState state)
    {
        if (!IsJoined) return;
        _writer.Reset();
        state.Write(_writer);
        _transport!.Send(_writer.CopyData(), SessionDelivery.LatestState);
        Stats.PlayerStatesSent++;
    }

    /// <summary>End the session. <paramref name="reason"/> is shown to the other player.</summary>
    public void Stop(string? reason = null)
    {
        if (Role == Role.None) return;
        var transport = _transport;
        _transport = null;
        if (transport != null)
        {
            transport.Connected -= OnPeerConnected;
            transport.Disconnected -= OnPeerDisconnected;
            transport.Received -= OnReceive;
            transport.Log -= TransportLog;
            transport.ValidateHello = null;
            if (reason != null) transport.Disconnect(reason);
            transport.Dispose();
        }
        _joined = false;
        _sender.Clear();
        _receiver.Reset();
        SetGuestState(GuestState.Connected);
        World?.OnLinkLost();
        World = null;
        Log?.Invoke($"Session stopped ({Role}). {Stats}");
        Role = Role.None;
        Port = 0;
        RemoteIdentity = null;
    }

    public void Dispose() => Stop();

    private void Begin(Role role, int port, PeerIdentity local)
    {
        Role = role;
        Port = port;
        LocalIdentity = local;
        RemoteIdentity = null;
        Warnings = Array.Empty<string>();
        Stats = new SessionStats();
        _joined = false;
        _refusal = null;
        _joinStarted = _now(); _handshakeExpired = false;
        _sender.Clear();
        _receiver.Reset();
        GuestState = GuestState.Connected;
        World = new WorldSync(role == Role.Host ? ActorSlot.Host : ActorSlot.Guest, SendReliable, m => Log?.Invoke("[world] " + m));
        WorldCreated?.Invoke(World);
    }

    private void SendReliable(NetDataWriter w)
    {
        if (HasPeer) _transport!.Send(w.CopyData(), SessionDelivery.ReliableOrdered);
    }

    private bool SendBulk(NetDataWriter w) => HasPeer && _transport!.Send(w.CopyData(), SessionDelivery.ReliableOrdered, bulk: true);
    private void EnsureIdle()
    {
        if (Role != Role.None) throw new InvalidOperationException($"Session already running as {Role}.");
    }

    // ---------------- handshake ----------------

    private string? ValidateHello(byte[] data)
    {
        string? error = null;
        try
        {
            var reader = new NetDataReader(data);
            var key = reader.GetString(Protocol.MaxShortString);
            if (!key.StartsWith(Protocol.KeyPrefix, StringComparison.Ordinal)) error = "Not a PCBS2 Coop client.";
            else if (key != Protocol.ConnectionKey) error = $"Different mod versions (host {ModVersion.Value}, you {key[Protocol.KeyPrefix.Length..]}).";
            else
            {
                var guest = PeerIdentity.Read(reader);
                if (reader.AvailableBytes != 0) throw new FormatException("Trailing hello bytes.");
                var result = Compatibility.Check(LocalIdentity!, guest);
                if (!result.Compatible) error = string.Join(" ", result.Problems);
                else { RemoteIdentity = guest; Warnings = result.Warnings; }
            }
        }
        catch (Exception e) when (Protocol.IsReadError(e)) { error = "The join request could not be read."; }
        if (error != null) { Stats.RefusedJoins++; Log?.Invoke("Refused guest: " + error); }
        return error;
    }
    private void OnPeerConnected()
    {
        Log?.Invoke("Peer connected.");
        if (Role != Role.Host) return; // the guest waits for Welcome

        _writer.Reset();
        _writer.Put((byte)MessageType.Welcome);
        LocalIdentity!.Write(_writer);
        var warnings = Warnings.Take(Protocol.MaxWarnings).ToList();
        _writer.Put((byte)warnings.Count);
        foreach (var w in warnings) _writer.Put(w, Protocol.MaxShortString);
        World!.WriteWorldPosition(_writer);
        SendReliable(_writer);

        _joined = true;
        World.Linked = true;
        PeerJoined?.Invoke();
    }

    private void OnWelcome(NetDataReader r)
    {
        if (Role != Role.Client || _joined) { Stats.MalformedMessages++; return; }
        var host = PeerIdentity.Read(r);
        var n = r.GetByte();
        if (n > Protocol.MaxWarnings) throw new FormatException("Too many warnings.");
        var hostWarnings = new List<string>();
        for (var i = 0; i < n; i++) hostWarnings.Add(Protocol.ReadString(r));

        // The guest checks too, so a host that skipped a check can't put us in a mismatched world.
        var mine = Compatibility.Check(host, LocalIdentity!);
        RemoteIdentity = host;
        if (!mine.Compatible)
        {
            _refusal = string.Join(" ", mine.Problems);
            Log?.Invoke($"Leaving: {_refusal} Host identity: {host}");
            _transport?.Disconnect(_refusal);
            return;
        }
        Warnings = hostWarnings.Union(mine.Warnings).ToList();
        foreach (var w in Warnings) Log?.Invoke("Compatibility warning: " + w);

        World!.Linked = true;
        World.ReadWorldPosition(r);
        _joined = true;
        Log?.Invoke($"Joined host: {host}.");
        PeerJoined?.Invoke();
    }

    private void OnPeerDisconnected(string reason)
    {
        _joined = false;
        _sender.Clear();
        _receiver.Reset();
        SetGuestState(GuestState.Connected);
        World?.OnLinkLost();
        RemoteIdentity = null;
        reason = _refusal ?? reason;
        _refusal = null;
        Log?.Invoke("Peer disconnected: " + reason);
        PeerDisconnected?.Invoke(reason);
    }
    // ---------------- messages ----------------

    private void OnReceive(byte[] data, SessionDelivery method)
    {
        var reader = new NetDataReader(data);
        try
        {
            if (reader.AvailableBytes < 1) return;
            var type = (MessageType)reader.GetByte();
            if (type == MessageType.Welcome && method == SessionDelivery.ReliableOrdered) { OnWelcome(reader); return; }
            if (!_joined) { Stats.MalformedMessages++; return; } // nothing else is valid before the handshake

            if (type == MessageType.PlayerState)
            {
                if (method != SessionDelivery.LatestState || reader.AvailableBytes < 17) { Stats.MalformedMessages++; return; }
                var s = PlayerState.Read(reader);
                if (!s.IsValid) { Stats.InvalidPlayerStates++; return; }
                Stats.PlayerStatesReceived++;
                RemoteState?.Invoke(s);
                return;
            }

            if (method != SessionDelivery.ReliableOrdered) { Stats.MalformedMessages++; return; }
            if (type is MessageType.SnapshotMark or MessageType.SnapshotFile or MessageType.SnapshotChunk or MessageType.SnapshotAbort)
            {
                OnSnapshotMessage(type, reader);
                return;
            }
            if (type == MessageType.GuestStatus) { OnGuestStatus(reader); return; }
            if (type == MessageType.Chat) { OnChat(reader); return; }
            if (type == MessageType.Sync) { OnSync(reader); return; }
            if (World == null || !World.Handle(type, reader))
            {
                Stats.UnknownMessages++;
                Log?.Invoke($"Ignored unknown message type {(byte)type}.");
            }
        }
        catch (Exception e) when (Protocol.IsReadError(e))
        {
            Stats.MalformedMessages++;
            Log?.Invoke($"Dropped a malformed message: {e.GetType().Name}.");
        }
    }
}

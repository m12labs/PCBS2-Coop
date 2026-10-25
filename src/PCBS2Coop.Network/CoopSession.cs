using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

public sealed class SessionStats
{
    public long PlayerStatesSent, PlayerStatesReceived, InvalidPlayerStates, UnknownMessages, MalformedMessages, RefusedJoins;
    public override string ToString() =>
        $"states sent {PlayerStatesSent}, received {PlayerStatesReceived}, invalid {InvalidPlayerStates}, unknown msgs {UnknownMessages}, malformed {MalformedMessages}, refused joins {RefusedJoins}";
}

/// <summary>
/// A two-player session over LiteNetLib: one host and one guest.
/// <para>Joining: the guest's connect request carries its <see cref="PeerIdentity"/>. The host refuses an incompatible
/// guest with a readable reason, otherwise accepts and sends Welcome (host identity, warnings, world position).
/// <see cref="PeerJoined"/> fires on both sides once that handshake is complete.</para>
/// <para>World changes go through <see cref="World"/> on one reliable ordered stream; player positions use a
/// separate sequenced stream that may drop stale packets.</para>
/// Not thread-safe; call every member, including <see cref="Poll"/>, from one thread. Events are raised from inside <see cref="Poll"/>.
/// </summary>
public sealed class CoopSession : IDisposable
{
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly NetDataWriter _writer = new();
    private NetPeer? _peer;
    private bool _joined;
    private string? _refusal;

    public Role Role { get; private set; }
    public int Port { get; private set; }
    public bool IsRunning => Role != Role.None;
    /// <summary>Transport connection exists (the handshake may still be in progress).</summary>
    public bool HasPeer => _peer != null && _peer.ConnectionState == ConnectionState.Connected;
    /// <summary>Handshake completed: identities exchanged and compatible.</summary>
    public bool IsJoined => HasPeer && _joined;
    public int PingMs => _peer?.Ping ?? -1;

    public PeerIdentity? LocalIdentity { get; private set; }
    public PeerIdentity? RemoteIdentity { get; private set; }
    /// <summary>Warnings from the last successful join (DLC/catalog differences…). Empty when none.</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = Array.Empty<string>();
    /// <summary>World change pipeline for the running session; null when stopped.</summary>
    public WorldSync? World { get; private set; }
    public SessionStats Stats { get; private set; } = new();

    /// <summary>Raised on both sides once the handshake completes.</summary>
    public event Action? PeerJoined;
    /// <summary>Raised when the peer leaves, a join is refused, or a connection attempt fails. The string is a readable reason.</summary>
    public event Action<string>? PeerDisconnected;
    public event Action<PlayerState>? RemoteState;
    public event Action<string>? Log;
    /// <summary>Raised when a new <see cref="World"/> is created (on Host/Join) so handlers can be registered.</summary>
    public event Action<WorldSync>? WorldCreated;

    public CoopSession()
    {
        _net = new NetManager(_listener) { DisconnectTimeout = 5000, UnconnectedMessagesEnabled = false };
        _listener.ConnectionRequestEvent += OnConnectionRequest;
        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.PeerDisconnectedEvent += OnPeerDisconnected;
        _listener.NetworkReceiveEvent += OnReceive;
        _listener.NetworkErrorEvent += (ep, err) => Log?.Invoke($"Network error from {ep}: {err}");
    }

    /// <summary>Start listening for one guest. Returns false with a reason if the port can't be bound.</summary>
    public bool Host(int port, PeerIdentity local, out string error)
    {
        EnsureIdle();
        if (!_net.Start(port))
        {
            error = $"Could not listen on UDP port {port} (already in use?).";
            return false;
        }
        Begin(Role.Host, port, local);
        error = "";
        Log?.Invoke($"Hosting on UDP port {port} as {local}.");
        return true;
    }

    /// <summary>Begin connecting to a host. The outcome arrives as <see cref="PeerJoined"/> or <see cref="PeerDisconnected"/>.</summary>
    public bool Join(string address, int port, PeerIdentity local, out string error)
    {
        EnsureIdle();
        if (!IPAddress.TryParse(address.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            error = $"'{address}' is not an IPv4 address.";
            return false;
        }
        if (!_net.Start())
        {
            error = "Could not open a local UDP socket.";
            return false;
        }
        Begin(Role.Client, port, local);
        var hello = new NetDataWriter();
        hello.Put(Protocol.ConnectionKey);
        local.Write(hello);
        _peer = _net.Connect(new IPEndPoint(ip, port), hello);
        error = "";
        Log?.Invoke($"Connecting to {ip}:{port} as {local}.");
        return true;
    }

    public void Poll() => _net.PollEvents();

    public void SendState(in PlayerState state)
    {
        if (!IsJoined) return;
        _writer.Reset();
        state.Write(_writer);
        _peer!.Send(_writer, DeliveryMethod.Sequenced);
        Stats.PlayerStatesSent++;
    }

    public void Stop()
    {
        if (Role == Role.None) return;
        _net.Stop(sendDisconnectMessages: true);
        _peer = null;
        _joined = false;
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
        World = new WorldSync(role == Role.Host ? ActorSlot.Host : ActorSlot.Guest, SendReliable, m => Log?.Invoke("[world] " + m));
        WorldCreated?.Invoke(World);
    }

    private void SendReliable(NetDataWriter w)
    {
        if (_peer == null || _peer.ConnectionState != ConnectionState.Connected) return;
        _peer.Send(w, DeliveryMethod.ReliableOrdered);
    }

    private void EnsureIdle()
    {
        if (Role != Role.None) throw new InvalidOperationException($"Session already running as {Role}.");
    }

    // ---------------- handshake ----------------

    private void OnConnectionRequest(ConnectionRequest request)
    {
        if (Role != Role.Host || _peer != null)
        {
            Log?.Invoke($"Rejected {request.RemoteEndPoint}: session is full.");
            request.Reject(ReasonBytes("The host already has a player connected (two-player session)."));
            return;
        }

        string key;
        PeerIdentity guest;
        try
        {
            key = request.Data.GetString(Protocol.MaxShortString);
            if (!key.StartsWith(Protocol.KeyPrefix, StringComparison.Ordinal))
            {
                Log?.Invoke($"Rejected {request.RemoteEndPoint}: not a PCBS2 Coop client.");
                request.RejectForce();
                return;
            }
            if (key != Protocol.ConnectionKey)
            {
                Refuse(request, $"Different mod versions (host {ModVersion.Value}, you {key[Protocol.KeyPrefix.Length..]}).");
                return;
            }
            guest = PeerIdentity.Read(request.Data);
        }
        catch (Exception e) when (Protocol.IsReadError(e))
        {
            Refuse(request, "The join request could not be read.");
            return;
        }

        var result = Compatibility.Check(LocalIdentity!, guest);
        if (!result.Compatible)
        {
            Refuse(request, string.Join(" ", result.Problems));
            Log?.Invoke($"Guest identity: {guest}");
            return;
        }

        _peer = request.Accept();
        RemoteIdentity = guest;
        Warnings = result.Warnings;
        Log?.Invoke($"Accepted {request.RemoteEndPoint}: {guest}.");
        foreach (var w in result.Warnings) Log?.Invoke("Compatibility warning: " + w);
    }

    private void Refuse(ConnectionRequest request, string reason)
    {
        Stats.RefusedJoins++;
        Log?.Invoke($"Refused {request.RemoteEndPoint}: {reason}");
        request.Reject(ReasonBytes(reason));
    }

    private static byte[] ReasonBytes(string reason)
    {
        var w = new NetDataWriter();
        w.Put(reason, Protocol.MaxShortString);
        return w.CopyData();
    }

    private void OnPeerConnected(NetPeer peer)
    {
        _peer = peer;
        Log?.Invoke($"Peer connected: {peer}.");
        if (Role != Role.Host) return; // the guest waits for Welcome

        _writer.Reset();
        _writer.Put((byte)MessageType.Welcome);
        LocalIdentity!.Write(_writer);
        var warnings = Warnings.Take(Protocol.MaxWarnings).ToList();
        _writer.Put((byte)warnings.Count);
        foreach (var w in warnings) _writer.Put(w, Protocol.MaxShortString);
        World!.WriteWorldPosition(_writer);
        peer.Send(_writer, DeliveryMethod.ReliableOrdered);

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
            _net.DisconnectPeer(_peer!);
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

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (_peer != null && _peer != peer) return;
        _peer = null;
        var wasJoined = _joined;
        _joined = false;
        World?.OnLinkLost();

        string? hostReason = null;
        if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0)
        {
            try { hostReason = info.AdditionalData.GetString(Protocol.MaxShortString); }
            catch (Exception e) when (Protocol.IsReadError(e)) { }
        }

        var reason = _refusal ?? (info.Reason switch
        {
            DisconnectReason.ConnectionFailed => "No host answered at that address and port.",
            DisconnectReason.ConnectionRejected => hostReason != null ? "The host refused the join: " + hostReason : "The host refused the join (full, or a different mod version).",
            DisconnectReason.Timeout => "Connection timed out.",
            DisconnectReason.RemoteConnectionClose => hostReason ?? "The other player left.",
            DisconnectReason.DisconnectPeerCalled => "Disconnected.",
            _ => info.Reason.ToString(),
        });
        _refusal = null;
        Log?.Invoke($"Peer disconnected: {info.Reason} (joined={wasJoined}). {reason}");
        PeerDisconnected?.Invoke(reason);
    }

    // ---------------- messages ----------------

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        try
        {
            if (peer != _peer || reader.AvailableBytes < 1) return;
            var type = (MessageType)reader.GetByte();
            if (type == MessageType.Welcome) { OnWelcome(reader); return; }
            if (!_joined) { Stats.MalformedMessages++; return; } // nothing else is valid before the handshake

            if (type == MessageType.PlayerState)
            {
                if (method != DeliveryMethod.Sequenced || reader.AvailableBytes < 17) { Stats.MalformedMessages++; return; }
                var s = PlayerState.Read(reader);
                if (!s.IsValid) { Stats.InvalidPlayerStates++; return; }
                Stats.PlayerStatesReceived++;
                RemoteState?.Invoke(s);
                return;
            }

            if (method != DeliveryMethod.ReliableOrdered) { Stats.MalformedMessages++; return; }
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
        finally
        {
            reader.Recycle();
        }
    }
}

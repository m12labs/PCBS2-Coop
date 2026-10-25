using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>
/// A two-player session over LiteNetLib: one host and one client.
/// Not thread-safe; call every member, including <see cref="Poll"/>, from one thread.
/// Events are raised from inside <see cref="Poll"/>.
/// </summary>
public sealed class CoopSession : IDisposable
{
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly NetDataWriter _writer = new();
    private NetPeer? _peer;

    public Role Role { get; private set; }
    public int Port { get; private set; }
    public bool IsRunning => Role != Role.None;
    public bool HasPeer => _peer != null && _peer.ConnectionState == ConnectionState.Connected;
    public int PingMs => _peer?.Ping ?? -1;

    public event Action? PeerConnected;
    /// <summary>Raised when the peer leaves or a client connection attempt fails. The string is a readable reason.</summary>
    public event Action<string>? PeerDisconnected;
    public event Action<PlayerState>? RemoteState;
    public event Action<string>? Log;

    public CoopSession()
    {
        _net = new NetManager(_listener) { DisconnectTimeout = 5000, UnconnectedMessagesEnabled = false };
        _listener.ConnectionRequestEvent += OnConnectionRequest;
        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.PeerDisconnectedEvent += OnPeerDisconnected;
        _listener.NetworkReceiveEvent += OnReceive;
        _listener.NetworkErrorEvent += (ep, err) => Log?.Invoke($"Network error from {ep}: {err}");
    }

    /// <summary>Start listening for one client. Returns false with a reason if the port can't be bound.</summary>
    public bool Host(int port, out string error)
    {
        EnsureIdle();
        if (!_net.Start(port))
        {
            error = $"Could not listen on UDP port {port} (already in use?).";
            return false;
        }
        Role = Role.Host;
        Port = port;
        error = "";
        Log?.Invoke($"Hosting on UDP port {port}.");
        return true;
    }

    /// <summary>Begin connecting to a host. The outcome arrives as <see cref="PeerConnected"/> or <see cref="PeerDisconnected"/>.</summary>
    public bool Join(string address, int port, out string error)
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
        Role = Role.Client;
        Port = port;
        _peer = _net.Connect(new IPEndPoint(ip, port), Protocol.ConnectionKey);
        error = "";
        Log?.Invoke($"Connecting to {ip}:{port}.");
        return true;
    }

    public void Poll() => _net.PollEvents();

    public void SendState(in PlayerState state)
    {
        if (!HasPeer) return;
        _writer.Reset();
        state.Write(_writer);
        _peer!.Send(_writer, DeliveryMethod.Sequenced);
    }

    public void Stop()
    {
        if (Role == Role.None) return;
        _net.Stop(sendDisconnectMessages: true);
        _peer = null;
        Log?.Invoke($"Session stopped ({Role}).");
        Role = Role.None;
        Port = 0;
    }

    public void Dispose() => Stop();

    private void EnsureIdle()
    {
        if (Role != Role.None) throw new InvalidOperationException($"Session already running as {Role}.");
    }

    private void OnConnectionRequest(ConnectionRequest request)
    {
        if (Role != Role.Host || _peer != null)
        {
            Log?.Invoke($"Rejected {request.RemoteEndPoint}: session is full.");
            request.Reject();
            return;
        }
        var peer = request.AcceptIfKey(Protocol.ConnectionKey);
        if (peer == null) Log?.Invoke($"Rejected {request.RemoteEndPoint}: wrong mod version or key.");
        else _peer = peer;
    }

    private void OnPeerConnected(NetPeer peer)
    {
        _peer = peer;
        Log?.Invoke($"Peer connected: {peer}.");
        PeerConnected?.Invoke();
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (_peer != null && _peer != peer) return;
        _peer = null;
        var reason = info.Reason switch
        {
            DisconnectReason.ConnectionFailed => "No host answered at that address and port.",
            DisconnectReason.ConnectionRejected => "The host rejected the connection (full, or a different mod version).",
            DisconnectReason.Timeout => "Connection timed out.",
            DisconnectReason.RemoteConnectionClose => "The other player left.",
            DisconnectReason.DisconnectPeerCalled => "Disconnected.",
            _ => info.Reason.ToString(),
        };
        Log?.Invoke($"Peer disconnected: {info.Reason}.");
        PeerDisconnected?.Invoke(reason);
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        try
        {
            if (reader.AvailableBytes < 1) return;
            switch ((MessageType)reader.GetByte())
            {
                case MessageType.PlayerState:
                    RemoteState?.Invoke(PlayerState.Read(reader));
                    break;
            }
        }
        finally
        {
            reader.Recycle();
        }
    }
}

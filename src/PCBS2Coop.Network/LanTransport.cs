using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>Direct IPv4 UDP. Keeps identity refusal in LiteNetLib's preconnection handshake.</summary>
public sealed class LanTransport : ISessionTransport
{
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly string? _address;
    private readonly int _port;
    private NetPeer? _peer;
    private Role _role;
    public string Name => "LAN";
    public bool IsConnected => _peer?.ConnectionState == ConnectionState.Connected;
    public int PingMs => _peer?.Ping ?? -1;
    public Func<byte[], string?>? ValidateHello { get; set; }
    public event Action? Connected;
    public event Action<string>? Disconnected;
    public event Action<byte[], SessionDelivery>? Received;
    public event Action<string>? Log;

    public LanTransport(int port, string? address = null)
    {
        _port = port; _address = address;
        _net = new NetManager(_listener) { DisconnectTimeout = 5000, UnconnectedMessagesEnabled = false };
        _listener.ConnectionRequestEvent += Request;
        _listener.PeerConnectedEvent += peer => { _peer = peer; Connected?.Invoke(); };
        _listener.NetworkErrorEvent += (ep, error) => Log?.Invoke($"Network error from {ep}: {error}");
        _listener.NetworkReceiveEvent += (peer, reader, _, method) =>
        {
            try
            {
                if (peer == _peer && method is DeliveryMethod.ReliableOrdered or DeliveryMethod.Sequenced)
                    Received?.Invoke(reader.GetRemainingBytes(), method == DeliveryMethod.Sequenced ? SessionDelivery.LatestState : SessionDelivery.ReliableOrdered);
            }
            finally { reader.Recycle(); }
        };
        _listener.PeerDisconnectedEvent += Dropped;
    }

    public bool Start(Role role, byte[] hello, out string error)
    {
        error = "";
        IPAddress? ip = null;
        if (_port < 1 || _port > 65535) { error = "Port must be from 1 to 65535."; return false; }
        if (role == Role.Client && (!IPAddress.TryParse(_address?.Trim(), out ip) || ip.AddressFamily != AddressFamily.InterNetwork))
        { error = $"'{_address}' is not an IPv4 address."; return false; }
        if (!_net.Start(role == Role.Host ? _port : 0))
        { error = role == Role.Host ? $"Could not listen on UDP port {_port} (already in use?)." : "Could not open a local UDP socket."; return false; }
        _role = role;
        if (role == Role.Client)
        {
            var writer = new NetDataWriter(); writer.Put(hello);
            _peer = _net.Connect(new IPEndPoint(ip!, _port), writer);
        }
        return true;
    }

    private void Request(ConnectionRequest request)
    {
        if (_role != Role.Host || _peer != null)
        { request.Reject(ReasonBytes("The host already has a player connected (two-player session).")); return; }
        var reason = ValidateHello?.Invoke(request.Data.GetRemainingBytes()) ?? (ValidateHello == null ? "Session is not accepting guests." : "");
        if (reason.Length != 0) { request.Reject(ReasonBytes(reason)); return; }
        _peer = request.Accept();
    }

    private void Dropped(NetPeer peer, DisconnectInfo info)
    {
        if (_peer != null && _peer != peer) return;
        _peer = null;
        string? detail = null;
        try { if (info.AdditionalData?.AvailableBytes > 0) detail = info.AdditionalData.GetString(Protocol.MaxShortString); }
        catch (Exception e) when (Protocol.IsReadError(e)) { }
        var reason = info.Reason switch
        {
            DisconnectReason.ConnectionFailed => "No host answered at that address and port.",
            DisconnectReason.ConnectionRejected => detail != null ? "The host refused the join: " + detail : "The host refused the join (full, or a different mod version).",
            DisconnectReason.Timeout => "Connection timed out.",
            DisconnectReason.RemoteConnectionClose => detail ?? "The other player left.",
            DisconnectReason.DisconnectPeerCalled => "Disconnected.",
            _ => info.Reason.ToString()
        };
        Disconnected?.Invoke(reason);
    }

    private static byte[] ReasonBytes(string reason)
    { var w = new NetDataWriter(); w.Put(reason, Protocol.MaxShortString); return w.CopyData(); }
    public void Poll() => _net.PollEvents();
    public bool Send(byte[] payload, SessionDelivery delivery, bool bulk = false)
    {
        if (!IsConnected) return false;
        _peer!.Send(payload, delivery == SessionDelivery.LatestState ? DeliveryMethod.Sequenced : DeliveryMethod.ReliableOrdered);
        return true;
    }
    public void Disconnect(string reason) { if (_peer != null) _net.DisconnectPeer(_peer, ReasonBytes(reason)); }
    public void Dispose() { _net.Stop(sendDisconnectMessages: true); _peer = null; }
}

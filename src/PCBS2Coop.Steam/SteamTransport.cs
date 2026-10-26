using System.Runtime.InteropServices;
using LiteNetLib.Utils;
using PCBS2Coop.Network;
using Steamworks;
using Steamworks.Data;

namespace PCBS2Coop.Steam;

/// <summary>Steam-authenticated single-peer link. Application admission still belongs to CoopSession.</summary>
internal sealed class SteamTransport : ISessionTransport, ISocketManager, IConnectionManager
{
    private readonly SteamService _service;
    private readonly Role _role;
    private readonly SteamId _host;
    private readonly string _session;
    private SocketManager? _listener;
    private ConnectionManager? _client;
    private Connection _connection;
    private SteamId _remote;
    private PacketStream _stream = new();
    private byte[]? _hello;
    private bool _nativeConnected, _disposed, _started, _remoteClosed;
    private string? _failure, _nativeEndReason;
    private long _connectingAt, _nextStats, _nativeStalledAt;
    public string Name => "Steam";
    public bool IsConnected { get; private set; }
    public int PingMs => IsConnected ? _connection.QuickStatus().Ping : -1;
    public bool Reserved => _connection.Id != 0;
    public Func<byte[], string?>? ValidateHello { get; set; }
    public event Action? Connected;
    public event Action<string>? Disconnected;
    public event Action<byte[], SessionDelivery>? Received;
    public event Action<string>? Log;

    public SteamTransport(SteamService service, Role role, SteamId host, string session)
    { _service = service; _role = role; _host = host; _session = session; }

    public void PrepareHost()
    {
        _listener = SteamNetworkingSockets.CreateRelaySocket(0, this);
        if ((uint)_listener.Socket == 0) throw new InvalidOperationException("Steam could not create the P2P listener.");
    }

    public bool Start(Role role, byte[] hello, out string error)
    {
        error = "";
        if (_disposed || _started || role != _role) { error = "Steam transport is no longer available."; return false; }
        _started = true; _hello = (byte[])hello.Clone();
        if (hello.Length > PacketStream.MaxFrameBytes - 128) { error = "Steam hello is too large."; return false; }
        _connectingAt = Environment.TickCount64;
        if (_role == Role.Client)
        {
            _client = SteamNetworkingSockets.ConnectRelay(_host, 0, this);
            _connection = _client.Connection;
            if (_connection.Id == 0) { error = "Steam could not start the P2P connection."; return false; }
        }
        return true;
    }

    public void Poll()
    {
        if (_disposed || !_started) return;
        _listener?.Receive(64, receiveToEnd: false);
        _client?.Receive(64, receiveToEnd: false);
        if (_disposed) return;
        _failure ??= _nativeEndReason;
        if (_nativeConnected)
        {
            if (_hello != null && _role == Role.Client)
            {
                var writer = new NetDataWriter(); writer.Put((byte)1);
                writer.Put((ulong)_service.LobbyId); writer.Put(_session, 64); writer.Put(_hello);
                var result = SendNative(writer.CopyData(), reliable: true);
                if (result == FrameSendResult.Accepted) _hello = null;
                else if (result == FrameSendResult.Failed) _failure = "Steam could not send the compatibility hello.";
            }
            if (IsConnected && _hello == null) _stream.Pump(frame => SendNative(frame, reliable: true));
            if (Environment.TickCount64 >= _nextStats)
            {
                _nextStats = Environment.TickCount64 + 15_000;
                var status = _connection.QuickStatus();
                Log?.Invoke($"Steam route: ping={status.Ping}ms, native pending={status.PendingReliable}, managed={_stream.QueuedBytes}, high-water={_stream.QueueHighWater}. {_connection.DetailedStatus()}");
            }
        }
        if (!IsConnected && Reserved && Environment.TickCount64 - _connectingAt > 30_000)
            _failure ??= "Steam connection/hello timed out after 30 seconds.";
        _stream.CheckTimeout();
        _failure ??= _stream.Failure;
        if (_nativeStalledAt != 0 && Environment.TickCount64 - _nativeStalledAt > 60_000)
            _failure ??= "Steam native send queue stalled for 60 seconds.";
        if (_failure != null) Drop(_failure);
    }

    public bool Send(byte[] payload, SessionDelivery delivery, bool bulk = false)
    {
        if (!IsConnected || _disposed || _failure != null) return false;
        if (delivery == SessionDelivery.LatestState)
            return SendNative(_stream.Latest(payload), reliable: false) == FrameSendResult.Accepted;
        return _stream.Enqueue(payload, bulk);
    }

    private FrameSendResult SendNative(byte[] frame, bool reliable)
    {
        var result = _connection.SendMessage(frame, reliable ? SendType.Reliable | SendType.NoNagle : SendType.Unreliable | SendType.NoDelay | SendType.NoNagle);
        if (result == Result.OK) { if (reliable) _nativeStalledAt = 0; return FrameSendResult.Accepted; }
        if (result is Result.LimitExceeded or Result.Ignored)
        {
            if (reliable && _nativeStalledAt == 0) _nativeStalledAt = Environment.TickCount64;
            return FrameSendResult.Retry;
        }
        _failure ??= $"Steam send failed: {result}.";
        return FrameSendResult.Failed;
    }

    public void OnConnecting(Connection connection, ConnectionInfo info)
    {
        var remote = info.Identity.SteamId;
        if (_disposed || !_started || Reserved || !info.Identity.IsSteamId || !_service.AcceptMember(remote))
        { connection.Close(false, 1001, "PCBS2 Coop lobby is not ready or is full."); return; }
        _connection = connection; _remote = remote; _connectingAt = Environment.TickCount64;
        if (connection.Accept() != Result.OK) _failure = "Steam could not accept the guest connection.";
    }
    public void OnConnected(Connection connection, ConnectionInfo info)
    {
        if (connection != _connection || _disposed) { connection.Close(); return; }
        _nativeConnected = true;
        _service.ReserveGuest();
    }
    public void OnConnecting(ConnectionInfo info) { }
    public void OnConnected(ConnectionInfo info)
    {
        if (_disposed || !info.Identity.IsSteamId || info.Identity.SteamId != _host)
        { _failure = "Steam connected to an unexpected host."; return; }
        _remote = _host; _nativeConnected = true; IsConnected = true;
        Connected?.Invoke(); // guest still waits for Welcome; hello is pumped first
    }
    public void OnDisconnected(Connection connection, ConnectionInfo info)
    {
        if (connection == _connection && !_disposed) _nativeEndReason ??= $"Steam connection closed ({info.EndReason}).";
        else connection.Close();
    }
    public void OnDisconnected(ConnectionInfo info) => OnDisconnected(_connection, info);
    public void OnMessage(Connection connection, NetIdentity identity, IntPtr data, int size, long messageNum, long recvTime, int channel)
    {
        if (connection == _connection && identity.IsSteamId && identity.SteamId == _remote) CopyMessage(data, size, channel);
    }
    public void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel) => CopyMessage(data, size, channel);

    private void CopyMessage(IntPtr data, int size, int channel)
    {
        if (_disposed || _failure != null) return;
        if (size < 1 || size > PacketStream.MaxFrameBytes || channel != 0) { _failure = "Invalid Steam message size/lane."; return; }
        var frame = new byte[size]; Marshal.Copy(data, frame, 0, size);
        if (frame[0] == 4)
        {
            try { var r = new NetDataReader(frame); r.GetByte(); _failure = r.GetString(256); _remoteClosed = true; }
            catch (Exception e) when (Protocol.IsReadError(e)) { _failure = "Invalid Steam close reason."; }
            return;
        }
        if (!IsConnected && _role == Role.Host)
        {
            try
            {
                var reader = new NetDataReader(frame);
                if (reader.GetByte() != 1 || reader.GetULong() != (ulong)_service.LobbyId || reader.GetString(64) != _session || !_service.IsMember(_remote))
                    throw new FormatException("Steam lobby/session mismatch.");
                var reason = ValidateHello?.Invoke(reader.GetRemainingBytes());
                if (ValidateHello == null || reason != null) { _failure = reason ?? "Steam session is no longer accepting guests."; return; }
                IsConnected = true; _hello = null; Connected?.Invoke();
            }
            catch (Exception e) when (Protocol.IsReadError(e)) { _failure = "Invalid Steam hello: " + e.Message; }
            return;
        }
        if (frame[0] == 1) { _failure = "Unexpected duplicate Steam hello."; return; }
        var payload = _stream.Receive(frame, out var delivery);
        if (payload != null) Received?.Invoke(payload, delivery);
    }

    public void Disconnect(string reason) => _failure ??= reason;
    private void Drop(string reason)
    {
        CloseNative(reason);
        _connection = default; _nativeConnected = false; IsConnected = false; _hello = null;
        _stream = new PacketStream(); _failure = null; _nativeStalledAt = 0;
        _nativeEndReason = null; _remoteClosed = false;
        _service.GuestDropped();
        Disconnected?.Invoke(reason);
    }
    private void CloseNative(string reason)
    {
        if (_connection.Id == 0) return;
        if (_nativeConnected && !_remoteClosed)
        {
            var writer = new NetDataWriter(); writer.Put((byte)4); writer.Put(reason.Length > 240 ? reason[..240] : reason, 256);
            _connection.SendMessage(writer.CopyData(), SendType.Reliable | SendType.NoNagle);
        }
        _connection.Close(!_remoteClosed, 1000, reason.Length > 100 ? reason[..100] : reason);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseNative(_failure ?? "The other player left.");
        _connection = default; IsConnected = false; _nativeConnected = false; _stream = new PacketStream();
        if (_listener != null) { _listener.Interface = null; var socket = _listener.Socket; socket.Manager = null; _listener.Close(); _listener = null; }
        if (_client != null) { _client.Interface = null; _client = null; }
    }
}

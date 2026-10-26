using System.Buffers.Binary;
using LiteNetLib.Utils;
using PCBS2Coop.Network;

internal static class TransportTests
{
    public static void Run(Checker t)
    {
        t.Section("Framed transport: bounded reliable delivery and native backpressure");
        var source = new byte[Protocol.MaxModStateBytes + 64]; new Random(91).NextBytes(source);
        var original = (byte[])source.Clone();
        var outgoing = new PacketStream(); var incoming = new PacketStream();
        t.Check(outgoing.Enqueue(source), "oversized legal snapshot mark is queued");
        Array.Fill(source, (byte)0);
        byte[]? received = null; byte[]? blockedFrame = null;
        var frames = 0; var retried = false; var frameLimit = true;
        outgoing.Pump(frame =>
        {
            if (frames == 3 && !retried) { blockedFrame = frame; return FrameSendResult.Retry; }
            frameLimit &= frame.Length <= PacketStream.MaxFrameBytes;
            received = incoming.Receive(frame, out _) ?? received; frames++;
            return FrameSendResult.Accepted;
        });
        t.Check(received == null && outgoing.Pending && outgoing.Failure == null, "queue-full pauses partway through a logical message");
        outgoing.Pump(frame =>
        {
            if (!retried) { retried = true; t.Check(frame.SequenceEqual(blockedFrame!), "retry sends the exact unsent fragment"); }
            frameLimit &= frame.Length <= PacketStream.MaxFrameBytes;
            received = incoming.Receive(frame, out _) ?? received;
            return FrameSendResult.Accepted;
        });
        t.Check(received != null && received.SequenceEqual(original) && incoming.Failure == null, "large message arrives intact despite writer reuse and native saturation");
        t.Check(frameLimit && !outgoing.Pending && outgoing.QueuedBytes == 0, "all frames bounded to 64 KiB and queue drains");
        var bounds = new PacketStream();
        t.Check(bounds.Enqueue(new byte[PacketStream.BulkLimit], true) && !bounds.Enqueue(new byte[1], true) && bounds.Failure == null,
            "bulk production stops at 2 MiB without failing the connection");
        t.Check(bounds.Enqueue(new byte[PacketStream.MaxMessageBytes]) && !bounds.Enqueue(new byte[1]) && bounds.Failure != null,
            "control traffic has reserved capacity; overflow fails explicitly");

        t.Section("Framed transport: hostile fragments, ordering, latest-state and stalls");
        var max = new PacketStream(); var maxReceiver = new PacketStream(); byte[]? maxResult = null;
        max.Enqueue(new byte[PacketStream.MaxMessageBytes]);
        max.Pump(f => { maxResult = maxReceiver.Receive(f, out _) ?? maxResult; return FrameSendResult.Accepted; });
        t.Check(maxResult?.Length == PacketStream.MaxMessageBytes, "2 MiB logical boundary reassembles");
        t.Check(!new PacketStream().Enqueue(new byte[PacketStream.MaxMessageBytes + 1]), "oversized logical send is refused");
        foreach (var bad in new[] { Fragment(1, -1, 0), Fragment(1, PacketStream.MaxMessageBytes + 1, 0), Fragment(1, 10, 1), Fragment(2, 1, 0), new byte[] { 9 } })
        {
            var receiver = new PacketStream();
            t.Check(receiver.Receive(bad, out _) == null && receiver.Failure != null, "invalid length/offset/id/kind rejected before allocation");
        }
        var inconsistent = new PacketStream();
        inconsistent.Receive(Fragment(1, 4, 0), out _);
        inconsistent.Receive(Fragment(1, 5, 1), out _);
        t.Check(inconsistent.Failure != null, "fragment total cannot change mid-message");
        var duplicate = new PacketStream(); var completeFrame = Fragment(1, 1, 0);
        duplicate.Receive(completeFrame, out _); duplicate.Receive(completeFrame, out _);
        t.Check(duplicate.Failure != null, "duplicate reliable message rejected");
        var states = new PacketStream(); var remote = new PacketStream();
        var old = states.Latest(new byte[] { 1 }); var fresh = states.Latest(new byte[] { 2 });
        t.Check(remote.Receive(fresh, out var delivery)![0] == 2 && delivery == SessionDelivery.LatestState && remote.Receive(old, out _) == null && remote.Receive(fresh, out _) == null,
            "reordered and duplicate movement cannot move the peer backward");
        t.Check(new PacketStream().Receive(old, out _)![0] == 1, "fresh connection resets latest-state sequence");
        long time = 0;
        var stalledSend = new PacketStream(() => time); stalledSend.Enqueue(new byte[] { 1 });
        stalledSend.Pump(_ => FrameSendResult.Retry); time = 60_001; stalledSend.CheckTimeout();
        t.Check(stalledSend.Failure != null, "native saturation times out after 60 seconds without progress");
        time = 0; var stalledReceive = new PacketStream(() => time); stalledReceive.Receive(Fragment(1, 4, 0), out _);
        time = 60_001; stalledReceive.CheckTimeout();
        t.Check(stalledReceive.Failure != null, "incomplete reassembly times out without leaking a growing buffer");

        t.Section("Snapshot transfer: rejected bulk sends retain offsets and header");
        var packets = new List<byte[]>(); var canSend = false;
        var sender = new SnapshotSender(w => packets.Add(w.CopyData()), w => { if (!canSend) return false; packets.Add(w.CopyData()); return true; });
        var id = sender.Mark(SnapshotPurpose.Copy, null);
        sender.SetFile(id, "coopworld.binary.zip", new byte[Protocol.SnapshotChunkBytes + 1]);
        sender.Pump(int.MaxValue);
        t.Check(packets.Count == 1 && sender.Queued == 1, "rejected bulk header leaves transfer queued after its ordered mark");
        canSend = true; sender.Pump(int.MaxValue);
        var snapshot = new SnapshotReceiver(); Snapshot? got = null;
        foreach (var packet in packets)
        {
            var r = new NetDataReader(packet); var type = (MessageType)r.GetByte();
            if (type == MessageType.SnapshotMark) snapshot.OnMark(r, out _, out _);
            else if (type == MessageType.SnapshotFile) snapshot.OnFile(r);
            else if (type == MessageType.SnapshotChunk) snapshot.OnChunk(r, out got);
        }
        t.Check(sender.Queued == 0 && got?.File.Length == Protocol.SnapshotChunkBytes + 1, "retry emits one header and contiguous chunks, producing a verified snapshot");
        FramedAdmission(t);
        var native = new byte[] { 1, 2, 3 };
        var metadata = CoopSavePair.Create(native, new SnapshotPosition(1, 2, 3, new byte[] { 4 }), new[] { new CarrierPayload(new NetObjectId(8), new byte[] { 5, 6 }) });
        var current = CoopSavePair.Encode(metadata);
        byte[] Version(string version)
        {
            var r = new NetDataReader(current); var w = new NetDataWriter();
            w.Put(r.GetULong()); r.GetString(); w.Put(version); w.Put(r.GetRemainingBytes()); return w.CopyData();
        }
        foreach (var older in new[] { "0.8", "0.9" })
        {
            var previous = CoopSavePair.Decode(Version(older)); CoopSavePair.Verify(native, previous);
            t.Check(previous.Position == metadata.Position with { State = previous.Position.State } && previous.Position.State.SequenceEqual(metadata.Position.State) && previous.Carriers[0].Pc.Value == 8,
                $"{older} paired save retains its hashed native bytes, world position and carried PC identity in {ModVersion.Value}");
        }
        bool Rejects(string version) { try { CoopSavePair.Decode(Version(version)); return false; } catch (InvalidDataException) { return true; } }
        t.Check(Rejects("0.7") && Rejects("9.9"), "unknown and older incompatible paired saves still fail explicitly");
        long joinTime = 0;
        using var waiting = new CoopSession(() => joinTime);
        var noWelcome = new WaitingTransport(); var drops = 0; string? reason = null;
        waiting.PeerDisconnected += why => { reason = why; drops++; };
        waiting.Join(noWelcome, Ids.Make(), out _); waiting.Poll();
        t.Check(waiting.Stats.MalformedMessages == 1 && !waiting.IsJoined, "malformed Welcome does not complete a compatibility handshake");
        joinTime = 30_001; waiting.Poll(); waiting.Poll(); waiting.Poll();
        t.Check(drops == 1 && reason!.Contains("handshake timed out") && !waiting.HasPeer, "uncompleted handshake times out once even after receiving malformed Welcome");
    }

    private static byte[] Fragment(ulong id, int length, int offset)
    {
        var f = new byte[18]; f[0] = 2;
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(1), id);
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(9), length);
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(13), offset);
        return f;
    }

    private static void FramedAdmission(Checker t)
    {
        t.Section("Framed transport: shared session handshake, late join and held changes");
        using var host = new CoopSession(); using var guest = new CoopSession();
        var hostAdapter = new FramedLink(); var guestAdapter = new FramedLink(); hostAdapter.Peer = guestAdapter; guestAdapter.Peer = hostAdapter;
        var hostMarkers = new MarkerHandler(); var guestMarkers = new MarkerHandler();
        host.WorldCreated += w => { w.Register(CommandTypes.DiagnosticMarker, hostMarkers); w.RegisterState(hostMarkers); w.RegisterState(new LargeState()); };
        guest.WorldCreated += w => { w.Register(CommandTypes.DiagnosticMarker, guestMarkers); w.RegisterState(guestMarkers); w.RegisterState(new LargeState()); };
        host.Host(hostAdapter, Ids.Make(), out _); host.World!.BeginGeneration("framed");
        host.World.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker("before").ToBytes(), out _);
        guest.Join(guestAdapter, Ids.Make(), out _);
        t.Check(Checker.PumpUntil(() => host.IsJoined && guest.IsJoined, 1000, host, guest), "non-LAN adapter uses shared identity checks and Welcome");
        var id = host.BeginSnapshot(SnapshotPurpose.Load);
        var data = new byte[3_000_000]; new Random(12).NextBytes(data);
        host.SetSnapshotFile(id, "coopworld.binary.zip", data);
        host.World.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker("held").ToBytes(), out _);
        Snapshot? received = null; guest.SnapshotReceived += snapshot => received = snapshot;
        hostAdapter.RetryPolls = 3;
        t.Check(Checker.PumpUntil(() => received != null, 5000, host, guest) && received!.File.SequenceEqual(data) && guest.World!.Holding,
            "large mod-state mark and 3 MB snapshot survive native backpressure with mutation hold intact");
        t.Check(guest.CompleteAdmission(out _) && guestMarkers.Texts.SequenceEqual(new[] { "before", "held" }) && guest.World!.ChainHash == host.World.ChainHash,
            "admission restores state then replays held changes exactly once");
        guest.Stop("bye");
        t.Check(Checker.PumpUntil(() => !host.IsJoined, 1000, host) && host.IsRunning && host.World.Admitted, "guest departure preserves host world through adapter");
    }

    private sealed class LargeState : IWorldState
    {
        public string Name => "large";
        public void Reset() { }
        public void Write(NetDataWriter w) => w.Put(new byte[Protocol.MaxModStateBytes - 256]);
        public void Read(NetDataReader r) { if (r.AvailableBytes != Protocol.MaxModStateBytes - 256) throw new FormatException(); r.GetRemainingBytes(); }
    }

    private sealed class WaitingTransport : ISessionTransport
    {
        private bool _sent;
        private string? _reason;
        public string Name => "No Welcome";
        public bool IsConnected { get; private set; }
        public int PingMs => 0;
        public Func<byte[], string?>? ValidateHello { get; set; }
        public event Action? Connected { add { } remove { } }
        public event Action<string>? Disconnected;
        public event Action<byte[], SessionDelivery>? Received;
        public event Action<string>? Log { add { } remove { } }
        public bool Start(Role role, byte[] hello, out string error) { IsConnected = true; error = ""; return true; }
        public void Poll()
        {
            if (!_sent) { _sent = true; Received?.Invoke(new[] { (byte)MessageType.Welcome }, SessionDelivery.ReliableOrdered); }
            if (_reason != null) { var reason = _reason; _reason = null; IsConnected = false; Disconnected?.Invoke(reason); }
        }
        public bool Send(byte[] payload, SessionDelivery delivery, bool bulk = false) => true;
        public void Disconnect(string reason) => _reason = reason;
        public void Dispose() => IsConnected = false;
    }

    private sealed class FramedLink : ISessionTransport
    {
        public FramedLink Peer = null!;
        private PacketStream _stream = new();
        private readonly Queue<Action> _events = new();
        private readonly Queue<byte[]> _frames = new();
        public int RetryPolls;
        public string Name => "Fake Steam";
        public bool IsConnected { get; private set; }
        public int PingMs => 1;
        public Func<byte[], string?>? ValidateHello { get; set; }
        public event Action? Connected;
        public event Action<string>? Disconnected;
        public event Action<byte[], SessionDelivery>? Received;
        public event Action<string>? Log { add { } remove { } }
        public bool Start(Role role, byte[] hello, out string error)
        {
            error = "";
            if (role == Role.Client) Peer._events.Enqueue(() =>
            {
                var reason = Peer.ValidateHello!(hello);
                if (reason != null) { _events.Enqueue(() => Disconnected?.Invoke(reason)); return; }
                Peer.IsConnected = IsConnected = true;
                Peer.Connected?.Invoke(); _events.Enqueue(() => Connected?.Invoke());
            });
            return true;
        }
        public void Poll()
        {
            while (_events.TryDequeue(out var action)) action();
            while (_frames.TryDequeue(out var frame))
            { var packet = _stream.Receive(frame, out var delivery); if (packet != null) Received?.Invoke(packet, delivery); }
            if (RetryPolls > 0) { RetryPolls--; return; }
            _stream.Pump(frame => { Peer._frames.Enqueue(frame); return FrameSendResult.Accepted; });
        }
        public bool Send(byte[] payload, SessionDelivery delivery, bool bulk = false)
        {
            if (delivery == SessionDelivery.LatestState) { Peer._frames.Enqueue(_stream.Latest(payload)); return true; }
            return _stream.Enqueue(payload, bulk);
        }
        public void Disconnect(string reason) => Dispose();
        public void Dispose()
        {
            IsConnected = false; _stream = new PacketStream();
            Peer._events.Enqueue(() => { Peer.IsConnected = false; Peer.Disconnected?.Invoke("The other player left."); });
        }
    }
}

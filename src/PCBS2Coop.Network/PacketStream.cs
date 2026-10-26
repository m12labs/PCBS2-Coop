using System.Buffers.Binary;

namespace PCBS2Coop.Network;

public enum FrameSendResult { Accepted, Retry, Failed }

/// <summary>Bounded, ordered framing for message transports. Independent of the native Steam API,
/// so fragmentation, backpressure and stale-state filtering can be exercised without Steam.</summary>
public sealed class PacketStream
{
    public const int MaxFrameBytes = 64 * 1024;
    public const int MaxMessageBytes = 2 * 1024 * 1024;
    public const int MaxQueuedBytes = 4 * 1024 * 1024;
    public const int BulkLimit = MaxQueuedBytes - MaxMessageBytes;
    private const int HeaderBytes = 17;
    private sealed class Outgoing
    {
        public byte[] Data = Array.Empty<byte>();
        public ulong Id;
        public int Offset;
    }
    private readonly Queue<Outgoing> _outbox = new();
    private readonly Func<long> _now;
    private ulong _sendId, _receivedId, _stateSent, _stateReceived;
    private byte[]? _partial;
    private int _offset;
    private long _sendProgress, _receiveProgress;
    public int QueuedBytes { get; private set; }
    public int QueueHighWater { get; private set; }
    public bool Pending => _outbox.Count > 0;
    public string? Failure { get; private set; }
    public PacketStream(Func<long>? now = null) => _now = now ?? (() => Environment.TickCount64);

    public bool Enqueue(byte[] data, bool bulk = false)
    {
        if (Failure != null) return false;
        if (data.Length < 1 || data.Length > MaxMessageBytes) return Fail("Reliable message exceeds the transport limit.");
        if (data.Length > (bulk ? BulkLimit : MaxQueuedBytes) - QueuedBytes)
            return bulk ? false : Fail("Reliable send queue is full.");
        if (!Pending) _sendProgress = _now();
        _outbox.Enqueue(new Outgoing { Data = (byte[])data.Clone(), Id = ++_sendId });
        QueuedBytes += data.Length;
        QueueHighWater = Math.Max(QueueHighWater, QueuedBytes);
        return true;
    }

    public void Pump(Func<byte[], FrameSendResult> send)
    {
        for (var i = 0; i < 128 && Pending && Failure == null; i++)
        {
            var message = _outbox.Peek();
            var count = Math.Min(MaxFrameBytes - HeaderBytes, message.Data.Length - message.Offset);
            var frame = new byte[HeaderBytes + count];
            frame[0] = 2;
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(1), message.Id);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(9), message.Data.Length);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(13), message.Offset);
            message.Data.AsSpan(message.Offset, count).CopyTo(frame.AsSpan(HeaderBytes));
            var result = send(frame);
            if (result == FrameSendResult.Retry) break;
            if (result == FrameSendResult.Failed) { Fail("Steam rejected a reliable send."); break; }
            message.Offset += count;
            _sendProgress = _now();
            if (message.Offset == message.Data.Length) { _outbox.Dequeue(); QueuedBytes -= message.Data.Length; }
        }
        CheckTimeout();
    }

    public byte[] Latest(byte[] data)
    {
        if (data.Length > 1024) throw new ArgumentException("Latest-state packet is too large.");
        var frame = new byte[9 + data.Length]; frame[0] = 3;
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(1), ++_stateSent);
        data.CopyTo(frame, 9);
        return frame;
    }

    /// <summary>Returns a complete owned application packet, or null for a fragment/stale state.</summary>
    public byte[]? Receive(byte[] frame, out SessionDelivery delivery)
    {
        delivery = SessionDelivery.ReliableOrdered;
        if (Failure != null) return null;
        if (frame.Length < 1 || frame.Length > MaxFrameBytes) { Fail("Invalid Steam frame length."); return null; }
        if (frame[0] == 3)
        {
            if (frame.Length < 10 || frame.Length > 1033) { Fail("Invalid latest-state frame."); return null; }
            delivery = SessionDelivery.LatestState;
            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(1));
            if (sequence <= _stateReceived) return null;
            _stateReceived = sequence;
            return frame.AsSpan(9).ToArray();
        }
        if (frame[0] != 2 || frame.Length <= HeaderBytes) { Fail("Invalid reliable frame."); return null; }
        var id = BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(1));
        var length = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(9));
        var offset = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(13));
        var count = frame.Length - HeaderBytes;
        if (id != _receivedId + 1 || length < 1 || length > MaxMessageBytes || offset != _offset || count > length - offset || offset < 0 ||
            (_partial != null && _partial.Length != length))
        { Fail("Out-of-order or oversized reliable frame."); return null; }
        _partial ??= new byte[length];
        frame.AsSpan(HeaderBytes).CopyTo(_partial.AsSpan(_offset));
        _offset += count; _receiveProgress = _now();
        if (_offset != length) return null;
        var result = _partial; _partial = null; _offset = 0; _receivedId = id;
        return result;
    }

    public void CheckTimeout()
    {
        if ((Pending && _now() - _sendProgress > 60_000) || (_partial != null && _now() - _receiveProgress > 60_000))
            Fail("Steam reliable transfer stalled for 60 seconds.");
    }
    private bool Fail(string reason) { Failure ??= reason; return false; }
}

using System.Security.Cryptography;
using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>A received snapshot: the game save file plus, for a load snapshot, where it sits in the change stream.</summary>
public sealed record Snapshot(uint TransferId, SnapshotPurpose Purpose, string FileName, byte[] File, SnapshotPosition? Position);

/// <summary>
/// Host side of snapshot transfers. One transfer at a time, in the order they were started:
/// mark → file header → chunks (paced by <see cref="Pump"/>). A load mark is sent immediately, because it fixes
/// the snapshot's place in the change stream; anything still queued or in flight is aborted first. A copy mark is
/// sent when the copy reaches the front of the queue.
/// </summary>
internal sealed class SnapshotSender
{
    private sealed class Outgoing
    {
        public uint Id;
        public SnapshotPurpose Purpose;
        public string? FileName;
        public byte[]? File;
        public int Sent;
        public bool HeaderSent;
        public byte[] MarkMessage = Array.Empty<byte>();
        public bool MarkSent;
    }

    private readonly Action<NetDataWriter> _send;
    private readonly Func<NetDataWriter, bool> _sendBulk;
    private readonly NetDataWriter _w = new();
    private readonly Queue<Outgoing> _queue = new();
    private uint _nextId;

    public SnapshotSender(Action<NetDataWriter> send, Func<NetDataWriter, bool>? sendBulk = null)
    {
        _send = send;
        _sendBulk = sendBulk ?? (w => { send(w); return true; });
    }

    /// <summary>A load transfer is marked and not yet fully sent.</summary>
    public bool LoadInProgress => _queue.Any(o => o.Purpose == SnapshotPurpose.Load);
    public int Queued => _queue.Count;

    /// <summary>Start a transfer; the file follows with <see cref="SetFile"/>.</summary>
    public uint Mark(SnapshotPurpose purpose, SnapshotPosition? position)
    {
        var o = new Outgoing { Id = ++_nextId, Purpose = purpose };
        _w.Reset();
        _w.Put((byte)MessageType.SnapshotMark);
        _w.Put(o.Id);
        _w.Put((byte)purpose);
        _w.Put(position?.Generation ?? 0);
        _w.Put(position?.Revision ?? 0);
        _w.Put(position?.ChainHash ?? 0);
        Protocol.WriteBlob(_w, position?.State ?? Array.Empty<byte>());
        o.MarkMessage = _w.CopyData();
        if (purpose == SnapshotPurpose.Load)
        {
            foreach (var old in _queue.ToList()) Abort(old.Id, "superseded by a new world snapshot");
            SendMark(o);
        }
        _queue.Enqueue(o);
        return o.Id;
    }

    private void SendMark(Outgoing o)
    {
        _w.Reset();
        _w.Put(o.MarkMessage);
        _send(_w);
        o.MarkSent = true;
    }

    public void SetFile(uint id, string fileName, byte[] file)
    {
        if (!Protocol.IsValidSnapshotName(fileName)) throw new ArgumentException($"'{fileName}' is not a valid snapshot file name.");
        if (file.Length < 1 || file.Length > Protocol.MaxSnapshotBytes) throw new ArgumentException($"Snapshot size {file.Length} is out of range.");
        var o = _queue.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException($"No snapshot transfer {id}.");
        if (o.File != null) throw new InvalidOperationException($"Snapshot transfer {id} already has a file.");
        o.FileName = fileName;
        o.File = file;
    }

    /// <summary>Give up on a transfer (for example the save failed). The guest is told.</summary>
    public void Abort(uint id, string reason)
    {
        var o = _queue.FirstOrDefault(x => x.Id == id);
        if (o == null) return;
        var rest = _queue.Where(x => x != o).ToList();
        _queue.Clear();
        foreach (var x in rest) _queue.Enqueue(x);
        if (!o.MarkSent) return; // the guest never heard of it
        _w.Reset();
        _w.Put((byte)MessageType.SnapshotAbort);
        _w.Put(id);
        _w.Put(reason ?? "", Protocol.MaxShortString);
        _send(_w);
    }

    /// <summary>Forget everything (link lost).</summary>
    public void Clear() => _queue.Clear();

    /// <summary>Send up to <paramref name="budget"/> bytes of the front transfer. Returns ids of transfers finished.</summary>
    public void Pump(int budget, Action<uint>? finished = null)
    {
        while (budget > 0 && _queue.Count > 0)
        {
            var o = _queue.Peek();
            if (!o.MarkSent) SendMark(o);
            if (o.File == null) return; // waiting for the game to finish writing the save
            if (!o.HeaderSent)
            {
                _w.Reset();
                _w.Put((byte)MessageType.SnapshotFile);
                _w.Put(o.Id);
                _w.Put(o.FileName, Protocol.MaxShortString);
                _w.Put(o.File.Length);
                var hash = SHA256.HashData(o.File);
                _w.Put(hash, 0, hash.Length);
                if (!_sendBulk(_w)) return;
                o.HeaderSent = true;
            }
            while (budget > 0 && o.Sent < o.File.Length)
            {
                var n = Math.Min(Protocol.SnapshotChunkBytes, o.File.Length - o.Sent);
                _w.Reset();
                _w.Put((byte)MessageType.SnapshotChunk);
                _w.Put(o.Id);
                _w.Put(o.Sent);
                _w.Put(n);
                _w.Put(o.File, o.Sent, n);
                if (!_sendBulk(_w)) return;
                o.Sent += n;
                budget -= n;
            }
            if (o.Sent < o.File.Length) return;
            _queue.Dequeue();
            finished?.Invoke(o.Id);
        }
    }
}

/// <summary>Guest side of snapshot transfers. Validates everything before handing a file over.</summary>
internal sealed class SnapshotReceiver
{
    private uint _id;
    private SnapshotPurpose _purpose;
    private SnapshotPosition? _position;
    private string? _name;
    private byte[]? _hash;
    private byte[]? _buffer;
    private int _received;

    public bool Active => _id != 0;
    public SnapshotPurpose Purpose => _purpose;
    /// <summary>0..1 of the current transfer, or -1 when the file size isn't known yet.</summary>
    public float Progress => _buffer == null ? -1 : (float)_received / _buffer.Length;

    /// <summary>A mark arrived. Returns the position for a load snapshot (the caller starts holding world changes).</summary>
    public SnapshotPosition? OnMark(NetDataReader r, out uint id, out SnapshotPurpose purpose)
    {
        id = r.GetUInt();
        purpose = (SnapshotPurpose)r.GetByte();
        var gen = r.GetUInt();
        var rev = r.GetULong();
        var hash = r.GetULong();
        var state = Protocol.ReadBlob(r, Protocol.MaxModStateBytes, out var len) ?? throw new FormatException($"mod state of {len} bytes is out of range");
        if (purpose is not (SnapshotPurpose.Load or SnapshotPurpose.Copy)) throw new FormatException($"unknown snapshot purpose {(byte)purpose}");
        if (id == 0) throw new FormatException("snapshot id 0");
        Reset();
        _id = id;
        _purpose = purpose;
        _position = purpose == SnapshotPurpose.Load ? new SnapshotPosition(gen, rev, hash, state) : null;
        return _position;
    }

    /// <summary>Returns an error, or null.</summary>
    public string? OnFile(NetDataReader r)
    {
        var id = r.GetUInt();
        var name = Protocol.ReadString(r);
        var size = r.GetInt();
        var hash = new byte[32];
        r.GetBytes(hash, 32);
        if (id != _id || _buffer != null) return $"Unexpected snapshot file header (transfer {id}).";
        if (!Protocol.IsValidSnapshotName(name)) return $"The host sent a snapshot with an unsafe file name '{Trim(name)}'.";
        if (size < 1 || size > Protocol.MaxSnapshotBytes) return $"The host's snapshot is {size} bytes; the limit is {Protocol.MaxSnapshotBytes}.";
        _name = name;
        _hash = hash;
        _buffer = new byte[size];
        _received = 0;
        return null;
    }

    /// <summary>Returns an error, or null. <paramref name="done"/> is set once the file is complete and verified.</summary>
    public string? OnChunk(NetDataReader r, out Snapshot? done)
    {
        done = null;
        var id = r.GetUInt();
        var offset = r.GetInt();
        var len = r.GetInt();
        if (id != _id || _buffer == null) return $"Unexpected snapshot chunk (transfer {id}).";
        if (offset != _received) return $"Snapshot chunk out of order (offset {offset}, expected {_received}).";
        if (len < 1 || len > Protocol.SnapshotChunkBytes || len > _buffer.Length - _received || len > r.AvailableBytes) return $"Snapshot chunk has a bad length ({len}).";
        r.GetBytes(_buffer, _received, len);
        _received += len;
        if (_received < _buffer.Length) return null;
        if (!SHA256.HashData(_buffer).AsSpan().SequenceEqual(_hash)) return "The host's snapshot arrived damaged (checksum mismatch).";
        done = new Snapshot(_id, _purpose, _name!, _buffer, _position);
        Reset();
        return null;
    }

    public void Reset()
    {
        _id = 0;
        _position = null;
        _name = null;
        _hash = null;
        _buffer = null;
        _received = 0;
    }

    private static string Trim(string s) => s.Length > 40 ? s[..40] + "…" : s;
}

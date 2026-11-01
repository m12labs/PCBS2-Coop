using System.Security.Cryptography;
using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>Native adapter for a boot drive's opaque software record. The network layer never interprets the bytes.</summary>
public interface ISoftwareNative
{
    string? CheckHost(in CommandContext ctx, PcFact pc, PartLink bootDrive);
    /// <summary>Called on both peers only after a complete, hash-verified host result is committed.</summary>
    void Changed(in MutationContext ctx, PcFact pc, PartLink bootDrive, byte[] record);
}

/// <summary>Host-ordered boot-drive software transfer. A transfer is one Begin, ordered chunks and Commit. Its
/// partial bytes are world state so a snapshot taken mid-transfer can continue with the next chunk.</summary>
public sealed class SoftwareWorld : ICommandHandler, IWorldState
{
    private enum Step : byte { Begin = 1, Chunk = 2, Commit = 3 }
    public const int MaxBytes = 2 * 1024 * 1024, MaxChunkBytes = 15 * 1024, HashBytes = 32;
    private sealed record Transfer(ulong Epoch, NetObjectId Pc, string Path, int Length, byte[] Hash, int Total,
        List<byte[]> Chunks);
    private readonly ObjectWorld _objects;
    private readonly ActivityWorld _activity;
    private readonly ISoftwareNative _native;
    private readonly bool _authority;
    private Transfer? _transfer;

    public SoftwareWorld(ObjectWorld objects, ActivityWorld activity, ISoftwareNative native, bool authority)
    { _objects = objects; _activity = activity; _native = native; _authority = authority; }
    public string Name => "software";
    public bool Busy => _transfer != null;

    public static byte[] Begin(ulong epoch, NetObjectId pc, string drivePath, int length, byte[] sha256) => Encode(w =>
    { w.Put((byte)Step.Begin); w.Put(epoch); pc.Write(w); w.Put(drivePath ?? ""); w.Put(length); Protocol.WriteBlob(w, sha256); });
    public static byte[] Chunk(ulong epoch, NetObjectId pc, int index, int total, byte[] data) => Encode(w =>
    { w.Put((byte)Step.Chunk); w.Put(epoch); pc.Write(w); w.Put(index); w.Put(total); Protocol.WriteBlob(w, data); });
    public static byte[] Commit(ulong epoch, NetObjectId pc) => Encode(w => { w.Put((byte)Step.Commit); w.Put(epoch); pc.Write(w); });

    private static byte[] Encode(Action<NetDataWriter> f) { var w = new NetDataWriter(); f(w); return w.CopyData(); }
    private sealed record Command(Step Step, ulong Epoch, NetObjectId Pc, string Path, int Length, byte[] Hash,
        int Index, int Total, byte[] Data);
    private static Command Parse(NetDataReader r)
    {
        var step = (Step)r.GetByte(); var epoch = r.GetULong(); var pc = NetObjectId.Read(r);
        Command c;
        if (step == Step.Begin)
        {
            var path = r.GetString(Protocol.MaxShortString); var len = r.GetInt();
            var hash = Protocol.ReadBlob(r, HashBytes, out var declared);
            if (hash == null || declared != HashBytes) throw new FormatException("Invalid software hash.");
            c = new(step, epoch, pc, path, len, hash, 0, 0, Array.Empty<byte>());
        }
        else if (step == Step.Chunk)
        {
            var index = r.GetInt(); var total = r.GetInt(); var data = Protocol.ReadBlob(r, MaxChunkBytes, out _);
            if (data == null) throw new FormatException("Invalid software chunk.");
            c = new(step, epoch, pc, "", 0, Array.Empty<byte>(), index, total, data);
        }
        else if (step == Step.Commit) c = new(step, epoch, pc, "", 0, Array.Empty<byte>(), 0, 0, Array.Empty<byte>());
        else throw new FormatException("Unknown software action.");
        if (r.AvailableBytes != 0) throw new FormatException("Trailing software bytes.");
        return c;
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        if (ctx.Op.Actor != ActorSlot.Host) return CommandVerdict.Reject("Only the host publishes software records.");
        if (_activity.Get(ActorSlot.Host) is not { } actor || actor.Epoch != c.Epoch)
            return CommandVerdict.Reject("Host presence changed.");
        if (c.Step == Step.Begin)
        {
            if (_transfer != null) return CommandVerdict.Reject("A software transfer is already in progress.");
            if (c.Pc.IsNone || c.Length is < 1 or > MaxBytes || c.Path.Length is < 1 or > Protocol.MaxShortString || c.Path.Any(char.IsControl))
                return CommandVerdict.Reject("Invalid software transfer header.");
            var pc = _objects.Pcs.SingleOrDefault(p => p.Id == c.Pc);
            var drive = pc?.Links.SingleOrDefault(x => x.Path == c.Path);
            if (pc == null || pc.Location.Place == PcPlace.Carrier || drive == null || pc.Parts.All(p => p.Id != drive.Part))
                return CommandVerdict.Reject("The PC or boot drive is no longer live.");
            string? error; try { error = _native.CheckHost(ctx, pc, drive); } catch (Exception e) { error = "Host check failed: " + e.Message; }
            return error == null ? CommandVerdict.Accept : CommandVerdict.Reject(error);
        }
        var t = _transfer;
        if (t == null || t.Epoch != c.Epoch || t.Pc != c.Pc) return CommandVerdict.Reject("Unknown or stale software transfer.");
        if (c.Step == Step.Chunk)
        {
            var expectedSize = c.Index == t.Total - 1 ? t.Length - c.Index * MaxChunkBytes : MaxChunkBytes;
            if (c.Total != t.Total || c.Index != t.Chunks.Count || c.Index < 0 || c.Index >= t.Total || c.Data.Length != expectedSize)
                return CommandVerdict.Reject("Duplicate, out-of-order or malformed software chunk.");
            return CommandVerdict.Accept;
        }
        if (c.Step == Step.Commit)
        {
            if (t.Chunks.Count != t.Total) return CommandVerdict.Reject("Software transfer is incomplete.");
            var bytes = t.Chunks.SelectMany(x => x).ToArray();
            return bytes.Length == t.Length && CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), t.Hash)
                ? CommandVerdict.Accept : CommandVerdict.Reject("Software record hash or length does not match.");
        }
        return CommandVerdict.Reject("Unknown software action.");
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        if (ctx.Op.Actor != ActorSlot.Host) throw new InvalidDataException("Software mutation from a guest.");
        if (c.Step == Step.Begin)
        {
            if (_transfer != null || c.Length is < 1 or > MaxBytes || c.Path.Length == 0 ||
                _objects.Pcs.All(p => p.Id != c.Pc) || _activity.Get(ActorSlot.Host) is not { } actor || actor.Epoch != c.Epoch)
                throw new InvalidDataException("Software transfer begin no longer applies.");
            var total = (c.Length + MaxChunkBytes - 1) / MaxChunkBytes;
            _transfer = new(c.Epoch, c.Pc, c.Path, c.Length, c.Hash, total, new List<byte[]>(total));
            return;
        }
        var t = _transfer;
        if (t == null || t.Epoch != c.Epoch || t.Pc != c.Pc) throw new InvalidDataException("Unknown software transfer.");
        if (c.Step == Step.Chunk)
        {
            if (c.Total != t.Total || c.Index != t.Chunks.Count || c.Index >= t.Total || c.Data.Length != (c.Index == t.Total - 1 ? t.Length - c.Index * MaxChunkBytes : MaxChunkBytes))
                throw new InvalidDataException("Software chunk order differs from the host.");
            t.Chunks.Add(c.Data); return;
        }
        if (c.Step != Step.Commit || t.Chunks.Count != t.Total) throw new InvalidDataException("Incomplete software transfer.");
        var record = t.Chunks.SelectMany(x => x).ToArray();
        if (record.Length != t.Length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(record), t.Hash))
            throw new InvalidDataException("Software record hash mismatch.");
        var pc = _objects.Pcs.SingleOrDefault(p => p.Id == t.Pc) ?? throw new InvalidDataException("Software PC disappeared.");
        var drive = pc.Links.SingleOrDefault(x => x.Path == t.Path) ?? throw new InvalidDataException("Software boot drive disappeared.");
        _transfer = null;
        _native.Changed(ctx, pc, drive, record);
    }

    public void Reset() => _transfer = null;
    public void Write(NetDataWriter w)
    {
        w.Put(_transfer != null);
        if (_transfer is not { } t) return;
        w.Put(t.Epoch); t.Pc.Write(w); w.Put(t.Path); w.Put(t.Length); Protocol.WriteBlob(w, t.Hash); w.Put(t.Total); w.Put(t.Chunks.Count);
        foreach (var chunk in t.Chunks) Protocol.WriteBlob(w, chunk);
    }
    public void Read(NetDataReader r)
    {
        _transfer = null;
        if (!r.GetBool()) return;
        var epoch = r.GetULong(); var pc = NetObjectId.Read(r); var path = r.GetString(Protocol.MaxShortString); var length = r.GetInt();
        var hash = Protocol.ReadBlob(r, HashBytes, out var hashLength); var total = r.GetInt(); var count = r.GetInt();
        if (hash == null || hashLength != HashBytes || length is < 1 or > MaxBytes || total != (length + MaxChunkBytes - 1) / MaxChunkBytes || count < 0 || count >= total)
            throw new FormatException("Invalid software snapshot state.");
        var chunks = new List<byte[]>(count); var accumulated = 0;
        for (var i = 0; i < count; i++)
        {
            var data = Protocol.ReadBlob(r, MaxChunkBytes, out _); if (data == null) throw new FormatException("Invalid software snapshot chunk.");
            var size = i == total - 1 ? length - i * MaxChunkBytes : MaxChunkBytes;
            if (data.Length != size) throw new FormatException("Invalid software snapshot chunk size.");
            accumulated += data.Length; chunks.Add(data);
        }
        if (accumulated > length || _objects.Pcs.All(p => p.Id != pc) || path.Length == 0) throw new FormatException("Invalid software snapshot transfer.");
        _transfer = new(epoch, pc, path, length, hash, total, chunks);
    }
}

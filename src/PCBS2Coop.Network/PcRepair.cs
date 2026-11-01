using System.Security.Cryptography;
using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>Native side of a PC repair: the host sends its record, the guest reloads that PC from it.</summary>
public interface IPcRepairNative
{
    /// <summary>A guest asked for this PC (both peers; the host schedules sending its record).</summary>
    void Requested(in MutationContext ctx, PcFact pc);
    /// <summary>The host's complete, hash-checked record arrived (both peers; the guest reloads).</summary>
    void Arrived(in MutationContext ctx, PcFact pc, byte[] record, bool powered);
}

/// <summary>Repairing one PC that a game could not show (owner decision, November 1, 2026: reload that PC with a notice
/// instead of going out of sync). The host's game is the reference: a guest requests a PC, the host sends its native
/// record in ordered chunks, the guest reloads that PC. A transfer is not saved; a snapshot taken during one drops it
/// (the new guest loads the host's PC anyway), so chunks without a transfer are ignored.</summary>
public sealed class PcRepairWorld : ICommandHandler
{
    private enum Step : byte { Request = 1, Begin = 2, Chunk = 3, Commit = 4 }
    public const int MaxChunkBytes = 15 * 1024, MaxBytes = 2 * 1024 * 1024, HashBytes = 32;

    private sealed class Transfer
    {
        public NetObjectId Pc;
        public int Length, Total;
        public byte[] Hash = Array.Empty<byte>();
        public bool Powered;
        public List<byte[]> Chunks = new();
    }

    private readonly ObjectWorld _objects;
    private readonly ActivityWorld _activity;
    private readonly IPcRepairNative _native;
    private Transfer? _transfer;

    public PcRepairWorld(ObjectWorld objects, ActivityWorld activity, IPcRepairNative native)
    { _objects = objects; _activity = activity; _native = native; }
    public string Name => "PC repair";
    public bool Busy => _transfer != null;
    public void Reset() => _transfer = null;

    private static byte[] Encode(Action<NetDataWriter> f) { var w = new NetDataWriter(); f(w); return w.CopyData(); }
    public static byte[] Request(ulong epoch, NetObjectId pc) => Encode(w => { w.Put((byte)Step.Request); w.Put(epoch); pc.Write(w); });
    public static byte[] Begin(ulong epoch, NetObjectId pc, int length, byte[] sha256, bool powered) => Encode(w =>
    { w.Put((byte)Step.Begin); w.Put(epoch); pc.Write(w); w.Put(length); Protocol.WriteBlob(w, sha256); w.Put(powered); });
    public static byte[] Chunk(ulong epoch, NetObjectId pc, int index, int total, byte[] data) => Encode(w =>
    { w.Put((byte)Step.Chunk); w.Put(epoch); pc.Write(w); w.Put(index); w.Put(total); Protocol.WriteBlob(w, data); });
    public static byte[] Commit(ulong epoch, NetObjectId pc) => Encode(w => { w.Put((byte)Step.Commit); w.Put(epoch); pc.Write(w); });

    private sealed record Command(Step Step, ulong Epoch, NetObjectId Pc, int Length, byte[] Hash, bool Powered, int Index, int Total, byte[] Data);

    private static Command Parse(NetDataReader r)
    {
        var step = (Step)r.GetByte(); var epoch = r.GetULong(); var pc = NetObjectId.Read(r);
        Command c = step switch
        {
            Step.Request or Step.Commit => new(step, epoch, pc, 0, Array.Empty<byte>(), false, 0, 0, Array.Empty<byte>()),
            Step.Begin => ReadBegin(r, epoch, pc),
            Step.Chunk => ReadChunk(r, epoch, pc),
            _ => throw new FormatException("Unknown PC repair step."),
        };
        if (r.AvailableBytes != 0 || pc.IsNone) throw new FormatException("Invalid PC repair payload.");
        return c;
    }

    private static Command ReadBegin(NetDataReader r, ulong epoch, NetObjectId pc)
    {
        var length = r.GetInt();
        var hash = Protocol.ReadBlob(r, HashBytes, out var declared);
        if (hash == null || declared != HashBytes) throw new FormatException("Invalid PC repair hash.");
        return new(Step.Begin, epoch, pc, length, hash, r.GetBool(), 0, 0, Array.Empty<byte>());
    }

    private static Command ReadChunk(NetDataReader r, ulong epoch, NetObjectId pc)
    {
        var index = r.GetInt(); var total = r.GetInt();
        var data = Protocol.ReadBlob(r, MaxChunkBytes, out _) ?? throw new FormatException("Invalid PC repair chunk.");
        return new(Step.Chunk, epoch, pc, 0, Array.Empty<byte>(), false, index, total, data);
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        if (_activity.Get(ctx.Op.Actor) is not { } actor || actor.Epoch != c.Epoch) return CommandVerdict.Reject("Actor presence changed.");
        var pc = _objects.Pcs.SingleOrDefault(p => p.Id == c.Pc);
        if (c.Step == Step.Request)
        {
            if (ctx.Op.Actor == ActorSlot.Host) return CommandVerdict.Reject("The host's game is the reference; it doesn't request repairs.");
            return pc is { Location.Place: PcPlace.Bench } ? CommandVerdict.Accept : CommandVerdict.Reject("That PC is no longer on a bench.");
        }
        if (ctx.Op.Actor != ActorSlot.Host) return CommandVerdict.Reject("Only the host sends PC records.");
        if (c.Step == Step.Begin)
        {
            if (_transfer != null) return CommandVerdict.Reject("A PC repair is already being sent.");
            if (pc == null || c.Length is < 1 or > MaxBytes) return CommandVerdict.Reject("Invalid PC repair header.");
            return CommandVerdict.Accept;
        }
        var t = _transfer;
        if (t == null || t.Pc != c.Pc) return CommandVerdict.Reject("No such PC repair transfer.");
        if (c.Step == Step.Chunk)
            return c.Index == t.Chunks.Count && c.Total == t.Total && c.Index < t.Total ? CommandVerdict.Accept : CommandVerdict.Reject("Out-of-order PC repair chunk.");
        return t.Chunks.Count == t.Total ? CommandVerdict.Accept : CommandVerdict.Reject("Incomplete PC repair transfer.");
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        var pc = _objects.Pcs.SingleOrDefault(p => p.Id == c.Pc);
        switch (c.Step)
        {
            case Step.Request:
                if (pc != null) _native.Requested(ctx, pc);
                return;
            case Step.Begin:
                _transfer = new Transfer
                {
                    Pc = c.Pc, Length = c.Length, Hash = c.Hash, Powered = c.Powered,
                    Total = (c.Length + MaxChunkBytes - 1) / MaxChunkBytes,
                };
                return;
            case Step.Chunk:
                // A guest that loaded a snapshot during the transfer has no Begin; its PC came from the host anyway.
                if (_transfer is { } t && t.Pc == c.Pc && c.Index == t.Chunks.Count) t.Chunks.Add(c.Data);
                return;
            case Step.Commit:
                var done = _transfer;
                _transfer = null;
                if (done == null || done.Pc != c.Pc || done.Chunks.Count != done.Total) return;
                var record = done.Chunks.SelectMany(x => x).ToArray();
                if (record.Length != done.Length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(record), done.Hash))
                    throw new InvalidDataException("PC repair record hash mismatch.");
                if (pc != null) _native.Arrived(ctx, pc, record, done.Powered);
                return;
        }
    }
}

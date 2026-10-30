using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

public enum TransferAction : byte { Pickup = 1, Drop = 2, Store = 3, Retrieve = 4, Recover = 5 }
/// <summary>A transfer request. <see cref="Bench"/> is a location fact ID. For a drop, <see cref="Occupant"/> is the PC
/// the requester saw on that bench (a deliberate swap) or none. <see cref="Absent"/> names the departed seat for Recover.</summary>
public sealed record TransferCommand(TransferAction Action, ulong Epoch, NetObjectId Pc, NetObjectId Bench, NetObjectId Occupant, ActorSlot Absent);
/// <summary>One location change of an accepted transfer. A swap has two, committed together; moves leaving a bench come first.</summary>
public sealed record PcMove(NetObjectId Pc, PcLocation From, PcLocation To);

/// <summary>Native side of PC transfers. CheckHost runs on the host during validation and only reads.
/// Moved runs after the model changed and must not submit commands.</summary>
public interface IPcTransferNative
{
    string? CheckHost(in CommandContext ctx, TransferCommand command, IReadOnlyList<PcMove> moves);
    void Moved(in MutationContext ctx, TransferCommand command, IReadOnlyList<PcMove> moves);
}

/// <summary>Host-ordered atomic PC transfers between a bench, a player's hands and the shared cupboard. Each
/// accepted transfer moves every affected PC in one change, so a PC always has exactly one location. The host
/// checks the source, the destination and every moved PC's activity; a drop names the bench occupant the requester
/// saw, so a bench that was just taken refuses instead of becoming a surprise swap. Recover (host only) moves a
/// departed player's carried PCs to the shared cupboard.</summary>
public sealed class PcTransfers : ICommandHandler
{
    private readonly ObjectWorld _objects;
    private readonly ActivityWorld _activity;
    private readonly IPcTransferNative _native;
    private readonly bool _authority;
    public PcTransfers(ObjectWorld objects, ActivityWorld activity, IPcTransferNative native, bool authority)
    { _objects = objects; _activity = activity; _native = native; _authority = authority; }
    public string Name => "pc-transfer";

    public static byte[] Pickup(ulong epoch, NetObjectId pc, NetObjectId bench) => Encode(TransferAction.Pickup, epoch, w => { pc.Write(w); bench.Write(w); });
    public static byte[] Drop(ulong epoch, NetObjectId pc, NetObjectId bench, NetObjectId occupant) =>
        Encode(TransferAction.Drop, epoch, w => { pc.Write(w); bench.Write(w); occupant.Write(w); });
    public static byte[] Store(ulong epoch, NetObjectId pc) => Encode(TransferAction.Store, epoch, w => pc.Write(w));
    public static byte[] Retrieve(ulong epoch, NetObjectId pc) => Encode(TransferAction.Retrieve, epoch, w => pc.Write(w));
    public static byte[] Recover(ulong epoch, ActorSlot absent) => Encode(TransferAction.Recover, epoch, w => w.Put((byte)absent));
    private static byte[] Encode(TransferAction action, ulong epoch, Action<NetDataWriter> body)
    { var w = new NetDataWriter(); w.Put((byte)action); w.Put(epoch); body(w); return w.CopyData(); }

    private static TransferCommand Parse(NetDataReader r)
    {
        var action = (TransferAction)r.GetByte(); var epoch = r.GetULong();
        TransferCommand c = action switch
        {
            TransferAction.Pickup => new(action, epoch, NetObjectId.Read(r), NetObjectId.Read(r), default, default),
            TransferAction.Drop => new(action, epoch, NetObjectId.Read(r), NetObjectId.Read(r), NetObjectId.Read(r), default),
            TransferAction.Store or TransferAction.Retrieve => new(action, epoch, NetObjectId.Read(r), default, default, default),
            TransferAction.Recover => new(action, epoch, default, default, default, (ActorSlot)r.GetByte()),
            _ => throw new FormatException("Unknown PC transfer action.")
        };
        if (r.AvailableBytes != 0) throw new FormatException("Trailing PC transfer bytes.");
        if (c.Action == TransferAction.Recover && c.Absent is not (ActorSlot.Host or ActorSlot.Guest)) throw new FormatException("Invalid departed seat.");
        return c;
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        var error = Plan(ctx.Op.Actor, c, tolerant: false, out var moves);
        if (error == null)
        {
            try { error = _native.CheckHost(ctx, c, moves); }
            catch (Exception e) { error = "Host check failed: " + e.Message; }
        }
        return error == null ? CommandVerdict.Accept : CommandVerdict.Reject(error);
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        // The same checks against this peer's copy of the same revision. A difference is a divergence, never a guess.
        if (Plan(ctx.Op.Actor, c, tolerant: !_authority, out var moves) is { } error) throw new InvalidDataException("PC transfer no longer applies: " + error);
        if (moves.Count == 0) return; // only host-only PCs this guest never saw
        var to = moves.ToDictionary(m => m.Pc, m => m.To);
        // A PC leaving its bench is next shown by a native load, which closes latches over installed parts (4b N3).
        var leaving = moves.Where(m => m.From.Place == PcPlace.Bench).Select(m => m.Pc).ToHashSet();
        _objects.Replace(_objects.WorldId, _objects.NextId, _objects.Locations,
            _objects.Pcs.Select(x => to.TryGetValue(x.Id, out var l)
                ? (leaving.Contains(x.Id) ? AssemblyResources.SettleLoad(x) : x).At(l) : x).ToArray(), _objects.Retired);
        _native.Moved(ctx, c, moves);
    }

    /// <summary>The location changes a command makes, or why it cannot. <paramref name="tolerant"/> (guest Apply only)
    /// skips a PC that only the host knows: its IDs are tombstones here, as in <see cref="PcLifecycle"/>.</summary>
    private string? Plan(ActorSlot requester, TransferCommand c, bool tolerant, out List<PcMove> moves)
    {
        moves = new List<PcMove>();
        if (_objects.WorldId == Guid.Empty) return "Objects are not bound yet.";
        if (_activity.Get(requester) is not { } actor || actor.Epoch != c.Epoch) return "Actor presence changed.";
        var hands = PcLocation.Carried(actor.Actor, actor.Player);
        PcFact? Find(NetObjectId id) => _objects.Pcs.SingleOrDefault(x => x.Id == id);
        bool HostOnly(NetObjectId id) => tolerant && Find(id) == null && _objects.Retired.Contains(id);
        var held = _objects.Pcs.SingleOrDefault(x => x.Location.Place == PcPlace.Carrier && x.Location.Actor == actor.Actor);
        switch (c.Action)
        {
            case TransferAction.Pickup:
            {
                if (HostOnly(c.Pc)) return null;
                if (Bench(c.Bench) is not { } bench) return "Unknown bench.";
                if (Find(c.Pc) is not { } pc) return "That PC no longer exists.";
                if (pc.Location != PcLocation.Bench(bench)) return "Someone else moved that PC.";
                if (held != null) return "Your hands already hold a PC.";
                if (_activity.MovementBlock(pc.Id) is { } block) return block;
                moves.Add(new(pc.Id, pc.Location, hands));
                return null;
            }
            case TransferAction.Drop:
            {
                if (Bench(c.Bench) is not { } bench) return "Unknown bench.";
                var occupant = _objects.Pcs.SingleOrDefault(x => x.Location == PcLocation.Bench(bench));
                if (c.Occupant.IsNone)
                {
                    if (occupant != null) return "That bench was just taken.";
                }
                else if (occupant?.Id != c.Occupant && !(occupant == null && HostOnly(c.Occupant))) return "That bench changed.";
                if (occupant != null)
                {
                    if (_activity.MovementBlock(occupant.Id) is { } busy) return busy;
                    moves.Add(new(occupant.Id, occupant.Location, hands));
                }
                if (HostOnly(c.Pc)) return null;
                if (Find(c.Pc) is not { } pc || pc.Location != hands) { moves.Clear(); return "That PC is not in your hands."; }
                if (_activity.MovementBlock(pc.Id) is { } block) { moves.Clear(); return block; }
                moves.Add(new(pc.Id, hands, PcLocation.Bench(bench)));
                return null;
            }
            case TransferAction.Store:
            {
                if (HostOnly(c.Pc)) return null;
                if (Find(c.Pc) is not { } pc || pc.Location != hands) return "That PC is not in your hands.";
                if (_activity.MovementBlock(pc.Id) is { } block) return block;
                moves.Add(new(pc.Id, hands, PcLocation.Storage));
                return null;
            }
            case TransferAction.Retrieve:
            {
                if (HostOnly(c.Pc)) return null;
                if (Find(c.Pc) is not { } pc) return "That PC no longer exists.";
                if (pc.Location != PcLocation.Storage) return "Someone else took that PC.";
                if (held != null) return "Your hands already hold a PC.";
                if (_activity.MovementBlock(pc.Id) is { } block) return block;
                moves.Add(new(pc.Id, PcLocation.Storage, hands));
                return null;
            }
            case TransferAction.Recover:
            {
                if (requester != ActorSlot.Host) return "Only the host recovers a departed player's PC.";
                var present = _activity.Get(c.Absent)?.Player ?? default;
                foreach (var pc in _objects.Pcs.Where(x => x.Location.Place == PcPlace.Carrier && x.Location.Actor == c.Absent && x.Location.Player != present))
                    moves.Add(new(pc.Id, pc.Location, PcLocation.Storage));
                return moves.Count == 0 ? "No departed player's PC to recover." : null;
            }
            default: return "Unknown action.";
        }
    }

    private string? Bench(NetObjectId id) => id.IsNone ? null : _objects.Locations.SingleOrDefault(l => l.Id == id)?.Key;
}

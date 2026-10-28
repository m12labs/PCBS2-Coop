using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>A latch toggle request: the latch's resource ID and the version and state the requester saw.
/// <see cref="Open"/> is the state asked for (the opposite of what was seen).</summary>
public sealed record MechanismCommand(ulong Epoch, NetObjectId Pc, NetObjectId Resource, ulong Version, bool Open);

/// <summary>Native side of latches. CheckHost runs on the host during validation and only reads. Changed runs after
/// the model changed (both peers, in host order) and must not submit commands.</summary>
public interface IMechanismNative
{
    string? CheckHost(in CommandContext ctx, PcFact pc, AssemblyResource latch, bool open);
    /// <summary><paramref name="settle"/>: a host save/snapshot boundary closing latches over installed parts,
    /// shown instantly; otherwise one player's toggle, shown with the native animation.</summary>
    void Changed(in MutationContext ctx, PcFact pc, IReadOnlyList<AssemblyResource> changed, bool settle);
}

/// <summary>Host-ordered latch state (task 4d): RAM clips, CPU levers and LGA shields, M.2 covers, PCI cover locks,
/// heatsink pins and hinged doors. Screws and standoffs are automatic inside component edits (4b) and have no
/// command. A toggle names the version the requester saw, so two toggles of one latch can't both land; it is refused
/// while the other player's operation holds that latch or a part it guards, or while it is blocked or moving on the
/// host's game. Owner decision (4d): a latch the native load closes (a clip over an installed stick) is closed when
/// the host saves or sends a snapshot and when its PC leaves the bench, as a single-player reload would.</summary>
public sealed class MechanismWorld : ICommandHandler
{
    private enum Step : byte { Toggle = 1, Settle = 2 }
    public const int MaxSettle = 4096;
    private readonly ObjectWorld _objects;
    private readonly ActivityWorld _activity;
    private readonly IMechanismNative _native;
    private readonly bool _authority;
    public MechanismWorld(ObjectWorld objects, ActivityWorld activity, IMechanismNative native, bool authority)
    { _objects = objects; _activity = activity; _native = native; _authority = authority; }
    public string Name => "mechanism";

    public static byte[] Toggle(ulong epoch, NetObjectId pc, NetObjectId latch, ulong version, bool open) => Encode(w =>
    { w.Put((byte)Step.Toggle); w.Put(epoch); pc.Write(w); latch.Write(w); w.Put(version); w.Put(open); });

    /// <summary>Host only: close every latch the native load would close on these PCs (exact versions).</summary>
    public static byte[] Settle(IEnumerable<PcFact> pcs) => Encode(w =>
    {
        var rows = pcs.SelectMany(pc => AssemblyResources.LoadClosed(pc).Select(id => (Pc: pc.Id, Id: id, pc.Resources.Single(r => r.Id == id).Version))).ToArray();
        w.Put((byte)Step.Settle); w.Put(rows.Length);
        foreach (var row in rows) { row.Pc.Write(w); row.Id.Write(w); w.Put(row.Version); }
    });

    /// <summary>Bench PCs with latches the native load would close.</summary>
    public static IEnumerable<PcFact> Unsettled(ObjectWorld objects) =>
        objects.Pcs.Where(pc => pc.Location.Place == PcPlace.Bench && AssemblyResources.LoadClosed(pc).Length > 0);

    private static byte[] Encode(Action<NetDataWriter> write) { var w = new NetDataWriter(); write(w); return w.CopyData(); }

    private sealed record Row(NetObjectId Pc, NetObjectId Id, ulong Version);
    private static (Step Action, MechanismCommand? Toggle, Row[] Settle) Parse(NetDataReader r)
    {
        var action = (Step)r.GetByte();
        (Step, MechanismCommand?, Row[]) result;
        if (action == Step.Toggle)
            result = (action, new MechanismCommand(r.GetULong(), NetObjectId.Read(r), NetObjectId.Read(r), r.GetULong(), r.GetBool()), Array.Empty<Row>());
        else if (action == Step.Settle)
        {
            var n = r.GetInt();
            if (n is < 1 or > MaxSettle) throw new FormatException("Invalid latch settle count.");
            var rows = new Row[n];
            for (var i = 0; i < n; i++) rows[i] = new(NetObjectId.Read(r), NetObjectId.Read(r), r.GetULong());
            result = (action, null, rows);
        }
        else throw new FormatException("Unknown mechanism action.");
        if (r.AvailableBytes != 0) throw new FormatException("Trailing mechanism bytes.");
        return result;
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var (action, toggle, rows) = Parse(payload);
        string? error;
        if (action == Step.Toggle)
        {
            error = Check(ctx.Op.Actor, toggle!, out var pc, out var latch);
            if (error == null)
            {
                try { error = _native.CheckHost(ctx, pc!, latch!, toggle!.Open); }
                catch (Exception e) { error = "Host check failed: " + e.Message; }
            }
        }
        else error = ctx.Op.Actor != ActorSlot.Host ? "Only the host settles latches." : CheckSettle(rows, tolerant: false);
        return error == null ? CommandVerdict.Accept : CommandVerdict.Reject(error);
    }

    private string? Check(ActorSlot requester, MechanismCommand c, out PcFact? pc, out AssemblyResource? latch)
    {
        pc = null; latch = null;
        if (_objects.WorldId == Guid.Empty) return "Objects are not bound yet.";
        if (_activity.Get(requester) is not { } actor || actor.Epoch != c.Epoch) return "Actor presence changed.";
        pc = _objects.Pcs.SingleOrDefault(x => x.Id == c.Pc);
        if (pc == null || pc.Location.Place != PcPlace.Bench) return "That PC is no longer on a bench.";
        if (actor.WorkingPc != pc.Id) return "You're not working on that PC.";
        latch = pc.Resources.SingleOrDefault(r => r.Id == c.Resource);
        if (latch == null || latch.Kind is not (AssemblyResourceKind.Pin or AssemblyResourceKind.Socket)) return "Unknown latch.";
        if (AssemblyResources.Dormant(pc).Contains(latch.Id)) return "That latch is on a removed panel.";
        // No linked latch exists in the default workshop (4b/4c reports); its native semantics are unprobed.
        if (latch.Coupled.Count != 0) return "Linked latches aren't shared in co-op yet.";
        if (latch.Version != c.Version || latch.Open == c.Open) return "That latch was just changed.";
        var id = latch.Id; var on = pc;
        foreach (var other in _activity.Actors)
            // Owner decision (4e): while the other player's edit runs on a PC, nothing else on it changes.
            if (other.Pending is { } p && p.Pc == on.Id && (p.Kind == ActivityOperation.Move || other.Actor != requester))
                return p.Kind == ActivityOperation.Move ? "That PC is being moved." : "The other player is changing a part of that PC.";
        return null;
    }

    private string? CheckSettle(Row[] rows, bool tolerant)
    {
        if (_objects.WorldId == Guid.Empty) return "Objects are not bound yet.";
        if (rows.Distinct().Count() != rows.Length || rows.Select(r => r.Id).Distinct().Count() != rows.Length) return "Duplicate latch.";
        foreach (var row in rows)
        {
            var pc = _objects.Pcs.SingleOrDefault(x => x.Id == row.Pc);
            if (pc == null)
            {
                // A guest skips a PC only the host knew, as the other world commands do; never a live one.
                if (tolerant && _objects.Retired.Contains(row.Pc)) continue;
                return "Unknown PC.";
            }
            if (pc.Location.Place != PcPlace.Bench) return "That PC is no longer on a bench.";
            var latch = pc.Resources.SingleOrDefault(r => r.Id == row.Id);
            if (latch == null || latch.Version != row.Version || !AssemblyResources.LoadClosed(pc).Contains(row.Id)) return "That latch was just changed.";
            if (_activity.Actors.Any(a => a.Pending is { } p && p.Pc == pc.Id &&
                (p.Kind == ActivityOperation.Move || AssemblyResources.Conflicts(pc, p.Writes, new[] { row.Id }))))
                return "A latch over an installed part is in use.";
        }
        return null;
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var (action, toggle, rows) = Parse(payload);
        if (action == Step.Toggle)
        {
            // The same checks against this peer's copy of the same revision. A difference is a divergence.
            if (Check(ctx.Op.Actor, toggle!, out var pc, out var latch) is { } error) throw new InvalidDataException("Latch toggle no longer applies: " + error);
            var changed = latch! with { Open = toggle!.Open, Version = checked(latch!.Version + 1) };
            var next = Publish(pc!, new[] { changed });
            _native.Changed(ctx, next, new[] { changed }, settle: false);
            return;
        }
        if (ctx.Op.Actor != ActorSlot.Host) throw new InvalidDataException("Latch settle from a guest.");
        if (CheckSettle(rows, tolerant: !_authority) is { } why) throw new InvalidDataException("Latch settle no longer applies: " + why);
        foreach (var group in rows.Where(r => _objects.Pcs.Any(p => p.Id == r.Pc)).GroupBy(r => r.Pc))
        {
            var pc = _objects.Pcs.Single(p => p.Id == group.Key);
            var ids = group.Select(r => r.Id).ToHashSet();
            var changed = pc.Resources.Where(r => ids.Contains(r.Id)).Select(r => r with { Open = false, Version = checked(r.Version + 1) }).ToArray();
            var next = Publish(pc, changed);
            _native.Changed(ctx, next, changed, settle: true);
        }
    }

    private PcFact Publish(PcFact pc, IReadOnlyList<AssemblyResource> changed)
    {
        var byId = changed.ToDictionary(r => r.Id);
        var next = pc with { Resources = pc.Resources.Select(r => byId.GetValueOrDefault(r.Id) ?? r).ToArray() };
        _objects.Replace(_objects.WorldId, _objects.NextId, _objects.Locations, _objects.Pcs.Select(p => p.Id == pc.Id ? next : p), _objects.Retired);
        return _objects.Pcs.Single(p => p.Id == pc.Id);
    }
}

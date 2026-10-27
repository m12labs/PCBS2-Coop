using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>A case-panel request: remove the panel in <see cref="Slot"/> (<see cref="Part"/> empty) or refit it with
/// <see cref="Part"/>, naming the slot version the requester saw.</summary>
public sealed record PanelCommand(ulong Epoch, NetObjectId Pc, NetObjectId Slot, ulong Version, string Part)
{
    public bool Remove => Part == "";
}

/// <summary>Native side of case panels. CheckHost runs on the host during validation and only reads. Changed runs after
/// the model changed (both peers, in host order) and must not submit commands.</summary>
public interface IPanelNative
{
    string? CheckHost(in CommandContext ctx, PcFact pc, AssemblyResource slot, PanelCommand command);
    void Changed(in MutationContext ctx, PcFact pc, AssemblyResource slot, PanelCommand command);
}

/// <summary>Host-ordered case access (task 4d, owner decision October 27, 2026): removing and refitting the integral
/// panels of a case (side, front and other case-part slots). A panel's own screws and latches stay in the model while
/// it is off (<see cref="AssemblyResources.Dormant"/>) and come back with it. Refused while the other player's
/// operation holds that panel or anything it carries or guards, while something is mounted on the panel, or when the
/// host's game can't do it. Other component edits are not shared yet.</summary>
public sealed class CasePanelWorld : ICommandHandler
{
    private const byte Action = 1;
    private readonly ObjectWorld _objects;
    private readonly ActivityWorld _activity;
    private readonly IPanelNative _native;
    public CasePanelWorld(ObjectWorld objects, ActivityWorld activity, IPanelNative native)
    { _objects = objects; _activity = activity; _native = native; }
    public string Name => "case-panel";

    public static byte[] Remove(ulong epoch, NetObjectId pc, NetObjectId slot, ulong version) => Encode(epoch, pc, slot, version, "");
    public static byte[] Refit(ulong epoch, NetObjectId pc, NetObjectId slot, ulong version, string part) => Encode(epoch, pc, slot, version, part ?? "");
    private static byte[] Encode(ulong epoch, NetObjectId pc, NetObjectId slot, ulong version, string part)
    {
        var w = new NetDataWriter(); w.Put(Action); w.Put(epoch); pc.Write(w); slot.Write(w); w.Put(version); w.Put(part); return w.CopyData();
    }

    private static PanelCommand Parse(NetDataReader r)
    {
        if (r.GetByte() != Action) throw new FormatException("Unknown case-panel action.");
        var c = new PanelCommand(r.GetULong(), NetObjectId.Read(r), NetObjectId.Read(r), r.GetULong(), r.GetString(1024));
        if (r.AvailableBytes != 0) throw new FormatException("Trailing case-panel bytes.");
        return c;
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        var error = Check(ctx.Op.Actor, c, out var pc, out var slot);
        if (error == null)
        {
            try { error = _native.CheckHost(ctx, pc!, slot!, c); }
            catch (Exception e) { error = "Host check failed: " + e.Message; }
        }
        return error == null ? CommandVerdict.Accept : CommandVerdict.Reject(error);
    }

    private string? Check(ActorSlot requester, PanelCommand c, out PcFact? pc, out AssemblyResource? slot)
    {
        pc = null; slot = null;
        if (_objects.WorldId == Guid.Empty) return "Objects are not bound yet.";
        if (_activity.Get(requester) is not { } actor || actor.Epoch != c.Epoch) return "Actor presence changed.";
        pc = _objects.Pcs.SingleOrDefault(x => x.Id == c.Pc);
        if (pc == null || pc.Location.Place != PcPlace.Bench) return "That PC is no longer on a bench.";
        if (actor.WorkingPc != pc.Id) return "You're not working on that PC.";
        slot = pc.Resources.SingleOrDefault(r => r.Id == c.Slot);
        if (slot == null || slot.Kind != AssemblyResourceKind.Slot || slot.IntegralHome == "" || !slot.Occupant.IsNone) return "That isn't a case panel.";
        if (c.Part.Length > 1023 || c.Part.Any(char.IsControl)) return "Invalid panel.";
        var dormant = AssemblyResources.Dormant(pc);
        if (dormant.Contains(slot.Id)) return "That panel's mount is removed.";
        if (slot.Version != c.Version || (c.Remove ? slot.IntegralPart == "" : slot.IntegralPart != "")) return "That panel was just changed.";
        if (!c.Remove && c.Part != slot.IntegralHome) return "That panel doesn't belong in this slot.";
        var resources = pc.Resources.ToDictionary(r => r.Id);
        if (c.Remove && slot.Carries.Any(id => resources[id] is { Kind: AssemblyResourceKind.Slot } s && (!s.Occupant.IsNone || s.IntegralPart != "")))
            return "Remove what's mounted on that panel first.";
        var targets = slot.Carries.Prepend(slot.Id).ToArray(); var on = pc;
        foreach (var other in _activity.Actors)
            if (other.Pending is { } p && p.Pc == on.Id && (p.Kind == ActivityOperation.Move ||
                (other.Actor != requester && AssemblyResources.Conflicts(on, p.Writes, targets))))
                return p.Kind == ActivityOperation.Move ? "That PC is being moved." : "The other player is using that part.";
        return null;
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        if (Check(ctx.Op.Actor, c, out var pc, out var slot) is { } error) throw new InvalidDataException("Case panel change no longer applies: " + error);
        // The panel's own resources keep their IDs; a refit shows new native instances in their fresh state, and every
        // change advances versions so an older callback can't act on them.
        var carried = slot!.Carries.ToHashSet();
        var next = pc! with { Resources = pc.Resources.Select(r =>
            r.Id == slot.Id ? r with { IntegralPart = c.Part, Version = checked(r.Version + 1) } :
            carried.Contains(r.Id) ? (c.Remove ? r : r with { Open = false, Rotation = 0, Offset = 0 }) with { Version = checked(r.Version + 1) } : r).ToArray() };
        _objects.Replace(_objects.WorldId, _objects.NextId, _objects.Locations, _objects.Pcs.Select(p => p.Id == pc.Id ? next : p), _objects.Retired);
        var published = _objects.Pcs.Single(p => p.Id == pc.Id);
        _native.Changed(ctx, published, published.Resources.Single(r => r.Id == slot.Id), c);
    }
}

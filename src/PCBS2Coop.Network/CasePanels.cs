using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>A case-panel request: remove the panel in <see cref="Slot"/> (<see cref="Part"/> empty) or refit it with
/// <see cref="Part"/>, naming the slot version the requester saw.</summary>
public sealed record PanelCommand(ulong Epoch, NetObjectId Pc, NetObjectId Slot, ulong Version, string Part)
{
    public bool Remove => Part == "";
}

/// <summary>The end of a panel change (0.68): the requester's game finished the panel's own gesture, screws included
/// (<see cref="Done"/>), or the gesture stopped with the panel as it was, which takes the change back.
/// <see cref="Token"/> is the request's operation, which holds the PC until then.</summary>
public sealed record PanelFinish(ulong Epoch, OperationId Token, NetObjectId Pc, NetObjectId Slot, bool Done);

/// <summary>Native side of case panels. CheckHost runs on the host during validation and only reads. Changed and
/// Finished run after the model changed (both peers, in host order) and must not submit commands.</summary>
public interface IPanelNative
{
    string? CheckHost(in CommandContext ctx, PcFact pc, AssemblyResource slot, PanelCommand command);
    /// <summary>A request was accepted. The requester's game runs the game's own gesture; the other game waits for
    /// <see cref="Finished"/>.</summary>
    void Changed(in MutationContext ctx, PcFact pc, AssemblyResource slot, PanelCommand command);
    /// <summary>The requester finished (<paramref name="done"/>) or took the change back (the model shows the panel as it
    /// was before the request).</summary>
    void Finished(in MutationContext ctx, PcFact pc, AssemblyResource slot, OperationId token, bool done);
}

/// <summary>Host-ordered case access (task 4d, owner decision October 27, 2026): removing and refitting the integral
/// panels of a case (side, front and other case-part slots). A panel's own screws and latches stay in the model while
/// it is off (<see cref="AssemblyResources.Dormant"/>) and come back with it. Refused while the other player's
/// operation holds that PC, while something is mounted on the panel, or when the host's game can't do it.
/// <para>0.68 (owner decision, November 10, 2026): Career panels need their screws turned by hand. The request holds
/// the whole PC for the requester until their game reports the panel fully off or fully on (<see cref="Finish"/>). The
/// other game shows the change only then. A gesture that stops part-way takes the change back.</para></summary>
public sealed class CasePanelWorld : ICommandHandler
{
    private const byte Request = 1, End = 2;
    private readonly ObjectWorld _objects;
    private readonly ActivityWorld _activity;
    private readonly IPanelNative _native;
    /// <summary>Requests this peer applied that still hold their PC. The host validates finishes against it.</summary>
    private readonly Dictionary<OperationId, (NetObjectId Pc, NetObjectId Slot)> _running = new();
    public CasePanelWorld(ObjectWorld objects, ActivityWorld activity, IPanelNative native)
    { _objects = objects; _activity = activity; _native = native; }
    public string Name => "case-panel";

    public static byte[] Remove(ulong epoch, NetObjectId pc, NetObjectId slot, ulong version) => Encode(epoch, pc, slot, version, "");
    public static byte[] Refit(ulong epoch, NetObjectId pc, NetObjectId slot, ulong version, string part) => Encode(epoch, pc, slot, version, part ?? "");
    private static byte[] Encode(ulong epoch, NetObjectId pc, NetObjectId slot, ulong version, string part)
    {
        var w = new NetDataWriter(); w.Put(Request); w.Put(epoch); pc.Write(w); slot.Write(w); w.Put(version); w.Put(part); return w.CopyData();
    }
    public static byte[] Finish(ulong epoch, OperationId token, NetObjectId pc, NetObjectId slot, bool done)
    {
        var w = new NetDataWriter(); w.Put(End); w.Put(epoch); w.Put((byte)token.Actor); w.Put(token.Sequence); pc.Write(w); slot.Write(w); w.Put(done);
        return w.CopyData();
    }

    private static object Parse(NetDataReader r)
    {
        object c = r.GetByte() switch
        {
            Request => new PanelCommand(r.GetULong(), NetObjectId.Read(r), NetObjectId.Read(r), r.GetULong(), r.GetString(1024)),
            End => new PanelFinish(r.GetULong(), new OperationId((ActorSlot)r.GetByte(), r.GetUInt()), NetObjectId.Read(r), NetObjectId.Read(r), r.GetBool()),
            _ => throw new FormatException("Unknown case-panel action."),
        };
        if (r.AvailableBytes != 0) throw new FormatException("Trailing case-panel bytes.");
        return c;
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var parsed = Parse(payload);
        if (parsed is PanelFinish f) return CheckFinish(ctx.Op.Actor, f, host: true, out _, out _) is { } why ? CommandVerdict.Reject(why) : CommandVerdict.Accept;
        var c = (PanelCommand)parsed;
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
        var on = pc;
        foreach (var other in _activity.Actors)
            // Owner decision (4e): while the other player's edit runs on a PC, nothing else on it changes.
            if (other.Pending is { } p && p.Pc == on.Id && (p.Kind == ActivityOperation.Move || other.Actor != requester))
                return p.Kind == ActivityOperation.Move ? "That PC is being moved." : "The other player is changing a part of that PC.";
        if (actor.Pending != null) return "Your previous change is still finishing.";
        return null;
    }

    private string? CheckFinish(ActorSlot requester, PanelFinish f, bool host, out PcFact? pc, out AssemblyResource? slot)
    {
        pc = null; slot = null;
        if (f.Token.Actor != requester) return "That panel change isn't yours.";
        if (_activity.Get(requester) is not { } actor || actor.Epoch != f.Epoch) return "Actor presence changed.";
        if (actor.Pending is not { Kind: ActivityOperation.Edit } p || p.Token != f.Token || p.Pc != f.Pc) return "That panel change no longer holds the PC.";
        // Only the host knows every running request; a guest applies what the host accepted.
        if (host && (!_running.TryGetValue(f.Token, out var run) || run.Pc != f.Pc || run.Slot != f.Slot)) return "That isn't a running panel change.";
        pc = _objects.Pcs.SingleOrDefault(x => x.Id == f.Pc);
        if (pc == null || pc.Location.Place != PcPlace.Bench) return "That PC is no longer on a bench.";
        slot = pc.Resources.SingleOrDefault(r => r.Id == f.Slot);
        if (slot == null || slot.Kind != AssemblyResourceKind.Slot || slot.IntegralHome == "") return "That isn't a case panel.";
        return null;
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var parsed = Parse(payload);
        if (parsed is PanelFinish f) { ApplyFinish(ctx, f); return; }
        var c = (PanelCommand)parsed;
        if (Check(ctx.Op.Actor, c, out var pc, out var slot) is { } error) throw new InvalidDataException("Case panel change no longer applies: " + error);
        // The panel's own resources keep their IDs; a refit shows new native instances in their fresh state, and every
        // change advances versions so an older callback can't act on them.
        var next = Toggle(pc!, slot!, c.Part);
        // The requester holds the whole PC until their game has finished the panel (Finish).
        _activity.SetEditClaim(ctx.Op, ctx.Op.Actor, pc!, Array.Empty<NetObjectId>(), allowWalking: false);
        _running[ctx.Op] = (pc!.Id, slot!.Id);
        _objects.Replace(_objects.WorldId, _objects.NextId, _objects.Locations, _objects.Pcs.Select(p => p.Id == pc.Id ? next : p), _objects.Retired);
        var published = _objects.Pcs.Single(p => p.Id == pc.Id);
        _native.Changed(ctx, published, published.Resources.Single(r => r.Id == slot.Id), c);
    }

    private void ApplyFinish(in MutationContext ctx, PanelFinish f)
    {
        if (CheckFinish(ctx.Op.Actor, f, host: false, out var pc, out var slot) is { } error)
            throw new InvalidDataException("Case panel finish no longer applies: " + error);
        _running.Remove(f.Token);
        if (!_activity.ClearClaim(f.Token.Actor, f.Token)) throw new InvalidDataException("The panel change's claim changed.");
        if (!f.Done)
        {
            // Taken back: the panel is as it was before the request. A removal taken back leaves what the panel carries
            // as it was; a refit taken back takes the panel off again.
            var back = Toggle(pc!, slot!, slot!.IntegralPart == "" ? slot.IntegralHome : "", fresh: false);
            _objects.Replace(_objects.WorldId, _objects.NextId, _objects.Locations, _objects.Pcs.Select(p => p.Id == f.Pc ? back : p), _objects.Retired);
        }
        var published = _objects.Pcs.Single(p => p.Id == f.Pc);
        _native.Finished(ctx, published, published.Resources.Single(r => r.Id == f.Slot), f.Token, f.Done);
    }

    /// <summary>The PC with that panel slot showing <paramref name="part"/> (empty: off). A refitted panel's carried
    /// resources start in their fresh state (<paramref name="fresh"/>).</summary>
    private static PcFact Toggle(PcFact pc, AssemblyResource slot, string part, bool fresh = true)
    {
        var carried = slot.Carries.ToHashSet(); var reset = fresh && part != "";
        return pc with { Resources = pc.Resources.Select(r =>
            r.Id == slot.Id ? r with { IntegralPart = part, Version = checked(r.Version + 1) } :
            carried.Contains(r.Id) ? (reset ? r with { Open = false, Rotation = 0, Offset = 0 } : r) with { Version = checked(r.Version + 1) } : r).ToArray() };
    }
}

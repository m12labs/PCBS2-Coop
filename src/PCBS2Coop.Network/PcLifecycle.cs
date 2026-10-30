using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

public enum PcAction : byte { Create = 1, Delete = 2, Observe = 3, Extend = 4 }
public enum PcRemoval : byte { Carried = 1, Stored = 2 }
/// <summary>A reusable Free Build catalog entry: SKU, native type and its position among entries with both. Not a physical part.</summary>
public sealed record CatalogRef(string Sku, string NativeType, int Ordinal);
public sealed record TemplatePart(string Sku, string NativeType);
public sealed record TemplateLink(string Path, int Part);
/// <summary>Topology of a PC freshly minted from a catalog entry: parts in first-appearance order over ordinally sorted paths.</summary>
public sealed record PcTemplate(IReadOnlyList<TemplatePart> Parts, IReadOnlyList<TemplateLink> Links);
/// <summary>A host-native location. <see cref="Bench"/> is the location fact ID for a bench and none otherwise.</summary>
public sealed record PcObservation(NetObjectId Pc, PcPlace Place, NetObjectId Bench);
/// <summary>A new path inside an existing PC: either a new part (index into <see cref="PcGrowth.Parts"/>) or an alias of an existing part.</summary>
public sealed record GrowthLink(string Path, int NewPart, NetObjectId Existing);
/// <summary>Parts the native load added to a created PC (for example a case prefab's default fans). Existing links never change.</summary>
public sealed record PcGrowth(IReadOnlyList<TemplatePart> Parts, IReadOnlyList<GrowthLink> Links);
public sealed record PcCommand(PcAction Action, ulong Epoch, CatalogRef? Template, NetObjectId Pc, PcRemoval From, IReadOnlyList<PcObservation> Moves)
{
    public PcGrowth? Growth { get; init; }
}

/// <summary>Native side of PC creation/removal. Describe must be deterministic on both peers. CheckHost runs on the
/// host during validation and only reads. Created/Deleted run after the model changed and must not submit commands.</summary>
public interface IPcNative
{
    PcTemplate? Describe(CatalogRef template);
    string? CheckHost(in CommandContext ctx, PcCommand command, PcFact? target);
    void Created(in MutationContext ctx, PcFact pc, CatalogRef template, PcFact? replaced);
    void Deleted(in MutationContext ctx, PcFact pc, PcRemoval from);
}

/// <summary>Host-ordered PC creation and removal. A catalog case creates a new physical PC in the requester's hands
/// (optionally replacing their empty carried case, as the native switch does); deletion retires every PC/part ID once.
/// Observe reports the host's own native locations so both models agree until atomic transfers (task 3f) replace it.
/// The host assigns IDs while canonicalizing; a guest whose model missed host-only allocations records them as
/// unresolvable tombstones rather than inventing facts.</summary>
public sealed class PcLifecycle : ICanonicalCommandHandler
{
    public const int MaxTemplateParts = 256;
    public const int MaxTemplateLinks = 512;
    public const int MaxObservations = 512;
    private readonly ObjectWorld _objects;
    private readonly ActivityWorld _activity;
    private readonly IPcNative _native;
    private readonly bool _authority;
    public PcLifecycle(ObjectWorld objects, ActivityWorld activity, IPcNative native, bool authority)
    { _objects = objects; _activity = activity; _native = native; _authority = authority; }
    public string Name => "pc-lifecycle";
    /// <summary>Guest only: IDs the host issued outside this guest's change stream (host save observation). They never resolve here.</summary>
    public ulong SkippedIds { get; private set; }

    public static byte[] Create(ulong epoch, CatalogRef template, NetObjectId replace) => Encode(w =>
    { w.Put((byte)PcAction.Create); w.Put(epoch); w.Put(template.Sku); w.Put(template.NativeType); w.Put(template.Ordinal); replace.Write(w); });
    public static byte[] Delete(ulong epoch, NetObjectId pc, PcRemoval from) => Encode(w =>
    { w.Put((byte)PcAction.Delete); w.Put(epoch); pc.Write(w); w.Put((byte)from); });
    public static byte[] Observe(ulong epoch, IReadOnlyList<PcObservation> moves) => Encode(w =>
    {
        w.Put((byte)PcAction.Observe); w.Put(epoch); w.Put(moves.Count);
        foreach (var m in moves) { m.Pc.Write(w); w.Put((byte)m.Place); m.Bench.Write(w); }
    });
    /// <summary>Host only: give identities to parts its native load added to a PC, in the change stream, before a capture would.</summary>
    public static byte[] Extend(ulong epoch, NetObjectId pc, PcGrowth growth) => Encode(w =>
    {
        w.Put((byte)PcAction.Extend); w.Put(epoch); pc.Write(w);
        w.Put(growth.Parts.Count); foreach (var p in growth.Parts) { w.Put(p.Sku); w.Put(p.NativeType); }
        w.Put(growth.Links.Count); foreach (var l in growth.Links) { w.Put(l.Path); w.Put(l.NewPart); l.Existing.Write(w); }
    });
    private static byte[] Encode(Action<NetDataWriter> write) { var w = new NetDataWriter(); write(w); return w.CopyData(); }

    private static PcCommand Parse(NetDataReader r)
    {
        var action = (PcAction)r.GetByte(); var epoch = r.GetULong();
        switch (action)
        {
            case PcAction.Create:
                var template = new CatalogRef(r.GetString(1024), r.GetString(1024), r.GetInt());
                return new(action, epoch, template, NetObjectId.Read(r), default, Array.Empty<PcObservation>());
            case PcAction.Delete:
                var pc = NetObjectId.Read(r); var from = (PcRemoval)r.GetByte();
                if (!Enum.IsDefined(from)) throw new FormatException("Unknown PC removal source.");
                return new(action, epoch, null, pc, from, Array.Empty<PcObservation>());
            case PcAction.Observe:
                var count = r.GetInt();
                if (count is < 1 or > MaxObservations) throw new FormatException("Invalid observation count.");
                var moves = new PcObservation[count];
                for (var i = 0; i < count; i++) moves[i] = new(NetObjectId.Read(r), (PcPlace)r.GetByte(), NetObjectId.Read(r));
                return new(action, epoch, null, default, default, moves);
            case PcAction.Extend:
                var target = NetObjectId.Read(r);
                var parts = new TemplatePart[ReadCount(r, MaxTemplateParts)];
                for (var i = 0; i < parts.Length; i++) parts[i] = new(r.GetString(1024), r.GetString(1024));
                var links = new GrowthLink[ReadCount(r, MaxTemplateLinks)];
                for (var i = 0; i < links.Length; i++) links[i] = new(r.GetString(1024), r.GetInt(), NetObjectId.Read(r));
                return new(action, epoch, null, target, default, Array.Empty<PcObservation>()) { Growth = new(parts, links) };
            default: throw new FormatException("Unknown PC lifecycle action.");
        }
    }

    private PcFact? Occupant(ActorSlot actor) =>
        _objects.Pcs.SingleOrDefault(x => x.Location.Place == PcPlace.Carrier && x.Location.Actor == actor);

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        var error = Check(ctx, c, out var target);
        if (error == null)
        {
            try { error = _native.CheckHost(ctx, c, target); }
            catch (Exception e) { error = "Host check failed: " + e.Message; }
        }
        return error == null ? CommandVerdict.Accept : CommandVerdict.Reject(error);
    }

    private string? Check(in CommandContext ctx, PcCommand c, out PcFact? target)
    {
        target = null;
        if (_objects.WorldId == Guid.Empty) return "Objects are not bound yet.";
        if (_activity.Get(ctx.Op.Actor) is not { } actor || actor.Epoch != c.Epoch) return "Actor presence changed.";
        switch (c.Action)
        {
            case PcAction.Create:
            {
                if (!Valid(c.Template!)) return "Invalid catalog reference.";
                PcTemplate? template;
                try { template = _native.Describe(c.Template!); }
                catch (Exception e) { return "Case template unavailable: " + e.Message; }
                if (template == null || !Valid(template)) return "Not a case template in this catalog.";
                var occupant = Occupant(actor.Actor);
                if (c.Pc.IsNone) { if (occupant != null) return "Your hands already hold a PC."; }
                else if (occupant == null || occupant.Id != c.Pc || occupant.Location.Player != actor.Player) return "The replaced case is not in your hands.";
                else if (_activity.MovementBlock(c.Pc) is { } block) return block;
                target = occupant;
                if (_objects.NextId + (ulong)template.Parts.Count + 1 > ObjectWorld.MaxObjects + 1UL) return "Object ID space exhausted.";
                if (c.Pc.IsNone && _objects.Pcs.Count >= ObjectWorld.MaxPcs) return "Too many PCs.";
                return null;
            }
            case PcAction.Delete:
            {
                target = _objects.Pcs.SingleOrDefault(x => x.Id == c.Pc);
                if (target == null) return "PC is unknown or retired.";
                if (c.From == PcRemoval.Carried && (target.Location.Place != PcPlace.Carrier ||
                    target.Location.Actor != actor.Actor || target.Location.Player != actor.Player)) return "That PC is not in your hands.";
                if (c.From == PcRemoval.Stored && target.Location.Place != PcPlace.Storage) return "That PC is not in storage.";
                return _activity.MovementBlock(c.Pc);
            }
            case PcAction.Observe:
                if (ctx.Op.Actor != ActorSlot.Host) return "Only the host reports its observed locations.";
                try { Relocated(c, actor, tolerant: false); return null; }
                catch (InvalidDataException e) { return e.Message; }
            case PcAction.Extend:
                if (ctx.Op.Actor != ActorSlot.Host) return "Only the host reports native part growth.";
                target = _objects.Pcs.SingleOrDefault(x => x.Id == c.Pc);
                return target == null ? "PC is unknown or retired." : GrowthError(target, c.Growth!);
            default: return "Unknown action.";
        }
    }

    public byte[] Canonicalize(in CommandContext ctx, NetDataReader request)
    {
        var bytes = request.RawData.AsSpan(request.Position, request.AvailableBytes).ToArray();
        var c = Parse(request);
        if (c.Action == PcAction.Extend) { var e = new NetDataWriter(); e.Put(bytes); e.Put(_objects.NextId); return e.CopyData(); }
        if (c.Action != PcAction.Create) return bytes;
        // Propose IDs only; Apply commits them. Requests can never carry IDs of their own.
        var template = _native.Describe(c.Template!) ?? throw new InvalidOperationException("Case template disappeared during validation.");
        var w = new NetDataWriter(); w.Put(bytes);
        _activity.Get(ctx.Op.Actor)!.Player.Write(w); w.Put(_objects.NextId);
        w.Put(template.Parts.Count); foreach (var p in template.Parts) { w.Put(p.Sku); w.Put(p.NativeType); }
        w.Put(template.Links.Count); foreach (var l in template.Links) { w.Put(l.Path); w.Put(l.Part); }
        return w.CopyData();
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        var actor = _activity.Get(ctx.Op.Actor);
        if (actor == null || actor.Epoch != c.Epoch) throw new InvalidDataException("Actor epoch changed before the PC change applied.");
        switch (c.Action)
        {
            case PcAction.Create: ApplyCreate(ctx, c, actor, payload); break;
            case PcAction.Delete:
            {
                End(payload);
                var target = _objects.Pcs.SingleOrDefault(x => x.Id == c.Pc);
                if (target == null)
                {
                    // Only a host-observed PC this guest never saw can be named here; its IDs are already tombstones.
                    if (_authority || !_objects.Retired.Contains(c.Pc)) throw new InvalidDataException("Deleted PC is unknown.");
                    return;
                }
                var ids = AllIds(target).ToHashSet();
                Publish(_objects.Pcs.Where(x => x.Id != target.Id), _objects.Retired.Concat(ids), _objects.NextId);
                _native.Deleted(ctx, target, c.From);
                break;
            }
            case PcAction.Observe:
                End(payload);
                if (ctx.Op.Actor != ActorSlot.Host) throw new InvalidDataException("Observation from a guest.");
                Publish(Relocated(c, actor, tolerant: !_authority), _objects.Retired, _objects.NextId);
                break;
            case PcAction.Extend:
                if (ctx.Op.Actor != ActorSlot.Host) throw new InvalidDataException("Growth from a guest.");
                ApplyExtend(c, payload); break;
            default: throw new InvalidDataException("Unknown PC lifecycle mutation.");
        }
    }

    private void ApplyCreate(in MutationContext ctx, PcCommand c, ActorActivity actor, NetDataReader r)
    {
        var player = PlayerId.Read(r); var first = r.GetULong();
        var parts = new TemplatePart[Count(r, MaxTemplateParts)];
        for (var i = 0; i < parts.Length; i++) parts[i] = new(r.GetString(1024), r.GetString(1024));
        var links = new TemplateLink[Count(r, MaxTemplateLinks)];
        for (var i = 0; i < links.Length; i++) links[i] = new(r.GetString(1024), r.GetInt());
        End(r);
        var canonical = new PcTemplate(parts, links);
        if (player != actor.Player || !Valid(canonical)) Bad("Invalid canonical PC creation.");
        var local = _native.Describe(c.Template!) ?? throw new InvalidDataException("This peer cannot reproduce the case template.");
        if (!local.Parts.SequenceEqual(canonical.Parts) || !local.Links.SequenceEqual(canonical.Links))
            Bad("The case template topology differs on this peer.");
        var (next, gap) = Allocation(first, parts.Length + 1);
        var occupant = Occupant(actor.Actor);
        PcFact? replaced = null;
        if (c.Pc.IsNone) { if (occupant != null) Bad("The creating actor's carrier is occupied."); }
        else if (occupant?.Id == c.Pc) replaced = occupant;
        else Bad("The replaced PC is not carried by the creating actor.");
        var partIds = Enumerable.Range(0, parts.Length).Select(i => new NetObjectId(first + 1 + (ulong)i)).ToArray();
        var fact = new PcFact(new(first), new(InstanceOrigin.Catalog, player, ctx.Op), PcLocation.Carried(actor.Actor, player),
            parts.Select((p, i) => new PartFact(partIds[i], p.Sku, p.NativeType)).ToArray(),
            links.Select(l => new PartLink(l.Path, partIds[l.Part])).ToArray());
        var retired = _objects.Retired.Concat(gap).Concat(replaced == null ? Enumerable.Empty<NetObjectId>() : AllIds(replaced));
        Publish(_objects.Pcs.Where(x => x.Id != c.Pc).Append(fact), retired, next);
        SkippedIds += (ulong)gap.Length;
        _native.Created(ctx, fact, c.Template!, replaced);
    }

    /// <summary>Check host-assigned IDs. The host's must start at its allocator; a guest may be behind the host by
    /// IDs the host issued outside the stream, which become tombstones here.</summary>
    private (ulong Next, NetObjectId[] Gap) Allocation(ulong first, int count)
    {
        var next = first + (ulong)count;
        if (first < _objects.NextId || (_authority && first != _objects.NextId) || next > ObjectWorld.MaxObjects + 1UL || first > ObjectWorld.MaxObjects)
            Bad("Invalid assigned object IDs.");
        var gap = new NetObjectId[first - _objects.NextId];
        for (var i = 0; i < gap.Length; i++) gap[i] = new(_objects.NextId + (ulong)i);
        return (next, gap);
    }

    private void ApplyExtend(PcCommand c, NetDataReader r)
    {
        var first = r.GetULong(); End(r);
        var pc = _objects.Pcs.SingleOrDefault(x => x.Id == c.Pc);
        if (pc == null)
        {
            if (_authority || !_objects.Retired.Contains(c.Pc)) Bad("Extended PC is unknown.");
            return; // a host-only PC this guest never saw; its later IDs arrive as tombstones
        }
        var g = c.Growth!;
        if (GrowthError(pc, g) is { } error) Bad(error);
        var (next, gap) = Allocation(first, g.Parts.Count);
        var ids = Enumerable.Range(0, g.Parts.Count).Select(i => new NetObjectId(first + (ulong)i)).ToArray();
        var grown = pc with
        {
            Parts = pc.Parts.Concat(g.Parts.Select((p, i) => new PartFact(ids[i], p.Sku, p.NativeType))).ToArray(),
            Links = pc.Links.Concat(g.Links.Select(l => new PartLink(l.Path, l.NewPart >= 0 ? ids[l.NewPart] : l.Existing))).ToArray()
        };
        Publish(_objects.Pcs.Select(x => x.Id == pc.Id ? grown : x), _objects.Retired.Concat(gap), next);
        SkippedIds += (ulong)gap.Length;
    }

    private string? GrowthError(PcFact pc, PcGrowth g)
    {
        if (g.Parts.Count > MaxTemplateParts || g.Links.Count is < 1 or > MaxTemplateLinks) return "Invalid growth size.";
        if (g.Parts.Any(p => !Text(p.Sku) || !Text(p.NativeType))) return "Invalid grown part.";
        if (pc.Parts.Count + g.Parts.Count > ObjectWorld.MaxPartsPerPc || pc.Links.Count + g.Links.Count > ObjectWorld.MaxLinksPerPc) return "PC topology limit reached.";
        if (_objects.NextId + (ulong)g.Parts.Count > ObjectWorld.MaxObjects + 1UL) return "Object ID space exhausted.";
        var paths = pc.Links.Select(l => l.Path).ToHashSet(StringComparer.Ordinal); var linked = new HashSet<int>();
        foreach (var l in g.Links)
        {
            if (!Text(l.Path) || !paths.Add(l.Path)) return "Growth repeats or changes an existing path.";
            if (l.NewPart >= 0 ? l.NewPart >= g.Parts.Count || !l.Existing.IsNone : l.NewPart != -1 || !pc.Parts.Any(p => p.Id == l.Existing))
                return "Growth link names an unknown part.";
            if (l.NewPart >= 0) linked.Add(l.NewPart);
        }
        return linked.Count == g.Parts.Count ? null : "A grown part has no path.";
    }

    private PcFact[] Relocated(PcCommand c, ActorActivity host, bool tolerant)
    {
        if (c.Moves.Select(m => m.Pc).Distinct().Count() != c.Moves.Count) Bad("Duplicate observed PC.");
        var pcs = _objects.Pcs.ToDictionary(x => x.Id);
        foreach (var m in c.Moves)
        {
            if (!pcs.TryGetValue(m.Pc, out var pc))
            {
                if (tolerant && _objects.Retired.Contains(m.Pc)) continue; // host-only PC this guest never saw
                Bad("Observed PC is unknown or retired.");
            }
            if (pc!.Location.Place == PcPlace.Carrier && pc.Location.Actor != ActorSlot.Host) Bad("The host cannot observe another actor's carrier.");
            PcLocation location;
            if (m.Place == PcPlace.Bench) location = PcLocation.Bench(_objects.Locations.SingleOrDefault(l => l.Id == m.Bench)?.Key ?? Bad<string>("Unknown observed bench."));
            else if (m.Place == PcPlace.Storage && m.Bench.IsNone) location = PcLocation.Storage;
            else if (m.Place == PcPlace.Carrier && m.Bench.IsNone) location = PcLocation.Carried(ActorSlot.Host, host.Player);
            else { Bad("Invalid observed location."); return Array.Empty<PcFact>(); }
            pcs[m.Pc] = pc.At(location);
        }
        var result = pcs.Values.ToArray();
        // Validate the complete candidate (occupancy, carriers) without publishing it.
        new ObjectWorld().Replace(_objects.WorldId, _objects.NextId, _objects.Locations, result, _objects.Retired);
        return result;
    }

    private void Publish(IEnumerable<PcFact> pcs, IEnumerable<NetObjectId> retired, ulong next) =>
        _objects.Replace(_objects.WorldId, next, _objects.Locations, pcs.ToArray(), retired.ToArray());

    private static IEnumerable<NetObjectId> AllIds(PcFact pc) => AssemblyResources.AllIds(pc);
    private static bool Text(string s) => !string.IsNullOrEmpty(s) && s.Length <= 1023 && !s.Any(char.IsControl);
    private static bool Valid(CatalogRef t) => Text(t.Sku) && Text(t.NativeType) && t.Ordinal is >= 0 and <= ObjectWorld.MaxObjects;
    private static bool Valid(PcTemplate t)
    {
        if (t.Parts.Count is < 1 or > MaxTemplateParts || t.Links.Count is < 1 or > MaxTemplateLinks) return false;
        if (t.Parts.Any(p => !Text(p.Sku) || !Text(p.NativeType))) return false;
        var paths = new HashSet<string>(StringComparer.Ordinal); var linked = new HashSet<int>();
        foreach (var l in t.Links)
        {
            if (!Text(l.Path) || l.Part < 0 || l.Part >= t.Parts.Count || !paths.Add(l.Path)) return false;
            linked.Add(l.Part);
        }
        return linked.Count == t.Parts.Count && paths.Contains("caseID");
    }
    private static int Count(NetDataReader r, int max) { var n = r.GetInt(); if (n < 0 || n > max) Bad("Invalid collection length."); return n; }
    /// <summary>Request parsing: a read error, so a malformed request gets a refusal instead of escaping the poll loop.</summary>
    private static int ReadCount(NetDataReader r, int max) { var n = r.GetInt(); if (n < 0 || n > max) throw new FormatException("Invalid collection length."); return n; }
    private static void End(NetDataReader r) { if (r.AvailableBytes != 0) Bad("Trailing PC lifecycle bytes."); }
    private static void Bad(string why) => throw new InvalidDataException(why);
    private static T Bad<T>(string why) => throw new InvalidDataException(why);
}

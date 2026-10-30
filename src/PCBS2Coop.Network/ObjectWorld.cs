using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

public enum PcPlace : byte { Bench = 1, Storage = 2, Carrier = 3 }
public enum InstanceOrigin : byte { Baseline = 1, Catalog = 2, Imported = 3, NativeObserved = 4 }
public sealed record InstanceProvenance(InstanceOrigin Origin, PlayerId Creator, OperationId Operation);
public sealed record PcLocation(PcPlace Place, string Key, ActorSlot Actor = ActorSlot.None, PlayerId Player = default)
{
    public static PcLocation Bench(string key) => new(PcPlace.Bench, key);
    public static PcLocation Storage => new(PcPlace.Storage, "cupboard");
    public static PcLocation Carried(ActorSlot actor, PlayerId player) => new(PcPlace.Carrier, "", actor, player);
}
public sealed record PartFact(NetObjectId Id, string Sku, string NativeType);
/// <summary>Multiple paths may name one part inside a PC (native aliases). Different PCs never share a physical part ID.</summary>
public sealed record PartLink(string Path, NetObjectId Part);
/// <summary>Integrity of native assembly side effects that are absent from part topology: installed orientation,
/// integral case membership and internal cable connections. Full native data remains in the paired game save.</summary>
public sealed record PcBuildProfile(string Signature, IReadOnlyList<string> InternalConnectors);
public sealed record PcFact(NetObjectId Id, InstanceProvenance Provenance, PcLocation Location,
    IReadOnlyList<PartFact> Parts, IReadOnlyList<PartLink> Links)
{
    // Empty until the native adapter has published the prefab manifest. An empty list is not evidence
    // that a case has no slots/mechanics, nor permission to synchronize component edits on that PC.
    public IReadOnlyList<AssemblyResource> Resources { get; init; } = Array.Empty<AssemblyResource>();
    public PcBuildProfile? BuildProfile { get; init; }
    /// <summary>Physically powered on (phase 5c). The game doesn't save power, so this restores it after a load. Only a
    /// PC on a bench can be on; leaving the bench turns it off (native <c>Case.OnPickup</c>).</summary>
    public bool Powered { get; init; }
    /// <summary>The same PC at another place: off unless it stays on its bench.</summary>
    public PcFact At(PcLocation location) => this with { Location = location, Powered = Powered && location == Location };
}
public sealed record LocationFact(NetObjectId Id, string Key);

/// <summary>Host facts, independent of meshes and save-record pointers. Replace validates the entire candidate
/// before publishing it. Every allocated ID is either live or retired; gaps cannot conceal recycled IDs.</summary>
public sealed class ObjectWorld : IWorldState
{
    public const int MaxObjects = 100_000;
    public const int MaxPcs = 2048;
    public const int MaxPartsPerPc = 2048;
    public const int MaxLinksPerPc = 4096;
    public string Name => "objects";
    public Guid WorldId { get; private set; }
    public ulong NextId { get; private set; } = 1;
    public IReadOnlyList<LocationFact> Locations { get; private set; } = Array.Empty<LocationFact>();
    public IReadOnlyList<PcFact> Pcs { get; private set; } = Array.Empty<PcFact>();
    public IReadOnlyList<NetObjectId> Retired { get; private set; } = Array.Empty<NetObjectId>();
    /// <summary>Called after parsing/validation, before publishing. Plugin uses this to validate a complete local binding map before replay.</summary>
    public Action<ObjectWorld>? CheckBindings { get; set; }

    public void Reset()
    {
        WorldId = Guid.Empty; NextId = 1;
        Locations = Array.Empty<LocationFact>(); Pcs = Array.Empty<PcFact>(); Retired = Array.Empty<NetObjectId>();
    }

    public ObjectLookup Lookup(NetObjectId id) => Pcs.Any(x => AssemblyResources.AllIds(x).Contains(id)) || Locations.Any(x => x.Id == id)
        ? ObjectLookup.Live : Retired.Contains(id) ? ObjectLookup.Retired : ObjectLookup.Unknown;

    public void Replace(Guid world, ulong next, IEnumerable<LocationFact> locations, IEnumerable<PcFact> pcs, IEnumerable<NetObjectId> retired) =>
        Replace(world, next, locations, pcs, retired, dryRun: false);

    /// <summary>Every check <see cref="Replace(Guid, ulong, IEnumerable{LocationFact}, IEnumerable{PcFact}, IEnumerable{NetObjectId})"/>
    /// makes against this world, without publishing (and without the plugin's binding check).</summary>
    internal void CheckReplace(Guid world, ulong next, IEnumerable<LocationFact> locations, IEnumerable<PcFact> pcs, IEnumerable<NetObjectId> retired) =>
        Replace(world, next, locations, pcs, retired, dryRun: true);

    private void Replace(Guid world, ulong next, IEnumerable<LocationFact> locations, IEnumerable<PcFact> pcs, IEnumerable<NetObjectId> retired, bool dryRun)
    {
        var candidate = new ObjectWorld
        {
            WorldId = world, NextId = next,
            Locations = Array.AsReadOnly(locations.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray()),
            Pcs = Array.AsReadOnly(pcs.OrderBy(x => x.Id.Value).Select(x => x with
            {
                Parts = Array.AsReadOnly(x.Parts.OrderBy(p => p.Id.Value).ToArray()),
                Links = Array.AsReadOnly(x.Links.OrderBy(p => p.Path, StringComparer.Ordinal).ToArray()),
                Resources = AssemblyResources.Freeze(x.Resources),
                BuildProfile = x.BuildProfile == null ? null : x.BuildProfile with
                    { InternalConnectors = Array.AsReadOnly(x.BuildProfile.InternalConnectors.OrderBy(c => c, StringComparer.Ordinal).ToArray()) }
            }).ToArray()),
            Retired = Array.AsReadOnly(retired.OrderBy(x => x.Value).ToArray())
        };
        candidate.Validate();
        if (WorldId == world && (next < NextId || Retired.Any(x => !candidate.Retired.Contains(x))))
            Bad("An allocator/tombstone cannot move backwards in the same world.");
        if (WorldId == world)
        {
            var oldIdentities = Identities().ToDictionary(x => x.Id, x => x.Identity);
            foreach (var (id, identity) in candidate.Identities())
                if (oldIdentities.TryGetValue(id, out var oldIdentity) && oldIdentity != identity)
                    Bad("A live ID cannot change physical identity or object kind.");
            // A prefab path belongs to this physical parent incarnation for its entire lifetime.
            // A replacement must retire the old resource and allocate a new ID, even for the same SKU/path.
            var oldResources = Pcs.SelectMany(pc => pc.Resources).ToDictionary(r => r.Id);
            foreach (var pc in candidate.Pcs)
                foreach (var r in pc.Resources)
                    if (oldResources.TryGetValue(r.Id, out var old) && (r.Version < old.Version ||
                        (r.Version == old.Version && (r.Occupant != old.Occupant || r.Open != old.Open ||
                            r.Rotation != old.Rotation || r.Offset != old.Offset || r.IntegralPart != old.IntegralPart ||
                            !r.Dependencies.SequenceEqual(old.Dependencies) || !r.Coupled.SequenceEqual(old.Coupled) ||
                            !r.LoadCloses.SequenceEqual(old.LoadCloses) || r.IntegralHome != old.IntegralHome || !r.Carries.SequenceEqual(old.Carries)))))
                        Bad("An assembly change must advance its resource version.");
        }
        if (dryRun) return;
        CheckBindings?.Invoke(candidate);
        WorldId = candidate.WorldId; NextId = candidate.NextId;
        Locations = candidate.Locations; Pcs = candidate.Pcs; Retired = candidate.Retired;
    }

    private sealed record PhysicalIdentity(NetObjectId Pc, string Kind, string Key, string NativeType, NetObjectId Parent);
    private IEnumerable<(NetObjectId Id, PhysicalIdentity Identity)> Identities()
    {
        foreach (var l in Locations) yield return (l.Id, new(default, "location", l.Key, "", default));
        foreach (var pc in Pcs)
        {
            yield return (pc.Id, new(pc.Id, "pc", "", "", default));
            foreach (var p in pc.Parts) yield return (p.Id, new(pc.Id, "part", p.Sku, p.NativeType, default));
            foreach (var r in pc.Resources) yield return (r.Id, new(pc.Id, "resource:" + r.Kind, r.Binding, r.NativeType, r.Parent));
        }
    }

    public void Validate()
    {
        if (WorldId == Guid.Empty || NextId == 0 || NextId > MaxObjects + 1UL) Bad("Invalid world/allocator.");
        if (Pcs.Count > MaxPcs || Locations.Count > 1024 || Retired.Count > MaxObjects) Bad("Object count exceeds limits.");
        var ids = new HashSet<ulong>();
        void Add(NetObjectId id) { if (id.IsNone || id.Value >= NextId || !ids.Add(id.Value)) Bad("Unknown, duplicate or reused object ID."); }
        var locations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in Locations) { Add(l.Id); Text(l.Key); if (!locations.Add(l.Key)) Bad("Duplicate location binding."); }
        var benches = new HashSet<string>(StringComparer.Ordinal);
        var carriers = new HashSet<ActorSlot>();
        foreach (var pc in Pcs)
        {
            Add(pc.Id);
            if (!Enum.IsDefined(pc.Provenance.Origin) || pc.Provenance.Operation.Actor is < ActorSlot.None or > ActorSlot.Guest) Bad("Invalid provenance.");
            switch (pc.Location.Place)
            {
                case PcPlace.Bench:
                    if (!locations.Contains(pc.Location.Key) || !benches.Add(pc.Location.Key)) Bad("Unknown or occupied bench.");
                    goto case PcPlace.Storage;
                case PcPlace.Storage:
                    if (pc.Location.Actor != ActorSlot.None || !pc.Location.Player.IsEmpty ||
                        (pc.Location.Place == PcPlace.Storage && pc.Location.Key != "cupboard")) Bad("Invalid non-carrier location.");
                    break;
                case PcPlace.Carrier:
                    if (pc.Location.Actor is not (ActorSlot.Host or ActorSlot.Guest) || pc.Location.Player.IsEmpty ||
                        pc.Location.Key != "" || !carriers.Add(pc.Location.Actor)) Bad("Invalid or occupied carrier.");
                    break;
                default: Bad("Unknown PC location."); break;
            }
            if (pc.Powered && pc.Location.Place != PcPlace.Bench) Bad("Only a PC on a bench can be powered.");
            if (pc.Parts.Count is < 1 or > MaxPartsPerPc || pc.Links.Count is < 1 or > MaxLinksPerPc) Bad("Invalid PC topology size.");
            var parts = new HashSet<NetObjectId>();
            foreach (var p in pc.Parts) { Add(p.Id); Text(p.Sku); Text(p.NativeType); parts.Add(p.Id); }
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var linked = new HashSet<NetObjectId>();
            foreach (var link in pc.Links)
            {
                Text(link.Path);
                if (!parts.Contains(link.Part) || !paths.Add(link.Path)) Bad("Unknown/retired part or duplicate slot binding.");
                linked.Add(link.Part);
            }
            if (!parts.SetEquals(linked) || !paths.Contains("caseID")) Bad("Incomplete PC binding map.");
            AssemblyResources.Validate(pc, Add);
            if (pc.BuildProfile is { } profile)
            {
                if (profile.Signature.Length != 64 || profile.Signature.Any(c => !Uri.IsHexDigit(c)) || profile.InternalConnectors.Count > 4096 ||
                    profile.InternalConnectors.Distinct(StringComparer.Ordinal).Count() != profile.InternalConnectors.Count) Bad("Invalid native assembly integrity profile.");
                foreach (var connector in profile.InternalConnectors) Text(connector);
            }
        }
        foreach (var id in Retired) Add(id);
        if ((ulong)ids.Count != NextId - 1) Bad("Allocator does not account for every issued ID.");
    }

    public byte[] ToBytes() { var w = new NetDataWriter(); Write(w); return w.CopyData(); }
    public void Write(NetDataWriter w)
    {
        Validate();
        w.Put(WorldId.ToByteArray()); w.Put(NextId);
        w.Put(Locations.Count);
        foreach (var l in Locations) { l.Id.Write(w); w.Put(l.Key); }
        w.Put(Pcs.Count);
        foreach (var pc in Pcs) WritePc(w, pc);
        w.Put(Retired.Count); foreach (var id in Retired) id.Write(w);
    }

    /// <summary>One PC fact in the object-state encoding (also used by component edits to publish a settled PC).</summary>
    internal static void WritePc(NetDataWriter w, PcFact pc)
    {
        pc.Id.Write(w); w.Put((byte)pc.Provenance.Origin); pc.Provenance.Creator.Write(w);
        w.Put((byte)pc.Provenance.Operation.Actor); w.Put(pc.Provenance.Operation.Sequence);
        w.Put((byte)pc.Location.Place); w.Put(pc.Location.Key); w.Put((byte)pc.Location.Actor); pc.Location.Player.Write(w);
        w.Put(pc.Parts.Count);
        foreach (var part in pc.Parts) { part.Id.Write(w); w.Put(part.Sku); w.Put(part.NativeType); }
        w.Put(pc.Links.Count);
        foreach (var link in pc.Links) { w.Put(link.Path); link.Part.Write(w); }
        w.Put(pc.Resources.Count); foreach (var resource in pc.Resources) AssemblyResources.Write(w, resource);
        WriteProfile(w, pc.BuildProfile);
        w.Put(pc.Powered);
    }

    internal static PcFact ReadPc(NetDataReader r)
    {
        var id = NetObjectId.Read(r);
        var provenance = new InstanceProvenance((InstanceOrigin)r.GetByte(), PlayerId.Read(r), new((ActorSlot)r.GetByte(), r.GetUInt()));
        var location = new PcLocation((PcPlace)r.GetByte(), r.GetString(1024), (ActorSlot)r.GetByte(), PlayerId.Read(r));
        var parts = new PartFact[Count(r, MaxPartsPerPc)];
        for (var j = 0; j < parts.Length; j++) parts[j] = new(NetObjectId.Read(r), r.GetString(1024), r.GetString(1024));
        var links = new PartLink[Count(r, MaxLinksPerPc)];
        for (var j = 0; j < links.Length; j++) links[j] = new(r.GetString(1024), NetObjectId.Read(r));
        var resources = new AssemblyResource[Count(r, AssemblyResources.MaxPerPc)];
        for (var j = 0; j < resources.Length; j++) resources[j] = AssemblyResources.Read(r);
        return new(id, provenance, location, parts, links) { Resources = resources, BuildProfile = ReadProfile(r), Powered = r.GetBool() };
    }

    public void Read(NetDataReader r)
    {
        try
        {
            var guid = new byte[16]; r.GetBytes(guid, 16);
            var world = new Guid(guid); var next = r.GetULong();
            var locations = new LocationFact[Count(r, 1024)];
            for (var i = 0; i < locations.Length; i++) locations[i] = new(NetObjectId.Read(r), r.GetString(1024));
            var pcs = new PcFact[Count(r, MaxPcs)];
            for (var i = 0; i < pcs.Length; i++) pcs[i] = ReadPc(r);
            var retired = new NetObjectId[Count(r, MaxObjects)];
            for (var i = 0; i < retired.Length; i++) retired[i] = NetObjectId.Read(r);
            if (r.AvailableBytes != 0) Bad("Trailing object state bytes.");
            Replace(world, next, locations, pcs, retired);
        }
        catch (Exception e) when (Protocol.IsReadError(e)) { throw new InvalidDataException("Malformed object state.", e); }
    }

    private static int Count(NetDataReader r, int max) { var n = r.GetInt(); if (n < 0 || n > max) Bad("Invalid collection length."); return n; }
    internal static void WriteProfile(NetDataWriter w, PcBuildProfile? profile)
    {
        w.Put(profile != null); if (profile == null) return;
        w.Put(profile.Signature); w.Put(profile.InternalConnectors.Count); foreach (var id in profile.InternalConnectors) w.Put(id);
    }
    internal static PcBuildProfile? ReadProfile(NetDataReader r)
    {
        if (!r.GetBool()) return null;
        var hash = r.GetString(128); var connectors = new string[Count(r, 4096)];
        for (var i = 0; i < connectors.Length; i++) connectors[i] = r.GetString(1024);
        return new(hash, connectors);
    }
    private static void Text(string s) { if (string.IsNullOrEmpty(s) || s.Length > 1023 || s.Any(char.IsControl)) Bad("Invalid binding text."); }
    private static void Bad(string why) => throw new InvalidDataException(why);
}

/// <summary>Local async mesh bindings. A ticket is valid only for that world, logical object and projection attempt.</summary>
public sealed class ProjectionBindings<T> where T : class
{
    public readonly record struct Ticket(Guid World, NetObjectId Id, ulong Attempt);
    private readonly Dictionary<NetObjectId, (ulong Attempt, T? Object)> _bindings = new();
    private ulong _attempt;
    private Guid _world;
    public void Reset(Guid world) { _world = world; _bindings.Clear(); }
    public Ticket Begin(NetObjectId id)
    {
        if (_world == Guid.Empty || id.IsNone || _attempt == ulong.MaxValue) throw new InvalidOperationException("Invalid projection ticket.");
        _bindings[id] = (++_attempt, null); return new(_world, id, _attempt);
    }
    public bool Complete(Ticket ticket, T obj)
    {
        if (obj == null) throw new ArgumentNullException(nameof(obj));
        if (!IsPending(ticket)) return false;
        _bindings[ticket.Id] = (ticket.Attempt, obj); return true;
    }
    public bool IsPending(Ticket ticket) => ticket.World == _world && _bindings.TryGetValue(ticket.Id, out var binding) &&
        binding.Attempt == ticket.Attempt && binding.Object == null;
    public void Remove(NetObjectId id) => _bindings.Remove(id);
    public bool TryGet(NetObjectId id, out T? obj) { obj = _bindings.TryGetValue(id, out var b) ? b.Object : null; return obj != null; }
}

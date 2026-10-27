using System.IO.Compression;
using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>Temporary local indices connect prefab resources before the host allocates their durable IDs.</summary>
public sealed record AssemblyTemplate(NetObjectId Parent, string Binding, string NativeType, AssemblyResourceKind Kind,
    NetObjectId Occupant, bool Open, float Rotation, float Offset, IReadOnlyList<int> Guards, IReadOnlyList<int> Coupled)
{
    public string IntegralPart { get; init; } = "";
}

public interface IAssemblyNative
{
    IReadOnlyList<AssemblyTemplate>? Describe(PcFact pc);
    void Bound(PcFact pc);
    PcBuildProfile? Profile(PcFact pc) => null;
}

/// <summary>Publish a newly loaded PC's prefab manifest in the accepted stream. Initial generation baseline
/// capture uses the same builder. Binding never changes an existing manifest or adopts a component edit.</summary>
public sealed class AssemblyManifest : ICanonicalCommandHandler
{
    private readonly ObjectWorld _objects;
    private readonly ActivityWorld _activity;
    private readonly IAssemblyNative _native;
    public AssemblyManifest(ObjectWorld objects, ActivityWorld activity, IAssemblyNative native)
    { _objects = objects; _activity = activity; _native = native; }
    public string Name => "assembly-manifest";
    public static byte[] Bind(NetObjectId pc) { var w = new NetDataWriter(); pc.Write(w); return w.CopyData(); }

    public static PcFact Build(PcFact pc, IReadOnlyList<AssemblyTemplate> templates, NetObjectIdAllocator allocator)
    {
        if (templates.Count is < 1 or > AssemblyResources.MaxPerPc || pc.Resources.Count != 0)
            throw new InvalidDataException("Invalid or already bound assembly manifest.");
        var ids = templates.Select(_ => allocator.Allocate()).ToArray();
        NetObjectId[] Links(IReadOnlyList<int> indices)
        {
            if (indices.Count > AssemblyResources.MaxClaimResources || indices.Any(i => i < 0 || i >= ids.Length))
                throw new InvalidDataException("Invalid prefab dependency index.");
            return indices.Select(i => ids[i]).ToArray();
        }
        return pc with { Resources = templates.Select((r, i) => new AssemblyResource(ids[i], r.Parent, r.Binding, r.NativeType,
            r.Kind, r.Occupant, r.Open, r.Rotation, r.Offset, Links(r.Guards)) { Coupled = Links(r.Coupled), IntegralPart = r.IntegralPart }).ToArray() };
    }

    /// <summary>Explicit /sync generation repair only. Keep unchanged physical bindings; new parent incarnations
    /// allocate new resources, and changed state advances its version. Normal capture must validate instead.</summary>
    public static PcFact Repair(PcFact pc, IReadOnlyList<AssemblyTemplate> templates, NetObjectIdAllocator allocator)
    {
        if (templates.Count is < 1 or > AssemblyResources.MaxPerPc) throw new InvalidDataException("Invalid prefab repair size.");
        var old = pc.Resources.ToDictionary(r => (r.Parent, r.Binding, r.NativeType, r.Kind));
        var ids = templates.Select(r => old.TryGetValue((r.Parent, r.Binding, r.NativeType, r.Kind), out var known) ? known.Id : allocator.Allocate()).ToArray();
        NetObjectId[] Links(IReadOnlyList<int> rows)
        {
            if (rows.Count > AssemblyResources.MaxClaimResources || rows.Any(i => i < 0 || i >= ids.Length)) throw new InvalidDataException("Invalid prefab repair reference.");
            return rows.Select(i => ids[i]).OrderBy(i => i.Value).ToArray();
        }
        return pc with { Resources = templates.Select((r, i) =>
        {
            var fact = new AssemblyResource(ids[i], r.Parent, r.Binding, r.NativeType, r.Kind, r.Occupant, r.Open, r.Rotation, r.Offset, Links(r.Guards))
                { Coupled = Links(r.Coupled), IntegralPart = r.IntegralPart };
            if (!old.TryGetValue((r.Parent, r.Binding, r.NativeType, r.Kind), out var prior)) return fact;
            var same = prior.Occupant == fact.Occupant && prior.Open == fact.Open && prior.Rotation == fact.Rotation && prior.Offset == fact.Offset &&
                prior.IntegralPart == fact.IntegralPart && prior.Dependencies.SequenceEqual(fact.Dependencies) && prior.Coupled.SequenceEqual(fact.Coupled);
            return fact with { Version = same ? prior.Version : checked(prior.Version + 1) };
        }).ToArray() };
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var id = NetObjectId.Read(payload);
        if (ctx.Op.Actor != ActorSlot.Host) return CommandVerdict.Reject("Only the host publishes prefab bindings.");
        var pc = _objects.Pcs.SingleOrDefault(p => p.Id == id);
        if (pc == null || pc.Resources.Count != 0 || _activity.Actors.Any(a => a.Pending?.Pc == id))
            return CommandVerdict.Reject("PC unavailable or its assembly is already bound.");
        var templates = _native.Describe(pc);
        if (templates == null) return CommandVerdict.Reject("The PC's native prefab is not ready.");
        var allocator = new NetObjectIdAllocator { Next = _objects.NextId };
        var bound = Build(pc, templates, allocator) with { BuildProfile = _native.Profile(pc) };
        var candidate = new ObjectWorld();
        candidate.Replace(_objects.WorldId, allocator.Next, _objects.Locations, _objects.Pcs.Select(p => p.Id == id ? bound : p), _objects.Retired);
        if (RawManifest(id, _objects.NextId, bound).Length > Protocol.MaxModStateBytes)
            return CommandVerdict.Reject("Prefab manifest exceeds the expanded state limit.");
        return CommandVerdict.Accept;
    }

    public byte[] Canonicalize(in CommandContext ctx, NetDataReader request)
    {
        var id = NetObjectId.Read(request); var pc = _objects.Pcs.Single(p => p.Id == id);
        var allocator = new NetObjectIdAllocator { Next = _objects.NextId };
        var bound = Build(pc, _native.Describe(pc) ?? throw new InvalidDataException("Prefab disappeared."), allocator) with { BuildProfile = _native.Profile(pc) };
        var raw = RawManifest(id, _objects.NextId, bound);
        if (raw.Length > Protocol.MaxModStateBytes) throw new InvalidDataException("Prefab manifest exceeds the expanded state limit.");
        // Relative paths repeat heavily. Keep the normal command-size limit; cap expansion before decoding.
        using var output = new MemoryStream();
        using (var zip = new DeflateStream(output, CompressionLevel.Fastest, true)) zip.Write(raw);
        return output.ToArray();
    }

    private static byte[] RawManifest(NetObjectId id, ulong next, PcFact bound)
    {
        var raw = new NetDataWriter(); id.Write(raw); raw.Put(next); raw.Put(bound.Resources.Count);
        foreach (var r in bound.Resources) AssemblyResources.Write(raw, r);
        ObjectWorld.WriteProfile(raw, bound.BuildProfile); return raw.CopyData();
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        if (ctx.Op.Actor != ActorSlot.Host) throw new InvalidDataException("Prefab binding from a guest.");
        using var input = new MemoryStream(payload.GetRemainingBytes());
        using var zip = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(); var chunk = new byte[4096]; int read;
        while ((read = zip.Read(chunk)) != 0)
        {
            if (output.Length + read > Protocol.MaxModStateBytes) throw new InvalidDataException("Prefab manifest expansion exceeds limits.");
            output.Write(chunk, 0, read);
        }
        var r = new NetDataReader(output.ToArray()); var id = NetObjectId.Read(r); var first = r.GetULong(); var count = r.GetInt();
        if (first != _objects.NextId || count is < 1 or > AssemblyResources.MaxPerPc) throw new InvalidDataException("Invalid prefab allocation.");
        var rows = new AssemblyResource[count]; for (var i = 0; i < count; i++) rows[i] = AssemblyResources.Read(r);
        var profile = ObjectWorld.ReadProfile(r);
        if (r.AvailableBytes != 0 || rows.Where((x, i) => x.Id.Value != first + (ulong)i).Any()) throw new InvalidDataException("Invalid prefab identity order.");
        var pc = _objects.Pcs.Single(p => p.Id == id);
        if (pc.Resources.Count != 0) throw new InvalidDataException("Prefab was already bound.");
        var bound = pc with { Resources = rows, BuildProfile = profile };
        _objects.Replace(_objects.WorldId, first + (ulong)count, _objects.Locations, _objects.Pcs.Select(p => p.Id == id ? bound : p), _objects.Retired);
        _native.Bound(bound);
    }
}

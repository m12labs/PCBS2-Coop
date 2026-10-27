using System.Security.Cryptography;
using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

public enum AssemblyResourceKind : byte { Slot = 1, Screw = 2, Pin = 3, Socket = 4 }

/// <summary>A prefab binding belongs to a physical parent part, never to a SKU or a bare object name.
/// Binding includes the typed-array index or relative hierarchy path with sibling indices. Several slots
/// may contain the same Occupant (for example an AIO and its proxies). Dependencies are shared read guards
/// such as a cover or a neighboring slot; native fit/eligibility is still checked by the gameplay adapter.</summary>
public sealed record AssemblyResource(NetObjectId Id, NetObjectId Parent, string Binding,
    string NativeType, AssemblyResourceKind Kind, NetObjectId Occupant, bool Open, float Rotation, float Offset,
    IReadOnlyList<NetObjectId> Dependencies)
{
    /// <summary>Increment for every accepted occupancy/mechanical/dependency change, including a change back
    /// to an earlier value. This prevents an old callback from surviving an empty/filled/empty cycle.</summary>
    public ulong Version { get; init; }
    /// <summary>Resources changed together, such as a slot's automatically closed clips or linked pins.</summary>
    public IReadOnlyList<NetObjectId> Coupled { get; init; } = Array.Empty<NetObjectId>();
    /// <summary>Integral panels/covers and generated radiator fans may have ephemeral native PartInstances.
    /// Nonempty is the installed child SKU; absence is empty. They are not standalone saved part records.</summary>
    public string IntegralPart { get; init; } = "";
}

public sealed record AssemblyClaimResources(IReadOnlyList<NetObjectId> Writes, IReadOnlyList<NetObjectId> All);

/// <summary>Pure model rules for component reservations. Descendants conflict with removal of their parent,
/// but two slots do not contend merely because they share a motherboard. No Unity pointers enter this model.</summary>
public static class AssemblyResources
{
    public const int MaxPerPc = 4096;
    public const int MaxClaimResources = 512;

    public static IEnumerable<NetObjectId> AllIds(PcFact pc) => pc.Parts.Select(p => p.Id)
        .Concat(pc.Resources.Select(r => r.Id)).Prepend(pc.Id);

    internal static IReadOnlyList<AssemblyResource> Freeze(IReadOnlyList<AssemblyResource> resources) =>
        Array.AsReadOnly(resources.OrderBy(r => r.Id.Value).Select(r => r with
        { Dependencies = Array.AsReadOnly(r.Dependencies.OrderBy(d => d.Value).ToArray()),
            Coupled = Array.AsReadOnly(r.Coupled.OrderBy(d => d.Value).ToArray()) }).ToArray());

    internal static void Validate(PcFact pc, Action<NetObjectId> add)
    {
        if (pc.Resources.Count > MaxPerPc) Bad("Too many assembly resources.");
        if (pc.Resources.Select(r => r.Id).Distinct().Count() != pc.Resources.Count) Bad("Duplicate assembly resource ID.");
        var parts = pc.Parts.Select(p => p.Id).ToHashSet();
        var resources = pc.Resources.ToDictionary(r => r.Id);
        var bindings = new HashSet<(NetObjectId, string)>();
        foreach (var r in pc.Resources)
        {
            add(r.Id);
            if (!parts.Contains(r.Parent) || !bindings.Add((r.Parent, r.Binding)) || !Enum.IsDefined(r.Kind))
                Bad("Invalid assembly parent, binding or kind.");
            Text(r.Binding); Text(r.NativeType);
            if (r.IntegralPart != "")
            {
                Text(r.IntegralPart);
                if (r.Kind != AssemblyResourceKind.Slot || !r.Occupant.IsNone) Bad("Invalid integral case part.");
            }
            if (!float.IsFinite(r.Rotation) || !float.IsFinite(r.Offset)) Bad("Invalid mechanical state.");
            if (r.Kind == AssemblyResourceKind.Slot)
            {
                if ((!r.Occupant.IsNone && !parts.Contains(r.Occupant)) || r.Occupant == r.Parent ||
                    r.Open || r.Rotation != 0 || r.Offset != 0) Bad("Invalid slot occupancy/state.");
            }
            else if (!r.Occupant.IsNone) Bad("A mechanical resource cannot contain a part.");
            if (new[] { r.Dependencies, r.Coupled }.Any(ds => ds.Count > MaxClaimResources || ds.Distinct().Count() != ds.Count ||
                ds.Any(d => d == r.Id || (!parts.Contains(d) && !resources.ContainsKey(d)))))
                Bad("Invalid assembly dependency.");
        }
        // A generated/proxy part can have multiple containing slots; every ownership branch must be acyclic.
        var parents = pc.Resources.Where(r => !r.Occupant.IsNone).GroupBy(r => r.Occupant)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Parent).Distinct().ToArray());
        var visiting = new HashSet<NetObjectId>(); var done = new HashSet<NetObjectId>();
        void Visit(NetObjectId id)
        {
            if (done.Contains(id)) return;
            if (!visiting.Add(id)) Bad("Cyclic assembly parent graph.");
            if (parents.TryGetValue(id, out var owners)) foreach (var owner in owners) Visit(owner);
            visiting.Remove(id); done.Add(id);
        }
        foreach (var id in parts) Visit(id);
    }

    /// <summary>Include occupancy aliases, descendants and dependency guards atomically. A child edit does
    /// not acquire its parent, so unrelated children can be edited at the same time.</summary>
    public static NetObjectId[] Expand(PcFact pc, IEnumerable<NetObjectId> targets) => Plan(pc, targets).All.ToArray();

    public static AssemblyClaimResources Plan(PcFact pc, IEnumerable<NetObjectId> targets)
    {
        var writes = Structure(pc, targets, coupled: true);
        // A blocker is read, not edited. Do not recursively follow its own blockers: those describe a
        // different gesture and can otherwise join most of the PC into one accidental reservation.
        var guards = Structure(pc, pc.Resources.Where(r => writes.Contains(r.Id)).SelectMany(r => r.Dependencies), coupled: false);
        var all = writes.Union(guards).OrderBy(id => id.Value).ToArray();
        if (all.Length > MaxClaimResources) Bad("Edit dependency set exceeds the claim limit.");
        return new(Array.AsReadOnly(writes.OrderBy(id => id.Value).ToArray()), Array.AsReadOnly(all));
    }

    private static HashSet<NetObjectId> Structure(PcFact pc, IEnumerable<NetObjectId> targets, bool coupled)
    {
        var parts = pc.Parts.Select(p => p.Id).ToHashSet();
        var resources = pc.Resources.ToDictionary(r => r.Id);
        var occupied = pc.Resources.Where(r => !r.Occupant.IsNone).ToLookup(r => r.Occupant);
        var children = pc.Resources.ToLookup(r => r.Parent);
        var held = new HashSet<NetObjectId>(); var pending = new Queue<NetObjectId>(targets);
        while (pending.TryDequeue(out var id))
        {
            if (!parts.Contains(id) && !resources.ContainsKey(id)) Bad("Unknown or retired edit resource.");
            if (!held.Add(id)) continue;
            if (held.Count > MaxClaimResources) Bad("Edit dependency set exceeds the claim limit.");
            if (resources.TryGetValue(id, out var resource))
            {
                if (!resource.Occupant.IsNone) pending.Enqueue(resource.Occupant);
                if (coupled) foreach (var dependency in resource.Coupled) pending.Enqueue(dependency);
            }
            // A physical part claims every containing slot, including multi-slot/proxy coverage.
            foreach (var slot in occupied[id]) pending.Enqueue(slot.Id);
            // Editing/removing a parent also guards its complete child graph. Sibling edits never acquire
            // the parent itself, so they remain independent of one another.
            if (parts.Contains(id)) foreach (var child in children[id]) pending.Enqueue(child.Id);
        }
        return held;
    }

    public static bool Conflicts(PcFact pc, IEnumerable<NetObjectId> left, IEnumerable<NetObjectId> right)
    {
        var a = Plan(pc, left); var b = Plan(pc, right);
        return a.Writes.Intersect(b.All).Any() || b.Writes.Intersect(a.All).Any();
    }

    /// <summary>Capture exactly the acquired resources and their parent incarnations. A slot emptied/refilled,
    /// a mechanic changed, or a replaced parent invalidates delayed work, even when the SKU is identical.
    /// An unrelated edit on the same PC does not invalidate it.</summary>
    public static string Stamp(PcFact pc, IEnumerable<NetObjectId> targets)
    {
        var ids = Expand(pc, targets).ToHashSet();
        var parents = pc.Resources.Select(r => (Child: r.Id, r.Parent))
            .Concat(pc.Resources.Where(r => !r.Occupant.IsNone).Select(r => (Child: r.Occupant, r.Parent)))
            .ToLookup(r => r.Child, r => r.Parent);
        var pending = new Queue<NetObjectId>(ids);
        while (pending.TryDequeue(out var current))
            foreach (var parent in parents[current]) if (ids.Add(parent)) pending.Enqueue(parent);
        var parts = pc.Parts.ToDictionary(p => p.Id); var resources = pc.Resources.ToDictionary(r => r.Id);
        var links = pc.Links.ToLookup(l => l.Part, l => l.Path);
        var w = new NetDataWriter(); pc.Id.Write(w);
        foreach (var id in ids.OrderBy(i => i.Value))
        {
            id.Write(w);
            if (parts.TryGetValue(id, out var part))
            {
                w.Put((byte)0); w.Put(part.Sku); w.Put(part.NativeType);
                var paths = links[id].OrderBy(p => p, StringComparer.Ordinal).ToArray();
                w.Put(paths.Length); foreach (var path in paths) w.Put(path);
            }
            else Write(w, resources[id]);
        }
        return Convert.ToHexString(SHA256.HashData(w.CopyData()));
    }

    internal static void Write(NetDataWriter w, AssemblyResource r)
    {
        w.Put((byte)r.Kind); r.Id.Write(w); r.Parent.Write(w); w.Put(r.Binding); w.Put(r.NativeType); w.Put(r.Version);
        r.Occupant.Write(w); w.Put(r.Open); w.Put(r.Rotation); w.Put(r.Offset);
        w.Put(r.Dependencies.Count); foreach (var d in r.Dependencies) d.Write(w);
        w.Put(r.Coupled.Count); foreach (var d in r.Coupled) d.Write(w);
        w.Put(r.IntegralPart);
    }

    internal static AssemblyResource Read(NetDataReader r)
    {
        var kind = (AssemblyResourceKind)r.GetByte(); var id = NetObjectId.Read(r); var parent = NetObjectId.Read(r);
        var binding = r.GetString(1024); var type = r.GetString(1024); var version = r.GetULong(); var occupant = NetObjectId.Read(r);
        var open = r.GetBool(); var rotation = r.GetFloat(); var offset = r.GetFloat(); var n = r.GetInt();
        if (n is < 0 or > MaxClaimResources) Bad("Invalid dependency count.");
        var dependencies = new NetObjectId[n]; for (var i = 0; i < n; i++) dependencies[i] = NetObjectId.Read(r);
        var c = r.GetInt(); if (c is < 0 or > MaxClaimResources) Bad("Invalid coupled resource count.");
        var linked = new NetObjectId[c]; for (var i = 0; i < c; i++) linked[i] = NetObjectId.Read(r);
        return new(id, parent, binding, type, kind, occupant, open, rotation, offset, dependencies)
            { Version = version, Coupled = linked, IntegralPart = r.GetString(1024) };
    }

    private static void Text(string s)
    { if (string.IsNullOrEmpty(s) || s.Length > 1023 || s.Any(char.IsControl)) Bad("Invalid assembly binding text."); }
    private static void Bad(string message) => throw new InvalidDataException(message);
}

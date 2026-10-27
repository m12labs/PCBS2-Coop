using HarmonyLib;
using LiteNetLib.Utils;
using PCBS2Coop.Network;
using System.Security.Cryptography;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>Main-thread prefab bindings. Pointers validate one local projection only; durable identity is
/// the physical owner plus a path inside that owner's prefab, excluding workshop/bench/load-root names.</summary>
internal sealed class NativeAssembly : IAssemblyNative
{
    private sealed class Row
    {
        public readonly Component Native;
        public readonly NetObjectId Parent, Occupant;
        public readonly string Key;
        public readonly AssemblyResourceKind Kind;
        public readonly long OccupantMesh;
        public readonly bool Open;
        public readonly float Rotation, Offset;
        public string IntegralPart = "";
        public Row(Component native, Transform root, NetObjectId parent, string key, AssemblyResourceKind kind,
            NetObjectId occupant, long occupantMesh, bool open, float rotation, float offset)
        { Native = native; Parent = parent; Key = key; Kind = kind; Occupant = occupant; OccupantMesh = occupantMesh; Open = open; Rotation = rotation; Offset = offset; }
    }
    private sealed class Binding
    {
        public readonly Case Mesh;
        public readonly Dictionary<NetObjectId, (Component Native, long Occupant)> Resources;
        public Binding(Case mesh, Dictionary<NetObjectId, (Component Native, long Occupant)> resources) { Mesh = mesh; Resources = resources; }
    }
    private readonly NativeObjectState _objects;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<NetObjectId, Binding> _bindings = new();
    private readonly Dictionary<NetObjectId, IReadOnlyList<AssemblyTemplate>> _prepared = new();
    private readonly Dictionary<NetObjectId, PcBuildProfile> _preparedProfiles = new();
    private readonly HashSet<NetObjectId> _faults = new();
    private static readonly Dictionary<long, ComponentPC> Previews = new();
    private static int _previewDepth;
    public AssemblyManifest Handler { get; private set; }

    public NativeAssembly(NativeObjectState objects, ActivityWorld activity = null)
    { _objects = objects; if (activity != null) Handler = new(objects.Model, activity, this); }
    internal void Configure(ActivityWorld activity) => Handler = new(_objects.Model, activity, this);
    private void MainThread() { if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Assembly bindings require the main thread."); }
    public void Reset() { MainThread(); _bindings.Clear(); _prepared.Clear(); _preparedProfiles.Clear(); _faults.Clear(); }
    public void Forget(NetObjectId id) { _bindings.Remove(id); _prepared.Remove(id); _preparedProfiles.Remove(id); }

    private static bool Preview(ComponentPC c) => c != null && Previews.TryGetValue(c.Pointer.ToInt64(), out var held) && held != null;
    internal static bool IsPreview(ComponentPC c) => Preview(c);
    internal static bool HasPreview
    {
        get
        {
            foreach (var key in Previews.Where(p => p.Value == null).Select(p => p.Key).ToArray()) Previews.Remove(key);
            return Previews.Values.Any(c => c != null);
        }
    }

    internal IReadOnlyList<AssemblyTemplate> Describe(PcFact pc, Case mesh, ComputerSave save)
    {
        MainThread();
        return Scan(pc, mesh, save).Templates;
    }
    public IReadOnlyList<AssemblyTemplate> Describe(PcFact pc) => _prepared.GetValueOrDefault(pc.Id);
    public PcBuildProfile Profile(PcFact pc) => _preparedProfiles.GetValueOrDefault(pc.Id);
    public void Bound(PcFact pc)
    {
        _prepared.Remove(pc.Id);
        _preparedProfiles.Remove(pc.Id);
        if (_objects.Projections.TryGet(pc.Id, out var mesh) && mesh != null)
            Bind(pc, mesh, _objects.RecordFor(pc.Id));
    }

    /// <summary>Explicit host publication boundary, never a SaveComputer polling loop. Unknown manifests
    /// stay unknown for record-only PCs until their first native load; existing manifests survive storage.</summary>
    internal void BindMissing()
    {
        MainThread(); var c = CoopController.Instance;
        if (c?.Session.Role != Role.Host || !c.WorldReady || !_objects.Initialized || !CanCapture || c.OutOfSync != null) return;
        var missing = _objects.Model.Pcs.Where(p => p.Resources.Count == 0 && _objects.Projections.TryGet(p.Id, out var mesh) && mesh != null).Select(p => p.Id).ToArray();
        if (missing.Length == 0) return;
        c.Lifecycle?.ExtendHost(); // explicit first-projection load growth, before its manifest is published
        foreach (var id in missing)
        {
            var pc = _objects.Model.Pcs.Single(p => p.Id == id);
            if (!_objects.Projections.TryGet(pc.Id, out var mesh) || mesh == null) continue;
            var record = _objects.CaptureRecord(pc.Id);
            NativeObjectState.CheckParts(pc, record);
            _prepared[pc.Id] = Describe(pc, mesh, record);
            _preparedProfiles[pc.Id] = CaptureProfile(pc, mesh, record);
            if (Handler == null || !c.Session.World.Submit(CommandTypes.AssemblyManifest, AssemblyManifest.Bind(pc.Id), out _))
                throw new InvalidDataException("The prefab manifest could not be published.");
            if (_objects.Model.Pcs.Single(p => p.Id == pc.Id).Resources.Count == 0)
                throw new InvalidDataException("The host refused the prefab manifest; it was not allocated.");
        }
    }

    internal void Update()
    {
        MainThread(); var c = CoopController.Instance;
        if (c?.WorldReady != true || !_objects.Initialized || HasPreview) return;
        foreach (var pc in _objects.Model.Pcs.Where(p => p.Resources.Count != 0))
        {
            if (_faults.Contains(pc.Id) || !_objects.Projections.TryGet(pc.Id, out var mesh) || mesh == null ||
                (_bindings.TryGetValue(pc.Id, out var prior) && prior.Mesh != null && prior.Mesh.Pointer == mesh.Pointer)) continue;
            try
            {
                var record = _objects.CaptureRecord(pc.Id); NativeObjectState.CheckParts(pc, record); Bind(pc, mesh, record);
            }
            catch (Exception e) { _faults.Add(pc.Id); c.ProjectionFailed($"PC {pc.Id}'s assembly could not be rebound after its load", e); }
        }
        try { BindMissing(); }
        catch (Exception e) { c.ProjectionFailed("A loaded PC's assembly manifest could not be published", e); }
    }

    internal HashSet<NetObjectId> ReplacedParts(PcFact pc, Case mesh)
    {
        var result = new HashSet<NetObjectId>();
        if (mesh == null || !_bindings.TryGetValue(pc.Id, out var binding) || binding.Mesh == null || binding.Mesh.Pointer != mesh.Pointer) return result;
        foreach (var r in pc.Resources.Where(r => !r.Occupant.IsNone))
            if (binding.Resources.TryGetValue(r.Id, out var old))
            {
                var slot = old.Native == null ? null : old.Native.TryCast<Slot>(); var component = slot?.GetComponent();
                if (slot == null || (component == null ? 0 : component.Pointer.ToInt64()) != old.Occupant) result.Add(r.Occupant);
            }
        return result;
    }

    internal static void CheckStable()
    {
        if (HasPreview) throw new InvalidDataException("Put the selected component away before saving or joining.");
        if (GameController.Get()?.GetCurrentState()?.TryCast<InstallingPartState>() != null)
            throw new InvalidDataException("A component edit is still running.");
    }
    internal static bool CanCapture => !HasPreview && GameController.Get()?.GetCurrentState()?.TryCast<InstallingPartState>() == null;

    /// <summary>C4b02 N3: native load closes clips over installed RAM after reopening saved pins. /sync uses
    /// that persisted result as its reference, so close those clips synchronously before its repair capture.</summary>
    internal bool PrepareReload()
    {
        MainThread(); if (!CanCapture) return false;
        var pins = new Dictionary<long, Pin>();
        foreach (var pc in _objects.Model.Pcs)
            if (_objects.Projections.TryGet(pc.Id, out var mesh) && mesh != null)
                foreach (var ram in mesh.GetComponentsInChildren<RAMSlot>(true))
                    if (ram.ramSlot?.GetComponent() != null && ram.pins != null)
                        foreach (var pin in ram.pins) if (pin != null) pins[pin.Pointer.ToInt64()] = pin;
        if (pins.Values.Any(pin => pin.IsAnimating())) return false;
        foreach (var pin in pins.Values.Where(pin => pin.IsOpen())) NativeProjection.Run(() => pin.Close());
        return true;
    }

    internal void CheckMeshes()
    {
        MainThread();
        CheckStable();
        foreach (var pc in _objects.Model.Pcs)
            if (_objects.Projections.TryGet(pc.Id, out var mesh) && mesh != null && pc.Resources.Count != 0)
                Validate(pc, mesh, _objects.RecordFor(pc.Id), rebind: false);
    }

    internal void Validate(PcFact pc, Case mesh, ComputerSave save, bool rebind)
    {
        MainThread();
        if (mesh == null || pc.Resources.Count == 0) return;
        var scan = Scan(pc, mesh, save);
        Check(pc, scan.Templates);
        CheckProfile(pc, save);
        if (!rebind && _bindings.TryGetValue(pc.Id, out var prior) && prior.Mesh != null && prior.Mesh.Pointer == mesh.Pointer)
        {
            var byKey = scan.Rows.ToDictionary(r => (r.Parent, r.Key));
            foreach (var r in pc.Resources)
                if (prior.Resources.TryGetValue(r.Id, out var old) && (old.Native == null ||
                    old.Native.Pointer != byKey[(r.Parent, r.Binding)].Native.Pointer || old.Occupant != byKey[(r.Parent, r.Binding)].OccupantMesh))
                    throw new InvalidDataException($"PC {pc.Id}: physical instance changed at {r.Binding}, including a possible same-SKU replacement.");
        }
    }

    internal void Bind(PcFact pc, Case mesh, ComputerSave save)
    {
        MainThread(); if (mesh == null || pc.Resources.Count == 0) return;
        var scan = Scan(pc, mesh, save); Check(pc, scan.Templates); CheckProfile(pc, save);
        var rows = scan.Rows.ToDictionary(r => (r.Parent, r.Key));
        _bindings[pc.Id] = new(mesh, pc.Resources.ToDictionary(r => r.Id,
            r => (rows[(r.Parent, r.Binding)].Native, rows[(r.Parent, r.Binding)].OccupantMesh)));
    }

    private static void Check(PcFact pc, IReadOnlyList<AssemblyTemplate> actual)
    {
        if (pc.Resources.Count != actual.Count) throw new InvalidDataException($"PC {pc.Id}: native assembly manifest changed.");
        var byKey = pc.Resources.ToDictionary(r => (r.Parent, r.Binding));
        var ids = actual.Select(r => byKey.TryGetValue((r.Parent, r.Binding), out var known) ? known.Id : default).ToArray();
        for (var i = 0; i < actual.Count; i++)
        {
            var a = actual[i];
            if (!byKey.TryGetValue((a.Parent, a.Binding), out var expected) || expected.NativeType != a.NativeType || expected.Kind != a.Kind ||
                expected.Occupant != a.Occupant || expected.Open != a.Open || expected.Rotation != a.Rotation || expected.Offset != a.Offset || expected.IntegralPart != a.IntegralPart ||
                !expected.Dependencies.SequenceEqual(a.Guards.Select(j => ids[j]).OrderBy(x => x.Value)) ||
                !expected.Coupled.SequenceEqual(a.Coupled.Select(j => ids[j]).OrderBy(x => x.Value)))
                throw new InvalidDataException($"PC {pc.Id}: unconfirmed assembly state at {a.Binding}.");
        }
    }

    internal static PcBuildProfile CaptureProfile(PcFact pc, Case mesh, ComputerSave save)
    {
        var connectors = mesh.GetComponentsInChildren<PowerConnector>(true).Select(c => c.id)
            .Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        return new(Signature(pc, save, connectors), connectors);
    }

    internal static void CheckProfile(PcFact pc, ComputerSave save)
    {
        if (pc.BuildProfile is { } profile && Signature(pc, save, profile.InternalConnectors) != profile.Signature)
            throw new InvalidDataException($"PC {pc.Id}: unconfirmed orientation, case membership or internal cable connections.");
    }

    private static string Signature(PcFact pc, ComputerSave save, IReadOnlyList<string> internalConnectors)
    {
        var w = new NetDataWriter(); var graph = NativeObjectState.Parts(save);
        foreach (var part in pc.Parts.OrderBy(p => p.Id.Value))
        {
            var path = pc.Links.First(l => l.Part == part.Id).Path;
            if (!graph.TryGetValue(path, out var native)) throw new InvalidDataException("Missing native assembly profile part.");
            part.Id.Write(w); w.Put(native.GetSlotOrientation()); w.Put(native.m_flipped);
        }
        void Flags(System.Collections.IEnumerable flags)
        {
            if (flags == null) { w.Put(-1); return; }
            var values = new List<bool>(); foreach (var value in flags) values.Add((bool)value);
            w.Put(values.Count); foreach (var value in values) w.Put(value);
        }
        Flags(save.casePciCovers); Flags(save.miscCaseSlots); Flags(save.optionalCaseSlots);
        var bespoke = new List<string>(); if (save.m_bespokeCaseSlots != null) foreach (var name in save.m_bespokeCaseSlots) bespoke.Add(name);
        w.Put(bespoke.Count); foreach (var name in bespoke.OrderBy(n => n, StringComparer.Ordinal)) w.Put(name);
        var connectors = internalConnectors.ToHashSet(StringComparer.Ordinal);
        var cables = save.cablesInstalled == null ? Array.Empty<CableSave>() : save.cablesInstalled
            .Where(c => connectors.Contains(c.m_from) && connectors.Contains(c.m_to))
            .OrderBy(c => c.m_from, StringComparer.Ordinal).ThenBy(c => c.m_to, StringComparer.Ordinal).ThenBy(c => c.m_desc, StringComparer.Ordinal).ToArray();
        w.Put(cables.Length);
        foreach (var cable in cables)
        {
            w.Put(cable.m_from); w.Put(cable.m_to); w.Put(cable.m_desc); w.Put(cable.m_connectorType);
            w.Put(cable.m_isNonModularConnector); w.Put(cable.m_playerChoice);
            w.Put(cable.m_pinMaterialIndices?.Length ?? -1);
            if (cable.m_pinMaterialIndices != null) foreach (var index in cable.m_pinMaterialIndices) w.Put(index);
        }
        return Convert.ToHexString(SHA256.HashData(w.CopyData()));
    }

    private static (List<Row> Rows, IReadOnlyList<AssemblyTemplate> Templates) Scan(PcFact pc, Case mesh, ComputerSave save)
    {
        if (mesh == null || !mesh || save?.caseID == null) throw new InvalidDataException("A native prefab/record is missing.");
        var parts = NativeObjectState.Parts(save);
        var identities = new Dictionary<long, NetObjectId>();
        foreach (var link in pc.Links)
            if (parts.TryGetValue(link.Path, out var part)) identities[part.Pointer.ToInt64()] = link.Part;
        var caseId = pc.Links.Single(l => l.Path == "caseID").Part;
        var caseSku = pc.Parts.Single(p => p.Id == caseId).Sku;
        var nativeSlots = mesh.GetComponentsInChildren<Slot>(true).ToArray();
        var componentSlots = new Dictionary<long, Slot>();
        foreach (var observed in nativeSlots)
        {
            var component = observed.GetComponent();
            if (component != null) componentSlots.TryAdd(component.Pointer.ToInt64(), observed);
        }
        Slot Origin(ComponentPC component) => component != null && componentSlots.TryGetValue(component.Pointer.ToInt64(), out var observed) ? observed : component?.Slot;
        string Integral(ComponentPC component)
        {
            if (component == null) return "";
            var part = component?.GetPartInstance(); var sku = part?.GetPartId();
            var origin = Origin(component);
            if ((part != null && identities.ContainsKey(part.Pointer.ToInt64())) || origin?.m_slotType == Slot.SlotType.BLOCKER_DUMMY) return "";
            if (origin?.m_slotType is Slot.SlotType.CASE_PART_REQUIRED or Slot.SlotType.CASE_PART_OPTIONAL or Slot.SlotType.PCI_COVER)
                return sku ?? "native:" + component.GetIl2CppType().FullName;
            if (sku == null) return "";
            if (sku.StartsWith(caseSku + "/", StringComparison.Ordinal)) return sku;
            // Default radiator fans can be generated from the parent rather than stored in FanId. Their
            // physical binding is the typed child slot under that radiator, not a guessed same-SKU record.
            var slot = origin;
            if (slot != null && slot.m_slotType == Slot.SlotType.CASE_COOLER)
                for (var p = slot.transform.parent; p != null && p.Pointer != mesh.transform.Pointer; p = p.parent)
                {
                    var owner = p.GetComponent<ComponentPC>();
                    if (owner == null) continue;
                    var nativePart = owner.GetPartInstance();
                    if (owner.GetComponent<Radiator>() != null && nativePart != null && identities.ContainsKey(nativePart.Pointer.ToInt64()) &&
                        owner.SubSlots != null && owner.SubSlots.Any(s => s != null && s.Pointer == slot.Pointer)) return sku;
                    break;
                }
            return "";
        }
        NetObjectId Part(ComponentPC component, bool strict = true)
        {
            if (component == null || Preview(component)) return default;
            var real = component.m_isProxy ? component.GetProxiedComponent() : component;
            var part = real?.GetPartInstance();
            if (part != null && identities.TryGetValue(part.Pointer.ToInt64(), out var id)) return id;
            if (Integral(component) != "") return default;
            // Native blocker dummies are not physical inventory parts. Other unmapped components are faults,
            // never rebound by SKU/path (that would adopt an unconfirmed replacement).
            if (Origin(component)?.m_slotType == Slot.SlotType.BLOCKER_DUMMY || !strict) return default;
            throw new InvalidDataException($"PC {pc.Id}: a native component is not in the accepted part graph ({component.name}, slot {Origin(component)?.m_slotType}, SKU {part?.GetPartId()}).");
        }
        (NetObjectId Id, Transform Root) Owner(Transform node)
        {
            for (var p = node.parent; p != null && p.Pointer != mesh.transform.Pointer; p = p.parent)
            {
                var c = p.GetComponent<ComponentPC>();
                if (c != null && !Preview(c)) { var id = Part(c, strict: false); if (!id.IsNone) return (id, c.transform); }
            }
            return (caseId, mesh.transform);
        }
        var rows = new List<Row>();
        void Add(Component native, AssemblyResourceKind kind, NetObjectId occupant, long occupantMesh, bool open = false, float rotation = 0, float offset = 0)
        {
            var owner = Owner(native.transform);
            var indices = kind == AssemblyResourceKind.Slot ? native.gameObject.GetComponents<Slot>().Select(c => c.Pointer).ToArray()
                : kind == AssemblyResourceKind.Screw ? native.gameObject.GetComponents<Screw>().Select(c => c.Pointer).ToArray()
                : native.gameObject.GetComponents<CPin>().Select(c => c.Pointer).ToArray();
            var key = kind + ":" + RelativePath(native.transform, owner.Root) + "#" + Array.IndexOf(indices, native.Pointer);
            rows.Add(new(native, owner.Root, owner.Id, key, kind, occupant, occupantMesh, open, rotation, offset));
        }
        foreach (var slot in nativeSlots)
        {
            var c = slot.GetComponent();
            if (Preview(c)) throw new InvalidDataException("A component preview is still present.");
            Add(slot, AssemblyResourceKind.Slot, Part(c), c == null ? 0 : c.Pointer.ToInt64());
            rows[^1].IntegralPart = Integral(c);
        }
        foreach (var pin in mesh.GetComponentsInChildren<CPin>(true))
        {
            if (pin.IsAnimating()) throw new InvalidDataException("A latch/socket is still animating.");
            Add(pin, pin.TryCast<AM3Slot>() != null || pin.TryCast<LGASlot>() != null ? AssemblyResourceKind.Socket : AssemblyResourceKind.Pin, default, 0, pin.IsOpen());
        }
        foreach (var screw in mesh.GetComponentsInChildren<Screw>(true))
            Add(screw, AssemblyResourceKind.Screw, default, 0, rotation: screw.m_rotation, offset: screw.m_offset);
        rows = rows.OrderBy(r => r.Parent.Value).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
        var indicesByPointer = rows.Select((r, i) => (r.Native.Pointer, Index: i)).ToDictionary(r => r.Pointer, r => r.Index);
        var ramGroups = mesh.GetComponentsInChildren<RAMSlot>(true).Where(ram => ram.ramSlot != null).ToLookup(ram => ram.ramSlot.Pointer);
        int[] Resolve(IEnumerable<Component> references, int self)
        {
            var found = new HashSet<int>();
            void AddReference(Component c)
            {
                if (c == null) return;
                var ram = c.TryCast<RAMSlot>();
                if (ram != null)
                {
                    AddReference(ram.ramSlot);
                    if (ram.pins != null) foreach (var pin in ram.pins) AddReference(pin);
                    return;
                }
                if (!indicesByPointer.TryGetValue(c.Pointer, out var index))
                    throw new InvalidDataException($"Blocker {c.GetIl2CppType().FullName} '{c.name}' is outside this PC's resource manifest.");
                if (index != self) found.Add(index);
            }
            foreach (var c in references) AddReference(c);
            return found.OrderBy(i => i).ToArray();
        }
        var templates = rows.Select((r, i) =>
        {
            var guards = new List<Component>(); var coupled = new List<Component>();
            var slot = r.Native.TryCast<Slot>(); var pin = r.Native.TryCast<Pin>(); var screw = r.Native.TryCast<Screw>();
            if (slot != null)
            {
                if (slot.blockedBy != null) foreach (var c in slot.blockedBy) guards.Add(c);
                if (slot.m_dynamicBlockedBy != null) foreach (var c in slot.m_dynamicBlockedBy) if (c != null && c.Slot != null) guards.Add(c.Slot);
                if (slot.m_dynamicComponentCollidesWith != null) foreach (var c in slot.m_dynamicComponentCollidesWith) if (c != null && c.Slot != null) guards.Add(c.Slot);
                if (slot.closePins != null) coupled.AddRange(slot.closePins.Select(c => (Component)c));
                if (slot.mustBeOpenPins != null) guards.AddRange(slot.mustBeOpenPins.Select(c => (Component)c));
                if (slot.DynamicClosePins != null) foreach (var c in slot.DynamicClosePins) coupled.Add(c);
                if (slot.DynamicMustBeOpenPins != null) foreach (var c in slot.DynamicMustBeOpenPins) guards.Add(c);
                if (slot.fillSlots != null) coupled.AddRange(slot.fillSlots.Select(c => (Component)c));
                foreach (var ram in ramGroups[slot.Pointer])
                    if (ram.pins != null) foreach (var clip in ram.pins) coupled.Add(clip);
                var component = slot.GetComponent();
                if (component != null && !Preview(component))
                    foreach (var attached in component.GetScrews()) coupled.Add(attached);
            }
            if (pin != null)
            {
                if (pin.blockedBy != null) guards.AddRange(pin.blockedBy.Select(c => (Component)c));
                if (pin.childPins != null) coupled.AddRange(pin.childPins.Select(c => (Component)c));
            }
            if (screw?.m_requiresStandOff != null)
            {
                // This field normally references the shared Standoff prefab asset, not one of its live
                // clones. Standoffs are automatic in the board edit; guard the parent's actual instances.
                if (indicesByPointer.ContainsKey(screw.m_requiresStandOff.Pointer)) coupled.Add(screw.m_requiresStandOff);
                else coupled.AddRange(rows.Where(x => x.Parent == r.Parent && x.Native.TryCast<Screw>()?.m_isStandOff == true).Select(x => x.Native));
            }
            // Case blockers stand for the root's physical part. Their whole-PC eligibility is checked by the
            // adapter; there is no separate case CBlocker resource to confuse with its installed descendants.
            guards.RemoveAll(g => g != null && g.TryCast<Case>() != null);
            return new AssemblyTemplate(r.Parent, r.Key, r.Native.GetIl2CppType().FullName, r.Kind, r.Occupant, r.Open, r.Rotation, r.Offset,
                Resolve(guards, i), Resolve(coupled, i)) { IntegralPart = r.IntegralPart };
        }).ToArray();
        return (rows, templates);
    }

    private static string RelativePath(Transform node, Transform root)
    {
        var rows = new List<string>();
        for (var t = node; t != null && t.Pointer != root.Pointer; t = t.parent) rows.Add(t.name + "[" + t.GetSiblingIndex() + "]");
        rows.Reverse(); return rows.Count == 0 ? "." : string.Join("/", rows);
    }

    [HarmonyPatch(typeof(Slot), nameof(Slot.InstantiatePreloadedPart))]
    internal static class PreviewTrace
    {
        private static void Prefix(bool Placeholder, out bool __state) { __state = Placeholder || _previewDepth > 0; if (__state) _previewDepth++; }
        private static void Postfix(ComponentPC __result, bool __state)
        { if (__state && __result != null) Previews[__result.Pointer.ToInt64()] = __result; }
        private static void Finalizer(bool __state) { if (__state) _previewDepth--; }
    }
}

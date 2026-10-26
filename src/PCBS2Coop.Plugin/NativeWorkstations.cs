using HarmonyLib;
using LiteNetLib.Utils;
using PCBS2Coop.Network;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// Native side of workstation peripherals (task 3g). A slot is the game's bench ID (<c>PerifBenchID</c>) and its
/// position in <c>SaveLoadSystem.GetBenchPerifSlots()</c>; its fact is the installed peripheral's asset path, which is
/// what the native save writes. Every swap (<c>SwapPerif</c> on each slot type) becomes a host-ordered request; after
/// acceptance the requester runs that same native swap and the other game runs it for the same slot, one at a time per
/// slot (a swap that arrives while the slot is still loading waits for it). Also refuses worlds with packages or
/// physical notes, which co-op does not share yet (owner decision, October 26, 2026).
/// </summary>
internal sealed class NativeWorkstations : IPeripheralNative, IWorldState
{
    private const float ProjectionTimeout = 30f;
    private sealed class Request
    {
        public string What;
        public PartInstance Part;
    }
    private readonly CoopController _coop;
    private readonly WorldSync _world;
    private readonly Dictionary<OperationId, Request> _pending = new();
    private readonly Dictionary<(int Bench, int Slot), (string Path, float Until)> _loading = new();
    private readonly Dictionary<(int Bench, int Slot), (string Part, string Path, Request Request)> _waiting = new();
    private Request _submitting;
    public WorkstationWorld Model { get; }
    public string Name => Model.Name;

    public NativeWorkstations(CoopController coop, WorldSync world)
    {
        _coop = coop; _world = world;
        Model = new WorkstationWorld(coop.Objects.Model, coop.Activity, this);
        world.Accepted += (OperationId op, ulong revision) => _pending.Remove(op);
        world.Rejected += OnRejected;
        world.GenerationStarted += (uint generation, string reason) => { _pending.Clear(); _waiting.Clear(); };
    }

    private bool Active => _world.Admitted && _world.Divergence == null && Model.Bound && _coop.Activity.Get(_world.Local) != null;
    internal bool HasPending => _pending.Count > 0;
    private static SaveLoadSystem Saves => Object.FindObjectOfType<SaveLoadSystem>() ?? throw new InvalidDataException("Save system unavailable.");

    // ---------------- native slot table ----------------

    /// <summary>Every peripheral slot in the game's own order, keyed by bench ID and position.</summary>
    private static List<((int Bench, int Slot) Key, PeripheralSlot Slot)> Table()
    {
        var table = Saves.GetBenchPerifSlots() ?? throw new InvalidDataException("The workstation peripheral table is unavailable.");
        var result = new List<((int, int), PeripheralSlot)>();
        foreach (var entry in table)
        {
            var slots = entry.Value;
            if (slots == null) continue;
            for (var i = 0; i < slots.Length; i++) result.Add((((int)entry.Key, i), slots[i]));
        }
        return result;
    }

    private static PeripheralSlot SlotAt(int bench, int slot) => Table().FirstOrDefault(x => x.Key == (bench, slot)).Slot;
    private static string PathOf(PeripheralSlot slot)
    {
        var p = slot?.GetPeripheral();
        return p != null && p ? p.AssetPath ?? "" : "";
    }
    private static string Describe(PeripheralSlot slot) => slot == null ? "missing" : $"{slot.name} ({slot.Type})";

    /// <summary>The slot is still showing an earlier load: the game's own first spawn, or a swap this mod ran.</summary>
    private bool Loading((int, int) key, PeripheralSlot slot)
    {
        if (slot.m_loadingPerif) return true;
        if (!_loading.TryGetValue(key, out var load)) return false;
        if (PathOf(slot) == load.Path) { _loading.Remove(key); return false; }
        if (Time.realtimeSinceStartup < load.Until) return true;
        Plugin.Logger.LogWarning($"[workstations] {Describe(slot)} did not finish loading '{load.Path}' (shows '{PathOf(slot)}').");
        _loading.Remove(key);
        return false;
    }

    /// <summary>No swap is loading or waiting here (a snapshot waits for this instead of failing).</summary>
    internal bool Settled
    {
        get
        {
            if (_waiting.Count > 0) return false;
            foreach (var key in _loading.Keys.ToArray())
                if (SlotAt(key.Bench, key.Slot) is { } slot && Loading(key, slot)) return false;
                else _loading.Remove(key);
            return true;
        }
    }

    /// <summary>Host, at a generation start: bind the slot table from the loaded world. A restored state (a paired save)
    /// must match it exactly; a save from before 0.10 has none and is bound from the world.</summary>
    internal void CaptureHost()
    {
        var facts = Read(out var loading);
        if (loading != null) throw new InvalidDataException($"Peripheral {loading} is still loading.");
        if (Model.Bound && Difference(facts) is { } diff) throw new InvalidDataException("The saved peripherals don't match the loaded world: " + diff);
        Model.Replace(facts);
        Plugin.Logger.LogInfo($"[workstations] bound {facts.Count} peripheral slots ({facts.Count(x => x.Path != "")} with a peripheral).");
    }

    /// <summary>Before a host save or snapshot: the native world must show exactly the accepted peripherals.</summary>
    internal void CheckNative()
    {
        if (!Model.Bound) return;
        var facts = Read(out var loading);
        if (loading != null || _waiting.Count > 0) throw new InvalidOperationException("A peripheral is still loading; wait before saving.");
        if (Difference(facts) is { } diff) throw new InvalidDataException("The game's peripherals differ from the shared world: " + diff);
    }

    private List<PeripheralFact> Read(out string loading)
    {
        loading = null; var facts = new List<PeripheralFact>();
        foreach (var (key, slot) in Table())
        {
            if (slot == null) throw new InvalidDataException($"Peripheral slot {key.Bench}/{key.Slot} is missing.");
            if (Loading(key, slot)) loading ??= Describe(slot);
            facts.Add(new(key.Bench, key.Slot, PathOf(slot)));
        }
        return facts;
    }

    private string Difference(IReadOnlyCollection<PeripheralFact> native)
    {
        var model = Model.Slots;
        if (native.Count != model.Count) return $"{native.Count} slots in the game, {model.Count} shared.";
        foreach (var n in native)
            if (model.SingleOrDefault(x => x.Bench == n.Bench && x.Slot == n.Slot) is not { } m) return $"slot {n.Bench}/{n.Slot} is not shared.";
            else if (m.Path != n.Path) return $"slot {n.Bench}/{n.Slot} shows '{n.Path}', shared '{m.Path}'.";
        return null;
    }

    // ---------------- IWorldState: saves and snapshots ----------------

    public void Reset() => Model.Reset();
    public void Write(NetDataWriter w) => Model.Write(w);
    public void Read(NetDataReader r)
    {
        Model.Read(r);
        // A guest enters (and a host restores) only after its own load settled: check the game shows the same peripherals.
        if (Model.Bound && CoopRunner.Instance?.Scene == CoopRunner.WorkshopScene && Difference(Read(out _)) is { } diff)
            throw new InvalidDataException("The loaded world's peripherals don't match the host's: " + diff);
    }

    // ---------------- requests (local input) ----------------

    internal void RequestSwap(PeripheralSlot slot, PartInstance part)
    {
        if (!Active) { Refuse("The shared world isn't ready on this game yet."); return; }
        if (!_world.IsAuthority && (HasPending || _coop.Lifecycle?.HasPending == true || _coop.Transfers?.HasPending == true))
        { Refuse("Still waiting for the host to answer the previous request."); return; }
        var entry = Table().FirstOrDefault(x => x.Slot != null && x.Slot.Pointer == slot.Pointer);
        if (entry.Slot == null) { Refuse("That peripheral slot has no co-op identity."); return; }
        if (part == null || string.IsNullOrEmpty(part.GetPartId())) { Refuse("No peripheral was chosen."); return; }
        var key = entry.Key;
        if (Loading(key, slot) || _waiting.ContainsKey(key)) { Refuse("That peripheral is still loading."); return; }
        var from = Model.Get(key.Bench, key.Slot)?.Path ?? "";
        var request = new Request { What = "Peripheral swap", Part = part };
        _submitting = request;
        try
        {
            if (!_world.Submit(CommandTypes.PeripheralSwap, WorkstationWorld.Swap(_coop.Activity.Get(_world.Local)!.Epoch, key.Bench, key.Slot, from, part.GetPartId()), out var op))
            { Refuse("Not connected to the host's world."); return; }
            if (!_world.IsAuthority) _pending[op] = request;
            Plugin.Logger.LogInfo($"[workstations] swap of {Describe(slot)} to {part.GetPartId()} requested ({op}).");
        }
        finally { _submitting = null; }
    }

    private void OnRejected(OperationId op, RejectReason reason, string text)
    {
        if (op.Actor != _world.Local) return;
        var request = _submitting;
        if (request == null && !_pending.Remove(op, out request)) return;
        Refuse(text);
    }

    private void Refuse(string why) => _coop.Notice("Peripheral swap refused: " + why.Replace(Model.Name + ": ", ""));

    // ---------------- IPeripheralNative ----------------

    public string Resolve(int bench, int slot, string part)
    {
        var native = SlotAt(bench, slot);
        var desc = native == null ? null : PartsDatabase.s_instance?.GetDesc(part, null);
        // PeripheralSlot.Swap installs a part only if its type is a peripheral type equal to the slot's type.
        if (desc == null || desc.m_type != native.Type || !PartDesc.s_peripheralTypes.Contains(desc.m_type)) return null;
        // MousePadPerifSlot.SwapPerif skips a pad whose size its desk doesn't take (its IsCompatible override); the
        // other slot types accept any part of their type.
        if (!native.IsCompatible(CatalogPart(part))) return null;
        return string.IsNullOrEmpty(desc.m_assetPath) ? null : desc.m_assetPath;
    }

    public IReadOnlyCollection<string> BenchKeys(int bench)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ws in Object.FindObjectsOfType<WorkStation>(true))
            if (ws != null && (int)ws.m_benchID == bench)
                foreach (var b in new[] { ws.m_slot, ws.m_BenchToolSlot })
                    if (b != null) keys.Add(NativeObjectState.BenchKey(b));
        return keys;
    }

    public string CheckHost(in CommandContext ctx, PeripheralCommand c)
    {
        var slot = SlotAt(c.Bench, c.Slot);
        if (slot == null) return "That peripheral slot is missing on the host's game.";
        return Loading((c.Bench, c.Slot), slot) || _waiting.ContainsKey((c.Bench, c.Slot)) ? "That peripheral is still loading on the host's game." : null;
    }

    public void Swapped(in MutationContext ctx, PeripheralCommand c, string path)
    {
        var request = ctx.FromLocalPlayer ? _submitting ?? _pending.GetValueOrDefault(ctx.Op) : null;
        var key = (c.Bench, c.Slot);
        try
        {
            var slot = SlotAt(c.Bench, c.Slot) ?? throw new InvalidDataException("slot missing");
            if (Loading(key, slot) || _waiting.ContainsKey(key))
            {
                // One native swap at a time per slot. The latest accepted fact wins.
                _waiting[key] = (c.Part, path, request);
                Plugin.Logger.LogInfo($"[workstations] {Describe(slot)} is loading; the swap to {c.Part} by {ctx.Op} waits for it.");
                return;
            }
            Project(key, slot, c.Part, path, request);
            Plugin.Logger.LogInfo($"[workstations] {Describe(slot)} → {c.Part} by {ctx.Op}" + (request != null ? " (here)." : " (projected)."));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[workstations] the swap of {c.Bench}/{c.Slot} to {c.Part} was accepted, but this game could not show it: {e}");
            _coop.Notice("A peripheral swap could not be shown on this game (see log).");
        }
    }

    private void Project((int, int) key, PeripheralSlot slot, string partId, string path, Request request)
    {
        var part = request?.Part != null && request.Part.GetPartId() == partId ? request.Part : CatalogPart(partId);
        NativeProjection.Run(() => slot.SwapPerif(part));
        if (PathOf(slot) != path) _loading[key] = (path, Time.realtimeSinceStartup + ProjectionTimeout);
    }

    /// <summary>A Free Build peripheral entry for that part, or a fresh instance of it. Swap reads only its description.</summary>
    private static PartInstance CatalogPart(string id)
    {
        var inventory = CareerStatus.Get()?.GetPeripheralInventory();
        if (inventory != null) foreach (var p in inventory) if (p != null && p.GetPartId() == id) return p;
        return PartInstance.NewPartInstance(id, 0, null) ?? throw new InvalidDataException($"part {id} is unavailable");
    }

    /// <summary>Per frame: run a swap that was waiting for its slot to finish loading.</summary>
    public void Update()
    {
        if (_waiting.Count == 0) return;
        foreach (var key in _waiting.Keys.ToArray())
        {
            var slot = SlotAt(key.Bench, key.Slot);
            if (slot == null) { _waiting.Remove(key); continue; }
            if (Loading(key, slot)) continue;
            var (part, path, request) = _waiting[key]; _waiting.Remove(key);
            try { Project(key, slot, part, path, request); Plugin.Logger.LogInfo($"[workstations] {Describe(slot)} → {part} (after waiting)."); }
            catch (Exception e) { Plugin.Logger.LogError($"[workstations] the waiting swap of {Describe(slot)} to {part} failed: {e}"); _coop.Notice("A peripheral swap could not be shown on this game (see log)."); }
        }
    }

    internal string Report()
    {
        var native = Table().Select(x => $"{x.Key.Bench}/{x.Key.Slot}:{x.Slot?.Type}={PathOf(x.Slot)}");
        return string.Join("; ", native);
    }

    // ---------------- packages and physical notes ----------------

    /// <summary>What the loaded world holds that co-op can't share yet, or null. Default Free Build has none (3b, B7/B8).</summary>
    internal static string Unsupported()
    {
        var found = new List<string>();
        var wc = Object.FindObjectOfType<WorkshopController>();
        if (wc != null && wc.GetCarriedPackage() != null) found.Add("a carried package");
        if (wc != null && wc.GetCarriedPostIt() != null) found.Add("a carried note");
        var packages = Object.FindObjectsOfType<Package>(true).Length;
        if (packages > 0) found.Add(packages == 1 ? "1 package" : $"{packages} packages");
        var notes = Object.FindObjectsOfType<PostItSlot>(true).Count(s => s != null && s.GetPostIt() != null);
        if (notes > 0) found.Add(notes == 1 ? "1 physical note" : $"{notes} physical notes");
        return found.Count == 0 ? null : string.Join(", ", found);
    }

    // ---------------- interception ----------------

    private static NativeWorkstations Current => CoopController.Instance?.Workstations;

    /// <summary>Every peripheral swap: the base slot (microphone and any other plain slot) and each override. The
    /// headset, keyboard, mouse pad and mouse overrides are one native function, hooked once; <c>__instance</c> is then
    /// any of those slot types, so the prefix only uses <see cref="PeripheralSlot"/> members.</summary>
    [HarmonyPatch]
    internal static class SwapIntercept
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var hooked = new HashSet<IntPtr>();
            foreach (var type in new[] { typeof(PeripheralSlot), typeof(HeadsetPerifSlot), typeof(KeyboardPerifSlot), typeof(MonitorPerifSlot), typeof(MousePadPerifSlot), typeof(MousePerifSlot) })
            {
                var method = AccessTools.DeclaredMethod(type, nameof(PeripheralSlot.SwapPerif));
                var code = GamePatches.NativeCode(method);
                if (code == IntPtr.Zero) throw new MissingMethodException(type.Name, nameof(PeripheralSlot.SwapPerif));
                if (hooked.Add(code)) yield return method;
                else Plugin.Logger.LogInfo($"[workstations] {type.Name}.SwapPerif shares its native code with an already hooked slot type.");
            }
        }
        private static bool Prefix(PeripheralSlot __instance, PartInstance selectedPerif)
        {
            if (NativeProjection.Active || !NativePcLifecycle.InCoopWorkshop) return true;
            try
            {
                if (Current == null) CoopController.Instance?.Notice("Peripheral swap refused: the shared world isn't ready on this game yet.");
                else Current.RequestSwap(__instance, selectedPerif);
            }
            catch (Exception e) { Plugin.Logger.LogError("[workstations] swap request failed: " + e); }
            return false;
        }
    }
}

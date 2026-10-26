using HarmonyLib;
using PCBS2Coop.Network;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// Native side of host-ordered PC creation/removal (task 3e). The three evidenced user-intent boundaries submit a
/// request instead of mutating: the case branch of <c>ItemDisplay.InstallButtonClicked</c> (catalog spawn/switch),
/// <c>CareerStatus.AddCaseToInventory</c> for the carried case (in-world delete; in Free Build it only calls
/// <c>DestroyCarriedCase</c>) and <c>StoredPC.DeletePC</c> (confirmed storage delete). The accepted change then runs the
/// same native steps on the requesting peer only; the other peer keeps a remote carrier record or removes its own
/// projection. The host also reports where its own native PCs are, until task 3f's atomic transfers replace that.
/// </summary>
internal sealed class NativePcLifecycle : IPcNative
{
    private static readonly Dictionary<string, string[]> DefaultFanLayouts = new(StringComparer.Ordinal);
    private const float ObserveInterval = .25f;
    private const float RejectedObserveBackoff = 5f;
    /// <summary>Native calls made by an accepted change. Interception and movement guards let them through.</summary>
    public static bool Projecting => NativeProjection.Active;
    private sealed class Request
    {
        public string What;
        public StoredPC Storage;
        public IntPtr State;
    }
    private readonly CoopController _coop;
    private readonly WorldSync _world;
    private readonly Dictionary<OperationId, Request> _pending = new();
    private Request _submitting;
    private bool _observing;
    private float _nextObserve;
    public PcLifecycle Handler { get; }
    private NativeObjectState Objects => _coop.Objects;

    public NativePcLifecycle(CoopController coop, WorldSync world)
    {
        _coop = coop; _world = world;
        Handler = new PcLifecycle(coop.Objects.Model, coop.Activity, this, world.IsAuthority);
        world.Accepted += (OperationId op, ulong revision) => _pending.Remove(op);
        world.Rejected += OnRejected;
        world.GenerationStarted += (uint generation, string reason) => _pending.Clear();
    }

    /// <summary>A co-op world is loaded here, whether or not this peer is admitted yet.</summary>
    internal static bool InCoopWorkshop => CoopSaves.Current != CoopSaves.Mode.None && CoopRunner.Instance?.Scene == CoopRunner.WorkshopScene &&
                                           CoopController.Instance?.Session.IsRunning == true;
    private bool Active => _world.Admitted && _world.Divergence == null && Objects.Initialized && _coop.Activity.Get(_world.Local) != null;
    private static WorkshopController Workshop => Object.FindObjectOfType<WorkshopController>() ?? throw new InvalidDataException("Workshop unavailable.");
    private static SaveLoadSystem Saves => Object.FindObjectOfType<SaveLoadSystem>() ?? throw new InvalidDataException("Save system unavailable.");

    // ---------------- requests (local input) ----------------

    internal void RequestCreate(PartInstance item)
    {
        if (!Ready("Case spawn")) return;
        var wc = Workshop;
        if (wc.IsCaseLoading || Saves.SpawningPC) { Refuse("Case spawn", "Objects are still loading."); return; }
        var carried = wc.GetCarriedCase();
        var replace = carried != null ? Objects.IdOf(carried) : default;
        if (carried != null && replace.IsNone) { Refuse("Case spawn", "The carried case has no co-op identity."); return; }
        var template = CatalogRefOf(item);
        if (template == null) { Refuse("Case spawn", "That case is not in the shared Free Build catalog."); return; }
        var state = GameController.Get()?.GetCurrentState();
        Submit(PcLifecycle.Create(Epoch, template, replace), new Request { What = "Case spawn", State = state?.Pointer ?? IntPtr.Zero });
    }

    internal void RequestCarriedDelete(Case carried)
    {
        if (!Ready("Case delete")) return;
        var id = Objects.IdOf(carried);
        if (id.IsNone) { Refuse("Case delete", "The carried case has no co-op identity."); return; }
        Submit(PcLifecycle.Delete(Epoch, id, PcRemoval.Carried), new Request { What = "Case delete" });
    }

    internal void RequestStoredDelete(StoredPC ui)
    {
        var request = new Request { What = "PC delete", Storage = ui };
        if (!Ready("PC delete")) { Restore(request); return; }
        var id = Objects.IdOf(ui.m_computer);
        if (id.IsNone) { Refuse("PC delete", "That stored PC has no co-op identity."); Restore(request); return; }
        Submit(PcLifecycle.Delete(Epoch, id, PcRemoval.Stored), request);
    }

    private ulong Epoch => _coop.Activity.Get(_world.Local)!.Epoch;
    /// <summary>A guest request is waiting for the host's answer.</summary>
    internal bool HasPending => _pending.Count > 0;

    private bool Ready(string what)
    {
        if (!Active) { Refuse(what, "The shared world isn't ready on this game yet."); return false; }
        if (!_world.IsAuthority && (HasPending || _coop.Transfers?.HasPending == true || _coop.Workstations?.HasPending == true)) { Refuse(what, "Still waiting for the host to answer the previous request."); return false; }
        // The host validates against its own current native locations, so report them first.
        if (_world.IsAuthority) ObserveHost(force: true);
        return true;
    }

    private void Submit(byte[] payload, Request request)
    {
        _submitting = request;
        try
        {
            if (!_world.Submit(CommandTypes.PcLifecycle, payload, out var op)) { Refuse(request.What, "Not connected to the host's world."); Restore(request); return; }
            if (!_world.IsAuthority) _pending[op] = request;
            Plugin.Logger.LogInfo($"[objects] {request.What} requested ({op}).");
        }
        finally { _submitting = null; }
    }

    private void OnRejected(OperationId op, RejectReason reason, string text)
    {
        if (op.Actor != _world.Local) return;
        var request = _submitting;
        if (request == null && !_pending.Remove(op, out request))
        {
            if (_observing) _nextObserve = Time.realtimeSinceStartup + RejectedObserveBackoff;
            return;
        }
        Refuse(request.What, text);
        Restore(request);
    }

    private void Refuse(string what, string why) => _coop.Notice($"{what} refused: {why.Replace(Handler.Name + ": ", "")}");

    /// <summary>Put the native UI back the way the game's own "No" answer does.</summary>
    private static void Restore(Request request)
    {
        try { if (request.Storage != null && request.Storage) request.Storage.OnClickNo_DeletePC(); }
        catch (Exception e) { Plugin.Logger.LogWarning("[objects] could not restore the storage UI: " + e.Message); }
    }

    private Request LocalRequest(in MutationContext ctx) => !ctx.FromLocalPlayer ? null : _submitting ?? _pending.GetValueOrDefault(ctx.Op);

    // ---------------- host observation (interim until task 3f) ----------------

    public void Update()
    {
        if (!_world.IsAuthority || !Active) return;
        if (_coop.WorldReady) Objects.ExpireStaleSpawns();
        ObserveHost(force: false);
    }

    /// <summary>Host: report its own native PC locations that differ from the model as one host-ordered change.</summary>
    internal void ObserveHost(bool force)
    {
        if (!_world.IsAuthority || !Active || (!force && Time.realtimeSinceStartup < _nextObserve)) return;
        _nextObserve = Time.realtimeSinceStartup + ObserveInterval;
        List<PcObservation> moves;
        try { moves = Objects.ObserveHostMoves(_coop.Session.LocalIdentity.Player); }
        catch (InvalidDataException) { return; } // a native load is running; observe once it settles
        if (moves.Count == 0) return;
        // Since task 3f every supported move is a host-ordered transfer the guest also projects. A difference here
        // means a native path moved a PC without one: keep both models equal, and say so.
        Plugin.Logger.LogWarning($"[objects] {moves.Count} host PC move(s) happened outside a synchronized transfer: " +
                                 string.Join(", ", moves.Select(m => $"{m.Pc}→{m.Place}")) + ". The other player's game does not show them.");
        _observing = true;
        try
        {
            foreach (var chunk in moves.Chunk(PcLifecycle.MaxObservations))
                _world.Submit(CommandTypes.PcLifecycle, PcLifecycle.Observe(Epoch, chunk), out _);
        }
        finally { _observing = false; }
    }

    /// <summary>Host, before a save/snapshot capture: give parts that native loads added to created PCs (default case
    /// fans) their IDs through the change stream, so the capture finds nothing new and both models stay identical.</summary>
    internal void ExtendHost()
    {
        if (!_world.IsAuthority || !Active) return;
        List<(NetObjectId Pc, PcGrowth Growth)> growth;
        try { growth = Objects.HostGrowth(_coop.Session.LocalIdentity.Player); }
        catch (InvalidDataException) { return; } // loading: the capture refuses the save as well
        foreach (var (pc, g) in growth)
        {
            _world.Submit(CommandTypes.PcLifecycle, PcLifecycle.Extend(Epoch, pc, g), out var op);
            Plugin.Logger.LogInfo($"[objects] PC {pc} native load added {g.Parts.Count} part(s) ({op}).");
        }
    }

    // ---------------- IPcNative ----------------

    public PcTemplate Describe(CatalogRef template)
    {
        var item = CatalogItem(template);
        return item == null || item.GetPart()?.m_type != PartDesc.Type.CASE ? null : NativeObjectState.Template(NewFromCatalog(item));
    }

    public string CheckHost(in CommandContext ctx, PcCommand command, PcFact target)
    {
        var wc = Workshop;
        var host = ctx.Op.Actor == ActorSlot.Host;
        switch (command.Action)
        {
            case PcAction.Create when host:
                if (wc.IsCaseLoading || Saves.SpawningPC) return "Objects are still loading.";
                if (target == null) return wc.CarryingEmpty() ? null : "Your hands are not empty.";
                return CarriedEmpty(wc, target.Id);
            case PcAction.Create:
                return target == null ? null : RemoteEmpty(target);
            case PcAction.Delete when command.From == PcRemoval.Carried:
                return host ? CarriedEmpty(wc, target.Id) : RemoteEmpty(target);
            case PcAction.Delete:
                return Objects.FindStored(target.Id) == null ? "That PC is not in the host's storage." : null;
            default: return null;
        }
    }

    private string CarriedEmpty(WorkshopController wc, NetObjectId id)
    {
        var carried = wc.GetCarriedCase();
        if (carried == null || Objects.IdOf(carried) != id) return "The carried case changed.";
        return carried.m_computer?.IsEmptyCase() == true ? null : "Only an empty carried case can be switched or deleted.";
    }

    private string RemoteEmpty(PcFact target) =>
        !Objects.TryRemoteCarrier(target, out var record) ? "The guest's carried case is unknown on the host." :
        record.IsEmptyCase() ? null : "Only an empty carried case can be switched or deleted.";

    public void Created(in MutationContext ctx, PcFact pc, CatalogRef template, PcFact replaced)
    {
        try
        {
            var record = NewFromCatalog(CatalogItem(template) ?? throw new InvalidDataException("Catalog case is gone."));
            NativeObjectState.CheckParts(pc, record);
            if (ctx.FromLocalPlayer) CreateHere(pc, record, replaced, LocalRequest(ctx));
            else
            {
                if (replaced != null) Objects.ForgetPc(replaced.Id);
                Objects.SetRemoteCarrier(pc, record);
            }
            Plugin.Logger.LogInfo($"[objects] created PC {pc.Id} ({template.Sku}) for {pc.Location.Actor} by {ctx.Op}" +
                                  (replaced != null ? $", replacing {replaced.Id}" : "") + (ctx.FromLocalPlayer ? ", spawning here." : ", remote carrier."));
        }
        catch (Exception e) { Fault($"PC {pc.Id} was created, but this game could not show it", e); }
    }

    private void CreateHere(PcFact pc, ComputerSave record, PcFact replaced, Request request)
    {
        var wc = Workshop;
        var carried = wc.GetCarriedCase();
        if (replaced != null)
        {
            if (carried == null || Objects.IdOf(carried) != replaced.Id) throw new InvalidDataException("Native hands no longer hold the replaced case.");
            // Same order as the native switch: destroy the mesh, then the inventory call that clears the carrier.
            var old = carried.m_computer;
            Native(() => { Object.DestroyImmediate(carried.gameObject); CareerStatus.Get().AddCaseToInventory(old.caseID, old, true); });
            Objects.ForgetPc(replaced.Id);
        }
        else if (!wc.CarryingEmpty()) throw new InvalidDataException("Native hands are not empty.");
        // Bind before loading so the asynchronous spawn completion maps the new mesh to this PC only.
        Objects.BindCreated(pc.Id, record);
        Native(() => Saves.PickupComputerFromStorage(record, true));
        var ctrl = GameController.Get();
        if (request != null && request.State != IntPtr.Zero && ctrl?.GetCurrentState()?.Pointer == request.State) ctrl.GoToPreviousState();
    }

    public void Deleted(in MutationContext ctx, PcFact pc, PcRemoval from)
    {
        try
        {
            var request = LocalRequest(ctx);
            var wc = Workshop;
            var carried = wc.GetCarriedCase();
            string where;
            if (carried != null && Objects.IdOf(carried) == pc.Id)
            {
                var save = carried.m_computer;
                Native(() => CareerStatus.Get().AddCaseToInventory(save.caseID, save, true));
                where = "native carrier";
            }
            else if (Objects.TryRemoteCarrier(pc, out _)) where = "remote carrier";
            else if (Objects.FindStored(pc.Id) is { } stored)
            {
                if (request?.Storage != null && request.Storage && request.Storage.m_computer?.Pointer == stored.Pointer) Native(() => request.Storage.DeletePC());
                else RemoveStored(stored);
                where = "storage";
            }
            else if (Objects.FindBench(pc.Id) is { } bench) { Native(() => bench.DestroyComputer()); where = "bench"; }
            else where = null;
            Objects.ForgetPc(pc.Id);
            if (where == null) Plugin.Logger.LogWarning($"[objects] deleted PC {pc.Id} ({from}) had no projection on this game.");
            else Plugin.Logger.LogInfo($"[objects] deleted PC {pc.Id} ({from}) from {where} by {ctx.Op}.");
            var expected = from == PcRemoval.Stored ? "storage" : ctx.FromLocalPlayer ? "native carrier" : "remote carrier";
            if (where != null && where != expected) Plugin.Logger.LogWarning($"[objects] PC {pc.Id} was at {where} here, not {expected}; this game's world had drifted (unsynchronized movement).");
        }
        catch (Exception e) { Fault($"PC {pc.Id} was deleted, but this game could not remove it", e); }
    }

    /// <summary>The native StoredPC.DeletePC removal without its UI object: the first matching custom-case design, then the record.</summary>
    private static void RemoveStored(ComputerSave record)
    {
        var save = Saves.localSaveGame;
        var custom = save.customCaseSaveList;
        if (custom != null)
            for (var i = 0; i < custom.Count; i++)
                if (custom[i] != null && custom[i].CustomisationID == record.CustomisationID) { custom.RemoveAt(i); break; }
        save.computersInStorage.Remove(record);
        foreach (var storage in Object.FindObjectsOfType<Storage>()) storage.Populate(true, true, false);
    }

    private void Fault(string what, Exception e)
    {
        Plugin.Logger.LogError($"[objects] {what}: {e}");
        _coop.Notice(what + " (see log).");
    }

    private static void Native(Action call) => NativeProjection.Run(call);

    /// <summary>Guest re-admission: put its own logically carried PC into its hands through the ordinary spawn path.</summary>
    internal static void SpawnIntoHands(NativeObjectState objects, NetObjectId id, ComputerSave record)
    {
        try
        {
            if (!Workshop.CarryingEmpty()) throw new InvalidDataException("Native hands are not empty.");
            objects.BindCreated(id, record);
            Native(() => Saves.PickupComputerFromStorage(record, true));
            Plugin.Logger.LogInfo($"[objects] restored carried PC {id} into this guest's hands.");
        }
        catch (Exception e) { Plugin.Logger.LogError($"[objects] could not restore carried PC {id}: {e}"); CoopController.Instance?.Notice($"Your carried PC {id} could not be shown (see log)."); }
    }

    // ---------------- catalog ----------------

    /// <summary>The native case branch, with the prefab's automatic fans made explicit before identity allocation.
    /// Otherwise a guest can store a newly loaded case before the host ever sees its default fans, leaving the
    /// host's held record incomplete. Read the game's actual slot defaults; never guess from the catalog flag.</summary>
    internal static ComputerSave NewFromCatalog(PartInstance item)
    {
        var pc = new ComputerSave { caseID = item };
        var database = PartsDatabase.s_instance ?? throw new InvalidDataException("Parts database unavailable.");
        var defaults = DefaultFans(database, item.GetPart());
        var count = Math.Max(defaults.Length, item.m_caseFanSlots?.Length ?? 0);
        if (count > 0)
        {
            var fans = new PartInstance[count];
            for (var i = 0; i < count; i++)
            {
                if (i < (item.m_caseFanSlots?.Length ?? 0)) fans[i] = item.m_caseFanSlots[i];
                var sku = i < defaults.Length ? defaults[i] : null;
                if (fans[i] != null || string.IsNullOrEmpty(sku)) continue;
                // Slot.DoAutoInstantiation uses this factory and SetNew before installing the default part.
                fans[i] = PartInstance.NewPartInstance(sku, AutoInstantiated: 1, seededRandom: null);
                fans[i].SetNew();
            }
            pc.caseFanSlots = fans;
        }
        return pc;
    }

    private static string[] DefaultFans(PartsDatabase database, PartDesc desc)
    {
        if (DefaultFanLayouts.TryGetValue(desc.m_id, out var known)) return known;
        // GetPrefab can return a placeholder until the first native spawn. Load only the asset for metadata;
        // do not instantiate a case, touch the carry singleton, or depend on a prior spawn on this peer.
        var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>((Il2CppSystem.String)desc.m_address);
        try
        {
            var prefab = handle.WaitForCompletion() ?? throw new InvalidDataException("The case prefab is unavailable.");
            var candidates = prefab.GetComponentsInChildren<Case>(true).ToArray();
            var mesh = candidates.SingleOrDefault(c => c.GetComponent<ComponentPC>()?.Type == desc.m_subPart) ?? (candidates.Length == 1 ? candidates[0] : null);
            if (mesh == null) throw new InvalidDataException("The case component cannot be identified in its prefab.");
            var slots = mesh.caseFanSlots;
            var result = new string[slots?.Length ?? 0];
            for (var i = 0; i < result.Length; i++)
            {
                var slot = slots[i];
                if (slot == null || slot.initiallyUninstalled || string.IsNullOrEmpty(slot.m_autoInstantiatePartID)) continue;
                result[i] = (database.GetDesc(slot.m_autoInstantiatePartID, null) ?? throw new InvalidDataException("A default case fan is unavailable.")).m_id;
            }
            DefaultFanLayouts.Add(desc.m_id, result);
            return result;
        }
        finally { if (handle.IsValid()) handle.Release(); }
    }

    internal static PartInstance CatalogItem(CatalogRef r)
    {
        var n = 0;
        foreach (var p in CareerStatus.Get()?.GetInventory() ?? throw new InvalidDataException("Inventory unavailable."))
            if (p != null && p.GetPartId() == r.Sku && p.GetIl2CppType().FullName == r.NativeType && n++ == r.Ordinal) return p;
        return null;
    }

    internal static CatalogRef CatalogRefOf(PartInstance item)
    {
        var sku = item.GetPartId(); var type = item.GetIl2CppType().FullName; var n = 0;
        foreach (var p in CareerStatus.Get()?.GetInventory() ?? throw new InvalidDataException("Inventory unavailable."))
        {
            if (p == null || p.GetPartId() != sku || p.GetIl2CppType().FullName != type) continue;
            if (p.Pointer == item.Pointer) return new(sku, type, n);
            n++;
        }
        return null;
    }

    // ---------------- interception ----------------

    private static NativePcLifecycle Current => CoopController.Instance?.Lifecycle;

    /// <summary>Only the case branch, and only when the native switch would proceed (empty hands or an empty carried case).</summary>
    [HarmonyPatch(typeof(ItemDisplay), nameof(ItemDisplay.InstallButtonClicked))]
    internal static class SpawnIntercept
    {
        private static bool Prefix(ItemDisplay __instance)
        {
            if (Projecting || !InCoopWorkshop) return true;
            try
            {
                var item = __instance.item;
                if (!__instance.enabled || item == null || item.GetPart()?.m_type != PartDesc.Type.CASE || !__instance.CanSwitchCase()) return true;
                if (Current == null) { CoopController.Instance.Notice("Case spawn refused: the shared world isn't ready on this game yet."); return false; }
                Current.RequestCreate(item);
            }
            catch (Exception e) { Plugin.Logger.LogError("[objects] case spawn request failed: " + e); }
            return false;
        }
    }

    /// <summary>In Free Build this call is the in-world carried-case deletion. Other callers keep the 3d movement guard.</summary>
    [HarmonyPatch(typeof(CareerStatus), nameof(CareerStatus.AddCaseToInventory))]
    internal static class CarriedDeleteIntercept
    {
        private static bool Prefix(ComputerSave computerSave)
        {
            if (Projecting || !InCoopWorkshop) return true;
            try
            {
                var carried = Workshop.GetCarriedCase();
                if (carried?.m_computer == null || computerSave == null || carried.m_computer.Pointer != computerSave.Pointer)
                    return NativeActivity.Allow(CoopController.Instance?.Objects.IdOf(computerSave) ?? default);
                if (Current == null) { CoopController.Instance.Notice("Case delete refused: the shared world isn't ready on this game yet."); return false; }
                Current.RequestCarriedDelete(carried);
            }
            catch (Exception e) { Plugin.Logger.LogError("[objects] case delete request failed: " + e); }
            return false;
        }
    }

    /// <summary>The confirmed "Yes" of the storage delete dialog.</summary>
    [HarmonyPatch(typeof(StoredPC), nameof(StoredPC.DeletePC))]
    internal static class StoredDeleteIntercept
    {
        private static bool Prefix(StoredPC __instance)
        {
            if (Projecting || !InCoopWorkshop) return true;
            try
            {
                if (Current == null)
                {
                    CoopController.Instance.Notice("PC delete refused: the shared world isn't ready on this game yet.");
                    __instance.OnClickNo_DeletePC();
                    return false;
                }
                Current.RequestStoredDelete(__instance);
            }
            catch (Exception e) { Plugin.Logger.LogError("[objects] PC delete request failed: " + e); }
            return false;
        }
    }
}

using HarmonyLib;
using PCBS2Coop.Network;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// Native side of case access (task 4d) and the co-op gate for component edits that aren't shared yet (owner decisions,
/// October 27, 2026). Removals start in <c>WorkingOnComputerState.DisassemblyTick</c>'s long-press callbacks; installs
/// commit in <c>AssemblyTick</c>'s callback after <c>ItemDisplay.InstallButtonClicked</c> placed previews. In co-op an
/// integral case panel becomes a host-ordered request (the requester runs the game's own animated removal or install
/// afterwards); every other install or removal is refused with a notice before it starts. The other game shows a panel
/// change directly, the way the game builds panels when it loads a save, so that player's own work state is untouched.
/// </summary>
internal sealed class NativeCaseAccess : IPanelNative
{
    private sealed class Request
    {
        public NetObjectId Pc, Slot;
        public bool Remove;
        public System.Action Replay;
        public State Work;
    }
    private readonly CoopController _coop;
    private readonly WorldSync _world;
    private readonly Dictionary<OperationId, Request> _pending = new();
    private Request _submitting;
    /// <summary>Accepted panel changes whose native work is still running on this game (the requester's own animated
    /// edit starts as a coroutine and finishes seconds later): the PC is rebound once the slot shows the result.</summary>
    private readonly List<(NetObjectId Pc, Slot Slot, bool Remove, float Until)> _finishing = new();
    private const float FinishTimeout = 20f;
    public CasePanelWorld Handler { get; }

    public NativeCaseAccess(CoopController coop, WorldSync world)
    {
        _coop = coop; _world = world;
        Handler = new CasePanelWorld(coop.Objects.Model, coop.Activity, this);
        world.Accepted += (OperationId op, ulong revision) => _pending.Remove(op);
        world.Rejected += OnRejected;
        world.GenerationStarted += (uint generation, string reason) => { _pending.Clear(); _finishing.Clear(); };
    }

    private bool Active => _world.Admitted && _world.Divergence == null && _coop.Objects.Initialized && _coop.Activity.Get(_world.Local) != null;
    /// <summary>A panel request is waiting for the host: saves and snapshots wait for it.</summary>
    internal bool Busy => _pending.Count > 0 || _finishing.Count > 0;

    private static bool PanelSlot(Slot slot) => slot != null && slot.m_slotType is Slot.SlotType.CASE_PART_REQUIRED or Slot.SlotType.CASE_PART_OPTIONAL;
    private static State CurrentState => GameController.Get()?.GetCurrentState();

    /// <summary>The shared case-panel slot a native slot is, or none.</summary>
    private (PcFact Pc, AssemblyResource Slot) Shared(Slot slot)
    {
        if (!PanelSlot(slot)) return default;
        var (pcId, id) = _coop.Objects.Assembly.Find(slot);
        var pc = _coop.Objects.Model.Pcs.SingleOrDefault(p => p.Id == pcId);
        var fact = pc?.Resources.SingleOrDefault(r => r.Id == id);
        return fact is { Kind: AssemblyResourceKind.Slot } && fact.IntegralHome != "" ? (pc, fact) : default;
    }

    // ---------------- requests (local input) ----------------

    /// <summary>A removal long press. Returns whether the native removal may run now.</summary>
    internal bool RequestRemove(ComponentPC comp, System.Action replay)
    {
        var slot = comp?.Slot;
        var (pc, fact) = Shared(slot);
        if (fact == null) { Refuse("Removing parts isn't shared in co-op yet."); return false; }
        if (fact.IntegralPart == "") { Refuse("That panel is already off in the shared world."); return false; }
        return Submit(new Request { Pc = pc.Id, Slot = fact.Id, Remove = true, Replay = replay, Work = CurrentState },
            CasePanelWorld.Remove(_coop.Activity.Get(_world.Local)!.Epoch, pc.Id, fact.Id, fact.Version), $"removal of {slot.name}");
    }

    /// <summary>An install commit. Returns whether the native install may run now.</summary>
    internal bool RequestRefit(Slot slot, System.Action replay)
    {
        var (pc, fact) = Shared(slot);
        var sku = slot?.GetComponent()?.GetPartInstance()?.GetPartId();
        if (fact == null || sku == null || sku != fact.IntegralHome) { Refuse("Installing parts isn't shared in co-op yet."); return false; }
        return Submit(new Request { Pc = pc.Id, Slot = fact.Id, Remove = false, Replay = replay, Work = CurrentState },
            CasePanelWorld.Refit(_coop.Activity.Get(_world.Local)!.Epoch, pc.Id, fact.Id, fact.Version, sku), $"refit of {slot.name} with {sku}");
    }

    /// <summary>Inventory selection while working on a PC: only a removed panel of that case may show its previews.</summary>
    internal bool AllowSelection(Case forCase, PartInstance item)
    {
        var id = _coop.Objects.IdOf(forCase); var sku = item?.GetPartId();
        var pc = _coop.Objects.Model.Pcs.SingleOrDefault(p => p.Id == id);
        if (pc != null && sku != null && pc.Resources.Any(r => r.IntegralHome == sku && r.IntegralPart == "")) return true;
        Refuse("Installing parts isn't shared in co-op yet.");
        return false;
    }

    private bool Submit(Request request, byte[] payload, string what)
    {
        if (!Active) { Refuse("The shared world isn't ready on this game yet."); return false; }
        if (!_world.IsAuthority && _pending.Count > 0) { Refuse("Still waiting for the host to answer the previous request."); return false; }
        _submitting = request;
        try
        {
            if (!_world.Submit(CommandTypes.CasePanel, payload, out var op)) { Refuse("Not connected to the host's world."); return false; }
            if (!_world.IsAuthority) _pending[op] = request;
            Plugin.Logger.LogInfo($"[panels] {what} requested ({op}).");
        }
        finally { _submitting = null; }
        return false;
    }

    private void OnRejected(OperationId op, RejectReason reason, string text)
    {
        if (op.Actor != _world.Local) return;
        if (_submitting == null && !_pending.Remove(op)) return;
        Refuse(text);
    }

    private void Refuse(string why) => _coop.Notice("Refused: " + why.Replace(Handler.Name + ": ", ""));

    // ---------------- IPanelNative ----------------

    public string CheckHost(in CommandContext ctx, PcFact pc, AssemblyResource fact, PanelCommand c)
    {
        var slot = _coop.Objects.Assembly.NativeFor(pc.Id, fact.Id)?.TryCast<Slot>();
        if (!PanelSlot(slot)) return "That panel is missing on the host's game.";
        var comp = slot.GetComponent();
        if (comp != null && NativeAssembly.IsPreview(comp)) comp = null;
        if (c.Remove)
        {
            if (comp == null) return "The host's game shows that panel already off.";
            if (!comp.CanBeUninstalled() || slot.IsUninstallingPathBlocked()) return "Something is in the way of that panel.";
            return null;
        }
        if (comp != null) return "The host's game shows that panel already fitted.";
        var part = PartInstance.NewPartInstance(c.Part, 0, null);
        if (part?.GetPart() == null || !PartsDatabase.s_instance.HasLoadedPart(part.GetPart(), out _)) return "That panel isn't loaded on the host's game.";
        return slot.CanFillWithItem(part) ? null : "Something is in the way of that panel.";
    }

    public void Changed(in MutationContext ctx, PcFact pc, AssemblyResource fact, PanelCommand c)
    {
        var request = ctx.FromLocalPlayer ? _submitting ?? _pending.GetValueOrDefault(ctx.Op) : null;
        var what = $"{(c.Remove ? "removal" : "refit")} of panel {fact.Id} on PC {pc.Id}";
        try
        {
            var slot = _coop.Objects.Assembly.NativeFor(pc.Id, fact.Id)?.TryCast<Slot>();
            if (slot == null)
            {
                // Not shown here (a record-only PC): its next native load is checked against the model.
                if (_coop.Objects.Projections.TryGet(pc.Id, out var mesh) && mesh != null) throw new InvalidDataException("the panel slot is not bound on this game");
                return;
            }
            // The requester runs the game's own gesture (animation, automatic screws, inventory) if its work state is
            // unchanged; otherwise, and on the other game, the panel changes directly.
            if (request?.Replay != null && request.Work != null && CurrentState?.Pointer == request.Work.Pointer)
            {
                NativeProjection.Run(request.Replay);
                Plugin.Logger.LogInfo($"[panels] {what} by {ctx.Op} (here).");
            }
            else
            {
                NativeProjection.Run(() => { if (c.Remove) RemoveDirect(slot); else RefitDirect(slot, c.Part); });
                Plugin.Logger.LogInfo($"[panels] {what} by {ctx.Op} (projected).");
            }
            _finishing.Add((pc.Id, slot, c.Remove, Time.realtimeSinceStartup + FinishTimeout));
            Update();
        }
        catch (Exception e) { _coop.ProjectionFailed($"The {what} was accepted, but this game could not show it", e); }
    }

    private static bool Shows(Slot slot, bool remove)
    {
        var comp = slot.GetComponent();
        var fitted = comp != null && !NativeAssembly.IsPreview(comp);
        return remove ? !fitted : fitted;
    }

    /// <summary>Per frame: rebind a PC once its accepted panel change has finished natively (no edit state running).</summary>
    public void Update()
    {
        for (var i = _finishing.Count - 1; i >= 0; i--)
        {
            var (pc, slot, remove, until) = _finishing[i];
            try
            {
                if (slot == null) { _finishing.RemoveAt(i); continue; } // the PC was reloaded meanwhile; its load is checked instead
                var editing = GameController.Get()?.GetCurrentState()?.TryCast<InstallingPartState>() != null;
                if (!editing && Shows(slot, remove)) { _finishing.RemoveAt(i); _coop.Objects.Assembly.RequestRebind(pc); continue; }
                if (Time.realtimeSinceStartup > until) throw new TimeoutException($"the panel was still {(remove ? "fitted" : "missing")} after {FinishTimeout} s");
            }
            catch (Exception e)
            {
                _finishing.RemoveAt(i);
                _coop.ProjectionFailed($"A case panel change on PC {pc} was accepted, but this game could not finish it", e);
            }
        }
    }

    /// <summary>The game's removal teardown without its edit state: unregister, destroy, rebuild the case, and keep the
    /// panel in this player's Removed Components (as the native removal does).</summary>
    private static void RemoveDirect(Slot slot)
    {
        var comp = slot.GetComponent();
        if (comp == null) return;
        if (NativeAssembly.IsPreview(comp)) throw new InvalidDataException("this game is previewing a part in that slot");
        var part = comp.GetPartInstance(); var mesh = slot.GetCase();
        mesh.OnChangePart(comp.gameObject, true, false);
        slot.SetComponentNull();
        Object.DestroyImmediate(comp.gameObject);
        mesh.UpdateGameStatus(); mesh.PostChangePart();
        if (part != null) CareerStatus.Get()?.AddItemToInventory(part, false, false, true, false);
        if (slot.GetComponent() != null) throw new InvalidDataException("the panel is still fitted");
    }

    /// <summary>The game's load-time panel instantiation (<c>Slot.DoAutoInstantiation</c>): a loaded part instance in
    /// the slot, taken from this player's Removed Components when it is there.</summary>
    private static void RefitDirect(Slot slot, string sku)
    {
        var existing = slot.GetComponent();
        if (existing != null && !NativeAssembly.IsPreview(existing)) return;
        if (existing != null) throw new InvalidDataException("this game is previewing a part in that slot");
        PartInstance part = null;
        var inventory = CareerStatus.Get()?.GetInventory();
        if (inventory != null) foreach (var p in inventory) if (p != null && p.GetPartId() == sku) { part = p; break; }
        if (part != null) CareerStatus.Get().RemoveItemFromInventory(part);
        part ??= PartInstance.NewPartInstance(sku, 0, null) ?? throw new InvalidDataException($"panel {sku} is unknown");
        if (part.GetPart() == null || !PartsDatabase.s_instance.HasLoadedPart(part.GetPart(), out var loaded)) throw new InvalidDataException($"panel {sku} is not loaded");
        var comp = slot.InstantiatePreloadedPart(part, loaded, true) ?? throw new InvalidDataException("the panel was not created");
        slot.SetComponent(comp);
        var mesh = slot.GetCase();
        mesh.OnChangePart(comp.gameObject, true, true);
        mesh.RebuildSlots(comp); mesh.FixupBlockers(comp); mesh.UpdateGameStatus(); mesh.PostChangePart();
    }

    // ---------------- interception ----------------

    private static NativeCaseAccess Current => CoopController.Instance?.Panels;
    private static bool Bypass => NativeProjection.Active || !NativePcLifecycle.InCoopWorkshop;
    private static bool NotReady()
    {
        CoopController.Instance?.Notice("Refused: the shared world isn't ready on this game yet.");
        return false;
    }
    private static IEnumerable<System.Reflection.MethodBase> Once(params System.Reflection.MethodBase[] methods)
    {
        var hooked = new HashSet<IntPtr>();
        foreach (var method in methods)
        {
            var code = method == null ? IntPtr.Zero : GamePatches.NativeCode(method);
            if (code == IntPtr.Zero) throw new MissingMethodException("component edit gesture method missing");
            if (hooked.Add(code)) yield return method;
            else Plugin.Logger.LogInfo($"[panels] {method.DeclaringType?.Name}.{method.Name} shares its native code with an already hooked gesture.");
        }
    }

    /// <summary>Removal long presses on a part (the three <c>DisassemblyTick</c> callbacks).</summary>
    [HarmonyPatch]
    internal static class RemovalIntercept
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => Once(
            AccessTools.Method(typeof(WorkingOnComputerState.__c__DisplayClass68_0), "_DisassemblyTick_b__0"),
            AccessTools.Method(typeof(WorkingOnComputerState.__c__DisplayClass68_0), "_DisassemblyTick_b__1"),
            AccessTools.Method(typeof(WorkingOnComputerState.__c__DisplayClass68_0), "_DisassemblyTick_b__2"));
        private static bool Prefix(WorkingOnComputerState.__c__DisplayClass68_0 __instance, System.Reflection.MethodBase __originalMethod)
        {
            if (Bypass) return true;
            try
            {
                if (Current == null) return NotReady();
                var closure = __instance; var method = __originalMethod;
                return Current.RequestRemove(closure.comp, () => method.Invoke(closure, null));
            }
            catch (Exception e) { Plugin.Logger.LogError("[panels] removal request failed: " + e); return false; }
        }
    }

    /// <summary>Removing a water pipe (custom loops are deferred by the owner).</summary>
    [HarmonyPatch(typeof(WorkingOnComputerState.__c__DisplayClass68_1), "_DisassemblyTick_b__3")]
    internal static class PipeRemovalIntercept
    {
        private static bool Prefix()
        {
            if (Bypass) return true;
            CoopController.Instance?.Notice("Refused: custom water-cooling edits aren't shared in co-op yet.");
            return false;
        }
    }

    /// <summary>The install commit long press (<c>AssemblyTick</c> callback).</summary>
    [HarmonyPatch(typeof(WorkingOnComputerState.__c__DisplayClass69_0), "_AssemblyTick_b__0")]
    internal static class InstallIntercept
    {
        private static bool Prefix(WorkingOnComputerState.__c__DisplayClass69_0 __instance)
        {
            if (Bypass) return true;
            try
            {
                if (Current == null) return NotReady();
                var closure = __instance;
                return Current.RequestRefit(closure.ParentSlot, () => closure._AssemblyTick_b__0());
            }
            catch (Exception e) { Plugin.Logger.LogError("[panels] install request failed: " + e); return false; }
        }
    }

    /// <summary>Selecting a part to install while working on a PC (peripheral swaps and case spawns are separate).</summary>
    [HarmonyPatch(typeof(ItemDisplay), nameof(ItemDisplay.InstallButtonClicked))]
    internal static class SelectionIntercept
    {
        private static bool Prefix(ItemDisplay __instance)
        {
            if (Bypass || __instance.m_forcase == null || __instance.m_periphSlot != null) return true;
            try
            {
                if (Current == null) return NotReady();
                return Current.AllowSelection(__instance.m_forcase, __instance.item);
            }
            catch (Exception e) { Plugin.Logger.LogError("[panels] selection check failed: " + e); return false; }
        }
    }
}

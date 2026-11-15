using HarmonyLib;
using PCBS2Coop.Network;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// Native side of case access (task 4d) and the co-op gate for component edits (owner decisions, October 27, 2026). Removals start in <c>WorkingOnComputerState.DisassemblyTick</c>'s long-press callbacks; installs
/// commit in <c>AssemblyTick</c>'s callback after <c>ItemDisplay.InstallButtonClicked</c> placed previews. In co-op an
/// integral case panel becomes a host-ordered request (the requester runs the game's own animated removal or install
/// afterwards); every other install or removal is a component edit (<see cref="NativeComponentEdits"/>, task 4e). The other game shows a panel
/// change directly, the way the game builds panels when it loads a save, so that player's own work state is untouched.
/// 0.68 (owner decision, November 10, 2026): the request holds the PC while the requester's game runs the gesture,
/// including Career's hand-turned screws. The requester then reports the panel finished (or taken back when the gesture
/// stopped with the panel as it was), and only then does the other game show it.
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
    /// edit starts as a coroutine and finishes seconds later, or after its screws are turned by hand): the PC is rebound
    /// once the slot shows the result. <see cref="Finishing.Token"/> is set on the requester's game, which then reports
    /// the end to the host.</summary>
    private sealed class Finishing
    {
        public NetObjectId Pc, SlotId;
        public Slot Slot;
        public bool Remove, SawEditing;
        public OperationId? Token;
        public float Started;
    }
    private readonly List<Finishing> _finishing = new();
    /// <summary>The other player's accepted panel changes this game shows once they report them finished.</summary>
    private readonly Dictionary<OperationId, (NetObjectId Pc, NetObjectId Slot)> _waiting = new();
    /// <summary>The other game shows its result within this time; the requester's gesture must start within it.</summary>
    private const float FinishTimeout = 20f;
    public CasePanelWorld Handler { get; }

    public NativeCaseAccess(CoopController coop, WorldSync world)
    {
        _coop = coop; _world = world;
        Handler = new CasePanelWorld(coop.Objects.Model, coop.Activity, this);
        world.Accepted += (OperationId op, ulong revision) => _pending.Remove(op);
        world.Rejected += OnRejected;
        world.GenerationStarted += (uint generation, string reason) => { _pending.Clear(); _finishing.Clear(); _waiting.Clear(); };
    }

    private bool Active => _world.Admitted && _world.Divergence == null && _coop.Objects.Initialized && _coop.Activity.Get(_world.Local) != null;
    /// <summary>A panel request is waiting for the host: saves and snapshots wait for it.</summary>
    internal bool Busy => _pending.Count > 0 || _finishing.Count > 0 || _waiting.Count > 0;
    /// <summary>0.73: a panel change the other player started isn't shown here yet.</summary>
    internal bool ShowingOther => _waiting.Count > 0;

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

    /// <summary>A removal long press. Returns whether the native removal may run now. Anything but a case panel is a
    /// component edit (task 4e).</summary>
    internal bool RequestRemove(ComponentPC comp, EditGesture gesture, System.Action replay)
    {
        var slot = comp?.Slot;
        var (pc, fact) = Shared(slot);
        if (fact == null) return _coop.Edits.RequestRemove(comp, gesture, replay);
        if (fact.IntegralPart == "") { Refuse("That panel is already off in the shared world."); return false; }
        return Submit(new Request { Pc = pc.Id, Slot = fact.Id, Remove = true, Replay = replay, Work = CurrentState },
            CasePanelWorld.Remove(_coop.Activity.Get(_world.Local)!.Epoch, pc.Id, fact.Id, fact.Version), $"removal of {slot.name}");
    }

    private bool RequestPipedOwnerRemoval(WorkingOnComputerState work, ComponentPC owner)
    {
        var slot = owner.Slot;
        var mesh = work?.m_case;
        if (slot == null || mesh == null || slot.GetCase()?.Pointer != mesh.Pointer || slot.GetComponent()?.Pointer != owner.Pointer)
        { Refuse("The cooler's main slot isn't bound to this case."); return false; }
        if (!owner.CanBeUninstalled()) { Refuse("Something is in the way of that cooler."); return false; }
        var removal = new WorkingOnComputerState.__c__DisplayClass68_0 { __4__this = work, comp = owner };
        return RequestRemove(owner, EditGesture.Remove, () => removal._DisassemblyTick_b__2());
    }

    /// <summary>An install commit. Returns whether the native install may run now.</summary>
    internal bool RequestRefit(Slot slot, System.Action replay)
    {
        var (pc, fact) = Shared(slot);
        var sku = slot?.GetComponent()?.GetPartInstance()?.GetPartId();
        if (fact == null || sku == null || sku != fact.IntegralHome) return _coop.Edits.RequestInstall(slot, sku, replay);
        return Submit(new Request { Pc = pc.Id, Slot = fact.Id, Remove = false, Replay = replay, Work = CurrentState },
            CasePanelWorld.Refit(_coop.Activity.Get(_world.Local)!.Epoch, pc.Id, fact.Id, fact.Version, sku), $"refit of {slot.name} with {sku}");
    }

    /// <summary>Inventory selection while working on a PC: refused while the other player changes that PC (task 4e).</summary>
    internal bool AllowSelection(Case forCase, PartInstance item) => _coop.Edits.AllowSelection(forCase);

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
            // The native removal coroutine can unplug these connectors before it removes the panel. The panel
            // change carries only the panel's state, so put the cable change through its own shared gesture first.
            if (comp.powerConnectors?.Any(connector => connector != null && connector.Connected) == true)
                return "Unplug the cables connected to that panel first.";
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
            if (!ctx.FromLocalPlayer)
            {
                // The other player is still turning screws or watching the animation: show it once they report it done.
                _waiting[ctx.Op] = (pc.Id, fact.Id);
                Plugin.Logger.LogInfo($"[panels] {what} by {ctx.Op}: shown here once the other player has finished it.");
                return;
            }
            var slot = _coop.Objects.Assembly.NativeFor(pc.Id, fact.Id)?.TryCast<Slot>();
            if (slot == null)
            {
                // Not shown here (a record-only PC): its next native load is checked against the model.
                SubmitFinish(ctx.Op, pc.Id, fact.Id, done: true);
                if (_coop.Objects.Projections.TryGet(pc.Id, out var mesh) && mesh != null) throw new InvalidDataException("the panel slot is not bound on this game");
                return;
            }
            // The requester runs the game's own gesture (animation, screws, inventory) if its work state is unchanged;
            // otherwise the panel changes directly.
            if (request?.Replay != null && request.Work != null && CurrentState?.Pointer == request.Work.Pointer)
            {
                NativeProjection.Run(request.Replay);
                Plugin.Logger.LogInfo($"[panels] {what} by {ctx.Op} (here).");
            }
            else
            {
                NativeProjection.Run(() => { if (c.Remove) RemoveDirect(slot); else RefitDirect(slot, c.Part); });
                Plugin.Logger.LogInfo($"[panels] {what} by {ctx.Op} (shown directly).");
            }
            _finishing.Add(new Finishing { Pc = pc.Id, SlotId = fact.Id, Slot = slot, Remove = c.Remove, Token = ctx.Op, Started = Time.realtimeSinceStartup });
            Update();
        }
        catch (Exception e) { _coop.ProjectionFailed($"The {what} was accepted, but this game could not show it", e); }
    }

    public void Finished(in MutationContext ctx, PcFact pc, AssemblyResource fact, OperationId token, bool done)
    {
        var remove = fact.IntegralPart == "";
        var what = $"{(done == remove ? "removal" : "refit")} of panel {fact.Id} on PC {pc.Id} by {token}";
        if (ctx.FromLocalPlayer)
        {
            Plugin.Logger.LogInfo($"[panels] {what}: {(done ? "finished" : "taken back")} here.");
            return;
        }
        var waited = _waiting.Remove(token);
        Show(pc, fact, $"{what}: {(done ? "finished" : "taken back")}", waited);
    }

    /// <summary>Make this game's panel match the shared world's (the other player finished, took back or left).</summary>
    private void Show(PcFact pc, AssemblyResource fact, string what, bool waited)
    {
        var remove = fact.IntegralPart == "";
        try
        {
            var slot = _coop.Objects.Assembly.NativeFor(pc.Id, fact.Id)?.TryCast<Slot>();
            if (slot == null)
            {
                if (_coop.Objects.Projections.TryGet(pc.Id, out var mesh) && mesh != null) throw new InvalidDataException("the panel slot is not bound on this game");
                return;
            }
            // A guest that joined meanwhile loaded the host's world as it was then; show whatever the model says now.
            if (Shows(slot, remove))
            {
                Plugin.Logger.LogInfo($"[panels] {what}; this game already shows it.");
                return;
            }
            ShowNative(pc.Id, slot, remove, fact.IntegralHome, what);
            Plugin.Logger.LogInfo($"[panels] {what}; showing it here{(waited ? "" : " (it was not waiting)")}.");
            _finishing.Add(new Finishing { Pc = pc.Id, SlotId = fact.Id, Slot = slot, Remove = remove, Started = Time.realtimeSinceStartup });
            Update();
        }
        catch (Exception e) { _coop.ProjectionFailed($"The {what}, but this game could not show it", e); }
    }

    /// <summary>0.71 (owner decision, November 12, 2026): show the other player's finished panel change with the game's own
    /// removal or install (animation, automatic screws), the way part edits are replayed, so both games run the same
    /// code; the PCI lock's bracket screws, for one, depend on it. Falls back to the direct change if the replay fails.</summary>
    private void ShowNative(NetObjectId pc, Slot slot, bool remove, string sku, string what)
    {
        void Direct(string why)
        {
            Plugin.Logger.LogWarning($"[panels] {what}: the game's own {(remove ? "removal" : "install")} couldn't be shown ({why}); changing the panel directly.");
            try { NativeProjection.Run(() => { if (remove) RemoveDirect(slot); else RefitDirect(slot, sku); }); }
            catch (Exception e) { _coop.ProjectionFailed($"The {what}, but this game could not show it", e); }
        }
        try
        {
            var plan = new NativeEditReplay.Plan
            {
                Label = $"panel {(remove ? "removal" : "refit")} at {slot.name}", Case = slot.GetCase(), Bench = _coop.Objects.FindBench(pc), Slot = slot,
                Known = true, KnownAt = Time.realtimeSinceStartup, Mode = remove ? 0 : 1, Selected = remove ? 0 : 1,
            };
            if (plan.Case == null) throw new InvalidDataException("the PC is not shown on this game");
            if (remove)
            {
                var comp = slot.GetComponent();
                if (comp == null || NativeAssembly.IsPreview(comp)) throw new InvalidDataException("the panel isn't fitted here");
                plan.Component = comp; plan.Direct = true;
            }
            else
            {
                // The game's install takes an inventory item itself (this player's Removed Components); else a new one.
                PartInstance part = null;
                if (CareerStatus.Get()?.GetInventory() is { } owned) foreach (var p in owned) if (p != null && p.GetPartId() == sku) { part = p; break; }
                plan.Part = part ?? PartInstance.NewPartInstance(sku, 0, null) ?? throw new InvalidDataException($"panel {sku} is unknown");
            }
            plan.Finished = error => { if (error != null) Direct(error); };
            CoopController.Instance.Replays.Start(plan);
        }
        catch (Exception e) { Direct(e.Message); }
    }

    private void SubmitFinish(OperationId token, NetObjectId pc, NetObjectId slot, bool done)
    {
        var actor = _coop.Activity.Get(_world.Local);
        // A rejoin or a new world already released the claim.
        if (actor?.Pending?.Token != token) return;
        if (!_world.Submit(CommandTypes.CasePanel, CasePanelWorld.Finish(actor.Epoch, token, pc, slot, done), out var op))
            Plugin.Logger.LogWarning($"[panels] the end of panel change {token} could not be sent; the host's world keeps the PC held until this player leaves.");
        else Plugin.Logger.LogInfo($"[panels] panel change {token} {(done ? "finished" : "stopped with the panel as it was")} here; sent ({op}).");
    }

    private static bool Shows(Slot slot, bool remove)
    {
        var comp = slot.GetComponent();
        var fitted = comp != null && !NativeAssembly.IsPreview(comp);
        return remove ? !fitted : fitted;
    }

    /// <summary>Per frame: once an accepted panel change has finished natively (no edit state running), the requester
    /// reports it and the PC is rebound. The other player's change waits while they still hold the PC.</summary>
    public void Update()
    {
        var now = Time.realtimeSinceStartup;
        foreach (var (token, (pcId, slotId)) in _waiting.ToArray())
        {
            // The requester left (or rejoined) without finishing: the host's world keeps the accepted change.
            if (_coop.Activity.Get(token.Actor)?.Pending?.Token == token) continue;
            _waiting.Remove(token);
            var pc = _coop.Objects.Model.Pcs.SingleOrDefault(p => p.Id == pcId);
            var fact = pc?.Resources.SingleOrDefault(r => r.Id == slotId);
            if (pc != null && fact != null) Show(pc, fact, $"panel change {token} on PC {pcId}: the other player left before finishing it", true);
        }
        for (var i = _finishing.Count - 1; i >= 0; i--)
        {
            var f = _finishing[i];
            try
            {
                if (f.Slot == null)
                {
                    // The PC was reloaded meanwhile; its load is checked instead and shows the shared world's panel.
                    _finishing.RemoveAt(i);
                    if (f.Token is { } reloaded) SubmitFinish(reloaded, f.Pc, f.SlotId, done: true);
                    continue;
                }
                var editing = GameController.Get()?.GetCurrentState()?.TryCast<InstallingPartState>() != null;
                if (editing) f.SawEditing = true;
                if (!editing && Shows(f.Slot, f.Remove))
                {
                    _finishing.RemoveAt(i);
                    if (f.Token is { } token) SubmitFinish(token, f.Pc, f.SlotId, done: true);
                    _coop.Objects.Assembly.RequestRebind(f.Pc);
                    continue;
                }
                if (f.Token is { } own)
                {
                    // The player left the gesture (screws) with the panel as it was, or it never started: take it back.
                    if (!editing && (f.SawEditing || now - f.Started > FinishTimeout))
                    {
                        _finishing.RemoveAt(i);
                        SubmitFinish(own, f.Pc, f.SlotId, done: false);
                        _coop.Objects.Assembly.RequestRebind(f.Pc);
                    }
                    continue;
                }
                if (now - f.Started > FinishTimeout) throw new TimeoutException($"the panel was still {(f.Remove ? "fitted" : "missing")} after {FinishTimeout} s");
            }
            catch (Exception e)
            {
                _finishing.RemoveAt(i);
                _coop.ProjectionFailed($"A case panel change on PC {f.Pc} was accepted, but this game could not finish it", e);
            }
        }
    }

    /// <summary>The game's removal teardown without its edit state: unregister, destroy, rebuild the case, and keep the
    /// panel in this player's Removed Components (as the native removal does), unless <paramref name="inventory"/> is false
    /// (a load that showed a panel the shared world has off: the panel was never this player's to keep).</summary>
    internal static void RemoveDirect(Slot slot, bool inventory = true)
    {
        var comp = slot.GetComponent();
        if (comp == null) return;
        if (NativeAssembly.IsPreview(comp)) throw new InvalidDataException("this game is previewing a part in that slot");
        var part = comp.GetPartInstance(); var mesh = slot.GetCase();
        mesh.OnChangePart(comp.gameObject, true, false);
        slot.SetComponentNull();
        Object.DestroyImmediate(comp.gameObject);
        mesh.UpdateGameStatus(); mesh.PostChangePart();
        if (inventory && part != null) CareerStatus.Get()?.AddItemToInventory(part, false, false, true, false);
        if (slot.GetComponent() != null) throw new InvalidDataException("the panel is still fitted");
    }

    /// <summary>The game's load-time panel instantiation (<c>Slot.DoAutoInstantiation</c>): a loaded part instance in
    /// the slot, taken from this player's Removed Components when it is there (and <paramref name="inventory"/> is true).</summary>
    internal static void RefitDirect(Slot slot, string sku, bool inventory = true)
    {
        var existing = slot.GetComponent();
        if (existing != null && !NativeAssembly.IsPreview(existing)) return;
        if (existing != null) throw new InvalidDataException("this game is previewing a part in that slot");
        PartInstance part = null;
        var owned = inventory ? CareerStatus.Get()?.GetInventory() : null;
        if (owned != null) foreach (var p in owned) if (p != null && p.GetPartId() == sku) { part = p; break; }
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

    [ThreadStatic] private static IntPtr s_refittingCase;

    internal static IntPtr BeginRefitSettle(InstallingPartState install)
    {
        var previous = s_refittingCase;
        var slot = install.m_slot;
        if (slot != null && Current?._finishing.Any(change => !change.Remove && change.Slot != null && change.Slot.Pointer == slot.Pointer) == true)
            s_refittingCase = install.m_case?.Pointer ?? IntPtr.Zero;
        return previous;
    }

    internal static void EndRefitSettle(IntPtr previous) => s_refittingCase = previous;

    /// <summary>The panel change carries no wiring. Leave its leads disconnected until a shared cable gesture.</summary>
    [HarmonyPatch(typeof(Case), nameof(Case.ConnectCables))]
    internal static class RefitAutoCableGuard
    {
        private static bool Prefix(Case __instance) => s_refittingCase == IntPtr.Zero || __instance.Pointer != s_refittingCase;
    }

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

    /// <summary>Long presses on a part in disassembly mode (the three <c>DisassemblyTick</c> callbacks, decompile 4e):
    /// b__0 installs a clicked placeholder (<c>m_isPlaceholder</c>), b__1 removes directly and b__2 runs the
    /// <c>Uninstall</c> coroutine (cables unplug first).</summary>
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
                // Removing only the CPU-side proxy leaves the AIO's radiator and pipe links behind in the save.
                if (method.Name == "_DisassemblyTick_b__2" && closure.comp?.GetProxiedComponent() is { m_includesPipes: true } owner)
                    return Current.RequestPipedOwnerRemoval(closure.__4__this, owner);
                System.Action replay = () => method.Invoke(closure, null);
                return method.Name switch
                {
                    "_DisassemblyTick_b__0" => Current.RequestRefit(closure.comp?.Slot, replay),
                    "_DisassemblyTick_b__1" => Current.RequestRemove(closure.comp, EditGesture.RemoveDirect, replay),
                    _ => Current.RequestRemove(closure.comp, EditGesture.Remove, replay),
                };
            }
            catch (Exception e) { Plugin.Logger.LogError("[panels] removal request failed: " + e); return false; }
        }
    }

    /// <summary>Removing a water pipe (phase 10: a water-cooling edit, shown on the other game by reloading the PC).</summary>
    [HarmonyPatch(typeof(WorkingOnComputerState.__c__DisplayClass68_1), "_DisassemblyTick_b__3")]
    internal static class PipeRemovalIntercept
    {
        private static bool Prefix(WorkingOnComputerState.__c__DisplayClass68_1 __instance)
        {
            if (Bypass) return true;
            try
            {
                var edits = CoopController.Instance?.Edits;
                if (edits == null) return NotReady();
                var closure = __instance;
                var mesh = GameController.Get()?.GetCurrentState()?.TryCast<WorkingOnComputerState>()?.m_case ?? closure.pipe?.GetComponentInParent<Case>();
                return edits.RequestWater(mesh, "pipe removal", () => closure._DisassemblyTick_b__3());
            }
            catch (Exception e) { Plugin.Logger.LogError("[panels] pipe removal request failed: " + e); return false; }
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

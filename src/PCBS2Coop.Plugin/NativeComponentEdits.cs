using HarmonyLib;
using PCBS2Coop.Network;
using UnityEngine;

namespace PCBS2Coop;

/// <summary>
/// Native side of component edits (task 4e, owner decisions October 27, 2026). The editor's gesture becomes a host-ordered
/// <c>Begin</c>; once accepted, the stored long-press callback runs the game's own edit here. This class follows that
/// edit: it reports <c>Progress</c> as soon as the edit started (and when a removal passes its cable step), then commits
/// what the game ended with (the <c>InstallingPartState</c> settling, or the removal coroutine stopping after Esc in the
/// cable step). The other game starts its replay (<see cref="NativeEditReplay"/>) on the progress, so both games animate
/// at the same time (owner decision, October 30, 2026); the replay waits at the orientation step and before settling
/// until the commit brings the editor's final choices. The host
/// settles once its own game shows the result: it captures that PC (part identity by native instance, prefab resources
/// by binding) and publishes it. Every PC is rebound after the settle, once no edit or replay is running there.
/// </summary>
internal sealed partial class NativeComponentEdits : IEditNative
{
    private sealed class Request
    {
        public NetObjectId Pc, Slot;
        public EditGesture Gesture;
        public string Sku = "";
        public System.Action Replay;
        public State Work;
        public Slot NativeSlot;
        public Case Mesh;
        public int InventoryBefore;
        /// <summary>Power edits: whether the press turns the PC on.</summary>
        public bool PowerOn;
    }
    /// <summary>The edit this player is running natively (one at a time).</summary>
    private sealed class Local
    {
        public OperationId Token;
        public Request Request;
        public InstallingPartState State;
        public bool Entered, Done, Committed;
        /// <summary>Part edits: the progress reports sent so far (started; a removal past its cable step).</summary>
        public bool StartSent, PartStepSent;
        public EditResult Result;
        public float Since;
        /// <summary>Cable and power edits: the PC before the edit, and since when nothing has moved on it.</summary>
        public WireState Before;
        /// <summary>Water-cooling edits: the PC record before the edit, then the finished one (both compressed).</summary>
        public byte[] RecordBefore, Record;
        public float Started, QuietSince;
    }

    private readonly CoopController _coop;
    private readonly WorldSync _world;
    private readonly Dictionary<OperationId, Request> _pending = new();
    private Request _submitting;
    private Local _local;
    private readonly List<OperationId> _cancel = new();
    /// <summary>Host: native part instance → part ID of each edited PC before its edit, for the settle capture.</summary>
    private readonly Dictionary<OperationId, Dictionary<long, NetObjectId>> _identities = new();
    /// <summary>Host: edits whose result its game shows (or whose replay failed); settled next frame.</summary>
    private readonly HashSet<OperationId> _ready = new();
    /// <summary>Host: the edit whose settle is being submitted (the host's Submit resolves synchronously).</summary>
    private OperationId? _settling;
    /// <summary>The commit of this player's edit, until the host answers.</summary>
    private OperationId? _commitOp;
    /// <summary>The other player's part edits this game started replaying on their progress, before their commit.</summary>
    private sealed class LiveReplay { public NativeEditReplay.Plan Plan; public bool Over; }
    private readonly Dictionary<OperationId, LiveReplay> _live = new();
    public const float EditTimeout = 120f;
    public ComponentEditWorld Handler { get; }
    private static NativeComponentEdits Current => CoopController.Instance?.Edits;

    /// <summary>Slot types whose edits are shared. Owner decisions (October 27, 2026): custom-loop parts are deferred; cable
    /// accessories (splitters, adapters) are shared since phase 5b; slot types not probed yet stay refused.</summary>
    private static readonly HashSet<Slot.SlotType> Shared = new()
    {
        Slot.SlotType.MOTHERBOARD, Slot.SlotType.CPU, Slot.SlotType.CPU_COOLER, Slot.SlotType.RAM, Slot.SlotType.PCI,
        Slot.SlotType.PCI_COVER, Slot.SlotType.STORAGE, Slot.SlotType.M2, Slot.SlotType.PSU, Slot.SlotType.CASE_COOLER,
        Slot.SlotType.RADIATOR, Slot.SlotType.GPU_PROXY, Slot.SlotType.USB, Slot.SlotType.POWER_SPLITTER, Slot.SlotType.POWER_ADAPTER,
    };

    public NativeComponentEdits(CoopController coop, WorldSync world)
    {
        _coop = coop; _world = world;
        Handler = new ComponentEditWorld(coop.Objects.Model, coop.Activity, this);
        world.Accepted += (OperationId op, ulong revision) => { _pending.Remove(op); if (_commitOp == op) _commitOp = null; };
        world.Rejected += OnRejected;
        world.GenerationStarted += (uint generation, string reason) => Reset();
    }

    internal void Reset()
    {
        Handler.Reset(); _pending.Clear(); _local = null; _cancel.Clear(); _identities.Clear(); _ready.Clear(); _settling = null; _commitOp = null;
        _live.Clear(); _previewWait.Clear();
        ResetWiring(); _reloads.Clear();
    }

    private bool Active => _world.Admitted && _world.Divergence == null && _coop.Objects.Initialized && _coop.Activity.Get(_world.Local) != null;
    /// <summary>An edit is waiting, running or being shown: saves and snapshots wait for it.</summary>
    internal bool Busy => Handler.Edits.Count > 0 || _pending.Count > 0 || _local != null || _ready.Count > 0 || _wireRuns.Count > 0 || _reloads.Count > 0;
    /// <summary>An edit claim this game's model holds for an edit in progress (activity must not release it).</summary>
    internal bool Holds(OperationId token) => Handler.Find(token) != null;
    private static State CurrentState => GameController.Get()?.GetCurrentState();
    private void Refuse(string why) => _coop.Notice("Refused: " + why.Replace(Handler.Name + ": ", ""));

    private static int InventoryCount(string sku)
    {
        var inventory = CareerStatus.Get()?.GetInventory();
        var n = 0;
        if (inventory != null && sku != null) foreach (var p in inventory) if (p != null && p.GetPartId() == sku) n++;
        return n;
    }

    /// <summary>The shared slot resource a native slot is, with why it can't be edited in co-op (or null).</summary>
    private (PcFact Pc, AssemblyResource Slot, string Why) Find(Slot slot)
    {
        if (slot == null) return (null, null, "That slot isn't shared in co-op yet.");
        if (!Shared.Contains(slot.m_slotType)) return (null, null, "That kind of part isn't shared in co-op yet.");
        var (pcId, id) = _coop.Objects.Assembly.Find(slot);
        var pc = _coop.Objects.Model.Pcs.SingleOrDefault(p => p.Id == pcId);
        var fact = pc?.Resources.SingleOrDefault(r => r.Id == id);
        if (fact is not { Kind: AssemblyResourceKind.Slot }) return (null, null, "That slot isn't shared in co-op yet.");
        if (Handler.Busy(pc.Id, _world.Local) is { } busy) return (null, null, busy);
        if (CoopController.Instance?.Replays.Running(slot.GetCase()) == true) return (null, null, "Your game is still showing the other player's change on that PC.");
        return (pc, fact, null);
    }

    // ---------------- requests (local input) ----------------

    /// <summary>An install commit (or a click on a placeholder). Returns whether the native install may run now.</summary>
    internal bool RequestInstall(Slot slot, string sku, System.Action replay)
    {
        if (IsWater(slot, string.IsNullOrEmpty(sku) ? null : PartsDatabase.s_instance?.GetDesc(sku, null), null))
            return RequestWater(slot.GetCase(), $"install {sku}", replay);
        var (pc, fact, why) = Find(slot);
        if (why != null) { Refuse(why); return false; }
        if (string.IsNullOrEmpty(sku)) { Refuse("That part isn't known."); return false; }
        return Submit(new Request { Pc = pc.Id, Slot = fact.Id, Gesture = EditGesture.Install, Sku = sku, Replay = replay, Work = CurrentState,
                NativeSlot = slot, Mesh = slot.GetCase(), InventoryBefore = InventoryCount(sku) },
            ComponentEditWorld.Begin(_coop.Activity.Get(_world.Local)!.Epoch, pc.Id, fact.Id, fact.Version, EditGesture.Install, sku, default),
            $"install of {sku} into {slot.name}");
    }

    /// <summary>A removal long press. Returns whether the native removal may run now.</summary>
    internal bool RequestRemove(ComponentPC comp, EditGesture gesture, System.Action replay)
    {
        var slot = comp?.Slot;
        if (comp != null && IsWater(slot, comp.GetPartInstance()?.GetPart(), comp))
            return RequestWater(slot?.GetCase() ?? comp.GetComponentInParent<Case>(), $"removal {comp.GetPartInstance()?.GetPartId()}", replay);
        var (pc, fact, why) = Find(slot);
        if (why != null) { Refuse(why); return false; }
        if (fact.Occupant.IsNone && fact.IntegralPart == "") { Refuse("That part isn't in the shared world."); return false; }
        return Submit(new Request { Pc = pc.Id, Slot = fact.Id, Gesture = gesture, Replay = replay, Work = CurrentState, NativeSlot = slot, Mesh = slot.GetCase() },
            ComponentEditWorld.Begin(_coop.Activity.Get(_world.Local)!.Epoch, pc.Id, fact.Id, fact.Version, gesture, "", fact.Occupant),
            $"removal of {comp.name} from {slot.name}");
    }

    /// <summary>Inventory selection while working on a PC: refused while the other player changes that PC.</summary>
    internal bool AllowSelection(Case forCase)
    {
        var id = _coop.Objects.IdOf(forCase);
        if (!id.IsNone && Handler.Busy(id, _world.Local) is { } busy) { Refuse(busy); return false; }
        return true;
    }

    private bool Submit(Request request, byte[] payload, string what)
    {
        if (!Active) { Refuse("The shared world isn't ready on this game yet."); return false; }
        if (_local != null || _pending.Count > 0) { Refuse("Your previous change is still finishing."); return false; }
        _submitting = request;
        try
        {
            if (!_world.Submit(CommandTypes.ComponentEdit, payload, out var op)) { Refuse("Not connected to the host's world."); return false; }
            if (!_world.IsAuthority && Handler.Find(op) == null) _pending[op] = request;
            Plugin.Logger.LogInfo($"[edits] {what} requested ({op}).");
        }
        finally { _submitting = null; }
        return false;
    }

    private void OnRejected(OperationId op, RejectReason reason, string text)
    {
        if (_settling is { } token && op.Actor == _world.Local)
        {
            _settling = null;
            // The host's own capture was refused: its game and the model now differ. Drop the edit so the PC is free again.
            _coop.ProjectionFailed($"The host could not settle component edit {token}", new InvalidDataException(text));
            _cancel.Add(token);
            return;
        }
        if (op.Actor != _world.Local) return;
        if (_commitOp == op)
        {
            // This game already shows the edit; the shared world doesn't. Drop it so the PC is free, and say so.
            _commitOp = null;
            var mine = _local?.Token;
            _local = null;
            _coop.ProjectionFailed("The host refused this game's finished component edit", new InvalidDataException(text));
            if (mine is { } t) _cancel.Add(t);
            return;
        }
        if (_submitting != null || _pending.Remove(op)) Refuse(text);
    }

    // ---------------- IEditNative ----------------

    public string CheckHost(in CommandContext ctx, PcFact pc, AssemblyResource fact, ComponentEdit edit)
    {
        if (edit.WholePc) return CheckWiredHost(pc, edit);
        var slot = _coop.Objects.Assembly.NativeFor(pc.Id, fact.Id)?.TryCast<Slot>();
        if (slot == null) return "That slot is missing on the host's game.";
        if (!Shared.Contains(slot.m_slotType)) return "That kind of part isn't shared in co-op yet.";
        if (CoopController.Instance?.Replays.Running(slot.GetCase()) == true) return "The host's game is still showing the previous change.";
        var comp = slot.GetComponent();
        if (comp != null && NativeAssembly.IsPreview(comp)) comp = null;
        if (!edit.Install)
        {
            if (comp == null) return "The host's game shows that slot empty.";
            if (!comp.CanBeUninstalled()) return "Something is in the way of that part.";
            if (!edit.Component.IsNone && pc.Parts.SingleOrDefault(p => p.Id == edit.Component)?.Sku != comp.GetPartInstance()?.GetPartId())
                return "The host's game shows a different part there.";
            return null;
        }
        if (comp != null) return "The host's game shows that slot filled.";
        var part = PartInstance.NewPartInstance(edit.Part, 0, null);
        if (part?.GetPart() == null) return "That part is unknown on the host's game.";
        return Fits(slot, part) ? null : "That part doesn't fit there on the host's game.";
    }

    /// <summary>The game's own fit check, as the editor's game ran it. Decompile: <c>Slot.CanFillWithItem</c> returns false
    /// while this game is paused (<c>GameController.paused</c>, for example its pause menu, or a window without focus),
    /// and when the part's model isn't loaded here yet (the prefab is a placeholder without its component). Neither says
    /// anything about the shared PC: the check runs unpaused, and is skipped (the editor's game checked it) for a part
    /// this game hasn't loaded (owner report, 0.17: a guest's air cooler was refused).</summary>
    private static bool Fits(Slot slot, PartInstance part)
    {
        if (!PartsDatabase.s_instance.HasLoadedPart(part.GetPart(), out _))
        {
            Plugin.Logger.LogInfo($"[edits] {part.GetPartId()} isn't loaded on this game; its fit into {slot.name} was checked by the editor's game.");
            return true;
        }
        var gc = GameController.Get();
        var paused = gc != null && gc.paused;
        if (paused) gc.paused = false;
        try { return slot.CanFillWithItem(part); }
        finally { if (paused) gc.paused = true; }
    }

    public void Began(in MutationContext ctx, PcFact pc, ComponentEdit edit)
    {
        var what = $"{edit.Gesture} at slot {edit.Slot} on PC {pc.Id} by {ctx.Op}";
        try
        {
            // The host remembers which native instance each part is before anything changes, for its settle capture.
            if (_world.IsAuthority) _identities[edit.Token] = Identities(pc);
            var request = ctx.FromLocalPlayer ? _submitting ?? _pending.GetValueOrDefault(ctx.Op) : null;
            _pending.Remove(ctx.Op);
            if (!ctx.FromLocalPlayer)
            {
                ClearPreviews(pc.Id);
                Plugin.Logger.LogInfo($"[edits] {what}: the other player is editing.");
                return;
            }
            if (request?.Replay == null || request.Work == null || CurrentState?.Pointer != request.Work.Pointer || (!edit.WholePc && request.NativeSlot == null))
            {
                Plugin.Logger.LogWarning($"[edits] {what}: this player's work state changed before the host answered; cancelling.");
                _cancel.Add(edit.Token);
                return;
            }
            _local = new Local { Token = edit.Token, Request = request, Since = Time.realtimeSinceStartup, Started = Time.realtimeSinceStartup };
            if (edit.Wired) _local.Before = Snapshot(request.Mesh);
            if (edit.Rebuilt) _local.RecordBefore = RecordBytes(pc.Id);
            NativeProjection.Run(request.Replay);
            Plugin.Logger.LogInfo($"[edits] {what}: running here.");
        }
        catch (Exception e)
        {
            _local = null;
            _coop.ProjectionFailed($"The {what} was accepted, but this game could not start it", e);
            if (ctx.FromLocalPlayer) _cancel.Add(edit.Token);
        }
    }

    public void Progressed(in MutationContext ctx, PcFact pc, ComponentEdit edit, EditProgress progress)
    {
        if (ctx.FromLocalPlayer) return;
        var what = $"{edit.Gesture} at slot {edit.Slot} on PC {pc.Id} by {edit.Token}";
        if (progress == EditProgress.PartStep)
        {
            if (_live.TryGetValue(edit.Token, out var live) && !live.Over) live.Plan.Held = false;
            Plugin.Logger.LogInfo($"[edits] {what}: the removal passed its cable step.");
            return;
        }
        try
        {
            // The other player's game started this edit: play it here now, with the game's own animations. The replay
            // waits at the orientation step and before settling until their commit says how they finished it.
            var plan = Plan(pc, edit);
            var replay = new LiveReplay { Plan = plan };
            _live[edit.Token] = replay;
            plan.Finished = Shown(edit.Token, what, replay);
            CoopController.Instance.Replays.Start(plan);
            Plugin.Logger.LogInfo($"[edits] {what}: started there; playing it here.");
        }
        catch (Exception e)
        {
            _live[edit.Token] = new LiveReplay { Over = true };
            _coop.ProjectionFailed($"The other player's {what} was accepted, but this game could not show it", e);
        }
    }

    /// <summary>A replay's completion: report a failure, and let the host settle once the edit is committed.</summary>
    private System.Action<string> Shown(OperationId token, string what, LiveReplay live = null) => error =>
    {
        if (live != null) live.Over = true;
        if (error != null) _coop.ProjectionFailed($"The other player's {what} was accepted, but this game could not show it", new InvalidDataException(error));
        // A live replay can only fail before the commit; the host settles once the editor committed (or left).
        if (_world.IsAuthority && (live == null || Handler.Find(token) is not { Stage: EditStage.Running })) _ready.Add(token);
        if (error == null) Plugin.Logger.LogInfo($"[edits] {what}: shown here.");
    };

    public void Committed(in MutationContext ctx, PcFact pc, ComponentEdit edit)
    {
        var token = edit.Token;
        if (ctx.FromLocalPlayer)
        {
            if (_local?.Token == token) _local = null;
            if (_world.IsAuthority) _ready.Add(token);
            return;
        }
        var what = $"{edit.Gesture} at slot {edit.Slot} on PC {pc.Id} by {edit.Token}";
        try
        {
            if (edit.Wired) { StartWireReplay(pc, edit); return; }
            if (edit.Rebuilt) { StartReload(pc, edit); return; }
            if (_live.TryGetValue(token, out var live))
            {
                // Already playing since the editor started: finish it the way the editor did.
                if (live.Over) { if (_world.IsAuthority) _ready.Add(token); return; }
                Know(live.Plan, edit, edit.Result!);
                Plugin.Logger.LogInfo($"[edits] {what}: committed ({edit.Result!.Outcome}, {edit.Result.Degrees} degrees); finishing it here.");
                return;
            }
            var plan = Plan(pc, edit);
            plan.Finished = Shown(token, what);
            CoopController.Instance.Replays.Start(plan);
        }
        catch (Exception e)
        {
            _coop.ProjectionFailed($"The other player's {what} was accepted, but this game could not show it", e);
            if (_world.IsAuthority) _ready.Add(token);
        }
    }

    public void Cancelled(in MutationContext ctx, ComponentEdit edit)
    {
        if (_local?.Token == edit.Token) _local = null;
        if (_live.Remove(edit.Token, out var live) && !live.Over)
        {
            // This game was already playing it: what it shows now is half an edit.
            live.Over = true;
            CoopController.Instance?.Replays.Abort(live.Plan, "the other player's change was cancelled while this game showed it");
        }
        _identities.Remove(edit.Token); _ready.Remove(edit.Token);
        Plugin.Logger.LogInfo($"[edits] edit {edit.Token} cancelled.");
    }

    public void Settled(in MutationContext ctx, PcFact pc, ComponentEdit edit)
    {
        _identities.Remove(edit.Token); _ready.Remove(edit.Token); _live.Remove(edit.Token);
        if (_local?.Token == edit.Token) _local = null;
        // Rebind once nothing runs on that PC here (this game's replay may still be playing).
        _coop.Objects.Assembly.RequestRebind(pc.Id);
        Plugin.Logger.LogInfo($"[edits] edit {edit.Token} settled on PC {pc.Id} ({pc.Parts.Count} parts, {pc.Resources.Count} resources).");
    }

    /// <summary>Host: the edited PC as its game shows it. A part keeps its ID while it is the same native instance.</summary>
    public PcFact Capture(PcFact pc, ComponentEdit edit, NetObjectIdAllocator allocator)
    {
        if (!_coop.Objects.Projections.TryGet(pc.Id, out var mesh) || mesh == null) throw new InvalidDataException("That PC isn't shown on the host's game.");
        if (!_identities.TryGetValue(edit.Token, out var identities)) throw new InvalidDataException("The host lost that PC's part identities.");
        var record = _coop.Objects.CaptureRecord(pc.Id) ?? throw new InvalidDataException("That PC has no native record.");
        var facts = new List<PartFact>(); var links = new List<PartLink>(); var aliases = new Dictionary<long, NetObjectId>();
        var known = pc.Parts.ToDictionary(p => p.Id);
        var byPath = new Dictionary<string, NetObjectId>(StringComparer.Ordinal); var used = new HashSet<NetObjectId>();
        foreach (var link in pc.Links) byPath[link.Path] = link.Part;
        foreach (var (path, part) in NativeObjectState.Parts(record))
        {
            var pointer = part.Pointer.ToInt64();
            if (!aliases.TryGetValue(pointer, out var id))
            {
                // A water-cooling edit reloaded the PC on the other game, so native instances are new there: keep a part's
                // ID while the same SKU sits at the same path. Otherwise a part keeps its ID while it is the same instance.
                var found = edit.Rebuilt ? byPath.TryGetValue(path, out var atPath) && !used.Contains(atPath) ? atPath : default
                    : identities.TryGetValue(pointer, out var prior) ? prior : default;
                id = !found.IsNone && known.TryGetValue(found, out var fact) &&
                    fact.Sku == part.GetPartId() && fact.NativeType == part.GetIl2CppType().FullName ? found : allocator.Allocate();
                used.Add(id);
                aliases.Add(pointer, id);
                facts.Add(new(id, part.GetPartId(), part.GetIl2CppType().FullName));
            }
            links.Add(new(path, id));
        }
        var captured = new PcFact(pc.Id, pc.Provenance, pc.Location, facts, links) { Resources = pc.Resources, BuildProfile = pc.BuildProfile };
        captured = AssemblyManifest.Repair(captured, _coop.Objects.Assembly.Describe(captured, mesh, record), allocator);
        var added = facts.Where(f => !known.ContainsKey(f.Id)).Select(f => f.Sku); var removed = pc.Parts.Where(p => !aliases.ContainsValue(p.Id)).Select(p => p.Sku);
        Plugin.Logger.LogInfo($"[edits] settle capture of PC {pc.Id}: added [{string.Join(", ", added)}], removed [{string.Join(", ", removed)}].");
        // The host's game is the record: a part edit can turn the PC off, and a cable or power edit can turn it on or off.
        return captured with { BuildProfile = NativeAssembly.CaptureProfile(captured, mesh, record), Powered = pc.Location.Place == PcPlace.Bench && Running(mesh) };
    }

    /// <summary>Native part instances of a PC (from its last record, or a fresh one if that no longer matches the model).</summary>
    private Dictionary<long, NetObjectId> Identities(PcFact pc)
    {
        var record = _coop.Objects.RecordFor(pc.Id);
        try { NativeObjectState.CheckParts(pc, record); }
        catch (Exception) { record = _coop.Objects.CaptureRecord(pc.Id); NativeObjectState.CheckParts(pc, record); }
        var result = new Dictionary<long, NetObjectId>();
        foreach (var (path, part) in NativeObjectState.Parts(record)) result[part.Pointer.ToInt64()] = pc.Links.Single(l => l.Path == path).Part;
        return result;
    }

    /// <summary>The other player's previews on that PC go away before their slots change underneath them.</summary>
    private void ClearPreviews(NetObjectId pc)
    {
        var work = CurrentState?.TryCast<WorkingOnComputerState>();
        if (work?.m_case == null || _coop.Objects.IdOf(work.m_case) != pc || !NativeAssembly.HasPreview) return;
        NativeProjection.Run(work.ClearPlaceholders);
        Plugin.Logger.LogInfo("[edits] cleared this player's part previews: the other player is changing that PC.");
    }

    /// <summary>The replay of an edit on this game: a committed one, or one the editor only started (it then waits for
    /// <see cref="Know"/> at the orientation step and before settling, and a removal also after its cable step).</summary>
    private NativeEditReplay.Plan Plan(PcFact pc, ComponentEdit edit)
    {
        var slot = _coop.Objects.Assembly.NativeFor(pc.Id, edit.Slot)?.TryCast<Slot>() ?? throw new InvalidDataException("the slot is not bound on this game");
        var r = edit.Result;
        var plan = new NativeEditReplay.Plan
        {
            Label = $"{edit.Gesture} {edit.Token} at {slot.name}", Case = slot.GetCase(), Bench = _coop.Objects.FindBench(pc.Id), Slot = slot,
            Known = false, Held = r == null && edit.Gesture == EditGesture.Remove && !edit.PartStep,
        };
        if (r != null) Know(plan, edit, r);
        if (plan.Case == null) throw new InvalidDataException("the PC is not shown on this game");
        if (edit.Install)
        {
            PartInstance part = null;
            var fromInventory = r?.FromInventory ?? edit.Start?.FromInventory ?? false;
            var flipped = r?.Flipped ?? edit.Start?.Flipped ?? false;
            if (fromInventory)
            {
                var inventory = CareerStatus.Get()?.GetInventory();
                if (inventory != null) foreach (var p in inventory) if (p != null && p.GetPartId() == edit.Part) { part = p; break; }
                if (part == null) Plugin.Logger.LogWarning($"[edits] {plan.Label}: the editor used a {edit.Part} from its inventory, but this player's inventory has none; using a new one.");
            }
            part ??= PartInstance.NewPartInstance(edit.Part, 0, null) ?? throw new InvalidDataException($"part {edit.Part} is unknown");
            if (part.IsFlipped() != flipped) part.SetFlipped(flipped);
            plan.Part = part;
            return plan;
        }
        var comp = slot.GetComponent();
        if (comp == null || NativeAssembly.IsPreview(comp)) throw new InvalidDataException("the slot is empty on this game");
        if (!edit.Component.IsNone && pc.Parts.SingleOrDefault(p => p.Id == edit.Component)?.Sku != comp.GetPartInstance()?.GetPartId())
            throw new InvalidDataException("this game shows a different part in that slot");
        plan.Component = comp;
        plan.Direct = edit.Gesture == EditGesture.RemoveDirect;
        return plan;
    }

    /// <summary>The editor's final choices for a replay: orientation, tool, cables and (removals) whether Esc kept the
    /// part in. The replay finishes with them.</summary>
    private static void Know(NativeEditReplay.Plan plan, ComponentEdit edit, EditResult r)
    {
        plan.Degrees = r.Degrees; plan.Mode = r.Mode; plan.Selected = r.Selected;
        plan.Cables = new NativeEditReplay.CableChoice { Type = r.CableType, Connector = r.CableConnector, Pins = r.Pins.ToArray() };
        plan.UnplugOnly = r.Outcome == EditOutcome.Unplugged;
        if (edit.Install) plan.Flip = r.Flipped;
        if (edit.Install && edit.Start is { } start && start.FromInventory != r.FromInventory)
            Plugin.Logger.LogWarning($"[edits] {plan.Label}: the editor's part source changed after it started (inventory {start.FromInventory} -> {r.FromInventory}).");
        plan.Held = false;
        plan.Known = true;
        plan.KnownAt = Time.realtimeSinceStartup;
    }

    // ---------------- per frame ----------------

    public void Update()
    {
        if (!_world.Admitted) return;
        foreach (var token in _cancel.ToArray())
        {
            _cancel.Remove(token);
            if (Handler.Find(token) is not { } edit) continue;
            var actor = _coop.Activity.Get(_world.Local);
            if (actor == null || (!_world.IsAuthority && edit.Actor != _world.Local)) continue;
            _world.Submit(CommandTypes.ComponentEdit, ComponentEditWorld.Cancel(actor.Epoch, token), out _);
        }
        UpdateLocal();
        UpdateWireRuns();
        UpdateReloads();
        if (_world.IsAuthority) UpdateHost();
        UpdatePower();
    }

    private void UpdateLocal()
    {
        if (_local == null || _local.Committed) return;
        if (Handler.Find(_local.Token) is not { Stage: EditStage.Running })
        {
            _local = null;
            return;
        }
        if (!_local.Done && _local.Before != null)
        {
            PollWired();
            if (_local == null) return;
        }
        if (!_local.Done && _local.RecordBefore != null)
        {
            PollWater();
            if (_local == null) return;
        }
        if (!ComponentEditWorld.IsWholePc(_local.Request.Gesture) && !SendProgress()) return;
        if (_local.Done)
        {
            _local.Committed = true;
            var actor = _coop.Activity.Get(_world.Local);
            Plugin.Logger.LogInfo($"[edits] edit {_local.Token} finished here ({_local.Result.Outcome}, {_local.Result.Degrees} degrees, mode {_local.Result.Mode}); committing.");
            if (actor != null && SendRecord(actor.Epoch) && _world.Submit(CommandTypes.ComponentEdit, ComponentEditWorld.Commit(actor.Epoch, _local.Token, _local.Result), out var op))
            {
                if (!_world.IsAuthority) _commitOp = op;
            }
            else
            {
                _coop.ProjectionFailed($"This game's component edit {_local.Token} could not be sent to the host", new InvalidDataException("not connected"));
                _local = null;
            }
            return;
        }
        if (Time.realtimeSinceStartup - _local.Since > EditTimeout)
        {
            var token = _local.Token; _local = null;
            _coop.ProjectionFailed($"This game's component edit {token} did not finish", new TimeoutException($"still running after {EditTimeout} s"));
            _cancel.Add(token);
        }
    }

    /// <summary>Report this player's part edit as it goes (sent from here, never from inside Apply): started (an install
    /// once its native state exists, with the part's source and flip), then a removal past its cable step. Returns false
    /// when the edit was dropped.</summary>
    private bool SendProgress()
    {
        var install = _local.Request.Gesture == EditGesture.Install;
        if (!_local.StartSent)
        {
            if (install && _local.State == null) return true; // its install state isn't made yet
            var start = install
                ? new EditStart(_local.Request.InventoryBefore > InventoryCount(_local.Request.Sku), _local.State.m_part?.IsFlipped() == true)
                : null;
            if (!Progress(EditProgress.Started, start)) return false;
            _local.StartSent = true;
        }
        if (_local.Request.Gesture == EditGesture.Remove && _local.Entered && !_local.PartStepSent)
        {
            if (!Progress(EditProgress.PartStep, null)) return false;
            _local.PartStepSent = true;
        }
        return true;

        bool Progress(EditProgress progress, EditStart start)
        {
            var actor = _coop.Activity.Get(_world.Local);
            if (actor != null && _world.Submit(CommandTypes.ComponentEdit, ComponentEditWorld.Progress(actor.Epoch, _local.Token, progress, start), out _))
            {
                Plugin.Logger.LogInfo($"[edits] edit {_local.Token}: {progress} sent{(start != null ? $" (inventory {start.FromInventory}, flipped {start.Flipped})" : "")}.");
                return true;
            }
            _coop.ProjectionFailed($"This game's component edit {_local.Token} could not be reported to the host", new InvalidDataException("not connected"));
            _local = null;
            return false;
        }
    }

    private void UpdateHost()
    {
        foreach (var edit in Handler.Edits)
        {
            // An editor that left (its claim went with it) never commits: settle from the host's game as it is. A replay
            // already playing it finishes as far as the editor got.
            if (edit.Stage == EditStage.Running && edit.Actor != _world.Local && _coop.Activity.Get(edit.Actor)?.Pending?.Token != edit.Token)
            {
                if (_live.TryGetValue(edit.Token, out var live) && !live.Over && !live.Plan.Known)
                {
                    Know(live.Plan, edit, EditResult.Done() with { Outcome = edit.Gesture == EditGesture.Remove && !edit.PartStep ? EditOutcome.Unplugged : EditOutcome.Done });
                    Plugin.Logger.LogWarning($"[edits] edit {edit.Token}: the editor left before committing; finishing the replay with defaults.");
                }
                _ready.Add(edit.Token);
            }
        }
        foreach (var token in _ready.ToArray())
        {
            if (Handler.Find(token) is not { } edit) { _ready.Remove(token); continue; }
            if (!Stable(edit.Pc, out var previews))
            {
                // Only this player's own part previews (for example the game re-showing the held GPU right after a PCI
                // cover came out) keep the settle waiting, and with it every next edit ("still finishing"). The capture
                // can't include a preview, so put them away after a moment (owner report, 0.17).
                if (!previews) { _previewWait.Remove(token); continue; }
                var since = _previewWait.TryGetValue(token, out var t) ? t : _previewWait[token] = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - since < PreviewGrace) continue;
                _previewWait.Remove(token);
                if (PutPreviewsAway(edit.Pc)) _coop.Notice("Your last change is shared now. Select the part again.");
                continue;
            }
            _previewWait.Remove(token);
            _ready.Remove(token);
            _settling = token;
            try { _world.Submit(CommandTypes.ComponentEdit, ComponentEditWorld.Settle(token), out _); }
            finally { _settling = null; }
        }
    }

    /// <summary>Host: nothing is moving on that PC here (no edit state on it, no replay, no preview, no moving latch).
    /// <paramref name="previews"/>: the only thing in the way is a part preview.</summary>
    private bool Stable(NetObjectId pc, out bool previews)
    {
        previews = false;
        if (!_coop.Objects.Projections.TryGet(pc, out var mesh) || mesh == null) return true; // not shown here: the capture refuses, and the edit is dropped
        if (CoopController.Instance.Replays.Running(mesh) || WireMoving(mesh)) return false;
        var install = CurrentState?.TryCast<InstallingPartState>();
        if (install?.m_case != null && install.m_case.Pointer == mesh.Pointer) return false;
        foreach (var pin in mesh.GetComponentsInChildren<CPin>(true))
            if (pin != null && pin.IsAnimating()) return false;
        foreach (var slot in mesh.GetComponentsInChildren<Slot>(true))
            if (slot.GetComponent() is { } c && NativeAssembly.IsPreview(c)) { previews = true; return false; }
        return true;
    }

    private const float PreviewGrace = 0.3f;
    private readonly Dictionary<OperationId, float> _previewWait = new();

    /// <summary>Put away this player's part previews on that PC (the game's own <c>ClearPlaceholders</c>).</summary>
    private bool PutPreviewsAway(NetObjectId pc)
    {
        var work = CurrentState?.TryCast<WorkingOnComputerState>();
        if (work?.m_case == null || _coop.Objects.IdOf(work.m_case) != pc) return false;
        NativeProjection.Run(work.ClearPlaceholders);
        Plugin.Logger.LogInfo($"[edits] put this player's part previews on PC {pc} away so its last change can settle.");
        return true;
    }

    // ---------------- following this player's own edit ----------------

    private void Entered(InstallingPartState state)
    {
        if (_local == null || _local.Entered || state == null || ComponentEditWorld.IsWholePc(_local.Request.Gesture)) return;
        // Only one edit of this player runs at a time, so the next edit state on that PC (or in that slot) is it. A
        // removal state and a clicked placeholder may not carry the same slot object as the gesture.
        var slot = state.m_slot; var mesh = state.m_case;
        var match = (slot != null && slot.Pointer == _local.Request.NativeSlot.Pointer) ||
            (mesh != null && _local.Request.Mesh != null && mesh.Pointer == _local.Request.Mesh.Pointer);
        if (!match) return;
        _local.Entered = true; _local.State = state;
    }

    private void Settling(InstallingPartState state)
    {
        if (_local?.State == null || state == null || state.Pointer != _local.State.Pointer || _local.Done) return;
        var work = GameController.Get()?.GetPreviousState()?.TryCast<WorkingOnComputerState>();
        var cables = NativeEditReplay.CableChoice.Current();
        _local.Result = new EditResult(EditOutcome.Done, state.m_degrees, false, (byte)(int)(work?.m_mode ?? (WorkingOnComputerState.Mode)1),
            (byte)(int)(work?.m_currentSelected ?? (WorkingOnComputerState.Mode)1), false, cables.Type ?? "", cables.Connector ?? "", cables.Pins ?? Array.Empty<int>());
    }

    private void Settled(InstallingPartState state)
    {
        if (_local?.State == null || state == null || state.Pointer != _local.State.Pointer || _local.Done || _local.Result == null) return;
        var install = _local.Request.Gesture == EditGesture.Install;
        _local.Result = _local.Result with
        {
            Flipped = install && state.m_part?.IsFlipped() == true,
            FromInventory = install && _local.Request.InventoryBefore > InventoryCount(_local.Request.Sku),
        };
        _local.Done = true;
    }

    /// <summary>The removal coroutine stopped without starting the part step: the editor left during the cable step.</summary>
    private void RemovalStopped()
    {
        if (_local == null || _local.Entered || _local.Done || _local.Request.Gesture != EditGesture.Remove) return;
        var cables = NativeEditReplay.CableChoice.Current();
        _local.Result = new EditResult(EditOutcome.Unplugged, float.NaN, false, 0, 0, false, cables.Type ?? "", cables.Connector ?? "", cables.Pins ?? Array.Empty<int>());
        _local.Done = true;
        Plugin.Logger.LogInfo($"[edits] edit {_local.Token}: the removal stopped during its cable step (cables only).");
    }

    private static bool Ours => !NativeEditReplay.Active && Current?._local != null;

    [HarmonyPatch(typeof(GameController), nameof(GameController.SetCurrentState))]
    internal static class EnterTrace
    {
        private static void Postfix(State state)
        {
            if (!Ours) return;
            try { Current.Entered(state?.TryCast<InstallingPartState>()); }
            catch (Exception e) { Plugin.Logger.LogError("[edits] state trace failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(InstallingPartState), nameof(InstallingPartState.OnStateDestroy))]
    internal static class SettleTrace
    {
        private static void Prefix(InstallingPartState __instance)
        {
            if (!Ours) return;
            try { Current.Settling(__instance); }
            catch (Exception e) { Plugin.Logger.LogError("[edits] settle trace failed: " + e); }
        }
        private static void Postfix(InstallingPartState __instance)
        {
            if (!Ours) return;
            try { Current.Settled(__instance); }
            catch (Exception e) { Plugin.Logger.LogError("[edits] settle trace failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(WorkingOnComputerState._Uninstall_d__72), nameof(WorkingOnComputerState._Uninstall_d__72.MoveNext))]
    internal static class RemovalTrace
    {
        private static void Postfix(bool __result)
        {
            if (__result || !Ours) return;
            try { Current.RemovalStopped(); }
            catch (Exception e) { Plugin.Logger.LogError("[edits] removal trace failed: " + e); }
        }
    }
}

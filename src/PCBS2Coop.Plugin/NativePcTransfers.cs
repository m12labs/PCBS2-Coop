using HarmonyLib;
using PCBS2Coop.Network;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// Native side of host-ordered PC transfers (task 3f). The evidenced user-intent boundaries submit a request instead
/// of moving the PC: <c>BenchSlot.OnPickupPC</c> and the modding/delid bench pickups (bench → hands),
/// <c>WorkshopController.DropCase</c> (every drop and occupied-bench swap), <c>StoreCarriedCase</c> (cupboard) and
/// <c>StoredPC.PickUp</c> (retrieval). After acceptance the requesting game runs that same native call; the other game
/// projects the move from the accepted facts (pickup side effects then the bench's own removal, the bench loader,
/// the storage list) and shows a carried PC as a box with the other player.
/// </summary>
internal sealed class NativePcTransfers : IPcTransferNative
{
    private const float RecoverInterval = 1f;
    private sealed class Request
    {
        public string What;
        public StoredPC Storage;
    }
    private readonly CoopController _coop;
    private readonly WorldSync _world;
    private readonly Dictionary<OperationId, Request> _pending = new();
    private Request _submitting;
    private float _nextRecover;
    public PcTransfers Handler { get; }
    private NativeObjectState Objects => _coop.Objects;

    public NativePcTransfers(CoopController coop, WorldSync world)
    {
        _coop = coop; _world = world;
        Handler = new PcTransfers(coop.Objects.Model, coop.Activity, this, world.IsAuthority);
        world.Accepted += (OperationId op, ulong revision) => _pending.Remove(op);
        world.Rejected += OnRejected;
        world.GenerationStarted += (uint generation, string reason) => _pending.Clear();
    }

    private bool Active => _world.Admitted && _world.Divergence == null && Objects.Initialized && _coop.Activity.Get(_world.Local) != null;
    private static WorkshopController Workshop => Object.FindObjectOfType<WorkshopController>() ?? throw new InvalidDataException("Workshop unavailable.");
    private static SaveLoadSystem Saves => Object.FindObjectOfType<SaveLoadSystem>() ?? throw new InvalidDataException("Save system unavailable.");
    private ulong Epoch => _coop.Activity.Get(_world.Local)!.Epoch;
    /// <summary>A guest request is waiting for the host's answer.</summary>
    internal bool HasPending => _pending.Count > 0;

    // ---------------- requests (local input) ----------------

    internal void RequestPickup(BenchSlot bench)
    {
        if (!Ready("Pickup")) return;
        if (bench.m_caseLoadingToSlot) { Refuse("Pickup", "That PC is still loading."); return; }
        var id = Objects.IdOf(bench.GetCaseInSlot()); var at = Objects.LocationOf(bench);
        if (id.IsNone || at.IsNone) { Refuse("Pickup", "That PC has no co-op identity."); return; }
        Submit(PcTransfers.Pickup(Epoch, id, at), new Request { What = "Pickup" });
    }

    internal void RequestDrop(BenchSlot bench)
    {
        if (!Ready("Drop")) return;
        var wc = Workshop;
        if (wc.IsCaseLoading || bench.m_caseLoadingToSlot) { Refuse("Drop", "A PC is still loading."); return; }
        var id = Objects.IdOf(wc.GetCarriedCase()); var at = Objects.LocationOf(bench);
        var occupantMesh = bench.GetCaseInSlot(); var occupant = Objects.IdOf(occupantMesh);
        if (id.IsNone || at.IsNone || (occupantMesh != null && occupant.IsNone)) { Refuse("Drop", "That PC or bench has no co-op identity."); return; }
        Submit(PcTransfers.Drop(Epoch, id, at, occupant), new Request { What = occupant.IsNone ? "Drop" : "Swap" });
    }

    internal void RequestStore()
    {
        if (!Ready("Store")) return;
        var wc = Workshop;
        if (wc.IsCaseLoading) { Refuse("Store", "The PC is still loading."); return; }
        var id = Objects.IdOf(wc.GetCarriedCase());
        if (id.IsNone) { Refuse("Store", "The carried PC has no co-op identity."); return; }
        Submit(PcTransfers.Store(Epoch, id), new Request { What = "Store" });
    }

    internal void RequestRetrieve(StoredPC ui)
    {
        if (!Ready("Retrieve")) return;
        var id = Objects.IdOf(ui.m_computer);
        if (id.IsNone) { Refuse("Retrieve", "That stored PC has no co-op identity."); return; }
        Submit(PcTransfers.Retrieve(Epoch, id), new Request { What = "Retrieve", Storage = ui });
    }

    private bool Ready(string what)
    {
        if (!Active) { Refuse(what, "The shared world isn't ready on this game yet."); return false; }
        if (!_world.IsAuthority && (HasPending || _coop.Lifecycle?.HasPending == true || _coop.Workstations?.HasPending == true)) { Refuse(what, "Still waiting for the host to answer the previous request."); return false; }
        return true;
    }

    private void Submit(byte[] payload, Request request)
    {
        _submitting = request;
        try
        {
            if (!_world.Submit(CommandTypes.PcTransfer, payload, out var op)) { Refuse(request.What, "Not connected to the host's world."); return; }
            if (!_world.IsAuthority) _pending[op] = request;
            Plugin.Logger.LogInfo($"[objects] {request.What} requested ({op}).");
        }
        finally { _submitting = null; }
    }

    private void OnRejected(OperationId op, RejectReason reason, string text)
    {
        if (op.Actor != _world.Local) return;
        var request = _submitting;
        if (request == null && !_pending.Remove(op, out request)) return;
        Refuse(request.What, text);
    }

    private void Refuse(string what, string why) => _coop.Notice($"{what} refused: {why.Replace(Handler.Name + ": ", "")}");
    private Request LocalRequest(in MutationContext ctx) => !ctx.FromLocalPlayer ? null : _submitting ?? _pending.GetValueOrDefault(ctx.Op);

    // ---------------- departure recovery (host) ----------------

    public void Update()
    {
        if (_world.IsAuthority && Active && Time.realtimeSinceStartup >= _nextRecover) RecoverDeparted();
    }

    /// <summary>Host: a departed player's carried PCs go to the shared cupboard (owner decision, task 3a).</summary>
    internal void RecoverDeparted()
    {
        if (!_world.IsAuthority || !Active) return;
        _nextRecover = Time.realtimeSinceStartup + RecoverInterval;
        foreach (var seat in new[] { ActorSlot.Guest })
        {
            var present = _coop.Activity.Get(seat)?.Player ?? default;
            if (!Objects.Model.Pcs.Any(x => x.Location.Place == PcPlace.Carrier && x.Location.Actor == seat && x.Location.Player != present)) continue;
            _world.Submit(CommandTypes.PcTransfer, PcTransfers.Recover(Epoch, seat), out var op);
            Plugin.Logger.LogInfo($"[objects] recovering the departed {seat}'s carried PC to the cupboard ({op}).");
        }
    }

    // ---------------- IPcTransferNative ----------------

    public string CheckHost(in CommandContext ctx, TransferCommand c, IReadOnlyList<PcMove> moves)
    {
        var wc = Workshop;
        var host = ctx.Op.Actor == ActorSlot.Host;
        string Carried(NetObjectId id)
        {
            if (host) return !wc.IsCaseLoading && Objects.IdOf(wc.GetCarriedCase()) == id ? null : "The carried PC changed.";
            var pc = Objects.Model.Pcs.SingleOrDefault(x => x.Id == id);
            return pc != null && Objects.TryRemoteCarrier(pc, out _) ? null : "The guest's carried PC is unknown on the host.";
        }
        string HandsFree() => !host || (wc.CarryingEmpty() && !wc.IsCaseLoading) ? null : "Your hands are not empty.";
        switch (c.Action)
        {
            case TransferAction.Pickup:
            {
                var bench = Objects.BenchAt(moves[0].From.Key);
                if (bench == null) return "That bench is missing on the host's game.";
                if (bench.m_caseLoadingToSlot) return "That PC is still appearing on the host's game.";
                return Objects.IdOf(bench.GetCaseInSlot()) != c.Pc ? "The host's bench doesn't hold that PC." : HandsFree();
            }
            case TransferAction.Drop:
            {
                var bench = Objects.BenchAt(moves[^1].To.Key);
                if (bench == null) return "That bench is missing on the host's game.";
                if (bench.m_caseLoadingToSlot) return "That bench is still loading on the host's game.";
                var there = Objects.IdOf(bench.GetCaseInSlot());
                if (c.Occupant.IsNone ? bench.GetCaseInSlot() != null : there != c.Occupant) return "That bench changed on the host's game.";
                return Carried(c.Pc);
            }
            case TransferAction.Store: return Carried(c.Pc);
            case TransferAction.Retrieve: return Objects.FindStored(c.Pc) == null ? "That PC is not in the host's storage." : HandsFree();
            default: return null;
        }
    }

    public void Moved(in MutationContext ctx, TransferCommand c, IReadOnlyList<PcMove> moves)
    {
        var what = string.Join(", ", moves.Select(m => $"{m.Pc} {m.From.Place}→{m.To.Place}"));
        try
        {
            if (ctx.FromLocalPlayer && c.Action != TransferAction.Recover) ProjectHere(c, moves, LocalRequest(ctx));
            else foreach (var m in moves) ProjectRemote(m);
            Plugin.Logger.LogInfo($"[objects] {c.Action} by {ctx.Op}: {what}" + (ctx.FromLocalPlayer && c.Action != TransferAction.Recover ? " (here)." : " (projected)."));
        }
        catch (Exception e)
        {
            _coop.ProjectionFailed($"A PC move ({c.Action} {what}) was accepted, but this game could not show it", e);
        }
    }

    /// <summary>The requester: the same native call its gesture would have made.</summary>
    private void ProjectHere(TransferCommand c, IReadOnlyList<PcMove> moves, Request request)
    {
        var wc = Workshop;
        switch (c.Action)
        {
            case TransferAction.Pickup:
            {
                var bench = Objects.BenchAt(moves[0].From.Key) ?? throw new InvalidDataException("bench missing");
                NativeProjection.Run(() => bench.OnPickupPC());
                if (Objects.IdOf(wc.GetCarriedCase()) != c.Pc) throw new InvalidDataException("the native pickup did not take the PC");
                break;
            }
            case TransferAction.Drop:
            {
                var bench = Objects.BenchAt(moves[^1].To.Key) ?? throw new InvalidDataException("bench missing");
                NativeProjection.Run(() => wc.DropCase(bench));
                if (Objects.IdOf(bench.GetCaseInSlot()) != c.Pc) throw new InvalidDataException("the native drop did not place the PC");
                // A gesture's DropPC also does this after we return; a guest's accepted drop or a pinned request does not.
                NativeObjectState.ShowBench(bench);
                break;
            }
            case TransferAction.Store:
                NativeProjection.Run(() => wc.StoreCarriedCase());
                if (wc.GetCarriedCase() != null || Objects.FindStored(c.Pc) == null) throw new InvalidDataException("the native store did not store the PC");
                break;
            case TransferAction.Retrieve:
            {
                var record = Objects.FindStored(c.Pc) ?? throw new InvalidDataException("stored record missing");
                Objects.Retrieving(c.Pc, record);
                var ui = request?.Storage;
                if (ui != null && ui && ui.m_computer?.Pointer == record.Pointer) NativeProjection.Run(() => ui.PickUp());
                else NativeProjection.Run(() => Saves.PickupComputerFromStorage(record, true));
                if (!wc.HasOrLoadingCarriedCase()) throw new InvalidDataException("the native retrieval did not start");
                break;
            }
        }
    }

    /// <summary>Another actor's move, or a departure recovery: change this game's world without its local carry/UI.</summary>
    private void ProjectRemote(PcMove m)
    {
        var pc = Objects.Model.Pcs.Single(x => x.Id == m.Pc);
        switch ((m.From.Place, m.To.Place))
        {
            case (PcPlace.Bench, PcPlace.Carrier):
                Objects.RemotePickup(pc, Objects.BenchAt(m.From.Key) ?? throw new InvalidDataException("bench missing"), m.To.Actor); break;
            case (PcPlace.Storage, PcPlace.Carrier): Objects.RemoteRetrieve(pc, m.To.Actor); break;
            case (PcPlace.Carrier, PcPlace.Bench):
                Objects.RemoteDrop(pc, Objects.BenchAt(m.To.Key) ?? throw new InvalidDataException("bench missing")); break;
            case (PcPlace.Carrier, PcPlace.Storage): Objects.RemoteStore(pc); break;
            default: throw new InvalidDataException($"unsupported move {m.From.Place}→{m.To.Place}");
        }
    }

    // ---------------- interception ----------------

    private static NativePcTransfers Current => CoopController.Instance?.Transfers;
    private static bool Intercepting => !NativeProjection.Active && NativePcLifecycle.InCoopWorkshop;
    private static void NotReady(string what) => CoopController.Instance?.Notice($"{what} refused: the shared world isn't ready on this game yet.");

    /// <summary>Bench pickups (the ordinary pick up / pick up case actions).</summary>
    [HarmonyPatch(typeof(BenchSlot), nameof(BenchSlot.OnPickupPC))]
    internal static class BenchPickupIntercept
    {
        private static bool Prefix(BenchSlot __instance)
        {
            if (!Intercepting) return true;
            try
            {
                if (__instance.GetCaseInSlot() == null) return true;
                if (Current == null) NotReady("Pickup"); else Current.RequestPickup(__instance);
            }
            catch (Exception e) { Plugin.Logger.LogError("[objects] pickup request failed: " + e); }
            return false;
        }
    }

    /// <summary>The modding/delid bench pick up actions call PickUpCase and clear their slot themselves.</summary>
    [HarmonyPatch]
    internal static class ToolPickupIntercept
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(BenchToolSlot.__c__DisplayClass37_0), nameof(BenchToolSlot.__c__DisplayClass37_0._PopulateToolActions_b__4));
            yield return AccessTools.Method(typeof(BenchToolSlot.__c__DisplayClass37_0), nameof(BenchToolSlot.__c__DisplayClass37_0._PopulateToolActions_b__5));
        }
        private static bool Prefix(BenchToolSlot.__c__DisplayClass37_0 __instance)
        {
            if (!Intercepting) return true;
            try
            {
                var bench = __instance.__4__this?.Slot;
                if (bench == null || bench.GetCaseInSlot() == null) return true;
                if (Current == null) NotReady("Pickup"); else Current.RequestPickup(bench);
            }
            catch (Exception e) { Plugin.Logger.LogError("[objects] tool bench pickup request failed: " + e); }
            return false;
        }
    }

    /// <summary>Every drop of the carried case onto a bench, including the occupied-bench swap.</summary>
    [HarmonyPatch(typeof(WorkshopController), nameof(WorkshopController.DropCase))]
    internal static class DropIntercept
    {
        private static bool Prefix(WorkshopController __instance, BenchSlot slot)
        {
            if (!Intercepting) return true;
            try
            {
                if (slot == null || __instance.GetCarriedCase() == null) return true;
                if (Current == null) NotReady("Drop"); else Current.RequestDrop(slot);
            }
            catch (Exception e) { Plugin.Logger.LogError("[objects] drop request failed: " + e); }
            return false;
        }
    }

    [HarmonyPatch(typeof(WorkshopController), nameof(WorkshopController.StoreCarriedCase))]
    internal static class StoreIntercept
    {
        private static bool Prefix(WorkshopController __instance)
        {
            if (!Intercepting) return true;
            try
            {
                // A guest's accepted store can arrive while the old cupboard interaction is still cached. That
                // callback may run again with empty hands; native StoreCarriedCase dereferences the missing case.
                if (__instance.GetCarriedCase() == null) return false;
                if (Current == null) NotReady("Store"); else Current.RequestStore();
            }
            catch (Exception e) { Plugin.Logger.LogError("[objects] store request failed: " + e); }
            return false;
        }
    }

    /// <summary>The storage "pick up" button. With full hands the native call only refuses, so it runs as is.</summary>
    [HarmonyPatch(typeof(StoredPC), nameof(StoredPC.PickUp))]
    internal static class RetrieveIntercept
    {
        private static bool Prefix(StoredPC __instance)
        {
            if (!Intercepting) return true;
            try
            {
                if (__instance.m_computer == null || !Workshop.CarryingEmpty()) return true;
                if (Current == null) NotReady("Retrieve"); else Current.RequestRetrieve(__instance);
            }
            catch (Exception e) { Plugin.Logger.LogError("[objects] retrieve request failed: " + e); }
            return false;
        }
    }
}

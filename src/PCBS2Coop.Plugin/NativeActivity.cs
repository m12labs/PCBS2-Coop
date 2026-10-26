using PCBS2Coop.Network;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>Reads local native state only. Remote activity never enters native singleton fields.</summary>
internal sealed class NativeActivity
{
    private readonly CoopController _coop;
    private OperationId? _pending;
    private float _retryAt;
    private bool _submitting;
    public NativeActivity(CoopController coop, WorldSync world)
    {
        _coop = coop;
        world.Accepted += (OperationId op, ulong revision) => { if (_pending == op) _pending = null; };
        world.Rejected += (OperationId op, RejectReason reason, string text) =>
        { if (_pending == op || (_submitting && op.Actor == world.Local)) { _pending = null; _retryAt = Time.realtimeSinceStartup + .5f; } };
        world.GenerationStarted += (uint generation, string reason) => { _pending = null; _retryAt = 0; };
    }
    public void Update()
    {
        var world = _coop.Session.World;
        if (!_coop.WorldReady || world is not { Admitted: true } || !_coop.Objects.Initialized || world.Divergence != null) return;
        var actor = _coop.Activity.Get(world.Local);
        if (actor == null || _pending != null || Time.realtimeSinceStartup < _retryAt) return;
        var desired = Observe();
        byte[] request;
        if (actor.Pending is { Kind: ActivityOperation.Edit } pending && desired != actor.WorkingPc)
            request = ActivityWorld.Release(actor, pending.Token);
        else
        {
            if (desired == actor.WorkingPc || actor.Pending != null) return;
            request = ActivityWorld.Work(actor, desired);
        }
        // Host Submit may resolve synchronously; only retain a pending token for guests.
        _submitting = true;
        try { if (world.Submit(CommandTypes.ActorActivity, request, out var op) && !world.IsAuthority) _pending = op; }
        finally { _submitting = false; }
    }
    private NetObjectId Observe()
    {
        var ctrl = GameController.Get(); var current = ctrl?.GetCurrentState();
        if (current == null || current.TryCast<WalkingState>() != null) return default;
        var pc = FromState(current);
        if (!pc.IsNone) return pc;
        // Inventory/UI states can cover a work state. Retain activity until the work stack actually exits.
        if (ctrl.m_previousState != null)
            foreach (var state in ctrl.m_previousState)
            { pc = FromState(state); if (!pc.IsNone) return pc; }
        return default;
    }
    private NetObjectId FromState(State state)
    {
        var work = state?.TryCast<WorkingOnComputerStateBase>();
        if (work != null) return _coop.Objects.IdOf(work.Case);
        var monitor = state?.TryCast<LookingAtMonitorState>();
        return monitor != null ? _coop.Objects.IdOf(monitor.m_case) : default;
    }
    internal static bool Allow(params NetObjectId[] pcs)
    {
        if (NativePcLifecycle.Projecting) return true; // an accepted change is running its native steps
        var coop = CoopController.Instance;
        if (coop?.Session.World is not { Admitted: true } || !coop.Objects.Initialized || CoopRunner.Instance?.Scene != CoopRunner.WorkshopScene) return true;
        foreach (var pc in pcs)
        {
            if (pc.IsNone) continue; // Unbound native objects get IDs at the next host capture.
            var reason = coop.Activity.MovementBlock(pc);
            if (reason == null) continue;
            Plugin.Logger.LogWarning($"[activity] movement of {pc} refused: {reason}");
            return false;
        }
        return true;
    }
    internal static NetObjectId Carried()
    {
        var workshop = Object.FindObjectOfType<WorkshopController>();
        return CoopController.Instance?.Objects.IdOf(workshop?.GetCarriedCase()) ?? default;
    }
}

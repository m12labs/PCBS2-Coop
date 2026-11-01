using PCBS2Coop.Network;

internal sealed class FakeMonitorInputNative : IMonitorInputNative
{
    public readonly List<(OperationId Op, MonitorInputAction Action, ActorSlot Actor)> Applied = new();
    public string? CheckHost(in CommandContext ctx, PcFact? pc, MonitorInput input, MonitorPress? press) => null;
    public void Changed(in MutationContext ctx, PcFact? pc, MonitorInput input, MonitorPress? press) =>
        Applied.Add((ctx.Op, input.Action, ctx.Op.Actor));
}

internal static class MonitorInputTests
{
    public static void Run(Checker t)
    {
        t.Section("Monitor input: independent actor presses in host order");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hn = new FakeMonitorInputNative(); var gn = new FakeMonitorInputNative();
        var hw = new MonitorInputWorld(ho, ha, hn); var gw = new MonitorInputWorld(go, ga, gn);
        l.Host.RegisterState(ho); l.Guest.RegisterState(go);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.Register(CommandTypes.MonitorInput, hw); l.Guest.Register(CommandTypes.MonitorInput, gw);
        l.Host.BeginGeneration("desktop input");
        var host = PlayerId.New(); var guest = PlayerId.New(); var pcId = new NetObjectId(2);
        ho.Replace(Guid.NewGuid(), 4, new[] { new LocationFact(new NetObjectId(1), "A") },
            new[] { new PcFact(pcId, new InstanceProvenance(InstanceOrigin.Baseline, host, default), PcLocation.Bench("A"),
                new[] { new PartFact(new NetObjectId(3), "case", "CasePartInstance") },
                new[] { new PartLink("caseID", new NetObjectId(3)) }) }, Array.Empty<NetObjectId>());
        OperationId Send(WorldSync world, ushort type, byte[] payload)
        { world.Submit(type, payload, out var op); l.Flush(); return op; }
        Send(l.Host, CommandTypes.ActorActivity, ActivityWorld.Presence(ActorSlot.Host, host));
        Send(l.Host, CommandTypes.ActorActivity, ActivityWorld.Presence(ActorSlot.Guest, guest));
        l.Join();

        var guestEpoch = ga.Get(ActorSlot.Guest)!.Epoch;
        var hostEpoch = ha.Get(ActorSlot.Host)!.Epoch;
        var downGuest = new MonitorInput(guestEpoch, pcId, MonitorInputAction.Down, MonitorButton.Left, 0, .2f, .3f);
        var before = l.Host.Revision;
        Send(l.Guest, CommandTypes.MonitorInput, downGuest.ToBytes());
        t.Check(l.Host.Revision == before && l.GuestRejected.Last().Text.Contains("not using"),
            "a player outside that PC monitor cannot press it");

        Send(l.Host, CommandTypes.ActorActivity, ActivityWorld.Work(ha.Get(ActorSlot.Host)!, pcId));
        Send(l.Guest, CommandTypes.ActorActivity, ActivityWorld.Work(ga.Get(ActorSlot.Guest)!, pcId));
        var guestDownOp = Send(l.Guest, CommandTypes.MonitorInput, downGuest.ToBytes());
        var hostDownOp = Send(l.Host, CommandTypes.MonitorInput,
            new MonitorInput(hostEpoch, pcId, MonitorInputAction.Down, MonitorButton.Left, 0, .8f, .7f).ToBytes());
        t.Check(hw.PressOf(ActorSlot.Guest)?.Sequence == guestDownOp.Sequence &&
            hw.PressOf(ActorSlot.Host)?.Sequence == hostDownOp.Sequence &&
            gw.PressOf(ActorSlot.Guest)?.Sequence == guestDownOp.Sequence &&
            gw.PressOf(ActorSlot.Host)?.Sequence == hostDownOp.Sequence && l.InStep,
            "both actors can hold a button on the same PC independently");

        Send(l.Guest, CommandTypes.MonitorInput,
            new MonitorInput(guestEpoch, pcId, MonitorInputAction.Drag, MonitorButton.Left, guestDownOp.Sequence, .4f, .5f).ToBytes());
        t.Check(hw.PressOf(ActorSlot.Guest)?.X == .4f && hw.PressOf(ActorSlot.Host)?.X == .8f && l.InStep,
            "guest drag updates only its own captured pointer");

        before = l.Host.Revision;
        Send(l.Guest, CommandTypes.MonitorInput,
            new MonitorInput(guestEpoch, pcId, MonitorInputAction.Up, MonitorButton.Left, guestDownOp.Sequence + 1, .4f, .5f).ToBytes());
        t.Check(l.Host.Revision == before && hw.PressOf(ActorSlot.Guest) != null,
            "a stale mouse-up cannot release a newer press");
        Send(l.Guest, CommandTypes.MonitorInput,
            new MonitorInput(guestEpoch, pcId, MonitorInputAction.Up, MonitorButton.Left, guestDownOp.Sequence, .4f, .5f).ToBytes());
        t.Check(hw.PressOf(ActorSlot.Guest) == null && hw.PressOf(ActorSlot.Host) != null && l.InStep,
            "guest mouse-up releases only the guest capture");
        Send(l.Host, CommandTypes.ActorActivity, ActivityWorld.Work(ha.Get(ActorSlot.Host)!, default));
        ho.Replace(ho.WorldId, ho.NextId, ho.Locations,
            ho.Pcs.Select(p => p.Id == pcId ? p.At(PcLocation.Storage) : p), ho.Retired);
        go.Replace(go.WorldId, go.NextId, go.Locations,
            go.Pcs.Select(p => p.Id == pcId ? p.At(PcLocation.Storage) : p), go.Retired);
        Send(l.Host, CommandTypes.MonitorInput,
            new MonitorInput(hostEpoch, pcId, MonitorInputAction.Cancel, MonitorButton.Left, hostDownOp.Sequence, .8f, .7f).ToBytes());
        t.Check(hw.PressOf(ActorSlot.Host) == null && gw.PressOf(ActorSlot.Host) == null &&
            hn.Applied.Count == gn.Applied.Count && l.InStep, "cancel after the PC moves releases the host capture on both peers");

        t.Check(!new MonitorInput(guestEpoch, pcId, MonitorInputAction.Down, MonitorButton.Left, 0, float.NaN, 0).IsValid &&
            !new MonitorInput(guestEpoch, pcId, MonitorInputAction.Down, MonitorButton.Left, 1, 0, 0).IsValid &&
            !new MonitorInput(guestEpoch, pcId, MonitorInputAction.Up, MonitorButton.Left, 0, 0, 0).IsValid,
            "invalid coordinates and press tokens cannot be encoded");
    }
}

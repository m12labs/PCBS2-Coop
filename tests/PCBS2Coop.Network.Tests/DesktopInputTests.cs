using LiteNetLib.Utils;
using PCBS2Coop.Network;

internal sealed class FakeDesktopInputNative : IDesktopInputNative
{
    public readonly List<(OperationId Op, ActorSlot Actor, DesktopInput Input, bool HasPc)> Applied = new();
    public void Changed(in MutationContext ctx, PcFact? pc, DesktopInput input) =>
        Applied.Add((ctx.Op, ctx.Op.Actor, input, pc != null));
}

internal static class DesktopInputTests
{
    public static void Run(Checker t)
    {
        t.Section("Desktop input: mirrored clicks, drags and typing in host order");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hn = new FakeDesktopInputNative(); var gn = new FakeDesktopInputNative();
        var hw = new DesktopInputWorld(ho, ha, hn); var gw = new DesktopInputWorld(go, ga, gn);
        l.Host.RegisterState(ho); l.Guest.RegisterState(go);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.Register(CommandTypes.DesktopInput, hw); l.Guest.Register(CommandTypes.DesktopInput, gw);
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

        var click = new DesktopInput(guestEpoch, pcId, new[]
        {
            new DesktopEvent(DesktopEventKind.Move, DesktopSurface.Desktop, 1f, .2f, .3f),
            new DesktopEvent(DesktopEventKind.Down, DesktopSurface.Desktop, 1.1f, .2f, .3f, MonitorButton.Left),
            new DesktopEvent(DesktopEventKind.Up, DesktopSurface.Desktop, 1.2f, .2f, .3f, MonitorButton.Left),
        });
        var guestOp = Send(l.Guest, CommandTypes.DesktopInput, click.ToBytes());
        var hostOp = Send(l.Host, CommandTypes.DesktopInput, new DesktopInput(hostEpoch, pcId, new[]
        {
            new DesktopEvent(DesktopEventKind.Down, DesktopSurface.Desktop, 5f, .8f, .7f, MonitorButton.Left),
        }).ToBytes());
        t.Check(hn.Applied.Count == 2 && gn.Applied.Count == 2 && l.InStep &&
            hn.Applied[0].Op == guestOp && hn.Applied[1].Op == hostOp && gn.Applied[0].Op == guestOp &&
            gn.Applied[0].Input.Events.Count == 3 && gn.Applied[0].Input.Events[1].Button == MonitorButton.Left,
            "both players' input reaches both games in host order, without a desktop lock");

        var typed = new DesktopInput(hostEpoch, pcId, new[]
        {
            new DesktopEvent(DesktopEventKind.Text, DesktopSurface.Desktop, 6f, .5f, .5f, Path: "0/3/1", Text: "hello wörld"),
            new DesktopEvent(DesktopEventKind.Submit, DesktopSurface.Desktop, 6.1f, .5f, .5f, Path: "0/3/1"),
            new DesktopEvent(DesktopEventKind.Scroll, DesktopSurface.Desktop, 6.2f, .5f, .5f, ScrollY: -1),
        });
        Send(l.Host, CommandTypes.DesktopInput, typed.ToBytes());
        var got = gn.Applied.Last().Input.Events;
        t.Check(got[0].Text == "hello wörld" && got[0].Path == "0/3/1" && got[1].Kind == DesktopEventKind.Submit &&
            got[2].ScrollY == -1, "typing, Enter and the scroll wheel round-trip");

        var before = l.Host.Revision;
        Send(l.Guest, CommandTypes.DesktopInput, new DesktopInput(guestEpoch + 1, pcId, click.Events).ToBytes());
        t.Check(l.Host.Revision == before && l.GuestRejected.Last().Text.Contains("presence"),
            "input from a stale actor epoch is refused");

        var windows = new DesktopInput(hostEpoch, pcId, new[]
        { new DesktopEvent(DesktopEventKind.Windows, DesktopSurface.Desktop, 10f, 0, 0, Text: "ADDPROGRAM\nOCCT") });
        Send(l.Host, CommandTypes.DesktopInput, windows.ToBytes());
        t.Check(gn.Applied.Last().Input.Events[0].Text == "ADDPROGRAM\nOCCT" && l.InStep, "the host's open windows reach the guest");
        before = l.Host.Revision;
        Send(l.Guest, CommandTypes.DesktopInput, new DesktopInput(guestEpoch, pcId, windows.Events).ToBytes());
        t.Check(l.Host.Revision == before && l.GuestRejected.Last().Text.Contains("Only the host"),
            "a guest cannot publish the open windows");

        ho.Replace(ho.WorldId, ho.NextId, ho.Locations, ho.Pcs.Select(p => p.Id == pcId ? p.At(PcLocation.Storage) : p), ho.Retired);
        go.Replace(go.WorldId, go.NextId, go.Locations, go.Pcs.Select(p => p.Id == pcId ? p.At(PcLocation.Storage) : p), go.Retired);
        before = l.Host.Revision;
        Send(l.Guest, CommandTypes.DesktopInput, click.ToBytes());
        t.Check(l.Host.Revision == before && l.GuestRejected.Last().Text.Contains("bench"),
            "a click on a PC that left its bench is refused");
        Send(l.Guest, CommandTypes.DesktopInput, new DesktopInput(guestEpoch, pcId, new[]
        {
            new DesktopEvent(DesktopEventKind.Up, DesktopSurface.Desktop, 9f, .2f, .3f, MonitorButton.Left),
            new DesktopEvent(DesktopEventKind.Leave, DesktopSurface.Desktop, 9f, 0, 0),
        }).ToBytes());
        t.Check(gn.Applied.Last().Input.Events[1].Kind == DesktopEventKind.Leave && l.InStep,
            "a release still reaches the other game after the PC moved");

        t.Check(!new DesktopEvent(DesktopEventKind.Move, DesktopSurface.Desktop, 0, float.NaN, 0).IsValid &&
            !new DesktopEvent(DesktopEventKind.Down, DesktopSurface.Desktop, 0, 0, 0).IsValid &&
            !new DesktopEvent(DesktopEventKind.Move, DesktopSurface.Desktop, 0, 1.5f, 0).IsValid &&
            !new DesktopEvent(DesktopEventKind.Text, DesktopSurface.Desktop, 0, 0, 0).IsValid &&
            !new DesktopEvent(DesktopEventKind.Text, DesktopSurface.Desktop, 0, 0, 0, Path: "1", Text: new string('x', 4001)).IsValid &&
            !new DesktopInput(guestEpoch, pcId, Array.Empty<DesktopEvent>()).IsValid,
            "invalid coordinates, buttons, text and empty batches cannot be encoded");

        var big = new DesktopInput(hostEpoch, pcId, Enumerable.Range(0, DesktopInput.MaxEvents)
            .Select(i => new DesktopEvent(DesktopEventKind.Move, DesktopSurface.Bios, i, .1f, .1f)).ToArray());
        var decoded = DesktopInput.Read(new NetDataReader(big.ToBytes()));
        t.Check(decoded.Events.Count == DesktopInput.MaxEvents && decoded.Events[63].Surface == DesktopSurface.Bios,
            "a full batch fits in one command");
        var bad = big.ToBytes(); bad[^1] ^= 0xFF; // corrupt the last event's button byte
        var refused = false;
        try { DesktopInput.Read(new NetDataReader(bad)); } catch (FormatException) { refused = true; }
        t.Check(refused, "a corrupted batch is refused when read");
    }
}

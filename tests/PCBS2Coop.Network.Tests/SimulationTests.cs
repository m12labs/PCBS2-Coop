using LiteNetLib.Utils;
using PCBS2Coop.Network;

internal sealed class FakeSimulationNative : ISimulationNative
{
    public readonly List<(NetObjectId Pc, int Reason, bool Local)> Failures = new();
    public readonly List<(NetObjectId Pc, IReadOnlyList<FanCurve> Fans, bool Local)> Fans = new();
    public void Failed(in MutationContext ctx, PcFact pc, int reason) => Failures.Add((pc.Id, reason, ctx.FromLocalPlayer));
    public void FansChanged(in MutationContext ctx, PcFact pc, IReadOnlyList<FanCurve> fans) => Fans.Add((pc.Id, fans, ctx.FromLocalPlayer));
}

internal static class SimulationTests
{
    private static NetObjectId Id(ulong n) => new(n);

    public static void Run(Checker t)
    {
        t.Section("Simulation: the host's PC failures and shared fan curves (phase 7)");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hn = new FakeSimulationNative(); var gn = new FakeSimulationNative();
        l.Host.RegisterState(ho); l.Guest.RegisterState(go);
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.Register(CommandTypes.Simulation, new SimulationWorld(ho, ha, hn));
        l.Guest.Register(CommandTypes.Simulation, new SimulationWorld(go, ga, gn));
        l.Host.BeginGeneration("simulation");
        var host = PlayerId.New(); var guest = PlayerId.New();
        var origin = new InstanceProvenance(InstanceOrigin.Baseline, host, default);
        var pc = new PcFact(Id(2), origin, PcLocation.Bench("A"),
            new[] { new PartFact(Id(3), "case", "CasePartInstance"), new PartFact(Id(4), "fan", "PartInstance"),
                new PartFact(Id(5), "cooler", "PartInstance") },
            new[] { new PartLink("caseID", Id(3)), new PartLink("caseFans[0]", Id(4)), new PartLink("cpuCooler", Id(5)) });
        var stored = new PcFact(Id(6), origin, PcLocation.Storage,
            new[] { new PartFact(Id(7), "case", "CasePartInstance") }, new[] { new PartLink("caseID", Id(7)) });
        ho.Replace(Guid.NewGuid(), 8, new[] { new LocationFact(Id(1), "A") }, new[] { pc, stored }, Array.Empty<NetObjectId>());
        void Send(WorldSync world, byte[] payload, ushort type = CommandTypes.Simulation) { world.Submit(type, payload, out _); l.Flush(); }
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, host), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, guest), CommandTypes.ActorActivity); l.Join();
        ulong HostEpoch() => ha.Get(ActorSlot.Host)!.Epoch;
        ulong GuestEpoch() => ga.Get(ActorSlot.Guest)!.Epoch;

        // Failures: only the host's game decides.
        Send(l.Host, SimulationWorld.Failure(HostEpoch(), pc.Id, 2));
        t.Check(gn.Failures.SequenceEqual(new[] { (pc.Id, 2, false) }) && hn.Failures.SequenceEqual(new[] { (pc.Id, 2, true) }) && l.InStep,
            "the host's PC failure reaches the guest once, with its reason");
        var before = l.Host.Revision;
        Send(l.Guest, SimulationWorld.Failure(GuestEpoch(), pc.Id, 3));
        t.Check(l.Host.Revision == before && l.GuestRejected.Last().Text.Contains("Only the host"), "a guest's own failure is refused");
        Send(l.Host, SimulationWorld.Failure(HostEpoch(), stored.Id, 2));
        t.Check(l.Host.Revision == before && l.HostRejected.Last().Text.Contains("bench"), "a failure needs a benched PC");
        var threw = false;
        try { SimulationWorld.Failure(HostEpoch(), pc.Id, 0); } catch (ArgumentOutOfRangeException) { threw = true; }
        t.Check(threw, "NO_SHUTDOWN is not a failure");

        // Fan curves: any player at that PC's monitor; the host any time (late join).
        var curve = new FanCurve("caseFans[0]", 0, true, 3, 0, new[] { (20f, 0.2f), (60f, 0.5f), (90f, 1f) });
        var cooler = new FanCurve("cpuCooler", 1, false, 1, 0, null);
        before = l.Host.Revision;
        Send(l.Guest, SimulationWorld.Fans(GuestEpoch(), pc.Id, new[] { curve }));
        t.Check(l.Host.Revision == before && l.GuestRejected.Last().Text.Contains("monitor"),
            "a guest away from the PC's monitor can't change its fan curves");
        Send(l.Guest, ActivityWorld.Work(ga.Get(ActorSlot.Guest)!, pc.Id), CommandTypes.ActorActivity);
        Send(l.Guest, SimulationWorld.Fans(GuestEpoch(), pc.Id, new[] { curve, cooler }));
        t.Check(hn.Fans.Count == 1 && !hn.Fans[0].Local && hn.Fans[0].Fans.Count == 2 && hn.Fans[0].Fans[0].SameAs(curve) &&
            hn.Fans[0].Fans[1].SameAs(cooler) && gn.Fans.Count == 1 && gn.Fans[0].Local && l.InStep,
            "a guest's applied fan curves reach the host intact");
        Send(l.Host, SimulationWorld.Fans(HostEpoch(), pc.Id, new[] { cooler }));
        t.Check(gn.Fans.Count == 2 && !gn.Fans[1].Local, "the host can refresh a PC's curves without being at its monitor");
        before = l.Host.Revision;
        Send(l.Host, SimulationWorld.Fans(HostEpoch(), pc.Id, new[] { curve with { Path = "gpuSlots[0]" } }));
        t.Check(l.Host.Revision == before && l.HostRejected.Last().Text.Contains("Unknown fan part"), "a fan on a part that isn't in the PC is refused");

        // Codec limits.
        threw = false;
        try { SimulationWorld.Fans(1, pc.Id, new[] { curve with { Points = new[] { (float.NaN, 1f) } } }); } catch (ArgumentException) { threw = true; }
        t.Check(threw, "non-finite curve points are refused before sending");
        var w = new NetDataWriter(); w.Put((byte)2); w.Put(1UL); pc.Id.Write(w); w.Put((byte)2); curve.Write(w); curve.Write(w);
        threw = false;
        try { new SimulationWorld(go, ga, gn).Validate(new CommandContext(new OperationId(ActorSlot.Host, 99), 0, 0), new NetDataReader(w.CopyData())); }
        catch (FormatException) { threw = true; }
        t.Check(threw, "the same fan twice in one change is refused");
    }
}

using PCBS2Coop.Network;

internal sealed class FakeBiosNative : IBiosNative
{
    public BiosSettings? Current;
    public readonly List<(OperationId Op, bool Local, bool Reset)> Applied = new();
    public FakeBiosNative(BiosSettings initial) => Current = initial;
    public string? CheckHost(in CommandContext ctx, PcFact pc, BiosChange change) =>
        BiosWorld.Baseline(Current) == change.ExpectedDigest ? null : "The BIOS configuration changed before this request arrived.";
    public void Changed(in MutationContext ctx, PcFact pc, BiosChange change)
    {
        if (BiosWorld.Baseline(Current) != change.ExpectedDigest) throw new InvalidDataException("BIOS baseline diverged.");
        Current = change.Settings;
        Applied.Add((ctx.Op, ctx.FromLocalPlayer, change.Reset));
    }
}

internal static class BiosTests
{
    private static NetObjectId Id(ulong value) => new(value);
    internal static readonly BiosSettings Default = new(100, 40, 1.2f, 2400, 1.5f, new[] { 13, 15, 15, 30 },
        false, 1800, 1219, 100, 0, 0, 0, "m2Slots[0]", false);

    public static void Run(Checker t)
    {
        t.Section("BIOS: one host-ordered PC config and factory reset");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hn = new FakeBiosNative(Default); var gn = new FakeBiosNative(Default);
        l.Host.RegisterState(ho); l.Guest.RegisterState(go);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.Register(CommandTypes.Bios, new BiosWorld(ho, ha, hn));
        l.Guest.Register(CommandTypes.Bios, new BiosWorld(go, ga, gn));
        l.Host.BeginGeneration("bios");
        var host = PlayerId.New(); var guest = PlayerId.New();
        var origin = new InstanceProvenance(InstanceOrigin.Baseline, host, default);
        var pc = new PcFact(Id(2), origin, PcLocation.Bench("A"),
            new[] { new PartFact(Id(3), "case", "CasePartInstance"), new PartFact(Id(4), "drive", "StoragePartInstance") },
            new[] { new PartLink("caseID", Id(3)), new PartLink("m2SSDs[0]", Id(4)) });
        ho.Replace(Guid.NewGuid(), 5, new[] { new LocationFact(Id(1), "A") }, new[] { pc }, Array.Empty<NetObjectId>());
        void Send(WorldSync world, ushort type, byte[] payload) { world.Submit(type, payload, out _); l.Flush(); }
        Send(l.Host, CommandTypes.ActorActivity, ActivityWorld.Presence(ActorSlot.Host, host));
        Send(l.Host, CommandTypes.ActorActivity, ActivityWorld.Presence(ActorSlot.Guest, guest));
        l.Join();
        var before = l.Host.Revision;
        Send(l.Guest, CommandTypes.Bios, BiosWorld.Request(ga.Get(ActorSlot.Guest)!.Epoch, pc.Id, Default, Default with { CpuMultiplier = 41 }));
        t.Check(l.Host.Revision == before && l.GuestRejected.Last().Text.Contains("not using"),
            "a guest outside the PC's monitor cannot change BIOS");

        Send(l.Host, CommandTypes.ActorActivity, ActivityWorld.Work(ha.Get(ActorSlot.Host)!, pc.Id));
        Send(l.Guest, CommandTypes.ActorActivity, ActivityWorld.Work(ga.Get(ActorSlot.Guest)!, pc.Id));
        var edited = Default with { CpuMultiplier = 41 };
        before = l.Host.Revision;
        Send(l.Guest, CommandTypes.Bios, BiosWorld.Request(ga.Get(ActorSlot.Guest)!.Epoch, pc.Id, Default, edited));
        t.Check(l.Host.Revision == before + 1 && hn.Current?.Digest() == edited.Digest() && gn.Current?.Digest() == edited.Digest() &&
            hn.Applied.Last().Local == false && gn.Applied.Last().Local && l.InStep && l.Divergences.Count == 0,
            "guest BIOS apply reaches both PCs once in host order");

        before = l.Host.Revision;
        Send(l.Host, CommandTypes.Bios, BiosWorld.Request(ha.Get(ActorSlot.Host)!.Epoch, pc.Id, Default, Default with { CpuMultiplier = 42 }));
        t.Check(l.Host.Revision == before && l.HostRejected.Last().Text.Contains("changed before"),
            "a stale BIOS draft is refused after another player's edit");

        before = l.Host.Revision;
        Send(l.Host, CommandTypes.Bios, BiosWorld.Request(ha.Get(ActorSlot.Host)!.Epoch, pc.Id, edited, null));
        t.Check(l.Host.Revision == before + 1 && hn.Current == null && gn.Current == null &&
            hn.Applied.Last().Reset && gn.Applied.Last().Reset && l.InStep,
            "factory reset clears both native BIOS configurations");

        before = l.Host.Revision;
        Send(l.Guest, CommandTypes.Bios, BiosWorld.Request(ga.Get(ActorSlot.Guest)!.Epoch, pc.Id, null, Default));
        t.Check(l.Host.Revision == before + 1 && hn.Current?.Digest() == Default.Digest() &&
            gn.Current?.Digest() == Default.Digest() && l.InStep,
            "a BIOS edit after factory reset applies on both peers");

        var malformed = Default with { CpuMultiplier = float.NaN };
        t.Check(!malformed.IsValid && !new BiosSettings(100, 40, 1.2f, 2400, 1.5f, Array.Empty<int>(), false,
            1800, 1219, 100, 0, 0, 0, "", false).IsValid,
            "non-finite BIOS values and invalid timing arrays are refused before encoding");
    }
}

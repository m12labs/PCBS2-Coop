using System.Security.Cryptography;
using PCBS2Coop.Network;

internal sealed class FakePcRepairNative : IPcRepairNative
{
    public readonly List<NetObjectId> Requests = new();
    public readonly List<(byte[] Record, bool Powered)> Records = new();
    public void Requested(in MutationContext ctx, PcFact pc) => Requests.Add(pc.Id);
    public void Arrived(in MutationContext ctx, PcFact pc, byte[] record, bool powered) => Records.Add((record.ToArray(), powered));
}

internal static class PcRepairTests
{
    private static NetObjectId Id(ulong n) => new(n);
    public static void Run(Checker t)
    {
        t.Section("PC repair: a guest reloads a PC from the host's record");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hn = new FakePcRepairNative(); var gn = new FakePcRepairNative();
        var hr = new PcRepairWorld(ho, ha, hn); var gr = new PcRepairWorld(go, ga, gn);
        l.Host.RegisterState(ho); l.Guest.RegisterState(go);
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.Register(CommandTypes.PcRepair, hr); l.Guest.Register(CommandTypes.PcRepair, gr);
        l.Host.BeginGeneration("repair");
        var host = PlayerId.New(); var guest = PlayerId.New();
        var pc = new PcFact(Id(2), new InstanceProvenance(InstanceOrigin.Baseline, host, default), PcLocation.Bench("A"),
            new[] { new PartFact(Id(3), "case", "CasePartInstance") }, new[] { new PartLink("caseID", Id(3)) });
        ho.Replace(Guid.NewGuid(), 4, new[] { new LocationFact(Id(1), "A") }, new[] { pc }, Array.Empty<NetObjectId>());
        void Send(WorldSync world, byte[] payload, ushort type = CommandTypes.PcRepair) { world.Submit(type, payload, out _); l.Flush(); }
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, host), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, guest), CommandTypes.ActorActivity); l.Join();
        var hostEpoch = ha.Get(ActorSlot.Host)!.Epoch; var guestEpoch = ga.Get(ActorSlot.Guest)!.Epoch;

        Send(l.Guest, PcRepairWorld.Request(guestEpoch, pc.Id));
        t.Check(hn.Requests.SequenceEqual(new[] { pc.Id }) && gn.Requests.Count == 1 && l.InStep, "a guest's request reaches the host");
        var before = l.Host.Revision;
        Send(l.Host, PcRepairWorld.Request(hostEpoch, pc.Id));
        t.Check(l.Host.Revision == before, "the host never requests a repair (its game is the reference)");

        var bytes = Enumerable.Range(0, PcRepairWorld.MaxChunkBytes + 99).Select(i => (byte)(i * 7)).ToArray();
        before = l.Host.Revision;
        Send(l.Guest, PcRepairWorld.Begin(guestEpoch, pc.Id, bytes.Length, SHA256.HashData(bytes), true));
        t.Check(l.Host.Revision == before && l.GuestRejected.Last().Text.Contains("Only the host"), "a guest cannot send a PC record");

        Send(l.Host, PcRepairWorld.Begin(hostEpoch, pc.Id, bytes.Length, SHA256.HashData(bytes), true));
        before = l.Host.Revision;
        Send(l.Host, PcRepairWorld.Chunk(hostEpoch, pc.Id, 1, 2, bytes[PcRepairWorld.MaxChunkBytes..]));
        t.Check(l.Host.Revision == before, "an out-of-order chunk is refused");
        Send(l.Host, PcRepairWorld.Chunk(hostEpoch, pc.Id, 0, 2, bytes[..PcRepairWorld.MaxChunkBytes]));
        Send(l.Host, PcRepairWorld.Chunk(hostEpoch, pc.Id, 1, 2, bytes[PcRepairWorld.MaxChunkBytes..]));
        Send(l.Host, PcRepairWorld.Commit(hostEpoch, pc.Id));
        t.Check(gn.Records.Count == 1 && gn.Records[0].Record.SequenceEqual(bytes) && gn.Records[0].Powered &&
            hn.Records.Count == 1 && !hr.Busy && !gr.Busy && l.InStep, "the host's record arrives whole, with its power state");

        before = l.Host.Revision;
        Send(l.Host, PcRepairWorld.Commit(hostEpoch, pc.Id));
        t.Check(l.Host.Revision == before, "a commit without a transfer is refused");
    }
}

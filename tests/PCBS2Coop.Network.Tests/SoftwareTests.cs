using System.Security.Cryptography;
using LiteNetLib.Utils;
using PCBS2Coop.Network;

internal sealed class FakeSoftwareNative : ISoftwareNative
{
    public readonly List<byte[]> Records = new();
    public string? CheckHost(in CommandContext ctx, PcFact pc, PartLink bootDrive) => null;
    public void Changed(in MutationContext ctx, PcFact pc, PartLink bootDrive, byte[] record) => Records.Add(record.ToArray());
}

internal static class SoftwareTests
{
    private static NetObjectId Id(ulong n) => new(n);
    public static void Run(Checker t)
    {
        t.Section("Software: host-published opaque boot-drive record");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hn = new FakeSoftwareNative(); var gn = new FakeSoftwareNative();
        var hs = new SoftwareWorld(ho, ha, hn, true); var gs = new SoftwareWorld(go, ga, gn, false);
        l.Host.RegisterState(ho); l.Guest.RegisterState(go);
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.RegisterState(hs); l.Guest.RegisterState(gs);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.Register(CommandTypes.Software, hs); l.Guest.Register(CommandTypes.Software, gs);
        l.Host.BeginGeneration("software");
        var host = PlayerId.New(); var guest = PlayerId.New();
        var pc = new PcFact(Id(2), new InstanceProvenance(InstanceOrigin.Baseline, host, default), PcLocation.Bench("A"),
            new[] { new PartFact(Id(3), "case", "CasePartInstance"), new PartFact(Id(4), "drive", "StoragePartInstance") },
            new[] { new PartLink("caseID", Id(3)), new PartLink("m2SSDs[0]", Id(4)) });
        ho.Replace(Guid.NewGuid(), 5, new[] { new LocationFact(Id(1), "A") }, new[] { pc }, Array.Empty<NetObjectId>());
        void Send(WorldSync world, byte[] payload, ushort type = CommandTypes.Software) { world.Submit(type, payload, out _); l.Flush(); }
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, host), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, guest), CommandTypes.ActorActivity); l.Join();
        var epoch = ha.Get(ActorSlot.Host)!.Epoch;
        var bytes = Enumerable.Range(0, SoftwareWorld.MaxChunkBytes + 17).Select(i => (byte)(i * 31)).ToArray();
        Send(l.Host, SoftwareWorld.Begin(epoch, pc.Id, "m2SSDs[0]", bytes.Length, SHA256.HashData(bytes)));
        Send(l.Host, SoftwareWorld.Chunk(epoch, pc.Id, 0, 2, bytes[..SoftwareWorld.MaxChunkBytes]));
        // A serialized mid-transfer state restores the guest's exact next expected chunk.
        var sw = new NetDataWriter(); hs.Write(sw); var copy = new SoftwareWorld(go, ga, new FakeSoftwareNative(), false);
        copy.Read(new NetDataReader(sw.CopyData()));
        t.Check(copy.Busy, "partial software transfer survives a world-state snapshot");
        var rev = l.Host.Revision;
        Send(l.Host, SoftwareWorld.Chunk(epoch, pc.Id, 0, 2, bytes[..SoftwareWorld.MaxChunkBytes]));
        t.Check(l.Host.Revision == rev && l.HostRejected.Last().Text.Contains("out-of-order"), "duplicate chunk is refused");
        Send(l.Host, SoftwareWorld.Chunk(epoch, pc.Id, 1, 2, bytes[SoftwareWorld.MaxChunkBytes..]));
        Send(l.Host, SoftwareWorld.Commit(epoch, pc.Id));
        t.Check(hn.Records.Count == 1 && gn.Records.Count == 1 && hn.Records[0].SequenceEqual(bytes) && gn.Records[0].SequenceEqual(bytes) &&
            l.Divergences.Count == 0 && !hs.Busy && !gs.Busy, "host commit publishes complete identical opaque bytes to both peers");

        var tampered = bytes.ToArray(); tampered[0] ^= 0x80;
        Send(l.Host, SoftwareWorld.Begin(epoch, pc.Id, "m2SSDs[0]", bytes.Length, SHA256.HashData(bytes)));
        Send(l.Host, SoftwareWorld.Chunk(epoch, pc.Id, 0, 2, tampered[..SoftwareWorld.MaxChunkBytes]));
        Send(l.Host, SoftwareWorld.Chunk(epoch, pc.Id, 1, 2, tampered[SoftwareWorld.MaxChunkBytes..]));
        rev = l.Host.Revision; Send(l.Host, SoftwareWorld.Commit(epoch, pc.Id));
        t.Check(l.Host.Revision == rev && l.HostRejected.Last().Text.Contains("hash") && hn.Records.Count == 1 && gn.Records.Count == 1,
            "tampered host payload fails hash check and never reaches native adapters");

        Send(l.Host, SoftwareWorld.Begin(epoch, pc.Id, "m2SSDs[0]", bytes.Length, SHA256.HashData(bytes)));
        rev = l.Host.Revision; Send(l.Host, SoftwareWorld.Chunk(epoch, pc.Id, 1, 2, bytes[SoftwareWorld.MaxChunkBytes..]));
        t.Check(l.Host.Revision == rev && l.HostRejected.Last().Text.Contains("out-of-order"), "out-of-order chunk is refused");
        var before = l.Host.Revision;
        Send(l.Guest, SoftwareWorld.Begin(ga.Get(ActorSlot.Guest)!.Epoch, pc.Id, "m2SSDs[0]", bytes.Length, SHA256.HashData(bytes)));
        t.Check(l.Host.Revision == before && l.GuestRejected.Last().Text.Contains("Only the host"), "guest cannot publish software bytes");
    }
}

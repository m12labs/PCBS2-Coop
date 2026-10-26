using LiteNetLib.Utils;
using PCBS2Coop.Network;

internal static class ObjectWorldTests
{
    private static readonly PlayerId Host = PlayerId.New(), Guest = PlayerId.New();
    private static readonly InstanceProvenance Origin = new(InstanceOrigin.Baseline, Host, default);
    public static void Run(Checker t)
    {
        t.Section("Objects: identity, aliases, locations and persistence");
        var model = Make();
        var bytes = model.ToBytes(); var copy = new ObjectWorld(); copy.Read(new(bytes));
        t.Check(bytes.SequenceEqual(copy.ToBytes()), "object facts, allocator and tombstones round-trip deterministically");
        t.Check(copy.Pcs[0].Parts[0].Sku == copy.Pcs[1].Parts[0].Sku && copy.Pcs[0].Parts[0].Id != copy.Pcs[1].Parts[0].Id,
            "identical same-SKU parts in different PCs retain distinct IDs");
        t.Check(copy.Pcs[0].Links[0].Part == copy.Pcs[0].Links[1].Part, "native aliases within a PC retain one logical part");
        var pc = model.Pcs[0]; var other = model.Pcs[1]; var next = model.NextId;
        model.Replace(model.WorldId, next, model.Locations, new[] { pc with { Location = PcLocation.Storage }, other }, model.Retired);
        model.Replace(model.WorldId, next, model.Locations, new[] { pc with { Location = PcLocation.Carried(ActorSlot.Host, Host) }, other }, model.Retired);
        model.Replace(model.WorldId, next, model.Locations, new[] { pc, other }, model.Retired);
        t.Check(bytes.SequenceEqual(model.ToBytes()), "store/retrieve/drop changes location without minting PC or part IDs");
        var alive = model.Pcs[1];
        model.Replace(model.WorldId, next, model.Locations, new[] { alive }, new[] { pc.Id, pc.Parts[0].Id, new NetObjectId(7) });
        t.Check(model.Lookup(pc.Id) == ObjectLookup.Retired && model.Lookup(new(42)) == ObjectLookup.Unknown, "deleted and unknown IDs remain distinguishable after restore");
        var allocator = new NetObjectIdAllocator { Next = model.NextId };
        t.Check(allocator.Allocate().Value == 8, "restored allocator continues after all live and retired IDs");
        t.Check(Throws(() => new NetObjectIdAllocator { Next = ulong.MaxValue }.Allocate()), "allocator exhaustion cannot wrap to zero");

        t.Check(Bad(m => m.Replace(m.WorldId, m.NextId, m.Locations, new[] { pc, other with { Id = pc.Id } }, m.Retired)), "duplicate logical PC IDs rejected");
        t.Check(Bad(m => m.Replace(m.WorldId, m.NextId, m.Locations, new[] { pc with { Links = new[] { new PartLink("caseID", new(7)) } }, other }, m.Retired)), "retired part references rejected");
        t.Check(Bad(m => m.Replace(m.WorldId, m.NextId, m.Locations, new[] { pc with { Location = PcLocation.Bench("unknown") }, other }, m.Retired)), "unknown location bindings rejected");
        t.Check(Bad(m => m.Replace(m.WorldId, m.NextId, m.Locations, new[] { pc, other with { Location = pc.Location } }, m.Retired)), "two PCs cannot occupy one bench");
        t.Check(Bad(m => m.Replace(m.WorldId, m.NextId, m.Locations, new[] { pc with { Location = PcLocation.Carried(ActorSlot.Guest, Guest) }, other with { Location = PcLocation.Carried(ActorSlot.Guest, Guest) } }, m.Retired)), "two PCs cannot occupy one actor carrier");
        t.Check(Bad(m => m.Replace(m.WorldId, 9, m.Locations, m.Pcs, m.Retired)), "allocator gaps rejected rather than silently reusing lost identities");
        t.Check(Bad(m => m.Replace(m.WorldId, 7, m.Locations, m.Pcs, m.Retired)), "allocator behind issued IDs rejected");
        t.Check(Bad(m => m.Replace(m.WorldId, m.NextId, m.Locations, new[] { pc with { Links = pc.Links.Append(pc.Links[0]).ToArray() }, other }, m.Retired)), "duplicate part paths rejected");
        t.Check(Bad(m => m.Replace(m.WorldId, m.NextId, m.Locations, new[] { pc with { Links = Array.Empty<PartLink>() }, other }, m.Retired)), "incomplete part binding map rejected");
        t.Check(Throws(() => copy.Read(new(bytes[..^1]))) && bytes.SequenceEqual(copy.ToBytes()), "truncated state fails without replacing previous facts");
        t.Check(Throws(() => copy.Read(new(bytes.Concat(new byte[] { 0 }).ToArray()))), "trailing object state bytes rejected");
        copy.CheckBindings = _ => throw new InvalidDataException("missing mesh");
        t.Check(Throws(() => copy.Read(new(bytes))) && copy.ToBytes().SequenceEqual(bytes), "incomplete native binding validation cannot publish candidate model");

        t.Section("Objects: async projection attempts");
        var bindings = new ProjectionBindings<object>(); var world = Guid.NewGuid(); bindings.Reset(world);
        var old = bindings.Begin(pc.Id); var current = bindings.Begin(pc.Id); var mesh = new object();
        t.Check(!bindings.IsPending(old) && bindings.IsPending(current), "native completion can reject an obsolete attempt before placing its mesh");
        t.Check(!bindings.Complete(old, new object()) && bindings.Complete(current, mesh) && bindings.TryGet(pc.Id, out var found) && ReferenceEquals(found, mesh), "obsolete async completion cannot bind a replacement PC projection");
        t.Check(!bindings.IsPending(current), "a completed attempt cannot authorize another native placement");
        t.Check(!bindings.Complete(current, new object()), "a projection callback cannot complete twice");
        var removed = bindings.Begin(pc.Id); bindings.Remove(pc.Id);
        t.Check(!bindings.IsPending(removed) && !bindings.Complete(removed, mesh), "completion after retirement/unbind refused before native placement");
        var previousWorld = bindings.Begin(pc.Id); bindings.Reset(Guid.NewGuid()); bindings.Begin(pc.Id);
        t.Check(!bindings.Complete(previousWorld, mesh), "completion from a previous world cannot bind a reused local key");

        t.Section("Objects: paired native saves and both carriers");
        var native = new byte[] { 1, 4, 9, 16 }; var position = new SnapshotPosition(2, 17, 123, bytes);
        var payloads = new[] { new CarrierPayload(new(3), new byte[] { 8, 9 }), new CarrierPayload(new(5), new byte[] { 8, 9 }) };
        var metadata = CoopSavePair.Create(native, position, payloads);
        var pair = CoopSavePair.Unpack(CoopSavePair.Pack(native, metadata));
        t.Check(pair.Native.SequenceEqual(native) && pair.Metadata.Position.State.SequenceEqual(bytes) && pair.Metadata.Carriers.Select(x => x.Pc).SequenceEqual(payloads.Select(x => x.Pc)),
            "native game bytes, both carried PC payloads and identities travel as one verified pair");
        t.Check(Throws(() => CoopSavePair.Verify(new byte[] { 2, 4, 9, 16 }, metadata)), "mismatched native/mod save pair explicitly rejected");
        t.Check(Throws(() => CoopSavePair.Unpack(CoopSavePair.Pack(native, metadata)[..^1])), "truncated paired transfer rejected");
        t.Check(Throws(() => CoopSavePair.Create(native, position, new[] { payloads[0], payloads[0] })), "duplicate carrier payload binding rejected");
        t.Check(Throws(() => CoopSavePair.Create(native, position, new[] { new CarrierPayload(new(3), new byte[CoopSavePair.MaxCarrierBytes + 1]) })), "oversized native carrier payload refused before native deserialization");
        var dir = Path.Combine(Path.GetTempPath(), "pcbs2-pair-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "freebuild.binary.zip"); File.WriteAllBytes(path, native);
            t.Check(Throws(() => CoopSavePair.ReadSidecar(path)), "native-only co-op save fails explicitly rather than reminting IDs");
            CoopSavePair.WriteSidecar(path, native, metadata);
            t.Check(CoopSavePair.Encode(CoopSavePair.ReadSidecar(path)).SequenceEqual(CoopSavePair.Encode(metadata)), "disk pair reload retains exact state and allocator");
            File.WriteAllBytes(path, new byte[] { 4, 3, 2, 1 });
            t.Check(Throws(() => CoopSavePair.ReadSidecar(path)), "interrupted native overwrite detected by sidecar hash");
        }
        finally { Directory.Delete(dir, true); }

        t.Section("Objects: host canonical payloads");
        var link = new FakeLink(); var h = new MintHandler(); var g = new MintHandler();
        link.Host.Register(20, h); link.Guest.Register(20, g); link.Host.BeginGeneration("mint"); link.Flush(); link.Join();
        var request = new NetDataWriter(); request.Put("same-sku");
        link.Guest.Submit(20, request.CopyData(), out _); var duplicate = link.ToHost[0].ToArray(); link.Flush();
        t.Check(h.Ids.SequenceEqual(g.Ids) && h.Ids.SequenceEqual(new ulong[] { 1 }) && h.CanonicalCalls == 1 && g.CanonicalCalls == 0 && link.InStep,
            "guest intent receives one host-assigned ID and both peers hash/apply the canonical bytes");
        FakeLink.Deliver(link.Host, duplicate); link.Flush();
        t.Check(h.Next == 2 && g.Next == 2 && h.CanonicalCalls == 1, "duplicate request neither allocates again nor reruns canonicalization");
        var forged = request.CopyData().Concat(BitConverter.GetBytes(999UL)).ToArray(); link.Guest.Submit(20, forged, out _); link.Flush();
        t.Check(h.Next == 2 && link.GuestRejected.Last().Reason == RejectReason.Malformed, "guest cannot append its own allocated ID to an intent");
        var bad = new MintHandler { Oversized = true }; link.Host.Register(21, bad); link.Guest.Register(21, new MintHandler());
        link.Guest.Submit(21, request.CopyData(), out _); link.Flush();
        t.Check(bad.Next == 1 && link.GuestRejected.Last().Reason == RejectReason.TooLarge, "oversized canonical result consumes no ID or world revision");

        t.Section("Objects: snapshot, held canonical creation and new-generation resume");
        var stream = new FakeLink(); var hm = new ObjectWorld(); var gm = new ObjectWorld();
        stream.Host.RegisterState(hm); stream.Guest.RegisterState(gm);
        stream.Host.Register(22, new WorldMintHandler(hm)); stream.Guest.Register(22, new WorldMintHandler(gm));
        stream.Host.BeginGeneration("objects"); stream.Flush();
        var baseline = Make(); hm.Read(new(baseline.ToBytes()));
        var mark = stream.MarkSnapshot();
        stream.Host.Submit(22, request.CopyData(), out _); stream.Flush();
        t.Check(gm.Pcs.Count == 0 && stream.Guest.Holding && hm.NextId == 10, "canonical object creation after snapshot mark is held during native loading");
        t.Check(stream.Guest.Adopt(out _) && hm.ToBytes().SequenceEqual(gm.ToBytes()) && stream.InStep,
            "snapshot identities plus held creation replay once with the host allocator and matching stream hash");
        var savedPosition = stream.Host.CapturePosition(); var savedModel = hm.ToBytes();
        stream.Host.BeginGeneration("host resume"); stream.Host.RestoreState(savedPosition.State);
        t.Check(hm.ToBytes().SequenceEqual(savedModel) && stream.Host.Revision == 0 && hm.NextId == 10,
            "new host generation restores paired object facts and allocator while starting a fresh change stream");
        stream.Flush(); stream.Join();
        t.Check(gm.ToBytes().SequenceEqual(savedModel), "rejoining guest adopts the resumed world's original IDs");
        stream.Guest.Submit(22, request.CopyData(), out _); stream.Flush();
        t.Check(hm.NextId == 12 && hm.ToBytes().SequenceEqual(gm.ToBytes()) && hm.Pcs.Any(x => x.Id.Value == 10),
            "post-resume guest creation receives the next unused host ID");
    }
    private static ObjectWorld Make()
    {
        var m = new ObjectWorld();
        var a = new PcFact(new(3), Origin, PcLocation.Bench("bench-A"), new[] { new PartFact(new(4), "same-case", "PartInstance") }, new[] { new PartLink("caseID", new(4)), new PartLink("caseID.m_generatedCopy", new(4)) });
        var b = new PcFact(new(5), Origin, PcLocation.Storage, new[] { new PartFact(new(6), "same-case", "PartInstance") }, new[] { new PartLink("caseID", new(6)) });
        m.Replace(Guid.NewGuid(), 8, new[] { new LocationFact(new(1), "bench-A"), new LocationFact(new(2), "bench-B") }, new[] { a, b }, new[] { new NetObjectId(7) }); return m;
    }
    private static bool Bad(Action<ObjectWorld> mutate) { var m = Make(); var before = m.ToBytes(); return Throws(() => mutate(m)) && before.SequenceEqual(m.ToBytes()); }
    private static bool Throws(Action action) { try { action(); return false; } catch (Exception) { return true; } }
    private sealed class MintHandler : ICanonicalCommandHandler
    {
        public string Name => "mint";
        public ulong Next = 1;
        public readonly List<ulong> Ids = new();
        public int CanonicalCalls;
        public bool Oversized;
        public CommandVerdict Validate(in CommandContext ctx, NetDataReader r) { r.GetString(64); return CommandVerdict.Accept; }
        public byte[] Canonicalize(in CommandContext ctx, NetDataReader r)
        {
            CanonicalCalls++; if (Oversized) return new byte[Protocol.MaxPayloadBytes + 1];
            var w = new NetDataWriter(); w.Put(Next); w.Put(r.GetString(64)); return w.CopyData();
        }
        public void Apply(in MutationContext ctx, NetDataReader r)
        { var id = r.GetULong(); r.GetString(64); if (id != Next) throw new InvalidDataException("allocator mismatch"); Ids.Add(id); Next++; }
    }
    private sealed class WorldMintHandler(ObjectWorld model) : ICanonicalCommandHandler
    {
        public string Name => "world mint";
        public CommandVerdict Validate(in CommandContext ctx, NetDataReader r) { r.GetString(64); return CommandVerdict.Accept; }
        public byte[] Canonicalize(in CommandContext ctx, NetDataReader r)
        { var w = new NetDataWriter(); w.Put(model.NextId); w.Put(r.GetString(64)); return w.CopyData(); }
        public void Apply(in MutationContext ctx, NetDataReader r)
        {
            var next = r.GetULong(); var sku = r.GetString(64);
            if (next != model.NextId) throw new InvalidDataException("Allocator mismatch.");
            var pc = new PcFact(new(next), new(InstanceOrigin.Catalog, ctx.Op.Actor == ActorSlot.Host ? Host : Guest, ctx.Op), PcLocation.Storage,
                new[] { new PartFact(new(next + 1), sku, "PartInstance") }, new[] { new PartLink("caseID", new(next + 1)) });
            model.Replace(model.WorldId, next + 2, model.Locations, model.Pcs.Append(pc), model.Retired);
        }
    }
}

using LiteNetLib.Utils;
using PCBS2Coop.Network;

internal static class AssemblyResourceTests
{
    private static NetObjectId Id(ulong n) => new(n);
    private static AssemblyResource Slot(ulong id, ulong parent, string path, ulong occupant = 0) =>
        new(Id(id), Id(parent), path, "Slot", AssemblyResourceKind.Slot, Id(occupant), false, 0, 0, Array.Empty<NetObjectId>());

    private static ObjectWorld Make()
    {
        var model = new ObjectWorld();
        var pc = new PcFact(Id(2), new(InstanceOrigin.Baseline, PlayerId.New(), default), PcLocation.Bench("A"),
            new[] { new PartFact(Id(3), "case", "CasePartInstance"), new PartFact(Id(4), "board", "MotherboardPartInstance"),
                new PartFact(Id(5), "identical-ram", "PartInstance"), new PartFact(Id(6), "aio", "PartInstance"),
                new PartFact(Id(7), "identical-ram", "PartInstance") },
            new[] { new PartLink("caseID", Id(3)), new PartLink("motherboardID", Id(4)), new PartLink("ramSlots[0]", Id(5)),
                new PartLink("ramSlots[1]", Id(7)), new PartLink("coolerID", Id(6)), new PartLink("radiatorSlots[0]", Id(6)),
                new PartLink("radiatorSlots[1]", Id(6)) })
        {
            Resources = new[] { Slot(8, 3, "motherboard", 4), Slot(9, 4, "RAM[0]", 5), Slot(10, 4, "RAM[1]", 7),
                Slot(11, 4, "RAM[2]"), Slot(12, 4, "RAM[3]"), Slot(13, 4, "cooler", 6),
                Slot(14, 3, "radiator[0]", 6), Slot(15, 3, "radiator[1]", 6),
                new AssemblyResource(Id(16), Id(4), "RAM[0]/clip[0]", "Pin", AssemblyResourceKind.Pin, default, true, 0, 0, new[] { Id(9) }),
                new AssemblyResource(Id(17), Id(4), "M2[0]/cover[3]", "Pin", AssemblyResourceKind.Pin, default, false, 0, 0, new[] { Id(11) }) }
        };
        model.Replace(Guid.NewGuid(), 18, new[] { new LocationFact(Id(1), "A") }, new[] { pc }, Array.Empty<NetObjectId>());
        return model;
    }

    private static void Publish(ObjectWorld model, PcFact pc) => model.Replace(model.WorldId, model.NextId, model.Locations, new[] { pc }, model.Retired);
    private static PcFact Change(PcFact pc, ulong resource, Func<AssemblyResource, AssemblyResource> edit) =>
        pc with { Resources = pc.Resources.Select(r => r.Id == Id(resource) ? edit(r) : r).ToArray() };
    private static bool Throws(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
    private static byte[] Bytes(ActivityWorld activity) { var w = new NetDataWriter(); activity.Write(w); return w.CopyData(); }

    public static void Run(Checker t)
    {
        t.Section("Assembly: parent-scoped slots, mechanics, aliases and persistence");
        var model = Make(); var pc = model.Pcs.Single(); var original = model.ToBytes(); var copy = new ObjectWorld(); copy.Read(new(original));
        t.Check(copy.ToBytes().SequenceEqual(original) && copy.Pcs.Single().Resources.Single(r => r.Id == Id(16)).Open,
            "empty/occupied slots, mechanical state and dependencies round-trip with stable IDs");
        t.Check(model.Lookup(Id(11)) == ObjectLookup.Live && model.Lookup(Id(16)) == ObjectLookup.Live,
            "empty destinations and mechanics resolve as live resources independently of installed parts");
        t.Check(!AssemblyResources.Conflicts(pc, new[] { Id(11) }, new[] { Id(12) }) &&
            !AssemblyResources.Conflicts(pc, new[] { Id(9) }, new[] { Id(10) }),
            "disjoint empty slots and distinct identical RAM sticks do not lock their whole motherboard");
        t.Check(AssemblyResources.Expand(pc, new[] { Id(13) }).SequenceEqual(new[] { Id(6), Id(13), Id(14), Id(15) }),
            "one AIO reservation includes its physical part and every proxy/radiator occupancy alias");
        t.Check(AssemblyResources.Conflicts(pc, new[] { Id(13) }, new[] { Id(15) }),
            "different slots occupied by the same multi-slot part contend");
        t.Check(AssemblyResources.Conflicts(pc, new[] { Id(4) }, new[] { Id(12) }) &&
            AssemblyResources.Conflicts(pc, new[] { Id(12) }, new[] { Id(3) }),
            "parent removal conflicts with descendants in both directions, including empty destinations");
        t.Check(AssemblyResources.Conflicts(pc, new[] { Id(16) }, new[] { Id(9) }) &&
            AssemblyResources.Conflicts(pc, new[] { Id(17) }, new[] { Id(11) }),
            "latch/cover reservations acquire the guarded occupancy resources atomically");
        var sharedGuard = Change(Change(pc, 11, r => r with { Dependencies = new[] { Id(17) }, Version = 1 }),
            12, r => r with { Dependencies = new[] { Id(17) }, Version = 1 });
        t.Check(!AssemblyResources.Conflicts(sharedGuard, new[] { Id(11) }, new[] { Id(12) }),
            "disjoint edits may read the same panel/cover guard without acquiring it exclusively");
        t.Check(AssemblyResources.Conflicts(sharedGuard, new[] { Id(11) }, new[] { Id(17) }),
            "editing a guard conflicts with another edit that relies on its unchanged state");
        var linked = Change(Change(pc, 11, r => r with { Coupled = new[] { Id(16) }, Version = 1 }),
            12, r => r with { Coupled = new[] { Id(16) }, Version = 1 });
        t.Check(AssemblyResources.Conflicts(linked, new[] { Id(11) }, new[] { Id(12) }),
            "edits that both change a coupled latch still conflict exclusively");
        var aliasStamp = AssemblyResources.Stamp(pc, new[] { Id(13) });
        t.Check(aliasStamp == AssemblyResources.Stamp(pc, new[] { Id(15) }), "equivalent alias targets have one binding stamp");
        var state = Change(pc, 16, r => r with { Open = false, Rotation = 180, Offset = 0.5f, Version = 1 });
        Publish(model, state);
        t.Check(model.Pcs.Single().Resources.Single(r => r.Id == Id(16)).Version == 1,
            "accepted mechanical state advances its version without changing identity");
        copy.Read(new(model.ToBytes()));
        t.Check(copy.ToBytes().SequenceEqual(model.ToBytes()), "mechanical rotation/offset/version survive exact reconstruction");
        var profileModel = Make(); var connectorNames = new[] { "PSU_PWR", "GPU_PWR" };
        var profiled = Change(profileModel.Pcs.Single(), 11, r => r with { IntegralPart = "case/panel", Version = 1 }) with
            { BuildProfile = new PcBuildProfile(new string('A', 64), connectorNames) };
        Publish(profileModel, profiled); var profileBytes = profileModel.ToBytes(); connectorNames[0] = "changed";
        var profileCopy = new ObjectWorld(); profileCopy.Read(new(profileBytes));
        t.Check(profileModel.ToBytes().SequenceEqual(profileBytes) && profileCopy.ToBytes().SequenceEqual(profileBytes) &&
            profileCopy.Pcs.Single().Resources.Single(r => r.Id == Id(11)).IntegralPart == "case/panel",
            "integral-part presence and native build integrity profile round-trip without retaining mutable connector arrays");

        void Bad(Func<PcFact, PcFact> edit, string message)
        {
            var m = Make(); var before = m.ToBytes();
            t.Check(Throws(() => Publish(m, edit(m.Pcs.Single()))) && before.SequenceEqual(m.ToBytes()), message);
        }
        Bad(p => Change(p, 11, r => r with { Parent = Id(99) }), "unknown parent rejected without publishing a partial model");
        Bad(p => Change(p, 11, r => r with { Parent = Id(5) }), "existing slot ID cannot be rebound to another live physical parent");
        Bad(p => Change(p, 11, r => r with { Binding = "RAM[3]" }), "duplicate parent-relative binding rejected");
        Bad(p => Change(p, 11, r => r with { Binding = "replacement-path" }), "same resource ID cannot change its prefab binding");
        Bad(p => Change(p, 11, r => r with { Occupant = Id(99), Version = 1 }), "unknown occupant rejected");
        Bad(p => Change(p, 11, r => r with { Occupant = Id(3), Version = 1 }), "cyclic part ownership through a child slot rejected");
        Bad(p => Change(p, 16, r => r with { Occupant = Id(5), Version = 1 }), "mechanics cannot masquerade as occupied slots");
        Bad(p => Change(p, 16, r => r with { Rotation = float.NaN, Version = 1 }), "nonfinite mechanical state rejected");
        Bad(p => Change(p, 16, r => r with { Open = false }), "state changes without a resource version increment rejected");
        Bad(p => Change(p, 11, r => r with { IntegralPart = "case/panel" }), "integral case membership changes must advance their resource version");
        Bad(p => p with { BuildProfile = new PcBuildProfile("invalid", Array.Empty<string>()) }, "malformed native build signature rejected atomically");
        Bad(p => p with { BuildProfile = new PcBuildProfile(new string('A', 64), new[] { "GPU", "GPU" }) }, "duplicate internal connector identities rejected atomically");
        Bad(p => Change(p, 16, r => r with { Dependencies = new[] { Id(99) }, Version = 1 }), "cross-PC/unknown dependencies rejected");
        Bad(p => Change(p, 16, r => r with { Dependencies = new[] { Id(9), Id(9) }, Version = 1 }), "duplicate dependency IDs rejected");
        Bad(p => p with { Resources = p.Resources.Append(p.Resources[0]).ToArray() }, "duplicate resource IDs rejected atomically");
        Bad(p => p with { Parts = p.Parts.Select(x => x.Id == Id(5) ? x with { Sku = "different-ram" } : x).ToArray() },
            "an issued physical part ID cannot change SKU in place");
        t.Check(Throws(() => Publish(model, pc)) && model.Pcs.Single().Resources.Single(r => r.Id == Id(16)).Version == 1,
            "resource versions cannot move backwards in the same world");
        var beforeRead = model.ToBytes();
        t.Check(Throws(() => model.Read(new(beforeRead[..^1]))) && model.ToBytes().SequenceEqual(beforeRead),
            "truncated assembly state cannot replace any previous object facts");
        var mutable = new[] { Id(9) }; var mutableRows = pc.Resources.ToArray();
        mutableRows[^2] = mutableRows[^2] with { Dependencies = mutable };
        var frozen = Make(); Publish(frozen, pc with { Resources = mutableRows }); var frozenBytes = frozen.ToBytes();
        mutable[0] = Id(99); mutableRows[0] = mutableRows[1];
        t.Check(frozen.ToBytes().SequenceEqual(frozenBytes), "published resource and dependency lists are detached from caller mutation");
        var indices = pc.Resources.Select((r, i) => (r.Id, Index: i)).ToDictionary(x => x.Id, x => x.Index);
        var templates = pc.Resources.Select(r => new AssemblyTemplate(r.Parent, r.Binding, r.NativeType, r.Kind, r.Occupant, r.Open, r.Rotation, r.Offset,
            r.Dependencies.Select(d => indices[d]).ToArray(), r.Coupled.Select(d => indices[d]).ToArray()) { IntegralPart = r.IntegralPart }).ToArray();
        var repairAllocator = new NetObjectIdAllocator { Next = 18 };
        var repaired = AssemblyManifest.Repair(pc, templates, repairAllocator);
        t.Check(repairAllocator.Next == 18 && repaired.Resources.Select(r => r.Id).SequenceEqual(pc.Resources.Select(r => r.Id)),
            "explicit repair preserves unchanged physical resource identities without allocating again");
        var alteredTemplates = templates.Select((r, i) => i == 8 ? r with { Open = false } : r).ToArray();
        repaired = AssemblyManifest.Repair(pc, alteredTemplates, repairAllocator);
        t.Check(repairAllocator.Next == 18 && repaired.Resources.Single(r => r.Id == Id(16)).Version == 1 &&
            repaired.Resources.Where(r => r.Id != Id(16)).All(r => r.Version == 0), "repair advances only changed mechanical facts and keeps unaffected incarnations");

        t.Section("Assembly: host-ordered claims and obsolete callback revalidation");
        var l = new FakeLink(); var ho = Make(); var go = new ObjectWorld(); var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        l.Host.RegisterState(ho); l.Guest.RegisterState(go);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        // BeginGeneration resets state; retain this fixture as the generation's explicit baseline.
        var baseline = ho.ToBytes(); l.Host.BeginGeneration("assembly"); ho.Read(new(baseline));
        OperationId Send(WorldSync world, byte[] data) { world.Submit(CommandTypes.ActorActivity, data, out var op); l.Flush(); return op; }
        ActorActivity H() => ha.Get(ActorSlot.Host)!; ActorActivity G() => ga.Get(ActorSlot.Guest)!;
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, PlayerId.New()));
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, PlayerId.New())); l.Join();
        Send(l.Host, ActivityWorld.Work(H(), Id(2))); Send(l.Guest, ActivityWorld.Work(G(), Id(2)));
        var hop = Send(l.Host, ActivityWorld.Reserve(H(), Id(2), ActivityOperation.Edit, Id(11)));
        var gop = Send(l.Guest, ActivityWorld.Reserve(G(), Id(2), ActivityOperation.Edit, Id(12)));
        t.Check(H().Pending != null && G().Pending != null && Bytes(ha).SequenceEqual(Bytes(ga)) && l.InStep,
            "both peers acquire disjoint empty destinations on one PC through host ordering");
        t.Check(ha.Owns(ActorSlot.Host, H().Epoch, hop, Id(2), ActivityOperation.Edit, Id(11)), "an unchanged acquired destination authorizes its own callback");
        Send(l.Guest, ActivityWorld.Release(G(), gop));
        var rev = l.Host.Revision;
        Send(l.Guest, ActivityWorld.Reserve(G(), Id(2), ActivityOperation.Edit, Id(11), Id(12)));
        t.Check(l.Host.Revision == rev && G().Pending == null, "contested multi-slot acquisition is refused without claiming its free subset");
        Send(l.Guest, ActivityWorld.Reserve(G(), Id(2), ActivityOperation.Edit, Id(4)));
        t.Check(l.Host.Revision == rev, "parent removal is refused while a child destination is reserved");
        Send(l.Guest, ActivityWorld.Reserve(G(), Id(2), ActivityOperation.Edit, Id(17)));
        t.Check(l.Host.Revision == rev, "cover action is refused while its dependency is reserved");
        var live = ho.Pcs.Single();
        var unrelated = Change(live, 16, r => r with { Open = false, Version = 1 }); Publish(ho, unrelated);
        t.Check(ha.Owns(ActorSlot.Host, H().Epoch, hop, Id(2), ActivityOperation.Edit, Id(11)), "an unrelated latch edit preserves the pending callback's authority");
        var changed = Change(unrelated, 11, r => r with { Version = 1 }); Publish(ho, changed);
        t.Check(!ha.Owns(ActorSlot.Host, H().Epoch, hop, Id(2), ActivityOperation.Edit, Id(11)),
            "a destination changed back to the same empty state still invalidates its older callback");
        go.Read(new(ho.ToBytes()));
        Send(l.Host, ActivityWorld.Release(H(), hop));
        hop = Send(l.Host, ActivityWorld.Reserve(H(), Id(2), ActivityOperation.Edit, Id(13)));
        t.Check(H().Pending!.Targets.SequenceEqual(new[] { Id(6), Id(13), Id(14), Id(15) }) &&
            ha.Owns(ActorSlot.Host, H().Epoch, hop, Id(2), ActivityOperation.Edit, Id(15)) && Bytes(ha).SequenceEqual(Bytes(ga)),
            "host canonicalizes alias/dependency closure and both peers revalidate the same acquisition");
        rev = l.Host.Revision; Send(l.Guest, ActivityWorld.Reserve(G(), Id(2), ActivityOperation.Edit, Id(14)));
        t.Check(l.Host.Revision == rev, "a second player cannot acquire another proxy of the reserved AIO");
        var savedActivity = Bytes(ha); var restoredActivity = new ActivityWorld(ho); restoredActivity.Read(new(savedActivity));
        t.Check(Bytes(restoredActivity).SequenceEqual(savedActivity) && restoredActivity.Owns(ActorSlot.Host, H().Epoch, hop, Id(2), ActivityOperation.Edit, Id(13)),
            "activity restore retains exact expanded resources, actor epoch, token and binding stamp");
        var forgedStamp = savedActivity.ToArray();
        var stampOffset = forgedStamp.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(H().Pending!.BindingStamp));
        if (stampOffset < 0) throw new InvalidOperationException("The saved claim has no binding stamp.");
        forgedStamp[stampOffset] ^= 1;
        t.Check(Throws(() => restoredActivity.Read(new(forgedStamp))) && Bytes(restoredActivity).SequenceEqual(savedActivity),
            "forged saved binding stamp is rejected before any actor state is replaced");
        var snapshot = l.MarkSnapshot(); Send(l.Host, ActivityWorld.Release(H(), hop));
        t.Check(l.Guest.Adopt(out _) && G().Pending == null && ga.Get(ActorSlot.Host)!.Pending == null && l.InStep,
            "a release after the snapshot mark replays over the saved assembly claim before admission");
        var native = new byte[] { 1, 2, 3 }; var pair = CoopSavePair.Create(native, snapshot, Array.Empty<CarrierPayload>());
        var unpacked = CoopSavePair.Unpack(CoopSavePair.Pack(native, pair));
        var resume = new FakeLink(); var ro = new ObjectWorld(); var ra = new ActivityWorld(ro);
        resume.Host.RegisterState(ro); resume.Host.RegisterState(ra); resume.Host.BeginGeneration("resume"); resume.Host.RestoreState(unpacked.Metadata.Position.State);
        t.Check(ro.ToBytes().SequenceEqual(ho.ToBytes()) && ra.Get(ActorSlot.Host)!.Pending!.Token == hop,
            "hash-matched paired save reconstructs assembly identities/mechanics and the snapshot's claim");
        var stale = Bytes(ra); var rpc = ro.Pcs.Single(); Publish(ro, Change(rpc, 13, r => r with { Version = 1 }));
        t.Check(Throws(() => ra.Read(new(stale))) && Bytes(ra).SequenceEqual(stale), "stale saved claim rejected without publishing a partial activity candidate");
        t.Check(!ra.Owns(ActorSlot.Host, ra.Get(ActorSlot.Host)!.Epoch, hop, Id(2), ActivityOperation.Edit, Id(13)), "an obsolete restored claim cannot authorize native work");
        var parentOp = Send(l.Host, ActivityWorld.Reserve(H(), Id(2), ActivityOperation.Edit, Id(4)));
        rev = l.Host.Revision; Send(l.Guest, ActivityWorld.Reserve(G(), Id(2), ActivityOperation.Edit, Id(12)));
        t.Check(l.Host.Revision == rev && H().Pending!.Token == parentOp && G().Pending == null,
            "a reserved parent also refuses later child acquisition without a partial child claim");
        var oldEpoch = H().Epoch;
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, PlayerId.New()));
        t.Check(H().Pending == null && !ha.Owns(ActorSlot.Host, oldEpoch, parentOp, Id(2), ActivityOperation.Edit, Id(4)),
            "actor replacement releases the entire expanded parent claim and invalidates its callbacks");

        // A same-SKU motherboard replacement receives fresh part and child-resource IDs, not recycled bindings.
        var replacementModel = Make(); var oldPc = replacementModel.Pcs.Single();
        var replacements = oldPc.Resources.Where(r => r.Parent == Id(4)).Select(r => r.Id).Append(Id(4)).ToHashSet();
        var allocator = new NetObjectIdAllocator { Next = replacementModel.NextId };
        var map = replacements.OrderBy(i => i.Value).ToDictionary(i => i, _ => allocator.Allocate());
        NetObjectId Remap(NetObjectId id) => map.GetValueOrDefault(id, id);
        var replacement = oldPc with
        {
            Parts = oldPc.Parts.Select(p => p with { Id = Remap(p.Id) }).ToArray(),
            Links = oldPc.Links.Select(p => p with { Part = Remap(p.Part) }).ToArray(),
            Resources = oldPc.Resources.Select(r => r with { Id = Remap(r.Id), Parent = Remap(r.Parent), Occupant = Remap(r.Occupant),
                Version = r.Id == Id(8) ? 1UL : r.Version, Dependencies = r.Dependencies.Select(Remap).ToArray() }).ToArray()
        };
        replacementModel.Replace(replacementModel.WorldId, allocator.Next, replacementModel.Locations, new[] { replacement }, replacements);
        t.Check(replacementModel.Lookup(Id(4)) == ObjectLookup.Retired && replacementModel.Lookup(Id(11)) == ObjectLookup.Retired &&
            replacementModel.Pcs.Single().Parts.Single(p => p.Id == map[Id(4)]).Sku == "board",
            "identical parent replacement retires its empty/mechanical resources and mints fresh incarnations");
        t.Check(Throws(() => AssemblyResources.Expand(replacementModel.Pcs.Single(), new[] { Id(11) })), "an obsolete callback's slot ID cannot resolve to the replacement motherboard");

        // Exercise the existing PC lifecycle deletion with resource-bearing facts, rather than a test-only retire.
        var deletion = new FakeLink(); var delObjects = Make(); var delState = delObjects.ToBytes(); var delActivity = new ActivityWorld(delObjects);
        deletion.Host.RegisterState(delObjects); deletion.Host.RegisterState(delActivity);
        deletion.Host.Register(CommandTypes.ActorActivity, delActivity);
        deletion.Host.Register(CommandTypes.PcLifecycle, new PcLifecycle(delObjects, delActivity, new FakePcNative(), true));
        deletion.Host.BeginGeneration("delete"); delObjects.Read(new(delState));
        Publish(delObjects, delObjects.Pcs.Single() with { Location = PcLocation.Storage });
        deletion.Host.Submit(CommandTypes.ActorActivity, ActivityWorld.Presence(ActorSlot.Host, PlayerId.New()), out _);
        deletion.Host.Submit(CommandTypes.PcLifecycle, PcLifecycle.Delete(delActivity.Get(ActorSlot.Host)!.Epoch, Id(2), PcRemoval.Stored), out _);
        t.Check(delObjects.Pcs.Count == 0 && Enumerable.Range(2, 16).All(n => delObjects.Lookup(Id((ulong)n)) == ObjectLookup.Retired),
            "existing PC deletion retires all part, empty-slot and mechanical resource IDs exactly once");

        var divergent = new FakeLink(); var dh = Make(); var dg = new ObjectWorld(); var da = new ActivityWorld(dh); var db = new ActivityWorld(dg);
        divergent.Host.RegisterState(dh); divergent.Guest.RegisterState(dg);
        divergent.Host.RegisterState(da); divergent.Guest.RegisterState(db);
        divergent.Host.Register(CommandTypes.ActorActivity, da); divergent.Guest.Register(CommandTypes.ActorActivity, db);
        var ds = dh.ToBytes(); divergent.Host.BeginGeneration("divergent"); dh.Read(new(ds));
        divergent.Host.Submit(CommandTypes.ActorActivity, ActivityWorld.Presence(ActorSlot.Host, PlayerId.New()), out _); divergent.Flush(); divergent.Join();
        divergent.Host.Submit(CommandTypes.ActorActivity, ActivityWorld.Work(da.Get(ActorSlot.Host)!, Id(2)), out _); divergent.Flush();
        Publish(dg, Change(dg.Pcs.Single(), 11, r => r with { Version = 1 }));
        divergent.Host.Submit(CommandTypes.ActorActivity, ActivityWorld.Reserve(da.Get(ActorSlot.Host)!, Id(2), ActivityOperation.Edit, Id(11)), out _);
        divergent.Flush();
        t.Check(da.Get(ActorSlot.Host)!.Pending != null && db.Get(ActorSlot.Host)!.Pending == null && divergent.Divergences.Count == 1,
            "a peer with different resource state detects the host binding stamp mismatch instead of authorizing a different callback");
        Manifest(t);
        SharedGuards(t);
    }

    private sealed class NativeManifest : IAssemblyNative
    {
        public int Described, Applied;
        public bool Oversized;
        public IReadOnlyList<AssemblyTemplate>? Describe(PcFact pc)
        {
            Described++;
            if (Oversized) return Enumerable.Range(0, 1200).Select(i => new AssemblyTemplate(Id(4), new string('x', 990) + i, "Slot",
                AssemblyResourceKind.Slot, default, false, 0, 0, Array.Empty<int>(), Array.Empty<int>())).ToArray();
            return new[] { new AssemblyTemplate(Id(4), "slot:RAM[2]", "Slot", AssemblyResourceKind.Slot, default, false, 0, 0,
                    Array.Empty<int>(), new[] { 1 }),
                new AssemblyTemplate(Id(4), "pin:RAM[2]/clip[0]", "Pin", AssemblyResourceKind.Pin, default, true, 0, 0,
                    new[] { 0 }, Array.Empty<int>()) };
        }
        public void Bound(PcFact pc) => Applied++;
        public PcBuildProfile Profile(PcFact pc) => new(new string('A', 64), new[] { "GPU_PWR", "PSU_PWR" });
    }

    private static void Manifest(Checker t)
    {
        t.Section("Assembly: host publication of newly loaded prefab manifests");
        var link = new FakeLink(); var hostObjects = Make(); var guestObjects = new ObjectWorld();
        var original = hostObjects.Pcs.Single();
        hostObjects.Replace(hostObjects.WorldId, hostObjects.NextId, hostObjects.Locations,
            new[] { original with { Resources = Array.Empty<AssemblyResource>() } }, original.Resources.Select(r => r.Id));
        var baseline = hostObjects.ToBytes(); var ha = new ActivityWorld(hostObjects); var ga = new ActivityWorld(guestObjects);
        var hn = new NativeManifest(); var gn = new NativeManifest();
        link.Host.RegisterState(hostObjects); link.Guest.RegisterState(guestObjects);
        link.Host.RegisterState(ha); link.Guest.RegisterState(ga);
        link.Host.Register(CommandTypes.ActorActivity, ha); link.Guest.Register(CommandTypes.ActorActivity, ga);
        link.Host.Register(CommandTypes.AssemblyManifest, new AssemblyManifest(hostObjects, ha, hn));
        link.Guest.Register(CommandTypes.AssemblyManifest, new AssemblyManifest(guestObjects, ga, gn));
        link.Host.BeginGeneration("prefab"); hostObjects.Read(new(baseline));
        link.Host.Submit(CommandTypes.ActorActivity, ActivityWorld.Presence(ActorSlot.Host, PlayerId.New()), out _);
        link.Host.Submit(CommandTypes.ActorActivity, ActivityWorld.Presence(ActorSlot.Guest, PlayerId.New()), out _); link.Flush(); link.Join();
        link.Guest.Submit(CommandTypes.AssemblyManifest, AssemblyManifest.Bind(Id(2)), out _); link.Flush();
        t.Check(hostObjects.NextId == 18 && hn.Applied == 0, "guest cannot allocate a prefab manifest or force host observation");
        link.MarkSnapshot();
        link.Host.Submit(CommandTypes.AssemblyManifest, AssemblyManifest.Bind(Id(2)), out _); link.Flush();
        t.Check(hostObjects.NextId == 20 && hostObjects.Pcs.Single().Resources.Select(r => r.Id).SequenceEqual(new[] { Id(18), Id(19) }) &&
            guestObjects.Pcs.Single().Resources.Count == 0, "host allocates the whole manifest once while a loading guest holds the mutation");
        t.Check(link.Guest.Adopt(out _) && hostObjects.ToBytes().SequenceEqual(guestObjects.ToBytes()) && gn.Described == 0 && gn.Applied == 1,
            "held prefab publication replays with host IDs and does not rediscover or allocate from the guest's assets");
        link.Host.Submit(CommandTypes.AssemblyManifest, AssemblyManifest.Bind(Id(2)), out _); link.Flush();
        t.Check(hostObjects.NextId == 20 && hn.Applied == 1 && gn.Applied == 1, "a repeated binding request cannot reallocate live resources");
        var before = hostObjects.ToBytes(); var candidate = new ObjectWorld(); candidate.Read(new(before));
        var invalid = hn.Describe(original)!.Select((r, i) => i == 0 ? r with { Guards = new[] { 500 } } : r).ToArray();
        t.Check(Throws(() => AssemblyManifest.Build(original with { Resources = Array.Empty<AssemblyResource>() }, invalid,
            new NetObjectIdAllocator { Next = 20 })) && hostObjects.ToBytes().SequenceEqual(before), "invalid prefab reference cannot partially allocate or publish host facts");
        var bigLink = new FakeLink(); var bigObjects = new ObjectWorld(); var bigActivity = new ActivityWorld(bigObjects); var bigNative = new NativeManifest { Oversized = true };
        bigLink.Host.RegisterState(bigObjects); bigLink.Host.RegisterState(bigActivity);
        bigLink.Host.Register(CommandTypes.ActorActivity, bigActivity);
        bigLink.Host.Register(CommandTypes.AssemblyManifest, new AssemblyManifest(bigObjects, bigActivity, bigNative));
        bigLink.Host.BeginGeneration("oversized"); bigObjects.Read(new(baseline));
        bigLink.Host.Submit(CommandTypes.ActorActivity, ActivityWorld.Presence(ActorSlot.Host, PlayerId.New()), out _);
        bigLink.Host.Submit(CommandTypes.AssemblyManifest, AssemblyManifest.Bind(Id(2)), out _);
        t.Check(bigObjects.ToBytes().SequenceEqual(baseline) && bigNative.Applied == 0 && bigLink.Host.Divergence == null,
            "a compressible manifest above the expansion cap is refused before allocating or diverging the host");
    }

    private static void SharedGuards(Checker t)
    {
        var link = new FakeLink(); var ho = Make(); var go = new ObjectWorld();
        Publish(ho, Change(Change(ho.Pcs.Single(), 11, r => r with { Dependencies = new[] { Id(17) }, Version = 1 }),
            12, r => r with { Dependencies = new[] { Id(17) }, Version = 1 }));
        var bytes = ho.ToBytes(); var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        link.Host.RegisterState(ho); link.Guest.RegisterState(go); link.Host.RegisterState(ha); link.Guest.RegisterState(ga);
        link.Host.Register(CommandTypes.ActorActivity, ha); link.Guest.Register(CommandTypes.ActorActivity, ga);
        link.Host.BeginGeneration("shared-guards"); ho.Read(new(bytes));
        void Send(WorldSync w, byte[] data) { w.Submit(CommandTypes.ActorActivity, data, out _); link.Flush(); }
        Send(link.Host, ActivityWorld.Presence(ActorSlot.Host, PlayerId.New())); Send(link.Host, ActivityWorld.Presence(ActorSlot.Guest, PlayerId.New())); link.Join();
        Send(link.Host, ActivityWorld.Work(ha.Get(ActorSlot.Host)!, Id(2))); Send(link.Guest, ActivityWorld.Work(ga.Get(ActorSlot.Guest)!, Id(2)));
        Send(link.Host, ActivityWorld.Reserve(ha.Get(ActorSlot.Host)!, Id(2), ActivityOperation.Edit, Id(11)));
        Send(link.Guest, ActivityWorld.Reserve(ga.Get(ActorSlot.Guest)!, Id(2), ActivityOperation.Edit, Id(12)));
        t.Check(ha.Actors.All(a => a.Pending != null) && ha.Get(ActorSlot.Host)!.Pending!.Writes.SequenceEqual(new[] { Id(11) }) &&
            ha.Get(ActorSlot.Host)!.Pending!.Targets.SequenceEqual(new[] { Id(11), Id(17) }),
            "host ordering permits concurrent writes with a shared read guard and records their distinct access modes");
        link.MarkSnapshot();
        t.Check(link.Guest.Adopt(out _) && Bytes(ha).SequenceEqual(Bytes(ga)) && link.InStep,
            "snapshot restoration preserves compatible shared guards without promoting them to conflicting writes");
    }
}

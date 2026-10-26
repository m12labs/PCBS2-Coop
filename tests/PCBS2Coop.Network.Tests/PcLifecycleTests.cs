using LiteNetLib.Utils;
using PCBS2Coop.Network;

/// <summary>Stands in for the native adapter: fixed catalog templates, a scripted host check and a call log.</summary>
internal sealed class FakePcNative : IPcNative
{
    public readonly Dictionary<string, PcTemplate> Templates = new()
    {
        ["kolink"] = Template("kolink"), ["kolink-white"] = Template("kolink-white"),
    };
    public Func<PcCommand, PcFact?, string?> Host = (_, _) => null;
    public readonly List<string> Calls = new();
    public static PcTemplate Template(string sku) => new(new[] { new TemplatePart(sku, "CasePartInstance"), new TemplatePart("fan", "FanPartInstance") },
        new[] { new TemplateLink("caseFanSlots[0]", 1), new TemplateLink("caseID", 0), new TemplateLink("caseID.m_caseFanSlots[0]", 1) });
    public PcTemplate? Describe(CatalogRef t) => t.Ordinal == 0 && Templates.TryGetValue(t.Sku, out var x) ? x : null;
    public string? CheckHost(in CommandContext ctx, PcCommand c, PcFact? target) => Host(c, target);
    public void Created(in MutationContext ctx, PcFact pc, CatalogRef t, PcFact? replaced) =>
        Calls.Add($"create {pc.Id.Value} {t.Sku} local={ctx.FromLocalPlayer} replaced={replaced?.Id.Value ?? 0}");
    public void Deleted(in MutationContext ctx, PcFact pc, PcRemoval from) => Calls.Add($"delete {pc.Id.Value} {from} local={ctx.FromLocalPlayer}");
}

internal static class PcLifecycleTests
{
    public static void Run(Checker t)
    {
        t.Section("PC lifecycle: host-ordered creation, removal and observed locations");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hn = new FakePcNative(); var gn = new FakePcNative();
        var hp = new PcLifecycle(ho, ha, hn, true); var gp = new PcLifecycle(go, ga, gn, false);
        l.Host.RegisterState(ho); l.Guest.RegisterState(go);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.Register(CommandTypes.PcLifecycle, hp); l.Guest.Register(CommandTypes.PcLifecycle, gp);
        l.Host.BeginGeneration("lifecycle");
        var host = PlayerId.New(); var guest = PlayerId.New();
        var origin = new InstanceProvenance(InstanceOrigin.Baseline, host, default);
        ho.Replace(Guid.NewGuid(), 9, new[] { new LocationFact(new(1), "A"), new LocationFact(new(2), "B") },
            new[] { new PcFact(new(3), origin, PcLocation.Bench("A"), new[] { new PartFact(new(4), "msi", "CasePartInstance") }, new[] { new PartLink("caseID", new(4)) }),
                new PcFact(new(5), origin, PcLocation.Storage, new[] { new PartFact(new(6), "bequiet", "CasePartInstance") }, new[] { new PartLink("caseID", new(6)) }) },
            new[] { new NetObjectId(7), new NetObjectId(8) });
        OperationId Send(WorldSync w, byte[] bytes, ushort type = CommandTypes.PcLifecycle) { w.Submit(type, bytes, out var op); l.Flush(); return op; }
        ulong E(ActivityWorld a, ActorSlot s) => a.Get(s)!.Epoch;
        bool Same() => ho.ToBytes().SequenceEqual(go.ToBytes()) && l.InStep && l.Divergences.Count == 0;
        PcFact? Pc(ObjectWorld o, ulong id) => o.Pcs.SingleOrDefault(x => x.Id.Value == id);
        var kolink = new CatalogRef("kolink", "CasePartInstance", 0);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, host), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, guest), CommandTypes.ActorActivity);
        l.Join();

        var hostOp = Send(l.Host, PcLifecycle.Create(E(ha, ActorSlot.Host), kolink, default));
        var created = Pc(ho, 9);
        t.Check(created is { Location.Place: PcPlace.Carrier, Location.Actor: ActorSlot.Host } && created.Provenance == new InstanceProvenance(InstanceOrigin.Catalog, host, hostOp) &&
            created.Parts.Select(p => p.Id.Value).SequenceEqual(new ulong[] { 10, 11 }) && ho.NextId == 12 && Same(),
            "host catalog case becomes one new physical PC in the host's hands with host-assigned IDs on both peers");
        t.Check(hn.Calls.Last() == "create 9 kolink local=True replaced=0" && gn.Calls.Last() == "create 9 kolink local=False replaced=0",
            "requesting peer projects the PC natively; the other peer receives it as a remote carrier");
        var guestOp = Send(l.Guest, PcLifecycle.Create(E(ga, ActorSlot.Guest), kolink, default));
        t.Check(Pc(go, 12) is { Location.Actor: ActorSlot.Guest } g && g.Provenance.Operation == guestOp && g.Parts.Count == 2 && Same(),
            "the same catalog entry mints a second distinct PC for the guest; the entry is not consumed");
        var rev = l.Host.Revision;
        Send(l.Host, PcLifecycle.Create(E(ha, ActorSlot.Host), kolink, default));
        t.Check(l.Host.Revision == rev && ho.Pcs.Count == 4 && Same(), "a second spawn into occupied hands is refused without changing objects");

        hn.Host = (c, target) => c.Action == PcAction.Create && !c.Pc.IsNone ? "The carried case has parts." : null;
        Send(l.Host, PcLifecycle.Create(E(ha, ActorSlot.Host), new("kolink-white", "CasePartInstance", 0), new(9)));
        t.Check(l.Host.Revision == rev && Pc(ho, 9) != null && l.HostRejected.Last().Text.Contains("has parts"), "host-native refusal (non-empty carried case) leaves the carried PC unchanged");
        hn.Host = (_, _) => null;
        Send(l.Host, PcLifecycle.Create(E(ha, ActorSlot.Host), new("kolink-white", "CasePartInstance", 0), new(9)));
        t.Check(Pc(ho, 9) == null && new ulong[] { 9, 10, 11 }.All(id => go.Lookup(new(id)) == ObjectLookup.Retired) &&
            Pc(go, 15) is { Location.Actor: ActorSlot.Host } && hn.Calls.Last() == "create 15 kolink-white local=True replaced=9" && Same(),
            "switching the carried empty case retires every old ID once and creates its replacement in one change");

        Send(l.Guest, PcLifecycle.Delete(E(ga, ActorSlot.Guest), new(15), PcRemoval.Carried));
        t.Check(Pc(ho, 15) != null && l.GuestRejected.Last().Text.Contains("not in your hands"), "a guest cannot delete the host's carried PC");
        Send(l.Guest, PcLifecycle.Delete(E(ga, ActorSlot.Guest), new(12), PcRemoval.Carried));
        t.Check(Pc(go, 12) == null && ho.Lookup(new(13)) == ObjectLookup.Retired && gn.Calls.Last() == "delete 12 Carried local=True" && Same(),
            "the guest deletes its carried case; the PC and parts become tombstones on both peers");
        var move = Send(l.Host, ActivityWorld.Reserve(ha.Get(ActorSlot.Host)!, new(5), ActivityOperation.Move), CommandTypes.ActorActivity);
        Send(l.Guest, PcLifecycle.Delete(E(ga, ActorSlot.Guest), new(5), PcRemoval.Stored));
        t.Check(Pc(ho, 5) != null && l.GuestRejected.Last().Text.Contains("in use"), "a PC with a pending operation cannot be deleted from storage");
        Send(l.Host, ActivityWorld.Release(ha.Get(ActorSlot.Host)!, move), CommandTypes.ActorActivity);
        Send(l.Guest, PcLifecycle.Delete(E(ga, ActorSlot.Guest), new(5), PcRemoval.Carried));
        Send(l.Guest, PcLifecycle.Delete(E(ga, ActorSlot.Guest), new(3), PcRemoval.Stored));
        t.Check(Pc(ho, 5) != null && Pc(ho, 3) != null, "removal must name the PC's actual location");
        Send(l.Guest, PcLifecycle.Delete(E(ga, ActorSlot.Guest), new(5), PcRemoval.Stored));
        var afterDelete = l.Host.Revision;
        Send(l.Host, PcLifecycle.Delete(E(ha, ActorSlot.Host), new(5), PcRemoval.Stored));
        t.Check(Pc(go, 5) == null && gn.Calls.Last() == "delete 5 Stored local=True" && l.Host.Revision == afterDelete && Same(),
            "stored PC deletes once; a repeated delete of the retired ID is refused");

        rev = l.Host.Revision;
        Send(l.Guest, PcLifecycle.Create(E(ga, ActorSlot.Guest), new("nope", "CasePartInstance", 0), default));
        Send(l.Guest, PcLifecycle.Create(E(ga, ActorSlot.Guest), new("", "CasePartInstance", 0), default));
        Send(l.Guest, PcLifecycle.Create(E(ga, ActorSlot.Guest), new("kolink", "CasePartInstance", 1), default));
        t.Check(l.Host.Revision == rev, "unknown, empty and out-of-range catalog references are refused");
        var malformed = l.GuestRejected.Count;
        Send(l.Guest, new byte[] { 9, 0, 0, 0, 0, 0, 0, 0, 0 });
        Send(l.Guest, PcLifecycle.Delete(E(ga, ActorSlot.Guest), new(3), PcRemoval.Stored).Concat(new byte[] { 1 }).ToArray());
        var badFrom = PcLifecycle.Delete(E(ga, ActorSlot.Guest), new(3), PcRemoval.Stored); badFrom[^1] = 7;
        Send(l.Guest, badFrom);
        t.Check(l.GuestRejected.Count == malformed + 3 && l.GuestRejected.TakeLast(3).All(x => x.Reason == RejectReason.Malformed),
            "unknown actions, trailing bytes and unknown removal sources receive malformed outcomes");

        var oldEpoch = E(ga, ActorSlot.Guest);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, default), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, guest), CommandTypes.ActorActivity);
        Send(l.Guest, PcLifecycle.Create(oldEpoch, kolink, default));
        t.Check(l.Host.Revision == rev + 2 && l.GuestRejected.Last().Text.Contains("presence"), "a request from an earlier actor epoch cannot create a PC");

        var creates = hn.Calls.Count(c => c.StartsWith("create")); var guestCreates = gn.Calls.Count(c => c.StartsWith("create"));
        l.Guest.Submit(CommandTypes.PcLifecycle, PcLifecycle.Create(E(ga, ActorSlot.Guest), kolink, default), out _);
        var raw = l.ToHost[0]; l.Flush(); FakeLink.Deliver(l.Host, raw); l.Flush();
        t.Check(hn.Calls.Count(c => c.StartsWith("create")) == creates + 1 && gn.Calls.Count(c => c.StartsWith("create")) == guestCreates + 1 && Same(),
            "a duplicated creation request creates and projects exactly once");
        var guestPc = go.Pcs.Single(x => x.Location.Actor == ActorSlot.Guest).Id;

        Send(l.Host, PcLifecycle.Observe(E(ha, ActorSlot.Host), new[] { new PcObservation(new(3), PcPlace.Storage, default), new PcObservation(new(15), PcPlace.Bench, new(2)) }));
        t.Check(Pc(go, 3)!.Location == PcLocation.Storage && Pc(go, 15)!.Location == PcLocation.Bench("B") && Same(),
            "host-observed native moves update both models together, including a swap-like multi-PC change");
        rev = l.Host.Revision;
        Send(l.Guest, PcLifecycle.Observe(E(ga, ActorSlot.Guest), new[] { new PcObservation(new(3), PcPlace.Bench, new(1)) }));
        Send(l.Host, PcLifecycle.Observe(E(ha, ActorSlot.Host), new[] { new PcObservation(guestPc, PcPlace.Storage, default) }));
        Send(l.Host, PcLifecycle.Observe(E(ha, ActorSlot.Host), new[] { new PcObservation(new(3), PcPlace.Bench, new(99)) }));
        Send(l.Host, PcLifecycle.Observe(E(ha, ActorSlot.Host), new[] { new PcObservation(new(3), PcPlace.Bench, new(2)) }));
        Send(l.Host, PcLifecycle.Observe(E(ha, ActorSlot.Host), new[] { new PcObservation(new(3), PcPlace.Carrier, new(1)) }));
        t.Check(l.Host.Revision == rev && Same(), "guests, another actor's carrier, unknown/occupied benches and malformed places cannot be observed");

        l.MarkSnapshot();
        Send(l.Host, PcLifecycle.Create(E(ha, ActorSlot.Host), kolink, default));
        var held = gn.Calls.Count;
        t.Check(l.Guest.Adopt(out _) && gn.Calls.Count == held + 1 && Same(), "a creation after the snapshot mark is held and projected once after admission");

        // A host save observed a native PC outside the change stream (allocated IDs the guest never saw).
        var observedId = ho.NextId;
        ho.Replace(ho.WorldId, observedId + 2, ho.Locations, ho.Pcs.Append(new PcFact(new(observedId),
            new(InstanceOrigin.NativeObserved, host, default), PcLocation.Bench("A"), new[] { new PartFact(new(observedId + 1), "nzxt", "CasePartInstance") },
            new[] { new PartLink("caseID", new(observedId + 1)) })).ToArray(), ho.Retired);
        Send(l.Guest, PcLifecycle.Delete(E(ga, ActorSlot.Guest), guestPc, PcRemoval.Carried));
        Send(l.Guest, PcLifecycle.Create(E(ga, ActorSlot.Guest), kolink, default));
        t.Check(gp.SkippedIds == 2 && go.Lookup(new(observedId)) == ObjectLookup.Retired && Pc(go, observedId + 2) != null && l.Divergences.Count == 0 && l.InStep,
            "a guest records host-only allocations as unresolvable tombstones and keeps identical new IDs");
        Send(l.Host, PcLifecycle.Observe(E(ha, ActorSlot.Host), new[] { new PcObservation(new(observedId), PcPlace.Storage, default) }));
        Send(l.Host, PcLifecycle.Delete(E(ha, ActorSlot.Host), new(observedId), PcRemoval.Stored));
        t.Check(Same(), "host-only PCs can be observed and deleted without a guest divergence; both models converge");

        var fans = new PcGrowth(new[] { new TemplatePart("Case_Fan_Generic_1", "FanPartInstance"), new TemplatePart("Case_Fan_Generic_1", "FanPartInstance") },
            new[] { new GrowthLink("caseFanSlots[0]", 0, default), new GrowthLink("caseFanSlots[5]", 1, default), new GrowthLink("caseID.alias", -1, new(4)) });
        var firstFan = ho.NextId;
        Send(l.Host, PcLifecycle.Extend(E(ha, ActorSlot.Host), new(3), fans));
        t.Check(Pc(go, 3)!.Parts.Select(p => p.Id.Value).SequenceEqual(new ulong[] { 4, firstFan, firstFan + 1 }) &&
            Pc(go, 3)!.Links.Single(x => x.Path == "caseID.alias").Part == new NetObjectId(4) && Same(),
            "native load growth (default case fans) receives the same host-ordered IDs on both peers, aliases included");
        rev = l.Host.Revision;
        Send(l.Guest, PcLifecycle.Extend(E(ga, ActorSlot.Guest), new(3), fans));
        Send(l.Host, PcLifecycle.Extend(E(ha, ActorSlot.Host), new(3), fans));
        Send(l.Host, PcLifecycle.Extend(E(ha, ActorSlot.Host), new(3), new(Array.Empty<TemplatePart>(), new[] { new GrowthLink("x", -1, new(999)) })));
        Send(l.Host, PcLifecycle.Extend(E(ha, ActorSlot.Host), new(3), new(new[] { new TemplatePart("fan", "FanPartInstance") }, new[] { new GrowthLink("y", -1, new(4)) })));
        t.Check(l.Host.Revision == rev && Same(), "growth from a guest, repeated paths, unknown aliases and unlinked parts are refused");

        var saved = l.Host.CapturePosition(); var next = ho.NextId; var live = ho.Pcs.Select(x => x.Id).ToArray();
        l.Host.BeginGeneration("resume"); l.Host.RestoreState(saved.State);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, host), CommandTypes.ActorActivity);
        t.Check(ho.NextId == next && ho.Pcs.Select(x => x.Id).SequenceEqual(live) && ho.Lookup(new(9)) == ObjectLookup.Retired, "saved state restores created PCs, tombstones and allocator");
        var hostCarried = ho.Pcs.SingleOrDefault(x => x.Location.Actor == ActorSlot.Host);
        Send(l.Host, PcLifecycle.Create(E(ha, ActorSlot.Host), kolink, hostCarried?.Id ?? default));
        t.Check(Pc(ho, next) != null && ho.Pcs.All(x => x.Id.Value != 9), "creation after resume never reuses an issued ID");

        // Separate link: a peer that cannot reproduce the template must report divergence, not invent a PC.
        var d = new FakeLink(); var dho = new ObjectWorld(); var dgo = new ObjectWorld();
        var dha = new ActivityWorld(dho); var dga = new ActivityWorld(dgo); var bad = new FakePcNative();
        bad.Templates["kolink"] = new(new[] { new TemplatePart("kolink", "CasePartInstance") }, new[] { new TemplateLink("caseID", 0) });
        d.Host.RegisterState(dho); d.Guest.RegisterState(dgo);
        d.Host.Register(CommandTypes.ActorActivity, dha); d.Guest.Register(CommandTypes.ActorActivity, dga);
        d.Host.RegisterState(dha); d.Guest.RegisterState(dga);
        d.Host.Register(CommandTypes.PcLifecycle, new PcLifecycle(dho, dha, new FakePcNative(), true));
        d.Guest.Register(CommandTypes.PcLifecycle, new PcLifecycle(dgo, dga, bad, false));
        d.Host.BeginGeneration("divergence");
        dho.Replace(Guid.NewGuid(), 2, new[] { new LocationFact(new(1), "A") }, Array.Empty<PcFact>(), Array.Empty<NetObjectId>());
        d.Host.Submit(CommandTypes.ActorActivity, ActivityWorld.Presence(ActorSlot.Host, host), out _); d.Join();
        d.Host.Submit(CommandTypes.PcLifecycle, PcLifecycle.Create(dha.Get(ActorSlot.Host)!.Epoch, kolink, default), out _); d.Flush();
        t.Check(d.Divergences.Count == 1 && dgo.Pcs.Count == 0, "a different native template topology on the receiving peer diverges explicitly");
    }
}

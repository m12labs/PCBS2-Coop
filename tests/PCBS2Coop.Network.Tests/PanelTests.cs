using LiteNetLib.Utils;
using PCBS2Coop.Network;

/// <summary>Stands in for the native panel adapter: a scripted host check and a log of shown changes.</summary>
internal sealed class FakePanelNative : IPanelNative
{
    public Func<PanelCommand, string?> Host = _ => null;
    public readonly List<string> Calls = new();
    public string? CheckHost(in CommandContext ctx, PcFact pc, AssemblyResource slot, PanelCommand c) => Host(c);
    public void Changed(in MutationContext ctx, PcFact pc, AssemblyResource slot, PanelCommand c) =>
        Calls.Add($"{slot.Id.Value}={(c.Remove ? "off" : c.Part)} local={ctx.FromLocalPlayer}");
}

internal static class PanelTests
{
    private static NetObjectId Id(ulong n) => new(n);
    private static AssemblyResource Slot(ulong id, ulong parent, string path, ulong occupant = 0) =>
        new(Id(id), Id(parent), path, "Slot", AssemblyResourceKind.Slot, Id(occupant), false, 0, 0, Array.Empty<NetObjectId>());

    /// <summary>PC 2 on bench A. Case 3 has a side panel in slot 10 carrying two screws (11, 12), a hinge latch (13)
    /// and a fan mount (14). The RAM-side slot 15 is guarded by the panel; slot 16 is a PCI cover (integral, no home);
    /// the front panel 17 is fitted. Board 4 sits in slot 8.</summary>
    private static PcFact Fixture(PlayerId creator) => new(Id(2), new(InstanceOrigin.Baseline, creator, default), PcLocation.Bench("A"),
        new[] { new PartFact(Id(3), "case", "CasePartInstance"), new PartFact(Id(4), "board", "MotherboardPartInstance"), new PartFact(Id(5), "fan", "PartInstance") },
        new[] { new PartLink("caseID", Id(3)), new PartLink("motherboardID", Id(4)), new PartLink("caseFanSlots[0]", Id(5)) })
    {
        Resources = new[]
        {
            Slot(8, 3, "Slot:Slots/Slot_Motherboard#0", 4),
            Slot(10, 3, "Slot:Slots/Slot_LeftLid#0") with { IntegralPart = "case/LeftLid", IntegralHome = "case/LeftLid",
                Carries = new[] { Id(11), Id(12), Id(13), Id(14) }, Coupled = new[] { Id(11), Id(12) } },
            new AssemblyResource(Id(11), Id(3), "Screw:Slots/Slot_LeftLid/LeftLid/Screw#0", "Screw", AssemblyResourceKind.Screw, default, false, 0, 0, Array.Empty<NetObjectId>()),
            new AssemblyResource(Id(12), Id(3), "Screw:Slots/Slot_LeftLid/LeftLid/Screw1#0", "Screw", AssemblyResourceKind.Screw, default, false, 0, 0, Array.Empty<NetObjectId>()),
            new AssemblyResource(Id(13), Id(3), "Pin:Slots/Slot_LeftLid/LeftLid/Hinge#0", "Pin", AssemblyResourceKind.Pin, default, false, 0, 0, Array.Empty<NetObjectId>()),
            Slot(14, 3, "Slot:Slots/Slot_LeftLid/LeftLid/FanMount#0"),
            Slot(15, 4, "Slot:RAM[0]#0") with { Dependencies = new[] { Id(10) } },
            Slot(16, 3, "Slot:PCI Covers/PCI_Cover#0") with { IntegralPart = "case/PCI_Shield" },
            Slot(17, 3, "Slot:Slots/Slot_Front#0") with { IntegralPart = "case/Front", IntegralHome = "case/Front" },
            Slot(18, 3, "Slot:Slots/Slot_Fan#0", 5),
        }
    };

    private static bool Throws(Action a) { try { a(); return false; } catch (InvalidDataException) { return true; } }

    public static void Run(Checker t)
    {
        t.Section("Case panels: host-ordered removal and refit");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hn = new FakePanelNative(); var gn = new FakePanelNative();
        l.Host.RegisterState(ho); l.Guest.RegisterState(go);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.Register(CommandTypes.CasePanel, new CasePanelWorld(ho, ha, hn)); l.Guest.Register(CommandTypes.CasePanel, new CasePanelWorld(go, ga, gn));
        l.Host.Register(CommandTypes.Mechanism, new MechanismWorld(ho, ha, new FakeMechanismNative(), true));
        l.Guest.Register(CommandTypes.Mechanism, new MechanismWorld(go, ga, new FakeMechanismNative(), false));
        l.Host.BeginGeneration("panels");
        var host = PlayerId.New(); var guest = PlayerId.New();
        ho.Replace(Guid.NewGuid(), 19, new[] { new LocationFact(Id(1), "A") }, new[] { Fixture(host) }, new[] { Id(6), Id(7), Id(9) });
        OperationId Send(WorldSync w, byte[] bytes, ushort type = CommandTypes.CasePanel) { w.Submit(type, bytes, out var op); l.Flush(); return op; }
        ActorActivity H() => ha.Get(ActorSlot.Host)!; ActorActivity G() => ga.Get(ActorSlot.Guest)!;
        PcFact P(ObjectWorld o) => o.Pcs.Single();
        AssemblyResource R(ObjectWorld o, ulong id) => P(o).Resources.Single(r => r.Id == Id(id));
        byte[] Remove(ActorActivity a, ulong slot, ObjectWorld o) => CasePanelWorld.Remove(a.Epoch, Id(2), Id(slot), R(o, slot).Version);
        byte[] Refit(ActorActivity a, ulong slot, ObjectWorld o, string sku) => CasePanelWorld.Refit(a.Epoch, Id(2), Id(slot), R(o, slot).Version, sku);
        bool Same() => ho.ToBytes().SequenceEqual(go.ToBytes()) && l.InStep && l.Divergences.Count == 0;
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, host), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, guest), CommandTypes.ActorActivity);
        l.Join();
        t.Check(Same() && AssemblyResources.Dormant(P(go)).Count == 0, "a joining guest receives the panels and what they carry, all fitted");

        var rev = l.Host.Revision;
        Send(l.Guest, Remove(G(), 10, go));
        t.Check(l.Host.Revision == rev && l.GuestRejected.Last().Text.Contains("not working"), "a player not working on that PC can't remove its panels");
        Send(l.Host, ActivityWorld.Work(H(), Id(2)), CommandTypes.ActorActivity);
        Send(l.Guest, ActivityWorld.Work(G(), Id(2)), CommandTypes.ActorActivity);

        rev = l.Host.Revision;
        Send(l.Guest, Remove(G(), 8, go));
        Send(l.Guest, Remove(G(), 16, go));
        Send(l.Guest, Remove(G(), 11, go));
        t.Check(l.Host.Revision == rev && l.GuestRejected.TakeLast(3).All(x => x.Text.Contains("isn't a case panel")),
            "a component slot, a PCI cover and a screw are not case panels");

        Send(l.Guest, Remove(G(), 10, go));
        t.Check(R(go, 10).IntegralPart == "" && R(go, 10).IntegralHome == "case/LeftLid" && R(go, 10).Version == 1 &&
            AssemblyResources.Dormant(P(go)).SetEquals(new[] { Id(11), Id(12), Id(13), Id(14) }) && R(go, 13).Version == 1 &&
            gn.Calls.Last() == "10=off local=True" && hn.Calls.Last() == "10=off local=False" && Same(),
            "a guest removes a side panel: both peers keep its screws, hinge and fan mount dormant with their IDs, and both games show it once");
        rev = l.Host.Revision;
        Send(l.Host, Remove(H(), 10, ho));
        Send(l.Host, MechanismWorld.Toggle(H().Epoch, Id(2), Id(13), R(ho, 13).Version, true), CommandTypes.Mechanism);
        t.Check(l.Host.Revision == rev && l.HostRejected.TakeLast(2).Select(x => x.Text).ToArray() is var why &&
            why[0].Contains("just changed") && why[1].Contains("removed panel"),
            "removing it again, or toggling a latch on the removed panel, is refused");

        rev = l.Host.Revision;
        Send(l.Guest, Refit(G(), 10, go, "case/Front"));
        Send(l.Guest, Refit(G(), 17, go, "case/Front"));
        t.Check(l.Host.Revision == rev && l.GuestRejected.TakeLast(2).Select(x => x.Text).ToArray() is var refits &&
            refits[0].Contains("doesn't belong") && refits[1].Contains("just changed"), "another panel's SKU, or refitting a panel that is on, is refused");
        l.Guest.Submit(CommandTypes.CasePanel, Refit(G(), 10, go, "case/LeftLid"), out _);
        l.Host.Submit(CommandTypes.CasePanel, Refit(H(), 10, ho, "case/LeftLid"), out _);
        l.Flush();
        t.Check(l.Host.Revision == rev + 1 && R(go, 10).IntegralPart == "case/LeftLid" && AssemblyResources.Dormant(P(go)).Count == 0 &&
            R(go, 11).Version == 2 && l.GuestRejected.Last().Text.Contains("just changed") && Same(),
            "two refits of one panel: the first lands, restoring the same carried IDs, and the other is refused");

        // The other player's edit reading the panel (a slot behind it) holds it; disjoint panels stay free.
        var claim = Send(l.Guest, ActivityWorld.Reserve(G(), Id(2), ActivityOperation.Edit, Id(15)), CommandTypes.ActorActivity);
        rev = l.Host.Revision;
        Send(l.Host, Remove(H(), 10, ho));
        t.Check(l.Host.Revision == rev && l.HostRejected.Last().Text.Contains("using that part"), "a panel guarding the other player's edit can't be removed");
        Send(l.Host, Remove(H(), 17, ho));
        t.Check(l.Host.Revision == rev + 1 && R(go, 17).IntegralPart == "" && Same(), "a different panel on the same PC can be removed meanwhile");
        Send(l.Guest, ActivityWorld.Release(G(), claim), CommandTypes.ActorActivity);

        var mounted = Fixture(host); var withFan = mounted with { Resources = mounted.Resources.Select(r => r.Id == Id(14) ? r with { Occupant = Id(5) } :
            r.Id == Id(18) ? r with { Occupant = default } : r).ToArray() };
        var fanModel = new ObjectWorld(); var fanActivity = new ActivityWorld(fanModel);
        fanModel.Replace(Guid.NewGuid(), 19, new[] { new LocationFact(Id(1), "A") }, new[] { withFan }, new[] { Id(6), Id(7), Id(9) });
        var fanWorld = new CasePanelWorld(fanModel, fanActivity, new FakePanelNative());
        fanActivity.Apply(new(new(ActorSlot.Host, 1), 1, 1, true), new NetDataReader(ActivityWorld.Presence(ActorSlot.Host, host).Concat(BitConverter.GetBytes(1UL)).ToArray()));
        fanActivity.Apply(new(new(ActorSlot.Host, 2), 1, 2, true), new NetDataReader(ActivityWorld.Work(fanActivity.Get(ActorSlot.Host)!, Id(2))));
        t.Check(fanWorld.Validate(new(new(ActorSlot.Host, 3), 1, 3), new NetDataReader(CasePanelWorld.Remove(fanActivity.Get(ActorSlot.Host)!.Epoch, Id(2), Id(10), 0))) is { Accepted: false } v &&
            v.Reason.Contains("mounted on that panel"), "a panel with a fan mounted on it can't be removed");

        rev = l.Host.Revision;
        hn.Host = _ => "Something is in the way of that panel.";
        Send(l.Guest, Remove(G(), 10, go));
        t.Check(l.Host.Revision == rev && R(ho, 10).IntegralPart != "" && l.GuestRejected.Last().Text.Contains("in the way"), "a host-native refusal leaves the panel fitted");
        hn.Host = _ => null;

        rev = l.Host.Revision; var malformed = l.GuestRejected.Count;
        Send(l.Guest, new byte[] { 9 });
        Send(l.Guest, Remove(G(), 10, go).Concat(new byte[] { 1 }).ToArray());
        t.Check(l.Host.Revision == rev && l.GuestRejected.Count == malformed + 2 && l.GuestRejected.TakeLast(2).All(x => x.Reason == RejectReason.Malformed),
            "unknown actions and trailing bytes are malformed");

        var calls = gn.Calls.Count;
        l.Guest.Submit(CommandTypes.CasePanel, Remove(G(), 10, go), out _);
        var raw = l.ToHost[0]; l.Flush(); FakeLink.Deliver(l.Host, raw); l.Flush();
        t.Check(gn.Calls.Count == calls + 1 && R(go, 10).IntegralPart == "" && Same(), "a duplicated removal applies and is shown exactly once");
        l.MarkSnapshot();
        Send(l.Host, Refit(H(), 10, ho, "case/LeftLid"));
        calls = gn.Calls.Count;
        t.Check(l.Guest.Adopt(out _) && gn.Calls.Count == calls + 1 && R(go, 10).IntegralPart == "case/LeftLid" && Same(),
            "a refit after the snapshot mark is held and shown once after admission");

        t.Section("Case panels: state, repair and validation");
        Send(l.Guest, Remove(G(), 10, go));
        var copy = new ObjectWorld(); copy.Read(new NetDataReader(ho.ToBytes()));
        t.Check(copy.ToBytes().SequenceEqual(ho.ToBytes()) && AssemblyResources.Dormant(P(copy)).Count == 4, "a removed panel's home and dormant resources round-trip exactly");
        // /sync repair from a native scan that shows the panel off: no carried rows exist natively.
        var pc = P(ho); var dormant = AssemblyResources.Dormant(pc);
        var rows = pc.Resources.Where(r => !dormant.Contains(r.Id)).ToArray();
        var index = rows.Select((r, i) => (r.Id, i)).ToDictionary(x => x.Id, x => x.i);
        int[] Map(IEnumerable<NetObjectId> ids) => ids.Where(index.ContainsKey).Select(id => index[id]).ToArray();
        var templates = rows.Select(r => new AssemblyTemplate(r.Parent, r.Binding, r.NativeType, r.Kind, r.Occupant, r.Open, r.Rotation, r.Offset, Map(r.Dependencies), Map(r.Coupled))
            { IntegralPart = r.IntegralPart, LoadCloses = Map(r.LoadCloses), IntegralHome = r.IntegralPart != "" ? r.IntegralHome : "", Carries = r.IntegralPart != "" ? Map(r.Carries) : Array.Empty<int>() }).ToArray();
        var allocator = new NetObjectIdAllocator { Next = ho.NextId };
        var repaired = AssemblyManifest.Repair(pc, templates, allocator);
        t.Check(allocator.Next == ho.NextId && repaired.Resources.Select(r => r.Id).OrderBy(x => x.Value).SequenceEqual(pc.Resources.Select(r => r.Id).OrderBy(x => x.Value)) &&
            AssemblyResources.Dormant(repaired).SetEquals(dormant) && repaired.Resources.Single(r => r.Id == Id(10)).IntegralHome == "case/LeftLid",
            "a /sync repair of a PC with a panel off keeps the panel's home and dormant IDs");

        void Bad(Func<AssemblyResource, AssemblyResource> edit, ulong id, string message)
        {
            var row = Fixture(host); var m = new ObjectWorld();
            var bad = row with { Resources = row.Resources.Select(r => r.Id == Id(id) ? edit(r) : r).ToArray() };
            t.Check(Throws(() => m.Replace(Guid.NewGuid(), 19, new[] { new LocationFact(Id(1), "A") }, new[] { bad }, new[] { Id(6), Id(7), Id(9) })), message);
        }
        Bad(r => r with { IntegralHome = "case/Other" }, 10, "a fitted panel must be its slot's home SKU");
        Bad(r => r with { IntegralHome = "case/X" }, 11, "only slots have a panel home");
        Bad(r => r with { Carries = new[] { Id(15) } }, 16, "a slot without a panel home carries nothing");
        Bad(r => r with { Carries = new[] { Id(99) } }, 10, "an unknown carried resource is refused");
        Bad(r => r with { Carries = new[] { Id(10) } }, 10, "a panel can't carry itself");
        var live = P(ho); var changed = live with { Resources = live.Resources.Select(r => r.Id == Id(17) ? r with { IntegralHome = "case/Other2" } : r).ToArray() };
        t.Check(Throws(() => ho.Replace(ho.WorldId, ho.NextId, ho.Locations, new[] { changed }, ho.Retired)), "changing a panel's home requires a version advance");
    }
}

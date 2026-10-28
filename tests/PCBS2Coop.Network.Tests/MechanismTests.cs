using LiteNetLib.Utils;
using PCBS2Coop.Network;

/// <summary>Stands in for the native latch adapter: a scripted host check and a log of shown changes.</summary>
internal sealed class FakeMechanismNative : IMechanismNative
{
    public Func<AssemblyResource, bool, string?> Host = (_, _) => null;
    public readonly List<string> Calls = new();
    public string? CheckHost(in CommandContext ctx, PcFact pc, AssemblyResource latch, bool open) => Host(latch, open);
    public void Changed(in MutationContext ctx, PcFact pc, IReadOnlyList<AssemblyResource> changed, bool settle) =>
        Calls.Add($"{string.Join(",", changed.Select(r => $"{r.Id.Value}={(r.Open ? "open" : "closed")}"))} local={ctx.FromLocalPlayer} settle={settle}");
}

internal static class MechanismTests
{
    private static NetObjectId Id(ulong n) => new(n);
    private static AssemblyResource Slot(ulong id, ulong parent, string path, ulong occupant = 0) =>
        new(Id(id), Id(parent), path, "Slot", AssemblyResourceKind.Slot, Id(occupant), false, 0, 0, Array.Empty<NetObjectId>());
    private static AssemblyResource Latch(ulong id, ulong parent, string path, bool open = false, AssemblyResourceKind kind = AssemblyResourceKind.Pin) =>
        new(Id(id), Id(parent), path, kind == AssemblyResourceKind.Pin ? "Pin" : "AM3Slot", kind, default, open, 0, 0, Array.Empty<NetObjectId>());

    /// <summary>PC 2 on bench A: board 4 holds one RAM stick (5) in RAM[0] and has an empty RAM[1]. Their clips close
    /// on load while the slot is occupied (12, 13 and 14). Also an M.2 cover guarding its slot (15), a CPU socket (16)
    /// and a linked latch (17). PC 18 is on bench B.</summary>
    private static (LocationFact[], PcFact[]) Fixture(PlayerId creator)
    {
        var origin = new InstanceProvenance(InstanceOrigin.Baseline, creator, default);
        var pc = new PcFact(Id(2), origin, PcLocation.Bench("A"),
            new[] { new PartFact(Id(3), "case", "CasePartInstance"), new PartFact(Id(4), "board", "MotherboardPartInstance"), new PartFact(Id(5), "ram", "RamPartInstance") },
            new[] { new PartLink("caseID", Id(3)), new PartLink("motherboardID", Id(4)), new PartLink("ramSlots[0]", Id(5)) })
        {
            Resources = new[]
            {
                Slot(8, 3, "motherboard", 4),
                Slot(9, 4, "RAM[0]", 5) with { Coupled = new[] { Id(12), Id(13) }, LoadCloses = new[] { Id(12), Id(13) } },
                Slot(10, 4, "RAM[1]") with { Coupled = new[] { Id(14) }, LoadCloses = new[] { Id(14) } },
                Slot(11, 4, "M2[0]") with { Dependencies = new[] { Id(15) } },
                Latch(12, 4, "RAM[0]/clip[0]", open: true), Latch(13, 4, "RAM[0]/clip[1]"), Latch(14, 4, "RAM[1]/clip[0]", open: true),
                Latch(15, 4, "M2[0]/cover"), Latch(16, 4, "socket", kind: AssemblyResourceKind.Socket),
                Latch(17, 3, "linked") with { Coupled = new[] { Id(15) } },
            }
        };
        var other = new PcFact(Id(18), origin, PcLocation.Bench("B"), new[] { new PartFact(Id(19), "case2", "CasePartInstance") }, new[] { new PartLink("caseID", Id(19)) });
        return (new[] { new LocationFact(Id(1), "A"), new LocationFact(Id(20), "B") }, new[] { pc, other });
    }

    private static bool Throws(Action a) { try { a(); return false; } catch (InvalidDataException) { return true; } }

    public static void Run(Checker t)
    {
        t.Section("Latches: host-ordered toggles and the load settle");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hn = new FakeMechanismNative(); var gn = new FakeMechanismNative();
        l.Host.RegisterState(ho); l.Guest.RegisterState(go);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.Register(CommandTypes.Mechanism, new MechanismWorld(ho, ha, hn, true));
        l.Guest.Register(CommandTypes.Mechanism, new MechanismWorld(go, ga, gn, false));
        l.Host.Register(CommandTypes.PcTransfer, new PcTransfers(ho, ha, new FakeTransferNative(), true));
        l.Guest.Register(CommandTypes.PcTransfer, new PcTransfers(go, ga, new FakeTransferNative(), false));
        l.Host.BeginGeneration("latches");
        var host = PlayerId.New(); var guest = PlayerId.New();
        var (locations, pcs) = Fixture(host);
        ho.Replace(Guid.NewGuid(), 21, locations, pcs, new[] { Id(6), Id(7) });
        OperationId Send(WorldSync w, byte[] bytes, ushort type = CommandTypes.Mechanism) { w.Submit(type, bytes, out var op); l.Flush(); return op; }
        ActorActivity H() => ha.Get(ActorSlot.Host)!; ActorActivity G() => ga.Get(ActorSlot.Guest)!;
        AssemblyResource R(ObjectWorld o, ulong id) => o.Pcs.Single(p => p.Id == Id(2)).Resources.Single(r => r.Id == Id(id));
        byte[] Toggle(ActorActivity a, ulong latch, ObjectWorld o) => MechanismWorld.Toggle(a.Epoch, Id(2), Id(latch), R(o, latch).Version, !R(o, latch).Open);
        bool Same() => ho.ToBytes().SequenceEqual(go.ToBytes()) && l.InStep && l.Divergences.Count == 0;
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, host), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, guest), CommandTypes.ActorActivity);
        l.Join();
        t.Check(Same() && R(go, 12).LoadCloses.Count == 0 && go.Pcs.Single(p => p.Id == Id(2)).Resources.Single(r => r.Id == Id(9)).LoadCloses.Count == 2,
            "a joining guest receives every latch and the clips a load closes over each slot");

        var rev = l.Host.Revision;
        Send(l.Guest, Toggle(G(), 15, go));
        t.Check(l.Host.Revision == rev && l.GuestRejected.Last().Text.Contains("not working"), "a player not working on that PC can't toggle its latches");
        Send(l.Host, ActivityWorld.Work(H(), Id(2)), CommandTypes.ActorActivity);
        Send(l.Guest, ActivityWorld.Work(G(), Id(2)), CommandTypes.ActorActivity);

        Send(l.Guest, Toggle(G(), 15, go));
        t.Check(R(ho, 15).Open && R(ho, 15).Version == 1 && gn.Calls.Last() == "15=open local=True settle=False" &&
            hn.Calls.Last() == "15=open local=False settle=False" && Same(),
            "a guest opens an M.2 cover: both peers record it once and both games show it");

        rev = l.Host.Revision;
        l.Guest.Submit(CommandTypes.Mechanism, Toggle(G(), 16, go), out _);
        l.Host.Submit(CommandTypes.Mechanism, Toggle(H(), 16, ho), out _);
        l.Flush();
        t.Check(l.Host.Revision == rev + 1 && R(go, 16).Open && R(go, 16).Version == 1 && l.GuestRejected.Last().Text.Contains("just changed") && Same(),
            "two players toggling one CPU lever: the first lands, the other is refused instead of toggling it back");

        rev = l.Host.Revision;
        l.Guest.Submit(CommandTypes.Mechanism, Toggle(G(), 13, go), out _);
        l.Host.Submit(CommandTypes.Mechanism, Toggle(H(), 16, ho), out _);
        l.Flush();
        t.Check(l.Host.Revision == rev + 2 && R(go, 13).Open && !R(go, 16).Open && Same(), "different latches on one PC change independently");

        rev = l.Host.Revision;
        Send(l.Guest, Toggle(G(), 17, go));
        Send(l.Guest, MechanismWorld.Toggle(G().Epoch, Id(2), Id(11), 0, true));
        Send(l.Guest, MechanismWorld.Toggle(G().Epoch, Id(18), Id(15), R(go, 15).Version, false));
        Send(l.Guest, MechanismWorld.Toggle(G().Epoch + 9, Id(2), Id(15), R(go, 15).Version, false));
        t.Check(l.Host.Revision == rev && l.GuestRejected.TakeLast(4).Select(x => x.Text).ToArray() is var why &&
            why[0].Contains("Linked") && why[1].Contains("Unknown latch") && why[2].Contains("not working") && why[3].Contains("presence"),
            "a linked latch, a slot named as a latch, a latch on another PC and a stale epoch are refused");

        // The other player's edit holding a RAM slot: the other player waits on that whole PC (owner decision, 4e);
        // the claim holder may still use the clips it holds.
        var claim = Send(l.Guest, ActivityWorld.Reserve(G(), Id(2), ActivityOperation.Edit, Id(10)), CommandTypes.ActorActivity);
        rev = l.Host.Revision;
        Send(l.Host, Toggle(H(), 14, ho));
        t.Check(l.Host.Revision == rev && l.HostRejected.Last().Text.Contains("changing a part of that PC"), "a latch inside the other player's edit claim is refused");
        Send(l.Host, Toggle(H(), 15, ho));
        t.Check(l.Host.Revision == rev && l.HostRejected.Last().Text.Contains("changing a part of that PC"), "so is a latch elsewhere on that PC");
        Send(l.Guest, Toggle(G(), 14, go));
        t.Check(l.Host.Revision == rev + 1 && !R(go, 14).Open && Same(), "the claim holder can toggle the latches it holds");
        Send(l.Guest, ActivityWorld.Release(G(), claim), CommandTypes.ActorActivity);
        Send(l.Host, Toggle(H(), 15, ho));
        t.Check(l.Host.Revision == rev + 3 && !R(go, 15).Open && Same(), "after the release the other latch is available again");

        rev = l.Host.Revision;
        hn.Host = (_, _) => "Something is blocking that latch.";
        Send(l.Guest, Toggle(G(), 15, go));
        t.Check(l.Host.Revision == rev && !R(ho, 15).Open && l.GuestRejected.Last().Text.Contains("blocking"), "a latch blocked on the host's game is refused and stays as it was");
        hn.Host = (_, _) => null;

        rev = l.Host.Revision; var malformed = l.GuestRejected.Count;
        Send(l.Guest, new byte[] { 9, 0 });
        Send(l.Guest, Toggle(G(), 15, go).Concat(new byte[] { 1 }).ToArray());
        t.Check(l.Host.Revision == rev && l.GuestRejected.Count == malformed + 2 && l.GuestRejected.TakeLast(2).All(x => x.Reason == RejectReason.Malformed),
            "unknown actions and trailing bytes are malformed");

        var calls = gn.Calls.Count;
        l.Guest.Submit(CommandTypes.Mechanism, Toggle(G(), 15, go), out _);
        var raw = l.ToHost[0]; l.Flush(); FakeLink.Deliver(l.Host, raw); l.Flush();
        t.Check(gn.Calls.Count == calls + 1 && R(go, 15).Open && Same(), "a duplicated toggle request applies and is shown exactly once");

        l.MarkSnapshot();
        Send(l.Host, Toggle(H(), 15, ho));
        calls = gn.Calls.Count;
        t.Check(l.Guest.Adopt(out _) && gn.Calls.Count == calls + 1 && !R(go, 15).Open && Same(),
            "a toggle after the snapshot mark is held and shown once after admission");

        t.Section("Latches: clips over installed parts close at saves, snapshots and moves (owner decision)");
        Send(l.Guest, Toggle(G(), 14, go)); // the empty slot's clip: open, and stays open
        t.Check(R(ho, 12).Open && R(ho, 13).Open && R(ho, 14).Open && MechanismWorld.Unsettled(ho).Single().Id == Id(2) &&
            AssemblyResources.LoadClosed(ho.Pcs.Single(p => p.Id == Id(2))).SequenceEqual(new[] { Id(12), Id(13) }),
            "only clips over an occupied RAM slot count as unsettled");
        rev = l.Host.Revision;
        Send(l.Guest, MechanismWorld.Settle(MechanismWorld.Unsettled(go)));
        t.Check(l.Host.Revision == rev && l.GuestRejected.Last().Text.Contains("Only the host"), "only the host settles latches");
        var claimed = Send(l.Guest, ActivityWorld.Reserve(G(), Id(2), ActivityOperation.Edit, Id(9)), CommandTypes.ActorActivity);
        rev = l.Host.Revision;
        Send(l.Host, MechanismWorld.Settle(MechanismWorld.Unsettled(ho)));
        t.Check(l.Host.Revision == rev && R(ho, 12).Open && l.HostRejected.Last().Text.Contains("in use"), "a settle waits while the other player's edit holds that slot");
        Send(l.Guest, ActivityWorld.Release(G(), claimed), CommandTypes.ActorActivity);
        var settle = MechanismWorld.Settle(MechanismWorld.Unsettled(ho));
        var v12 = R(ho, 12).Version;
        Send(l.Host, settle);
        t.Check(!R(go, 12).Open && !R(go, 13).Open && R(go, 14).Open && R(go, 12).Version == v12 + 1 &&
            gn.Calls.Last() == "12=closed,13=closed local=False settle=True" && hn.Calls.Last() == "12=closed,13=closed local=True settle=True" &&
            !MechanismWorld.Unsettled(go).Any() && Same(),
            "the host's settle closes clips over installed sticks on both games and leaves the empty slot's clip open");
        rev = l.Host.Revision;
        Send(l.Host, settle);
        t.Check(l.Host.Revision == rev && l.HostRejected.Last().Text.Contains("just changed"), "a repeated or stale settle is refused");

        Send(l.Guest, Toggle(G(), 12, go));
        Send(l.Host, ActivityWorld.Work(H(), default), CommandTypes.ActorActivity);
        Send(l.Guest, ActivityWorld.Work(G(), default), CommandTypes.ActorActivity);
        var v = R(ho, 12).Version;
        Send(l.Guest, PcTransfers.Pickup(G().Epoch, Id(2), Id(1)), CommandTypes.PcTransfer);
        var moved = go.Pcs.Single(p => p.Id == Id(2));
        t.Check(moved.Location.Place == PcPlace.Carrier && !R(go, 12).Open && R(go, 12).Version == v + 1 && R(go, 14).Open && Same(),
            "picking a PC up settles its clips over installed sticks, since its next load closes them");
        rev = l.Host.Revision;
        Send(l.Guest, MechanismWorld.Toggle(G().Epoch, Id(2), Id(15), R(go, 15).Version, true));
        t.Check(l.Host.Revision == rev && l.GuestRejected.Last().Text.Contains("no longer on a bench"), "a carried PC's latches can't be toggled");

        t.Section("Latches: load-closed lists in object state");
        var copy = new ObjectWorld(); copy.Read(new NetDataReader(ho.ToBytes()));
        t.Check(copy.ToBytes().SequenceEqual(ho.ToBytes()) && copy.Pcs.Single(p => p.Id == Id(2)).Resources.Single(r => r.Id == Id(9)).LoadCloses.SequenceEqual(new[] { Id(12), Id(13) }),
            "load-closed latch lists round-trip exactly");
        void Bad(Func<AssemblyResource, AssemblyResource> edit, ulong id, string message)
        {
            var (loc, rows) = Fixture(host); var m = new ObjectWorld();
            var pc = rows[0] with { Resources = rows[0].Resources.Select(r => r.Id == Id(id) ? edit(r) : r).ToArray() };
            t.Check(Throws(() => m.Replace(Guid.NewGuid(), 21, loc, new[] { pc, rows[1] }, new[] { Id(6), Id(7) })), message);
        }
        Bad(r => r with { LoadCloses = new[] { Id(13) } }, 12, "only a slot lists the latches its load closes");
        Bad(r => r with { LoadCloses = new[] { Id(11) } }, 9, "a load-closed entry must be a latch, not a slot");
        Bad(r => r with { LoadCloses = new[] { Id(12), Id(12) } }, 9, "duplicate load-closed latches are refused");
        Bad(r => r with { LoadCloses = new[] { Id(99) } }, 9, "an unknown load-closed latch is refused");
        var same = ho.Pcs.Single(p => p.Id == Id(2));
        var edited = same with { Resources = same.Resources.Select(r => r.Id == Id(10) ? r with { LoadCloses = Array.Empty<NetObjectId>() } : r).ToArray() };
        t.Check(Throws(() => ho.Replace(ho.WorldId, ho.NextId, ho.Locations, ho.Pcs.Select(p => p.Id == Id(2) ? edited : p), ho.Retired)),
            "changing a slot's load-closed latches requires a resource version advance");
    }
}

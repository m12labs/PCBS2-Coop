using LiteNetLib.Utils;
using PCBS2Coop.Network;

/// <summary>Stands in for the native peripheral adapter: catalog resolution by part prefix, workstation benches, a
/// scripted host check and a log of projected swaps.</summary>
internal sealed class FakePeripheralNative : IPeripheralNative
{
    public Func<PeripheralCommand, string?> Host = _ => null;
    public string Prefix = "Prefabs/";
    public readonly List<string> Calls = new();
    private static readonly Dictionary<int, string> Kinds = new() { [10] = "MON", [11] = "KEY" };
    private static readonly Dictionary<int, string[]> Benches = new() { [2] = new[] { "A", "T2" }, [3] = new[] { "B" } };
    public string? Resolve(int bench, int slot, string part) => Kinds.TryGetValue(slot, out var kind) && part.StartsWith(kind) ? Prefix + part : null;
    public IReadOnlyCollection<string> BenchKeys(int bench) => Benches.GetValueOrDefault(bench, Array.Empty<string>());
    public string? CheckHost(in CommandContext ctx, PeripheralCommand c) => Host(c);
    public void Swapped(in MutationContext ctx, PeripheralCommand c, string path) => Calls.Add($"{c.Bench}/{c.Slot}={path} local={ctx.FromLocalPlayer}");
}

internal static class WorkstationTests
{
    public static void Run(Checker t)
    {
        t.Section("Workstations: host-ordered peripheral swaps");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hn = new FakePeripheralNative(); var gn = new FakePeripheralNative();
        var hw = new WorkstationWorld(ho, ha, hn); var gw = new WorkstationWorld(go, ga, gn);
        l.Host.RegisterState(ho); l.Guest.RegisterState(go);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.Register(CommandTypes.PeripheralSwap, hw); l.Guest.Register(CommandTypes.PeripheralSwap, gw);
        l.Host.RegisterState(hw, optional: true); l.Guest.RegisterState(gw, optional: true);
        l.Host.BeginGeneration("workstations");
        var host = PlayerId.New(); var guest = PlayerId.New();
        var origin = new InstanceProvenance(InstanceOrigin.Baseline, host, default);
        PcFact Fact(ulong id, PcLocation at) => new(new(id), origin, at, new[] { new PartFact(new(id + 1), "case" + id, "CasePartInstance") }, new[] { new PartLink("caseID", new(id + 1)) });
        ho.Replace(Guid.NewGuid(), 8, new[] { new LocationFact(new(1), "A"), new LocationFact(new(2), "B"), new LocationFact(new(3), "T2") },
            new[] { Fact(4, PcLocation.Bench("A")), Fact(6, PcLocation.Bench("B")) }, Array.Empty<NetObjectId>());
        hw.Replace(new[] { new PeripheralFact(2, 10, "Prefabs/MON-a"), new PeripheralFact(2, 11, "Prefabs/KEY-a"), new PeripheralFact(3, 10, "Prefabs/MON-a") });
        OperationId Send(WorldSync w, byte[] bytes, ushort type = CommandTypes.PeripheralSwap) { w.Submit(type, bytes, out var op); l.Flush(); return op; }
        ulong E(ActivityWorld a, ActorSlot s) => a.Get(s)!.Epoch;
        byte[] Bytes(IWorldState s) { var w = new NetDataWriter(); s.Write(w); return w.CopyData(); }
        bool Same() => Bytes(hw).SequenceEqual(Bytes(gw)) && l.InStep && l.Divergences.Count == 0;
        string At(WorkstationWorld w, int bench, int slot) => w.Get(bench, slot)!.Path;
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, host), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, guest), CommandTypes.ActorActivity);
        l.Join();
        t.Check(Same() && gw.Slots.Count == 3, "a joining guest receives every workstation's peripheral bindings");

        Send(l.Guest, WorkstationWorld.Swap(E(ga, ActorSlot.Guest), 2, 10, "Prefabs/MON-a", "MON-b"));
        t.Check(At(hw, 2, 10) == "Prefabs/MON-b" && gn.Calls.Last() == "2/10=Prefabs/MON-b local=True" && hn.Calls.Last() == "2/10=Prefabs/MON-b local=False" && Same(),
            "a guest monitor swap changes that slot on both peers; the requester runs the native swap, the other game projects it");
        var rev = l.Host.Revision;
        l.Guest.Submit(CommandTypes.PeripheralSwap, WorkstationWorld.Swap(E(ga, ActorSlot.Guest), 2, 11, "Prefabs/KEY-a", "KEY-g"), out _);
        l.Host.Submit(CommandTypes.PeripheralSwap, WorkstationWorld.Swap(E(ha, ActorSlot.Host), 2, 11, "Prefabs/KEY-a", "KEY-h"), out _);
        l.Flush();
        t.Check(l.Host.Revision == rev + 1 && At(gw, 2, 11) == "Prefabs/KEY-h" && l.GuestRejected.Last().Text.Contains("just changed") && Same(),
            "two swaps of one slot: the first lands, the other is refused instead of overwriting it");
        rev = l.Host.Revision;
        l.Guest.Submit(CommandTypes.PeripheralSwap, WorkstationWorld.Swap(E(ga, ActorSlot.Guest), 3, 10, "Prefabs/MON-a", "MON-c"), out _);
        l.Host.Submit(CommandTypes.PeripheralSwap, WorkstationWorld.Swap(E(ha, ActorSlot.Host), 2, 10, "Prefabs/MON-b", "MON-d"), out _);
        l.Flush();
        t.Check(l.Host.Revision == rev + 2 && At(gw, 3, 10) == "Prefabs/MON-c" && At(gw, 2, 10) == "Prefabs/MON-d" && Same(),
            "swaps at different workstations proceed independently");

        rev = l.Host.Revision;
        Send(l.Guest, WorkstationWorld.Swap(E(ga, ActorSlot.Guest), 2, 10, "Prefabs/MON-d", "KEY-x"));
        Send(l.Guest, WorkstationWorld.Swap(E(ga, ActorSlot.Guest), 2, 12, "", "MON-x"));
        Send(l.Guest, WorkstationWorld.Swap(E(ga, ActorSlot.Guest), 2, 10, "Prefabs/MON-d", ""));
        Send(l.Guest, WorkstationWorld.Swap(E(ga, ActorSlot.Guest) + 9, 2, 10, "Prefabs/MON-d", "MON-x"));
        t.Check(l.Host.Revision == rev && l.GuestRejected.TakeLast(4).Select(x => x.Text).ToArray() is var why &&
            why[0].Contains("doesn't fit") && why[1].Contains("Unknown peripheral slot") && why[2].Contains("Invalid") && why[3].Contains("presence"),
            "a part that doesn't fit, an unknown slot, an empty part and a stale epoch are refused");

        // The other player working on a PC at that workstation (main or tool bench) blocks swaps there only.
        Send(l.Host, ActivityWorld.Work(ha.Get(ActorSlot.Host)!, new(4)), CommandTypes.ActorActivity);
        rev = l.Host.Revision;
        Send(l.Guest, WorkstationWorld.Swap(E(ga, ActorSlot.Guest), 2, 10, "Prefabs/MON-d", "MON-e"));
        t.Check(l.Host.Revision == rev && At(gw, 2, 10) == "Prefabs/MON-d" && l.GuestRejected.Last().Text.Contains("working at that workstation"),
            "a swap where the other player is working is refused");
        Send(l.Guest, WorkstationWorld.Swap(E(ga, ActorSlot.Guest), 3, 10, "Prefabs/MON-c", "MON-e"));
        Send(l.Host, WorkstationWorld.Swap(E(ha, ActorSlot.Host), 2, 11, "Prefabs/KEY-h", "KEY-e"));
        t.Check(l.Host.Revision == rev + 2 && At(gw, 3, 10) == "Prefabs/MON-e" && At(gw, 2, 11) == "Prefabs/KEY-e" && Same(),
            "other workstations stay available, and a player may swap at the workstation they work at");
        Send(l.Host, ActivityWorld.Work(ha.Get(ActorSlot.Host)!, default), CommandTypes.ActorActivity);
        Send(l.Guest, ActivityWorld.Work(ga.Get(ActorSlot.Guest)!, new(6)), CommandTypes.ActorActivity);
        rev = l.Host.Revision;
        Send(l.Host, WorkstationWorld.Swap(E(ha, ActorSlot.Host), 3, 10, "Prefabs/MON-e", "MON-f"));
        Send(l.Host, WorkstationWorld.Swap(E(ha, ActorSlot.Host), 2, 10, "Prefabs/MON-d", "MON-f"));
        t.Check(l.Host.Revision == rev + 1 && At(gw, 3, 10) == "Prefabs/MON-e" && At(gw, 2, 10) == "Prefabs/MON-f" && l.HostRejected.Last().Text.Contains("working"),
            "the host is refused where the guest works, and allowed once the host itself stopped working");
        Send(l.Guest, ActivityWorld.Work(ga.Get(ActorSlot.Guest)!, default), CommandTypes.ActorActivity);

        rev = l.Host.Revision;
        hn.Host = _ => "That peripheral is still loading on the host's game.";
        Send(l.Guest, WorkstationWorld.Swap(E(ga, ActorSlot.Guest), 3, 10, "Prefabs/MON-e", "MON-g"));
        t.Check(l.Host.Revision == rev && At(hw, 3, 10) == "Prefabs/MON-e" && l.GuestRejected.Last().Text.Contains("still loading"),
            "a host-native refusal leaves the slot as it was");
        hn.Host = _ => null;

        rev = l.Host.Revision;
        var malformed = l.GuestRejected.Count;
        Send(l.Guest, new byte[] { 9, 0, 0, 0, 0, 0, 0, 0, 0 });
        Send(l.Guest, WorkstationWorld.Swap(E(ga, ActorSlot.Guest), 3, 10, "Prefabs/MON-e", "MON-g").Concat(new byte[] { 1 }).ToArray());
        t.Check(l.Host.Revision == rev && l.GuestRejected.Count == malformed + 2 && l.GuestRejected.TakeLast(2).All(x => x.Reason == RejectReason.Malformed),
            "unknown actions and trailing bytes are malformed");

        var calls = gn.Calls.Count;
        l.Guest.Submit(CommandTypes.PeripheralSwap, WorkstationWorld.Swap(E(ga, ActorSlot.Guest), 3, 10, "Prefabs/MON-e", "MON-h"), out _);
        var raw = l.ToHost[0]; l.Flush(); FakeLink.Deliver(l.Host, raw); l.Flush();
        t.Check(gn.Calls.Count == calls + 1 && At(gw, 3, 10) == "Prefabs/MON-h" && Same(), "a duplicated swap request applies and projects exactly once");

        l.MarkSnapshot();
        Send(l.Host, WorkstationWorld.Swap(E(ha, ActorSlot.Host), 3, 10, "Prefabs/MON-h", "MON-i"));
        calls = gn.Calls.Count;
        t.Check(l.Guest.Adopt(out _) && gn.Calls.Count == calls + 1 && At(gw, 3, 10) == "Prefabs/MON-i" && Same(),
            "a swap after the snapshot mark is held and projected once after admission");

        var before = l.Divergences.Count;
        gn.Prefix = "Other/";
        Send(l.Host, WorkstationWorld.Swap(E(ha, ActorSlot.Host), 3, 10, "Prefabs/MON-i", "MON-j"));
        t.Check(l.Divergences.Count == before + 1, "a peer that resolves the part to a different asset diverges explicitly");

        t.Section("Workstations: state and optional restore");
        var copy = new WorkstationWorld(new ObjectWorld(), new ActivityWorld(new ObjectWorld()), new FakePeripheralNative());
        copy.Read(new NetDataReader(Bytes(hw)));
        t.Check(Bytes(copy).SequenceEqual(Bytes(hw)), "peripheral bindings round-trip");
        bool Throws(Action a) { try { a(); return false; } catch (InvalidDataException) { return true; } }
        t.Check(Throws(() => copy.Replace(new[] { new PeripheralFact(2, 10, "a"), new PeripheralFact(2, 10, "b") })) &&
                Throws(() => copy.Replace(new[] { new PeripheralFact(-1, 10, "a") })) &&
                Throws(() => copy.Read(new NetDataReader(Bytes(hw).Concat(new byte[] { 0 }).ToArray()))) &&
                Throws(() => copy.Read(new NetDataReader(new byte[] { 1, 0, 0, 0, 2 }))),
            "duplicate slots, invalid bench IDs, trailing and truncated state are refused");

        // A save written before 0.10 has no workstation state: the restore leaves it empty for the host to recapture,
        // while every other registered state is still required.
        var older = new WorldSync(ActorSlot.Host, _ => { }); var olderObjects = new ObjectWorld();
        older.RegisterState(olderObjects); older.BeginGeneration("old");
        olderObjects.Replace(ho.WorldId, ho.NextId, ho.Locations, ho.Pcs, ho.Retired);
        var oldState = older.CapturePosition().State;
        var newer = new WorldSync(ActorSlot.Host, _ => { }); var newerObjects = new ObjectWorld();
        var newerSlots = new WorkstationWorld(newerObjects, new ActivityWorld(newerObjects), new FakePeripheralNative());
        newer.RegisterState(newerObjects); newer.RegisterState(newerSlots, optional: true); newer.BeginGeneration("new");
        newer.RestoreState(oldState);
        t.Check(newerObjects.Pcs.Count == 2 && !newerSlots.Bound, "an older save without workstation state restores, leaving the bindings for recapture");
        var strict = new WorldSync(ActorSlot.Host, _ => { }); var strictObjects = new ObjectWorld();
        strict.RegisterState(strictObjects); strict.RegisterState(new ActivityWorld(strictObjects)); strict.BeginGeneration("strict");
        t.Check(Throws(() => strict.RestoreState(oldState)), "a missing required state still fails the restore");
        var unbound = new WorkstationWorld(ho, ha, hn);
        t.Check(!unbound.Validate(new(new(ActorSlot.Host, 1), 1, 1), new NetDataReader(WorkstationWorld.Swap(E(ha, ActorSlot.Host), 3, 10, "x", "MON-a"))).Accepted,
            "swaps are refused until the host has captured the workstations");
    }
}

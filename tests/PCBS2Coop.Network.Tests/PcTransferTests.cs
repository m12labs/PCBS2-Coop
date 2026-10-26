using PCBS2Coop.Network;

/// <summary>Stands in for the native transfer adapter: a scripted host check and a log of projected moves.</summary>
internal sealed class FakeTransferNative : IPcTransferNative
{
    public Func<TransferCommand, IReadOnlyList<PcMove>, string?> Host = (_, _) => null;
    public readonly List<string> Calls = new();
    public string? CheckHost(in CommandContext ctx, TransferCommand c, IReadOnlyList<PcMove> moves) => Host(c, moves);
    /// <summary>Injected projection failure: the native move throws, as a failed native load would.</summary>
    public Func<TransferCommand, bool> Fail = _ => false;
    public void Moved(in MutationContext ctx, TransferCommand c, IReadOnlyList<PcMove> moves)
    {
        if (Fail(c)) throw new InvalidDataException("injected projection failure");
        Calls.Add($"{c.Action} {string.Join(",", moves.Select(m => $"{m.Pc.Value}:{Where(m.From)}>{Where(m.To)}"))} local={ctx.FromLocalPlayer}");
    }
    private static string Where(PcLocation l) => l.Place switch { PcPlace.Bench => l.Key, PcPlace.Storage => "S", _ => l.Actor.ToString() };
}

internal static class PcTransferTests
{
    public static void Run(Checker t)
    {
        t.Section("PC transfers: atomic pickup, drop/swap, store, retrieve and departure recovery");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hn = new FakeTransferNative(); var gn = new FakeTransferNative();
        l.Host.RegisterState(ho); l.Guest.RegisterState(go);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.Register(CommandTypes.PcLifecycle, new PcLifecycle(ho, ha, new FakePcNative(), true));
        l.Guest.Register(CommandTypes.PcLifecycle, new PcLifecycle(go, ga, new FakePcNative(), false));
        l.Host.Register(CommandTypes.PcTransfer, new PcTransfers(ho, ha, hn, true));
        l.Guest.Register(CommandTypes.PcTransfer, new PcTransfers(go, ga, gn, false));
        l.Host.BeginGeneration("transfers");
        var host = PlayerId.New(); var guest = PlayerId.New();
        var origin = new InstanceProvenance(InstanceOrigin.Baseline, host, default);
        PcFact Fact(ulong id, PcLocation at) => new(new(id), origin, at, new[] { new PartFact(new(id + 1), "case" + id, "CasePartInstance") }, new[] { new PartLink("caseID", new(id + 1)) });
        ho.Replace(Guid.NewGuid(), 10, new[] { new LocationFact(new(1), "A"), new LocationFact(new(2), "B"), new LocationFact(new(3), "C") },
            new[] { Fact(4, PcLocation.Bench("A")), Fact(6, PcLocation.Bench("B")), Fact(8, PcLocation.Storage) }, Array.Empty<NetObjectId>());
        OperationId Send(WorldSync w, byte[] bytes, ushort type = CommandTypes.PcTransfer) { w.Submit(type, bytes, out var op); l.Flush(); return op; }
        ulong E(ActivityWorld a, ActorSlot s) => a.Get(s)!.Epoch;
        bool Same() => ho.ToBytes().SequenceEqual(go.ToBytes()) && l.InStep && l.Divergences.Count == 0;
        PcLocation At(ObjectWorld o, ulong id) => o.Pcs.Single(x => x.Id.Value == id).Location;
        NetObjectId A = new(1), B = new(2), C = new(3);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, host), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, guest), CommandTypes.ActorActivity);
        l.Join();
        var hostHands = PcLocation.Carried(ActorSlot.Host, host); var guestHands = PcLocation.Carried(ActorSlot.Guest, guest);

        Send(l.Host, PcTransfers.Pickup(E(ha, ActorSlot.Host), new(4), A));
        t.Check(At(go, 4) == hostHands && hn.Calls.Last() == "Pickup 4:A>Host local=True" && gn.Calls.Last() == "Pickup 4:A>Host local=False" && Same(),
            "host bench pickup moves the PC into the host's hands on both peers; only the requester projects it natively");
        var rev = l.Host.Revision;
        Send(l.Guest, PcTransfers.Pickup(E(ga, ActorSlot.Guest), new(4), A));
        t.Check(l.Host.Revision == rev && l.GuestRejected.Last().Text.Contains("Someone else moved"), "a contested pickup of a PC already taken is refused");
        Send(l.Guest, PcTransfers.Pickup(E(ga, ActorSlot.Guest), new(6), A));
        Send(l.Guest, PcTransfers.Pickup(E(ga, ActorSlot.Guest), new(6), new(99)));
        t.Check(l.Host.Revision == rev && At(ho, 6) == PcLocation.Bench("B"), "a pickup naming the wrong or an unknown bench is refused");
        Send(l.Guest, PcTransfers.Pickup(E(ga, ActorSlot.Guest), new(6), B));
        t.Check(At(ho, 6) == guestHands && Same(), "the guest picks up a different PC independently");
        Send(l.Guest, PcTransfers.Retrieve(E(ga, ActorSlot.Guest), new(8)));
        t.Check(At(ho, 8) == PcLocation.Storage && l.GuestRejected.Last().Text.Contains("already hold"), "retrieval into occupied hands is refused");

        // Both drop onto empty bench C; the host's arrives first.
        rev = l.Host.Revision;
        l.Guest.Submit(CommandTypes.PcTransfer, PcTransfers.Drop(E(ga, ActorSlot.Guest), new(6), C, default), out _);
        l.Host.Submit(CommandTypes.PcTransfer, PcTransfers.Drop(E(ha, ActorSlot.Host), new(4), C, default), out _);
        l.Flush();
        t.Check(l.Host.Revision == rev + 1 && At(go, 4) == PcLocation.Bench("C") && At(go, 6) == guestHands &&
            l.GuestRejected.Last().Text.Contains("just taken") && Same(), "two drops into one empty bench: one lands, the other is refused and keeps its PC");
        rev = l.Host.Revision;
        Send(l.Guest, PcTransfers.Drop(E(ga, ActorSlot.Guest), new(6), C, new(8)));
        t.Check(l.Host.Revision == rev && l.GuestRejected.Last().Text.Contains("changed"), "a swap naming a PC that is no longer on that bench is refused");
        var swapCalls = gn.Calls.Count;
        Send(l.Guest, PcTransfers.Drop(E(ga, ActorSlot.Guest), new(6), C, new(4)));
        t.Check(l.Host.Revision == rev + 1 && At(ho, 6) == PcLocation.Bench("C") && At(ho, 4) == guestHands && gn.Calls.Count == swapCalls + 1 &&
            gn.Calls.Last() == "Drop 4:C>Guest,6:Guest>C local=True" && Same(), "an occupied-bench swap commits both PCs in one change, bench side first");
        rev = l.Host.Revision;
        Send(l.Guest, PcTransfers.Store(E(ga, ActorSlot.Guest), new(6)));
        Send(l.Guest, PcTransfers.Drop(E(ga, ActorSlot.Guest), new(6), A, default));
        Send(l.Host, PcTransfers.Store(E(ha, ActorSlot.Host), new(4)));
        t.Check(l.Host.Revision == rev && At(ho, 4) == guestHands, "a player cannot store or drop a PC that is not in their own hands");
        Send(l.Guest, PcTransfers.Store(E(ga, ActorSlot.Guest), new(4)));
        t.Check(At(go, 4) == PcLocation.Storage && Same(), "the guest stores its carried PC in the shared cupboard");

        // Both retrieve the same stored PC; the host's arrives first.
        rev = l.Host.Revision;
        l.Guest.Submit(CommandTypes.PcTransfer, PcTransfers.Retrieve(E(ga, ActorSlot.Guest), new(8)), out _);
        l.Host.Submit(CommandTypes.PcTransfer, PcTransfers.Retrieve(E(ha, ActorSlot.Host), new(8)), out _);
        l.Flush();
        t.Check(l.Host.Revision == rev + 1 && At(go, 8) == hostHands && l.GuestRejected.Last().Text.Contains("Someone else took") && Same(),
            "a contested cupboard retrieval gives the PC to exactly one player");
        Send(l.Guest, PcTransfers.Retrieve(E(ga, ActorSlot.Guest), new(4)));
        Send(l.Host, PcTransfers.Drop(E(ha, ActorSlot.Host), new(8), A, default));
        t.Check(At(ho, 4) == guestHands && At(ho, 8) == PcLocation.Bench("A") && Same(), "independent transfers by both players proceed");

        // Activity blocks movement of a PC someone works on or has a pending operation for.
        Send(l.Host, ActivityWorld.Work(ha.Get(ActorSlot.Host)!, new(8)), CommandTypes.ActorActivity);
        rev = l.Host.Revision;
        Send(l.Guest, PcTransfers.Drop(E(ga, ActorSlot.Guest), new(4), A, new(8)));
        t.Check(l.Host.Revision == rev && At(ho, 8) == PcLocation.Bench("A") && l.GuestRejected.Last().Text.Contains("in use"), "a swap cannot move a PC another player works on");
        Send(l.Host, ActivityWorld.Work(ha.Get(ActorSlot.Host)!, default), CommandTypes.ActorActivity);
        var claim = Send(l.Host, ActivityWorld.Reserve(ha.Get(ActorSlot.Host)!, new(6), ActivityOperation.Move), CommandTypes.ActorActivity);
        rev = l.Host.Revision;
        Send(l.Host, PcTransfers.Pickup(E(ha, ActorSlot.Host), new(6), C));
        t.Check(l.Host.Revision == rev && l.HostRejected.Last().Text.Contains("in use"), "a PC with a pending operation cannot be picked up");
        Send(l.Host, ActivityWorld.Release(ha.Get(ActorSlot.Host)!, claim), CommandTypes.ActorActivity);

        rev = l.Host.Revision;
        hn.Host = (c, _) => c.Action == TransferAction.Pickup ? "That PC is still appearing on the host's game." : null;
        Send(l.Guest, PcTransfers.Drop(E(ga, ActorSlot.Guest), new(4), B, default));
        Send(l.Guest, PcTransfers.Pickup(E(ga, ActorSlot.Guest), new(6), C));
        t.Check(l.Host.Revision == rev + 1 && At(ho, 6) == PcLocation.Bench("C") && l.GuestRejected.Last().Text.Contains("still appearing"),
            "a host-native refusal (projection still loading) leaves the PC where it was");
        hn.Host = (_, _) => null;

        rev = l.Host.Revision;
        var malformed = l.GuestRejected.Count;
        Send(l.Guest, new byte[] { 9, 0, 0, 0, 0, 0, 0, 0, 0 });
        Send(l.Guest, PcTransfers.Store(E(ga, ActorSlot.Guest), new(6)).Concat(new byte[] { 1 }).ToArray());
        Send(l.Host, PcTransfers.Recover(E(ha, ActorSlot.Host), (ActorSlot)7));
        t.Check(l.Host.Revision == rev && l.GuestRejected.Count == malformed + 2 && l.GuestRejected.TakeLast(2).All(x => x.Reason == RejectReason.Malformed) &&
            l.HostRejected.Last().Reason == RejectReason.Malformed, "unknown actions, trailing bytes and an invalid departed seat are malformed");

        Send(l.Guest, PcTransfers.Pickup(E(ga, ActorSlot.Guest), new(6), C));
        t.Check(At(ho, 6) == guestHands, "guest carries a PC before leaving");
        var oldEpoch = E(ga, ActorSlot.Guest);
        Send(l.Host, PcTransfers.Recover(E(ha, ActorSlot.Host), ActorSlot.Guest));
        Send(l.Guest, PcTransfers.Recover(E(ga, ActorSlot.Guest), ActorSlot.Guest));
        t.Check(At(ho, 6) == guestHands && l.HostRejected.Last().Text.Contains("No departed") && l.GuestRejected.Last().Text.Contains("Only the host"),
            "recovery refuses while the carrier is present and refuses guest requests");
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, default), CommandTypes.ActorActivity);
        rev = l.Host.Revision;
        Send(l.Host, PcTransfers.Recover(E(ha, ActorSlot.Host), ActorSlot.Guest));
        t.Check(l.Host.Revision == rev + 1 && At(go, 6) == PcLocation.Storage && go.Pcs.Single(x => x.Id.Value == 6).Parts.Single().Id.Value == 7 &&
            hn.Calls.Last() == "Recover 6:Guest>S local=True" && Same(), "a departed guest's carried PC moves to the shared cupboard once, keeping its IDs");
        Send(l.Host, PcTransfers.Recover(E(ha, ActorSlot.Host), ActorSlot.Guest));
        t.Check(l.Host.Revision == rev + 1, "a repeated recovery finds nothing to move");
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, guest), CommandTypes.ActorActivity);
        Send(l.Guest, PcTransfers.Retrieve(oldEpoch, new(6)));
        t.Check(At(ho, 6) == PcLocation.Storage && l.GuestRejected.Last().Text.Contains("presence"), "a request from the departed epoch cannot move a PC");
        Send(l.Guest, PcTransfers.Retrieve(E(ga, ActorSlot.Guest), new(6)));
        t.Check(At(ho, 6) == guestHands && Same(), "the rejoined guest retrieves the recovered PC from the cupboard");

        var moved = gn.Calls.Count;
        l.Guest.Submit(CommandTypes.PcTransfer, PcTransfers.Drop(E(ga, ActorSlot.Guest), new(6), C, default), out _);
        var raw = l.ToHost[0]; l.Flush(); FakeLink.Deliver(l.Host, raw); l.Flush();
        t.Check(gn.Calls.Count == moved + 1 && At(go, 6) == PcLocation.Bench("C") && Same(), "a duplicated transfer request moves and projects exactly once");

        l.MarkSnapshot();
        Send(l.Host, PcTransfers.Pickup(E(ha, ActorSlot.Host), new(6), C));
        var held = gn.Calls.Count;
        t.Check(l.Guest.Adopt(out _) && gn.Calls.Count == held + 1 && At(go, 6) == hostHands && Same(), "a transfer after the snapshot mark is held and projected once after admission");

        // A PC the host bound outside the change stream (a capture) is a tombstone on the guest: tolerated, not invented.
        var hidden = ho.NextId;
        ho.Replace(ho.WorldId, hidden + 2, ho.Locations, ho.Pcs.Append(Fact(hidden, PcLocation.Bench("C"))).ToArray(), ho.Retired);
        Send(l.Guest, PcLifecycle.Create(E(ga, ActorSlot.Guest), new("kolink", "CasePartInstance", 0), default), CommandTypes.PcLifecycle);
        Send(l.Guest, PcLifecycle.Delete(E(ga, ActorSlot.Guest), ho.Pcs.Single(x => x.Location.Actor == ActorSlot.Guest).Id, PcRemoval.Carried), CommandTypes.PcLifecycle);
        Send(l.Host, PcTransfers.Store(E(ha, ActorSlot.Host), new(6)));
        Send(l.Host, PcTransfers.Pickup(E(ha, ActorSlot.Host), new(hidden), C));
        t.Check(go.Lookup(new(hidden)) == ObjectLookup.Retired && l.Divergences.Count == 0 && l.InStep, "a guest skips moves of host-only PCs without diverging");
        Send(l.Host, PcTransfers.Store(E(ha, ActorSlot.Host), new(hidden)));
        Send(l.Host, PcLifecycle.Delete(E(ha, ActorSlot.Host), new(hidden), PcRemoval.Stored), CommandTypes.PcLifecycle);
        t.Check(Same(), "both models converge once the host-only PC is gone");

        // A peer whose copy disagrees about a location must report divergence rather than guess.
        var before = l.Divergences.Count;
        go.Replace(go.WorldId, go.NextId, go.Locations, go.Pcs.Select(x => x.Id.Value == 8 ? x with { Location = PcLocation.Storage } : x).ToArray(), go.Retired);
        Send(l.Host, PcTransfers.Pickup(E(ha, ActorSlot.Host), new(8), A));
        t.Check(l.Divergences.Count == before + 1, "a transfer that no longer applies on the receiving peer diverges explicitly");
    }
}

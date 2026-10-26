using PCBS2Coop.Network;

/// <summary>
/// Phase 3 acceptance (task 3h): a disconnect at each point of a guest request, and repair after a game fails to
/// show an accepted change. Races, swaps, held replay and departure recovery are in <see cref="PcTransferTests"/>.
/// </summary>
internal static class AcceptanceTests
{
    private sealed class World
    {
        public readonly FakeLink L = new();
        public readonly ObjectWorld Ho = new(), Go = new();
        public readonly ActivityWorld Ha, Ga;
        public readonly FakeTransferNative Hn = new(), Gn = new();
        public PlayerId Host = PlayerId.New(), Guest = PlayerId.New();
        public static readonly NetObjectId A = new(1), B = new(2), C = new(3);

        public World()
        {
            Ha = new ActivityWorld(Ho); Ga = new ActivityWorld(Go);
            L.Host.RegisterState(Ho); L.Guest.RegisterState(Go);
            L.Host.Register(CommandTypes.ActorActivity, Ha); L.Guest.Register(CommandTypes.ActorActivity, Ga);
            L.Host.RegisterState(Ha); L.Guest.RegisterState(Ga);
            L.Host.Register(CommandTypes.PcTransfer, new PcTransfers(Ho, Ha, Hn, true));
            L.Guest.Register(CommandTypes.PcTransfer, new PcTransfers(Go, Ga, Gn, false));
            L.Host.BeginGeneration("acceptance");
            Seed();
            Presence(ActorSlot.Host, Host); Presence(ActorSlot.Guest, Guest);
            L.Join();
        }

        /// <summary>Two same-SKU benched PCs (distinct identities) and one stored PC.</summary>
        public void Seed()
        {
            var origin = new InstanceProvenance(InstanceOrigin.Baseline, Host, default);
            PcFact Fact(ulong id, PcLocation at) => new(new(id), origin, at, new[] { new PartFact(new(id + 1), "kolink", "CasePartInstance") }, new[] { new PartLink("caseID", new(id + 1)) });
            Ho.Replace(Guid.NewGuid(), 10, new[] { new LocationFact(A, "A"), new LocationFact(B, "B"), new LocationFact(C, "C") },
                new[] { Fact(4, PcLocation.Bench("A")), Fact(6, PcLocation.Bench("B")), Fact(8, PcLocation.Storage) }, Array.Empty<NetObjectId>());
        }

        public void Presence(ActorSlot seat, PlayerId player) => Send(L.Host, ActivityWorld.Presence(seat, player), CommandTypes.ActorActivity);
        public void Send(WorldSync w, byte[] bytes, ushort type = CommandTypes.PcTransfer) { w.Submit(type, bytes, out _); L.Flush(); }
        public ulong E(ActivityWorld a, ActorSlot s) => a.Get(s)!.Epoch;
        public PcLocation At(ObjectWorld o, ulong id) => o.Pcs.Single(x => x.Id.Value == id).Location;
        public PcLocation GuestHands => PcLocation.Carried(ActorSlot.Guest, Guest);
        public bool Same => Ho.ToBytes().SequenceEqual(Go.ToBytes()) && L.InStep && L.Guest.Divergence == null && L.Host.Divergence == null;

        /// <summary>The link drops: nothing still on the wire arrives.</summary>
        public void Drop()
        {
            L.ToHost.Clear(); L.ToGuest.Clear();
            L.Host.OnLinkLost(); L.Guest.OnLinkLost();
        }

        /// <summary>What the host does when the guest leaves, then a rejoin by the same player with a fresh snapshot.</summary>
        public void DepartAndRejoin()
        {
            Presence(ActorSlot.Guest, default);
            Send(L.Host, PcTransfers.Recover(E(Ha, ActorSlot.Host), ActorSlot.Guest));
            L.Host.Linked = L.Guest.Linked = true;
            Presence(ActorSlot.Guest, Guest);
            L.Join();
        }
    }

    public static void Run(Checker t)
    {
        t.Section("Acceptance: disconnect before the host answers");
        {
            var w = new World();
            var rev = w.L.Host.Revision;
            w.L.Guest.Submit(CommandTypes.PcTransfer, PcTransfers.Pickup(w.E(w.Ga, ActorSlot.Guest), new(4), World.A), out _);
            w.Drop();
            t.Check(w.L.GuestRejected.Last().Reason == RejectReason.Abandoned && w.L.Host.Revision == rev && w.At(w.Ho, 4) == PcLocation.Bench("A"),
                "a request lost with the link is abandoned on the guest and never reaches the host's world");
            var epoch = w.E(w.Ha, ActorSlot.Guest);
            w.DepartAndRejoin();
            t.Check(w.At(w.Ho, 4) == PcLocation.Bench("A") && w.E(w.Ha, ActorSlot.Guest) != epoch && w.Same,
                "after rejoining, the PC is still on its bench and both copies match (new guest epoch)");
            w.Send(w.L.Guest, PcTransfers.Pickup(w.E(w.Ga, ActorSlot.Guest), new(4), World.A));
            t.Check(w.At(w.Go, 4) == w.GuestHands && w.Same, "the rejoined guest picks it up normally");
        }

        t.Section("Acceptance: disconnect after the host commits");
        {
            var w = new World();
            w.L.Guest.Submit(CommandTypes.PcTransfer, PcTransfers.Pickup(w.E(w.Ga, ActorSlot.Guest), new(6), World.B), out _);
            var request = w.L.ToHost.ToList(); w.L.ToHost.Clear();
            foreach (var m in request) FakeLink.Deliver(w.L.Host, m); // the host accepts and commits...
            t.Check(w.At(w.Ho, 6) == w.GuestHands, "the host committed the pickup to the guest's hands");
            w.Drop();                                                   // ...but the change never reaches the guest
            t.Check(w.L.GuestRejected.Last().Reason == RejectReason.Abandoned && w.At(w.Go, 6) == PcLocation.Bench("B"),
                "the guest never saw the commit: its request is abandoned");
            var part = w.Ho.Pcs.Single(x => x.Id.Value == 6).Parts.Single().Id;
            w.DepartAndRejoin();
            t.Check(w.At(w.Ho, 6) == PcLocation.Storage && w.Ho.Pcs.Single(x => x.Id.Value == 6).Parts.Single().Id == part && w.Same,
                "the departed guest's PC went to the shared cupboard once with its IDs; the rejoined guest sees the same");
            w.Send(w.L.Guest, PcTransfers.Retrieve(w.E(w.Ga, ActorSlot.Guest), new(6)));
            w.Send(w.L.Guest, PcTransfers.Drop(w.E(w.Ga, ActorSlot.Guest), new(6), World.C, default));
            t.Check(w.At(w.Go, 6) == PcLocation.Bench("C") && w.Same, "the rejoined guest retrieves and places it");
        }

        t.Section("Acceptance: a game fails to show an accepted change, then /sync repairs it");
        {
            var w = new World();
            w.Gn.Fail = c => c.Action == TransferAction.Pickup;
            w.Send(w.L.Host, PcTransfers.Pickup(w.E(w.Ha, ActorSlot.Host), new(4), World.A));
            t.Check(w.L.Guest.Divergence != null && w.L.Divergences.Single().Contains("injected projection failure"),
                "the guest's failed projection is reported as out of sync, not silently accepted");
            var applied = w.L.Guest.Revision;
            w.Send(w.L.Host, PcTransfers.Drop(w.E(w.Ha, ActorSlot.Host), new(4), World.C, default));
            t.Check(w.L.Guest.Revision == applied, "an out-of-sync guest stops applying later changes");
            w.Gn.Fail = _ => false;

            // /sync: the guest leaves; the host saves and reloads (a new generation restoring its saved state); the
            // guest is admitted again from a fresh snapshot.
            w.L.Guest.LeaveWorld("the host started a sync");
            var saved = w.L.Host.CapturePosition().State;
            w.L.Host.BeginGeneration("co-op world after /sync");
            w.L.Host.RestoreState(saved);
            w.Presence(ActorSlot.Guest, default); w.Presence(ActorSlot.Host, w.Host); w.Presence(ActorSlot.Guest, w.Guest);
            w.L.Join();
            t.Check(w.Same && w.At(w.Go, 4) == PcLocation.Bench("C") && w.L.Guest.Generation == 2, "after /sync both copies match the host's world again");
            w.Send(w.L.Guest, PcTransfers.Pickup(w.E(w.Ga, ActorSlot.Guest), new(4), World.C));
            t.Check(w.At(w.Ho, 4) == w.GuestHands && w.Same, "play continues normally after the sync");

            // When the host's save fails, the guest still reloads the host's current world (same generation).
            w.Gn.Fail = c => c.Action == TransferAction.Store;
            w.Send(w.L.Guest, PcTransfers.Store(w.E(w.Ga, ActorSlot.Guest), new(4)));
            t.Check(w.L.Guest.Divergence != null, "a second failure (the guest's own accepted store)");
            w.Gn.Fail = _ => false;
            w.L.Guest.LeaveWorld("the host started a sync");
            w.L.Join();
            t.Check(w.Same && w.At(w.Go, 4) == PcLocation.Storage && w.L.Guest.Generation == 2, "a guest-only reload of the current world also repairs it");
        }

        t.Section("Acceptance: the host's own game fails to show a change");
        {
            var w = new World();
            w.Hn.Fail = c => c.Action == TransferAction.Pickup;
            w.Send(w.L.Host, PcTransfers.Pickup(w.E(w.Ha, ActorSlot.Host), new(6), World.B));
            t.Check(w.L.Host.Divergence != null, "a failure on the host is reported on the host as well");
            w.Hn.Fail = _ => false;
            w.L.Guest.LeaveWorld("the host started a sync");
            var saved = w.L.Host.CapturePosition().State;
            w.L.Host.BeginGeneration("co-op world after /sync");
            w.L.Host.RestoreState(saved);
            w.Presence(ActorSlot.Guest, default); w.Presence(ActorSlot.Host, w.Host); w.Presence(ActorSlot.Guest, w.Guest);
            w.L.Join();
            t.Check(w.Same, "a new host generation clears the host's out-of-sync state and the guest rejoins in step");
        }
    }
}

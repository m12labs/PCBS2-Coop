using LiteNetLib.Utils;
using PCBS2Coop.Network;

/// <summary>
/// Phase 18a: faults at the Career boundaries with every Career world on both games. A request that arrives twice, out
/// of order, from an old world or from an old presence; a link lost before and after the host accepts; claims a
/// departing player leaves; a late join; and what a save taken in the middle of an action holds. Each case checks
/// one durable result on the host's game and the same model on both games.
/// </summary>
internal static class CareerFaultTests
{
    private static readonly JobFact[] Jobs = { new(1, JobState.New, 0), new(2, JobState.Finished, 0), new(3, JobState.Accepted, 0) };
    private static readonly MailFact Rent = new(MailKind.Rent, 638_000_000_000_000_001, 200);
    private static readonly OnboardingFact FirstDay = new("FirstDayProgressionOnboardings", 1, 3);
    private static readonly DeliveryChoice Tomorrow = new(30, 1, 1);
    private static byte[] Line(int n) => Enumerable.Range(0, 12).Select(i => (byte)(i + n)).ToArray();

    private sealed class Rig
    {
        public readonly FakeLink L = new();
        public readonly ActivityWorld Ha = new(new ObjectWorld()), Ga = new(new ObjectWorld());
        public readonly FakeCareerNative HcN = new(), GcN = new();
        public readonly FakeCommerceNative HsN = new(), GsN = new();
        public readonly FakePackageNative HpN = new(), GpN = new();
        public readonly FakeJobNative HjN = new(), GjN = new();
        public readonly FakeDayNative HdN = new(), GdN = new();
        public readonly FakeOnboardingNative HoN = new(), GoN = new();
        public readonly CareerWorld Hc, Gc;
        public readonly CommerceWorld Hs, Gs;
        public readonly PackageWorld Hp, Gp;
        public readonly JobWorld Hj, Gj;
        public readonly DayWorld Hd, Gd;
        public readonly OnboardingWorld Ho, Go;
        public readonly PlayerId Host = PlayerId.New(), Guest = PlayerId.New();
        public int HostDay = 5;

        public Rig()
        {
            Hc = new(Ha, HcN); Gc = new(Ga, GcN);
            Hs = new(Ha, Hc, HsN); Gs = new(Ga, Gc, GsN);
            Hp = new(Ha, Hc, HpN); Gp = new(Ga, Gc, GpN);
            Hj = new(Ha, HjN); Gj = new(Ga, GjN);
            Hd = new(Ha, HdN); Gd = new(Ga, GdN);
            Ho = new(Ha, HoN); Go = new(Ga, GoN);
            Hd.ReadyCheck = day => day != HostDay ? "the host's game is on another day." : null;
            Hs.OrderCheck = (o, _) => o.Total > Hc.Facts.Cash ? "There isn't enough cash for this order." : null;
            L.Host.RegisterState(Ha); L.Guest.RegisterState(Ga);
            foreach (var (h, g) in States) { L.Host.RegisterState(h, optional: true); L.Guest.RegisterState(g, optional: true); }
            L.Host.Register(CommandTypes.ActorActivity, Ha); L.Guest.Register(CommandTypes.ActorActivity, Ga);
            L.Host.Register(CommandTypes.Career, Hc); L.Guest.Register(CommandTypes.Career, Gc);
            L.Host.Register(CommandTypes.Commerce, Hs); L.Guest.Register(CommandTypes.Commerce, Gs);
            L.Host.Register(CommandTypes.Packages, Hp); L.Guest.Register(CommandTypes.Packages, Gp);
            L.Host.Register(CommandTypes.Jobs, Hj); L.Guest.Register(CommandTypes.Jobs, Gj);
            L.Host.Register(CommandTypes.Day, Hd); L.Guest.Register(CommandTypes.Day, Gd);
            L.Host.Register(CommandTypes.Onboarding, Ho); L.Guest.Register(CommandTypes.Onboarding, Go);
            L.Host.Observe(Hd); L.Guest.Observe(Gd);
            Load("career");
            L.Join();
        }

        private IEnumerable<(IWorldState Host, IWorldState Guest)> States =>
            new (IWorldState, IWorldState)[] { (Hc, Gc), (Hs, Gs), (Hp, Gp), (Hj, Gj), (Hd, Gd), (Ho, Go) };

        /// <summary>What the host does when a Career world generation starts (a load, a /sync, the day reload).</summary>
        public void Load(string why)
        {
            L.Host.BeginGeneration(why);
            Presence(ActorSlot.Host, Host); Presence(ActorSlot.Guest, Guest);
            Hc.BindHost(new CareerFacts(1000, 5, 3.5f, HostDay, 200, 0, 50, 0, 0, 0, 2, CareerFlags.UnlockedTablet),
                new[] { new OwnedItem(1, ItemList.Main, "cpu|a"), new OwnedItem(2, ItemList.Main, "gpu|b") }, 0, new byte[32], 3);
            Hs.BindHost(new Dictionary<ShopKind, IReadOnlyList<byte[]>> { [ShopKind.Parts] = new[] { Line(1) } }, Array.Empty<byte>());
            Hp.BindHost(new Dictionary<string, PackageFact>(), new Dictionary<string, int> { ["board/0"] = 2 }, null, 0);
            Hj.BindHost(Jobs, new[] { Rent });
            Hd.BindHost();
            Ho.BindHost(new[] { FirstDay });
        }

        public ulong H => Ha.Get(ActorSlot.Host)!.Epoch;
        public ulong G => Ga.Get(ActorSlot.Guest)!.Epoch;
        public void Presence(ActorSlot seat, PlayerId player) => Send(L.Host, CommandTypes.ActorActivity, ActivityWorld.Presence(seat, player));
        public void Send(WorldSync w, ushort type, byte[] payload) { w.Submit(type, payload, out _); L.Flush(); }

        /// <summary>The guest sends a request; it is held on the wire (returned) instead of delivered.</summary>
        public List<byte[]> Hold(ushort type, byte[] payload)
        {
            L.Guest.Submit(type, payload, out _);
            var wire = L.ToHost.ToList(); L.ToHost.Clear();
            return wire;
        }

        public void ToHost(IEnumerable<byte[]> wire) { foreach (var m in wire) FakeLink.Deliver(L.Host, m); }

        /// <summary>The link drops: nothing still on the wire arrives.</summary>
        public void Drop() { L.ToHost.Clear(); L.ToGuest.Clear(); L.Host.OnLinkLost(); L.Guest.OnLinkLost(); }

        /// <summary>What the host does when the guest leaves (its package and note go back), then the same player rejoins.</summary>
        public void DepartAndRejoin(string bench = "bench/9", string slot = "board/9")
        {
            Presence(ActorSlot.Guest, default);
            if (Hp.HandsFull(ActorSlot.Guest)) Send(L.Host, CommandTypes.Packages, PackageWorld.Recover(H, ActorSlot.Guest, bench, slot));
            L.Host.Linked = L.Guest.Linked = true;
            Presence(ActorSlot.Guest, Guest);
            L.Join();
        }

        /// <summary>The host's game ran the accepted actions and shares its jobs and inbox again.</summary>
        public void HostUpdate(params JobFact[] jobs)
        {
            foreach (var p in JobWorld.Submissions(H, new InboxUpdate(jobs, new byte[] { 7 }) { Mails = new[] { Rent } })) L.Host.Submit(CommandTypes.Jobs, p, out _);
            L.Flush();
        }

        public bool Same => States.All(s => Bytes(s.Host).SequenceEqual(Bytes(s.Guest))) && Bytes(Ha).SequenceEqual(Bytes(Ga)) &&
                            L.InStep && L.Guest.Divergence == null && L.Host.Divergence == null;

        public int Count(FakeJobNative n, string call) => n.Calls.Count(c => c == call);
    }

    private static byte[] Bytes(IWorldState s) { var w = new NetDataWriter(); s.Write(w); return w.CopyData(); }

    public static void Run(Checker t)
    {
        Repeats(t);
        Disconnects(t);
        Departures(t);
        LateJoin(t);
    }

    private static void Repeats(Checker t)
    {
        t.Section("Career faults: a request that arrives twice, out of order or from an old world (phase 18a)");
        {
            var r = new Rig();
            var collect = r.Hold(CommandTypes.Jobs, JobWorld.Act(r.G, JobAct.Collect, 2));
            r.ToHost(collect); r.ToHost(collect); r.L.Flush();
            t.Check(r.Count(r.HjN, "Collect 2 0 local=False") == 1 && r.L.Host.Stats.Duplicates == 1 && r.L.GuestAccepted.Count(a => a.Op.Actor == ActorSlot.Guest) == 1 && r.Same,
                "a job collect that arrives twice is run once by the host's game; the repeat gets the first answer");

            var order = new ShopOrder(ShopKind.Parts, r.Gs.Cart(ShopKind.Parts).Revision, r.Gs.BasketRevision, 1, 150, Tomorrow, default);
            var first = r.Hold(CommandTypes.Commerce, CommerceWorld.Order(r.G, order));
            var second = r.Hold(CommandTypes.Day, DayWorld.Ready(r.G, r.HostDay));
            var rejected = r.L.GuestRejected.Count;
            r.ToHost(second); r.ToHost(first); r.L.Flush();
            t.Check(r.HsN.Orders.Count == 0 && r.L.GuestRejected.Count == rejected + 1 && r.L.GuestRejected.Last().Reason == RejectReason.OutOfOrder &&
                    r.Gd.ReadyDay == r.HostDay && r.Same,
                "an order overtaken by a later request is refused, never placed late; the later request stands");

            var pay = r.Hold(CommandTypes.Jobs, JobWorld.Mail(r.G, Rent));
            var move = r.Hold(CommandTypes.Onboarding, OnboardingWorld.Move(r.G, FirstDay, FirstDay with { Progress = 4 }));
            rejected = r.L.GuestRejected.Count;
            r.Load("the host loaded its save");
            var hostRejected = r.L.Host.Stats.Rejected;
            r.ToHost(pay); r.ToHost(move); r.L.Flush();
            t.Check(!r.HjN.Calls.Any(c => c.StartsWith("mail")) && !r.HoN.Calls.Any() && r.L.Host.Stats.Rejected == hostRejected + 2 &&
                    r.L.GuestRejected.Count == rejected + 2 && r.L.GuestRejected.TakeLast(2).All(x => x.Reason == RejectReason.Abandoned),
                "a bill payment or tutorial step made for the world before a load is refused by the host and dropped by Client, not paid or moved in the new world");
            r.L.Join();
            t.Check(r.Same && r.Gj.Mails.Single() == Rent && r.Go.Fact(FirstDay.Type) == FirstDay, "after joining the loaded world, both games show the bill unpaid and the step unmoved");
        }
    }

    private static void Disconnects(Checker t)
    {
        t.Section("Career faults: the link drops before the host answers (phase 18a)");
        {
            var r = new Rig();
            var oldEpoch = r.G;
            var pay = JobWorld.Mail(oldEpoch, Rent);
            var rev = r.L.Host.Revision;
            r.L.Guest.Submit(CommandTypes.Jobs, pay, out _);
            r.L.Guest.Submit(CommandTypes.Day, DayWorld.Ready(oldEpoch, r.HostDay), out _);
            r.Drop();
            t.Check(r.L.GuestRejected.TakeLast(2).All(x => x.Reason == RejectReason.Abandoned) && r.L.Host.Revision == rev &&
                    !r.HjN.Calls.Any(c => c.StartsWith("mail")) && r.Hd.ReadyDay == 0,
                "a payment and a ready mark lost with the link are abandoned on the client and never reach the host's world");
            r.DepartAndRejoin();
            t.Check(r.G != oldEpoch && r.Same && r.Gd.ReadyDay == 0, "after rejoining, both games match and Client isn't ready (new presence)");
            var rejected = r.L.GuestRejected.Count;
            r.Send(r.L.Guest, CommandTypes.Jobs, pay);
            t.Check(r.L.GuestRejected.Count == rejected + 1 && r.L.GuestRejected.Last().Text.Contains("presence"),
                "the old request sent again from before the rejoin is refused (its presence is gone)");
            r.Send(r.L.Guest, CommandTypes.Jobs, JobWorld.Mail(r.G, Rent));
            t.Check(r.HjN.Calls.Count(c => c == "mail Rent 200 local=False") == 1 && r.Same, "the rejoined client pays the bill once");
        }

        t.Section("Career faults: the link drops after the host commits (phase 18a)");
        {
            var r = new Rig();
            var order = new ShopOrder(ShopKind.Parts, r.Gs.Cart(ShopKind.Parts).Revision, r.Gs.BasketRevision, 1, 150, Tomorrow, default);
            r.ToHost(r.Hold(CommandTypes.Commerce, CommerceWorld.Order(r.G, order)));
            r.ToHost(r.Hold(CommandTypes.Jobs, JobWorld.Act(r.G, JobAct.Collect, 2)));
            t.Check(r.HsN.Orders.Count == 1 && r.Hs.Cart(ShopKind.Parts).Lines.Count == 0 && r.Count(r.HjN, "Collect 2 0 local=False") == 1,
                "the host's game placed Client's order and collected the job");
            r.Drop();
            t.Check(r.GsN.Orders.Count == 0 && r.Gs.Cart(ShopKind.Parts).Lines.Count == 1 && r.L.GuestRejected.TakeLast(2).All(x => x.Reason == RejectReason.Abandoned),
                "Client never saw the commits: its requests are abandoned and its cart still shows the line");
            r.HostUpdate(Jobs[0], Jobs[1] with { State = JobState.Collected }, Jobs[2]);
            r.DepartAndRejoin();
            t.Check(r.Same && r.Gs.Cart(ShopKind.Parts).Lines.Count == 0 && r.Gj.Job(2)!.State == JobState.Collected,
                "the rejoined client sees the cart emptied by its order and the job collected, from the snapshot");
            var rejected = r.L.GuestRejected.Count;
            r.Send(r.L.Guest, CommandTypes.Commerce, CommerceWorld.Order(r.G, order with { CartRevision = r.Gs.Cart(ShopKind.Parts).Revision }));
            r.Send(r.L.Guest, CommandTypes.Jobs, JobWorld.Act(r.G, JobAct.Collect, 2));
            t.Check(r.HsN.Orders.Count == 1 && r.Count(r.HjN, "Collect 2 0 local=False") == 1 && r.L.GuestRejected.Count == rejected + 2,
                "trying again after the rejoin places no second order and collects no second payment");
        }
    }

    private static void Departures(Checker t)
    {
        t.Section("Career faults: what a departing client leaves behind (phase 18a)");
        var r = new Rig();
        r.Send(r.L.Guest, CommandTypes.Packages, PackageWorld.Pack(r.G, 1));
        r.Send(r.L.Guest, CommandTypes.Day, DayWorld.Ready(r.G, r.HostDay));
        t.Check(r.Gp.CarriedBy(ActorSlot.Guest) != null && r.Hd.ReadyDay == r.HostDay, "Client carries a package and is ready to end the day");
        r.Drop();
        r.DepartAndRejoin("bench/3");
        t.Check(!r.Hp.HandsFull(ActorSlot.Guest) && r.Hp.Benches["bench/3"].Contents.Count == 1 && r.Hd.ReadyDay == 0 && r.Same,
            "on leaving, its package goes to a free bench once and its ready mark is taken back; the rejoined client sees the same");

        r.ToHost(r.Hold(CommandTypes.Jobs, JobWorld.Act(r.G, JobAct.Accept, 1)));
        r.Drop();
        r.DepartAndRejoin();
        var rejected = r.L.HostRejected.Count;
        r.Send(r.L.Host, CommandTypes.Jobs, JobWorld.Act(r.H, JobAct.Accept, 1));
        t.Check(r.L.HostRejected.Count == rejected + 1 && r.L.HostRejected.Last().Text.Contains("already handling") && r.Count(r.HjN, "Accept 1 0 local=False") == 1,
            "a job Client's accept holds stays held after Client leaves, until the host's game has run it");
        r.HostUpdate(Jobs[0] with { State = JobState.Accepted }, Jobs[1], Jobs[2]);
        rejected = r.L.HostRejected.Count;
        r.Send(r.L.Host, CommandTypes.Jobs, JobWorld.Act(r.H, JobAct.Accept, 1));
        t.Check(!r.Hj.AnyActed && r.L.HostRejected.Count == rejected + 1 && r.L.HostRejected.Last().Text.Contains("can't be accepted") && r.Same,
            "the host's next update releases it; the job is accepted once");

        var stale = r.Hold(CommandTypes.Onboarding, OnboardingWorld.Move(r.G, FirstDay, FirstDay with { Progress = 4 }));
        r.Send(r.L.Host, CommandTypes.Onboarding, OnboardingWorld.Move(r.H, FirstDay, FirstDay with { Progress = 5 }));
        rejected = r.L.GuestRejected.Count;
        r.ToHost(stale); r.L.Flush();
        t.Check(r.Go.Fact(FirstDay.Type)!.Progress == 5 && r.L.GuestRejected.Count == rejected + 1 && r.L.GuestRejected.Last().Text.Contains("moved on") && r.Same,
            "a tutorial step both players did at once counts once (the first)");
    }

    private static void LateJoin(Checker t)
    {
        t.Section("Career faults: a client joining in the middle of a busy day (phase 18a)");
        var r = new Rig();
        r.Drop();
        r.Presence(ActorSlot.Guest, default);
        r.Send(r.L.Host, CommandTypes.Commerce, CommerceWorld.SetCart(r.H, ShopKind.Parts, r.Hs.Cart(ShopKind.Parts).Revision, new[] { Line(2), Line(3) }));
        r.Send(r.L.Host, CommandTypes.Jobs, JobWorld.Act(r.H, JobAct.Accept, 1));
        r.Send(r.L.Host, CommandTypes.Packages, PackageWorld.HostBench(r.H, "bench/1", new PackageFact(2, new[] { new byte[] { 1 } })));
        r.Send(r.L.Host, CommandTypes.Onboarding, OnboardingWorld.Move(r.H, FirstDay, FirstDay with { State = 2 }));
        var saved = new JobWorld(new ActivityWorld(new ObjectWorld()), new FakeJobNative());
        saved.Read(new NetDataReader(Bytes(r.Hj)));
        t.Check(saved.Job(1)!.State == JobState.New && !saved.AnyActed,
            "a save taken before the host's game has run an accepted job action doesn't hold it (so the host waits for it before saving)");
        r.L.Host.Linked = r.L.Guest.Linked = true;
        r.Presence(ActorSlot.Guest, r.Guest);
        r.L.Join();
        t.Check(r.Same && r.Gs.Cart(ShopKind.Parts).Lines.Count == 2 && r.Gp.Benches.ContainsKey("bench/1") && r.Go.Fact(FirstDay.Type)!.State == 2 &&
                r.L.Guest.Revision == r.L.Host.Revision && r.Gd.ReadyDay == 0,
            "the late client gets the same carts, packages, tutorials and revision as the host, and starts not ready");
        var rejected = r.L.GuestRejected.Count;
        r.Send(r.L.Guest, CommandTypes.Jobs, JobWorld.Act(r.G, JobAct.Accept, 1));
        t.Check(r.L.GuestRejected.Count == rejected + 1 && r.L.GuestRejected.Last().Text.Contains("already handling") && r.Count(r.HjN, "Accept 1 0 local=False") == 0,
            "its accept of the job the host's game is still accepting is refused (one result wins)");
    }
}

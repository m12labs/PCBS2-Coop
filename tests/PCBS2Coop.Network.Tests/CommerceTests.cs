using LiteNetLib.Utils;
using PCBS2Coop.Network;

internal sealed class FakeCommerceNative : ICommerceNative
{
    public readonly List<(bool Local, ShopKind Shop)> Carts = new();
    public readonly List<(bool Local, ShopOrder Order, int Lines)> Orders = new();
    public readonly List<(bool Local, ulong[] Items)> Sales = new();
    public int Baskets;
    public void CartChanged(in MutationContext ctx, ShopKind shop) => Carts.Add((ctx.FromLocalPlayer, shop));
    public void BasketsChanged(in MutationContext ctx) => Baskets++;
    public void Ordered(in MutationContext ctx, ShopOrder order, IReadOnlyList<byte[]> lines) => Orders.Add((ctx.FromLocalPlayer, order, lines.Count));
    public void Sold(in MutationContext ctx, IReadOnlyList<ulong> items) => Sales.Add((ctx.FromLocalPlayer, items.ToArray()));
}

/// <summary>Phase 16b: shared carts and job baskets, checkout placed by the host, and part sales.</summary>
internal static class CommerceTests
{
    private static byte[] Line(int n) => Enumerable.Range(0, 12).Select(i => (byte)(i + n)).ToArray();
    private static CareerFacts Facts(int cash = 1000) => new(cash, 5, 3.5f, 1, 200, 0, 50, 0, 0, 0, 2, CareerFlags.UnlockedTablet);
    private static readonly DeliveryChoice Tomorrow = new(30, 1, 1);

    public static void Run(Checker t)
    {
        Model(t);
        Sync(t);
    }

    private static void Model(Checker t)
    {
        t.Section("Shops: the shared carts");
        var activity = new ActivityWorld(new ObjectWorld());
        var w = new CommerceWorld(activity, new CareerWorld(activity, new FakeCareerNative()), new FakeCommerceNative());
        t.Check(!w.Bound && w.Cart(ShopKind.Parts).Lines.Count == 0, "an unbound model (Free Build) has empty carts");
        t.Check(Throws(() => w.BindHost(new Dictionary<ShopKind, IReadOnlyList<byte[]>> { [(ShopKind)9] = new[] { Line(1) } }, Array.Empty<byte>())),
            "an unknown shop is refused");
        t.Check(Throws(() => w.BindHost(new Dictionary<ShopKind, IReadOnlyList<byte[]>> { [ShopKind.Parts] = Enumerable.Range(0, CommerceWorld.MaxLines + 1).Select(Line).ToArray() },
            Array.Empty<byte>())), "a cart too large to share is refused");
        w.BindHost(new Dictionary<ShopKind, IReadOnlyList<byte[]>> { [ShopKind.Parts] = new[] { Line(1), Line(2) }, [ShopKind.Cable] = Array.Empty<byte[]>() }, new byte[] { 7 });
        t.Check(w.Bound && w.Cart(ShopKind.Parts).Revision == 1 && w.Cart(ShopKind.Parts).Lines.Count == 2 && w.BasketRevision == 1,
            "the host binds its carts and job baskets");
        var s = new NetDataWriter(); w.Write(s);
        var copy = new CommerceWorld(activity, new CareerWorld(activity, new FakeCareerNative()), new FakeCommerceNative());
        copy.Read(new NetDataReader(s.CopyData()));
        t.Check(copy.Bound && copy.Cart(ShopKind.Parts).SameLines(w.Cart(ShopKind.Parts).Lines) && copy.Cart(ShopKind.Parts).Revision == 1 &&
                copy.Baskets.SequenceEqual(new byte[] { 7 }) && copy.Cart(ShopKind.Water).Lines.Count == 0,
            "the carts travel with snapshots and saves exactly");
        copy.Reset();
        t.Check(!copy.Bound, "a new world generation starts unbound until the host binds it");
    }

    private static bool Throws(Action a) { try { a(); return false; } catch (InvalidDataException) { return true; } }

    private static void Sync(Checker t)
    {
        t.Section("Shops: both players shop, the host's game places orders");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hc = new CareerWorld(ha, new FakeCareerNative()); var gc = new CareerWorld(ga, new FakeCareerNative());
        var hn = new FakeCommerceNative(); var gn = new FakeCommerceNative();
        var hw = new CommerceWorld(ha, hc, hn); var gw = new CommerceWorld(ga, gc, gn);
        var checkedOrders = new List<int>();
        hw.OrderCheck = (o, lines) => { checkedOrders.Add(lines.Count); return o.Total > 900 ? "There isn't enough cash for this order." : null; };
        hw.SellCheck = items => items.Contains(3UL) ? "That part is being installed." : null;
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.RegisterState(hc, optional: true); l.Guest.RegisterState(gc, optional: true);
        l.Host.RegisterState(hw, optional: true); l.Guest.RegisterState(gw, optional: true);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.Register(CommandTypes.Career, hc); l.Guest.Register(CommandTypes.Career, gc);
        l.Host.Register(CommandTypes.Commerce, hw); l.Guest.Register(CommandTypes.Commerce, gw);
        l.Host.BeginGeneration("shop");
        void Send(WorldSync world, byte[] payload, ushort type = CommandTypes.Commerce) { world.Submit(type, payload, out _); l.Flush(); }
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, PlayerId.New()), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, PlayerId.New()), CommandTypes.ActorActivity);
        hc.BindHost(Facts(), new[] { new OwnedItem(1, ItemList.Main, "cpu|a"), new OwnedItem(2, ItemList.Main, "gpu|b"), new OwnedItem(3, ItemList.Main, "ram|c") }, 0, new byte[32], 4);
        hw.BindHost(new Dictionary<ShopKind, IReadOnlyList<byte[]>> { [ShopKind.Parts] = new[] { Line(1) } }, Array.Empty<byte>());
        l.Join();
        ulong H() => ha.Get(ActorSlot.Host)!.Epoch; ulong G() => ga.Get(ActorSlot.Guest)!.Epoch;
        t.Check(gw.Bound && gw.Cart(ShopKind.Parts).SameLines(hw.Cart(ShopKind.Parts).Lines), "a joining guest gets the host's carts with its snapshot");

        // The guest adds to the parts cart; the host's game shows it.
        Send(l.Guest, CommerceWorld.SetCart(G(), ShopKind.Parts, 1, new[] { Line(1), Line(2) }));
        t.Check(hw.Cart(ShopKind.Parts).Revision == 2 && hw.Cart(ShopKind.Parts).Lines.Count == 2 && gw.Cart(ShopKind.Parts).Revision == 2 &&
                hn.Carts.Last() == (false, ShopKind.Parts) && gn.Carts.Last() == (true, ShopKind.Parts) && l.InStep,
            "a cart edit by either player reaches the other game");
        // Both edit at once: the second edit names an old revision and is refused (its game takes the shared cart back).
        Send(l.Host, CommerceWorld.SetCart(H(), ShopKind.Parts, 2, new[] { Line(3) }));
        var before = l.GuestRejected.Count;
        Send(l.Guest, CommerceWorld.SetCart(G(), ShopKind.Parts, 2, new[] { Line(4) }));
        t.Check(l.GuestRejected.Count == before + 1 && l.GuestRejected.Last().Text.Contains("same moment") && gw.Cart(ShopKind.Parts).Lines.Single().SequenceEqual(Line(3)),
            "two edits of one cart at once: the later one is refused and the cart is the other's");
        Send(l.Guest, CommerceWorld.SetBaskets(G(), 1, new byte[] { 1, 2 }));
        t.Check(hw.Baskets.SequenceEqual(new byte[] { 1, 2 }) && hn.Baskets == 1 && gw.BasketRevision == 2, "job baskets are shared the same way");

        // The guest buys the cart as its screen showed it; the host's game places the order and both carts empty.
        var cart = gw.Cart(ShopKind.Parts);
        var order = new ShopOrder(ShopKind.Parts, cart.Revision, gw.BasketRevision, 1, 150, Tomorrow, default);
        Send(l.Guest, CommerceWorld.Order(G(), order));
        t.Check(hn.Orders.Single() == (false, order, 1) && gn.Orders.Single() == (true, order, 1) && checkedOrders.Single() == 1 &&
                hw.Cart(ShopKind.Parts).Lines.Count == 0 && gw.Cart(ShopKind.Parts).Lines.Count == 0 && l.InStep,
            "the guest's order reaches the host's game with its lines, and the cart empties on both");
        before = l.GuestRejected.Count;
        Send(l.Guest, CommerceWorld.Order(G(), order));
        t.Check(l.GuestRejected.Count == before + 1 && hn.Orders.Count == 1, "the same order sent twice is refused (the cart is empty)");

        // A stale cart: the host changed it after the guest's screen showed it.
        Send(l.Guest, CommerceWorld.SetCart(G(), ShopKind.Parts, gw.Cart(ShopKind.Parts).Revision, new[] { Line(5) }));
        var stale = new ShopOrder(ShopKind.Parts, gw.Cart(ShopKind.Parts).Revision, gw.BasketRevision, 1, 100, Tomorrow, default);
        Send(l.Host, CommerceWorld.SetCart(H(), ShopKind.Parts, hw.Cart(ShopKind.Parts).Revision, new[] { Line(5), Line(6) }));
        before = l.GuestRejected.Count;
        Send(l.Guest, CommerceWorld.Order(G(), stale));
        t.Check(l.GuestRejected.Count == before + 1 && l.GuestRejected.Last().Text.Contains("cart changed") && hn.Orders.Count == 1,
            "an order for a cart that changed since is refused");
        before = l.GuestRejected.Count;
        Send(l.Guest, CommerceWorld.Order(G(), stale with { CartRevision = gw.Cart(ShopKind.Parts).Revision, Lines = 2, Total = 1200 }));
        t.Check(l.GuestRejected.Count == before + 1 && l.GuestRejected.Last().Text.Contains("enough cash") && gw.Cart(ShopKind.Parts).Lines.Count == 2,
            "the host's check refuses an order it can't pay for, and the cart stays");
        before = l.GuestRejected.Count;
        Send(l.Guest, CommerceWorld.Order(G(), stale with { CartRevision = gw.Cart(ShopKind.Parts).Revision, Lines = 2, Delivery = new DeliveryChoice(10, 5, 2) }));
        t.Check(l.GuestRejected.Count == before + 1, "an impossible delivery is refused");

        // Selling: owned items by ID; the host's game sells.
        Send(l.Guest, CommerceWorld.Sell(G(), new ulong[] { 1, 2 }));
        t.Check(hn.Sales.Single().Items.SequenceEqual(new ulong[] { 1, 2 }) && !hn.Sales[0].Local &&
                gn.Sales.Single().Local, "the guest's sale reaches the host's game by item ID");
        before = l.GuestRejected.Count;
        Send(l.Guest, CommerceWorld.Sell(G(), new ulong[] { 9 }));
        Send(l.Guest, CommerceWorld.Sell(G(), new ulong[] { 1, 1 }));
        Send(l.Guest, CommerceWorld.Sell(G(), new ulong[] { 3 }));
        t.Check(l.GuestRejected.Count == before + 3 && l.GuestRejected.Last().Text.Contains("installed") && hn.Sales.Count == 1,
            "an item that isn't owned, sold twice, or in use can't be sold");

        // Free Build: nothing is bound; shopping commands are refused.
        hw.Reset();
        before = l.HostRejected.Count;
        Send(l.Host, CommerceWorld.SetCart(H(), ShopKind.Parts, 0, new[] { Line(1) }));
        t.Check(l.HostRejected.Count == before + 1 && l.HostRejected.Last().Text.Contains("co-op career"), "without a bound career, shopping commands are refused");
    }
}

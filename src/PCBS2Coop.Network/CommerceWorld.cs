using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>The game's shops (<c>ShopID</c>, same numbers).</summary>
public enum ShopKind : byte { Parts = 1, Water = 2, Peripheral = 3, Cable = 4 }

/// <summary>One shop's shared cart: the lines (each a serialized native cart entry, compared byte for byte) and a
/// revision that every accepted change moves on.</summary>
public sealed record ShopCart(ulong Revision, IReadOnlyList<byte[]> Lines)
{
    public static ShopCart Empty { get; } = new(0, Array.Empty<byte[]>());
    public bool SameLines(IReadOnlyList<byte[]> other) => Lines.Count == other.Count && Lines.Zip(other).All(p => p.First.AsSpan().SequenceEqual(p.Second));
}

/// <summary>The delivery the buyer chose (the game's <c>DeliveryOption</c>: cost and working days).</summary>
public sealed record DeliveryChoice(int Cost, int MinDays, int MaxDays)
{
    public bool IsValid => Cost is >= 0 and <= 1_000_000 && MinDays is >= 0 and <= 60 && MaxDays >= MinDays && MaxDays <= 60;
}

/// <summary>A checkout as the buyer saw it: the cart (by revision) and its job baskets, the number of lines, the total
/// shown and the delivery. <see cref="Pc"/> is the shared PC whose Cable app the buyer used (none for tablet shops).</summary>
public sealed record ShopOrder(ShopKind Shop, ulong CartRevision, ulong BasketRevision, int Lines, int Total, DeliveryChoice Delivery, NetObjectId Pc)
{
    internal void Write(NetDataWriter w)
    {
        w.Put((byte)Shop); w.Put(CartRevision); w.Put(BasketRevision); w.Put(Lines); w.Put(Total);
        w.Put(Delivery.Cost); w.Put(Delivery.MinDays); w.Put(Delivery.MaxDays); w.Put(Pc.Value);
    }

    internal static ShopOrder Read(NetDataReader r) =>
        new((ShopKind)r.GetByte(), r.GetULong(), r.GetULong(), r.GetInt(), r.GetInt(), new DeliveryChoice(r.GetInt(), r.GetInt(), r.GetInt()), new NetObjectId(r.GetULong()));
}

/// <summary>Native side of shared shopping. Runs on both peers in host order and must not submit commands.</summary>
public interface ICommerceNative
{
    /// <summary>A shop's cart changed (an edit or an order). The game that made the edit already shows it.</summary>
    void CartChanged(in MutationContext ctx, ShopKind shop);
    /// <summary>The job baskets changed.</summary>
    void BasketsChanged(in MutationContext ctx);
    /// <summary>The order is accepted, with the cart lines it bought (the cart is already empty in the model). The
    /// host's game places it; the buyer's game says so.</summary>
    void Ordered(in MutationContext ctx, ShopOrder order, IReadOnlyList<byte[]> lines);
    /// <summary>Owned items are sold. The host's game sells them; its career update follows.</summary>
    void Sold(in MutationContext ctx, IReadOnlyList<ulong> items);
}

/// <summary>
/// Phase 16b (owner decisions, November 8, 2026): shared shop carts, checkout and part sales in a co-op Career.
/// <list type="bullet">
/// <item>Each shop has one cart both players edit. An edit replaces the whole cart and names the revision it was
/// made on; one made on an old revision is refused and that game takes the shared cart back. The job baskets (which
/// cart items are for which job) are shared the same way.</item>
/// <item>The host's game places every order. A buyer's checkout names the cart revision, the number of lines, the
/// total it showed and the delivery. The host refuses a stale cart, a different total or too little cash
/// (<see cref="OrderCheck"/>), then places the order with the game's own steps; the cash, items and ledger reach the
/// guest as a career update (<see cref="CareerWorld"/>).</item>
/// <item>Selling owned items names them by ID; the host refuses one that is gone or in use (<see cref="SellCheck"/>).</item>
/// </list>
/// Free Build leaves it unbound and refuses every command.
/// </summary>
public sealed class CommerceWorld : ICommandHandler, IWorldState
{
    private enum Step : byte { SetCart = 1, SetBaskets = 2, Order = 3, Sell = 4 }
    public const int MaxLines = 160, MaxLineBytes = 2048, MaxCartBytes = 12 * 1024, MaxBasketBytes = 12 * 1024, MaxSold = 512;

    private readonly ActivityWorld _activity;
    private readonly CareerWorld _career;
    private readonly ICommerceNative _native;
    private readonly Dictionary<ShopKind, ShopCart> _carts = new();

    public CommerceWorld(ActivityWorld activity, CareerWorld career, ICommerceNative native) { _activity = activity; _career = career; _native = native; }
    public string Name => "commerce";

    /// <summary>Host: why this order can't be placed (cash, a different total), or null. Called after the model checks.</summary>
    public Func<ShopOrder, IReadOnlyList<byte[]>, string?>? OrderCheck { get; set; }
    /// <summary>Host: why these owned items can't be sold now, or null.</summary>
    public Func<IReadOnlyList<ulong>, string?>? SellCheck { get; set; }

    public bool Bound { get; private set; }
    public ShopCart Cart(ShopKind shop) => _carts.TryGetValue(shop, out var c) ? c : ShopCart.Empty;
    public ulong BasketRevision { get; private set; }
    public byte[] Baskets { get; private set; } = Array.Empty<byte>();

    public static bool IsShop(ShopKind shop) => shop is ShopKind.Parts or ShopKind.Water or ShopKind.Peripheral or ShopKind.Cable;

    public void Reset() { _carts.Clear(); Bound = false; BasketRevision = 0; Baskets = Array.Empty<byte>(); }

    /// <summary>Host, when a world generation starts: the carts and baskets as its game holds them.</summary>
    public void BindHost(IReadOnlyDictionary<ShopKind, IReadOnlyList<byte[]>> carts, byte[] baskets)
    {
        if (carts.Keys.Any(k => !IsShop(k)) || carts.Values.Any(l => !LinesValid(l)) || baskets.Length > MaxBasketBytes)
            throw new InvalidDataException("Invalid shop binding.");
        _carts.Clear();
        foreach (var (shop, lines) in carts) _carts[shop] = new ShopCart(1, lines.Select(l => l.ToArray()).ToArray());
        Baskets = baskets.ToArray(); BasketRevision = 1; Bound = true;
    }

    public static bool LinesValid(IReadOnlyList<byte[]> lines) =>
        lines.Count <= MaxLines && lines.All(l => l.Length is > 0 and <= MaxLineBytes) && lines.Sum(l => l.Length + 4) <= MaxCartBytes;

    // ---------------- commands ----------------

    private static byte[] Encode(Action<NetDataWriter> f) { var w = new NetDataWriter(); f(w); return w.CopyData(); }

    public static byte[] SetCart(ulong epoch, ShopKind shop, ulong baseRevision, IReadOnlyList<byte[]> lines) => Encode(w =>
    {
        w.Put((byte)Step.SetCart); w.Put(epoch); w.Put((byte)shop); w.Put(baseRevision);
        w.Put(lines.Count); foreach (var l in lines) Protocol.WriteBlob(w, l);
    });

    public static byte[] SetBaskets(ulong epoch, ulong baseRevision, byte[] baskets) => Encode(w =>
    {
        w.Put((byte)Step.SetBaskets); w.Put(epoch); w.Put(baseRevision); Protocol.WriteBlob(w, baskets);
    });

    public static byte[] Order(ulong epoch, ShopOrder order) => Encode(w => { w.Put((byte)Step.Order); w.Put(epoch); order.Write(w); });

    public static byte[] Sell(ulong epoch, IReadOnlyList<ulong> items) => Encode(w =>
    {
        w.Put((byte)Step.Sell); w.Put(epoch); w.Put(items.Count); foreach (var i in items) w.Put(i);
    });

    private sealed record Command(Step Step, ulong Epoch, ShopKind Shop, ulong Revision, IReadOnlyList<byte[]> Lines, byte[] Blob, ShopOrder? Order, IReadOnlyList<ulong> Items);

    private static Command Parse(NetDataReader r)
    {
        var step = (Step)r.GetByte(); var epoch = r.GetULong();
        Command c;
        switch (step)
        {
            case Step.SetCart:
            {
                var shop = (ShopKind)r.GetByte(); var revision = r.GetULong(); var n = r.GetInt();
                if (n is < 0 or > MaxLines) throw new FormatException("Too many cart lines.");
                var lines = new byte[n][];
                for (var i = 0; i < n; i++) lines[i] = Protocol.ReadBlob(r, MaxLineBytes, out _) is { Length: > 0 } l ? l : throw new FormatException("Invalid cart line.");
                c = new(step, epoch, shop, revision, lines, Array.Empty<byte>(), null, Array.Empty<ulong>());
                break;
            }
            case Step.SetBaskets:
            {
                var revision = r.GetULong();
                var blob = Protocol.ReadBlob(r, MaxBasketBytes, out _) ?? throw new FormatException("Invalid job baskets.");
                c = new(step, epoch, default, revision, Array.Empty<byte[]>(), blob, null, Array.Empty<ulong>());
                break;
            }
            case Step.Order:
                c = new(step, epoch, default, 0, Array.Empty<byte[]>(), Array.Empty<byte>(), ShopOrder.Read(r), Array.Empty<ulong>());
                break;
            case Step.Sell:
            {
                var n = r.GetInt();
                if (n is < 1 or > MaxSold) throw new FormatException("Invalid sale.");
                var items = new ulong[n]; for (var i = 0; i < n; i++) items[i] = r.GetULong();
                c = new(step, epoch, default, 0, Array.Empty<byte[]>(), Array.Empty<byte>(), null, items);
                break;
            }
            default: throw new FormatException("Unknown shop step.");
        }
        if (r.AvailableBytes != 0) throw new FormatException("Trailing shop bytes.");
        return c;
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        if (!Bound || !_career.Bound) return CommandVerdict.Reject("Shopping is shared only in a co-op career.");
        if (_activity.Get(ctx.Op.Actor) is not { } actor || actor.Epoch != c.Epoch) return CommandVerdict.Reject("Actor presence changed.");
        switch (c.Step)
        {
            case Step.SetCart:
                if (!IsShop(c.Shop)) return CommandVerdict.Reject("Unknown shop.");
                if (!LinesValid(c.Lines)) return CommandVerdict.Reject("The cart is too large to share.");
                return c.Revision != Cart(c.Shop).Revision ? CommandVerdict.Reject("The other player changed this cart at the same moment.") : CommandVerdict.Accept;
            case Step.SetBaskets:
                return c.Revision != BasketRevision ? CommandVerdict.Reject("The other player changed the job baskets at the same moment.") : CommandVerdict.Accept;
            case Step.Order:
            {
                var o = c.Order!;
                if (!IsShop(o.Shop) || !o.Delivery.IsValid || o.Total < 0) return CommandVerdict.Reject("Invalid order.");
                var cart = Cart(o.Shop);
                if (cart.Lines.Count == 0) return CommandVerdict.Reject("The cart is empty.");
                if (o.CartRevision != cart.Revision || o.BasketRevision != BasketRevision || o.Lines != cart.Lines.Count)
                    return CommandVerdict.Reject("The cart changed before the order arrived. Check it and buy again.");
                return OrderCheck?.Invoke(o, cart.Lines) is { } why ? CommandVerdict.Reject(why) : CommandVerdict.Accept;
            }
            default:
            {
                if (c.Items.Distinct().Count() != c.Items.Count) return CommandVerdict.Reject("An item is sold twice.");
                if (c.Items.Any(i => _career.Item(i) == null)) return CommandVerdict.Reject("That part is no longer in the shared inventory.");
                return SellCheck?.Invoke(c.Items) is { } why ? CommandVerdict.Reject(why) : CommandVerdict.Accept;
            }
        }
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        switch (c.Step)
        {
            case Step.SetCart:
                _carts[c.Shop] = new ShopCart(Cart(c.Shop).Revision + 1, c.Lines);
                _native.CartChanged(ctx, c.Shop);
                break;
            case Step.SetBaskets:
                Baskets = c.Blob; BasketRevision++;
                _native.BasketsChanged(ctx);
                break;
            case Step.Order:
            {
                var o = c.Order!;
                var bought = Cart(o.Shop).Lines;
                // The order buys the whole cart, which empties on both games; its job baskets empty with it on the
                // host, whose next basket edit shares that.
                _carts[o.Shop] = new ShopCart(Cart(o.Shop).Revision + 1, Array.Empty<byte[]>());
                _native.Ordered(ctx, o, bought);
                _native.CartChanged(ctx, o.Shop);
                break;
            }
            default:
                _native.Sold(ctx, c.Items);
                break;
        }
    }

    // ---------------- snapshot state ----------------

    public void Write(NetDataWriter w)
    {
        w.Put(Bound);
        if (!Bound) return;
        w.Put(_carts.Count);
        foreach (var (shop, cart) in _carts.OrderBy(p => p.Key))
        {
            w.Put((byte)shop); w.Put(cart.Revision); w.Put(cart.Lines.Count);
            foreach (var l in cart.Lines) Protocol.WriteBlob(w, l);
        }
        w.Put(BasketRevision); Protocol.WriteBlob(w, Baskets);
    }

    public void Read(NetDataReader r)
    {
        Reset();
        if (!r.GetBool()) return;
        var n = r.GetInt();
        if (n is < 0 or > 4) throw new InvalidDataException("Invalid shop state.");
        for (var i = 0; i < n; i++)
        {
            var shop = (ShopKind)r.GetByte(); var revision = r.GetULong(); var count = r.GetInt();
            if (!IsShop(shop) || _carts.ContainsKey(shop) || count is < 0 or > MaxLines) throw new InvalidDataException("Invalid cart in the shop state.");
            var lines = new byte[count][];
            for (var k = 0; k < count; k++) lines[k] = Protocol.ReadBlob(r, MaxLineBytes, out _) ?? throw new InvalidDataException("Invalid cart line in the shop state.");
            _carts[shop] = new ShopCart(revision, lines);
        }
        BasketRevision = r.GetULong();
        Baskets = Protocol.ReadBlob(r, MaxBasketBytes, out _) ?? Array.Empty<byte>();
        Bound = true;
    }
}

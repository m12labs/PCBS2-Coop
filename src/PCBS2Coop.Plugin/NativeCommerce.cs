using LiteNetLib.Utils;
using UnityEngine;
using PCBS2Coop.Network;
using NativeList = Il2CppSystem.Collections.Generic.List<ShopEntry>;

namespace PCBS2Coop;

/// <summary>
/// Phase 16b (owner decisions, November 8, 2026): shared shopping in a co-op Career on this game
/// (<see cref="CommerceWorld"/>, command 19).
/// <list type="bullet">
/// <item>Carts: each shop's cart lives in <c>CareerStatus.State</c> (<c>GetShopTrolley</c>); the shop screens read that
/// same list. Both games check their carts four times a second; a local edit is shared as the whole cart, and the
/// other game writes it into its list in place and refreshes any open shop screen. Job baskets
/// (<c>JobTag.InBasket</c>/<c>InBasketWater</c>) follow the same way.</item>
/// <item>Checkout: the host's own Buy runs the game's checkout. The guest's Buy sends the order to the host, whose game
/// places it with the game's own steps in the game's order (decompile, <c>Checkout.OnBuy</c>): pay, move job baskets
/// to transit, then each line to the inventory (available now), the delivery box (same day) or the calendar (later
/// days). Its cash, items and ledger reach the guest as a career update. Not repeated for a guest's order: the
/// confirmation page, tutorials, the order e-mail and onboarding exemptions.</item>
/// <item>A case bought for same-day delivery goes to the delivery box instead of appearing in the workshop (co-op
/// shares PCs only through its own case spawn).</item>
/// <item>Cable app: the guest's Buy has the host's game press Buy on the same shared PC's Cable app.</item>
/// <item>Selling parts from the inventory: the host's game sells; the guest's sale is sent to it by item ID.</item>
/// </list>
/// </summary>
internal sealed class NativeCommerce : ICommerceNative
{
    private const float PollEvery = 0.25f, PendingTimeout = 15f;
    private static readonly ShopKind[] Shops = { ShopKind.Parts, ShopKind.Water, ShopKind.Peripheral, ShopKind.Cable };

    private readonly CoopController _coop;
    private readonly WorldSync _world;
    public CommerceWorld Handler { get; }
    internal static NativeCommerce Current => CoopController.Instance?.Commerce;

    /// <summary>Each cart as this game's list last read or wrote it: a difference is a local edit.</summary>
    private readonly Dictionary<ShopKind, List<byte[]>> _seen = new();
    private byte[] _seenBaskets = Array.Empty<byte>();
    private readonly Dictionary<OperationId, (string What, ShopKind Shop, float At)> _pending = new();
    private float _nextPoll;
    /// <summary>The host's own checkout is running (a same-day case goes to the delivery box).</summary>
    [ThreadStatic] internal static bool InHostCheckout;

    public NativeCommerce(CoopController coop, WorldSync world, CareerWorld career)
    {
        _coop = coop; _world = world;
        Handler = new CommerceWorld(coop.Activity, career, this) { OrderCheck = CheckOrder, SellCheck = CheckSell };
        world.GenerationStarted += (uint _, string _) => Clear();
        world.Rejected += OnRejected;
    }

    private void Clear() { _seen.Clear(); _seenBaskets = Array.Empty<byte>(); _pending.Clear(); }

    private bool Active => NativeCareer.InCareer && Handler.Bound && _world.Admitted && _world.Divergence == null && _coop.WorldReady;
    private ulong Epoch => _coop.Activity.Get(_world.Local)?.Epoch ?? 0;
    private static CareerStatus Status => CareerStatus.Get();
    private static CareerStatus.State Data => CareerStatus.GetState();

    // ---------------- reading and writing the game ----------------

    private static NativeList Trolley(ShopKind shop) => Status?.GetShopTrolley((PartDesc.ShopID)(int)shop);

    internal static byte[] WriteLine(ShopEntry e)
    {
        var w = new NetDataWriter();
        w.Put(e.PartID ?? ""); w.Put(e.m_cost); w.Put(e.m_delivery); w.Put(e.m_inventory);
        var misc = e.m_miscData;
        w.Put(misc == null ? -1 : misc.Length);
        if (misc != null) foreach (var v in misc) w.Put(v);
        w.Put(e.m_cableBundleName != null); if (e.m_cableBundleName != null) w.Put(e.m_cableBundleName);
        var cables = e.m_customCableMiscData;
        w.Put(cables == null ? -1 : cables.Count);
        if (cables != null)
            foreach (var a in cables)
            {
                w.Put(a == null ? -1 : a.Length);
                if (a != null) foreach (var v in a) w.Put(v);
            }
        return w.CopyData();
    }

    private static int[] ReadInts(NetDataReader r)
    {
        var n = r.GetInt();
        if (n < -1 || n > 4096) throw new InvalidDataException("Invalid cart line.");
        if (n < 0) return null;
        var a = new int[n]; for (var i = 0; i < n; i++) a[i] = r.GetInt();
        return a;
    }

    internal static ShopEntry ReadLine(byte[] data)
    {
        var r = new NetDataReader(data);
        var id = r.GetString(512); var cost = r.GetInt(); var delivery = r.GetInt(); var stock = r.GetInt();
        var misc = ReadInts(r);
        var bundle = r.GetBool() ? r.GetString(512) : null;
        var n = r.GetInt();
        if (n < -1 || n > 4096) throw new InvalidDataException("Invalid cart line.");
        Il2CppSystem.Collections.Generic.List<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>> cables = null;
        if (n >= 0)
        {
            cables = new Il2CppSystem.Collections.Generic.List<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>>(n);
            for (var i = 0; i < n; i++) { var a = ReadInts(r); cables.Add(a == null ? null : new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(a)); }
        }
        if (r.AvailableBytes != 0) throw new InvalidDataException("Trailing cart line bytes.");
        ShopEntry e;
        if (bundle == null && cables == null)
        {
            var desc = PartsDatabase.s_instance?.GetDesc(id, null) ?? throw new InvalidDataException($"the part {id} isn't in this game's parts list");
            e = new ShopEntry(desc);
        }
        else e = new ShopEntry(id, cost, delivery, bundle, cables);
        e.m_cost = cost; e.m_delivery = delivery; e.m_inventory = stock;
        e.m_miscData = misc == null ? null : new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(misc);
        return e;
    }

    private static List<byte[]> ReadCart(ShopKind shop)
    {
        var list = new List<byte[]>();
        var t = Trolley(shop);
        if (t != null) foreach (var e in t) if (e != null) list.Add(WriteLine(e));
        return list;
    }

    private static bool Same(IReadOnlyList<byte[]> a, IReadOnlyList<byte[]> b) => a.Count == b.Count && a.Zip(b).All(p => p.First.AsSpan().SequenceEqual(p.Second));

    private void WriteCart(ShopKind shop, IReadOnlyList<byte[]> lines)
    {
        var t = Trolley(shop) ?? throw new InvalidDataException($"this game has no {shop} cart");
        var entries = lines.Select(ReadLine).ToList();
        NativeProjection.Run(() => { t.Clear(); foreach (var e in entries) t.Add(e); });
        _seen[shop] = ReadCart(shop);
        RefreshShops(shop);
    }

    /// <summary>Open shop screens show their cart again (the same list they read).</summary>
    private static void RefreshShops(ShopKind shop)
    {
        foreach (var s in UnityEngine.Object.FindObjectsOfType<Shop>())
        {
            if (s == null || (int)s.GetShopID() != (int)shop) continue;
            try { NativeProjection.Run(() => { s.UpdateTrolley(); if (s.InCheckout()) s.m_checkout?.ConstructCheckout(); }); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[shop] the {shop} screen could not show the new cart: {e.Message}"); }
        }
        if (shop != ShopKind.Cable) return;
        foreach (var c in UnityEngine.Object.FindObjectsOfType<CableApp>())
        {
            if (c == null) continue;
            try { NativeProjection.Run(c.UpdateTrolley); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[shop] the Cable app could not show the new cart: {e.Message}"); }
        }
    }

    // Job baskets: which cart items are for which job (a tag per accepted job or PC Bay repair).
    private static string TagKey(JobTag t) => $"{t.JobId}|{t.Offer?.m_id ?? ""}";

    private static Dictionary<string, (List<string> Basket, List<string> Water)> ReadBasketMap()
    {
        var map = new Dictionary<string, (List<string>, List<string>)>(StringComparer.Ordinal);
        var tags = Data?.m_jobTags;
        if (tags == null) return map;
        foreach (var t in tags)
        {
            if (t == null) continue;
            var basket = new List<string>(); if (t.InBasket != null) foreach (var s in t.InBasket) basket.Add(s ?? "");
            List<string> water = null;
            if (t.InBasketWater != null) { water = new List<string>(); foreach (var s in t.InBasketWater) water.Add(s ?? ""); }
            map[TagKey(t)] = (basket, water);
        }
        return map;
    }

    internal static byte[] EncodeBaskets(Dictionary<string, (List<string> Basket, List<string> Water)> map)
    {
        var w = new NetDataWriter();
        w.Put(map.Count);
        foreach (var (key, (basket, water)) in map.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            w.Put(key); w.Put(basket.Count); foreach (var s in basket) w.Put(s);
            w.Put(water == null ? -1 : water.Count); if (water != null) foreach (var s in water) w.Put(s);
        }
        return w.CopyData();
    }

    internal static Dictionary<string, (List<string> Basket, List<string> Water)> DecodeBaskets(byte[] data)
    {
        var map = new Dictionary<string, (List<string>, List<string>)>(StringComparer.Ordinal);
        if (data.Length == 0) return map;
        var r = new NetDataReader(data);
        var n = r.GetInt();
        if (n is < 0 or > 512) throw new InvalidDataException("Invalid job baskets.");
        for (var i = 0; i < n; i++)
        {
            var key = r.GetString(256);
            var basket = new List<string>(); var k = r.GetInt();
            if (k is < 0 or > 512) throw new InvalidDataException("Invalid job basket.");
            for (var j = 0; j < k; j++) basket.Add(r.GetString(512));
            List<string> water = null; k = r.GetInt();
            if (k < -1 || k > 512) throw new InvalidDataException("Invalid job basket.");
            if (k >= 0) { water = new List<string>(); for (var j = 0; j < k; j++) water.Add(r.GetString(512)); }
            map[key] = (basket, water);
        }
        if (r.AvailableBytes != 0) throw new InvalidDataException("Trailing job basket bytes.");
        return map;
    }

    /// <summary>The shared baskets on this game's job tags. A job this game doesn't have (the host took it today)
    /// keeps its basket in the model only.</summary>
    private void WriteBaskets(byte[] blob)
    {
        var map = DecodeBaskets(blob);
        var tags = Data?.m_jobTags;
        if (tags != null)
            NativeProjection.Run(() =>
            {
                foreach (var t in tags)
                {
                    if (t == null || !map.TryGetValue(TagKey(t), out var b)) continue;
                    t.InBasket ??= new Il2CppSystem.Collections.Generic.List<string>();
                    t.InBasket.Clear(); foreach (var s in b.Basket) t.InBasket.Add(s);
                    if (b.Water == null) t.InBasketWater = null;
                    else { t.InBasketWater ??= new Il2CppSystem.Collections.Generic.List<string>(); t.InBasketWater.Clear(); foreach (var s in b.Water) t.InBasketWater.Add(s); }
                }
            });
        _seenBaskets = EncodeBaskets(ReadBasketMap());
    }

    // ---------------- binding ----------------

    internal void BindHost()
    {
        if (!NativeCareer.InCareer) return;
        Clear();
        var carts = new Dictionary<ShopKind, IReadOnlyList<byte[]>>();
        foreach (var shop in Shops)
        {
            var lines = ReadCart(shop);
            if (!CommerceWorld.LinesValid(lines))
            {
                Plugin.Logger.LogWarning($"[shop] the {shop} cart is too large to share ({lines.Count} lines); it starts empty in co-op.");
                lines = new List<byte[]>();
            }
            carts[shop] = lines; _seen[shop] = lines;
        }
        var baskets = EncodeBaskets(ReadBasketMap());
        if (baskets.Length > CommerceWorld.MaxBasketBytes) { Plugin.Logger.LogWarning("[shop] the job baskets are too large to share."); baskets = EncodeBaskets(new()); }
        Handler.BindHost(carts, baskets);
        _seenBaskets = baskets;
        Plugin.Logger.LogInfo($"[shop] host bound the carts: {string.Join(", ", carts.Select(c => $"{c.Key} {c.Value.Count}"))}.");
    }

    /// <summary>Guest, once admitted: its carts and baskets become the host's.</summary>
    internal void BindGuest()
    {
        if (!NativeCareer.InCareer) return;
        Clear();
        if (!Handler.Bound) { Plugin.Logger.LogWarning("[shop] the host's world has no shared shop state; carts aren't shared."); return; }
        try
        {
            foreach (var shop in Shops)
            {
                var model = Handler.Cart(shop).Lines;
                if (Same(ReadCart(shop), model)) { _seen[shop] = model.ToList(); continue; }
                WriteCart(shop, model);
                Plugin.Logger.LogInfo($"[shop] the {shop} cart follows the host's ({model.Count} lines).");
            }
            WriteBaskets(Handler.Baskets);
        }
        catch (Exception e) { _coop.ProjectionFailed("This game's carts could not follow the host's", e); }
    }

    // ---------------- per frame: local edits ----------------

    public void Update()
    {
        if (!Active) return;
        var now = Time.realtimeSinceStartup;
        foreach (var (op, p) in _pending.ToList())
            if (now - p.At > PendingTimeout) { _pending.Remove(op); Plugin.Logger.LogWarning($"[shop] {p.What} wasn't answered; continuing."); }
        if (now < _nextPoll) return;
        _nextPoll = now + PollEvery;
        try
        {
            foreach (var shop in Shops)
            {
                if (_pending.Values.Any(p => p.What == "cart" && p.Shop == shop)) continue;
                var lines = ReadCart(shop);
                if (_seen.TryGetValue(shop, out var seen) && Same(lines, seen)) continue;
                if (!CommerceWorld.LinesValid(lines))
                {
                    _coop.Notice("That cart is too large to share in co-op. Remove some items.");
                    WriteCart(shop, Handler.Cart(shop).Lines);
                    continue;
                }
                _seen[shop] = lines;
                if (!Send(CommerceWorld.SetCart(Epoch, shop, Handler.Cart(shop).Revision, lines), "cart", shop)) WriteCart(shop, Handler.Cart(shop).Lines);
            }
            if (!_pending.Values.Any(p => p.What == "baskets"))
            {
                var mine = ReadBasketMap();
                var encoded = EncodeBaskets(mine);
                if (!encoded.AsSpan().SequenceEqual(_seenBaskets))
                {
                    // Keep the baskets of jobs this game doesn't have.
                    var merged = DecodeBaskets(Handler.Baskets);
                    foreach (var (k, v) in mine) merged[k] = v;
                    var blob = EncodeBaskets(merged);
                    _seenBaskets = encoded;
                    if (blob.Length > CommerceWorld.MaxBasketBytes || !Send(CommerceWorld.SetBaskets(Epoch, Handler.BasketRevision, blob), "baskets", default))
                        WriteBaskets(Handler.Baskets);
                }
            }
        }
        catch (Exception e) { _coop.ProjectionFailed("A cart change could not be shared", e); }
    }

    /// <summary>The command being submitted: the host answers its own inside Submit, before it could be recorded.</summary>
    private (string What, ShopKind Shop)? _sending;

    /// <summary>Submit one shop command; a guest's waits for the host's answer in <see cref="_pending"/>.</summary>
    private bool Send(byte[] payload, string what, ShopKind shop)
    {
        if (payload.Length > Protocol.MaxPayloadBytes) return false;
        _sending = (what, shop);
        try
        {
            if (!_world.Submit(CommandTypes.Commerce, payload, out var op)) return false;
            if (_world.Local != ActorSlot.Host) _pending[op] = (what, shop, Time.realtimeSinceStartup);
            return true;
        }
        finally { _sending = null; }
    }

    private void OnRejected(OperationId op, RejectReason reason, string text)
    {
        if (!_pending.Remove(op, out var p))
        {
            if (op.Actor != _world.Local || _sending is not { } s) return;
            p = (s.What, s.Shop, 0);
        }
        try
        {
            switch (p.What)
            {
                case "cart":
                    WriteCart(p.Shop, Handler.Cart(p.Shop).Lines);
                    // The Cable app is on a shared desktop: the other game's mirrored click made the same edit.
                    if (p.Shop != ShopKind.Cable) _coop.Notice("The other player changed that cart at the same moment; it shows theirs.");
                    break;
                case "baskets":
                    WriteBaskets(Handler.Baskets);
                    break;
                case "order":
                    _coop.Notice("The host couldn't place the order: " + text);
                    break;
                case "sale":
                    _coop.Notice("The host couldn't sell that: " + text);
                    NativeCareer.Current?.Recheck();
                    break;
            }
            Plugin.Logger.LogInfo($"[shop] {p.What} refused: {text}");
        }
        catch (Exception e) { _coop.ProjectionFailed("A refused cart change could not be undone", e); }
    }

    // ---------------- guest requests ----------------

    private bool ReadyToSend(ShopKind shop)
    {
        if (!Active) { _coop.Notice("Shopping isn't shared yet; try again in a moment."); return false; }
        if (_pending.Values.Any(p => p.What == "order")) { _coop.Notice("Your last order is still on its way to the host."); return false; }
        if (!CartSettled(shop) || _pending.Values.Any(p => p.What == "baskets"))
        {
            _coop.Notice("The cart is still being shared; press Buy again in a moment.");
            return false;
        }
        return true;
    }

    /// <summary>This game's cart for <paramref name="shop"/> shows the model's latest revision: no edit of it is waiting
    /// to be sent or answered. (Compared with what this game last read or wrote, not the model's bytes: a written line
    /// may read back slightly differently.)</summary>
    private bool CartSettled(ShopKind shop) =>
        !_pending.Values.Any(p => p.What == "cart" && p.Shop == shop) && _seen.TryGetValue(shop, out var seen) && Same(ReadCart(shop), seen);

    private void SendOrder(ShopOrder order)
    {
        if (!Send(CommerceWorld.Order(Epoch, order), "order", order.Shop)) { _coop.Notice("Not connected to the host."); return; }
        _coop.Notice("Placing your order with the host…");
        Plugin.Logger.LogInfo($"[shop] sent the {order.Shop} order: {order.Lines} line(s), total {order.Total}.");
    }

    /// <summary>Guest pressed Buy in a tablet shop.</summary>
    internal void RequestOrder(Checkout checkout)
    {
        var shop = (ShopKind)(int)(checkout?.m_shopApp?.GetShopID() ?? 0);
        if (!CommerceWorld.IsShop(shop) || shop == ShopKind.Cable) { _coop.Notice("That shop isn't shared in co-op yet."); return; }
        if (!ReadyToSend(shop)) return;
        var d = checkout.GetDeliveryType();
        var cart = Handler.Cart(shop);
        SendOrder(new ShopOrder(shop, cart.Revision, Handler.BasketRevision, cart.Lines.Count, checkout.m_checkoutTotal,
            d == null ? new DeliveryChoice(0, 0, 0) : new DeliveryChoice(d.m_cost, d.m_minWorkingDays, d.m_maxWorkingDays), default));
    }

    /// <summary>Guest pressed Buy in a shared PC's Cable app.</summary>
    internal void RequestCableOrder(CableApp app)
    {
        var record = app?.m_virtualComputer?.ComputerSave;
        var pc = record == null ? default : _coop.Objects.IdOf(record);
        if (pc.IsNone) { _coop.Notice("Buy cables at a shared workshop PC in co-op."); return; }
        if (!ReadyToSend(ShopKind.Cable)) return;
        var cart = Handler.Cart(ShopKind.Cable);
        var total = cart.Lines.Sum(l => ReadLine(l).m_cost);
        SendOrder(new ShopOrder(ShopKind.Cable, cart.Revision, Handler.BasketRevision, cart.Lines.Count, total, new DeliveryChoice(0, 0, 0), pc));
    }

    /// <summary>Guest sells owned items (one from the inventory, or every broken one).</summary>
    internal void RequestSale(IReadOnlyList<ulong> items)
    {
        if (!Active) { _coop.Notice("Selling isn't shared yet; try again in a moment."); return; }
        if (items.Count == 0) return;
        if (items.Count > CommerceWorld.MaxSold) items = items.Take(CommerceWorld.MaxSold).ToList();
        if (!Send(CommerceWorld.Sell(Epoch, items), "sale", default)) { _coop.Notice("Not connected to the host."); return; }
        Plugin.Logger.LogInfo($"[shop] asked the host to sell {items.Count} item(s).");
    }

    // ---------------- host checks ----------------

    /// <summary>What the game charges for these lines (decompile, <c>Checkout.UpdateCost</c>): their prices, plus the
    /// delivery when any line isn't available at once.</summary>
    private static int TotalOf(IReadOnlyList<ShopEntry> lines, DeliveryChoice delivery) =>
        lines.Sum(e => e.m_cost) + (lines.Any(e => e.m_part != null && !e.m_part.ImmediateDelivery()) ? delivery.Cost : 0);

    private static CableApp CableAppOf(CoopController coop, NetObjectId pc)
    {
        foreach (var c in UnityEngine.Object.FindObjectsOfType<CableApp>())
        {
            var record = c?.m_virtualComputer?.ComputerSave;
            if (record != null && coop.Objects.IdOf(record) == pc) return c;
        }
        return null;
    }

    private string CheckOrder(ShopOrder order, IReadOnlyList<byte[]> lines)
    {
        try
        {
            if (!CartSettled(order.Shop)) return "The cart changed on the host's game just now. Check it and buy again.";
            var entries = new List<ShopEntry>();
            if (Trolley(order.Shop) is { } trolley) foreach (var e in trolley) if (e != null) entries.Add(e);
            if (entries.Count != lines.Count) return "The cart changed on the host's game just now. Check it and buy again.";
            var cash = Data?.m_cash ?? 0;
            if (order.Shop == ShopKind.Cable)
            {
                if (CableAppOf(_coop, order.Pc) == null) return "The host's game doesn't show that PC's Cable app.";
                var sum = entries.Sum(e => e.m_cost);
                if (sum != order.Total) return $"The cables cost {sum} now. Check the cart and buy again.";
                return cash < sum ? "There isn't enough cash for this order." : null;
            }
            var total = TotalOf(entries, order.Delivery);
            if (total != order.Total) return $"The total is {total} on the host's game. Check the cart and buy again.";
            return cash < total ? "There isn't enough cash for this order." : null;
        }
        catch (Exception e) { return "The order could not be checked: " + e.Message; }
    }

    private string CheckSell(IReadOnlyList<ulong> items)
    {
        var career = NativeCareer.Current;
        if (career == null) return "The career isn't loaded.";
        var inUse = new HashSet<ulong>(_coop.Edits?.Handler.ItemsInUse(default) ?? Enumerable.Empty<ulong>());
        foreach (var id in items)
        {
            var part = career.Find(id);
            if (part == null) return "That part is no longer in the host's inventory.";
            if (inUse.Contains(id)) return "That part is being installed.";
            if (!part.CanSell()) return "That part can't be sold.";
        }
        return null;
    }

    // ---------------- ICommerceNative ----------------

    public void CartChanged(in MutationContext ctx, ShopKind shop)
    {
        if (ctx.FromLocalPlayer) _pending.Remove(ctx.Op);
        var model = Handler.Cart(shop).Lines;
        if (_seen.TryGetValue(shop, out var seen) && Same(seen, model) && Same(ReadCart(shop), model)) return;
        try { WriteCart(shop, model); }
        catch (Exception e) { _coop.ProjectionFailed($"The shared {shop} cart could not be shown on this game", e); }
    }

    public void BasketsChanged(in MutationContext ctx)
    {
        if (ctx.FromLocalPlayer) { _pending.Remove(ctx.Op); return; }
        try { WriteBaskets(Handler.Baskets); }
        catch (Exception e) { _coop.ProjectionFailed("The shared job baskets could not be shown on this game", e); }
    }

    public void Ordered(in MutationContext ctx, ShopOrder order, IReadOnlyList<byte[]> lines)
    {
        if (ctx.FromLocalPlayer && _pending.Remove(ctx.Op)) _coop.Notice($"Order placed (${order.Total}).");
        if (_world.Local != ActorSlot.Host || ctx.Op.Actor == ActorSlot.Host) return;
        try
        {
            if (order.Shop == ShopKind.Cable) PlaceCables(order);
            else PlaceShopOrder(order);
            _coop.AddChat($"Client bought {lines.Count} item(s) (${order.Total}).");
        }
        catch (Exception e) { _coop.ProjectionFailed("The client's order could not be placed", e); }
    }

    /// <summary>Host: the game's checkout steps for the guest's order (decompile, <c>Checkout.OnBuy</c>).</summary>
    private void PlaceShopOrder(ShopOrder order)
    {
        var s = Status ?? throw new InvalidDataException("The career isn't loaded.");
        var cal = s.GetCalendar() ?? throw new InvalidDataException("The calendar isn't loaded.");
        var trolley = Trolley(order.Shop) ?? throw new InvalidDataException("The cart isn't loaded.");
        var entries = new List<ShopEntry>(); foreach (var e in trolley) if (e != null) entries.Add(e);
        var total = TotalOf(entries, order.Delivery);
        NativeProjection.Run(() =>
        {
            if (!s.SpendCash(total, false, eTransactionCategory.GOODS)) throw new InvalidDataException("the host's game refused the payment");
            var ids = new Il2CppSystem.Collections.Generic.List<string>();
            foreach (var e in entries) ids.Add(e.m_part?.m_id ?? e.PartID);
            s.TransferBasketToTransit(ids, (PartDesc.ShopID)(int)order.Shop);
            var days = UnityEngine.Random.Range(order.Delivery.MinDays, order.Delivery.MaxDays + 1);
            foreach (var e in entries)
            {
                var desc = e.m_part ?? throw new InvalidDataException($"the part {e.PartID} isn't in the host's parts list");
                if (desc.ImmediateDelivery())
                    s.AddItemToInventory(PartInstance.NewPartInstance(desc.m_id, 0, null), true, true, true, false);
                else if (days > 0)
                {
                    s.MarkEverInInventory(desc.m_id);
                    cal.ScheduleDelivery(days, desc.m_id, e.m_cost, true, null, false);
                }
                else
                {
                    // Same day: the delivery box. A case too: co-op makes PCs only through its own case spawn.
                    var part = PartInstance.NewPartInstance(desc.m_id, 0, null);
                    part.SetNew();
                    s.AddDelivery(part);
                }
            }
            trolley.Clear();
            s.UpdateInventory();
        });
        _seen[order.Shop] = new List<byte[]>();
        RefreshShops(order.Shop);
        Plugin.Logger.LogInfo($"[shop] placed the client's {order.Shop} order: {entries.Count} line(s), ${total}.");
    }

    /// <summary>Host: press Buy on the same shared PC's Cable app (its purchase path shares the cable sets as usual).</summary>
    private void PlaceCables(ShopOrder order)
    {
        var app = CableAppOf(_coop, order.Pc) ?? throw new InvalidDataException("the host's game doesn't show that PC's Cable app");
        app.OnBuy();
        _seen[ShopKind.Cable] = ReadCart(ShopKind.Cable);
        Plugin.Logger.LogInfo($"[shop] bought the client's cables on PC {order.Pc}.");
    }

    public void Sold(in MutationContext ctx, IReadOnlyList<ulong> items)
    {
        if (ctx.FromLocalPlayer && _pending.Remove(ctx.Op)) _coop.Notice(items.Count == 1 ? "Sold." : $"Sold {items.Count} items.");
        if (_world.Local != ActorSlot.Host || ctx.Op.Actor == ActorSlot.Host) return;
        try { NativeCareer.Current?.SellOwned(items); }
        catch (Exception e) { _coop.ProjectionFailed("The client's sale could not be made", e); }
    }
}

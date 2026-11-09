using System.Globalization;
using LiteNetLib.Utils;
using UnityEngine;
using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>
/// Phase 15c (owner decisions, November 8, 2026): the shared career on this game (<see cref="CareerWorld"/>, command 18).
/// <list type="bullet">
/// <item>Owned items: the game gives parts and peripherals in <c>CareerStatus.m_inventory</c> /
/// <c>m_peripheralInventory</c> no ID, so this binds each native <c>PartInstance</c> to a shared item ID. The host binds
/// when a world generation starts, keeping the IDs of a restored state where the items still match; a guest binds its
/// loaded game to the snapshot's items.</item>
/// <item>Host: whenever nothing is being edited, carried or used at a table, it compares its game with the model and
/// sends what changed: numbers, items gone, items gained (serialized whole, silicon lottery and all) and new ledger
/// entries. Its game is the reference.</item>
/// <item>Guest: takes the host's numbers and ledger entries at once, and brings its owned items in line with the model
/// when it is quiet (an install of an item already gone on the host must still find it). Owner decision: the host wins
/// quietly; an item that can't be matched or made reports out of sync for <c>/sync</c>.</item>
/// </list>
/// Only active in a co-op Career; a Free Build world leaves the model unbound.
/// </summary>
internal sealed class NativeCareer : ICareerNative
{
    private const float ObserveEvery = 0.5f, CheckEvery = 2f, PendingTimeout = 10f;

    private readonly CoopController _coop;
    private readonly WorldSync _world;
    public CareerWorld Handler { get; }
    internal static NativeCareer Current => CoopController.Instance?.Career;

    private readonly Dictionary<IntPtr, ulong> _ids = new();
    private readonly Dictionary<ulong, PartInstance> _natives = new();
    /// <summary>Items that recently left the lists (an install took them): their IDs stay known for the edit reporting it.</summary>
    private readonly Dictionary<IntPtr, ulong> _retired = new();
    private readonly Queue<IntPtr> _retiredOrder = new();
    /// <summary>Guest: the serialized items the host added, until this game has them.</summary>
    private readonly Dictionary<ulong, byte[]> _made = new();
    /// <summary>Guest: items this game used up (installed, made into a PC) and when; the host's update removing them follows.</summary>
    private readonly Dictionary<ulong, float> _gone = new();
    private const float GoneGrace = 20f;
    private float _nextObserve, _nextCheck, _pendingSince = -1;
    private bool _dirty;
    private int _lastDay = -1;
    private bool _dayEnded;

    public NativeCareer(CoopController coop, WorldSync world)
    {
        _coop = coop; _world = world;
        Handler = new CareerWorld(coop.Activity, this);
        world.GenerationStarted += (uint _, string _) => Clear();
        world.Rejected += (OperationId op, RejectReason reason, string text) =>
        {
            if (op.Actor == world.Local && text.Contains(Handler.Name)) { Plugin.Logger.LogWarning($"[career] {op} refused: {text}"); _pendingSince = -1; }
        };
    }

    private void Clear()
    {
        _ids.Clear(); _natives.Clear(); _retired.Clear(); _retiredOrder.Clear(); _made.Clear(); _gone.Clear();
        _pendingSince = -1; _dirty = false; _lastDay = -1; _dayEnded = false;
    }

    /// <summary>This game plays a co-op Career world.</summary>
    internal static bool InCareer => CoopSaves.Kind == CoopGameMode.Career && CoopSaves.Current != CoopSaves.Mode.None;
    private bool Active => InCareer && Handler.Bound && _world.Admitted && _world.Divergence == null && _coop.Objects.Initialized && _coop.WorldReady;

    /// <summary>Nothing on either game is mid-change: no edit, replay, table session, carry move or save.</summary>
    private bool Quiet
    {
        get
        {
            if (_coop.Replays.Busy || (_coop.Edits?.Handler.Edits.Count ?? 0) > 0 || SaveLoadSystem.WaitingOnThread()) return false;
            foreach (var actor in new[] { ActorSlot.Host, ActorSlot.Guest })
            {
                if (_coop.ToolBenches?.Handler.Of(actor) != null) return false;
                if (_coop.Activity?.Get(actor)?.Pending != null) return false;
            }
            return true;
        }
    }

    // ---------------- reading the game ----------------

    private static CareerStatus Status => CareerStatus.Get();
    /// <summary>The career's saved fields (cash, lists, ledger) live in <c>CareerStatus.State</c>.</summary>
    private static CareerStatus.State Data => CareerStatus.GetState();

    internal static string KeyOf(PartInstance p)
    {
        var c = CultureInfo.InvariantCulture;
        // Not the value: GetNewValue may follow the day's market rather than the item.
        var key = $"{PartVariants.Key(p)}|job {p.GetJobId()}|cond {(int)p.GetCondition()}|dirt {p.GetFilth().ToString("F3", c)}" +
                  $"|lot {p.GetSiliconLottery().ToString("R", c)}|bay {(p.m_PcBay ? 1 : 0)}|dec {p.GetDecorationId() ?? ""}";
        key = new string(key.Where(ch => !char.IsControl(ch)).ToArray());
        return key.Length <= OwnedItem.MaxKey ? key : key[..OwnedItem.MaxKey];
    }

    /// <summary>Every owned item on this game, list by list in the game's order.</summary>
    private static List<(PartInstance Part, ItemList List)> Natives()
    {
        var all = new List<(PartInstance, ItemList)>();
        var s = Status;
        if (s == null) return all;
        if (s.GetInventory() is { } main) foreach (var p in main) if (p != null) all.Add((p, ItemList.Main));
        if (s.GetPeripheralInventory() is { } perifs) foreach (var p in perifs) if (p != null) all.Add((p, ItemList.Peripheral));
        return all;
    }

    private static CareerFacts ReadFacts()
    {
        var s = Data ?? throw new InvalidDataException("The career isn't loaded.");
        var flags = CareerFlags.None;
        if (s.m_unlockedReviews) flags |= CareerFlags.UnlockedReviews;
        if (s.m_unlockedPCBay) flags |= CareerFlags.UnlockedPcBay;
        if (s.m_unlockedTablet) flags |= CareerFlags.UnlockedTablet;
        if (s.m_upgradedShop) flags |= CareerFlags.UpgradedShop;
        if (s.m_paidForShopUpgrade) flags |= CareerFlags.PaidForShopUpgrade;
        return new CareerFacts(s.m_cash, s.m_kudos, s.m_starRating, Status?.GetCalendar()?.GetToday() ?? 0, s.m_rent, s.m_rentCostCharged, s.m_utilityCost,
            s.m_utilityCostCharged, s.m_bankingCharges, s.m_sharesOwned, s.m_numberJobsCompleted, flags);
    }

    /// <summary>Guest: the host's numbers. The day isn't written: a new day reloads this game (owner decision).</summary>
    private static void WriteFacts(CareerFacts f)
    {
        var s = Data ?? throw new InvalidDataException("The career isn't loaded.");
        s.m_cash = f.Cash; s.m_kudos = f.Kudos; s.m_starRating = f.Rating; s.m_rent = f.Rent; s.m_rentCostCharged = f.RentCharged;
        s.m_utilityCost = f.Utility; s.m_utilityCostCharged = f.UtilityCharged; s.m_bankingCharges = f.BankCharges; s.m_sharesOwned = f.Shares;
        s.m_numberJobsCompleted = f.JobsCompleted;
        s.m_unlockedReviews = f.Flags.HasFlag(CareerFlags.UnlockedReviews);
        s.m_unlockedPCBay = f.Flags.HasFlag(CareerFlags.UnlockedPcBay);
        s.m_unlockedTablet = f.Flags.HasFlag(CareerFlags.UnlockedTablet);
        s.m_upgradedShop = f.Flags.HasFlag(CareerFlags.UpgradedShop);
        s.m_paidForShopUpgrade = f.Flags.HasFlag(CareerFlags.PaidForShopUpgrade);
    }

    private static int LedgerCount() => Data?.m_transactions?.Count ?? 0;

    internal static byte[] WriteEntry(Transaction t)
    {
        var w = new NetDataWriter();
        w.Put(t.m_price); w.Put(t.m_dateTime.Ticks); w.Put((int)t.m_transactionCategory); w.Put(t.m_resaleValue);
        w.Put(t.m_name ?? ""); w.Put(t.m_componentList ?? ""); w.Put(t.m_collected); w.Put(t.m_bongleRating);
        var save = t.m_save;
        w.Put(save != null);
        if (save != null) { var bytes = NativePcCodec.Write(save); w.Put(bytes.Length); w.Put(bytes); }
        return w.CopyData();
    }

    private static Transaction ReadEntry(byte[] data)
    {
        var r = new NetDataReader(data);
        var price = r.GetInt(); var ticks = r.GetLong(); var category = (eTransactionCategory)r.GetInt(); var resale = r.GetInt();
        var name = r.GetString(65535); var components = r.GetString(65535); var collected = r.GetBool(); var rating = r.GetFloat();
        ComputerSave save = null;
        if (r.GetBool())
        {
            var length = r.GetInt();
            if (length is < 1 or > CareerUpdate.MaxEntryBytes) throw new InvalidDataException("Invalid ledger PC record.");
            var bytes = new byte[length]; r.GetBytes(bytes, length);
            save = NativePcCodec.Read(bytes);
        }
        if (r.AvailableBytes != 0) throw new InvalidDataException("Trailing ledger bytes.");
        var when = new Il2CppSystem.DateTime(ticks);
        var t = save != null ? new Transaction(price, when, category, save) : new Transaction(price, when, category);
        t.m_resaleValue = resale; t.m_name = name; t.m_componentList = components; t.m_collected = collected; t.m_bongleRating = rating;
        return t;
    }

    private static byte[] LedgerHash(int count)
    {
        var hash = new byte[32];
        var list = Data?.m_transactions;
        if (list == null) return hash;
        for (var i = 0; i < Math.Min(count, list.Count); i++) if (list[i] is { } t) hash = CareerWorld.ChainLedger(hash, WriteEntry(t));
        return hash;
    }

    // ---------------- binding ----------------

    private void Bind(PartInstance p, ulong id)
    {
        _ids[p.Pointer] = id; _natives[id] = p;
        _retired.Remove(p.Pointer);
    }

    private void Unbind(ulong id)
    {
        if (!_natives.Remove(id, out var p)) return;
        _ids.Remove(p.Pointer);
        _retired[p.Pointer] = id; _retiredOrder.Enqueue(p.Pointer);
        while (_retiredOrder.Count > 256) _retired.Remove(_retiredOrder.Dequeue());
    }

    /// <summary>The shared ID of an owned item on this game (also one an install just took out of the inventory), or 0.</summary>
    internal ulong IdOf(PartInstance p)
    {
        if (p == null || !Handler.Bound) return 0;
        return _ids.TryGetValue(p.Pointer, out var id) || _retired.TryGetValue(p.Pointer, out id) ? id : 0;
    }

    /// <summary>The native item bound to <paramref name="id"/> on this game, or null.</summary>
    internal PartInstance Find(ulong id) => _natives.TryGetValue(id, out var p) && p != null ? p : null;

    /// <summary>The shared ID for acting on <paramref name="p"/> (a sale): its own, else that of a bound item equal to
    /// it (the inventory shows equal items as one row), or 0.</summary>
    internal ulong IdForUse(PartInstance p)
    {
        if (p == null || !Handler.Bound) return 0;
        if (_ids.TryGetValue(p.Pointer, out var id)) return id;
        var key = KeyOf(p);
        foreach (var (part, _) in Natives())
            if (_ids.TryGetValue(part.Pointer, out var other) && KeyOf(part) == key) return other;
        return 0;
    }

    /// <summary>The shared IDs of every broken part in the main inventory (what the game's Sell All Broken sells).</summary>
    internal List<ulong> BrokenItems() =>
        Natives().Where(n => n.List == ItemList.Main && n.Part.GetCondition() == PartCondition.BROKEN && _ids.ContainsKey(n.Part.Pointer))
            .Select(n => _ids[n.Part.Pointer]).ToList();

    /// <summary>Host: sell these owned items the way the game's Sell does (decompile, <c>ItemDisplay.SellItem</c>):
    /// their resale value as a part sale, then the exact item out. The next update shares both.</summary>
    internal void SellOwned(IReadOnlyList<ulong> ids)
    {
        var s = Status ?? throw new InvalidDataException("The career isn't loaded.");
        var sold = 0;
        foreach (var id in ids)
        {
            var part = Find(id);
            if (part == null) { Plugin.Logger.LogWarning($"[career] item {id} to sell is gone."); continue; }
            var list = Natives().FirstOrDefault(n => n.Part.Pointer == part.Pointer);
            if (list.Part == null) continue;
            var value = part.GetResaleValue();
            NativeProjection.Run(() => s.AddCash(value, eTransactionCategory.PART_SALE, null, false));
            Remove(s, part, list.List);
            sold++;
        }
        if (sold > 0) NativeProjection.Run(s.UpdateInventory);
        Plugin.Logger.LogInfo($"[career] sold {sold} item(s) for the client.");
    }

    /// <summary>Guest: compare the owned items with the model again soon (a refused sale).</summary>
    internal void Recheck() => _dirty = true;

    /// <summary>An accepted change used item <paramref name="id"/> up (a case made into a PC): take it out of this
    /// game's inventory. The host's next update removes it from the model.</summary>
    internal void Consume(ulong id, string why)
    {
        if (!InCareer || id == 0) return;
        _gone[id] = Time.realtimeSinceStartup;
        var part = Find(id);
        if (part == null) { Plugin.Logger.LogInfo($"[career] item {id} ({why}) is already gone here."); return; }
        var s = Status ?? throw new InvalidDataException("The career isn't loaded.");
        var list = Natives().FirstOrDefault(n => n.Part.Pointer == part.Pointer);
        if (list.Part != null) { Remove(s, part, list.List); NativeProjection.Run(s.UpdateInventory); }
        Unbind(id);
        Plugin.Logger.LogInfo($"[career] used up item {id} for {why}.");
    }

    /// <summary>Host, at a world generation start (after the restored state, if any): bind the career from its game.</summary>
    internal void BindHost()
    {
        if (!InCareer) return;
        var previous = Handler.Items.GroupBy(i => (i.List, i.Key)).ToDictionary(g => g.Key, g => new Queue<ulong>(g.Select(i => i.Id)));
        var next = Math.Max(1UL, Handler.NextItem);
        _ids.Clear(); _natives.Clear(); _retired.Clear(); _retiredOrder.Clear();
        var items = new List<OwnedItem>();
        var kept = 0;
        foreach (var (part, list) in Natives())
        {
            var key = KeyOf(part);
            ulong id;
            if (previous.TryGetValue((list, key), out var q) && q.Count > 0) { id = q.Dequeue(); kept++; }
            else id = next++;
            items.Add(new OwnedItem(id, list, key));
            Bind(part, id);
        }
        next = Math.Max(next, items.Count == 0 ? 1 : items.Max(i => i.Id) + 1);
        var count = LedgerCount();
        var facts = ReadFacts();
        Handler.BindHost(facts, items, count, LedgerHash(count), next);
        _lastDay = facts.Day;
        Plugin.Logger.LogInfo($"[career] host bound the career: {items.Count} owned items ({kept} kept their IDs), cash {facts.Cash}, kudos {facts.Kudos}, day {facts.Day}, {count} ledger entries.");
    }

    /// <summary>Guest, once admitted: bind its loaded game to the host's career. Differences are put right later (host wins).</summary>
    internal void BindGuest()
    {
        if (!InCareer) return;
        Clear();
        if (!Handler.Bound) { Plugin.Logger.LogWarning("[career] the host's world has no shared career state; nothing is shared."); return; }
        var wanted = Handler.Items.GroupBy(i => (i.List, i.Key)).ToDictionary(g => g.Key, g => new Queue<ulong>(g.Select(i => i.Id)));
        var bound = 0; var byOrder = 0;
        var left = new List<(PartInstance Part, ItemList List)>();
        foreach (var (part, list) in Natives())
        {
            if (wanted.TryGetValue((list, KeyOf(part)), out var q) && q.Count > 0) { Bind(part, q.Dequeue()); bound++; }
            else left.Add((part, list));
        }
        // Both games loaded the same save, so the lists are in the same order: an item whose details read differently
        // here (a float, a derived field) still pairs with the host's item of the same part at the same place.
        var open = Handler.Items.Where(i => !_natives.ContainsKey(i.Id)).ToList();
        foreach (var (part, list) in left.ToList())
        {
            var part0 = PartVariants.Key(part);
            var match = open.FindIndex(i => i.List == list && i.Key.Split('|')[0] == part0.Split('|')[0]);
            if (match < 0) continue;
            Plugin.Logger.LogInfo($"[career] bound {part0} by its place in the list (details differ: here '{KeyOf(part)}', host '{open[match].Key}').");
            Bind(part, open[match].Id); open.RemoveAt(match); left.Remove((part, list)); byOrder++;
        }
        bound += byOrder;
        var extra = left.Count;
        var missing = Handler.Items.Count - bound;
        var facts = ReadFacts();
        if (facts with { Day = 0 } != Handler.Facts with { Day = 0 })
        {
            Plugin.Logger.LogWarning($"[career] this game's numbers differ from the host's ({facts.Difference(Handler.Facts)}); taking the host's.");
            WriteFacts(Handler.Facts);
        }
        var count = LedgerCount();
        if (count != Handler.LedgerCount) Plugin.Logger.LogWarning($"[career] this game has {count} ledger entries, the host {Handler.LedgerCount}.");
        else if (!LedgerHash(count).AsSpan().SequenceEqual(Handler.LedgerHash)) Plugin.Logger.LogInfo("[career] the ledger has the same length as the host's but different contents (only checked).");
        _dirty = missing > 0 || extra > 0;
        _lastDay = Handler.Facts.Day;
        Plugin.Logger.LogInfo($"[career] bound to the host's career: {bound} owned items matched, {missing} missing here, {extra} extra here.");
    }

    // ---------------- per frame ----------------

    public void Update()
    {
        if (!Active) return;
        var now = Time.realtimeSinceStartup;
        if (_world.Local == ActorSlot.Host)
        {
            if (_pendingSince >= 0 && now - _pendingSince > PendingTimeout) { Plugin.Logger.LogWarning("[career] the last career update wasn't confirmed; comparing again."); _pendingSince = -1; }
            if (_pendingSince < 0 && now >= _nextObserve) { _nextObserve = now + ObserveEvery; if (Quiet) Observe(); }
            return;
        }
        if (now < _nextCheck && !_dirty) return;
        if (!Quiet) return;
        _nextCheck = now + CheckEvery;
        try
        {
            var facts = ReadFacts();
            if (facts with { Day = 0 } != Handler.Facts with { Day = 0 })
            {
                Plugin.Logger.LogInfo($"[career] this game's numbers drifted ({facts.Difference(Handler.Facts)}); taking the host's.");
                NativeProjection.Run(() => WriteFacts(Handler.Facts));
            }
            Reconcile();
        }
        catch (Exception e) { _coop.ProjectionFailed("This game's career could not follow the host's", e); }
    }

    /// <summary>Host, just before a save or a snapshot: share what its game changed so the saved career is current.</summary>
    internal void FlushHost()
    {
        if (Active && _world.Local == ActorSlot.Host && _pendingSince < 0 && Quiet) Observe();
    }

    /// <summary>Host: the game's day moved on since the last check (once per change). The controller saves and

    /// reloads the guest once the day has settled.</summary>
    internal bool TakeDayEnded() { var d = _dayEnded; _dayEnded = false; return d; }

    /// <summary>Host: send what its game changed since the model.</summary>
    private void Observe()
    {
        CareerUpdate update;
        try
        {
            var facts = ReadFacts();
            if (_lastDay >= 0 && facts.Day > _lastDay) { _dayEnded = true; Plugin.Logger.LogInfo($"[career] the day moved on ({_lastDay} -> {facts.Day})."); }
            _lastDay = facts.Day;

            var model = Handler.Items.ToDictionary(i => i.Id);
            var seen = new HashSet<ulong>();
            var removed = new List<ulong>();
            var added = new List<AddedItem>();
            var next = Handler.NextItem;
            var rebind = new List<(PartInstance Part, ulong Id)>();
            foreach (var (part, list) in Natives())
            {
                var key = KeyOf(part);
                if (_ids.TryGetValue(part.Pointer, out var id) && model.TryGetValue(id, out var item) && item.List == list && item.Key == key) { seen.Add(id); continue; }
                // Changed in place (cleaned, delidded, ...) or new: the same item under a new ID.
                var fresh = new OwnedItem(next++, list, key);
                added.Add(new AddedItem(fresh, PartVariants.Write(part)));
                rebind.Add((part, fresh.Id));
            }
            foreach (var item in Handler.Items) if (!seen.Contains(item.Id)) removed.Add(item.Id);
            var ledger = new List<byte[]>();
            var list2 = Data?.m_transactions;
            var count = list2?.Count ?? 0;
            if (count < Handler.LedgerCount) Plugin.Logger.LogWarning($"[career] the ledger shrank ({Handler.LedgerCount} -> {count}); only new entries are shared.");
            for (var i = Handler.LedgerCount; i < count; i++) if (list2[i] is { } t) ledger.Add(WriteEntry(t));
            update = new CareerUpdate(facts == Handler.Facts ? null : facts, removed, added, ledger);
            if (update.IsEmpty) return;
            if (Handler.CheckUpdate(update) is { } bad) throw new InvalidDataException(bad);
            var host = _coop.Activity.Get(ActorSlot.Host) ?? throw new InvalidDataException("the host isn't in the shared world");
            // Before submitting: the host's own commands are answered inside Submit, which clears this again.
            _pendingSince = Time.realtimeSinceStartup;
            foreach (var payload in CareerWorld.Submissions(host.Epoch, update))
                if (!_world.Submit(CommandTypes.Career, payload, out _)) { _pendingSince = -1; throw new InvalidDataException("not connected to the shared world"); }
            foreach (var id in removed) Unbind(id);
            foreach (var (part, id) in rebind) { if (_ids.TryGetValue(part.Pointer, out var old)) Unbind(old); Bind(part, id); }
            Plugin.Logger.LogInfo($"[career] shared: {(update.Facts == null ? "" : facts.Difference(Handler.Facts) + "; ")}{removed.Count} item(s) out, {added.Count} in, {ledger.Count} ledger entr{(ledger.Count == 1 ? "y" : "ies")}.");
        }
        catch (Exception e) { _coop.ProjectionFailed("The host's career change could not be shared", e); }
    }

    // ---------------- ICareerNative ----------------

    public void Updated(in MutationContext ctx, CareerUpdate update)
    {
        if (ctx.FromLocalPlayer) { _pendingSince = -1; return; }
        try
        {
            if (update.Facts != null) NativeProjection.Run(() => WriteFacts(update.Facts));
            foreach (var a in update.Added) _made[a.Item.Id] = a.Native;
        foreach (var id in update.Removed) { _gone.Remove(id); _made.Remove(id); }

            var list = Data?.m_transactions ?? throw new InvalidDataException("The career isn't loaded.");
            foreach (var e in update.Ledger) { var t = ReadEntry(e); NativeProjection.Run(() => list.Add(t)); }
            if (update.Removed.Count > 0 || update.Added.Count > 0) _dirty = true;
            Plugin.Logger.LogInfo($"[career] the host's change is here: {update.Removed.Count} item(s) out, {update.Added.Count} in, {update.Ledger.Count} ledger entries{(update.Facts == null ? "" : $", cash {update.Facts.Cash}, kudos {update.Facts.Kudos}")}.");
        }
        catch (Exception e) { _coop.ProjectionFailed("The host's career change could not be shown on this game", e); }
    }

    // ---------------- guest: items follow the model ----------------

    /// <summary>Guest: make this game's owned items the model's (host wins).</summary>
    private void Reconcile()
    {
        var s = Status ?? throw new InvalidDataException("The career isn't loaded.");
        var natives = Natives();
        var present = new HashSet<IntPtr>(natives.Select(n => n.Part.Pointer));
        var wanted = Handler.Items.ToDictionary(i => i.Id);
        var changes = 0;

        // Bound items the host no longer has, and bindings whose item left the lists (installed, or gone here).
        foreach (var id in _natives.Keys.ToList())
        {
            var part = _natives[id];
            if (!present.Contains(part.Pointer)) { Unbind(id); _gone.TryAdd(id, Time.realtimeSinceStartup); continue; }
            if (wanted.ContainsKey(id)) continue;
            Remove(s, part, natives.First(n => n.Part.Pointer == part.Pointer).List);
            Unbind(id); changes++;
        }
        // Model items this game hasn't bound: an equal unbound item here, else the host's own item.
        var unbound = natives.Where(n => !_ids.ContainsKey(n.Part.Pointer)).Select(n => (n.Part, n.List, Key: KeyOf(n.Part))).ToList();
        foreach (var item in Handler.Items)
        {
            if (_natives.ContainsKey(item.Id)) continue;
            var match = unbound.FindIndex(u => u.List == item.List && u.Key == item.Key);
            if (match >= 0) { Bind(unbound[match].Part, item.Id); unbound.RemoveAt(match); continue; }
            if (!_made.TryGetValue(item.Id, out var bytes))
            {
                // This game used it up just now (an install or a new PC); the host's update removing it is on its way.
                if (_gone.TryGetValue(item.Id, out var at) && Time.realtimeSinceStartup - at < GoneGrace) { _dirty = true; continue; }
                throw new InvalidDataException($"the host's {item.Key.Split('|')[0]} (item {item.Id}) isn't in this game's inventory");
            }
            var part = PartVariants.Read(bytes);
            Add(s, part, item.List);
            Bind(part, item.Id); _made.Remove(item.Id); changes++;
        }
        // Whatever is left here wasn't shared: the host's game doesn't have it.
        foreach (var (part, list, key) in unbound)
        {
            Plugin.Logger.LogInfo($"[career] removed {key.Split('|')[0]} from this game: the host's career doesn't have it.");
            Remove(s, part, list); changes++;
        }
        if (changes > 0)
        {
            NativeProjection.Run(s.UpdateInventory);
            Plugin.Logger.LogInfo($"[career] owned items follow the host's ({changes} change(s), {Handler.Items.Count} items).");
        }
        _dirty = false;
    }

    /// <summary>Take exactly this object out of the lists. Not <c>RemoveItemFromInventory</c>: it may take an equal item
    /// (the game compares items with <c>IsSameAs</c>), and equal items are separate shared items here.</summary>
    private static void Remove(CareerStatus s, PartInstance part, ItemList list)
    {
        var d = Data ?? throw new InvalidDataException("The career isn't loaded.");
        NativeProjection.Run(() =>
        {
            foreach (var l in new[] { d.m_inventory, d.m_peripheralInventory })
                for (var i = l.Count - 1; i >= 0; i--)
                    if (l[i] != null && l[i].Pointer == part.Pointer) l.RemoveAt(i);
        });
    }

    private static void Add(CareerStatus s, PartInstance part, ItemList list)
    {
        var d = Data ?? throw new InvalidDataException("The career isn't loaded.");
        NativeProjection.Run(() =>
        {
            s.AddItemToInventory(part, false, false, true, true);
            // Put it in the list the host has it in, whichever list the game chose.
            var inMain = d.m_inventory.Contains(part); var inPerif = d.m_peripheralInventory.Contains(part);
            if (list == ItemList.Main && !inMain) { if (inPerif) d.m_peripheralInventory.Remove(part); d.m_inventory.Add(part); }
            if (list == ItemList.Peripheral && !inPerif) { if (inMain) d.m_inventory.Remove(part); d.m_peripheralInventory.Add(part); }
        });
    }
}

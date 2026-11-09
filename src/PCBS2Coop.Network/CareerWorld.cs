using System.Security.Cryptography;
using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>Career flags shared between both games (the game's <c>CareerStatus</c> booleans).</summary>
[Flags]
public enum CareerFlags
{
    None = 0,
    UnlockedReviews = 1,
    UnlockedPcBay = 2,
    UnlockedTablet = 4,
    UpgradedShop = 8,
    PaidForShopUpgrade = 16,
}

/// <summary>The career's shared numbers, as the host's game holds them (<c>CareerStatus</c> and its calendar).</summary>
public sealed record CareerFacts(int Cash, int Kudos, float Rating, int Day, int Rent, int RentCharged, int Utility, int UtilityCharged,
    int BankCharges, int Shares, int JobsCompleted, CareerFlags Flags)
{
    public static CareerFacts Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, CareerFlags.None);

    public bool IsValid => float.IsFinite(Rating) && Rating is >= -1000 and <= 1000 && Day >= 0 && Kudos >= 0 && JobsCompleted >= 0 &&
        ((int)Flags & ~31) == 0;

    internal void Write(NetDataWriter w)
    {
        w.Put(Cash); w.Put(Kudos); w.Put(Rating); w.Put(Day); w.Put(Rent); w.Put(RentCharged); w.Put(Utility); w.Put(UtilityCharged);
        w.Put(BankCharges); w.Put(Shares); w.Put(JobsCompleted); w.Put((int)Flags);
    }

    internal static CareerFacts Read(NetDataReader r)
    {
        var f = new CareerFacts(r.GetInt(), r.GetInt(), r.GetFloat(), r.GetInt(), r.GetInt(), r.GetInt(), r.GetInt(), r.GetInt(), r.GetInt(), r.GetInt(),
            r.GetInt(), (CareerFlags)r.GetInt());
        return f.IsValid ? f : throw new FormatException("Invalid career numbers.");
    }

    /// <summary>The names of the numbers that differ (logs).</summary>
    public string Difference(CareerFacts other)
    {
        var d = new List<string>();
        void Add(string name, object a, object b) { if (!Equals(a, b)) d.Add($"{name} {a} -> {b}"); }
        Add("cash", Cash, other.Cash); Add("kudos", Kudos, other.Kudos); Add("rating", Rating, other.Rating); Add("day", Day, other.Day);
        Add("rent", Rent, other.Rent); Add("rent charged", RentCharged, other.RentCharged); Add("utility", Utility, other.Utility);
        Add("utility charged", UtilityCharged, other.UtilityCharged); Add("bank charges", BankCharges, other.BankCharges);
        Add("shares", Shares, other.Shares); Add("jobs completed", JobsCompleted, other.JobsCompleted); Add("flags", Flags, other.Flags);
        return string.Join(", ", d);
    }
}

/// <summary>The game's two owned-item lists in <c>CareerStatus</c>: parts and peripherals.</summary>
public enum ItemList : byte { Main = 1, Peripheral = 2 }

/// <summary>One owned item. <see cref="Key"/> is what tells otherwise equal items apart on both games (part, variant,
/// job, condition, dirt, value, silicon lottery, PC Bay origin); the game gives items no ID of its own.</summary>
public sealed record OwnedItem(ulong Id, ItemList List, string Key)
{
    public const int MaxKey = 512;
    public bool IsValid => Id != 0 && Enum.IsDefined(List) && !string.IsNullOrEmpty(Key) && Key.Length <= MaxKey && !Key.Any(char.IsControl);
}

/// <summary>An item the host's game gained, with its serialized native form so the guest can make the same one.</summary>
public sealed record AddedItem(OwnedItem Item, byte[] Native);

/// <summary>One host update: the new numbers (if they changed), the items gone and the items gained, and new ledger
/// entries (serialized by the plugin) in the order the host's game added them.</summary>
public sealed record CareerUpdate(CareerFacts? Facts, IReadOnlyList<ulong> Removed, IReadOnlyList<AddedItem> Added, IReadOnlyList<byte[]> Ledger)
{
    public const int MaxItems = 4096, MaxItemBytes = 512 * 1024, MaxLedger = 512, MaxEntryBytes = CoopSavePair.MaxCarrierBytes + 64 * 1024;
    public bool IsEmpty => Facts == null && Removed.Count == 0 && Added.Count == 0 && Ledger.Count == 0;

    public byte[] Encode()
    {
        var w = new NetDataWriter();
        w.Put(Facts != null); Facts?.Write(w);
        w.Put(Removed.Count); foreach (var id in Removed) w.Put(id);
        w.Put(Added.Count);
        foreach (var a in Added) { w.Put(a.Item.Id); w.Put((byte)a.Item.List); w.Put(a.Item.Key, OwnedItem.MaxKey); Protocol.WriteBlob(w, a.Native); }
        w.Put(Ledger.Count); foreach (var e in Ledger) Protocol.WriteBlob(w, e);
        return w.CopyData();
    }

    public static CareerUpdate Decode(byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            var facts = r.GetBool() ? CareerFacts.Read(r) : null;
            var n = r.GetInt(); if (n is < 0 or > MaxItems) throw new FormatException("Too many removed items.");
            var removed = new ulong[n]; for (var i = 0; i < n; i++) removed[i] = r.GetULong();
            n = r.GetInt(); if (n is < 0 or > MaxItems) throw new FormatException("Too many added items.");
            var added = new AddedItem[n];
            for (var i = 0; i < n; i++)
            {
                var item = new OwnedItem(r.GetULong(), (ItemList)r.GetByte(), r.GetString(OwnedItem.MaxKey));
                var native = Protocol.ReadBlob(r, MaxItemBytes, out _);
                if (!item.IsValid || native is not { Length: > 0 }) throw new FormatException("Invalid owned item.");
                added[i] = new AddedItem(item, native);
            }
            n = r.GetInt(); if (n is < 0 or > MaxLedger) throw new FormatException("Too many ledger entries.");
            var ledger = new byte[n][];
            for (var i = 0; i < n; i++) ledger[i] = Protocol.ReadBlob(r, MaxEntryBytes, out _) is { Length: > 0 } e ? e : throw new FormatException("Invalid ledger entry.");
            if (r.AvailableBytes != 0) throw new FormatException("Trailing career bytes.");
            return new CareerUpdate(facts, removed, added, ledger);
        }
        catch (Exception e) when (Protocol.IsReadError(e)) { throw new FormatException("Malformed career update.", e); }
    }
}

/// <summary>Native side of the shared career. Runs on both peers in host order and must not submit commands.</summary>
public interface ICareerNative
{
    /// <summary>The host's update is accepted. The host's game already shows it.</summary>
    void Updated(in MutationContext ctx, CareerUpdate update);
}

/// <summary>
/// Phase 15c (owner decisions, November 8, 2026): one shared career, kept by the host.
/// <list type="bullet">
/// <item>The model holds the career's numbers (<see cref="CareerFacts"/>), an ID for every owned item and the ledger's
/// length and running hash. The host binds it from its game when a world generation starts
/// (<see cref="BindHost"/>); it travels with snapshots and saves, and a joining guest checks its loaded game against it.</item>
/// <item>Only the host changes it, with <b>Update</b>s of what its game changed (chunked like other large results:
/// <b>Chunk</b>s, then <b>Commit</b> with the length and SHA-256). Until phases 16–17 share them as actions, the host
/// alone shops, takes jobs and ends the day (the guest is refused), and the guest takes the results. Where the
/// guest's game disagrees, the host wins.</item>
/// <item>A component install names the exact item it uses (<see cref="ComponentEditWorld"/>); <see cref="CheckUse"/>
/// refuses an item that is gone or already being installed.</item>
/// </list>
/// </summary>
public sealed class CareerWorld : ICommandHandler, IWorldState
{
    private enum Step : byte { Chunk = 1, Commit = 2 }
    public const int MaxChunkBytes = 15 * 1024, MaxBytes = 8 * 1024 * 1024, MaxChunks = MaxBytes / MaxChunkBytes + 1, MaxOwned = 20000;

    private readonly ActivityWorld _activity;
    private readonly ICareerNative _native;
    private readonly List<OwnedItem> _items = new();
    private (ulong Epoch, int Total, List<byte[]> Chunks)? _chunks;

    public CareerWorld(ActivityWorld activity, ICareerNative native) { _activity = activity; _native = native; }
    public string Name => "career";

    /// <summary>The model was bound from the host's game (always for a Career world once it starts).</summary>
    public bool Bound { get; private set; }
    public CareerFacts Facts { get; private set; } = CareerFacts.Empty;
    public IReadOnlyList<OwnedItem> Items => _items;
    /// <summary>The next item ID the host gives out.</summary>
    public ulong NextItem { get; private set; } = 1;
    public int LedgerCount { get; private set; }
    public byte[] LedgerHash { get; private set; } = new byte[32];

    public OwnedItem? Item(ulong id) => _items.FirstOrDefault(i => i.Id == id);

    /// <summary>A generation start or a state read: the host binds again (keeping a restored state's IDs where items match).</summary>
    public void Reset()
    {
        _chunks = null; _items.Clear(); Bound = false; Facts = CareerFacts.Empty; NextItem = 1; LedgerCount = 0; LedgerHash = new byte[32];
    }

    // ---------------- host binding ----------------

    /// <summary>Host, when a world generation starts: the career as its game holds it. Existing item IDs (from a
    /// restored state) are kept where the host's binding matched them; new ones count on from <see cref="NextItem"/>.</summary>
    public void BindHost(CareerFacts facts, IEnumerable<OwnedItem> items, int ledgerCount, byte[] ledgerHash, ulong nextItem)
    {
        var list = items.ToList();
        if (!facts.IsValid || list.Count > MaxOwned || list.Any(i => !i.IsValid) || list.Select(i => i.Id).Distinct().Count() != list.Count ||
            ledgerCount < 0 || ledgerHash.Length != 32 || list.Any(i => i.Id >= nextItem))
            throw new InvalidDataException("Invalid career binding.");
        Facts = facts; _items.Clear(); _items.AddRange(list); LedgerCount = ledgerCount; LedgerHash = ledgerHash.ToArray(); NextItem = nextItem;
        Bound = true;
    }

    /// <summary>The running ledger hash after <paramref name="entry"/>.</summary>
    public static byte[] ChainLedger(byte[] previous, byte[] entry)
    {
        var data = new byte[previous.Length + entry.Length];
        previous.CopyTo(data, 0); entry.CopyTo(data, previous.Length);
        return SHA256.HashData(data);
    }

    // ---------------- reservations ----------------

    /// <summary>Why <paramref name="item"/> can't be installed now (it is gone, or <paramref name="inUse"/> already holds
    /// it), or null.</summary>
    public string? CheckUse(ulong item, IEnumerable<ulong> inUse)
    {
        if (!Bound) return null;
        if (Item(item) == null) return "That part is no longer in the shared inventory.";
        return inUse.Contains(item) ? "The other player is already installing that part." : null;
    }

    // ---------------- commands ----------------

    private static byte[] Encode(Action<NetDataWriter> f) { var w = new NetDataWriter(); f(w); return w.CopyData(); }

    /// <summary>Host: one update as chunks then the commit, ready to submit in order.</summary>
    public static IReadOnlyList<byte[]> Submissions(ulong epoch, CareerUpdate update)
    {
        var data = update.Encode();
        if (data.Length > MaxBytes) throw new InvalidDataException("The career update is too large to share.");
        var total = Math.Max(1, (data.Length + MaxChunkBytes - 1) / MaxChunkBytes);
        var list = new List<byte[]>();
        for (var i = 0; i < total; i++)
        {
            var chunk = data.AsSpan(i * MaxChunkBytes, Math.Min(MaxChunkBytes, data.Length - i * MaxChunkBytes)).ToArray();
            list.Add(Encode(w => { w.Put((byte)Step.Chunk); w.Put(epoch); w.Put(i); w.Put(total); Protocol.WriteBlob(w, chunk); }));
        }
        list.Add(Encode(w => { w.Put((byte)Step.Commit); w.Put(epoch); w.Put(data.Length); Protocol.WriteBlob(w, SHA256.HashData(data)); }));
        return list;
    }

    private sealed record Command(Step Step, ulong Epoch, int Index, int Total, byte[] Data, int Length, byte[] Hash);

    private static Command Parse(NetDataReader r)
    {
        var step = (Step)r.GetByte(); var epoch = r.GetULong();
        Command c;
        switch (step)
        {
            case Step.Chunk:
            {
                var index = r.GetInt(); var total = r.GetInt();
                var data = Protocol.ReadBlob(r, MaxChunkBytes, out _) ?? throw new FormatException("Invalid career chunk.");
                c = new(step, epoch, index, total, data, 0, Array.Empty<byte>());
                break;
            }
            case Step.Commit:
            {
                var length = r.GetInt();
                var hash = Protocol.ReadBlob(r, 32, out var declared) ?? throw new FormatException("Invalid career hash.");
                if (length is < 0 or > MaxBytes || declared != 32) throw new FormatException("Invalid career commit.");
                c = new(step, epoch, 0, 0, Array.Empty<byte>(), length, hash);
                break;
            }
            default: throw new FormatException("Unknown career step.");
        }
        if (r.AvailableBytes != 0) throw new FormatException("Trailing career bytes.");
        return c;
    }

    private byte[]? Assembled(ulong epoch) =>
        _chunks is { } g && g.Epoch == epoch && g.Chunks.Count == g.Total ? g.Chunks.SelectMany(x => x).ToArray() : null;

    /// <summary>Why the host's <paramref name="u"/> doesn't fit the model, or null.</summary>
    public string? CheckUpdate(CareerUpdate u)
    {
        if (!Bound) return "The career isn't bound yet.";
        var removed = new HashSet<ulong>();
        foreach (var id in u.Removed) if (Item(id) == null || !removed.Add(id)) return "The update removes an item that isn't owned.";
        var next = NextItem;
        foreach (var a in u.Added)
        {
            if (!a.Item.IsValid || a.Item.Id < next) return "The update adds an item with an unexpected ID.";
            next = a.Item.Id + 1;
        }
        if (_items.Count - u.Removed.Count + u.Added.Count > MaxOwned) return "Too many owned items.";
        return null;
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        if (ctx.Op.Actor != ActorSlot.Host) return CommandVerdict.Reject("Only the host changes the shared career.");
        if (_activity.Get(ActorSlot.Host) is not { } host || host.Epoch != c.Epoch) return CommandVerdict.Reject("Actor presence changed.");
        if (c.Step == Step.Chunk)
        {
            if (c.Total is < 1 or > MaxChunks || c.Data.Length > MaxChunkBytes) return CommandVerdict.Reject("Invalid career chunk.");
            if (c.Index == 0) return CommandVerdict.Accept;
            if (_chunks is not { } g || g.Epoch != c.Epoch || g.Total != c.Total || g.Chunks.Count != c.Index) return CommandVerdict.Reject("Career chunk out of order.");
            return g.Chunks.Sum(x => x.Length) + c.Data.Length > MaxBytes ? CommandVerdict.Reject("The career update is too large.") : CommandVerdict.Accept;
        }
        var data = Assembled(c.Epoch);
        if (data == null || data.Length != c.Length || !SHA256.HashData(data).AsSpan().SequenceEqual(c.Hash)) return CommandVerdict.Reject("The career update hasn't fully arrived.");
        CareerUpdate update;
        try { update = CareerUpdate.Decode(data); }
        catch (FormatException e) { return CommandVerdict.Reject(e.Message); }
        return CheckUpdate(update) is { } error ? CommandVerdict.Reject(error) : CommandVerdict.Accept;
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        if (c.Step == Step.Chunk)
        {
            // A guest that joined halfway through an update can't assemble it; the snapshot it loaded already has it.
            if (c.Index == 0) _chunks = (c.Epoch, c.Total, new List<byte[]> { c.Data });
            else if (_chunks is { } g && g.Epoch == c.Epoch && g.Chunks.Count == c.Index) g.Chunks.Add(c.Data);
            return;
        }
        var data = Assembled(c.Epoch);
        _chunks = null;
        if (data == null || data.Length != c.Length || !SHA256.HashData(data).AsSpan().SequenceEqual(c.Hash)) return;
        var update = CareerUpdate.Decode(data);
        if (CheckUpdate(update) is { } error) throw new InvalidDataException("Career update no longer applies: " + error);
        Commit(update);
        _native.Updated(ctx, update);
    }

    /// <summary>The model takes <paramref name="u"/> (already checked).</summary>
    private void Commit(CareerUpdate u)
    {
        if (u.Facts != null) Facts = u.Facts;
        var removed = new HashSet<ulong>(u.Removed);
        _items.RemoveAll(i => removed.Contains(i.Id));
        foreach (var a in u.Added) { _items.Add(a.Item); NextItem = Math.Max(NextItem, a.Item.Id + 1); }
        foreach (var e in u.Ledger) { LedgerHash = ChainLedger(LedgerHash, e); LedgerCount++; }
    }

    // ---------------- snapshot state ----------------

    public void Write(NetDataWriter w)
    {
        w.Put(Bound);
        if (!Bound) return;
        Facts.Write(w);
        w.Put(NextItem); w.Put(LedgerCount); w.Put(LedgerHash);
        w.Put(_items.Count);
        foreach (var i in _items) { w.Put(i.Id); w.Put((byte)i.List); w.Put(i.Key, OwnedItem.MaxKey); }
    }

    public void Read(NetDataReader r)
    {
        Reset();
        Bound = r.GetBool();
        if (!Bound) return;
        Facts = CareerFacts.Read(r);
        NextItem = r.GetULong(); LedgerCount = r.GetInt();
        var hash = new byte[32]; r.GetBytes(hash, 32); LedgerHash = hash;
        var n = r.GetInt();
        if (n is < 0 or > MaxOwned || LedgerCount < 0) throw new InvalidDataException("Invalid career state.");
        for (var k = 0; k < n; k++)
        {
            var item = new OwnedItem(r.GetULong(), (ItemList)r.GetByte(), r.GetString(OwnedItem.MaxKey));
            if (!item.IsValid || item.Id >= NextItem || _items.Any(x => x.Id == item.Id)) throw new InvalidDataException("Invalid owned item in the career state.");
            _items.Add(item);
        }
    }
}

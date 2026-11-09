using LiteNetLib.Utils;
using PCBS2Coop.Network;

internal sealed class FakeCareerNative : ICareerNative
{
    public readonly List<(bool Local, CareerUpdate Update)> Updates = new();
    public void Updated(in MutationContext ctx, CareerUpdate update) => Updates.Add((ctx.FromLocalPlayer, update));
}

/// <summary>Phase 15c: the host's shared career (numbers, owned item IDs, ledger) and the exact items installs and
/// new PCs use.</summary>
internal static class CareerTests
{
    private static OwnedItem Item(ulong id, string key, ItemList list = ItemList.Main) => new(id, list, key);
    private static CareerFacts Facts(int cash = 1000, int kudos = 5, int day = 1) => new(cash, kudos, 3.5f, day, 200, 0, 50, 0, 0, 0, 2, CareerFlags.UnlockedTablet);
    private static byte[] Native(int n) => Enumerable.Range(0, n).Select(i => (byte)(i * 7 + 1)).ToArray();

    public static void Run(Checker t)
    {
        Model(t);
        Sync(t);
        Edits(t);
    }

    private static void Model(Checker t)
    {
        t.Section("Career: the shared model");
        var w = new CareerWorld(new ActivityWorld(new ObjectWorld()), new FakeCareerNative());
        t.Check(!w.Bound && w.CheckUse(5, Array.Empty<ulong>()) == null, "an unbound model (Free Build) checks nothing");
        var items = new[] { Item(1, "CpuPartInstance:i5|job 0|lot 0.71"), Item(2, "CpuPartInstance:i5|job 0|lot 0.42"), Item(3, "Keyboard|x", ItemList.Peripheral) };
        w.BindHost(Facts(), items, 4, new byte[32], 4);
        t.Check(w.Bound && w.Items.Count == 3 && w.NextItem == 4 && w.Facts.Cash == 1000, "the host binds numbers and owned items");
        t.Check(Throws(() => w.BindHost(Facts(), new[] { Item(1, "a"), Item(1, "b") }, 0, new byte[32], 9)), "duplicate item IDs are refused");
        t.Check(Throws(() => w.BindHost(Facts(), new[] { Item(9, "a") }, 0, new byte[32], 9)), "an item ID at or past the next ID is refused");
        t.Check(Throws(() => w.BindHost(Facts() with { Rating = float.NaN }, items, 0, new byte[32], 4)), "broken numbers are refused");
        t.Check(w.CheckUse(1, Array.Empty<ulong>()) == null && w.CheckUse(1, new ulong[] { 1 })!.Contains("already installing") &&
                w.CheckUse(77, Array.Empty<ulong>())!.Contains("no longer"),
            "an owned item can be used once at a time, and only while it is owned");
        t.Check(w.CheckUpdate(new CareerUpdate(null, new ulong[] { 9 }, Array.Empty<AddedItem>(), Array.Empty<byte[]>()))!.Contains("isn't owned") &&
                w.CheckUpdate(new CareerUpdate(null, Array.Empty<ulong>(), new[] { new AddedItem(Item(3, "x"), Native(4)) }, Array.Empty<byte[]>()))!.Contains("unexpected ID") &&
                w.CheckUpdate(new CareerUpdate(null, new ulong[] { 2, 2 }, Array.Empty<AddedItem>(), Array.Empty<byte[]>())) != null,
            "updates can't remove what isn't owned, reuse IDs or remove twice");

        var state = new NetDataWriter(); w.Write(state);
        var copy = new CareerWorld(new ActivityWorld(new ObjectWorld()), new FakeCareerNative());
        copy.Read(new NetDataReader(state.CopyData()));
        t.Check(copy.Bound && copy.Items.SequenceEqual(w.Items) && copy.NextItem == 4 && copy.LedgerCount == 4 && copy.Facts == w.Facts,
            "the career travels with snapshots and saves exactly");
        copy.Reset();
        t.Check(!copy.Bound && copy.Items.Count == 0 && copy.NextItem == 1, "a new world generation starts unbound until the host binds it");

        var update = new CareerUpdate(Facts(cash: 750), new ulong[] { 1 },
            new[] { new AddedItem(Item(4, "GpuPartInstance:gtx|job 3"), Native(20)) }, new[] { Native(9), Native(3) });
        var back = CareerUpdate.Decode(update.Encode());
        t.Check(back.Facts == update.Facts && back.Removed.SequenceEqual(update.Removed) && back.Added.Single().Item == update.Added.Single().Item &&
                back.Added.Single().Native.SequenceEqual(update.Added.Single().Native) && back.Ledger.Count == 2 && back.Ledger[1].SequenceEqual(Native(3)),
            "an update keeps numbers, items and ledger entries exactly");
        var broken = update.Encode(); broken[^1] ^= 0xFF;
        t.Check(Fails(() => CareerUpdate.Decode(broken.Take(broken.Length - 2).ToArray())), "a cut-off update is malformed");
    }

    private static bool Throws(Action a) { try { a(); return false; } catch (InvalidDataException) { return true; } }
    private static bool Fails(Action a) { try { a(); return false; } catch (FormatException) { return true; } }

    private static void Sync(Checker t)
    {
        t.Section("Career: host updates reach the guest");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hn = new FakeCareerNative(); var gn = new FakeCareerNative();
        var hw = new CareerWorld(ha, hn); var gw = new CareerWorld(ga, gn);
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.RegisterState(hw, optional: true); l.Guest.RegisterState(gw, optional: true);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.Register(CommandTypes.Career, hw); l.Guest.Register(CommandTypes.Career, gw);
        l.Host.BeginGeneration("career");
        var host = PlayerId.New(); var guest = PlayerId.New();
        void Send(WorldSync world, byte[] payload, ushort type = CommandTypes.Career) { world.Submit(type, payload, out _); l.Flush(); }
        void SendAll(WorldSync world, IEnumerable<byte[]> payloads) { foreach (var p in payloads) world.Submit(CommandTypes.Career, p, out _); l.Flush(); }
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, host), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, guest), CommandTypes.ActorActivity);
        hw.BindHost(Facts(), new[] { Item(1, "case|a"), Item(2, "cpu|b"), Item(3, "cpu|b") }, 0, new byte[32], 4);
        l.Join();
        ulong H() => ha.Get(ActorSlot.Host)!.Epoch; ulong G() => ga.Get(ActorSlot.Guest)!.Epoch;
        t.Check(gw.Bound && gw.Items.SequenceEqual(hw.Items) && gw.Facts == hw.Facts, "a joining guest gets the host's career with its snapshot");

        // Two equal CPUs stay two items with their own IDs: the host bought a third, sold one, paid rent.
        var ledger = new[] { Native(30), Native(12) };
        SendAll(l.Host, CareerWorld.Submissions(H(), new CareerUpdate(Facts(cash: 600), new ulong[] { 2 }, new[] { new AddedItem(Item(4, "cpu|b"), Native(40)) }, ledger)));
        t.Check(gw.Items.Select(i => i.Id).SequenceEqual(new ulong[] { 1, 3, 4 }) && gw.Facts.Cash == 600 && gw.LedgerCount == 2 &&
                gw.LedgerHash.SequenceEqual(hw.LedgerHash) && gw.NextItem == 5 && l.InStep,
            "the guest's model follows the host's: items out and in by ID, cash and ledger");
        t.Check(gn.Updates.Single().Local == false && gn.Updates[0].Update.Added[0].Native.SequenceEqual(Native(40)) && hn.Updates.Single().Local,
            "the guest's game gets the host's serialized item to make the same one");

        // A large update (many items bought at once) arrives in chunks.
        var many = Enumerable.Range(0, 30).Select(i => new AddedItem(Item(5 + (ulong)i, $"ram|{i}"), Native(2000))).ToArray();
        var parts = CareerWorld.Submissions(H(), new CareerUpdate(null, Array.Empty<ulong>(), many, Array.Empty<byte[]>()));
        t.Check(parts.Count > 2 && parts.All(p => p.Length <= Protocol.MaxPayloadBytes), $"a large update is split into commands that fit ({parts.Count})");
        SendAll(l.Host, parts);
        t.Check(gw.Items.Count == 33 && gw.NextItem == 35 && l.InStep, "every chunk arrives and the guest takes the whole update");

        var before = l.GuestRejected.Count;
        SendAll(l.Guest, CareerWorld.Submissions(G(), new CareerUpdate(Facts(cash: 999999), Array.Empty<ulong>(), Array.Empty<AddedItem>(), Array.Empty<byte[]>())));
        t.Check(l.GuestRejected.Skip(before).Any(r => r.Text.Contains("Only the host")) && gw.Facts.Cash == 600, "the guest can't change the career (host decision)");
        before = l.HostRejected.Count;
        Send(l.Host, CareerWorld.Submissions(H(), new CareerUpdate(Facts(), Array.Empty<ulong>(), Array.Empty<AddedItem>(), Array.Empty<byte[]>()))[^1]);
        t.Check(l.HostRejected.Count == before + 1 && l.HostRejected.Last().Text.Contains("arrived"), "a commit without its chunks is refused");
        before = l.HostRejected.Count;
        SendAll(l.Host, CareerWorld.Submissions(H(), new CareerUpdate(null, new ulong[] { 1, 1 }, Array.Empty<AddedItem>(), Array.Empty<byte[]>())));
        t.Check(l.HostRejected.Count == before + 1 && gw.Items.Any(i => i.Id == 1), "an update that removes an item twice is refused as a whole");
    }

    private static void Edits(Checker t)
    {
        t.Section("Career: installs and new PCs name the exact owned item");
        var start = new EditStart(true, false) { Variant = "delidded", Item = 42 };
        var w = new NetDataWriter(); w.Put(ComponentEditWorld.Progress(1, new OperationId(ActorSlot.Guest, 3), EditProgress.Started, start));
        t.Check(w.Length > 0 && ComponentEditWorld.Progress(1, new OperationId(ActorSlot.Guest, 3), EditProgress.Started, start).Length ==
                ComponentEditWorld.Progress(1, new OperationId(ActorSlot.Guest, 3), EditProgress.Started, start with { Item = 0 }).Length,
            "an install's start always carries its item (0 in Free Build)");
        var edit = new ComponentEdit(new OperationId(ActorSlot.Host, 1), ActorSlot.Host, 1, new NetObjectId(2), new NetObjectId(3), EditGesture.Install, "cpu", default,
            Array.Empty<NetObjectId>()) { Start = start };
        t.Check(ComponentEditWorld.ItemOf(edit) == 42 && ComponentEditWorld.ItemOf(edit with { Result = EditResult.Done() with { Item = 43 } }) == 43 &&
                ComponentEditWorld.ItemOf(edit with { Start = null }) == 0, "the item an install uses: its result's, else its start's");
        var create = PcLifecycle.Create(1, new CatalogRef("kolink", "CasePartInstance", 0), default, 77);
        var free = PcLifecycle.Create(1, new CatalogRef("kolink", "CasePartInstance", 0), default);
        t.Check(create.Length == free.Length && !create.SequenceEqual(free), "a new PC names the owned case it is made from (0 in Free Build)");
    }
}

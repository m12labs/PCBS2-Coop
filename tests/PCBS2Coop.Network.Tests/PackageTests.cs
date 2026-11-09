using LiteNetLib.Utils;
using PCBS2Coop.Network;

internal sealed class FakePackageNative : IPackageNative
{
    public readonly List<(bool Local, PackageChange Change, PackageFact? Before)> Changes = new();
    public string? Refuse;
    public byte[] Part = { 9, 9, 9 };
    public string? CheckHost(in CommandContext ctx, PackageChange change) => Refuse;
    public (byte[] Part, int Job) PackItem(ulong item) => (Part, 12);
    public void Changed(in MutationContext ctx, PackageChange change, PackageFact? before) => Changes.Add((ctx.FromLocalPlayer, change, before));
}

/// <summary>Phase 16c: packages and physical job notes, and the shared delivery box.</summary>
internal static class PackageTests
{
    private static CareerFacts Facts() => new(1000, 5, 3.5f, 1, 200, 0, 50, 0, 0, 0, 2, CareerFlags.UnlockedTablet);
    private static PackageFact Box(int job, int parts) => new(job, Enumerable.Range(1, parts).Select(i => new[] { (byte)i, (byte)(i + 1) }).ToArray());

    public static void Run(Checker t)
    {
        Model(t);
        Sync(t);
        Deliveries(t);
    }

    private static void Model(Checker t)
    {
        t.Section("Packages: the shared model");
        var activity = new ActivityWorld(new ObjectWorld());
        var w = new PackageWorld(activity, new CareerWorld(activity, new FakeCareerNative()), new FakePackageNative());
        t.Check(!w.Bound, "an unbound model (Free Build) has no packages");
        t.Check(Throws(() => w.BindHost(new Dictionary<string, PackageFact> { [""] = Box(1, 1) }, new Dictionary<string, int>(), null, 0)), "a bench without a key is refused");
        t.Check(Throws(() => w.BindHost(new Dictionary<string, PackageFact>(), new Dictionary<string, int> { ["board/0"] = 0 }, null, 0)), "a note without a job is refused");
        w.BindHost(new Dictionary<string, PackageFact> { ["bench/1"] = Box(4, 2) }, new Dictionary<string, int> { ["board/0"] = 4 }, Box(7, 1), 0);
        t.Check(w.Bound && w.Benches.Count == 1 && w.Notes["board/0"] == 4 && w.CarriedBy(ActorSlot.Host)!.JobId == 7 && w.HandsFull(ActorSlot.Host) && !w.HandsFull(ActorSlot.Guest),
            "the host binds its benches, notes and what it carries");
        var s = new NetDataWriter(); w.Write(s);
        var copy = new PackageWorld(activity, new CareerWorld(activity, new FakeCareerNative()), new FakePackageNative());
        copy.Read(new NetDataReader(s.CopyData()));
        t.Check(copy.Bound && copy.Benches["bench/1"].Like(Box(4, 2)) && copy.Benches["bench/1"].Contents[1].SequenceEqual(new byte[] { 2, 3 }) && copy.Notes["board/0"] == 4 &&
                copy.CarriedBy(ActorSlot.Host)!.Like(Box(7, 1)), "packages and notes travel with snapshots and saves exactly");
        t.Check(Box(4, 2).Like(Box(4, 2) with { Contents = new[] { new byte[] { 7 }, new byte[] { 8 } } }) && !Box(4, 2).Like(Box(4, 1)) && !Box(4, 2).Like(Box(5, 2)),
            "two games' copies of a package match by job and part count");
    }

    private static bool Throws(Action a) { try { a(); return false; } catch (InvalidDataException) { return true; } }

    private static void Sync(Checker t)
    {
        t.Section("Packages: both players pack, carry, place and open; notes move");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hc = new CareerWorld(ha, new FakeCareerNative()); var gc = new CareerWorld(ga, new FakeCareerNative());
        var hn = new FakePackageNative(); var gn = new FakePackageNative();
        var hw = new PackageWorld(ha, hc, hn); var gw = new PackageWorld(ga, gc, gn);
        var installing = new List<ulong>();
        hw.ItemsInUse = () => installing;
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.RegisterState(hc, optional: true); l.Guest.RegisterState(gc, optional: true);
        l.Host.RegisterState(hw, optional: true); l.Guest.RegisterState(gw, optional: true);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.Register(CommandTypes.Career, hc); l.Guest.Register(CommandTypes.Career, gc);
        l.Host.Register(CommandTypes.Packages, hw); l.Guest.Register(CommandTypes.Packages, gw);
        l.Host.BeginGeneration("packages");
        void Send(WorldSync world, byte[] payload, ushort type = CommandTypes.Packages) { world.Submit(type, payload, out _); l.Flush(); }
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, PlayerId.New()), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, PlayerId.New()), CommandTypes.ActorActivity);
        hc.BindHost(Facts(), new[] { new OwnedItem(1, ItemList.Main, "cpu|a"), new OwnedItem(2, ItemList.Main, "gpu|b"), new OwnedItem(3, ItemList.Delivery, "ram|c") }, 0, new byte[32], 4);
        hw.BindHost(new Dictionary<string, PackageFact> { ["bench/1"] = Box(4, 1) }, new Dictionary<string, int> { ["board/0"] = 4 }, null, 0);
        l.Join();
        ulong H() => ha.Get(ActorSlot.Host)!.Epoch; ulong G() => ga.Get(ActorSlot.Guest)!.Epoch;
        t.Check(gw.Bound && gw.Benches["bench/1"].Like(Box(4, 1)) && gw.Notes["board/0"] == 4, "a joining guest gets the host's packages and notes with its snapshot");

        // The guest packs an owned CPU into a new package: the host names the part's serialized form and job.
        Send(l.Guest, PackageWorld.Pack(G(), 1));
        var packed = gw.CarriedBy(ActorSlot.Guest);
        t.Check(packed != null && packed.JobId == 12 && packed.Contents.Single().SequenceEqual(hn.Part) && hw.CarriedBy(ActorSlot.Guest)!.Like(packed) &&
                gn.Changes.Last().Local && !hn.Changes.Last().Local && hn.Changes.Last().Change.Item == 1 && l.InStep,
            "packing an owned item: both games learn the part, and the host's game takes it out of its inventory");
        var before = l.GuestRejected.Count;
        Send(l.Guest, PackageWorld.Pack(G(), 7));
        installing.Add(2);
        Send(l.Guest, PackageWorld.Pack(G(), 2));
        Send(l.Guest, PackageWorld.Pack(G(), 3));
        t.Check(l.GuestRejected.Count == before + 3 && gw.CarriedBy(ActorSlot.Guest)!.Contents.Count == 1,
            "an item that isn't owned, being installed or still in the delivery box can't be packed");
        installing.Clear();
        Send(l.Guest, PackageWorld.Pack(G(), 2));
        t.Check(gw.CarriedBy(ActorSlot.Guest)!.Contents.Count == 2, "a second item goes into the package the player carries");

        // Hands: one package or note at a time.
        before = l.GuestRejected.Count;
        Send(l.Guest, PackageWorld.Take(G(), "bench/1"));
        Send(l.Guest, PackageWorld.NoteTake(G(), "board/0"));
        t.Check(l.GuestRejected.Count == before + 2 && gw.Benches.ContainsKey("bench/1") && l.GuestRejected.Last().Text.Contains("hands"),
            "a player carrying a package can't pick up another package or a note");
        Send(l.Guest, PackageWorld.Place(G(), "bench/1"));
        t.Check(l.GuestRejected.Last().Text.Contains("already a package"), "a package can't go on a bench that has one");
        Send(l.Guest, PackageWorld.Place(G(), "bench/2"));
        t.Check(!gw.HandsFull(ActorSlot.Guest) && gw.Benches["bench/2"].Contents.Count == 2 && hw.Benches["bench/2"].Like(gw.Benches["bench/2"]) &&
                hn.Changes.Last().Before!.Contents.Count == 2 && l.InStep, "placing it puts the same package on that bench on both games");

        // The host's own game: it picks up a bench package; its report reaches the guest.
        Send(l.Host, PackageWorld.HostBench(H(), "bench/1", null));
        Send(l.Host, PackageWorld.HostCarry(H(), Box(4, 1), 0));
        t.Check(!gw.Benches.ContainsKey("bench/1") && gw.CarriedBy(ActorSlot.Host)!.JobId == 4 && !gn.Changes.Last().Local, "the host's own moves reach the guest as reports");
        before = l.GuestRejected.Count;
        Send(l.Guest, PackageWorld.HostBench(G(), "bench/9", Box(1, 1)));
        t.Check(l.GuestRejected.Count == before + 1 && l.GuestRejected.Last().Text.Contains("Only the host"), "only the host reports its benches");

        // Notes: take one off the board and drop it on another slot.
        Send(l.Guest, PackageWorld.NoteTake(G(), "board/0"));
        t.Check(gw.NoteCarriedBy(ActorSlot.Guest) == 4 && !gw.Notes.ContainsKey("board/0") && gn.Changes.Last().Change.Note == 4, "a player picks up a job note");
        Send(l.Guest, PackageWorld.NoteDrop(G(), "bench/2/note"));
        t.Check(gw.Notes["bench/2/note"] == 4 && hw.Notes["bench/2/note"] == 4 && gw.NoteCarriedBy(ActorSlot.Guest) == 0, "and drops it on a free note slot");
        hn.Refuse = "That note slot is missing on the host's game.";
        before = l.GuestRejected.Count;
        Send(l.Guest, PackageWorld.NoteTake(G(), "bench/2/note"));
        t.Check(l.GuestRejected.Count == before + 1 && gw.Notes.ContainsKey("bench/2/note"), "the host's own check can refuse a change");
        hn.Refuse = null;

        // Opening the package on the bench: it's gone on both games, its parts go back to the inventory.
        Send(l.Guest, PackageWorld.Open(G(), "bench/2"));
        t.Check(!gw.Benches.ContainsKey("bench/2") && !hw.Benches.ContainsKey("bench/2") && hn.Changes.Last().Before!.Contents.Count == 2, "opening a bench package removes it with its parts");
        before = l.GuestRejected.Count;
        Send(l.Guest, PackageWorld.Open(G(), ""));
        t.Check(l.GuestRejected.Count == before + 1, "a player can't open a package it doesn't carry");

        // The guest leaves while carrying: the host puts the package on a free bench and the note on a slot.
        Send(l.Guest, PackageWorld.Pack(G(), 1));
        Send(l.Guest, PackageWorld.NoteTake(G(), "bench/2/note"));
        t.Check(l.GuestRejected.Last().Text.Contains("hands"), "(still one thing at a time)");
        var saved = new NetDataWriter(); hw.Write(saved);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, default), CommandTypes.ActorActivity);
        Send(l.Host, PackageWorld.Recover(H(), ActorSlot.Guest, "bench/3", ""));
        t.Check(!hw.HandsFull(ActorSlot.Guest) && hw.Benches["bench/3"].Contents.Count == 1 && hn.Changes.Last().Change.Step == PackageStep.Recover,
            "a departed guest's package goes to a free bench");
        before = l.HostRejected.Count;
        Send(l.Host, PackageWorld.Recover(H(), ActorSlot.Guest, "", ""));
        t.Check(l.HostRejected.Count == before + 1, "nothing is recovered twice");

        // Cold Continue: the save holds what the guest carried; the host's binding keeps it until it is recovered.
        var restored = new PackageWorld(ha, hc, new FakePackageNative());
        restored.Read(new NetDataReader(saved.CopyData()));
        restored.BindHost(new Dictionary<string, PackageFact> { ["bench/3"] = Box(0, 1) }, new Dictionary<string, int>(), null, 0);
        t.Check(restored.CarriedBy(ActorSlot.Guest)?.Contents.Count == 1 && restored.Benches.Count == 1 && restored.CarriedBy(ActorSlot.Host) == null,
            "after a cold Continue the guest's carried package is still known (and the benches are the host's)");
    }

    private static void Deliveries(Checker t)
    {
        t.Section("Deliveries: the shared delivery box");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hc = new CareerWorld(ha, new FakeCareerNative()); var gc = new CareerWorld(ga, new FakeCareerNative());
        var hn = new FakeCommerceNative(); var gn = new FakeCommerceNative();
        var hw = new CommerceWorld(ha, hc, hn); var gw = new CommerceWorld(ga, gc, gn);
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.RegisterState(hc, optional: true); l.Guest.RegisterState(gc, optional: true);
        l.Host.RegisterState(hw, optional: true); l.Guest.RegisterState(gw, optional: true);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.Register(CommandTypes.Career, hc); l.Guest.Register(CommandTypes.Career, gc);
        l.Host.Register(CommandTypes.Commerce, hw); l.Guest.Register(CommandTypes.Commerce, gw);
        l.Host.BeginGeneration("deliveries");
        void Send(WorldSync world, byte[] payload, ushort type = CommandTypes.Commerce) { world.Submit(type, payload, out _); l.Flush(); }
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, PlayerId.New()), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, PlayerId.New()), CommandTypes.ActorActivity);
        hc.BindHost(Facts(), new[] { new OwnedItem(1, ItemList.Main, "cpu|a") }, 0, new byte[32], 2);
        hw.BindHost(new Dictionary<ShopKind, IReadOnlyList<byte[]>>(), Array.Empty<byte>());
        l.Join();
        ulong H() => ha.Get(ActorSlot.Host)!.Epoch; ulong G() => ga.Get(ActorSlot.Guest)!.Epoch;
        var before = l.GuestRejected.Count;
        Send(l.Guest, CommerceWorld.Collect(G()));
        t.Check(l.GuestRejected.Count == before + 1 && l.GuestRejected.Last().Text.Contains("empty") && hn.Collections == 0, "an empty delivery box can't be collected");
        // The host's game got a same-day delivery: its career update puts it in the shared box.
        foreach (var p in CareerWorld.Submissions(H(), new CareerUpdate(null, Array.Empty<ulong>(), new[] { new AddedItem(new OwnedItem(2, ItemList.Delivery, "gpu|b"), new byte[] { 1 }) }, Array.Empty<byte[]>())))
            l.Host.Submit(CommandTypes.Career, p, out _);
        l.Flush();
        t.Check(gc.Items.Any(i => i.Id == 2 && i.List == ItemList.Delivery), "a delivery reaches the guest's delivery box as an owned item");
        Send(l.Guest, CommerceWorld.Collect(G()));
        t.Check(hn.Collections == 1 && gn.Collections == 1 && l.InStep, "the guest's collection reaches the host's game, which collects");
    }
}

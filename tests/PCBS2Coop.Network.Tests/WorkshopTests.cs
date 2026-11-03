using System.Security.Cryptography;
using LiteNetLib.Utils;
using PCBS2Coop.Network;

internal sealed class FakeWorkshopNative : IWorkshopNative
{
    public readonly List<string> Calls = new();
    public readonly Dictionary<string, byte[]> Content = new();
    public IReadOnlyList<DecorItem> LastItems = Array.Empty<DecorItem>();
    public RoomLight? LastLight;
    public int Loaded;
    public void Decorating(in MutationContext ctx, ActorSlot actor, bool on) => Calls.Add($"decorating {actor} {on} local={ctx.FromLocalPlayer}");
    public void Decorated(in MutationContext ctx, IReadOnlyList<DecorItem> items, RoomLight? light)
    {
        Calls.Add($"decor {items.Count} light={light != null} local={ctx.FromLocalPlayer}");
        LastItems = items; LastLight = light;
    }
    public void Lights(in MutationContext ctx, bool on) => Calls.Add($"lights {on} local={ctx.FromLocalPlayer}");
    public void Decoration(in MutationContext ctx, byte id) => Calls.Add($"decoration {id} local={ctx.FromLocalPlayer}");
    public bool HasContent(byte[] hash) => Content.ContainsKey(Convert.ToHexString(hash));
    public void ContentArrived(in MutationContext ctx, byte[] hash, byte[] data)
    {
        Content[Convert.ToHexString(hash)] = data;
        Calls.Add($"content {data.Length} local={ctx.FromLocalPlayer}");
    }
    public void Wallpaper(in MutationContext ctx, WallpaperFact fact) => Calls.Add($"wallpaper {fact.Pc} {fact.Name} local={ctx.FromLocalPlayer}");
    public void ContentNeeded(in MutationContext ctx, byte[] hash) => Calls.Add($"need {Convert.ToHexString(hash)[..8]} local={ctx.FromLocalPlayer}");
    public void StateLoaded() => Loaded++;
}

/// <summary>PC creation natives that also import: the imported PC's topology comes from content this game has.</summary>
internal sealed class FakeImportNative : IPcNative, IPcImportNative
{
    public readonly FakeWorkshopNative Store;
    public readonly List<string> Calls = new();
    public FakeImportNative(FakeWorkshopNative store) { Store = store; }
    public PcTemplate? Describe(CatalogRef t) => null;
    public string? CheckHost(in CommandContext ctx, PcCommand c, PcFact? target) => null;
    public void Created(in MutationContext ctx, PcFact pc, CatalogRef t, PcFact? replaced) => Calls.Add("create");
    public void Deleted(in MutationContext ctx, PcFact pc, PcRemoval from) => Calls.Add("delete");
    public PcTemplate? DescribeImport(byte[] hash) => Store.HasContent(hash) ? FakePcNative.Template("imported-case") : null;
    public void Imported(in MutationContext ctx, PcFact pc, byte[] hash) => Calls.Add($"import {pc.Id.Value} local={ctx.FromLocalPlayer}");
}

internal static class WorkshopTests
{
    private static NetObjectId Id(ulong n) => new(n);

    public static void Run(Checker t)
    {
        t.Section("Workshop: decorating, room lights, seasonal decorations, pictures and imported PCs (phase 12)");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hn = new FakeWorkshopNative(); var gn = new FakeWorkshopNative();
        var hw = new WorkshopWorld(ho, ha, hn); var gw = new WorkshopWorld(go, ga, gn);
        var hi = new FakeImportNative(hn); var gi = new FakeImportNative(gn);
        var hp = new PcLifecycle(ho, ha, hi, true); var gp = new PcLifecycle(go, ga, gi, false);
        l.Host.RegisterState(ho); l.Guest.RegisterState(go);
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.Register(CommandTypes.Workshop, hw); l.Guest.Register(CommandTypes.Workshop, gw);
        l.Host.RegisterState(hw, optional: true); l.Guest.RegisterState(gw, optional: true);
        l.Host.Register(CommandTypes.PcLifecycle, hp); l.Guest.Register(CommandTypes.PcLifecycle, gp);
        l.Host.BeginGeneration("workshop");
        var host = PlayerId.New(); var guest = PlayerId.New();
        var origin = new InstanceProvenance(InstanceOrigin.Baseline, host, default);
        var pc = new PcFact(Id(2), origin, PcLocation.Bench("A"), new[] { new PartFact(Id(3), "case", "CasePartInstance") }, new[] { new PartLink("caseID", Id(3)) });
        ho.Replace(Guid.NewGuid(), 4, new[] { new LocationFact(Id(1), "A") }, new[] { pc }, Array.Empty<NetObjectId>());
        void Send(WorldSync world, byte[] payload, ushort type = CommandTypes.Workshop) { world.Submit(type, payload, out _); l.Flush(); }
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, host), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, guest), CommandTypes.ActorActivity); l.Join();
        ulong H() => ha.Get(ActorSlot.Host)!.Epoch; ulong G() => ga.Get(ActorSlot.Guest)!.Epoch;
        bool Same() => l.InStep && l.Divergences.Count == 0;

        // Decorating: one player at a time.
        var desk = new DecorItem("DESK", 4, "2_1", 3, 1, false, 0);
        var wall = new DecorItem("WALL", 0, "5", 1, 1, true, 2);
        Send(l.Host, WorkshopWorld.Decorate(H(), true));
        t.Check(hw.Decorator == ActorSlot.Host && gw.Decorator == ActorSlot.Host && gn.Calls.Last() == "decorating Host True local=False" && Same(),
            "opening Customise Workshop gives the host decorating on both games");
        Send(l.Guest, WorkshopWorld.Decorate(G(), true));
        t.Check(l.GuestRejected.Last().Text.Contains(WorkshopWorld.Busy) && hw.Decorator == ActorSlot.Host, "the guest is refused: \"Someone is decorating the workshop.\"");
        Send(l.Guest, WorkshopWorld.Decor(G(), new[] { desk }, null));
        t.Check(l.GuestRejected.Last().Text.Contains(WorkshopWorld.Busy) && !hn.Calls.Any(c => c.StartsWith("decor ")), "a decoration from the player who isn't decorating is refused");
        var light = new RoomLight(0.2f, 0.4f, 1f, 1f, true);
        Send(l.Host, WorkshopWorld.Decor(H(), new[] { desk, wall }, light));
        t.Check(gn.Calls.Last() == "decor 2 light=True local=False" && gn.LastItems.SequenceEqual(new[] { desk, wall }) && gn.LastLight == light && Same(),
            "the host's changed decorations and light colour reach the guest intact (DLC flag and pack too)");
        Send(l.Host, WorkshopWorld.Decorate(H(), false));
        Send(l.Guest, WorkshopWorld.Decorate(G(), true));
        t.Check(hw.Decorator == ActorSlot.Guest && hw.DecorBusy(ActorSlot.Host) == WorkshopWorld.Busy && Same(), "after the host closes the app the guest can decorate");
        Send(l.Guest, WorkshopWorld.Decor(G(), new[] { desk with { Current = "0_0" } }, null));
        t.Check(hn.Calls.Last() == "decor 1 light=False local=False" && hn.LastItems[0].Current == "0_0", "the guest's decorating reaches the host");
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, default), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, guest), CommandTypes.ActorActivity);
        t.Check(hw.Decorator == null && hw.DecorBusy(ActorSlot.Host) == null, "a guest who leaves or rejoins frees decorating");
        var malformed = l.GuestRejected.Count;
        var badItem = WorkshopWorld.Decor(G(), new[] { desk with { Type = -4 } }, null);
        Send(l.Guest, badItem);
        Send(l.Guest, WorkshopWorld.Decor(G(), Array.Empty<DecorItem>(), new RoomLight(float.NaN, 0, 0, 1, false)));
        t.Check(l.GuestRejected.Count == malformed + 2 && l.GuestRejected.TakeLast(2).All(x => x.Reason == RejectReason.Malformed),
            "decorations of an unknown kind and invalid light colours are malformed");
        var twin = desk with { Nth = 1, Current = "3_0" };
        t.Check(twin.Key != desk.Key && new DecorItem("x", 4, "x", 3, 0, false, 0).Key == desk.Key, "two desks with the same number are told apart by their order; the pick isn't part of the key");

        // Room lights and seasonal decorations.
        Send(l.Guest, WorkshopWorld.Lights(G(), false));
        t.Check(hn.Calls.Last() == "lights False local=False" && hw.LightsOn == false && gw.LightsOn == false && Same(), "the guest switches the room lights off on both games");
        Send(l.Host, WorkshopWorld.Decoration(H(), 3));
        t.Check(gn.Calls.Last() == "decoration 3 local=False" && gw.ActiveDecoration == 3, "the host's Christmas decoration switch reaches the guest");
        Send(l.Guest, WorkshopWorld.Decoration(G(), 9));
        t.Check(l.GuestRejected.Last().Text.Contains("Unknown seasonal") && hw.ActiveDecoration == 3, "an unknown seasonal decoration is refused");

        // Pictures: chunked content, then the PC's shared picture.
        var picture = new byte[40_000]; new Random(5).NextBytes(picture);
        var hash = SHA256.HashData(picture);
        Send(l.Guest, WorkshopWorld.Wallpaper(G(), pc.Id, @"C:\Users\guest\cat.png", hash, picture.Length, true));
        t.Check(l.GuestRejected.Last().Text.Contains("doesn't have that picture") && hw.WallpaperOf(pc.Id) == null, "a picture the host doesn't have yet can't be set");
        var parts = WorkshopWorld.Send(G(), picture);
        t.Check(parts.Count == 4, "a 40 KB picture travels in three chunks and its hash");
        Send(l.Guest, parts[1]);
        t.Check(l.GuestRejected.Last().Text.Contains("out of order"), "a chunk out of order is refused");
        foreach (var p in parts) Send(l.Guest, p);
        t.Check(hn.Content.TryGetValue(Convert.ToHexString(hash), out var got) && got.SequenceEqual(picture) && gn.Calls.Last() == "content 40000 local=True" && Same(),
            "the picture arrives whole on the host and is kept under its hash on both games");
        Send(l.Guest, WorkshopWorld.Wallpaper(G(), pc.Id, @"C:\Users\guest\cat.png", hash, picture.Length, true));
        t.Check(hn.Calls.Last() == $"wallpaper {pc.Id} C:\\Users\\guest\\cat.png local=False" && gw.WallpaperOf(pc.Id)?.HashText == Convert.ToHexString(hash) && Same(),
            "the guest's own picture becomes PC 2's shared wallpaper on both games");
        Send(l.Guest, WorkshopWorld.Wallpaper(G(), Id(99), "USER_WALLPAPER", hash, picture.Length, true));
        t.Check(l.GuestRejected.Last().Text.Contains("isn't in the shared world"), "a picture for an unknown PC is refused");
        var wrong = WorkshopWorld.Send(G(), picture);
        for (var i = 0; i < wrong.Count - 1; i++) Send(l.Guest, wrong[i]);
        Send(l.Guest, WorkshopWorld.Content(G(), picture.Reverse().ToArray()));
        t.Check(l.GuestRejected.Last().Text.Contains("doesn't match"), "content whose hash doesn't match its chunks is refused");
        t.Check(Throws(() => WorkshopWorld.Send(G(), new byte[WorkshopWorld.MaxContentBytes + 1])), "a picture over 8 MB can't be sent");
        Send(l.Guest, WorkshopWorld.Need(G(), hash));
        t.Check(hn.Calls.Last().StartsWith("need ") && hn.Calls.Last().EndsWith("local=False"), "a guest missing a picture asks the host for it");
        Send(l.Host, WorkshopWorld.Need(H(), hash));
        t.Check(l.HostRejected.Last().Text.Contains("Only a guest"), "the host doesn't ask itself");
        Send(l.Guest, WorkshopWorld.Need(G(), new byte[32]));
        t.Check(l.GuestRejected.Last().Text.Contains("doesn't have that content"), "asking for content the host doesn't have is refused");

        // The state travels with snapshots and saves.
        hw.Season = 2;
        var w = new NetDataWriter(); hw.Write(w);
        var rn = new FakeWorkshopNative(); var restored = new WorkshopWorld(ho, ha, rn);
        restored.Read(new NetDataReader(w.CopyData()));
        t.Check(restored.LightsOn == false && restored.ActiveDecoration == 3 && restored.Season == 2 && restored.WallpaperOf(pc.Id)?.Name == @"C:\Users\guest\cat.png" &&
            restored.WallpaperOf(pc.Id)!.Aspect && rn.Loaded == 1, "lights, the seasonal decoration, the host's season and pictures survive save and join");
        var empty = new WorkshopWorld(new ObjectWorld(), new ActivityWorld(new ObjectWorld()), new FakeWorkshopNative());
        var ew = new NetDataWriter(); empty.Write(ew);
        empty.Read(new NetDataReader(ew.CopyData()));
        t.Check(empty.LightsOn == null && empty.ActiveDecoration == 0 && !empty.Wallpapers.Any(), "a world nobody changed keeps each game's own lights and decorations");

        // Importing a PC file: content first, then the host gives the PC its IDs in the importer's hands.
        var file = new byte[20_000]; new Random(9).NextBytes(file);
        var fileHash = SHA256.HashData(file);
        Send(l.Guest, PcLifecycle.Import(G(), fileHash, file.Length), CommandTypes.PcLifecycle);
        t.Check(l.GuestRejected.Last().Text.Contains("hasn't arrived"), "an import whose PC hasn't reached the host is refused");
        foreach (var p in WorkshopWorld.Send(G(), file)) Send(l.Guest, p);
        var before = ho.NextId;
        Send(l.Guest, PcLifecycle.Import(G(), fileHash, file.Length), CommandTypes.PcLifecycle);
        var imported = ho.Pcs.SingleOrDefault(x => x.Location.Place == PcPlace.Carrier && x.Location.Actor == ActorSlot.Guest);
        t.Check(imported != null && imported.Provenance.Origin == InstanceOrigin.Imported && imported.Id.Value == before && imported.Parts.Count == 2 &&
            gi.Calls.Last() == $"import {before} local=True" && hi.Calls.Last() == $"import {before} local=False" && ho.ToBytes().SequenceEqual(go.ToBytes()) && Same(),
            "the imported PC arrives in the guest's hands on both games with host-given IDs");
        Send(l.Guest, PcLifecycle.Import(G(), fileHash, file.Length), CommandTypes.PcLifecycle);
        t.Check(l.GuestRejected.Last().Text.Contains("already hold"), "importing again with full hands is refused");
        Send(l.Host, PcLifecycle.Import(H(), fileHash, file.Length), CommandTypes.PcLifecycle);
        t.Check(ho.Pcs.Count(x => x.Provenance.Origin == InstanceOrigin.Imported) == 2 && hi.Calls.Last().EndsWith("local=True") && Same(),
            "the host imports the same file as a second, separate PC");
    }

    private static bool Throws(Action a)
    {
        try { a(); return false; }
        catch (ArgumentOutOfRangeException) { return true; }
    }
}

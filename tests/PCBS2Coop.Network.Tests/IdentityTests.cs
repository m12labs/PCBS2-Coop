using LiteNetLib.Utils;
using PCBS2Coop.Network;

internal static class IdentityTests
{
    public static void Run(Checker t)
    {
        t.Section("Identity: object IDs and registry");
        {
            var alloc = new NetObjectIdAllocator();
            var a = alloc.Allocate(); var b = alloc.Allocate();
            t.Check(a.Value == 1 && b.Value == 2 && !a.IsNone, "object IDs start at 1 and only grow");
            alloc.Next = 500;
            t.Check(alloc.Allocate().Value == 500, "allocator resumes from a restored value");

            var reg = new NetObjectRegistry<object>();
            var pc = new object();
            reg.Bind(a, pc);
            t.Check(reg.Lookup(a) == ObjectLookup.Live && reg.TryGet(a, out var got) && ReferenceEquals(got, pc), "bound object resolves");
            t.Check(reg.TryGetId(pc, out var back) && back == a, "reverse lookup by reference");
            t.Check(Throws(() => reg.Bind(b, pc)), "one object can't have two IDs");
            t.Check(Throws(() => reg.Bind(a, new object())), "one ID can't name two objects");
            t.Check(Throws(() => reg.Bind(NetObjectId.None, new object())), "'no object' can't be bound");
            t.Check(reg.Retire(a) && reg.Lookup(a) == ObjectLookup.Retired && !reg.TryGet(a, out _), "retired ID no longer resolves and is remembered as deleted");
            t.Check(Throws(() => reg.Bind(a, new object())), "a retired ID is never reused");
            t.Check(reg.Lookup(b) == ObjectLookup.Unknown, "never-bound ID is unknown");
            reg.Clear();
            t.Check(reg.Count == 0 && reg.RetiredCount == 0 && reg.Lookup(a) == ObjectLookup.Unknown, "world reset clears bindings and tombstones");
        }

        t.Section("Identity: player IDs and wire format");
        {
            var p = PlayerId.New();
            var w = new NetDataWriter(); p.Write(w);
            t.Check(PlayerId.Read(new NetDataReader(w.CopyData())) == p, "player ID round-trips");
            t.Check(PlayerId.TryParse(p.Value.ToString(), out var parsed) && parsed == p, "player ID parses from config text");
            t.Check(!PlayerId.TryParse("", out _) && !PlayerId.TryParse(Guid.Empty.ToString(), out _), "empty/blank player IDs are rejected");

            var id = Ids.Make(dlcs: new[] { "A", "B" });
            var iw = new NetDataWriter(); id.Write(iw);
            var read = PeerIdentity.Read(new NetDataReader(iw.CopyData()));
            t.Check(read.Player == id.Player && read.Game == id.Game && read.Content.Dlcs.SequenceEqual(id.Content.Dlcs)
                    && read.Content.CatalogHash == id.Content.CatalogHash && read.Content.CatalogCount == id.Content.CatalogCount,
                "peer identity round-trips");
            t.Check(Throws(() => PeerIdentity.Read(new NetDataReader(iw.CopyData()[..10]))), "truncated identity is rejected");

            t.Check(new PlayerState(1, 2, 3, 90, true).IsValid, "normal player state is valid");
            t.Check(!new PlayerState(float.NaN, 0, 0, 0, true).IsValid && !new PlayerState(0, float.PositiveInfinity, 0, 0, true).IsValid
                    && !new PlayerState(0, 0, 20_000, 0, true).IsValid, "NaN, infinite or far-away positions are invalid");
            t.Check(Compatibility.HashIds(new[] { "b", "a", "a" }) == Compatibility.HashIds(new[] { "a", "b" }), "catalog hash ignores order and duplicates");
        }

        t.Section("Compatibility rules (owner decision: refuse game build, warn DLC/catalog)");
        {
            var host = Ids.Make();
            t.Check(Compatibility.Check(host, Ids.Make()) is { Compatible: true, Warnings.Count: 0 }, "identical installs: compatible, no warnings");
            var build = Compatibility.Check(host, Ids.Make(build: "ffff"));
            t.Check(!build.Compatible && build.Problems[0].Contains("game builds"), "different game build refused");
            var mod = Compatibility.Check(host, host with { ModVersion = "0.0.1", Player = PlayerId.New() });
            t.Check(!mod.Compatible && mod.Problems[0].Contains("mod versions"), "different mod version refused");
            var dlc = Compatibility.Check(host, Ids.Make(dlcs: new[] { "DLCPack_PCBS1DecoratorTheme" }));
            t.Check(dlc.Compatible && dlc.Warnings.Count == 2 && dlc.Warnings.Any(x => x.Contains("only the host")), "different DLC allowed with warnings naming each side");
            var cat = Compatibility.Check(host, Ids.Make(catalog: "zzz", parts: 99));
            t.Check(cat.Compatible && cat.Warnings.Any(x => x.Contains("part catalogs")), "different part catalog allowed with warning");
            var unknown = Compatibility.Check(host, Ids.Make(parts: -1));
            t.Check(unknown.Compatible && unknown.Warnings.Any(x => x.Contains("not compared")), "unloaded catalog allowed with 'not compared' warning");
            var same = Compatibility.Check(host, Ids.Make(player: host.Player));
            t.Check(same.Compatible && same.Warnings.Any(x => x.Contains("same install ID")), "same install ID allowed with warning");
            var noGame = Compatibility.Check(host, host with { Game = GameIdentity.Unknown, Player = PlayerId.New() });
            t.Check(noGame.Compatible && noGame.Warnings.Any(x => x.Contains("game build could not be read")), "unreadable game build warns instead of refusing");
        }
    }

    private static bool Throws(Action a)
    {
        try { a(); return false; }
        catch (Exception) { return true; }
    }
}

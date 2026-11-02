using PCBS2Coop.Network;

internal sealed class FakeToolBenchNative : IToolBenchNative
{
    public readonly List<string> Calls = new();
    public readonly List<byte[]?> Results = new();
    public readonly List<int> Designs = new();
    public int NextDesign;
    public readonly List<string> Asked = new();
    public void Used(in MutationContext ctx, BenchUse use) => Calls.Add($"used {use.Bench} {use.Tool} {use.Actor} local={ctx.FromLocalPlayer}");
    public void Left(in MutationContext ctx, BenchUse use, byte[]? result, int design)
    {
        Calls.Add($"left {use.Bench} {use.Actor} {(result == null ? "nothing" : result.Length + " bytes")} local={ctx.FromLocalPlayer}");
        Results.Add(result); Designs.Add(design);
    }
    public int DesignId(BenchUse use, byte[] result) { Asked.Add($"{use.Bench} {use.Actor} {result.Length}"); return NextDesign; }
    public void Switched(in MutationContext ctx, string bench, byte kind, bool orbit) => Calls.Add($"switched {bench} {kind} {orbit} local={ctx.FromLocalPlayer}");
}

internal static class ToolBenchTests
{
    public static void Run(Checker t)
    {
        t.Section("Tool tables (phase 9): one player per table, the result into the other inventory");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hn = new FakeToolBenchNative(); var gn = new FakeToolBenchNative();
        var hb = new ToolBenchWorld(ha, hn); var gb = new ToolBenchWorld(ga, gn);
        l.Host.RegisterState(ho); l.Guest.RegisterState(go);
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.Register(CommandTypes.ToolBench, hb); l.Guest.Register(CommandTypes.ToolBench, gb);
        l.Host.BeginGeneration("tool tables");
        ho.Replace(Guid.NewGuid(), 1, Array.Empty<LocationFact>(), Array.Empty<PcFact>(), Array.Empty<NetObjectId>());
        var host = PlayerId.New(); var guest = PlayerId.New();
        void Send(WorldSync world, byte[] payload, ushort type = CommandTypes.ToolBench) { world.Submit(type, payload, out _); l.Flush(); }
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, host), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, guest), CommandTypes.ActorActivity); l.Join();
        ulong H() => ha.Get(ActorSlot.Host)!.Epoch; ulong G() => ga.Get(ActorSlot.Guest)!.Epoch;
        string Uses(ToolBenchWorld w) => string.Join(";", w.Uses.OrderBy(u => u.Bench).Select(u => $"{u.Bench}:{u.Actor}:{u.Epoch}:{u.Tool}"));
        bool Same() => Uses(hb) == Uses(gb) && l.InStep && l.Divergences.Count == 0;

        Send(l.Guest, ToolBenchWorld.Use(G(), "bench2", BenchTool.Delid));
        t.Check(gb.At("bench2") is { Actor: ActorSlot.Guest, Tool: BenchTool.Delid } && hb.Of(ActorSlot.Guest)?.Bench == "bench2" &&
            hn.Calls.Last() == "used bench2 Delid Guest local=False" && gn.Calls.Last() == "used bench2 Delid Guest local=True" && Same(),
            "a guest starts delidding at a table: both peers know who uses it");
        var rev = l.Host.Revision;
        Send(l.Host, ToolBenchWorld.Use(H(), "bench2", BenchTool.Delid));
        t.Check(l.Host.Revision == rev && l.HostRejected.Last().Text.EndsWith("Someone is using the delidding table.") &&
            hb.Busy("bench2", ActorSlot.Host) == "Someone is using the delidding table." && hb.Busy("bench2", ActorSlot.Guest) == null,
            "the host is told someone is using the delidding table");
        Send(l.Guest, ToolBenchWorld.Use(G(), "bench2", BenchTool.Delid));
        t.Check(l.Host.Revision == rev + 1 && Same(), "announcing the same table again is accepted (the host does it when a guest arrives)");
        Send(l.Guest, ToolBenchWorld.Use(G(), "bench3", BenchTool.WaterCooling));
        Send(l.Guest, ToolBenchWorld.Use(G(), "bench3", (BenchTool)9));
        Send(l.Host, ToolBenchWorld.Leave(H(), "bench2", BenchTool.Delid, null));
        t.Check(l.GuestRejected.TakeLast(2).Select(x => x.Text).ToArray() is var bad && bad[0].Contains("another tool table") && bad[1].Contains("Unknown tool table") &&
            l.HostRejected.Last().Text.Contains("not using that tool table"), "one table per player, known tools only, and only the user leaves");
        Send(l.Host, ToolBenchWorld.Use(H(), "bench3", BenchTool.WaterCooling));
        t.Check(hb.At("bench3")?.Tool == BenchTool.WaterCooling && hb.Uses.Count() == 2 && Same(), "the host water-cools at another table meanwhile");
        Send(l.Guest, ToolBenchWorld.Use(G(), "bench3", BenchTool.WaterCooling));
        t.Check(l.GuestRejected.Last().Text.EndsWith("Someone is using the water-cooling table."), "and the guest is told so at that table");

        // The finished CPU goes into the other inventory: chunks in order, then the leave with length and hash.
        var result = Enumerable.Range(0, ToolBenchWorld.MaxChunkBytes + 50).Select(i => (byte)(i * 13)).ToArray();
        var one = result.Take(ToolBenchWorld.MaxChunkBytes).ToArray(); var two = result.Skip(ToolBenchWorld.MaxChunkBytes).ToArray();
        rev = l.Host.Revision;
        Send(l.Guest, ToolBenchWorld.Chunk(G(), "bench2", 1, 2, two));
        Send(l.Guest, ToolBenchWorld.Chunk(G(), "bench2", 0, 2, one));
        Send(l.Guest, ToolBenchWorld.Leave(G(), "bench2", BenchTool.Delid, result));
        t.Check(l.GuestRejected.TakeLast(2).Select(x => x.Text).ToArray() is var cbad && cbad[0].Contains("out of order") && cbad[1].Contains("fully arrived") &&
            gb.At("bench2") != null, "chunks arrive in order and the leave waits for the whole result");
        Send(l.Guest, ToolBenchWorld.Chunk(G(), "bench2", 1, 2, two));
        Send(l.Guest, ToolBenchWorld.Leave(G(), "bench2", BenchTool.Delid, result.Reverse().ToArray()));
        t.Check(l.GuestRejected.Last().Text.Contains("doesn't match"), "a result that doesn't match its hash is refused");
        Send(l.Guest, ToolBenchWorld.Leave(G(), "bench2", BenchTool.Delid, result));
        t.Check(gb.At("bench2") == null && hn.Results.Last() is { } got && got.SequenceEqual(result) && gn.Results.Last()!.SequenceEqual(result) &&
            hn.Calls.Last().StartsWith("left bench2 Guest") && Same(), "the guest leaves the table: both peers get the result and the table is free");
        Send(l.Host, ToolBenchWorld.Leave(H(), "bench3", BenchTool.WaterCooling, null));
        t.Check(hb.At("bench3") == null && gn.Calls.Last() == "left bench3 Host nothing local=False" && Same(), "a session that changed nothing just frees the table");

        var malformed = l.GuestRejected.Count;
        Send(l.Guest, ToolBenchWorld.Use(G(), "", BenchTool.Delid));
        Send(l.Guest, ToolBenchWorld.Use(G(), "a\nb", BenchTool.Delid));
        t.Check(l.GuestRejected.Count == malformed + 2 && l.GuestRejected.TakeLast(2).All(x => x.Reason == RejectReason.Malformed), "a table needs a plain key");
        Send(l.Guest, ToolBenchWorld.Use(G(), "bench2", BenchTool.Delid));
        Send(l.Guest, ToolBenchWorld.Chunk(G(), "bench2", 0, 1, new byte[ToolBenchWorld.MaxChunkBytes + 1]));
        t.Check(l.GuestRejected.Last().Reason == RejectReason.Malformed, "an oversized chunk is malformed");

        // A guest that drops out of the session leaves its table: the next epoch doesn't own it.
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, default), CommandTypes.ActorActivity);
        t.Check(hb.At("bench2") == null && hb.Busy("bench2", ActorSlot.Host) == null, "a departed guest's table is free again");
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, guest), CommandTypes.ActorActivity);
        t.Check(hb.At("bench2") == null && gb.At("bench2") == null, "a rejoined guest (new epoch) doesn't own it either");
        Send(l.Host, ToolBenchWorld.Use(H(), "bench2", BenchTool.Delid));
        t.Check(hb.At("bench2")?.Actor == ActorSlot.Host && Same(), "the host can use that table now");
        gb.Reset(); // the guest joined during the host's session: uses aren't in snapshots
        var small = new byte[] { 1, 2, 3 };
        Send(l.Host, ToolBenchWorld.Chunk(H(), "bench2", 0, 1, small));
        Send(l.Host, ToolBenchWorld.Leave(H(), "bench2", BenchTool.Delid, small));
        t.Check(gn.Results.Last() is { } late && late.SequenceEqual(small) && gb.At("bench2") == null && l.InStep && l.Divergences.Count == 0,
            "a guest that joined during the host's session still gets its result, without going out of sync");
        t.Check(hn.Designs.All(d => d == 0) && hn.Asked.Count == 0, "delid and water results never ask for a case design ID");

        t.Section("Case-modding table and switching tables (phase 11)");
        Send(l.Guest, ToolBenchWorld.Use(G(), "mod1", BenchTool.CaseModding));
        t.Check(hb.At("mod1")?.Tool == BenchTool.CaseModding && Same(), "a guest starts case modding at a table");
        Send(l.Host, ToolBenchWorld.Use(H(), "mod1", BenchTool.CaseModding));
        t.Check(l.HostRejected.Last().Text.EndsWith("Someone is using the case modding table."), "the host is told someone is using the case modding table");
        Send(l.Host, ToolBenchWorld.Switch(H(), "mod1", ToolBenchWorld.NoTool, true));
        t.Check(l.HostRejected.Last().Text.EndsWith("Someone is using the case modding table.") && !hn.Calls.Any(c => c.StartsWith("switched")),
            "nobody switches a table someone else is using");
        var design = Enumerable.Range(0, 900).Select(i => (byte)(i * 7)).ToArray();
        Send(l.Guest, ToolBenchWorld.Chunk(G(), "mod1", 0, 1, design));
        Send(l.Guest, ToolBenchWorld.Leave(G(), "mod1", BenchTool.Delid, design));
        t.Check(l.GuestRejected.Last().Text.Contains("another tool") && gb.At("mod1") != null, "a leave names the tool the table was started with");
        hn.NextDesign = 42;
        Send(l.Guest, ToolBenchWorld.Leave(G(), "mod1", BenchTool.CaseModding, design));
        t.Check(hn.Asked.Last() == "mod1 Guest 900" && hn.Designs.Last() == 42 && gn.Designs.Last() == 42 &&
            hn.Results.Last()!.SequenceEqual(design) && gn.Results.Last()!.SequenceEqual(design) && hb.At("mod1") == null && Same(),
            "the finished design reaches both peers with the design ID the host gave out");
        Send(l.Host, ToolBenchWorld.Use(H(), "mod1", BenchTool.CaseModding));
        Send(l.Host, ToolBenchWorld.Leave(H(), "mod1", BenchTool.CaseModding, null));
        t.Check(hn.Designs.Last() == 0 && gn.Calls.Last() == "left mod1 Host nothing local=False" && hn.Asked.Count == 1 && Same(),
            "a case-modding session that changed nothing frees the table without a design ID");
        hn.NextDesign = -5;
        Send(l.Host, ToolBenchWorld.Use(H(), "mod1", BenchTool.CaseModding));
        Send(l.Host, ToolBenchWorld.Chunk(H(), "mod1", 0, 1, small));
        Send(l.Host, ToolBenchWorld.Leave(H(), "mod1", BenchTool.CaseModding, small));
        t.Check(hn.Designs.Last() == 0 && gn.Designs.Last() == 0 && Same(), "an unusable host answer keeps the editor's own design ID");

        Send(l.Guest, ToolBenchWorld.Switch(G(), "mod1", 2, false));
        t.Check(hn.Calls.Last() == "switched mod1 2 False local=False" && gn.Calls.Last() == "switched mod1 2 False local=True" && Same(),
            "a guest switches a free table to case modding: both games switch it");
        Send(l.Host, ToolBenchWorld.Switch(H(), "mod1", ToolBenchWorld.NoTool, true));
        t.Check(gn.Calls.Last() == "switched mod1 4 True local=False", "and the host switches it back to working on the PC");
        Send(l.Host, ToolBenchWorld.Switch(H(), "mod1", 3, true));
        t.Check(l.HostRejected.Last().Text.Contains("Unknown tool table type"), "only the game's table types");
        hb.Reset();
        t.Check(!hb.Uses.Any(), "a new world generation starts with free tables");
    }
}

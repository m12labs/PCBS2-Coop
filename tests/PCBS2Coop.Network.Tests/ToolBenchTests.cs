using PCBS2Coop.Network;

internal sealed class FakeToolBenchNative : IToolBenchNative
{
    public readonly List<string> Calls = new();
    public readonly List<byte[]?> Results = new();
    public void Used(in MutationContext ctx, BenchUse use) => Calls.Add($"used {use.Bench} {use.Tool} {use.Actor} local={ctx.FromLocalPlayer}");
    public void Left(in MutationContext ctx, BenchUse use, byte[]? result)
    {
        Calls.Add($"left {use.Bench} {use.Actor} {(result == null ? "nothing" : result.Length + " bytes")} local={ctx.FromLocalPlayer}");
        Results.Add(result);
    }
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
        Send(l.Host, ToolBenchWorld.Leave(H(), "bench2", null));
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
        Send(l.Guest, ToolBenchWorld.Leave(G(), "bench2", result));
        t.Check(l.GuestRejected.TakeLast(2).Select(x => x.Text).ToArray() is var cbad && cbad[0].Contains("out of order") && cbad[1].Contains("fully arrived") &&
            gb.At("bench2") != null, "chunks arrive in order and the leave waits for the whole result");
        Send(l.Guest, ToolBenchWorld.Chunk(G(), "bench2", 1, 2, two));
        Send(l.Guest, ToolBenchWorld.Leave(G(), "bench2", result.Reverse().ToArray()));
        t.Check(l.GuestRejected.Last().Text.Contains("doesn't match"), "a result that doesn't match its hash is refused");
        Send(l.Guest, ToolBenchWorld.Leave(G(), "bench2", result));
        t.Check(gb.At("bench2") == null && hn.Results.Last() is { } got && got.SequenceEqual(result) && gn.Results.Last()!.SequenceEqual(result) &&
            hn.Calls.Last().StartsWith("left bench2 Guest") && Same(), "the guest leaves the table: both peers get the result and the table is free");
        Send(l.Host, ToolBenchWorld.Leave(H(), "bench3", null));
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
        Send(l.Host, ToolBenchWorld.Leave(H(), "bench2", small));
        t.Check(gn.Results.Last() is { } late && late.SequenceEqual(small) && gb.At("bench2") == null && l.InStep && l.Divergences.Count == 0,
            "a guest that joined during the host's session still gets its result, without going out of sync");
        hb.Reset();
        t.Check(!hb.Uses.Any(), "a new world generation starts with free tables");
    }
}

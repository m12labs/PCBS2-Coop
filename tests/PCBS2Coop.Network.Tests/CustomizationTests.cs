using PCBS2Coop.Network;

internal sealed class FakeCustomizationNative : ICustomizationNative
{
    public readonly List<string> Calls = new();
    public readonly List<IReadOnlyList<LedLight>> Lights = new();
    public readonly List<byte[]> Purchases = new();
    public void LightsChanged(in MutationContext ctx, PcFact pc, LightTarget target, int preset, string slot, IReadOnlyList<LedLight> lights)
    {
        Calls.Add($"lights {pc.Id} {target} {preset} '{slot}' {lights.Count} local={ctx.FromLocalPlayer}");
        Lights.Add(lights);
    }
    public void Clock(in MutationContext ctx, PcFact pc, float time) => Calls.Add($"clock {pc.Id} {time} local={ctx.FromLocalPlayer}");
    public void Purchased(in MutationContext ctx, ActorSlot actor, byte[] result)
    {
        Calls.Add($"bought {actor} {result.Length} local={ctx.FromLocalPlayer}");
        Purchases.Add(result);
    }
}

internal static class CustomizationTests
{
    private static NetObjectId Id(ulong n) => new(n);

    private static LedValues Red(float speed = 1f) => new(1, 0, 0, 0, 0, 1, 1, speed, 1, 0, 1, 7, true, true, 0, 1, 0, 0, 2, 0.5f, true, true);

    public static void Run(Checker t)
    {
        t.Section("Lighting, the lighting clock and custom cable purchases (phase 11)");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hn = new FakeCustomizationNative(); var gn = new FakeCustomizationNative();
        var hw = new CustomizationWorld(ho, ha, hn); var gw = new CustomizationWorld(go, ga, gn);
        l.Host.RegisterState(ho); l.Guest.RegisterState(go);
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.Register(CommandTypes.Customization, hw); l.Guest.Register(CommandTypes.Customization, gw);
        l.Host.BeginGeneration("customization");
        var host = PlayerId.New(); var guest = PlayerId.New();
        var origin = new InstanceProvenance(InstanceOrigin.Baseline, host, default);
        var pc = new PcFact(Id(2), origin, PcLocation.Bench("A"), new[] { new PartFact(Id(3), "case", "CasePartInstance") }, new[] { new PartLink("caseID", Id(3)) });
        ho.Replace(Guid.NewGuid(), 4, new[] { new LocationFact(Id(1), "A") }, new[] { pc }, Array.Empty<NetObjectId>());
        void Send(WorldSync world, byte[] payload, ushort type = CommandTypes.Customization) { world.Submit(type, payload, out _); l.Flush(); }
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, host), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, guest), CommandTypes.ActorActivity); l.Join();
        ulong H() => ha.Get(ActorSlot.Host)!.Epoch; ulong G() => ga.Get(ActorSlot.Guest)!.Epoch;
        bool Same() => l.InStep && l.Divergences.Count == 0;

        var lights = new[] { new LedLight("Fan_1", 0, Red()), new LedLight("Fan_1", 1, Red(2)), new LedLight("RAM_0", 0, Red(3)) };
        Send(l.Guest, CustomizationWorld.Lights(G(), pc.Id, LightTarget.Pc, 1, "", lights));
        t.Check(hn.Calls.Last() == "lights obj:2 Pc 1 '' 3 local=False" && gn.Calls.Last().EndsWith("local=True") &&
            hn.Lights.Last().SequenceEqual(lights) && Same(), "a guest's Lighting change reaches the host, every value intact");
        Send(l.Host, CustomizationWorld.Lights(H(), pc.Id, LightTarget.Preset, 2, "", lights.Take(1).ToArray()));
        Send(l.Host, CustomizationWorld.Lights(H(), pc.Id, LightTarget.Peripheral, -1, "Desk/Keyboard", lights.Take(2).ToArray()));
        t.Check(gn.Calls.TakeLast(2).SequenceEqual(new[] { "lights obj:2 Preset 2 '' 1 local=False", "lights obj:2 Peripheral -1 'Desk/Keyboard' 2 local=False" }),
            "presets and peripheral lights travel the same way");
        var rejected = l.GuestRejected.Count;
        Send(l.Guest, CustomizationWorld.Lights(G(), Id(99), LightTarget.Pc, 0, "", lights));
        Send(l.Guest, CustomizationWorld.Lights(G(), pc.Id, LightTarget.Preset, 8, "", lights));
        Send(l.Guest, CustomizationWorld.Lights(G(), pc.Id, LightTarget.Peripheral, 0, "", lights));
        Send(l.Guest, CustomizationWorld.Lights(G(), pc.Id, LightTarget.Pc, 0, "slot", lights));
        t.Check(l.GuestRejected.Count == rejected + 4 && l.GuestRejected.Skip(rejected).Select(x => x.Text).ToArray() is var why &&
            why[0].Contains("isn't in the shared world") && why[1].Contains("preset") && why[2].Contains("peripheral") && why[3].Contains("peripheral"),
            "unknown PCs, presets and peripherals are refused");
        Send(l.Guest, CustomizationWorld.Lights(G(), pc.Id, LightTarget.Pc, 0, "", new[] { new LedLight("Fan", 0, Red(float.NaN)) }));
        Send(l.Guest, CustomizationWorld.Lights(G(), pc.Id, LightTarget.Pc, 0, "", new[] { new LedLight("Fan", 0, Red() with { Effect = 7 }) }));
        Send(l.Guest, CustomizationWorld.Lights(G(), pc.Id, LightTarget.Pc, 0, "", new[] { new LedLight("a\nb", 0, Red()) }));
        t.Check(l.GuestRejected.TakeLast(3).All(x => x.Reason == RejectReason.Malformed), "broken light values are malformed");
        var many = Enumerable.Range(0, CustomizationWorld.MaxLights).Select(i => new LedLight("Group_" + i, i % 4, Red(i))).ToArray();
        var big = CustomizationWorld.Lights(G(), pc.Id, LightTarget.Pc, 0, "", many);
        Send(l.Guest, big);
        t.Check(big.Length <= Protocol.MaxPayloadBytes && hn.Lights.Last().Count == CustomizationWorld.MaxLights, "a full batch of lights fits in one command");

        Send(l.Host, CustomizationWorld.Clock(H(), pc.Id, 12.5f));
        t.Check(gn.Calls.Last() == "clock obj:2 12.5 local=False" && Same(), "the host's lighting clock reaches the guest");
        Send(l.Guest, CustomizationWorld.Clock(G(), pc.Id, 3f));
        t.Check(l.GuestRejected.Last().Text.Contains("Only the host"), "only the host keeps the clock");

        // A bought cable set: chunks, then the purchase with length and hash.
        var bought = Enumerable.Range(0, CustomizationWorld.MaxChunkBytes + 99).Select(i => (byte)(i * 31)).ToArray();
        var one = bought.Take(CustomizationWorld.MaxChunkBytes).ToArray(); var two = bought.Skip(CustomizationWorld.MaxChunkBytes).ToArray();
        Send(l.Guest, CustomizationWorld.Chunk(G(), 1, 2, two));
        Send(l.Guest, CustomizationWorld.Purchase(G(), bought));
        t.Check(l.GuestRejected.TakeLast(2).Select(x => x.Text).ToArray() is var p && p[0].Contains("out of order") && p[1].Contains("fully arrived"),
            "a purchase arrives in order and whole");
        Send(l.Guest, CustomizationWorld.Chunk(G(), 0, 2, one));
        Send(l.Guest, CustomizationWorld.Chunk(G(), 1, 2, two));
        Send(l.Guest, CustomizationWorld.Purchase(G(), bought.Reverse().ToArray()));
        t.Check(l.GuestRejected.Last().Text.Contains("doesn't match"), "a purchase that doesn't match its hash is refused");
        Send(l.Guest, CustomizationWorld.Purchase(G(), bought));
        t.Check(hn.Calls.Last() == $"bought Guest {bought.Length} local=False" && gn.Calls.Last().EndsWith("local=True") &&
            hn.Purchases.Last().SequenceEqual(bought) && gn.Purchases.Last().SequenceEqual(bought) && Same(), "the bought set reaches both games");
        Send(l.Guest, CustomizationWorld.Purchase(G(), bought));
        t.Check(l.GuestRejected.Last().Text.Contains("fully arrived"), "a purchase is shared once");
        Send(l.Host, CustomizationWorld.Chunk(H(), 0, 1, new byte[] { 5 }));
        gw.Reset(); // a guest that joined halfway through the host's purchase
        Send(l.Host, CustomizationWorld.Purchase(H(), new byte[] { 5 }));
        t.Check(hn.Calls.Last() == "bought Host 1 local=True" && !gn.Calls.Last().StartsWith("bought Host") && Same(),
            "a guest that missed part of a purchase skips it without going out of sync (the host's save has it)");
    }
}

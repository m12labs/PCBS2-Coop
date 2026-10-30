using LiteNetLib.Utils;
using PCBS2Coop.Network;

/// <summary>Stands in for the native edit adapter: a scripted host check, a scripted host capture of the edited PC
/// (what the host's game shows after the edit) and a log of native callbacks.</summary>
internal sealed class FakeEditNative : IEditNative
{
    public Func<ComponentEdit, string?> Host = _ => null;
    public Func<PcFact, ComponentEdit, NetObjectIdAllocator, PcFact> Capture = (pc, _, _) => pc;
    public readonly List<string> Calls = new();
    public string? CheckHost(in CommandContext ctx, PcFact pc, AssemblyResource? slot, ComponentEdit edit) => Host(edit);
    PcFact IEditNative.Capture(PcFact pc, ComponentEdit edit, NetObjectIdAllocator allocator) => Capture(pc, edit, allocator);
    public void Began(in MutationContext ctx, PcFact pc, ComponentEdit edit) => Calls.Add($"began {edit.Gesture} {edit.Slot.Value} local={ctx.FromLocalPlayer}");
    public void Progressed(in MutationContext ctx, PcFact pc, ComponentEdit edit, EditProgress progress) =>
        Calls.Add($"progressed {progress} {edit.Slot.Value} local={ctx.FromLocalPlayer}");
    public void Committed(in MutationContext ctx, PcFact pc, ComponentEdit edit) =>
        Calls.Add($"committed {edit.Result!.Outcome} {edit.Result.Degrees} local={ctx.FromLocalPlayer}");
    public void Cancelled(in MutationContext ctx, ComponentEdit edit) => Calls.Add($"cancelled {edit.Slot.Value} local={ctx.FromLocalPlayer}");
    public void Settled(in MutationContext ctx, PcFact pc, ComponentEdit edit) => Calls.Add($"settled {edit.Slot.Value} local={ctx.FromLocalPlayer}");
}

internal static class ComponentEditTests
{
    private static NetObjectId Id(ulong n) => new(n);
    private static bool Throws(Action a) { try { a(); return false; } catch (InvalidDataException) { return true; } }
    private static AssemblyResource Slot(ulong id, ulong parent, string path, ulong occupant = 0) =>
        new(Id(id), Id(parent), path, "Slot", AssemblyResourceKind.Slot, Id(occupant), false, 0, 0, Array.Empty<NetObjectId>());
    private static AssemblyResource Screw(ulong id, ulong parent, string path) =>
        new(Id(id), Id(parent), path, "Screw", AssemblyResourceKind.Screw, default, false, 0, 0, Array.Empty<NetObjectId>());

    /// <summary>PC 2 on bench A: case 3 with board 4 in slot 8 and a card 5 in slot 9 (its two screws 11 and 12 belong
    /// to the card), an empty RAM slot 10 with clip 14, the side panel 15 and a PCI cover 16 (integral, no home).</summary>
    private static PcFact Fixture(PlayerId creator) => new(Id(2), new(InstanceOrigin.Baseline, creator, default), PcLocation.Bench("A"),
        new[] { new PartFact(Id(3), "case", "CasePartInstance"), new PartFact(Id(4), "board", "MotherboardPartInstance"), new PartFact(Id(5), "gpu", "GpuPartInstance") },
        new[] { new PartLink("caseID", Id(3)), new PartLink("motherboardID", Id(4)), new PartLink("gpus[0]", Id(5)) })
    {
        Resources = new[]
        {
            Slot(8, 3, "Slot:Slots/Slot_Motherboard#0", 4),
            Slot(9, 4, "Slot:PCIE[0]#0", 5) with { Coupled = new[] { Id(11), Id(12) } },
            Slot(10, 4, "Slot:RAM[0]#0") with { Coupled = new[] { Id(14) }, LoadCloses = new[] { Id(14) } },
            Screw(11, 5, "Screw:Screw#0"), Screw(12, 5, "Screw:Screw1#0"),
            new AssemblyResource(Id(14), Id(4), "Pin:RAM[0]/Clip#0", "Pin", AssemblyResourceKind.Pin, default, true, 0, 0, Array.Empty<NetObjectId>()),
            Slot(15, 3, "Slot:Slots/Slot_LeftLid#0") with { IntegralPart = "case/LeftLid", IntegralHome = "case/LeftLid" },
            Slot(16, 3, "Slot:PCI Covers/PCI_Cover#0") with { IntegralPart = "case/PCI_Shield" },
        }
    };

    /// <summary>The host's game after the edit: an installed stick (one new part, its own screw) or a removed card.</summary>
    private static PcFact Edited(PcFact pc, ComponentEdit edit, NetObjectIdAllocator allocator)
    {
        if (edit.Rebuilt)
        {
            // The host's game after reloading the editor's finished PC: a new water block in the free RAM slot.
            var block = allocator.Allocate();
            return pc with
            {
                Parts = pc.Parts.Append(new PartFact(block, edit.Part, "PartInstance")).ToArray(),
                Links = pc.Links.Append(new PartLink("waterBlock", block)).ToArray(),
                Powered = edit.Result?.Rebuild?.Powered ?? false,
            };
        }
        if (edit.Wired)
        {
            var wiring = edit.Result?.Wiring;
            var next = pc with { Powered = wiring?.Powered ?? pc.Powered };
            if (wiring?.Accessories.FirstOrDefault(a => a.Sku != "") is { } added)
            {
                var splitter = allocator.Allocate();
                next = next with
                {
                    Parts = pc.Parts.Append(new PartFact(splitter, added.Sku, "PowerSplitterPartInstance")).ToArray(),
                    Links = pc.Links.Append(new PartLink($"psuSplitterIDs[{added.Index}]", splitter)).ToArray(),
                };
            }
            return next;
        }
        var slot = pc.Resources.Single(r => r.Id == edit.Slot);
        if (edit.Install)
        {
            var part = allocator.Allocate(); var screw = allocator.Allocate();
            return pc with
            {
                Parts = pc.Parts.Append(new PartFact(part, edit.Part, "RamPartInstance")).ToArray(),
                Links = pc.Links.Append(new PartLink($"part@{slot.Id.Value}", part)).ToArray(),
                Resources = pc.Resources.Select(r => r.Id == slot.Id ? r with { Occupant = part, Version = r.Version + 1 } :
                    r.Id == Id(14) ? r with { Open = false, Version = r.Version + 1 } : r).Append(Screw(screw.Value, part.Value, "Screw:Ram#0")).ToArray(),
            };
        }
        if (edit.Result?.Outcome == EditOutcome.Unplugged) return pc;
        if (slot.IntegralPart != "")
            return pc with { Resources = pc.Resources.Select(r => r.Id == slot.Id ? r with { IntegralPart = "", Version = r.Version + 1 } : r).ToArray() };
        var gone = edit.Component;
        return pc with
        {
            Parts = pc.Parts.Where(p => p.Id != gone).ToArray(),
            Links = pc.Links.Where(l => l.Part != gone).ToArray(),
            Resources = pc.Resources.Where(r => r.Parent != gone).Select(r => r.Id == slot.Id ? r with { Occupant = default, Coupled = Array.Empty<NetObjectId>(), Version = r.Version + 1 } : r).ToArray(),
        };
    }

    private static byte[] WiringBytes(Wiring wiring) { var w = new NetDataWriter(); wiring.Write(w); return w.CopyData(); }
    private static readonly CableFact Cable24 = new("PSU_24", "MB_24", "cable/24pin", "conn/24", false, true, new[] { 4, 4, 2 }, Array.Empty<float>());

    public static void Run(Checker t)
    {
        t.Section("Component edits: begin, commit and settle");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hn = new FakeEditNative { Capture = Edited }; var gn = new FakeEditNative();
        var he = new ComponentEditWorld(ho, ha, hn); var ge = new ComponentEditWorld(go, ga, gn);
        l.Host.RegisterState(ho); l.Guest.RegisterState(go);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.Register(CommandTypes.ComponentEdit, he); l.Guest.Register(CommandTypes.ComponentEdit, ge);
        l.Host.Register(CommandTypes.Mechanism, new MechanismWorld(ho, ha, new FakeMechanismNative(), true));
        l.Guest.Register(CommandTypes.Mechanism, new MechanismWorld(go, ga, new FakeMechanismNative(), false));
        l.Host.Register(CommandTypes.CasePanel, new CasePanelWorld(ho, ha, new FakePanelNative()));
        l.Guest.Register(CommandTypes.CasePanel, new CasePanelWorld(go, ga, new FakePanelNative()));
        l.Host.BeginGeneration("edits");
        var host = PlayerId.New(); var guest = PlayerId.New();
        ho.Replace(Guid.NewGuid(), 17, new[] { new LocationFact(Id(1), "A") }, new[] { Fixture(host) }, new[] { Id(6), Id(7), Id(13) });
        OperationId Send(WorldSync w, byte[] bytes, ushort type = CommandTypes.ComponentEdit) { w.Submit(type, bytes, out var op); l.Flush(); return op; }
        ActorActivity H() => ha.Get(ActorSlot.Host)!; ActorActivity G() => ga.Get(ActorSlot.Guest)!;
        PcFact P(ObjectWorld o) => o.Pcs.Single();
        AssemblyResource R(ObjectWorld o, ulong id) => P(o).Resources.Single(r => r.Id == Id(id));
        byte[] Install(ActorActivity a, ulong slot, ObjectWorld o, string sku = "ram") =>
            ComponentEditWorld.Begin(a.Epoch, Id(2), Id(slot), R(o, slot).Version, EditGesture.Install, sku, default);
        byte[] Remove(ActorActivity a, ulong slot, ObjectWorld o, EditGesture gesture = EditGesture.Remove) =>
            ComponentEditWorld.Begin(a.Epoch, Id(2), Id(slot), R(o, slot).Version, gesture, "", R(o, slot).Occupant);
        byte[] Bytes(ActivityWorld a) { var w = new NetDataWriter(); a.Write(w); return w.CopyData(); }
        string Key(ComponentEdit e) => $"{e.Token}|{e.Actor}|{e.Epoch}|{e.Pc}|{e.Slot}|{e.Gesture}|{e.Part}|{e.Component}|{string.Join(",", e.Targets)}|{e.Stage}|" +
            (e.Result is { } r ? $"{r.Outcome}|{r.Degrees}|{r.Flipped}|{r.Mode}|{r.Selected}|{r.FromInventory}|{r.CableType}|{r.CableConnector}|{string.Join(",", r.Pins)}|" +
                (r.Wiring is { } wr ? Convert.ToHexString(WiringBytes(wr)) : "") +
                (r.Rebuild is { } rb ? $"|{rb.Length}|{Convert.ToHexString(rb.Hash.ToArray())}|{rb.Powered}" : "") : "") + $"|{e.Start}|{e.PartStep}";
        bool Same() => ho.ToBytes().SequenceEqual(go.ToBytes()) && Bytes(ha).SequenceEqual(Bytes(ga)) &&
            he.Edits.Select(Key).SequenceEqual(ge.Edits.Select(Key)) && l.InStep && l.Divergences.Count == 0;
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, host), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, guest), CommandTypes.ActorActivity);
        l.Join();

        var rev = l.Host.Revision;
        Send(l.Guest, Install(G(), 10, go));
        t.Check(l.Host.Revision == rev && l.GuestRejected.Last().Text.Contains("not working"), "a player not working on that PC can't change its parts");
        Send(l.Host, ActivityWorld.Work(H(), Id(2)), CommandTypes.ActorActivity);
        Send(l.Guest, ActivityWorld.Work(G(), Id(2)), CommandTypes.ActorActivity);

        rev = l.Host.Revision;
        Send(l.Guest, Install(G(), 9, go));
        Send(l.Guest, ComponentEditWorld.Begin(G().Epoch, Id(2), Id(9), R(go, 9).Version, EditGesture.Remove, "", Id(4)));
        Send(l.Guest, Remove(G(), 15, go));
        Send(l.Guest, Install(G(), 14, go));
        Send(l.Guest, ComponentEditWorld.Begin(G().Epoch, Id(2), Id(10), R(go, 10).Version + 1, EditGesture.Install, "ram", default));
        t.Check(l.Host.Revision == rev && l.GuestRejected.TakeLast(5).Select(x => x.Text).ToArray() is var why &&
            why[0].Contains("just filled") && why[1].Contains("just changed") && why[2].Contains("panels are changed separately") &&
            why[3].Contains("Unknown slot") && why[4].Contains("just changed"),
            "installing into a filled slot, removing a different part, a case panel, a latch named as a slot and a stale version are refused");

        var begin = Send(l.Guest, Install(G(), 10, go));
        var edit = ge.Find(begin);
        t.Check(edit is { Stage: EditStage.Running, Actor: ActorSlot.Guest, Part: "ram" } && G().Pending?.Token == begin &&
            gn.Calls.Last() == "began Install 10 local=True" && hn.Calls.Last() == "began Install 10 local=False" && Same(),
            "a guest begins installing a stick: both peers record the edit and its claim, and both games are told once");

        rev = l.Host.Revision;
        Send(l.Host, Remove(H(), 9, ho));
        Send(l.Host, MechanismWorld.Toggle(H().Epoch, Id(2), Id(14), R(ho, 14).Version, false), CommandTypes.Mechanism);
        Send(l.Host, CasePanelWorld.Remove(H().Epoch, Id(2), Id(15), R(ho, 15).Version), CommandTypes.CasePanel);
        Send(l.Guest, Remove(G(), 9, go));
        t.Check(l.Host.Revision == rev && l.HostRejected.TakeLast(3).All(x => x.Text.Contains("changing a part of that PC")) &&
            l.GuestRejected.Last().Text.Contains("still finishing"),
            "while it runs, the other player's edits, latch clicks and panel changes on that PC wait, and the editor starts one edit at a time");
        t.Check(ha.MovementBlock(Id(2)) != null, "and the PC can't be moved");

        rev = l.Host.Revision;
        Send(l.Host, ComponentEditWorld.Commit(H().Epoch, begin, EditResult.Done()));
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, EditResult.Done() with { Outcome = EditOutcome.Unplugged }));
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, EditResult.Done(float.PositiveInfinity)));
        Send(l.Guest, ComponentEditWorld.Settle(begin));
        t.Check(l.Host.Revision == rev && l.HostRejected.Last().Text.Contains("Only the editor") &&
            l.GuestRejected.TakeLast(3).Select(x => x.Text).ToArray() is var bad && bad[0].Contains("outcome") && bad[1].Contains("orientation") && bad[2].Contains("Only the host"),
            "only the editor commits, an install can't end unplugged, an infinite angle is refused and a guest can't settle");

        var result = EditResult.Done(180) with { Mode = 1, Selected = 1, CableType = "cable/black", Pins = new[] { 2, 3 } };
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, result));
        t.Check(ge.Find(begin) is { Stage: EditStage.Committed } e && e.Result!.Degrees == 180 && e.Result.Pins.SequenceEqual(new[] { 2, 3 }) &&
            hn.Calls.Last() == "committed Done 180 local=False" && gn.Calls.Last() == "committed Done 180 local=True" && G().Pending?.Token == begin && Same(),
            "the commit carries the editor's result to both peers (the other game replays it) and keeps the claim");
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, result));
        t.Check(l.GuestRejected.Last().Text.Contains("already finished"), "a second commit is refused");

        var next = ho.NextId;
        Send(l.Host, ComponentEditWorld.Settle(begin));
        var stick = P(go).Links.Single(x => x.Path == "part@10").Part;
        t.Check(stick.Value == next && R(go, 10).Occupant == stick && R(go, 10).Version == 1 && !R(go, 14).Open && go.NextId == next + 2 &&
            ge.Edits.Count == 0 && G().Pending == null && gn.Calls.Last() == "settled 10 local=False" && Same(),
            "the host settles from its game: the stick and its screw get new IDs, the slot and clip advance, the claim is released");

        t.Section("Component edits: removals, Esc during the cable step, cancel and departure");
        begin = Send(l.Host, Remove(H(), 9, ho));
        Send(l.Host, ComponentEditWorld.Commit(H().Epoch, begin, EditResult.Done() with { Outcome = EditOutcome.Unplugged }));
        Send(l.Host, ComponentEditWorld.Settle(begin));
        t.Check(R(go, 9).Occupant == Id(5) && P(go).Parts.Any(p => p.Id == Id(5)) && ge.Edits.Count == 0 && H().Pending == null && Same(),
            "a removal the editor escaped during the cable step ends with the card still in");

        begin = Send(l.Guest, Remove(G(), 9, go, EditGesture.RemoveDirect));
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, EditResult.Done() with { Outcome = EditOutcome.Unplugged }));
        t.Check(l.GuestRejected.Last().Text.Contains("outcome"), "a direct removal has no cable step to escape");
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, EditResult.Done()));
        Send(l.Host, ComponentEditWorld.Settle(begin));
        t.Check(R(go, 9).Occupant.IsNone && !P(go).Parts.Any(p => p.Id == Id(5)) && new ulong[] { 5, 11, 12 }.All(i => go.Retired.Contains(Id(i))) &&
            go.Lookup(Id(11)) == ObjectLookup.Retired && Same(),
            "a guest's card removal retires the card and its screws on both peers");

        begin = Send(l.Guest, Remove(G(), 16, go));
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, EditResult.Done()));
        Send(l.Host, ComponentEditWorld.Settle(begin));
        t.Check(R(go, 16).IntegralPart == "" && R(go, 16).Version == 1 && Same(), "an integral PCI cover can be removed");

        rev = l.Host.Revision;
        begin = Send(l.Guest, Install(G(), 9, go, "gpu2"));
        Send(l.Guest, ComponentEditWorld.Cancel(G().Epoch, begin));
        t.Check(l.Host.Revision == rev + 2 && ge.Edits.Count == 0 && G().Pending == null && R(go, 9).Occupant.IsNone &&
            gn.Calls.Last() == "cancelled 9 local=True" && Same(), "an edit that never ran is cancelled without changing the PC");

        begin = Send(l.Guest, Install(G(), 9, go, "gpu2"));
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, EditResult.Done()));
        Send(l.Guest, ComponentEditWorld.Cancel(G().Epoch, begin));
        t.Check(l.GuestRejected.Last().Text.Contains("already finished"), "a committed edit can't be cancelled by the editor");
        Send(l.Host, ComponentEditWorld.Cancel(H().Epoch, begin));
        t.Check(ge.Edits.Count == 0 && G().Pending == null && Same(), "the host can drop an edit it can't settle");

        begin = Send(l.Guest, Install(G(), 9, go, "gpu2"));
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, EditResult.Done()));
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, default), CommandTypes.ActorActivity);
        t.Check(ga.Get(ActorSlot.Guest) == null && ge.Edits.Count == 1, "the editor leaving after its commit drops its claim but not the edit");
        Send(l.Host, ComponentEditWorld.Settle(begin));
        t.Check(ge.Edits.Count == 0 && R(go, 9).Occupant == P(go).Links.Single(x => x.Path == "part@9").Part && Same(),
            "the host still settles it from its own game");
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, guest), CommandTypes.ActorActivity);
        Send(l.Guest, ActivityWorld.Work(G(), Id(2)), CommandTypes.ActorActivity);

        t.Section("Component edits: host refusals and validation");
        rev = l.Host.Revision;
        hn.Host = _ => "Something is in the way of that slot.";
        Send(l.Guest, Remove(G(), 10, go));
        t.Check(l.Host.Revision == rev && l.GuestRejected.Last().Text.Contains("in the way") && ge.Edits.Count == 0, "a host-native refusal starts nothing");
        hn.Host = _ => null;

        begin = Send(l.Guest, Remove(G(), 10, go));
        hn.Capture = (pc, e, a) => pc with { Location = PcLocation.Bench("B") };
        Send(l.Host, ComponentEditWorld.Settle(begin));
        t.Check(l.HostRejected.Last().Text.Contains("identity or place") && ge.Edits.Count == 1 && l.Divergences.Count == 0, "a settlement can't move the PC");
        hn.Capture = (pc, e, a) => pc with { Parts = pc.Parts.Select(p => p.Id == Id(4) ? p with { Sku = "other" } : p).ToArray() };
        Send(l.Host, ComponentEditWorld.Settle(begin));
        t.Check(ge.Edits.Count == 1 && l.HostRejected.Last().Text.Contains("physical identity") && l.Divergences.Count == 0, "nor change a kept part's identity");
        hn.Capture = Edited;
        Send(l.Guest, ComponentEditWorld.Cancel(G().Epoch, begin));

        rev = l.Host.Revision; var malformed = l.GuestRejected.Count;
        Send(l.Guest, new byte[] { 9 });
        Send(l.Guest, Install(G(), 9, go).Concat(new byte[] { 1 }).ToArray());
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, EditResult.Done() with { Pins = new int[EditResult.MaxPins + 1] }));
        t.Check(l.Host.Revision == rev && l.GuestRejected.Count == malformed + 3 && l.GuestRejected.TakeLast(3).All(x => x.Reason == RejectReason.Malformed),
            "unknown actions, trailing bytes and too many cable pins are malformed");

        var calls = gn.Calls.Count;
        begin = Send(l.Guest, Remove(G(), 9, go));
        l.Guest.Submit(CommandTypes.ComponentEdit, ComponentEditWorld.Commit(G().Epoch, begin, EditResult.Done()), out _);
        var raw = l.ToHost[0]; l.Flush(); FakeLink.Deliver(l.Host, raw); l.Flush();
        t.Check(gn.Calls.Count == calls + 2 && ge.Find(begin)?.Stage == EditStage.Committed && Same(), "a duplicated commit applies and is shown exactly once");
        Send(l.Host, ComponentEditWorld.Settle(begin));
        t.Check(ge.Edits.Count == 0 && R(go, 9).Occupant.IsNone && Same(), "and settles normally");

        t.Section("Cable and power edits (phase 5)");
        byte[] Wire(ActorActivity a, EditGesture g, string part) => ComponentEditWorld.Begin(a.Epoch, Id(2), default, 0, g, part, default);
        rev = l.Host.Revision;
        Send(l.Guest, ComponentEditWorld.Begin(G().Epoch, Id(2), Id(10), 0, EditGesture.Connect, "PSU_24", default));
        Send(l.Guest, Wire(G(), EditGesture.Power, "maybe"));
        Send(l.Guest, Wire(G(), EditGesture.Unplug, ""));
        t.Check(l.Host.Revision == rev && l.GuestRejected.TakeLast(3).Select(x => x.Text).ToArray() is var wbad &&
            wbad[0].Contains("Invalid cable") && wbad[1].Contains("Invalid power") && wbad[2].Contains("Invalid cable"),
            "a cable edit names no slot, power is on or off, and an unplug names its cable");

        begin = Send(l.Guest, Wire(G(), EditGesture.Connect, "PSU_24"));
        t.Check(ge.Find(begin) is { Gesture: EditGesture.Connect, Targets.Count: 0, Wired: true } && G().Pending is { Targets.Count: 0 } &&
            gn.Calls.Last() == "began Connect 0 local=True" && Same(), "a guest starts plugging a cable in: the claim covers the whole PC");
        rev = l.Host.Revision;
        Send(l.Host, Remove(H(), 10, ho));
        Send(l.Host, MechanismWorld.Toggle(H().Epoch, Id(2), Id(14), R(ho, 14).Version, true), CommandTypes.Mechanism);
        Send(l.Host, Wire(H(), EditGesture.Power, "on"));
        Send(l.Host, ActivityWorld.Reserve(H(), Id(2), ActivityOperation.Edit, Id(10)), CommandTypes.ActorActivity);
        t.Check(l.Host.Revision == rev && l.HostRejected.TakeLast(4).Select(x => x.Text).ToArray() is var waits &&
            waits.Take(3).All(x => x.Contains("changing a part of that PC")) && waits[3].Contains("in use"),
            "while it runs, the other player's part edits, latches, power and resource claims on that PC wait");

        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, EditResult.Done()));
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, EditResult.Wired(Wiring.None with { Added = new[] { Cable24, Cable24 } })));
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, EditResult.Wired(Wiring.None with { Inventory = new[] { new InventoryChange("cable/red", 0) } })));
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, EditResult.Wired(Wiring.None with { Added = new[] { Cable24 with { Points = new[] { 1f } } } })));
        t.Check(l.GuestRejected.TakeLast(4).Select(x => x.Text).ToArray() is var cbad && cbad[0].Contains("Invalid edit result") &&
            cbad[1].Contains("added twice") && cbad[2].Contains("inventory") && cbad[3].Contains("added cable"),
            "a cable commit carries a cable change; duplicates, empty inventory changes and broken route points are refused");
        var wiring = new Wiring(new[] { "PSU_FAN>FAN_1" }, new[] { Cable24 }, new[] { new AccessoryChange(AccessoryKind.Splitter, 0, "PSU_SPLITTER_3X6PIN") },
            new[] { new InventoryChange("cable/custom", -1) }, false);
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, EditResult.Wired(wiring)));
        t.Check(ge.Find(begin)?.Result?.Wiring is { } got && WiringBytes(got).SequenceEqual(WiringBytes(wiring)) &&
            hn.Calls.Last() == "committed Done NaN local=False" && Same(), "the cable change reaches the host's game to replay");
        next = ho.NextId;
        Send(l.Host, ComponentEditWorld.Settle(begin));
        t.Check(ge.Edits.Count == 0 && G().Pending == null && P(go).Links.Single(x => x.Path == "psuSplitterIDs[0]").Part.Value == next && Same(),
            "the host settles it: the automatic splitter gets a new ID and the PC is free again");

        begin = Send(l.Host, Wire(H(), EditGesture.Power, "on"));
        Send(l.Host, ComponentEditWorld.Commit(H().Epoch, begin, EditResult.Wired(Wiring.None with { Powered = true })));
        Send(l.Host, ComponentEditWorld.Settle(begin));
        t.Check(P(go).Powered && P(ho).Powered && ge.Edits.Count == 0 && Same(), "the host powers the PC on: both peers record it");
        begin = Send(l.Guest, Wire(G(), EditGesture.Unplug, Cable24.Key));
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, EditResult.Wired(Wiring.None with { Removed = new[] { Cable24.Key } })));
        Send(l.Host, ComponentEditWorld.Settle(begin));
        t.Check(!P(go).Powered && Same(), "a guest pulls the 24-pin of the running PC: the host's game shows it off, so both do");
        begin = Send(l.Guest, Wire(G(), EditGesture.Power, "on"));
        Send(l.Guest, ComponentEditWorld.Cancel(G().Epoch, begin));
        t.Check(ge.Edits.Count == 0 && G().Pending == null && !P(go).Powered && Same(), "a press that changed nothing is cancelled");

        var powered = P(ho) with { Powered = true };
        t.Check(!powered.At(PcLocation.Storage).Powered && powered.At(powered.Location).Powered &&
            Throws(() => new ObjectWorld().Replace(ho.WorldId, ho.NextId, ho.Locations, new[] { powered with { Location = PcLocation.Storage } }, ho.Retired)),
            "only a PC on its bench can be powered; moving it turns it off");

        rev = l.Host.Revision; malformed = l.GuestRejected.Count;
        begin = Send(l.Guest, Wire(G(), EditGesture.Connect, "PSU_24"));
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, EditResult.Wired(Wiring.None with { Removed = Enumerable.Range(0, Wiring.MaxCables + 1).Select(i => $"a{i}>b").ToArray() })));
        t.Check(l.GuestRejected.Count == malformed + 1 && l.GuestRejected.Last().Reason == RejectReason.Malformed, "too many cable changes are malformed");
        Send(l.Guest, ComponentEditWorld.Cancel(G().Epoch, begin));

        t.Section("Component edits: progress, so the other game plays along (0.17)");
        byte[] Progress(ActorActivity a, OperationId token, EditProgress p, EditStart? start = null) => ComponentEditWorld.Progress(a.Epoch, token, p, start);
        begin = Send(l.Guest, Install(G(), 9, go, "gpu2"));
        Send(l.Guest, Progress(G(), begin, EditProgress.Started, new EditStart(true, true)));
        t.Check(ge.Find(begin) is { Start: { FromInventory: true, Flipped: true }, PartStep: false, Stage: EditStage.Running } &&
            hn.Calls.Last() == "progressed Started 9 local=False" && gn.Calls.Last() == "progressed Started 9 local=True" && Same(),
            "a guest's game started an install: both peers learn where the part came from and its flip, and the host's game plays it now");
        rev = l.Host.Revision;
        Send(l.Guest, Progress(G(), begin, EditProgress.Started, new EditStart(false, false)));
        Send(l.Guest, Progress(G(), begin, EditProgress.PartStep));
        Send(l.Host, Progress(H(), begin, EditProgress.Started));
        Send(l.Guest, Progress(G(), begin, (EditProgress)9));
        t.Check(l.Host.Revision == rev && l.GuestRejected.TakeLast(3).Select(x => x.Text).ToArray() is var pbad &&
            pbad[0].Contains("already started") && pbad[1].Contains("Only a removal") && pbad[2].Contains("Unknown edit progress") &&
            l.HostRejected.Last().Text.Contains("Only the editor reports"),
            "an edit starts once, only a removal has a cable step, only the editor reports progress, and unknown progress is refused");
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, EditResult.Done() with { FromInventory = true, Flipped = true }));
        t.Check(ge.Find(begin) is { Stage: EditStage.Committed, Start: not null } && Same(), "the commit keeps what the start reported");
        Send(l.Guest, Progress(G(), begin, EditProgress.Started));
        t.Check(l.GuestRejected.Last().Text.Contains("already finished"), "progress after the commit is refused");
        Send(l.Host, ComponentEditWorld.Settle(begin));
        t.Check(ge.Edits.Count == 0 && !R(go, 9).Occupant.IsNone && Same(), "and it settles normally");

        begin = Send(l.Host, Remove(H(), 9, ho));
        rev = l.Host.Revision;
        Send(l.Host, Progress(H(), begin, EditProgress.PartStep));
        Send(l.Host, Progress(H(), begin, EditProgress.Started, new EditStart(true, false)));
        t.Check(l.Host.Revision == rev && l.HostRejected.TakeLast(2).Select(x => x.Text).ToArray() is var rbadp &&
            rbadp[0].Contains("hasn't started") && rbadp[1].Contains("Invalid edit start"),
            "a removal reports its start before its cable step, and carries no part source");
        Send(l.Host, Progress(H(), begin, EditProgress.Started));
        Send(l.Host, Progress(H(), begin, EditProgress.PartStep));
        t.Check(ge.Find(begin) is { Start: not null, PartStep: true } && gn.Calls.Last() == "progressed PartStep 9 local=False" && Same(),
            "the host's removal passes its cable step: the guest's game takes the part out too");
        Send(l.Host, Progress(H(), begin, EditProgress.PartStep));
        Send(l.Host, ComponentEditWorld.Commit(H().Epoch, begin, EditResult.Done() with { Outcome = EditOutcome.Unplugged }));
        t.Check(l.HostRejected.TakeLast(2).Select(x => x.Text).ToArray() is var rbad2 && rbad2[0].Contains("already passed") && rbad2[1].Contains("outcome"),
            "the cable step passes once, and after it Esc can no longer keep the part in");
        Send(l.Host, ComponentEditWorld.Commit(H().Epoch, begin, EditResult.Done()));
        Send(l.Host, ComponentEditWorld.Settle(begin));
        t.Check(ge.Edits.Count == 0 && R(go, 9).Occupant.IsNone && Same(), "the removal settles on both peers");

        begin = Send(l.Guest, Wire(G(), EditGesture.Connect, "PSU_24"));
        Send(l.Guest, Progress(G(), begin, EditProgress.Started));
        t.Check(l.GuestRejected.Last().Text.Contains("don't report progress"), "cable, power and water edits don't report progress");
        Send(l.Guest, ComponentEditWorld.Cancel(G().Epoch, begin));

        t.Section("Water-cooling edits (phase 10): the finished PC record in chunks");
        var record = Enumerable.Range(0, Rebuild.MaxChunkBytes + 100).Select(i => (byte)(i * 7)).ToArray();
        var part1 = record.Take(Rebuild.MaxChunkBytes).ToArray(); var part2 = record.Skip(Rebuild.MaxChunkBytes).ToArray();
        var hash = System.Security.Cryptography.SHA256.HashData(record);
        var rebuilt = EditResult.Rebuilt(new Rebuild(record.Length, hash, true));
        begin = Send(l.Guest, Wire(G(), EditGesture.Water, "pipe"));
        t.Check(ge.Find(begin) is { Rebuilt: true, Targets.Count: 0 } && Same(), "a guest starts a pipe: the claim covers the whole PC");
        Send(l.Host, Remove(H(), 10, ho));
        t.Check(l.HostRejected.Last().Text.Contains("building water cooling"), "the other player is told someone is building water cooling there");
        rev = l.Host.Revision;
        Send(l.Guest, ComponentEditWorld.Chunk(G().Epoch, begin, 1, 2, part2));
        Send(l.Guest, ComponentEditWorld.Chunk(G().Epoch, begin, 0, 2, part1));
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, rebuilt));
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, EditResult.Done()));
        t.Check(l.GuestRejected.TakeLast(3).Select(x => x.Text).ToArray() is var rbad && rbad[0].Contains("out of order") &&
            rbad[1].Contains("hasn't fully arrived") && rbad[2].Contains("Invalid edit result") && ge.RecordOf(begin) == null,
            "chunks arrive in order, the commit waits for the whole record, and a water edit commits a record");
        Send(l.Guest, ComponentEditWorld.Chunk(G().Epoch, begin, 1, 2, part2));
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, EditResult.Rebuilt(new Rebuild(record.Length, new byte[32], true))));
        t.Check(l.GuestRejected.Last().Text.Contains("doesn't match"), "a record that doesn't match its hash is refused");
        Send(l.Host, ComponentEditWorld.Chunk(H().Epoch, begin, 2, 2, part2));
        t.Check(l.HostRejected.Last().Text.Contains("no longer running") || l.HostRejected.Last().Text.Contains("Only the editor"), "only the editor sends its record");
        Send(l.Guest, ComponentEditWorld.Commit(G().Epoch, begin, rebuilt));
        t.Check(ge.RecordOf(begin) is { } got1 && got1.SequenceEqual(record) && he.RecordOf(begin)!.SequenceEqual(record) &&
            ge.Find(begin)?.Stage == EditStage.Committed && Same(), "both peers hold the whole record when the commit lands (the host's game reloads that PC)");
        next = ho.NextId;
        Send(l.Host, ComponentEditWorld.Settle(begin));
        t.Check(ge.Edits.Count == 0 && ge.RecordOf(begin) == null && G().Pending == null && P(go).Powered &&
            P(go).Links.Single(x => x.Path == "waterBlock").Part.Value == next && Same(),
            "the host settles from its reloaded PC: the new block gets an ID, the power state carries, the record is dropped");

        begin = Send(l.Guest, Wire(G(), EditGesture.Connect, "PSU_24"));
        Send(l.Guest, ComponentEditWorld.Chunk(G().Epoch, begin, 0, 1, part2));
        t.Check(l.GuestRejected.Last().Text.Contains("Only a water-cooling"), "only a water-cooling edit sends a record");
        malformed = l.GuestRejected.Count;
        Send(l.Guest, ComponentEditWorld.Chunk(G().Epoch, begin, 0, 1, new byte[Rebuild.MaxChunkBytes + 1]));
        t.Check(l.GuestRejected.Count == malformed + 1 && l.GuestRejected.Last().Reason == RejectReason.Malformed, "an oversized chunk is malformed");
        Send(l.Guest, ComponentEditWorld.Cancel(G().Epoch, begin));

        var copy = new ObjectWorld(); copy.Read(new NetDataReader(ho.ToBytes()));
        t.Check(copy.ToBytes().SequenceEqual(ho.ToBytes()), "a settled world round-trips exactly");
    }
}

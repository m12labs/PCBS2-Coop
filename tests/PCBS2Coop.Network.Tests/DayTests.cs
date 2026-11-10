using LiteNetLib.Utils;
using PCBS2Coop.Network;

internal sealed class FakeDayNative : IDayNative
{
    public readonly List<string> Calls = new();
    public void ReadyChanged(in MutationContext ctx, int day, bool byChange) => Calls.Add($"ready {day} change={byChange} local={ctx.FromLocalPlayer}");
}

/// <summary>Phase 17d: the day ends only when both players are ready.</summary>
internal static class DayTests
{
    public static void Run(Checker t)
    {
        Changes(t);
        Sync(t);
    }

    private static void Changes(Checker t)
    {
        t.Section("Day: what takes the client's ready mark back (phase 17d)");
        var g = ActorSlot.Guest; var h = ActorSlot.Host;
        var pc = new NetObjectId(7); var bench = new NetObjectId(8);
        var actor = new ActorActivity(g, PlayerId.New(), 1, 1, default, null);
        bool S(ushort type, byte[] p, ActorSlot by = ActorSlot.Guest) => DayWorld.StartsChange(type, by, p);

        t.Check(S(CommandTypes.ActorActivity, ActivityWorld.Presence(g, PlayerId.New()), h) && S(CommandTypes.ActorActivity, ActivityWorld.Presence(g, default), h) &&
                !S(CommandTypes.ActorActivity, ActivityWorld.Presence(h, PlayerId.New()), h),
            "the client leaving or coming back counts; the host's own presence doesn't");
        t.Check(S(CommandTypes.ActorActivity, ActivityWorld.Reserve(actor, pc, ActivityOperation.Edit)) && !S(CommandTypes.ActorActivity, ActivityWorld.Work(actor, pc)) &&
                !S(CommandTypes.ActorActivity, ActivityWorld.Release(actor, new OperationId(g, 3))),
            "a claim for an edit or a move counts; walking up to a PC or finishing doesn't");
        t.Check(S(CommandTypes.PcTransfer, PcTransfers.Pickup(1, pc, bench)) && S(CommandTypes.PcTransfer, PcTransfers.Store(1, pc)) &&
                !S(CommandTypes.PcTransfer, PcTransfers.Recover(1, g), h) && !S(CommandTypes.PcTransfer, PcTransfers.Pickup(1, pc, bench), h),
            "the client moving a PC counts; the host's moves don't");
        t.Check(S(CommandTypes.ComponentEdit, ComponentEditWorld.Begin(1, pc, default, 0, EditGesture.Clean, "clean", default)) &&
                !S(CommandTypes.ComponentEdit, new byte[] { 2 }) && !S(CommandTypes.ComponentEdit, new byte[] { 3 }),
            "starting an edit counts; the result sent when it ends doesn't");
        t.Check(S(CommandTypes.Workshop, WorkshopWorld.Decorate(1, true)) && !S(CommandTypes.Workshop, WorkshopWorld.Decorate(1, false)) &&
                S(CommandTypes.Workshop, WorkshopWorld.Lights(1, true)) && !S(CommandTypes.Workshop, WorkshopWorld.Need(1, new byte[32])),
            "opening the decorating app or using the lights counts; closing it or asking for a missing picture doesn't");
        t.Check(S(CommandTypes.Jobs, JobWorld.Act(1, JobAct.Accept, 3)) && S(CommandTypes.Jobs, JobWorld.Mail(1, new MailFact(MailKind.Rent, 5, 100))) &&
                S(CommandTypes.Packages, PackageWorld.Take(1, "b")) && S(CommandTypes.Commerce, CommerceWorld.Collect(1)) && !S(CommandTypes.Commerce, new byte[] { 1 }) &&
                S(CommandTypes.ToolBench, ToolBenchWorld.Use(1, "b", BenchTool.Delid)) && S(CommandTypes.Mechanism, MechanismWorld.Toggle(1, pc, bench, 0, true)) &&
                S(CommandTypes.PcLifecycle, PcLifecycle.Delete(1, pc, PcRemoval.Carried)),
            "job and e-mail actions, packages, deliveries, tool tables, latches and new or removed PCs count; a cart change doesn't");
        t.Check(!S(CommandTypes.PcRepair, PcRepairWorld.Request(1, pc)) && !S(CommandTypes.DesktopInput, new byte[] { 1, 2 }) &&
                !S(CommandTypes.Customization, CustomizationWorld.Clock(1, pc, 2f)) && !S(CommandTypes.Day, DayWorld.Ready(1, 4)) &&
                !S(CommandTypes.DiagnosticMarker, new byte[] { 1 }) && !S(CommandTypes.Jobs, Array.Empty<byte>()),
            "automatic repair requests, desktop input, the lighting clock and the ready mark itself don't");
    }

    private static void Sync(Checker t)
    {
        t.Section("Day: the client marks itself ready and the host's End Day waits for it");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hn = new FakeDayNative(); var gn = new FakeDayNative();
        var hd = new DayWorld(ha, hn); var gd = new DayWorld(ga, gn);
        var hj = new JobWorld(ha, new FakeJobNative()); var gj = new JobWorld(ga, new FakeJobNative());
        var hostDay = 5; string? busy = null;
        hd.ReadyCheck = day => day != hostDay ? "the host's game is on another day." : busy;
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.RegisterState(hj, optional: true); l.Guest.RegisterState(gj, optional: true);
        l.Host.RegisterState(hd, optional: true); l.Guest.RegisterState(gd, optional: true);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.Register(CommandTypes.Jobs, hj); l.Guest.Register(CommandTypes.Jobs, gj);
        l.Host.Register(CommandTypes.Day, hd); l.Guest.Register(CommandTypes.Day, gd);
        l.Host.Observe(hd); l.Guest.Observe(gd);
        l.Host.BeginGeneration("day");
        void Send(WorldSync world, byte[] payload, ushort type = CommandTypes.Day) { world.Submit(type, payload, out _); l.Flush(); }
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, PlayerId.New()), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, PlayerId.New()), CommandTypes.ActorActivity);
        hj.BindHost(new[] { new JobFact(1, JobState.New, 0), new JobFact(2, JobState.New, 0) });
        hd.BindHost();
        l.Join();
        ulong H() => ha.Get(ActorSlot.Host)!.Epoch; ulong G() => ga.Get(ActorSlot.Guest)!.Epoch;
        t.Check(gd.Bound && gd.ReadyDay == 0 && !hd.GuestReady(5), "a joining client gets the day state, not ready");

        Send(l.Guest, DayWorld.Ready(G(), 5));
        t.Check(hd.GuestReady(5) && gd.GuestReady(5) && hn.Calls.Last() == "ready 5 change=False local=False" && gn.Calls.Last() == "ready 5 change=False local=True",
            "the client's End Day marks it ready for its day on both games");
        t.Check(!hd.GuestReady(6), "a ready mark is for one day: the next day isn't ready by itself");

        var before = l.GuestRejected.Count;
        Send(l.Guest, DayWorld.Ready(G(), 5));
        Send(l.Host, DayWorld.Ready(H(), 5));
        t.Check(l.GuestRejected.Count == before + 1 && l.GuestRejected.Last().Text.Contains("already ready") && l.HostRejected.Last().Text.Contains("Only the client"),
            "pressing again changes nothing, and the host doesn't mark itself ready (it ends the day)");

        Send(l.Host, JobWorld.Act(H(), JobAct.Accept, 1), CommandTypes.Jobs);
        t.Check(hd.GuestReady(5) && gd.GuestReady(5), "the host's own actions leave the client ready");
        Send(l.Guest, JobWorld.Act(G(), JobAct.Accept, 2), CommandTypes.Jobs);
        t.Check(!hd.GuestReady(5) && !gd.GuestReady(5) && hn.Calls.Last() == "ready 0 change=True local=False" && gn.Calls.Last() == "ready 0 change=True local=True" && l.InStep,
            "the client starting something takes its ready mark back on both games");

        before = l.GuestRejected.Count;
        busy = "put down the PC you're carrying first.";
        Send(l.Guest, DayWorld.Ready(G(), 5));
        busy = null;
        Send(l.Guest, DayWorld.Ready(G(), 4));
        Send(l.Guest, DayWorld.Ready(G(), 0));
        t.Check(l.GuestRejected.Count == before + 3 && l.GuestRejected[^3].Text.Contains("carrying") && l.GuestRejected[^2].Text.Contains("another day") &&
                l.GuestRejected[^1].Text.Contains("Invalid day"),
            "the host's game refuses a ready mark while the client is busy, for another day, or for no day");

        Send(l.Guest, DayWorld.Ready(G(), 5));
        var s = new NetDataWriter(); hd.Write(s);
        var copy = new DayWorld(new ActivityWorld(new ObjectWorld()), new FakeDayNative());
        copy.Read(new NetDataReader(s.CopyData()));
        t.Check(copy.Bound && copy.GuestReady(5), "the ready mark travels with snapshots");
        var oldEpoch = G();
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, default), CommandTypes.ActorActivity);
        t.Check(!hd.GuestReady(5) && hn.Calls.Last() == "ready 0 change=False local=True" && gn.Calls.Last() == "ready 0 change=False local=False",
            "the client leaving (the host sets its presence) takes its ready mark back");

        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, PlayerId.New()), CommandTypes.ActorActivity);
        Send(l.Guest, DayWorld.Ready(oldEpoch, 5));
        t.Check(l.GuestRejected.Last().Text.Contains("presence"), "a ready mark from an earlier presence is refused");
        copy.Reset();
        t.Check(!copy.Bound && copy.ReadyDay == 0, "a new world generation starts unbound and not ready");

        var free = new DayWorld(new ActivityWorld(new ObjectWorld()), new FakeDayNative());
        var fl = new FakeLink();
        fl.Host.Register(CommandTypes.Day, free);
        fl.Host.BeginGeneration("free build");
        fl.Host.Submit(CommandTypes.Day, DayWorld.Ready(1, 1), out _); fl.Flush();
        t.Check(fl.HostRejected.Last().Text.Contains("co-op career"), "Free Build refuses the day command");
    }
}

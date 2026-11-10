using LiteNetLib.Utils;
using PCBS2Coop.Network;

internal sealed class FakeOnboardingNative : IOnboardingNative
{
    public readonly List<string> Calls = new();
    public void Moved(in MutationContext ctx, OnboardingFact fact) => Calls.Add($"{fact.Type} {fact.State}/{fact.Progress} local={ctx.FromLocalPlayer}");
}

/// <summary>Phase 17e: both players go through the tutorials together.</summary>
internal static class OnboardingTests
{
    private static readonly OnboardingFact FirstDay = new("FirstDayProgressionOnboardings", 1, 3), Tablet = new("TabletOnboarding", 2, 7);

    public static void Run(Checker t)
    {
        t.Section("Tutorials: whichever player does a step, it counts for both (phase 17e)");
        var activity = new ActivityWorld(new ObjectWorld());
        var w = new OnboardingWorld(activity, new FakeOnboardingNative());
        t.Check(Throws(() => w.BindHost(new[] { FirstDay, FirstDay with { Progress = 4 } })) && Throws(() => w.BindHost(new[] { FirstDay with { State = 3 } })) &&
                Throws(() => w.BindHost(new[] { FirstDay with { Type = "" } })), "a tutorial listed twice, with an unknown state or without a type is refused");
        w.BindHost(new[] { FirstDay, Tablet });
        var s = new NetDataWriter(); w.Write(s);
        var copy = new OnboardingWorld(activity, new FakeOnboardingNative());
        copy.Read(new NetDataReader(s.CopyData()));
        t.Check(copy.Bound && copy.Facts.OrderBy(f => f.Type).SequenceEqual(new[] { FirstDay, Tablet }), "the tutorials travel with snapshots and saves");
        copy.Reset();
        t.Check(!copy.Bound && copy.Facts.Count == 0, "a new world generation starts unbound");

        var l = new FakeLink(); var ha = new ActivityWorld(new ObjectWorld()); var ga = new ActivityWorld(new ObjectWorld());
        var hn = new FakeOnboardingNative(); var gn = new FakeOnboardingNative();
        var hw = new OnboardingWorld(ha, hn); var gw = new OnboardingWorld(ga, gn);
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.RegisterState(hw, optional: true); l.Guest.RegisterState(gw, optional: true);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.Register(CommandTypes.Onboarding, hw); l.Guest.Register(CommandTypes.Onboarding, gw);
        l.Host.BeginGeneration("tutorials");
        void Send(WorldSync world, byte[] payload, ushort type = CommandTypes.Onboarding) { world.Submit(type, payload, out _); l.Flush(); }
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, PlayerId.New()), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, PlayerId.New()), CommandTypes.ActorActivity);
        hw.BindHost(new[] { FirstDay, Tablet });
        l.Join();
        ulong H() => ha.Get(ActorSlot.Host)!.Epoch; ulong G() => ga.Get(ActorSlot.Guest)!.Epoch;
        t.Check(gw.Fact(FirstDay.Type) == FirstDay, "a joining client gets the host's tutorial steps");

        var four = FirstDay with { Progress = 4 };
        Send(l.Guest, OnboardingWorld.Move(G(), FirstDay, four));
        t.Check(hw.Fact(FirstDay.Type) == four && gw.Fact(FirstDay.Type) == four && hn.Calls.Last() == "FirstDayProgressionOnboardings 1/4 local=False" &&
                gn.Calls.Last() == "FirstDayProgressionOnboardings 1/4 local=True", "the client's step moves the host's tutorial too");
        var five = four with { Progress = 5 };
        Send(l.Host, OnboardingWorld.Move(H(), four, five));
        t.Check(gw.Fact(FirstDay.Type) == five && gn.Calls.Last() == "FirstDayProgressionOnboardings 1/5 local=False", "the host's step moves the client's tutorial too");

        var before = l.GuestRejected.Count;
        Send(l.Host, OnboardingWorld.Move(H(), five, five with { Progress = 6 }));
        Send(l.Guest, OnboardingWorld.Move(G(), five, five with { Progress = 7 }));
        t.Check(l.GuestRejected.Count == before + 1 && l.GuestRejected.Last().Text.Contains("already moved on") && gw.Fact(FirstDay.Type)!.Progress == 6 && l.InStep,
            "when both move the same step at once, the first move wins and the other game follows it");

        var done = five with { State = 2, Progress = 6 };
        Send(l.Guest, OnboardingWorld.Move(G(), five with { Progress = 6 }, done));
        t.Check(hw.Fact(FirstDay.Type)!.State == 2, "the client finishing a tutorial finishes it for the host");

        var fresh = new OnboardingFact("RepairPCOnboarding", 1, 0);
        Send(l.Guest, OnboardingWorld.Move(G(), fresh with { State = 0 }, fresh));
        t.Check(hw.Fact(fresh.Type) == fresh, "a tutorial the host hadn't listed starts from inactive, step 0");
        before = l.GuestRejected.Count;
        Send(l.Guest, OnboardingWorld.Move(G(), Tablet, Tablet));
        Send(l.Guest, OnboardingWorld.Move(G(), Tablet, Tablet with { State = 5 }));
        t.Check(l.GuestRejected.Count == before + 2 && l.GuestRejected.All(r => r.Text.Length > 0), "a move to the same step or to an unknown state is refused");

        var free = new OnboardingWorld(new ActivityWorld(new ObjectWorld()), new FakeOnboardingNative());
        var fl = new FakeLink();
        fl.Host.Register(CommandTypes.Onboarding, free);
        fl.Host.BeginGeneration("unbound");
        fl.Host.Submit(CommandTypes.Onboarding, OnboardingWorld.Move(1, FirstDay, four), out _); fl.Flush();
        t.Check(fl.HostRejected.Last().Text.Contains("aren't shared"), "an unbound world refuses tutorial moves");
    }

    private static bool Throws(Action a) { try { a(); return false; } catch (InvalidDataException) { return true; } }
}

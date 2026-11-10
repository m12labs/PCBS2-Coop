using LiteNetLib.Utils;
using PCBS2Coop.Network;

internal sealed class FakeJobNative : IJobNative
{
    public readonly List<string> Calls = new();
    public InboxUpdate? Last;
    public void Updated(in MutationContext ctx, InboxUpdate update) { Last = update; Calls.Add($"update {update.Jobs.Count} {update.Native.Length} local={ctx.FromLocalPlayer}"); }
    public void Acted(in MutationContext ctx, JobAct act, int job, int value) => Calls.Add($"{act} {job} {value} local={ctx.FromLocalPlayer}");
    public void MailActed(in MutationContext ctx, MailFact mail) => Calls.Add($"mail {mail.Kind} {mail.Cost} local={ctx.FromLocalPlayer}");
    public void InboxActed(in MutationContext ctx, InboxAction a) =>
        Calls.Add($"inbox {a.Act} {(a.OnJob ? "job " + a.Job : $"mail {a.List}/{a.Index}")}{(a.Tag.Length > 0 ? " " + a.Tag : "")} local={ctx.FromLocalPlayer}");
}

/// <summary>Phase 17b: the host's jobs and inbox, and either player's job actions.</summary>
internal static class JobTests
{
    private static readonly JobFact[] Start =
    {
        new(1, JobState.New, 0), new(2, JobState.Finished, 0), new(3, JobState.Accepted, 0),
        new(10, JobState.Read, 0), new(11, JobState.Read, 10), new(12, JobState.Read, 10),
    };

    public static void Run(Checker t)
    {
        Model(t);
        Sync(t);
        Mails(t);
        Inbox(t);
    }

    private static void Inbox(Checker t)
    {
        t.Section("Jobs: either player organizes the shared inbox (phase 17e)");
        var flag = InboxAction.ForMail(InboxAct.Flag, 0, 3, 638_000_000_000_000_010);
        t.Check(flag.Problem() == null && InboxAction.ForJob(InboxAct.Delete, 2).Problem() == null && InboxAction.NewTag("Urgent", 0xFF0000FF).Problem() == null &&
                InboxAction.Delivered(new byte[] { 1, 2 }).Problem() == null && InboxAction.ForMail(InboxAct.AddTag, 1, 0, 5, "Urgent").Problem() == null,
            "reads, flags, tags, deletes, new tags and a tutorial's e-mail are well formed");
        t.Check(InboxAction.ForMail(InboxAct.AddTag, 0, 0, 5).Problem() != null && InboxAction.ForMail(InboxAct.Flag, 0, 0, 5, "x").Problem() != null &&
                InboxAction.ForMail(InboxAct.Flag, 5, 0, 5).Problem() != null && InboxAction.ForJob(InboxAct.Restore, 2).Problem() != null &&
                InboxAction.Delivered(Array.Empty<byte>()).Problem() != null && InboxAction.NewTag(new string('t', InboxAction.MaxTag + 1), 0).Problem() != null &&
                (flag with { Act = (InboxAct)42 }).Problem() != null,
            "a tag action without a tag, a tag on another action, an unknown list or action, a job restore, an empty e-mail or an overlong tag is refused");

        var l = new FakeLink(); var ha = new ActivityWorld(new ObjectWorld()); var ga = new ActivityWorld(new ObjectWorld());
        var hn = new FakeJobNative(); var gn = new FakeJobNative();
        var hw = new JobWorld(ha, hn); var gw = new JobWorld(ga, gn);
        string? gone = null;
        hw.InboxCheck = a => gone;
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.RegisterState(hw, optional: true); l.Guest.RegisterState(gw, optional: true);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.Register(CommandTypes.Jobs, hw); l.Guest.Register(CommandTypes.Jobs, gw);
        l.Host.BeginGeneration("inbox");
        void Send(WorldSync world, byte[] payload, ushort type = CommandTypes.Jobs) { world.Submit(type, payload, out _); l.Flush(); }
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, PlayerId.New()), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, PlayerId.New()), CommandTypes.ActorActivity);
        hw.BindHost(Start);
        l.Join();
        ulong H() => ha.Get(ActorSlot.Host)!.Epoch; ulong G() => ga.Get(ActorSlot.Guest)!.Epoch;

        Send(l.Guest, JobWorld.Inbox(G(), flag));
        Send(l.Guest, JobWorld.Inbox(G(), InboxAction.ForJob(InboxAct.AddTag, 2, "Urgent")));
        t.Check(hn.Calls[^2] == "inbox Flag mail 0/3 local=False" && hn.Calls[^1] == "inbox AddTag job 2 Urgent local=False" && gn.Calls[^1].EndsWith("local=True"),
            "the client's flag and tag reach the host's game, which repeats them");
        Send(l.Guest, JobWorld.Inbox(G(), flag));
        t.Check(hn.Calls[^1] == "inbox Flag mail 0/3 local=False" && !hw.AnyActed, "inbox actions don't hold the e-mail or job for one player");

        var before = l.GuestRejected.Count;
        Send(l.Guest, JobWorld.Inbox(G(), InboxAction.ForJob(InboxAct.Read, 99)));
        gone = "That e-mail isn't in the host's inbox (any more).";
        Send(l.Guest, JobWorld.Inbox(G(), flag));
        gone = null;
        Send(l.Host, JobWorld.Inbox(H(), InboxAction.Delivered(new byte[] { 1 })));
        Send(l.Guest, JobWorld.Inbox(G(), flag with { Index = -1 }));
        t.Check(l.GuestRejected.Count == before + 3 && l.GuestRejected[^3].Text.Contains("isn't on the host's list") && l.GuestRejected[^2].Text.Contains("any more") &&
                l.GuestRejected[^1].Text.Contains("Invalid") && l.HostRejected.Last().Text.Contains("Only the client"),
            "an unknown job, an e-mail the host's game can't find or a malformed action is refused; only the client delivers a tutorial's e-mail");
        Send(l.Guest, JobWorld.Inbox(G(), InboxAction.Delivered(new byte[] { 7, 7 })));
        t.Check(hn.Calls[^1] == "inbox Deliver mail 0/0 local=False", "the client's tablet tutorial e-mail goes to the host");
    }

    private static bool Throws(Action a) { try { a(); return false; } catch (InvalidDataException) { return true; } }

    private static void Model(Checker t)
    {
        t.Section("Jobs: the shared job list (phase 17b)");
        var activity = new ActivityWorld(new ObjectWorld());
        var w = new JobWorld(activity, new FakeJobNative());
        t.Check(!w.Bound && w.Jobs.Count == 0, "an unbound model (Free Build) has no jobs");
        t.Check(Throws(() => w.BindHost(new[] { new JobFact(1, JobState.New, 0), new JobFact(1, JobState.Read, 0) })), "a job listed twice is refused");
        t.Check(Throws(() => w.BindHost(new[] { new JobFact(0, JobState.New, 0) })) && Throws(() => w.BindHost(new[] { new JobFact(4, JobState.New, 4) })) &&
                Throws(() => w.BindHost(new[] { new JobFact(4, (JobState)42, 0) })), "a job without an ID, its own multi-job or an unknown status is refused");
        w.BindHost(Start);
        t.Check(w.Bound && w.Jobs.Count == 6 && w.Job(11)!.Master == 10, "the host binds its jobs, multi-job parts included");
        var s = new NetDataWriter(); w.Write(s);
        var copy = new JobWorld(activity, new FakeJobNative());
        copy.Read(new NetDataReader(s.CopyData()));
        t.Check(copy.Bound && copy.Jobs.OrderBy(j => j.Id).SequenceEqual(w.Jobs.OrderBy(j => j.Id)), "the jobs travel with snapshots and saves exactly");
        copy.Reset();
        t.Check(!copy.Bound && copy.Jobs.Count == 0, "a new world generation starts unbound until the host binds it");

        var update = new InboxUpdate(Start, Enumerable.Range(0, 300).Select(i => (byte)i).ToArray());
        var back = InboxUpdate.Decode(update.Encode());
        t.Check(back.Jobs.SequenceEqual(Start) && back.Native.SequenceEqual(update.Native), "an inbox update round-trips");
        var bad = update.Encode(); bad[0] = 0xFF; bad[1] = 0xFF; bad[2] = 0xFF; bad[3] = 0x7F;
        var trailing = update.Encode().Append((byte)1).ToArray();
        t.Check(ThrowsFormat(() => InboxUpdate.Decode(bad)) && ThrowsFormat(() => InboxUpdate.Decode(trailing)) && ThrowsFormat(() => InboxUpdate.Decode(new byte[] { 1 })),
            "a malformed, oversized or truncated inbox update is refused");

        t.Check(JobWorld.Allows(JobAct.Accept, JobState.New) && JobWorld.Allows(JobAct.Accept, JobState.Read) && !JobWorld.Allows(JobAct.Accept, JobState.Accepted) &&
                JobWorld.Allows(JobAct.Collect, JobState.Finished) && !JobWorld.Allows(JobAct.Collect, JobState.Collected) &&
                JobWorld.Allows(JobAct.Quit, JobState.Accepted) && JobWorld.Allows(JobAct.Quit, JobState.InTransit) && !JobWorld.Allows(JobAct.Quit, JobState.New) &&
                JobWorld.Allows(JobAct.Reject, JobState.Read) && !JobWorld.Allows(JobAct.Reject, JobState.Finished),
            "each action needs the status the game's own button needs");
    }

    private static bool ThrowsFormat(Action a) { try { a(); return false; } catch (FormatException) { return true; } }

    private static readonly MailFact Rent = new(MailKind.Rent, 638_000_000_000_000_001, 450), Power = new(MailKind.Utility, 638_000_000_000_000_002, 80),
        Shop = new(MailKind.Renovation, 638_000_000_000_000_003, 25_000);

    private static void Mails(Checker t)
    {
        t.Section("Jobs: either player pays a bill or orders the shop renovation (phase 17d)");
        var update = new InboxUpdate(Start, new byte[] { 1, 2, 3 }) { Mails = new[] { Rent, Power, Shop } };
        var back = InboxUpdate.Decode(update.Encode());
        t.Check(back.Mails.SequenceEqual(update.Mails) && back.Jobs.SequenceEqual(Start), "the bills and the renovation offer travel with the inbox update");
        var twice = new InboxUpdate(Start, new byte[] { 1 }) { Mails = new[] { Rent, Rent with { Cost = 1 } } };
        var negative = new InboxUpdate(Start, new byte[] { 1 }) { Mails = new[] { Rent with { Cost = -1 } } };
        t.Check(ThrowsFormat(() => InboxUpdate.Decode(twice.Encode())) && ThrowsFormat(() => InboxUpdate.Decode(negative.Encode())),
            "an e-mail listed twice or with a negative cost is refused");

        var activity = new ActivityWorld(new ObjectWorld());
        var w = new JobWorld(activity, new FakeJobNative());
        w.BindHost(Start, new[] { Rent, Shop });
        var s = new NetDataWriter(); w.Write(s);
        var copy = new JobWorld(activity, new FakeJobNative());
        copy.Read(new NetDataReader(s.CopyData()));
        t.Check(copy.Mails.OrderBy(m => m.Time).SequenceEqual(new[] { Rent, Shop }), "the e-mails travel with snapshots and saves");
        var old = new NetDataWriter();
        old.Put(true); old.Put(3); old.Put(1); old.Put(1); old.Put((byte)JobState.New); old.Put(0);
        copy.Read(new NetDataReader(old.CopyData()));
        t.Check(copy.Bound && copy.Revision == 3 && copy.Jobs.Count == 1 && copy.Mails.Count == 0, "a 0.63 or 0.64 save without e-mails still loads");
        t.Check(Throws(() => w.BindHost(Start, new[] { Rent, Rent })), "an e-mail bound twice is refused");

        var l = new FakeLink(); var ha = new ActivityWorld(new ObjectWorld()); var ga = new ActivityWorld(new ObjectWorld());
        var hn = new FakeJobNative(); var gn = new FakeJobNative();
        var hw = new JobWorld(ha, hn); var gw = new JobWorld(ga, gn);
        string? broke = null;
        hw.MailCheck = m => broke;
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.RegisterState(hw, optional: true); l.Guest.RegisterState(gw, optional: true);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.Register(CommandTypes.Jobs, hw); l.Guest.Register(CommandTypes.Jobs, gw);
        l.Host.BeginGeneration("mails");
        void Send(WorldSync world, byte[] payload, ushort type = CommandTypes.Jobs) { world.Submit(type, payload, out _); l.Flush(); }
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, PlayerId.New()), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, PlayerId.New()), CommandTypes.ActorActivity);
        hw.BindHost(Start, new[] { Rent, Power, Shop });
        l.Join();
        ulong H() => ha.Get(ActorSlot.Host)!.Epoch; ulong G() => ga.Get(ActorSlot.Guest)!.Epoch;
        t.Check(gw.Mails.Count == 3, "a joining guest gets the host's bills and offers with the world");

        Send(l.Guest, JobWorld.Mail(G(), Rent));
        t.Check(hn.Calls.Last() == "mail Rent 450 local=False" && gn.Calls.Last() == "mail Rent 450 local=True" && hw.MailActed(Rent) && hw.AnyActed,
            "the guest's Pay reaches the host's game, which pays the bill");
        var before = l.GuestRejected.Count;
        Send(l.Guest, JobWorld.Mail(G(), Rent));
        Send(l.Host, JobWorld.Mail(H(), Rent));
        t.Check(l.GuestRejected.Count == before + 1 && l.GuestRejected.Last().Text.Contains("already handling") && l.HostRejected.Last().Text.Contains("already handling"),
            "a bill is paid once even if both players press Pay");

        before = l.GuestRejected.Count;
        Send(l.Guest, JobWorld.Mail(G(), Power with { Cost = 81 }));
        Send(l.Guest, JobWorld.Mail(G(), Shop with { Time = 4 }));
        broke = "not enough money";
        Send(l.Guest, JobWorld.Mail(G(), Power));
        broke = null;
        t.Check(l.GuestRejected.Count == before + 3 && l.GuestRejected[^3].Text.Contains("no unpaid bill") && l.GuestRejected[^2].Text.Contains("no renovation offer") &&
                l.GuestRejected[^1].Text.Contains("not enough money"),
            "an e-mail the host's list doesn't show, or one the host's game can't act on, is refused");
        Send(l.Guest, JobWorld.Mail(G(), Shop));
        t.Check(hn.Calls.Last() == "mail Renovation 25000 local=False", "the guest can order the shop renovation");

        foreach (var p in JobWorld.Submissions(H(), new InboxUpdate(Start, new byte[] { 9 }) { Mails = new[] { Power } })) l.Host.Submit(CommandTypes.Jobs, p, out _);
        l.Flush();
        t.Check(gw.Mails.SequenceEqual(new[] { Power }) && !gw.AnyActed && !hw.AnyActed && l.InStep, "the host's next update lists what is still unpaid and frees the actions");
    }

    private static void Sync(Checker t)
    {
        t.Section("Jobs: either player acts, the host's game runs it and shares the result");
        var l = new FakeLink(); var ho = new ObjectWorld(); var go = new ObjectWorld();
        var ha = new ActivityWorld(ho); var ga = new ActivityWorld(go);
        var hn = new FakeJobNative(); var gn = new FakeJobNative();
        var hw = new JobWorld(ha, hn); var gw = new JobWorld(ga, gn);
        var noSlot = false;
        hw.ActCheck = (act, job, value) => act == JobAct.Accept && noSlot ? "There's no free note slot in the workshop for that job." : null;
        l.Host.RegisterState(ha); l.Guest.RegisterState(ga);
        l.Host.RegisterState(hw, optional: true); l.Guest.RegisterState(gw, optional: true);
        l.Host.Register(CommandTypes.ActorActivity, ha); l.Guest.Register(CommandTypes.ActorActivity, ga);
        l.Host.Register(CommandTypes.Jobs, hw); l.Guest.Register(CommandTypes.Jobs, gw);
        l.Host.BeginGeneration("jobs");
        void Send(WorldSync world, byte[] payload, ushort type = CommandTypes.Jobs) { world.Submit(type, payload, out _); l.Flush(); }
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, PlayerId.New()), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, PlayerId.New()), CommandTypes.ActorActivity);
        hw.BindHost(Start);
        l.Join();
        ulong H() => ha.Get(ActorSlot.Host)!.Epoch; ulong G() => ga.Get(ActorSlot.Guest)!.Epoch;
        t.Check(gw.Bound && gw.Jobs.Count == 6, "a joining guest gets the host's jobs with the world");

        Send(l.Guest, JobWorld.Act(G(), JobAct.Accept, 1));
        t.Check(hn.Calls.Last() == "Accept 1 0 local=False" && gn.Calls.Last() == "Accept 1 0 local=True", "the guest's accept reaches the host's game, which runs it");
        var before = l.GuestRejected.Count;
        Send(l.Guest, JobWorld.Act(G(), JobAct.Reject, 1));
        Send(l.Host, JobWorld.Act(H(), JobAct.Accept, 1));
        t.Check(l.GuestRejected.Count == before + 1 && l.GuestRejected.Last().Text.Contains("already handling") && l.HostRejected.Last().Text.Contains("already handling"),
            "a job already being handled can't be acted on again until the host's next update (one result wins)");

        Send(l.Guest, JobWorld.Act(G(), JobAct.Collect, 2));
        Send(l.Host, JobWorld.Act(H(), JobAct.Collect, 2));
        t.Check(hn.Calls.Count(c => c.StartsWith("Collect 2")) == 1 && l.HostRejected.Last().Text.Contains("already handling"),
            "both players collecting one finished job pays once");

        before = l.GuestRejected.Count;
        Send(l.Guest, JobWorld.Act(G(), JobAct.Collect, 3));
        Send(l.Guest, JobWorld.Act(G(), JobAct.Accept, 3));
        Send(l.Guest, JobWorld.Act(G(), JobAct.Quit, 10, 5));
        Send(l.Guest, JobWorld.Act(G(), JobAct.Accept, 99));
        t.Check(l.GuestRejected.Count == before + 4 && l.GuestRejected[^4].Text.Contains("finished yet") && l.GuestRejected[^3].Text.Contains("can't be accepted") &&
                l.GuestRejected[^2].Text.Contains("isn't in progress") && l.GuestRejected[^1].Text.Contains("isn't on the host's list"),
            "an action the job's status doesn't allow, or an unknown job, is refused");

        before = l.GuestRejected.Count;
        Send(l.Guest, JobWorld.Act(G(), JobAct.Quit, 3, -1));
        Send(l.Guest, JobWorld.Act(G(), JobAct.Accept, 10, 5));
        Send(l.Guest, JobWorld.Act(G(), JobAct.Quit, 3, JobWorld.MaxValue + 1));
        t.Check(l.GuestRejected.Count == before + 3, "a negative or oversized quit value, or a value on another action, is refused");
        Send(l.Guest, JobWorld.Act(G(), JobAct.Quit, 3, 450));
        t.Check(hn.Calls.Last() == "Quit 3 450 local=False", "a quit carries the PC value the guest's confirmation showed");

        Send(l.Guest, JobWorld.Act(G(), JobAct.Accept, 11));
        before = l.GuestRejected.Count;
        Send(l.Guest, JobWorld.Act(G(), JobAct.Accept, 10));
        Send(l.Guest, JobWorld.Act(G(), JobAct.Reject, 12));
        t.Check(l.GuestRejected.Count == before + 2 && l.GuestRejected.Last().Text.Contains("already handling"),
            "acting on one part of a multi-job holds the whole multi-job");

        noSlot = true;
        var fresh = new[] { new JobFact(1, JobState.Accepted, 0), new JobFact(2, JobState.Collected, 0), new JobFact(3, JobState.Rejected, 0),
            new JobFact(10, JobState.Accepted, 0), new JobFact(11, JobState.Accepted, 10), new JobFact(12, JobState.Accepted, 10), new JobFact(20, JobState.New, 0) };
        var native = Enumerable.Range(0, 40_000).Select(i => (byte)(i * 7)).ToArray();
        foreach (var p in JobWorld.Submissions(H(), new InboxUpdate(fresh, native))) l.Host.Submit(CommandTypes.Jobs, p, out _);
        l.Flush();
        t.Check(gn.Last != null && gn.Last.Native.SequenceEqual(native) && gw.Jobs.Count == 7 && gw.Job(2)!.State == JobState.Collected && gw.Acted.Count == 0 && hw.Acted.Count == 0 &&
                hn.Calls.Last().StartsWith("update 7") && l.InStep, "the host's update (chunked) reaches the guest's game whole, with the new statuses");
        Send(l.Guest, JobWorld.Act(G(), JobAct.Accept, 20));
        t.Check(l.GuestRejected.Last().Text.Contains("note slot"), "the host's game refuses an accept it can't do (no free note slot)");
        noSlot = false;
        Send(l.Guest, JobWorld.Act(G(), JobAct.Accept, 20));
        t.Check(hn.Calls.Last() == "Accept 20 0 local=False", "a job that arrived with the update can be accepted");

        before = l.GuestRejected.Count;
        foreach (var p in JobWorld.Submissions(G(), new InboxUpdate(fresh, native))) l.Guest.Submit(CommandTypes.Jobs, p, out _);
        l.Flush();
        t.Check(l.GuestRejected.Count > before && l.GuestRejected.Last().Text.Contains("Only the host"), "only the host shares its jobs and inbox");

        var oldEpoch = G();
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, default), CommandTypes.ActorActivity);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, PlayerId.New()), CommandTypes.ActorActivity);
        Send(l.Guest, JobWorld.Act(oldEpoch, JobAct.Quit, 1, 0));
        t.Check(l.GuestRejected.Last().Text.Contains("presence"), "an action from an earlier presence is refused");

        var free = new JobWorld(new ActivityWorld(new ObjectWorld()), new FakeJobNative());
        var fl = new FakeLink();
        fl.Host.Register(CommandTypes.Jobs, free);
        fl.Host.BeginGeneration("free build");
        fl.Host.Submit(CommandTypes.Jobs, JobWorld.Act(1, JobAct.Accept, 1), out _); fl.Flush();
        t.Check(fl.HostRejected.Last().Text.Contains("co-op career"), "Free Build refuses job commands");
    }
}

using System.IO.Compression;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using LiteNetLib.Utils;
using UnityEngine;
using PCBS2Coop.Network;
using JobList = Il2CppSystem.Collections.Generic.List<Job>;
using EmailList = Il2CppSystem.Collections.Generic.List<Email>;
using StringList = Il2CppSystem.Collections.Generic.List<string>;

namespace PCBS2Coop;

/// <summary>
/// Phase 17b (owner decisions, November 9, 2026): shared jobs and the shared inbox in a co-op Career on this game
/// (<see cref="JobWorld"/>, command 21).
/// <list type="bullet">
/// <item>The host's game keeps the jobs, e-mails, reviews and calendar (<c>CareerStatus.State</c>). Once a second it
/// compares a summary of them with the last one it shared; on a difference it sends the game's own lists, serialized
/// together so shared references (a job in several lists, a multi-job's parts, a review's job) stay shared. The guest's
/// game takes them in place of its own and redraws the inbox and calendar. Paused while the host's day changes: the
/// guest reloads the host's world then anyway.</item>
/// <item>Either player accepts, collects, quits or turns down a job. The host's own buttons run the game's code (after
/// checking the job's PC isn't in use). The guest's are sent to the host (hooks in <see cref="JobHooks"/>), whose game
/// runs the game's own action without its result screen or tutorial pop-ups: <c>Job.OnAccept</c>; collect drops the
/// "return the PC" objectives then <c>OnCollected</c> (as <c>OnPositiveAction</c> does); quit charges the fee the
/// guest's confirmation showed, then <c>DoQuit</c>; turn down is <c>OnRejected</c>.</item>
/// <item>Collecting, quitting or turning down a job removes its PC on the host's game (<c>SaveLoadSystem.RemoveJobItem</c>):
/// the host shares that removal (<see cref="PcRemoval.Game"/>). Its package and note follow the host's benches (16c).</item>
/// </list>
/// </summary>
internal sealed class NativeJobs : IJobNative
{
    private const float ObserveEvery = 1f, PendingTimeout = 20f, DayHold = 90f;
    private const byte Layout = 1;
    private const int Slots = 22;

    private readonly CoopController _coop;
    private readonly WorldSync _world;
    public JobWorld Handler { get; }
    internal static NativeJobs Current => CoopController.Instance?.Jobs;

    /// <summary>The host's game is running the guest's job action: no result screen or tutorial pop-up on the host.</summary>
    [ThreadStatic] internal static bool HostRunning;

    private string _shared;
    private float _nextObserve, _dayHoldUntil;
    private readonly Dictionary<OperationId, (JobAct Act, int Job, float At)> _pending = new();
    private (JobAct Act, int Job)? _sending;
    /// <summary>Host: guest actions accepted, run on the next frame (outside command apply).</summary>
    private readonly Queue<(JobAct Act, int Job, int Value)> _forGuest = new();
    /// <summary>Host: shared PCs its game removed by itself, to share.</summary>
    private readonly List<NetObjectId> _removed = new();

    public NativeJobs(CoopController coop, WorldSync world)
    {
        _coop = coop; _world = world;
        Handler = new JobWorld(coop.Activity, this) { ActCheck = CheckAct };
        world.GenerationStarted += (uint _, string _) => Clear();
        world.Rejected += OnRejected;
    }

    private void Clear() { _shared = null; _pending.Clear(); _forGuest.Clear(); _removed.Clear(); _dayHoldUntil = 0; }

    private bool Active => NativeCareer.InCareer && Handler.Bound && _world.Admitted && _world.Divergence == null && _coop.WorldReady;
    private ulong Epoch => _coop.Activity.Get(_world.Local)?.Epoch ?? 0;
    private static CareerStatus Status => CareerStatus.Get();
    private static CareerStatus.State Data => CareerStatus.GetState();

    /// <summary>Host: its End Day started. The inbox isn't shared while the day changes (the guest reloads after it).</summary>
    internal void DayStarted() => _dayHoldUntil = Time.realtimeSinceStartup + DayHold;

    // ---------------- reading the game ----------------

    /// <summary>Every job the game holds (each list and every multi-job's parts), once each.</summary>
    private static IEnumerable<(Job Job, int Master)> AllJobs(CareerStatus.State d)
    {
        var seen = new HashSet<int>();
        foreach (var list in new[] { d.m_jobs, d.m_todaysJobs, d.m_acceptedJobs, d.m_flaggedJobs, d.m_doneJobs, d.m_deletedJobs, d.m_rejectedJobs })
        {
            if (list == null) continue;
            foreach (var j in list)
            {
                if (j == null || !seen.Add(j.m_id)) continue;
                yield return (j, 0);
                if (j.TryCast<MultiJob>()?.m_subJobs is { } subs)
                    foreach (var s in subs) if (s != null && seen.Add(s.m_id)) yield return (s, j.m_id);
            }
        }
    }

    internal static Job FindJob(int id)
    {
        var d = Data;
        return d == null ? null : AllJobs(d).Where(x => x.Job.m_id == id).Select(x => x.Job).FirstOrDefault();
    }

    private static List<JobFact> ReadFacts(CareerStatus.State d)
    {
        var facts = new List<JobFact>();
        foreach (var (job, master) in AllJobs(d))
        {
            var fact = new JobFact(job.m_id, (JobState)(int)job.m_status, master);
            if (fact.IsValid) facts.Add(fact);
            else Plugin.Logger.LogWarning($"[jobs] job {job.m_id} (status {(int)job.m_status}) can't be shared; its buttons work on the host only.");
        }
        return facts.Take(InboxUpdate.MaxJobs).ToList();
    }

    /// <summary>What the guest's screens show of the jobs and inbox: a change here is worth sharing.</summary>
    private static string Fingerprint(CareerStatus.State d)
    {
        var b = new StringBuilder(8192);
        void Ids(JobList l, char tag)
        {
            b.Append(tag);
            if (l == null) { b.Append('-'); return; }
            b.Append(l.Count);
            foreach (var j in l) b.Append(',').Append(j?.m_id ?? 0);
        }
        Ids(d.m_jobs, 'J'); Ids(d.m_todaysJobs, 'T'); Ids(d.m_acceptedJobs, 'A'); Ids(d.m_flaggedJobs, 'F');
        Ids(d.m_doneJobs, 'D'); Ids(d.m_deletedJobs, 'X'); Ids(d.m_rejectedJobs, 'R');
        foreach (var (j, master) in AllJobs(d))
        {
            b.Append('|').Append(j.m_id).Append(':').Append(master).Append(':').Append((int)j.m_status)
             .Append(j.m_flagged ? 'f' : '-').Append(j.m_permanentDelete ? 'p' : '-').Append(j.m_isSpam ? 's' : '-')
             .Append(j.m_deliveryDay).Append('/').Append(j.m_reviewDay).Append('/').Append(j.m_cashReward).Append('/')
             .Append(j.m_pcStatus).Append('/').Append(j.m_tags?.Count ?? -1);
            if (j.m_objectives != null)
                foreach (var o in j.m_objectives)
                    if (o != null)
                        b.Append(o.m_satisfied ? 'S' : 's').Append(o.m_invalid ? 'I' : 'i').Append(o.m_hidden ? 'H' : 'h')
                         .Append((int)(o.percentageCovered * 100)).Append('.').Append(o.numberOfStickers);
        }
        void Mails(EmailList l, char tag)
        {
            b.Append(tag);
            if (l == null) { b.Append('-'); return; }
            b.Append(l.Count);
            foreach (var e in l)
                if (e != null)
                    b.Append(',').Append(e.m_creationTime).Append(e.m_unread ? 'u' : '-').Append(e.m_removed ? 'r' : '-')
                     .Append(e.m_flagged ? 'f' : '-').Append(e.m_inbox ? 'i' : '-').Append(e.m_permanentDelete ? 'p' : '-').Append(e.m_tags?.Count ?? -1);
        }
        Mails(d.m_messages, 'M'); Mails(d.m_todaysMessages, 'N'); Mails(d.m_deletedMsgs, 'Q'); Mails(d.m_flaggedMsgs, 'G'); Mails(d.m_spamMessages, 'P');
        b.Append("|r").Append(d.m_reviews?.Count ?? -1).Append("|t").Append(d.m_emailTags?.Count ?? -1).Append('.').Append(d.m_emailTagNames?.Count ?? -1);
        if (d.m_calendar is { } cal)
        {
            b.Append("|c").Append(cal.Today);
            if (cal.m_events != null)
                foreach (var e in cal.m_events)
                    if (e != null) b.Append(',').Append((int)e.GetTypeOfEvent()).Append('@').Append(e.GetDay()).Append('#').Append(e.m_param).Append(e.m_strParam);
        }
        b.Append("|s").Append(d.m_completedJobs?.Count ?? -1).Append('.').Append(d.m_completedStoryJobs?.Count ?? -1).Append('.')
         .Append(d.m_generatedJobs?.Count ?? -1).Append('.').Append(d.m_completedObjectiveTypesInStory?.Count ?? -1).Append('.')
         .Append(d.m_requiredPartIdsForJobs?.Count ?? -1);
        b.Append("|g");
        if (d.m_jobTags != null) foreach (var t in d.m_jobTags) if (t != null) b.Append(',').Append(t.JobId);
        b.Append("|n").Append(d.m_nextJobId).Append('.').Append(d.m_numberJobsCompleted).Append('.').Append(d.m_quitJobs).Append('.')
         .Append(d.m_fiveStarJobs).Append('.').Append(d.m_lastJobID);
        return b.ToString();
    }

    /// <summary>The game's job, inbox and calendar lists as one object graph (shared references stay shared).</summary>
    private static Il2CppReferenceArray<Il2CppSystem.Object> Graph(CareerStatus.State d)
    {
        var a = new Il2CppReferenceArray<Il2CppSystem.Object>(Slots);
        a[0] = d.m_jobs; a[1] = d.m_todaysJobs; a[2] = d.m_doneJobs; a[3] = d.m_acceptedJobs; a[4] = d.m_deletedJobs;
        a[5] = d.m_rejectedJobs; a[6] = d.m_flaggedJobs; a[7] = d.m_reviews;
        a[8] = d.m_messages; a[9] = d.m_todaysMessages; a[10] = d.m_deletedMsgs; a[11] = d.m_flaggedMsgs; a[12] = d.m_spamMessages;
        a[13] = d.m_emailTags; a[14] = d.m_emailTagNames; a[15] = d.m_calendar;
        a[16] = d.m_completedJobs; a[17] = d.m_completedStoryJobs; a[18] = d.m_generatedJobs; a[19] = d.m_jobTags;
        a[20] = d.m_completedObjectiveTypesInStory; a[21] = d.m_requiredPartIdsForJobs;
        return a;
    }

    private static T As<T>(Il2CppSystem.Object o, int slot) where T : Il2CppObjectBase =>
        o == null ? null : o.TryCast<T>() ?? throw new InvalidDataException($"inbox slot {slot} has the wrong type");

    /// <summary>The guest's game takes the host's lists in place of its own.</summary>
    private static void Take(CareerStatus.State d, Il2CppReferenceArray<Il2CppSystem.Object> a)
    {
        var jobs = new JobList[7];
        for (var i = 0; i < 7; i++) jobs[i] = As<JobList>(a[i], i);
        var reviews = As<Il2CppSystem.Collections.Generic.List<IReview>>(a[7], 7);
        var mails = new EmailList[5];
        for (var i = 0; i < 5; i++) mails[i] = As<EmailList>(a[8 + i], 8 + i);
        var tags = As<Il2CppSystem.Collections.Generic.List<Tag>>(a[13], 13);
        var tagNames = As<StringList>(a[14], 14);
        var calendar = As<Calendar>(a[15], 15);
        var strings = new StringList[5];
        foreach (var (i, slot) in new[] { (0, 16), (1, 17), (2, 18), (3, 20), (4, 21) }) strings[i] = As<StringList>(a[slot], slot);
        var jobTags = As<Il2CppSystem.Collections.Generic.List<JobTag>>(a[19], 19);
        d.m_jobs = jobs[0]; d.m_todaysJobs = jobs[1]; d.m_doneJobs = jobs[2]; d.m_acceptedJobs = jobs[3]; d.m_deletedJobs = jobs[4];
        d.m_rejectedJobs = jobs[5]; d.m_flaggedJobs = jobs[6]; d.m_reviews = reviews;
        d.m_messages = mails[0]; d.m_todaysMessages = mails[1]; d.m_deletedMsgs = mails[2]; d.m_flaggedMsgs = mails[3]; d.m_spamMessages = mails[4];
        d.m_emailTags = tags; d.m_emailTagNames = tagNames;
        if (calendar != null) { d.m_calendar = calendar; calendar.SetDirty(); }
        d.m_completedJobs = strings[0]; d.m_completedStoryJobs = strings[1]; d.m_generatedJobs = strings[2];
        d.m_completedObjectiveTypesInStory = strings[3]; d.m_requiredPartIdsForJobs = strings[4];
        d.m_jobTags = jobTags;
    }

    internal static byte[] Pack(CareerStatus.State d)
    {
        byte[] raw;
        var stream = new Il2CppSystem.IO.MemoryStream();
        try
        {
            new Il2CppSystem.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Serialize(stream, new Il2CppSystem.Object(Graph(d).Pointer));
            raw = stream.ToArray();
        }
        finally { stream.Dispose(); }
        using var output = new MemoryStream();
        using (var z = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
        var w = new NetDataWriter();
        w.Put(Layout); w.Put(d.m_nextJobId); w.Put(d.m_numberJobsCompleted); w.Put(d.m_quitJobs); w.Put(d.m_fiveStarJobs);
        w.Put(d.m_lastJobID != null); if (d.m_lastJobID != null) w.Put(d.m_lastJobID);
        // The compressed graph is the rest of the data.
        w.Put(output.ToArray());
        return w.CopyData();
    }

    private static void Unpack(CareerStatus.State d, byte[] data)
    {
        var r = new NetDataReader(data);
        if (r.GetByte() != Layout) throw new InvalidDataException("the host's inbox has a layout this version doesn't read");
        var next = r.GetInt(); var completed = r.GetInt(); var quit = r.GetInt(); var five = r.GetInt();
        var last = r.GetBool() ? r.GetString(1024) : null;
        if (r.AvailableBytes is < 1 or > JobWorld.MaxBytes) throw new InvalidDataException("the host's inbox data is missing");
        var compressed = r.GetRemainingBytes();
        using var input = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        input.CopyTo(raw);
        if (raw.Length > 64 * 1024 * 1024) throw new InvalidDataException("the host's inbox is too large");
        var stream = new Il2CppSystem.IO.MemoryStream(raw.ToArray());
        Il2CppReferenceArray<Il2CppSystem.Object> graph;
        try
        {
            var obj = new Il2CppSystem.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Deserialize(stream)
                ?? throw new InvalidDataException("the host's inbox is empty");
            graph = new Il2CppReferenceArray<Il2CppSystem.Object>(obj.Pointer);
            if (graph.Length != Slots || stream.Position != stream.Length) throw new InvalidDataException("the host's inbox has an unexpected shape");
        }
        finally { stream.Dispose(); }
        NativeProjection.Run(() =>
        {
            Take(d, graph);
            d.m_nextJobId = next; d.m_numberJobsCompleted = completed; d.m_quitJobs = quit; d.m_fiveStarJobs = five; d.m_lastJobID = last;
        });
    }

    // ---------------- binding ----------------

    internal void BindHost()
    {
        if (!NativeCareer.InCareer) return;
        Clear();
        var d = Data ?? throw new InvalidDataException("the career isn't loaded");
        var facts = ReadFacts(d);
        Handler.BindHost(facts);
        // The guest loads this game's save, which already holds these lists.
        _shared = Fingerprint(d);
        Plugin.Logger.LogInfo($"[jobs] host bound {facts.Count} job(s).");
    }

    internal void BindGuest()
    {
        if (!NativeCareer.InCareer) return;
        Clear();
        if (!Handler.Bound) Plugin.Logger.LogWarning("[jobs] the host's world has no shared job state; jobs stay host-only.");
    }

    // ---------------- per frame ----------------

    public void Update()
    {
        if (!Active) return;
        var now = Time.realtimeSinceStartup;
        foreach (var (op, p) in _pending.ToList())
            if (now - p.At > PendingTimeout) { _pending.Remove(op); Plugin.Logger.LogWarning($"[jobs] {p.Act} of job {p.Job} wasn't answered; continuing."); }
        if (_world.Local != ActorSlot.Host) return;
        while (_forGuest.Count > 0)
        {
            var (act, job, value) = _forGuest.Dequeue();
            try { RunForGuest(act, job, value); }
            catch (Exception e) { _coop.ProjectionFailed($"The client's job action ({act}) could not be run", e); }
        }
        ShareRemoved();
        if (now < _nextObserve || now < _dayHoldUntil || _coop.DayEnding) return;
        _nextObserve = now + ObserveEvery;
        Observe(force: false);
    }

    /// <summary>Host: share the jobs and inbox if they changed since the last share.</summary>
    private void Observe(bool force)
    {
        var d = Data;
        if (d == null) return;
        string fp;
        try { fp = Fingerprint(d); }
        catch (Exception e) { Plugin.Logger.LogWarning("[jobs] could not read the jobs and inbox: " + e.Message); return; }
        if (!force && fp == _shared) return;
        // Once per change: a change that can't be shared is reported once, not every second.
        _shared = fp;
        IReadOnlyList<byte[]> payloads; InboxUpdate update;
        try
        {
            update = new InboxUpdate(ReadFacts(d), Pack(d));
            var host = _coop.Activity.Get(ActorSlot.Host) ?? throw new InvalidDataException("the host isn't in the shared world");
            payloads = JobWorld.Submissions(host.Epoch, update);
        }
        catch (Exception e) { _coop.ProjectionFailed("The host's jobs and inbox could not be shared", e); return; }
        foreach (var payload in payloads)
            if (!_world.Submit(CommandTypes.Jobs, payload, out _))
            {
                Plugin.Logger.LogWarning("[jobs] not connected to the shared world; the inbox is shared again later.");
                _shared = null;
                return;
            }
        Plugin.Logger.LogInfo($"[jobs] shared {update.Jobs.Count} job(s) and the inbox ({update.Native.Length / 1024} KB).");
    }

    // ---------------- requests ----------------

    private static string Verb(JobAct act) => act switch
    {
        JobAct.Accept => "accept", JobAct.Collect => "collect", JobAct.Quit => "quit", _ => "turn down",
    };

    private static string Subject(Job job)
    {
        try { return job.GetSubject() ?? $"job {job.m_id}"; }
        catch { return $"job {job.m_id}"; }
    }

    /// <summary>Guest: ask the host to run <paramref name="act"/> on <paramref name="job"/>.</summary>
    internal void Request(JobAct act, Job job, int value)
    {
        if (!Active) { _coop.Notice("Jobs aren't shared yet; try again in a moment."); return; }
        if (_pending.Values.Any(p => p.Job == job.m_id)) { _coop.Notice("Still waiting for the host about that job."); return; }
        if (Handler.Job(job.m_id) == null) { _coop.Notice("The host doesn't have that job (yet); try again in a moment."); return; }
        var payload = JobWorld.Act(Epoch, act, job.m_id, value);
        _sending = (act, job.m_id);
        try
        {
            if (!_world.Submit(CommandTypes.Jobs, payload, out var op)) { _coop.Notice("Not connected to the host."); return; }
            if (_world.Local != ActorSlot.Host) _pending[op] = (act, job.m_id, Time.realtimeSinceStartup);
        }
        finally { _sending = null; }
        Plugin.Logger.LogInfo($"[jobs] asked the host to {Verb(act)} job {job.m_id} (value {value}).");
    }

    private void OnRejected(OperationId op, RejectReason reason, string text)
    {
        if (!_pending.Remove(op, out var p))
        {
            if (op.Actor != _world.Local || _sending is not { } s) return;
            p = (s.Act, s.Job, 0);
        }
        _coop.Notice($"The host couldn't {Verb(p.Act)} that job: {text}");
        Plugin.Logger.LogInfo($"[jobs] {p.Act} of job {p.Job} refused: {text}");
    }

    /// <summary>Host: may its own button do <paramref name="act"/> on <paramref name="job"/> (its PC isn't in use, the
    /// guest isn't already handling it)? Says why not.</summary>
    internal bool AllowHostOwn(Job job, JobAct act)
    {
        if (!Active) return true;
        string why = null;
        if (Handler.Acted.Contains(job.m_id) || _forGuest.Any(x => x.Job == job.m_id)) why = "The other player is already handling that job.";
        else if (act != JobAct.Accept) why = JobPcBusy(job);
        if (why == null) return true;
        _coop.Notice(why);
        Plugin.Logger.LogInfo($"[jobs] the host's own {act} of job {job.m_id} waits: {why}");
        return false;
    }

    // ---------------- host checks and actions ----------------

    /// <summary>The native record of a shared PC wherever it is, or null.</summary>
    private ComputerSave RecordOf(PcFact pc)
    {
        var objects = _coop.Objects;
        switch (pc.Location.Place)
        {
            case PcPlace.Bench: return objects.FindBench(pc.Id)?.GetCaseInSlot()?.m_computer;
            case PcPlace.Storage: return objects.FindStored(pc.Id);
            case PcPlace.Carrier:
                if (pc.Location.Actor == ActorSlot.Host)
                {
                    var carried = PCBS.Singleton<WorkshopController>.Instance?.GetCarriedCase();
                    return carried != null && objects.IdOf(carried) == pc.Id ? carried.m_computer : null;
                }
                return objects.TryRemoteCarrier(pc, out var record) ? record : null;
            default: return null;
        }
    }

    /// <summary>Why the job's PC can't leave now (carried, or someone works on it), or null.</summary>
    private string JobPcBusy(Job job)
    {
        if (_coop.Objects?.Initialized != true) return null;
        var ids = new HashSet<int> { job.m_id };
        if (job.TryCast<MultiJob>()?.m_subJobs is { } subs) foreach (var s in subs) if (s != null) ids.Add(s.m_id);
        foreach (var pc in _coop.Objects.Model.Pcs)
        {
            ComputerSave record;
            try { record = RecordOf(pc); }
            catch { continue; }
            if (record?.m_status == null || !ids.Contains(record.m_status.jobID)) continue;
            if (pc.Location.Place == PcPlace.Carrier) return "That job's PC is being carried; put it down first.";
            if (_coop.Activity.MovementBlock(pc.Id) is { } busy) return "Someone is working on that job's PC: " + busy;
        }
        return null;
    }

    private string CheckAct(JobAct act, int id, int value)
    {
        try
        {
            var job = FindJob(id);
            if (job == null) return "That job isn't on the host's list.";
            if (!JobWorld.Allows(act, (JobState)(int)job.m_status)) return "That job changed on the host's game; check it again.";
            if (_forGuest.Any(x => x.Job == id)) return "The other player is already handling that job.";
            if (act != JobAct.Accept) return JobPcBusy(job);
            if (!job.CanAccept())
            {
                var why = job.GetAcceptFailText();
                return string.IsNullOrEmpty(why) ? "That job can't be taken yet." : why;
            }
            var type = job.GetDesc()?.m_type;
            if (type is JobDesc.Type.BUILD or JobDesc.Type.PACKAGE && PCBS.Singleton<WorkshopController>.Instance?.AnyPostItSlotsFree() == false)
                return "There's no free note slot in the workshop for that job.";
            return null;
        }
        catch (Exception e) { return "The job could not be checked: " + e.Message; }
    }

    /// <summary>The objectives <c>OnPositiveAction</c> drops before collecting: "return the PC" (decompile, the
    /// <c>RemoveAll</c> predicate tests <c>Objective.m_type == RETURN_PC</c>; a multi-job does it on each part).</summary>
    private static void DropReturnObjectives(Job job)
    {
        void Drop(Job j)
        {
            var list = j?.m_objectives;
            if (list == null) return;
            for (var i = list.Count - 1; i >= 0; i--) if (list[i] != null && list[i].m_type == Objective.Type.RETURN_PC) list.RemoveAt(i);
        }
        if (job.TryCast<MultiJob>()?.m_subJobs is { } subs) foreach (var s in subs) Drop(s);
        else Drop(job);
    }

    private void RunForGuest(JobAct act, int id, int value)
    {
        var job = FindJob(id) ?? throw new InvalidDataException($"job {id} is gone from the host's game");
        if (!JobWorld.Allows(act, (JobState)(int)job.m_status)) { Plugin.Logger.LogWarning($"[jobs] job {id} changed before the client's {act} ran; skipped."); return; }
        var subject = Subject(job);
        HostRunning = true;
        try
        {
            switch (act)
            {
                case JobAct.Accept:
                    job.OnAccept();
                    break;
                case JobAct.Collect:
                    DropReturnObjectives(job);
                    job.OnCollected();
                    break;
                case JobAct.Quit:
                {
                    // The fee the guest's confirmation showed (decompile, <QuitJob>b__2: the job's initial value
                    // minus its PC's value now), charged as the game charges it.
                    var fee = job.GetInitialValue() - value;
                    if (fee > 0 && !Status.SpendCash(fee, false, eTransactionCategory.UNKNOWN)) Plugin.Logger.LogWarning($"[jobs] the host's game didn't charge the quit fee {fee}.");
                    job.DoQuit();
                    break;
                }
                default:
                    job.OnRejected();
                    break;
            }
        }
        finally { HostRunning = false; }
        _coop.AddChat($"Client {(act == JobAct.Collect ? "collected" : act == JobAct.Accept ? "accepted" : act == JobAct.Quit ? "quit" : "turned down")} {subject}.");
        Plugin.Logger.LogInfo($"[jobs] ran the client's {act} of job {id}.");
        // Share the result at once.
        Observe(force: false);
    }

    // ---------------- PCs the host's game removes ----------------

    /// <summary>Host: the shared bench and storage PCs its game shows now.</summary>
    internal List<NetObjectId> PresentShared()
    {
        var list = new List<NetObjectId>();
        if (!Active || _world.Local != ActorSlot.Host || _coop.Objects?.Initialized != true) return list;
        foreach (var pc in _coop.Objects.Model.Pcs)
        {
            if (pc.Location.Place == PcPlace.Bench && _coop.Objects.FindBench(pc.Id) != null) list.Add(pc.Id);
            else if (pc.Location.Place == PcPlace.Storage && _coop.Objects.FindStored(pc.Id) != null) list.Add(pc.Id);
        }
        return list;
    }

    /// <summary>Host: after <c>RemoveJobItem</c>, the PCs from <paramref name="before"/> its game no longer shows.</summary>
    internal void NoteRemoved(List<NetObjectId> before)
    {
        if (before == null || before.Count == 0) return;
        var now = new HashSet<NetObjectId>(PresentShared());
        foreach (var id in before) if (!now.Contains(id) && !_removed.Contains(id)) _removed.Add(id);
    }

    private void ShareRemoved()
    {
        if (_removed.Count == 0) return;
        foreach (var id in _removed.ToList())
        {
            _removed.Remove(id);
            if (_coop.Objects.Model.Pcs.All(x => x.Id != id)) continue;
            if (_world.Submit(CommandTypes.PcLifecycle, PcLifecycle.Delete(Epoch, id, PcRemoval.Game), out var op))
                Plugin.Logger.LogInfo($"[jobs] the host's game removed job PC {id}; shared ({op}).");
        }
    }

    // ---------------- IJobNative ----------------

    public void Updated(in MutationContext ctx, InboxUpdate update)
    {
        if (_world.Local == ActorSlot.Host) return;
        var d = Data;
        if (d == null) { Plugin.Logger.LogWarning("[jobs] the career isn't loaded; the host's inbox comes with its world."); return; }
        try
        {
            Unpack(d, update.Native);
            Refresh();
            Plugin.Logger.LogInfo($"[jobs] took the host's {update.Jobs.Count} job(s) and inbox.");
        }
        catch (Exception e) { _coop.ProjectionFailed("The host's jobs and inbox could not be shown on this game", e); }
    }

    /// <summary>Open inboxes and calendars show the new lists; the job baskets come back from the shared cart state.</summary>
    private static void Refresh()
    {
        foreach (var app in UnityEngine.Object.FindObjectsOfType<EmailApp>())
        {
            try { if (app != null) NativeProjection.Run(app.RebuildInbox); }
            catch (Exception e) { Plugin.Logger.LogWarning("[jobs] an inbox could not show the new e-mails: " + e.Message); }
        }
        try { NativeCommerce.Current?.FollowBaskets(); }
        catch (Exception e) { Plugin.Logger.LogWarning("[jobs] the job baskets could not follow: " + e.Message); }
    }

    public void Acted(in MutationContext ctx, JobAct act, int job, int value)
    {
        if (ctx.FromLocalPlayer && _pending.Remove(ctx.Op))
            _coop.Notice(act switch { JobAct.Accept => "Job accepted.", JobAct.Collect => "Payment collected.", JobAct.Quit => "Job quit.", _ => "Job turned down." });
        if (_world.Local != ActorSlot.Host || ctx.Op.Actor == ActorSlot.Host) return;
        // Run outside command apply: the game's job code reaches paths that share their own changes.
        _forGuest.Enqueue((act, job, value));
    }
}

using System.Security.Cryptography;
using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>A job's status in the game (<c>Job.Status</c>, same numbers).</summary>
public enum JobState : byte { New = 0, Read = 1, InTransit = 2, Accepted = 3, Finished = 4, Collected = 5, Done = 6, Expired = 7, Deleted = 8, Rejected = 9 }

/// <summary>What a player does with a job.</summary>
public enum JobAct : byte { Accept = 1, Collect = 2, Quit = 3, Reject = 4 }

/// <summary>An inbox e-mail with a button that changes the shared career (phase 17d): a rent or utility bill (Pay),
/// or the shop renovation offer (accept: paid now, built at the day end).</summary>
public enum MailKind : byte { Rent = 1, Utility = 2, Renovation = 3 }

/// <summary>One such e-mail as the host's game holds it: its kind, the game's creation time stamp and its cost. The
/// guest's inbox is a copy of the host's, so the same three name the same e-mail on both games.</summary>
public sealed record MailFact(MailKind Kind, long Time, int Cost)
{
    public bool IsValid => Enum.IsDefined(Kind) && Cost is >= 0 and <= JobWorld.MaxValue;

    internal void Write(NetDataWriter w) { w.Put((byte)Kind); w.Put(Time); w.Put(Cost); }
    internal static MailFact Read(NetDataReader r) => new((MailKind)r.GetByte(), r.GetLong(), r.GetInt());
}

/// <summary>One job as the host's game holds it: its ID, status and the multi-job it belongs to (0 for none).</summary>
public sealed record JobFact(int Id, JobState State, int Master)
{
    public bool IsValid => Id > 0 && Enum.IsDefined(State) && Master >= 0 && Master != Id;
}

/// <summary>The host's jobs and inbox after a change: the job facts the model checks actions against, and the game's
/// own job, e-mail, review and calendar lists (serialized by the game's serializer, compressed), which the guest's
/// game takes in place of its own.</summary>
public sealed record InboxUpdate(IReadOnlyList<JobFact> Jobs, byte[] Native)
{
    public const int MaxJobs = 4096, MaxMails = 256;
    /// <summary>The e-mails whose buttons the host's game runs for either player (phase 17d).</summary>
    public IReadOnlyList<MailFact> Mails { get; init; } = Array.Empty<MailFact>();

    public byte[] Encode()
    {
        var w = new NetDataWriter();
        w.Put(Jobs.Count);
        foreach (var j in Jobs) { w.Put(j.Id); w.Put((byte)j.State); w.Put(j.Master); }
        w.Put(Mails.Count);
        foreach (var m in Mails) m.Write(w);
        Protocol.WriteBlob(w, Native);
        return w.CopyData();
    }

    public static InboxUpdate Decode(byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            var n = r.GetInt();
            if (n is < 0 or > MaxJobs) throw new FormatException("Too many jobs.");
            var jobs = new JobFact[n];
            for (var i = 0; i < n; i++)
            {
                jobs[i] = new JobFact(r.GetInt(), (JobState)r.GetByte(), r.GetInt());
                if (!jobs[i].IsValid) throw new FormatException("Invalid job.");
            }
            if (jobs.Select(j => j.Id).Distinct().Count() != n) throw new FormatException("A job is listed twice.");
            var mails = ReadMails(r);
            var native = Protocol.ReadBlob(r, JobWorld.MaxBytes, out _) ?? throw new FormatException("Invalid inbox data.");
            if (r.AvailableBytes != 0) throw new FormatException("Trailing inbox bytes.");
            return new InboxUpdate(jobs, native) { Mails = mails };
        }
        catch (Exception e) when (Protocol.IsReadError(e)) { throw new FormatException("Truncated inbox update."); }
    }

    internal static MailFact[] ReadMails(NetDataReader r)
    {
        var n = r.GetInt();
        if (n is < 0 or > MaxMails) throw new FormatException("Too many e-mails.");
        var mails = new MailFact[n];
        for (var i = 0; i < n; i++)
        {
            mails[i] = MailFact.Read(r);
            if (!mails[i].IsValid) throw new FormatException("Invalid e-mail.");
        }
        if (mails.Select(m => (m.Kind, m.Time)).Distinct().Count() != n) throw new FormatException("An e-mail is listed twice.");
        return mails;
    }
}

/// <summary>Native side of shared jobs. Runs on both peers in host order and must not submit commands.</summary>
public interface IJobNative
{
    /// <summary>The host's jobs and inbox changed. The host's game already shows it; the guest's takes it.</summary>
    void Updated(in MutationContext ctx, InboxUpdate update);
    /// <summary>A player's job action is accepted. The host's game runs it (its result reaches the guest as an update);
    /// the acting game says so. <paramref name="value"/> is the job PC's value the quitting player's game showed.</summary>
    void Acted(in MutationContext ctx, JobAct act, int job, int value);
    /// <summary>A player's bill payment or renovation order is accepted (phase 17d). The host's game runs the
    /// e-mail's own button.</summary>
    void MailActed(in MutationContext ctx, MailFact mail);
}

/// <summary>
/// Phase 17b (owner decisions, November 9, 2026): shared jobs and the shared inbox in a co-op Career.
/// <list type="bullet">
/// <item>The host's game keeps the jobs, e-mails, reviews and calendar. When they change, the host sends them as an
/// <b>Update</b> (chunked like other large results: <b>Chunk</b>s, then <b>Commit</b> with the length and SHA-256);
/// the guest's game takes them in place of its own. Job offers are inbox e-mails, so the inbox travels with them.</item>
/// <item>Either player accepts, collects, quits or turns down a job with an <b>Act</b>. The model refuses a job that
/// is unknown, in the wrong status or already being handled since the host's last update (one result wins a race);
/// the host's game checks its own live job as well (<see cref="ActCheck"/>), then runs the game's own action.</item>
/// <item>Phase 17d: either player pays a bill or orders the shop renovation with a <b>Mail</b> naming the e-mail as
/// the host's last update listed it. One action per e-mail until the next update; the host's game checks the
/// e-mail and the cash (<see cref="MailCheck"/>), then runs the e-mail's own button.</item>
/// </list>
/// Free Build leaves it unbound and refuses every command.
/// </summary>
public sealed class JobWorld : ICommandHandler, IWorldState
{
    private enum Step : byte { Chunk = 1, Commit = 2, Act = 3, Mail = 4 }
    public const int MaxChunkBytes = 15 * 1024, MaxBytes = 8 * 1024 * 1024, MaxChunks = MaxBytes / MaxChunkBytes + 1, MaxValue = 100_000_000;

    private readonly ActivityWorld _activity;
    private readonly IJobNative _native;
    private readonly Dictionary<int, JobFact> _jobs = new();
    private readonly HashSet<int> _acted = new();
    private readonly Dictionary<(MailKind, long), MailFact> _mails = new();
    private readonly HashSet<(MailKind, long)> _mailActed = new();
    private (ulong Epoch, int Total, List<byte[]> Chunks)? _chunks;

    public JobWorld(ActivityWorld activity, IJobNative native) { _activity = activity; _native = native; }
    public string Name => "jobs";

    /// <summary>Host: why its game can't do <paramref name="act"/> on this job now (gone, other status, no free note
    /// slot, its PC in use), or null. Called after the model checks.</summary>
    public Func<JobAct, int, int, string?>? ActCheck { get; set; }
    /// <summary>Host: why its game can't run this e-mail's button now (gone, already paid, not enough cash), or null.</summary>
    public Func<MailFact, string?>? MailCheck { get; set; }

    public bool Bound { get; private set; }
    /// <summary>The number of host updates taken in this generation.</summary>
    public int Revision { get; private set; }
    public IReadOnlyCollection<JobFact> Jobs => _jobs.Values;
    public JobFact? Job(int id) => _jobs.GetValueOrDefault(id);
    /// <summary>Jobs acted on since the host's last update.</summary>
    public IReadOnlyCollection<int> Acted => _acted;
    public IReadOnlyCollection<MailFact> Mails => _mails.Values;
    /// <summary>Whether this e-mail was acted on since the host's last update.</summary>
    public bool MailActed(MailFact mail) => _mailActed.Contains((mail.Kind, mail.Time));
    /// <summary>Any job or e-mail action accepted and not yet in a host update.</summary>
    public bool AnyActed => _acted.Count > 0 || _mailActed.Count > 0;

    public void Reset() { _jobs.Clear(); _acted.Clear(); _mails.Clear(); _mailActed.Clear(); _chunks = null; Bound = false; Revision = 0; }

    /// <summary>Host, when a world generation starts: the jobs (and actionable e-mails) as its game holds them.</summary>
    public void BindHost(IEnumerable<JobFact> jobs, IEnumerable<MailFact>? mails = null)
    {
        var list = jobs.ToList();
        var mailList = mails?.ToList() ?? new List<MailFact>();
        if (list.Count > InboxUpdate.MaxJobs || list.Any(j => !j.IsValid) || list.Select(j => j.Id).Distinct().Count() != list.Count)
            throw new InvalidDataException("Invalid job binding.");
        if (mailList.Count > InboxUpdate.MaxMails || mailList.Any(m => !m.IsValid) || mailList.Select(m => (m.Kind, m.Time)).Distinct().Count() != mailList.Count)
            throw new InvalidDataException("Invalid e-mail binding.");
        Reset();
        foreach (var j in list) _jobs[j.Id] = j;
        foreach (var m in mailList) _mails[(m.Kind, m.Time)] = m;
        Bound = true;
    }

    /// <summary>Whether a job in <paramref name="state"/> allows <paramref name="act"/> (the game's own buttons).</summary>
    public static bool Allows(JobAct act, JobState state) => act switch
    {
        JobAct.Accept or JobAct.Reject => state is JobState.New or JobState.Read,
        JobAct.Collect => state == JobState.Finished,
        JobAct.Quit => state is JobState.InTransit or JobState.Accepted or JobState.Finished,
        _ => false,
    };

    // ---------------- commands ----------------

    private static byte[] Encode(Action<NetDataWriter> f) { var w = new NetDataWriter(); f(w); return w.CopyData(); }

    /// <summary>Host: one update as chunks then the commit, ready to submit in order.</summary>
    public static IReadOnlyList<byte[]> Submissions(ulong epoch, InboxUpdate update)
    {
        var data = update.Encode();
        if (data.Length > MaxBytes) throw new InvalidDataException("The inbox is too large to share.");
        var total = Math.Max(1, (data.Length + MaxChunkBytes - 1) / MaxChunkBytes);
        var list = new List<byte[]>();
        for (var i = 0; i < total; i++)
        {
            var chunk = data.AsSpan(i * MaxChunkBytes, Math.Min(MaxChunkBytes, data.Length - i * MaxChunkBytes)).ToArray();
            list.Add(Encode(w => { w.Put((byte)Step.Chunk); w.Put(epoch); w.Put(i); w.Put(total); Protocol.WriteBlob(w, chunk); }));
        }
        list.Add(Encode(w => { w.Put((byte)Step.Commit); w.Put(epoch); w.Put(data.Length); Protocol.WriteBlob(w, SHA256.HashData(data)); }));
        return list;
    }

    /// <summary>A player's job action. <paramref name="value"/> is the job PC's value a quit showed (0 otherwise).</summary>
    public static byte[] Act(ulong epoch, JobAct act, int job, int value = 0) =>
        Encode(w => { w.Put((byte)Step.Act); w.Put(epoch); w.Put((byte)act); w.Put(job); w.Put(value); });

    /// <summary>A player's bill payment or renovation order (phase 17d).</summary>
    public static byte[] Mail(ulong epoch, MailFact mail) => Encode(w => { w.Put((byte)Step.Mail); w.Put(epoch); mail.Write(w); });

    private sealed record Command(Step Step, ulong Epoch, int Index, int Total, byte[] Data, int Length, byte[] Hash)
    {
        public MailFact? Mail { get; init; }
        public JobAct Act { get; init; }
        public int Job { get; init; }
        public int Value { get; init; }
    }

    private static Command Parse(NetDataReader r)
    {
        var step = (Step)r.GetByte(); var epoch = r.GetULong();
        Command c;
        switch (step)
        {
            case Step.Chunk:
            {
                var index = r.GetInt(); var total = r.GetInt();
                var data = Protocol.ReadBlob(r, MaxChunkBytes, out _) ?? throw new FormatException("Invalid inbox chunk.");
                c = new(step, epoch, index, total, data, 0, Array.Empty<byte>());
                break;
            }
            case Step.Commit:
            {
                var length = r.GetInt();
                var hash = Protocol.ReadBlob(r, 32, out var declared) ?? throw new FormatException("Invalid inbox hash.");
                if (length is < 0 or > MaxBytes || declared != 32) throw new FormatException("Invalid inbox commit.");
                c = new(step, epoch, 0, 0, Array.Empty<byte>(), length, hash);
                break;
            }
            case Step.Act:
            {
                var act = (JobAct)r.GetByte(); var job = r.GetInt(); var value = r.GetInt();
                if (!Enum.IsDefined(act)) throw new FormatException("Unknown job action.");
                c = new(step, epoch, 0, 0, Array.Empty<byte>(), 0, Array.Empty<byte>()) { Act = act, Job = job, Value = value };
                break;
            }
            case Step.Mail:
            {
                var mail = MailFact.Read(r);
                if (!Enum.IsDefined(mail.Kind)) throw new FormatException("Unknown e-mail kind.");
                c = new(step, epoch, 0, 0, Array.Empty<byte>(), 0, Array.Empty<byte>()) { Mail = mail };
                break;
            }
            default: throw new FormatException("Unknown job step.");
        }
        if (r.AvailableBytes != 0) throw new FormatException("Trailing job bytes.");
        return c;
    }

    private byte[]? Assembled(ulong epoch) =>
        _chunks is { } g && g.Epoch == epoch && g.Chunks.Count == g.Total ? g.Chunks.SelectMany(x => x).ToArray() : null;

    /// <summary>The job, its multi-job and the multi-job's other parts: acting on one handles them all.</summary>
    private IEnumerable<int> Family(JobFact job)
    {
        var master = job.Master != 0 ? job.Master : job.Id;
        yield return master;
        foreach (var j in _jobs.Values) if (j.Master == master) yield return j.Id;
    }

    private string? CheckAct(Command c)
    {
        if (Job(c.Job) is not { } job) return "That job isn't on the host's list (any more).";
        if (Family(job).Any(_acted.Contains)) return "The other player is already handling that job.";
        if (!Allows(c.Act, job.State)) return c.Act switch
        {
            JobAct.Accept => "That job can't be accepted now.",
            JobAct.Collect => "The host's game doesn't show that job as finished yet, or its payment was already collected.",
            JobAct.Quit => "That job isn't in progress.",
            _ => "That job can't be turned down now.",
        };
        if (c.Value is < 0 or > MaxValue || (c.Act != JobAct.Quit && c.Value != 0)) return "Invalid job action.";
        return null;
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        if (!Bound) return CommandVerdict.Reject("Jobs are shared only in a co-op career.");
        if (c.Step == Step.Mail)
        {
            if (_activity.Get(ctx.Op.Actor) is not { } actor || actor.Epoch != c.Epoch) return CommandVerdict.Reject("Actor presence changed.");
            var m = c.Mail!;
            if (!_mails.TryGetValue((m.Kind, m.Time), out var known) || known != m)
                return CommandVerdict.Reject(m.Kind == MailKind.Renovation ? "The host's inbox has no renovation offer like that (any more)." : "The host's inbox has no unpaid bill like that (any more).");
            if (_mailActed.Contains((m.Kind, m.Time))) return CommandVerdict.Reject("The other player is already handling that e-mail.");
            return MailCheck?.Invoke(m) is { } mailWhy ? CommandVerdict.Reject(mailWhy) : CommandVerdict.Accept;
        }
        if (c.Step == Step.Act)
        {
            if (_activity.Get(ctx.Op.Actor) is not { } actor || actor.Epoch != c.Epoch) return CommandVerdict.Reject("Actor presence changed.");
            if (CheckAct(c) is { } why) return CommandVerdict.Reject(why);
            return ActCheck?.Invoke(c.Act, c.Job, c.Value) is { } nativeWhy ? CommandVerdict.Reject(nativeWhy) : CommandVerdict.Accept;
        }
        if (ctx.Op.Actor != ActorSlot.Host) return CommandVerdict.Reject("Only the host shares its jobs and inbox.");
        if (_activity.Get(ActorSlot.Host) is not { } host || host.Epoch != c.Epoch) return CommandVerdict.Reject("Actor presence changed.");
        if (c.Step == Step.Chunk)
        {
            if (c.Total is < 1 or > MaxChunks || c.Data.Length > MaxChunkBytes) return CommandVerdict.Reject("Invalid inbox chunk.");
            if (c.Index == 0) return CommandVerdict.Accept;
            if (_chunks is not { } g || g.Epoch != c.Epoch || g.Total != c.Total || g.Chunks.Count != c.Index) return CommandVerdict.Reject("Inbox chunk out of order.");
            return g.Chunks.Sum(x => x.Length) + c.Data.Length > MaxBytes ? CommandVerdict.Reject("The inbox update is too large.") : CommandVerdict.Accept;
        }
        var data = Assembled(c.Epoch);
        if (data == null || data.Length != c.Length || !SHA256.HashData(data).AsSpan().SequenceEqual(c.Hash)) return CommandVerdict.Reject("The inbox update hasn't fully arrived.");
        try { InboxUpdate.Decode(data); }
        catch (FormatException e) { return CommandVerdict.Reject(e.Message); }
        return CommandVerdict.Accept;
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        switch (c.Step)
        {
            case Step.Act:
                _acted.Add(c.Job);
                _native.Acted(ctx, c.Act, c.Job, c.Value);
                return;
            case Step.Mail:
                _mailActed.Add((c.Mail!.Kind, c.Mail.Time));
                _native.MailActed(ctx, c.Mail);
                return;
            case Step.Chunk:
                // A guest that joined halfway through an update can't assemble it; the snapshot it loaded already has it.
                if (c.Index == 0) _chunks = (c.Epoch, c.Total, new List<byte[]> { c.Data });
                else if (_chunks is { } g && g.Epoch == c.Epoch && g.Chunks.Count == c.Index) g.Chunks.Add(c.Data);
                return;
        }
        var data = Assembled(c.Epoch);
        _chunks = null;
        if (data == null || data.Length != c.Length || !SHA256.HashData(data).AsSpan().SequenceEqual(c.Hash)) return;
        var update = InboxUpdate.Decode(data);
        _jobs.Clear();
        foreach (var j in update.Jobs) _jobs[j.Id] = j;
        _mails.Clear();
        foreach (var m in update.Mails) _mails[(m.Kind, m.Time)] = m;
        // The host's game ran every accepted action before it made this update.
        _acted.Clear(); _mailActed.Clear();
        Revision++;
        _native.Updated(ctx, update);
    }

    // ---------------- snapshot state ----------------

    public void Write(NetDataWriter w)
    {
        w.Put(Bound);
        if (!Bound) return;
        w.Put(Revision); w.Put(_jobs.Count);
        foreach (var j in _jobs.Values.OrderBy(j => j.Id)) { w.Put(j.Id); w.Put((byte)j.State); w.Put(j.Master); }
        w.Put(_mails.Count);
        foreach (var m in _mails.Values.OrderBy(m => m.Kind).ThenBy(m => m.Time)) m.Write(w);
    }

    public void Read(NetDataReader r)
    {
        Reset();
        if (!r.GetBool()) return;
        Revision = r.GetInt();
        var n = r.GetInt();
        if (n is < 0 or > InboxUpdate.MaxJobs || Revision < 0) throw new InvalidDataException("Invalid job state.");
        for (var i = 0; i < n; i++)
        {
            var j = new JobFact(r.GetInt(), (JobState)r.GetByte(), r.GetInt());
            if (!j.IsValid || _jobs.ContainsKey(j.Id)) throw new InvalidDataException("Invalid job in the job state.");
            _jobs[j.Id] = j;
        }
        // Saves from 0.63 and 0.64 end here: no e-mails are listed until the host's next update.
        if (r.AvailableBytes > 0)
        {
            try { foreach (var m in InboxUpdate.ReadMails(r)) _mails[(m.Kind, m.Time)] = m; }
            catch (FormatException e) { throw new InvalidDataException(e.Message); }
        }
        Bound = true;
    }
}

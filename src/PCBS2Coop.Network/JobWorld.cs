using System.Security.Cryptography;
using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>A job's status in the game (<c>Job.Status</c>, same numbers).</summary>
public enum JobState : byte { New = 0, Read = 1, InTransit = 2, Accepted = 3, Finished = 4, Collected = 5, Done = 6, Expired = 7, Deleted = 8, Rejected = 9 }

/// <summary>What a player does with a job.</summary>
public enum JobAct : byte { Accept = 1, Collect = 2, Quit = 3, Reject = 4 }

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
    public const int MaxJobs = 4096;

    public byte[] Encode()
    {
        var w = new NetDataWriter();
        w.Put(Jobs.Count);
        foreach (var j in Jobs) { w.Put(j.Id); w.Put((byte)j.State); w.Put(j.Master); }
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
            var native = Protocol.ReadBlob(r, JobWorld.MaxBytes, out _) ?? throw new FormatException("Invalid inbox data.");
            if (r.AvailableBytes != 0) throw new FormatException("Trailing inbox bytes.");
            return new InboxUpdate(jobs, native);
        }
        catch (Exception e) when (Protocol.IsReadError(e)) { throw new FormatException("Truncated inbox update."); }
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
/// </list>
/// Free Build leaves it unbound and refuses every command.
/// </summary>
public sealed class JobWorld : ICommandHandler, IWorldState
{
    private enum Step : byte { Chunk = 1, Commit = 2, Act = 3 }
    public const int MaxChunkBytes = 15 * 1024, MaxBytes = 8 * 1024 * 1024, MaxChunks = MaxBytes / MaxChunkBytes + 1, MaxValue = 100_000_000;

    private readonly ActivityWorld _activity;
    private readonly IJobNative _native;
    private readonly Dictionary<int, JobFact> _jobs = new();
    private readonly HashSet<int> _acted = new();
    private (ulong Epoch, int Total, List<byte[]> Chunks)? _chunks;

    public JobWorld(ActivityWorld activity, IJobNative native) { _activity = activity; _native = native; }
    public string Name => "jobs";

    /// <summary>Host: why its game can't do <paramref name="act"/> on this job now (gone, other status, no free note
    /// slot, its PC in use), or null. Called after the model checks.</summary>
    public Func<JobAct, int, int, string?>? ActCheck { get; set; }

    public bool Bound { get; private set; }
    /// <summary>The number of host updates taken in this generation.</summary>
    public int Revision { get; private set; }
    public IReadOnlyCollection<JobFact> Jobs => _jobs.Values;
    public JobFact? Job(int id) => _jobs.GetValueOrDefault(id);
    /// <summary>Jobs acted on since the host's last update.</summary>
    public IReadOnlyCollection<int> Acted => _acted;

    public void Reset() { _jobs.Clear(); _acted.Clear(); _chunks = null; Bound = false; Revision = 0; }

    /// <summary>Host, when a world generation starts: the jobs as its game holds them.</summary>
    public void BindHost(IEnumerable<JobFact> jobs)
    {
        var list = jobs.ToList();
        if (list.Count > InboxUpdate.MaxJobs || list.Any(j => !j.IsValid) || list.Select(j => j.Id).Distinct().Count() != list.Count)
            throw new InvalidDataException("Invalid job binding.");
        Reset();
        foreach (var j in list) _jobs[j.Id] = j;
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

    private sealed record Command(Step Step, ulong Epoch, int Index, int Total, byte[] Data, int Length, byte[] Hash)
    {
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
            JobAct.Collect => "That job isn't finished, or its payment was already collected.",
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
        // The host's game ran every accepted action before it made this update.
        _acted.Clear();
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
        Bound = true;
    }
}

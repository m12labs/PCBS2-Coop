using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>Why the host refused a command. 0 is reserved for "accepted" in <see cref="MessageType.CommandResult"/>.</summary>
public enum RejectReason : byte
{
    None = 0,
    /// <summary>The command was made for an older world (before a load/reset).</summary>
    StaleGeneration = 1,
    /// <summary>Sequence number older than one already processed, with no remembered outcome.</summary>
    OutOfOrder = 2,
    UnknownCommand = 3,
    Malformed = 4,
    TooLarge = 5,
    /// <summary>The command's own validator refused it (target gone, slot taken…).</summary>
    Invalid = 6,
    /// <summary>The host has no world yet (generation 0).</summary>
    NoWorld = 7,
    /// <summary>Not sent by the host; raised locally when a guest's pending command can no longer succeed.</summary>
    Abandoned = 8,
}

public readonly record struct CommandVerdict(bool Accepted, string Reason)
{
    public static readonly CommandVerdict Accept = new(true, "");
    public static CommandVerdict Reject(string reason) => new(false, reason);
}

/// <summary>What the host knows while validating a command.</summary>
public readonly record struct CommandContext(OperationId Op, uint Generation, ulong NextRevision);

/// <summary>What both peers know while applying an accepted change.</summary>
public readonly record struct MutationContext(OperationId Op, uint Generation, ulong Revision, bool FromLocalPlayer);

/// <summary>
/// One kind of world change. <see cref="Validate"/> runs on the host only; <see cref="Apply"/> runs on both peers
/// in host order, on the main thread. Apply must only make the change; it must not submit gameplay side effects
/// that would come back as a second change.
/// </summary>
public interface ICommandHandler
{
    string Name { get; }
    CommandVerdict Validate(in CommandContext ctx, NetDataReader payload);
    void Apply(in MutationContext ctx, NetDataReader payload);
}

/// <summary>Optional host construction of the accepted payload. Validation and canonicalization must not mutate
/// world facts. Propose IDs from the allocator; commit them only in Apply. Duplicate requests skip this path.</summary>
public interface ICanonicalCommandHandler : ICommandHandler
{
    byte[] Canonicalize(in CommandContext ctx, NetDataReader request);
}

/// <summary>
/// Mod-side world state that a joining guest needs and that isn't in the game's save (for example object IDs).
/// It travels with a load snapshot. <see cref="Reset"/> runs when a world generation starts.
/// </summary>
public interface IWorldState
{
    string Name { get; }
    void Reset();
    void Write(NetDataWriter w);
    void Read(NetDataReader r);
}

/// <summary>Where a load snapshot sits in the host's change stream, plus the mod state at that point.</summary>
public sealed record SnapshotPosition(uint Generation, ulong Revision, ulong ChainHash, byte[] State);

public sealed class WorldStats
{
    public long Submitted, Accepted, Rejected, Applied, Duplicates, StaleDropped, Malformed, UnexpectedResults, Held;
    public override string ToString() =>
        $"submitted {Submitted}, accepted {Accepted}, rejected {Rejected}, applied {Applied}, duplicates {Duplicates}, stale dropped {StaleDropped}, malformed {Malformed}, held {Held}";
}

/// <summary>
/// Host-ordered world changes for two players.
/// <list type="bullet">
/// <item>Each participant numbers its own requests (<see cref="OperationId"/>). The guest sends them to the host.</item>
/// <item>The host validates each request, gives accepted ones the next revision of the current world generation,
/// applies them and forwards them. Both peers apply the same changes in the same order.</item>
/// <item>A world generation is one incarnation of the world. Starting a new one (load/reset) makes every older
/// request and change stale.</item>
/// <item>Each change carries a running hash of all changes so far, so a peer that applied something different notices.</item>
/// <item>A guest enters the world only from a host snapshot (<see cref="Admitted"/>). Changes after the snapshot's
/// position are held while it loads and applied afterwards, so each change applies exactly once.</item>
/// </list>
/// Not thread-safe; call everything from the main thread (CoopSession.Poll delivers messages there).
/// </summary>
public sealed class WorldSync
{
    private const int OutcomeMemory = 256;
    private const int MaxPending = 256;

    private const int MaxHeldMessages = 10_000;
    private const long MaxHeldBytes = 32L * 1024 * 1024;

    private readonly Dictionary<ushort, ICommandHandler> _handlers = new();
    private readonly List<IWorldState> _states = new();
    private readonly List<(MessageType Type, byte[] Body)> _held = new();
    private long _heldBytes;
    private SnapshotPosition? _holdFor;
    private readonly Action<NetDataWriter> _send;
    private readonly Action<string>? _log;
    private readonly NetDataWriter _w = new();

    // Host state
    private readonly uint[] _lastSeq = new uint[3];
    private readonly Dictionary<OperationId, Outcome> _outcomes = new();
    private readonly Queue<OperationId> _outcomeOrder = new();
    private readonly Queue<(ushort Type, byte[] Payload, uint Seq, uint Generation)> _localQueue = new();
    private bool _processing;

    // Shared state
    private uint _nextSeq;
    private readonly Dictionary<uint, PendingCommand> _pending = new();

    public WorldSync(ActorSlot local, Action<NetDataWriter> sendReliable, Action<string>? log = null)
    {
        if (local == ActorSlot.None) throw new ArgumentException("Need a host or guest seat.", nameof(local));
        Local = local;
        _send = sendReliable;
        _log = log;
    }

    public ActorSlot Local { get; }
    public bool IsAuthority => Local == ActorSlot.Host;
    /// <summary>0 means no world yet.</summary>
    public uint Generation { get; private set; }
    /// <summary>Number of changes applied in the current generation.</summary>
    public ulong Revision { get; private set; }
    public ulong ChainHash { get; private set; } = ChainSeed;
    /// <summary>Set when this peer's change stream can no longer be trusted. Cleared by the next generation.</summary>
    public string? Divergence { get; private set; }
    /// <summary>False until a guest learns the host's world (Welcome). Always true on the host.</summary>
    public bool Synchronized { get; private set; }
    /// <summary>Guest: it loaded a host snapshot of the current generation. Host: it has a world (generation > 0).</summary>
    public bool Admitted => IsAuthority ? Generation > 0 : _admitted;
    private bool _admitted;
    /// <summary>Guest: world changes are being held while a snapshot loads.</summary>
    public bool Holding => _holdFor != null;
    public int PendingCount => _pending.Count;
    public WorldStats Stats { get; } = new();

    /// <summary>Whether outgoing guest commands can reach the host. Set by the session.</summary>
    internal bool Linked { get; set; }

    /// <summary>A command this peer submitted was accepted (revision given).</summary>
    public event Action<OperationId, ulong>? Accepted;
    /// <summary>A command this peer submitted was refused.</summary>
    public event Action<OperationId, RejectReason, string>? Rejected;
    public event Action<uint, string>? GenerationStarted;
    /// <summary>This peer's view of the world stopped matching the host's change stream.</summary>
    public event Action<string>? Diverged;
    /// <summary>Guest: the host started a different world after this guest was admitted. The guest is no longer in the world.</summary>
    public event Action<string>? WorldReplaced;

    /// <summary>Add mod state that travels with load snapshots. Names must be unique.</summary>
    public void RegisterState(IWorldState state)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        if (_states.Any(x => x.Name == state.Name)) throw new InvalidOperationException($"World state '{state.Name}' is already registered.");
        _states.Add(state);
    }

    public void Register(ushort type, ICommandHandler handler)
    {
        if (_handlers.ContainsKey(type)) throw new InvalidOperationException($"Command type {type} is already registered.");
        _handlers[type] = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>
    /// Ask for a world change. On the host it is validated and applied right away (queued if called from inside Apply).
    /// On the guest it is sent to the host; the result arrives later as <see cref="Accepted"/> or <see cref="Rejected"/>.
    /// Returns false (no operation) if a guest isn't connected to a world or has too many requests waiting.
    /// </summary>
    public bool Submit(ushort type, byte[] payload, out OperationId op)
    {
        op = default;
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        if (!IsAuthority && (!Linked || !Synchronized || _pending.Count >= MaxPending)) return false;

        op = new OperationId(Local, ++_nextSeq);
        Stats.Submitted++;
        _pending[op.Sequence] = new PendingCommand(type, Generation);

        if (IsAuthority)
        {
            _localQueue.Enqueue((type, payload, op.Sequence, Generation));
            if (!_processing) DrainLocalQueue();
            return true;
        }

        _w.Reset();
        _w.Put((byte)MessageType.Command);
        _w.Put(Generation);
        _w.Put(op.Sequence);
        _w.Put(type);
        Protocol.WriteBlob(_w, payload);
        _send(_w);
        return true;
    }

    /// <summary>Host only: start a new world generation (new world, load, reset). Older requests and changes become stale.</summary>
    public void BeginGeneration(string reason)
    {
        if (!IsAuthority) throw new InvalidOperationException("Only the host starts world generations.");
        if (_processing) throw new InvalidOperationException("Start a world generation outside command handling, not from Apply.");
        StartGeneration(Generation + 1, reason);
        _w.Reset();
        _w.Put((byte)MessageType.WorldReset);
        _w.Put(Generation);
        _w.Put(reason ?? "", Protocol.MaxShortString);
        if (Linked) _send(_w);
    }

    /// <summary>Host: write the current world position for a joining guest (part of Welcome).</summary>
    internal void WriteWorldPosition(NetDataWriter w)
    {
        w.Put(Generation);
        w.Put(Revision);
        w.Put(ChainHash);
    }

    /// <summary>Guest: read the host's world position from Welcome. Informational; the guest enters the world from a snapshot.</summary>
    internal void ReadWorldPosition(NetDataReader r)
    {
        var gen = r.GetUInt();
        var rev = r.GetULong();
        r.GetULong();
        _log?.Invoke(gen == 0 ? "Host has no world loaded yet." : $"Host world is at generation {gen}, revision {rev}; waiting for its snapshot.");
    }

    /// <summary>Host: the current world position and mod state, captured at the moment the game save is taken.</summary>
    public SnapshotPosition CapturePosition()
    {
        if (!IsAuthority) throw new InvalidOperationException("Only the host captures snapshots.");
        if (_processing) throw new InvalidOperationException("Capture a snapshot outside command handling.");
        var w = new NetDataWriter();
        w.Put(_states.Count);
        foreach (var st in _states)
        {
            var sw = new NetDataWriter();
            st.Write(sw);
            w.Put(st.Name, Protocol.MaxShortString);
            Protocol.WriteBlob(w, sw.CopyData());
        }
        var state = w.CopyData();
        if (state.Length > Protocol.MaxModStateBytes) throw new InvalidOperationException($"Mod world state is {state.Length} bytes; the limit is {Protocol.MaxModStateBytes}.");
        return new SnapshotPosition(Generation, Revision, ChainHash, state);
    }

    /// <summary>
    /// Guest: a load snapshot starts at <paramref name="position"/>. From now on world messages are held until
    /// <see cref="Adopt"/> (snapshot loaded) or <see cref="CancelHold"/> (snapshot failed).
    /// </summary>
    internal void BeginHold(SnapshotPosition position)
    {
        if (IsAuthority) throw new InvalidOperationException("Only a guest holds world changes.");
        AbandonPending("loading the host's world");
        _admitted = false;
        Synchronized = false;
        _held.Clear();
        _heldBytes = 0;
        _holdFor = position;
        _log?.Invoke($"Snapshot at generation {position.Generation}, revision {position.Revision}: holding world changes until it is loaded.");
    }

    internal void CancelHold()
    {
        _holdFor = null;
        _held.Clear();
        _heldBytes = 0;
    }

    /// <summary>
    /// Guest: the snapshot's game world is loaded. Adopt its position and mod state, then apply the held changes in
    /// order. Returns false (and stays outside the world) if the mod state can't be read.
    /// </summary>
    public bool Adopt(out string error)
    {
        error = "";
        if (_holdFor is not { } p) { error = "No snapshot is waiting to be adopted."; return false; }
        _holdFor = null;
        try { ReadStates(p.State); }
        catch (Exception e) when (Protocol.IsReadError(e) || e is InvalidDataException)
        {
            error = "The host's world state could not be read: " + e.Message;
            CancelHold();
            return false;
        }
        Generation = p.Generation;
        Revision = p.Revision;
        ChainHash = p.ChainHash;
        Divergence = null;
        Synchronized = true;
        _admitted = true;
        _log?.Invoke($"Entered the host's world at generation {p.Generation}, revision {p.Revision}; applying {_held.Count} held message(s).");
        GenerationStarted?.Invoke(p.Generation, "joined");
        var held = _held.ToList();
        _held.Clear();
        _heldBytes = 0;
        foreach (var (type, body) in held) Handle(type, new NetDataReader(body));
        return true;
    }

    /// <summary>Restore a disk save at the start of a new host generation; its stream starts at revision 0.</summary>
    public void RestoreState(byte[] state)
    {
        if (!IsAuthority || Generation == 0 || Revision != 0 || _processing)
            throw new InvalidOperationException("Restore state only at the start of a host generation.");
        ReadStates(state);
    }

    private void ReadStates(byte[] data)
    {
        var r = new NetDataReader(data);
        var n = r.GetInt();
        if (n < 0 || n > 256) throw new InvalidDataException($"bad state count {n}");
        var seen = new HashSet<string>();
        foreach (var st in _states) st.Reset();
        for (var i = 0; i < n; i++)
        {
            var name = Protocol.ReadString(r);
            var blob = Protocol.ReadBlob(r, Protocol.MaxModStateBytes, out _) ?? throw new InvalidDataException($"bad state blob for '{name}'");
            var st = _states.FirstOrDefault(x => x.Name == name) ?? throw new InvalidDataException($"unknown world state '{name}'");
            if (!seen.Add(name)) throw new InvalidDataException($"world state '{name}' sent twice");
            var sr = new NetDataReader(blob);
            st.Read(sr);
            if (sr.AvailableBytes != 0) throw new InvalidDataException($"trailing state bytes for '{name}'");
        }
        var missing = _states.Where(x => !seen.Contains(x.Name)).Select(x => x.Name).ToList();
        if (missing.Count > 0) throw new InvalidDataException("missing world state: " + string.Join(", ", missing));
        if (r.AvailableBytes != 0) throw new InvalidDataException("Trailing world state bytes.");
    }

    /// <summary>Session ended or the guest dropped: forget per-link state. Host keeps its world generation.</summary>
    internal void OnLinkLost()
    {
        Linked = false;
        if (IsAuthority)
        {
            // A reconnecting guest restarts its sequence numbers at 1.
            _lastSeq[(int)ActorSlot.Guest] = 0;
            ForgetOutcomes(ActorSlot.Guest);
        }
        else
        {
            AbandonPending("disconnected");
            CancelHold();
            Synchronized = false;
            _admitted = false;
        }
    }

    /// <summary>Handle one reliable world message. Returns false if the type isn't a world message.</summary>
    internal bool Handle(MessageType type, NetDataReader r)
    {
        if (_holdFor != null && type is MessageType.Mutation or MessageType.WorldReset or MessageType.CommandResult)
        {
            var body = new byte[r.AvailableBytes];
            r.GetBytes(body, body.Length);
            _held.Add((type, body));
            _heldBytes += body.Length;
            Stats.Held++;
            if (_held.Count > MaxHeldMessages || _heldBytes > MaxHeldBytes)
            {
                CancelHold();
                Diverge("Too many world changes arrived while loading the host's world.");
            }
            return true;
        }
        switch (type)
        {
            case MessageType.Command when IsAuthority: OnCommand(r); return true;
            case MessageType.Mutation when !IsAuthority: OnMutation(r); return true;
            case MessageType.CommandResult when !IsAuthority: OnResult(r); return true;
            case MessageType.WorldReset when !IsAuthority: OnWorldReset(r); return true;
            case MessageType.Command or MessageType.Mutation or MessageType.CommandResult or MessageType.WorldReset:
                Stats.Malformed++;
                _log?.Invoke($"Ignored {type}: not valid for the {Local}.");
                return true;
            default: return false;
        }
    }

    // ---------------- host ----------------

    private void DrainLocalQueue()
    {
        _processing = true;
        try
        {
            while (_localQueue.Count > 0)
            {
                var (type, payload, seq, gen) = _localQueue.Dequeue();
                if (!_pending.ContainsKey(seq)) continue; // abandoned by a generation change while queued
                Process(new OperationId(ActorSlot.Host, seq), gen, type, payload);
            }
        }
        finally { _processing = false; }
    }

    private void OnCommand(NetDataReader r)
    {
        uint gen, seq;
        ushort type;
        byte[] payload;
        try
        {
            gen = r.GetUInt();
            seq = r.GetUInt();
            type = r.GetUShort();
            var blob = Protocol.ReadBlob(r, Protocol.MaxPayloadBytes, out var len);
            if (blob == null)
            {
                Stats.Malformed++;
                SendResult(seq, len > Protocol.MaxPayloadBytes ? RejectReason.TooLarge : RejectReason.Malformed, 0, "Command payload has a bad size.");
                return;
            }
            payload = blob;
        }
        catch (Exception e) when (Protocol.IsReadError(e))
        {
            Stats.Malformed++;
            _log?.Invoke("Dropped a truncated command from the guest.");
            return;
        }

        var op = new OperationId(ActorSlot.Guest, seq);
        if (_processing) throw new InvalidOperationException("Guest command arrived during Apply."); // Poll is never called from Apply
        _processing = true;
        try { Process(op, gen, type, payload); }
        finally { _processing = false; }
        DrainLocalQueue();
    }

    private void Process(OperationId op, uint gen, ushort type, byte[] payload)
    {
        var actor = (int)op.Actor;

        // Duplicates: a sequence at or below the last processed one.
        if (op.Sequence <= _lastSeq[actor])
        {
            Stats.Duplicates++;
            if (_outcomes.TryGetValue(op, out var known))
            {
                _log?.Invoke($"Duplicate {op}: answering with its original outcome.");
                if (op.Actor == ActorSlot.Guest) SendResult(op.Sequence, known.Reason, known.Revision, known.Text);
            }
            else Finish(op, RejectReason.OutOfOrder, 0, "Older than an already processed request.", remember: false);
            return;
        }
        _lastSeq[actor] = op.Sequence;

        if (Generation == 0) { Finish(op, RejectReason.NoWorld, 0, "The host has no world loaded."); return; }
        if (gen != Generation) { Finish(op, RejectReason.StaleGeneration, 0, $"Made for world generation {gen}; current is {Generation}."); return; }
        if (payload.Length > Protocol.MaxPayloadBytes) { Finish(op, RejectReason.TooLarge, 0, "Payload too large."); return; }
        if (!_handlers.TryGetValue(type, out var handler)) { Finish(op, RejectReason.UnknownCommand, 0, $"Unknown command type {type}."); return; }

        CommandVerdict verdict;
        try
        {
            var context = new CommandContext(op, Generation, Revision + 1);
            var request = new NetDataReader(payload);
            verdict = handler.Validate(context, request);
            if (request.AvailableBytes != 0) throw new FormatException("Trailing request bytes.");
            if (verdict.Accepted && handler is ICanonicalCommandHandler canonical)
                payload = canonical.Canonicalize(context, new NetDataReader(payload));
            if (payload == null || payload.Length > Protocol.MaxPayloadBytes)
            {
                Finish(op, RejectReason.TooLarge, 0, "Canonical payload too large.");
                return;
            }
        }
        catch (Exception e) when (Protocol.IsReadError(e))
        {
            Stats.Malformed++;
            Finish(op, RejectReason.Malformed, 0, $"{handler.Name}: could not read the request.");
            return;
        }
        if (!verdict.Accepted) { Finish(op, RejectReason.Invalid, 0, $"{handler.Name}: {verdict.Reason}"); return; }

        // Accept: next revision, apply here, forward.
        var revision = Revision + 1;
        var ctx = new MutationContext(op, Generation, revision, op.Actor == Local);
        Revision = revision;
        ChainHash = Chain(ChainHash, Generation, revision, op, type, payload);
        ApplyHandler(handler, ctx, payload);

        if (Linked)
        {
            _w.Reset();
            _w.Put((byte)MessageType.Mutation);
            _w.Put(Generation);
            _w.Put(revision);
            _w.Put((byte)op.Actor);
            _w.Put(op.Sequence);
            _w.Put(type);
            Protocol.WriteBlob(_w, payload);
            _w.Put(ChainHash);
            _send(_w);
        }
        Finish(op, RejectReason.None, revision, "");
    }

    /// <summary>Record an outcome on the host and tell whoever asked.</summary>
    private void Finish(OperationId op, RejectReason reason, ulong revision, string text, bool remember = true)
    {
        if (reason == RejectReason.None) Stats.Accepted++;
        else
        {
            Stats.Rejected++;
            _log?.Invoke($"Rejected {op}: {reason} - {text}");
        }

        if (remember)
        {
            _outcomes[op] = new Outcome(reason, revision, text);
            _outcomeOrder.Enqueue(op);
            while (_outcomeOrder.Count > OutcomeMemory) _outcomes.Remove(_outcomeOrder.Dequeue());
        }

        if (op.Actor == ActorSlot.Host)
        {
            _pending.Remove(op.Sequence);
            if (reason == RejectReason.None) Accepted?.Invoke(op, revision);
            else Rejected?.Invoke(op, reason, text);
        }
        else if (reason != RejectReason.None)
        {
            SendResult(op.Sequence, reason, 0, text); // accepted guest commands are confirmed by their Mutation
        }
    }

    private void SendResult(uint seq, RejectReason reason, ulong revision, string text)
    {
        if (!Linked) return;
        _w.Reset();
        _w.Put((byte)MessageType.CommandResult);
        _w.Put(seq);
        _w.Put((byte)reason);
        _w.Put(revision);
        _w.Put(text ?? "", Protocol.MaxShortString);
        _send(_w);
    }

    private void ForgetOutcomes(ActorSlot actor)
    {
        var keep = _outcomeOrder.Where(o => o.Actor != actor).ToList();
        foreach (var o in _outcomeOrder) if (o.Actor == actor) _outcomes.Remove(o);
        _outcomeOrder.Clear();
        foreach (var o in keep) _outcomeOrder.Enqueue(o);
    }

    // ---------------- guest ----------------

    private void OnWorldReset(NetDataReader r)
    {
        var gen = r.GetUInt();
        var reason = Protocol.ReadString(r);
        if (gen <= Generation)
        {
            Stats.StaleDropped++;
            _log?.Invoke($"Ignored world reset to generation {gen} (already at {Generation}).");
            return;
        }
        var wasAdmitted = _admitted;
        StartGeneration(gen, reason);
        // The guest's game still shows the old world. It needs a new snapshot before it is in the world again.
        _admitted = false;
        Synchronized = false;
        if (wasAdmitted) WorldReplaced?.Invoke(reason);
    }

    private void OnMutation(NetDataReader r)
    {
        uint gen, seq;
        ulong revision, hostHash;
        ushort type;
        ActorSlot actor;
        byte[] payload;
        try
        {
            gen = r.GetUInt();
            revision = r.GetULong();
            actor = (ActorSlot)r.GetByte();
            seq = r.GetUInt();
            type = r.GetUShort();
            payload = Protocol.ReadBlob(r, Protocol.MaxPayloadBytes, out _) ?? throw new FormatException("bad payload length");
            hostHash = r.GetULong();
        }
        catch (Exception e) when (Protocol.IsReadError(e))
        {
            Stats.Malformed++;
            Diverge("Received an unreadable world change.");
            return;
        }

        if (!_admitted || gen != Generation) { Stats.StaleDropped++; return; }
        if (Divergence != null) return; // wait for a new generation (resync is phase 2)
        if (revision <= Revision) { Stats.StaleDropped++; return; } // already part of the snapshot
        if (revision != Revision + 1) { Diverge($"Missing world changes: expected revision {Revision + 1}, got {revision}."); return; }
        if (actor is not (ActorSlot.Host or ActorSlot.Guest)) { Diverge($"World change from unknown actor {(byte)actor}."); return; }
        if (!_handlers.TryGetValue(type, out var handler)) { Diverge($"World change of unknown type {type}; cannot apply."); return; }

        var op = new OperationId(actor, seq);
        Revision = revision;
        ChainHash = Chain(ChainHash, gen, revision, op, type, payload);
        ApplyHandler(handler, new MutationContext(op, gen, revision, actor == Local), payload);
        if (ChainHash != hostHash) Diverge($"World change hash mismatch at revision {revision}.");

        if (actor == Local)
        {
            if (_pending.Remove(seq)) { Stats.Accepted++; Accepted?.Invoke(op, revision); }
            else Stats.UnexpectedResults++;
        }
    }

    private void OnResult(NetDataReader r)
    {
        var seq = r.GetUInt();
        var reason = (RejectReason)r.GetByte();
        var revision = r.GetULong();
        var text = Protocol.ReadString(r);
        var op = new OperationId(Local, seq);
        if (!_pending.Remove(seq)) { Stats.UnexpectedResults++; return; }
        if (reason == RejectReason.None) { Stats.Accepted++; Accepted?.Invoke(op, revision); }
        else { Stats.Rejected++; Rejected?.Invoke(op, reason, text); }
    }

    // ---------------- shared ----------------

    private void StartGeneration(uint gen, string reason)
    {
        Generation = gen;
        Revision = 0;
        ChainHash = ChainSeed;
        Divergence = null;
        Synchronized = true;
        foreach (var st in _states) st.Reset();
        // Requests made for the old world can't succeed any more. The host also rejects them if they're in flight.
        AbandonPending($"world generation changed to {gen}");
        _log?.Invoke($"World generation {gen} started ({reason}).");
        GenerationStarted?.Invoke(gen, reason);
    }

    private void AbandonPending(string why)
    {
        if (_pending.Count == 0) return;
        var seqs = _pending.Keys.OrderBy(s => s).ToList();
        _pending.Clear();
        foreach (var s in seqs) { Stats.Rejected++; Rejected?.Invoke(new OperationId(Local, s), RejectReason.Abandoned, why); }
    }

    private void ApplyHandler(ICommandHandler handler, in MutationContext ctx, byte[] payload)
    {
        try
        {
            handler.Apply(ctx, new NetDataReader(payload));
            Stats.Applied++;
        }
        catch (Exception e)
        {
            Diverge($"{handler.Name} failed to apply {ctx.Op} at revision {ctx.Revision}: {e.Message}");
        }
    }

    private void Diverge(string why)
    {
        if (Divergence != null) return;
        Divergence = why;
        _log?.Invoke("World diverged: " + why);
        Diverged?.Invoke(why);
    }

    private const ulong ChainSeed = 14695981039346656037UL;

    /// <summary>FNV-1a over the previous hash and the change. Same input stream gives the same hash on both peers.</summary>
    internal static ulong Chain(ulong prev, uint gen, ulong revision, OperationId op, ushort type, byte[] payload)
    {
        var h = prev;
        void Mix(ulong v, int bytes) { for (var i = 0; i < bytes; i++) { h ^= (byte)(v >> (8 * i)); h *= 1099511628211UL; } }
        Mix(gen, 4);
        Mix(revision, 8);
        Mix((byte)op.Actor, 1);
        Mix(op.Sequence, 4);
        Mix(type, 2);
        Mix((ulong)payload.Length, 4);
        foreach (var b in payload) { h ^= b; h *= 1099511628211UL; }
        return h;
    }

    private readonly record struct Outcome(RejectReason Reason, ulong Revision, string Text);
    private readonly record struct PendingCommand(ushort Type, uint Generation);
}

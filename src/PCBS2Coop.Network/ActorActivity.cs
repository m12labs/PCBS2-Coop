using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

public enum ActivityOperation : byte { Edit = 1, Move = 2 }
public sealed record ActivityClaim(OperationId Token, ActivityOperation Kind, NetObjectId Pc, IReadOnlyList<NetObjectId> Targets);
public sealed record ActorActivity(ActorSlot Actor, PlayerId Player, ulong Epoch, ulong Version, NetObjectId WorkingPc, ActivityClaim? Pending);

/// <summary>Session activity, separate from committed object locations. Work is shared; resource claims are
/// exclusive. A movement claim conflicts with every worker/claim on its PC, including its requester.
/// Callers claim all affected resources together (including dependencies) before committing a later command.</summary>
public sealed class ActivityWorld : IWorldState, ICanonicalCommandHandler
{
    private enum Action : byte { Presence = 1, Work = 2, Reserve = 3, Release = 4 }
    private readonly ObjectWorld _objects;
    private readonly Dictionary<ActorSlot, ActorActivity> _actors = new();
    public ActivityWorld(ObjectWorld objects) => _objects = objects;
    public string Name => "activity";
    public ulong NextEpoch { get; private set; } = 1;
    public IReadOnlyCollection<ActorActivity> Actors => _actors.Values;
    public ActorActivity? Get(ActorSlot actor) => _actors.GetValueOrDefault(actor);
    public NetObjectId Carried(ActorSlot actor) => _objects.Pcs.SingleOrDefault(x => x.Location.Place == PcPlace.Carrier &&
        x.Location.Actor == actor && Get(actor)?.Player == x.Location.Player)?.Id ?? default;
    public void Reset() { _actors.Clear(); NextEpoch = 1; }

    public static byte[] Presence(ActorSlot seat, PlayerId player) => Encode(w => { w.Put((byte)Action.Presence); w.Put((byte)seat); player.Write(w); });
    public static byte[] Work(ActorActivity actor, NetObjectId pc) => Encode(w => { Header(w, Action.Work, actor); pc.Write(w); });
    public static byte[] Reserve(ActorActivity actor, NetObjectId pc, ActivityOperation kind, params NetObjectId[] targets) => Encode(w =>
    { Header(w, Action.Reserve, actor); pc.Write(w); w.Put((byte)kind); w.Put(targets.Length); foreach (var t in targets) t.Write(w); });
    public static byte[] Release(ActorActivity actor, OperationId token) => Encode(w =>
    { Header(w, Action.Release, actor); w.Put((byte)token.Actor); w.Put(token.Sequence); });
    private static void Header(NetDataWriter w, Action action, ActorActivity actor) { w.Put((byte)action); w.Put(actor.Epoch); w.Put(actor.Version); }
    private static byte[] Encode(System.Action<NetDataWriter> write) { var w = new NetDataWriter(); write(w); return w.CopyData(); }

    public string? MovementBlock(NetObjectId pc)
    {
        if (!_objects.Pcs.Any(x => x.Id == pc)) return "PC is unknown or retired.";
        return _actors.Values.Any(x => x.WorkingPc == pc || x.Pending?.Pc == pc) ? "PC is in use or has a pending operation." : null;
    }
    /// <summary>Revalidate immediately before a future commit. The acquisition token and epoch must still own
    /// exactly the requested resources; a cancellation/reconnect cannot authorize a delayed completion.</summary>
    public bool Owns(ActorSlot actor, ulong epoch, OperationId token, NetObjectId pc, ActivityOperation kind, params NetObjectId[] targets) =>
        Get(actor) is { } a && a.Epoch == epoch && a.Pending is { } p && p.Token == token && p.Pc == pc && p.Kind == kind &&
        p.Targets.SequenceEqual(targets.OrderBy(x => x.Value)) && _objects.Pcs.SingleOrDefault(x => x.Id == pc) is { } current &&
        (kind == ActivityOperation.Move || (a.WorkingPc == pc && current.Location.Place == PcPlace.Bench && targets.All(t => current.Parts.Any(part => part.Id == t))));

    private sealed record Request(Action Action, ActorSlot Seat, PlayerId Player, ulong Epoch, ulong Version,
        NetObjectId Pc, ActivityOperation Kind, NetObjectId[] Targets, OperationId Token);
    private static Request Parse(NetDataReader r, bool canonical)
    {
        var action = (Action)r.GetByte();
        if (action == Action.Presence)
        {
            var seat = (ActorSlot)r.GetByte(); var player = PlayerId.Read(r); var epoch = canonical ? r.GetULong() : 0;
            return new(action, seat, player, epoch, 0, default, default, Array.Empty<NetObjectId>(), default);
        }
        var e = r.GetULong(); var v = r.GetULong();
        if (action == Action.Work) return new(action, default, default, e, v, NetObjectId.Read(r), default, Array.Empty<NetObjectId>(), default);
        if (action == Action.Release) return new(action, default, default, e, v, default, default, Array.Empty<NetObjectId>(), new((ActorSlot)r.GetByte(), r.GetUInt()));
        if (action != Action.Reserve) throw new FormatException("Unknown activity action.");
        var pc = NetObjectId.Read(r); var kind = (ActivityOperation)r.GetByte(); var count = r.GetInt();
        if (count is < 0 or > 32) throw new FormatException("Invalid resource count.");
        var targets = new NetObjectId[count]; for (var i = 0; i < count; i++) targets[i] = NetObjectId.Read(r);
        return new(action, default, default, e, v, pc, kind, targets, default);
    }
    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var r = Parse(payload, false);
        string? error = null;
        if (r.Action == Action.Presence)
        {
            if (ctx.Op.Actor != ActorSlot.Host || r.Seat is not (ActorSlot.Host or ActorSlot.Guest)) error = "Only the host changes actor presence.";
            else if (NextEpoch == ulong.MaxValue) error = "Actor epoch exhausted.";
        }
        else if (Get(ctx.Op.Actor) is not { } a || a.Epoch != r.Epoch || a.Version != r.Version || a.Version == ulong.MaxValue)
            error = "Actor presence/activity changed.";
        else if (r.Action == Action.Work)
        {
            if (a.Pending != null) error = "Release the pending operation first.";
            else if (!r.Pc.IsNone && (_objects.Pcs.SingleOrDefault(x => x.Id == r.Pc) is not { Location.Place: PcPlace.Bench } ||
                _actors.Values.Any(x => x.Pending is { Kind: ActivityOperation.Move } p && p.Pc == r.Pc))) error = "PC is unavailable for work.";
        }
        else if (r.Action == Action.Release)
        { if (a.Pending?.Token != r.Token) error = "Operation no longer owned."; }
        else
        {
            var pc = _objects.Pcs.SingleOrDefault(x => x.Id == r.Pc);
            if (pc == null || a.Pending != null) error = "PC unavailable or actor already has an operation.";
            else if (r.Kind == ActivityOperation.Move)
            {
                error = r.Targets.Length != 0 ? "Movement claims have no part targets." : MovementBlock(r.Pc);
                if (pc.Location.Place == PcPlace.Carrier && (pc.Location.Actor != a.Actor || pc.Location.Player != a.Player)) error = "PC belongs to another carrier.";
            }
            else if (r.Kind != ActivityOperation.Edit || a.WorkingPc != pc.Id || r.Targets.Length == 0 ||
                r.Targets.Distinct().Count() != r.Targets.Length || r.Targets.Any(t => !pc.Parts.Any(p => p.Id == t))) error = "Invalid edit resources or actor is not working on this PC.";
            else if (_actors.Values.Any(x => x.Pending is { } p && p.Pc == pc.Id &&
                (p.Kind == ActivityOperation.Move || p.Targets.Intersect(r.Targets).Any()))) error = "Resource already in use.";
        }
        return error == null ? CommandVerdict.Accept : CommandVerdict.Reject(error);
    }
    public byte[] Canonicalize(in CommandContext ctx, NetDataReader request)
    {
        var bytes = request.RawData.AsSpan(request.Position, request.AvailableBytes).ToArray();
        var r = Parse(request, false);
        return r.Action == Action.Presence ? bytes.Concat(BitConverter.GetBytes(NextEpoch)).ToArray() : bytes;
    }
    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var r = Parse(payload, true);
        if (r.Action == Action.Presence)
        {
            if (ctx.Op.Actor != ActorSlot.Host || r.Epoch != NextEpoch) throw new InvalidDataException("Invalid actor epoch.");
            NextEpoch++;
            if (r.Player.IsEmpty) _actors.Remove(r.Seat);
            else _actors[r.Seat] = new(r.Seat, r.Player, r.Epoch, 0, default, null);
        }
        else
        {
            var a = _actors[ctx.Op.Actor];
            _actors[a.Actor] = r.Action switch
            {
                Action.Work => a with { Version = a.Version + 1, WorkingPc = r.Pc },
                Action.Reserve => a with { Version = a.Version + 1, Pending = new(ctx.Op, r.Kind, r.Pc, Array.AsReadOnly(r.Targets.OrderBy(x => x.Value).ToArray())) },
                Action.Release => a with { Version = a.Version + 1, Pending = null },
                _ => throw new InvalidDataException("Invalid activity mutation.")
            };
        }
    }
    public void Write(NetDataWriter w)
    {
        w.Put(NextEpoch); w.Put(_actors.Count);
        foreach (var a in _actors.Values.OrderBy(x => x.Actor))
        {
            w.Put((byte)a.Actor); a.Player.Write(w); w.Put(a.Epoch); w.Put(a.Version); a.WorkingPc.Write(w);
            w.Put(a.Pending != null);
            if (a.Pending is not { } p) continue;
            w.Put((byte)p.Token.Actor); w.Put(p.Token.Sequence); w.Put((byte)p.Kind); p.Pc.Write(w);
            w.Put(p.Targets.Count); foreach (var t in p.Targets) t.Write(w);
        }
    }
    public void Read(NetDataReader r)
    {
        // Build through the same command validators to check cross-resource conflicts and object membership.
        var candidate = new ActivityWorld(_objects); var next = r.GetULong(); var count = r.GetInt();
        if (next == 0 || count is < 0 or > 2) throw new InvalidDataException("Invalid actor state.");
        var rows = new List<ActorActivity>();
        for (var i = 0; i < count; i++)
        {
            var seat = (ActorSlot)r.GetByte(); var player = PlayerId.Read(r); var epoch = r.GetULong(); var version = r.GetULong(); var pc = NetObjectId.Read(r);
            if (seat is not (ActorSlot.Host or ActorSlot.Guest) || player.IsEmpty || epoch == 0 || epoch >= next || rows.Any(x => x.Actor == seat || x.Epoch == epoch)) throw new InvalidDataException("Invalid actor binding.");
            ActivityClaim? pending = null;
            if (r.GetBool())
            {
                var token = new OperationId((ActorSlot)r.GetByte(), r.GetUInt()); var kind = (ActivityOperation)r.GetByte(); var claimPc = NetObjectId.Read(r); var n = r.GetInt();
                if (n is < 0 or > 32 || token.Actor != seat || token.Sequence == 0) throw new InvalidDataException("Invalid operation binding.");
                var targets = new NetObjectId[n]; for (var t = 0; t < n; t++) targets[t] = NetObjectId.Read(r);
                pending = new(token, kind, claimPc, Array.AsReadOnly(targets));
            }
            rows.Add(new(seat, player, epoch, version, pc, pending));
        }
        if (r.AvailableBytes != 0) throw new InvalidDataException("Trailing actor state.");
        foreach (var a in rows)
        {
            candidate._actors.Add(a.Actor, a with { WorkingPc = default, Pending = null });
            var verdict = candidate.Validate(new(new(a.Actor, 1), 1, 1), new(Work(candidate.Get(a.Actor)!, a.WorkingPc)));
            if (!verdict.Accepted) throw new InvalidDataException(verdict.Reason);
            candidate._actors[a.Actor] = a with { Pending = null };
        }
        foreach (var a in rows.Where(x => x.Pending != null))
        {
            var p = a.Pending!; var verdict = candidate.Validate(new(p.Token, 1, 1), new(Reserve(candidate.Get(a.Actor)!, p.Pc, p.Kind, p.Targets.ToArray())));
            if (!verdict.Accepted || !p.Targets.SequenceEqual(p.Targets.OrderBy(x => x.Value))) throw new InvalidDataException("Invalid/conflicting operation state.");
            candidate._actors[a.Actor] = a;
        }
        _actors.Clear(); foreach (var a in rows) _actors.Add(a.Actor, a); NextEpoch = next;
    }
}

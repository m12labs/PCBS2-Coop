using System.IO.Compression;
using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>How the editor's game started the edit (4e decompile): an install (<c>AssemblyTick</c>, or clicking a
/// placeholder), a removal through <c>WorkingOnComputerState.Uninstall</c> (cables unplug first), or the direct removal
/// that skips the cable step.</summary>
public enum EditGesture : byte { Install = 1, Remove = 2, RemoveDirect = 3 }
/// <summary><see cref="Unplugged"/>: the editor pressed Esc during a removal's cable step, so only the cables came off
/// and the part stayed in (4b N2; owner decision: share whatever the editor's game ends with).</summary>
public enum EditOutcome : byte { Done = 1, Unplugged = 2 }
public enum EditStage : byte { Running = 1, Committed = 2 }

/// <summary>What the editor's game ended with, so the other game replays the same thing: the orientation-step angle
/// (NaN without one), the part's airflow flip, the editor's work tool at settlement (it decides automatic cabling),
/// whether an installed part came out of the editor's inventory (rather than the Free Build catalog) and the editor's
/// cable choice (a global game setting the settlement reads).</summary>
public sealed record EditResult(EditOutcome Outcome, float Degrees, bool Flipped, byte Mode, byte Selected, bool FromInventory,
    string CableType, string CableConnector, IReadOnlyList<int> Pins)
{
    public const int MaxPins = 64;
    public static EditResult Done(float degrees = float.NaN) => new(EditOutcome.Done, degrees, false, 1, 1, false, "", "", Array.Empty<int>());
}

/// <summary>One component edit in progress. <see cref="Part"/> is the installed SKU; <see cref="Component"/> the
/// removed part (none for an integral cover). <see cref="Targets"/> is the claim the edit holds.</summary>
public sealed record ComponentEdit(OperationId Token, ActorSlot Actor, ulong Epoch, NetObjectId Pc, NetObjectId Slot,
    EditGesture Gesture, string Part, NetObjectId Component, IReadOnlyList<NetObjectId> Targets)
{
    public bool Install => Gesture == EditGesture.Install;
    public EditStage Stage { get; init; } = EditStage.Running;
    public EditResult? Result { get; init; }
}

/// <summary>Native side of component edits. CheckHost and Capture run on the host and only read. Began, Committed,
/// Cancelled and Settled run after the model changed (both peers, in host order) and must not submit commands.</summary>
public interface IEditNative
{
    string? CheckHost(in CommandContext ctx, PcFact pc, AssemblyResource slot, ComponentEdit edit);
    /// <summary>Host: the PC as its game shows it now. Unchanged parts and resources keep their IDs; new ones come from
    /// <paramref name="allocator"/>.</summary>
    PcFact Capture(PcFact pc, ComponentEdit edit, NetObjectIdAllocator allocator);
    void Began(in MutationContext ctx, PcFact pc, ComponentEdit edit);
    void Committed(in MutationContext ctx, PcFact pc, ComponentEdit edit);
    void Cancelled(in MutationContext ctx, ComponentEdit edit);
    void Settled(in MutationContext ctx, PcFact pc, ComponentEdit edit);
}

/// <summary>
/// Host-ordered component installs and removals (task 4e, owner decisions October 27, 2026): motherboards, CPUs, RAM,
/// cards, drives, PSUs, coolers, fans and the other standard parts, with their automatic screws, standoffs and cables.
/// <list type="number">
/// <item><b>Begin</b> (editor): the host checks the slot and its own game, then claims the PC for the editor. While an
/// edit runs, the other player's edits, latch clicks and panel changes on that PC wait (they are refused).</item>
/// <item>The editor's game runs the game's own edit (animation, screws, orientation, cables, inventory).</item>
/// <item><b>Commit</b> (editor): what its game ended with. The other game replays the same edit with the game's
/// own animations.</item>
/// <item><b>Settle</b> (host, once its game shows the result): the PC's new parts, links, resources and assembly
/// profile, captured from the host's game, and the claim's release. Unchanged parts and resources keep their IDs.</item>
/// </list>
/// <b>Cancel</b> drops an edit that never ran (editor) or can't be settled (host). Not in snapshots: saves and snapshots
/// wait while an edit is in progress.
/// </summary>
public sealed class ComponentEditWorld : ICanonicalCommandHandler
{
    private enum Step : byte { Begin = 1, Commit = 2, Cancel = 3, Settle = 4 }
    public const int MaxText = 255;
    private readonly ObjectWorld _objects;
    private readonly ActivityWorld _activity;
    private readonly IEditNative _native;
    private readonly List<ComponentEdit> _edits = new();
    private (OperationId Op, byte[] Payload)? _settling;
    public ComponentEditWorld(ObjectWorld objects, ActivityWorld activity, IEditNative native)
    { _objects = objects; _activity = activity; _native = native; }
    public string Name => "component-edit";
    public IReadOnlyList<ComponentEdit> Edits => _edits;
    public ComponentEdit? Find(OperationId token) => _edits.SingleOrDefault(e => e.Token == token);
    /// <summary>The edit running on that PC, if any (one per PC: the other player waits).</summary>
    public ComponentEdit? On(NetObjectId pc) => _edits.SingleOrDefault(e => e.Pc == pc);
    /// <summary>World generation start: edits never outlive their world.</summary>
    public void Reset() { _edits.Clear(); _settling = null; }

    public static byte[] Begin(ulong epoch, NetObjectId pc, NetObjectId slot, ulong version, EditGesture gesture, string part, NetObjectId component) =>
        Encode(w => { w.Put((byte)Step.Begin); w.Put(epoch); pc.Write(w); slot.Write(w); w.Put(version); w.Put((byte)gesture); w.Put(part ?? ""); component.Write(w); });
    public static byte[] Commit(ulong epoch, OperationId token, EditResult result) => Encode(w =>
    {
        w.Put((byte)Step.Commit); w.Put(epoch); Token(w, token);
        w.Put((byte)result.Outcome); w.Put(result.Degrees); w.Put(result.Flipped); w.Put(result.Mode); w.Put(result.Selected); w.Put(result.FromInventory);
        w.Put(result.CableType ?? ""); w.Put(result.CableConnector ?? ""); w.Put(result.Pins.Count); foreach (var p in result.Pins) w.Put(p);
    });
    public static byte[] Cancel(ulong epoch, OperationId token) => Encode(w => { w.Put((byte)Step.Cancel); w.Put(epoch); Token(w, token); });
    /// <summary>Host only: publish the PC as the host's game shows it after that edit.</summary>
    public static byte[] Settle(OperationId token) => Encode(w => { w.Put((byte)Step.Settle); Token(w, token); });

    private static byte[] Encode(Action<NetDataWriter> write) { var w = new NetDataWriter(); write(w); return w.CopyData(); }
    private static void Token(NetDataWriter w, OperationId token) { w.Put((byte)token.Actor); w.Put(token.Sequence); }
    private static OperationId Token(NetDataReader r) => new((ActorSlot)r.GetByte(), r.GetUInt());

    private sealed record Request(Step Step, ulong Epoch, NetObjectId Pc, NetObjectId Slot, ulong Version, EditGesture Gesture, string Part,
        NetObjectId Component, OperationId Token, EditResult? Result)
    {
        public string Stamp { get; init; } = "";
        public byte[] Settled { get; init; } = Array.Empty<byte>();
    }

    private static Request Parse(NetDataReader r, bool canonical)
    {
        var step = (Step)r.GetByte();
        Request c;
        switch (step)
        {
            case Step.Begin:
                c = new(step, r.GetULong(), NetObjectId.Read(r), NetObjectId.Read(r), r.GetULong(), (EditGesture)r.GetByte(), r.GetString(1024), NetObjectId.Read(r), default, null);
                if (canonical) c = c with { Stamp = r.GetString(256) };
                break;
            case Step.Commit:
            {
                var epoch = r.GetULong(); var token = Token(r);
                var outcome = (EditOutcome)r.GetByte(); var degrees = r.GetFloat(); var flipped = r.GetBool(); var mode = r.GetByte(); var selected = r.GetByte();
                var inventory = r.GetBool(); var type = r.GetString(1024); var connector = r.GetString(1024); var n = r.GetInt();
                if (n is < 0 or > EditResult.MaxPins) throw new FormatException("Invalid cable pin count.");
                var pins = new int[n]; for (var i = 0; i < n; i++) pins[i] = r.GetInt();
                c = new(step, epoch, default, default, 0, default, "", default, token, new(outcome, degrees, flipped, mode, selected, inventory, type, connector, pins));
                break;
            }
            case Step.Cancel:
                c = new(step, r.GetULong(), default, default, 0, default, "", default, Token(r), null);
                break;
            case Step.Settle:
                c = new(step, 0, default, default, 0, default, "", default, Token(r), null);
                if (canonical) c = c with { Settled = r.GetRemainingBytes() };
                break;
            default: throw new FormatException("Unknown component-edit action.");
        }
        if (r.AvailableBytes != 0) throw new FormatException("Trailing component-edit bytes.");
        return c;
    }

    private static bool Text(string s) => s.Length <= MaxText && !s.Any(char.IsControl);

    // ---------------- checks ----------------

    private string? CheckBegin(ActorSlot requester, Request c, out PcFact? pc, out AssemblyResource? slot, out NetObjectId[] targets)
    {
        pc = null; slot = null; targets = Array.Empty<NetObjectId>();
        if (_objects.WorldId == Guid.Empty) return "Objects are not bound yet.";
        if (_activity.Get(requester) is not { } actor || actor.Epoch != c.Epoch) return "Actor presence changed.";
        pc = _objects.Pcs.SingleOrDefault(x => x.Id == c.Pc);
        if (pc == null || pc.Location.Place != PcPlace.Bench) return "That PC is no longer on a bench.";
        if (actor.WorkingPc != pc.Id) return "You're not working on that PC.";
        if (pc.Resources.Count == 0) return "That PC's parts aren't ready in the shared world yet.";
        if (!Enum.IsDefined(c.Gesture)) return "Unknown edit.";
        if (actor.Pending != null || _edits.Any(e => e.Actor == requester)) return "Your previous change is still finishing.";
        if (Busy(pc.Id, requester) is { } busy) return busy;
        slot = pc.Resources.SingleOrDefault(r => r.Id == c.Slot);
        if (slot == null || slot.Kind != AssemblyResourceKind.Slot) return "Unknown slot.";
        if (AssemblyResources.Dormant(pc).Contains(slot.Id)) return "That slot is on a removed panel.";
        if (slot.IntegralHome != "") return "Case panels are changed separately.";
        if (slot.Version != c.Version) return "That slot was just changed.";
        var integral = slot.IntegralPart != "";
        if (c.Gesture == EditGesture.Install)
        {
            if (c.Part.Length == 0 || !Text(c.Part) || !c.Component.IsNone) return "Invalid part.";
            if (!slot.Occupant.IsNone || integral) return "That slot was just filled.";
            targets = new[] { slot.Id };
        }
        else
        {
            if (c.Part.Length != 0) return "Invalid removal.";
            if (integral ? !c.Component.IsNone : c.Component.IsNone || slot.Occupant != c.Component) return "That part was just changed.";
            targets = integral ? new[] { slot.Id } : new[] { slot.Id, c.Component };
        }
        try { AssemblyResources.Plan(pc, targets); }
        catch (InvalidDataException e) { return e.Message; }
        return null;
    }

    /// <summary>Why the other player can't change that PC now, or null. Used by latches and panels too.</summary>
    public string? Busy(NetObjectId pc, ActorSlot requester)
    {
        if (_edits.Any(e => e.Pc == pc && e.Actor != requester)) return "The other player is changing a part of that PC.";
        foreach (var other in _activity.Actors)
            if (other.Pending is { } p && p.Pc == pc)
            {
                if (p.Kind == ActivityOperation.Move) return "That PC is being moved.";
                if (other.Actor != requester) return "The other player is changing a part of that PC.";
            }
        return null;
    }

    private string? CheckResult(ComponentEdit edit, EditResult r)
    {
        if (r.Outcome == EditOutcome.Unplugged ? edit.Gesture != EditGesture.Remove : r.Outcome != EditOutcome.Done) return "Invalid edit outcome.";
        if (!float.IsNaN(r.Degrees) && (!float.IsFinite(r.Degrees) || MathF.Abs(r.Degrees) > 3600)) return "Invalid orientation.";
        if (r.Mode > 8 || r.Selected > 8 || !Text(r.CableType) || !Text(r.CableConnector) || r.Pins.Count > EditResult.MaxPins) return "Invalid edit values.";
        return null;
    }

    private (ComponentEdit? Edit, string? Error) Owned(ActorSlot requester, Request c, EditStage? stage)
    {
        var edit = Find(c.Token);
        if (edit == null || (requester != ActorSlot.Host && edit.Actor != requester)) return (null, "That change is no longer running.");
        if (stage != null && edit.Stage != stage) return (null, "That change already finished.");
        if (requester == edit.Actor && c.Step != Step.Settle && (_activity.Get(requester)?.Epoch != c.Epoch ||
            !_activity.Owns(requester, edit.Epoch, edit.Token, edit.Pc, ActivityOperation.Edit, edit.Targets.ToArray())))
            return (null, "That change no longer owns its parts.");
        return (edit, null);
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var c = Parse(payload, false);
        string? error;
        switch (c.Step)
        {
            case Step.Begin:
                error = CheckBegin(ctx.Op.Actor, c, out var pc, out var slot, out var targets);
                if (error == null)
                {
                    try { error = _native.CheckHost(ctx, pc!, slot!, new(ctx.Op, ctx.Op.Actor, c.Epoch, pc!.Id, slot!.Id, c.Gesture, c.Part, c.Component, targets)); }
                    catch (Exception e) { error = "Host check failed: " + e.Message; }
                }
                break;
            case Step.Commit:
            {
                var (edit, why) = Owned(ctx.Op.Actor, c, EditStage.Running);
                error = why ?? (ctx.Op.Actor != edit!.Actor ? "Only the editor commits its change." : CheckResult(edit, c.Result!));
                break;
            }
            case Step.Cancel:
            {
                // The editor cancels an edit that never ran; the host drops one it can't settle.
                var (edit, why) = Owned(ctx.Op.Actor, c, ctx.Op.Actor == ActorSlot.Host ? null : EditStage.Running);
                error = why;
                break;
            }
            case Step.Settle:
                error = ctx.Op.Actor != ActorSlot.Host ? "Only the host settles component edits." : null;
                if (error == null)
                {
                    try { _settling = (ctx.Op, Settled(c.Token)); }
                    catch (InvalidDataException e) { error = e.Message; }
                }
                break;
            default: error = "Unknown component-edit action."; break;
        }
        return error == null ? CommandVerdict.Accept : CommandVerdict.Reject(error);
    }

    /// <summary>Host: capture the edited PC from its game and encode it (compressed; expansion is capped on decode).</summary>
    private byte[] Settled(OperationId token)
    {
        var edit = Find(token) ?? throw new InvalidDataException("That change is no longer running.");
        var pc = _objects.Pcs.SingleOrDefault(p => p.Id == edit.Pc) ?? throw new InvalidDataException("That PC is gone.");
        var allocator = new NetObjectIdAllocator { Next = _objects.NextId };
        var next = _native.Capture(pc, edit, allocator);
        if (next.Id != pc.Id || next.Location != pc.Location || next.Provenance != pc.Provenance) throw new InvalidDataException("The settled PC changed identity or place.");
        var live = AssemblyResources.AllIds(next).ToHashSet();
        var retired = _objects.Retired.Concat(AssemblyResources.AllIds(pc).Where(id => !live.Contains(id))).ToArray();
        // Same checks as Apply's publish, before anything is accepted.
        _objects.CheckReplace(_objects.WorldId, allocator.Next, _objects.Locations, _objects.Pcs.Select(p => p.Id == pc.Id ? next : p), retired);
        var raw = new NetDataWriter(); edit.Pc.Write(raw); raw.Put(_objects.NextId); raw.Put(allocator.Next);
        var gone = retired.Except(_objects.Retired).ToArray(); raw.Put(gone.Length); foreach (var id in gone) id.Write(raw);
        ObjectWorld.WritePc(raw, next);
        if (raw.Length > Protocol.MaxModStateBytes) throw new InvalidDataException("The settled PC exceeds the expanded state limit.");
        using var output = new MemoryStream();
        using (var zip = new DeflateStream(output, CompressionLevel.Fastest, true)) zip.Write(raw.CopyData());
        if (output.Length > Protocol.MaxPayloadBytes - 64) throw new InvalidDataException("The settled PC is too large to send.");
        return output.ToArray();
    }

    public byte[] Canonicalize(in CommandContext ctx, NetDataReader request)
    {
        var bytes = request.RawData.AsSpan(request.Position, request.AvailableBytes).ToArray();
        var c = Parse(request, false);
        if (c.Step == Step.Begin)
        {
            CheckBegin(ctx.Op.Actor, c, out var pc, out _, out var targets);
            return bytes.Concat(Encode(w => w.Put(_activity.EditStamp(pc!, targets)))).ToArray();
        }
        if (c.Step == Step.Settle)
        {
            if (_settling is not { } s || s.Op != ctx.Op) throw new InvalidDataException("The settled PC was not captured.");
            _settling = null;
            return bytes.Concat(s.Payload).ToArray();
        }
        return bytes;
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var c = Parse(payload, true);
        switch (c.Step)
        {
            case Step.Begin:
            {
                // The same checks against this peer's copy of the same revision. A difference is a divergence.
                if (CheckBegin(ctx.Op.Actor, c, out var pc, out var slot, out var targets) is { } error) throw new InvalidDataException("Component edit no longer applies: " + error);
                if (c.Stamp != _activity.EditStamp(pc!, targets)) throw new InvalidDataException("Component edit binding differs from the host's.");
                var edit = new ComponentEdit(ctx.Op, ctx.Op.Actor, c.Epoch, pc!.Id, slot!.Id, c.Gesture, c.Part, c.Component,
                    Array.AsReadOnly(targets));
                _activity.SetEditClaim(ctx.Op, ctx.Op.Actor, pc, targets);
                _edits.Add(edit);
                _native.Began(ctx, pc, edit);
                break;
            }
            case Step.Commit:
            {
                var (edit, why) = Owned(ctx.Op.Actor, c, EditStage.Running);
                if (why != null || edit!.Actor != ctx.Op.Actor || CheckResult(edit, c.Result!) is { } bad) throw new InvalidDataException("Component commit no longer applies: " + (why ?? "invalid result"));
                var committed = edit with { Stage = EditStage.Committed, Result = c.Result };
                _edits[_edits.IndexOf(edit)] = committed;
                _native.Committed(ctx, _objects.Pcs.Single(p => p.Id == edit.Pc), committed);
                break;
            }
            case Step.Cancel:
            {
                var (edit, why) = Owned(ctx.Op.Actor, c, ctx.Op.Actor == ActorSlot.Host ? null : EditStage.Running);
                if (why != null) throw new InvalidDataException("Component cancel no longer applies: " + why);
                _edits.Remove(edit!);
                _activity.ClearClaim(edit!.Actor, edit.Token);
                _native.Cancelled(ctx, edit);
                break;
            }
            case Step.Settle:
            {
                if (ctx.Op.Actor != ActorSlot.Host) throw new InvalidDataException("Component settle from a guest.");
                var edit = Find(c.Token) ?? throw new InvalidDataException("Settled component edit is unknown.");
                var next = Expand(c.Settled, out var first, out var allocated, out var gone);
                var pc = _objects.Pcs.Single(p => p.Id == edit.Pc);
                if (first != _objects.NextId || allocated < first || next.Id != pc.Id || next.Location != pc.Location || next.Provenance != pc.Provenance)
                    throw new InvalidDataException("Invalid settled PC allocation or identity.");
                if (gone.Any(id => _objects.Retired.Contains(id))) throw new InvalidDataException("A settled PC retires an ID twice.");
                _objects.Replace(_objects.WorldId, allocated, _objects.Locations, _objects.Pcs.Select(p => p.Id == pc.Id ? next : p), _objects.Retired.Concat(gone));
                _edits.Remove(edit);
                _activity.ClearClaim(edit.Actor, edit.Token);
                _native.Settled(ctx, _objects.Pcs.Single(p => p.Id == pc.Id), edit);
                break;
            }
        }
    }

    private static PcFact Expand(byte[] compressed, out ulong first, out ulong next, out NetObjectId[] retired)
    {
        using var input = new MemoryStream(compressed);
        using var zip = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(); var chunk = new byte[4096]; int read;
        while ((read = zip.Read(chunk)) != 0)
        {
            if (output.Length + read > Protocol.MaxModStateBytes) throw new InvalidDataException("Settled PC expansion exceeds limits.");
            output.Write(chunk, 0, read);
        }
        var r = new NetDataReader(output.ToArray());
        try
        {
            NetObjectId.Read(r); first = r.GetULong(); next = r.GetULong();
            var n = r.GetInt();
            if (n is < 0 or > ObjectWorld.MaxObjects) throw new InvalidDataException("Invalid settled retire count.");
            retired = new NetObjectId[n]; for (var i = 0; i < n; i++) retired[i] = NetObjectId.Read(r);
            var pc = ObjectWorld.ReadPc(r);
            if (r.AvailableBytes != 0) throw new InvalidDataException("Trailing settled PC bytes.");
            return pc;
        }
        catch (Exception e) when (Protocol.IsReadError(e)) { throw new InvalidDataException("Malformed settled PC.", e); }
    }
}

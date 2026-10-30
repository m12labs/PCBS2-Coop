using System.IO.Compression;
using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>How the editor's game started the edit (4e decompile): an install (<c>AssemblyTick</c>, or clicking a
/// placeholder), a removal through <c>WorkingOnComputerState.Uninstall</c> (cables unplug first), or the direct removal
/// that skips the cable step. Phase 5 adds the whole-PC gestures: plugging a cable in from a connector (<c>Connect</c>,
/// which may also add a splitter), unplugging one (<c>Unplug</c>) and the power button (<c>Power</c>). Phase 10 adds
/// <c>Water</c>: any custom water-cooling change (a pipe, coolant, a block, pump or reservoir), shown on the other game by
/// reloading the finished PC (owner decision, October 29, 2026).</summary>
public enum EditGesture : byte { Install = 1, Remove = 2, RemoveDirect = 3, Connect = 4, Unplug = 5, Power = 6, Water = 7 }
public enum AccessoryKind : byte { Splitter = 1, Adapter = 2 }

/// <summary>One cable as the game saves it (<c>CableSave</c>): connector IDs at both ends, cable and connector types,
/// custom wire colours and route points (9 floats per point: position, up, forward; usually none).</summary>
public sealed record CableFact(string From, string To, string Desc, string Connector, bool NonModular, bool PlayerChoice,
    IReadOnlyList<int> Pins, IReadOnlyList<float> Points)
{
    public const int MaxPointFloats = 9 * 128;
    public string Key => Wiring.Key(From, To);
}
/// <summary>A PSU splitter or power adapter slot (by its index in <c>Case.psuSplitterSlots</c>/<c>powerAdapterSlots</c>)
/// that now holds <see cref="Sku"/> (empty: now empty).</summary>
public sealed record AccessoryChange(AccessoryKind Kind, int Index, string Sku);
public sealed record InventoryChange(string Sku, int Count);

/// <summary>A water-cooling edit's result: the editor's finished PC record (sent before the commit in chunks, compressed)
/// by length and SHA-256, and whether that PC is on.</summary>
public sealed record Rebuild(int Length, IReadOnlyList<byte> Hash, bool Powered)
{
    public const int HashBytes = 32;
    /// <summary>Compressed record limits: chunks fit one command; the whole record stays under 2 MB.</summary>
    public const int MaxChunkBytes = 15 * 1024, MaxBytes = 2 * 1024 * 1024, MaxChunks = MaxBytes / MaxChunkBytes + 1;
}

/// <summary>What a cable or power edit changed on the editor's PC (phase 5b-5c): cables removed (by key) and added,
/// splitter/adapter slots, the editor's inventory and the PC's power state afterwards.</summary>
public sealed record Wiring(IReadOnlyList<string> Removed, IReadOnlyList<CableFact> Added, IReadOnlyList<AccessoryChange> Accessories,
    IReadOnlyList<InventoryChange> Inventory, bool Powered)
{
    public const int MaxCables = 64, MaxAccessories = 32, MaxInventory = 32, MaxIndex = 64, MaxCount = 64;
    public static string Key(string from, string to) => from + ">" + to;
    public static readonly Wiring None = new(Array.Empty<string>(), Array.Empty<CableFact>(), Array.Empty<AccessoryChange>(), Array.Empty<InventoryChange>(), false);
    /// <summary>Nothing changed, given the power state before the edit.</summary>
    public bool Empty(bool poweredBefore) => Removed.Count == 0 && Added.Count == 0 && Accessories.Count == 0 && Inventory.Count == 0 && Powered == poweredBefore;

    internal void Write(NetDataWriter w)
    {
        w.Put(Removed.Count); foreach (var k in Removed) w.Put(k);
        w.Put(Added.Count);
        foreach (var c in Added)
        {
            w.Put(c.From); w.Put(c.To); w.Put(c.Desc); w.Put(c.Connector); w.Put(c.NonModular); w.Put(c.PlayerChoice);
            w.Put(c.Pins.Count); foreach (var p in c.Pins) w.Put(p);
            w.Put(c.Points.Count); foreach (var f in c.Points) w.Put(f);
        }
        w.Put(Accessories.Count); foreach (var a in Accessories) { w.Put((byte)a.Kind); w.Put(a.Index); w.Put(a.Sku); }
        w.Put(Inventory.Count); foreach (var i in Inventory) { w.Put(i.Sku); w.Put(i.Count); }
        w.Put(Powered);
    }

    internal static Wiring Read(NetDataReader r)
    {
        static int Count(NetDataReader r, int max) { var n = r.GetInt(); if (n < 0 || n > max) throw new FormatException("Invalid cable change count."); return n; }
        var removed = new string[Count(r, MaxCables)]; for (var i = 0; i < removed.Length; i++) removed[i] = r.GetString(1024);
        var added = new CableFact[Count(r, MaxCables)];
        for (var i = 0; i < added.Length; i++)
        {
            var from = r.GetString(1024); var to = r.GetString(1024); var desc = r.GetString(1024); var connector = r.GetString(1024);
            var nonModular = r.GetBool(); var choice = r.GetBool();
            var pins = new int[Count(r, EditResult.MaxPins)]; for (var j = 0; j < pins.Length; j++) pins[j] = r.GetInt();
            var points = new float[Count(r, CableFact.MaxPointFloats)]; for (var j = 0; j < points.Length; j++) points[j] = r.GetFloat();
            added[i] = new(from, to, desc, connector, nonModular, choice, pins, points);
        }
        var accessories = new AccessoryChange[Count(r, MaxAccessories)];
        for (var i = 0; i < accessories.Length; i++) accessories[i] = new((AccessoryKind)r.GetByte(), r.GetInt(), r.GetString(1024));
        var inventory = new InventoryChange[Count(r, MaxInventory)];
        for (var i = 0; i < inventory.Length; i++) inventory[i] = new(r.GetString(1024), r.GetInt());
        return new(removed, added, accessories, inventory, r.GetBool());
    }

    /// <summary>Why this change is malformed, or null.</summary>
    internal string? Check()
    {
        static bool Text(string s) => s.Length <= ComponentEditWorld.MaxText && !s.Any(char.IsControl);
        if (Removed.Any(k => k.Length == 0 || !Text(k)) || Removed.Distinct(StringComparer.Ordinal).Count() != Removed.Count) return "Invalid removed cable.";
        foreach (var c in Added)
            if (c.From.Length == 0 || c.To.Length == 0 || !Text(c.From) || !Text(c.To) || !Text(c.Desc) || !Text(c.Connector) ||
                c.Points.Count % 9 != 0 || c.Points.Any(f => !float.IsFinite(f) || MathF.Abs(f) > 1000)) return "Invalid added cable.";
        if (Added.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() != Added.Count) return "A cable is added twice.";
        if (Accessories.Any(a => !Enum.IsDefined(a.Kind) || a.Index is < 0 or >= MaxIndex || !Text(a.Sku)) ||
            Accessories.Select(a => (a.Kind, a.Index)).Distinct().Count() != Accessories.Count) return "Invalid splitter or adapter change.";
        if (Inventory.Any(i => i.Sku.Length == 0 || !Text(i.Sku) || i.Count == 0 || Math.Abs(i.Count) > MaxCount) ||
            Inventory.Select(i => i.Sku).Distinct(StringComparer.Ordinal).Count() != Inventory.Count) return "Invalid inventory change.";
        return null;
    }
}
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
    /// <summary>A cable or power edit's result: what changed on the editor's PC.</summary>
    public static EditResult Wired(Wiring wiring) => Done() with { Wiring = wiring };
    /// <summary>Set for cable and power edits (phase 5), absent for part edits.</summary>
    public Wiring? Wiring { get; init; }
    /// <summary>A water-cooling edit's result (phase 10): the finished record to reload.</summary>
    public static EditResult Rebuilt(Rebuild rebuild) => Done() with { Rebuild = rebuild };
    public Rebuild? Rebuild { get; init; }
}

/// <summary>One component edit in progress. <see cref="Part"/> is the installed SKU; <see cref="Component"/> the
/// removed part (none for an integral cover). <see cref="Targets"/> is the claim the edit holds.</summary>
/// <remarks>For a cable or power edit (<see cref="Wired"/>), <see cref="Slot"/> is none, <see cref="Targets"/> is empty
/// (the claim covers the whole PC) and <see cref="Part"/> names what was pressed: the connector the cable starts from, the
/// unplugged cable's key, or "on"/"off".</remarks>
public sealed record ComponentEdit(OperationId Token, ActorSlot Actor, ulong Epoch, NetObjectId Pc, NetObjectId Slot,
    EditGesture Gesture, string Part, NetObjectId Component, IReadOnlyList<NetObjectId> Targets)
{
    public bool Install => Gesture == EditGesture.Install;
    public bool Wired => ComponentEditWorld.IsWiring(Gesture);
    /// <summary>A water-cooling edit: the other game reloads the finished PC.</summary>
    public bool Rebuilt => Gesture == EditGesture.Water;
    public bool WholePc => ComponentEditWorld.IsWholePc(Gesture);
    public EditStage Stage { get; init; } = EditStage.Running;
    public EditResult? Result { get; init; }
}

/// <summary>Native side of component edits. CheckHost and Capture run on the host and only read. Began, Committed,
/// Cancelled and Settled run after the model changed (both peers, in host order) and must not submit commands.</summary>
public interface IEditNative
{
    /// <summary><paramref name="slot"/> is null for a cable or power edit.</summary>
    string? CheckHost(in CommandContext ctx, PcFact pc, AssemblyResource? slot, ComponentEdit edit);
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
    private enum Step : byte { Begin = 1, Commit = 2, Cancel = 3, Settle = 4, Chunk = 5 }
    public const int MaxText = 255;
    private readonly ObjectWorld _objects;
    private readonly ActivityWorld _activity;
    private readonly IEditNative _native;
    private readonly List<ComponentEdit> _edits = new();
    /// <summary>Water-cooling edits: the chunks of the editor's finished record received so far, and their total.</summary>
    private readonly Dictionary<OperationId, (int Total, List<byte[]> Chunks)> _records = new();
    private (OperationId Op, byte[] Payload)? _settling;
    public ComponentEditWorld(ObjectWorld objects, ActivityWorld activity, IEditNative native)
    { _objects = objects; _activity = activity; _native = native; }
    public string Name => "component-edit";
    public static bool IsWiring(EditGesture g) => g is EditGesture.Connect or EditGesture.Unplug or EditGesture.Power;
    /// <summary>Gestures whose claim covers the whole PC: cables, power and water cooling.</summary>
    public static bool IsWholePc(EditGesture g) => IsWiring(g) || g == EditGesture.Water;
    public IReadOnlyList<ComponentEdit> Edits => _edits;
    public ComponentEdit? Find(OperationId token) => _edits.SingleOrDefault(e => e.Token == token);
    /// <summary>The edit running on that PC, if any (one per PC: the other player waits).</summary>
    public ComponentEdit? On(NetObjectId pc) => _edits.SingleOrDefault(e => e.Pc == pc);
    /// <summary>World generation start: edits never outlive their world.</summary>
    public void Reset() { _edits.Clear(); _records.Clear(); _settling = null; }
    /// <summary>A water-cooling edit's complete record (compressed), once every chunk has arrived; otherwise null.</summary>
    public byte[]? RecordOf(OperationId token) =>
        _records.TryGetValue(token, out var r) && r.Chunks.Count == r.Total ? r.Chunks.SelectMany(c => c).ToArray() : null;

    public static byte[] Begin(ulong epoch, NetObjectId pc, NetObjectId slot, ulong version, EditGesture gesture, string part, NetObjectId component) =>
        Encode(w => { w.Put((byte)Step.Begin); w.Put(epoch); pc.Write(w); slot.Write(w); w.Put(version); w.Put((byte)gesture); w.Put(part ?? ""); component.Write(w); });
    public static byte[] Commit(ulong epoch, OperationId token, EditResult result) => Encode(w =>
    {
        w.Put((byte)Step.Commit); w.Put(epoch); Token(w, token);
        w.Put((byte)result.Outcome); w.Put(result.Degrees); w.Put(result.Flipped); w.Put(result.Mode); w.Put(result.Selected); w.Put(result.FromInventory);
        w.Put(result.CableType ?? ""); w.Put(result.CableConnector ?? ""); w.Put(result.Pins.Count); foreach (var p in result.Pins) w.Put(p);
        w.Put(result.Wiring != null); result.Wiring?.Write(w);
        w.Put(result.Rebuild != null);
        if (result.Rebuild is { } b) { w.Put(b.Length); w.Put(b.Hash.Count); foreach (var x in b.Hash) w.Put(x); w.Put(b.Powered); }
    });
    /// <summary>Editor, before a water-cooling commit: chunk <paramref name="index"/> of <paramref name="total"/> of its
    /// finished record.</summary>
    public static byte[] Chunk(ulong epoch, OperationId token, int index, int total, byte[] data) => Encode(w =>
    {
        w.Put((byte)Step.Chunk); w.Put(epoch); Token(w, token); w.Put(index); w.Put(total); Protocol.WriteBlob(w, data);
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
        public int Index { get; init; }
        public int Total { get; init; }
        public byte[] Data { get; init; } = Array.Empty<byte>();
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
                var wiring = r.GetBool() ? Wiring.Read(r) : null;
                Rebuild? rebuild = null;
                if (r.GetBool())
                {
                    var length = r.GetInt(); var hashLength = r.GetInt();
                    if (hashLength != Rebuild.HashBytes) throw new FormatException("Invalid record hash.");
                    var hash = new byte[hashLength]; for (var i = 0; i < hashLength; i++) hash[i] = r.GetByte();
                    rebuild = new(length, hash, r.GetBool());
                }
                c = new(step, epoch, default, default, 0, default, "", default, token,
                    new(outcome, degrees, flipped, mode, selected, inventory, type, connector, pins) { Wiring = wiring, Rebuild = rebuild });
                break;
            }
            case Step.Cancel:
                c = new(step, r.GetULong(), default, default, 0, default, "", default, Token(r), null);
                break;
            case Step.Settle:
                c = new(step, 0, default, default, 0, default, "", default, Token(r), null);
                if (canonical) c = c with { Settled = r.GetRemainingBytes() };
                break;
            case Step.Chunk:
            {
                var epoch = r.GetULong(); var token = Token(r); var index = r.GetInt(); var total = r.GetInt();
                var data = Protocol.ReadBlob(r, Rebuild.MaxChunkBytes, out _) ?? throw new FormatException("Invalid record chunk.");
                c = new(step, epoch, default, default, 0, default, "", default, token, null) { Index = index, Total = total, Data = data };
                break;
            }
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
        if (IsWholePc(c.Gesture))
        {
            // Whole-PC claim: a cable edit can touch connectors on many parts and add a splitter (phase 5 audit).
            if (!c.Slot.IsNone || c.Version != 0 || !c.Component.IsNone || c.Part.Length == 0 || !Text(c.Part)) return "Invalid cable or power change.";
            if (c.Gesture == EditGesture.Power && c.Part is not ("on" or "off")) return "Invalid power change.";
            return null;
        }
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
        if (_edits.FirstOrDefault(e => e.Pc == pc && e.Actor != requester) is { } running)
            return running.Rebuilt ? "The other player is building water cooling on that PC." : "The other player is changing a part of that PC.";
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
        if (edit.Wired != (r.Wiring != null) || edit.Rebuilt != (r.Rebuild != null)) return "Invalid edit result.";
        if (r.Rebuild is { } rebuild)
        {
            if (r.Outcome != EditOutcome.Done) return "Invalid edit outcome.";
            if (RecordOf(edit.Token) is not { } record) return "The finished PC record hasn't fully arrived.";
            if (record.Length != rebuild.Length || !System.Security.Cryptography.SHA256.HashData(record).SequenceEqual(rebuild.Hash))
                return "The finished PC record doesn't match its commit.";
        }
        if (r.Wiring is { } wiring)
        {
            if (r.Outcome != EditOutcome.Done) return "Invalid edit outcome.";
            if (wiring.Check() is { } bad) return bad;
        }
        if (r.Outcome == EditOutcome.Unplugged ? edit.Gesture != EditGesture.Remove : r.Outcome != EditOutcome.Done) return "Invalid edit outcome.";
        if (!float.IsNaN(r.Degrees) && (!float.IsFinite(r.Degrees) || MathF.Abs(r.Degrees) > 3600)) return "Invalid orientation.";
        if (r.Mode > 8 || r.Selected > 8 || !Text(r.CableType) || !Text(r.CableConnector) || r.Pins.Count > EditResult.MaxPins) return "Invalid edit values.";
        return null;
    }

    private string? CheckChunk(ComponentEdit edit, Request c)
    {
        if (!edit.Rebuilt) return "Only a water-cooling edit sends a record.";
        if (c.Total is < 1 or > Rebuild.MaxChunks || c.Data.Length is < 1 or > Rebuild.MaxChunkBytes) return "Invalid record chunk.";
        var (total, chunks) = _records.TryGetValue(edit.Token, out var r) ? r : (c.Total, new List<byte[]>());
        if (c.Total != total || c.Index != chunks.Count) return "Record chunk out of order.";
        if (chunks.Sum(x => x.Length) + c.Data.Length > Rebuild.MaxBytes) return "The finished PC record is too large.";
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
                    try { error = _native.CheckHost(ctx, pc!, slot, new(ctx.Op, ctx.Op.Actor, c.Epoch, pc!.Id, slot?.Id ?? default, c.Gesture, c.Part, c.Component, targets)); }
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
            case Step.Chunk:
            {
                var (edit, why) = Owned(ctx.Op.Actor, c, EditStage.Running);
                error = why ?? (ctx.Op.Actor != edit!.Actor ? "Only the editor sends its record." : CheckChunk(edit, c));
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
                var edit = new ComponentEdit(ctx.Op, ctx.Op.Actor, c.Epoch, pc!.Id, slot?.Id ?? default, c.Gesture, c.Part, c.Component,
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
            case Step.Chunk:
            {
                var (edit, why) = Owned(ctx.Op.Actor, c, EditStage.Running);
                if (why != null || edit!.Actor != ctx.Op.Actor || CheckChunk(edit, c) is { } bad) throw new InvalidDataException("Record chunk no longer applies: " + (why ?? "invalid chunk"));
                if (!_records.TryGetValue(edit.Token, out var r)) _records[edit.Token] = r = (c.Total, new List<byte[]>());
                r.Chunks.Add(c.Data);
                break;
            }
            case Step.Cancel:
            {
                var (edit, why) = Owned(ctx.Op.Actor, c, ctx.Op.Actor == ActorSlot.Host ? null : EditStage.Running);
                if (why != null) throw new InvalidDataException("Component cancel no longer applies: " + why);
                _edits.Remove(edit!); _records.Remove(edit!.Token);
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
                _edits.Remove(edit); _records.Remove(edit.Token);
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

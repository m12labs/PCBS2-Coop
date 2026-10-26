using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>The peripheral installed in one workstation slot: the game's bench ID (<c>PerifBenchID</c>), the slot's
/// part type and the peripheral's asset path, which is what the native save stores. An empty path is an empty slot.</summary>
public sealed record PeripheralFact(int Bench, int Slot, string Path);
/// <summary>A swap request. <see cref="From"/> is the peripheral the requester saw in that slot; <see cref="Part"/> is
/// the catalog part ID to install.</summary>
public sealed record PeripheralCommand(ulong Epoch, int Bench, int Slot, string From, string Part);

/// <summary>Native side of peripheral swaps. Resolve and BenchKeys must be deterministic on both peers. CheckHost runs on
/// the host during validation and only reads. Swapped runs after the model changed and must not submit commands.</summary>
public interface IPeripheralNative
{
    /// <summary>Asset path of catalog peripheral <paramref name="part"/> if it fits that slot, otherwise null.</summary>
    string? Resolve(int bench, int slot, string part);
    /// <summary>Bench location keys that belong to that workstation (its main and tool bench slots).</summary>
    IReadOnlyCollection<string> BenchKeys(int bench);
    string? CheckHost(in CommandContext ctx, PeripheralCommand command);
    void Swapped(in MutationContext ctx, PeripheralCommand command, string path);
}

/// <summary>Host-ordered peripheral bindings of the workshop's workstations (task 3g). Each slot holds exactly one
/// peripheral fact. A swap names what the requester saw, so two swaps of one slot can't both land; it is refused while
/// the other player works on a PC at that workstation. Selection and inventory browsing stay local. The host captures
/// the facts from its loaded world at every generation start; saves and snapshots carry them for checking.</summary>
public sealed class WorkstationWorld : IWorldState, ICanonicalCommandHandler
{
    public const int MaxSlots = 256;
    private const byte SwapAction = 1;
    private readonly ObjectWorld _objects;
    private readonly ActivityWorld _activity;
    private readonly IPeripheralNative _native;
    public WorkstationWorld(ObjectWorld objects, ActivityWorld activity, IPeripheralNative native)
    { _objects = objects; _activity = activity; _native = native; }
    public string Name => "workstations";
    public IReadOnlyList<PeripheralFact> Slots { get; private set; } = Array.Empty<PeripheralFact>();
    public bool Bound => Slots.Count > 0;
    public PeripheralFact? Get(int bench, int slot) => Slots.SingleOrDefault(x => x.Bench == bench && x.Slot == slot);

    public void Reset() => Slots = Array.Empty<PeripheralFact>();

    /// <summary>Publish a complete, validated slot table (a host capture or a restored state).</summary>
    public void Replace(IEnumerable<PeripheralFact> slots)
    {
        var sorted = slots.OrderBy(x => x.Bench).ThenBy(x => x.Slot).ToArray();
        if (sorted.Length > MaxSlots) Bad("Too many peripheral slots.");
        for (var i = 0; i < sorted.Length; i++)
        {
            var s = sorted[i];
            if (!ValidKey(s.Bench, s.Slot) || !ValidPath(s.Path)) Bad("Invalid peripheral slot fact.");
            if (i > 0 && sorted[i - 1].Bench == s.Bench && sorted[i - 1].Slot == s.Slot) Bad("Duplicate peripheral slot.");
        }
        Slots = Array.AsReadOnly(sorted);
    }

    public static byte[] Swap(ulong epoch, int bench, int slot, string from, string part)
    {
        var w = new NetDataWriter(); w.Put(SwapAction); w.Put(epoch); w.Put(bench); w.Put(slot); w.Put(from ?? ""); w.Put(part ?? "");
        return w.CopyData();
    }

    private static PeripheralCommand Parse(NetDataReader r)
    {
        if (r.GetByte() != SwapAction) throw new FormatException("Unknown peripheral action.");
        return new(r.GetULong(), r.GetInt(), r.GetInt(), r.GetString(1024), r.GetString(1024));
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        if (payload.AvailableBytes != 0) throw new FormatException("Trailing peripheral bytes.");
        var error = Check(ctx.Op.Actor, c, out _);
        if (error == null)
        {
            try { error = _native.CheckHost(ctx, c); }
            catch (Exception e) { error = "Host check failed: " + e.Message; }
        }
        return error == null ? CommandVerdict.Accept : CommandVerdict.Reject(error);
    }

    private string? Check(ActorSlot requester, PeripheralCommand c, out string? path)
    {
        path = null;
        if (!Bound || _objects.WorldId == Guid.Empty) return "Workstations are not bound yet.";
        if (_activity.Get(requester) is not { } actor || actor.Epoch != c.Epoch) return "Actor presence changed.";
        if (!ValidKey(c.Bench, c.Slot) || !ValidPath(c.From) || !Text(c.Part)) return "Invalid peripheral request.";
        if (Get(c.Bench, c.Slot) is not { } current) return "Unknown peripheral slot.";
        if (current.Path != c.From) return "That peripheral was just changed.";
        try { path = _native.Resolve(c.Bench, c.Slot, c.Part); }
        catch (Exception e) { return "Peripheral unavailable: " + e.Message; }
        if (path == null || !Text(path)) return "That part doesn't fit this peripheral slot.";
        var benches = _native.BenchKeys(c.Bench);
        foreach (var other in _activity.Actors.Where(x => x.Actor != requester && !x.WorkingPc.IsNone))
            if (_objects.Pcs.SingleOrDefault(x => x.Id == other.WorkingPc) is { Location.Place: PcPlace.Bench } pc && benches.Contains(pc.Location.Key))
                return "The other player is working at that workstation.";
        return null;
    }

    public byte[] Canonicalize(in CommandContext ctx, NetDataReader request)
    {
        var bytes = request.RawData.AsSpan(request.Position, request.AvailableBytes).ToArray();
        var c = Parse(request);
        // The host's resolution of the part becomes the shared fact; a peer that resolves it differently diverges.
        var path = _native.Resolve(c.Bench, c.Slot, c.Part) ?? throw new InvalidOperationException("Peripheral disappeared during validation.");
        var w = new NetDataWriter(); w.Put(bytes); w.Put(path); return w.CopyData();
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var c = Parse(payload); var path = payload.GetString(1024);
        if (payload.AvailableBytes != 0) Bad("Trailing peripheral bytes.");
        if (Check(ctx.Op.Actor, c, out var local) is { } error) Bad("Peripheral swap no longer applies: " + error);
        if (local != path) Bad("This peer resolves the peripheral differently.");
        Slots = Array.AsReadOnly(Slots.Select(x => x.Bench == c.Bench && x.Slot == c.Slot ? x with { Path = path } : x).ToArray());
        _native.Swapped(ctx, c, path);
    }

    public void Write(NetDataWriter w)
    {
        w.Put(Slots.Count);
        foreach (var s in Slots) { w.Put(s.Bench); w.Put(s.Slot); w.Put(s.Path); }
    }

    public void Read(NetDataReader r)
    {
        try
        {
            var n = r.GetInt();
            if (n is < 0 or > MaxSlots) Bad("Invalid peripheral slot count.");
            var slots = new PeripheralFact[n];
            for (var i = 0; i < n; i++) slots[i] = new(r.GetInt(), r.GetInt(), r.GetString(1024));
            if (r.AvailableBytes != 0) Bad("Trailing peripheral state.");
            Replace(slots);
        }
        catch (Exception e) when (Protocol.IsReadError(e)) { throw new InvalidDataException("Malformed peripheral state.", e); }
    }

    private static bool ValidKey(int bench, int slot) => bench is >= 0 and <= 255 && slot is >= 0 and <= 1023;
    private static bool ValidPath(string s) => s != null && (s.Length == 0 || Text(s));
    private static bool Text(string s) => !string.IsNullOrEmpty(s) && s.Length <= 1023 && !s.Any(char.IsControl);
    private static void Bad(string why) => throw new InvalidDataException(why);
}

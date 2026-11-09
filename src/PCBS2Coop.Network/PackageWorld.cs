using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>One package: its job (the job note it carries, 0 for none) and its parts, each serialized whole.</summary>
public sealed record PackageFact(int JobId, IReadOnlyList<byte[]> Contents)
{
    public const int MaxContents = 32, MaxPartBytes = 4096;
    public bool IsValid => JobId >= -1 && Contents.Count <= MaxContents && Contents.All(c => c.Length is > 0 and <= MaxPartBytes);

    /// <summary>The same package as another game reads it: the job and the number of parts (a part read back from its
    /// bytes may serialize slightly differently).</summary>
    public bool Like(PackageFact? other) => other != null && other.JobId == JobId && other.Contents.Count == Contents.Count;

    internal void Write(NetDataWriter w)
    {
        w.Put(JobId); w.Put(Contents.Count);
        foreach (var c in Contents) Protocol.WriteBlob(w, c);
    }

    internal static PackageFact Read(NetDataReader r)
    {
        var job = r.GetInt(); var n = r.GetInt();
        if (n is < 0 or > MaxContents) throw new FormatException("Invalid package.");
        var contents = new byte[n][];
        for (var i = 0; i < n; i++) contents[i] = Protocol.ReadBlob(r, MaxPartBytes, out _) is { Length: > 0 } c ? c : throw new FormatException("Invalid package part.");
        var p = new PackageFact(job, contents);
        return p.IsValid ? p : throw new FormatException("Invalid package.");
    }
}

public enum PackageStep : byte
{
    /// <summary>Host: what its game shows on one bench (a package or none).</summary>
    HostBench = 1,
    /// <summary>Host: the job note on one note slot (0 for none).</summary>
    HostNote = 2,
    /// <summary>Host: what the host carries (a package or none, and a note or none).</summary>
    HostCarry = 3,
    /// <summary>A player picks up the package on a bench.</summary>
    Take = 4,
    /// <summary>A player puts the package it carries on a bench.</summary>
    Place = 5,
    /// <summary>A player packs an owned item into the package it carries (a new one when its hands are empty).</summary>
    Pack = 6,
    /// <summary>A player opens a package (the one it carries, or one on a bench): its parts go back to the inventory.</summary>
    Open = 7,
    /// <summary>A player picks up the job note on a note slot.</summary>
    NoteTake = 8,
    /// <summary>A player puts the note it carries on a note slot.</summary>
    NoteDrop = 9,
    /// <summary>Host: a departed player's package goes to a bench (or its parts to the inventory) and its note to a slot.</summary>
    Recover = 10,
}

/// <summary>One accepted package or note change, as both games apply it.</summary>
public sealed record PackageChange(PackageStep Step, ActorSlot Actor, string Key, PackageFact? Package, int Note, ulong Item)
{
    /// <summary>Recover: the note slot the departed player's note goes to ("" when none was free or there was no note).</summary>
    public string NoteKey { get; init; } = "";
}

/// <summary>Native side of shared packages and notes. <see cref="CheckHost"/> runs on the host during validation and
/// only reads; <see cref="Changed"/> runs after the model changed, in host order, and must not submit commands.</summary>
public interface IPackageNative
{
    string? CheckHost(in CommandContext ctx, PackageChange change);
    /// <summary>Host, for an accepted Pack: the owned item's serialized form and its job (read, not removed).</summary>
    (byte[] Part, int Job) PackItem(ulong item);
    void Changed(in MutationContext ctx, PackageChange change, PackageFact? before);
}

/// <summary>
/// Phase 16c (owner decisions, November 8, 2026): packages and physical job notes in a co-op Career.
/// <list type="bullet">
/// <item>The host's game is the reference for what lies on benches and note slots and what the host carries: it
/// sends a <b>Host…</b> step whenever its game differs from the model, and the guest's game follows.</item>
/// <item>The guest asks first: Take, Place, Pack, Open, NoteTake and NoteDrop name the bench, slot or owned item.
/// The host checks the model (hands, an occupied or empty bench, a note already there) and its game
/// (<see cref="IPackageNative.CheckHost"/>), then both games make the change: the guest's in its hands, the host's on
/// its benches and inventory. The guest's carried package and note exist on the host only in this model.</item>
/// <item>A package's parts left the owned inventory when packed; they travel serialized and go back as new owned
/// items when the package is opened. A departed player's package goes to a free bench, or its parts to the inventory
/// when none is free; its note to a free note slot (<b>Recover</b>).</item>
/// </list>
/// </summary>
public sealed class PackageWorld : ICanonicalCommandHandler, IWorldState
{
    public const int MaxKey = 512, MaxBenches = 256, MaxNotes = 512;

    private readonly ActivityWorld _activity;
    private readonly CareerWorld _career;
    private readonly IPackageNative _native;
    private readonly Dictionary<string, PackageFact> _benches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _notes = new(StringComparer.Ordinal);
    private readonly Dictionary<ActorSlot, PackageFact> _carried = new();
    private readonly Dictionary<ActorSlot, int> _carriedNotes = new();

    public PackageWorld(ActivityWorld activity, CareerWorld career, IPackageNative native) { _activity = activity; _career = career; _native = native; }
    public string Name => "packages";

    /// <summary>Owned items being installed right now (they can't be packed).</summary>
    public Func<IEnumerable<ulong>>? ItemsInUse { get; set; }

    public bool Bound { get; private set; }
    public IReadOnlyDictionary<string, PackageFact> Benches => _benches;
    public IReadOnlyDictionary<string, int> Notes => _notes;
    public PackageFact? CarriedBy(ActorSlot actor) => _carried.GetValueOrDefault(actor);
    public int NoteCarriedBy(ActorSlot actor) => _carriedNotes.GetValueOrDefault(actor);
    /// <summary>The player holds a package or a note (so it can't pick up a PC, and vice versa).</summary>
    public bool HandsFull(ActorSlot actor) => _carried.ContainsKey(actor) || _carriedNotes.ContainsKey(actor);

    public void Reset() { _benches.Clear(); _notes.Clear(); _carried.Clear(); _carriedNotes.Clear(); Bound = false; }

    /// <summary>Host, when a world generation starts: the packages and notes as its game holds them. What a guest
    /// carried in a restored state (it exists only here) is kept, and goes to a bench when nobody comes back for it.</summary>
    public void BindHost(IReadOnlyDictionary<string, PackageFact> benches, IReadOnlyDictionary<string, int> notes, PackageFact? carried, int carriedNote)
    {
        if (benches.Count > MaxBenches || notes.Count > MaxNotes || benches.Keys.Concat(notes.Keys).Any(k => !KeyValid(k)) ||
            benches.Values.Any(p => !p.IsValid) || carried is { IsValid: false } || notes.Values.Any(n => n <= 0) || carriedNote < 0)
            throw new InvalidDataException("Invalid package binding.");
        var guestPackage = _carried.GetValueOrDefault(ActorSlot.Guest);
        var guestNote = _carriedNotes.GetValueOrDefault(ActorSlot.Guest);
        Reset();
        if (guestPackage != null) _carried[ActorSlot.Guest] = guestPackage;
        if (guestNote > 0) _carriedNotes[ActorSlot.Guest] = guestNote;
        foreach (var (k, v) in benches) _benches[k] = v;
        foreach (var (k, v) in notes) _notes[k] = v;
        if (carried != null) _carried[ActorSlot.Host] = carried;
        if (carriedNote > 0) _carriedNotes[ActorSlot.Host] = carriedNote;
        Bound = true;
    }

    private static bool KeyValid(string k) => !string.IsNullOrEmpty(k) && k.Length <= MaxKey && !k.Any(char.IsControl);

    // ---------------- commands ----------------

    private static byte[] Encode(PackageStep step, ulong epoch, Action<NetDataWriter> body)
    {
        var w = new NetDataWriter(); w.Put((byte)step); w.Put(epoch); body(w); return w.CopyData();
    }

    private static void PutPackage(NetDataWriter w, PackageFact? p) { w.Put(p != null); p?.Write(w); }
    private static PackageFact? GetPackage(NetDataReader r) => r.GetBool() ? PackageFact.Read(r) : null;

    public static byte[] HostBench(ulong epoch, string bench, PackageFact? package) => Encode(PackageStep.HostBench, epoch, w => { w.Put(bench, MaxKey); PutPackage(w, package); });
    public static byte[] HostNote(ulong epoch, string slot, int job) => Encode(PackageStep.HostNote, epoch, w => { w.Put(slot, MaxKey); w.Put(job); });
    public static byte[] HostCarry(ulong epoch, PackageFact? package, int note) => Encode(PackageStep.HostCarry, epoch, w => { PutPackage(w, package); w.Put(note); });
    public static byte[] Take(ulong epoch, string bench) => Encode(PackageStep.Take, epoch, w => w.Put(bench, MaxKey));
    public static byte[] Place(ulong epoch, string bench) => Encode(PackageStep.Place, epoch, w => w.Put(bench, MaxKey));
    /// <summary>The request names the item; the host's accepted command adds the item's serialized form and job.</summary>
    public static byte[] Pack(ulong epoch, ulong item) => Encode(PackageStep.Pack, epoch, w => { w.Put(item); w.Put(false); });
    /// <summary>Open the carried package (<paramref name="bench"/> empty) or the one on that bench.</summary>
    public static byte[] Open(ulong epoch, string bench) => Encode(PackageStep.Open, epoch, w => w.Put(bench ?? "", MaxKey));
    public static byte[] NoteTake(ulong epoch, string slot) => Encode(PackageStep.NoteTake, epoch, w => w.Put(slot, MaxKey));
    public static byte[] NoteDrop(ulong epoch, string slot) => Encode(PackageStep.NoteDrop, epoch, w => w.Put(slot, MaxKey));
    /// <summary>Host: <paramref name="bench"/> is where the package goes ("" puts its parts in the inventory);
    /// <paramref name="noteSlot"/> where the note goes ("" when none is free).</summary>
    public static byte[] Recover(ulong epoch, ActorSlot absent, string bench, string noteSlot) =>
        Encode(PackageStep.Recover, epoch, w => { w.Put((byte)absent); w.Put(bench ?? "", MaxKey); w.Put(noteSlot ?? "", MaxKey); });

    private sealed record Command(PackageStep Step, ulong Epoch, PackageChange Change, bool HasPart);

    private static Command Parse(NetDataReader r, ActorSlot actor)
    {
        var step = (PackageStep)r.GetByte(); var epoch = r.GetULong();
        PackageChange c; var hasPart = false;
        switch (step)
        {
            case PackageStep.HostBench: c = new(step, actor, r.GetString(MaxKey), GetPackage(r), 0, 0); break;
            case PackageStep.HostNote: c = new(step, actor, r.GetString(MaxKey), null, r.GetInt(), 0); break;
            case PackageStep.HostCarry: { var p = GetPackage(r); c = new(step, actor, "", p, r.GetInt(), 0); break; }
            case PackageStep.Take or PackageStep.Place or PackageStep.Open or PackageStep.NoteTake or PackageStep.NoteDrop:
                c = new(step, actor, r.GetString(MaxKey), null, 0, 0); break;
            case PackageStep.Pack:
            {
                var item = r.GetULong(); hasPart = r.GetBool();
                PackageFact? part = null;
                if (hasPart)
                {
                    var job = r.GetInt();
                    var bytes = Protocol.ReadBlob(r, PackageFact.MaxPartBytes, out _) is { Length: > 0 } b ? b : throw new FormatException("Invalid packed part.");
                    part = new PackageFact(job, new[] { bytes });
                }
                c = new(step, actor, "", part, 0, item);
                break;
            }
            case PackageStep.Recover:
            {
                var absent = (ActorSlot)r.GetByte();
                if (absent is not (ActorSlot.Host or ActorSlot.Guest)) throw new FormatException("Invalid departed seat.");
                c = new(step, absent, r.GetString(MaxKey), null, 0, 0) { NoteKey = r.GetString(MaxKey) };
                break;
            }
            default: throw new FormatException("Unknown package step.");
        }
        if (r.AvailableBytes != 0) throw new FormatException("Trailing package bytes.");
        if (c.Package is { IsValid: false } || c.Note < 0) throw new FormatException("Invalid package.");
        return new(step, epoch, c, hasPart);
    }

    /// <summary>Why <paramref name="c"/> doesn't fit the model, or null.</summary>
    public string? Check(PackageChange c, ActorSlot by)
    {
        var host = by == ActorSlot.Host;
        bool KeyOk(string k) => KeyValid(k);
        string? Free(ActorSlot a) => HandsFull(a) || !_activity.Carried(a).IsNone ? "Your hands are full." : null;
        switch (c.Step)
        {
            case PackageStep.HostBench:
                if (!host) return "Only the host reports its benches.";
                return KeyOk(c.Key) && (_benches.ContainsKey(c.Key) || _benches.Count < MaxBenches) ? null : "Invalid bench.";
            case PackageStep.HostNote:
                if (!host) return "Only the host reports its notes.";
                return KeyOk(c.Key) && (_notes.ContainsKey(c.Key) || _notes.Count < MaxNotes) ? null : "Invalid note slot.";
            case PackageStep.HostCarry:
                return host ? null : "Only the host reports what it carries.";
            case PackageStep.Take:
                if (!KeyOk(c.Key) || !_benches.ContainsKey(c.Key)) return "There's no package on that bench any more.";
                return Free(by);
            case PackageStep.Place:
                if (!_carried.ContainsKey(by)) return "You aren't carrying a package.";
                if (!KeyOk(c.Key) || _benches.ContainsKey(c.Key)) return "There's already a package on that bench.";
                return _benches.Count >= MaxBenches ? "Too many packages." : null;
            case PackageStep.Pack:
            {
                if (_career.Item(c.Item) is not { List: ItemList.Main or ItemList.Peripheral }) return "That part is no longer in the shared inventory.";
                if (ItemsInUse?.Invoke().Contains(c.Item) == true) return "That part is being installed.";
                if (_carried.TryGetValue(by, out var p)) return p.Contents.Count >= PackageFact.MaxContents ? "The package is full." : null;
                return _carriedNotes.ContainsKey(by) || !_activity.Carried(by).IsNone ? "Your hands are full." : null;
            }
            case PackageStep.Open:
                if (c.Key.Length == 0) return _carried.ContainsKey(by) ? null : "You aren't carrying a package.";
                return KeyOk(c.Key) && _benches.ContainsKey(c.Key) ? null : "There's no package on that bench any more.";
            case PackageStep.NoteTake:
                if (!KeyOk(c.Key) || !_notes.ContainsKey(c.Key)) return "That note isn't there any more.";
                return Free(by);
            case PackageStep.NoteDrop:
                if (!_carriedNotes.ContainsKey(by)) return "You aren't carrying a note.";
                return !KeyOk(c.Key) || _notes.ContainsKey(c.Key) ? "There's already a note there." : null;
            default:
                if (!host) return "Only the host recovers a departed player's things.";
                if (!HandsFull(c.Actor)) return "Nothing to recover.";
                if (c.Key.Length > 0 && (!KeyOk(c.Key) || _benches.ContainsKey(c.Key) || !_carried.ContainsKey(c.Actor))) return "That bench isn't free.";
                return c.NoteKey.Length > 0 && (!KeyOk(c.NoteKey) || _notes.ContainsKey(c.NoteKey)) ? "That note slot isn't free." : null;
        }
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var c = Parse(payload, ctx.Op.Actor);
        if (!Bound || !_career.Bound) return CommandVerdict.Reject("Packages and notes are shared only in a co-op career.");
        if (_activity.Get(ctx.Op.Actor) is not { } actor || actor.Epoch != c.Epoch) return CommandVerdict.Reject("Actor presence changed.");
        if (c.HasPart) return CommandVerdict.Reject("Only the host names a packed part.");
        if (Check(c.Change, ctx.Op.Actor) is { } why) return CommandVerdict.Reject(why);
        string? native;
        try { native = _native.CheckHost(ctx, c.Change); }
        catch (Exception e) { native = "Host check failed: " + e.Message; }
        return native == null ? CommandVerdict.Accept : CommandVerdict.Reject(native);
    }

    public byte[] Canonicalize(in CommandContext ctx, NetDataReader request)
    {
        var start = request.Position;
        var c = Parse(request, ctx.Op.Actor);
        var raw = request.RawData.AsSpan(start, request.Position - start).ToArray();
        if (c.Step != PackageStep.Pack) return raw;
        var (part, job) = _native.PackItem(c.Change.Item);
        if (part.Length is 0 or > PackageFact.MaxPartBytes) throw new InvalidDataException("That part is too large to pack in co-op.");
        return Encode(PackageStep.Pack, c.Epoch, w => { w.Put(c.Change.Item); w.Put(true); w.Put(job); Protocol.WriteBlob(w, part); });
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var c = Parse(payload, ctx.Op.Actor).Change;
        var by = ctx.Op.Actor;
        if (Check(c, by) is { } error) throw new InvalidDataException("Package change no longer applies: " + error);
        PackageFact? before = null;
        switch (c.Step)
        {
            case PackageStep.HostBench:
                before = _benches.GetValueOrDefault(c.Key);
                if (c.Package == null) _benches.Remove(c.Key); else _benches[c.Key] = c.Package;
                break;
            case PackageStep.HostNote:
                if (c.Note == 0) _notes.Remove(c.Key); else _notes[c.Key] = c.Note;
                break;
            case PackageStep.HostCarry:
                before = _carried.GetValueOrDefault(ActorSlot.Host);
                if (c.Package == null) _carried.Remove(ActorSlot.Host); else _carried[ActorSlot.Host] = c.Package;
                if (c.Note == 0) _carriedNotes.Remove(ActorSlot.Host); else _carriedNotes[ActorSlot.Host] = c.Note;
                break;
            case PackageStep.Take:
                before = _benches[c.Key]; _benches.Remove(c.Key); _carried[by] = before;
                break;
            case PackageStep.Place:
                before = _carried[by]; _carried.Remove(by); _benches[c.Key] = before;
                break;
            case PackageStep.Pack:
            {
                var part = c.Package ?? throw new InvalidDataException("The packed part is missing.");
                before = _carried.GetValueOrDefault(by);
                _carried[by] = before == null ? part : before with { Contents = before.Contents.Append(part.Contents[0]).ToArray() };
                break;
            }
            case PackageStep.Open:
                if (c.Key.Length == 0) { before = _carried[by]; _carried.Remove(by); }
                else { before = _benches[c.Key]; _benches.Remove(c.Key); }
                break;
            case PackageStep.NoteTake:
                _carriedNotes[by] = _notes[c.Key]; _notes.Remove(c.Key);
                c = c with { Note = _carriedNotes[by] };
                break;
            case PackageStep.NoteDrop:
                _notes[c.Key] = _carriedNotes[by]; _carriedNotes.Remove(by);
                c = c with { Note = _notes[c.Key] };
                break;
            default:
            {
                before = _carried.GetValueOrDefault(c.Actor);
                var note = _carriedNotes.GetValueOrDefault(c.Actor);
                _carried.Remove(c.Actor); _carriedNotes.Remove(c.Actor);
                if (before != null && c.Key.Length > 0) _benches[c.Key] = before;
                if (note > 0 && c.NoteKey.Length > 0) _notes[c.NoteKey] = note;
                c = c with { Note = note };
                break;
            }
        }
        _native.Changed(ctx, c, before);
    }

    // ---------------- snapshot state ----------------

    public void Write(NetDataWriter w)
    {
        w.Put(Bound);
        if (!Bound) return;
        w.Put(_benches.Count);
        foreach (var (k, v) in _benches.OrderBy(p => p.Key, StringComparer.Ordinal)) { w.Put(k, MaxKey); v.Write(w); }
        w.Put(_notes.Count);
        foreach (var (k, v) in _notes.OrderBy(p => p.Key, StringComparer.Ordinal)) { w.Put(k, MaxKey); w.Put(v); }
        w.Put(_carried.Count);
        foreach (var (a, v) in _carried.OrderBy(p => p.Key)) { w.Put((byte)a); v.Write(w); }
        w.Put(_carriedNotes.Count);
        foreach (var (a, v) in _carriedNotes.OrderBy(p => p.Key)) { w.Put((byte)a); w.Put(v); }
    }

    public void Read(NetDataReader r)
    {
        Reset();
        if (!r.GetBool()) return;
        int Count(int max) { var n = r.GetInt(); return n >= 0 && n <= max ? n : throw new InvalidDataException("Invalid package state."); }
        for (int i = 0, n = Count(MaxBenches); i < n; i++) { var k = r.GetString(MaxKey); _benches[k] = PackageFact.Read(r); }
        for (int i = 0, n = Count(MaxNotes); i < n; i++) { var k = r.GetString(MaxKey); _notes[k] = r.GetInt(); }
        for (int i = 0, n = Count(2); i < n; i++) { var a = (ActorSlot)r.GetByte(); _carried[a] = PackageFact.Read(r); }
        for (int i = 0, n = Count(2); i < n; i++) { var a = (ActorSlot)r.GetByte(); _carriedNotes[a] = r.GetInt(); }
        if (_benches.Keys.Concat(_notes.Keys).Any(k => !KeyValid(k)) || _notes.Values.Any(v => v <= 0) || _carriedNotes.Values.Any(v => v <= 0) ||
            _carried.Keys.Concat(_carriedNotes.Keys).Any(a => a is not (ActorSlot.Host or ActorSlot.Guest)))
            throw new InvalidDataException("Invalid package state.");
        Bound = true;
    }
}

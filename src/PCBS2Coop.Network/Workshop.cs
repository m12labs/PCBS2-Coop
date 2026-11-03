using System.Security.Cryptography;
using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>One decorated thing in the workshop as the game saves it (<c>DecoratorListLink</c>): its pick as the game
/// writes it twice (<see cref="Selection"/> and <see cref="Current"/>, a material, mesh or prefab name), the object's
/// number, the game's swap type (wall, ceiling, floor, poster, desk...) and theme values, its DLC pack, and which of the
/// objects with the same type and number it is (<see cref="Nth"/>, in the game's own order).</summary>
public sealed record DecorItem(string Selection, int Id, string Current, int Type, int Theme, bool Dlc, int DlcPack, int Nth = 0)
{
    public const int MaxText = 128;
    public (int, int, int) Key => (Type, Id, Nth);
    public bool IsValid => Selection.Length <= MaxText && !Selection.Any(char.IsControl) && Current.Length <= MaxText && !Current.Any(char.IsControl) &&
        Type is >= 0 and <= 1024 && Theme is >= 0 and <= 1024 && DlcPack is >= 0 and <= 1024 && Id is >= -1 and <= 1_000_000 && Nth is >= 0 and <= 255;
}

/// <summary>The workshop light colour from the Customise Workshop app (<c>ThemeManager</c>): colour and pulse.</summary>
public readonly record struct RoomLight(float R, float G, float B, float A, bool Pulse)
{
    public bool IsValid => new[] { R, G, B, A }.All(f => float.IsFinite(f) && f is >= 0 and <= 100f);
}

/// <summary>A PC's shared picture: the game's wallpaper value it stands for (a file path on the chooser's machine, or
/// "USER_WALLPAPER" for their Windows wallpaper), the picture's SHA-256 and length, and whether its aspect is kept.</summary>
public sealed record WallpaperFact(NetObjectId Pc, string Name, byte[] Hash, int Length, bool Aspect)
{
    public const int MaxName = 1024;
    public string HashText => Convert.ToHexString(Hash);
}

/// <summary>Native side of phase 12. All of these run on both peers in host order, after the model changed.</summary>
public interface IWorkshopNative
{
    /// <summary>A player started or stopped decorating (the Customise Workshop app).</summary>
    void Decorating(in MutationContext ctx, ActorSlot actor, bool on);
    /// <summary>Changed decorations and, when it changed, the light colour (the sender's game already shows them).</summary>
    void Decorated(in MutationContext ctx, IReadOnlyList<DecorItem> items, RoomLight? light);
    /// <summary>The room lights (and ceiling fans) were switched on or off.</summary>
    void Lights(in MutationContext ctx, bool on);
    /// <summary>A seasonal decoration switch was used (the game's <c>DecorationID</c>).</summary>
    void Decoration(in MutationContext ctx, byte id);
    /// <summary>Host only, while validating: this game keeps a picture (or other content) with that SHA-256.</summary>
    bool HasContent(byte[] hash);
    /// <summary>Content arrived whole and hash-checked (a picture, an imported PC); keep it under its hash.</summary>
    void ContentArrived(in MutationContext ctx, byte[] hash, byte[] data);
    /// <summary>A PC's shared picture changed.</summary>
    void Wallpaper(in MutationContext ctx, WallpaperFact fact);
    /// <summary>The guest is missing that content; the host's game sends it.</summary>
    void ContentNeeded(in MutationContext ctx, byte[] hash);
    /// <summary>The workshop state came from a snapshot or a save (lights, seasonal decoration, season, pictures).</summary>
    void StateLoaded();
}

/// <summary>
/// Phase 12 (owner decisions, November 2, 2026): the workshop around the PCs.
/// <list type="bullet">
/// <item><b>Decorate</b>: one player decorates at a time ("Someone is decorating the workshop."), from opening the
/// Customise Workshop app to closing it. <b>Decor</b> carries the decorations that changed and the light colour once a
/// change has loaded; the other game applies them with the game's own restore.</item>
/// <item><b>Lights</b>: the room lights and ceiling fans. <b>Decoration</b>: the seasonal decoration switches. Both are
/// kept here (the game doesn't save the lights), so a joining guest gets them.</item>
/// <item><b>Season</b>: the host's season, in the state only; a guest's game uses it for its next workshop load.</item>
/// <item><b>Chunk</b>/<b>Content</b>: a picture (or an imported PC) in ordered chunks with its length and SHA-256; both
/// games keep it under its hash. <b>Wallpaper</b> sets a PC's shared picture, once the host has it. <b>Need</b>: a guest
/// asks the host for content it doesn't have (it joined later, or missed the start).</item>
/// </list>
/// </summary>
public sealed class WorkshopWorld : ICommandHandler, IWorldState
{
    private enum Step : byte { Decorate = 1, Decor = 2, Lights = 3, Decoration = 4, Chunk = 5, Content = 6, Wallpaper = 7, Need = 8 }
    public const int MaxDecorItems = 100, MaxChunkBytes = 15 * 1024, MaxContentBytes = 8 * 1024 * 1024, MaxChunks = MaxContentBytes / MaxChunkBytes + 1,
        HashBytes = 32, MaxWallpapers = 512;
    public const string Busy = "Someone is decorating the workshop.";
    /// <summary>The game's <c>DecorationID</c>: default, Halloween, Christmas.</summary>
    public const byte MaxDecoration = 3;
    private const byte StateVersion = 1;

    private readonly ObjectWorld _objects;
    private readonly ActivityWorld _activity;
    private readonly IWorkshopNative _native;
    private readonly Dictionary<ActorSlot, (ulong Epoch, int Total, List<byte[]> Chunks)> _chunks = new();
    private readonly Dictionary<NetObjectId, WallpaperFact> _wallpapers = new();
    private (ActorSlot Actor, ulong Epoch)? _decorator;

    public WorkshopWorld(ObjectWorld objects, ActivityWorld activity, IWorkshopNative native)
    { _objects = objects; _activity = activity; _native = native; }
    public string Name => "workshop";

    /// <summary>The room lights: null until someone switches them in this world.</summary>
    public bool? LightsOn { get; private set; }
    /// <summary>The active seasonal decoration (the game's <c>DecorationID</c>), or 0 until someone switches it.</summary>
    public byte ActiveDecoration { get; private set; }
    /// <summary>The host's season (the game's <c>Season</c> + 1), or 0 when unknown. Set by the host's game.</summary>
    public byte Season { get; set; }
    /// <summary>The player decorating now, if they're still in the session with the same epoch.</summary>
    public ActorSlot? Decorator => _decorator is { } d && _activity.Get(d.Actor)?.Epoch == d.Epoch ? d.Actor : null;
    public IEnumerable<WallpaperFact> Wallpapers => _wallpapers.Values.Where(w => _objects.Pcs.Any(p => p.Id == w.Pc));
    public WallpaperFact? WallpaperOf(NetObjectId pc) => _wallpapers.TryGetValue(pc, out var w) && _objects.Pcs.Any(p => p.Id == pc) ? w : null;
    /// <summary>Why <paramref name="actor"/> can't decorate now, or null.</summary>
    public string? DecorBusy(ActorSlot actor) => Decorator is { } d && d != actor ? Busy : null;

    public void Reset() { _chunks.Clear(); _wallpapers.Clear(); _decorator = null; LightsOn = null; ActiveDecoration = 0; Season = 0; }

    // ---------------- requests ----------------

    private static byte[] Encode(Action<NetDataWriter> f) { var w = new NetDataWriter(); f(w); return w.CopyData(); }
    public static byte[] Decorate(ulong epoch, bool on) => Encode(w => { w.Put((byte)Step.Decorate); w.Put(epoch); w.Put(on); });
    public static byte[] Decor(ulong epoch, IReadOnlyList<DecorItem> items, RoomLight? light) => Encode(w =>
    {
        if (items.Count > MaxDecorItems) throw new ArgumentOutOfRangeException(nameof(items));
        w.Put((byte)Step.Decor); w.Put(epoch); w.Put((byte)items.Count);
        foreach (var i in items)
        {
            w.Put(i.Selection ?? "", DecorItem.MaxText); w.Put(i.Id); w.Put(i.Current ?? "", DecorItem.MaxText); w.Put(i.Type); w.Put(i.Theme); w.Put(i.Dlc); w.Put(i.DlcPack);
            w.Put((byte)i.Nth);
        }
        w.Put(light.HasValue);
        if (light is { } l) { w.Put(l.R); w.Put(l.G); w.Put(l.B); w.Put(l.A); w.Put(l.Pulse); }
    });
    public static byte[] Lights(ulong epoch, bool on) => Encode(w => { w.Put((byte)Step.Lights); w.Put(epoch); w.Put(on); });
    public static byte[] Decoration(ulong epoch, byte id) => Encode(w => { w.Put((byte)Step.Decoration); w.Put(epoch); w.Put(id); });
    public static byte[] Chunk(ulong epoch, int index, int total, byte[] data) => Encode(w =>
    { w.Put((byte)Step.Chunk); w.Put(epoch); w.Put(index); w.Put(total); Protocol.WriteBlob(w, data); });
    public static byte[] Content(ulong epoch, byte[] data) => Encode(w =>
    { w.Put((byte)Step.Content); w.Put(epoch); w.Put(data.Length); Protocol.WriteBlob(w, SHA256.HashData(data)); });
    public static byte[] Wallpaper(ulong epoch, NetObjectId pc, string name, byte[] hash, int length, bool aspect) => Encode(w =>
    {
        w.Put((byte)Step.Wallpaper); w.Put(epoch); pc.Write(w); w.Put(name, WallpaperFact.MaxName); Protocol.WriteBlob(w, hash); w.Put(length); w.Put(aspect);
    });
    public static byte[] Need(ulong epoch, byte[] hash) => Encode(w => { w.Put((byte)Step.Need); w.Put(epoch); Protocol.WriteBlob(w, hash); });

    /// <summary>Split content into the chunk and content requests that send it.</summary>
    public static List<byte[]> Send(ulong epoch, byte[] data)
    {
        if (data.Length is < 1 or > MaxContentBytes) throw new ArgumentOutOfRangeException(nameof(data));
        var total = (data.Length + MaxChunkBytes - 1) / MaxChunkBytes;
        var result = new List<byte[]>(total + 1);
        for (var i = 0; i < total; i++) result.Add(Chunk(epoch, i, total, data.AsSpan(i * MaxChunkBytes, Math.Min(MaxChunkBytes, data.Length - i * MaxChunkBytes)).ToArray()));
        result.Add(Content(epoch, data));
        return result;
    }

    // ---------------- parsing and checks ----------------

    private sealed record Command(Step Step, ulong Epoch)
    {
        public bool On { get; init; }
        public IReadOnlyList<DecorItem> Items { get; init; } = Array.Empty<DecorItem>();
        public RoomLight? Light { get; init; }
        public byte Id { get; init; }
        public int Index { get; init; }
        public int Total { get; init; }
        public byte[] Data { get; init; } = Array.Empty<byte>();
        public int Length { get; init; }
        public byte[] Hash { get; init; } = Array.Empty<byte>();
        public NetObjectId Pc { get; init; }
        public string Name { get; init; } = "";
    }

    private static byte[] ReadHash(NetDataReader r)
    {
        var hash = Protocol.ReadBlob(r, HashBytes, out var declared);
        if (hash == null || declared != HashBytes) throw new FormatException("Invalid content hash.");
        return hash;
    }

    private static Command Parse(NetDataReader r)
    {
        var step = (Step)r.GetByte(); var epoch = r.GetULong();
        var c = new Command(step, epoch);
        switch (step)
        {
            case Step.Decorate: case Step.Lights: c = c with { On = r.GetBool() }; break;
            case Step.Decor:
            {
                int count = r.GetByte();
                if (count > MaxDecorItems) throw new FormatException("Too many decorations.");
                var items = new DecorItem[count];
                for (var i = 0; i < count; i++)
                {
                    items[i] = new DecorItem(r.GetString(DecorItem.MaxText), r.GetInt(), r.GetString(DecorItem.MaxText), r.GetInt(), r.GetInt(), r.GetBool(), r.GetInt(), r.GetByte());
                    if (!items[i].IsValid) throw new FormatException("Invalid decoration.");
                }
                RoomLight? light = null;
                if (r.GetBool())
                {
                    light = new RoomLight(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetBool());
                    if (!light.Value.IsValid) throw new FormatException("Invalid light colour.");
                }
                c = c with { Items = items, Light = light };
                break;
            }
            case Step.Decoration: c = c with { Id = r.GetByte() }; break;
            case Step.Chunk:
            {
                var index = r.GetInt(); var total = r.GetInt();
                var data = Protocol.ReadBlob(r, MaxChunkBytes, out _) ?? throw new FormatException("Invalid content chunk.");
                c = c with { Index = index, Total = total, Data = data };
                break;
            }
            case Step.Content:
            {
                var length = r.GetInt();
                if (length is < 1 or > MaxContentBytes) throw new FormatException("Invalid content length.");
                c = c with { Length = length, Hash = ReadHash(r) };
                break;
            }
            case Step.Wallpaper:
            {
                var pc = NetObjectId.Read(r); var name = r.GetString(WallpaperFact.MaxName); var hash = ReadHash(r); var length = r.GetInt();
                if (name.Length == 0 || name.Any(char.IsControl) || length is < 1 or > MaxContentBytes) throw new FormatException("Invalid picture.");
                c = c with { Pc = pc, Name = name, Hash = hash, Length = length, On = r.GetBool() };
                break;
            }
            case Step.Need: c = c with { Hash = ReadHash(r) }; break;
            default: throw new FormatException("Unknown workshop step.");
        }
        if (r.AvailableBytes != 0) throw new FormatException("Trailing workshop bytes.");
        return c;
    }

    private byte[]? Assembled(ActorSlot actor, Command c)
    {
        if (!_chunks.TryGetValue(actor, out var g) || g.Epoch != c.Epoch || g.Chunks.Count != g.Total) return null;
        var data = new byte[g.Chunks.Sum(x => x.Length)];
        var at = 0;
        foreach (var chunk in g.Chunks) { chunk.CopyTo(data, at); at += chunk.Length; }
        return data.Length == c.Length && SHA256.HashData(data).AsSpan().SequenceEqual(c.Hash) ? data : null;
    }

    private string? Check(ActorSlot requester, Command c, bool host)
    {
        if (_activity.Get(requester) is not { } actor || actor.Epoch != c.Epoch) return "Actor presence changed.";
        switch (c.Step)
        {
            case Step.Decorate:
                if (c.On) return DecorBusy(requester);
                return Decorator == requester ? null : "You're not decorating the workshop.";
            case Step.Decor:
                return Decorator == requester ? null : Decorator == null ? "Open the Customise Workshop app to decorate." : Busy;
            case Step.Lights: return null;
            case Step.Decoration: return c.Id is >= 1 and <= MaxDecoration ? null : "Unknown seasonal decoration.";
            case Step.Chunk:
            {
                if (c.Total is < 1 or > MaxChunks || c.Data.Length is < 1 or > MaxChunkBytes) return "Invalid content chunk.";
                if (c.Index == 0) return null; // new content starts over
                var got = _chunks.TryGetValue(requester, out var g) && g.Epoch == c.Epoch ? g : (c.Epoch, 0, new List<byte[]>());
                if (c.Total != got.Item2 || c.Index != got.Item3.Count) return "Content chunk out of order.";
                if (got.Item3.Sum(x => x.Length) + c.Data.Length > MaxContentBytes) return "The picture is too large (the limit is 8 MB).";
                return null;
            }
            case Step.Content:
                return Assembled(requester, c) != null ? null : "The content hasn't fully arrived or doesn't match.";
            case Step.Wallpaper:
                if (_objects.Pcs.All(p => p.Id != c.Pc)) return "That PC isn't in the shared world.";
                if (_wallpapers.Count >= MaxWallpapers && !_wallpapers.ContainsKey(c.Pc)) return "Too many shared pictures.";
                return !host || _native.HasContent(c.Hash) ? null : "The host doesn't have that picture.";
            default:
                if (requester != ActorSlot.Guest) return "Only a guest asks for missing content.";
                return !host || _native.HasContent(c.Hash) ? null : "The host doesn't have that content.";
        }
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var error = Check(ctx.Op.Actor, Parse(payload), host: true);
        return error == null ? CommandVerdict.Accept : CommandVerdict.Reject(error);
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        var actor = ctx.Op.Actor;
        if (_activity.Get(actor)?.Epoch != c.Epoch) throw new InvalidDataException("Workshop change no longer applies: actor presence changed.");
        switch (c.Step)
        {
            case Step.Decorate:
                if (c.On) _decorator = (actor, c.Epoch);
                else if (_decorator?.Actor == actor) _decorator = null;
                _native.Decorating(ctx, actor, c.On);
                return;
            case Step.Decor:
                // A guest that joined while the host decorated never saw the claim (it's in the snapshot only from then on): adopt it.
                if (Decorator == null) _decorator = (actor, c.Epoch);
                _native.Decorated(ctx, c.Items, c.Light);
                return;
            case Step.Lights:
                LightsOn = c.On;
                _native.Lights(ctx, c.On);
                return;
            case Step.Decoration:
                ActiveDecoration = c.Id;
                _native.Decoration(ctx, c.Id);
                return;
            case Step.Chunk:
                // A guest that joined halfway through content can't assemble it; it asks for it later.
                if (c.Index == 0) _chunks[actor] = (c.Epoch, c.Total, new List<byte[]> { c.Data });
                else if (_chunks.TryGetValue(actor, out var g) && g.Epoch == c.Epoch && g.Chunks.Count == c.Index && g.Total == c.Total) g.Chunks.Add(c.Data);
                return;
            case Step.Content:
            {
                var data = Assembled(actor, c);
                _chunks.Remove(actor);
                if (data != null) _native.ContentArrived(ctx, c.Hash, data);
                return;
            }
            case Step.Wallpaper:
            {
                if (_objects.Pcs.All(p => p.Id != c.Pc)) throw new InvalidDataException("Workshop change no longer applies: the PC is gone.");
                var fact = new WallpaperFact(c.Pc, c.Name, c.Hash, c.Length, c.On);
                _wallpapers[c.Pc] = fact;
                _native.Wallpaper(ctx, fact);
                return;
            }
            default:
                _native.ContentNeeded(ctx, c.Hash);
                return;
        }
    }

    // ---------------- state ----------------

    public void Write(NetDataWriter w)
    {
        w.Put(StateVersion);
        w.Put((byte)(LightsOn == null ? 0 : LightsOn.Value ? 2 : 1));
        w.Put(ActiveDecoration); w.Put(Season);
        var d = Decorator; w.Put((byte)(d ?? 0)); w.Put(d == null ? 0UL : _decorator!.Value.Epoch);
        var list = Wallpapers.OrderBy(x => x.Pc.Value).ToList();
        w.Put(list.Count);
        foreach (var x in list) { x.Pc.Write(w); w.Put(x.Name, WallpaperFact.MaxName); Protocol.WriteBlob(w, x.Hash); w.Put(x.Length); w.Put(x.Aspect); }
    }

    public void Read(NetDataReader r)
    {
        Reset();
        if (r.GetByte() != StateVersion) throw new InvalidDataException("Unknown workshop state version.");
        var lights = r.GetByte();
        if (lights > 2) throw new InvalidDataException("Invalid light state.");
        LightsOn = lights == 0 ? null : lights == 2;
        ActiveDecoration = r.GetByte(); Season = r.GetByte();
        if (ActiveDecoration > MaxDecoration || Season > 8) throw new InvalidDataException("Invalid workshop state.");
        var actor = (ActorSlot)r.GetByte(); var epoch = r.GetULong();
        if (actor != 0) _decorator = (actor, epoch);
        var n = r.GetInt();
        if (n is < 0 or > MaxWallpapers) throw new InvalidDataException("Invalid picture count.");
        for (var i = 0; i < n; i++)
        {
            var pc = NetObjectId.Read(r); var name = r.GetString(WallpaperFact.MaxName); var hash = ReadHash(r); var length = r.GetInt(); var aspect = r.GetBool();
            if (pc.IsNone || name.Length == 0 || length is < 1 or > MaxContentBytes) throw new InvalidDataException("Invalid shared picture.");
            _wallpapers[pc] = new WallpaperFact(pc, name, hash, length, aspect);
        }
        _native.StateLoaded();
    }
}

using System.Security.Cryptography;
using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>Where a set of lights belongs: the PC's live lighting, one of its saved presets, or a peripheral at the
/// PC's workstation.</summary>
public enum LightTarget : byte { Pc = 1, Preset = 2, Peripheral = 3 }

/// <summary>Everything the game saves about one light (<c>LEDConfig</c>): colours, effect and its options. Enums are the
/// game's values.</summary>
public readonly record struct LedValues(
    float R, float G, float B, float R2, float G2, float B2, byte Effect, float Speed, float SpeedModifier, float Offset, float Modulo,
    int Flow, bool AllowColour, bool AllowEffect, byte Direction, byte AnimType, byte ColourMode, byte MusicMode, byte TemperatureMode,
    float FlowSpread, bool Enabled, bool Shown)
{
    /// <summary>The game's <c>LEDEffect</c> values below COUNT, and the option enums' ranges.</summary>
    public const byte MaxEffect = 6;

    public bool IsValid =>
        new[] { R, G, B, R2, G2, B2, Speed, SpeedModifier, Offset, Modulo, FlowSpread }.All(f => float.IsFinite(f) && MathF.Abs(f) <= 1_000_000f) &&
        Effect <= MaxEffect && Direction <= 2 && AnimType <= 2 && ColourMode <= 1 && MusicMode <= 1 && TemperatureMode <= 2;

    public void Write(NetDataWriter w)
    {
        w.Put(R); w.Put(G); w.Put(B); w.Put(R2); w.Put(G2); w.Put(B2); w.Put(Effect); w.Put(Speed); w.Put(SpeedModifier); w.Put(Offset);
        w.Put(Modulo); w.Put(Flow); w.Put(AllowColour); w.Put(AllowEffect); w.Put(Direction); w.Put(AnimType); w.Put(ColourMode);
        w.Put(MusicMode); w.Put(TemperatureMode); w.Put(FlowSpread); w.Put(Enabled); w.Put(Shown);
    }

    public static LedValues Read(NetDataReader r) => new(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat(),
        r.GetByte(), r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetInt(), r.GetBool(), r.GetBool(), r.GetByte(), r.GetByte(),
        r.GetByte(), r.GetByte(), r.GetByte(), r.GetFloat(), r.GetBool(), r.GetBool());
}

/// <summary>One light: the game's light group key (<c>LEDDriverConfig</c> dictionary key) and its place in that group.</summary>
public sealed record LedLight(string Group, int Index, LedValues Values)
{
    public const int MaxIndex = 255;
    public bool IsValid => !string.IsNullOrEmpty(Group) && Group.Length <= Protocol.MaxShortString && !Group.Any(char.IsControl) &&
        Index is >= 0 and <= MaxIndex && Values.IsValid;
}

/// <summary>Native side of phase 11's lighting and purchases. Runs on both peers in host order.</summary>
public interface ICustomizationNative
{
    /// <summary>Accepted lights of a PC (the sender's game already shows them). <paramref name="preset"/>: the current
    /// preset index for <see cref="LightTarget.Pc"/>, the preset for <see cref="LightTarget.Preset"/>.
    /// <paramref name="slot"/>: the peripheral slot for <see cref="LightTarget.Peripheral"/>.</summary>
    void LightsChanged(in MutationContext ctx, PcFact pc, LightTarget target, int preset, string slot, IReadOnlyList<LedLight> lights);
    /// <summary>The host's lighting clock of a powered PC (seconds since its lights came on).</summary>
    void Clock(in MutationContext ctx, PcFact pc, float time);
    /// <summary>A player bought something that adds new kinds of part (a custom cable set): the new part kinds and items,
    /// compressed and hash-checked; the buyer's game already has them.</summary>
    void Purchased(in MutationContext ctx, ActorSlot actor, byte[] result);
}

/// <summary>
/// Phase 11 (owner decisions, November 2, 2026): what a player sets on a PC's look that isn't a table session.
/// <list type="bullet">
/// <item><b>Lights</b>: the Lighting app's settings of a PC, its presets and its workstation's peripherals, shared after
/// each change settles; the last change wins. Lights are matched one by one, so a large set can come in several
/// commands.</item>
/// <item><b>Clock</b>: the host's lighting clock of a powered PC, so breathe, rainbow, strobe and flow effects run in step.</item>
/// <item><b>Chunk</b>/<b>Purchase</b>: a bought custom cable set (new part kinds and inventory items), in ordered chunks
/// with the length and SHA-256 at the end.</item>
/// </list>
/// Nothing here is in snapshots: lights live in the PC's software record and the workstation save (the host's save
/// carries them to a joining guest), and the clock is sent again when a guest arrives.
/// </summary>
public sealed class CustomizationWorld : ICommandHandler
{
    private enum Step : byte { Lights = 1, Clock = 2, Chunk = 3, Purchase = 4 }
    public const int MaxLights = 128, MaxPresets = 8, MaxChunkBytes = 15 * 1024, MaxBytes = 1024 * 1024, MaxChunks = MaxBytes / MaxChunkBytes + 1;

    private readonly ObjectWorld _objects;
    private readonly ActivityWorld _activity;
    private readonly ICustomizationNative _native;
    private readonly Dictionary<ActorSlot, (ulong Epoch, int Total, List<byte[]> Chunks)> _chunks = new();

    public CustomizationWorld(ObjectWorld objects, ActivityWorld activity, ICustomizationNative native)
    { _objects = objects; _activity = activity; _native = native; }
    public string Name => "customization";
    public void Reset() => _chunks.Clear();

    private static byte[] Encode(Action<NetDataWriter> f) { var w = new NetDataWriter(); f(w); return w.CopyData(); }

    public static byte[] Lights(ulong epoch, NetObjectId pc, LightTarget target, int preset, string slot, IReadOnlyList<LedLight> lights) => Encode(w =>
    {
        if (lights.Count > MaxLights) throw new ArgumentOutOfRangeException(nameof(lights));
        w.Put((byte)Step.Lights); w.Put(epoch); pc.Write(w); w.Put((byte)target); w.Put((sbyte)preset); w.Put(slot ?? "", Protocol.MaxShortString);
        w.Put((byte)lights.Count);
        foreach (var l in lights) { w.Put(l.Group, Protocol.MaxShortString); w.Put((byte)l.Index); l.Values.Write(w); }
    });
    public static byte[] Clock(ulong epoch, NetObjectId pc, float time) => Encode(w => { w.Put((byte)Step.Clock); w.Put(epoch); pc.Write(w); w.Put(time); });
    public static byte[] Chunk(ulong epoch, int index, int total, byte[] data) => Encode(w =>
    { w.Put((byte)Step.Chunk); w.Put(epoch); w.Put(index); w.Put(total); Protocol.WriteBlob(w, data); });
    public static byte[] Purchase(ulong epoch, byte[] result) => Encode(w =>
    { w.Put((byte)Step.Purchase); w.Put(epoch); w.Put(result.Length); Protocol.WriteBlob(w, SHA256.HashData(result)); });

    private sealed record Command(Step Step, ulong Epoch, NetObjectId Pc, LightTarget Target, int Preset, string Slot, IReadOnlyList<LedLight> Lights,
        float Time, int Index, int Total, byte[] Data, int Length, byte[] Hash);

    private static Command Parse(NetDataReader r)
    {
        var step = (Step)r.GetByte(); var epoch = r.GetULong();
        Command c;
        switch (step)
        {
            case Step.Lights:
            {
                var pc = NetObjectId.Read(r); var target = (LightTarget)r.GetByte(); int preset = r.GetSByte(); var slot = r.GetString(Protocol.MaxShortString);
                int count = r.GetByte();
                if (count > MaxLights) throw new FormatException("Too many lights.");
                var lights = new LedLight[count];
                for (var i = 0; i < count; i++)
                {
                    lights[i] = new LedLight(r.GetString(Protocol.MaxShortString), r.GetByte(), LedValues.Read(r));
                    if (!lights[i].IsValid) throw new FormatException("Invalid light.");
                }
                c = new(step, epoch, pc, target, preset, slot, lights, 0, 0, 0, Array.Empty<byte>(), 0, Array.Empty<byte>());
                break;
            }
            case Step.Clock:
            {
                var pc = NetObjectId.Read(r); var time = r.GetFloat();
                if (!float.IsFinite(time) || time is < 0 or > 10_000_000f) throw new FormatException("Invalid lighting clock.");
                c = new(step, epoch, pc, default, 0, "", Array.Empty<LedLight>(), time, 0, 0, Array.Empty<byte>(), 0, Array.Empty<byte>());
                break;
            }
            case Step.Chunk:
            {
                var index = r.GetInt(); var total = r.GetInt();
                var data = Protocol.ReadBlob(r, MaxChunkBytes, out _) ?? throw new FormatException("Invalid purchase chunk.");
                c = new(step, epoch, default, default, 0, "", Array.Empty<LedLight>(), 0, index, total, data, 0, Array.Empty<byte>());
                break;
            }
            case Step.Purchase:
            {
                var length = r.GetInt();
                var hash = Protocol.ReadBlob(r, 32, out var declared) ?? throw new FormatException("Invalid purchase hash.");
                if (length is < 1 or > MaxBytes || declared != 32) throw new FormatException("Invalid purchase.");
                c = new(step, epoch, default, default, 0, "", Array.Empty<LedLight>(), 0, 0, 0, Array.Empty<byte>(), length, hash);
                break;
            }
            default: throw new FormatException("Unknown customization step.");
        }
        if (r.AvailableBytes != 0) throw new FormatException("Trailing customization bytes.");
        return c;
    }

    private string? Check(ActorSlot requester, Command c)
    {
        if (_activity.Get(requester) is not { } actor || actor.Epoch != c.Epoch) return "Actor presence changed.";
        switch (c.Step)
        {
            case Step.Lights:
                if (_objects.Pcs.All(p => p.Id != c.Pc)) return "That PC isn't in the shared world.";
                if (!Enum.IsDefined(c.Target)) return "Unknown light target.";
                if (c.Target == LightTarget.Preset ? c.Preset is < 0 or >= MaxPresets : c.Preset is < -1 or >= MaxPresets) return "Unknown lighting preset.";
                if (c.Target == LightTarget.Peripheral ? c.Slot.Length == 0 || c.Slot.Any(char.IsControl) : c.Slot.Length != 0) return "Unknown peripheral.";
                return null;
            case Step.Clock:
                if (requester != ActorSlot.Host) return "Only the host keeps the lighting clock.";
                return _objects.Pcs.Any(p => p.Id == c.Pc) ? null : "That PC isn't in the shared world.";
            case Step.Chunk:
            {
                var got = _chunks.TryGetValue(requester, out var g) && g.Epoch == c.Epoch ? g : (c.Epoch, 0, new List<byte[]>());
                if (c.Total is < 1 or > MaxChunks || c.Data.Length is < 1 or > MaxChunkBytes) return "Invalid purchase chunk.";
                if (c.Index == 0) return null; // a new purchase starts over
                if (c.Total != got.Item2 || c.Index != got.Item3.Count) return "Purchase chunk out of order.";
                if (got.Item3.Sum(x => x.Length) + c.Data.Length > MaxBytes) return "The purchase is too large.";
                return null;
            }
            default:
            {
                if (!_chunks.TryGetValue(requester, out var g) || g.Epoch != c.Epoch || g.Chunks.Count != g.Total) return "The purchase hasn't fully arrived.";
                var data = g.Chunks.SelectMany(x => x).ToArray();
                return data.Length == c.Length && SHA256.HashData(data).AsSpan().SequenceEqual(c.Hash) ? null : "The purchase doesn't match.";
            }
        }
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var error = Check(ctx.Op.Actor, Parse(payload));
        return error == null ? CommandVerdict.Accept : CommandVerdict.Reject(error);
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        if (_activity.Get(ctx.Op.Actor)?.Epoch != c.Epoch) throw new InvalidDataException("Customization no longer applies: actor presence changed.");
        switch (c.Step)
        {
            case Step.Lights:
            case Step.Clock:
            {
                var pc = _objects.Pcs.SingleOrDefault(p => p.Id == c.Pc) ?? throw new InvalidDataException("Customization no longer applies: the PC is gone.");
                if (c.Step == Step.Lights) _native.LightsChanged(ctx, pc, c.Target, c.Preset, c.Slot, c.Lights);
                else _native.Clock(ctx, pc, c.Time);
                return;
            }
            case Step.Chunk:
            {
                // A guest that joined halfway through someone's purchase can't assemble it; the host's save carries it.
                if (c.Index == 0) _chunks[ctx.Op.Actor] = (c.Epoch, c.Total, new List<byte[]> { c.Data });
                else if (_chunks.TryGetValue(ctx.Op.Actor, out var g) && g.Epoch == c.Epoch && g.Chunks.Count == c.Index) g.Chunks.Add(c.Data);
                return;
            }
            default:
            {
                byte[]? data = null;
                if (_chunks.TryGetValue(ctx.Op.Actor, out var g) && g.Epoch == c.Epoch && g.Chunks.Count == g.Total)
                {
                    data = g.Chunks.SelectMany(x => x).ToArray();
                    if (data.Length != c.Length || !SHA256.HashData(data).AsSpan().SequenceEqual(c.Hash)) data = null;
                }
                _chunks.Remove(ctx.Op.Actor);
                if (data != null) _native.Purchased(ctx, ctx.Op.Actor, data);
                return;
            }
        }
    }
}

using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>One fan's live control curve. A fan is its part's native path inside the PC plus its position among
/// that part's fans (a radiator or cooler can carry several). Preset and sensor are the game's enum values.</summary>
public sealed record FanCurve(string Path, int Index, bool Custom, int Preset, int Sensor, IReadOnlyList<(float X, float Y)>? Points)
{
    public const int MaxPoints = 64, MaxFansPerPart = 16;

    public bool IsValid => !string.IsNullOrEmpty(Path) && Path.Length <= Protocol.MaxShortString && !Path.Any(char.IsControl) &&
        Index is >= 0 and < MaxFansPerPart && Preset is >= 0 and <= 16 && Sensor is >= 0 and <= 16 &&
        (Points == null || (Points.Count <= MaxPoints && Points.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y) &&
            MathF.Abs(p.X) <= 100_000f && MathF.Abs(p.Y) <= 100_000f)));

    public void Write(NetDataWriter w)
    {
        w.Put(Path, Protocol.MaxShortString); w.Put((byte)Index); w.Put(Custom); w.Put((byte)Preset); w.Put((byte)Sensor);
        w.Put(Points != null);
        if (Points == null) return;
        w.Put((byte)Points.Count);
        foreach (var (x, y) in Points) { w.Put(x); w.Put(y); }
    }

    public static FanCurve Read(NetDataReader r)
    {
        var path = r.GetString(Protocol.MaxShortString); int index = r.GetByte(); var custom = r.GetBool();
        int preset = r.GetByte(); int sensor = r.GetByte();
        (float, float)[]? points = null;
        if (r.GetBool())
        {
            int count = r.GetByte();
            if (count > MaxPoints) throw new FormatException("Too many fan curve points.");
            points = new (float, float)[count];
            for (var i = 0; i < count; i++) points[i] = (r.GetFloat(), r.GetFloat());
        }
        var curve = new FanCurve(path, index, custom, preset, sensor, points);
        if (!curve.IsValid) throw new FormatException("Invalid fan curve.");
        return curve;
    }

    public bool SameAs(FanCurve other) => Path == other.Path && Index == other.Index && Custom == other.Custom &&
        Preset == other.Preset && Sensor == other.Sensor &&
        (Points == null ? other.Points == null : other.Points != null && Points.SequenceEqual(other.Points));
}

public interface ISimulationNative
{
    /// <summary>The host's game had this PC fail (both peers; the host's game already shows it).</summary>
    void Failed(in MutationContext ctx, PcFact pc, int reason);
    /// <summary>Accepted fan curves of one PC (both peers; the sender's game already has them).</summary>
    void FansChanged(in MutationContext ctx, PcFact pc, IReadOnlyList<FanCurve> fans);
}

/// <summary>Outcomes of the game's PC simulation (phase 7). Each game simulates every powered PC itself from the same
/// mirrored app input; this command shares what must happen once: the host's failures (blue screens) and fan curves
/// applied in the Fan Control app. Nothing here is saved: the game saves neither its simulation nor Free Build fan curves.</summary>
public sealed class SimulationWorld : ICommandHandler
{
    private enum Step : byte { Failure = 1, Fans = 2 }
    public const int MaxFans = 64;
    /// <summary>The game's <c>EmergencyShutdownReason</c> values that cause a failure (NO_SHUTDOWN = 0 is not one).</summary>
    public const int MinReason = 1, MaxReason = 9;

    private readonly ObjectWorld _objects;
    private readonly ActivityWorld _activity;
    private readonly ISimulationNative _native;

    public SimulationWorld(ObjectWorld objects, ActivityWorld activity, ISimulationNative native)
    { _objects = objects; _activity = activity; _native = native; }
    public string Name => "simulation";

    private static byte[] Encode(Action<NetDataWriter> f) { var w = new NetDataWriter(); f(w); return w.CopyData(); }

    public static byte[] Failure(ulong epoch, NetObjectId pc, int reason)
    {
        if (reason is < MinReason or > MaxReason) throw new ArgumentOutOfRangeException(nameof(reason));
        return Encode(w => { w.Put((byte)Step.Failure); w.Put(epoch); pc.Write(w); w.Put((byte)reason); });
    }

    public static byte[] Fans(ulong epoch, NetObjectId pc, IReadOnlyList<FanCurve> fans)
    {
        if (fans.Count is < 1 or > MaxFans || fans.Any(f => !f.IsValid)) throw new ArgumentException("Invalid fan curves.");
        return Encode(w =>
        {
            w.Put((byte)Step.Fans); w.Put(epoch); pc.Write(w); w.Put((byte)fans.Count);
            foreach (var f in fans) f.Write(w);
        });
    }

    private sealed record Command(Step Step, ulong Epoch, NetObjectId Pc, int Reason, IReadOnlyList<FanCurve> Fans);

    private static Command Parse(NetDataReader r)
    {
        var step = (Step)r.GetByte(); var epoch = r.GetULong(); var pc = NetObjectId.Read(r);
        Command c;
        switch (step)
        {
            case Step.Failure:
                int reason = r.GetByte();
                if (reason is < MinReason or > MaxReason) throw new FormatException("Invalid failure reason.");
                c = new(step, epoch, pc, reason, Array.Empty<FanCurve>());
                break;
            case Step.Fans:
                int count = r.GetByte();
                if (count is < 1 or > MaxFans) throw new FormatException("Invalid fan count.");
                var fans = new FanCurve[count];
                for (var i = 0; i < count; i++) fans[i] = FanCurve.Read(r);
                if (fans.Select(f => (f.Path, f.Index)).Distinct().Count() != count) throw new FormatException("Duplicate fan.");
                c = new(step, epoch, pc, 0, fans);
                break;
            default: throw new FormatException("Unknown simulation step.");
        }
        if (r.AvailableBytes != 0 || pc.IsNone) throw new FormatException("Invalid simulation payload.");
        return c;
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        if (_activity.Get(ctx.Op.Actor) is not { } actor || actor.Epoch != c.Epoch) return CommandVerdict.Reject("Actor presence changed.");
        var pc = _objects.Pcs.SingleOrDefault(p => p.Id == c.Pc);
        if (pc == null || pc.Location.Place != PcPlace.Bench) return CommandVerdict.Reject("That PC is no longer on a bench.");
        if (c.Step == Step.Failure)
            return ctx.Op.Actor == ActorSlot.Host ? CommandVerdict.Accept : CommandVerdict.Reject("Only the host's game decides PC failures.");
        // The host may refresh any PC's curves (late join); a guest changes them from that PC's monitor.
        if (ctx.Op.Actor != ActorSlot.Host && actor.WorkingPc != pc.Id) return CommandVerdict.Reject("You're not using that PC's monitor.");
        var paths = pc.Links.Select(l => l.Path).ToHashSet(StringComparer.Ordinal);
        var unknown = c.Fans.FirstOrDefault(f => !paths.Contains(f.Path));
        return unknown == null ? CommandVerdict.Accept : CommandVerdict.Reject($"Unknown fan part {unknown.Path}.");
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
        var pc = _objects.Pcs.SingleOrDefault(p => p.Id == c.Pc) ?? throw new InvalidDataException("Simulation PC disappeared.");
        if (c.Step == Step.Failure) _native.Failed(ctx, pc, c.Reason);
        else _native.FansChanged(ctx, pc, c.Fans);
    }
}

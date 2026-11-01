using System.Security.Cryptography;
using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>The editable, PC-owned BIOS values. BootPath is a canonical native part path inside this PC,
/// not a native pointer or catalog SKU. Native-only hardware caches stay with the game's save record.</summary>
public sealed record BiosSettings(int BaseClock, float CpuMultiplier, float CpuVoltage, int RamClock, float RamVoltage,
    IReadOnlyList<int> RamTimings, bool Xmp, int GpuClock, int GpuMemClock, int GpuVoltagePercent,
    int GpuClockLast, int GpuMemClockLast, int GpuVoltLast, string BootPath, bool OsInstallFlag)
{
    public const int MaxTimings = 8;
    public bool IsValid => BaseClock is >= 1 and <= 1000 && float.IsFinite(CpuMultiplier) && CpuMultiplier is >= 1 and <= 200 &&
        float.IsFinite(CpuVoltage) && CpuVoltage is >= 0 and <= 5 && RamClock is >= 1 and <= 20000 &&
        float.IsFinite(RamVoltage) && RamVoltage is >= 0 and <= 5 && RamTimings != null && RamTimings.Count is >= 1 and <= MaxTimings &&
        RamTimings.All(x => x is >= 0 and <= 255) && GpuClock is >= 0 and <= 20000 && GpuMemClock is >= 0 and <= 20000 &&
        GpuVoltagePercent is >= 0 and <= 300 && GpuClockLast is >= 0 and <= 20000 && GpuMemClockLast is >= 0 and <= 20000 &&
        GpuVoltLast is >= 0 and <= 300 && BootPath != null && BootPath.Length <= 1024 && !BootPath.Any(char.IsControl);

    public void Write(NetDataWriter w)
    {
        w.Put(BaseClock); w.Put(CpuMultiplier); w.Put(CpuVoltage); w.Put(RamClock); w.Put(RamVoltage);
        w.Put((byte)RamTimings.Count); foreach (var timing in RamTimings) w.Put(timing);
        w.Put(Xmp); w.Put(GpuClock); w.Put(GpuMemClock); w.Put(GpuVoltagePercent);
        w.Put(GpuClockLast); w.Put(GpuMemClockLast); w.Put(GpuVoltLast);
        w.Put(BootPath, 1025); w.Put(OsInstallFlag);
    }

    public static BiosSettings Read(NetDataReader r)
    {
        var bclk = r.GetInt(); var cpu = r.GetFloat(); var cv = r.GetFloat(); var ram = r.GetInt(); var rv = r.GetFloat();
        var count = r.GetByte();
        if (count is < 1 or > MaxTimings) throw new FormatException("Invalid BIOS timing count.");
        var timings = new int[count]; for (var i = 0; i < count; i++) timings[i] = r.GetInt();
        var result = new BiosSettings(bclk, cpu, cv, ram, rv, timings, r.GetBool(), r.GetInt(), r.GetInt(), r.GetInt(),
            r.GetInt(), r.GetInt(), r.GetInt(), r.GetString(1025), r.GetBool());
        if (!result.IsValid) throw new FormatException("Invalid BIOS values.");
        return result;
    }

    public string Digest()
    {
        var w = new NetDataWriter(); Write(w);
        return Convert.ToHexString(SHA256.HashData(w.CopyData()));
    }
}

public sealed record BiosChange(ulong Epoch, NetObjectId Pc, string ExpectedDigest, bool Reset, BiosSettings? Settings);

/// <summary>Read-only host check and native projection for one accepted BIOS edit.</summary>
public interface IBiosNative
{
    string? CheckHost(in CommandContext ctx, PcFact pc, BiosChange change);
    void Changed(in MutationContext ctx, PcFact pc, BiosChange change);
}

/// <summary>BIOS apply or factory reset is ordered by the host. The native save already owns the BIOS graph;
/// late join obtains it from the host's exact native snapshot, then replays later accepted commands.</summary>
public sealed class BiosWorld : ICommandHandler
{
    private readonly ObjectWorld _objects;
    private readonly ActivityWorld _activity;
    private readonly IBiosNative _native;
    public BiosWorld(ObjectWorld objects, ActivityWorld activity, IBiosNative native)
    { _objects = objects; _activity = activity; _native = native; }
    public string Name => "bios";

    // Factory Reset clears ComputerSave.m_biosConfig. Keep that distinct from a populated
    // configuration with default values; GetBiosConfig would materialize a new native graph.
    public static string Baseline(BiosSettings? settings) => settings?.Digest() ?? new string('0', 64);

    public static byte[] Request(ulong epoch, NetObjectId pc, BiosSettings? expected, BiosSettings? target)
    {
        if (expected is { IsValid: false } || target is { IsValid: false }) throw new ArgumentException("Invalid BIOS values.");
        var w = new NetDataWriter(); w.Put(epoch); pc.Write(w); w.Put(Baseline(expected)); w.Put(target == null);
        target?.Write(w); return w.CopyData();
    }

    private static BiosChange Parse(NetDataReader r)
    {
        var epoch = r.GetULong(); var pc = NetObjectId.Read(r); var digest = r.GetString(65); var reset = r.GetBool();
        if (digest.Length != 64 || digest.Any(c => !Uri.IsHexDigit(c))) throw new FormatException("Invalid BIOS baseline.");
        var target = reset ? null : BiosSettings.Read(r);
        if (r.AvailableBytes != 0) throw new FormatException("Trailing BIOS bytes.");
        return new(epoch, pc, digest, reset, target);
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var change = Parse(payload);
        if (_activity.Get(ctx.Op.Actor) is not { } actor || actor.Epoch != change.Epoch)
            return CommandVerdict.Reject("Actor presence changed.");
        var pc = _objects.Pcs.SingleOrDefault(x => x.Id == change.Pc);
        if (pc == null || pc.Location.Place != PcPlace.Bench) return CommandVerdict.Reject("That PC is no longer on a bench.");
        if (actor.WorkingPc != pc.Id) return CommandVerdict.Reject("You're not using that PC's monitor.");
        if (_activity.Actors.Any(x => x.Pending?.Pc == pc.Id)) return CommandVerdict.Reject("That PC has a pending edit.");
        string? reason;
        try { reason = _native.CheckHost(ctx, pc, change); }
        catch (Exception e) { reason = "Host BIOS check failed: " + e.Message; }
        return reason == null ? CommandVerdict.Accept : CommandVerdict.Reject(reason);
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var change = Parse(payload);
        var pc = _objects.Pcs.SingleOrDefault(x => x.Id == change.Pc)
            ?? throw new InvalidDataException("Accepted BIOS PC disappeared.");
        _native.Changed(ctx, pc, change);
    }
}

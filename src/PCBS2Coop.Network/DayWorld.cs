using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>Native side of the shared day. Runs on both peers in host order and must not submit commands.</summary>
public interface IDayNative
{
    /// <summary>The guest became ready to end <paramref name="day"/> (its End Day or Skip Work), or stopped being ready
    /// (<paramref name="day"/> 0): it started a new change, or it left. <paramref name="byChange"/> says it started
    /// something.</summary>
    void ReadyChanged(in MutationContext ctx, int day, bool byChange);
}

/// <summary>
/// Phase 17d (owner decisions, November 10, 2026): the day ends only when both players are ready.
/// <list type="bullet">
/// <item>The guest's End Day or Skip Work sends <b>Ready</b> with the day its game shows. The host's game checks it is
/// its day too and that the guest isn't in the middle of something (<see cref="ReadyCheck"/>).</item>
/// <item>The host's own End Day or Skip Work runs only while the guest in its world is ready for the host's day (the
/// host presses again once told the guest is ready). The day number makes an old ready mark stale by itself.</item>
/// <item>The guest starting a new change takes its ready mark back (<see cref="StartsChange"/>), as does the guest
/// leaving or coming back. Both peers see every applied change in the same order, so both agree.</item>
/// </list>
/// Free Build leaves it unbound and refuses its command.
/// </summary>
public sealed class DayWorld : ICommandHandler, IWorldState, IChangeObserver
{
    private enum Step : byte { Ready = 1 }
    public const int MaxDay = 1_000_000;

    private readonly ActivityWorld _activity;
    private readonly IDayNative _native;

    public DayWorld(ActivityWorld activity, IDayNative native) { _activity = activity; _native = native; }
    public string Name => "day";

    /// <summary>Host: why the guest can't be ready to end <c>day</c> now (another day on the host, the guest still
    /// carrying or working on something), or null. Called after the model checks.</summary>
    public Func<int, string?>? ReadyCheck { get; set; }

    public bool Bound { get; private set; }
    /// <summary>The day the guest is ready to end, or 0.</summary>
    public int ReadyDay { get; private set; }
    public bool GuestReady(int day) => Bound && day > 0 && ReadyDay == day;

    public void Reset() { Bound = false; ReadyDay = 0; }

    /// <summary>Host, when a Career generation starts.</summary>
    public void BindHost() { Reset(); Bound = true; }

    public static byte[] Ready(ulong epoch, int day)
    {
        var w = new NetDataWriter(); w.Put((byte)Step.Ready); w.Put(epoch); w.Put(day); return w.CopyData();
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var (epoch, day) = Parse(payload);
        if (!Bound) return CommandVerdict.Reject("The day is shared only in a co-op career.");
        if (ctx.Op.Actor != ActorSlot.Guest) return CommandVerdict.Reject("Only the client marks itself ready; the host ends the day.");
        if (_activity.Get(ActorSlot.Guest) is not { } guest || guest.Epoch != epoch) return CommandVerdict.Reject("Actor presence changed.");
        if (day is < 1 or > MaxDay) return CommandVerdict.Reject("Invalid day.");
        if (ReadyDay == day) return CommandVerdict.Reject("You're already ready to end the day.");
        return ReadyCheck?.Invoke(day) is { } why ? CommandVerdict.Reject(why) : CommandVerdict.Accept;
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var (_, day) = Parse(payload);
        ReadyDay = day;
        _native.ReadyChanged(ctx, day, false);
    }

    private static (ulong Epoch, int Day) Parse(NetDataReader r)
    {
        if ((Step)r.GetByte() != Step.Ready) throw new FormatException("Unknown day step.");
        var epoch = r.GetULong(); var day = r.GetInt();
        if (r.AvailableBytes != 0) throw new FormatException("Trailing day bytes.");
        return (epoch, day);
    }

    // ---------------- the guest starting something ----------------

    public void Applied(ushort type, in MutationContext ctx, byte[] payload)
    {
        if (!Bound || ReadyDay == 0 || !StartsChange(type, ctx.Op.Actor, payload)) return;
        ReadyDay = 0;
        _native.ReadyChanged(ctx, 0, !IsGuestPresence(type, payload));
    }

    /// <summary>Whether this applied change takes the guest's ready mark back: the guest leaving or coming back, or the
    /// guest starting something (a claim on a PC, a PC or package moved, an edit, a table, decorating, a purchase, a
    /// sale, a job or e-mail action). Automatic guest requests (pictures it is missing, repairs, results it sends at
    /// the end of a change) and desktop input don't count; neither does anything the host does.</summary>
    public static bool StartsChange(ushort type, ActorSlot actor, byte[] payload)
    {
        if (payload == null || payload.Length == 0) return false;
        if (IsGuestPresence(type, payload)) return true;
        if (actor != ActorSlot.Guest) return false;
        var step = payload[0];
        return type switch
        {
            CommandTypes.ActorActivity => step == 3, // Reserve: a claim for an edit or a move
            CommandTypes.PcLifecycle => step is (byte)PcAction.Create or (byte)PcAction.Delete,
            CommandTypes.PcTransfer => step is >= (byte)TransferAction.Pickup and <= (byte)TransferAction.Retrieve,
            CommandTypes.PeripheralSwap or CommandTypes.CasePanel or CommandTypes.Bios => true,
            CommandTypes.Mechanism => step == 1, // Toggle
            CommandTypes.ComponentEdit => step == 1, // Begin
            CommandTypes.ToolBench => step is 1 or 4, // Use, Switch
            CommandTypes.Customization => step is 1 or 4, // Lights (the Lighting app), Purchase
            // Decorate (on), Lights, Decoration (seasonal), Wallpaper.
            CommandTypes.Workshop => step == 1 ? payload.Length > 9 && payload[9] != 0 : step is 3 or 4 or 7,
            CommandTypes.Commerce => step is >= 3 and <= 7 or 9, // Order, Sell, Collect, BuyOffer, BuyShares, SalePrice
            CommandTypes.Packages => step is >= (byte)PackageStep.Take and <= (byte)PackageStep.NoteDrop,
            CommandTypes.Jobs => step is 3 or 4, // Act, Mail
            _ => false,
        };
    }

    /// <summary>The host sets the guest's presence when it joins, leaves or reloads.</summary>
    private static bool IsGuestPresence(ushort type, byte[] payload) =>
        type == CommandTypes.ActorActivity && payload.Length > 1 && payload[0] == 1 && payload[1] == (byte)ActorSlot.Guest;

    // ---------------- snapshot state ----------------

    public void Write(NetDataWriter w)
    {
        w.Put(Bound);
        if (Bound) w.Put(ReadyDay);
    }

    public void Read(NetDataReader r)
    {
        Reset();
        if (!r.GetBool()) return;
        var day = r.GetInt();
        if (day is < 0 or > MaxDay) throw new InvalidDataException("Invalid day state.");
        ReadyDay = day; Bound = true;
    }
}

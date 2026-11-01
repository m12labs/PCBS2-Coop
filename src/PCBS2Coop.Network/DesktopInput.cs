using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>Which screen of a PC an input was made on. The other game replays an event only on the same screen.</summary>
public enum DesktopSurface : byte { Boot = 1, Bios = 2, Desktop = 3 }

public enum DesktopEventKind : byte
{
    /// <summary>Pointer moved (hover, or a drag while a button is held).</summary>
    Move = 1,
    Down = 2,
    Up = 3,
    Scroll = 4,
    /// <summary>The text of the focused input field (Path) changed to Text.</summary>
    Text = 5,
    /// <summary>Enter was pressed in the input field at Path.</summary>
    Submit = 6,
    /// <summary>The actor left this PC's monitor: release anything they held and clear their hover.</summary>
    Leave = 7,
    /// <summary>The actor's game entered this PC's BIOS from the boot screen.</summary>
    EnterBios = 8,
    /// <summary>Host only: the app windows open on this PC's desktop, bottom to top, as program IDs in Text
    /// separated by newlines. A game that joined later (or drifted) opens and closes apps to match.</summary>
    Windows = 9,
}

public enum MonitorButton : byte { None = 0, Left = 1, Right = 2, Middle = 3 }

/// <summary>One mirrored input. X/Y are fractions of the PC screen (0..1, origin bottom-left), independent of each
/// player's resolution and monitor shape. Time is the sender's clock in seconds; only differences are meaningful.</summary>
public readonly record struct DesktopEvent(DesktopEventKind Kind, DesktopSurface Surface, float Time, float X, float Y,
    MonitorButton Button = MonitorButton.None, float ScrollX = 0, float ScrollY = 0, string Path = "", string Text = "")
{
    public const int MaxPath = 512, MaxText = 4000;

    private static bool Fraction(float v) => float.IsFinite(v) && v is >= 0 and <= 1;
    private static bool Small(float v) => float.IsFinite(v) && MathF.Abs(v) <= 1000;

    public bool IsValid => Enum.IsDefined(Kind) && Enum.IsDefined(Surface) && Enum.IsDefined(Button) && float.IsFinite(Time) &&
        Fraction(X) && Fraction(Y) && Small(ScrollX) && Small(ScrollY) && Path != null && Text != null &&
        Path.Length <= MaxPath && Text.Length <= MaxText && !Path.Any(char.IsControl) &&
        (Kind is DesktopEventKind.Down or DesktopEventKind.Up ? Button != MonitorButton.None : Button == MonitorButton.None) &&
        (Kind is DesktopEventKind.Text or DesktopEventKind.Submit ? Path.Length > 0 : Path.Length == 0) &&
        (Kind is DesktopEventKind.Text or DesktopEventKind.Windows || Text.Length == 0) &&
        (Kind == DesktopEventKind.Scroll || (ScrollX == 0 && ScrollY == 0));

    /// <summary>A release-type event is still meaningful after the PC left its bench.</summary>
    public bool Releases => Kind is DesktopEventKind.Up or DesktopEventKind.Leave;

    public void Write(NetDataWriter w)
    {
        w.Put((byte)Kind); w.Put((byte)Surface); w.Put(Time); w.Put(X); w.Put(Y); w.Put((byte)Button);
        if (Kind == DesktopEventKind.Scroll) { w.Put(ScrollX); w.Put(ScrollY); }
        if (Kind is DesktopEventKind.Text or DesktopEventKind.Submit) { w.Put(Path, MaxPath + 1); }
        if (Kind is DesktopEventKind.Text or DesktopEventKind.Windows) w.Put(Text, MaxText + 1);
    }

    public static DesktopEvent Read(NetDataReader r)
    {
        var kind = (DesktopEventKind)r.GetByte(); var surface = (DesktopSurface)r.GetByte();
        var time = r.GetFloat(); var x = r.GetFloat(); var y = r.GetFloat(); var button = (MonitorButton)r.GetByte();
        float sx = 0, sy = 0; string path = "", text = "";
        if (kind == DesktopEventKind.Scroll) { sx = r.GetFloat(); sy = r.GetFloat(); }
        if (kind is DesktopEventKind.Text or DesktopEventKind.Submit) path = r.GetString(MaxPath + 1);
        if (kind is DesktopEventKind.Text or DesktopEventKind.Windows) text = r.GetString(MaxText + 1);
        var e = new DesktopEvent(kind, surface, time, x, y, button, sx, sy, path, text);
        if (!e.IsValid) throw new FormatException("Invalid desktop event.");
        return e;
    }
}

/// <summary>A batch of one actor's inputs on one shared PC, in the order they happened on the sender's game.</summary>
public sealed record DesktopInput(ulong Epoch, NetObjectId Pc, IReadOnlyList<DesktopEvent> Events)
{
    public const int MaxEvents = 64;

    public bool IsValid => !Pc.IsNone && Events != null && Events.Count is >= 1 and <= MaxEvents && Events.All(e => e.IsValid);

    public byte[] ToBytes()
    {
        if (!IsValid) throw new ArgumentException("Invalid desktop input.");
        var w = new NetDataWriter();
        w.Put(Epoch); Pc.Write(w); w.Put((byte)Events.Count);
        foreach (var e in Events) e.Write(w);
        if (w.Length > Protocol.MaxPayloadBytes - 64) throw new ArgumentException("Desktop input batch is too large.");
        return w.CopyData();
    }

    public static DesktopInput Read(NetDataReader r)
    {
        var epoch = r.GetULong(); var pc = NetObjectId.Read(r); var count = r.GetByte();
        if (count is < 1 or > MaxEvents) throw new FormatException("Invalid desktop event count.");
        var events = new DesktopEvent[count];
        for (var i = 0; i < count; i++) events[i] = DesktopEvent.Read(r);
        var input = new DesktopInput(epoch, pc, events);
        if (!input.IsValid || r.AvailableBytes != 0) throw new FormatException("Invalid desktop input payload.");
        return input;
    }
}

/// <summary>Replays accepted input on this game. The sender's own game already ran it (owner decision: your own
/// clicks act instantly), so the adapter skips batches from the local actor.</summary>
public interface IDesktopInputNative
{
    void Changed(in MutationContext ctx, PcFact? pc, DesktopInput input);
}

/// <summary>Host-ordered relay of desktop, BIOS and boot-screen input (owner decision: mirror input, so both games run
/// the same apps from the same clicks). There is no desktop lock: both actors' batches are accepted in host order.
/// Nothing here is saved; the PC's software record and BIOS stay in the native save.</summary>
public sealed class DesktopInputWorld : ICommandHandler
{
    private readonly ObjectWorld _objects;
    private readonly ActivityWorld _activity;
    private readonly IDesktopInputNative _native;

    public DesktopInputWorld(ObjectWorld objects, ActivityWorld activity, IDesktopInputNative native)
    { _objects = objects; _activity = activity; _native = native; }

    public string Name => "desktop input";

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var input = DesktopInput.Read(payload);
        var actor = _activity.Get(ctx.Op.Actor);
        if (actor == null || actor.Epoch != input.Epoch) return CommandVerdict.Reject("Actor presence changed.");
        var pc = _objects.Pcs.SingleOrDefault(x => x.Id == input.Pc);
        // A release can arrive after the PC was picked up; anything else needs the PC on its bench.
        if ((pc == null || pc.Location.Place != PcPlace.Bench) && !input.Events.All(e => e.Releases))
            return CommandVerdict.Reject("That PC is no longer on a bench.");
        if (ctx.Op.Actor != ActorSlot.Host && input.Events.Any(e => e.Kind == DesktopEventKind.Windows))
            return CommandVerdict.Reject("Only the host publishes the open windows.");
        return CommandVerdict.Accept;
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var input = DesktopInput.Read(payload);
        _native.Changed(ctx, _objects.Pcs.SingleOrDefault(x => x.Id == input.Pc), input);
    }
}

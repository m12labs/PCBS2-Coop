using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

public enum MonitorInputAction : byte { Down = 1, Drag = 2, Up = 3, Cancel = 4 }
public enum MonitorButton : byte { Left = 1, Right = 2, Middle = 3 }

/// <summary>A button transition on one shared PC desktop. Drag/Up/Cancel name the Down operation's
/// sequence, so a delayed release cannot terminate a newer gesture by the same actor.</summary>
public readonly record struct MonitorInput(ulong Epoch, NetObjectId Pc, MonitorInputAction Action,
    MonitorButton Button, uint PressSequence, float X, float Y)
{
    public bool IsValid => !Pc.IsNone && Enum.IsDefined(Action) && Enum.IsDefined(Button) &&
        float.IsFinite(X) && float.IsFinite(Y) && X is >= 0 and <= 1 && Y is >= 0 and <= 1 &&
        (Action == MonitorInputAction.Down ? PressSequence == 0 : PressSequence != 0);

    public byte[] ToBytes()
    {
        if (!IsValid) throw new ArgumentException("Invalid monitor input.");
        var w = new NetDataWriter();
        w.Put(Epoch); Pc.Write(w); w.Put((byte)Action); w.Put((byte)Button); w.Put(PressSequence); w.Put(X); w.Put(Y);
        return w.CopyData();
    }

    public static MonitorInput Read(NetDataReader r)
    {
        var input = new MonitorInput(r.GetULong(), NetObjectId.Read(r), (MonitorInputAction)r.GetByte(),
            (MonitorButton)r.GetByte(), r.GetUInt(), r.GetFloat(), r.GetFloat());
        if (!input.IsValid || r.AvailableBytes != 0) throw new FormatException("Invalid monitor input payload.");
        return input;
    }
}

public readonly record struct MonitorPress(ActorSlot Actor, ulong Epoch, NetObjectId Pc, MonitorButton Button,
    uint Sequence, float X, float Y);

/// <summary>Native target validation and projection. The host resolves hits against its current OS raycaster.
/// Its callback may execute the game UI; a guest projection must not repeat authoritative app side effects.</summary>
public interface IMonitorInputNative
{
    string? CheckHost(in CommandContext ctx, PcFact? pc, MonitorInput input, MonitorPress? press);
    void Changed(in MutationContext ctx, PcFact? pc, MonitorInput input, MonitorPress? press);
}

/// <summary>Host-ordered mouse button and drag lifecycle, with one independent capture per actor.
/// Hover/pointer position is carried by the latest-state stream and is never saved.</summary>
public sealed class MonitorInputWorld : ICommandHandler
{
    private readonly ObjectWorld _objects;
    private readonly ActivityWorld _activity;
    private readonly IMonitorInputNative _native;
    private readonly Dictionary<ActorSlot, MonitorPress> _presses = new();

    public MonitorInputWorld(ObjectWorld objects, ActivityWorld activity, IMonitorInputNative native)
    { _objects = objects; _activity = activity; _native = native; }
    public string Name => "monitor input";
    public MonitorPress? PressOf(ActorSlot actor) => _presses.TryGetValue(actor, out var p) ? p : null;
    public void Reset() => _presses.Clear();

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var input = MonitorInput.Read(payload);
        var actor = _activity.Get(ctx.Op.Actor);
        if (actor == null || actor.Epoch != input.Epoch) return CommandVerdict.Reject("Actor presence changed.");
        var pc = _objects.Pcs.SingleOrDefault(x => x.Id == input.Pc);
        // A release can arrive after the actor exits the monitor or the PC moves. Keep the
        // capture releasable; only new presses and drags require a currently benched PC.
        if (input.Action is MonitorInputAction.Down or MonitorInputAction.Drag &&
            (pc == null || pc.Location.Place != PcPlace.Bench))
            return CommandVerdict.Reject("That PC is no longer on a bench.");
        var press = PressOf(ctx.Op.Actor);
        if (press is { } stale && stale.Epoch != actor.Epoch) press = null;
        if (input.Action == MonitorInputAction.Down)
        {
            if (actor.WorkingPc != pc!.Id) return CommandVerdict.Reject("You're not using that PC's monitor.");
            if (press != null) return CommandVerdict.Reject("Release your previous monitor press first.");
        }
        else
        {
            if (press is not { } held || held.Pc != input.Pc || held.Button != input.Button || held.Sequence != input.PressSequence)
                return CommandVerdict.Reject("That monitor press is no longer active.");
            if (input.Action == MonitorInputAction.Drag && actor.WorkingPc != pc!.Id)
                return CommandVerdict.Reject("You're no longer using that PC's monitor.");
        }
        if (input.Action is MonitorInputAction.Down or MonitorInputAction.Drag &&
            _activity.Actors.Any(x => x.Pending?.Pc == pc!.Id))
            return CommandVerdict.Reject("That PC has a pending edit.");
        string? reason;
        try { reason = _native.CheckHost(ctx, pc, input, press); }
        catch (Exception e) { reason = "Host monitor check failed: " + e.Message; }
        return reason == null ? CommandVerdict.Accept : CommandVerdict.Reject(reason);
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var input = MonitorInput.Read(payload);
        var pc = _objects.Pcs.SingleOrDefault(x => x.Id == input.Pc);
        if (pc == null && input.Action is MonitorInputAction.Down or MonitorInputAction.Drag)
            throw new InvalidDataException("Accepted monitor PC disappeared.");
        var press = PressOf(ctx.Op.Actor);
        if (press is { } stale && stale.Epoch != input.Epoch) { _presses.Remove(ctx.Op.Actor); press = null; }
        if (input.Action == MonitorInputAction.Down)
            _presses[ctx.Op.Actor] = new(ctx.Op.Actor, input.Epoch, pc!.Id, input.Button, ctx.Op.Sequence, input.X, input.Y);
        else if (input.Action is MonitorInputAction.Up or MonitorInputAction.Cancel)
            _presses.Remove(ctx.Op.Actor);
        else if (press is { } held)
            _presses[ctx.Op.Actor] = held with { X = input.X, Y = input.Y };
        _native.Changed(ctx, pc, input, press);
    }
}

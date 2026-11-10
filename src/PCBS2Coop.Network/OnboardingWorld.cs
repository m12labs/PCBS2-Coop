using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>One tutorial as a game holds it: its type name, its state (the game's <c>OnboardingState</c>: 0 inactive,
/// 1 active, 2 complete) and its step (the type's own <c>progress</c> value).</summary>
public sealed record OnboardingFact(string Type, byte State, int Progress)
{
    public const int MaxType = 128;
    public bool IsValid => !string.IsNullOrEmpty(Type) && Type.Length <= MaxType && State <= 2;

    internal void Write(NetDataWriter w) { w.Put(Type, MaxType); w.Put(State); w.Put(Progress); }
    internal static OnboardingFact Read(NetDataReader r) => new(r.GetString(MaxType), r.GetByte(), r.GetInt());
}

/// <summary>Native side of shared tutorials. Runs on both peers in host order and must not submit commands.</summary>
public interface IOnboardingNative
{
    /// <summary>A tutorial moved on. The game that moved it already shows it; the other sets its own tutorial the same.</summary>
    void Moved(in MutationContext ctx, OnboardingFact fact);
}

/// <summary>
/// Phase 17e (owner decision, November 10, 2026): both players go through the tutorials together. Whichever player
/// does a tutorial step, it counts for both.
/// <list type="bullet">
/// <item>Each game watches its own tutorials (every type the game has, each with a state and a step). When one moves on
/// by itself, that game sends <b>Move</b> with the state and step it moved from and to.</item>
/// <item>A move from what this model last had is accepted; the other game sets its tutorial the same. A move from
/// anything else (the other player got there first, or the host's tutorial went another way) is refused, and that game
/// follows the accepted state instead. So both games always agree on every tutorial.</item>
/// <item>The host binds what its game holds when a generation starts; the guest's loaded save matches it.</item>
/// </list>
/// </summary>
public sealed class OnboardingWorld : ICommandHandler, IWorldState
{
    private enum Step : byte { Move = 1 }
    public const int MaxTutorials = 256;

    private readonly ActivityWorld _activity;
    private readonly IOnboardingNative _native;
    private readonly Dictionary<string, OnboardingFact> _facts = new(StringComparer.Ordinal);

    public OnboardingWorld(ActivityWorld activity, IOnboardingNative native) { _activity = activity; _native = native; }
    public string Name => "onboarding";

    public bool Bound { get; private set; }
    public IReadOnlyCollection<OnboardingFact> Facts => _facts.Values;
    public OnboardingFact? Fact(string type) => _facts.GetValueOrDefault(type);

    public void Reset() { _facts.Clear(); Bound = false; }

    /// <summary>Host, when a world generation starts: its tutorials as its game holds them.</summary>
    public void BindHost(IEnumerable<OnboardingFact> facts)
    {
        var list = facts.ToList();
        if (list.Count > MaxTutorials || list.Any(f => !f.IsValid) || list.Select(f => f.Type).Distinct(StringComparer.Ordinal).Count() != list.Count)
            throw new InvalidDataException("Invalid tutorial binding.");
        Reset();
        foreach (var f in list) _facts[f.Type] = f;
        Bound = true;
    }

    /// <summary>A tutorial moved from <paramref name="was"/> to <paramref name="to"/> (same type) on the sender's game.</summary>
    public static byte[] Move(ulong epoch, OnboardingFact was, OnboardingFact to)
    {
        var w = new NetDataWriter(); w.Put((byte)Step.Move); w.Put(epoch); was.Write(w); w.Put(to.State); w.Put(to.Progress); return w.CopyData();
    }

    private static (ulong Epoch, OnboardingFact Was, OnboardingFact To) Parse(NetDataReader r)
    {
        if ((Step)r.GetByte() != Step.Move) throw new FormatException("Unknown tutorial step.");
        var epoch = r.GetULong(); var was = OnboardingFact.Read(r);
        var to = was with { State = r.GetByte(), Progress = r.GetInt() };
        if (r.AvailableBytes != 0) throw new FormatException("Trailing tutorial bytes.");
        return (epoch, was, to);
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var (epoch, was, to) = Parse(payload);
        if (!Bound) return CommandVerdict.Reject("Tutorials aren't shared in this world.");
        if (_activity.Get(ctx.Op.Actor) is not { } actor || actor.Epoch != epoch) return CommandVerdict.Reject("Actor presence changed.");
        if (!was.IsValid || !to.IsValid || was == to) return CommandVerdict.Reject("Invalid tutorial step.");
        var known = Fact(was.Type);
        if (known == null && _facts.Count >= MaxTutorials) return CommandVerdict.Reject("Too many tutorials.");
        // A tutorial the host's game didn't list yet starts from inactive at step 0.
        if ((known ?? was with { State = 0, Progress = 0 }) != was) return CommandVerdict.Reject("That tutorial already moved on.");
        return CommandVerdict.Accept;
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var (_, _, to) = Parse(payload);
        _facts[to.Type] = to;
        _native.Moved(ctx, to);
    }

    // ---------------- snapshot state ----------------

    public void Write(NetDataWriter w)
    {
        w.Put(Bound);
        if (!Bound) return;
        w.Put(_facts.Count);
        foreach (var f in _facts.Values.OrderBy(f => f.Type, StringComparer.Ordinal)) f.Write(w);
    }

    public void Read(NetDataReader r)
    {
        Reset();
        if (!r.GetBool()) return;
        var n = r.GetInt();
        if (n is < 0 or > MaxTutorials) throw new InvalidDataException("Invalid tutorial state.");
        for (var i = 0; i < n; i++)
        {
            var f = OnboardingFact.Read(r);
            if (!f.IsValid || _facts.ContainsKey(f.Type)) throw new InvalidDataException("Invalid tutorial in the tutorial state.");
            _facts[f.Type] = f;
        }
        Bound = true;
    }
}

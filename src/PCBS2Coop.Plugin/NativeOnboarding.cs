using UnityEngine;
using PCBS2Coop.Network;
using OnboardingList = Il2CppSystem.Collections.Generic.List<BaseOnboardingClass>;

namespace PCBS2Coop;

/// <summary>
/// Phase 17e (owner decision, November 10, 2026): both players go through the tutorials together (<see cref="OnboardingWorld"/>,
/// command 23). Each game keeps its tutorials in <c>JobTutorialHintUi.onboardingClasses</c> (Career) or
/// <c>freebuildOnboardingClasses</c> (Free Build): a state (<c>BaseOnboardingClass.State</c>) and a step, the type's
/// <c>progress</c> enum, which the game itself reads by reflection (<c>getOnboardingProgress</c>, <c>GetEnumProperty</c>).
/// <list type="bullet">
/// <item>Four times a second each game compares its tutorials with the shared ones. A tutorial its own player moved on
/// is sent as a move from the shared value.</item>
/// <item>The other game sets the same tutorial the game's own way: <c>SetOnboardingActive</c> / <c>Complete</c> /
/// <c>Inactive</c> for the state, the <c>progress</c> property for the step. A step it was shown is cleared from its
/// action bar (<c>ClearActionBar</c>); the new step's own prompts show when that game's tutorial code shows them. So a
/// step either player does counts for both, and buttons a tutorial blocks free up on both games.</item>
/// <item>When both players move the same tutorial at once, the first move wins and the other game follows it.</item>
/// </list>
/// </summary>
internal sealed class NativeOnboarding : IOnboardingNative
{
    private const float SampleEvery = 0.25f, PendingTimeout = 10f;

    private readonly CoopController _coop;
    private readonly WorldSync _world;
    public OnboardingWorld Handler { get; }
    private readonly Dictionary<string, (OperationId Op, float At)> _pending = new(StringComparer.Ordinal);
    private float _next;
    private bool _readFailed;

    public NativeOnboarding(CoopController coop, WorldSync world)
    {
        _coop = coop; _world = world;
        Handler = new OnboardingWorld(coop.Activity, this);
        world.GenerationStarted += (uint _, string _) => { _pending.Clear(); _readFailed = false; };
        world.Rejected += OnRejected;
    }

    private bool Active => Handler.Bound && _world.Admitted && _world.Divergence == null && _coop.WorldReady;
    private ulong Epoch => _coop.Activity.Get(_world.Local)?.Epoch ?? 0;

    private static OnboardingList Tutorials => NativeCareer.InCareer ? JobTutorialHintUi.onboardingClasses : JobTutorialHintUi.freebuildOnboardingClasses;

    /// <summary>This game's tutorials, once per type.</summary>
    private List<(BaseOnboardingClass Tutorial, OnboardingFact Fact)> Read()
    {
        var found = new List<(BaseOnboardingClass, OnboardingFact)>();
        var list = Tutorials;
        if (list == null) return found;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in list)
        {
            if (t == null) continue;
            try
            {
                var type = t.GetIl2CppType().FullName;
                if (string.IsNullOrEmpty(type) || !seen.Add(type)) continue;
                var fact = new OnboardingFact(type, (byte)(int)t.State, t.getOnboardingProgress);
                if (fact.IsValid) found.Add((t, fact));
                if (found.Count == OnboardingWorld.MaxTutorials) break;
            }
            catch (Exception e)
            {
                if (_readFailed) continue;
                _readFailed = true;
                Plugin.Logger.LogWarning("[tutorial] a tutorial could not be read; it stays on each game: " + e.Message);
            }
        }
        return found;
    }

    internal void BindHost()
    {
        var facts = Read().Select(x => x.Fact).ToList();
        Handler.BindHost(facts);
        Plugin.Logger.LogInfo($"[tutorial] host bound {facts.Count} tutorial(s), {facts.Count(f => f.State == 1)} active.");
    }

    internal void BindGuest()
    {
        if (!Handler.Bound) Plugin.Logger.LogWarning("[tutorial] the host's world has no shared tutorial state; tutorials stay on each game.");
    }

    public void Update()
    {
        if (!Active) return;
        var now = Time.realtimeSinceStartup;
        foreach (var (type, p) in _pending.ToList())
            if (now - p.At > PendingTimeout) _pending.Remove(type);
        if (now < _next) return;
        _next = now + SampleEvery;
        foreach (var (tutorial, fact) in Read())
        {
            var shared = Handler.Fact(fact.Type) ?? fact with { State = 0, Progress = 0 };
            if (fact == shared || _pending.ContainsKey(fact.Type)) continue;
            if (!_world.Submit(CommandTypes.Onboarding, OnboardingWorld.Move(Epoch, shared, fact), out var op)) return;
            // The host's own move is applied before Submit returns.
            if (_world.Local != ActorSlot.Host) _pending[fact.Type] = (op, now);
            Plugin.Logger.LogInfo($"[tutorial] {Short(fact.Type)} moved here: {shared.State}/{shared.Progress} -> {fact.State}/{fact.Progress}.");
        }
    }

    private void OnRejected(OperationId op, RejectReason reason, string text)
    {
        var type = _pending.FirstOrDefault(p => p.Value.Op == op).Key;
        if (type == null) return;
        _pending.Remove(type);
        // The other player's move won (or the presence changed): follow the shared tutorial.
        if (Handler.Fact(type) is { } shared) Follow(shared);
        Plugin.Logger.LogInfo($"[tutorial] {Short(type)}: this game's move was refused ({text}); following the shared step.");
    }

    public void Moved(in MutationContext ctx, OnboardingFact fact)
    {
        _pending.Remove(fact.Type);
        if (ctx.FromLocalPlayer) return;
        Follow(fact);
    }

    /// <summary>Set this game's tutorial to the shared state and step, the game's own way.</summary>
    private void Follow(OnboardingFact fact)
    {
        try
        {
            var tutorial = Read().Where(x => x.Fact.Type == fact.Type).Select(x => x.Tutorial).FirstOrDefault();
            if (tutorial == null) { Plugin.Logger.LogInfo($"[tutorial] {Short(fact.Type)} isn't on this game; skipped."); return; }
            NativeProjection.Run(() =>
            {
                if ((int)tutorial.State != fact.State)
                {
                    if (fact.State == 2) tutorial.SetOnboardingComplete();
                    else if (fact.State == 1) tutorial.SetOnboardingActive();
                    else tutorial.SetOnboardingInactive();
                }
                if (tutorial.getOnboardingProgress != fact.Progress)
                {
                    var property = tutorial.GetEnumProperty() ?? throw new InvalidDataException("the tutorial has no step property");
                    property.SetValue(tutorial, Il2CppSystem.Enum.ToObject(property.PropertyType, fact.Progress));
                    JobTutorialHintUi.ClearActionBar(false);
                }
            });
            Plugin.Logger.LogInfo($"[tutorial] {Short(fact.Type)} follows the other player: {fact.State}/{fact.Progress}.");
        }
        catch (Exception e) { _coop.ProjectionFailed($"The tutorial {Short(fact.Type)} could not follow the other player", e); }
    }

    private static string Short(string type) => type.Length > 48 ? type[^48..] : type;
}

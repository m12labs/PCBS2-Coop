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
    private const float SampleEvery = 0.25f, PendingTimeout = 10f, BackWindow = 3f;

    private readonly CoopController _coop;
    private readonly WorldSync _world;
    public OnboardingWorld Handler { get; }
    private readonly Dictionary<string, (OperationId Op, float At)> _pending = new(StringComparer.Ordinal);
    private float _next;
    private bool _readFailed;
    /// <summary>0.73: the other player's steps this game holds until their part change is shown here.</summary>
    private readonly Dictionary<string, float> _held = new(StringComparer.Ordinal);
    /// <summary>0.73: the step this game last followed for each tutorial, when, and whether that was a second try.</summary>
    private readonly Dictionary<string, (int To, float At, bool Again)> _followed = new(StringComparer.Ordinal);

    public NativeOnboarding(CoopController coop, WorldSync world)
    {
        _coop = coop; _world = world;
        Handler = new OnboardingWorld(coop.Activity, this);
        world.GenerationStarted += (uint _, string _) => { _pending.Clear(); _held.Clear(); _followed.Clear(); _readFailed = false; };
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
        ReportLeftovers();
        if (now < _next) return;
        _next = now + SampleEvery;
        PruneOutlines();
        FollowHeld(now);
        foreach (var (tutorial, fact) in Read())
        {
            var shared = Handler.Fact(fact.Type) ?? fact with { State = 0, Progress = 0 };
            if (fact == shared || _pending.ContainsKey(fact.Type) || _held.ContainsKey(fact.Type)) continue;
            if (WentBack(fact, shared, now)) continue;
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
        if (OtherPlayersChangeShowing)
        {
            _held[fact.Type] = 0f;
            Plugin.Logger.LogInfo($"[tutorial] {Short(fact.Type)} step {fact.Progress}: held until the other player's change to the PC is shown here.");
            return;
        }
        _held.Remove(fact.Type);
        Follow(fact);
    }

    /// <summary>0.73 (game run 6, November 12, 2026): a tutorial step checks this game's PC. At INSTALL_PCI the game puts
    /// the tutorial back to INSTALL_GPU when the first GPU slot is empty (<c>NewStartOnboarding.InstallPCILock</c>, 0x832300).
    /// The host's tutorial moved on as their GPU went in, but in Career the guest shows that install once the host
    /// commits it, so the guest's game went back a step and both games followed it there, with the GPU in. A game now
    /// follows the other player's step once their part or panel change is shown here.</summary>
    private bool OtherPlayersChangeShowing
    {
        get
        {
            var coop = CoopController.Instance;
            return coop != null && (coop.Replays.Busy || coop.Edits?.ShowingOther == true || coop.Panels?.ShowingOther == true);
        }
    }

    private void FollowHeld(float now)
    {
        if (_held.Count == 0) return;
        var waiting = OtherPlayersChangeShowing;
        foreach (var (type, notBefore) in _held.ToList())
        {
            // Hand-turned panel screws can take as long as the player needs. A timeout must not
            // run the next native step against a PC that still shows the previous panel state.
            if (waiting || now < notBefore) continue;
            _held.Remove(type);
            if (Handler.Fact(type) is not { } shared) continue;
            Follow(shared);
        }
    }

    /// <summary>0.73: this game's tutorial went back a step right after following the other player forward, because its
    /// step code checked a PC change not yet shown here. Follow again once it is, instead of moving the other player
    /// back; a second time it is shared as this player's move.</summary>
    private bool WentBack(OnboardingFact fact, OnboardingFact shared, float now)
    {
        if (fact.State != shared.State || fact.Progress >= shared.Progress) return false;
        if (!_followed.TryGetValue(fact.Type, out var f) || f.To != shared.Progress || now - f.At > BackWindow || f.Again) return false;
        _followed[fact.Type] = f with { Again = true };
        _held[fact.Type] = now + 1f;
        Plugin.Logger.LogInfo($"[tutorial] {Short(fact.Type)} went back to step {fact.Progress} on this game right after following step {shared.Progress}; following again once the other player's change is shown here.");
        return true;
    }

    /// <summary>Set this game's tutorial to the shared state and step, the game's own way.</summary>
    private void Follow(OnboardingFact fact)
    {
        try
        {
            var tutorial = Read().Where(x => x.Fact.Type == fact.Type).Select(x => x.Tutorial).FirstOrDefault();
            if (tutorial == null) { Plugin.Logger.LogInfo($"[tutorial] {Short(fact.Type)} isn't on this game; skipped."); return; }
            ClearHighlights(fact.Type);
            var handled = RunStepEnd(tutorial, fact);
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
                    // The game's own step end showed the next step's prompt; keep it.
                    if (!handled) JobTutorialHintUi.ClearActionBar(false);
                }
            });
            FollowNote(tutorial, fact);
            var now = Time.realtimeSinceStartup;
            var again = _followed.TryGetValue(fact.Type, out var last) && last.To == fact.Progress && last.Again && now - last.At < BackWindow + 2f;
            _followed[fact.Type] = (fact.Progress, now, again);
            _report = (fact.Type, fact.Progress, now + 1f);
            Plugin.Logger.LogInfo($"[tutorial] {Short(fact.Type)} follows the other player: {fact.State}/{fact.Progress}.");
        }
        catch (Exception e) { _coop.ProjectionFailed($"The tutorial {Short(fact.Type)} could not follow the other player", e); }
    }

    private static readonly PerifBenchID[] Benches =
    {
        PerifBenchID.ShopComputer, PerifBenchID.BenchSlot1, PerifBenchID.BenchSlot2, PerifBenchID.BenchSlot3,
        PerifBenchID.BenchSlot4, PerifBenchID.BenchSlot5, PerifBenchID.BenchSlot6,
    };

    /// <summary>0.68 (game run, November 10): the outlines a step drew stay on the game that follows it, because the
    /// step's own end (a pickup, a drop, a removal) ran on the other game. Clear the tutorial's action tips and their
    /// outlines, the workbench highlights and case outlines; this game's tutorial code draws the new step's own again.
    /// 0.69 (game run, November 10): the flashing yellow outline of a tutorial target (the PC, the GPU) is the game's
    /// <c>CustomOutline</c> in its action-tip colour, which outlives its tip; turn those off too.</summary>
    private static void ClearHighlights(string type)
    {
        try
        {
            NativeProjection.Run(() =>
            {
                JobTutorialHintUi.DestroyAllActionTips();
                foreach (var bench in Benches) JobTutorialHintUi.HighlightWorkbench(false, bench);
                foreach (var mesh in UnityEngine.Object.FindObjectsOfType<Case>())
                    if (mesh != null && mesh.IsOutlined) mesh.SetOutline(false, 0, true, false);
                foreach (var box in UnityEngine.Object.FindObjectsOfType<DeliveryBox>())
                    if (box != null) box.SetSelectedWithLerp(false);
                var tip = cakeslice.CustomOutline.ACTIONTOOLTIP;
                foreach (var outline in UnityEngine.Object.FindObjectsOfType<cakeslice.CustomOutline>())
                    if (outline != null && outline.Colour == tip) outline.Enable(false);
            });
            PruneOutlines();
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[tutorial] {Short(type)}: the previous step's outlines could not be cleared: {e.Message}"); }
    }

    private static readonly HashSet<string> PrunedLogged = new(StringComparer.Ordinal);

    /// <summary>0.70 (game run, November 12): the game draws outlines from a static list
    /// (<c>CustomOutline.OutlineMeshDict</c>) that nothing clears when the outlined object goes away. A delivery box or
    /// PC the other player took is removed here by the co-op sync, not by this game's own pickup, so its flashing yellow
    /// outline stayed in the air. Drop entries whose object is destroyed or hidden.</summary>
    private static void PruneOutlines()
    {
        try
        {
            var outlines = cakeslice.CustomOutline.OutlineMeshDict;
            if (outlines == null || outlines.Count == 0) return;
            var gone = new List<GameObject>();
            foreach (var key in outlines.Keys)
            {
                if (key == null || !key.activeInHierarchy) { gone.Add(key); continue; }
                var element = outlines[key];
                if (element == null || element.Rend == null || !element.Rend.gameObject.activeInHierarchy) gone.Add(key);
            }
            foreach (var key in gone)
            {
                var name = key == null ? "a removed object" : key.name;
                outlines.Remove(key);
                if (PrunedLogged.Add(name)) Plugin.Logger.LogInfo($"[tutorial] removed the outline of {name}: it is no longer shown on this game.");
            }
        }
        catch (Exception e)
        {
            if (PrunedLogged.Add("!error")) Plugin.Logger.LogWarning("[tutorial] outlines could not be checked: " + e.Message);
        }
    }

    private const int CollectGpuStep = 5, CollectPcStep = 6; // NewStartEnum

    /// <summary>A step-end handler of the game's own tutorial is running for the other player's step.</summary>
    [ThreadStatic] private static bool s_stepEnd;

    /// <summary>0.71 (owner suggestion, November 12): the game ends a first-tutorial step in its event handler
    /// (<c>NewStartOnboarding.OnDeliveryCollected</c>, <c>OnCasePickup</c>), which hides that step's markers (the
    /// delivery area's yellow box) and highlights, and shows the next step's prompt. The handler runs on the game whose
    /// player did it. When this game follows the other player past such a step, run the same handler here (its tutorial
    /// card stays with the player who did it). Returns whether a handler ran.</summary>
    private static bool RunStepEnd(BaseOnboardingClass tutorial, OnboardingFact fact)
    {
        var start = tutorial.TryCast<NewStartOnboarding>();
        if (start == null || fact.State != 1) return false;
        var here = tutorial.getOnboardingProgress;
        if (fact.Progress <= here) return false;
        System.Action handler = here switch
        {
            CollectGpuStep => start.OnDeliveryCollected,
            // The COLLECT_PC branch reads neither argument (decompile, 0x833AC0).
            CollectPcStep => () => start.OnCasePickup(null, null),
            _ => null,
        };
        if (handler == null) return false;
        s_stepEnd = true;
        try
        {
            NativeProjection.Run(handler);
            Plugin.Logger.LogInfo($"[tutorial] ended step {here} the game's own way: the other player did it.");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[tutorial] the game's own end of step {here} failed here; following the step directly: {e.Message}");
            return false;
        }
        finally { s_stepEnd = false; }
    }

    /// <summary>The tutorial card a step end opens (<c>GameController.ShowTutorial</c>) stays with the player who did it.</summary>
    [HarmonyLib.HarmonyPatch(typeof(GameController), nameof(GameController.ShowTutorial))]
    internal static class StepEndCard
    {
        private static bool Prefix() => !s_stepEnd;
    }

    private (string Type, int Progress, float At)? _report;
    /// <summary>The /outlines chat command: report on the next frame.</summary>
    internal void ReportNow() => _report = ("", -1, 0f);

    /// <summary>0.71 diagnostics: one second after following a step, name what this game still draws in the tutorial's
    /// yellow (outlines and yellow-line markers), so a leftover is fixed from evidence. 0.73 (game run 6, November 12):
    /// outline colours are named (the game's table: 0 orange, 1 green, 2 red, 3 yellow tip; off is -1), only shown
    /// outlines count, yellow-looking materials are found by colour as well as by name, and /outlines also names what
    /// lies under the middle of the screen.</summary>
    private void ReportLeftovers()
    {
        if (_report is not { } r || Time.realtimeSinceStartup < r.At) return;
        _report = null;
        try
        {
            var found = new List<string>();
            // A busy PC can contribute more than forty outlines by itself. Keep the actual focus diagnostic
            // outside that capped list so /outlines still identifies a guide or marker under the pointer.
            if (r.Progress < 0) Plugin.Logger.LogInfo("[tutorial] /outlines focus: " + string.Join("; ", LookedAt()) + ".");
            if (cakeslice.CustomOutline.OutlineMeshDict is { } outlines)
                foreach (var key in outlines.Keys)
                    found.Add($"outline {(key == null ? "(destroyed)" : key.name + (key.activeInHierarchy ? "" : " (hidden)"))}");
            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var o in UnityEngine.Object.FindObjectsOfType<cakeslice.CustomOutline>())
            {
                if (o == null || o.Colour == cakeslice.CustomOutline.OFF || !o.enabled || !o.gameObject.activeInHierarchy) continue;
                var key = $"{OutlineColour(o.Colour)} outlines under {o.transform.root.name}";
                if (!groups.TryGetValue(key, out var names)) groups[key] = names = new List<string>();
                names.Add(o.name);
            }
            foreach (var (key, names) in groups)
                found.Add($"{names.Count} {key} ({string.Join(", ", names.Distinct().Take(6))}{(names.Distinct().Count() > 6 ? ", ..." : "")})");
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy || Ours(renderer)) continue;
                if (YellowMaterial(renderer) is { } material)
                    found.Add($"yellow {renderer.name} ({material}) under {Path(renderer.transform)}");
            }
            var list = found.Count == 0 ? "nothing" : string.Join("; ", found.Take(40));
            Plugin.Logger.LogInfo(r.Progress < 0 ? $"[tutorial] /outlines: this game draws: {list}." : $"[tutorial] {Short(r.Type)} step {r.Progress} followed; this game still draws: {list}.");
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[tutorial] leftover check failed: " + e.Message); }
    }

    private static string OutlineColour(int colour) =>
        colour == cakeslice.CustomOutline.ACTIONTOOLTIP ? "yellow (tip)"
        : colour == cakeslice.CustomOutline.NOT_REMOVABLE ? "orange"
        : colour == cakeslice.CustomOutline.REMOVABLE ? "green"
        : colour == cakeslice.CustomOutline.BLOCKING ? "red"
        : "colour " + colour;

    private static bool Ours(Renderer renderer) => renderer.transform.root.name.StartsWith("PCBS2Coop", StringComparison.Ordinal);

    private static readonly string[] ColourProperties = { "_BaseColor", "_UnlitColor", "_EmissiveColor", "_Color" };

    /// <summary>The name of a yellow-looking material on this renderer, or null.</summary>
    private static string YellowMaterial(Renderer renderer)
    {
        var materials = renderer.sharedMaterials;
        if (materials == null) return null;
        foreach (var material in materials)
        {
            if (material == null) continue;
            if (material.name.IndexOf("Yellow", StringComparison.OrdinalIgnoreCase) >= 0) return material.name;
            foreach (var property in ColourProperties)
            {
                if (!material.HasProperty(property)) continue;
                var c = material.GetColor(property);
                if (c.r > 0.7f && c.g > 0.5f && c.b < 0.4f && c.r + c.g > 1.5f) return $"{material.name}, {property} {c.r:0.00} {c.g:0.00} {c.b:0.00}";
            }
        }
        return null;
    }

    /// <summary>The shown renderers under the middle of the screen, nearest first.</summary>
    private static IEnumerable<string> LookedAt()
    {
        var camera = Camera.main;
        if (camera == null) return new[] { "no camera to look from" };
        var ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        var hits = new List<(float Distance, string What)>();
        foreach (var renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
        {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy || Ours(renderer)) continue;
            if (!renderer.bounds.IntersectRay(ray, out var distance) || distance > 8f) continue;
            var materials = renderer.sharedMaterials;
            var names = materials == null ? "" : string.Join("/", materials.Where(m => m != null).Select(m => m.name + (m.shader != null ? " [" + m.shader.name + "]" : "")).Take(3));
            hits.Add((distance, $"looking at {Path(renderer.transform)} {distance:0.00} m ({renderer.GetIl2CppType().Name}: {names})"));
        }
        return hits.OrderBy(h => h.Distance).Take(12).Select(h => h.What);
    }

    private static string Path(Transform t)
    {
        var parts = new List<string>();
        for (var i = 0; t != null && i < 4; i++, t = t.parent) parts.Add(t.name);
        if (t != null) parts.Add("...");
        parts.Reverse();
        return string.Join("/", parts);
    }

    private const int ReadNoteStep = 4; // NewStartEnum.READ_NOTE

    /// <summary>0.69 (game run, November 10): Uncle Tim's note (<c>InteractableNote</c>) belongs to the first Career
    /// tutorial. Its step code places it (<c>SetupNoteForReload</c> after a load) and hides it once read
    /// (<c>ReadNote</c>), so a game that follows those steps never showed it, or kept it with its yellow frame after the
    /// other player read it. Place it when following the read-the-note step, and hide it when following a later one.</summary>
    private static void FollowNote(BaseOnboardingClass tutorial, OnboardingFact fact)
    {
        var start = tutorial.TryCast<NewStartOnboarding>();
        if (start == null || fact.State != 1) return;
        try
        {
            var notes = UnityEngine.Object.FindObjectsOfType<InteractableNote>();
            if (fact.Progress == ReadNoteStep && notes.Length == 0)
            {
                NativeProjection.Run(start.SetupNoteForReload);
                Plugin.Logger.LogInfo("[tutorial] placed Uncle Tim's note: the other player's game reached that step.");
            }
            else if (fact.Progress > ReadNoteStep && notes.Length > 0)
            {
                NativeProjection.Run(() =>
                {
                    NewStartOnboarding.isNoteRead = true;
                    foreach (var note in notes) if (note != null) note.gameObject.SetActive(false);
                });
                Plugin.Logger.LogInfo("[tutorial] put Uncle Tim's note away: the other player read it.");
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[tutorial] Uncle Tim's note could not follow the other player: " + e.Message); }
    }

    private static string Short(string type) => type.Length > 48 ? type[^48..] : type;

    /// <summary>The first tutorial's removal methods remember the slot and SKU for their later refits. A player who
    /// follows the other player's removal never runs that setup. Its fixed office PC still has the same shared
    /// integral panel slots while those panels are off; restore the references before the native refit step runs.</summary>
    private static void BindRefitPanels(NewStartOnboarding tutorial, Case mesh)
    {
        var coop = CoopController.Instance;
        if (tutorial == null || mesh == null || coop?.Objects.Initialized != true) return;
        var id = coop.Objects.IdOf(mesh);
        var pc = coop.Objects.Model.Pcs.SingleOrDefault(p => p.Id == id);
        if (pc == null) return;
        (Slot Slot, string Sku) Panel(string known, string suffix)
        {
            var fact = pc.Resources.SingleOrDefault(r => r.Kind == AssemblyResourceKind.Slot &&
                !string.IsNullOrEmpty(r.IntegralHome) && (string.IsNullOrEmpty(known)
                    ? r.IntegralHome.EndsWith(suffix, StringComparison.Ordinal) : r.IntegralHome == known));
            return fact == null ? default : (coop.Objects.Assembly.NativeFor(id, fact.Id)?.TryCast<Slot>(), fact.IntegralHome);
        }
        // These are the serialized panel names of NewStartOnboarding.OFFICE_PC_PREBUILD (office_no_gpu).
        var pci = Panel(tutorial.pciLockID, "/PCI Plate");
        if (pci.Slot != null && (tutorial.pciSlot == null || tutorial.pciSlot.Pointer != pci.Slot.Pointer || tutorial.pciLockID != pci.Sku))
        {
            tutorial.pciSlot = pci.Slot; tutorial.pciLockID = pci.Sku;
            Plugin.Logger.LogInfo($"[tutorial] bound the PCI lock refit to {pci.Slot.name} ({pci.Sku}) on {id}.");
        }
        var side = Panel(tutorial.sidePanelID, "/Left Lid");
        if (side.Slot != null && (tutorial.sidePanelSlot == null || tutorial.sidePanelSlot.Pointer != side.Slot.Pointer || tutorial.sidePanelID != side.Sku))
        {
            tutorial.sidePanelSlot = side.Slot; tutorial.sidePanelID = side.Sku;
            Plugin.Logger.LogInfo($"[tutorial] bound the side panel refit to {side.Slot.name} ({side.Sku}) on {id}.");
        }
    }

    /// <summary>0.68 (game run, November 10): the first Career tutorial's PC steps look up the side panel, the PCI lock
    /// and its covers when this player enters work mode. When the other player already took one off, the game's own
    /// step code fails (<c>RemoveSidePanel</c>), and the work mode it was entering stops part-way. In co-op that step is
    /// skipped instead; the shared tutorial step still follows the other player.</summary>
    [HarmonyLib.HarmonyPatch]
    internal static class StepGuard
    {
        private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
            new[] { "RemoveSidePanel", "RemovePCILock", "RemovePCICovers", "InstallGPU", "InstallPCILock", "InstallSidePanel", "CableGPU" }
                .Select(name => (System.Reflection.MethodBase)HarmonyLib.AccessTools.Method(typeof(NewStartOnboarding), name)
                    ?? throw new MissingMethodException(nameof(NewStartOnboarding), name));

        private static void Prefix(NewStartOnboarding __instance, Case __0, System.Reflection.MethodBase __originalMethod)
        {
            if (!NativePcLifecycle.InCoopWorkshop || __originalMethod.Name is not ("InstallPCILock" or "InstallSidePanel")) return;
            BindRefitPanels(__instance, __0);
        }

        private static Exception Finalizer(Exception __exception, System.Reflection.MethodBase __originalMethod)
        {
            if (__exception == null || !NativePcLifecycle.InCoopWorkshop) return __exception;
            if (Logged.Add(__originalMethod.Name))
                Plugin.Logger.LogWarning($"[tutorial] the tutorial step {__originalMethod.Name} failed on this game (the other player may have done it already); skipped: {__exception.Message}");
            return null;
        }
    }
}

using HarmonyLib;
using PCBS2Coop.Network;
using UnityEngine;

namespace PCBS2Coop;

/// <summary>
/// Native side of shared latches (task 4d): RAM clips, CPU levers (AM3/AM4 <c>AM3Slot</c>), LGA shields, M.2 covers,
/// PCI cover locks, heatsink pins and hinged doors. The player's click is <c>PinTick</c> on the work state or the
/// Cable tool (directly, or through <c>GameController.DoAfterLongPress</c> for long-press latches), which calls
/// <c>TogglePin</c>. Only a toggle inside that gesture becomes a host-ordered request; the game's own calls (an
/// install closing its clips, a load reopening saved latches) pass through. After acceptance both games run the
/// native toggle with its animation; a game where the latch is blocked locally sets the state directly. Screw-held
/// covers (<c>ScrewPin</c>, none in the default workshop) are refused until they are probed (owner decision, 4d).
/// </summary>
internal sealed class NativeMechanisms : IMechanismNative
{
    private const float ShowTimeout = 5f;
    private sealed class Showing
    {
        public NetObjectId Pc;
        public bool Open, Tried;
        public float Until;
    }
    private readonly CoopController _coop;
    private readonly WorldSync _world;
    private readonly Dictionary<OperationId, NetObjectId> _pending = new();
    private readonly Dictionary<NetObjectId, Showing> _showing = new();
    private bool _submitting, _settling;
    public MechanismWorld Handler { get; }

    public NativeMechanisms(CoopController coop, WorldSync world)
    {
        _coop = coop; _world = world;
        Handler = new MechanismWorld(coop.Objects.Model, coop.Activity, this, world.IsAuthority);
        world.Accepted += (OperationId op, ulong revision) => _pending.Remove(op);
        world.Rejected += OnRejected;
        world.GenerationStarted += (uint generation, string reason) => { _pending.Clear(); _showing.Clear(); };
    }

    private bool Active => _world.Admitted && _world.Divergence == null &&
        _coop.Objects.Initialized && _coop.Activity.Get(_world.Local) != null;
    /// <summary>A toggle is waiting for the host or still being shown: saves and snapshots wait for it.</summary>
    internal bool Busy => _pending.Count > 0 || _showing.Count > 0;

    // ---------------- native helpers ----------------

    /// <summary>The native toggle's own refusal: <c>TogglePin</c> does nothing while a latch is blocked.</summary>
    private static bool Blocked(CPin pin)
    {
        var plain = pin.TryCast<Pin>(); if (plain != null) return plain.IsBlocked();
        var am3 = pin.TryCast<AM3Slot>(); if (am3 != null) return am3.IsBlocked();
        var lga = pin.TryCast<LGASlot>(); if (lga != null) return lga.IsBlocked();
        return true;
    }
    private static string Describe(CPin pin) => pin == null ? "missing latch" : $"{pin.GetIl2CppType().Name} '{pin.name}'";
    private static string State(bool open) => open ? "open" : "closed";
    private CPin Latch(NetObjectId pc, NetObjectId id) => _coop.Objects.Assembly.NativeFor(pc, id)?.TryCast<CPin>();

    // ---------------- requests (local input) ----------------

    /// <summary>A gesture toggle. Returns whether the native toggle should run now (only when it would do nothing).</summary>
    internal bool Request(CPin pin)
    {
        if (pin.TryCast<ScrewPin>() != null) { Refuse("Screw-held covers aren't shared in co-op yet."); return false; }
        // Blocked or still moving: the native toggle is a no-op here (4b: a toggle call is an attempt, not a result).
        if (pin.IsAnimating() || Blocked(pin)) return true;
        if (!Active) { Refuse("The shared world isn't ready on this game yet."); return false; }
        var (pcId, latchId) = _coop.Objects.Assembly.Find(pin);
        var pc = _coop.Objects.Model.Pcs.SingleOrDefault(p => p.Id == pcId);
        var latch = pc?.Resources.SingleOrDefault(r => r.Id == latchId);
        if (latch == null) { Refuse("That latch has no co-op identity yet."); return false; }
        // A held button repeats the gesture; one request per latch until it has been answered and shown.
        if (_showing.ContainsKey(latchId) || _pending.ContainsValue(latchId)) return false;
        if (pin.IsOpen() != latch.Open)
        {
            _coop.ProjectionFailed($"Latch {latchId} on PC {pcId} shows {State(pin.IsOpen())}, the shared world says {State(latch.Open)}", null);
            return false;
        }
        var actor = _coop.Activity.Get(_world.Local)!;
        _submitting = true;
        try
        {
            if (!_world.Submit(CommandTypes.Mechanism, MechanismWorld.Toggle(actor.Epoch, pcId, latchId, latch.Version, !latch.Open), out var op))
            { Refuse("Not connected to the host's world."); return false; }
            if (!_world.IsAuthority) _pending[op] = latchId;
            Plugin.Logger.LogInfo($"[latches] {Describe(pin)} ({latchId}, PC {pcId}) → {State(!latch.Open)} requested ({op}).");
        }
        finally { _submitting = false; }
        return false;
    }

    private void OnRejected(OperationId op, RejectReason reason, string text)
    {
        if (op.Actor != _world.Local || _settling || (!_pending.Remove(op) && !_submitting)) return;
        Refuse(text);
    }

    private void Refuse(string why) => _coop.Notice("Latch refused: " + why.Replace(Handler.Name + ": ", ""));

    // ---------------- IMechanismNative ----------------

    public string CheckHost(in CommandContext ctx, PcFact pc, AssemblyResource latch, bool open)
    {
        var pin = Latch(pc.Id, latch.Id);
        if (pin == null) return "That latch is missing on the host's game.";
        if (_showing.ContainsKey(latch.Id) || pin.IsAnimating()) return "That latch is still moving.";
        if (pin.IsOpen() != latch.Open) return "The host's game shows that latch differently.";
        if (pin.TryCast<ScrewPin>() != null) return "Screw-held covers aren't shared in co-op yet.";
        return Blocked(pin) ? "Something is blocking that latch." : null;
    }

    public void Changed(in MutationContext ctx, PcFact pc, IReadOnlyList<AssemblyResource> changed, bool settle)
    {
        foreach (var latch in changed)
        {
            try
            {
                var pin = Latch(pc.Id, latch.Id);
                if (pin == null)
                {
                    // Not shown here (a record-only PC). Its next native load is checked against the model.
                    if (_coop.Objects.Projections.TryGet(pc.Id, out var mesh) && mesh != null)
                        throw new InvalidDataException("the latch is not bound on this game");
                    continue;
                }
                // The newest accepted state replaces one still being shown. A settle (a host save/snapshot boundary)
                // closes instantly, as the native load would (4b N3), once any animation here has ended.
                _showing[latch.Id] = new Showing { Pc = pc.Id, Open = latch.Open, Tried = settle, Until = Time.realtimeSinceStartup + ShowTimeout };
                Show(latch.Id);
                if (settle) continue;
                Plugin.Logger.LogInfo($"[latches] {Describe(pin)} ({latch.Id}, PC {pc.Id}) → {State(latch.Open)} by {ctx.Op}" + (ctx.FromLocalPlayer ? " (here)." : " (projected)."));
            }
            catch (Exception e)
            {
                _showing.Remove(latch.Id);
                _coop.ProjectionFailed($"Latch {latch.Id} on PC {pc.Id} was {(settle ? "closed for a save" : "changed")}, but this game could not show it", e);
            }
        }
    }

    /// <summary>Drive one accepted toggle toward its state: the native animated toggle first; where the latch is
    /// blocked on this game only (its own preview, for example), set the state directly.</summary>
    private void Show(NetObjectId id)
    {
        var s = _showing[id];
        var pin = Latch(s.Pc, id);
        if (pin == null)
        {
            // The PC was moved or reloaded meanwhile; its new projection is validated against the model.
            _showing.Remove(id);
            Plugin.Logger.LogInfo($"[latches] latch {id} on PC {s.Pc} is no longer shown; its next load is checked instead.");
            return;
        }
        if (pin.IsAnimating()) return;
        if (pin.IsOpen() == s.Open) { _showing.Remove(id); return; }
        if (!s.Tried && !Blocked(pin))
        {
            s.Tried = true;
            NativeProjection.Run(() => pin.TogglePin());
            if (pin.IsAnimating() || pin.IsOpen() == s.Open) { if (!pin.IsAnimating()) _showing.Remove(id); return; }
        }
        s.Tried = true;
        if (s.Open) NativeProjection.Run(() => pin.PopOpen());
        else if (pin.TryCast<Pin>() is { } plain) NativeProjection.Run(() => plain.Close());
        if (pin.IsAnimating()) return;
        if (pin.IsOpen() != s.Open) throw new InvalidDataException($"{Describe(pin)} could not be {State(s.Open)}");
        _showing.Remove(id);
    }

    /// <summary>Per frame: finish showing accepted toggles once their animation ends.</summary>
    public void Update()
    {
        if (_showing.Count == 0) return;
        foreach (var id in _showing.Keys.ToArray())
        {
            if (!_showing.TryGetValue(id, out var s)) continue;
            try
            {
                if (Time.realtimeSinceStartup > s.Until) throw new TimeoutException($"still not {State(s.Open)} after {ShowTimeout} s");
                Show(id);
            }
            catch (Exception e)
            {
                _showing.Remove(id);
                _coop.ProjectionFailed($"Latch {id} on PC {s.Pc} was changed, but this game could not show it", e);
            }
        }
    }

    // ---------------- host save/snapshot boundary ----------------

    /// <summary>Host, just before capturing a save or snapshot: close latches over installed parts on both games
    /// (owner decision, 4d: what a single-player reload does). Throws when they can't be settled now.</summary>
    internal void SettleForCapture()
    {
        if (!_world.IsAuthority || !_world.Admitted || !_coop.Objects.Initialized) return;
        var pcs = MechanismWorld.Unsettled(_coop.Objects.Model).ToArray();
        if (pcs.Length == 0) return;
        if (Busy) throw new InvalidOperationException("A latch is still moving; wait before saving.");
        _settling = true;
        try
        {
            if (!_world.Submit(CommandTypes.Mechanism, MechanismWorld.Settle(pcs), out _) || MechanismWorld.Unsettled(_coop.Objects.Model).Any())
                throw new InvalidOperationException("A latch over an installed part is in use; wait before saving.");
        }
        finally { _settling = false; }
        if (Busy) throw new InvalidOperationException("A latch over an installed part is still closing; wait before saving.");
        Plugin.Logger.LogInfo($"[latches] closed latches over installed parts on {pcs.Length} PC(s) before the capture.");
    }

    internal string Report()
    {
        var rows = new List<string>();
        foreach (var pc in _coop.Objects.Model.Pcs)
            foreach (var r in pc.Resources.Where(r => r.Kind is AssemblyResourceKind.Pin or AssemblyResourceKind.Socket))
            {
                var pin = Latch(pc.Id, r.Id);
                var clip = pc.Resources.FirstOrDefault(s => s.LoadCloses.Contains(r.Id));
                var where = clip == null ? "" : clip.Occupant.IsNone ? " clip(empty slot)" : " clip(installed)";
                rows.Add($"{pc.Id}@{pc.Location.Key.Split('|')[0].Split('/').Last()}/{r.Id} v{r.Version} {State(r.Open)}{where}" + (pin == null ? " (not shown)" :
                    $" native={State(pin.IsOpen())}{(pin.IsAnimating() ? " moving" : "")}{(Blocked(pin) ? " blocked" : "")} {Describe(pin)}"));
            }
        return string.Join("; ", rows);
    }

    // ---------------- interception ----------------

    private static int _gesture;
    private static NativeMechanisms Current => CoopController.Instance?.Mechanisms;

    /// <summary>The player's latch click: both work-state <c>PinTick</c>s and their long-press callbacks. The two
    /// callbacks have identical bodies and may be one native function; each native function is hooked once.</summary>
    [HarmonyPatch]
    internal static class GestureScope
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var hooked = new HashSet<IntPtr>();
            foreach (var method in new[]
            {
                AccessTools.Method(typeof(WorkingOnComputerState), "PinTick"),
                AccessTools.Method(typeof(LayCableState), "PinTick"),
                AccessTools.Method(typeof(WorkingOnComputerState.__c__DisplayClass51_0), "_PinTick_b__0"),
                AccessTools.Method(typeof(LayCableState.__c__DisplayClass32_0), "_PinTick_b__0"),
            })
            {
                var code = method == null ? IntPtr.Zero : GamePatches.NativeCode(method);
                if (code == IntPtr.Zero) throw new MissingMethodException("latch gesture method missing");
                if (hooked.Add(code)) yield return method;
                else Plugin.Logger.LogInfo($"[latches] {method.DeclaringType?.Name}.{method.Name} shares its native code with an already hooked gesture.");
            }
        }
        private static void Prefix() => _gesture++;
        private static void Finalizer() => _gesture--;
    }

    /// <summary>Every <c>TogglePin</c> implementation, each native function once. Outside a gesture this passes.</summary>
    [HarmonyPatch]
    internal static class ToggleIntercept
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var hooked = new HashSet<IntPtr>();
            foreach (var type in new[] { typeof(Pin), typeof(ScrewPin), typeof(AM3Slot), typeof(LGASlot) })
            {
                var method = AccessTools.DeclaredMethod(type, nameof(CPin.TogglePin));
                var code = method == null ? IntPtr.Zero : GamePatches.NativeCode(method);
                if (code == IntPtr.Zero) throw new MissingMethodException(type.Name, nameof(CPin.TogglePin));
                if (hooked.Add(code)) yield return method;
                else Plugin.Logger.LogInfo($"[latches] {type.Name}.TogglePin shares its native code with an already hooked latch type.");
            }
        }
        private static bool Prefix(CPin __instance)
        {
            if (_gesture == 0 || NativeProjection.Active || !NativePcLifecycle.InCoopWorkshop) return true;
            try
            {
                if (Current == null) { CoopController.Instance?.Notice("Latch refused: the shared world isn't ready on this game yet."); return false; }
                return Current.Request(__instance);
            }
            catch (Exception e) { Plugin.Logger.LogError("[latches] toggle request failed: " + e); return false; }
        }
    }
}

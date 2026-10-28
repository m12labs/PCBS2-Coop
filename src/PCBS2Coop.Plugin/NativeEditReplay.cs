using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace PCBS2Coop;

/// <summary>
/// Plays a component install or removal with the game's own <c>InstallingPartState</c> (part animation, automatic screws
/// and standoffs, orientation, settlement with cables and inventory), <b>detached</b> from this game's state machine: the
/// state is never made current, so this player's camera, input and state stack stay theirs (task 4e, owner decision:
/// the other player sees the single-player animations). Decompile findings this depends on (milestone 1 in HANDOFF.md):
/// <list type="bullet">
/// <item><c>Tick</c> only rotates the camera and calls <c>m_updateFn</c> (the current step); replay calls the step.</item>
/// <item>Steps end with <c>GoToPreviousState</c>/<c>GoToLastDifferentState</c>; during a replay step those are skipped
/// and mark the edit finished, then replay runs <c>OnStateDestroy</c> itself.</item>
/// <item>Removal starts in the <c>WorkingOnComputerState.Uninstall</c> coroutine (unplug cables, wait, then
/// <c>UninstallState</c> + <c>SetCurrentState</c>); replay steps that coroutine on a stand-in work state and keeps the
/// state instead of making it current.</item>
/// <item><c>OnStateDestroy</c> connects cables only when the previous state is a work state in assembly mode; replay hands
/// it the stand-in with the editor's tool mode, and the editor's cable choice (a global game setting) around it.</item>
/// <item>Input reads inside steps (<c>InputButton.IsDown/IsHeld/IsUp</c>) return false, so this player's mouse can't
/// tighten screws or rotate a fan. Orientation takes the editor's final angle and is accepted.</item>
/// </list>
/// </summary>
internal sealed class NativeEditReplay
{
    internal sealed class CableChoice
    {
        public string Type, Connector;
        public int[] Pins;
        /// <summary>This game's current cable choice (the editor reads it when its edit settles).</summary>
        public static CableChoice Current()
        {
            var gc = GameController.Get();
            return new() { Type = gc?.m_cableType?.m_id, Connector = gc?.m_cableConnectorType?.m_id, Pins = gc?.m_pinMaterialIndices?.ToArray() };
        }
    }

    /// <summary>One replay. Install: <see cref="Part"/> into <see cref="Slot"/>. Removal: <see cref="Component"/> out of
    /// its slot, or only its cables when <see cref="UnplugOnly"/> (the editor pressed Esc during the cable step, 4b N2).</summary>
    internal sealed class Plan
    {
        public string Label = "edit";
        public Case Case;
        public BenchSlot Bench;
        public Slot Slot;
        public PartInstance Part;
        public ComponentPC Component;
        public bool UnplugOnly;
        /// <summary>The removal without the cable step (<c>DisassemblyTick</c>'s direct callback): the part animates out
        /// straight away.</summary>
        public bool Direct;
        /// <summary>The editor's final orientation-step angle (<c>m_degrees</c>), or NaN when the part has no orientation step.</summary>
        public float Degrees = float.NaN;
        /// <summary>The editor's work tool at settlement (<c>WorkingOnComputerState.Mode</c>: 1 assembly, 0 disassembly) and selection.</summary>
        public int Mode = 1, Selected = 1;
        public CableChoice Cables;
        /// <summary>Called once: null on success, otherwise why it failed.</summary>
        public System.Action<string> Finished;
    }

    private enum Phase { Loading, Unplugging, Animating }
    private sealed class Run
    {
        public Plan Plan;
        public Phase Phase;
        public InstallingPartState State;
        public Il2CppSystem.Collections.IEnumerator Iterator;
        public CustomYieldInstruction Waiting;
        public bool Exited, Loaded;
        public WorkingOnComputerState StandIn;
        public float Started;
    }

    public const float Timeout = 60f;
    private readonly List<Run> _runs = new();
    private readonly List<Plan> _queued = new();

    /// <summary>The run whose native step is executing right now (synchronous calls only).</summary>
    private static Run _step;
    private static bool _inIterator, _inDestroy;
    private static InstallingPartState _captured;

    /// <summary>A replay is running or waiting on this game: saves and snapshots wait for it.</summary>
    public bool Busy => _runs.Count > 0 || _queued.Count > 0;
    public bool Running(Case mesh) => mesh != null && (_runs.Any(r => r.Plan.Case?.Pointer == mesh.Pointer) || _queued.Any(p => p.Case?.Pointer == mesh.Pointer));
    /// <summary>Inside a replay's native call: other interception (gestures, gates) must let the game's own calls through.</summary>
    internal static bool Active => _step != null;

    /// <summary>Start now, or after the running replay on the same PC (replays of one PC play in order).</summary>
    public void Start(Plan plan)
    {
        if (plan?.Case == null || plan.Slot == null || (plan.Part == null) == (plan.Component == null)) throw new ArgumentException("Invalid replay plan.");
        if (Running(plan.Case)) { _queued.Add(plan); return; }
        Begin(plan);
    }

    /// <summary>Drop every replay without finishing it (world reset, leaving the workshop).</summary>
    public void Reset()
    {
        foreach (var run in _runs.ToArray()) Fail(run, "the world was reset", notify: false);
        _runs.Clear(); _queued.Clear();
    }

    private void Begin(Plan plan)
    {
        var run = new Run { Plan = plan, Started = Time.realtimeSinceStartup };
        _runs.Add(run);
        Plugin.Logger.LogInfo($"[replay] {plan.Label}: starting ({(plan.Part != null ? "install" : plan.UnplugOnly ? "unplug" : plan.Direct ? "direct removal" : "removal")}).");
        try
        {
            if (plan.Part != null)
            {
                run.Phase = Phase.Loading;
                var desc = plan.Part.GetPart() ?? throw new InvalidDataException($"part {plan.Part.GetPartId()} is unknown");
                if (PartsDatabase.s_instance.HasLoadedPart(desc, out _)) run.Loaded = true;
                else
                {
                    PartsDatabase.TestDelegate loaded = (System.Action<PartsDatabase.LoadedPart, Transform>)((asset, parent) => run.Loaded = true);
                    PartsDatabase.s_instance.InitPartLoading(desc, loaded, null);
                }
            }
            else if (plan.Direct)
            {
                // DisassemblyTick b__1 (decompile): Case.OnChangePart(part, true, false), the multi-GPU bridge for a card,
                // then UninstallState, which the editor's game makes current.
                InstallingPartState state = null;
                var comp = plan.Component;
                Native(run, () =>
                {
                    plan.Case.OnChangePart(comp.gameObject, true, false);
                    var type = (int)(comp.GetPartDesc()?.m_type ?? 0);
                    if (type is 6 or 24) comp.Slot?.HandleMGPUBridge(true);
                    state = InstallingPartState.UninstallState(plan.Case, Camera(plan), comp);
                });
                Started(run, state ?? throw new InvalidDataException("the removal state was not created"));
            }
            else
            {
                run.Phase = Phase.Unplugging;
                run.StandIn = StandIn(plan.Case, Camera(plan), 0, 0);
                run.Iterator = run.StandIn.Uninstall(plan.Component);
            }
        }
        catch (Exception e) { Fail(run, e.ToString()); }
    }

    /// <summary>Per frame, main thread.</summary>
    public void Update()
    {
        foreach (var run in _runs.ToArray())
        {
            try
            {
                if (Time.realtimeSinceStartup - run.Started > Timeout) throw new TimeoutException($"still running after {Timeout} s");
                if (run.Plan.Case == null || run.Plan.Slot == null) throw new InvalidDataException("the PC or slot was destroyed");
                switch (run.Phase)
                {
                    case Phase.Loading:
                        if (run.Loaded) Construct(run);
                        break;
                    case Phase.Unplugging:
                        Unplug(run);
                        break;
                    case Phase.Animating:
                        Step(run);
                        break;
                }
            }
            catch (Exception e) { Fail(run, e.ToString()); }
        }
    }

    private void Construct(Run run)
    {
        var p = run.Plan;
        if (!PartsDatabase.s_instance.HasLoadedPart(p.Part.GetPart(), out _)) throw new InvalidDataException("the part asset did not load");
        if (p.Slot.GetComponent() != null) throw new InvalidDataException("the slot is not empty on this game");
        InstallingPartState state = null;
        Native(run, () => state = new InstallingPartState(p.Case, Camera(p), p.Part, p.Slot));
        Started(run, state ?? throw new InvalidDataException("the install state was not created"));
    }

    /// <summary>A constructed state: automatic screws/standoffs (as the editor's game with its upgrades), then animate.
    /// The constructor registers the part with this player's work selection; undo that unless they work on this PC.</summary>
    private void Started(Run run, InstallingPartState state)
    {
        run.State = state;
        state.ForceUpgrades = true;
        Native(run, state.InitUpgrades);
        run.Phase = Phase.Animating;
        Plugin.Logger.LogInfo($"[replay] {run.Plan.Label}: animating.");
        if (run.Exited) Settle(run);
    }

    private void Unplug(Run run)
    {
        if (run.Waiting != null)
        {
            if (run.Waiting.keepWaiting) return;
            run.Waiting = null;
        }
        _captured = null;
        bool more;
        // After the cable wait the coroutine continues only while GameController.m_currentState is a work state
        // (C4e02; that is also why Esc during the cable step keeps the part in, 4b N2). For a full removal it sees
        // the stand-in, for this synchronous call only. For UnplugOnly it sees this player's real state and stops
        // where the editor's did.
        var gc = GameController.Get();
        var current = gc.m_currentState;
        _inIterator = true;
        try
        {
            if (!run.Plan.UnplugOnly) gc.m_currentState = run.StandIn;
            more = Native(run, () => run.Iterator.MoveNext());
        }
        finally
        {
            _inIterator = false;
            gc.m_currentState = current;
        }
        if (run.Plan.UnplugOnly && _captured != null)
            Plugin.Logger.LogWarning($"[replay] {run.Plan.Label}: this player is working on a PC, so the removal reached its part step; dropped (cables only).");
        if (_captured != null)
        {
            var state = _captured; _captured = null;
            if (run.Plan.UnplugOnly) { Finish(run); return; }
            Started(run, state);
            return;
        }
        if (!more) { Finish(run); return; }
        // `yield return null` resumes next frame (the next Update); a WaitUntil is polled.
        run.Waiting = run.Iterator.Current?.TryCast<CustomYieldInstruction>();
    }

    private void Step(Run run)
    {
        var step = run.State.m_updateFn;
        if (step == null) throw new InvalidDataException("the edit has no current step");
        Native(run, step.Invoke);
        if (run.Exited) Settle(run);
    }

    /// <summary>The game's settlement for a finished edit, as on the editor's game.</summary>
    private void Settle(Run run)
    {
        var p = run.Plan;
        run.StandIn ??= StandIn(p.Case, Camera(p), p.Mode, p.Selected);
        run.StandIn.m_mode = (WorkingOnComputerState.Mode)p.Mode;
        run.StandIn.m_currentSelected = (WorkingOnComputerState.Mode)p.Selected;
        _inDestroy = true;
        try { Native(run, run.State.OnStateDestroy); }
        finally { _inDestroy = false; }
        Finish(run);
    }

    private void Finish(Run run)
    {
        _runs.Remove(run);
        Plugin.Logger.LogInfo($"[replay] {run.Plan.Label}: finished in {Time.realtimeSinceStartup - run.Started:F2} s.");
        Next(run.Plan.Case);
        try { run.Plan.Finished?.Invoke(null); }
        catch (Exception e) { Plugin.Logger.LogError("[replay] completion callback failed: " + e); }
    }

    private void Fail(Run run, string why, bool notify = true)
    {
        _runs.Remove(run);
        Plugin.Logger.LogError($"[replay] {run.Plan.Label}: failed: {why}");
        if (!notify) return;
        Next(run.Plan.Case);
        try { run.Plan.Finished?.Invoke(why); }
        catch (Exception e) { Plugin.Logger.LogError("[replay] failure callback failed: " + e); }
    }

    private void Next(Case mesh)
    {
        var next = _queued.FirstOrDefault(p => p.Case != null && mesh != null && p.Case.Pointer == mesh.Pointer);
        if (next == null) { _queued.RemoveAll(p => p.Case == null); return; }
        _queued.Remove(next);
        Begin(next);
    }

    /// <summary>Run one synchronous native call of a replay with its guards and the editor's cable choice.</summary>
    private static void Native(Run run, System.Action call) => Native(run, () => { call(); return true; });
    private static T Native<T>(Run run, Func<T> call)
    {
        var gc = GameController.Get() ?? throw new InvalidDataException("the game controller is missing");
        var saved = (gc.m_cableType, gc.m_pinMaterialIndices, gc.m_cableInstance, gc.m_cableConnectorType);
        var outer = _step;
        _step = run;
        try
        {
            if (run.Plan.Cables is { } c)
            {
                if (!string.IsNullOrEmpty(c.Type)) gc.m_cableType = PartsDatabase.s_instance.GetDesc(c.Type, null);
                if (!string.IsNullOrEmpty(c.Connector)) gc.m_cableConnectorType = PartsDatabase.s_instance.GetDesc(c.Connector, null);
                if (c.Pins != null) gc.m_pinMaterialIndices = c.Pins;
            }
            return NativeProjection.Run(call);
        }
        finally
        {
            _step = outer;
            (gc.m_cableType, gc.m_pinMaterialIndices, gc.m_cableInstance, gc.m_cableConnectorType) = saved;
        }
    }

    private static bool WorkingOn(Case mesh)
    {
        var work = GameController.Get()?.GetCurrentState()?.TryCast<WorkingOnComputerStateBase>();
        return work != null && mesh != null && work.m_case != null && work.m_case.Pointer == mesh.Pointer;
    }

    /// <summary>The bench's own work camera: the base state takes its workstation from it. It is not claimed.</summary>
    private static Camera Camera(Plan p)
    {
        // The base state reads camera.GetComponent<WorkStation>(), so the bench's workstation sits on its work camera.
        // C4e01: BenchSlot.orbitCamera is empty on the default benches.
        var bench = p.Bench ?? throw new InvalidDataException("the PC's bench is unknown");
        var camera = bench.m_workStation != null ? bench.m_workStation.GetComponent<UnityEngine.Camera>() : null;
        if (camera == null) camera = bench.orbitCamera;
        if (camera == null) camera = UnityEngine.Camera.main;
        if (camera == null) throw new InvalidDataException("no camera for the bench");
        if (camera.GetComponent<WorkStation>() == null)
            Plugin.Logger.LogWarning($"[replay] {p.Label}: the camera {camera.name} has no workstation; using it anyway.");
        else Plugin.Logger.LogInfo($"[replay] {p.Label}: using camera {camera.name} (enabled {camera.enabled}).");
        return camera;
    }

    /// <summary>A work state object that is never entered: the removal coroutine's owner and the settlement's
    /// "previous state". Allocated without its constructor (which would claim a camera); only these fields are read.</summary>
    private static WorkingOnComputerState StandIn(Case mesh, Camera camera, int mode, int selected)
    {
        var standIn = new WorkingOnComputerState(IL2CPP.il2cpp_object_new(Il2CppClassPointerStore<WorkingOnComputerState>.NativeClassPtr));
        standIn.ctrl = GameController.Get(); // State.ctrl: the coroutine's SetCurrentState receiver (C4e02)
        standIn.m_case = mesh;
        standIn.m_camera = camera;
        standIn.m_mode = (WorkingOnComputerState.Mode)mode;
        standIn.m_currentSelected = (WorkingOnComputerState.Mode)selected;
        return standIn;
    }

    // ---------------- guards (always installed; inert unless a replay step runs) ----------------

    [HarmonyPatch(typeof(GameController), nameof(GameController.GoToPreviousState), typeof(bool))]
    internal static class PreviousGuard
    {
        private static bool Prefix() { if (_step == null) return true; _step.Exited = true; return false; }
    }

    [HarmonyPatch(typeof(GameController), nameof(GameController.GoToLastDifferentState))]
    internal static class LastDifferentGuard
    {
        private static bool Prefix() { if (_step == null) return true; _step.Exited = true; return false; }
    }

    /// <summary>The removal coroutine's <c>SetCurrentState(UninstallState)</c>: keep the state, don't enter it.</summary>
    [HarmonyPatch(typeof(GameController), nameof(GameController.SetCurrentState))]
    internal static class EnterGuard
    {
        private static bool Prefix(State state)
        {
            if (_step == null) return true;
            if (_inIterator && state?.TryCast<InstallingPartState>() is { } install) _captured = install;
            else Plugin.Logger.LogWarning($"[replay] skipped an unexpected state change to {state?.GetIl2CppType().Name} during a replay.");
            return false;
        }
    }

    [HarmonyPatch(typeof(GameController), nameof(GameController.GetPreviousState))]
    internal static class PreviousStateGuard
    {
        private static bool Prefix(ref State __result)
        {
            if (_step == null || !_inDestroy || _step.StandIn == null) return true;
            __result = _step.StandIn;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class InputGuard
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(typeof(InputButton), nameof(InputButton.IsDown)),
            AccessTools.Method(typeof(InputButton), nameof(InputButton.IsHeld)),
            AccessTools.Method(typeof(InputButton), nameof(InputButton.IsUp)),
        };
        private static bool Prefix(ref bool __result)
        {
            if (_step == null) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>The work selection list (static, one per work session) belongs to this player: a replay changes it only
    /// while they work on that PC. Outside work mode the list is null and the native add throws (C4e04).</summary>
    [HarmonyPatch]
    internal static class SelectableGuard
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(typeof(WorkStation), nameof(WorkStation.AddSelectable)),
            AccessTools.Method(typeof(WorkStation), nameof(WorkStation.RemoveSelectable)),
        };
        private static bool Prefix() => _step == null || WorkingOn(_step.Plan.Case);
    }

    /// <summary>The orientation step (<c>OrientComponentStep</c> forwards here): the editor's final angle, accepted.</summary>
    [HarmonyPatch(typeof(InstallingPartState), nameof(InstallingPartState.DoOrientation))]
    internal static class OrientationGuard
    {
        private static void Prefix(InstallingPartState __instance)
        {
            if (_step?.State == null || __instance == null || __instance.Pointer != _step.State.Pointer || float.IsNaN(_step.Plan.Degrees)) return;
            __instance.m_degrees = _step.Plan.Degrees;
            __instance.m_accept = true;
        }
    }
}

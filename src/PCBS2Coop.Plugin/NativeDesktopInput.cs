using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using PCBS2Coop.Network;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>Mirrors desktop, BIOS and boot-screen input (owner decision, November 1, 2026): this player's own input
/// runs natively at once and is sent to the other game, which replays it through a virtual pointer of its own. The
/// replay never touches the game's single local cursor (<c>InputModule</c>) and gives the selection back afterwards,
/// so the other player's own mouse and typing keep working.</summary>
internal sealed partial class NativeDesktopInput : IDesktopInputNative
{
    /// <summary>True while a replayed event runs native UI code. Hooks that would turn a click into a new request
    /// (BIOS Apply/Factory Reset) let it pass silently: the editor's own request carries the result.</summary>
    internal static bool Injecting { get; private set; }

    private const float SendInterval = 1f / 30f, SurfaceWait = 4f, BiosWait = 20f, EnterGrace = .5f;

    private readonly CoopController _coop;
    private readonly WorldSync _world;
    public DesktopInputWorld Handler { get; }

    public NativeDesktopInput(CoopController coop, WorldSync world)
    {
        _coop = coop; _world = world;
        Handler = new DesktopInputWorld(coop.Objects.Model, coop.Activity, this);
        world.GenerationStarted += (uint _, string _) => Reset();
        world.Rejected += (OperationId op, RejectReason reason, string text) =>
        {
            if (op.Actor == world.Local && _sent.Remove(op))
                Plugin.Logger.LogWarning($"[desktop] input {op} refused: {text}");
        };
        world.Accepted += (OperationId op, ulong _) => _sent.Remove(op);
    }

    public void Reset()
    {
        foreach (var pointer in _pointers.Values) Release(pointer, click: false);
        _pointers.Clear(); _queue.Clear(); _biosWanted.Clear(); _batch.Clear(); _sent.Clear();
        _publishedWindows.Clear(); _hostWindows.Clear(); _bootRestarted.Clear(); _publishedForGuest = 0;
        _local = default; _field = null; _wasInBios = false;
    }

    public void Update()
    {
        if (!_coop.WorldReady || _world is not { Admitted: true } || !_coop.Objects.Initialized) return;
        try { Capture(); } catch (Exception e) { Plugin.Logger.LogError("[desktop] capture failed: " + e); _batch.Clear(); }
        Flush(false);
        try { Replay(); } catch (Exception e) { Plugin.Logger.LogError("[desktop] replay failed: " + e); }
        Unfocus();
        try { PublishWindows(); MatchWindows(); } catch (Exception e) { Plugin.Logger.LogError("[desktop] window match failed: " + e); }
        UpdateBiosWanted();
    }

    // ---------------- screens ----------------

    /// <summary>One drawn PC screen: its root canvas (normalized coordinates are fractions of its rectangle), the
    /// raycasters under it, and the camera that maps those fractions to raycast points.</summary>
    private sealed class Screen
    {
        public Canvas Root;
        public RectTransform Frame;
        public List<GraphicRaycaster> Casters = new();
        /// <summary>The game covers a screen nobody is using with this (VirtualComputer.SetInteraction); a replay looks through it.</summary>
        public Transform Blocker;
    }

    internal static DesktopSurface? SurfaceOf(VirtualComputer vc)
    {
        try
        {
            if (vc == null) return null;
            // IsPoweredOn() reads false while the BIOS is open (game run mirror-2); the BIOS check comes first.
            if (vc.IsInBios()) return DesktopSurface.Bios;
            if (!vc.m_on) return null;
            var os = vc.m_os;
            if (vc.InOS() && os != null && os.m_desktop != null && os.m_desktop.canvas != null && os.GetRaycaster() != null)
                return DesktopSurface.Desktop;
            return DesktopSurface.Boot;
        }
        catch { return null; } // Uninitialized scene computers throw from these getters (6b probe).
    }

    private static Screen ScreenOf(VirtualComputer vc, DesktopSurface surface)
    {
        Canvas root = null;
        try
        {
            if (surface == DesktopSurface.Desktop) root = vc.m_os?.m_desktop?.canvas?.rootCanvas;
            else if (surface == DesktopSurface.Bios) root = vc.GetBios()?.GetComponentInParent<Canvas>()?.rootCanvas;
            root ??= vc.m_canvas?.rootCanvas;
        }
        catch { return null; }
        var frame = root?.GetComponent<RectTransform>();
        if (frame == null || frame.rect.width <= 0 || frame.rect.height <= 0) return null;
        var screen = new Screen { Root = root, Frame = frame, Blocker = vc.m_screenBlocker?.transform };
        foreach (var caster in root.GetComponentsInChildren<GraphicRaycaster>(false)) screen.Casters.Add(caster);
        var osCaster = surface == DesktopSurface.Desktop ? vc.m_os?.GetRaycaster() : null;
        if (osCaster != null && screen.Casters.All(c => c.Pointer != osCaster.Pointer)) screen.Casters.Add(osCaster);
        return screen.Casters.Count == 0 ? null : screen;
    }

    private static VirtualComputer ComputerOf(CoopController coop, NetObjectId pc)
    {
        var vc = coop.Objects.FindBench(pc)?.m_workStation?.m_virtualComputer;
        var record = vc?.ComputerSave;
        return record != null && coop.Objects.IdOf(record) == pc ? vc : null;
    }

    /// <summary>The camera this game uses to hit its own screen while the player looks at it.</summary>
    private static Camera LocalCamera(Screen screen) =>
        screen.Root.renderMode == RenderMode.ScreenSpaceOverlay ? null : screen.Root.worldCamera ?? Camera.main;

    private static GameObject _eyeObject;
    private static Camera _eye;

    /// <summary>A world-space screen is hit through the camera of whoever looks at it, which on the other game is
    /// somewhere else in the workshop. Replays use a fixed, disabled camera placed square in front of the screen;
    /// window drags then measure in its pixels (WindowFrame.CalcScreenToCanvasScale reads the press camera).</summary>
    private static Camera ReplayCamera(Screen screen)
    {
        if (screen.Root.renderMode == RenderMode.ScreenSpaceCamera) return screen.Root.worldCamera;
        if (screen.Root.renderMode != RenderMode.WorldSpace) return null;
        if (_eye == null)
        {
            _eyeObject = new GameObject("PCBS2Coop desktop replay camera");
            Object.DontDestroyOnLoad(_eyeObject);
            _eye = _eyeObject.AddComponent<Camera>();
            _eye.enabled = false;
            _eye.fieldOfView = 30f;
            _eye.nearClipPlane = .01f;
            _eye.farClipPlane = 50f;
        }
        var corners = new Il2CppStructArray<Vector3>(4);
        screen.Frame.GetWorldCorners(corners);
        var center = (corners[0] + corners[2]) * .5f;
        var height = (corners[1] - corners[0]).magnitude;
        var width = (corners[3] - corners[0]).magnitude;
        var aspect = Mathf.Max(.1f, UnityEngine.Screen.width / (float)Mathf.Max(1, UnityEngine.Screen.height));
        var half = Mathf.Max(height * .5f, width * .5f / aspect) * 1.1f;
        var distance = half / Mathf.Tan(15f * Mathf.Deg2Rad);
        var forward = screen.Frame.forward;
        _eye.transform.SetPositionAndRotation(center - forward * distance, Quaternion.LookRotation(forward, screen.Frame.up));
        _eye.farClipPlane = distance * 4f + 1f;
        return _eye;
    }

    private static Vector3 WorldPoint(Screen screen, float x, float y)
    {
        var r = screen.Frame.rect;
        return screen.Frame.TransformPoint(new Vector3(r.xMin + x * r.width, r.yMin + y * r.height, 0));
    }

    private static bool Normalize(Screen screen, Vector2 local, out float x, out float y)
    {
        var r = screen.Frame.rect;
        x = (local.x - r.xMin) / r.width; y = (local.y - r.yMin) / r.height;
        return float.IsFinite(x) && float.IsFinite(y) && x is >= -.02f and <= 1.02f && y is >= -.02f and <= 1.02f;
    }

    /// <summary>Raycasts every raycaster of a screen at one point and returns the topmost hit, as the event system
    /// orders them (canvas sorting order, then depth, then distance).</summary>
    private static RaycastResult Hit(Screen screen, PointerEventData data, out bool found)
    {
        var all = new List<RaycastResult>();
        var list = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
        foreach (var caster in screen.Casters)
        {
            if (caster == null || !caster.gameObject.activeInHierarchy) continue;
            list.Clear();
            try { caster.Raycast(data, list); } catch { continue; }
            for (var i = 0; i < list.Count; i++)
            {
                var go = list[i].gameObject;
                if (go != null && screen.Blocker != null && go.transform.IsChildOf(screen.Blocker)) continue;
                all.Add(list[i]);
            }
        }
        found = all.Count > 0;
        if (!found) return default;
        return all.OrderByDescending(h => h.sortingLayer).ThenByDescending(h => h.sortingOrder)
            .ThenByDescending(h => h.depth).ThenBy(h => h.distance).First();
    }

    // ---------------- this player's input ----------------

    private struct LocalState
    {
        public NetObjectId Pc;
        public DesktopSurface Surface;
        public float X, Y;
        public bool Has;
        public byte Buttons; // bit per MonitorButton
        public byte Quiet;   // held when the screen was first used; their release isn't sent either
        public float Since;  // when this player started using this PC's monitor
    }

    private LocalState _local;
    private readonly List<DesktopEvent> _batch = new();
    private NetObjectId _batchPc;
    private float _lastFlush;
    private readonly HashSet<OperationId> _sent = new();
    private GameObject _field;
    private string _fieldText = "";
    private bool _wasInBios;
    private NetObjectId _wasInBiosPc;

    /// <summary>The PC whose monitor this player is looking at, if it is a bound shared bench PC.</summary>
    internal static (NetObjectId Pc, VirtualComputer Vc, LookingAtMonitorState State) LocalMonitor(CoopController coop)
    {
        var state = GameController.Get()?.GetCurrentState()?.TryCast<LookingAtMonitorState>();
        if (state == null || state.m_case == null || state.m_ws == null) return default;
        var pc = coop.Objects.IdOf(state.m_case);
        if (pc.IsNone) return default;
        var vc = state.m_ws.m_virtualComputer;
        if (vc == null || coop.Objects.FindBench(pc)?.m_workStation?.m_virtualComputer?.Pointer != vc.Pointer) return default;
        return (pc, vc, state);
    }

    /// <summary>This player's pointer on a PC screen as fractions of that screen, or false when it is off the screen.</summary>
    internal static bool LocalPoint(VirtualComputer vc, DesktopSurface surface, LookingAtMonitorState state, out float x, out float y)
    {
        x = y = 0;
        var screen = ScreenOf(vc, surface);
        if (screen == null) return false;
        // A curved monitor draws the screen into a texture on a mesh; its texture coordinates are the fractions.
        var curved = surface == DesktopSurface.Desktop ? vc.m_os?.m_monitorDisplay : null;
        if (curved != null && curved.enabled && curved.gameObject.activeInHierarchy && state?.Camera != null)
        {
            if (!curved.Raycast(state.Camera.ScreenPointToRay(Input.mousePosition), out var hit, 100f)) return false;
            x = hit.textureCoord.x; y = hit.textureCoord.y;
            return x is >= 0 and <= 1 && y is >= 0 and <= 1;
        }
        var cam = LocalCamera(screen);
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(screen.Frame, InputModule.GetLocalCursorPos(), cam, out var local))
            return false;
        return Normalize(screen, local, out x, out y);
    }

    private static bool Button(int index)
    {
        try { return Input.GetMouseButton(index); }
        catch { }
        try { return Rewired.ReInput.isReady && Rewired.ReInput.controllers.Mouse.GetButton(index); }
        catch { return false; }
    }

    private static Vector2 ScrollDelta()
    {
        try { return Input.mouseScrollDelta; }
        catch { return Vector2.zero; }
    }

    private static bool ChatTyping => CoopRunner.Instance?.Chat?.Typing == true;

    private void Capture()
    {
        var (pc, vc, state) = LocalMonitor(_coop);
        var surface = vc == null ? null : SurfaceOf(vc);
        if (!_local.Pc.IsNone && (_local.Pc != pc || surface == null || ChatTyping))
            LeaveLocal();
        if (pc.IsNone || surface == null || ChatTyping) { _wasInBios = false; return; }

        // Entering BIOS on the boot screen (hold the BIOS key) is a branch of the game's boot coroutine.
        var inBios = surface == DesktopSurface.Bios;
        if (inBios && (!_wasInBios || _wasInBiosPc != pc) && !_biosWanted.ContainsKey(pc))
            Add(pc, new DesktopEvent(DesktopEventKind.EnterBios, DesktopSurface.Boot, Time.unscaledTime, 0, 0));
        _wasInBios = inBios; _wasInBiosPc = pc;

        var now = Time.unscaledTime;
        CaptureText(pc, vc, surface.Value, now); // typing doesn't need the pointer on the screen
        if (!LocalPoint(vc, surface.Value, state, out var x, out var y))
        {
            if (_local.Buttons == 0) { if (_local.Has) _local.Has = false; return; }
            x = _local.X; y = _local.Y; // keep a held drag at its last point
        }
        x = Mathf.Clamp01(x); y = Mathf.Clamp01(y);
        if (_local.Pc != pc || _local.Surface != surface.Value)
        {
            if (!_local.Pc.IsNone) LeaveLocal();
            // A button already down when the screen is first used (the click that opened the monitor) is not
            // sent: only its release would arrive, and a release without a press does nothing.
            byte heldNow = 0;
            if (_local.Pc != pc)
                for (var b = MonitorButton.Left; b <= MonitorButton.Middle; b++)
                    if (Button((int)b - 1)) heldNow |= (byte)(1 << (int)b);
            _local = new LocalState { Pc = pc, Surface = surface.Value, X = x, Y = y, Buttons = heldNow, Quiet = heldNow,
                Since = _local.Pc == pc ? _local.Since : now };
        }
        if (!_local.Has || Mathf.Abs(x - _local.X) > .0005f || Mathf.Abs(y - _local.Y) > .0005f)
        {
            _local.X = x; _local.Y = y; _local.Has = true;
            Add(pc, new DesktopEvent(DesktopEventKind.Move, surface.Value, now, x, y));
        }
        for (var b = MonitorButton.Left; b <= MonitorButton.Middle; b++)
        {
            var bit = (byte)(1 << (int)b);
            var held = Button((int)b - 1);
            var was = (_local.Buttons & bit) != 0;
            if (held == was) continue;
            _local.Buttons = held ? (byte)(_local.Buttons | bit) : (byte)(_local.Buttons & ~bit);
            if (!held && (_local.Quiet & bit) != 0) { _local.Quiet = (byte)(_local.Quiet & ~bit); continue; }
            // The click that opens the monitor can land a few frames into the monitor view (game run mirror-10).
            if (held && now - _local.Since < EnterGrace) { _local.Quiet |= bit; continue; }
            Add(pc, new DesktopEvent(held ? DesktopEventKind.Down : DesktopEventKind.Up, surface.Value, now, x, y, b));
            if (held) Plugin.Logger.LogInfo($"[desktop] PC {pc}: {b} press at ({x:0.000}, {y:0.000}) on {surface.Value}; sending.");
        }
        var scroll = ScrollDelta();
        if (scroll != Vector2.zero && float.IsFinite(scroll.x) && float.IsFinite(scroll.y))
            Add(pc, new DesktopEvent(DesktopEventKind.Scroll, surface.Value, now, x, y,
                ScrollX: Mathf.Clamp(scroll.x, -100, 100), ScrollY: Mathf.Clamp(scroll.y, -100, 100)));
    }

    private void CaptureText(NetObjectId pc, VirtualComputer vc, DesktopSurface surface, float now)
    {
        var selected = EventSystem.current?.currentSelectedGameObject;
        var screen = selected == null ? null : ScreenOf(vc, surface);
        var text = selected == null ? null : FieldText(selected);
        if (screen == null || text == null || !selected.transform.IsChildOf(screen.Root.transform)) { _field = null; return; }
        if (_field == null || _field.Pointer != selected.Pointer) { _field = selected; _fieldText = text; return; }
        var path = PathOf(screen.Root.transform, selected.transform);
        if (path == null) return;
        if (text != _fieldText && text.Length <= DesktopEvent.MaxText)
        {
            _fieldText = text;
            Add(pc, new DesktopEvent(DesktopEventKind.Text, surface, now, _local.X, _local.Y, Path: path, Text: text));
        }
        if (Ui.KeyDown(KeyCode.Return) || Ui.KeyDown(KeyCode.KeypadEnter))
            Add(pc, new DesktopEvent(DesktopEventKind.Submit, surface, now, _local.X, _local.Y, Path: path));
    }

    private void LeaveLocal()
    {
        var pc = _local.Pc;
        for (var b = MonitorButton.Left; b <= MonitorButton.Middle; b++)
            if ((_local.Buttons & ~_local.Quiet & (1 << (int)b)) != 0)
                Add(pc, new DesktopEvent(DesktopEventKind.Up, _local.Surface, Time.unscaledTime, _local.X, _local.Y, b));
        Add(pc, new DesktopEvent(DesktopEventKind.Leave, _local.Surface, Time.unscaledTime, 0, 0));
        _local = default; _field = null;
        Flush(true);
    }

    private void Add(NetObjectId pc, DesktopEvent e)
    {
        if (!_batchPc.IsNone && _batchPc != pc) Flush(true);
        _batchPc = pc;
        // Consecutive moves only need the latest point.
        if (e.Kind == DesktopEventKind.Move && _batch.Count > 0 && _batch[^1].Kind == DesktopEventKind.Move)
            _batch[^1] = e;
        else _batch.Add(e);
        if (_batch.Count >= DesktopInput.MaxEvents || e.Kind is DesktopEventKind.Text) Flush(true);
    }

    private void Flush(bool force)
    {
        if (_batch.Count == 0) return;
        var now = Time.unscaledTime;
        var urgent = _batch.Any(e => e.Kind != DesktopEventKind.Move);
        if (!force && !urgent && now - _lastFlush < SendInterval) return;
        _lastFlush = now;
        var actor = _coop.Activity.Get(_world.Local);
        var events = _batch.ToArray(); var pc = _batchPc;
        _batch.Clear(); _batchPc = default;
        if (actor == null || _world.Divergence != null)
        {
            if (urgent) Plugin.Logger.LogWarning($"[desktop] PC {pc}: input not sent ({(actor == null ? "no activity yet" : "out of sync")}).");
            return;
        }
        try
        {
            if (!_world.Submit(CommandTypes.DesktopInput, new DesktopInput(actor.Epoch, pc, events).ToBytes(), out var op))
                Plugin.Logger.LogWarning($"[desktop] PC {pc}: input not sent (world not linked).");
            else if (!_world.IsAuthority) _sent.Add(op);
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[desktop] input not sent: " + e.Message); }
    }

    // ---------------- the other player's input ----------------

    private sealed class Pointer
    {
        public ActorSlot Actor;
        public NetObjectId Pc;
        public DesktopSurface Surface;
        public PointerEventData Data;
        public GameObject Hover;
        public MonitorButton Held;
        public float LastClickAt = -10;
        public long LastClickTarget;
        public int ClickCount;
        public Screen Screen;
    }

    private readonly Dictionary<(ActorSlot, NetObjectId), Pointer> _pointers = new();
    private readonly Queue<(ActorSlot Actor, NetObjectId Pc, DesktopEvent Event, float Received)> _queue = new();

    /// <summary>True while the other player's input for this PC is waiting or a replayed button is held.</summary>
    internal bool Busy(NetObjectId pc) =>
        _queue.Any(q => q.Pc == pc) || _pointers.Values.Any(p => p.Pc == pc && p.Held != MonitorButton.None);

    public void Changed(in MutationContext ctx, PcFact pc, DesktopInput input)
    {
        if (ctx.Op.Actor == _world.Local) return; // already ran on this game when it happened
        var now = Time.unscaledTime;
        foreach (var e in input.Events) _queue.Enqueue((ctx.Op.Actor, input.Pc, e, now));
        if (_queue.Count > 4000) { Plugin.Logger.LogWarning("[desktop] replay queue overflow; dropping it."); _queue.Clear(); }
    }

    private void Replay()
    {
        var now = Time.unscaledTime;
        var guard = 0;
        while (_queue.Count > 0 && guard++ < 512)
        {
            var (actor, pc, e, received) = _queue.Peek();
            if (e.Kind == DesktopEventKind.EnterBios) { _queue.Dequeue(); WantBios(pc); continue; }
            if (e.Kind == DesktopEventKind.Windows) { _queue.Dequeue(); HostWindows(pc, e.Text); continue; }
            var vc = ComputerOf(_coop, pc);
            var surface = vc == null ? null : SurfaceOf(vc);
            if (e.Releases || (surface == e.Surface && vc != null))
            {
                _queue.Dequeue();
                Inject(actor, pc, vc, surface, e);
                continue;
            }
            if (now - received < SurfaceWait) return; // this game's PC is still a moment behind (boot, BIOS)
            _queue.Dequeue();
            Plugin.Logger.LogWarning($"[desktop] PC {pc}: dropped the other player's {e.Kind} on {e.Surface}; this game shows {surface?.ToString() ?? "no screen"}.");
        }
    }

    private void Inject(ActorSlot actor, NetObjectId pc, VirtualComputer vc, DesktopSurface? surface, DesktopEvent e)
    {
        var key = (actor, pc);
        _pointers.TryGetValue(key, out var p);
        if (e.Kind == DesktopEventKind.Leave)
        {
            if (p != null) { Release(p, click: false); _pointers.Remove(key); }
            return;
        }
        if (vc == null || surface != e.Surface)
        {
            if (p != null && e.Kind == DesktopEventKind.Up) Release(p, click: false);
            return;
        }
        var events = EventSystem.current;
        if (events == null) return;
        if (p == null || p.Surface != e.Surface || p.Data == null)
        {
            if (p != null) Release(p, click: false);
            p = new Pointer { Actor = actor, Pc = pc, Surface = e.Surface, Data = new PointerEventData(events) { pointerId = 7700 + (int)actor } };
            _pointers[key] = p;
        }
        p.Screen = ScreenOf(vc, e.Surface);
        if (p.Screen == null) return;
        var cam = ReplayCamera(p.Screen);
        if (cam == null) return;
        if (e.Kind is DesktopEventKind.Text or DesktopEventKind.Submit) { Run(p, cam, () => Type(p, e)); return; }
        Run(p, cam, () =>
        {
            var screenPos = (Vector2)cam.WorldToScreenPoint(WorldPoint(p.Screen, e.X, e.Y));
            var data = p.Data;
            data.delta = screenPos - data.position;
            data.position = screenPos;
            var hit = Hit(p.Screen, data, out var found);
            data.pointerCurrentRaycast = hit;
            var over = found ? hit.gameObject : null;
            Hover(p, over);
            switch (e.Kind)
            {
                case DesktopEventKind.Move: Drag(p); break;
                case DesktopEventKind.Down:
                    Plugin.Logger.LogInfo($"[desktop] PC {p.Pc}: the other player's {e.Button} press at ({e.X:0.000}, {e.Y:0.000}) on {e.Surface} hits {(over == null ? "nothing" : PathOf(p.Screen.Root.transform, over.transform) + " " + over.name)} (screen {screenPos}).");
                    Press(p, over, e);
                    break;
                case DesktopEventKind.Up: if (p.Held == e.Button) Release(p, click: true, over); break;
                case DesktopEventKind.Scroll:
                    data.scrollDelta = new Vector2(e.ScrollX, e.ScrollY);
                    var target = over == null ? null : ExecuteEvents.GetEventHandler<IScrollHandler>(over);
                    if (target != null) ExecuteEvents.ExecuteHierarchy(target, data, ExecuteEvents.scrollHandler);
                    data.scrollDelta = Vector2.zero;
                    break;
            }
        });
    }

    /// <summary>Runs replayed UI code with the replay camera as the screen's event camera, then restores the camera
    /// and this player's own selection.</summary>
    private static void Run(Pointer p, Camera cam, Action action)
    {
        var events = EventSystem.current;
        var selected = events?.currentSelectedGameObject;
        var canvases = new List<(Canvas Canvas, Camera Camera)>();
        if (p.Screen.Root.renderMode == RenderMode.WorldSpace)
        {
            canvases.Add((p.Screen.Root, p.Screen.Root.worldCamera));
            foreach (var caster in p.Screen.Casters)
            {
                var canvas = caster?.GetComponent<Canvas>();
                if (canvas != null && canvases.All(c => c.Canvas.Pointer != canvas.Pointer)) canvases.Add((canvas, canvas.worldCamera));
            }
            foreach (var (canvas, _) in canvases) canvas.worldCamera = cam;
        }
        Injecting = true;
        try { NativeProjection.Run(action); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[desktop] PC {p.Pc}: replayed input failed: {e}"); }
        finally
        {
            Injecting = false;
            foreach (var (canvas, camera) in canvases) if (canvas != null) canvas.worldCamera = camera;
            try
            {
                if (events != null && events.currentSelectedGameObject?.Pointer != selected?.Pointer)
                    events.SetSelectedGameObject(selected);
            }
            catch { }
        }
    }

    /// <summary>Null for a destroyed Unity object (a restart or closed window replaces the screen's objects).</summary>
    private static GameObject Live(GameObject go) => go == null ? null : go;

    private static void Hover(Pointer p, GameObject over)
    {
        p.Hover = Live(p.Hover);
        if (p.Hover?.Pointer == over?.Pointer) return;
        var data = p.Data;
        var common = Common(p.Hover, over);
        for (var t = p.Hover?.transform; t != null && t != common; t = t.parent)
            ExecuteEvents.Execute(t.gameObject, data, ExecuteEvents.pointerExitHandler);
        data.pointerEnter = over;
        for (var t = over?.transform; t != null && t != common; t = t.parent)
            ExecuteEvents.Execute(t.gameObject, data, ExecuteEvents.pointerEnterHandler);
        p.Hover = over;
    }

    private static Transform Common(GameObject a, GameObject b)
    {
        if (a == null || b == null) return null;
        for (var t = a.transform; t != null; t = t.parent)
            if (b.transform.IsChildOf(t)) return t;
        return null;
    }

    /// <summary>Text fields a replayed click focused here. The other player types through Text events, so this
    /// player's keys must not go into them; they are switched off again for a few frames (TMP activates late).</summary>
    private static readonly List<(Selectable Field, int Until)> _replayFocused = new();

    private static void Unfocus()
    {
        for (var i = _replayFocused.Count - 1; i >= 0; i--)
        {
            var (field, until) = _replayFocused[i];
            if (field == null || Time.frameCount > until) { _replayFocused.RemoveAt(i); continue; }
            try
            {
                var go = field.gameObject;
                if (EventSystem.current?.currentSelectedGameObject?.Pointer == go.Pointer) continue; // this player's own
                var tmp = go.GetComponent<TMP_InputField>();
                if (tmp != null && tmp.isFocused) tmp.DeactivateInputField(false);
                var legacy = go.GetComponent<InputField>();
                if (legacy != null && legacy.isFocused) legacy.DeactivateInputField();
            }
            catch { _replayFocused.RemoveAt(i); }
        }
    }

    private static void Press(Pointer p, GameObject over, DesktopEvent e)
    {
        if (p.Held != MonitorButton.None) Release(p, click: false);
        var data = p.Data;
        data.button = e.Button switch
        {
            MonitorButton.Right => PointerEventData.InputButton.Right,
            MonitorButton.Middle => PointerEventData.InputButton.Middle,
            _ => PointerEventData.InputButton.Left,
        };
        p.Held = e.Button;
        data.eligibleForClick = true;
        data.delta = Vector2.zero;
        data.dragging = false;
        data.useDragThreshold = true;
        data.pressPosition = data.position;
        data.pointerPressRaycast = data.pointerCurrentRaycast;
        if (over == null) return;
        var field = (Selectable)over.GetComponentInParent<TMP_InputField>() ?? over.GetComponentInParent<InputField>();
        if (field != null) _replayFocused.Add((field, Time.frameCount + 5));
        var pressed = ExecuteEvents.ExecuteHierarchy(over, data, ExecuteEvents.pointerDownHandler);
        var click = ExecuteEvents.GetEventHandler<IPointerClickHandler>(over);
        pressed ??= click;
        // Double clicks count with the sender's clock, so network timing can't merge or split them.
        var target = pressed == null ? 0 : pressed.Pointer.ToInt64();
        p.ClickCount = target == p.LastClickTarget && e.Time - p.LastClickAt < .3f ? p.ClickCount + 1 : 1;
        p.LastClickAt = e.Time; p.LastClickTarget = target;
        data.clickCount = p.ClickCount;
        data.clickTime = Time.unscaledTime;
        data.pointerPress = pressed;
        data.rawPointerPress = over;
        data.pointerClick = click;
        data.pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(over);
        if (data.pointerDrag != null) ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.initializePotentialDrag);
    }

    private static void Drag(Pointer p)
    {
        var data = p.Data;
        data.pointerDrag = Live(data.pointerDrag);
        data.pointerPress = Live(data.pointerPress);
        if (p.Held == MonitorButton.None || data.pointerDrag == null) return;
        var threshold = EventSystem.current?.pixelDragThreshold ?? 10;
        if (!data.dragging && (!data.useDragThreshold || (data.pressPosition - data.position).sqrMagnitude >= threshold * threshold))
        {
            ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.beginDragHandler);
            data.dragging = true;
        }
        if (!data.dragging) return;
        if (data.pointerPress != null && data.pointerPress.Pointer != data.pointerDrag.Pointer)
        {
            ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
            data.eligibleForClick = false;
            data.pointerPress = null;
            data.rawPointerPress = null;
        }
        ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.dragHandler);
    }

    private static void Release(Pointer p, bool click, GameObject over = null)
    {
        var data = p.Data;
        if (data == null) return;
        try
        {
            data.pointerPress = Live(data.pointerPress);
            data.pointerDrag = Live(data.pointerDrag);
            data.pointerClick = Live(data.pointerClick);
            over = Live(over);
            if (p.Held != MonitorButton.None)
            {
                if (data.pointerPress != null) ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
                var clickHandler = over == null ? null : ExecuteEvents.GetEventHandler<IPointerClickHandler>(over);
                if (click && data.eligibleForClick && clickHandler != null && data.pointerClick?.Pointer == clickHandler.Pointer)
                    ExecuteEvents.Execute(data.pointerClick, data, ExecuteEvents.pointerClickHandler);
                if (click && data.pointerDrag != null && data.dragging && over != null)
                    ExecuteEvents.ExecuteHierarchy(over, data, ExecuteEvents.dropHandler);
                if (data.pointerDrag != null && data.dragging)
                    ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.endDragHandler);
            }
            if (!click) Hover(p, null);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[desktop] PC {p.Pc}: replayed release failed: {e.Message}"); }
        finally
        {
            data.eligibleForClick = false;
            data.pointerPress = null;
            data.rawPointerPress = null;
            data.pointerClick = null;
            data.dragging = false;
            data.pointerDrag = null;
            p.Held = MonitorButton.None;
        }
    }

    // ---------------- typing ----------------

    private static string FieldText(GameObject go)
    {
        var tmp = go.GetComponent<TMP_InputField>();
        if (tmp != null) return tmp.text ?? "";
        var legacy = go.GetComponent<InputField>();
        return legacy != null ? legacy.text ?? "" : null;
    }

    /// <summary>Sibling indices from the screen's root canvas down to a field. Both games build the same screen from
    /// the same clicks, so the indices name the same field.</summary>
    private static string PathOf(Transform root, Transform target)
    {
        var parts = new List<string>();
        for (var t = target; t != null && t != root; t = t.parent) parts.Add(t.GetSiblingIndex().ToString());
        if (parts.Count == 0) return null;
        parts.Reverse();
        var path = string.Join("/", parts);
        return path.Length <= DesktopEvent.MaxPath ? path : null;
    }

    private static Transform Resolve(Transform root, string path)
    {
        var t = root;
        foreach (var part in path.Split('/'))
        {
            if (!int.TryParse(part, out var index) || index < 0 || index >= t.childCount) return null;
            t = t.GetChild(index);
        }
        return t;
    }

    private void Type(Pointer p, DesktopEvent e)
    {
        var target = Resolve(p.Screen.Root.transform, e.Path)?.gameObject;
        var tmp = target?.GetComponent<TMP_InputField>();
        var legacy = tmp == null ? target?.GetComponent<InputField>() : null;
        if (tmp == null && legacy == null)
        {
            Plugin.Logger.LogWarning($"[desktop] PC {p.Pc}: the other player's text field {e.Path} isn't on this screen.");
            return;
        }
        if (e.Kind == DesktopEventKind.Text)
        {
            if (tmp != null) tmp.text = e.Text; else legacy.text = e.Text;
            if (_field != null && _field.Pointer == target.Pointer) _fieldText = e.Text; // not an edit to send back
            return;
        }
        if (tmp != null) { tmp.onSubmit?.Invoke(tmp.text); tmp.onEndEdit?.Invoke(tmp.text); }
        else { legacy.onSubmit?.Invoke(legacy.text); legacy.onEndEdit?.Invoke(legacy.text); }
    }
}

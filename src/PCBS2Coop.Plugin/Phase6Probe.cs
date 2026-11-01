using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using PCBS2Coop.Network;
using UnityEngine;
using UnityEngine.EventSystems;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>Opt-in phase 6b observation. The self test isolates saves before any event is recorded.
/// Hooks observe BIOS commits; boot stages are sampled without changing native calls.</summary>
internal sealed class Phase6Probe
{
    public static bool Enabled => AutoTest.IsEnabled && Environment.GetEnvironmentVariable("PCBS2COOP_PROBE") == "6";
    private static readonly Lazy<string> Id = new(() =>
    {
        var value = Environment.GetEnvironmentVariable("PCBS2COOP_PROBE_RUN") ?? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        if (value.Length is < 1 or > 48 || value.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-')))
            throw new ArgumentException("PCBS2COOP_PROBE_RUN must be 1-48 ASCII letters/digits/hyphens.");
        return value;
    });
    public static string RunId => Id.Value;

    private readonly string _root;
    private readonly string _file;
    private readonly Dictionary<long, string> _last = new();
    private readonly Dictionary<long, string> _lastSoftware = new();
    private long _lastMonitor;
    private bool _monitorDesktopObserved;
    private string _lastDesktopHit = "";
    private string _lastDesktopWindows = "";
    private float _nextSample;
    private bool _wasReady;
    private static Phase6Probe _current;

    public Phase6Probe()
    {
        var role = Environment.GetEnvironmentVariable("PCBS2COOP_AUTOTEST")?.Trim().ToLowerInvariant();
        _root = Path.GetFullPath(Path.Combine(Diagnostics.Dir, "phase6", RunId));
        _file = Path.Combine(_root, role ?? "unknown", "events.jsonl");
        _current = this;
        InstallHooks();
    }

    private bool Safe
    {
        get
        {
            var dir = AutoTest.IsolatedSaveDir;
            if (dir == null) return false;
            var isolated = Path.GetFullPath(dir);
            var save = Path.GetFullPath(SaveLoadSystem.s_saveDir ?? ".");
            return isolated.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                   (save.Equals(isolated, StringComparison.OrdinalIgnoreCase) ||
                    save.StartsWith(isolated + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void Event(string kind, object detail)
    {
        if (!Safe) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.AppendAllText(_file, JsonSerializer.Serialize(new
            {
                at = DateTime.UtcNow.ToString("O"),
                seconds = Time.realtimeSinceStartup,
                kind,
                detail
            }) + "\n");
        }
        catch (Exception e) { Plugin.Logger.LogWarning("PHASE6 probe log failed: " + e.Message); }
    }

    private static NetObjectId BoundPc(VirtualComputer vc)
    {
        var coop = CoopRunner.Instance?.Coop;
        if (vc == null || coop?.Objects.Initialized != true) return default;
        foreach (var pc in coop.Objects.Model.Pcs)
            if (coop.Objects.FindBench(pc.Id)?.m_workStation?.m_virtualComputer?.Pointer == vc.Pointer)
                return pc.Id;
        return default;
    }

    private static object Describe(ComputerSave save, VirtualComputer vc)
    {
        if (save == null) return new { present = false, pcByBench = BoundPc(vc).Value };
        var coop = CoopRunner.Instance?.Coop;
        var config = save.m_biosConfig;
        return new
        {
            present = true,
            pointer = save.Pointer.ToInt64(),
            pcBySave = (coop?.Objects.IdOf(save) ?? default).Value,
            pcByBench = BoundPc(vc).Value,
            caseSku = save.caseID?.GetPartId(),
            bios = Config(config),
            software = save.m_software?.Pointer.ToInt64() ?? 0
        };
    }

    private static object Config(BiosConfig c)
    {
        if (c == null) return new { present = false };
        string bootSku = null;
        long bootPointer = 0;
        try { var drive = c.GetBootDrive(); bootSku = drive?.GetPartId(); bootPointer = drive?.Pointer.ToInt64() ?? 0; }
        catch (Exception e) { bootSku = "error: " + e.GetType().Name; }
        return new
        {
            present = true,
            pointer = c.Pointer.ToInt64(),
            c.m_baseClock, c.m_cpuMultiplier, c.m_cpuVoltage,
            c.m_RAMClock, c.m_RAMVoltage, timings = c.m_RAMTimings?.ToArray(), c.m_xmp,
            c.m_gpuClock, c.m_gpuMemClock, c.m_gpuVoltagePercent,
            c.m_gpuClockLast, c.m_gpuMemClockLast, c.m_gpuVoltLast,
            bootSku, bootPointer, c.m_OSInstallFlag
        };
    }

    private static VirtualComputer ComputerFor(Bios bios)
    {
        var coop = CoopRunner.Instance?.Coop;
        if (coop?.Objects.Initialized == true)
            foreach (var pc in coop.Objects.Model.Pcs)
            {
                var bound = coop.Objects.FindBench(pc.Id)?.m_workStation?.m_virtualComputer;
                try { if (bound != null && bound.GetBios()?.Pointer == bios.Pointer) return bound; }
                catch { } // An off PC need not have initialized its BIOS prefab.
            }
        foreach (var vc in Object.FindObjectsOfType<VirtualComputer>())
            try { if (vc != null && vc.GetBios()?.Pointer == bios.Pointer) return vc; }
            catch { }
        return null;
    }

    private static void BiosEvent(string kind, Bios bios)
    {
        var p = _current;
        if (p?.Safe != true || bios == null) return;
        try
        {
            var vc = ComputerFor(bios);
            p.Event(kind, new
            {
                bios = bios.Pointer.ToInt64(),
                draft = Config(bios.BiosConfig),
                virtualComputer = vc?.Pointer.ToInt64() ?? 0,
                record = Describe(vc?.ComputerSave, vc)
            });
        }
        catch (Exception e) { p.Event(kind + "-error", e.ToString()); }
    }

    private static void BeforeApply(Bios __instance) => BiosEvent("bios-apply-before", __instance);
    private static void AfterApply(Bios __instance) => BiosEvent("bios-apply-after", __instance);
    private static void BeforeReset(Bios __instance) => BiosEvent("bios-reset-before", __instance);
    private static void AfterReset(Bios __instance) => BiosEvent("bios-reset-after", __instance);

    private void InstallHooks()
    {
        var harmony = new Harmony(Plugin.Guid + ".phase6-probe");
        void Hook(Type type, string target, string before, string after)
        {
            var method = AccessTools.Method(type, target);
            if (method == null) { Plugin.Logger.LogWarning($"PHASE6 probe: {type.Name}.{target} was not found."); return; }
            var folded = GamePatches.FoldedWith(method);
            if (folded != null) { Plugin.Logger.LogWarning($"PHASE6 probe: {type.Name}.{target} shares native code with {folded.DeclaringType?.Name}.{folded.Name}; skipped."); return; }
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(Phase6Probe).GetMethod(before, flags)),
                postfix: new HarmonyMethod(typeof(Phase6Probe).GetMethod(after, flags)));
        }
        try
        {
            Hook(typeof(Bios), "ApplyRestart", nameof(BeforeApply), nameof(AfterApply));
            Hook(typeof(Bios), "ResetSettings", nameof(BeforeReset), nameof(AfterReset));
        }
        catch (Exception e) { Plugin.Logger.LogError("PHASE6 probe hooks failed: " + e); }
    }

    public void Update()
    {
        if (!Safe || Time.realtimeSinceStartup < _nextSample) return;
        _nextSample = Time.realtimeSinceStartup + 0.25f;
        try
        {
            var ready = CoopRunner.Instance?.Coop.WorldReady == true;
            if (ready && !_wasReady)
            {
                _last.Clear(); // Emit one bound observation once the host assigns PC IDs.
                _lastSoftware.Clear();
                var readyCoop = CoopRunner.Instance.Coop;
                Event("pc-bindings", readyCoop.Objects.Model.Pcs.Select(pc =>
                {
                    var bench = readyCoop.Objects.FindBench(pc.Id);
                    var record = readyCoop.Objects.RecordFor(pc.Id);
                    return new
                    {
                        pc = pc.Id.Value,
                        place = pc.Location.Place.ToString(),
                        bench = bench?.Pointer.ToInt64() ?? 0,
                        workstation = bench?.m_workStation?.Pointer.ToInt64() ?? 0,
                        virtualComputer = bench?.m_workStation?.m_virtualComputer?.Pointer.ToInt64() ?? 0,
                        save = record?.Pointer.ToInt64() ?? 0,
                        casePart = record?.caseID?.Pointer.ToInt64() ?? 0,
                        caseSku = record?.caseID?.GetPartId()
                    };
                }).ToArray());
            }
            _wasReady = ready;
            var coop = CoopRunner.Instance?.Coop;
            var monitor = GameController.Get()?.GetCurrentState()?.TryCast<LookingAtMonitorState>();
            var monitorKey = monitor?.Pointer.ToInt64() ?? 0;
            if (monitorKey != _lastMonitor)
            {
                _lastMonitor = monitorKey;
                _monitorDesktopObserved = false;
                _lastDesktopHit = "";
                _lastDesktopWindows = "";
                if (monitor != null)
                {
                    var ws = monitor.m_ws;
                    var vc = ws?.m_virtualComputer;
                    var os = vc?.m_os;
                    var events = EventSystem.current;
                    Event("monitor-input", new
                    {
                        pcByCase = (coop?.Objects.IdOf(monitor.m_case) ?? default).Value,
                        pcByBench = BoundPc(vc).Value,
                        workstation = ws?.Pointer.ToInt64() ?? 0,
                        virtualComputer = vc?.Pointer.ToInt64() ?? 0,
                        camera = monitor.Camera?.Pointer.ToInt64() ?? 0,
                        os = os?.Pointer.ToInt64() ?? 0,
                        curvedCamera = os?.m_curvedCam?.Pointer.ToInt64() ?? 0,
                        curvedCollider = os?.m_monitorDisplay?.Pointer.ToInt64() ?? 0,
                        raycaster = os?.GetRaycaster()?.Pointer.ToInt64() ?? 0,
                        curvedRaycaster = os?.m_graphicCaster?.Pointer.ToInt64() ?? 0,
                        eventSystem = events?.Pointer.ToInt64() ?? 0,
                        inputModule = events?.currentInputModule?.GetIl2CppType().FullName,
                        selected = events?.currentSelectedGameObject?.name
                    });
                }
            }
            // The state enters before the OS canvas is built. Record its input path again
            // once the desktop raycaster exists, without a GPU screenshot/readback.
            if (monitor != null && !_monitorDesktopObserved)
            {
                var desktopOs = monitor.m_ws?.m_virtualComputer?.m_os;
                var desktopRaycaster = desktopOs?.GetRaycaster();
                var desktopCanvas = desktopOs?.m_desktop?.canvas;
                if (desktopRaycaster != null && desktopCanvas != null &&
                    monitor.m_ws?.m_virtualComputer?.IsPoweredOn() == true &&
                    monitor.m_ws?.m_virtualComputer?.IsInBios() != true)
                {
                    var cursor = coop != null ? NativeMonitorCursor.Observe(coop) : default;
                    if (!cursor.Visible) return; // The OS can still be switching from its loading graphic.
                    _monitorDesktopObserved = true;
                    var display = monitor.m_displayRect;
                    var pos = InputModule.GetLocalCursorPos();
                    var windows = DesktopWindows(desktopOs);
                    Vector2 mappedPoint = default;
                    var mappedHit = cursor.Visible
                        ? NativeDesktopRaycast.TargetAt(desktopOs, cursor.X, cursor.Y, out mappedPoint)
                        : null;
                    var localHits = UnityExt.Raycast(desktopRaycaster, pos);
                    var localHit = localHits != null && localHits.Count > 0 ? localHits[0].gameObject : null;
                    Event("monitor-desktop-ready", new
                    {
                        pc = cursor.Pc.Value,
                        cursor.Visible,
                        cursor.X,
                        cursor.Y,
                        localCursorX = pos.x,
                        localCursorY = pos.y,
                        mappedCursorX = cursor.Visible ? mappedPoint.x : 0,
                        mappedCursorY = cursor.Visible ? mappedPoint.y : 0,
                        localHit = localHit?.Pointer.ToInt64() ?? 0,
                        mappedHit = mappedHit?.Pointer.ToInt64() ?? 0,
                        rectWidth = display?.rect.width ?? 0,
                        rectHeight = display?.rect.height ?? 0,
                        raycaster = desktopRaycaster.Pointer.ToInt64(),
                        canvas = desktopOs?.m_desktop?.canvas?.Pointer.ToInt64() ?? 0,
                        canvasCamera = desktopOs?.m_desktop?.canvas?.worldCamera?.Pointer.ToInt64() ?? 0,
                        windows
                    });
                }
            }
            if (monitor != null && _monitorDesktopObserved)
            {
                var os = monitor.m_ws?.m_virtualComputer?.m_os;
                var caster = os?.GetRaycaster();
                if (caster != null)
                {
                    var hits = UnityExt.Raycast(caster, InputModule.GetLocalCursorPos());
                    var target = hits != null && hits.Count > 0 ? hits[0].gameObject : null;
                    var path = target == null ? "" : DesktopPath(target.transform, os?.m_desktop?.canvas?.transform);
                    if (path != _lastDesktopHit)
                    {
                        _lastDesktopHit = path;
                        Event("monitor-hit", new { pc = BoundPc(monitor.m_ws?.m_virtualComputer).Value,
                            path, target = target?.Pointer.ToInt64() ?? 0, hitCount = hits?.Count ?? 0 });
                    }
                    var windows = DesktopWindows(os);
                    var signature = JsonSerializer.Serialize(windows);
                    if (signature != _lastDesktopWindows)
                    {
                        _lastDesktopWindows = signature;
                        Event("desktop-windows", new { pc = BoundPc(monitor.m_ws?.m_virtualComputer).Value, windows });
                    }
                }
            }
            var current = new HashSet<long>();
            var computers = (coop?.Objects.Initialized == true
                ? coop.Objects.Model.Pcs.Select(pc => coop.Objects.FindBench(pc.Id)?.m_workStation?.m_virtualComputer)
                : Object.FindObjectsOfType<VirtualComputer>().AsEnumerable()).Where(vc => vc != null);
            foreach (var vc in computers)
            {
                if (vc == null) continue;
                var key = vc.Pointer.ToInt64();
                current.Add(key);
                string Check(Func<bool> read)
                {
                    try { return read() ? "1" : "0"; }
                    catch { return "?"; } // A scene can contain an uninitialized VirtualComputer during load.
                }
                var powered = Check(vc.IsPoweredOn);
                var running = Check(vc.IsPcRunning);
                var bios = Check(vc.IsInBios);
                var os = Check(vc.InOS);
                var record = vc.ComputerSave;
                if (os == "1" && record?.m_biosConfig != null && record.m_software is { } software)
                {
                    var state = new
                    {
                        pc = BoundPc(vc).Value,
                        bootDrive = record.m_biosConfig.GetBootDrive()?.GetPartId(),
                        wallpaper = software.m_wallpaper,
                        userWallpaper = software.m_userWallpaper,
                        programs = software.m_programs?.ToArray() ?? Array.Empty<string>(),
                        folders = software.Folders?.Length ?? 0,
                        autoExec = software.AutoExec?.ToArray() ?? Array.Empty<string>(),
                        software.m_uniqueID
                    };
                    var signature = JsonSerializer.Serialize(state);
                    if (!_lastSoftware.TryGetValue(key, out var prior) || prior != signature)
                    {
                        _lastSoftware[key] = signature;
                        try { Event("software-state", new { state, serializedBytes = NativeSoftwareCodec.Write(software).Length }); }
                        catch (Exception e) { Event("software-state", new { state, serializationError = e.Message }); }
                    }
                }
                var stage = $"{powered}/{running}/{bios}/{os}";
                if (_last.TryGetValue(key, out var old) && old == stage) continue;
                _last[key] = stage;
                Event("boot-stage", new { virtualComputer = key, powered, running, bios, os, record = Describe(vc.ComputerSave, vc) });
            }
            foreach (var key in _last.Keys.Where(k => !current.Contains(k)).ToArray()) _last.Remove(key);
            foreach (var key in _lastSoftware.Keys.Where(k => !current.Contains(k)).ToArray()) _lastSoftware.Remove(key);
        }
        catch (Exception e) { Event("sample-error", e.ToString()); }
    }

    private static string DesktopPath(Transform target, Transform root)
    {
        var names = new List<string>();
        for (var current = target; current != null && names.Count < 32; current = current.parent)
        {
            names.Add(current.name);
            if (root != null && current.Pointer == root.Pointer) break;
        }
        names.Reverse();
        return string.Join("/", names);
    }

    private static object[] DesktopWindows(OS os)
    {
        var rows = new List<object>();
        var open = os?.m_openWindows;
        if (open == null) return rows.ToArray();
        for (var i = 0; i < open.Count; i++)
        {
            var frame = open[i];
            if (frame == null) continue;
            var rect = frame.m_scaleRect;
            rows.Add(new
            {
                program = frame.Desc?.m_id,
                frame = frame.Pointer.ToInt64(),
                order = frame.transform.GetSiblingIndex(),
                frame.m_minimised,
                frame.m_maximised,
                x = rect?.anchoredPosition.x ?? 0,
                y = rect?.anchoredPosition.y ?? 0,
                width = rect?.rect.width ?? 0,
                height = rect?.rect.height ?? 0
            });
        }
        return rows.ToArray();
    }
}

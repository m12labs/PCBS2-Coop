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
internal sealed partial class Phase6Probe
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

    private static void TraceStep(System.Reflection.MethodBase __originalMethod)
    { if (NativeEditReplay.Active) Plugin.Logger.LogInfo($"[replay-trace] enter {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}"); }
    private static void TraceDone(System.Reflection.MethodBase __originalMethod)
    { if (NativeEditReplay.Active) Plugin.Logger.LogInfo($"[replay-trace] done {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}"); }

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
            // Which native step of a replayed install fails when this player isn't at that bench (owner, Nov 1).
            if (Environment.GetEnvironmentVariable("PCBS2COOP_TRACE") == "1")
            foreach (var (type, name) in new[]
            {
                (typeof(Slot), nameof(Slot.InstantiatePart)), (typeof(Slot), nameof(Slot.InstantiatePreloadedPart)),
                (typeof(WorkStation), nameof(WorkStation.AddSelectable)), (typeof(Case), nameof(Case.DisableInvalidPSUConnectors)),
                (typeof(Case), nameof(Case.OnChangePart)), (typeof(Motherboard), nameof(Motherboard.OnChangePart)),
                (typeof(Case), nameof(Case.RefreshCustomisations)), (typeof(Slot), nameof(Slot.GetHomePos)),
                (typeof(InstallingPartState), nameof(InstallingPartState.FixupSubSlots)),
                (typeof(InstallingPartState), nameof(InstallingPartState.FinishInit)),
                (typeof(GameController), nameof(GameController.GetCableType)), (typeof(GameController), nameof(GameController.SetCableType)),
                (typeof(CareerStatus), nameof(CareerStatus.RemoveItemFromInventory)),
            })
            {
                var method = AccessTools.GetDeclaredMethods(type).FirstOrDefault(m => m.Name == name);
                if (method == null) { Plugin.Logger.LogWarning($"PHASE6 probe: {type.Name}.{name} not found."); continue; }
                if (GamePatches.FoldedWith(method) is { } folded) { Plugin.Logger.LogWarning($"PHASE6 probe: {type.Name}.{name} folded with {folded.Name}; skipped."); continue; }
                try
                {
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(Phase6Probe).GetMethod(nameof(TraceStep), BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(typeof(Phase6Probe).GetMethod(nameof(TraceDone), BindingFlags.Static | BindingFlags.NonPublic)));
                }
                catch (Exception e) { Plugin.Logger.LogWarning($"PHASE6 probe: {type.Name}.{name} not traced: {e.Message}"); }
            }
        }
        catch (Exception e) { Plugin.Logger.LogError("PHASE6 probe hooks failed: " + e); }
    }

    private int _commandsRead;
    private bool _skipCommandHistory = Environment.GetEnvironmentVariable("PCBS2COOP_PROBE_RESUME") == "1";

    public void Update()
    {
        try { Commands(); } catch (Exception e) { Event("command-error", e.ToString()); }
        if (!Safe || Time.realtimeSinceStartup < _nextSample) return;
        _nextSample = Time.realtimeSinceStartup + 0.25f;
        try { UpdateStressFixture(); } catch (Exception e) { Event("stress-fixture-failed", new { stage = "update", error = e.ToString() }); _stress = null; }
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
    // ---------------- operator commands (commands.txt next to events.jsonl) ----------------

    private void Commands()
    {
        if (!Safe) return;
        var file = Path.Combine(Path.GetDirectoryName(_file)!, "commands.txt");
        if (!File.Exists(file)) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, ""); return; }
        var lines = File.ReadAllLines(file);
        if (_skipCommandHistory)
        {
            // This file is an append-only operator journal. A fresh process must not run the earlier process's
            // save, exit or edit commands again while the menu is being initialized.
            _commandsRead = lines.Length;
            _skipCommandHistory = false;
            Event("commands-resumed", new { skipped = _commandsRead });
            return;
        }
        while (_commandsRead < lines.Length)
        {
            var command = lines[_commandsRead++].Trim();
            if (command.Length == 0) continue;
            Event("operator-command", command);
            if (command.StartsWith("aim:", StringComparison.Ordinal)) Aim(command);
            else if (command == "look-remote") LookAtRemote();
            else if (command == "desktop-report") Event("desktop-report", DesktopReport());
            else if (command.StartsWith("screen-dump:", StringComparison.Ordinal)) Event("screen-dump", ScreenDump(command[12..]));
            else if (command.StartsWith("repair:", StringComparison.Ordinal) && uint.TryParse(command[7..], out var repairPc))
                CoopRunner.Instance?.Coop.Repair?.Failed(new NetObjectId(repairPc), "probe: pretend this game couldn't show a change", new InvalidDataException("probe"));
            else if (SavesCommand(command)) { }
            else if (WorkshopCommand(command)) { }
            else if (StressCommand(command)) { }
            else if (ToolsCommand(command)) { }
            else if (GesturesCommand(command)) { }
            else if (command == "sim-report") Event("sim-report", SimReport());
            else if (command.StartsWith("sim-", StringComparison.Ordinal) || command.StartsWith("tuner", StringComparison.Ordinal) ||
                     command.StartsWith("fans", StringComparison.Ordinal) || command.StartsWith("programs:", StringComparison.Ordinal) ||
                     command.StartsWith("launch:", StringComparison.Ordinal))
            {
                try { Event("sim-command", SimCommand(command)); }
                catch (Exception e) { Event("sim-command", "failed: " + e.Message); }
            }
            else Event("command-unknown", command);
        }
    }

    // ---------------- phase 7: simulation, GPU Tuner and Fan Control ----------------

    private static VirtualComputer ProbeComputer(string arg)
    {
        var coop = CoopRunner.Instance?.Coop;
        if (!uint.TryParse(arg, out var id) || coop?.Objects.Initialized != true) throw new ArgumentException("bad pc");
        return coop.Objects.FindBench(new NetObjectId(id))?.m_workStation?.m_virtualComputer ?? throw new ArgumentException("no vc");
    }

    private static object SimReport()
    {
        var coop = CoopRunner.Instance?.Coop;
        if (coop?.Objects.Initialized != true) return "not ready";
        return coop.Objects.Model.Pcs.Where(p => p.Location.Place == PcPlace.Bench).Select(pc =>
        {
            var vc = coop.Objects.FindBench(pc.Id)?.m_workStation?.m_virtualComputer;
            var sim = vc?.GetComputerSim();
            var bios = SafeName(() => { var b = vc?.ComputerSave?.m_biosConfig; return b == null ? "none" : $"{b.m_gpuClock}/{b.m_gpuMemClock}/{b.m_gpuVoltagePercent}% x{b.m_cpuMultiplier}"; });
            var unmatched = new List<string>();
            var fans = SafeName(() => string.Join(" ", NativeSimulation.FansOf(vc?.ComputerSave, sim?.GetCase(), sim, unmatched).Select(f =>
            {
                var d = f.Fan.CustomControlData; var saved = d?.BuildSaveData(0);
                return $"{f.Path}#{f.Index}:{(saved == null ? "-" : $"{(saved.CustomEnabled ? "C" : "S")}{(int)saved.Preset}/{saved.CustomGraphPoints?.Count ?? -1}")}";
            })));
            return new
            {
                pc = pc.Id.Value, powered = SafeBool(() => vc.IsPoweredOn()), surface = vc == null ? null : NativeDesktopInput.SurfaceOf(vc)?.ToString(),
                running = SafeBool(() => sim.IsRunning()), bsod = SafeBool(() => sim.BSODActive),
                cpu = SafeName(() => sim == null ? null : sim.GetCpuTemp().ToString("0.0")), throttled = SafeBool(() => sim.IsCpuThrottled()),
                bios, fans, unmatched = string.Join(" ", unmatched),
            };
        }).ToArray();
    }

    private static object SimCommand(string command)
    {
        var bits = command.Split(':');
        var vc = ProbeComputer(bits.ElementAtOrDefault(1));
        var os = vc.m_os;
        switch (bits[0])
        {
            case "sim-fail": // as the game's own simulation would: not forced, not a projection
                vc.GetComputerSim().EmergencyShutdown((EmergencyShutdownReason)int.Parse(bits[2]), false);
                return new { bsod = vc.GetComputerSim().BSODActive };
            case "programs":
                var installed = os?.m_programsInstalled;
                return new { installed = installed == null ? null : installed.ToArray(), open = DesktopWindows(os) };
            case "launch":
                var desc = PartsDatabase.s_instance?.GetProgram(bits[2]) ?? throw new ArgumentException("no such program");
                os.Launch(desc, true);
                return "launched";
            case "tuner":
            case "tuner-reset":
                var tuner = vc.GetComponentsInChildren<GPUTuner>(false).FirstOrDefault() ?? throw new InvalidOperationException("GPU Tuner isn't open");
                if (bits[0] == "tuner-reset") tuner.OnReset();
                else
                {
                    tuner.m_gpuClock = int.Parse(bits[2]); tuner.m_gpuMemClock = int.Parse(bits[3]); tuner.m_gpuVoltagePercent = int.Parse(bits[4]);
                    tuner.OnApply();
                }
                return "done";
            case "fans":
            case "fans-custom":
                var app = vc.GetComponentsInChildren<FanControlApp>(false).FirstOrDefault() ?? throw new InvalidOperationException("Fan Control isn't open");
                var row = app.FanList?.Count > 0 ? app.FanList[0] : throw new InvalidOperationException("no fan rows");
                app.ButtonPress_SelectFan(row, false);
                var data = row.MyFan?.CustomControlData ?? throw new InvalidOperationException("fan has no control data");
                if (bits[0] == "fans-custom") data.EnableCustomMode(true);
                else data.Preset = (FanControlData.GraphPreset)int.Parse(bits[2]);
                app.ApplySetting(-1);
                return new { fan = row.MyFan.name, preset = (int)data.Preset, custom = data.IsCustom() };
        }
        throw new ArgumentException("unknown");
    }

    private static object DesktopReport()
    {
        var coop = CoopRunner.Instance?.Coop;
        if (coop?.Objects.Initialized != true) return "not ready";
        var local = NativeDesktopInput.LocalMonitor(coop);
        return new
        {
            role = coop.Session.Role.ToString(),
            monitor = local.Pc.Value,
            pcs = coop.Objects.Model.Pcs.Where(p => p.Location.Place == PcPlace.Bench).Select(pc =>
            {
                var vc = coop.Objects.FindBench(pc.Id)?.m_workStation?.m_virtualComputer;
                var os = vc?.m_os;
                string software = null, error = null;
                try
                {
                    var drive = vc?.ComputerSave?.GetBiosConfig(false)?.GetBootDrive();
                    if (drive?.m_software != null)
                        software = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(NativeSoftwareCodec.Write(drive.m_software)))[..16];
                }
                catch (Exception e) { error = e.Message; }
                var root = os?.m_desktop?.canvas?.rootCanvas;
                return new
                {
                    pc = pc.Id.Value,
                    powered = SafeBool(() => vc.IsPoweredOn()),
                    surface = vc == null ? null : NativeDesktopInput.SurfaceOf(vc)?.ToString(),
                    windows = DesktopWindows(os),
                    software, error,
                    rootMode = root?.renderMode.ToString(),
                    rootCamera = root?.worldCamera?.name,
                    rootCasters = root == null ? 0 : root.GetComponentsInChildren<UnityEngine.UI.GraphicRaycaster>(false).Length,
                    rootSize = root == null ? null : root.GetComponent<RectTransform>()?.rect.size.ToString(),
                    bios = SafeName(() => vc.GetBios()?.GetComponentInParent<Canvas>()?.rootCanvas?.name),
                    vcCanvas = SafeName(() => vc.m_canvas?.rootCanvas?.name),
                    desktopCanvas = root?.name,
                };
            }).ToArray()
        };
    }

    private static bool? SafeBool(Func<bool> f) { try { return f(); } catch { return null; } }
    private static string SafeName(Func<string> f) { try { return f(); } catch (Exception e) { return "!" + e.Message; } }

    /// <summary>Names, active flags and components of a PC's screen canvas tree (depth-limited).</summary>
    private static object ScreenDump(string arg)
    {
        var coop = CoopRunner.Instance?.Coop;
        if (!uint.TryParse(arg, out var id) || coop?.Objects.Initialized != true) return "bad pc";
        var vc = coop.Objects.FindBench(new NetObjectId(id))?.m_workStation?.m_virtualComputer;
        if (vc == null) return "no vc";
        var rows = new List<string>();
        void Walk(Transform t, int depth, string path)
        {
            if (rows.Count > 600 || depth > 7) return;
            var comps = string.Join(",", t.GetComponents<Component>().Select(c => c?.GetIl2CppType().Name).Where(n => n != "Transform" && n != "RectTransform" && n != "CanvasRenderer"));
            rows.Add($"{path} {t.name} {(t.gameObject.activeInHierarchy ? "on" : "off")} [{comps}]");
            for (var i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1, path.Length == 0 ? i.ToString() : path + "/" + i);
        }
        Walk(vc.transform, 0, "");
        return rows;
    }

    private static void Aim(string command)
    {
        var bits = command.Split(':');
        if (bits.Length is < 3 or > 4 || !int.TryParse(bits[2], out var index) || index < 0)
            throw new ArgumentException("aim:<bench|cupboard|peripheral>:<index>[:height-offset]");
        var offset = 0f;
        if (bits.Length == 4 && (!float.TryParse(bits[3], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out offset) || !float.IsFinite(offset) || Math.Abs(offset) > 1))
            throw new ArgumentException("Aim height offset must be within one metre.");
        var root = LocalPlayer.Root;
        if (Camera.main == null || root == null) throw new InvalidOperationException("No workshop player.");
        Component target = bits[1] switch
        {
            "bench" => Object.FindObjectOfType<SaveLoadSystem>().benchSlots[index],
            "cupboard" => Object.FindObjectsOfType<Cupboard>(true).OrderBy(x => DesktopPath(x.transform, null)).ElementAt(index),
            "peripheral" => Object.FindObjectsOfType<PeripheralSlot>(true).OrderBy(x => DesktopPath(x.transform, null)).ElementAt(index),
            _ => throw new ArgumentException("Unknown aim target.")
        };
        var col = target is BenchSlot bench ? bench.m_caseCol : target.GetComponentInChildren<Collider>();
        var point = col != null ? col.bounds.center : target.transform.position + Vector3.up;
        point.y += offset;
        var reset = (target as BenchSlot)?.PlayerResetPosition;
        var pos = reset != null ? reset.transform.position : point + target.transform.forward * 1.3f;
        pos.y = root.position.y;
        var cc = LocalPlayer.Controller;
        var wasEnabled = cc != null && cc.enabled;
        if (wasEnabled) cc.enabled = false;
        try { root.position = pos; }
        finally { if (wasEnabled) cc.enabled = true; }
        LookAt(root, point);
        _current?.Event("camera-setup", new { target = target.name, path = DesktopPath(target.transform, null), position = pos.ToString(), aim = point.ToString() });
    }

    private void LookAtRemote()
    {
        var coop = CoopRunner.Instance?.Coop;
        var root = LocalPlayer.Root;
        if (coop?.RemoteViewPosition is not { } remote || root == null || Camera.main == null)
        {
            Event("look-remote", new { available = false });
            return;
        }
        LookAt(root, remote + Vector3.up * 1.4f);
        Event("look-remote", new { available = true, view = coop.RemoteViewReport, activity = coop.RemoteActivity });
    }

    private static void LookAt(Transform root, Vector3 point)
    {
        var cam = Camera.main;
        var dir = point - cam.transform.position;
        var yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        var pitch = -Mathf.Atan2(dir.y, new Vector2(dir.x, dir.z).magnitude) * Mathf.Rad2Deg;
        foreach (var look in root.GetComponentsInChildren<MouseLook>(true))
        {
            var parentYaw = look.transform.parent != null ? look.transform.parent.eulerAngles.y : 0;
            look.rotationX = look.transform == root ? yaw : yaw - parentYaw;
            look.rotationY = -pitch;
        }
        cam.transform.rotation = Quaternion.Euler(pitch, yaw, 0);
    }
}

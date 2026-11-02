using HarmonyLib;
using PCBS2Coop.Network;
using UnityEngine;

namespace PCBS2Coop;

/// <summary>Phase 7: what the game's PC simulation decides once for both games. Each game simulates every powered PC
/// itself from the same mirrored app input (phase 6), so loads, temperatures and benchmark scores normally agree; a
/// failure (blue screen) can still come a moment earlier on one game, or on one game only. Owner question 1
/// (recommended answer): the host's game decides. The guest holds back its own failures on shared PCs and shows the
/// host's. Fan curves applied in the Fan Control app are sent as the result (the curve dragging itself is polled input
/// that phase 6 doesn't mirror).</summary>
internal sealed class NativeSimulation : ISimulationNative
{
    private const float FailureWait = 10f, FanSettle = 0.25f;

    private sealed class PendingFailure { public NetObjectId Pc; public int Reason; public float Since; }

    private readonly CoopController _coop;
    private readonly WorldSync _world;
    private readonly List<(NetObjectId Pc, int Reason)> _hostFailures = new();
    private readonly List<PendingFailure> _guestFailures = new();
    private readonly Dictionary<NetObjectId, float> _fanSends = new();
    private readonly Dictionary<NetObjectId, float> _heldLogged = new();
    private ulong _fansForGuest;
    public SimulationWorld Handler { get; }
    internal static NativeSimulation Instance => CoopController.Instance?.Simulation;

    public NativeSimulation(CoopController coop, WorldSync world)
    {
        _coop = coop; _world = world;
        Handler = new SimulationWorld(coop.Objects.Model, coop.Activity, this);
        world.GenerationStarted += (uint _, string _) =>
        { _hostFailures.Clear(); _guestFailures.Clear(); _fanSends.Clear(); _heldLogged.Clear(); _waitingFans.Clear(); _fansForGuest = 0; };
        world.Rejected += (OperationId op, RejectReason reason, string text) =>
        {
            if (op.Actor == world.Local && text.Contains(Handler.Name)) Plugin.Logger.LogWarning($"[sim] {op} refused: {text}");
        };
    }

    private bool Active => _world.Admitted && _world.Divergence == null && _coop.Objects.Initialized && _coop.WorldReady;

    private static VirtualComputer ComputerOf(CoopController coop, NetObjectId pc) =>
        coop.Objects.FindBench(pc)?.m_workStation?.m_virtualComputer;

    /// <summary>The shared PC a simulation belongs to, or none.</summary>
    private NetObjectId PcOf(ComputerSim sim)
    {
        if (sim == null) return default;
        try
        {
            var id = _coop.Objects.IdOf(sim.GetCase());
            if (id.IsNone) id = _coop.Objects.IdOf(sim.Comp);
            return id;
        }
        catch { return default; }
    }

    // ---- Failures ----

    /// <summary>Guest: hold back this game's own failure on a shared PC; the host's arrives instead.</summary>
    private bool AllowLocalFailure(ComputerSim sim, int reason)
    {
        if (_world.IsAuthority || NativeProjection.Active || !Active) return true;
        var pc = PcOf(sim);
        if (pc.IsNone) return true;
        // Only log when the game would really have failed (it calls this every frame while the condition holds).
        if (sim.IsRunning() && !sim.BSODActive && (!_heldLogged.TryGetValue(pc, out var at) || Time.realtimeSinceStartup - at > 10f))
        {
            _heldLogged[pc] = Time.realtimeSinceStartup;
            Plugin.Logger.LogInfo($"[sim] PC {pc}: this game would fail ({(EmergencyShutdownReason)reason}); waiting for the host's result.");
        }
        return false;
    }

    /// <summary>Host: a shared PC just failed in this game.</summary>
    private void LocalFailed(ComputerSim sim, int reason)
    {
        if (!_world.IsAuthority || !Active) return;
        var pc = PcOf(sim);
        if (pc.IsNone || reason is < SimulationWorld.MinReason or > SimulationWorld.MaxReason) return;
        Plugin.Logger.LogInfo($"[sim] PC {pc} failed here ({(EmergencyShutdownReason)reason}); sharing it.");
        _hostFailures.Add((pc, reason));
    }

    public void Failed(in MutationContext ctx, PcFact pc, int reason)
    {
        if (_world.IsAuthority) return; // The host's game already shows it.
        _guestFailures.RemoveAll(f => f.Pc == pc.Id);
        _guestFailures.Add(new PendingFailure { Pc = pc.Id, Reason = reason, Since = Time.realtimeSinceStartup });
        ShowFailures();
    }

    /// <summary>Guest: show the host's failures once this game's PC is running.</summary>
    private void ShowFailures()
    {
        for (var i = _guestFailures.Count - 1; i >= 0; i--)
        {
            var f = _guestFailures[i];
            var sim = ComputerOf(_coop, f.Pc)?.GetComputerSim();
            var reason = (EmergencyShutdownReason)f.Reason;
            if (sim != null && sim.BSODActive)
            {
                _guestFailures.RemoveAt(i);
                Plugin.Logger.LogInfo($"[sim] PC {f.Pc}: the host's failure ({reason}) is already shown here.");
                continue;
            }
            if (sim == null || !sim.IsRunning())
            {
                if (Time.realtimeSinceStartup - f.Since < FailureWait) continue;
                _guestFailures.RemoveAt(i);
                Plugin.Logger.LogWarning($"[sim] PC {f.Pc}: the host's failure ({reason}) was dropped; this game's PC isn't running.");
                continue;
            }
            _guestFailures.RemoveAt(i);
            try
            {
                NativeProjection.Run(() => sim.EmergencyShutdown(reason, true));
                Plugin.Logger.LogInfo($"[sim] PC {f.Pc} failed ({reason}) from the host.");
            }
            catch (Exception e) { _coop.ProjectionFailed($"the host's failure on PC {f.Pc} could not be shown here", e); }
        }
    }

    // ---- Fan curves ----

    /// <summary>The PC's controllable fans keyed by native part path + position on that part. The game's Fan Control app
    /// lists the simulation's fans; the case's children cover a PC whose simulation isn't running.</summary>
    internal static List<(string Path, int Index, Fan Fan)> FansOf(ComputerSave record, Case pcCase, ComputerSim sim = null) =>
        FansOf(record, pcCase, sim, null);

    internal static List<(string Path, int Index, Fan Fan)> FansOf(ComputerSave record, Case pcCase, ComputerSim sim, List<string> unmatched)
    {
        var result = new List<(string, int, Fan)>();
        if (record == null || pcCase == null) return result;
        var paths = new Dictionary<long, string>();
        foreach (var (path, part) in NativeObjectState.Parts(record))
            if (part != null) paths.TryAdd(part.Pointer.ToInt64(), path);
        var fans = new List<Fan>();
        var known = new HashSet<long>();
        foreach (var fan in pcCase.GetComponentsInChildren<Fan>(true))
            if (fan != null && known.Add(fan.Pointer.ToInt64())) fans.Add(fan);
        var active = sim?.FansInSim;
        if (active != null)
            foreach (var fan in active)
                if (fan != null && known.Add(fan.Pointer.ToInt64())) fans.Add(fan);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var fan in fans)
        {
            if (!fan.CanBeControlled()) continue;
            var p = PathOf(fan, paths, pcCase.transform);
            if (p == null)
            {
                var part = fan.m_part;
                unmatched?.Add($"{fan.name}({(part == null ? "no part" : part.GetPart()?.m_id ?? "?")})");
                continue;
            }
            seen.TryGetValue(p, out var index);
            seen[p] = index + 1;
            result.Add((p, index, fan));
        }
        return result;
    }

    /// <summary>The fan's own part if the PC record lists it, else the nearest part above it that the record lists. A
    /// radiator's fans are separate part objects at run time from the ones its record keeps (game run sim-2), so they
    /// key on the radiator; both games build the same hierarchy, so their order on it agrees.</summary>
    private static string PathOf(Fan fan, Dictionary<long, string> paths, Transform root)
    {
        if (fan.m_part is { } own && paths.TryGetValue(own.Pointer.ToInt64(), out var direct)) return direct;
        for (var t = fan.transform; t != null; t = t.parent)
        {
            var component = t.GetComponent<ComponentPC>();
            var part = component?.GetPartInstance();
            if (part != null && paths.TryGetValue(part.Pointer.ToInt64(), out var path)) return path;
            if (root != null && t.Pointer == root.Pointer) break;
        }
        return null;
    }

    private static FanCurve CurveOf(string path, int index, Fan fan)
    {
        var data = fan.CustomControlData;
        if (data == null) return null;
        var saved = data.BuildSaveData(0);
        if (saved == null) return null;
        List<(float, float)> points = null;
        if (saved.CustomGraphPoints != null)
        {
            points = new List<(float, float)>();
            foreach (var p in saved.CustomGraphPoints) if (p != null) points.Add((p.PosX, p.PosY));
        }
        return new FanCurve(path, index, saved.CustomEnabled, (int)saved.Preset, (int)saved.CustomSensor, points);
    }

    private (ComputerSave Record, Case Case) Native(NetObjectId pc)
    {
        var vc = ComputerOf(_coop, pc);
        var record = vc?.ComputerSave;
        if (record == null || _coop.Objects.IdOf(record) != pc) return (null, null);
        var pcCase = vc.GetComputerSim()?.GetCase() ?? _coop.Objects.FindBench(pc)?.GetCaseInSlot();
        return (record, pcCase);
    }

    private List<FanCurve> Curves(NetObjectId pc)
    {
        var (record, pcCase) = Native(pc);
        return FansOf(record, pcCase, ComputerOf(_coop, pc)?.GetComputerSim()).Select(f => CurveOf(f.Path, f.Index, f.Fan)).Where(c => c is { IsValid: true }).ToList();
    }

    /// <summary>This player applied a Fan Control change on that app's PC.</summary>
    private void LocalFansApplied(FanControlApp app)
    {
        if (!Active || NativeDesktopInput.Injecting || NativeProjection.Active) return;
        var record = app?.Station?.m_virtualComputer?.ComputerSave;
        var pc = record == null ? default : _coop.Objects.IdOf(record);
        if (pc.IsNone) return;
        _fanSends[pc] = Time.realtimeSinceStartup + FanSettle; // after the app's own refresh in this frame
    }

    private void SendFans()
    {
        foreach (var (pc, due) in _fanSends.ToArray())
        {
            if (Time.realtimeSinceStartup < due) continue;
            _fanSends.Remove(pc);
            var actor = _coop.Activity.Get(_world.Local);
            if (actor == null) continue;
            try
            {
                var curves = Curves(pc);
                if (curves.Count == 0) continue;
                foreach (var batch in curves.Chunk(SimulationWorld.MaxFans))
                    if (!_world.Submit(CommandTypes.Simulation, SimulationWorld.Fans(actor.Epoch, pc, batch), out var op))
                        _coop.Notice("Fan curves not shared: not connected to the host's world.");
                    else Plugin.Logger.LogInfo($"[fans] PC {pc}: {batch.Length} fan curve(s) sent ({op}).");
            }
            catch (Exception e) { Plugin.Logger.LogError($"[fans] PC {pc}: sending fan curves failed: {e}"); }
        }
    }

    /// <summary>Host: a guest that just joined gets every shared PC's live curves (the game doesn't save them).</summary>
    private void RefreshGuest()
    {
        if (!_world.IsAuthority) return;
        var guest = _coop.Activity.Get(ActorSlot.Guest)?.Epoch ?? 0;
        if (guest == _fansForGuest) return;
        _fansForGuest = guest;
        if (guest == 0) return;
        foreach (var pc in _coop.Objects.Model.Pcs.Where(p => p.Location.Place == PcPlace.Bench && p.Powered))
            if (Native(pc.Id).Record != null) _fanSends[pc.Id] = Time.realtimeSinceStartup + 2f;
    }

    public void FansChanged(in MutationContext ctx, PcFact pc, IReadOnlyList<FanCurve> fans)
    {
        if (ctx.FromLocalPlayer) return; // This game already has them.
        _waitingFans.Remove(pc.Id);
        if (!ShowFans(ctx.Op, pc.Id, fans))
        {
            // A guest that just joined may still be loading that PC (power comes back about 90 s after a load): retry.
            _waitingFans[pc.Id] = (ctx.Op, fans, Time.realtimeSinceStartup);
            Plugin.Logger.LogInfo($"[fans] PC {pc.Id} isn't shown here yet; its curves wait.");
        }
    }

    private readonly Dictionary<NetObjectId, (OperationId Op, IReadOnlyList<FanCurve> Fans, float Since)> _waitingFans = new();

    private void RetryFans()
    {
        foreach (var (pc, w) in _waitingFans.ToArray())
        {
            if (_coop.Objects.Model.Pcs.All(p => p.Id != pc) || Time.realtimeSinceStartup - w.Since > 150f)
            { _waitingFans.Remove(pc); Plugin.Logger.LogWarning($"[fans] PC {pc}: waiting curves dropped."); continue; }
            if (ShowFans(w.Op, pc, w.Fans)) _waitingFans.Remove(pc);
        }
    }

    /// <summary>False while that PC isn't shown here yet.</summary>
    private bool ShowFans(OperationId op, NetObjectId pc, IReadOnlyList<FanCurve> fans)
    {
        try
        {
            var (record, pcCase) = Native(pc);
            if (record == null || pcCase == null || ComputerOf(_coop, pc)?.GetComputerSim() is not { } sim || !sim.IsRunning()) return false;
            var local = FansOf(record, pcCase, sim);
            var applied = 0;
            NativeProjection.Run(() =>
            {
                foreach (var curve in fans)
                {
                    var fan = local.FirstOrDefault(f => f.Path == curve.Path && f.Index == curve.Index).Fan;
                    if (fan == null) { Plugin.Logger.LogWarning($"[fans] PC {pc}: no fan {curve.Path}#{curve.Index} here."); continue; }
                    Restore(fan, curve);
                    applied++;
                }
                pcCase.m_airflowSim?.RefreshSimData(2f);
                RefreshApps(record);
            });
            Plugin.Logger.LogInfo($"[fans] PC {pc}: {applied}/{fans.Count} fan curve(s) from {op} applied.");
        }
        catch (Exception e) { _coop.ProjectionFailed($"fan curves on PC {pc} could not be shown here", e); }
        return true;
    }

    private static void Restore(Fan fan, FanCurve curve)
    {
        var saved = new FanControlDataSaveContainer
        {
            DebugName = fan.name, ParentFanID = curve.Index, CustomEnabled = curve.Custom,
            Preset = (FanControlData.GraphPreset)curve.Preset, CustomSensor = (FanControlGraphData.Sensor)curve.Sensor,
        };
        if (curve.Points != null)
        {
            var list = new Il2CppSystem.Collections.Generic.List<SavedFanControlGraphPoint>();
            foreach (var (x, y) in curve.Points) list.Add(new SavedFanControlGraphPoint(x, y));
            saved.CustomGraphPoints = list;
        }
        fan.CustomControlData ??= new FanControlData(fan);
        if (!fan.CustomControlData.RestoreSaveData(fan, saved))
            Plugin.Logger.LogWarning($"[fans] the game didn't accept the curve for {curve.Path}#{curve.Index}.");
    }

    /// <summary>An open Fan Control app on that PC shows the new curves and keeps them as its last applied state.</summary>
    private static void RefreshApps(ComputerSave record)
    {
        foreach (var app in UnityEngine.Object.FindObjectsOfType<FanControlApp>())
        {
            if (app == null || app.Station?.m_virtualComputer?.ComputerSave?.Pointer != record.Pointer) continue;
            try
            {
                var selections = app.SelectionList;
                if (selections != null)
                    foreach (var s in selections)
                        if (s?.FR?.MyFan?.CustomControlData is { } data) s.CustomControlDataBackup = new FanControlData(data);
                app.UpdateSelection(false);
                app.SetDirty(false);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[fans] refreshing an open Fan Control app failed: " + e.Message); }
        }
    }

    public void Update()
    {
        if (!Active) return;
        if (_hostFailures.Count > 0 && _coop.Activity.Get(ActorSlot.Host) is { } host)
        {
            foreach (var (pc, reason) in _hostFailures)
                if (_coop.Objects.Model.Pcs.Any(p => p.Id == pc && p.Location.Place == PcPlace.Bench))
                    _world.Submit(CommandTypes.Simulation, SimulationWorld.Failure(host.Epoch, pc, reason), out _);
            _hostFailures.Clear();
        }
        if (_guestFailures.Count > 0) ShowFailures();
        if (_waitingFans.Count > 0) RetryFans();
        RefreshGuest();
        if (_fanSends.Count > 0) SendFans();
    }

    private static bool Bypass => !NativePcLifecycle.InCoopWorkshop;

    [HarmonyPatch(typeof(ComputerSim), nameof(ComputerSim.EmergencyShutdown))]
    internal static class FailureHook
    {
        // __0 = reason, __1 = Force. The game itself returns early when a failure is already shown or the PC is off.
        private static bool Prefix(ComputerSim __instance, EmergencyShutdownReason __0, bool __1, out bool __state)
        {
            __state = false;
            if (Bypass || __instance == null) return true;
            try
            {
                __state = __instance.BSODActive;
                return __1 || Instance?.AllowLocalFailure(__instance, (int)__0) != false;
            }
            catch (Exception e) { Plugin.Logger.LogError("[sim] failure hook: " + e); return true; }
        }

        private static void Postfix(ComputerSim __instance, EmergencyShutdownReason __0, bool __state)
        {
            if (Bypass || __instance == null || NativeProjection.Active) return;
            try { if (!__state && __instance.BSODActive) Instance?.LocalFailed(__instance, (int)__0); }
            catch (Exception e) { Plugin.Logger.LogError("[sim] failure hook: " + e); }
        }
    }

    [HarmonyPatch(typeof(FanControlApp), nameof(FanControlApp.ApplySetting))]
    internal static class FanApplyHook
    {
        private static void Postfix(FanControlApp __instance)
        {
            if (Bypass) return;
            try { Instance?.LocalFansApplied(__instance); }
            catch (Exception e) { Plugin.Logger.LogError("[fans] apply hook: " + e); }
        }
    }
}

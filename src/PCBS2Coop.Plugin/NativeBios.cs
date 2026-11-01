using HarmonyLib;
using PCBS2Coop.Network;
using UnityEngine;

namespace PCBS2Coop;

/// <summary>Intercepts the two BIOS commit buttons. The game keeps a private draft until Apply/Factory Reset;
/// the confirmed command writes only the PC-owned BIOS graph and restarts that PC's local virtual computer.</summary>
internal sealed class NativeBios : IBiosNative
{
    private readonly CoopController _coop;
    private readonly WorldSync _world;
    private readonly HashSet<OperationId> _pending = new();
    private readonly Queue<(MutationContext Context, NetObjectId Pc, BiosChange Change, float Deadline)> _deferred = new();
    private bool _submitting;
    public BiosWorld Handler { get; }

    public NativeBios(CoopController coop, WorldSync world)
    {
        _coop = coop; _world = world;
        Handler = new BiosWorld(coop.Objects.Model, coop.Activity, this);
        world.Accepted += (OperationId op, ulong revision) => _pending.Remove(op);
        world.Rejected += (OperationId op, RejectReason reason, string text) =>
        {
            if (_pending.Remove(op) || (_submitting && op.Actor == world.Local))
                _coop.Notice("BIOS refused: " + text.Replace(Handler.Name + ": ", ""));
        };
        world.GenerationStarted += (uint generation, string reason) => { _pending.Clear(); _deferred.Clear(); };
    }

    private bool Active => _world.Admitted && _world.Divergence == null && _coop.Objects.Initialized && _coop.WorldReady;

    private (PcFact Pc, VirtualComputer Computer, ComputerSave Record) Find(Bios bios)
    {
        foreach (var pc in _coop.Objects.Model.Pcs.Where(x => x.Location.Place == PcPlace.Bench))
        {
            var vc = _coop.Objects.FindBench(pc.Id)?.m_workStation?.m_virtualComputer;
            if (vc == null) continue;
            Bios shown;
            try { shown = vc.GetBios(); }
            catch { continue; }
            if (shown == null || shown.Pointer != bios.Pointer) continue;
            var record = vc.ComputerSave;
            if (record == null || _coop.Objects.IdOf(record) != pc.Id)
                throw new InvalidDataException("The BIOS is not bound to the shared PC record.");
            return (pc, vc, record);
        }
        throw new InvalidDataException("This BIOS is not on a shared bench PC.");
    }

    private (VirtualComputer Computer, ComputerSave Record) Find(PcFact pc)
    {
        var vc = _coop.Objects.FindBench(pc.Id)?.m_workStation?.m_virtualComputer;
        var record = vc?.ComputerSave;
        if (record == null || _coop.Objects.IdOf(record) != pc.Id)
            throw new InvalidDataException("The shared PC's BIOS record is not ready here.");
        return (vc, record);
    }

    private static BiosSettings Capture(ComputerSave record, BiosConfig config)
    {
        if (record == null || config == null) throw new InvalidDataException("BIOS configuration is unavailable.");
        var drive = config.GetBootDrive();
        var parts = NativeObjectState.Parts(record);
        var path = drive == null ? "" : parts.FirstOrDefault(x => x.Value?.Pointer == drive.Pointer).Key;
        if (drive != null && path == null) throw new InvalidDataException("The boot drive is not in this PC's native graph.");
        var result = new BiosSettings(config.m_baseClock, config.m_cpuMultiplier, config.m_cpuVoltage,
            config.m_RAMClock, config.m_RAMVoltage, config.m_RAMTimings?.ToArray() ?? Array.Empty<int>(),
            config.m_xmp, config.m_gpuClock, config.m_gpuMemClock, config.m_gpuVoltagePercent,
            config.m_gpuClockLast, config.m_gpuMemClockLast, config.m_gpuVoltLast, path ?? "", config.m_OSInstallFlag);
        if (!result.IsValid) throw new InvalidDataException("The game's BIOS values exceed the shared format.");
        return result;
    }

    private static string Baseline(ComputerSave record) => BiosWorld.Baseline(
        record.m_biosConfig == null ? null : Capture(record, record.m_biosConfig));

    private static PartInstance Drive(ComputerSave record, PcFact pc, string path)
    {
        if (path.Length == 0) return null;
        if (!(path.StartsWith("storageSlots[", StringComparison.Ordinal) ||
              path.StartsWith("m2Slots[", StringComparison.Ordinal) ||
              path.StartsWith("usbSlots[", StringComparison.Ordinal)))
            throw new InvalidDataException("Boot drive must be an installed storage or USB slot.");
        if (!pc.Links.Any(x => x.Path == path)) throw new InvalidDataException("Unknown boot-drive path.");
        if (!NativeObjectState.Parts(record).TryGetValue(path, out var drive) || drive == null)
            throw new InvalidDataException("Boot drive is missing on this game.");
        return drive;
    }

    internal bool Request(Bios bios, bool reset)
    {
        if (!Active) { _coop.Notice("BIOS refused: the shared world is not ready."); return false; }
        try
        {
            var (pc, _, record) = Find(bios);
            var actor = _coop.Activity.Get(_world.Local);
            if (actor == null || actor.WorkingPc != pc.Id) { _coop.Notice("BIOS refused: wait for monitor activity to sync."); return false; }
            var expected = record.m_biosConfig == null ? null : Capture(record, record.m_biosConfig);
            var target = reset ? null : Capture(record, bios.BiosConfig);
            var bytes = BiosWorld.Request(actor.Epoch, pc.Id, expected, target);
            _submitting = true;
            try
            {
                if (!_world.Submit(CommandTypes.Bios, bytes, out var op))
                { _coop.Notice("BIOS refused: not connected to the host's world."); return false; }
                if (!_world.IsAuthority) _pending.Add(op);
                Plugin.Logger.LogInfo($"[bios] PC {pc.Id} {(reset ? "factory reset" : "apply and restart")} requested by {op}.");
            }
            finally { _submitting = false; }
        }
        catch (Exception e) { Plugin.Logger.LogError("[bios] request failed: " + e); _coop.Notice("BIOS refused: " + e.Message); }
        return false; // The native button runs only after host confirmation.
    }

    public string CheckHost(in CommandContext ctx, PcFact pc, BiosChange change)
    {
        var (_, record) = Find(pc);
        if (Baseline(record) != change.ExpectedDigest) return "The BIOS configuration changed before this request arrived.";
        if (change.Settings is { } target) Drive(record, pc, target.BootPath);
        return null;
    }

    private void Project(in MutationContext ctx, PcFact pc, BiosChange change)
    {
        var vc = pc.Location.Place == PcPlace.Bench ? _coop.Objects.FindBench(pc.Id)?.m_workStation?.m_virtualComputer : null;
        var live = vc?.ComputerSave;
        if (live != null && _coop.Objects.IdOf(live) != pc.Id)
            throw new InvalidDataException("The live BIOS record belongs to another PC.");
        var record = live ?? _coop.Objects.RecordFor(pc.Id);
        if (record == null) throw new InvalidOperationException("The PC's native BIOS record is still loading.");
        if (change.Reset && record.m_biosConfig == null) return; // A load can already contain the reset result.
        var current = Baseline(record);
        if (current != change.ExpectedDigest)
        {
            if (change.Settings is { } already && current == already.Digest()) return;
            throw new InvalidDataException("The native BIOS baseline changed before projection.");
        }
        NativeProjection.Run(() =>
        {
            if (change.Reset) record.m_biosConfig = null;
            else
            {
                var target = change.Settings ?? throw new InvalidDataException("Missing BIOS settings.");
                var config = record.GetBiosConfig(false)?.Clone() ?? throw new InvalidDataException("BIOS configuration disappeared.");
                config.m_baseClock = target.BaseClock; config.m_cpuMultiplier = target.CpuMultiplier; config.m_cpuVoltage = target.CpuVoltage;
                config.m_RAMClock = target.RamClock; config.m_RAMVoltage = target.RamVoltage;
                config.m_RAMTimings = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(target.RamTimings.ToArray());
                config.m_xmp = target.Xmp; config.m_gpuClock = target.GpuClock; config.m_gpuMemClock = target.GpuMemClock;
                config.m_gpuVoltagePercent = target.GpuVoltagePercent; config.m_gpuClockLast = target.GpuClockLast;
                config.m_gpuMemClockLast = target.GpuMemClockLast; config.m_gpuVoltLast = target.GpuVoltLast;
                config.m_OSInstallFlag = target.OsInstallFlag;
                config.SetBootDrive(Drive(record, pc, target.BootPath));
                record.m_biosConfig = config;
            }
            // A not-yet-live PC will boot through phase 5's normal power restore using this saved config.
            if (live != null) vc.Restart();
        });
        Plugin.Logger.LogInfo($"[bios] PC {pc.Id} {(change.Reset ? "factory reset" : "settings applied")} by {ctx.Op}" +
            (live == null ? " (record only)." : " (restarted)."));
    }

    public void Changed(in MutationContext ctx, PcFact pc, BiosChange change)
    {
        if (!_world.IsAuthority && _deferred.Count > 0) { Defer(ctx, pc, change); return; }
        try { Project(ctx, pc, change); }
        catch (InvalidOperationException) when (!_world.IsAuthority) { Defer(ctx, pc, change); }
        catch (Exception e) { _coop.ProjectionFailed($"BIOS change on PC {pc.Id} could not be shown here", e); }
    }

    private void Defer(in MutationContext ctx, PcFact pc, BiosChange change)
    {
        _deferred.Enqueue((ctx, pc.Id, change, Time.realtimeSinceStartup + 10f));
        Plugin.Logger.LogInfo($"[bios] PC {pc.Id} record is still loading; queued accepted change {ctx.Op}.");
    }

    public void Update()
    {
        while (_deferred.TryPeek(out var next))
        {
            if (_world.Generation != next.Context.Generation || !_world.Admitted) { _deferred.Clear(); return; }
            var pc = _coop.Objects.Model.Pcs.SingleOrDefault(x => x.Id == next.Pc);
            if (pc == null) { _deferred.Dequeue(); continue; } // A later accepted delete removed the PC.
            try { Project(next.Context, pc, next.Change); _deferred.Dequeue(); }
            catch (InvalidOperationException) when (Time.realtimeSinceStartup <= next.Deadline) { return; }
            catch (Exception e)
            {
                _deferred.Dequeue();
                _coop.ProjectionFailed($"BIOS change on PC {next.Pc} could not settle here", e);
                return;
            }
        }
    }

    private static bool Bypass => (NativeProjection.Active && !NativeDesktopInput.Injecting) || !NativePcLifecycle.InCoopWorkshop;

    [HarmonyPatch(typeof(Bios), "ApplyRestart")]
    internal static class ApplyIntercept
    {
        // A replayed click on the other player's Apply does nothing here: their request brings the result.
        private static bool Prefix(Bios __instance) => !NativeDesktopInput.Injecting && (Bypass || (CoopController.Instance?.Bios?.Request(__instance, reset: false) ?? false));
    }

    [HarmonyPatch(typeof(Bios), "ResetSettings")]
    internal static class ResetIntercept
    {
        private static bool Prefix(Bios __instance) => !NativeDesktopInput.Injecting && (Bypass || (CoopController.Instance?.Bios?.Request(__instance, reset: true) ?? false));
    }
}

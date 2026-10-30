using System.IO.Compression;
using HarmonyLib;
using PCBS2Coop.Network;
using UnityEngine;

namespace PCBS2Coop;

/// <summary>
/// Custom water-cooling edits (phase 10, owner decisions October 29, 2026). Laying a pipe (<c>&lt;PipingTick&gt;b__0</c>
/// opens <c>ConnectPipeState</c>, then <c>RoutePipeState</c>), removing a pipe (<c>&lt;DisassemblyTick&gt;b__3</c>),
/// filling or draining coolant (<c>WaterLoopController.FillLoop</c>/<c>EmptyLoop</c>) and installing or removing water
/// blocks, pumps, reservoirs and the VRM/heat-sink/GPU-cooler slots are one whole-PC <c>Water</c> edit in command 9.
/// The editor's game runs the game's own gesture and is followed until nothing moves on that PC. The editor then sends
/// its finished PC record, in chunks, and commits. The other game <b>reloads that PC</b> from the record, as the owner
/// chose, rather than replaying the animation. The host settles from its game as for other edits; parts keep their IDs
/// by path, since a reload makes new native instances. While it runs, the other player is told someone is building water
/// cooling there. The water-cooling workbench is not part of this.
/// </summary>
internal sealed partial class NativeComponentEdits
{
    private sealed class Reload
    {
        public ComponentEdit Edit;
        public NetObjectId Pc;
        public BenchSlot Bench;
        public long OldMesh;
        public bool Powered, Pressed;
        public float Started, PressedAt, QuietSince;
    }

    private readonly List<Reload> _reloads = new();
    private const float ReloadTimeout = 30f;

    private static readonly HashSet<Slot.SlotType> WaterSlots = new()
        { Slot.SlotType.RESERVOIR, Slot.SlotType.VRM, Slot.SlotType.HEAT_SINK, Slot.SlotType.GPU_COOLER };
    private static readonly HashSet<PartDesc.Type> WaterTypes = new()
    {
        PartDesc.Type.PUMP_RESERVOIR, PartDesc.Type.CPU_BLOCK, PartDesc.Type.WATER_COOLED_GPU, PartDesc.Type.PIPES, PartDesc.Type.PIPE_CONNECTORS,
        PartDesc.Type.PIPE_COOLANT, PartDesc.Type.MOTHERBOARD_BLOCK, PartDesc.Type.MEMORY_BLOCK, PartDesc.Type.GPU_BLOCK, PartDesc.Type.RESERVOIR,
        PartDesc.Type.PUMP, PartDesc.Type.RAM_HEATSINK,
    };

    /// <summary>A custom water-cooling part or slot: shared as a whole-PC water edit (reload), not as an animated part edit.</summary>
    internal static bool IsWater(Slot slot, PartDesc desc, ComponentPC comp)
    {
        if (slot != null && WaterSlots.Contains(slot.m_slotType)) return true;
        if (desc != null && WaterTypes.Contains(desc.m_type)) return true;
        return comp != null && (comp.m_includesPipes || comp.GetPartInstance()?.m_onLoop == true);
    }

    /// <summary>A water-cooling edit or its reload is running on that PC: its assembly binds after the host's settle.</summary>
    internal bool Reloading(NetObjectId pc) => Handler.On(pc)?.Rebuilt == true || _reloads.Any(r => r.Pc == pc);

    internal bool RequestWater(Case mesh, string what, System.Action replay) =>
        SubmitWired(mesh, EditGesture.Water, what.Length > ComponentEditWorld.MaxText ? what[..ComponentEditWorld.MaxText] : what, false, replay, "water cooling: " + what);

    // ---------------- the finished record ----------------

    /// <summary>The PC's native record as this game shows it, serialized and compressed.</summary>
    private byte[] RecordBytes(NetObjectId pc)
    {
        var record = _coop.Objects.CaptureRecord(pc) ?? throw new InvalidDataException("That PC has no native record.");
        var raw = NativePcCodec.Write(record);
        using var output = new MemoryStream();
        using (var zip = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, true)) zip.Write(raw, 0, raw.Length);
        return output.ToArray();
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zip = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(); var chunk = new byte[16384]; int read;
        while ((read = zip.Read(chunk, 0, chunk.Length)) != 0)
        {
            if (output.Length + read > CoopSavePair.MaxCarrierBytes) throw new InvalidDataException("The finished PC record expands beyond limits.");
            output.Write(chunk, 0, read);
        }
        return output.ToArray();
    }

    /// <summary>Editor: send the finished record's chunks ahead of the commit (host order keeps them first).</summary>
    private bool SendRecord(ulong epoch)
    {
        var record = _local?.Record;
        if (record == null) return true;
        var total = (record.Length + Rebuild.MaxChunkBytes - 1) / Rebuild.MaxChunkBytes;
        for (var i = 0; i < total; i++)
        {
            var data = record.AsSpan(i * Rebuild.MaxChunkBytes, Math.Min(Rebuild.MaxChunkBytes, record.Length - i * Rebuild.MaxChunkBytes)).ToArray();
            if (!_world.Submit(CommandTypes.ComponentEdit, ComponentEditWorld.Chunk(epoch, _local.Token, i, total, data), out _)) return false;
        }
        Plugin.Logger.LogInfo($"[water] edit {_local.Token}: sent the finished PC record ({record.Length} bytes in {total} chunks).");
        return true;
    }

    // ---------------- following this player's own edit ----------------

    private static bool WaterMoving() => UnityEngine.Object.FindObjectOfType<WaterLoopController>()?.IsAnimating() == true;

    private void PollWater()
    {
        var l = _local; var mesh = l.Request.Mesh; var now = Time.realtimeSinceStartup;
        if (mesh == null || !mesh)
        {
            _local = null; _cancel.Add(l.Token);
            Plugin.Logger.LogWarning($"[water] edit {l.Token}: the PC is gone here; cancelling.");
            return;
        }
        // Inside a pipe tool or a part's install/removal state on that PC, the edit is alive (up to ten minutes).
        var state = CurrentState;
        var inTool = state != null && l.Request.Work != null && state.Pointer != l.Request.Work.Pointer &&
            state.TryCast<WorkingOnComputerStateBase>()?.m_case?.Pointer == mesh.Pointer;
        if (inTool && now - l.Started < ConnectTimeout) l.Since = now;
        var busy = inTool || now - l.Started < QuietSeconds || CablesBusy(mesh) || WaterMoving() ||
            mesh.GetComponentsInChildren<CPin>(true).Any(p => p != null && p.IsAnimating());
        if (busy) { l.QuietSince = 0; return; }
        if (l.QuietSince == 0) { l.QuietSince = now; return; }
        if (now - l.QuietSince < QuietSeconds) return;
        byte[] record;
        try
        {
            record = RecordBytes(l.Request.Pc);
            if (record.Length > Rebuild.MaxBytes) throw new InvalidDataException($"the finished PC record is too large to share ({record.Length} bytes)");
        }
        catch (Exception e)
        {
            _local = null; _cancel.Add(l.Token);
            _coop.ProjectionFailed($"This game's water-cooling edit {l.Token} could not be shared", e);
            return;
        }
        if (record.AsSpan().SequenceEqual(l.RecordBefore))
        {
            Plugin.Logger.LogInfo($"[water] edit {l.Token} ({l.Request.Sku}) changed nothing; cancelling.");
            _local = null; _cancel.Add(l.Token);
            return;
        }
        l.Record = record;
        l.Result = EditResult.Rebuilt(new Rebuild(record.Length, System.Security.Cryptography.SHA256.HashData(record), Running(mesh)));
        l.Done = true;
        Plugin.Logger.LogInfo($"[water] edit {l.Token} ({l.Request.Sku}) finished here; the PC is {(Running(mesh) ? "on" : "off")}.");
    }

    // ---------------- showing the other player's edit: reload that PC ----------------

    private void StartReload(PcFact pc, ComponentEdit edit)
    {
        var bytes = Handler.RecordOf(edit.Token) ?? throw new InvalidDataException("the finished PC record is missing");
        var record = NativePcCodec.Read(Inflate(bytes));
        var bench = _coop.Objects.FindBench(pc.Id) ?? throw new InvalidDataException("the PC is not on a bench on this game");
        var old = bench.GetCaseInSlot();
        _coop.Objects.ReloadOnBench(pc, bench, record);
        _reloads.Add(new Reload
        {
            Edit = edit, Pc = pc.Id, Bench = bench, OldMesh = old == null ? 0 : old.Pointer.ToInt64(),
            Powered = edit.Result!.Rebuild!.Powered, Started = Time.realtimeSinceStartup,
        });
        Plugin.Logger.LogInfo($"[water] reloading PC {pc.Id} with the other player's finished water cooling ({edit.Part}).");
    }

    private void UpdateReloads()
    {
        foreach (var r in _reloads.ToArray())
        {
            var now = Time.realtimeSinceStartup;
            var mesh = r.Bench == null ? null : r.Bench.GetCaseInSlot();
            var loaded = mesh != null && mesh.Pointer.ToInt64() != r.OldMesh && !r.Bench.m_caseLoadingToSlot && _coop.Objects.IdOf(mesh) == r.Pc;
            if (!loaded || CablesBusy(mesh) || WaterMoving())
            {
                r.QuietSince = 0;
                if (now - r.Started > ReloadTimeout) FinishReload(r, "the PC didn't finish reloading");
                continue;
            }
            if (r.Powered && !Running(mesh))
            {
                if (!r.Pressed)
                {
                    r.Pressed = true; r.PressedAt = now;
                    try { NativeProjection.Run(() => mesh.GetMotherboard()?.TurnOn()); }
                    catch (Exception e) { FinishReload(r, "turning the reloaded PC on failed: " + e.Message); }
                    continue;
                }
                if (now - r.PressedAt < PowerDelaySeconds) continue;
            }
            if (r.QuietSince == 0) { r.QuietSince = now; continue; }
            if (now - r.QuietSince < QuietSeconds) continue;
            FinishReload(r, r.Powered && !Running(mesh) ? "the reloaded PC didn't turn on" : null);
        }
    }

    private void FinishReload(Reload r, string error)
    {
        _reloads.Remove(r);
        var what = $"water-cooling edit {r.Edit.Token} on PC {r.Pc}";
        if (error != null) _coop.ProjectionFailed($"The other player's {what} was accepted, but this game could not show it", new InvalidDataException(error));
        else Plugin.Logger.LogInfo($"[water] {what}: reloaded here.");
        if (_world.IsAuthority) _ready.Add(r.Edit.Token);
    }

    // ---------------- interception ----------------

    /// <summary>The piping-mode long press on a fitting (opens <c>ConnectPipeState</c>).</summary>
    [HarmonyPatch(typeof(WorkingOnComputerState.__c__DisplayClass71_0), "_PipingTick_b__0")]
    internal static class PipeIntercept
    {
        private static bool Prefix(WorkingOnComputerState.__c__DisplayClass71_0 __instance)
        {
            if (WireBypass) return true;
            try
            {
                if (Current == null) return NotReady();
                var closure = __instance;
                return Current.RequestWater(closure.__4__this?.m_case, "pipe", () => closure._PipingTick_b__0());
            }
            catch (Exception e) { Plugin.Logger.LogError("[water] pipe request failed: " + e); return false; }
        }
    }

    /// <summary>Filling the loop with coolant (fill mode).</summary>
    [HarmonyPatch(typeof(WaterLoopController), nameof(WaterLoopController.FillLoop), new[] { typeof(ComponentPC), typeof(PartDesc) })]
    internal static class FillIntercept
    {
        private static bool Prefix(WaterLoopController __instance, ComponentPC comp, PartDesc coolant, ref bool __result)
        {
            if (WireBypass) return true;
            __result = false;
            try
            {
                if (Current == null) return NotReady();
                var controller = __instance; var target = comp; var fluid = coolant;
                var mesh = comp?.Slot?.GetCase() ?? comp?.GetComponentInParent<Case>();
                Current.RequestWater(mesh, $"coolant fill {coolant?.m_id}", () => controller.FillLoop(target, fluid));
            }
            catch (Exception e) { Plugin.Logger.LogError("[water] fill request failed: " + e); }
            return false;
        }
    }

    /// <summary>Draining the loop.</summary>
    [HarmonyPatch(typeof(WaterLoopController), nameof(WaterLoopController.EmptyLoop), new[] { typeof(ComponentPC) })]
    internal static class DrainIntercept
    {
        private static bool Prefix(WaterLoopController __instance, ComponentPC comp)
        {
            if (WireBypass) return true;
            try
            {
                if (Current == null) return NotReady();
                var controller = __instance; var target = comp;
                var mesh = comp?.Slot?.GetCase() ?? comp?.GetComponentInParent<Case>();
                Current.RequestWater(mesh, "coolant drain", () => controller.EmptyLoop(target));
            }
            catch (Exception e) { Plugin.Logger.LogError("[water] drain request failed: " + e); }
            return false;
        }
    }
}

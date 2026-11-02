using HarmonyLib;
using PCBS2Coop.Network;
using UnityEngine;

namespace PCBS2Coop;

/// <summary>
/// Switching a table between delidding, water cooling, case modding and working on the PC (phase 11, owner decision 5).
/// The game does it in its desk menu (<c>DeskFunctionChoice.SetUpNewTool</c>, and <c>ClearPreviousTool</c> for "work on
/// the PC"); the switch is shared and refused while the other player uses that table. The other game repeats the menu's
/// steps on that workstation without the menu itself (the menu ends with <c>GoBack</c>, which would leave that player's
/// own view).
/// </summary>
internal sealed partial class NativeToolBenches
{
    private bool _inSetUp, _submittingSwitch;
    private (string Bench, byte Before, OperationId Op)? _switching;

    private static byte KindOf(BenchToolSlot table) => (byte)table.CurrentBenchType;

    /// <summary>Why this player can't switch that table now, or null.</summary>
    private string SwitchRefusal(BenchToolSlot table)
    {
        if (table == null || Bypass) return null;
        if (!Ready) return "The shared world isn't ready on this game yet.";
        return Handler.Busy(Key(table), _world.Local);
    }

    private void Switching(BenchToolSlot table, byte before, byte kind, bool orbit)
    {
        if (table == null || Bypass || !Ready || before == kind) return;
        var key = Key(table);
        _switching = (key, before, default);
        _submittingSwitch = true;
        bool sent; OperationId op;
        try { sent = _world.Submit(CommandTypes.ToolBench, ToolBenchWorld.Switch(_coop.Activity.Get(_world.Local)!.Epoch, key, kind, orbit), out op); }
        finally { _submittingSwitch = false; }
        if (!sent)
        {
            _switching = null;
            Notice("Table change not shared: not connected to the host's world.");
            return;
        }
        if (_switching is { } pending && pending.Bench == key && pending.Op == default) _switching = (key, before, op);
        Plugin.Logger.LogInfo($"[tools] table {key}: switched to tool {kind} here (was {before}); sent {op}.");
    }

    /// <summary>The host refused this game's table switch: put the table back.</summary>
    private bool SwitchRejected(OperationId op, string text)
    {
        if (_switching is not { } s || (!_submittingSwitch && s.Op != op)) return false;
        _switching = null;
        Notice(text);
        var table = FindTable(s.Bench);
        if (table != null)
            try { NativeProjection.Run(() => ApplySwitch(table, s.Before, s.Before != ToolBenchWorld.NoTool)); }
            catch (Exception e) { Plugin.Logger.LogError($"[tools] putting table {s.Bench} back failed: {e}"); }
        return true;
    }

    private static BenchToolSlot FindTable(string bench)
    {
        foreach (var table in UnityEngine.Object.FindObjectsOfType<BenchToolSlot>())
            if (table != null && Key(table) == bench) return table;
        return null;
    }

    public void Switched(in MutationContext ctx, string bench, byte kind, bool orbit)
    {
        if (ctx.FromLocalPlayer)
        {
            if (_switching?.Bench == bench) _switching = null;
            return;
        }
        var table = FindTable(bench);
        if (table == null) { _coop.ProjectionFailed($"The other player switched tool table {bench}", new InvalidDataException("that table isn't on this game")); return; }
        try
        {
            var before = KindOf(table);
            if (before == kind) return;
            NativeProjection.Run(() => ApplySwitch(table, kind, orbit));
            Plugin.Logger.LogInfo($"[tools] the other player switched table {bench} to tool {kind} (was {before} here).");
        }
        catch (Exception e) { _coop.ProjectionFailed($"The other player's table switch ({bench}) could not be shown on this game", e); }
    }

    /// <summary>The desk menu's steps (<c>ClearPreviousTool</c>, then <c>SetUpNewTool</c> without its sound and menu exit).</summary>
    private static void ApplySwitch(BenchToolSlot table, byte kind, bool orbit)
    {
        var ws = table.Workstation ?? table.GetComponent<WorkStation>() ?? throw new InvalidDataException("the table has no workstation");
        void Place()
        {
            if (ws.CompSimToMove != null) ws.CompSimToMove.transform.localPosition = ws.CompSimPos;
            if (ws.MonitorToMove != null) ws.MonitorToMove.transform.localPosition = Vector3.zero;
        }
        Place();
        if (ws.MonitorToMove != null) ws.MonitorToMove.transform.localRotation = new Quaternion(0, 0, 0, 0);
        if (table.CurrentBenchType != (BenchToolSlot.Type)ToolBenchWorld.NoTool) table.SetTool(false, table.CurrentBenchType, true);
        if (kind == ToolBenchWorld.NoTool) return;
        Place();
        if (ws.MonitorToMove != null) ws.MonitorToMove.transform.localRotation = new Quaternion(0, 0, 0, 0);
        ws.InitMonitorSlot();
        table.SetTool(true, (BenchToolSlot.Type)kind, orbit);
    }

    // ---------------- hooks ----------------

    [HarmonyPatch(typeof(DeskFunctionChoice), nameof(DeskFunctionChoice.SetUpNewTool))]
    internal static class SwitchIntercept
    {
        private static bool Prefix(DeskFunctionChoice __instance, out int __state)
        {
            __state = -1;
            var tools = Current;
            if (tools == null || Bypass) return true;
            try
            {
                var table = __instance.BenchTools;
                if (tools.SwitchRefusal(table) is { } why) { tools.Notice(why); return false; }
                if (table != null) __state = KindOf(table);
                tools._inSetUp = true;
            }
            catch (Exception e) { Plugin.Logger.LogError("[tools] table switch hook failed: " + e); }
            return true;
        }

        private static void Postfix(DeskFunctionChoice __instance, BenchToolSlot.Type __0, bool __1, int __state)
        {
            var tools = Current;
            if (tools == null) return;
            tools._inSetUp = false;
            if (__state < 0) return;
            try { tools.Switching(__instance.BenchTools, (byte)__state, (byte)__0, __1); }
            catch (Exception e) { Plugin.Logger.LogError("[tools] table switch hook failed: " + e); }
        }
    }

    /// <summary>"Work on the PC" in the desk menu (outside <c>SetUpNewTool</c>, which clears the previous tool first).</summary>
    [HarmonyPatch(typeof(DeskFunctionChoice), nameof(DeskFunctionChoice.ClearPreviousTool))]
    internal static class ClearIntercept
    {
        private static bool Prefix(DeskFunctionChoice __instance, out int __state)
        {
            __state = -1;
            var tools = Current;
            if (tools == null || Bypass || tools._inSetUp) return true;
            try
            {
                var table = __instance.BenchTools;
                if (tools.SwitchRefusal(table) is { } why) { tools.Notice(why); return false; }
                if (table != null) __state = KindOf(table);
            }
            catch (Exception e) { Plugin.Logger.LogError("[tools] table switch hook failed: " + e); }
            return true;
        }

        private static void Postfix(DeskFunctionChoice __instance, int __state)
        {
            if (__state < 0 || Current is not { } tools) return;
            try { tools.Switching(__instance.BenchTools, (byte)__state, ToolBenchWorld.NoTool, true); }
            catch (Exception e) { Plugin.Logger.LogError("[tools] table switch hook failed: " + e); }
        }
    }
}

using System.IO.Compression;
using System.Security.Cryptography;
using HarmonyLib;
using LiteNetLib.Utils;
using PCBS2Coop.Network;
using UnityEngine;

namespace PCBS2Coop;

/// <summary>
/// The specialist tool tables in co-op (phase 9, owner decisions November 2, 2026). The delidding and water-cooling
/// tables are used by one player at a time ("Someone is using the delidding table.") and only their result is shared:
/// when a session ends, whatever it changed in the user's inventory (the delidded CPU, the water-cooled card with its
/// pads and paste) is sent to the other game, which makes the same inventory change. Nobody sees the procedure; the
/// other player's capsule shows a label instead. Phase 11 adds the case-modding table the same way: its result is the
/// finished case design, which the other game puts in its design list and shows by reloading that PC on the table
/// (NativeToolBenches.Designs.cs), and switching a table's tool (NativeToolBenches.Switch.cs).
/// <para>The tool states open as the game opens them (<c>BenchToolSlot</c> and <c>DeskLogic</c> closures construct
/// them). This class claims the table when its state appears and closes the state again if the claim is refused.</para>
/// </summary>
internal sealed partial class NativeToolBenches : IToolBenchNative
{
    private sealed class Session
    {
        public State State;
        public BenchToolSlot Table;
        /// <summary>Case modding: the PC on the table when the session started.</summary>
        public NetObjectId Pc;
        /// <summary>Case modding: the design's hash when the session started (null: always share it).</summary>
        public string DesignBefore;
        public string Bench;
        public BenchTool Tool;
        public Dictionary<string, List<PartInstance>> Before;
        public bool Destroyed;
        public float GoneAt;
        public OperationId Use;
    }

    private const float Quiet = 0.75f, UseWait = 15f;
    private const int MaxItems = 16, MaxItemBytes = 256 * 1024;
    private readonly CoopController _coop;
    private readonly WorldSync _world;
    private Session _session;
    private bool _submittingUse, _announce;
    public ToolBenchWorld Handler { get; }
    private static NativeToolBenches Current => CoopController.Instance?.ToolBenches;

    public NativeToolBenches(CoopController coop, WorldSync world)
    {
        _coop = coop; _world = world;
        Handler = new ToolBenchWorld(coop.Activity, this);
        world.Rejected += OnRejected;
        world.GenerationStarted += (uint generation, string reason) => { Handler.Reset(); _session = null; _announce = false; };
    }

    private bool Ready => _world.Admitted && _world.Divergence == null && _coop.Objects.Initialized && _coop.Activity.Get(_world.Local) != null;
    private static bool Bypass => NativeProjection.Active || !NativePcLifecycle.InCoopWorkshop;
    private void Notice(string text) => _coop.Notice(text.Replace(Handler.Name + ": ", ""));

    /// <summary>The label over the other player's capsule while they use a table, or null.</summary>
    internal string LabelOf(ActorSlot actor) => Handler.Of(actor) is { } u
        ? u.Tool switch { BenchTool.Delid => "Delidding", BenchTool.WaterCooling => "Water cooling", _ => "Case modding" } : null;

    /// <summary>The bench key of a tool table (its own bench slot, or the table itself).</summary>
    internal static string Key(BenchToolSlot table)
    {
        var key = table.Slot != null ? "tool:" + NativeObjectState.BenchKey(table.Slot) : "tool:" + NativeObjectState.PathOf(table.transform);
        return key.Length <= 400 ? key : "tool:" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>Why this player can't touch the PC on that tool table now (pick it up or drop onto it), or null.</summary>
    internal string BusyTable(BenchToolSlot table) => table == null || Bypass ? null : Handler.Busy(Key(table), _world.Local);

    /// <summary>The same for a bench slot that belongs to a tool table (a drop onto it), or null.</summary>
    internal string BusyBench(BenchSlot slot)
    {
        if (slot == null || Bypass || !Handler.Uses.Any()) return null;
        foreach (var table in UnityEngine.Object.FindObjectsOfType<BenchToolSlot>())
            if (table != null && table.Slot != null && table.Slot.Pointer == slot.Pointer) return BusyTable(table);
        return null;
    }

    private static (BenchToolSlot Table, BenchTool Tool) Table(State state)
    {
        if (state.TryCast<DeliddingState>() is { } delid) return (delid.BenchToolSlot, BenchTool.Delid);
        if (state.TryCast<WaterCoolingState>() is { } water) return (water.m_BenchToolSlot, BenchTool.WaterCooling);
        if (state.TryCast<CaseModdingState>() is { } mod) return (mod.ToolSlot, BenchTool.CaseModding);
        return (null, default);
    }

    // ---------------- entering and leaving a table ----------------

    /// <summary>A game state was just entered (postfix of <c>GameController.SetCurrentState</c>).</summary>
    internal void Entered(State state)
    {
        if (state == null || Bypass) return;
        var (table, tool) = Table(state);
        if (table == null && state.TryCast<DeliddingState>() == null && state.TryCast<WaterCoolingState>() == null && state.TryCast<CaseModdingState>() == null) return;
        if (_session != null && _session.State.Pointer == state.Pointer) return;
        var key = table == null ? null : Key(table);
        var why = key == null ? "That tool table isn't known." : !Ready ? "The shared world isn't ready on this game yet."
            : Handler.Busy(key, _world.Local) ?? (_session != null ? "Your last tool table session is still being shared." : null);
        if (why != null) { Notice(why); StateExit.Leave(state, closeChildren: true); return; }
        _session = new Session
        {
            State = state, Table = table, Bench = key, Tool = tool,
            Before = tool == BenchTool.CaseModding ? null : Inventory(),
            DesignBefore = tool == BenchTool.CaseModding ? DesignHash(table) : null,
            Pc = tool == BenchTool.CaseModding ? PcOn(table).Id : default,
        };
        _submittingUse = true;
        try
        {
            if (!_world.Submit(CommandTypes.ToolBench, ToolBenchWorld.Use(_coop.Activity.Get(_world.Local)!.Epoch, key, tool), out var op))
            {
                Notice("Not connected to the host's world.");
                _session = null; StateExit.Leave(state, closeChildren: true);
                return;
            }
            if (_session != null) _session.Use = op;
        }
        finally { _submittingUse = false; }
        if (_session != null) Plugin.Logger.LogInfo($"[tools] {ToolBenchWorld.ToolName(tool)} table {key}: session started here.");
    }

    private void OnRejected(OperationId op, RejectReason reason, string text)
    {
        if (op.Actor != _world.Local) return;
        if (SwitchRejected(op, text)) return;
        var s = _session;
        if (s != null && (_submittingUse || s.Use == op))
        {
            Notice(text);
            _session = null;
            StateExit.Leave(s.State, closeChildren: true);
            Plugin.Logger.LogInfo($"[tools] table {s.Bench}: refused ({text}).");
            return;
        }
        if (text.Contains(Handler.Name)) _coop.ProjectionFailed("The host refused this game's tool table result", new InvalidDataException(text));
    }

    private void Destroyed(State state)
    {
        if (_session != null && state != null && _session.State.Pointer == state.Pointer) _session.Destroyed = true;
    }

    /// <summary>Host: a guest's snapshot is being taken. The guest's world will hold this game's inventory as it is now,
    /// so this player's running session shares only what changes from here, and announces itself again.</summary>
    internal void Snapshotted()
    {
        if (_session == null) return;
        if (_session.Tool == BenchTool.CaseModding) _session.DesignBefore = null; // the guest gets this design as it is now
        else _session.Before = Inventory();
        _announce = true;
    }

    public void Update()
    {
        StateExit.Update();
        if (!_world.Admitted) return;
        var s = _session;
        if (s == null) return;
        var now = Time.realtimeSinceStartup;
        if (_announce && Handler.Of(_world.Local)?.Bench == s.Bench)
        {
            _announce = false;
            if (_coop.Activity.Get(_world.Local) is { } a) _world.Submit(CommandTypes.ToolBench, ToolBenchWorld.Use(a.Epoch, s.Bench, s.Tool), out _);
        }
        if (!s.Destroyed && StateExit.InStack(GameController.Get(), s.State.Pointer)) return;
        if (s.GoneAt == 0) { s.GoneAt = now; return; }
        if (now - s.GoneAt < Quiet) return;
        // A guest's Use may still be on its way: the leave has to come after it.
        if (Handler.Of(_world.Local)?.Bench != s.Bench)
        {
            if (now - s.GoneAt < UseWait) return;
            _session = null;
            _coop.ProjectionFailed($"This game's {ToolBenchWorld.ToolName(s.Tool)} table session could not be shared", new TimeoutException("the host never confirmed the table"));
            return;
        }
        _session = null;
        byte[] result;
        try { result = s.Tool == BenchTool.CaseModding ? DesignResult(s) : Result(s.Before, Inventory()); }
        catch (Exception e)
        {
            result = null;
            _coop.ProjectionFailed($"This game's {ToolBenchWorld.ToolName(s.Tool)} table result could not be read", e);
        }
        var actor = _coop.Activity.Get(_world.Local);
        if (actor == null) return;
        var total = result == null ? 0 : (result.Length + ToolBenchWorld.MaxChunkBytes - 1) / ToolBenchWorld.MaxChunkBytes;
        for (var i = 0; i < total; i++)
        {
            var data = result.AsSpan(i * ToolBenchWorld.MaxChunkBytes, Math.Min(ToolBenchWorld.MaxChunkBytes, result.Length - i * ToolBenchWorld.MaxChunkBytes)).ToArray();
            _world.Submit(CommandTypes.ToolBench, ToolBenchWorld.Chunk(actor.Epoch, s.Bench, i, total, data), out _);
        }
        _world.Submit(CommandTypes.ToolBench, ToolBenchWorld.Leave(actor.Epoch, s.Bench, s.Tool, result), out _);
        Plugin.Logger.LogInfo($"[tools] {ToolBenchWorld.ToolName(s.Tool)} table {s.Bench}: session over; {(result == null ? "nothing changed" : $"result {result.Length} bytes")}.");
    }

    // ---------------- the inventory result ----------------

    internal static Dictionary<string, List<PartInstance>> Inventory()
    {
        var result = new Dictionary<string, List<PartInstance>>(StringComparer.Ordinal);
        var inventory = CareerStatus.Get()?.GetInventory() ?? throw new InvalidDataException("Inventory unavailable.");
        foreach (var p in inventory)
        {
            if (p == null) continue;
            var key = PartVariants.Key(p);
            if (!result.TryGetValue(key, out var list)) result[key] = list = new List<PartInstance>();
            list.Add(p);
        }
        return result;
    }

    /// <summary>What changed between two inventories: the kinds of item that went (by key) and the items that came
    /// (whole), compressed; null when nothing changed.</summary>
    private static byte[] Result(Dictionary<string, List<PartInstance>> before, Dictionary<string, List<PartInstance>> after)
    {
        var w = InventoryChange(before, after);
        if (w == null) return null;
        using var output = new MemoryStream();
        using (var zip = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, true)) zip.Write(w.Data, 0, w.Length);
        if (output.Length > ToolBenchWorld.MaxBytes) throw new InvalidDataException("the table result is too large to share");
        return output.ToArray();
    }

    /// <summary>The inventory change, uncompressed, or null when nothing changed (also used by the Cable app's purchases).</summary>
    internal static NetDataWriter InventoryChange(Dictionary<string, List<PartInstance>> before, Dictionary<string, List<PartInstance>> after)
    {
        var removed = new List<string>(); var added = new List<byte[]>();
        foreach (var (key, list) in before)
        {
            var now = after.TryGetValue(key, out var a) ? a.Count : 0;
            for (var i = now; i < list.Count; i++) removed.Add(key);
        }
        foreach (var (key, list) in after)
        {
            var had = before.TryGetValue(key, out var b) ? b : new List<PartInstance>();
            if (list.Count <= had.Count) continue;
            // Prefer items that weren't there before (a new object, or one the table changed in place).
            var old = had.Select(x => x.Pointer).ToHashSet();
            var fresh = list.Where(x => !old.Contains(x.Pointer)).Concat(list.Where(x => old.Contains(x.Pointer))).Take(list.Count - had.Count);
            foreach (var item in fresh) added.Add(PartVariants.Write(item));
        }
        if (removed.Count == 0 && added.Count == 0) return null;
        if (removed.Count > MaxItems || added.Count > MaxItems || added.Any(x => x.Length > MaxItemBytes))
            throw new InvalidDataException($"the table changed too much of the inventory ({removed.Count} items out, {added.Count} in)");
        var w = new NetDataWriter();
        w.Put(removed.Count); foreach (var k in removed) w.Put(k);
        w.Put(added.Count); foreach (var x in added) { w.Put(x.Length); w.Put(x); }
        Plugin.Logger.LogInfo($"[tools] inventory change: out [{string.Join(", ", removed)}], in {added.Count} item(s).");
        return w;
    }

    /// <summary>Decompresses a shared result, refusing one that expands beyond <paramref name="limit"/>.</summary>
    internal static byte[] Inflate(byte[] compressed, long limit)
    {
        using var input = new MemoryStream(compressed);
        using var zip = new DeflateStream(input, CompressionMode.Decompress);
        using var raw = new MemoryStream(); var chunk = new byte[16384]; int read;
        while ((read = zip.Read(chunk, 0, chunk.Length)) != 0)
        {
            if (raw.Length + read > limit) throw new InvalidDataException("The shared result expands beyond limits.");
            raw.Write(chunk, 0, read);
        }
        return raw.ToArray();
    }

    internal static byte[] Deflate(byte[] data, int length)
    {
        using var output = new MemoryStream();
        using (var zip = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, true)) zip.Write(data, 0, length);
        return output.ToArray();
    }

    private static void Apply(byte[] compressed) => ApplyInventoryChange(new NetDataReader(Inflate(compressed, 2 * MaxItems * MaxItemBytes)));

    /// <summary>Makes the same inventory change as another game (<see cref="InventoryChange"/>).</summary>
    internal static void ApplyInventoryChange(NetDataReader r)
    {
        var removed = new List<string>(); var added = new List<PartInstance>();
        var n = r.GetInt(); if (n is < 0 or > MaxItems) throw new InvalidDataException("Invalid table result.");
        for (var i = 0; i < n; i++) removed.Add(r.GetString(4096));
        n = r.GetInt(); if (n is < 0 or > MaxItems) throw new InvalidDataException("Invalid table result.");
        for (var i = 0; i < n; i++)
        {
            var length = r.GetInt(); if (length is < 1 or > MaxItemBytes) throw new InvalidDataException("Invalid table item.");
            var bytes = new byte[length]; r.GetBytes(bytes, length);
            added.Add(PartVariants.Read(bytes));
        }
        var status = CareerStatus.Get() ?? throw new InvalidDataException("Inventory unavailable.");
        foreach (var key in removed)
        {
            PartInstance match = null;
            foreach (var p in status.GetInventory()) if (p != null && PartVariants.Key(p) == key) { match = p; break; }
            if (match == null) { Plugin.Logger.LogWarning($"[tools] the other player's table took a {key} this inventory doesn't have."); continue; }
            NativeProjection.Run(() => status.RemoveItemFromInventory(match));
        }
        foreach (var part in added) NativeProjection.Run(() => status.AddItemToInventory(part, false, false, true, false));
        Plugin.Logger.LogInfo($"[tools] the other player's change is in this inventory: out [{string.Join(", ", removed)}], in [{string.Join(", ", added.Select(PartVariants.Key))}].");
    }

    // ---------------- IToolBenchNative ----------------

    public void Used(in MutationContext ctx, BenchUse use)
    {
        if (!ctx.FromLocalPlayer) Plugin.Logger.LogInfo($"[tools] the other player uses the {ToolBenchWorld.ToolName(use.Tool)} table {use.Bench}.");
    }

    public void Left(in MutationContext ctx, BenchUse use, byte[] result, int design)
    {
        if (use.Tool == BenchTool.CaseModding) { DesignLeft(ctx, use, result, design); return; }
        if (ctx.FromLocalPlayer) return;
        Plugin.Logger.LogInfo($"[tools] the other player left the {ToolBenchWorld.ToolName(use.Tool)} table {use.Bench}{(result == null ? "" : " with a result")}.");
        if (result == null) return;
        try { Apply(result); }
        catch (Exception e) { _coop.ProjectionFailed("The other player's tool table result could not be shown on this game", e); }
    }

    // ---------------- hooks ----------------

    [HarmonyPatch(typeof(DeliddingState), nameof(DeliddingState.OnStateDestroy))]
    internal static class DelidDestroyTrace
    {
        private static void Postfix(DeliddingState __instance)
        {
            try { Current?.Destroyed(__instance); }
            catch (Exception e) { Plugin.Logger.LogError("[tools] delid trace failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(CaseModdingState), nameof(CaseModdingState.OnStateDestroy))]
    internal static class CaseModDestroyTrace
    {
        private static void Postfix(CaseModdingState __instance)
        {
            try { Current?.Destroyed(__instance); }
            catch (Exception e) { Plugin.Logger.LogError("[tools] case-modding trace failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(WaterCoolingState), nameof(WaterCoolingState.OnStateDestroy))]
    internal static class WaterDestroyTrace
    {
        private static void Postfix(WaterCoolingState __instance)
        {
            try { Current?.Destroyed(__instance); }
            catch (Exception e) { Plugin.Logger.LogError("[tools] water table trace failed: " + e); }
        }
    }
}

/// <summary>What makes an inventory item differ from the plain part, in the terms the game compares items with
/// (<c>PartInstance.IsSameAs</c>: the CPU's delid flag, water blocks, motherboard and radiator extras).</summary>
internal static class PartVariants
{
    internal static string Of(PartInstance p)
    {
        if (p == null) return "";
        var parts = new List<string>();
        if (p.TryCast<CpuPartInstance>() is { m_Delidded: true }) parts.Add("delidded");
        if (p.TryCast<GpuPartInstance>()?.WaterCoolBlock is { } gpuBlock) parts.Add("block " + gpuBlock.GetPartId());
        if (p.TryCast<RamPartInstance>()?.WaterCoolBlock is { } ramBlock) parts.Add("block " + ramBlock.GetPartId());
        if (p.TryCast<MotherboardPartInstance>() is { } board)
        {
            if (board.WaterCoolBlock != null) parts.Add("block " + board.WaterCoolBlock.GetPartId());
            if (board.CPUInstance != null) parts.Add("cpu " + board.CPUInstance.GetPartId());
            if (board.VrmCoversRemoved) parts.Add("vrm covers off");
            if (board.BenchWaterCooled) parts.Add("bench water-cooled");
        }
        if (p.TryCast<RadiatorPartInstance>()?.FanId is { Count: > 0 } fans)
        {
            var ids = new List<string>(); foreach (var f in fans) ids.Add(f?.GetPartId() ?? "-");
            parts.Add("fans " + string.Join(",", ids));
        }
        var text = string.Join("; ", parts);
        return text.Length <= ComponentEditWorld.MaxText ? text : text[..ComponentEditWorld.MaxText];
    }

    internal static string Key(PartInstance p) => p.GetIl2CppType().FullName + ":" + p.GetPartId() + "|" + Of(p);

    internal static byte[] Write(PartInstance part)
    {
        var stream = new Il2CppSystem.IO.MemoryStream();
        try
        {
            new Il2CppSystem.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Serialize(stream, part);
            return stream.ToArray();
        }
        finally { stream.Dispose(); }
    }

    internal static PartInstance Read(byte[] bytes)
    {
        var stream = new Il2CppSystem.IO.MemoryStream(bytes);
        try
        {
            var part = new Il2CppSystem.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Deserialize(stream).TryCast<PartInstance>();
            if (part?.GetPart() == null || stream.Position != stream.Length) throw new InvalidDataException("Invalid part item.");
            return part;
        }
        finally { stream.Dispose(); }
    }

    /// <summary>A separate item equal to <paramref name="part"/> (its own water block and fans too).</summary>
    internal static PartInstance Copy(PartInstance part) => Read(Write(part));
}

/// <summary>Closes a game state this player just opened, on the next frame (not from inside <c>SetCurrentState</c>).</summary>
internal static class StateExit
{
    private static readonly List<(IntPtr State, float At, bool CloseChildren)> s_leave = new();

    internal static void Leave(State state, bool closeChildren = false)
    {
        if (state != null && s_leave.All(x => x.State != state.Pointer))
            s_leave.Add((state.Pointer, Time.realtimeSinceStartup, closeChildren));
    }

    internal static bool InStack(GameController ctrl, IntPtr state)
    {
        if (ctrl == null) return false;
        if (ctrl.GetCurrentState()?.Pointer == state) return true;
        if (ctrl.m_previousState != null)
            foreach (var previous in ctrl.m_previousState)
                if (previous != null && previous.Pointer == state) return true;
        return false;
    }

    internal static void Update()
    {
        if (s_leave.Count == 0) return;
        var ctrl = GameController.Get();
        foreach (var item in s_leave.ToArray())
        {
            var current = ctrl?.GetCurrentState();
            var atTool = current?.Pointer == item.State;
            if (atTool || (item.CloseChildren && current != null && InStack(ctrl, item.State)))
            {
                // Delidding opens its CPU inventory before the host can answer. Close each child through the native
                // state stack until the refused tool itself exits. Other active tools are left alone.
                if (atTool) s_leave.Remove(item);
                try { ctrl.GoToPreviousState(); }
                catch (Exception e)
                {
                    s_leave.Remove(item);
                    Plugin.Logger.LogError("[tools] leaving a refused tool failed: " + e);
                }
            }
            else if (Time.realtimeSinceStartup - item.At > 3f) s_leave.Remove(item);
        }
    }
}

using HarmonyLib;
using PCBS2Coop.Network;
using UnityEngine;

namespace PCBS2Coop;

/// <summary>
/// Cable and power edits (phase 5b-5c, owner decisions October 27, 2026). The gestures are the cabling-mode long press
/// on a connector (<c>&lt;CablingTick&gt;b__0</c>, which opens <c>ConnectCableState</c> and may install a splitter), the
/// unplug long press (<c>&lt;RemoveCableTick&gt;b__0</c>) and the power buttons (<c>PowerButton.Press</c>, the work HUD's
/// <c>PowerClicked</c>). Each is a whole-PC edit in command 9. The editor's game runs the game's own gesture and is
/// followed until nothing moves on that PC. The commit carries what changed (cables, splitters/adapters, inventory, power),
/// and the other game applies that change with the game's own calls: press, <c>Plug.Unplug</c>, the splitter install,
/// <c>Case.CreateCableAsync</c>. The host settles as for part edits. The game doesn't save power, so after a load each game
/// turns on the PCs the shared world says are on.
/// </summary>
internal sealed partial class NativeComponentEdits
{
    /// <summary>The wiring of one PC at a moment: cables by key, splitter/adapter slots, power and the inventory.</summary>
    internal sealed class WireState
    {
        public readonly Dictionary<string, CableInstance> Cables = new(StringComparer.Ordinal);
        public string[] Splitters = Array.Empty<string>(), Adapters = Array.Empty<string>();
        public bool Powered;
        public Dictionary<string, int> Inventory = new(StringComparer.Ordinal);
    }

    /// <summary>The other player's cable or power edit being shown on this game.</summary>
    private sealed class WireRun
    {
        public ComponentEdit Edit;
        public NetObjectId Pc;
        public Case Mesh;
        public IEnumerator<bool> Steps;
        public readonly List<string> Errors = new();
    }

    private readonly List<WireRun> _wireRuns = new();
    /// <summary>Power restores already tried (PC, wanted state), so a press that fails here isn't repeated each frame.</summary>
    private readonly HashSet<(NetObjectId, bool)> _powerTried = new();
    private float _worldSeen = -1;
    private const float QuietSeconds = 1f, ConnectTimeout = 600f, StepTimeout = 10f, PowerDelaySeconds = 8f, RestoreDelaySeconds = 4f, RestoreWindowSeconds = 90f;

    private void ResetWiring() { _wireRuns.Clear(); _powerTried.Clear(); _worldSeen = -1; }

    // ---------------- native reads ----------------

    private static bool Running(Case mesh)
    {
        try { return mesh != null && mesh.GetMotherboard()?.GetComputerRunning() == true; }
        catch (Exception) { return false; }
    }

    private static string Key(CableInstance c) => c?.m_start == null || c.m_end == null ? null : Wiring.Key(c.m_start.id, c.m_end.id);

    /// <summary>The cables of a PC, internal and external (a cable to the bench is not a child of the case).</summary>
    private static IEnumerable<CableInstance> CablesOf(Case mesh)
    {
        if (mesh == null) yield break;
        foreach (var c in UnityEngine.Object.FindObjectsOfType<CableInstance>())
            if (c != null && c.m_case != null && c.m_case.Pointer == mesh.Pointer) yield return c;
    }

    private static CableInstance FindCable(Case mesh, string key) => CablesOf(mesh).FirstOrDefault(c => Key(c) == key);

    private static string Sku(Slot slot)
    {
        var comp = slot == null ? null : slot.GetComponent();
        if (comp == null || NativeAssembly.IsPreview(comp)) return "";
        return comp.GetPartInstance()?.GetPartId() ?? "";
    }

    private static string[] Skus(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Slot> slots)
    {
        if (slots == null) return Array.Empty<string>();
        var result = new string[Math.Min(slots.Length, Wiring.MaxIndex)];
        for (var i = 0; i < result.Length; i++) result[i] = Sku(slots[i]);
        return result;
    }

    private static Dictionary<string, int> InventoryCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var inventory = CareerStatus.Get()?.GetInventory();
        if (inventory != null)
            foreach (var p in inventory)
                if (p?.GetPartId() is { Length: > 0 } sku) counts[sku] = counts.GetValueOrDefault(sku) + 1;
        return counts;
    }

    internal static WireState Snapshot(Case mesh)
    {
        var state = new WireState { Powered = Running(mesh), Inventory = InventoryCounts() };
        if (mesh == null) return state;
        foreach (var c in CablesOf(mesh))
            if (Key(c) is { } key && !state.Cables.ContainsKey(key)) state.Cables.Add(key, c);
        state.Splitters = Skus(mesh.psuSplitterSlots); state.Adapters = Skus(mesh.powerAdapterSlots);
        return state;
    }

    private static CableFact Fact(CableInstance c)
    {
        if (c.m_usingCableLay) Plugin.Logger.LogWarning($"[cables] cable {Key(c)} uses a hand-laid route; the other game shows the game's default route.");
        return new(c.m_start.id, c.m_end.id, c.m_desc?.m_id ?? "", c.m_connectorDesc?.m_id ?? "", c.m_isNonModularConnector, c.m_playerChoice,
            c.m_pinMaterialIndices?.ToArray() ?? Array.Empty<int>(), Array.Empty<float>());
    }

    /// <summary>What changed on a PC between two snapshots.</summary>
    private static Wiring Diff(WireState before, WireState after)
    {
        var removed = before.Cables.Keys.Where(k => !after.Cables.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var added = after.Cables.Where(kv => !before.Cables.ContainsKey(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => Fact(kv.Value)).ToList();
        var accessories = new List<AccessoryChange>();
        void Compare(AccessoryKind kind, string[] a, string[] b)
        {
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var was = i < a.Length ? a[i] : ""; var now = i < b.Length ? b[i] : "";
                if (was != now) accessories.Add(new(kind, i, now));
            }
        }
        Compare(AccessoryKind.Splitter, before.Splitters, after.Splitters);
        Compare(AccessoryKind.Adapter, before.Adapters, after.Adapters);
        var inventory = before.Inventory.Keys.Union(after.Inventory.Keys)
            .Select(sku => new InventoryChange(sku, after.Inventory.GetValueOrDefault(sku) - before.Inventory.GetValueOrDefault(sku)))
            .Where(c => c.Count != 0).OrderBy(c => c.Sku, StringComparer.Ordinal).ToList();
        if (removed.Count > Wiring.MaxCables || added.Count > Wiring.MaxCables || accessories.Count > Wiring.MaxAccessories || inventory.Count > Wiring.MaxInventory ||
            inventory.Any(c => Math.Abs(c.Count) > Wiring.MaxCount))
            throw new InvalidDataException($"the change is too large to share ({removed.Count} cables off, {added.Count} on, {accessories.Count} accessories, {inventory.Count} inventory lines)");
        return new(removed, added, accessories, inventory, after.Powered);
    }

    /// <summary>A cable is animating or loading on that PC, or its power button is moving.</summary>
    private static bool CablesBusy(Case mesh)
    {
        var db = PartsDatabase.s_instance;
        if ((db?.m_loadCableOperations?.Count ?? 0) + (db?.m_loadPlugOperations?.Count ?? 0) > 0) return true;
        foreach (var c in CablesOf(mesh)) if (c.Installing || c.Uninstalling) return true;
        var button = mesh == null ? null : mesh.GetComponentInChildren<PowerButton>(true);
        return button != null && button.animationRunning;
    }

    /// <summary>Host stability: a cable edit or power press is still moving there, or being shown.</summary>
    private bool WireMoving(Case mesh) => CablesBusy(mesh) || _wireRuns.Any(r => r.Mesh != null && r.Mesh.Pointer == mesh.Pointer);

    private static PowerConnector FindConnector(Case mesh, BenchSlot bench, string id)
    {
        if (mesh != null)
            foreach (var c in mesh.GetComponentsInChildren<PowerConnector>(true)) if (c != null && c.id == id) return c;
        if (bench != null)
            foreach (var c in bench.GetComponentsInChildren<PowerConnector>(true)) if (c != null && c.id == id) return c;
        // An external end (a monitor or the mains at the workstation): only when that ID is unique in the scene.
        var found = UnityEngine.Object.FindObjectsOfType<PowerConnector>().Where(c => c != null && c.id == id).Take(2).ToArray();
        return found.Length == 1 ? found[0] : null;
    }

    // ---------------- requests (local input) ----------------

    private (PcFact Pc, string Why) WiredPc(Case mesh)
    {
        var id = mesh == null ? default : _coop.Objects.IdOf(mesh);
        var pc = id.IsNone ? null : _coop.Objects.Model.Pcs.SingleOrDefault(p => p.Id == id);
        if (pc == null) return (null, "That PC isn't in the shared world.");
        if (Handler.Busy(pc.Id, _world.Local) is { } busy) return (null, busy);
        return (pc, null);
    }

    private bool SubmitWired(Case mesh, EditGesture gesture, string part, bool powerOn, System.Action replay, string what)
    {
        var (pc, why) = WiredPc(mesh);
        if (why != null) { Refuse(why); return false; }
        if (string.IsNullOrEmpty(part)) { Refuse("That connector isn't known."); return false; }
        return Submit(new Request { Pc = pc.Id, Gesture = gesture, Sku = part, PowerOn = powerOn, Replay = replay, Work = CurrentState, Mesh = mesh },
            ComponentEditWorld.Begin(_coop.Activity.Get(_world.Local)!.Epoch, pc.Id, default, 0, gesture, part, default), what);
    }

    internal bool RequestConnect(WorkingOnComputerState work, PowerConnector from, System.Action replay) =>
        SubmitWired(work?.m_case, EditGesture.Connect, from?.id, false, replay, $"cable from {from?.id}");

    internal bool RequestUnplug(WorkingOnComputerState work, Plug plug, System.Action replay)
    {
        var mesh = work?.m_case;
        var cable = CablesOf(mesh).FirstOrDefault(c => (c.m_plugs != null && c.m_plugs.Any(x => x != null && x.Pointer == plug.Pointer)) || c.GetCableEnd()?.Pointer == plug.Pointer)
            ?? plug.GetComponentInParent<CableInstance>();
        if (Key(cable) is not { } key) { Refuse("That cable isn't known."); return false; }
        return SubmitWired(mesh, EditGesture.Unplug, key, false, replay, $"unplugging {key}");
    }

    internal bool RequestPower(Case mesh, System.Action replay)
    {
        var on = !Running(mesh);
        return SubmitWired(mesh, EditGesture.Power, on ? "on" : "off", on, replay, on ? "power on" : "power off");
    }

    // ---------------- host check ----------------

    private string CheckWiredHost(PcFact pc, ComponentEdit edit)
    {
        if (!_coop.Objects.Projections.TryGet(pc.Id, out var mesh) || mesh == null) return "That PC isn't shown on the host's game.";
        if (CoopController.Instance?.Replays.Running(mesh) == true || _wireRuns.Any(r => r.Pc == pc.Id)) return "The host's game is still showing the previous change.";
        switch (edit.Gesture)
        {
            case EditGesture.Connect:
                return FindConnector(mesh, _coop.Objects.FindBench(pc.Id), edit.Part) == null ? "That connector is missing on the host's game." : null;
            case EditGesture.Unplug:
                return FindCable(mesh, edit.Part) == null ? "The host's game doesn't show that cable." : null;
            default:
                var on = edit.Part == "on";
                return Running(mesh) == on ? $"The host's game already shows that PC {(on ? "on" : "off")}." : null;
        }
    }

    // ---------------- following this player's own edit ----------------

    private void PollWired()
    {
        var l = _local; var mesh = l.Request.Mesh; var now = Time.realtimeSinceStartup;
        if (mesh == null)
        {
            _local = null; _cancel.Add(l.Token);
            Plugin.Logger.LogWarning($"[cables] edit {l.Token}: the PC is gone here; cancelling.");
            return;
        }
        var choosing = CurrentState?.TryCast<ConnectCableState>() != null;
        // While the player chooses where the cable goes, the edit is alive (up to ten minutes).
        if (choosing && now - l.Started < ConnectTimeout) l.Since = now;
        var busy = choosing || CablesBusy(mesh) ||
            (l.Request.Gesture == EditGesture.Power && Running(mesh) != l.Request.PowerOn && now - l.Started < PowerDelaySeconds);
        if (busy) { l.QuietSince = 0; return; }
        if (l.QuietSince == 0) { l.QuietSince = now; return; }
        if (now - l.QuietSince < QuietSeconds) return;
        Wiring wiring;
        try { wiring = Diff(l.Before, Snapshot(mesh)); }
        catch (Exception e)
        {
            _local = null; _cancel.Add(l.Token);
            _coop.ProjectionFailed($"This game's cable edit {l.Token} could not be shared", e);
            return;
        }
        if (wiring.Empty(l.Before.Powered))
        {
            Plugin.Logger.LogInfo($"[cables] edit {l.Token} ({l.Request.Gesture}) changed nothing; cancelling.");
            _local = null; _cancel.Add(l.Token);
            return;
        }
        l.Result = EditResult.Wired(wiring);
        l.Done = true;
        Plugin.Logger.LogInfo($"[cables] edit {l.Token} ({l.Request.Gesture}) finished here: off [{string.Join(", ", wiring.Removed)}], " +
            $"on [{string.Join(", ", wiring.Added.Select(c => c.Key))}], accessories {wiring.Accessories.Count}, inventory {wiring.Inventory.Count}, powered {wiring.Powered}.");
    }

    // ---------------- showing the other player's edit ----------------

    private void StartWireReplay(PcFact pc, ComponentEdit edit)
    {
        if (!_coop.Objects.Projections.TryGet(pc.Id, out var mesh) || mesh == null) throw new InvalidDataException("the PC is not shown on this game");
        var run = new WireRun { Edit = edit, Pc = pc.Id, Mesh = mesh };
        run.Steps = Replay(run, _coop.Objects.FindBench(pc.Id));
        _wireRuns.Add(run);
        Plugin.Logger.LogInfo($"[cables] showing {edit.Gesture} {edit.Token} on PC {pc.Id}.");
    }

    private void UpdateWireRuns()
    {
        foreach (var run in _wireRuns.ToArray())
        {
            bool more;
            try { more = run.Mesh != null && run.Steps.MoveNext(); }
            catch (Exception e) { run.Errors.Add(e.Message); more = false; }
            if (run.Mesh == null) run.Errors.Add("the PC is gone");
            if (more) continue;
            _wireRuns.Remove(run);
            var what = $"{run.Edit.Gesture} {run.Edit.Token} on PC {run.Pc}";
            if (run.Errors.Count > 0)
                _coop.ProjectionFailed($"The other player's {what} was accepted, but this game could not show it all", new InvalidDataException(string.Join("; ", run.Errors)));
            else Plugin.Logger.LogInfo($"[cables] {what}: shown here.");
            if (_world.IsAuthority) _ready.Add(run.Edit.Token);
        }
    }

    /// <summary>Wait (one frame at a time) until <paramref name="done"/> or the timeout; false when it timed out.</summary>
    private static IEnumerable<bool> Wait(Func<bool> done, float seconds)
    {
        var until = Time.realtimeSinceStartup + seconds;
        while (!done())
        {
            if (Time.realtimeSinceStartup > until) yield break;
            yield return true;
        }
    }

    private IEnumerator<bool> Replay(WireRun run, BenchSlot bench)
    {
        var mesh = run.Mesh; var w = run.Edit.Result!.Wiring!; var start = Snapshot(mesh);
        yield return true;

        // 1. The power button, as the editor pressed it (TurnOn may connect the bench's external cables itself).
        if (run.Edit.Gesture == EditGesture.Power && Running(mesh) != w.Powered)
        {
            var button = mesh.GetComponentInChildren<PowerButton>(true);
            NativeProjection.Run(() =>
            {
                if (button != null) button.Press();
                else if (w.Powered) mesh.GetMotherboard()?.TurnOn();
                else mesh.GetMotherboard()?.TurnOff(false);
            });
            foreach (var s in Wait(() => !CablesBusy(mesh) && Running(mesh) == w.Powered, PowerDelaySeconds)) yield return s;
            if (Running(mesh) != w.Powered) run.Errors.Add($"the PC didn't turn {(w.Powered ? "on" : "off")} here");
        }

        // 2. Cables that came off: the game's own unplug (RemoveCableTick's callback).
        foreach (var key in w.Removed)
        {
            var cable = FindCable(mesh, key);
            if (cable == null) continue; // already off here (the power press or a splitter did it)
            var plug = cable.GetCableEnd() ?? (cable.m_plugs is { Length: > 0 } plugs ? plugs[0] : null);
            NativeProjection.Run(() =>
            {
                if (plug != null) { mesh.OnChangePart(plug.gameObject, true, false); plug.Unplug(); }
                else { cable.Uninstall(false, true); }
            });
            foreach (var s in Wait(() => FindCable(mesh, key) == null, StepTimeout)) yield return s;
            if (FindCable(mesh, key) != null) run.Errors.Add($"cable {key} is still plugged in here");
        }

        // 3. Splitters and adapters (ConnectCableState.OnStateEnter's install sequence).
        foreach (var a in w.Accessories)
        {
            var slots = a.Kind == AccessoryKind.Splitter ? mesh.psuSplitterSlots : mesh.powerAdapterSlots;
            var slot = slots != null && a.Index < slots.Length ? slots[a.Index] : null;
            if (slot == null) { run.Errors.Add($"{a.Kind} slot {a.Index} is missing here"); continue; }
            var now = Sku(slot);
            if (now == a.Sku) continue;
            if (a.Sku == "" || now != "") { run.Errors.Add($"removing or swapping the {a.Kind} in slot {a.Index} isn't shown yet"); continue; }
            NativeProjection.Run(() =>
            {
                var part = PartInstance.NewPartInstance(a.Sku, 0, null) ?? throw new InvalidDataException($"part {a.Sku} is unknown");
                var comp = slot.InstantiatePart(part, false, false, false);
                comp?.GetComponent<PowerSplitter>()?.UpdateIDs(slot, true);
                if (a.Kind == AccessoryKind.Splitter) mesh.RefreshPowerSplitterSlots(); else mesh.RefreshPowerAdapterSlots();
            });
            yield return true;
            if (Sku(slot) != a.Sku) run.Errors.Add($"the {a.Kind} {a.Sku} didn't install in slot {a.Index} here");
        }

        // 4. Cables that went on: the game's own connect call (DoConnect), animated.
        foreach (var c in w.Added)
        {
            if (FindCable(mesh, c.Key) != null) continue; // the game already connected it here
            var from = FindConnector(mesh, bench, c.From); var to = FindConnector(mesh, bench, c.To);
            var desc = PartsDatabase.s_instance.GetDesc(c.Desc, null);
            var connector = c.Connector == "" ? null : PartsDatabase.s_instance.GetDesc(c.Connector, null);
            if (from == null || to == null || desc == null) { run.Errors.Add($"cable {c.Key}: {(from == null ? "start" : to == null ? "end" : "cable type")} unknown here"); continue; }
            var pins = c.Pins.Count == 0 ? null : new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(c.Pins.ToArray());
            NativeProjection.Run(() => mesh.CreateCableAsync(desc, from, to, connector, false, pins, null, true, null, null));
            foreach (var s in Wait(() => FindCable(mesh, c.Key) is { } made && !made.Installing && !CablesBusy(mesh), StepTimeout)) yield return s;
            if (FindCable(mesh, c.Key) == null) run.Errors.Add($"cable {c.Key} didn't connect here");
        }

        // 5. Inventory mirrors the editor's: whatever the game didn't already do here.
        var counts = InventoryCounts();
        foreach (var change in w.Inventory)
        {
            var mine = counts.GetValueOrDefault(change.Sku) - start.Inventory.GetValueOrDefault(change.Sku);
            var need = change.Count - mine;
            for (; need > 0; need--)
            {
                var part = PartInstance.NewPartInstance(change.Sku, 0, null);
                if (part == null) { run.Errors.Add($"inventory part {change.Sku} is unknown"); break; }
                NativeProjection.Run(() => CareerStatus.Get().AddItemToInventory(part, true, false, true, false));
            }
            for (; need < 0; need++)
            {
                PartInstance item = null;
                foreach (var p in CareerStatus.Get()?.GetInventory() ?? new Il2CppSystem.Collections.Generic.List<PartInstance>())
                    if (p != null && p.GetPartId() == change.Sku) { item = p; break; }
                if (item == null) { Plugin.Logger.LogWarning($"[cables] this player's inventory has no {change.Sku} to take."); break; }
                NativeProjection.Run(() => CareerStatus.Get().RemoveItemFromInventory(item));
            }
        }

        // 6. The same cables as the editor's game now (the start, minus what came off, plus what went on).
        var expected = start.Cables.Keys.Except(w.Removed, StringComparer.Ordinal).Union(w.Added.Select(c => c.Key), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        foreach (var s in Wait(() => !CablesBusy(mesh), StepTimeout)) yield return s;
        var shown = Snapshot(mesh).Cables.Keys.ToHashSet(StringComparer.Ordinal);
        if (run.Edit.Gesture != EditGesture.Power && !shown.SetEquals(expected))
            run.Errors.Add($"cables differ: missing [{string.Join(", ", expected.Except(shown))}], extra [{string.Join(", ", shown.Except(expected))}]");
        if (Running(mesh) != w.Powered && run.Edit.Gesture != EditGesture.Power)
            Plugin.Logger.LogWarning($"[cables] PC {run.Pc} is {(Running(mesh) ? "on" : "off")} here; the editor's is {(w.Powered ? "on" : "off")}.");
    }

    // ---------------- power after a load ----------------

    /// <summary>The game doesn't save power: shortly after this game's world loads (Continue, late join), turn on each PC
    /// on its bench that the shared world says is on. Only in that window: later, a PC that switched off by itself (a
    /// simulation shutdown, phases 6-7) is left alone. Tried once per PC, so a press that can't work here isn't repeated.</summary>
    private void UpdatePower()
    {
        if (!Active) { _worldSeen = -1; return; }
        var now = Time.realtimeSinceStartup;
        if (_worldSeen < 0) { _worldSeen = now; return; }
        var age = now - _worldSeen;
        if (age < RestoreDelaySeconds || age > RestoreWindowSeconds) return;
        if (Busy || NativeEditReplay.Active || CoopController.Instance?.Replays.Busy == true) return;
        foreach (var pc in _coop.Objects.Model.Pcs)
        {
            if (pc.Location.Place != PcPlace.Bench || !_coop.Objects.Projections.TryGet(pc.Id, out var mesh) || mesh == null) continue;
            var bench = _coop.Objects.FindBench(pc.Id);
            if (bench == null || bench.m_caseLoadingToSlot || CablesBusy(mesh)) continue;
            var running = Running(mesh);
            if (running == pc.Powered || !pc.Powered) continue;
            if (!_powerTried.Add((pc.Id, pc.Powered))) continue;
            Plugin.Logger.LogInfo($"[cables] PC {pc.Id} is {(running ? "on" : "off")} here; the shared world has it {(pc.Powered ? "on" : "off")}: turning it {(pc.Powered ? "on" : "off")}.");
            try
            {
                NativeProjection.Run(() =>
                {
                    var mb = mesh.GetMotherboard();
                    if (mb == null) return;
                    if (pc.Powered) mb.TurnOn(); else mb.TurnOff(false);
                });
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[cables] PC {pc.Id}: power change failed here: {e.Message}"); }
        }
    }

    // ---------------- interception ----------------

    private static bool WireBypass => NativeProjection.Active || !NativePcLifecycle.InCoopWorkshop;
    private static bool NotReady()
    {
        CoopController.Instance?.Notice("Refused: the shared world isn't ready on this game yet.");
        return false;
    }

    /// <summary>The cabling-mode long press on a connector (opens <c>ConnectCableState</c>, which may add a splitter).</summary>
    [HarmonyPatch(typeof(WorkingOnComputerState.__c__DisplayClass70_0), "_CablingTick_b__0")]
    internal static class ConnectIntercept
    {
        private static bool Prefix(WorkingOnComputerState.__c__DisplayClass70_0 __instance)
        {
            if (WireBypass) return true;
            try
            {
                if (Current == null) return NotReady();
                var closure = __instance;
                return Current.RequestConnect(closure.__4__this, closure.connector, () => closure._CablingTick_b__0());
            }
            catch (Exception e) { Plugin.Logger.LogError("[cables] connect request failed: " + e); return false; }
        }
    }

    /// <summary>The unplug long press on a plug.</summary>
    [HarmonyPatch(typeof(WorkingOnComputerState.__c__DisplayClass67_0), "_RemoveCableTick_b__0")]
    internal static class UnplugIntercept
    {
        private static bool Prefix(WorkingOnComputerState.__c__DisplayClass67_0 __instance)
        {
            if (WireBypass) return true;
            try
            {
                if (Current == null) return NotReady();
                var closure = __instance;
                if (closure.cableEnd == null) return true;
                return Current.RequestUnplug(closure.__4__this, closure.cableEnd, () => closure._RemoveCableTick_b__0());
            }
            catch (Exception e) { Plugin.Logger.LogError("[cables] unplug request failed: " + e); return false; }
        }
    }

    /// <summary>The case's power button (work mode and the LED-strip tool press it).</summary>
    [HarmonyPatch(typeof(PowerButton), nameof(PowerButton.Press))]
    internal static class PowerButtonIntercept
    {
        private static bool Prefix(PowerButton __instance)
        {
            if (WireBypass) return true;
            try
            {
                if (Current == null) return NotReady();
                var button = __instance;
                var mesh = button.m_case ?? button.GetComponentInParent<Case>();
                return Current.RequestPower(mesh, () => button.Press());
            }
            catch (Exception e) { Plugin.Logger.LogError("[cables] power request failed: " + e); return false; }
        }
    }

    /// <summary>The work HUD's power button.</summary>
    [HarmonyPatch(typeof(WorkingOnComputerStateBase), nameof(WorkingOnComputerStateBase.PowerClicked))]
    internal static class PowerClickIntercept
    {
        private static bool Prefix(WorkingOnComputerStateBase __instance)
        {
            if (WireBypass) return true;
            try
            {
                if (Current == null) return NotReady();
                var state = __instance;
                return Current.RequestPower(state.m_case, () => state.PowerClicked());
            }
            catch (Exception e) { Plugin.Logger.LogError("[cables] power request failed: " + e); return false; }
        }
    }
}

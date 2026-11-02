using System.Security.Cryptography;
using HarmonyLib;
using LiteNetLib.Utils;
using PCBS2Coop.Network;
using UnityEngine;

namespace PCBS2Coop;

/// <summary>
/// Phase 11 (owner decisions, November 2, 2026) outside the tool tables:
/// <list type="bullet">
/// <item><b>Lighting app</b> (decision 2): the game applies every control at once (<c>LightingApp.OnApply</c>); after a
/// short pause this player's game sends the PC's light settings, presets and peripheral lights, and the other game copies
/// them into its own light objects, so its lights change in place with no restart. The colour wheel reads the mouse
/// itself, which phase 6 doesn't mirror, so only the game of the player at that monitor sends.</item>
/// <item><b>Opening Lighting</b>: on its first open the app pushes its own view state whoever is at the monitor; when it
/// opens from the other player's mirrored click (or for nobody here), that push and its camera claim are skipped.</item>
/// <item><b>Effect clock</b> (decision 3): each game's light clock starts at its own power-on; the host sends its clock and
/// the guest keeps its own in step.</item>
/// <item><b>Cable app</b> (decision 4): a bought cable set adds new part kinds and inventory items; the buyer's game sends
/// both, and the other game adds them (the mirrored buy click itself does nothing there).</item>
/// </list>
/// </summary>
internal sealed class NativeCustomization : ICustomizationNative
{
    private const float LightsSettle = 0.5f, ClockEvery = 30f, ClockTolerance = 0.15f;

    private readonly CoopController _coop;
    private readonly WorldSync _world;
    private readonly Dictionary<NetObjectId, float> _lightSends = new();
    private readonly Dictionary<string, string> _sentLights = new(StringComparer.Ordinal);
    private readonly Dictionary<NetObjectId, (float Time, float At, long Driver)> _clocks = new();
    private ulong _clocksForGuest;
    private float _nextClock;
    public CustomizationWorld Handler { get; }
    internal static NativeCustomization Current => CoopController.Instance?.Customization;

    public NativeCustomization(CoopController coop, WorldSync world)
    {
        _coop = coop; _world = world;
        Handler = new CustomizationWorld(coop.Objects.Model, coop.Activity, this);
        world.GenerationStarted += (uint _, string _) => { Handler.Reset(); _lightSends.Clear(); _sentLights.Clear(); _clocks.Clear(); _clocksForGuest = 0; };
        world.Rejected += (OperationId op, RejectReason reason, string text) =>
        {
            if (op.Actor == world.Local && text.Contains(Handler.Name)) Plugin.Logger.LogWarning($"[lights] {op} refused: {text}");
        };
    }

    private bool Active => _world.Admitted && _world.Divergence == null && _coop.Objects.Initialized && _coop.WorldReady;
    private static bool Bypass => !NativePcLifecycle.InCoopWorkshop;

    private VirtualComputer ComputerOf(NetObjectId pc) => _coop.Objects.FindBench(pc)?.m_workStation?.m_virtualComputer;
    private ComputerSave RecordOf(NetObjectId pc) => ComputerOf(pc)?.ComputerSave ?? _coop.Objects.FindBench(pc)?.GetComputer(false) ?? _coop.Objects.RecordFor(pc);
    private static LEDDriver DriverOf(VirtualComputer vc)
    {
        var sim = vc?.GetComputerSim();
        return sim != null && sim.IsRunning() ? sim.m_LEDDriver : null;
    }

    /// <summary>This player is at that workstation's monitor (or in its Lighting view).</summary>
    internal static bool LocalAt(WorkStation ws)
    {
        var ctrl = GameController.Get();
        if (ws == null || ctrl == null) return false;
        bool Here(State s) =>
            s?.TryCast<LookingAtMonitorState>()?.m_ws?.Pointer == ws.Pointer ||
            s?.TryCast<LightingAppState>()?.lightingApp?.m_workStation?.Pointer == ws.Pointer;
        if (Here(ctrl.GetCurrentState())) return true;
        if (ctrl.m_previousState != null) foreach (var s in ctrl.m_previousState) if (Here(s)) return true;
        return false;
    }

    // ---------------- light values ----------------

    private static LedValues Values(LEDConfig c) => new(
        c.m_colour.m_r, c.m_colour.m_g, c.m_colour.m_b, c.m_colour2.m_r, c.m_colour2.m_g, c.m_colour2.m_b, (byte)c.m_effect, c.m_speed,
        c.m_speedModifier, c.m_offset, c.m_modulo, c.m_flowSequenceID, c.m_allowChangeColour, c.m_allowChangeEffect, (byte)c.m_effectDirection,
        (byte)c.m_effectAddressableAnimType, (byte)c.m_effectColourMode, (byte)c.m_effectMusicColourMode, (byte)c.m_effectTemperatureMode,
        c.FlowSpread, c.m_isEnabled, c.m_showInLightinApp);

    private static void Write(LEDConfig c, LedValues v)
    {
        var one = c.m_colour; one.m_r = v.R; one.m_g = v.G; one.m_b = v.B; c.m_colour = one;
        var two = c.m_colour2; two.m_r = v.R2; two.m_g = v.G2; two.m_b = v.B2; c.m_colour2 = two;
        c.m_effect = (LEDEffect)v.Effect; c.m_speed = v.Speed; c.m_speedModifier = v.SpeedModifier; c.m_offset = v.Offset; c.m_modulo = v.Modulo;
        c.m_flowSequenceID = v.Flow; c.m_allowChangeColour = v.AllowColour; c.m_allowChangeEffect = v.AllowEffect;
        c.m_effectDirection = (LEDConfig.EffectDirection)v.Direction; c.m_effectAddressableAnimType = (LEDConfig.EffectAddressableAnimationType)v.AnimType;
        c.m_effectColourMode = (LEDConfig.EffectColourMode)v.ColourMode; c.m_effectMusicColourMode = (LEDConfig.EffectMusicColourMode)v.MusicMode;
        c.m_effectTemperatureMode = (LEDConfig.EffectTemperatureMode)v.TemperatureMode; c.FlowSpread = v.FlowSpread; c.m_isEnabled = v.Enabled;
        c.m_showInLightinApp = v.Shown;
    }

    private static List<LedLight> Read(LEDDriverConfig cfg)
    {
        var result = new List<LedLight>();
        var dict = cfg?.m_config;
        if (dict == null) return result;
        foreach (var entry in dict)
        {
            var list = entry.Value;
            if (string.IsNullOrEmpty(entry.Key) || list == null) continue;
            for (var i = 0; i < list.Count && i <= LedLight.MaxIndex; i++)
                if (list[i] != null) result.Add(new LedLight(entry.Key, i, Values(list[i])));
        }
        return result.Where(l => l.IsValid).ToList();
    }

    private static Peripheral PeripheralAt(WorkStation ws, string slot)
    {
        if (ws == null) return null;
        foreach (var p in ws.GetComponentsInChildren<Peripheral>(true))
            if (p != null && p.Slot != null && NativeObjectState.PathOf(p.Slot.transform) == slot) return p;
        return null;
    }

    /// <summary>Every set of lights of a PC: its live lights, presets and the peripherals at its workstation.</summary>
    private static List<(LightTarget Target, int Preset, string Slot, List<LedLight> Lights)> Sets(ComputerSave record, WorkStation ws)
    {
        var sets = new List<(LightTarget, int, string, List<LedLight>)>();
        var software = record?.m_software;
        if (software?.m_LEDConfig != null) sets.Add((LightTarget.Pc, Math.Clamp(software.m_currentPresetIndex, -1, CustomizationWorld.MaxPresets - 1), "", Read(software.m_LEDConfig)));
        var presets = software?.m_LEDConfigPresets;
        if (presets != null)
            for (var i = 0; i < presets.Length && i < CustomizationWorld.MaxPresets; i++)
                if (presets[i] != null) sets.Add((LightTarget.Preset, i, "", Read(presets[i])));
        if (ws != null)
            foreach (var p in ws.GetComponentsInChildren<Peripheral>(true))
                if (p?.m_LEDConfig != null && p.Slot != null) sets.Add((LightTarget.Peripheral, -1, NativeObjectState.PathOf(p.Slot.transform), Read(p.m_LEDConfig)));
        return sets;
    }

    // ---------------- sending this player's lighting ----------------

    internal void LocalLightsChanged(LightingApp app)
    {
        if (!Active || NativeDesktopInput.Injecting || NativeProjection.Active || app == null || !LocalAt(app.m_workStation)) return;
        var record = app.m_Save ?? app.m_workStation?.m_virtualComputer?.ComputerSave;
        var pc = _coop.Objects.IdOf(record);
        if (pc.IsNone) return;
        if (!_lightSends.ContainsKey(pc)) _lightSends[pc] = Time.realtimeSinceStartup + LightsSettle;
    }

    private void SendLights()
    {
        foreach (var (pc, due) in _lightSends.ToArray())
        {
            if (Time.realtimeSinceStartup < due) continue;
            _lightSends.Remove(pc);
            var actor = _coop.Activity.Get(_world.Local);
            if (actor == null) continue;
            try
            {
                var record = RecordOf(pc);
                if (record == null) continue;
                var sent = 0;
                foreach (var (target, preset, slot, lights) in Sets(record, _coop.Objects.FindBench(pc)?.m_workStation))
                {
                    var payloads = new List<byte[]>();
                    for (var i = 0; i < lights.Count; i += CustomizationWorld.MaxLights)
                        payloads.Add(CustomizationWorld.Lights(actor.Epoch, pc, target, preset, slot, lights.GetRange(i, Math.Min(CustomizationWorld.MaxLights, lights.Count - i))));
                    if (payloads.Count == 0) continue;
                    var key = $"{pc}|{target}|{preset}|{slot}";
                    using var all = new MemoryStream();
                    foreach (var payload in payloads) all.Write(payload, 0, payload.Length);
                    var signature = Convert.ToHexString(SHA256.HashData(all.ToArray()));
                    if (_sentLights.TryGetValue(key, out var last) && last == signature) continue;
                    foreach (var payload in payloads)
                        if (!_world.Submit(CommandTypes.Customization, payload, out _)) { _coop.Notice("Lighting not shared: not connected to the host's world."); return; }
                    _sentLights[key] = signature;
                    sent += lights.Count;
                }
                if (sent > 0) Plugin.Logger.LogInfo($"[lights] PC {pc}: {sent} light setting(s) sent.");
            }
            catch (Exception e) { Plugin.Logger.LogError($"[lights] PC {pc}: sending the lights failed: {e}"); }
        }
    }

    // ---------------- showing the other player's lighting ----------------

    public void LightsChanged(in MutationContext ctx, PcFact pc, LightTarget target, int preset, string slot, IReadOnlyList<LedLight> lights)
    {
        if (ctx.FromLocalPlayer) return; // this game already shows them
        try
        {
            var record = RecordOf(pc.Id);
            var vc = ComputerOf(pc.Id);
            var software = record?.m_software;
            var cfg = target switch
            {
                LightTarget.Pc => software?.m_LEDConfig,
                LightTarget.Preset => software?.m_LEDConfigPresets is { } p && preset < p.Length ? p[preset] : null,
                _ => PeripheralAt(_coop.Objects.FindBench(pc.Id)?.m_workStation, slot)?.m_LEDConfig,
            };
            if (cfg == null) { Plugin.Logger.LogWarning($"[lights] PC {pc.Id}: no {target} lights {(slot == "" ? "" : slot + " ")}here; the other player's change waits for the next load."); return; }
            int applied = 0, missing = 0; var rebuild = false;
            NativeProjection.Run(() =>
            {
                if (target == LightTarget.Pc && preset >= 0 && software != null) software.m_currentPresetIndex = preset;
                foreach (var light in lights)
                {
                    var list = cfg.GetConfig(light.Group);
                    if (list == null || light.Index >= list.Count || list[light.Index] == null) { missing++; continue; }
                    var c = list[light.Index];
                    var old = Values(c);
                    if (old == light.Values) continue;
                    rebuild |= old.Flow != light.Values.Flow || old.Effect != light.Values.Effect || old.AnimType != light.Values.AnimType || old.Enabled != light.Values.Enabled;
                    Write(c, light.Values);
                    applied++;
                }
                if (target != LightTarget.Preset && applied > 0) Refresh(record, vc, rebuild);
            });
            if (applied + missing > 0)
                Plugin.Logger.LogInfo($"[lights] PC {pc.Id}: {applied} {target} light(s) from {ctx.Op} applied{(missing > 0 ? $", {missing} not on this PC here" : "")}{(rebuild ? ", lights rebuilt" : "")}.");
        }
        catch (Exception e) { _coop.ProjectionFailed($"the other player's lighting on PC {pc.Id} could not be shown here", e); }
    }

    /// <summary>Lights read their settings every frame; an effect that flows across lights or a light switched on or off
    /// also needs the game's own rebuild (<c>LEDDriver.TurnOn</c>, as the Lighting app does). An open Lighting app on that
    /// PC shows the new settings.</summary>
    private static void Refresh(ComputerSave record, VirtualComputer vc, bool rebuild)
    {
        var app = UnityEngine.Object.FindObjectsOfType<LightingApp>().FirstOrDefault(a => a != null && a.isActiveAndEnabled && a.m_Save?.Pointer == record?.Pointer);
        try
        {
            if (app != null) { app.UpdateLightList(); return; }
            if (rebuild && DriverOf(vc) is { } driver && vc.GetComputerSim()?.GetCase() is { } mesh) driver.TurnOn(mesh, record);
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[lights] refreshing the lights failed: " + e.Message); }
    }

    /// <summary>Phase 6 adopts the host's software record by swapping the object; the lights hold the old record's light
    /// objects. Copy the new settings into those and keep them, so the lights follow (and later edits are saved).</summary>
    internal static void KeepLights(ComputerSoftware old, ComputerSoftware incoming)
    {
        var live = old?.m_LEDConfig; var next = incoming?.m_LEDConfig;
        if (live == null || next == null) return;
        var lights = Read(next);
        var same = Read(live).Count == lights.Count;
        foreach (var light in lights)
        {
            var list = live.GetConfig(light.Group);
            if (list == null || light.Index >= list.Count || list[light.Index] == null) { same = false; continue; }
            Write(list[light.Index], light.Values);
        }
        if (same) incoming.m_LEDConfig = live;
        else Plugin.Logger.LogInfo("[lights] the host's software record has other lights than this PC; they take effect at its next power-on.");
    }

    // ---------------- the effect clock ----------------

    public void Clock(in MutationContext ctx, PcFact pc, float time)
    {
        if (ctx.FromLocalPlayer) return;
        _clocks[pc.Id] = (time, Time.realtimeSinceStartup, 0);
    }

    private void UpdateClocks()
    {
        var now = Time.realtimeSinceStartup;
        if (now < _nextClock) return;
        _nextClock = now + (_world.IsAuthority ? 1f : 0.25f);
        if (_world.IsAuthority)
        {
            var guest = _coop.Activity.Get(ActorSlot.Guest)?.Epoch ?? 0;
            var host = _coop.Activity.Get(ActorSlot.Host);
            if (guest == 0 || host == null) { _clocksForGuest = guest; return; }
            var fresh = guest != _clocksForGuest;
            _clocksForGuest = guest;
            foreach (var pc in _coop.Objects.Model.Pcs.Where(p => p.Location.Place == PcPlace.Bench))
            {
                var driver = DriverOf(ComputerOf(pc.Id));
                if (driver == null) { _clocks.Remove(pc.Id); continue; }
                var time = driver.m_time;
                var known = _clocks.TryGetValue(pc.Id, out var last);
                var expected = last.Time + (now - last.At);
                if (!fresh && known && last.Driver == driver.Pointer.ToInt64() && time > expected - 1f && now - last.At < ClockEvery) continue;
                if (_world.Submit(CommandTypes.Customization, CustomizationWorld.Clock(host.Epoch, pc.Id, time), out _))
                    _clocks[pc.Id] = (time, now, driver.Pointer.ToInt64());
            }
            return;
        }
        foreach (var (pc, clock) in _clocks.ToArray())
        {
            if (_coop.Objects.Model.Pcs.All(p => p.Id != pc)) { _clocks.Remove(pc); continue; }
            var driver = DriverOf(ComputerOf(pc));
            if (driver == null) continue;
            var expected = clock.Time + (now - clock.At);
            if (MathF.Abs(driver.m_time - expected) > ClockTolerance) driver.m_time = expected;
        }
    }

    // ---------------- custom cable purchases ----------------

    internal sealed class Purchase
    {
        public Dictionary<string, List<PartInstance>> Inventory;
        public HashSet<string> Custom;
    }

    private static HashSet<string> CustomIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var list = PartsDatabase.s_instance?.CustomCableDescs;
        if (list != null) foreach (var d in list) if (!string.IsNullOrEmpty(d?.m_id)) ids.Add(d.m_id);
        return ids;
    }

    internal Purchase BeforeBuy() => Active ? new Purchase { Inventory = NativeToolBenches.Inventory(), Custom = CustomIds() } : null;

    internal void AfterBuy(Purchase before)
    {
        if (before == null || !Active) return;
        try
        {
            var after = NativeToolBenches.Inventory();
            var change = NativeToolBenches.InventoryChange(before.Inventory, after);
            if (change == null) return; // nothing was bought
            var db = PartsDatabase.s_instance ?? throw new InvalidDataException("Parts database unavailable.");
            // New part kinds: the ones this purchase registered, and any custom kind the new items are made of.
            var needed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, items) in after)
            {
                var had = before.Inventory.TryGetValue(key, out var b) ? b.Count : 0;
                if (items.Count <= had) continue;
                foreach (var item in items)
                {
                    needed.Add(item.GetPartId());
                    var set = item.TryCast<CableSetPartInstance>();
                    if (set != null) foreach (var c in set.GetAllCablesInSet()) if (c != null) needed.Add(c.GetPartId());
                }
            }
            var descs = new List<byte[]>();
            foreach (var desc in db.CustomCableDescs)
            {
                if (desc == null || string.IsNullOrEmpty(desc.m_id) || (before.Custom.Contains(desc.m_id) && !needed.Contains(desc.m_id))) continue;
                descs.Add(WriteDesc(desc));
            }
            var w = new NetDataWriter();
            w.Put(descs.Count); foreach (var d in descs) { w.Put(d.Length); w.Put(d); }
            w.Put(change.Data, 0, change.Length);
            var packed = NativeToolBenches.Deflate(w.Data, w.Length);
            if (packed.Length > CustomizationWorld.MaxBytes) throw new InvalidDataException("the purchase is too large to share");
            var actor = _coop.Activity.Get(_world.Local) ?? throw new InvalidDataException("not in the shared world");
            var total = (packed.Length + CustomizationWorld.MaxChunkBytes - 1) / CustomizationWorld.MaxChunkBytes;
            for (var i = 0; i < total; i++)
            {
                var data = packed.AsSpan(i * CustomizationWorld.MaxChunkBytes, Math.Min(CustomizationWorld.MaxChunkBytes, packed.Length - i * CustomizationWorld.MaxChunkBytes)).ToArray();
                if (!_world.Submit(CommandTypes.Customization, CustomizationWorld.Chunk(actor.Epoch, i, total, data), out _)) throw new InvalidDataException("not connected to the host's world");
            }
            _world.Submit(CommandTypes.Customization, CustomizationWorld.Purchase(actor.Epoch, packed), out _);
            Plugin.Logger.LogInfo($"[cables] purchase shared: {descs.Count} custom cable kind(s), {packed.Length} bytes.");
        }
        catch (Exception e) { _coop.ProjectionFailed("This game's Cable app purchase could not be shared", e); }
    }

    private static byte[] WriteDesc(PartDesc desc)
    {
        var stream = new Il2CppSystem.IO.MemoryStream();
        try
        {
            new Il2CppSystem.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Serialize(stream, desc);
            return stream.ToArray();
        }
        finally { stream.Dispose(); }
    }

    private static PartDesc ReadDesc(byte[] bytes)
    {
        var stream = new Il2CppSystem.IO.MemoryStream(bytes);
        try
        {
            var desc = new Il2CppSystem.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Deserialize(stream).TryCast<PartDesc>();
            if (desc == null || string.IsNullOrEmpty(desc.m_id) || stream.Position != stream.Length) throw new InvalidDataException("Invalid part kind.");
            return desc;
        }
        finally { stream.Dispose(); }
    }

    public void Purchased(in MutationContext ctx, ActorSlot actor, byte[] result)
    {
        if (ctx.FromLocalPlayer) return;
        try
        {
            var r = new NetDataReader(NativeToolBenches.Inflate(result, 16L * 1024 * 1024));
            var n = r.GetInt();
            if (n is < 0 or > 64) throw new InvalidDataException("Invalid purchase.");
            var descs = new List<PartDesc>();
            for (var i = 0; i < n; i++)
            {
                var length = r.GetInt();
                if (length is < 1 or > 1024 * 1024) throw new InvalidDataException("Invalid part kind.");
                var bytes = new byte[length]; r.GetBytes(bytes, length);
                descs.Add(ReadDesc(bytes));
            }
            var db = PartsDatabase.s_instance ?? throw new InvalidDataException("Parts database unavailable.");
            var added = 0;
            NativeProjection.Run(() =>
            {
                // A mirrored "Add to cart" may already have made the kind known here; the save keeps only listed custom kinds.
                var listed = CustomIds();
                foreach (var desc in descs)
                {
                    if (!db.IsKnownPart(desc.m_id)) { db.AddNewPart(desc.m_id, desc); added++; }
                    if (!listed.Contains(desc.m_id)) db.AddCustomCable(db.GetDesc(desc.m_id, null) ?? desc);
                }
            });
            NativeToolBenches.ApplyInventoryChange(r);
            Plugin.Logger.LogInfo($"[cables] the other player's purchase is here: {added} new cable kind(s) of {descs.Count}.");
        }
        catch (Exception e) { _coop.ProjectionFailed("The other player's Cable app purchase could not be shown on this game", e); }
    }

    public void Update()
    {
        if (!Active) return;
        if (_lightSends.Count > 0) SendLights();
        UpdateClocks();
    }

    // ---------------- hooks ----------------

    private static bool s_remoteOpen;

    [HarmonyPatch]
    internal static class LightingChangeHook
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(LightingApp), nameof(LightingApp.OnApply));
            yield return AccessTools.Method(typeof(LightingApp), nameof(LightingApp.OnReset));
            yield return AccessTools.Method(typeof(LightingApp), nameof(LightingApp.OnPresetSavePressed));
            yield return AccessTools.Method(typeof(LightingApp), nameof(LightingApp.OnPresetLoadPressed));
            yield return AccessTools.Method(typeof(LightingApp), nameof(LightingApp.OnPowerButtonPressed));
        }

        private static void Postfix(LightingApp __instance)
        {
            if (Bypass) return;
            try { Current?.LocalLightsChanged(__instance); }
            catch (Exception e) { Plugin.Logger.LogError("[lights] change hook failed: " + e); }
        }
    }

    /// <summary>The Lighting app opening for someone who isn't this player (the other player's mirrored click, or the
    /// host's open-app matching) doesn't take over this player's view.</summary>
    [HarmonyPatch(typeof(LightingApp), "OnEnable")]
    internal static class LightingOpenScope
    {
        private static void Prefix(LightingApp __instance, out bool __state)
        {
            __state = false;
            if (Bypass) return;
            try { __state = NativeDesktopInput.Injecting || NativeProjection.Active || !LocalAt(__instance.m_workStation); }
            catch (Exception e) { Plugin.Logger.LogError("[lights] open hook failed: " + e); }
            s_remoteOpen = __state;
        }

        private static void Postfix(LightingApp __instance, bool __state)
        {
            if (!__state) return;
            try { __instance.initialised = false; } // this player's own first open still sets up the Lighting view
            catch (Exception e) { Plugin.Logger.LogError("[lights] open hook failed: " + e); }
            Plugin.Logger.LogInfo("[lights] the Lighting app opened on a monitor this player isn't using; its view stays as it is.");
        }

        private static Exception Finalizer(Exception __exception) { s_remoteOpen = false; return __exception; }
    }

    [HarmonyPatch(typeof(BenchSlot), nameof(BenchSlot.ClaimCamera))]
    internal static class LightingCameraGuard
    {
        private static bool Prefix() => !s_remoteOpen;
    }

    [HarmonyPatch(typeof(GameController), nameof(GameController.SetCurrentState))]
    internal static class LightingStateGuard
    {
        private static bool Prefix(State __0) => !(s_remoteOpen && __0?.TryCast<LightingAppState>() != null);
    }

    [HarmonyPatch(typeof(CableApp), nameof(CableApp.OnBuy))]
    internal static class BuyHook
    {
        private static bool Prefix(out Purchase __state)
        {
            __state = null;
            if (Bypass) return true;
            // The buyer's game shares the purchase; the mirrored click must not buy a second set here.
            if (NativeDesktopInput.Injecting) { Plugin.Logger.LogInfo("[cables] the other player's Buy click: their game shares the purchase."); return false; }
            try { __state = Current?.BeforeBuy(); }
            catch (Exception e) { Plugin.Logger.LogError("[cables] buy hook failed: " + e); }
            return true;
        }

        private static void Postfix(Purchase __state)
        {
            if (__state == null) return;
            try { Current?.AfterBuy(__state); }
            catch (Exception e) { Plugin.Logger.LogError("[cables] buy hook failed: " + e); }
        }
    }
}

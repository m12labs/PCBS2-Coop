using HarmonyLib;
using PCBS2Coop.Network;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// Phase 12 (owner decisions, November 2, 2026): the workshop around the PCs.
/// <list type="bullet">
/// <item><b>Decorating</b> (decision 1): opening the Customise Workshop app claims decorating for this player; the other
/// player's app closes with "Someone is decorating the workshop.". While it's open this game compares the game's own
/// decoration list (<c>Decorator.CreateSaveList</c>) and the light colour after each change has loaded, and sends what
/// changed; the other game merges the picks and refreshes the room with <c>Decorator.UpdateOnlyMainRoom</c>.</item>
/// <item><b>Lights</b> (decision 2): the light switch's click is shared; the other game switches with the game's own
/// <c>LightSwitch.SetOn</c> (lights and ceiling fans).</item>
/// <item><b>Seasonal decorations</b> (decision 3): the switch's click is shared (<c>DecorationSwitch.OnClickSwitch</c>);
/// a guest's game takes the host's season for its workshop loads.</item>
/// </list>
/// Lights and the seasonal decoration are kept in the co-op world state, so a joining guest and Continue get them.
/// Pictures and imported PCs: <c>NativeWorkshop.Content.cs</c>.
/// </summary>
internal sealed partial class NativeWorkshop : IWorkshopNative
{
    private const float DecorPoll = 0.5f, DecorCloseWait = 10f, DecorVerify = 1.5f;

    private readonly CoopController _coop;
    private readonly WorldSync _world;
    public WorkshopWorld Handler { get; }
    internal static NativeWorkshop Current => CoopController.Instance?.Workshop;

    // This player's decorating.
    private bool _decorating, _closing, _claiming;
    private OperationId _claimOp;
    private float _closingSince, _nextDecorPoll;
    private CustomiseWorkShopApp _appToClose;
    private int _closeAppFrame;
    private CalendarInteraction _roomPickerRequest;
    private bool _roomPickerOpen;
    private Dictionary<(int, int, int), DecorItem> _baseline;
    private RoomLight? _baselineLight;
    private bool _localRoomChanged, _refreshRoom;
    private float _refreshAt;
    private Dictionary<(int, int, int), DecorItem> _refreshPicks;
    // The other player's decorating, applied here.
    private List<DecorItem> _decorTarget;
    private float _verifyAt;
    private int _verifyLeft;
    // The workshop state from a snapshot or save, applied once the world is ready here.
    private bool _stateWaiting;
    private float _nextSeason;

    public NativeWorkshop(CoopController coop, WorldSync world)
    {
        _coop = coop; _world = world;
        Handler = new WorkshopWorld(coop.Objects.Model, coop.Activity, this);
        world.GenerationStarted += (uint _, string _) => ResetLocal();
        world.Rejected += OnRejected;
    }

    private void ResetLocal()
    {
        _decorating = _closing = false; _baseline = null; _baselineLight = null; _decorTarget = null; _verifyLeft = 0;
        _appToClose = null;
        _roomPickerRequest = null;
        _roomPickerOpen = false;
        _localRoomChanged = _refreshRoom = false;
        _refreshPicks = null;
        ResetContent();
    }

    private bool Active => _world.Admitted && _world.Divergence == null && _coop.Objects.Initialized && _coop.WorldReady;
    private static bool Bypass => !NativePcLifecycle.InCoopWorkshop;
    private ulong? Epoch => _coop.Activity.Get(_world.Local)?.Epoch;

    private void OnRejected(OperationId op, RejectReason reason, string text)
    {
        if (op.Actor != _world.Local || !text.Contains(Handler.Name)) return;
        var why = text.Replace(Handler.Name + ": ", "");
        Plugin.Logger.LogWarning($"[workshop] {op} refused: {why}");
        if (_claiming || op == _claimOp)
        {
            _decorating = _closing = false;
            _coop.Notice(why);
            DeferAppClose();
        }
        else ContentRejected(op, why);
    }

    // ---------------- decorations ----------------

    private static Decorator Decorator => Object.FindObjectOfType<Decorator>();
    private static ThemeManager Theme => Object.FindObjectOfType<ThemeManager>();
    internal bool Settled => !_refreshRoom && _refreshPicks == null && Decorator?.Loading != true;

    private static List<DecorItem> ReadDecor()
    {
        var result = new List<DecorItem>();
        var dec = Decorator;
        if (dec == null) return result;
        var list = new Il2CppSystem.Collections.Generic.List<DecoratorListLink>();
        dec.CreateSaveList(list);
        var seen = new Dictionary<(int, int), int>();
        foreach (var l in list)
        {
            var item = new DecorItem(l.SelectionIndex ?? "", l.ID, l.CurrentIndex ?? "", (int)l.DecType, (int)l.Theme, l.isDlcItem, (int)l.SteamDlcPack, NextOf(seen, l));
            if (item.IsValid) result.Add(item);
            else Plugin.Logger.LogWarning($"[workshop] decoration '{item.Current}' ({item.Type}/{item.Id}) can't be shared.");
        }
        return result;
    }

    /// <summary>Which of the decorations with this type and number <paramref name="l"/> is, in the game's own list order.</summary>
    private static int NextOf(Dictionary<(int, int), int> seen, DecoratorListLink l)
    {
        var key = ((int)l.DecType, l.ID);
        var n = seen.GetValueOrDefault(key);
        seen[key] = n + 1;
        return n;
    }

    private static RoomLight? ReadLight()
    {
        var tm = Theme;
        if (tm == null) return null;
        var c = tm.m_currentColour;
        var light = new RoomLight(c.r, c.g, c.b, c.a, tm.GetPulse());
        return light.IsValid ? light : null;
    }

    private static Dictionary<(int, int, int), DecorItem> Keyed(IEnumerable<DecorItem> items)
    {
        var map = new Dictionary<(int, int, int), DecorItem>();
        foreach (var i in items) map[i.Key] = i;
        return map;
    }

    /// <summary>The Customise Workshop app opened on this game.</summary>
    internal void AppOpened()
    {
        if (!Active || _decorating) return;
        if (!BeginDecorating()) DeferAppClose();
    }

    private bool BeginDecorating()
    {
        if (!Active) return false;
        if (Handler.DecorBusy(_world.Local) is { } busy)
        {
            _coop.Notice(busy);
            return false;
        }
        if (Epoch is not { } epoch) return false;
        _baseline = Keyed(ReadDecor()); _baselineLight = ReadLight();
        _decorating = true; _closing = false;
        _localRoomChanged = false;
        _claiming = true;
        try
        {
            if (!_world.Submit(CommandTypes.Workshop, WorkshopWorld.Decorate(epoch, true), out _claimOp))
            { _decorating = false; _coop.Notice("Decorating isn't shared: not connected to the host's world."); return false; }
        }
        finally { _claiming = false; }
        if (_decorating) Plugin.Logger.LogInfo($"[workshop] decorating ({_baseline.Count} decoration(s) at the start).");
        return _decorating;
    }

    /// <summary>The app closed: send the last change once it has loaded, then free decorating.</summary>
    internal void AppClosed()
    {
        if (!_decorating || _closing || _roomPickerRequest is not null || _roomPickerOpen) return;
        _closing = true; _closingSince = Time.realtimeSinceStartup;
    }

    private void DeferAppClose()
    {
        if (_roomPickerRequest is not null || _roomPickerOpen)
        {
            _roomPickerRequest = null;
            return;
        }
        // OnEnable runs before the tablet finishes opening its native window.
        _appToClose = CustomiseWorkShopApp.Get();
        _closeAppFrame = Time.frameCount + 1;
    }

    private void RequestRoomPicker(CalendarInteraction picker)
    {
        if (_decorating) return;
        _roomPickerRequest = picker;
        if (!BeginDecorating()) _roomPickerRequest = null;
    }

    private void UpdateRoomPicker()
    {
        var ctrl = GameController.Get();
        if (_roomPickerRequest is { } picker && Handler.Decorator == _world.Local)
        {
            _roomPickerRequest = null;
            if (picker != null && picker.isActiveAndEnabled && ctrl?.GetCurrentState()?.TryCast<WalkingState>() != null)
            {
                try { NativeProjection.Run(picker._GetInteractions_b__4_1); }
                catch (Exception e) { Plugin.Logger.LogWarning("[workshop] opening the room colour picker failed: " + e.Message); }
            }
            _roomPickerOpen = ctrl?.GetCurrentState()?.TryCast<LookingAtMonitorState>()?.m_isColourPicker == true;
            if (!_roomPickerOpen) AppClosed();
        }
        if (!_roomPickerOpen) return;
        if (ctrl?.GetCurrentState()?.TryCast<LookingAtMonitorState>()?.m_isColourPicker == true)
        {
            if (!_decorating) ctrl.GoToPreviousState();
            return;
        }
        _roomPickerOpen = false;
        AppClosed();
    }

    private void UpdateAppClose()
    {
        var app = _appToClose;
        if (app == null || Time.frameCount < _closeAppFrame) return;
        _appToClose = null;
        try
        {
            if (app.isActiveAndEnabled) app.OnClickCloseApp(true);
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[workshop] closing the Customise Workshop app failed: " + e.Message); }
    }

    private void UpdateDecorating()
    {
        if (!_decorating || Time.realtimeSinceStartup < _nextDecorPoll) return;
        _nextDecorPoll = Time.realtimeSinceStartup + DecorPoll;
        var loading = Decorator?.Loading == true;
        if (loading && !(_closing && Time.realtimeSinceStartup - _closingSince > DecorCloseWait)) return;
        SendDecor();
        if (!_closing) return;
        _decorating = _closing = false;
        if (_localRoomChanged) RefreshRoomSoon();
        if (Epoch is { } epoch && _world.Submit(CommandTypes.Workshop, WorkshopWorld.Decorate(epoch, false), out _))
            Plugin.Logger.LogInfo("[workshop] decorating finished.");
    }

    private void SendDecor()
    {
        try
        {
            if (_baseline == null || Epoch is not { } epoch) return;
            var now = ReadDecor();
            var changed = now.Where(i => !_baseline.TryGetValue(i.Key, out var was) || was != i).ToList();
            var light = ReadLight();
            var lightChanged = light != null && light != _baselineLight;
            if (changed.Count == 0 && !lightChanged) return;
            for (var i = 0; i < Math.Max(1, changed.Count); i += WorkshopWorld.MaxDecorItems)
            {
                var part = changed.Skip(i).Take(WorkshopWorld.MaxDecorItems).ToList();
                var last = i + WorkshopWorld.MaxDecorItems >= changed.Count;
                if (!_world.Submit(CommandTypes.Workshop, WorkshopWorld.Decor(epoch, part, last && lightChanged ? light : null), out _))
                { _coop.Notice("Decorating not shared: not connected to the host's world."); return; }
            }
            _baseline = Keyed(now); _baselineLight = light;
            _localRoomChanged |= changed.Count > 0;
            Plugin.Logger.LogInfo($"[workshop] {changed.Count} decoration(s){(lightChanged ? " and the light colour" : "")} sent.");
        }
        catch (Exception e) { Plugin.Logger.LogError("[workshop] sending the decorations failed: " + e); }
    }

    public void Decorating(in MutationContext ctx, ActorSlot actor, bool on)
    {
        if (ctx.FromLocalPlayer) return;
        Plugin.Logger.LogInfo($"[workshop] the other player {(on ? "started" : "finished")} decorating.");
        // This player's app may have opened while the claim was on its way: the host gave decorating to the other player.
        if (on && _decorating && !_closing) { _decorating = false; _coop.Notice(WorkshopWorld.Busy); DeferAppClose(); }
    }

    public void Decorated(in MutationContext ctx, IReadOnlyList<DecorItem> items, RoomLight? light)
    {
        if (ctx.FromLocalPlayer) return;
        try
        {
            if (items.Count > 0)
            {
                ApplyDecor(items);
                if (_refreshPicks != null)
                    foreach (var item in items) _refreshPicks[item.Key] = item;
                RefreshRoomSoon();
                _decorTarget = (_decorTarget ?? new List<DecorItem>()).Where(t => items.All(i => i.Key != t.Key)).Concat(items).ToList();
                _verifyAt = Time.realtimeSinceStartup + DecorVerify; _verifyLeft = 2;
            }
            if (light is { } l) ApplyLight(l);
            Plugin.Logger.LogInfo($"[workshop] the other player's decorating: {items.Count} decoration(s){(light != null ? " and the light colour" : "")} applied.");
        }
        catch (Exception e) { _coop.ProjectionFailed("The other player's decorating could not be shown on this game", e); }
    }

    private void RefreshRoomSoon()
    {
        _refreshRoom = true;
        _refreshAt = Time.realtimeSinceStartup + DecorPoll;
    }

    private void UpdateRoom()
    {
        if ((!_refreshRoom && _refreshPicks == null) || Decorator is not { Loading: false } dec) return;
        try
        {
            if (_refreshPicks != null)
            {
                // The renderer normalizes some saved names to the surrounding theme while it loads a prefab.
                // Keep the chosen values for later saves and admission, including the unchanged surfaces.
                var picks = _refreshPicks.Values.ToArray();
                _refreshPicks = null;
                ApplyDecor(picks);
                if (Handler.LightsOn is { } on) SetLights(on, animate: false);
            }
            if (!_refreshRoom || _decorating || Time.realtimeSinceStartup < _refreshAt) return;
            _refreshRoom = false;
            _refreshPicks = Keyed(ReadDecor());
            NativeProjection.Run(dec.UpdateOnlyMainRoom);
        }
        catch (Exception e)
        {
            _refreshRoom = false;
            _refreshPicks = null;
            _coop.ProjectionFailed("The shared decorations could not refresh the workshop", e);
        }
    }

    /// <summary>Set the native picks before refreshing the main room. The full save restore also changes the AR room.</summary>
    private static void ApplyDecor(IReadOnlyList<DecorItem> items)
    {
        var tm = Theme ?? throw new InvalidDataException("The workshop decorations aren't loaded.");
        var links = tm.DecoratorLinks ?? throw new InvalidDataException("The workshop decorations aren't loaded.");
        var picks = new Dictionary<(int, int), DecorItem>();
        foreach (var item in items) picks[(item.Type, item.Id)] = item;
        var matched = new HashSet<(int, int)>();
        NativeProjection.Run(() =>
        {
            foreach (var link in links)
            {
                if (link == null || !picks.TryGetValue(((int)link.DecType, link.ID), out var item)) continue;
                link.SavedIndex = item.Selection;
                link.CurrentIndex = item.Current;
                link.Theme = (Decorator.WorkshopThemes)item.Theme;
                link.isDlcItem = item.Dlc;
                matched.Add((item.Type, item.Id));
            }
        });
        var missing = picks.Keys.Count(k => !matched.Contains(k));
        if (missing > 0) Plugin.Logger.LogWarning($"[workshop] {missing} of the other player's decoration(s) have no matching object in this workshop.");
    }

    private static void ApplyLight(RoomLight light)
    {
        var tm = Theme ?? throw new InvalidDataException("The workshop lights aren't loaded.");
        NativeProjection.Run(() =>
        {
            var colour = new Color(light.R, light.G, light.B, light.A);
            // The theme reads its attached wheel's selection every frame.
            var wheel = tm.m_attachedColourWheel;
            if (wheel != null) wheel.SetColour(colour, true, false);
            tm.m_currentColour = colour;
            tm.SetLightColor(colour);
            if (tm.GetPulse() == light.Pulse) return;
            if (tm.m_Lights is { Count: > 0 } lights && lights[0] != null) tm.OnPulse();
            else tm.SetSavedPulse(light.Pulse);
        });
    }

    /// <summary>Check the picks took (something of the game's own may still be changing them): compare once the decorator is idle.</summary>
    private void VerifyDecor()
    {
        if (_decorTarget == null || _verifyLeft <= 0 || Time.realtimeSinceStartup < _verifyAt || !Settled) return;
        _verifyAt = Time.realtimeSinceStartup + DecorVerify;
        try
        {
            var now = Keyed(ReadDecor());
            var wrong = _decorTarget.Where(t => !now.TryGetValue(t.Key, out var got) || got.Current != t.Current).ToList();
            if (wrong.Count == 0 || --_verifyLeft == 0)
            {
                if (wrong.Count > 0) Plugin.Logger.LogWarning($"[workshop] {wrong.Count} decoration(s) still differ from the other player's after two tries.");
                _decorTarget = null; return;
            }
            Plugin.Logger.LogInfo($"[workshop] {wrong.Count} decoration(s) differ from the other player's; setting them again.");
            ApplyDecor(wrong);
            RefreshRoomSoon();
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[workshop] checking the decorations failed: " + e.Message); _decorTarget = null; }
    }

    // ---------------- lights ----------------

    private void LocalLightSwitch(bool on)
    {
        if (!Active || Epoch is not { } epoch || GameController.Get()?.IsLightsOn == on) return;
        if (_world.Submit(CommandTypes.Workshop, WorkshopWorld.Lights(epoch, on), out _)) Plugin.Logger.LogInfo($"[workshop] lights {(on ? "on" : "off")} sent.");
    }

    public void Lights(in MutationContext ctx, bool on)
    {
        if (ctx.FromLocalPlayer) return;
        try { if (SetLights(on, animate: true)) Plugin.Logger.LogInfo($"[workshop] the other player switched the lights {(on ? "on" : "off")}."); }
        catch (Exception e) { _coop.ProjectionFailed("The other player's light switch could not be shown on this game", e); }
    }

    private static bool SetLights(bool on, bool animate)
    {
        var ctrl = GameController.Get();
        if (ctrl == null || ctrl.IsLightsOn == on) return false;
        var sw = Object.FindObjectsOfType<LightSwitch>().FirstOrDefault(s => s != null && s.isActiveAndEnabled) ?? Object.FindObjectOfType<LightSwitch>();
        if (sw == null) throw new InvalidDataException("The workshop has no light switch here.");
        NativeProjection.Run(() => sw.SetOn(on, animate, true));
        return true;
    }

    // ---------------- seasonal decorations ----------------

    private void LocalDecorationSwitch(DecorationSwitch sw)
    {
        if (!Active || Epoch is not { } epoch || sw == null) return;
        var id = (byte)sw.m_id;
        if (id is < 1 or > WorkshopWorld.MaxDecoration) return;
        if (_world.Submit(CommandTypes.Workshop, WorkshopWorld.Decoration(epoch, id), out _)) Plugin.Logger.LogInfo($"[workshop] seasonal decoration {sw.m_id} sent.");
    }

    public void Decoration(in MutationContext ctx, byte id)
    {
        if (ctx.FromLocalPlayer) return;
        try { if (SetDecoration(id)) Plugin.Logger.LogInfo($"[workshop] the other player switched the seasonal decoration to {(PCBS.DecorationID)id}."); }
        catch (Exception e) { _coop.ProjectionFailed("The other player's seasonal decoration could not be shown on this game", e); }
    }

    private static bool SetDecoration(byte id)
    {
        var all = Object.FindObjectsOfType<DecorationSwitch>(true);
        var sw = all.FirstOrDefault(s => s != null && (byte)s.m_id == id);
        if (sw == null) { Plugin.Logger.LogWarning($"[workshop] no {(PCBS.DecorationID)id} decoration switch in this workshop."); return false; }
        if (sw.IsDecorationActive()) return false;
        NativeProjection.Run(() => sw.OnClickSwitch());
        return true;
    }

    // ---------------- season and loaded state ----------------

    public void StateLoaded()
    {
        _stateWaiting = true;
        // Before the workshop scene loads, so the guest's game loads the host's seasonal decorations.
        if (!_world.IsAuthority) ApplySeason();
    }

    private void ApplySeason()
    {
        try
        {
            var ctrl = GameController.Get();
            if (ctrl == null || Handler.Season == 0) return;
            var season = (Season)(Handler.Season - 1);
            if (ctrl.m_season == season) return;
            Plugin.Logger.LogInfo($"[workshop] the host's season is {season} (this game had {ctrl.m_season}).");
            ctrl.m_season = season;
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[workshop] taking the host's season failed: " + e.Message); }
    }

    private void UpdateState()
    {
        var now = Time.realtimeSinceStartup;
        if (_world.IsAuthority && now >= _nextSeason)
        {
            _nextSeason = now + 5f;
            var ctrl = GameController.Get();
            if (ctrl != null) Handler.Season = (byte)((int)ctrl.m_season + 1);
        }
        if (!_stateWaiting) return;
        _stateWaiting = false;
        try
        {
            if (Handler.LightsOn is { } on && SetLights(on, animate: false)) Plugin.Logger.LogInfo($"[workshop] lights {(on ? "on" : "off")} as the world has them.");
            if (Handler.ActiveDecoration != 0 && SetDecoration(Handler.ActiveDecoration)) Plugin.Logger.LogInfo($"[workshop] seasonal decoration {(PCBS.DecorationID)Handler.ActiveDecoration} as the world has it.");
            if (!_world.IsAuthority) ApplySeason();
            RefreshWallpapers();
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[workshop] applying the workshop state failed: " + e.Message); }
    }

    public void Update()
    {
        UpdateAppClose();
        if (!Active) return;
        UpdateState();
        if (_roomPickerRequest is not null || _roomPickerOpen) UpdateRoomPicker();
        UpdateDecorating();
        UpdateRoom();
        VerifyDecor();
        UpdateContent();
    }

    // ---------------- hooks ----------------

    [HarmonyPatch(typeof(CalendarInteraction), nameof(CalendarInteraction._GetInteractions_b__4_1))]
    internal static class RoomPickerHook
    {
        private static bool Prefix(CalendarInteraction __instance)
        {
            if (Bypass || NativeProjection.Active) return true;
            Current?.RequestRoomPicker(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(CustomiseWorkShopApp), "OnEnable")]
    internal static class AppOpenHook
    {
        private static void Postfix()
        {
            if (Bypass || NativeProjection.Active) return;
            try { Current?.AppOpened(); }
            catch (Exception e) { Plugin.Logger.LogError("[workshop] app open hook failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(CustomiseWorkShopApp), "OnDisable")]
    internal static class AppCloseHook
    {
        private static void Postfix()
        {
            if (Bypass) return;
            try { Current?.AppClosed(); }
            catch (Exception e) { Plugin.Logger.LogError("[workshop] app close hook failed: " + e); }
        }
    }

    /// <summary>A pick in the app while the other player holds decorating (a claim still on its way) does nothing.</summary>
    [HarmonyPatch]
    internal static class DecorGuard
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CustomiseWorkShopApp), nameof(CustomiseWorkShopApp.OnClickMaterialSelect));
            yield return AccessTools.Method(typeof(CustomiseWorkShopApp), nameof(CustomiseWorkShopApp.ApplyToAll));
            yield return AccessTools.Method(typeof(ThemeManager), nameof(ThemeManager.OnPulse));
        }

        private static bool Prefix()
        {
            if (Bypass || NativeProjection.Active) return true;
            var me = Current;
            if (me == null || !me.Active || me.Handler.DecorBusy(me._world.Local) is not { } busy) return true;
            me._coop.Notice(busy);
            return false;
        }
    }

    private static bool s_tableLights, s_lightSwitchClick;

    [HarmonyPatch(typeof(LightSwitch), nameof(LightSwitch._GetInteractions_b__20_0))]
    internal static class LightSwitchClickScope
    {
        private static void Prefix(out bool __state) { __state = s_lightSwitchClick; s_lightSwitchClick = true; }
        private static Exception Finalizer(Exception __exception, bool __state) { s_lightSwitchClick = __state; return __exception; }
    }

    /// <summary>The switch's click and the lights key both end here (so do the switch's start and the water-cooling
    /// table's dimming, which stay this game's own).</summary>
    [HarmonyPatch(typeof(LightSwitch), nameof(LightSwitch.SetOn))]
    internal static class LightSwitchHook
    {
        private static void Prefix(ref bool on)
        {
            if (Bypass || NativeProjection.Active || s_tableLights) return;
            try
            {
                if (Current is not { Active: true } me) return;
                if (s_lightSwitchClick || PCBSInput.m_toggleLights?.IsDown() == true) me.LocalLightSwitch(on);
                else if (me.Handler.LightsOn is { } saved) on = saved;
            }
            catch (Exception e) { Plugin.Logger.LogError("[workshop] light switch hook failed: " + e); }
        }
    }

    /// <summary>The water-cooling table dims the ceiling lights for its own view: not a light switch.</summary>
    [HarmonyPatch(typeof(WaterCoolingState), nameof(WaterCoolingState.ModifyCeilingLights))]
    internal static class TableLightsScope
    {
        private static void Prefix() => s_tableLights = true;
        private static Exception Finalizer(Exception __exception) { s_tableLights = false; return __exception; }
    }

    [HarmonyPatch(typeof(DecorationSwitch), nameof(DecorationSwitch.OnClickSwitch))]
    internal static class DecorationSwitchHook
    {
        private static void Prefix(DecorationSwitch __instance)
        {
            if (Bypass || NativeProjection.Active) return;
            try { Current?.LocalDecorationSwitch(__instance); }
            catch (Exception e) { Plugin.Logger.LogError("[workshop] decoration switch hook failed: " + e); }
        }
    }

    /// <summary>The game works the season out from this machine's date; a guest keeps the host's.</summary>
    [HarmonyPatch(typeof(GameController), nameof(GameController.FindCurrentSeason))]
    internal static class SeasonHook
    {
        private static void Postfix()
        {
            var me = Current;
            if (me == null || me._world.IsAuthority || me.Handler.Season == 0 || CoopSaves.Current == CoopSaves.Mode.None) return;
            me.ApplySeason();
        }
    }
}

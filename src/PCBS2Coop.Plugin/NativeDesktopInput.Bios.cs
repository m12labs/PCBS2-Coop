using HarmonyLib;
using Il2CppInterop.Runtime;
using PCBS2Coop.Network;
using UnityEngine;

namespace PCBS2Coop;

/// <summary>Entering BIOS: the game's boot coroutine opens the BIOS when the BIOS key is held while this player looks at
/// that monitor. When the other player's game entered a PC's BIOS, this game's boot of the same PC takes that branch
/// itself: during its <c>MoveNext</c> only, the BIOS key reads as held and the current state is a stand-in
/// <c>LookingAtMonitorState</c> (as the 4e replay does with its stand-in work state).</summary>
internal sealed partial class NativeDesktopInput
{
    private readonly Dictionary<NetObjectId, float> _biosWanted = new();
    private static NativeDesktopInput _instance;
    private static bool _forceBiosKey;
    private LookingAtMonitorState _standIn;

    internal void Bind() => _instance = this;

    private void WantBios(NetObjectId pc)
    {
        var vc = ComputerOf(_coop, pc);
        if (vc == null) { Plugin.Logger.LogWarning($"[desktop] PC {pc}: the other player entered BIOS, but the PC isn't here."); return; }
        var surface = SurfaceOf(vc);
        if (surface == DesktopSurface.Bios) return;
        _biosWanted[pc] = Time.unscaledTime + BiosWait;
        if (surface == DesktopSurface.Desktop || surface == null)
        {
            // This game's boot already passed the BIOS prompt (or the PC is off here): boot it again into BIOS.
            if (surface == DesktopSurface.Desktop) NativeProjection.Run(() => vc.Restart());
            Plugin.Logger.LogInfo($"[desktop] PC {pc}: the other player entered BIOS; {(surface == null ? "waiting for this game's boot" : "restarting into BIOS here")}.");
        }
        else Plugin.Logger.LogInfo($"[desktop] PC {pc}: the other player entered BIOS; this game's boot follows.");
    }

    private void UpdateBiosWanted()
    {
        if (_biosWanted.Count == 0) return;
        foreach (var (pc, until) in _biosWanted.ToArray())
        {
            var vc = ComputerOf(_coop, pc);
            if (vc != null && SurfaceOf(vc) == DesktopSurface.Bios)
            { _biosWanted.Remove(pc); Plugin.Logger.LogInfo($"[desktop] PC {pc}: BIOS open here too."); continue; }
            if (Time.unscaledTime > until)
            {
                _biosWanted.Remove(pc);
                Plugin.Logger.LogWarning($"[desktop] PC {pc}: this game's boot didn't reach the BIOS the other player opened.");
                _coop.ProjectionFailed($"BIOS on PC {pc} could not be opened here", new TimeoutException("boot did not enter BIOS"));
            }
        }
    }

    private bool BiosWantedFor(VirtualComputer vc)
    {
        if (_biosWanted.Count == 0 || vc == null) return false;
        foreach (var pc in _biosWanted.Keys)
            if (ComputerOf(_coop, pc)?.Pointer == vc.Pointer) return true;
        return false;
    }

    private LookingAtMonitorState StandIn()
    {
        if (_standIn == null)
        {
            _standIn = new LookingAtMonitorState(IL2CPP.il2cpp_object_new(Il2CppClassPointerStore<LookingAtMonitorState>.NativeClassPtr));
            _standIn.ctrl = GameController.Get();
        }
        return _standIn;
    }

    /// <summary>Called by the boot coroutine hook: returns the state to restore, or null when nothing was changed.</summary>
    private static State BeginBoot(VirtualComputer vc)
    {
        var self = _instance;
        if (self == null || !self.BiosWantedFor(vc)) return null;
        var gc = GameController.Get();
        if (gc == null) return null;
        var current = gc.m_currentState;
        gc.m_currentState = self.StandIn();
        _forceBiosKey = true;
        return current ?? self.StandIn(); // never return null when we changed something
    }

    private static void EndBoot(State restore)
    {
        if (restore == null) return;
        _forceBiosKey = false;
        var gc = GameController.Get();
        if (gc != null) gc.m_currentState = restore.Pointer == _instance?._standIn?.Pointer ? null : restore;
    }

    /// <summary>The BIOS key reads as held only inside a boot step this game is following.</summary>
    internal static bool ForceHeld(InputButton button) =>
        _forceBiosKey && button != null && (button.Pointer == PCBSInput.m_enterBios?.Pointer || button.Pointer == PCBSInput.m_enterBiosAlt?.Pointer);

    [HarmonyPatch(typeof(VirtualComputer._StartupSequence_d__69), nameof(VirtualComputer._StartupSequence_d__69.MoveNext))]
    internal static class BootFollow
    {
        private static void Prefix(VirtualComputer._StartupSequence_d__69 __instance, out State __state)
        {
            __state = null;
            try { __state = BeginBoot(__instance.__4__this); }
            catch (Exception e) { Plugin.Logger.LogError("[desktop] boot follow failed: " + e); }
        }

        private static void Postfix(State __state) => EndBoot(__state);
    }
}

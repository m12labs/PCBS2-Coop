using HarmonyLib;

namespace PCBS2Coop;

/// <summary>The power key while looking at a monitor: <c>LookingAtMonitorState.Tick</c> calls
/// <c>Motherboard.TurnOn/TurnOff</c> directly, past the case and work-HUD buttons that phase 5 shares. Inside that
/// Tick only, those calls become the same host-ordered power gesture (<c>NativeComponentEdits.RequestPower</c>).</summary>
internal sealed partial class NativeDesktopInput
{
    private static Case _tickCase;

    private static bool PowerBypass => _tickCase == null || NativeProjection.Active || !NativePcLifecycle.InCoopWorkshop;

    [HarmonyPatch(typeof(LookingAtMonitorState), nameof(LookingAtMonitorState.Tick))]
    internal static class MonitorTickScope
    {
        private static void Prefix(LookingAtMonitorState __instance) => _tickCase = __instance?.m_case;
        private static void Postfix() => _tickCase = null;
    }

    /// <summary>Start menu → Shut Down ends with <c>Motherboard.TurnOff</c>. This player's own click becomes the shared
    /// power-off; the other game's replay of the same click turns its copy off natively (projection bypass).</summary>
    [HarmonyPatch(typeof(StartMenu), nameof(StartMenu.OnShutdown))]
    internal static class StartMenuShutdownScope
    {
        private static void Prefix(StartMenu __instance)
        {
            if (Injecting || NativeProjection.Active) return;
            // This player's click: they are looking at that PC's monitor.
            try { _tickCase = GameController.Get()?.GetCurrentState()?.TryCast<LookingAtMonitorState>()?.m_case; }
            catch { _tickCase = null; }
        }
        private static void Postfix() => _tickCase = null;
    }

    [HarmonyPatch(typeof(Motherboard), nameof(Motherboard.TurnOn))]
    internal static class MonitorPowerOn
    {
        private static bool Prefix(Motherboard __instance)
        {
            if (PowerBypass) return true;
            var mesh = _tickCase; var board = __instance;
            _tickCase = null; // the replay below runs the native call again, outside this scope
            try { return CoopController.Instance?.Edits?.RequestPower(mesh, () => board.TurnOn()) ?? false; }
            catch (System.Exception e) { Plugin.Logger.LogError("[desktop] monitor power request failed: " + e); return false; }
        }
    }

    [HarmonyPatch(typeof(Motherboard), nameof(Motherboard.TurnOff))]
    internal static class MonitorPowerOff
    {
        private static bool Prefix(Motherboard __instance, bool isChangingPart)
        {
            if (PowerBypass) return true;
            var mesh = _tickCase; var board = __instance; var changing = isChangingPart;
            _tickCase = null;
            try { return CoopController.Instance?.Edits?.RequestPower(mesh, () => board.TurnOff(changing)) ?? false; }
            catch (System.Exception e) { Plugin.Logger.LogError("[desktop] monitor power request failed: " + e); return false; }
        }
    }
}

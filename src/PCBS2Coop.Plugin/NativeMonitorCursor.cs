using PCBS2Coop.Network;
using UnityEngine;

namespace PCBS2Coop;

/// <summary>Reads this actor's monitor cursor without writing the game's single local cursor.
/// The sampled position is transient; clicks and drag capture use a separate host-ordered path.</summary>
internal static class NativeMonitorCursor
{
    internal static MonitorCursor Observe(CoopController coop)
    {
        if (!coop.WorldReady || coop.Objects.Initialized != true) return default;
        var state = GameController.Get()?.GetCurrentState()?.TryCast<LookingAtMonitorState>();
        if (state == null || state.m_case == null || state.m_ws == null) return default;
        var pc = coop.Objects.IdOf(state.m_case);
        if (pc.IsNone) return default;
        var vc = state.m_ws.m_virtualComputer;
        if (vc == null || coop.Objects.FindBench(pc)?.m_workStation?.m_virtualComputer?.Pointer != vc.Pointer)
            return default;
        try
        {
            var os = vc.m_os;
            // The monitor state can enter before the OS canvas/raycaster exists. A BIOS or
            // boot screen also uses this state but is not the shared desktop overlay.
            if (!vc.IsPoweredOn() || vc.IsInBios() || os?.GetRaycaster() == null || os.m_desktop?.canvas == null)
                return default;
            var pos = InputModule.GetLocalCursorPos();
            var curved = os?.m_monitorDisplay;
            if (curved != null && curved.enabled && curved.gameObject.activeInHierarchy)
            {
                var cam = state.Camera;
                if (cam == null || !curved.Raycast(cam.ScreenPointToRay(pos), out var hit, 100f)) return default;
                return Cursor(pc, hit.textureCoord.x, hit.textureCoord.y);
            }
            var display = state.m_displayRect;
            if (display == null || !RectTransformUtility.ScreenPointToLocalPointInRectangle(display, pos, state.Camera, out var local))
                return default;
            var rect = display.rect;
            if (rect.width <= 0 || rect.height <= 0) return default;
            return Cursor(pc, (local.x - rect.xMin) / rect.width, (local.y - rect.yMin) / rect.height);
        }
        catch { return default; } // A monitor can be rebinding during a peripheral swap or PC reload.
    }

    private static MonitorCursor Cursor(NetObjectId pc, float x, float y)
    {
        var cursor = new MonitorCursor(pc, x, y, true);
        return cursor.IsValid ? cursor : default;
    }
}

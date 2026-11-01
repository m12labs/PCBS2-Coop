using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>Reads this actor's pointer on a shared PC desktop for the other player's label. It never writes the
/// game's single local cursor; clicks go through <see cref="NativeDesktopInput"/>.</summary>
internal static class NativeMonitorCursor
{
    internal static MonitorCursor Observe(CoopController coop)
    {
        if (!coop.WorldReady || coop.Objects.Initialized != true) return default;
        try
        {
            var (pc, vc, state) = NativeDesktopInput.LocalMonitor(coop);
            // The labels are drawn on the desktop only; BIOS and boot screens show the game's own pointer.
            if (vc == null || NativeDesktopInput.SurfaceOf(vc) != DesktopSurface.Desktop) return default;
            if (!NativeDesktopInput.LocalPoint(vc, DesktopSurface.Desktop, state, out var x, out var y)) return default;
            var cursor = new MonitorCursor(pc, UnityEngine.Mathf.Clamp01(x), UnityEngine.Mathf.Clamp01(y), true);
            return cursor.IsValid ? cursor : default;
        }
        catch { return default; } // A monitor can be rebinding during a peripheral swap or PC reload.
    }
}

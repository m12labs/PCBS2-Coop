using HarmonyLib;

namespace PCBS2Coop;

/// <summary>Native calls made by an accepted change (inside WorldSync's Apply). Interception and the movement guards
/// let them through. The native Free Build autosave such a call would trigger (a PC dropped on a bench) is skipped
/// in co-op, like every game autosave (<see cref="CoopController.AllowGameAutosave"/>), so no save runs inside Apply.</summary>
internal static class NativeProjection
{
    public static bool Active { get; private set; }
    internal static WorkStation Workstation { get; set; }

    public static void Run(Action call) => Run(() => { call(); return true; });

    public static T Run<T>(Func<T> call, WorkStation station = null)
    {
        var outer = Active;
        var previous = Workstation;
        Active = true;
        if (station != null) Workstation = station;
        try { return call(); }
        finally { Active = outer; Workstation = previous; }
    }

    [HarmonyPatch(typeof(WorkStation), nameof(WorkStation.GetGetActiveWorkStation))]
    internal static class WorkstationGuard
    {
        private static bool Prefix(ref WorkStation __result)
        {
            if (Workstation == null) return true;
            __result = Workstation;
            return false;
        }
    }
}

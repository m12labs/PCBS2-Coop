using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>Native calls made by an accepted change (inside WorldSync's Apply). Interception and the movement guards
/// let them through. The native Free Build autosave a drop triggers is deferred to the next frame: a save captures
/// and may submit changes, which must never happen from inside Apply.</summary>
internal static class NativeProjection
{
    public static bool Active { get; private set; }
    private static bool _autosaveDeferred;

    public static void Run(Action call)
    {
        var outer = Active;
        Active = true;
        try { call(); }
        finally { Active = outer; }
    }

    /// <summary>Per frame, outside Apply: run an autosave a projection asked for.</summary>
    public static void Update()
    {
        if (!_autosaveDeferred || Active) return;
        // A completed native load can still be unwinding its queued work. Keep the autosave pending until the
        // host's capture barrier is ready, rather than dropping it when CreateSaveGame refuses that frame.
        if (CoopController.Instance is { WorldReady: false, Session.Role: PCBS2Coop.Network.Role.Host }) return;
        // A latch still moving (4d) or a held selection would refuse the save; keep it pending until they settle.
        if (CoopController.Instance is { Session.Role: PCBS2Coop.Network.Role.Host } && !NativeAssembly.CanCapture) return;
        _autosaveDeferred = false;
        try { Object.FindObjectOfType<SaveLoadSystem>()?.FreebuildSave(); }
        catch (Exception e) { Plugin.Logger.LogError("[objects] deferred autosave failed: " + e); }
    }

    public static void Reset() => _autosaveDeferred = false;

    [HarmonyPatch(typeof(SaveLoadSystem), nameof(SaveLoadSystem.FreebuildSave))]
    internal static class AutosaveDeferral
    {
        private static bool Prefix()
        {
            if (!Active) return true;
            _autosaveDeferred = true;
            return false;
        }
    }
}

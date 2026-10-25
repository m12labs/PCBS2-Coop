using HarmonyLib;

namespace PCBS2Coop;

/// <summary>Hooks into game methods. Each hook only observes; none changes what the game does.</summary>
internal static class GamePatches
{
    public static void Apply()
    {
        try
        {
            new Harmony(Plugin.Guid + ".game").PatchAll(typeof(SaveHook));
            Plugin.Logger.LogInfo("Game hooks installed (save).");
        }
        catch (Exception e) { Plugin.Logger.LogError("Game hooks failed: " + e); }
    }

    /// <summary>Every game save (manual, auto, Free Build) goes through CreateSaveGame. The host sends its saves to the guest as copies.</summary>
    [HarmonyPatch(typeof(SaveLoadSystem), nameof(SaveLoadSystem.CreateSaveGame), typeof(string), typeof(bool), typeof(string))]
    private static class SaveHook
    {
        private static void Postfix(string __result)
        {
            try
            {
                if (CoopController.TakingSnapshot || CoopSaves.Current != CoopSaves.Mode.Host) return;
                CoopController.Instance?.OnHostSaved(__result);
            }
            catch (Exception e) { Plugin.Logger.LogError(e); }
        }
    }
}

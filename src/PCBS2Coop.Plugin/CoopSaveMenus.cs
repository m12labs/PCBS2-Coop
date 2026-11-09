using HarmonyLib;

namespace PCBS2Coop;

/// <summary>
/// The pause menu's Save and Load screen and Free Build Options' Reset in a co-op world (owner decisions, phase 8):
/// <list type="bullet">
/// <item>Host: Save works as before and makes a saved copy (autosaves stay with the world being played). Load and Reset
/// reload both games into the chosen save or the default build, like /sync (<see cref="CoopController.StartWorldSwitch"/>).</item>
/// <item>Guest: Save, Load and Reset are refused with a notice. Only the host saves the co-op world.</item>
/// </list>
/// Solo games and the main menu are untouched (no co-op save folder is active there).
/// Decompile: Load is <c>SaveLoadMenu.OnLoadSelected</c> → <c>LevelLoadPersistency.LoadGameFromSaveLoadMenu</c>; a new
/// save is <c>SaveLoadMenu.NewSaveGame</c>, overwriting one <c>OverwriteSave</c> (the same native function); Reset is the <c>FreebuildOptions</c>
/// confirm lambda → <c>LevelLoadPersistency.ResetFreebuild</c>.
/// </summary>
internal sealed partial class CoopController
{
    private const string GuestSaveRefusal = "Only the host saves and loads the co-op world.";

    private static bool GuestInWorld => CoopSaves.Current == CoopSaves.Mode.Guest;

    /// <summary>Pause-menu Load: <paramref name="saveName"/> is the save list's name. Returns whether the game's own
    /// load may run (only outside co-op).</summary>
    internal bool OnPauseMenuLoad(string saveName)
    {
        if (GuestInWorld) { Notice(GuestSaveRefusal); return false; }
        if (CoopSaves.Current != CoopSaves.Mode.Host) return true;
        if (StartWorldSwitch(saveName) is { } why) Notice($"Can't load that world now: {why}.");
        return false;
    }

    /// <summary>Free Build Options' Reset. Returns whether the game's own reset may run (only outside co-op).</summary>
    internal bool OnFreeBuildReset()
    {
        if (GuestInWorld) { Notice("Only the host can reset the co-op world."); return false; }
        if (CoopSaves.Current != CoopSaves.Mode.Host || !CoopRunner.InWorkshop) return true;
        if (StartWorldSwitch(null) is { } why) Notice($"Can't reset the world now: {why}.");
        return false;
    }

    [HarmonyPatch(typeof(SaveLoadMenu), nameof(SaveLoadMenu.OnLoadSelected))]
    internal static class PauseLoad
    {
        private static bool Prefix(string filename)
        {
            try { return Instance?.OnPauseMenuLoad(filename) ?? true; }
            catch (Exception e) { Plugin.Logger.LogError("[saves] pause-menu load: " + e); return CoopSaves.Current == CoopSaves.Mode.None; }
        }
    }

    [HarmonyPatch(typeof(LevelLoadPersistency), nameof(LevelLoadPersistency.ResetFreebuild))]
    internal static class FreeBuildReset
    {
        private static bool Prefix()
        {
            try { return Instance?.OnFreeBuildReset() ?? true; }
            catch (Exception e) { Plugin.Logger.LogError("[saves] Free Build reset: " + e); return CoopSaves.Current == CoopSaves.Mode.None; }
        }
    }

    /// <summary>A new save and overwriting one: <c>NewSaveGame</c> and <c>OverwriteSave</c> are one native function in
    /// the game build (same address), so this one hook covers both buttons. Never hook the other one too.</summary>
    [HarmonyPatch(typeof(SaveLoadMenu), nameof(SaveLoadMenu.NewSaveGame))]
    internal static class GuestNewSave
    {
        private static bool Prefix()
        {
            if (!GuestInWorld) return true;
            Instance?.Notice(GuestSaveRefusal);
            return false;
        }
    }
}

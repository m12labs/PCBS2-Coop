using HarmonyLib;
using UnityEngine;
using PCBS2Coop.Network;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// When a co-op world is saved (owner decisions, October 30, 2026). The game's own Free Build autosaves are skipped in a
/// co-op world on both games: every <c>SaveLoadSystem.FreebuildSave</c> call (decompile: leaving work mode after each
/// edit in <c>WalkingState.OnStateEnter</c>, a PC dropped on a bench in <c>WorkshopController.DropCase</c>, a decoration
/// switch, and the saves right after a load in <c>WalkingState.WaitForLoad</c> and <c>NewLoadingScreen.LoadASync</c>).
/// Each of those host saves captured the whole world and sent the guest a copy. The host saves instead:
/// <list type="bullet">
/// <item>on an interval (Co-op Options: off, 2, 5 or 10 minutes, default 5), skipped while nothing changed;</item>
/// <item>when it leaves the world through the pause menu's Exit (the exit waits for the save, up to a few seconds);</item>
/// <item>from the game's Save screen, as before (it calls <c>CreateSaveGame</c> directly).</item>
/// </list>
/// The mod's own saves call <c>CreateSaveGame</c> with the Free Build autosave name, which the save hook redirects to the
/// world's own autosave file. The guest keeps only the copies the host sends.
/// </summary>
internal sealed partial class CoopController
{
    private const float AutosaveRetrySeconds = 5f, ExitSaveWaitSeconds = 8f;
    private float _autosaveDue = -1;
    /// <summary>The world revision of the last host save, or null before one in this world.</summary>
    private ulong? _savedRevision;
    private int _hostSaves;
    /// <summary>The pause menu's Exit, held until the leaving save is written.</summary>
    private ExitPanel _exitPanel;
    private float _exitGiveUp;
    private bool _exitSaveStarted, _exiting;

    /// <summary>The game's Free Build autosave: refused in a co-op world (both roles).</summary>
    internal static bool AllowGameAutosave()
    {
        if (CoopSaves.Current == CoopSaves.Mode.None) return true;
        Plugin.Logger.LogInfo("[saves] skipped the game's own autosave (co-op saves on its interval, on Exit and from the Save screen).");
        return false;
    }

    private void ResetAutosave()
    {
        _autosaveDue = -1; _savedRevision = null; _exitPanel = null; _exitSaveStarted = false; _exiting = false;
    }

    private bool HostInWorld => Session.Role == Role.Host && CoopSaves.Current == CoopSaves.Mode.Host && _hostWorldStarted && _readiness.Ready &&
        Session.World is { Admitted: true };

    /// <summary>Why the host can't save its world right now, or null.</summary>
    private string SaveBlocker()
    {
        if (!HostInWorld) return "the world isn't ready";
        if (SaveLoadSystem.WaitingOnThread() || _pairWrites.Count > 0 || _pendingFile != null) return "the previous save is still being written";
        if (_sync != SyncStep.None || Session.GuestNeedsWorld) return "a sync or a joining player's world copy is in progress";
        if (_initialSave != null) return "the new world's first save is still pending";
        if (OutOfSync != null) return "this game is out of sync";
        if (Workstations?.Settled == false || !NativeAssembly.CanCapture) return "a change is still being shown";
        return null;
    }

    /// <summary>Host: save the co-op world now (to its autosave file). Returns whether the save started.</summary>
    internal bool SaveWorld(string why, bool quiet = false)
    {
        if (SaveBlocker() is { } blocked) { if (!quiet) Plugin.Logger.LogInfo($"[saves] {why} save waits: {blocked}."); return false; }
        var saves = Object.FindObjectOfType<SaveLoadSystem>();
        if (saves == null) return false;
        var before = _hostSaves;
        try { saves.CreateSaveGame(SaveLoadSystem.s_autoFreeBuildSaveName, false, ""); }
        catch (Exception e) { Plugin.Logger.LogError($"[saves] {why} save failed: {e}"); return false; }
        var started = _hostSaves != before;
        Plugin.Logger.LogInfo(started ? $"[saves] {why} save started." : $"[saves] {why} save was refused (see the save guard above).");
        return started;
    }

    /// <summary>Every host save of the world (the mod's, the Save screen's, a /sync's): the interval starts again.</summary>
    private void HostSaveWritten(ulong revision)
    {
        _hostSaves++;
        _savedRevision = revision;
        var minutes = CoopSettings.AutosaveMinutes;
        _autosaveDue = minutes > 0 ? Time.realtimeSinceStartup + minutes * 60f : -1;
    }

    /// <summary>Per frame on the host while in the workshop.</summary>
    private void UpdateAutosave()
    {
        if (!HostInWorld) { _autosaveDue = -1; return; }
        if (_exitPanel != null) { UpdateExitSave(); return; }
        var minutes = CoopSettings.AutosaveMinutes;
        var now = Time.realtimeSinceStartup;
        if (minutes <= 0) { _autosaveDue = -1; return; }
        if (_autosaveDue < 0 || _autosaveDue > now + minutes * 60f) { _autosaveDue = now + minutes * 60f; return; }
        if (now < _autosaveDue) return;
        if (_savedRevision == Session.World!.Revision)
        {
            _autosaveDue = now + minutes * 60f;
            Plugin.Logger.LogInfo("[saves] autosave skipped: nothing changed since the last save.");
            return;
        }
        if (SaveWorld("autosave")) AddChat("World saved.");
        else _autosaveDue = now + AutosaveRetrySeconds;
    }

    /// <summary>The pause menu's Exit while hosting: save first, then exit once the save is written. Returns whether
    /// the exit may run now.</summary>
    internal bool BeforeExit(ExitPanel panel)
    {
        if (_exiting || !HostInWorld) return true;
        if (_savedRevision == Session.World!.Revision && !SaveLoadSystem.WaitingOnThread() && _pairWrites.Count == 0)
        {
            Plugin.Logger.LogInfo("[saves] leaving: nothing changed since the last save.");
            return true;
        }
        if (_exitPanel != null) return false; // already saving for this exit
        _exitPanel = panel; _exitGiveUp = Time.realtimeSinceStartup + ExitSaveWaitSeconds; _exitSaveStarted = false;
        Notice("Saving the world before leaving...");
        if (SaveBlocker() is { } blocked) Plugin.Logger.LogInfo($"[saves] leaving save waits: {blocked}.");
        UpdateExitSave();
        return false;
    }

    private void UpdateExitSave()
    {
        var now = Time.realtimeSinceStartup;
        if (!_exitSaveStarted) _exitSaveStarted = SaveWorld("leaving", quiet: true);
        var written = _exitSaveStarted && !SaveLoadSystem.WaitingOnThread() && _pairWrites.Count == 0;
        if (!written && now < _exitGiveUp) return;
        if (!written) Plugin.Logger.LogWarning($"[saves] leaving without a final save: {(_exitSaveStarted ? "it didn't finish in time" : SaveBlocker() ?? "it was refused")}.");
        var panel = _exitPanel;
        _exitPanel = null;
        _exiting = true;
        try { if (panel != null) panel.Exit(); }
        catch (Exception e) { Plugin.Logger.LogError("[saves] exit after the leaving save failed: " + e); }
        finally { _exiting = false; }
    }

    /// <summary>The game's Free Build autosave entry point.</summary>
    [HarmonyPatch(typeof(SaveLoadSystem), nameof(SaveLoadSystem.FreebuildSave))]
    internal static class GameAutosaveGuard
    {
        private static bool Prefix() => AllowGameAutosave();
    }

    /// <summary>The pause menu's Exit (to the main menu), the host's way out of a co-op world.</summary>
    [HarmonyPatch(typeof(ExitPanel), nameof(ExitPanel.Exit))]
    internal static class ExitSave
    {
        private static bool Prefix(ExitPanel __instance)
        {
            try { return Instance?.BeforeExit(__instance) ?? true; }
            catch (Exception e) { Plugin.Logger.LogError("[saves] leaving save failed: " + e); return true; }
        }
    }
}

using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using PCBS2Coop.Network;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// The native shop renovation in a co-op Career (phase 17d, owner decisions November 10, 2026). Either player may
/// order it from the inbox (<see cref="NativeJobs"/>). The game builds it at the next day end (decompile):
/// <list type="number">
/// <item><c>ShopUpgradeEmail.OnPositiveAction</c> pays and adds <c>DoBuild</c> to <c>CareerStatus.s_onDayEnd</c>.</item>
/// <item>At the day end <c>DoBuild</c> sets <c>m_upgradedShop</c> and adds <c>CareerStatus.UpgradeShop</c> to
/// <c>s_onDayEndPostSave</c>, which the day iterator runs after its autosave (skipped in co-op).</item>
/// <item><c>UpgradeShop</c> updates the bench PCs' apps and the rent, adds <c>PostShopRennovation</c> to
/// <c>SceneManager.sceneLoaded</c> (it spawns the new workstations' peripherals), saves with <c>CreateSaveGame("", true)</c>
/// and loads that save in <c>Stage_02_Workshop</c>.</item>
/// </list>
/// In co-op the host's game holds <c>UpgradeShop</c> until the day has settled, then runs it as a host world change
/// like /sync: Client leaves to its menu, the host runs <c>UpgradeShop</c> (its save goes to the world's autosave, paired
/// with the renovated scene; its scene load is skipped), returns to its menu and loads that save in the renovated
/// workshop. The old shop's bench bindings don't fit the new scene, so that load starts a fresh world generation and
/// binds the shared IDs from the game, as a new career does; the host saves the new world at once. Client is then
/// admitted from a fresh snapshot of the renovated workshop. <c>PostShopRennovation</c> waits for the workshop scene.
/// </summary>
internal sealed partial class CoopController
{
    private const float RenovationRetrySeconds = 5f;
    /// <summary>What the guest is told when the host renovates (matched in <see cref="OnGuestSyncStarted"/>).</summary>
    private const string GuestRenovation = "the host's shop is being renovated";

    /// <summary>The game's own <c>UpgradeShop</c> is running for the mod: its save goes to the world's autosave and
    /// its scene load is skipped (the mod loads the save itself).</summary>
    internal static bool RenovationRunning;
    private bool _renovationDue, _renovating, _saveAfterRenovation;
    private float _renovationAt;
    private string _renovationSave;

    private void ResetRenovation()
    {
        _renovationDue = false; _renovating = false; _saveAfterRenovation = false; _renovationSave = null; RenovationRunning = false;
    }

    /// <summary>Host: the day end queued the renovation. Returns whether it is held for the mod to run.</summary>
    internal bool QueueRenovation()
    {
        if (!HostInWorld) return false;
        if (!_renovationDue) AddChat("The shop renovation starts once the day has finished: both games then load the renovated workshop.");
        _renovationDue = true;
        _renovationAt = Time.realtimeSinceStartup + DaySettleSeconds;
        Plugin.Logger.LogInfo("[career] the day end queued the shop renovation; it runs once the day has settled.");
        return true;
    }

    /// <summary>Host, each frame: start the held renovation, and save the renovated world once it is loaded.</summary>
    private void UpdateRenovation()
    {
        var now = Time.realtimeSinceStartup;
        if (_saveAfterRenovation && _hostWorldStarted && SaveWorld("renovated shop", quiet: true)) _saveAfterRenovation = false;
        if (!_renovationDue || _day != DayStep.None || _sync != SyncStep.None || now < _renovationAt) return;
        var level = PCBS.Singleton<LevelLoadPersistency>.Instance;
        string why = null;
        if (level != null && level.isLevelLoading) why = "the game is loading";
        else if (!_hostWorldStarted || !CoopRunner.InWorkshop) why = "the world is still loading";
        else if (_pendingFile != null || (Session.IsJoined && Session.GuestState == GuestState.Loading)) why = "Client is still loading the world";
        if (why != null)
        {
            _renovationAt = now + RenovationRetrySeconds;
            Plugin.Logger.LogInfo($"[career] the shop renovation waits: {why}.");
            return;
        }
        _syncRequestUntil = -1;
        _renovating = true;
        _syncGuestLeft = Session.BeginResync(GuestRenovation);
        _copiesToSend.Clear();
        _sync = SyncStep.Saving;
        _syncDeadline = now + SyncSaveTimeout;
        AddChat("Renovating the shop: saving the career, then both games load the renovated workshop.");
        Plugin.Logger.LogInfo($"[career] shop renovation started; guest told to leave: {_syncGuestLeft}.");
    }

    /// <summary>The renovation's save step: the game's own <c>UpgradeShop</c>, with its save redirected and its load held.</summary>
    private void SaveForRenovation()
    {
        var status = CareerStatus.Get();
        if (status == null) { FailSync("the career isn't loaded"); return; }
        _saveError = null; _renovationSave = null;
        // As for /sync: the old world's claims end; nothing stale enters the save.
        Session.World.Submit(CommandTypes.ActorActivity, ActivityWorld.Presence(ActorSlot.Guest, default), out _);
        Session.World.Submit(CommandTypes.ActorActivity, ActivityWorld.Presence(ActorSlot.Host, Session.LocalIdentity.Player), out _);
        _syncSaving = true; RenovationRunning = true;
        try { status.UpgradeShop(); }
        catch (Exception e) { _saveError ??= e.Message; }
        finally { RenovationRunning = false; _syncSaving = false; }
        // The game ran it (and took it off the day-end list); it isn't retried.
        _renovationDue = false;
        if (string.IsNullOrEmpty(_renovationSave) || _saveError != null)
        {
            Plugin.Logger.LogError($"[career] the shop renovation's save failed: {_saveError ?? "no save was written"}.");
            FailSync(_saveError ?? "the renovation's save did not start");
            return;
        }
        if (CoopSaves.Campaign is { } campaign) CoopSaves.SetCampaign(campaign.InScene(CoopCampaign.CareerRenovatedScene));
        _syncFile = _renovationSave;
        _sync = SyncStep.Pairing;
        _syncDeadline = Time.realtimeSinceStartup + SyncPairTimeout;
        Plugin.Logger.LogInfo($"[career] the shop renovation saved '{_renovationSave}'.");
    }

    /// <summary>On the main menu after the renovation's save: load it in the renovated workshop as a fresh generation.</summary>
    private void ReloadForRenovation()
    {
        var fileName = Path.GetFileName(_syncFile);
        var bare = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;
        var name = CoopSaves.FindSaveName(f => f.Equals(fileName, StringComparison.OrdinalIgnoreCase) || f.Equals(bare, StringComparison.OrdinalIgnoreCase))
                   ?? throw new FileNotFoundException("The renovated career isn't in the save list.", fileName);
        var campaign = CoopSaves.Campaign ?? throw new InvalidOperationException("No co-op run is being hosted.");
        // The save's shared IDs are bound to the old shop's benches: bind them afresh from the loaded game.
        _loadingPair = null;
        _hostWorldStarted = false;
        _hostWorldKind = "co-op career after the shop renovation";
        _readiness.Reset();
        Objects.Reset(); Activity?.Reset(); Workstations?.Reset(); ResetAutosave(); DestroyView();
        _guestPoses.Clear(); // another workshop layout: where Client stood no longer applies
        Plugin.Logger.LogInfo($"[career] host loads '{name}' in {campaign.Scene} after the shop renovation.");
        LoadExact(name, campaign);
    }

    /// <summary>The run's scene as the renovation's save records it.</summary>
    private static CoopCampaign RenovationCampaign(CoopCampaign campaign) =>
        Instance is { _renovating: true } && campaign.Mode == CoopGameMode.Career ? campaign.InScene(CoopCampaign.CareerRenovatedScene) : campaign;

    /// <summary>The native shop renovation: held for the mod on the host (see the class summary); never on the guest.</summary>
    [HarmonyPatch(typeof(CareerStatus), nameof(CareerStatus.UpgradeShop))]
    internal static class ShopRenovationGuard
    {
        private static bool Prefix()
        {
            if (CoopSaves.Current == CoopSaves.Mode.None || RenovationRunning) return true;
            try
            {
                if (CoopSaves.Current == CoopSaves.Mode.Host && Instance?.QueueRenovation() == true) return false;
            }
            catch (Exception e) { Plugin.Logger.LogError("[career] could not hold the shop renovation: " + e); }
            Plugin.Logger.LogWarning("[career] refused the native shop renovation here: only the host's game runs it, as a shared world change.");
            return false;
        }
    }

    /// <summary>The renovation's own scene load: skipped; the mod loads the save from its menu.</summary>
    [HarmonyPatch(typeof(LevelLoadPersistency), nameof(LevelLoadPersistency.LoadGameFromDir))]
    internal static class RenovationLoad
    {
        private static bool Prefix(string saveName, string scene)
        {
            if (!RenovationRunning) return true;
            Plugin.Logger.LogInfo($"[career] the renovation's load of '{saveName}' in {scene} is left to the co-op reload.");
            return false;
        }
    }

    /// <summary><c>PostShopRennovation</c> runs on the first scene loaded after the renovation and then removes itself.
    /// The co-op reload passes the main menu first, so it waits for the workshop scene.</summary>
    [HarmonyPatch(typeof(CareerStatus), nameof(CareerStatus.PostShopRennovation))]
    internal static class RenovationPeripherals
    {
        private static bool Prefix(Scene scene)
        {
            if (CoopSaves.Current == CoopSaves.Mode.None) return true;
            if (scene.name == CoopCampaign.CareerRenovatedScene) { Plugin.Logger.LogInfo("[career] the renovated workshop is loaded; the game sets up its workstations."); return true; }
            Plugin.Logger.LogInfo($"[career] the renovation's workstation setup waits for the workshop (loaded {scene.name}).");
            return false;
        }
    }
}

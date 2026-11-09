using UnityEngine;
using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>
/// Phase 15c, host: after its day has finished, save the career and give the guest the new day (owner decisions,
/// November 8, 2026). Until phase 17 shares the day itself, the guest catches up by loading the host's world again,
/// like a short rejoin; the host keeps playing. The save waits for the usual save guard (nothing mid-change).
/// </summary>
internal sealed partial class CoopController
{
    private const float DaySettleSeconds = 4f, DaySaveGiveUp = 45f;
    private enum DayStep { None, Settling, Saving }
    private DayStep _day;
    private float _dayAt, _dayGiveUp;
    private bool _daySaveStarted;
    public NativeCareer Career { get; private set; }
    /// <summary>Phase 16b: shared carts, checkout and part sales.</summary>
    public NativeCommerce Commerce { get; private set; }

    /// <summary>Host check of the owned item an install names (a method group: the plugin builds without nullable attributes).</summary>
    private string CheckCareerItem(ulong item, IEnumerable<ulong> inUse) => NativeCareer.InCareer ? Career?.Handler.CheckUse(item, inUse) : null;

    /// <summary>Host check of the owned case item a new PC is made from.</summary>
    private string CheckCareerCase(ulong item) => NativeCareer.InCareer ? Career?.Handler.CheckUse(item, Edits?.Handler.ItemsInUse(default) ?? Enumerable.Empty<ulong>()) : null;

    private void ResetDayEnd() { _day = DayStep.None; _daySaveStarted = false; }

    private void UpdateDayEnd()
    {
        if (Career == null || Session.Role != Role.Host || !HostInWorld) { ResetDayEnd(); return; }
        var now = Time.realtimeSinceStartup;
        if (Career.TakeDayEnded())
        {
            _day = DayStep.Settling; _dayAt = now + DaySettleSeconds; _dayGiveUp = now + DaySaveGiveUp; _daySaveStarted = false;
            Plugin.Logger.LogInfo("[career] the host's day finished; saving and sending the new day once it settles.");
        }
        if (_day == DayStep.None || now < _dayAt) return;
        var level = PCBS.Singleton<LevelLoadPersistency>.Instance;
        if (level != null && level.isLevelLoading) { _dayAt = now + 1; return; }
        if (_day == DayStep.Settling)
        {
            _daySaveStarted = SaveWorld("day end", quiet: true);
            if (_daySaveStarted || now > _dayGiveUp)
            {
                if (!_daySaveStarted) Plugin.Logger.LogWarning($"[career] the day-end save didn't run ({SaveBlocker() ?? "refused"}); sending the new day anyway.");
                _day = DayStep.Saving;
            }
            else _dayAt = now + 1;
            return;
        }
        if (_daySaveStarted && (SaveLoadSystem.WaitingOnThread() || _pairWrites.Count > 0) && now < _dayGiveUp) return;
        ResetDayEnd();
        if (_daySaveStarted) AddChat("Day saved.");
        if (!Session.IsJoined || Session.GuestState != GuestState.InWorld) return;
        // The guest's claims end with its stay in this copy of the world; it comes back with a new presence.
        Session.World!.Submit(CommandTypes.ActorActivity, ActivityWorld.Presence(ActorSlot.Guest, default), out _);
        if (Session.BeginResync(GuestDayReload)) AddChat("Sending Client the new day.");
        else Plugin.Logger.LogWarning("[career] could not send the guest the new day (it is busy); it keeps the old day until /sync.");
    }

    /// <summary>What the guest is told when the host's day finished (matched in <see cref="OnGuestSyncStarted"/>).</summary>
    private const string GuestDayReload = "the host finished the day";
}

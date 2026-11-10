using UnityEngine;
using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>
/// Phase 15c, host: after its day has finished, save the career and give the guest the new day (owner decisions,
/// November 8, 2026). The guest catches up by loading the host's world again, like a short rejoin; the host keeps
/// playing. The save waits for the usual save guard (nothing mid-change). Since 0.65 (17d) the day ends only when both
/// players are ready (<see cref="NativeDay"/>), and a day end that builds the shop renovation runs it instead
/// (<see cref="UpdateRenovation"/>), which reloads both games.
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
    /// <summary>Phase 16c: packages and physical job notes.</summary>
    public NativePackages Packages { get; private set; }
    /// <summary>Phase 17b: shared jobs and the inbox.</summary>
    public NativeJobs Jobs { get; private set; }
    /// <summary>Phase 17d: the guest's ready mark for ending the day.</summary>
    public NativeDay Day { get; private set; }
    /// <summary>Phase 17e: tutorials both players go through together.</summary>
    public NativeOnboarding Onboarding { get; private set; }

    /// <summary>Host: its day finished and the day-end save and guest reload are still running.</summary>
    internal bool DayEnding => _day != DayStep.None;

    /// <summary>Host check of the owned item an install names (a method group: the plugin builds without nullable attributes).</summary>
    private string CheckCareerItem(ulong item, IEnumerable<ulong> inUse) => NativeCareer.InCareer ? Career?.Handler.CheckUse(item, inUse) : null;

    /// <summary>Host check of the owned case item a new PC is made from.</summary>
    private string CheckCareerCase(ulong item) => NativeCareer.InCareer ? Career?.Handler.CheckUse(item, Edits?.Handler.ItemsInUse(default) ?? Enumerable.Empty<ulong>()) : null;

    private void ResetDayEnd() { _day = DayStep.None; _daySaveStarted = false; }

    /// <summary>Host (phase 16d): a PC its game made by itself (PC Bay, a customer's PC arriving) has no shared IDs
    /// yet. Saves and a guest's world copy wait for it: the capture refuses unshared PCs.</summary>
    private bool UnsharedGamePcs => NativeCareer.InCareer && (Lifecycle?.Adopting == true || Objects?.Initialized == true && Objects.UnboundHostPcs().Count > 0);

    private void UpdateDayEnd()
    {
        if (Career == null || Session.Role != Role.Host) { ResetDayEnd(); return; }
        UpdateRenovation();
        if (!HostInWorld) { ResetDayEnd(); return; }
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
            if (_renovationDue)
            {
                // The renovation's own save and reload give both games the new day.
                ResetDayEnd();
                _renovationAt = Math.Min(_renovationAt, now);
                return;
            }
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

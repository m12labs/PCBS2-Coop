using UnityEngine;
using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>
/// Phase 17d (owner decisions, November 10, 2026): the day ends only when both players are ready (<see cref="DayWorld"/>,
/// command 22).
/// <list type="bullet">
/// <item>The guest's End Day or Skip Work (<c>DaySummary.OnEndDay</c> / <c>OnSkipWork</c>) marks it ready for the day
/// its game shows, instead of running. The host's game refuses while the guest still carries or works on something.</item>
/// <item>The host's End Day or Skip Work is refused while the guest in its world isn't ready, with a notice naming
/// Client; the guest is asked to press End Day. Once Client is ready the host is told and presses again.</item>
/// <item>The guest starting something new takes the ready mark back (decided by the model on both games).</item>
/// </list>
/// The guest still takes the new day by reloading the host's world after it (phase 15c).
/// </summary>
internal sealed class NativeDay : IDayNative
{
    private const float AskGuestEvery = 15f, PendingTimeout = 20f;

    private readonly CoopController _coop;
    private readonly WorldSync _world;
    public DayWorld Handler { get; }
    internal static NativeDay Current => CoopController.Instance?.Day;

    private OperationId? _pending;
    private float _pendingAt, _askedAt = -100f;

    public NativeDay(CoopController coop, WorldSync world)
    {
        _coop = coop; _world = world;
        Handler = new DayWorld(coop.Activity, this) { ReadyCheck = CheckReady };
        world.GenerationStarted += (uint _, string _) => { _pending = null; _askedAt = -100f; };
        world.Rejected += OnRejected;
    }

    private bool Active => NativeCareer.InCareer && Handler.Bound && _world.Admitted && _world.Divergence == null && _coop.WorldReady;
    private ulong Epoch => _coop.Activity.Get(_world.Local)?.Epoch ?? 0;
    internal static int Today => CareerStatus.Get()?.GetCalendar()?.GetToday() ?? 0;

    internal void BindHost() { if (NativeCareer.InCareer) Handler.BindHost(); }

    internal void BindGuest()
    {
        if (NativeCareer.InCareer && !Handler.Bound) Plugin.Logger.LogWarning("[day] the host's world has no shared day state; End Day stays with the host.");
    }

    public void Update()
    {
        if (_pending != null && Time.realtimeSinceStartup - _pendingAt > PendingTimeout) { _pending = null; Plugin.Logger.LogWarning("[day] the ready mark wasn't answered; continuing."); }
    }

    // ---------------- guest ----------------

    /// <summary>Guest: its End Day (or Skip Work) marks it ready instead of ending the day.</summary>
    internal void GuestPressed(bool skip)
    {
        if (!Active) { _coop.Notice("The shared day isn't ready on this game yet; try again in a moment."); return; }
        var day = Today;
        if (Handler.GuestReady(day)) { _coop.Notice("You're ready to end the day. The host ends it."); return; }
        if (_pending != null) { _coop.Notice("Still waiting for the host."); return; }
        if (!_world.Submit(CommandTypes.Day, DayWorld.Ready(Epoch, day), out var op)) { _coop.Notice("Not connected to the host."); return; }
        _pending = op; _pendingAt = Time.realtimeSinceStartup;
        Plugin.Logger.LogInfo($"[day] asked to be ready to end day {day} ({(skip ? "Skip Work" : "End Day")}).");
    }

    private void OnRejected(OperationId op, RejectReason reason, string text)
    {
        if (_pending != op) return;
        _pending = null;
        const string prefix = "day: ";
        if (text.StartsWith(prefix, StringComparison.Ordinal)) text = text[prefix.Length..];
        _coop.Notice("You can't be ready to end the day yet: " + text);
        Plugin.Logger.LogInfo("[day] the ready mark was refused: " + text);
    }

    // ---------------- host ----------------

    private bool GuestInWorld => _coop.Session.IsJoined && _coop.Session.GuestState == GuestState.InWorld && _coop.Activity.Get(ActorSlot.Guest) != null;

    /// <summary>Host: why its End Day waits for the guest, or null. Asks the guest to press End Day.</summary>
    internal string GuestBlocker()
    {
        if (!NativeCareer.InCareer || !Handler.Bound) return null;
        if (_coop.Session.IsJoined && _coop.Session.GuestState == GuestState.Loading) return "Wait until Client has finished loading the world, then end the day.";
        if (!GuestInWorld || Handler.GuestReady(Today)) return null;
        var now = Time.realtimeSinceStartup;
        if (now - _askedAt > AskGuestEvery)
        {
            _askedAt = now;
            _coop.Session.SendChat(ChatKind.System, "The host wants to end the day. Press End Day (or Skip Work) on your tablet when you're done.");
            _coop.AddChat("Asked Client to end the day.");
        }
        return "Waiting for Client to end the day. Press End Day again once Client is ready.";
    }

    /// <summary>Host: why the guest can't be ready to end <paramref name="day"/> now, or null.</summary>
    private string CheckReady(int day)
    {
        try
        {
            if (day != Today) return "the host's game is on another day; wait for the new day to load.";
            if (_coop.DayEnding) return "the host's day is already ending.";
            return GuestBusy();
        }
        catch (Exception e) { return "the host's game could not check the day: " + e.Message; }
    }

    /// <summary>What the guest is still doing that an End Day would cut off, or null (the host's shared models).</summary>
    private string GuestBusy()
    {
        var guest = _coop.Activity.Get(ActorSlot.Guest);
        if (guest?.Pending != null || _coop.Edits?.Handler.Edits.Any(e => e.Actor == ActorSlot.Guest) == true) return "finish what you're doing on that PC first.";
        if (!_coop.Activity.Carried(ActorSlot.Guest).IsNone) return "put down the PC you're carrying first.";
        if (_coop.Packages?.Handler.HandsFull(ActorSlot.Guest) == true) return "put down what you're carrying first.";
        if (_coop.ToolBenches?.Handler.Of(ActorSlot.Guest) != null) return "leave the tool table first.";
        if (_coop.Workshop?.Handler.Decorator == ActorSlot.Guest) return "close the Customise Workshop app first.";
        return null;
    }

    // ---------------- IDayNative ----------------

    public void ReadyChanged(in MutationContext ctx, int? day, bool byChange)
    {
        if (_world.Local == ActorSlot.Host)
        {
            if (day.HasValue)
            {
                _coop.AddChat("Client is ready to end the day.");
                _coop.Notice("Client is ready to end the day. Press End Day to end it.");
            }
            else if (byChange) _coop.AddChat("Client started something and is no longer ready to end the day.");
            Plugin.Logger.LogInfo($"[day] the client's ready mark is now {(day.HasValue ? "day " + day : "off")}.");
            return;
        }
        if (day.HasValue)
        {
            if (ctx.FromLocalPlayer && _pending == ctx.Op) _pending = null;
            _coop.Notice("You're ready to end the day. The host ends it.");
            _coop.AddChat("You're ready to end the day; waiting for the host.");
        }
        else if (byChange) _coop.Notice("You started something, so you're no longer ready to end the day. Press End Day again when you're done.");
    }
}

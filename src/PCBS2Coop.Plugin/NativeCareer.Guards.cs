using HarmonyLib;
using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>
/// Career actions phases 16–17 will share (owner decision, November 8, 2026): until then the host does them and the
/// guest takes the results; the guest's own are refused with a notice. Shop checkout and part sales are shared since
/// 0.60 (<see cref="CommerceHooks"/>); job actions since 0.63 (<see cref="JobHooks"/>), whose guest hooks send them
/// before the game reaches the <c>CareerStatus</c> job calls below, which stay as backstops. Since 0.65 (17d) the
/// host's End Day waits until both players are ready (<see cref="NativeDay"/>). A click the other player's desktop mirrors here
/// (<see cref="NativeDesktopInput.Injecting"/>) never runs the action on this game: the host's result arrives as a
/// career update, and a guest's mirrored action stays refused on the host too. Native addresses checked distinct
/// (decompile RVAs); startup still reports any folded pair under <c>[hooks]</c>.
/// </summary>
internal static class CareerGuards
{
    /// <summary>Whether this game may run <paramref name="action"/> (a phrase like "buy parts").</summary>
    internal static bool Allow(string action)
    {
        if (!NativeCareer.InCareer || NativeProjection.Active) return true;
        if (NativeDesktopInput.Injecting)
        {
            Plugin.Logger.LogInfo($"[career] the other player's mirrored click would {action} here; their game decides.");
            return false;
        }
        if (CoopSaves.Current == CoopSaves.Mode.Host) return true;
        CoopController.Instance?.Notice($"Only the host can {action} in co-op for now.");
        Plugin.Logger.LogInfo($"[career] refused: this guest tried to {action}.");
        return false;
    }

    /// <summary>Host: why End Day (or Skip Work) must wait, or null. The day changes the world, so both players'
    /// changes settle first, and the guest in the world must be ready (phase 17d).</summary>
    internal static string EndDayBlocker(CoopController coop)
    {
        if (coop.SyncRunning || coop.DayEnding) return "The world is being saved or reloaded; end the day once it's done.";
        var busy = (coop.Edits?.Handler.Edits.Count ?? 0) > 0 || coop.Replays.Busy ||
                   coop.ToolBenches?.Handler.Of(ActorSlot.Host) != null || coop.ToolBenches?.Handler.Of(ActorSlot.Guest) != null ||
                   coop.Jobs?.Busy == true;
        if (busy) return "Wait until both players finish what they're doing, then end the day.";
        return coop.Day?.GuestBlocker();
    }

    /// <summary>End Day and Skip Work as they reach the game's day change: the guest's never runs; the host's waits for
    /// <see cref="EndDayBlocker"/>.</summary>
    internal static bool AllowEndDay()
    {
        if (!Allow("end the day")) return false;
        if (!NativeCareer.InCareer) return true;
        var coop = CoopController.Instance;
        if (coop == null) return true;
        if (EndDayBlocker(coop) is not { } why) { coop.Jobs?.DayStarted(); return true; }
        coop.Notice(why);
        Plugin.Logger.LogInfo("[career] End Day waits: " + why);
        return false;
    }

    /// <summary>The End Day and Skip Work buttons (phase 17d): the guest's mark it ready; the host's are refused early,
    /// before the game's end-of-day warnings and door walk, while <see cref="EndDayBlocker"/> holds.</summary>
    private static bool DayButton(bool skip)
    {
        if (!NativeCareer.InCareer || NativeProjection.Active || CoopSaves.Current == CoopSaves.Mode.None) return true;
        if (NativeDesktopInput.Injecting) return false;
        var coop = CoopController.Instance;
        if (coop == null) return true;
        try
        {
            if (CoopSaves.Current == CoopSaves.Mode.Guest)
            {
                if (coop.Day == null) return Allow("end the day");
                coop.Day.GuestPressed(skip);
                return false;
            }
            if (EndDayBlocker(coop) is not { } why) return true;
            coop.Notice(why);
            Plugin.Logger.LogInfo($"[career] {(skip ? "Skip Work" : "End Day")} waits: {why}");
            return false;
        }
        catch (Exception e) { Plugin.Logger.LogError("[career] End Day check failed: " + e); return false; }
    }


    [HarmonyPatch(typeof(CareerStatus), nameof(CareerStatus.OnAccept), typeof(Job))]
    internal static class JobAccept
    {
        private static bool Prefix() => Allow("accept jobs");
    }

    [HarmonyPatch(typeof(CareerStatus), nameof(CareerStatus.OnCollect), typeof(Job))]
    internal static class JobCollect
    {
        private static bool Prefix() => Allow("collect job payments");
    }

    [HarmonyPatch(typeof(CareerStatus), nameof(CareerStatus.OnCollect), typeof(MultiJob))]
    internal static class MultiJobCollect
    {
        private static bool Prefix() => Allow("collect job payments");
    }

    [HarmonyPatch(typeof(CareerStatus), nameof(CareerStatus.OnQuit), typeof(Job))]
    internal static class JobQuit
    {
        private static bool Prefix() => Allow("quit jobs");
    }

    [HarmonyPatch(typeof(CareerStatus), nameof(CareerStatus.OnQuit), typeof(MultiJob))]
    internal static class MultiJobQuit
    {
        private static bool Prefix() => Allow("quit jobs");
    }

    [HarmonyPatch(typeof(CareerStatus), nameof(CareerStatus.OnReject), typeof(Job))]
    internal static class JobReject
    {
        private static bool Prefix() => Allow("turn down jobs");
    }

    [HarmonyPatch(typeof(CareerStatus), nameof(CareerStatus.OnReject), typeof(MultiJob))]
    internal static class MultiJobReject
    {
        private static bool Prefix() => Allow("turn down jobs");
    }

    /// <summary>End Day and Skip Work both go through <c>DaySummary.AdvanceDay(bool)</c> (decompile).</summary>
    [HarmonyPatch(typeof(DaySummary), "AdvanceDay")]
    internal static class EndDay
    {
        private static bool Prefix() => AllowEndDay();
    }

    /// <summary>The End Day button: warnings (unbought basket, uncollected jobs), then <c>EndDay</c>, the door sound and
    /// <c>AdvanceDay(false)</c> (decompile, RVA 78C420).</summary>
    [HarmonyPatch(typeof(DaySummary), nameof(DaySummary.OnEndDay))]
    internal static class EndDayButton
    {
        private static bool Prefix() => DayButton(false);
    }

    /// <summary>The Skip Work button: <c>AdvanceDay(true)</c> (decompile, RVA 78C680, a tail jump).</summary>
    [HarmonyPatch(typeof(DaySummary), nameof(DaySummary.OnSkipWork))]
    internal static class SkipWorkButton
    {
        private static bool Prefix() => DayButton(true);
    }

    internal static void Apply(Harmony h)
    {
        foreach (var t in new[] { typeof(JobAccept), typeof(JobCollect), typeof(MultiJobCollect),
                     typeof(JobQuit), typeof(MultiJobQuit), typeof(JobReject), typeof(MultiJobReject), typeof(EndDay),
                     typeof(EndDayButton), typeof(SkipWorkButton) })
            h.PatchAll(t);
    }
}

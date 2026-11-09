using HarmonyLib;
using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>
/// Career actions phases 16–17 will share (owner decision, November 8, 2026): until then the host does them and the
/// guest takes the results; the guest's own are refused with a notice. Shop checkout and part sales are shared since
/// 0.60 (<see cref="CommerceHooks"/>). A click the other player's desktop mirrors here
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

    /// <summary>The host's End Day waits until both players' current changes are done (the day changes the world).</summary>
    internal static bool AllowEndDay()
    {
        if (!Allow("end the day")) return false;
        if (!NativeCareer.InCareer) return true;
        var coop = CoopController.Instance;
        if (coop == null) return true;
        var busy = (coop.Edits?.Handler.Edits.Count ?? 0) > 0 || coop.Replays.Busy ||
                   coop.ToolBenches?.Handler.Of(ActorSlot.Host) != null || coop.ToolBenches?.Handler.Of(ActorSlot.Guest) != null;
        if (!busy) return true;
        coop.Notice("Wait until both players finish what they're doing, then end the day.");
        Plugin.Logger.LogInfo("[career] End Day waits: an edit or table session is still running.");
        return false;
    }

    [HarmonyPatch(typeof(PCBayApp), nameof(PCBayApp.OnBuyAction))]
    internal static class PcBayBuy
    {
        private static bool Prefix() => Allow("buy from PC Bay");
    }

    [HarmonyPatch(typeof(CareerStatus), nameof(CareerStatus.BuyShare))]
    internal static class ShareBuy
    {
        private static bool Prefix(ref bool __result) { if (Allow("buy shares")) return true; __result = false; return false; }
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

    internal static void Apply(Harmony h)
    {
        foreach (var t in new[] { typeof(PcBayBuy), typeof(ShareBuy), typeof(JobAccept), typeof(JobCollect), typeof(MultiJobCollect),
                     typeof(JobQuit), typeof(MultiJobQuit), typeof(JobReject), typeof(MultiJobReject), typeof(EndDay) })
            h.PatchAll(t);
    }
}

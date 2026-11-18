using HarmonyLib;
using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>
/// Phase 17b hooks (owner decisions, November 9, 2026). In a co-op Career the host's own job buttons run the game's
/// code once the job's PC is free; the guest's are sent to the host (<see cref="NativeJobs"/>). A click the other
/// player's desktop mirrors here never acts: the clicking game sends it. Each hooked native function is distinct
/// (decompile RVAs: Job/MultiJob OnAccept E4B8F0/E64770, OnCollected E4C370/E64BD0, DoQuit E445D0/E63250, OnRejected
/// E4D3D0/E652A0, OnPositiveAction E4CBA0/E64E10, OnNegativeAction E4CA30/E64D10, the quit confirmation E52DD0,
/// ShowResult E4DBA0, RemoveJobItem A879A0, the tutorial pop-up 703090; phase 17d: EmailBill.OnPositiveAction E40780,
/// ShopUpgradeEmail.OnPositiveAction 677190).
/// </summary>
internal static class JobHooks
{
    /// <summary>Whether this game runs the job action itself.</summary>
    private static bool Run(Job job, JobAct act, int value)
    {
        if (!NativeCareer.InCareer || NativeJobs.HostRunning || job == null) return true;
        // Desktop injection itself runs inside NativeProjection. Check it first so a replay cannot pay or accept
        // a job locally before the host's one authoritative result arrives.
        if (NativeDesktopInput.Injecting)
        {
            Plugin.Logger.LogInfo($"[jobs] the other player's mirrored click would {act} job {job.m_id} here; their game sends it.");
            return false;
        }
        if (NativeProjection.Active) return true;
        var jobs = NativeJobs.Current;
        if (jobs == null) return true;
        if (CoopSaves.Current == CoopSaves.Mode.Host) return jobs.AllowHostOwn(job, act);
        try { jobs.Request(act, job, value); }
        catch (System.Exception e) { Plugin.Logger.LogError("[jobs] job hook failed: " + e); }
        return false;
    }

    private static int NoFee(Job job)
    {
        try { return Math.Max(0, job.GetInitialValue()); }
        catch { return 0; }
    }

    /// <summary>A mirrored click on a shared PC's inbox runs no job button here (it would open the result screen or a
    /// quit confirmation on this player's game); the clicking game handles its own buttons.</summary>
    private static bool NotMirrored(Job job, string button)
    {
        if (!NativeCareer.InCareer || NativeJobs.HostRunning || !NativeDesktopInput.Injecting) return true;
        Plugin.Logger.LogInfo($"[jobs] the other player's mirrored {button} click on job {job?.m_id ?? 0} stays on their game.");
        return false;
    }

    [HarmonyPatch(typeof(Job), nameof(Job.OnPositiveAction))]
    internal static class Positive { private static bool Prefix(Job __instance) => NotMirrored(__instance, "job") && MayCollect(__instance); }

    [HarmonyPatch(typeof(MultiJob), nameof(MultiJob.OnPositiveAction))]
    internal static class MultiPositive { private static bool Prefix(MultiJob __instance) => NotMirrored(__instance, "job") && MayCollect(__instance); }

    /// <summary>Guest: a finished job's Collect shows the invoice only when the host's game also shows it finished
    /// (phase 17c).</summary>
    private static bool MayCollect(Job job)
    {
        if (!NativeCareer.InCareer || NativeProjection.Active || NativeJobs.HostRunning || CoopSaves.Current == CoopSaves.Mode.Host) return true;
        try { return NativeJobs.Current?.GuestMayCollect(job) ?? true; }
        catch (System.Exception e) { Plugin.Logger.LogError("[jobs] collect check failed: " + e); return true; }
    }

    [HarmonyPatch(typeof(Job), nameof(Job.OnNegativeAction))]
    internal static class Negative { private static bool Prefix(Job __instance) => NotMirrored(__instance, "decline/quit"); }

    [HarmonyPatch(typeof(MultiJob), nameof(MultiJob.OnNegativeAction))]
    internal static class MultiNegative { private static bool Prefix(MultiJob __instance) => NotMirrored(__instance, "decline/quit"); }

    [HarmonyPatch(typeof(Job), nameof(Job.OnAccept))]
    internal static class Accept { private static bool Prefix(Job __instance) => Run(__instance, JobAct.Accept, 0); }

    [HarmonyPatch(typeof(MultiJob), nameof(MultiJob.OnAccept))]
    internal static class MultiAccept { private static bool Prefix(MultiJob __instance) => Run(__instance, JobAct.Accept, 0); }

    /// <summary>Collecting: <c>OnPositiveAction</c> on a finished job shows the result screen, then calls this.</summary>
    [HarmonyPatch(typeof(Job), nameof(Job.OnCollected))]
    internal static class Collect { private static bool Prefix(Job __instance) => Run(__instance, JobAct.Collect, 0); }

    [HarmonyPatch(typeof(MultiJob), nameof(MultiJob.OnCollected))]
    internal static class MultiCollect { private static bool Prefix(MultiJob __instance) => Run(__instance, JobAct.Collect, 0); }

    /// <summary>The quit confirmation's Yes: it charges the fee (the job's initial value minus its PC's value now),
    /// then quits. The guest sends the PC value its confirmation showed.</summary>
    [HarmonyPatch(typeof(Job.__c__DisplayClass92_0), nameof(Job.__c__DisplayClass92_0._QuitJob_b__2))]
    internal static class QuitConfirm
    {
        private static bool Prefix(Job.__c__DisplayClass92_0 __instance) => Run(__instance.__4__this, JobAct.Quit, Math.Max(0, __instance.Value));
    }

    /// <summary>Quitting without a fee comes here directly.</summary>
    [HarmonyPatch(typeof(Job), "DoQuit")]
    internal static class Quit { private static bool Prefix(Job __instance) => Run(__instance, JobAct.Quit, NoFee(__instance)); }

    [HarmonyPatch(typeof(MultiJob), "DoQuit")]
    internal static class MultiQuit { private static bool Prefix(MultiJob __instance) => Run(__instance, JobAct.Quit, NoFee(__instance)); }

    [HarmonyPatch(typeof(Job), nameof(Job.OnRejected))]
    internal static class Reject { private static bool Prefix(Job __instance) => Run(__instance, JobAct.Reject, 0); }

    [HarmonyPatch(typeof(MultiJob), nameof(MultiJob.OnRejected))]
    internal static class MultiReject { private static bool Prefix(MultiJob __instance) => Run(__instance, JobAct.Reject, 0); }

    /// <summary>Phase 17d: a bill's Pay or the renovation offer's accept. On a deleted e-mail the button only restores
    /// it (decompile: both test <c>m_removed</c> first), which stays on this game.</summary>
    private static bool RunMail(Email mail)
    {
        if (!NativeCareer.InCareer || NativeJobs.HostRunning || mail == null || mail.m_removed) return true;
        if (NativeDesktopInput.Injecting)
        {
            Plugin.Logger.LogInfo("[jobs] the other player's mirrored e-mail click stays on their game.");
            return false;
        }
        if (NativeProjection.Active) return true;
        var jobs = NativeJobs.Current;
        if (jobs == null) return true;
        try
        {
            if (CoopSaves.Current == CoopSaves.Mode.Host) return jobs.AllowHostOwnMail(mail);
            jobs.RequestMail(mail);
        }
        catch (System.Exception e) { Plugin.Logger.LogError("[jobs] e-mail hook failed: " + e); }
        return false;
    }

    [HarmonyPatch(typeof(EmailBill), nameof(EmailBill.OnPositiveAction))]
    internal static class PayBill { private static bool Prefix(EmailBill __instance) => RunMail(__instance); }

    [HarmonyPatch(typeof(ShopUpgradeEmail), nameof(ShopUpgradeEmail.OnPositiveAction))]
    internal static class OrderRenovation { private static bool Prefix(ShopUpgradeEmail __instance) => RunMail(__instance); }

    /// <summary>The host running the guest's action shows no result screen (the guest's game shows its own).</summary>
    [HarmonyPatch(typeof(Job), "ShowResult")]
    internal static class NoResultScreen { private static bool Prefix() => !NativeJobs.HostRunning; }

    /// <summary>Nor tutorial pop-ups that the guest's accept would raise on the host's screen.</summary>
    [HarmonyPatch(typeof(GameController), nameof(GameController.ShowImmediateTutorialWithoutState))]
    internal static class NoTutorialPopUp { private static bool Prefix() => !NativeJobs.HostRunning; }

    /// <summary>Host: collecting, quitting or turning down a job removes its PC, package and note on its game. The
    /// removed shared PCs are shared; packages and notes follow the host's benches (phase 16c).</summary>
    [HarmonyPatch(typeof(SaveLoadSystem), nameof(SaveLoadSystem.RemoveJobItem))]
    internal static class RemovedJobPc
    {
        private static void Prefix(out List<NetObjectId> __state)
        {
            __state = null;
            try { if (NativeCareer.InCareer && CoopSaves.Current == CoopSaves.Mode.Host) __state = NativeJobs.Current?.PresentShared(); }
            catch (System.Exception e) { Plugin.Logger.LogWarning("[jobs] could not list the shared PCs before a job removal: " + e.Message); }
        }

        private static void Postfix(List<NetObjectId> __state)
        {
            try { NativeJobs.Current?.NoteRemoved(__state); }
            catch (System.Exception e) { Plugin.Logger.LogWarning("[jobs] could not compare the shared PCs after a job removal: " + e.Message); }
        }
    }

    internal static void Apply(Harmony h)
    {
        foreach (var t in new[] { typeof(Positive), typeof(MultiPositive), typeof(Negative), typeof(MultiNegative), typeof(Accept), typeof(MultiAccept), typeof(Collect), typeof(MultiCollect), typeof(QuitConfirm),
                     typeof(Quit), typeof(MultiQuit), typeof(Reject), typeof(MultiReject), typeof(NoResultScreen), typeof(NoTutorialPopUp),
                     typeof(RemovedJobPc), typeof(PayBill), typeof(OrderRenovation) })
            h.PatchAll(t);
    }
}

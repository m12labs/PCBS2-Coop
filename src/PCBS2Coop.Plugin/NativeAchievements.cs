using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PCBS2Coop;

/// <summary>
/// Phase 17e (owner decisions, November 8 and 10, 2026): achievements in co-op.
/// <list type="bullet">
/// <item>An achievement belongs to the account that did the action: a game showing the other player's change (a
/// projection, a replayed edit, mirrored desktop input) or the host's game running the guest's job, bill or renovation
/// action never earns one. Every <c>Achievements.On…</c> entry is held in those moments.</item>
/// <item>Business milestones count for both: each game checks the shared cash, level, star rating and jobs completed
/// itself (<c>OnCash</c>, <c>OnLevel</c>, <c>OnStarRating</c>, <c>OnJobComplete</c>, with the values the game's own
/// <c>AddCash</c>, <c>AddKudos</c>, <c>AddReview</c> and <c>OnCollect</c> pass) whenever they change.</item>
/// </list>
/// </summary>
internal static class NativeAchievements
{
    private const float CheckEvery = 1f;
    [ThreadStatic] private static bool _milestone;
    private static float _next;
    private static (int Cash, int Level, float Rating, int Jobs)? _last;
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);

    /// <summary>This game is showing or running the other player's action right now.</summary>
    private static bool ForOtherPlayer => NativeProjection.Active || NativeJobs.HostRunning || NativeDesktopInput.Injecting || NativeEditReplay.Active;

    internal static void Reset() { _last = null; _next = 0; }

    /// <summary>Each game, in a co-op career: the business milestones from the shared numbers.</summary>
    internal static void Update()
    {
        if (!NativeCareer.InCareer || CoopSaves.Current == CoopSaves.Mode.None) return;
        var now = Time.realtimeSinceStartup;
        if (now < _next) return;
        _next = now + CheckEvery;
        var s = CareerStatus.GetState();
        if (s == null) return;
        var values = (s.m_cash, LevelProgression.GetZeroIndexedLevel(s.m_kudos), s.m_starRating, s.m_numberJobsCompleted);
        var last = _last;
        if (last == values) return;
        _last = values;
        _milestone = true;
        try
        {
            if (last?.Cash != values.m_cash) Achievements.OnCash(values.m_cash);
            if (last?.Level != values.Item2) Achievements.OnLevel(values.Item2);
            if (last?.Rating != values.m_starRating) Achievements.OnStarRating(values.m_starRating);
            if (last?.Jobs != values.m_numberJobsCompleted) Achievements.OnJobComplete(values.m_numberJobsCompleted);
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[achievements] the business milestones could not be checked: " + e.Message); }
        finally { _milestone = false; }
    }

    /// <summary>Every <c>Achievements.On…</c> entry: held while this game shows or runs the other player's action.</summary>
    [HarmonyPatch]
    internal static class Guard
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            typeof(Achievements).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.DeclaringType == typeof(Achievements) && m.Name.StartsWith("On", StringComparison.Ordinal) && m.ReturnType == typeof(void));

        private static bool Prefix(MethodBase __originalMethod)
        {
            if (CoopSaves.Current == CoopSaves.Mode.None || _milestone || !ForOtherPlayer) return true;
            if (Logged.Add(__originalMethod.Name)) Plugin.Logger.LogInfo($"[achievements] {__originalMethod.Name} held: this game is showing the other player's action.");
            return false;
        }
    }
}

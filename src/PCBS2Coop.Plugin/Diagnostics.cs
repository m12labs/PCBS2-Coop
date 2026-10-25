using System.Text;
using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;
using PCBS2Coop.Network;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// Opt-in runtime evidence dumps for the phase audits (PCBS2COOP_DIAG=1, or the self test).
/// Read-only: it inspects the scene and game singletons but changes nothing.
/// Output: BepInEx/PCBS2Coop-diag/census-&lt;tag&gt;-&lt;scene&gt;.txt
/// </summary>
internal static class Diagnostics
{
    public static bool Enabled => AutoTest.IsEnabled || Environment.GetEnvironmentVariable("PCBS2COOP_DIAG") == "1";
    public static string Dir => Path.Combine(Paths.BepInExRootPath, "PCBS2Coop-diag");

    public static void WriteCensus(string tag)
    {
        var scene = SceneManager.GetActiveScene().name;
        var sb = new StringBuilder();
        sb.AppendLine($"Census '{tag}' in scene {scene} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}, realtime {Time.realtimeSinceStartup:F1}s");
        Section(sb, "Identity", () =>
        {
            sb.AppendLine($"game: {GameInfo.ReadGame()}");
            sb.AppendLine($"content: {GameInfo.ReadContent()}");
            sb.AppendLine($"unity: {Application.unityVersion}, dataPath: {Application.dataPath}");
        });
        Section(sb, "Save paths (SaveLoadSystem statics)", () =>
        {
            sb.AppendLine($"s_saveDir = '{SaveLoadSystem.s_saveDir}' (full: {Path.GetFullPath(SaveLoadSystem.s_saveDir ?? ".")})");
            sb.AppendLine($"s_freeBuildSaveName = '{SaveLoadSystem.s_freeBuildSaveName}', s_defaultFreeBuildSaveName = '{SaveLoadSystem.s_defaultFreeBuildSaveName}', s_autoFreeBuildSaveName = '{SaveLoadSystem.s_autoFreeBuildSaveName}'");
            sb.AppendLine($"cwd = {Environment.CurrentDirectory}");
        });
        Section(sb, "LevelLoadPersistency", () =>
        {
            var l = Object.FindObjectOfType<LevelLoadPersistency>();
            if (l == null) { sb.AppendLine("not found"); return; }
            sb.AppendLine($"gameModeToLoad={l.gameModeToLoad} startFreebuild={l.startFreebuild} resetFreebuild={l.resetFreebuild} isLevelLoading={l.isLevelLoading} " +
                          $"m_isNewGame={l.m_isNewGame} loadedFromMainMenu={l.loadedFromMainMenu} LoadInProgress={l.GetLoadInProgress()} SaveName='{l.SaveName}' Scene='{l.Scene}'");
        });
        Section(sb, "GameController", () =>
        {
            var g = Object.FindObjectOfType<GameController>();
            sb.AppendLine(g == null ? "not found" : $"allowPause={g.allowPause} paused={g.GetPaused()}");
        });
        Section(sb, "BenchSlots (candidate bench identity)", () =>
        {
            var seen = new Dictionary<string, int>();
            foreach (var b in Object.FindObjectsOfType<BenchSlot>(true))
            {
                var path = PathOf(b.transform);
                seen[path] = seen.TryGetValue(path, out var n) ? n + 1 : 1;
                sb.AppendLine($"{path} | type={b.m_type} active={b.gameObject.activeInHierarchy} sibling={b.transform.GetSiblingIndex()} " +
                              $"workStation={(b.m_workStation != null ? PathOf(b.m_workStation.transform) : "none")} pos={b.transform.position}");
            }
            foreach (var kv in seen) if (kv.Value > 1) sb.AppendLine($"AMBIGUOUS PATH x{kv.Value}: {kv.Key}");
        });
        Section(sb, "WorkStations", () =>
        {
            foreach (var w in Object.FindObjectsOfType<WorkStation>(true)) sb.AppendLine($"{PathOf(w.transform)} active={w.gameObject.activeInHierarchy}");
        });
        Section(sb, "Objects with gameplay identity needs (counts)", () =>
        {
            sb.AppendLine($"Case {Object.FindObjectsOfType<Case>(true).Length}, Package {Object.FindObjectsOfType<Package>(true).Length}, " +
                          $"PostItSlot {Object.FindObjectsOfType<PostItSlot>(true).Length}, Cupboard {Object.FindObjectsOfType<Cupboard>(true).Length}, " +
                          $"WorkshopController {Object.FindObjectsOfType<WorkshopController>(true).Length}");
        });
        Section(sb, "Scene roots", () =>
        {
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                sb.AppendLine($"{root.name} active={root.activeSelf} children={root.transform.childCount}");
        });

        Directory.CreateDirectory(Dir);
        var file = Path.Combine(Dir, $"census-{tag}-{scene}.txt");
        File.WriteAllText(file, sb.ToString());
        Plugin.Logger.LogInfo($"Census written: {file}");
    }

    /// <summary>One line summarising the world pipeline, for comparing both peers' logs.</summary>
    public static string WorldSummary(CoopController coop)
    {
        var w = coop.Session.World;
        if (w == null) return "no session";
        return $"gen {w.Generation} rev {w.Revision} hash {w.ChainHash:x16} markers [{string.Join(",", coop.Markers.Applied)}] " +
               $"divergence {(w.Divergence ?? "none")} | {w.Stats} | {coop.Session.Stats}";
    }

    private static void Section(StringBuilder sb, string title, Action body)
    {
        sb.AppendLine().AppendLine($"=== {title} ===");
        try { body(); }
        catch (Exception e) { sb.AppendLine($"ERROR: {e.GetType().Name}: {e.Message}"); }
    }

    private static string PathOf(Transform t)
    {
        var s = t.name;
        for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
        return s;
    }
}

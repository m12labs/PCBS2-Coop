using System.IO.Compression;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// Phase 2 audit probe (PCBS2COOP_PROBE=1, only together with the self test's disposable save folder).
/// In the workshop it logs load/readiness signals, pause and focus settings, takes one save into a probe folder,
/// then returns to the menu and loads that save through the game's Continue Free Build path.
/// Output lines start with "PROBE:".
/// </summary>
internal sealed class Probe
{
    public static bool Enabled => AutoTest.IsEnabled && Environment.GetEnvironmentVariable("PCBS2COOP_PROBE") == "1";

    private int _step;
    private float _at;
    private float _sceneAt = -1;
    private float _nextSignals;
    private string _probeDir;
    private int _workshopVisits;
    private string _lastSignals;

    public void Update(CoopRunner r)
    {
        try { Tick(r); }
        catch (Exception e) { Log("ERROR " + e); _step = int.MaxValue; }
    }

    private void Tick(CoopRunner r)
    {
        var now = Time.realtimeSinceStartup;
        if (r.Scene == CoopRunner.WorkshopScene)
        {
            if (_sceneAt < 0) { _sceneAt = now; _workshopVisits++; Log($"workshop visit {_workshopVisits} started"); }
            if (now - _sceneAt < 40 && now >= _nextSignals)
            {
                _nextSignals = now + 0.5f;
                var s = Signals();
                if (s != _lastSignals) { Log($"t+{now - _sceneAt:F1}s {s}"); _lastSignals = s; }
            }
        }
        else _sceneAt = -1;

        if (now < _at) return;
        switch (_step)
        {
            case 0 when r.Scene == CoopRunner.WorkshopScene && _sceneAt >= 0 && now - _sceneAt > 25:
                if (AutoTest.IsolatedSaveDir == null || SaveLoadSystem.s_saveDir != AutoTest.IsolatedSaveDir) { Log("saves not isolated; stopping."); _step = int.MaxValue; return; }
                State("before save");
                PauseCheck();
                _step++;
                _at = now + 3;
                break;
            case 1:
            {
                var gc = Object.FindObjectOfType<GameController>();
                Log($"3 s after PauseGame: paused={gc?.GetPaused()} timeScale={Time.timeScale}");
                gc?.UnpauseGame();
                Log($"after UnpauseGame: paused={gc?.GetPaused()} timeScale={Time.timeScale}");
                TakeSave();
                _step++;
                break;
            }
            case 2 when !SaveLoadSystem.WaitingOnThread():
                Log("save thread finished");
                ListDir(_probeDir);
                State("after save");
                _step++;
                _at = now + 2;
                break;
            case 3:
                Log("returning to the main menu via NewLoadingScreen.LoadScene(Main_Menu)");
                Object.FindObjectOfType<NewLoadingScreen>().LoadScene(CoopRunner.MenuScene, false);
                _step++;
                break;
            case 4 when r.Scene == CoopRunner.MenuScene && Object.FindObjectOfType<NewMainMenu>() != null:
                _step++;
                _at = now + 8;
                break;
            case 5:
                LoadProbeSave();
                _step++;
                break;
            case 6 when _workshopVisits >= 2 && _sceneAt >= 0 && now - _sceneAt > 25:
                State("after loading the probe save");
                Log($"probe dir after load:");
                ListDir(_probeDir);
                Log("finished");
                _step = int.MaxValue;
                break;
        }
    }

    private static string Signals()
    {
        var l = Object.FindObjectOfType<LevelLoadPersistency>();
        var sls = Object.FindObjectOfType<SaveLoadSystem>();
        var wc = Object.FindObjectOfType<WorkshopController>();
        var db = PartsDatabase.s_instance;
        string ops = db == null ? "db=null" :
            $"ops pc={db.m_loadPCOperations?.Count} part={db.m_loadPartOperations?.Count} plug={db.m_loadPlugOperations?.Count} cable={db.m_loadCableOperations?.Count} prefab={db.m_loadPrefabOperations?.Count}";
        return $"isLevelLoading={l?.isLevelLoading} LoadInProgress={l?.GetLoadInProgress()} spawningPC={sls?.SpawningPC} " +
               $"caseLoading={wc?.IsCaseLoading} saveThread={SaveLoadSystem.WaitingOnThread()} {ops} cases={Object.FindObjectsOfType<Case>().Length} player={LocalPlayer.Find() != null}";
    }

    private void State(string tag)
    {
        var l = Object.FindObjectOfType<LevelLoadPersistency>();
        var sls = Object.FindObjectOfType<SaveLoadSystem>();
        var gc = Object.FindObjectOfType<GameController>();
        Log($"[{tag}] s_saveDir='{SaveLoadSystem.s_saveDir}' SaveName='{l?.SaveName}' Scene='{l?.Scene}' opened='{sls?.GetOpenedSaveName()}' " +
            $"loadedFromSave={sls?.WasLoadedFromSave()} gameModeToLoad={l?.gameModeToLoad} runInBackground={Application.runInBackground} " +
            $"timeScale={Time.timeScale} allowPause={gc?.allowPause} paused={gc?.GetPaused()} focused={Application.isFocused}");
    }

    private static void PauseCheck()
    {
        var gc = Object.FindObjectOfType<GameController>();
        if (gc == null) { Log("no GameController"); return; }
        gc.PauseGame();
        Log($"after PauseGame: paused={gc.GetPaused()} timeScale={Time.timeScale} canPause={gc.CanPause()}");
    }

    private void TakeSave()
    {
        _probeDir = Path.Combine(Diagnostics.Dir, "probe-save").Replace('\\', '/');
        if (Directory.Exists(_probeDir)) Directory.Delete(_probeDir, true);
        Directory.CreateDirectory(_probeDir);
        var sls = Object.FindObjectOfType<SaveLoadSystem>();
        var original = SaveLoadSystem.s_saveDir;
        SaveLoadSystem.s_saveDir = _probeDir;
        try
        {
            var result = sls.CreateSaveGame("coopprobe.binary", false, "");
            Log($"CreateSaveGame returned '{result}'");
        }
        finally { SaveLoadSystem.s_saveDir = original; }
        Log($"s_saveDir restored to '{SaveLoadSystem.s_saveDir}', save thread busy={SaveLoadSystem.WaitingOnThread()}");
    }

    private void LoadProbeSave()
    {
        SaveLoadSystem.s_saveDir = _probeDir;
        AutoTest.SetIsolatedSaveDir(_probeDir);
        var list = SaveLoadSystem.GetSaveGamesWindows();
        Log($"GetSaveGamesWindows in probe dir: {list.Count} entries");
        string pick = null;
        foreach (var info in list)
        {
            Log($"  name='{info.m_name}' file='{info.m_info?.Name}' mode={info.m_header?.m_gameMode} scene='{info.m_header?.m_scene}' saveName='{info.m_header?.m_saveName}' compressed={info.m_header?.m_compressedFile} version={info.m_header?.m_version}");
            pick ??= info.m_name;
        }
        ListDir(_probeDir);
        var menu = Object.FindObjectOfType<NewMainMenu>();
        Log($"calling NewMainMenu.ContinueFreeBuild('{pick}')");
        menu.ContinueFreeBuild(pick ?? "");
    }

    private static void ListDir(string dir)
    {
        foreach (var f in new DirectoryInfo(dir).GetFiles("*", SearchOption.AllDirectories))
        {
            var sb = new StringBuilder($"  file {f.FullName.Substring(dir.Length)} {f.Length} bytes {f.LastWriteTime:HH:mm:ss}");
            if (f.Extension == ".zip")
                try
                {
                    using var z = ZipFile.OpenRead(f.FullName);
                    foreach (var e in z.Entries) sb.Append($" [entry {e.FullName} {e.Length}]");
                }
                catch (Exception e) { sb.Append(" [zip error " + e.Message + "]"); }
            Log(sb.ToString());
        }
    }

    private static void Log(string m) => Plugin.Logger.LogInfo("PROBE: " + m);
}

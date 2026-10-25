using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// Whether the local workshop world has finished loading: the scene is active, no level load is running, no
/// PC/part/cable/prefab loads are pending, no PC is spawning or case loading, no save is being written, and the
/// player exists — continuously for <see cref="SettleSeconds"/>.
/// </summary>
internal sealed class WorldReadiness
{
    public const float SettleSeconds = 2f;

    private float _calmSince = -1;
    private LevelLoadPersistency _level;
    private SaveLoadSystem _saves;
    private WorkshopController _workshop;

    public bool Ready { get; private set; }
    /// <summary>What is still loading, for status text and logs.</summary>
    public string Waiting { get; private set; } = "";

    public void Reset()
    {
        _calmSince = -1;
        Ready = false;
        Waiting = "";
        _level = null;
        _saves = null;
        _workshop = null;
    }

    public void Update(bool inWorkshop)
    {
        if (!inWorkshop) { Reset(); Waiting = "not in the workshop"; return; }
        Waiting = Check();
        var now = Time.realtimeSinceStartup;
        if (Waiting != "") { _calmSince = -1; Ready = false; return; }
        if (_calmSince < 0) _calmSince = now;
        Ready = now - _calmSince >= SettleSeconds;
    }

    private string Check()
    {
        if (_level == null) _level = Object.FindObjectOfType<LevelLoadPersistency>();
        if (_saves == null) _saves = Object.FindObjectOfType<SaveLoadSystem>();
        if (_workshop == null) _workshop = Object.FindObjectOfType<WorkshopController>();
        if (_level != null && (_level.isLevelLoading || _level.GetLoadInProgress())) return "level loading";
        if (SaveLoadSystem.WaitingOnThread()) return "save being written";
        if (_saves != null && _saves.SpawningPC) return "PC spawning";
        if (_workshop != null && _workshop.IsCaseLoading) return "case loading";
        var db = PartsDatabase.s_instance;
        if (db != null)
        {
            var pending = (db.m_loadPCOperations?.Count ?? 0) + (db.m_loadPartOperations?.Count ?? 0) + (db.m_loadPlugOperations?.Count ?? 0)
                          + (db.m_loadCableOperations?.Count ?? 0) + (db.m_loadPrefabOperations?.Count ?? 0);
            if (pending > 0) return $"{pending} part/PC load(s) pending";
        }
        if (LocalPlayer.Find() == null) return "player not spawned";
        return "";
    }
}

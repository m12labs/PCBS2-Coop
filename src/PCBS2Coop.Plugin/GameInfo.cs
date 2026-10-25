using BepInEx.Configuration;
using UnityEngine;
using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>Builds this install's <see cref="PeerIdentity"/>: player ID, game build and loaded content. Main thread only.</summary>
internal static class GameInfo
{
    private static ConfigEntry<string> _playerIdEntry;
    private static PlayerId? _player;

    public static void Init(ConfigFile config)
    {
        _playerIdEntry = config.Bind("Identity", "PlayerId", "",
            "Random ID of this install, used to recognise you when you rejoin a co-op world. Generated automatically; don't copy it to another PC.");
    }

    /// <summary>The persistent install ID. Self-test instances get a fresh one each launch so two local copies differ.</summary>
    public static PlayerId Player
    {
        get
        {
            if (_player != null) return _player.Value;
            if (AutoTest.IsEnabled)
            {
                _player = PlayerId.New();
                Plugin.Logger.LogInfo($"AUTOTEST: using a temporary player ID {_player}.");
            }
            else if (_playerIdEntry != null && PlayerId.TryParse(_playerIdEntry.Value, out var saved)) _player = saved;
            else
            {
                _player = PlayerId.New();
                if (_playerIdEntry != null) _playerIdEntry.Value = _player.Value.Value.ToString();
                Plugin.Logger.LogInfo($"Created player ID {_player}.");
            }
            return _player.Value;
        }
    }

    public static PeerIdentity Build()
    {
        var id = new PeerIdentity(Player, ModVersion.Value, Protocol.Version, ReadGame(), ReadContent());
        Plugin.Logger.LogInfo($"Local identity: {id}");
        return id;
    }

    /// <summary>Application.version plus the build GUID from PCBS2_Data/boot.config (the same GUID the decompile manifest records).</summary>
    public static GameIdentity ReadGame()
    {
        string version = "", guid = "";
        try { version = Application.version ?? ""; }
        catch (Exception e) { Plugin.Logger.LogWarning("Could not read Application.version: " + e.Message); }
        try
        {
            var boot = Path.Combine(Application.dataPath, "boot.config");
            foreach (var line in File.ReadLines(boot))
                if (line.StartsWith("build-guid=", StringComparison.Ordinal)) guid = line["build-guid=".Length..].Trim();
        }
        catch (Exception e) { Plugin.Logger.LogWarning("Could not read the build GUID from boot.config: " + e.Message); }
        return new GameIdentity(version, guid);
    }

    /// <summary>Loaded DLC packs and a hash of the part catalog IDs. Either may be unavailable early in the menu.</summary>
    public static ContentIdentity ReadContent()
    {
        var dlcs = new List<string>();
        try
        {
            var list = DlcManagerAssetBundles.Instance?.LoadedDlcs;
            if (list != null)
                for (var i = 0; i < list.Count; i++) dlcs.Add(list[i].ToString());
        }
        catch (Exception e) { Plugin.Logger.LogWarning("Could not read loaded DLC: " + e.Message); }

        var hash = "";
        var count = -1;
        try
        {
            var parts = PartsDatabase.s_instance?.m_parts;
            if (parts != null && parts.Count > 0)
            {
                var ids = new List<string>(parts.Count);
                foreach (var key in parts.Keys) ids.Add(key);
                hash = PCBS2Coop.Network.Compatibility.HashIds(ids);
                count = ids.Count;
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning("Could not read the part catalog: " + e.Message); }

        dlcs.Sort(StringComparer.Ordinal);
        return new ContentIdentity(dlcs, hash, count);
    }
}

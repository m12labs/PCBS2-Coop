using UnityEngine;
using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>
/// Keeps co-op worlds out of the solo save folder by pointing the game's save folder
/// (<c>SaveLoadSystem.s_saveDir</c>, normally "Saves") somewhere else while a co-op world is loaded.
/// <list type="bullet">
/// <item>Host: <c>Saves/Coop</c> holds the co-op worlds (see <see cref="CoopWorldNames"/>): each world's named save and
/// its own autosave, every one paired with its <c>.coop</c> file.</item>
/// <item>Guest: plays from <c>Saves/Coop/GuestSession</c> (emptied at each join) and keeps its copy of the host's
/// world in <c>Saves/Coop/Joined</c> (latest file only).</item>
/// <item>Host snapshots for a joining guest are written to <c>Saves/Coop/Transfer</c>.</item>
/// <item>A world saved before named worlds keeps the game's Free Build file name (<see cref="IsEarlierWorld"/>).
/// Save zips must never be renamed: the file inside keeps the original name and the game unpacks it under that name.</item>
/// </list>
/// The game lists only the top level of its save folder, so the guest folders never appear in a load list and co-op
/// worlds never appear in the solo one. The redirect is undone only on the main menu, so an autosave can never land
/// in the solo folder.
/// </summary>
internal static class CoopSaves
{
    public enum Mode { None, Host, Guest }

    private static string _base;

    public static Mode Current { get; private set; }

    /// <summary>Host: the world being played, or null.</summary>
    public static string WorldStem { get; private set; }

    /// <summary>The game's save folder before the redirect (normally "Saves").</summary>
    public static string BaseDir => _base ?? SaveLoadSystem.s_saveDir;
    public static string CoopDir => BaseDir + "/Coop";
    public static string GuestSessionDir => CoopDir + "/GuestSession";
    public static string JoinedDir => CoopDir + "/Joined";
    public static string TransferDir => CoopDir + "/Transfer";

    private const string DefaultBuild = "defaultFreebuild.binary";

    /// <summary>Save files of co-op worlds that have their <c>.coop</c> pair, newest first.</summary>
    public static List<FileInfo> WorldSaves() =>
        SaveFiles().Where(f => File.Exists(f.FullName + CoopSavePair.Extension)).OrderByDescending(f => f.LastWriteTimeUtc).ToList();

    /// <summary>A co-op world exists (Continue is possible).</summary>
    public static bool HasCoopWorld() => WorldSaves().Count > 0;

    /// <summary>Stems of every world save in the co-op folder (paired or not).</summary>
    public static IEnumerable<string> WorldStems() => SaveFiles().Select(f => CoopWorldNames.StemOf(f.Name)).Distinct(StringComparer.OrdinalIgnoreCase);

    public static bool WorldExists(string stem) => WorldStems().Contains(stem, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<FileInfo> SaveFiles()
    {
        var dir = new DirectoryInfo(CoopDir);
        if (!dir.Exists) return Enumerable.Empty<FileInfo>();
        return dir.GetFiles().Where(f =>
            (f.Name.EndsWith(".binary", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".binary.zip", StringComparison.OrdinalIgnoreCase)) &&
            !f.Name.StartsWith(DefaultBuild, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Host: play the world <paramref name="stem"/> from the co-op folder.</summary>
    public static void EnterHost(string stem)
    {
        Enter(Mode.Host, CoopDir);
        WorldStem = stem;
        EnsureDefaultBuild();
        Plugin.Logger.LogInfo($"[saves] hosting world '{CoopWorldNames.WorldName(stem)}' (files '{CoopWorldNames.FileOf(stem)}', autosave '{CoopWorldNames.AutoFileOf(stem)}').");
    }

    public static void EnterGuest()
    {
        Enter(Mode.Guest, null);
        Recreate(GuestSessionDir);
        SaveLoadSystem.s_saveDir = GuestSessionDir;
        Plugin.Logger.LogInfo($"[saves] guest saves go to '{GuestSessionDir}'.");
    }

    /// <summary>Point the game back at the solo save folder. Call only on the main menu.</summary>
    public static void Restore(string why)
    {
        if (Current == Mode.None) return;
        SaveLoadSystem.s_saveDir = _base;
        Plugin.Logger.LogInfo($"[saves] save folder restored to '{_base}' ({why}).");
        Current = Mode.None;
        WorldStem = null;
        _base = null;
    }

    /// <summary>
    /// Host: the game's Free Build autosave (every Free Build save except the Save screen's named ones) goes to the
    /// hosted world's own autosave, named after the world.
    /// </summary>
    public static void RedirectAutosave(ref string fileName, ref string overrideName)
    {
        if (Current != Mode.Host || WorldStem == null || CoopController.TakingSnapshot) return;
        if (!string.Equals(fileName, SaveLoadSystem.s_autoFreeBuildSaveName, StringComparison.OrdinalIgnoreCase)) return;
        fileName = CoopWorldNames.AutoFileOf(WorldStem);
        overrideName = CoopWorldNames.DisplayOf(fileName);
        Plugin.Logger.LogInfo($"[saves] autosave goes to '{fileName}'.");
    }

    /// <summary>A save deleted outside the co-op menu leaves its <c>.coop</c> pair behind; remove it.</summary>
    public static void DropOrphanPairs()
    {
        try
        {
            if (!Directory.Exists(CoopDir)) return;
            foreach (var pair in Directory.GetFiles(CoopDir, "*" + CoopSavePair.Extension))
            {
                var native = pair[..^CoopSavePair.Extension.Length];
                if (File.Exists(native)) continue;
                File.Delete(pair);
                Plugin.Logger.LogInfo($"[saves] removed '{Path.GetFileName(pair)}': its save was deleted.");
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[saves] could not tidy the co-op folder: " + e.Message); }
    }

    /// <summary>
    /// A world saved before named worlds: the game's own Free Build file names (<c>autofreebuild.binary</c>). Hosting it
    /// continues it as a new named world, so its autosaves go to that world's files.
    /// </summary>
    public static bool IsEarlierWorld(string fileName)
    {
        var stem = CoopWorldNames.StemOf(fileName);
        return new[] { SaveLoadSystem.s_autoFreeBuildSaveName, SaveLoadSystem.s_freeBuildSaveName }
            .Any(n => !string.IsNullOrEmpty(n) && CoopWorldNames.StemOf(n).Equals(stem, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The name a save is shown under in the co-op menu.</summary>
    public static string DisplayName(string fileName) => IsEarlierWorld(fileName) ? "Earlier co-op world" : CoopWorldNames.DisplayOf(fileName);

    /// <summary>Delete one world save and its <c>.coop</c> pair.</summary>
    public static void DeleteSave(FileInfo file)
    {
        if (File.Exists(file.FullName)) File.Delete(file.FullName);
        if (File.Exists(file.FullName + CoopSavePair.Extension)) File.Delete(file.FullName + CoopSavePair.Extension);
        Plugin.Logger.LogInfo($"[saves] deleted '{file.Name}' and its pair.");
    }

    /// <summary>Guest: write a received load snapshot into the session folder. Returns the full path.</summary>
    public static string WriteGuestWorld(string fileName, byte[] data, SaveMetadata metadata)
    {
        var path = Path.Combine(GuestSessionDir, fileName);
        File.WriteAllBytes(path, data);
        CoopSavePair.WriteSidecar(path, data, metadata);
        return path;
    }

    /// <summary>Guest: keep the latest copy of the host's world. Never touches the guest's own co-op worlds or solo saves.</summary>
    public static void StoreJoinedCopy(string fileName, byte[] data, SaveMetadata metadata)
    {
        Recreate(JoinedDir);
        File.WriteAllBytes(Path.Combine(JoinedDir, fileName), data);
        CoopSavePair.WriteSidecar(Path.Combine(JoinedDir, fileName), data, metadata);
        Plugin.Logger.LogInfo($"[saves] stored the host's world as '{JoinedDir}/{fileName}' ({data.Length} bytes).");
    }

    public static void Recreate(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
    }

    public static string NativeFile(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        return File.Exists(path + ".zip") ? path + ".zip" : path;
    }

    private static void Enter(Mode mode, string dir)
    {
        if (Current != Mode.None) Restore("switching co-op save mode");
        _base = SaveLoadSystem.s_saveDir;
        Directory.CreateDirectory(CoopDir);
        Current = mode;
        if (dir == null) return;
        SaveLoadSystem.s_saveDir = dir;
        Plugin.Logger.LogInfo($"[saves] {mode} saves go to '{dir}' (was '{_base}').");
    }

    /// <summary>
    /// A new Free Build loads the game's shipped default build when it is in the save folder
    /// (otherwise the workshop starts empty). Put a copy in the co-op folder once.
    /// </summary>
    private static void EnsureDefaultBuild()
    {
        if (File.Exists(Path.Combine(CoopDir, DefaultBuild)) || File.Exists(Path.Combine(CoopDir, DefaultBuild + ".zip"))) return;
        var source = Path.Combine(Application.streamingAssetsPath, "UtilitySaves", DefaultBuild);
        if (!File.Exists(source))
        {
            Plugin.Logger.LogWarning($"[saves] shipped default build not found at '{source}'; a new co-op world starts empty.");
            return;
        }
        File.Copy(source, Path.Combine(CoopDir, DefaultBuild));
        Plugin.Logger.LogInfo($"[saves] copied the game's default build into '{CoopDir}'.");
    }

    /// <summary>The game's save list entry for a file in the current save folder, by file name (case-insensitive).</summary>
    public static string FindSaveName(Func<string, bool> fileMatches)
    {
        foreach (var info in SaveLoadSystem.GetSaveGamesWindows())
        {
            var file = info.m_info?.Name;
            if (file != null && fileMatches(file)) return info.m_name;
        }
        return null;
    }

    /// <summary>The save list entry for one file name, zipped or not.</summary>
    public static string FindSaveFile(string fileName)
    {
        var bare = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;
        return FindSaveName(f => f.Equals(bare, StringComparison.OrdinalIgnoreCase) || f.Equals(bare + ".zip", StringComparison.OrdinalIgnoreCase));
    }
}

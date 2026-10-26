using UnityEngine;
using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>
/// Keeps co-op worlds out of the solo save folder by pointing the game's save folder
/// (<c>SaveLoadSystem.s_saveDir</c>, normally "Saves") somewhere else while a co-op world is loaded.
/// <list type="bullet">
/// <item>Host: <c>Saves/Coop</c> holds the one co-op Free Build world, under the game's own file names.</item>
/// <item>Guest: plays from <c>Saves/Coop/GuestSession</c> (emptied at each join) and keeps its copy of the host's
/// world in <c>Saves/Coop/Joined</c> (latest file only).</item>
/// <item>Host snapshots for a joining guest are written to <c>Saves/Coop/Transfer</c>.</item>
/// </list>
/// The game lists only the top level of its save folder, so none of these appear in the solo save list.
/// The redirect is undone only on the main menu, so an autosave can never land in the solo folder.
/// </summary>
internal static class CoopSaves
{
    public enum Mode { None, Host, Guest }

    private static string _base;

    public static Mode Current { get; private set; }

    /// <summary>The game's save folder before the redirect (normally "Saves").</summary>
    public static string BaseDir => _base ?? SaveLoadSystem.s_saveDir;
    public static string CoopDir => BaseDir + "/Coop";
    public static string GuestSessionDir => CoopDir + "/GuestSession";
    public static string JoinedDir => CoopDir + "/Joined";
    public static string TransferDir => CoopDir + "/Transfer";

    /// <summary>Save file names of a Free Build world in the co-op folder (manual and automatic).</summary>
    private static IEnumerable<string> WorldFiles()
    {
        foreach (var n in new[] { SaveLoadSystem.s_freeBuildSaveName, SaveLoadSystem.s_autoFreeBuildSaveName })
        {
            yield return n;
            yield return n + ".zip";
        }
    }

    /// <summary>A co-op world exists (Continue is possible).</summary>
    public static bool HasCoopWorld() => WorldFiles().Any(n => File.Exists(Path.Combine(CoopDir, n)));

    public static void EnterHost()
    {
        Enter(Mode.Host, CoopDir);
        EnsureDefaultBuild();
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
        _base = null;
    }

    /// <summary>Guest: write a received load snapshot into the session folder. Returns the full path.</summary>
    public static string WriteGuestWorld(string fileName, byte[] data, SaveMetadata metadata)
    {
        var path = Path.Combine(GuestSessionDir, fileName);
        File.WriteAllBytes(path, data);
        CoopSavePair.WriteSidecar(path, data, metadata);
        return path;
    }

    /// <summary>Guest: keep the latest copy of the host's world. Never touches the guest's own co-op world or solo saves.</summary>
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
        var name = "defaultFreebuild.binary";
        if (File.Exists(Path.Combine(CoopDir, name)) || File.Exists(Path.Combine(CoopDir, name + ".zip"))) return;
        var source = Path.Combine(Application.streamingAssetsPath, "UtilitySaves", name);
        if (!File.Exists(source))
        {
            Plugin.Logger.LogWarning($"[saves] shipped default build not found at '{source}'; a new co-op world starts empty.");
            return;
        }
        File.Copy(source, Path.Combine(CoopDir, name));
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
}

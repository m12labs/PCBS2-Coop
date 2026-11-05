namespace PCBS2Coop.Network;

/// <summary>
/// One hidden backup per co-op save file (owner decision, phase 8): before the host overwrites a save, its current pair
/// is copied into the backup folder if it still reads. A save cut off by a crash or a closed game leaves a native file
/// that no longer matches its <c>.coop</c>; the next look at the save folder puts the backup back in its place and keeps
/// the unreadable files beside the backup as <c>.unreadable</c> for diagnosis.
/// </summary>
public static class CoopSaveBackups
{
    public const string UnreadableSuffix = ".unreadable";

    /// <summary>Whether the save at <paramref name="nativePath"/> and its <c>.coop</c> load together.</summary>
    public static bool Readable(string nativePath, out string? problem)
    {
        problem = null;
        try
        {
            if (!File.Exists(nativePath)) { problem = "The save file is missing."; return false; }
            CoopSavePair.ReadSidecar(nativePath);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { problem = e.Message; return false; }
    }

    /// <summary>
    /// Before a save replaces <paramref name="nativePath"/>: copy its pair into <paramref name="backupDir"/> when it reads,
    /// replacing the previous backup. An unreadable pair keeps the older backup. Returns whether a backup was made.
    /// </summary>
    public static bool Keep(string nativePath, string backupDir)
    {
        if (!Readable(nativePath, out _)) return false;
        Directory.CreateDirectory(backupDir);
        CopyPair(nativePath, Path.Combine(backupDir, Path.GetFileName(nativePath)));
        return true;
    }

    /// <summary>
    /// Recover a save that doesn't load with its pair. A raw write left beside a readable zip is moved aside.
    /// Otherwise a readable backup is put back. Returns whether recovery happened and why the save didn't read.
    /// </summary>
    public static bool Restore(string nativePath, string backupDir, out string? problem)
    {
        problem = null;
        if (!File.Exists(nativePath) || Readable(nativePath, out problem)) return false;
        var backup = Path.Combine(backupDir, Path.GetFileName(nativePath));
        var aside = backup + UnreadableSuffix;
        if (!Readable(backup, out _))
        {
            if (!nativePath.EndsWith(".binary", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(nativePath + CoopSavePair.Extension) || !Readable(nativePath + ".zip", out _)) return false;
            // The game writes the raw file before zipping it. Keep the last paired zip and move the unfinished write
            // before the game's load list quarantines it without telling co-op.
            Directory.CreateDirectory(backupDir);
            File.Move(nativePath, aside, true);
            return true;
        }
        File.Copy(nativePath, aside, true);
        if (File.Exists(nativePath + CoopSavePair.Extension)) File.Copy(nativePath + CoopSavePair.Extension, aside + CoopSavePair.Extension, true);
        else if (File.Exists(aside + CoopSavePair.Extension)) File.Delete(aside + CoopSavePair.Extension);
        CopyPair(backup, nativePath);
        return true;
    }

    /// <summary>A save was deleted: drop its backup too.</summary>
    public static void Delete(string fileName, string backupDir)
    {
        foreach (var name in new[] { fileName, fileName + UnreadableSuffix })
            foreach (var path in new[] { Path.Combine(backupDir, name), Path.Combine(backupDir, name) + CoopSavePair.Extension })
                if (File.Exists(path)) File.Delete(path);
        if (fileName.EndsWith(".binary.zip", StringComparison.OrdinalIgnoreCase))
        {
            var rawAside = Path.Combine(backupDir, fileName[..^4] + UnreadableSuffix);
            if (File.Exists(rawAside)) File.Delete(rawAside);
        }
    }

    /// <summary>Copy a native file and its sidecar: both land in temporary files first, then replace the targets.</summary>
    private static void CopyPair(string from, string to)
    {
        var tag = ".tmp-" + Guid.NewGuid().ToString("N");
        var native = to + tag; var pair = to + CoopSavePair.Extension + tag;
        try
        {
            File.Copy(from, native);
            File.Copy(from + CoopSavePair.Extension, pair);
            File.Move(native, to, true);
            File.Move(pair, to + CoopSavePair.Extension, true);
        }
        finally
        {
            if (File.Exists(native)) File.Delete(native);
            if (File.Exists(pair)) File.Delete(pair);
        }
    }
}

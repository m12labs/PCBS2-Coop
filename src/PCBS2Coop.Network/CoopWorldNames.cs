namespace PCBS2Coop.Network;

/// <summary>
/// Names of co-op worlds in the co-op save folder. A world is one file stem:
/// <list type="bullet">
/// <item><c>&lt;stem&gt;.binary</c>: its named save (written when the world is created, or by the game's Save screen).</item>
/// <item><c>&lt;stem&gt;--auto.binary</c>: its autosave (the game's Free Build autosave, redirected per world).</item>
/// </list>
/// The game may zip either file (<c>.binary.zip</c>). Stems use only the characters snapshot names allow
/// (<see cref="Protocol.IsValidSnapshotName"/>), so every save of a world can be sent to the guest as a copy.
/// The name players see is the stem with underscores shown as spaces: "Co-op World 2" is stored as "Co-op_World_2".
/// </summary>
public static class CoopWorldNames
{
    public const string AutoSuffix = "--auto";
    public const int MaxNameLength = 32;
    public const string DefaultName = "Co-op World";
    private const int MaxStemLength = 48 - 6; // room for the autosave suffix inside a snapshot name

    /// <summary>The world stem of a save file name ("X--auto.binary.zip" → "X").</summary>
    public static string StemOf(string fileName)
    {
        var s = Bare(fileName);
        return s.EndsWith(AutoSuffix, StringComparison.OrdinalIgnoreCase) ? s[..^AutoSuffix.Length] : s;
    }

    /// <summary>The file is a world's autosave.</summary>
    public static bool IsAutosave(string fileName) => Bare(fileName).EndsWith(AutoSuffix, StringComparison.OrdinalIgnoreCase);

    public static string FileOf(string stem) => stem + ".binary";
    public static string AutoFileOf(string stem) => stem + AutoSuffix + ".binary";

    /// <summary>The world's name as players see it.</summary>
    public static string WorldName(string stem) => stem.Replace('_', ' ');

    /// <summary>The name shown for one save file: "Co-op World 2" or "Co-op World 2 (auto)".</summary>
    public static string DisplayOf(string fileName) => WorldName(StemOf(fileName)) + (IsAutosave(fileName) ? " (auto)" : "");

    /// <summary>The stem for a name a player typed, or null with the reason it can't be used.</summary>
    public static string? StemFromName(string? name, out string error)
    {
        var n = string.Join(" ", (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        error = "";
        if (n.Length == 0) error = "Enter a name for the world.";
        else if (n.Length > MaxNameLength) error = $"Use at most {MaxNameLength} characters.";
        else if (!n.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or ' ' or '-'))
            error = "Use letters, numbers, spaces and hyphens only.";
        else if (n.Contains("--")) error = "Don't use two hyphens in a row.";
        else if (n.StartsWith("auto", StringComparison.OrdinalIgnoreCase)) error = "The name can't start with \"auto\" (the game keeps those for autosaves).";
        else if (n.Equals("defaultFreebuild", StringComparison.OrdinalIgnoreCase)) error = "That name is used by the game.";
        return error.Length == 0 ? n.Replace(' ', '_') : null;
    }

    /// <summary>
    /// A stem that can be sent for any save name (for example one typed in the game's own Save screen, which allows
    /// other characters): other characters become underscores.
    /// </summary>
    public static string SafeStem(string stem)
    {
        var chars = (stem ?? "").Select(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' ? c : '_').ToArray();
        var s = new string(chars).Replace("--", "-_");
        if (s.Length > MaxStemLength) s = s[..MaxStemLength];
        if (s.Length == 0) s = "World";
        if (s.StartsWith("auto", StringComparison.OrdinalIgnoreCase)) s = ("W_" + s)[..Math.Min(MaxStemLength, s.Length + 2)];
        return s;
    }

    /// <summary>"Co-op World N" with the lowest N no existing world uses.</summary>
    public static string NextDefaultName(IEnumerable<string> existingStems)
    {
        var taken = new HashSet<string>(existingStems, StringComparer.OrdinalIgnoreCase);
        for (var i = 1; ; i++)
        {
            var name = $"{DefaultName} {i}";
            if (!taken.Contains(name.Replace(' ', '_'))) return name;
        }
    }

    private static string Bare(string fileName)
    {
        var s = Path.GetFileName(fileName ?? "");
        if (s.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        if (s.EndsWith(".binary", StringComparison.OrdinalIgnoreCase)) s = s[..^7];
        return s;
    }
}

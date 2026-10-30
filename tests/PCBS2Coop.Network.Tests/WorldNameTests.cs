using PCBS2Coop.Network;

/// <summary>Co-op world file names: named save, autosave, display names and what a player may type.</summary>
internal static class WorldNameTests
{
    public static void Run(Checker t)
    {
        t.Section("Co-op worlds: file names");
        t.Check(CoopWorldNames.StemFromName("Co-op World 2", out _) == "Co-op_World_2", "typed name becomes a stem with underscores");
        t.Check(CoopWorldNames.StemFromName("  My   build ", out _) == "My_build", "extra spaces are dropped");
        t.Check(CoopWorldNames.FileOf("Co-op_World_2") == "Co-op_World_2.binary" && CoopWorldNames.AutoFileOf("Co-op_World_2") == "Co-op_World_2--auto.binary",
            "named save and autosave file names");
        t.Check(CoopWorldNames.StemOf("Co-op_World_2--auto.binary.zip") == "Co-op_World_2" && CoopWorldNames.StemOf("Co-op_World_2.binary") == "Co-op_World_2"
                && CoopWorldNames.StemOf("Co-op_World_2.BINARY.ZIP") == "Co-op_World_2", "both files of a world have the same stem (any case, zipped or not)");
        t.Check(CoopWorldNames.IsAutosave("X--auto.binary.zip") && !CoopWorldNames.IsAutosave("X.binary.zip"), "the autosave is recognized");
        t.Check(CoopWorldNames.DisplayOf("Co-op_World_2--auto.binary.zip") == "Co-op World 2 (auto)" && CoopWorldNames.DisplayOf("Co-op_World_2.binary") == "Co-op World 2",
            "display names: underscores shown as spaces, autosave marked");
        t.Check(!CoopWorldNames.DisplayOf("Co-op_World_2.binary").StartsWith("auto") && !CoopWorldNames.FileOf("Co-op_World_2").StartsWith("auto"),
            "world files never start with 'auto' (the game would label them just 'Autosave')");

        t.Section("Co-op worlds: every world save can be sent as a copy");
        foreach (var name in new[] { "Co-op World 1", "A", "abcdefghijklmnopqrstuvwxyz 12345", "x-y z" })
        {
            var stem = CoopWorldNames.StemFromName(name, out _)!;
            t.Check(Protocol.IsValidSnapshotName(CoopWorldNames.FileOf(stem) + ".zip") && Protocol.IsValidSnapshotName(CoopWorldNames.AutoFileOf(stem) + ".zip"),
                $"'{name}': both file names are valid snapshot names");
        }
        foreach (var raw in new[] { "My PC build!", "Ünïcode name", "autofreebuild", "", new string('a', 80), "a--b", "--auto" })
        {
            var stem = CoopWorldNames.SafeStem(raw);
            t.Check(Protocol.IsValidSnapshotName(CoopWorldNames.AutoFileOf(stem) + ".zip") && !stem.StartsWith("auto", StringComparison.OrdinalIgnoreCase)
                    && CoopWorldNames.StemOf(CoopWorldNames.AutoFileOf(stem)) == stem, $"SafeStem('{(raw.Length > 20 ? raw[..20] + "…" : raw)}') = '{stem}' is sendable and round-trips");
        }

        t.Section("Co-op worlds: names a player may type");
        string err;
        t.Check(CoopWorldNames.StemFromName("", out err) == null && err.Length > 0, "empty name refused");
        t.Check(CoopWorldNames.StemFromName(new string('a', CoopWorldNames.MaxNameLength + 1), out err) == null && err.Contains("32"), "too long refused");
        t.Check(CoopWorldNames.StemFromName(new string('a', CoopWorldNames.MaxNameLength), out _) != null, "longest allowed name accepted");
        t.Check(CoopWorldNames.StemFromName("my/world", out err) == null && err.Contains("letters"), "path characters refused");
        t.Check(CoopWorldNames.StemFromName("my_world", out _) == null, "underscore refused (it stands for a space)");
        t.Check(CoopWorldNames.StemFromName("a--b", out _) == null, "double hyphen refused (it marks the autosave)");
        t.Check(CoopWorldNames.StemFromName("Autosave 2", out _) == null && CoopWorldNames.StemFromName("autofreebuild", out _) == null, "names starting with 'auto' refused");
        t.Check(CoopWorldNames.StemFromName("defaultFreebuild", out _) == null, "the game's default build name refused");

        t.Section("Co-op worlds: default names");
        t.Check(CoopWorldNames.NextDefaultName(Array.Empty<string>()) == "Co-op World 1", "first world");
        t.Check(CoopWorldNames.NextDefaultName(new[] { "Co-op_World_1", "co-op_world_3", "Other" }) == "Co-op World 2", "lowest free number, any case");
        t.Check(CoopWorldNames.StemFromName(CoopWorldNames.NextDefaultName(new[] { "Co-op_World_1" }), out _) == "Co-op_World_2", "the default name is a valid name");
    }
}

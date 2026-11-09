using System.Security.Cryptography;
using LiteNetLib.Utils;
using PCBS2Coop.Network;

/// <summary>Phase 15b: the mode, run and scene a co-op save belongs to, and the checks that keep Free Build and Career
/// saves apart.</summary>
internal static class CampaignTests
{
    public static void Run(Checker t)
    {
        Campaigns(t);
        Layouts(t);
        Folders(t);
        Names(t);
    }

    private static bool Throws(Action a) { try { a(); return false; } catch (InvalidDataException) { return true; } }
    private static string Message(Action a) { try { a(); return ""; } catch (InvalidDataException e) { return e.Message; } }

    private static readonly byte[] Native = { 3, 1, 4, 1, 5 };
    private static readonly SnapshotPosition Position = new(2, 9, 77, new byte[] { 0, 0, 0, 0 });

    private static void Campaigns(Checker t)
    {
        t.Section("Career: modes, runs and workshop scenes");
        var career = CoopCampaign.NewCareer();
        t.Check(career.Mode == CoopGameMode.Career && career.RunId != Guid.Empty && career.Scene == "Stage_02_Workshop_Shop_Rundown",
            "a new career gets its own run ID and starts in the run-down shop");
        t.Check(CoopCampaign.NewCareer().RunId != career.RunId && CoopCampaign.NewFreeBuild().RunId != Guid.Empty, "every new run has a different ID");
        t.Check(CoopCampaign.Legacy.Mode == CoopGameMode.FreeBuild && CoopCampaign.Legacy.RunId == Guid.Empty && CoopCampaign.Legacy.Scene == "Stage_02_Workshop",
            "saves from before Career read as Free Build in the Free Build workshop");
        t.Check(CoopCampaign.Supports(CoopGameMode.Career, "Stage_02_Workshop_Shop_Rundown") && CoopCampaign.Supports(CoopGameMode.Career, "Stage_02_Workshop") &&
                !CoopCampaign.Supports(CoopGameMode.FreeBuild, "Stage_02_Workshop_Shop_Rundown") && !CoopCampaign.Supports(CoopGameMode.Career, "Stage_03_Other") &&
                !CoopCampaign.Supports(CoopGameMode.Career, null),
            "Career plays in the starting and renovated shop; Free Build only in its workshop; other workshops are refused");
        t.Check(CoopCampaign.IsWorkshop("Stage_02_Workshop_Shop_Rundown") && CoopCampaign.IsWorkshop("Stage_02_Workshop") && !CoopCampaign.IsWorkshop("Main_Menu"),
            "the mod treats both supported workshops as in-world scenes");
        var renovated = career.InScene(CoopCampaign.CareerRenovatedScene);
        t.Check(renovated.RunId == career.RunId && renovated.Mode == CoopGameMode.Career && renovated.Problem(CoopGameMode.Career) == null,
            "the same run continues in the renovated shop");
        var wrong = career.Problem(CoopGameMode.FreeBuild) ?? "";
        t.Check(wrong.Contains("Career") && wrong.Contains("Free Build"), $"a career save offered as Free Build is refused with a reason ({wrong})");
        var elsewhere = (career with { Scene = "Stage_09_Somewhere" }).Problem(CoopGameMode.Career) ?? "";
        t.Check(elsewhere.Contains("doesn't support") && elsewhere.Contains("Stage_09_Somewhere"), $"an unsupported workshop is named ({elsewhere})");
        var junk = (career with { Scene = "x\u0001/../y" }).Problem(CoopGameMode.Career) ?? "";
        t.Check(junk.Contains("unknown") && !junk.Contains("\u0001"), "an unreadable scene name isn't echoed");
    }

    /// <summary>The 0.21–0.57 layout: compressed state, no campaign.</summary>
    private static byte[] PackedLayout(string version)
    {
        var w = new NetDataWriter();
        w.Put(0x3252494150324350UL); w.Put(version); w.Put(Native.Length); w.Put(SHA256.HashData(Native));
        w.Put(Position.Generation); w.Put(Position.Revision); w.Put(Position.ChainHash);
        Protocol.WriteBlob(w, Protocol.PackState(Position.State)); w.Put(0);
        return w.CopyData();
    }

    /// <summary>This version's layout with a campaign written by hand (to test what reading refuses).</summary>
    private static byte[] CampaignLayout(byte mode, string scene)
    {
        var w = new NetDataWriter();
        w.Put(0x3352494150324350UL); w.Put(ModVersion.Value);
        w.Put(mode); w.Put(Guid.NewGuid().ToByteArray()); w.Put(scene, 64);
        w.Put(Native.Length); w.Put(SHA256.HashData(Native));
        w.Put(Position.Generation); w.Put(Position.Revision); w.Put(Position.ChainHash);
        Protocol.WriteBlob(w, Protocol.PackState(Position.State)); w.Put(0);
        return w.CopyData();
    }

    private static void Layouts(Checker t)
    {
        t.Section("Career: save pair layout");
        var career = CoopCampaign.NewCareer();
        var meta = CoopSavePair.Create(Native, Position, Array.Empty<CarrierPayload>(), career);
        var encoded = CoopSavePair.Encode(meta);
        var back = CoopSavePair.Decode(encoded);
        t.Check(BitConverter.ToUInt64(encoded, 0) == 0x3352494150324350UL && back.Campaign == career && back.Position.Revision == Position.Revision &&
                back.Position.State.SequenceEqual(Position.State), "this version writes the campaign layout and reads mode, run and scene back");
        var free = CoopSavePair.Decode(CoopSavePair.Encode(CoopSavePair.Create(Native, Position, Array.Empty<CarrierPayload>())));
        t.Check(free.Campaign == CoopCampaign.Legacy, "a pair made without a campaign is a legacy Free Build pair");
        var old = CoopSavePair.Decode(PackedLayout("0.57"));
        t.Check(old.Campaign == CoopCampaign.Legacy && old.Position.State.SequenceEqual(Position.State) && CoopSavePair.VersionProblem("0.57") == null,
            "a 0.57 co-op save still loads, as a Free Build world");
        t.Check(CoopSavePair.Decode(PackedLayout("0.16")).Campaign.Mode == CoopGameMode.FreeBuild, "the oldest readable saves read as Free Build too");
        var bad = Message(() => CoopSavePair.Decode(CampaignLayout(2, "Stage_07_Elsewhere")));
        t.Check(bad.Contains("Stage_07_Elsewhere"), $"a saved career in an unsupported workshop is refused with its scene ({bad})");
        t.Check(Throws(() => CoopSavePair.Decode(CampaignLayout(9, "Stage_02_Workshop"))), "an unknown game mode is refused");
        t.Check(Throws(() => CoopSavePair.Create(Native, Position, Array.Empty<CarrierPayload>(), career with { Scene = "Main_Menu" })),
            "a pair can't be made for a scene co-op doesn't play in");

        var bundle = CoopSavePair.Pack(Native, meta);
        var (native, received) = CoopSavePair.Unpack(bundle);
        t.Check(native.SequenceEqual(Native) && received.Campaign == career,
            "the snapshot a guest receives names the host's mode, run and scene (the guest loads it in that mode)");
    }

    private static void Folders(Checker t)
    {
        t.Section("Career: saves stay in their own mode");
        var dir = Path.Combine(Path.GetTempPath(), "pcbs2-campaign-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            void Save(string path, CoopCampaign campaign)
            {
                File.WriteAllBytes(path, Native);
                CoopSavePair.WriteSidecar(path, Native, CoopSavePair.Create(Native, Position, Array.Empty<CarrierPayload>(), campaign));
            }
            var careerSave = Path.Combine(dir, "Co-op_Career_1.binary.zip");
            var freeSave = Path.Combine(dir, "Co-op_World_1.binary.zip");
            Save(careerSave, CoopCampaign.NewCareer());
            Save(freeSave, CoopCampaign.NewFreeBuild());
            t.Check(CoopSaveBackups.Readable(careerSave, CoopGameMode.Career, out _) && CoopSaveBackups.Readable(freeSave, CoopGameMode.FreeBuild, out _),
                "each save reads in its own mode");
            t.Check(!CoopSaveBackups.Readable(careerSave, CoopGameMode.FreeBuild, out var asFree) && asFree!.Contains("Career") &&
                    !CoopSaveBackups.Readable(freeSave, CoopGameMode.Career, out var asCareer) && asCareer!.Contains("Free Build"),
                "a save found in the other mode's folder is refused for Continue and Load, with a reason");
            t.Check(!CoopSaveBackups.Readable(Path.Combine(dir, "missing.binary.zip"), CoopGameMode.Career, out var missing) && missing!.Contains("missing"),
                "a missing career save is reported");

            // A host save cut off before its pair: the backup goes back with its run unchanged.
            var backups = Path.Combine(dir, "Backup");
            var run = CoopSavePair.ReadSidecar(careerSave).Campaign;
            CoopSaveBackups.Keep(careerSave, backups);
            File.WriteAllBytes(careerSave, new byte[] { 7, 7, 7 });
            t.Check(CoopSaveBackups.Restore(careerSave, backups, out _) && CoopSavePair.ReadSidecar(careerSave).Campaign == run,
                "a restored career backup keeps its mode, run ID and scene");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void Names(Checker t)
    {
        t.Section("Career: names");
        t.Check(CoopWorldNames.NextDefaultName(Array.Empty<string>(), CoopGameMode.Career) == "Co-op Career 1" &&
                CoopWorldNames.NextDefaultName(new[] { "Co-op_Career_1", "Co-op_World_2" }, CoopGameMode.Career) == "Co-op Career 2",
            "a new career is suggested as \"Co-op Career N\" (lowest free number)");
        t.Check(CoopWorldNames.NextDefaultName(new[] { "Co-op_Career_1" }) == "Co-op World 1", "Free Build keeps \"Co-op World N\"");
        var stem = CoopWorldNames.StemFromName("Co-op Career 1", out _)!;
        t.Check(stem == "Co-op_Career_1" && CoopWorldNames.AutoFileOf(stem) == "Co-op_Career_1--auto.binary" &&
                Protocol.IsValidSnapshotName(CoopWorldNames.AutoFileOf(stem) + ".zip"),
            "a career has a named save and its own autosave, both sendable to the guest");
        t.Check(CoopWorldNames.StemFromName("auto", out var err) == null && err.Contains("auto"),
            "a career can't be named like the game's own Career autosave (auto.binary)");
    }
}

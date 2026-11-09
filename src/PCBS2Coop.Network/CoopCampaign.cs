using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>The game mode a co-op world is played in (phase 15). Values are stored in co-op saves.</summary>
public enum CoopGameMode : byte { FreeBuild = 1, Career = 2 }

/// <summary>
/// Which run a co-op save belongs to: its mode, a run ID made when the world starts, and the workshop scene it was saved
/// in. The host writes it into every save pair, and the guest loads the host's snapshot with it. Pairs written before
/// 0.58 have none and read as <see cref="Legacy"/> (a Free Build world in the Free Build workshop).
/// </summary>
public sealed record CoopCampaign(CoopGameMode Mode, Guid RunId, string Scene)
{
    public const string FreeBuildScene = "Stage_02_Workshop";
    /// <summary>A new standard Career starts in the run-down shop (decompile: <c>NewMainMenu.PlayCareer</c>).</summary>
    public const string CareerStartScene = "Stage_02_Workshop_Shop_Rundown";
    /// <summary>After the native shop renovation, Career continues in the Free Build workshop scene
    /// (decompile: <c>CareerStatus.UpgradeShop</c> loads <c>Stage_02_Workshop</c>).</summary>
    public const string CareerRenovatedScene = FreeBuildScene;
    private const int MaxSceneLength = 64;

    /// <summary>A pair from before 0.58: Free Build, no run ID.</summary>
    public static CoopCampaign Legacy { get; } = new(CoopGameMode.FreeBuild, Guid.Empty, FreeBuildScene);

    public static CoopCampaign NewFreeBuild() => new(CoopGameMode.FreeBuild, Guid.NewGuid(), FreeBuildScene);
    public static CoopCampaign NewCareer() => new(CoopGameMode.Career, Guid.NewGuid(), CareerStartScene);

    /// <summary>Workshop scenes co-op supports in this mode (the first supported Career is the standard starting shop).</summary>
    public static bool Supports(CoopGameMode mode, string? scene) => mode switch
    {
        CoopGameMode.FreeBuild => scene == FreeBuildScene,
        CoopGameMode.Career => scene is CareerStartScene or CareerRenovatedScene,
        _ => false,
    };

    /// <summary>Any scene a co-op world can be played in.</summary>
    public static bool IsWorkshop(string? scene) => Supports(CoopGameMode.FreeBuild, scene) || Supports(CoopGameMode.Career, scene);

    /// <summary>The same run saved in another (supported) scene, for example after the shop renovation.</summary>
    public CoopCampaign InScene(string scene) => this with { Scene = scene };

    public static string ModeName(CoopGameMode mode) => mode == CoopGameMode.Career ? "Career" : "Free Build";

    /// <summary>Why this save can't be played as a <paramref name="expected"/> world, or null.</summary>
    public string? Problem(CoopGameMode expected)
    {
        if (Mode != expected) return $"This is a co-op {ModeName(Mode)} save, not a {ModeName(expected)} one.";
        if (!Supports(Mode, Scene)) return $"This co-op {ModeName(Mode)} save is in a workshop co-op doesn't support yet ({Shown(Scene)}).";
        return null;
    }

    private static string Shown(string scene) => scene.Length is > 0 and <= MaxSceneLength && scene.All(c => char.IsLetterOrDigit(c) || c == '_') ? scene : "unknown";

    internal void Write(NetDataWriter w)
    {
        w.Put((byte)Mode);
        w.Put(RunId.ToByteArray());
        w.Put(Scene, MaxSceneLength);
    }

    internal static CoopCampaign Read(NetDataReader r)
    {
        var mode = (CoopGameMode)r.GetByte();
        var id = new byte[16]; r.GetBytes(id, id.Length);
        var scene = r.GetString(MaxSceneLength);
        var campaign = new CoopCampaign(mode, new Guid(id), scene);
        if (!Enum.IsDefined(typeof(CoopGameMode), mode)) throw new InvalidDataException("Unknown co-op game mode in the save.");
        if (!Supports(mode, scene)) throw new InvalidDataException(campaign.Problem(mode));
        return campaign;
    }
}

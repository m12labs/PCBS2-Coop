using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>Which game build is running. Read from Unity's Application.version / buildGUID by the plugin.</summary>
public sealed record GameIdentity(string Version, string BuildGuid)
{
    public static readonly GameIdentity Unknown = new("", "");
    public bool IsKnown => Version.Length > 0 && BuildGuid.Length > 0;
    public override string ToString() => IsKnown ? $"{Version} ({BuildGuid})" : "unknown";
}

/// <summary>
/// Which content is loaded: DLC packs and a hash of the part catalog IDs.
/// <see cref="CatalogCount"/> is -1 when the catalog wasn't loaded yet and couldn't be compared.
/// </summary>
public sealed record ContentIdentity(IReadOnlyList<string> Dlcs, string CatalogHash, int CatalogCount)
{
    public static readonly ContentIdentity Unknown = new(Array.Empty<string>(), "", -1);
    public bool CatalogKnown => CatalogCount >= 0;
    public override string ToString() =>
        $"DLC [{string.Join(", ", Dlcs)}], catalog {(CatalogKnown ? $"{CatalogCount} parts {CatalogHash}" : "not loaded")}";
}

/// <summary>Everything a peer tells the other when connecting.</summary>
public sealed record PeerIdentity(PlayerId Player, string ModVersion, GameIdentity Game, ContentIdentity Content)
{
    public void Write(NetDataWriter w)
    {
        Player.Write(w);
        w.Put(ModVersion, Protocol.MaxShortString);
        w.Put(Game.Version, Protocol.MaxShortString);
        w.Put(Game.BuildGuid, Protocol.MaxShortString);
        w.Put((byte)Math.Min(Content.Dlcs.Count, Protocol.MaxDlcEntries));
        for (var i = 0; i < Math.Min(Content.Dlcs.Count, Protocol.MaxDlcEntries); i++) w.Put(Content.Dlcs[i], Protocol.MaxShortString);
        w.Put(Content.CatalogHash, Protocol.MaxShortString);
        w.Put(Content.CatalogCount);
    }

    /// <summary>Reads and bounds-checks an identity. Throws <see cref="FormatException"/> on bad data.</summary>
    public static PeerIdentity Read(NetDataReader r)
    {
        var player = PlayerId.Read(r);
        var mod = Protocol.ReadString(r);
        var game = new GameIdentity(Protocol.ReadString(r), Protocol.ReadString(r));
        var n = r.GetByte();
        if (n > Protocol.MaxDlcEntries) throw new FormatException("Too many DLC entries.");
        var dlcs = new string[n];
        for (var i = 0; i < n; i++) dlcs[i] = Protocol.ReadString(r);
        var hash = Protocol.ReadString(r);
        var count = r.GetInt();
        if (count < -1) throw new FormatException("Bad catalog count.");
        return new PeerIdentity(player, mod, game, new ContentIdentity(dlcs, hash, count));
    }

    public override string ToString() => $"player {Player}, mod {ModVersion}, game {Game}, {Content}";
}

/// <summary>
/// Outcome of comparing two peers. <see cref="Problems"/> refuse the join; <see cref="Warnings"/> allow it.
/// Owner decision (Oct 25, 2026): refuse a different mod version or game build; warn on DLC or part catalog differences.
/// </summary>
public sealed record CompatibilityResult(IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings)
{
    public bool Compatible => Problems.Count == 0;
}

public static class Compatibility
{
    public static CompatibilityResult Check(PeerIdentity host, PeerIdentity guest)
    {
        var problems = new List<string>();
        var warnings = new List<string>();

        if (host.ModVersion != guest.ModVersion)
            problems.Add($"Different mod versions (host {host.ModVersion}, you {guest.ModVersion}).");

        if (!host.Game.IsKnown || !guest.Game.IsKnown)
            warnings.Add("The game build could not be read on one side, so it was not compared.");
        else if (host.Game.Version != guest.Game.Version || host.Game.BuildGuid != guest.Game.BuildGuid)
            problems.Add($"Different game builds (host {host.Game}, you {guest.Game}). Both players need the same game update.");

        var hostDlc = new HashSet<string>(host.Content.Dlcs, StringComparer.Ordinal);
        var guestDlc = new HashSet<string>(guest.Content.Dlcs, StringComparer.Ordinal);
        var onlyHost = hostDlc.Except(guestDlc).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var onlyGuest = guestDlc.Except(hostDlc).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (onlyHost.Count > 0) warnings.Add($"DLC only the host has: {string.Join(", ", onlyHost)}.");
        if (onlyGuest.Count > 0) warnings.Add($"DLC only the guest has: {string.Join(", ", onlyGuest)}.");

        if (!host.Content.CatalogKnown || !guest.Content.CatalogKnown)
            warnings.Add("The part catalog was not loaded on one side, so it was not compared.");
        else if (host.Content.CatalogHash != guest.Content.CatalogHash || host.Content.CatalogCount != guest.Content.CatalogCount)
            warnings.Add($"Different part catalogs (host {host.Content.CatalogCount} parts, guest {guest.Content.CatalogCount}).");

        if (!host.Player.IsEmpty && host.Player == guest.Player)
            warnings.Add("Both players have the same install ID (two copies on one PC?). Rejoin can't tell them apart.");

        return new CompatibilityResult(problems, warnings);
    }

    /// <summary>Stable hash of a set of IDs (order-independent), used for the part catalog.</summary>
    public static string HashIds(IEnumerable<string> ids)
    {
        var sorted = ids.Where(s => s != null).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", sorted));
        return Convert.ToHexString(sha.ComputeHash(bytes))[..16].ToLowerInvariant();
    }
}

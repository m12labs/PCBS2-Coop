namespace PCBS2Coop.Network;

/// <summary>Case.OnPickup returns installed USB devices to inventory when a motherboard is fitted, including case ports.
/// This native side effect belongs to the same accepted transfer, including cached generated-copy identities.</summary>
public static class PickupUsb
{
    public static string[] Roots(PcFact pc)
    {
        var board = pc.Links.FirstOrDefault(l => l.Path == "motherboardID")?.Part ?? default;
        if (board.IsNone) return Array.Empty<string>();
        // Motherboard.usbSlots includes the case's cached USB slots too (runtime guest pickup, 0.89).
        var devices = pc.Parts.Where(p => p.NativeType == "USBDrivePartInstance").Select(p => p.Id).ToHashSet();
        return pc.Links.Where(l => devices.Contains(l.Part) && l.Path.StartsWith("usbSlots[", StringComparison.Ordinal) &&
            l.Path.EndsWith("]", StringComparison.Ordinal) && !l.Path.Contains('.')).Select(l => l.Path).ToArray();
    }

    public static PcFact Remove(PcFact pc)
    {
        var roots = Roots(pc);
        if (roots.Length == 0) return pc;
        var links = pc.Links.Where(l => !roots.Any(root => l.Path == root || l.Path.StartsWith(root + ".", StringComparison.Ordinal))).ToArray();
        var liveParts = links.Select(l => l.Part).ToHashSet();
        var resources = pc.Resources.Where(r => liveParts.Contains(r.Parent)).ToArray();
        var liveResources = resources.Select(r => r.Id).ToHashSet();
        var rows = resources.Select(r =>
        {
            var occupant = liveParts.Contains(r.Occupant) ? r.Occupant : default;
            var dependencies = r.Dependencies.Where(liveResources.Contains).ToArray();
            var coupled = r.Coupled.Where(liveResources.Contains).ToArray();
            var closes = r.LoadCloses.Where(liveResources.Contains).ToArray();
            var carries = r.Carries.Where(liveResources.Contains).ToArray();
            var changed = occupant != r.Occupant || dependencies.Length != r.Dependencies.Count || coupled.Length != r.Coupled.Count ||
                closes.Length != r.LoadCloses.Count || carries.Length != r.Carries.Count;
            return changed ? r with { Occupant = occupant, Dependencies = dependencies, Coupled = coupled, LoadCloses = closes,
                Carries = carries, Version = checked(r.Version + 1) } : r;
        }).ToArray();
        return pc with { Parts = pc.Parts.Where(p => liveParts.Contains(p.Id)).ToArray(), Links = links, Resources = rows };
    }
}

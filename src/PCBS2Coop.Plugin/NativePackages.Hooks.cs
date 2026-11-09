using HarmonyLib;

namespace PCBS2Coop;

/// <summary>
/// Phase 16c hooks (owner decisions, November 8, 2026). In a co-op Career the host's own package and note actions run
/// the game's code and its game shares the result; the guest's are asked first (<see cref="NativePackages"/>) and run
/// here once the host accepts (under <see cref="NativeProjection"/>, which these hooks let through). Native addresses
/// checked distinct (decompile RVAs).
/// </summary>
internal static class PackageHooks
{
    private static bool Guest => NativeCareer.InCareer && !NativeProjection.Active && CoopSaves.Current != CoopSaves.Mode.Host;

    private static bool Ask(System.Action<NativePackages> request)
    {
        if (!Guest) return true;
        try { if (NativePackages.Current is { } p) request(p); }
        catch (System.Exception e) { Plugin.Logger.LogError("[packages] hook failed: " + e); }
        return false;
    }

    [HarmonyPatch(typeof(ItemDisplay), nameof(ItemDisplay.ButtonPressedPackage))]
    internal static class PackItem
    {
        private static bool Prefix(ItemDisplay __instance) => Ask(p => p.RequestPack(__instance.item));
    }

    [HarmonyPatch(typeof(WorkshopController), nameof(WorkshopController.PickupPackage))]
    internal static class TakePackage
    {
        private static bool Prefix(BenchSlot __0) => Ask(p => p.RequestTake(__0));
    }

    [HarmonyPatch(typeof(WorkshopController), nameof(WorkshopController.DropPackage))]
    internal static class PlacePackage
    {
        private static bool Prefix(BenchSlot __0) => Ask(p => p.RequestPlace(__0));
    }

    [HarmonyPatch(typeof(WorkshopController), nameof(WorkshopController.EmptyPackage))]
    internal static class OpenCarried
    {
        private static bool Prefix() => Ask(p => p.RequestOpen(null));
    }

    [HarmonyPatch(typeof(WorkshopController), nameof(WorkshopController.EmptyPackageSlot))]
    internal static class OpenOnBench
    {
        private static bool Prefix(BenchSlot __0) => Ask(p => p.RequestOpen(__0));
    }

    [HarmonyPatch(typeof(WorkshopController), nameof(WorkshopController.PickUpPostIt))]
    internal static class TakeNote
    {
        private static bool Prefix(PostIt __0, ref bool __result)
        {
            if (!Guest) return true;
            __result = false;
            return Ask(p => p.RequestNoteTake(__0));
        }
    }

    [HarmonyPatch(typeof(WorkshopController), nameof(WorkshopController.DropPostIt), typeof(PostItSlot))]
    internal static class DropNote
    {
        private static bool Prefix(PostItSlot __0) => Ask(p => p.RequestNoteDrop(__0));
    }

    [HarmonyPatch(typeof(WorkshopController), nameof(WorkshopController.DropPostIt), typeof(BenchSlot))]
    internal static class DropNoteOnBench
    {
        private static bool Prefix(BenchSlot __0) => Ask(p => p.RequestNoteDrop(__0));
    }

    internal static void Apply(Harmony h)
    {
        foreach (var t in new[] { typeof(PackItem), typeof(TakePackage), typeof(PlacePackage), typeof(OpenCarried), typeof(OpenOnBench), typeof(TakeNote),
                     typeof(DropNote), typeof(DropNoteOnBench) })
            h.PatchAll(t);
    }
}

using HarmonyLib;
using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>
/// Phase 16b hooks (owner decisions, November 8, 2026). In a co-op Career the host's own Buy and Sell run the game's
/// code; the guest's are sent to the host (<see cref="NativeCommerce"/>). A click the other player's desktop mirrors
/// here never buys: the buyer's game sends the order. Native addresses checked distinct (decompile RVAs).
/// </summary>
internal static class CommerceHooks
{
    private static bool Guest => NativeCareer.InCareer && !NativeProjection.Active && CoopSaves.Current != CoopSaves.Mode.Host;

    [HarmonyPatch(typeof(Checkout), nameof(Checkout.OnBuy))]
    internal static class ShopCheckout
    {
        private static bool Prefix(Checkout __instance, ref bool __result)
        {
            if (!NativeCareer.InCareer || NativeProjection.Active) return true;
            if (NativeDesktopInput.Injecting)
            {
                Plugin.Logger.LogInfo("[shop] the other player's mirrored Buy click: their game sends the order.");
                __result = false; return false;
            }
            if (CoopSaves.Current == CoopSaves.Mode.Host) { NativeCommerce.InHostCheckout = true; return true; }
            __result = false;
            try { NativeCommerce.Current?.RequestOrder(__instance); }
            catch (System.Exception e) { Plugin.Logger.LogError("[shop] checkout hook failed: " + e); }
            return false;
        }

        private static void Finalizer() => NativeCommerce.InHostCheckout = false;
    }

    /// <summary>The host's checkout places a same-day case in the delivery box: co-op makes PCs only through its own
    /// case spawn, which both games run.</summary>
    [HarmonyPatch(typeof(WorkshopController), nameof(WorkshopController.SpawnCase))]
    internal static class CheckoutCase
    {
        private static bool Prefix(PartInstance part, ref bool __result)
        {
            if (!NativeCommerce.InHostCheckout || !NativeCareer.InCareer || NativeProjection.Active || part == null) return true;
            CareerStatus.Get()?.AddDelivery(part);
            Plugin.Logger.LogInfo("[shop] a same-day case went to the delivery box (co-op).");
            __result = true;
            return false;
        }
    }

    /// <summary>Selling one item from the inventory (the confirmation's answer).</summary>
    [HarmonyPatch(typeof(ItemDisplay), "SellItem")]
    internal static class SellOne
    {
        private static bool Prefix(ItemDisplay __instance, bool doIt)
        {
            if (!doIt || !Guest) return true;
            try
            {
                var id = NativeCareer.Current?.IdForUse(__instance.item) ?? 0;
                if (id == 0) CoopController.Instance?.Notice("That part isn't in the shared inventory yet; try again in a moment.");
                else NativeCommerce.Current?.RequestSale(new[] { id });
            }
            catch (System.Exception e) { Plugin.Logger.LogError("[shop] sell hook failed: " + e); }
            return false;
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.SellAllBroken))]
    internal static class SellBroken
    {
        private static bool Prefix()
        {
            if (!Guest) return true;
            try
            {
                var ids = NativeCareer.Current?.BrokenItems() ?? new List<ulong>();
                if (ids.Count == 0) CoopController.Instance?.Notice("There are no broken parts in the shared inventory.");
                else NativeCommerce.Current?.RequestSale(ids);
            }
            catch (System.Exception e) { Plugin.Logger.LogError("[shop] sell hook failed: " + e); }
            return false;
        }
    }

    /// <summary>Deliveries are shared in phase 16c; until then the host collects them.</summary>
    [HarmonyPatch(typeof(CareerStatus), nameof(CareerStatus.CollectDeliveries))]
    internal static class CollectDeliveries
    {
        private static bool Prefix() => CareerGuards.Allow("collect deliveries");
    }

    internal static void Apply(Harmony h)
    {
        foreach (var t in new[] { typeof(ShopCheckout), typeof(CheckoutCase), typeof(SellOne), typeof(SellBroken), typeof(CollectDeliveries) })
            h.PatchAll(t);
    }
}

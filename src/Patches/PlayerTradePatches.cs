using GraveyardKeeperCoop.Multiplayer;
using HarmonyLib;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Reuses the game's controller-ready vendor screen as a player trade screen.
    /// Vanilla vendor pricing and inventory mutation are bypassed only while the
    /// trade manager owns this particular VendorGUI instance.
    /// </summary>
    [HarmonyPatch]
    public static class PlayerTradePatches
    {
        [HarmonyPatch(typeof(VendorGUI), "MoveItem")]
        [HarmonyPrefix]
        private static bool MoveItemPrefix(VendorGUI __instance, int count)
        {
            PlayerTradeManager manager = PlayerTradeManager.Instance;
            if (manager?.OwnsGui(__instance) != true)
                return true;
            manager.PatchMoveItem(__instance, count);
            return false;
        }

        [HarmonyPatch(typeof(VendorGUI), "UpdateTips")]
        [HarmonyPrefix]
        private static bool UpdateTipsPrefix(VendorGUI __instance)
        {
            PlayerTradeManager manager = PlayerTradeManager.Instance;
            return manager?.PatchUpdateTips(__instance) != false;
        }

        [HarmonyPatch(typeof(VendorGUI), nameof(VendorGUI.FinishOffer))]
        [HarmonyPrefix]
        private static bool FinishOfferPrefix(VendorGUI __instance)
        {
            PlayerTradeManager manager = PlayerTradeManager.Instance;
            if (manager?.OwnsGui(__instance) != true)
                return true;
            manager.PatchFinishOffer();
            return false;
        }

        [HarmonyPatch(typeof(VendorGUI), "Redraw")]
        [HarmonyPrefix]
        private static bool RedrawPrefix(VendorGUI __instance)
        {
            PlayerTradeManager manager = PlayerTradeManager.Instance;
            if (manager?.OwnsGui(__instance) != true)
                return true;
            manager.RedrawTradeGui();
            return false;
        }

        [HarmonyPatch(typeof(VendorGUI), "RefreshButtonsState")]
        [HarmonyPrefix]
        private static bool RefreshButtonsStatePrefix(VendorGUI __instance)
        {
            PlayerTradeManager manager = PlayerTradeManager.Instance;
            if (manager?.OwnsGui(__instance) != true)
                return true;
            manager.RedrawTradeGui();
            return false;
        }

        [HarmonyPatch(typeof(VendorGUI), "ResetOrder")]
        [HarmonyPrefix]
        private static bool ResetOrderPrefix(VendorGUI __instance)
        {
            PlayerTradeManager manager = PlayerTradeManager.Instance;
            if (manager?.OwnsGui(__instance) != true)
                return true;
            manager.PatchResetOrder();
            return false;
        }

        [HarmonyPatch(typeof(VendorGUI), "OfferIsEmpty")]
        [HarmonyPrefix]
        private static bool OfferIsEmptyPrefix(VendorGUI __instance, ref bool __result)
        {
            PlayerTradeManager manager = PlayerTradeManager.Instance;
            if (manager?.OwnsGui(__instance) != true)
                return true;
            __result = manager.PatchOfferIsEmpty();
            return false;
        }

        [HarmonyPatch(typeof(VendorGUI), nameof(VendorGUI.OnClosePressed))]
        [HarmonyPrefix]
        private static bool OnClosePressedPrefix(VendorGUI __instance)
        {
            PlayerTradeManager manager = PlayerTradeManager.Instance;
            if (manager?.OwnsGui(__instance) != true)
                return true;
            manager.PatchClose();
            return false;
        }
    }
}

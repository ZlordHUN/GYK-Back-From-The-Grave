using System;
using System.Collections.Generic;
using HarmonyLib;
using GraveyardKeeperCoop.Multiplayer;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch]
    internal static class InventoryMutationSyncPatches
    {
        [HarmonyPatch(typeof(WorldGameObject), "AddToInventory", new Type[] { typeof(Item) })]
        [HarmonyPostfix]
        private static void WorldGameObject_AddToInventory_Postfix(
            WorldGameObject __instance,
            Item item,
            bool __result)
        {
            if (!__result || InventorySync.IsApplyingRemote)
                return;

            bool isBody =
                item?.definition?.type == ItemDefinition.ItemType.Body;
            ItemDefinition.ItemType itemType =
                item?.definition?.type ?? ItemDefinition.ItemType.None;
            bool isExtractedBodyPart =
                __instance?.is_player == true &&
                (itemType == ItemDefinition.ItemType.BodyHeadPart ||
                 itemType == ItemDefinition.ItemType.BodyBodyPart ||
                 itemType == ItemDefinition.ItemType.BodyArmPart ||
                 itemType == ItemDefinition.ItemType.BodyLegPart ||
                 itemType == ItemDefinition.ItemType.BodyUniversalPart ||
                 itemType == ItemDefinition.ItemType.SoulBodyPart);
            if (isBody)
            {
                CoopMod.Logger.LogInfo(
                    $"[InventorySync] Body inserted into {__instance?.obj_id ?? "unknown"}; sending authoritative inventory immediately");
            }
            else if (isExtractedBodyPart)
            {
                CoopMod.Logger.LogInfo(
                    $"[InventorySync] Extracted body part '{item?.id ?? "unknown"}' " +
                    "added to the local player; sending inventory immediately");
            }
            InventorySync.Instance?.NotifyInventoryMutated(
                __instance,
                force: isBody || isExtractedBodyPart);
        }

        [HarmonyPatch(typeof(WorldGameObject), "GiveItemToPlayersHands", new Type[] { typeof(Item) })]
        [HarmonyPostfix]
        private static void WorldGameObject_GiveItemToPlayersHands_Postfix(
            WorldGameObject __instance,
            Item item)
        {
            if (InventorySync.IsApplyingRemote ||
                item?.definition?.type != ItemDefinition.ItemType.Body)
            {
                return;
            }

            int duplicateBodiesRemoved = 0;
            if (__instance != null &&
                __instance.is_autopsy_table &&
                __instance.data?.inventory != null)
            {
                for (int i = __instance.data.inventory.Count - 1; i >= 0; i--)
                {
                    Item remaining = __instance.data.inventory[i];
                    if (remaining?.definition?.type !=
                        ItemDefinition.ItemType.Body)
                    {
                        continue;
                    }

                    __instance.data.inventory.RemoveAt(i);
                    duplicateBodiesRemoved++;
                }
            }

            CoopMod.Logger.LogInfo(
                $"[InventorySync] Body removed from {__instance?.obj_id ?? "unknown"}; " +
                $"cleared_duplicate_bodies={duplicateBodiesRemoved}, sending authoritative inventory immediately");
            InventorySync.Instance?.NotifyInventoryMutated(
                __instance,
                force: true);
        }

        [HarmonyPatch(typeof(Item), "RemoveItem", new Type[] { typeof(string), typeof(int), typeof(Item) })]
        [HarmonyPostfix]
        private static void Item_RemoveItemById_Postfix(Item __instance, bool __result)
        {
            if (__result)
                InventorySync.Instance?.NotifyInventoryDataMutated(__instance);
        }

        [HarmonyPatch(typeof(Item), "RemoveItem", new Type[] { typeof(Item), typeof(int), typeof(Item) })]
        [HarmonyPostfix]
        private static void Item_RemoveItemByItem_Postfix(Item __instance, bool __result)
        {
            if (__result)
                InventorySync.Instance?.NotifyInventoryDataMutated(__instance);
        }

        [HarmonyPatch(typeof(Item), "RemoveItems", new Type[] { typeof(List<Item>), typeof(int) })]
        [HarmonyPostfix]
        private static void Item_RemoveItems_Postfix(Item __instance, bool __result)
        {
            if (__result)
                InventorySync.Instance?.NotifyInventoryDataMutated(__instance);
        }

        [HarmonyPatch(typeof(Item), "RemoveItemByIndex", new Type[] { typeof(int) })]
        [HarmonyPostfix]
        private static void Item_RemoveItemByIndex_Postfix(Item __instance, bool __result)
        {
            if (__result)
                InventorySync.Instance?.NotifyInventoryDataMutated(__instance);
        }

        [HarmonyPatch(typeof(Item), "RemoveNotFoldedItem", new Type[] { typeof(Item) })]
        [HarmonyPostfix]
        private static void Item_RemoveNotFoldedItem_Postfix(Item __instance)
        {
            InventorySync.Instance?.NotifyInventoryDataMutated(__instance);
        }

        [HarmonyPatch(typeof(InventoryPanelGUI), "Hide")]
        [HarmonyPrefix]
        private static void InventoryPanelGUI_Hide_Prefix()
        {
            InventorySync.Instance?.FlushNow();
        }
    }
}

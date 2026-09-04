using System;
using System.Collections.Generic;
using HarmonyLib;
using GraveyardKeeperCoop.Multiplayer;
using UnityEngine;

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
            if (!__result || InventorySync.IsSuppressingMutationCapture)
                return;

            bool isBody =
                item?.definition?.type == ItemDefinition.ItemType.Body;
            if (isBody)
            {
                CoopMod.Logger.LogInfo(
                    $"[InventorySync] Body inserted into {__instance?.obj_id ?? "unknown"}; sending authoritative inventory immediately");
            }
            InventorySync.Instance?.NotifyInventoryMutated(
                __instance,
                force: isBody);
            if (__instance == MainGame.me?.player)
            {
                JoinerProfileManager.NotifyPersonalInventoryChanged(
                    $"picked up {item?.id ?? "item"}");
            }
        }

        [HarmonyPatch(typeof(Item), "AddItem", new Type[]
        {
            typeof(Item),
            typeof(bool)
        })]
        [HarmonyPostfix]
        private static void Item_AddItem_Postfix(
            Item __instance,
            Item item,
            bool __result)
        {
            if (!__result || InventorySync.IsSuppressingMutationCapture)
                return;

            InventorySync.Instance?.NotifyInventoryDataMutated(__instance);
            if (JoinerProfileManager.IsOwnedByLocalPlayer(__instance))
            {
                JoinerProfileManager.NotifyPersonalInventoryChanged(
                    $"added {item?.id ?? "item"}");
            }
        }

        [HarmonyPatch(typeof(WorldGameObject), "GiveItemToPlayersHands", new Type[] { typeof(Item) })]
        [HarmonyPostfix]
        private static void WorldGameObject_GiveItemToPlayersHands_Postfix(
            WorldGameObject __instance,
            Item item)
        {
            if (InventorySync.IsSuppressingMutationCapture ||
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
            if (__result && !InventorySync.IsSuppressingMutationCapture)
            {
                InventorySync.Instance?.NotifyInventoryDataMutated(__instance);
                NotifyPersonalRemoval(__instance);
            }
        }

        [HarmonyPatch(typeof(Item), "RemoveItem", new Type[] { typeof(Item), typeof(int), typeof(Item) })]
        [HarmonyPostfix]
        private static void Item_RemoveItemByItem_Postfix(Item __instance, bool __result)
        {
            if (__result && !InventorySync.IsSuppressingMutationCapture)
            {
                InventorySync.Instance?.NotifyInventoryDataMutated(__instance);
                NotifyPersonalRemoval(__instance);
            }
        }

        [HarmonyPatch(typeof(Item), "RemoveItems", new Type[] { typeof(List<Item>), typeof(int) })]
        [HarmonyPostfix]
        private static void Item_RemoveItems_Postfix(Item __instance, bool __result)
        {
            if (__result && !InventorySync.IsSuppressingMutationCapture)
            {
                InventorySync.Instance?.NotifyInventoryDataMutated(__instance);
                NotifyPersonalRemoval(__instance);
            }
        }

        [HarmonyPatch(typeof(Item), "RemoveItemByIndex", new Type[] { typeof(int) })]
        [HarmonyPostfix]
        private static void Item_RemoveItemByIndex_Postfix(Item __instance, bool __result)
        {
            if (__result && !InventorySync.IsSuppressingMutationCapture)
            {
                InventorySync.Instance?.NotifyInventoryDataMutated(__instance);
                NotifyPersonalRemoval(__instance);
            }
        }

        [HarmonyPatch(typeof(Item), "RemoveNotFoldedItem", new Type[] { typeof(Item) })]
        [HarmonyPostfix]
        private static void Item_RemoveNotFoldedItem_Postfix(Item __instance)
        {
            if (!InventorySync.IsSuppressingMutationCapture)
            {
                InventorySync.Instance?.NotifyInventoryDataMutated(__instance);
                NotifyPersonalRemoval(__instance);
            }
        }

        private static void NotifyPersonalRemoval(Item owner)
        {
            if (JoinerProfileManager.IsOwnedByLocalPlayer(owner))
            {
                JoinerProfileManager.NotifyPersonalInventoryChanged(
                    "removed personal inventory item");
            }
        }

        [HarmonyPatch(typeof(InventoryPanelGUI), "Hide")]
        [HarmonyPrefix]
        private static void InventoryPanelGUI_Hide_Prefix()
        {
            InventorySync.Instance?.FlushNow();
        }
    }

    [HarmonyPatch(
        typeof(WorldGameObject),
        nameof(WorldGameObject.UseItemFromInventory),
        new Type[]
        {
            typeof(Item),
            typeof(Nullable<Vector3>),
            typeof(Item)
        })]
    internal static class PersonalConsumableUsePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            WorldGameObject __instance,
            Item item,
            Vector3? effect_bubble_pos,
            Item use_from_bag,
            ref GameRes __result)
        {
            if (InventorySync.Instance?.TryDeferPersonalConsumableUse(
                    __instance,
                    item,
                    effect_bubble_pos,
                    use_from_bag) != true)
            {
                return true;
            }

            __result = new GameRes();
            return false;
        }
    }

    [HarmonyPatch]
    internal static class PersonalBuffUsagePatches
    {
        [HarmonyPatch(typeof(BuffsLogics), nameof(BuffsLogics.AddBuff))]
        [HarmonyPrefix]
        private static bool AddBuffPrefix(string buff_id)
        {
            return AllowPersonalBuffMutation("add", buff_id);
        }

        [HarmonyPatch(typeof(BuffsLogics), nameof(BuffsLogics.RemoveBuff))]
        [HarmonyPrefix]
        private static bool RemoveBuffPrefix(string buff_id)
        {
            return AllowPersonalBuffMutation("remove", buff_id);
        }

        private static bool AllowPersonalBuffMutation(
            string operation,
            string buffId)
        {
            if (!CutsceneSyncPatches.IsApplyingRemotePresentation)
                return true;

            CoopMod.Logger.LogInfo(
                $"[PersonalBuff] Suppressed remote-presentation {operation} " +
                $"for personal buff '{buffId ?? "unknown"}'");
            return false;
        }
    }


    [HarmonyPatch(typeof(ChestGUI), "MoveItem", new Type[]
    {
        typeof(Item),
        typeof(int),
        typeof(bool),
        typeof(Item),
        typeof(bool)
    })]
    internal static class ChestGuiMoveItemOperationPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            ChestGUI __instance,
            WorldGameObject ____chest_obj,
            Item item,
            int count,
            bool to_chest,
            bool after_item_count_gui,
            out InventorySync.ChestMoveCapture __state)
        {
            InventorySync sync = InventorySync.Instance;
            if (sync == null)
            {
                __state = null;
                return true;
            }

            return sync.TryBeginChestMove(
                __instance,
                ____chest_obj,
                item,
                count,
                to_chest,
                after_item_count_gui,
                out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(InventorySync.ChestMoveCapture __state)
        {
            InventorySync.Instance?.CompleteChestMove(__state);
        }
    }
}

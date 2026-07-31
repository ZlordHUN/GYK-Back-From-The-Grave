using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch(typeof(CraftComponent), "CraftAsPlayer")]
    internal static class CraftAsPlayerLeasePatch
    {
        private static readonly MethodInfo CraftAsPlayerMethod = AccessTools.DeclaredMethod(
            typeof(CraftComponent),
            "CraftAsPlayer",
            new[]
            {
                typeof(CraftDefinition),
                typeof(Item),
                typeof(List<string>),
                typeof(List<Item>),
                typeof(bool),
                typeof(int),
                typeof(WorldGameObject)
            });

        static bool Prefix(
            CraftComponent __instance,
            CraftDefinition craft,
            Item try_use_particular_item,
            List<string> multiquality_ids,
            List<Item> override_needs,
            bool ignore_crafts_list,
            int amount,
            WorldGameObject other_obj_override,
            ref bool __result,
            out bool __state)
        {
            __state = true;
            var sync = GraveyardKeeperCoop.Multiplayer.CraftSync.Instance;
            if (sync == null)
                return true;

            bool proceed = sync.TryAcquireStationLease(
                __instance,
                craft,
                () => CraftAsPlayerMethod?.Invoke(
                    __instance,
                    new object[]
                    {
                        craft,
                        try_use_particular_item,
                        multiquality_ids,
                        override_needs,
                        ignore_crafts_list,
                        amount,
                        other_obj_override
                    }));
            if (proceed)
                return true;

            // The UI may finish closing while the reliable grant is in flight. No
            // queue or material state has changed yet; a rejection simply leaves the
            // station idle.
            __state = false;
            __result = true;
            return false;
        }

        static void Postfix(
            CraftComponent __instance,
            bool __result,
            bool __state)
        {
            if (__state && !__result)
            {
                GraveyardKeeperCoop.Multiplayer.CraftSync.Instance
                    ?.NotifyLocalCraftStartFailed(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(CraftComponent), "CraftReally")]
    internal static class CraftReallyPatch
    {
        private static readonly MethodInfo CraftReallyMethod = AccessTools.DeclaredMethod(
            typeof(CraftComponent),
            "CraftReally",
            new[]
            {
                typeof(CraftDefinition),
                typeof(Item),
                typeof(List<string>),
                typeof(List<Item>),
                typeof(bool),
                typeof(int),
                typeof(bool),
                typeof(bool),
                typeof(bool)
            });

        static bool Prefix(
            CraftComponent __instance,
            CraftDefinition craft,
            Item try_use_particular_item,
            List<string> multiquality_ids,
            List<Item> override_needs,
            bool ignore_crafts_list,
            int amount,
            bool for_gratitude_points,
            bool use_player_inv,
            bool start_by_player,
            ref bool __result,
            out bool __state)
        {
            __state = true;
            var sync = GraveyardKeeperCoop.Multiplayer.CraftSync.Instance;
            if (sync == null)
                return true;

            bool proceed = sync.TryAcquireStationLease(
                __instance,
                craft,
                () => ReplayCraftReally(
                    __instance,
                    craft,
                    try_use_particular_item,
                    multiquality_ids,
                    override_needs,
                    ignore_crafts_list,
                    amount,
                    for_gratitude_points,
                    use_player_inv,
                    start_by_player));
            if (proceed)
                return true;

            __state = false;
            __result = false;
            return false;
        }

        static void Postfix(
            CraftComponent __instance,
            bool __result,
            bool ___is_crafting,
            bool __state)
        {
            if (!__state)
                return;
            if (!__result || !___is_crafting)
            {
                GraveyardKeeperCoop.Multiplayer.CraftSync.Instance
                    ?.NotifyLocalCraftStartFailed(__instance);
                return;
            }
            GraveyardKeeperCoop.Multiplayer.CraftSync.Instance
                ?.NotifyLocalCraftStarted(__instance);
            MarkDirty(__instance);
        }

        private static void ReplayCraftReally(
            CraftComponent craftComponent,
            CraftDefinition craft,
            Item particularItem,
            List<string> multiqualityIds,
            List<Item> overrideNeeds,
            bool ignoreCraftsList,
            int amount,
            bool forGratitudePoints,
            bool usePlayerInventory,
            bool startByPlayer)
        {
            if (CraftReallyMethod == null || craftComponent == null)
                return;

            CraftReallyMethod.Invoke(
                craftComponent,
                new object[]
                {
                    craft,
                    particularItem,
                    multiqualityIds,
                    overrideNeeds,
                    ignoreCraftsList,
                    amount,
                    forGratitudePoints,
                    usePlayerInventory,
                    startByPlayer
                });
        }

        internal static void MarkDirty(CraftComponent craft)
        {
            if (craft?.wgo == null) return;
            var sync = GraveyardKeeperCoop.Multiplayer.CraftSync.Instance;
            if (sync == null) return;
            sync.MarkDirty(craft.wgo.unique_id);
        }
    }

    [HarmonyPatch(typeof(CraftComponent), "TryStartCraftFromQueue")]
    internal static class TryStartCraftFromQueueLeasePatch
    {
        private static readonly MethodInfo TryStartMethod = AccessTools.DeclaredMethod(
            typeof(CraftComponent),
            "TryStartCraftFromQueue",
            new[] { typeof(bool), typeof(bool) });

        static bool Prefix(
            CraftComponent __instance,
            bool can_use_player_inventory,
            bool start_by_player)
        {
            if (__instance == null || __instance.is_crafting)
                return true;

            var sync = GraveyardKeeperCoop.Multiplayer.CraftSync.Instance;
            if (sync == null)
                return true;

            CraftDefinition intendedCraft = null;
            if (__instance.craft_queue != null)
            {
                for (int i = 0; i < __instance.craft_queue.Count; i++)
                {
                    if (__instance.craft_queue[i]?.craft != null)
                    {
                        intendedCraft = __instance.craft_queue[i].craft;
                        break;
                    }
                }
            }

            return sync.TryAcquireStationLease(
                __instance,
                intendedCraft,
                () =>
                {
                    TryStartMethod?.Invoke(
                        __instance,
                        new object[] { can_use_player_inventory, start_by_player });
                    if (!__instance.is_crafting)
                        sync.NotifyLocalCraftStartFailed(__instance);
                });
        }

        static void Postfix(CraftComponent __instance)
        {
            if (__instance != null && !__instance.is_crafting)
            {
                GraveyardKeeperCoop.Multiplayer.CraftSync.Instance
                    ?.NotifyLocalCraftStartFailed(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(CraftComponent), "Cancel")]
    internal static class CraftCancelPatch
    {
        static void Postfix(CraftComponent __instance)
        {
            GraveyardKeeperCoop.Multiplayer.CraftSync.Instance
                ?.NotifyLocalCraftEnded(__instance);
            CraftReallyPatch.MarkDirty(__instance);
        }
    }

    [HarmonyPatch(typeof(CraftComponent), "End")]
    internal static class CraftEndPatch
    {
        static void Postfix(CraftComponent __instance)
        {
            GraveyardKeeperCoop.Multiplayer.CraftSync.Instance
                ?.NotifyLocalCraftEnded(__instance);
            if (GraveyardKeeperCoop.Network.OnlineCoopManager.Instance?.IsHost == false)
            {
                // ProcessFinishedCraft mutates nested corpse/item data before End.
                // Publish that durable table state with the final inactive craft so
                // the host never has to repeat a client's autopsy or other craft.
                GraveyardKeeperCoop.Multiplayer.InventorySync.Instance?.NotifyInventoryMutated(
                    __instance?.wgo,
                    force: true);
            }
            CraftReallyPatch.MarkDirty(__instance);
        }
    }

    [HarmonyPatch(typeof(CraftComponent), "EnqueueCraft")]
    internal static class CraftEnqueuePatch
    {
        static void Postfix(CraftComponent __instance)
        {
            CraftReallyPatch.MarkDirty(__instance);
        }
    }

    [HarmonyPatch(typeof(CraftComponent), "StartRemovalCraft")]
    internal static class CraftStartRemovalPatch
    {
        static void Postfix(CraftComponent __instance)
        {
            GraveyardKeeperCoop.Multiplayer.CraftSync.Instance
                ?.NotifyLocalCraftStarted(__instance);
            CraftReallyPatch.MarkDirty(__instance);
        }
    }

    [HarmonyPatch(typeof(CraftComponent), "CancelRemovalCraft")]
    internal static class CraftCancelRemovalPatch
    {
        static void Postfix(CraftComponent __instance)
        {
            GraveyardKeeperCoop.Multiplayer.CraftSync.Instance
                ?.NotifyLocalCraftEnded(__instance);
            CraftReallyPatch.MarkDirty(__instance);
        }
    }

    [HarmonyPatch(typeof(WorldGameObject), "OnCraftStateChanged")]
    internal static class CraftStateChangedPatch
    {
        static void Postfix(WorldGameObject __instance)
        {
            var craft = __instance.components?.craft;
            if (craft == null) return;
            CraftReallyPatch.MarkDirty(craft);
        }
    }

    [HarmonyPatch(typeof(CraftComponent), "ProcessFinishedCraft")]
    internal static class CraftFinishedProgressionSyncPatch
    {
        static void Prefix(
            CraftComponent __instance,
            out GraveyardKeeperCoop.Multiplayer.CraftSync.LocalCraftCompletion __state)
        {
            __state = GraveyardKeeperCoop.Multiplayer.CraftSync.Instance
                ?.CaptureLocalCraftCompletion(__instance);
        }

        static void Postfix(
            CraftComponent __instance,
            GraveyardKeeperCoop.Multiplayer.CraftSync.LocalCraftCompletion __state)
        {
            GraveyardKeeperCoop.Multiplayer.CraftSync.Instance
                ?.NotifyLocalCraftFinished(__instance, __state);
        }
    }

    [HarmonyPatch(typeof(QuestSystem), "CheckKeyQuests")]
    internal static class RemoteCraftCompletionQuestPresentationPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(ref bool __result)
        {
            if (!GraveyardKeeperCoop.Multiplayer.CraftSync
                    .IsApplyingRemoteCraftCompletion)
            {
                return true;
            }

            // The worker already ran these quest checks and sends the resulting
            // canonical quest/cutscene events. Repeating them while the host records
            // craft completion can start the same reward script a second time.
            __result = false;
            return false;
        }
    }
}

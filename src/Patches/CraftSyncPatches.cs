using HarmonyLib;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch(typeof(CraftComponent), "CraftReally")]
    internal static class CraftReallyPatch
    {
        static void Postfix(
            CraftComponent __instance,
            bool __result,
            bool ___is_crafting)
        {
            if (!__result || !___is_crafting) return;
            GraveyardKeeperCoop.Multiplayer.CraftSync.Instance
                ?.NotifyLocalCraftStarted(__instance);
            MarkDirty(__instance);
        }

        internal static void MarkDirty(CraftComponent craft)
        {
            if (craft?.wgo == null) return;
            var sync = GraveyardKeeperCoop.Multiplayer.CraftSync.Instance;
            if (sync == null) return;
            sync.MarkDirty(craft.wgo.unique_id);
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

    [HarmonyPatch(typeof(GameSave), nameof(GameSave.OnFinishedCraft))]
    internal static class CraftFinishedProgressionSyncPatch
    {
        static void Postfix(CraftDefinition craft)
        {
            GraveyardKeeperCoop.Multiplayer.CraftSync.Instance
                ?.NotifyLocalCraftFinished(craft);
        }
    }
}

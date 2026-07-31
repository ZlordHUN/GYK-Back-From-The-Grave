using HarmonyLib;
using GraveyardKeeperCoop.Multiplayer;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch]
    internal static class TechSyncPatches
    {
        private static void MarkDirty()
        {
            var sync = TechSync.Instance;
            if (sync == null) return;
            sync.MarkDirty();
        }

        [HarmonyPatch(typeof(GameSave), "UnlockTech")]
        [HarmonyPrefix]
        internal static bool UnlockTech_Prefix(string tech_id, out bool __state)
        {
            var sync = TechSync.Instance;
            __state =
                sync?.ShouldSuppressDuplicateSharedTechUnlock(tech_id) == true;
            if (!__state)
            {
                sync?.NotifyTechUnlockStarting(tech_id);
            }
            return !__state;
        }

        [HarmonyPatch(typeof(GameSave), "UnlockTech")]
        [HarmonyPostfix]
        internal static void UnlockTech_Postfix(string tech_id, bool __state)
        {
            if (__state)
                return;

            MarkDirty();
            TechSync.Instance?.BroadcastTechUnlock(tech_id);
        }

        [HarmonyPatch(typeof(GameSave), "UnlockPhrase")]
        [HarmonyPostfix]
        internal static void UnlockPhrase_Postfix() => MarkDirty();

        [HarmonyPatch(typeof(GameSave), "UnlockCraft")]
        [HarmonyPostfix]
        internal static void UnlockCraft_Postfix(GameSave __instance, string craft_id)
        {
            MarkDirty();
            TechSync.Instance?.BroadcastCraftUnlock(craft_id);
        }

        [HarmonyPatch(typeof(GameSave), "LockCraft")]
        [HarmonyPostfix]
        internal static void LockCraft_Postfix() => MarkDirty();

        [HarmonyPatch(typeof(GameSave), "LockCraftForever")]
        [HarmonyPostfix]
        internal static void LockCraftForever_Postfix() => MarkDirty();

        [HarmonyPatch(typeof(GameSave), "AddPhraseToBlackList")]
        [HarmonyPostfix]
        internal static void AddPhraseToBlackList_Postfix() => MarkDirty();

        [HarmonyPatch(typeof(GameSave), "UnlockTechBranch")]
        [HarmonyPostfix]
        internal static void UnlockTechBranch_Postfix() => MarkDirty();

        [HarmonyPatch(typeof(GameSave), "RevealHiddenTech")]
        [HarmonyPostfix]
        internal static void RevealHiddenTech_Postfix() => MarkDirty();

        [HarmonyPatch(typeof(GameSave), "MakeVisibleInvisibleTech")]
        [HarmonyPostfix]
        internal static void MakeVisibleInvisibleTech_Postfix() => MarkDirty();

        [HarmonyPatch(typeof(GameSave), "UnlockPerk")]
        [HarmonyPostfix]
        internal static void UnlockPerk_Postfix() => MarkDirty();

        [HarmonyPatch(typeof(GameSave), "BuyTech")]
        [HarmonyPostfix]
        internal static void BuyTech_Postfix() => MarkDirty();

        [HarmonyPatch(typeof(TutorialGUI), nameof(TutorialGUI.Open))]
        [HarmonyPostfix]
        internal static void TutorialGUI_Open_Postfix(string id)
        {
            var sync = TechSync.Instance;
            sync?.NotifyTutorialPopupOpened(id);
            sync?.BroadcastTutorialPresentation(id);
        }

        [HarmonyPatch(typeof(TechUnlockDialogGUI), nameof(TechUnlockDialogGUI.Open))]
        [HarmonyPostfix]
        internal static void TechUnlockDialogGUI_Open_Postfix(
            TechDefinition tech,
            bool forced_unlock,
            bool reveal_tech,
            bool show_tech_tree_after,
            bool pseudotech)
        {
            TechSync.Instance?.NotifyTechPopupOpened(
                tech,
                forced_unlock,
                reveal_tech,
                show_tech_tree_after,
                pseudotech);
        }

        [HarmonyPatch(typeof(BaseGUI), nameof(BaseGUI.Hide))]
        [HarmonyPrefix]
        internal static void BaseGUI_Hide_Prefix(BaseGUI __instance)
        {
            TechSync.Instance?.NotifyPresentationClosing(__instance);
        }

        [HarmonyPatch(typeof(TutorialGUI), nameof(TutorialGUI.Hide))]
        [HarmonyPostfix]
        internal static void TutorialGUI_Hide_Postfix()
        {
            TechSync.Instance?.NotifyTutorialPopupHidden();
        }

        [HarmonyPatch(typeof(GameLogics), "AddToBlackList")]
        [HarmonyPostfix]
        internal static void GameLogics_AddToBlackList_Postfix() => MarkDirty();
    }
}

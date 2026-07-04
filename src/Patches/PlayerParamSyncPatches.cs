using HarmonyLib;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch]
    internal static class PlayerParamSyncPatches
    {
        private static void MarkDirty()
        {
            var sync = GraveyardKeeperCoop.Multiplayer.PlayerParamSync.Instance;
            if (sync == null) return;
            sync.MarkDirty();
        }

        private static bool IsSyncEnabled()
        {
            var onlineCoop = GraveyardKeeperCoop.Network.OnlineCoopManager.Instance;
            return onlineCoop != null && onlineCoop.IsOnlineCoopEnabled;
        }

        [HarmonyPatch(typeof(WorldGameObject), "AddToParams", typeof(GameRes))]
        [HarmonyPostfix]
        internal static void AddToParams_GameRes_Postfix(WorldGameObject __instance)
        {
            if (__instance == null || !__instance.is_player) return;
            if (!IsSyncEnabled()) return;
            MarkDirty();
        }

        [HarmonyPatch(typeof(WorldGameObject), "AddToParams", typeof(string), typeof(float))]
        [HarmonyPostfix]
        internal static void AddToParams_StringFloat_Postfix(WorldGameObject __instance)
        {
            if (__instance == null || !__instance.is_player) return;
            if (!IsSyncEnabled()) return;
            MarkDirty();
        }

        [HarmonyPatch(typeof(WorldGameObject), "SubParam", typeof(string), typeof(float))]
        [HarmonyPostfix]
        internal static void SubParam_Postfix(WorldGameObject __instance)
        {
            if (__instance == null || !__instance.is_player) return;
            if (!IsSyncEnabled()) return;
            MarkDirty();
        }

        [HarmonyPatch(typeof(WorldGameObject), "SetParam", typeof(string), typeof(float))]
        [HarmonyPostfix]
        internal static void SetParam_StringFloat_Postfix(WorldGameObject __instance)
        {
            if (__instance == null || !__instance.is_player) return;
            if (!IsSyncEnabled()) return;
            MarkDirty();
        }

        [HarmonyPatch(typeof(WorldGameObject), "SetParam", typeof(GameRes))]
        [HarmonyPostfix]
        internal static void SetParam_GameRes_Postfix(WorldGameObject __instance)
        {
            if (__instance == null || !__instance.is_player) return;
            if (!IsSyncEnabled()) return;
            MarkDirty();
        }

        [HarmonyPatch(typeof(GameLogics), "ForceExecute", new System.Type[] { typeof(string) })]
        [HarmonyPostfix]
        internal static void ForceExecute_Postfix()
        {
            if (!IsSyncEnabled()) return;
            MarkDirty();
        }

        [HarmonyPatch(typeof(GameLogics), "ForceExecuteCond")]
        [HarmonyPostfix]
        internal static void ForceExecuteCond_Postfix()
        {
            if (!IsSyncEnabled()) return;
            MarkDirty();
        }
    }
}
using HarmonyLib;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch]
    internal static class WorkerSyncPatches
    {
        [HarmonyPatch(typeof(SavedWorkersList), "CreateNewWorker")]
        [HarmonyPostfix]
        internal static void CreateNewWorker_Postfix(SavedWorkersList __instance, Worker __result)
        {
            if (!IsSyncEnabled()) return;
            if (__result == null) return;

            var sync = GraveyardKeeperCoop.Multiplayer.WorkerSync.Instance;
            if (sync == null) return;
            sync.SendWorkerCreated(__result);
        }

        private static bool IsSyncEnabled()
        {
            var onlineCoop = GraveyardKeeperCoop.Network.OnlineCoopManager.Instance;
            return onlineCoop != null && onlineCoop.IsOnlineCoopEnabled;
        }
    }
}
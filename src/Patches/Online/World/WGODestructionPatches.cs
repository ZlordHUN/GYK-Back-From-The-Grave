using HarmonyLib;
using GraveyardKeeperCoop.Multiplayer;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Harmony patches for synchronizing WorldGameObject destruction in multiplayer.
    /// When a WGO is destroyed, we notify other players so they can destroy it too.
    /// </summary>
    [HarmonyPatch]
    public static class WGODestructionPatches
    {
        /// <summary>
        /// Prefix patch for WorldGameObject.DestroyMe()
        /// Sends destruction notification to network before the WGO is destroyed
        /// </summary>
        [HarmonyPatch(typeof(WorldGameObject), "DestroyMe")]
        [HarmonyPrefix]
        public static void DestroyMe_Prefix(WorldGameObject __instance)
        {
            if (__instance == null ||
                __instance.GetComponent<RemoteBuildingPreviewMarker>() != null)
            {
                return;
            }

            if (__instance != null && __instance.unique_id != 0)
            {
                WGORegistry.Instance?.Unregister(__instance.unique_id);
            }

            // Skip if we're processing a remote destruction (to avoid loops)
            if (WGODestructionSync.IsProcessingRemote)
                return;
            
            // Skip if the WGO has no unique ID (dynamic/temporary objects)
            if (__instance.unique_id == 0)
                return;
            
            // Skip if not in online coop
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return;
            
            // Notify the sync system
            WGODestructionSync.Instance?.OnLocalWGODestroyed(__instance.unique_id);
        }
    }
}

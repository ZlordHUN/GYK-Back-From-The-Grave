using HarmonyLib;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch]
    public static class OnlineZonePatches
    {
        // Ownership: this is the suppressing online-zone gate. It runs before local
        // co-op zone ownership and ZoneNavSync observers so remote proxy players do
        // not drive local zone scripts or navigation sync.
        [HarmonyPatch(typeof(GDZone), "OnTriggerEnter2D")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static bool GDZone_OnTriggerEnter2D_Prefix(Collider2D collision)
        {
            return !IsRemoteOnlinePlayer(collision);
        }

        [HarmonyPatch(typeof(GDZone), "OnTriggerExit2D")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static bool GDZone_OnTriggerExit2D_Prefix(Collider2D collision)
        {
            return !IsRemoteOnlinePlayer(collision);
        }

        private static bool IsRemoteOnlinePlayer(Collider2D collision)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled || collision == null)
                return false;

            WorldGameObject wgo = collision.gameObject.GetComponentInParent<WorldGameObject>();
            if (wgo == null || !wgo.is_player)
                return false;

            WorldGameObject remotePlayer = onlineCoop.GetRemotePlayer();
            if (remotePlayer != null && wgo == remotePlayer)
                return true;

            WorldGameObject localPlayer = MainGame.me?.player;
            return localPlayer != null && wgo != localPlayer;
        }
    }
}

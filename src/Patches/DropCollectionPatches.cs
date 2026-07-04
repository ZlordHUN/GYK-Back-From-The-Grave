using HarmonyLib;
using GraveyardKeeperCoop.LocalCoop;
using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches drop collection to work properly with local co-op.
    /// Fixes:
    /// 1. Tech points (XP) going to the correct player who collected them
    /// 2. Items being added to the correct player's inventory
    /// </summary>
    [HarmonyPatch(typeof(DropResGameObject))]
    public class DropCollectionPatches
    {
        /// <summary>
        /// Patch CollectDrop to give tech points (XP) to the collecting player, not always P1.
        /// Original code always does: MainGame.me.player.AddToParams(this.res.id, value)
        /// We need to redirect this to the actual collecting player.
        /// </summary>
        [HarmonyPatch("CollectDrop")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static bool CollectDrop_Prefix(DropResGameObject __instance, WorldGameObject player)
        {
            if (__instance?.res == null)
                return true;

            var manager = LocalCoopManager.Instance;
            
            // Only intercept in local co-op mode
            if (manager == null || !manager.IsLocalCoopEnabled)
                return true; // Use original
            
            // Check if this is a tech point (XP)
            if (!__instance.res.is_tech_point)
                return true; // Let original handle non-XP drops
            
            // Handle tech point collection for the correct player
            Debug.Log("<color=yellow>Collect drop (co-op)</color> " + __instance.res?.ToString());
            __instance.is_collected = true;
            
            // Play pickup sound using reflection since MasterAudio might not be directly accessible
            try
            {
                var masterAudioType = System.Type.GetType("MasterAudio, Assembly-CSharp");
                if (masterAudioType != null)
                {
                    var playMethod = masterAudioType.GetMethod("PlaySound", new[] { typeof(string), typeof(float), typeof(Transform), typeof(float), typeof(string), typeof(object), typeof(bool), typeof(bool) });
                    playMethod?.Invoke(null, new object[] { "pickup", 1f, null, 0f, "pickup1", null, false, false });
                }
            }
            catch { /* Ignore sound errors */ }
            
            // Add tech points to the COLLECTING player, not always P1
            player.AddToParams(__instance.res.id, (float)__instance.res.value);
            CoopMod.Logger.LogInfo($"[DropCollection] Tech point {__instance.res.id} x{__instance.res.value} added to {(player == manager.Player1?.wgo ? "P1" : "P2")}");
            
            // Destroy linked hint
            __instance.DestroyLinkedHint();
            
            // Show the collection GUI
            DropCollectGUI.OnDropCollected(__instance.res);

            CombatSyncPatches.NotifyLocalDropCollectedFromLocalPath(__instance);
            
            return false; // Skip original
        }
    }
}

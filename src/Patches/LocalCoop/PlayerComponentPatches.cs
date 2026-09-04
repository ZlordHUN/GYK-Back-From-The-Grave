using HarmonyLib;
using GraveyardKeeperCoop.LocalCoop;
using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches for MainGame to handle camera following in split-screen.
    /// </summary>
    [HarmonyPatch(typeof(MainGame))]
    public class MainGameCameraPatches
    {
        /// <summary>
        /// Patch SetCameraPlayerFollow to prevent it from running in split-screen mode.
        /// </summary>
        [HarmonyPatch("SetCameraPlayerFollow")]
        [HarmonyPrefix]
        public static bool SetCameraPlayerFollow_Prefix(Transform t)
        {
            var coopManager = LocalCoopManager.Instance;
            if (coopManager != null && coopManager.IsLocalCoopEnabled)
            {
                // In split-screen mode, we handle camera following ourselves
                CoopMod.Logger.LogInfo($"[CameraPatch] Blocked SetCameraPlayerFollow for {t.gameObject.name} - using split-screen camera system");
                return false; // Skip the original method
            }
            
            return true; // Run the original method
        }
    }
}

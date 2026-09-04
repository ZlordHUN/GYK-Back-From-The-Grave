using HarmonyLib;
using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches HUD and Toolbar to show both players' information simultaneously in local co-op
    /// </summary>
    [HarmonyPatch(typeof(ToolbarGUI))]
    public class ToolbarPatches
    {
        private static bool hasRepositioned = false;

        /// <summary>
        /// Patch Redraw to show BOTH keyboard and gamepad toolbars in local co-op mode
        /// and reposition keyboard toolbar to bottom-right corner
        /// </summary>
        [HarmonyPatch("Redraw")]
        [HarmonyPrefix]
        public static bool Redraw_Prefix(ToolbarGUI __instance)
        {
            var manager = GraveyardKeeperCoop.LocalCoop.LocalCoopManager.Instance;
            
            // Only intercept in local co-op mode with Player 2 active
            if (manager == null || !manager.IsLocalCoopEnabled || manager.Player2 == null)
                return true; // Use original method
            
            // LOCAL CO-OP MODE: Show BOTH toolbars simultaneously
            __instance.Activate<ToolbarGUI>();
            
            // Always show both keyboard (P1) and gamepad (P2) toolbars
            __instance.keyboard.SetActive(true);
            __instance.gamepad.SetActive(true);
            
            // Reposition keyboard toolbar to bottom-right corner (only once)
            if (!hasRepositioned)
            {
                RepositionKeyboardToolbar(__instance.keyboard);
                hasRepositioned = true;
            }
            
            // Redraw both
            __instance.keyboard.Redraw();
            __instance.gamepad.Redraw();
            
            return false; // Skip original method
        }

        /// <summary>
        /// Reposition the keyboard toolbar to the bottom-right corner of the screen
        /// </summary>
        private static void RepositionKeyboardToolbar(ToolbarSetGUI keyboard)
        {
            if (keyboard == null || keyboard.transform == null)
            {
                CoopMod.Logger.LogError("[HUDPatch] Keyboard toolbar or transform is null!");
                return;
            }

            // Debug hierarchy
            Transform current = keyboard.transform;
            CoopMod.Logger.LogInfo($"[HUDPatch] === TOOLBAR HIERARCHY ===");
            while (current != null)
            {
                var widget = current.GetComponent<UIWidget>();
                var panel = current.GetComponent<UIPanel>();
                var anchor = current.GetComponent<UIAnchor>();
                CoopMod.Logger.LogInfo($"[HUDPatch]   {current.name}: localPos={current.localPosition}, " +
                    $"widget={widget != null}, panel={panel != null}, anchor={anchor != null}" +
                    (anchor != null ? $", anchorSide={anchor.side}" : ""));
                current = current.parent;
            }

            // Get the current position
            Vector3 originalPos = keyboard.transform.localPosition;
            Vector3 worldPos = keyboard.transform.position;
            CoopMod.Logger.LogInfo($"[HUDPatch] Keyboard toolbar original: local={originalPos}, world={worldPos}");

            // Get screen dimensions
            int screenWidth = Screen.width;
            int screenHeight = Screen.height;
            CoopMod.Logger.LogInfo($"[HUDPatch] Screen: {screenWidth}x{screenHeight}");

            // Move it to the right, keep same vertical position
            float newX = originalPos.x + 550f;  // Move 550 units right
            float newY = originalPos.y;         // Keep original Y position
            
            keyboard.transform.localPosition = new Vector3(newX, newY, originalPos.z);
            
            CoopMod.Logger.LogInfo($"[HUDPatch] Repositioned to: local={keyboard.transform.localPosition}, world={keyboard.transform.position}");
        }
    }
    
    /// <summary>
    /// Patch HUD.Update to handle both players' stats
    /// Note: For now, we'll keep showing Player 1's stats (health, energy, etc.)
    /// since they share the same save data. The toolbar is the main differentiator.
    /// </summary>
    [HarmonyPatch(typeof(HUD))]
    public class HUDUpdatePatch
    {
        /// <summary>
        /// Post-patch to ensure HUD stays visible with both toolbars
        /// </summary>
        [HarmonyPatch("Update")]
        [HarmonyPostfix]
        public static void Update_Postfix(HUD __instance)
        {
            var manager = GraveyardKeeperCoop.LocalCoop.LocalCoopManager.Instance;
            
            // Only run in local co-op mode
            if (manager == null || !manager.IsLocalCoopEnabled || manager.Player2 == null)
                return;
            
            // Ensure toolbar is visible (it might get hidden by the single-player logic)
            if (__instance.toolbar != null && !__instance.toolbar.gameObject.activeSelf)
            {
                __instance.toolbar.gameObject.SetActive(true);
            }
        }
    }
}

using HarmonyLib;
using UnityEngine;
using GraveyardKeeperCoop.Utils;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Debug patches for testing map features with keyboard shortcuts
    /// </summary>
    [HarmonyPatch]
    public static class MapDebugPatches
    {
        /// <summary>
        /// Add debug keyboard shortcuts to MapGUI.Update
        /// F8: Print player world position
        /// F9: Print calculated map position
        /// F10: Print all visible zones
        /// F11: Toggle map open/close
        /// </summary>
        [HarmonyPatch(typeof(MapGUI), "Update")]
        [HarmonyPostfix]
        public static void Update_Postfix(MapGUI __instance)
        {
            // F8: Print player position
            if (Input.GetKeyDown(KeyCode.F8))
            {
                if (MainGame.me != null && MainGame.me.player != null)
                {
                    Vector3 worldPos = MainGame.me.player.transform.position;
                    Vector2 mapPos = MapCoordinateHelper.WorldToMap(worldPos);
                    
                    GraveyardKeeperCoop.CoopMod.Logger.LogInfo("=== DEBUG: Player Position ===");
                    GraveyardKeeperCoop.CoopMod.Logger.LogInfo($"World: ({worldPos.x:F1}, {worldPos.y:F1}, {worldPos.z:F1})");
                    GraveyardKeeperCoop.CoopMod.Logger.LogInfo($"Map: ({mapPos.x:F1}, {mapPos.y:F1})");
                    
                    WorldZone zone = MainGame.me.player.GetMyWorldZone();
                    if (zone != null)
                    {
                        GraveyardKeeperCoop.CoopMod.Logger.LogInfo($"Zone: {zone.id}");
                    }
                }
            }
        }
    }
}

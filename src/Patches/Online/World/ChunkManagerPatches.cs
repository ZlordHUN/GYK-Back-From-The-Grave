using HarmonyLib;
using UnityEngine;
using System.Reflection;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches ChunkManager to load world chunks around BOTH players in local co-op.
    /// 
    /// The Problem:
    /// - ChunkManager uses MainGame.me.transform.position (Player 1) as the center for chunk loading
    /// - visible_chunk_radius is calculated from screen size, not accounting for zoom or second player
    /// - When zoomed out or players are far apart, chunks around Player 2 aren't loaded
    /// 
    /// The Solution:
    /// - Override _camera_pos to be the midpoint between both players
    /// - Dynamically increase visible_chunk_radius based on player distance
    /// - This ensures chunks are loaded around both players
    /// </summary>
    [HarmonyPatch(typeof(ChunkManager))]
    public class ChunkManagerPatches
    {
        // Reflection fields for accessing ChunkManager internals
        private static FieldInfo _cameraPosField;
        private static FieldInfo _visibleChunkRadiusXField;
        private static FieldInfo _visibleChunkRadiusYField;
        
        // Store the original radius values
        private static int _baseRadiusX = -1;
        private static int _baseRadiusY = -1;
        
        // Chunk size in world units
        private const float CHUNK_SIZE = 96f;
        
        // Extra buffer chunks beyond calculated distance
        private const int BUFFER_CHUNKS = 2;

        static ChunkManagerPatches()
        {
            var chunkManagerType = typeof(ChunkManager);
            _cameraPosField = AccessTools.Field(chunkManagerType, "_camera_pos");
            _visibleChunkRadiusXField = AccessTools.Field(chunkManagerType, "visible_chunk_radius_x");
            _visibleChunkRadiusYField = AccessTools.Field(chunkManagerType, "visible_chunk_radius_y");
        }

        /// <summary>
        /// Prefix: Set camera position to midpoint and adjust radius BEFORE the original Update runs.
        /// The original method sets _camera_pos = MainGame.me.transform.position at line 178.
        /// We override it before AND after to ensure the background thread sees our midpoint.
        /// </summary>
        [HarmonyPatch("Update")]
        [HarmonyPrefix]
        public static void Update_Prefix(ChunkManager __instance)
        {
            var manager = LocalCoop.LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
            {
                // Single player mode - restore original radius if we modified it
                RestoreOriginalRadius(__instance);
                return;
            }
            
            if (MainGame.me?.player == null || manager.Player1 == null || manager.Player2 == null)
            {
                return;
            }

            // Store base radius on first run (before we modify it)
            if (_baseRadiusX == -1)
            {
                _baseRadiusX = (int)_visibleChunkRadiusXField.GetValue(__instance);
                _baseRadiusY = (int)_visibleChunkRadiusYField.GetValue(__instance);
                CoopMod.Logger.LogInfo($"[ChunkManager] Stored base radius: {_baseRadiusX}x{_baseRadiusY} chunks");
            }

            // Get player positions
            Vector3 player1Pos = manager.Player1.transform.position;
            Vector3 player2Pos = manager.Player2.transform.position;
            
            // Calculate midpoint between players
            Vector3 midpoint = (player1Pos + player2Pos) / 2f;
            
            // Set camera position to midpoint
            _cameraPosField.SetValue(null, midpoint);
            
            // Calculate distance between players
            float distance = Vector3.Distance(player1Pos, player2Pos);
            
            // Calculate extra radius needed to cover both players from midpoint
            // Distance from midpoint to either player is distance/2
            // Convert to chunks and add buffer
            int extraRadiusX = Mathf.CeilToInt((distance / 2f) / CHUNK_SIZE) + BUFFER_CHUNKS;
            int extraRadiusY = Mathf.CeilToInt((distance / 2f) / CHUNK_SIZE) + BUFFER_CHUNKS;
            
            // Apply new radius
            int newRadiusX = _baseRadiusX + extraRadiusX;
            int newRadiusY = _baseRadiusY + extraRadiusY;
            
            _visibleChunkRadiusXField.SetValue(__instance, newRadiusX);
            _visibleChunkRadiusYField.SetValue(__instance, newRadiusY);
        }

        /// <summary>
        /// Postfix: Re-override camera position after the original method tries to set it back to P1.
        /// This ensures the background thread reads our midpoint, not Player 1's position.
        /// </summary>
        [HarmonyPatch("Update")]
        [HarmonyPostfix]
        public static void Update_Postfix()
        {
            var manager = LocalCoop.LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
            {
                return;
            }
            
            if (MainGame.me?.player == null || manager.Player1 == null || manager.Player2 == null)
            {
                return;
            }

            // Re-override camera position to midpoint
            Vector3 player1Pos = manager.Player1.transform.position;
            Vector3 player2Pos = manager.Player2.transform.position;
            Vector3 midpoint = (player1Pos + player2Pos) / 2f;
            
            _cameraPosField.SetValue(null, midpoint);
        }

        /// <summary>
        /// Restore original radius values when returning to single player mode.
        /// </summary>
        private static void RestoreOriginalRadius(ChunkManager instance)
        {
            if (_baseRadiusX != -1)
            {
                int currentX = (int)_visibleChunkRadiusXField.GetValue(instance);
                int currentY = (int)_visibleChunkRadiusYField.GetValue(instance);
                
                // Only restore if we actually modified it
                if (currentX != _baseRadiusX || currentY != _baseRadiusY)
                {
                    _visibleChunkRadiusXField.SetValue(instance, _baseRadiusX);
                    _visibleChunkRadiusYField.SetValue(instance, _baseRadiusY);
                    CoopMod.Logger.LogInfo($"[ChunkManager] Restored original radius: {_baseRadiusX}x{_baseRadiusY}");
                }
            }
        }
    }

    /// <summary>
    /// Patches to ensure Player 2's ChunkedGameObject is never deactivated by the chunk system.
    /// Even with the midpoint/radius fix, we want to be extra safe.
    /// </summary>
    [HarmonyPatch(typeof(ChunkedGameObject))]
    public class ChunkedGameObjectPatches
    {
        /// <summary>
        /// Prevent Player 2 from being deactivated by chunk visibility updates.
        /// </summary>
        [HarmonyPatch("UpdateVisibility")]
        [HarmonyPrefix]
        public static bool UpdateVisibility_Prefix(ChunkedGameObject __instance)
        {
            var manager = LocalCoop.LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
            {
                return true; // Run original
            }
            
            // Check if this is Player 2's ChunkedGameObject
            if (manager.Player2 != null && __instance.gameObject == manager.Player2.gameObject)
            {
                // Ensure Player 2 is always visible and active
                if (!__instance.obj_visible || !__instance.gameObject.activeSelf)
                {
                    __instance.obj_visible = true;
                    __instance.gameObject.SetActive(true);
                }
                return false; // Skip original method for Player 2
            }
            
            return true; // Run original method for other objects
        }
    }
}

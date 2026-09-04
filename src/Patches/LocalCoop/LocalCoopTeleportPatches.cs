using HarmonyLib;
using UnityEngine;
using GraveyardKeeperCoop.LocalCoop;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches for synchronized area transitions in local co-op.
    /// When either player teleports or changes area, the other player follows.
    /// </summary>
    public class LocalCoopTeleportPatches
    {
        /// <summary>
        /// Patch TeleportWithFade to sync Player 2 when Player 1 teleports
        /// </summary>
        [HarmonyPatch(typeof(BaseCharacterComponent), "TeleportWithFade", typeof(Vector2), typeof(GJCommons.VoidDelegate), typeof(GJCommons.VoidDelegate))]
        public class TeleportWithFade_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(BaseCharacterComponent __instance, Vector2 dest)
            {
                var manager = LocalCoopManager.Instance;
                if (manager == null || !manager.IsLocalCoopEnabled)
                    return;

                // Only sync if Player 1 is teleporting
                if (__instance.wgo != manager.Player1?.wgo)
                    return;

                // Teleport Player 2 to same destination (slightly offset)
                if (manager.Player2 != null)
                {
                    Vector2 player2Dest = dest + new Vector2(1f, 0f); // Slight offset to avoid overlap
                    TeleportPlayer2(manager.Player2, player2Dest);
                    // CoopMod.Logger.LogInfo($"[LocalCoopTeleport] Synced Player 2 teleport to {player2Dest}");
                }
            }
        }

        /// <summary>
        /// Patch TeleportToGDPoint to sync Player 2 when Player 1 teleports to a GD point
        /// Also handles online co-op remote player teleportation
        /// </summary>
        [HarmonyPatch(typeof(WorldGameObject), "TeleportToGDPoint")]
        public class TeleportToGDPoint_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(WorldGameObject __instance, string gd_point_tag)
            {
                // Handle LOCAL CO-OP
                var localManager = LocalCoopManager.Instance;
                if (localManager != null && localManager.IsLocalCoopEnabled)
                {
                    // Only sync if Player 1 is teleporting
                    if (__instance == localManager.Player1?.wgo && localManager.Player2?.wgo != null)
                    {
                        GDPoint gdPoint = WorldMap.GetGDPointByGDTag(gd_point_tag, true, true);
                        if (gdPoint != null)
                        {
                            Vector3 offset = new Vector3(200f, 0f, 0f);
                            localManager.Player2.transform.position = gdPoint.transform.position + offset;
                            
                            var chunked = localManager.Player2.GetComponent<ChunkedGameObject>();
                            chunked?.RecalculateChunk();
                            
                            LocalCoopCamera.Instance?.OnPlayer2Teleported(localManager.Player2.transform.position);
                        }
                    }
                    return; // Don't also handle online co-op
                }
                
                // Handle ONLINE CO-OP
                var onlineManager = OnlineCoopManager.Instance;
                if (onlineManager != null && onlineManager.IsOnlineCoopEnabled)
                {
                    // Only sync if the LOCAL player is teleporting
                    if (__instance != MainGame.me?.player)
                        return;
                    
                    GDPoint gdPoint = WorldMap.GetGDPointByGDTag(gd_point_tag, true, true);
                    if (gdPoint != null)
                    {
                        Vector3 targetPos = gdPoint.transform.position;

                        bool controlDisabled = MainGame.me?.player_char != null && !MainGame.me.player_char.control_enabled;
                        bool inIntroPhase = GraveyardKeeperCoop.Multiplayer.GameLoadSync.Instance?.IsInIntroPhase == true;

                        if (gd_point_tag == "gd_player_respawn")
                        {
                            if (inIntroPhase)
                                onlineManager.TransitionRedEyeIntroFormationToHouse();
                            CoopMod.Logger.LogInfo($"[OnlineTeleport] Skipping remote intro sync for '{gd_point_tag}' to avoid remote proxy triggering local quest zones");
                            return;
                        }

                        // Only force-teleport the remote player when the local player teleports during the
                        // INTRO PHASE (the dark-zone scene). During regular gameplay or non-intro cutscenes
                        // (e.g. digging up Gerry), the remote player should stay where they actually are.
                        // Other cutscenes use the new CutsceneWalkTo flow to make the absent player walk over.
                        if (!inIntroPhase)
                        {
                            CoopMod.Logger.LogInfo($"[OnlineTeleport] Local TeleportToGDPoint '{gd_point_tag}' at {targetPos} — NOT syncing remote (intro={inIntroPhase}, controlDisabled={controlDisabled})");
                            return;
                        }

                        // Intro-phase camera-aware offset: at orthoSize=810 (dark zone), 1.5 world units = 1 pixel.
                        // 60 units gives a visible gap; LateUpdate will refine it using the live camera.
                        Vector3 offset = new Vector3(60f, 0f, 0f);

                        CoopMod.Logger.LogInfo($"[OnlineTeleport] INTRO local teleported to GD point '{gd_point_tag}' at {targetPos}");
                        CoopMod.Logger.LogInfo($"[OnlineTeleport] INTRO teleporting remote player to {targetPos + offset}");

                        onlineManager.TeleportRemotePlayerTo(targetPos + offset);
                    }
                }
            }
        }

        /// <summary>
        /// Patch dungeon level teleportation
        /// </summary>
        [HarmonyPatch(typeof(MainGame), "TeleportToDungeonLevel")]
        public class TeleportToDungeonLevel_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(MainGame __instance, int level)
            {
                var manager = LocalCoopManager.Instance;
                if (manager == null || !manager.IsLocalCoopEnabled)
                    return;

                if (manager.Player1 != null && manager.Player2 != null)
                {
                    // After dungeon teleport, move Player 2 next to Player 1
                    Vector3 player1Pos = manager.Player1.transform.position;
                    manager.Player2.transform.position = player1Pos + new Vector3(200f, 0f, 0f);

                    var chunked = manager.Player2.GetComponent<ChunkedGameObject>();
                    if (chunked != null)
                    {
                        chunked.RecalculateChunk();
                    }

                    // CoopMod.Logger.LogInfo($"[LocalCoopTeleport] Synced Player 2 after dungeon level {level} teleport");
                }
            }
        }

        /// <summary>
        /// Helper to teleport Player 2 without triggering recursive patches
        /// </summary>
        private static void TeleportPlayer2(PlayerComponent player2, Vector2 dest)
        {
            if (player2 == null)
                return;

            try
            {
                // Direct position set (the fade is already happening from Player 1's teleport)
                player2.transform.position = dest * 96f;

                // Update chunk
                var chunked = player2.GetComponent<ChunkedGameObject>();
                if (chunked != null)
                {
                    chunked.RecalculateChunk();
                }

                // Notify camera
                LocalCoopCamera.Instance?.OnPlayer2Teleported(player2.transform.position);
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[LocalCoopTeleport] Error teleporting Player 2: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Patches for zone changes - prevents Player 2 from updating the zone HUD indicator
    /// which would cause flickering between both players' zones.
    /// </summary>
    [HarmonyPatch(typeof(PlayerComponent), "UpdateZone")]
    public class PlayerZoneUpdate_Patch
    {
        // Track the last known zone of Player 1
        private static WorldZone lastPlayer1Zone = null;

        /// <summary>
        /// Skip UpdateZone entirely for Player 2 to prevent HUD flickering
        /// </summary>
        [HarmonyPrefix]
        public static bool Prefix(
            PlayerComponent __instance,
            ref float ____time_passed_after_zone_update)
        {
            // Vanilla resets this at the start of UpdateZone. Do it here too so a
            // skipped proxy returns on the normal 0.5-second cadence instead of
            // re-entering this prefix on every frame forever.
            ____time_passed_after_zone_update = 0f;

            // Online remote players are view-only proxies. Vanilla UpdateZone
            // changes the shared HUD and calls WorldZone.OnPlayerEnter/Exit,
            // which would let a client indoors replace the local player's
            // outdoor music and smart-sound context.
            var onlineManager = OnlineCoopManager.Instance;
            if (onlineManager != null && onlineManager.IsOnlineCoopEnabled &&
                MainGame.me?.player != null &&
                __instance?.wgo != MainGame.me.player)
            {
                return false;
            }

            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return true; // Run original method

            // Skip UpdateZone for Player 2 - only Player 1 should update the zone HUD
            if (__instance == manager.Player2)
                return false; // Skip original method

            return true; // Run original for Player 1
        }

        [HarmonyPostfix]
        public static void Postfix(PlayerComponent __instance)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return;

            // Only track Player 1's zone changes
            if (__instance != manager.Player1)
                return;

            WorldZone currentZone = __instance.current_zone;

            // If zone changed, update tracking (Player 2 will naturally be in the same zone if close enough)
            if (currentZone != lastPlayer1Zone)
            {
                lastPlayer1Zone = currentZone;
            }
        }
    }
}

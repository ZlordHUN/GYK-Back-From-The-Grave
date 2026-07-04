using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches for interaction handling in online multiplayer.
    /// Prevents remote players from triggering interaction highlights (the "E" prompt).
    /// Only the local player should see and interact with nearby objects.
    /// </summary>
    [HarmonyPatch]
    public static class OnlineInteractionPatches
    {
        private static readonly System.Reflection.FieldInfo CollisionsField = AccessTools.Field(typeof(InteractionComponent), "_collisions");
        private static readonly System.Reflection.MethodInfo FindCurrentInteractionNearestMethod = AccessTools.Method(typeof(InteractionComponent), "FindCurrentInteractionNearest");
        private static readonly HashSet<string> SuppressedTeleportQuestKeysLogged = new HashSet<string>();

        [HarmonyPatch(typeof(InteractionComponent), "UpdateComponent")]
        [HarmonyPrefix]
        public static bool UpdateComponent_Prefix(InteractionComponent __instance)
        {
            if (!IsRemoteOnlinePlayer(__instance))
                return true;

            ClearRemoteInteractionState(__instance);
            return false;
        }

        /// <summary>
        /// Patch UpdateInteractionNearest to only allow local player to highlight objects.
        /// Remote player's InteractionComponent should not trigger PrepareForInteraction
        /// since that shows the interact bubble to the actual human player.
        /// </summary>
        [HarmonyPatch(typeof(InteractionComponent), "UpdateInteractionNearest")]
        [HarmonyPrefix]
        public static bool UpdateInteractionNearest_Prefix(InteractionComponent __instance)
        {
            if (IsRemoteOnlinePlayer(__instance))
            {
                ClearRemoteInteractionState(__instance);
                return false;
            }

            // Only apply in online coop
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return true; // Let original method run normally

            if (FindCurrentInteractionNearestMethod == null || CollisionsField == null)
                return true;
            
            // Find the nearest interactable object
            WorldGameObject nearestWGO = (WorldGameObject)FindCurrentInteractionNearestMethod.Invoke(__instance, null);

            // If nothing changed, skip further processing
            if (nearestWGO == __instance.nearest)
            {
                return false; // Skip original method
            }

            if (nearestWGO == null)
            {
                __instance.nearest?.UnprepareForInteraction();
                __instance.nearest = null;
                __instance.components.character.wgo_hilighted_for_work = null;
                return false;
            }
            
            // Update the nearest reference
            __instance.nearest = nearestWGO;
            
            // Unprepare all other collided objects for interaction
            var collisions = (List<WorldGameObject>)CollisionsField.GetValue(__instance);
            
            foreach (WorldGameObject collision in collisions)
            {
                if (collision != __instance.nearest)
                {
                    collision.UnprepareForInteraction();
                }
            }
            
            // CRITICAL: Only show interaction prompts for the LOCAL player
            // Check if this InteractionComponent belongs to the local player
            bool isLocalPlayer = false;
            
            if (__instance.wgo != null && __instance.wgo.is_player)
            {
                // Compare with MainGame's player unique_id
                if (MainGame.me?.player != null)
                {
                    isLocalPlayer = (MainGame.me.player.unique_id == __instance.wgo.unique_id);
                }
            }
            
            if (isLocalPlayer)
            {
                // Clear any previous highlight
                __instance.components.character.wgo_hilighted_for_work = null;
                
                // Don't show interaction prompt while moving
                if (__instance.wgo.components.character.average_step > 0.01f && 
                    LazyInput.GetDirection().magnitude > 0f)
                {
                    __instance.nearest = null;
                }
                else
                {
                    // Show interaction prompt (the "E" bubble)
                    __instance.nearest.PrepareForInteraction(__instance.components.character);
                }
            }
            // else: Remote player - don't call PrepareForInteraction, no bubble shown
            
            return false; // Skip original method, we've handled it
        }
        
        /// <summary>
        /// Prevent remote player's InteractionComponent from running Interact at all.
        /// Only the local player should be able to interact with objects.
        /// </summary>
        [HarmonyPatch(typeof(InteractionComponent), "Interact")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)] // Run before other patches
        public static bool Interact_Prefix(InteractionComponent __instance, ref bool __result)
        {
            if (IsRemoteOnlinePlayer(__instance))
            {
                __result = false;
                return false;
            }
            
            return true; // Allow local player interactions
        }

        /// <summary>
        /// The vanilla house door flow is sensitive to quest checks that run before the Teleport
        /// FlowScript starts. In online co-op, the other player's earlier exit can leave the local
        /// house tutorial quest ready to finish; consuming it on interact_teleport_inside makes the
        /// Teleport graph terminate before it reaches Flow_TeleportToWGO.
        ///
        /// Let the Teleport flow run first. It still calls the tp_* key quest after it has resolved
        /// the destination, so tutorial progression is preserved without trapping the second player.
        /// </summary>
        [HarmonyPatch(typeof(QuestSystem), "CheckKeyQuests")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static bool CheckKeyQuests_Prefix(string key, ref bool __result)
        {
            if (!ShouldSuppressOnlineTeleportInteractionQuest(key))
                return true;

            __result = false;
            if (SuppressedTeleportQuestKeysLogged.Add(key ?? string.Empty))
            {
                CoopMod.Logger.LogInfo($"[OnlineInteraction] Suppressed early teleport interaction quest '{key}' in online co-op");
            }

            return false;
        }

        [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.Interact))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static void WorldGameObject_Interact_Prefix(WorldGameObject __instance, WorldGameObject other_obj, bool interaction_start)
        {
            if (!interaction_start || !IsOnlineLocalPlayer(other_obj) || !IsInsideTeleport(__instance))
                return;

            float lockTp = other_obj.data.GetParam("lock_tp", 0f);
            if (lockTp <= 0.5f)
                return;

            other_obj.data.SetParam("lock_tp", 0f);
            other_obj.data.SetParam("lock_tp_param", 0f);
            CoopMod.Logger.LogInfo($"[OnlineInteraction] Cleared stale teleport lock before using '{__instance.custom_tag}'");
        }

        private static bool IsRemoteOnlinePlayer(InteractionComponent interaction)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled || interaction?.wgo == null || !interaction.wgo.is_player)
                return false;

            WorldGameObject remotePlayer = onlineCoop.GetRemotePlayer();
            if (remotePlayer != null && interaction.wgo == remotePlayer)
                return true;

            WorldGameObject localPlayer = MainGame.me?.player;
            return localPlayer != null && interaction.wgo != localPlayer;
        }

        private static bool ShouldSuppressOnlineTeleportInteractionQuest(string key)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return false;

            return !string.IsNullOrEmpty(key)
                && key.StartsWith("interact_teleport_inside", System.StringComparison.Ordinal);
        }

        private static bool IsOnlineLocalPlayer(WorldGameObject wgo)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            return onlineCoop != null
                && onlineCoop.IsOnlineCoopEnabled
                && wgo != null
                && wgo.is_player
                && MainGame.me?.player == wgo;
        }

        private static bool IsInsideTeleport(WorldGameObject wgo)
        {
            return wgo != null
                && string.Equals(wgo.obj_id, "teleport_inside", System.StringComparison.Ordinal);
        }

        private static void ClearRemoteInteractionState(InteractionComponent interaction)
        {
            try
            {
                if (interaction.nearest != null)
                {
                    interaction.nearest.UnprepareForInteraction();
                    interaction.nearest = null;
                }

                interaction.nearest_drop = null;

                var collisions = CollisionsField?.GetValue(interaction) as List<WorldGameObject>;
                if (collisions != null)
                {
                    for (int i = 0; i < collisions.Count; i++)
                    {
                        collisions[i]?.UnprepareForInteraction();
                    }
                    collisions.Clear();
                }

                // Drop highlighting is global in the base game. Clearing it from
                // the remote avatar's suppressed interaction update also clears
                // the real local player's highlighted corpse/drop, which makes
                // TryOtherInteractions unable to pick it up. The remote update
                // never runs FindNearestDrop, so it has no drop highlight of its
                // own to clear here.
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[OnlineInteraction] Failed to clear remote interaction state: {ex.Message}");
            }
        }
    }
}

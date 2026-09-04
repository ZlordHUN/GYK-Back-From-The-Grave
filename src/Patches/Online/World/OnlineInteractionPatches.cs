using HarmonyLib;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;
using GraveyardKeeperCoop.Multiplayer;

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
        private static readonly System.Reflection.FieldInfo NearestHasInteractionField = AccessTools.Field(typeof(InteractionComponent), "_nearest_has_interaction");
        private static readonly System.Reflection.MethodInfo FindCurrentInteractionNearestMethod = AccessTools.Method(typeof(InteractionComponent), "FindCurrentInteractionNearest");
        private static readonly HashSet<string> SuppressedTeleportQuestKeysLogged = new HashSet<string>();
        private const string FirstMorgueExitQuest =
            "go_to_graveyard_and_talk_with_skull";
        private const string FirstMorgueExitTeleportTag = "tp_mortuary_a";
        private static float partyMorgueExitCheckUntil;

        [HarmonyPatch(typeof(InteractionComponent), "UpdateComponent")]
        [HarmonyPrefix]
        public static bool UpdateComponent_Prefix(InteractionComponent __instance)
        {
            if (!IsRemoteOnlinePlayer(__instance))
                return true;

            ClearRemoteInteractionState(__instance);
            return false;
        }

        [HarmonyPatch(typeof(InteractionComponent), "UpdateComponent")]
        [HarmonyPostfix]
        public static void UpdateComponent_Postfix(InteractionComponent __instance)
        {
            if (IsRemoteOnlinePlayer(__instance) ||
                __instance?.wgo != MainGame.me?.player)
            {
                return;
            }

            OnlineCoopManager onlineCoop = OnlineCoopManager.Instance;
            PlayerTradeManager trade = PlayerTradeManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled || trade == null)
                return;

            // Player avatars are not part of vanilla's interaction collision list.
            // Resolve them after every local interaction update, including the
            // vanilla early-return path when that collision list is empty. Any
            // ordinary world interaction wins; player trading is only a fallback.
            WorldGameObject ordinaryTarget = __instance.nearest;
            if (ordinaryTarget != null &&
                !trade.IsInteractionPromptTarget(ordinaryTarget))
            {
                trade.ClearInteractionPrompt();
                return;
            }

            if (trade.TrySelectInteractionTarget(
                    __instance,
                    out WorldGameObject tradeTarget,
                    out CSteamID tradeTargetSteamId))
            {
                __instance.nearest = tradeTarget;
                NearestHasInteractionField?.SetValue(__instance, true);
                __instance.components.character.wgo_hilighted_for_work = null;
                trade.ShowInteractionPrompt(tradeTarget, tradeTargetSteamId);
                return;
            }

            trade.ClearInteractionPrompt();
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

            // Find the ordinary interaction target first. The postfix only offers
            // player trading when this search produces no usable world target.
            WorldGameObject nearestWGO =
                (WorldGameObject)FindCurrentInteractionNearestMethod.Invoke(__instance, null);

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
        /// A remote avatar is not a vanilla interactable, and pickup handling runs
        /// before InteractionComponent.Interact. Consume the bound interaction key
        /// here when the current deterministic proximity target is another player.
        /// This also covers controller A through the game's GameKey binding.
        /// </summary>
        [HarmonyPatch(typeof(BaseCharacterComponent), "ProcessInteraction")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool ProcessInteraction_Prefix(
            BaseCharacterComponent __instance,
            ref bool __result)
        {
            if (__instance?.wgo != MainGame.me?.player ||
                !LazyInput.GetKeyDown(GameKey.Interaction))
            {
                return true;
            }

            InteractionComponent interaction = __instance.wgo.components?.interaction;
            if (PlayerTradeManager.Instance?.TryHandleInteraction(interaction) != true)
                return true;

            __result = true;
            return false;
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
            if (PlayerTradeManager.Instance?.TryHandleInteraction(__instance) == true)
            {
                __result = true;
                return false;
            }

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

            if (IsFirstMorgueExit(__instance))
            {
                // The Teleport FlowScript evaluates Flow_GetOverhead synchronously
                // after Interact. Limit party-aware overhead redirection to this
                // one gate so other body and inventory checks remain player-local.
                partyMorgueExitCheckUntil =
                    Time.realtimeSinceStartup + 1f;
            }

            float lockTp = other_obj.data.GetParam("lock_tp", 0f);
            if (lockTp <= 0.5f)
                return;

            other_obj.data.SetParam("lock_tp", 0f);
            other_obj.data.SetParam("lock_tp_param", 0f);
            CoopMod.Logger.LogInfo($"[OnlineInteraction] Cleared stale teleport lock before using '{__instance.custom_tag}'");
        }

        /// <summary>
        /// The first morgue exit uses Flow_GetOverhead, which always asks the local
        /// player for a carried body. In shared progression, another player may have
        /// already carried that body outside. During this specific exit evaluation,
        /// let the flow observe the party's body without copying or transferring it.
        /// </summary>
        [HarmonyPatch(typeof(BaseCharacterComponent), "GetOverheadItem")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        public static void GetOverheadItem_Postfix(
            BaseCharacterComponent __instance,
            ref Item __result)
        {
            if (__result != null ||
                Time.realtimeSinceStartup > partyMorgueExitCheckUntil ||
                __instance == null ||
                __instance != MainGame.me?.player_char)
            {
                return;
            }

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return;

            if (TryGetRemoteCarriedBody(onlineCoop, out Item remoteBody))
            {
                __result = remoteBody;
                CoopMod.Logger.LogInfo(
                    "[OnlineInteraction] First morgue exit accepted the body carried by another player");
                return;
            }

            QuestSystem quests = MainGame.me?.save?.quests;
            if (quests == null ||
                (!quests.IsQuestCurrent(FirstMorgueExitQuest) &&
                 !quests.IsQuestSucced(FirstMorgueExitQuest)))
            {
                return;
            }

            // This item is only a read-only Flow_GetOverhead result. It is never
            // placed in the local inventory or assigned as the local overhead item.
            __result = new Item("body", 1);
            CoopMod.Logger.LogInfo(
                "[OnlineInteraction] First morgue exit accepted shared quest proof that the corpse already left");
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

        private static bool IsFirstMorgueExit(WorldGameObject wgo)
        {
            return IsInsideTeleport(wgo) &&
                   !string.IsNullOrEmpty(wgo.custom_tag) &&
                   wgo.custom_tag.StartsWith(
                       FirstMorgueExitTeleportTag,
                       System.StringComparison.Ordinal);
        }

        private static bool TryGetRemoteCarriedBody(
            OnlineCoopManager onlineCoop,
            out Item body)
        {
            body = null;
            List<KeyValuePair<Steamworks.CSteamID, PlayerComponent>> remotes =
                onlineCoop.GetRemotePlayersSnapshot();
            for (int i = 0; i < remotes.Count; i++)
            {
                Item overhead = remotes[i].Value
                    ?.wgo?.components?.character?.GetOverheadItem();
                if (!IsBody(overhead))
                    continue;

                body = overhead;
                return true;
            }

            return false;
        }

        private static bool IsBody(Item item)
        {
            if (item == null)
                return false;

            try
            {
                return string.Equals(
                           item.id,
                           "body",
                           System.StringComparison.Ordinal) ||
                       item.definition?.type ==
                           ItemDefinition.ItemType.Body;
            }
            catch
            {
                return false;
            }
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

    /// <summary>
    /// Keeps vanilla's single-character SleepGUI presentation, but makes its time
    /// acceleration and wake-up lifecycle party-coordinated in online sessions.
    /// </summary>
    [HarmonyPatch]
    public static class OnlineSleepSyncPatches
    {
        private static bool manualWakeRequested;
        private static bool suppressSleepRecovery;

        [HarmonyPatch(typeof(SleepGUI), nameof(SleepGUI.Open))]
        [HarmonyPrefix]
        public static void SleepOpen_Prefix(
            ref GJCommons.VoidDelegate on_appeared,
            ref GJCommons.VoidDelegate on_wake_up,
            ref GJCommons.VoidDelegate on_after_save)
        {
            GJCommons.VoidDelegate originalAppeared = on_appeared;
            on_appeared = delegate
            {
                GameTimeSync.Instance?.NotifyLocalSleepStarted();
                originalAppeared.TryInvoke();
            };

            GJCommons.VoidDelegate originalWakeUp = on_wake_up;
            on_wake_up = delegate
            {
                GameTimeSync sync = GameTimeSync.Instance;
                if (sync != null)
                    sync.CompleteLocalWakeTransition(originalWakeUp);
                else
                    originalWakeUp.TryInvoke();
            };

            GJCommons.VoidDelegate originalAfterSave = on_after_save;
            on_after_save = delegate
            {
                GameTimeSync sync = GameTimeSync.Instance;
                if (sync?.ShouldSuppressLocalFirstSleepWakeSetup == true)
                {
                    CoopMod.Logger.LogInfo(
                        "[SleepSync] Suppressed duplicate non-host Yorick " +
                        "wake setup during synchronized party wake");
                    return;
                }

                originalAfterSave.TryInvoke();
            };
        }

        [HarmonyPatch(typeof(SleepGUI), nameof(SleepGUI.Update))]
        [HarmonyPrefix]
        public static void SleepUpdate_Prefix()
        {
            GameTimeSync sync = GameTimeSync.Instance;
            suppressSleepRecovery = sync?.IsLocalSleepWaitingForParty == true;

            if (sync?.IsLocalSleeping == true &&
                LazyInput.GetKeyDown(GameKey.Interaction))
            {
                manualWakeRequested = true;
            }
        }

        [HarmonyPatch(typeof(SleepGUI), nameof(SleepGUI.Update))]
        [HarmonyPostfix]
        public static void SleepUpdate_Postfix()
        {
            suppressSleepRecovery = false;
        }

        [HarmonyPatch(typeof(SleepGUI), nameof(SleepGUI.Update))]
        [HarmonyFinalizer]
        public static System.Exception SleepUpdate_Finalizer(System.Exception __exception)
        {
            suppressSleepRecovery = false;
            return __exception;
        }

        [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.energy), MethodType.Setter)]
        [HarmonyPrefix]
        public static bool PlayerEnergySet_Prefix(WorldGameObject __instance, float value)
        {
            WorldGameObject localPlayer = MainGame.me?.player;
            return !suppressSleepRecovery || __instance != localPlayer || value <= __instance.energy;
        }

        [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.hp), MethodType.Setter)]
        [HarmonyPrefix]
        public static bool PlayerHpSet_Prefix(WorldGameObject __instance, float value)
        {
            WorldGameObject localPlayer = MainGame.me?.player;
            return !suppressSleepRecovery || __instance != localPlayer || value <= __instance.hp;
        }

        [HarmonyPatch(typeof(SleepGUI), "OnPressedBack")]
        [HarmonyPrefix]
        public static void SleepBack_Prefix()
        {
            if (GameTimeSync.Instance?.IsLocalSleeping == true)
                manualWakeRequested = true;
        }

        [HarmonyPatch(typeof(SleepGUI), "WakeUp")]
        [HarmonyPrefix]
        public static bool SleepWakeUp_Prefix()
        {
            GameTimeSync sync = GameTimeSync.Instance;
            bool manualWake = manualWakeRequested;
            manualWakeRequested = false;
            return sync == null || sync.HandleLocalWakeAttempt(manualWake);
        }
    }
}

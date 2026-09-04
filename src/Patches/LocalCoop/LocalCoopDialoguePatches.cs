using HarmonyLib;
using UnityEngine;
using GraveyardKeeperCoop.LocalCoop;
using System.Collections.Generic;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches for dialogue and speech bubbles in local co-op.
    /// Ensures dialogue bubbles appear above the player who initiated the conversation,
    /// and prevents dialogue bugs when Player 2 interacts with NPCs.
    /// </summary>
    public class LocalCoopDialoguePatches
    {
        /// <summary>
        /// Tracks which player initiated the current interaction/dialogue.
        /// This is used to correctly position speech bubbles and handle NPC dialogue.
        /// </summary>
        public static WorldGameObject CurrentInteractingPlayer { get; internal set; } = null;

        /// <summary>
        /// Timestamp when CurrentInteractingPlayer was last set.
        /// Used for timeout-based clearing to prevent permanent blocking.
        /// </summary>
        private static float _interactionStartTime = 0f;
        
        /// <summary>
        /// Maximum time (in seconds) to keep CurrentInteractingPlayer set.
        /// After this timeout, it will be auto-cleared to prevent permanent blocking.
        /// </summary>
        private const float INTERACTION_TIMEOUT_SECONDS = 120f; // 2 minutes max
        
        /// <summary>
        /// Sets the current interacting player with timeout tracking.
        /// </summary>
        public static void SetCurrentInteractingPlayer(WorldGameObject player)
        {
            var manager = LocalCoopManager.Instance;
            string playerName = player == manager?.Player1?.wgo ? "P1" : (player == manager?.Player2?.wgo ? "P2" : "unknown");
            CoopMod.Logger.LogInfo($"[LocalCoopDialogue] SetCurrentInteractingPlayer: {playerName}");
            CurrentInteractingPlayer = player;
            _interactionStartTime = Time.time;
        }
        
        /// <summary>
        /// Clears the current interacting player AND last dialogue input player.
        /// </summary>
        public static void ClearCurrentInteractingPlayer()
        {
            if (CurrentInteractingPlayer != null || Player2InputPatches.LastDialogueInputPlayer != null)
            {
                CoopMod.Logger.LogInfo($"[LocalCoopDialogue] ClearCurrentInteractingPlayer: was CurrentInteracting={CurrentInteractingPlayer != null}, wasLastDialogue={Player2InputPatches.LastDialogueInputPlayer != null}");
            }
            CurrentInteractingPlayer = null;
            Player2InputPatches.LastDialogueInputPlayer = null;
            _interactionStartTime = 0f;
        }
        
        /// <summary>
        /// Checks if the interaction has timed out and clears if necessary.
        /// Call this periodically to prevent permanent blocking.
        /// </summary>
        public static void CheckInteractionTimeout()
        {
            if (CurrentInteractingPlayer != null && 
                Time.time - _interactionStartTime > INTERACTION_TIMEOUT_SECONDS)
            {
                CoopMod.Logger.LogInfo($"[LocalCoopDialogue] Interaction timeout - clearing CurrentInteractingPlayer after {INTERACTION_TIMEOUT_SECONDS}s");
                ClearCurrentInteractingPlayer();
            }
        }

        /// <summary>
        /// Flag to track when we're evaluating SmartExpression (dialogue/quest conditions).
        /// Only redirect overhead checks during this context to avoid interfering with normal gameplay.
        /// </summary>
        public static bool IsEvaluatingSmartExpression { get; set; } = false;

        /// <summary>
        /// Gets the "effective" player for dialogue/quest purposes.
        /// Returns the interacting player if one is tracked, otherwise MainGame.me.player.
        /// This is crucial for quest progression - if Player 2 picks up the corpse,
        /// dialogue checks should see that Player 2 has the corpse.
        /// </summary>
        public static WorldGameObject GetEffectivePlayer()
        {
            if (CurrentInteractingPlayer != null)
                return CurrentInteractingPlayer;
            return MainGame.me?.player;
        }
        
        /// <summary>
        /// Returns true if EITHER player has an overhead item (carrying something)
        /// This is used for quest checks where the game wants to know if "the player" has something
        /// </summary>
        public static bool AnyPlayerHasOverhead()
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return MainGame.me?.player_char?.has_overhead ?? false;
            
            bool p1Has = manager.Player1?.wgo?.components?.character?.has_overhead ?? false;
            bool p2Has = manager.Player2?.wgo?.components?.character?.has_overhead ?? false;
            
            return p1Has || p2Has;
        }
        
        /// <summary>
        /// Gets the overhead item from the effective player (interacting player)
        /// </summary>
        public static Item GetEffectivePlayerOverheadItem()
        {
            var effectivePlayer = GetEffectivePlayer();
            return effectivePlayer?.components?.character?.GetOverheadItem();
        }
        
        /// <summary>
        /// Returns true if any player is currently in a dialogue/interaction
        /// This checks multiple conditions including speech bubbles being active
        /// </summary>
        public static bool IsAnyPlayerInDialogue()
        {
            // Check if any GUI is open (dialogue, menu, etc.)
            if (!BaseGUI.all_guis_closed)
                return true;
            
            // Check if there are any active speech bubbles (cinematic dialogues)
            if (SpeechBubbleGUI.all != null && SpeechBubbleGUI.all.Count > 0)
                return true;
            
            // Check if either player has control disabled (usually means in dialogue/cutscene)
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return false;

            if (manager.Player1?.wgo?.components?.character != null &&
                !manager.Player1.wgo.components.character.control_enabled)
                return true;

            if (manager.Player2?.wgo?.components?.character != null &&
                !manager.Player2.wgo.components.character.control_enabled)
                return true;

            return false;
        }
        
        /// <summary>
        /// Gets the correct player to use for dialogue - either the interacting player or Player 1 as fallback
        /// </summary>
        public static WorldGameObject GetDialoguePlayer()
        {
            if (CurrentInteractingPlayer != null)
                return CurrentInteractingPlayer;
            
            return MainGame.me?.player;
        }
        
        /// <summary>
        /// Gets the bubble position transform for the currently interacting player
        /// </summary>
        public static Transform GetDialoguePlayerBubblePos()
        {
            var player = GetDialoguePlayer();
            return player?.bubble_pos_tf;
        }
    }

    /// <summary>
    /// CRITICAL: Block Player 2 from interacting when Player 1 is in dialogue (and vice versa)
    /// This prevents the "What" bug where pressing A during dialogue triggers another interaction
    /// </summary>
    [HarmonyPatch(typeof(BaseCharacterComponent), "ProcessInteraction")]
    public class ProcessInteraction_Patch
    {
        private static float lastBlockedLogTime = 0f;
        private const float LOG_DEBOUNCE_SECONDS = 5f; // Only log every 5 seconds
        
        [HarmonyPrefix]
        public static bool Prefix(BaseCharacterComponent __instance, ref bool __result)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return true; // Run original

            // Check for timeout to prevent permanent blocking
            LocalCoopDialoguePatches.CheckInteractionTimeout();

            // Check if ANY dialogue/GUI is active using our comprehensive check
            if (LocalCoopDialoguePatches.IsAnyPlayerInDialogue())
            {
                // If someone is in dialogue/cutscene, block the OTHER player from triggering new interactions
                if (LocalCoopDialoguePatches.CurrentInteractingPlayer != null)
                {
                    // There's an active interacting player - only allow that one to continue
                    if (__instance.wgo != LocalCoopDialoguePatches.CurrentInteractingPlayer)
                    {
                        // Debounce logging to avoid spam
                        if (UnityEngine.Time.time - lastBlockedLogTime > LOG_DEBOUNCE_SECONDS)
                        {
                            CoopMod.Logger.LogInfo($"[LocalCoopDialogue] ProcessInteraction blocked for {__instance.wgo?.obj_id} - another player is in dialogue");
                            lastBlockedLogTime = UnityEngine.Time.time;
                        }
                        __result = false;
                        return false; // Skip original
                    }
                }
                else
                {
                    // No tracked interacting player yet, but dialogue/cutscene is active
                    // Block Player 2 as a fallback (P1 likely started the cutscene)
                    if (__instance.wgo == manager.Player2?.wgo)
                    {
                        if (UnityEngine.Time.time - lastBlockedLogTime > LOG_DEBOUNCE_SECONDS)
                        {
                            CoopMod.Logger.LogInfo("[LocalCoopDialogue] ProcessInteraction blocked for Player 2 - dialogue/cutscene active");
                            lastBlockedLogTime = UnityEngine.Time.time;
                        }
                        __result = false;
                        return false; // Skip original
                    }
                }
            }

            return true; // Run original
        }
    }

    /// <summary>
    /// Patch InteractionComponent.Interact to track which player initiated the interaction
    /// </summary>
    [HarmonyPatch(typeof(InteractionComponent), "Interact")]
    public class InteractionComponent_Interact_Patch
    {
        // Ownership: online interaction filtering runs first and may suppress remote
        // proxy players. This prefix only handles local co-op player routing after that.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Normal)]
        public static bool Prefix(InteractionComponent __instance, ref bool __result)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
            {
                LocalCoopDialoguePatches.ClearCurrentInteractingPlayer();
                return true; // Run original
            }

            // CRITICAL: Block ALL interactions during cutscenes/dialogue
            // Check speech bubbles first (this is what indicates a cutscene is active)
            if (SpeechBubbleGUI.all != null && SpeechBubbleGUI.all.Count > 0)
            {
                CoopMod.Logger.LogInfo("[LocalCoopDialogue] Blocked interaction - speech bubble active (cutscene)");
                __result = false;
                return false; // Skip original - no interactions during cutscenes!
            }

            // Block interaction if another player is already in dialogue
            if (LocalCoopDialoguePatches.IsAnyPlayerInDialogue())
            {
                // Allow the current interacting player to continue, block others
                if (LocalCoopDialoguePatches.CurrentInteractingPlayer != null &&
                    LocalCoopDialoguePatches.CurrentInteractingPlayer != __instance.wgo)
                {
                    CoopMod.Logger.LogInfo("[LocalCoopDialogue] Blocked interaction - another player is in dialogue");
                    __result = false;
                    return false; // Skip original
                }
            }

            // Track which player is initiating this interaction
            WorldGameObject interactingWgo = __instance.wgo;
            
            if (interactingWgo == manager.Player1?.wgo)
            {
                LocalCoopDialoguePatches.SetCurrentInteractingPlayer(manager.Player1.wgo);
                CoopMod.Logger.LogInfo("[LocalCoopDialogue] Player 1 initiated interaction");
            }
            else if (interactingWgo == manager.Player2?.wgo)
            {
                LocalCoopDialoguePatches.SetCurrentInteractingPlayer(manager.Player2.wgo);
                CoopMod.Logger.LogInfo("[LocalCoopDialogue] Player 2 initiated interaction");
            }

            return true; // Run original
        }
    }

    /// <summary>
    /// Patch ToolComponent.UseTool to track which player is using a tool.
    /// This is critical because tool usage (like digging up Gerry) can trigger cutscenes,
    /// and we need to know which player triggered it.
    /// </summary>
    [HarmonyPatch(typeof(ToolComponent), "UseTool")]
    public class ToolComponent_UseTool_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(ToolComponent __instance)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return;

            // Only track if we're NOT already in a dialogue/cutscene
            // This prevents overwriting the tracking during a cutscene
            if (LocalCoopDialoguePatches.IsAnyPlayerInDialogue())
                return;

            // Track which player is using the tool
            WorldGameObject toolUser = __instance.wgo;
            
            if (toolUser == manager.Player1?.wgo)
            {
                LocalCoopDialoguePatches.SetCurrentInteractingPlayer(manager.Player1.wgo);
            }
            else if (toolUser == manager.Player2?.wgo)
            {
                LocalCoopDialoguePatches.SetCurrentInteractingPlayer(manager.Player2.wgo);
                CoopMod.Logger.LogInfo("[LocalCoopDialogue] Player 2 using tool - tracking as interacting player");
            }
        }
    }

    /// <summary>
    /// Patch WorldGameObject.ShowMultianswer to use the correct player's bubble position
    /// This is called when the player gets dialogue options to choose from
    /// </summary>
    [HarmonyPatch(typeof(WorldGameObject), "ShowMultianswer")]
    public class WorldGameObject_ShowMultianswer_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(WorldGameObject __instance, ref WorldGameObject talker)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return;

            // If this is being called for Player 1 but Player 2 is interacting, redirect
            var interactingPlayer = LocalCoopDialoguePatches.CurrentInteractingPlayer;
            if (interactingPlayer != null && __instance == manager.Player1?.wgo && interactingPlayer == manager.Player2?.wgo)
            {
                CoopMod.Logger.LogInfo("[LocalCoopDialogue] ShowMultianswer called for P1 but P2 is interacting");
            }
        }
    }

    /// <summary>
    /// Patch MultiAnswerGUI.ShowAnswers to use the correct player's position for the bubble
    /// </summary>
    [HarmonyPatch(typeof(MultiAnswerGUI), "ShowAnswers", 
        new System.Type[] { typeof(List<AnswerVisualData>), typeof(Transform), typeof(MultiAnswerGUI.MultiAnswerResult), typeof(bool), typeof(GJCommons.VoidDelegate), typeof(WorldGameObject) })]
    public class MultiAnswerGUI_ShowAnswers_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(ref Transform link)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return;

            // If there's a current interacting player, use their bubble position
            var interactingPlayer = LocalCoopDialoguePatches.CurrentInteractingPlayer;
            if (interactingPlayer != null && interactingPlayer.bubble_pos_tf != null)
            {
                // Always use the interacting player's position
                if (interactingPlayer == manager.Player2?.wgo)
                {
                    link = interactingPlayer.bubble_pos_tf;
                    CoopMod.Logger.LogInfo("[LocalCoopDialogue] Redirected MultiAnswer bubble to Player 2");
                }
            }
        }
    }

    /// <summary>
    /// Patch SpeechBubbleGUI.ShowMessage to use correct player for player speech bubbles.
    /// CRITICAL FIX: When P2 is interacting, we must redirect BOTH the link (Transform)
    /// AND the speaker_id so the bubble appears at and is associated with P2.
    /// 
    /// We use multiple sources to determine the correct player:
    /// 1. LastDialogueInputPlayer - who last pressed Select/Back (most accurate during cutscenes)
    /// 2. CurrentInteractingPlayer - who initiated the dialogue/interaction
    /// </summary>
    [HarmonyPatch(typeof(SpeechBubbleGUI), "ShowMessage", 
        new System.Type[] { typeof(long), typeof(string), typeof(Transform), typeof(GJCommons.VoidDelegate), typeof(bool), typeof(bool), typeof(SpeechBubbleGUI.SpeechBubbleType), typeof(bool), typeof(SmartSpeechEngine.VoiceID) })]
    public class SpeechBubbleGUI_ShowMessage_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(ref long speaker_id, ref Transform link, bool is_player)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return;

            // Only redirect if this is a player speech bubble
            if (!is_player)
                return;

            var p1Wgo = manager.Player1?.wgo;
            var p2Wgo = manager.Player2?.wgo;
            var p1UniqueId = p1Wgo?.unique_id ?? 0;
            var p2UniqueId = p2Wgo?.unique_id ?? 0;

            // Determine the active player using multiple sources (in order of priority):
            // 1. Who last pressed the dialogue advance key (Select/Back)
            // 2. Who initiated the current interaction
            WorldGameObject activePlayer = Player2InputPatches.LastDialogueInputPlayer 
                                          ?? LocalCoopDialoguePatches.CurrentInteractingPlayer;

            // Log for debugging
            CoopMod.Logger.LogInfo($"[LocalCoopDialogue] SpeechBubbleGUI.ShowMessage: is_player={is_player}, speaker_id={speaker_id}, P1id={p1UniqueId}, P2id={p2UniqueId}, " +
                $"lastDialogueInput={(Player2InputPatches.LastDialogueInputPlayer == p2Wgo ? "P2" : (Player2InputPatches.LastDialogueInputPlayer == p1Wgo ? "P1" : "none"))}, " +
                $"interacting={(LocalCoopDialoguePatches.CurrentInteractingPlayer == p2Wgo ? "P2" : (LocalCoopDialoguePatches.CurrentInteractingPlayer == p1Wgo ? "P1" : "none"))}, " +
                $"activePlayer={(activePlayer == p2Wgo ? "P2" : (activePlayer == p1Wgo ? "P1" : "none"))}");

            // If Player 2 is the active player, redirect the bubble to P2
            if (activePlayer != null && activePlayer == p2Wgo)
            {
                // Check if this is P1's speaker_id - redirect to P2
                if (speaker_id == p1UniqueId)
                {
                    // Use P2's unique_id so the bubble is associated with P2
                    speaker_id = p2UniqueId;
                    link = p2Wgo.bubble_pos_tf;
                    CoopMod.Logger.LogInfo($"[LocalCoopDialogue] Redirected player speech bubble from P1 (id={p1UniqueId}) to P2 (id={speaker_id})");
                }
            }
        }
    }

    /// <summary>
    /// Patch WorldGameObject.Say to use correct player position when saying as player
    /// </summary>
    [HarmonyPatch(typeof(WorldGameObject), "Say")]
    public class WorldGameObject_Say_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(WorldGameObject __instance, ref Transform overrode_pos, bool say_as_player)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return;

            // If this is the player speaking, use the correct player's position
            if (say_as_player || __instance.is_player)
            {
                var interactingPlayer = LocalCoopDialoguePatches.CurrentInteractingPlayer;
                if (interactingPlayer != null && 
                    interactingPlayer == manager.Player2?.wgo &&
                    overrode_pos == null) // Only override if not already specified
                {
                    // Set overrode_pos to Player 2's bubble position
                    overrode_pos = interactingPlayer.bubble_pos_tf;
                    CoopMod.Logger.LogInfo("[LocalCoopDialogue] Redirected Say bubble to Player 2");
                }
            }
        }
    }

    /// <summary>
    /// Patch Flow_GetPlayer.Invoke to return the correct player during dialogue.
    /// This is critical - NPC dialogue scripts use "Get Player" to get the player object,
    /// which should return the player who initiated the dialogue, not always Player 1.
    /// </summary>
    [HarmonyPatch(typeof(FlowCanvas.Nodes.Flow_GetPlayer), "Invoke")]
    public class Flow_GetPlayer_Invoke_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ref WorldGameObject __result)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return;

            // If Player 2 is the interacting player, return Player 2 instead of Player 1
            var interactingPlayer = LocalCoopDialoguePatches.CurrentInteractingPlayer;
            if (interactingPlayer != null && interactingPlayer == manager.Player2?.wgo)
            {
                __result = interactingPlayer;
                CoopMod.Logger.LogInfo("[LocalCoopDialogue] Flow_GetPlayer returning Player 2");
            }
        }
    }

    /// <summary>
    /// CRITICAL: Patch Flow_GetOverhead to get overhead from the correct player.
    /// This FlowCanvas node is used in dialogue scripts (like Gerry's) to check if
    /// the player has an overhead item (corpse). It calls MainGame.me.player.components.character.GetOverheadItem()
    /// directly, bypassing SmartExpression entirely!
    /// </summary>
    [HarmonyPatch(typeof(FlowCanvas.Nodes.Flow_GetOverhead), "RegisterPorts")]
    public class Flow_GetOverhead_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(FlowCanvas.Nodes.Flow_GetOverhead __instance)
        {
            // We can't easily patch the lambda inside RegisterPorts, so instead we'll
            // patch the item field after it's retrieved. See Flow_GetOverhead_ItemAccess_Patch
        }
    }

    /// <summary>
    /// Clear the interacting player when ALL GUIs are closed AND player has control back.
    /// We must be careful not to clear too early - intermediate popups (like TechUnlock)
    /// may close while we're still in a cutscene/dialogue sequence.
    /// </summary>
    [HarmonyPatch(typeof(BaseGUI), "Hide")]
    public class BaseGUI_Hide_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(BaseGUI __instance)
        {
            // Don't clear if not all GUIs are closed
            if (!BaseGUI.all_guis_closed)
                return;
            
            // Check if there's anything to clear (either tracking variable)
            if (LocalCoopDialoguePatches.CurrentInteractingPlayer == null && 
                Player2InputPatches.LastDialogueInputPlayer == null)
                return;

            // Don't clear if there are speech bubbles active (cutscene still happening)
            if (SpeechBubbleGUI.all != null && SpeechBubbleGUI.all.Count > 0)
                return;
                
            // Don't clear if player control is still disabled (still in cutscene)
            var manager = LocalCoopManager.Instance;
            if (manager != null && manager.IsLocalCoopEnabled)
            {
                // Check if either player has control disabled
                bool p1ControlDisabled = manager.Player1?.wgo?.components?.character != null &&
                    !manager.Player1.wgo.components.character.control_enabled;
                bool p2ControlDisabled = manager.Player2?.wgo?.components?.character != null &&
                    !manager.Player2.wgo.components.character.control_enabled;
                    
                if (p1ControlDisabled || p2ControlDisabled)
                {
                    // Still in cutscene, don't clear
                    return;
                }
            }
            
            CoopMod.Logger.LogInfo("[LocalCoopDialogue] All GUIs closed and players have control, clearing interaction tracking");
            LocalCoopDialoguePatches.ClearCurrentInteractingPlayer();
            
            // Also clear inventory isolation tracking
            InventoryIsolationPatches.ClearActiveUIPlayer();
        }
    }

    /// <summary>
    /// Also clear when control is re-enabled for the interacting player
    /// </summary>
    [HarmonyPatch(typeof(BaseCharacterComponent), "set_control_enabled")]
    public class CharacterControl_Enabled_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(BaseCharacterComponent __instance, bool value)
        {
            // When player control is re-enabled, they're no longer in dialogue
            if (value && __instance.wgo.is_player)
            {
                var manager = LocalCoopManager.Instance;
                if (manager == null || !manager.IsLocalCoopEnabled)
                    return;

                // If this is the interacting player regaining control, clear tracking
                if (LocalCoopDialoguePatches.CurrentInteractingPlayer != null &&
                    LocalCoopDialoguePatches.CurrentInteractingPlayer == __instance.wgo)
                {
                    CoopMod.Logger.LogInfo("[LocalCoopDialogue] Interacting player regained control, clearing tracking");
                    LocalCoopDialoguePatches.ClearCurrentInteractingPlayer();
                }
            }
        }
    }

    /// <summary>
    /// Patch EffectBubblesManager.ShowImmediately to redirect task completion bubbles to the correct player.
    /// This intercepts the "Task completed" bubble and moves it to the interacting player if it's Player 2.
    /// </summary>
    [HarmonyPatch(typeof(EffectBubblesManager), "ShowImmediately", 
        new System.Type[] { typeof(Vector3), typeof(string), typeof(EffectBubblesManager.BubbleColor), typeof(bool), typeof(float), typeof(bool) })]
    public class EffectBubblesManager_ShowImmediately_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(ref Vector3 position, string text, EffectBubblesManager.BubbleColor color)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return;

            // Only redirect bubbles that appear at Player 1's position
            var player1 = manager.Player1;
            var player2 = manager.Player2;
            if (player1 == null || player2 == null)
                return;

            // Check if this bubble is at Player 1's position
            Vector3 p1BubblePos = player1.wgo?.bubble_pos ?? Vector3.zero;
            float distanceToP1 = Vector3.Distance(position, p1BubblePos);
            
            // If the bubble is near Player 1's position and Player 2 is interacting, redirect it
            if (distanceToP1 < 0.5f)
            {
                var interactingPlayer = LocalCoopDialoguePatches.CurrentInteractingPlayer;
                if (interactingPlayer != null && interactingPlayer == player2.wgo)
                {
                    Vector3 p2BubblePos = player2.wgo?.bubble_pos ?? Vector3.zero;
                    if (p2BubblePos != Vector3.zero)
                    {
                        position = p2BubblePos;
                        CoopMod.Logger.LogInfo($"[LocalCoopDialogue] Redirected effect bubble '{text}' to Player 2");
                    }
                }
            }
        }
    }

    /// <summary>
    /// FIX: The game uses a global static DropResGameObject.currently_higlighted_obj to track
    /// which drop item is highlighted. When Player 1 updates their InteractionComponent and
    /// finds no drop nearby, it clears this global - preventing Player 2 from picking up drops!
    /// 
    /// This patch prevents Player 1 from clearing the highlight when Player 2 has a drop highlighted.
    /// We also need to track per-player highlights separately.
    /// </summary>
    public static class DropInteractionFix
    {
        // Track which drop each player has highlighted
        public static DropResGameObject Player1HighlightedDrop = null;
        public static DropResGameObject Player2HighlightedDrop = null;
        
        // Track which player's InteractionComponent is currently updating
        public static WorldGameObject CurrentUpdatingPlayer = null;
        
        /// <summary>
        /// Gets the correct highlighted drop for the given player
        /// </summary>
        public static DropResGameObject GetHighlightedDropForPlayer(WorldGameObject playerWgo)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return DropResGameObject.currently_higlighted_obj;
            
            if (playerWgo == manager.Player1?.wgo)
                return Player1HighlightedDrop;
            else if (playerWgo == manager.Player2?.wgo)
                return Player2HighlightedDrop;
            
            return DropResGameObject.currently_higlighted_obj;
        }
    }

    /// <summary>
    /// Track which player's InteractionComponent is updating
    /// </summary>
    [HarmonyPatch(typeof(InteractionComponent), "UpdateComponent")]
    public class InteractionComponent_UpdateComponent_Tracker
    {
        [HarmonyPrefix]
        public static void Prefix(InteractionComponent __instance)
        {
            if (__instance.wgo != null && __instance.wgo.is_player)
            {
                DropInteractionFix.CurrentUpdatingPlayer = __instance.wgo;
            }
        }
        
        [HarmonyPostfix]
        public static void Postfix(InteractionComponent __instance)
        {
            if (__instance.wgo != null && __instance.wgo.is_player)
            {
                DropInteractionFix.CurrentUpdatingPlayer = null;
            }
        }
    }

    /// <summary>
    /// Patch DropsList.SetHighlighted to track per-player drop highlights
    /// and maintain visual highlighting if EITHER player is near a drop
    /// </summary>
    [HarmonyPatch(typeof(DropsList), "SetHighlighted")]
    public class DropsList_SetHighlighted_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(DropsList __instance, DropResGameObject drop)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return true; // Run original
            
            // Determine which player is calling this (based on which InteractionComponent is updating)
            var currentPlayer = DropInteractionFix.CurrentUpdatingPlayer;
            if (currentPlayer == null)
                return true; // Run original if we can't determine
            
            // Track this player's highlighted drop
            if (currentPlayer == manager.Player1?.wgo)
            {
                DropInteractionFix.Player1HighlightedDrop = drop;
            }
            else if (currentPlayer == manager.Player2?.wgo)
            {
                DropInteractionFix.Player2HighlightedDrop = drop;
            }
            
            // Now handle visual highlighting ourselves - highlight if EITHER player is near it
            // Get the list of drops via reflection
            var dropsField = typeof(DropsList).GetField("drops", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (dropsField == null)
                return true; // Fallback to original
            
            var drops = dropsField.GetValue(__instance) as System.Collections.Generic.List<DropResGameObject>;
            if (drops == null)
                return true; // Fallback to original
            
            // Set visual highlight based on whether EITHER player has this drop highlighted
            foreach (var d in drops)
            {
                bool shouldHighlight = (d == DropInteractionFix.Player1HighlightedDrop) || 
                                       (d == DropInteractionFix.Player2HighlightedDrop);
                d.SetInteractionHilight(shouldHighlight);
            }
            
            return false; // Skip original - we handled it
        }
    }

    /// <summary>
    /// Patch TryOtherInteractions to use the correct player's highlighted drop
    /// instead of the global currently_higlighted_obj
    /// </summary>
    [HarmonyPatch(typeof(BaseCharacterComponent), "TryOtherInteractions")]
    public class TryOtherInteractions_Patch
    {
        // Ownership: local co-op manually handles per-player highlighted drops and
        // can skip the vanilla pickup flow. When online is also active, explicitly
        // notify CombatSync before mutating the drop so the remote side is updated.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static bool Prefix(BaseCharacterComponent __instance, ref bool __result)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return true; // Run original
            
            // Get the correct highlighted drop for THIS player
            DropResGameObject highlightedDrop = DropInteractionFix.GetHighlightedDropForPlayer(__instance.wgo);
            
            if (highlightedDrop == null)
            {
                __result = false;
                return false; // Skip original - no drop highlighted for this player
            }
            
            if (!highlightedDrop.CanPickupWithInteraction(__instance))
            {
                __result = false;
                return false; // Skip original
            }
            
            // Has overhead item? Drop it first
            if (__instance.has_overhead)
            {
                __instance.DropOverheadItem(false);
            }
            
            // Create the item from the drop
            Item item = new Item(highlightedDrop.res);
            if (highlightedDrop.res != null)
            {
                item.sub_name = highlightedDrop.res.sub_name;
            }
            
            // Set the overhead item on THIS player (the one who interacted)
            __instance.SetOverheadItem(item);
            
            // Mark the drop as collected
            highlightedDrop.is_collected = true;
            highlightedDrop.DestroyLinkedHint();
            
            // Clear our tracked highlight for this player
            if (__instance.wgo == manager.Player1?.wgo)
                DropInteractionFix.Player1HighlightedDrop = null;
            else if (__instance.wgo == manager.Player2?.wgo)
                DropInteractionFix.Player2HighlightedDrop = null;
            
            // Clear global too if it was this drop
            if (DropResGameObject.currently_higlighted_obj == highlightedDrop)
                DropResGameObject.currently_higlighted_obj = null;
            
            // Update direction using reflection (OnChangeDir is private)
            var onChangeDirMethod = typeof(BaseCharacterComponent).GetMethod("OnChangeDir", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            onChangeDirMethod?.Invoke(__instance, new object[] { LazyInput.GetDirection() });

            CombatSyncPatches.NotifyLocalDropCollectedFromLocalPath(highlightedDrop);
            
            __result = true;
            return false; // Skip original - we handled it
        }
    }

    /// <summary>
    /// CRITICAL: Patch SmartExpression.EvaluateBoolean to set context flag.
    /// This allows us to know when quest/dialogue condition checks are happening,
    /// so we can redirect overhead item checks only during those evaluations.
    /// </summary>
    [HarmonyPatch(typeof(SmartExpression), "EvaluateBoolean")]
    public class SmartExpression_EvaluateBoolean_Patch
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            LocalCoopDialoguePatches.IsEvaluatingSmartExpression = true;
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            LocalCoopDialoguePatches.IsEvaluatingSmartExpression = false;
        }
    }

    /// <summary>
    /// Patch SmartExpression.EvaluateFloat to set context flag.
    /// </summary>
    [HarmonyPatch(typeof(SmartExpression), "EvaluateFloat")]
    public class SmartExpression_EvaluateFloat_Patch
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            LocalCoopDialoguePatches.IsEvaluatingSmartExpression = true;
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            LocalCoopDialoguePatches.IsEvaluatingSmartExpression = false;
        }
    }

    /// <summary>
    /// Patch SmartExpression.EvaluateChance to set context flag.
    /// </summary>
    [HarmonyPatch(typeof(SmartExpression), "EvaluateChance")]
    public class SmartExpression_EvaluateChance_Patch
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            LocalCoopDialoguePatches.IsEvaluatingSmartExpression = true;
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            LocalCoopDialoguePatches.IsEvaluatingSmartExpression = false;
        }
    }

    /// <summary>
    /// Patch SmartExpression.Evaluate (void) to set context flag.
    /// </summary>
    [HarmonyPatch(typeof(SmartExpression), "Evaluate", typeof(WorldGameObject), typeof(WorldGameObject))]
    public class SmartExpression_Evaluate_Patch
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            LocalCoopDialoguePatches.IsEvaluatingSmartExpression = true;
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            LocalCoopDialoguePatches.IsEvaluatingSmartExpression = false;
        }
    }

    /// <summary>
    /// CRITICAL FIX: Patch GetOverheadItem to return the interacting player's overhead item
    /// when quest/dialogue scripts check MainGame.me.player.components.character.GetOverheadItem()
    /// 
    /// The problem: Both SmartExpression AND FlowCanvas nodes (like Flow_GetOverhead) check 
    /// MainGame.me.player (Player 1), so if Player 2 picks up the corpse and talks to Gerry, 
    /// Gerry still sees Player 1 has no corpse!
    /// 
    /// The fix: When GetOverheadItem is called on Player 1's character during a dialogue context,
    /// and Player 2 is the active player, return Player 2's overhead item instead.
    /// 
    /// Active player = LastDialogueInputPlayer OR CurrentInteractingPlayer
    /// 
    /// IMPORTANT: We redirect when CurrentInteractingPlayer is set to P2, even before speech bubbles appear.
    /// This handles zone triggers where the FlowScript runs BEFORE speech bubbles appear.
    /// </summary>
    [HarmonyPatch(typeof(BaseCharacterComponent), "GetOverheadItem")]
    public class GetOverheadItem_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(BaseCharacterComponent __instance, ref Item __result)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return true; // Run original
            
            // Only redirect if this is Player 1's character being checked
            if (__instance.wgo != manager.Player1?.wgo)
                return true; // Run original - this is not Player 1
            
            // Determine active player using same logic as dialogue bubbles:
            // Priority: LastDialogueInputPlayer > CurrentInteractingPlayer
            var activePlayer = Player2InputPatches.LastDialogueInputPlayer 
                              ?? LocalCoopDialoguePatches.CurrentInteractingPlayer;
            
            if (activePlayer == null || activePlayer != manager.Player2?.wgo)
                return true; // Run original - P2 is not the active player
            
            // If CurrentInteractingPlayer is explicitly set to P2, always redirect
            // This handles zone triggers where the FlowScript runs BEFORE speech bubbles appear
            bool shouldRedirect = (LocalCoopDialoguePatches.CurrentInteractingPlayer == manager.Player2?.wgo) ||
                                  LocalCoopDialoguePatches.IsEvaluatingSmartExpression ||
                                  (SpeechBubbleGUI.all != null && SpeechBubbleGUI.all.Count > 0) ||
                                  !BaseGUI.all_guis_closed;
            
            if (!shouldRedirect)
                return true; // Run original
            
            // Player 2 is the active player, but the game is checking Player 1's overhead
            // Return Player 2's overhead item instead!
            var p2Char = manager.Player2?.wgo?.components?.character;
            if (p2Char != null && p2Char.has_overhead)
            {
                // Use reflection to get the protected overhead_item field
                var overheadField = typeof(BaseCharacterComponent).GetField("overhead_item", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (overheadField != null)
                {
                    __result = overheadField.GetValue(p2Char) as Item;
                    CoopMod.Logger.LogInfo("[LocalCoopDialogue] Redirected GetOverheadItem from P1 to P2 (dialogue context)");
                    return false; // Skip original
                }
            }
            else if (p2Char != null && !p2Char.has_overhead)
            {
                // P2 has no overhead item
                __result = null;
                return false;
            }
            
            return true; // Run original as fallback
        }
    }

    /// <summary>
    /// CRITICAL FIX: Patch SetOverheadItem to set the overhead on the correct player.
    /// 
    /// The problem: When PutOverheadToWGO runs, it calls:
    ///   SmartExpression.player.components.character.SetOverheadItem(null)
    /// This always clears P1's overhead, even if P2 was the one carrying the item!
    /// 
    /// The fix: When SetOverheadItem is called on P1 during SmartExpression context
    /// and P2 is the interacting player, redirect the call to P2's character.
    /// </summary>
    [HarmonyPatch(typeof(BaseCharacterComponent), "SetOverheadItem")]
    public class SetOverheadItem_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(BaseCharacterComponent __instance, Item item)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return true; // Run original
            
            // Only redirect if this is Player 1's character
            bool isP1 = __instance.wgo == manager.Player1?.wgo;
            if (!isP1)
                return true; // Run original - this is not Player 1
            
            // Check if Player 2 is the interacting player (priority: LastDialogueInputPlayer > CurrentInteractingPlayer)
            var activePlayer = Player2InputPatches.LastDialogueInputPlayer 
                              ?? LocalCoopDialoguePatches.CurrentInteractingPlayer;
            
            if (activePlayer == null || activePlayer != manager.Player2?.wgo)
                return true; // Run original - P2 is not the active player
            
            // If CurrentInteractingPlayer is explicitly set to P2, always redirect
            // This handles zone triggers and interactions where the FlowScript runs BEFORE speech bubbles appear
            bool shouldRedirect = (LocalCoopDialoguePatches.CurrentInteractingPlayer == manager.Player2?.wgo) ||
                                  LocalCoopDialoguePatches.IsEvaluatingSmartExpression ||
                                  (SpeechBubbleGUI.all != null && SpeechBubbleGUI.all.Count > 0) ||
                                  !BaseGUI.all_guis_closed;
            
            if (!shouldRedirect)
                return true; // Run original - not in proper context
            
            // Player 2 is the active player, redirect SetOverheadItem to P2
            var p2Char = manager.Player2?.wgo?.components?.character;
            if (p2Char != null)
            {
                CoopMod.Logger.LogInfo($"[LocalCoopDialogue] Redirecting SetOverheadItem from P1 to P2 (item={item?.id ?? "null"})");
                p2Char.SetOverheadItem(item);
                return false; // Skip original - we handled it
            }
            
            return true; // Run original as fallback
        }
    }

    /// <summary>
    /// CRITICAL FIX: Patch has_overhead property getter to return true if active player has overhead
    /// This is used in many SmartExpression checks AND FlowCanvas nodes like Flow_GetOverhead.
    /// 
    /// Active player = LastDialogueInputPlayer OR CurrentInteractingPlayer
    /// 
    /// IMPORTANT: We remove the "dialogue context" restriction when CurrentInteractingPlayer is set
    /// because zone triggers (GDZone) set CurrentInteractingPlayer BEFORE speech bubbles appear.
    /// </summary>
    [HarmonyPatch(typeof(BaseCharacterComponent), "get_has_overhead")]
    public class HasOverhead_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(BaseCharacterComponent __instance, ref bool __result)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return true; // Run original
            
            // Only redirect if this is Player 1's character being checked
            if (__instance.wgo != manager.Player1?.wgo)
                return true; // Run original - this is not Player 1
            
            // Determine active player using same logic as dialogue bubbles:
            // Priority: LastDialogueInputPlayer > CurrentInteractingPlayer
            var activePlayer = Player2InputPatches.LastDialogueInputPlayer 
                              ?? LocalCoopDialoguePatches.CurrentInteractingPlayer;
            
            if (activePlayer == null || activePlayer != manager.Player2?.wgo)
                return true; // Run original - P2 is not the active player
            
            // If CurrentInteractingPlayer is explicitly set to P2, always redirect
            // This handles zone triggers where the FlowScript runs BEFORE speech bubbles appear
            bool shouldRedirect = (LocalCoopDialoguePatches.CurrentInteractingPlayer == manager.Player2?.wgo) ||
                                  LocalCoopDialoguePatches.IsEvaluatingSmartExpression ||
                                  (SpeechBubbleGUI.all != null && SpeechBubbleGUI.all.Count > 0) ||
                                  !BaseGUI.all_guis_closed;
            
            if (!shouldRedirect)
                return true; // Run original
            
            // Player 2 is the active player, but the game is checking Player 1's has_overhead
            // Return Player 2's has_overhead instead!
            var p2Char = manager.Player2?.wgo?.components?.character;
            if (p2Char != null)
            {
                __result = p2Char.has_overhead;
                // Removed log to prevent spam - this can run every frame
                return false; // Skip original
            }
            
            return true; // Run original as fallback
        }
    }

    /// <summary>
    /// CRITICAL FIX: Patch GDZone.OnTriggerEnter2D to track which player entered the zone.
    /// This is essential for the Gerry corpse check - when P2 with the corpse enters the morgue zone,
    /// we need to set CurrentInteractingPlayer to P2 BEFORE the FlowScript runs and checks has_overhead.
    /// 
    /// The problem: GDZone triggers a FlowScript (on_enter_mortuary_gd_zone) which checks
    /// MainGame.me.player.has_overhead. Without this patch, it always checks P1 even if P2 entered.
    /// </summary>
    [HarmonyPatch(typeof(GDZone), "OnTriggerEnter2D")]
    public class GDZone_OnTriggerEnter2D_Patch
    {
        // Ownership: OnlineZonePatches filters remote online proxy players first.
        // This observer only records local co-op zone ownership for local players.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Normal)]
        public static void Prefix(UnityEngine.Collider2D collision)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return;

            if (collision == null)
                return;

            // Get the WorldGameObject that entered the zone
            WorldGameObject wgo = collision.gameObject.GetComponentInParent<WorldGameObject>();
            if (wgo == null || !wgo.is_player)
                return;

            // Track which player entered the zone
            if (wgo == manager.Player1?.wgo)
            {
                LocalCoopDialoguePatches.SetCurrentInteractingPlayer(manager.Player1.wgo);
                Player2InputPatches.LastDialogueInputPlayer = manager.Player1.wgo;
                CoopMod.Logger.LogInfo("[LocalCoopDialogue] GDZone: Player 1 entered zone - tracking as interacting player");
            }
            else if (wgo == manager.Player2?.wgo)
            {
                LocalCoopDialoguePatches.SetCurrentInteractingPlayer(manager.Player2.wgo);
                Player2InputPatches.LastDialogueInputPlayer = manager.Player2.wgo;
                CoopMod.Logger.LogInfo("[LocalCoopDialogue] GDZone: Player 2 entered zone - tracking as interacting player");
            }
        }
    }
}

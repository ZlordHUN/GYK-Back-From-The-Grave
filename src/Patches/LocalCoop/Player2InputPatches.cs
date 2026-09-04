using HarmonyLib;
using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches LazyInput to route gamepad input to Player 2
    /// Player 1 gets keyboard, Player 2 gets gamepad
    /// </summary>
    [HarmonyPatch(typeof(LazyInput))]
    public class Player2InputPatches
    {
        private static KeyboardController player1Keyboard;
        private static GamePadController player2Gamepad;
        private static bool initialized = false;
        
        // Track which player's Update is currently executing
        public static BaseCharacterComponent currentUpdatingCharacter = null;

        /// <summary>
        /// Track which player last pressed the dialogue advance key (Select/Back)
        /// This is used to attribute player speech bubbles to the correct player during cutscenes
        /// </summary>
        public static WorldGameObject LastDialogueInputPlayer = null;

        /// <summary>
        /// Clear a key from Player 2's pressed_keys to prevent double-processing
        /// This is needed when P2 opens a GUI - the key that opened it shouldn't also close it
        /// </summary>
        public static void ClearP2PressedKey(GameKey key)
        {
            if (player2Gamepad != null)
            {
                player2Gamepad.pressed_keys.Remove(key);
            }
        }

        /// <summary>
        /// Clear a key from Player 1's pressed_keys to prevent double-processing
        /// </summary>
        public static void ClearP1PressedKey(GameKey key)
        {
            if (player1Keyboard != null)
            {
                player1Keyboard.pressed_keys.Remove(key);
            }
        }

        /// <summary>
        /// Initialize input controllers
        /// </summary>
        private static void InitializeControllers()
        {
            if (initialized)
                return;

            player1Keyboard = new KeyboardController();
            player2Gamepad = new GamePadController();
            initialized = true;

            // CoopMod.Logger.LogInfo("[Player2Input] Input controllers initialized");
        }

        /// <summary>
        /// Update input controllers every frame
        /// </summary>
        [HarmonyPatch("Update")]
        [HarmonyPrefix]
        public static void Update_Prefix()
        {
            var manager = GraveyardKeeperCoop.LocalCoop.LocalCoopManager.Instance;
            
            // Only update controllers if local co-op is actually active (Player 2 exists)
            if (manager == null || !manager.IsLocalCoopEnabled || manager.Player2 == null)
                return;

            if (!initialized)
            {
                InitializeControllers();
                // CoopMod.Logger.LogInfo("[Player2Input] Controllers initialized in Update");
            }

            player1Keyboard.Update();
            player2Gamepad.Update();
        }

        /// <summary>
        /// Route GetDirection based on which player is calling
        /// During cutscenes/dialogue, only the interacting player can move
        /// </summary>
        [HarmonyPatch("GetDirection")]
        [HarmonyPrefix]
        public static bool GetDirection_Prefix(ref Vector2 __result)
        {
            var manager = GraveyardKeeperCoop.LocalCoop.LocalCoopManager.Instance;
            
            // Only intercept if local co-op is actually active (not just configured)
            if (manager == null || !manager.IsLocalCoopEnabled || manager.Player2 == null)
                return true; // Use original method

            InitializeControllers();

            // Determine which player is calling this
            if (currentUpdatingCharacter != null)
            {
                // Only block the other player during CUTSCENES (speech bubbles), not normal GUI dialogues
                // This allows both players to move freely unless one is in a cinematic cutscene
                bool inCutscene = SpeechBubbleGUI.all != null && SpeechBubbleGUI.all.Count > 0;
                var interactingPlayer = LocalCoopDialoguePatches.CurrentInteractingPlayer;
                
                // Debug: If this is a player character but we can't identify them, log details
                bool isP1 = IsPlayer1(currentUpdatingCharacter);
                bool isP2 = IsPlayer2(currentUpdatingCharacter);
                if (!isP1 && !isP2 && currentUpdatingCharacter.wgo.is_player)
                {
                    CoopMod.Logger.LogWarning($"[Player2Input] Unidentified player! wgo.obj_id={currentUpdatingCharacter.wgo.obj_id}, " +
                        $"P1.wgo?.obj_id={manager.Player1?.wgo?.obj_id}, P2.wgo?.obj_id={manager.Player2?.wgo?.obj_id}, " +
                        $"wgo match P1={(currentUpdatingCharacter.wgo == manager.Player1?.wgo)}, wgo match P2={(currentUpdatingCharacter.wgo == manager.Player2?.wgo)}");
                }
                
                if (isP1)
                {
                    // Block P1 movement only during cutscenes when P2 is the interacting player
                    if (inCutscene && interactingPlayer != null && interactingPlayer != manager.Player1?.wgo)
                    {
                        __result = Vector2.zero;
                        return false;
                    }
                    __result = player1Keyboard.direction;
                    // DEBUG: Log if P1 has keyboard input
                    if (__result.magnitude > 0.1f)
                    {
                        CoopMod.Logger.LogInfo($"[Player2Input] P1 GetDirection returning: {__result}");
                    }
                    return false; // Skip original
                }
                else if (isP2)
                {
                    // Block P2 movement only during cutscenes when P1 is the interacting player
                    if (inCutscene && interactingPlayer != null && interactingPlayer != manager.Player2?.wgo)
                    {
                        __result = Vector2.zero;
                        return false;
                    }
                    __result = player2Gamepad.direction;
                    // DEBUG: Log if P2 has gamepad input
                    if (__result.magnitude > 0.1f)
                    {
                        CoopMod.Logger.LogInfo($"[Player2Input] P2 GetDirection returning: {__result}");
                    }
                    return false; // Skip original
                }
                else
                {
                    // Character is not P1 or P2 - this shouldn't happen for players
                    CoopMod.Logger.LogWarning($"[Player2Input] GetDirection called for unknown player character!");
                }
            }
            else
            {
                // No tracking context - fall through to original
                // This can happen if GetDirection is called outside of UpdateComponent
            }

            return true; // Use original method
        }

        /// <summary>
        /// Route GetKey based on which player is calling
        /// </summary>
        [HarmonyPatch("GetKey")]
        [HarmonyPrefix]
        public static bool GetKey_Prefix(GameKey key, ref bool __result)
        {
            var manager = GraveyardKeeperCoop.LocalCoop.LocalCoopManager.Instance;
            
            // Only intercept if local co-op is actually active (not just configured)
            if (manager == null || !manager.IsLocalCoopEnabled || manager.Player2 == null)
                return true;

            InitializeControllers();

            if (currentUpdatingCharacter != null)
            {
                if (IsPlayer1(currentUpdatingCharacter))
                {
                    __result = player1Keyboard.holded_keys.Contains(key);
                    return false;
                }
                else if (IsPlayer2(currentUpdatingCharacter))
                {
                    __result = player2Gamepad.holded_keys.Contains(key);
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Route GetKeyDown based on which player is calling
        /// Also tracks who pressed Select/Back during dialogue for bubble attribution
        /// And tracks who pressed Inventory/GameGUI keys for inventory isolation
        /// </summary>
        [HarmonyPatch("GetKeyDown")]
        [HarmonyPrefix]
        public static bool GetKeyDown_Prefix(GameKey key, ref bool __result)
        {
            var manager = GraveyardKeeperCoop.LocalCoop.LocalCoopManager.Instance;
            
            // Only intercept if local co-op is actually active (not just configured)
            if (manager == null || !manager.IsLocalCoopEnabled || manager.Player2 == null)
                return true;

            InitializeControllers();

            // CRITICAL: Track which player pressed the dialogue advance key (Select/Back)
            // This is used during cutscenes to attribute speech bubbles to the correct player
            if (key == GameKey.Select || key == GameKey.Back)
            {
                // Check if either player pressed this key
                bool p1Pressed = player1Keyboard.pressed_keys.Contains(key);
                bool p2Pressed = player2Gamepad.pressed_keys.Contains(key);
                
                if (p2Pressed && manager.Player2?.wgo != null)
                {
                    LastDialogueInputPlayer = manager.Player2.wgo;
                    CoopMod.Logger.LogInfo($"[Player2Input] P2 pressed {key} - tracking as dialogue input source");
                }
                else if (p1Pressed && manager.Player1?.wgo != null)
                {
                    LastDialogueInputPlayer = manager.Player1.wgo;
                    CoopMod.Logger.LogInfo($"[Player2Input] P1 pressed {key} - tracking as dialogue input source");
                }
            }

            // CRITICAL: Track which player pressed inventory/menu keys for inventory isolation
            // This MUST be tracked BEFORE returning the result, so the UI knows who opened it
            if (key == GameKey.Inventory || key == GameKey.GameGUI || key == GameKey.KnownNPCs || 
                key == GameKey.Techs || key == GameKey.Map)
            {
                // Check if either player pressed this key
                bool p1Pressed = player1Keyboard.pressed_keys.Contains(key);
                bool p2Pressed = player2Gamepad.pressed_keys.Contains(key);
                
                if (p2Pressed && manager.Player2?.wgo != null)
                {
                    InventoryIsolationPatches.SetLastInventoryInputPlayer(manager.Player2.wgo);
                }
                else if (p1Pressed && manager.Player1?.wgo != null)
                {
                    InventoryIsolationPatches.SetLastInventoryInputPlayer(manager.Player1.wgo);
                }
            }

            if (currentUpdatingCharacter != null)
            {
                if (IsPlayer1(currentUpdatingCharacter))
                {
                    __result = player1Keyboard.pressed_keys.Contains(key);
                    return false;
                }
                else if (IsPlayer2(currentUpdatingCharacter))
                {
                    __result = player2Gamepad.pressed_keys.Contains(key);
                    return false;
                }
            }
            
            // CRITICAL: When a GUI is open, route input based on who opened it
            // This prevents P1's keyboard from closing P2's GUI (and vice versa)
            var activeUIPlayer = InventoryIsolationPatches.ActiveUIPlayer;
            if (activeUIPlayer != null && !BaseGUI.all_guis_closed)
            {
                if (activeUIPlayer == manager.Player2?.wgo)
                {
                    // P2 has the GUI open - only respond to P2's gamepad
                    __result = player2Gamepad.pressed_keys.Contains(key);
                    return false;
                }
                else if (activeUIPlayer == manager.Player1?.wgo)
                {
                    // P1 has the GUI open - only respond to P1's keyboard
                    __result = player1Keyboard.pressed_keys.Contains(key);
                    return false;
                }
            }

            return true;
        }

        private static bool IsPlayer1(BaseCharacterComponent character)
        {
            var manager = GraveyardKeeperCoop.LocalCoop.LocalCoopManager.Instance;
            if (manager?.Player1 == null)
                return false;

            return character.wgo == manager.Player1.wgo;
        }

        private static bool IsPlayer2(BaseCharacterComponent character)
        {
            var manager = GraveyardKeeperCoop.LocalCoop.LocalCoopManager.Instance;
            if (manager?.Player2 == null)
                return false;

            return character.wgo == manager.Player2.wgo;
        }
    }

    /// <summary>
    /// Patch BaseCharacterComponent.UpdateComponent to track which player is updating
    /// UpdateComponent is called every frame and decides whether to call UpdatePlayer
    /// </summary>
    [HarmonyPatch(typeof(BaseCharacterComponent), "UpdateComponent")]
    public class CharacterUpdateTracker
    {
        private static bool hasLoggedP2Update = false;
        private static float lastP2DiagTime = 0f;
        private static long __charUpdateStart;

        [HarmonyPrefix]
        public static void UpdateComponent_Prefix(BaseCharacterComponent __instance)
        {
            // Only track player characters
            if (!__instance.wgo.is_player)
                return;

            Player2InputPatches.currentUpdatingCharacter = __instance;
            __charUpdateStart = GraveyardKeeperCoop.Utils.FrameProfiler.BeginSection();

            // Debug log to see if Player 2's UpdateComponent is being called
            var manager = GraveyardKeeperCoop.LocalCoop.LocalCoopManager.Instance;
            if (manager?.Player2 != null && __instance.wgo == manager.Player2.wgo)
            {
                // Log once initially
                if (!hasLoggedP2Update)
                {
                    CoopMod.Logger.LogInfo("[Player2Input] Player 2 UpdateComponent is being called!");
                    CoopMod.Logger.LogInfo($"[Player2Input] P2 control_enabled: {__instance.control_enabled}");
                    CoopMod.Logger.LogInfo($"[Player2Input] P2 can_be_locally_controlled: {__instance.can_be_locally_controlled}");
                    CoopMod.Logger.LogInfo($"[Player2Input] P2 player_controlled_by_script: {__instance.player_controlled_by_script}");
                    hasLoggedP2Update = true;
                }
                // Log periodically if P2 control is disabled
                else if (UnityEngine.Time.time - lastP2DiagTime > 5f)
                {
                    lastP2DiagTime = UnityEngine.Time.time;
                    bool controlDisabled = !__instance.control_enabled || !BaseGUI.all_guis_closed || 
                                           !__instance.can_be_locally_controlled || 
                                           MainGame.me.build_mode_logics.IsBuilding();
                    if (controlDisabled)
                    {
                        CoopMod.Logger.LogWarning($"[Player2Input] P2 control DISABLED! control_enabled={__instance.control_enabled}, " +
                            $"all_guis_closed={BaseGUI.all_guis_closed}, can_be_locally_controlled={__instance.can_be_locally_controlled}, " +
                            $"player_controlled_by_script={__instance.player_controlled_by_script}");
                    }
                }
            }
        }

        [HarmonyPostfix]
        public static void UpdateComponent_Postfix(BaseCharacterComponent __instance)
        {
            // Only clear for player characters
            if (!__instance.wgo.is_player)
                return;

            Player2InputPatches.currentUpdatingCharacter = null;
            if (__charUpdateStart != 0)
            {
                GraveyardKeeperCoop.Utils.FrameProfiler.EndSection("Char.Update", __charUpdateStart);
                __charUpdateStart = 0;
            }
        }
    }
}

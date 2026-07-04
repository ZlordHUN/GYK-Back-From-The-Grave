using HarmonyLib;
using UnityEngine;
using GraveyardKeeperCoop.Utils;
using GraveyardKeeperCoop.Network;
using GraveyardKeeperCoop.UI;
using GraveyardKeeperCoop.LocalCoop;
using GraveyardKeeperCoop.Multiplayer;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches PlayerComponent to sync player state across network
    /// </summary>
    [HarmonyPatch(typeof(PlayerComponent))]
    public class PlayerPatches
    {
        private static bool hasShownSessionMessage = false;

        // Note: FixedUpdate patch removed - PlayerComponent doesn't have a FixedUpdate method
        // Position synchronization will be handled differently

        /// <summary>
        /// Apply the current peer's saved multiplayer position before the base game
        /// resets the local player from GameSave.player_position.
        /// </summary>
        [HarmonyPatch("SpawnPlayer")]
        [HarmonyPrefix]
        public static void SpawnPlayer_Prefix(bool is_local_player)
        {
            if (is_local_player)
            {
                MultiplayerSavePositions.TryApplyLocalPositionForCurrentSlot();
            }
        }

        /// <summary>
        /// Log when player spawns and add session start message
        /// </summary>
        [HarmonyPatch("SpawnPlayer")]
        [HarmonyPostfix]
        public static void SpawnPlayer_Postfix(bool is_local_player, PlayerComponent __result)
        {
            if (__result != null)
            {
                CoopMod.Logger.LogInfo($"Player spawned: is_local={is_local_player}, position={__result.transform.position}");

                // Only process for local player
                if (is_local_player && !hasShownSessionMessage)
                {
                    hasShownSessionMessage = true;

                    // Detect multiplayer BEFORE clearing messages
                    bool isInLobby = SteamLobbyManager.Instance?.IsInLobby ?? false;
                    bool isOnlineCoopRunning = OnlineCoopManager.Instance?.IsOnlineCoopEnabled == true;
                    bool isMultiplayerSession = LobbyGUI.IsMultiplayerSessionActive; // Check if started from multiplayer menu
                    bool isMultiplayer = isInLobby || isOnlineCoopRunning || isMultiplayerSession;

                    CoopMod.Logger.LogInfo($"[PlayerPatch] Session start - isInLobby: {isInLobby}, isOnlineCoopRunning: {isOnlineCoopRunning}, isMultiplayerSession: {isMultiplayerSession}, isMultiplayer: {isMultiplayer}");

                    // Only clear and add session message if NOT in multiplayer
                    // (multiplayer messages are handled by LobbyGUI when it opens)
                    if (!isMultiplayer)
                    {
                        // Clear any previous session messages
                        ChatManager.ClearMessages();
                        CoopMod.Logger.LogInfo("[PlayerPatch] Cleared previous session messages");

                        // Singleplayer - show simple message
                        ChatManager.AddMessage("[System] Singleplayer Session Started");
                        CoopMod.Logger.LogInfo("[PlayerPatch] Added singleplayer session message");
                    }
                    else
                    {
                        CoopMod.Logger.LogInfo("[PlayerPatch] Multiplayer session detected - messages already set by LobbyGUI");
                    }

                }
            }
        }

        /// <summary>
        /// Reset the flag when returning to main menu
        /// </summary>
        public static void ResetSessionFlag()
        {
            hasShownSessionMessage = false;
            CoopMod.Logger.LogInfo("[PlayerPatch] Session flag reset");
        }
    }
}

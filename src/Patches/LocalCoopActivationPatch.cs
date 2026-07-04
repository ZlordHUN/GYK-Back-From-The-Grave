using HarmonyLib;
using GraveyardKeeperCoop.LocalCoop;
using GraveyardKeeperCoop.Network;
using GraveyardKeeperCoop.Multiplayer;
using UnityEngine;
using Steamworks;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches to enable local co-op and online co-op when player spawns in game
    /// </summary>
    [HarmonyPatch]
    public class LocalCoopActivationPatch
    {
        private static bool hasActivated = false;

        /// <summary>
        /// After PlayerComponent.SpawnPlayer is called, activate appropriate co-op mode
        /// </summary>
        [HarmonyPatch(typeof(PlayerComponent), "SpawnPlayer")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)] // Run after other postfixes
        public static void ActivateCoopOnSpawn_Postfix(bool is_local_player, PlayerComponent __result)
        {
            CoopMod.Logger.LogInfo($"[CoopActivation] ActivateCoopOnSpawn_Postfix called, is_local={is_local_player}");
            
            // Only activate for local player spawn
            if (!is_local_player)
            {
                CoopMod.Logger.LogInfo("[CoopActivation] Non-local player spawned, ignoring");
                return;
            }

            // Only activate once per game session
            if (hasActivated)
            {
                CoopMod.Logger.LogInfo("[CoopActivation] Already activated, skipping");
                return;
            }

            CoopMod.Logger.LogInfo("[CoopActivation] Local player spawned!");

            // Check if we're in an online multiplayer session
            if (SteamLobbyManager.Instance?.IsInLobby == true)
            {
                CoopMod.Logger.LogInfo("[CoopActivation] In online lobby - spawning remote player immediately (like local coop)");
                
                // Spawn remote player immediately, just like local coop does
                // This ensures both players are visible during the intro cutscene
                var onlineManager = OnlineCoopManager.Instance;
                if (onlineManager != null)
                {
                    var lobbyManager = SteamLobbyManager.Instance;
                    int memberCount = lobbyManager.GetLobbyMemberCount();
                    
                    if (memberCount > 1)
                    {
                        CSteamID myID = SteamUser.GetSteamID();
                        bool isHost = lobbyManager.IsHost;
                        
                        if (isHost)
                        {
                            // Find the client
                            for (int i = 0; i < memberCount; i++)
                            {
                                CSteamID memberID = SteamMatchmaking.GetLobbyMemberByIndex(lobbyManager.CurrentLobbyID, i);
                                if (memberID != myID)
                                {
                                    CoopMod.Logger.LogInfo($"[CoopActivation] Host enabling online coop with client {memberID}");
                                    onlineManager.EnableAsHost(memberID);
                                    break;
                                }
                            }
                        }
                        else
                        {
                            // Get the host's ID
                            CSteamID hostID = SteamMatchmaking.GetLobbyOwner(lobbyManager.CurrentLobbyID);
                            CoopMod.Logger.LogInfo($"[CoopActivation] Client enabling online coop with host {hostID}");
                            onlineManager.EnableAsClient(hostID);
                        }
                        
                        // Position sync is blocked until MainGame.game_started is true
                        // This keeps P2 next to P1 during the intro cutscene
                    }
                }
                
                hasActivated = true;
                return;
            }

            // Check if local co-op is enabled in config
            if (!ModConfig.EnableLocalCoop.Value)
            {
                CoopMod.Logger.LogInfo("[CoopActivation] Local co-op disabled in config");
                return;
            }

            CoopMod.Logger.LogInfo("[CoopActivation] Player 1 spawned - enabling local co-op...");

            // Find or create the LocalCoopManager
            var manager = LocalCoopManager.Instance;
            if (manager == null)
            {
                // Create it on demand if it doesn't exist
                CoopMod.Logger.LogInfo("[CoopActivation] Creating LocalCoopManager on demand...");
                var localCoopObj = new UnityEngine.GameObject("LocalCoopManager");
                UnityEngine.Object.DontDestroyOnLoad(localCoopObj);
                manager = localCoopObj.AddComponent<LocalCoopManager>();
            }

            // Enable local co-op (spawns Player 2)
            manager.EnableLocalCoop();
            hasActivated = true;

            CoopMod.Logger.LogInfo("[CoopActivation] Local co-op activated!");
        }

        /// <summary>
        /// Reset activation flag when returning to main menu
        /// </summary>
        [HarmonyPatch(typeof(MainMenuGUI), "Open")]
        [HarmonyPostfix]
        public static void MainMenuOpen_Postfix()
        {
            hasActivated = false;
            CoopMod.Logger.LogInfo("[CoopActivation] Reset activation flag (returned to main menu)");
            
            // Also reset GameLoadSync
            if (GameLoadSync.Instance != null)
            {
                GameLoadSync.Instance.ResetSyncState();
            }
        }

        /// <summary>
        /// Allow other systems (e.g. host hot-reload flow) to reset the activation flag
        /// so that <see cref="ActivateCoopOnSpawn_Postfix"/> runs again when the player
        /// respawns after an in-game save load.
        /// </summary>
        public static void ResetActivation()
        {
            hasActivated = false;
            CoopMod.Logger.LogInfo("[CoopActivation] Activation flag reset (external request)");
        }
    }
}

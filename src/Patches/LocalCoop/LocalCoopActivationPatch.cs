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
        private static bool isWaitingForHotReloadSpawn;
        private static PlayerComponent playerBeforeHotReload;

        /// <summary>
        /// True while an in-game save replacement is downloading/loading and the old
        /// local player must not be used to reactivate online co-op.
        /// </summary>
        public static bool IsWaitingForHotReloadSpawn => isWaitingForHotReloadSpawn;

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

            if (isWaitingForHotReloadSpawn)
            {
                if (object.ReferenceEquals(__result, playerBeforeHotReload))
                {
                    CoopMod.Logger.LogWarning(
                        "[CoopActivation] Ignoring local spawn from the pre-reload player");
                    return;
                }

                isWaitingForHotReloadSpawn = false;
                playerBeforeHotReload = null;
                CoopMod.Logger.LogInfo(
                    "[CoopActivation] Fresh post-reload local player spawned; reactivation unblocked");
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
            isWaitingForHotReloadSpawn = false;
            playerBeforeHotReload = null;
            CoopMod.Logger.LogInfo("[CoopActivation] Reset activation flag (returned to main menu)");
            
            // Also reset GameLoadSync
            if (GameLoadSync.Instance != null)
            {
                GameLoadSync.Instance.ResetSyncState();
            }
        }

        /// <summary>
        /// Begin an in-game save replacement. The old world remains alive while a client
        /// downloads the host save, so clearing only <see cref="hasActivated"/> would let
        /// OnlineCoopManager's frame update immediately reactivate against the old player.
        /// Keep auto-activation blocked until SpawnPlayer reports a different local player.
        /// </summary>
        public static void BeginHotReload()
        {
            playerBeforeHotReload = MainGame.me?.player != null
                ? MainGame.me.player.GetComponent<PlayerComponent>()
                : null;
            isWaitingForHotReloadSpawn = true;
            hasActivated = false;
            CoopMod.Logger.LogInfo(
                $"[CoopActivation] Hot-reload gate armed (old player captured={playerBeforeHotReload != null})");
        }
    }
}

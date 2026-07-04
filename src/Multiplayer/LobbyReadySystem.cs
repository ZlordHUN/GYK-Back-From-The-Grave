using System;
using System.Collections.Generic;
using System.Text;
using Steamworks;
using GraveyardKeeperCoop.Network;
using GraveyardKeeperCoop.Utils;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Manages the ready state of players in the lobby.
    /// Host can start when all players are ready.
    /// Clients see a Ready button instead of Start Game.
    /// </summary>
    public static class LobbyReadySystem
    {
        // P2P message types
        public const string MSG_READY_STATE = "READY:";
        public const string MSG_ALL_READY_CHECK = "ALLREADY?";
        public const string MSG_ALL_READY_RESPONSE = "ALLREADY:";
        
        // Channel for ready state messages (same as chat channel for now)
        public const int READY_CHANNEL = 1;
        
        // Track ready states by Steam ID
        private static Dictionary<CSteamID, bool> playerReadyStates = new Dictionary<CSteamID, bool>();
        
        // Event fired when a player's ready state changes
        public static event Action<CSteamID, bool> OnPlayerReadyChanged;
        
        // Event fired when all players are ready (host only)
        public static event Action OnAllPlayersReady;
        
        /// <summary>
        /// Reset all ready states (called when entering a new lobby)
        /// </summary>
        public static void ResetReadyStates()
        {
            playerReadyStates.Clear();
            CoopMod.Logger.LogInfo("[ReadySystem] Ready states cleared");
        }
        
        /// <summary>
        /// Remove a player from the ready system (when they leave)
        /// </summary>
        public static void RemovePlayer(CSteamID playerID)
        {
            if (playerReadyStates.ContainsKey(playerID))
            {
                playerReadyStates.Remove(playerID);
                CoopMod.Logger.LogInfo($"[ReadySystem] Removed player {playerID} from ready states");
                OnPlayerReadyChanged?.Invoke(playerID, false);
            }
        }
        
        /// <summary>
        /// Check if the local player is ready
        /// </summary>
        public static bool IsLocalPlayerReady()
        {
            CSteamID localID = SteamUser.GetSteamID();
            return playerReadyStates.ContainsKey(localID) && playerReadyStates[localID];
        }
        
        /// <summary>
        /// Check if a specific player is ready
        /// </summary>
        public static bool IsPlayerReady(CSteamID playerID)
        {
            return playerReadyStates.ContainsKey(playerID) && playerReadyStates[playerID];
        }
        
        /// <summary>
        /// Toggle the local player's ready state
        /// </summary>
        public static void ToggleLocalReady()
        {
            CSteamID localID = SteamUser.GetSteamID();
            bool newState = !IsLocalPlayerReady();
            SetLocalReady(newState);
        }
        
        /// <summary>
        /// Set the local player's ready state and broadcast to others
        /// </summary>
        public static void SetLocalReady(bool isReady)
        {
            CSteamID localID = SteamUser.GetSteamID();
            playerReadyStates[localID] = isReady;
            
            CoopMod.Logger.LogInfo($"[ReadySystem] Local player ready state: {isReady}");
            
            // Notify UI
            OnPlayerReadyChanged?.Invoke(localID, isReady);
            
            // Broadcast to all other players
            BroadcastReadyState(isReady);
            
            // Add chat message
            string playerName = SteamFriends.GetPersonaName();
            string statusMessage = isReady 
                ? $"[System] {playerName} is ready!" 
                : $"[System] {playerName} is not ready";
            ChatManager.AddMessage(statusMessage);
            LobbyChatSync.BroadcastChatMessage(statusMessage);
            
            // Check if all players are ready (host only)
            if (SteamLobbyManager.Instance.IsHost)
            {
                CheckAllPlayersReady();
            }
        }
        
        /// <summary>
        /// Broadcast ready state to all players in the lobby
        /// </summary>
        private static void BroadcastReadyState(bool isReady)
        {
            var lobbyManager = SteamLobbyManager.Instance;
            if (!lobbyManager.IsInLobby)
            {
                return;
            }
            
            CSteamID localID = SteamUser.GetSteamID();
            string message = $"{MSG_READY_STATE}{localID.m_SteamID}|{(isReady ? "1" : "0")}";
            
            int memberCount = lobbyManager.GetLobbyMemberCount();
            for (int i = 0; i < memberCount; i++)
            {
                CSteamID memberID = SteamMatchmaking.GetLobbyMemberByIndex(lobbyManager.CurrentLobbyID, i);
                if (memberID != localID)
                {
                    SendReadyP2PMessage(memberID, message);
                }
            }
        }
        
        /// <summary>
        /// Check if all players are ready (host only)
        /// </summary>
        public static bool CheckAllPlayersReady()
        {
            var lobbyManager = SteamLobbyManager.Instance;
            if (!lobbyManager.IsInLobby)
            {
                return false;
            }
            
            int memberCount = lobbyManager.GetLobbyMemberCount();
            
            // Need at least 2 players
            if (memberCount < 2)
            {
                return false;
            }
            
            // Check each player's ready state (including host - they must click Ready too)
            for (int i = 0; i < memberCount; i++)
            {
                CSteamID memberID = SteamMatchmaking.GetLobbyMemberByIndex(lobbyManager.CurrentLobbyID, i);
                
                if (!IsPlayerReady(memberID))
                {
                    CoopMod.Logger.LogInfo($"[ReadySystem] Player {memberID} is not ready");
                    return false;
                }
            }
            
            CoopMod.Logger.LogInfo("[ReadySystem] All players are ready!");
            OnAllPlayersReady?.Invoke();
            return true;
        }
        
        /// <summary>
        /// Get the number of ready players
        /// </summary>
        public static int GetReadyPlayerCount()
        {
            int count = 0;
            var lobbyManager = SteamLobbyManager.Instance;
            
            if (!lobbyManager.IsInLobby)
            {
                CoopMod.Logger.LogInfo("[ReadySystem] GetReadyPlayerCount: Not in lobby, returning 0");
                return 0;
            }
            
            int memberCount = lobbyManager.GetLobbyMemberCount();
            CSteamID myID = SteamUser.GetSteamID();
            bool amHost = lobbyManager.IsHost;
            
            CoopMod.Logger.LogInfo($"[ReadySystem] GetReadyPlayerCount: memberCount={memberCount}, myID={myID}, amHost={amHost}");
            
            for (int i = 0; i < memberCount; i++)
            {
                CSteamID memberID = SteamMatchmaking.GetLobbyMemberByIndex(lobbyManager.CurrentLobbyID, i);
                string memberName = SteamFriends.GetFriendPersonaName(memberID);
                
                // Check if this player is ready (including host - they must click Ready like everyone else)
                bool memberReady = IsPlayerReady(memberID);
                if (memberReady)
                {
                    count++;
                }
                CoopMod.Logger.LogInfo($"[ReadySystem]   [{i}] {memberName} ({memberID}): ready={memberReady}, inDict={playerReadyStates.ContainsKey(memberID)}");
            }
            
            CoopMod.Logger.LogInfo($"[ReadySystem] GetReadyPlayerCount: returning {count}");
            return count;
        }
        
        /// <summary>
        /// Handle incoming ready-related P2P messages
        /// Returns true if message was handled
        /// </summary>
        public static bool HandleReadyMessage(CSteamID senderID, string message)
        {
            if (message.StartsWith(MSG_READY_STATE))
            {
                string payload = message.Substring(MSG_READY_STATE.Length);
                string[] parts = payload.Split('|');
                
                if (parts.Length == 2)
                {
                    ulong playerIDValue = ulong.Parse(parts[0]);
                    bool isReady = parts[1] == "1";
                    CSteamID playerID = new CSteamID(playerIDValue);
                    
                    playerReadyStates[playerID] = isReady;
                    CoopMod.Logger.LogInfo($"[ReadySystem] Player {playerID} ready state: {isReady}");
                    
                    // Notify UI
                    OnPlayerReadyChanged?.Invoke(playerID, isReady);
                    
                    // Check if all ready (host only)
                    if (SteamLobbyManager.Instance.IsHost)
                    {
                        CheckAllPlayersReady();
                    }
                }
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Send a P2P message on the ready channel
        /// </summary>
        private static bool SendReadyP2PMessage(CSteamID recipientID, string message)
        {
            if (!SteamManager.Initialized)
            {
                return false;
            }
            
            byte[] data = Encoding.UTF8.GetBytes(message);
            
            return SteamNetworking.SendP2PPacket(
                recipientID,
                data,
                (uint)data.Length,
                EP2PSend.k_EP2PSendReliable,
                READY_CHANNEL
            );
        }
    }
}

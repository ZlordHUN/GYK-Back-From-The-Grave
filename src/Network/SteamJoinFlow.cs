using System;
using Steamworks;

namespace GraveyardKeeperCoop.Network
{
    /// <summary>
    /// Handles Steam Rich Presence for "Join Game" functionality.
    /// When hosting, sets a "connect" token in Rich Presence.
    /// Friends can then click "Join Game" in Steam overlay to join directly.
    /// </summary>
    public static class SteamJoinFlow
    {
        private static Callback<GameRichPresenceJoinRequested_t> _callbackJoinRequested;
        private static bool _initialized;
        
        /// <summary>
        /// Event fired when someone requests to join via Steam overlay.
        /// Parameters: connect token, friend's Steam ID
        /// </summary>
        public static event Action<string, CSteamID> OnJoinRequested;
        
        /// <summary>
        /// Initialize the join flow system. Call once at startup.
        /// </summary>
        public static void Init()
        {
            if (_initialized) return;
            
            if (!SteamManager.Initialized)
            {
                CoopMod.Logger.LogWarning("[SteamJoinFlow] Steam not initialized, cannot set up join flow");
                return;
            }
            
            _callbackJoinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(OnJoinRequestedCallback);
            _initialized = true;
            
            CoopMod.Logger.LogInfo("[SteamJoinFlow] Initialized");
        }
        
        /// <summary>
        /// Set the connect token for Rich Presence.
        /// Friends will see "Join Game" option and this token will be passed to them.
        /// Typically use "steam:{hostSteamId}" format.
        /// </summary>
        public static void SetConnectToken(string connectToken)
        {
            if (!SteamManager.Initialized) return;
            
            try
            {
                SteamFriends.SetRichPresence("connect", connectToken);
                CoopMod.Logger.LogInfo($"[SteamJoinFlow] Set connect token: {connectToken}");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[SteamJoinFlow] Failed to set connect token: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Set Rich Presence to show we're hosting a game.
        /// Call when starting to host.
        /// </summary>
        public static void SetHosting(CSteamID hostId, bool allowJoinViaPresence = true)
        {
            SetHostPresence(hostId, SteamLobbyManager.LobbyStatusInLobby, allowJoinViaPresence, "hosting");
        }

        /// <summary>
        /// Set host rich presence for an already-running game while keeping the join token available.
        /// </summary>
        public static void SetHostInGame(CSteamID hostId, bool allowJoinViaPresence = true)
        {
            SetHostPresence(hostId, SteamLobbyManager.LobbyStatusInGame, allowJoinViaPresence, "host in-game");
        }

        private static void SetHostPresence(CSteamID hostId, string status, bool allowJoinViaPresence, string logContext)
        {
            if (!SteamManager.Initialized) return;

            try
            {
                SteamFriends.SetRichPresence("status", status);
                SteamFriends.SetRichPresence(
                    SteamLobbyManager.LobbyDataDLC,
                    ModConfig.GetDLCRequirementsString());
                SteamFriends.SetRichPresence(
                    SteamLobbyManager.LobbyDataModVersion,
                    PluginInfo.PLUGIN_VERSION ?? string.Empty);

                // Publish the connect token last so anyone reacting to it can
                // already read the compatibility metadata above.
                if (allowJoinViaPresence)
                {
                    SetConnectToken($"steam:{hostId.m_SteamID}");
                }
                else
                {
                    SteamFriends.SetRichPresence("connect", "");
                    CoopMod.Logger.LogInfo("[SteamJoinFlow] Cleared connect token for private host presence");
                }
                CoopMod.Logger.LogInfo($"[SteamJoinFlow] Set {logContext} presence (join via presence: {allowJoinViaPresence})");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[SteamJoinFlow] Failed to set {logContext} presence: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Set Rich Presence to show we're in a co-op game.
        /// Call when joining as client.
        /// </summary>
        public static void SetInGame(string hostName = null)
        {
            if (!SteamManager.Initialized) return;
            
            try
            {
                string status = string.IsNullOrEmpty(hostName) 
                    ? "Playing Co-op" 
                    : $"Playing Co-op with {hostName}";
                    
                SteamFriends.SetRichPresence("status", status);
                
                // Clear connect token - clients shouldn't be joinable
                SteamFriends.SetRichPresence("connect", "");
                
                CoopMod.Logger.LogInfo($"[SteamJoinFlow] Set in-game presence: {status}");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[SteamJoinFlow] Failed to set in-game presence: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Clear all Rich Presence data.
        /// Call when leaving multiplayer or returning to menu.
        /// </summary>
        public static void ClearPresence()
        {
            try
            {
                if (SteamManager.Initialized)
                {
                    SteamFriends.ClearRichPresence();
                    CoopMod.Logger.LogInfo("[SteamJoinFlow] Cleared presence");
                }
            }
            catch
            {
                // Prevent crash on teardown
            }
        }
        
        /// <summary>
        /// Parse a connect token to extract the host Steam ID.
        /// Returns CSteamID.Nil if parsing fails.
        /// </summary>
        public static CSteamID ParseConnectToken(string token)
        {
            if (string.IsNullOrEmpty(token))
                return CSteamID.Nil;
                
            // Expected format: "steam:12345678901234567"
            if (token.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
            {
                string idStr = token.Substring(6); // Skip "steam:"
                if (ulong.TryParse(idStr, out ulong id))
                {
                    return new CSteamID(id);
                }
            }
            
            return CSteamID.Nil;
        }
        
        /// <summary>
        /// Trigger a join request programmatically (e.g., from Server Browser).
        /// This invokes the OnJoinRequested event as if the user clicked "Join Game" in Steam.
        /// </summary>
        public static void TriggerJoin(string connectToken, CSteamID hostId)
        {
            CoopMod.Logger.LogInfo($"[SteamJoinFlow] Triggering join: token={connectToken}, host={hostId}");
            OnJoinRequested?.Invoke(connectToken, hostId);
        }
        
        private static void OnJoinRequestedCallback(GameRichPresenceJoinRequested_t data)
        {
            string token = data.m_rgchConnect;
            CSteamID friend = data.m_steamIDFriend;
            
            CoopMod.Logger.LogInfo($"[SteamJoinFlow] Join requested: token={token}, friend={friend}");
            
            OnJoinRequested?.Invoke(token, friend);
        }
        
        /// <summary>
        /// Clean up callbacks
        /// </summary>
        public static void Shutdown()
        {
            ClearPresence();
            
            if (_callbackJoinRequested != null)
            {
                _callbackJoinRequested.Dispose();
                _callbackJoinRequested = null;
            }
            
            _initialized = false;
        }
    }
}

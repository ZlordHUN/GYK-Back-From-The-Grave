using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.UI;
using GraveyardKeeperCoop.Utils;

namespace GraveyardKeeperCoop.Network
{
    /// <summary>
    /// Manages Steam lobby functionality including creation, joining, and invitations
    /// </summary>
    public class SteamLobbyManager
    {
        public const string LobbyStatusInLobby = "In Lobby";
        public const string LobbyStatusInGame = "In Game";
        public const string LobbyDataDLC = "dlc";

        private static SteamLobbyManager _instance;
        public static SteamLobbyManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new SteamLobbyManager();
                }
                return _instance;
            }
        }

        // Current lobby state
        private CSteamID currentLobbyID = CSteamID.Nil;
        private bool isInLobby = false;
        private bool isJoining = false; // True between JoinLobby() call and OnLobbyEnter callback
        private bool isHost = false;
        private CSteamID originalHostID = CSteamID.Nil; // Store the original host when joining
        private ModConfig.SessionVisibility currentSessionVisibility = ModConfig.SessionVisibility.Friends;

        // Steam callbacks
        private Callback<LobbyCreated_t> lobbyCreatedCallback;
        private Callback<LobbyEnter_t> lobbyEnterCallback;
        private Callback<GameLobbyJoinRequested_t> joinRequestCallback;
        private Callback<LobbyChatUpdate_t> lobbyChatUpdateCallback;

        // Lobby search
        private CallResult<LobbyMatchList_t> lobbySearchCallResult;
        private bool isSearching = false;
        private int activeSearchId = 0;
        private LobbySearchMode activeSearchMode = LobbySearchMode.Friends;
        public bool IsSearching => isSearching;

        /// <summary>
        /// Fired when a lobby search completes. The list contains all matching lobbies found.
        /// </summary>
        public event Action<LobbySearchMode, List<LobbySearchResult>> OnLobbySearchCompleted;

        /// <summary>
        /// Fires after a successful OnLobbyEnter (i.e. the local player has actually
        /// joined a Steam lobby, whether as host or client). Subscribers can use this
        /// to dismiss any "Joining..." UI and react to a completed join.
        /// </summary>
        public event Action<CSteamID> OnLobbyJoined;

        public CSteamID CurrentLobbyID => currentLobbyID;
        public bool IsInLobby => isInLobby;
        public bool IsJoining => isJoining;
        public bool IsHost => isHost;
        public ModConfig.SessionVisibility CurrentSessionVisibility => currentSessionVisibility;
        public bool AllowsRichPresenceJoin => currentSessionVisibility != ModConfig.SessionVisibility.Private;

        public static bool IsRunningGameStatus(string status)
        {
            if (string.IsNullOrEmpty(status))
                return false;

            return status.IndexOf("in game", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   status.IndexOf("playing", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private SteamLobbyManager()
        {
            CoopMod.Logger.LogInfo("[LOBBY] ========== SteamLobbyManager() CONSTRUCTOR ==========");
            CoopMod.Logger.LogInfo($"[LOBBY] SteamManager.Initialized = {SteamManager.Initialized}");
            
            if (!SteamManager.Initialized)
            {
                CoopMod.Logger.LogError("[LOBBY] Steam is not initialized! Cannot set up lobby manager.");
                return;
            }

            // Register Steam callbacks
            CoopMod.Logger.LogInfo("[LOBBY] Registering Steam callbacks...");
            lobbyCreatedCallback = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            lobbyEnterCallback = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            joinRequestCallback = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequest);
            lobbyChatUpdateCallback = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);

            CoopMod.Logger.LogInfo("[LOBBY] ✓ SteamLobbyManager initialized - callbacks registered");
            CoopMod.Logger.LogInfo($"[LOBBY] Local Steam ID: {SteamUser.GetSteamID()}");
            CoopMod.Logger.LogInfo($"[LOBBY] Local Steam Name: {SteamFriends.GetPersonaName()}");
        }

        /// <summary>
        /// Create a new Steam lobby
        /// </summary>
        /// <param name="maxMembers">Maximum number of players (default 2)</param>
        public void CreateLobby(int maxMembers = 2)
        {
            currentSessionVisibility = ModConfig.HostedSessionVisibility?.Value ?? ModConfig.SessionVisibility.Friends;
            CreateLobby(maxMembers, ToSteamLobbyType(currentSessionVisibility));
        }

        /// <param name="maxMembers">Maximum number of players</param>
        /// <param name="lobbyType">Lobby visibility type</param>
        public void CreateLobby(int maxMembers, ELobbyType lobbyType)
        {
            CoopMod.Logger.LogInfo("[LOBBY] ========== CreateLobby() CALLED ==========");
            CoopMod.Logger.LogInfo($"[LOBBY] SteamManager.Initialized = {SteamManager.Initialized}");
            CoopMod.Logger.LogInfo($"[LOBBY] isInLobby = {isInLobby}");
            CoopMod.Logger.LogInfo($"[LOBBY] currentLobbyID = {currentLobbyID}");
            
            if (!SteamManager.Initialized)
            {
                CoopMod.Logger.LogError("[LOBBY] Cannot create lobby - Steam not initialized!");
                return;
            }

            if (isInLobby)
            {
                CoopMod.Logger.LogWarning("[LOBBY] Already in a lobby! Leave current lobby before creating a new one.");
                return;
            }

            if (!ModConfig.CheckDLCAvailability(out string missingHostDLCs))
            {
                string dlcMessage = $"Cannot create lobby: DLC sync is enabled for DLC(s) you don't have:\n{missingHostDLCs}";
                CoopMod.Logger.LogWarning($"[LOBBY] Refusing to create lobby because enabled DLC is missing locally: {missingHostDLCs}");
                ShowUserDialog(dlcMessage);
                return;
            }

            CoopMod.Logger.LogInfo($"[LOBBY] Creating Steam lobby (max {maxMembers} players, visibility: {currentSessionVisibility}, type: {lobbyType})...");
            CoopMod.Logger.LogInfo($"[LOBBY] Local Steam ID: {SteamUser.GetSteamID()}");
            CoopMod.Logger.LogInfo($"[LOBBY] Local Steam Name: {SteamFriends.GetPersonaName()}");
            SteamMatchmaking.CreateLobby(lobbyType, maxMembers);
            CoopMod.Logger.LogInfo("[LOBBY] CreateLobby call sent to Steam API");
        }

        private static ELobbyType ToSteamLobbyType(ModConfig.SessionVisibility visibility)
        {
            switch (visibility)
            {
                case ModConfig.SessionVisibility.Public:
                    return ELobbyType.k_ELobbyTypePublic;
                case ModConfig.SessionVisibility.Private:
                    return ELobbyType.k_ELobbyTypePrivate;
                case ModConfig.SessionVisibility.Friends:
                default:
                    return ELobbyType.k_ELobbyTypeFriendsOnly;
            }
        }

        /// <summary>
        /// Join an existing Steam lobby
        /// </summary>
        public void JoinLobby(CSteamID lobbyID)
        {
            CoopMod.Logger.LogInfo("[LOBBY] ========== JoinLobby() CALLED ==========");
            CoopMod.Logger.LogInfo($"[LOBBY] Target lobbyID = {lobbyID}");
            CoopMod.Logger.LogInfo($"[LOBBY] SteamManager.Initialized = {SteamManager.Initialized}");
            CoopMod.Logger.LogInfo($"[LOBBY] isInLobby = {isInLobby}, isJoining = {isJoining}");
            
            if (!SteamManager.Initialized)
            {
                CoopMod.Logger.LogError("[LOBBY] Cannot join lobby - Steam not initialized!");
                return;
            }

            if (isInLobby)
            {
                CoopMod.Logger.LogWarning("[LOBBY] Already in a lobby! Leave current lobby before joining another.");
                return;
            }

            if (isJoining)
            {
                CoopMod.Logger.LogWarning("[LOBBY] Already joining a lobby! Ignoring duplicate join request.");
                return;
            }

            if (lobbyID == CSteamID.Nil)
            {
                CoopMod.Logger.LogError("[LOBBY] Invalid lobby ID (CSteamID.Nil)!");
                return;
            }

            // Best-effort preflight for lobby-browser/LAN entries where Steam has
            // already cached lobby metadata. Invite/rich-presence joins may not have
            // data yet, so OnLobbyEnter repeats this check authoritatively.
            string knownDLCRequirements = SteamMatchmaking.GetLobbyData(lobbyID, LobbyDataDLC);
            if (!string.IsNullOrEmpty(knownDLCRequirements) &&
                !TryValidateDLCRequirements(knownDLCRequirements, out string dlcRejectMessage))
            {
                CoopMod.Logger.LogWarning($"[LOBBY] Refusing to join lobby {lobbyID}: {dlcRejectMessage}");
                RejectJoinForDLC(dlcRejectMessage, leaveLobby: false);
                return;
            }

            isJoining = true;
            CoopMod.Logger.LogInfo($"[LOBBY] Joining Steam lobby {lobbyID}...");
            CoopMod.Logger.LogInfo($"[LOBBY] Local Steam ID: {SteamUser.GetSteamID()}");
            SteamMatchmaking.JoinLobby(lobbyID);
            CoopMod.Logger.LogInfo("[LOBBY] JoinLobby call sent to Steam API");
        }

        /// <summary>
        /// Leave the current lobby
        /// </summary>
        public void LeaveLobby()
        {
            if (!isInLobby || currentLobbyID == CSteamID.Nil)
            {
                CoopMod.Logger.LogWarning("Not in a lobby!");
                return;
            }

            CoopMod.Logger.LogInfo($"Leaving Steam lobby {currentLobbyID}...");
            SteamMatchmaking.LeaveLobby(currentLobbyID);
            SteamJoinFlow.ClearPresence();
            LanDiscoveryService.Instance.StopAdvertising();
            SteamP2PManager.Instance.Reliable?.Clear();

            currentLobbyID = CSteamID.Nil;
            isInLobby = false;
            isJoining = false;
            isHost = false;
            originalHostID = CSteamID.Nil;
            currentSessionVisibility = ModConfig.SessionVisibility.Friends;
        }

        /// <summary>
        /// Invite a Steam friend to the current lobby
        /// </summary>
        public bool InviteFriend(CSteamID friendID)
        {
            CoopMod.Logger.LogInfo("[INVITE] ========== InviteFriend() CALLED ==========");
            CoopMod.Logger.LogInfo($"[INVITE] Target friendID = {friendID}");
            CoopMod.Logger.LogInfo($"[INVITE] SteamManager.Initialized = {SteamManager.Initialized}");
            CoopMod.Logger.LogInfo($"[INVITE] isInLobby = {isInLobby}");
            CoopMod.Logger.LogInfo($"[INVITE] currentLobbyID = {currentLobbyID}");
            
            if (!SteamManager.Initialized)
            {
                CoopMod.Logger.LogError("[INVITE] Cannot invite friend - Steam not initialized!");
                return false;
            }

            if (!isInLobby || currentLobbyID == CSteamID.Nil)
            {
                CoopMod.Logger.LogError("[INVITE] Cannot invite friend - not in a lobby!");
                return false;
            }

            if (friendID == CSteamID.Nil)
            {
                CoopMod.Logger.LogError("[INVITE] Invalid friend Steam ID (CSteamID.Nil)!");
                return false;
            }

            string friendName = SteamFriends.GetFriendPersonaName(friendID);
            CoopMod.Logger.LogInfo($"[INVITE] Inviting {friendName} ({friendID}) to lobby {currentLobbyID}...");

            // Send Steam lobby invite (shows in Steam overlay)
            CoopMod.Logger.LogInfo($"[INVITE] Calling SteamMatchmaking.InviteUserToLobby({currentLobbyID}, {friendID})...");
            bool success = SteamMatchmaking.InviteUserToLobby(currentLobbyID, friendID);
            
            if (success)
            {
                CoopMod.Logger.LogInfo($"[INVITE] ✓ Steam lobby invite sent successfully to {friendName}");
                
                // Also send a P2P notification so they get Gerry in-game immediately!
                CoopMod.Logger.LogInfo($"[INVITE] Sending P2P invite notification...");
                bool p2pSent = SteamP2PManager.Instance.SendInviteNotification(friendID, currentLobbyID);
                if (p2pSent)
                {
                    CoopMod.Logger.LogInfo($"Sent P2P invite notification to {friendName}");
                }
                else
                {
                    CoopMod.Logger.LogWarning($"Failed to send P2P invite notification to {friendName}");
                }
            }
            else
            {
                CoopMod.Logger.LogError($"Failed to send invite to {friendName}");
            }

            return success;
        }

        /// <summary>
        /// Set lobby data (key-value pairs)
        /// </summary>
        public bool SetLobbyData(string key, string value)
        {
            if (!isInLobby || currentLobbyID == CSteamID.Nil)
            {
                CoopMod.Logger.LogError("Cannot set lobby data - not in a lobby!");
                return false;
            }

            if (!isHost)
            {
                CoopMod.Logger.LogError("Cannot set lobby data - not the host!");
                return false;
            }

            bool success = SteamMatchmaking.SetLobbyData(currentLobbyID, key, value);
            
            if (success)
            {
                CoopMod.Logger.LogInfo($"Set lobby data: {key} = {value}");
            }
            else
            {
                CoopMod.Logger.LogError($"Failed to set lobby data: {key} = {value}");
            }

            return success;
        }

        /// <summary>
        /// Get lobby data by key
        /// </summary>
        public string GetLobbyData(string key)
        {
            if (!isInLobby || currentLobbyID == CSteamID.Nil)
            {
                CoopMod.Logger.LogError("Cannot get lobby data - not in a lobby!");
                return null;
            }

            return SteamMatchmaking.GetLobbyData(currentLobbyID, key);
        }

        /// <summary>
        /// Get the number of members in the current lobby
        /// </summary>
        public int GetLobbyMemberCount()
        {
            if (!isInLobby || currentLobbyID == CSteamID.Nil)
            {
                return 0;
            }

            return SteamMatchmaking.GetNumLobbyMembers(currentLobbyID);
        }

        /// <summary>
        /// Get lobby owner Steam ID
        /// </summary>
        public CSteamID GetLobbyOwner()
        {
            if (!isInLobby || currentLobbyID == CSteamID.Nil)
            {
                return CSteamID.Nil;
            }

            return SteamMatchmaking.GetLobbyOwner(currentLobbyID);
        }

        /// <summary>
        /// Post a message to the lobby chat if it exists
        /// </summary>
        private void PostChatMessage(string message)
        {
            // Use ChatManager to ensure messages are stored in history for sync
            ChatManager.AddMessage(message);
        }

        public static bool TryValidateDLCRequirements(string hostRequirements, out string rejectionMessage)
        {
            rejectionMessage = "";
            if (ModConfig.HasRequiredDLCs(hostRequirements, out string missingDLCs))
                return true;

            rejectionMessage = $"Cannot join: the host requires DLC(s) you don't have:\n{missingDLCs}";
            return false;
        }

        private static void ShowUserDialog(string message)
        {
            if (GUIElements.me != null && GUIElements.me.dialog != null)
            {
                GUIElements.me.dialog.OpenOK(message);
            }
            else
            {
                CoopMod.Logger.LogWarning($"[LOBBY] Could not show dialog: {message}");
            }
        }

        private void RejectJoinForDLC(string message, bool leaveLobby)
        {
            isJoining = false;

            if (ServerBrowserGUI.Instance != null)
            {
                ServerBrowserGUI.Instance.RejectJoin(message);
                return;
            }

            if (leaveLobby && isInLobby && currentLobbyID != CSteamID.Nil)
            {
                LeaveLobby();
            }

            ShowUserDialog(message);
        }

        // ===== Steam Callback Handlers =====

        private void OnLobbyCreated(LobbyCreated_t callback)
        {
            CoopMod.Logger.LogInfo("[LOBBY] ========== OnLobbyCreated CALLBACK ==========");
            CoopMod.Logger.LogInfo($"[LOBBY] Result: {callback.m_eResult}");
            CoopMod.Logger.LogInfo($"[LOBBY] Lobby ID: {callback.m_ulSteamIDLobby}");
            
            if (callback.m_eResult != EResult.k_EResultOK)
            {
                CoopMod.Logger.LogError($"[LOBBY] ✗ Failed to create lobby! Result: {callback.m_eResult}");
                PostChatMessage($"[System] Failed to create Steam lobby: {callback.m_eResult}");
                return;
            }

            currentLobbyID = new CSteamID(callback.m_ulSteamIDLobby);
            isInLobby = true;
            isHost = true;

            CoopMod.Logger.LogInfo($"[LOBBY] ✓ Successfully created lobby: {currentLobbyID}");
            CoopMod.Logger.LogInfo("[LOBBY] You are the HOST!");
            CoopMod.Logger.LogInfo($"[LOBBY] isInLobby = {isInLobby}, isHost = {isHost}");

            // Set Rich Presence so allowed players can "Join Game" via Steam overlay.
            SteamJoinFlow.SetHosting(SteamUser.GetSteamID(), AllowsRichPresenceJoin);

            // Set some default lobby data
            SetLobbyData("game", "Graveyard Keeper");
            SetLobbyData("mod", PluginInfo.PLUGIN_DISPLAY_NAME);
            SetLobbyData("host_name", SteamFriends.GetPersonaName());
            SetLobbyData("max_players", ModConfig.MaxPlayers.Value.ToString());
            SetLobbyData("visibility", currentSessionVisibility.ToString());
            SetLobbyData("status", LobbyStatusInLobby);
            SetLobbyData(LobbyDataDLC, ModConfig.GetDLCRequirementsString());
            RefreshLanAdvertisement();

            // Notify via chat
            PostChatMessage("[System] Steam lobby created successfully! You can now invite friends.");
        }

        private void OnLobbyEnter(LobbyEnter_t callback)
        {
            CoopMod.Logger.LogInfo("[LOBBY] ========== OnLobbyEnter CALLBACK ==========");
            CoopMod.Logger.LogInfo($"[LOBBY] Lobby ID: {callback.m_ulSteamIDLobby}");
            CoopMod.Logger.LogInfo($"[LOBBY] Chat Room Enter Response: {callback.m_EChatRoomEnterResponse}");
            CoopMod.Logger.LogInfo($"[LOBBY] Permissions: {callback.m_rgfChatPermissions}");
            CoopMod.Logger.LogInfo($"[LOBBY] Locked: {callback.m_bLocked}");

            isJoining = false; // Always clear - the async join attempt is resolved

            // Response 1 = success, anything else = failure
            if (callback.m_EChatRoomEnterResponse != 1)
            {
                CoopMod.Logger.LogError($"[LOBBY] ✗ Failed to enter lobby! Response: {callback.m_EChatRoomEnterResponse}");
                PostChatMessage($"[System] Failed to join lobby (error {callback.m_EChatRoomEnterResponse})");
                return;
            }

            currentLobbyID = new CSteamID(callback.m_ulSteamIDLobby);
            isInLobby = true;

            // Advertise the application-level reliable transport (see ReliableTransport.cs) so other
            // members route their reliable traffic through it; members without this key (older mod
            // versions) are served over native Steam reliable as before.
            SteamMatchmaking.SetLobbyMemberData(currentLobbyID, ReliableTransport.CapabilityKey, ReliableTransport.CapabilityValue);

            // Check if we're the host
            CSteamID owner = GetLobbyOwner();
            CSteamID localPlayer = SteamUser.GetSteamID();
            isHost = (owner == localPlayer);
            originalHostID = owner; // Store for later host-left detection

            int memberCount = GetLobbyMemberCount();
            
            CoopMod.Logger.LogInfo($"[LOBBY] ✓ Entered lobby: {currentLobbyID}");
            CoopMod.Logger.LogInfo($"[LOBBY] Owner Steam ID: {owner}");
            CoopMod.Logger.LogInfo($"[LOBBY] Local Steam ID: {localPlayer}");
            CoopMod.Logger.LogInfo($"[LOBBY] Members in lobby: {memberCount}");
            CoopMod.Logger.LogInfo($"[LOBBY] You are {(isHost ? "the HOST" : "a CLIENT")}");

            // Get lobby info
            string gameName = GetLobbyData("game");
            string modVersion = GetLobbyData("mod");
            string lobbyStatus = GetLobbyData("status");
            if (!string.IsNullOrEmpty(gameName))
            {
                CoopMod.Logger.LogInfo($"Lobby game: {gameName}");
            }
            if (!string.IsNullOrEmpty(modVersion))
            {
                CoopMod.Logger.LogInfo($"Lobby mod: {modVersion}");
            }
            CoopMod.Logger.LogInfo($"Lobby status: {(string.IsNullOrEmpty(lobbyStatus) ? "(empty)" : lobbyStatus)}");

            // If we're a client (not host), we need to transition to the LobbyGUI
            if (!isHost)
            {
                // Validate DLC compatibility
                string hostDLC = GetLobbyData(LobbyDataDLC);
                if (!TryValidateDLCRequirements(hostDLC, out string dlcRejectMessage))
                {
                    CoopMod.Logger.LogWarning($"[LOBBY] DLC compatibility check failed: {dlcRejectMessage}");
                    RejectJoinForDLC(dlcRejectMessage, leaveLobby: true);
                    return;
                }

                // Get the host's name for display
                string hostName = SteamFriends.GetFriendPersonaName(owner);
                CoopMod.Logger.LogInfo($"[LOBBY] Host name: {hostName}");
                
                // Set Rich Presence for Steam overlay "Join Game"
                SteamJoinFlow.SetInGame(hostName);

                if (IsRunningGameStatus(lobbyStatus))
                {
                    CoopMod.Logger.LogInfo("[LOBBY] We are a CLIENT joining an already-running game");
                    BeginRunningGameJoin(owner, hostName);
                }
                else
                {
                    CoopMod.Logger.LogInfo("[LOBBY] We are a CLIENT - transitioning to LobbyGUI...");
                
                    // Create LobbyGUI if it doesn't exist
                    if (LobbyGUI.Instance == null)
                    {
                        CoopMod.Logger.LogInfo("[LOBBY] Creating LobbyGUI...");
                        LobbyGUI.Create();
                    }
                
                    // Open the LobbyGUI as a client
                    if (LobbyGUI.Instance != null)
                    {
                        CoopMod.Logger.LogInfo("[LOBBY] Opening LobbyGUI as client...");
                        LobbyGUI.Instance.OpenAsClient(hostName);
                    }
                    else
                    {
                        CoopMod.Logger.LogError("[LOBBY] Failed to create LobbyGUI!");
                    }
                }
            }

            // Notify subscribers only after the lobby is accepted. This prevents
            // side effects like host-save requests from incompatible DLC clients.
            try { OnLobbyJoined?.Invoke(currentLobbyID); }
            catch (Exception ex) { CoopMod.Logger.LogWarning($"[LOBBY] OnLobbyJoined handler threw: {ex.Message}"); }

            // Notify via chat
            if (isHost)
            {
                PostChatMessage($"[System] You are hosting the lobby ({memberCount} players)");
            }
            else
            {
                PostChatMessage($"[System] Joined lobby! ({memberCount} players)");
            }
        }

        private void BeginRunningGameJoin(CSteamID hostID, string hostName)
        {
            if (LobbyGUI.Instance == null)
            {
                CoopMod.Logger.LogInfo("[LOBBY] Creating LobbyGUI for hidden running-game join flow...");
                LobbyGUI.Create();
            }

            if (LobbyGUI.Instance != null)
            {
                LobbyGUI.Instance.JoinRunningGameFromHost(hostID, hostName);
            }
            else
            {
                CoopMod.Logger.LogError("[LOBBY] Failed to create LobbyGUI for running-game join!");
                PostChatMessage("[System] Failed to join running game: lobby UI unavailable");
            }
        }

        private void OnJoinRequest(GameLobbyJoinRequested_t callback)
        {
            CoopMod.Logger.LogInfo("[INVITE] ========== OnJoinRequest CALLBACK (Steam Overlay) ==========");
            CoopMod.Logger.LogInfo($"[INVITE] Lobby ID: {callback.m_steamIDLobby}");
            CoopMod.Logger.LogInfo($"[INVITE] From Friend: {callback.m_steamIDFriend}");
            
            string friendName = SteamFriends.GetFriendPersonaName(callback.m_steamIDFriend);
            CoopMod.Logger.LogInfo($"[INVITE] Friend Name: {friendName}");
            CoopMod.Logger.LogInfo("[INVITE] This callback means user clicked Steam overlay invite!");
            
            // Show Gerry with the invitation on the Join Game screen
            // Create JoinGameGUI if it doesn't exist
            if (JoinGameGUI.Instance == null)
            {
                CoopMod.Logger.LogInfo("Creating JoinGameGUI for Steam invitation");
                JoinGameGUI.Create();
            }
            
            // If it's already open, just show the invitation
            if (JoinGameGUI.Instance != null && JoinGameGUI.Instance.is_shown)
            {
                CoopMod.Logger.LogInfo("JoinGameGUI already open, showing invitation directly");
                JoinGameGUI.Instance.ShowInvitation(
                    friendName,
                    () => AcceptInvite(callback.m_steamIDLobby, friendName),
                    () => DeclineInvite(friendName)
                );
            }
            else if (JoinGameGUI.Instance != null)
            {
                // Open it first, then show invitation
                CoopMod.Logger.LogInfo("Opening JoinGameGUI to show invitation");
                JoinGameGUI.Instance.Open();
                JoinGameGUI.Instance.ShowInvitation(
                    friendName,
                    () => AcceptInvite(callback.m_steamIDLobby, friendName),
                    () => DeclineInvite(friendName)
                );
            }
        }

        /// <summary>
        /// Called when a lobby member's state changes (join/leave/disconnect)
        /// </summary>
        private void OnLobbyChatUpdate(LobbyChatUpdate_t callback)
        {
            CoopMod.Logger.LogInfo("[LOBBY] ========== OnLobbyChatUpdate CALLBACK ==========");
            
            // Only process updates for our current lobby
            if (callback.m_ulSteamIDLobby != currentLobbyID.m_SteamID)
            {
                CoopMod.Logger.LogInfo("[LOBBY] Update for different lobby, ignoring");
                return;
            }

            CSteamID userChanged = new CSteamID(callback.m_ulSteamIDUserChanged);
            CSteamID userMakingChange = new CSteamID(callback.m_ulSteamIDMakingChange);
            EChatMemberStateChange stateChange = (EChatMemberStateChange)callback.m_rgfChatMemberStateChange;
            
            string userName = SteamFriends.GetFriendPersonaName(userChanged);
            CoopMod.Logger.LogInfo($"[LOBBY] User: {userName} ({userChanged})");
            CoopMod.Logger.LogInfo($"[LOBBY] State change: {stateChange}");

            // Handle different state changes
            if ((stateChange & EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
            {
                CoopMod.Logger.LogInfo($"[LOBBY] {userName} joined the lobby!");
                
                // Update the LobbyGUI if open
                CoopMod.Logger.LogInfo($"[LOBBY] LobbyGUI.Instance: {(LobbyGUI.Instance != null ? "exists" : "null")}");
                if (LobbyGUI.Instance != null)
                {
                    CoopMod.Logger.LogInfo($"[LOBBY] LobbyGUI.is_shown: {LobbyGUI.Instance.is_shown}");
                    CoopMod.Logger.LogInfo($"[LOBBY] LobbyGUI.gameObject.activeSelf: {LobbyGUI.Instance.gameObject.activeSelf}");
                }
                
                if (LobbyGUI.Instance != null && LobbyGUI.Instance.gameObject.activeSelf)
                {
                    CoopMod.Logger.LogInfo($"[LOBBY] Adding player avatar for {userName}...");
                    LobbyGUI.Instance.AddPlayerWithSteamID(userName, userChanged);
                }

                if (isHost)
                {
                    RefreshLanAdvertisement();
                }
                
                PostChatMessage($"[System] {userName} joined the lobby!");
            }
            else if ((stateChange & EChatMemberStateChange.k_EChatMemberStateChangeLeft) != 0 ||
                     (stateChange & EChatMemberStateChange.k_EChatMemberStateChangeDisconnected) != 0 ||
                     (stateChange & EChatMemberStateChange.k_EChatMemberStateChangeKicked) != 0 ||
                     (stateChange & EChatMemberStateChange.k_EChatMemberStateChangeBanned) != 0)
            {
                CoopMod.Logger.LogInfo($"[LOBBY] {userName} left the lobby!");
                
                // Tear down the leaver's reliable channel; if they rejoin, a fresh one starts at seq 0.
                SteamP2PManager.Instance.OnPeerLeftLobby(userChanged);

                // Check if the person who left was the original host
                // We compare against originalHostID (stored when we joined) because
                // GetLobbyOwner may return a new owner after transfer or Nil if lobby closed
                bool hostLeft = (userChanged == originalHostID);
                CoopMod.Logger.LogInfo($"[LOBBY] userChanged={userChanged}, originalHostID={originalHostID}, hostLeft={hostLeft}, isHost={isHost}");
                
                // If we're not the host and the host left, we need to leave too
                if (!isHost && hostLeft)
                {
                    CoopMod.Logger.LogInfo("[LOBBY] Host left the lobby - returning to menu");
                    ChatManager.AddMessage("[System] The host has closed the lobby.");
                    
                    // Force leave and return to menu
                    LeaveLobby();
                    
                    // If game is loaded and online coop is active, use the in-game disconnect flow
                    // (shows dialog, returns to title screen) instead of just toggling LobbyGUI
                    if (MainGame.game_started && OnlineCoopManager.Instance?.IsOnlineCoopEnabled == true)
                    {
                        CoopMod.Logger.LogInfo("[LOBBY] Game is active - triggering in-game disconnect flow");
                        OnlineCoopManager.Instance.HandleHostDisconnectedFromLobby();
                    }
                    else
                    {
                        // Not in-game yet (still in lobby screen) - just return to multiplayer menu
                        if (LobbyGUI.Instance != null)
                        {
                            LobbyGUI.Instance.OnHostDisconnected();
                        }
                    }
                    return;
                }
                
                // Update the LobbyGUI if open - remove the specific player's avatar
                if (LobbyGUI.Instance != null && LobbyGUI.Instance.gameObject.activeSelf)
                {
                    LobbyGUI.Instance.RemovePlayerBySteamID(userChanged, userName);
                }
                
                // Remove from ready system
                GraveyardKeeperCoop.Multiplayer.LobbyReadySystem.RemovePlayer(userChanged);

                if (isHost)
                {
                    RefreshLanAdvertisement();
                }
            }
        }

        /// <summary>
        /// Called when player accepts a Steam invitation
        /// </summary>
        private void AcceptInvite(CSteamID lobbyID, string friendName)
        {
            CoopMod.Logger.LogInfo($"Player accepted invitation from {friendName}");
            PostChatMessage($"[System] Joining {friendName}'s game...");
            JoinLobby(lobbyID);
        }

        /// <summary>
        /// Called when player declines a Steam invitation
        /// </summary>
        private void DeclineInvite(string friendName)
        {
            CoopMod.Logger.LogInfo($"Player declined invitation from {friendName}");
        }

        // ===== Lobby Search =====

        /// <summary>
        /// Search for lobbies hosted by Steam friends.
        /// Requests Graveyard Keeper lobbies, then filters owners to the local friend list.
        /// Results are returned via the OnLobbySearchCompleted event.
        /// </summary>
        public void RequestFriendLobbies()
        {
            RequestLobbies(LobbySearchMode.Friends);
        }

        public void RequestPublicLobbies()
        {
            RequestLobbies(LobbySearchMode.Public);
        }

        public void RequestFavoriteLobbies()
        {
            RequestLobbies(LobbySearchMode.Favorites);
        }

        private void RequestLobbies(LobbySearchMode searchMode)
        {
            if (!SteamManager.Initialized)
            {
                CoopMod.Logger.LogWarning("[LOBBY SEARCH] Steam not initialized");
                OnLobbySearchCompleted?.Invoke(searchMode, new List<LobbySearchResult>());
                return;
            }

            if (isSearching)
            {
                CoopMod.Logger.LogWarning($"[LOBBY SEARCH] Superseding active {activeSearchMode} search with {searchMode} search");
            }

            int searchId = ++activeSearchId;
            isSearching = true;
            activeSearchMode = searchMode;
            bool publicOnly = searchMode == LobbySearchMode.Public;
            CoopMod.Logger.LogInfo($"[LOBBY SEARCH] Starting {searchMode} lobby search...");

            // Filter to our game's lobbies
            SteamMatchmaking.AddRequestLobbyListStringFilter("game", "Graveyard Keeper", ELobbyComparison.k_ELobbyComparisonEqual);
            if (publicOnly)
            {
                SteamMatchmaking.AddRequestLobbyListStringFilter("visibility", ModConfig.SessionVisibility.Public.ToString(), ELobbyComparison.k_ELobbyComparisonEqual);
            }
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            // Only need lobbies with at least 1 open slot
            SteamMatchmaking.AddRequestLobbyListFilterSlotsAvailable(1);
            // Limit results
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(50);

            SteamAPICall_t apiCall = SteamMatchmaking.RequestLobbyList();
            lobbySearchCallResult = CallResult<LobbyMatchList_t>.Create((result, ioFailure) => OnLobbySearchResults(searchMode, searchId, result, ioFailure));
            lobbySearchCallResult.Set(apiCall);

            CoopMod.Logger.LogInfo("[LOBBY SEARCH] RequestLobbyList sent to Steam API");
        }

        /// <summary>
        /// Callback when Steam returns lobby search results.
        /// Applies browser-tab specific filters before publishing results.
        /// </summary>
        private void OnLobbySearchResults(LobbySearchMode searchMode, int searchId, LobbyMatchList_t result, bool ioFailure)
        {
            if (searchId != activeSearchId)
            {
                CoopMod.Logger.LogInfo($"[LOBBY SEARCH] Ignoring stale {searchMode} search result (id {searchId}, active {activeSearchId})");
                return;
            }

            isSearching = false;

            if (ioFailure)
            {
                CoopMod.Logger.LogError($"[LOBBY SEARCH] IO failure during {searchMode} lobby search!");
                OnLobbySearchCompleted?.Invoke(searchMode, new List<LobbySearchResult>());
                return;
            }

            int totalLobbies = (int)result.m_nLobbiesMatching;
            CoopMod.Logger.LogInfo($"[LOBBY SEARCH] Found {totalLobbies} total lobbies matching filters");

            // Build a set of friend Steam IDs for fast lookup when the Friends tab asks.
            var friendIDs = new HashSet<ulong>();
            if (searchMode == LobbySearchMode.Friends)
            {
                int friendCount = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
                for (int i = 0; i < friendCount; i++)
                {
                    CSteamID friendID = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                    friendIDs.Add(friendID.m_SteamID);
                }
            }

            var results = new List<LobbySearchResult>();
            CSteamID localSteamID = SteamUser.GetSteamID();

            for (int i = 0; i < totalLobbies; i++)
            {
                CSteamID lobbyID = SteamMatchmaking.GetLobbyByIndex(i);
                CSteamID owner = SteamMatchmaking.GetLobbyOwner(lobbyID);
                if (owner == CSteamID.Nil)
                {
                    CoopMod.Logger.LogInfo($"[LOBBY SEARCH] Lobby {lobbyID} has no owner, skipping");
                    continue;
                }

                if (owner == localSteamID)
                {
                    CoopMod.Logger.LogInfo($"[LOBBY SEARCH] Lobby {lobbyID} is owned by the local player, skipping");
                    continue;
                }

                // Only include lobbies owned by friends for the Friends tab.
                if (searchMode == LobbySearchMode.Friends && !friendIDs.Contains(owner.m_SteamID))
                {
                    CoopMod.Logger.LogInfo($"[LOBBY SEARCH] Lobby {lobbyID} owner {owner} is not a friend, skipping");
                    continue;
                }

                // Read lobby metadata
                string hostName = SteamMatchmaking.GetLobbyData(lobbyID, "host_name");
                string modVersion = SteamMatchmaking.GetLobbyData(lobbyID, "mod");
                string status = SteamMatchmaking.GetLobbyData(lobbyID, "status");
                string visibility = SteamMatchmaking.GetLobbyData(lobbyID, "visibility");
                string dlcRequirements = SteamMatchmaking.GetLobbyData(lobbyID, LobbyDataDLC);
                int maxPlayers = SteamMatchmaking.GetLobbyMemberLimit(lobbyID);
                int currentPlayers = SteamMatchmaking.GetNumLobbyMembers(lobbyID);
                if (maxPlayers <= 0)
                {
                    string maxPlayersText = SteamMatchmaking.GetLobbyData(lobbyID, "max_players");
                    int parsedMaxPlayers;
                    if (int.TryParse(maxPlayersText, out parsedMaxPlayers))
                        maxPlayers = parsedMaxPlayers;
                }

                if (string.Equals(visibility, ModConfig.SessionVisibility.Private.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    CoopMod.Logger.LogInfo($"[LOBBY SEARCH] Lobby {lobbyID} is private, skipping");
                    continue;
                }

                if (searchMode == LobbySearchMode.Public &&
                    !string.Equals(visibility, ModConfig.SessionVisibility.Public.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    CoopMod.Logger.LogInfo($"[LOBBY SEARCH] Lobby {lobbyID} visibility '{visibility}' is not public, skipping");
                    continue;
                }

                if (maxPlayers > 0 && currentPlayers >= maxPlayers)
                {
                    CoopMod.Logger.LogInfo($"[LOBBY SEARCH] Lobby {lobbyID} is full ({currentPlayers}/{maxPlayers}), skipping");
                    continue;
                }

                // Fallback: use friend persona name if host_name not set
                if (string.IsNullOrEmpty(hostName))
                    hostName = SteamFriends.GetFriendPersonaName(owner);
                if (string.IsNullOrEmpty(hostName))
                    hostName = $"Host {owner.m_SteamID % 10000}";

                if (string.IsNullOrEmpty(status))
                    status = LobbyStatusInLobby;

                var entry = new LobbySearchResult
                {
                    LobbyID = lobbyID,
                    HostSteamID = owner,
                    HostName = hostName,
                    ModVersion = modVersion ?? "",
                    Status = status,
                    Visibility = visibility ?? "",
                    CurrentPlayers = currentPlayers,
                    MaxPlayers = maxPlayers,
                    DLCRequirements = dlcRequirements ?? ""
                };

                results.Add(entry);
                CoopMod.Logger.LogInfo($"[LOBBY SEARCH] Found {searchMode} lobby: {hostName}'s Game ({currentPlayers}/{maxPlayers}) - {status}");
            }

            CoopMod.Logger.LogInfo($"[LOBBY SEARCH] Search complete: {results.Count} {searchMode} lobbies found out of {totalLobbies} total");
            OnLobbySearchCompleted?.Invoke(searchMode, results);
        }

        /// <summary>
        /// Update lobby status metadata (call when game state changes)
        /// </summary>
        public void UpdateLobbyStatus(string status)
        {
            if (isInLobby && isHost)
            {
                SetLobbyData("status", status);
                SetLobbyData(LobbyDataDLC, ModConfig.GetDLCRequirementsString());
                if (IsRunningGameStatus(status))
                {
                    SteamJoinFlow.SetHostInGame(SteamUser.GetSteamID(), AllowsRichPresenceJoin);
                }
                else
                {
                    SteamJoinFlow.SetHosting(SteamUser.GetSteamID(), AllowsRichPresenceJoin);
                }
                RefreshLanAdvertisement();
            }
        }

        public void RefreshLobbyDLCRequirements()
        {
            if (!isInLobby || !isHost)
                return;

            if (!ModConfig.CheckDLCAvailability(out string missingDLCs))
            {
                CoopMod.Logger.LogWarning($"[LOBBY] DLC requirements were changed to include missing local DLC: {missingDLCs}");
                ShowUserDialog($"DLC sync is enabled for DLC(s) you don't have:\n{missingDLCs}");
                return;
            }

            SetLobbyData(LobbyDataDLC, ModConfig.GetDLCRequirementsString());
            if (IsRunningGameStatus(SteamMatchmaking.GetLobbyData(currentLobbyID, "status")))
                SteamJoinFlow.SetHostInGame(SteamUser.GetSteamID(), AllowsRichPresenceJoin);
            else
                SteamJoinFlow.SetHosting(SteamUser.GetSteamID(), AllowsRichPresenceJoin);
            RefreshLanAdvertisement();
        }

        public void MarkGameRunning()
        {
            UpdateLobbyStatus(LobbyStatusInGame);
        }

        private void RefreshLanAdvertisement()
        {
            if (!isInLobby || !isHost || currentLobbyID == CSteamID.Nil ||
                currentSessionVisibility == ModConfig.SessionVisibility.Private)
            {
                LanDiscoveryService.Instance.StopAdvertising();
                return;
            }

            string status = SteamMatchmaking.GetLobbyData(currentLobbyID, "status");
            if (string.IsNullOrEmpty(status))
                status = LobbyStatusInLobby;

            int maxPlayers = SteamMatchmaking.GetLobbyMemberLimit(currentLobbyID);
            if (maxPlayers <= 0)
                maxPlayers = ModConfig.MaxPlayers.Value;

            LanDiscoveryService.Instance.SetAdvertisedSession(new LanServerInfo
            {
                LobbyID = currentLobbyID,
                HostSteamID = SteamUser.GetSteamID(),
                HostName = SteamFriends.GetPersonaName(),
                CurrentPlayers = GetLobbyMemberCount(),
                MaxPlayers = maxPlayers,
                Status = status,
                Visibility = currentSessionVisibility.ToString(),
                ModVersion = PluginInfo.PLUGIN_DISPLAY_VERSION,
                DLCRequirements = ModConfig.GetDLCRequirementsString()
            });
            LanDiscoveryService.Instance.StartAdvertising();
        }
    }

    /// <summary>
    /// Kind of Steam lobby search requested by the server browser.
    /// </summary>
    public enum LobbySearchMode
    {
        Friends,
        Public,
        Favorites
    }

    /// <summary>
    /// Result from a Steam lobby search
    /// </summary>
    public class LobbySearchResult
    {
        public CSteamID LobbyID { get; set; }
        public CSteamID HostSteamID { get; set; }
        public string HostName { get; set; }
        public string ModVersion { get; set; }
        public string Status { get; set; }
        public string Visibility { get; set; }
        public int CurrentPlayers { get; set; }
        public int MaxPlayers { get; set; }
        public string DLCRequirements { get; set; }
    }
}

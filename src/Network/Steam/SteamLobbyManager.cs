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
        public const string LobbyDataModVersion = "mod_version";
        public const string LobbyDataProtocolRevision = "protocol_revision";
        private const float MemberCompatibilityValidationTimeout = 3f;
        private const float InviteCompatibilityLookupTimeout = 3f;

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
        private CSteamID reliableTransportLobbyID = CSteamID.Nil;
        private readonly Dictionary<ulong, float> pendingMemberCompatibilityChecks =
            new Dictionary<ulong, float>();
        private readonly HashSet<ulong> validatedMemberCompatibility =
            new HashSet<ulong>();
        private CSteamID pendingInviteLobbyID = CSteamID.Nil;
        private string pendingInviteFriendName;
        private float pendingInviteCompatibilityDeadline;

        // Steam callbacks
        private Callback<LobbyCreated_t> lobbyCreatedCallback;
        private Callback<LobbyEnter_t> lobbyEnterCallback;
        private Callback<GameLobbyJoinRequested_t> joinRequestCallback;
        private Callback<LobbyChatUpdate_t> lobbyChatUpdateCallback;
        private Callback<LobbyDataUpdate_t> lobbyDataUpdateCallback;

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
            lobbyDataUpdateCallback = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdate);

            CoopMod.Logger.LogInfo("[LOBBY] ✓ SteamLobbyManager initialized - callbacks registered");
            CoopMod.Logger.LogInfo($"[LOBBY] Local Steam ID: {SteamUser.GetSteamID()}");
            CoopMod.Logger.LogInfo($"[LOBBY] Local Steam Name: {SteamFriends.GetPersonaName()}");
        }

        /// <summary>
        /// Create a new Steam lobby
        /// </summary>
        /// <param name="maxMembers">Maximum number of players (2-4)</param>
        public void CreateLobby(int maxMembers = 4)
        {
            currentSessionVisibility = ModConfig.HostedSessionVisibility?.Value ?? ModConfig.SessionVisibility.Friends;
            CreateLobby(maxMembers, ToSteamLobbyType(currentSessionVisibility));
        }

        /// <param name="maxMembers">Maximum number of players</param>
        /// <param name="lobbyType">Lobby visibility type</param>
        public void CreateLobby(int maxMembers, ELobbyType lobbyType)
        {
            maxMembers = Mathf.Clamp(maxMembers, 2, 4);
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
                    // Steam excludes k_ELobbyTypeFriendsOnly from RequestLobbyList.
                    // Use a searchable backend lobby and enforce the Friends policy
                    // through lobby metadata, browser filtering, and host admission.
                    return ELobbyType.k_ELobbyTypePublic;
                default:
                    return ELobbyType.k_ELobbyTypePublic;
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
            string knownHostVersion =
                SteamMatchmaking.GetLobbyData(lobbyID, LobbyDataModVersion);
            if (!string.IsNullOrEmpty(knownHostVersion) &&
                !TryValidateHostModVersion(
                    knownHostVersion,
                    out string versionRejectMessage))
            {
                CoopMod.Logger.LogWarning(
                    $"[LOBBY] Refusing to join lobby {lobbyID}: " +
                    versionRejectMessage.Replace('\n', ' '));
                RejectJoinAttempt(versionRejectMessage, leaveLobby: false);
                return;
            }

            string knownHostProtocol =
                SteamMatchmaking.GetLobbyData(lobbyID, LobbyDataProtocolRevision);
            if (!string.IsNullOrEmpty(knownHostProtocol) &&
                !TryValidateHostProtocolRevision(
                    knownHostProtocol,
                    out string protocolRejectMessage))
            {
                CoopMod.Logger.LogWarning(
                    $"[LOBBY] Refusing to join lobby {lobbyID}: " +
                    protocolRejectMessage.Replace('\n', ' '));
                RejectJoinAttempt(protocolRejectMessage, leaveLobby: false);
                return;
            }

            string knownDLCRequirements = SteamMatchmaking.GetLobbyData(lobbyID, LobbyDataDLC);
            if (!string.IsNullOrEmpty(knownDLCRequirements) &&
                !TryValidateDLCRequirements(knownDLCRequirements, out string dlcRejectMessage))
            {
                CoopMod.Logger.LogWarning($"[LOBBY] Refusing to join lobby {lobbyID}: {dlcRejectMessage}");
                RejectJoinAttempt(dlcRejectMessage, leaveLobby: false);
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
        public void LeaveLobby(string reason = "local_leave")
        {
            if (!isInLobby || currentLobbyID == CSteamID.Nil)
            {
                CoopMod.Logger.LogWarning("Not in a lobby!");
                return;
            }

            CoopMod.Logger.LogInfo($"Leaving Steam lobby {currentLobbyID}...");
            SessionLogContext.EndLobby(reason);
            SteamP2PManager.Instance.ClearLobbySession();
            SteamMatchmaking.LeaveLobby(currentLobbyID);
            SteamJoinFlow.ClearPresence();
            LanDiscoveryService.Instance.StopAdvertising();
            ChatOverlay.Instance?.EndSession();

            currentLobbyID = CSteamID.Nil;
            isInLobby = false;
            isJoining = false;
            isHost = false;
            originalHostID = CSteamID.Nil;
            reliableTransportLobbyID = CSteamID.Nil;
            currentSessionVisibility = ModConfig.SessionVisibility.Friends;
            pendingMemberCompatibilityChecks.Clear();
            validatedMemberCompatibility.Clear();
            ClearPendingInviteCompatibilityCheck();
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

        public static bool TryValidateHostModVersion(
            string hostVersion,
            out string rejectionMessage)
        {
            string localVersion = PluginInfo.PLUGIN_VERSION ?? string.Empty;
            string normalizedHostVersion = (hostVersion ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(normalizedHostVersion) &&
                string.Equals(
                    normalizedHostVersion,
                    localVersion,
                    StringComparison.Ordinal))
            {
                rejectionMessage = string.Empty;
                return true;
            }

            string displayedHostVersion = string.IsNullOrEmpty(normalizedHostVersion)
                ? "unavailable (outdated mod)"
                : normalizedHostVersion;
            rejectionMessage =
                "Cannot join: mod version mismatch.\n" +
                $"Your version: {localVersion}\n" +
                $"Host version: {displayedHostVersion}";
            return false;
        }

        public static bool TryValidateHostProtocolRevision(
            string hostProtocolRevision,
            out string rejectionMessage)
        {
            string normalizedHostProtocol =
                (hostProtocolRevision ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(normalizedHostProtocol) &&
                string.Equals(
                    normalizedHostProtocol,
                    CoopProtocol.Revision,
                    StringComparison.Ordinal))
            {
                rejectionMessage = string.Empty;
                return true;
            }

            string displayedHostProtocol =
                string.IsNullOrEmpty(normalizedHostProtocol)
                    ? "unavailable (outdated mod)"
                    : normalizedHostProtocol;
            rejectionMessage =
                "Cannot join: multiplayer protocol mismatch.\n" +
                $"Your protocol: {CoopProtocol.Revision}\n" +
                $"Host protocol: {displayedHostProtocol}";
            return false;
        }

        public static bool TryValidateHostCompatibility(
            string hostVersion,
            string hostProtocolRevision,
            out string rejectionMessage)
        {
            if (!TryValidateHostModVersion(hostVersion, out rejectionMessage))
                return false;

            return TryValidateHostProtocolRevision(
                hostProtocolRevision,
                out rejectionMessage);
        }

        public static string BuildInviteCompatibilityNotice(
            string inviterName,
            string hostVersion,
            string hostProtocolRevision)
        {
            string localVersion = PluginInfo.PLUGIN_VERSION ?? string.Empty;
            string normalizedHostVersion = (hostVersion ?? string.Empty).Trim();
            string invitation =
                $"{inviterName} has invited you to their graveyard, but ";

            if (string.IsNullOrEmpty(normalizedHostVersion))
            {
                return invitation +
                    "their mod is outdated and does not report a compatible version. " +
                    $"Please ask the host to update to {localVersion}.";
            }

            if (!string.Equals(
                    normalizedHostVersion,
                    localVersion,
                    StringComparison.Ordinal))
            {
                if (TryCompareSemanticVersions(
                        normalizedHostVersion,
                        localVersion,
                        out int comparison))
                {
                    if (comparison < 0)
                    {
                        return invitation +
                            "their mod is outdated. " +
                            $"Please downgrade to version {normalizedHostVersion} " +
                            $"or ask the host to update to {localVersion}.";
                    }

                    if (comparison > 0)
                    {
                        return invitation +
                            "your mod is outdated. " +
                            $"Please update to {normalizedHostVersion} or have the host " +
                            $"downgrade to {localVersion}.";
                    }
                }

                return invitation +
                    "your mod versions do not match. " +
                    $"Please use {normalizedHostVersion} or ask the host to use {localVersion}.";
            }

            string normalizedHostProtocol =
                (hostProtocolRevision ?? string.Empty).Trim();
            return invitation +
                "your multiplayer protocols are incompatible. " +
                $"Please reinstall matching builds of version {localVersion}. " +
                $"Your protocol is {CoopProtocol.Revision}; the host protocol is " +
                $"{(string.IsNullOrEmpty(normalizedHostProtocol) ? "unavailable" : normalizedHostProtocol)}.";
        }

        private static bool TryCompareSemanticVersions(
            string left,
            string right,
            out int comparison)
        {
            comparison = 0;
            if (!TrySplitSemanticVersion(left, out System.Version leftCore, out string leftPre) ||
                !TrySplitSemanticVersion(right, out System.Version rightCore, out string rightPre))
            {
                return false;
            }

            comparison = leftCore.CompareTo(rightCore);
            if (comparison != 0)
                return true;

            bool leftRelease = string.IsNullOrEmpty(leftPre);
            bool rightRelease = string.IsNullOrEmpty(rightPre);
            if (leftRelease || rightRelease)
            {
                comparison = leftRelease == rightRelease ? 0 : (leftRelease ? 1 : -1);
                return true;
            }

            string[] leftParts = leftPre.Split('.');
            string[] rightParts = rightPre.Split('.');
            int count = Math.Min(leftParts.Length, rightParts.Length);
            for (int i = 0; i < count; i++)
            {
                bool leftNumeric = int.TryParse(leftParts[i], out int leftNumber);
                bool rightNumeric = int.TryParse(rightParts[i], out int rightNumber);
                if (leftNumeric && rightNumeric)
                    comparison = leftNumber.CompareTo(rightNumber);
                else if (leftNumeric != rightNumeric)
                    comparison = leftNumeric ? -1 : 1;
                else
                    comparison = string.CompareOrdinal(leftParts[i], rightParts[i]);

                if (comparison != 0)
                    return true;
            }

            comparison = leftParts.Length.CompareTo(rightParts.Length);
            return true;
        }

        private static bool TrySplitSemanticVersion(
            string value,
            out System.Version core,
            out string prerelease)
        {
            core = null;
            prerelease = string.Empty;
            string normalized = (value ?? string.Empty).Trim();
            int buildMetadata = normalized.IndexOf('+');
            if (buildMetadata >= 0)
                normalized = normalized.Substring(0, buildMetadata);

            int prereleaseSeparator = normalized.IndexOf('-');
            if (prereleaseSeparator >= 0)
            {
                prerelease = normalized.Substring(prereleaseSeparator + 1);
                normalized = normalized.Substring(0, prereleaseSeparator);
            }

            return System.Version.TryParse(normalized, out core);
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

        private void RejectJoinAttempt(string message, bool leaveLobby)
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

        private static bool IsImmediateSteamFriend(CSteamID steamID)
        {
            if (!SteamManager.Initialized || steamID == CSteamID.Nil)
                return false;

            int friendCount = SteamFriends.GetFriendCount(
                EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < friendCount; i++)
            {
                if (SteamFriends.GetFriendByIndex(
                        i,
                        EFriendFlags.k_EFriendFlagImmediate) == steamID)
                {
                    return true;
                }
            }

            return false;
        }

        // ===== Steam Callback Handlers =====

        private void InitializeReliableTransport(CSteamID lobbyID)
        {
            if (lobbyID == CSteamID.Nil)
                return;

            SteamP2PManager p2p = SteamP2PManager.Instance;
            if (p2p?.Reliable == null)
                return;

            if (reliableTransportLobbyID != lobbyID)
            {
                p2p.BeginLobbySession(lobbyID);
                reliableTransportLobbyID = lobbyID;
            }

            // Publish the session nonce before capability so peers never observe v2 support
            // without the nonce required to authenticate its handshake.
            SteamMatchmaking.SetLobbyMemberData(
                lobbyID,
                LobbyDataModVersion,
                PluginInfo.PLUGIN_VERSION ?? string.Empty);
            SteamMatchmaking.SetLobbyMemberData(
                lobbyID,
                LobbyDataProtocolRevision,
                CoopProtocol.Revision);
            SteamMatchmaking.SetLobbyMemberData(
                lobbyID,
                ReliableTransport.LegacyCapabilityKey,
                string.Empty);
            SteamMatchmaking.SetLobbyMemberData(
                lobbyID,
                ReliableTransport.NonceKey,
                p2p.Reliable.LocalNonceValue);
            SteamMatchmaking.SetLobbyMemberData(
                lobbyID,
                ReliableTransport.CapabilityKey,
                ReliableTransport.CapabilityValue);
            SteamMatchmaking.SetLobbyMemberData(
                lobbyID,
                Multiplayer.SpawnSync.BuildingPreviewCapabilityKey,
                Multiplayer.SpawnSync.BuildingPreviewCapabilityValue);
            SteamMatchmaking.SetLobbyMemberData(
                lobbyID,
                Multiplayer.InventorySync.PersonalUseCapabilityKey,
                Multiplayer.InventorySync.PersonalUseCapabilityValue);

            CSteamID localID = SteamUser.GetSteamID();
            int memberCount = SteamMatchmaking.GetNumLobbyMembers(lobbyID);
            for (int i = 0; i < memberCount; i++)
            {
                CSteamID memberID = SteamMatchmaking.GetLobbyMemberByIndex(lobbyID, i);
                if (memberID != localID)
                    p2p.OnPeerEnteredLobby(memberID);
            }
        }

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
            pendingMemberCompatibilityChecks.Clear();
            validatedMemberCompatibility.Clear();

            InitializeReliableTransport(currentLobbyID);

            CoopMod.Logger.LogInfo($"[LOBBY] ✓ Successfully created lobby: {currentLobbyID}");
            CoopMod.Logger.LogInfo("[LOBBY] You are the HOST!");
            CoopMod.Logger.LogInfo($"[LOBBY] isInLobby = {isInLobby}, isHost = {isHost}");

            // Set some default lobby data
            SetLobbyData("game", "Graveyard Keeper");
            SetLobbyData("mod", PluginInfo.PLUGIN_DISPLAY_NAME);
            SetLobbyData(LobbyDataModVersion, PluginInfo.PLUGIN_VERSION);
            SetLobbyData(LobbyDataProtocolRevision, CoopProtocol.Revision);
            SetLobbyData("host_name", SteamFriends.GetPersonaName());
            SetLobbyData("max_players", ModConfig.MaxPlayers.Value.ToString());
            SetLobbyData("visibility", currentSessionVisibility.ToString());
            SetLobbyData("status", LobbyStatusInLobby);
            SetLobbyData(LobbyDataDLC, ModConfig.GetDLCRequirementsString());
            SessionLogContext.BeginHostLobby(currentLobbyID);
            RefreshLanAdvertisement();

            // Publish joinable presence only after compatibility metadata exists.
            SteamJoinFlow.SetHosting(SteamUser.GetSteamID(), AllowsRichPresenceJoin);

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

            // Check if we're the host
            CSteamID owner = GetLobbyOwner();
            CSteamID localPlayer = SteamUser.GetSteamID();
            isHost = (owner == localPlayer);
            originalHostID = owner; // Store for later host-left detection
            pendingMemberCompatibilityChecks.Clear();
            validatedMemberCompatibility.Clear();

            string declaredVisibility = GetLobbyData("visibility");
            if (!isHost &&
                string.Equals(
                    declaredVisibility,
                    ModConfig.SessionVisibility.Friends.ToString(),
                    StringComparison.OrdinalIgnoreCase) &&
                !IsImmediateSteamFriend(owner))
            {
                const string rejection =
                    "Cannot join: this lobby is restricted to the host's Steam friends.";
                CoopMod.Logger.LogWarning(
                    $"[LOBBY] Rejecting Friends lobby {currentLobbyID}: " +
                    $"owner {owner} is not an immediate Steam friend");
                RejectJoinAttempt(rejection, leaveLobby: true);
                return;
            }

            if (!isHost)
            {
                string hostVersion = GetLobbyData(LobbyDataModVersion);
                string hostProtocol = GetLobbyData(LobbyDataProtocolRevision);
                if (!TryValidateHostCompatibility(
                        hostVersion,
                        hostProtocol,
                        out string compatibilityRejectMessage))
                {
                    CoopMod.Logger.LogWarning(
                        $"[LOBBY] Compatibility check failed for lobby " +
                        $"{currentLobbyID}: " +
                        compatibilityRejectMessage.Replace('\n', ' '));
                    RejectJoinAttempt(compatibilityRejectMessage, leaveLobby: true);
                    return;
                }

            }

            InitializeReliableTransport(currentLobbyID);

            if (!isHost)
            {
                // Confirm product and protocol compatibility over Steam P2P before any subscriber
                // can send save-list, save-transfer, or gameplay traffic. The
                // host also checks member data as a fallback if this packet is lost.
                SteamP2PManager.Instance.SendCompatibilityHello(owner);
            }

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
                    RejectJoinAttempt(dlcRejectMessage, leaveLobby: true);
                    return;
                }

                SessionLogContext.BeginJoinedLobby(currentLobbyID, owner);

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
            else
            {
                SessionLogContext.BeginJoinedLobby(currentLobbyID, owner);
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

            pendingInviteLobbyID = callback.m_steamIDLobby;
            pendingInviteFriendName = friendName;
            pendingInviteCompatibilityDeadline =
                Time.realtimeSinceStartup + InviteCompatibilityLookupTimeout;

            // Cached metadata may already be sufficient. Otherwise ask Steam for
            // the lobby data and defer Gerry until compatibility is known.
            if (TryPresentPendingInvite(allowMissingMetadata: false))
                return;

            if (!SteamMatchmaking.RequestLobbyData(callback.m_steamIDLobby))
            {
                CoopMod.Logger.LogWarning(
                    "[INVITE] Steam rejected the compatibility metadata request");
                TryPresentPendingInvite(allowMissingMetadata: true);
            }
        }

        private bool TryPresentPendingInvite(bool allowMissingMetadata)
        {
            if (pendingInviteLobbyID == CSteamID.Nil)
                return false;

            string hostVersion = SteamMatchmaking.GetLobbyData(
                pendingInviteLobbyID,
                LobbyDataModVersion);
            string hostProtocol = SteamMatchmaking.GetLobbyData(
                pendingInviteLobbyID,
                LobbyDataProtocolRevision);
            string localVersion = PluginInfo.PLUGIN_VERSION ?? string.Empty;
            bool productKnown = !string.IsNullOrEmpty(hostVersion);
            bool productMatches = productKnown && string.Equals(
                hostVersion.Trim(),
                localVersion,
                StringComparison.Ordinal);

            // A known product mismatch is enough to reject immediately. Matching
            // products still need the independent protocol field before offering Join.
            if (!allowMissingMetadata &&
                (!productKnown ||
                 (productMatches && string.IsNullOrEmpty(hostProtocol))))
            {
                return false;
            }

            CSteamID lobbyID = pendingInviteLobbyID;
            string friendName = pendingInviteFriendName;
            ClearPendingInviteCompatibilityCheck();

            if (JoinGameGUI.Instance == null)
            {
                CoopMod.Logger.LogInfo("Creating JoinGameGUI for Steam invitation");
                JoinGameGUI.Create();
            }

            if (JoinGameGUI.Instance == null)
                return true;

            if (!JoinGameGUI.Instance.is_shown)
                JoinGameGUI.Instance.Open();

            if (!TryValidateHostCompatibility(
                    hostVersion,
                    hostProtocol,
                    out string compatibilityRejectMessage))
            {
                CoopMod.Logger.LogWarning(
                    $"[INVITE] Gerry rejected incompatible overlay invite: " +
                    compatibilityRejectMessage.Replace('\n', ' '));
                JoinGameGUI.Instance.ShowIncompatibleInvitation(
                    friendName,
                    BuildInviteCompatibilityNotice(
                        friendName,
                        hostVersion,
                        hostProtocol));
                return true;
            }

            JoinGameGUI.Instance.ShowInvitation(
                friendName,
                () => AcceptInvite(lobbyID, friendName),
                () => DeclineInvite(friendName));
            return true;
        }

        private void ClearPendingInviteCompatibilityCheck()
        {
            pendingInviteLobbyID = CSteamID.Nil;
            pendingInviteFriendName = null;
            pendingInviteCompatibilityDeadline = 0f;
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
                if (isHost &&
                    userChanged != SteamUser.GetSteamID() &&
                    currentSessionVisibility == ModConfig.SessionVisibility.Friends &&
                    !IsImmediateSteamFriend(userChanged))
                {
                    const string rejection =
                        "This lobby is restricted to the host's Steam friends.";
                    CoopMod.Logger.LogWarning(
                        $"[LOBBY] Rejecting non-friend {userName} ({userChanged}) " +
                        "from Friends session");
                    bool sent = SteamP2PManager.Instance.SendLobbyKick(
                        userChanged,
                        rejection);
                    CoopMod.Logger.LogInfo(
                        $"[LOBBY] Friends-only rejection sent to {userChanged}: {sent}");
                    PostChatMessage(
                        $"[System] Rejected {userName}: Friends-only session.");
                    return;
                }

                if (isHost && userChanged != SteamUser.GetSteamID())
                {
                    ValidateOrQueueLobbyMember(userChanged, userName);
                    return;
                }

                AdmitLobbyMember(userChanged, userName);
            }
            else if ((stateChange & EChatMemberStateChange.k_EChatMemberStateChangeLeft) != 0 ||
                     (stateChange & EChatMemberStateChange.k_EChatMemberStateChangeDisconnected) != 0 ||
                     (stateChange & EChatMemberStateChange.k_EChatMemberStateChangeKicked) != 0 ||
                     (stateChange & EChatMemberStateChange.k_EChatMemberStateChangeBanned) != 0)
            {
                pendingMemberCompatibilityChecks.Remove(userChanged.m_SteamID);
                validatedMemberCompatibility.Remove(userChanged.m_SteamID);
                CoopMod.Logger.LogInfo($"[LOBBY] {userName} left the lobby!");
                SessionLogContext.PeerLeft(userChanged, stateChange.ToString());
                SteamP2PManager.Instance.OnPeerLeftLobby(userChanged);
                OnlineCoopManager.Instance?.HandlePeerLeftLobby(userChanged, userName);
                GraveyardKeeperCoop.Multiplayer.GameTimeSync.Instance
                    ?.NotifySleepPeerLeft(userChanged);
                
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
                    LeaveLobby("original_host_left");
                    
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

        private void OnLobbyDataUpdate(LobbyDataUpdate_t callback)
        {
            if (pendingInviteLobbyID != CSteamID.Nil &&
                callback.m_ulSteamIDLobby == pendingInviteLobbyID.m_SteamID)
            {
                TryPresentPendingInvite(allowMissingMetadata: false);
                if (callback.m_ulSteamIDLobby != currentLobbyID.m_SteamID)
                    return;
            }

            if (callback.m_ulSteamIDLobby != currentLobbyID.m_SteamID)
                return;

            SessionLogContext.RefreshFromLobby(currentLobbyID);

            CSteamID member = new CSteamID(callback.m_ulSteamIDMember);
            if (!pendingMemberCompatibilityChecks.ContainsKey(member.m_SteamID))
                return;

            string memberName = SteamFriends.GetFriendPersonaName(member);
            ValidateOrQueueLobbyMember(member, memberName, preserveDeadline: true);
        }

        public void Update()
        {
            if (pendingInviteLobbyID != CSteamID.Nil &&
                Time.realtimeSinceStartup >= pendingInviteCompatibilityDeadline)
            {
                CoopMod.Logger.LogWarning(
                    "[INVITE] Compatibility metadata lookup timed out; treating the invite as incompatible");
                TryPresentPendingInvite(allowMissingMetadata: true);
            }

            if (!isHost || !isInLobby || pendingMemberCompatibilityChecks.Count == 0)
                return;

            float now = Time.realtimeSinceStartup;
            var members = new List<ulong>(pendingMemberCompatibilityChecks.Keys);
            for (int i = 0; i < members.Count; i++)
            {
                ulong memberValue = members[i];
                if (!pendingMemberCompatibilityChecks.TryGetValue(
                        memberValue,
                        out float deadline))
                {
                    continue;
                }

                CSteamID member = new CSteamID(memberValue);
                string memberName = SteamFriends.GetFriendPersonaName(member);
                string memberVersion = SteamMatchmaking.GetLobbyMemberData(
                    currentLobbyID,
                    member,
                    LobbyDataModVersion);
                string memberProtocol = SteamMatchmaking.GetLobbyMemberData(
                    currentLobbyID,
                    member,
                    LobbyDataProtocolRevision);
                if (!string.IsNullOrEmpty(memberVersion) &&
                    !string.IsNullOrEmpty(memberProtocol))
                {
                    CompleteLobbyMemberCompatibilityValidation(
                        member,
                        memberName,
                        memberVersion,
                        memberProtocol);
                }
                else if (now >= deadline)
                {
                    RejectLobbyMemberCompatibility(
                        member,
                        memberName,
                        memberVersion,
                        memberProtocol);
                }
            }
        }

        private void ValidateOrQueueLobbyMember(
            CSteamID member,
            string memberName,
            bool preserveDeadline = false)
        {
            string memberVersion = SteamMatchmaking.GetLobbyMemberData(
                currentLobbyID,
                member,
                LobbyDataModVersion);
            string memberProtocol = SteamMatchmaking.GetLobbyMemberData(
                currentLobbyID,
                member,
                LobbyDataProtocolRevision);
            if (!string.IsNullOrEmpty(memberVersion) &&
                !string.IsNullOrEmpty(memberProtocol))
            {
                CompleteLobbyMemberCompatibilityValidation(
                    member,
                    memberName,
                    memberVersion,
                    memberProtocol);
                return;
            }

            if (!preserveDeadline ||
                !pendingMemberCompatibilityChecks.ContainsKey(member.m_SteamID))
            {
                pendingMemberCompatibilityChecks[member.m_SteamID] =
                    Time.realtimeSinceStartup + MemberCompatibilityValidationTimeout;
            }
            CoopMod.Logger.LogInfo(
                $"[LOBBY] Waiting for compatibility metadata from {memberName} ({member})");
        }

        private void CompleteLobbyMemberCompatibilityValidation(
            CSteamID member,
            string memberName,
            string memberVersion,
            string memberProtocol)
        {
            pendingMemberCompatibilityChecks.Remove(member.m_SteamID);
            if (validatedMemberCompatibility.Contains(member.m_SteamID))
                return;

            string localVersion = PluginInfo.PLUGIN_VERSION ?? string.Empty;
            if (!string.Equals(
                    (memberVersion ?? string.Empty).Trim(),
                    localVersion,
                    StringComparison.Ordinal))
            {
                RejectLobbyMemberCompatibility(
                    member,
                    memberName,
                    memberVersion,
                    memberProtocol);
                return;
            }

            if (!string.Equals(
                    (memberProtocol ?? string.Empty).Trim(),
                    CoopProtocol.Revision,
                    StringComparison.Ordinal))
            {
                RejectLobbyMemberCompatibility(
                    member,
                    memberName,
                    memberVersion,
                    memberProtocol);
                return;
            }

            validatedMemberCompatibility.Add(member.m_SteamID);
            CoopMod.Logger.LogInfo(
                $"[LOBBY] Accepted {memberName} ({member}) with mod version " +
                $"{memberVersion} and protocol {memberProtocol}");
            AdmitLobbyMember(member, memberName);
        }

        internal void HandleCompatibilityHello(
            CSteamID member,
            string memberVersion,
            string memberProtocol)
        {
            if (!isHost || !isInLobby || member == CSteamID.Nil)
                return;

            CompleteLobbyMemberCompatibilityValidation(
                member,
                SteamFriends.GetFriendPersonaName(member),
                memberVersion,
                memberProtocol);
        }

        private void RejectLobbyMemberCompatibility(
            CSteamID member,
            string memberName,
            string memberVersion,
            string memberProtocol)
        {
            pendingMemberCompatibilityChecks.Remove(member.m_SteamID);
            string localVersion = PluginInfo.PLUGIN_VERSION ?? string.Empty;
            string displayedMemberVersion = string.IsNullOrEmpty(memberVersion)
                ? "unavailable (outdated mod)"
                : memberVersion.Trim();
            string displayedMemberProtocol = string.IsNullOrEmpty(memberProtocol)
                ? "unavailable (outdated mod)"
                : memberProtocol.Trim();
            bool productMismatch = !string.Equals(
                displayedMemberVersion,
                localVersion,
                StringComparison.Ordinal);
            string rejection = productMismatch
                ? "Cannot join: mod version mismatch.\n" +
                  $"Host version: {localVersion}\n" +
                  $"Your version: {displayedMemberVersion}"
                : "Cannot join: multiplayer protocol mismatch.\n" +
                  $"Host protocol: {CoopProtocol.Revision}\n" +
                  $"Your protocol: {displayedMemberProtocol}";
            bool sent = SteamP2PManager.Instance?.SendLobbyKick(
                member,
                rejection) == true;
            CoopMod.Logger.LogWarning(
                $"[LOBBY] Rejected {memberName} ({member}) with mod version " +
                $"'{displayedMemberVersion}' and protocol " +
                $"'{displayedMemberProtocol}'; kick_sent={sent}");
            PostChatMessage(
                $"[System] Rejected {memberName}: multiplayer compatibility mismatch.");
        }

        private void AdmitLobbyMember(CSteamID member, string memberName)
        {
            CoopMod.Logger.LogInfo($"[LOBBY] {memberName} joined the lobby!");
            SessionLogContext.PeerJoined(member);
            SteamP2PManager.Instance.OnPeerEnteredLobby(member);
            OnlineCoopManager.Instance?.HandlePeerEnteredLobby(member);

            CoopMod.Logger.LogInfo(
                $"[LOBBY] LobbyGUI.Instance: " +
                $"{(LobbyGUI.Instance != null ? "exists" : "null")}");
            if (LobbyGUI.Instance != null)
            {
                CoopMod.Logger.LogInfo(
                    $"[LOBBY] LobbyGUI.is_shown: {LobbyGUI.Instance.is_shown}");
                CoopMod.Logger.LogInfo(
                    $"[LOBBY] LobbyGUI.gameObject.activeSelf: " +
                    $"{LobbyGUI.Instance.gameObject.activeSelf}");
            }

            if (LobbyGUI.Instance != null &&
                LobbyGUI.Instance.gameObject.activeSelf)
            {
                CoopMod.Logger.LogInfo(
                    $"[LOBBY] Adding player avatar for {memberName}...");
                LobbyGUI.Instance.AddPlayerWithSteamID(memberName, member);
            }

            if (isHost)
                RefreshLanAdvertisement();

            PostChatMessage($"[System] {memberName} joined the lobby!");
        }

        internal bool IsPeerCompatibilityValidated(CSteamID peer)
        {
            if (peer == CSteamID.Nil || peer == SteamUser.GetSteamID())
                return false;

            if (!isHost)
                return true;

            return validatedMemberCompatibility.Contains(peer.m_SteamID);
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
                string modVersion = SteamMatchmaking.GetLobbyData(
                    lobbyID,
                    LobbyDataModVersion);
                string protocolRevision = SteamMatchmaking.GetLobbyData(
                    lobbyID,
                    LobbyDataProtocolRevision);
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
                    ProtocolRevision = protocolRevision ?? "",
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
                ModVersion = PluginInfo.PLUGIN_VERSION,
                ProtocolRevision = CoopProtocol.Revision,
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
        public string ProtocolRevision { get; set; }
        public string Status { get; set; }
        public string Visibility { get; set; }
        public int CurrentPlayers { get; set; }
        public int MaxPlayers { get; set; }
        public string DLCRequirements { get; set; }
    }
}

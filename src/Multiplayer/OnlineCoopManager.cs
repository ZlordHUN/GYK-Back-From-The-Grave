using UnityEngine;
using Steamworks;
using System.Collections.Generic;
using System.Reflection;
using GraveyardKeeperCoop.UI;
using GraveyardKeeperCoop.Multiplayer;
using GraveyardKeeperCoop.Patches;

namespace GraveyardKeeperCoop.Network
{
    /// <summary>
    /// Manages online co-op session - spawning remote players, syncing positions, etc.
    /// Host: Spawns remote player for each client, sends position updates
    /// Client: Spawns remote player for host, receives position updates
    /// </summary>
    public class OnlineCoopManager : MonoBehaviour
    {
        private static OnlineCoopManager _instance;
        public static OnlineCoopManager Instance => _instance;
        
        // The local player (Player 1)
        private PlayerComponent localPlayer;
        
        // The remote player (network-controlled Player 2)
        private PlayerComponent remotePlayer;

        private sealed class SecondaryRemotePeer
        {
            public CSteamID SteamID;
            public PlayerComponent Player;
            public GhostDriver Ghost;
            public GameObject NameTag;
            public string DisplayName;
            public Vector3 TargetPosition;
            public Vector2 Velocity;
            public Vector2 Direction = Vector2.down;
            public Vector2 AppliedDirection = Vector2.down;
            public CharAnimState Animation = CharAnimState.Idle;
            public ItemDefinition.ItemType ItemType = ItemDefinition.ItemType.None;
            public bool HasPosition;
            public bool Moving;
            public bool AppliedMoving;
            public bool ToolAnimationActive;
            public bool ActionExplicitlyStopped;
            public float IdleTimer;
        }

        // Keep the original primary fields for legacy cutscene/dialogue paths,
        // and store players three and four directly on this manager.
        private readonly Dictionary<ulong, SecondaryRemotePeer> secondaryRemotePeers =
            new Dictionary<ulong, SecondaryRemotePeer>();
        private readonly List<ulong> staleSecondaryPeerIds = new List<ulong>();
        private float nextSecondaryRosterRefreshAt;
        private const float SecondaryRosterRefreshSeconds = 1f;
        private const float SecondaryTeleportSnapDistance = 384f;
        private const float SecondaryIdleDelaySeconds = 0.15f;
        
        // GhostDriver for smooth remote player interpolation
        private GhostDriver ghostDriver;
        
        // Name tag for remote player
        private GameObject remotePlayerNameTag;
        private string remotePlayerDisplayName;
        
        // Track whether online coop is active
        public bool IsOnlineCoopEnabled { get; private set; }
        
        // Track if we're the host or client
        public bool IsHost { get; private set; }
        
        // Remote player's Steam ID (for position sync)
        private CSteamID remotePlayerSteamID;
        
        /// <summary>
        /// Get the remote player's WorldGameObject (for dialogue sync distance checks)
        /// </summary>
        public WorldGameObject GetRemotePlayer()
        {
            return remotePlayer?.wgo;
        }

        public WorldGameObject GetRemotePlayer(CSteamID steamID)
        {
            return GetRemotePlayerComponent(steamID)?.wgo;
        }

        public PlayerComponent GetRemotePlayerComponent(CSteamID steamID)
        {
            if (steamID == CSteamID.Nil)
                return null;
            if (steamID == remotePlayerSteamID)
                return remotePlayer;
            return secondaryRemotePeers.TryGetValue(
                steamID.m_SteamID,
                out SecondaryRemotePeer peer)
                ? peer.Player
                : null;
        }

        public List<KeyValuePair<CSteamID, PlayerComponent>> GetRemotePlayersSnapshot()
        {
            var result = new List<KeyValuePair<CSteamID, PlayerComponent>>();
            if (remotePlayerSteamID != CSteamID.Nil && remotePlayer != null)
            {
                result.Add(new KeyValuePair<CSteamID, PlayerComponent>(
                    remotePlayerSteamID,
                    remotePlayer));
            }

            foreach (SecondaryRemotePeer peer in secondaryRemotePeers.Values)
            {
                if (peer?.Player != null)
                {
                    result.Add(new KeyValuePair<CSteamID, PlayerComponent>(
                        peer.SteamID,
                        peer.Player));
                }
            }
            return result;
        }

        public int RemotePlayerCount =>
            (remotePlayer != null ? 1 : 0) + secondaryRemotePeers.Count;

        public CSteamID RemotePlayerSteamID => remotePlayerSteamID;

        public PlayerComponent LocalPlayerComponent => localPlayer;
        public PlayerComponent RemotePlayerComponent => remotePlayer;
        public GhostDriver RemoteGhostDriver => ghostDriver;

        public bool IsRemotePlayer(CSteamID steamID)
        {
            if (steamID == CSteamID.Nil || steamID == SteamUser.GetSteamID())
                return false;
            if (steamID == remotePlayerSteamID ||
                secondaryRemotePeers.ContainsKey(steamID.m_SteamID))
                return true;

            var lobby = SteamLobbyManager.Instance;
            if (lobby == null || !lobby.IsInLobby || lobby.CurrentLobbyID == CSteamID.Nil)
                return false;

            int memberCount = SteamMatchmaking.GetNumLobbyMembers(lobby.CurrentLobbyID);
            for (int i = 0; i < memberCount; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(lobby.CurrentLobbyID, i) == steamID)
                    return true;
            }
            return false;
        }

        public bool TryGetRemotePlayerSavePosition(out Vector3 position)
        {
            position = Vector3.zero;

            if (remotePlayer == null)
            {
                return false;
            }

            Vector3 remoteWorldPosition;
            if (hasReceivedRemotePlayerPosition)
            {
                remoteWorldPosition = targetRemotePosition;
            }
            else
            {
                remoteWorldPosition = remotePlayer.transform.position;
            }

            position = WorldToSavePosition(remoteWorldPosition);
            return true;
        }

        public bool TryGetRemotePlayerSavePosition(
            CSteamID steamID,
            out Vector3 position)
        {
            if (steamID == remotePlayerSteamID)
                return TryGetRemotePlayerSavePosition(out position);
            if (TryGetSecondaryRemoteSavePosition(steamID, out position))
            {
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        private Vector3 WorldToSavePosition(Vector3 worldPosition)
        {
            Transform saveParent = MainGame.me?.player?.transform?.parent ?? localPlayer?.transform?.parent;
            return saveParent != null
                ? saveParent.InverseTransformPoint(worldPosition)
                : worldPosition;
        }

        private static long BuildRemotePlayerUniqueId(CSteamID steamID)
        {
            long suffix = (long)(steamID.m_SteamID % 900000000UL);
            return -1000000000L - suffix;
        }
        
        // Position sync settings (20Hz for smoother remote movement)
        private float positionSyncInterval = 1f / 20f; // 20 Hz update rate
        private float cutsceneSyncInterval = 1f / 30f; // 30 Hz during cutscenes (cap to avoid flooding)
        private const float FallbackRemoteSpawnOffset = 96f;
        private float lastPositionSyncTime;
        private float lastCutsceneSyncTime;
        private Vector3 lastSentPosition;
        private float lastSentPositionSampleTime;
        private bool hasSentPositionSample;
        private Vector3 targetRemotePosition;
        private bool hasReceivedRemotePlayerPosition;
        private bool remotePositionSeededFromSidecar;
        private Vector2 lastReceivedVelocity; // Velocity from remote player's packet
        private const float RemoteTeleportSnapDistance = 384f;
        private float positionMoveSpeed = 500f; // Units per second for smooth movement (fallback only)
        
        // Lobby-based disconnect detection - check if remote player is still in the Steam lobby
        // This is more reliable than packet-based heartbeat since idle players don't send packets
        private float lastLobbyCheckTime;
        private const float LOBBY_CHECK_INTERVAL = 5f; // Check lobby membership every 5 seconds
        private float remotePlayerMissingSince; // When we first noticed remote player missing from lobby
        private const float LOBBY_DISCONNECT_TIMEOUT = 10f; // Seconds of being missing before treating as disconnected
        private bool remotePlayerMissingFromLobby; // Whether remote player is currently missing
        
        // Prevent auto-re-enable immediately after a timeout disconnect
        private float lastTimeoutDisconnectTime = -100f; // Start negative so cooldown doesn't block first enable
        private float stuckControlDisabledSince = -1f;
        private const float AUTO_ENABLE_COOLDOWN = 30f; // Seconds to wait before auto-re-enabling after timeout
        
        // Teleport lock - when we explicitly teleport the remote player, ignore incoming positions briefly
        private bool teleportLockActive;
        private float teleportLockEndTime;
        
        // Diagnostic logging for camera stuttering
        private float lastDiagnosticLogTime;
        private const float DIAGNOSTIC_LOG_INTERVAL = 2f; // Log every 2 seconds
        private int positionUpdatesThisInterval;
        private float maxPositionDeltaThisInterval;
        
        // Track pending connection info
        private string pendingSaveSlotName;
        private CSteamID pendingHostID;

        // The chat overlay is session UI, not remote-player UI. A host can be in
        // a valid multiplayer session before anyone joins, so don't tie this to
        // SpawnRemotePlayer().
        private bool sessionChatOverlayShown;
        private float nextSessionChatOverlayAttemptTime;
        
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            
            _instance = this;
            DontDestroyOnLoad(gameObject);
            CoopMod.Logger.LogInfo("[OnlineCoopManager] Initialized");
        }
        
        private void OnEnable()
        {
            // Subscribe to P2P events
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnRemotePlayerPosition -= OnRemotePlayerPositionReceived;
                SteamP2PManager.Instance.OnRemotePlayerPosition += OnRemotePlayerPositionReceived;
                
                SteamP2PManager.Instance.OnRemotePlayerState -= OnRemotePlayerStateReceived;
                SteamP2PManager.Instance.OnRemotePlayerState += OnRemotePlayerStateReceived;
                
                SteamP2PManager.Instance.OnRemotePlayerAnimation -= OnRemotePlayerAnimationReceived;
                SteamP2PManager.Instance.OnRemotePlayerAnimation += OnRemotePlayerAnimationReceived;
                
                SteamP2PManager.Instance.OnClientGameLoaded -= OnClientGameLoadedReceived;
                SteamP2PManager.Instance.OnClientGameLoaded += OnClientGameLoadedReceived;
                
                SteamP2PManager.Instance.OnGameStartReceived -= OnGameStartReceivedFromHost;
                SteamP2PManager.Instance.OnGameStartReceived += OnGameStartReceivedFromHost;

                SteamP2PManager.Instance.OnLobbyKickReceived -= OnLobbyKickReceived;
                SteamP2PManager.Instance.OnLobbyKickReceived += OnLobbyKickReceived;
            }
        }
        
        private void OnDisable()
        {
            // Unsubscribe from P2P events
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnRemotePlayerPosition -= OnRemotePlayerPositionReceived;
                SteamP2PManager.Instance.OnRemotePlayerState -= OnRemotePlayerStateReceived;
                SteamP2PManager.Instance.OnRemotePlayerAnimation -= OnRemotePlayerAnimationReceived;
                SteamP2PManager.Instance.OnClientGameLoaded -= OnClientGameLoadedReceived;
                SteamP2PManager.Instance.OnGameStartReceived -= OnGameStartReceivedFromHost;
                SteamP2PManager.Instance.OnLobbyKickReceived -= OnLobbyKickReceived;
            }
        }
        
        /// <summary>
        /// Called when a peer disconnects - show dialog and return to title
        /// </summary>
        private void OnPeerDisconnected(CSteamID peer, string reason)
        {
            if (!IsOnlineCoopEnabled)
                return;
                
            CoopMod.Logger.LogWarning($"[OnlineCoopManager] Peer {peer} disconnected: {reason}");
            
            // Only show disconnect dialog if we're a client and the host disconnected
            // Or if we're a host and a client disconnected (just log it, don't exit)
            if (!IsHost && peer == remotePlayerSteamID)
            {
                // Client lost connection to host - show dialog and return to title
                ShowDisconnectDialogAndReturnToTitle("Lost connection to host.");
            }
            else if (IsHost)
            {
                // Host: remove only the failed peer; other clients remain in session.
                CoopMod.Logger.LogInfo($"[OnlineCoopManager] Client {peer} disconnected, cleaning up...");
                HandlePeerLeftLobby(
                    peer,
                    SteamFriends.GetFriendPersonaName(peer));
            }
        }
        
        /// <summary>
        /// Called by SteamLobbyManager when the host leaves the Steam lobby while we're in-game.
        /// This provides a clean disconnect flow with a dialog and return to title.
        /// </summary>
        public void HandleHostDisconnectedFromLobby()
        {
            CoopMod.Logger.LogWarning("[OnlineCoopManager] Host disconnected (detected via Steam lobby callback)");
            ShowDisconnectDialogAndReturnToTitle("The host has left the game.");
        }

        private void OnLobbyKickReceived(CSteamID senderID, string reason)
        {
            if (!IsOnlineCoopEnabled || !MainGame.game_started || IsHost)
                return;

            var lobbyManager = SteamLobbyManager.Instance;
            if (lobbyManager == null || !lobbyManager.IsInLobby)
                return;

            CSteamID lobbyOwner = lobbyManager.GetLobbyOwner();
            if (lobbyOwner != CSteamID.Nil && senderID != lobbyOwner)
            {
                CoopMod.Logger.LogWarning($"[OnlineCoopManager] Ignoring LobbyKick from non-host {senderID}; owner is {lobbyOwner}");
                return;
            }

            string message = string.IsNullOrEmpty(reason)
                ? "You were removed from the lobby by the host."
                : reason;

            CoopMod.Logger.LogWarning($"[OnlineCoopManager] Kicked by host {senderID}: {message}");
            GraveyardKeeperCoop.Utils.ChatManager.AddMessage($"[System] {message}");
            lobbyManager.LeaveLobby();
            ShowDisconnectDialogAndReturnToTitle(message);
        }

        public void HandleRemotePlayerKicked(CSteamID targetID, string playerName)
        {
            if (!IsHost || targetID == CSteamID.Nil || !IsRemotePlayer(targetID))
                return;

            CoopMod.Logger.LogInfo($"[OnlineCoopManager] Cleaning up kicked remote player {playerName} ({targetID})");
            HandlePeerLeftLobby(targetID, playerName);
        }

        public void HandlePeerEnteredLobby(CSteamID peer)
        {
            if (!IsOnlineCoopEnabled || peer == CSteamID.Nil ||
                peer == SteamUser.GetSteamID())
            {
                return;
            }

            if (IsHost && remotePlayerSteamID == CSteamID.Nil)
                SetPrimaryRemotePeer(peer);
            else
                EnsureSecondaryRemotePeer(peer);
        }

        public void HandlePeerLeftLobby(CSteamID peer, string playerName)
        {
            if (!IsOnlineCoopEnabled || peer == CSteamID.Nil)
                return;

            PlayerTradeManager.Instance?.NotifyPeerLeft(peer);

            RemoveSecondaryRemotePeer(peer);
            if (peer != remotePlayerSteamID)
                return;

            if (!IsHost)
                return;

            CoopMod.Logger.LogInfo(
                $"[MultiPeer] Primary client {playerName} left; selecting a new legacy primary");
            DestroyPrimaryRemoteAvatar(clearSteamID: true);

            CSteamID replacement = FindFirstRemoteLobbyMember();
            if (replacement != CSteamID.Nil)
                SetPrimaryRemotePeer(replacement);
        }
        
        /// <summary>
        /// Check if the remote player is still in the Steam lobby.
        /// This is used for disconnect detection instead of packet-based heartbeat,
        /// since idle players may not send any packets.
        /// </summary>
        private bool IsRemotePlayerInLobby()
        {
            var lobbyManager = SteamLobbyManager.Instance;
            if (lobbyManager == null || !lobbyManager.IsInLobby)
                return false;

            // A host may remain in an active lobby after the last client leaves.
            // No legacy primary is expected until another client joins.
            if (IsHost && remotePlayerSteamID == CSteamID.Nil)
                return true;
            
            int memberCount = lobbyManager.GetLobbyMemberCount();
            if (memberCount <= 1)
                return false;
            
            // Check if our specific remote player is still in the lobby
            var lobbyID = lobbyManager.CurrentLobbyID;
            for (int i = 0; i < memberCount; i++)
            {
                CSteamID memberID = SteamMatchmaking.GetLobbyMemberByIndex(lobbyID, i);
                if (memberID == remotePlayerSteamID)
                    return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Show a disconnect dialog and return to title screen when user clicks OK
        /// </summary>
        private void ShowDisconnectDialogAndReturnToTitle(string message)
        {
            // Disable coop first
            DisableOnlineCoop();
            
            // Show dialog using game's built-in dialog system
            if (GUIElements.me != null && GUIElements.me.dialog != null)
            {
                GUIElements.me.dialog.OpenOK(message, new GJCommons.VoidDelegate(ReturnToTitle));
            }
            else
            {
                // Fallback: just return to title if dialog isn't available
                ReturnToTitle();
            }
        }
        
        /// <summary>
        /// Return to the title screen
        /// </summary>
        private void ReturnToTitle()
        {
            // Use LoadingGUI to show loading screen while returning to title
            LoadingGUI.Show(delegate
            {
                if (GUIElements.me != null && GUIElements.me.ingame_menu != null)
                {
                    GUIElements.me.ingame_menu.ReturnToMainMenu();
                }
            });
        }
        
        /// <summary>
        /// Enable online co-op as the HOST (we control Player 1, remote player is Player 2)
        /// </summary>
        public void EnableAsHost(CSteamID clientSteamID)
        {
            if (IsOnlineCoopEnabled)
            {
                CoopMod.Logger.LogWarning("[OnlineCoopManager] Online co-op already enabled!");
                return;
            }
            
            CoopMod.Logger.LogInfo("[OnlineCoopManager] Enabling online co-op as HOST...");
            IsHost = true;
            remotePlayerSteamID = clientSteamID;
            remotePlayerMissingFromLobby = false;
            lastLobbyCheckTime = Time.realtimeSinceStartup;
            ResetPositionSyncState();
            
            if (MainGame.me != null && MainGame.me.player != null)
            {
                localPlayer = MainGame.me.player.GetComponent<PlayerComponent>();
                CoopMod.Logger.LogInfo($"[OnlineCoopManager] Found local player: {localPlayer.gameObject.name}");
            }
            else
            {
                CoopMod.Logger.LogError("[OnlineCoopManager] Cannot enable online co-op - local player not found!");
                return;
            }
            
            SpawnRemotePlayer();
            IsOnlineCoopEnabled = true;
            EnableSecondaryRemotePeers();
            
            // Enable time synchronization (host broadcasts time)
            GameTimeSync.Instance?.EnableSync();

            // Enable weather synchronization (host broadcasts selected weather timeline)
            WeatherSync.Instance?.EnableSync();

            // Enable full player stat/buff/action parity synchronization
            PlayerParitySync.Instance?.EnableSync();

            // Enable player sprite/child-transform visual delta synchronization
            PlayerVisualSync.Instance?.EnableSync();

            // Enable host-driven NPC visual synchronization
            NpcVisualSync.Instance?.EnableSync();

            // Enable live inventory/item/drop synchronization
            InventorySync.Instance?.EnableSync();
            
            // Enable WGO destruction synchronization
            WGODestructionSync.Instance?.EnableSync();

            // Enable optional full WGO lifecycle/state synchronization
            WGOStateSync.Instance?.EnableSync();

            // Enable lightweight live transform smoothing for physics-backed WGOs
            LiveWGOTransformSync.Instance?.EnableSync();

            // Enable host-authoritative interaction relay for high-risk WGOs
            HostAuthorityInteractionSync.Instance?.EnableSync();

            // Enable craft synchronization
            CraftSync.Instance?.EnableSync();

            // Enable remote player-owned work/craft progress indicators
            WorkIndicatorSync.Instance?.EnableSync();

            // Enable technology/perk/progression synchronization
            TechSync.Instance?.EnableSync();

            // Enable quest/NPC knowledge synchronization
            QuestSync.Instance?.EnableSync();

            // Enable zone/navigation synchronization
            ZoneNavSync.Instance?.EnableSync();

            // Enable combat/damage synchronization
            CombatSync.Instance?.EnableSync();

            // Enable player parameter synchronization
            PlayerParamSync.Instance?.EnableSync();

            // Enable spawn synchronization
            SpawnSync.Instance?.EnableSync();
            // Enable dungeon synchronization
            DungeonSync.Instance?.EnableSync();
            // Enable worker synchronization
            WorkerSync.Instance?.EnableSync();
            // Enable fishing synchronization
            FishingSync.Instance?.EnableSync();
            // Enable DLC synchronization
            DLCSync.Instance?.EnableSync();

            // Enable cutscene/FlowScript synchronization
            CutsceneSyncPatches.Enable();

            // Enable NPC interaction synchronization (donkey intro, Gerry talk, etc.)
            NpcInteractionSyncPatches.Enable();
            
            // Listen for cutscene walk-to requests from the remote player
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnCutsceneWalkToReceived -= OnCutsceneWalkToReceived;
                SteamP2PManager.Instance.OnCutsceneWalkToReceived += OnCutsceneWalkToReceived;
            }
            
            // Ensure camera follows only local player
            EnsureCameraFollowsLocalPlayer();
            
            CoopMod.Logger.LogInfo("[OnlineCoopManager] Online co-op enabled as HOST");
        }
        
        /// <summary>
        /// Enable online co-op as a CLIENT (we wait for host's position, spawn remote player for host)
        /// </summary>
        public void EnableAsClient(CSteamID hostSteamID)
        {
            if (IsOnlineCoopEnabled)
            {
                CoopMod.Logger.LogWarning("[OnlineCoopManager] Online co-op already enabled!");
                return;
            }
            
            CoopMod.Logger.LogInfo("[OnlineCoopManager] Enabling online co-op as CLIENT...");
            IsHost = false;
            remotePlayerSteamID = hostSteamID;
            remotePlayerMissingFromLobby = false;
            lastLobbyCheckTime = Time.realtimeSinceStartup;
            ResetPositionSyncState();
            
            if (MainGame.me != null && MainGame.me.player != null)
            {
                localPlayer = MainGame.me.player.GetComponent<PlayerComponent>();
                CoopMod.Logger.LogInfo($"[OnlineCoopManager] Found local player: {localPlayer.gameObject.name}");
            }
            else
            {
                CoopMod.Logger.LogError("[OnlineCoopManager] Cannot enable online co-op - local player not found!");
                return;
            }
            
            SpawnRemotePlayer();
            IsOnlineCoopEnabled = true;
            EnableSecondaryRemotePeers();
            
            // Enable time synchronization (client receives time)
            GameTimeSync.Instance?.EnableSync();

            // Enable weather synchronization (client receives host weather timeline)
            WeatherSync.Instance?.EnableSync();

            // Enable full player stat/buff/action parity synchronization
            PlayerParitySync.Instance?.EnableSync();

            // Enable player sprite/child-transform visual delta synchronization
            PlayerVisualSync.Instance?.EnableSync();

            // Enable host-driven NPC visual synchronization
            NpcVisualSync.Instance?.EnableSync();

            // Enable live inventory/item/drop synchronization
            InventorySync.Instance?.EnableSync();
            
            // Enable WGO destruction synchronization
            WGODestructionSync.Instance?.EnableSync();

            // Enable optional full WGO lifecycle/state synchronization
            WGOStateSync.Instance?.EnableSync();

            // Enable lightweight live transform smoothing for physics-backed WGOs
            LiveWGOTransformSync.Instance?.EnableSync();

            // Enable host-authoritative interaction relay for high-risk WGOs
            HostAuthorityInteractionSync.Instance?.EnableSync();

            // Enable craft synchronization
            CraftSync.Instance?.EnableSync();

            // Enable remote player-owned work/craft progress indicators
            WorkIndicatorSync.Instance?.EnableSync();

            // Enable technology/perk/progression synchronization
            TechSync.Instance?.EnableSync();

            // Enable quest/NPC knowledge synchronization
            QuestSync.Instance?.EnableSync();

            // Enable zone/navigation synchronization
            ZoneNavSync.Instance?.EnableSync();

            // Enable combat/damage synchronization
            CombatSync.Instance?.EnableSync();

            // Enable player parameter synchronization
            PlayerParamSync.Instance?.EnableSync();

            // Enable spawn synchronization
            SpawnSync.Instance?.EnableSync();
            // Enable dungeon synchronization
            DungeonSync.Instance?.EnableSync();
            // Enable worker synchronization
            WorkerSync.Instance?.EnableSync();
            // Enable fishing synchronization
            FishingSync.Instance?.EnableSync();
            // Enable DLC synchronization
            DLCSync.Instance?.EnableSync();

            // Enable cutscene/FlowScript synchronization
            CutsceneSyncPatches.Enable();

            // Enable NPC interaction synchronization (donkey intro, Gerry talk, etc.)
            NpcInteractionSyncPatches.Enable();
            
            // Listen for cutscene walk-to requests from the remote player
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnCutsceneWalkToReceived -= OnCutsceneWalkToReceived;
                SteamP2PManager.Instance.OnCutsceneWalkToReceived += OnCutsceneWalkToReceived;
            }
            
            // Ensure camera follows only local player
            EnsureCameraFollowsLocalPlayer();

            // Notify host after client-side sync receivers are ready for initial baselines.
            SteamP2PManager.Instance?.NotifyHostGameLoaded();
            
            CoopMod.Logger.LogInfo("[OnlineCoopManager] Online co-op enabled as CLIENT");
        }
        
        /// <summary>
        /// Disable online co-op and clean up
        /// </summary>
        public void DisableOnlineCoop()
        {
            if (!IsOnlineCoopEnabled)
                return;
            
            CoopMod.Logger.LogInfo("[OnlineCoopManager] Disabling online co-op...");
            
            // Disable time synchronization
            GameTimeSync.Instance?.DisableSync();

            // Disable weather synchronization
            WeatherSync.Instance?.DisableSync();

            // Disable player stat/buff/action parity synchronization
            PlayerParitySync.Instance?.DisableSync();

            // Disable player visual delta synchronization
            PlayerVisualSync.Instance?.DisableSync();

            // Disable NPC visual synchronization
            NpcVisualSync.Instance?.DisableSync();

            // Disable live inventory/item/drop synchronization
            InventorySync.Instance?.DisableSync();
            
            // Disable WGO destruction synchronization
            WGODestructionSync.Instance?.DisableSync();

            // Disable full WGO lifecycle/state synchronization
            WGOStateSync.Instance?.DisableSync();

            // Disable lightweight live transform smoothing
            LiveWGOTransformSync.Instance?.DisableSync();

            // Disable host-authoritative interaction relay
            HostAuthorityInteractionSync.Instance?.DisableSync();

            // Disable craft synchronization
            CraftSync.Instance?.DisableSync();

            // Disable remote player-owned work/craft progress indicators
            WorkIndicatorSync.Instance?.DisableSync();

            // Disable technology/perk/progression synchronization
            TechSync.Instance?.DisableSync();

            // Disable quest/NPC knowledge synchronization
            QuestSync.Instance?.DisableSync();

            // Disable zone/navigation synchronization
            ZoneNavSync.Instance?.DisableSync();

            // Disable combat/damage synchronization
            CombatSync.Instance?.DisableSync();

            // Disable player parameter synchronization
            PlayerParamSync.Instance?.DisableSync();

            // Disable spawn synchronization
            SpawnSync.Instance?.DisableSync();
            // Disable dungeon synchronization
            DungeonSync.Instance?.DisableSync();
            // Disable worker synchronization
            WorkerSync.Instance?.DisableSync();
            // Disable fishing synchronization
            FishingSync.Instance?.DisableSync();
            // Disable DLC synchronization
            DLCSync.Instance?.DisableSync();

            // Disable cutscene/FlowScript synchronization
            CutsceneSyncPatches.Disable();

            // Disable NPC interaction synchronization
            NpcInteractionSyncPatches.Disable();

            // Cancel pending/active trades and return any locally escrowed items.
            PlayerTradeManager.Instance?.Shutdown();

            // Remove all non-primary experimental avatars before the shared
            // cosmetics/session cleanup clears their registrations.
            DisableSecondaryRemotePeers();
            
            // Unsubscribe walk-to handler and clear any in-flight walk
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnCutsceneWalkToReceived -= OnCutsceneWalkToReceived;
            }
            EndObservedCutsceneFollow("online co-op disabled");
            RestoreCutsceneWalkSpeed(MainGame.me?.player);
            walkInProgress = false;
            
            // Disable cosmetics synchronization
            CosmeticsSync.Instance?.OnCoopEnded();

            // The overlay is session-scoped. Destroy it rather than merely
            // fading it so menu/lobby messages cannot reveal stale in-game UI.
            UI.ChatOverlay.Instance?.EndSession();
            sessionChatOverlayShown = false;
            nextSessionChatOverlayAttemptTime = 0f;
            UI.ChatBubbleManager.Cleanup();

            DestroyPrimaryRemoteAvatar(clearSteamID: true);

            localPlayer = null;
            remotePlayerDisplayName = null;
            hasReceivedRemotePlayerPosition = false;
            remotePositionSeededFromSidecar = false;
            ResetPositionSyncState();
            IsOnlineCoopEnabled = false;

            CoopMod.Logger.LogInfo("[OnlineCoopManager] Online co-op disabled");
        }

        private void DestroyPrimaryRemoteAvatar(bool clearSteamID)
        {
            if (ghostDriver != null)
            {
                ghostDriver.Disable();
                ghostDriver = null;
            }

            CosmeticsSync.Instance?.RemoveRemotePlayerDriver(remotePlayerSteamID);
            if (remotePlayerNameTag != null)
            {
                NameTagManager.DestroyNameTag(remotePlayerNameTag);
                remotePlayerNameTag = null;
            }

            if (!string.IsNullOrEmpty(remotePlayerDisplayName))
                ChatGUI.Instance?.RemoveRemotePlayer(remotePlayerDisplayName);
            if (remotePlayerSteamID != CSteamID.Nil)
            {
                MapGUIPatches.RemoveRemotePlayerIndicator(
                    remotePlayerSteamID.m_SteamID.ToString());
            }

            if (remotePlayer != null)
            {
                Destroy(remotePlayer.gameObject);
                remotePlayer = null;
            }

            remotePlayerDisplayName = null;
            hasReceivedRemotePlayerPosition = false;
            remotePositionSeededFromSidecar = false;
            if (clearSteamID)
                remotePlayerSteamID = CSteamID.Nil;
            ResetPositionSyncState();
        }

        private void SetPrimaryRemotePeer(CSteamID peer)
        {
            if (peer == CSteamID.Nil || peer == SteamUser.GetSteamID())
                return;

            RemoveSecondaryRemotePeer(peer);
            remotePlayerSteamID = peer;
            remotePlayerMissingFromLobby = false;
            ResetPositionSyncState();
            SpawnRemotePlayer();
            CoopMod.Logger.LogInfo(
                $"[MultiPeer] Legacy primary remote is now {peer.m_SteamID}");
        }

        private static CSteamID FindFirstRemoteLobbyMember()
        {
            SteamLobbyManager lobby = SteamLobbyManager.Instance;
            if (lobby == null || !lobby.IsInLobby ||
                lobby.CurrentLobbyID == CSteamID.Nil)
            {
                return CSteamID.Nil;
            }

            CSteamID local = SteamUser.GetSteamID();
            int count = SteamMatchmaking.GetNumLobbyMembers(lobby.CurrentLobbyID);
            for (int i = 0; i < count; i++)
            {
                CSteamID member =
                    SteamMatchmaking.GetLobbyMemberByIndex(lobby.CurrentLobbyID, i);
                if (member != CSteamID.Nil && member != local)
                    return member;
            }
            return CSteamID.Nil;
        }

        /// <summary>
        /// Tear down the current coop session in preparation for a mid-session save reload.
        /// The Steam lobby / P2P session stays up, but all references to the about-to-be-
        /// destroyed remote player are cleared and the per-session activation flags are
        /// reset so that when the player respawns after the load, the normal spawn-hook
        /// + CheckAutoEnable path will rebuild coop cleanly.
        ///
        /// Intended callers:
        ///   - Host: <see cref="Patches.SaveLoadPatches.OnLoadSlot_Prefix"/> when the host
        ///     picks a save from the in-game Load menu.
        ///   - Client: <see cref="UI.LobbyGUI.OnGameStartReceived"/> when the host is
        ///     hot-reloading and the client is already in a game.
        /// </summary>
        public void PrepareHotReload()
        {
            CoopMod.Logger.LogInfo("[OnlineCoopManager] PrepareHotReload - tearing down coop state for in-game reload");

            // Arm this before disabling the sync systems. On clients the old world
            // remains playable while the replacement save downloads; without the gate,
            // Update.CheckAutoEnable reuses that old player on the next frame and the
            // client-side load protection then rejects the host-directed load.
            Patches.LocalCoopActivationPatch.BeginHotReload();

            // Disable is a no-op if already disabled, but safe to call.
            DisableOnlineCoop();

            // Clear any leftover sync state (we will re-arm it via StartWaitingForPlayers).
            if (Multiplayer.GameLoadSync.Instance != null)
            {
                Multiplayer.GameLoadSync.Instance.ResetSyncState();
            }
        }
        
        /// <summary>
        /// Called by client when they receive GAME_START from host
        /// </summary>
        private void OnGameStartReceivedFromHost(CSteamID hostID, string saveSlotName)
        {
            CoopMod.Logger.LogInfo($"[OnlineCoopManager] Received GAME_START from host: {saveSlotName}");
            
            // Store the info for when the host's game loads
            pendingHostID = hostID;
            pendingSaveSlotName = saveSlotName;
            
            // For now, we need the client to also start a game
            // In a full implementation, the client would receive the game state from the host
            // For this MVP, the client will start their own game and position sync will work
            
            // TODO: Implement proper state sync or spectator mode
            // For now, just log that we're waiting
            CoopMod.Logger.LogInfo("[OnlineCoopManager] Client waiting for implementation of game joining...");
        }
        
        /// <summary>
        /// Called on HOST when a client notifies they've loaded
        /// </summary>
        private void OnClientGameLoadedReceived(CSteamID clientID)
        {
            CoopMod.Logger.LogInfo($"[OnlineCoopManager] Client {clientID} has loaded their game!");
            
            // If we're host and our game is loaded, enable online coop with this client
            if (IsHost || SteamLobbyManager.Instance?.IsHost == true)
            {
                if (MainGame.game_started && MainGame.me?.player != null)
                {
                    if (IsOnlineCoopEnabled && IsHost)
                    {
                        if (remotePlayerSteamID == CSteamID.Nil)
                            SetPrimaryRemotePeer(clientID);
                        else
                            EnsureSecondaryRemotePeer(clientID);

                        if (ModConfig.EnableLiveWGOStateSync?.Value == true)
                        {
                            WGOStateSync.Instance?.SendLocalSnapshot(force: true);
                        }

                        InventorySync.Instance?.SendLocalSnapshot(force: true);
                        WeatherSync.Instance?.SendWeatherSync();
                        PlayerParitySync.Instance?.SendLocalSnapshot(force: true);
                        CraftSync.Instance?.SendLocalSnapshot(force: true);
                        TechSync.Instance?.SendLocalSnapshot(force: true);
                        QuestSync.Instance?.SendLocalSnapshot(force: true);
                        ZoneNavSync.Instance?.SendLocalSnapshot(force: true);
                        PlayerParamSync.Instance?.SendLocalSnapshot(force: true);
                        return;
                    }

                    EnableAsHost(clientID);
                }
                else
                {
                    CoopMod.Logger.LogInfo("[OnlineCoopManager] Waiting for host game to finish loading...");
                }
            }
        }
        
        /// <summary>
        /// Spawn a network-controlled remote player
        /// </summary>
        private void SpawnRemotePlayer()
        {
            try
            {
                CoopMod.Logger.LogInfo("[OnlineCoopManager] Spawning remote player...");
                hasReceivedRemotePlayerPosition = false;
                remotePositionSeededFromSidecar = false;
                
                // Spawn Player 2 using the same method as LocalCoopManager
                remotePlayer = PlayerComponent.SpawnPlayer(is_local_player: false, inventory: null);
                
                if (remotePlayer == null)
                {
                    CoopMod.Logger.LogError("[OnlineCoopManager] Failed to spawn remote player!");
                    return;
                }
                
                CoopMod.Logger.LogInfo($"[OnlineCoopManager] Remote player spawned: {remotePlayer.gameObject.name}");

                remotePlayer.gameObject.name =
                    $"RemotePlayer_{remotePlayerSteamID.m_SteamID}";
                if (remotePlayer.wgo != null)
                {
                    remotePlayer.wgo.unique_id =
                        BuildRemotePlayerUniqueId(remotePlayerSteamID);
                }
                
                // Disable local control - this player is network controlled
                // NOTE: We intentionally do NOT set player_controlled_by_script = true
                // because that affects how UpdateComponent works for the character.
                // Local coop also doesn't set this flag and works fine.
                if (remotePlayer.wgo?.components?.character != null)
                {
                    var character = remotePlayer.wgo.components.character;
                    character.can_be_locally_controlled = false;
                    character.control_enabled = false; // No local input
                    // Don't set player_controlled_by_script - let the character update normally
                    CoopMod.Logger.LogInfo("[OnlineCoopManager] Remote player set to network control mode (control_enabled=false)");
                }
                
                // Set speed to normal player speed
                if (remotePlayer.wgo != null)
                {
                    float normalSpeed = LazyConsts.PLAYER_SPEED;
                    remotePlayer.wgo.data.SetParam("speed", normalSpeed);
                }
                
                bool positionedFromSave = false;
                if (MultiplayerSavePositions.TryGetSavedPositionForSteam(remotePlayerSteamID, out Vector3 savedRemotePosition))
                {
                    remotePlayer.transform.localPosition = savedRemotePosition;
                    if (remotePlayer.wgo != null)
                    {
                        remotePlayer.wgo.transform.localPosition = savedRemotePosition;
                    }

                    targetRemotePosition = remotePlayer.transform.position;
                    positionedFromSave = true;
                    remotePositionSeededFromSidecar = true;
                    CoopMod.Logger.LogInfo($"[OnlineCoopManager] Remote player positioned from multiplayer save sidecar: {savedRemotePosition}");
                }

                // Position near local player, but far enough that player colliders do not overlap.
                if (!positionedFromSave && localPlayer != null)
                {
                    Vector3 spawnPos = localPlayer.transform.position + new Vector3(FallbackRemoteSpawnOffset, 0f, 0f);
                    remotePlayer.transform.position = spawnPos;
                    if (remotePlayer.wgo != null)
                    {
                        remotePlayer.wgo.transform.position = spawnPos;
                    }

                    targetRemotePosition = spawnPos;
                    CoopMod.Logger.LogInfo($"[OnlineCoopManager] Remote player positioned at fallback offset {FallbackRemoteSpawnOffset:F0}: {spawnPos} (local player at {localPlayer.transform.position})");
                }
                
                // Initialize inventory
                if (remotePlayer.wgo != null)
                {
                    remotePlayer.wgo.data.SetInventorySize(20);
                    
                    if (localPlayer?.wgo != null)
                    {
                        remotePlayer.wgo.data.hp = localPlayer.wgo.data.hp;
                        remotePlayer.wgo.data.SetParam("energy", localPlayer.wgo.data.GetParam("energy", 100f));
                        remotePlayer.wgo.data.SetParam("sanity", localPlayer.wgo.data.GetParam("sanity", 100f));
                    }
                }
                
                // Initialize DropCollector for item pickup
                InitializeDropCollector();
                
                // IMPORTANT: Remove remote player from camera targets
                // The camera should only follow the local player, not the network-controlled remote player
                try
                {
                    CameraTools.RemoveFromCameraTargets(remotePlayer.transform, 0f);
                    CoopMod.Logger.LogInfo("[OnlineCoopManager] Removed remote player from camera targets");
                }
                catch (System.Exception ex)
                {
                    CoopMod.Logger.LogWarning($"[OnlineCoopManager] Could not remove from camera targets: {ex.Message}");
                }
                
                // Set ChunkedGameObject to always_active to prevent chunk recalculation stuttering
                try
                {
                    var chunked = remotePlayer.GetComponent<ChunkedGameObject>();
                    if (chunked != null)
                    {
                        chunked.always_active = true;
                        CoopMod.Logger.LogInfo("[OnlineCoopManager] Remote player ChunkedGameObject set to always_active");
                    }
                }
                catch (System.Exception ex)
                {
                    CoopMod.Logger.LogWarning($"[OnlineCoopManager] Could not set ChunkedGameObject: {ex.Message}");
                }
                
                // CRITICAL: Disable AI pathfinding components that override our position updates
                // The AILerp component updates position every frame which fights with our network sync
                DisableAIPathfinding();
                
                // Add GhostDriver for smooth position interpolation
                ghostDriver = remotePlayer.gameObject.AddComponent<GhostDriver>();
                ghostDriver.Enable();
                CoopMod.Logger.LogInfo("[OnlineCoopManager] GhostDriver attached and enabled for smooth interpolation");
                
                // Add PlayerCosmeticsDriver for visual differentiation
                var cosmeticsDriver = remotePlayer.gameObject.AddComponent<PlayerCosmeticsDriver>();
                cosmeticsDriver.Initialize();
                // Apply default P2 cosmetics until we receive actual cosmetics
                cosmeticsDriver.SetCosmetics(PlayerCosmetics.Player2Default);
                // Register with CosmeticsSync
                CosmeticsSync.Instance?.SetRemotePlayerDriver(
                    remotePlayerSteamID,
                    cosmeticsDriver);
                CoopMod.Logger.LogInfo("[OnlineCoopManager] PlayerCosmeticsDriver attached for remote player");
                
                // Create name tag for remote player
                string remoteName = NameTagManager.GetSteamDisplayName(remotePlayerSteamID);
                remotePlayerNameTag = NameTagManager.CreateNameTag(remotePlayer.gameObject, remoteName);
                remotePlayerDisplayName = remoteName;
                CoopMod.Logger.LogInfo($"[OnlineCoopManager] Name tag created for remote player: {remoteName}");
                
                // Add remote player to the in-game chat player list
                if (ChatGUI.Instance != null)
                {
                    ChatGUI.Instance.AddRemotePlayer(remoteName, remotePlayerSteamID);
                    CoopMod.Logger.LogInfo($"[OnlineCoopManager] Added remote player to chat player list: {remoteName}");
                }
                
                // Copy sorting layer from local player to ensure remote player renders correctly
                // This is important for special scenes like the "in the dark" intro
                CopySortingLayerFromLocalPlayer();
                
                // Log detailed comparison of local vs remote player state
                LogPlayerComparison();
                
                // Notify cosmetics sync that coop has started
                CosmeticsSync.Instance?.OnCoopStarted();
                
                CoopMod.Logger.LogInfo("[OnlineCoopManager] Remote player setup complete!");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[OnlineCoopManager] Error spawning remote player: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        private void InitializeDropCollector()
        {
            try
            {
                var dropCollector = new DropCollectorComponent();
                dropCollector.Init(remotePlayer.wgo);
                dropCollector.StartComponent();
                
                var dropCollectorField = typeof(PlayerComponent).GetField("_drop_collector",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    
                if (dropCollectorField != null)
                {
                    dropCollectorField.SetValue(remotePlayer, dropCollector);
                    CoopMod.Logger.LogInfo("[OnlineCoopManager] Remote player DropCollector initialized");
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[OnlineCoopManager] Failed to init DropCollector: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Disable AI pathfinding components and physics on the remote player.
        /// The AILerp, Seeker, and Rigidbody2D components fight with our network position sync,
        /// constantly overwriting the position we set in LateUpdate.
        /// 
        /// ROOT CAUSE FOUND: The game uses RigidbodyInterpolation2D.Extrapolate on player characters.
        /// Unity's physics extrapolation predicts position between FixedUpdate frames.
        /// When we set transform.position in LateUpdate, the extrapolation moves it back on next frame.
        /// Solution: Disable Rigidbody2D simulation entirely for the remote player.
        /// </summary>
        private void DisableAIPathfinding()
        {
            if (remotePlayer == null) return;
            
            try
            {
                // Get the GameObject to search for components
                var go = remotePlayer.gameObject;
                
                // CRITICAL FIX: Disable Rigidbody2D simulation to stop physics extrapolation
                // from moving the player position between frames!
                // The game uses RigidbodyInterpolation2D.Extrapolate which predicts position
                // and fights with our transform.position assignments.
                var rigidbody2D = go.GetComponentInChildren<Rigidbody2D>(true);
                if (rigidbody2D != null)
                {
                    rigidbody2D.simulated = false; // Completely disable physics simulation
                    rigidbody2D.velocity = Vector2.zero;
                    rigidbody2D.angularVelocity = 0f;
                    CoopMod.Logger.LogInfo($"[OnlineCoopManager] Disabled Rigidbody2D simulation on remote player (found on {rigidbody2D.gameObject.name})");
                }
                else
                {
                    // Try the wgo path
                    if (remotePlayer.wgo?.components?.character?.body != null)
                    {
                        var body = remotePlayer.wgo.components.character.body;
                        body.simulated = false;
                        body.velocity = Vector2.zero;
                        body.angularVelocity = 0f;
                        CoopMod.Logger.LogInfo("[OnlineCoopManager] Disabled Rigidbody2D simulation via character.body");
                    }
                    else
                    {
                        CoopMod.Logger.LogWarning("[OnlineCoopManager] Rigidbody2D NOT FOUND on remote player!");
                    }
                }
                
                // NOTE: Local coop doesn't call StopMovement/StopImmediate and works fine.
                // We'll skip these calls to match local coop behavior.
                // The position will be controlled by our LateUpdate instead.
                
                // Disable AILerp - this is the component that moves the character along paths
                // Search in children too since it might not be on the root object
                var aiLerp = go.GetComponentInChildren<AILerp>(true);
                if (aiLerp != null)
                {
                    aiLerp.enabled = false;
                    aiLerp.canMove = false; // Also set canMove to false as extra safety
                    CoopMod.Logger.LogInfo($"[OnlineCoopManager] Disabled AILerp on remote player (found on {aiLerp.gameObject.name})");
                }
                else
                {
                    CoopMod.Logger.LogWarning("[OnlineCoopManager] AILerp NOT FOUND on remote player!");
                }
                
                // Disable Seeker - this calculates paths for AILerp
                var seeker = go.GetComponentInChildren<Seeker>(true);
                if (seeker != null)
                {
                    seeker.enabled = false;
                    CoopMod.Logger.LogInfo($"[OnlineCoopManager] Disabled Seeker on remote player (found on {seeker.gameObject.name})");
                }
                
                // Also try to disable AIPath if it exists
                var aiPath = go.GetComponentInChildren<AIPath>(true);
                if (aiPath != null)
                {
                    aiPath.enabled = false;
                    CoopMod.Logger.LogInfo($"[OnlineCoopManager] Disabled AIPath on remote player (found on {aiPath.gameObject.name})");
                }
                
                // CRITICAL FIX: DESTROY the shadow GameObjects on remote player
                // ObjectDynamicShadow creates dark circular shadows around characters.
                // The remote player shouldn't have its own shadows - they cause the
                // "dark fog" visual bug around the remote player in interiors.
                // We need to destroy them, not just disable, because the SpriteRenderers
                // on shadow children can still render even when the component is disabled.
                var dynamicShadow = go.GetComponentInChildren<ObjectDynamicShadow>(true);
                if (dynamicShadow != null)
                {
                    // Destroy the entire shadow GameObject and all its children
                    var shadowGO = dynamicShadow.gameObject;
                    CoopMod.Logger.LogInfo($"[OnlineCoopManager] DESTROYING ObjectDynamicShadow GameObject: {shadowGO.name}");
                    UnityEngine.Object.Destroy(shadowGO);
                }
                
                // Also find and destroy any standalone shadow child GameObjects
                // (they might be on separate GameObjects from the parent shadow)
                var shadowChildren = go.GetComponentsInChildren<ObjectDynamicShadowChild>(true);
                foreach (var shadowChild in shadowChildren)
                {
                    if (shadowChild != null && shadowChild.gameObject != null)
                    {
                        CoopMod.Logger.LogInfo($"[OnlineCoopManager] DESTROYING ObjectDynamicShadowChild GameObject: {shadowChild.gameObject.name}");
                        UnityEngine.Object.Destroy(shadowChild.gameObject);
                    }
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[OnlineCoopManager] Error disabling AI pathfinding: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Copy the sorting layer configuration from local player to remote player.
        /// This ensures the remote player renders on the same layers as the local player,
        /// which is important for special scenes like the "in the dark" intro.
        /// </summary>
        private void CopySortingLayerFromLocalPlayer()
        {
            if (remotePlayer == null || localPlayer == null)
                return;
            
            try
            {
                // Get all sprite renderers from local player
                var localSprites = localPlayer.GetComponentsInChildren<SpriteRenderer>(true);
                var remoteSprites = remotePlayer.GetComponentsInChildren<SpriteRenderer>(true);
                
                if (localSprites.Length == 0 || remoteSprites.Length == 0)
                    return;
                
                // Find the main body sprite from local player (usually the one with non-default layer)
                string targetLayer = "Default";
                int targetOrder = 0;
                
                foreach (var sr in localSprites)
                {
                    // Skip shadow sprites
                    if (sr.gameObject.name.ToLower().Contains("shadow"))
                        continue;
                    
                    if (sr.sortingLayerName != "Default")
                    {
                        targetLayer = sr.sortingLayerName;
                        targetOrder = sr.sortingOrder;
                        break;
                    }
                }
                
                // Apply to remote player sprites
                foreach (var sr in remoteSprites)
                {
                    // Skip shadow sprites
                    if (sr.gameObject.name.ToLower().Contains("shadow"))
                        continue;
                    
                    if (sr.sortingLayerName != targetLayer)
                    {
                        sr.sortingLayerName = targetLayer;
                    }
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[OnlineCoopManager] Error copying sorting layer: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Log detailed comparison between local and remote player state to help diagnose visibility issues
        /// </summary>
        private void LogPlayerComparison()
        {
            if (localPlayer == null || remotePlayer == null) return;
            
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("===== PLAYER COMPARISON (Local vs Remote) =====");
                
                // GameObject state
                sb.AppendLine($"[GO] Local active: {localPlayer.gameObject.activeInHierarchy}, Remote active: {remotePlayer.gameObject.activeInHierarchy}");
                
                // Position
                sb.AppendLine($"[Pos] Local: {localPlayer.transform.position}, Remote: {remotePlayer.transform.position}");
                
                // WGO state
                if (localPlayer.wgo != null && remotePlayer.wgo != null)
                {
                    sb.AppendLine($"[WGO] Local is_player: {localPlayer.wgo.is_player}, Remote is_player: {remotePlayer.wgo.is_player}");
                }
                
                // Character state
                var localChar = localPlayer.wgo?.components?.character;
                var remoteChar = remotePlayer.wgo?.components?.character;
                if (localChar != null && remoteChar != null)
                {
                    sb.AppendLine($"[Char] Local control_enabled: {localChar.control_enabled}, Remote: {remoteChar.control_enabled}");
                    sb.AppendLine($"[Char] Local can_be_locally_controlled: {localChar.can_be_locally_controlled}, Remote: {remoteChar.can_be_locally_controlled}");
                    sb.AppendLine($"[Char] Local player_controlled_by_script: {localChar.player_controlled_by_script}, Remote: {remoteChar.player_controlled_by_script}");
                }
                
                // ChunkedGameObject state
                var localChunked = localPlayer.GetComponent<ChunkedGameObject>();
                var remoteChunked = remotePlayer.GetComponent<ChunkedGameObject>();
                if (localChunked != null && remoteChunked != null)
                {
                    sb.AppendLine($"[Chunk] Local always_active: {localChunked.always_active}, obj_visible: {localChunked.obj_visible}");
                    sb.AppendLine($"[Chunk] Remote always_active: {remoteChunked.always_active}, obj_visible: {remoteChunked.obj_visible}");
                }
                
                // SpriteRenderer count
                var localSprites = localPlayer.GetComponentsInChildren<SpriteRenderer>(true);
                var remoteSprites = remotePlayer.GetComponentsInChildren<SpriteRenderer>(true);
                sb.AppendLine($"[Sprites] Local count: {localSprites.Length}, Remote count: {remoteSprites.Length}");
                
                // Count enabled/disabled sprites
                int localEnabled = 0, localDisabled = 0, remoteEnabled = 0, remoteDisabled = 0;
                foreach (var sr in localSprites) { if (sr.enabled) localEnabled++; else localDisabled++; }
                foreach (var sr in remoteSprites) { if (sr.enabled) remoteEnabled++; else remoteDisabled++; }
                sb.AppendLine($"[Sprites] Local enabled: {localEnabled}, disabled: {localDisabled}");
                sb.AppendLine($"[Sprites] Remote enabled: {remoteEnabled}, disabled: {remoteDisabled}");
                
                // Check Rigidbody2D
                var localRb = localPlayer.GetComponentInChildren<Rigidbody2D>(true);
                var remoteRb = remotePlayer.GetComponentInChildren<Rigidbody2D>(true);
                if (localRb != null)
                    sb.AppendLine($"[RB2D] Local simulated: {localRb.simulated}, interpolation: {localRb.interpolation}");
                if (remoteRb != null)
                    sb.AppendLine($"[RB2D] Remote simulated: {remoteRb.simulated}, interpolation: {remoteRb.interpolation}");
                
                // Check AILerp
                var localAI = localPlayer.GetComponentInChildren<AILerp>(true);
                var remoteAI = remotePlayer.GetComponentInChildren<AILerp>(true);
                if (localAI != null)
                    sb.AppendLine($"[AILerp] Local enabled: {localAI.enabled}, canMove: {localAI.canMove}");
                if (remoteAI != null)
                    sb.AppendLine($"[AILerp] Remote enabled: {remoteAI.enabled}, canMove: {remoteAI.canMove}");
                
                sb.AppendLine("===== END COMPARISON =====");
                CoopMod.Logger.LogInfo(sb.ToString());
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[OnlineCoopManager] LogPlayerComparison error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Teleport the remote player to a specific position.
        /// Called when the local player teleports (e.g., to "player_in_the_dark" for intro cutscene).
        /// </summary>
        public void TeleportRemotePlayerTo(Vector3 position)
        {
            if (remotePlayer == null)
            {
                CoopMod.Logger.LogWarning("[OnlineCoopManager] Cannot teleport remote player - remotePlayer is null");
                return;
            }
            
            CoopMod.Logger.LogInfo($"[OnlineCoopManager] Teleporting remote player to {position}");
            
            // Lock position sync for a brief period to prevent network messages from overwriting
            teleportLockActive = true;
            teleportLockEndTime = Time.time + 3f; // Lock for 3 seconds
            CoopMod.Logger.LogInfo($"[OnlineCoopManager] Teleport lock activated for 3 seconds");
            
            // Immediately set position on all transforms
            remotePlayer.transform.position = position;
            if (remotePlayer.wgo != null)
            {
                remotePlayer.wgo.transform.position = position;
            }
            
            // Force GhostDriver to snap to position (bypasses interpolation)
            if (ghostDriver != null)
            {
                ghostDriver.ForcePosition(position);
            }
            
            // Update target position so LateUpdate doesn't fight us
            targetRemotePosition = position;
            lastLocalPlayerPosition = MainGame.me?.player?.transform.position ?? position;
            
            // Recalculate chunk for proper rendering
            try
            {
                var chunked = remotePlayer.GetComponent<ChunkedGameObject>();
                if (chunked != null)
                {
                    chunked.RecalculateChunk();
                }
            }
            catch { }
            
            // Re-disable AI pathfinding in case it got re-enabled
            DisableAIPathfinding();
            
            // Force visibility
            ForceRemotePlayerVisible();
            
            CoopMod.Logger.LogInfo($"[OnlineCoopManager] Remote player teleported successfully to {position}");
        }

        private void SnapRemotePlayerToNetworkPosition(Vector3 position, string reason)
        {
            if (remotePlayer == null)
                return;

            remotePlayer.transform.position = position;
            if (remotePlayer.wgo != null)
            {
                remotePlayer.wgo.transform.position = position;
            }

            targetRemotePosition = position;
            hasReceivedRemotePlayerPosition = true;
            lastReceivedVelocity = Vector2.zero;
            remoteIsMoving = false;
            remoteIdleTimer = IDLE_DELAY;

            if (ghostDriver != null)
            {
                ghostDriver.ForcePosition(position);
            }

            try
            {
                var chunked = remotePlayer.GetComponent<ChunkedGameObject>();
                if (chunked != null)
                {
                    chunked.RecalculateChunk();
                }
            }
            catch { }

            DisableAIPathfinding();
            ForceRemotePlayerVisible();

            CoopMod.Logger.LogInfo($"[OnlineCoopManager] Snapped remote player to network position {position} ({reason})");
        }

        private bool ShouldUseIntroFollowPositioning()
        {
            return GameLoadSync.Instance?.IsInIntroPhase == true && !remotePositionSeededFromSidecar;
        }
        
        /// <summary>
        /// Called when we receive a position update from the remote player
        /// </summary>
        private void OnRemotePlayerPositionReceived(CSteamID senderID, Vector3 position, float timestamp)
        {
            if (senderID != remotePlayerSteamID)
            {
                ApplySecondaryRemotePosition(senderID, position, timestamp);
                return;
            }
            
            // Check if we're in the intro phase or cutscene - if so, always accept position updates
            // During cutscenes, the game teleports the player and we need to track that
            bool isInIntroPhase = GameLoadSync.Instance?.IsInIntroPhase == true;
            bool useIntroFollowPositioning = ShouldUseIntroFollowPositioning();
            
            bool hasPlayerChar = MainGame.me?.player_char != null;
            bool controlEnabled = hasPlayerChar && MainGame.me.player_char.control_enabled;
            bool isInCutscene = hasPlayerChar && !controlEnabled;
            
            // Log during intro/cutscene phase for debugging
            if (isInIntroPhase || isInCutscene)
            {
                CoopMod.Logger.LogDebug($"[OnlineCoopManager] OnRemotePlayerPositionReceived: pos={position}, isInIntroPhase={isInIntroPhase}, isInCutscene={isInCutscene}, controlEnabled={controlEnabled}");
            }
            
            // During normal gameplay, only accept position updates when player has control
            // During intro phase or cutscene, always accept them so remote player follows teleports
            if (!isInIntroPhase && !isInCutscene)
            {
                // Normal gameplay - accept position
            }
            else if (!hasPlayerChar)
            {
                CoopMod.Logger.LogInfo($"[OnlineCoopManager] BLOCKED position update (no player character)");
                return;
            }
            
            // Check teleport lock - if active, ignore incoming positions
            if (teleportLockActive)
            {
                if (Time.time >= teleportLockEndTime)
                {
                    teleportLockActive = false;
                    CoopMod.Logger.LogInfo($"[OnlineCoopManager] Teleport lock released");
                }
                else
                {
                    CoopMod.Logger.LogInfo($"[OnlineCoopManager] BLOCKED position update due to teleport lock (expires in {teleportLockEndTime - Time.time:F1}s)");
                    return;
                }
            }
            
            Vector3 oldTarget = targetRemotePosition;

            if (!useIntroFollowPositioning && remotePlayer != null)
            {
                float snapDistance = Distance2D(remotePlayer.transform.position, position);
                if (!hasReceivedRemotePlayerPosition || snapDistance >= RemoteTeleportSnapDistance)
                {
                    string reason = !hasReceivedRemotePlayerPosition
                        ? "first network position"
                        : $"teleport-sized delta {snapDistance:F1}";
                    SnapRemotePlayerToNetworkPosition(position, reason);
                    return;
                }
            }

            targetRemotePosition = position;
            hasReceivedRemotePlayerPosition = true;
            
            // Push to GhostDriver for smooth interpolation.
            // Only the true INTRO phase forces the remote player to follow the local player visually.
            // All other cutscenes let the remote player render at their real network position.
            if (ghostDriver != null && !useIntroFollowPositioning)
            {
                ghostDriver.PushSnapshot(position, lastReceivedVelocity, timestamp);
            }
            
            if (isInIntroPhase)
            {
                CoopMod.Logger.LogInfo($"[OnlineCoopManager] INTRO: targetRemotePosition updated from {oldTarget} to {position}");
            }
        }

        private static float Distance2D(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dy = a.y - b.y;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }
        
        /// <summary>
        /// Called when we receive movement state update from the remote player
        /// </summary>
        private void OnRemotePlayerStateReceived(
            CSteamID senderID,
            Vector2 direction,
            bool isMoving,
            Vector2 velocity)
        {
            if (senderID != remotePlayerSteamID)
            {
                ApplySecondaryRemoteState(senderID, direction, isMoving, velocity);
                return;
            }

            lastReceivedVelocity = velocity;

            remoteDirection = direction.sqrMagnitude > 0.0001f ? direction : remoteDirection;
            remoteIsMoving = isMoving;
            
            // Reset idle timer when we receive moving=true
            if (remoteIsMoving)
            {
                remoteIdleTimer = 0f;
                
                // If remote player starts moving, they've stopped using tool
                if (remoteAnimIsToolActive ||
                    remoteAnimState == CharAnimState.Tool)
                {
                    ClearRemoteWorkAnimation();
                    CoopMod.Logger.LogInfo(
                        "[OnlineCoopManager] Remote player started moving — cleared tool animation state");
                }
            }
        }
        
        /// <summary>
        /// Called when we receive an animation state change from the remote player.
        /// This handles tool use, attack, fishing, etc. — anything beyond walk/idle.
        /// </summary>
        private void OnRemotePlayerAnimationReceived(CSteamID senderID, int animState, int itemType)
        {
            if (senderID != remotePlayerSteamID)
            {
                ApplySecondaryRemoteAnimation(senderID, animState, itemType);
                return;
            }
            
            CharAnimState incomingState = (CharAnimState)animState;
            ItemDefinition.ItemType incomingItemType =
                (ItemDefinition.ItemType)itemType;
            remoteActionExplicitlyStopped =
                incomingState == CharAnimState.Idle ||
                incomingState == CharAnimState.Walking;
            BaseCharacterComponent character =
                remotePlayer?.wgo?.components?.character;
            if (incomingState == remoteAnimState &&
                incomingItemType == remoteAnimItemType &&
                character?.anim_state == incomingState &&
                incomingState != CharAnimState.Idle)
            {
                return;
            }

            remoteAnimState = incomingState;
            remoteAnimItemType = incomingItemType;
            
            // Track whether a tool animation is active (to prevent walk/idle from overriding it)
            bool isTool = remoteAnimState == CharAnimState.Tool;
            bool isIdle = remoteAnimState == CharAnimState.Idle;
            
            if (isTool)
            {
                remoteAnimIsToolActive = true;
                CoopMod.Logger.LogDebug($"[OnlineCoopManager] Remote player TOOL animation: state={remoteAnimState}, itemType={remoteAnimItemType} (globalState={100 + (int)remoteAnimItemType})");
            }
            else if (isIdle)
            {
                remoteAnimIsToolActive = false;
                CoopMod.Logger.LogDebug("[OnlineCoopManager] Remote player back to Idle");
            }
            else
            {
                remoteAnimIsToolActive = remoteAnimState != CharAnimState.Walking;
                CoopMod.Logger.LogDebug($"[OnlineCoopManager] Remote player animation: state={remoteAnimState}, itemType={remoteAnimItemType}");
            }
            
            // Apply the animation state immediately
            ApplyRemoteAnimationState();
        }
        
        // Track remote player animation state (tool use, attack, etc.)
        private CharAnimState remoteAnimState = CharAnimState.Idle;
        private ItemDefinition.ItemType remoteAnimItemType = ItemDefinition.ItemType.None;
        private bool remoteAnimIsToolActive;
        // Vanilla StopUsingTool clears ToolComponent but can leave anim_state at
        // Tool. Do not let a slower parity/raw-visual packet resurrect that pose
        // after the reliable animation/work-stop signal.
        private bool remoteActionExplicitlyStopped;
        private string remoteRuntimeZoneId = string.Empty;
        private string remoteRuntimeSubZoneId = string.Empty;
        private bool remoteRuntimeIsInDungeon;
        private int remoteRuntimeDungeonLevel = -1;
        private string remoteRuntimeDungeonPresetName = string.Empty;
        private const float RuntimeTargetResolveDistance = 96f;
        private static readonly BindingFlags RuntimeFieldFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static FieldInfo remoteToolTargetObjField;
        private static FieldInfo remoteToolActionDelayField;
        private static FieldInfo remoteToolTargetStateChangedField;
        private static FieldInfo remoteToolDrivenByAnimEventField;
        private static FieldInfo remoteToolPlayingAnimationField;
        private static FieldInfo remoteToolIsUsingField;
        private static FieldInfo remoteToolTriedToStopField;
        private static FieldInfo remoteToolWasUsingField;
        private static FieldInfo remoteToolActionStartTimeField;
        private static FieldInfo remoteToolCurrentToolField;
        private static FieldInfo remoteAttackPerformingField;
        private static FieldInfo remoteAttackAnimBasedTimingField;
        private static FieldInfo remoteAttackSuccessedField;
        private static FieldInfo remoteAttackUsingItemField;
        private static FieldInfo remoteAttackStoppedTimeField;
        
        /// <summary>
        /// Apply a received animation state to the remote player's character.
        /// This drives the Unity Animator to show tool use, attack, etc.
        /// </summary>
        private void ApplyRemoteAnimationState()
        {
            if (remotePlayer?.wgo?.components?.character == null)
                return;
            
            var character = remotePlayer.wgo.components.character;
            
            // SetAnimationState internally calls SetGlobalState which sets the animator parameter
            // For Tool state: global_state = 100 + (int)itemType (e.g. 110 for Hand, 103 for Shovel)
            character.SetAnimationState(remoteAnimState, remoteAnimItemType);
            
            // For tool animations, also set the tool graphics (the visual tool sprite)
            if (remoteAnimState == CharAnimState.Tool)
            {
                character.SetToolGraphics((int)remoteAnimItemType);
            }
            else if (remoteAnimState == CharAnimState.Idle)
            {
                // Clear tool graphics when returning to idle
                character.SetToolGraphics(0);
            }
            
            CoopMod.Logger.LogDebug($"[OnlineCoopManager] Applied remote animation: state={remoteAnimState}, itemType={remoteAnimItemType}, toolActive={remoteAnimIsToolActive}");
        }

        public void ClearRemoteWorkAnimation()
        {
            ClearRemoteWorkAnimation(remotePlayerSteamID);
        }

        public void ClearRemoteWorkAnimation(CSteamID peer)
        {
            if (peer == CSteamID.Nil)
                return;

            if (peer != remotePlayerSteamID)
            {
                if (!secondaryRemotePeers.TryGetValue(
                        peer.m_SteamID,
                        out SecondaryRemotePeer secondary))
                {
                    return;
                }

                secondary.Animation = CharAnimState.Idle;
                secondary.ItemType = ItemDefinition.ItemType.None;
                secondary.ToolAnimationActive = false;
                secondary.ActionExplicitlyStopped = true;
                ClearToolVisual(secondary.Player?.wgo?.components?.character);
                return;
            }

            remoteAnimState = CharAnimState.Idle;
            remoteAnimItemType = ItemDefinition.ItemType.None;
            remoteAnimIsToolActive = false;
            remoteActionExplicitlyStopped = true;
            ClearToolVisual(remotePlayer?.wgo?.components?.character);
            CoopMod.Logger.LogInfo(
                "[OnlineCoopManager] Cleared remote work animation");
        }

        public void ReconcileRemoteActionVisual(CSteamID peer)
        {
            if (peer == remotePlayerSteamID)
            {
                if (remoteActionExplicitlyStopped)
                    ClearStaleToolVisual(
                        remotePlayer?.wgo?.components?.character,
                        remoteIsMoving);
                return;
            }

            if (secondaryRemotePeers.TryGetValue(
                    peer.m_SteamID,
                    out SecondaryRemotePeer secondary) &&
                secondary.ActionExplicitlyStopped)
            {
                ClearStaleToolVisual(
                    secondary.Player?.wgo?.components?.character,
                    secondary.Moving);
            }
        }

        private static void ClearToolVisual(BaseCharacterComponent character)
        {
            if (character == null)
                return;

            character.SetAnimationState(
                CharAnimState.Idle,
                ItemDefinition.ItemType.None);
            character.SetToolGraphics(0);
        }

        private static void ClearStaleToolVisual(
            BaseCharacterComponent character,
            bool isMoving)
        {
            PlayerComponent player = character?.player;
            bool toolVisible =
                player?.spr_tool?.sprite != null ||
                player?.spr_tool_2?.sprite != null;
            if (character == null ||
                (!toolVisible && character.anim_state != CharAnimState.Tool))
            {
                return;
            }

            if (character.anim_state == CharAnimState.Tool)
            {
                character.SetAnimationState(
                    isMoving ? CharAnimState.Walking : CharAnimState.Idle,
                    ItemDefinition.ItemType.None);
            }
            character.SetToolGraphics(0);
        }

        /// <summary>
        /// Apply the slower parity snapshot for action state that is not covered by
        /// position packets alone: tool/global animator state and carried overhead item.
        /// </summary>
        public void ApplyRemoteParityAction(
            CSteamID peer,
            int animState,
            int itemType,
            int globalState,
            Vector2 direction,
            bool hasOverhead,
            string overheadItemJson)
        {
            if (peer == remotePlayerSteamID)
            {
                ApplyRemoteParityAction(
                    animState,
                    itemType,
                    globalState,
                    direction,
                    hasOverhead,
                    overheadItemJson);
                return;
            }

            BaseCharacterComponent character = GetRemotePlayer(peer)
                ?.components?.character;
            if (!IsOnlineCoopEnabled || character == null)
                return;

            try
            {
                if (direction.sqrMagnitude > 0.0001f)
                {
                    character.direction = direction;
                    character.LookAt(direction);
                }

                CharAnimState state = (CharAnimState)animState;
                ItemDefinition.ItemType type =
                    (ItemDefinition.ItemType)itemType;
                bool suppressStoppedTool =
                    secondaryRemotePeers.TryGetValue(
                        peer.m_SteamID,
                        out SecondaryRemotePeer secondary) &&
                    secondary.ActionExplicitlyStopped &&
                    IsToolActionState(state, globalState);
                if (!suppressStoppedTool &&
                    state != CharAnimState.Idle &&
                    state != CharAnimState.Walking)
                {
                    if (secondary != null)
                        secondary.ActionExplicitlyStopped = false;
                    character.SetAnimationState(state, type);
                    int expectedGlobal = state == CharAnimState.Tool
                        ? 100 + (int)type
                        : (int)state;
                    if (globalState != expectedGlobal)
                        character.SetGlobalState(globalState);
                }

                ApplyRemoteOverheadItem(
                    character,
                    hasOverhead,
                    overheadItemJson);
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[MultiPeer] Failed to apply parity action for " +
                    $"{peer.m_SteamID}: {ex.Message}");
            }
        }

        public void ApplyRemoteParityAction(int animState, int itemType, int globalState, Vector2 direction, bool hasOverhead, string overheadItemJson)
        {
            if (!IsOnlineCoopEnabled || remotePlayer?.wgo?.components?.character == null)
                return;

            try
            {
                var character = remotePlayer.wgo.components.character;

                if (direction.sqrMagnitude > 0.0001f)
                {
                    remoteDirection = direction;
                    character.direction = direction;
                    character.LookAt(direction);
                    lastAppliedRemoteDirection = direction;
                }

                CharAnimState previousState = remoteAnimState;
                ItemDefinition.ItemType previousItemType = remoteAnimItemType;
                CharAnimState incomingState = (CharAnimState)animState;
                if (remoteActionExplicitlyStopped &&
                    IsToolActionState(incomingState, globalState))
                {
                    ApplyRemoteOverheadItem(
                        character,
                        hasOverhead,
                        overheadItemJson);
                    ClearStaleToolVisual(character, remoteIsMoving);
                    return;
                }

                if (incomingState != CharAnimState.Idle &&
                    incomingState != CharAnimState.Walking)
                {
                    remoteActionExplicitlyStopped = false;
                }
                remoteAnimState = incomingState;
                remoteAnimItemType = (ItemDefinition.ItemType)itemType;
                bool actionChanged =
                    previousState != remoteAnimState ||
                    previousItemType != remoteAnimItemType;
                remoteAnimIsToolActive = remoteAnimState != CharAnimState.Idle &&
                                         remoteAnimState != CharAnimState.Walking;

                int expectedGlobalState = remoteAnimState == CharAnimState.Tool
                    ? 100 + (int)remoteAnimItemType
                    : (int)remoteAnimState;

                if (globalState < 0 || globalState >= 100 || globalState != expectedGlobalState)
                {
                    remoteAnimIsToolActive = globalState != 0 && globalState != -1;
                }

                if (remoteAnimIsToolActive &&
                    (actionChanged || character.anim_state != remoteAnimState))
                {
                    ApplyRemoteAnimationState();
                }

                if (remoteAnimIsToolActive && globalState != expectedGlobalState)
                {
                    character.SetGlobalState(globalState);
                }

                ApplyRemoteOverheadItem(character, hasOverhead, overheadItemJson);
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[OnlineCoopManager] Failed to apply remote parity action: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies the reflected action/context phase state from PlayerParitySync so the
        /// remote avatar keeps the same work/attack timers and world context metadata.
        /// </summary>
        public void ApplyRemoteRuntimeContext(
            string zoneId,
            string subZoneId,
            bool isInDungeon,
            int dungeonLevel,
            string dungeonPresetName,
            bool controlEnabled,
            bool toolIsUsing,
            bool toolPlayingAnimation,
            bool toolDrivenByAnimEvent,
            bool toolTargetStateChanged,
            bool toolTriedToStop,
            bool toolWasUsing,
            int toolActionDelay,
            float toolActionElapsed,
            int toolCurrentType,
            long toolTargetUniqueId,
            string toolTargetObjId,
            string toolTargetCustomTag,
            Vector3 toolTargetPosition,
            bool attackPerforming,
            int attackType,
            bool attackAnimBasedTiming,
            bool attackUsingItem,
            bool attackSuccessed,
            float attackStoppedElapsed)
        {
            if (!IsOnlineCoopEnabled || remotePlayer?.wgo?.components?.character == null)
                return;

            try
            {
                WorldGameObject remoteWgo = remotePlayer.wgo;
                BaseCharacterComponent character = remoteWgo.components.character;
                character.control_enabled = controlEnabled;
                remoteWgo.cur_zone = zoneId ?? string.Empty;
                remoteRuntimeZoneId = zoneId ?? string.Empty;
                remoteRuntimeSubZoneId = subZoneId ?? string.Empty;
                remoteRuntimeIsInDungeon = isInDungeon;
                remoteRuntimeDungeonLevel = dungeonLevel;
                remoteRuntimeDungeonPresetName = dungeonPresetName ?? string.Empty;

                ApplyRemoteToolRuntime(
                    remoteWgo.components.tool,
                    toolIsUsing,
                    toolPlayingAnimation,
                    toolDrivenByAnimEvent,
                    toolTargetStateChanged,
                    toolTriedToStop,
                    toolWasUsing,
                    toolActionDelay,
                    toolActionElapsed,
                    toolCurrentType,
                    toolTargetUniqueId,
                    toolTargetObjId,
                    toolTargetCustomTag,
                    toolTargetPosition);

                ApplyRemoteAttackRuntime(
                    character.attack,
                    remoteWgo,
                    attackPerforming,
                    attackType,
                    attackAnimBasedTiming,
                    attackUsingItem,
                    attackSuccessed,
                    attackStoppedElapsed);
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[OnlineCoopManager] Failed to apply remote runtime context: {ex.Message}");
            }
        }

        private void ApplyRemoteToolRuntime(
            ToolComponent tool,
            bool toolIsUsing,
            bool toolPlayingAnimation,
            bool toolDrivenByAnimEvent,
            bool toolTargetStateChanged,
            bool toolTriedToStop,
            bool toolWasUsing,
            int toolActionDelay,
            float toolActionElapsed,
            int toolCurrentType,
            long toolTargetUniqueId,
            string toolTargetObjId,
            string toolTargetCustomTag,
            Vector3 toolTargetPosition)
        {
            if (tool == null)
                return;

            if (remoteActionExplicitlyStopped)
            {
                toolIsUsing = false;
                toolPlayingAnimation = false;
                toolDrivenByAnimEvent = false;
                toolTargetStateChanged = false;
                toolTriedToStop = false;
                toolWasUsing = false;
                toolActionDelay = 0;
                toolActionElapsed = -1f;
                toolCurrentType = (int)ItemDefinition.ItemType.None;
                toolTargetUniqueId = -1L;
                toolTargetObjId = string.Empty;
                toolTargetCustomTag = string.Empty;
            }

            WorldGameObject target = ResolveRuntimeTarget(toolTargetUniqueId, toolTargetCustomTag, toolTargetObjId, toolTargetPosition);
            SetRuntimeField(tool, GetRemoteToolTargetObjField(), target);
            SetRuntimeField(tool, GetRemoteToolIsUsingField(), toolIsUsing);
            SetRuntimeField(tool, GetRemoteToolPlayingAnimationField(), toolPlayingAnimation);
            SetRuntimeField(tool, GetRemoteToolDrivenByAnimEventField(), toolDrivenByAnimEvent);
            SetRuntimeField(tool, GetRemoteToolTargetStateChangedField(), toolTargetStateChanged);
            SetRuntimeField(tool, GetRemoteToolTriedToStopField(), toolTriedToStop);
            SetRuntimeField(tool, GetRemoteToolWasUsingField(), toolWasUsing);
            SetRuntimeField(tool, GetRemoteToolActionDelayField(), toolActionDelay);
            SetRuntimeField(tool, GetRemoteToolActionStartTimeField(), toolActionElapsed >= 0f ? Time.time - toolActionElapsed : -1f);
            SetRuntimeField(tool, GetRemoteToolCurrentToolField(), (ItemDefinition.ItemType)toolCurrentType);
        }

        private void ApplyRemoteAttackRuntime(
            BaseCharacterAttack attack,
            WorldGameObject remoteWgo,
            bool attackPerforming,
            int attackType,
            bool attackAnimBasedTiming,
            bool attackUsingItem,
            bool attackSuccessed,
            float attackStoppedElapsed)
        {
            if (attack == null)
                return;

            attack.cur_attack_type = attackType;
            SetRuntimeField(attack, GetRemoteAttackPerformingField(), attackPerforming);
            SetRuntimeField(attack, GetRemoteAttackAnimBasedTimingField(), attackAnimBasedTiming);
            SetRuntimeField(attack, GetRemoteAttackUsingItemField(), attackUsingItem);
            SetRuntimeField(attack, GetRemoteAttackSuccessedField(), attackSuccessed);
            SetRuntimeField(attack, GetRemoteAttackStoppedTimeField(), attackStoppedElapsed >= 0f ? Time.time - attackStoppedElapsed : -1f);

            try
            {
                var animator = remoteWgo?.components?.animator;
                if (animator != null)
                {
                    animator.SetFloat("attack_type_f", attackType);
                    animator.SetInteger("attack_type", attackPerforming ? attackType : 0);
                }
            }
            catch
            {
            }
        }

        private static void SetRuntimeField(object target, FieldInfo field, object value)
        {
            if (target == null || field == null)
                return;

            field.SetValue(target, value);
        }

        private static WorldGameObject ResolveRuntimeTarget(long uniqueId, string customTag, string objId, Vector3 position)
        {
            try
            {
                if (uniqueId > 0L)
                {
                    WorldGameObject byUniqueId = WorldMap.GetWorldGameObjectByUniqueId(uniqueId, false);
                    if (byUniqueId != null)
                        return byUniqueId;
                }

                if (!string.IsNullOrEmpty(customTag))
                {
                    WorldGameObject byTag = WorldMap.GetWorldGameObjectByCustomTag(customTag, true);
                    if (byTag != null)
                        return byTag;
                }

                if (!string.IsNullOrEmpty(objId) && MainGame.me != null)
                {
                    WorldGameObject nearest = null;
                    float nearestDistance = RuntimeTargetResolveDistance * RuntimeTargetResolveDistance;
                    var objects = WGORegistry.Instance?.SnapshotAll() ?? MainGame.me?.GetListOfWorldObjects();
                    for (int i = 0; i < objects.Count; i++)
                    {
                        WorldGameObject candidate = objects[i];
                        if (candidate == null || candidate.obj_id != objId)
                            continue;

                        float distance = (candidate.transform.position - position).sqrMagnitude;
                        if (distance < nearestDistance)
                        {
                            nearestDistance = distance;
                            nearest = candidate;
                        }
                    }

                    return nearest;
                }
            }
            catch
            {
            }

            return null;
        }

        private static FieldInfo GetRemoteToolTargetObjField() => remoteToolTargetObjField ?? (remoteToolTargetObjField = typeof(ToolComponent).GetField("_target_obj", RuntimeFieldFlags));
        private static FieldInfo GetRemoteToolActionDelayField() => remoteToolActionDelayField ?? (remoteToolActionDelayField = typeof(ToolComponent).GetField("_action_delay", RuntimeFieldFlags));
        private static FieldInfo GetRemoteToolTargetStateChangedField() => remoteToolTargetStateChangedField ?? (remoteToolTargetStateChangedField = typeof(ToolComponent).GetField("_target_state_changed", RuntimeFieldFlags));
        private static FieldInfo GetRemoteToolDrivenByAnimEventField() => remoteToolDrivenByAnimEventField ?? (remoteToolDrivenByAnimEventField = typeof(ToolComponent).GetField("_is_driven_by_anim_event", RuntimeFieldFlags));
        private static FieldInfo GetRemoteToolPlayingAnimationField() => remoteToolPlayingAnimationField ?? (remoteToolPlayingAnimationField = typeof(ToolComponent).GetField("_playing_animation", RuntimeFieldFlags));
        private static FieldInfo GetRemoteToolIsUsingField() => remoteToolIsUsingField ?? (remoteToolIsUsingField = typeof(ToolComponent).GetField("_is_using_tool", RuntimeFieldFlags));
        private static FieldInfo GetRemoteToolTriedToStopField() => remoteToolTriedToStopField ?? (remoteToolTriedToStopField = typeof(ToolComponent).GetField("_tried_to_stop", RuntimeFieldFlags));
        private static FieldInfo GetRemoteToolWasUsingField() => remoteToolWasUsingField ?? (remoteToolWasUsingField = typeof(ToolComponent).GetField("_was_using_tool", RuntimeFieldFlags));
        private static FieldInfo GetRemoteToolActionStartTimeField() => remoteToolActionStartTimeField ?? (remoteToolActionStartTimeField = typeof(ToolComponent).GetField("_action_start_time", RuntimeFieldFlags));
        private static FieldInfo GetRemoteToolCurrentToolField() => remoteToolCurrentToolField ?? (remoteToolCurrentToolField = typeof(ToolComponent).GetField("_current_tool", RuntimeFieldFlags));
        private static FieldInfo GetRemoteAttackPerformingField() => remoteAttackPerformingField ?? (remoteAttackPerformingField = typeof(BaseCharacterAttack).GetField("performing", RuntimeFieldFlags));
        private static FieldInfo GetRemoteAttackAnimBasedTimingField() => remoteAttackAnimBasedTimingField ?? (remoteAttackAnimBasedTimingField = typeof(BaseCharacterAttack).GetField("anim_based_timing", RuntimeFieldFlags));
        private static FieldInfo GetRemoteAttackSuccessedField() => remoteAttackSuccessedField ?? (remoteAttackSuccessedField = typeof(BaseCharacterAttack).GetField("successed", RuntimeFieldFlags));
        private static FieldInfo GetRemoteAttackUsingItemField() => remoteAttackUsingItemField ?? (remoteAttackUsingItemField = typeof(BaseCharacterAttack).GetField("using_item", RuntimeFieldFlags));
        private static FieldInfo GetRemoteAttackStoppedTimeField() => remoteAttackStoppedTimeField ?? (remoteAttackStoppedTimeField = typeof(BaseCharacterAttack).GetField("_stopped_time", RuntimeFieldFlags));

        private void ApplyRemoteOverheadItem(BaseCharacterComponent character, bool hasOverhead, string overheadItemJson)
        {
            if (!hasOverhead || string.IsNullOrEmpty(overheadItemJson))
            {
                if (character.has_overhead)
                {
                    character.SetOverheadItem(null);
                }
                return;
            }

            Item item = JsonUtility.FromJson<Item>(overheadItemJson);
            if (item == null || item.IsEmpty())
            {
                if (character.has_overhead)
                {
                    character.SetOverheadItem(null);
                }
                return;
            }

            Item current = character.GetOverheadItem();
            if (HasSameOverheadVisual(current, item))
            {
                return;
            }

            character.SetOverheadItem(item);
        }

        private static bool HasSameOverheadVisual(Item current, Item incoming)
        {
            if (current == null || incoming == null)
            {
                return false;
            }

            if (!string.Equals(current.id, incoming.id, System.StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    current.GetOverheadIcon(),
                    incoming.GetOverheadIcon(),
                    System.StringComparison.Ordinal);
            }
            catch
            {
                // The carried item id already identifies the visual in the normal
                // item path. Avoid replaying pickup audio merely because optional
                // icon metadata was unavailable on the remote proxy.
                return true;
            }
        }
        
        // Track remote player movement state
        private Vector2 remoteDirection = Vector2.down;
        private bool remoteIsMoving;
        private bool lastSentIsMoving;
        private Vector2 lastSentDirection = Vector2.down;
        
        // Track last applied state to avoid resetting every frame
        private bool lastAppliedRemoteIsMoving;
        private Vector2 lastAppliedRemoteDirection = Vector2.down;
        
        // Smoothing: delay before transitioning to idle (prevents flicker)
        private float remoteIdleTimer = 0f;
        private const float IDLE_DELAY = 0.15f; // Wait 150ms before showing idle
        
        // Logging throttle for intro phase to avoid spam
        private float lastIntroLogTime;
        private const float INTRO_LOG_INTERVAL = 0.5f;
        
        // Track control state to detect when player gains control after cutscene
        private bool wasControlDisabled;
        
        // Keepalive for heartbeat - send position even when idle to prevent false timeouts
        private float lastKeepAliveTime;
        private const float KEEPALIVE_INTERVAL = 3f; // Send keepalive every 3 seconds when idle
        
        private void Update()
        {
            long __profStart = GraveyardKeeperCoop.Utils.FrameProfiler.BeginSection();
            try
            {
                UpdateInternal();
            }
            finally
            {
                GraveyardKeeperCoop.Utils.FrameProfiler.EndSection("OCM.Update", __profStart);
            }
        }

        private void UpdateInternal()
        {
            TickStuckControlWatchdog();
            UpdateSessionChatOverlayLifecycle();

            if (!IsOnlineCoopEnabled)
            {
                // Check if we should auto-enable online coop
                // But don't auto-re-enable right after a timeout disconnect
                if (Time.realtimeSinceStartup - lastTimeoutDisconnectTime > AUTO_ENABLE_COOLDOWN)
                {
                    CheckAutoEnable();
                }
                return;
            }

            TickSecondaryRemotePeers();
            
            NpcInteractionSyncPatches.TickRemoteDonkeyStateSync();
            CutsceneSyncPatches.TickRemoteCutsceneSession();

            // Check if we're in the intro phase OR in a cutscene (control disabled)
            // Both require position sync so remote player follows teleports
            bool isInIntroPhase = GameLoadSync.Instance?.IsInIntroPhase == true;
            
            // Lobby-based disconnect detection: periodically check if remote player is still in the lobby
            // This is more reliable than packet-based heartbeat since idle players don't send packets
            if (Time.realtimeSinceStartup - lastLobbyCheckTime >= LOBBY_CHECK_INTERVAL)
            {
                lastLobbyCheckTime = Time.realtimeSinceStartup;
                bool remoteStillInLobby = IsRemotePlayerInLobby();
                
                if (!remoteStillInLobby)
                {
                    if (!remotePlayerMissingFromLobby)
                    {
                        // First time noticing they're missing
                        remotePlayerMissingFromLobby = true;
                        remotePlayerMissingSince = Time.realtimeSinceStartup;
                        CoopMod.Logger.LogWarning("[OnlineCoopManager] Remote player not found in lobby! Starting disconnect timer...");
                    }
                    else if (Time.realtimeSinceStartup - remotePlayerMissingSince >= LOBBY_DISCONNECT_TIMEOUT)
                    {
                        // They've been missing for too long
                        CoopMod.Logger.LogWarning($"[OnlineCoopManager] LOBBY DISCONNECT: Remote player missing from lobby for {LOBBY_DISCONNECT_TIMEOUT}s! Treating as disconnected.");
                        lastTimeoutDisconnectTime = Time.realtimeSinceStartup;
                        
                        if (!IsHost)
                        {
                            ShowDisconnectDialogAndReturnToTitle("Lost connection to host.");
                        }
                        else
                        {
                            CSteamID missingPeer = remotePlayerSteamID;
                            CoopMod.Logger.LogInfo(
                                "[OnlineCoopManager] Primary client left lobby; " +
                                "cleaning up only that peer...");
                            HandlePeerLeftLobby(
                                missingPeer,
                                SteamFriends.GetFriendPersonaName(missingPeer));
                        }
                        return;
                    }
                }
                else
                {
                    // They're back or still here
                    if (remotePlayerMissingFromLobby)
                    {
                        CoopMod.Logger.LogInfo("[OnlineCoopManager] Remote player found in lobby again.");
                    }
                    remotePlayerMissingFromLobby = false;
                }
            }
            bool hasPlayerChar = MainGame.me?.player_char != null;
            bool controlEnabled = hasPlayerChar && MainGame.me.player_char.control_enabled;
            bool useIntroFollowPositioning = ShouldUseIntroFollowPositioning();
            
            // Detect when player gains control after a cutscene (control transitions from disabled to enabled)
            // This is when we should end the intro phase
            if (isInIntroPhase && !useIntroFollowPositioning && controlEnabled)
            {
                CoopMod.Logger.LogInfo("[OnlineCoopManager] Ending intro phase for sidecar-seeded existing save");
                GameLoadSync.Instance?.EndIntroPhase();
                isInIntroPhase = false;
                useIntroFollowPositioning = false;
            }
            else if (wasControlDisabled && controlEnabled && useIntroFollowPositioning)
            {
                CoopMod.Logger.LogInfo("[OnlineCoopManager] Player gained control - ending intro phase (control transition detected)");
                GameLoadSync.Instance?.EndIntroPhase();
                isInIntroPhase = false;
                useIntroFollowPositioning = false;
            }
            wasControlDisabled = !controlEnabled;
            
            // DISABLED: the previous 5s auto-end fallback fired during the brief controllable
            // window between the intro animation and the dark-zone scene (camera_in_the_dark),
            // which broke the dark-zone follow logic in LateUpdate. The control-transition
            // detector above correctly ends intro phase when the dark scene finishes and the
            // player regains control for real. Existing-save lobby loads can still trip
            // ShowIntro, but sidecar-seeded loads are ended above and do not use dark-zone
            // follow positioning.
            //
            // If we ever discover a code path that leaves intro phase stuck TRUE, use a very
            // long conservative delay (>= 60s, covers full intro + dark scene) and require that
            // we've observed at least one disabled->enabled transition first (i.e. the dark
            // scene actually played) before auto-ending.
            // During cutscenes (control disabled), we're in "cutscene mode" - always sync positions
            bool isInCutscene = !controlEnabled;
            
            // Log during intro/cutscene phase for debugging (throttled)
            if ((useIntroFollowPositioning || isInCutscene) && Time.time - lastIntroLogTime >= INTRO_LOG_INTERVAL)
            {
                lastIntroLogTime = Time.time;
                Vector3 localPos = localPlayer != null ? localPlayer.transform.position : Vector3.zero;
                CoopMod.Logger.LogDebug($"[OnlineCoopManager] Update: useIntroFollowPositioning={useIntroFollowPositioning}, isInIntroPhase={isInIntroPhase}, isInCutscene={isInCutscene}, controlEnabled={controlEnabled}, localPlayer={localPlayer != null}, pos={localPos}");
            }
            
            // During normal gameplay, don't sync positions until player has control
            // During intro phase or cutscene, always sync so remote player follows teleports
            if (!useIntroFollowPositioning && !isInCutscene)
            {
                // Normal gameplay - position sync handled below
            }
            else if (!useIntroFollowPositioning && isInCutscene && !hasPlayerChar)
            {
                return;
            }
            
            // Send our position and movement state to remote player
            // During intro phase or cutscene, use 30Hz cap (not per-frame) to catch teleports without flooding
            bool shouldSendNow;
            if (useIntroFollowPositioning || isInCutscene)
            {
                shouldSendNow = Time.time - lastCutsceneSyncTime >= cutsceneSyncInterval;
            }
            else
            {
                shouldSendNow = Time.time - lastPositionSyncTime >= positionSyncInterval;
            }
            
            if (localPlayer != null && shouldSendNow)
            {
                Vector3 currentPos = localPlayer.transform.position;
                
                // Get movement state from local player
                Vector2 currentDir = Vector2.down;
                bool isMoving = false;
                Vector3 positionDelta = currentPos - lastSentPosition;
                bool scriptedPositionMoving = (useIntroFollowPositioning || isInCutscene) &&
                                              hasSentPositionSample &&
                                              positionDelta.sqrMagnitude > 0.0004f &&
                                              positionDelta.sqrMagnitude < 128f * 128f;
                
                if (localPlayer.wgo?.components?.character != null)
                {
                    var character = localPlayer.wgo.components.character;
                    currentDir = character.direction;
                    // Trust the local character's actual walking animation for walk/idle sync.
                    // movement_dir can briefly retain/decay independent of the animation state.
                    isMoving = character.anim_state == CharAnimState.Walking &&
                               character.movement_dir.sqrMagnitude > 0.0001f;
                }

                if (scriptedPositionMoving)
                {
                    isMoving = true;
                    Vector2 scriptedDirection = new Vector2(positionDelta.x, positionDelta.y);
                    if (scriptedDirection.sqrMagnitude > 0.0001f)
                    {
                        currentDir = scriptedDirection.normalized;
                    }
                }
                
                // Determine whether to send this tick:
                // - While moving: ALWAYS send at tick rate (no threshold) for smooth curves
                // - While stopped: only send on state/direction changes or position drift
                // - During the intro phase: send on any tiny position change so the dark-zone follow stays tight
                bool introPositionChanged = useIntroFollowPositioning && Vector3.Distance(currentPos, lastSentPosition) > 0.01f;
                bool stoppedStateChanged = !isMoving && (isMoving != lastSentIsMoving || 
                                   Vector2.Distance(currentDir, lastSentDirection) > 0.1f ||
                                   Vector3.Distance(currentPos, lastSentPosition) > 0.1f);
                bool shouldSend = isMoving || stoppedStateChanged || introPositionChanged;
                
                if (shouldSend)
                {
                    if (useIntroFollowPositioning)
                    {
                        CoopMod.Logger.LogInfo($"[OnlineCoopManager] INTRO sending pos: {currentPos} (lastSent: {lastSentPosition}, delta: {Vector3.Distance(currentPos, lastSentPosition):F2})");
                    }
                    
                    Vector2 velocity = isMoving ? CalculateSentPositionVelocity(currentPos) : Vector2.zero;
                    
                    Network.SteamP2PManager.Instance?.SendPositionWithState(currentPos, currentDir, isMoving, velocity);
                    RememberSentPositionSample(currentPos);
                    lastSentPosition = currentPos;
                    lastSentIsMoving = isMoving;
                    lastSentDirection = currentDir;
                }
                
                // Update throttle timer
                if (useIntroFollowPositioning || isInCutscene)
                {
                    lastCutsceneSyncTime = Time.time;
                }
                else
                {
                    lastPositionSyncTime = Time.time;
                }
                lastKeepAliveTime = Time.realtimeSinceStartup;
            }
            else if (!useIntroFollowPositioning && !isInCutscene && localPlayer != null && 
                     Time.realtimeSinceStartup - lastKeepAliveTime >= KEEPALIVE_INTERVAL)
            {
                // Keepalive: send current position even when idle to prevent heartbeat timeout
                Vector3 currentPos = localPlayer.transform.position;
                Vector2 currentDir = Vector2.down;
                bool isMoving = false;
                
                if (localPlayer.wgo?.components?.character != null)
                {
                    var character = localPlayer.wgo.components.character;
                    currentDir = character.direction;
                    isMoving = character.anim_state == CharAnimState.Walking &&
                               character.movement_dir.sqrMagnitude > 0.0001f;
                }
                
                Vector2 velocity = isMoving ? CalculateSentPositionVelocity(currentPos) : Vector2.zero;
                
                Network.SteamP2PManager.Instance?.SendPositionWithState(currentPos, currentDir, isMoving, velocity);
                RememberSentPositionSample(currentPos);
                lastSentPosition = currentPos;
                lastSentIsMoving = isMoving;
                lastSentDirection = currentDir;
                lastKeepAliveTime = Time.realtimeSinceStartup;
            }
            
            // Immediate stop packet: when transitioning from moving -> stopped,
            // send right away (bypass tick throttle) so remote player stops promptly
            if (localPlayer != null && !useIntroFollowPositioning && !isInCutscene && lastSentIsMoving)
            {
                bool currentlyMoving = false;
                if (localPlayer.wgo?.components?.character != null)
                {
                    var character = localPlayer.wgo.components.character;
                    currentlyMoving = character.anim_state == CharAnimState.Walking &&
                                      character.movement_dir.sqrMagnitude > 0.0001f;
                }
                if (!currentlyMoving)
                {
                    Vector3 currentPos2 = localPlayer.transform.position;
                    Vector2 currentDir2 = localPlayer.wgo?.components?.character?.direction ?? Vector2.down;
                    Network.SteamP2PManager.Instance?.SendPositionWithState(currentPos2, currentDir2, false, Vector2.zero);
                    RememberSentPositionSample(currentPos2);
                    lastSentPosition = currentPos2;
                    lastSentIsMoving = false;
                    lastSentDirection = currentDir2;
                    lastPositionSyncTime = Time.time;
                }
            }
            
            // Update remote player position and animation
            if (remotePlayer != null)
            {
                // Update remote player animation state
                UpdateRemotePlayerAnimation();
                
                // Update remote player's map indicator position
                Vector3 mapPosition = hasReceivedRemotePlayerPosition
                    ? targetRemotePosition
                    : remotePlayer.transform.position;
                Patches.MapGUIPatches.UpdateRemotePlayerIndicator(
                    remotePlayerSteamID.m_SteamID.ToString(),
                    remotePlayerDisplayName ?? "Player 2",
                    mapPosition,
                    new Color(0.3f, 0.5f, 1f, 1f) // Blue for remote player
                );
            }
        }

        private void UpdateSessionChatOverlayLifecycle()
        {
            if (CanShowSessionChatOverlay())
            {
                EnsureSessionChatOverlayShown();
                return;
            }

            if (sessionChatOverlayShown)
            {
                UI.ChatOverlay.Instance?.Hide();
                sessionChatOverlayShown = false;
                nextSessionChatOverlayAttemptTime = 0f;
            }
        }

        private void EnsureSessionChatOverlayShown()
        {
            if (!CanShowSessionChatOverlay())
                return;

            // Some scene transitions rebuild the UI hierarchy. A stale lifecycle
            // flag must not prevent recreation after Unity destroys the old overlay.
            if (sessionChatOverlayShown && UI.ChatOverlay.Instance != null)
                return;

            sessionChatOverlayShown = false;

            if (Time.realtimeSinceStartup < nextSessionChatOverlayAttemptTime)
                return;

            nextSessionChatOverlayAttemptTime = Time.realtimeSinceStartup + 1f;

            var overlay = UI.ChatOverlay.Instance ?? UI.ChatOverlay.Create();
            if (overlay == null)
                return;

            overlay.Show();
            sessionChatOverlayShown = true;
            CoopMod.Logger.LogInfo("[OnlineCoopManager] Chat overlay shown for multiplayer session");
        }

        private static bool CanShowSessionChatOverlay()
        {
            // MainGame.game_started is restored early by some menu/load patches,
            // before the native loading screen actually leaves. LoadingGUI is the
            // authoritative presentation gate: creating the overlay while it is
            // shown makes chat render on top of save transfer and scene loading.
            return LobbyGUI.IsMultiplayerSessionActive &&
                   MainGame.game_started &&
                   MainGame.me?.player != null &&
                   SteamLobbyManager.Instance?.IsInLobby == true &&
                   !LoadingGUI.is_shown;
        }

        private Vector2 CalculateSentPositionVelocity(Vector3 currentPosition)
        {
            if (!hasSentPositionSample)
                return Vector2.zero;

            float elapsed = Time.realtimeSinceStartup - lastSentPositionSampleTime;
            if (elapsed <= 0.0001f)
                return Vector2.zero;

            Vector3 delta = currentPosition - lastSentPosition;
            if (delta.sqrMagnitude > 128f * 128f)
                return Vector2.zero;

            return new Vector2(delta.x, delta.y) / elapsed;
        }

        private void RememberSentPositionSample(Vector3 currentPosition)
        {
            lastSentPosition = currentPosition;
            lastSentPositionSampleTime = Time.realtimeSinceStartup;
            hasSentPositionSample = true;
        }

        private void ResetPositionSyncState()
        {
            lastSentPosition = Vector3.zero;
            lastSentPositionSampleTime = 0f;
            hasSentPositionSample = false;
            lastSentIsMoving = false;
            lastSentDirection = Vector2.down;
            lastReceivedVelocity = Vector2.zero;
            remoteDirection = Vector2.down;
            remoteIsMoving = false;
            remoteAnimState = CharAnimState.Idle;
            remoteAnimItemType = ItemDefinition.ItemType.None;
            remoteAnimIsToolActive = false;
            remoteActionExplicitlyStopped = false;
            lastAppliedRemoteIsMoving = false;
            lastAppliedRemoteDirection = Vector2.down;
            remoteIdleTimer = IDLE_DELAY;
            AnimationSyncPatch.Reset();
        }
        
        // Track for cutscene position logging throttle in LateUpdate
        private float lastLateUpdateIntroLogTime;
        
        // Track last known local player position to detect teleports
        private Vector3 lastLocalPlayerPosition;
        
        /// <summary>
        /// LateUpdate runs after all Update calls - better for visual position updates
        /// This prevents camera jitter by ensuring positions are stable when camera updates
        /// </summary>
        private void LateUpdate()
        {
            if (!IsOnlineCoopEnabled || remotePlayer == null)
                return;
            
            // Drive the cutscene walk-to state machine (independent of remote player position logic)
            TickCutsceneWalk();
            TickObservedCutsceneFollow();
            
            // Check teleport lock - if active, check for expiry and don't override the teleported position
            if (teleportLockActive)
            {
                if (Time.time >= teleportLockEndTime)
                {
                    teleportLockActive = false;
                    CoopMod.Logger.LogInfo($"[OnlineCoopManager] LateUpdate: Teleport lock released");
                }
                else
                {
                    // Only the INTRO phase (dark zone) cancels the teleport lock so the
                    // dark-zone follow code below can override the remote position.
                    // During regular gameplay AND non-intro cutscenes, respect the lock.
                    bool lockIsInIntro = ShouldUseIntroFollowPositioning();
                    
                    if (lockIsInIntro)
                    {
                        teleportLockActive = false;
                        CoopMod.Logger.LogInfo("[OnlineCoopManager] Teleport lock cancelled - INTRO phase positioning takes priority");
                    }
                    else
                    {
                        // Normal gameplay or non-intro cutscene - keep the teleport lock and maintain position
                        ForceRemotePlayerVisible();
                        return;
                    }
                }
            }
            
            // Check if we're in the intro phase OR in a cutscene (control disabled)
            bool isInIntroPhase = GameLoadSync.Instance?.IsInIntroPhase == true;
            bool useIntroFollowPositioning = ShouldUseIntroFollowPositioning();
            bool hasPlayerChar = MainGame.me?.player_char != null;
            bool controlEnabled = hasPlayerChar && MainGame.me.player_char.control_enabled;
            bool isInCutscene = hasPlayerChar && !controlEnabled;
            
            // During normal gameplay, don't update positions until player has control.
            // Only the INTRO phase (dark zone) forces position updates while control is disabled —
            // generic cutscenes let GhostDriver handle remote position from the network.
            if (!isInIntroPhase && !controlEnabled)
            {
                return;
            }
            
            Vector3 currentPos = remotePlayer.transform.position;
            Vector3 newPos;
            
            // During a true INTRO phase ONLY, position the remote player NEXT TO the local player.
            // The intro "in the dark" scene uses a fixed camera and teleports the local player around;
            // network positions don't work because each player's camera only sees their own area.
            // Existing-save lobby loads also trip the intro flag, but they have sidecar positions;
            // those must keep using normal network positions instead of snapping next to local.
            //
            // For ALL other cutscenes (Gerry dig-up, etc.), the remote player must stay at their real
            // network position. The CutsceneWalkTo system makes the absent player walk over naturally.
            if (useIntroFollowPositioning && localPlayer != null)
            {
                Vector3 localPos = localPlayer.transform.position;
                
                // Detect teleports: if local player moved more than 50 units, it's a teleport
                // Immediately snap the remote player to follow
                float localMoveDelta = Vector3.Distance(localPos, lastLocalPlayerPosition);
                bool wasTeleported = localMoveDelta > 50f;
                
                // Position remote player next to local player with camera-aware offset.
                // The offset must be large enough in SCREEN PIXELS to see two distinct characters.
                // At orthoSize=810 (dark zone), 1.5 world units = ~1 pixel, so we need much more.
                float cutsceneOffset = CalculateCameraAwareOffset();
                Vector3 offset = new Vector3(cutsceneOffset, 0f, 0f);
                newPos = localPos + offset;
                
                if (wasTeleported)
                {
                    CoopMod.Logger.LogInfo($"[OnlineCoopManager] CUTSCENE TELEPORT detected! Local moved {localMoveDelta:F1} units. Snapping remote to {newPos}");
                    
                    // IMMEDIATELY snap position on ALL transforms - bypass any interpolation
                    remotePlayer.transform.position = newPos;
                    if (remotePlayer.wgo != null)
                    {
                        remotePlayer.wgo.transform.position = newPos;
                    }
                    
                    // Recalculate chunk after teleport to ensure proper rendering/fog state
                    try
                    {
                        var chunked = remotePlayer.GetComponent<ChunkedGameObject>();
                        if (chunked != null)
                        {
                            chunked.RecalculateChunk();
                        }
                    }
                    catch { }
                    
                    // Re-disable AILerp in case it got re-enabled
                    DisableAIPathfinding();
                }
                
                lastLocalPlayerPosition = localPos;
                
                // Force visibility during cutscene - the dark scene might hide the player
                ForceRemotePlayerVisible();
                
                // One-time detailed render pipeline dump when entering cutscene
                if (!hasLoggedCutsceneRenderDump && isInCutscene)
                {
                    hasLoggedCutsceneRenderDump = true;
                    LogDetailedRenderState("CUTSCENE_ENTER");
                }
                
                // Periodic render diagnostics during cutscene (every 5s)
                if (Time.time - lastRenderDiagTime >= RENDER_DIAG_INTERVAL)
                {
                    lastRenderDiagTime = Time.time;
                    LogDetailedRenderState("PERIODIC");
                }
                
                // Slim cutscene position+state log every 2s for debugging
                if (Time.time - lastLateUpdateIntroLogTime >= 2f)
                {
                    lastLateUpdateIntroLogTime = Time.time;
                    float orthoSize = Camera.main != null ? Camera.main.orthographicSize : -1f;
                    CoopMod.Logger.LogInfo($"[DARKZONE] pos=({localPos.x:F0},{localPos.y:F0}) remote=({currentPos.x:F0},{currentPos.y:F0}) dist={Vector3.Distance(localPos, currentPos):F1} offset={cutsceneOffset:F1} orthoSize={orthoSize:F0} active={remotePlayer.gameObject.activeInHierarchy} intro={isInIntroPhase} cutscene={isInCutscene}");
                }
                

            }
            else if (useIntroFollowPositioning)
            {
                // INTRO phase fallback: no local player reference - use network position with camera-aware offset
                float fallbackOffset = CalculateCameraAwareOffset();
                newPos = targetRemotePosition + new Vector3(fallbackOffset, 0f, 0f);
                ForceRemotePlayerVisible();
            }
            else
            {
                // During normal gameplay, GhostDriver handles smooth interpolation
                if (ghostDriver != null && ghostDriver.IsEnabled)
                {
                    // Reset cutscene dump flag when leaving cutscene
                    hasLoggedCutsceneRenderDump = false;
                    
                    // GhostDriver is the SOLE authority on position during normal gameplay.
                    // Just sync wgo.transform to match what GhostDriver set in Update(),
                    // then return — do NOT do any additional position writes.
                    if (remotePlayer.wgo != null)
                    {
                        remotePlayer.wgo.transform.position = remotePlayer.transform.position;
                    }
                    
                    // Periodic diagnostic logging
                    if (Time.time - lastDiagnosticLogTime >= DIAGNOSTIC_LOG_INTERVAL)
                    {
                        LogCameraDiagnostics();
                        lastDiagnosticLogTime = Time.time;
                        positionUpdatesThisInterval = 0;
                        maxPositionDeltaThisInterval = 0f;
                    }
                    return;
                }
                else
                {
                    // Fallback: smoothly move to target position (old method, when GhostDriver is off)
                    float step = positionMoveSpeed * Time.deltaTime;
                    newPos = Vector3.MoveTowards(currentPos, targetRemotePosition, step);
                }
            }
            
            float positionDelta = Vector3.Distance(currentPos, newPos);
            
            // Track diagnostics
            if (positionDelta > 0.001f)
            {
                positionUpdatesThisInterval++;
                if (positionDelta > maxPositionDeltaThisInterval)
                    maxPositionDeltaThisInterval = positionDelta;
                
                // Set position on both the PlayerComponent transform AND the wgo transform
                remotePlayer.transform.position = newPos;
                
                if (remotePlayer.wgo != null)
                {
                    remotePlayer.wgo.transform.position = newPos;
                }
                
                // During cutscenes the transform write above is the authoritative snap.
                // Do not also call Rigidbody.MovePosition: the game can switch the remote
                // body to Static while control is disabled, which makes Unity emit costly
                // "Cannot use MovePosition on a static body" warnings during cutscene motion.
            }
            
            // Periodic diagnostic logging
            if (Time.time - lastDiagnosticLogTime >= DIAGNOSTIC_LOG_INTERVAL)
            {
                LogCameraDiagnostics();
                lastDiagnosticLogTime = Time.time;
                positionUpdatesThisInterval = 0;
                maxPositionDeltaThisInterval = 0f;
            }
        }
        
        // Track last visual state log time to avoid spam
        // Track last sorting layer sync time during cutscenes
        private float lastSortingLayerSyncTime;
        private const float SORTING_LAYER_SYNC_INTERVAL = 1f;
        
        // Track whether we've done the one-time detailed render dump for cutscene
        private bool hasLoggedCutsceneRenderDump;
        
        // Track last render diagnostic time (separate from visual state)
        private float lastRenderDiagTime;
        private const float RENDER_DIAG_INTERVAL = 5f;
        
        
        /// <summary>
        /// Calculate a camera-aware offset that ensures ~40 screen pixels of visible separation
        /// between the local and remote player, regardless of camera zoom level.
        /// At orthoSize=810 (dark zone), this gives ~60 world units instead of the old 1.5.
        /// </summary>
        private float CalculateCameraAwareOffset()
        {
            const float DESIRED_PIXEL_SEPARATION = 40f;
            
            var cam = Camera.main;
            if (cam == null)
                return 50f; // Safe fallback
            
            float viewportWidth = cam.orthographicSize * 2f * cam.aspect;
            float screenWidth = Screen.width > 0 ? Screen.width : 1920f;
            float worldUnitsPerPixel = viewportWidth / screenWidth;
            float offset = DESIRED_PIXEL_SEPARATION * worldUnitsPerPixel;
            
            // Clamp to reasonable range: at least 3 units, at most 120 units
            return Mathf.Clamp(offset, 3f, 120f);
        }

        /// <summary>
        /// Force the remote player to be visible during intro/cutscenes
        /// The game's "in the dark" scene and other cutscenes may hide or disable player objects
        /// </summary>
        private void ForceRemotePlayerVisible()
        {
            if (remotePlayer == null) return;
            
            try
            {
                // Ensure GameObject is active
                if (!remotePlayer.gameObject.activeInHierarchy)
                {
                    remotePlayer.gameObject.SetActive(true);
                    CoopMod.Logger.LogInfo("[OnlineCoopManager] Re-activated remote player GameObject");
                }
                
                // Ensure ChunkedGameObject is visible
                var chunked = remotePlayer.GetComponent<ChunkedGameObject>();
                if (chunked != null)
                {
                    if (!chunked.always_active)
                    {
                        chunked.always_active = true;
                    }
                    if (!chunked.obj_visible)
                    {
                        chunked.obj_visible = true;
                        chunked.UpdateVisibility();
                    }
                }
                
                // Force enable all SpriteRenderers - the dark scene might disable them
                var spriteRenderers = remotePlayer.GetComponentsInChildren<SpriteRenderer>(true);
                int enabledCount = 0;
                int disabledCount = 0;
                int transparentCount = 0;
                
                foreach (var sr in spriteRenderers)
                {
                    if (sr.enabled)
                        enabledCount++;
                    else
                    {
                        disabledCount++;
                        sr.enabled = true;
                    }
                    
                    // Ensure they're not completely transparent
                    if (sr.color.a < 0.1f)
                    {
                        transparentCount++;
                        var c = sr.color;
                        c.a = 1f;
                        sr.color = c;
                    }
                }
                

                
                // Force enable all Renderers in general
                var renderers = remotePlayer.GetComponentsInChildren<Renderer>(true);
                foreach (var r in renderers)
                {
                    if (!r.enabled)
                    {
                        r.enabled = true;
                    }
                }
                
                // Periodically sync sorting layers from local player during cutscenes
                // The dark zone or other cutscenes may change sorting layers dynamically
                if (Time.time - lastSortingLayerSyncTime >= SORTING_LAYER_SYNC_INTERVAL)
                {
                    lastSortingLayerSyncTime = Time.time;
                    CopySortingLayerFromLocalPlayer();
                }
                
                // CRITICAL: Ensure remote player is on the same Unity Layer as local player
                // The camera's culling mask only renders specific layers - if the remote player
                // is on a different layer, the camera won't render it at all!
                if (localPlayer != null && remotePlayer.gameObject.layer != localPlayer.gameObject.layer)
                {
                    int targetLayer = localPlayer.gameObject.layer;
                    CoopMod.Logger.LogInfo($"[ForceVisible] Remote player layer {remotePlayer.gameObject.layer} differs from local {targetLayer}, fixing!");
                    SetLayerRecursive(remotePlayer.gameObject, targetLayer);
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[OnlineCoopManager] ForceRemotePlayerVisible error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Log comprehensive render pipeline state for debugging invisible remote player.
        /// Checks: sprite null, camera culling, Unity layer, Animator, SortingGroup, material, Renderer.isVisible
        /// </summary>
        private void LogDetailedRenderState(string context)
        {
            if (remotePlayer == null || localPlayer == null) return;
            
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[RENDER_DIAG] === {context} ===");
                
                // Camera-aware offset diagnostic
                float calcOffset = CalculateCameraAwareOffset();
                sb.AppendLine($"[RENDER_DIAG] CameraAwareOffset={calcOffset:F1} worldUnits (target: ~40 screen pixels)");
                
                // 1. Unity Layer check (camera culling mask)
                int localLayer = localPlayer.gameObject.layer;
                int remoteLayer = remotePlayer.gameObject.layer;
                sb.AppendLine($"[RENDER_DIAG] Unity Layer: local={localLayer} ({LayerMask.LayerToName(localLayer)}), remote={remoteLayer} ({LayerMask.LayerToName(remoteLayer)})");
                
                var cam = Camera.main;
                if (cam != null)
                {
                    int cullingMask = cam.cullingMask;
                    bool localInMask = (cullingMask & (1 << localLayer)) != 0;
                    bool remoteInMask = (cullingMask & (1 << remoteLayer)) != 0;
                    sb.AppendLine($"[RENDER_DIAG] Camera cullingMask={cullingMask}, localInMask={localInMask}, remoteInMask={remoteInMask}");
                    sb.AppendLine($"[RENDER_DIAG] Camera pos={cam.transform.position}, orthoSize={cam.orthographicSize}");
                    
                    // FIX: If remote is not in camera mask, copy local layer
                    if (!remoteInMask && localInMask && remoteLayer != localLayer)
                    {
                        sb.AppendLine($"[RENDER_DIAG] FIXING: Remote layer {remoteLayer} not in camera mask! Changing to {localLayer}");
                        SetLayerRecursive(remotePlayer.gameObject, localLayer);
                    }
                }
                
                // 2. SortingGroup check
                var localSG = localPlayer.GetComponent<UnityEngine.Rendering.SortingGroup>();
                var remoteSG = remotePlayer.GetComponent<UnityEngine.Rendering.SortingGroup>();
                sb.AppendLine($"[RENDER_DIAG] SortingGroup: local={localSG != null}, remote={remoteSG != null}");
                if (localSG != null)
                    sb.AppendLine($"[RENDER_DIAG] Local SortingGroup: layer={localSG.sortingLayerName}, order={localSG.sortingOrder}");
                if (remoteSG != null)
                    sb.AppendLine($"[RENDER_DIAG] Remote SortingGroup: layer={remoteSG.sortingLayerName}, order={remoteSG.sortingOrder}");
                
                // 3. Character component check (drives animations/sprites)
                var localChar2 = localPlayer.wgo?.components?.character;
                var remoteChar2 = remotePlayer.wgo?.components?.character;
                sb.AppendLine($"[RENDER_DIAG] Character: local={localChar2 != null}, remote={remoteChar2 != null}");
                if (localChar2 != null)
                    sb.AppendLine($"[RENDER_DIAG] Local Character: enabled={localChar2.enabled}, direction={localChar2.direction}, anim_direction={localChar2.anim_direction}");
                if (remoteChar2 != null)
                    sb.AppendLine($"[RENDER_DIAG] Remote Character: enabled={remoteChar2.enabled}, direction={remoteChar2.direction}, anim_direction={remoteChar2.anim_direction}");
                
                // Check wgo.wop (the visual parts gameObject)
                var localWop = localPlayer.wgo?.wop;
                var remoteWop = remotePlayer.wgo?.wop;
                sb.AppendLine($"[RENDER_DIAG] WOP (visual parts): local={localWop != null} (active={localWop?.gameObject?.activeInHierarchy}), remote={remoteWop != null} (active={remoteWop?.gameObject?.activeInHierarchy})");
                
                // 4. Per-sprite diagnostics (check sprite != null, isVisible)
                var localSRs = localPlayer.GetComponentsInChildren<SpriteRenderer>(true);
                var remoteSRs = remotePlayer.GetComponentsInChildren<SpriteRenderer>(true);
                
                int localNullSprites = 0, remoteNullSprites = 0;
                int localInvisible = 0, remoteInvisible = 0;
                
                sb.AppendLine($"[RENDER_DIAG] --- LOCAL PLAYER SPRITES ({localSRs.Length}) ---");
                foreach (var sr in localSRs)
                {
                    if (sr.sprite == null) localNullSprites++;
                    if (!sr.isVisible) localInvisible++;
                    // Log body sprites only (head, top, bottom, top_2)
                    string n = sr.gameObject.name.ToLower();
                    if (n == "head" || n == "top" || n == "bottom" || n == "top_2")
                    {
                        sb.AppendLine($"[RENDER_DIAG]   [{n}] sprite={(sr.sprite != null ? sr.sprite.name : "NULL")}, enabled={sr.enabled}, visible={sr.isVisible}, alpha={sr.color.a:F2}, layer={sr.sortingLayerName}/{sr.sortingOrder}, mat={sr.material?.shader?.name}");
                    }
                }
                
                sb.AppendLine($"[RENDER_DIAG] --- REMOTE PLAYER SPRITES ({remoteSRs.Length}) ---");
                foreach (var sr in remoteSRs)
                {
                    if (sr.sprite == null) remoteNullSprites++;
                    if (!sr.isVisible) remoteInvisible++;
                    string n = sr.gameObject.name.ToLower();
                    if (n == "head" || n == "top" || n == "bottom" || n == "top_2")
                    {
                        sb.AppendLine($"[RENDER_DIAG]   [{n}] sprite={(sr.sprite != null ? sr.sprite.name : "NULL")}, enabled={sr.enabled}, visible={sr.isVisible}, alpha={sr.color.a:F2}, layer={sr.sortingLayerName}/{sr.sortingOrder}, mat={sr.material?.shader?.name}");
                    }
                }
                
                sb.AppendLine($"[RENDER_DIAG] Summary: Local nullSprites={localNullSprites}/{localSRs.Length}, invisible={localInvisible}. Remote nullSprites={remoteNullSprites}/{remoteSRs.Length}, invisible={remoteInvisible}");
                
                // 5. Check for any overlaid UI/Canvas that might block
                var canvases = GameObject.FindObjectsOfType<Canvas>();
                int screenOverlayCount = 0;
                foreach (var c in canvases)
                {
                    if (c.renderMode == RenderMode.ScreenSpaceOverlay && c.gameObject.activeInHierarchy)
                        screenOverlayCount++;
                }
                sb.AppendLine($"[RENDER_DIAG] Active ScreenSpaceOverlay canvases: {screenOverlayCount}");
                
                // 6. Check if the remote player's Renderer.isVisible reports false for ALL sprites
                // If so, the camera frustum doesn't include the remote player
                bool anyRemoteVisible = false;
                foreach (var sr in remoteSRs)
                {
                    if (sr.isVisible) { anyRemoteVisible = true; break; }
                }
                sb.AppendLine($"[RENDER_DIAG] Any remote sprite in camera frustum: {anyRemoteVisible}");
                
                // 6b. Screen-space positions - exactly where on screen each player appears
                if (cam != null)
                {
                    Vector3 localScreen = cam.WorldToScreenPoint(localPlayer.transform.position);
                    Vector3 remoteScreen = cam.WorldToScreenPoint(remotePlayer.transform.position);
                    float screenDist = Vector2.Distance(new Vector2(localScreen.x, localScreen.y), new Vector2(remoteScreen.x, remoteScreen.y));
                    sb.AppendLine($"[RENDER_DIAG] ScreenPos: local=({localScreen.x:F0},{localScreen.y:F0}), remote=({remoteScreen.x:F0},{remoteScreen.y:F0}), pixelDist={screenDist:F0}");
                }
                
                // 6c. Nearby Light components that could affect visibility
                try
                {
                    var lights = GameObject.FindObjectsOfType<Light>();
                    int lightCount = 0;
                    foreach (var light in lights)
                    {
                        if (!light.gameObject.activeInHierarchy || !light.enabled) continue;
                        float distToLocal = Vector2.Distance(light.transform.position, localPlayer.transform.position);
                        float distToRemote = Vector2.Distance(light.transform.position, remotePlayer.transform.position);
                        if (distToLocal < 500f || distToRemote < 500f)
                        {
                            sb.AppendLine($"[RENDER_DIAG] Light '{light.gameObject.name}': type={light.type}, intensity={light.intensity:F2}, range={light.range:F0}, distToLocal={distToLocal:F0}, distToRemote={distToRemote:F0}");
                            lightCount++;
                        }
                    }
                    if (lightCount == 0)
                        sb.AppendLine("[RENDER_DIAG] No active Light within 500 units of players");
                }
                catch (System.Exception lightEx)
                {
                    sb.AppendLine($"[RENDER_DIAG] Light check error: {lightEx.Message}");
                }
                
                // 7. Check BaseCharacterComponent state
                var localChar = localPlayer.wgo?.components?.character;
                var remoteChar = remotePlayer.wgo?.components?.character;
                if (localChar != null && remoteChar != null)
                {
                    sb.AppendLine($"[RENDER_DIAG] Character: local direction={localChar.direction}, remote direction={remoteChar.direction}");
                    sb.AppendLine($"[RENDER_DIAG] Character: local anim_direction={localChar.anim_direction}, remote anim_direction={remoteChar.anim_direction}");
                }
                
                // Log everything
                CoopMod.Logger.LogInfo(sb.ToString());
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[RENDER_DIAG] Error: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Set Unity layer recursively on a game object and all its children
        /// </summary>
        private void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
            {
                SetLayerRecursive(child.gameObject, layer);
            }
        }
        
        /// <summary>
        /// Log diagnostic information to help debug camera stuttering
        /// </summary>
        private void LogCameraDiagnostics()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[CameraDiag] IsHost={IsHost}, FPS={1f/Time.deltaTime:F1}");
                sb.AppendLine($"[CameraDiag] PosUpdates/2s={positionUpdatesThisInterval}, MaxDelta={maxPositionDeltaThisInterval:F3}");
                
                // Check camera targets
                bool remotePlayerInCameraTargets = false;
                if (CameraTools.pro_cam != null)
                {
                    var targets = CameraTools.pro_cam.CameraTargets;
                    sb.AppendLine($"[CameraDiag] CameraTargets.Count={targets?.Count ?? 0}");
                    if (targets != null)
                    {
                        for (int i = 0; i < targets.Count; i++)
                        {
                            var t = targets[i];
                            string name = t.TargetTransform?.name ?? "NULL";
                            sb.AppendLine($"[CameraDiag]   Target[{i}]: {name}, InfluenceH={t.TargetInfluenceH:F2}, InfluenceV={t.TargetInfluenceV:F2}");
                            
                            // Check if remote player somehow got into camera targets
                            if (remotePlayer != null && t.TargetTransform == remotePlayer.transform)
                            {
                                remotePlayerInCameraTargets = true;
                                sb.AppendLine($"[CameraDiag]   !!! REMOTE PLAYER IN CAMERA TARGETS - REMOVING !!!");
                            }
                        }
                    }
                }
                
                // If remote player is in camera targets, remove it
                if (remotePlayerInCameraTargets)
                {
                    CameraTools.RemoveFromCameraTargets(remotePlayer.transform, 0f);
                    sb.AppendLine($"[CameraDiag] Removed remote player from camera targets");
                }
                
                // Check remote player state
                if (remotePlayer != null)
                {
                    var chunked = remotePlayer.GetComponent<ChunkedGameObject>();
                    sb.AppendLine($"[CameraDiag] RemotePlayer always_active={chunked?.always_active}, obj_visible={chunked?.obj_visible}");
                    sb.AppendLine($"[CameraDiag] RemotePlayer pos={remotePlayer.transform.position}, targetPos={targetRemotePosition}");
                    sb.AppendLine($"[CameraDiag] Distance to target={Vector3.Distance(remotePlayer.transform.position, targetRemotePosition):F2}");
                }
                
                // Check local player
                if (localPlayer != null)
                {
                    sb.AppendLine($"[CameraDiag] LocalPlayer pos={localPlayer.transform.position}");
                }
                
                CoopMod.Logger.LogDebug(sb.ToString());
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[CameraDiag] Error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Update the remote player's animation based on received movement state.
        /// Walk/idle sync runs every frame; tool animations are driven by OnRemotePlayerAnimationReceived.
        /// </summary>
        private void UpdateRemotePlayerAnimation()
        {
            if (remotePlayer?.wgo?.components?.character == null)
                return;
            
            var character = remotePlayer.wgo.components.character;
            
            // Update direction for facing (only when direction changes significantly)
            bool directionChanged = Vector2.Distance(remoteDirection, lastAppliedRemoteDirection) > 0.1f;
            if (directionChanged && remoteDirection.magnitude > 0.01f)
            {
                character.direction = remoteDirection;
                // LookAt updates the animator's direction_angle parameter through ProcessDirection
                character.LookAt(remoteDirection);
                lastAppliedRemoteDirection = remoteDirection;
            }
            
            // If the remote player is currently in a tool/attack animation, don't override with walk/idle.
            // The tool animation state is managed by OnRemotePlayerAnimationReceived and will be
            // cleared when the remote player sends Idle or starts moving again.
            if (remoteAnimIsToolActive)
                return;
            
            // Determine the smoothed movement state with idle delay
            bool shouldBeMoving;
            if (remoteIsMoving)
            {
                // Immediately show walking when moving
                shouldBeMoving = true;
            }
            else
            {
                // Delay before transitioning to idle (prevents flicker)
                remoteIdleTimer += Time.deltaTime;
                shouldBeMoving = remoteIdleTimer < IDLE_DELAY;
            }
            
            // Set animation state when the smoothed state changes OR when a parity/action
            // snapshot has stomped the animator back to Idle while position packets are still moving.
            if (shouldBeMoving && (!lastAppliedRemoteIsMoving || character.anim_state != CharAnimState.Walking))
            {
                character.OnStartWalking();
                lastAppliedRemoteIsMoving = true;
            }
            else if (!shouldBeMoving && (lastAppliedRemoteIsMoving || character.anim_state == CharAnimState.Walking))
            {
                character.OnStopped();
                lastAppliedRemoteIsMoving = false;
            }
        }
        
        /// <summary>
        /// Check if we should auto-enable online coop when game starts
        /// </summary>
        private void CheckAutoEnable()
        {
            // Only auto-enable if:
            // 1. We're in a multiplayer session
            // 2. Game has started
            // 3. Local player exists
            // 4. We're in a Steam lobby with other players
            
            if (!LobbyGUI.IsMultiplayerSessionActive)
                return;

            // A host-directed in-game reload deliberately keeps the lobby and manager
            // alive while the old world is still present. Only the SpawnPlayer postfix
            // may reopen this gate after the replacement world creates its local player.
            if (Patches.LocalCoopActivationPatch.IsWaitingForHotReloadSpawn)
                return;
            
            if (!MainGame.game_started || MainGame.me?.player == null)
                return;
            
            var lobbyManager = SteamLobbyManager.Instance;
            if (lobbyManager == null || !lobbyManager.IsInLobby)
                return;
            
            int memberCount = lobbyManager.GetLobbyMemberCount();
            if (memberCount <= 1)
                return;
            
            // We have other players in lobby, determine if we're host or client
            bool isHost = lobbyManager.IsHost;
            
            CoopMod.Logger.LogInfo($"[OnlineCoopManager] Auto-enabling online coop. IsHost: {isHost}, MemberCount: {memberCount}");
            
            if (isHost)
            {
                // Get the first client's Steam ID
                CSteamID myID = SteamUser.GetSteamID();
                for (int i = 0; i < memberCount; i++)
                {
                    CSteamID memberID = SteamMatchmaking.GetLobbyMemberByIndex(lobbyManager.CurrentLobbyID, i);
                    if (memberID != myID)
                    {
                        EnableAsHost(memberID);
                        break;
                    }
                }
            }
            else
            {
                // Get the host's Steam ID
                CSteamID hostID = SteamMatchmaking.GetLobbyOwner(lobbyManager.CurrentLobbyID);
                EnableAsClient(hostID);
            }
        }
        
        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }
        
        /// <summary>
        /// Ensure the camera only follows the local player, not the remote player.
        /// This prevents camera stuttering when both players move.
        /// </summary>
        private void EnsureCameraFollowsLocalPlayer()
        {
            try
            {
                if (CameraTools.pro_cam == null)
                    return;

                bool remoteWasTarget = false;
                bool localIsTarget = false;
                var targets = CameraTools.pro_cam.CameraTargets;
                if (targets != null)
                {
                    for (int i = 0; i < targets.Count; i++)
                    {
                        Transform targetTransform = targets[i].TargetTransform;
                        if (remotePlayer != null && targetTransform == remotePlayer.transform)
                        {
                            remoteWasTarget = true;
                        }
                        if (localPlayer != null && targetTransform == localPlayer.transform)
                        {
                            localIsTarget = true;
                        }
                    }
                }

                if (remoteWasTarget)
                {
                    CameraTools.RemoveFromCameraTargets(remotePlayer.transform, 0f);
                }
                
                if (localPlayer != null && !localIsTarget)
                {
                    CameraTools.AddToCameraTargets(localPlayer.transform, true, 0.7f);
                }

                CoopMod.Logger.LogInfo("[OnlineCoopManager] Camera target verified for local player only");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[OnlineCoopManager] Error setting camera target: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Teleport remote player to be near local player (for scene changes)
        /// </summary>
        public void TeleportRemotePlayerToLocal(Vector3? offset = null)
        {
            if (localPlayer == null || remotePlayer == null)
                return;
            
            Vector3 actualOffset = offset ?? new Vector3(200f, 0f, 0f);
            Vector3 newPos = localPlayer.transform.position + actualOffset;
            remotePlayer.transform.position = newPos;
            targetRemotePosition = newPos;
            
            // Recalculate chunk
            var chunked = remotePlayer.GetComponent<ChunkedGameObject>();
            chunked?.RecalculateChunk();
            
            CoopMod.Logger.LogInfo($"[OnlineCoopManager] Remote player teleported to {newPos}");
        }
        
        /// <summary>
        /// Get distance between local and remote player
        /// </summary>
        public float GetPlayerDistance()
        {
            if (localPlayer == null || remotePlayer == null)
                return 0f;
            
            return Vector3.Distance(localPlayer.transform.position, remotePlayer.transform.position);
        }
        
        public PlayerComponent LocalPlayer => localPlayer;
        public PlayerComponent RemotePlayer => remotePlayer;

        #region Cutscene Walk-To (the absent player walks over to the cutscene)

        // When a remote player triggers a syncable cutscene (e.g. digging up Gerry), they also send
        // CutsceneWalkTo. We resolve our LOCAL player's MovementComponent and call GoTo so they
        // visibly walk over to the digger and stand beside them, facing the same direction.
        // 
        // For now this is unconditional: when the request arrives, we go. We'll add "don't disrupt me"
        // conditions (e.g. skip if the local player is in combat / in a menu / very far away) later.

        private bool walkInProgress;
        private bool bypassNextCutsceneWalkScopeCheck;
        private Vector3 walkTargetPos;
        private byte walkTargetFacing;
        private float walkStartTime;
        // After arrival, keep re-applying LookAt each frame until this timestamp. The dialogue
        // cutscene that triggers right after the walk often runs a DialogueActor that snaps the
        // listener's facing toward the speaker, overriding our one-shot LookAt. Re-stamping the
        // facing for a short window ensures the walker ends up facing the same direction as
        // the dialogue leader before dialogue-actor logic quiesces.
        private float walkFacingHoldUntil;
        // The dialogue cutscene that fires immediately after the walk can run a DialogueActor
        // that keeps re-aiming the listener toward the speaker for several seconds. We need
        // to out-stamp it; 4 s covers the opening of most Gerry-style dig-up conversations.
        private const float WALK_FACING_HOLD_SEC = 4f;
        private const float WALK_ARRIVAL_DIST = 24f;          // Stop checking facing once within this many world units
        private const float WALK_TIMEOUT_SEC = 15f;           // Give up driving the walk after this long
        private const float WALK_SIDE_OFFSET = 48f;           // Stand this far to the side of the digger (about half a tile)
        private const float WALK_NEAR_THRESHOLD = 200f;       // If already within this distance of digger, don't bother walking
        // Canonical cutscene walk speed: Flow_GoTo (the flowscript node cutscenes use to move
        // characters) defaults to 1.2f when no speed is specified. Match that exactly so our
        // walk-over looks identical to a scripted cutscene walk.
        private const float CUTSCENE_WALK_SPEED = 1.2f;

        // Stored speed so we can restore it once the cutscene walk finishes
        private bool walkSpeedStored;
        private float walkOriginalSpeed;

        // During an observed online cutscene the local player has no local FlowScript to move
        // them alongside the triggering player. Track that live remote player explicitly.
        private bool observedCutsceneFollowActive;
        private CSteamID observedCutsceneFollowSenderID = CSteamID.Nil;
        private bool observedFollowMoving;
        private bool observedFollowMovementIssued;
        private float observedFollowNextCheckTime;
        private bool observedFollowSpeedStored;
        private float observedFollowOriginalSpeed;
        // Stay a few steps behind the initiating player. FollowTarget tracks the
        // live remote transform instead of completing a series of stale GoTo
        // destinations, which avoids the visible stop/start cycle.
        private const float OBSERVED_FOLLOW_DISTANCE = 0.7f * 96f;
        private const float OBSERVED_FOLLOW_START_DISTANCE = 0.95f * 96f;
        private const float OBSERVED_FOLLOW_CATCH_UP_START_DISTANCE = 1.25f * 96f;
        private const float OBSERVED_FOLLOW_CATCH_UP_END_DISTANCE = 1.0f * 96f;
        private const float OBSERVED_FOLLOW_CHECK_INTERVAL = 0.1f;
        private const float OBSERVED_FOLLOW_CRUISE_SPEED = 1.4f;
        private const float OBSERVED_FOLLOW_CATCH_UP_SPEED = 1.8f;

        public void BeginObservedCutsceneFollow(CSteamID senderID)
        {
            if (!IsOnlineCoopEnabled || senderID == CSteamID.Nil ||
                !IsRemotePlayer(senderID))
            {
                return;
            }

            observedCutsceneFollowActive = true;
            observedCutsceneFollowSenderID = senderID;
            observedFollowMoving = false;
            observedFollowMovementIssued = false;
            observedFollowNextCheckTime = Time.time + 0.1f;
            CoopMod.Logger.LogInfo($"[CutsceneFollow] Local player will follow {SteamFriends.GetFriendPersonaName(senderID)} for the observed cutscene");
        }

        public void EndObservedCutsceneFollow(string reason)
        {
            if (!observedCutsceneFollowActive &&
                !observedFollowSpeedStored &&
                !observedFollowMovementIssued &&
                !walkInProgress &&
                !walkSpeedStored)
            {
                return;
            }

            var lp = MainGame.me?.player;
            var character = lp?.components?.character;

            if (observedFollowMovementIssued || walkInProgress)
            {
                try
                {
                    character?.StopMovement();
                    if (character != null)
                    {
                        character.player_controlled_by_script = false;
                        character.astar?.Clear();
                    }
                }
                catch { }

                var body = lp?.GetComponent<Rigidbody2D>();
                if (body != null)
                {
                    body.velocity = Vector2.zero;
                }
            }

            RestoreCutsceneWalkSpeed(lp);
            RestoreObservedFollowSpeed(lp);
            walkInProgress = false;
            walkFacingHoldUntil = 0f;
            observedCutsceneFollowActive = false;
            observedCutsceneFollowSenderID = CSteamID.Nil;
            observedFollowMoving = false;
            observedFollowMovementIssued = false;
            observedFollowNextCheckTime = 0f;
            CoopMod.Logger.LogInfo($"[CutsceneFollow] Stopped observed cutscene follow ({reason})");
        }

        public void StartDeferredCutsceneWalk(CSteamID senderID, Vector3 diggerPos, byte facingByte)
        {
            StartCutsceneWalkIgnoringInitialScope(senderID, diggerPos, facingByte);
        }

        public void StartLiveCutsceneJoinWalk(CSteamID senderID, Vector3 initiatorPos, byte facingByte)
        {
            StartCutsceneWalkIgnoringInitialScope(senderID, initiatorPos, facingByte);
        }

        private void StartCutsceneWalkIgnoringInitialScope(CSteamID senderID, Vector3 targetPos, byte facingByte)
        {
            bypassNextCutsceneWalkScopeCheck = true;
            try
            {
                OnCutsceneWalkToReceived(senderID, targetPos, facingByte);
            }
            finally
            {
                bypassNextCutsceneWalkScopeCheck = false;
            }
        }

        private void OnCutsceneWalkToReceived(CSteamID senderID, Vector3 diggerPos, byte facingByte)
        {
            if (!IsOnlineCoopEnabled)
            {
                CoopMod.Logger.LogInfo("[CutsceneWalk] Ignoring CutsceneWalkTo \u2014 online coop not enabled");
                return;
            }

            if (!IsRemotePlayer(senderID))
            {
                CoopMod.Logger.LogInfo($"[CutsceneWalk] Ignoring CutsceneWalkTo from unknown sender {senderID}");
                return;
            }

            var lp = MainGame.me?.player;
            if (lp == null)
            {
                CoopMod.Logger.LogWarning("[CutsceneWalk] No local player \u2014 cannot walk to cutscene");
                return;
            }

            if (!bypassNextCutsceneWalkScopeCheck && NpcInteractionSyncPatches.TryDeferCutsceneWalk(senderID, diggerPos, facingByte))
            {
                return;
            }

            bool bypassScopeCheck = bypassNextCutsceneWalkScopeCheck ||
                                    NpcInteractionSyncPatches.ShouldBypassCutsceneWalkScopeCheck();
            if (!bypassScopeCheck && CutsceneSyncPatches.ShouldDeferCutsceneWalk(senderID, diggerPos, facingByte))
            {
                return;
            }

            if (bypassScopeCheck)
            {
                CoopMod.Logger.LogInfo("[CutsceneWalk] Bypassing initial scope check for active NPC replay/live cutscene join");
            }

            // Side offset: pick the side opposite the digger's facing so we don't stand in front of them
            // staring at the back of their head. For Up/Down facing, stand to the right.
            // For Left facing, stand to the right of digger; for Right facing, stand to the left.
            Direction facingDir = (Direction)facingByte;
            Vector3 sideOffset;
            switch (facingDir)
            {
                case Direction.Left:  sideOffset = new Vector3( WALK_SIDE_OFFSET, 0f, 0f); break;
                case Direction.Right: sideOffset = new Vector3(-WALK_SIDE_OFFSET, 0f, 0f); break;
                default:              sideOffset = new Vector3( WALK_SIDE_OFFSET, 0f, 0f); break;
            }

            walkTargetPos = diggerPos + sideOffset;
            walkTargetFacing = facingByte;
            walkStartTime = Time.time;

            float distanceToDigger = Vector3.Distance(lp.transform.position, diggerPos);
            float distanceToTarget = Vector3.Distance(lp.transform.position, walkTargetPos);
            CoopMod.Logger.LogInfo($"[CutsceneWalk] Received walk request from {SteamFriends.GetFriendPersonaName(senderID)}. " +
                                   $"diggerPos={diggerPos}, facing={facingDir}, target={walkTargetPos}, distToDigger={distanceToDigger:F1}, distToTarget={distanceToTarget:F1}");

            // FAST PATH: already standing near the digger -- no need to walk, just face the right way.
            if (distanceToDigger <= WALK_NEAR_THRESHOLD)
            {
                CoopMod.Logger.LogInfo($"[CutsceneWalk] Local player already near digger ({distanceToDigger:F1} <= {WALK_NEAR_THRESHOLD:F1}) -- skipping walk");
                // Even though we aren't driving a network walk, the cutscene's local FlowScript
                // may still move this player (e.g. walking alongside Gerry). control_enabled=false
                // during cutscenes makes the game force the Rigidbody2D to Static, which blocks that
                // scripted movement too. Apply the same Static->Kinematic workaround as the walk path
                // so the local script can actually move the player.
                var skipBody = lp.GetComponent<Rigidbody2D>();
                if (skipBody != null && skipBody.bodyType == RigidbodyType2D.Static)
                {
                    skipBody.bodyType = RigidbodyType2D.Kinematic;
                    CoopMod.Logger.LogInfo("[CutsceneWalk] Switched Rigidbody2D from Static to Kinematic (skip path) so the cutscene script can still move the local player");
                }
                walkInProgress = false;
                ApplyFacingToLocalPlayer(walkTargetFacing);
                walkFacingHoldUntil = Time.time + WALK_FACING_HOLD_SEC;
                return;
            }

            walkInProgress = true;
            try
            {
                var movement = lp.components?.character;
                if (movement == null)
                {
                    CoopMod.Logger.LogWarning("[CutsceneWalk] Local player has no MovementComponent \u2014 snapping instead of walking");
                    lp.transform.position = walkTargetPos;
                    ApplyFacingToLocalPlayer(walkTargetFacing);
                    walkInProgress = false;
                    return;
                }

                // CRITICAL: During cutscenes, control_enabled is false for the local player.
                // The game's UpdateBodyPhysics sets Rigidbody2D to Static when control_enabled=false
                // and movement_state=None, which BLOCKS any movement we try to drive. We need to
                // force the body to Kinematic so MovementComponent.GoTo can actually move us.
                // (Same workaround used in LocalCoopCutsceneFollower for split-screen cutscenes.)
                var body = lp.GetComponent<Rigidbody2D>();
                if (body != null && body.bodyType == RigidbodyType2D.Static)
                {
                    body.bodyType = RigidbodyType2D.Kinematic;
                    CoopMod.Logger.LogInfo("[CutsceneWalk] Switched Rigidbody2D from Static to Kinematic so the cutscene-disabled player can walk");
                }

                // Slow the player to cinematic walk speed so they walk (not run) over.
                // We DO NOT use with_cinematic=true on GoTo because that calls CameraTools.PlayCinematics
                // which would yank the camera off the digger and ruin the cutscene framing.
                if (lp.data != null && !walkSpeedStored)
                {
                    float currentSpeed = lp.data.GetParam("speed", LazyConsts.PLAYER_SPEED);
                    walkOriginalSpeed = Mathf.Max(currentSpeed, LazyConsts.PLAYER_SPEED);
                    walkSpeedStored = true;
                    lp.data.SetParam("speed", CUTSCENE_WALK_SPEED);
                    CoopMod.Logger.LogInfo($"[CutsceneWalk] Stored speed={walkOriginalSpeed}, set walk speed to {CUTSCENE_WALK_SPEED}");
                }

                // Use A* pathfinding so the player respects collisions / nav mesh.
                // from_script=true so the cutscene's control_enabled=false doesn't reject the command.
                movement.GoTo(new Vector2(walkTargetPos.x, walkTargetPos.y), false, null, null,
                              false, MovementComponent.GoToMethod.AStar, "", null, true, null);
                CoopMod.Logger.LogInfo($"[CutsceneWalk] MovementComponent.GoTo dispatched to {walkTargetPos}");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[CutsceneWalk] Failed to start walk: {ex}");
                walkInProgress = false;
            }
        }

        /// <summary>
        /// Polled from LateUpdate -- snaps facing once the player arrives or times out.
        /// The cutscene script runs in parallel and may take over movement at any point;
        /// that's fine, we just stop driving the walk when arrival/timeout fires.
        /// </summary>
        private void TickCutsceneWalk()
        {
            // Keep stamping facing for a short window after arrival — even if walk already ended —
            // so the dialogue cutscene's DialogueActor can't pull us into a different direction.
            if (!walkInProgress && walkFacingHoldUntil > 0f && Time.time < walkFacingHoldUntil)
            {
                ApplyFacingToLocalPlayer(walkTargetFacing);
            }
            else if (!walkInProgress && walkFacingHoldUntil > 0f)
            {
                walkFacingHoldUntil = 0f;
            }

            if (!walkInProgress) return;

            var lp = MainGame.me?.player;
            if (lp == null)
            {
                RestoreCutsceneWalkSpeed(null);
                walkInProgress = false;
                walkFacingHoldUntil = 0f;
                return;
            }

            float elapsed = Time.time - walkStartTime;
            float dist = Vector3.Distance(lp.transform.position, walkTargetPos);

            // Detect "walk actually finished" via MovementComponent state, not just distance.
            // Pathfinder may route around obstacles and stop far from our target point; when
            // that happens the distance check never fires and the walker sits there (still facing
            // whatever direction MovementComponent last set) until WALK_TIMEOUT_SEC elapses —
            // which is exactly the "waits numerous seconds before facing" symptom.
            // A brief grace period lets the walk actually start before we start listening for
            // movement_state == None (otherwise we'd immediately think we're done on frame 1).
            var character = lp.components?.character;
            bool movementStopped = false;
            if (character != null && elapsed > 0.25f)
            {
                try
                {
                    var ms = character.movement_state;
                    movementStopped = (ms == MovementComponent.MovementState.None);
                }
                catch { }
            }

            bool arrived = dist <= WALK_ARRIVAL_DIST;
            bool timedOut = elapsed >= WALK_TIMEOUT_SEC;

            if (arrived || timedOut || movementStopped)
            {
                CoopMod.Logger.LogInfo($"[CutsceneWalk] Walk finished (arrived={arrived}, stopped={movementStopped}, timedOut={timedOut}, dist={dist:F1}, elapsed={elapsed:F1}). Snapping facing.");
                try
                {
                    lp.components?.character?.StopMovement();
                }
                catch { }

                RestoreCutsceneWalkSpeed(lp);

                ApplyFacingToLocalPlayer(walkTargetFacing);
                walkFacingHoldUntil = Time.time + WALK_FACING_HOLD_SEC;
                walkInProgress = false;
            }
        }

        private void TickObservedCutsceneFollow()
        {
            if (!observedCutsceneFollowActive || walkInProgress || Time.time < observedFollowNextCheckTime)
            {
                return;
            }

            observedFollowNextCheckTime = Time.time + OBSERVED_FOLLOW_CHECK_INTERVAL;

            if (!IsRemotePlayer(observedCutsceneFollowSenderID))
            {
                EndObservedCutsceneFollow("remote player changed");
                return;
            }

            var lp = MainGame.me?.player;
            var remote = GetRemotePlayer(observedCutsceneFollowSenderID);
            if (lp == null || remote == null)
            {
                return;
            }

            Vector3 localPos = lp.transform.position;
            Vector3 remotePos = remote.transform.position;
            float distance = Vector2.Distance(localPos, remotePos);

            var character = lp.components?.character;
            if (character == null)
            {
                return;
            }

            if (observedFollowMoving && character.movement_state == MovementComponent.MovementState.None)
            {
                observedFollowMoving = false;
            }

            if (observedFollowMoving)
            {
                ApplyObservedFollowSpeed(lp, distance);
                return;
            }

            if (distance > OBSERVED_FOLLOW_START_DISTANCE)
            {
                MoveLocalPlayerForObservedCutscene(remote, distance);
            }
        }

        private void MoveLocalPlayerForObservedCutscene(WorldGameObject remote, float distanceToRemote)
        {
            var lp = MainGame.me?.player;
            var character = lp?.components?.character;
            if (lp == null || character == null || remote == null)
            {
                return;
            }

            var body = lp.GetComponent<Rigidbody2D>();
            if (body != null && body.bodyType == RigidbodyType2D.Static)
            {
                body.bodyType = RigidbodyType2D.Kinematic;
            }

            if (lp.data != null && !observedFollowSpeedStored)
            {
                observedFollowOriginalSpeed = lp.data.GetParam("speed", LazyConsts.PLAYER_SPEED);
                observedFollowSpeedStored = true;
            }
            ApplyObservedFollowSpeed(lp, distanceToRemote);

            // Following movement owns facing now; stop the initial walk's facing hold from
            // forcing a cardinal direction while the player is moving down the road.
            walkFacingHoldUntil = 0f;

            try
            {
                character.FollowTarget(
                    remote.transform,
                    OBSERVED_FOLLOW_DISTANCE / 96f,
                    OnObservedFollowMoveComplete,
                    false,
                    "");

                // FollowTarget does not set this flag (unlike GoTo(..., from_script: true)).
                // Without it, BaseCharacterComponent.UpdatePlayer processes local input while
                // the player is cutscene-locked; ProcessWork then cancels every Following state
                // because the Work key is not held.
                character.player_controlled_by_script = true;
                observedFollowMoving = true;
                observedFollowMovementIssued = true;
                CoopMod.Logger.LogInfo(
                    $"[CutsceneFollow] Following live remote target " +
                    $"(distance={distanceToRemote / 96f:F1} units, desired={OBSERVED_FOLLOW_DISTANCE / 96f:F1})");
            }
            catch (System.Exception ex)
            {
                observedFollowMoving = false;
                CoopMod.Logger.LogWarning($"[CutsceneFollow] Failed to dispatch follow movement: {ex.Message}");
            }
        }

        private void OnObservedFollowMoveComplete()
        {
            observedFollowMoving = false;
        }

        private void ApplyObservedFollowSpeed(WorldGameObject player, float distanceToRemote)
        {
            if (player?.data == null)
            {
                return;
            }

            float currentSpeed = player.data.GetParam("speed", LazyConsts.PLAYER_SPEED);
            bool alreadyCatchingUp = Mathf.Abs(currentSpeed - OBSERVED_FOLLOW_CATCH_UP_SPEED) <= 0.01f;
            float desiredSpeed = alreadyCatchingUp
                ? (distanceToRemote > OBSERVED_FOLLOW_CATCH_UP_END_DISTANCE
                    ? OBSERVED_FOLLOW_CATCH_UP_SPEED
                    : OBSERVED_FOLLOW_CRUISE_SPEED)
                : (distanceToRemote > OBSERVED_FOLLOW_CATCH_UP_START_DISTANCE
                    ? OBSERVED_FOLLOW_CATCH_UP_SPEED
                    : OBSERVED_FOLLOW_CRUISE_SPEED);
            if (Mathf.Abs(currentSpeed - desiredSpeed) > 0.01f)
            {
                player.data.SetParam("speed", desiredSpeed);
                CoopMod.Logger.LogInfo(
                    $"[CutsceneFollow] Follow speed {currentSpeed:F1} -> {desiredSpeed:F1} " +
                    $"at {distanceToRemote / 96f:F1} units");
            }
        }

        private void RestoreObservedFollowSpeed(WorldGameObject player)
        {
            if (!observedFollowSpeedStored)
            {
                return;
            }

            WorldGameObject targetPlayer = player ?? MainGame.me?.player;
            if (targetPlayer?.data != null)
            {
                targetPlayer.data.SetParam("speed", observedFollowOriginalSpeed);
            }

            observedFollowSpeedStored = false;
        }

        private void RestoreCutsceneWalkSpeed(WorldGameObject player)
        {
            if (!walkSpeedStored)
                return;

            WorldGameObject targetPlayer = player ?? MainGame.me?.player;
            if (targetPlayer?.data != null)
            {
                float restoredSpeed = walkOriginalSpeed > CUTSCENE_WALK_SPEED + 0.05f
                    ? walkOriginalSpeed
                    : LazyConsts.PLAYER_SPEED;
                targetPlayer.data.SetParam("speed", restoredSpeed);
                CoopMod.Logger.LogInfo($"[CutsceneWalk] Restored original speed={restoredSpeed}");
            }

            walkSpeedStored = false;
        }

        private static void ApplyFacingToLocalPlayer(byte facingByte)
        {
            try
            {
                var character = MainGame.me?.player_char;
                if (character == null) return;

                Direction dir = (Direction)facingByte;
                if (dir == Direction.None || dir == Direction.IgnoreDirection || dir == Direction.ToPlayer)
                {
                    CoopMod.Logger.LogInfo($"[CutsceneWalk] Skipping facing -- non-cardinal direction {dir}");
                    return;
                }

                // LookAt drives ProcessDirection -> SetDirectionVectorForAnimator,
                // which updates the animator floats AND _anim_direction together. This is
                // much more durable than stamping the private field via reflection (the
                // animator would otherwise revert on its next blend update).
                character.LookAt(dir);
                CoopMod.Logger.LogDebug($"[CutsceneWalk] LookAt({dir}) applied to local player");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[CutsceneWalk] Failed to set facing: {ex.Message}");
            }
        }

        private void EnableSecondaryRemotePeers()
        {
            nextSecondaryRosterRefreshAt = 0f;
            RefreshSecondaryRemoteRoster();
            CoopMod.Logger.LogWarning(
                "[MultiPeer] Experimental 3-4 player avatar support enabled");
        }

        private void DisableSecondaryRemotePeers()
        {
            staleSecondaryPeerIds.Clear();
            foreach (SecondaryRemotePeer peer in secondaryRemotePeers.Values)
                DestroySecondaryRemotePeer(peer);
            secondaryRemotePeers.Clear();
        }

        private void TickSecondaryRemotePeers()
        {
            float now = Time.realtimeSinceStartup;
            if (now >= nextSecondaryRosterRefreshAt)
            {
                nextSecondaryRosterRefreshAt = now + SecondaryRosterRefreshSeconds;
                RefreshSecondaryRemoteRoster();
            }

            foreach (SecondaryRemotePeer peer in secondaryRemotePeers.Values)
            {
                UpdateSecondaryRemoteAnimation(peer);
                UpdateSecondaryRemoteMapIndicator(peer);
            }
        }

        private void RefreshSecondaryRemoteRoster()
        {
            SteamLobbyManager lobby = SteamLobbyManager.Instance;
            if (!IsOnlineCoopEnabled || lobby == null || !lobby.IsInLobby ||
                lobby.CurrentLobbyID == CSteamID.Nil)
            {
                return;
            }

            var present = new HashSet<ulong>();
            CSteamID local = SteamUser.GetSteamID();
            int count = SteamMatchmaking.GetNumLobbyMembers(lobby.CurrentLobbyID);
            for (int i = 0; i < count; i++)
            {
                CSteamID member =
                    SteamMatchmaking.GetLobbyMemberByIndex(lobby.CurrentLobbyID, i);
                if (member == CSteamID.Nil || member == local ||
                    member == remotePlayerSteamID)
                {
                    continue;
                }

                present.Add(member.m_SteamID);
                EnsureSecondaryRemotePeer(member);
            }

            staleSecondaryPeerIds.Clear();
            foreach (ulong steamID in secondaryRemotePeers.Keys)
            {
                if (!present.Contains(steamID))
                    staleSecondaryPeerIds.Add(steamID);
            }

            for (int i = 0; i < staleSecondaryPeerIds.Count; i++)
                RemoveSecondaryRemotePeer(new CSteamID(staleSecondaryPeerIds[i]));
        }

        private void EnsureSecondaryRemotePeer(CSteamID steamID)
        {
            if (!IsOnlineCoopEnabled || steamID == CSteamID.Nil ||
                steamID == SteamUser.GetSteamID() || steamID == remotePlayerSteamID ||
                secondaryRemotePeers.ContainsKey(steamID.m_SteamID) ||
                !IsCurrentLobbyMember(steamID) || MainGame.me?.player == null)
            {
                return;
            }

            SecondaryRemotePeer peer = SpawnSecondaryRemotePeer(
                steamID,
                secondaryRemotePeers.Count + 2);
            if (peer != null)
                secondaryRemotePeers[steamID.m_SteamID] = peer;
        }

        private void RemoveSecondaryRemotePeer(CSteamID steamID)
        {
            if (steamID == CSteamID.Nil ||
                !secondaryRemotePeers.TryGetValue(
                    steamID.m_SteamID,
                    out SecondaryRemotePeer peer))
            {
                return;
            }

            secondaryRemotePeers.Remove(steamID.m_SteamID);
            DestroySecondaryRemotePeer(peer);
        }

        private SecondaryRemotePeer SpawnSecondaryRemotePeer(
            CSteamID steamID,
            int ordinal)
        {
            try
            {
                PlayerComponent player =
                    PlayerComponent.SpawnPlayer(is_local_player: false, inventory: null);
                if (player == null)
                    return null;

                player.gameObject.name = $"RemotePlayer_{steamID.m_SteamID}";
                if (player.wgo != null)
                {
                    player.wgo.unique_id = BuildRemotePlayerUniqueId(steamID);
                    player.wgo.data.SetParam("speed", LazyConsts.PLAYER_SPEED);
                    player.wgo.data.SetInventorySize(20);
                    if (MainGame.me?.player?.data != null)
                    {
                        player.wgo.data.hp = MainGame.me.player.data.hp;
                        player.wgo.data.SetParam(
                            "energy",
                            MainGame.me.player.data.GetParam("energy", 100f));
                        player.wgo.data.SetParam(
                            "sanity",
                            MainGame.me.player.data.GetParam("sanity", 100f));
                    }
                }

                BaseCharacterComponent character = player.wgo?.components?.character;
                if (character != null)
                {
                    character.can_be_locally_controlled = false;
                    character.control_enabled = false;
                }

                Vector3 spawnPosition = MainGame.me.player.transform.position +
                    new Vector3(FallbackRemoteSpawnOffset * ordinal, 0f, 0f);
                bool fromSave = MultiplayerSavePositions.TryGetSavedPositionForSteam(
                    steamID,
                    out Vector3 savedPosition);
                if (fromSave)
                    player.transform.localPosition = savedPosition;
                else
                    player.transform.position = spawnPosition;

                DisableRemoteSimulation(player);
                CameraTools.RemoveFromCameraTargets(player.transform, 0f);

                ChunkedGameObject chunked = player.GetComponent<ChunkedGameObject>();
                if (chunked != null)
                    chunked.always_active = true;

                GhostDriver ghost = player.gameObject.AddComponent<GhostDriver>();
                ghost.Enable();

                var cosmeticsDriver =
                    player.gameObject.AddComponent<PlayerCosmeticsDriver>();
                cosmeticsDriver.Initialize();
                cosmeticsDriver.SetCosmetics(
                    PlayerCosmetics.FromSteamID(steamID.m_SteamID));
                CosmeticsSync.Instance?.SetRemotePlayerDriver(steamID, cosmeticsDriver);
                CosmeticsSync.Instance?.RequestCosmeticsFrom(steamID);

                string name = NameTagManager.GetSteamDisplayName(steamID);
                GameObject nameTag =
                    NameTagManager.CreateNameTag(player.gameObject, name);
                ChatGUI.Instance?.AddRemotePlayer(name, steamID);

                var peer = new SecondaryRemotePeer
                {
                    SteamID = steamID,
                    Player = player,
                    Ghost = ghost,
                    NameTag = nameTag,
                    DisplayName = name,
                    TargetPosition = player.transform.position,
                    HasPosition = fromSave
                };

                CoopMod.Logger.LogInfo(
                    $"[MultiPeer] Spawned remote avatar {name} " +
                    $"({steamID.m_SteamID}); remote_count={secondaryRemotePeers.Count + 2}");
                return peer;
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError(
                    $"[MultiPeer] Failed to spawn peer {steamID.m_SteamID}: {ex}");
                return null;
            }
        }

        private void DestroySecondaryRemotePeer(SecondaryRemotePeer peer)
        {
            if (peer == null)
                return;

            peer.Ghost?.Disable();
            CosmeticsSync.Instance?.RemoveRemotePlayerDriver(peer.SteamID);
            if (peer.NameTag != null)
                NameTagManager.DestroyNameTag(peer.NameTag);
            if (!string.IsNullOrEmpty(peer.DisplayName))
                ChatGUI.Instance?.RemoveRemotePlayer(peer.DisplayName);
            MapGUIPatches.RemoveRemotePlayerIndicator(
                peer.SteamID.m_SteamID.ToString());
            if (peer.Player != null)
                Destroy(peer.Player.gameObject);

            CoopMod.Logger.LogInfo(
                $"[MultiPeer] Removed remote avatar {peer.SteamID.m_SteamID}");
        }

        private bool TryGetSecondaryRemoteSavePosition(
            CSteamID steamID,
            out Vector3 position)
        {
            position = Vector3.zero;
            if (steamID == CSteamID.Nil ||
                !secondaryRemotePeers.TryGetValue(
                    steamID.m_SteamID,
                    out SecondaryRemotePeer peer) ||
                peer?.Player == null)
            {
                return false;
            }

            Vector3 world = peer.HasPosition
                ? peer.TargetPosition
                : peer.Player.transform.position;
            position = WorldToSavePosition(world);
            return true;
        }

        private void ApplySecondaryRemoteState(
            CSteamID senderID,
            Vector2 direction,
            bool isMoving,
            Vector2 velocity)
        {
            EnsureSecondaryRemotePeer(senderID);
            if (!secondaryRemotePeers.TryGetValue(
                    senderID.m_SteamID,
                    out SecondaryRemotePeer peer))
            {
                return;
            }

            peer.Velocity = velocity;
            if (direction.sqrMagnitude > 0.0001f)
                peer.Direction = direction;
            peer.Moving = isMoving;
            if (isMoving)
            {
                peer.IdleTimer = 0f;
                if (peer.ToolAnimationActive ||
                    peer.Animation == CharAnimState.Tool)
                {
                    ClearRemoteWorkAnimation(senderID);
                }
            }
        }

        private void ApplySecondaryRemotePosition(
            CSteamID senderID,
            Vector3 position,
            float timestamp)
        {
            EnsureSecondaryRemotePeer(senderID);
            if (!secondaryRemotePeers.TryGetValue(
                    senderID.m_SteamID,
                    out SecondaryRemotePeer peer) ||
                peer.Player == null)
            {
                return;
            }

            float distance = Vector3.Distance(
                peer.Player.transform.position,
                position);
            peer.TargetPosition = position;
            if (!peer.HasPosition || distance >= SecondaryTeleportSnapDistance)
            {
                peer.Player.transform.position = position;
                if (peer.Player.wgo != null)
                    peer.Player.wgo.transform.position = position;
                peer.Ghost?.ForcePosition(position);
                peer.HasPosition = true;

                ChunkedGameObject chunked =
                    peer.Player.GetComponent<ChunkedGameObject>();
                chunked?.RecalculateChunk();
                return;
            }

            peer.Ghost?.PushSnapshot(position, peer.Velocity, timestamp);
        }

        private void ApplySecondaryRemoteAnimation(
            CSteamID senderID,
            int animation,
            int itemType)
        {
            EnsureSecondaryRemotePeer(senderID);
            if (!secondaryRemotePeers.TryGetValue(
                    senderID.m_SteamID,
                    out SecondaryRemotePeer peer))
            {
                return;
            }

            CharAnimState incomingAnimation = (CharAnimState)animation;
            ItemDefinition.ItemType incomingItemType =
                (ItemDefinition.ItemType)itemType;
            BaseCharacterComponent character =
                peer.Player?.wgo?.components?.character;
            if (peer.Animation == incomingAnimation &&
                peer.ItemType == incomingItemType &&
                character?.anim_state == incomingAnimation &&
                incomingAnimation != CharAnimState.Idle)
            {
                return;
            }

            peer.Animation = incomingAnimation;
            peer.ItemType = incomingItemType;
            peer.ActionExplicitlyStopped =
                peer.Animation == CharAnimState.Idle ||
                peer.Animation == CharAnimState.Walking;
            peer.ToolAnimationActive =
                peer.Animation != CharAnimState.Idle &&
                peer.Animation != CharAnimState.Walking;
            ApplySecondaryAnimationState(peer);
        }

        private static bool IsToolActionState(
            CharAnimState state,
            int globalState)
        {
            return state == CharAnimState.Tool || globalState >= 100;
        }

        private static void ApplySecondaryAnimationState(SecondaryRemotePeer peer)
        {
            BaseCharacterComponent character = peer?.Player?.wgo?.components?.character;
            if (character == null)
                return;

            character.SetAnimationState(peer.Animation, peer.ItemType);
            if (peer.Animation == CharAnimState.Tool)
                character.SetToolGraphics((int)peer.ItemType);
            else if (peer.Animation == CharAnimState.Idle)
                character.SetToolGraphics(0);
        }

        private static void UpdateSecondaryRemoteAnimation(SecondaryRemotePeer peer)
        {
            BaseCharacterComponent character = peer?.Player?.wgo?.components?.character;
            if (character == null)
                return;

            if (Vector2.Distance(peer.Direction, peer.AppliedDirection) > 0.1f &&
                peer.Direction.sqrMagnitude > 0.0001f)
            {
                character.direction = peer.Direction;
                character.LookAt(peer.Direction);
                peer.AppliedDirection = peer.Direction;
            }

            if (peer.ToolAnimationActive)
                return;

            bool shouldMove;
            if (peer.Moving)
            {
                shouldMove = true;
            }
            else
            {
                peer.IdleTimer += Time.deltaTime;
                shouldMove = peer.IdleTimer < SecondaryIdleDelaySeconds;
            }

            if (shouldMove && (!peer.AppliedMoving ||
                               character.anim_state != CharAnimState.Walking))
            {
                character.OnStartWalking();
                peer.AppliedMoving = true;
            }
            else if (!shouldMove && (peer.AppliedMoving ||
                                     character.anim_state == CharAnimState.Walking))
            {
                character.OnStopped();
                peer.AppliedMoving = false;
            }
        }

        private static void UpdateSecondaryRemoteMapIndicator(
            SecondaryRemotePeer peer)
        {
            if (peer?.Player == null)
                return;

            Color color = Color.HSVToRGB(
                (peer.SteamID.m_SteamID % 997UL) / 997f,
                0.65f,
                1f);
            MapGUIPatches.UpdateRemotePlayerIndicator(
                peer.SteamID.m_SteamID.ToString(),
                peer.DisplayName ?? "Player",
                peer.HasPosition ? peer.TargetPosition : peer.Player.transform.position,
                color);
        }

        private static void DisableRemoteSimulation(PlayerComponent player)
        {
            if (player == null)
                return;

            Rigidbody2D body = player.GetComponentInChildren<Rigidbody2D>(true);
            if (body != null)
            {
                body.velocity = Vector2.zero;
                body.angularVelocity = 0f;
                body.simulated = false;
            }

            AILerp aiLerp = player.GetComponentInChildren<AILerp>(true);
            if (aiLerp != null)
            {
                aiLerp.canMove = false;
                aiLerp.enabled = false;
            }

            Seeker seeker = player.GetComponentInChildren<Seeker>(true);
            if (seeker != null)
                seeker.enabled = false;

            AIPath aiPath = player.GetComponentInChildren<AIPath>(true);
            if (aiPath != null)
                aiPath.enabled = false;

            ObjectDynamicShadow shadow =
                player.GetComponentInChildren<ObjectDynamicShadow>(true);
            if (shadow != null)
                Destroy(shadow.gameObject);

            ObjectDynamicShadowChild[] shadowChildren =
                player.GetComponentsInChildren<ObjectDynamicShadowChild>(true);
            for (int i = 0; i < shadowChildren.Length; i++)
            {
                if (shadowChildren[i] != null)
                    Destroy(shadowChildren[i].gameObject);
            }
        }

        private static bool IsCurrentLobbyMember(CSteamID steamID)
        {
            SteamLobbyManager lobby = SteamLobbyManager.Instance;
            if (steamID == CSteamID.Nil || lobby == null || !lobby.IsInLobby ||
                lobby.CurrentLobbyID == CSteamID.Nil)
            {
                return false;
            }

            int count = SteamMatchmaking.GetNumLobbyMembers(lobby.CurrentLobbyID);
            for (int i = 0; i < count; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(lobby.CurrentLobbyID, i) ==
                    steamID)
                {
                    return true;
                }
            }
            return false;
        }

        private void TickStuckControlWatchdog()
        {
            var pc = MainGame.me?.player_char;
            if (pc == null) return;

            if (!pc.control_enabled)
            {
                if (CutsceneSyncPatches.ShouldKeepLocalPlayerLocked(remotePlayerSteamID))
                {
                    // This is an intentional network cutscene lock, not a stuck control
                    // state. CutsceneComplete owns the corresponding unlock.
                    stuckControlDisabledSince = -1f;
                    return;
                }

                if (stuckControlDisabledSince < 0f)
                {
                    stuckControlDisabledSince = Time.realtimeSinceStartup;
                }
                else if (Time.realtimeSinceStartup - stuckControlDisabledSince > 45f)
                {
                    CoopMod.Logger.LogWarning("[OnlineCoopManager] Player stuck with control disabled for >45s — force-unlocking (safety net)");
                    pc.control_enabled = true;
                    if (pc.player_controlled_by_script)
                        pc.player_controlled_by_script = false;
                    try { GS.SetPlayerEnable(true, true); } catch { }
                    stuckControlDisabledSince = -1f;
                }
            }
            else
            {
                stuckControlDisabledSince = -1f;
            }
        }

        #endregion
    }
}

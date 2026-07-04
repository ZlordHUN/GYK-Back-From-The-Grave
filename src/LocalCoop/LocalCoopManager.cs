using UnityEngine;
using GraveyardKeeperCoop.UI;

namespace GraveyardKeeperCoop.LocalCoop
{
    public class LocalCoopManager : MonoBehaviour
    {
        private static LocalCoopManager _instance;
        public static LocalCoopManager Instance => _instance;

        private PlayerComponent player1;
        private PlayerComponent player2;
        
        // Camera manager for local co-op
        private LocalCoopCamera cameraManager;
        
        // Cutscene follower for idle player
        private LocalCoopCutsceneFollower cutsceneFollower;
        
        public bool IsLocalCoopEnabled { get; private set; }
        public PlayerComponent Player1 => player1;
        public PlayerComponent Player2 => player2;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            
            _instance = this;
            DontDestroyOnLoad(gameObject);
            
            // Create camera manager
            cameraManager = gameObject.AddComponent<LocalCoopCamera>();
            
            // Create cutscene follower for idle player behavior
            cutsceneFollower = gameObject.AddComponent<LocalCoopCutsceneFollower>();
            
            CoopMod.Logger.LogInfo("LocalCoopManager initialized");
        }

        public void EnableLocalCoop()
        {
            if (IsLocalCoopEnabled)
            {
                CoopMod.Logger.LogWarning("Local co-op already enabled!");
                return;
            }

            CoopMod.Logger.LogInfo("Enabling local co-op mode...");
            IsLocalCoopEnabled = true;

            if (MainGame.me != null && MainGame.me.player != null)
            {
                player1 = MainGame.me.player.GetComponent<PlayerComponent>();
                CoopMod.Logger.LogInfo($"Found Player 1: {player1.gameObject.name}");
            }
            else
            {
                CoopMod.Logger.LogError("Cannot enable local co-op - Player 1 not found!");
                IsLocalCoopEnabled = false;
                return;
            }

            SpawnPlayer2();
        }

        public void DisableLocalCoop()
        {
            if (!IsLocalCoopEnabled)
                return;

            CoopMod.Logger.LogInfo("Disabling local co-op mode...");
            
            // Reset camera before removing Player 2
            if (cameraManager != null)
            {
                cameraManager.ResetCameraZoom();
            }
            
            if (player2 != null)
            {
                Destroy(player2.gameObject);
                player2 = null;
            }

            IsLocalCoopEnabled = false;
        }

        private void SpawnPlayer2()
        {
            try
            {
                CoopMod.Logger.LogInfo("Spawning Player 2...");

                player2 = PlayerComponent.SpawnPlayer(is_local_player: false, inventory: null);
                
                if (player2 == null)
                {
                    CoopMod.Logger.LogError("Failed to spawn Player 2 - SpawnPlayer returned null!");
                    return;
                }

                CoopMod.Logger.LogInfo($"Player 2 spawned successfully: {player2.gameObject.name}");

                // Enable player control for Player 2
                if (player2.wgo != null && player2.wgo.components != null && player2.wgo.components.character != null)
                {
                    var character = player2.wgo.components.character;
                    character.can_be_locally_controlled = true;
                    character.control_enabled = true; // CRITICAL: Must enable control!
                    // CoopMod.Logger.LogInfo($"Player 2 can_be_locally_controlled set to true");
                    // CoopMod.Logger.LogInfo($"Player 2 control_enabled set to true");
                    // CoopMod.Logger.LogInfo($"Player 2 is_player: {player2.wgo.is_player}");
                    // CoopMod.Logger.LogInfo($"Player 2 player_controlled_by_script: {character.player_controlled_by_script}");
                }

                // CRITICAL: Set Player 2's speed parameter to normal player speed
                // Don't copy from P1 because P1 might be in a cutscene with reduced speed!
                if (player2.wgo != null)
                {
                    float normalSpeed = LazyConsts.PLAYER_SPEED; // 3.3
                    player2.wgo.data.SetParam("speed", normalSpeed);
                    CoopMod.Logger.LogInfo($"Player 2 speed set to {normalSpeed} (normal player speed)");
                    
                    // Verify it was set correctly
                    float p2SpeedAfter = player2.wgo.data.GetParam("speed", -1f);
                    CoopMod.Logger.LogInfo($"Player 2 speed verification: {p2SpeedAfter}");
                }
                
                if (player1 != null && player2.wgo != null)
                {
                    Vector3 player1Pos = player1.transform.position;
                    player2.transform.position = player1Pos + new Vector3(200f, 0f, 0f);
                    // CoopMod.Logger.LogInfo($"Player 2 positioned at: {player2.transform.position}");
                }

                // Initialize Player 2's inventory (player inventory size is 20, toolbelt is 7)
                if (player2.wgo != null)
                {
                    // Set main inventory size to match Player 1 (default is 20)
                    player2.wgo.data.SetInventorySize(20);
                    
                    // Initialize secondary inventory (toolbelt) if it doesn't exist
                    if (player2.wgo.data.secondary_inventory == null)
                    {
                        player2.wgo.data.secondary_inventory = new System.Collections.Generic.List<Item>();
                    }
                    
                    // Copy some basic stats from P1
                    if (player1 != null)
                    {
                        // Copy HP, energy, sanity from Player 1
                        player2.wgo.data.hp = player1.wgo.data.hp;
                        player2.wgo.data.SetParam("energy", player1.wgo.data.GetParam("energy", 100f));
                        player2.wgo.data.SetParam("sanity", player1.wgo.data.GetParam("sanity", 100f));
                    }
                    
                    CoopMod.Logger.LogInfo($"Player 2 inventory initialized with size: {player2.wgo.data.inventory_size}");
                }
                
                // CRITICAL: Initialize DropCollectorComponent for P2 (normally only done for is_local_player=true)
                // The PlayerComponent forwards OnTriggerStay2D/OnTriggerEnter2D to _drop_collector
                // We need to set this private field via reflection so P2 can pick up items
                try
                {
                    var dropCollector = new DropCollectorComponent();
                    dropCollector.Init(player2.wgo);
                    dropCollector.StartComponent();
                    
                    // Use reflection to set the private _drop_collector field on PlayerComponent
                    var dropCollectorField = typeof(PlayerComponent).GetField("_drop_collector", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (dropCollectorField != null)
                    {
                        dropCollectorField.SetValue(player2, dropCollector);
                        CoopMod.Logger.LogInfo("Player 2 DropCollectorComponent initialized and attached to PlayerComponent");
                    }
                    else
                    {
                        CoopMod.Logger.LogWarning("Could not find _drop_collector field on PlayerComponent");
                    }
                }
                catch (System.Exception dropEx)
                {
                    CoopMod.Logger.LogWarning($"Failed to init DropCollectorComponent for P2: {dropEx.Message}");
                }

                CoopMod.Logger.LogInfo("Player 2 setup complete!");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"Error spawning Player 2: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void Update()
        {
            // Check if we should auto-enable local co-op
            // Only enable if:
            // 1. Local coop is not already enabled
            // 2. Config has EnableLocalCoop = true
            // 3. Multiplayer session is active (game was started from multiplayer menu)
            // 4. Game is started and player exists
            if (!IsLocalCoopEnabled && ModConfig.EnableLocalCoop.Value)
            {
                // CRITICAL: Only enable local coop when multiplayer session is active
                // This prevents Player 2 from spawning when starting from the regular "Play" menu
                // Use LobbyGUI.IsMultiplayerSessionActive which is set when lobby opens
                bool isMultiplayer =
                    LobbyGUI.IsMultiplayerSessionActive ||
                    GraveyardKeeperCoop.Network.SteamLobbyManager.Instance?.IsInLobby == true ||
                    GraveyardKeeperCoop.Network.OnlineCoopManager.Instance?.IsOnlineCoopEnabled == true;
                
                if (!isMultiplayer)
                {
                    return; // Not in multiplayer mode, don't spawn Player 2
                }
                
                // Wait for the game to start and player to spawn
                if (MainGame.game_started && MainGame.me != null && MainGame.me.player != null)
                {
                    CoopMod.Logger.LogInfo("[LocalCoopManager] Multiplayer game started, enabling local co-op...");
                    // CoopMod.Logger.LogInfo($"[LocalCoopManager] LobbyGUI.IsMultiplayerSessionActive: {LobbyGUI.IsMultiplayerSessionActive}");
                    // CoopMod.Logger.LogInfo($"[LocalCoopManager] OnlineCoopManager.IsOnlineCoopEnabled: {GraveyardKeeperCoop.Network.OnlineCoopManager.Instance?.IsOnlineCoopEnabled}");
                    EnableLocalCoop();
                }
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
        /// Teleport Player 2 to be near Player 1.
        /// Called after Player 1 teleports to a new area.
        /// </summary>
        /// <param name="offset">Offset from Player 1's position</param>
        public void TeleportPlayer2ToPlayer1(Vector3? offset = null)
        {
            if (player1 == null || player2 == null)
                return;

            Vector3 actualOffset = offset ?? new Vector3(200f, 0f, 0f);
            player2.transform.position = player1.transform.position + actualOffset;

            // Recalculate chunk for Player 2
            var chunked = player2.GetComponent<ChunkedGameObject>();
            if (chunked != null)
            {
                chunked.RecalculateChunk();
            }

            // Notify camera
            cameraManager?.OnPlayer2Teleported(player2.transform.position);

            // CoopMod.Logger.LogInfo($"[LocalCoopManager] Player 2 teleported to {player2.transform.position}");
        }

        /// <summary>
        /// Get the distance between both players
        /// </summary>
        public float GetPlayerDistance()
        {
            if (player1 == null || player2 == null)
                return 0f;

            return Vector3.Distance(player1.transform.position, player2.transform.position);
        }
    }
}

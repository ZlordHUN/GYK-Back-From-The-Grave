using UnityEngine;
using GraveyardKeeperCoop.Patches;

namespace GraveyardKeeperCoop.LocalCoop
{
    /// <summary>
    /// Manages idle player behavior during cutscenes.
    /// When one player is in a cutscene (e.g., P2 talking to Gerry), 
    /// the idle player (P1) will follow a few steps behind using A* pathfinding.
    /// </summary>
    public class LocalCoopCutsceneFollower : MonoBehaviour
    {
        private static LocalCoopCutsceneFollower _instance;
        public static LocalCoopCutsceneFollower Instance => _instance;

        // Follow distance settings (in game units, 96 pixels = 1 unit)
        private const float FOLLOW_DISTANCE_UNITS = 1.5f;     // Stay this close behind the target
        private const float MIN_DISTANCE_TO_START_MOVING = 2.5f; // Only move if further than this
        private const float POSITION_CHECK_INTERVAL = 0.25f; // How often to check positions

        // State tracking
        private bool isMoving = false;
        private WorldGameObject idlePlayer = null;
        private WorldGameObject activePlayer = null;
        private Vector3 lastActivePlayerPosition = Vector3.zero;
        private float nextPositionCheckTime = 0f;
        
        // Track if we started the cutscene follow
        private bool cutsceneFollowActive = false;
        
        // Speed tracking - store original speed PER PLAYER to restore later
        private float originalPlayer1Speed = 0f;
        private float originalPlayer2Speed = 0f;
        private bool hasStoredPlayer1Speed = false;
        private bool hasStoredPlayer2Speed = false;
        
        // Walking speed during cutscenes - slower for smooth following
        private const float CUTSCENE_FOLLOW_SPEED = 1.5f;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
            {
                StopFollowing();
                return;
            }

            if (manager.Player1 == null || manager.Player2 == null)
            {
                StopFollowing();
                return;
            }

            // Check if we're in a cutscene with speech bubbles
            bool inCutscene = SpeechBubbleGUI.all != null && SpeechBubbleGUI.all.Count > 0;
            
            if (inCutscene)
            {
                // Determine which player is in the cutscene and which is idle
                var interactingPlayer = LocalCoopDialoguePatches.CurrentInteractingPlayer;
                
                if (interactingPlayer != null)
                {
                    WorldGameObject newActivePlayer = interactingPlayer;
                    WorldGameObject newIdlePlayer = (interactingPlayer == manager.Player1.wgo) 
                        ? manager.Player2.wgo 
                        : manager.Player1.wgo;
                    
                    if (!cutsceneFollowActive || activePlayer != newActivePlayer)
                    {
                        // Start or update following
                        StartFollowing(newIdlePlayer, newActivePlayer);
                    }
                    else
                    {
                        // Continue following - update position if needed
                        UpdateFollowing();
                    }
                }
            }
            else if (cutsceneFollowActive)
            {
                // Cutscene ended, stop following
                StopFollowing();
            }
        }

        private void StartFollowing(WorldGameObject idle, WorldGameObject active)
        {
            idlePlayer = idle;
            activePlayer = active;
            lastActivePlayerPosition = active.tf.position;
            cutsceneFollowActive = true;
            isMoving = false;
            nextPositionCheckTime = Time.time + 0.3f; // Small delay before starting to follow
            
            // Store original speed per-player and set to cutscene follow speed
            bool isIdlePlayer1 = (idle == LocalCoopManager.Instance?.Player1?.wgo);
            
            if (idle.data != null)
            {
                if (isIdlePlayer1 && !hasStoredPlayer1Speed)
                {
                    originalPlayer1Speed = idle.data.GetParam("speed", LazyConsts.PLAYER_SPEED);
                    hasStoredPlayer1Speed = true;
                    CoopMod.Logger.LogInfo($"[CutsceneFollower] Stored P1 original speed: {originalPlayer1Speed}");
                }
                else if (!isIdlePlayer1 && !hasStoredPlayer2Speed)
                {
                    originalPlayer2Speed = idle.data.GetParam("speed", LazyConsts.PLAYER_SPEED);
                    hasStoredPlayer2Speed = true;
                    CoopMod.Logger.LogInfo($"[CutsceneFollower] Stored P2 original speed: {originalPlayer2Speed}");
                }
                
                // Set cutscene speed for idle player
                idle.data.SetParam("speed", CUTSCENE_FOLLOW_SPEED);
                CoopMod.Logger.LogInfo($"[CutsceneFollower] Set idle player speed to {CUTSCENE_FOLLOW_SPEED}");
            }
            
            string idleName = isIdlePlayer1 ? "Player 1" : "Player 2";
            string activeName = (active == LocalCoopManager.Instance?.Player1?.wgo) ? "Player 1" : "Player 2";
            CoopMod.Logger.LogInfo($"[CutsceneFollower] {idleName} will follow {activeName} during cutscene");
        }

        private void UpdateFollowing()
        {
            if (idlePlayer == null || activePlayer == null)
                return;

            if (Time.time < nextPositionCheckTime)
                return;

            nextPositionCheckTime = Time.time + POSITION_CHECK_INTERVAL;

            // Get current positions
            Vector3 currentActivePos = activePlayer.tf.position;
            Vector3 idlePos = idlePlayer.tf.position;
            
            // Calculate distance between players (in game units)
            float distanceToActive = Vector3.Distance(idlePos, currentActivePos) / 96f;
            
            // Check if active player has moved significantly from their last tracked position
            float activePlayerMoved = Vector3.Distance(currentActivePos, lastActivePlayerPosition) / 96f;
            
            // If far enough away or active player has moved, start moving toward them
            if (distanceToActive > MIN_DISTANCE_TO_START_MOVING || (activePlayerMoved > 0.3f && distanceToActive > FOLLOW_DISTANCE_UNITS))
            {
                lastActivePlayerPosition = currentActivePos;
                
                // Calculate a point behind the active player (toward the idle player's direction)
                Vector3 directionToIdle = (idlePos - currentActivePos).normalized;
                Vector3 targetPoint = currentActivePos + directionToIdle * FOLLOW_DISTANCE_UNITS * 96f;
                
                MoveToPoint(targetPoint);
            }
        }

        private void MoveToPoint(Vector3 targetPoint)
        {
            if (idlePlayer == null)
                return;

            var character = idlePlayer.components?.character;
            if (character == null)
                return;

            // Don't start a new movement if we're already moving
            if (isMoving && character.movement_state != MovementComponent.MovementState.None)
                return;

            // CRITICAL: During cutscenes, control_enabled is false for all players.
            // The game's UpdateBodyPhysics checks MainGame.me.player_char.control_enabled
            // which is Player 1. When control_enabled=false and state=None, body is Static.
            // We need to set body to Kinematic so the idle player can actually move!
            var body = idlePlayer.GetComponent<Rigidbody2D>();
            if (body != null && body.bodyType == RigidbodyType2D.Static)
            {
                body.bodyType = RigidbodyType2D.Kinematic;
            }

            // Use GoTo with AStar method for proper pathfinding around obstacles
            character.GoTo(
                (Vector2)targetPoint, 
                snap_to_node: false, 
                on_complete: OnMoveComplete, 
                on_failed: OnMoveFailed, 
                with_cinematic: false, 
                goto_method: MovementComponent.GoToMethod.AStar,
                event_on_complete: "",
                filter_astar_area: null,
                from_script: true,
                target_gd_point: null
            );
            
            isMoving = true;
            CoopMod.Logger.LogInfo($"[CutsceneFollower] Idle player moving to follow (distance: {Vector3.Distance(idlePlayer.tf.position, activePlayer.tf.position) / 96f:F1} units)");
        }

        private void OnMoveComplete()
        {
            isMoving = false;
        }

        private void OnMoveFailed()
        {
            isMoving = false;
            CoopMod.Logger.LogWarning("[CutsceneFollower] Movement failed - will retry");
        }

        private void StopFollowing()
        {
            if (cutsceneFollowActive)
            {
                CoopMod.Logger.LogInfo("[CutsceneFollower] Cutscene ended, stopping follow");
            }
            
            // Restore original speeds for BOTH players
            var p1wgo = LocalCoopManager.Instance?.Player1?.wgo;
            var p2wgo = LocalCoopManager.Instance?.Player2?.wgo;
            
            if (hasStoredPlayer1Speed && p1wgo?.data != null)
            {
                p1wgo.data.SetParam("speed", originalPlayer1Speed);
                CoopMod.Logger.LogInfo($"[CutsceneFollower] Restored P1 speed to {originalPlayer1Speed}");
                hasStoredPlayer1Speed = false;
            }
            
            if (hasStoredPlayer2Speed && p2wgo?.data != null)
            {
                p2wgo.data.SetParam("speed", originalPlayer2Speed);
                CoopMod.Logger.LogInfo($"[CutsceneFollower] Restored P2 speed to {originalPlayer2Speed}");
                hasStoredPlayer2Speed = false;
            }
            
            // CRITICAL: Reset player_controlled_by_script for BOTH players!
            // When we call GoTo with from_script=true, this flag gets set to true.
            // It's only cleared in OnComplete, but StopMovement doesn't trigger OnComplete.
            // If this stays true, UpdatePlayer() is never called and the player can't move!
            if (p1wgo?.components?.character != null)
            {
                p1wgo.components.character.player_controlled_by_script = false;
            }
            if (p2wgo?.components?.character != null)
            {
                p2wgo.components.character.player_controlled_by_script = false;
            }
            
            // Stop any ongoing movement - must do this BEFORE clearing idlePlayer
            if (idlePlayer?.components?.character != null)
            {
                var character = idlePlayer.components.character;
                
                // Force stop movement
                character.StopMovement();
                
                // Reset the astar seeker
                if (character.astar != null)
                {
                    character.astar.Clear();
                }
                
                // Reset the body velocity directly
                var body = idlePlayer.GetComponent<Rigidbody2D>();
                if (body != null)
                {
                    body.velocity = Vector2.zero;
                }
            }
            
            cutsceneFollowActive = false;
            isMoving = false;
            idlePlayer = null;
            activePlayer = null;
        }

        /// <summary>
        /// Called externally when a cutscene starts to initialize following
        /// </summary>
        public void OnCutsceneStarted(WorldGameObject interactingPlayer)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return;

            if (interactingPlayer == null)
                return;

            WorldGameObject idle = (interactingPlayer == manager.Player1?.wgo) 
                ? manager.Player2?.wgo 
                : manager.Player1?.wgo;

            if (idle != null)
            {
                StartFollowing(idle, interactingPlayer);
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                StopFollowing();
                _instance = null;
            }
        }
    }
}

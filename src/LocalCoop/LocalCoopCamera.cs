using UnityEngine;
using Com.LuisPedroFonseca.ProCamera2D;

namespace GraveyardKeeperCoop.LocalCoop
{
    /// <summary>
    /// Manages camera behavior for local co-op:
    /// 1. Centers camera between both players
    /// 2. Dynamically zooms out as players move apart
    /// </summary>
    public class LocalCoopCamera : MonoBehaviour
    {
        private static LocalCoopCamera _instance;
        public static LocalCoopCamera Instance => _instance;

        // Camera reference
        private Camera worldCamera;
        private ProCamera2D proCamera;

        // Original camera settings
        private float originalOrthographicSize;
        private bool hasStoredOriginalSize = false;

        // Zoom settings
        private const float MIN_ZOOM = 1.0f;           // Minimum zoom multiplier (normal view)
        private const float MAX_ZOOM = 2.5f;           // Maximum zoom multiplier (zoomed out)
        private const float MIN_PLAYER_DISTANCE = 800f; // Distance at which zoom starts (near edge of screen)
        private const float MAX_PLAYER_DISTANCE = 2000f; // Distance at which max zoom is reached
        private const float ZOOM_SMOOTH_TIME = 0.3f;    // How smoothly the camera zooms

        // Camera center settings
        private const float CENTER_SMOOTH_TIME = 0.15f; // How smoothly camera moves to center

        // Current state
        private float currentZoomMultiplier = 1.0f;
        private float zoomVelocity = 0f;
        private Vector3 cameraVelocity = Vector3.zero;

        // Player 2 camera target (for ProCamera2D)
        private Transform player2CameraTarget;
        private bool hasAddedPlayer2Target = false;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
            // CoopMod.Logger.LogInfo("[LocalCoopCamera] Initialized");
        }

        private void Start()
        {
            CacheCamera();
        }

        private void CacheCamera()
        {
            if (MainGame.me != null)
            {
                worldCamera = MainGame.me.world_cam;
                proCamera = CameraTools.pro_cam;

                if (worldCamera != null && !hasStoredOriginalSize)
                {
                    originalOrthographicSize = worldCamera.orthographicSize;
                    hasStoredOriginalSize = true;
                    // CoopMod.Logger.LogInfo($"[LocalCoopCamera] Cached original orthographic size: {originalOrthographicSize}");
                }
            }
        }

        private void LateUpdate()
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
            {
                // Reset zoom if local co-op is disabled
                if (hasStoredOriginalSize && worldCamera != null)
                {
                    ResetCameraZoom();
                }
                return;
            }

            if (manager.Player1 == null || manager.Player2 == null)
                return;

            // Make sure we have camera reference
            if (worldCamera == null)
            {
                CacheCamera();
                if (worldCamera == null)
                    return;
            }

            UpdateCameraForLocalCoop(manager.Player1, manager.Player2);
        }

        private void UpdateCameraForLocalCoop(PlayerComponent player1, PlayerComponent player2)
        {
            Vector3 p1Pos = player1.transform.position;
            Vector3 p2Pos = player2.transform.position;

            // Calculate distance between players
            float distance = Vector3.Distance(p1Pos, p2Pos);

            // Calculate zoom multiplier based on distance
            float targetZoom = CalculateZoomMultiplier(distance);

            // Smoothly interpolate to target zoom
            currentZoomMultiplier = Mathf.SmoothDamp(currentZoomMultiplier, targetZoom, ref zoomVelocity, ZOOM_SMOOTH_TIME);

            // Apply zoom
            float targetSize = originalOrthographicSize * currentZoomMultiplier;
            worldCamera.orthographicSize = targetSize;

            // Handle camera centering between players
            // We use ProCamera2D's multi-target system if available
            UpdateCameraTargets(player1, player2);
        }

        private float CalculateZoomMultiplier(float playerDistance)
        {
            if (playerDistance <= MIN_PLAYER_DISTANCE)
                return MIN_ZOOM;

            if (playerDistance >= MAX_PLAYER_DISTANCE)
                return MAX_ZOOM;

            // Linear interpolation between min and max zoom based on distance
            float t = (playerDistance - MIN_PLAYER_DISTANCE) / (MAX_PLAYER_DISTANCE - MIN_PLAYER_DISTANCE);
            return Mathf.Lerp(MIN_ZOOM, MAX_ZOOM, t);
        }

        private void UpdateCameraTargets(PlayerComponent player1, PlayerComponent player2)
        {
            if (proCamera == null)
                return;

            // Add Player 2 as a camera target if not already added
            if (!hasAddedPlayer2Target && player2 != null)
            {
                try
                {
                    // Create a dedicated transform for Player 2's camera target
                    // This allows us to control influence separately
                    if (player2CameraTarget == null)
                    {
                        var targetGO = new GameObject("Player2CameraTarget");
                        player2CameraTarget = targetGO.transform;
                        targetGO.transform.SetParent(player2.transform);
                        targetGO.transform.localPosition = Vector3.zero;
                    }

                    // Add Player 2 to camera targets with equal influence
                    proCamera.AddCameraTarget(player2.transform, 1f, 1f, 0.5f, Vector2.zero);
                    hasAddedPlayer2Target = true;
                    // CoopMod.Logger.LogInfo("[LocalCoopCamera] Added Player 2 as camera target");
                }
                catch (System.Exception ex)
                {
                    CoopMod.Logger.LogError($"[LocalCoopCamera] Error adding Player 2 camera target: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Reset camera to original settings when local co-op is disabled
        /// </summary>
        public void ResetCameraZoom()
        {
            if (worldCamera != null && hasStoredOriginalSize)
            {
                worldCamera.orthographicSize = originalOrthographicSize;
                currentZoomMultiplier = 1.0f;
            }

            // Remove Player 2 from camera targets
            if (proCamera != null && hasAddedPlayer2Target)
            {
                var manager = LocalCoopManager.Instance;
                if (manager?.Player2 != null)
                {
                    try
                    {
                        proCamera.RemoveCameraTarget(manager.Player2.transform, 0.5f);
                    }
                    catch { }
                }
                hasAddedPlayer2Target = false;
            }

            if (player2CameraTarget != null)
            {
                Destroy(player2CameraTarget.gameObject);
                player2CameraTarget = null;
            }
        }

        /// <summary>
        /// Called when Player 2 teleports - ensures camera is properly updated
        /// </summary>
        public void OnPlayer2Teleported(Vector3 newPosition)
        {
            // Force recalculation of camera on next frame
            // CoopMod.Logger.LogInfo($"[LocalCoopCamera] Player 2 teleported to {newPosition}");
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                ResetCameraZoom();
                _instance = null;
            }
        }
    }
}

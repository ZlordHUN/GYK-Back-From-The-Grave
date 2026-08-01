using UnityEngine;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// Displays a floating name tag above a player that follows them in world space.
    /// Uses NGUI's UILabel and converts world position to UI screen position.
    /// </summary>
    public class PlayerNameTag : MonoBehaviour
    {
        // The transform to follow (player's head anchor)
        public Transform Target { get; set; }
        
        // Camera references
        private Camera _worldCamera;
        private Camera _uiCamera;
        
        // Offset from target position in world space
        public Vector3 WorldOffset { get; set; } = new Vector3(0f, 1.25f, 0f);
        
        // Offset in pixels after screen projection
        public Vector2 PixelOffset { get; set; } = new Vector2(0f, 8f);
        
        // Whether to hide tag when target is behind camera
        public bool HideIfBehind { get; set; } = true;
        
        // The NGUI label component
        private UILabel _label;
        
        // The name being displayed
        private string _playerName;
        
        /// <summary>
        /// Initialize the name tag with the player's name
        /// </summary>
        public void Initialize(string playerName)
        {
            _playerName = playerName;

            ResolveCameras();
            
            // Setup the label
            SetupLabel();
            
            CoopMod.Logger.LogInfo(
                $"[PlayerNameTag] Initialized for player: {playerName}, " +
                $"world_camera={(_worldCamera != null ? _worldCamera.name : "NULL")}, " +
                $"ui_camera={(_uiCamera != null ? _uiCamera.name : "NULL")}, " +
                $"font={(_label?.ambigiousFont != null ? _label.ambigiousFont.ToString() : "NULL")}");
        }
        
        private void SetupLabel()
        {
            // Get or add UILabel component
            _label = gameObject.GetComponent<UILabel>();
            if (_label == null)
            {
                _label = gameObject.AddComponent<UILabel>();
            }
            
            // Try to get font from game's GUI
            Object font = null;
            int fontSize = 16;
            if (GUIElements.me?.hud != null)
            {
                // Try to find a label in the HUD to get its font
                var existingLabel =
                    GUIElements.me.hud.GetComponentInChildren<UILabel>(true);
                if (existingLabel != null)
                {
                    font = existingLabel.ambigiousFont;
                    if (existingLabel.fontSize > 0)
                        fontSize = existingLabel.fontSize;
                }
            }
            
            // Configure label appearance
            if (font != null)
                _label.ambigiousFont = font;
            else
                CoopMod.Logger.LogWarning(
                    $"[PlayerNameTag] No HUD font found for {_playerName}");

            _label.fontSize = Mathf.Clamp(fontSize, 14, 18);
            _label.alignment = NGUIText.Alignment.Center;
            _label.pivot = UIWidget.Pivot.Bottom;
            _label.effectStyle = UILabel.Effect.Outline;
            _label.effectColor = new Color(0f, 0f, 0f, 0.7f);
            _label.color = Color.white;
            _label.depth = 200; // High depth to render on top
            _label.text = _playerName;
            _label.width = 240;
            _label.height = 30;
            _label.overflowMethod = UILabel.Overflow.ShrinkContent;
            
            // Make sure widget updates
            _label.MakePixelPerfect();
            _label.CreatePanel();
            _label.MarkAsChanged();
        }

        private bool ResolveCameras()
        {
            Camera gameWorldCamera = MainGame.me?.world_cam;
            if (gameWorldCamera != null)
                _worldCamera = gameWorldCamera;
            else if (_worldCamera == null)
                _worldCamera = Camera.main;

            if (_uiCamera == null)
            {
                _uiCamera = NGUITools.FindCameraForLayer(gameObject.layer);
                if (_uiCamera == null &&
                    UICamera.list != null &&
                    UICamera.list.size > 0)
                {
                    _uiCamera = UICamera.list[0].cachedCamera;
                }
            }

            return _worldCamera != null && _uiCamera != null;
        }

        private void SetLabelVisible(bool visible)
        {
            if (_label != null && _label.enabled != visible)
            {
                _label.enabled = visible;
                if (visible)
                    _label.MarkAsChanged();
            }
        }
        
        /// <summary>
        /// Update the displayed name
        /// </summary>
        public void SetName(string newName)
        {
            _playerName = newName;
            if (_label != null)
            {
                _label.text = newName;
            }
        }
        
        /// <summary>
        /// Set the label color
        /// </summary>
        public void SetColor(Color color)
        {
            if (_label != null)
            {
                _label.color = color;
            }
        }
        
        private void LateUpdate()
        {
            // Keep this GameObject active. Disabling it here prevents
            // LateUpdate from ever running again when the camera/target
            // becomes valid or the player comes back on screen.
            if (Target == null || !ShouldRenderNameTag() || !ResolveCameras())
            {
                SetLabelVisible(false);
                return;
            }
            
            var worldCam = _worldCamera;
            if (worldCam == null)
            {
                SetLabelVisible(false);
                return;
            }
            
            // Calculate world position
            Vector3 worldPos = Target.position + WorldOffset;
            
            // Convert to screen space
            Vector3 screenPos = worldCam.WorldToScreenPoint(worldPos);
            
            // Hide if behind camera
            if (HideIfBehind && screenPos.z <= 0f)
            {
                SetLabelVisible(false);
                return;
            }
            
            // Map screen position to UI world space
            // The UI camera's depth determines the plane we project onto
            float depth = Mathf.Abs(_uiCamera.transform.position.z - _uiCamera.nearClipPlane);
            Vector3 uiPos = _uiCamera.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, depth));
            
            // Apply position
            transform.position = uiPos;
            
            // Apply pixel offset (in local space)
            transform.localPosition += new Vector3(PixelOffset.x, PixelOffset.y, 0f);
            
            SetLabelVisible(true);
        }

        private static bool ShouldRenderNameTag()
        {
            if (!MainGame.game_started)
                return false;

            // Name tags live on their own UI-root panel and do not need the HUD.
            // Keep them visible during free-roam and cinematics, hiding them only
            // while a full game window/menu is open.
            return MainGame.me?.player_char != null && BaseGUI.all_guis_closed;
        }
        
        private void OnDestroy()
        {
            CoopMod.Logger.LogInfo($"[PlayerNameTag] Destroyed for player: {_playerName}");
        }
    }
}

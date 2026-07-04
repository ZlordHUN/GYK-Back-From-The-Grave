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
            
            // Get cameras
            _worldCamera = Camera.main;
            
            // Find NGUI camera
            if (UICamera.list != null && UICamera.list.size > 0)
            {
                _uiCamera = UICamera.list[0].cachedCamera;
            }
            
            // Setup the label
            SetupLabel();
            
            CoopMod.Logger.LogInfo($"[PlayerNameTag] Initialized for player: {playerName}");
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
            UIFont font = null;
            if (GUIElements.me?.hud != null)
            {
                // Try to find a label in the HUD to get its font
                var existingLabel = GUIElements.me.hud.GetComponentInChildren<UILabel>();
                if (existingLabel != null)
                {
                    font = existingLabel.bitmapFont;
                }
            }
            
            // Configure label appearance
            _label.bitmapFont = font;
            _label.fontSize = 16;
            _label.alignment = NGUIText.Alignment.Center;
            _label.pivot = UIWidget.Pivot.Bottom;
            _label.effectStyle = UILabel.Effect.Outline;
            _label.effectColor = new Color(0f, 0f, 0f, 0.7f);
            _label.color = Color.white;
            _label.depth = 200; // High depth to render on top
            _label.text = _playerName;
            
            // Make sure widget updates
            _label.MakePixelPerfect();
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
            // Safety checks
            if (Target == null || _uiCamera == null)
            {
                if (gameObject.activeSelf)
                {
                    gameObject.SetActive(false);
                }
                return;
            }
            
            // Get world camera (it can change)
            var worldCam = _worldCamera ?? Camera.main;
            if (worldCam == null)
            {
                if (gameObject.activeSelf)
                {
                    gameObject.SetActive(false);
                }
                return;
            }
            
            // Calculate world position
            Vector3 worldPos = Target.position + WorldOffset;
            
            // Convert to screen space
            Vector3 screenPos = worldCam.WorldToScreenPoint(worldPos);
            
            // Hide if behind camera
            if (HideIfBehind && screenPos.z <= 0f)
            {
                if (gameObject.activeSelf)
                {
                    gameObject.SetActive(false);
                }
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
            
            // Show if hidden
            if (!gameObject.activeSelf)
            {
                gameObject.SetActive(true);
            }
        }
        
        private void OnDestroy()
        {
            CoopMod.Logger.LogInfo($"[PlayerNameTag] Destroyed for player: {_playerName}");
        }
    }
}

using System.Collections;
using UnityEngine;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// Displays a temporary chat bubble above a player's head.
    /// The bubble appears when the player sends a message and fades out after a duration.
    /// </summary>
    public class ChatBubble : MonoBehaviour
    {
        // The transform to follow (player's head anchor)
        public Transform Target { get; set; }
        
        // Camera references
        private Camera _worldCamera;
        private Camera _uiCamera;
        
        // Offset from target position in world space
        public Vector3 WorldOffset { get; set; } = new Vector3(0f, 1.5f, 0f);
        
        // The NGUI label component
        private UILabel _label;
        
        // Animation state
        private float _displayDuration = 5f;
        private float _fadeOutDuration = 1f;
        private float _timeRemaining;
        private bool _isFading;
        
        // Max width for word wrap
        private const int MAX_WIDTH = 200;
        private const int PADDING = 8;
        
        /// <summary>
        /// Initialize the chat bubble
        /// </summary>
        public void Initialize()
        {
            // Get cameras
            _worldCamera = Camera.main;
            
            // Find NGUI camera
            _uiCamera = NGUITools.FindCameraForLayer(gameObject.layer);
            if (_uiCamera == null && UICamera.list != null && UICamera.list.size > 0)
            {
                _uiCamera = UICamera.list[0].cachedCamera;
            }

            CoopMod.Logger.LogInfo($"[ChatBubble] Initialize: worldCamera={(_worldCamera != null ? _worldCamera.name : "NULL")}, uiCamera={(_uiCamera != null ? _uiCamera.name : "NULL")}, layer={gameObject.layer}");
            
            SetupUI();
        }
        
        private void SetupUI()
        {
            // Get font from HUD
            Object font = null;
            int fontSize = 14;
            if (GUIElements.me?.hud != null)
            {
                var existingLabel = GUIElements.me.hud.GetComponentInChildren<UILabel>(true);
                if (existingLabel != null)
                {
                    font = existingLabel.ambigiousFont;
                    fontSize = existingLabel.fontSize > 0 ? existingLabel.fontSize : fontSize;
                }
            }
            
            // Create background texture
            var bgTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            bgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.75f));
            bgTex.Apply();
            
            // Create background as UITexture
            var bgObj = new GameObject("BubbleBg");
            bgObj.transform.SetParent(transform, false);
            bgObj.layer = gameObject.layer;
            var bgTexture = bgObj.AddComponent<UITexture>();
            bgTexture.mainTexture = bgTex;
            bgTexture.width = MAX_WIDTH + PADDING * 2;
            bgTexture.height = 30;
            bgTexture.depth = 199;
            bgTexture.pivot = UIWidget.Pivot.Bottom;
            
            // Create label
            _label = gameObject.AddComponent<UILabel>();
            if (font != null)
            {
                _label.ambigiousFont = font;
            }
            else
            {
                CoopMod.Logger.LogWarning("[ChatBubble] No HUD font found; bubble text may not render");
            }
            _label.fontSize = Mathf.Clamp(fontSize, 12, 18);
            _label.alignment = NGUIText.Alignment.Center;
            _label.pivot = UIWidget.Pivot.Bottom;
            _label.effectStyle = UILabel.Effect.Outline;
            _label.effectColor = new Color(0f, 0f, 0f, 0.5f);
            _label.color = Color.white;
            _label.depth = 200;
            _label.overflowMethod = UILabel.Overflow.ResizeHeight;
            _label.width = MAX_WIDTH;
            _label.multiLine = true;
            _label.supportEncoding = false;
            
            // Position label slightly above background center
            _label.transform.localPosition = new Vector3(0, PADDING, 0);
            
            // Initially hidden
            gameObject.SetActive(false);
            CoopMod.Logger.LogInfo($"[ChatBubble] UI setup complete: font={(font != null ? font.ToString() : "NULL")}, fontSize={_label.fontSize}");
        }
        
        /// <summary>
        /// Show a message in the bubble
        /// </summary>
        public void ShowMessage(string message, float duration = 5f)
        {
            if (_label == null)
            {
                CoopMod.Logger.LogWarning($"[ChatBubble] Cannot show '{message}' because label is null");
                return;
            }
            
            _label.text = message;
            _label.MakePixelPerfect();
            
            // Resize background to fit text
            var bgTexture = GetComponentInChildren<UITexture>();
            if (bgTexture != null)
            {
                bgTexture.width = Mathf.Max(_label.width + PADDING * 2, 50);
                bgTexture.height = _label.height + PADDING * 2;
            }
            
            _displayDuration = duration;
            _timeRemaining = duration;
            _isFading = false;
            
            // Reset alpha
            SetAlpha(1f);
            
            gameObject.SetActive(true);
            
            CoopMod.Logger.LogInfo($"[ChatBubble] Showing: {message}");
        }
        
        /// <summary>
        /// Hide the bubble immediately
        /// </summary>
        public void Hide()
        {
            gameObject.SetActive(false);
            _timeRemaining = 0;
        }
        
        private void Update()
        {
            if (_timeRemaining > 0)
            {
                _timeRemaining -= Time.deltaTime;
                
                // Start fading when time is almost up
                if (_timeRemaining <= _fadeOutDuration && !_isFading)
                {
                    _isFading = true;
                }
                
                if (_isFading)
                {
                    float alpha = _timeRemaining / _fadeOutDuration;
                    SetAlpha(alpha);
                }
                
                if (_timeRemaining <= 0)
                {
                    Hide();
                }
            }
        }
        
        private void SetAlpha(float alpha)
        {
            if (_label != null)
            {
                var color = _label.color;
                color.a = alpha;
                _label.color = color;
            }
            
            var bgTexture = GetComponentInChildren<UITexture>();
            if (bgTexture != null)
            {
                var color = bgTexture.color;
                color.a = alpha * 0.75f; // Background slightly more transparent
                bgTexture.color = color;
            }
        }
        
        private void LateUpdate()
        {
            // Follow target
            if (Target == null || _uiCamera == null || !gameObject.activeSelf)
                return;
            
            var worldCam = _worldCamera ?? Camera.main;
            if (worldCam == null)
                return;
            
            // Calculate world position
            Vector3 worldPos = Target.position + WorldOffset;
            
            // Convert to screen space
            Vector3 screenPos = worldCam.WorldToScreenPoint(worldPos);
            
            // Hide if behind camera
            if (screenPos.z <= 0f)
            {
                if (gameObject.activeSelf)
                    gameObject.SetActive(false);
                return;
            }
            
            // Map screen position to UI world space
            float depth = Mathf.Abs(_uiCamera.transform.position.z - _uiCamera.nearClipPlane);
            Vector3 uiPos = _uiCamera.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, depth));
            
            transform.position = uiPos;
        }
    }
    
    /// <summary>
    /// Manages chat bubbles for all players
    /// </summary>
    public static class ChatBubbleManager
    {
        private static ChatBubble _localPlayerBubble;
        private static ChatBubble _remotePlayerBubble;
        private static bool _isShowingChatBubble;
        private const long NO_SPEECH_BUBBLE_ID = long.MinValue;
        private static long _localSpeechBubbleId = NO_SPEECH_BUBBLE_ID;
        private static long _remoteSpeechBubbleId = NO_SPEECH_BUBBLE_ID;
        public static bool IsShowingChatBubble => _isShowingChatBubble;
        
        /// <summary>
        /// Show a chat bubble above the local player
        /// </summary>
        public static void ShowLocalMessage(string message, float duration = 5f)
        {
            if (ShowGameSpeechBubble(MainGame.me?.player, message, "local", out long speakerId))
            {
                _localSpeechBubbleId = speakerId;
            }
        }
        
        /// <summary>
        /// Show a chat bubble above the remote player
        /// </summary>
        public static void ShowRemoteMessage(string message, float duration = 5f)
        {
            var remoteWGO = Network.OnlineCoopManager.Instance?.GetRemotePlayer();
            if (ShowGameSpeechBubble(remoteWGO, message, "remote", out long speakerId))
            {
                _remoteSpeechBubbleId = speakerId;
            }
        }

        private static bool ShowGameSpeechBubble(WorldGameObject speaker, string message, string source, out long speakerId)
        {
            speakerId = NO_SPEECH_BUBBLE_ID;

            if (speaker == null)
            {
                CoopMod.Logger.LogWarning($"[ChatBubble] Cannot show {source} speech bubble '{message}' - speaker WGO is null");
                return false;
            }

            if (speaker.bubble_pos_tf == null)
            {
                CoopMod.Logger.LogWarning($"[ChatBubble] Cannot show {source} speech bubble '{message}' - speaker bubble_pos_tf is null");
                return false;
            }

            speakerId = speaker.unique_id != 0 ? speaker.unique_id : speaker.GetInstanceID();

            try
            {
                HideGameSpeechBubble(speakerId);

                _isShowingChatBubble = true;
                SpeechBubbleGUI.ShowMessage(
                    speaker_id: speakerId,
                    txt: message,
                    link: speaker.bubble_pos_tf,
                    on_disappeared: null,
                    show_to_left: false,
                    use_world_cam: true,
                    type: SpeechBubbleGUI.SpeechBubbleType.Talk,
                    is_player: false,
                    voice: SmartSpeechEngine.VoiceID.Player
                );
                CoopMod.Logger.LogInfo($"[ChatBubble] Showing game speech bubble ({source}) speakerId={speakerId}, text='{message}'");
                return true;
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[ChatBubble] Failed to show game speech bubble ({source}): {ex.Message}");
                return false;
            }
            finally
            {
                _isShowingChatBubble = false;
            }
        }

        private static void HideGameSpeechBubble(long speakerId)
        {
            try
            {
                if (SpeechBubbleGUI.all != null && SpeechBubbleGUI.all.ContainsKey(speakerId))
                {
                    var bubble = SpeechBubbleGUI.all[speakerId];
                    if (bubble != null)
                    {
                        bubble.ForceHide(false);
                    }

                    SpeechBubbleGUI.all.Remove(speakerId);
                    CoopMod.Logger.LogInfo($"[ChatBubble] Replaced existing game speech bubble for speakerId={speakerId}");
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[ChatBubble] Failed to hide existing game speech bubble for speakerId={speakerId}: {ex.Message}");
            }
        }
        
        private static ChatBubble CreateBubbleForPlayer(Transform playerTransform, string name)
        {
            if (GUIElements.me?.hud == null)
            {
                CoopMod.Logger.LogWarning($"[ChatBubble] Cannot create {name} - HUD is null");
                return null;
            }
            
            var hudGO = GUIElements.me.hud.gameObject;
            
            // Create head anchor
            var headAnchor = new GameObject("ChatBubbleAnchor");
            headAnchor.transform.SetParent(playerTransform, false);
            headAnchor.transform.localPosition = new Vector3(0f, 1.0f, 0f);
            headAnchor.layer = hudGO.layer;
            
            // Create bubble as child of HUD
            var bubbleObj = NGUITools.AddChild(hudGO);
            bubbleObj.name = name;
            bubbleObj.layer = hudGO.layer;
            
            var bubble = bubbleObj.AddComponent<ChatBubble>();
            bubble.Target = headAnchor.transform;
            bubble.WorldOffset = new Vector3(0f, 0.5f, 0f);
            bubble.Initialize();

            CoopMod.Logger.LogInfo($"[ChatBubble] Created {name} for target={playerTransform.name}, hud={hudGO.name}, layer={hudGO.layer}");
            
            return bubble;
        }
        
        /// <summary>
        /// Clean up bubbles when coop ends
        /// </summary>
        public static void Cleanup()
        {
            if (_localSpeechBubbleId != NO_SPEECH_BUBBLE_ID)
            {
                HideGameSpeechBubble(_localSpeechBubbleId);
                _localSpeechBubbleId = NO_SPEECH_BUBBLE_ID;
            }

            if (_remoteSpeechBubbleId != NO_SPEECH_BUBBLE_ID)
            {
                HideGameSpeechBubble(_remoteSpeechBubbleId);
                _remoteSpeechBubbleId = NO_SPEECH_BUBBLE_ID;
            }

            if (_localPlayerBubble != null)
            {
                Object.Destroy(_localPlayerBubble.gameObject);
                _localPlayerBubble = null;
            }
            
            if (_remotePlayerBubble != null)
            {
                Object.Destroy(_remotePlayerBubble.gameObject);
                _remotePlayerBubble = null;
            }
        }
    }
}

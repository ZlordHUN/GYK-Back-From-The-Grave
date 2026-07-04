using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Steamworks;
using GraveyardKeeperCoop.Multiplayer;
using GraveyardKeeperCoop.Network;
using GraveyardKeeperCoop.Utils;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// In-game chat overlay that appears during multiplayer sessions.
    /// Provides a draggable chat window with message history and text input.
    /// Press Enter to focus input, type message, Enter to send, Escape to cancel.
    /// </summary>
    public class ChatOverlay : MonoBehaviour
    {
        private static ChatOverlay _instance;
        public static ChatOverlay Instance => _instance;

        // UI Components
        private UIPanel _panel;
        private UITexture _background;
        private UIPanel _logClipPanel;
        private UILabel _logLabel;
        private UILabel _inputLabel;
        private UIInput _input;
        private bool _focused;
        private HUD _cachedHud;
        private int _lastScreenWidth;
        private int _lastScreenHeight;
        private int _lastSubmitFrame = -1;

        // Chat history
        private readonly List<string> _history = new List<string>(256);
        private const int MAX_HISTORY = 200;

        // Auto-fade state — after INACTIVITY_HIDE_SEC with no new messages and no typing,
        // the overlay fades out (panel alpha=0) but stays Active so Update() keeps polling
        // for Enter to bring it back. Pressing Enter while faded re-shows it and focuses
        // the input, the way most in-game chats work.
        private const float INACTIVITY_HIDE_SEC = 10f;
        private const float FADE_DURATION = 0.25f;
        private float _lastActivityTime;
        private bool _faded;

        // Tab-completion state
        private string _tabCompletionText;
        private string[] _tabCompletionOptions;
        private int _tabCompletionIndex;

        // Font settings (shared with NameTag)
        public static int FontSize { get; private set; }
        public static object Font { get; private set; }

        // Dimensions
        private const float BG_WIDTH = 460f;
        private const float BG_HEIGHT = 220f;
        private const float CLIP_WIDTH = 390f;
        private const float CLIP_HEIGHT = 180f;
        private const float PADDING = 8f;
        private const float SCREEN_PADDING_X = 20f;
        private const float SCREEN_PADDING_Y = 20f;

        /// <summary>
        /// Create and show the chat overlay
        /// </summary>
        public static ChatOverlay Create()
        {
            if (_instance != null)
            {
                _instance.Show();
                return _instance;
            }

            var uiRoot = MainGame.me?.ui_root;
            if (uiRoot == null)
            {
                CoopMod.Logger.LogWarning("[ChatOverlay] Cannot create - UI root not found");
                return null;
            }

            var overlayObj = new GameObject("ChatOverlay");
            overlayObj.layer = LayerMask.NameToLayer("UI");
            overlayObj.transform.SetParent(uiRoot.transform, false);

            _instance = overlayObj.AddComponent<ChatOverlay>();
            return _instance;
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);

            // Seed the inactivity timer to "now" so we don't immediately fade on the first
            // frame just because _lastActivityTime defaults to 0 while Time.unscaledTime
            // is already large.
            _lastActivityTime = Time.unscaledTime;

            BuildUI();
            Hide();

            CoopMod.Logger.LogInfo("[ChatOverlay] Created");
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        private void BuildUI()
        {
            // We have to wait until the HUD is not only present but ACTIVE in hierarchy,
            // otherwise reparenting ourselves under it will deactivate us and the
            // "FinishSetupNextFrame" coroutine (which creates our UIInput) can't start.
            // That's exactly what used to leave `_input == null` and caused Enter to do nothing.
            var hud = GUIElements.me?.hud;
            if (hud != null && hud.gameObject.activeInHierarchy)
            {
                BuildUIInternal(hud.gameObject);
                return;
            }

            CoopMod.Logger.LogInfo("[ChatOverlay] HUD not ready/active yet, deferring UI build");
            StartCoroutine(BuildUIWhenReady());
        }

        private IEnumerator BuildUIWhenReady()
        {
            // Wait for the HUD to exist AND be active in hierarchy.
            while (GUIElements.me?.hud == null || !GUIElements.me.hud.gameObject.activeInHierarchy)
                yield return null;

            CoopMod.Logger.LogInfo("[ChatOverlay] HUD is active \u2014 building UI now");
            BuildUIInternal(GUIElements.me.hud.gameObject);
        }

        private void BuildUIInternal(GameObject hudGO)
        {

            var hudPanel = hudGO.GetComponentInParent<UIPanel>();
            if (hudPanel == null)
                hudPanel = hudGO.AddComponent<UIPanel>();

            int hudMaxDepth = FindMaxWidgetDepth(hudPanel);
            int baseDepth = hudMaxDepth + 10;

            // Parent to HUD
            _cachedHud = hudGO.GetComponent<HUD>();
            _lastScreenWidth = Screen.width;
            _lastScreenHeight = Screen.height;
            transform.SetParent(hudGO.transform, false);
            transform.localScale = Vector3.one;
            SetLayerRecursively(transform, hudGO.layer);

            // Get font from existing HUD label
            var sampleLabel = hudGO.GetComponentInChildren<UILabel>(true);
            var ambFont = sampleLabel?.ambigiousFont;
            var sampleSize = sampleLabel?.fontSize ?? 18;
            Font = ambFont;
            FontSize = sampleSize;

            // Create main panel
            _panel = gameObject.GetComponent<UIPanel>() ?? gameObject.AddComponent<UIPanel>();
            _panel.depth = baseDepth;
            _panel.clipping = UIDrawCall.Clipping.None;

            // Create background
            var bgTex = CreateWhiteTexture();
            _background = NGUITools.AddChild<UITexture>(gameObject);
            _background.name = "ChatBg";
            _background.mainTexture = bgTex;
            _background.color = new Color(0f, 0f, 0f, 0.7f);
            _background.width = (int)BG_WIDTH;
            _background.height = (int)BG_HEIGHT;
            _background.depth = baseDepth;

            var drag = gameObject.GetComponent<UIDragObject>();
            if (drag != null)
            {
                Destroy(drag);
            }

            // Create log clip panel (for scrolling chat history)
            var logGO = NGUITools.AddChild(gameObject);
            logGO.name = "LogClip";
            _logClipPanel = logGO.AddComponent<UIPanel>();
            _logClipPanel.depth = baseDepth + 1;
            _logClipPanel.clipping = UIDrawCall.Clipping.SoftClip;
            _logClipPanel.baseClipRegion = new Vector4(0f, 0f, CLIP_WIDTH, CLIP_HEIGHT);
            _logClipPanel.clipSoftness = new Vector2(2f, 2f);
            _logClipPanel.cachedTransform.localPosition = new Vector3(-BG_WIDTH * 0.5f + PADDING + CLIP_WIDTH * 0.5f, 0f, 0f);

            // Create log label
            var labelGO = NGUITools.AddChild(logGO);
            _logLabel = labelGO.AddComponent<UILabel>();
            _logLabel.ambigiousFont = ambFont;
            _logLabel.fontSize = sampleSize;
            _logLabel.alignment = NGUIText.Alignment.Left;
            _logLabel.pivot = UIWidget.Pivot.BottomLeft;
            _logLabel.depth = baseDepth + 2;
            _logLabel.color = Color.white;
            _logLabel.supportEncoding = true;
            _logLabel.multiLine = true;
            _logLabel.maxLineCount = 0;
            _logLabel.overflowMethod = UILabel.Overflow.ResizeHeight;
            _logLabel.width = (int)(CLIP_WIDTH - PADDING * 2f);
            _logLabel.symbolStyle = NGUIText.SymbolStyle.Colored;
            _logLabel.cachedTransform.localPosition = new Vector3(
                -CLIP_WIDTH * 0.5f + PADDING,
                -CLIP_HEIGHT * 0.5f + PADDING,
                0f
            );

            // Create input label
            var inputGO = NGUITools.AddChild(gameObject);
            _inputLabel = inputGO.AddComponent<UILabel>();
            _inputLabel.ambigiousFont = ambFont;
            _inputLabel.fontSize = sampleSize;
            _inputLabel.overflowMethod = UILabel.Overflow.ClampContent;
            _inputLabel.alignment = NGUIText.Alignment.Left;
            _inputLabel.pivot = UIWidget.Pivot.BottomLeft;
            _inputLabel.text = "";
            _inputLabel.width = (int)(BG_WIDTH - PADDING * 4f);
            _inputLabel.height = 24;
            _inputLabel.multiLine = false;
            _inputLabel.depth = baseDepth + 3;
            _inputLabel.supportEncoding = true;
            _inputLabel.cachedTransform.localPosition = new Vector3(
                -BG_WIDTH * 0.5f + PADDING * 2f,
                -BG_HEIGHT * 0.5f + PADDING * 2f,
                0f
            );
            NGUITools.AddWidgetCollider(inputGO);

            RepositionToBottomLeft();

            // Refresh panels
            foreach (var w in GetComponentsInChildren<UIWidget>(true))
            {
                w.CreatePanel();
                w.MarkAsChanged();
            }
            hudPanel.Refresh();
            _panel.Refresh();
            _logClipPanel.Refresh();

            NGUITools.SetActiveSelf(gameObject, true);
            NGUITools.SetActiveChildren(gameObject, true);

            // Finish setup next frame
            StartCoroutine(FinishSetupNextFrame());
        }

        private IEnumerator FinishSetupNextFrame()
        {
            yield return null;

            // Enable keyboard input for NGUI
            EnableNguiKeyboard();

            // Wait for input label panel
            if (_inputLabel.panel == null)
                yield return null;
            if (_inputLabel.panel == null)
            {
                _inputLabel.CreatePanel();
                _inputLabel.MarkAsChanged();
            }

            // Create UIInput
            var inputGO = _inputLabel.gameObject;
            _input = inputGO.AddComponent<UIInput>();
            _input.label = _inputLabel;
            _input.value = "";
            _input.activeTextColor = Color.white;
            _input.defaultText = "[ Press Enter to chat ]";
            _input.validation = UIInput.Validation.None;
            _input.onSubmit.Clear();
            _input.onSubmit.Add(new EventDelegate(OnSubmit));
            _input.onReturnKey = UIInput.OnReturnKey.Submit;

            CoopMod.Logger.LogInfo("[ChatOverlay] Input setup complete");
        }

        private void EnableNguiKeyboard()
        {
            if (UICamera.list != null)
            {
                foreach (var cam in UICamera.list)
                {
                    cam.useKeyboard = true;
                    cam.eventReceiverMask |= (1 << gameObject.layer);
                    cam.submitKey0 = KeyCode.Return;
                    cam.submitKey1 = KeyCode.KeypadEnter;
                }
            }
        }

        private void Update()
        {
            if (_input == null)
                return;

            if (Screen.width != _lastScreenWidth || Screen.height != _lastScreenHeight)
            {
                CoopMod.Logger.LogInfo($"[ChatOverlay] Resolution changed from {_lastScreenWidth}x{_lastScreenHeight} to {Screen.width}x{Screen.height}");
                RepositionToBottomLeft();
            }

            bool enterDown  = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
            bool escapeDown = Input.GetKeyDown(KeyCode.Escape);
            bool currentlyFocused = _focused || _input.isSelected;

            if (enterDown && currentlyFocused)
            {
                SubmitCurrentInput("Update");
                return;
            }

            // Enter — open/focus the chat when we're not already typing.
            // Works whether the overlay is faded or fully visible.
            if (enterDown && !currentlyFocused)
            {
                CoopMod.Logger.LogInfo($"[ChatOverlay] Enter pressed — revealing + focusing input (faded={_faded})");
                if (_faded) Reveal();
                _focused = true;
                try
                {
                    UICamera.selectedObject = _input.gameObject;
                    _input.isSelected = true;
                    UICamera.Notify(_input.gameObject, "OnSelect", true);
                }
                catch (System.Exception ex)
                {
                    CoopMod.Logger.LogWarning($"[ChatOverlay] Failed to focus input: {ex.Message}");
                }
                BumpActivity();
                return;
            }

            // Escape — cancel typing.
            if (escapeDown && currentlyFocused)
            {
                _input.value = "";
                _tabCompletionText = null;
                _tabCompletionOptions = null;
                _tabCompletionIndex = 0;
                UICamera.selectedObject = null;
                _input.isSelected = false;
                _focused = false;
                BumpActivity();
                return;
            }

            // Tab — autocomplete command arguments.
            if (Input.GetKeyDown(KeyCode.Tab) && currentlyFocused && _input.value.StartsWith("/"))
            {
                string result = Multiplayer.DebugChatCommands.TryAutocomplete(
                    _input.value, ref _tabCompletionText, ref _tabCompletionOptions, ref _tabCompletionIndex);
                if (result != null)
                {
                    _input.value = result;
                    _input.label.ProcessText();
                }
                BumpActivity();
                return;
            }

            // If user typed something different, reset completion state
            if (currentlyFocused && _tabCompletionText != null && _input.value != _tabCompletionText)
            {
                _tabCompletionText = null;
                _tabCompletionOptions = null;
                _tabCompletionIndex = 0;
            }

            // While the user is actively typing, keep the overlay awake.
            if (currentlyFocused)
            {
                _lastActivityTime = Time.unscaledTime;
            }

            // Inactivity timeout → fade out
            if (!_faded && !currentlyFocused && _panel != null &&
                Time.unscaledTime - _lastActivityTime >= INACTIVITY_HIDE_SEC)
            {
                FadeOut();
            }
        }

        /// <summary>
        /// Mark "something happened" — any new message or user input bumps the
        /// inactivity timer and un-fades the overlay if it was hidden.
        /// </summary>
        private void BumpActivity()
        {
            _lastActivityTime = Time.unscaledTime;
            if (_faded)
                Reveal();
        }

        private void FadeOut()
        {
            if (_panel == null) return;
            _faded = true;
            _panel.alpha = 0f;
        }

        private void Reveal()
        {
            if (_panel == null) return;
            _faded = false;
            _panel.alpha = 1f;
            _lastActivityTime = Time.unscaledTime;
        }

        private void OnSubmit()
        {
            SubmitCurrentInput("NGUI");
        }

        private void SubmitCurrentInput(string source)
        {
            if (_lastSubmitFrame == Time.frameCount)
            {
                CoopMod.Logger.LogInfo($"[ChatOverlay] Ignoring duplicate submit from {source} on frame {Time.frameCount}");
                return;
            }

            _lastSubmitFrame = Time.frameCount;

            var msg = _input.value ?? "";
            CoopMod.Logger.LogInfo($"[ChatOverlay] Submit from {source}: raw='{msg}', focused={_focused}, selected={_input.isSelected}");
            if (string.IsNullOrWhiteSpace(msg))
            {
                _input.value = "";
                UICamera.selectedObject = null;
                _input.isSelected = false;
                _focused = false;
                return;
            }

            SendChatMessage(msg.Trim());

            _input.value = "";
            _input.label.ProcessText();
            UICamera.selectedObject = null;
            _input.isSelected = false;
            _focused = false;
        }

        /// <summary>
        /// Send a chat message to other players
        /// </summary>
        private void SendChatMessage(string text)
        {
            CoopMod.Logger.LogInfo($"[ChatOverlay] Sending: {text}");

            if (DebugChatCommands.TryExecute(text, AddSystemMessage))
            {
                return;
            }

            // Add to local history
            string myName = SteamFriends.GetPersonaName() ?? "Player";
            AddLine(FormatMessage(myName, text, true));

            // Show chat bubble above local player if enabled
            if (AreChatBubblesEnabled())
            {
                float duration = ModConfig.ChatBubbleDuration?.Value ?? 5f;
                ChatBubbleManager.ShowLocalMessage(text, duration);
            }

            // Send via lobby chat
            string formattedMsg = $"{myName}: {text}";
            ChatManager.AddMessage(formattedMsg);
            Multiplayer.LobbyChatSync.BroadcastChatMessage(formattedMsg);
        }

        /// <summary>
        /// Receive a chat message from another player
        /// </summary>
        public void ReceiveMessage(string senderName, string text)
        {
            AddLine(FormatMessage(senderName, text, false));

            // Show chat bubble above remote player if enabled
            if (AreChatBubblesEnabled())
            {
                float duration = ModConfig.ChatBubbleDuration?.Value ?? 5f;
                ChatBubbleManager.ShowRemoteMessage(text, duration);
            }
        }

        private static bool AreChatBubblesEnabled()
        {
            // Chat bubbles are hardcoded enabled while the settings row is hidden.
            // return ModConfig.ShowChatBubbles?.Value == true;
            return true;
        }

        /// <summary>
        /// Add a line to chat history
        /// </summary>
        public void AddLine(string richText)
        {
            if (string.IsNullOrEmpty(richText))
                return;

            _history.Add(richText);
            if (_history.Count > MAX_HISTORY)
                _history.RemoveRange(0, _history.Count - MAX_HISTORY);

            CoopMod.Logger.LogInfo($"[ChatOverlay] AddLine history={_history.Count}: {richText}");

            if (_logLabel != null)
            {
                _logLabel.text = string.Join("\n", _history.ToArray());
                _logLabel.ProcessText();
                _logLabel.MarkAsChanged();
                _logClipPanel?.Refresh();
                _panel?.Refresh();
            }

            // New message → bring the overlay back if it was faded and reset the timer.
            BumpActivity();
        }

        /// <summary>
        /// Add a system message
        /// </summary>
        public void AddSystemMessage(string message)
        {
            var time = DateTime.Now.ToString("HH:mm");
            AddLine($"[{time}] [i]{message}[/i]");
        }

        /// <summary>
        /// Format a chat message with timestamp and color
        /// </summary>
        private string FormatMessage(string sender, string text, bool isMe)
        {
            var time = DateTime.Now.ToString("HH:mm");
            var nameColor = isMe ? "00FF00" : "00FFFF"; // Green for self, cyan for others
            return $"[{time}] [{nameColor}][b]{sender}[/b][-]: {text}";
        }

        public void Show()
        {
            gameObject.SetActive(true);
            Reveal();
        }

        public void Hide()
        {
            // Fade out visually but keep the GameObject active so Enter can still wake us up.
            if (_focused && _input != null)
                _input.isSelected = false;
            _focused = false;
            FadeOut();
        }

        public void Toggle()
        {
            if (_faded)
                Show();
            else
                Hide();
        }

        public bool IsVisible => gameObject.activeSelf && !_faded;
        public bool IsFocused => _focused;

        #region Helpers

        private void RepositionToBottomLeft()
        {
            var hud = _cachedHud ?? GUIElements.me?.hud;
            UIRoot uiRoot = hud != null ? hud.GetComponentInParent<UIRoot>() : MainGame.me?.ui_root;
            int manualHeight = uiRoot != null ? uiRoot.manualHeight : 800;
            if (manualHeight <= 0)
            {
                manualHeight = 800;
            }

            float scaleFactor = (float)Screen.height / manualHeight;
            if (scaleFactor <= 0.01f)
            {
                scaleFactor = 1f;
            }

            float virtualWidth = Screen.width / scaleFactor;
            float virtualHeight = manualHeight;
            float posX = -virtualWidth * 0.5f + SCREEN_PADDING_X + BG_WIDTH * 0.5f;
            float posY = -virtualHeight * 0.5f + SCREEN_PADDING_Y + BG_HEIGHT * 0.5f;

            transform.localPosition = new Vector3(posX, posY, 0f);
            _lastScreenWidth = Screen.width;
            _lastScreenHeight = Screen.height;

            CoopMod.Logger.LogInfo($"[ChatOverlay] Anchored bottom-left: screen={Screen.width}x{Screen.height}, manualHeight={manualHeight}, virtual={virtualWidth:F1}x{virtualHeight:F1}, pos=({posX:F1},{posY:F1})");
        }

        private static void SetLayerRecursively(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursively(t.GetChild(i), layer);
        }

        private static int FindMaxWidgetDepth(UIPanel panel)
        {
            int max = 0;
            var widgets = panel.GetComponentsInChildren<UIWidget>(true);
            foreach (var w in widgets)
            {
                if (w != null && w.depth > max)
                    max = w.depth;
            }
            return max;
        }

        private static Texture2D CreateWhiteTexture()
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            return tex;
        }

        #endregion
    }
}

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
    /// Press Enter or Y to focus input, type message, Enter to send, Escape to cancel.
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
        private UITexture _completionListBackground;
        private readonly UILabel[] _completionOptionLabels =
            new UILabel[MAX_VISIBLE_COMPLETIONS];
        private UILabel _completionFooterLabel;
        private UILabel _inputSuggestionLabel;
        private UILabel _inputLabel;
        private UIInput _input;
        private string _suggestionSourceText;
        private DebugChatCommands.CommandCompletion[] _completionCandidates =
            new DebugChatCommands.CommandCompletion[0];
        private int _completionIndex;
        private int _completionWindowStart;
        private int _completionLineHeight;
        private bool _focused;
        private HUD _cachedHud;
        private int _lastScreenWidth;
        private int _lastScreenHeight;
        private int _lastSubmitFrame = -1;
        private bool _inputSetupLogged;
        private bool _inputWasAllowed;
        private float _acceptInputAfterTime;
        private bool _endingSession;

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
        private const int MAX_VISIBLE_COMPLETIONS = 8;

        /// <summary>
        /// Create and show the chat overlay
        /// </summary>
        public static ChatOverlay Create()
        {
            if (_instance != null)
            {
                if (_instance._endingSession)
                    return null;
                _instance.Show();
                return _instance;
            }

            var uiRoot = MainGame.me?.ui_root;
            if (uiRoot == null)
            {
                CoopMod.Logger.LogWarning("[ChatOverlay] Cannot create - UI root not found");
                return null;
            }

            // Awake attaches the overlay to the current game's UI root. It is deliberately
            // scene-scoped so a rebuilt UI receives a freshly initialized overlay.
            var overlayObj = new GameObject("ChatOverlay");
            overlayObj.layer = LayerMask.NameToLayer("UI");

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
            // The HUD supplies the game's font and depth baseline, but the overlay must not
            // be parented to it. HUD.Hide disables the whole HUD GameObject during cutscenes
            // and game windows, which would also stop this component from polling Enter.
            var hud = GUIElements.me?.hud;
            if (hud != null && MainGame.me?.ui_root != null)
            {
                BuildUIInternal(hud.gameObject);
                return;
            }

            CoopMod.Logger.LogInfo("[ChatOverlay] HUD/UI root not ready yet, deferring UI build");
            StartCoroutine(BuildUIWhenReady());
        }

        private IEnumerator BuildUIWhenReady()
        {
            while (GUIElements.me?.hud == null || MainGame.me?.ui_root == null)
                yield return null;

            CoopMod.Logger.LogInfo("[ChatOverlay] HUD/UI root available - building UI now");
            BuildUIInternal(GUIElements.me.hud.gameObject);
        }

        private void BuildUIInternal(GameObject hudGO)
        {

            var hudPanel = hudGO.GetComponentInParent<UIPanel>();
            if (hudPanel == null)
                hudPanel = hudGO.AddComponent<UIPanel>();

            int hudMaxDepth = FindMaxWidgetDepth(hudPanel);
            int baseDepth = hudMaxDepth + 10;

            // Parent to the UI root, not the HUD. The HUD is routinely deactivated,
            // while the UI root remains active to receive keyboard input.
            _cachedHud = hudGO.GetComponent<HUD>();
            _lastScreenWidth = Screen.width;
            _lastScreenHeight = Screen.height;
            transform.SetParent(MainGame.me.ui_root.transform, false);
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

            // Prefix-preserving completions render behind the live input so only the
            // gray suffix remains visible. Completions that insert quotes or normalize
            // case move in front so the preview exactly matches what Tab will apply.
            var suggestionGO = NGUITools.AddChild(gameObject);
            suggestionGO.name = "CommandSuggestion";
            _inputSuggestionLabel = suggestionGO.AddComponent<UILabel>();
            _inputSuggestionLabel.ambigiousFont = ambFont;
            _inputSuggestionLabel.fontSize = sampleSize;
            _inputSuggestionLabel.overflowMethod = UILabel.Overflow.ClampContent;
            _inputSuggestionLabel.alignment = NGUIText.Alignment.Left;
            _inputSuggestionLabel.pivot = UIWidget.Pivot.BottomLeft;
            _inputSuggestionLabel.text = "";
            _inputSuggestionLabel.color = new Color(0.48f, 0.58f, 0.64f, 1f);
            _inputSuggestionLabel.width = (int)(BG_WIDTH - PADDING * 4f);
            _inputSuggestionLabel.height = 24;
            _inputSuggestionLabel.multiLine = false;
            _inputSuggestionLabel.supportEncoding = false;
            _inputSuggestionLabel.depth = baseDepth + 2;
            _inputSuggestionLabel.cachedTransform.localPosition = new Vector3(
                -BG_WIDTH * 0.5f + PADDING * 2f,
                -BG_HEIGHT * 0.5f + PADDING * 2f,
                0f
            );

            // Minecraft-style completion menu. It grows upward from the chat window
            // and presents a moving slice when there are more matches than fit at once.
            // The text does not parse NGUI color tags because item and player names are
            // dynamic; the ASCII selection marker keeps every candidate safe to display.
            float completionListBottom = BG_HEIGHT * 0.5f + PADDING;
            _completionLineHeight = Mathf.Max(18, sampleSize + 3);

            _completionListBackground = NGUITools.AddChild<UITexture>(gameObject);
            _completionListBackground.name = "CommandCompletionListBg";
            _completionListBackground.mainTexture = bgTex;
            _completionListBackground.color = new Color(0f, 0f, 0f, 0.88f);
            _completionListBackground.pivot = UIWidget.Pivot.BottomLeft;
            _completionListBackground.width = (int)BG_WIDTH;
            _completionListBackground.height = _completionLineHeight + (int)(PADDING * 2f);
            _completionListBackground.depth = baseDepth + 4;
            _completionListBackground.cachedTransform.localPosition = new Vector3(
                -BG_WIDTH * 0.5f,
                completionListBottom,
                0f
            );

            for (int i = 0; i < _completionOptionLabels.Length; i++)
            {
                var optionGO = NGUITools.AddChild(gameObject);
                optionGO.name = "CommandCompletionOption" + i;
                UILabel optionLabel = optionGO.AddComponent<UILabel>();
                ConfigureSingleLineCompletionLabel(
                    optionLabel,
                    ambFont,
                    sampleSize,
                    baseDepth + 5);
                _completionOptionLabels[i] = optionLabel;
            }

            var footerGO = NGUITools.AddChild(gameObject);
            footerGO.name = "CommandCompletionFooter";
            _completionFooterLabel = footerGO.AddComponent<UILabel>();
            ConfigureSingleLineCompletionLabel(
                _completionFooterLabel,
                ambFont,
                sampleSize,
                baseDepth + 5);
            _completionFooterLabel.color =
                new Color(0.64f, 0.7f, 0.75f, 1f);
            _completionFooterLabel.cachedTransform.localPosition = new Vector3(
                -BG_WIDTH * 0.5f + PADDING,
                completionListBottom + PADDING,
                0f);

            SetCompletionListVisible(false);

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
            ApplyPresentationVisibility(!_faded);

            // Finish setup next frame
            StartCoroutine(FinishSetupNextFrame());
        }

        private static void ConfigureSingleLineCompletionLabel(
            UILabel label,
            UnityEngine.Object font,
            int fontSize,
            int depth)
        {
            label.ambigiousFont = font;
            label.fontSize = fontSize;
            label.overflowMethod = UILabel.Overflow.ClampContent;
            label.alignment = NGUIText.Alignment.Left;
            label.pivot = UIWidget.Pivot.BottomLeft;
            label.text = string.Empty;
            label.color = new Color(0.86f, 0.9f, 0.94f, 1f);
            label.width = (int)(BG_WIDTH - PADDING * 2f);
            label.height = 24;
            label.multiLine = false;
            label.maxLineCount = 1;
            label.supportEncoding = false;
            label.depth = depth;
        }

        private IEnumerator FinishSetupNextFrame()
        {
            yield return null;
            TryFinishInputSetup();
        }

        private bool TryFinishInputSetup()
        {
            if (_input != null)
                return true;
            if (_inputLabel == null || !gameObject.activeInHierarchy)
                return false;

            // Enable keyboard input for NGUI
            EnableNguiKeyboard();

            // Wait for input label panel
            if (_inputLabel.panel == null)
            {
                _inputLabel.CreatePanel();
                _inputLabel.MarkAsChanged();
            }

            // Create UIInput
            var inputGO = _inputLabel.gameObject;
            _input = inputGO.GetComponent<UIInput>() ??
                     inputGO.AddComponent<UIInput>();
            _input.label = _inputLabel;
            _input.value = "";
            _input.activeTextColor = Color.white;
            _input.defaultText = "[ Press Enter or Y to chat ]";
            _input.validation = UIInput.Validation.None;
            // Tab temporarily reselects this dynamic input after NGUI navigation.
            // Never let that focus restoration select and replace the whole command.
            _input.selectAllTextOnFocus = false;
            _input.onSubmit.Clear();
            _input.onSubmit.Add(new EventDelegate(OnSubmit));
            _input.onReturnKey = UIInput.OnReturnKey.Submit;

            if (!_inputSetupLogged)
            {
                _inputSetupLogged = true;
                CoopMod.Logger.LogInfo(
                    "[ChatOverlay] Input setup complete");
            }

            return true;
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
            // Retry setup if the next-frame coroutine was interrupted by a scene
            // transition. This component remains active independently of HUD.Hide().
            if (_input == null && !TryFinishInputSetup())
                return;

            if (Screen.width != _lastScreenWidth || Screen.height != _lastScreenHeight)
            {
                CoopMod.Logger.LogInfo($"[ChatOverlay] Resolution changed from {_lastScreenWidth}x{_lastScreenHeight} to {Screen.width}x{Screen.height}");
                RepositionToBottomLeft();
            }

            bool enterDown = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
            bool yDown = Input.GetKeyDown(KeyCode.Y);
            bool openDown = enterDown || yDown;
            bool escapeDown = Input.GetKeyDown(KeyCode.Escape);
            bool currentlyFocused = _focused || _input.isSelected;
            bool allGuisClosed = BaseGUI.all_guis_closed;

            // Presentation has its own lifecycle. Input may be blocked by a
            // cutscene or another GUI, but that must not pin stale chat on screen.
            if (!_faded && !currentlyFocused &&
                Time.unscaledTime - _lastActivityTime >= INACTIVITY_HIDE_SEC)
            {
                FadeOut();
            }

            bool gameplayInputAllowed = MainGame.me?.player_char != null &&
                                        MainGame.me.player_char.control_enabled &&
                                        DialogueSync.Instance?.IsInSyncedDialogue != true &&
                                        allGuisClosed;
            // Y is an unambiguous chat command, so allow it during dialogue and
            // cinematics. Enter remains gated there because the game also uses it
            // to advance speech. Once chat owns focus, keep accepting submit/cancel
            // input even if the cutscene changes player-control state underneath it.
            bool explicitChatInputAllowed = !LoadingGUI.is_shown &&
                                            (yDown || currentlyFocused);
            bool inputAllowed = gameplayInputAllowed || explicitChatInputAllowed;
            if (!inputAllowed)
            {
                _inputWasAllowed = false;
                if (openDown)
                {
                    CoopMod.Logger.LogInfo(
                        $"[ChatOverlay] Open key blocked: " +
                        $"control={MainGame.me?.player_char?.control_enabled}, " +
                        $"dialogue={DialogueSync.Instance?.IsInSyncedDialogue == true}, " +
                        $"allGuisClosed={BaseGUI.all_guis_closed}");
                }
                return;
            }

            if (!_inputWasAllowed)
            {
                // Do not reuse the Return press that may have just closed a dialogue
                // or game window. Y is an explicit chat-only command and must not be
                // swallowed by this debounce; neither transition should auto-reveal chat.
                _inputWasAllowed = true;
                _acceptInputAfterTime = Time.unscaledTime + 0.15f;
                if (!yDown)
                    return;
            }

            if (Time.unscaledTime < _acceptInputAfterTime && !yDown)
                return;

            if (enterDown && currentlyFocused)
            {
                SubmitCurrentInput("Update");
                return;
            }

            // Enter or Y - open/focus the chat when we're not already typing.
            // Once focused, Y remains ordinary text and Enter submits.
            // Works whether the overlay is faded or fully visible.
            if (openDown && !currentlyFocused)
            {
                CoopMod.Logger.LogInfo($"[ChatOverlay] Open key pressed - revealing + focusing input (faded={_faded})");
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
                ClearCommandSuggestion();
                UICamera.selectedObject = null;
                _input.isSelected = false;
                _focused = false;
                BumpActivity();
                return;
            }

            // Tab — autocomplete command arguments.
            if (Input.GetKeyDown(KeyCode.Tab) && currentlyFocused && _input.value.TrimStart().StartsWith("/"))
            {
                if (CanApplyCommandSuggestion())
                    ApplyCommandSuggestion();
                StartCoroutine(RestoreCommandInputFocusAfterTab());
                BumpActivity();
                return;
            }

            // Up/Down select a completion without changing the command. The caret is
            // restored at the end after NGUI has processed the same key event.
            if (currentlyFocused && TryNavigateCommandSuggestions())
            {
                StartCoroutine(RestoreCommandInputCaretAfterNavigation());
                BumpActivity();
                return;
            }

            // While the user is actively typing, keep the overlay awake.
            if (currentlyFocused)
            {
                _lastActivityTime = Time.unscaledTime;
            }

            RefreshCommandSuggestion(currentlyFocused);
        }

        private void RefreshCommandSuggestion(bool currentlyFocused)
        {
            if (_inputSuggestionLabel == null || _inputLabel == null)
                return;

            string value = _input?.value ?? string.Empty;
            if (!currentlyFocused)
            {
                ClearCommandSuggestion();
                return;
            }

            bool caretAtEnd = _input.cursorPosition == value.Length &&
                _input.selectionStart == _input.selectionEnd;
            if (!caretAtEnd)
            {
                ClearCommandSuggestion();
                return;
            }

            if (string.Equals(_suggestionSourceText, value, StringComparison.Ordinal))
            {
                RefreshCommandSuggestionPresentation(value);
                return;
            }

            _suggestionSourceText = value;
            _completionIndex = 0;
            _completionWindowStart = 0;
            _completionCandidates =
                DebugChatCommands.GetAutocompleteSuggestionOptions(value);
            RefreshCommandSuggestionPresentation(value);
        }

        private void ApplyCommandSuggestion()
        {
            string value = _input?.value ?? string.Empty;
            if (!string.Equals(
                    value,
                    _suggestionSourceText,
                    StringComparison.Ordinal))
            {
                _suggestionSourceText = value;
                _completionCandidates =
                    DebugChatCommands.GetAutocompleteSuggestionOptions(value);
                _completionIndex = 0;
                _completionWindowStart = 0;
            }

            if (_completionCandidates.Length == 0)
            {
                SetCommandSuggestionText(string.Empty);
                SetCompletionListVisible(false);
                return;
            }

            _completionIndex = Mathf.Clamp(
                _completionIndex,
                0,
                _completionCandidates.Length - 1);
            string completedText =
                _completionCandidates[_completionIndex].CompletedText;
            _input.value = completedText;
            MoveCommandInputCaretToEnd();
            _input.label.ProcessText();

            // Let the next frame calculate suggestions for the newly completed token.
            // This makes completing a command immediately advance to its arguments.
            _suggestionSourceText = null;
            _completionCandidates =
                new DebugChatCommands.CommandCompletion[0];
            _completionIndex = 0;
            _completionWindowStart = 0;
            SetCommandSuggestionText(string.Empty);
            SetCompletionListVisible(false);
        }

        private bool TryNavigateCommandSuggestions()
        {
            int direction = 0;
            if (Input.GetKeyDown(KeyCode.DownArrow))
                direction = 1;
            else if (Input.GetKeyDown(KeyCode.UpArrow))
                direction = -1;

            if (direction == 0 ||
                _completionCandidates.Length == 0 ||
                !string.Equals(
                    _suggestionSourceText,
                    _input?.value ?? string.Empty,
                    StringComparison.Ordinal))
            {
                return false;
            }

            _completionIndex =
                (_completionIndex + direction + _completionCandidates.Length) %
                _completionCandidates.Length;
            EnsureSelectedCompletionIsVisible();
            MoveCommandInputCaretToEnd();
            RefreshCommandSuggestionPresentation(_input.value ?? string.Empty);
            return true;
        }

        private void RefreshCommandSuggestionPresentation(string currentText)
        {
            string suggestion = string.Empty;
            bool showExactCompletionInForeground = false;
            // UIInput renders only a horizontally scrolled suffix for long text.
            // The popup remains useful there, but full ghost text cannot align.
            bool canAlignGhost = _inputLabel != null && string.Equals(
                _inputLabel.text,
                currentText,
                StringComparison.Ordinal);
            if (_completionCandidates.Length > 0 && canAlignGhost)
            {
                _completionIndex = Mathf.Clamp(
                    _completionIndex,
                    0,
                    _completionCandidates.Length - 1);
                suggestion =
                    _completionCandidates[_completionIndex].CompletedText;
                showExactCompletionInForeground = !suggestion.StartsWith(
                    currentText,
                    StringComparison.Ordinal);
            }

            SetCommandSuggestionText(
                suggestion,
                showExactCompletionInForeground);
            RenderCommandSuggestionList(currentText);

            // UIInput may shift its label horizontally to keep the caret visible.
            _inputSuggestionLabel.cachedTransform.localPosition =
                _inputLabel.cachedTransform.localPosition;
        }

        private void EnsureSelectedCompletionIsVisible()
        {
            if (_completionIndex < _completionWindowStart)
            {
                _completionWindowStart = _completionIndex;
            }
            else if (_completionIndex >=
                     _completionWindowStart + MAX_VISIBLE_COMPLETIONS)
            {
                _completionWindowStart =
                    _completionIndex - MAX_VISIBLE_COMPLETIONS + 1;
            }

            int maximumWindowStart = Mathf.Max(
                0,
                _completionCandidates.Length - MAX_VISIBLE_COMPLETIONS);
            _completionWindowStart = Mathf.Clamp(
                _completionWindowStart,
                0,
                maximumWindowStart);
        }

        private void RenderCommandSuggestionList(string currentText)
        {
            if (_completionListBackground == null ||
                _completionFooterLabel == null ||
                _completionOptionLabels[0] == null ||
                _completionCandidates.Length == 0)
            {
                SetCompletionListVisible(false);
                return;
            }

            EnsureSelectedCompletionIsVisible();
            int end = Math.Min(
                _completionCandidates.Length,
                _completionWindowStart + MAX_VISIBLE_COMPLETIONS);
            int visibleCount = end - _completionWindowStart;
            float completionListBottom = BG_HEIGHT * 0.5f + PADDING;
            float labelX = -BG_WIDTH * 0.5f + PADDING;

            for (int row = 0; row < _completionOptionLabels.Length; row++)
            {
                UILabel label = _completionOptionLabels[row];
                if (row >= visibleCount)
                {
                    label.enabled = false;
                    continue;
                }

                int candidateIndex = _completionWindowStart + row;
                label.enabled = true;
                label.height = _completionLineHeight;
                label.text =
                    (candidateIndex == _completionIndex ? "> " : "  ") +
                    GetCompletionOptionText(
                        currentText,
                        _completionCandidates[candidateIndex]);
                // First candidate is the top row; every option owns exactly one
                // non-wrapping label, so long item descriptions cannot hide rows.
                label.cachedTransform.localPosition = new Vector3(
                    labelX,
                    completionListBottom + PADDING +
                    (visibleCount - row) * _completionLineHeight,
                    0f);
                label.ProcessText();
                label.MarkAsChanged();
            }

            string footerText = "  Up/Down: select  Tab: complete";
            if (_completionCandidates.Length > MAX_VISIBLE_COMPLETIONS)
            {
                footerText += "  " + (_completionWindowStart + 1) + "-" +
                              end + "/" + _completionCandidates.Length;
            }

            _completionFooterLabel.height = _completionLineHeight;
            _completionFooterLabel.text = footerText;
            _completionFooterLabel.cachedTransform.localPosition = new Vector3(
                labelX,
                completionListBottom + PADDING,
                0f);
            _completionFooterLabel.ProcessText();
            _completionFooterLabel.MarkAsChanged();

            int lineCount = visibleCount + 1;
            int contentHeight = lineCount * _completionLineHeight;
            _completionListBackground.height =
                contentHeight + (int)(PADDING * 2f);
            _completionListBackground.MarkAsChanged();
            SetCompletionListVisible(true);
        }

        private static string GetCompletionOptionText(
            string currentText,
            DebugChatCommands.CommandCompletion completion)
        {
            if (!string.IsNullOrWhiteSpace(completion?.DisplayText))
                return completion.DisplayText;

            string completedText = completion?.CompletedText;
            if (string.IsNullOrEmpty(completedText))
                return string.Empty;

            string displayText = (completedText ?? string.Empty).TrimEnd();
            if (string.IsNullOrEmpty(currentText))
                return displayText;

            int tokenStart = FindCompletionTokenStart(currentText);
            if (tokenStart <= completedText.Length)
            {
                string fixedPrefix = currentText.Substring(0, tokenStart);
                if (completedText.StartsWith(
                        fixedPrefix,
                        StringComparison.Ordinal))
                {
                    return completedText.Substring(tokenStart).TrimEnd();
                }
            }

            return displayText;
        }

        private static int FindCompletionTokenStart(string text)
        {
            bool inQuotes = false;
            int tokenStart = 0;
            bool tokenStarted = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"')
                {
                    if (!tokenStarted)
                    {
                        tokenStart = i;
                        tokenStarted = true;
                    }
                    inQuotes = !inQuotes;
                }
                else if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    tokenStarted = false;
                    tokenStart = i + 1;
                }
                else if (!tokenStarted)
                {
                    tokenStart = i;
                    tokenStarted = true;
                }
            }

            return tokenStart;
        }

        private void SetCompletionListVisible(bool visible)
        {
            if (_completionListBackground != null)
                _completionListBackground.enabled = visible;
            if (_completionFooterLabel != null)
                _completionFooterLabel.enabled = visible;
            if (!visible)
            {
                for (int i = 0; i < _completionOptionLabels.Length; i++)
                {
                    if (_completionOptionLabels[i] != null)
                        _completionOptionLabels[i].enabled = false;
                }
            }
        }

        private void MoveCommandInputCaretToEnd()
        {
            if (_input == null)
                return;

            int end = (_input.value ?? string.Empty).Length;
            _input.cursorPosition = end;
            _input.selectionStart = end;
            _input.selectionEnd = end;
        }

        private bool CanApplyCommandSuggestion()
        {
            if (_input == null || _inputLabel == null)
                return false;

            string value = _input.value ?? string.Empty;
            return _input.cursorPosition == value.Length &&
                _input.selectionStart == _input.selectionEnd;
        }

        private IEnumerator RestoreCommandInputFocusAfterTab()
        {
            // NGUI handles Tab after regular Update and deselects UIInput for widget
            // navigation. Restore chat focus after that handler has finished.
            yield return new WaitForEndOfFrame();
            if (_endingSession || !_focused || _input == null)
                yield break;

            UICamera.selectedObject = _input.gameObject;
            _input.isSelected = true;
            UICamera.Notify(_input.gameObject, "OnSelect", true);
            MoveCommandInputCaretToEnd();
            _input.label.ProcessText();
        }

        private IEnumerator RestoreCommandInputCaretAfterNavigation()
        {
            yield return new WaitForEndOfFrame();
            if (_endingSession ||
                _input == null ||
                (!_focused && !_input.isSelected))
                yield break;

            MoveCommandInputCaretToEnd();
            _input.label.ProcessText();
        }

        private void SetCommandSuggestionText(
            string suggestion,
            bool foreground = false)
        {
            if (_inputSuggestionLabel == null)
            {
                return;
            }

            int desiredDepth = _inputLabel != null
                ? _inputLabel.depth + (foreground ? 1 : -1)
                : _inputSuggestionLabel.depth;
            bool hideLiveText = foreground && !string.IsNullOrEmpty(suggestion);
            if (_inputLabel != null &&
                !Mathf.Approximately(_inputLabel.alpha, hideLiveText ? 0f : 1f))
            {
                _inputLabel.alpha = hideLiveText ? 0f : 1f;
                _inputLabel.MarkAsChanged();
            }
            bool textChanged = _inputSuggestionLabel.text != suggestion;
            bool depthChanged = _inputSuggestionLabel.depth != desiredDepth;
            if (!textChanged && !depthChanged)
                return;

            _inputSuggestionLabel.text = suggestion;
            _inputSuggestionLabel.depth = desiredDepth;
            _inputSuggestionLabel.ProcessText();
            _inputSuggestionLabel.MarkAsChanged();
        }

        private void ClearCommandSuggestion()
        {
            _suggestionSourceText = null;
            _completionCandidates =
                new DebugChatCommands.CommandCompletion[0];
            _completionIndex = 0;
            _completionWindowStart = 0;
            SetCommandSuggestionText(string.Empty);
            SetCompletionListVisible(false);
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
            _faded = true;
            ApplyPresentationVisibility(false);
        }

        private void Reveal()
        {
            _faded = false;
            ApplyPresentationVisibility(true);
            _lastActivityTime = Time.unscaledTime;
        }

        private void ApplyPresentationVisibility(bool visible)
        {
            if (_panel != null)
                _panel.alpha = visible ? 1f : 0f;

            // Keep this component active for hotkey polling, but remove every
            // rendered/clickable child while chat is dormant. This also avoids
            // nested NGUI panels remaining visible when their parent alpha changes.
            NGUITools.SetActiveChildren(gameObject, visible);
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
                ClearCommandSuggestion();
                UICamera.selectedObject = null;
                _input.isSelected = false;
                _focused = false;
                return;
            }

            SendChatMessage(msg.Trim());

            _input.value = "";
            _input.label.ProcessText();
            ClearCommandSuggestion();
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
        public void ReceiveMessage(
            string senderName,
            string text,
            Steamworks.CSteamID senderID = default)
        {
            AddLine(FormatMessage(senderName, text, false));

            // Show chat bubble above remote player if enabled
            if (AreChatBubblesEnabled())
            {
                float duration = ModConfig.ChatBubbleDuration?.Value ?? 5f;
                ChatBubbleManager.ShowRemoteMessage(senderID, text, duration);
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
            if (_endingSession)
                return;
            gameObject.SetActive(true);
            Reveal();
        }

        public void Hide()
        {
            // Fade out visually but keep the GameObject active so Enter can still wake us up.
            if (_focused && _input != null)
                _input.isSelected = false;
            _focused = false;
            ClearCommandSuggestion();
            FadeOut();
        }

        /// <summary>
        /// Permanently close this session-scoped overlay. Hide() deliberately
        /// keeps Update alive for the chat hotkey; that behavior is wrong after
        /// a disconnect because menu/lobby messages can reveal the old overlay.
        /// </summary>
        public void EndSession()
        {
            if (_endingSession)
                return;

            _endingSession = true;
            if (_input != null)
            {
                _input.value = string.Empty;
                _input.isSelected = false;
                if (UICamera.selectedObject == _input.gameObject)
                    UICamera.selectedObject = null;
            }
            _focused = false;
            ClearCommandSuggestion();
            _history.Clear();
            if (_logLabel != null)
            {
                _logLabel.text = string.Empty;
                _logLabel.MarkAsChanged();
            }

            ApplyPresentationVisibility(false);
            gameObject.SetActive(false);
            if (_instance == this)
                _instance = null;
            Destroy(gameObject);
            CoopMod.Logger.LogInfo(
                "[ChatOverlay] Closed and cleared for session end");
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

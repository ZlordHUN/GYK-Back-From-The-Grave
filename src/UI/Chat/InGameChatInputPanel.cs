using UnityEngine;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// SIMPLE input panel for in-game GameGUI - just a label, no bullshit
    /// </summary>
    public class InGameChatInputPanel : MonoBehaviour
    {
        private UILabel inputLabel;
        private GameObject inputBackground;
        private UIWidget inputBackgroundWidget;
        private Camera uiCamera;
        private string currentText = "";
        private bool isFocused = false;
        private float caretBlinkTime = 0f;
        private bool showCaret = true;
        private int caretPosition = 0;
        private int scrollOffset = 0; // Character offset for horizontal scrolling
        
        private GameObject sendButton;
        private UIWidget sendButtonWidget;
        private UILabel sendButtonLabel;
        
        private System.Action<string> onMessageSent;

        public void Initialize(Transform parent, Vector3 localPosition, UIFont font, System.Action<string> onSendCallback, GameObject backgroundTemplate)
        {
            gameObject.layer = 13;
            transform.SetParent(parent, false);
            transform.localPosition = localPosition;
            gameObject.name = "InGameChatInputPanel";
            
            onMessageSent = onSendCallback;
            
            // Create background at the SAME position as this panel
            inputBackground = Object.Instantiate(backgroundTemplate, parent);
            inputBackground.name = "InGameChatInputBackground";
            inputBackground.layer = 13;
            inputBackground.transform.localPosition = localPosition; // SAME position as panel
            inputBackground.transform.localScale = Vector3.one;
            
            // Clean up the background
            var saveSlotGUI = inputBackground.GetComponent<SaveSlotGUI>();
            if (saveSlotGUI != null) Object.Destroy(saveSlotGUI);
            
            var colliders = inputBackground.GetComponentsInChildren<Collider>(true);
            foreach (var col in colliders) Object.Destroy(col);
            
            var labels = inputBackground.GetComponentsInChildren<UILabel>(true);
            foreach (var label in labels) Object.Destroy(label.gameObject);
            
            var buttons = inputBackground.GetComponentsInChildren<UIButton>(true);
            foreach (var button in buttons) Object.Destroy(button.gameObject);
            
            // Resize background
            var bgWidget = inputBackground.GetComponent<UIWidget>();
            if (bgWidget != null)
            {
                bgWidget.width = 220;
                bgWidget.height = 35;
                bgWidget.pivot = UIWidget.Pivot.Left;
            }
            
            // Store for click detection
            inputBackgroundWidget = bgWidget;
            uiCamera = NGUITools.FindCameraForLayer(inputBackground.layer);
            
            if (uiCamera == null)
            {
                CoopMod.Logger.LogWarning("InGameChatInputPanel: Could not find UI camera for click detection!");
            }
            else
            {
                CoopMod.Logger.LogInfo($"InGameChatInputPanel: UI Camera found: {uiCamera.name}");
            }
            
            if (inputBackgroundWidget == null)
            {
                CoopMod.Logger.LogWarning("InGameChatInputPanel: Background widget is null!");
            }
            
            // Move background slightly to the left so text appears inside
            inputBackground.transform.localPosition = new Vector3(localPosition.x - 60, localPosition.y, localPosition.z);
            
            // SIMPLE: Just create a label directly - NO position changes
            inputLabel = gameObject.AddComponent<UILabel>();
            inputLabel.bitmapFont = font;
            inputLabel.fontSize = 18;
            inputLabel.color = Color.white;
            inputLabel.pivot = UIWidget.Pivot.Left;
            inputLabel.overflowMethod = UILabel.Overflow.ClampContent;
            inputLabel.width = 280;
            inputLabel.depth = 107;
            inputLabel.supportEncoding = true;
            inputLabel.text = "[888888]Type here...[-]";
            
            CoopMod.Logger.LogInfo($"InGameChatInputPanel initialized at {localPosition}");
            CoopMod.Logger.LogInfo($"GameObject active: {gameObject.activeInHierarchy}, enabled: {enabled}");
        }

        private void Start()
        {
            CoopMod.Logger.LogInfo("InGameChatInputPanel Start() called");
        }

        private void OnEnable()
        {
            CoopMod.Logger.LogInfo("InGameChatInputPanel OnEnable() called");
        }

        private void OnInputClicked()
        {
            CoopMod.Logger.LogInfo("InGameChatInputPanel: OnInputClicked - focusing input");
            isFocused = true;
            showCaret = true;
            caretBlinkTime = 0f;
            
            // Disable game input so only chat receives keystrokes
            var lazyInput = UnityEngine.Object.FindObjectOfType<LazyInput>();
            if (lazyInput != null)
            {
                lazyInput.enabled = false;
                CoopMod.Logger.LogInfo("InGameChatInputPanel: Disabled LazyInput");
            }
            
            UpdateDisplay();
        }

        private void Update()
        {
            // Check for clicks to focus/unfocus
            if (Input.GetMouseButtonDown(0))
            {
                // Re-find camera if it's null (might have been destroyed)
                if (uiCamera == null)
                {
                    uiCamera = NGUITools.FindCameraForLayer(13);
                    if (uiCamera == null)
                    {
                        CoopMod.Logger.LogWarning("InGameChatInputPanel: Still can't find UI camera!");
                        return;
                    }
                    CoopMod.Logger.LogInfo($"InGameChatInputPanel: Re-found UI camera: {uiCamera.name}");
                }
                
                // Check send button click first
                if (sendButtonWidget != null)
                {
                    Vector3 mousePos = Input.mousePosition;
                    Vector3[] corners = sendButtonWidget.worldCorners;
                    Vector2 min = uiCamera.WorldToScreenPoint(corners[0]);
                    Vector2 max = uiCamera.WorldToScreenPoint(corners[2]);
                    
                    if (mousePos.x >= min.x && mousePos.x <= max.x &&
                        mousePos.y >= min.y && mousePos.y <= max.y)
                    {
                        OnSendButtonClicked();
                        return; // Exit early since we handled the click
                    }
                }
                
                if (inputBackgroundWidget == null)
                {
                    CoopMod.Logger.LogWarning("InGameChatInputPanel: Widget is null!");
                    return;
                }
                
                Vector3 mousePos2 = Input.mousePosition;
                Vector3[] corners2 = inputBackgroundWidget.worldCorners;
                Vector2 min2 = uiCamera.WorldToScreenPoint(corners2[0]);
                Vector2 max2 = uiCamera.WorldToScreenPoint(corners2[2]);
                
                float minX = Mathf.Min(min2.x, max2.x);
                float maxX = Mathf.Max(min2.x, max2.x);
                float minY = Mathf.Min(min2.y, max2.y);
                float maxY = Mathf.Max(min2.y, max2.y);
                
                bool clickedOnInput = mousePos2.x >= minX && mousePos2.x <= maxX && 
                                      mousePos2.y >= minY && mousePos2.y <= maxY;
                
                CoopMod.Logger.LogInfo($"Click detected - Mouse: {mousePos2}, Bounds: ({minX},{minY}) to ({maxX},{maxY}), Hit: {clickedOnInput}");
                
                if (clickedOnInput && !isFocused)
                {
                    OnInputClicked();
                }
                else if (!clickedOnInput && isFocused)
                {
                    CoopMod.Logger.LogInfo("InGameChatInputPanel: Clicked outside - unfocusing");
                    isFocused = false;
                    
                    // Re-enable game input
                    var lazyInput = UnityEngine.Object.FindObjectOfType<LazyInput>();
                    if (lazyInput != null)
                    {
                        lazyInput.enabled = true;
                        CoopMod.Logger.LogInfo("InGameChatInputPanel: Re-enabled LazyInput");
                    }
                    
                    UpdateDisplay();
                }
            }
            
            if (!isFocused) return;
            
            // ESC to unfocus
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                isFocused = false;
                
                // Re-enable game input
                var lazyInput = UnityEngine.Object.FindObjectOfType<LazyInput>();
                if (lazyInput != null)
                {
                    lazyInput.enabled = true;
                    CoopMod.Logger.LogInfo("InGameChatInputPanel: Re-enabled LazyInput (ESC pressed)");
                }
                
                UpdateDisplay();
                return; // Don't process any other input this frame
            }
            
            if (Input.anyKeyDown)
            {
                foreach (char c in Input.inputString)
                {
                    if (c == '\b') // Backspace
                    {
                        if (currentText.Length > 0 && caretPosition > 0)
                        {
                            currentText = currentText.Remove(caretPosition - 1, 1);
                            caretPosition--;
                        }
                    }
                    else if (c == '\n' || c == '\r') // Enter
                    {
                        SubmitMessage();
                    }
                    else if (c >= ' ')
                    {
                        currentText = currentText.Insert(caretPosition, c.ToString());
                        caretPosition++;
                    }
                }
                UpdateDisplay();
            }
            
            caretBlinkTime += Time.deltaTime;
            if (caretBlinkTime > 0.5f)
            {
                showCaret = !showCaret;
                caretBlinkTime = 0f;
                UpdateDisplay();
            }
        }

        private void UpdateDisplay()
        {
            if (inputLabel == null) return;
            
            string displayText = currentText;
            if (isFocused && showCaret)
            {
                displayText = displayText.Insert(caretPosition, "|");
            }
            
            // Show placeholder only when not focused and text is empty
            if (!isFocused && string.IsNullOrEmpty(currentText))
            {
                inputLabel.text = "[888888]Type here...[-]";
                scrollOffset = 0;
            }
            else
            {
                // Calculate approximate character width (rough estimate for small_font_bold at size 18)
                float charWidth = 7f; // Reduced from 9 to allow more characters
                int maxVisibleChars = (int)(280f / charWidth); // ~40 characters visible
                
                // Auto-scroll: if caret is beyond visible area, adjust scroll offset
                if (caretPosition > scrollOffset + maxVisibleChars - 3)
                {
                    // Scroll right to keep caret visible
                    scrollOffset = Mathf.Max(0, caretPosition - maxVisibleChars + 3);
                }
                else if (caretPosition < scrollOffset)
                {
                    // Scroll left if caret moves back
                    scrollOffset = caretPosition;
                }
                
                // Ensure scroll offset doesn't exceed text length
                scrollOffset = Mathf.Clamp(scrollOffset, 0, Mathf.Max(0, displayText.Length - maxVisibleChars));
                
                // Extract visible portion of text
                int endIndex = Mathf.Min(scrollOffset + maxVisibleChars, displayText.Length);
                string visibleText = displayText.Substring(scrollOffset, endIndex - scrollOffset);
                
                inputLabel.text = visibleText;
            }
        }

        private void SubmitMessage()
        {
            if (!string.IsNullOrEmpty(currentText))
            {
                onMessageSent?.Invoke(currentText);
                currentText = "";
                caretPosition = 0;
                
                // DON'T re-enable game input or unfocus - stay focused for multiple messages
                // User can click elsewhere or press ESC to unfocus
                
                UpdateDisplay();
            }
        }

        public void Focus()
        {
            isFocused = true;
            showCaret = true;
            caretBlinkTime = 0f;
            UpdateDisplay();
        }

        public void Unfocus()
        {
            isFocused = false;
            
            // Re-enable game input
            var lazyInput = UnityEngine.Object.FindObjectOfType<LazyInput>();
            if (lazyInput != null)
            {
                lazyInput.enabled = true;
            }
            
            UpdateDisplay();
        }

        public void CreateSendButton(Transform parent, Vector3 localPosition)
        {
            // Try to find a DialogButtonGUI to clone from (red button style)
            var dialogButtons = Resources.FindObjectsOfTypeAll<DialogButtonGUI>();
            if (dialogButtons != null && dialogButtons.Length > 0)
            {
                sendButton = Object.Instantiate(dialogButtons[0].gameObject, parent);
                sendButton.name = "SendButton";
                sendButton.layer = 13;
                sendButton.transform.localPosition = localPosition;
                sendButton.transform.localScale = Vector3.one;
                
                // Find and update the label
                sendButtonLabel = sendButton.GetComponentInChildren<UILabel>(true);
                if (sendButtonLabel != null)
                {
                    sendButtonLabel.text = "Send";
                }
                
                // Store the widget for manual click detection
                sendButtonWidget = sendButton.GetComponent<UIWidget>();
                if (sendButtonWidget == null)
                {
                    // Try to find it in children
                    sendButtonWidget = sendButton.GetComponentInChildren<UIWidget>(true);
                }
                
                if (sendButtonWidget != null)
                {
                    CoopMod.Logger.LogInfo($"Send button widget found: size {sendButtonWidget.width}x{sendButtonWidget.height}");
                }
                else
                {
                    CoopMod.Logger.LogWarning("Could not find UIWidget on send button for click detection");
                }
                
                // Remove the old event-based approach since it doesn't work with zero scale
                var buttonGUI = sendButton.GetComponent<DialogButtonGUI>();
                if (buttonGUI != null)
                {
                    Object.Destroy(buttonGUI);
                }
                
                // Remove UIButton components as they don't work with zero scale
                var uiButtons = sendButton.GetComponentsInChildren<UIButton>(true);
                foreach (var btn in uiButtons)
                {
                    Object.Destroy(btn);
                }
                
                sendButton.SetActive(true);
                
                CoopMod.Logger.LogInfo($"Send button created at {localPosition}");
            }
            else
            {
                CoopMod.Logger.LogError("Could not find DialogButtonGUI to clone for Send button!");
            }
        }

        private void OnSendButtonClicked()
        {
            CoopMod.Logger.LogInfo("InGameChatInputPanel: Send button clicked!");
            SubmitMessage();
        }
    }
}

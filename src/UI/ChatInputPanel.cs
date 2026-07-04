using UnityEngine;

namespace GraveyardKeeperCoop.UI
{
    public class ChatInputPanel : MonoBehaviour
    {
        private UILabel inputLabel;
        private GameObject inputBackground;
        private UIWidget inputBackgroundWidget; // Store for click detection
        private Camera uiCamera; // Store UI camera for click detection
        private GameObject sendButton;
        private UIWidget sendButtonWidget; // Store for click detection
        private UILabel sendButtonLabel;
        private string currentText = "";
        private bool isFocused = false;
        private float caretBlinkTime = 0f;
        private bool showCaret = true;
        private int caretPosition = 0; // Cursor position in the text
        private float scrollOffset = 0f; // Pixel offset for scrolling the text
        private float visibleTextWidth = 210f;
        private float inputTextLeftX = -105f;
        private UIPanel clipPanel; // Panel to clip the text to visible bounds
        
        // Store for debugging
        private GameObject clipObj;
        private GameObject inputObj;
        
        // Key repeat for backspace (hold to delete continuously)
        private float backspaceHoldTime = 0f;
        private const float BACKSPACE_REPEAT_DELAY = 0.5f; // Initial delay before repeat starts
        private const float BACKSPACE_REPEAT_RATE = 0.05f; // Time between repeats (20 per second)
        private float nextBackspaceTime = 0f;
        
        public System.Action<string> OnMessageSent;

        public void Initialize(Transform parent, Vector3 localPosition, GameObject backgroundTemplate, UIFont font)
        {
            // Set layer first
            gameObject.layer = 13;
            transform.SetParent(parent, false);
            transform.localPosition = localPosition;
            
            // Clone the background from template (save slot)
            inputBackground = Object.Instantiate(backgroundTemplate, parent);
            inputBackground.name = "ChatInputBackground";
            inputBackground.layer = 13;
            inputBackground.transform.localPosition = localPosition;
            inputBackground.transform.localScale = Vector3.one;

            // This is a visual clone only. The save-slot controller item would
            // otherwise appear in LobbyGUI's selectable controller hierarchy.
            foreach (var navigationItem in inputBackground.GetComponentsInChildren<GamepadNavigationItem>(true))
                Object.DestroyImmediate(navigationItem);
            foreach (var menuItem in inputBackground.GetComponentsInChildren<MenuItemGUI>(true))
                Object.DestroyImmediate(menuItem);
            foreach (var controller in inputBackground.GetComponentsInChildren<GamepadNavigationController>(true))
                Object.DestroyImmediate(controller);
            
            // Remove SaveSlotGUI component if present
            var saveSlotGUI = inputBackground.GetComponent<SaveSlotGUI>();
            if (saveSlotGUI != null)
            {
                Object.Destroy(saveSlotGUI);
            }
            
            // Remove all colliders from the background
            var colliders = inputBackground.GetComponentsInChildren<Collider>(true);
            foreach (var col in colliders)
            {
                Object.Destroy(col);
            }
            
            // Remove all UIButton components
            var uiButtons = inputBackground.GetComponentsInChildren<UIButton>(true);
            foreach (var btn in uiButtons)
            {
                Object.Destroy(btn);
            }
            
            // Remove all labels and buttons from cloned background
            var labels = inputBackground.GetComponentsInChildren<UILabel>(true);
            foreach (var label in labels)
            {
                Object.Destroy(label.gameObject);
            }
            
            var buttons = inputBackground.GetComponentsInChildren<UIButton>(true);
            foreach (var button in buttons)
            {
                Object.Destroy(button.gameObject);
            }
            
            // Resize background to be a thin line (220px wide to match chat box better, 35px tall)
            var bgWidget = inputBackground.GetComponent<UIWidget>();
            if (bgWidget != null)
            {
                bgWidget.width = 220;
                bgWidget.height = 35;
                bgWidget.pivot = UIWidget.Pivot.TopLeft;
            }
            else
            {
                CoopMod.Logger.LogWarning("No UIWidget on input background, adding one");
                bgWidget = inputBackground.AddComponent<UIWidget>();
                bgWidget.width = 220;
                bgWidget.height = 35;
                bgWidget.depth = 100;
                bgWidget.pivot = UIWidget.Pivot.TopLeft;
            }
            
            // Store the widget and UI camera for click detection
            inputBackgroundWidget = bgWidget;
            uiCamera = NGUITools.FindCameraForLayer(inputBackground.layer);
            
            if (uiCamera == null)
            {
                CoopMod.Logger.LogWarning("Could not find UI camera for click detection!");
            }
            else
            {
                CoopMod.Logger.LogInfo($"UI Camera found: {uiCamera.name}");
            }
            
            inputBackground.SetActive(true);
            
            // Create a clipping panel to contain the text
            clipObj = new GameObject("InputClipPanel");
            clipObj.layer = 13;
            clipObj.transform.SetParent(inputBackground.transform, false);
            clipObj.transform.localPosition = new Vector3(110, -17, 0); // Center in the background
            clipObj.transform.localScale = Vector3.one;
            
            clipPanel = clipObj.AddComponent<UIPanel>();
            clipPanel.depth = 105;
            clipPanel.clipping = UIDrawCall.Clipping.SoftClip;
            clipPanel.baseClipRegion = new Vector4(0, 0, 210, 30); // Center (0,0), size 210x30
            clipPanel.clipSoftness = new Vector2(0, 0); // Hard clipping edges
            
            // Create a simple text label that we'll update manually with keyboard input
            inputObj = new GameObject("InputField");
            inputObj.layer = 13;
            inputObj.transform.SetParent(clipObj.transform, false);
            inputObj.transform.localPosition = new Vector3(inputTextLeftX, 0, 0);
            inputObj.transform.localScale = Vector3.one;
            
            // Create UILabel for displaying typed text
            inputLabel = inputObj.AddComponent<UILabel>();
            inputLabel.bitmapFont = font;
            inputLabel.depth = 106;
            inputLabel.fontSize = 16;
            inputLabel.color = Color.white;
            inputLabel.width = 1000; // Large width to allow long text
            inputLabel.height = 30;
            inputLabel.pivot = UIWidget.Pivot.Left; // Left-align to show beginning of text
            inputLabel.overflowMethod = UILabel.Overflow.ResizeFreely; // Allow text to extend beyond bounds
            inputLabel.alignment = NGUIText.Alignment.Left;
            inputLabel.text = ""; // Start empty
            
            // Initialize caret position
            caretPosition = 0;
            scrollOffset = 0;
            
            // Initialize display
            UpdateDisplay();
            
            // Wait for next frame to get accurate screen positions
            CoopMod.Instance.StartCoroutine(LogPositionsNextFrame());
        }
        
        private System.Collections.IEnumerator LogPositionsNextFrame()
        {
            yield return null; // Wait one frame for UI layout
            
            // Get actual screen-space positions
            var uiCamera = NGUITools.FindCameraForLayer(gameObject.layer);
            Vector3 labelScreenPos = Vector3.zero;
            Vector3 labelWorldCorner = inputLabel.worldCorners[0]; // Bottom-left corner
            
            if (uiCamera != null)
            {
                labelScreenPos = uiCamera.WorldToScreenPoint(labelWorldCorner);
            }
            
            CoopMod.Logger.LogInfo($"=== CHATINPUTPANEL DEBUG (after layout) ===");
            CoopMod.Logger.LogInfo($"  GameObject layer: {gameObject.layer}");
            CoopMod.Logger.LogInfo($"  UI Camera: {(uiCamera != null ? uiCamera.name : "NULL")}");
            CoopMod.Logger.LogInfo($"  Camera depth: {(uiCamera != null ? uiCamera.depth.ToString() : "N/A")}");
            CoopMod.Logger.LogInfo($"  Local position: {transform.localPosition}");
            CoopMod.Logger.LogInfo($"  World position: {transform.position}");
            CoopMod.Logger.LogInfo($"  ClipPanel local: {clipObj.transform.localPosition}");
            CoopMod.Logger.LogInfo($"  ClipPanel world: {clipObj.transform.position}");
            CoopMod.Logger.LogInfo($"  InputField local: {inputObj.transform.localPosition}");
            CoopMod.Logger.LogInfo($"  InputField world: {inputObj.transform.position}");
            CoopMod.Logger.LogInfo($"  InputLabel local: {inputLabel.transform.localPosition}");
            CoopMod.Logger.LogInfo($"  InputLabel world: {inputLabel.transform.position}");
            CoopMod.Logger.LogInfo($"  InputLabel pivot: {inputLabel.pivot}");
            CoopMod.Logger.LogInfo($"  InputLabel width: {inputLabel.width}");
            CoopMod.Logger.LogInfo($"  InputLabel worldCorner[0] (bottom-left): {labelWorldCorner}");
            CoopMod.Logger.LogInfo($"  *** INPUTLABEL SCREEN POSITION: {labelScreenPos} ***");
        }

        public void CreateSendButton(Transform parent, Vector3 localPosition)
        {
            // Try to find a DialogButtonGUI to clone from
            var dialogButtons = Resources.FindObjectsOfTypeAll<DialogButtonGUI>();
            if (dialogButtons != null && dialogButtons.Length > 0)
            {
                sendButton = Object.Instantiate(dialogButtons[0].gameObject, parent);
                sendButton.name = "SendButton";
                sendButton.layer = 13;
                sendButton.transform.localPosition = localPosition;
                sendButton.transform.localScale = Vector3.one;

                foreach (var localizedLabel in sendButton.GetComponentsInChildren<LocalizedLabel>(true))
                {
                    localizedLabel.enabled = false;
                    Object.DestroyImmediate(localizedLabel);
                }
                
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

        public void ApplyLayout(
            Vector3 inputPosition,
            Vector3 sendButtonPosition,
            int inputWidth,
            int inputHeight,
            int clipWidth,
            int clipHeight)
        {
            transform.localPosition = inputPosition;

            if (inputBackground != null)
            {
                inputBackground.transform.localPosition = inputPosition;
            }

            if (inputBackgroundWidget != null)
            {
                inputBackgroundWidget.width = inputWidth;
                inputBackgroundWidget.height = inputHeight;
                inputBackgroundWidget.pivot = UIWidget.Pivot.TopLeft;
                inputBackgroundWidget.MarkAsChanged();
            }

            visibleTextWidth = clipWidth;
            inputTextLeftX = -clipWidth * 0.5f;

            if (clipObj != null)
            {
                clipObj.transform.localPosition = new Vector3(inputWidth * 0.5f, -inputHeight * 0.5f, 0f);
            }

            if (clipPanel != null)
            {
                clipPanel.baseClipRegion = new Vector4(0f, 0f, clipWidth, clipHeight);
            }

            if (inputObj != null)
            {
                inputObj.transform.localPosition = new Vector3(inputTextLeftX, 0f, 0f);
            }

            if (sendButton != null)
            {
                sendButton.transform.localPosition = sendButtonPosition;
            }

            UpdateDisplay();
        }

        private void OnSendButtonClicked()
        {
            if (!string.IsNullOrEmpty(currentText))
            {
                string message = currentText;
                currentText = ""; // Clear input
                caretPosition = 0;
                scrollOffset = 0;
                // Keep input focused after sending so user can type immediately again
                isFocused = true;
                showCaret = true;
                caretBlinkTime = 0f;
                UpdateDisplay();
                
                OnMessageSent?.Invoke(message);
                
                CoopMod.Logger.LogInfo($"Message sent: {message}");
            }
        }

        private void OnInputClicked()
        {
            CoopMod.Logger.LogInfo("=== OnInputClicked FIRED ===");
            isFocused = true;
            showCaret = true;
            caretBlinkTime = 0f;
            UpdateDisplay();
            CoopMod.Logger.LogInfo("Chat input focused - ready for typing");
        }

        private void Update()
        {
            // Check for clicks on the input field (even when not focused - this is how we focus it)
            if (UnityEngine.Input.GetMouseButtonDown(0) && uiCamera != null)
            {
                Vector3 mousePos = UnityEngine.Input.mousePosition;
                bool clickedOnInput = false;
                bool clickedOnSendButton = false;
                
                // Check if clicked on input field
                if (inputBackgroundWidget != null)
                {
                    Vector3[] corners = inputBackgroundWidget.worldCorners;
                    Vector2 min = uiCamera.WorldToScreenPoint(corners[0]);
                    Vector2 max = uiCamera.WorldToScreenPoint(corners[2]);
                    
                    float minX = Mathf.Min(min.x, max.x);
                    float maxX = Mathf.Max(min.x, max.x);
                    float minY = Mathf.Min(min.y, max.y);
                    float maxY = Mathf.Max(min.y, max.y);
                    
                    if (mousePos.x >= minX && mousePos.x <= maxX && 
                        mousePos.y >= minY && mousePos.y <= maxY)
                    {
                        clickedOnInput = true;
                        OnInputClicked();
                    }
                }
                
                // Check if clicked on Send button
                if (!clickedOnInput && sendButtonWidget != null)
                {
                    Vector3[] corners = sendButtonWidget.worldCorners;
                    Vector2 min = uiCamera.WorldToScreenPoint(corners[0]);
                    Vector2 max = uiCamera.WorldToScreenPoint(corners[2]);
                    
                    float minX = Mathf.Min(min.x, max.x);
                    float maxX = Mathf.Max(min.x, max.x);
                    float minY = Mathf.Min(min.y, max.y);
                    float maxY = Mathf.Max(min.y, max.y);
                    
                    if (mousePos.x >= minX && mousePos.x <= maxX && 
                        mousePos.y >= minY && mousePos.y <= maxY)
                    {
                        clickedOnSendButton = true;
                        CoopMod.Logger.LogInfo("Send button clicked!");
                        OnSendButtonClicked();
                    }
                }
                
                // If clicked outside both, unfocus the input
                if (!clickedOnInput && !clickedOnSendButton && isFocused)
                {
                    isFocused = false;
                    UpdateDisplay();
                    CoopMod.Logger.LogInfo("Clicked outside input field - unfocused");
                }
            }
            
            // Also support TAB key to focus the input (for accessibility/alternative input method)
            if (UnityEngine.Input.GetKeyDown(KeyCode.Tab) && !isFocused)
            {
                OnInputClicked();
            }
            
            if (!isFocused) return;

            // Handle continuous backspace (hold to delete)
            if (UnityEngine.Input.GetKey(KeyCode.Backspace))
            {
                if (UnityEngine.Input.GetKeyDown(KeyCode.Backspace))
                {
                    // First press - delete immediately
                    DeleteCharacter();
                    backspaceHoldTime = 0f;
                    nextBackspaceTime = Time.time + BACKSPACE_REPEAT_DELAY;
                }
                else
                {
                    // Key is being held
                    backspaceHoldTime += Time.deltaTime;
                    
                    // After initial delay, start repeating
                    if (Time.time >= nextBackspaceTime)
                    {
                        DeleteCharacter();
                        nextBackspaceTime = Time.time + BACKSPACE_REPEAT_RATE;
                    }
                }
            }
            else
            {
                // Reset when key is released
                backspaceHoldTime = 0f;
            }

            // Handle arrow keys for caret movement
            if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow))
            {
                if (caretPosition > 0)
                {
                    caretPosition--;
                    showCaret = true;
                    caretBlinkTime = 0f;
                    UpdateDisplay();
                }
            }
            else if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow))
            {
                if (caretPosition < currentText.Length)
                {
                    caretPosition++;
                    showCaret = true;
                    caretBlinkTime = 0f;
                    UpdateDisplay();
                }
            }
            else if (UnityEngine.Input.GetKeyDown(KeyCode.Home))
            {
                caretPosition = 0;
                showCaret = true;
                caretBlinkTime = 0f;
                UpdateDisplay();
            }
            else if (UnityEngine.Input.GetKeyDown(KeyCode.End))
            {
                caretPosition = currentText.Length;
                showCaret = true;
                caretBlinkTime = 0f;
                UpdateDisplay();
            }

            // Handle text input
            if (UnityEngine.Input.anyKeyDown)
            {
                string input = UnityEngine.Input.inputString;
                
                CoopMod.Logger.LogInfo($"Key pressed, inputString: '{input}', length: {input.Length}");
                
                foreach (char c in input)
                {
                    // Note: Backspace is handled separately above for continuous deletion
                    if (c == '\n' || c == '\r') // Enter
                    {
                        CoopMod.Logger.LogInfo("Enter pressed - sending message");
                        OnSendButtonClicked();
                        // Keep focus after sending message so user can type immediately again
                        // isFocused remains true
                        return;
                    }
                    else if (c >= 32 && c <= 126) // Printable ASCII characters
                    {
                        if (currentText.Length < 100) // Max length
                        {
                            currentText = currentText.Insert(caretPosition, c.ToString());
                            caretPosition++;
                            CoopMod.Logger.LogInfo($"Added '{c}' at position {caretPosition - 1} - text now: '{currentText}'");
                        }
                    }
                }
                
                UpdateDisplay();
            }
            
            // Handle caret blinking
            caretBlinkTime += Time.deltaTime;
            if (caretBlinkTime >= 0.5f)
            {
                caretBlinkTime = 0f;
                showCaret = !showCaret;
                UpdateDisplay();
            }
            
            // Unfocus if Escape pressed
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                isFocused = false;
                UpdateDisplay();
            }
        }

        private void UpdateDisplay()
        {
            if (inputLabel == null) 
            {
                CoopMod.Logger.LogWarning("UpdateDisplay called but inputLabel is null!");
                return;
            }
            
            if (string.IsNullOrEmpty(currentText) && !isFocused)
            {
                // Show placeholder when empty and not focused
                inputLabel.text = "[888888]Type message...[-]";
                inputLabel.transform.localPosition = new Vector3(inputTextLeftX, 0, 0); // Reset to left edge
                CoopMod.Logger.LogInfo("UpdateDisplay - showing placeholder text");
                return;
            }
            
            // Build display text with caret
            string textBeforeCaret = currentText.Substring(0, caretPosition);
            string textAfterCaret = caretPosition < currentText.Length ? currentText.Substring(caretPosition) : "";
            
            // Temporarily set text to measure character widths
            inputLabel.text = textBeforeCaret;
            inputLabel.ProcessText();
            float textBeforeCaretWidth = inputLabel.printedSize.x;
            
            // Calculate visible area width (210px as defined in clip panel)
            // Adjust scroll offset to keep caret visible
            // If caret is too far right, scroll right
            if (textBeforeCaretWidth - scrollOffset > visibleTextWidth - 20) // 20px margin for caret
            {
                scrollOffset = textBeforeCaretWidth - (visibleTextWidth - 20);
            }
            // If caret is too far left, scroll left
            else if (textBeforeCaretWidth < scrollOffset)
            {
                scrollOffset = textBeforeCaretWidth;
            }
            
            // Make sure we don't scroll past the beginning
            if (scrollOffset < 0)
            {
                scrollOffset = 0;
            }
            
            // Build final display text with caret
            string displayText = currentText;
            if (isFocused && showCaret)
            {
                displayText = textBeforeCaret + "|" + textAfterCaret;
            }
            
            // Set the text
            inputLabel.text = displayText;
            
            // Position the label to create scrolling effect
            // Start at the left edge and shift left by scroll offset
            inputLabel.transform.localPosition = new Vector3(inputTextLeftX - scrollOffset, 0, 0);
            
            CoopMod.Logger.LogInfo($"UpdateDisplay - text: '{currentText}', caret: {caretPosition}, scroll: {scrollOffset}, textWidth: {textBeforeCaretWidth}");
        }

        private void DeleteCharacter()
        {
            if (caretPosition > 0 && currentText.Length > 0)
            {
                currentText = currentText.Remove(caretPosition - 1, 1);
                caretPosition--;
                showCaret = true;
                caretBlinkTime = 0f;
                UpdateDisplay();
                CoopMod.Logger.LogInfo($"Backspace - text now: '{currentText}', caret at {caretPosition}");
            }
        }

        public void SetActive(bool active)
        {
            if (inputBackground != null)
            {
                inputBackground.SetActive(active);
            }
            if (sendButton != null)
            {
                sendButton.SetActive(active);
            }
        }

        public void Focus()
        {
            if (!isFocused)
            {
                OnInputClicked();
            }
        }

        public void Unfocus()
        {
            isFocused = false;
            UpdateDisplay();
        }
    }
}

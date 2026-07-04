using UnityEngine;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// IP input panel - text field for entering server IP address with a Join button
    /// Based on ChatInputPanel design
    /// </summary>
    public class IPInputPanel : MonoBehaviour
    {
        private UILabel inputLabel;
        private GameObject inputBackground;
        private UIWidget inputBackgroundWidget;
        private Camera uiCamera;
        private GameObject joinButton;
        private UIWidget joinButtonWidget;
        private UILabel joinButtonLabel;
        private string currentText = "";
        private bool isFocused = false;
        private float caretBlinkTime = 0f;
        private bool showCaret = true;
        private int caretPosition = 0;
        private float scrollOffset = 0f;
        private UIPanel clipPanel;
        
        public System.Action<string> OnJoinPressed;
        public System.Action OnInputStarted; // Callback when player starts typing
        public System.Action OnInputCleared; // Callback when player clears all input
        private bool hasNotifiedInputStart = false; // Track if we've notified about input starting
        
        // Key repeat for backspace (hold to delete continuously)
        private float backspaceHoldTime = 0f;
        private const float BACKSPACE_REPEAT_DELAY = 0.5f; // Initial delay before repeat starts
        private const float BACKSPACE_REPEAT_RATE = 0.05f; // Time between repeats (20 per second)
        private float nextBackspaceTime = 0f;

        public void Initialize(Transform parent, Vector3 localPosition, GameObject backgroundTemplate, UIFont font)
        {
            // Clone the background from template (save slot)
            inputBackground = Object.Instantiate(backgroundTemplate, parent);
            inputBackground.name = "IPInputBackground";
            inputBackground.layer = 13;
            inputBackground.transform.localPosition = localPosition;
            inputBackground.transform.localScale = Vector3.one;
            
            // Log what components the background has
            var components = inputBackground.GetComponents<Component>();
            CoopMod.Logger.LogInfo($"Background has {components.Length} components:");
            foreach (var comp in components)
            {
                CoopMod.Logger.LogInfo($"  - {comp.GetType().Name}");
            }
            
            // Remove SaveSlotGUI component if present
            var saveSlotGUI = inputBackground.GetComponent<SaveSlotGUI>();
            if (saveSlotGUI != null)
            {
                Object.Destroy(saveSlotGUI);
            }
            
            // SELECTIVE CLEANUP: Remove specific UI elements but KEEP the background sprite
            // This matches ChatInputPanel's approach
            
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
            
            // Remove all labels from cloned background (buttons, text, etc.)
            var labels = inputBackground.GetComponentsInChildren<UILabel>(true);
            foreach (var label in labels)
            {
                Object.Destroy(label.gameObject);
            }
            
            // Remove button GameObjects (Continue button, etc.)
            var buttons = inputBackground.GetComponentsInChildren<UIButton>(true);
            foreach (var button in buttons)
            {
                Object.Destroy(button.gameObject);
            }
            
            CoopMod.Logger.LogInfo($"Cleaned up save slot template (kept background sprite)");
            
            // Resize background to be wider than chat (300px wide, 35px tall)
            var bgWidget = inputBackground.GetComponent<UIWidget>();
            if (bgWidget != null)
            {
                bgWidget.width = 300;
                bgWidget.height = 35;
                bgWidget.pivot = UIWidget.Pivot.TopLeft;
            }
            else
            {
                CoopMod.Logger.LogWarning("No UIWidget on input background, adding one");
                bgWidget = inputBackground.AddComponent<UIWidget>();
                bgWidget.width = 300;
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
            GameObject clipObj = new GameObject("InputClipPanel");
            clipObj.layer = 13;
            clipObj.transform.SetParent(inputBackground.transform, false);
            clipObj.transform.localPosition = new Vector3(150, -17, 0); // Center in the background
            clipObj.transform.localScale = Vector3.one;
            
            clipPanel = clipObj.AddComponent<UIPanel>();
            clipPanel.depth = 105;
            clipPanel.clipping = UIDrawCall.Clipping.SoftClip;
            clipPanel.baseClipRegion = new Vector4(0, 0, 290, 30); // Center (0,0), size 290x30
            clipPanel.clipSoftness = new Vector2(0, 0); // Hard clipping edges
            
            // Create text label for the IP input
            GameObject inputObj = new GameObject("InputField");
            inputObj.layer = 13;
            inputObj.transform.SetParent(clipObj.transform, false);
            inputObj.transform.localPosition = new Vector3(-145, 0, 0); // Start from left edge
            inputObj.transform.localScale = Vector3.one;
            
            // Create UILabel for displaying typed text
            inputLabel = inputObj.AddComponent<UILabel>();
            inputLabel.bitmapFont = font;
            inputLabel.depth = 106;
            inputLabel.fontSize = 16;
            inputLabel.color = Color.white;
            inputLabel.width = 1000; // Large width to allow long text
            inputLabel.height = 30;
            inputLabel.pivot = UIWidget.Pivot.Left;
            inputLabel.overflowMethod = UILabel.Overflow.ResizeFreely;
            inputLabel.alignment = NGUIText.Alignment.Left;
            inputLabel.text = "";
            inputLabel.enabled = true;
            inputLabel.gameObject.SetActive(true);
            
            CoopMod.Logger.LogInfo($"Created input label: font={font?.name}, depth={inputLabel.depth}, enabled={inputLabel.enabled}");
            
            // Start with empty IP field (player must type their own IP)
            currentText = "";
            caretPosition = 0;
            scrollOffset = 0;
            
            CoopMod.Logger.LogInfo($"Initial IP text: '{currentText}' (empty), caret at {caretPosition}");
            
            // Initialize display
            UpdateDisplay();
            
            CoopMod.Logger.LogInfo($"IPInputPanel initialized at {localPosition}, label text: '{inputLabel.text}'");
        }

        public void CreateJoinButton(Transform parent, Vector3 localPosition)
        {
            // Try to find a DialogButtonGUI to clone from
            var dialogButtons = Resources.FindObjectsOfTypeAll<DialogButtonGUI>();
            if (dialogButtons != null && dialogButtons.Length > 0)
            {
                joinButton = Object.Instantiate(dialogButtons[0].gameObject, parent);
                joinButton.name = "JoinButton";
                joinButton.layer = 13;
                joinButton.transform.localPosition = localPosition;
                joinButton.transform.localScale = Vector3.one;
                
                // Find and update the label to say "Join" (keep default styling)
                joinButtonLabel = joinButton.GetComponentInChildren<UILabel>(true);
                if (joinButtonLabel != null)
                {
                    joinButtonLabel.text = "Join";
                }
                
                // Store the widget for manual click detection
                joinButtonWidget = joinButton.GetComponent<UIWidget>();
                if (joinButtonWidget == null)
                {
                    joinButtonWidget = joinButton.GetComponentInChildren<UIWidget>(true);
                }
                
                if (joinButtonWidget != null)
                {
                    CoopMod.Logger.LogInfo($"Join button widget found: size {joinButtonWidget.width}x{joinButtonWidget.height}");
                }
                else
                {
                    CoopMod.Logger.LogWarning("Could not find UIWidget on join button for click detection");
                }
                
                // Remove old components that don't work with zero scale
                var buttonGUI = joinButton.GetComponent<DialogButtonGUI>();
                if (buttonGUI != null)
                {
                    Object.Destroy(buttonGUI);
                }
                
                var uiButtons = joinButton.GetComponentsInChildren<UIButton>(true);
                foreach (var btn in uiButtons)
                {
                    Object.Destroy(btn);
                }
                
                joinButton.SetActive(true);
                
                CoopMod.Logger.LogInfo($"Join button created at {localPosition}");
            }
            else
            {
                CoopMod.Logger.LogError("Could not find DialogButtonGUI to clone for Join button!");
            }
        }

        private void OnJoinButtonClicked()
        {
            if (!string.IsNullOrEmpty(currentText))
            {
                string ipAddress = currentText;
                CoopMod.Logger.LogInfo($"Join button clicked with IP: {ipAddress}");
                
                // Save to config
                ModConfig.ServerIP.Value = ipAddress;
                
                // Invoke the callback
                OnJoinPressed?.Invoke(ipAddress);
            }
            else
            {
                CoopMod.Logger.LogWarning("Join button clicked but no IP address entered!");
            }
        }

        private void OnInputClicked()
        {
            CoopMod.Logger.LogInfo("=== IP Input Clicked ===");
            isFocused = true;
            showCaret = true;
            caretBlinkTime = 0f;
            UpdateDisplay();
            CoopMod.Logger.LogInfo("IP input focused - ready for typing");
        }

        private void Update()
        {
            // Check for clicks on the input field or join button
            if (UnityEngine.Input.GetMouseButtonDown(0) && uiCamera != null)
            {
                Vector3 mousePos = UnityEngine.Input.mousePosition;
                bool clickedOnInput = false;
                bool clickedOnJoinButton = false;
                
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
                
                // Check if clicked on Join button
                if (!clickedOnInput && joinButtonWidget != null)
                {
                    Vector3[] corners = joinButtonWidget.worldCorners;
                    Vector2 min = uiCamera.WorldToScreenPoint(corners[0]);
                    Vector2 max = uiCamera.WorldToScreenPoint(corners[2]);
                    
                    float minX = Mathf.Min(min.x, max.x);
                    float maxX = Mathf.Max(min.x, max.x);
                    float minY = Mathf.Min(min.y, max.y);
                    float maxY = Mathf.Max(min.y, max.y);
                    
                    if (mousePos.x >= minX && mousePos.x <= maxX && 
                        mousePos.y >= minY && mousePos.y <= maxY)
                    {
                        clickedOnJoinButton = true;
                        CoopMod.Logger.LogInfo("Join button clicked!");
                        OnJoinButtonClicked();
                    }
                }
                
                // If clicked outside both, unfocus the input
                if (!clickedOnInput && !clickedOnJoinButton && isFocused)
                {
                    isFocused = false;
                    UpdateDisplay();
                    CoopMod.Logger.LogInfo("Clicked outside input field - unfocused");
                }
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
                
                foreach (char c in input)
                {
                    // Note: Backspace is handled separately above for continuous deletion
                    if (c == '\n' || c == '\r') // Enter
                    {
                        CoopMod.Logger.LogInfo("Enter pressed - joining");
                        OnJoinButtonClicked();
                        return;
                    }
                    else if ((c >= '0' && c <= '9') || c == '.' || c == ':') // IP characters
                    {
                        if (currentText.Length < 50) // Max length for IP:port
                        {
                            currentText = currentText.Insert(caretPosition, c.ToString());
                            caretPosition++;
                            
                            // Notify that input has started (only once)
                            if (!hasNotifiedInputStart)
                            {
                                hasNotifiedInputStart = true;
                                OnInputStarted?.Invoke();
                                CoopMod.Logger.LogInfo("IP input started - notifying callback");
                            }
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
                inputLabel.text = "[888888]Enter IP Address...[-]";
                inputLabel.transform.localPosition = new Vector3(-145, 0, 0);
                inputLabel.MarkAsChanged();
                CoopMod.Logger.LogInfo($"UpdateDisplay - placeholder text set: '{inputLabel.text}'");
                return;
            }
            
            CoopMod.Logger.LogInfo($"UpdateDisplay - currentText: '{currentText}', focused: {isFocused}, caret: {caretPosition}");
            
            // Build display text with caret
            string textBeforeCaret = currentText.Substring(0, caretPosition);
            string textAfterCaret = caretPosition < currentText.Length ? currentText.Substring(caretPosition) : "";
            
            // Measure text width
            inputLabel.text = textBeforeCaret;
            inputLabel.ProcessText();
            float textBeforeCaretWidth = inputLabel.printedSize.x;
            
            // Calculate visible area width
            float visibleWidth = 290f;
            
            // Adjust scroll offset to keep caret visible
            if (textBeforeCaretWidth - scrollOffset > visibleWidth - 20)
            {
                scrollOffset = textBeforeCaretWidth - (visibleWidth - 20);
            }
            else if (textBeforeCaretWidth < scrollOffset)
            {
                scrollOffset = textBeforeCaretWidth;
            }
            
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
            
            inputLabel.text = displayText;
            inputLabel.transform.localPosition = new Vector3(-145 - scrollOffset, 0, 0);
            inputLabel.MarkAsChanged();
            
            CoopMod.Logger.LogInfo($"UpdateDisplay - final text: '{displayText}', scroll: {scrollOffset}, pos: {inputLabel.transform.localPosition}");
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
                
                // Check if input is now empty
                if (string.IsNullOrEmpty(currentText))
                {
                    // Reset state
                    hasNotifiedInputStart = false;
                    
                    // Notify that input has been cleared
                    OnInputCleared?.Invoke();
                    CoopMod.Logger.LogInfo("IP input cleared - notifying callback");
                }
            }
        }

        public void SetActive(bool active)
        {
            if (inputBackground != null)
            {
                inputBackground.SetActive(active);
            }
            if (joinButton != null)
            {
                joinButton.SetActive(active);
            }
        }

        public string GetIPAddress()
        {
            return currentText;
        }

        public void SetIPAddress(string ipAddress)
        {
            currentText = ipAddress ?? "";
            caretPosition = currentText.Length;
            scrollOffset = 0;
            UpdateDisplay();
        }
    }
}

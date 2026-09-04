using System.Collections.Generic;
using UnityEngine;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// Manages the chat display in the lobby with scrolling support
    /// </summary>
    public class ChatPanel : MonoBehaviour
    {
        private UILabel chatLabel;
        private UIPanel scrollPanel;
        private GameObject labelContainer;
        private BoxCollider scrollCollider;
        private List<string> chatMessages = new List<string>();
        private const int MAX_CHAT_MESSAGES = 50; // Increased since we have scrolling
        private int maxVisibleLines = 10;
        private int wrapCharsPerLine = 45;
        private float clipWidth = 435f;
        private float clipHeight = 185f;
        private float clipCenterX = -10f;
        private float clipCenterY = -92.5f;
        private float contentX = -10f;
        private float contentTopY = 18f;
        private int scrollLineOffset;
        private int renderedVisibleLineCapacity = 10;
        private const float CLIP_BOTTOM_PADDING = 2f;
        
        public void Initialize(Transform parent, Vector3 localPosition, UIFont font, Color? textColor = null)
        {
            // Set parent first
            transform.SetParent(parent, false);
            
            gameObject.name = "ChatPanel";
            gameObject.layer = 13;
            transform.localPosition = localPosition;
            
            // Create the clipped chat viewport.
            scrollPanel = gameObject.AddComponent<UIPanel>();
            scrollPanel.depth = 105;
            scrollPanel.clipping = UIDrawCall.Clipping.SoftClip;
            scrollPanel.clipSoftness = new Vector2(4, 4);
            
            // Set the clipping region (viewport size)
            // Center at Y=-80 to move viewport higher, closer to the header
            // X offset of -10 to shift content slightly left
            // Width increased to 430 to prevent text clipping
            scrollPanel.baseClipRegion = new Vector4(clipCenterX, clipCenterY, clipWidth, clipHeight);
            
            // Add BoxCollider for mouse wheel/drag detection (required for manual scrolling)
            scrollCollider = gameObject.AddComponent<BoxCollider>();
            scrollCollider.isTrigger = true;
            scrollCollider.size = new Vector3(clipWidth, clipHeight, 0); // Match clip region size
            scrollCollider.center = new Vector3(clipCenterX, clipCenterY, 0); // Match clip region center
            
            labelContainer = new GameObject("ChatLabelContainer");
            labelContainer.layer = 13;
            labelContainer.transform.SetParent(transform, false);
            labelContainer.transform.localPosition = new Vector3(clipCenterX, 0, 0);
            labelContainer.transform.localScale = Vector3.one;

            chatLabel = labelContainer.AddComponent<UILabel>();
            chatLabel.bitmapFont = font;
            chatLabel.text = "";
            chatLabel.fontSize = 18;
            chatLabel.color = Color.white;
            chatLabel.alignment = NGUIText.Alignment.Left;
            chatLabel.pivot = UIWidget.Pivot.TopLeft;
            chatLabel.overflowMethod = UILabel.Overflow.ResizeHeight; // Grow vertically with word wrapping
            chatLabel.width = 1000; // Very large width - we handle wrapping manually in WrapText()
            chatLabel.spacingX = 0; // No extra character spacing
            chatLabel.depth = 106;
            chatLabel.supportEncoding = true; // Enable BBCode color tags
            chatLabel.maxLineCount = 0; // Unlimited lines
            chatLabel.multiLine = true; // Enable multi-line text
            chatLabel.enabled = true;
            
            chatLabel.transform.localPosition = new Vector3(contentX, contentTopY, 0);
            
            // Wait for next frame to get accurate screen positions
            CoopMod.Instance.StartCoroutine(LogPositionsNextFrame());
            CoopMod.Logger.LogInfo("ChatPanel initialized with line-window scrolling");
        }

        public void ApplyLayout(
            Vector3 localPosition,
            float clipWidth,
            float clipHeight,
            float clipCenterX,
            float clipCenterY,
            float contentX,
            float contentTopY,
            int maxVisibleLines,
            int wrapCharsPerLine)
        {
            transform.localPosition = localPosition;
            this.clipWidth = clipWidth;
            this.clipHeight = clipHeight;
            this.clipCenterX = clipCenterX;
            this.clipCenterY = clipCenterY;
            this.contentX = contentX;
            this.contentTopY = contentTopY;
            this.maxVisibleLines = Mathf.Max(1, maxVisibleLines);
            this.renderedVisibleLineCapacity = this.maxVisibleLines;
            this.wrapCharsPerLine = Mathf.Max(1, wrapCharsPerLine);

            if (scrollPanel != null)
            {
                scrollPanel.baseClipRegion = new Vector4(this.clipCenterX, this.clipCenterY, this.clipWidth, this.clipHeight);
                scrollPanel.Refresh();
            }

            if (scrollCollider != null)
            {
                scrollCollider.size = new Vector3(this.clipWidth, this.clipHeight, 0f);
                scrollCollider.center = new Vector3(this.clipCenterX, this.clipCenterY, 0f);
            }

            if (labelContainer != null)
            {
                labelContainer.transform.localPosition = new Vector3(this.clipCenterX, 0f, 0f);
            }

            if (chatLabel != null)
            {
                chatLabel.transform.localPosition = new Vector3(this.contentX, this.contentTopY, 0f);
                chatLabel.MarkAsChanged();
                UpdateDisplay();
            }
        }
        
        private System.Collections.IEnumerator LogPositionsNextFrame()
        {
            yield return null; // Wait one frame for UI layout
            
            // Get actual screen-space positions
            var uiCamera = NGUITools.FindCameraForLayer(gameObject.layer);
            Vector3 labelScreenPos = Vector3.zero;
            Vector3 labelWorldCorner = chatLabel.worldCorners[0]; // Bottom-left corner
            
            if (uiCamera != null)
            {
                labelScreenPos = uiCamera.WorldToScreenPoint(labelWorldCorner);
            }
            
            CoopMod.Logger.LogInfo($"=== CHATPANEL DEBUG (after layout) ===");
            CoopMod.Logger.LogInfo($"  GameObject layer: {gameObject.layer}");
            CoopMod.Logger.LogInfo($"  UI Camera: {(uiCamera != null ? uiCamera.name : "NULL")}");
            CoopMod.Logger.LogInfo($"  Camera depth: {(uiCamera != null ? uiCamera.depth.ToString() : "N/A")}");
            CoopMod.Logger.LogInfo($"  Local position: {transform.localPosition}");
            CoopMod.Logger.LogInfo($"  World position: {transform.position}");
            CoopMod.Logger.LogInfo($"  Container local: {labelContainer.transform.localPosition}");
            CoopMod.Logger.LogInfo($"  Container world: {labelContainer.transform.position}");
            CoopMod.Logger.LogInfo($"  Label local: {chatLabel.transform.localPosition}");
            CoopMod.Logger.LogInfo($"  Label world: {chatLabel.transform.position}");
            CoopMod.Logger.LogInfo($"  Label pivot: {chatLabel.pivot}");
            CoopMod.Logger.LogInfo($"  Label width: {chatLabel.width}");
            CoopMod.Logger.LogInfo($"  Label worldCorner[0] (bottom-left): {labelWorldCorner}");
            CoopMod.Logger.LogInfo($"  *** LABEL SCREEN POSITION: {labelScreenPos} ***");
        }
        
        private void Update()
        {
            // Monitor scroll wheel input
            float scrollDelta = Input.GetAxis("Mouse ScrollWheel");
            if (scrollDelta != 0f)
            {
                // Check if mouse is over the chat panel
                if (scrollPanel != null)
                {
                    Vector3 mousePos = Input.mousePosition;
                    Camera uiCam = NGUITools.FindCameraForLayer(gameObject.layer);
                    
                    if (uiCam != null)
                    {
                        // Check if mouse is within the panel bounds
                        Vector3[] corners = scrollPanel.worldCorners;
                        Vector2 min = uiCam.WorldToScreenPoint(corners[0]);
                        Vector2 max = uiCam.WorldToScreenPoint(corners[2]);
                        
                        float minX = Mathf.Min(min.x, max.x);
                        float maxX = Mathf.Max(min.x, max.x);
                        float minY = Mathf.Min(min.y, max.y);
                        float maxY = Mathf.Max(min.y, max.y);
                        
                        bool mouseInPanel = mousePos.x >= minX && mousePos.x <= maxX && 
                                           mousePos.y >= minY && mousePos.y <= maxY;
                        
                        if (!mouseInPanel)
                            return;

                        List<string> lines = BuildDisplayLines();
                        int maxOffset = Mathf.Max(0, lines.Count - renderedVisibleLineCapacity);
                        if (maxOffset <= 0)
                            return;

                        int previousOffset = scrollLineOffset;
                        if (scrollDelta > 0f)
                            scrollLineOffset = Mathf.Min(maxOffset, scrollLineOffset + 1);
                        else
                            scrollLineOffset = Mathf.Max(0, scrollLineOffset - 1);

                        if (previousOffset != scrollLineOffset)
                            UpdateDisplay();
                    }
                }
            }
        }
        
        public void AddMessage(string message)
        {
            CoopMod.Logger.LogDebug($"[ChatPanel] AddMessage called: '{message}'");
            CoopMod.Logger.LogDebug($"[ChatPanel] Current message count before add: {chatMessages.Count}");
            
            chatMessages.Add(message);
            if (chatMessages.Count > MAX_CHAT_MESSAGES)
            {
                chatMessages.RemoveAt(0);
            }
            
            CoopMod.Logger.LogDebug($"[ChatPanel] Message count after add: {chatMessages.Count}");
            scrollLineOffset = 0;
            UpdateDisplay(scrollToBottom: true);
            
            CoopMod.Logger.LogDebug($"[ChatPanel] Chat message added: {message}");
        }
        
        public void ClearMessages()
        {
            CoopMod.Logger.LogDebug($"[ChatPanel] ClearMessages called! Was {chatMessages.Count} messages");
            chatMessages.Clear();
            scrollLineOffset = 0;
            UpdateDisplay(scrollToBottom: false);
            CoopMod.Logger.LogDebug($"[ChatPanel] Messages cleared, now {chatMessages.Count}");
        }
        
        // Removed ScrollToBottom coroutine - scrolling now happens directly in UpdateDisplay
        
        private void UpdateDisplay(bool scrollToBottom = false)
        {
            if (chatLabel == null)
            {
                CoopMod.Logger.LogWarning("UpdateDisplay: chatLabel is null!");
                return;
            }

            List<string> lines = BuildDisplayLines();
            int visibleLineCapacity = Mathf.Clamp(renderedVisibleLineCapacity, 1, maxVisibleLines);
            int maxOffset = Mathf.Max(0, lines.Count - visibleLineCapacity);
            scrollLineOffset = scrollToBottom ? 0 : Mathf.Clamp(scrollLineOffset, 0, maxOffset);

            int end = lines.Count - scrollLineOffset;
            int start = Mathf.Max(0, end - visibleLineCapacity);
            int requestedCount = Mathf.Max(0, end - start);
            float clipBottomY = clipCenterY - (clipHeight * 0.5f);
            float maximumTopY = contentTopY;
            float activeTopY = contentTopY;

            while (true)
            {
                SetVisibleLines(lines, start, end);

                // ResizeHeight is only reliable after NGUI has processed the new text.
                chatLabel.ProcessText();

                float requiredTopY = clipBottomY + CLIP_BOTTOM_PADDING + chatLabel.height;
                if (requiredTopY <= maximumTopY || start >= end - 1)
                {
                    activeTopY = Mathf.Max(contentTopY, Mathf.Min(requiredTopY, maximumTopY));
                    break;
                }

                // The configured line count does not fit at this font's rendered
                // height. Keep the newest lines and expose the omitted line through
                // normal chat scrolling.
                start++;
            }

            int renderedCount = Mathf.Max(0, end - start);
            if (renderedCount < requestedCount)
                renderedVisibleLineCapacity = Mathf.Max(1, renderedCount);

            chatLabel.transform.localPosition = new Vector3(contentX, activeTopY, 0f);
            CoopMod.Logger.LogDebug(
                $"[ChatPanel] UpdateDisplay: messages={chatMessages.Count}, lines={lines.Count}, " +
                $"visible={renderedCount}, capacity={renderedVisibleLineCapacity}, " +
                $"scrollOffset={scrollLineOffset}, labelHeight={chatLabel.height}, topY={activeTopY}");
        }

        private void SetVisibleLines(List<string> lines, int start, int end)
        {
            int count = Mathf.Max(0, end - start);
            string[] visibleLines = new string[count];
            for (int i = 0; i < count; i++)
            {
                visibleLines[i] = lines[start + i];
            }

            chatLabel.text = string.Join("\n", visibleLines);
        }

        private List<string> BuildDisplayLines()
        {
            List<string> lines = new List<string>();
            foreach (var msg in chatMessages)
            {
                string wrappedMsg = WrapText(msg, wrapCharsPerLine);
                string[] wrappedLines = wrappedMsg.Split('\n');

                if (wrappedMsg.StartsWith("[System]"))
                {
                    for (int i = 0; i < wrappedLines.Length; i++)
                    {
                        string line = wrappedLines[i];
                        if (i == 0)
                        {
                            string body = line.Length > 8 ? line.Substring(8).TrimStart() : "";
                            lines.Add($"[DFAA6C][System][-] [A4A2AC]{body}[-]");
                        }
                        else
                        {
                            lines.Add($"[A4A2AC]{line}[-]");
                        }
                    }
                }
                else
                {
                    foreach (string line in wrappedLines)
                    {
                        int colonIndex = line.IndexOf(':');
                        if (colonIndex > 0)
                        {
                            string username = line.Substring(0, colonIndex);
                            string messageBody = line.Substring(colonIndex);
                            lines.Add($"[DFAA6C]{username}[-][A4A2AC]{messageBody}[-]");
                        }
                        else
                        {
                            lines.Add($"[A4A2AC]{line}[-]");
                        }
                    }
                }
            }

            return lines;
        }
        
        /// <summary>
        /// Simple word-based wrapping that keeps words intact
        /// </summary>
        private string WrapText(string text, int maxCharsPerLine)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            
            string[] words = text.Split(' ');
            System.Text.StringBuilder result = new System.Text.StringBuilder();
            System.Text.StringBuilder currentLine = new System.Text.StringBuilder();
            
            foreach (string word in words)
            {
                // Check if adding this word would exceed the line length
                if (currentLine.Length + word.Length + 1 > maxCharsPerLine && currentLine.Length > 0)
                {
                    // Start a new line
                    result.AppendLine(currentLine.ToString());
                    currentLine.Clear();
                }
                
                if (currentLine.Length > 0)
                    currentLine.Append(" ");
                    
                currentLine.Append(word);
            }
            
            // Add the last line
            if (currentLine.Length > 0)
                result.Append(currentLine.ToString());
            
            return result.ToString();
        }
    }
}

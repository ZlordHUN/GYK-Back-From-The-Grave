using System.Collections.Generic;
using UnityEngine;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// SIMPLE chat panel for in-game GameGUI - just a label, no bullshit
    /// </summary>
    public class InGameChatPanel : MonoBehaviour
    {
        private UILabel chatLabel;
        private GameObject chatBackground;
        private List<string> chatMessages = new List<string>();
        private const int MAX_CHAT_MESSAGES = 50;
        private const int CHAT_WIDTH = 280;
        private const int MAX_VISIBLE_LINES = 17;
        private int scrollOffset = 0; // How many messages to scroll back
        private bool reachedFirstMessage = false; // Track if we've reached the start of chat history

        public void Initialize(Transform parent, Vector3 localPosition, UIFont font, Color textColor, GameObject backgroundTemplate)
        {
            transform.SetParent(parent, false);
            gameObject.name = "InGameChatPanel";
            gameObject.layer = 13;
            transform.localPosition = localPosition;
            
            // Create background at the SAME position as this panel
            chatBackground = Object.Instantiate(backgroundTemplate, parent);
            chatBackground.name = "InGameChatBackground";
            chatBackground.layer = 13;
            chatBackground.transform.localPosition = localPosition; // SAME position as panel
            chatBackground.transform.localScale = Vector3.one;
            
            // Clean up the background
            var saveSlotGUI = chatBackground.GetComponent<SaveSlotGUI>();
            if (saveSlotGUI != null) Object.Destroy(saveSlotGUI);
            
            var colliders = chatBackground.GetComponentsInChildren<Collider>(true);
            foreach (var col in colliders) Object.Destroy(col);
            
            var labels = chatBackground.GetComponentsInChildren<UILabel>(true);
            foreach (var label in labels) Object.Destroy(label.gameObject);
            
            var buttons = chatBackground.GetComponentsInChildren<UIButton>(true);
            foreach (var button in buttons) Object.Destroy(button.gameObject);
            
            // Resize background for chat area
            var bgWidget = chatBackground.GetComponent<UIWidget>();
            if (bgWidget != null)
            {
                bgWidget.width = 300;
                bgWidget.height = 325;
                bgWidget.pivot = UIWidget.Pivot.TopLeft; // Top-left so height extends downward
            }
            
            // Move background slightly to the left so text appears inside
            chatBackground.transform.localPosition = new Vector3(localPosition.x - 60, localPosition.y + 10, localPosition.z);
            
            chatLabel = gameObject.AddComponent<UILabel>();
            chatLabel.bitmapFont = font;
            chatLabel.text = "";
            chatLabel.fontSize = 18;
            chatLabel.color = Color.white;
            chatLabel.alignment = NGUIText.Alignment.Left;
            chatLabel.pivot = UIWidget.Pivot.TopLeft;
            chatLabel.overflowMethod = UILabel.Overflow.ResizeHeight;
            chatLabel.width = CHAT_WIDTH;
            chatLabel.depth = 106;
            chatLabel.supportEncoding = true;
            chatLabel.maxLineCount = 0;
            chatLabel.multiLine = true;
            
            // Position text to start inside the background (offset down from top)
            transform.localPosition = new Vector3(localPosition.x - 50, localPosition.y, localPosition.z);
            
            CoopMod.Logger.LogInfo($"InGameChatPanel initialized at {localPosition}");
        }

        private void Update()
        {
            // Handle scroll wheel input
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (scroll != 0f)
            {
                int oldOffset = scrollOffset;
                
                if (scroll > 0) // Scroll up = go back in history
                {
                    // Only allow scrolling up if we haven't reached the first message yet
                    if (!reachedFirstMessage)
                    {
                        scrollOffset++;
                    }
                }
                else // Scroll down = go forward to recent
                {
                    scrollOffset--;
                    scrollOffset = Mathf.Max(0, scrollOffset); // Can't go below 0
                }
                
                // Only update display if scroll offset actually changed
                if (oldOffset != scrollOffset)
                {
                    UpdateDisplay();
                }
            }
        }

        public void AddMessage(string message)
        {
            chatMessages.Add(message);
            
            if (chatMessages.Count > MAX_CHAT_MESSAGES)
            {
                chatMessages.RemoveAt(0);
            }
            
            // Reset scroll to bottom when new message arrives
            scrollOffset = 0;
            
            CoopMod.Logger.LogInfo($"[InGameChatPanel] AddMessage called: '{message}', total messages: {chatMessages.Count}");
            
            UpdateDisplay();
        }

        public int GetMessageCount()
        {
            return chatMessages.Count;
        }

        public void ClearAllMessages()
        {
            chatMessages.Clear();
            scrollOffset = 0;
            UpdateDisplay();
            CoopMod.Logger.LogInfo("[InGameChatPanel] All messages cleared");
        }

        private void UpdateDisplay()
        {
            if (chatLabel == null)
            {
                CoopMod.Logger.LogWarning("[InGameChatPanel] UpdateDisplay called but chatLabel is null!");
                return;
            }

            int totalMessages = chatMessages.Count;
            
            CoopMod.Logger.LogInfo($"[InGameChatPanel] UpdateDisplay: {totalMessages} messages, scrollOffset: {scrollOffset}");
            
            if (totalMessages == 0)
            {
                chatLabel.text = "";
                return;
            }
            
            // Build text from bottom up, counting lines as we go
            string text = "";
            int lineCount = 0;
            int messagesIncluded = 0;
            reachedFirstMessage = false; // Reset flag
            
            // Start from the most recent message and work backwards
            for (int i = totalMessages - 1 - scrollOffset; i >= 0; i--)
            {
                var msg = chatMessages[i];
                string formattedMsg = "";
                
                // Format the message with NGUI color codes
                if (msg.StartsWith("[System]"))
                {
                    formattedMsg = $"[DFAA6C][System][-] [A4A2AC]{msg.Substring(8)}[-]";
                }
                else
                {
                    int colonIndex = msg.IndexOf(':');
                    if (colonIndex > 0)
                    {
                        string username = msg.Substring(0, colonIndex);
                        string messageBody = msg.Substring(colonIndex);
                        formattedMsg = $"[DFAA6C]{username}[-][A4A2AC]{messageBody}[-]";
                    }
                    else
                    {
                        formattedMsg = $"[A4A2AC]{msg}[-]";
                    }
                }
                
                // Calculate how many lines this message will take
                // Approximate: count characters and divide by width (~40 chars per line for 280 width)
                int messageLines = CalculateLineCount(formattedMsg);
                
                // Check if adding this message would exceed the line limit
                if (lineCount + messageLines > MAX_VISIBLE_LINES)
                {
                    break; // Stop adding messages
                }
                
                // Add this message to the top of our text
                text = formattedMsg + "\n" + text;
                lineCount += messageLines;
                messagesIncluded++;
                
                // Check if we've reached the first message
                if (i == 0)
                {
                    reachedFirstMessage = true;
                }
            }

            chatLabel.text = text.TrimEnd('\n');
        }
        
        private int CalculateLineCount(string message)
        {
            // Remove NGUI color codes for accurate character counting
            string cleanText = System.Text.RegularExpressions.Regex.Replace(message, @"\[[-/]?\w*\]", "");
            
            // Keep this conservative so NGUI wrapping cannot add hidden extra rows
            // that reach the input field.
            int charsPerLine = 34;
            
            int length = cleanText.Length;
            if (length == 0) return 1;
            
            // Calculate number of lines needed
            int lines = (length + charsPerLine - 1) / charsPerLine; // Ceiling division
            return Mathf.Max(1, lines);
        }

        public void ClearMessages()
        {
            chatMessages.Clear();
            if (chatLabel != null)
            {
                chatLabel.text = "";
            }
        }
    }
}

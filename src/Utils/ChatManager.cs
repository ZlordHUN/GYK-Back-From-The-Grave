using System.Collections.Generic;
using GraveyardKeeperCoop.UI;

namespace GraveyardKeeperCoop.Utils
{
    /// <summary>
    /// Centralized chat message manager that syncs messages between lobby chat and in-game chat
    /// </summary>
    public static class ChatManager
    {
        private static List<string> allMessages = new List<string>();
        private const int MAX_MESSAGES = 100; // Keep more history than what's displayed

        /// <summary>
        /// Add a message to the global chat history and notify all listeners
        /// </summary>
        public static void AddMessage(string message)
        {
            allMessages.Add(message);
            
            // Trim old messages if we exceed the limit
            if (allMessages.Count > MAX_MESSAGES)
            {
                allMessages.RemoveAt(0);
            }

            CoopMod.Logger.LogInfo($"[ChatManager] Message added: {message}");

            // Notify lobby chat
            if (LobbyGUI.Instance != null)
            {
                LobbyGUI.Instance.AddChatMessage(message);
            }

            // Notify in-game chat (ChatGUI tab)
            if (ChatGUI.Instance != null)
            {
                ChatGUI.Instance.AddIncomingMessage("", message); // Empty name since message already formatted
            }

            // Player chat already reaches ChatOverlay through LobbyChatSync so
            // it can preserve sender identity and bubbles. System notifications
            // have no separate route; surface those here so sleep/ready/session
            // messages wake the dormant Minecraft-style overlay too.
            const string systemPrefix = "[System]";
            if (ChatOverlay.Instance != null &&
                !string.IsNullOrEmpty(message) &&
                message.StartsWith(systemPrefix))
            {
                ChatOverlay.Instance.AddSystemMessage(
                    message.Substring(systemPrefix.Length).TrimStart());
            }
        }

        /// <summary>
        /// Get all messages for initial sync (e.g., when opening a chat UI)
        /// </summary>
        public static List<string> GetAllMessages()
        {
            return new List<string>(allMessages); // Return a copy
        }

        /// <summary>
        /// Clear all messages (e.g., when leaving a lobby)
        /// </summary>
        public static void ClearMessages()
        {
            allMessages.Clear();
            CoopMod.Logger.LogInfo("[ChatManager] All messages cleared");

            // Notify both UIs to clear their local message lists
            if (LobbyGUI.Instance != null)
            {
                // LobbyGUI will be cleared when it reopens
            }

            if (ChatGUI.Instance != null)
            {
                ChatGUI.Instance.ClearAllMessages();
            }
        }

        /// <summary>
        /// Replace all messages with history from host (used when joining a lobby)
        /// This adds messages WITHOUT broadcasting them back
        /// History comes first, then any local messages added after joining are preserved
        /// </summary>
        public static void SetMessagesFromHistory(List<string> historyMessages)
        {
            // Save any local messages that were added after we started joining
            // (e.g., "Joined X's lobby!", "Waiting for host...")
            List<string> localMessages = new List<string>(allMessages);
            
            // Clear and rebuild: history first, then local messages
            allMessages.Clear();
            allMessages.AddRange(historyMessages);
            allMessages.AddRange(localMessages);
            if (allMessages.Count > MAX_MESSAGES)
                allMessages.RemoveRange(0, allMessages.Count - MAX_MESSAGES);
            
            CoopMod.Logger.LogInfo($"[ChatManager] Set history: {historyMessages.Count} history + {localMessages.Count} local = {allMessages.Count} total messages");

            // Update the lobby UI with all messages
            CoopMod.Logger.LogInfo($"[ChatManager] LobbyGUI.Instance: {(LobbyGUI.Instance != null ? "exists" : "null")}");
            if (LobbyGUI.Instance != null)
            {
                CoopMod.Logger.LogInfo($"[ChatManager] LobbyGUI.activeSelf: {LobbyGUI.Instance.gameObject.activeSelf}");
                LobbyGUI.Instance.ClearChatMessages();
                foreach (string msg in allMessages)
                {
                    CoopMod.Logger.LogInfo($"[ChatManager] Adding message to UI: {msg}");
                    LobbyGUI.Instance.AddChatMessage(msg);
                }
                CoopMod.Logger.LogInfo($"[ChatManager] Finished adding {allMessages.Count} messages to UI");
            }
            else
            {
                CoopMod.Logger.LogWarning("[ChatManager] LobbyGUI.Instance is null when setting history!");
            }
        }

        /// <summary>
        /// Get the last N messages (for in-game chat which shows fewer messages)
        /// </summary>
        public static List<string> GetLastMessages(int count)
        {
            int startIndex = System.Math.Max(0, allMessages.Count - count);
            int messageCount = System.Math.Min(count, allMessages.Count);
            
            if (messageCount == 0)
                return new List<string>();

            return allMessages.GetRange(startIndex, messageCount);
        }
    }
}

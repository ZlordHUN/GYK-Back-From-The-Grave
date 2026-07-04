using System;
using System.Collections.Generic;
using System.Text;
using Steamworks;
using GraveyardKeeperCoop.Network;
using GraveyardKeeperCoop.Utils;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Handles synchronization of lobby chat messages between players
    /// When a new player joins, they receive the full chat history from the host
    /// </summary>
    public static class LobbyChatSync
    {
        // Message type prefixes for P2P communication
        public const string MSG_CHAT_HISTORY_REQUEST = "CHAT_HISTORY_REQ";
        public const string MSG_CHAT_HISTORY_RESPONSE = "CHAT_HISTORY:";
        public const string MSG_CHAT_MESSAGE = "CHAT:";
        
        // Channel for chat messages (different from invite channel 0)
        public const int CHAT_CHANNEL = 1;
        
        // Separator for multiple messages in history
        private const string MESSAGE_SEPARATOR = "|||";

        /// <summary>
        /// Request chat history from the host
        /// Called when a client joins the lobby
        /// </summary>
        public static void RequestChatHistory()
        {
            var lobbyManager = SteamLobbyManager.Instance;
            if (!lobbyManager.IsInLobby)
            {
                CoopMod.Logger.LogWarning("[ChatSync] Cannot request chat history - not in a lobby");
                return;
            }
            
            if (lobbyManager.IsHost)
            {
                CoopMod.Logger.LogInfo("[ChatSync] We are the host, no need to request history");
                return;
            }
            
            // Get the host's Steam ID
            CSteamID hostID = lobbyManager.GetLobbyOwner();
            if (hostID == CSteamID.Nil)
            {
                CoopMod.Logger.LogError("[ChatSync] Cannot get host ID");
                return;
            }
            
            CoopMod.Logger.LogInfo($"[ChatSync] Requesting chat history from host: {hostID}");
            SendChatP2PMessage(hostID, MSG_CHAT_HISTORY_REQUEST);
        }

        /// <summary>
        /// Send chat history to a requesting client
        /// Called by the host when a client requests history
        /// </summary>
        public static void SendChatHistoryTo(CSteamID clientID)
        {
            var messages = ChatManager.GetAllMessages();
            
            if (messages.Count == 0)
            {
                CoopMod.Logger.LogInfo("[ChatSync] No chat history to send");
                return;
            }
            
            // Combine all messages with separator
            string historyPayload = string.Join(MESSAGE_SEPARATOR, messages);
            string fullMessage = MSG_CHAT_HISTORY_RESPONSE + historyPayload;
            
            CoopMod.Logger.LogInfo($"[ChatSync] Sending {messages.Count} messages to client {clientID}");
            SendChatP2PMessage(clientID, fullMessage);
        }

        /// <summary>
        /// Handle incoming chat-related P2P messages
        /// Returns true if the message was handled, false if it's not a chat message
        /// </summary>
        public static bool HandleChatMessage(CSteamID senderID, string message)
        {
            if (message == MSG_CHAT_HISTORY_REQUEST)
            {
                CoopMod.Logger.LogInfo($"[ChatSync] Received chat history request from {senderID}");
                
                // Only host should respond to history requests
                if (SteamLobbyManager.Instance.IsHost)
                {
                    SendChatHistoryTo(senderID);
                }
                return true;
            }
            
            if (message.StartsWith(MSG_CHAT_HISTORY_RESPONSE))
            {
                CoopMod.Logger.LogInfo("[ChatSync] Received chat history from host");
                string payload = message.Substring(MSG_CHAT_HISTORY_RESPONSE.Length);
                ProcessChatHistory(payload);
                return true;
            }
            
            if (message.StartsWith(MSG_CHAT_MESSAGE))
            {
                string chatContent = message.Substring(MSG_CHAT_MESSAGE.Length);
                CoopMod.Logger.LogInfo($"[ChatSync] Received chat message: {chatContent}");
                ChatManager.AddMessage(chatContent);
                
                // Also forward to ChatOverlay (history accumulates even when hidden so it's
                // already populated the moment the player opens the overlay back up).
                if (UI.ChatOverlay.Instance != null)
                {
                    // Extract sender name from message (format is "Name: message")
                    int colonIndex = chatContent.IndexOf(':');
                    if (colonIndex > 0)
                    {
                        string senderName = chatContent.Substring(0, colonIndex).Trim();
                        string text = chatContent.Substring(colonIndex + 1).Trim();
                        UI.ChatOverlay.Instance.ReceiveMessage(senderName, text);
                    }
                    else
                    {
                        UI.ChatOverlay.Instance.AddLine(chatContent);
                    }
                }
                return true;
            }
            
            return false;
        }

        /// <summary>
        /// Process received chat history and replace local chat with it
        /// </summary>
        private static void ProcessChatHistory(string payload)
        {
            if (string.IsNullOrEmpty(payload))
            {
                CoopMod.Logger.LogInfo("[ChatSync] Empty chat history received");
                return;
            }
            
            string[] messages = payload.Split(new[] { MESSAGE_SEPARATOR }, StringSplitOptions.RemoveEmptyEntries);
            CoopMod.Logger.LogInfo($"[ChatSync] Processing {messages.Length} messages from history");
            
            // Replace ChatManager's messages with the history
            // This properly stores them and updates the UI
            ChatManager.SetMessagesFromHistory(new System.Collections.Generic.List<string>(messages));
        }

        /// <summary>
        /// Broadcast a chat message to all players in the lobby
        /// </summary>
        public static void BroadcastChatMessage(string message)
        {
            var lobbyManager = SteamLobbyManager.Instance;
            if (!lobbyManager.IsInLobby)
            {
                CoopMod.Logger.LogWarning("[ChatSync] Cannot broadcast - not in a lobby");
                return;
            }
            
            string fullMessage = MSG_CHAT_MESSAGE + message;
            
            // Get all lobby members and send to everyone except ourselves
            int memberCount = lobbyManager.GetLobbyMemberCount();
            CSteamID localID = SteamUser.GetSteamID();
            
            for (int i = 0; i < memberCount; i++)
            {
                CSteamID memberID = SteamMatchmaking.GetLobbyMemberByIndex(lobbyManager.CurrentLobbyID, i);
                if (memberID != localID)
                {
                    CoopMod.Logger.LogInfo($"[ChatSync] Sending chat message to {memberID}");
                    SendChatP2PMessage(memberID, fullMessage);
                }
            }
        }

        /// <summary>
        /// Send a P2P message on the chat channel
        /// </summary>
        private static bool SendChatP2PMessage(CSteamID recipientID, string message)
        {
            if (!SteamManager.Initialized)
            {
                CoopMod.Logger.LogError("[ChatSync] Steam not initialized");
                return false;
            }
            
            byte[] data = Encoding.UTF8.GetBytes(message);
            
            bool success = SteamNetworking.SendP2PPacket(
                recipientID,
                data,
                (uint)data.Length,
                EP2PSend.k_EP2PSendReliable,
                CHAT_CHANNEL
            );
            
            if (success)
            {
                CoopMod.Logger.LogInfo($"[ChatSync] ✓ Message sent to {recipientID} on channel {CHAT_CHANNEL}");
            }
            else
            {
                CoopMod.Logger.LogError($"[ChatSync] ✗ Failed to send message to {recipientID}");
            }
            
            return success;
        }

        /// <summary>
        /// Process incoming P2P packets on the chat channel
        /// Should be called from the main update loop
        /// </summary>
        public static void ProcessIncomingMessages()
        {
            if (!SteamManager.Initialized)
                return;
            
            uint msgSize;
            while (SteamNetworking.IsP2PPacketAvailable(out msgSize, CHAT_CHANNEL))
            {
                byte[] data = new byte[msgSize];
                CSteamID senderID;
                uint bytesRead;
                
                if (SteamNetworking.ReadP2PPacket(data, msgSize, out bytesRead, out senderID, CHAT_CHANNEL))
                {
                    string message = Encoding.UTF8.GetString(data, 0, (int)bytesRead);
                    string senderName = SteamFriends.GetFriendPersonaName(senderID);
                    CoopMod.Logger.LogInfo($"[ChatSync] Received from {senderName}: {message}");
                    
                    // Try ready system first, then chat
                    if (!LobbyReadySystem.HandleReadyMessage(senderID, message))
                    {
                        HandleChatMessage(senderID, message);
                    }
                }
            }
        }
    }
}

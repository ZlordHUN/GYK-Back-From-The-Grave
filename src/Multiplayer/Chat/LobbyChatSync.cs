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

        private const int MaxNativePacketsPerFrame = 32;
        private const int MaxLobbyMessageBytes = 256 * 1024;
        private const int MaxChatMessageCharacters = 2048;
        private const int MaxHistoryMessages = 100;
        
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
            
            int start = Math.Max(0, messages.Count - MaxHistoryMessages);
            var boundedMessages = new List<string>(Math.Min(messages.Count, MaxHistoryMessages));
            for (int i = start; i < messages.Count; i++)
            {
                if (!string.IsNullOrEmpty(messages[i]) && messages[i].Length <= MaxChatMessageCharacters)
                    boundedMessages.Add(messages[i]);
            }
            if (boundedMessages.Count == 0)
                return;

            // Combine only the bounded tail of history.
            string historyPayload = string.Join(MESSAGE_SEPARATOR, boundedMessages);
            string fullMessage = MSG_CHAT_HISTORY_RESPONSE + historyPayload;
            
            CoopMod.Logger.LogInfo($"[ChatSync] Sending {boundedMessages.Count} messages to client {clientID}");
            SendChatP2PMessage(clientID, fullMessage);
        }

        /// <summary>
        /// Handle incoming chat-related P2P messages
        /// Returns true if the message was handled, false if it's not a chat message
        /// </summary>
        public static bool HandleChatMessage(CSteamID senderID, string message)
        {
            if (string.IsNullOrEmpty(message))
                return false;

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
                var lobbyManager = SteamLobbyManager.Instance;
                if (lobbyManager.IsHost || senderID != lobbyManager.GetLobbyOwner())
                {
                    CoopMod.Logger.LogWarning($"[ChatSync] Ignored chat history from non-host {senderID}");
                    return true;
                }

                CoopMod.Logger.LogInfo("[ChatSync] Received chat history from host");
                string payload = message.Substring(MSG_CHAT_HISTORY_RESPONSE.Length);
                ProcessChatHistory(payload);
                return true;
            }
            
            if (message.StartsWith(MSG_CHAT_MESSAGE))
            {
                string chatContent = message.Substring(MSG_CHAT_MESSAGE.Length);
                if (chatContent.Length > MaxChatMessageCharacters)
                {
                    CoopMod.Logger.LogWarning($"[ChatSync] Ignored oversized chat message from {senderID}");
                    return true;
                }
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
                        UI.ChatOverlay.Instance.ReceiveMessage(
                            senderName,
                            text,
                            senderID);
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
            int start = Math.Max(0, messages.Length - MaxHistoryMessages);
            var boundedHistory = new List<string>(Math.Min(messages.Length, MaxHistoryMessages));
            for (int i = start; i < messages.Length; i++)
            {
                if (messages[i].Length <= MaxChatMessageCharacters)
                    boundedHistory.Add(messages[i]);
            }
            CoopMod.Logger.LogInfo($"[ChatSync] Processing {boundedHistory.Count} bounded messages from history");
            
            // Replace ChatManager's messages with the history
            // This properly stores them and updates the UI
            ChatManager.SetMessagesFromHistory(boundedHistory);
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
            
            if (message.StartsWith(MSG_CHAT_MESSAGE) &&
                message.Length - MSG_CHAT_MESSAGE.Length > MaxChatMessageCharacters)
            {
                CoopMod.Logger.LogWarning("[ChatSync] Refusing oversized chat content");
                return false;
            }

            byte[] data = Encoding.UTF8.GetBytes(message);
            if (data.Length > MaxLobbyMessageBytes)
            {
                CoopMod.Logger.LogWarning(
                    $"[ChatSync] Refusing oversized lobby message ({data.Length} bytes)");
                return false;
            }

            bool success = SteamP2PManager.Instance.SendBinary(
                recipientID,
                data,
                EP2PSend.k_EP2PSendReliable,
                CHAT_CHANNEL);
            
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
            int processed = 0;
            while (processed < MaxNativePacketsPerFrame &&
                   SteamNetworking.IsP2PPacketAvailable(out msgSize, CHAT_CHANNEL))
            {
                byte[] data = new byte[msgSize];
                CSteamID senderID;
                uint bytesRead;
                
                if (SteamNetworking.ReadP2PPacket(data, msgSize, out bytesRead, out senderID, CHAT_CHANNEL))
                {
                    if (bytesRead == 0 || bytesRead > MaxLobbyMessageBytes)
                    {
                        CoopMod.Logger.LogWarning(
                            $"[ChatSync] Dropped invalid native lobby message ({bytesRead} bytes) from {senderID}");
                    }
                    else
                    {
                        SteamP2PManager.Instance.DispatchNativeLobbyLaneMessage(
                            senderID,
                            data,
                            (int)bytesRead);
                    }
                }
                processed++;
            }
        }
    }
}

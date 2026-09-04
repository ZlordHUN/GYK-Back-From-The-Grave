using UnityEngine;
using System.Collections.Generic;
using Steamworks;
using GraveyardKeeperCoop.Multiplayer;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// Chat tab in the GameGUI (alongside Map, Inventory, Techs, NPCs)
    /// Displays chat messages and input field, integrated into the game's tab system
    /// </summary>
    public class ChatGUI : BaseGameGUI
    {
        private static ChatGUI _instance;
        public static ChatGUI Instance => _instance;

        private InGameChatPanel chatPanel;
        private InGameChatInputPanel chatInputPanel;
        private InGamePlayerListPanel playerListPanel;
        private UILabel titleLabel;
        private GameObject contentRoot;
        
        // References to templates we need
        private GameObject saveSlotTemplate;
        private UIFont gameFont;
        private bool isInitialized = false;
        private CSteamID pendingKickTarget = CSteamID.Nil;
        private string pendingKickPlayerName = "";
        private static readonly ChatResolutionLayout DefaultResolutionLayout = new ChatResolutionLayout(
            "Default",
            0,
            0,
            Vector3.zero,
            Vector3.one);
        private static readonly ChatResolutionLayout[] ResolutionLayouts =
        {
            new ChatResolutionLayout(
                "SteamDeck1280x800",
                1280,
                800,
                Vector3.zero,
                new Vector3(0.78f, 0.78f, 1f)),
            new ChatResolutionLayout(
                "1366x768",
                1366,
                768,
                Vector3.zero,
                new Vector3(0.72f, 0.72f, 1f)),
            new ChatResolutionLayout(
                "1440x900",
                1440,
                900,
                Vector3.zero,
                new Vector3(0.78f, 0.78f, 1f)),
            new ChatResolutionLayout(
                "1600x900",
                1600,
                900,
                Vector3.zero,
                new Vector3(0.78f, 0.78f, 1f)),
            new ChatResolutionLayout(
                "1920x800",
                1920,
                800,
                Vector3.zero,
                new Vector3(0.72f, 0.72f, 1f)),
            new ChatResolutionLayout(
                "1920x1080",
                1920,
                1080,
                Vector3.zero,
                new Vector3(0.78f, 0.78f, 1f)),
            new ChatResolutionLayout(
                "1920x1200",
                1920,
                1200,
                Vector3.zero,
                new Vector3(0.78f, 0.78f, 1f)),
            new ChatResolutionLayout(
                "1920x1280",
                1920,
                1280,
                Vector3.zero,
                new Vector3(0.78f, 0.78f, 1f)),
            new ChatResolutionLayout(
                "1920x1440",
                1920,
                1440,
                Vector3.zero,
                new Vector3(0.78f, 0.78f, 1f)),
            new ChatResolutionLayout(
                "2048x1152",
                2048,
                1152,
                Vector3.zero,
                new Vector3(0.78f, 0.78f, 1f)),
            new ChatResolutionLayout(
                "2048x1536",
                2048,
                1536,
                Vector3.zero,
                new Vector3(0.78f, 0.78f, 1f)),
            new ChatResolutionLayout(
                "2560x1080",
                2560,
                1080,
                Vector3.zero,
                new Vector3(0.78f, 0.78f, 1f)),
            new ChatResolutionLayout(
                "1600x1200",
                1600,
                1200,
                Vector3.zero,
                new Vector3(0.78f, 0.78f, 1f)),
            new ChatResolutionLayout(
                "1680x1050",
                1680,
                1050,
                Vector3.zero,
                new Vector3(0.78f, 0.78f, 1f)),
            new ChatResolutionLayout(
                "1440x960",
                1440,
                960,
                Vector3.zero,
                new Vector3(0.78f, 0.78f, 1f)),
            new ChatResolutionLayout(
                "1440x1080",
                1440,
                1080,
                Vector3.zero,
                new Vector3(0.78f, 0.78f, 1f)),
            new ChatResolutionLayout(
                "1280x960",
                1280,
                960,
                Vector3.zero,
                new Vector3(0.78f, 0.78f, 1f)),
            new ChatResolutionLayout(
                "1280x1024",
                1280,
                1024,
                Vector3.zero,
                new Vector3(0.78f, 0.78f, 1f)),
            new ChatResolutionLayout(
                "1400x1050",
                1400,
                1050,
                Vector3.zero,
                new Vector3(0.78f, 0.78f, 1f))
        };

        private sealed class ChatResolutionLayout
        {
            public readonly string Name;
            public readonly int ScreenWidth;
            public readonly int ScreenHeight;
            public readonly Vector3 RootPosition;
            public readonly Vector3 RootScale;

            public ChatResolutionLayout(
                string name,
                int screenWidth,
                int screenHeight,
                Vector3 rootPosition,
                Vector3 rootScale)
            {
                Name = name;
                ScreenWidth = screenWidth;
                ScreenHeight = screenHeight;
                RootPosition = rootPosition;
                RootScale = rootScale;
            }

            public bool Matches(int screenWidth, int screenHeight)
            {
                if (Name == "1366x768")
                {
                    return Mathf.Abs(screenWidth - ScreenWidth) <= 12 &&
                           Mathf.Abs(screenHeight - ScreenHeight) <= 12;
                }

                return ScreenWidth == screenWidth && ScreenHeight == screenHeight;
            }
        }

        public static ChatGUI Create()
        {
            if (_instance != null)
            {
                CoopMod.Logger.LogWarning("[ChatGUI] Instance already exists!");
                return _instance;
            }

            // Create a new GameObject in the GUIElements hierarchy
            var guiElements = GUIElements.me;
            if (guiElements == null)
            {
                CoopMod.Logger.LogError("[ChatGUI] GUIElements not found! Cannot create ChatGUI.");
                return null;
            }

            GameObject chatGUIObj = new GameObject("ChatGUI");
            chatGUIObj.transform.SetParent(guiElements.transform, false);
            chatGUIObj.layer = 5; // UI layer
            
            _instance = chatGUIObj.AddComponent<ChatGUI>();
            
            CoopMod.Logger.LogInfo("[ChatGUI] Created successfully");
            return _instance;
        }

        public override void Init()
        {
            if (isInitialized)
            {
                CoopMod.Logger.LogInfo("[ChatGUI] Already initialized");
                return;
            }

            CoopMod.Logger.LogInfo("[ChatGUI] Initializing...");

            // Find templates we need
            FindTemplates();

            if (saveSlotTemplate == null || gameFont == null)
            {
                CoopMod.Logger.LogError("[ChatGUI] Missing required templates!");
                return;
            }

            // Create the UI structure
            BuildUI();

            isInitialized = true;
            base.Init();
            
            // Start hidden
            gameObject.SetActive(false);

            CoopMod.Logger.LogInfo("[ChatGUI] Initialization complete");
        }

        private void FindTemplates()
        {
            // Find SaveSlotsMenuGUI to get the save slot template
            var saveSlotsMenus = Resources.FindObjectsOfTypeAll<SaveSlotsMenuGUI>();
            if (saveSlotsMenus.Length > 0)
            {
                var saveSlots = saveSlotsMenus[0].GetComponentsInChildren<SaveSlotGUI>(true);
                if (saveSlots != null && saveSlots.Length > 0)
                {
                    saveSlotTemplate = saveSlots[0].gameObject;
                    CoopMod.Logger.LogInfo($"[ChatGUI] Found save slot template");
                }
            }

            // Get the game's font from inventory
            var inventoryGUIs = Resources.FindObjectsOfTypeAll<InventoryGUI>();
            if (inventoryGUIs.Length > 0)
            {
                var labels = inventoryGUIs[0].GetComponentsInChildren<UILabel>(true);
                if (labels.Length > 0)
                {
                    gameFont = labels[0].bitmapFont;
                    CoopMod.Logger.LogInfo($"[ChatGUI] Found game font");
                }
            }
        }

        private void BuildUI()
        {
            // Create content root
            contentRoot = new GameObject("ChatContent");
            contentRoot.layer = 13; // Use UI layer (same as NGUI elements)
            contentRoot.transform.SetParent(transform, false);
            contentRoot.transform.localPosition = Vector3.zero;
            contentRoot.transform.localScale = Vector3.one;

            // Add UIPanel for depth control
            var panel = contentRoot.AddComponent<UIPanel>();
            panel.depth = 100;
            panel.clipping = UIDrawCall.Clipping.None; // Don't clip content
            
            CoopMod.Logger.LogInfo("[ChatGUI] Content root created with UIPanel (no clipping)");

            // Don't create a title - the tab button already shows "CHAT"
            // Just create the chat panel and input directly

            // Create chat panel (message display)
            CreateChatPanel();

            // Create input field
            CreateInputField();

            // Create player list
            CreatePlayerList();

            ApplyResolutionLayout();

            CoopMod.Logger.LogInfo("[ChatGUI] UI built successfully");
        }

        private void CreateTitle()
        {
            GameObject titleObj = new GameObject("ChatTitle");
            titleObj.layer = 5;
            titleObj.transform.SetParent(contentRoot.transform, false);
            titleObj.transform.localPosition = new Vector3(0, 200, 0); // Top of the screen
            titleObj.transform.localScale = Vector3.one;

            titleLabel = titleObj.AddComponent<UILabel>();
            titleLabel.bitmapFont = gameFont;
            titleLabel.text = "CHAT";
            titleLabel.fontSize = 28;
            titleLabel.color = new Color(1f, 0.9f, 0.7f); // Gold color like other headers
            titleLabel.alignment = NGUIText.Alignment.Center;
            titleLabel.width = 600;
            titleLabel.height = 50;
            titleLabel.depth = 105;
            titleLabel.pivot = UIWidget.Pivot.Top;

            CoopMod.Logger.LogInfo("[ChatGUI] Title created");
        }

        private void CreateChatPanel()
        {
            GameObject chatPanelObj = new GameObject("InGameChatMessagesPanel");
            chatPanel = chatPanelObj.AddComponent<InGameChatPanel>();
            
            // Position chat panel well ABOVE the input field
            // Input is at Y=-180, so put chat at Y=-100 (80 units above)
            chatPanel.Initialize(
                contentRoot.transform, 
                new Vector3(-225, 175, 0),
                gameFont,
                Color.white,
                saveSlotTemplate
            );

            CoopMod.Logger.LogInfo("[ChatGUI] In-game chat panel created");
        }

        private void CreateInputField()
        {
            GameObject inputObj = new GameObject("InGameChatInputField");
            chatInputPanel = inputObj.AddComponent<InGameChatInputPanel>();
            
            // Pass background template for styled input
            chatInputPanel.Initialize(
                contentRoot.transform,
                new Vector3(-225, -180, 0),
                gameFont,
                OnMessageSent,
                saveSlotTemplate
            );

            // Create Send button next to input field (to the right)
            // Input is at X=-225, width ~220, so button at X=-5 (closer to input)
            chatInputPanel.CreateSendButton(contentRoot.transform, new Vector3(-10, -180, 0));

            CoopMod.Logger.LogInfo("[ChatGUI] In-game input field and Send button created");
        }

        private void CreatePlayerList()
        {
            GameObject playerListObj = new GameObject("InGamePlayerList");
            playerListPanel = playerListObj.AddComponent<InGamePlayerListPanel>();
            
            // Position to the right of chat panel
            playerListPanel.Initialize(
                contentRoot.transform,
                new Vector3(150, 125, 0), // Edit these to position the player names inside the in game player list
                gameFont,
                saveSlotTemplate,
                OnKickPlayerRequested
            );

            // Add local player with Steam username
            string localPlayerName = GraveyardKeeperCoopMod.Utils.SteamHelper.GetLocalPlayerName();
            playerListPanel.AddPlayer(localPlayerName);
            playerListPanel.SetKickButtonsEnabled(CanKickPlayers());

            CoopMod.Logger.LogInfo($"[ChatGUI] In-game player list created with local player: {localPlayerName}");
        }

        public override void Open()
        {
            CoopMod.Logger.LogInfo("[ChatGUI] Opening...");
            base.Open();
            ApplyResolutionLayout();

            if (playerListPanel != null)
            {
                playerListPanel.SetKickButtonsEnabled(CanKickPlayers());
            }
            
            // Sync with ChatManager to load all existing messages (only if panel is empty)
            if (chatPanel != null && chatPanel.GetMessageCount() == 0)
            {
                SyncWithChatManager();
            }
            
            // Don't auto-focus - let user click to activate
        }

        private void ApplyResolutionLayout()
        {
            ChatResolutionLayout layout = GetActiveResolutionLayout();
            if (layout == DefaultResolutionLayout)
            {
                CoopMod.Logger.LogInfo($"[ChatGUI] Using default unscaled layout for {Screen.width}x{Screen.height}");
                return;
            }

            if (contentRoot != null)
            {
                contentRoot.transform.localPosition = layout.RootPosition;
                contentRoot.transform.localScale = layout.RootScale;
            }

            CoopMod.Logger.LogInfo(
                $"[ChatGUI] Applied layout {layout.Name}: " +
                $"root={layout.RootPosition}, scale={layout.RootScale}");
        }

        private static ChatResolutionLayout GetActiveResolutionLayout()
        {
            int screenWidth = Screen.width;
            int screenHeight = Screen.height;
            for (int i = 0; i < ResolutionLayouts.Length; i++)
            {
                ChatResolutionLayout layout = ResolutionLayouts[i];
                if (layout.Matches(screenWidth, screenHeight))
                    return layout;
            }

            return DefaultResolutionLayout;
        }

        public override void OpenFromGameGUI()
        {
            CoopMod.Logger.LogInfo("[ChatGUI] OpenFromGameGUI called");
            Open();
        }

        /// <summary>
        /// Sync chat panel with all messages from ChatManager
        /// </summary>
        private void SyncWithChatManager()
        {
            if (chatPanel == null) return;

            var messages = GraveyardKeeperCoop.Utils.ChatManager.GetAllMessages();
            CoopMod.Logger.LogInfo($"[ChatGUI] Syncing {messages.Count} messages from ChatManager");
            
            foreach (var message in messages)
            {
                chatPanel.AddMessage(message);
            }
        }

        public override void Hide(bool play_hide_sound = true)
        {
            CoopMod.Logger.LogInfo("[ChatGUI] Hiding...");
            
            // Unfocus input
            if (chatInputPanel != null)
            {
                chatInputPanel.Unfocus();
            }
            
            base.Hide(play_hide_sound);
        }

        public override void CloseFromGameGUI()
        {
            CoopMod.Logger.LogInfo("[ChatGUI] CloseFromGameGUI called");
            Hide(false);
        }

        /// <summary>
        /// Clear all messages from the chat panel (called when returning to menu)
        /// </summary>
        public void ClearAllMessages()
        {
            if (chatPanel != null)
            {
                chatPanel.ClearAllMessages();
                CoopMod.Logger.LogInfo("[ChatGUI] Cleared all messages from chat panel");
            }
        }

        private void OnMessageSent(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            CoopMod.Logger.LogInfo($"[ChatGUI] Message sent: {message}");

            // Add message to chat panel
            if (chatPanel != null)
            {
                string formattedMessage = $"[YOU]: {message}";
                chatPanel.AddMessage(formattedMessage);
            }

            LobbyChatSync.BroadcastChatMessage(message);
        }

        /// <summary>
        /// Add a remote player to the in-game player list
        /// </summary>
        public void AddRemotePlayer(string playerName)
        {
            AddRemotePlayer(playerName, CSteamID.Nil);
        }

        public void AddRemotePlayer(string playerName, CSteamID steamID)
        {
            if (playerListPanel != null)
            {
                playerListPanel.AddPlayer(playerName, steamID, false);
                playerListPanel.SetKickButtonsEnabled(CanKickPlayers());
                CoopMod.Logger.LogInfo($"[ChatGUI] Added remote player to list: {playerName}");
            }
        }

        /// <summary>
        /// Remove a remote player from the in-game player list
        /// </summary>
        public void RemoveRemotePlayer(string playerName)
        {
            if (playerListPanel != null)
            {
                playerListPanel.RemovePlayer(playerName);
                CoopMod.Logger.LogInfo($"[ChatGUI] Removed remote player from list: {playerName}");
            }
        }

        private bool CanKickPlayers()
        {
            if (!SteamManager.Initialized)
                return false;

            var onlineCoop = GraveyardKeeperCoop.Network.OnlineCoopManager.Instance;
            if (onlineCoop != null && onlineCoop.IsOnlineCoopEnabled)
            {
                return onlineCoop.IsHost;
            }

            return GraveyardKeeperCoop.Network.SteamLobbyManager.Instance?.IsHost == true;
        }

        private void OnKickPlayerRequested(CSteamID targetID)
        {
            if (!CanKickPlayers())
            {
                CoopMod.Logger.LogWarning("[ChatGUI] Ignoring kick request because this client is not host");
                return;
            }

            if (targetID == CSteamID.Nil || targetID == SteamUser.GetSteamID())
                return;

            string playerName = SteamFriends.GetFriendPersonaName(targetID);
            if (string.IsNullOrEmpty(playerName))
            {
                playerName = targetID.ToString();
            }

            ShowKickConfirmation(targetID, playerName);
        }

        private void ShowKickConfirmation(CSteamID targetID, string playerName)
        {
            pendingKickTarget = targetID;
            pendingKickPlayerName = playerName ?? targetID.ToString();

            var dialog = GUIElements.me?.dialog;
            if (dialog == null)
            {
                CoopMod.Logger.LogWarning("[ChatGUI] DialogGUI unavailable; kick confirmation cannot be shown");
                ClearPendingKick();
                return;
            }

            dialog.Open(
                $"Kick {pendingKickPlayerName} from Lobby?",
                "Yes",
                new GJCommons.VoidDelegate(OnKickConfirmed),
                "No",
                new GJCommons.VoidDelegate(OnKickCancelled),
                null,
                GameKey.Select,
                GameKey.Back,
                "",
                false,
                "");
        }

        private void OnKickConfirmed()
        {
            CSteamID targetID = pendingKickTarget;
            string playerName = pendingKickPlayerName;
            ClearPendingKick();

            if (targetID == CSteamID.Nil || targetID == SteamUser.GetSteamID())
                return;

            string reason = "You were removed from the lobby by the host.";
            bool sent = GraveyardKeeperCoop.Network.SteamP2PManager.Instance.SendLobbyKick(targetID, reason);
            if (sent)
            {
                CoopMod.Logger.LogInfo($"[ChatGUI] Kick requested for {playerName} ({targetID})");
                GraveyardKeeperCoop.Utils.ChatManager.AddMessage($"[System] Removing {playerName} from the lobby...");

                var onlineCoop = GraveyardKeeperCoop.Network.OnlineCoopManager.Instance;
                if (onlineCoop != null && onlineCoop.IsHost && onlineCoop.IsRemotePlayer(targetID))
                {
                    onlineCoop.HandleRemotePlayerKicked(targetID, playerName);
                }
            }
            else
            {
                CoopMod.Logger.LogWarning($"[ChatGUI] Failed to send lobby kick to {playerName} ({targetID})");
                GraveyardKeeperCoop.Utils.ChatManager.AddMessage($"[System] Could not remove {playerName} from the lobby.");
            }
        }

        private void OnKickCancelled()
        {
            CoopMod.Logger.LogInfo($"[ChatGUI] Kick cancelled for {pendingKickPlayerName} ({pendingKickTarget})");
            ClearPendingKick();
        }

        private void ClearPendingKick()
        {
            pendingKickTarget = CSteamID.Nil;
            pendingKickPlayerName = "";
        }

        public void AddIncomingMessage(string playerName, string message)
        {
            if (chatPanel != null)
            {
                string formattedMessage;
                
                // If playerName is empty, the message is already formatted (e.g., "[System] ...")
                // Otherwise, format it as "[PlayerName]: message"
                if (string.IsNullOrEmpty(playerName))
                {
                    formattedMessage = message;
                }
                else
                {
                    formattedMessage = $"[{playerName}]: {message}";
                }
                
                chatPanel.AddMessage(formattedMessage);
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }
    }
}

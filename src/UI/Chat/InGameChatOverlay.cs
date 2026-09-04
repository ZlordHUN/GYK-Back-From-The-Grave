using UnityEngine;
using System.Reflection;
using GraveyardKeeperCoop.LocalCoop;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// In-game chat button overlay
    /// Shows a button in the bottom-left corner when in-game
    /// Button opens the Chat tab in GameGUI
    /// Hidden when local co-op is active (not needed for local multiplayer)
    /// </summary>
    public class InGameChatOverlay : MonoBehaviour
    {
        private static InGameChatOverlay _instance;
        public static InGameChatOverlay Instance => _instance;

        private GameObject chatButtonObject;
        private bool hasCreatedButton = false;
        private UIWidget chatButtonWidget;
        private Camera uiCamera;
        private int framesSinceLastCheck = 0;
        private const int CHECK_INTERVAL = 60; // Check every 60 frames (~1 second at 60fps)

        // Resolution tracking for dynamic repositioning
        private int lastScreenWidth = 0;
        private int lastScreenHeight = 0;
        private HUD cachedHud = null;

        public static InGameChatOverlay Create()
        {
            if (_instance != null)
                return _instance;

            CoopMod.Logger.LogInfo("[InGameChat] Creating chat overlay...");

            // Create a simple GameObject that persists
            GameObject overlayObj = new GameObject("InGameChatOverlay");
            Object.DontDestroyOnLoad(overlayObj);

            _instance = overlayObj.AddComponent<InGameChatOverlay>();

            CoopMod.Logger.LogInfo("[InGameChat] Chat overlay created (will check for HUD periodically)");
            return _instance;
        }

        /// <summary>
        /// Lightweight Update - only checks every 60 frames if we should create button
        /// Also handles manual click detection for the chat button
        /// Hides button when local co-op is active
        /// Detects resolution changes and repositions button
        /// </summary>
        private void Update()
        {
            // Check for resolution changes and reposition button
            if (hasCreatedButton && chatButtonObject != null && cachedHud != null)
            {
                if (Screen.width != lastScreenWidth || Screen.height != lastScreenHeight)
                {
                    CoopMod.Logger.LogInfo($"[InGameChat] Resolution changed from {lastScreenWidth}x{lastScreenHeight} to {Screen.width}x{Screen.height}");
                    RepositionChatButton(cachedHud);
                    lastScreenWidth = Screen.width;
                    lastScreenHeight = Screen.height;
                }
            }

            // Hide button if local co-op is enabled
            if (hasCreatedButton && chatButtonObject != null)
            {
                var localCoopManager = LocalCoopManager.Instance;
                bool shouldShow = localCoopManager == null || !localCoopManager.IsLocalCoopEnabled;

                if (chatButtonObject.activeSelf != shouldShow)
                {
                    chatButtonObject.SetActive(shouldShow);
                    CoopMod.Logger.LogInfo($"[InGameChat] Chat button visibility changed: {shouldShow}");
                }
            }

            // Manual click detection for chat button
            if (hasCreatedButton && chatButtonWidget != null && Input.GetMouseButtonDown(0))
            {
                // Re-find camera if it's null (might have been destroyed)
                if (uiCamera == null)
                {
                    uiCamera = NGUITools.FindCameraForLayer(13);
                    if (uiCamera == null) return;
                }

                Vector3 mousePos = Input.mousePosition;
                Vector3[] corners = chatButtonWidget.worldCorners;
                Vector2 min = uiCamera.WorldToScreenPoint(corners[0]);
                Vector2 max = uiCamera.WorldToScreenPoint(corners[2]);

                if (mousePos.x >= min.x && mousePos.x <= max.x &&
                    mousePos.y >= min.y && mousePos.y <= max.y)
                {
                    OnChatButtonClicked();
                    return; // Exit early since we handled the click
                }
            }

            // If button already created, nothing more to do
            if (hasCreatedButton)
                return;

            // Only check every CHECK_INTERVAL frames to minimize performance impact
            framesSinceLastCheck++;
            if (framesSinceLastCheck < CHECK_INTERVAL)
                return;

            framesSinceLastCheck = 0;

            // Check if we're in-game
            if (!IsInGame())
                return;

            // Try to create button
            TryCreateButton();
        }

        /// <summary>
        /// Check if we're actually in-game (not in menus)
        /// </summary>
        private bool IsInGame()
        {
            try
            {
                var platformType = typeof(PlatformSpecific);
                var statusField = platformType.GetField("_cur_status", BindingFlags.NonPublic | BindingFlags.Static);
                if (statusField != null)
                {
                    var status = (GameEvents.GameStatus)statusField.GetValue(null);
                    return status == GameEvents.GameStatus.InGame;
                }
            }
            catch
            {
                // Ignore errors
            }
            return false;
        }

        /// <summary>
        /// Try to create the chat button
        /// </summary>
        private void TryCreateButton()
        {
            // Find HUD
            HUD hud = Object.FindObjectOfType<HUD>();
            if (hud == null)
                return;

            CoopMod.Logger.LogInfo("[InGameChat] HUD found! Creating chat button...");
            CreateChatButton(hud);
        }

        /// <summary>
        /// Called by patch when player spawns - this is when we create the button
        /// </summary>
        public void OnPlayerSpawned()
        {
            if (hasCreatedButton)
            {
                CoopMod.Logger.LogInfo("[InGameChat] Button already created, skipping");
                return;
            }

            CoopMod.Logger.LogInfo("[InGameChat] Player spawned! Creating chat button...");

            // Find HUD immediately
            HUD hud = Object.FindObjectOfType<HUD>();
            if (hud != null)
            {
                CreateChatButton(hud);
            }
            else
            {
                CoopMod.Logger.LogWarning("[InGameChat] HUD not found on player spawn, will try in Update loop");
            }
        }

        private void CreateChatButton(HUD hud)
        {
            CoopMod.Logger.LogInfo($"[InGameChat] Creating button with HUD...");

            // Find DialogButtonGUI to clone from (red button style)
            var dialogButtons = Resources.FindObjectsOfTypeAll<DialogButtonGUI>();
            if (dialogButtons == null || dialogButtons.Length == 0)
            {
                CoopMod.Logger.LogError("[InGameChat] Cannot find DialogButtonGUI to clone!");
                return;
            }

            // Clone the button
            chatButtonObject = Object.Instantiate(dialogButtons[0].gameObject);
            chatButtonObject.name = "ChatButton_Simple";
            chatButtonObject.layer = 13; // NGUI layer

            // Parent to HUD
            chatButtonObject.transform.SetParent(hud.transform, false);

            // Cache HUD reference for resolution changes
            cachedHud = hud;
            lastScreenWidth = Screen.width;
            lastScreenHeight = Screen.height;

            // Position the button
            RepositionChatButton(hud);

            // Update button text
            UILabel buttonLabel = chatButtonObject.GetComponentInChildren<UILabel>(true);
            if (buttonLabel != null)
            {
                buttonLabel.text = "Chat";
                buttonLabel.color = Color.white;
            }

            // Remove the DialogButtonGUI component to avoid null reference errors
            var buttonGUI = chatButtonObject.GetComponent<DialogButtonGUI>();
            if (buttonGUI != null)
            {
                Object.Destroy(buttonGUI);
                CoopMod.Logger.LogInfo("[InGameChat] Removed DialogButtonGUI component");
            }

            // Remove UIButton components as they don't work with manual click detection
            var uiButtons = chatButtonObject.GetComponentsInChildren<UIButton>(true);
            foreach (var btn in uiButtons)
            {
                Object.Destroy(btn);
            }
            CoopMod.Logger.LogInfo($"[InGameChat] Removed {uiButtons.Length} UIButton components");

            // Store widget for manual click detection
            chatButtonWidget = chatButtonObject.GetComponent<UIWidget>();
            if (chatButtonWidget == null)
            {
                chatButtonWidget = chatButtonObject.GetComponentInChildren<UIWidget>();
            }

            // Get UI camera for click detection
            uiCamera = NGUITools.FindCameraForLayer(13);
            if (uiCamera != null)
            {
                CoopMod.Logger.LogInfo($"[InGameChat] UI Camera found: {uiCamera.name}");
            }

            // Make sure it's active
            chatButtonObject.SetActive(true);
            hasCreatedButton = true;

            CoopMod.Logger.LogInfo("[InGameChat] Chat button created successfully!");
        }

        /// <summary>
        /// Reposition the chat button based on current resolution
        /// Called on creation and when resolution changes
        /// </summary>
        private void RepositionChatButton(HUD hud)
        {
            if (chatButtonObject == null) return;

            // Get UIRoot to determine resolution-based position
            UIRoot uiRoot = hud.GetComponentInParent<UIRoot>();
            int manualHeight = uiRoot != null ? uiRoot.manualHeight : 800;

            // Calculate position accounting for both height and width scaling
            // NGUI coordinate system: center is (0,0), extends to +/- (width/2, height/2)
            // Reference: 2560x1600 with manualHeight=800 worked at (-620, -370)

            // Calculate the virtual width based on aspect ratio
            float scaleFactor = uiRoot != null ? (float)Screen.height / manualHeight : 2f;
            float virtualWidth = Screen.width / scaleFactor;
            float virtualHeight = manualHeight;

            // Position as offset from bottom-left corner
            float paddingX = 50f;  // Virtual units from left edge (lower = more left)
            float paddingY = 30f;  // Virtual units from bottom edge (lower = more down)

            float posX = -(virtualWidth / 2f) + paddingX;
            float posY = -(virtualHeight / 2f) + paddingY;

            chatButtonObject.transform.localPosition = new Vector3(posX, posY, 0);

            CoopMod.Logger.LogInfo($"[InGameChat] Screen: {Screen.width}x{Screen.height}, manualHeight: {manualHeight}, virtualWidth: {virtualWidth:F1}");
            CoopMod.Logger.LogInfo($"[InGameChat] Position: ({posX:F1}, {posY:F1})");
        }

        /// <summary>
        /// Called when chat button is clicked
        /// </summary>
        public void OnChatButtonClicked()
        {
            CoopMod.Logger.LogInfo("[InGameChat] Chat button clicked!");

            // Open GameGUI at the Chat tab
            var gameGUI = GUIElements.me?.game_gui;
            if (gameGUI != null)
            {
                // Get the Chat tab type (value 5)
                GameGUI.TabType chatTab = (GameGUI.TabType)5;

                if (gameGUI.is_shown)
                {
                    // If GameGUI is already open, just switch to Chat tab
                    gameGUI.OpenOrSelectTab(chatTab);
                }
                else
                {
                    // Open GameGUI at Chat tab
                    gameGUI.OpenAtTab(chatTab);
                }

                CoopMod.Logger.LogInfo("[InGameChat] Opened GameGUI at Chat tab");
            }
            else
            {
                CoopMod.Logger.LogWarning("[InGameChat] GameGUI not found!");
            }
        }

        /// <summary>
        /// Clean up when returning to menu
        /// </summary>
        public void OnReturnToMenu()
        {
            if (chatButtonObject != null)
            {
                CoopMod.Logger.LogInfo("[InGameChat] Cleaning up chat button");
                Object.Destroy(chatButtonObject);
                chatButtonObject = null;
                hasCreatedButton = false;
            }

            // Leave lobby if we're in one
            if (SteamLobbyManager.Instance != null && SteamLobbyManager.Instance.IsInLobby)
            {
                CoopMod.Logger.LogInfo("[InGameChat] Leaving lobby on return to menu");
                SteamLobbyManager.Instance.LeaveLobby();
            }

            // Clear multiplayer session flag
            if (LobbyGUI.Instance != null)
            {
                LobbyGUI.ClearMultiplayerSession();
            }

            // Reset session flags so messages show on next game start
            GraveyardKeeperCoop.Patches.PlayerPatches.ResetSessionFlag();
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using GraveyardKeeperCoop.UI;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches MainMenuGUI to add a Multiplayer button
    /// </summary>
    [HarmonyPatch(typeof(MainMenuGUI))]
    public class MainMenuPatches
    {
        private static GameObject multiplayerButton;
        private static GameObject loadGameButton;
        private static GameObject modsButton;
        private static GameObject versionLabel;
        private static GameObject dlcCaptionLogo;
        private static Texture2D dlcCaptionTexture;
        private static bool dlcCaptionLoadWarningShown = false;
        private static bool dlcCaptionGapApplied = false;
        private static float dlcCaptionAppliedGap = 0f;
        private static bool buttonCreated = false;
        private const string DLC_CAPTION_RESOURCE_NAME = "GraveyardKeeperCoop.Assets.back_from_the_grave_caption.png";
        private const int DLC_CAPTION_WIDTH = 175;
        private const float DLC_CAPTION_SOLO_TABLE_GAP = 12f;
        private const float DLC_CAPTION_STACKED_TABLE_GAP = 0f;
        private static readonly Dictionary<Transform, Vector3> originalLocalPositions = new Dictionary<Transform, Vector3>();
        private static readonly Dictionary<Transform, Vector3> originalLocalScales = new Dictionary<Transform, Vector3>();
        private static readonly Dictionary<SimpleUITable, int> originalSimpleTableOffsets = new Dictionary<SimpleUITable, int>();
        private static readonly MainMenuResolutionLayout DefaultMainMenuLayout = new MainMenuResolutionLayout(
            "Default",
            0,
            0,
            DLC_CAPTION_WIDTH,
            DLC_CAPTION_SOLO_TABLE_GAP,
            DLC_CAPTION_STACKED_TABLE_GAP,
            null,
            Vector3.zero,
            1f,
            Vector3.zero,
            1f);
        private static readonly MainMenuResolutionLayout[] MainMenuResolutionLayouts =
        {
            // Steam Deck native resolution. Keep the default art/buttons, but tighten the
            // button table and shrink the full logo/DLC stack so added entries fit.
            new MainMenuResolutionLayout(
                "SteamDeck1280x800",
                1280,
                800,
                150,
                8f,
                0f,
                4,
                new Vector3(0f, 52f, 0f),
                1f,
                new Vector3(0f, 30f, 0f),
                0.76f),
            new MainMenuResolutionLayout(
                "1366x768",
                1366,
                768,
                140,
                8f,
                0f,
                1,
                new Vector3(0f, 42f, 0f),
                1f,
                new Vector3(0f, 34f, 0f),
                0.66f),
            new MainMenuResolutionLayout(
                "1440x900",
                1440,
                900,
                145,
                8f,
                0f,
                2,
                new Vector3(0f, 28f, 0f),
                1f,
                new Vector3(0f, 8f, 0f),
                0.72f),
            new MainMenuResolutionLayout(
                "1600x900",
                1600,
                900,
                145,
                8f,
                0f,
                2,
                new Vector3(0f, 28f, 0f),
                1f,
                new Vector3(0f, 8f, 0f),
                0.72f),
            new MainMenuResolutionLayout(
                "1920x800",
                1920,
                800,
                140,
                8f,
                0f,
                1,
                new Vector3(0f, 46f, 0f),
                1f,
                new Vector3(0f, 30f, 0f),
                0.68f),
            new MainMenuResolutionLayout(
                "1920x1080",
                1920,
                1080,
                150,
                8f,
                0f,
                4,
                new Vector3(0f, 0f, 0f),
                1f,
                new Vector3(0f, -20f, 0f),
                0.76f),
            new MainMenuResolutionLayout(
                "1920x1200",
                1920,
                1200,
                150,
                8f,
                0f,
                4,
                new Vector3(0f, 0f, 0f),
                1f,
                new Vector3(0f, -20f, 0f),
                0.76f),
            new MainMenuResolutionLayout(
                "1920x1280",
                1920,
                1280,
                150,
                8f,
                0f,
                4,
                new Vector3(0f, 0f, 0f),
                1f,
                new Vector3(0f, -20f, 0f),
                0.76f),
            new MainMenuResolutionLayout(
                "1920x1440",
                1920,
                1440,
                150,
                8f,
                0f,
                4,
                new Vector3(0f, 0f, 0f),
                1f,
                new Vector3(0f, -20f, 0f),
                0.76f),
            new MainMenuResolutionLayout(
                "2048x1152",
                2048,
                1152,
                150,
                8f,
                0f,
                4,
                new Vector3(0f, 0f, 0f),
                1f,
                new Vector3(0f, -20f, 0f),
                0.76f),
            new MainMenuResolutionLayout(
                "2048x1536",
                2048,
                1536,
                150,
                8f,
                0f,
                4,
                new Vector3(0f, 0f, 0f),
                1f,
                new Vector3(0f, -20f, 0f),
                0.76f),
            new MainMenuResolutionLayout(
                "2560x1080",
                2560,
                1080,
                150,
                8f,
                0f,
                4,
                new Vector3(0f, 0f, 0f),
                1f,
                new Vector3(0f, -20f, 0f),
                0.76f),
            new MainMenuResolutionLayout(
                "1600x1200",
                1600,
                1200,
                150,
                8f,
                0f,
                4,
                new Vector3(0f, 0f, 0f),
                1f,
                new Vector3(0f, -20f, 0f),
                0.76f),
            new MainMenuResolutionLayout(
                "1680x1050",
                1680,
                1050,
                150,
                8f,
                0f,
                4,
                new Vector3(0f, 0f, 0f),
                1f,
                new Vector3(0f, -20f, 0f),
                0.76f),
            new MainMenuResolutionLayout(
                "1440x960",
                1440,
                960,
                150,
                8f,
                0f,
                4,
                new Vector3(0f, 0f, 0f),
                1f,
                new Vector3(0f, -20f, 0f),
                0.76f),
            new MainMenuResolutionLayout(
                "1440x1080",
                1440,
                1080,
                150,
                8f,
                0f,
                4,
                new Vector3(0f, 0f, 0f),
                1f,
                new Vector3(0f, -20f, 0f),
                0.76f),
            new MainMenuResolutionLayout(
                "1280x960",
                1280,
                960,
                150,
                8f,
                0f,
                4,
                new Vector3(0f, 0f, 0f),
                1f,
                new Vector3(0f, -20f, 0f),
                0.76f),
            new MainMenuResolutionLayout(
                "1280x1024",
                1280,
                1024,
                150,
                8f,
                0f,
                4,
                new Vector3(0f, 0f, 0f),
                1f,
                new Vector3(0f, -20f, 0f),
                0.76f),
            new MainMenuResolutionLayout(
                "1400x1050",
                1400,
                1050,
                150,
                8f,
                0f,
                4,
                new Vector3(0f, 0f, 0f),
                1f,
                new Vector3(0f, -20f, 0f),
                0.76f)
        };

        private sealed class MainMenuResolutionLayout
        {
            public readonly string Name;
            public readonly int ScreenWidth;
            public readonly int ScreenHeight;
            public readonly int DlcCaptionWidth;
            public readonly float DlcCaptionSoloTableGap;
            public readonly float DlcCaptionStackedTableGap;
            public readonly int? ButtonTableItemGap;
            public readonly Vector3 ButtonTableLocalOffset;
            public readonly float ButtonTableScale;
            public readonly Vector3 LogoControllerLocalOffset;
            public readonly float LogoControllerScale;

            public MainMenuResolutionLayout(
                string name,
                int screenWidth,
                int screenHeight,
                int dlcCaptionWidth,
                float dlcCaptionSoloTableGap,
                float dlcCaptionStackedTableGap,
                int? buttonTableItemGap,
                Vector3 buttonTableLocalOffset,
                float buttonTableScale,
                Vector3 logoControllerLocalOffset,
                float logoControllerScale)
            {
                Name = name;
                ScreenWidth = screenWidth;
                ScreenHeight = screenHeight;
                DlcCaptionWidth = dlcCaptionWidth;
                DlcCaptionSoloTableGap = dlcCaptionSoloTableGap;
                DlcCaptionStackedTableGap = dlcCaptionStackedTableGap;
                ButtonTableItemGap = buttonTableItemGap;
                ButtonTableLocalOffset = buttonTableLocalOffset;
                ButtonTableScale = buttonTableScale <= 0f ? 1f : buttonTableScale;
                LogoControllerLocalOffset = logoControllerLocalOffset;
                LogoControllerScale = logoControllerScale <= 0f ? 1f : logoControllerScale;
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
        
        // Flag to indicate if Load Game button was pressed (to hide New Game option)
        public static bool IsLoadGameMode { get; internal set; } = false;
        
        // Flag to indicate if Save Game button was pressed (to change New Game to New Save)
        public static bool IsSaveGameMode { get; internal set; } = false;
        
        // Flag to indicate if Mods button was pressed (to show mods instead of saves)
        public static bool IsModsMode { get; internal set; } = false;
        
        // Flag to indicate if save/load was opened from in-game (game was running)
        public static bool WasInGame { get; internal set; } = false;
        
        // Flag to indicate if we're in multiplayer save/load context (LobbyGUI)
        // When true, only show coop-prefixed saves and create coop-prefixed saves
        public static bool IsMultiplayerSaveMode { get; internal set; } = false;

        /// <summary>
        /// Returns true while the current game belongs to any multiplayer session.
        /// A hosted Steam lobby remains a multiplayer save context even when it only
        /// has one member; OnlineCoopManager intentionally does not enable until a
        /// remote player joins.
        /// </summary>
        public static bool IsMultiplayerSaveContextActive()
        {
            var lobby = GraveyardKeeperCoop.Network.SteamLobbyManager.Instance;
            var onlineCoop = GraveyardKeeperCoop.Network.OnlineCoopManager.Instance;
            var localCoop = GraveyardKeeperCoop.LocalCoop.LocalCoopManager.Instance;

            return LobbyGUI.IsMultiplayerSessionActive ||
                   (lobby != null && lobby.IsInLobby) ||
                   (onlineCoop != null && onlineCoop.IsOnlineCoopEnabled) ||
                   (localCoop != null && localCoop.IsLocalCoopEnabled);
        }

        /// <summary>
        /// The user-facing Load menu is host-only in an online lobby. Prefer the Steam
        /// lobby role because it is authoritative even before OnlineCoopManager activates
        /// (notably for a one-person hosted lobby). Lower-level host-delivered client load
        /// operations use a separate authorization path.
        /// </summary>
        public static bool CanLocalPlayerOpenLoadMenu()
        {
            var lobby = GraveyardKeeperCoop.Network.SteamLobbyManager.Instance;
            if (lobby != null && lobby.IsInLobby)
                return lobby.IsHost;

            var onlineCoop = GraveyardKeeperCoop.Network.OnlineCoopManager.Instance;
            return onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled || onlineCoop.IsHost;
        }
        
        /// <summary>
        /// Returns true if the given save slot is a multiplayer save (coop_ prefix or sidecar exists).
        /// </summary>
        public static bool IsMultiplayerSave(SaveSlotData slot)
        {
            if (slot == null) return false;
            if (string.IsNullOrEmpty(slot.filename_no_extension)) return false;
            if (slot.filename_no_extension.StartsWith("coop_", StringComparison.Ordinal))
                return true;
            // Fallback: check for coop_positions sidecar
            string sidecarPath = PlatformSpecific.GetSaveFolder() +
                                 slot.filename_no_extension + ".coop_positions.json";
            if (System.IO.File.Exists(sidecarPath))
                return true;
            return false;
        }
        
        /// <summary>
        /// After the main menu initializes, add our multiplayer button
        /// </summary>
        [HarmonyPatch("Init")]
        [HarmonyPostfix]
        public static void Init_Postfix(MainMenuGUI __instance)
        {
            // Debug logging commented out - only enable when debugging menu button issues
            // CoopMod.Logger.LogInfo("=================================");
            // CoopMod.Logger.LogInfo($"[INIT] Init_Postfix ENTRY - DLL VERSION: 2025-10-29-00:36");
            // CoopMod.Logger.LogInfo($"[INIT] buttonCreated={buttonCreated}");
            // CoopMod.Logger.LogInfo($"[INIT] multiplayerButton null? {multiplayerButton == null}");
            // CoopMod.Logger.LogInfo($"[INIT] multiplayerButton destroyed? {(multiplayerButton != null ? !multiplayerButton : true)}");\n            // CoopMod.Logger.LogInfo("=================================");
            try
            {
                // Only create the multiplayer button once (check if GameObject is actually valid, not just the reference)
                if (buttonCreated && multiplayerButton != null && multiplayerButton)
                {
                    // CoopMod.Logger.LogInfo("[INIT] Early return - button already exists and is valid");
                    ApplyMainMenuResolutionLayout(__instance);
                    return;
                }
                
                // Reset flag if GameObject was destroyed
                if (buttonCreated && (multiplayerButton == null || !multiplayerButton))
                {
                    // CoopMod.Logger.LogInfo("[INIT] Resetting flag - button was created before but GameObject is now destroyed");
                    buttonCreated = false;
                }
                
                // CoopMod.Logger.LogInfo("[INIT] Proceeding with button creation...");
                
                // Create MultiplayerSubMenuGUI, JoinGameGUI, and LobbyGUI_v2 early so they're ready when needed
                if (MultiplayerSubMenuGUI.Instance == null)
                {
                    CoopMod.Logger.LogInfo("Creating MultiplayerSubMenuGUI");
                    MultiplayerSubMenuGUI.Create(__instance);
                }
                
                if (JoinGameGUI.Instance == null)
                {
                    CoopMod.Logger.LogInfo("Creating JoinGameGUI");
                    JoinGameGUI.Create();
                }
                
                // NOTE: Don't pre-create LobbyGUI here - it will be created when needed
                // Pre-creating causes issues because DontDestroyOnLoad doesn't work on
                // child objects, and the lobby gets destroyed on scene changes
                
                // Chat button overlay (InGameChatOverlay) has been retired in favour of the
                // GYK-style ChatOverlay (src/UI/Chat/ChatOverlay.cs), which is created by
                // OnlineCoopManager when a remote player spawns. The chat tab inside the
                // inventory menu (ChatGUI) is unaffected and still works as before.
                // ---------------------------------------------------------------------
                // try
                // {
                //     if (InGameChatOverlay.Instance == null)
                //     {
                //         CoopMod.Logger.LogInfo("Creating InGameChatOverlay");
                //         InGameChatOverlay.Create();
                //     }
                // }
                // catch (System.Exception ex)
                // {
                //     CoopMod.Logger.LogError($"Failed to create InGameChatOverlay: {ex.Message}");
                //     CoopMod.Logger.LogError($"Stack trace: {ex.StackTrace}");
                // }
                
                // Find the buttons table (where all menu buttons are)
                var buttonsTable = __instance.buttons_table;
                if (buttonsTable == null)
                {
                    CoopMod.Logger.LogError("Could not find buttons_table!");
                    return;
                }
                
                // Find an existing button to clone (we'll use the Play button as template)
                Transform playButton = null;
                foreach (Transform child in buttonsTable.transform)
                {
                    if (child.name.Contains("play") || child.name.Contains("Play"))
                    {
                        playButton = child;
                        break;
                    }
                }
                
                if (playButton == null)
                {
                    CoopMod.Logger.LogWarning("Could not find Play button to clone. Trying first child...");
                    if (buttonsTable.transform.childCount > 0)
                    {
                        playButton = buttonsTable.transform.GetChild(0);
                    }
                }
                
                if (playButton == null)
                {
                    CoopMod.Logger.LogError("No buttons found to clone!");
                    return;
                }
                
                // Clone the button
                multiplayerButton = GameObject.Instantiate(playButton.gameObject, buttonsTable.transform);
                multiplayerButton.name = "btn_multiplayer";
                CoopMod.Logger.LogInfo($"Cloned button from: {playButton.name}, new name: btn_multiplayer");
                
                // Change the text - try to find all UILabels
                var labels = multiplayerButton.GetComponentsInChildren<UILabel>();
                CoopMod.Logger.LogInfo($"Found {labels.Length} UILabel components");
                
                foreach (var label in labels)
                {
                    CoopMod.Logger.LogInfo($"Label text before: '{label.text}'");
                    label.text = "Multiplayer";
                    CoopMod.Logger.LogInfo($"Label text after: '{label.text}'");
                }
                
                if (labels.Length == 0)
                {
                    CoopMod.Logger.LogWarning("No UILabel components found!");
                }
                
                // The game uses MenuItemGUI which has an EventDelegate called on_pressed
                // We need to change this EventDelegate to call our custom method
                var menuItem = multiplayerButton.GetComponent<MenuItemGUI>();
                if (menuItem != null)
                {
                    CoopMod.Logger.LogInfo("Found MenuItemGUI - initializing and changing on_pressed EventDelegate");
                    
                    // Initialize the MenuItemGUI for gamepad navigation
                    menuItem.Init(__instance);
                    
                    // Clear the existing EventDelegate (which calls OnPressedPlay)
                    menuItem.on_pressed = new EventDelegate(() =>
                    {
                        CoopMod.Logger.LogInfo("Multiplayer button pressed via EventDelegate!");
                        OnMultiplayerButtonPressed(__instance);
                    });
                    
                    CoopMod.Logger.LogInfo("EventDelegate changed successfully!");
                }
                else
                {
                    CoopMod.Logger.LogError("No MenuItemGUI component found on button!");
                }
                
                // Reposition the button (move it to appear after Play button)
                CoopMod.Logger.LogInfo("About to reposition multiplayer button with SetSiblingIndex");
                int playButtonIndex = playButton.GetSiblingIndex();
                CoopMod.Logger.LogInfo($"Play button sibling index: {playButtonIndex}");
                multiplayerButton.transform.SetSiblingIndex(playButtonIndex + 1);
                CoopMod.Logger.LogInfo("SetSiblingIndex completed");
                
                // Reposition the table
                CoopMod.Logger.LogInfo("About to call buttonsTable.Reposition()");
                buttonsTable.Reposition();
                CoopMod.Logger.LogInfo("Reposition() completed successfully");
                
                CoopMod.Logger.LogInfo("=================================");
                CoopMod.Logger.LogInfo("[INIT] MULTIPLAYER BUTTON CREATED!");
                CoopMod.Logger.LogInfo($"[INIT] Setting buttonCreated = true");
                buttonCreated = true;
                CoopMod.Logger.LogInfo($"[INIT] multiplayerButton name: {multiplayerButton.name}");
                CoopMod.Logger.LogInfo($"[INIT] multiplayerButton position: {multiplayerButton.transform.localPosition}");
                CoopMod.Logger.LogInfo("=================================");
                
                CoopMod.Logger.LogInfo("[LOAD] ========== STARTING LOAD GAME BUTTON CREATION ==========");
                
                // Now add Load Game button below Multiplayer
                CoopMod.Logger.LogInfo("[LOAD] Starting Load Game button creation...");
                CoopMod.Logger.LogInfo($"[LOAD] multiplayerButton null? {multiplayerButton == null}");
                CoopMod.Logger.LogInfo($"[LOAD] multiplayerButton destroyed? {!multiplayerButton}");
                
                if (multiplayerButton == null || !multiplayerButton)
                {
                    CoopMod.Logger.LogError("[LOAD] Cannot create Load Game button - Multiplayer button is null or destroyed!");
                    return;
                }
                
                CoopMod.Logger.LogInfo("[LOAD] Cloning multiplayer button...");
                loadGameButton = GameObject.Instantiate(multiplayerButton, multiplayerButton.transform.parent);
                CoopMod.Logger.LogInfo($"[LOAD] Clone created: {loadGameButton.name}");
                loadGameButton.name = "btn_load_game";
                CoopMod.Logger.LogInfo($"[LOAD] Renamed to: {loadGameButton.name}");
                
                // Remove LocalizedLabel component to prevent text override
                CoopMod.Logger.LogInfo("[LOAD] Removing LocalizedLabel components...");
                var localizedLabels = loadGameButton.GetComponentsInChildren<LocalizedLabel>();
                foreach (var localizedLabel in localizedLabels)
                {
                    GameObject.Destroy(localizedLabel);
                }
                CoopMod.Logger.LogInfo($"[LOAD] Removed {localizedLabels.Length} LocalizedLabel components");
                
                // Update the text
                CoopMod.Logger.LogInfo("[LOAD] Updating labels...");
                var loadLabels = loadGameButton.GetComponentsInChildren<UILabel>();
                CoopMod.Logger.LogInfo($"[LOAD] Found {loadLabels.Length} labels");
                foreach (var label in loadLabels)
                {
                    CoopMod.Logger.LogInfo($"[LOAD]   Label before: '{label.text}'");
                    label.text = "Load Game";
                    CoopMod.Logger.LogInfo($"[LOAD]   Label after: '{label.text}'");
                }
                CoopMod.Logger.LogInfo("[LOAD] Labels updated successfully");
                
                // Update the click handler
                CoopMod.Logger.LogInfo("[LOAD] Setting up click handler...");
                var loadMenuItem = loadGameButton.GetComponent<MenuItemGUI>();
                if (loadMenuItem != null)
                {
                    CoopMod.Logger.LogInfo("[LOAD] Found MenuItemGUI component - initializing for gamepad");
                    
                    // Initialize the MenuItemGUI for gamepad navigation
                    loadMenuItem.Init(__instance);
                    
                    loadMenuItem.on_pressed = new EventDelegate(() =>
                    {
                        OnLoadGameButtonPressed(__instance);
                    });
                    CoopMod.Logger.LogInfo("[LOAD] Click handler configured");
                }
                else
                {
                    CoopMod.Logger.LogError("[LOAD] MenuItemGUI component not found!");
                }
                
                // Position after multiplayer button
                CoopMod.Logger.LogInfo("[LOAD] Positioning button...");
                int mpIndex = multiplayerButton.transform.GetSiblingIndex();
                CoopMod.Logger.LogInfo($"[LOAD] Multiplayer index: {mpIndex}");
                loadGameButton.transform.SetSiblingIndex(mpIndex + 1);
                CoopMod.Logger.LogInfo($"[LOAD] Load Game positioned at index: {mpIndex + 1}");
                
                loadGameButton.SetActive(true);
                CoopMod.Logger.LogInfo($"[LOAD] Button active: {loadGameButton.activeSelf}");
                
                buttonsTable.Reposition();
                CoopMod.Logger.LogInfo("[LOAD] Table repositioned");
                
                // CRITICAL: Re-initialize gamepad navigation for the menu
                ReinitializeGamepadNavigation(__instance);
                
                CoopMod.Logger.LogInfo("=================================");
                CoopMod.Logger.LogInfo("[LOAD] ✓✓✓ LOAD GAME BUTTON CREATED! ✓✓✓");
                CoopMod.Logger.LogInfo($"[LOAD] Name: {loadGameButton.name}");
                CoopMod.Logger.LogInfo($"[LOAD] Position: {loadGameButton.transform.localPosition}");
                CoopMod.Logger.LogInfo($"[LOAD] Active: {loadGameButton.activeSelf}");
                CoopMod.Logger.LogInfo($"[LOAD] Index: {loadGameButton.transform.GetSiblingIndex()}");
                CoopMod.Logger.LogInfo("=================================");
                
                // Add Mods button after Load Game
                CoopMod.Logger.LogInfo("[MODS] ========== STARTING MODS BUTTON CREATION ==========");
                
                if (loadGameButton == null || !loadGameButton)
                {
                    CoopMod.Logger.LogError("[MODS] Cannot create Mods button - Load Game button is null or destroyed!");
                }
                else
                {
                    CoopMod.Logger.LogInfo("[MODS] Cloning load game button...");
                    modsButton = GameObject.Instantiate(loadGameButton, loadGameButton.transform.parent);
                    CoopMod.Logger.LogInfo($"[MODS] Clone created: {modsButton.name}");
                    modsButton.name = "btn_mods";
                    CoopMod.Logger.LogInfo($"[MODS] Renamed to: {modsButton.name}");
                    
                    // CRITICAL: Remove LocalizedLabel IMMEDIATELY and set text before anything else
                    CoopMod.Logger.LogInfo("[MODS] Removing LocalizedLabel components and updating text...");
                    var modsLocalizedLabels = modsButton.GetComponentsInChildren<LocalizedLabel>(true);
                    foreach (var localizedLabel in modsLocalizedLabels)
                    {
                        // Disable first, then destroy
                        localizedLabel.enabled = false;
                        UnityEngine.Object.DestroyImmediate(localizedLabel);
                    }
                    CoopMod.Logger.LogInfo($"[MODS] Removed {modsLocalizedLabels.Length} LocalizedLabel components");
                    
                    // Update text IMMEDIATELY after destroying LocalizedLabel
                    var modsLabels = modsButton.GetComponentsInChildren<UILabel>(true);
                    CoopMod.Logger.LogInfo($"[MODS] Found {modsLabels.Length} labels");
                    foreach (var label in modsLabels)
                    {
                        CoopMod.Logger.LogInfo($"[MODS]   Label before: '{label.text}'");
                        label.text = "Mods";
                        label.MarkAsChanged();
                        CoopMod.Logger.LogInfo($"[MODS]   Label after: '{label.text}'");
                    }
                    CoopMod.Logger.LogInfo("[MODS] Labels updated successfully");
                    
                    // Update the click handler
                    CoopMod.Logger.LogInfo("[MODS] Setting up click handler...");
                    var modsMenuItem = modsButton.GetComponent<MenuItemGUI>();
                    if (modsMenuItem != null)
                    {
                        CoopMod.Logger.LogInfo("[MODS] Found MenuItemGUI component - initializing for gamepad");
                        
                        // Initialize the MenuItemGUI for gamepad navigation
                        modsMenuItem.Init(__instance);
                        
                        modsMenuItem.on_pressed = new EventDelegate(() =>
                        {
                            OnModsButtonPressed(__instance);
                        });
                        CoopMod.Logger.LogInfo("[MODS] Click handler configured");
                    }
                    else
                    {
                        CoopMod.Logger.LogError("[MODS] MenuItemGUI component not found!");
                    }
                    
                    // Position after load game button
                    CoopMod.Logger.LogInfo("[MODS] Positioning button...");
                    int lgIndex = loadGameButton.transform.GetSiblingIndex();
                    CoopMod.Logger.LogInfo($"[MODS] Load Game index: {lgIndex}");
                    modsButton.transform.SetSiblingIndex(lgIndex + 1);
                    CoopMod.Logger.LogInfo($"[MODS] Mods positioned at index: {lgIndex + 1}");
                    
                    modsButton.SetActive(true);
                    CoopMod.Logger.LogInfo($"[MODS] Button active: {modsButton.activeSelf}");
                    
                    buttonsTable.Reposition();
                    CoopMod.Logger.LogInfo("[MODS] Table repositioned");

                    // Mods is cloned after the first navigation rebuild performed
                    // for Load Game. Rebuild from the final button set so its copied
                    // Up/Down links cannot bypass Load Game on the way back up.
                    ReinitializeGamepadNavigation(__instance);
                    
                    CoopMod.Logger.LogInfo("=================================");
                    CoopMod.Logger.LogInfo("[MODS] ✓✓✓ MODS BUTTON CREATED! ✓✓✓");
                    CoopMod.Logger.LogInfo($"[MODS] Name: {modsButton.name}");
                    CoopMod.Logger.LogInfo($"[MODS] Position: {modsButton.transform.localPosition}");
                    CoopMod.Logger.LogInfo($"[MODS] Active: {modsButton.activeSelf}");
                    CoopMod.Logger.LogInfo($"[MODS] Index: {modsButton.transform.GetSiblingIndex()}");
                    CoopMod.Logger.LogInfo("=================================");
                }
                
                // Create version label in bottom-left corner
                CreateVersionLabel(__instance);
                EnsureDlcCaptionLogo(__instance);
                ApplyMainMenuResolutionLayout(__instance);
                ReinitializeGamepadNavigation(__instance);
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"Error adding Multiplayer button: {ex.Message}");
                CoopMod.Logger.LogError($"Stack trace: {ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Creates the mod version label in the bottom-left corner of the main menu
        /// </summary>
        private static void CreateVersionLabel(MainMenuGUI menu)
        {
            try
            {
                // Skip if already created
                if (versionLabel != null && versionLabel)
                    return;
                
                // Create a new GameObject for the version label
                // Parent directly to menu so it hides when menu hides
                versionLabel = new GameObject("CoopModVersionLabel");
                versionLabel.transform.SetParent(menu.transform, false);
                versionLabel.layer = menu.gameObject.layer;
                
                // Add UILabel component
                var label = versionLabel.AddComponent<UILabel>();
                
                // Find a font to use (from an existing label in the menu)
                UIFont foundFont = null;
                var existingLabels = menu.GetComponentsInChildren<UILabel>(true);
                if (existingLabels.Length > 0)
                {
                    foundFont = existingLabels[0].bitmapFont;
                }
                
                if (foundFont != null)
                {
                    label.bitmapFont = foundFont;
                }
                
                // Set label properties
                label.text = PluginInfo.PLUGIN_DISPLAY_NAME;
                label.color = new Color(0.6f, 0.6f, 0.6f, 0.8f); // Light gray, slightly transparent
                label.fontSize = 16;
                label.overflowMethod = UILabel.Overflow.ResizeFreely;
                label.alignment = NGUIText.Alignment.Left;
                label.pivot = UIWidget.Pivot.BottomLeft;
                label.depth = 1000; // Make sure it's on top
                
                // Add dark outline for better readability
                label.effectStyle = UILabel.Effect.Outline;
                label.effectColor = new Color(0f, 0f, 0f, 0.8f); // Dark outline
                label.effectDistance = new Vector2(1f, 1f);
                
                // Use UIAnchor to position relative to screen corner (resolution-independent)
                var anchor = versionLabel.AddComponent<UIAnchor>();
                anchor.side = UIAnchor.Side.BottomLeft;
                anchor.pixelOffset = new Vector2(10f, 18f); // Keep clear of the bottom edge at 1080p
                
                CoopMod.Logger.LogInfo($"[VERSION] Created version label: {label.text}");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[VERSION] Failed to create version label: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Called when Load Game button is pressed
        /// </summary>
        private static void OnLoadGameButtonPressed(MainMenuGUI menu)
        {
            try
            {
                CoopMod.Logger.LogInfo("Load Game button pressed!");
                
                // Set flags - IMPORTANT: Clear all other flags to prevent conflicts
                IsLoadGameMode = true;
                IsSaveGameMode = false;
                IsModsMode = false;
                WasInGame = false; // Main menu = not in game
                IsMultiplayerSaveMode = false;
                
                CoopMod.Logger.LogInfo($"Set flags: IsLoadGameMode={IsLoadGameMode}, IsSaveGameMode={IsSaveGameMode}, IsModsMode={IsModsMode}, WasInGame={WasInGame}");
                
                // Open the native save slots menu (same as Play button)
                // This shows ALL saves, not just manual saves
                GUIElements.me.saves.Open();
                
                CoopMod.Logger.LogInfo("Opened SaveSlotsMenuGUI in Load Game mode");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"Error in OnLoadGameButtonPressed: {ex}");
            }
        }
        
        /// <summary>
        /// Called when Mods button is pressed
        /// </summary>
        private static void OnModsButtonPressed(MainMenuGUI menu)
        {
            try
            {
                CoopMod.Logger.LogInfo("Mods button pressed!");
                
                // Set flags
                IsModsMode = true;
                IsLoadGameMode = false;
                IsSaveGameMode = false;
                WasInGame = false;
                IsMultiplayerSaveMode = false;
                
                CoopMod.Logger.LogInfo($"Set flags: IsModsMode={IsModsMode}");
                
                // Open the save slots menu (we'll patch it to show mods instead)
                GUIElements.me.saves.Open();
                
                CoopMod.Logger.LogInfo("Opened SaveSlotsMenuGUI in Mods mode");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"Error in OnModsButtonPressed: {ex}");
            }
        }
        
        /// <summary>
        /// After the main menu opens, ensure our button text is correct and hide/show based on pause state
        /// </summary>
        [HarmonyPatch("Open", new Type[] { typeof(bool) })]
        [HarmonyPostfix]
        public static void Open_Postfix(MainMenuGUI __instance, bool switch_music)
        {
            CoopMod.Logger.LogInfo("=================================");
            CoopMod.Logger.LogInfo("[OPEN] Open_Postfix ENTRY - DLL VERSION: 2025-10-29-00:36");
            CoopMod.Logger.LogInfo($"[OPEN] switch_music: {switch_music}");
            CoopMod.Logger.LogInfo($"[OPEN] MainGame.game_started: {MainGame.game_started}");
            CoopMod.Logger.LogInfo("=================================");
            try
            {
                if (!MainGame.game_started)
                    ChatOverlay.Instance?.EndSession();

                // If button exists, make sure the text is still "MULTIPLAYER"
                if (multiplayerButton != null)
                {
                    // Check if this is the pause menu (game has started) or main menu (game not started)
                    bool isPauseMenu = MainGame.game_started;
                    
                    // Hide the multiplayer button in pause menu, show it in main menu
                    multiplayerButton.SetActive(!isPauseMenu);
                    
                    if (isPauseMenu)
                    {
                        CoopMod.Logger.LogInfo("Pause menu opened - hiding Multiplayer button");
                    }
                    else
                    {
                        CoopMod.Logger.LogInfo("Main menu opened - showing Multiplayer button");
                    }
                    
                    // Fix button text if needed
                    var labels = multiplayerButton.GetComponentsInChildren<UILabel>();
                    foreach (var label in labels)
                    {
                        if (label.text != "Multiplayer")
                        {
                            CoopMod.Logger.LogInfo($"Fixing button text: '{label.text}' -> 'Multiplayer'");
                            label.text = "Multiplayer";
                        }
                    }
                }
                
                // Fix Load Game button text if it exists
                if (loadGameButton != null && loadGameButton)
                {
                    var loadLabels = loadGameButton.GetComponentsInChildren<UILabel>();
                    foreach (var label in loadLabels)
                    {
                        if (label.text != "Load Game")
                        {
                            CoopMod.Logger.LogInfo($"[OPEN] Fixing Load Game button text: '{label.text}' -> 'Load Game'");
                            label.text = "Load Game";
                        }
                    }
                    
                    CoopMod.Logger.LogInfo("Main menu opened - showing Load Game button");
                }
                
                // Debug the Load Game button creation condition
                CoopMod.Logger.LogInfo($"[OPEN DEBUG] loadGameButton null? {loadGameButton == null}, destroyed? {!loadGameButton}, multiplayerButton null? {multiplayerButton == null}, destroyed? {!multiplayerButton}, game_started? {MainGame.game_started}");
                
                // Create Load Game button if it doesn't exist yet and we're in main menu (not pause menu)
                // Use Unity's null check which properly handles destroyed objects
                if ((loadGameButton == null || !loadGameButton) && multiplayerButton != null && multiplayerButton && !MainGame.game_started)
                {
                    CoopMod.Logger.LogInfo("[OPEN] Creating Load Game button...");
                    
                    // Find the buttons table
                    var buttonsTable = __instance.buttons_table;
                    
                    // Clone the multiplayer button
                    loadGameButton = GameObject.Instantiate(multiplayerButton, buttonsTable.transform);
                    loadGameButton.name = "btn_load_game";
                    
                    // Update labels
                    var loadLabels = loadGameButton.GetComponentsInChildren<UILabel>();
                    foreach (var label in loadLabels)
                    {
                        label.text = "Load Game";
                    }
                    
                    // Update click handler and initialize for gamepad
                    var loadMenuItem = loadGameButton.GetComponent<MenuItemGUI>();
                    if (loadMenuItem != null)
                    {
                        loadMenuItem.Init(__instance);
                        loadMenuItem.on_pressed = new EventDelegate(() =>
                        {
                            OnLoadGameButtonPressed(__instance);
                        });
                    }
                    
                    // Position after multiplayer button
                    loadGameButton.transform.SetSiblingIndex(multiplayerButton.transform.GetSiblingIndex() + 1);
                    buttonsTable.Reposition();
                    
                    // Reinitialize gamepad navigation
                    ReinitializeGamepadNavigation(__instance);
                    
                    CoopMod.Logger.LogInfo("[OPEN] Load Game button created successfully!");
                }
                else if (loadGameButton != null && loadGameButton)
                {
                    // Button exists, show/hide based on pause state
                    loadGameButton.SetActive(!MainGame.game_started);
                    
                    if (!MainGame.game_started)
                    {
                        CoopMod.Logger.LogInfo("Main menu opened - showing Load Game button");
                    }
                }
                
                // Show/hide version label based on pause state (only show on main menu, not in-game)
                if (versionLabel != null && versionLabel)
                {
                    versionLabel.SetActive(!MainGame.game_started);
                }

                EnsureDlcCaptionLogo(__instance);
                ApplyMainMenuResolutionLayout(__instance);
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"Error in Open_Postfix: {ex.Message}");
            }
        }

        private static void EnsureDlcCaptionLogo(MainMenuGUI menu)
        {
            try
            {
                if (menu == null)
                    return;

                bool shouldShow = !MainGame.game_started;
                if (dlcCaptionLogo != null && dlcCaptionLogo)
                {
                    dlcCaptionLogo.SetActive(shouldShow);
                    ApplyDlcCaptionLogoSize(GetActiveMainMenuLayout());
                    RepositionMainMenuLogoTable(dlcCaptionLogo.transform.parent);
                    return;
                }

                if (!shouldShow)
                    return;

                MainMenuLogoController logoController = menu.GetComponentInChildren<MainMenuLogoController>(true);
                if (logoController == null)
                    return;

                Texture2D texture = LoadDlcCaptionTexture();
                if (texture == null)
                    return;

                dlcCaptionLogo = new GameObject("BackFromTheGraveDlcCaption");
                dlcCaptionGapApplied = false;
                dlcCaptionLogo.layer = logoController.gameObject.layer;
                dlcCaptionLogo.transform.SetParent(logoController.transform, false);
                dlcCaptionLogo.transform.localScale = Vector3.one;
                dlcCaptionLogo.transform.SetAsLastSibling();

                var uiTexture = dlcCaptionLogo.AddComponent<UITexture>();
                uiTexture.mainTexture = texture;
                uiTexture.pivot = UIWidget.Pivot.Center;
                uiTexture.depth = 30;
                ApplyDlcCaptionLogoSize(GetActiveMainMenuLayout());

                RepositionMainMenuLogoTable(logoController.transform);
                CoopMod.Logger.LogInfo("[MainMenu] Added Back From The Grave DLC-style caption logo");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[MainMenu] Failed to add DLC-style caption logo: {ex.Message}");
            }
        }

        private static Texture2D LoadDlcCaptionTexture()
        {
            if (dlcCaptionTexture != null)
                return dlcCaptionTexture;

            byte[] data = null;
            using (Stream stream = typeof(MainMenuPatches).Assembly.GetManifestResourceStream(DLC_CAPTION_RESOURCE_NAME))
            {
                if (stream != null)
                {
                    using (var memory = new MemoryStream())
                    {
                        stream.CopyTo(memory);
                        data = memory.ToArray();
                    }
                }
            }

            if (data == null || data.Length == 0)
            {
                if (!dlcCaptionLoadWarningShown)
                {
                    dlcCaptionLoadWarningShown = true;
                    CoopMod.Logger.LogWarning("[MainMenu] Back From The Grave caption asset was not found in embedded resources");
                }
                return null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            texture.name = "back_from_the_grave_caption";
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            if (!texture.LoadImage(data))
            {
                UnityEngine.Object.Destroy(texture);
                if (!dlcCaptionLoadWarningShown)
                {
                    dlcCaptionLoadWarningShown = true;
                    CoopMod.Logger.LogWarning("[MainMenu] Failed to decode Back From The Grave caption texture");
                }
                return null;
            }

            dlcCaptionTexture = texture;
            return dlcCaptionTexture;
        }

        private static void RepositionMainMenuLogoTable(Transform logoControllerTransform)
        {
            if (logoControllerTransform == null)
                return;

            var simpleTable = logoControllerTransform.GetComponent<SimpleUITable>();
            if (simpleTable != null)
            {
                ResetDlcCaptionLogoGap(logoControllerTransform);
                simpleTable.Reposition();
                ApplyDlcCaptionLogoGap(logoControllerTransform);
                return;
            }

            var table = logoControllerTransform.GetComponent<UITable>();
            if (table != null)
            {
                ResetDlcCaptionLogoGap(logoControllerTransform);
                table.repositionNow = true;
                table.Reposition();
                ApplyDlcCaptionLogoGap(logoControllerTransform);
            }
        }

        private static void ApplyMainMenuResolutionLayout(MainMenuGUI menu)
        {
            try
            {
                if (menu == null)
                    return;

                MainMenuResolutionLayout layout = GetActiveMainMenuLayout();

                if (menu.buttons_table != null)
                {
                    ApplySimpleTableItemGap(menu.buttons_table, layout.ButtonTableItemGap);
                    menu.buttons_table.Reposition();
                    ApplyTransformLayout(menu.buttons_table.transform, layout.ButtonTableLocalOffset, layout.ButtonTableScale);
                }

                MainMenuLogoController logoController = menu.GetComponentInChildren<MainMenuLogoController>(true);
                if (logoController != null)
                {
                    ApplyTransformLayout(logoController.transform, layout.LogoControllerLocalOffset, layout.LogoControllerScale);
                }

                if (layout != DefaultMainMenuLayout)
                {
                    CoopMod.Logger.LogInfo(
                        $"[MainMenu] Applied layout {layout.Name} for {Screen.width}x{Screen.height}: " +
                        $"buttonsOffset={layout.ButtonTableLocalOffset}, buttonsScale={layout.ButtonTableScale}, " +
                        $"logoOffset={layout.LogoControllerLocalOffset}, logoScale={layout.LogoControllerScale}");
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[MainMenu] Failed to apply resolution layout: {ex.Message}");
            }
        }

        private static MainMenuResolutionLayout GetActiveMainMenuLayout()
        {
            int screenWidth = Screen.width;
            int screenHeight = Screen.height;

            for (int i = 0; i < MainMenuResolutionLayouts.Length; i++)
            {
                MainMenuResolutionLayout layout = MainMenuResolutionLayouts[i];
                if (layout.Matches(screenWidth, screenHeight))
                    return layout;
            }

            return DefaultMainMenuLayout;
        }

        private static void ApplySimpleTableItemGap(SimpleUITable table, int? itemGapOverride)
        {
            if (table == null)
                return;

            if (!originalSimpleTableOffsets.ContainsKey(table))
            {
                originalSimpleTableOffsets[table] = table.offset;
            }

            int desiredOffset = itemGapOverride.HasValue
                ? itemGapOverride.Value
                : originalSimpleTableOffsets[table];

            table.offset = desiredOffset;
        }

        private static void ApplyTransformLayout(Transform target, Vector3 localOffset, float scaleMultiplier)
        {
            if (target == null)
                return;

            if (!originalLocalPositions.ContainsKey(target))
            {
                originalLocalPositions[target] = target.localPosition;
            }

            if (!originalLocalScales.ContainsKey(target))
            {
                originalLocalScales[target] = target.localScale;
            }

            target.localPosition = originalLocalPositions[target] + localOffset;
            target.localScale = originalLocalScales[target] * (scaleMultiplier <= 0f ? 1f : scaleMultiplier);
        }

        private static void ApplyDlcCaptionLogoSize(MainMenuResolutionLayout layout)
        {
            if (layout == null || dlcCaptionLogo == null || !dlcCaptionLogo)
                return;

            Texture2D texture = dlcCaptionTexture;
            UITexture uiTexture = dlcCaptionLogo.GetComponent<UITexture>();
            if (texture == null || uiTexture == null)
                return;

            uiTexture.width = layout.DlcCaptionWidth;
            uiTexture.height = Mathf.RoundToInt(layout.DlcCaptionWidth * ((float)texture.height / texture.width));
        }

        private static void ResetDlcCaptionLogoGap(Transform logoControllerTransform)
        {
            if (!dlcCaptionGapApplied || logoControllerTransform == null || dlcCaptionLogo == null || !dlcCaptionLogo)
                return;

            if (dlcCaptionLogo.transform.parent != logoControllerTransform)
                return;

            float appliedGap = dlcCaptionAppliedGap;
            Vector3 shiftedPosition = dlcCaptionLogo.transform.localPosition;
            dlcCaptionLogo.transform.localPosition = new Vector3(
                shiftedPosition.x,
                shiftedPosition.y + appliedGap,
                shiftedPosition.z);
            dlcCaptionGapApplied = false;
            dlcCaptionAppliedGap = 0f;
        }

        private static void ApplyDlcCaptionLogoGap(Transform logoControllerTransform)
        {
            if (dlcCaptionGapApplied || logoControllerTransform == null || dlcCaptionLogo == null || !dlcCaptionLogo)
                return;

            if (dlcCaptionLogo.transform.parent != logoControllerTransform)
                return;

            float gap = GetDlcCaptionLogoGap();
            if (Mathf.Abs(gap) <= 0.01f)
                return;

            Vector3 tablePosition = dlcCaptionLogo.transform.localPosition;
            dlcCaptionLogo.transform.localPosition = new Vector3(
                tablePosition.x,
                tablePosition.y - gap,
                tablePosition.z);
            dlcCaptionGapApplied = true;
            dlcCaptionAppliedGap = gap;
        }

        private static float GetDlcCaptionLogoGap()
        {
            try
            {
                return DLCEngine.DLCAvailableCount() <= 1
                    ? GetActiveMainMenuLayout().DlcCaptionSoloTableGap
                    : GetActiveMainMenuLayout().DlcCaptionStackedTableGap;
            }
            catch
            {
                return GetActiveMainMenuLayout().DlcCaptionSoloTableGap;
            }
        }
        
        /// <summary>
        /// Handler when Multiplayer button is clicked
        /// </summary>
        public static void OnMultiplayerButtonPressed(MainMenuGUI mainMenu)
        {
            CoopMod.Logger.LogInfo("Multiplayer button pressed!");
            
            // Open multiplayer submenu (create if needed)
            if (MultiplayerSubMenuGUI.Instance == null)
            {
                CoopMod.Logger.LogInfo("Creating MultiplayerSubMenuGUI for the first time");
                MultiplayerSubMenuGUI.Create(mainMenu);
            }
            
            if (MultiplayerSubMenuGUI.Instance != null)
            {
                MultiplayerSubMenuGUI.Instance.Open();
            }
            else
            {
                CoopMod.Logger.LogError("Failed to create/open MultiplayerSubMenuGUI!");
            }
        }
        
        /// <summary>
        /// Reinitialize gamepad navigation after adding new menu items
        /// </summary>
        private static void ReinitializeGamepadNavigation(MainMenuGUI menu)
        {
            try
            {
                // First, clear ALL stale navigation references on all buttons
                // When buttons are cloned, they copy the original's navigation references which become invalid
                Transform buttonsRoot = menu.buttons_table != null
                    ? menu.buttons_table.transform
                    : menu.transform;
                var allMenuItems = buttonsRoot.GetComponentsInChildren<MenuItemGUI>(true);
                foreach (var menuItem in allMenuItems)
                {
                    var navItem = menuItem.GetComponent<GamepadNavigationItem>();
                    if (navItem != null)
                    {
                        navItem.SetCustomDirectionItem(null, Direction.Up);
                        navItem.SetCustomDirectionItem(null, Direction.Down);
                        navItem.SetCustomDirectionItem(null, Direction.Left);
                        navItem.SetCustomDirectionItem(null, Direction.Right);
                    }
                }
                CoopMod.Logger.LogInfo($"[MainMenu] Cleared stale navigation references on {allMenuItems.Length} items");
                
                // Update the cached items array in BaseMenuGUI
                var itemsField = typeof(BaseMenuGUI).GetField("items", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (itemsField != null)
                {
                    var newItems = buttonsRoot.GetComponentsInChildren<MenuItemGUI>(true);
                    itemsField.SetValue(menu, newItems);
                    CoopMod.Logger.LogInfo($"[MainMenu] Updated items array with {newItems.Length} items");
                    
                    // Build a list of active items in order (by Y position, descending = top to bottom)
                    var activeItems = new System.Collections.Generic.List<MenuItemGUI>();
                    foreach (var item in newItems)
                    {
                        if (item.gameObject.activeInHierarchy && item.gamepad_item != null)
                        {
                            item.gamepad_item.active = true;
                            activeItems.Add(item);
                        }
                    }
                    
                    // Preserve the base menu's visual top-to-bottom order. Newly
                    // cloned rows briefly share the same Y coordinate, so use their
                    // UITable sibling order as the deterministic tie-breaker.
                    activeItems.Sort((a, b) =>
                    {
                        float yDelta = b.transform.position.y - a.transform.position.y;
                        if (Mathf.Abs(yDelta) > 0.0001f)
                            return yDelta > 0f ? 1 : -1;

                        return a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex());
                    });
                    
                    CoopMod.Logger.LogInfo($"[MainMenu] Active items sorted by Y position:");
                    for (int i = 0; i < activeItems.Count; i++)
                    {
                        CoopMod.Logger.LogInfo($"  [{i}] {activeItems[i].name} at Y={activeItems[i].transform.position.y}");
                    }
                    
                    // Set up complete navigation chain: each item links to next/prev
                    for (int i = 0; i < activeItems.Count; i++)
                    {
                        var currentItem = activeItems[i];
                        var prevItem = (i > 0) ? activeItems[i - 1] : activeItems[activeItems.Count - 1]; // wrap to last
                        var nextItem = (i < activeItems.Count - 1) ? activeItems[i + 1] : activeItems[0]; // wrap to first
                        
                        // UP goes to previous (higher Y), DOWN goes to next (lower Y)
                        currentItem.gamepad_item.SetCustomDirectionItem(prevItem.gamepad_item, Direction.Up);
                        currentItem.gamepad_item.SetCustomDirectionItem(nextItem.gamepad_item, Direction.Down);
                    }
                    CoopMod.Logger.LogInfo("[MainMenu] Set up complete gamepad navigation chain with wrap-around");
                }
                
                // Reinitialize gamepad controller if using gamepad
                if (BaseGUI.for_gamepad && menu.gamepad_controller != null)
                {
                    menu.gamepad_controller.ReinitItems(true);
                    CoopMod.Logger.LogInfo("[MainMenu] Reinitialized gamepad controller");
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[MainMenu] Error reinitializing gamepad navigation: {ex.Message}");
            }
        }
    }
}

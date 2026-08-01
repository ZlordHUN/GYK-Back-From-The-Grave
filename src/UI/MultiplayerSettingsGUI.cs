using System.Linq;
using UnityEngine;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// Multiplayer settings menu - mirrors OptionsMenuGUI structure
    /// </summary>
    public class MultiplayerSettingsGUI : BaseMenuGUI
    {
        // Network Settings UI Elements
        // private MenuItemGUI serverPortInput;
        private MenuItemGUI maxPlayersSlider;
        private MenuItemGUI sessionVisibilitySwitcher;
        // private MenuItemGUI serverIPInput;

        // Local Co-op Settings UI Elements
        private MenuItemGUI localCoopToggle;
        private MenuItemGUI cameraModeSwitcher;

        // DLC Settings UI Elements
        private MenuItemGUI dlcStoriesToggle;
        private MenuItemGUI dlcRefugeesToggle;
        private MenuItemGUI dlcSoulsToggle;
        private MenuItemGUI cheatsToggle;

        // Dialogue to Chat Settings UI Elements
        // private MenuItemGUI dialogueEnableToggle;
        // private MenuItemGUI npcDialogueToggle;
        // private MenuItemGUI playerChoicesToggle;

        // Chat Settings UI Elements
        // private MenuItemGUI chatModeToggle;
        // private MenuItemGUI chatBubblesToggle;

        private MenuItemGUI backButton;

        private UILabel titleLabel;
        private GameObject contentPanel;
        private UIWidget backgroundWidget;
        private int defaultBackgroundHeight;
        private Vector3 defaultLocalPosition;
        private Vector3 defaultLocalScale = Vector3.one;

        // Per-row default colors captured before any disable, so re-enabling restores the look.
        private readonly System.Collections.Generic.Dictionary<MenuItemGUI, Color> rowLabelColors
            = new System.Collections.Generic.Dictionary<MenuItemGUI, Color>();
        private readonly System.Collections.Generic.Dictionary<MenuItemGUI, Color> rowValueColors
            = new System.Collections.Generic.Dictionary<MenuItemGUI, Color>();

        private static readonly Color DisabledRowColor = new Color(0.5f, 0.5f, 0.5f, 1f);

        public static MultiplayerSettingsGUI Instance { get; private set; }

        private const string DLC_STORIES_NAME = "Stranger Sins DLC";
        private const string DLC_REFUGEES_NAME = "Game of Crone DLC";
        private const string DLC_SOULS_NAME = "Better Save Soul DLC";
        private static readonly MultiplayerSettingsResolutionLayout DefaultResolutionLayout = new MultiplayerSettingsResolutionLayout(
            "Default",
            0,
            0,
            Vector3.zero,
            Vector3.one,
            60f,
            20f,
            -200f,
            140);
        private static readonly MultiplayerSettingsResolutionLayout[] ResolutionLayouts =
        {
            new MultiplayerSettingsResolutionLayout(
                "SteamDeck1280x800",
                1280,
                800,
                new Vector3(0f, 35f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                60f,
                20f,
                -175f,
                100),
            new MultiplayerSettingsResolutionLayout(
                "1366x768",
                1366,
                768,
                new Vector3(0f, 30f, 0f),
                new Vector3(0.88f, 0.88f, 1f),
                60f,
                20f,
                -175f,
                100),
            new MultiplayerSettingsResolutionLayout(
                "1440x900",
                1440,
                900,
                new Vector3(0f, 35f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                60f,
                20f,
                -175f,
                100),
            new MultiplayerSettingsResolutionLayout(
                "1600x900",
                1600,
                900,
                new Vector3(0f, 35f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                60f,
                20f,
                -175f,
                100),
            new MultiplayerSettingsResolutionLayout(
                "1920x800",
                1920,
                800,
                new Vector3(0f, 30f, 0f),
                new Vector3(0.88f, 0.88f, 1f),
                60f,
                20f,
                -175f,
                100),
            new MultiplayerSettingsResolutionLayout(
                "1920x1080",
                1920,
                1080,
                new Vector3(0f, 35f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                60f,
                20f,
                -175f,
                100),
            new MultiplayerSettingsResolutionLayout(
                "1920x1200",
                1920,
                1200,
                new Vector3(0f, 35f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                60f,
                20f,
                -175f,
                100),
            new MultiplayerSettingsResolutionLayout(
                "1920x1280",
                1920,
                1280,
                new Vector3(0f, 35f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                60f,
                20f,
                -175f,
                100),
            new MultiplayerSettingsResolutionLayout(
                "1920x1440",
                1920,
                1440,
                new Vector3(0f, 35f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                60f,
                20f,
                -175f,
                100),
            new MultiplayerSettingsResolutionLayout(
                "2048x1152",
                2048,
                1152,
                new Vector3(0f, 35f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                60f,
                20f,
                -175f,
                100),
            new MultiplayerSettingsResolutionLayout(
                "2048x1536",
                2048,
                1536,
                new Vector3(0f, 35f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                60f,
                20f,
                -175f,
                100),
            new MultiplayerSettingsResolutionLayout(
                "2560x1080",
                2560,
                1080,
                new Vector3(0f, 35f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                60f,
                20f,
                -175f,
                100),
            new MultiplayerSettingsResolutionLayout(
                "1600x1200",
                1600,
                1200,
                new Vector3(0f, 35f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                60f,
                20f,
                -175f,
                100),
            new MultiplayerSettingsResolutionLayout(
                "1680x1050",
                1680,
                1050,
                new Vector3(0f, 35f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                60f,
                20f,
                -175f,
                100),
            new MultiplayerSettingsResolutionLayout(
                "1440x960",
                1440,
                960,
                new Vector3(0f, 35f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                60f,
                20f,
                -175f,
                100),
            new MultiplayerSettingsResolutionLayout(
                "1440x1080",
                1440,
                1080,
                new Vector3(0f, 35f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                60f,
                20f,
                -175f,
                100),
            new MultiplayerSettingsResolutionLayout(
                "1280x960",
                1280,
                960,
                new Vector3(0f, 35f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                60f,
                20f,
                -175f,
                100),
            new MultiplayerSettingsResolutionLayout(
                "1280x1024",
                1280,
                1024,
                new Vector3(0f, 35f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                60f,
                20f,
                -175f,
                100),
            new MultiplayerSettingsResolutionLayout(
                "1400x1050",
                1400,
                1050,
                new Vector3(0f, 35f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                60f,
                20f,
                -175f,
                100)
        };

        private sealed class MultiplayerSettingsResolutionLayout
        {
            public readonly string Name;
            public readonly int ScreenWidth;
            public readonly int ScreenHeight;
            public readonly Vector3 RootOffset;
            public readonly Vector3 RootScale;
            public readonly float FirstItemY;
            public readonly float ItemSpacingY;
            public readonly float BackButtonY;
            public readonly int BackgroundHeightAdd;

            public MultiplayerSettingsResolutionLayout(
                string name,
                int screenWidth,
                int screenHeight,
                Vector3 rootOffset,
                Vector3 rootScale,
                float firstItemY,
                float itemSpacingY,
                float backButtonY,
                int backgroundHeightAdd)
            {
                Name = name;
                ScreenWidth = screenWidth;
                ScreenHeight = screenHeight;
                RootOffset = rootOffset;
                RootScale = rootScale;
                FirstItemY = firstItemY;
                ItemSpacingY = itemSpacingY;
                BackButtonY = backButtonY;
                BackgroundHeightAdd = backgroundHeightAdd;
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

        public static void CreateInstance()
        {
            if (Instance != null)
            {
                CoopMod.Logger.LogInfo("MultiplayerSettingsGUI already exists");
                return;
            }

            CoopMod.Logger.LogInfo("Creating MultiplayerSettingsGUI...");

            // Find the OptionsMenuGUI to use as a template
            var optionsMenu = Object.FindObjectOfType<OptionsMenuGUI>(true);
            if (optionsMenu == null)
            {
                CoopMod.Logger.LogError("Could not find OptionsMenuGUI to clone!");
                return;
            }

            // Get the language_switcher as our template (it has SimpleOptionsSwitcher)
            GameObject optionTemplate = null;
            if (optionsMenu.language_switcher != null)
            {
                optionTemplate = optionsMenu.language_switcher.gameObject;
                CoopMod.Logger.LogInfo($"Using language_switcher as template: {optionTemplate.name}");
            }
            else if (optionsMenu.screen_mode_switcher != null)
            {
                optionTemplate = optionsMenu.screen_mode_switcher.gameObject;
                CoopMod.Logger.LogInfo($"Using screen_mode_switcher as template: {optionTemplate.name}");
            }

            if (optionTemplate == null)
            {
                CoopMod.Logger.LogError("Could not find option switcher template!");
                return;
            }

            // Clone the options menu
            GameObject menuObj = Object.Instantiate(optionsMenu.gameObject);
            menuObj.name = "MultiplayerSettingsGUI";

            // Parent it to the same parent as OptionsMenuGUI to maintain UI hierarchy
            if (optionsMenu.transform.parent != null)
            {
                menuObj.transform.SetParent(optionsMenu.transform.parent, false);
                CoopMod.Logger.LogInfo($"Parented to: {optionsMenu.transform.parent.name}");
            }
            else
            {
                CoopMod.Logger.LogWarning("OptionsMenuGUI has no parent, keeping cloned menu unparented");
            }

            // Remove the OptionsMenuGUI component
            var optionsMenuComponent = menuObj.GetComponent<OptionsMenuGUI>();
            if (optionsMenuComponent != null)
            {
                Object.DestroyImmediate(optionsMenuComponent);
            }

            // Add our component
            Instance = menuObj.AddComponent<MultiplayerSettingsGUI>();
            Instance.InitializeFromClone(menuObj, optionTemplate);

            // Initialize BaseMenuGUI only after the replacement rows exist. This
            // populates the inherited controller reference and initializes the cloned
            // Back row, which is otherwise left with a null gamepad_item.
            var navigationController = menuObj.GetComponent<GamepadNavigationController>();
            if (navigationController == null)
            {
                navigationController = menuObj.AddComponent<GamepadNavigationController>();
            }
            navigationController.auto_select = true;
            if (navigationController.vertical_settings == null)
                navigationController.vertical_settings = new GamepadNavigationSettings();
            if (navigationController.horizontal_settings == null)
                navigationController.horizontal_settings = new GamepadNavigationSettings();

            Instance.Init();
            Instance.RefreshConditionalRowStates();
            Instance.ConfigureGamepadNavigation();

            CoopMod.Logger.LogInfo("MultiplayerSettingsGUI created successfully!");
        }

        private void LogHierarchy(Transform t, int depth)
        {
            if (depth > 3) return; // Limit depth to avoid spam
            string indent = new string(' ', depth * 2);
            CoopMod.Logger.LogInfo($"{indent}{t.name} pos={t.localPosition}");

            foreach (Transform child in t)
            {
                LogHierarchy(child, depth + 1);
            }
        }

        private void InitializeFromClone(GameObject clonedMenu, GameObject optionTemplate)
        {
            CoopMod.Logger.LogInfo("Initializing MultiplayerSettingsGUI from cloned OptionsMenuGUI...");
            defaultLocalPosition = clonedMenu.transform.localPosition;
            defaultLocalScale = clonedMenu.transform.localScale;

            CoopMod.Logger.LogInfo("=== OPTIONS MENU HIERARCHY ===");
            LogHierarchy(clonedMenu.transform, 0);

            // DIAGNOSTIC: Find and inspect the ORIGINAL OptionsMenuGUI before we modify anything
            var originalOptionsMenu = Object.FindObjectOfType<OptionsMenuGUI>(true);
            if (originalOptionsMenu != null)
            {
                CoopMod.Logger.LogInfo("=== INSPECTING ORIGINAL OptionsMenuGUI ===");

                // Find all UI2DSprites in the original
                var originalSprites = originalOptionsMenu.GetComponentsInChildren<UI2DSprite>(true);
                CoopMod.Logger.LogInfo($"Found {originalSprites.Length} UI2DSprite components in original OptionsMenuGUI");
                foreach (var sprite in originalSprites)
                {
                    CoopMod.Logger.LogInfo($"  Sprite: '{sprite.name}' | Parent: '{(sprite.transform.parent ? sprite.transform.parent.name : "NULL")}' | Texture: {(sprite.mainTexture ? sprite.mainTexture.name : "NULL")} | Size: {sprite.width}x{sprite.height} | Depth: {sprite.depth}");
                }

                // Find all labels
                var originalLabels = originalOptionsMenu.GetComponentsInChildren<UILabel>(true);
                CoopMod.Logger.LogInfo($"Found {originalLabels.Length} UILabel components in original OptionsMenuGUI");
                foreach (var lbl in originalLabels)
                {
                    if (lbl.name.ToLower().Contains("header") || lbl.name.ToLower().Contains("title"))
                    {
                        CoopMod.Logger.LogInfo($"  HEADER Label: '{lbl.name}' | Text: '{lbl.text}' | Parent: '{(lbl.transform.parent ? lbl.transform.parent.name : "NULL")}' | Depth: {lbl.depth} | Pos: {lbl.transform.localPosition}");
                    }
                }

                CoopMod.Logger.LogInfo("=== END ORIGINAL OptionsMenuGUI INSPECTION ===");
            }

            // Find ALL UILabels and log them to find where "Options" text is coming from
            CoopMod.Logger.LogInfo("=== SEARCHING FOR ALL LABELS IN CLONED MENU ===");
            var allLabels = clonedMenu.GetComponentsInChildren<UILabel>(true);
            foreach (var lbl in allLabels)
            {
                CoopMod.Logger.LogInfo($"Label: '{lbl.name}' | Text: '{lbl.text}' | Parent: '{(lbl.transform.parent ? lbl.transform.parent.name : "NULL")}' | Enabled: {lbl.enabled} | Depth: {lbl.depth}");
            }
            CoopMod.Logger.LogInfo("=== END LABEL SEARCH ===");

            // Also log all UI2DSprites in the cloned menu
            CoopMod.Logger.LogInfo("=== SEARCHING FOR ALL UI2DSPRITES IN CLONED MENU ===");
            var allSprites = clonedMenu.GetComponentsInChildren<UI2DSprite>(true);
            CoopMod.Logger.LogInfo($"Found {allSprites.Length} UI2DSprite components");
            foreach (var sprite in allSprites)
            {
                CoopMod.Logger.LogInfo($"Sprite: '{sprite.name}' | Parent: '{(sprite.transform.parent ? sprite.transform.parent.name : "NULL")}' | Texture: {(sprite.mainTexture ? sprite.mainTexture.name : "NULL")} | Size: {sprite.width}x{sprite.height} | Depth: {sprite.depth} | Enabled: {sprite.enabled}");
            }
            CoopMod.Logger.LogInfo("=== END SPRITE SEARCH ===");

            // Find the title label and update it
            var labels = clonedMenu.GetComponentsInChildren<UILabel>(true);

            // FIRST: Find and disable ANY label that says "Options" or "Pause"
            foreach (var label in labels)
            {
                if (label.text == "Options" || label.text == "Pause")
                {
                    CoopMod.Logger.LogInfo($"FOUND UNWANTED LABEL: '{label.name}' with text '{label.text}' at depth {label.depth}, parent: {label.transform.parent?.name ?? "NULL"}");
                    label.text = "Settings";
                    label.depth = 200;
                    CoopMod.Logger.LogInfo($"Changed to 'Settings' at depth 200");
                }
            }

            // THEN: Find the header label specifically
            foreach (var label in labels)
            {
                if (label.name.ToLower().Contains("title") || label.name.ToLower().Contains("header"))
                {
                    titleLabel = label;
                    CoopMod.Logger.LogInfo($"Found title label: '{label.name}', current text: '{label.text}'");

                    // NOTE: The 'back' sprite (child of header label) has "Options" text BAKED into the texture image.
                    // This text is part of the image file itself and CANNOT be changed at runtime.
                    // If we enable this sprite, it will always show "Options" regardless of what we set titleLabel.text to.
                    // Leaving it enabled per user request, even though it says "Options".
                    var headerLabelTransform = label.transform;
                    var backgroundSprite = headerLabelTransform.Find("back")?.GetComponent<UI2DSprite>();
                    if (backgroundSprite != null)
                    {
                        CoopMod.Logger.LogInfo($"Keeping 'back' sprite enabled (will show 'Options' baked in texture): texture={backgroundSprite.sprite2D?.name ?? "NULL"}, size={backgroundSprite.width}x{backgroundSprite.height}");
                        // backgroundSprite.enabled = true; // Already enabled by default
                    }

                    titleLabel.text = "Settings";
                    titleLabel.depth = 200;
                    titleLabel.MarkAsChanged();

                    CoopMod.Logger.LogInfo($"Updated title label text to: '{titleLabel.text}', depth: {titleLabel.depth}");
                    break;
                }
            }

            // Find all MenuItemGUI elements (sliders/inputs from options menu)
            var menuItems = clonedMenu.GetComponentsInChildren<MenuItemGUI>(true);
            CoopMod.Logger.LogInfo($"Found {menuItems.Length} MenuItemGUI elements in cloned menu");

            // CRITICAL: Remove ALL SmartSlider and UIProgressBar components from the cloned menu
            // These cause NullReferenceExceptions when their Start() methods run
            // Use DestroyImmediate to remove them before they can initialize
            var allSmartSliders = clonedMenu.GetComponentsInChildren<SmartSlider>(true);
            foreach (var slider in allSmartSliders)
            {
                Object.DestroyImmediate(slider);
            }
            CoopMod.Logger.LogInfo($"Removed {allSmartSliders.Length} SmartSlider components from cloned menu");

            var allProgressBars = clonedMenu.GetComponentsInChildren<UIProgressBar>(true);
            foreach (var bar in allProgressBars)
            {
                Object.DestroyImmediate(bar);
            }
            CoopMod.Logger.LogInfo($"Removed {allProgressBars.Length} UIProgressBar components from cloned menu");

            // Find the ORIGINAL content panel from OptionsMenuGUI instead of creating our own
            // Look for a panel/table that contains the menu items
            // Get content table - this is where original menu items live
            Transform contentTransform = null;
            foreach (var item in menuItems)
            {
                if (item != null && item.transform.parent != null)
                {
                    contentTransform = item.transform.parent;
                    CoopMod.Logger.LogInfo($"Found content table from menu item: {contentTransform.name}");
                    break;
                }
            }

            if (contentTransform == null)
            {
                CoopMod.Logger.LogError("Could not find content table transform!");
                return;
            }

            // DON'T use UITable - just use the content table directly as parent
            // This is how the original OptionsMenuGUI works
            contentPanel = contentTransform.gameObject;
            CoopMod.Logger.LogInfo($"Using content table as parent: {contentPanel.name}");

            // DESTROY all existing settings items IMMEDIATELY (not queued)
            // We need to destroy them all before creating new ones
            var itemsToDestroy = new System.Collections.Generic.List<GameObject>();
            foreach (var item in menuItems)
            {
                if (item != null && item.gameObject != null)
                {
                    itemsToDestroy.Add(item.gameObject);
                }
            }

            // Find and CLONE the back button template BEFORE destroying anything
            MenuItemGUI backButtonTemplate = null;
            if (contentPanel != null)
            {
                foreach (Transform child in contentPanel.transform)
                {
                    if (child != null && child.gameObject != null && child.name == "back")
                    {
                        var originalBack = child.GetComponent<MenuItemGUI>();
                        if (originalBack != null)
                        {
                            // Clone it NOW before destruction
                            var clonedBackObj = Object.Instantiate(originalBack.gameObject);
                            clonedBackObj.name = "back_template_clone";
                            clonedBackObj.SetActive(false); // Keep it hidden
                            Object.DontDestroyOnLoad(clonedBackObj); // Protect it
                            backButtonTemplate = clonedBackObj.GetComponent<MenuItemGUI>();
                            CoopMod.Logger.LogInfo($"Cloned back button template before destruction");
                        }
                        break;
                    }
                }
            }

            // Now destroy all child objects (including original back button)
            if (contentPanel != null)
            {
                foreach (Transform child in contentPanel.transform)
                {
                    if (child != null && child.gameObject != null)
                    {
                        itemsToDestroy.Add(child.gameObject);
                    }
                }
            }

            // Destroy all collected items
            foreach (var obj in itemsToDestroy)
            {
                Object.DestroyImmediate(obj);
            }
            CoopMod.Logger.LogInfo($"Destroyed {itemsToDestroy.Count} old items from cloned menu IMMEDIATELY");

            // Create our multiplayer settings using the option template and back button template
            if (optionTemplate != null)
            {
                CreateMultiplayerSettings(optionTemplate, backButtonTemplate);
            }
        }

        private void CreateMultiplayerSettings(GameObject template, MenuItemGUI backTemplate)
        {
            CoopMod.Logger.LogInfo("Creating multiplayer settings from template...");

            /*
            // Server Port - hidden for now; Steam lobby flow does not expose manual ports.
            serverPortInput = CreateSettingItem(template, "Server Port", SettingType.Options);
            if (serverPortInput != null)
            {
                string[] portOptions = new string[] { "7777", "25565", "27015", "7778", "8080" };
                int currentIndex = System.Array.IndexOf(portOptions, ModConfig.ServerPort.Value.ToString());
                if (currentIndex == -1) currentIndex = 0;

                SetupOptions(serverPortInput, currentIndex, portOptions, (index, label) =>
                {
                    int port = int.Parse(portOptions[index]);
                    ModConfig.ServerPort.Value = port;
                    label.text = portOptions[index];
                    CoopMod.Logger.LogInfo($"Server port changed to: {port}");
                });
                // Re-apply left label styling after SetupOptions (which may have initialized internal labels)
                ApplyLabelStyling(serverPortInput, "Server Port");
            }
            */

            // Max Players - Use option switcher
            maxPlayersSlider = CreateSettingItem(template, "Max Players", SettingType.Options);
            if (maxPlayersSlider != null)
            {
                string[] playerOptions = new string[]
                {
                    "2",
                    "3 (Experimental)",
                    "4 (Experimental)"
                };
                int currentIndex = ModConfig.MaxPlayers.Value - 2; // 2 players = index 0
                if (currentIndex < 0) currentIndex = 0;
                if (currentIndex > 2) currentIndex = 2;

                SetupOptions(maxPlayersSlider, currentIndex, playerOptions, (index, label) =>
                {
                    int players = index + 2; // index 0 = 2 players
                    ModConfig.MaxPlayers.Value = players;
                    label.text = playerOptions[index];
                    CoopMod.Logger.LogInfo($"Max players changed to: {players}");
                });
                // Re-apply left label styling after SetupOptions
                ApplyLabelStyling(maxPlayersSlider, "Max Players");
            }

            // Session Visibility - Controls how the hosted Steam lobby is advertised.
            sessionVisibilitySwitcher = CreateSettingItem(template, "Session Visibility", SettingType.Options);
            if (sessionVisibilitySwitcher != null)
            {
                string[] visibilityOptions = new string[] { "Public", "Friends", "Private" };
                int currentIndex = (int)ModConfig.HostedSessionVisibility.Value;
                if (currentIndex < 0) currentIndex = 1;
                if (currentIndex >= visibilityOptions.Length) currentIndex = 1;

                SetupOptions(sessionVisibilitySwitcher, currentIndex, visibilityOptions, (index, label) =>
                {
                    var visibility = (ModConfig.SessionVisibility)index;
                    ModConfig.HostedSessionVisibility.Value = visibility;
                    label.text = visibilityOptions[index];
                    CoopMod.Logger.LogInfo($"Session Visibility set to: {visibility}");
                });
                ApplyLabelStyling(sessionVisibilitySwitcher, "Session Visibility");
            }

            // ===== Local Co-op Settings =====

            // Local Co-op Enable/Disable Toggle
            localCoopToggle = CreateSettingItem(template, "Local Co-op", SettingType.Options);
            if (localCoopToggle != null)
            {
                string[] coopOptions = new string[] { "Disabled", "Enabled" };
                int currentIndex = ModConfig.EnableLocalCoop.Value ? 1 : 0;

                SetupOptions(localCoopToggle, currentIndex, coopOptions, (index, label) =>
                {
                    bool enabled = index == 1;
                    ModConfig.EnableLocalCoop.Value = enabled;
                    label.text = coopOptions[index];
                    CoopMod.Logger.LogInfo($"Local Co-op {(enabled ? "enabled" : "disabled")}");

                    // Camera Mode only applies when Local Co-op is on; flip its enabled state
                    // immediately so the row visibly locks/unlocks as the user toggles.
                    SetOptionRowEnabled(cameraModeSwitcher, enabled);

                    if (enabled)
                    {
                        CoopMod.Logger.LogInfo("Note: Local co-op will activate on next game load");
                    }
                });
                ApplyLabelStyling(localCoopToggle, "Local Co-op");
            }

            // Camera Mode Switcher
            cameraModeSwitcher = CreateSettingItem(template, "Camera Mode", SettingType.Options);
            if (cameraModeSwitcher != null)
            {
                string[] cameraModes = new string[] { "Shared", "Split Screen (N/A)" };
                int currentCameraMode = ModConfig.LocalCoopCameraMode.Value == ModConfig.CameraMode.Shared ? 0 : 1;

                SetupOptions(cameraModeSwitcher, currentCameraMode, cameraModes, (index, label) =>
                {
                    if (index == 0) // Shared mode
                    {
                        ModConfig.LocalCoopCameraMode.Value = ModConfig.CameraMode.Shared;
                        label.text = cameraModes[0];
                        CoopMod.Logger.LogInfo("Camera Mode set to: Shared (single camera follows both players)");
                    }
                    else // Split screen not implemented
                    {
                        ModConfig.LocalCoopCameraMode.Value = ModConfig.CameraMode.Shared;
                        label.text = cameraModes[0];
                        CoopMod.Logger.LogInfo("Split Screen mode not implemented yet - staying on Shared mode");
                    }
                });
                ApplyLabelStyling(cameraModeSwitcher, "Camera Mode");
            }

            // ===== DLC Settings =====

            dlcStoriesToggle = CreateSettingItem(template, DLC_STORIES_NAME, SettingType.Options);
            if (dlcStoriesToggle != null)
            {
                bool dlcInstalled = DLCEngine.IsDLCAvailable(DLCEngine.DLCVersion.Stories);
                if (!dlcInstalled && ModConfig.EnableDLCStories.Value)
                    ModConfig.EnableDLCStories.Value = false;
                string[] storiesOptions = dlcInstalled ? new string[] { "Disabled", "Enabled" } : new string[] { "Not Installed" };
                int currentIndex = dlcInstalled && ModConfig.EnableDLCStories.Value ? 1 : 0;

                SetupOptions(dlcStoriesToggle, currentIndex, storiesOptions, (index, label) =>
                {
                    if (!dlcInstalled)
                    {
                        ModConfig.EnableDLCStories.Value = false;
                        label.text = storiesOptions[0];
                        GraveyardKeeperCoop.Network.SteamLobbyManager.Instance?.RefreshLobbyDLCRequirements();
                        return;
                    }

                    bool enabled = index == 1;
                    ModConfig.EnableDLCStories.Value = enabled;
                    label.text = storiesOptions[index];
                    GraveyardKeeperCoop.Network.SteamLobbyManager.Instance?.RefreshLobbyDLCRequirements();
                    CoopMod.Logger.LogInfo($"Stranger Sins DLC sync {(enabled ? "enabled" : "disabled")}");
                });
                ApplyDLCLabelStyling(dlcStoriesToggle, DLC_STORIES_NAME, dlcInstalled);
            }

            dlcRefugeesToggle = CreateSettingItem(template, DLC_REFUGEES_NAME, SettingType.Options);
            if (dlcRefugeesToggle != null)
            {
                bool dlcInstalled = DLCEngine.IsDLCAvailable(DLCEngine.DLCVersion.Refugees);
                if (!dlcInstalled && ModConfig.EnableDLCRefugees.Value)
                    ModConfig.EnableDLCRefugees.Value = false;
                string[] refugeesOptions = dlcInstalled ? new string[] { "Disabled", "Enabled" } : new string[] { "Not Installed" };
                int currentIndex = dlcInstalled && ModConfig.EnableDLCRefugees.Value ? 1 : 0;

                SetupOptions(dlcRefugeesToggle, currentIndex, refugeesOptions, (index, label) =>
                {
                    if (!dlcInstalled)
                    {
                        ModConfig.EnableDLCRefugees.Value = false;
                        label.text = refugeesOptions[0];
                        GraveyardKeeperCoop.Network.SteamLobbyManager.Instance?.RefreshLobbyDLCRequirements();
                        return;
                    }

                    bool enabled = index == 1;
                    ModConfig.EnableDLCRefugees.Value = enabled;
                    label.text = refugeesOptions[index];
                    GraveyardKeeperCoop.Network.SteamLobbyManager.Instance?.RefreshLobbyDLCRequirements();
                    CoopMod.Logger.LogInfo($"Game of Crone DLC sync {(enabled ? "enabled" : "disabled")}");
                });
                ApplyDLCLabelStyling(dlcRefugeesToggle, DLC_REFUGEES_NAME, dlcInstalled);
            }

            dlcSoulsToggle = CreateSettingItem(template, DLC_SOULS_NAME, SettingType.Options);
            if (dlcSoulsToggle != null)
            {
                bool dlcInstalled = DLCEngine.IsDLCAvailable(DLCEngine.DLCVersion.Souls);
                if (!dlcInstalled && ModConfig.EnableDLCSouls.Value)
                    ModConfig.EnableDLCSouls.Value = false;
                string[] soulsOptions = dlcInstalled ? new string[] { "Disabled", "Enabled" } : new string[] { "Not Installed" };
                int currentIndex = dlcInstalled && ModConfig.EnableDLCSouls.Value ? 1 : 0;

                SetupOptions(dlcSoulsToggle, currentIndex, soulsOptions, (index, label) =>
                {
                    if (!dlcInstalled)
                    {
                        ModConfig.EnableDLCSouls.Value = false;
                        label.text = soulsOptions[0];
                        GraveyardKeeperCoop.Network.SteamLobbyManager.Instance?.RefreshLobbyDLCRequirements();
                        return;
                    }

                    bool enabled = index == 1;
                    ModConfig.EnableDLCSouls.Value = enabled;
                    label.text = soulsOptions[index];
                    GraveyardKeeperCoop.Network.SteamLobbyManager.Instance?.RefreshLobbyDLCRequirements();
                    CoopMod.Logger.LogInfo($"Better Save Soul DLC sync {(enabled ? "enabled" : "disabled")}");
                });
                ApplyDLCLabelStyling(dlcSoulsToggle, DLC_SOULS_NAME, dlcInstalled);
            }

            // ===== Cheats Settings =====

            cheatsToggle = CreateSettingItem(template, "Enable Cheats", SettingType.Options);
            if (cheatsToggle != null)
            {
                string[] cheatOptions = new string[] { "Disabled", "Enabled" };
                int currentIndex = ModConfig.EnableCheats.Value ? 1 : 0;

                SetupOptions(cheatsToggle, currentIndex, cheatOptions, (index, label) =>
                {
                    bool enabled = index == 1;
                    ModConfig.EnableCheats.Value = enabled;
                    label.text = cheatOptions[index];
                    CoopMod.Logger.LogInfo($"Cheats {(enabled ? "enabled" : "disabled")}");
                });
                ApplyLabelStyling(cheatsToggle, "Enable Cheats");
            }

            // ===== Dialogue to Chat Settings =====

            /*
            // Enable Dialogue to Chat Toggle - hidden while dialogue chat is hardcoded enabled.
            dialogueEnableToggle = CreateSettingItem(template, "Dialogue to Chat", SettingType.Options);
            if (dialogueEnableToggle != null)
            {
                string[] dialogueOptions = new string[] { "Disabled", "Enabled" };
                int currentIndex = ModConfig.EnableDialogueToChat.Value ? 1 : 0;

                SetupOptions(dialogueEnableToggle, currentIndex, dialogueOptions, (index, label) =>
                {
                    bool enabled = index == 1;
                    ModConfig.EnableDialogueToChat.Value = enabled;
                    label.text = dialogueOptions[index];
                    CoopMod.Logger.LogInfo($"Dialogue to Chat {(enabled ? "enabled" : "disabled")}");
                });
                ApplyLabelStyling(dialogueEnableToggle, "Dialogue to Chat");
            }

            // Show NPC Dialogue Toggle - hidden while NPC dialogue capture is hardcoded enabled.
            npcDialogueToggle = CreateSettingItem(template, "Show NPC Dialogue", SettingType.Options);
            if (npcDialogueToggle != null)
            {
                string[] npcOptions = new string[] { "Hidden", "Shown" };
                int currentIndex = ModConfig.ShowNPCDialogue.Value ? 1 : 0;

                SetupOptions(npcDialogueToggle, currentIndex, npcOptions, (index, label) =>
                {
                    bool enabled = index == 1;
                    ModConfig.ShowNPCDialogue.Value = enabled;
                    label.text = npcOptions[index];
                    CoopMod.Logger.LogInfo($"Show NPC Dialogue {(enabled ? "enabled" : "disabled")}");
                });
                ApplyLabelStyling(npcDialogueToggle, "Show NPC Dialogue");
            }

            // Show Player Choices Toggle - hidden while player choice capture is hardcoded enabled.
            playerChoicesToggle = CreateSettingItem(template, "Show Player Choices", SettingType.Options);
            if (playerChoicesToggle != null)
            {
                string[] choicesOptions = new string[] { "Hidden", "Shown" };
                int currentIndex = ModConfig.ShowPlayerChoices.Value ? 1 : 0;

                SetupOptions(playerChoicesToggle, currentIndex, choicesOptions, (index, label) =>
                {
                    bool enabled = index == 1;
                    ModConfig.ShowPlayerChoices.Value = enabled;
                    label.text = choicesOptions[index];
                    CoopMod.Logger.LogInfo($"Show Player Choices {(enabled ? "enabled" : "disabled")}");
                });
                ApplyLabelStyling(playerChoicesToggle, "Show Player Choices");
            }
            */

            // ===== Chat Settings =====

            /*
            // Chat Mode Toggle (Overlay vs Button) - hidden for now; overlay is the current chat path.
            chatModeToggle = CreateSettingItem(template, "Chat Mode", SettingType.Options);
            if (chatModeToggle != null)
            {
                string[] chatModeOptions = new string[] { "Overlay", "Button" };
                int currentIndex = ModConfig.InGameChatMode.Value == ModConfig.ChatMode.Overlay ? 0 : 1;

                SetupOptions(chatModeToggle, currentIndex, chatModeOptions, (index, label) =>
                {
                    var mode = index == 0 ? ModConfig.ChatMode.Overlay : ModConfig.ChatMode.Button;
                    ModConfig.InGameChatMode.Value = mode;
                    label.text = chatModeOptions[index];
                    CoopMod.Logger.LogInfo($"Chat Mode set to: {mode}");
                });
                ApplyLabelStyling(chatModeToggle, "Chat Mode");
            }

            // Chat Bubbles Toggle - hidden while chat bubbles are hardcoded enabled.
            chatBubblesToggle = CreateSettingItem(template, "Chat Bubbles", SettingType.Options);
            if (chatBubblesToggle != null)
            {
                string[] bubblesOptions = new string[] { "Disabled", "Enabled" };
                int currentIndex = ModConfig.ShowChatBubbles.Value ? 1 : 0;

                SetupOptions(chatBubblesToggle, currentIndex, bubblesOptions, (index, label) =>
                {
                    bool enabled = index == 1;
                    ModConfig.ShowChatBubbles.Value = enabled;
                    label.text = bubblesOptions[index];
                    CoopMod.Logger.LogInfo($"Chat Bubbles {(enabled ? "enabled" : "disabled")}");
                });
                ApplyLabelStyling(chatBubblesToggle, "Chat Bubbles");
            }
            */

            // Position settings - work with the existing layout system
            // In original menu, items are directly positioned as children of content table
            // Let's follow that same pattern instead of fighting with UITable

            // Create Back button by cloning the template (same approach as MultiplayerSubMenuGUI)
            if (backTemplate != null)
            {
                // Clone the back button (same as MultiplayerSubMenuGUI.CreateButton)
                var backButtonObj = Object.Instantiate(backTemplate.gameObject, contentPanel.transform);
                backButtonObj.name = "btn_back";
                backButtonObj.transform.localScale = Vector3.one;

                backButton = backButtonObj.GetComponent<MenuItemGUI>();
                if (backButton != null)
                {
                    // Update the label text
                    var labels = backButtonObj.GetComponentsInChildren<UILabel>(true);
                    foreach (var label in labels)
                    {
                        label.text = "Back";
                        label.MarkAsChanged();
                    }

                    // Set up the callback
                    backButton.on_pressed = new EventDelegate(this, "OnBackPressed");
                    backButtonObj.SetActive(true);
                    CoopMod.Logger.LogInfo("Created Back button");
                }
            }
            else
            {
                CoopMod.Logger.LogError($"Back button template was null!");
            }

            CoopMod.Logger.LogInfo($"Multiplayer settings created");

            // Collect ONLY our created items for BaseMenuGUI management
            var ourItems = new System.Collections.Generic.List<MenuItemGUI>();
            // Network settings
            // if (serverPortInput != null) ourItems.Add(serverPortInput);
            if (maxPlayersSlider != null) ourItems.Add(maxPlayersSlider);
            if (sessionVisibilitySwitcher != null) ourItems.Add(sessionVisibilitySwitcher);
            // if (serverIPInput != null) ourItems.Add(serverIPInput);
            // Local co-op settings
            if (localCoopToggle != null) ourItems.Add(localCoopToggle);
            if (cameraModeSwitcher != null) ourItems.Add(cameraModeSwitcher);
            // DLC settings
            if (dlcStoriesToggle != null) ourItems.Add(dlcStoriesToggle);
            if (dlcRefugeesToggle != null) ourItems.Add(dlcRefugeesToggle);
            if (dlcSoulsToggle != null) ourItems.Add(dlcSoulsToggle);
            if (cheatsToggle != null) ourItems.Add(cheatsToggle);
            // Dialogue to chat settings
            // if (dialogueEnableToggle != null) ourItems.Add(dialogueEnableToggle);
            // if (npcDialogueToggle != null) ourItems.Add(npcDialogueToggle);
            // if (playerChoicesToggle != null) ourItems.Add(playerChoicesToggle);
            // Chat settings
            // if (chatModeToggle != null) ourItems.Add(chatModeToggle);
            // if (chatBubblesToggle != null) ourItems.Add(chatBubblesToggle);
            // Back button
            if (backButton != null) ourItems.Add(backButton);

            items = ourItems.ToArray();
            CoopMod.Logger.LogInfo($"Added {items.Length} custom settings items (including Back button) to BaseMenuGUI items array");

            // Extend the background panel to accommodate all settings
            ExtendBackgroundPanel();
            ApplyResolutionLayout();
        }

        private enum SettingType
        {
            Options,
            Toggle
        }

        private MenuItemGUI CreateSettingItem(GameObject template, string labelText, SettingType type)
        {
            GameObject itemObj = Object.Instantiate(template, contentPanel.transform);
            itemObj.name = $"Setting_{labelText.Replace(" ", "")}";
            itemObj.layer = template.layer;
            itemObj.transform.localScale = Vector3.one;
            itemObj.transform.localPosition = Vector3.zero; // Start at origin within parent
            itemObj.SetActive(true);

            // CRITICAL: Disable ALL UIAnchors on this item and its children to prevent auto-repositioning
            var anchors = itemObj.GetComponentsInChildren<UIAnchor>(true);
            foreach (var anchor in anchors)
            {
                anchor.enabled = false;
            }
            CoopMod.Logger.LogInfo($"Disabled {anchors.Length} UIAnchors in {labelText}");

            // CRITICAL: Disable LocalizedLabel components that will overwrite our custom text
            var localizedLabels = itemObj.GetComponentsInChildren<LocalizedLabel>(true);
            foreach (var locLabel in localizedLabels)
            {
                locLabel.enabled = false;
                Object.DestroyImmediate(locLabel);
            }
            CoopMod.Logger.LogInfo($"Removed {localizedLabels.Length} LocalizedLabel components from {labelText}");

            var menuItem = itemObj.GetComponent<MenuItemGUI>();
            if (menuItem == null)
            {
                CoopMod.Logger.LogError($"MenuItemGUI component not found on template!");
                return null;
            }

            // Check if SimpleOptionsSwitcher exists
            var optionsSwitcher = itemObj.GetComponentInChildren<SimpleOptionsSwitcher>(true);
            if (optionsSwitcher != null)
            {
                CoopMod.Logger.LogInfo($"Found SimpleOptionsSwitcher in {labelText}");
            }
            else
            {
                CoopMod.Logger.LogWarning($"NO SimpleOptionsSwitcher found in {labelText}!");
            }

            // CRITICAL: Initialize the MenuItemGUI so it finds its _options_switcher
            // Init() may call LocalizedLabel.Localize(), but we've already destroyed those components
            menuItem.Init(this);
            CoopMod.Logger.LogInfo($"MenuItemGUI.Init() called for {labelText}");

            // Also call Show() to make sure it's fully initialized
            menuItem.Show();
            CoopMod.Logger.LogInfo($"MenuItemGUI.Show() called for {labelText}");

            // Update ALL labels to the setting name (to replace "Language" or other template text)
            var labels = itemObj.GetComponentsInChildren<UILabel>(true);

            // DIAGNOSTIC: Log ALL labels found
            CoopMod.Logger.LogInfo($"=== DIAGNOSTIC: Found {labels.Length} labels in '{labelText}' item ===");
            for (int i = 0; i < labels.Length; i++)
            {
                var lbl = labels[i];
                CoopMod.Logger.LogInfo($"  [{i}] name='{lbl.name}', text='{lbl.text}', " +
                                     $"gameObject='{lbl.gameObject.name}', " +
                                     $"parent='{(lbl.transform.parent ? lbl.transform.parent.name : "null")}'");
            }

            // Find the left-side setting label from the cloned options row and replace it.
            int labelCount = 0;
            UILabel settingLabel = null;

            foreach (var label in labels)
            {
                string parentName = label.transform.parent ? label.transform.parent.name : "null";

                if (label.name == "label" && parentName != "options switcher" &&
                    parentName != "dec" && parentName != "inc")
                {
                    settingLabel = label;
                    CoopMod.Logger.LogInfo($"  → FOUND setting label to replace: text='{label.text}', parent='{parentName}'");
                    break;
                }
            }

            if (settingLabel != null)
            {
                // Get the golden color from the original Options menu labels
                // Find a label from the original menu that has the golden color
                Color goldenColor = new Color(223f/255f, 170f/255f, 108f/255f, 1f); // Fallback

                // Try to get the actual color from an original Options menu label
                var originalOptionsMenu = GameObject.Find("Options menu");
                if (originalOptionsMenu != null)
                {
                    var originalLabels = originalOptionsMenu.GetComponentsInChildren<UILabel>(true);
                    foreach (var origLabel in originalLabels)
                    {
                        // Find "Master volume", "Music", "SFX", or "Speech" label - these have golden color
                        if (origLabel.text == "Master volume" || origLabel.text == "Music" ||
                            origLabel.text == "SFX" || origLabel.text == "Speech")
                        {
                            goldenColor = origLabel.color;
                            CoopMod.Logger.LogInfo($"  → Got golden color from '{origLabel.text}': {goldenColor}");
                            break;
                        }
                    }
                }

                // CRITICAL: Update text and color AFTER Show() has been called
                settingLabel.text = labelText;
                settingLabel.color = goldenColor;

                CoopMod.Logger.LogInfo($"  ✓ Updated label text to '{labelText}' with golden color {goldenColor}");
                labelCount = 1;
            }

            CoopMod.Logger.LogInfo($"=== Created setting '{labelText}' with {labelCount} label(s) replaced ===");

            // Make the item visible
            menuItem.Show();
            // Ensure label changes are committed to NGUI
            if (settingLabel != null) settingLabel.MarkAsChanged();
            CoopMod.Logger.LogInfo($"MenuItemGUI.Show() called for {labelText}");

            return menuItem;
        }

        /// <summary>
        /// Ensures the left-hand label for a MenuItemGUI uses the correct text and color.
        /// Call this after any internal initializers (SetupOptions/Show) which may overwrite labels.
        /// </summary>
        private void ApplyLabelStyling(MenuItemGUI item, string desiredText)
        {
            if (item == null) return;

            var labels = item.GetComponentsInChildren<UILabel>(true);
            UILabel target = null;
            UILabel rightLabel = null; // Label inside options switcher (for color reference)

            foreach (var lbl in labels)
            {
                string parentName = lbl.transform.parent ? lbl.transform.parent.name : "null";
                if (lbl.name == "label" && parentName != "options switcher" && parentName != "dec" && parentName != "inc")
                {
                    target = lbl;
                }
                // Find the right-side label (value label inside options switcher) for color reference
                if (lbl.name == "label" && parentName == "options switcher")
                {
                    rightLabel = lbl;
                }
            }

            if (target == null)
            {
                CoopMod.Logger.LogWarning($"ApplyLabelStyling: could not find left label for item '{item.gameObject.name}'");
                return;
            }

            // Use the color from the right-side label (options switcher label) - this is white/gray
            Color labelColor = Color.white; // Fallback
            if (rightLabel != null)
            {
                labelColor = rightLabel.color;
                CoopMod.Logger.LogInfo($"  → Got color from right label: {labelColor}");
            }
            else
            {
                CoopMod.Logger.LogWarning($"  → Could not find right label, using white");
            }

            // Apply values
            target.text = desiredText;
            target.color = labelColor;
            target.MarkAsChanged();
            CoopMod.Logger.LogInfo($"ApplyLabelStyling: set '{desiredText}' on '{target.gameObject.name}' with color {labelColor}");
        }

        private void ApplyDLCLabelStyling(MenuItemGUI item, string dlcName, bool installed)
        {
            if (item == null) return;
            ApplyLabelStyling(item, dlcName);
            SetOptionRowEnabled(item, installed);
        }

        /// <summary>
        /// Toggles a settings row between interactive and disabled-looking: grays every label
        /// in the row and disables the BoxColliders on the `inc`/`dec` arrow buttons so they no
        /// longer respond to clicks. Original colors are cached on first enable so they can be
        /// restored when the row becomes interactive again.
        /// </summary>
        private void SetOptionRowEnabled(MenuItemGUI item, bool enabled)
        {
            if (item == null) return;

            var navigationItem = item.gamepad_item;
            if (navigationItem != null)
            {
                navigationItem.active = enabled;
            }

            var switcherTransform = item.transform.Find("options switcher");
            var optionsSwitcher = switcherTransform != null
                ? switcherTransform.GetComponent<SimpleOptionsSwitcher>()
                : null;
            if (!enabled && optionsSwitcher != null)
            {
                optionsSwitcher.game_keys_enabled = false;
            }

            Transform decTransform = switcherTransform != null ? switcherTransform.Find("dec") : null;
            Transform incTransform = switcherTransform != null ? switcherTransform.Find("inc") : null;

            // Toggle the arrow colliders — NGUI buttons use a BoxCollider for hit detection, so
            // disabling it is the cleanest way to make the button unclickable without restructuring.
            SetColliderEnabled(decTransform, enabled);
            SetColliderEnabled(incTransform, enabled);

            // Gray or restore every label in the row.
            var labels = item.GetComponentsInChildren<UILabel>(true);
            for (int i = 0; i < labels.Length; i++)
            {
                UILabel lbl = labels[i];
                if (lbl == null) continue;

                if (enabled)
                {
                    if (!rowLabelColors.ContainsKey(item)) continue;
                    // We don't track per-label colors (labels in a row can have different default
                    // colors — left label is golden, switcher value is white). Restore based on
                    // which label this is.
                    string parentName = lbl.transform.parent ? lbl.transform.parent.name : "null";
                    if (lbl.name == "label" && parentName == "options switcher")
                    {
                        lbl.color = rowValueColors[item];
                    }
                    else
                    {
                        lbl.color = rowLabelColors[item];
                    }
                }
                else
                {
                    // Capture the live color the first time we disable, so any restyle applied
                    // before disabling (e.g. ApplyLabelStyling) is preserved.
                    if (!rowLabelColors.ContainsKey(item))
                    {
                        UILabel leftLabel = null;
                        UILabel valueLabel = null;
                        for (int j = 0; j < labels.Length; j++)
                        {
                            UILabel candidate = labels[j];
                            if (candidate == null) continue;
                            string parent = candidate.transform.parent ? candidate.transform.parent.name : "null";
                            if (candidate.name != "label") continue;
                            if (parent == "options switcher") valueLabel = candidate;
                            else if (parent != "dec" && parent != "inc") leftLabel = candidate;
                        }
                        if (leftLabel != null) rowLabelColors[item] = leftLabel.color;
                        if (valueLabel != null) rowValueColors[item] = valueLabel.color;
                    }
                    lbl.color = DisabledRowColor;
                }
                lbl.MarkAsChanged();
            }

            ConfigureGamepadNavigation();
        }

        private void ConfigureGamepadNavigation()
        {
            if (items == null || items.Length == 0)
                return;

            var navigableItems = new System.Collections.Generic.List<GamepadNavigationItem>();
            foreach (var item in items)
            {
                var navigationItem = item?.gamepad_item;
                if (navigationItem == null)
                    continue;

                navigationItem.SetCustomDirectionItem(null, Direction.Up);
                navigationItem.SetCustomDirectionItem(null, Direction.Down);
                navigationItem.SetCustomDirectionItem(null, Direction.Left);
                navigationItem.SetCustomDirectionItem(null, Direction.Right);
                if (navigationItem.active)
                {
                    navigableItems.Add(navigationItem);
                }
            }

            for (int i = 0; i < navigableItems.Count; i++)
            {
                var current = navigableItems[i];
                current.SetCustomDirectionItem(
                    navigableItems[(i + navigableItems.Count - 1) % navigableItems.Count],
                    Direction.Up);
                current.SetCustomDirectionItem(
                    navigableItems[(i + 1) % navigableItems.Count],
                    Direction.Down);
            }
        }

        private static void SetColliderEnabled(Transform buttonTransform, bool enabled)
        {
            if (buttonTransform == null) return;
            var collider = buttonTransform.GetComponent<Collider>();
            if (collider != null) collider.enabled = enabled;
            // Some NGUI setups use Collider2D instead; toggle it as well if present.
            var collider2d = buttonTransform.GetComponent<Collider2D>();
            if (collider2d != null) collider2d.enabled = enabled;
        }

        private void RefreshDLCSettingLabels()
        {
            ApplyDLCLabelStyling(dlcStoriesToggle, DLC_STORIES_NAME,
                DLCEngine.IsDLCAvailable(DLCEngine.DLCVersion.Stories));
            ApplyDLCLabelStyling(dlcRefugeesToggle, DLC_REFUGEES_NAME,
                DLCEngine.IsDLCAvailable(DLCEngine.DLCVersion.Refugees));
            ApplyDLCLabelStyling(dlcSoulsToggle, DLC_SOULS_NAME,
                DLCEngine.IsDLCAvailable(DLCEngine.DLCVersion.Souls));
        }

        /// <summary>
        /// Re-applies the disabled/enabled appearance of every settings row that is conditional
        /// on another setting or on external state (installed DLCs, Local Co-op being on).
        /// Called on menu open and after every relevant setting change.
        /// </summary>
        private void RefreshConditionalRowStates()
        {
            RefreshDLCSettingLabels();
            // Camera Mode only matters in Local Co-op sessions.
            SetOptionRowEnabled(cameraModeSwitcher, ModConfig.EnableLocalCoop.Value);
        }

        private void ApplyResolutionLayout()
        {
            MultiplayerSettingsResolutionLayout layout = GetActiveResolutionLayout();
            if (layout == DefaultResolutionLayout)
            {
                CoopMod.Logger.LogInfo($"[MultiplayerSettingsGUI] Using default unscaled layout for {Screen.width}x{Screen.height}");
            }
            else
            {
                transform.localPosition = defaultLocalPosition + layout.RootOffset;
                transform.localScale = Vector3.Scale(defaultLocalScale, layout.RootScale);
                ApplyBackgroundHeight(layout);
            }

            MenuItemGUI[] orderedItems =
            {
                maxPlayersSlider,
                sessionVisibilitySwitcher,
                localCoopToggle,
                cameraModeSwitcher,
                dlcStoriesToggle,
                dlcRefugeesToggle,
                dlcSoulsToggle,
                cheatsToggle
            };

            int visibleIndex = 0;
            foreach (var item in orderedItems)
            {
                if (item == null)
                    continue;

                item.transform.localPosition = new Vector3(0f, layout.FirstItemY - visibleIndex * layout.ItemSpacingY, 0f);
                item.transform.localScale = Vector3.one;
                visibleIndex++;
            }

            if (backButton != null)
            {
                backButton.transform.localPosition = new Vector3(0f, layout.BackButtonY, 0f);
                backButton.transform.localScale = Vector3.one;
            }

            CoopMod.Logger.LogInfo(
                $"[MultiplayerSettingsGUI] Applied layout {layout.Name}: " +
                $"root={transform.localPosition}, scale={transform.localScale}, " +
                $"items={visibleIndex}, backY={layout.BackButtonY}");
        }

        private void ApplyBackgroundHeight(MultiplayerSettingsResolutionLayout layout)
        {
            if (backgroundWidget == null || defaultBackgroundHeight <= 0)
                return;

            int targetHeight = defaultBackgroundHeight + layout.BackgroundHeightAdd;
            if (backgroundWidget.height == targetHeight)
                return;

            int previousHeight = backgroundWidget.height;
            backgroundWidget.height = targetHeight;
            CoopMod.Logger.LogInfo(
                $"[MultiplayerSettingsGUI] Applied background height for {layout.Name}: " +
                $"{previousHeight} -> {targetHeight} (base={defaultBackgroundHeight}, add={layout.BackgroundHeightAdd})");
        }

        private static MultiplayerSettingsResolutionLayout GetActiveResolutionLayout()
        {
            int screenWidth = Screen.width;
            int screenHeight = Screen.height;
            for (int i = 0; i < ResolutionLayouts.Length; i++)
            {
                MultiplayerSettingsResolutionLayout layout = ResolutionLayouts[i];
                if (layout.Matches(screenWidth, screenHeight))
                    return layout;
            }

            return DefaultResolutionLayout;
        }

        private void ExtendBackgroundPanel()
        {
            CoopMod.Logger.LogInfo("Extending background panel to accommodate all settings...");

            // Find the main background widget
            // The OptionsMenuGUI has a background panel that contains all items
            // We need to find it in our cloned hierarchy
            var allWidgets = gameObject.GetComponentsInChildren<UIWidget>(true);

            UIWidget foundBackgroundWidget = null;
            foreach (var widget in allWidgets)
            {
                // Look for the main background panel (usually named something like "back", "background", or is the largest sprite)
                if (widget.name.ToLower().Contains("back") && widget is UI2DSprite)
                {
                    var sprite = widget as UI2DSprite;
                    // Check if this is a large background (not a small button background)
                    if (sprite.width > 200 && sprite.height > 100)
                    {
                        foundBackgroundWidget = sprite;
                        CoopMod.Logger.LogInfo($"Found background widget: {widget.name}, size: {sprite.width}x{sprite.height}");
                        break;
                    }
                }
            }

            if (foundBackgroundWidget != null)
            {
                backgroundWidget = foundBackgroundWidget;
                defaultBackgroundHeight = backgroundWidget.height;
                CoopMod.Logger.LogInfo($"Captured background panel base height: {defaultBackgroundHeight}");
            }
            else
            {
                CoopMod.Logger.LogWarning("Could not find background widget to extend");
            }
        }

        private void SetupOptions(MenuItemGUI menuItem, int currentIndex, string[] options, System.Action<int, UILabel> onChange)
        {
            if (menuItem == null) return;

            try
            {
                // Use MenuItemGUI's SetupOptions method (like the game does for screen mode, language, etc.)
                menuItem.SetupOptions(currentIndex, options.Length - 1, options[currentIndex],
                    (index, label) =>
                    {
                        onChange?.Invoke(index, label);
                    },
                    true); // allow wrapping

                CoopMod.Logger.LogInfo($"Options setup successful: {options[currentIndex]} ({options.Length} options)");
            }
            catch (System.Exception e)
            {
                CoopMod.Logger.LogWarning($"Options setup failed: {e.Message}");

                // Fallback: Just display the current value
                var labels = menuItem.GetComponentsInChildren<UILabel>(true);
                foreach (var label in labels)
                {
                    if (label.name.ToLower().Contains("value") || label.name.ToLower().Contains("current"))
                    {
                        label.text = options[currentIndex];
                        CoopMod.Logger.LogInfo($"Using label fallback for options: {label.name}");
                        break;
                    }
                }
            }
        }

        public override void Open()
        {
            CoopMod.Logger.LogInfo("Opening MultiplayerSettingsGUI");

            gameObject.SetActive(true);
            RefreshConditionalRowStates();
            ConfigureGamepadNavigation();

            // DIAGNOSTIC: Log hierarchy and visibility
            CoopMod.Logger.LogInfo($"GameObject active: {gameObject.activeSelf}, activeInHierarchy: {gameObject.activeInHierarchy}");
            CoopMod.Logger.LogInfo($"GameObject name: {gameObject.name}, parent: {(gameObject.transform.parent != null ? gameObject.transform.parent.name : "NULL")}");

            if (contentPanel != null)
            {
                CoopMod.Logger.LogInfo($"ContentPanel: {contentPanel.name}, active: {contentPanel.activeSelf}, activeInHierarchy: {contentPanel.activeInHierarchy}");
                CoopMod.Logger.LogInfo($"ContentPanel pos: {contentPanel.transform.position}, localPos: {contentPanel.transform.localPosition}");
                CoopMod.Logger.LogInfo($"ContentPanel parent: {(contentPanel.transform.parent != null ? contentPanel.transform.parent.name : "NULL")}");
                CoopMod.Logger.LogInfo($"ContentPanel scale: {contentPanel.transform.localScale}");
            }
            else
            {
                CoopMod.Logger.LogError("ContentPanel is NULL!");
            }

            if (titleLabel != null)
            {
                CoopMod.Logger.LogInfo($"Title label: '{titleLabel.text}', enabled: {titleLabel.enabled}, depth: {titleLabel.depth}");
                CoopMod.Logger.LogInfo($"Title GameObject: {titleLabel.gameObject.name}, active: {titleLabel.gameObject.activeSelf}");
            }

            // Make sure contentPanel is active
            if (contentPanel != null)
            {
                contentPanel.SetActive(true);
            }

            // Make sure all setting items are active and visible
            if (items != null)
            {
                CoopMod.Logger.LogInfo($"Opening with {items.Length} items in array");
                for (int i = 0; i < items.Length; i++)
                {
                    var item = items[i];
                    if (item != null)
                    {
                        item.gameObject.SetActive(true);
                        item.Show();
                        CoopMod.Logger.LogInfo($"  Item {i}: {item.gameObject.name}, active: {item.gameObject.activeSelf}");
                    }
                    else
                    {
                        CoopMod.Logger.LogWarning($"  Item {i}: NULL");
                    }
                }
            }
            else
            {
                CoopMod.Logger.LogError("items array is NULL!");
            }

            try
            {
                base.Open();
            }
            catch (System.Exception e)
            {
                CoopMod.Logger.LogWarning($"BaseMenuGUI.Open() failed: {e}, using simple open");
            }

            // CRITICAL: base.Open() resets the title text back to "Options"
            // We must set it to "Settings" again AFTER base.Open()
            if (titleLabel != null)
            {
                titleLabel.text = "Settings";
                titleLabel.depth = 200;
                CoopMod.Logger.LogInfo($"Re-set title to 'Settings' after base.Open(), depth: {titleLabel.depth}");
            }

            ApplyResolutionLayout();

            // Refresh all values from config
            RefreshSettings();
        }

        public override void Hide(bool playSfx = true)
        {
            CoopMod.Logger.LogInfo("Hiding MultiplayerSettingsGUI");

            try
            {
                base.Hide(playSfx);
            }
            catch (System.Exception e)
            {
                CoopMod.Logger.LogWarning($"BaseMenuGUI.Hide() failed: {e.Message}, using simple hide");
            }

            gameObject.SetActive(false);
        }

        private void RefreshSettings()
        {
            RefreshConditionalRowStates();

            CoopMod.Logger.LogInfo($"Settings refreshed - Port: {ModConfig.ServerPort.Value}, " +
                                  $"MaxPlayers: {ModConfig.MaxPlayers.Value}, " +
                                  $"Visibility: {ModConfig.HostedSessionVisibility.Value}, " +
                                  $"IP: {ModConfig.ServerIP.Value}, " +
                                  $"TickRate: {ModConfig.NetworkTickRate.Value}, " +
                                  $"DLC: Stories={ModConfig.EnableDLCStories.Value}, Refugees={ModConfig.EnableDLCRefugees.Value}, Souls={ModConfig.EnableDLCSouls.Value}");
        }

        public void OnBackPressed()
        {
            CoopMod.Logger.LogInfo("Back button pressed in settings");
            Hide(true);

            // Return to multiplayer submenu
            if (MultiplayerSubMenuGUI.Instance != null)
            {
                MultiplayerSubMenuGUI.Instance.Open();
            }
        }

        protected override bool OnPressedBack()
        {
            OnBackPressed();
            return true;
        }

        public override void OnClosePressed()
        {
            OnBackPressed();
        }
    }
}

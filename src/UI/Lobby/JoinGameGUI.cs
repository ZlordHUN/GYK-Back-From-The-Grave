using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// Join Game GUI - Browse hosted games and accept Steam friend invitations to join multiplayer
    /// </summary>
    public class JoinGameGUI : BaseMenuGUI
    {
        private static JoinGameGUI _instance;
        public static JoinGameGUI Instance => _instance;

        private GameObject titleBannerObj;
        private UILabel titleLabel;
        private GameObject buttonLayerObj;
        private GameObject backButtonObj;
        private UIWidget backButtonWidget;
        private GameObject refreshButtonObj;
        private UIWidget refreshButtonWidget;
        private GameObject connectButtonObj;
        private UIWidget connectButtonWidget;
        private GamepadNavigationItem backNavigationItem;
        private GamepadNavigationItem refreshNavigationItem;
        private GamepadNavigationItem connectNavigationItem;
        private GameObject saveSlotBackgroundTemplate; // Store template for IP input
        private Camera uiCamera; // UI Camera for click detection
        private GerryInviteNotifier gerryInviteNotifier; // Gerry for Steam invitations
        private GerryJoinNotifier gerryJoinNotifier; // Gerry for join attempt feedback
        private ServerBrowserGUI serverBrowser; // Server browser showing friends hosting games
        private const int JOIN_BUTTON_PANEL_DEPTH = 500;
        private const int JOIN_BUTTON_WIDGET_DEPTH = 501;
        private const int JOIN_BUTTON_LABEL_DEPTH = 502;
        private const float DEFAULT_BROWSER_BUTTON_GAP = 8f;
        private static readonly JoinGameResolutionLayout DefaultResolutionLayout = new JoinGameResolutionLayout(
            "Default",
            0,
            0,
            new Vector3(0f, 290f, 0f),
            new Vector3(0f, 20f, 0f),
            -220f,
            200f,
            Vector3.one,
            false,
            DEFAULT_BROWSER_BUTTON_GAP);
        private static readonly JoinGameResolutionLayout[] ResolutionLayouts =
        {
            new JoinGameResolutionLayout(
                "SteamDeck1280x800",
                1280,
                800,
                new Vector3(0f, 285f, 0f),
                new Vector3(0f, 0f, 0f),
                -175f,
                190f,
                Vector3.one,
                true,
                DEFAULT_BROWSER_BUTTON_GAP),
            new JoinGameResolutionLayout(
                "1366x768",
                1366,
                768,
                new Vector3(0f, 270f, 0f),
                new Vector3(0f, 0f, 0f),
                -170f,
                205f,
                new Vector3(0.96f, 0.96f, 1f),
                true,
                DEFAULT_BROWSER_BUTTON_GAP),
            new JoinGameResolutionLayout(
                "1440x900",
                1440,
                900,
                new Vector3(0f, 285f, 0f),
                new Vector3(0f, 20f, 0f),
                -175f,
                205f,
                Vector3.one,
                true,
                DEFAULT_BROWSER_BUTTON_GAP),
            new JoinGameResolutionLayout(
                "1600x900",
                1600,
                900,
                new Vector3(0f, 285f, 0f),
                new Vector3(0f, 0f, 0f),
                -175f,
                220f,
                Vector3.one,
                true,
                DEFAULT_BROWSER_BUTTON_GAP),
            new JoinGameResolutionLayout(
                "1920x800",
                1920,
                800,
                new Vector3(0f, 270f, 0f),
                new Vector3(0f, 0f, 0f),
                -170f,
                260f,
                new Vector3(0.96f, 0.96f, 1f),
                true,
                DEFAULT_BROWSER_BUTTON_GAP),
            new JoinGameResolutionLayout(
                "1920x1080",
                1920,
                1080,
                new Vector3(0f, 285f, 0f),
                new Vector3(0f, 20f, 0f),
                -175f,
                260f,
                Vector3.one,
                true,
                DEFAULT_BROWSER_BUTTON_GAP),
            new JoinGameResolutionLayout(
                "1920x1200",
                1920,
                1200,
                new Vector3(0f, 285f, 0f),
                new Vector3(0f, 20f, 0f),
                -175f,
                260f,
                Vector3.one,
                true,
                DEFAULT_BROWSER_BUTTON_GAP),
            new JoinGameResolutionLayout(
                "1920x1280",
                1920,
                1280,
                new Vector3(0f, 285f, 0f),
                new Vector3(0f, 20f, 0f),
                -175f,
                260f,
                Vector3.one,
                true,
                DEFAULT_BROWSER_BUTTON_GAP),
            new JoinGameResolutionLayout(
                "1920x1440",
                1920,
                1440,
                new Vector3(0f, 285f, 0f),
                new Vector3(0f, 20f, 0f),
                -175f,
                260f,
                Vector3.one,
                true,
                DEFAULT_BROWSER_BUTTON_GAP),
            new JoinGameResolutionLayout(
                "2048x1152",
                2048,
                1152,
                new Vector3(0f, 285f, 0f),
                new Vector3(0f, 20f, 0f),
                -175f,
                260f,
                Vector3.one,
                true,
                DEFAULT_BROWSER_BUTTON_GAP),
            new JoinGameResolutionLayout(
                "2048x1536",
                2048,
                1536,
                new Vector3(0f, 285f, 0f),
                new Vector3(0f, 20f, 0f),
                -175f,
                260f,
                Vector3.one,
                true,
                DEFAULT_BROWSER_BUTTON_GAP),
            new JoinGameResolutionLayout(
                "2560x1080",
                2560,
                1080,
                new Vector3(0f, 285f, 0f),
                new Vector3(0f, 20f, 0f),
                -175f,
                260f,
                Vector3.one,
                true,
                DEFAULT_BROWSER_BUTTON_GAP),
            new JoinGameResolutionLayout(
                "1600x1200",
                1600,
                1200,
                new Vector3(0f, 285f, 0f),
                new Vector3(0f, 20f, 0f),
                -175f,
                220f,
                Vector3.one,
                true,
                DEFAULT_BROWSER_BUTTON_GAP),
            new JoinGameResolutionLayout(
                "1680x1050",
                1680,
                1050,
                new Vector3(0f, 285f, 0f),
                new Vector3(0f, 20f, 0f),
                -175f,
                235f,
                Vector3.one,
                true,
                DEFAULT_BROWSER_BUTTON_GAP),
            new JoinGameResolutionLayout(
                "1440x960",
                1440,
                960,
                new Vector3(0f, 285f, 0f),
                new Vector3(0f, 20f, 0f),
                -175f,
                205f,
                Vector3.one,
                true,
                DEFAULT_BROWSER_BUTTON_GAP),
            new JoinGameResolutionLayout(
                "1440x1080",
                1440,
                1080,
                new Vector3(0f, 285f, 0f),
                new Vector3(0f, 20f, 0f),
                -175f,
                205f,
                Vector3.one,
                true,
                DEFAULT_BROWSER_BUTTON_GAP),
            new JoinGameResolutionLayout(
                "1280x960",
                1280,
                960,
                new Vector3(0f, 285f, 0f),
                new Vector3(0f, 20f, 0f),
                -175f,
                190f,
                Vector3.one,
                true,
                DEFAULT_BROWSER_BUTTON_GAP),
            new JoinGameResolutionLayout(
                "1280x1024",
                1280,
                1024,
                new Vector3(0f, 285f, 0f),
                new Vector3(0f, 20f, 0f),
                -175f,
                190f,
                Vector3.one,
                true,
                DEFAULT_BROWSER_BUTTON_GAP),
            new JoinGameResolutionLayout(
                "1400x1050",
                1400,
                1050,
                new Vector3(0f, 285f, 0f),
                new Vector3(0f, 20f, 0f),
                -175f,
                190f,
                Vector3.one,
                true,
                DEFAULT_BROWSER_BUTTON_GAP)
        };

        private sealed class JoinGameResolutionLayout
        {
            public readonly string Name;
            public readonly int ScreenWidth;
            public readonly int ScreenHeight;
            public readonly Vector3 TitlePosition;
            public readonly Vector3 ServerBrowserPosition;
            public readonly float BottomButtonY;
            public readonly float SideButtonOffset;
            public readonly Vector3 LocalScale;
            public readonly bool AnchorButtonsToBrowser;
            public readonly float BrowserButtonGap;

            public JoinGameResolutionLayout(
                string name,
                int screenWidth,
                int screenHeight,
                Vector3 titlePosition,
                Vector3 serverBrowserPosition,
                float bottomButtonY,
                float sideButtonOffset,
                Vector3 localScale,
                bool anchorButtonsToBrowser,
                float browserButtonGap)
            {
                Name = name;
                ScreenWidth = screenWidth;
                ScreenHeight = screenHeight;
                // NGUI uses roughly half-resolution UI coordinates here. Keep
                // the title inside the top edge on shorter display modes.
                float titleY = screenHeight > 0
                    ? Mathf.Min(titlePosition.y, screenHeight * 0.25f - 15f)
                    : titlePosition.y;
                TitlePosition = new Vector3(titlePosition.x, titleY, titlePosition.z);
                ServerBrowserPosition = serverBrowserPosition;
                BottomButtonY = bottomButtonY;
                SideButtonOffset = sideButtonOffset;
                LocalScale = localScale;
                AnchorButtonsToBrowser = anchorButtonsToBrowser;
                BrowserButtonGap = browserButtonGap;
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

        public static JoinGameGUI Create()
        {
            if (_instance != null)
                return _instance;

            CoopMod.Logger.LogInfo("Creating JoinGameGUI...");

            // Find UIRoot to parent to
            UIRoot uiRoot = Object.FindObjectOfType<UIRoot>();
            if (uiRoot == null)
            {
                CoopMod.Logger.LogError("Cannot find UIRoot!");
                return null;
            }

            // Create a simple GameObject for our join game screen
            GameObject joinGameObj = new GameObject("JoinGameGUI");
            joinGameObj.layer = 13; // NGUI layer
            joinGameObj.transform.SetParent(uiRoot.transform, false);
            joinGameObj.transform.localPosition = Vector3.zero;
            joinGameObj.transform.localScale = Vector3.one;
            
            _instance = joinGameObj.AddComponent<JoinGameGUI>();
            _instance.add_to_opened_stack = true;
            var navigationController = joinGameObj.AddComponent<GamepadNavigationController>();
            navigationController.auto_select = true;
            navigationController.vertical_settings = new GamepadNavigationSettings();
            navigationController.horizontal_settings = new GamepadNavigationSettings();

            // Keep alive across scenes
            Object.DontDestroyOnLoad(joinGameObj);

            // Add UIPanel for rendering
            UIPanel panel = joinGameObj.AddComponent<UIPanel>();
            panel.depth = 100; // Above main menu
            panel.alpha = 1f;
            panel.clipping = UIDrawCall.Clipping.None;

            CoopMod.Logger.LogInfo($"Created join game panel, parent: {uiRoot.name}");

            // Store UI camera for click detection
            _instance.uiCamera = NGUITools.FindCameraForLayer(joinGameObj.layer);
            if (_instance.uiCamera != null)
            {
                CoopMod.Logger.LogInfo($"UI Camera found: {_instance.uiCamera.name}");
            }

            // Create join game content
            _instance.CreateJoinGameContent(joinGameObj);

            // Initialize BaseMenuGUI
            _instance.Init();
            _instance.ConfigureGamepadNavigation();

            // Hide initially
            joinGameObj.SetActive(false);
            _instance.ApplyResolutionLayout();

            CoopMod.Logger.LogInfo($"JoinGameGUI created successfully!");
            return _instance;
        }

        private void CreateJoinGameContent(GameObject root)
        {
            CoopMod.Logger.LogInfo("Creating join game content...");

            // Get a reference font from existing UI
            UIFont referenceFont = null;
            var existingLabels = Object.FindObjectsOfType<UILabel>();
            if (existingLabels != null && existingLabels.Length > 0)
            {
                foreach (var label in existingLabels)
                {
                    if (label.bitmapFont != null)
                    {
                        referenceFont = label.bitmapFont;
                        CoopMod.Logger.LogInfo($"Found reference font: {referenceFont.name}");
                        break;
                    }
                }
            }

            if (referenceFont == null)
            {
                CoopMod.Logger.LogError("Could not find any font reference!");
                return;
            }

            // Find styled header template from InventoryGUI
            GameObject headerTemplate = null;
            var inventoryGUI = Object.FindObjectOfType<InventoryGUI>(true);
            if (inventoryGUI != null)
            {
                CoopMod.Logger.LogInfo("Found InventoryGUI, searching for styled headers...");
                
                try
                {
                    var goHdrBuffsField = typeof(InventoryGUI).GetField("go_hdr_buffs");
                    
                    if (goHdrBuffsField != null)
                    {
                        headerTemplate = goHdrBuffsField.GetValue(inventoryGUI) as GameObject;
                        if (headerTemplate != null)
                            CoopMod.Logger.LogInfo($"Found header template: {headerTemplate.name}");
                    }
                }
                catch (System.Exception ex)
                {
                    CoopMod.Logger.LogWarning($"Reflection failed: {ex.Message}");
                }
                
                // Fallback: Search through all children
                if (headerTemplate == null)
                {
                    CoopMod.Logger.LogInfo("Reflection failed, searching children manually...");
                    var allTransforms = inventoryGUI.GetComponentsInChildren<Transform>(true);
                    
                    foreach (var t in allTransforms)
                    {
                        if (t.name.ToLower().Contains("hdr") || t.name.ToLower().Contains("buff"))
                        {
                            headerTemplate = t.gameObject;
                            CoopMod.Logger.LogInfo($"Using as header template: {t.name}");
                            break;
                        }
                    }
                }
            }

            // Find save slot template for IP input background
            var saveSlotsMenu = Object.FindObjectOfType<SaveSlotsMenuGUI>(true);
            if (saveSlotsMenu != null)
            {
                CoopMod.Logger.LogInfo("Found SaveSlotsMenuGUI, searching for save slot template...");
                var saveSlots = saveSlotsMenu.GetComponentsInChildren<SaveSlotGUI>(true);
                if (saveSlots != null && saveSlots.Length > 0)
                {
                    saveSlotBackgroundTemplate = saveSlots[0].gameObject;
                    CoopMod.Logger.LogInfo($"Found save slot template: {saveSlotBackgroundTemplate.name}");
                }
            }

            if (saveSlotBackgroundTemplate == null)
            {
                CoopMod.Logger.LogError("Could not find save slot template for IP input!");
                return;
            }

            Transform contentParent = root.transform;

            // Create title banner (centered at top)
            CreateTitleBanner(contentParent, headerTemplate, referenceFont);

            // Create Server Browser (shows friends hosting games)
            CreateServerBrowser(contentParent);

            // Create Back button
            CreateBackButton(contentParent, referenceFont);

            // Create Gerry notifiers (two separate instances)
            CreateGerryInviteNotifier(contentParent); // For Steam invitations
            CreateGerryJoinNotifier(contentParent);   // For join attempt feedback

            CoopMod.Logger.LogInfo("Join game content created successfully!");
        }

        private void CreateServerBrowser(Transform parent)
        {
            CoopMod.Logger.LogInfo("Creating Server Browser...");
            
            // Position the server browser below the title
            serverBrowser = ServerBrowserGUI.Create(parent, GetActiveResolutionLayout().ServerBrowserPosition);
            
            if (serverBrowser != null)
            {
                serverBrowser.ControllerItemsChanged -= RefreshGamepadNavigation;
                serverBrowser.ControllerItemsChanged += RefreshGamepadNavigation;
                CoopMod.Logger.LogInfo("Server Browser created!");
            }
            else
            {
                CoopMod.Logger.LogWarning("Failed to create Server Browser");
            }
        }

        private void CreateGerryInviteNotifier(Transform parent)
        {
            CoopMod.Logger.LogInfo("Creating Gerry invite notifier...");
            
            GameObject gerryObj = new GameObject("GerryInviteNotifier");
            gerryObj.layer = 13;
            gerryObj.transform.SetParent(parent, false);
            gerryObj.transform.localPosition = Vector3.zero;
            gerryObj.transform.localScale = Vector3.one;
            
            gerryInviteNotifier = gerryObj.AddComponent<GerryInviteNotifier>();
            gerryInviteNotifier.Initialize(parent);
            
            CoopMod.Logger.LogInfo("Gerry invite notifier created!");
        }

        private void CreateGerryJoinNotifier(Transform parent)
        {
            CoopMod.Logger.LogInfo("Creating Gerry join notifier...");
            
            GameObject gerryObj = new GameObject("GerryJoinNotifier");
            gerryObj.layer = 13;
            gerryObj.transform.SetParent(parent, false);
            gerryObj.transform.localPosition = Vector3.zero;
            gerryObj.transform.localScale = Vector3.one;
            
            gerryJoinNotifier = gerryObj.AddComponent<GerryJoinNotifier>();
            gerryJoinNotifier.Initialize(parent);
            
            CoopMod.Logger.LogInfo("Gerry join notifier created!");
        }

        private void CreateTitleBanner(Transform parent, GameObject headerTemplate, UIFont font)
        {
            GameObject titleObj;
            
            if (headerTemplate != null)
            {
                // Clone the styled header
                titleObj = Object.Instantiate(headerTemplate, parent);
                titleObj.name = "JoinGameTitle";
                titleObj.transform.localPosition = GetActiveResolutionLayout().TitlePosition;
                titleObj.transform.localScale = Vector3.one;
                titleObj.SetActive(true);

                // This header is cloned from InventoryGUI.go_hdr_buffs. Leaving
                // its LocalizedLabel attached makes BaseGUI.Open localize it back
                // to "Buffs", especially on the controller open path.
                foreach (var localizedLabel in titleObj.GetComponentsInChildren<LocalizedLabel>(true))
                {
                    localizedLabel.enabled = false;
                    Object.DestroyImmediate(localizedLabel);
                }
                
                // Configure children
                foreach (Transform child in titleObj.transform)
                {
                    child.gameObject.SetActive(true);
                    
                    var sprite2D = child.GetComponent<UI2DSprite>();
                    if (sprite2D != null)
                    {
                        sprite2D.enabled = true;
                        sprite2D.depth = 100;
                        sprite2D.MarkAsChanged();
                        CoopMod.Logger.LogInfo($"Configured title sprite: {sprite2D.width}x{sprite2D.height}");
                    }
                }
                
                // Update ALL labels in the header (there might be multiple)
                var allLabels = titleObj.GetComponentsInChildren<UILabel>(true);
                CoopMod.Logger.LogInfo($"Found {allLabels.Length} labels in title banner");
                
                foreach (var label in allLabels)
                {
                    CoopMod.Logger.LogInfo($"  Updating label: '{label.text}' -> 'JOIN A GAME'");
                    label.text = "JOIN A GAME";
                    label.overflowMethod = UILabel.Overflow.ClampContent;
                    label.enabled = true;
                    label.depth = 105;
                    label.MarkAsChanged();
                }
                
                // Store the first label as our title label
                if (allLabels.Length > 0)
                {
                    titleLabel = allLabels[0];
                    CoopMod.Logger.LogInfo($"Created title banner with {allLabels.Length} labels updated");
                }
            }
            else
            {
                // Fallback: simple label
                titleObj = new GameObject("JoinGameTitle");
                titleObj.layer = 13;
                titleObj.transform.SetParent(parent, false);
                titleObj.transform.localPosition = GetActiveResolutionLayout().TitlePosition;
                titleObj.transform.localScale = Vector3.one;
                
                titleLabel = titleObj.AddComponent<UILabel>();
                titleLabel.bitmapFont = font;
                titleLabel.text = "JOIN A GAME";
                titleLabel.fontSize = 32;
                titleLabel.color = new Color(1f, 0.84f, 0f);
                titleLabel.alignment = NGUIText.Alignment.Center;
                titleLabel.overflowMethod = UILabel.Overflow.ResizeFreely;
                titleLabel.depth = 105;
                titleLabel.enabled = true;
                titleLabel.MarkAsChanged();
                titleObj.SetActive(true);
                CoopMod.Logger.LogInfo("Created fallback title label");
            }

            titleBannerObj = titleObj;
        }

        private void CreateSeparatorLabel(Transform parent, UIFont font)
        {
            GameObject separatorObj = new GameObject("SeparatorLabel");
            separatorObj.layer = 13;
            separatorObj.transform.SetParent(parent, false);
            separatorObj.transform.localPosition = new Vector3(0, -85, 0); // Between server browser and IP input

            var separatorLabel = separatorObj.AddComponent<UILabel>();
            separatorLabel.bitmapFont = font;
            separatorLabel.text = "— or enter IP address —";
            separatorLabel.fontSize = 14;
            separatorLabel.color = new Color(0.6f, 0.6f, 0.6f); // Gray
            separatorLabel.alignment = NGUIText.Alignment.Center;
            separatorLabel.overflowMethod = UILabel.Overflow.ResizeFreely;
            separatorLabel.depth = 105;
            
            CoopMod.Logger.LogInfo("Created separator label");
        }

        private void CreateBackButton(Transform parent, UIFont font)
        {
            // Use DialogButtonGUI as template (same as lobby Back button)
            var dialogButtons = Resources.FindObjectsOfTypeAll<DialogButtonGUI>();
            if (dialogButtons == null || dialogButtons.Length == 0)
            {
                CoopMod.Logger.LogError("Cannot find DialogButtonGUI to clone Back button from!");
                return;
            }

            var template = dialogButtons[0].gameObject;
            const float SIDE_BUTTON_OFFSET = 200f;
            const float BOTTOM_BUTTON_Y = -220f;
            Transform buttonParent = GetOrCreateButtonLayer(parent);

            backButtonObj = CreateVisibleButton(buttonParent, template, font, "btn_back", "Back", new Vector3(-SIDE_BUTTON_OFFSET, BOTTOM_BUTTON_Y, 0), out backButtonWidget);
            backButtonObj.SetActive(true);
            SetButtonWidgetDepth(backButtonObj, JOIN_BUTTON_WIDGET_DEPTH);
            CoopMod.Logger.LogInfo($"Created Back button at {backButtonObj.transform.localPosition}, widget: {backButtonWidget != null}");

            // ---- Refresh and Connect buttons (cloned from same DialogButtonGUI template) ----
            // Layout: [Back]  [Refresh]  [Connect]
            refreshButtonObj = CreateVisibleButton(buttonParent, template, font, "btn_refresh", "Refresh", new Vector3(0, BOTTOM_BUTTON_Y, 0), out refreshButtonWidget);
            refreshButtonObj.SetActive(true);
            SetButtonWidgetDepth(refreshButtonObj, JOIN_BUTTON_WIDGET_DEPTH);

            connectButtonObj = CreateVisibleButton(buttonParent, template, font, "btn_connect", "Connect", new Vector3(SIDE_BUTTON_OFFSET, BOTTOM_BUTTON_Y, 0), out connectButtonWidget);
            connectButtonObj.SetActive(true);
            SetButtonWidgetDepth(connectButtonObj, JOIN_BUTTON_WIDGET_DEPTH);

            refreshNavigationItem = ConfigureControllerButton(refreshButtonObj, OnRefreshButtonPressed);
            backNavigationItem = ConfigureControllerButton(backButtonObj, OnBackButtonPressed);
            connectNavigationItem = ConfigureControllerButton(connectButtonObj, OnConnectButtonPressed);

            CoopMod.Logger.LogInfo("Created Refresh and Connect buttons next to Back");
            ApplyResolutionLayout();
        }

        private GamepadNavigationItem ConfigureControllerButton(GameObject buttonObj, System.Action onPressed)
        {
            if (buttonObj == null)
                return null;

            var navigationItem = buttonObj.GetComponent<GamepadNavigationItem>() ??
                                 buttonObj.AddComponent<GamepadNavigationItem>();
            navigationItem.active = true;
            ControllerFocusFrame.Attach(buttonObj, navigationItem);

            UILabel label = buttonObj.GetComponentInChildren<UILabel>(true);
            Color normalColor = label != null ? label.color : Color.white;
            navigationItem.SetCallbacks(
                () => SetControllerButtonFocus(label, normalColor, true),
                () => SetControllerButtonFocus(label, normalColor, false),
                () => onPressed?.Invoke());
            return navigationItem;
        }

        private static void SetControllerButtonFocus(UILabel label, Color normalColor, bool focused)
        {
            if (label == null)
                return;
            label.color = focused ? new Color(1f, 0.84f, 0f, 1f) : normalColor;
            label.MarkAsChanged();
        }

        private void ConfigureGamepadNavigation()
        {
            if (refreshNavigationItem == null || backNavigationItem == null || connectNavigationItem == null)
                return;

            GamepadNavigationItem[] tabs = serverBrowser?.GetTabNavigationItems() ??
                                           new GamepadNavigationItem[0];
            GamepadNavigationItem[] rows = serverBrowser?.GetServerRowNavigationItems() ??
                                           new GamepadNavigationItem[0];
            GamepadNavigationItem currentTab = serverBrowser?.GetCurrentTabNavigationItem();
            GamepadNavigationItem firstRow = rows.Length > 0 ? rows[0] : null;
            GamepadNavigationItem lastRow = rows.Length > 0 ? rows[rows.Length - 1] : null;

            for (int i = 0; i < tabs.Length; i++)
            {
                GamepadNavigationItem tab = tabs[i];
                tab.active = true;
                tab.SetCustomDirectionItem(tabs[(i + tabs.Length - 1) % tabs.Length], Direction.Left);
                tab.SetCustomDirectionItem(tabs[(i + 1) % tabs.Length], Direction.Right);
                tab.SetCustomDirectionItem(firstRow ?? backNavigationItem, Direction.Down);
            }

            for (int i = 0; i < rows.Length; i++)
            {
                GamepadNavigationItem row = rows[i];
                row.active = true;
                row.SetCustomDirectionItem(i > 0 ? rows[i - 1] : currentTab, Direction.Up);
                row.SetCustomDirectionItem(i + 1 < rows.Length ? rows[i + 1] : backNavigationItem, Direction.Down);
                row.SetCustomDirectionItem(row, Direction.Left);
                row.SetCustomDirectionItem(row, Direction.Right);
            }

            GamepadNavigationItem[] buttons =
            {
                backNavigationItem,
                refreshNavigationItem,
                connectNavigationItem
            };
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i].active = true;
                buttons[i].SetCustomDirectionItem(
                    buttons[(i + buttons.Length - 1) % buttons.Length], Direction.Left);
                buttons[i].SetCustomDirectionItem(
                    buttons[(i + 1) % buttons.Length], Direction.Right);
                buttons[i].SetCustomDirectionItem(lastRow ?? currentTab, Direction.Up);
            }
        }

        private void RefreshGamepadNavigation()
        {
            ConfigureGamepadNavigation();
            if (!is_shown || !BaseGUI.for_gamepad || gamepad_controller == null)
                return;

            gamepad_controller.ReinitItems(false);
            if (gamepad_controller.focused_item == null)
            {
                GamepadNavigationItem currentTab =
                    serverBrowser?.GetCurrentTabNavigationItem();
                if (currentTab != null)
                    gamepad_controller.SetFocusedItem(currentTab, false);
                else
                    gamepad_controller.FocusOnFirstActive();
            }
        }

        internal void FocusControllerItem(GamepadNavigationItem item)
        {
            if (item == null || gamepad_controller == null)
                return;

            if (gamepad_controller.GetFocusedItemIndex(item) < 0)
                return;

            gamepad_controller.SetFocusedItem(item, false);
        }

        private GameObject CreateVisibleButton(Transform parent, GameObject visualTemplate, UIFont font, string name, string text, Vector3 localPosition, out UIWidget widget)
        {
            GameObject buttonObj = visualTemplate != null
                ? Object.Instantiate(visualTemplate, parent)
                : new GameObject(name);

            buttonObj.name = name;
            buttonObj.layer = 13;
            buttonObj.transform.SetParent(parent, false);
            buttonObj.transform.localPosition = localPosition;
            buttonObj.transform.localScale = Vector3.one;

            foreach (var localizedLabel in buttonObj.GetComponentsInChildren<LocalizedLabel>(true))
            {
                localizedLabel.enabled = false;
                Object.DestroyImmediate(localizedLabel);
            }

            foreach (Transform child in buttonObj.GetComponentsInChildren<Transform>(true))
            {
                child.gameObject.layer = 13;
            }

            var buttonGUI = buttonObj.GetComponent<DialogButtonGUI>();
            if (buttonGUI != null)
            {
                Object.Destroy(buttonGUI);
            }

            foreach (var btn in buttonObj.GetComponentsInChildren<UIButton>(true))
            {
                Object.Destroy(btn);
            }

            UILabel label = buttonObj.GetComponentInChildren<UILabel>(true);
            if (label != null)
            {
                label.bitmapFont = font;
                label.text = text;
                label.depth = JOIN_BUTTON_LABEL_DEPTH;
                label.MarkAsChanged();
            }

            foreach (var uiWidget in buttonObj.GetComponentsInChildren<UIWidget>(true))
            {
                uiWidget.gameObject.SetActive(true);
                uiWidget.enabled = true;
                uiWidget.alpha = 1f;
                uiWidget.depth = uiWidget is UILabel ? JOIN_BUTTON_LABEL_DEPTH : JOIN_BUTTON_WIDGET_DEPTH;
                Color widgetColor = uiWidget.color;
                widgetColor.a = 1f;
                uiWidget.color = widgetColor;
                uiWidget.MarkAsChanged();
            }

            widget = FindLargestWidget(buttonObj);

            if (buttonObj.GetComponent<BoxCollider2D>() == null && widget != null)
            {
                BoxCollider2D collider = buttonObj.AddComponent<BoxCollider2D>();
                collider.size = new Vector2(widget.width, widget.height);
                collider.isTrigger = true;
            }

            return buttonObj;
        }

        private Transform GetOrCreateButtonLayer(Transform parent)
        {
            if (buttonLayerObj != null)
                return buttonLayerObj.transform;

            buttonLayerObj = new GameObject("JoinGameButtonLayer");
            buttonLayerObj.layer = 13;
            buttonLayerObj.transform.SetParent(parent, false);
            buttonLayerObj.transform.localPosition = Vector3.zero;
            buttonLayerObj.transform.localScale = Vector3.one;

            UIPanel buttonPanel = buttonLayerObj.AddComponent<UIPanel>();
            buttonPanel.depth = JOIN_BUTTON_PANEL_DEPTH;
            buttonPanel.alpha = 1f;
            buttonPanel.clipping = UIDrawCall.Clipping.None;

            return buttonLayerObj.transform;
        }

        private static UIWidget FindLargestWidget(GameObject root)
        {
            UIWidget largest = null;
            int largestArea = 0;
            foreach (var widget in root.GetComponentsInChildren<UIWidget>(true))
            {
                int area = Mathf.Abs(widget.width * widget.height);
                if (largest == null || area > largestArea)
                {
                    largest = widget;
                    largestArea = area;
                }
            }

            return largest;
        }

        private static void SetButtonWidgetDepth(GameObject buttonObj, int depth)
        {
            if (buttonObj == null)
                return;

            foreach (var widget in buttonObj.GetComponentsInChildren<UIWidget>(true))
            {
                widget.enabled = true;
                widget.alpha = 1f;
                widget.depth = widget is UILabel ? JOIN_BUTTON_LABEL_DEPTH : depth;
                Color widgetColor = widget.color;
                widgetColor.a = 1f;
                widget.color = widgetColor;
                widget.MarkAsChanged();
            }
        }

        private void ApplyResolutionLayout()
        {
            JoinGameResolutionLayout layout = GetActiveResolutionLayout();
            if (layout == DefaultResolutionLayout)
            {
                CoopMod.Logger.LogInfo($"[JoinGameGUI] Using default unscaled layout for {Screen.width}x{Screen.height}");
            }

            transform.localScale = layout.LocalScale;

            if (buttonLayerObj != null)
            {
                buttonLayerObj.transform.localPosition = Vector3.zero;
                buttonLayerObj.transform.localScale = Vector3.one;
                buttonLayerObj.transform.SetAsLastSibling();

                UIPanel buttonPanel = buttonLayerObj.GetComponent<UIPanel>();
                if (buttonPanel != null)
                {
                    buttonPanel.depth = JOIN_BUTTON_PANEL_DEPTH;
                    buttonPanel.SortWidgets();
                }
            }

            if (titleBannerObj != null)
            {
                titleBannerObj.transform.localPosition = layout.TitlePosition;
            }

            if (serverBrowser != null)
            {
                serverBrowser.transform.localPosition = layout.ServerBrowserPosition;
                serverBrowser.ApplyResolutionLayout();
            }

            float buttonY = GetButtonY(layout);
            float buttonSideOffset = GetButtonSideOffset(layout);

            if (backButtonObj != null)
            {
                backButtonObj.transform.localPosition = new Vector3(-buttonSideOffset, buttonY, 0f);
                SetButtonWidgetDepth(backButtonObj, JOIN_BUTTON_WIDGET_DEPTH);
            }

            if (refreshButtonObj != null)
            {
                refreshButtonObj.transform.localPosition = new Vector3(0f, buttonY, 0f);
                SetButtonWidgetDepth(refreshButtonObj, JOIN_BUTTON_WIDGET_DEPTH);
            }

            if (connectButtonObj != null)
            {
                connectButtonObj.transform.localPosition = new Vector3(buttonSideOffset, buttonY, 0f);
                SetButtonWidgetDepth(connectButtonObj, JOIN_BUTTON_WIDGET_DEPTH);
            }

            CoopMod.Logger.LogInfo(
                $"[JoinGameGUI] Applied layout {layout.Name}: " +
                $"browser={layout.ServerBrowserPosition}, buttonsY={buttonY}, buttonOffset={buttonSideOffset}, " +
                $"buttonDepth={JOIN_BUTTON_PANEL_DEPTH}/{JOIN_BUTTON_WIDGET_DEPTH}, backScreen={GetWidgetScreenRect(backButtonWidget)}");
        }

        private float GetButtonY(JoinGameResolutionLayout layout)
        {
            if (!layout.AnchorButtonsToBrowser || serverBrowser == null || backButtonWidget == null)
                return layout.BottomButtonY;

            if (!TryGetServerBrowserBottom(out float browserBottom))
                return layout.BottomButtonY;

            float buttonHeight = GetWidgetHeightInLocalSpace(backButtonWidget);
            if (buttonHeight <= 0f)
                return layout.BottomButtonY;

            return browserBottom - layout.BrowserButtonGap - buttonHeight * 0.5f;
        }

        private float GetButtonSideOffset(JoinGameResolutionLayout layout)
        {
            if (serverBrowser == null || backButtonWidget == null ||
                !TryGetServerBrowserHorizontalBounds(out float browserMinX, out float browserMaxX))
            {
                return layout.SideButtonOffset;
            }

            float buttonWidth = GetWidgetWidthInLocalSpace(backButtonWidget);
            float availableOffset = (browserMaxX - browserMinX) * 0.5f - buttonWidth * 0.5f - 16f;
            float minimumOffset = buttonWidth + 12f;
            if (availableOffset < minimumOffset)
                return Mathf.Max(0f, availableOffset);

            return Mathf.Min(layout.SideButtonOffset, availableOffset);
        }

        private bool TryGetServerBrowserHorizontalBounds(out float minX, out float maxX)
        {
            minX = 0f;
            maxX = 0f;
            if (serverBrowser == null)
                return false;

            bool found = false;
            foreach (var widget in serverBrowser.GetComponentsInChildren<UIWidget>(true))
            {
                if (widget == null || !widget.enabled || !widget.gameObject.activeInHierarchy)
                    continue;
                if (widget is UILabel || Mathf.Abs(widget.width * widget.height) < 100)
                    continue;

                Vector3[] corners = widget.worldCorners;
                for (int i = 0; i < corners.Length; i++)
                {
                    float x = transform.InverseTransformPoint(corners[i]).x;
                    if (!found)
                    {
                        minX = x;
                        maxX = x;
                        found = true;
                    }
                    else
                    {
                        minX = Mathf.Min(minX, x);
                        maxX = Mathf.Max(maxX, x);
                    }
                }
            }

            return found;
        }

        private bool TryGetServerBrowserBottom(out float bottom)
        {
            bottom = 0f;
            if (serverBrowser == null)
                return false;

            bool found = false;
            float minY = 0f;
            foreach (var widget in serverBrowser.GetComponentsInChildren<UIWidget>(true))
            {
                if (widget == null || !widget.enabled || !widget.gameObject.activeInHierarchy)
                    continue;

                // Labels and tiny widgets should not define the browser frame.
                if (widget is UILabel || Mathf.Abs(widget.width * widget.height) < 100)
                    continue;

                Vector3[] corners = widget.worldCorners;
                for (int i = 0; i < corners.Length; i++)
                {
                    float y = transform.InverseTransformPoint(corners[i]).y;
                    if (!found || y < minY)
                    {
                        minY = y;
                        found = true;
                    }
                }
            }

            bottom = minY;
            return found;
        }

        private float GetWidgetHeightInLocalSpace(UIWidget widget)
        {
            if (widget == null)
                return 0f;

            Vector3[] corners = widget.worldCorners;
            float minY = transform.InverseTransformPoint(corners[0]).y;
            float maxY = minY;
            for (int i = 1; i < corners.Length; i++)
            {
                float y = transform.InverseTransformPoint(corners[i]).y;
                minY = Mathf.Min(minY, y);
                maxY = Mathf.Max(maxY, y);
            }

            return Mathf.Abs(maxY - minY);
        }

        private float GetWidgetWidthInLocalSpace(UIWidget widget)
        {
            if (widget == null)
                return 0f;

            Vector3[] corners = widget.worldCorners;
            float minX = transform.InverseTransformPoint(corners[0]).x;
            float maxX = minX;
            for (int i = 1; i < corners.Length; i++)
            {
                float x = transform.InverseTransformPoint(corners[i]).x;
                minX = Mathf.Min(minX, x);
                maxX = Mathf.Max(maxX, x);
            }

            return Mathf.Abs(maxX - minX);
        }

        private string GetWidgetScreenRect(UIWidget widget)
        {
            if (widget == null || uiCamera == null)
                return "n/a";

            Vector3[] corners = widget.worldCorners;
            Vector3 min = uiCamera.WorldToScreenPoint(corners[0]);
            Vector3 max = uiCamera.WorldToScreenPoint(corners[2]);
            return $"{Mathf.RoundToInt(Mathf.Min(min.x, max.x))},{Mathf.RoundToInt(Mathf.Min(min.y, max.y))}-" +
                   $"{Mathf.RoundToInt(Mathf.Max(min.x, max.x))},{Mathf.RoundToInt(Mathf.Max(min.y, max.y))}";
        }

        private static JoinGameResolutionLayout GetActiveResolutionLayout()
        {
            int screenWidth = Screen.width;
            int screenHeight = Screen.height;
            for (int i = 0; i < ResolutionLayouts.Length; i++)
            {
                JoinGameResolutionLayout layout = ResolutionLayouts[i];
                if (layout.Matches(screenWidth, screenHeight))
                    return layout;
            }

            return DefaultResolutionLayout;
        }

        private void OnBackButtonPressed()
        {
            CoopMod.Logger.LogInfo("Back button pressed");
            Hide(true);
            
            // Go back to multiplayer menu
            if (MultiplayerSubMenuGUI.Instance != null)
            {
                MultiplayerSubMenuGUI.Instance.Open();
            }
            else
            {
                // Fallback to main menu
                if (GUIElements.me?.main_menu != null)
                {
                    GUIElements.me.main_menu.Open(false);
                }
            }
        }

        private void OnRefreshButtonPressed()
        {
            CoopMod.Logger.LogInfo("Refresh button pressed");
            Sounds.OnGUIClick();
            serverBrowser?.RefreshServerList();
        }

        private void OnConnectButtonPressed()
        {
            CoopMod.Logger.LogInfo("Connect button pressed");
            Sounds.OnGUIClick();
            serverBrowser?.TryJoinSelected();
        }

        public new void Open()
        {
            CoopMod.Logger.LogInfo("Opening JoinGameGUI");

            // Hide main menu
            if (GUIElements.me?.main_menu != null)
            {
                GUIElements.me.main_menu.Hide(true);
            }

            // Show our screen
            gameObject.SetActive(true);
            base.Open();
            ConfigureGamepadNavigation();
            ApplyResolutionLayout();

            // Force refresh the title text to ensure it's correct
            RefreshTitleText();

            // Refresh server browser to show friends hosting games
            if (serverBrowser != null)
            {
                serverBrowser.RefreshServerList();
            }

            // IP input starts blank - player must type their own IP
            // (Gerry will appear when they start typing)

            // TEST: Uncomment to test Gerry without Steam invites
            /*
            if (gerryInviteNotifier != null)
            {
                gerryInviteNotifier.ShowInvitation(
                    inviterName: "TestPlayer",
                    onAcceptCallback: () => CoopMod.Logger.LogInfo("TEST: Invitation accepted!"),
                    onDeclineCallback: () => CoopMod.Logger.LogInfo("TEST: Invitation declined!")
                );
            }
            */

            CoopMod.Logger.LogInfo("JoinGameGUI opened");
        }

        /// <summary>
        /// Shows a Steam invitation from a friend via Gerry
        /// </summary>
        public void ShowInvitation(string inviterName, System.Action onAcceptCallback, System.Action onDeclineCallback)
        {
            CoopMod.Logger.LogInfo("[UI] ========== JoinGameGUI.ShowInvitation() ==========");
            CoopMod.Logger.LogInfo($"[UI] Inviter: {inviterName}");
            CoopMod.Logger.LogInfo($"[UI] gerryInviteNotifier null? {gerryInviteNotifier == null}");
            
            // Hide the server browser so dialogue is visible
            SetServerBrowserVisible(false);
            
            if (gerryInviteNotifier != null)
            {
                CoopMod.Logger.LogInfo($"[UI] Passing invitation to GerryInviteNotifier...");
                gerryInviteNotifier.ShowInvitation(inviterName, onAcceptCallback, onDeclineCallback);
            }
            else
            {
                CoopMod.Logger.LogError("[UI] Cannot show invitation - Gerry invite notifier is null!");
            }
        }

        /// <summary>
        /// Shows Gerry's invitation context and incompatibility explanation without
        /// presenting Join or Decline actions.
        /// </summary>
        public void ShowIncompatibleInvitation(string inviterName, string message)
        {
            CoopMod.Logger.LogInfo("[UI] Showing incompatible invitation through Gerry");
            SetServerBrowserVisible(false);

            if (gerryInviteNotifier != null)
            {
                gerryInviteNotifier.ShowCompatibilityNotice(inviterName, message);
            }
            else
            {
                CoopMod.Logger.LogError(
                    "[UI] Cannot show incompatible invitation - Gerry invite notifier is null!");
                SetServerBrowserVisible(true);
                GUIElements.me?.dialog?.OpenOK(message);
            }
        }

        private void RefreshTitleText()
        {
            // Find all labels in the title and force them to show "JOIN A GAME"
            if (titleLabel != null)
            {
                var titleObj = titleLabel.gameObject;
                while (titleObj.transform.parent != null && titleObj.name != "JoinGameTitle")
                {
                    titleObj = titleObj.transform.parent.gameObject;
                }

                var allLabels = titleObj.GetComponentsInChildren<UILabel>(true);
                CoopMod.Logger.LogInfo($"Refreshing {allLabels.Length} title labels");
                
                foreach (var label in allLabels)
                {
                    CoopMod.Logger.LogInfo($"  Label '{label.text}' -> 'JOIN A GAME'");
                    label.text = "JOIN A GAME";
                    label.enabled = true;
                    label.MarkAsChanged();
                }
            }
        }

        public override void Hide(bool play_sound = true)
        {
            CoopMod.Logger.LogInfo("Hiding JoinGameGUI");
            
            // Clean up both Gerry instances and any active dialogue when closing the screen
            if (gerryInviteNotifier != null)
            {
                gerryInviteNotifier.HideGerry();
            }
            
            if (gerryJoinNotifier != null)
            {
                gerryJoinNotifier.HideGerry();
            }
            
            base.Hide(play_sound);
            gameObject.SetActive(false);
        }

        /// <summary>
        /// Shows or hides the server browser (used when showing Gerry's dialogue)
        /// </summary>
        public void SetServerBrowserVisible(bool visible)
        {
            if (serverBrowser != null && serverBrowser.gameObject != null)
            {
                serverBrowser.gameObject.SetActive(visible);
                CoopMod.Logger.LogInfo($"[UI] Server browser visibility set to: {visible}");
            }
        }

        private new void Update()
        {
            // Call base Update first
            base.Update();
            
            // Manual click detection for Back button (same pattern as lobby)
            if (Input.GetMouseButtonDown(0) && uiCamera != null)
            {
                Vector3 mousePos = Input.mousePosition;
                
                // Check Back button click
                if (backButtonWidget != null)
                {
                    Vector3[] corners = backButtonWidget.worldCorners;
                    Vector2 min = uiCamera.WorldToScreenPoint(corners[0]);
                    Vector2 max = uiCamera.WorldToScreenPoint(corners[2]);
                    
                    float minX = Mathf.Min(min.x, max.x);
                    float maxX = Mathf.Max(min.x, max.x);
                    float minY = Mathf.Min(min.y, max.y);
                    float maxY = Mathf.Max(min.y, max.y);
                    
                    if (mousePos.x >= minX && mousePos.x <= maxX && 
                        mousePos.y >= minY && mousePos.y <= maxY)
                    {
                        CoopMod.Logger.LogInfo("Back button clicked!");
                        OnBackButtonPressed();
                        return;
                    }
                }

                // Check Refresh button click
                if (refreshButtonWidget != null && WidgetContainsScreenPoint(refreshButtonWidget, mousePos))
                {
                    CoopMod.Logger.LogInfo("Refresh button clicked!");
                    OnRefreshButtonPressed();
                    return;
                }

                // Check Connect button click
                if (connectButtonWidget != null && WidgetContainsScreenPoint(connectButtonWidget, mousePos))
                {
                    CoopMod.Logger.LogInfo("Connect button clicked!");
                    OnConnectButtonPressed();
                    return;
                }
            }
        }

        protected override bool OnPressedBack()
        {
            OnBackButtonPressed();
            return true;
        }

        protected override bool OnPressedPrevTab()
        {
            serverBrowser?.SwitchRelativeTab(-1);
            return serverBrowser != null;
        }

        protected override bool OnPressedNextTab()
        {
            serverBrowser?.SwitchRelativeTab(1);
            return serverBrowser != null;
        }

        public override void OnClosePressed()
        {
            OnBackButtonPressed();
        }

        private void OnDestroy()
        {
            if (serverBrowser != null)
                serverBrowser.ControllerItemsChanged -= RefreshGamepadNavigation;
            if (_instance == this)
                _instance = null;
        }

        /// <summary>
        /// Helper: hit-test a UIWidget against a screen-space point using its world corners.
        /// </summary>
        private bool WidgetContainsScreenPoint(UIWidget widget, Vector3 screenPoint)
        {
            if (widget == null || uiCamera == null) return false;
            Vector3[] corners = widget.worldCorners;
            Vector2 a = uiCamera.WorldToScreenPoint(corners[0]);
            Vector2 b = uiCamera.WorldToScreenPoint(corners[2]);
            float minX = Mathf.Min(a.x, b.x);
            float maxX = Mathf.Max(a.x, b.x);
            float minY = Mathf.Min(a.y, b.y);
            float maxY = Mathf.Max(a.y, b.y);
            return screenPoint.x >= minX && screenPoint.x <= maxX
                && screenPoint.y >= minY && screenPoint.y <= maxY;
        }
    }
}

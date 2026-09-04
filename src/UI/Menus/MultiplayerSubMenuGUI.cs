using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// Simple multiplayer submenu that shows over the main menu
    /// </summary>
    public class MultiplayerSubMenuGUI : BaseMenuGUI
    {
        private static MultiplayerSubMenuGUI _instance;
        public static MultiplayerSubMenuGUI Instance => _instance;

        private MenuItemGUI hostButton;
        private MenuItemGUI joinButton;
        private MenuItemGUI settingsButton;
        private MenuItemGUI backButton;
        private SimpleUITable buttonsTable;

        public static MultiplayerSubMenuGUI Create(MainMenuGUI mainMenu)
        {
            if (_instance != null)
                return _instance;

            CoopMod.Logger.LogInfo("Creating MultiplayerSubMenuGUI...");

            // Find the play button from main menu to use as template
            Transform playButtonTransform = null;
            foreach (Transform child in mainMenu.buttons_table.transform)
            {
                if (child.name.Contains("play") || child.name.Contains("Play"))
                {
                    playButtonTransform = child;
                    break;
                }
            }

            if (playButtonTransform == null)
            {
                CoopMod.Logger.LogError("Could not find play button template!");
                return null;
            }

            // Create a new GameObject for our submenu
            var menuObj = new GameObject("MultiplayerSubMenuGUI");
            _instance = menuObj.AddComponent<MultiplayerSubMenuGUI>();
            var navigationController = menuObj.AddComponent<GamepadNavigationController>();
            navigationController.auto_select = true;
            navigationController.vertical_settings = new GamepadNavigationSettings();
            navigationController.horizontal_settings = new GamepadNavigationSettings();
            Object.DontDestroyOnLoad(menuObj);

            // Create a plain GameObject parent for buttons (NO layout components)
            var tableObj = new GameObject("buttons_table");
            tableObj.layer = 13; // NGUI layer
            tableObj.transform.SetParent(menuObj.transform);
            tableObj.transform.localPosition = Vector3.zero;
            tableObj.transform.localScale = Vector3.one;
            
            // Store the transform (not a layout component)
            _instance.buttonsTable = tableObj.AddComponent<SimpleUITable>();
            _instance.buttonsTable.enabled = false; // Keep disabled permanently
            
            CoopMod.Logger.LogInfo("Created button container (manual positioning only)");

            // Get the template button
            var templateButton = playButtonTransform.GetComponent<MenuItemGUI>();

            // Create buttons (manual positioning will happen in CreateButton)
            _instance.hostButton = _instance.CreateButton(templateButton, "Host Game", _instance.OnHostPressed);
            _instance.joinButton = _instance.CreateButton(templateButton, "Join Game", _instance.OnJoinPressed);
            _instance.settingsButton = _instance.CreateButton(templateButton, "Settings", _instance.OnSettingsPressed);
            _instance.backButton = _instance.CreateButton(templateButton, "Back", _instance.OnBackPressed);
            
            // NOTE: Do NOT call Reposition() here - it would reset our manual positions
            CoopMod.Logger.LogInfo($"Created {_instance.buttonsTable.transform.childCount} buttons with manual spacing");

            // Initialize the base menu
            _instance.Init();
            _instance.ConfigureGamepadNavigation();

            // Start hidden
            menuObj.SetActive(false);

            CoopMod.Logger.LogInfo("MultiplayerSubMenuGUI created successfully!");
            return _instance;
        }

        private MenuItemGUI CreateButton(MenuItemGUI template, string text, System.Action callback)
        {
            var buttonObj = Object.Instantiate(template.gameObject, buttonsTable.transform);
            buttonObj.name = $"btn_{text.ToLower().Replace(" ", "_")}";
            
            // Manually position each button with spacing
            int buttonIndex = buttonsTable.transform.childCount - 1;
            float yOffset = -buttonIndex * 50f; // 50 pixels spacing between each button
            buttonObj.transform.localPosition = new Vector3(0, yOffset, 0);
            buttonObj.transform.localScale = Vector3.one;
            CoopMod.Logger.LogInfo($"  Button '{text}' positioned at: {buttonObj.transform.localPosition}, index: {buttonIndex}");

            var menuItem = buttonObj.GetComponent<MenuItemGUI>();
            if (menuItem == null)
            {
                CoopMod.Logger.LogError($"Failed to get MenuItemGUI from button {text}");
                return null;
            }

            // The template's LocalizedLabel still points at the base game's "play"
            // token. MenuItemGUI.Init and BaseGUI.Open both localize it again, which
            // overwrites custom text—most visibly when controller opening takes the
            // native BaseMenuGUI path.
            var localizedLabels = buttonObj.GetComponentsInChildren<LocalizedLabel>(true);
            foreach (var localizedLabel in localizedLabels)
            {
                localizedLabel.enabled = false;
                Object.DestroyImmediate(localizedLabel);
            }
            menuItem.locale_token = "";

            // Change label text - need to do this properly for NGUI
            var labels = buttonObj.GetComponentsInChildren<UILabel>(true);
            CoopMod.Logger.LogInfo($"Creating button '{text}', found {labels.Length} labels");
            
            foreach (var label in labels)
            {
                CoopMod.Logger.LogInfo($"  Label before: '{label.text}'");
                label.text = text;
                // Force NGUI to update the label
                label.MarkAsChanged();
                CoopMod.Logger.LogInfo($"  Label after: '{label.text}'");
            }

            // Set up the callback
            menuItem.on_pressed = new EventDelegate(() =>
            {
                CoopMod.Logger.LogInfo($"{text} button pressed");
                callback?.Invoke();
            });

            buttonObj.SetActive(true);
            return menuItem;
        }

        private void ConfigureGamepadNavigation()
        {
            MenuItemGUI[] orderedButtons = { hostButton, joinButton, settingsButton, backButton };
            for (int i = 0; i < orderedButtons.Length; i++)
            {
                var item = orderedButtons[i];
                var navigationItem = item?.gamepad_item;
                if (navigationItem == null)
                {
                    CoopMod.Logger.LogWarning(
                        $"Multiplayer submenu button {i} has no GamepadNavigationItem");
                    continue;
                }

                navigationItem.active = true;
                navigationItem.SetCustomDirectionItem(null, Direction.Left);
                navigationItem.SetCustomDirectionItem(null, Direction.Right);
                navigationItem.SetCustomDirectionItem(
                    orderedButtons[(i + orderedButtons.Length - 1) % orderedButtons.Length]?.gamepad_item,
                    Direction.Up);
                navigationItem.SetCustomDirectionItem(
                    orderedButtons[(i + 1) % orderedButtons.Length]?.gamepad_item,
                    Direction.Down);
            }
        }

        public new void Open()
        {
            CoopMod.Logger.LogInfo("Opening MultiplayerSubMenuGUI");

            // Hide main menu
            if (GUIElements.me?.main_menu != null)
            {
                GUIElements.me.main_menu.Hide(true);
            }

            // Show our menu
            gameObject.SetActive(true);
            base.Open();

            // CRITICAL: base.Open() repositions buttons, so we must fix positions AFTER it
            // Testing 25px spacing to match main menu visual appearance
            if (buttonsTable != null)
            {
                int index = 0;
                foreach (Transform child in buttonsTable.transform)
                {
                    float yOffset = -index * 25f; // 25px spacing
                    child.localPosition = new Vector3(0, yOffset, 0);
                    CoopMod.Logger.LogInfo($"  Re-positioned {child.name}: {child.localPosition}");
                    index++;
                }
            }

            // Fix button text AFTER opening (base.Open() calls Show() which might reset text)
            FixButtonText(hostButton, "Host Game");
            FixButtonText(joinButton, "Join Game");
            FixButtonText(settingsButton, "Settings");
            FixButtonText(backButton, "Back");

            // NOTE: Do NOT reposition - we're using manual positioning
            // Repositioning would reset all our manual localPosition values

            CoopMod.Logger.LogInfo("MultiplayerSubMenuGUI opened");
        }

        private void FixButtonText(MenuItemGUI button, string text)
        {
            if (button == null) return;

            var labels = button.GetComponentsInChildren<UILabel>(true);
            foreach (var label in labels)
            {
                label.text = text;
                label.MarkAsChanged();
            }
        }

        public override void Hide(bool play_sound = true)
        {
            CoopMod.Logger.LogInfo("Hiding MultiplayerSubMenuGUI");
            base.Hide(play_sound);
            gameObject.SetActive(false);
        }

        public override void UpdatTip(bool select_active)
        {
            // This lightweight submenu does not clone the main menu's ButtonTipsStr.
            // BaseMenuGUI assumes one exists when controller focus changes.
            if (button_tips != null)
            {
                base.UpdatTip(select_active);
            }
        }

        private void OnHostPressed()
        {
            CoopMod.Logger.LogInfo("Host Game selected");
            ModConfig.IsHost.Value = true;
            
            Hide(true);

            // Open lobby. LobbyGUI initializes the session and creates the Steam lobby for host mode.
            CoopMod.Logger.LogInfo($"LobbyGUI.Instance is null? {LobbyGUI.Instance == null}");
            if (LobbyGUI.Instance != null)
            {
                CoopMod.Logger.LogInfo("Opening LobbyGUI...");
                LobbyGUI.Instance.Open();
            }
            else
            {
                CoopMod.Logger.LogError("LobbyGUI Instance is null! Attempting to create it now...");
                var lobby = LobbyGUI.Create();
                if (lobby != null)
                {
                    lobby.Open();
                }
                else
                {
                    CoopMod.Logger.LogError("Failed to create LobbyGUI!");
                }
            }
        }

        private void OnJoinPressed()
        {
            CoopMod.Logger.LogInfo("Join Game selected");
            Hide(true);

            // Create JoinGameGUI if it doesn't exist
            if (JoinGameGUI.Instance == null)
            {
                JoinGameGUI.Create();
            }

            // Open the join game screen
            if (JoinGameGUI.Instance != null)
            {
                JoinGameGUI.Instance.Open();
            }
            else
            {
                CoopMod.Logger.LogError("Failed to create JoinGameGUI!");
            }
        }

        private void OnSettingsPressed()
        {
            CoopMod.Logger.LogInfo("Settings selected");
            
            // Hide this menu
            Hide(true);
            
            // Create native-style settings GUI if it doesn't exist
            if (MultiplayerSettingsGUI.Instance == null)
            {
                MultiplayerSettingsGUI.CreateInstance();
            }
            
            // Open settings
            if (MultiplayerSettingsGUI.Instance != null)
            {
                MultiplayerSettingsGUI.Instance.Open();
            }
            else
            {
                CoopMod.Logger.LogError("Failed to create MultiplayerSettingsGUI!");
            }
        }

        private void OnBackPressed()
        {
            CoopMod.Logger.LogInfo("Back to main menu");
            Hide(true);

            if (GUIElements.me?.main_menu != null)
            {
                GUIElements.me.main_menu.Open(false);
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

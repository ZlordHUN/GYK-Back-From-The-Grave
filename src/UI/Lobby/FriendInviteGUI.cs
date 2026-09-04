using System.Collections.Generic;
using UnityEngine;
using Steamworks;
using GraveyardKeeperCoop.Network;
using GraveyardKeeperCoop.Utils;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// Friend invite screen - shows Steam friends list for inviting to lobby
    /// Clones SaveSlotsMenuGUI structure for consistent UI
    /// </summary>
    public class FriendInviteGUI : BaseMenuGUI
    {
        private static FriendInviteGUI _instance;
        public static FriendInviteGUI Instance => _instance;

        private UIScrollView scrollView;
        private UIPanel scrollPanel;
        private UITable friendTable;
        private GameObject backButtonObj;
        private UIWidget backButtonWidget;
        private GameObject inviteButtonObj;
        private UIWidget inviteButtonWidget;
        private GameObject refreshButtonObj;
        private UIWidget refreshButtonWidget;
        private Camera uiCamera;
        private GamepadNavigationItem backNavigationItem;
        private GamepadNavigationItem refreshNavigationItem;
        private GamepadNavigationItem inviteNavigationItem;
        private Coroutine pendingFriendLoad;
        private Coroutine pendingResolutionLayoutRefresh;
        private int friendLoadGeneration;
        private int appliedLayoutWidth = -1;
        private int appliedLayoutHeight = -1;
        private static FriendInviteResolutionLayout cachedLowResolutionLayout;

        private List<CSteamID> friendSteamIDs = new List<CSteamID>();
        private CSteamID selectedFriendID = CSteamID.Nil;
        
        // Cache the friend item template to avoid finding it every refresh
        private GameObject friendItemTemplate;
        
        // Track friend items for highlighting
        private List<GameObject> friendItems = new List<GameObject>();
        
        // Cache the gamepad_frame sprite for selection highlighting
        private Sprite cachedFrameSprite;
        
        // Selection frame color (golden outline)
        private static readonly Color SELECTION_FRAME_COLOR = new Color(1f, 0.843f, 0f, 1f); // Gold color
        private const int DEFAULT_FRAME_SIDE_EXPANSION = 180;
        private const int DEFAULT_FRAME_HEIGHT_EXPANSION = 25;
        private static readonly FriendInviteResolutionLayout DefaultResolutionLayout = new FriendInviteResolutionLayout(
            "Default",
            0,
            0,
            new Vector3(0f, 50f, 0f),
            Vector3.one,
            DEFAULT_FRAME_SIDE_EXPANSION,
            DEFAULT_FRAME_HEIGHT_EXPANSION,
            3,
            new Vector2(10f, 10f),
            Vector3.one,
            new Vector3(-150f, 40f, 0f),
            new Vector3(-100f, -270f, 0f),
            new Vector3(0f, -270f, 0f),
            new Vector3(100f, -270f, 0f));
        private static FriendInviteResolutionLayout CreateScaledResolutionLayout(
            string name,
            int width,
            int height,
            float rootScale,
            float rootY)
        {
            return new FriendInviteResolutionLayout(
                name,
                width,
                height,
                new Vector3(0f, rootY, 0f),
                new Vector3(rootScale, rootScale, 1f),
                DEFAULT_FRAME_SIDE_EXPANSION,
                DEFAULT_FRAME_HEIGHT_EXPANSION,
                3,
                new Vector2(10f, 10f),
                Vector3.one,
                new Vector3(-150f, 40f, 0f),
                new Vector3(-100f, -270f, 0f),
                new Vector3(0f, -270f, 0f),
                new Vector3(100f, -270f, 0f));
        }

        private static readonly FriendInviteResolutionLayout[] ResolutionLayouts =
        {
            CreateScaledResolutionLayout("1366x768", 1366, 768, 0.70f, 25f),
            CreateScaledResolutionLayout("1440x900", 1440, 900, 0.78f, 30f),
            CreateScaledResolutionLayout("1600x900", 1600, 900, 0.78f, 30f),
            CreateScaledResolutionLayout("1920x800", 1920, 800, 0.70f, 25f),
            CreateScaledResolutionLayout("1920x1080", 1920, 1080, 0.86f, 75f),
            CreateScaledResolutionLayout("1920x1200", 1920, 1200, 0.90f, 60f),
            CreateScaledResolutionLayout("1920x1280", 1920, 1280, 0.94f, 55f),
            CreateScaledResolutionLayout("1920x1440", 1920, 1440, 1.00f, 50f),
            CreateScaledResolutionLayout("2048x1152", 2048, 1152, 0.90f, 65f),
            CreateScaledResolutionLayout("2048x1536", 2048, 1536, 1.00f, 50f),
            CreateScaledResolutionLayout("2560x1080", 2560, 1080, 0.86f, 75f),
            CreateScaledResolutionLayout("1600x1200", 1600, 1200, 0.90f, 60f),
            CreateScaledResolutionLayout("1680x1050", 1680, 1050, 0.84f, 55f),
            CreateScaledResolutionLayout("1440x960", 1440, 960, 0.80f, 45f),
            CreateScaledResolutionLayout("1440x1080", 1440, 1080, 0.84f, 55f),
            CreateScaledResolutionLayout("1400x1050", 1400, 1050, 0.82f, 55f)
        };

        private sealed class FriendInviteResolutionLayout
        {
            public readonly string Name;
            public readonly int ScreenWidth;
            public readonly int ScreenHeight;
            public readonly Vector3 RootPosition;
            public readonly Vector3 RootScale;
            public readonly int FrameSideExpansion;
            public readonly int FrameHeightExpansion;
            public readonly int TableColumns;
            public readonly Vector2 TablePadding;
            public readonly Vector3 FriendItemScale;
            public readonly Vector3 TableOffset;
            public readonly Vector3 BackButtonPosition;
            public readonly Vector3 RefreshButtonPosition;
            public readonly Vector3 InviteButtonPosition;

            public FriendInviteResolutionLayout(
                string name,
                int screenWidth,
                int screenHeight,
                Vector3 rootPosition,
                Vector3 rootScale,
                int frameSideExpansion,
                int frameHeightExpansion,
                int tableColumns,
                Vector2 tablePadding,
                Vector3 friendItemScale,
                Vector3 tableOffset,
                Vector3 backButtonPosition,
                Vector3 refreshButtonPosition,
                Vector3 inviteButtonPosition)
            {
                Name = name;
                ScreenWidth = screenWidth;
                ScreenHeight = screenHeight;
                RootPosition = rootPosition;
                RootScale = rootScale;
                FrameSideExpansion = frameSideExpansion;
                FrameHeightExpansion = frameHeightExpansion;
                TableColumns = tableColumns;
                TablePadding = tablePadding;
                FriendItemScale = friendItemScale;
                TableOffset = tableOffset;
                BackButtonPosition = backButtonPosition;
                RefreshButtonPosition = refreshButtonPosition;
                InviteButtonPosition = inviteButtonPosition;
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

        public static FriendInviteGUI Create()
        {
            if (_instance != null)
                return _instance;

            CoopMod.Logger.LogInfo("Creating FriendInviteGUI...");

            // Find SaveSlotsMenuGUI to clone from
            var saveSlotsMenu = Object.FindObjectOfType<SaveSlotsMenuGUI>(true);
            if (saveSlotsMenu == null)
            {
                CoopMod.Logger.LogError("Cannot find SaveSlotsMenuGUI to clone!");
                return null;
            }

            // Clone the save slots menu
            GameObject menuObj = Object.Instantiate(saveSlotsMenu.gameObject);
            menuObj.name = "FriendInviteGUI";
            menuObj.SetActive(false);

            // Find UIRoot to parent to
            UIRoot uiRoot = Object.FindObjectOfType<UIRoot>();
            if (uiRoot != null)
            {
                menuObj.transform.SetParent(uiRoot.transform, false);
                menuObj.transform.localPosition = Vector3.zero;
                menuObj.transform.localScale = Vector3.one;
            }

            // Remove SaveSlotsMenuGUI component
            var saveSlotsComponent = menuObj.GetComponent<SaveSlotsMenuGUI>();
            if (saveSlotsComponent != null)
            {
                Object.DestroyImmediate(saveSlotsComponent);
            }

            // Clean up any stale ModEntry_ objects that leaked from Mods mode
            var allTransforms = menuObj.GetComponentsInChildren<Transform>(true);
            foreach (var t in allTransforms)
            {
                if (t != null && t.name.StartsWith("ModEntry_"))
                {
                    CoopMod.Logger.LogInfo($"[FriendInviteGUI] Removing stale mod entry: {t.name}");
                    Object.DestroyImmediate(t.gameObject);
                }
            }

            // Add our component
            _instance = menuObj.AddComponent<FriendInviteGUI>();
            _instance.add_to_opened_stack = true;
            var navigationController = menuObj.GetComponent<GamepadNavigationController>() ??
                                       menuObj.AddComponent<GamepadNavigationController>();
            navigationController.auto_select = true;
            navigationController.vertical_settings = new GamepadNavigationSettings();
            navigationController.horizontal_settings = new GamepadNavigationSettings();

            // Initialize from cloned menu
            _instance.InitializeFromClone(menuObj);

            // Keep alive across scenes
            Object.DontDestroyOnLoad(menuObj);

            CoopMod.Logger.LogInfo("FriendInviteGUI created successfully!");
            return _instance;
        }

        private void InitializeFromClone(GameObject clonedMenu)
        {
            CoopMod.Logger.LogInfo("Initializing FriendInviteGUI from cloned SaveSlotsMenuGUI...");

            FriendInviteResolutionLayout layout = GetActiveResolutionLayout();

            // Move and scale the cloned menu per resolution while preserving the default layout.
            clonedMenu.transform.localPosition = layout.RootPosition;
            clonedMenu.transform.localScale = layout.RootScale;
            CoopMod.Logger.LogInfo($"Repositioned menu to: {clonedMenu.transform.localPosition}");

            // Find the main panel/widget and expand it
            var mainPanel = clonedMenu.GetComponent<UIPanel>();
            if (mainPanel != null)
            {
                // Increase width more, but keep height reasonable
                var currentClip = mainPanel.baseClipRegion;
                mainPanel.baseClipRegion = new Vector4(currentClip.x, currentClip.y, currentClip.z + layout.FrameSideExpansion, currentClip.w + layout.FrameHeightExpansion);
                CoopMod.Logger.LogInfo($"Expanded main panel clip region: {mainPanel.baseClipRegion}");
            }

            // Find all UI2DSprite components (these are the visible backgrounds/frames)
            var sprites = clonedMenu.GetComponentsInChildren<UI2DSprite>(true);
            foreach (var sprite in sprites)
            {
                // Expand any background or frame sprites
                if (sprite.width > 200 && sprite.height > 100) // Only expand larger sprites (backgrounds)
                {
                    int oldWidth = sprite.width;
                    int oldHeight = sprite.height;
                    sprite.width += layout.FrameSideExpansion; // Horizontal expansion reduced for 1080p
                    sprite.height += layout.FrameHeightExpansion;
                    CoopMod.Logger.LogInfo($"Expanded sprite {sprite.name}: {oldWidth}x{oldHeight} -> {sprite.width}x{sprite.height}");
                }
            }

            // Find any UIWidget (background) and expand it
            var widgets = clonedMenu.GetComponentsInChildren<UIWidget>(true);
            foreach (var widget in widgets)
            {
                if ((widget.name.ToLower().Contains("background") || widget.name.ToLower().Contains("frame") || widget.name.ToLower().Contains("back")) 
                    && widget.width > 200 && widget.height > 100)
                {
                    int oldWidth = widget.width;
                    int oldHeight = widget.height;
                    widget.width += layout.FrameSideExpansion;  // Horizontal expansion reduced for 1080p
                    widget.height += layout.FrameHeightExpansion;
                    CoopMod.Logger.LogInfo($"Expanded widget {widget.name}: {oldWidth}x{oldHeight} -> {widget.width}x{widget.height}");
                }
            }

            // Find the scroll view and panel
            scrollView = clonedMenu.GetComponentInChildren<UIScrollView>(true);
            if (scrollView != null)
            {
                CoopMod.Logger.LogInfo("Found ScrollView");
                scrollPanel = scrollView.GetComponent<UIPanel>();
                
                // Expand the scroll view clip region for more visible content
                if (scrollPanel != null)
                {
                    var currentClip = scrollPanel.baseClipRegion;
                    scrollPanel.baseClipRegion = new Vector4(currentClip.x, currentClip.y, currentClip.z + layout.FrameSideExpansion, currentClip.w + layout.FrameHeightExpansion);
                    CoopMod.Logger.LogInfo($"Expanded scroll panel clip region: {scrollPanel.baseClipRegion}");
                }
            }

            // Find the UITable for arranging friend items
            friendTable = clonedMenu.GetComponentInChildren<UITable>(true);
            if (friendTable != null)
            {
                CoopMod.Logger.LogInfo("Found UITable for friend list");
                
                // Configure for horizontal wrapping grid layout
                friendTable.columns = layout.TableColumns; // 3 friends per row
                friendTable.direction = UITable.Direction.Down; // Fill rows from left to right, then down
                friendTable.sorting = UITable.Sorting.None; // Keep insertion order
                friendTable.padding = layout.TablePadding; // Space between items
                friendTable.hideInactive = true;
                
                // Move the table to center it better with grid layout
                var tableTransform = friendTable.transform;
                var currentPos = tableTransform.localPosition;
                tableTransform.localPosition = currentPos + layout.TableOffset;
                CoopMod.Logger.LogInfo($"Configured UITable for grid layout (3 columns) at position: {tableTransform.localPosition}");
            }

            // Find and update specific labels - be very careful not to clear friend names
            var labels = clonedMenu.GetComponentsInChildren<UILabel>(true);
            foreach (var label in labels)
            {
                CoopMod.Logger.LogInfo($"Found label: {label.name}, text: '{label.text}'");
                
                // Hide the main title completely
                if (label.name.ToLower().Contains("header") && label.name.ToLower().Contains("main"))
                {
                    label.gameObject.SetActive(false);
                    CoopMod.Logger.LogInfo($"Hid main title label: {label.name}");
                }
                // Update any banner that contains "pick a save slot" (case-insensitive)
                else if (!string.IsNullOrEmpty(label.text) && label.text.ToLower().Contains("pick a save slot"))
                {
                    label.text = "Select a friend to invite";
                    label.fontSize = 20;
                    label.color = Color.white;
                    label.alignment = NGUIText.Alignment.Center;
                    label.gameObject.SetActive(true); // Ensure banner is visible
                    CoopMod.Logger.LogInfo($"Replaced banner text with 'Select a friend to invite' on label: {label.name}");
                }
            }

            // Remove the close button if it exists (X in top-right corner)
            var allTransforms = clonedMenu.GetComponentsInChildren<Transform>(true);
            foreach (var t in allTransforms)
            {
                string nameLower = t.name.ToLower();
                // Check if it's a label showing "X" text (the visible X button)
                var label = t.GetComponent<UILabel>();
                if (label != null && label.text == "X")
                {
                    // Hide the entire parent button, not just the label
                    if (t.parent != null)
                    {
                        t.parent.gameObject.SetActive(false);
                        CoopMod.Logger.LogInfo($"Disabled close button parent: {t.parent.name} (had X label: {t.name})");
                    }
                    else
                    {
                        t.gameObject.SetActive(false);
                        CoopMod.Logger.LogInfo($"Disabled X label directly: {t.name}");
                    }
                }
                // Also check for close button by name
                else if (nameLower.Contains("close") || nameLower == "x")
                {
                    t.gameObject.SetActive(false);
                    CoopMod.Logger.LogInfo($"Disabled close button by name: {t.name}");
                }
            }

            // Clear existing save slot items (but cache one as a template first!)
            if (friendTable != null)
            {
                // Cache the first child as our friend item template
                if (friendTable.transform.childCount > 0)
                {
                    var firstChild = friendTable.transform.GetChild(0);
                    friendItemTemplate = Object.Instantiate(firstChild.gameObject);
                    friendItemTemplate.name = "FriendItemTemplate";
                    friendItemTemplate.SetActive(false);
                    friendItemTemplate.transform.SetParent(null); // Detach from hierarchy so it won't be destroyed
                    Object.DontDestroyOnLoad(friendItemTemplate); // Keep it alive between scenes
                    
                    // CRITICAL: Strip out SaveSlotGUI component to prevent interference with other GUIs
                    // This component might have references or callbacks that affect other SaveSlotGUI instances
                    var saveSlotGUI = friendItemTemplate.GetComponent<SaveSlotGUI>();
                    if (saveSlotGUI != null)
                    {
                        Object.DestroyImmediate(saveSlotGUI);
                        CoopMod.Logger.LogInfo("Removed SaveSlotGUI component from template to prevent cross-GUI interference");
                    }
                    
                    // Also remove any GamepadNavigationItem components as they might interfere
                    var navItem = friendItemTemplate.GetComponent<GamepadNavigationItem>();
                    if (navItem != null)
                    {
                        Object.DestroyImmediate(navItem);
                        CoopMod.Logger.LogInfo("Removed GamepadNavigationItem component from template");
                    }
                    
                    // Remove PanelAutoScroll if present
                    var autoScroll = friendItemTemplate.GetComponent<PanelAutoScroll>();
                    if (autoScroll != null)
                    {
                        Object.DestroyImmediate(autoScroll);
                        CoopMod.Logger.LogInfo("Removed PanelAutoScroll component from template");
                    }
                    
                    // CRITICAL: Remove ALL DialogButtonGUI components from template and children
                    // These can interfere with LobbyGUI's button creation via Resources.FindObjectsOfTypeAll
                    var dialogButtons = friendItemTemplate.GetComponentsInChildren<DialogButtonGUI>(true);
                    foreach (var btn in dialogButtons)
                    {
                        Object.DestroyImmediate(btn);
                    }
                    if (dialogButtons.Length > 0)
                    {
                        CoopMod.Logger.LogInfo($"Removed {dialogButtons.Length} DialogButtonGUI components from template");
                    }
                    
                    // Also remove any MenuItemGUI components
                    var menuItems = friendItemTemplate.GetComponentsInChildren<MenuItemGUI>(true);
                    foreach (var item in menuItems)
                    {
                        Object.DestroyImmediate(item);
                    }
                    if (menuItems.Length > 0)
                    {
                        CoopMod.Logger.LogInfo($"Removed {menuItems.Length} MenuItemGUI components from template");
                    }
                    
                    CoopMod.Logger.LogInfo("Cached friend item template from save slot (cleaned of interfering components)");
                }
                
                var children = new List<Transform>();
                foreach (Transform child in friendTable.transform)
                {
                    children.Add(child);
                }
                foreach (var child in children)
                {
                    Object.DestroyImmediate(child.gameObject);
                }
                CoopMod.Logger.LogInfo("Cleared existing save slot items");
            }

            // Remove navigation owned by the cloned SaveSlotsMenuGUI. Only the
            // friend cards and the three actions created below are selectable.
            foreach (var navigationItem in clonedMenu.GetComponentsInChildren<GamepadNavigationItem>(true))
                Object.DestroyImmediate(navigationItem);
            foreach (var menuItem in clonedMenu.GetComponentsInChildren<MenuItemGUI>(true))
                Object.DestroyImmediate(menuItem);

            // Create Back button
            CreateBackButton(clonedMenu.transform);

            // Create Invite button
            CreateInviteButton(clonedMenu.transform);

            // Create Refresh button
            CreateRefreshButton(clonedMenu.transform);

            ApplyResolutionLayout();

            // Find UI camera
            uiCamera = Object.FindObjectOfType<Camera>();
            if (uiCamera != null)
            {
                CoopMod.Logger.LogInfo($"UI Camera found: {uiCamera.name}");
            }

            // Initialize BaseMenuGUI
            Init();
            ConfigureGamepadNavigation();

            CoopMod.Logger.LogInfo("FriendInviteGUI initialization complete");
        }

        private void CreateBackButton(Transform parent)
        {
            // Find DialogButtonGUI to clone from
            var dialogButtons = Resources.FindObjectsOfTypeAll<DialogButtonGUI>();
            if (dialogButtons == null || dialogButtons.Length == 0)
            {
                CoopMod.Logger.LogError("Cannot find DialogButtonGUI to clone back button from!");
                return;
            }

            var template = dialogButtons[0].gameObject;

            // Create Back button
            GameObject backObj = Object.Instantiate(template, parent);
            backObj.name = "btn_back";
            backObj.transform.localPosition = GetActiveResolutionLayout().BackButtonPosition;
            backObj.transform.localScale = Vector3.one;
            backObj.layer = 13;

            var backLabel = backObj.GetComponentInChildren<UILabel>(true);
            if (backLabel != null)
            {
                backLabel.text = "Back";
            }

            // Remove DialogButtonGUI component
            var backButtonGUI = backObj.GetComponent<DialogButtonGUI>();
            if (backButtonGUI != null)
            {
                Object.Destroy(backButtonGUI);
            }

            // Remove UIButton components
            var backUIButtons = backObj.GetComponentsInChildren<UIButton>(true);
            foreach (var btn in backUIButtons)
            {
                Object.Destroy(btn);
            }

            // Store widget for manual click detection
            backButtonWidget = backObj.GetComponent<UIWidget>();
            if (backButtonWidget == null)
            {
                backButtonWidget = backObj.GetComponentInChildren<UIWidget>(true);
            }

            backButtonObj = backObj;
            backNavigationItem = ConfigureControllerButton(backObj, OnBackPressed);
            CoopMod.Logger.LogInfo($"Created Back button at {backObj.transform.localPosition}");
        }

        private void CreateInviteButton(Transform parent)
        {
            // Find DialogButtonGUI to clone from
            var dialogButtons = Resources.FindObjectsOfTypeAll<DialogButtonGUI>();
            if (dialogButtons == null || dialogButtons.Length == 0)
            {
                CoopMod.Logger.LogError("Cannot find DialogButtonGUI to clone invite button from!");
                return;
            }

            var template = dialogButtons[0].gameObject;

            // Create Invite button - position it on the right side, opposite of Back
            GameObject inviteObj = Object.Instantiate(template, parent);
            inviteObj.name = "btn_invite";
            inviteObj.transform.localPosition = GetActiveResolutionLayout().InviteButtonPosition;
            inviteObj.transform.localScale = Vector3.one;
            inviteObj.layer = 13;

            var inviteLabel = inviteObj.GetComponentInChildren<UILabel>(true);
            if (inviteLabel != null)
            {
                inviteLabel.text = "Invite";
            }

            // Remove DialogButtonGUI component
            var inviteButtonGUI = inviteObj.GetComponent<DialogButtonGUI>();
            if (inviteButtonGUI != null)
            {
                Object.Destroy(inviteButtonGUI);
            }

            // Remove UIButton components
            var inviteUIButtons = inviteObj.GetComponentsInChildren<UIButton>(true);
            foreach (var btn in inviteUIButtons)
            {
                Object.Destroy(btn);
            }

            // Store widget for manual click detection
            inviteButtonWidget = inviteObj.GetComponent<UIWidget>();
            if (inviteButtonWidget == null)
            {
                inviteButtonWidget = inviteObj.GetComponentInChildren<UIWidget>(true);
            }

            inviteButtonObj = inviteObj;
            inviteNavigationItem = ConfigureControllerButton(inviteObj, OnInvitePressed);
            CoopMod.Logger.LogInfo($"Created Invite button at {inviteObj.transform.localPosition}");
        }

        private void CreateRefreshButton(Transform parent)
        {
            // Find DialogButtonGUI to clone from
            var dialogButtons = Resources.FindObjectsOfTypeAll<DialogButtonGUI>();
            if (dialogButtons == null || dialogButtons.Length == 0)
            {
                CoopMod.Logger.LogError("Cannot find DialogButtonGUI to clone refresh button from!");
                return;
            }

            var template = dialogButtons[0].gameObject;

            // Create Refresh button - position it between Back and Invite
            GameObject refreshObj = Object.Instantiate(template, parent);
            refreshObj.name = "btn_refresh";
            refreshObj.transform.localPosition = GetActiveResolutionLayout().RefreshButtonPosition;
            refreshObj.transform.localScale = Vector3.one;
            refreshObj.layer = 13;

            var refreshLabel = refreshObj.GetComponentInChildren<UILabel>(true);
            if (refreshLabel != null)
            {
                refreshLabel.text = "Refresh";
            }

            // Remove DialogButtonGUI component
            var refreshButtonGUI = refreshObj.GetComponent<DialogButtonGUI>();
            if (refreshButtonGUI != null)
            {
                Object.Destroy(refreshButtonGUI);
            }

            // Remove UIButton components
            var refreshUIButtons = refreshObj.GetComponentsInChildren<UIButton>(true);
            foreach (var btn in refreshUIButtons)
            {
                Object.Destroy(btn);
            }

            // Store widget for manual click detection
            refreshButtonWidget = refreshObj.GetComponent<UIWidget>();
            if (refreshButtonWidget == null)
            {
                refreshButtonWidget = refreshObj.GetComponentInChildren<UIWidget>(true);
            }

            refreshButtonObj = refreshObj;
            refreshNavigationItem = ConfigureControllerButton(refreshObj, OnRefreshPressed);
            CoopMod.Logger.LogInfo($"Created Refresh button at {refreshObj.transform.localPosition}");
        }

        private GamepadNavigationItem ConfigureControllerButton(GameObject buttonObj, System.Action onPressed)
        {
            if (buttonObj == null)
                return null;

            foreach (var localizedLabel in buttonObj.GetComponentsInChildren<LocalizedLabel>(true))
            {
                localizedLabel.enabled = false;
                Object.DestroyImmediate(localizedLabel);
            }

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
            label.color = focused ? SELECTION_FRAME_COLOR : normalColor;
            label.MarkAsChanged();
        }

        private void ApplyResolutionLayout()
        {
            FriendInviteResolutionLayout layout = GetActiveResolutionLayout();
            if (layout == DefaultResolutionLayout)
            {
                CoopMod.Logger.LogInfo($"[FriendInviteGUI] Applying default layout for {Screen.width}x{Screen.height}");
            }

            transform.localPosition = layout.RootPosition;
            transform.localScale = layout.RootScale;

            if (friendTable != null)
            {
                friendTable.columns = layout.TableColumns;
                friendTable.padding = layout.TablePadding;
                foreach (Transform child in friendTable.transform)
                {
                    child.localScale = layout.FriendItemScale;
                }

                if (friendTable.transform.childCount > 0)
                {
                    friendTable.repositionNow = true;
                    RepositionFriendTable();
                }
            }

            if (backButtonObj != null)
            {
                backButtonObj.transform.localPosition = layout.BackButtonPosition;
            }

            if (refreshButtonObj != null)
            {
                refreshButtonObj.transform.localPosition = layout.RefreshButtonPosition;
            }

            if (inviteButtonObj != null)
            {
                inviteButtonObj.transform.localPosition = layout.InviteButtonPosition;
            }

            appliedLayoutWidth = Screen.width;
            appliedLayoutHeight = Screen.height;

            CoopMod.Logger.LogInfo(
                $"[FriendInviteGUI] Applied layout {layout.Name}: " +
                $"root={transform.localPosition}, scale={transform.localScale}, " +
                $"columns={layout.TableColumns}, itemScale={layout.FriendItemScale}, " +
                $"buttons={layout.BackButtonPosition}/{layout.RefreshButtonPosition}/{layout.InviteButtonPosition}");
        }

        private static FriendInviteResolutionLayout GetActiveResolutionLayout()
        {
            int screenWidth = Screen.width;
            int screenHeight = Screen.height;

            // Every resolution below the 1368x768 menu entry is at most 1280
            // pixels wide. Scale the whole cloned menu to the available viewport
            // so none of those legacy/handheld modes can fall back to the
            // oversized default frame.
            if (screenWidth <= 1280)
            {
                if (cachedLowResolutionLayout == null ||
                    !cachedLowResolutionLayout.Matches(screenWidth, screenHeight))
                {
                    float widthScale = screenWidth / 1920f;
                    float heightScale = screenHeight / 1000f;
                    float rootScale = Mathf.Clamp(Mathf.Min(widthScale, heightScale), 0.16f, 0.66f);
                    float rootY = Mathf.Clamp(15f + (screenHeight - 600f) * 0.08f, 0f, 50f);
                    cachedLowResolutionLayout = CreateScaledResolutionLayout(
                        $"LowResolution{screenWidth}x{screenHeight}",
                        screenWidth,
                        screenHeight,
                        rootScale,
                        rootY);
                }

                return cachedLowResolutionLayout;
            }

            for (int i = 0; i < ResolutionLayouts.Length; i++)
            {
                FriendInviteResolutionLayout layout = ResolutionLayouts[i];
                if (layout.Matches(screenWidth, screenHeight))
                    return layout;
            }

            return DefaultResolutionLayout;
        }

        public override void Open()
        {
            CoopMod.Logger.LogInfo("Opening FriendInviteGUI");

            gameObject.SetActive(true);
            ApplyResolutionLayout();
            QueueResolutionLayoutRefresh();

            try
            {
                base.Open();
            }
            catch (System.Exception e)
            {
                CoopMod.Logger.LogWarning($"BaseMenuGUI.Open() failed: {e.Message}, using simple open");
            }

            // Force banner text replacement after base.Open() (which might reset it)
            var labels = GetComponentsInChildren<UILabel>(true);
            foreach (var label in labels)
            {
                if (!string.IsNullOrEmpty(label.text) && label.text.ToLower().Contains("pick a save slot"))
                {
                    label.text = "Select a friend to invite";
                    label.fontSize = 20;
                    label.color = Color.white;
                    label.alignment = NGUIText.Alignment.Center;
                    CoopMod.Logger.LogInfo($"OPEN: Replaced banner text on label: {label.name}");
                }
                // Also hide close button X label again (base.Open() might re-enable it)
                else if (label.text == "X")
                {
                    if (label.transform.parent != null)
                    {
                        label.transform.parent.gameObject.SetActive(false);
                        CoopMod.Logger.LogInfo($"OPEN: Re-disabled close button parent: {label.transform.parent.name}");
                    }
                    else
                    {
                        label.gameObject.SetActive(false);
                        CoopMod.Logger.LogInfo($"OPEN: Re-disabled X label: {label.name}");
                    }
                }
            }

            // The cloned save-slot widgets finish their anchors during the first
            // active frame. Building the grid immediately uses stale bounds for
            // the first card, which is why reopening the screen fixed its layout.
            friendLoadGeneration++;
            if (pendingFriendLoad != null)
                StopCoroutine(pendingFriendLoad);
            pendingFriendLoad = StartCoroutine(
                LoadSteamFriendsAfterLayout(friendLoadGeneration));
        }

        private void QueueResolutionLayoutRefresh()
        {
            if (!gameObject.activeInHierarchy)
                return;

            if (pendingResolutionLayoutRefresh != null)
                StopCoroutine(pendingResolutionLayoutRefresh);
            pendingResolutionLayoutRefresh = StartCoroutine(ReapplyResolutionLayoutAfterUiSettles());
        }

        private System.Collections.IEnumerator ReapplyResolutionLayoutAfterUiSettles()
        {
            // UIRoot and the cloned menu anchors settle asynchronously after a
            // live resolution change. Reapply once their scale is stable.
            for (int frame = 0; frame < 4; frame++)
                yield return null;

            if (gameObject.activeInHierarchy)
                ApplyResolutionLayout();
            pendingResolutionLayoutRefresh = null;
        }

        public override void Hide(bool playSfx = true)
        {
            CoopMod.Logger.LogInfo("Hiding FriendInviteGUI");
            friendLoadGeneration++;
            if (pendingFriendLoad != null)
            {
                StopCoroutine(pendingFriendLoad);
                pendingFriendLoad = null;
            }

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

        private System.Collections.IEnumerator LoadSteamFriendsAfterLayout(int generation)
        {
            yield return null;

            if (generation != friendLoadGeneration ||
                !gameObject.activeInHierarchy ||
                !is_shown)
            {
                yield break;
            }

            UpdateAllAnchors();
            ApplyResolutionLayout();
            LoadSteamFriends();

            // New labels and anchored backgrounds also need one layout pass before
            // UITable measures them. Re-run the final layout from settled bounds.
            yield return null;
            if (generation != friendLoadGeneration ||
                !gameObject.activeInHierarchy ||
                !is_shown)
            {
                yield break;
            }

            if (friendTable != null)
            {
                RepositionFriendTable();
            }
            RefreshGamepadNavigation();
            pendingFriendLoad = null;
        }

        private void LoadSteamFriends()
        {
            CoopMod.Logger.LogInfo("Loading Steam friends list...");

            friendSteamIDs.Clear();
            friendItems.Clear();
            FriendInviteResolutionLayout layout = GetActiveResolutionLayout();

            if (!SteamManager.Initialized)
            {
                CoopMod.Logger.LogError("Steam is not initialized!");
                return;
            }

            // Clear existing friend items from the table
            if (friendTable != null)
            {
                // Store the original position before clearing
                Vector3 originalTablePos = friendTable.transform.localPosition;
                CoopMod.Logger.LogInfo($"Table position before clear: {originalTablePos}");
                
                // DIAGNOSTIC: Check ScrollView state
                if (scrollView != null && scrollPanel != null)
                {
                    CoopMod.Logger.LogInfo($"ScrollView panel offset before clear: {scrollPanel.clipOffset}");
                }
                
                var children = new List<Transform>();
                foreach (Transform child in friendTable.transform)
                {
                    children.Add(child);
                }
                foreach (var child in children)
                {
                    Object.DestroyImmediate(child.gameObject);
                }
                
                // Reset scroll offset to top (but DON'T touch transform positions!)
                if (scrollPanel != null)
                {
                    scrollPanel.clipOffset = Vector2.zero;
                    CoopMod.Logger.LogInfo($"Reset ScrollView panel clipOffset to zero");
                }
                
                // Restore the table's local position
                friendTable.transform.localPosition = originalTablePos;
                CoopMod.Logger.LogInfo($"Reset table position to: {originalTablePos}");
                
                CoopMod.Logger.LogInfo("Cleared existing friend items");
            }

            // Get friend count
            int friendCount = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            CoopMod.Logger.LogInfo($"Found {friendCount} Steam friends");

            // Use cached template
            if (friendItemTemplate == null || friendTable == null)
            {
                CoopMod.Logger.LogError("Cannot create friend items - missing cached template or table");
                return;
            }

            // CRITICAL: Disable UITable auto-repositioning while we create children
            // This prevents UITable from positioning items as they're activated
            friendTable.enabled = false;
            CoopMod.Logger.LogInfo("Disabled UITable during item creation");

            // Create a friend item for each Steam friend
            for (int i = 0; i < friendCount; i++)
            {
                CSteamID friendID = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                string friendName = SteamFriends.GetFriendPersonaName(friendID);
                EPersonaState friendState = SteamFriends.GetFriendPersonaState(friendID);
                
                // Skip offline friends - only show online friends
                if (friendState == EPersonaState.k_EPersonaStateOffline)
                {
                    CoopMod.Logger.LogInfo($"Skipping offline friend: {friendName}");
                    continue;
                }
                
                friendSteamIDs.Add(friendID);

                // Create friend item
                GameObject friendItem = Object.Instantiate(friendItemTemplate, friendTable.transform);
                friendItem.name = $"friend_{i}";
                
                // CRITICAL: Reset transform BEFORE activating so UITable starts fresh
                friendItem.transform.localPosition = Vector3.zero;
                friendItem.transform.localRotation = Quaternion.identity;
                friendItem.transform.localScale = layout.FriendItemScale;
                
                // Activate AFTER resetting transform
                friendItem.SetActive(true);

                // Save-slot rows can carry motion helpers intended for the original
                // vertical list. In a three-column friend grid they can leave the
                // initially focused first card offset from the other first-column
                // cards, so the table must be the sole owner of card positioning.
                foreach (SpringPosition spring in friendItem.GetComponentsInChildren<SpringPosition>(true))
                {
                    Object.DestroyImmediate(spring);
                }

                // Hide the delete button (red X) on each friend item
                var deleteButtons = friendItem.GetComponentsInChildren<Transform>(true);
                foreach (var t in deleteButtons)
                {
                    if (t.name.ToLower().Contains("delete") || t.name.ToLower().Contains("cross") || t.name.ToLower().Contains("remove"))
                    {
                        t.gameObject.SetActive(false);
                    }
                }

                // Update the labels to show friend name and status
                var labels = friendItem.GetComponentsInChildren<UILabel>(true);
                UILabel nameLabel = null;
                UILabel statusLabel = null;
                UILabel fallbackNameLabel = null;
                
                // First pass: identify labels and clear all text
                CoopMod.Logger.LogInfo($"Friend item has {labels.Length} labels:");
                foreach (var label in labels)
                {
                    CoopMod.Logger.LogInfo($"  Label: {label.name}, text: '{label.text}'");
                    
                    string labelName = label.name.ToLower();
                    
                    // Identify the "new game" label for friend name
                    if (nameLabel == null && labelName.Contains("new game"))
                    {
                        nameLabel = label;
                    }
                    // Identify the "descr" label for status
                    else if (statusLabel == null && labelName.Contains("descr"))
                    {
                        statusLabel = label;
                    }
                    // Track first non-descr label as fallback for name
                    else if (fallbackNameLabel == null && !labelName.Contains("descr"))
                    {
                        fallbackNameLabel = label;
                    }
                    
                    // Clear ALL labels first
                    label.text = "";
                }
                
                // Second pass: set the labels we identified
                if (nameLabel != null)
                {
                    nameLabel.text = GetDisplayableName(friendName, friendID);
                    nameLabel.fontSize = 20;
                    nameLabel.color = Color.white;
                    CoopMod.Logger.LogInfo($"Set friend name '{nameLabel.text}' on label: {nameLabel.name}");
                }
                else if (fallbackNameLabel != null)
                {
                    // Use fallback if "new game" label not found
                    fallbackNameLabel.text = GetDisplayableName(friendName, friendID);
                    fallbackNameLabel.fontSize = 20;
                    fallbackNameLabel.color = Color.white;
                    CoopMod.Logger.LogWarning($"Used fallback label for friend name '{fallbackNameLabel.text}': {fallbackNameLabel.name}");
                }
                else
                {
                    CoopMod.Logger.LogError($"Failed to set name for friend: {friendName} - no suitable label found!");
                }
                
                if (statusLabel != null)
                {
                    string statusText = GetStatusText(friendState);
                    Color statusColor = GetStatusColor(friendState);
                    statusLabel.text = statusText;
                    statusLabel.fontSize = 16;
                    statusLabel.color = statusColor;
                    CoopMod.Logger.LogInfo($"Set friend status '{statusText}' on label: {statusLabel.name}");
                }

                // Remove SaveSlotGUI component and add click handler
                var saveSlotGUI = friendItem.GetComponent<SaveSlotGUI>();
                if (saveSlotGUI != null)
                {
                    Object.DestroyImmediate(saveSlotGUI);
                }

                // Add click handler component
                var clickHandler = friendItem.AddComponent<FriendItemClickHandler>();
                clickHandler.Initialize(friendID, friendName, this);
                
                // Create and attach selection frame
                var selectionFrame = CreateSelectionFrame(friendItem);
                if (selectionFrame != null)
                {
                    clickHandler.SetSelectionFrame(selectionFrame);
                }

                ConfigureFriendNavigation(friendItem, clickHandler);

                // Add to tracking list
                friendItems.Add(friendItem);

                CoopMod.Logger.LogInfo($"Created friend item: {friendName} ({GetStatusText(friendState)})");
            }

            // Reposition the table
            if (friendTable != null)
            {
                CoopMod.Logger.LogInfo($"Table position before Reposition: {friendTable.transform.localPosition}");
                CoopMod.Logger.LogInfo($"Table enabled state: {friendTable.enabled}");
                
                // DIAGNOSTIC: Check first child's position BEFORE manual reset
                if (friendTable.transform.childCount > 0)
                {
                    var firstChild = friendTable.transform.GetChild(0);
                    CoopMod.Logger.LogInfo($"First child position BEFORE manual reset: {firstChild.localPosition}");
                }
                
                // CRITICAL: Manually reset all children positions to zero
                // This must happen while UITable is still disabled
                int resetCount = 0;
                foreach (Transform child in friendTable.transform)
                {
                    var oldPos = child.localPosition;
                    child.localPosition = Vector3.zero;
                    child.localRotation = Quaternion.identity;
                    child.localScale = layout.FriendItemScale;
                    resetCount++;
                    if (resetCount == 1)
                    {
                        CoopMod.Logger.LogInfo($"Reset first child from {oldPos} to {child.localPosition}");
                    }
                }
                CoopMod.Logger.LogInfo($"Manually reset {resetCount} children to (0,0,0)");
                
                // DIAGNOSTIC: Count actual children
                int actualChildCount = friendTable.transform.childCount;
                int activeChildCount = 0;
                foreach (Transform child in friendTable.transform)
                {
                    if (child.gameObject.activeSelf) activeChildCount++;
                }
                CoopMod.Logger.LogInfo($"Table has {actualChildCount} total children, {activeChildCount} active");
                
                // DIAGNOSTIC: Verify first child is now at zero
                if (friendTable.transform.childCount > 0)
                {
                    var firstChild = friendTable.transform.GetChild(0);
                    CoopMod.Logger.LogInfo($"First child position AFTER manual reset: {firstChild.localPosition}");
                }
                
                // CRITICAL: Set repositionNow BEFORE enabling to prevent auto-repositioning on enable
                friendTable.repositionNow = true;
                
                // Re-enable UITable
                friendTable.enabled = true;
                CoopMod.Logger.LogInfo("Re-enabled UITable for repositioning");
                
                // Now call Reposition() explicitly
                RepositionFriendTable();
                
                // DIAGNOSTIC: Check first child's position after
                if (friendTable.transform.childCount > 0)
                {
                    var firstChild = friendTable.transform.GetChild(0);
                    CoopMod.Logger.LogInfo($"First child position after Reposition: {firstChild.localPosition}");
                }
                
                CoopMod.Logger.LogInfo($"Table position after Reposition: {friendTable.transform.localPosition}");
            }

            CoopMod.Logger.LogInfo($"Loaded {friendCount} friends");
            RefreshGamepadNavigation();
        }

        /// <summary>
        /// UITable includes active child widgets when calculating cell bounds.
        /// Controller focus activates the first card's oversized decorative frame,
        /// so hide those frames while measuring to keep controller and mouse
        /// openings on the exact same grid.
        /// </summary>
        private void RepositionFriendTable()
        {
            if (friendTable == null)
                return;

            var visibleFrames = new List<GameObject>();
            foreach (var friendItem in friendItems)
            {
                FriendItemClickHandler clickHandler =
                    friendItem != null
                        ? friendItem.GetComponent<FriendItemClickHandler>()
                        : null;
                GameObject frameObject =
                    clickHandler?.SelectionFrame != null
                        ? clickHandler.SelectionFrame.gameObject
                        : null;
                if (frameObject != null && frameObject.activeSelf)
                {
                    visibleFrames.Add(frameObject);
                    frameObject.SetActive(false);
                }
            }

            friendTable.Reposition();
            CenterFriendTableInVisibleArea();
            NormalizeFriendGridPositions();

            foreach (GameObject frameObject in visibleFrames)
            {
                if (frameObject != null)
                    frameObject.SetActive(true);
            }
        }

        private void ConfigureFriendNavigation(GameObject friendItem, FriendItemClickHandler clickHandler)
        {
            if (friendItem == null || clickHandler == null)
                return;

            foreach (var childItem in friendItem.GetComponentsInChildren<GamepadNavigationItem>(true))
            {
                if (childItem.gameObject != friendItem)
                    Object.DestroyImmediate(childItem);
            }

            var navigationItem = friendItem.GetComponent<GamepadNavigationItem>() ??
                                 friendItem.AddComponent<GamepadNavigationItem>();
            navigationItem.active = true;
            if (clickHandler.SelectionFrame != null)
                navigationItem.focus_frame = clickHandler.SelectionFrame.gameObject;
            navigationItem.SetCallbacks(
                null,
                UpdateFriendHighlighting,
                () => OnFriendSelected(clickHandler.FriendID, clickHandler.FriendName));
        }

        private void ConfigureGamepadNavigation()
        {
            if (backNavigationItem == null || refreshNavigationItem == null || inviteNavigationItem == null)
                return;

            var friendNavigationItems = new List<GamepadNavigationItem>();
            foreach (var friendItem in friendItems)
            {
                if (friendItem == null)
                    continue;
                var item = friendItem.GetComponent<GamepadNavigationItem>();
                if (item != null)
                    friendNavigationItems.Add(item);
            }

            int columns = Mathf.Max(1, GetActiveResolutionLayout().TableColumns);
            GamepadNavigationItem[] buttons =
            {
                backNavigationItem,
                refreshNavigationItem,
                inviteNavigationItem
            };

            for (int i = 0; i < friendNavigationItems.Count; i++)
            {
                GamepadNavigationItem item = friendNavigationItems[i];
                int column = i % columns;
                item.active = true;
                item.SetCustomDirectionItem(
                    column > 0 ? friendNavigationItems[i - 1] : item,
                    Direction.Left);
                item.SetCustomDirectionItem(
                    column + 1 < columns && i + 1 < friendNavigationItems.Count
                        ? friendNavigationItems[i + 1]
                        : item,
                    Direction.Right);
                item.SetCustomDirectionItem(
                    i >= columns ? friendNavigationItems[i - columns] : buttons[Mathf.Min(column, buttons.Length - 1)],
                    Direction.Up);
                item.SetCustomDirectionItem(
                    i + columns < friendNavigationItems.Count
                        ? friendNavigationItems[i + columns]
                        : buttons[Mathf.Min(column, buttons.Length - 1)],
                    Direction.Down);
            }

            for (int i = 0; i < buttons.Length; i++)
            {
                GamepadNavigationItem button = buttons[i];
                button.active = true;
                button.SetCustomDirectionItem(buttons[(i + buttons.Length - 1) % buttons.Length], Direction.Left);
                button.SetCustomDirectionItem(buttons[(i + 1) % buttons.Length], Direction.Right);
                button.SetCustomDirectionItem(GetLastFriendInColumn(friendNavigationItems, i, columns) ?? button, Direction.Up);
                button.SetCustomDirectionItem(button, Direction.Down);
            }
        }

        private static GamepadNavigationItem GetLastFriendInColumn(
            List<GamepadNavigationItem> items,
            int column,
            int columns)
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (i % columns == column)
                    return items[i];
            }
            return null;
        }

        private void RefreshGamepadNavigation()
        {
            ConfigureGamepadNavigation();
            if (!is_shown || !BaseGUI.for_gamepad || gamepad_controller == null)
                return;

            gamepad_controller.ReinitItems(false);
            GamepadNavigationItem firstFriend = null;
            foreach (var friendItem in friendItems)
            {
                if (friendItem != null)
                {
                    firstFriend = friendItem.GetComponent<GamepadNavigationItem>();
                    if (firstFriend != null)
                        break;
                }
            }

            if (firstFriend != null)
            {
                gamepad_controller.SetFocusedItem(firstFriend, false);
                NormalizeFriendGridPositions();
            }
            else if (gamepad_controller.focused_item == null)
                gamepad_controller.FocusOnFirstActive();
        }

        private void NormalizeFriendGridPositions()
        {
            if (friendTable == null || friendItems.Count < 2)
                return;

            int columns = Mathf.Max(1, GetActiveResolutionLayout().TableColumns);
            int rowCount = (friendItems.Count + columns - 1) / columns;
            var columnX = new float[columns];
            var rowY = new float[rowCount];

            for (int column = 0; column < columns; column++)
            {
                int referenceIndex = column + columns < friendItems.Count
                    ? column + columns
                    : column;
                if (referenceIndex < friendItems.Count && friendItems[referenceIndex] != null)
                    columnX[column] = friendItems[referenceIndex].transform.localPosition.x;
            }

            for (int row = 0; row < rowCount; row++)
            {
                int rowStart = row * columns;
                int referenceIndex = Mathf.Min(rowStart + 1, friendItems.Count - 1);
                if (friendItems[referenceIndex] != null)
                    rowY[row] = friendItems[referenceIndex].transform.localPosition.y;
            }

            int corrected = 0;
            for (int i = 0; i < friendItems.Count; i++)
            {
                GameObject item = friendItems[i];
                if (item == null)
                    continue;

                int column = i % columns;
                int row = i / columns;
                Vector3 position = item.transform.localPosition;
                Vector3 normalized = new Vector3(columnX[column], rowY[row], position.z);
                if ((position - normalized).sqrMagnitude > 0.001f)
                    corrected++;
                item.transform.localPosition = normalized;
            }

            if (corrected > 0)
            {
                CoopMod.Logger.LogInfo(
                    $"[FriendInviteGUI] Normalized {corrected} friend card positions to the shared grid");
            }
        }

        private void CenterFriendTableInVisibleArea()
        {
            if (friendTable == null || friendTable.transform.childCount == 0)
            {
                return;
            }

            if (!TryGetFriendTableBounds(out float minX, out float maxX))
            {
                return;
            }

            float contentCenterX = (minX + maxX) * 0.5f;
            if (Mathf.Abs(contentCenterX) < 0.01f)
            {
                return;
            }

            Vector3 oldPosition = friendTable.transform.localPosition;
            friendTable.transform.localPosition = oldPosition + new Vector3(-contentCenterX, 0f, 0f);
            CoopMod.Logger.LogInfo($"Centered friend table horizontally: boundsX=({minX:F1}, {maxX:F1}), offsetX={-contentCenterX:F1}, pos={oldPosition} -> {friendTable.transform.localPosition}");
        }

        private bool TryGetFriendTableBounds(out float minX, out float maxX)
        {
            minX = float.MaxValue;
            maxX = float.MinValue;
            bool found = false;

            foreach (Transform item in friendTable.transform)
            {
                if (item == null || !item.gameObject.activeInHierarchy)
                {
                    continue;
                }

                UIWidget[] widgets = item.GetComponentsInChildren<UIWidget>(true);
                foreach (UIWidget widget in widgets)
                {
                    if (widget == null || !widget.enabled || !widget.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    Vector3[] corners = widget.worldCorners;
                    for (int i = 0; i < corners.Length; i++)
                    {
                        float x = transform.InverseTransformPoint(corners[i]).x;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                    }

                    found = true;
                }
            }

            return found;
        }

        private string GetStatusText(EPersonaState state)
        {
            switch (state)
            {
                case EPersonaState.k_EPersonaStateOnline:
                    return "Online";
                case EPersonaState.k_EPersonaStateBusy:
                    return "Busy";
                case EPersonaState.k_EPersonaStateAway:
                    return "Away";
                case EPersonaState.k_EPersonaStateSnooze:
                    return "Snooze";
                case EPersonaState.k_EPersonaStateLookingToTrade:
                    return "Looking to Trade";
                case EPersonaState.k_EPersonaStateLookingToPlay:
                    return "Looking to Play";
                case EPersonaState.k_EPersonaStateOffline:
                default:
                    return "Offline";
            }
        }

        private Color GetStatusColor(EPersonaState state)
        {
            switch (state)
            {
                case EPersonaState.k_EPersonaStateOnline:
                case EPersonaState.k_EPersonaStateLookingToPlay:
                    return new Color(0.5f, 1f, 0.5f); // Green for online
                case EPersonaState.k_EPersonaStateBusy:
                    return new Color(1f, 0.5f, 0.5f); // Red for busy
                case EPersonaState.k_EPersonaStateAway:
                case EPersonaState.k_EPersonaStateSnooze:
                    return new Color(1f, 0.8f, 0.3f); // Orange for away
                case EPersonaState.k_EPersonaStateOffline:
                default:
                    return new Color(0.6f, 0.6f, 0.6f); // Gray for offline
            }
        }

        /// <summary>
        /// Finds and caches the gamepad_frame sprite used for selection highlighting
        /// Uses the same approach as PlayerListPanel.CreateReadyOutline
        /// </summary>
        private void FindAndCacheFrameSprite()
        {
            if (cachedFrameSprite != null) return;
            
            // Try to find the gamepad_frame from SaveSlotGUI
            var saveSlotGUIs = Resources.FindObjectsOfTypeAll<SaveSlotGUI>();
            foreach (var saveSlot in saveSlotGUIs)
            {
                if (saveSlot.gamepad_frame != null)
                {
                    // The gamepad_frame is a UIWidget, but at runtime it's often a UI2DSprite
                    var frameSprite = saveSlot.gamepad_frame as UI2DSprite;
                    if (frameSprite != null && frameSprite.sprite2D != null)
                    {
                        cachedFrameSprite = frameSprite.sprite2D;
                        CoopMod.Logger.LogInfo($"[FriendInviteGUI] Found gamepad_frame sprite from SaveSlotGUI: {cachedFrameSprite.name}");
                        return;
                    }
                    
                    // Try getting UI2DSprite component from the gamepad_frame gameObject
                    var childSprite = saveSlot.gamepad_frame.GetComponent<UI2DSprite>();
                    if (childSprite != null && childSprite.sprite2D != null)
                    {
                        cachedFrameSprite = childSprite.sprite2D;
                        CoopMod.Logger.LogInfo($"[FriendInviteGUI] Found gamepad_frame sprite from component: {cachedFrameSprite.name}");
                        return;
                    }
                    
                    // Check children
                    var childSprites = saveSlot.gamepad_frame.GetComponentsInChildren<UI2DSprite>(true);
                    foreach (var cs in childSprites)
                    {
                        if (cs.sprite2D != null)
                        {
                            cachedFrameSprite = cs.sprite2D;
                            CoopMod.Logger.LogInfo($"[FriendInviteGUI] Found gamepad_frame sprite from child: {cachedFrameSprite.name}");
                            return;
                        }
                    }
                }
            }
            
            // Fallback: search all UI2DSprites for a frame
            var existingSprites = Resources.FindObjectsOfTypeAll<UI2DSprite>();
            foreach (var sprite in existingSprites)
            {
                if (sprite.sprite2D != null && sprite.name.ToLower().Contains("gamepad") && sprite.name.ToLower().Contains("frame"))
                {
                    cachedFrameSprite = sprite.sprite2D;
                    CoopMod.Logger.LogInfo($"[FriendInviteGUI] Found frame sprite by name: {sprite.name}, sprite: {cachedFrameSprite.name}");
                    return;
                }
            }
            
            if (cachedFrameSprite == null)
            {
                CoopMod.Logger.LogWarning("[FriendInviteGUI] No gamepad_frame sprite found for selection highlighting");
            }
        }

        /// <summary>
        /// Creates a selection frame sprite for a friend item
        /// </summary>
        private UI2DSprite CreateSelectionFrame(GameObject friendItem)
        {
            // Find and cache the frame sprite if not done yet
            FindAndCacheFrameSprite();
            
            if (cachedFrameSprite == null)
            {
                CoopMod.Logger.LogWarning("[FriendInviteGUI] Cannot create selection frame - no sprite available");
                return null;
            }
            
            // Fixed dimensions that match the visual size of friend items
            int width = 300;
            int height = 55;
            
            // Create the selection frame as a child of the friend item
            GameObject frameObj = new GameObject("SelectionFrame");
            frameObj.layer = friendItem.layer;
            frameObj.transform.SetParent(friendItem.transform, false);
            frameObj.transform.localPosition = Vector3.zero;
            frameObj.transform.localScale = Vector3.one;
            
            UI2DSprite frame = frameObj.AddComponent<UI2DSprite>();
            frame.sprite2D = cachedFrameSprite;
            frame.width = width;
            frame.height = height;
            frame.depth = 120; // In front of everything else
            frame.color = SELECTION_FRAME_COLOR;
            
            CoopMod.Logger.LogInfo($"[FriendInviteGUI] Created selection frame {width}x{height}");
            
            // Start hidden
            frameObj.SetActive(false);
            
            CoopMod.Logger.LogInfo($"[FriendInviteGUI] Created selection frame for friend item: {friendItem.name}");
            return frame;
        }

        /// <summary>
        /// Gets a displayable name, falling back to Steam ID if name contains only non-ASCII characters (emojis)
        /// </summary>
        private string GetDisplayableName(string friendName, CSteamID friendID)
        {
            if (string.IsNullOrEmpty(friendName))
            {
                return $"User_{friendID.m_SteamID}";
            }
            
            // Check if name has any ASCII printable characters (letters, numbers, common symbols)
            bool hasDisplayableChars = false;
            foreach (char c in friendName)
            {
                // ASCII printable range: 32-126
                if (c >= 32 && c <= 126)
                {
                    hasDisplayableChars = true;
                    break;
                }
            }
            
            if (hasDisplayableChars)
            {
                return friendName;
            }
            else
            {
                // Name is only emojis/special unicode - show Steam ID
                CoopMod.Logger.LogInfo($"Friend name '{friendName}' has no displayable chars, using Steam ID");
                return $"User_{friendID.m_SteamID}";
            }
        }

        public void OnFriendSelected(CSteamID friendID, string friendName)
        {
            // Toggle behavior: if clicking the same friend, deselect them
            if (selectedFriendID == friendID)
            {
                CoopMod.Logger.LogInfo($"Friend deselected: {friendName} ({friendID})");
                selectedFriendID = CSteamID.Nil;
            }
            else
            {
                CoopMod.Logger.LogInfo($"Friend selected: {friendName} ({friendID})");
                selectedFriendID = friendID;
            }
            
            // Update visual highlighting
            UpdateFriendHighlighting();
        }

        /// <summary>
        /// Updates the visual highlighting for all friend items based on current selection
        /// Uses gamepad_frame sprite (same as save slot selection and player ready)
        /// </summary>
        private void UpdateFriendHighlighting()
        {
            foreach (var friendItem in friendItems)
            {
                if (friendItem == null) continue;
                
                var clickHandler = friendItem.GetComponent<FriendItemClickHandler>();
                if (clickHandler == null) continue;
                
                bool isSelected = (selectedFriendID != CSteamID.Nil && clickHandler.FriendID == selectedFriendID);
                
                // Show/hide selection frame
                if (clickHandler.SelectionFrame != null)
                {
                    clickHandler.SelectionFrame.gameObject.SetActive(isSelected);
                    CoopMod.Logger.LogInfo($"Selection frame for {clickHandler.FriendName}: {(isSelected ? "VISIBLE" : "HIDDEN")}");
                }
                else
                {
                    CoopMod.Logger.LogWarning($"No selection frame for {clickHandler.FriendName}");
                }
            }
        }

        private new void Update()
        {
            base.Update();

            if (is_shown && gameObject.activeInHierarchy &&
                (Screen.width != appliedLayoutWidth || Screen.height != appliedLayoutHeight) &&
                pendingResolutionLayoutRefresh == null)
            {
                QueueResolutionLayoutRefresh();
            }

            // Manual click detection for back button
            if (Input.GetMouseButtonDown(0) && uiCamera != null && backButtonWidget != null)
            {
                Vector3 mousePos = Input.mousePosition;
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
                    Sounds.PlaySound("win_close", null, false, 0f);
                    CoopMod.Logger.LogInfo("Back button clicked!");
                    OnBackPressed();
                }
            }

            // Manual click detection for invite button
            if (Input.GetMouseButtonDown(0) && uiCamera != null && inviteButtonWidget != null)
            {
                Vector3 mousePos = Input.mousePosition;
                Vector3[] corners = inviteButtonWidget.worldCorners;
                Vector2 min = uiCamera.WorldToScreenPoint(corners[0]);
                Vector2 max = uiCamera.WorldToScreenPoint(corners[2]);

                float minX = Mathf.Min(min.x, max.x);
                float maxX = Mathf.Max(min.x, max.x);
                float minY = Mathf.Min(min.y, max.y);
                float maxY = Mathf.Max(min.y, max.y);

                if (mousePos.x >= minX && mousePos.x <= maxX &&
                    mousePos.y >= minY && mousePos.y <= maxY)
                {
                    Sounds.PlaySound("gui_click", null, false, 0f);
                    CoopMod.Logger.LogInfo("Invite button clicked!");
                    OnInvitePressed();
                }
            }

            // Manual click detection for refresh button
            if (Input.GetMouseButtonDown(0) && uiCamera != null && refreshButtonWidget != null)
            {
                Vector3 mousePos = Input.mousePosition;
                Vector3[] corners = refreshButtonWidget.worldCorners;
                Vector2 min = uiCamera.WorldToScreenPoint(corners[0]);
                Vector2 max = uiCamera.WorldToScreenPoint(corners[2]);

                float minX = Mathf.Min(min.x, max.x);
                float maxX = Mathf.Max(min.x, max.x);
                float minY = Mathf.Min(min.y, max.y);
                float maxY = Mathf.Max(min.y, max.y);

                if (mousePos.x >= minX && mousePos.x <= maxX &&
                    mousePos.y >= minY && mousePos.y <= maxY)
                {
                    Sounds.PlaySound("gui_click", null, false, 0f);
                    CoopMod.Logger.LogInfo("Refresh button clicked!");
                    OnRefreshPressed();
                }
            }
        }

        private void OnInvitePressed()
        {
            CoopMod.Logger.LogInfo("[UI] ========== OnInvitePressed() ==========");
            CoopMod.Logger.LogInfo($"[UI] selectedFriendID = {selectedFriendID}");
            
            if (selectedFriendID == CSteamID.Nil)
            {
                CoopMod.Logger.LogWarning("[UI] No friend selected to invite!");
                return;
            }

            string friendName = SteamFriends.GetFriendPersonaName(selectedFriendID);
            CoopMod.Logger.LogInfo($"[UI] Friend name: {friendName}");
            CoopMod.Logger.LogInfo($"[UI] Attempting to invite {friendName} ({selectedFriendID}) to lobby...");

            // Check if we're in a lobby
            CoopMod.Logger.LogInfo($"[UI] SteamLobbyManager.IsInLobby = {SteamLobbyManager.Instance.IsInLobby}");
            if (!SteamLobbyManager.Instance.IsInLobby)
            {
                CoopMod.Logger.LogError("[UI] Cannot invite friend - you're not in a lobby!");
                CoopMod.Logger.LogInfo("[UI] Creating a new lobby first...");
                
                // Create a lobby first, then the invite will be handled after lobby creation
                SteamLobbyManager.Instance.CreateLobby();

                // The invite is not sent automatically once the lobby is ready;
                // the player can re-trigger it from the friends list afterwards.
                return;
            }

            // Send the Steam lobby invite
            CoopMod.Logger.LogInfo("[UI] Calling SteamLobbyManager.InviteFriend()...");
            bool success = SteamLobbyManager.Instance.InviteFriend(selectedFriendID);

            if (success)
            {
                CoopMod.Logger.LogInfo($"[UI] ✓ Successfully invited {friendName}!");
                
                // Clear selection after successful invite so user can select another friend
                CSteamID justInvitedID = selectedFriendID;
                selectedFriendID = CSteamID.Nil;
                
                // Update highlighting to show no selection
                UpdateFriendHighlighting();
                
                CoopMod.Logger.LogInfo("[UI] Cleared friend selection - ready to invite another friend");
                
                // Notify via chat (use ChatManager to ensure sync)
                ChatManager.AddMessage($"[System] Invited {friendName} to the lobby");
            }
            else
            {
                CoopMod.Logger.LogError($"[UI] ✗ Failed to invite {friendName}");
                
                // Notify via chat (use ChatManager to ensure sync)
                ChatManager.AddMessage($"[System] Failed to invite {friendName}");
            }

            // Close the friend invite screen
            Hide();

            // Return to lobby - just show it without reinitializing
            if (LobbyGUI.Instance != null)
            {
                LobbyGUI.Instance.gameObject.SetActive(true);
                LobbyGUI.Instance.ShowChatInput();
            }
        }

        private void OnBackPressed()
        {
            CoopMod.Logger.LogInfo("Back button pressed - returning to lobby");
            Hide();

            // Return to lobby - reactivate without reinitializing (preserves chat messages)
            if (LobbyGUI.Instance != null)
            {
                LobbyGUI.Instance.gameObject.SetActive(true);
                
                // CRITICAL: Re-show the chat input panel that was hidden by Hide()
                // We can't call Open() because that would reinitialize and clear chat
                // Use the public ShowChatInput() method to reactivate the input panel
                LobbyGUI.Instance.ShowChatInput();
            }
        }

        private void OnRefreshPressed()
        {
            CoopMod.Logger.LogInfo("Refresh button pressed - reloading Steam friends list");
            
            // Clear selection
            selectedFriendID = CSteamID.Nil;
            friendItems.Clear();
            
            // Reload the friends list
            LoadSteamFriends();
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

    /// <summary>
    /// Click handler for friend list items
    /// </summary>
    public class FriendItemClickHandler : MonoBehaviour
    {
        private CSteamID friendID;
        private string friendName;
        private FriendInviteGUI parentGUI;
        private UIWidget widget;
        private UI2DSprite selectionFrame;

        public CSteamID FriendID => friendID;
        public string FriendName => friendName;
        public UI2DSprite SelectionFrame => selectionFrame;

        public void Initialize(CSteamID id, string name, FriendInviteGUI parent)
        {
            friendID = id;
            friendName = name;
            parentGUI = parent;

            widget = GetComponent<UIWidget>();
            if (widget == null)
            {
                widget = GetComponentInChildren<UIWidget>(true);
            }

            CoopMod.Logger.LogInfo($"FriendItemClickHandler initialized: {friendName}");
        }

        public void SetSelectionFrame(UI2DSprite frame)
        {
            selectionFrame = frame;
        }

        private void Update()
        {
            if (Input.GetMouseButtonDown(0) && widget != null)
            {
                var uiCamera = Object.FindObjectOfType<Camera>();
                if (uiCamera == null) return;

                Vector3 mousePos = Input.mousePosition;
                Vector3[] corners = widget.worldCorners;
                Vector2 min = uiCamera.WorldToScreenPoint(corners[0]);
                Vector2 max = uiCamera.WorldToScreenPoint(corners[2]);

                float minX = Mathf.Min(min.x, max.x);
                float maxX = Mathf.Max(min.x, max.x);
                float minY = Mathf.Min(min.y, max.y);
                float maxY = Mathf.Max(min.y, max.y);

                if (mousePos.x >= minX && mousePos.x <= maxX &&
                    mousePos.y >= minY && mousePos.y <= maxY)
                {
                    CoopMod.Logger.LogInfo($"Friend clicked: {friendName}");
                    if (parentGUI != null)
                    {
                        parentGUI.OnFriendSelected(friendID, friendName);
                    }
                }
            }
        }
    }
}

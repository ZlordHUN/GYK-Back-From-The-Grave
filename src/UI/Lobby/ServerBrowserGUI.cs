using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Steamworks;
using GraveyardKeeperCoop.Network;
using GraveyardKeeperCoop.Utils;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// Server Browser GUI - Shows friends hosting games.
    /// Uses the game's actual background sprites for proper styling.
    /// </summary>
    public class ServerBrowserGUI : MonoBehaviour
    {
        private static ServerBrowserGUI _instance;
        public static ServerBrowserGUI Instance => _instance;

        // Sub-tab system
        public enum BrowserTab
        {
            Internet,
            Friends,
            Favorites,
            LAN
        }

        private BrowserTab currentTab = BrowserTab.Friends;
        internal BrowserTab CurrentTab => currentTab;

        // UI Components
        private UIFont gameFont;
        private Sprite backgroundSprite; // The actual game's panel background sprite
        private SaveSlotGUI saveSlotTemplate;

        // Background panel
        private UI2DSprite panelBackground;

        // Tab bar (cloned from GameGUI)
        private GameObject tabBarContainer;
        private Dictionary<BrowserTab, GameTabItemGUI> tabItems = new Dictionary<BrowserTab, GameTabItemGUI>();

        // Tab buttons
        private Dictionary<BrowserTab, UILabel> tabLabels = new Dictionary<BrowserTab, UILabel>();
        private Dictionary<BrowserTab, GamepadNavigationItem> tabNavigationItems = new Dictionary<BrowserTab, GamepadNavigationItem>();
        private Dictionary<BrowserTab, Color> tabNormalColors = new Dictionary<BrowserTab, Color>();

        // Server list
        private UIScrollView scrollView;
        private UIPanel scrollPanel;
        private GameObject serverListContainer;
        private UILabel noServersLabel;

        // Server data
        private List<ServerEntry> serverEntries = new List<ServerEntry>();
        private List<GameObject> serverRows = new List<GameObject>();
        private ServerEntry selectedServer = null;
        private GameObject selectedRowObj = null; // For highlight toggling
        private List<string> favoriteServers = new List<string>();
        private Dictionary<string, string> favoriteServerNames = new Dictionary<string, string>();
        private ServerEntry favoriteMenuServer = null;
        private GameObject favoriteMenuObj = null;
        private UILabel favoriteMenuLabel = null;
        private UI2DSprite favoriteMenuBackSprite = null;
        private Color favoriteMenuBackNormalColor = Color.white;
        private Color favoriteMenuBackHoverColor = new Color(1f, 0.88f, 0.56f, 1f);
        private Color favoriteMenuLabelNormalColor = Color.black;
        private Color favoriteMenuLabelHoverColor = new Color(0.16f, 0.06f, 0f, 1f);
        private bool favoriteMenuOpen = false;
        private bool favoriteMenuUsesGameContextBubble = false;
        private bool favoriteMenuHideInProgress = false;
        private Vector3 favoriteMenuClickLocalPosition = Vector3.zero;
        private GameObject rightClickPressedRow = null;
        private float rightClickPressedAt = 0f;
        private float suppressRowClickUntil = 0f;
        private bool refreshFavoritesAfterFavoriteMenu = false;
        public event Action ControllerItemsChanged;
        private const string FAVORITE_ACTION_ADD = "Add To Favorites";
        private const string FAVORITE_ACTION_REMOVE = "Remove From Favorites";
        private const float RIGHT_CLICK_CONSUME_SECONDS = 0.5f;
        private const float FAVORITE_MENU_WIDTH = 220f;
        private const float FAVORITE_MENU_HEIGHT = 34f;
        
        // Join-in-progress guard
        private bool isJoinInProgress = false;
        private float joinStartTime = 0f;
        private const float JOIN_TIMEOUT = 10f; // Reset after 10s if join never completes
        private Callback<FriendRichPresenceUpdate_t> friendPresenceUpdateCallback;
        private ServerEntry pendingPresenceJoinServer;
        private float pendingPresenceJoinStartedAt;
        private const float PRESENCE_JOIN_RETRY_TIMEOUT = 3f;

        // Tracks whether the connecting-to-server dialog is currently open so we can dismiss it
        // (we use the same DialogGUI window the engine uses for host-disconnect popups).
        private bool joinDialogOpen = false;
        
        // Ping label map: HostSteamID -> UILabel so we can update async
        private Dictionary<ulong, UILabel> pingLabels = new Dictionary<ulong, UILabel>();
        private Dictionary<ulong, PingProbeState> activePingProbes = new Dictionary<ulong, PingProbeState>();
        private const int PING_MAX_ATTEMPTS = 4;
        private const float PING_RETRY_INTERVAL = 0.6f;
        private const float PING_TIMEOUT = 4f;
        private const float LAN_REFRESH_INTERVAL = 0.5f;
        private const float LAN_PROBE_INTERVAL = 2f;
        private float nextLanRefreshTime = 0f;
        private float nextLanProbeTime = 0f;
        private string lastLanSnapshotFingerprint = "";

        // Colors matching game UI
        private static readonly Color HEADER_ACTIVE_COLOR = new Color(0.87f, 0.67f, 0.42f, 1f); // Orange/gold
        private static readonly Color HEADER_INACTIVE_COLOR = new Color(0.5f, 0.5f, 0.5f, 1f); // Gray
        private static readonly Color TEXT_COLOR = new Color(0.95f, 0.93f, 0.88f, 1f); // Light cream
        private static readonly Color DIM_TEXT_COLOR = new Color(0.64f, 0.64f, 0.67f, 1f); // Dimmed
        private static readonly Color ONLINE_COLOR = new Color(0.4f, 0.9f, 0.4f, 1f); // Green
        private static readonly Color RUNNING_GAME_COLOR = new Color(1f, 0.85f, 0.3f, 1f); // Yellow

        // Panel dimensions
        private const float PANEL_WIDTH = 550f;
        private const float PANEL_HEIGHT = 280f;
        private const float ROW_HEIGHT = 42f;
        private const float ROW_WIDTH = 630f;
        private ServerBrowserResolutionLayout activeLayout;
        private static readonly ServerBrowserResolutionLayout DefaultResolutionLayout = new ServerBrowserResolutionLayout(
            "Default",
            0,
            0,
            PANEL_WIDTH,
            PANEL_HEIGHT,
            ROW_HEIGHT,
            ROW_WIDTH,
            false,
            true,
            Vector3.one,
            Vector3.one);
        private static readonly ServerBrowserResolutionLayout[] ResolutionLayouts =
        {
            new ServerBrowserResolutionLayout(
                "SteamDeck1280x800",
                1280,
                800,
                PANEL_WIDTH,
                PANEL_HEIGHT,
                ROW_HEIGHT,
                ROW_WIDTH,
                false,
                true,
                new Vector3(0.72f, 0.72f, 1f),
                Vector3.one),
            new ServerBrowserResolutionLayout(
                "1366x768",
                1366,
                768,
                PANEL_WIDTH,
                PANEL_HEIGHT,
                ROW_HEIGHT,
                ROW_WIDTH,
                false,
                true,
                new Vector3(0.68f, 0.68f, 1f),
                Vector3.one),
            new ServerBrowserResolutionLayout(
                "1440x900",
                1440,
                900,
                PANEL_WIDTH,
                PANEL_HEIGHT,
                ROW_HEIGHT,
                ROW_WIDTH,
                false,
                true,
                new Vector3(0.72f, 0.72f, 1f),
                Vector3.one),
            new ServerBrowserResolutionLayout(
                "1600x900",
                1600,
                900,
                PANEL_WIDTH,
                PANEL_HEIGHT,
                ROW_HEIGHT,
                ROW_WIDTH,
                false,
                true,
                new Vector3(0.72f, 0.72f, 1f),
                Vector3.one),
            new ServerBrowserResolutionLayout(
                "1920x800",
                1920,
                800,
                PANEL_WIDTH,
                PANEL_HEIGHT,
                ROW_HEIGHT,
                ROW_WIDTH,
                false,
                true,
                new Vector3(0.70f, 0.70f, 1f),
                Vector3.one),
            new ServerBrowserResolutionLayout(
                "1920x1080",
                1920,
                1080,
                PANEL_WIDTH,
                PANEL_HEIGHT,
                ROW_HEIGHT,
                ROW_WIDTH,
                false,
                true,
                new Vector3(0.72f, 0.72f, 1f),
                Vector3.one),
            new ServerBrowserResolutionLayout(
                "1920x1200",
                1920,
                1200,
                PANEL_WIDTH,
                PANEL_HEIGHT,
                ROW_HEIGHT,
                ROW_WIDTH,
                false,
                true,
                new Vector3(0.72f, 0.72f, 1f),
                Vector3.one),
            new ServerBrowserResolutionLayout(
                "1920x1280",
                1920,
                1280,
                PANEL_WIDTH,
                PANEL_HEIGHT,
                ROW_HEIGHT,
                ROW_WIDTH,
                false,
                true,
                new Vector3(0.72f, 0.72f, 1f),
                Vector3.one),
            new ServerBrowserResolutionLayout(
                "1920x1440",
                1920,
                1440,
                PANEL_WIDTH,
                PANEL_HEIGHT,
                ROW_HEIGHT,
                ROW_WIDTH,
                false,
                true,
                new Vector3(0.72f, 0.72f, 1f),
                Vector3.one),
            new ServerBrowserResolutionLayout(
                "2048x1152",
                2048,
                1152,
                PANEL_WIDTH,
                PANEL_HEIGHT,
                ROW_HEIGHT,
                ROW_WIDTH,
                false,
                true,
                new Vector3(0.72f, 0.72f, 1f),
                Vector3.one),
            new ServerBrowserResolutionLayout(
                "2048x1536",
                2048,
                1536,
                PANEL_WIDTH,
                PANEL_HEIGHT,
                ROW_HEIGHT,
                ROW_WIDTH,
                false,
                true,
                new Vector3(0.72f, 0.72f, 1f),
                Vector3.one),
            new ServerBrowserResolutionLayout(
                "2560x1080",
                2560,
                1080,
                PANEL_WIDTH,
                PANEL_HEIGHT,
                ROW_HEIGHT,
                ROW_WIDTH,
                false,
                true,
                new Vector3(0.72f, 0.72f, 1f),
                Vector3.one),
            new ServerBrowserResolutionLayout(
                "1600x1200",
                1600,
                1200,
                PANEL_WIDTH,
                PANEL_HEIGHT,
                ROW_HEIGHT,
                ROW_WIDTH,
                false,
                true,
                new Vector3(0.72f, 0.72f, 1f),
                Vector3.one),
            new ServerBrowserResolutionLayout(
                "1680x1050",
                1680,
                1050,
                PANEL_WIDTH,
                PANEL_HEIGHT,
                ROW_HEIGHT,
                ROW_WIDTH,
                false,
                true,
                new Vector3(0.72f, 0.72f, 1f),
                Vector3.one),
            new ServerBrowserResolutionLayout(
                "1440x960",
                1440,
                960,
                PANEL_WIDTH,
                PANEL_HEIGHT,
                ROW_HEIGHT,
                ROW_WIDTH,
                false,
                true,
                new Vector3(0.72f, 0.72f, 1f),
                Vector3.one),
            new ServerBrowserResolutionLayout(
                "1440x1080",
                1440,
                1080,
                PANEL_WIDTH,
                PANEL_HEIGHT,
                ROW_HEIGHT,
                ROW_WIDTH,
                false,
                true,
                new Vector3(0.72f, 0.72f, 1f),
                Vector3.one),
            new ServerBrowserResolutionLayout(
                "1280x960",
                1280,
                960,
                PANEL_WIDTH,
                PANEL_HEIGHT,
                ROW_HEIGHT,
                ROW_WIDTH,
                false,
                true,
                new Vector3(0.72f, 0.72f, 1f),
                Vector3.one),
            new ServerBrowserResolutionLayout(
                "1280x1024",
                1280,
                1024,
                PANEL_WIDTH,
                PANEL_HEIGHT,
                ROW_HEIGHT,
                ROW_WIDTH,
                false,
                true,
                new Vector3(0.72f, 0.72f, 1f),
                Vector3.one),
            new ServerBrowserResolutionLayout(
                "1400x1050",
                1400,
                1050,
                PANEL_WIDTH,
                PANEL_HEIGHT,
                ROW_HEIGHT,
                ROW_WIDTH,
                false,
                true,
                new Vector3(0.72f, 0.72f, 1f),
                Vector3.one)
        };

        private float PanelWidth => activeLayout != null ? activeLayout.PanelWidth : PANEL_WIDTH;
        private float PanelHeight => activeLayout != null ? activeLayout.PanelHeight : PANEL_HEIGHT;
        private float RowHeight => activeLayout != null ? activeLayout.RowHeight : ROW_HEIGHT;
        private float RowWidth => activeLayout != null ? activeLayout.RowWidth : ROW_WIDTH;

        private sealed class ServerBrowserResolutionLayout
        {
            public readonly string Name;
            public readonly int ScreenWidth;
            public readonly int ScreenHeight;
            public readonly float PanelWidth;
            public readonly float PanelHeight;
            public readonly float RowHeight;
            public readonly float RowWidth;
            public readonly bool CreatePanelBackground;
            public readonly bool UseStyledGameTabs;
            public readonly Vector3 LocalScale;
            public readonly Vector3 StyledTabContainerScale;

            public ServerBrowserResolutionLayout(
                string name,
                int screenWidth,
                int screenHeight,
                float panelWidth,
                float panelHeight,
                float rowHeight,
                float rowWidth,
                bool createPanelBackground,
                bool useStyledGameTabs,
                Vector3 localScale,
                Vector3 styledTabContainerScale)
            {
                Name = name;
                ScreenWidth = screenWidth;
                ScreenHeight = screenHeight;
                PanelWidth = panelWidth;
                PanelHeight = panelHeight;
                RowHeight = rowHeight;
                RowWidth = rowWidth;
                CreatePanelBackground = createPanelBackground;
                UseStyledGameTabs = useStyledGameTabs;
                LocalScale = GetResponsiveBrowserScale(screenWidth, screenHeight, localScale);
                StyledTabContainerScale = styledTabContainerScale;
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

        private static Vector3 GetResponsiveBrowserScale(
            int screenWidth,
            int screenHeight,
            Vector3 configuredScale)
        {
            float scale;
            switch (screenHeight)
            {
                case 768:
                    scale = 0.64f;
                    break;
                case 800:
                    scale = 0.66f;
                    break;
                case 900:
                    scale = screenWidth == 1600 ? 0.72f : 0.78f;
                    break;
                case 960:
                    scale = screenWidth == 1280 ? 0.78f : 0.80f;
                    break;
                case 1024:
                    scale = 0.80f;
                    break;
                case 1050:
                    scale = screenWidth == 1400 ? 0.82f : 0.84f;
                    break;
                case 1080:
                    scale = screenWidth == 1440 ? 0.84f : 0.86f;
                    break;
                case 1152:
                case 1200:
                    scale = 0.90f;
                    break;
                case 1280:
                    scale = 0.94f;
                    break;
                case 1440:
                case 1536:
                    scale = 1.00f;
                    break;
                default:
                    return configuredScale;
            }

            return new Vector3(scale, scale, 1f);
        }

        public class ServerEntry
        {
            public string ServerID { get; set; }
            public CSteamID HostSteamID { get; set; }
            public string ServerName { get; set; }
            public string ConnectToken { get; set; }
            public int CurrentPlayers { get; set; }
            public int MaxPlayers { get; set; }
            public int Ping { get; set; }
            public string Status { get; set; }
            public bool IsFavorite { get; set; }
            public bool IsRunningGame { get; set; }
            public bool IsOffline { get; set; }
            public BrowserTab SourceTab { get; set; }
            public string DLCRequirements { get; set; }
            public string ModVersion { get; set; }
            public string ProtocolRevision { get; set; }

            public string PlayersString => IsOffline || MaxPlayers <= 0 ? "-/-" : $"{CurrentPlayers}/{MaxPlayers}";
        }

        private class PingProbeState
        {
            public CSteamID HostID;
            public int AttemptsSent;
            public float NextAttemptTime;
            public float StartedAt;
        }

        public static ServerBrowserGUI Create(Transform parent, Vector3 localPosition)
        {
            if (_instance != null)
            {
                _instance.gameObject.SetActive(true);
                _instance.ApplyResolutionLayout();
                return _instance;
            }

            CoopMod.Logger.LogInfo("[ServerBrowser] Creating server browser...");

            GameObject browserObj = new GameObject("ServerBrowserGUI");
            browserObj.layer = 13;
            browserObj.transform.SetParent(parent, false);
            browserObj.transform.localPosition = localPosition;
            browserObj.transform.localScale = Vector3.one;

            _instance = browserObj.AddComponent<ServerBrowserGUI>();
            _instance.Initialize();
            _instance.ApplyResolutionLayout();

            return _instance;
        }

        private void Initialize()
        {
            activeLayout = GetActiveResolutionLayout();
            FindRequiredResources();
            LoadFavoriteServers();
            if (activeLayout.CreatePanelBackground)
            {
                CreatePanelBackground();
            }

            if (activeLayout.UseStyledGameTabs)
            {
                CreateTabHeaders();
            }
            else
            {
                CreateFallbackTabHeaders();
            }

            CreateServerListArea();
            SwitchSubTab(BrowserTab.Friends);

            // Listen for completed lobby joins so we can dismiss the "Joining..." dialog
            // even after this GUI's GameObject has been torn down by the menu transition.
            var lobbyMgr = SteamLobbyManager.Instance;
            if (lobbyMgr != null)
            {
                lobbyMgr.OnLobbyJoined += OnLobbyJoinedHandler;
            }
            if (SteamManager.Initialized)
            {
                friendPresenceUpdateCallback =
                    Callback<FriendRichPresenceUpdate_t>.Create(OnFriendRichPresenceUpdated);
            }

            CoopMod.Logger.LogInfo("[ServerBrowser] Server browser created!");
        }

        public void ApplyResolutionLayout()
        {
            activeLayout = GetActiveResolutionLayout();
            if (activeLayout == DefaultResolutionLayout)
            {
                CoopMod.Logger.LogInfo($"[ServerBrowser] Using default unscaled layout for {Screen.width}x{Screen.height}");
                activeLayout = null;
            }

            transform.localScale = activeLayout != null ? activeLayout.LocalScale : Vector3.one;

            if (panelBackground != null)
            {
                panelBackground.width = Mathf.RoundToInt(PanelWidth);
                panelBackground.height = Mathf.RoundToInt(PanelHeight);
                panelBackground.MarkAsChanged();
            }

            if (tabBarContainer != null)
            {
                tabBarContainer.transform.localScale = activeLayout != null
                    ? activeLayout.StyledTabContainerScale
                    : Vector3.one;
            }

            if (scrollPanel != null)
            {
                scrollPanel.baseClipRegion = new Vector4(0f, 30f, PanelWidth, PanelHeight);
            }

            if (serverListContainer != null)
            {
                serverListContainer.transform.localPosition = new Vector3(0f, PanelHeight / 2f + 10f, 0f);
            }

            if (noServersLabel != null)
            {
                noServersLabel.transform.localPosition = Vector3.zero;
            }
        }

        private static ServerBrowserResolutionLayout GetActiveResolutionLayout()
        {
            int screenWidth = Screen.width;
            int screenHeight = Screen.height;
            for (int i = 0; i < ResolutionLayouts.Length; i++)
            {
                ServerBrowserResolutionLayout layout = ResolutionLayouts[i];
                if (layout.Matches(screenWidth, screenHeight))
                    return layout;
            }

            return DefaultResolutionLayout;
        }

        /// <summary>
        /// Fires from SteamLobbyManager.OnLobbyEnter once the join completes successfully.
        /// Closes the join dialog (if open) and clears the in-progress guard.
        /// </summary>
        private void OnLobbyJoinedHandler(CSteamID lobbyID)
        {
            CoopMod.Logger.LogInfo($"[ServerBrowser] Lobby join completed ({lobbyID}), dismissing dialog");
            isJoinInProgress = false;
            HideJoinDialog();
        }

        private void FindRequiredResources()
        {
            // Find game font from existing labels
            var existingLabels = UnityEngine.Object.FindObjectsOfType<UILabel>();
            foreach (var label in existingLabels)
            {
                if (label.bitmapFont != null)
                {
                    gameFont = label.bitmapFont;
                    CoopMod.Logger.LogInfo($"[ServerBrowser] Found font: {gameFont.name}");
                    break;
                }
            }

            // Find save slot template - we need its background sprite
            var saveSlotsMenus = Resources.FindObjectsOfTypeAll<SaveSlotsMenuGUI>();
            if (saveSlotsMenus.Length > 0)
            {
                var saveSlots = saveSlotsMenus[0].GetComponentsInChildren<SaveSlotGUI>(true);
                if (saveSlots != null && saveSlots.Length > 0)
                {
                    saveSlotTemplate = saveSlots[0];
                    
                    // Get the background sprite from the save slot
                    if (saveSlotTemplate.back != null && saveSlotTemplate.back.sprite2D != null)
                    {
                        backgroundSprite = saveSlotTemplate.back.sprite2D;
                        CoopMod.Logger.LogInfo($"[ServerBrowser] Found background sprite: {backgroundSprite.name}");
                    }
                }
            }
        }

        private void CreatePanelBackground()
        {
            // Create background using the game's sprite
            GameObject bgObj = new GameObject("PanelBackground");
            bgObj.layer = 13;
            bgObj.transform.SetParent(transform, false);
            bgObj.transform.localPosition = new Vector3(0, -20, 0);

            panelBackground = bgObj.AddComponent<UI2DSprite>();
            panelBackground.depth = 10;
            panelBackground.width = Mathf.RoundToInt(PanelWidth);
            panelBackground.height = Mathf.RoundToInt(PanelHeight);

            if (backgroundSprite != null)
            {
                panelBackground.sprite2D = backgroundSprite;
                panelBackground.type = UIBasicSprite.Type.Sliced;
                panelBackground.border = new Vector4(16, 16, 16, 16); // 9-slice borders
                CoopMod.Logger.LogInfo("[ServerBrowser] Applied background sprite to panel");
            }
            else
            {
                // Fallback: dark color
                panelBackground.color = new Color(0.1f, 0.1f, 0.12f, 0.95f);
                CoopMod.Logger.LogWarning("[ServerBrowser] No background sprite found, using dark color");
            }
        }

        private void CreateTabHeaders()
        {
            // Try to clone the entire GameGUI tab bar with its background
            var gameGUI = GUIElements.me?.game_gui;
            if (gameGUI == null)
            {
                CoopMod.Logger.LogWarning("[ServerBrowser] GameGUI not found, using fallback tabs");
                CreateFallbackTabHeaders();
                return;
            }

            // Clone the ENTIRE GameGUI to get the full tab bar with background
            // Then we'll strip out everything except the tab bar
            CoopMod.Logger.LogInfo($"[ServerBrowser] Cloning GameGUI: {gameGUI.name}");

            tabBarContainer = UnityEngine.Object.Instantiate(gameGUI.gameObject, transform);
            tabBarContainer.name = "TabBarClone";
            tabBarContainer.transform.localPosition = new Vector3(0, 0, 0);
            tabBarContainer.transform.localScale = Vector3.one;
            tabBarContainer.SetActive(true);

            // The cloned GameGUI contains navigation components owned by the
            // original inventory screen. They must not become selectable inside
            // JoinGameGUI's controller.
            foreach (var controller in tabBarContainer.GetComponentsInChildren<GamepadNavigationController>(true))
                UnityEngine.Object.DestroyImmediate(controller);
            foreach (var navigationItem in tabBarContainer.GetComponentsInChildren<GamepadNavigationItem>(true))
                UnityEngine.Object.DestroyImmediate(navigationItem);
            foreach (var menuItem in tabBarContainer.GetComponentsInChildren<MenuItemGUI>(true))
                UnityEngine.Object.DestroyImmediate(menuItem);

            // Remove the GameGUI component itself
            var gameGUIComponent = tabBarContainer.GetComponent<GameGUI>();
            if (gameGUIComponent != null)
                UnityEngine.Object.DestroyImmediate(gameGUIComponent);

            // Remove BaseGUI component if present
            var baseGUI = tabBarContainer.GetComponent<BaseGUI>();
            if (baseGUI != null)
                UnityEngine.Object.DestroyImmediate(baseGUI);

            // Find the UITable (tabs container)
            var clonedTable = tabBarContainer.GetComponentInChildren<UITable>(true);

            // Hide everything EXCEPT the tab bar area
            // The tab bar is typically at the top, content panels are below
            foreach (Transform child in tabBarContainer.transform)
            {
                string childName = child.name.ToLower();
                
                // Keep anything related to tabs or the top bar
                if (childName.Contains("tab") || childName.Contains("top") || childName.Contains("header") || childName.Contains("bar"))
                {
                    child.gameObject.SetActive(true);
                    continue;
                }

                // Hide content panels (inventory, tech tree, etc.)
                if (childName.Contains("inventory") || childName.Contains("tech") || 
                    childName.Contains("npc") || childName.Contains("map") || 
                    childName.Contains("content") || childName.Contains("panel") ||
                    childName.Contains("chat"))
                {
                    child.gameObject.SetActive(false);
                }
            }

            // Remove all existing GameTabItemGUI components and their tabs
            var existingTabs = tabBarContainer.GetComponentsInChildren<GameTabItemGUI>(true);
            foreach (var existingTab in existingTabs)
            {
                existingTab.gameObject.SetActive(false);
            }

            // Find a tab template from the original GameGUI
            var tabTemplate = gameGUI.GetComponentInChildren<GameTabItemGUI>(true);
            if (tabTemplate == null)
            {
                CoopMod.Logger.LogWarning("[ServerBrowser] Tab template not found");
                return;
            }

            // Create our 4 tabs
            string[] tabNames = { "Internet", "Friends", "Favorites", "LAN" };
            int index = 0;
            foreach (BrowserTab tab in Enum.GetValues(typeof(BrowserTab)))
            {
                GameObject tabObj = UnityEngine.Object.Instantiate(tabTemplate.gameObject, clonedTable != null ? clonedTable.transform : tabBarContainer.transform);
                tabObj.name = $"Tab_{tab}";
                tabObj.transform.localScale = Vector3.one;
                tabObj.SetActive(true);

                // Log all children to understand tab structure
                CoopMod.Logger.LogInfo($"[ServerBrowser] Tab {tab} children:");
                foreach (Transform child in tabObj.GetComponentsInChildren<Transform>(true))
                {
                    var sprites = child.GetComponents<Component>();
                    string components = string.Join(", ", System.Array.ConvertAll(sprites, c => c.GetType().Name));
                    CoopMod.Logger.LogInfo($"  - {child.name} [{components}] active={child.gameObject.activeSelf}");
                }

                // Remove the GameTabItemGUI component
                var gameTabItem = tabObj.GetComponent<GameTabItemGUI>();
                if (gameTabItem != null)
                    UnityEngine.Object.DestroyImmediate(gameTabItem);

                // Find and update the label
                var label = tabObj.GetComponentInChildren<UILabel>(true);
                if (label != null)
                {
                    label.text = tabNames[index];
                    // Don't override color - keep original from template
                    tabLabels[tab] = label;
                    
                    // Remove localization component
                    var localizedLabel = label.GetComponent<LocalizedLabel>();
                    if (localizedLabel != null)
                        UnityEngine.Object.DestroyImmediate(localizedLabel);
                }

                ConfigureTabNavigation(tabObj, tab, label);

                // Set up button click using UIEventListener
                var button = tabObj.GetComponentInChildren<UIButton>(true);
                if (button != null)
                {
                    var clickHandler = tabObj.AddComponent<BrowserTabClickHandler>();
                    clickHandler.Initialize(this, tab);
                    
                    // Wire up the click event
                    UIEventListener listener = UIEventListener.Get(button.gameObject);
                    BrowserTab capturedTab = tab; // Capture for closure
                    listener.onClick = (go) => { this.SwitchSubTab(capturedTab); };
                    listener.onHover = (go, isOver) =>
                    {
                        if (isOver)
                            FocusSubTab(capturedTab);
                        else
                            FocusSubTab(currentTab);
                    };
                }

                // Control the selection highlight - "back active" sprites
                // Only show for current tab, hide for all others
                bool isSelected = (tab == currentTab);
                
                foreach (var child in tabObj.GetComponentsInChildren<Transform>(true))
                {
                    string childName = child.name.ToLower();
                    // Toggle all "back active" sprites based on selection
                    if (childName.Contains("back active") || childName == "active")
                    {
                        child.gameObject.SetActive(isSelected);
                        // Don't touch color - use whatever the cloned sprite has
                    }
                }

                index++;
            }

            // Reposition the table
            if (clonedTable != null)
            {
                clonedTable.Reposition();
            }

            // Remove the close button (X) if present
            foreach (Transform child in tabBarContainer.GetComponentsInChildren<Transform>(true))
            {
                string childName = child.name.ToLower();
                if (childName.Contains("close") || childName.Contains("btn_x") || childName == "x")
                {
                    child.gameObject.SetActive(false);
                }
            }

            CoopMod.Logger.LogInfo("[ServerBrowser] Created styled tab bar with background");
        }

        private void CreateFallbackTabHeaders()
        {
            // Fallback: simple row of tab labels
            float startX = -210f;
            float tabSpacing = 140f;
            float yPos = PanelHeight / 2f + 25f;

            int index = 0;
            foreach (BrowserTab tab in Enum.GetValues(typeof(BrowserTab)))
            {
                float xPos = startX + (index * tabSpacing);
                CreateFallbackTabLabel(tab, new Vector3(xPos, yPos, 0));
                index++;
            }
        }

        private void CreateFallbackTabLabel(BrowserTab tab, Vector3 position)
        {
            GameObject tabObj = new GameObject($"Tab_{tab}");
            tabObj.layer = 13;
            tabObj.transform.SetParent(transform, false);
            tabObj.transform.localPosition = position;

            var label = tabObj.AddComponent<UILabel>();
            label.bitmapFont = gameFont;
            label.text = tab.ToString();
            label.fontSize = 18;
            label.color = (tab == currentTab) ? HEADER_ACTIVE_COLOR : HEADER_INACTIVE_COLOR;
            label.alignment = NGUIText.Alignment.Center;
            label.overflowMethod = UILabel.Overflow.ResizeFreely;
            label.depth = 30;

            tabLabels[tab] = label;

            // Add clickable collider
            var col = tabObj.AddComponent<BoxCollider>();
            col.size = new Vector3(90, 25, 1);
            col.isTrigger = true;

            var clickHandler = tabObj.AddComponent<BrowserTabClickHandler>();
            clickHandler.Initialize(this, tab);
            ConfigureTabNavigation(tabObj, tab, label);
        }

        private void ConfigureTabNavigation(GameObject tabObj, BrowserTab tab, UILabel label)
        {
            if (tabObj == null)
                return;

            foreach (var childItem in tabObj.GetComponentsInChildren<GamepadNavigationItem>(true))
            {
                if (childItem.gameObject != tabObj)
                    UnityEngine.Object.DestroyImmediate(childItem);
            }

            var navigationItem = tabObj.GetComponent<GamepadNavigationItem>() ??
                                 tabObj.AddComponent<GamepadNavigationItem>();
            navigationItem.active = true;
            UIWidget nativeHighlight = FindNativeTabHighlight(tabObj);
            ControllerFocusFrame.Attach(
                tabObj,
                navigationItem,
                boundsWidget: nativeHighlight,
                horizontalPadding: 14,
                verticalPadding: 0);
            if (label != null)
                tabNormalColors[tab] = label.color;
            navigationItem.SetCallbacks(
                () => SetTabControllerFocus(tab, label, true),
                () => SetTabControllerFocus(tab, label, false),
                () => SwitchSubTab(tab));
            tabNavigationItems[tab] = navigationItem;
        }

        private static UIWidget FindNativeTabHighlight(GameObject tabObj)
        {
            if (tabObj == null)
                return null;

            UIWidget fallback = null;
            UI2DSprite[] sprites = tabObj.GetComponentsInChildren<UI2DSprite>(true);
            for (int i = 0; i < sprites.Length; i++)
            {
                UI2DSprite sprite = sprites[i];
                if (sprite == null ||
                    !sprite.gameObject.name.StartsWith(
                        "back active",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (sprite.gameObject.activeSelf)
                    return sprite;
                if (fallback == null)
                    fallback = sprite;
            }

            return fallback;
        }

        private void SetTabControllerFocus(BrowserTab tab, UILabel label, bool focused)
        {
            if (label == null)
                return;

            if (focused)
            {
                label.color = HEADER_ACTIVE_COLOR;
            }
            else if (tabBarContainer == null)
            {
                label.color = tab == currentTab ? HEADER_ACTIVE_COLOR : HEADER_INACTIVE_COLOR;
            }
            else
            {
                // Styled tabs show selection with their "back active" sprite.
                Color normalColor;
                label.color = tabNormalColors.TryGetValue(tab, out normalColor)
                    ? normalColor
                    : Color.white;
            }

            label.MarkAsChanged();
        }

        private void CreateServerListArea()
        {
            // Create scroll panel for the server list
            GameObject scrollObj = new GameObject("ServerScrollView");
            scrollObj.layer = 13;
            scrollObj.transform.SetParent(transform, false);
            scrollObj.transform.localPosition = new Vector3(0, 15, 0);

            scrollPanel = scrollObj.AddComponent<UIPanel>();
            scrollPanel.depth = 20;
            scrollPanel.clipping = UIDrawCall.Clipping.SoftClip;
            scrollPanel.baseClipRegion = new Vector4(0, 30, PanelWidth, PanelHeight);
            scrollPanel.clipSoftness = new Vector2(5, 5);

            scrollView = scrollObj.AddComponent<UIScrollView>();
            scrollView.movement = UIScrollView.Movement.Vertical;
            scrollView.contentPivot = UIWidget.Pivot.Top;
            scrollView.dragEffect = UIScrollView.DragEffect.MomentumAndSpring;
            scrollView.scrollWheelFactor = 0.5f;

            // Container for server rows
            serverListContainer = new GameObject("ServerList");
            serverListContainer.layer = 13;
            serverListContainer.transform.SetParent(scrollObj.transform, false);
            serverListContainer.transform.localPosition = new Vector3(0, PanelHeight / 2f + 10f, 0);

            // "No servers" message label
            GameObject noServersObj = new GameObject("NoServersLabel");
            noServersObj.layer = 13;
            noServersObj.transform.SetParent(transform, false);
            noServersObj.transform.localPosition = new Vector3(0, 0, 0);

            noServersLabel = noServersObj.AddComponent<UILabel>();
            noServersLabel.bitmapFont = gameFont;
            noServersLabel.text = "No friends hosting games";
            noServersLabel.fontSize = 16;
            noServersLabel.color = DIM_TEXT_COLOR;
            noServersLabel.alignment = NGUIText.Alignment.Center;
            noServersLabel.overflowMethod = UILabel.Overflow.ResizeFreely;
            noServersLabel.depth = 30;
        }

        public void SwitchSubTab(BrowserTab tab)
        {
            HideFavoriteMenu();

            if (currentTab == BrowserTab.LAN && tab != BrowserTab.LAN)
            {
                LanDiscoveryService.Instance.StopListening();
                lastLanSnapshotFingerprint = "";
            }

            currentTab = tab;
            FocusSubTab(tab);

            // Update tab states for styled tabs
            if (tabBarContainer != null)
            {
                foreach (BrowserTab t in Enum.GetValues(typeof(BrowserTab)))
                {
                    // Search recursively for the tab object
                    Transform tabObj = null;
                    foreach (var child in tabBarContainer.GetComponentsInChildren<Transform>(true))
                    {
                        if (child.name == $"Tab_{t}")
                        {
                            tabObj = child;
                            break;
                        }
                    }
                    
                    if (tabObj != null)
                    {
                        bool isSelected = (t == tab);
                        
                        // Toggle the selection highlight sprites
                        foreach (Transform child in tabObj.GetComponentsInChildren<Transform>(true))
                        {
                            string childName = child.name.ToLower();
                            if (childName.Contains("back active") || childName == "active")
                            {
                                child.gameObject.SetActive(isSelected);
                            }
                        }
                    }
                }
            }
            else
            {
                foreach (var pair in tabLabels)
                {
                    if (pair.Value == null)
                        continue;

                    pair.Value.color = pair.Key == currentTab ? HEADER_ACTIVE_COLOR : HEADER_INACTIVE_COLOR;
                    pair.Value.MarkAsChanged();
                }
            }

            // Note: Don't override label colors for styled tabs - they keep their original colors

            RefreshServerList();
            Sounds.OnGUITabClick();
        }

        internal void FocusSubTab(BrowserTab tab)
        {
            if (!tabNavigationItems.TryGetValue(tab, out GamepadNavigationItem item) ||
                item == null)
            {
                return;
            }

            JoinGameGUI.Instance?.FocusControllerItem(item);
        }

        public void RefreshServerList()
        {
            CoopMod.Logger.LogInfo($"[ServerBrowser] Refreshing {currentTab} tab...");
            LoadFavoriteServers();
            HideFavoriteMenu();

            // Reset join state when refreshing
            isJoinInProgress = false;

            serverEntries.Clear();
            ClearServerRows();
            selectedServer = null;
            selectedRowObj = null;

            switch (currentTab)
            {
                case BrowserTab.Internet:
                    noServersLabel.text = "Searching internet...";
                    noServersLabel.gameObject.SetActive(true);
                    StartPublicLobbySearch();
                    break;

                case BrowserTab.Friends:
                    RefreshFriendsServers();
                    // Async lobby search will populate via callback
                    // Show searching state while waiting
                    if (serverEntries.Count == 0)
                    {
                        noServersLabel.text = "Searching for friends...";
                        noServersLabel.gameObject.SetActive(true);
                    }
                    // Also fire async Steam lobby search for richer results
                    StartFriendLobbySearch();
                    break;

                case BrowserTab.Favorites:
                    StartFavoriteLobbySearch();
                    return;

                case BrowserTab.LAN:
                    StartLanServerSearch();
                    return;
            }

            if (serverEntries.Count > 0)
            {
                noServersLabel.gameObject.SetActive(false);
                PopulateServerRows();
                if (currentTab == BrowserTab.Friends)
                {
                    SendPingsToVisibleHosts();
                }
            }
            else
            {
                noServersLabel.text = currentTab == BrowserTab.Friends
                    ? "Searching for friends..."
                    : currentTab == BrowserTab.Internet
                        ? "Searching internet..."
                        : "No servers found";
                noServersLabel.gameObject.SetActive(true);
            }
        }

        private void RefreshFriendsServers()
        {
            if (!SteamManager.Initialized)
            {
                CoopMod.Logger.LogWarning("[ServerBrowser] Steam not initialized");
                return;
            }

            // Quick pass: check Rich Presence for friends currently hosting
            // This gives instant results while lobby search is async
            int friendCount = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            CoopMod.Logger.LogInfo($"[ServerBrowser] Checking {friendCount} Steam friends via Rich Presence...");

            for (int i = 0; i < friendCount; i++)
            {
                CSteamID friendID = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                string friendName = SteamFriends.GetFriendPersonaName(friendID);
                string connectToken = SteamFriends.GetFriendRichPresence(friendID, "connect");
                string status = SteamFriends.GetFriendRichPresence(friendID, "status");
                string dlcRequirements = SteamFriends.GetFriendRichPresence(friendID, SteamLobbyManager.LobbyDataDLC);
                string modVersion = SteamFriends.GetFriendRichPresence(
                    friendID,
                    SteamLobbyManager.LobbyDataModVersion);
                string protocolRevision = SteamFriends.GetFriendRichPresence(
                    friendID,
                    SteamLobbyManager.LobbyDataProtocolRevision);
                bool isRunningGame = SteamLobbyManager.IsRunningGameStatus(status);

                if (!string.IsNullOrEmpty(connectToken))
                {
                    CoopMod.Logger.LogInfo($"[ServerBrowser] Found friend hosting via Rich Presence: {friendName}");
                    serverEntries.Add(new ServerEntry
                    {
                        ServerID = friendID.ToString(),
                        HostSteamID = friendID,
                        ServerName = $"{friendName}'s Game",
                        ConnectToken = connectToken,
                        CurrentPlayers = 1,
                        MaxPlayers = 4,
                        Ping = -1,
                        Status = GetServerStatusText(status, isRunningGame),
                        IsFavorite = IsFavoriteServer(friendID, friendID.m_SteamID.ToString()),
                        IsRunningGame = isRunningGame,
                        SourceTab = BrowserTab.Friends,
                        DLCRequirements = dlcRequirements ?? "",
                        ModVersion = modVersion ?? "",
                        ProtocolRevision = protocolRevision ?? ""
                    });
                }
            }

            CoopMod.Logger.LogInfo($"[ServerBrowser] Rich Presence pass found {serverEntries.Count} friends hosting");
        }

        /// <summary>
        /// Start an async Steam lobby search for friends' lobbies.
        /// Results merge with Rich Presence results when they arrive.
        /// </summary>
        private void StartFriendLobbySearch()
        {
            var lobbyMgr = SteamLobbyManager.Instance;
            if (lobbyMgr == null) return;

            // Subscribe to results (unsubscribe first to avoid duplicate handlers)
            lobbyMgr.OnLobbySearchCompleted -= OnFriendLobbySearchResults;
            lobbyMgr.OnLobbySearchCompleted -= OnPublicLobbySearchResults;
            lobbyMgr.OnLobbySearchCompleted -= OnFavoriteLobbySearchResults;
            lobbyMgr.OnLobbySearchCompleted += OnFriendLobbySearchResults;

            lobbyMgr.RequestFriendLobbies();
        }

        private void StartPublicLobbySearch()
        {
            var lobbyMgr = SteamLobbyManager.Instance;
            if (lobbyMgr == null) return;

            lobbyMgr.OnLobbySearchCompleted -= OnFriendLobbySearchResults;
            lobbyMgr.OnLobbySearchCompleted -= OnPublicLobbySearchResults;
            lobbyMgr.OnLobbySearchCompleted -= OnFavoriteLobbySearchResults;
            lobbyMgr.OnLobbySearchCompleted += OnPublicLobbySearchResults;

            lobbyMgr.RequestPublicLobbies();
        }

        private void StartFavoriteLobbySearch()
        {
            if (favoriteServers.Count == 0)
            {
                noServersLabel.text = "No favorite servers";
                noServersLabel.gameObject.SetActive(true);
                return;
            }

            RequestFavoritePresenceUpdates();
            BuildFavoriteServerEntries(null);
            noServersLabel.gameObject.SetActive(false);
            PopulateServerRows();

            var lobbyMgr = SteamLobbyManager.Instance;
            if (lobbyMgr == null) return;

            lobbyMgr.OnLobbySearchCompleted -= OnFriendLobbySearchResults;
            lobbyMgr.OnLobbySearchCompleted -= OnPublicLobbySearchResults;
            lobbyMgr.OnLobbySearchCompleted -= OnFavoriteLobbySearchResults;
            lobbyMgr.OnLobbySearchCompleted += OnFavoriteLobbySearchResults;

            lobbyMgr.RequestFavoriteLobbies();
        }

        private void RequestFavoritePresenceUpdates()
        {
            if (!SteamManager.Initialized)
                return;

            foreach (string key in favoriteServers)
            {
                ulong hostIDValue;
                if (ulong.TryParse(key, out hostIDValue))
                    SteamFriends.RequestFriendRichPresence(new CSteamID(hostIDValue));
            }
        }

        private void StartLanServerSearch()
        {
            noServersLabel.text = "Searching LAN...";
            noServersLabel.gameObject.SetActive(true);
            lastLanSnapshotFingerprint = "";
            LanDiscoveryService.Instance.StartListening();
            LanDiscoveryService.Instance.SendDiscoveryProbe();
            nextLanProbeTime = Time.unscaledTime + LAN_PROBE_INTERVAL;
            RefreshLanServersFromDiscovery(force: true);
        }

        private void RefreshLanServersFromDiscovery(bool force)
        {
            if (currentTab != BrowserTab.LAN)
                return;

            List<LanServerInfo> lanServers = LanDiscoveryService.Instance.GetServers();
            string fingerprint = BuildLanSnapshotFingerprint(lanServers);
            if (!force && fingerprint == lastLanSnapshotFingerprint)
                return;

            lastLanSnapshotFingerprint = fingerprint;
            serverEntries.Clear();
            ClearServerRows();
            selectedServer = null;
            selectedRowObj = null;

            CSteamID localSteamID = SteamManager.Initialized ? SteamUser.GetSteamID() : CSteamID.Nil;
            foreach (var lanServer in lanServers)
            {
                if (lanServer.HostSteamID != CSteamID.Nil && lanServer.HostSteamID == localSteamID)
                    continue;

                bool isRunningGame = SteamLobbyManager.IsRunningGameStatus(lanServer.Status);
                string hostName = string.IsNullOrEmpty(lanServer.HostName) ? lanServer.Endpoint : lanServer.HostName;
                serverEntries.Add(new ServerEntry
                {
                    ServerID = lanServer.LobbyID.ToString(),
                    HostSteamID = lanServer.HostSteamID,
                    ServerName = $"{hostName}'s Game ({lanServer.Endpoint})",
                    ConnectToken = "",
                    CurrentPlayers = lanServer.CurrentPlayers,
                    MaxPlayers = lanServer.MaxPlayers,
                    Ping = -1,
                    Status = GetServerStatusText(lanServer.Status, isRunningGame),
                    IsFavorite = IsFavoriteServer(lanServer.HostSteamID, lanServer.LobbyID.ToString()),
                    IsRunningGame = isRunningGame,
                    SourceTab = BrowserTab.LAN,
                    DLCRequirements = lanServer.DLCRequirements ?? "",
                    ModVersion = lanServer.ModVersion ?? "",
                    ProtocolRevision = lanServer.ProtocolRevision ?? ""
                });
            }

            if (serverEntries.Count == 0)
            {
                AddLanRichPresenceFallbackServers();
            }

            if (serverEntries.Count > 0)
            {
                noServersLabel.gameObject.SetActive(false);
                PopulateServerRows();
                SendPingsToVisibleHosts();
            }
            else
            {
                noServersLabel.text = "No LAN sessions found";
                noServersLabel.gameObject.SetActive(true);
            }
        }

        private void AddLanRichPresenceFallbackServers()
        {
            if (!SteamManager.Initialized)
                return;

            int friendCount = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            int added = 0;
            for (int i = 0; i < friendCount; i++)
            {
                CSteamID friendID = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                ServerEntry entry;
                if (!TryCreateRichPresenceServerEntry(friendID, BrowserTab.LAN, " (Steam)", out entry))
                    continue;

                serverEntries.Add(entry);
                added++;
            }

            if (added > 0)
                CoopMod.Logger.LogInfo($"[ServerBrowser] LAN tab using {added} Rich Presence compatibility session(s); host may be running an older DLL without LAN discovery");
        }

        private static string BuildLanSnapshotFingerprint(List<LanServerInfo> lanServers)
        {
            if (lanServers == null || lanServers.Count == 0)
                return "";

            lanServers.Sort((a, b) => a.LobbyID.m_SteamID.CompareTo(b.LobbyID.m_SteamID));
            var parts = new List<string>();
            foreach (var server in lanServers)
            {
                parts.Add($"{server.LobbyID.m_SteamID}:{server.HostSteamID.m_SteamID}:{server.CurrentPlayers}:{server.MaxPlayers}:{server.Status}:{server.Endpoint}:{server.DLCRequirements}:{server.ModVersion}:{server.ProtocolRevision}");
            }

            return string.Join(";", parts.ToArray());
        }

        private void LoadFavoriteServers()
        {
            favoriteServers.Clear();
            favoriteServerNames.Clear();

            string rawFavorites = ModConfig.FavoriteServerHostIds?.Value;
            if (!string.IsNullOrEmpty(rawFavorites))
            {
                string[] parts = rawFavorites.Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string part in parts)
                {
                    string key = NormalizeFavoriteKey(part);
                    if (string.IsNullOrEmpty(key))
                        continue;

                    if (!favoriteServers.Contains(key))
                        favoriteServers.Add(key);
                }
            }

            string rawNames = ModConfig.FavoriteServerNames?.Value;
            if (string.IsNullOrEmpty(rawNames))
                return;

            string[] nameParts = rawNames.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in nameParts)
            {
                int separator = part.IndexOf('=');
                if (separator <= 0 || separator >= part.Length - 1)
                    continue;

                string key = NormalizeFavoriteKey(part.Substring(0, separator));
                if (string.IsNullOrEmpty(key))
                    continue;

                string decodedName = DecodeFavoriteName(part.Substring(separator + 1));
                if (!string.IsNullOrEmpty(decodedName))
                    favoriteServerNames[key] = decodedName;
            }
        }

        private void SaveFavoriteServers()
        {
            if (ModConfig.FavoriteServerHostIds != null)
                ModConfig.FavoriteServerHostIds.Value = string.Join(",", favoriteServers.ToArray());

            if (ModConfig.FavoriteServerNames != null)
            {
                var parts = new List<string>();
                foreach (string key in favoriteServers)
                {
                    string name;
                    if (favoriteServerNames.TryGetValue(key, out name) && !string.IsNullOrEmpty(name))
                        parts.Add($"{key}={EncodeFavoriteName(name)}");
                }

                ModConfig.FavoriteServerNames.Value = string.Join(";", parts.ToArray());
            }
        }

        private static string NormalizeFavoriteKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                return "";

            key = key.Trim();
            ulong ignored;
            return ulong.TryParse(key, out ignored) ? key : "";
        }

        private static string EncodeFavoriteName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "";

            return Convert.ToBase64String(Encoding.UTF8.GetBytes(name));
        }

        private static string DecodeFavoriteName(string encodedName)
        {
            if (string.IsNullOrEmpty(encodedName))
                return "";

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(encodedName));
            }
            catch
            {
                return "";
            }
        }

        private static string GetFavoriteKey(ServerEntry server)
        {
            if (server == null)
                return "";

            return GetFavoriteKey(server.HostSteamID, server.ServerID);
        }

        private static string GetFavoriteKey(CSteamID hostSteamID, string fallbackServerID)
        {
            if (hostSteamID != CSteamID.Nil)
                return hostSteamID.m_SteamID.ToString();

            return NormalizeFavoriteKey(fallbackServerID);
        }

        private bool IsFavoriteServer(ServerEntry server)
        {
            if (server == null)
                return false;

            return IsFavoriteServer(server.HostSteamID, server.ServerID);
        }

        private bool IsFavoriteServer(CSteamID hostSteamID, string fallbackServerID)
        {
            string key = GetFavoriteKey(hostSteamID, fallbackServerID);
            return !string.IsNullOrEmpty(key) && favoriteServers.Contains(key);
        }

        private string GetFavoriteDisplayName(ServerEntry server)
        {
            if (server == null)
                return "";

            if (!string.IsNullOrEmpty(server.ServerName))
                return server.ServerName;

            return GetFavoriteDisplayName(GetFavoriteKey(server));
        }

        private string GetFavoriteDisplayName(string key)
        {
            string savedName;
            if (!string.IsNullOrEmpty(key) && favoriteServerNames.TryGetValue(key, out savedName) && !string.IsNullOrEmpty(savedName))
                return savedName;

            if (!string.IsNullOrEmpty(key) && SteamManager.Initialized)
            {
                ulong steamIDValue;
                if (ulong.TryParse(key, out steamIDValue))
                {
                    var steamID = new CSteamID(steamIDValue);
                    string personaName = SteamFriends.GetFriendPersonaName(steamID);
                    if (!string.IsNullOrEmpty(personaName))
                        return $"{personaName}'s Game";
                }
            }

            return string.IsNullOrEmpty(key) || key.Length < 4
                ? "Favorite Server"
                : $"Favorite Server {key.Substring(key.Length - 4)}";
        }

        private void BuildFavoriteServerEntries(List<LobbySearchResult> liveResults)
        {
            var liveByFavoriteKey = new Dictionary<string, LobbySearchResult>();
            if (liveResults != null)
            {
                foreach (var lobby in liveResults)
                {
                    string key = GetFavoriteKey(lobby.HostSteamID, lobby.LobbyID.ToString());
                    if (!string.IsNullOrEmpty(key) && favoriteServers.Contains(key))
                        liveByFavoriteKey[key] = lobby;
                }
            }

            var richPresenceByFavoriteKey = BuildFavoriteRichPresenceEntries();
            bool favoriteNamesChanged = false;
            serverEntries.Clear();
            ClearServerRows();
            selectedServer = null;
            selectedRowObj = null;

            foreach (string key in favoriteServers)
            {
                LobbySearchResult liveLobby;
                if (liveByFavoriteKey.TryGetValue(key, out liveLobby))
                {
                    bool isRunningGame = SteamLobbyManager.IsRunningGameStatus(liveLobby.Status);
                    string serverName = string.IsNullOrEmpty(liveLobby.HostName)
                        ? GetFavoriteDisplayName(key)
                        : $"{liveLobby.HostName}'s Game";
                    ServerEntry richPresenceEntryForLive;
                    richPresenceByFavoriteKey.TryGetValue(key, out richPresenceEntryForLive);
                    serverEntries.Add(new ServerEntry
                    {
                        ServerID = liveLobby.LobbyID.ToString(),
                        HostSteamID = liveLobby.HostSteamID,
                        ServerName = serverName,
                        ConnectToken = richPresenceEntryForLive != null ? richPresenceEntryForLive.ConnectToken : "",
                        CurrentPlayers = liveLobby.CurrentPlayers,
                        MaxPlayers = liveLobby.MaxPlayers,
                        Ping = -1,
                        Status = GetServerStatusText(liveLobby.Status, isRunningGame),
                        IsFavorite = true,
                        IsRunningGame = isRunningGame,
                        IsOffline = false,
                        SourceTab = BrowserTab.Favorites,
                        DLCRequirements = !string.IsNullOrEmpty(liveLobby.DLCRequirements)
                            ? liveLobby.DLCRequirements
                            : richPresenceEntryForLive?.DLCRequirements ?? "",
                        ModVersion = !string.IsNullOrEmpty(liveLobby.ModVersion)
                            ? liveLobby.ModVersion
                            : richPresenceEntryForLive?.ModVersion ?? "",
                        ProtocolRevision = !string.IsNullOrEmpty(liveLobby.ProtocolRevision)
                            ? liveLobby.ProtocolRevision
                            : richPresenceEntryForLive?.ProtocolRevision ?? ""
                    });

                    if (!favoriteServerNames.ContainsKey(key) || favoriteServerNames[key] != serverName)
                    {
                        favoriteServerNames[key] = serverName;
                        favoriteNamesChanged = true;
                    }

                    continue;
                }

                ServerEntry richPresenceEntry;
                if (richPresenceByFavoriteKey.TryGetValue(key, out richPresenceEntry))
                {
                    serverEntries.Add(richPresenceEntry);

                    if (!favoriteServerNames.ContainsKey(key) || favoriteServerNames[key] != richPresenceEntry.ServerName)
                    {
                        favoriteServerNames[key] = richPresenceEntry.ServerName;
                        favoriteNamesChanged = true;
                    }

                    CoopMod.Logger.LogInfo($"[ServerBrowser] Favorite server is online via Rich Presence: {richPresenceEntry.ServerName}");
                    continue;
                }

                ulong hostIDValue;
                CSteamID hostID = ulong.TryParse(key, out hostIDValue) ? new CSteamID(hostIDValue) : CSteamID.Nil;
                serverEntries.Add(new ServerEntry
                {
                    ServerID = key,
                    HostSteamID = hostID,
                    ServerName = GetFavoriteDisplayName(key),
                    ConnectToken = "",
                    CurrentPlayers = 0,
                    MaxPlayers = 0,
                    Ping = -1,
                    Status = "Offline",
                    IsFavorite = true,
                    IsRunningGame = false,
                    IsOffline = true,
                    SourceTab = BrowserTab.Favorites
                });
            }

            if (favoriteNamesChanged)
                SaveFavoriteServers();
        }

        private Dictionary<string, ServerEntry> BuildFavoriteRichPresenceEntries()
        {
            var entries = new Dictionary<string, ServerEntry>();
            if (!SteamManager.Initialized || favoriteServers.Count == 0)
                return entries;

            foreach (string key in favoriteServers)
            {
                ulong hostIDValue;
                if (!ulong.TryParse(key, out hostIDValue))
                    continue;

                CSteamID hostID = new CSteamID(hostIDValue);
                ServerEntry entry;
                if (TryCreateRichPresenceServerEntry(hostID, BrowserTab.Favorites, "", out entry))
                    entries[key] = entry;
            }

            return entries;
        }

        private bool TryCreateRichPresenceServerEntry(CSteamID hostID, BrowserTab sourceTab, string serverNameSuffix, out ServerEntry entry)
        {
            entry = null;
            if (!SteamManager.Initialized || hostID == CSteamID.Nil)
                return false;

            string connectToken = SteamFriends.GetFriendRichPresence(hostID, "connect");
            if (string.IsNullOrEmpty(connectToken))
                return false;

            CSteamID tokenHostID = SteamJoinFlow.ParseConnectToken(connectToken);
            if (tokenHostID != CSteamID.Nil && tokenHostID != hostID)
                return false;

            string status = SteamFriends.GetFriendRichPresence(hostID, "status");
            string dlcRequirements = SteamFriends.GetFriendRichPresence(hostID, SteamLobbyManager.LobbyDataDLC);
            string modVersion = SteamFriends.GetFriendRichPresence(
                hostID,
                SteamLobbyManager.LobbyDataModVersion);
            string protocolRevision = SteamFriends.GetFriendRichPresence(
                hostID,
                SteamLobbyManager.LobbyDataProtocolRevision);
            bool isRunningGame = SteamLobbyManager.IsRunningGameStatus(status);
            string hostName = SteamFriends.GetFriendPersonaName(hostID);
            if (string.IsNullOrEmpty(hostName))
                hostName = $"Host {hostID.m_SteamID % 10000}";

            entry = new ServerEntry
            {
                ServerID = hostID.m_SteamID.ToString(),
                HostSteamID = hostID,
                ServerName = $"{hostName}'s Game{serverNameSuffix}",
                ConnectToken = connectToken,
                CurrentPlayers = 1,
                MaxPlayers = 4,
                Ping = -1,
                Status = GetServerStatusText(status, isRunningGame),
                IsFavorite = IsFavoriteServer(hostID, hostID.m_SteamID.ToString()),
                IsRunningGame = isRunningGame,
                IsOffline = false,
                SourceTab = sourceTab,
                DLCRequirements = dlcRequirements ?? "",
                ModVersion = modVersion ?? "",
                ProtocolRevision = protocolRevision ?? ""
            };
            return true;
        }

        private void SetFavoriteServer(ServerEntry server, bool isFavorite)
        {
            string key = GetFavoriteKey(server);
            if (string.IsNullOrEmpty(key))
            {
                CoopMod.Logger.LogWarning("[ServerBrowser] Favorite toggle ignored because the server has no stable ID");
                return;
            }

            if (isFavorite)
            {
                if (!favoriteServers.Contains(key))
                    favoriteServers.Add(key);

                string displayName = GetFavoriteDisplayName(server);
                if (!string.IsNullOrEmpty(displayName))
                    favoriteServerNames[key] = displayName;
            }
            else
            {
                favoriteServers.RemoveAll(id => id == key);
                favoriteServerNames.Remove(key);
            }

            SaveFavoriteServers();

            foreach (var entry in serverEntries)
            {
                if (GetFavoriteKey(entry) == key)
                    entry.IsFavorite = isFavorite;
            }

            CoopMod.Logger.LogInfo($"[ServerBrowser] {(isFavorite ? "Added" : "Removed")} favorite server {server.ServerName} ({key})");

            if (currentTab == BrowserTab.Favorites && !isFavorite)
                refreshFavoritesAfterFavoriteMenu = true;
        }

        private void ShowFavoriteChoiceMenu(ServerEntry server, GameObject rowObj)
        {
            string key = GetFavoriteKey(server);
            if (string.IsNullOrEmpty(key))
            {
                CoopMod.Logger.LogWarning("[ServerBrowser] Cannot show favorite menu for server without a stable ID");
                return;
            }

            HideFavoriteMenu();
            favoriteMenuServer = server;
            refreshFavoritesAfterFavoriteMenu = false;

            bool isFavorite = IsFavoriteServer(server);
            string actionText = isFavorite ? FAVORITE_ACTION_REMOVE : FAVORITE_ACTION_ADD;
            favoriteMenuClickLocalPosition = GetFavoriteMenuClickLocalPosition(rowObj);

            if (TryShowGameContextFavoriteMenu(actionText))
            {
                favoriteMenuOpen = true;
                CoopMod.Logger.LogInfo($"[ServerBrowser] Showing game context favorite menu: {actionText}");
                Sounds.OnGUIClick();
                return;
            }

            EnsureFavoriteMenuObject();
            favoriteMenuLabel.text = actionText;
            favoriteMenuObj.transform.localPosition = GetFavoriteMenuLocalPosition(rowObj, favoriteMenuClickLocalPosition);
            favoriteMenuObj.SetActive(true);

            favoriteMenuOpen = true;
            CoopMod.Logger.LogInfo($"[ServerBrowser] Showing favorite menu: {actionText}");
            Sounds.OnGUIClick();
        }

        private bool TryShowGameContextFavoriteMenu(string actionText)
        {
            var elements = GUIElements.me;
            if (elements == null || elements.context_menu_bubble == null)
                return false;

            try
            {
                favoriteMenuUsesGameContextBubble = true;
                Vector3 mouse = Input.mousePosition;
                ContextMenuBubbleGUI.Show(
                    new[] { actionText },
                    new Action[] { () => OnFavoriteMenuChosen(actionText) },
                    new Vector2(mouse.x, mouse.y),
                    OnGameContextFavoriteMenuHidden);
                return true;
            }
            catch (Exception ex)
            {
                favoriteMenuUsesGameContextBubble = false;
                CoopMod.Logger.LogWarning($"[ServerBrowser] Failed to show game context favorite menu, using fallback: {ex.Message}");
                return false;
            }
        }

        private void EnsureFavoriteMenuObject()
        {
            if (favoriteMenuObj != null)
                return;

            favoriteMenuObj = new GameObject("FavoriteChoiceMenu");
            favoriteMenuObj.layer = 13;
            favoriteMenuObj.transform.SetParent(transform, false);
            favoriteMenuObj.transform.localScale = Vector3.one;

            var menuPanel = favoriteMenuObj.AddComponent<UIPanel>();
            menuPanel.depth = 90;
            menuPanel.clipping = UIDrawCall.Clipping.None;

            CreateDialogueStyledFavoriteMenuOption();

            var collider2D = favoriteMenuObj.AddComponent<BoxCollider2D>();
            collider2D.size = new Vector2(FAVORITE_MENU_WIDTH, FAVORITE_MENU_HEIGHT);
            collider2D.isTrigger = true;

            var listener = UIEventListener.Get(favoriteMenuObj);
            listener.onPress = (go, isPressed) =>
            {
                if (isPressed)
                    suppressRowClickUntil = Time.unscaledTime + 0.25f;
            };
            listener.onHover = (go, isOver) =>
            {
                SetFavoriteMenuHover(isOver);
            };
            listener.onClick = (go) =>
            {
                suppressRowClickUntil = Time.unscaledTime + 0.25f;
                if (favoriteMenuServer == null)
                    return;

                string chosenAction = IsFavoriteServer(favoriteMenuServer)
                    ? FAVORITE_ACTION_REMOVE
                    : FAVORITE_ACTION_ADD;
                OnFavoriteMenuChosen(chosenAction);
            };

            favoriteMenuObj.SetActive(false);
        }

        private void CreateDialogueStyledFavoriteMenuOption()
        {
            if (!TryCreateFavoriteMenuFromDialogueAssets())
                CreateFavoriteMenuFromPanelAsset();

            GameObject labelObj = new GameObject("FavoriteChoiceLabel");
            labelObj.layer = 13;
            labelObj.transform.SetParent(favoriteMenuObj.transform, false);
            labelObj.transform.localPosition = new Vector3(0f, 1f, 0f);
            labelObj.transform.localScale = Vector3.one;

            favoriteMenuLabel = labelObj.AddComponent<UILabel>();
            favoriteMenuLabel.bitmapFont = gameFont;
            favoriteMenuLabel.fontSize = 15;
            favoriteMenuLabel.color = Color.black;
            favoriteMenuLabel.alignment = NGUIText.Alignment.Center;
            favoriteMenuLabel.pivot = UIWidget.Pivot.Center;
            favoriteMenuLabel.overflowMethod = UILabel.Overflow.ClampContent;
            favoriteMenuLabel.width = (int)FAVORITE_MENU_WIDTH - 22;
            favoriteMenuLabel.height = (int)FAVORITE_MENU_HEIGHT;
            favoriteMenuLabel.depth = 10;
            favoriteMenuLabelNormalColor = favoriteMenuLabel.color;
        }

        private bool TryCreateFavoriteMenuFromDialogueAssets()
        {
            var prefab = GUIElements.me?.multi_answer?.answer_prefab;
            if (prefab == null)
                return false;

            UI2DSprite back = prefab.back != null ? prefab.back : FindChildSprite(prefab.gameObject, "back");
            if (back == null || back.sprite2D == null)
                return false;

            favoriteMenuBackSprite = CreateMenuSprite("AnswerBack", back, Vector3.zero,
                FAVORITE_MENU_WIDTH, FAVORITE_MENU_HEIGHT, 1);
            favoriteMenuBackSprite.type = UIBasicSprite.Type.Sliced;
            favoriteMenuBackNormalColor = favoriteMenuBackSprite.color;

            CreateDialogueCornerSprite("CornerLD", prefab.ld ?? FindChildSprite(prefab.gameObject, "ld") ?? FindChildSprite(prefab.gameObject, "left down"), -1f, -1f, 2);
            CreateDialogueCornerSprite("CornerRD", prefab.rd ?? FindChildSprite(prefab.gameObject, "rd") ?? FindChildSprite(prefab.gameObject, "right down"), 1f, -1f, 2);
            CreateDialogueCornerSprite("CornerLU", prefab.lu ?? FindChildSprite(prefab.gameObject, "lu") ?? FindChildSprite(prefab.gameObject, "left up"), -1f, 1f, 2);
            CreateDialogueCornerSprite("CornerRU", prefab.ru ?? FindChildSprite(prefab.gameObject, "ru") ?? FindChildSprite(prefab.gameObject, "right up"), 1f, 1f, 2);
            return true;
        }

        private void CreateFavoriteMenuFromPanelAsset()
        {
            if (backgroundSprite != null)
            {
                GameObject bgObj = new GameObject("FavoriteChoiceBack");
                bgObj.layer = 13;
                bgObj.transform.SetParent(favoriteMenuObj.transform, false);
                bgObj.transform.localPosition = Vector3.zero;
                bgObj.transform.localScale = Vector3.one;

                var bg = bgObj.AddComponent<UI2DSprite>();
                bg.sprite2D = backgroundSprite;
                bg.type = UIBasicSprite.Type.Sliced;
                bg.border = new Vector4(16, 16, 16, 16);
                bg.color = new Color(0.58f, 0.38f, 0.16f, 1f);
                bg.width = (int)FAVORITE_MENU_WIDTH;
                bg.height = (int)FAVORITE_MENU_HEIGHT;
                bg.depth = 1;
                return;
            }

            CoopMod.Logger.LogWarning("[ServerBrowser] No dialogue or panel sprite found for favorite menu; using texture fallback");
            CreateMenuTexture("AnswerBack", Vector3.zero, FAVORITE_MENU_WIDTH, FAVORITE_MENU_HEIGHT,
                new Color(0.46f, 0.31f, 0.17f, 1f), 1);
        }

        private UI2DSprite CreateMenuSprite(string name, UI2DSprite source, Vector3 localPosition, float width, float height, int depth)
        {
            GameObject obj = new GameObject(name);
            obj.layer = 13;
            obj.transform.SetParent(favoriteMenuObj.transform, false);
            obj.transform.localPosition = localPosition;
            obj.transform.localScale = Vector3.one;

            var sprite = obj.AddComponent<UI2DSprite>();
            sprite.sprite2D = source.sprite2D;
            sprite.type = source.type;
            sprite.border = source.border;
            sprite.color = source.color;
            if (sprite.color.a < 0.5f)
                sprite.color = Color.white;
            sprite.width = Mathf.RoundToInt(width);
            sprite.height = Mathf.RoundToInt(height);
            sprite.depth = depth;
            return sprite;
        }

        private void CreateDialogueCornerSprite(string name, UI2DSprite source, float xSign, float ySign, int depth)
        {
            if (source == null || source.sprite2D == null)
                return;

            float width = Mathf.Clamp(source.width, 8f, 24f);
            float height = Mathf.Clamp(source.height, 8f, 24f);
            Vector3 localPosition = new Vector3(
                xSign * (FAVORITE_MENU_WIDTH * 0.5f - width * 0.5f),
                ySign * (FAVORITE_MENU_HEIGHT * 0.5f - height * 0.5f),
                0f);

            CreateMenuSprite(name, source, localPosition, width, height, depth);
        }

        private static UI2DSprite FindChildSprite(GameObject root, string spriteName)
        {
            if (root == null || string.IsNullOrEmpty(spriteName))
                return null;

            foreach (var sprite in root.GetComponentsInChildren<UI2DSprite>(true))
            {
                if (string.Equals(sprite.name, spriteName, StringComparison.OrdinalIgnoreCase))
                    return sprite;
            }

            return null;
        }

        private UITexture CreateMenuTexture(string name, Vector3 localPosition, float width, float height, Color color, int depth)
        {
            GameObject obj = new GameObject(name);
            obj.layer = 13;
            obj.transform.SetParent(favoriteMenuObj.transform, false);
            obj.transform.localPosition = localPosition;
            obj.transform.localScale = Vector3.one;

            var texture = obj.AddComponent<UITexture>();
            texture.mainTexture = Texture2D.whiteTexture;
            texture.color = color;
            texture.width = Mathf.RoundToInt(width);
            texture.height = Mathf.RoundToInt(height);
            texture.depth = depth;
            return texture;
        }

        private Vector3 GetFavoriteMenuClickLocalPosition(GameObject rowObj)
        {
            var mainGame = MainGame.me;
            if (mainGame != null && mainGame.gui_cam != null)
            {
                Ray ray = mainGame.gui_cam.ScreenPointToRay(Input.mousePosition);
                Plane browserPlane = new Plane(transform.forward, transform.position);
                float enter;
                if (browserPlane.Raycast(ray, out enter))
                    return transform.InverseTransformPoint(ray.GetPoint(enter));
            }

            if (rowObj != null)
            {
                Vector3 rowWorldPos = rowObj.transform.TransformPoint(Vector3.zero);
                return transform.InverseTransformPoint(rowWorldPos);
            }

            return Vector3.zero;
        }

        private Vector3 GetFavoriteMenuLocalPosition(GameObject rowObj, Vector3 clickLocalPosition)
        {
            Vector3 anchorLocalPosition = clickLocalPosition;

            if (anchorLocalPosition == Vector3.zero && rowObj != null)
            {
                Vector3 rowWorldPos = rowObj.transform.TransformPoint(Vector3.zero);
                anchorLocalPosition = transform.InverseTransformPoint(rowWorldPos);
            }

            Vector3 localPos = anchorLocalPosition;
            float minX = -PanelWidth * 0.5f + FAVORITE_MENU_WIDTH * 0.5f + 8f;
            float maxX = PanelWidth * 0.5f - FAVORITE_MENU_WIDTH * 0.5f - 8f;
            float minY = -PanelHeight * 0.5f + FAVORITE_MENU_HEIGHT * 0.5f + 8f;
            float maxY = PanelHeight * 0.5f + 28f - FAVORITE_MENU_HEIGHT * 0.5f;
            float gap = 10f;

            float belowClickY = anchorLocalPosition.y - FAVORITE_MENU_HEIGHT * 0.5f - gap;
            float aboveClickY = anchorLocalPosition.y + FAVORITE_MENU_HEIGHT * 0.5f + gap;
            localPos.x = Mathf.Clamp(anchorLocalPosition.x, minX, maxX);
            localPos.y = belowClickY >= minY || aboveClickY > maxY
                ? belowClickY
                : aboveClickY;
            localPos.y = Mathf.Clamp(localPos.y, minY, maxY);
            localPos.z = -5f;
            return localPos;
        }

        private void SetFavoriteMenuHover(bool isHovering)
        {
            if (favoriteMenuBackSprite != null)
            {
                favoriteMenuBackSprite.color = isHovering ? favoriteMenuBackHoverColor : favoriteMenuBackNormalColor;
                favoriteMenuBackSprite.Update();
            }

            if (favoriteMenuLabel != null)
            {
                favoriteMenuLabel.color = isHovering ? favoriteMenuLabelHoverColor : favoriteMenuLabelNormalColor;
                favoriteMenuLabel.MarkAsChanged();
            }
        }

        private void OnFavoriteMenuChosen(string chosenId)
        {
            var server = favoriteMenuServer;
            if (server == null)
                return;

            if (chosenId == FAVORITE_ACTION_ADD)
            {
                SetFavoriteServer(server, true);
            }
            else if (chosenId == FAVORITE_ACTION_REMOVE)
            {
                SetFavoriteServer(server, false);
            }

            bool shouldRefreshFavorites = refreshFavoritesAfterFavoriteMenu;
            HideFavoriteMenu();

            if (shouldRefreshFavorites)
            {
                refreshFavoritesAfterFavoriteMenu = false;
                RefreshServerList();
            }
        }

        private void OnGameContextFavoriteMenuHidden()
        {
            if (favoriteMenuHideInProgress)
                return;

            favoriteMenuOpen = false;
            favoriteMenuServer = null;
            rightClickPressedRow = null;
            refreshFavoritesAfterFavoriteMenu = false;
            favoriteMenuUsesGameContextBubble = false;
        }

        private void HideFavoriteMenu()
        {
            var contextMenuBubble = GUIElements.me?.context_menu_bubble;
            bool contextMenuShown = favoriteMenuUsesGameContextBubble &&
                contextMenuBubble != null &&
                contextMenuBubble.is_shown;

            if (!favoriteMenuOpen && (favoriteMenuObj == null || !favoriteMenuObj.activeSelf) && !contextMenuShown)
                return;

            bool hideGameContextBubble = favoriteMenuUsesGameContextBubble && contextMenuBubble != null;
            favoriteMenuOpen = false;
            favoriteMenuServer = null;
            rightClickPressedRow = null;
            refreshFavoritesAfterFavoriteMenu = false;
            favoriteMenuUsesGameContextBubble = false;
            SetFavoriteMenuHover(false);

            if (hideGameContextBubble)
            {
                favoriteMenuHideInProgress = true;
                try
                {
                    contextMenuBubble.OnHide();
                }
                finally
                {
                    favoriteMenuHideInProgress = false;
                }
            }

            if (favoriteMenuObj != null)
                favoriteMenuObj.SetActive(false);
        }

        /// <summary>
        /// Called when Steam lobby search returns results.
        /// Merges with any existing Rich Presence entries (deduplicates by host Steam ID).
        /// </summary>
        private void OnFriendLobbySearchResults(LobbySearchMode searchMode, List<LobbySearchResult> results)
        {
            if (searchMode != LobbySearchMode.Friends) return;

            CoopMod.Logger.LogInfo($"[ServerBrowser] Lobby search returned {results.Count} friend lobbies");

            // Only process if we're still on the Friends tab
            if (currentTab != BrowserTab.Friends) return;

            // Build set of hosts already found via Rich Presence
            var existingHosts = new HashSet<ulong>();
            foreach (var entry in serverEntries)
            {
                if (entry.HostSteamID != CSteamID.Nil)
                    existingHosts.Add(entry.HostSteamID.m_SteamID);
            }

            // Add new entries from lobby search that weren't found via Rich Presence
            foreach (var lobby in results)
            {
                if (existingHosts.Contains(lobby.HostSteamID.m_SteamID))
                {
                    // Update existing entry with richer data from lobby
                    foreach (var entry in serverEntries)
                    {
                        if (entry.HostSteamID == lobby.HostSteamID)
                        {
                            bool isRunningGame = SteamLobbyManager.IsRunningGameStatus(lobby.Status);
                            entry.CurrentPlayers = lobby.CurrentPlayers;
                            entry.MaxPlayers = lobby.MaxPlayers;
                            entry.Status = GetServerStatusText(lobby.Status, isRunningGame);
                            entry.IsRunningGame = isRunningGame;
                            // Store lobby ID for direct join
                            entry.ServerID = lobby.LobbyID.ToString();
                            entry.DLCRequirements = !string.IsNullOrEmpty(lobby.DLCRequirements)
                                ? lobby.DLCRequirements
                                : entry.DLCRequirements ?? "";
                            entry.ModVersion = lobby.ModVersion ?? "";
                            entry.ProtocolRevision = lobby.ProtocolRevision ?? "";
                            break;
                        }
                    }
                }
                else
                {
                    // New entry not seen via Rich Presence
                    bool isRunningGame = SteamLobbyManager.IsRunningGameStatus(lobby.Status);
                    serverEntries.Add(new ServerEntry
                    {
                        ServerID = lobby.LobbyID.ToString(),
                        HostSteamID = lobby.HostSteamID,
                        ServerName = $"{lobby.HostName}'s Game",
                        ConnectToken = "", // Will join via lobby ID
                        CurrentPlayers = lobby.CurrentPlayers,
                        MaxPlayers = lobby.MaxPlayers,
                        Ping = -1,
                        Status = GetServerStatusText(lobby.Status, isRunningGame),
                        IsFavorite = IsFavoriteServer(lobby.HostSteamID, lobby.LobbyID.ToString()),
                        IsRunningGame = isRunningGame,
                        SourceTab = BrowserTab.Friends,
                        DLCRequirements = lobby.DLCRequirements ?? "",
                        ModVersion = lobby.ModVersion ?? "",
                        ProtocolRevision = lobby.ProtocolRevision ?? ""
                    });
                }
            }

            // Refresh the display
            ClearServerRows();
            if (serverEntries.Count > 0)
            {
                noServersLabel.gameObject.SetActive(false);
                PopulateServerRows();
                SendPingsToVisibleHosts();
            }
            else
            {
                noServersLabel.text = "No friends hosting";
                noServersLabel.gameObject.SetActive(true);
            }

            CoopMod.Logger.LogInfo($"[ServerBrowser] Friends tab now showing {serverEntries.Count} entries");
        }

        private void OnPublicLobbySearchResults(LobbySearchMode searchMode, List<LobbySearchResult> results)
        {
            if (searchMode != LobbySearchMode.Public) return;

            CoopMod.Logger.LogInfo($"[ServerBrowser] Lobby search returned {results.Count} public lobbies");

            if (currentTab != BrowserTab.Internet) return;

            serverEntries.Clear();
            ClearServerRows();
            selectedServer = null;
            selectedRowObj = null;

            foreach (var lobby in results)
            {
                bool isRunningGame = SteamLobbyManager.IsRunningGameStatus(lobby.Status);
                serverEntries.Add(new ServerEntry
                {
                    ServerID = lobby.LobbyID.ToString(),
                    HostSteamID = lobby.HostSteamID,
                    ServerName = $"{lobby.HostName}'s Game",
                    ConnectToken = "",
                    CurrentPlayers = lobby.CurrentPlayers,
                    MaxPlayers = lobby.MaxPlayers,
                    Ping = -1,
                    Status = GetServerStatusText(lobby.Status, isRunningGame),
                    IsFavorite = IsFavoriteServer(lobby.HostSteamID, lobby.LobbyID.ToString()),
                    IsRunningGame = isRunningGame,
                    SourceTab = BrowserTab.Internet,
                    DLCRequirements = lobby.DLCRequirements ?? "",
                    ModVersion = lobby.ModVersion ?? "",
                    ProtocolRevision = lobby.ProtocolRevision ?? ""
                });
            }

            if (serverEntries.Count > 0)
            {
                noServersLabel.gameObject.SetActive(false);
                PopulateServerRows();
                SendPingsToVisibleHosts();
            }
            else
            {
                noServersLabel.text = "No public sessions";
                noServersLabel.gameObject.SetActive(true);
            }

            CoopMod.Logger.LogInfo($"[ServerBrowser] Internet tab now showing {serverEntries.Count} public entries");
        }

        private void OnFavoriteLobbySearchResults(LobbySearchMode searchMode, List<LobbySearchResult> results)
        {
            if (searchMode != LobbySearchMode.Favorites) return;

            CoopMod.Logger.LogInfo($"[ServerBrowser] Lobby search returned {results.Count} visible lobbies for Favorites filtering");

            if (currentTab != BrowserTab.Favorites) return;

            BuildFavoriteServerEntries(results);

            if (serverEntries.Count > 0)
            {
                noServersLabel.gameObject.SetActive(false);
                PopulateServerRows();
                SendPingsToVisibleHosts();
            }
            else
            {
                noServersLabel.text = "No favorite servers";
                noServersLabel.gameObject.SetActive(true);
            }

            CoopMod.Logger.LogInfo($"[ServerBrowser] Favorites tab now showing {serverEntries.Count} favorite entries");
            TryCompletePendingPresenceJoin();
        }

        private void ClearServerRows()
        {
            foreach (var row in serverRows)
            {
                if (row != null)
                {
                    row.SetActive(false);
                    Destroy(row);
                }
            }
            serverRows.Clear();
            pingLabels.Clear();
            activePingProbes.Clear();
            ControllerItemsChanged?.Invoke();
        }

        private void PopulateServerRows()
        {
            float yPos = 0;

            foreach (var server in serverEntries)
            {
                GameObject row = CreateServerRow(server, yPos);
                serverRows.Add(row);
                yPos -= RowHeight;
            }

            scrollView?.ResetPosition();
            ControllerItemsChanged?.Invoke();
        }

        private GameObject CreateServerRow(ServerEntry server, float yPos)
        {
            GameObject rowObj;

            if (saveSlotTemplate != null)
            {
                // Clone save slot for consistent styling
                rowObj = UnityEngine.Object.Instantiate(saveSlotTemplate.gameObject, serverListContainer.transform);
                rowObj.name = $"Server_{server.ServerID}";
                rowObj.transform.localPosition = new Vector3(0, yPos, 0);
                rowObj.transform.localScale = Vector3.one * 0.85f;
                rowObj.SetActive(true);

                // Remove the SaveSlotGUI component
                var slotGUI = rowObj.GetComponent<SaveSlotGUI>();
                if (slotGUI != null)
                    UnityEngine.Object.DestroyImmediate(slotGUI);

                // Widen background sprites to fill the panel
                // Must clear NGUI anchors first — they override width/height settings
                foreach (var widget in rowObj.GetComponentsInChildren<UIWidget>(true))
                {
                    if (widget.leftAnchor != null) widget.leftAnchor.target = null;
                    if (widget.rightAnchor != null) widget.rightAnchor.target = null;
                    if (widget.topAnchor != null) widget.topAnchor.target = null;
                    if (widget.bottomAnchor != null) widget.bottomAnchor.target = null;
                }

                var rootWidget = rowObj.GetComponent<UIWidget>();
                if (rootWidget != null)
                {
                    rootWidget.width = Mathf.RoundToInt(RowWidth);
                    rootWidget.height = 36;
                }
                foreach (Transform child in rowObj.GetComponentsInChildren<Transform>(true))
                {
                    string n = child.name.ToLower();
                    if (n == "back")
                    {
                        var sprite = child.GetComponent<UI2DSprite>();
                        if (sprite != null)
                        {
                            sprite.width = Mathf.RoundToInt(RowWidth);
                            sprite.height = 34;
                        }
                    }
                    else if (n == "gamepad frame")
                    {
                        var sprite = child.GetComponent<UI2DSprite>();
                        if (sprite != null)
                        {
                            sprite.width = Mathf.RoundToInt(RowWidth + 13f);
                            sprite.height = 40;
                        }
                        // Hide by default -- only shown when the row is selected
                        child.gameObject.SetActive(false);
                    }
                }

                // Hide ALL existing labels and controls — we'll create our own
                foreach (var label in rowObj.GetComponentsInChildren<UILabel>(true))
                {
                    label.gameObject.SetActive(false);
                }
                foreach (Transform child in rowObj.GetComponentsInChildren<Transform>(true))
                {
                    string n = child.name.ToLower();
                    if (n.Contains("delete") || n.Contains("cross") || n.Contains("btn"))
                    {
                        child.gameObject.SetActive(false);
                    }
                }

                float halfW = RowWidth * 0.5f - 12f;

                // Server name — far left
                GameObject nameObj = new GameObject("ServerName");
                nameObj.layer = 13;
                nameObj.transform.SetParent(rowObj.transform, false);
                nameObj.transform.localPosition = new Vector3(-halfW + 60, 5, 0);
                nameObj.transform.localScale = Vector3.one;
                var nameLabel = nameObj.AddComponent<UILabel>();
                nameLabel.bitmapFont = gameFont;
                nameLabel.text = server.ServerName;
                nameLabel.color = TEXT_COLOR;
                nameLabel.fontSize = 16;
                nameLabel.pivot = UIWidget.Pivot.Left;
                nameLabel.alignment = NGUIText.Alignment.Left;
                nameLabel.overflowMethod = UILabel.Overflow.ResizeFreely;
                nameLabel.depth = 35;

                // Status — left, below name
                GameObject statusObj = new GameObject("ServerStatus");
                statusObj.layer = 13;
                statusObj.transform.SetParent(rowObj.transform, false);
                statusObj.transform.localPosition = new Vector3(-halfW + 60, -8, 0);
                statusObj.transform.localScale = Vector3.one;
                var statusLabel = statusObj.AddComponent<UILabel>();
                statusLabel.bitmapFont = gameFont;
                statusLabel.text = server.Status;
                statusLabel.color = GetStatusColor(server);
                statusLabel.fontSize = 11;
                statusLabel.pivot = UIWidget.Pivot.Left;
                statusLabel.alignment = NGUIText.Alignment.Left;
                statusLabel.overflowMethod = UILabel.Overflow.ResizeFreely;
                statusLabel.depth = 35;

                // Player count — middle-right
                GameObject playersObj = new GameObject("ServerPlayers");
                playersObj.layer = 13;
                playersObj.transform.SetParent(rowObj.transform, false);
                playersObj.transform.localPosition = new Vector3(halfW - 120, 0, 0);
                playersObj.transform.localScale = Vector3.one;
                var playersLabel = playersObj.AddComponent<UILabel>();
                playersLabel.bitmapFont = gameFont;
                playersLabel.text = server.PlayersString;
                playersLabel.color = TEXT_COLOR;
                playersLabel.fontSize = 14;
                playersLabel.alignment = NGUIText.Alignment.Center;
                playersLabel.overflowMethod = UILabel.Overflow.ResizeFreely;
                playersLabel.depth = 35;

                // Ping — far right
                GameObject pingObj = new GameObject("ServerPing");
                pingObj.layer = 13;
                pingObj.transform.SetParent(rowObj.transform, false);
                pingObj.transform.localPosition = new Vector3(halfW - 60, 0, 0);
                pingObj.transform.localScale = Vector3.one;
                var pingLabel = pingObj.AddComponent<UILabel>();
                pingLabel.bitmapFont = gameFont;
                string pingText = server.IsOffline
                    ? "---"
                    : server.SourceTab == BrowserTab.LAN && string.IsNullOrEmpty(server.ConnectToken)
                    ? "LAN"
                    : server.Ping >= 0
                        ? $"{server.Ping}ms"
                        : (server.HostSteamID != CSteamID.Nil ? "..." : "---");
                pingLabel.text = pingText;
                pingLabel.color = server.IsOffline
                    ? DIM_TEXT_COLOR
                    : server.SourceTab == BrowserTab.LAN && string.IsNullOrEmpty(server.ConnectToken) ? ONLINE_COLOR : GetPingColor(server.Ping);
                pingLabel.fontSize = 12;
                pingLabel.pivot = UIWidget.Pivot.Right;
                pingLabel.alignment = NGUIText.Alignment.Right;
                pingLabel.overflowMethod = UILabel.Overflow.ResizeFreely;
                pingLabel.depth = 35;
                
                // Store reference for async ping updates
                if (server.HostSteamID != CSteamID.Nil)
                {
                    pingLabels[server.HostSteamID.m_SteamID] = pingLabel;
                }
            }
            else
            {
                // Fallback: simple label row
                rowObj = new GameObject($"Server_{server.ServerID}");
                rowObj.layer = 13;
                rowObj.transform.SetParent(serverListContainer.transform, false);
                rowObj.transform.localPosition = new Vector3(0, yPos, 0);

                var nameLabel = rowObj.AddComponent<UILabel>();
                nameLabel.bitmapFont = gameFont;
                nameLabel.text = $"{server.ServerName}  [{server.PlayersString}]  {server.Status}";
                nameLabel.fontSize = 14;
                nameLabel.color = TEXT_COLOR;
                nameLabel.alignment = NGUIText.Alignment.Center;
                nameLabel.overflowMethod = UILabel.Overflow.ClampContent;
                nameLabel.width = Mathf.RoundToInt(PanelWidth) - 20;
                nameLabel.depth = 30;
            }

            // Add click detection
            // NGUI uses Physics2D raycasting — must use BoxCollider2D, NOT BoxCollider (3D).
            // Remove ALL existing colliders from the cloned template hierarchy first.
            // Destroy scripts that require colliders before destroying the colliders themselves.
            foreach (var autoSize in rowObj.GetComponentsInChildren<PolygonColliderAutoSize>(true))
                UnityEngine.Object.DestroyImmediate(autoSize);

            // Remove old NGUI button components from the cloned template
            foreach (var btn in rowObj.GetComponentsInChildren<UIButton>(true))
                UnityEngine.Object.DestroyImmediate(btn);
            foreach (var btnColor in rowObj.GetComponentsInChildren<UIButtonColor>(true))
                UnityEngine.Object.DestroyImmediate(btnColor);
            foreach (var btnScale in rowObj.GetComponentsInChildren<UIButtonScale>(true))
                UnityEngine.Object.DestroyImmediate(btnScale);
            foreach (var existingCollider in rowObj.GetComponentsInChildren<Collider>(true))
                UnityEngine.Object.DestroyImmediate(existingCollider);
            foreach (var existingCollider2D in rowObj.GetComponentsInChildren<Collider2D>(true))
                UnityEngine.Object.DestroyImmediate(existingCollider2D);

            // Add 2D collider on root — NGUI will raycast this
            var collider2D = rowObj.AddComponent<BoxCollider2D>();
            collider2D.size = new Vector2(RowWidth, RowHeight - 4f);
            collider2D.isTrigger = true;

            // Wire up click via UIEventListener (same pattern as working tab clicks)
            var serverForClosure = server;
            var rowForClosure = rowObj;
            var listener = UIEventListener.Get(rowObj);
            listener.onPress = (go, isPressed) =>
            {
                if (isPressed && IsRightClickEvent())
                {
                    rightClickPressedRow = rowForClosure;
                    rightClickPressedAt = Time.unscaledTime;
                    suppressRowClickUntil = Time.unscaledTime + 0.25f;
                    ShowFavoriteChoiceMenu(serverForClosure, rowForClosure);
                }
            };
            listener.onClick = (go) =>
            {
                if (rightClickPressedRow == rowForClosure)
                {
                    rightClickPressedRow = null;
                    return;
                }

                if (rightClickPressedRow != null &&
                    Time.unscaledTime - rightClickPressedAt > RIGHT_CLICK_CONSUME_SECONDS)
                {
                    rightClickPressedRow = null;
                }

                if (IsRightClickEvent())
                {
                    suppressRowClickUntil = Time.unscaledTime + 0.25f;
                    return;
                }

                if (Time.unscaledTime <= suppressRowClickUntil)
                    return;

                HideFavoriteMenu();

                float currentTime = Time.unscaledTime;
                float timeSinceLast = currentTime - (listener.parameter as float? ?? 0f);
                CoopMod.Logger.LogInfo($"[ServerBrowser] Row clicked: {serverForClosure.ServerName}, timeSinceLast={timeSinceLast:F3}s");

                if (timeSinceLast < 0.5f)
                {
                    CoopMod.Logger.LogInfo($"[ServerBrowser] DOUBLE-CLICK detected on {serverForClosure.ServerName}");
                    OnServerRowDoubleClicked(serverForClosure);
                }
                else
                {
                    OnServerRowClicked(serverForClosure, rowForClosure);
                }
                listener.parameter = currentTime;
            };

            ConfigureServerRowNavigation(rowObj, server);

            return rowObj;
        }

        private void ConfigureServerRowNavigation(GameObject rowObj, ServerEntry server)
        {
            foreach (var childItem in rowObj.GetComponentsInChildren<GamepadNavigationItem>(true))
            {
                if (childItem.gameObject != rowObj)
                    UnityEngine.Object.DestroyImmediate(childItem);
            }

            var navigationItem = rowObj.GetComponent<GamepadNavigationItem>() ??
                                 rowObj.AddComponent<GamepadNavigationItem>();
            navigationItem.active = true;

            foreach (Transform child in rowObj.GetComponentsInChildren<Transform>(true))
            {
                if (child.name.ToLower().Contains("gamepad") &&
                    child.name.ToLower().Contains("frame"))
                {
                    navigationItem.focus_frame = child.gameObject;
                    break;
                }
            }

            navigationItem.SetCallbacks(
                () =>
                {
                    selectedServer = server;
                    selectedRowObj = rowObj;
                    UpdateRowHighlights();
                },
                UpdateRowHighlights,
                () => OnServerRowDoubleClicked(server));
        }

        private static bool IsRightClickEvent()
        {
            return UICamera.currentTouchID == -2 ||
                   Input.GetMouseButtonDown(1) ||
                   Input.GetMouseButtonUp(1);
        }

        public void OnServerRowClicked(ServerEntry server, GameObject rowObj)
        {
            selectedServer = server;
            selectedRowObj = rowObj;
            UpdateRowHighlights();
            Sounds.OnGUIClick();
        }

        /// <summary>
        /// Toggle the gamepad_frame highlight sprite on every row to match the current selection.
        /// Same pattern used by FriendInviteGUI / SaveSelectorPanel.
        /// </summary>
        private void UpdateRowHighlights()
        {
            foreach (var row in serverRows)
            {
                if (row == null) continue;
                bool isSelected = (row == selectedRowObj);
                foreach (Transform child in row.GetComponentsInChildren<Transform>(true))
                {
                    if (child.name.ToLower() == "gamepad frame")
                    {
                        child.gameObject.SetActive(isSelected);
                    }
                }
            }
        }

        /// <summary>
        /// Public entry point used by external Join buttons (e.g. JoinGameGUI's Connect button).
        /// Returns true if a join attempt was started, false if no server was highlighted
        /// or a join was already in progress.
        /// </summary>
        public bool TryJoinSelected()
        {
            if (selectedServer == null)
            {
                CoopMod.Logger.LogInfo("[ServerBrowser] Connect pressed but no server selected");
                return false;
            }
            if (selectedServer.IsOffline)
            {
                return BeginOfflineServerAvailabilityCheck(selectedServer);
            }
            if (isJoinInProgress)
            {
                CoopMod.Logger.LogInfo("[ServerBrowser] Connect pressed but a join is already in progress");
                return false;
            }
            OnServerRowDoubleClicked(selectedServer);
            return true;
        }

        public GamepadNavigationItem[] GetTabNavigationItems()
        {
            var result = new List<GamepadNavigationItem>();
            foreach (BrowserTab tab in Enum.GetValues(typeof(BrowserTab)))
            {
                GamepadNavigationItem item;
                if (tabNavigationItems.TryGetValue(tab, out item) && item != null)
                    result.Add(item);
            }
            return result.ToArray();
        }

        public GamepadNavigationItem GetCurrentTabNavigationItem()
        {
            GamepadNavigationItem item;
            return tabNavigationItems.TryGetValue(currentTab, out item) ? item : null;
        }

        public GamepadNavigationItem[] GetServerRowNavigationItems()
        {
            var result = new List<GamepadNavigationItem>();
            foreach (var row in serverRows)
            {
                if (row == null)
                    continue;
                var item = row.GetComponent<GamepadNavigationItem>();
                if (item != null)
                    result.Add(item);
            }
            return result.ToArray();
        }

        public void SwitchRelativeTab(int delta)
        {
            int count = Enum.GetValues(typeof(BrowserTab)).Length;
            int index = ((int)currentTab + delta) % count;
            if (index < 0)
                index += count;
            SwitchSubTab((BrowserTab)index);
        }

        public void OnServerRowDoubleClicked(ServerEntry server)
        {
            if (server.IsOffline)
            {
                if (!TryResolveServerFromPresence(server, out ServerEntry onlineServer))
                {
                    BeginOfflineServerAvailabilityCheck(server);
                    return;
                }
                server = onlineServer;
            }

            // Guard against duplicate join attempts
            if (isJoinInProgress)
            {
                CoopMod.Logger.LogWarning($"[ServerBrowser] Join already in progress, ignoring double-click on {server.ServerName}");
                return;
            }

            isJoinInProgress = true;
            joinStartTime = Time.unscaledTime;

            CoopMod.Logger.LogInfo($"[ServerBrowser] ========== DOUBLE-CLICK JOIN ==========");
            CoopMod.Logger.LogInfo($"[ServerBrowser] ServerName: {server.ServerName}");
            CoopMod.Logger.LogInfo($"[ServerBrowser] ServerID: {server.ServerID}");
            CoopMod.Logger.LogInfo($"[ServerBrowser] HostSteamID: {server.HostSteamID}");
            CoopMod.Logger.LogInfo($"[ServerBrowser] ConnectToken: '{server.ConnectToken}'");
            CoopMod.Logger.LogInfo($"[ServerBrowser] SourceTab: {server.SourceTab}");
            CoopMod.Logger.LogInfo($"[ServerBrowser] DLCRequirements: '{server.DLCRequirements}'");
            CoopMod.Logger.LogInfo($"[ServerBrowser] ModVersion: '{server.ModVersion}'");
            CoopMod.Logger.LogInfo($"[ServerBrowser] ProtocolRevision: '{server.ProtocolRevision}'");

            if (!SteamLobbyManager.TryValidateHostCompatibility(
                    server.ModVersion,
                    server.ProtocolRevision,
                    out string compatibilityRejectMessage))
            {
                CoopMod.Logger.LogWarning(
                    $"[ServerBrowser] Join blocked by compatibility check: " +
                    compatibilityRejectMessage.Replace('\n', ' '));
                isJoinInProgress = false;
                var dialog = GUIElements.me?.dialog;
                if (dialog != null)
                {
                    dialog.OpenOK(compatibilityRejectMessage);
                }
                else if (noServersLabel != null)
                {
                    noServersLabel.text = compatibilityRejectMessage;
                    noServersLabel.color = DIM_TEXT_COLOR;
                    noServersLabel.gameObject.SetActive(true);
                }
                return;
            }

            if (!SteamLobbyManager.TryValidateDLCRequirements(server.DLCRequirements, out string dlcRejectMessage))
            {
                CoopMod.Logger.LogWarning($"[ServerBrowser] Join blocked by DLC requirements: {dlcRejectMessage}");
                isJoinInProgress = false;
                if (GUIElements.me != null && GUIElements.me.dialog != null)
                {
                    GUIElements.me.dialog.OpenOK(dlcRejectMessage);
                }
                else if (noServersLabel != null)
                {
                    noServersLabel.text = dlcRejectMessage;
                    noServersLabel.color = DIM_TEXT_COLOR;
                    noServersLabel.gameObject.SetActive(true);
                }
                return;
            }

            // Show the standard engine dialog window (same one used for host-disconnect
            // popups) but with a Cancel button instead of OK. Falls back to the inline label
            // if the dialog GUI isn't available for some reason.
            if (!joinDialogOpen)
                ShowJoinDialog(server.ServerName);

            // Try connect via Rich Presence token first
            if (!string.IsNullOrEmpty(server.ConnectToken) && server.HostSteamID != CSteamID.Nil)
            {
                CoopMod.Logger.LogInfo($"[ServerBrowser] Attempting Rich Presence join...");
                CSteamID hostID = SteamJoinFlow.ParseConnectToken(server.ConnectToken);
                CoopMod.Logger.LogInfo($"[ServerBrowser] ParseConnectToken result: {hostID}");
                if (hostID != CSteamID.Nil)
                {
                    CoopMod.Logger.LogInfo($"[ServerBrowser] ✓ Joining via Rich Presence connect token...");
                    SteamJoinFlow.TriggerJoin(server.ConnectToken, hostID);
                    return;
                }
                CoopMod.Logger.LogWarning($"[ServerBrowser] ParseConnectToken returned Nil!");
            }
            else
            {
                CoopMod.Logger.LogInfo($"[ServerBrowser] No Rich Presence token (ConnectToken empty={string.IsNullOrEmpty(server.ConnectToken)}, HostSteamID={server.HostSteamID})");
            }

            // Fallback: join via Steam lobby ID (from lobby search results)
            CoopMod.Logger.LogInfo($"[ServerBrowser] Attempting lobby ID join with ServerID='{server.ServerID}'...");
            ulong lobbyIDValue;
            if (ulong.TryParse(server.ServerID, out lobbyIDValue))
            {
                CSteamID lobbyID = new CSteamID(lobbyIDValue);
                CoopMod.Logger.LogInfo($"[ServerBrowser] Parsed lobby ID: {lobbyID} (isNil={lobbyID == CSteamID.Nil})");
                if (lobbyID != CSteamID.Nil)
                {
                    CoopMod.Logger.LogInfo($"[ServerBrowser] ✓ Joining via lobby ID {lobbyID}...");
                    SteamLobbyManager.Instance.JoinLobby(lobbyID);
                    return;
                }
            }
            else
            {
                CoopMod.Logger.LogWarning($"[ServerBrowser] Failed to parse ServerID '{server.ServerID}' as ulong!");
            }

            CoopMod.Logger.LogWarning($"[ServerBrowser] ✗ No valid connect method for {server.ServerName}");
            ResetJoinState("Failed to connect");
        }

        private bool BeginOfflineServerAvailabilityCheck(ServerEntry server)
        {
            if (server == null || server.HostSteamID == CSteamID.Nil || !SteamManager.Initialized)
            {
                ShowServerUnavailable(server?.ServerName);
                return false;
            }

            if (TryResolveServerFromPresence(server, out ServerEntry onlineServer))
            {
                OnServerRowDoubleClicked(onlineServer);
                return true;
            }

            pendingPresenceJoinServer = server;
            pendingPresenceJoinStartedAt = Time.unscaledTime;
            if (!joinDialogOpen)
                ShowJoinDialog(server.ServerName);
            SteamFriends.RequestFriendRichPresence(server.HostSteamID);

            var lobbyMgr = SteamLobbyManager.Instance;
            if (server.SourceTab == BrowserTab.Favorites && lobbyMgr != null)
                lobbyMgr.RequestFavoriteLobbies();

            if (noServersLabel != null)
            {
                noServersLabel.text = $"Checking {server.ServerName}...";
                noServersLabel.color = DIM_TEXT_COLOR;
                noServersLabel.depth = 100;
                noServersLabel.gameObject.SetActive(true);
            }

            CoopMod.Logger.LogInfo(
                $"[ServerBrowser] Requested fresh presence/lobby data before joining {server.ServerName}");
            return true;
        }

        private bool TryResolveServerFromPresence(ServerEntry server, out ServerEntry onlineServer)
        {
            onlineServer = null;
            if (server == null || server.HostSteamID == CSteamID.Nil)
                return false;

            return TryCreateRichPresenceServerEntry(
                server.HostSteamID,
                server.SourceTab,
                "",
                out onlineServer);
        }

        private void OnFriendRichPresenceUpdated(FriendRichPresenceUpdate_t update)
        {
            if (update.m_steamIDFriend == CSteamID.Nil)
                return;
            bool isPendingHost = pendingPresenceJoinServer != null &&
                                 pendingPresenceJoinServer.HostSteamID == update.m_steamIDFriend;
            if (!isPendingHost && currentTab != BrowserTab.Favorites)
                return;

            ServerEntry refreshedServer;
            if (!TryCreateRichPresenceServerEntry(
                    update.m_steamIDFriend,
                    BrowserTab.Favorites,
                    "",
                    out refreshedServer))
            {
                return;
            }

            for (int i = 0; i < serverEntries.Count; i++)
            {
                if (serverEntries[i].HostSteamID == update.m_steamIDFriend)
                {
                    serverEntries[i] = refreshedServer;
                    break;
                }
            }

            if (isPendingHost)
            {
                pendingPresenceJoinServer = null;
                OnServerRowDoubleClicked(refreshedServer);
                return;
            }

            if (currentTab == BrowserTab.Favorites)
            {
                ClearServerRows();
                selectedServer = null;
                selectedRowObj = null;
                PopulateServerRows();
            }
        }

        private void TryCompletePendingPresenceJoin()
        {
            if (pendingPresenceJoinServer == null)
                return;

            foreach (var server in serverEntries)
            {
                if (server.HostSteamID == pendingPresenceJoinServer.HostSteamID && !server.IsOffline)
                {
                    pendingPresenceJoinServer = null;
                    OnServerRowDoubleClicked(server);
                    return;
                }
            }
        }

        private void ShowServerUnavailable(string serverName)
        {
            string message = string.IsNullOrEmpty(serverName)
                ? "That server is no longer available."
                : $"{serverName} is offline or no longer hosting.";
            HideJoinDialog();
            isJoinInProgress = false;
            CoopMod.Logger.LogInfo($"[ServerBrowser] {message}");
            var dialog = GUIElements.me?.dialog;
            if (dialog != null)
            {
                dialog.OpenOK(message);
            }
            else if (noServersLabel != null)
            {
                noServersLabel.text = message;
                noServersLabel.color = DIM_TEXT_COLOR;
                noServersLabel.depth = 100;
                noServersLabel.gameObject.SetActive(true);
            }
        }

        /// <summary>
        /// Reset the join-in-progress state and restore UI
        /// </summary>
        private void ResetJoinState(string statusText = null)
        {
            isJoinInProgress = false;
            HideJoinDialog();
            if (noServersLabel != null && statusText != null)
            {
                noServersLabel.text = statusText;
                noServersLabel.color = DIM_TEXT_COLOR;
            }
        }

        /// <summary>
        /// Called when a join attempt is rejected (e.g. DLC mismatch).
        /// Closes the join dialog, leaves the lobby, and shows an error message
        /// while keeping the player in the server browser.
        /// </summary>
        public void RejectJoin(string errorMessage)
        {
            CoopMod.Logger.LogInfo($"[ServerBrowser] Join rejected: {errorMessage}");
            isJoinInProgress = false;
            HideJoinDialog();

            var lobbyMgr = SteamLobbyManager.Instance;
            if (lobbyMgr != null && (lobbyMgr.IsInLobby || lobbyMgr.IsJoining))
            {
                lobbyMgr.LeaveLobby();
            }

            if (GUIElements.me != null && GUIElements.me.dialog != null)
            {
                GUIElements.me.dialog.OpenOK(errorMessage);
            }
        }

        /// <summary>
        /// Open the engine's standard dialog window (same one used for host-disconnect popups)
        /// showing a "Joining {server}..." message with a Cancel button. Clicking Cancel
        /// aborts the join attempt and leaves any partially-joined lobby.
        /// </summary>
        private void ShowJoinDialog(string serverName)
        {
            try
            {
                var dialog = GUIElements.me?.dialog;
                if (dialog == null)
                {
                    // Fallback: old inline label behaviour so we still show *something*.
                    if (noServersLabel != null)
                    {
                        noServersLabel.text = $"Connecting to {serverName}...";
                        noServersLabel.color = ONLINE_COLOR;
                        noServersLabel.gameObject.SetActive(true);
                    }
                    return;
                }

                string message = $"Joining {serverName}...";
                string cancelLabel = GJL.L("Cancel");
                if (string.IsNullOrEmpty(cancelLabel) || cancelLabel == "Cancel")
                    cancelLabel = "Cancel";

                // DialogGUI.Open(text, option_1, delegate_1, option_2, delegate_2, ...)
                // We pass option_2=null/delegate_2=null so only the Cancel button shows.
                dialog.Open(message, cancelLabel, new GJCommons.VoidDelegate(OnJoinDialogCancelled),
                            null, null, null, GameKey.Select, GameKey.Back, "", false, "");
                joinDialogOpen = true;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[ServerBrowser] Failed to open join dialog: {ex.Message}");
            }
        }

        /// <summary>
        /// Hide the join dialog if it is currently shown.
        /// </summary>
        private void HideJoinDialog()
        {
            if (!joinDialogOpen) return;
            joinDialogOpen = false;
            try
            {
                var dialog = GUIElements.me?.dialog;
                if (dialog != null && dialog.is_shown)
                {
                    dialog.Hide(true);
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[ServerBrowser] Failed to hide join dialog: {ex.Message}");
            }
        }

        /// <summary>
        /// Cancel callback wired into the join dialog's Cancel button.
        /// Aborts the in-flight join and leaves any lobby we may have partially joined.
        /// </summary>
        private void OnJoinDialogCancelled()
        {
            CoopMod.Logger.LogInfo("[ServerBrowser] User cancelled join via dialog");
            joinDialogOpen = false; // dialog already auto-hides on button click
            pendingPresenceJoinServer = null;

            // If the join already succeeded by the time the user clicked Cancel, treat it
            // as a no-op -- otherwise we'd disconnect them from the lobby they just joined
            // (and kick the host as a side effect on the host's end).
            var lobbyMgr = SteamLobbyManager.Instance;
            if (lobbyMgr != null && lobbyMgr.IsInLobby && !lobbyMgr.IsJoining)
            {
                CoopMod.Logger.LogInfo("[ServerBrowser] Cancel pressed but lobby join already succeeded -- ignoring");
                isJoinInProgress = false;
                return;
            }

            try
            {
                if (lobbyMgr != null && (lobbyMgr.IsInLobby || lobbyMgr.IsJoining))
                {
                    lobbyMgr.LeaveLobby();
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[ServerBrowser] LeaveLobby on cancel failed: {ex.Message}");
            }
            // Reset the in-progress guard but DON'T re-hide the dialog (it's already gone).
            isJoinInProgress = false;
            if (noServersLabel != null)
            {
                noServersLabel.text = "Cancelled";
                noServersLabel.color = DIM_TEXT_COLOR;
            }
        }

        private void Update()
        {
            ProcessPendingPingProbes();
            HideFavoriteMenuOnOutsideClick();

            if (currentTab == BrowserTab.LAN && Time.unscaledTime >= nextLanRefreshTime)
            {
                nextLanRefreshTime = Time.unscaledTime + LAN_REFRESH_INTERVAL;
                RefreshLanServersFromDiscovery(force: false);
            }

            if (currentTab == BrowserTab.LAN && Time.unscaledTime >= nextLanProbeTime)
            {
                nextLanProbeTime = Time.unscaledTime + LAN_PROBE_INTERVAL;
                LanDiscoveryService.Instance.SendDiscoveryProbe();
            }

            // Timeout the join-in-progress guard so the UI isn't stuck forever
            if (isJoinInProgress && Time.unscaledTime - joinStartTime > JOIN_TIMEOUT)
            {
                CoopMod.Logger.LogWarning("[ServerBrowser] Join timed out after 10 seconds, resetting state");
                ResetJoinState("Connection timed out. Try again.");
            }

            if (pendingPresenceJoinServer != null &&
                Time.unscaledTime - pendingPresenceJoinStartedAt > PRESENCE_JOIN_RETRY_TIMEOUT)
            {
                string serverName = pendingPresenceJoinServer.ServerName;
                pendingPresenceJoinServer = null;
                ShowServerUnavailable(serverName);
            }
        }

        private void HideFavoriteMenuOnOutsideClick()
        {
            if (!favoriteMenuOpen || !Input.GetMouseButtonDown(0))
                return;

            if (favoriteMenuUsesGameContextBubble)
            {
                var bubble = GUIElements.me?.context_menu_bubble;
                if (bubble != null && IsMouseOverWidget(bubble.gameObject))
                    return;

                HideFavoriteMenu();
                return;
            }

            if (favoriteMenuObj == null || IsMouseOverFavoriteMenu())
                return;

            HideFavoriteMenu();
        }

        private bool IsMouseOverFavoriteMenu()
        {
            var mainGame = MainGame.me;
            if (mainGame == null || mainGame.gui_cam == null || favoriteMenuObj == null)
                return false;

            Vector3 mouse = Input.mousePosition;
            Vector3 topLeft = favoriteMenuObj.transform.TransformPoint(new Vector3(-FAVORITE_MENU_WIDTH * 0.5f, FAVORITE_MENU_HEIGHT * 0.5f, 0f));
            Vector3 bottomRight = favoriteMenuObj.transform.TransformPoint(new Vector3(FAVORITE_MENU_WIDTH * 0.5f, -FAVORITE_MENU_HEIGHT * 0.5f, 0f));
            Vector3 topLeftScreen = mainGame.gui_cam.WorldToScreenPoint(topLeft);
            Vector3 bottomRightScreen = mainGame.gui_cam.WorldToScreenPoint(bottomRight);

            const float padding = 12f;
            float minX = Mathf.Min(topLeftScreen.x, bottomRightScreen.x) - padding;
            float maxX = Mathf.Max(topLeftScreen.x, bottomRightScreen.x) + padding;
            float minY = Mathf.Min(topLeftScreen.y, bottomRightScreen.y) - padding;
            float maxY = Mathf.Max(topLeftScreen.y, bottomRightScreen.y) + padding;

            return mouse.x >= minX && mouse.x <= maxX && mouse.y >= minY && mouse.y <= maxY;
        }

        private bool IsMouseOverWidget(GameObject obj)
        {
            var mainGame = MainGame.me;
            if (mainGame == null || mainGame.gui_cam == null || obj == null)
                return false;

            var widget = obj.GetComponent<UIWidget>();
            if (widget == null)
                return false;

            Vector3[] corners = widget.worldCorners;
            Vector3 bottomLeft = mainGame.gui_cam.WorldToScreenPoint(corners[0]);
            Vector3 topRight = mainGame.gui_cam.WorldToScreenPoint(corners[2]);

            const float padding = 12f;
            Vector3 mouse = Input.mousePosition;
            float minX = Mathf.Min(bottomLeft.x, topRight.x) - padding;
            float maxX = Mathf.Max(bottomLeft.x, topRight.x) + padding;
            float minY = Mathf.Min(bottomLeft.y, topRight.y) - padding;
            float maxY = Mathf.Max(bottomLeft.y, topRight.y) + padding;

            return mouse.x >= minX && mouse.x <= maxX && mouse.y >= minY && mouse.y <= maxY;
        }

        private void OnDisable()
        {
            pendingPresenceJoinServer = null;
            HideFavoriteMenu();
        }

        private void OnDestroy()
        {
            // Unsubscribe from lobby search events
            if (SteamManager.Initialized)
            {
                var lobbyMgr = SteamLobbyManager.Instance;
                if (lobbyMgr != null)
                {
                    lobbyMgr.OnLobbySearchCompleted -= OnFriendLobbySearchResults;
                    lobbyMgr.OnLobbySearchCompleted -= OnPublicLobbySearchResults;
                    lobbyMgr.OnLobbySearchCompleted -= OnFavoriteLobbySearchResults;
                    lobbyMgr.OnLobbyJoined -= OnLobbyJoinedHandler;
                }
            }
            LanDiscoveryService.Instance.StopListening();
            friendPresenceUpdateCallback?.Dispose();
            friendPresenceUpdateCallback = null;
            HideFavoriteMenu();
            if (favoriteMenuObj != null)
            {
                Destroy(favoriteMenuObj);
                favoriteMenuObj = null;
                favoriteMenuLabel = null;
                favoriteMenuBackSprite = null;
            }
            // Unsubscribe from ping results
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnPingResult -= OnPingResultReceived;
            }
            _instance = null;
        }

        private static Color GetPingColor(int ping)
        {
            if (ping < 0) return DIM_TEXT_COLOR;       // Unknown
            if (ping <= 50) return ONLINE_COLOR;       // Green — great
            if (ping <= 100) return new Color(1f, 0.85f, 0.3f, 1f); // Yellow — okay
            return new Color(0.9f, 0.35f, 0.3f, 1f);  // Red — poor
        }

        private static Color GetStatusColor(ServerEntry server)
        {
            if (server != null && server.IsOffline)
                return DIM_TEXT_COLOR;

            return server != null && server.IsRunningGame ? RUNNING_GAME_COLOR : ONLINE_COLOR;
        }

        private static string GetServerStatusText(string status, bool isRunningGame)
        {
            if (isRunningGame)
                return SteamLobbyManager.LobbyStatusInGame;

            if (string.IsNullOrEmpty(status))
                return SteamLobbyManager.LobbyStatusInLobby;

            if (status.IndexOf("lobby", StringComparison.OrdinalIgnoreCase) >= 0 ||
                status.IndexOf("hosting", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return SteamLobbyManager.LobbyStatusInLobby;
            }

            return status;
        }
        
        /// <summary>
        /// Send P2P pings to visible Steam hosts for latency measurement.
        /// </summary>
        private void SendPingsToVisibleHosts()
        {
            // Subscribe to ping results (unsubscribe first to avoid duplicates)
            var p2p = SteamP2PManager.Instance;
            if (p2p != null)
            {
                p2p.OnPingResult -= OnPingResultReceived;
                p2p.OnPingResult += OnPingResultReceived;
            }
            
            foreach (var entry in serverEntries)
            {
                if (!entry.IsOffline && entry.HostSteamID != CSteamID.Nil)
                {
                    StartPingProbe(entry.HostSteamID);
                    if (entry.Ping < 0)
                    {
                        UpdatePingLabel(entry.HostSteamID, "...", GetPingColor(entry.Ping));
                    }
                }
            }

            ProcessPendingPingProbes();
        }

        private void StartPingProbe(CSteamID hostID)
        {
            ulong hostKey = hostID.m_SteamID;
            if (activePingProbes.ContainsKey(hostKey))
                return;

            float now = Time.realtimeSinceStartup;
            activePingProbes[hostKey] = new PingProbeState
            {
                HostID = hostID,
                AttemptsSent = 0,
                NextAttemptTime = 0f,
                StartedAt = now
            };
        }

        private void ProcessPendingPingProbes()
        {
            if (activePingProbes.Count == 0 || !SteamManager.Initialized)
                return;

            var p2p = SteamP2PManager.Instance;
            if (p2p == null)
                return;

            float now = Time.realtimeSinceStartup;
            List<ulong> finishedHosts = null;

            foreach (var pair in activePingProbes)
            {
                var state = pair.Value;
                bool timedOut = now - state.StartedAt >= PING_TIMEOUT;

                if (state.AttemptsSent >= PING_MAX_ATTEMPTS && timedOut)
                {
                    MarkPingProbeTimedOut(pair.Key);
                    if (finishedHosts == null)
                        finishedHosts = new List<ulong>();
                    finishedHosts.Add(pair.Key);
                    continue;
                }

                if (state.AttemptsSent < PING_MAX_ATTEMPTS && now >= state.NextAttemptTime)
                {
                    CoopMod.Logger.LogInfo($"[ServerBrowser] Sending ping attempt {state.AttemptsSent + 1}/{PING_MAX_ATTEMPTS} to {state.HostID}");
                    p2p.SendPing(state.HostID);
                    state.AttemptsSent++;
                    state.NextAttemptTime = now + PING_RETRY_INTERVAL;
                }
            }

            if (finishedHosts == null)
                return;

            foreach (ulong hostKey in finishedHosts)
            {
                activePingProbes.Remove(hostKey);
            }
        }

        private void MarkPingProbeTimedOut(ulong hostKey)
        {
            foreach (var entry in serverEntries)
            {
                if (entry.HostSteamID.m_SteamID == hostKey && entry.Ping < 0)
                {
                    CoopMod.Logger.LogWarning($"[ServerBrowser] Ping timed out for {entry.ServerName} ({entry.HostSteamID})");
                    UpdatePingLabel(entry.HostSteamID, "---", GetPingColor(entry.Ping));
                    break;
                }
            }
        }
        
        /// <summary>
        /// Called when a ping result comes back — update the server entry and row label
        /// </summary>
        private void OnPingResultReceived(CSteamID hostID, int pingMs)
        {
            activePingProbes.Remove(hostID.m_SteamID);

            // Update the server entry
            foreach (var entry in serverEntries)
            {
                if (entry.HostSteamID == hostID)
                {
                    entry.Ping = pingMs;
                    break;
                }
            }
            
            // Update the row label if it exists
            UpdatePingLabel(hostID, $"{pingMs}ms", GetPingColor(pingMs));
        }

        private void UpdatePingLabel(CSteamID hostID, string text, Color color)
        {
            UILabel label;
            if (pingLabels.TryGetValue(hostID.m_SteamID, out label) && label != null)
            {
                label.text = text;
                label.color = color;
            }
        }
        
    }

    #region Click Handlers

    public class BrowserTabClickHandler : MonoBehaviour
    {
        private ServerBrowserGUI browser;
        private ServerBrowserGUI.BrowserTab tab;

        public void Initialize(ServerBrowserGUI browser, ServerBrowserGUI.BrowserTab tab)
        {
            this.browser = browser;
            this.tab = tab;
        }

        private void OnClick()
        {
            browser?.SwitchSubTab(tab);
        }

        private void OnHover(bool isOver)
        {
            if (isOver)
                browser?.FocusSubTab(tab);
            else
                browser?.FocusSubTab(browser.CurrentTab);
        }

        private void OnPress(bool isPressed)
        {
            if (!isPressed)
                OnClick();
        }
    }

    #endregion
}

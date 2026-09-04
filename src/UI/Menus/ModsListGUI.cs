using UnityEngine;
using BepInEx;
using System.Linq;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// Mods List GUI - Screen showing all loaded BepInEx mods
    /// </summary>
    public class ModsListGUI : BaseMenuGUI
    {
        private static ModsListGUI _instance;
        public static ModsListGUI Instance => _instance;

        private UILabel titleLabel;
        private UIPanel scrollPanel;
        private UIScrollView scrollView;
        private UIGrid modsGrid;
        private GameObject backButtonObj;

        public static ModsListGUI Create()
        {
            if (_instance != null)
                return _instance;

            CoopMod.Logger.LogInfo("Creating ModsListGUI...");

            // Find UIRoot to parent to
            UIRoot uiRoot = Object.FindObjectOfType<UIRoot>();
            if (uiRoot == null)
            {
                CoopMod.Logger.LogError("Cannot find UIRoot!");
                return null;
            }

            // Create GameObject for our mods screen
            GameObject modsListObj = new GameObject("ModsListGUI");
            modsListObj.layer = 13; // NGUI layer
            modsListObj.transform.SetParent(uiRoot.transform, false);
            modsListObj.transform.localPosition = Vector3.zero;
            modsListObj.transform.localScale = Vector3.one;
            
            _instance = modsListObj.AddComponent<ModsListGUI>();
            _instance.add_to_opened_stack = true;

            // Keep alive across scenes
            Object.DontDestroyOnLoad(modsListObj);

            // Add UIPanel for rendering
            UIPanel panel = modsListObj.AddComponent<UIPanel>();
            panel.depth = 100; // Above main menu
            panel.alpha = 1f;
            panel.clipping = UIDrawCall.Clipping.None;

            CoopMod.Logger.LogInfo($"Created mods list panel, parent: {uiRoot.name}");

            // Create content
            _instance.CreateModsListContent(modsListObj);

            // Initialize BaseMenuGUI
            _instance.Init();

            // Hide initially
            modsListObj.SetActive(false);

            CoopMod.Logger.LogInfo($"ModsListGUI created successfully!");
            return _instance;
        }

        private void CreateModsListContent(GameObject root)
        {
            CoopMod.Logger.LogInfo("Creating mods list content...");

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
                CoopMod.Logger.LogWarning("Could not find reference font!");
            }

            // Create title
            GameObject titleObj = new GameObject("Title");
            titleObj.transform.SetParent(root.transform, false);
            titleObj.layer = root.layer;
            
            titleLabel = titleObj.AddComponent<UILabel>();
            if (referenceFont != null)
                titleLabel.bitmapFont = referenceFont;
            titleLabel.text = "Loaded Mods";
            titleLabel.fontSize = 48;
            titleLabel.color = Color.white;
            titleLabel.alignment = NGUIText.Alignment.Center;
            titleLabel.pivot = UIWidget.Pivot.Top;
            titleLabel.overflowMethod = UILabel.Overflow.ResizeFreely;
            
            // Position title at top
            titleObj.transform.localPosition = new Vector3(0f, 300f, 0f);

            // Create scrollable area for mods list
            CreateScrollableModsList(root, referenceFont);

            // Create back button
            CreateBackButton(root, referenceFont);

            CoopMod.Logger.LogInfo("Mods list content created");
        }

        private void CreateScrollableModsList(GameObject root, UIFont font)
        {
            // Create scroll view container
            GameObject scrollObj = new GameObject("ModsScrollView");
            scrollObj.transform.SetParent(root.transform, false);
            scrollObj.layer = root.layer;
            scrollObj.transform.localPosition = new Vector3(0f, 0f, 0f);

            // Add panel for clipping
            scrollPanel = scrollObj.AddComponent<UIPanel>();
            scrollPanel.depth = 1;
            scrollPanel.clipping = UIDrawCall.Clipping.SoftClip;
            scrollPanel.clipSoftness = new Vector2(10f, 10f);
            
            // Set clip region size (width, height)
            scrollPanel.baseClipRegion = new Vector4(0f, 0f, 800f, 400f);

            // Add scroll view
            scrollView = scrollObj.AddComponent<UIScrollView>();
            scrollView.contentPivot = UIWidget.Pivot.Top;
            scrollView.movement = UIScrollView.Movement.Vertical;
            scrollView.dragEffect = UIScrollView.DragEffect.MomentumAndSpring;
            scrollView.scrollWheelFactor = 0.05f;

            // Create grid container for mod entries
            GameObject gridObj = new GameObject("ModsGrid");
            gridObj.transform.SetParent(scrollObj.transform, false);
            gridObj.layer = root.layer;
            gridObj.transform.localPosition = Vector3.zero;

            modsGrid = gridObj.AddComponent<UIGrid>();
            modsGrid.arrangement = UIGrid.Arrangement.Vertical;
            modsGrid.cellHeight = 60f;
            modsGrid.cellWidth = 780f;
            modsGrid.pivot = UIWidget.Pivot.Top;

            // Populate with loaded mods
            PopulateModsList(gridObj, font);

            // Reposition grid
            modsGrid.Reposition();
        }

        private void PopulateModsList(GameObject gridParent, UIFont font)
        {
            CoopMod.Logger.LogInfo("Populating mods list...");

            // Get all loaded plugins
            var plugins = BepInEx.Bootstrap.Chainloader.PluginInfos.Values.ToList();
            
            CoopMod.Logger.LogInfo($"Found {plugins.Count} loaded mods");

            foreach (var plugin in plugins)
            {
                CreateModEntry(gridParent, plugin, font);
            }
        }

        private void CreateModEntry(GameObject parent, BepInEx.PluginInfo plugin, UIFont font)
        {
            // Create container for this mod entry
            GameObject entryObj = new GameObject($"ModEntry_{plugin.Metadata.GUID}");
            entryObj.transform.SetParent(parent.transform, false);
            entryObj.layer = parent.layer;

            // Create background (optional, for visual separation)
            UISprite bg = entryObj.AddComponent<UISprite>();
            bg.width = 780;
            bg.height = 55;
            bg.color = new Color(0.2f, 0.2f, 0.2f, 0.5f);
            bg.type = UIBasicSprite.Type.Sliced;
            bg.pivot = UIWidget.Pivot.Top;

            // Create label for mod name and version
            GameObject labelObj = new GameObject("Label");
            labelObj.transform.SetParent(entryObj.transform, false);
            labelObj.layer = parent.layer;
            labelObj.transform.localPosition = new Vector3(-380f, -10f, 0f);

            UILabel label = labelObj.AddComponent<UILabel>();
            if (font != null)
                label.bitmapFont = font;
            
            label.text = $"{plugin.Metadata.Name} v{plugin.Metadata.Version}";
            label.fontSize = 24;
            label.color = Color.white;
            label.alignment = NGUIText.Alignment.Left;
            label.pivot = UIWidget.Pivot.TopLeft;
            label.overflowMethod = UILabel.Overflow.ResizeFreely;

            // Create GUID label (smaller, below name)
            GameObject guidObj = new GameObject("GUID");
            guidObj.transform.SetParent(entryObj.transform, false);
            guidObj.layer = parent.layer;
            guidObj.transform.localPosition = new Vector3(-380f, -35f, 0f);

            UILabel guidLabel = guidObj.AddComponent<UILabel>();
            if (font != null)
                guidLabel.bitmapFont = font;
            
            guidLabel.text = plugin.Metadata.GUID;
            guidLabel.fontSize = 16;
            guidLabel.color = new Color(0.7f, 0.7f, 0.7f, 1f);
            guidLabel.alignment = NGUIText.Alignment.Left;
            guidLabel.pivot = UIWidget.Pivot.TopLeft;
            guidLabel.overflowMethod = UILabel.Overflow.ResizeFreely;

            CoopMod.Logger.LogInfo($"Added mod entry: {plugin.Metadata.Name}");
        }

        private void CreateBackButton(GameObject root, UIFont font)
        {
            // Try to find an existing button to clone
            var existingButtons = Object.FindObjectsOfType<MenuItemGUI>();
            MenuItemGUI templateButton = null;
            
            foreach (var btn in existingButtons)
            {
                if (btn.gameObject.name.Contains("back") || btn.gameObject.name.Contains("cancel"))
                {
                    templateButton = btn;
                    break;
                }
            }

            if (templateButton != null)
            {
                // Clone existing button
                backButtonObj = Object.Instantiate(templateButton.gameObject, root.transform);
                backButtonObj.name = "BackButton";
                backButtonObj.transform.localPosition = new Vector3(0f, -350f, 0f);

                // Update text
                var labels = backButtonObj.GetComponentsInChildren<UILabel>();
                foreach (var label in labels)
                {
                    label.text = "Back";
                }

                // Update click handler
                var menuItem = backButtonObj.GetComponent<MenuItemGUI>();
                if (menuItem != null)
                {
                    menuItem.on_pressed = new EventDelegate(() => OnBackButtonPressed());
                }

                CoopMod.Logger.LogInfo("Back button created from template");
            }
            else
            {
                // Create simple back button
                CoopMod.Logger.LogInfo("Creating simple back button");
                
                backButtonObj = new GameObject("BackButton");
                backButtonObj.transform.SetParent(root.transform, false);
                backButtonObj.layer = root.layer;
                backButtonObj.transform.localPosition = new Vector3(0f, -350f, 0f);

                UILabel backLabel = backButtonObj.AddComponent<UILabel>();
                if (font != null)
                    backLabel.bitmapFont = font;
                backLabel.text = "[Press ESC to go back]";
                backLabel.fontSize = 24;
                backLabel.color = new Color(0.8f, 0.8f, 0.8f, 1f);
                backLabel.alignment = NGUIText.Alignment.Center;
                backLabel.pivot = UIWidget.Pivot.Center;
                backLabel.overflowMethod = UILabel.Overflow.ResizeFreely;
            }
        }

        private void OnBackButtonPressed()
        {
            CoopMod.Logger.LogInfo("Back button pressed, closing mods list");
            Close();
        }

        public override void Update()
        {
            base.Update();
            
            // Allow ESC to close
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                OnBackButtonPressed();
            }
        }

        public override void Open()
        {
            base.Open();
            CoopMod.Logger.LogInfo("ModsListGUI opened");
            gameObject.SetActive(true);
        }

        public void Close()
        {
            CoopMod.Logger.LogInfo("ModsListGUI closed");
            gameObject.SetActive(false);
            
            // Return to main menu
            var mainMenu = Object.FindObjectOfType<MainMenuGUI>();
            if (mainMenu != null)
            {
                mainMenu.Open();
            }
        }
    }
}

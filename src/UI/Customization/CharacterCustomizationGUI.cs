using System;
using System.Collections.Generic;
using System.IO;
using GraveyardKeeperCoop.Multiplayer;
using UnityEngine;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// Wardrobe editor assembled from the vanilla character/inventory window.
    /// Changes preview locally and are persisted/networked only by Apply.
    /// </summary>
    [DefaultExecutionOrder(21000)]
    public sealed class CharacterCustomizationGUI : BaseGUI
    {
        private const int MaxRecentColors = 8;
        private const float FallbackLeftX = -220f;
        private const float FallbackRightX = 220f;
        private const float FallbackHeaderY = 170f;
        private const int LivePreviewDepth = 70;
        private const int LivePreviewTextureHeight = 512;
        private const float LivePreviewPlayerPadding = 0.54f;
        private const float EyesPreviewPlayerPadding = 0.9f;
        private const float EyesPreviewOrthographicScale = 0.36f;
        private const float EyesPreviewFocusFromTop = 0.25f;
        private const float LivePreviewFrameBorder = 16f;
        private const float LivePreviewFrameColumnInset = 2f;
        private const float LivePreviewBottomOffset = 108f;
        private const string LivePreviewFrameResource =
            "GraveyardKeeperCoop.Assets.CharacterCustomization.wardrobe_camera_frame.png";
        private const float FooterOffsetFromHeader = 390f;
        private const float FooterWidthScale = 1.045f;
        private const float FooterHeightScale = 2f;
        private const float HeaderWidthScale = 1.045f;
        private const int FooterBarDepth = 50;
        private const int FooterButtonDepth = 60;
        private const float HeaderHorizontalInset = 0.5f;
        private const int PaletteTrackWidth = 195;
        private const int PaletteTrackHeight = 14;
        private const int PresetPreviewWidth = 84;
        private const int PresetPreviewHeight = 110;
        private const float PresetCarouselVerticalOffset = 18f;
        private const int PresetManagerPreviewWidth = 180;
        private const int PresetManagerPreviewHeight = 236;
        private const int PresetManagerSlotWidth = 76;
        private const int PresetManagerSlotHeight = 88;
        private const int PresetManagerDepth = 140;
        private const float PresetPreviewPlayerPadding = 0.62f;
        private const float ResponsiveFitReferenceHeight = 800f;
        private const float ResponsiveFitVerticalMargin = 80f;
        private const float ResponsiveMinimumScale = 0.72f;
        private const int IsolatedPreviewLayer = 30;
        private const int PresetPreviewLayer = 31;
        private const float SliderPreviewDebounceSeconds = 0.25f;
        private static readonly Color IsolatedPreviewBackground =
            new Color32(22, 23, 29, 255);
        private static readonly Vector3 IsolatedPreviewPosition =
            new Vector3(100000f, 100000f, 0f);

        private static readonly CosmeticCategory[] Categories =
        {
            CosmeticCategory.Pants,
            CosmeticCategory.Shirt,
            CosmeticCategory.Hair,
            CosmeticCategory.Eyes,
            CosmeticCategory.Skin,
        };

        private static readonly string[] CategoryNames =
        {
            "Pants", "Shirt", "Hair", "Eyes", "Skin",
        };

        private static readonly string[][] ColorNames =
        {
            new[] { "Brown", "Navy", "Burgundy", "Forest", "Gray", "Black", "Tan", "Plum" },
            new[] { "Beige", "Blue", "Red", "Green", "White", "Gray", "Yellow", "Pink" },
            new[] { "Brown", "Blond", "Black", "Auburn", "Gray", "White", "Ginger", "Dark brown" },
            new[] { "Brown", "Blue", "Green", "Hazel", "Gray", "Violet", "Amber", "Black" },
            new[] { "Light", "Medium light", "Medium", "Deep", "Dark", "Very dark" },
        };

        private static readonly string[] ToneNames =
        {
            "Muted dark", "Dark", "Soft", "Standard",
            "Clear", "Light", "Bright", "Vivid",
        };

        private sealed class TabVisual
        {
            public GameObject Root;
            public UIButton Button;
            public GameObject ActiveDecoration;
            public Color DefaultColor;
        }

        private sealed class ColorSlot
        {
            public GameObject Root;
            public UITexture Swatch;
            public UI2DSprite Selection;
            public int Value;
        }

        private sealed class PresetManagerSlotVisual
        {
            public int Index;
            public GameObject Root;
            public UITexture Preview;
            public RenderTexture RenderTexture;
            public readonly List<UIWidget> FramePieces = new List<UIWidget>();
            public readonly List<Color> FrameColors = new List<Color>();
        }

        private static CharacterCustomizationGUI _instance;
        private readonly List<TabVisual> _tabs = new List<TabVisual>();
        private readonly List<ColorSlot> _recentSlots = new List<ColorSlot>();
        private readonly List<PlayerCosmetics> _savedPresets =
            new List<PlayerCosmetics>();
        private readonly List<PresetManagerSlotVisual> _presetManagerSlots =
            new List<PresetManagerSlotVisual>();

        private PlayerCosmetics _original;
        private PlayerCosmetics _draft;
        private PlayerCosmetics _lastSuccessfulPreview;
        private GameObject _contentLayer;
        private GameObject _slotTemplate;
        private UITexture _livePreviewWidget;
        private GameObject _livePreviewFrameRoot;
        private Texture2D _livePreviewFrameTexture;
        private GameObject _livePreviewCameraObject;
        private Camera _livePreviewCamera;
        private RenderTexture _livePreviewRenderTexture;
        private UITexture _presetPreviewWidget;
        private GameObject _presetPreviewCameraObject;
        private Camera _presetPreviewCamera;
        private RenderTexture _presetPreviewRenderTexture;
        private GameObject _presetPreviousArrow;
        private GameObject _presetNextArrow;
        private UILabel _presetSlotStatusLabel;
        private GameObject _presetSaveButton;
        private GameObject _presetEditButton;
        private GameObject _presetManagerRoot;
        private UITexture _presetManagerPreviewWidget;
        private UILabel _presetManagerStatusLabel;
        private GameObject _presetManagerPreviousArrow;
        private GameObject _presetManagerNextArrow;
        private GameObject _presetManagerDeleteButton;
        private readonly Dictionary<GameObject, bool> _editorViewStates =
            new Dictionary<GameObject, bool>();
        private GameObject _isolatedPreviewRoot;
        private PlayerCosmeticsDriver _isolatedPreviewDriver;
        private BaseCharacterComponent _livePreviewCharacter;
        private Vector2 _livePreviewOriginalDirection;
        private bool _livePreviewFacingStored;
        private UISlider _hueSlider;
        private UISlider _toneSlider;
        private UITexture _hueTrack;
        private UITexture _toneTrack;
        private UITexture _currentSwatch;
        private UILabel _leftHeaderLabel;
        private UILabel _rightHeaderLabel;
        private UILabel _currentNameLabel;
        private Texture2D _hueTrackTexture;
        private Texture2D _toneTrackTexture;
        private bool _initialized;
        private bool _closing;
        private bool _openedFromLobby;
        private bool _suppressLobbyReturn;
        private bool _updatingSlider;
        private bool _previewPending;
        private bool _livePreviewDirty;
        private bool _presetPreviewDirty;
        private bool _presetManagerVisible;
        private bool _deleteConfirmationOpen;
        private bool _saveConfirmationOpen;
        private bool _sliderDragging;
        private float _previewDueAt;
        private int _selectedCategory;
        private int _presetIndex;
        private float _presetPreviewY;
        private float _leftX;
        private float _rightX;
        private float _headerY;
        private float _bottomY;
        private int _uiLayer;
        private int _contentPanelDepth;
        private int _appliedScreenWidth = -1;
        private int _appliedScreenHeight = -1;

        public static CharacterCustomizationGUI Instance => _instance;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
            _sliderDragging = false;
            StopPresetPreview();
            DestroyPresetManagerSlotRenderTextures();
            StopLivePlayerPreview();
            StopIsolatedKeeperPreview();
            if (_hueTrackTexture != null)
                Destroy(_hueTrackTexture);
            if (_toneTrackTexture != null)
                Destroy(_toneTrackTexture);
            if (_livePreviewFrameTexture != null)
                Destroy(_livePreviewFrameTexture);
        }

        public static CharacterCustomizationGUI Create()
        {
            if (_instance != null)
                return _instance;

            GameObject clone = null;
            try
            {
                GameGUI sourceWindow = GUIElements.me?.game_gui;
                InventoryGUI sourceInventory = GUIElements.me?.inventory;
                SmartSlider tradeSlider =
                    GUIElements.me?.item_count?.GetComponentInChildren<SmartSlider>(true);
                if (sourceWindow == null || sourceInventory == null || tradeSlider == null)
                {
                    CoopMod.Logger.LogError(
                        $"[CharacterCustomization] Native UI unavailable: " +
                        $"game_gui={sourceWindow != null}, inventory={sourceInventory != null}, " +
                        $"trade_slider={tradeSlider != null}");
                    return null;
                }

                clone = new GameObject("CharacterCustomizationGUI");
                clone.layer = sourceWindow.gameObject.layer;
                clone.transform.SetParent(sourceWindow.transform.parent, false);
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localScale = Vector3.one;
                clone.SetActive(false);

                GameObject chrome = CloneNativeRoot(
                    sourceWindow.gameObject,
                    clone.transform,
                    "WardrobeWindowChrome");
                chrome.SetActive(true);

                // These roots are scaled independently by GUIElements. Preserve
                // their effective world transforms when moving them below the
                // wardrobe wrapper or the inventory grows at some resolutions.
                GameObject inventoryClone = CloneNativeRoot(
                    sourceInventory.gameObject,
                    clone.transform,
                    "WardrobeInventorySurface");
                inventoryClone.SetActive(true);
                InventoryGUI inventory = inventoryClone.GetComponent<InventoryGUI>();
                if (inventory == null)
                    throw new InvalidOperationException("Cloned inventory surface has no InventoryGUI");

                GameObject inventoryRoot = inventory.gameObject;
                foreach (BaseGameGUI panel in clone.GetComponentsInChildren<BaseGameGUI>(true))
                {
                    // Keep the cloned GameGUI root alive: it owns the native frame,
                    // tabs, and every child panel. Only hide unrelated sibling panels.
                    bool ownsInventory = panel.gameObject == inventoryRoot ||
                                         inventoryRoot.transform.IsChildOf(panel.transform);
                    if (!ownsInventory)
                        panel.gameObject.SetActive(false);
                }
                inventoryRoot.SetActive(true);

                CharacterCustomizationGUI gui = clone.AddComponent<CharacterCustomizationGUI>();
                if (!gui.BuildInventoryWindow(inventoryRoot, inventory, tradeSlider.gameObject))
                    throw new InvalidOperationException("Native inventory window templates are incomplete");

                foreach (BaseGUI oldGui in clone.GetComponentsInChildren<BaseGUI>(true))
                {
                    if (oldGui != gui)
                        DestroyImmediate(oldGui);
                }
                foreach (GamepadNavigationController oldNavigation in
                         clone.GetComponentsInChildren<GamepadNavigationController>(true))
                    DestroyImmediate(oldNavigation);

                gui.Init();
                clone.SetActive(false);
                CoopMod.Logger.LogInfo(
                    $"[CharacterCustomization] Created from vanilla GameGUI/InventoryGUI " +
                    $"with native horizontal trade sliders (content_panel_depth={gui._contentPanelDepth}, " +
                    $"ui_layer={gui._uiLayer})");
                return gui;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError(
                    $"[CharacterCustomization] Failed to construct inventory wardrobe UI: {ex}");
                if (clone != null)
                {
                    if (_instance != null && _instance.gameObject == clone)
                        _instance = null;
                    DestroyImmediate(clone);
                }
                return null;
            }
        }

        public override void Init()
        {
            if (_initialized)
                return;
            base.Init();
            _initialized = true;
        }

        public void OpenForWardrobe(WorldGameObject wardrobe)
        {
            if (!MainGame.game_started || MainGame.me?.player == null)
                return;

            _openedFromLobby = false;
            _suppressLobbyReturn = false;
            StopIsolatedKeeperPreview();
            CosmeticsSync sync = CosmeticsSync.Instance;
            _original = (sync?.LocalCosmetics ?? PlayerCosmetics.Default).Clamped();
            _draft = _original.Clone();
            _selectedCategory = 0;
            _deleteConfirmationOpen = false;
            _saveConfirmationOpen = false;
            _sliderDragging = false;
            RestoreEditorView();
            LoadSavedPresets();
            PreviewDraft();
            RefreshAll();

            GUIElements.me?.hud?.OnAnyWindowOpened(this);
            base.Open();
            ApplyResponsiveScale(true);
            MainGame.SetPausedMode(true);
            StartLivePlayerPreview();
            StartPresetPreview();
            CoopMod.Logger.LogInfo(
                $"[CharacterCustomization] Opened from wardrobe '{wardrobe?.obj_id}'");
        }

        public bool OpenFromLobby()
        {
            if (Network.SteamLobbyManager.Instance?.IsInLobby != true)
            {
                CoopMod.Logger.LogWarning(
                    "[CharacterCustomization] Lobby editor requested outside a Steam lobby");
                return false;
            }

            CosmeticsSync sync = CosmeticsSync.Instance;
            if (sync == null)
            {
                CoopMod.Logger.LogError(
                    "[CharacterCustomization] CosmeticsSync is unavailable in the lobby");
                return false;
            }

            _openedFromLobby = true;
            _suppressLobbyReturn = false;
            _original = (sync.LocalCosmetics ?? PlayerCosmetics.Default).Clamped();
            _draft = _original.Clone();
            _selectedCategory = 0;
            _deleteConfirmationOpen = false;
            _saveConfirmationOpen = false;
            _sliderDragging = false;
            RestoreEditorView();
            LoadSavedPresets();

            base.Open();
            ApplyResponsiveScale(true);
            if (!StartIsolatedKeeperPreview())
            {
                base.Hide(false);
                _openedFromLobby = false;
                _original = null;
                _draft = null;
                return false;
            }

            PreviewDraft();
            RefreshAll();
            StartLivePlayerPreview();
            StartPresetPreview();
            CoopMod.Logger.LogInfo(
                "[CharacterCustomization] Opened lobby keeper editor with isolated preview");
            return true;
        }

        public void CloseForLobbyTransition()
        {
            if (!is_shown || !_openedFromLobby)
                return;

            _suppressLobbyReturn = true;
            CloseWithoutSaving(false);
        }

        public override void Update()
        {
            if (!is_shown)
                return;
            ApplyResponsiveScale(false);
            if (_openedFromLobby)
            {
                if (Network.SteamLobbyManager.Instance?.IsInLobby != true)
                {
                    _suppressLobbyReturn = true;
                    CloseWithoutSaving(false);
                    return;
                }
            }
            else if (!MainGame.game_started || MainGame.me?.player == null)
            {
                CloseWithoutSaving(false);
                return;
            }

            if (_presetManagerVisible && Input.GetKeyDown(KeyCode.Escape))
            {
                RestoreEditorView();
                return;
            }

            base.Update();
            FlushPendingPreview();
            if (Input.GetKeyDown(KeyCode.Escape))
                CloseWithoutSaving(true);
        }

        private void ApplyResponsiveScale(bool force)
        {
            int screenWidth = Screen.width;
            int screenHeight = Screen.height;
            if (!force && screenWidth == _appliedScreenWidth &&
                screenHeight == _appliedScreenHeight)
                return;

            _appliedScreenWidth = screenWidth;
            _appliedScreenHeight = screenHeight;
            float scale = Mathf.Clamp(
                (screenHeight - ResponsiveFitVerticalMargin) /
                ResponsiveFitReferenceHeight,
                ResponsiveMinimumScale,
                1f);
            transform.localScale = new Vector3(scale, scale, 1f);
            CoopMod.Logger.LogInfo(
                $"[CharacterCustomization] Applied responsive layout for " +
                $"{screenWidth}x{screenHeight}: rootScale={scale:F3}");
        }

        public new void LateUpdate()
        {
            base.LateUpdate();
            if (is_shown && _livePreviewDirty)
                RenderLivePlayerPreview();
            if (is_shown && _presetPreviewDirty)
            {
                _presetPreviewDirty = false;
                RenderPresetPreview();
            }
        }

        public override void Hide(bool playSound = true)
        {
            if (!is_shown)
                return;
            bool lobbyMode = _openedFromLobby;
            bool returnToLobby = lobbyMode && !_suppressLobbyReturn &&
                                 Network.SteamLobbyManager.Instance?.IsInLobby == true &&
                                 !MainGame.game_started;
            _sliderDragging = false;
            RestoreEditorView();
            if (!_closing)
                RestoreOriginalPreview();

            StopPresetPreview();
            StopLivePlayerPreview();
            StopIsolatedKeeperPreview();
            if (!lobbyMode)
                GUIElements.me?.hud?.OnAnyWindowClosed(this);
            base.Hide(playSound);
            if (!lobbyMode)
                MainGame.SetPausedMode(false);
            _openedFromLobby = false;

            if (returnToLobby)
                LobbyGUI.Instance?.ShowChatInput();
        }

        public override void OnClosePressed()
        {
            CloseWithoutSaving(true);
        }

        protected override bool CanCloseWithRightClick()
        {
            return true;
        }

        protected override bool OnPressedBack()
        {
            if (_presetManagerVisible)
            {
                RestoreEditorView();
                return true;
            }
            CloseWithoutSaving(true);
            return true;
        }

        protected override bool OnPressedOption1()
        {
            if (_presetManagerVisible)
                return true;
            Randomize();
            return true;
        }

        protected override bool OnPressedSelect()
        {
            // Mouse controls are wired directly. Until explicit gamepad focus is
            // added, do not call BaseGUI's null navigation controller.
            return false;
        }

        protected override bool OnPressedPrevTab()
        {
            if (_presetManagerVisible)
            {
                CycleSavedPreset(-1);
                return true;
            }
            SelectCategory((_selectedCategory + Categories.Length - 1) % Categories.Length);
            return true;
        }

        protected override bool OnPressedNextTab()
        {
            if (_presetManagerVisible)
            {
                CycleSavedPreset(1);
                return true;
            }
            SelectCategory((_selectedCategory + 1) % Categories.Length);
            return true;
        }

        protected override bool OnPressedLeft()
        {
            if (_presetManagerVisible)
            {
                CycleSavedPreset(-1);
                return true;
            }
            CycleColor(-1);
            return true;
        }

        protected override bool OnPressedRight()
        {
            if (_presetManagerVisible)
            {
                CycleSavedPreset(1);
                return true;
            }
            CycleColor(1);
            return true;
        }

        private bool BuildInventoryWindow(
            GameObject inventoryRoot,
            InventoryGUI inventory,
            GameObject sliderTemplate)
        {
            BaseItemCellGUI sourceCell = inventoryRoot.GetComponentInChildren<BaseItemCellGUI>(true);
            GameObject headerTemplate = inventory.go_hdr_buffs;
            UISlider sourceSlider = sliderTemplate.GetComponentInChildren<UISlider>(true);
            InventoryPanelGUI inventoryPanel =
                inventoryRoot.GetComponentInChildren<InventoryPanelGUI>(true);
            GameObject footerTemplate = inventoryPanel?.bottom_bar;
            if (sourceCell == null || headerTemplate == null || sourceSlider == null ||
                footerTemplate == null)
            {
                CoopMod.Logger.LogError(
                    "[CharacterCustomization] Inventory slot, header, slider, or footer template is missing");
                return false;
            }

            CalculateLayout(inventoryRoot.transform, headerTemplate.transform);
            _uiLayer = inventoryRoot.layer;
            _slotTemplate = Instantiate(sourceCell.gameObject);
            _slotTemplate.name = "WardrobeColorSlotTemplate";
            _slotTemplate.transform.SetParent(inventoryRoot.transform, false);
            _slotTemplate.SetActive(false);

            foreach (BaseInventoryWidget widget in
                     inventoryRoot.GetComponentsInChildren<BaseInventoryWidget>(true))
                widget.gameObject.SetActive(false);
            foreach (PerkBuffItemGUI item in
                     inventoryRoot.GetComponentsInChildren<PerkBuffItemGUI>(true))
                item.gameObject.SetActive(false);
            foreach (SeparatorGUI separator in
                     inventoryRoot.GetComponentsInChildren<SeparatorGUI>(true))
                separator.gameObject.SetActive(false);

            inventory.go_no_buffs?.SetActive(false);
            inventory.go_no_perks?.SetActive(false);
            inventory.go_hdr_buffs?.SetActive(false);
            inventory.go_hdr_perks?.SetActive(false);
            inventory.toolbelt_widget?.gameObject.SetActive(false);
            inventory.bag_inventory_widget?.gameObject.SetActive(false);
            inventory.bag_panel?.gameObject.SetActive(false);

            _contentLayer = new GameObject("WardrobeContent");
            _contentLayer.layer = _uiLayer;
            _contentLayer.transform.SetParent(inventoryRoot.transform, false);
            _contentLayer.transform.localPosition = Vector3.zero;
            _contentLayer.transform.localScale = Vector3.one;
            UIPanel contentPanel = _contentLayer.AddComponent<UIPanel>();
            _contentPanelDepth = GetMaximumPanelDepth(gameObject) + 1;
            contentPanel.depth = _contentPanelDepth;
            contentPanel.alpha = 1f;
            contentPanel.clipping = UIDrawCall.Clipping.None;

            BuildFooterBar(footerTemplate);
            footerTemplate.SetActive(false);
            BuildLivePreviewSurface();
            BuildLivePreviewFrame();
            BuildPresetCarousel();
            BuildHeaders(headerTemplate);
            if (!BuildTabs())
                return false;
            BuildRecentColorSlots();
            if (!BuildSliders(sourceSlider.gameObject, headerTemplate))
                return false;
            BuildCurrentColor(headerTemplate);
            if (!BuildButtons())
                return false;
            BuildPresetManager(headerTemplate);
            WireCloseButton();
            StripLocalization(gameObject);
            SetLayerRecursively(_contentLayer, _uiLayer);
            return true;
        }

        private void CalculateLayout(Transform root, Transform rightHeader)
        {
            Vector3 headerPosition = root.InverseTransformPoint(rightHeader.position);
            _rightX = Mathf.Abs(headerPosition.x) > 100f ? headerPosition.x : FallbackRightX;
            _leftX = -Mathf.Abs(_rightX);
            _headerY = Mathf.Abs(headerPosition.y) > 50f ? headerPosition.y : FallbackHeaderY;

            InventoryPanelGUI inventoryPanel =
                root.GetComponentInChildren<InventoryPanelGUI>(true);
            if (inventoryPanel?.bottom_bar != null)
            {
                _bottomY = root.InverseTransformPoint(
                    inventoryPanel.bottom_bar.transform.position).y;
                return;
            }

            float minimumY = float.MaxValue;
            foreach (UIWidget widget in root.GetComponentsInChildren<UIWidget>(true))
            {
                if (!widget.enabled || !widget.gameObject.activeSelf || widget.alpha <= 0f)
                    continue;
                foreach (Vector3 corner in widget.worldCorners)
                    minimumY = Mathf.Min(minimumY, root.InverseTransformPoint(corner).y);
            }
            _bottomY = minimumY < -100f && minimumY > -500f ? minimumY + 48f : -220f;
        }

        private void BuildHeaders(GameObject template)
        {
            float sideWidth = GetSideHeaderWidth();
            float sideCenter = GetSideHeaderCenter();
            _leftHeaderLabel = CreateHeader(
                template,
                "Saved pants colors",
                -sideCenter,
                _headerY,
                sideWidth);
            CreateHeader(template, "Keeper", 0f, _headerY, GetKeeperHeaderWidth());
            _rightHeaderLabel = CreateHeader(
                template,
                "Pants color",
                sideCenter,
                _headerY,
                sideWidth);
        }

        private UILabel CreateHeader(
            GameObject template,
            string text,
            float x,
            float y,
            float sectionWidth,
            Transform parent = null,
            int minimumDepth = -1)
        {
            GameObject header = Instantiate(template, parent ?? _contentLayer.transform);
            header.name = "WardrobeHeader_" + text.Replace(' ', '_');
            ClearExternalAnchors(header);
            header.transform.localPosition = new Vector3(x, y, 0f);
            header.transform.localScale = Vector3.one;
            header.SetActive(true);
            StripLocalization(header);
            UILabel label = header.GetComponentInChildren<UILabel>(true);
            Vector3 originalLabelScale = label != null
                ? label.transform.localScale
                : Vector3.one;
            if (label != null)
            {
                label.text = text;
                label.MarkAsChanged();
            }
            if (TryGetWidgetBounds(header, out Bounds bounds))
            {
                float desiredWidth = Mathf.Max(
                    1f,
                    sectionWidth - HeaderHorizontalInset * 2f);
                if (bounds.size.x > 1f)
                {
                    float widthScale = desiredWidth / bounds.size.x;
                    header.transform.localScale = new Vector3(widthScale, 1f, 1f);
                    if (label != null && Mathf.Abs(widthScale) > 0.001f)
                    {
                        label.transform.localScale = new Vector3(
                            originalLabelScale.x / widthScale,
                            originalLabelScale.y,
                            originalLabelScale.z);
                    }
                    TryGetWidgetBounds(header, out bounds);
                }
                header.transform.localPosition += new Vector3(
                    x - bounds.center.x,
                    y - bounds.center.y,
                    0f);
            }
            if (minimumDepth >= 0)
                SetMinimumWidgetDepth(header, minimumDepth);
            return label;
        }

        private float GetKeeperHeaderWidth()
        {
            return Mathf.Max(120f, Mathf.Abs(_rightX) - 32f);
        }

        private float GetSideHeaderWidth()
        {
            float totalWidth = Mathf.Abs(_rightX) * 3f * HeaderWidthScale;
            return Mathf.Max(120f, (totalWidth - GetKeeperHeaderWidth()) * 0.5f);
        }

        private float GetSideHeaderCenter()
        {
            return (GetKeeperHeaderWidth() + GetSideHeaderWidth()) * 0.5f;
        }

        private bool BuildTabs()
        {
            List<GameTabItemGUI> activeTabs = new List<GameTabItemGUI>();
            foreach (GameTabItemGUI tab in GetComponentsInChildren<GameTabItemGUI>(true))
            {
                if (tab.gameObject.activeSelf)
                    activeTabs.Add(tab);
            }
            activeTabs.Sort((a, b) => a.transform.position.x.CompareTo(b.transform.position.x));
            if (activeTabs.Count == 0)
            {
                CoopMod.Logger.LogWarning("[CharacterCustomization] No inventory tabs found");
                return false;
            }

            while (activeTabs.Count < Categories.Length)
            {
                GameObject duplicate = Instantiate(
                    activeTabs[activeTabs.Count - 1].gameObject,
                    activeTabs[activeTabs.Count - 1].transform.parent);
                duplicate.SetActive(true);
                activeTabs.Add(duplicate.GetComponent<GameTabItemGUI>());
            }

            UITable table = activeTabs[0].GetComponentInParent<UITable>();

            for (int index = 0; index < activeTabs.Count; index++)
            {
                GameTabItemGUI sourceTab = activeTabs[index];
                if (index >= Categories.Length)
                {
                    sourceTab.gameObject.SetActive(false);
                    continue;
                }

                int capturedIndex = index;
                GameObject root = sourceTab.gameObject;
                root.name = "WardrobeTab_" + CategoryNames[index];
                StripLocalization(root);
                UILabel label = root.GetComponentInChildren<UILabel>(true);
                if (label != null)
                {
                    label.text = CategoryNames[index];
                    label.MarkAsChanged();
                }

                UIButton button = root.GetComponentInChildren<UIButton>(true);
                if (button != null)
                    button.onClick.Clear();
                UIEventListener.Get(button != null ? button.gameObject : root).onClick =
                    go => SelectCategory(capturedIndex);

                _tabs.Add(new TabVisual
                {
                    Root = root,
                    Button = button,
                    ActiveDecoration = sourceTab.go_active,
                    DefaultColor = button != null ? button.defaultColor : Color.white,
                });
                DestroyImmediate(sourceTab);
            }

            if (table != null)
            {
                table.Reposition();
                table.repositionNow = true;
            }
            return true;
        }

        private void BuildRecentColorSlots()
        {
            const float spacing = 58f;
            for (int index = 0; index < MaxRecentColors; index++)
            {
                int column = index % 3;
                int row = index / 3;
                Vector3 position = new Vector3(
                    _leftX + (column - 1) * spacing,
                    _headerY - 55f - row * spacing,
                    0f);
                ColorSlot slot = CreateColorSlot("RecentColor_" + index, position);
                int capturedIndex = index;
                UIEventListener.Get(slot.Root).onClick = go => SelectRecentColor(capturedIndex);
                _recentSlots.Add(slot);
            }
        }

        private ColorSlot CreateColorSlot(string name, Vector3 position)
        {
            GameObject root = Instantiate(_slotTemplate, _contentLayer.transform);
            root.name = name;
            ClearExternalAnchors(root);
            root.transform.localPosition = position;
            root.transform.localScale = Vector3.one;
            root.SetActive(true);
            BaseItemCellGUI cell = root.GetComponent<BaseItemCellGUI>();
            BaseItemCellElements elements = cell?.x1;
            if (elements?.container == null)
                elements = cell?.x2;
            if (elements?.container == null || elements.back == null)
            {
                throw new InvalidOperationException(
                    $"Native inventory cell '{_slotTemplate?.name}' has no usable single-slot frame");
            }
            if (cell?.x1?.container != null)
                cell.x1.container.SetActive(elements == cell.x1);
            if (cell?.x2?.container != null)
                cell.x2.container.SetActive(elements == cell.x2);
            UI2DSprite back = elements?.back;
            UI2DSprite selection = elements?.selection ?? FindNamedSprite(root, "selection");
            if (back != null)
            {
                back.enabled = true;
                back.gameObject.SetActive(true);
            }
            if (elements?.icon != null)
                elements.icon.gameObject.SetActive(false);
            if (elements?.counter != null)
                elements.counter.gameObject.SetActive(false);
            if (elements?.gamepad_frame != null)
                elements.gamepad_frame.gameObject.SetActive(false);
            if (elements?.gratitude_craft_label != null)
                elements.gratitude_craft_label.gameObject.SetActive(false);
            if (elements?.empty_item_gfx != null)
                elements.empty_item_gfx.SetActive(false);
            if (elements?.icon_cap_limit != null)
                elements.icon_cap_limit.gameObject.SetActive(false);
            SetInactive(cell?.additional_icon);
            SetInactive(cell?.progress);
            SetInactive(cell?.quality_icon);
            SetInactive(cell?.radial_dim);
            SetInactive(cell?.item_name);
            SetInactive(cell?.item_description);
            SetInactive(cell?.price);
            foreach (LocalizedLabel localized in root.GetComponentsInChildren<LocalizedLabel>(true))
                DestroyImmediate(localized);
            foreach (UIButton button in root.GetComponentsInChildren<UIButton>(true))
                DestroyImmediate(button);
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
                DestroyImmediate(collider);
            foreach (Collider2D collider in root.GetComponentsInChildren<Collider2D>(true))
                DestroyImmediate(collider);
            foreach (BaseItemCellGUI oldCell in root.GetComponentsInChildren<BaseItemCellGUI>(true))
                DestroyImmediate(oldCell);

            UIWidget frame = back != null ? back : FindLargestWidget(root);
            int width = frame != null ? Mathf.Clamp(frame.width - 14, 22, 42) : 34;
            int height = frame != null ? Mathf.Clamp(frame.height - 14, 22, 42) : 34;
            GameObject swatchObject = new GameObject("ColorSwatch");
            swatchObject.layer = _uiLayer;
            swatchObject.transform.SetParent(root.transform, false);
            if (frame != null)
            {
                swatchObject.transform.localPosition =
                    root.transform.InverseTransformPoint(frame.transform.position);
            }
            UITexture swatch = swatchObject.AddComponent<UITexture>();
            swatch.mainTexture = Texture2D.whiteTexture;
            swatch.width = width;
            swatch.height = height;
            int maximumDepth = GetMaximumDepth(root);
            swatch.depth = back != null ? back.depth + 1 : maximumDepth + 1;
            if (selection != null)
                selection.depth = Mathf.Max(selection.depth, swatch.depth + 1);

            BoxCollider2D box = root.AddComponent<BoxCollider2D>();
            box.offset = frame != null
                ? (Vector2)root.transform.InverseTransformPoint(frame.transform.position)
                : Vector2.zero;
            box.size = new Vector2(frame != null ? frame.width : 50, frame != null ? frame.height : 50);
            box.isTrigger = true;

            ColorSlot result = new ColorSlot { Root = root, Swatch = swatch, Selection = selection };
            UIEventListener listener = UIEventListener.Get(root);
            listener.onHover = (go, over) =>
            {
                if (selection != null)
                    selection.gameObject.SetActive(over || IsSlotSelected(result));
                if (over && !Sounds.WasAnySoundPlayedThisFrame())
                    Sounds.OnGUIHover(Sounds.ElementType.Button);
            };
            return result;
        }

        private bool BuildSliders(GameObject sliderTemplate, GameObject headerTemplate)
        {
            float headerCenter = GetSideHeaderCenter();
            float headerWidth = GetSideHeaderWidth();
            CreateHeader(
                headerTemplate,
                "Hue",
                headerCenter,
                _headerY - 48f,
                headerWidth);
            CreateHeader(
                headerTemplate,
                "Saturation / Brightness",
                headerCenter,
                _headerY - 132f,
                headerWidth);

            _hueSlider = CreateHorizontalColorSlider(
                sliderTemplate,
                "WardrobeHueSlider",
                new Vector3(_rightX, _headerY - 82f, 0f),
                OnHueSliderChanged,
                out _hueTrack);
            _toneSlider = CreateHorizontalColorSlider(
                sliderTemplate,
                "WardrobeToneSlider",
                new Vector3(_rightX, _headerY - 166f, 0f),
                OnToneSliderChanged,
                out _toneTrack);
            return _hueSlider != null && _toneSlider != null;
        }

        private UISlider CreateHorizontalColorSlider(
            GameObject sliderTemplate,
            string name,
            Vector3 targetCenter,
            EventDelegate.Callback onChanged,
            out UITexture track)
        {
            track = null;
            GameObject sliderObject = Instantiate(sliderTemplate, _contentLayer.transform);
            sliderObject.name = name;
            ClearExternalAnchors(sliderObject);
            sliderObject.transform.localPosition = Vector3.zero;
            sliderObject.transform.localScale = Vector3.one;
            sliderObject.SetActive(true);
            StripLocalization(sliderObject);

            UISlider slider = sliderObject.GetComponent<UISlider>() ??
                              sliderObject.GetComponentInChildren<UISlider>(true);
            if (slider == null)
            {
                DestroyImmediate(sliderObject);
                return null;
            }

            SmartSlider inheritedSmartSlider =
                sliderObject.GetComponent<SmartSlider>() ??
                sliderObject.GetComponentInChildren<SmartSlider>(true);
            if (inheritedSmartSlider != null)
                DestroyImmediate(inheritedSmartSlider);
            foreach (MenuItemGUI menuItem in sliderObject.GetComponentsInChildren<MenuItemGUI>(true))
                DestroyImmediate(menuItem);
            foreach (UIInput input in sliderObject.GetComponentsInChildren<UIInput>(true))
                input.gameObject.SetActive(false);
            foreach (UILabel label in sliderObject.GetComponentsInChildren<UILabel>(true))
                label.gameObject.SetActive(false);
            foreach (GamepadNavigationItem navigation in
                     sliderObject.GetComponentsInChildren<GamepadNavigationItem>(true))
                DestroyImmediate(navigation);
            foreach (UIEventTrigger trigger in sliderObject.GetComponentsInChildren<UIEventTrigger>(true))
                DestroyImmediate(trigger);

            slider.onChange.Clear();
            slider.onChange.Add(new EventDelegate(onChanged));
            slider.onDragFinished = OnColorSliderDragFinished;
            UIEventListener.Get(slider.gameObject).onPress = OnColorSliderPressed;
            slider.fillDirection = UIProgressBar.FillDirection.LeftToRight;

            UIWidget background = slider.backgroundWidget;
            UIWidget foreground = slider.foregroundWidget;
            if (background != null)
            {
                ClearAllAnchors(background);
                background.pivot = UIWidget.Pivot.Center;
                background.width = PaletteTrackWidth + 4;
                background.height = PaletteTrackHeight + 4;
                background.alpha = 0f;
            }
            if (foreground != null)
            {
                ClearAllAnchors(foreground);
                foreground.pivot = UIWidget.Pivot.Center;
                foreground.width = PaletteTrackWidth;
                foreground.height = PaletteTrackHeight;
                foreground.transform.localPosition = background != null
                    ? slider.transform.InverseTransformPoint(background.transform.position)
                    : Vector3.zero;
                foreground.alpha = 0f;
            }

            Vector3 currentCenter = background != null
                ? _contentLayer.transform.InverseTransformPoint(background.transform.position)
                : _contentLayer.transform.InverseTransformPoint(slider.transform.position);
            sliderObject.transform.localPosition += targetCenter - currentCenter;

            GameObject trackObject = new GameObject(name + "Track");
            trackObject.layer = _uiLayer;
            trackObject.transform.SetParent(slider.transform, false);
            trackObject.transform.localPosition = background != null
                ? slider.transform.InverseTransformPoint(background.transform.position)
                : Vector3.zero;
            track = trackObject.AddComponent<UITexture>();
            track.width = PaletteTrackWidth;
            track.height = PaletteTrackHeight;
            int frameDepth = background != null ? background.depth + 1 : 80;
            UIWidget frame = CreateSliderFrame(
                name + "Frame",
                slider.transform,
                trackObject.transform.localPosition,
                frameDepth);
            track.depth = frame != null ? frame.depth + 1 : frameDepth;

            UIWidget thumbWidget = slider.thumb?.GetComponent<UIWidget>();
            if (thumbWidget != null)
            {
                ClearAllAnchors(thumbWidget);
                thumbWidget.depth = Mathf.Max(thumbWidget.depth, track.depth + 2);
                thumbWidget.transform.localRotation = Quaternion.identity;
            }
            foreach (BoxCollider2D collider in sliderObject.GetComponentsInChildren<BoxCollider2D>(true))
                collider.enabled = collider.gameObject == slider.gameObject;
            BoxCollider2D sliderCollider = slider.GetComponent<BoxCollider2D>();
            if (sliderCollider == null)
                sliderCollider = slider.gameObject.AddComponent<BoxCollider2D>();
            sliderCollider.enabled = true;
            sliderCollider.isTrigger = true;
            sliderCollider.offset = background != null
                ? (Vector2)slider.transform.InverseTransformPoint(background.transform.position)
                : Vector2.zero;
            sliderCollider.size = new Vector2(PaletteTrackWidth + 16f, PaletteTrackHeight + 14f);
            slider.ForceUpdate();
            return slider;
        }

        private UIWidget CreateSliderFrame(
            string name,
            Transform parent,
            Vector3 position,
            int depth)
        {
            GameObject frame = CreateInventorySlotFrame(
                name,
                parent,
                position,
                PaletteTrackWidth,
                PaletteTrackHeight,
                depth,
                4f);
            return frame?.GetComponentInChildren<UIWidget>(true);
        }

        private GameObject CreateInventorySlotFrame(
            string name,
            Transform parent,
            Vector3 position,
            float innerWidth,
            float innerHeight,
            int depth,
            float targetBorder = 3f)
        {
            BaseItemCellGUI sourceCell = _slotTemplate?.GetComponent<BaseItemCellGUI>();
            BaseItemCellElements sourceElements = sourceCell?.x1?.container != null
                ? sourceCell.x1
                : sourceCell?.x2;
            UI2DSprite sourceFrame = sourceElements?.back;
            Sprite sourceSprite = sourceFrame?.sprite2D;
            Texture2D sourceTexture = sourceSprite?.texture;
            if (sourceFrame == null || sourceSprite == null || sourceTexture == null)
                return null;

            GameObject frameObject = new GameObject(name);
            frameObject.layer = _uiLayer;
            frameObject.transform.SetParent(parent, false);
            frameObject.name = name;
            frameObject.transform.localPosition = position;
            frameObject.transform.localRotation = Quaternion.identity;
            frameObject.transform.localScale = Vector3.one;
            try
            {
                Rect sourceRect = sourceSprite.textureRect;
                float sourceBorder = Mathf.Clamp(
                    Mathf.Min(sourceRect.width, sourceRect.height) * 0.1f,
                    3f,
                    10f);
                float outerWidth = innerWidth + targetBorder * 2f;
                float outerHeight = innerHeight + targetBorder * 2f;
                float innerSourceWidth = sourceRect.width - sourceBorder * 2f;
                float innerSourceHeight = sourceRect.height - sourceBorder * 2f;
                if (innerSourceWidth <= 0f || innerSourceHeight <= 0f)
                    throw new InvalidOperationException("source frame is too small to segment");

                float left = -outerWidth * 0.5f;
                float bottom = -outerHeight * 0.5f;
                AddSliderFramePiece(
                    frameObject.transform,
                    "BottomLeft",
                    sourceTexture,
                    new Rect(sourceRect.x, sourceRect.y, sourceBorder, sourceBorder),
                    new Rect(left, bottom, targetBorder, targetBorder),
                    depth,
                    sourceFrame.color);
                AddSliderFramePiece(
                    frameObject.transform,
                    "Bottom",
                    sourceTexture,
                    new Rect(sourceRect.x + sourceBorder, sourceRect.y, innerSourceWidth, sourceBorder),
                    new Rect(left + targetBorder, bottom, innerWidth, targetBorder),
                    depth,
                    sourceFrame.color);
                AddSliderFramePiece(
                    frameObject.transform,
                    "BottomRight",
                    sourceTexture,
                    new Rect(sourceRect.xMax - sourceBorder, sourceRect.y, sourceBorder, sourceBorder),
                    new Rect(left + targetBorder + innerWidth, bottom, targetBorder, targetBorder),
                    depth,
                    sourceFrame.color);
                AddSliderFramePiece(
                    frameObject.transform,
                    "Left",
                    sourceTexture,
                    new Rect(sourceRect.x, sourceRect.y + sourceBorder, sourceBorder, innerSourceHeight),
                    new Rect(left, bottom + targetBorder, targetBorder, innerHeight),
                    depth,
                    sourceFrame.color);
                AddSliderFramePiece(
                    frameObject.transform,
                    "Right",
                    sourceTexture,
                    new Rect(sourceRect.xMax - sourceBorder, sourceRect.y + sourceBorder, sourceBorder, innerSourceHeight),
                    new Rect(left + targetBorder + innerWidth, bottom + targetBorder, targetBorder, innerHeight),
                    depth,
                    sourceFrame.color);
                AddSliderFramePiece(
                    frameObject.transform,
                    "TopLeft",
                    sourceTexture,
                    new Rect(sourceRect.x, sourceRect.yMax - sourceBorder, sourceBorder, sourceBorder),
                    new Rect(left, bottom + targetBorder + innerHeight, targetBorder, targetBorder),
                    depth,
                    sourceFrame.color);
                AddSliderFramePiece(
                    frameObject.transform,
                    "Top",
                    sourceTexture,
                    new Rect(sourceRect.x + sourceBorder, sourceRect.yMax - sourceBorder, innerSourceWidth, sourceBorder),
                    new Rect(left + targetBorder, bottom + targetBorder + innerHeight, innerWidth, targetBorder),
                    depth,
                    sourceFrame.color);
                AddSliderFramePiece(
                    frameObject.transform,
                    "TopRight",
                    sourceTexture,
                    new Rect(sourceRect.xMax - sourceBorder, sourceRect.yMax - sourceBorder, sourceBorder, sourceBorder),
                    new Rect(left + targetBorder + innerWidth, bottom + targetBorder + innerHeight, targetBorder, targetBorder),
                    depth,
                    sourceFrame.color);
                return frameObject;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[CharacterCustomization] Could not segment slider frame: {ex.Message}");
                DestroyImmediate(frameObject);
                return null;
            }
        }

        private UITexture AddSliderFramePiece(
            Transform parent,
            string name,
            Texture2D texture,
            Rect sourcePixels,
            Rect targetRect,
            int depth,
            Color color)
        {
            GameObject pieceObject = new GameObject("WardrobeSliderFrame_" + name);
            pieceObject.layer = _uiLayer;
            pieceObject.transform.SetParent(parent, false);
            pieceObject.transform.localPosition = targetRect.center;

            UITexture piece = pieceObject.AddComponent<UITexture>();
            piece.pivot = UIWidget.Pivot.Center;
            piece.mainTexture = texture;
            piece.uvRect = new Rect(
                sourcePixels.x / texture.width,
                sourcePixels.y / texture.height,
                sourcePixels.width / texture.width,
                sourcePixels.height / texture.height);
            piece.width = Mathf.Max(1, Mathf.RoundToInt(targetRect.width));
            piece.height = Mathf.Max(1, Mathf.RoundToInt(targetRect.height));
            piece.depth = depth;
            piece.color = color;
            piece.MarkAsChanged();
            return piece;
        }

        private void BuildCurrentColor(GameObject headerTemplate)
        {
            _currentNameLabel = CreateHeader(
                headerTemplate,
                "Current",
                GetSideHeaderCenter(),
                _headerY - 235f,
                GetSideHeaderWidth());

            ColorSlot current = CreateColorSlot(
                "CurrentColorCell",
                new Vector3(_rightX, _headerY - 285f, 0f));
            _currentSwatch = current.Swatch;
            if (current.Selection != null)
                current.Selection.gameObject.SetActive(false);
            Collider2D collider = current.Root.GetComponent<Collider2D>();
            if (collider != null)
                collider.enabled = false;
            UIEventListener listener = current.Root.GetComponent<UIEventListener>();
            if (listener != null)
                DestroyImmediate(listener);
        }

        private bool BuildButtons()
        {
            float spacing = Mathf.Abs(_rightX) * 0.8f;
            return CreateRedButton("Back", -spacing * 1.5f, () => CloseWithoutSaving(true)) &&
                   CreateRedButton("Reset", -spacing * 0.5f, ResetToDefaults) &&
                   CreateRedButton("Randomize", spacing * 0.5f, Randomize) &&
                   CreateRedButton("Apply", spacing * 1.5f, ApplyChanges);
        }

        private bool CreateRedButton(string text, float x, Action callback)
        {
            return CreateRedButtonObject(
                       text,
                       x,
                       _headerY - FooterOffsetFromHeader,
                       _contentLayer.transform,
                       callback,
                       FooterButtonDepth) != null;
        }

        private GameObject CreateRedButtonObject(
            string text,
            float x,
            float y,
            Transform parent,
            Action callback,
            int minimumDepth)
        {
            DialogButtonGUI template =
                GUIElements.me?.dialog?.GetComponentInChildren<DialogButtonGUI>(true) ??
                GUIElements.me?.item_count?.GetComponentInChildren<DialogButtonGUI>(true);
            if (template == null)
            {
                CoopMod.Logger.LogError(
                    $"[CharacterCustomization] Cannot create '{text}': native red button template is unavailable");
                return null;
            }

            GameObject root = Instantiate(template.gameObject, parent);
            root.name = "WardrobeButton_" + text;
            ClearExternalAnchors(root);
            root.transform.localScale = Vector3.one;
            StripLocalization(root);
            foreach (UIPanel nestedPanel in root.GetComponentsInChildren<UIPanel>(true))
                DestroyImmediate(nestedPanel);
            UIWidget background = FindButtonBackground(root);
            if (background == null)
            {
                CoopMod.Logger.LogError(
                    $"[CharacterCustomization] Native button '{text}' has no visible background");
                DestroyImmediate(root);
                return null;
            }
            root.transform.localPosition = new Vector3(
                x,
                y,
                0f);
            if (background != null)
            {
                background.enabled = true;
                background.alpha = 1f;
                background.gameObject.SetActive(true);
            }
            UILabel label = root.GetComponentInChildren<UILabel>(true);
            if (label == null)
            {
                CoopMod.Logger.LogError(
                    $"[CharacterCustomization] Native button '{text}' has no label");
                DestroyImmediate(root);
                return null;
            }
            else
            {
                label.text = text;
                label.MarkAsChanged();
            }
            // The native footer contains several widgets up through FooterBarDepth.
            // Preserve the button's internal depth ordering, but move its lowest
            // widget above the bar so the red frame is not hidden behind it.
            SetMinimumWidgetDepth(root, minimumDepth);
            foreach (DialogButtonGUI oldButton in root.GetComponentsInChildren<DialogButtonGUI>(true))
                DestroyImmediate(oldButton);
            UIButton button = root.GetComponentInChildren<UIButton>(true);
            if (button != null)
                button.onClick.Clear();
            Collider2D eventCollider = FindEnabledCollider(root);
            if (eventCollider == null)
            {
                CoopMod.Logger.LogError(
                    $"[CharacterCustomization] Native button '{text}' has no enabled collider");
                DestroyImmediate(root);
                return null;
            }
            GameObject eventTarget = eventCollider != null
                ? eventCollider.gameObject
                : button != null ? button.gameObject : root;
            UIEventListener listener = UIEventListener.Get(eventTarget);
            listener.onClick = go =>
            {
                Sounds.OnGUIClick();
                callback();
            };
            root.SetActive(true);
            return root;
        }

        private void BuildFooterBar(GameObject template)
        {
            float targetY = _headerY - FooterOffsetFromHeader;
            UIWidget sourceBackground = FindButtonBackground(template);
            if (sourceBackground == null)
            {
                CoopMod.Logger.LogWarning(
                    "[CharacterCustomization] Native footer has no resizable background");
                return;
            }

            GameObject footer = Instantiate(sourceBackground.gameObject, _contentLayer.transform);
            footer.name = "WardrobeFooterBar";
            ClearExternalAnchors(footer);
            footer.transform.localPosition = new Vector3(0f, targetY, 0f);
            footer.transform.localScale = Vector3.one;
            StripLocalization(footer);
            foreach (UIPanel nestedPanel in footer.GetComponentsInChildren<UIPanel>(true))
                DestroyImmediate(nestedPanel);
            foreach (UILabel label in footer.GetComponentsInChildren<UILabel>(true))
                label.gameObject.SetActive(false);
            foreach (Collider collider in footer.GetComponentsInChildren<Collider>(true))
                DestroyImmediate(collider);
            foreach (Collider2D collider in footer.GetComponentsInChildren<Collider2D>(true))
                DestroyImmediate(collider);
            HideFooterPayload(footer);

            UIWidget background = footer.GetComponent<UIWidget>() ?? FindButtonBackground(footer);
            ClearAllAnchors(background);
            background.pivot = UIWidget.Pivot.Center;
            background.width = Mathf.RoundToInt(
                Mathf.Abs(_rightX) * 3f * FooterWidthScale);
            background.height = Mathf.RoundToInt(
                Mathf.Abs(sourceBackground.height) * FooterHeightScale);
            foreach (UIWidget widget in footer.GetComponentsInChildren<UIWidget>(true))
                widget.depth = FooterBarDepth;
            footer.SetActive(true);
            background.MarkAsChanged();
            if (TryGetWidgetBounds(footer, out Bounds bounds))
            {
                footer.transform.localPosition += new Vector3(
                    -bounds.center.x,
                    targetY - bounds.center.y,
                    0f);
            }
        }

        private static void HideFooterPayload(GameObject root)
        {
            foreach (UIWidget widget in root.GetComponentsInChildren<UIWidget>(true))
            {
                if (widget == null || widget is UILabel)
                    continue;
                string name = widget.name.ToLowerInvariant();
                bool isIcon = name.Contains("coin") || name.Contains("icon");
                bool isSmallCurrencyWidget =
                    (name.Contains("money") || name.Contains("currency") || name.Contains("debt")) &&
                    Mathf.Abs(widget.width) <= 64 && Mathf.Abs(widget.height) <= 64;
                if (isIcon || isSmallCurrencyWidget)
                    widget.gameObject.SetActive(false);
            }
        }

        private bool TryGetWidgetBounds(GameObject root, out Bounds result)
        {
            result = default(Bounds);
            bool found = false;
            foreach (UIWidget widget in root.GetComponentsInChildren<UIWidget>(true))
            {
                if (widget == null || !widget.enabled || !widget.gameObject.activeSelf)
                    continue;
                foreach (Vector3 corner in widget.worldCorners)
                {
                    Vector3 localCorner = _contentLayer.transform.InverseTransformPoint(corner);
                    if (!found)
                    {
                        result = new Bounds(localCorner, Vector3.zero);
                        found = true;
                    }
                    else
                    {
                        result.Encapsulate(localCorner);
                    }
                }
            }
            return found;
        }

        private void WireCloseButton()
        {
            foreach (Transform child in GetComponentsInChildren<Transform>(true))
            {
                string name = child.name ?? string.Empty;
                if (name.IndexOf("close", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (child.GetComponentInChildren<Collider2D>(true) == null &&
                    child.GetComponentInChildren<UIButton>(true) == null)
                    continue;

                UIButton button = child.GetComponentInChildren<UIButton>(true);
                if (button != null)
                    button.onClick.Clear();
                SetMinimumWidgetDepth(child.gameObject, PresetManagerDepth + 20);
                Collider2D collider = child.GetComponentInChildren<Collider2D>(true);
                GameObject eventTarget = button != null
                    ? button.gameObject
                    : collider != null ? collider.gameObject : child.gameObject;
                UIEventListener.Get(eventTarget).onClick = go => CloseWithoutSaving(true);
            }
        }

        private UILabel CreateLabel(
            string name, string text, Vector3 position, int size,
            NGUIText.Alignment alignment, Transform parent = null)
        {
            GameObject root = new GameObject(name);
            root.layer = _uiLayer;
            root.transform.SetParent(parent ?? _contentLayer.transform, false);
            root.transform.localPosition = position;
            UILabel label = root.AddComponent<UILabel>();
            label.bitmapFont = FindReferenceFont();
            label.text = text;
            label.fontSize = size;
            label.alignment = alignment;
            label.pivot = UIWidget.Pivot.Center;
            label.overflowMethod = UILabel.Overflow.ShrinkContent;
            label.width = 190;
            label.height = 28;
            label.depth = 95;
            label.MarkAsChanged();
            return label;
        }

        private void SelectCategory(int index)
        {
            if (index < 0 || index >= Categories.Length)
                return;
            _selectedCategory = index;
            RefreshAll();
            ReframeLivePreviewCamera();
            Sounds.OnGUITabClick();
        }

        private void ReframeLivePreviewCamera()
        {
            if (_livePreviewCamera == null)
                return;

            FrameLivePreviewCamera(GetPreviewSourceCamera(), GetPreviewKeeperRoot());
            _livePreviewDirty = true;
        }

        private void SelectRecentColor(int slotIndex)
        {
            int[] recent = CosmeticsSync.Instance?.GetRecentColors(CurrentCategory) ?? new int[0];
            if (_draft == null || slotIndex < 0 || slotIndex >= recent.Length)
                return;
            _draft.SetSelection(CurrentCategory, recent[slotIndex]);
            PreviewAndRefresh();
        }

        private void LoadSavedPresets()
        {
            _savedPresets.Clear();
            PlayerCosmetics[] presets =
                CosmeticsSync.Instance?.GetSavedPresetSlots() ?? new PlayerCosmetics[0];
            for (int index = 0; index < CosmeticsSync.SavedPresetSlotCount; index++)
            {
                PlayerCosmetics preset = index < presets.Length ? presets[index] : null;
                _savedPresets.Add(preset?.Clamped());
            }
            _presetIndex = 0;
            if (_original != null)
            {
                for (int index = 0; index < _savedPresets.Count; index++)
                {
                    if (_savedPresets[index] == null ||
                        !_savedPresets[index].Equals(_original))
                        continue;
                    _presetIndex = index;
                    break;
                }
            }
            RefreshPresetControls();
        }

        private void CycleSavedPreset(int direction)
        {
            if (_savedPresets.Count <= 1)
                return;

            int index =
                (_presetIndex + direction + _savedPresets.Count) % _savedPresets.Count;
            SelectSavedPreset(index);
        }

        private void SelectSavedPreset(int index)
        {
            if (index < 0 || index >= _savedPresets.Count)
                return;

            _presetIndex = index;
            PlayerCosmetics selectedPreset = GetFocusedSavedPreset();
            if (selectedPreset != null)
            {
                _draft = selectedPreset.Clone();
                PreviewAndRefresh();
            }
            _presetPreviewDirty = true;
            RefreshPresetControls();
        }

        private void RefreshPresetControls()
        {
            bool hasSlots = _savedPresets.Count > 0;
            bool canCycle = _savedPresets.Count > 1;
            bool focusedSlotOccupied = GetFocusedSavedPreset() != null;
            SetPresetArrowState(_presetPreviousArrow, hasSlots, canCycle);
            SetPresetArrowState(_presetNextArrow, hasSlots, canCycle);
            SetButtonState(_presetSaveButton, hasSlots, hasSlots);
            SetButtonState(_presetEditButton, hasSlots, hasSlots);
            SetPresetArrowState(
                _presetManagerPreviousArrow,
                _presetManagerVisible && hasSlots,
                canCycle);
            SetPresetArrowState(
                _presetManagerNextArrow,
                _presetManagerVisible && hasSlots,
                canCycle);
            SetButtonState(
                _presetManagerDeleteButton,
                _presetManagerVisible && focusedSlotOccupied,
                focusedSlotOccupied);
            bool showPresetPreview = focusedSlotOccupied &&
                                     _presetPreviewRenderTexture != null;
            if (_presetPreviewWidget != null)
            {
                _presetPreviewWidget.enabled = showPresetPreview;
                _presetPreviewWidget.MarkAsChanged();
            }
            if (_presetManagerPreviewWidget != null)
            {
                _presetManagerPreviewWidget.enabled =
                    _presetManagerVisible && showPresetPreview;
                _presetManagerPreviewWidget.MarkAsChanged();
            }
            foreach (PresetManagerSlotVisual slot in _presetManagerSlots)
            {
                bool occupied = slot.Index >= 0 && slot.Index < _savedPresets.Count &&
                                _savedPresets[slot.Index] != null;
                if (slot.Preview != null)
                {
                    slot.Preview.enabled = _presetManagerVisible && occupied &&
                                           slot.RenderTexture != null;
                    slot.Preview.MarkAsChanged();
                }
                RefreshPresetManagerSlotFrames(slot.Index, false);
            }
            string slotText = hasSlots
                ? $"Slot {_presetIndex + 1} of {_savedPresets.Count}" +
                  (focusedSlotOccupied ? " - Saved" : " - Empty")
                : "No preset slots";
            if (_presetSlotStatusLabel != null)
            {
                _presetSlotStatusLabel.text = slotText;
                _presetSlotStatusLabel.MarkAsChanged();
            }
            if (_presetManagerStatusLabel != null)
            {
                _presetManagerStatusLabel.text = slotText;
                _presetManagerStatusLabel.MarkAsChanged();
            }
        }

        private void RefreshPresetManagerSlotFrames(int slotIndex, bool hovered)
        {
            PresetManagerSlotVisual slot = _presetManagerSlots.Find(
                candidate => candidate.Index == slotIndex);
            if (slot == null)
                return;

            bool selected = slot.Index == _presetIndex;
            Color highlight = selected
                ? new Color(0.65f, 1f, 0.45f, 1f)
                : new Color(0.82f, 0.9f, 1f, 1f);
            float blend = selected ? 0.55f : hovered ? 0.28f : 0f;
            for (int index = 0; index < slot.FramePieces.Count; index++)
            {
                UIWidget piece = slot.FramePieces[index];
                if (piece == null)
                    continue;
                Color original = index < slot.FrameColors.Count
                    ? slot.FrameColors[index]
                    : Color.white;
                piece.color = Color.Lerp(original, highlight, blend);
                piece.MarkAsChanged();
            }
        }

        private PlayerCosmetics GetFocusedSavedPreset()
        {
            return _presetIndex >= 0 && _presetIndex < _savedPresets.Count
                ? _savedPresets[_presetIndex]
                : null;
        }

        private int CountSavedPresets()
        {
            int count = 0;
            foreach (PlayerCosmetics preset in _savedPresets)
            {
                if (preset != null)
                    count++;
            }
            return count;
        }

        private void RequestSaveFocusedPreset()
        {
            if (_saveConfirmationOpen || _draft == null || _presetIndex < 0 ||
                _presetIndex >= _savedPresets.Count)
            {
                return;
            }

            int slotIndex = _presetIndex;
            PlayerCosmetics cosmetics = _draft.Clone();
            if (_savedPresets[slotIndex] == null)
            {
                SaveFocusedPreset(slotIndex, cosmetics);
                return;
            }

            DialogGUI dialog = GUIElements.me?.dialog;
            if (dialog == null)
            {
                CoopMod.Logger.LogWarning(
                    "[CharacterCustomization] DialogGUI unavailable; saved keeper overwrite cannot be confirmed");
                return;
            }

            _saveConfirmationOpen = true;
            GJCommons.VoidDelegate clearConfirmation = () =>
                _saveConfirmationOpen = false;
            try
            {
                dialog.OpenYesNo(
                    "Overwrite this saved keeper?",
                    new GJCommons.VoidDelegate(() => SaveFocusedPreset(slotIndex, cosmetics)),
                    clearConfirmation,
                    clearConfirmation);
            }
            catch (Exception ex)
            {
                _saveConfirmationOpen = false;
                CoopMod.Logger.LogError(
                    $"[CharacterCustomization] Failed to open saved keeper overwrite confirmation: {ex}");
            }
        }

        private void SaveFocusedPreset(int slotIndex, PlayerCosmetics cosmetics)
        {
            if (cosmetics == null || slotIndex < 0 || slotIndex >= _savedPresets.Count)
                return;

            PlayerCosmetics saved = cosmetics.Clamped();
            if (CosmeticsSync.Instance?.SaveSavedPresetSlot(slotIndex, saved) != true)
            {
                CoopMod.Logger.LogWarning(
                    "[CharacterCustomization] Saved keeper slot could not be written");
                return;
            }

            _savedPresets[slotIndex] = saved.Clone();
            RefreshPresetControls();
            if (_presetManagerVisible)
                RenderPresetManagerSlots();
            _presetPreviewDirty = true;
            CoopMod.Logger.LogInfo(
                $"[CharacterCustomization] Saved keeper preset to slot {slotIndex + 1}");
        }

        private static void SetButtonState(GameObject root, bool visible, bool enabled)
        {
            if (root == null)
                return;
            root.SetActive(visible);
            foreach (UIButton button in root.GetComponentsInChildren<UIButton>(true))
                button.isEnabled = enabled;
            foreach (Collider2D collider in root.GetComponentsInChildren<Collider2D>(true))
                collider.enabled = enabled;
        }

        private void RequestDeleteFocusedPreset()
        {
            if (_deleteConfirmationOpen || _presetIndex < 0 ||
                _presetIndex >= _savedPresets.Count || GetFocusedSavedPreset() == null)
                return;

            DialogGUI dialog = GUIElements.me?.dialog;
            if (dialog == null)
            {
                CoopMod.Logger.LogWarning(
                    "[CharacterCustomization] DialogGUI unavailable; saved keeper deletion cannot be confirmed");
                return;
            }

            int slotIndex = _presetIndex;
            _deleteConfirmationOpen = true;
            GJCommons.VoidDelegate clearConfirmation = () =>
                _deleteConfirmationOpen = false;
            try
            {
                dialog.OpenYesNo(
                    "Delete this saved keeper?",
                    new GJCommons.VoidDelegate(() => DeleteSavedPreset(slotIndex)),
                    clearConfirmation,
                    clearConfirmation);
            }
            catch (Exception ex)
            {
                _deleteConfirmationOpen = false;
                CoopMod.Logger.LogError(
                    $"[CharacterCustomization] Failed to open saved keeper deletion confirmation: {ex}");
            }
        }

        private void DeleteSavedPreset(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= _savedPresets.Count)
                return;

            if (CosmeticsSync.Instance?.DeleteSavedPresetSlot(slotIndex) != true)
            {
                CoopMod.Logger.LogWarning(
                    "[CharacterCustomization] Saved keeper could not be deleted");
                return;
            }

            _savedPresets[slotIndex] = null;
            RefreshPresetControls();
            if (_presetManagerVisible)
                RenderPresetManagerSlots();
            _presetPreviewDirty = true;
            CoopMod.Logger.LogInfo(
                $"[CharacterCustomization] Deleted saved keeper preset from slot " +
                $"{slotIndex + 1}; {CountSavedPresets()} remain");
        }

        private void ShowPresetManager()
        {
            if (_presetManagerVisible || _presetManagerRoot == null)
            {
                return;
            }

            _editorViewStates.Clear();
            foreach (Transform child in _contentLayer.transform)
            {
                GameObject childObject = child.gameObject;
                if (childObject == _presetManagerRoot)
                    continue;
                RememberAndHideEditorObject(childObject);
            }
            foreach (TabVisual tab in _tabs)
                RememberAndHideEditorObject(tab.Root);

            _presetManagerVisible = true;
            _presetManagerRoot.SetActive(true);
            if (_presetManagerPreviewWidget != null)
            {
                _presetManagerPreviewWidget.mainTexture = _presetPreviewRenderTexture;
                _presetManagerPreviewWidget.MarkAsChanged();
            }
            RefreshPresetControls();
            RenderPresetManagerSlots();
            _presetPreviewDirty = true;
        }

        private void RememberAndHideEditorObject(GameObject target)
        {
            if (target == null || _editorViewStates.ContainsKey(target))
                return;
            _editorViewStates.Add(target, target.activeSelf);
            target.SetActive(false);
        }

        private void RestoreEditorView()
        {
            if (_presetManagerRoot != null)
                _presetManagerRoot.SetActive(false);

            if (!_presetManagerVisible)
                return;

            _presetManagerVisible = false;
            foreach (KeyValuePair<GameObject, bool> entry in _editorViewStates)
            {
                if (entry.Key != null)
                    entry.Key.SetActive(entry.Value);
            }
            _editorViewStates.Clear();
            RefreshAll();
            RefreshPresetControls();
            _presetPreviewDirty = true;
        }

        private static void SetPresetArrowState(
            GameObject arrow,
            bool visible,
            bool enabled)
        {
            if (arrow == null)
                return;
            arrow.SetActive(visible);
            foreach (UIButton button in arrow.GetComponentsInChildren<UIButton>(true))
                button.isEnabled = enabled;
            foreach (Collider2D collider in arrow.GetComponentsInChildren<Collider2D>(true))
                collider.enabled = enabled;
        }

        private void OnHueSliderChanged()
        {
            if (_updatingSlider || _draft == null || _hueSlider == null)
                return;
            int count = PlayerCosmetics.GetOptionCount(CurrentCategory);
            int value = Mathf.Clamp(
                Mathf.RoundToInt(_hueSlider.value * (count - 1)),
                0,
                count - 1);
            _draft.SetColor(CurrentCategory, value);
            RefreshColorDisplays(false);
            QueuePreview();
            if (!Sounds.WasAnySoundPlayedThisFrame())
                Sounds.OnGUIClick();
        }

        private void OnToneSliderChanged()
        {
            if (_updatingSlider || _draft == null || _toneSlider == null)
                return;
            int value = Mathf.Clamp(
                Mathf.RoundToInt(
                    _toneSlider.value * (PlayerCosmetics.MAX_TONE_OPTIONS - 1)),
                0,
                PlayerCosmetics.MAX_TONE_OPTIONS - 1);
            _draft.SetTone(CurrentCategory, value);
            RefreshColorDisplays(false);
            QueuePreview();
            if (!Sounds.WasAnySoundPlayedThisFrame())
                Sounds.OnGUIClick();
        }

        private void OnColorSliderPressed(GameObject slider, bool pressed)
        {
            _sliderDragging = pressed;
            if (!pressed)
                FlushPendingPreview(true);
        }

        private void OnColorSliderDragFinished()
        {
            _sliderDragging = false;
            FlushPendingPreview(true);
        }

        private void CycleColor(int direction)
        {
            if (_draft == null)
                return;
            int count = PlayerCosmetics.GetOptionCount(CurrentCategory);
            int value = (_draft.GetColor(CurrentCategory) + direction + count) % count;
            _draft.SetColor(CurrentCategory, value);
            PreviewAndRefresh();
        }

        private void Randomize()
        {
            _draft = PlayerCosmetics.Random();
            PreviewAndRefresh();
        }

        private void ResetToDefaults()
        {
            _draft = PlayerCosmetics.Default;
            PreviewAndRefresh();
        }

        private void PreviewAndRefresh(bool configureSlider = true)
        {
            _previewPending = false;
            PreviewDraft();
            if (_presetManagerVisible)
                RefreshPresetControls();
            else
                RefreshColorDisplays(configureSlider);
        }

        private void QueuePreview()
        {
            _previewPending = true;
            _previewDueAt = Time.unscaledTime + SliderPreviewDebounceSeconds;
        }

        private void FlushPendingPreview(bool force = false)
        {
            if (!_previewPending || _draft == null)
                return;
            if (_sliderDragging && !force)
                return;
            if (!force && Time.unscaledTime < _previewDueAt)
                return;

            _previewPending = false;
            PreviewDraft();
        }

        private bool PreviewDraft()
        {
            bool succeeded = ApplyPreviewCosmetics(_draft);
            _lastSuccessfulPreview = succeeded ? _draft.Clone() : null;
            if (succeeded)
            {
                _livePreviewDirty = true;
                _presetPreviewDirty = true;
            }
            return succeeded;
        }

        private bool ApplyPreviewCosmetics(PlayerCosmetics cosmetics)
        {
            if (cosmetics == null)
                return false;

            if (!_openedFromLobby)
                return CosmeticsSync.Instance?.PreviewLocalCosmetics(cosmetics) == true;

            if (_isolatedPreviewDriver == null)
                return false;

            _isolatedPreviewDriver.SetCosmetics(cosmetics);
            return _isolatedPreviewDriver.LastApplySucceeded;
        }

        private void RefreshAll()
        {
            RefreshTabs();
            RefreshHeaders();
            RefreshColorDisplays(true);
        }

        private void RefreshTabs()
        {
            for (int index = 0; index < _tabs.Count; index++)
            {
                bool selected = index == _selectedCategory;
                TabVisual tab = _tabs[index];
                if (tab.Button != null)
                {
                    tab.Button.defaultColor = selected ? tab.Button.pressed : tab.DefaultColor;
                    tab.Button.enabled = !selected;
                }
                tab.ActiveDecoration?.SetActive(selected);
            }
        }

        private void RefreshHeaders()
        {
            string category = CategoryNames[_selectedCategory];
            if (_leftHeaderLabel != null)
            {
                _leftHeaderLabel.text = "Saved " + category.ToLowerInvariant() + " colors";
                _leftHeaderLabel.MarkAsChanged();
            }
            if (_rightHeaderLabel != null)
            {
                _rightHeaderLabel.text = category + " color";
                _rightHeaderLabel.MarkAsChanged();
            }
        }

        private void RefreshColorDisplays(bool configureSlider)
        {
            if (_draft == null)
                return;

            int currentColor = _draft.GetColor(CurrentCategory);
            int currentTone = _draft.GetTone(CurrentCategory);
            int currentSelection = _draft.GetSelection(CurrentCategory);
            int[] recent = CosmeticsSync.Instance?.GetRecentColors(CurrentCategory) ?? new int[0];
            for (int index = 0; index < _recentSlots.Count; index++)
            {
                ColorSlot slot = _recentSlots[index];
                bool visible = index < recent.Length;
                slot.Root.SetActive(visible);
                if (!visible)
                    continue;
                slot.Value = recent[index];
                slot.Swatch.color =
                    PlayerCosmetics.GetResolvedColor(CurrentCategory, slot.Value);
                slot.Swatch.MarkAsChanged();
                if (slot.Selection != null)
                    slot.Selection.gameObject.SetActive(slot.Value == currentSelection);
            }

            if (_currentSwatch != null)
            {
                _currentSwatch.color = PlayerCosmetics.GetResolvedColor(
                    CurrentCategory,
                    currentColor,
                    currentTone);
                _currentSwatch.MarkAsChanged();
            }
            if (_currentNameLabel != null)
            {
                _currentNameLabel.text =
                    "Current: " + ColorNames[_selectedCategory][currentColor] +
                    " / " + ToneNames[currentTone];
                _currentNameLabel.MarkAsChanged();
            }

            UpdateColorTracks(currentColor);
            if (configureSlider && _hueSlider != null && _toneSlider != null)
            {
                _updatingSlider = true;
                try
                {
                    int colorCount = PlayerCosmetics.GetOptionCount(CurrentCategory);
                    _hueSlider.numberOfSteps = colorCount;
                    _hueSlider.Set(
                        colorCount <= 1 ? 0f : (float)currentColor / (colorCount - 1),
                        false);
                    _toneSlider.numberOfSteps = PlayerCosmetics.MAX_TONE_OPTIONS;
                    _toneSlider.Set(
                        (float)currentTone / (PlayerCosmetics.MAX_TONE_OPTIONS - 1),
                        false);
                    _hueSlider.ForceUpdate();
                    _toneSlider.ForceUpdate();
                }
                finally
                {
                    _updatingSlider = false;
                }
            }
        }

        private void UpdateColorTracks(int currentColor)
        {
            if (_hueTrack == null || _toneTrack == null)
                return;
            if (_hueTrackTexture != null)
                Destroy(_hueTrackTexture);
            if (_toneTrackTexture != null)
                Destroy(_toneTrackTexture);

            int colorCount = PlayerCosmetics.GetOptionCount(CurrentCategory);
            _hueTrackTexture = BuildTrackTexture(
                "WardrobeHueTrack",
                colorCount,
                value => PlayerCosmetics.GetPalette(CurrentCategory)[value]);
            _toneTrackTexture = BuildTrackTexture(
                "WardrobeToneTrack",
                PlayerCosmetics.MAX_TONE_OPTIONS,
                value => PlayerCosmetics.GetResolvedColor(
                    CurrentCategory,
                    currentColor,
                    value));
            _hueTrack.mainTexture = _hueTrackTexture;
            _toneTrack.mainTexture = _toneTrackTexture;
            _hueTrack.MarkAsChanged();
            _toneTrack.MarkAsChanged();
        }

        private static Texture2D BuildTrackTexture(
            string name,
            int stepCount,
            Func<int, Color> getColor)
        {
            Texture2D texture = new Texture2D(
                PaletteTrackWidth,
                PaletteTrackHeight,
                TextureFormat.RGBA32,
                false)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            for (int x = 0; x < PaletteTrackWidth; x++)
            {
                int value = Mathf.Min(stepCount - 1, x * stepCount / PaletteTrackWidth);
                Color color = getColor(value);
                for (int y = 0; y < PaletteTrackHeight; y++)
                    texture.SetPixel(x, y, color);
            }
            texture.Apply(false, false);
            return texture;
        }

        private void ApplyChanges()
        {
            if (_draft == null)
                return;
            CosmeticsSync sync = CosmeticsSync.Instance;
            _previewPending = false;
            bool alreadyPreviewed = SameCosmetics(_lastSuccessfulPreview, _draft);
            if (sync == null || (!alreadyPreviewed && !PreviewDraft()))
            {
                CoopMod.Logger.LogWarning(
                    "[CharacterCustomization] Apply was blocked because the selected colors " +
                    "could not be rendered");
                return;
            }

            sync.CommitPreviewedLocalCosmetics(_draft.Clone());
            _original = _draft.Clone();
            _lastSuccessfulPreview = _draft.Clone();
            RefreshColorDisplays(true);
            CoopMod.Logger.LogInfo($"[CharacterCustomization] Applied {_draft}");
        }

        private void RestoreOriginalPreview()
        {
            _previewPending = false;
            _lastSuccessfulPreview = null;
            if (_original != null)
                ApplyPreviewCosmetics(_original);
        }

        private void CloseWithoutSaving(bool playSound)
        {
            RestoreOriginalPreview();
            CloseInternal(playSound);
        }

        private void CloseInternal(bool playSound)
        {
            if (!is_shown)
                return;
            _closing = true;
            try
            {
                Hide(playSound);
            }
            finally
            {
                _closing = false;
                _original = null;
                _draft = null;
                _lastSuccessfulPreview = null;
                _previewPending = false;
                _deleteConfirmationOpen = false;
                _saveConfirmationOpen = false;
                _sliderDragging = false;
                StopPresetPreview();
                StopLivePlayerPreview();
                StopIsolatedKeeperPreview();
                _openedFromLobby = false;
                _suppressLobbyReturn = false;
            }
        }

        private static bool SameCosmetics(PlayerCosmetics left, PlayerCosmetics right)
        {
            return left != null && left.Equals(right);
        }

        private CosmeticCategory CurrentCategory => Categories[_selectedCategory];

        private bool IsSlotSelected(ColorSlot slot)
        {
            return _draft != null && slot.Value == _draft.GetSelection(CurrentCategory);
        }

        private void BuildLivePreviewSurface()
        {
            float top = _headerY - 24f;
            float bottom = _bottomY + LivePreviewBottomOffset;
            float width = Mathf.Max(
                120f,
                GetKeeperHeaderWidth() -
                (LivePreviewFrameBorder + LivePreviewFrameColumnInset) * 2f);
            float height = Mathf.Max(140f, top - bottom);

            GameObject previewObject = new GameObject("WardrobeLiveKeeperView");
            previewObject.layer = _uiLayer;
            previewObject.transform.SetParent(_contentLayer.transform, false);
            previewObject.transform.localPosition = new Vector3(0f, (top + bottom) * 0.5f, 0f);
            _livePreviewWidget = previewObject.AddComponent<UITexture>();
            _livePreviewWidget.pivot = UIWidget.Pivot.Center;
            _livePreviewWidget.width = Mathf.RoundToInt(width);
            _livePreviewWidget.height = Mathf.RoundToInt(height);
            _livePreviewWidget.depth = LivePreviewDepth;
            _livePreviewWidget.color = Color.white;
            _livePreviewWidget.enabled = false;
        }

        private void BuildPresetCarousel()
        {
            float portraitBottom =
                _bottomY + LivePreviewBottomOffset - LivePreviewFrameBorder;
            float footerTop = _headerY - FooterOffsetFromHeader + 22f;
            float presetCarouselBaseY = (portraitBottom + footerTop) * 0.5f;
            _presetPreviewY = presetCarouselBaseY + PresetCarouselVerticalOffset;

            GameObject previewObject = new GameObject("WardrobeSavedKeeperView");
            previewObject.layer = _uiLayer;
            previewObject.transform.SetParent(_contentLayer.transform, false);
            previewObject.transform.localPosition =
                new Vector3(0f, _presetPreviewY, 0f);
            _presetPreviewWidget = previewObject.AddComponent<UITexture>();
            _presetPreviewWidget.pivot = UIWidget.Pivot.Center;
            _presetPreviewWidget.width = PresetPreviewWidth;
            _presetPreviewWidget.height = PresetPreviewHeight;
            _presetPreviewWidget.depth = LivePreviewDepth;
            _presetPreviewWidget.color = Color.white;
            _presetPreviewWidget.enabled = false;

            _presetSlotStatusLabel = CreateLabel(
                "WardrobePresetSlotStatus",
                $"Slot 1 of {CosmeticsSync.SavedPresetSlotCount} - Empty",
                new Vector3(
                    0f,
                    _presetPreviewY + PresetPreviewHeight * 0.5f + 8f,
                    0f),
                14,
                NGUIText.Alignment.Center,
                _contentLayer.transform);
            _presetSlotStatusLabel.depth = LivePreviewDepth + 5;

            float footerY = _headerY - FooterOffsetFromHeader;
            float editY = Mathf.Max(
                footerY + 44f,
                presetCarouselBaseY - PresetPreviewHeight * 0.5f - 8f);
            _presetSaveButton = CreateRedButtonObject(
                "Save",
                0f,
                editY,
                _contentLayer.transform,
                RequestSaveFocusedPreset,
                LivePreviewDepth + 5);
            _presetEditButton = CreateRedButtonObject(
                "Edit",
                0f,
                editY + 32f,
                _contentLayer.transform,
                ShowPresetManager,
                LivePreviewDepth + 5);

            if (!TryGetPresetArrowTemplates(
                    out UIButton previousTemplate,
                    out UIButton nextTemplate))
            {
                CoopMod.Logger.LogWarning(
                    "[CharacterCustomization] Native preset carousel arrows are unavailable");
                return;
            }

            float arrowOffset = PresetPreviewWidth * 0.5f + 12f;
            _presetPreviousArrow = CreatePresetArrow(
                previousTemplate,
                "WardrobePresetPrevious",
                -arrowOffset,
                _presetPreviewY,
                _contentLayer.transform,
                () => CycleSavedPreset(-1));
            _presetNextArrow = CreatePresetArrow(
                nextTemplate,
                "WardrobePresetNext",
                arrowOffset,
                _presetPreviewY,
                _contentLayer.transform,
                () => CycleSavedPreset(1));
        }

        private void BuildPresetManager(GameObject headerTemplate)
        {
            _presetManagerRoot = new GameObject("WardrobePresetManager");
            _presetManagerRoot.layer = _uiLayer;
            _presetManagerRoot.transform.SetParent(_contentLayer.transform, false);
            _presetManagerRoot.transform.localPosition = Vector3.zero;
            _presetManagerRoot.transform.localScale = Vector3.one;

            float totalWidth = Mathf.Abs(_rightX) * 3f * FooterWidthScale;
            float bodyTop = _headerY - 12f;
            float bodyBottom = _headerY - FooterOffsetFromHeader - 24f;
            GameObject backdropObject = new GameObject("PresetManagerBackdrop");
            backdropObject.layer = _uiLayer;
            backdropObject.transform.SetParent(_presetManagerRoot.transform, false);
            backdropObject.transform.localPosition =
                new Vector3(0f, (bodyTop + bodyBottom) * 0.5f, 0f);
            UITexture backdrop = backdropObject.AddComponent<UITexture>();
            backdrop.pivot = UIWidget.Pivot.Center;
            backdrop.mainTexture = Texture2D.whiteTexture;
            backdrop.color = IsolatedPreviewBackground;
            backdrop.width = Mathf.RoundToInt(totalWidth);
            backdrop.height = Mathf.RoundToInt(bodyTop - bodyBottom);
            backdrop.depth = PresetManagerDepth;

            CreateHeader(
                headerTemplate,
                "Saved Keepers",
                0f,
                _headerY,
                totalWidth,
                _presetManagerRoot.transform,
                PresetManagerDepth + 1);

            float previewY = _headerY - 195f;
            float middleSlotY = _headerY - 200f;
            GameObject previewObject = new GameObject("PresetManagerKeeperView");
            previewObject.layer = _uiLayer;
            previewObject.transform.SetParent(_presetManagerRoot.transform, false);
            previewObject.transform.localPosition = new Vector3(0f, previewY, 0f);
            _presetManagerPreviewWidget = previewObject.AddComponent<UITexture>();
            _presetManagerPreviewWidget.pivot = UIWidget.Pivot.Center;
            _presetManagerPreviewWidget.width = PresetManagerPreviewWidth;
            _presetManagerPreviewWidget.height = PresetManagerPreviewHeight;
            _presetManagerPreviewWidget.depth = PresetManagerDepth + 3;
            _presetManagerPreviewWidget.color = Color.white;
            _presetManagerPreviewWidget.enabled = false;

            if (TryGetPresetArrowTemplates(
                    out UIButton previousTemplate,
                    out UIButton nextTemplate))
            {
                float arrowOffset = PresetManagerPreviewWidth * 0.5f + 22f;
                _presetManagerPreviousArrow = CreatePresetArrow(
                    previousTemplate,
                    "PresetManagerPrevious",
                    -arrowOffset,
                    middleSlotY,
                    _presetManagerRoot.transform,
                    () => CycleSavedPreset(-1),
                    1.35f,
                    PresetManagerDepth + 5);
                _presetManagerNextArrow = CreatePresetArrow(
                    nextTemplate,
                    "PresetManagerNext",
                    arrowOffset,
                    middleSlotY,
                    _presetManagerRoot.transform,
                    () => CycleSavedPreset(1),
                    1.35f,
                    PresetManagerDepth + 5);
            }

            float slotX = Mathf.Abs(_rightX) * 0.92f;
            for (int index = 0; index < CosmeticsSync.SavedPresetSlotCount; index++)
            {
                int row = index % 3;
                float x = index < 3 ? -slotX : slotX;
                float y = _headerY - 95f - row * 105f;
                BuildPresetManagerSlot(index, new Vector3(x, y, 0f));
            }

            _presetManagerStatusLabel = CreateLabel(
                "PresetManagerStatus",
                "No saved keepers",
                new Vector3(0f, previewY - PresetManagerPreviewHeight * 0.5f - 18f, 0f),
                18,
                NGUIText.Alignment.Center,
                _presetManagerRoot.transform);
            _presetManagerStatusLabel.depth = PresetManagerDepth + 5;

            float deleteY = previewY - PresetManagerPreviewHeight * 0.5f - 50f;
            _presetManagerDeleteButton = CreateRedButtonObject(
                "Delete",
                0f,
                deleteY,
                _presetManagerRoot.transform,
                RequestDeleteFocusedPreset,
                PresetManagerDepth + 6);
            CreateRedButtonObject(
                "Back",
                0f,
                _headerY - FooterOffsetFromHeader,
                _presetManagerRoot.transform,
                RestoreEditorView,
                PresetManagerDepth + 6);

            SetLayerRecursively(_presetManagerRoot, _uiLayer);
            _presetManagerRoot.SetActive(false);
        }

        private void BuildPresetManagerSlot(int index, Vector3 position)
        {
            GameObject root = CreateInventorySlotFrame(
                "PresetManagerSlotFrame_" + (index + 1),
                _presetManagerRoot.transform,
                position,
                PresetManagerSlotWidth,
                PresetManagerSlotHeight,
                PresetManagerDepth + 3,
                4f);
            if (root == null)
                return;

            PresetManagerSlotVisual slot = new PresetManagerSlotVisual
            {
                Index = index,
                Root = root,
            };
            foreach (UIWidget piece in root.GetComponentsInChildren<UIWidget>(true))
            {
                slot.FramePieces.Add(piece);
                slot.FrameColors.Add(piece.color);
            }

            GameObject backgroundObject = new GameObject("PresetManagerSlotBackground");
            backgroundObject.layer = _uiLayer;
            backgroundObject.transform.SetParent(root.transform, false);
            UITexture background = backgroundObject.AddComponent<UITexture>();
            background.pivot = UIWidget.Pivot.Center;
            background.mainTexture = Texture2D.whiteTexture;
            background.color = IsolatedPreviewBackground;
            background.width = PresetManagerSlotWidth;
            background.height = PresetManagerSlotHeight;
            background.depth = PresetManagerDepth + 2;

            GameObject previewObject = new GameObject("PresetManagerSlotKeeper");
            previewObject.layer = _uiLayer;
            previewObject.transform.SetParent(root.transform, false);
            slot.Preview = previewObject.AddComponent<UITexture>();
            slot.Preview.pivot = UIWidget.Pivot.Center;
            slot.Preview.width = PresetManagerSlotWidth - 6;
            slot.Preview.height = PresetManagerSlotHeight - 6;
            slot.Preview.depth = PresetManagerDepth + 4;
            slot.Preview.color = Color.white;
            slot.Preview.enabled = false;

            BoxCollider2D collider = root.AddComponent<BoxCollider2D>();
            collider.size = new Vector2(
                PresetManagerSlotWidth + 6f,
                PresetManagerSlotHeight + 6f);
            collider.isTrigger = true;
            UIEventListener listener = UIEventListener.Get(root);
            listener.onClick = go => SelectSavedPreset(index);
            listener.onHover = (go, over) => RefreshPresetManagerSlotFrames(index, over);
            _presetManagerSlots.Add(slot);
        }

        private static bool TryGetPresetArrowTemplates(
            out UIButton previous,
            out UIButton next)
        {
            previous = null;
            next = null;

            MenuItemGUI languageSwitcher = GUIElements.me?.options?.language_switcher;
            SimpleOptionsSwitcher options =
                languageSwitcher?.GetComponentInChildren<SimpleOptionsSwitcher>(true);
            if (options != null)
            {
                List<UIButton> buttons = new List<UIButton>(
                    options.GetComponentsInChildren<UIButton>(true));
                buttons.Sort((left, right) =>
                    left.transform.position.x.CompareTo(right.transform.position.x));
                if (buttons.Count >= 2)
                {
                    previous = buttons[0];
                    next = buttons[buttons.Count - 1];
                    return true;
                }
            }

            CraftIngredientButtonsPair fallback =
                GUIElements.me?.GetComponentInChildren<CraftIngredientButtonsPair>(true);
            previous = fallback?.button_previous;
            next = fallback?.button_next;
            return previous != null && next != null;
        }

        private GameObject CreatePresetArrow(
            UIButton template,
            string name,
            float x,
            float y,
            Transform parent,
            Action callback,
            float scale = 1.35f,
            int minimumDepth = LivePreviewDepth + 4)
        {
            GameObject root = Instantiate(template.gameObject, parent);
            root.name = name;
            ClearExternalAnchors(root);
            root.SetActive(true);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one * scale;
            foreach (UIButtonMessage message in root.GetComponentsInChildren<UIButtonMessage>(true))
                DestroyImmediate(message);
            foreach (UIButton button in root.GetComponentsInChildren<UIButton>(true))
                button.onClick.Clear();
            SetMinimumWidgetDepth(root, minimumDepth);
            if (TryGetWidgetBounds(root, out Bounds bounds))
            {
                root.transform.localPosition += new Vector3(
                    x - bounds.center.x,
                    y - bounds.center.y,
                    0f);
            }
            else
            {
                root.transform.localPosition = new Vector3(x, y, 0f);
            }

            Collider2D collider = FindEnabledCollider(root);
            GameObject eventTarget = collider != null ? collider.gameObject : root;
            UIEventListener.Get(eventTarget).onClick = go =>
            {
                Sounds.OnGUIClick();
                callback();
            };
            root.SetActive(false);
            return root;
        }

        private void BuildLivePreviewFrame()
        {
            if (_livePreviewWidget == null)
                return;

            try
            {
                byte[] imageData;
                using (Stream stream = typeof(CharacterCustomizationGUI).Assembly
                           .GetManifestResourceStream(LivePreviewFrameResource))
                {
                    if (stream == null)
                        throw new InvalidOperationException("embedded camera frame is missing");
                    imageData = new byte[stream.Length];
                    int offset = 0;
                    while (offset < imageData.Length)
                    {
                        int read = stream.Read(imageData, offset, imageData.Length - offset);
                        if (read <= 0)
                            throw new EndOfStreamException("camera frame resource ended early");
                        offset += read;
                    }
                }

                _livePreviewFrameTexture = new Texture2D(
                    2,
                    2,
                    TextureFormat.RGBA32,
                    false)
                {
                    name = "WardrobeCameraFrame",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                };
                if (!ImageConversion.LoadImage(_livePreviewFrameTexture, imageData, false))
                    throw new InvalidOperationException("Unity could not decode the camera frame");

                _livePreviewFrameRoot = new GameObject("WardrobeCameraFrameRoot");
                _livePreviewFrameRoot.layer = _uiLayer;
                _livePreviewFrameRoot.transform.SetParent(_contentLayer.transform, false);
                _livePreviewFrameRoot.transform.localPosition =
                    _livePreviewWidget.transform.localPosition;

                float innerWidth = _livePreviewWidget.width;
                float innerHeight = _livePreviewWidget.height;
                float left = -innerWidth * 0.5f;
                float right = innerWidth * 0.5f;
                float bottom = -innerHeight * 0.5f;
                float top = innerHeight * 0.5f;
                float border = LivePreviewFrameBorder;

                AddLivePreviewFramePiece(
                    "TopLeft",
                    new Rect(125f, 43f, 123f, 111f),
                    new Rect(left - border, top, border, border));
                AddLivePreviewFramePiece(
                    "Top",
                    new Rect(248f, 43f, 834f, 111f),
                    new Rect(left, top, innerWidth, border));
                AddLivePreviewFramePiece(
                    "TopRight",
                    new Rect(1082f, 43f, 123f, 111f),
                    new Rect(right, top, border, border));
                AddLivePreviewFramePiece(
                    "Left",
                    new Rect(125f, 154f, 123f, 874f),
                    new Rect(left - border, bottom, border, innerHeight));
                AddLivePreviewFramePiece(
                    "Right",
                    new Rect(1082f, 154f, 123f, 874f),
                    new Rect(right, bottom, border, innerHeight));
                AddLivePreviewFramePiece(
                    "BottomLeft",
                    new Rect(125f, 1028f, 123f, 103f),
                    new Rect(left - border, bottom - border, border, border));
                AddLivePreviewFramePiece(
                    "Bottom",
                    new Rect(248f, 1028f, 834f, 103f),
                    new Rect(left, bottom - border, innerWidth, border));
                AddLivePreviewFramePiece(
                    "BottomRight",
                    new Rect(1082f, 1028f, 123f, 103f),
                    new Rect(right, bottom - border, border, border));
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[CharacterCustomization] Could not create camera frame: {ex.Message}");
                if (_livePreviewFrameRoot != null)
                    DestroyImmediate(_livePreviewFrameRoot);
                _livePreviewFrameRoot = null;
                if (_livePreviewFrameTexture != null)
                    DestroyImmediate(_livePreviewFrameTexture);
                _livePreviewFrameTexture = null;
            }
        }

        private void AddLivePreviewFramePiece(string name, Rect sourcePixels, Rect targetRect)
        {
            GameObject pieceObject = new GameObject("WardrobeCameraFrame_" + name);
            pieceObject.layer = _uiLayer;
            pieceObject.transform.SetParent(_livePreviewFrameRoot.transform, false);
            pieceObject.transform.localPosition = targetRect.center;

            UITexture piece = pieceObject.AddComponent<UITexture>();
            piece.pivot = UIWidget.Pivot.Center;
            piece.mainTexture = _livePreviewFrameTexture;
            piece.uvRect = SourcePixelsToUv(sourcePixels);
            piece.width = Mathf.Max(1, Mathf.RoundToInt(targetRect.width));
            piece.height = Mathf.Max(1, Mathf.RoundToInt(targetRect.height));
            piece.depth = LivePreviewDepth + 1;
            piece.color = Color.white;
            piece.MarkAsChanged();
        }

        private static Rect SourcePixelsToUv(Rect sourcePixels)
        {
            const float sourceWidth = 1330f;
            const float sourceHeight = 1182f;
            return new Rect(
                sourcePixels.x / sourceWidth,
                (sourceHeight - sourcePixels.y - sourcePixels.height) / sourceHeight,
                sourcePixels.width / sourceWidth,
                sourcePixels.height / sourceHeight);
        }

        private bool StartIsolatedKeeperPreview()
        {
            StopIsolatedKeeperPreview();

            GameObject playerPrefab = Prefabs.me?.player_prefab;
            if (playerPrefab == null)
            {
                PlayerComponent loadedPrefab = GameLoader.GetPlayerPrefab();
                playerPrefab = loadedPrefab != null ? loadedPrefab.gameObject : null;
            }
            if (playerPrefab == null)
            {
                CoopMod.Logger.LogError(
                    "[CharacterCustomization] No keeper prefab is available in the menu scene");
                return false;
            }

            GameObject clone = null;
            try
            {
                clone = Instantiate(playerPrefab);
                clone.name = "LobbyKeeperPreview";
                clone.hideFlags = HideFlags.HideAndDontSave;
                clone.SetActive(false);

                PlayerComponent player = clone.GetComponent<PlayerComponent>();
                if (player != null)
                    player.is_local_player = false;
                WorldGameObject worldObject = clone.GetComponent<WorldGameObject>();
                if (worldObject != null)
                    worldObject.is_player = false;

                // The prefab supplies the keeper's animator and sprite hierarchy only.
                // Disabling every gameplay behaviour before reactivation keeps this
                // preview out of world updates, interaction, movement, and networking.
                foreach (Behaviour behaviour in clone.GetComponentsInChildren<Behaviour>(true))
                {
                    if (!(behaviour is Animator))
                        behaviour.enabled = false;
                }
                foreach (Collider collider in clone.GetComponentsInChildren<Collider>(true))
                    collider.enabled = false;
                foreach (Collider2D collider in clone.GetComponentsInChildren<Collider2D>(true))
                    collider.enabled = false;
                foreach (Rigidbody body in clone.GetComponentsInChildren<Rigidbody>(true))
                    body.isKinematic = true;
                foreach (Rigidbody2D body in clone.GetComponentsInChildren<Rigidbody2D>(true))
                    body.simulated = false;
                foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
                {
                    SpriteRenderer sprite = renderer as SpriteRenderer;
                    if (sprite == null)
                    {
                        renderer.enabled = false;
                        continue;
                    }

                    if (ShouldExcludeLivePreviewSprite(sprite))
                    {
                        sprite.enabled = false;
                        continue;
                    }
                    sprite.gameObject.layer = IsolatedPreviewLayer;
                }

                clone.transform.position = IsolatedPreviewPosition;
                clone.transform.rotation = Quaternion.identity;
                clone.transform.localScale = Vector3.one;
                clone.SetActive(true);

                foreach (Animator animator in clone.GetComponentsInChildren<Animator>(true))
                {
                    animator.enabled = true;
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    animator.Rebind();
                    animator.SetInteger("global_state", (int)CharAnimState.Idle);
                    animator.SetInteger("sub_state", 0);
                    animator.SetFloat("direction_angle", -90f);
                    animator.SetFloat("diagonal_direction_angle", -90f);
                    animator.Update(0f);
                }

                _isolatedPreviewRoot = clone;
                _isolatedPreviewDriver = clone.AddComponent<PlayerCosmeticsDriver>();
                _isolatedPreviewDriver.Initialize();
                _isolatedPreviewDriver.SetCosmetics(
                    _draft ?? CosmeticsSync.Instance?.LocalCosmetics ?? PlayerCosmetics.Default);
                if (!_isolatedPreviewDriver.LastApplySucceeded)
                    throw new InvalidOperationException("the isolated keeper could not be recolored");

                CoopMod.Logger.LogInfo(
                    $"[CharacterCustomization] Isolated keeper preview ready " +
                    $"({_isolatedPreviewDriver.ActiveBackend})");
                return true;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError(
                    $"[CharacterCustomization] Could not create isolated keeper preview: {ex}");
                if (clone != null)
                {
                    clone.SetActive(false);
                    Destroy(clone);
                }
                _isolatedPreviewRoot = null;
                _isolatedPreviewDriver = null;
                return false;
            }
        }

        private void StopIsolatedKeeperPreview()
        {
            _isolatedPreviewDriver = null;
            if (_isolatedPreviewRoot == null)
                return;

            _isolatedPreviewRoot.SetActive(false);
            Destroy(_isolatedPreviewRoot);
            _isolatedPreviewRoot = null;
        }

        private GameObject GetPreviewKeeperRoot()
        {
            if (_openedFromLobby)
                return _isolatedPreviewRoot;
            return MainGame.me?.player != null ? MainGame.me.player.gameObject : null;
        }

        private Camera GetPreviewSourceCamera()
        {
            if (!_openedFromLobby && MainGame.me != null)
                return MainGame.me.world_cam ?? MainGame.me.GetComponent<Camera>() ?? Camera.main;
            return Camera.main;
        }

        private void StartLivePlayerPreview()
        {
            StopLivePlayerPreview();
            GameObject previewRoot = GetPreviewKeeperRoot();
            if (_livePreviewWidget == null || previewRoot == null)
                return;

            Camera sourceCamera = GetPreviewSourceCamera();
            if (sourceCamera == null && !_openedFromLobby)
            {
                CoopMod.Logger.LogWarning(
                    "[CharacterCustomization] Live keeper preview has no world camera");
                return;
            }

            try
            {
                FaceLivePreviewPlayerTowardCamera();

                float aspect = Mathf.Max(
                    0.25f,
                    (float)_livePreviewWidget.width / _livePreviewWidget.height);
                int textureWidth = Mathf.Max(
                    128,
                    Mathf.RoundToInt(LivePreviewTextureHeight * aspect));
                _livePreviewRenderTexture = new RenderTexture(
                    textureWidth,
                    LivePreviewTextureHeight,
                    16,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Default)
                {
                    name = "WardrobeLiveKeeperRenderTexture",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    antiAliasing = 1,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _livePreviewRenderTexture.Create();

                _livePreviewCameraObject = new GameObject("WardrobeLiveKeeperCamera")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _livePreviewCamera = _livePreviewCameraObject.AddComponent<Camera>();
                if (sourceCamera != null)
                    _livePreviewCamera.CopyFrom(sourceCamera);
                _livePreviewCamera.targetTexture = _livePreviewRenderTexture;
                _livePreviewCamera.rect = new Rect(0f, 0f, 1f, 1f);
                _livePreviewCamera.aspect = aspect;
                _livePreviewCamera.allowHDR = false;
                _livePreviewCamera.allowMSAA = false;
                _livePreviewCamera.enabled = false;
                if (_openedFromLobby)
                {
                    _livePreviewCamera.clearFlags = CameraClearFlags.SolidColor;
                    _livePreviewCamera.backgroundColor = IsolatedPreviewBackground;
                    _livePreviewCamera.cullingMask = 1 << IsolatedPreviewLayer;
                }

                FrameLivePreviewCamera(sourceCamera, previewRoot);
                _livePreviewWidget.mainTexture = _livePreviewRenderTexture;
                _livePreviewWidget.enabled = true;
                _livePreviewWidget.MarkAsChanged();
                _livePreviewDirty = true;
                CoopMod.Logger.LogInfo(
                    $"[CharacterCustomization] Live keeper camera ready: " +
                    $"{textureWidth}x{LivePreviewTextureHeight}, " +
                    $"ortho={_livePreviewCamera.orthographicSize:F1}");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[CharacterCustomization] Could not start live keeper preview: {ex}");
                StopLivePlayerPreview();
            }
        }

        private void FaceLivePreviewPlayerTowardCamera()
        {
            if (_openedFromLobby)
                return;
            BaseCharacterComponent character = MainGame.me?.player_char;
            if (character == null)
                return;

            _livePreviewCharacter = character;
            _livePreviewOriginalDirection = character.direction;
            _livePreviewFacingStored =
                _livePreviewOriginalDirection.sqrMagnitude > 0.0001f;
            character.LookAt(Direction.Down);

            // Pausing the game stops normal animator evaluation, so refresh the
            // facing pose immediately before the preview camera renders it.
            Animator animator = character.wgo?.GetComponentInChildren<Animator>();
            if (animator != null && animator.isActiveAndEnabled)
                animator.Update(0f);
        }

        private void RestoreLivePreviewPlayerFacing()
        {
            BaseCharacterComponent character = _livePreviewCharacter;
            Vector2 direction = _livePreviewOriginalDirection;
            bool shouldRestore = _livePreviewFacingStored;

            _livePreviewCharacter = null;
            _livePreviewOriginalDirection = Vector2.zero;
            _livePreviewFacingStored = false;

            if (shouldRestore && character != null)
                character.LookAt(direction);
        }

        private void FrameLivePreviewCamera(Camera sourceCamera, GameObject previewRoot)
        {
            if (_livePreviewCamera == null || previewRoot == null ||
                !TryGetPreviewKeeperBounds(previewRoot, out Bounds playerBounds))
            {
                return;
            }

            float aspect = Mathf.Max(0.01f, _livePreviewCamera.aspect);
            float playerPadding = CurrentCategory == CosmeticCategory.Eyes
                ? EyesPreviewPlayerPadding
                : LivePreviewPlayerPadding;
            float verticalSize = playerBounds.size.y * playerPadding * 0.5f;
            float horizontalSize =
                playerBounds.size.x * playerPadding * 0.5f / aspect;
            float orthographicSize = Mathf.Max(0.7f, verticalSize, horizontalSize);
            float centerY = playerBounds.center.y;
            if (CurrentCategory == CosmeticCategory.Eyes)
            {
                orthographicSize = Mathf.Max(
                    0.45f,
                    orthographicSize * EyesPreviewOrthographicScale);
                centerY = playerBounds.max.y -
                          playerBounds.size.y * EyesPreviewFocusFromTop;
            }

            _livePreviewCamera.orthographic = true;
            _livePreviewCamera.orthographicSize = orthographicSize;
            _livePreviewCamera.transform.position = new Vector3(
                playerBounds.center.x,
                centerY,
                _openedFromLobby || sourceCamera == null
                    ? playerBounds.center.z - 100f
                    : sourceCamera.transform.position.z);
            _livePreviewCamera.transform.rotation = !_openedFromLobby && sourceCamera != null
                ? sourceCamera.transform.rotation
                : Quaternion.identity;
        }

        private static bool TryGetPreviewKeeperBounds(GameObject previewRoot, out Bounds result)
        {
            result = default(Bounds);
            if (previewRoot == null)
                return false;

            bool found = false;
            foreach (SpriteRenderer renderer in
                     previewRoot.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (renderer == null || renderer.sprite == null || !renderer.enabled ||
                    !renderer.gameObject.activeInHierarchy || ShouldExcludeLivePreviewSprite(renderer))
                {
                    continue;
                }

                if (!found)
                {
                    result = renderer.bounds;
                    found = true;
                }
                else
                {
                    result.Encapsulate(renderer.bounds);
                }
            }
            return found;
        }

        private void RenderLivePlayerPreview()
        {
            _livePreviewDirty = false;
            if (_livePreviewCamera == null || _livePreviewRenderTexture == null ||
                !_livePreviewRenderTexture.IsCreated())
            {
                return;
            }

            try
            {
                _livePreviewCamera.Render();
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[CharacterCustomization] Live keeper preview render failed: {ex.Message}");
                StopLivePlayerPreview();
            }
        }

        private void StopLivePlayerPreview()
        {
            _livePreviewDirty = false;
            RestoreLivePreviewPlayerFacing();
            if (_livePreviewWidget != null)
            {
                _livePreviewWidget.mainTexture = null;
                _livePreviewWidget.enabled = false;
                _livePreviewWidget.MarkAsChanged();
            }

            if (_livePreviewCamera != null)
                _livePreviewCamera.targetTexture = null;
            if (_livePreviewCameraObject != null)
            {
                _livePreviewCameraObject.SetActive(false);
                Destroy(_livePreviewCameraObject);
            }
            _livePreviewCamera = null;
            _livePreviewCameraObject = null;

            if (_livePreviewRenderTexture != null)
            {
                if (_livePreviewRenderTexture.IsCreated())
                    _livePreviewRenderTexture.Release();
                Destroy(_livePreviewRenderTexture);
            }
            _livePreviewRenderTexture = null;
        }

        private void StartPresetPreview()
        {
            StopPresetPreview();
            RefreshPresetControls();
            GameObject previewRoot = GetPreviewKeeperRoot();
            if (_presetPreviewWidget == null || _savedPresets.Count == 0 ||
                previewRoot == null)
            {
                return;
            }

            Camera sourceCamera = GetPreviewSourceCamera();
            if (sourceCamera == null && !_openedFromLobby)
                return;

            try
            {
                const int textureHeight = 192;
                float aspect = (float)PresetPreviewWidth / PresetPreviewHeight;
                int textureWidth = Mathf.Max(64, Mathf.RoundToInt(textureHeight * aspect));
                _presetPreviewRenderTexture = new RenderTexture(
                    textureWidth,
                    textureHeight,
                    16,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Default)
                {
                    name = "WardrobeSavedKeeperRenderTexture",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    antiAliasing = 1,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _presetPreviewRenderTexture.Create();

                _presetPreviewCameraObject = new GameObject("WardrobeSavedKeeperCamera")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _presetPreviewCamera = _presetPreviewCameraObject.AddComponent<Camera>();
                if (sourceCamera != null)
                    _presetPreviewCamera.CopyFrom(sourceCamera);
                _presetPreviewCamera.targetTexture = _presetPreviewRenderTexture;
                _presetPreviewCamera.rect = new Rect(0f, 0f, 1f, 1f);
                _presetPreviewCamera.aspect = aspect;
                _presetPreviewCamera.clearFlags = CameraClearFlags.SolidColor;
                _presetPreviewCamera.backgroundColor = Color.clear;
                _presetPreviewCamera.cullingMask = 1 << PresetPreviewLayer;
                _presetPreviewCamera.allowHDR = false;
                _presetPreviewCamera.allowMSAA = false;
                _presetPreviewCamera.enabled = false;
                FramePresetPreviewCamera(sourceCamera, previewRoot);

                _presetPreviewWidget.mainTexture = _presetPreviewRenderTexture;
                _presetPreviewWidget.enabled = GetFocusedSavedPreset() != null;
                _presetPreviewWidget.MarkAsChanged();
                if (_presetManagerPreviewWidget != null)
                {
                    _presetManagerPreviewWidget.mainTexture = _presetPreviewRenderTexture;
                    _presetManagerPreviewWidget.enabled =
                        _presetManagerVisible && GetFocusedSavedPreset() != null;
                    _presetManagerPreviewWidget.MarkAsChanged();
                }
                _presetPreviewDirty = true;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[CharacterCustomization] Could not start saved keeper preview: {ex.Message}");
                StopPresetPreview();
            }
        }

        private void FramePresetPreviewCamera(Camera sourceCamera, GameObject previewRoot)
        {
            if (_presetPreviewCamera == null || previewRoot == null ||
                !TryGetPreviewKeeperBounds(previewRoot, out Bounds playerBounds))
            {
                return;
            }

            float aspect = Mathf.Max(0.01f, _presetPreviewCamera.aspect);
            float verticalSize = playerBounds.size.y * PresetPreviewPlayerPadding * 0.5f;
            float horizontalSize =
                playerBounds.size.x * PresetPreviewPlayerPadding * 0.5f / aspect;
            _presetPreviewCamera.orthographic = true;
            _presetPreviewCamera.orthographicSize =
                Mathf.Max(0.5f, verticalSize, horizontalSize);
            _presetPreviewCamera.transform.position = new Vector3(
                playerBounds.center.x,
                playerBounds.center.y,
                _openedFromLobby || sourceCamera == null
                    ? playerBounds.center.z - 100f
                    : sourceCamera.transform.position.z);
            _presetPreviewCamera.transform.rotation = !_openedFromLobby && sourceCamera != null
                ? sourceCamera.transform.rotation
                : Quaternion.identity;
        }

        private void RenderPresetPreview()
        {
            GameObject previewRoot = GetPreviewKeeperRoot();
            if (_presetPreviewCamera == null || _presetPreviewRenderTexture == null ||
                !_presetPreviewRenderTexture.IsCreated() || previewRoot == null ||
                GetFocusedSavedPreset() == null)
            {
                return;
            }

            RenderPresetPreviewRoot(previewRoot);
        }

        private void RenderPresetManagerSlots()
        {
            GameObject previewRoot = GetPreviewKeeperRoot();
            if (!_presetManagerVisible || _presetPreviewCamera == null ||
                previewRoot == null || _draft == null)
            {
                return;
            }

            EnsurePresetManagerSlotRenderTextures();
            PlayerCosmetics restoreCosmetics = _draft.Clone();
            Camera sourceCamera = GetPreviewSourceCamera();
            try
            {
                foreach (PresetManagerSlotVisual slot in _presetManagerSlots)
                {
                    PlayerCosmetics preset =
                        slot.Index >= 0 && slot.Index < _savedPresets.Count
                            ? _savedPresets[slot.Index]
                            : null;
                    if (slot.Preview == null || slot.RenderTexture == null ||
                        !slot.RenderTexture.IsCreated() || preset == null)
                    {
                        if (slot.Preview != null)
                        {
                            slot.Preview.enabled = false;
                            slot.Preview.MarkAsChanged();
                        }
                        continue;
                    }

                    if (!ApplyPreviewCosmetics(preset))
                        continue;

                    _presetPreviewCamera.targetTexture = slot.RenderTexture;
                    _presetPreviewCamera.aspect =
                        (float)PresetManagerSlotWidth / PresetManagerSlotHeight;
                    FramePresetPreviewCamera(sourceCamera, previewRoot);
                    RenderPresetPreviewRoot(previewRoot);
                    slot.Preview.mainTexture = slot.RenderTexture;
                    slot.Preview.enabled = true;
                    slot.Preview.MarkAsChanged();
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[CharacterCustomization] Saved keeper gallery render failed: {ex.Message}");
            }
            finally
            {
                ApplyPreviewCosmetics(restoreCosmetics);
                _presetPreviewCamera.targetTexture = _presetPreviewRenderTexture;
                _presetPreviewCamera.aspect =
                    (float)PresetPreviewWidth / PresetPreviewHeight;
                FramePresetPreviewCamera(sourceCamera, previewRoot);
                _presetPreviewDirty = true;
                RefreshPresetControls();
            }
        }

        private void EnsurePresetManagerSlotRenderTextures()
        {
            const int textureHeight = 176;
            int textureWidth = Mathf.Max(
                64,
                Mathf.RoundToInt(
                    textureHeight * (float)PresetManagerSlotWidth /
                    PresetManagerSlotHeight));
            foreach (PresetManagerSlotVisual slot in _presetManagerSlots)
            {
                if (slot.RenderTexture != null && slot.RenderTexture.IsCreated())
                    continue;

                if (slot.RenderTexture != null)
                    Destroy(slot.RenderTexture);
                slot.RenderTexture = new RenderTexture(
                    textureWidth,
                    textureHeight,
                    16,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Default)
                {
                    name = "WardrobeSavedKeeperSlotRenderTexture_" + (slot.Index + 1),
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    antiAliasing = 1,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                slot.RenderTexture.Create();
                if (slot.Preview != null)
                {
                    slot.Preview.mainTexture = slot.RenderTexture;
                    slot.Preview.MarkAsChanged();
                }
            }
        }

        private void DestroyPresetManagerSlotRenderTextures()
        {
            foreach (PresetManagerSlotVisual slot in _presetManagerSlots)
            {
                if (slot.Preview != null)
                {
                    slot.Preview.mainTexture = null;
                    slot.Preview.enabled = false;
                    slot.Preview.MarkAsChanged();
                }
                if (slot.RenderTexture == null)
                    continue;
                if (slot.RenderTexture.IsCreated())
                    slot.RenderTexture.Release();
                Destroy(slot.RenderTexture);
                slot.RenderTexture = null;
            }
        }

        private void RenderPresetPreviewRoot(GameObject previewRoot)
        {
            if (_presetPreviewCamera == null || previewRoot == null)
                return;

            Dictionary<GameObject, int> originalLayers = new Dictionary<GameObject, int>();
            try
            {
                foreach (SpriteRenderer renderer in
                         previewRoot.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    if (renderer == null || renderer.sprite == null || !renderer.enabled ||
                        !renderer.gameObject.activeInHierarchy ||
                        ShouldExcludeLivePreviewSprite(renderer))
                    {
                        continue;
                    }

                    GameObject renderedObject = renderer.gameObject;
                    if (!originalLayers.ContainsKey(renderedObject))
                        originalLayers.Add(renderedObject, renderedObject.layer);
                    renderedObject.layer = PresetPreviewLayer;
                }
                _presetPreviewCamera.Render();
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[CharacterCustomization] Saved keeper preview render failed: {ex.Message}");
            }
            finally
            {
                foreach (KeyValuePair<GameObject, int> entry in originalLayers)
                {
                    if (entry.Key != null)
                        entry.Key.layer = entry.Value;
                }
            }
        }

        private void StopPresetPreview()
        {
            _presetPreviewDirty = false;
            if (_presetPreviewWidget != null)
            {
                _presetPreviewWidget.mainTexture = null;
                _presetPreviewWidget.enabled = false;
                _presetPreviewWidget.MarkAsChanged();
            }
            if (_presetManagerPreviewWidget != null)
            {
                _presetManagerPreviewWidget.mainTexture = null;
                _presetManagerPreviewWidget.enabled = false;
                _presetManagerPreviewWidget.MarkAsChanged();
            }
            if (_presetPreviewCamera != null)
                _presetPreviewCamera.targetTexture = null;
            if (_presetPreviewCameraObject != null)
            {
                _presetPreviewCameraObject.SetActive(false);
                Destroy(_presetPreviewCameraObject);
            }
            _presetPreviewCamera = null;
            _presetPreviewCameraObject = null;

            if (_presetPreviewRenderTexture != null)
            {
                if (_presetPreviewRenderTexture.IsCreated())
                    _presetPreviewRenderTexture.Release();
                Destroy(_presetPreviewRenderTexture);
            }
            _presetPreviewRenderTexture = null;
        }

        private static bool ShouldExcludeLivePreviewSprite(SpriteRenderer renderer)
        {
            string name = renderer.name.ToLowerInvariant();
            return name.Contains("shadow") || name.Contains("tool") ||
                   name.Contains("overhead") || name.Contains("fish") ||
                   name.Contains("effect") || name.Contains("light");
        }

        private UIFont FindReferenceFont()
        {
            foreach (UILabel label in GetComponentsInChildren<UILabel>(true))
            {
                if (label.bitmapFont != null)
                    return label.bitmapFont;
            }
            return null;
        }

        private static UIWidget FindLargestWidget(GameObject root)
        {
            UIWidget largest = null;
            int area = 0;
            foreach (UIWidget widget in root.GetComponentsInChildren<UIWidget>(true))
            {
                int currentArea = Mathf.Abs(widget.width * widget.height);
                if (largest == null || currentArea > area)
                {
                    largest = widget;
                    area = currentArea;
                }
            }
            return largest;
        }

        private static UIWidget FindButtonBackground(GameObject root)
        {
            UIWidget largest = null;
            int area = 0;
            foreach (UIWidget widget in root.GetComponentsInChildren<UIWidget>(true))
            {
                if (widget is UILabel ||
                    (!(widget is UI2DSprite) && !(widget is UISprite) && !(widget is UITexture)))
                {
                    continue;
                }

                int currentArea = Mathf.Abs(widget.width * widget.height);
                if (largest == null || currentArea > area)
                {
                    largest = widget;
                    area = currentArea;
                }
            }
            return largest;
        }

        private static Collider2D FindEnabledCollider(GameObject root)
        {
            foreach (Collider2D collider in root.GetComponentsInChildren<Collider2D>(true))
            {
                if (collider.enabled && collider.gameObject.activeSelf)
                    return collider;
            }
            return null;
        }

        private static int GetMaximumDepth(GameObject root)
        {
            int depth = 0;
            foreach (UIWidget widget in root.GetComponentsInChildren<UIWidget>(true))
                depth = Mathf.Max(depth, widget.depth);
            return depth;
        }

        private static void SetMaximumWidgetDepth(GameObject root, int maximumDepth)
        {
            int currentMaximum = int.MinValue;
            foreach (UIWidget widget in root.GetComponentsInChildren<UIWidget>(true))
            {
                if (widget.enabled && widget.gameObject.activeSelf)
                    currentMaximum = Mathf.Max(currentMaximum, widget.depth);
            }
            if (currentMaximum == int.MinValue)
                return;

            int offset = maximumDepth - currentMaximum;
            foreach (UIWidget widget in root.GetComponentsInChildren<UIWidget>(true))
                widget.depth += offset;
        }

        private static void SetMinimumWidgetDepth(GameObject root, int minimumDepth)
        {
            int currentMinimum = int.MaxValue;
            foreach (UIWidget widget in root.GetComponentsInChildren<UIWidget>(true))
            {
                if (widget.enabled && widget.gameObject.activeSelf)
                    currentMinimum = Mathf.Min(currentMinimum, widget.depth);
            }
            if (currentMinimum == int.MaxValue)
                return;

            int offset = minimumDepth - currentMinimum;
            foreach (UIWidget widget in root.GetComponentsInChildren<UIWidget>(true))
                widget.depth += offset;
        }

        private static int GetMaximumPanelDepth(GameObject root)
        {
            int depth = 0;
            foreach (UIPanel panel in root.GetComponentsInChildren<UIPanel>(true))
                depth = Mathf.Max(depth, panel.depth);
            return depth;
        }

        private static GameObject CloneNativeRoot(
            GameObject source,
            Transform wrapper,
            string name)
        {
            GameObject clone = Instantiate(source, source.transform.parent, false);
            clone.name = name;
            clone.transform.SetParent(wrapper, true);
            ClearExternalAnchors(clone);
            return clone;
        }

        private static void ClearExternalAnchors(GameObject root)
        {
            Transform rootTransform = root.transform;
            foreach (UIRect rect in root.GetComponentsInChildren<UIRect>(true))
            {
                ClearExternalAnchor(rect.leftAnchor, rootTransform);
                ClearExternalAnchor(rect.rightAnchor, rootTransform);
                ClearExternalAnchor(rect.topAnchor, rootTransform);
                ClearExternalAnchor(rect.bottomAnchor, rootTransform);
            }
        }

        private static void ClearAllAnchors(UIRect rect)
        {
            if (rect == null)
                return;
            rect.leftAnchor.target = null;
            rect.rightAnchor.target = null;
            rect.topAnchor.target = null;
            rect.bottomAnchor.target = null;
        }

        private static void SetInactive(Behaviour behaviour)
        {
            if (behaviour != null)
                behaviour.gameObject.SetActive(false);
        }

        private static void ClearExternalAnchor(
            UIRect.AnchorPoint anchor,
            Transform root)
        {
            Transform target = anchor?.target;
            if (target != null && target != root && !target.IsChildOf(root))
                anchor.target = null;
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = layer;
        }

        private static UI2DSprite FindNamedSprite(GameObject root, string part)
        {
            foreach (UI2DSprite sprite in root.GetComponentsInChildren<UI2DSprite>(true))
            {
                if (sprite.name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                    return sprite;
            }
            return null;
        }

        private static void StripLocalization(GameObject root)
        {
            foreach (LocalizedLabel localized in root.GetComponentsInChildren<LocalizedLabel>(true))
                DestroyImmediate(localized);
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// Manages save slot selection in the multiplayer lobby - mirrors singleplayer save selector.
    /// On the HOST, displays the host's own local saves (clickable, selectable).
    /// On a CLIENT, displays the host's saves mirrored via HostSaveListSync (read-only,
    /// including the host's "New Game" option and currently-selected slot highlighted).
    /// </summary>
    public class SaveSelectorPanel : MonoBehaviour
    {
        private const int SelectionFrameHorizontalPadding = 14;
        private const int SelectionFrameVerticalPadding = 8;

        private GameObject dialogFrame;
        private UILabel titleLabel;
        private UIScrollView scrollView;
        private UIPanel scrollPanel;
        private GameObject slotsContainer;
        private SimpleUITable slotsTable;
        private List<GameObject> slotItems = new List<GameObject>();
        private GameObject slotTemplate;
        private SaveSlotData selectedSlot;
        private bool hasSelection;
        private bool deleteInProgress;
        private bool deleteConfirmationOpen;
        private bool readOnlyMirrorMode; // true = show host's saves (client view)
        private bool subscribedToHostSaveListUpdates;
        
        public System.Action<SaveSlotData> OnSlotSelected;
        public System.Action OnSlotsChanged;
        
        /// <summary>
        /// Initialize the save selector panel with singleplayer-style dialog
        /// </summary>
        /// <param name="parent">Parent transform</param>
        /// <param name="localPosition">Local position in UI space</param>
        /// <param name="saveSlotTemplate">Save slot template to clone from SaveSlotsMenuGUI</param>
        public void Initialize(Transform parent, Vector3 localPosition, GameObject saveSlotTemplate)
        {
            transform.SetParent(parent, false);
            gameObject.name = "SaveSelectorPanel";
            gameObject.layer = 13;
            transform.localPosition = localPosition;
            
            this.slotTemplate = saveSlotTemplate;
            
            // Client-in-lobby = mirror host's saves read-only. Host shows own saves normally.
            readOnlyMirrorMode = (SteamLobbyManager.Instance != null
                                  && SteamLobbyManager.Instance.CurrentLobbyID.IsValid()
                                  && !SteamLobbyManager.Instance.IsHost);
            CoopMod.Logger.LogInfo($"[SaveSelectorPanel] readOnlyMirrorMode = {readOnlyMirrorMode}");
            
            // Find and clone the SaveSlotsMenuGUI as a base
            var saveSlotsMenuGUI = Object.FindObjectOfType<SaveSlotsMenuGUI>(true);
            if (saveSlotsMenuGUI != null)
            {
                // Clone the entire save slots menu to get the proper frame
                GameObject menuClone = Object.Instantiate(saveSlotsMenuGUI.gameObject, transform);
                menuClone.name = "SaveSelectorDialog";
                menuClone.layer = 13;
                menuClone.transform.localPosition = Vector3.zero;
                menuClone.transform.localScale = Vector3.one;

                // Navigation in the cloned save menu belongs to the original
                // SaveSlotsMenuGUI. Lobby slots receive fresh callbacks below.
                foreach (var controller in menuClone.GetComponentsInChildren<GamepadNavigationController>(true))
                    Object.DestroyImmediate(controller);
                foreach (var navigationItem in menuClone.GetComponentsInChildren<GamepadNavigationItem>(true))
                    Object.DestroyImmediate(navigationItem);
                foreach (var menuItem in menuClone.GetComponentsInChildren<MenuItemGUI>(true))
                    Object.DestroyImmediate(menuItem);
                
                // Remove the SaveSlotsMenuGUI component - we'll manage it ourselves
                var menuComponent = menuClone.GetComponent<SaveSlotsMenuGUI>();
                if (menuComponent != null)
                {
                    Object.DestroyImmediate(menuComponent);
                }
                
                // Clean up any stale ModEntry_ objects that leaked from Mods mode
                var modEntries = menuClone.GetComponentsInChildren<Transform>(true);
                foreach (var t in modEntries)
                {
                    if (t != null && t.name.StartsWith("ModEntry_"))
                    {
                        CoopMod.Logger.LogInfo($"[SaveSelector] Removing stale mod entry: {t.name}");
                        Object.DestroyImmediate(t.gameObject);
                    }
                }
                
                // Find the scroll view and table from the cloned menu
                scrollView = menuClone.GetComponentInChildren<UIScrollView>(true);
                slotsTable =
                    menuClone.GetComponentInChildren<SimpleUITable>(true);
                
                if (scrollView != null)
                {
                    scrollPanel = scrollView.GetComponent<UIPanel>();
                    CoopMod.Logger.LogInfo("Found ScrollView and UIPanel from cloned SaveSlotsMenuGUI");
                }
                
                if (slotsTable != null)
                {
                    // Match vanilla SaveSlotsMenuGUI. The prefab also carries a
                    // disabled legacy UITable whose bounds-based X calculation
                    // shifts these asymmetric rows left when invoked manually.
                    slotsTable.dont_change_x = true;
                    slotsContainer = slotsTable.gameObject;
                    CoopMod.Logger.LogInfo(
                        "Found SimpleUITable for save slots (X locked)");
                    RemoveClonedMenuSaveSlots();
                }
                
                // Find and update the title label
                var labels = menuClone.GetComponentsInChildren<UILabel>(true);
                foreach (var label in labels)
                {
                    if (label.text.Contains("Pick") || label.text.Contains("save") || label.name.ToLower().Contains("title"))
                    {
                        titleLabel = label;
                        titleLabel.text = readOnlyMirrorMode ? "Host's save slot" : "Pick a save slot";
                        CoopMod.Logger.LogInfo($"Found and updated title label: {titleLabel.name}");
                        break;
                    }
                }
                
                // Remove the close button (red X) - we don't need it in multiplayer
                var allButtons = menuClone.GetComponentsInChildren<UIButton>(true);
                foreach (var button in allButtons)
                {
                    if (button.name.ToLower().Contains("close") || button.name.ToLower().Contains("exit") || 
                        button.gameObject.name.ToLower().Contains("x"))
                    {
                        button.gameObject.SetActive(false);
                        CoopMod.Logger.LogInfo($"Removed close button: {button.name}");
                    }
                }
                
                menuClone.SetActive(true);
                dialogFrame = menuClone;
            }
            else
            {
                CoopMod.Logger.LogError("Could not find SaveSlotsMenuGUI to clone!");
                CreateFallbackDialog();
            }
            
            // Load save slots
            LoadSaveSlots();

            // Client: keep panel in sync with host's list / selection changes.
            if (readOnlyMirrorMode)
            {
                SubscribeToHostSaveListUpdates();
            }
            
            CoopMod.Logger.LogInfo($"SaveSelectorPanel initialized at {localPosition}");
        }

        private void OnDestroy()
        {
            UnsubscribeFromHostSaveListUpdates();
        }

        public void SetReadOnlyMirrorMode(bool enabled)
        {
            if (readOnlyMirrorMode == enabled)
            {
                if (enabled)
                {
                    SubscribeToHostSaveListUpdates();
                }
                else
                {
                    UnsubscribeFromHostSaveListUpdates();
                }

                CoopMod.Logger.LogInfo($"[SaveSelectorPanel] Refreshing {(enabled ? "client mirror" : "host local")} save list");
                LoadSaveSlots();
                return;
            }

            if (enabled)
            {
                SubscribeToHostSaveListUpdates();
            }
            else
            {
                UnsubscribeFromHostSaveListUpdates();
            }

            readOnlyMirrorMode = enabled;
            selectedSlot = null;
            hasSelection = false;

            if (titleLabel != null)
            {
                titleLabel.text = readOnlyMirrorMode ? "Host's save slot" : "Pick a save slot";
            }

            CoopMod.Logger.LogInfo($"[SaveSelectorPanel] Switched to {(readOnlyMirrorMode ? "client mirror" : "host local")} mode");
            LoadSaveSlots();
        }

        private void SubscribeToHostSaveListUpdates()
        {
            if (subscribedToHostSaveListUpdates)
                return;

            HostSaveListSync.OnUpdated += OnHostSaveListUpdated;
            subscribedToHostSaveListUpdates = true;
        }

        private void UnsubscribeFromHostSaveListUpdates()
        {
            if (!subscribedToHostSaveListUpdates)
                return;

            HostSaveListSync.OnUpdated -= OnHostSaveListUpdated;
            subscribedToHostSaveListUpdates = false;
        }

        private void OnHostSaveListUpdated()
        {
            // Rebuild slot list from the mirrored host data.
            LoadSaveSlots();
        }
        
        private void CreateFallbackDialog()
        {
            // Fallback: create a simple dialog if we can't clone SaveSlotsMenuGUI
            CoopMod.Logger.LogWarning("Using fallback dialog creation");
            
            dialogFrame = new GameObject("SaveSelectorDialog");
            dialogFrame.layer = 13;
            dialogFrame.transform.SetParent(transform, false);
            dialogFrame.transform.localPosition = Vector3.zero;
            dialogFrame.transform.localScale = Vector3.one;
            
            // Create a simple panel
            var panel = dialogFrame.AddComponent<UIPanel>();
            panel.depth = 110;
            
            // Create title
            GameObject titleObj = new GameObject("Title");
            titleObj.layer = 13;
            titleObj.transform.SetParent(dialogFrame.transform, false);
            titleObj.transform.localPosition = new Vector3(0, 200, 0);
            
            titleLabel = titleObj.AddComponent<UILabel>();
            titleLabel.text = "Pick a save slot";
            titleLabel.fontSize = 24;
            titleLabel.alignment = NGUIText.Alignment.Center;
            titleLabel.depth = 111;
            
            CreateScrollView();
        }
        
        private void CreateScrollView()
        {
            // Fallback scroll view creation (only used if we couldn't clone SaveSlotsMenuGUI)
            GameObject scrollObj = new GameObject("SaveScrollView");
            scrollObj.layer = 13;
            scrollObj.transform.SetParent(dialogFrame.transform, false);
            scrollObj.transform.localPosition = new Vector3(0, 0, 0);
            scrollObj.transform.localScale = Vector3.one;
            
            scrollPanel = scrollObj.AddComponent<UIPanel>();
            scrollPanel.depth = 111;
            scrollPanel.clipping = UIDrawCall.Clipping.SoftClip;
            scrollPanel.clipSoftness = new Vector2(4, 4);
            scrollPanel.baseClipRegion = new Vector4(0, 0, 400, 400);
            
            scrollView = scrollObj.AddComponent<UIScrollView>();
            scrollView.movement = UIScrollView.Movement.Vertical;
            scrollView.dragEffect = UIScrollView.DragEffect.MomentumAndSpring;
            scrollView.scrollWheelFactor = 0.5f;
            scrollView.restrictWithinPanel = true;
            
            slotsContainer = new GameObject("SlotsContainer");
            slotsContainer.layer = 13;
            slotsContainer.transform.SetParent(scrollObj.transform, false);
            slotsContainer.transform.localPosition = Vector3.zero;
            
            slotsTable = slotsContainer.AddComponent<SimpleUITable>();
            slotsTable.alignment = SimpleUITable.Alignment.Top;
            slotsTable.offset = 10;
            slotsTable.dont_change_x = true;
            
            CoopMod.Logger.LogInfo("Created fallback scroll view");
        }
        
        private void LoadSaveSlots()
        {
            // Clear existing slots
            ClearSlots();
            
            if (readOnlyMirrorMode)
            {
                // Client: build from mirrored host list; don't touch local disk.
                BuildSlotsFromHostMirror();
            }
            else
            {
                // Host: read local disk saves as normal.
                PlatformSpecific.ReadSaveSlots(OnSaveSlotsLoaded);
            }
        }

        private void RemoveClonedMenuSaveSlots()
        {
            if (slotsContainer == null)
                return;

            var inheritedSlots = slotsContainer.GetComponentsInChildren<SaveSlotGUI>(true);
            int removed = 0;
            foreach (var inheritedSlot in inheritedSlots)
            {
                if (inheritedSlot == null || inheritedSlot.gameObject == slotTemplate)
                    continue;

                inheritedSlot.gameObject.SetActive(false);
                Object.Destroy(inheritedSlot.gameObject);
                removed++;
            }

            if (removed > 0)
            {
                CoopMod.Logger.LogInfo($"[SaveSelectorPanel] Removed {removed} inherited save slot(s) from cloned menu");
            }
        }

        /// <summary>
        /// Build slot items from HostSaveListSync (client mirror view).
        /// </summary>
        private void BuildSlotsFromHostMirror()
        {
            if (slotsContainer == null || slotTemplate == null)
            {
                CoopMod.Logger.LogWarning("[SaveSelectorPanel] Mirror load skipped - missing container or template");
                return;
            }

            var entries = HostSaveListSync.HostSlots;
            string selected = HostSaveListSync.HostSelectedFilename;
            bool hostHasSelection = selected != null;

            // Reflect "waiting for host" state in the title.
            if (titleLabel != null)
            {
                titleLabel.text = (entries == null || entries.Count == 0)
                    ? "Waiting for host's save list..."
                    : "Host's save slot";
            }

            if (entries != null)
            {
                foreach (var e in entries)
                {
                    CreateMirrorSlotItem(
                        e,
                        hostHasSelection &&
                        string.Equals(e.Filename, selected, System.StringComparison.Ordinal));
                }
            }

            if (slotsTable != null) slotsTable.Reposition();
            if (scrollView != null) scrollView.ResetPosition();
            OnSlotsChanged?.Invoke();

            CoopMod.Logger.LogInfo(
                $"[SaveSelectorPanel] Built {slotItems.Count} mirrored host slot(s), " +
                $"selected='{selected ?? "<none>"}'");
        }

        private void ClearSlots()
        {
            foreach (var slot in slotItems)
            {
                if (slot != null)
                {
                    slot.SetActive(false);
                    Object.Destroy(slot);
                }
            }
            slotItems.Clear();
        }
        
        private void OnSaveSlotsLoaded(List<SaveSlotData> slots)
        {
            // In the multiplayer lobby, only show coop saves. Do not mutate the list
            // owned by PlatformSpecific: vanilla also uses it to allocate filenames.
            int sourceCount = slots != null ? slots.Count : 0;
            slots = (slots ?? new List<SaveSlotData>())
                .Where(GraveyardKeeperCoop.Patches.MainMenuPatches.IsMultiplayerSave)
                .ToList();
            CoopMod.Logger.LogInfo($"Loaded {slots.Count} of {sourceCount} save slots for multiplayer lobby (coop saves only)");

            // ReadSaveSlots returns fresh objects. Preserve selection by filename when a
            // different slot was deleted and the list is rebuilt.
            if (hasSelection && selectedSlot != null)
            {
                string selectedFilename = selectedSlot.filename_no_extension;
                selectedSlot = slots.Find(slot =>
                    slot != null &&
                    string.Equals(
                        slot.filename_no_extension,
                        selectedFilename,
                        System.StringComparison.Ordinal));
                if (selectedSlot == null)
                    hasSelection = false;
            }
            
            if (slotsContainer == null || slotTemplate == null)
            {
                CoopMod.Logger.LogError("Cannot create slots - missing container or template");
                return;
            }
            
            // Add "New Game" option first
            CreateSlotItem(null);
            
            // Add each save slot
            foreach (var slot in slots)
            {
                // Skip empty slots (game_time = 0)
                if (slot.game_time > 0.01f)
                {
                    CreateSlotItem(slot);
                }
            }

            UpdateSlotHighlights();
            
            // Reposition table/grid
            if (slotsTable != null)
            {
                slotsTable.Reposition();
            }
            
            // Reset scroll view position
            if (scrollView != null)
            {
                scrollView.ResetPosition();
            }

            OnSlotsChanged?.Invoke();
            
            CoopMod.Logger.LogInfo($"Created {slotItems.Count} save slot items");
        }
        
        private void CreateSlotItem(SaveSlotData slotData)
        {
            if (slotTemplate == null)
            {
                CoopMod.Logger.LogError("No slot template available!");
                return;
            }
            
            // Clone the save slot template (this is already a properly configured SaveSlotGUI)
            GameObject slotObj = Object.Instantiate(slotTemplate, slotsContainer.transform);
            slotObj.name = slotData != null ? $"SaveSlot_{slotData.filename_no_extension}" : "NewGameSlot";
            slotObj.layer = 13;
            slotObj.transform.localScale = Vector3.one;
            Vector3 slotPosition = slotObj.transform.localPosition;
            slotPosition.x = 0f;
            slotObj.transform.localPosition = slotPosition;
            slotObj.SetActive(true);
            
            // Get the SaveSlotGUI component and configure it
            var saveSlotGUI = slotObj.GetComponent<SaveSlotGUI>();
            UIWidget selectionFrame = null;
            GameObject deleteButtonObject = null;
            if (saveSlotGUI != null)
            {
                // Disable the SaveSlotGUI component to prevent auto-loading
                // but keep it for now to use Show() method
                saveSlotGUI.Show(slotData);
                
                // Hide the gamepad_frame initially (we use it for selection highlighting)
                if (saveSlotGUI.gamepad_frame != null)
                {
                    selectionFrame = saveSlotGUI.gamepad_frame;
                    saveSlotGUI.gamepad_frame.gameObject.SetActive(false);
                    BindSelectionFrameToRow(slotObj, selectionFrame, saveSlotGUI.back);
                }

                deleteButtonObject = saveSlotGUI.delete_button;
                
                // NOW destroy it so it doesn't handle clicks
                Object.Destroy(saveSlotGUI);
                
                // Host slots keep a replacement delete action. New Game and mirrored
                // client slots never expose deletion.
                if (deleteButtonObject != null)
                {
                    deleteButtonObject.SetActive(slotData != null && !readOnlyMirrorMode);
                    if (slotData != null && !readOnlyMirrorMode)
                        ConfigureDeleteButton(deleteButtonObject, slotData);
                }
                
                // Disable default slot buttons while preserving the rebound delete button.
                var uiButtons = slotObj.GetComponentsInChildren<UIButton>(true);
                foreach (var btn in uiButtons)
                {
                    if (deleteButtonObject != null && slotData != null && !readOnlyMirrorMode &&
                        (btn.gameObject == deleteButtonObject ||
                         btn.transform.IsChildOf(deleteButtonObject.transform)))
                    {
                        continue;
                    }
                    Object.Destroy(btn);
                }
                
                CoopMod.Logger.LogInfo($"Configured SaveSlotGUI for: {(slotData != null ? slotData.real_time : "New Game")}");
            }
            else
            {
                CoopMod.Logger.LogWarning("SaveSlotGUI component not found on cloned slot");
                // Fallback: manually update labels
                UpdateSlotLabels(slotObj, slotData);

                deleteButtonObject = FindDeleteButton(slotObj);
                if (deleteButtonObject != null)
                {
                    deleteButtonObject.SetActive(slotData != null && !readOnlyMirrorMode);
                    if (slotData != null && !readOnlyMirrorMode)
                        ConfigureDeleteButton(deleteButtonObject, slotData);
                }
            }
            
            // Add click handler
            AddClickHandler(slotObj, slotData, deleteButtonObject, selectionFrame);
            AddControllerHandler(slotObj, slotData, selectionFrame);
            
            slotItems.Add(slotObj);
        }

        private void AddControllerHandler(GameObject slotObj, SaveSlotData slotData, UIWidget selectionFrame)
        {
            foreach (var childItem in slotObj.GetComponentsInChildren<GamepadNavigationItem>(true))
            {
                if (childItem.gameObject != slotObj)
                    Object.DestroyImmediate(childItem);
            }

            var navigationItem = slotObj.GetComponent<GamepadNavigationItem>() ??
                                 slotObj.AddComponent<GamepadNavigationItem>();
            navigationItem.active = true;
            navigationItem.focus_frame = selectionFrame != null ? selectionFrame.gameObject : null;
            navigationItem.SetCallbacks(
                null,
                UpdateSlotHighlights,
                () => OnSlotClicked(slotData));
        }

        /// <summary>
        /// Client-only variant: render a host-mirrored save slot. No click handler,
        /// no delete button, no "New Game" entry, and if this slot matches the host's
        /// current selection we keep the gamepad_frame highlight visible.
        /// </summary>
        private void CreateMirrorSlotItem(HostSaveEntryWire entry, bool isHostSelected)
        {
            if (slotTemplate == null) return;

            GameObject slotObj = Object.Instantiate(slotTemplate, slotsContainer.transform);
            slotObj.name = $"MirroredSaveSlot_{entry.Filename}";
            slotObj.layer = 13;
            slotObj.transform.localScale = Vector3.one;
            Vector3 slotPosition = slotObj.transform.localPosition;
            slotPosition.x = 0f;
            slotObj.transform.localPosition = slotPosition;
            slotObj.SetActive(true);

            var saveSlotGUI = slotObj.GetComponent<SaveSlotGUI>();
            if (saveSlotGUI != null)
            {
                if (string.IsNullOrEmpty(entry.Filename))
                {
                    saveSlotGUI.Show(null);
                }
                else
                {
                    // Build a throwaway SaveSlotData purely to drive SaveSlotGUI.Show() labels.
                    // We leave linked_save blank so nothing tries to load it.
                    var fakeSlot = new SaveSlotData
                    {
                        filename_no_extension = entry.Filename,
                        real_time = entry.RealTime ?? "",
                        stats = entry.Stats ?? "",
                        game_time = entry.GameTime,
                        version = entry.Version,
                    };
                    saveSlotGUI.Show(fakeSlot);
                }

                // Show the highlight frame only on the host's currently-selected slot.
                if (saveSlotGUI.gamepad_frame != null)
                {
                    BindSelectionFrameToRow(
                        slotObj,
                        saveSlotGUI.gamepad_frame,
                        saveSlotGUI.back);
                    saveSlotGUI.gamepad_frame.gameObject.SetActive(isHostSelected);
                }

                // Kill the GUI component so it cannot auto-load.
                Object.Destroy(saveSlotGUI);

                // No delete button in mirror view.
                var deleteButton = slotObj.transform.Find("delete_button");
                if (deleteButton != null) deleteButton.gameObject.SetActive(false);

                // Strip all UIButtons so there is zero interactive behavior.
                foreach (var btn in slotObj.GetComponentsInChildren<UIButton>(true))
                {
                    Object.Destroy(btn);
                }

                // Also strip any BoxCollider so it can't absorb mouse events.
                foreach (var box in slotObj.GetComponentsInChildren<BoxCollider>(true))
                {
                    Object.Destroy(box);
                }
            }
            else
            {
                // Fallback - manually fill labels if SaveSlotGUI isn't present.
                UpdateMirrorSlotLabels(slotObj, entry);
            }

            // Mirrored client slots display the host's choice and are deliberately
            // not controller-selectable on the client.
            foreach (var navigationItem in slotObj.GetComponentsInChildren<GamepadNavigationItem>(true))
            {
                Object.DestroyImmediate(navigationItem);
            }

            // Intentionally NOT calling AddClickHandler - mirror view is read-only.
            slotItems.Add(slotObj);
        }

        private static void BindSelectionFrameToRow(
            GameObject slotObj,
            UIWidget selectionFrame,
            UIWidget rowBackground)
        {
            if (slotObj == null || selectionFrame == null || rowBackground == null)
                return;

            // The cloned native frame can retain anchors from the single-player
            // menu. Those anchors are evaluated against the old menu geometry and
            // visibly offset the highlight after the lobby panel is scaled.
            if (selectionFrame.leftAnchor != null) selectionFrame.leftAnchor.target = null;
            if (selectionFrame.rightAnchor != null) selectionFrame.rightAnchor.target = null;
            if (selectionFrame.topAnchor != null) selectionFrame.topAnchor.target = null;
            if (selectionFrame.bottomAnchor != null) selectionFrame.bottomAnchor.target = null;

            var geometryDriver = slotObj.GetComponent<ControllerFocusFrameDriver>() ??
                                 slotObj.AddComponent<ControllerFocusFrameDriver>();
            // The native selection sprite includes transparent edge pixels. Match
            // the padding used by the other controller focus frames so its visible
            // border surrounds the complete save-row background.
            geometryDriver.Initialize(
                selectionFrame,
                0,
                0,
                rowBackground,
                SelectionFrameHorizontalPadding,
                SelectionFrameVerticalPadding);
        }

        private void UpdateMirrorSlotLabels(GameObject slotObj, HostSaveEntryWire entry)
        {
            foreach (var label in slotObj.GetComponentsInChildren<UILabel>(true))
            {
                if (label.name == "slot_name" || label.name.Contains("name"))
                {
                    label.text = "";
                }
                else if (label.name == "txt_realtime" || label.name.Contains("realtime"))
                {
                    label.text = entry.RealTime ?? "";
                }
                else if (label.name == "txt_stats" || label.name.Contains("stats"))
                {
                    label.text = entry.Stats ?? "";
                }
                else if (label.name == "txt_descr" || label.name.Contains("descr"))
                {
                    float gameTime = Mathf.Max(0f, entry.GameTime - 1.5f);
                    label.text = $"[c][F0A33E]Play time: {gameTime:0.0} hours[-][/c]";
                }
            }
        }
        
        private void AddClickHandler(
            GameObject slotObj,
            SaveSlotData slotData,
            GameObject deleteButton,
            UIWidget selectionFrame)
        {
            // Add a component to handle clicks
            var clickHandler = slotObj.AddComponent<SaveSlotClickHandler>();
            clickHandler.Initialize(slotData, () => OnSlotClicked(slotData), deleteButton, selectionFrame);
        }

        private static GameObject FindDeleteButton(GameObject slotObj)
        {
            if (slotObj == null)
                return null;

            foreach (var child in slotObj.GetComponentsInChildren<Transform>(true))
            {
                if (child != null &&
                    string.Equals(child.name, "delete_button", System.StringComparison.OrdinalIgnoreCase))
                {
                    return child.gameObject;
                }
            }

            return null;
        }

        private void ConfigureDeleteButton(GameObject deleteButton, SaveSlotData slotData)
        {
            if (deleteButton == null || slotData == null)
                return;

            // Remove callbacks targeting the SaveSlotGUI component that this panel replaces.
            var eventTargets = new HashSet<GameObject> { deleteButton };
            foreach (var button in deleteButton.GetComponentsInChildren<UIButton>(true))
            {
                button.onClick.Clear();
                button.isEnabled = true;
                eventTargets.Add(button.gameObject);
            }
            foreach (var trigger in deleteButton.GetComponentsInChildren<UIEventTrigger>(true))
            {
                trigger.onClick.Clear();
                eventTargets.Add(trigger.gameObject);
            }
            foreach (var collider in deleteButton.GetComponentsInChildren<BoxCollider>(true))
                eventTargets.Add(collider.gameObject);
            foreach (var collider in deleteButton.GetComponentsInChildren<BoxCollider2D>(true))
                eventTargets.Add(collider.gameObject);
            foreach (var message in deleteButton.GetComponentsInChildren<UIButtonMessage>(true))
                Object.Destroy(message);
            foreach (var forwarder in deleteButton.GetComponentsInChildren<UIForwardEvents>(true))
                forwarder.onClick = false;

            foreach (GameObject eventTarget in eventTargets)
            {
                UIEventListener listener = UIEventListener.Get(eventTarget);
                listener.onClick = go => RequestDeleteSlot(slotData);
            }
        }

        private void RequestDeleteSlot(SaveSlotData slotData)
        {
            if (readOnlyMirrorMode || slotData == null || deleteInProgress || deleteConfirmationOpen)
                return;
            if (!GraveyardKeeperCoop.Patches.MainMenuPatches.IsMultiplayerSave(slotData))
            {
                CoopMod.Logger.LogWarning("[SaveSelectorPanel] Refused deletion of a non-co-op save");
                return;
            }

            var dialog = GUIElements.me?.dialog;
            if (dialog == null)
            {
                CoopMod.Logger.LogError("[SaveSelectorPanel] Cannot confirm save deletion because the dialog UI is unavailable");
                return;
            }

            deleteConfirmationOpen = true;
            GJCommons.VoidDelegate clearConfirmation = () => deleteConfirmationOpen = false;
            try
            {
                dialog.OpenYesNo(
                    GJL.L("delete_slot"),
                    new GJCommons.VoidDelegate(() =>
                    {
                        deleteConfirmationOpen = false;
                        DeleteSlot(slotData);
                    }),
                    clearConfirmation,
                    clearConfirmation);
            }
            catch (System.Exception ex)
            {
                deleteConfirmationOpen = false;
                CoopMod.Logger.LogError($"[SaveSelectorPanel] Failed to open delete confirmation: {ex}");
            }
        }

        private void DeleteSlot(SaveSlotData slotData)
        {
            if (readOnlyMirrorMode || slotData == null || deleteInProgress)
                return;
            if (!GraveyardKeeperCoop.Patches.MainMenuPatches.IsMultiplayerSave(slotData))
            {
                CoopMod.Logger.LogWarning("[SaveSelectorPanel] Save no longer qualifies as co-op; deletion cancelled");
                return;
            }

            deleteInProgress = true;
            string filename = slotData.filename_no_extension ?? string.Empty;

            try
            {
                PlatformSpecific.DeleteSlot(slotData, () =>
                {
                    try
                    {
                        GraveyardKeeperCoop.Multiplayer.MultiplayerSavePositions.DeleteSidecar(filename);

                        bool deletedSelectedSlot =
                            hasSelection && selectedSlot != null &&
                            string.Equals(
                                selectedSlot.filename_no_extension,
                                filename,
                                System.StringComparison.Ordinal);
                        if (deletedSelectedSlot)
                            OnSlotClicked(null);

                        CoopMod.Logger.LogInfo($"[SaveSelectorPanel] Deleted multiplayer save '{filename}'");
                        LoadSaveSlots();
                        HostSaveListSync.HostBroadcastCurrentList();
                    }
                    finally
                    {
                        deleteInProgress = false;
                    }
                });
            }
            catch (System.Exception ex)
            {
                deleteInProgress = false;
                CoopMod.Logger.LogError($"[SaveSelectorPanel] Failed to delete multiplayer save '{filename}': {ex}");
            }
        }
        
        private void UpdateSlotLabels(GameObject slotObj, SaveSlotData slotData)
        {
            // Fallback label update if SaveSlotGUI isn't working
            var labels = slotObj.GetComponentsInChildren<UILabel>(true);
            
            foreach (var label in labels)
            {
                if (label.name == "slot_name" || label.name.Contains("name"))
                {
                    label.text = slotData == null ? "NEW GAME" : "";
                }
                else if (label.name == "txt_realtime" || label.name.Contains("realtime"))
                {
                    label.text = slotData != null ? slotData.real_time : "";
                }
                else if (label.name == "txt_stats" || label.name.Contains("stats"))
                {
                    label.text = slotData != null ? slotData.stats : "";
                }
                else if (label.name == "txt_descr" || label.name.Contains("descr"))
                {
                    if (slotData != null)
                    {
                        float gameTime = Mathf.Max(0f, slotData.game_time - 1.5f);
                        label.text = $"[c][F0A33E]Play time: {gameTime:0.0} hours[-][/c]";
                    }
                    else
                    {
                        label.text = "";
                    }
                }
            }
        }
        
        private void OnSlotClicked(SaveSlotData slotData)
        {
            selectedSlot = slotData;
            hasSelection = true;
            UpdateSlotHighlights();

            // Notify listeners
            OnSlotSelected?.Invoke(slotData);

            string slotName = slotData != null ? slotData.real_time : "New Game";
            CoopMod.Logger.LogInfo($"Save slot selected: {slotName}");
        }

        private void UpdateSlotHighlights()
        {
            // Highlight selected slot using gamepad_frame (like the original game does)
            foreach (var slotObj in slotItems)
            {
                var clickHandler = slotObj.GetComponent<SaveSlotClickHandler>();
                if (clickHandler == null) continue;
                
                bool isSelected = hasSelection && clickHandler.SlotData == selectedSlot;
                clickHandler.SetSelected(isSelected);
            }
        }
        
        public SaveSlotData GetSelectedSlot()
        {
            return selectedSlot;
        }

        public bool HasSelection()
        {
            return hasSelection;
        }

        public GamepadNavigationItem[] GetControllerNavigationItems()
        {
            var result = new List<GamepadNavigationItem>();
            foreach (var slot in slotItems)
            {
                if (slot == null)
                    continue;
                var item = slot.GetComponent<GamepadNavigationItem>();
                if (item != null)
                    result.Add(item);
            }
            return result.ToArray();
        }

        public void ApplyLayout(Vector3 localPosition, Vector3 localScale)
        {
            transform.localPosition = localPosition;
            transform.localScale = localScale;

            if (dialogFrame != null)
            {
                dialogFrame.transform.localPosition = Vector3.zero;
                dialogFrame.transform.localScale = Vector3.one;
            }
        }
        
        public void SetActive(bool active)
        {
            if (dialogFrame != null)
            {
                dialogFrame.SetActive(active);
            }
            else
            {
                gameObject.SetActive(active);
            }
            
            // Refresh save slots when becoming active (in case saves were deleted/added)
            if (active)
            {
                RefreshSaveSlots();
            }
        }
        
        /// <summary>
        /// Refresh the save slots list (reload from disk)
        /// </summary>
        public void RefreshSaveSlots()
        {
            LoadSaveSlots();
        }
    }
    
    /// <summary>
    /// Click handler for save slot items
    /// </summary>
    public class SaveSlotClickHandler : MonoBehaviour
    {
        private SaveSlotData slotData;
        private System.Action onClicked;
        private UIWidget backgroundWidget;
        private UIWidget selectionFrame;
        private UIWidget[] deleteWidgets;
        private Camera uiCamera;
        
        public SaveSlotData SlotData => slotData;
        
        public void Initialize(
            SaveSlotData data,
            System.Action clickCallback,
            GameObject deleteButton = null,
            UIWidget exactSelectionFrame = null)
        {
            slotData = data;
            onClicked = clickCallback;
            selectionFrame = exactSelectionFrame;
            
            // Find background widget for bounds detection
            backgroundWidget = GetComponentInChildren<UIWidget>();
            deleteWidgets = deleteButton != null
                ? deleteButton.GetComponentsInChildren<UIWidget>(true)
                : new UIWidget[0];
            
            // Find UI camera
            uiCamera = NGUITools.FindCameraForLayer(gameObject.layer);
            
            CoopMod.Logger.LogInfo($"SaveSlotClickHandler initialized: {(data != null ? data.real_time : "New Game")}");
        }

        public void SetSelected(bool selected)
        {
            if (selectionFrame != null)
                selectionFrame.gameObject.SetActive(selected);
        }
        
        private void Update()
        {
            // Check for mouse clicks
            if (Input.GetMouseButtonDown(0) && uiCamera != null && backgroundWidget != null)
            {
                Vector3 mousePos = Input.mousePosition;

                // The delete X owns its click and must not also select the slot below it.
                for (int i = 0; i < deleteWidgets.Length; i++)
                {
                    if (deleteWidgets[i] != null && IsWithinWidget(deleteWidgets[i], mousePos))
                        return;
                }
                
                if (IsWithinWidget(backgroundWidget, mousePos))
                {
                    CoopMod.Logger.LogInfo($"SaveSlot clicked: {(slotData != null ? slotData.real_time : "New Game")}");
                    onClicked?.Invoke();
                }
            }
        }

        private bool IsWithinWidget(UIWidget widget, Vector3 screenPosition)
        {
            if (widget == null || uiCamera == null)
                return false;

            Vector3[] worldCorners = widget.worldCorners;
            Vector2 min = uiCamera.WorldToScreenPoint(worldCorners[0]);
            Vector2 max = uiCamera.WorldToScreenPoint(worldCorners[2]);

            float minX = Mathf.Min(min.x, max.x);
            float maxX = Mathf.Max(min.x, max.x);
            float minY = Mathf.Min(min.y, max.y);
            float maxY = Mathf.Max(min.y, max.y);

            return screenPosition.x >= minX && screenPosition.x <= maxX &&
                   screenPosition.y >= minY && screenPosition.y <= maxY;
        }
    }
}

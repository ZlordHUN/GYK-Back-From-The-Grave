using HarmonyLib;
using UnityEngine;
using GraveyardKeeperCoop.UI;
using GraveyardKeeperCoopMod.Utils;
using GraveyardKeeperCoop.Features;
using GraveyardKeeperCoop.Network;
using GraveyardKeeperCoop.LocalCoop;
using System.Collections;
using System.Collections.Generic;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches InGameMenuGUI to add Save Game button and Multiplayer button
    /// </summary>
    [HarmonyPatch(typeof(InGameMenuGUI))]
    public class InGameMenuPatches
    {
        private static GameObject saveGameButton = null;
        private static GameObject loadGameButton = null;

        static InGameMenuPatches()
        {
            CoopMod.Logger.LogInfo("========================================");
            CoopMod.Logger.LogInfo("InGameMenuPatches CLASS LOADED");
            CoopMod.Logger.LogInfo("========================================");
        }

        /// <summary>
        /// Patch the Hide method to ensure HUD is shown when unpausing
        /// </summary>
        [HarmonyPatch("Hide")]
        [HarmonyPostfix]
        public static void Hide_Postfix(InGameMenuGUI __instance, bool play_sound = true)
        {
            // Reset button tracking when menu closes
            saveGameButton = null;
            loadGameButton = null;
            
            // When hiding the pause menu (unpausing), always show the HUD
            // This handles the case where we came from save/load menu which hid the HUD
            CoopMod.Logger.LogInfo($"[InGameMenu] Hide_Postfix called - game_started={MainGame.game_started}, paused={MainGame.paused}");
            
            if (!MainGame.paused)
            {
                // Restore game_started flag if it was set to false by save menu
                if (!MainGame.game_started)
                {
                    MainGame.game_started = true;
                    CoopMod.Logger.LogInfo("[InGameMenu] Restored game_started to true");
                }
                
                GUIElements.me.hud.Open();
                CoopMod.Logger.LogInfo("[InGameMenu] Showed HUD after closing pause menu");
            }
        }
        
        // Note: Multiplayer button disabled in pause menu - only shown in main menu
        
        /// <summary>
        /// After the in-game menu opens, add our Save Game and Load Game buttons
        /// </summary>
        [HarmonyPatch("Open")]
        [HarmonyPostfix]
        public static void Open_Postfix(InGameMenuGUI __instance)
        {
            CoopMod.Logger.LogInfo("========================================");
            CoopMod.Logger.LogInfo("InGameMenuGUI.Open_Postfix CALLED!");
            CoopMod.Logger.LogInfo("========================================");
            
            // Add both buttons at once to avoid double-shifting
            AddBothButtons(__instance);
            
            // Start coroutine to refresh UI on next frame
            __instance.StartCoroutine(RefreshUINextFrame(__instance));
        }
        
        /// <summary>
        /// Coroutine to refresh UI elements on the next frame
        /// </summary>
        private static IEnumerator RefreshUINextFrame(InGameMenuGUI menu)
        {
            // Wait for end of frame to ensure all layout is calculated
            yield return new WaitForEndOfFrame();
            
            CoopMod.Logger.LogInfo("[InGameMenu] Refreshing UI on next frame...");
            
            // Re-apply button labels in case localization overwrote them
            if (saveGameButton != null)
            {
                var saveLabels = saveGameButton.GetComponentsInChildren<UILabel>();
                foreach (var label in saveLabels)
                {
                    label.text = "Save Game";
                }
            }
            if (loadGameButton != null)
            {
                var loadLabels = loadGameButton.GetComponentsInChildren<UILabel>();
                foreach (var label in loadLabels)
                {
                    label.text = "Load Game";
                }
            }
            
            // Force all panels to refresh
            var allPanels = menu.GetComponentsInChildren<UIPanel>(true);
            foreach (var panel in allPanels)
            {
                panel.Refresh();
            }
            
            // Force all widgets to update
            var allWidgets = menu.GetComponentsInChildren<UIWidget>(true);
            foreach (var widget in allWidgets)
            {
                widget.MarkAsChanged();
            }
            
            CoopMod.Logger.LogInfo("[InGameMenu] UI refresh complete");
        }

        /// <summary>
        /// Adds both Save Game and Load Game buttons to the pause menu
        /// </summary>
        private static void AddBothButtons(InGameMenuGUI menu)
        {
            try
            {
                // Decide up-front whether the Load button should exist for this session.
                // Clients in online co-op are not allowed to load (host-only policy).
                var onlineCoop = GraveyardKeeperCoop.Network.OnlineCoopManager.Instance;
                bool showLoadButton = onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled || onlineCoop.IsHost;

                // Check if buttons already exist in THIS menu instance.
                // Don't rely on static references as they may point to destroyed objects.
                Transform existingSave = menu.transform.Find("btn_save_game");
                Transform existingLoad = menu.transform.Find("btn_load_game");

                // Also search in children (deep)
                if (existingSave == null)
                {
                    foreach (Transform child in menu.GetComponentsInChildren<Transform>(true))
                    {
                        if (child.name == "btn_save_game")
                        {
                            existingSave = child;
                            break;
                        }
                    }
                }
                if (existingLoad == null)
                {
                    foreach (Transform child in menu.GetComponentsInChildren<Transform>(true))
                    {
                        if (child.name == "btn_load_game")
                        {
                            existingLoad = child;
                            break;
                        }
                    }
                }

                // If every button we INTEND to create already exists, skip creation.
                // For a client (!showLoadButton) we only need existingSave — we never
                // created a Load button, so the old "require both" check was broken
                // and a new Save clone was added on every pause-menu open.
                bool allPresent = existingSave != null && (!showLoadButton || existingLoad != null);
                if (allPresent)
                {
                    saveGameButton = existingSave.gameObject;
                    loadGameButton = existingLoad != null ? existingLoad.gameObject : null;
                    CoopMod.Logger.LogInfo($"[InGameMenu] Buttons already exist, skipping creation (showLoadButton={showLoadButton})");
                    return;
                }

                // If Save exists but Load is missing on the host, or vice versa (shouldn't
                // normally happen) — destroy the partial leftovers so we rebuild cleanly
                // and don't stack duplicates.
                if (existingSave != null)
                {
                    Object.DestroyImmediate(existingSave.gameObject);
                    existingSave = null;
                }
                if (existingLoad != null)
                {
                    Object.DestroyImmediate(existingLoad.gameObject);
                    existingLoad = null;
                }

                CoopMod.Logger.LogInfo("[InGameMenu] Adding Save and Load Game buttons...");

                if (!showLoadButton)
                {
                    CoopMod.Logger.LogInfo("[InGameMenu] Client in online co-op — hiding Load Game button (host-only)");
                }

                // Find all MenuItemGUI components
                var rawMenuItems = menu.GetComponentsInChildren<MenuItemGUI>(true);
                var filteredMenuItems = new List<MenuItemGUI>(rawMenuItems.Length);
                foreach (var item in rawMenuItems)
                {
                    if (item == null || item.gameObject == null)
                        continue;
                    if (item.name == "btn_save_game" || item.name == "btn_load_game")
                        continue;
                    filteredMenuItems.Add(item);
                }
                var menuItems = filteredMenuItems.ToArray();
                CoopMod.Logger.LogInfo($"[InGameMenu] Found {menuItems.Length} menu items");

                if (menuItems.Length == 0)
                {
                    CoopMod.Logger.LogError("[InGameMenu] No menu items found!");
                    return;
                }

                // Use the first menu item (Continue) as template
                GameObject continueButton = menuItems[0].gameObject;
                Transform parent = continueButton.transform.parent;
                
                // Get Continue button position
                Vector3 continuePos = continueButton.transform.localPosition;
                CoopMod.Logger.LogInfo($"[InGameMenu] Continue button at: {continuePos}");
                
                // FIRST: Shift existing buttons (except Continue) down to make room for Save (+Load if host)
                int newButtonCount = showLoadButton ? 2 : 1;
                float shiftAmount = 35f * newButtonCount;
                CoopMod.Logger.LogInfo($"[InGameMenu] Shifting existing buttons down by {shiftAmount} (newButtonCount={newButtonCount})...");
                for (int i = 1; i < menuItems.Length; i++)
                {
                    Vector3 pos = menuItems[i].transform.localPosition;
                    Vector3 oldPos = pos;
                    pos.y -= shiftAmount;
                    menuItems[i].transform.localPosition = pos;
                    CoopMod.Logger.LogInfo($"[InGameMenu] Shifted button {i} ({menuItems[i].name}) from {oldPos} to {pos}");
                }

                // NOW: Clone Save Game button
                saveGameButton = Object.Instantiate(continueButton, parent);
                saveGameButton.name = "btn_save_game";
                // Don't change sibling index yet - add at end
                
                // Position Save Game button below Continue
                Vector3 savePos = continuePos;
                savePos.y -= 35f;
                saveGameButton.transform.localPosition = savePos;
                CoopMod.Logger.LogInfo($"[InGameMenu] Save Game positioned at: {savePos}");

                // Remove LocalizedLabel components that might override our text
                // Use DestroyImmediate to ensure they're gone before we set text
                var saveLocalizedLabels = saveGameButton.GetComponentsInChildren<LocalizedLabel>();
                foreach (var localizedLabel in saveLocalizedLabels)
                {
                    Object.DestroyImmediate(localizedLabel);
                }
                CoopMod.Logger.LogInfo($"[InGameMenu] Removed {saveLocalizedLabels.Length} LocalizedLabel components from Save Game button");

                // Update Save Game button labels
                var saveLabels = saveGameButton.GetComponentsInChildren<UILabel>();
                foreach (var label in saveLabels)
                {
                    label.text = "Save Game";
                }
                CoopMod.Logger.LogInfo($"[InGameMenu] Updated {saveLabels.Length} labels to 'Save Game'");
                
                // Also update the MenuItemGUI locale_token to prevent re-localization
                var saveMenuItemGUI = saveGameButton.GetComponent<MenuItemGUI>();
                if (saveMenuItemGUI != null)
                {
                    saveMenuItemGUI.locale_token = ""; // Clear locale token
                    
                    // Initialize the MenuItemGUI with the menu (required for gamepad navigation)
                    saveMenuItemGUI.Init(menu);
                    
                    // CRITICAL: Reset the visual state - the cloned button may have copied the highlighted state
                    saveMenuItemGUI.Show();
                    
                    saveMenuItemGUI.on_pressed = new EventDelegate(() =>
                    {
                        OnSaveGameButtonPressed(menu);
                    });
                }

                // Clone Load Game button (host-only in online co-op)
                if (!showLoadButton)
                {
                    loadGameButton = null;
                    CoopMod.Logger.LogInfo("[InGameMenu] Skipped Load Game button creation (client-side restriction)");
                }
                else
                {
                loadGameButton = Object.Instantiate(continueButton, parent);
                loadGameButton.name = "btn_load_game";
                // Don't change sibling index yet - add at end
                
                // Position Load Game button below Save Game
                Vector3 loadPos = savePos;
                loadPos.y -= 35f;
                loadGameButton.transform.localPosition = loadPos;
                CoopMod.Logger.LogInfo($"[InGameMenu] Load Game positioned at: {loadPos}");

                // Remove LocalizedLabel components that might override our text
                // Use DestroyImmediate to ensure they're gone before we set text
                var loadLocalizedLabels = loadGameButton.GetComponentsInChildren<LocalizedLabel>();
                foreach (var localizedLabel in loadLocalizedLabels)
                {
                    Object.DestroyImmediate(localizedLabel);
                }
                CoopMod.Logger.LogInfo($"[InGameMenu] Removed {loadLocalizedLabels.Length} LocalizedLabel components from Load Game button");

                // Update Load Game button labels
                var loadLabels = loadGameButton.GetComponentsInChildren<UILabel>();
                foreach (var label in loadLabels)
                {
                    label.text = "Load Game";
                }
                CoopMod.Logger.LogInfo($"[InGameMenu] Updated {loadLabels.Length} labels to 'Load Game'");

                // Hook up Load Game click event
                var loadMenuItemGUI = loadGameButton.GetComponent<MenuItemGUI>();
                if (loadMenuItemGUI != null)
                {
                    loadMenuItemGUI.locale_token = ""; // Clear locale token
                    
                    // Initialize the MenuItemGUI with the menu (required for gamepad navigation)
                    loadMenuItemGUI.Init(menu);
                    
                    // CRITICAL: Reset the visual state - the cloned button may have copied the highlighted state
                    loadMenuItemGUI.Show();
                    
                    loadMenuItemGUI.on_pressed = new EventDelegate(() =>
                    {
                        OnLoadGameButtonPressed(menu);
                    });
                }
                } // end if (showLoadButton)
                
                // CRITICAL: Clear any stale navigation references from cloned GamepadNavigationItems
                // When we clone buttons, the GamepadNavigationItem copies the original's custom direction items
                // These stale references break navigation. We need to clear them so ReinitItems can work properly.
                var saveNavItem = saveGameButton.GetComponent<GamepadNavigationItem>();
                var loadNavItem = loadGameButton != null ? loadGameButton.GetComponent<GamepadNavigationItem>() : null;
                
                if (saveNavItem != null)
                {
                    saveNavItem.SetCustomDirectionItem(null, Direction.Up);
                    saveNavItem.SetCustomDirectionItem(null, Direction.Down);
                    saveNavItem.SetCustomDirectionItem(null, Direction.Left);
                    saveNavItem.SetCustomDirectionItem(null, Direction.Right);
                    
                    // CRITICAL: Deactivate the cloned focus frame - it may have copied the active state
                    if (saveNavItem.focus_frame != null)
                    {
                        saveNavItem.focus_frame.SetActive(false);
                        CoopMod.Logger.LogInfo("[InGameMenu] Deactivated Save button's focus frame");
                    }
                    
                    CoopMod.Logger.LogInfo("[InGameMenu] Cleared Save button's stale navigation references");
                }
                
                if (loadNavItem != null)
                {
                    loadNavItem.SetCustomDirectionItem(null, Direction.Up);
                    loadNavItem.SetCustomDirectionItem(null, Direction.Down);
                    loadNavItem.SetCustomDirectionItem(null, Direction.Left);
                    loadNavItem.SetCustomDirectionItem(null, Direction.Right);
                    
                    // CRITICAL: Deactivate the cloned focus frame - it may have copied the active state
                    if (loadNavItem.focus_frame != null)
                    {
                        loadNavItem.focus_frame.SetActive(false);
                        CoopMod.Logger.LogInfo("[InGameMenu] Deactivated Load button's focus frame");
                    }
                    
                    CoopMod.Logger.LogInfo("[InGameMenu] Cleared Load button's stale navigation references");
                }
                
                // Also clear navigation references on ALL existing buttons to ensure clean state
                // This prevents any confusion from the original menu setup
                foreach (var existingItem in menuItems)
                {
                    var existingNavItem = existingItem.GetComponent<GamepadNavigationItem>();
                    if (existingNavItem != null)
                    {
                        existingNavItem.SetCustomDirectionItem(null, Direction.Up);
                        existingNavItem.SetCustomDirectionItem(null, Direction.Down);
                        existingNavItem.SetCustomDirectionItem(null, Direction.Left);
                        existingNavItem.SetCustomDirectionItem(null, Direction.Right);
                    }
                }
                CoopMod.Logger.LogInfo("[InGameMenu] Cleared all existing buttons' navigation references");
                
                // NOW set the sibling indices to reorder in hierarchy
                // The parent has multiple children, we want: Continue(0) -> Save(1) -> Load(2) -> Others...
                // Since our buttons were added at the end, we need to move them up
                int continueIndex = continueButton.transform.GetSiblingIndex();
                CoopMod.Logger.LogInfo($"[InGameMenu] Continue is at sibling index: {continueIndex}");
                
                saveGameButton.transform.SetSiblingIndex(continueIndex + 1);
                if (loadGameButton != null)
                {
                    loadGameButton.transform.SetSiblingIndex(continueIndex + 2);
                }
                
                CoopMod.Logger.LogInfo($"[InGameMenu] Reordered buttons - Save at {continueIndex + 1}, Load at {(loadGameButton != null ? (continueIndex + 2).ToString() : "(hidden)")}");
                
                // Extend the background to fit the new buttons
                // Only extend UI2DSprite components (the visible backgrounds)
                // UIWidget is the component that UI2DSprite extends, so modifying the sprite modifies the widget too
                CoopMod.Logger.LogInfo("[InGameMenu] Extending backgrounds...");
                
                var sprites = menu.GetComponentsInChildren<UI2DSprite>(true);
                foreach (var sprite in sprites)
                {
                    // Find main background sprite - 'dark back' is typically 1160x740
                    if (sprite.name.Contains("dark back") || (sprite.width > 1000 && sprite.height > 500))
                    {
                        int oldHeight = sprite.height;
                        Vector3 oldPos = sprite.transform.localPosition;
                        
                        sprite.height += 70; // Add 70 pixels for the two new buttons
                        
                        // Shift the background down by half the extension to keep top edge fixed
                        Vector3 bgPos = sprite.transform.localPosition;
                        bgPos.y -= 35f; // Half of 70
                        sprite.transform.localPosition = bgPos;
                        
                        // Force the widget to mark as changed and update
                        sprite.MarkAsChanged();
                        
                        CoopMod.Logger.LogInfo($"[InGameMenu] Extended '{sprite.name}': {oldHeight}->{sprite.height}, pos {oldPos}->{bgPos}");
                        break; // Only extend one background
                    }
                }
                
                // Force layout recalculation
                var uiTable = menu.GetComponentInChildren<UITable>(true);
                if (uiTable != null)
                {
                    CoopMod.Logger.LogInfo("[InGameMenu] Found UITable, repositioning...");
                    uiTable.repositionNow = true;
                    uiTable.Reposition();
                }
                
                var uiGrid = menu.GetComponentInChildren<UIGrid>(true);
                if (uiGrid != null)
                {
                    CoopMod.Logger.LogInfo("[InGameMenu] Found UIGrid, repositioning...");
                    uiGrid.repositionNow = true;
                    uiGrid.Reposition();
                }
                
                // Force all widgets to update
                var allWidgets = menu.GetComponentsInChildren<UIWidget>(true);
                foreach (var widget in allWidgets)
                {
                    widget.MarkAsChanged();
                }
                CoopMod.Logger.LogInfo($"[InGameMenu] Marked {allWidgets.Length} widgets as changed for refresh");
                
                // Force all UIPanels to recalculate their clipping regions
                var allPanels = menu.GetComponentsInChildren<UIPanel>(true);
                foreach (var panel in allPanels)
                {
                    panel.Refresh();
                    CoopMod.Logger.LogInfo($"[InGameMenu] Refreshed panel: {panel.name}, clipping: {panel.clipping}");
                }
                
                // Call the menu's Reposition method to update the layout
                // This is what BaseMenuGUI.Open() calls, and we need to call it again after adding buttons
                var repositionMethod = menu.GetType().GetMethod("Reposition", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (repositionMethod != null)
                {
                    repositionMethod.Invoke(menu, null);
                    CoopMod.Logger.LogInfo("[InGameMenu] Called menu.Reposition()");
                }

                // CRITICAL: Re-initialize the menu's items array to include our new buttons
                // This is required for gamepad navigation to work
                var itemsField = typeof(BaseMenuGUI).GetField("items", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (itemsField != null)
                {
                    // Update the cached items array
                    var newItems = menu.GetComponentsInChildren<MenuItemGUI>(true);
                    itemsField.SetValue(menu, newItems);
                    CoopMod.Logger.LogInfo($"[InGameMenu] Updated items array with {newItems.Length} items");
                    
                    // Build a list of active items in order (by Y position, descending = top to bottom)
                    var activeItems = new List<MenuItemGUI>();
                    foreach (var item in newItems)
                    {
                        if (item.gameObject.activeSelf && item.gamepad_item != null)
                        {
                            activeItems.Add(item);
                        }
                    }
                    
                    // Sort by Y position descending (top = highest Y comes first)
                    activeItems.Sort((a, b) => b.transform.position.y.CompareTo(a.transform.position.y));
                    
                    CoopMod.Logger.LogInfo($"[InGameMenu] Active items sorted by Y position:");
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
                    CoopMod.Logger.LogInfo("[InGameMenu] Set up complete gamepad navigation chain with wrap-around");
                }
                
                // Reinitialize gamepad controller if using gamepad
                if (BaseGUI.for_gamepad && menu.gamepad_controller != null)
                {
                    menu.gamepad_controller.ReinitItems(true);
                    CoopMod.Logger.LogInfo("[InGameMenu] Reinitialized gamepad controller");
                }

                CoopMod.Logger.LogInfo("[InGameMenu] Save and Load Game buttons added successfully!");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[InGameMenu] Error adding buttons: {ex}");
            }
        }

        /// <summary>
        /// Called when Save Game button is pressed
        /// </summary>
        private static void OnSaveGameButtonPressed(InGameMenuGUI menu)
        {
            try
            {
                CoopMod.Logger.LogInfo("[InGameMenu] Save Game button pressed!");

                // Close the menu
                menu.Hide(true);

                // Set flags - capture game_started state NOW before it changes
                // IMPORTANT: Clear the opposite flag to prevent conflicts
                MainMenuPatches.IsSaveGameMode = true;
                MainMenuPatches.IsLoadGameMode = false;
                MainMenuPatches.WasInGame = MainGame.game_started;
                MainMenuPatches.IsMultiplayerSaveMode = (OnlineCoopManager.Instance != null && OnlineCoopManager.Instance.IsOnlineCoopEnabled)
                    || (LocalCoopManager.Instance != null && LocalCoopManager.Instance.IsLocalCoopEnabled);
                
                CoopMod.Logger.LogInfo($"[InGameMenu] Set flags: IsSaveGameMode={MainMenuPatches.IsSaveGameMode}, IsLoadGameMode={MainMenuPatches.IsLoadGameMode}, WasInGame={MainMenuPatches.WasInGame}, IsMultiplayerSaveMode={MainMenuPatches.IsMultiplayerSaveMode}");

                // Open the native save menu directly
                // This will show all save slots for saving over them
                CoopMod.Logger.LogInfo("[InGameMenu] Opening native save menu for saving...");
                GUIElements.me.saves.Open();
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[InGameMenu] Error opening save menu: {ex}");
            }
        }

        /// <summary>
        /// Called when Load Game button is pressed
        /// </summary>
        private static void OnLoadGameButtonPressed(InGameMenuGUI menu)
        {
            try
            {
                CoopMod.Logger.LogInfo("[InGameMenu] Load Game button pressed!");

                // Close the in-game menu
                menu.Hide(true);

                // Set flags - capture game_started state NOW before it changes
                // IMPORTANT: Clear the opposite flag to prevent conflicts
                MainMenuPatches.IsLoadGameMode = true;
                MainMenuPatches.IsSaveGameMode = false;
                MainMenuPatches.WasInGame = MainGame.game_started;
                MainMenuPatches.IsMultiplayerSaveMode = (OnlineCoopManager.Instance != null && OnlineCoopManager.Instance.IsOnlineCoopEnabled)
                    || (LocalCoopManager.Instance != null && LocalCoopManager.Instance.IsLocalCoopEnabled);
                
                CoopMod.Logger.LogInfo($"[InGameMenu] Set flags: IsLoadGameMode={MainMenuPatches.IsLoadGameMode}, IsSaveGameMode={MainMenuPatches.IsSaveGameMode}, WasInGame={MainMenuPatches.WasInGame}, IsMultiplayerSaveMode={MainMenuPatches.IsMultiplayerSaveMode}");

                // Open the native save menu directly for loading
                CoopMod.Logger.LogInfo("[InGameMenu] Opening native save menu for loading...");
                GUIElements.me.saves.Open();
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[InGameMenu] Error opening load game menu: {ex}");
            }
        }
        
        /// <summary>
        /// Old multiplayer button code - disabled
        /// NOTE: Disabled - Multiplayer button should only appear in main menu, not pause menu
        /// </summary>
        /*
        [HarmonyPatch("Open")]
        [HarmonyPostfix]
        public static void Open_Postfix(InGameMenuGUI __instance)
        {
            // Don't add multiplayer button to pause menu - only show it in main menu
            return;
            
            /* DISABLED CODE - Multiplayer button in pause menu
            try
            {
                // Only create the button once per instance
                if (buttonCreated || multiplayerButton != null)
                {
                    return;
                }
                
                CoopMod.Logger.LogInfo("InGameMenuGUI.Open - Adding Multiplayer button");
                
                // Create LobbyGUI_v2 if it doesn't exist
                if (LobbyGUI_v2.Instance == null)
                {
                    CoopMod.Logger.LogInfo("Creating LobbyGUI_v2 from in-game menu");
                    LobbyGUI_v2.Create();
                }
                
                // Find all MenuItemGUI components in the in-game menu
                var menuItems = __instance.GetComponentsInChildren<MenuItemGUI>(true);
                CoopMod.Logger.LogInfo($"Found {menuItems.Length} menu items in InGameMenuGUI");
                
                if (menuItems.Length == 0)
                {
                    CoopMod.Logger.LogError("Could not find any MenuItemGUI components to clone");
                    return;
                }
                
                // Use the first menu item as template
                GameObject templateButton = menuItems[0].gameObject;
                CoopMod.Logger.LogInfo($"Using template button: {templateButton.name}");
                
                // Clone the button
                multiplayerButton = Object.Instantiate(templateButton, templateButton.transform.parent);
                multiplayerButton.name = "btn_multiplayer";
                
                // Position it below the template
                var localPos = multiplayerButton.transform.localPosition;
                localPos.y -= 60; // Move down
                multiplayerButton.transform.localPosition = localPos;
                
                CoopMod.Logger.LogInfo($"Cloned button at position: {localPos}");
                
                // Update the label text
                var labels = multiplayerButton.GetComponentsInChildren<UILabel>();
                CoopMod.Logger.LogInfo($"Found {labels.Length} UILabel components");
                
                foreach (var label in labels)
                {
                    CoopMod.Logger.LogInfo($"Label text before: '{label.text}'");
                    label.text = "Multiplayer";
                    CoopMod.Logger.LogInfo($"Label text after: '{label.text}'");
                }
                
                // Change the button's onClick to open our multiplayer lobby
                var menuItemGUI = multiplayerButton.GetComponent<MenuItemGUI>();
                if (menuItemGUI != null)
                {
                    CoopMod.Logger.LogInfo("Found MenuItemGUI - changing on_pressed EventDelegate");
                    
                    // Create new EventDelegate pointing to our method
                    menuItemGUI.on_pressed = new EventDelegate(() =>
                    {
                        CoopMod.Logger.LogInfo("Multiplayer button pressed from in-game menu!");
                        OnMultiplayerButtonPressed(__instance);
                    });
                    
                    CoopMod.Logger.LogInfo("EventDelegate changed successfully!");
                }
                
                buttonCreated = true;
                CoopMod.Logger.LogInfo("Multiplayer button added to in-game menu successfully!");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"Error adding multiplayer button to in-game menu: {ex}");
            }
        */
        
        /* Multiplayer button handler - also disabled
        private static void OnMultiplayerButtonPressed(InGameMenuGUI inGameMenu)
        {
            try
            {
                CoopMod.Logger.LogInfo("Opening multiplayer lobby from in-game menu");
                
                // Close the in-game menu first
                inGameMenu.Hide();
                
                // Open the lobby
                if (LobbyGUI_v2.Instance != null)
                {
                    LobbyGUI_v2.Instance.Open();
                    
                    // Add the host player with their Steam username
                    var playerName = SteamHelper.GetLocalPlayerName();
                    LobbyGUI_v2.Instance.AddPlayer(playerName);
                    LobbyGUI_v2.Instance.AddChatMessage("[System] Lobby created!");
                    LobbyGUI_v2.Instance.AddChatMessage("[System] Waiting for players...");
                }
                else
                {
                    CoopMod.Logger.LogError("LobbyGUI_v2.Instance is null!");
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"Error opening multiplayer lobby: {ex}");
            }
        }
        */
    }
}

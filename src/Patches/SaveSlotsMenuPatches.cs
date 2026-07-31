using HarmonyLib;
using GraveyardKeeperCoop.LocalCoop;
using GraveyardKeeperCoop.Multiplayer;
using GraveyardKeeperCoop.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches for SaveSlotsMenuGUI to properly restore cameras when exiting game.
    /// </summary>
    [HarmonyPatch(typeof(SaveSlotsMenuGUI))]
    public class SaveSlotsMenuPatches
    {
        // Store references to the header label and its LocalizedLabel so we can disable/enable (NOT destroy)
        private static UILabel _headerLabel;
        private static LocalizedLabel _headerLocalizedLabel;
        /// <summary>
        /// Patch StopPlayingGame to disable local co-op before leaving to main menu.
        /// </summary>
        [HarmonyPatch("StopPlayingGame")]
        [HarmonyPrefix]
        public static void StopPlayingGame_Prefix()
        {
            var manager = LocalCoopManager.Instance;
            if (manager != null && manager.IsLocalCoopEnabled)
            {
                CoopMod.Logger.LogInfo("[SaveSlotsPatch] StopPlayingGame called - disabling local co-op");
                manager.DisableLocalCoop();
            }
        }

        /// <summary>
        /// Intercepts the Open method to potentially show custom slots
        /// </summary>
        [HarmonyPatch("Open")]
        [HarmonyPrefix]
        public static void Open_Prefix(SaveSlotsMenuGUI __instance)
        {
            CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Open_Prefix called - IsModsMode={MainMenuPatches.IsModsMode}, IsLoadGameMode={MainMenuPatches.IsLoadGameMode}");
            
            // Always clean up leftover ModEntry_ objects (they survive Clear() since they're not in _slots)
            var slotsTableField = typeof(SaveSlotsMenuGUI).GetField("_slots_table",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            SimpleUITable slotsTable = slotsTableField?.GetValue(__instance) as SimpleUITable;
            
            if (slotsTable != null)
            {
                var leftoverModEntries = slotsTable.GetComponentsInChildren<Transform>(true)
                    .Where(t => t.name.StartsWith("ModEntry_"))
                    .ToList();
                
                if (leftoverModEntries.Count > 0)
                {
                    CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Cleaning up {leftoverModEntries.Count} leftover mod entries");
                    foreach (var entry in leftoverModEntries)
                    {
                        UnityEngine.Object.DestroyImmediate(entry.gameObject);
                    }
                }
            }
            
            // Re-enable header's LocalizedLabel if we previously disabled it
            // This allows the game to restore the original header text naturally
            if (_headerLocalizedLabel != null && (bool)_headerLocalizedLabel)
            {
                if (!_headerLocalizedLabel.enabled)
                {
                    _headerLocalizedLabel.enabled = true;
                    CoopMod.Logger.LogInfo("[SaveSlotsMenuPatch] Re-enabled header LocalizedLabel");
                }
            }
            else if (_headerLabel != null && (bool)_headerLabel && _headerLabel.text == "Mods")
            {
                // Fallback: LocalizedLabel was destroyed by old code - manually reset header text
                _headerLabel.text = "Pick a save slot";
                CoopMod.Logger.LogInfo("[SaveSlotsMenuPatch] Manually reset header text (LocalizedLabel was destroyed)");
            }
            
            // Check if ManualSaveSlotsGUI wants to take over
            if (ManualSaveSlotsGUI.Instance != null && ManualSaveSlotsGUI.Instance.IsInCustomMode())
            {
                CoopMod.Logger.LogInfo("[SaveSlotsMenuPatch] In custom mode - will intercept slot loading");
            }
        }
        
        /// <summary>
        /// After Open completes, log the current flag state
        /// </summary>
        [HarmonyPatch("Open")]
        [HarmonyPostfix]
        public static void Open_Postfix(SaveSlotsMenuGUI __instance)
        {
            CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Open_Postfix - IsLoadGameMode={MainMenuPatches.IsLoadGameMode}, IsSaveGameMode={MainMenuPatches.IsSaveGameMode}, WasInGame={MainMenuPatches.WasInGame}, IsModsMode={MainMenuPatches.IsModsMode}");
            
            // Only modify header when in Mods mode
            // When NOT in Mods mode, Open_Prefix already re-enabled the LocalizedLabel which auto-restores the text
            if (MainMenuPatches.IsModsMode)
            {
                var allLabels = __instance.GetComponentsInChildren<UILabel>(true);
                
                foreach (var label in allLabels)
                {
                    if (label.text != null &&
                        (label.text.Contains("Save") || label.text.Contains("Slot") || label.text.Contains("Pick") || label.text == "Mods"))
                    {
                        _headerLabel = label;
                        
                        // DISABLE (not destroy!) the LocalizedLabel to prevent it from overwriting our text
                        var localizedLabel = label.GetComponent<LocalizedLabel>();
                        if (localizedLabel != null)
                        {
                            _headerLocalizedLabel = localizedLabel; // Store reference for re-enabling later
                            localizedLabel.enabled = false;
                            CoopMod.Logger.LogInfo("[SaveSlotsMenuPatch] Disabled header LocalizedLabel");
                        }
                        
                        label.text = "Mods";
                        label.MarkAsChanged();
                        CoopMod.Logger.LogInfo("[SaveSlotsMenuPatch] Changed header to 'Mods'");
                        break;
                    }
                }
            }
        }
        
        /// <summary>
        /// Immediately removes ModEntry_ GameObjects and restores the header in the SaveSlotsMenuGUI.
        /// Must be called whenever we leave Mods mode (close, back, ESC) so that other code
        /// cloning the SaveSlotsMenuGUI (e.g. SaveSelectorPanel in LobbyGUI) doesn't pick up stale entries.
        /// </summary>
        private static void CleanupModsEntries(SaveSlotsMenuGUI instance)
        {
            var slotsTableField = typeof(SaveSlotsMenuGUI).GetField("_slots_table",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            SimpleUITable slotsTable = slotsTableField?.GetValue(instance) as SimpleUITable;
            
            if (slotsTable != null)
            {
                var modEntries = slotsTable.GetComponentsInChildren<Transform>(true)
                    .Where(t => t.name.StartsWith("ModEntry_"))
                    .ToList();
                
                if (modEntries.Count > 0)
                {
                    CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Cleaning up {modEntries.Count} mod entries on close");
                    foreach (var entry in modEntries)
                    {
                        UnityEngine.Object.DestroyImmediate(entry.gameObject);
                    }
                }
            }
            
            // Restore header
            if (_headerLocalizedLabel != null && (bool)_headerLocalizedLabel && !_headerLocalizedLabel.enabled)
            {
                _headerLocalizedLabel.enabled = true;
                CoopMod.Logger.LogInfo("[SaveSlotsMenuPatch] Re-enabled header LocalizedLabel on close");
            }
            else if (_headerLabel != null && (bool)_headerLabel && _headerLabel.text == "Mods")
            {
                _headerLabel.text = "Pick a save slot";
                CoopMod.Logger.LogInfo("[SaveSlotsMenuPatch] Manually reset header text on close");
            }
        }
        
        /// <summary>
        /// Intercept OnClosePressed to prevent returning to main menu when opened from in-game
        /// </summary>
        [HarmonyPatch("OnClosePressed")]
        [HarmonyPrefix]
        public static bool OnClosePressed_Prefix(SaveSlotsMenuGUI __instance)
        {
            CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] OnClosePressed - WasInGame={MainMenuPatches.WasInGame}");
            
            // If we opened from in-game (pause menu), don't go to main menu
            if (MainMenuPatches.WasInGame)
            {
                CoopMod.Logger.LogInfo("[SaveSlotsMenuPatch] Closing save menu and returning to pause menu");
                
                // Clean up mod entries immediately so they don't leak into other UIs
                CleanupModsEntries(__instance);
                
                // Hide the save menu
                __instance.Hide(true);
                
                // Return to the pause menu instead of main menu
                GUIElements.me.ingame_menu.Open();
                CoopMod.Logger.LogInfo("[SaveSlotsMenuPatch] Opened pause menu");
                
                // Reset flags
                MainMenuPatches.IsLoadGameMode = false;
                MainMenuPatches.IsSaveGameMode = false;
                MainMenuPatches.IsModsMode = false;
                MainMenuPatches.WasInGame = false;
                MainMenuPatches.IsMultiplayerSaveMode = false;
                
                // Prevent original method from running
                return false;
            }
            
            // Reset flags for main menu case too
            MainMenuPatches.IsLoadGameMode = false;
            MainMenuPatches.IsSaveGameMode = false;
            MainMenuPatches.IsModsMode = false;
            MainMenuPatches.WasInGame = false;
            MainMenuPatches.IsMultiplayerSaveMode = false;
            
            // Clean up mod entries immediately so they don't leak into other UIs
            CleanupModsEntries(__instance);
            
            // Allow original method to run (will open main menu)
            return true;
        }

        /// <summary>
        /// Intercept OnPressedBack (ESC key) to prevent returning to main menu when opened from in-game
        /// </summary>
        [HarmonyPatch("OnPressedBack")]
        [HarmonyPrefix]
        public static bool OnPressedBack_Prefix(SaveSlotsMenuGUI __instance)
        {
            CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] OnPressedBack (ESC) - WasInGame={MainMenuPatches.WasInGame}");
            
            // If we opened from in-game (pause menu), don't go to main menu
            if (MainMenuPatches.WasInGame)
            {
                CoopMod.Logger.LogInfo("[SaveSlotsMenuPatch] ESC pressed - returning to pause menu instead of main menu");
                
                // Clean up mod entries immediately so they don't leak into other UIs
                CleanupModsEntries(__instance);
                
                // Hide the save menu
                __instance.Hide(true);
                
                // Return to the pause menu instead of main menu
                GUIElements.me.ingame_menu.Open();
                
                // Reset flags
                MainMenuPatches.IsLoadGameMode = false;
                MainMenuPatches.IsSaveGameMode = false;
                MainMenuPatches.WasInGame = false;
                MainMenuPatches.IsMultiplayerSaveMode = false;
                
                // Prevent original method from running (which would open main menu)
                return false;
            }
            
            // Reset flags for main menu case
            MainMenuPatches.IsLoadGameMode = false;
            MainMenuPatches.IsSaveGameMode = false;
            MainMenuPatches.IsModsMode = false;
            MainMenuPatches.WasInGame = false;
            MainMenuPatches.IsMultiplayerSaveMode = false;
            MainMenuPatches.WasInGame = false;
            
            // Clean up mod entries immediately so they don't leak into other UIs
            CleanupModsEntries(__instance);
            
            // Allow original method to run (will open main menu)
            return true;
        }

        /// <summary>
        /// Intercepts OnSlotsLoaded to filter saves based on multiplayer context.
        /// In multiplayer mode: only show coop saves.
        /// In single-player mode: hide coop saves.
        /// Also handles custom manual save slots mode.
        /// </summary>
        [HarmonyPatch("OnSlotsLoaded")]
        [HarmonyPrefix]
        public static bool OnSlotsLoaded_Prefix(SaveSlotsMenuGUI __instance, List<SaveSlotData> slots)
        {
            // Check if we should show custom manual save slots
            if (ManualSaveSlotsGUI.Instance != null && ManualSaveSlotsGUI.Instance.IsInCustomMode())
            {
                CoopMod.Logger.LogInfo("[SaveSlotsMenuPatch] Intercepting slot loading - showing manual saves only");
                
                // Get custom manual save slots
                List<SaveSlotData> customSlots = ManualSaveSlotsGUI.Instance.GetCustomSlots();
                
                // Call the original method with our custom slots instead
                __instance.GetType()
                    .GetMethod("OnSlotsLoaded", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Invoke(__instance, new object[] { customSlots });
                
                // Return false to prevent the original method from running
                return false;
            }

            // Filter saves based on multiplayer context
            if (MainMenuPatches.IsMultiplayerSaveMode)
            {
                // In multiplayer mode: only show coop saves
                slots.RemoveAll(s => !MainMenuPatches.IsMultiplayerSave(s));
                CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Multiplayer mode - filtered to {slots.Count} coop saves");
            }
            else
            {
                // In single-player mode: hide coop saves
                slots.RemoveAll(s => MainMenuPatches.IsMultiplayerSave(s));
                CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Single-player mode - filtered out {slots.Count} saves remaining (coop saves hidden)");
            }

            // Let the original method run with filtered slots
            return true;
        }
        
        /// <summary>
        /// Intercept RedrawSlots to replace with mod list if in mods mode.
        /// Uses the game's own _save_slot_prefab as the template (NEVER destroy it!).
        /// </summary>
        [HarmonyPatch("RedrawSlots")]
        [HarmonyPrefix]
        public static bool RedrawSlots_Prefix(SaveSlotsMenuGUI __instance, List<SaveSlotData> slot_datas)
        {
            // Only intercept in Mods mode - let Play, Load Game, Save Game run normally
            if (!MainMenuPatches.IsModsMode)
                return true;
            
            CoopMod.Logger.LogInfo("[SaveSlotsMenuPatch] Mods mode - intercepting RedrawSlots");
            
            // Get the slots table
            var slotsTableField = typeof(SaveSlotsMenuGUI).GetField("_slots_table",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            SimpleUITable slotsTable = slotsTableField?.GetValue(__instance) as SimpleUITable;
            
            if (slotsTable == null)
            {
                CoopMod.Logger.LogWarning("[SaveSlotsMenuPatch] Could not find slots table!");
                return false;
            }
            
            // Clean up any existing mod entries from previous calls (RedrawSlots is called 2x per Open)
            var existingModEntries = slotsTable.GetComponentsInChildren<Transform>(true)
                .Where(t => t.name.StartsWith("ModEntry_"))
                .ToList();
            
            if (existingModEntries.Count > 0)
            {
                CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Cleaning {existingModEntries.Count} existing mod entries");
                foreach (var entry in existingModEntries)
                {
                    UnityEngine.Object.DestroyImmediate(entry.gameObject);
                }
            }
            
            // CRITICAL: Use the game's own _save_slot_prefab as template.
            // This is the permanent template stored during Init() - it must NEVER be destroyed.
            // Do NOT use GetComponentsInChildren which could return copies that get destroyed.
            var prefabField = typeof(SaveSlotsMenuGUI).GetField("_save_slot_prefab",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            SaveSlotGUI prefab = prefabField?.GetValue(__instance) as SaveSlotGUI;
            
            if (prefab == null || !(bool)prefab)
            {
                CoopMod.Logger.LogWarning("[SaveSlotsMenuPatch] _save_slot_prefab is null! Cannot create mod entries.");
                return false;
            }
            
            CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Using _save_slot_prefab: {prefab.name}");
            
            // Get scroll view for updating later
            var scrollViewField = typeof(SaveSlotsMenuGUI).GetField("_scroll_view",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            UIScrollView scrollView = scrollViewField?.GetValue(__instance) as UIScrollView;
            
            // Get all loaded plugins
            var plugins = BepInEx.Bootstrap.Chainloader.PluginInfos.Values.ToList();
            CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Found {plugins.Count} loaded mods");
            
            // Create a mod entry for each plugin using the prefab as template
            foreach (var plugin in plugins)
            {
                CreateModEntry(slotsTable.transform, plugin, prefab);
            }
            
            // Reposition the table and scroll view
            slotsTable.Reposition();
            
            if (scrollView != null)
            {
                scrollView.ResetPosition();
                scrollView.UpdateScrollbars(true);
            }
            
            CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Created {plugins.Count} mod entries");
            return false; // Skip original RedrawSlots
        }
        
        /// <summary>
        /// After RedrawSlots completes, hide the New Game slot if in Load Game mode,
        /// or change its label to "New Save" if in Save Game mode.
        /// CRITICAL: Does NOTHING for normal Play mode - leaves it completely untouched.
        /// Uses the game's _slots list (copies only, NOT the prefab) to avoid destroying the template.
        /// </summary>
        [HarmonyPatch("RedrawSlots")]
        [HarmonyPostfix]
        public static void RedrawSlots_Postfix(SaveSlotsMenuGUI __instance, List<SaveSlotData> slot_datas)
        {
            // Skip if in mods mode (handled entirely by Prefix)
            if (MainMenuPatches.IsModsMode)
                return;
            
            // CRITICAL: Do NOTHING for normal Play mode - leave it completely untouched!
            if (!MainMenuPatches.IsLoadGameMode && !MainMenuPatches.IsSaveGameMode)
                return;
            
            // Use the game's own _slots list - this contains only COPIES, not the prefab.
            // This is critical: GetComponentsInChildren would also find _save_slot_prefab,
            // and destroying that breaks everything permanently.
            var slotsField = typeof(SaveSlotsMenuGUI).GetField("_slots",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var slots = slotsField?.GetValue(__instance) as List<SaveSlotGUI>;
            
            if (slots == null || slots.Count == 0)
            {
                CoopMod.Logger.LogInfo("[SaveSlotsMenuPatch] Postfix: _slots list is empty, nothing to modify");
                return;
            }
            
            var dataField = typeof(SaveSlotGUI).GetField("_data",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (MainMenuPatches.IsLoadGameMode)
            {
                CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Load Game mode - hiding New Game slot ({slots.Count} slots in list)");
                
                foreach (var slotGUI in slots)
                {
                    if (slotGUI == null || !(bool)slotGUI) continue;
                    
                    var slotData = dataField?.GetValue(slotGUI) as SaveSlotData;
                    if (slotData == null)
                    {
                        // This is the "New Game" COPY - hide it, NEVER destroy!
                        slotGUI.gameObject.SetActive(false);
                        CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Hidden New Game slot: {slotGUI.gameObject.name}");
                        break;
                    }
                }
            }
            
            if (MainMenuPatches.IsSaveGameMode)
            {
                CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Save Game mode - renaming New Game to New Save ({slots.Count} slots in list)");
                
                foreach (var slotGUI in slots)
                {
                    if (slotGUI == null || !(bool)slotGUI) continue;
                    
                    var slotData = dataField?.GetValue(slotGUI) as SaveSlotData;
                    if (slotData == null)
                    {
                        var labels = slotGUI.GetComponentsInChildren<UILabel>(true);
                        foreach (var label in labels)
                        {
                            if (label.text.Equals("New Game", StringComparison.OrdinalIgnoreCase) || label.text == "New game")
                            {
                                label.text = "New Save";
                                CoopMod.Logger.LogInfo("[SaveSlotsMenuPatch] Changed 'New Game' to 'New Save'");
                            }
                        }
                        break;
                    }
                }
            }
            
            // Update scroll view bounds after modifying slots
            var scrollViewField = typeof(SaveSlotsMenuGUI).GetField("_scroll_view",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            UIScrollView scrollView = scrollViewField?.GetValue(__instance) as UIScrollView;
            
            if (scrollView != null)
            {
                var slotsTableField = typeof(SaveSlotsMenuGUI).GetField("_slots_table",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                SimpleUITable slotsTable = slotsTableField?.GetValue(__instance) as SimpleUITable;
                
                if (slotsTable != null)
                    slotsTable.Reposition();
                
                scrollView.ResetPosition();
                scrollView.UpdateScrollbars(true);
            }
        }

        /// <summary>
        /// Intercepts slot selection to handle Save Game mode and manual saves
        /// </summary>
        [HarmonyPatch("OnSelectSlotPressed")]
        [HarmonyPrefix]
        public static bool OnSelectSlotPressed_Prefix(SaveSlotsMenuGUI __instance, SaveSlotData slot)
        {
            CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] OnSelectSlotPressed - IsSaveGameMode={MainMenuPatches.IsSaveGameMode}, IsLoadGameMode={MainMenuPatches.IsLoadGameMode}, slot null={slot == null}");
            
            // Check if we're in Save Game mode (either new save or overwrite existing)
            if (MainMenuPatches.IsSaveGameMode)
            {
                // CRITICAL: Update the current game state before saving
                // This captures the player's current position, inventory, etc.
                MainGame.me.save.PrepareForSave();
                CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Called PrepareForSave() - player position: {MainGame.me.save.player_position}");
                
                if (slot == null)
                {
                    CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Save Game mode - prompting for save name");

                    // Default name: current real-time timestamp + " - Manual" so the user
                    // can see what the autogenerated label would have been and edit it.
                    string defaultName = System.DateTime.Now.ToString("HH:mm, dd MMM yyyy") + " - Manual";

                    // Keep the save slots menu visible in the background; open the rename
                    // dialog modally on top (same window used for host-disconnect popups).
                    GraveyardKeeperCoop.UI.RenameSaveDialog.Show(
                        defaultName,
                        onConfirm: customName =>
                        {
                            SaveSlotDataPatches.PendingCustomName = customName;
                            CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Creating new save with name: '{customName}'");

                            // Let the game's SaveGame handle filename allocation. Our
                            // SaveSlotDataPatches postfix will overwrite real_time with
                            // the user's custom name.
                            PlatformSpecific.SaveGame(null, MainGame.me.save, delegate (SaveSlotData savedSlot)
                            {
                                try
                                {
                                    if (savedSlot != null)
                                    {
                                        CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Save callback - slot filename: {savedSlot.filename_no_extension}, game_time: {savedSlot.game_time}");

                                        // If game_time is 0, the slot wasn't fully prepared
                                        // (happens when PlatformSpecific.SaveGame creates a
                                        // brand-new slot). Force a PrepareForSave + info rewrite.
                                        if (savedSlot.game_time <= 0.001f)
                                        {
                                            CoopMod.Logger.LogWarning($"[SaveSlotsMenuPatch] Detected zero game_time! Updating slot info...");
                                            savedSlot.PrepareForSave();
                                        }

                                        // Ensure the custom name sticks even if the postfix
                                        // didn't fire for some reason (e.g. PrepareForSave
                                        // was skipped internally).
                                        if (!string.IsNullOrEmpty(customName))
                                            savedSlot.real_time = customName;

                                        // Re-save the .info file so the updated real_time +
                                        // game_time are persisted.
                                        string saveFolder = UnityEngine.Application.persistentDataPath + "/";
                                        string infoPath = saveFolder + savedSlot.filename_no_extension + ".info";
                                        System.IO.File.WriteAllText(infoPath, savedSlot.ToJSON());

                                        CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Saved with real_time='{savedSlot.real_time}' game_time={savedSlot.game_time}");
                                        MultiplayerSavePositions.CaptureCurrentSession(savedSlot);
                                    }
                                }
                                finally
                                {
                                    SaveSlotDataPatches.PendingCustomName = null;
                                }

                                // Close save menu and return to pause menu
                                __instance.Hide(true);
                                GUIElements.me.ingame_menu.Open();
                            });
                        },
                        onCancel: () =>
                        {
                            CoopMod.Logger.LogInfo("[SaveSlotsMenuPatch] Save name prompt cancelled - returning to save slots menu");
                            // Save slots menu is still open behind the dialog; nothing to do.
                        });
                }
                else
                {
                    // Overwrite existing slot
                    CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Save Game mode - overwriting existing slot: {slot.real_time}");
                    
                    // CRITICAL: Update the linked_save to the current game state
                    // Otherwise it will save the OLD game state from when this slot was created
                    slot.linked_save = MainGame.me.save;
                    
                    // Save the current game to the existing slot (overwrite)
                    PlatformSpecific.SaveGame(slot, MainGame.me.save, delegate(SaveSlotData savedSlot)
                    {
                        CoopMod.Logger.LogInfo("[SaveSlotsMenuPatch] Game saved successfully, overwriting existing slot");
                        if (savedSlot != null)
                        {
                            MultiplayerSavePositions.CaptureCurrentSession(savedSlot);
                        }
                        // Close save menu and return to pause menu
                        __instance.Hide(true);
                        GUIElements.me.ingame_menu.Open();
                    });
                }
                
                // Prevent original method from running (which would load the game instead)
                return false;
            }
            
            // If we're in Load Game mode, let the original method run to load the game
            if (MainMenuPatches.IsLoadGameMode)
            {
                CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Load Game mode - allowing original load method to run for slot: {(slot != null ? slot.real_time : "NEW GAME")}");
                CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] MainGame.loaded_from_scene_main = {MainGame.loaded_from_scene_main}, MainGame.game_started = {MainGame.game_started}");

                if (MainMenuPatches.WasInGame && slot != null)
                {
                    SaveLoadPatches.MarkInGameLoadStarting();
                }
                
                // Reset flags before loading (load will change scenes)
                MainMenuPatches.IsLoadGameMode = false;
                MainMenuPatches.IsSaveGameMode = false;
                MainMenuPatches.WasInGame = false;
                MainMenuPatches.IsMultiplayerSaveMode = false;
                
                // Allow original method to run (will load the game)
                return true;
            }
            
            // Check if we're in custom mode
            if (ManualSaveSlotsGUI.Instance != null && ManualSaveSlotsGUI.Instance.IsInCustomMode())
            {
                CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Intercepting slot selection - manual save mode");
                
                // Handle the slot selection through our custom GUI
                ManualSaveSlotsGUI.Instance.OnSlotSelected(slot);
                
                // Close the menu
                __instance.Hide(true);
                
                // Return false to prevent the original method from running
                return false;
            }

            // Not in custom mode, let the original method run
            return true;
        }
        
        /// <summary>
        /// Creates a mod entry in the save slots list by cloning a save slot template
        /// </summary>
        private static void CreateModEntry(Transform parent, BepInEx.PluginInfo plugin, SaveSlotGUI templateSlot)
        {
            if (templateSlot == null)
            {
                CoopMod.Logger.LogWarning($"[SaveSlotsMenuPatch] No template slot available to clone for mod entry");
                return;
            }
            
            // Clone the template slot
            GameObject entryObj = UnityEngine.Object.Instantiate(templateSlot.gameObject, parent);
            entryObj.name = $"ModEntry_{plugin.Metadata.GUID}";
            entryObj.transform.localPosition = Vector3.zero;
            entryObj.transform.localScale = Vector3.one;
            entryObj.SetActive(true);
            
            CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Cloned mod entry: {entryObj.name}, active={entryObj.activeSelf}, parent={parent.name}");
            
            // Remove the SaveSlotGUI component so it doesn't act like a real save slot
            var slotGUI = entryObj.GetComponent<SaveSlotGUI>();
            if (slotGUI != null)
            {
                UnityEngine.Object.DestroyImmediate(slotGUI);
                CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Removed SaveSlotGUI component");
            }
            
            // Find and update the labels
            var labels = entryObj.GetComponentsInChildren<UILabel>(true);
            CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Found {labels.Length} labels in mod entry");
            
            foreach (var label in labels)
            {
                // Remove LocalizedLabel components so they don't reset the text
                var localizedLabel = label.GetComponent<LocalizedLabel>();
                if (localizedLabel != null)
                {
                    UnityEngine.Object.DestroyImmediate(localizedLabel);
                }
                
                // Update label text based on what it was
                if (label.name.Contains("new game") || label.transform.parent.name.Contains("new game"))
                {
                    // This is the main title label - show name + version centered
                    label.text = $"{plugin.Metadata.Name} v{plugin.Metadata.Version}";
                    label.color = Color.white;
                    CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch]   Set title label to: {label.text}");
                }
                else if (label.name.Contains("descr"))
                {
                    // Hide the description/GUID label entirely
                    label.text = "";
                    CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch]   Cleared desc label");
                }
                else
                {
                    // Hide realtime, stats, and any other labels
                    label.text = "";
                    CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch]   Cleared label: {label.name}");
                }
            }
            
            CoopMod.Logger.LogInfo($"[SaveSlotsMenuPatch] Created mod entry for: {plugin.Metadata.Name}");
        }
    }

    /// <summary>
    /// Patches SaveSlotData.PrepareForSave to append " - Manual" to the real_time
    /// field when the player is doing a manual save (via the Save Game button).
    /// This allows players to distinguish manual saves from automatic ones in the save slot list.
    /// </summary>
    [HarmonyPatch(typeof(SaveSlotData))]
    public class SaveSlotDataPatches
    {
        /// <summary>
        /// When non-null, <see cref="PrepareForSave_Postfix"/> overwrites real_time with
        /// this value instead of appending " - Manual". Set by the rename dialog flow.
        /// </summary>
        public static string PendingCustomName;

        [HarmonyPatch("PrepareForSave")]
        [HarmonyPostfix]
        public static void PrepareForSave_Postfix(SaveSlotData __instance)
        {
            if (MainMenuPatches.IsSaveGameMode)
            {
                if (!string.IsNullOrEmpty(PendingCustomName))
                {
                    __instance.real_time = PendingCustomName;
                    CoopMod.Logger.LogInfo($"[SaveSlotDataPatch] Applied custom save name: {__instance.real_time}");
                }
                else
                {
                    __instance.real_time += " - Manual";
                    CoopMod.Logger.LogInfo($"[SaveSlotDataPatch] Marked save as manual: {__instance.real_time}");
                }
            }
        }
    }
}

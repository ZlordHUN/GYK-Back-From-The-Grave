using System.Collections.Generic;
using UnityEngine;
using GraveyardKeeperCoop.Features;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// Custom save slots menu that only shows manual saves
    /// </summary>
    public class ManualSaveSlotsGUI : MonoBehaviour
    {
        private static ManualSaveSlotsGUI _instance;
        public static ManualSaveSlotsGUI Instance => _instance;

        private SaveSlotsMenuGUI nativeSaveMenu;
        private bool isForSaving; // true = saving mode, false = loading mode
        private List<SaveSlotData> currentSlots;

        public static void Create()
        {
            if (_instance != null)
            {
                CoopMod.Logger.LogInfo("[ManualSaveSlotsGUI] Instance already exists");
                return;
            }

            GameObject go = new GameObject("ManualSaveSlotsGUI");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<ManualSaveSlotsGUI>();
            
            CoopMod.Logger.LogInfo("[ManualSaveSlotsGUI] Created");
        }

        private void Awake()
        {
            if (_instance == null)
            {
                _instance = this;
            }
            else if (_instance != this)
            {
                Destroy(gameObject);
                return;
            }

            // Find the native save menu
            nativeSaveMenu = FindObjectOfType<SaveSlotsMenuGUI>();
            if (nativeSaveMenu == null)
            {
                CoopMod.Logger.LogWarning("[ManualSaveSlotsGUI] SaveSlotsMenuGUI not found yet");
            }
        }

        /// <summary>
        /// Opens the manual save menu in save mode
        /// </summary>
        public void OpenForSaving()
        {
            CoopMod.Logger.LogInfo("[ManualSaveSlotsGUI] Opening for SAVING");
            isForSaving = true;
            OpenMenu();
        }

        /// <summary>
        /// Opens the manual save menu in load mode
        /// </summary>
        public void OpenForLoading()
        {
            CoopMod.Logger.LogInfo("[ManualSaveSlotsGUI] Opening for LOADING");
            isForSaving = false;
            OpenMenu();
        }

        private void OpenMenu()
        {
            // Make sure we have the native menu reference
            if (nativeSaveMenu == null)
            {
                nativeSaveMenu = FindObjectOfType<SaveSlotsMenuGUI>();
                
                if (nativeSaveMenu == null)
                {
                    CoopMod.Logger.LogError("[ManualSaveSlotsGUI] Cannot find SaveSlotsMenuGUI!");
                    return;
                }
            }

            // Load manual save slots
            currentSlots = ManualSaveSlotManager.Instance.GetManualSaveSlots();
            
            CoopMod.Logger.LogInfo($"[ManualSaveSlotsGUI] Loaded {currentSlots.Count} manual saves, mode: {(isForSaving ? "SAVE" : "LOAD")}");

            // The native menu will be patched to show our custom slots
            nativeSaveMenu.Open();
        }

        /// <summary>
        /// Called by our patch to intercept slot loading
        /// </summary>
        public List<SaveSlotData> GetCustomSlots()
        {
            if (currentSlots == null)
            {
                CoopMod.Logger.LogWarning("[ManualSaveSlotsGUI] No custom slots loaded, returning empty list");
                return new List<SaveSlotData>();
            }

            CoopMod.Logger.LogInfo($"[ManualSaveSlotsGUI] Returning {currentSlots.Count} custom slots");
            return currentSlots;
        }

        /// <summary>
        /// Called when a slot is selected
        /// </summary>
        public void OnSlotSelected(SaveSlotData slot)
        {
            if (isForSaving)
            {
                // In save mode, clicking any slot (including "New Game" which is null) creates a new manual save
                CoopMod.Logger.LogInfo("[ManualSaveSlotsGUI] Creating new manual save...");
                
                ManualSaveSlotManager.Instance.CreateManualSave((SaveSlotData savedSlot) =>
                {
                    CoopMod.Logger.LogInfo($"[ManualSaveSlotsGUI] Manual save created: {savedSlot.filename_no_extension}");
                    
                    // Refresh the menu to show the new save
                    currentSlots = ManualSaveSlotManager.Instance.GetManualSaveSlots();
                });
            }
            else
            {
                // In load mode, load the selected manual save
                if (slot == null)
                {
                    CoopMod.Logger.LogWarning("[ManualSaveSlotsGUI] Cannot load null slot");
                    return;
                }

                CoopMod.Logger.LogInfo($"[ManualSaveSlotsGUI] Loading manual save: {slot.filename_no_extension}");
                
                ManualSaveSlotManager.Instance.LoadManualSave(slot, () =>
                {
                    CoopMod.Logger.LogInfo("[ManualSaveSlotsGUI] Manual save loaded successfully");
                });
            }

            // Clear the custom slots so next time the menu opens normally
            currentSlots = null;
        }

        /// <summary>
        /// Checks if we're currently in custom mode
        /// </summary>
        public bool IsInCustomMode()
        {
            return currentSlots != null;
        }

        /// <summary>
        /// Resets to normal mode
        /// </summary>
        public void ResetToNormalMode()
        {
            currentSlots = null;
            CoopMod.Logger.LogInfo("[ManualSaveSlotsGUI] Reset to normal mode");
        }
    }
}

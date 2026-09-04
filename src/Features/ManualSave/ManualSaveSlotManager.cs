using System;
using System.Collections.Generic;
using System.IO;
using GraveyardKeeperCoop.Multiplayer;
using UnityEngine;

namespace GraveyardKeeperCoop.Features
{
    /// <summary>
    /// Manages manual save slots separately from the game's auto-saves
    /// </summary>
    public class ManualSaveSlotManager
    {
        private static ManualSaveSlotManager _instance;
        public static ManualSaveSlotManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new ManualSaveSlotManager();
                }
                return _instance;
            }
        }

        private const string MANUAL_SAVE_PREFIX = "manual_save_";
        private const string SAVE_EXTENSION = ".dat";
        
        private string SaveDirectory
        {
            get
            {
                return Application.persistentDataPath + "/saves";
            }
        }

        /// <summary>
        /// Gets all manual save slots (files starting with "manual_save_")
        /// </summary>
        public List<SaveSlotData> GetManualSaveSlots()
        {
            List<SaveSlotData> manualSlots = new List<SaveSlotData>();

            try
            {
                if (!Directory.Exists(SaveDirectory))
                {
                    CoopMod.Logger.LogInfo("[ManualSaves] Save directory doesn't exist yet");
                    return manualSlots;
                }

                string[] files = Directory.GetFiles(SaveDirectory, MANUAL_SAVE_PREFIX + "*" + SAVE_EXTENSION);
                
                foreach (string filePath in files)
                {
                    try
                    {
                        string json = File.ReadAllText(filePath);
                        SaveSlotData slot = SaveSlotData.FromJSON(json);
                        
                        if (slot != null)
                        {
                            slot.filename_no_extension = Path.GetFileNameWithoutExtension(filePath);
                            manualSlots.Add(slot);
                            CoopMod.Logger.LogInfo($"[ManualSaves] Loaded slot: {slot.filename_no_extension}");
                        }
                    }
                    catch (Exception ex)
                    {
                        CoopMod.Logger.LogError($"[ManualSaves] Error loading slot {filePath}: {ex.Message}");
                    }
                }

                CoopMod.Logger.LogInfo($"[ManualSaves] Found {manualSlots.Count} manual save slots");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[ManualSaves] Error reading manual saves: {ex}");
            }

            return manualSlots;
        }

        /// <summary>
        /// Creates a new manual save in the next available slot
        /// </summary>
        public void CreateManualSave(Action<SaveSlotData> onComplete = null)
        {
            bool saveCompletionPending = false;

            try
            {
                if (MainGame.me == null || MainGame.me.save == null)
                {
                    CoopMod.Logger.LogError("[ManualSaves] Cannot save - game not loaded");
                    return;
                }

                // Find next available slot number
                int nextSlot = GetNextAvailableSlotNumber();
                string filename = MANUAL_SAVE_PREFIX + nextSlot;

                CoopMod.Logger.LogInfo($"[ManualSaves] Creating manual save in slot {nextSlot}");
                ManualSaveIndicatorSync.BeginLocal();
                saveCompletionPending = true;

                // Create new slot data
                SaveSlotData slotData = new SaveSlotData
                {
                    filename_no_extension = filename
                };
                slotData.PrepareForSave();

                // Save using PlatformSpecific which handles both slot data and game save
                PlatformSpecific.SaveGame(slotData, MainGame.me.save, (SaveSlotData savedSlot) =>
                {
                    try
                    {
                        CoopMod.Logger.LogInfo($"[ManualSaves] Manual save created: {savedSlot.filename_no_extension}");
                        onComplete?.Invoke(savedSlot);
                    }
                    finally
                    {
                        if (saveCompletionPending)
                        {
                            saveCompletionPending = false;
                            ManualSaveIndicatorSync.EndLocal();
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                if (saveCompletionPending)
                {
                    saveCompletionPending = false;
                    ManualSaveIndicatorSync.EndLocal();
                }

                CoopMod.Logger.LogError($"[ManualSaves] Error creating manual save: {ex}");
            }
        }

        /// <summary>
        /// Loads a manual save slot
        /// </summary>
        public void LoadManualSave(SaveSlotData slot, Action onComplete = null)
        {
            try
            {
                if (slot == null)
                {
                    CoopMod.Logger.LogError("[ManualSaves] Cannot load null slot");
                    return;
                }

                CoopMod.Logger.LogInfo($"[ManualSaves] Loading manual save: {slot.filename_no_extension}");

                PlatformSpecific.LoadGame(slot, (GameSave loadedSave) =>
                {
                    CoopMod.Logger.LogInfo("[ManualSaves] Manual save loaded successfully");
                    onComplete?.Invoke();
                });
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[ManualSaves] Error loading manual save: {ex}");
            }
        }

        /// <summary>
        /// Deletes a manual save slot
        /// </summary>
        public void DeleteManualSave(SaveSlotData slot, Action onComplete = null)
        {
            try
            {
                if (slot == null)
                {
                    CoopMod.Logger.LogError("[ManualSaves] Cannot delete null slot");
                    return;
                }

                CoopMod.Logger.LogInfo($"[ManualSaves] Deleting manual save: {slot.filename_no_extension}");

                PlatformSpecific.DeleteSlot(slot, () =>
                {
                    CoopMod.Logger.LogInfo("[ManualSaves] Manual save deleted");
                    onComplete?.Invoke();
                });
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[ManualSaves] Error deleting manual save: {ex}");
            }
        }

        /// <summary>
        /// Finds the next available slot number
        /// </summary>
        private int GetNextAvailableSlotNumber()
        {
            List<SaveSlotData> existing = GetManualSaveSlots();
            int maxSlot = 0;

            foreach (SaveSlotData slot in existing)
            {
                // Extract number from filename (e.g., "manual_save_1" -> 1)
                string filename = slot.filename_no_extension;
                if (filename.StartsWith(MANUAL_SAVE_PREFIX))
                {
                    string numberPart = filename.Substring(MANUAL_SAVE_PREFIX.Length);
                    if (int.TryParse(numberPart, out int slotNumber))
                    {
                        maxSlot = Mathf.Max(maxSlot, slotNumber);
                    }
                }
            }

            return maxSlot + 1;
        }

        /// <summary>
        /// Checks if a slot is a manual save (vs auto-save)
        /// </summary>
        public bool IsManualSave(SaveSlotData slot)
        {
            if (slot == null) return false;
            return slot.filename_no_extension.StartsWith(MANUAL_SAVE_PREFIX);
        }
    }
}

using System;
using UnityEngine;

namespace GraveyardKeeperCoop.Features
{
    /// <summary>
    /// Provides manual save functionality for the game
    /// </summary>
    public class ManualSaveSystem
    {
        private static ManualSaveSystem _instance;
        public static ManualSaveSystem Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new ManualSaveSystem();
                }
                return _instance;
            }
        }

        public bool IsSaving { get; private set; }

        /// <summary>
        /// Performs a quick save using the game's native save system
        /// </summary>
        public void QuickSave(Action onComplete = null)
        {
            if (IsSaving)
            {
                CoopMod.Logger.LogWarning("[ManualSave] Save already in progress!");
                return;
            }

            if (MainGame.me == null || MainGame.me.save == null)
            {
                CoopMod.Logger.LogError("[ManualSave] MainGame or save system not available!");
                return;
            }

            try
            {
                IsSaving = true;
                CoopMod.Logger.LogInfo("[ManualSave] Starting quick save...");
                
                MainGame.me.save.QuickSave();
                
                CoopMod.Logger.LogInfo("[ManualSave] Game saved successfully!");
                IsSaving = false;
                
                onComplete?.Invoke();
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[ManualSave] Error during save: {ex}");
                IsSaving = false;
            }
        }
    }
}

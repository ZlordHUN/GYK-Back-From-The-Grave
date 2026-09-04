using HarmonyLib;
using System;
using System.Linq;
using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Fixes the vanilla game limitation where carried/overhead items (bodies, logs, etc.)
    /// are NOT saved by GameSave.PrepareForSave.
    /// 
    /// The problem:
    /// - When the player carries a body overhead, it's stored in BaseCharacterComponent.overhead_item
    /// - GameSave.PrepareForSave only saves MainGame.me.player.data (the player's WGO Item/inventory)
    /// - overhead_item is a completely separate field — it's NOT part of player.data
    /// - On load, BaseCharacterComponent.InitComponents() sets overhead_item = null
    /// - Result: any carried body/item vanishes after save/reload
    /// 
    /// The fix:
    /// - Before serialization (ToBinary prefix): inject the overhead item into player.data.inventory
    ///   with a marker param so we can identify it later
    /// - After serialization (ToBinary postfix): remove it from player.data.inventory
    /// - After load (OnGameStartedPlaying postfix): scan player inventory for the marker,
    ///   remove the item from inventory, and restore it to overhead
    /// </summary>
    [HarmonyPatch]
    public static class OverheadItemSavePatches
    {
        private const string OVERHEAD_MARKER_PARAM = "_coop_was_overhead";
        
        /// <summary>
        /// Track the overhead item we injected so we can cleanly remove it after serialization.
        /// </summary>
        private static Item _injectedOverheadItem = null;
        
        /// <summary>
        /// Before ToBinary serializes the GameSave, inject the player's overhead item
        /// into the player's inventory so it gets included in the save data.
        /// 
        /// Flow: PrepareForSave sets _inventory = player.data (reference).
        /// So adding to player.data.inventory here means it's also in _inventory.inventory
        /// when SmartSerializer.Serialize runs inside ToBinary.
        /// </summary>
        [HarmonyPatch(typeof(GameSave), "ToBinary")]
        [HarmonyPriority(Priority.High)]
        [HarmonyPrefix]
        public static void ToBinary_InjectOverhead_Prefix()
        {
            try
            {
                var player = MainGame.me?.player;
                if (player?.components?.character == null) return;
                
                var overhead = player.components.character.GetOverheadItem();
                if (overhead == null) return;
                
                if (player.data?.inventory == null) return;
                
                // Mark the item so we can identify it on load
                overhead.SetParam(OVERHEAD_MARKER_PARAM, 1f);
                
                // Inject into player's inventory data (which is referenced by GameSave._inventory)
                player.data.inventory.Add(overhead);
                _injectedOverheadItem = overhead;
                
                CoopMod.Logger.LogInfo($"[OverheadSave] Injected overhead item '{overhead.id}' into player inventory for serialization");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[OverheadSave] Error injecting overhead item: {ex.Message}");
            }
        }
        
        /// <summary>
        /// After ToBinary finishes or throws, remove the injected overhead item
        /// from the player's live inventory to avoid gameplay side effects.
        /// </summary>
        [HarmonyPatch(typeof(GameSave), "ToBinary")]
        [HarmonyPriority(Priority.High)]
        [HarmonyFinalizer]
        public static Exception ToBinary_RemoveOverhead_Finalizer(Exception __exception)
        {
            CleanupInjectedOverheadItem();
            return __exception;
        }

        private static void CleanupInjectedOverheadItem()
        {
            if (_injectedOverheadItem == null) return;
            
            try
            {
                var player = MainGame.me?.player;
                if (player?.data?.inventory != null)
                {
                    player.data.inventory.Remove(_injectedOverheadItem);
                }
                
                // Remove the marker from the live item (player is still carrying it)
                _injectedOverheadItem.SetParam(OVERHEAD_MARKER_PARAM, 0f);
                
                CoopMod.Logger.LogInfo($"[OverheadSave] Removed overhead item '{_injectedOverheadItem.id}' from player inventory after serialization");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[OverheadSave] Error removing overhead item: {ex.Message}");
            }
            finally
            {
                _injectedOverheadItem = null;
            }
        }
        
        /// <summary>
        /// After the game finishes loading and is fully initialized, check if the player's
        /// inventory contains a marked overhead item and restore it to the carried position.
        /// 
        /// OnGameStartedPlaying fires after:
        /// - Scene restored
        /// - Player spawned with inventory
        /// - All WGOs initialized
        /// - HUD opened
        /// - Player teleported to spawn point
        /// This is the safest point to call SetOverheadItem.
        /// </summary>
        [HarmonyPatch(typeof(MainGame), "OnGameStartedPlaying")]
        [HarmonyPostfix]
        public static void RestoreOverheadItem_Postfix()
        {
            try
            {
                var player = MainGame.me?.player;
                if (player?.data?.inventory == null) return;
                if (player.components?.character == null) return;
                
                // Find any item marked as overhead
                Item overheadItem = null;
                foreach (var item in player.data.inventory)
                {
                    if (item != null && item.GetParam(OVERHEAD_MARKER_PARAM, 0f) > 0.5f)
                    {
                        overheadItem = item;
                        break;
                    }
                }
                
                if (overheadItem == null) return;
                
                // Remove from inventory
                player.data.inventory.Remove(overheadItem);
                
                // Clear the marker
                overheadItem.SetParam(OVERHEAD_MARKER_PARAM, 0f);
                
                // Drop any existing overhead item first (shouldn't happen, but be safe)
                if (player.components.character.has_overhead)
                {
                    CoopMod.Logger.LogWarning("[OverheadSave] Player already has overhead item during restore — dropping it first");
                    player.components.character.DropOverheadItem(false);
                }
                
                // Restore the carried item
                player.components.character.SetOverheadItem(overheadItem);
                
                CoopMod.Logger.LogInfo($"[OverheadSave] Restored overhead item '{overheadItem.id}' after load");
                
                if (overheadItem.id == "body" && overheadItem.inventory != null)
                {
                    var parts = overheadItem.inventory
                        .Where(p => p != null)
                        .Select(p => p.id);
                    CoopMod.Logger.LogInfo($"[OverheadSave] Body parts: [{string.Join(",", parts)}]");
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[OverheadSave] Error restoring overhead item: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}

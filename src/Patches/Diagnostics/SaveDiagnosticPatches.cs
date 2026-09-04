using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Diagnostic patches for the save/load system.
    /// Logs detailed information about what world objects are being serialized
    /// to help debug issues like disappearing corpses after save/reload.
    /// 
    /// KEY INSIGHT: In Graveyard Keeper, the "body" (corpse) is an Item with 
    /// ItemType.Body, stored INSIDE a WGO's inventory (corpse_bed, autopsi_table, 
    /// grave_ground, etc.). It is NOT its own WorldGameObject.
    /// So we need to check WGO inventories for body items, not look for "corpse" WGOs.
    /// </summary>
    [HarmonyPatch]
    public static class SaveDiagnosticPatches
    {
        private static bool DiagnosticsEnabled => ModConfig.EnableSaveDiagnostics?.Value == true;

        // All obj_id patterns for WGOs that can hold a body in their inventory
        private static readonly string[] BODY_HOLDER_IDS = new[]
        {
            "corpse_bed",       // morgue tables (StartsWith)
            "corpse_fridge",    // corpse refrigerators (StartsWith)
            "autopsi_table",    // autopsy table (note: game typo — "autopsi")
            "mf_preparation",   // preparation tables (Contains)
            "mf_balsamation",   // embalming tables (StartsWith)
            "mf_crematorium",   // crematorium
            "mf_pyre",          // funeral pyre
            "grave_ground",     // active graves
            "grave_empty",      // empty grave slots
            "zombie_crafting",  // zombie resurrection table
            "soul_extractor",   // soul extractors 1-3
        };
        
        /// <summary>
        /// Check if a WGO obj_id matches any body-holder pattern
        /// </summary>
        private static bool IsBodyHolder(string obj_id)
        {
            if (string.IsNullOrEmpty(obj_id)) return false;
            foreach (var pattern in BODY_HOLDER_IDS)
            {
                if (obj_id.Contains(pattern)) return true;
            }
            return false;
        }
        
        /// <summary>
        /// Check if an Item or any of its inventory items is a body
        /// </summary>
        private static bool HasBodyItem(Item item)
        {
            if (item == null) return false;
            // Check the item itself
            if (item.id == "body") return true;
            // Check its inventory
            if (item.inventory != null)
            {
                foreach (var sub in item.inventory)
                {
                    if (sub != null && sub.id == "body") return true;
                }
            }
            return false;
        }
        
        /// <summary>
        /// Get a summary of body-related inventory contents
        /// </summary>
        private static string GetBodySummary(Item item)
        {
            if (item == null) return "item=null";
            
            var parts = new List<string>();
            parts.Add($"id={item.id}");
            
            if (item.inventory != null && item.inventory.Count > 0)
            {
                parts.Add($"inv_count={item.inventory.Count}");
                // List actual items in inventory
                var itemIds = item.inventory
                    .Where(i => i != null)
                    .Select(i => i.id)
                    .ToList();
                if (itemIds.Count > 0)
                    parts.Add($"inv=[{string.Join(",", itemIds)}]");
                
                // Check specifically for body items and their sub-parts
                foreach (var sub in item.inventory)
                {
                    if (sub != null && sub.id == "body" && sub.inventory != null && sub.inventory.Count > 0)
                    {
                        var bodyParts = sub.inventory.Where(p => p != null).Select(p => p.id).ToList();
                        parts.Add($"BODY_PARTS=[{string.Join(",", bodyParts)}]");
                    }
                }
            }
            else
            {
                parts.Add("inv=empty");
            }
            
            // If this IS the body item, show its parts directly
            if (item.id == "body" && item.inventory != null && item.inventory.Count > 0)
            {
                var bodyParts = item.inventory.Where(p => p != null).Select(p => p.id).ToList();
                parts.Add($"BODY_PARTS=[{string.Join(",", bodyParts)}]");
            }
            
            return string.Join(", ", parts);
        }
        
        /// <summary>
        /// Log what GameSave.PrepareForSave captures (player state, environment, etc.)
        /// Also logs player inventory for carried body detection.
        /// </summary>
        [HarmonyPatch(typeof(GameSave), "PrepareForSave")]
        [HarmonyPostfix]
        public static void PrepareForSave_Postfix(GameSave __instance)
        {
            if (!DiagnosticsEnabled) return;

            CoopMod.Logger.LogInfo("[SaveDiag] === GameSave.PrepareForSave completed ===");
            CoopMod.Logger.LogInfo($"[SaveDiag]   player_position: {__instance.player_position}");
            CoopMod.Logger.LogInfo($"[SaveDiag]   game_version: {__instance.game_version}");
            CoopMod.Logger.LogInfo($"[SaveDiag]   unique_id_iterator: {__instance.unique_id_iterator}");
            
            if (MainGame.me?.player != null)
            {
                CoopMod.Logger.LogInfo($"[SaveDiag]   player actual position: {MainGame.me.player.transform.localPosition}");
                
                // Check if player is carrying a body (overhead item / in inventory)
                var playerData = MainGame.me.player.data;
                if (playerData != null && playerData.inventory != null)
                {
                    CoopMod.Logger.LogInfo($"[SaveDiag]   player inventory count: {playerData.inventory.Count}");
                    foreach (var item in playerData.inventory)
                    {
                        if (item != null && item.id == "body")
                        {
                            CoopMod.Logger.LogInfo($"[SaveDiag]   BODY IN PLAYER INVENTORY! {GetBodySummary(item)}");
                        }
                    }
                }
                
                // Check overhead item (carried body, logs, etc.)
                var character = MainGame.me.player.components?.character;
                if (character != null)
                {
                    var overhead = character.GetOverheadItem();
                    if (overhead != null)
                    {
                        CoopMod.Logger.LogInfo($"[SaveDiag]   PLAYER OVERHEAD ITEM: {GetBodySummary(overhead)}");
                        if (overhead.id == "body")
                        {
                            CoopMod.Logger.LogInfo($"[SaveDiag]   PLAYER IS CARRYING A BODY OVERHEAD!");
                        }
                    }
                    else
                    {
                        CoopMod.Logger.LogInfo($"[SaveDiag]   player overhead: none");
                    }
                }
            }
            else
            {
                CoopMod.Logger.LogWarning("[SaveDiag]   MainGame.me.player is NULL during PrepareForSave!");
            }
        }

        /// <summary>
        /// Log what SaveSceneToMe captures — the critical method that serializes ALL world objects.
        /// Specifically checks all body-holder WGOs and ANY WGO with a body item in inventory.
        /// </summary>
        [HarmonyPatch(typeof(SerializableGameMap), "SaveSceneToMe")]
        [HarmonyPostfix]
        public static void SaveSceneToMe_Postfix(SerializableGameMap __instance, bool at_game_start)
        {
            if (!DiagnosticsEnabled) return;

            CoopMod.Logger.LogInfo($"[SaveDiag] === SaveSceneToMe completed (at_game_start={at_game_start}) ===");
            
            var wgosField = typeof(SerializableGameMap).GetField("_wgos",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (wgosField == null)
            {
                CoopMod.Logger.LogWarning("[SaveDiag]   Could not access _wgos field via reflection");
                return;
            }
            
            var wgos = wgosField.GetValue(__instance) as List<SerializableWGO>;
            if (wgos == null)
            {
                CoopMod.Logger.LogWarning("[SaveDiag]   _wgos list is null!");
                return;
            }
            
            CoopMod.Logger.LogInfo($"[SaveDiag]   Total serialized WGOs: {wgos.Count}");
            
            // Count and log world_root children for comparison
            if (MainGame.me?.world_root != null)
            {
                var allWGOs = MainGame.me.world_root.gameObject.GetComponentsInChildren<WorldGameObject>(true);
                var activeWGOs = MainGame.me.world_root.gameObject.GetComponentsInChildren<WorldGameObject>(false);
                CoopMod.Logger.LogInfo($"[SaveDiag]   world_root WGOs: {allWGOs.Length} total ({activeWGOs.Length} active, {allWGOs.Length - activeWGOs.Length} inactive)");
            }
            
            // Log ALL body-holder WGOs and their inventory state
            int bodyHolderCount = 0;
            int bodiesFound = 0;
            foreach (var wgo in wgos)
            {
                if (!IsBodyHolder(wgo.obj_id)) continue;
                bodyHolderCount++;
                
                bool hasBody = HasBodyItem(wgo.item);
                if (hasBody) bodiesFound++;
                
                string bodyMarker = hasBody ? " HAS_BODY" : "";
                CoopMod.Logger.LogInfo($"[SaveDiag]   BODY_HOLDER [{wgo.obj_id}] pos=({wgo.position.x:F1},{wgo.position.y:F1},{wgo.position.z:F1}){bodyMarker} | {GetBodySummary(wgo.item)}");
            }
            CoopMod.Logger.LogInfo($"[SaveDiag]   Body holders: {bodyHolderCount}, Bodies found in inventories: {bodiesFound}");
            
            // ALSO scan ALL WGOs for any that contain a body item — catches unexpected locations
            foreach (var wgo in wgos)
            {
                if (IsBodyHolder(wgo.obj_id)) continue; // already logged
                if (HasBodyItem(wgo.item))
                {
                    CoopMod.Logger.LogInfo($"[SaveDiag]   UNEXPECTED BODY in [{wgo.obj_id}] pos=({wgo.position.x:F1},{wgo.position.y:F1},{wgo.position.z:F1}) | {GetBodySummary(wgo.item)}");
                    bodiesFound++;
                }
            }
            
            if (bodiesFound == 0)
            {
                CoopMod.Logger.LogWarning("[SaveDiag]   NO BODIES FOUND in any WGO inventory during save!");
            }
        }
        
        /// <summary>
        /// Log when SaveSlotData.PrepareForSave runs (writes .info metadata).
        /// </summary>
        [HarmonyPatch(typeof(SaveSlotData), "PrepareForSave")]
        [HarmonyPostfix]
        public static void SlotPrepareForSave_Postfix(SaveSlotData __instance)
        {
            if (!DiagnosticsEnabled) return;

            CoopMod.Logger.LogInfo($"[SaveDiag] === SaveSlotData.PrepareForSave completed ===");
            CoopMod.Logger.LogInfo($"[SaveDiag]   game_time: {__instance.game_time}");
            CoopMod.Logger.LogInfo($"[SaveDiag]   real_time: {__instance.real_time}");
            CoopMod.Logger.LogInfo($"[SaveDiag]   filename: {__instance.filename_no_extension}");
            CoopMod.Logger.LogInfo($"[SaveDiag]   version: {__instance.version}");
            
            if (__instance.game_time <= 0.001f)
            {
                CoopMod.Logger.LogError($"[SaveDiag]   game_time is ZERO! This save will be DELETED on next RedrawSlots!");
            }
        }
        
        /// <summary>
        /// Log when PlatformSpecific.SaveGameDataToSlot runs.
        /// </summary>
        [HarmonyPatch(typeof(PlatformSpecific), "SaveGameDataToSlot")]
        [HarmonyPrefix]
        public static void SaveGameDataToSlot_Prefix(SaveSlotData slot, GameSave save)
        {
            if (!DiagnosticsEnabled) return;

            CoopMod.Logger.LogInfo($"[SaveDiag] === PlatformSpecific.SaveGameDataToSlot starting ===");
            CoopMod.Logger.LogInfo($"[SaveDiag]   slot null: {slot == null}");
            if (slot != null)
            {
                CoopMod.Logger.LogInfo($"[SaveDiag]   slot filename: {slot.filename_no_extension}");
                CoopMod.Logger.LogInfo($"[SaveDiag]   slot game_time: {slot.game_time}");
            }
            CoopMod.Logger.LogInfo($"[SaveDiag]   save null: {save == null}");
            CoopMod.Logger.LogInfo($"[SaveDiag]   MainGame.game_time: {MainGame.game_time}");
            CoopMod.Logger.LogInfo($"[SaveDiag]   MainGame.game_started: {MainGame.game_started}");
        }
        
        /// <summary>
        /// Log after RestoreScene — shows what WGOs were restored and whether bodies survived.
        /// </summary>
        [HarmonyPatch(typeof(SerializableGameMap), "RestoreScene")]
        [HarmonyPostfix]
        public static void RestoreScene_Postfix(SerializableGameMap __instance)
        {
            if (!DiagnosticsEnabled) return;

            CoopMod.Logger.LogInfo("[SaveDiag] === RestoreScene completed ===");
            
            var wgosField = typeof(SerializableGameMap).GetField("_wgos",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (wgosField != null)
            {
                var wgos = wgosField.GetValue(__instance) as List<SerializableWGO>;
                if (wgos != null)
                {
                    CoopMod.Logger.LogInfo($"[SaveDiag]   WGOs in save data: {wgos.Count}");
                    
                    // Log all body-holder WGOs and scan for bodies
                    int bodiesFound = 0;
                    foreach (var wgo in wgos)
                    {
                        if (!IsBodyHolder(wgo.obj_id)) continue;
                        
                        bool hasBody = HasBodyItem(wgo.item);
                        if (hasBody) bodiesFound++;
                        
                        string bodyMarker = hasBody ? " HAS_BODY" : "";
                        CoopMod.Logger.LogInfo($"[SaveDiag]   RESTORE [{wgo.obj_id}] pos=({wgo.position.x:F1},{wgo.position.y:F1},{wgo.position.z:F1}){bodyMarker} | {GetBodySummary(wgo.item)}");
                    }
                    
                    // Also scan all WGOs for unexpected body locations
                    foreach (var wgo in wgos)
                    {
                        if (IsBodyHolder(wgo.obj_id)) continue;
                        if (HasBodyItem(wgo.item))
                        {
                            CoopMod.Logger.LogInfo($"[SaveDiag]   UNEXPECTED BODY in [{wgo.obj_id}] pos=({wgo.position.x:F1},{wgo.position.y:F1},{wgo.position.z:F1}) | {GetBodySummary(wgo.item)}");
                            bodiesFound++;
                        }
                    }
                    
                    CoopMod.Logger.LogInfo($"[SaveDiag]   Total bodies in restore data: {bodiesFound}");
                    
                    if (bodiesFound == 0)
                    {
                        CoopMod.Logger.LogWarning("[SaveDiag]   NO BODIES in restore data!");
                    }
                }
            }
            
            // Count what's now in the scene after restore
            if (MainGame.me?.world_root != null)
            {
                var restoredWGOs = MainGame.me.world_root.gameObject.GetComponentsInChildren<WorldGameObject>(true);
                CoopMod.Logger.LogInfo($"[SaveDiag]   WGOs now in world_root after restore: {restoredWGOs.Length}");
            }
        }
        
        /// <summary>
        /// Log when RedrawSlots runs — it DELETES saves with game_time ~= 0!
        /// </summary>
        [HarmonyPatch(typeof(SaveSlotsMenuGUI), "RedrawSlots")]
        [HarmonyPrefix]
        [HarmonyBefore("GraveyardKeeperCoop.Patches.SaveSlotsMenuPatches")]
        public static void RedrawSlots_DiagPrefix(List<SaveSlotData> slot_datas)
        {
            if (!DiagnosticsEnabled) return;

            CoopMod.Logger.LogInfo($"[SaveDiag] === RedrawSlots called with {slot_datas.Count} slots ===");
            
            foreach (var slot in slot_datas)
            {
                bool willBeDeleted = slot.game_time < 0.00001f;
                string deleteWarning = willBeDeleted ? " WILL_BE_DELETED!" : "";
                CoopMod.Logger.LogInfo($"[SaveDiag]   Slot: {slot.filename_no_extension} | game_time={slot.game_time} | real_time={slot.real_time}{deleteWarning}");
            }
        }
        
        /// <summary>
        /// Log when ClearSceneMap runs during load.
        /// </summary>
        [HarmonyPatch(typeof(SerializableGameMap), "ClearSceneMap")]
        [HarmonyPrefix]
        public static void ClearSceneMap_Prefix()
        {
            if (!DiagnosticsEnabled) return;

            CoopMod.Logger.LogInfo("[SaveDiag] === ClearSceneMap starting ===");
            
            if (MainGame.me?.world_root != null)
            {
                var existingWGOs = MainGame.me.world_root.GetComponentsInChildren<WorldGameObject>(true);
                CoopMod.Logger.LogInfo($"[SaveDiag]   WGOs about to be destroyed: {existingWGOs.Length}");
                
                // Count body holders with actual bodies being cleared
                int bodyHolders = 0;
                int bodiesBeingCleared = 0;
                foreach (var wgo in existingWGOs)
                {
                    if (IsBodyHolder(wgo.obj_id))
                    {
                        bodyHolders++;
                        if (wgo.data != null && HasBodyItem(wgo.data))
                        {
                            bodiesBeingCleared++;
                            CoopMod.Logger.LogInfo($"[SaveDiag]   CLEARING [{wgo.obj_id}] with body | {GetBodySummary(wgo.data)}");
                        }
                    }
                }
                CoopMod.Logger.LogInfo($"[SaveDiag]   Body holders: {bodyHolders}, with bodies: {bodiesBeingCleared}");
            }
        }
        
        /// <summary>
        /// Log when GameSave.ToBinary runs.
        /// </summary>
        [HarmonyPatch(typeof(GameSave), "ToBinary")]
        [HarmonyPriority(Priority.Low)]
        [HarmonyPrefix]
        public static void ToBinary_Prefix(GameSave __instance)
        {
            if (!DiagnosticsEnabled) return;

            CoopMod.Logger.LogInfo("[SaveDiag] === GameSave.ToBinary starting ===");
            CoopMod.Logger.LogInfo($"[SaveDiag]   player_position in save: {__instance.player_position}");
        }
        
        /// <summary>
        /// Log after ToBinary to confirm serialization succeeded.
        /// </summary>
        [HarmonyPatch(typeof(GameSave), "ToBinary")]
        [HarmonyPriority(Priority.Low)]
        [HarmonyPostfix]
        public static void ToBinary_Postfix(byte[] __result)
        {
            if (!DiagnosticsEnabled) return;

            CoopMod.Logger.LogInfo($"[SaveDiag] === GameSave.ToBinary completed, binary size: {__result?.Length ?? 0} bytes ===");
        }
    }
}

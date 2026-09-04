using HarmonyLib;
using UnityEngine;
using System.Collections.Generic;
using GraveyardKeeperCoop.LocalCoop;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches to give each player their own inventory in local co-op.
    /// 
    /// Problem: The game's UI always uses MainGame.me.player (Player 1) for inventory operations.
    /// This means when P2 opens their inventory, they see P1's items.
    /// 
    /// Solution: Track which player opened the UI and redirect inventory operations to that player.
    /// 
    /// How it works:
    /// 1. Player2InputPatches.GetKeyDown_Prefix tracks when P2 presses Inventory key
    /// 2. This sets LastInventoryInputPlayer to P2's wgo BEFORE UpdatePlayer processes it
    /// 3. When GameGUI.Open or InventoryGUI.Open runs, we use LastInventoryInputPlayer
    /// 4. The inventory is then populated with the correct player's items
    /// </summary>
    public static class InventoryIsolationPatches
    {
        /// <summary>
        /// The player who last pressed an inventory/menu key.
        /// Set immediately when input is detected, before UI opens.
        /// </summary>
        public static WorldGameObject LastInventoryInputPlayer { get; set; } = null;

        /// <summary>
        /// The player who currently has a UI open (inventory, chest, craft, etc.)
        /// This is set when a UI actually opens and cleared when it closes.
        /// </summary>
        public static WorldGameObject ActiveUIPlayer { get; set; } = null;

        /// <summary>
        /// Call this when a player presses an inventory/menu key
        /// </summary>
        public static void SetLastInventoryInputPlayer(WorldGameObject player)
        {
            LastInventoryInputPlayer = player;
            
            var manager = LocalCoopManager.Instance;
            if (manager != null)
            {
                string playerName = (player == manager.Player1?.wgo) ? "Player 1" : 
                                   (player == manager.Player2?.wgo) ? "Player 2" : "Unknown";
                CoopMod.Logger.LogInfo($"[InventoryIsolation] Inventory input from: {playerName}");
            }
        }

        /// <summary>
        /// Call this when a player opens any inventory-related UI
        /// </summary>
        public static void SetActiveUIPlayer(WorldGameObject player)
        {
            ActiveUIPlayer = player;
            
            var manager = LocalCoopManager.Instance;
            if (manager != null)
            {
                string playerName = (player == manager.Player1?.wgo) ? "Player 1" : 
                                   (player == manager.Player2?.wgo) ? "Player 2" : "Unknown";
                CoopMod.Logger.LogInfo($"[InventoryIsolation] Active UI player set to: {playerName}");
            }
        }

        /// <summary>
        /// Get the correct player for inventory operations.
        /// Priority: ActiveUIPlayer > LastInventoryInputPlayer > CurrentInteractingPlayer > MainGame.me.player
        /// </summary>
        public static WorldGameObject GetInventoryPlayer()
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
            {
                return MainGame.me.player;
            }

            // If we have an active UI player already set, use them
            if (ActiveUIPlayer != null)
            {
                CoopMod.Logger.LogInfo($"[InventoryIsolation] GetInventoryPlayer: Using ActiveUIPlayer = {(ActiveUIPlayer == manager.Player2?.wgo ? "P2" : "P1")}");
                return ActiveUIPlayer;
            }

            // If we know who just pressed the inventory key, use them
            if (LastInventoryInputPlayer != null)
            {
                CoopMod.Logger.LogInfo($"[InventoryIsolation] GetInventoryPlayer: Using LastInventoryInputPlayer = {(LastInventoryInputPlayer == manager.Player2?.wgo ? "P2" : "P1")}");
                return LastInventoryInputPlayer;
            }

            // Fall back to the interacting player from dialogue patches
            var interactingPlayer = LocalCoopDialoguePatches.CurrentInteractingPlayer;
            if (interactingPlayer != null)
            {
                CoopMod.Logger.LogInfo($"[InventoryIsolation] GetInventoryPlayer: Using CurrentInteractingPlayer = {(interactingPlayer == manager.Player2?.wgo ? "P2" : "P1")}");
                return interactingPlayer;
            }

            // Default to Player 1
            CoopMod.Logger.LogInfo("[InventoryIsolation] GetInventoryPlayer: Falling back to P1 (MainGame.me.player)");
            return MainGame.me.player;
        }

        /// <summary>
        /// Clear the active UI player when UI closes
        /// </summary>
        public static void ClearActiveUIPlayer()
        {
            if (ActiveUIPlayer != null)
            {
                CoopMod.Logger.LogInfo("[InventoryIsolation] Active UI player cleared");
                ActiveUIPlayer = null;
            }
            // Also clear last input player
            LastInventoryInputPlayer = null;
        }
    }

    /// <summary>
    /// Patch WorldGameObject.GetMultiInventory to use the correct player for inventory operations.
    /// This is the CRITICAL patch - GetMultiInventory always uses MainGame.me.player internally,
    /// even when called on a different player's WGO. We need to redirect this.
    /// 
    /// The game's code does:
    ///   if (player_mi == IncludePlayer || flag2 || this.is_player)
    ///       Inventory inventory = new Inventory(MainGame.me.player);  // Always P1!
    /// 
    /// We need it to use the correct player when an ActiveUIPlayer is set.
    /// 
    /// IMPORTANT: This patch ONLY applies when:
    /// 1. Local co-op is active
    /// 2. ActiveUIPlayer is set to P2
    /// 3. The call is on P2's WGO (to avoid affecting other GetMultiInventory calls like map, etc.)
    /// </summary>
    [HarmonyPatch(typeof(WorldGameObject), "GetMultiInventory")]
    public class WorldGameObject_GetMultiInventory_Patch
    {
        /// <summary>
        /// When P2 opens the inventory, the game still calls GetMultiInventory on MainGame.me.player (P1).
        /// We need to intercept this and return P2's inventory instead.
        /// 
        /// The key insight is: when ActiveUIPlayer is P2, we want GetMultiInventory calls that
        /// would normally return P1's inventory to return P2's inventory instead.
        /// </summary>
        [HarmonyPrefix]
        public static bool Prefix(WorldGameObject __instance, ref MultiInventory __result, 
            List<WorldGameObject> exceptions, string force_world_zone, 
            ref MultiInventory.PlayerMultiInventory player_mi, bool include_toolbelt, 
            bool sortWGOS, bool include_bags)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return true; // Run original

            var activePlayer = InventoryIsolationPatches.ActiveUIPlayer;
            
            // Only intercept when P2 has the UI open
            if (activePlayer == null || activePlayer != manager.Player2?.wgo)
                return true; // Run original
            
            // Only intercept calls that would include the player's inventory
            // These are the calls from InventoryGUI that we need to redirect
            bool isPlayerInventoryCall = 
                (__instance == MainGame.me.player && player_mi != MultiInventory.PlayerMultiInventory.ExcludePlayer) ||
                __instance.is_player;
            
            if (!isPlayerInventoryCall)
                return true; // Run original
            
            CoopMod.Logger.LogInfo($"[InventoryIsolation] Intercepting GetMultiInventory for P2 (called on {(__instance == MainGame.me.player ? "P1" : "other")})");
            
            // Build a MultiInventory for Player 2 instead
            __result = BuildP2MultiInventory(manager.Player2.wgo, include_toolbelt, include_bags);
            
            return false; // Skip original
        }

        /// <summary>
        /// Build a MultiInventory containing P2's items
        /// </summary>
        private static MultiInventory BuildP2MultiInventory(WorldGameObject p2Wgo, bool include_toolbelt, bool include_bags)
        {
            var mi = new MultiInventory();
            
            // Add P2's main inventory
            Inventory p2MainInventory = new Inventory(p2Wgo);
            mi.AddInventory(p2MainInventory, -1);
            CoopMod.Logger.LogInfo($"[InventoryIsolation] Added P2 main inventory with {p2MainInventory.data?.inventory?.Count ?? 0} item types");
            
            // Add P2's toolbelt (secondary inventory)
            if (include_toolbelt && p2Wgo.data?.secondary_inventory != null)
            {
                Item toolbeltContainer = new Item
                {
                    inventory = p2Wgo.data.secondary_inventory,
                    inventory_size = 7
                };
                Inventory toolbeltInv = new Inventory(toolbeltContainer, "toolbelt");
                mi.AddInventory(toolbeltInv, -1);
                CoopMod.Logger.LogInfo("[InventoryIsolation] Added P2 toolbelt");
            }
            
            // Add P2's bags if requested
            if (include_bags)
            {
                // Check for bags in P2's main inventory
                if (p2Wgo.data?.inventory != null)
                {
                    foreach (var item in p2Wgo.data.inventory)
                    {
                        if (item?.inventory != null && item.inventory.Count > 0)
                        {
                            Inventory bagInv = new Inventory(item, "bag");
                            mi.AddInventory(bagInv, -1);
                            CoopMod.Logger.LogInfo("[InventoryIsolation] Added P2 bag");
                        }
                    }
                }
                
                // Check toolbelt for bags too
                if (p2Wgo.data?.secondary_inventory != null)
                {
                    foreach (var item in p2Wgo.data.secondary_inventory)
                    {
                        if (item?.inventory != null && item.inventory.Count > 0)
                        {
                            Inventory bagInv = new Inventory(item, "bag");
                            mi.AddInventory(bagInv, -1);
                        }
                    }
                }
            }
            
            return mi;
        }
    }

    /// <summary>
    /// Patch InventoryGUI.Open - just let the original run, but the WorldGameObject_GetMultiInventory_Patch
    /// will redirect to P2's inventory when needed. We just need to log for debugging.
    /// 
    /// NOTE: We previously tried to completely replace Open() but it caused infinite loops
    /// because of complex interactions with base class methods. The simpler approach is to
    /// let the original run and intercept GetMultiInventory.
    /// </summary>
    [HarmonyPatch(typeof(InventoryGUI), "Open")]
    [HarmonyPatch(new System.Type[] { })] // Parameterless Open()
    public class InventoryGUI_Open_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(InventoryGUI __instance)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return;

            // Get the correct player for this inventory session
            WorldGameObject inventoryPlayer = InventoryIsolationPatches.GetInventoryPlayer();
            
            // CRITICAL: Set the active UI player so input routing works correctly
            InventoryIsolationPatches.SetActiveUIPlayer(inventoryPlayer);
            
            // CRITICAL: Clear the GameGUI key from the player's controller so it doesn't
            // immediately close the GUI that was just opened
            if (inventoryPlayer == manager.Player2?.wgo)
            {
                Player2InputPatches.ClearP2PressedKey(GameKey.GameGUI);
                Player2InputPatches.ClearP2PressedKey(GameKey.Inventory);
                CoopMod.Logger.LogInfo("[InventoryIsolation] InventoryGUI.Open - P2 detected, cleared GameGUI key, WorldGameObject_GetMultiInventory_Patch will redirect");
            }
            else if (inventoryPlayer == manager.Player1?.wgo)
            {
                Player2InputPatches.ClearP1PressedKey(GameKey.GameGUI);
                Player2InputPatches.ClearP1PressedKey(GameKey.Inventory);
            }
            // Let original run - WorldGameObject_GetMultiInventory_Patch handles the redirection
        }
    }

    /// <summary>
    /// Patch InventoryGUI.RedrawPlayerInfoAndToolbelt to use correct player
    /// </summary>
    [HarmonyPatch(typeof(InventoryGUI), "RedrawPlayerInfoAndToolbelt")]
    public class InventoryGUI_RedrawPlayerInfo_Patch
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            // This method uses MainGame.me.player internally
            // We'll patch the specific calls that need redirection
        }
    }

    /// <summary>
    /// Patch ChestGUI.Open to use the correct player's inventory
    /// </summary>
    [HarmonyPatch(typeof(ChestGUI), "Open")]
    public class ChestGUI_Open_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(ChestGUI __instance, WorldGameObject chest_obj)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return true; // Run original

            WorldGameObject inventoryPlayer = InventoryIsolationPatches.GetInventoryPlayer();
            
            if (inventoryPlayer == null || inventoryPlayer == MainGame.me.player)
                return true; // Run original

            // Player 2 is opening the chest - redirect
            CoopMod.Logger.LogInfo("[InventoryIsolation] Redirecting ChestGUI.Open to Player 2's inventory");
            
            if (chest_obj == null)
            {
                UnityEngine.Debug.LogError("Cannot open chest gui for null obj");
                return false;
            }

            // Call base.Open()
            var baseOpenMethod = typeof(BaseGUI).GetMethod("Open", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            baseOpenMethod?.Invoke(__instance, null);

            // Set chest object
            var chestObjField = typeof(ChestGUI).GetField("_chest_obj", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            chestObjField?.SetValue(__instance, chest_obj);

            // Get player inventory - using the correct player!
            MultiInventory playerInventory = inventoryPlayer.GetMultiInventory(
                new List<WorldGameObject> { chest_obj }, 
                "", 
                MultiInventory.PlayerMultiInventory.DontChange, 
                false, true, true
            );

            var playerInvField = typeof(ChestGUI).GetField("_player_inventory", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            playerInvField?.SetValue(__instance, playerInventory);

            // Get chest inventory
            MultiInventory chestInventory = chest_obj.GetMultiInventoryOfWGOWithoutWorldZone(true);
            var chestInvField = typeof(ChestGUI).GetField("_chest_inventory", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            chestInvField?.SetValue(__instance, chestInventory);

            // Setup panels using public fields
            __instance.player_panel.Open(playerInventory, 1, 0, false, -1, false);
            __instance.chest_panel.Open(chestInventory, 2, 0, false, -1, false);
            __instance.player_panel.SetGrayToNotMainWidgets(true);

            // Handle gamepad focus
            if (BaseGUI.for_gamepad)
            {
                __instance.gamepad_controller.ReinitItems(false);
                __instance.gamepad_controller.FocusOnFirstActive(-1);
            }

            MainGame.SetPausedMode(true);

            return false; // Skip original
        }
    }

    // NOTE: BaseGUI.Hide patch is in LocalCoopDialoguePatches.cs - it also handles clearing ActiveUIPlayer

    /// <summary>
    /// Patch GameGUI.Open to track which player opened it
    /// </summary>
    [HarmonyPatch(typeof(GameGUI), "Open")]
    public class GameGUI_Open_Patch
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return;

            // Get who pressed the inventory key (set by Player2InputPatches)
            var inventoryPlayer = InventoryIsolationPatches.GetInventoryPlayer();
            InventoryIsolationPatches.SetActiveUIPlayer(inventoryPlayer);
            
            // CRITICAL: Clear the GameGUI key from the player's controller so it doesn't
            // immediately close the GUI that was just opened (the game checks this key in Update)
            if (inventoryPlayer == manager.Player2?.wgo)
            {
                Player2InputPatches.ClearP2PressedKey(GameKey.GameGUI);
                Player2InputPatches.ClearP2PressedKey(GameKey.Inventory);
                CoopMod.Logger.LogInfo("[InventoryIsolation] GameGUI.Open - P2, cleared keys");
            }
            else
            {
                Player2InputPatches.ClearP1PressedKey(GameKey.GameGUI);
                Player2InputPatches.ClearP1PressedKey(GameKey.Inventory);
            }
        }
    }

    /// <summary>
    /// Patch GameGUI.OpenAtTab to track which player opened it  
    /// </summary>
    [HarmonyPatch(typeof(GameGUI), "OpenAtTab")]
    public class GameGUI_OpenAtTab_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(GameGUI.TabType tab)
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return;

            // Get who pressed the inventory key (set by Player2InputPatches)
            var inventoryPlayer = InventoryIsolationPatches.GetInventoryPlayer();
            InventoryIsolationPatches.SetActiveUIPlayer(inventoryPlayer);
            
            // CRITICAL: Clear the key from the player's controller
            if (inventoryPlayer == manager.Player2?.wgo)
            {
                Player2InputPatches.ClearP2PressedKey(GameKey.GameGUI);
                Player2InputPatches.ClearP2PressedKey(GameKey.Inventory);
                Player2InputPatches.ClearP2PressedKey(GameKey.Map);
                Player2InputPatches.ClearP2PressedKey(GameKey.Techs);
                Player2InputPatches.ClearP2PressedKey(GameKey.KnownNPCs);
            }
            else
            {
                Player2InputPatches.ClearP1PressedKey(GameKey.GameGUI);
                Player2InputPatches.ClearP1PressedKey(GameKey.Inventory);
                Player2InputPatches.ClearP1PressedKey(GameKey.Map);
                Player2InputPatches.ClearP1PressedKey(GameKey.Techs);
                Player2InputPatches.ClearP1PressedKey(GameKey.KnownNPCs);
            }
            
            CoopMod.Logger.LogInfo($"[InventoryIsolation] GameGUI.OpenAtTab({tab}) - player: {(inventoryPlayer == manager.Player2?.wgo ? "P2" : "P1")}");
        }
    }

    /// <summary>
    /// Patch GameGUI.Hide to clear the active UI player when the GUI closes
    /// </summary>
    [HarmonyPatch(typeof(GameGUI), "Hide")]
    public class GameGUI_Hide_Patch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            var manager = LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return;

            CoopMod.Logger.LogInfo("[InventoryIsolation] GameGUI.Hide - clearing active UI player");
            InventoryIsolationPatches.ClearActiveUIPlayer();
        }
    }
}

using HarmonyLib;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using GraveyardKeeperCoop.Utils;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches for MapGUI to add player location indicators
    /// Shows the position of the local player and any connected remote players
    /// </summary>
    [HarmonyPatch(typeof(MapGUI))]
    public class MapGUIPatches
    {
        private static Dictionary<string, GameObject> playerIndicators = new Dictionary<string, GameObject>();
        private static GameObject localPlayerIndicator;
        private static GameObject player2Indicator;
        private static Transform mapTransform;
        private static Transform mapContentsTransform;
        private static MapGUI cachedMapGUI;

        /// <summary>
        /// Patch the MapGUI.Init method to set up our indicator system
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch("Init")]
        public static void Init_Postfix(MapGUI __instance)
        {
            try
            {
                CoopMod.Logger.LogInfo("Initializing map player indicators");
                
                // Store references to map GUI and transform
                cachedMapGUI = __instance;
                mapTransform = __instance.transform;
                
                // Clean up any existing indicators
                CleanupIndicators();
                
                CoopMod.Logger.LogInfo("Map player indicators initialized");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"Error initializing map indicators: {ex.Message}");
            }
        }

        /// <summary>
        /// Patch the MapGUI.Open method to create/update player indicators
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch("Open")]
        public static void Open_Postfix(MapGUI __instance)
        {
            try
            {
                CoopMod.Logger.LogInfo("MapGUI.Open() called - updating player indicators");
                
                // Initialize if not already done (in case Init was called before mod loaded)
                if (mapTransform == null)
                {
                    CoopMod.Logger.LogInfo("Late initialization of map indicator system");
                    mapTransform = __instance.transform;
                    CleanupIndicators();
                }
                
                // Create or update local player indicator
                UpdateLocalPlayerIndicator(__instance);
                
                // Create or update Player 2 indicator if local co-op is active
                UpdatePlayer2Indicator(__instance);
                
                CoopMod.Logger.LogInfo($"Player indicators updated. P1 active: {localPlayerIndicator != null && localPlayerIndicator.activeSelf}, P2 active: {player2Indicator != null && player2Indicator.activeSelf}");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"Error opening map indicators: {ex.Message}");
                CoopMod.Logger.LogError($"Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Patch the MapGUI.Update method to continuously update indicator positions
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch("Update")]
        public static void Update_Postfix(MapGUI __instance)
        {
            try
            {
                // Only update if map is open
                if (__instance.gameObject.activeSelf)
                {
                    if (localPlayerIndicator != null)
                    {
                        UpdateLocalPlayerIndicator(__instance);
                    }
                    if (player2Indicator != null)
                    {
                        UpdatePlayer2Indicator(__instance);
                    }
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"Error updating map indicators: {ex.Message}");
            }
        }

        /// <summary>
        /// Patch the MapGUI.OnDisable to clean up indicators
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch("OnDisable")]
        public static void OnDisable_Postfix(MapGUI __instance)
        {
            try
            {
                // Hide indicators when map is closed (don't destroy them)
                if (localPlayerIndicator != null)
                {
                    localPlayerIndicator.SetActive(false);
                }
                
                if (player2Indicator != null)
                {
                    player2Indicator.SetActive(false);
                }
                
                foreach (var indicator in playerIndicators.Values)
                {
                    if (indicator != null)
                    {
                        indicator.SetActive(false);
                    }
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"Error disabling map indicators: {ex.Message}");
            }
        }

        /// <summary>
        /// Create or update the local player indicator
        /// </summary>
        private static void UpdateLocalPlayerIndicator(MapGUI mapGUI)
        {
            if (MainGame.me == null || MainGame.me.player == null)
            {
                return;
            }

            // Create indicator if it doesn't exist
            if (localPlayerIndicator == null)
            {
                // Find the scroll view and attach to its content transform
                UIScrollView scrollView = mapGUI.GetComponentInChildren<UIScrollView>(true);
                
                CoopMod.Logger.LogInfo("=== MAP HIERARCHY DEBUG ===");
                CoopMod.Logger.LogInfo($"MapGUI transform: {mapGUI.transform.name}");
                CoopMod.Logger.LogInfo($"MapGUI layer: {mapGUI.gameObject.layer}");
                CoopMod.Logger.LogInfo($"ScrollView found: {scrollView != null}");
                
                if (scrollView != null)
                {
                    CoopMod.Logger.LogInfo($"ScrollView name: {scrollView.name}");
                    CoopMod.Logger.LogInfo($"ScrollView layer: {scrollView.gameObject.layer}");
                    CoopMod.Logger.LogInfo($"ScrollView children count: {scrollView.transform.childCount}");
                    
                    for (int i = 0; i < scrollView.transform.childCount; i++)
                    {
                        var child = scrollView.transform.GetChild(i);
                        CoopMod.Logger.LogInfo($"  Child {i}: {child.name}, layer: {child.gameObject.layer}, active: {child.gameObject.activeSelf}");
                        
                        // Check for UIPanel
                        var panel = child.GetComponent<UIPanel>();
                        if (panel != null)
                        {
                            CoopMod.Logger.LogInfo($"    UIPanel depth: {panel.depth}");
                        }
                        
                        // Log first few grandchildren with their map AND world positions
                        if (child.childCount > 0)
                        {
                            CoopMod.Logger.LogInfo($"    Has {child.childCount} grandchildren");
                            for (int j = 0; j < Mathf.Min(5, child.childCount); j++)  
                            {
                                var grandchild = child.GetChild(j);
                                CoopMod.Logger.LogInfo($"      Grandchild {j}: {grandchild.name}, map pos: {grandchild.localPosition}");
                                
                                // Try to find the WorldZone for this map element
                                WorldZone zone = WorldZone.GetZoneByID(grandchild.name, false);
                                if (zone != null)
                                {
                                    Vector3 worldPos = zone.center_tf.position;
                                    CoopMod.Logger.LogInfo($"        World pos: ({worldPos.x:F1}, {worldPos.y:F1}, {worldPos.z:F1})");
                                }
                            }
                        }
                    }
                }
                
                Transform parentTransform = (scrollView != null && scrollView.transform.childCount > 0) 
                    ? scrollView.transform.GetChild(0) // Attach to first child (usually the content)
                    : mapGUI.transform; // Fallback to map GUI itself
                
                // Cache for remote-player indicators so they get attached to the same pan/zoom parent
                mapContentsTransform = parentTransform;
                
                CoopMod.Logger.LogInfo($"Attaching indicator to: {parentTransform.name}");
                localPlayerIndicator = CreatePlayerIndicator(mapGUI, parentTransform, "LocalPlayer", Color.green, Steamworks.CSteamID.Nil);
                
                // If indicator creation failed, just return - nothing more we can do
                if (localPlayerIndicator == null)
                {
                    CoopMod.Logger.LogWarning("Failed to create local player indicator - MapZoneGUI template not found");
                    return;
                }
            }

            // Get player world position
            Vector3 playerWorldPos = MainGame.me.player.transform.position;
            
            // Convert world position to map position using helper
            Vector2 mapPos = MapCoordinateHelper.WorldToMap(playerWorldPos);
            
            CoopMod.Logger.LogInfo($"Player world pos: ({playerWorldPos.x:F1}, {playerWorldPos.y:F1}, {playerWorldPos.z:F1})");
            CoopMod.Logger.LogInfo($"Converted to map pos: ({mapPos.x:F1}, {mapPos.y:F1})");
            
            // Check current zone for reference
            WorldZone currentZone = MainGame.me.player.GetMyWorldZone();
            if (currentZone != null)
            {
                CoopMod.Logger.LogInfo($"Player is in zone: {currentZone.id}");
                
                // Log the actual WorldZone center transform position
                Vector3 zoneCenterWorld = currentZone.center_tf.position;
                CoopMod.Logger.LogInfo($"Zone '{currentZone.id}' center in world: ({zoneCenterWorld.x:F1}, {zoneCenterWorld.y:F1}, {zoneCenterWorld.z:F1})");
                
                // Try to find this zone on the map to see its position
                UIScrollView scrollView = mapGUI.GetComponentInChildren<UIScrollView>();
                if (scrollView != null && scrollView.transform.childCount > 0)
                {
                    Transform contentsTransform = scrollView.transform.GetChild(0);
                    Transform zoneTransform = contentsTransform.Find(currentZone.id);
                    if (zoneTransform != null)
                    {
                        CoopMod.Logger.LogInfo($"Found zone '{currentZone.id}' on map at local position: {zoneTransform.localPosition}");
                        CoopMod.Logger.LogInfo($"  Our calculated position would be offset by: ({mapPos.x - zoneTransform.localPosition.x:F1}, {mapPos.y - zoneTransform.localPosition.y:F1})");
                    }
                    else
                    {
                        CoopMod.Logger.LogInfo($"Zone '{currentZone.id}' not found on map (might not have a visible label)");
                    }
                }
            }
            
            // Update indicator position with calculated coordinates
            localPlayerIndicator.transform.localPosition = new Vector3(mapPos.x, mapPos.y, 0f);
            
            CoopMod.Logger.LogInfo($"Indicator positioned at: ({mapPos.x:F1}, {mapPos.y:F1})");
            
            // Ensure indicator is active
            if (!localPlayerIndicator.activeSelf)
            {
                localPlayerIndicator.SetActive(true);
            }
        }

        /// <summary>
        /// Create or update the Player 2 indicator (local co-op)
        /// </summary>
        private static void UpdatePlayer2Indicator(MapGUI mapGUI)
        {
            var manager = GraveyardKeeperCoop.LocalCoop.LocalCoopManager.Instance;
            if (manager == null || manager.Player2 == null)
            {
                // No P2 spawned, hide indicator if it exists
                if (player2Indicator != null)
                {
                    player2Indicator.SetActive(false);
                }
                return;
            }

            // Create indicator if it doesn't exist
            if (player2Indicator == null)
            {
                // Find the scroll view and attach to its content transform
                UIScrollView scrollView = mapGUI.GetComponentInChildren<UIScrollView>(true);
                
                Transform parentTransform = (scrollView != null && scrollView.transform.childCount > 0) 
                    ? scrollView.transform.GetChild(0) // Attach to first child (usually the content)
                    : mapGUI.transform; // Fallback to map GUI itself
                
                // Store contents transform for reuse
                mapContentsTransform = parentTransform;
                
                CoopMod.Logger.LogInfo($"Creating Player 2 indicator on: {parentTransform.name}");
                player2Indicator = CreateSimpleIndicatorForPlayer2(parentTransform, "Player2");
                
                if (player2Indicator == null)
                {
                    CoopMod.Logger.LogWarning("Failed to create Player 2 indicator");
                    return;
                }
            }

            // Get Player 2 world position
            Vector3 p2WorldPos = manager.Player2.transform.position;
            
            // Convert world position to map position using helper
            Vector2 mapPos = MapCoordinateHelper.WorldToMap(p2WorldPos);
            
            // Update indicator position
            player2Indicator.transform.localPosition = new Vector3(mapPos.x, mapPos.y, 0f);
            
            // Ensure indicator is active
            if (!player2Indicator.activeSelf)
            {
                player2Indicator.SetActive(true);
            }
        }

        /// <summary>
        /// Create a simple indicator for Player 2 (different color from P1)
        /// </summary>
        private static GameObject CreateSimpleIndicatorForPlayer2(Transform parent, string playerName)
        {
            CoopMod.Logger.LogInfo($"Creating Player 2 indicator");
            
            // Create a new GameObject
            GameObject indicatorObj = new GameObject($"PlayerIndicator_{playerName}");
            indicatorObj.transform.SetParent(parent, false);
            indicatorObj.layer = parent.gameObject.layer;
            
            // Try to get P2's Steam avatar if available, otherwise use a different colored indicator
            // For now, use a blue-colored indicator to distinguish from P1's green
            Texture2D avatarTexture = null;
            
            // TODO: Get P2's Steam avatar when we have their Steam ID
            // For local co-op, P2 might be the same Steam user or a guest
            
            if (avatarTexture != null)
            {
                UITexture avatarDisplay = indicatorObj.AddComponent<UITexture>();
                avatarDisplay.mainTexture = avatarTexture;
                avatarDisplay.width = 32;
                avatarDisplay.height = 32;
                avatarDisplay.depth = 99; // Slightly below P1
                avatarDisplay.shader = Shader.Find("Unlit/Transparent Colored");
            }
            else
            {
                // Create a blue-tinted version using the same Steam avatar but with color tint
                // Or just use a colored square
                Texture2D p1Avatar = GraveyardKeeperCoopMod.Utils.SteamHelper.GetLocalPlayerAvatar();
                
                if (p1Avatar != null)
                {
                    UITexture avatarDisplay = indicatorObj.AddComponent<UITexture>();
                    avatarDisplay.mainTexture = p1Avatar;
                    avatarDisplay.width = 32;
                    avatarDisplay.height = 32;
                    avatarDisplay.depth = 99;
                    avatarDisplay.color = new Color(0.5f, 0.7f, 1f, 1f); // Blue tint
                    avatarDisplay.shader = Shader.Find("Unlit/Transparent Colored");
                    
                    CoopMod.Logger.LogInfo("Created P2 indicator with blue-tinted avatar");
                }
                else
                {
                    // Fallback to colored square
                    UISprite sprite = indicatorObj.AddComponent<UISprite>();
                    sprite.width = 24;
                    sprite.height = 24;
                    sprite.depth = 99;
                    sprite.color = new Color(0.3f, 0.5f, 1f, 1f); // Blue color for P2
                    sprite.type = UIBasicSprite.Type.Simple;
                    
                    CoopMod.Logger.LogInfo("Created P2 indicator with blue square fallback");
                }
            }
            
            // Add a pulsing effect (slightly different timing from P1)
            TweenScale pulseEffect = indicatorObj.AddComponent<TweenScale>();
            pulseEffect.from = new Vector3(1f, 1f, 1f);
            pulseEffect.to = new Vector3(1.15f, 1.15f, 1f);
            pulseEffect.duration = 0.9f; // Slightly different from P1
            pulseEffect.style = UITweener.Style.PingPong;
            pulseEffect.enabled = true;
            
            CoopMod.Logger.LogInfo($"=== PLAYER 2 INDICATOR CREATED ===");
            return indicatorObj;
        }

        /// <summary>
        /// Create a player indicator using Steam profile picture.
        /// If <paramref name="steamId"/> is CSteamID.Nil the LOCAL player's avatar is used;
        /// otherwise the avatar for that specific Steam user is fetched.
        /// </summary>
        private static GameObject CreatePlayerIndicator(MapGUI mapGUI, Transform parent, string playerName, Color color, Steamworks.CSteamID steamId)
        {
            CoopMod.Logger.LogInfo($"Creating Steam avatar indicator for {playerName} (steamId={steamId.m_SteamID})");
            
            // Find an existing MapZoneGUI to use as a template - search from mapGUI root
            MapZoneGUI templateZone = mapGUI.GetComponentInChildren<MapZoneGUI>(true);
            
            // Debug: List all MapZoneGUI components we can find
            MapZoneGUI[] allZones = mapGUI.GetComponentsInChildren<MapZoneGUI>(true);
            CoopMod.Logger.LogInfo($"Found {allZones.Length} MapZoneGUI components in MapGUI hierarchy");
            foreach (var zone in allZones)
            {
                CoopMod.Logger.LogInfo($"  MapZoneGUI: {zone.name}, active: {zone.gameObject.activeSelf}");
            }
            
            if (templateZone == null)
            {
                // Fallback: Create a simple indicator without cloning
                CoopMod.Logger.LogWarning("No MapZoneGUI template found - creating simple indicator");
                return CreateSimpleIndicator(parent, playerName, color);
            }
            
            CoopMod.Logger.LogInfo($"Found template zone: {templateZone.name}");
            
            // Clone the template zone GameObject
            GameObject indicatorObj = UnityEngine.Object.Instantiate(templateZone.gameObject, parent);
            indicatorObj.name = $"PlayerIndicator_{playerName}";
            indicatorObj.SetActive(true);
            
            CoopMod.Logger.LogInfo($"=== CREATING STEAM AVATAR INDICATOR ===");
            CoopMod.Logger.LogInfo($"  Cloned from: {templateZone.name}");
            CoopMod.Logger.LogInfo($"  GameObject: {indicatorObj.name}");
            CoopMod.Logger.LogInfo($"  Layer: {indicatorObj.layer}");
            
            // Remove the MapZoneGUI component since we don't need it
            MapZoneGUI zoneComponent = indicatorObj.GetComponent<MapZoneGUI>();
            if (zoneComponent != null)
            {
                UnityEngine.Object.Destroy(zoneComponent);
            }
            
            // Find the label in the cloned object
            UILabel existingLabel = indicatorObj.GetComponentInChildren<UILabel>();
            if (existingLabel != null)
            {
                CoopMod.Logger.LogInfo($"  Found cloned label: {existingLabel.name}");
                // Hide or repurpose the label
                existingLabel.text = "";
                existingLabel.enabled = false;
            }

            // Get Steam avatar texture using SteamHelper — remote players pass their own SteamID,
            // local player passes Nil which falls back to the local avatar.
            Texture2D avatarTexture = (steamId == Steamworks.CSteamID.Nil)
                ? GraveyardKeeperCoopMod.Utils.SteamHelper.GetLocalPlayerAvatar()
                : GraveyardKeeperCoopMod.Utils.SteamHelper.GetAvatarForSteamID(steamId);
            
            if (avatarTexture != null)
            {
                CoopMod.Logger.LogInfo($"  Got Steam avatar texture: {avatarTexture.width}x{avatarTexture.height}");
                
                // Create UITexture to display the avatar
                UITexture avatarDisplay = indicatorObj.AddComponent<UITexture>();
                avatarDisplay.mainTexture = avatarTexture;
                avatarDisplay.width = 32;  // Small avatar size
                avatarDisplay.height = 32;
                avatarDisplay.depth = 20;
                avatarDisplay.shader = Shader.Find("Unlit/Transparent Colored");
                
                CoopMod.Logger.LogInfo($"  UITexture configured: {avatarDisplay.width}x{avatarDisplay.height}, depth: {avatarDisplay.depth}");
                CoopMod.Logger.LogInfo($"  Shader: {avatarDisplay.shader?.name ?? "null"}");
                
                // Add a pulsing effect
                TweenScale pulseEffect = indicatorObj.AddComponent<TweenScale>();
                pulseEffect.from = new Vector3(1f, 1f, 1f);
                pulseEffect.to = new Vector3(1.1f, 1.1f, 1f);
                pulseEffect.duration = 1f;
                pulseEffect.style = UITweener.Style.PingPong;
                pulseEffect.enabled = true;
            }
            else
            {
                CoopMod.Logger.LogWarning("  Could not get Steam avatar texture!");
            }
            
            CoopMod.Logger.LogInfo($"=== INDICATOR CREATED ===");
            
            return indicatorObj;
        }

        /// <summary>
        /// Create a simple player indicator without cloning MapZoneGUI (fallback)
        /// </summary>
        private static GameObject CreateSimpleIndicator(Transform parent, string playerName, Color color)
        {
            CoopMod.Logger.LogInfo($"Creating simple indicator for {playerName}");
            
            // Create a new GameObject
            GameObject indicatorObj = new GameObject($"PlayerIndicator_{playerName}");
            indicatorObj.transform.SetParent(parent, false);
            indicatorObj.layer = parent.gameObject.layer;
            
            // Get Steam avatar texture
            Texture2D avatarTexture = GraveyardKeeperCoopMod.Utils.SteamHelper.GetLocalPlayerAvatar();
            
            if (avatarTexture != null)
            {
                CoopMod.Logger.LogInfo($"  Got Steam avatar texture: {avatarTexture.width}x{avatarTexture.height}");
                
                // Create UITexture to display the avatar
                UITexture avatarDisplay = indicatorObj.AddComponent<UITexture>();
                avatarDisplay.mainTexture = avatarTexture;
                avatarDisplay.width = 32;
                avatarDisplay.height = 32;
                avatarDisplay.depth = 100; // High depth to render on top
                avatarDisplay.shader = Shader.Find("Unlit/Transparent Colored");
                
                CoopMod.Logger.LogInfo($"  UITexture configured: {avatarDisplay.width}x{avatarDisplay.height}, depth: {avatarDisplay.depth}");
                
                // Add a pulsing effect
                TweenScale pulseEffect = indicatorObj.AddComponent<TweenScale>();
                pulseEffect.from = new Vector3(1f, 1f, 1f);
                pulseEffect.to = new Vector3(1.15f, 1.15f, 1f);
                pulseEffect.duration = 0.8f;
                pulseEffect.style = UITweener.Style.PingPong;
                pulseEffect.enabled = true;
            }
            else
            {
                CoopMod.Logger.LogWarning("  Could not get Steam avatar - creating colored square fallback");
                
                // Create a simple colored square as fallback
                UISprite sprite = indicatorObj.AddComponent<UISprite>();
                sprite.width = 24;
                sprite.height = 24;
                sprite.depth = 100;
                sprite.color = color;
                // Use a built-in atlas sprite if available
                sprite.type = UIBasicSprite.Type.Simple;
            }
            
            CoopMod.Logger.LogInfo($"=== SIMPLE INDICATOR CREATED ===");
            return indicatorObj;
        }

        /// <summary>
        /// Add or update a remote player indicator
        /// </summary>
        public static void UpdateRemotePlayerIndicator(string playerId, string playerName, Vector3 worldPosition, Color indicatorColor)
        {
            if (mapTransform == null || cachedMapGUI == null)
            {
                return;
            }

            if (!cachedMapGUI.gameObject.activeInHierarchy)
            {
                return;
            }

            // Prefer the scroll-contents transform (same parent as the local indicator) so the
            // remote indicator pans and zooms with the map. Fall back to the mapGUI root only
            // if we haven't cached contents yet (e.g. map hasn't been opened once).
            Transform parent = mapContentsTransform;
            if (parent == null)
            {
                UIScrollView scrollView = cachedMapGUI.GetComponentInChildren<UIScrollView>(true);
                if (scrollView != null && scrollView.transform.childCount > 0)
                {
                    parent = scrollView.transform.GetChild(0);
                    mapContentsTransform = parent;
                }
                else
                {
                    parent = mapTransform;
                }
            }

            // Create indicator if it doesn't exist
            if (!playerIndicators.ContainsKey(playerId) || playerIndicators[playerId] == null)
            {
                // Try to parse the playerId as a Steam ID so we can fetch the correct avatar.
                Steamworks.CSteamID remoteSteamId = Steamworks.CSteamID.Nil;
                if (ulong.TryParse(playerId, out ulong parsed))
                {
                    remoteSteamId = new Steamworks.CSteamID(parsed);
                }
                playerIndicators[playerId] = CreatePlayerIndicator(cachedMapGUI, parent, playerName, indicatorColor, remoteSteamId);
            }

            GameObject indicator = playerIndicators[playerId];
            
            // Null check in case creation failed
            if (indicator == null)
            {
                return;
            }

            // Convert world position to map position using helper
            Vector2 mapPos = MapCoordinateHelper.WorldToMap(worldPosition);
            
            // Update indicator position
            indicator.transform.localPosition = new Vector3(mapPos.x, mapPos.y, -10);
            
            // Keep the indicator enabled; its visibility follows the parent's activeSelf
            // (the scroll contents is active whenever the map is open).
            if (!indicator.activeSelf)
            {
                indicator.SetActive(true);
            }
        }

        /// <summary>
        /// Remove a remote player indicator
        /// </summary>
        public static void RemoveRemotePlayerIndicator(string playerId)
        {
            if (playerIndicators.ContainsKey(playerId))
            {
                if (playerIndicators[playerId] != null)
                {
                    Object.Destroy(playerIndicators[playerId]);
                }
                playerIndicators.Remove(playerId);
                
                CoopMod.Logger.LogInfo($"Removed player indicator for {playerId}");
            }
        }

        /// <summary>
        /// Clean up all indicators
        /// </summary>
        private static void CleanupIndicators()
        {
            if (localPlayerIndicator != null)
            {
                Object.Destroy(localPlayerIndicator);
                localPlayerIndicator = null;
            }

            if (player2Indicator != null)
            {
                Object.Destroy(player2Indicator);
                player2Indicator = null;
            }

            foreach (var indicator in playerIndicators.Values)
            {
                if (indicator != null)
                {
                    Object.Destroy(indicator);
                }
            }
            playerIndicators.Clear();
        }
    }
}

using HarmonyLib;
using UnityEngine;
using System.Collections.Generic;
using System.IO;
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
        private sealed class RemoteIndicatorState
        {
            public string PlayerName;
            public Vector3 WorldPosition;
            public Color Color;
        }

        private sealed class IndicatorVisualState
        {
            public UITexture AvatarWidget;
            public ulong SteamId;
            public Color AvatarTint;
            public float NextAvatarAttemptAt;
            public bool HasSteamAvatar;
            public UIWidget[] Widgets;
            public Vector3 LastMapPosition;
            public bool HasMapPosition;
        }

        private const string AvatarFrameResourceName =
            "GraveyardKeeperCoop.Assets.avatar_frame.png";
        private const int AvatarWidgetSize = 26;
        private const int FrameWidgetSize = 44;
        private const int AvatarDepth = 100;
        private const int FrameDepth = 101;
        private const float AvatarRetrySeconds = 2f;

        private static Dictionary<string, GameObject> playerIndicators = new Dictionary<string, GameObject>();
        private static readonly Dictionary<string, RemoteIndicatorState> remoteIndicatorStates =
            new Dictionary<string, RemoteIndicatorState>();
        private static readonly Dictionary<GameObject, IndicatorVisualState> indicatorVisualStates =
            new Dictionary<GameObject, IndicatorVisualState>();
        private static readonly Dictionary<ulong, Texture2D> circularAvatarTextures =
            new Dictionary<ulong, Texture2D>();
        private static GameObject localPlayerIndicator;
        private static GameObject player2Indicator;
        private static Transform mapTransform;
        private static Transform mapContentsTransform;
        private static MapGUI cachedMapGUI;
        private static Texture2D avatarFrameTexture;
        private static Texture2D fallbackCircleTexture;

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
                UpdateLocalPlayerIndicator(__instance, logDetails: true);
                
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

                    foreach (var pair in remoteIndicatorStates)
                    {
                        ApplyRemotePlayerIndicator(
                            pair.Key,
                            pair.Value.PlayerName,
                            pair.Value.WorldPosition,
                            pair.Value.Color);
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
        private static void UpdateLocalPlayerIndicator(MapGUI mapGUI, bool logDetails = false)
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
                localPlayerIndicator = CreatePlayerIndicator(
                    parentTransform,
                    "LocalPlayer",
                    Color.green,
                    GraveyardKeeperCoopMod.Utils.SteamHelper.GetLocalSteamID());
                
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
            Vector2 mapPos = MapCoordinateHelper.PlayerWorldToMap(playerWorldPos);
            
            if (logDetails)
            {
                CoopMod.Logger.LogInfo($"Player world pos: ({playerWorldPos.x:F1}, {playerWorldPos.y:F1}, {playerWorldPos.z:F1})");
                CoopMod.Logger.LogInfo($"Converted to map pos: ({mapPos.x:F1}, {mapPos.y:F1})");
                
                // Check current zone for reference when the map opens.
                WorldZone currentZone = MainGame.me.player.GetMyWorldZone();
                if (currentZone != null)
                {
                    CoopMod.Logger.LogInfo($"Player is in zone: {currentZone.id}");
                    Vector3 zoneCenterWorld = currentZone.center_tf.position;
                    CoopMod.Logger.LogInfo($"Zone '{currentZone.id}' center in world: ({zoneCenterWorld.x:F1}, {zoneCenterWorld.y:F1}, {zoneCenterWorld.z:F1})");

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
            }
            
            // Update indicator position with calculated coordinates
            localPlayerIndicator.transform.localPosition = new Vector3(mapPos.x, mapPos.y, 0f);
            
            if (logDetails)
                CoopMod.Logger.LogInfo($"Indicator positioned at: ({mapPos.x:F1}, {mapPos.y:F1})");
            
            // Ensure indicator is active
            if (!localPlayerIndicator.activeSelf)
            {
                localPlayerIndicator.SetActive(true);
            }

            RefreshIndicatorAvatar(localPlayerIndicator);
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
            Vector2 mapPos = MapCoordinateHelper.PlayerWorldToMap(p2WorldPos);
            
            // Update indicator position
            player2Indicator.transform.localPosition = new Vector3(mapPos.x, mapPos.y, 0f);
            
            // Ensure indicator is active
            if (!player2Indicator.activeSelf)
            {
                player2Indicator.SetActive(true);
            }
            RefreshIndicatorAvatar(player2Indicator);
        }

        /// <summary>
        /// Create a simple indicator for Player 2 (different color from P1)
        /// </summary>
        private static GameObject CreateSimpleIndicatorForPlayer2(Transform parent, string playerName)
        {
            Steamworks.CSteamID localSteamId =
                GraveyardKeeperCoopMod.Utils.SteamHelper.GetLocalSteamID();
            return CreateFramedIndicator(
                parent,
                playerName,
                localSteamId,
                new Color(0.3f, 0.5f, 1f, 1f),
                new Color(0.65f, 0.8f, 1f, 1f),
                0.9f,
                1.12f);
        }

        /// <summary>
        /// Create a player indicator using the supplied Steam profile picture.
        /// A missing Steam ID retains the colored circular fallback.
        /// </summary>
        private static GameObject CreatePlayerIndicator(
            Transform parent,
            string playerName,
            Color color,
            Steamworks.CSteamID steamId)
        {
            return CreateFramedIndicator(
                parent,
                playerName,
                steamId,
                color,
                Color.white,
                1f,
                1.08f);
        }

        private static GameObject CreateFramedIndicator(
            Transform parent,
            string playerName,
            Steamworks.CSteamID steamId,
            Color fallbackColor,
            Color avatarTint,
            float pulseDuration,
            float pulseScale)
        {
            if (parent == null)
                return null;

            GameObject indicator = new GameObject($"PlayerIndicator_{playerName}");
            indicator.transform.SetParent(parent, false);
            indicator.layer = parent.gameObject.layer;

            GameObject avatarObject = new GameObject("Avatar");
            avatarObject.transform.SetParent(indicator.transform, false);
            avatarObject.layer = indicator.layer;

            UITexture avatarWidget = avatarObject.AddComponent<UITexture>();
            avatarWidget.mainTexture = GetFallbackCircleTexture();
            avatarWidget.width = AvatarWidgetSize;
            avatarWidget.height = AvatarWidgetSize;
            avatarWidget.depth = AvatarDepth;
            avatarWidget.pivot = UIWidget.Pivot.Center;
            avatarWidget.color = fallbackColor;
            avatarWidget.shader = Shader.Find("Unlit/Transparent Colored");

            Texture2D frameTexture = GetAvatarFrameTexture();
            if (frameTexture != null)
            {
                GameObject frameObject = new GameObject("Frame");
                frameObject.transform.SetParent(indicator.transform, false);
                frameObject.layer = indicator.layer;

                UITexture frameWidget = frameObject.AddComponent<UITexture>();
                frameWidget.mainTexture = frameTexture;
                frameWidget.width = FrameWidgetSize;
                frameWidget.height = FrameWidgetSize;
                frameWidget.depth = FrameDepth;
                frameWidget.pivot = UIWidget.Pivot.Center;
                frameWidget.shader = Shader.Find("Unlit/Transparent Colored");
            }

            TweenScale pulseEffect = indicator.AddComponent<TweenScale>();
            pulseEffect.from = Vector3.one;
            pulseEffect.to = new Vector3(pulseScale, pulseScale, 1f);
            pulseEffect.duration = pulseDuration;
            pulseEffect.style = UITweener.Style.PingPong;
            pulseEffect.enabled = true;

            indicatorVisualStates[indicator] = new IndicatorVisualState
            {
                AvatarWidget = avatarWidget,
                SteamId = steamId.m_SteamID,
                AvatarTint = avatarTint,
                NextAvatarAttemptAt = 0f,
                HasSteamAvatar = false,
                Widgets = indicator.GetComponentsInChildren<UIWidget>(true)
            };

            RefreshIndicatorAvatar(indicator);
            CoopMod.Logger.LogInfo(
                $"Created framed map indicator for {playerName} (steamId={steamId.m_SteamID})");
            return indicator;
        }

        private static void RefreshIndicatorAvatar(GameObject indicator)
        {
            if (indicator == null ||
                !indicatorVisualStates.TryGetValue(indicator, out IndicatorVisualState state) ||
                state.HasSteamAvatar || state.SteamId == 0 || state.AvatarWidget == null ||
                Time.unscaledTime < state.NextAvatarAttemptAt)
            {
                return;
            }

            state.NextAvatarAttemptAt = Time.unscaledTime + AvatarRetrySeconds;
            Texture2D avatar = GetOrCreateCircularAvatar(state.SteamId);
            if (avatar == null)
                return;

            state.AvatarWidget.mainTexture = avatar;
            state.AvatarWidget.color = state.AvatarTint;
            state.AvatarWidget.MarkAsChanged();
            state.HasSteamAvatar = true;
        }

        private static Texture2D GetOrCreateCircularAvatar(ulong steamId)
        {
            if (circularAvatarTextures.TryGetValue(steamId, out Texture2D cachedAvatar) &&
                cachedAvatar != null)
            {
                return cachedAvatar;
            }

            Texture2D sourceAvatar = GraveyardKeeperCoopMod.Utils.SteamHelper.GetAvatarForSteamID(
                new Steamworks.CSteamID(steamId));
            if (sourceAvatar == null)
                return null;

            Texture2D circularAvatar = CreateCircularAvatar(sourceAvatar, steamId);
            Object.Destroy(sourceAvatar);
            if (circularAvatar != null)
                circularAvatarTextures[steamId] = circularAvatar;
            return circularAvatar;
        }

        private static Texture2D CreateCircularAvatar(Texture2D source, ulong steamId)
        {
            if (source == null || source.width <= 0 || source.height <= 0)
                return null;

            Color32[] pixels = source.GetPixels32();
            float centerX = (source.width - 1) * 0.5f;
            float centerY = (source.height - 1) * 0.5f;
            float radius = Mathf.Min(source.width, source.height) * 0.49f;
            float edgeFeather = Mathf.Max(1f, Mathf.Min(source.width, source.height) * 0.025f);

            for (int y = 0; y < source.height; y++)
            {
                for (int x = 0; x < source.width; x++)
                {
                    int index = y * source.width + x;
                    float distance = Vector2.Distance(
                        new Vector2(x, y),
                        new Vector2(centerX, centerY));
                    float circleAlpha = Mathf.Clamp01((radius - distance) / edgeFeather);
                    Color32 pixel = pixels[index];
                    pixel.a = (byte)(pixel.a * circleAlpha);
                    pixels[index] = pixel;
                }
            }

            Texture2D circular = new Texture2D(
                source.width,
                source.height,
                TextureFormat.RGBA32,
                false);
            circular.name = $"MapAvatar_{steamId}";
            circular.filterMode = FilterMode.Bilinear;
            circular.wrapMode = TextureWrapMode.Clamp;
            circular.SetPixels32(pixels);
            circular.Apply(false, false);
            return circular;
        }

        private static Texture2D GetFallbackCircleTexture()
        {
            if (fallbackCircleTexture != null)
                return fallbackCircleTexture;

            const int size = 32;
            Color32[] pixels = new Color32[size * size];
            float center = (size - 1) * 0.5f;
            float radius = size * 0.47f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = Vector2.Distance(
                        new Vector2(x, y),
                        new Vector2(center, center));
                    byte alpha = (byte)(Mathf.Clamp01(radius - distance) * 255f);
                    pixels[y * size + x] = new Color32(255, 255, 255, alpha);
                }
            }

            fallbackCircleTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            fallbackCircleTexture.name = "MapAvatarFallbackCircle";
            fallbackCircleTexture.filterMode = FilterMode.Bilinear;
            fallbackCircleTexture.wrapMode = TextureWrapMode.Clamp;
            fallbackCircleTexture.SetPixels32(pixels);
            fallbackCircleTexture.Apply(false, false);
            return fallbackCircleTexture;
        }

        private static Texture2D GetAvatarFrameTexture()
        {
            if (avatarFrameTexture != null)
                return avatarFrameTexture;

            try
            {
                using (Stream stream = typeof(MapGUIPatches).Assembly
                    .GetManifestResourceStream(AvatarFrameResourceName))
                {
                    if (stream == null)
                    {
                        CoopMod.Logger.LogWarning(
                            $"Map avatar frame resource not found: {AvatarFrameResourceName}");
                        return null;
                    }

                    byte[] imageData = new byte[stream.Length];
                    int offset = 0;
                    while (offset < imageData.Length)
                    {
                        int read = stream.Read(imageData, offset, imageData.Length - offset);
                        if (read <= 0)
                            break;
                        offset += read;
                    }

                    Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!texture.LoadImage(imageData))
                    {
                        Object.Destroy(texture);
                        CoopMod.Logger.LogWarning("Could not decode the embedded map avatar frame");
                        return null;
                    }

                    RemoveBakedCheckerboard(texture);
                    texture.name = "MapAvatarFrame";
                    texture.filterMode = FilterMode.Bilinear;
                    texture.wrapMode = TextureWrapMode.Clamp;
                    avatarFrameTexture = texture;
                    return avatarFrameTexture;
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"Could not load map avatar frame: {ex.Message}");
                return null;
            }
        }

        private static void RemoveBakedCheckerboard(Texture2D texture)
        {
            Color32[] pixels = texture.GetPixels32();
            bool hasTransparency = false;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].a < 250)
                {
                    hasTransparency = true;
                    break;
                }
            }

            if (hasTransparency)
                return;

            int removedPixels = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 pixel = pixels[i];
                byte min = pixel.r < pixel.g ? pixel.r : pixel.g;
                if (pixel.b < min) min = pixel.b;
                byte max = pixel.r > pixel.g ? pixel.r : pixel.g;
                if (pixel.b > max) max = pixel.b;

                if (min >= 210 && max - min <= 28)
                {
                    pixel.a = 0;
                    pixels[i] = pixel;
                    removedPixels++;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            CoopMod.Logger.LogInfo(
                $"Removed baked checkerboard from map avatar frame ({removedPixels} pixels)");
        }

        /// <summary>
        /// Add or update a remote player indicator
        /// </summary>
        public static void UpdateRemotePlayerIndicator(string playerId, string playerName, Vector3 worldPosition, Color indicatorColor)
        {
            if (string.IsNullOrEmpty(playerId))
                return;

            if (!remoteIndicatorStates.TryGetValue(playerId, out RemoteIndicatorState state))
            {
                state = new RemoteIndicatorState();
                remoteIndicatorStates[playerId] = state;
            }

            state.PlayerName = playerName;
            state.WorldPosition = worldPosition;
            state.Color = indicatorColor;

            // MapGUI.Update owns rendering while the map is open. Keeping this
            // method state-only prevents OnlineCoopManager and MapGUI from applying
            // the same marker twice in one frame.
        }

        private static void ApplyRemotePlayerIndicator(
            string playerId,
            string playerName,
            Vector3 worldPosition,
            Color indicatorColor)
        {
            if (mapTransform == null || cachedMapGUI == null ||
                !cachedMapGUI.gameObject.activeInHierarchy)
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
                playerIndicators[playerId] = CreatePlayerIndicator(
                    parent,
                    playerName,
                    indicatorColor,
                    remoteSteamId);
            }

            GameObject indicator = playerIndicators[playerId];
            
            // Null check in case creation failed
            if (indicator == null)
            {
                return;
            }

            RefreshIndicatorAvatar(indicator);

            // Convert world position to map position using helper
            Vector2 mapPos = MapCoordinateHelper.PlayerWorldToMap(worldPosition);
            Vector3 nextMapPosition = new Vector3(mapPos.x, mapPos.y, -10);
            if (!indicatorVisualStates.TryGetValue(indicator, out IndicatorVisualState visualState) ||
                !visualState.HasMapPosition ||
                visualState.LastMapPosition != nextMapPosition)
            {
                indicator.transform.localPosition = nextMapPosition;

                // NGUI can retain previous geometry while the game is paused. Cache
                // the child widgets at creation and dirty them only when the marker moves.
                UIWidget[] widgets = visualState?.Widgets;
                if (widgets != null)
                {
                    for (int i = 0; i < widgets.Length; i++)
                    {
                        if (widgets[i] != null)
                            widgets[i].MarkAsChanged();
                    }
                }

                if (visualState != null)
                {
                    visualState.LastMapPosition = nextMapPosition;
                    visualState.HasMapPosition = true;
                }
            }
            
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
            remoteIndicatorStates.Remove(playerId);

            if (playerIndicators.ContainsKey(playerId))
            {
                if (playerIndicators[playerId] != null)
                {
                    DestroyIndicator(playerIndicators[playerId]);
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
                DestroyIndicator(localPlayerIndicator);
                localPlayerIndicator = null;
            }

            if (player2Indicator != null)
            {
                DestroyIndicator(player2Indicator);
                player2Indicator = null;
            }

            foreach (var indicator in playerIndicators.Values)
            {
                if (indicator != null)
                {
                    DestroyIndicator(indicator);
                }
            }
            playerIndicators.Clear();
            remoteIndicatorStates.Clear();
            mapContentsTransform = null;

            foreach (Texture2D avatarTexture in circularAvatarTextures.Values)
            {
                if (avatarTexture != null)
                    Object.Destroy(avatarTexture);
            }
            circularAvatarTextures.Clear();

            if (avatarFrameTexture != null)
            {
                Object.Destroy(avatarFrameTexture);
                avatarFrameTexture = null;
            }

            if (fallbackCircleTexture != null)
            {
                Object.Destroy(fallbackCircleTexture);
                fallbackCircleTexture = null;
            }

            indicatorVisualStates.Clear();
        }

        private static void DestroyIndicator(GameObject indicator)
        {
            if (indicator == null)
                return;

            indicatorVisualStates.Remove(indicator);
            Object.Destroy(indicator);
        }
    }
}

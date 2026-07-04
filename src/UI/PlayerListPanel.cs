using System;
using System.Collections.Generic;
using UnityEngine;
using Steamworks;
using GraveyardKeeperCoop.Multiplayer;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// Manages the player list display in the lobby using Steam profile pictures
    /// Shows a golden outline around players who are ready
    /// </summary>
    public class PlayerListPanel : MonoBehaviour
    {
        private List<PlayerAvatarWidget> playerAvatars = new List<PlayerAvatarWidget>();
        private const int AVATAR_SIZE = 32; // Size of each avatar image (fits in ~35px tall frame)
        private const int AVATAR_SPACING = 2; // Small space between avatars for clarity
        private const int OUTLINE_SIZE = 38; // Size of outline frame (slightly larger than avatar)
        private const int KICK_BUTTON_SIZE = 16;
        private const int KICK_BUTTON_OFFSET = 12;

        private UIFont kickButtonFont;
        private Action<CSteamID> onKickRequested;
        private bool kickButtonsEnabled;
        
        // Golden color for ready outline (matches game's selection frame)
        private static readonly Color READY_OUTLINE_COLOR = new Color(0.94f, 0.64f, 0.24f, 1f); // #F0A33E
        
        private class PlayerAvatarWidget
        {
            public GameObject gameObject;
            public UITexture texture;
            public Texture2D avatarTexture;
            public CSteamID steamID;
            public UI2DSprite readyOutline; // Golden outline shown when player is ready
            public GameObject kickButton;
            public bool isReady;
        }
        
        public void Initialize(Transform parent, Vector3 localPosition, UIFont font, Color? textColor = null, Action<CSteamID> kickRequested = null)
        {
            // Set parent and position
            transform.SetParent(parent, false);
            gameObject.name = "PlayerListPanel";
            gameObject.layer = 13;
            transform.localPosition = localPosition;
            kickButtonFont = font;
            onKickRequested = kickRequested;
            
            CoopMod.Logger.LogInfo($"PlayerListPanel initialized at: {localPosition}");
        }

        public void SetKickButtonsEnabled(bool enabled)
        {
            kickButtonsEnabled = enabled;
            RefreshKickButtons();
        }
        
        public void AddPlayer(string playerName)
        {
            // For local co-op, we use the local Steam ID for both players
            CSteamID steamID = SteamUser.GetSteamID();
            
            AddPlayerAvatar(steamID);
            CoopMod.Logger.LogInfo($"Player avatar added for: {playerName}");
        }
        
        /// <summary>
        /// Add a player with their actual Steam ID (for online multiplayer)
        /// </summary>
        public void AddPlayer(string playerName, CSteamID steamID)
        {
            AddPlayerAvatar(steamID);
            CoopMod.Logger.LogInfo($"Player avatar added for: {playerName} (Steam ID: {steamID})");
        }
        
        public void RemovePlayer(string playerName)
        {
            // This method is for backwards compatibility - prefer RemovePlayerBySteamID
            if (playerAvatars.Count > 0)
            {
                // Remove the last avatar
                var lastAvatar = playerAvatars[playerAvatars.Count - 1];
                DestroyPlayerAvatar(lastAvatar);
                playerAvatars.RemoveAt(playerAvatars.Count - 1);
                
                // Reposition remaining avatars
                RepositionAvatars();
                CoopMod.Logger.LogInfo($"Player avatar removed for: {playerName}");
            }
        }
        
        public void RemovePlayerBySteamID(CSteamID steamID)
        {
            for (int i = 0; i < playerAvatars.Count; i++)
            {
                if (playerAvatars[i].steamID == steamID)
                {
                    DestroyPlayerAvatar(playerAvatars[i]);
                    playerAvatars.RemoveAt(i);
                    
                    // Reposition remaining avatars
                    RepositionAvatars();
                    CoopMod.Logger.LogInfo($"Player avatar removed for Steam ID: {steamID}");
                    return;
                }
            }
            CoopMod.Logger.LogWarning($"No avatar found for Steam ID: {steamID}");
        }
        
        private void DestroyPlayerAvatar(PlayerAvatarWidget avatar)
        {
            if (avatar.readyOutline != null)
            {
                Destroy(avatar.readyOutline.gameObject);
            }
            if (avatar.kickButton != null)
            {
                Destroy(avatar.kickButton);
            }
            if (avatar.gameObject != null)
            {
                Destroy(avatar.gameObject);
            }
            if (avatar.avatarTexture != null)
            {
                Destroy(avatar.avatarTexture);
            }
        }
        
        public void ClearPlayers()
        {
            foreach (var avatar in playerAvatars)
            {
                DestroyPlayerAvatar(avatar);
            }
            playerAvatars.Clear();
            CoopMod.Logger.LogInfo("Player avatars cleared");
        }
        
        public int GetPlayerCount()
        {
            return playerAvatars.Count;
        }
        
        private void AddPlayerAvatar(CSteamID steamID)
        {
            // Get Steam avatar
            int avatarHandle = SteamFriends.GetMediumFriendAvatar(steamID);
            
            if (avatarHandle == -1)
            {
                CoopMod.Logger.LogWarning($"Avatar not available yet for {steamID}, will try again");
                // Avatar is still loading, we'll need to wait and try again
                StartCoroutine(WaitForAvatar(steamID));
                return;
            }
            
            if (avatarHandle == 0)
            {
                CoopMod.Logger.LogWarning($"No avatar available for {steamID}");
                return;
            }
            
            // Get avatar image dimensions
            uint width, height;
            if (!SteamUtils.GetImageSize(avatarHandle, out width, out height))
            {
                CoopMod.Logger.LogError($"Failed to get avatar size for {steamID}");
                return;
            }
            
            // Get avatar image data
            byte[] imageData = new byte[width * height * 4];
            if (!SteamUtils.GetImageRGBA(avatarHandle, imageData, (int)(width * height * 4)))
            {
                CoopMod.Logger.LogError($"Failed to get avatar image data for {steamID}");
                return;
            }
            
            // Create texture
            Texture2D avatarTexture = new Texture2D((int)width, (int)height, TextureFormat.RGBA32, false);
            avatarTexture.LoadRawTextureData(imageData);
            avatarTexture.Apply();
            
            // Flip the texture vertically (Steam provides bottom-to-top, Unity expects top-to-bottom)
            FlipTextureVertically(avatarTexture);
            
            // Create GameObject for this avatar
            GameObject avatarObj = new GameObject($"PlayerAvatar_{playerAvatars.Count}");
            avatarObj.layer = 13; // NGUI layer
            avatarObj.transform.SetParent(transform, false);
            
            // Add UITexture component first
            UITexture uiTexture = avatarObj.AddComponent<UITexture>();
            uiTexture.mainTexture = avatarTexture;
            uiTexture.width = AVATAR_SIZE;
            uiTexture.height = AVATAR_SIZE;
            uiTexture.depth = 106;
            
            // Create ready outline as a SIBLING (not child) and IN FRONT of the avatar
            UI2DSprite readyOutline = CreateReadyOutline(transform, avatarObj.transform);
            
            // Store the avatar widget
            PlayerAvatarWidget widget = new PlayerAvatarWidget
            {
                gameObject = avatarObj,
                texture = uiTexture,
                avatarTexture = avatarTexture,
                steamID = steamID,
                readyOutline = readyOutline,
                kickButton = null,
                isReady = false
            };
            widget.kickButton = CreateKickButton(widget);
            playerAvatars.Add(widget);
            
            // Check if this player is already marked as ready
            if (LobbyReadySystem.IsPlayerReady(steamID))
            {
                SetPlayerReady(steamID, true);
            }
            
            // Position all avatars
            RepositionAvatars();
            
            CoopMod.Logger.LogInfo($"Avatar created for {steamID}: {width}x{height}");
        }

        private GameObject CreateKickButton(PlayerAvatarWidget avatar)
        {
            if (avatar == null || avatar.gameObject == null || kickButtonFont == null)
                return null;

            GameObject kickObj = new GameObject("KickPlayerButton");
            kickObj.layer = 13;
            kickObj.transform.SetParent(avatar.gameObject.transform, false);
            kickObj.transform.localPosition = new Vector3(KICK_BUTTON_OFFSET, KICK_BUTTON_OFFSET, 0);
            kickObj.transform.localScale = Vector3.one;

            UILabel label = kickObj.AddComponent<UILabel>();
            label.bitmapFont = kickButtonFont;
            label.text = "X";
            label.fontSize = 14;
            label.width = KICK_BUTTON_SIZE;
            label.height = KICK_BUTTON_SIZE;
            label.depth = 125;
            label.color = new Color(1f, 0.12f, 0.08f, 1f);
            label.alignment = NGUIText.Alignment.Center;
            label.pivot = UIWidget.Pivot.Center;
            label.effectStyle = UILabel.Effect.Outline;
            label.effectColor = Color.black;
            label.supportEncoding = false;

            var clickHandler = kickObj.AddComponent<PlayerAvatarKickClickHandler>();
            clickHandler.Initialize(avatar.steamID, label, RequestKick);

            kickObj.SetActive(ShouldShowKickButton(avatar));
            return kickObj;
        }

        private void RefreshKickButtons()
        {
            foreach (var avatar in playerAvatars)
            {
                if (avatar.kickButton != null)
                {
                    avatar.kickButton.SetActive(ShouldShowKickButton(avatar));
                }
            }
        }

        private bool ShouldShowKickButton(PlayerAvatarWidget avatar)
        {
            if (!kickButtonsEnabled || onKickRequested == null || avatar == null)
                return false;

            CSteamID localID = SteamUser.GetSteamID();
            return avatar.steamID != CSteamID.Nil && avatar.steamID != localID;
        }

        private void RequestKick(CSteamID steamID)
        {
            if (!kickButtonsEnabled || onKickRequested == null)
                return;

            if (steamID == CSteamID.Nil || steamID == SteamUser.GetSteamID())
                return;

            onKickRequested(steamID);
        }
        
        /// <summary>
        /// Create a golden outline sprite for showing ready state
        /// Uses the same frame as save slot selection
        /// </summary>
        private UI2DSprite CreateReadyOutline(Transform panelParent, Transform avatarTransform)
        {
            GameObject outlineObj = new GameObject("ReadyOutline");
            outlineObj.layer = 13;
            // Make it a sibling at the panel level, not a child of the avatar
            outlineObj.transform.SetParent(panelParent, false);
            // Position will be synced with avatar in RepositionAvatars
            outlineObj.transform.localPosition = avatarTransform.localPosition;
            
            UI2DSprite outline = outlineObj.AddComponent<UI2DSprite>();
            outline.width = OUTLINE_SIZE;
            outline.height = OUTLINE_SIZE;
            outline.depth = 110; // IN FRONT of the avatar (106) to ensure visibility
            outline.color = READY_OUTLINE_COLOR;
            
            // Try to find the gamepad_frame from SaveSlotGUI (same as save slot selection)
            var saveSlotGUIs = Resources.FindObjectsOfTypeAll<SaveSlotGUI>();
            foreach (var saveSlot in saveSlotGUIs)
            {
                if (saveSlot.gamepad_frame != null)
                {
                    // The gamepad_frame is a UIWidget, but at runtime it's often a UI2DSprite
                    var frameSprite = saveSlot.gamepad_frame as UI2DSprite;
                    if (frameSprite != null && frameSprite.sprite2D != null)
                    {
                        outline.sprite2D = frameSprite.sprite2D;
                        CoopMod.Logger.LogInfo($"Found gamepad_frame sprite from SaveSlotGUI: {frameSprite.sprite2D.name}");
                        break;
                    }
                    
                    // Try getting UI2DSprite component from the gamepad_frame gameObject
                    var childSprite = saveSlot.gamepad_frame.GetComponent<UI2DSprite>();
                    if (childSprite != null && childSprite.sprite2D != null)
                    {
                        outline.sprite2D = childSprite.sprite2D;
                        CoopMod.Logger.LogInfo($"Found gamepad_frame sprite from component: {childSprite.sprite2D.name}");
                        break;
                    }
                    
                    // Check children
                    var childSprites = saveSlot.gamepad_frame.GetComponentsInChildren<UI2DSprite>(true);
                    foreach (var cs in childSprites)
                    {
                        if (cs.sprite2D != null)
                        {
                            outline.sprite2D = cs.sprite2D;
                            CoopMod.Logger.LogInfo($"Found gamepad_frame sprite from child: {cs.sprite2D.name}");
                            break;
                        }
                    }
                    
                    if (outline.sprite2D != null) break;
                }
            }
            
            // Fallback: search all UI2DSprites for a frame
            if (outline.sprite2D == null)
            {
                var existingSprites = Resources.FindObjectsOfTypeAll<UI2DSprite>();
                foreach (var sprite in existingSprites)
                {
                    if (sprite.sprite2D != null && sprite.name.ToLower().Contains("gamepad") && sprite.name.ToLower().Contains("frame"))
                    {
                        outline.sprite2D = sprite.sprite2D;
                        CoopMod.Logger.LogInfo($"Found frame sprite by name: {sprite.name}, sprite: {sprite.sprite2D.name}");
                        break;
                    }
                }
            }
            
            if (outline.sprite2D == null)
            {
                CoopMod.Logger.LogWarning("No gamepad_frame sprite found for ready outline");
            }
            
            // Start hidden
            outlineObj.SetActive(false);
            
            return outline;
        }
        
        /// <summary>
        /// Set a player's ready state and update their visual
        /// </summary>
        public void SetPlayerReady(CSteamID steamID, bool isReady)
        {
            CoopMod.Logger.LogInfo($"[SetPlayerReady] Looking for avatar with steamID: {steamID}, isReady: {isReady}");
            CoopMod.Logger.LogInfo($"[SetPlayerReady] Total avatars in list: {playerAvatars.Count}");
            
            foreach (var avatar in playerAvatars)
            {
                CoopMod.Logger.LogInfo($"[SetPlayerReady] Checking avatar steamID: {avatar.steamID}");
                if (avatar.steamID == steamID)
                {
                    avatar.isReady = isReady;
                    if (avatar.readyOutline != null)
                    {
                        CoopMod.Logger.LogInfo($"[SetPlayerReady] Found avatar! readyOutline exists, sprite2D: {(avatar.readyOutline.sprite2D != null ? avatar.readyOutline.sprite2D.name : "NULL")}");
                        CoopMod.Logger.LogInfo($"[SetPlayerReady] Outline size: {avatar.readyOutline.width}x{avatar.readyOutline.height}, depth: {avatar.readyOutline.depth}");
                        avatar.readyOutline.gameObject.SetActive(isReady);
                        CoopMod.Logger.LogInfo($"Player {steamID} ready outline: {isReady}");
                    }
                    else
                    {
                        CoopMod.Logger.LogWarning($"[SetPlayerReady] Avatar found but readyOutline is NULL!");
                    }
                    return;
                }
            }
            
            CoopMod.Logger.LogWarning($"[SetPlayerReady] Avatar not found for steamID: {steamID}");
        }
        
        private void RepositionAvatars()
        {
            // Position avatars horizontally, centered
            int totalWidth = playerAvatars.Count * AVATAR_SIZE + (playerAvatars.Count - 1) * AVATAR_SPACING;
            float startX = -totalWidth / 2f + AVATAR_SIZE / 2f;
            
            for (int i = 0; i < playerAvatars.Count; i++)
            {
                float x = startX + i * (AVATAR_SIZE + AVATAR_SPACING);
                playerAvatars[i].gameObject.transform.localPosition = new Vector3(x, 0, 0);
                
                // Also position the outline (it's a sibling now)
                if (playerAvatars[i].readyOutline != null)
                {
                    playerAvatars[i].readyOutline.transform.localPosition = new Vector3(x, 0, 0);
                }
            }

            RefreshKickButtons();
        }
        
        private System.Collections.IEnumerator WaitForAvatar(CSteamID steamID)
        {
            // Wait up to 5 seconds for the avatar to load
            float timeout = 5f;
            float elapsed = 0f;
            
            while (elapsed < timeout)
            {
                yield return new WaitForSeconds(0.5f);
                elapsed += 0.5f;
                
                int avatarHandle = SteamFriends.GetMediumFriendAvatar(steamID);
                if (avatarHandle > 0)
                {
                    AddPlayerAvatar(steamID);
                    yield break;
                }
            }
            
            CoopMod.Logger.LogWarning($"Timeout waiting for avatar for {steamID}");
        }
        
        /// <summary>
        /// Flips a texture vertically. This is needed because Steam provides image data
        /// in bottom-to-top format, while Unity expects top-to-bottom format.
        /// </summary>
        private void FlipTextureVertically(Texture2D texture)
        {
            int width = texture.width;
            int height = texture.height;
            Color[] pixels = texture.GetPixels();
            Color[] flippedPixels = new Color[pixels.Length];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    flippedPixels[x + y * width] = pixels[x + (height - 1 - y) * width];
                }
            }

            texture.SetPixels(flippedPixels);
            texture.Apply();
        }
    }

    public class PlayerAvatarKickClickHandler : MonoBehaviour
    {
        private CSteamID steamID;
        private Action<CSteamID> onClicked;
        private UIWidget hitWidget;
        private Camera uiCamera;

        public void Initialize(CSteamID playerSteamID, UIWidget widget, Action<CSteamID> clickCallback)
        {
            steamID = playerSteamID;
            hitWidget = widget;
            onClicked = clickCallback;
            uiCamera = NGUITools.FindCameraForLayer(gameObject.layer);
        }

        private void Update()
        {
            if (!Input.GetMouseButtonDown(0) || uiCamera == null || hitWidget == null)
                return;

            Vector3 mousePos = Input.mousePosition;
            Vector3[] corners = hitWidget.worldCorners;
            Vector2 min = uiCamera.WorldToScreenPoint(corners[0]);
            Vector2 max = uiCamera.WorldToScreenPoint(corners[2]);

            float minX = Mathf.Min(min.x, max.x);
            float maxX = Mathf.Max(min.x, max.x);
            float minY = Mathf.Min(min.y, max.y);
            float maxY = Mathf.Max(min.y, max.y);

            if (mousePos.x >= minX && mousePos.x <= maxX &&
                mousePos.y >= minY && mousePos.y <= maxY)
            {
                onClicked?.Invoke(steamID);
            }
        }
    }
}

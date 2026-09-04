using BepInEx.Logging;
using Steamworks;

namespace GraveyardKeeperCoopMod.Utils
{
    public static class SteamHelper
    {
        private static ManualLogSource Logger => GraveyardKeeperCoop.CoopMod.Logger;

        /// <summary>
        /// Gets the local player's Steam display name.
        /// Falls back to "Player" if Steam is not available.
        /// </summary>
        public static string GetLocalPlayerName()
        {
            try
            {
                // Check if Steam is initialized (game uses SteamManager)
                if (SteamManager.Initialized)
                {
                    string steamName = SteamFriends.GetPersonaName();
                    
                    if (!string.IsNullOrEmpty(steamName))
                    {
                        Logger.LogInfo($"Steam username retrieved: {steamName}");
                        return steamName;
                    }
                    else
                    {
                        Logger.LogWarning("Steam initialized but GetPersonaName returned empty");
                    }
                }
                else
                {
                    Logger.LogWarning("SteamManager not initialized, using fallback name");
                }
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Error getting Steam username: {ex.Message}");
            }

            // Fallback to generic name
            return "Player";
        }

        /// <summary>
        /// Gets the local player's Steam ID.
        /// Returns CSteamID.Nil if Steam is not available.
        /// </summary>
        public static CSteamID GetLocalSteamID()
        {
            try
            {
                if (SteamManager.Initialized)
                {
                    CSteamID steamID = SteamUser.GetSteamID();
                    Logger.LogInfo($"Steam ID retrieved: {steamID.m_SteamID}");
                    return steamID;
                }
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Error getting Steam ID: {ex.Message}");
            }

            return CSteamID.Nil;
        }

        /// <summary>
        /// Gets a friend's Steam display name by their Steam ID.
        /// Falls back to "Unknown Player" if name cannot be retrieved.
        /// </summary>
        public static string GetFriendName(CSteamID steamID)
        {
            try
            {
                if (SteamManager.Initialized && steamID != CSteamID.Nil)
                {
                    string friendName = SteamFriends.GetFriendPersonaName(steamID);
                    
                    if (!string.IsNullOrEmpty(friendName))
                    {
                        return friendName;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Error getting friend name for {steamID.m_SteamID}: {ex.Message}");
            }

            return "Unknown Player";
        }

        /// <summary>
        /// Checks if Steam is properly initialized.
        /// </summary>
        public static bool IsSteamAvailable()
        {
            return SteamManager.Initialized;
        }

        /// <summary>
        /// Flips a texture vertically. This is needed because Steam provides image data
        /// in bottom-to-top format, while Unity expects top-to-bottom format.
        /// </summary>
        private static void FlipTextureVertically(UnityEngine.Texture2D texture)
        {
            int width = texture.width;
            int height = texture.height;
            UnityEngine.Color[] pixels = texture.GetPixels();
            UnityEngine.Color[] flippedPixels = new UnityEngine.Color[pixels.Length];

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

        /// <summary>
        /// Gets the Steam avatar texture for the local player.
        /// Returns null if avatar cannot be retrieved.
        /// </summary>
        public static UnityEngine.Texture2D GetLocalPlayerAvatar()
        {
            if (!SteamManager.Initialized)
            {
                Logger.LogWarning("Steam not initialized");
                return null;
            }
            return GetAvatarForSteamID(SteamUser.GetSteamID());
        }

        /// <summary>
        /// Gets the Steam avatar texture for a specific Steam user (local or friend).
        /// Returns null if avatar cannot be retrieved (e.g. not yet cached by Steam).
        /// </summary>
        public static UnityEngine.Texture2D GetAvatarForSteamID(CSteamID steamId)
        {
            try
            {
                if (!SteamManager.Initialized)
                {
                    Logger.LogWarning("Steam not initialized");
                    return null;
                }

                if (steamId == CSteamID.Nil)
                {
                    Logger.LogWarning("GetAvatarForSteamID called with CSteamID.Nil");
                    return null;
                }

                Logger.LogInfo($"Getting avatar for Steam ID: {steamId.m_SteamID}");

                // Ask Steam to start downloading friend info (incl. avatar) if we don't have it yet.
                // This is a no-op for the local user / for friends whose info is already cached.
                SteamFriends.RequestUserInformation(steamId, false);

                // Get the medium avatar (64x64)
                int avatarInt = SteamFriends.GetMediumFriendAvatar(steamId);
                
                if (avatarInt == -1)
                {
                    Logger.LogWarning("Avatar not loaded yet (returned -1)");
                    return null;
                }
                
                if (avatarInt == 0)
                {
                    Logger.LogWarning("No avatar available (returned 0)");
                    return null;
                }
                
                Logger.LogInfo($"Avatar handle: {avatarInt}");
                
                // Get avatar size
                uint width, height;
                bool success = SteamUtils.GetImageSize(avatarInt, out width, out height);
                
                if (!success)
                {
                    Logger.LogWarning("Failed to get image size");
                    return null;
                }
                
                Logger.LogInfo($"Avatar size: {width}x{height}");
                
                // Get the image data
                byte[] imageData = new byte[width * height * 4]; // RGBA
                success = SteamUtils.GetImageRGBA(avatarInt, imageData, (int)(width * height * 4));
                
                if (!success)
                {
                    Logger.LogWarning("Failed to get image RGBA data");
                    return null;
                }
                
                // Create texture
                UnityEngine.Texture2D texture = new UnityEngine.Texture2D((int)width, (int)height, UnityEngine.TextureFormat.RGBA32, false);
                texture.LoadRawTextureData(imageData);
                texture.Apply();
                
                // Flip the texture vertically (Steam provides bottom-to-top, Unity expects top-to-bottom)
                FlipTextureVertically(texture);
                
                Logger.LogInfo("Steam avatar texture created successfully!");
                return texture;
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Error getting Steam avatar: {ex.Message}");
                return null;
            }
        }
    }
}

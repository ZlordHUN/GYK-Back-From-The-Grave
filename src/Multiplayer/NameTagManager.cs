using UnityEngine;
using Steamworks;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Manages player name tags - creates and attaches floating name labels above players.
    /// </summary>
    public static class NameTagManager
    {
        /// <summary>
        /// Create a name tag for a remote player
        /// </summary>
        /// <param name="playerObject">The player GameObject to attach the name tag to</param>
        /// <param name="playerName">The display name for the player</param>
        /// <returns>The created name tag GameObject</returns>
        public static GameObject CreateNameTag(GameObject playerObject, string playerName)
        {
            if (playerObject == null)
            {
                CoopMod.Logger.LogWarning("[NameTagManager] Cannot create name tag - player object is null");
                return null;
            }
            
            // Create a head anchor point on the player
            var headAnchor = new GameObject("HeadAnchor");
            headAnchor.transform.SetParent(playerObject.transform, false);
            headAnchor.transform.localPosition = new Vector3(0f, 0.85f, 0f); // Above player's head
            
            // Get the HUD to parent the name tag to
            if (GUIElements.me?.hud == null)
            {
                CoopMod.Logger.LogWarning("[NameTagManager] Cannot create name tag - HUD not found");
                return null;
            }
            
            var hudGO = GUIElements.me.hud.gameObject;
            
            // Create name tag as child of HUD
            var nameTagObj = NGUITools.AddChild(hudGO);
            nameTagObj.name = $"NameTag_{playerName}";
            nameTagObj.layer = hudGO.layer;
            
            // Add and configure the name tag component
            var nameTag = nameTagObj.AddComponent<UI.PlayerNameTag>();
            nameTag.Target = headAnchor.transform;
            nameTag.WorldOffset = new Vector3(0f, 0.4f, 0f); // Additional offset from head anchor
            nameTag.PixelOffset = new Vector2(0f, 0f);
            nameTag.Initialize(playerName);
            
            CoopMod.Logger.LogInfo($"[NameTagManager] Created name tag for: {playerName}");
            
            return nameTagObj;
        }
        
        /// <summary>
        /// Create a name tag for a remote player using their Steam ID to get display name
        /// </summary>
        public static GameObject CreateNameTagFromSteamID(GameObject playerObject, CSteamID steamID)
        {
            string displayName = GetSteamDisplayName(steamID);
            return CreateNameTag(playerObject, displayName);
        }
        
        /// <summary>
        /// Get a player's display name from their Steam ID
        /// </summary>
        public static string GetSteamDisplayName(CSteamID steamID)
        {
            if (steamID.IsValid())
            {
                string name = SteamFriends.GetFriendPersonaName(steamID);
                if (!string.IsNullOrEmpty(name) && name != "[unknown]")
                {
                    return name;
                }
            }
            return $"Player_{steamID.m_SteamID % 10000}";
        }
        
        /// <summary>
        /// Destroy a name tag
        /// </summary>
        public static void DestroyNameTag(GameObject nameTagObj)
        {
            if (nameTagObj != null)
            {
                Object.Destroy(nameTagObj);
            }
        }
        
        /// <summary>
        /// Update a name tag's displayed name
        /// </summary>
        public static void UpdateNameTag(GameObject nameTagObj, string newName)
        {
            if (nameTagObj == null)
                return;
            
            var nameTag = nameTagObj.GetComponent<UI.PlayerNameTag>();
            if (nameTag != null)
            {
                nameTag.SetName(newName);
            }
        }
        
        /// <summary>
        /// Set a name tag's color
        /// </summary>
        public static void SetNameTagColor(GameObject nameTagObj, Color color)
        {
            if (nameTagObj == null)
                return;
            
            var nameTag = nameTagObj.GetComponent<UI.PlayerNameTag>();
            if (nameTag != null)
            {
                nameTag.SetColor(color);
            }
        }
    }
}

using UnityEngine;
using Steamworks;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Manages player name tags - creates and attaches floating name labels above players.
    /// </summary>
    public static class NameTagManager
    {
        private const string NameTagRootName = "OnlinePlayerNameTags";
        internal static readonly Vector3 DefaultWorldOffset =
            new Vector3(0f, 12f, 0f);
        private static GameObject nameTagRoot;
        private static int defaultPanelDepth;

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

            // Get the HUD to parent the name tag to
            if (GUIElements.me?.hud == null)
            {
                CoopMod.Logger.LogWarning("[NameTagManager] Cannot create name tag - HUD not found");
                return null;
            }

            WorldGameObject playerWgo =
                playerObject.GetComponent<WorldGameObject>();
            Transform target = playerWgo?.bubble_pos_tf;
            if (target == null)
            {
                // Fallback for player prefabs without a bubble anchor. Game
                // world coordinates use roughly pixel-sized units.
                var headAnchor = new GameObject("HeadAnchor");
                headAnchor.transform.SetParent(playerObject.transform, false);
                headAnchor.transform.localPosition =
                    new Vector3(0f, 72f, 0f);
                target = headAnchor.transform;
            }
            
            var hudGO = GUIElements.me.hud.gameObject;
            GameObject parent = GetOrCreateNameTagRoot(hudGO);
            if (parent == null)
            {
                CoopMod.Logger.LogWarning(
                    "[NameTagManager] Cannot create name tag - UI root not found");
                return null;
            }
            
            // The game deactivates HUD during cinematics. Keep player identity
            // labels on their own UIRoot panel so cutscenes do not hide them.
            var nameTagObj = NGUITools.AddChild(parent);
            nameTagObj.name = $"NameTag_{playerName}";
            nameTagObj.layer = hudGO.layer;
            
            // Add and configure the name tag component
            var nameTag = nameTagObj.AddComponent<UI.PlayerNameTag>();
            nameTag.Target = target;
            nameTag.WorldOffset = DefaultWorldOffset;
            nameTag.PixelOffset = new Vector2(0f, 0f);
            nameTag.Initialize(playerName);
            
            CoopMod.Logger.LogInfo($"[NameTagManager] Created name tag for: {playerName}");
            
            return nameTagObj;
        }

        private static GameObject GetOrCreateNameTagRoot(GameObject hudGO)
        {
            UIRoot uiRoot = MainGame.me?.ui_root ??
                (hudGO != null ? hudGO.GetComponentInParent<UIRoot>() : null);
            if (uiRoot == null)
                return null;

            if (nameTagRoot != null &&
                nameTagRoot.transform.parent == uiRoot.transform)
            {
                if (!nameTagRoot.activeSelf)
                    nameTagRoot.SetActive(true);
                return nameTagRoot;
            }

            nameTagRoot = NGUITools.AddChild(uiRoot.gameObject);
            nameTagRoot.name = NameTagRootName;
            nameTagRoot.layer = hudGO != null
                ? hudGO.layer
                : LayerMask.NameToLayer("UI");

            UIPanel panel = nameTagRoot.AddComponent<UIPanel>();
            UIPanel hudPanel = hudGO?.GetComponent<UIPanel>() ??
                hudGO?.GetComponentInParent<UIPanel>();
            panel.depth = (hudPanel?.depth ?? 0) + 1;
            defaultPanelDepth = panel.depth;
            return nameTagRoot;
        }

        /// <summary>
        /// NGUI sorts panels before widget depths. Temporarily place the
        /// independent name-tag panel below a speech bubble's panel so the
        /// complete bubble, including its background, covers nearby tags.
        /// </summary>
        public static void EnsureBelowSpeechBubble(SpeechBubbleGUI bubble)
        {
            if (bubble == null || nameTagRoot == null)
                return;

            UIPanel namePanel = nameTagRoot.GetComponent<UIPanel>();
            UIPanel bubblePanel = bubble.GetComponentInParent<UIPanel>();
            if (namePanel == null || bubblePanel == null ||
                namePanel == bubblePanel)
            {
                return;
            }

            int desiredDepth = bubblePanel.depth - 1;
            if (namePanel.depth > desiredDepth)
                namePanel.depth = desiredDepth;
        }

        public static void RestoreDefaultRenderDepthIfNoSpeechBubbles()
        {
            if (nameTagRoot == null ||
                (SpeechBubbleGUI.all != null && SpeechBubbleGUI.all.Count > 0))
            {
                return;
            }

            RestoreDefaultRenderDepth();
        }

        public static void RestoreDefaultRenderDepth()
        {
            if (nameTagRoot == null)
                return;

            UIPanel panel = nameTagRoot.GetComponent<UIPanel>();
            if (panel != null && panel.depth != defaultPanelDepth)
                panel.depth = defaultPanelDepth;
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

using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// Player list panel for in-game GameGUI - displays connected players with styled background
    /// </summary>
    public class InGamePlayerListPanel : MonoBehaviour
    {
        private class PlayerEntry
        {
            public string Name;
            public CSteamID SteamID;
            public bool IsLocal;
            public GameObject RowObject;
            public UILabel NameLabel;
            public UILabel KickLabel;
            public UILabel PingLabel;
        }

        private GameObject listBackground;
        private readonly List<PlayerEntry> connectedPlayers = new List<PlayerEntry>();
        private UIFont rowFont;
        private Action<CSteamID> onKickRequested;
        private bool kickButtonsEnabled;
        private float nextPingRefreshAt;
        private const float RowHeight = 28f;
        private const float NameX = 0f;
        private const float KickX = 106f;
        private const float PingX = 122f;
        private const int NameWidth = 100;
        private const int KickWidth = 16;
        private const int PingWidth = 50;

        public void Initialize(Transform parent, Vector3 localPosition, UIFont font, GameObject backgroundTemplate, Action<CSteamID> kickRequested = null)
        {
            transform.SetParent(parent, false);
            gameObject.name = "InGamePlayerListPanel";
            gameObject.layer = 13;
            transform.localPosition = localPosition;
            rowFont = font;
            onKickRequested = kickRequested;
            
            // Create background at the SAME position as this panel
            listBackground = UnityEngine.Object.Instantiate(backgroundTemplate, parent);
            listBackground.name = "InGamePlayerListBackground";
            listBackground.layer = 13;
            listBackground.transform.localPosition = localPosition;
            listBackground.transform.localScale = Vector3.one;
            
            // Clean up the background
            var saveSlotGUI = listBackground.GetComponent<SaveSlotGUI>();
            if (saveSlotGUI != null) UnityEngine.Object.Destroy(saveSlotGUI);
            
            var colliders = listBackground.GetComponentsInChildren<Collider>(true);
            foreach (var col in colliders) UnityEngine.Object.Destroy(col);
            
            var labels = listBackground.GetComponentsInChildren<UILabel>(true);
            foreach (var label in labels) UnityEngine.Object.Destroy(label.gameObject);
            
            var buttons = listBackground.GetComponentsInChildren<UIButton>(true);
            foreach (var button in buttons) UnityEngine.Object.Destroy(button.gameObject);
            
            // Resize background for player list
            var bgWidget = listBackground.GetComponent<UIWidget>();
            if (bgWidget != null)
            {
                bgWidget.width = 200;
                bgWidget.height = 175;
                bgWidget.pivot = UIWidget.Pivot.TopLeft;
            }
            
            // Background ALWAYS at fixed position - text position (localPosition) can change independently
            listBackground.transform.localPosition = new Vector3(85, 185, 0);
            
            CoopMod.Logger.LogInfo($"InGamePlayerListPanel initialized at {localPosition}");
        }

        public void SetKickButtonsEnabled(bool enabled)
        {
            kickButtonsEnabled = enabled;
            RefreshKickButtons();
        }

        public void AddPlayer(string playerName)
        {
            CSteamID localSteamID = SteamManager.Initialized ? SteamUser.GetSteamID() : CSteamID.Nil;
            AddPlayer(playerName, localSteamID, true);
        }

        public void AddPlayer(string playerName, CSteamID steamID, bool isLocal = false)
        {
            if (string.IsNullOrEmpty(playerName))
                return;

            for (int i = 0; i < connectedPlayers.Count; i++)
            {
                PlayerEntry existing = connectedPlayers[i];
                if (existing.Name == playerName || (steamID != CSteamID.Nil && existing.SteamID == steamID))
                {
                    existing.Name = playerName;
                    existing.SteamID = steamID;
                    existing.IsLocal = isLocal;
                    RebuildRows();
                    return;
                }
            }

            connectedPlayers.Add(new PlayerEntry
            {
                Name = playerName,
                SteamID = steamID,
                IsLocal = isLocal
            });

            RebuildRows();
            CoopMod.Logger.LogInfo($"Player added to in-game list: {playerName}");
        }

        public void RemovePlayer(string playerName)
        {
            for (int i = connectedPlayers.Count - 1; i >= 0; i--)
            {
                if (connectedPlayers[i].Name == playerName)
                {
                    DestroyRow(connectedPlayers[i]);
                    connectedPlayers.RemoveAt(i);
                    RebuildRows();
                    CoopMod.Logger.LogInfo($"Player removed from in-game list: {playerName}");
                    return;
                }
            }
        }

        public void ClearPlayers()
        {
            foreach (var player in connectedPlayers)
            {
                DestroyRow(player);
            }

            connectedPlayers.Clear();
        }

        public int GetPlayerCount()
        {
            return connectedPlayers.Count;
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup < nextPingRefreshAt)
                return;

            nextPingRefreshAt = Time.realtimeSinceStartup + 1f;
            UpdatePingLabels();
        }

        private void RebuildRows()
        {
            if (rowFont == null) return;

            for (int i = 0; i < connectedPlayers.Count; i++)
            {
                PlayerEntry player = connectedPlayers[i];
                if (player.RowObject == null)
                {
                    CreateRow(player);
                }

                player.RowObject.transform.localPosition = new Vector3(0, -i * RowHeight, 0);

                if (player.NameLabel != null)
                {
                    player.NameLabel.text = player.Name;
                }
            }

            RefreshKickButtons();
            UpdatePingLabels();
        }

        private void CreateRow(PlayerEntry player)
        {
            GameObject rowObj = new GameObject($"PlayerRow_{player.Name}");
            rowObj.layer = 13;
            rowObj.transform.SetParent(transform, false);
            rowObj.transform.localScale = Vector3.one;

            player.RowObject = rowObj;
            player.NameLabel = CreateRowLabel(rowObj.transform, "Name", NameX, NameWidth, NGUIText.Alignment.Left, 106);
            player.KickLabel = CreateRowLabel(rowObj.transform, "Kick", KickX, KickWidth, NGUIText.Alignment.Center, 108);
            player.PingLabel = CreateRowLabel(rowObj.transform, "Ping", PingX, PingWidth, NGUIText.Alignment.Right, 106);

            if (player.NameLabel != null)
            {
                player.NameLabel.text = player.Name;
            }

            if (player.KickLabel != null)
            {
                player.KickLabel.text = "X";
                player.KickLabel.color = new Color(1f, 0.12f, 0.08f, 1f);
                player.KickLabel.effectStyle = UILabel.Effect.Outline;
                player.KickLabel.effectColor = Color.black;
                player.KickLabel.supportEncoding = false;

                var clickHandler = player.KickLabel.gameObject.AddComponent<PlayerAvatarKickClickHandler>();
                clickHandler.Initialize(player.SteamID, player.KickLabel, RequestKick);
            }
        }

        private UILabel CreateRowLabel(Transform parent, string name, float x, int width, NGUIText.Alignment alignment, int depth)
        {
            GameObject labelObj = new GameObject(name);
            labelObj.layer = 13;
            labelObj.transform.SetParent(parent, false);
            labelObj.transform.localPosition = new Vector3(x, 0, 0);
            labelObj.transform.localScale = Vector3.one;

            UILabel label = labelObj.AddComponent<UILabel>();
            label.bitmapFont = rowFont;
            label.text = "";
            label.fontSize = 18;
            label.color = new Color(0.875f, 0.667f, 0.424f, 1f);
            label.alignment = alignment;
            label.pivot = UIWidget.Pivot.TopLeft;
            label.overflowMethod = UILabel.Overflow.ShrinkContent;
            label.width = width;
            label.height = 24;
            label.depth = depth;
            label.supportEncoding = true;
            label.maxLineCount = 1;
            label.multiLine = false;
            return label;
        }

        private void DestroyRow(PlayerEntry player)
        {
            if (player?.RowObject != null)
            {
                Destroy(player.RowObject);
                player.RowObject = null;
                player.NameLabel = null;
                player.KickLabel = null;
                player.PingLabel = null;
            }
        }

        private void UpdatePingLabels()
        {
            for (int i = 0; i < connectedPlayers.Count; i++)
            {
                PlayerEntry player = connectedPlayers[i];
                if (player.PingLabel == null)
                    continue;

                player.PingLabel.text = player.SteamID != CSteamID.Nil
                    ? GetEncodedPingText(player.SteamID)
                    : "[B8B8C6]-- ms[-]";
            }
        }

        private void RefreshKickButtons()
        {
            foreach (var player in connectedPlayers)
            {
                if (player.KickLabel != null)
                {
                    player.KickLabel.gameObject.SetActive(ShouldShowKickButton(player));
                }
            }
        }

        private bool ShouldShowKickButton(PlayerEntry player)
        {
            if (!kickButtonsEnabled || onKickRequested == null || player == null)
                return false;

            CSteamID localID = SteamManager.Initialized ? SteamUser.GetSteamID() : CSteamID.Nil;
            return !player.IsLocal &&
                   player.SteamID != CSteamID.Nil &&
                   player.SteamID != localID;
        }

        private void RequestKick(CSteamID steamID)
        {
            if (!kickButtonsEnabled || onKickRequested == null)
                return;

            if (steamID == CSteamID.Nil || steamID == SteamUser.GetSteamID())
                return;

            onKickRequested(steamID);
        }

        private static string GetEncodedPingText(CSteamID steamID)
        {
            var indicator = PingIndicator.Instance;
            if (indicator == null)
                return "[B8B8C6]-- ms[-]";

            bool pending;
            int pingMs;
            if (!indicator.TryGetPing(steamID, out pingMs, out pending))
                return pending ? "[B8B8C6]...[-]" : "[B8B8C6]-- ms[-]";

            return $"{GetPingColorTag(pingMs)}{PingIndicator.FormatPingText(pingMs)}[-]";
        }

        private static string GetPingColorTag(int pingMs)
        {
            if (pingMs <= 50)
                return "[59FF59]";
            if (pingMs <= 100)
                return "[FFE659]";
            if (pingMs <= 200)
                return "[FF943F]";

            return "[FF5252]";
        }
    }
}

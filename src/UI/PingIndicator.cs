using System.Collections.Generic;
using GraveyardKeeperCoop.Multiplayer;
using GraveyardKeeperCoop.Network;
using Steamworks;
using UnityEngine;

namespace GraveyardKeeperCoop.UI
{
    public class PingIndicator : MonoBehaviour
    {
        private class PeerPingState
        {
            public CSteamID SteamID;
            public int LastPingMs = -1;
            public float LastPingAt;
            public float LastRequestAt;
            public bool Pending;
        }

        private const float PingIntervalSeconds = 3f;
        private const float PeerRefreshIntervalSeconds = 1f;
        private const float PingTimeoutSeconds = 8f;
        private const float StalePingSeconds = 12f;
        private const float Padding = 16f;
        private const float MinWidth = 96f;
        private const float Height = 28f;

        private static PingIndicator _instance;
        public static PingIndicator Instance => _instance;

        private readonly Dictionary<ulong, PeerPingState> peerStates = new Dictionary<ulong, PeerPingState>();
        private readonly List<CSteamID> activePeers = new List<CSteamID>();
        private readonly List<ulong> stalePeerKeys = new List<ulong>();

        private GUIStyle labelStyle;
        private GUIStyle shadowStyle;
        private Texture2D backgroundTexture;
        private bool subscribed;
        private bool shouldDraw;
        private string displayText = "Ping: -- ms";
        private Color displayColor = new Color(0.72f, 0.72f, 0.78f, 1f);
        private float nextPeerRefreshAt;
        private float nextPingAt;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDisable()
        {
            UnsubscribeFromPingResults();
        }

        private void OnDestroy()
        {
            UnsubscribeFromPingResults();

            if (_instance == this)
                _instance = null;

            if (backgroundTexture != null)
            {
                Destroy(backgroundTexture);
                backgroundTexture = null;
            }
        }

        private void Update()
        {
            if (!SteamManager.Initialized)
            {
                ResetIndicator();
                return;
            }

            if (!IsMultiplayerActive())
            {
                ResetIndicator();
                return;
            }

            EnsureSubscribedToPingResults();

            float now = Time.realtimeSinceStartup;
            if (now >= nextPeerRefreshAt)
            {
                RefreshActivePeers();
                nextPeerRefreshAt = now + PeerRefreshIntervalSeconds;
            }

            SendDuePings(now);
            UpdateDisplay(now);
        }

        private void OnGUI()
        {
            if (!shouldDraw)
                return;

            EnsureGuiResources();

            GUIContent content = new GUIContent(displayText);
            Vector2 textSize = labelStyle.CalcSize(content);
            float width = Mathf.Max(MinWidth, textSize.x + 22f);
            Rect rect = new Rect(Screen.width - width - Padding, Screen.height - Height - Padding, width, Height);
            Rect textRect = new Rect(rect.x + 10f, rect.y, rect.width - 20f, rect.height);

            int oldDepth = GUI.depth;
            Color oldColor = GUI.color;
            GUI.depth = -100;

            GUI.color = Color.white;
            GUI.DrawTexture(rect, backgroundTexture);

            shadowStyle.normal.textColor = new Color(0f, 0f, 0f, 0.95f);
            GUI.Label(new Rect(textRect.x + 1f, textRect.y + 1f, textRect.width, textRect.height), content, shadowStyle);

            labelStyle.normal.textColor = displayColor;
            GUI.Label(textRect, content, labelStyle);

            GUI.color = oldColor;
            GUI.depth = oldDepth;
        }

        private bool IsMultiplayerActive()
        {
            var lobby = SteamLobbyManager.Instance;
            if (lobby != null && lobby.IsInLobby)
                return true;

            var online = OnlineCoopManager.Instance;
            return online != null && online.IsOnlineCoopEnabled;
        }

        private void RefreshActivePeers()
        {
            activePeers.Clear();

            CSteamID localId = SteamUser.GetSteamID();
            var online = OnlineCoopManager.Instance;
            if (online != null && online.IsOnlineCoopEnabled)
            {
                AddPeer(online.RemotePlayerSteamID, localId);
            }
            else
            {
                var lobby = SteamLobbyManager.Instance;
                if (lobby != null && lobby.IsInLobby && lobby.CurrentLobbyID != CSteamID.Nil)
                {
                    int memberCount = SteamMatchmaking.GetNumLobbyMembers(lobby.CurrentLobbyID);
                    for (int i = 0; i < memberCount; i++)
                    {
                        AddPeer(SteamMatchmaking.GetLobbyMemberByIndex(lobby.CurrentLobbyID, i), localId);
                    }
                }
            }

            RemoveStalePeers();
        }

        private void AddPeer(CSteamID peer, CSteamID localId)
        {
            if (peer == CSteamID.Nil || peer == localId)
                return;

            for (int i = 0; i < activePeers.Count; i++)
            {
                if (activePeers[i] == peer)
                    return;
            }

            activePeers.Add(peer);
            if (!peerStates.ContainsKey(peer.m_SteamID))
            {
                peerStates[peer.m_SteamID] = new PeerPingState
                {
                    SteamID = peer
                };
            }
        }

        private void RemoveStalePeers()
        {
            stalePeerKeys.Clear();

            foreach (var pair in peerStates)
            {
                bool stillActive = false;
                for (int i = 0; i < activePeers.Count; i++)
                {
                    if (activePeers[i].m_SteamID == pair.Key)
                    {
                        stillActive = true;
                        break;
                    }
                }

                if (!stillActive)
                    stalePeerKeys.Add(pair.Key);
            }

            for (int i = 0; i < stalePeerKeys.Count; i++)
                peerStates.Remove(stalePeerKeys[i]);
        }

        private void SendDuePings(float now)
        {
            if (activePeers.Count == 0 || now < nextPingAt)
                return;

            var p2p = SteamP2PManager.Instance;
            if (p2p == null)
                return;

            for (int i = 0; i < activePeers.Count; i++)
            {
                PeerPingState state;
                if (!peerStates.TryGetValue(activePeers[i].m_SteamID, out state))
                    continue;

                if (state.Pending && now - state.LastRequestAt < PingTimeoutSeconds)
                    continue;

                state.Pending = true;
                state.LastRequestAt = now;
                p2p.SendPing(state.SteamID);
            }

            nextPingAt = now + PingIntervalSeconds;
        }

        private void UpdateDisplay(float now)
        {
            if (activePeers.Count == 0)
            {
                shouldDraw = true;
                displayText = "Ping: 0 ms";
                displayColor = GetPingColor(0);
                return;
            }

            shouldDraw = true;

            int validCount = 0;
            int pingTotal = 0;
            int worstPing = -1;
            bool anyPending = false;

            for (int i = 0; i < activePeers.Count; i++)
            {
                PeerPingState state;
                if (!peerStates.TryGetValue(activePeers[i].m_SteamID, out state))
                    continue;

                if (state.Pending && now - state.LastRequestAt >= PingTimeoutSeconds)
                    state.Pending = false;

                anyPending |= state.Pending;

                if (state.LastPingMs >= 0 && now - state.LastPingAt <= StalePingSeconds)
                {
                    validCount++;
                    pingTotal += state.LastPingMs;
                    if (state.LastPingMs > worstPing)
                        worstPing = state.LastPingMs;
                }
            }

            if (validCount > 0)
            {
                int displayPing = Mathf.RoundToInt((float)pingTotal / validCount);
                displayText = $"Ping: {displayPing} ms";
                displayColor = GetPingColor(worstPing);
            }
            else
            {
                displayText = anyPending ? "Ping: ..." : "Ping: -- ms";
                displayColor = new Color(0.72f, 0.72f, 0.78f, 1f);
            }
        }

        private void OnPingResult(CSteamID steamID, int pingMs)
        {
            PeerPingState state;
            if (!peerStates.TryGetValue(steamID.m_SteamID, out state))
                return;

            state.LastPingMs = pingMs;
            state.LastPingAt = Time.realtimeSinceStartup;
            state.Pending = false;
        }

        public bool TryGetPing(CSteamID steamID, out int pingMs, out bool pending)
        {
            pingMs = -1;
            pending = false;

            if (steamID == CSteamID.Nil)
                return false;

            if (SteamManager.Initialized && steamID == SteamUser.GetSteamID())
            {
                pingMs = 0;
                pending = false;
                return true;
            }

            PeerPingState state;
            if (!peerStates.TryGetValue(steamID.m_SteamID, out state))
                return false;

            float now = Time.realtimeSinceStartup;
            pending = state.Pending && now - state.LastRequestAt < PingTimeoutSeconds;

            if (state.LastPingMs < 0 || now - state.LastPingAt > StalePingSeconds)
                return false;

            pingMs = state.LastPingMs;
            return true;
        }

        public static string FormatPingText(int pingMs)
        {
            if (pingMs < 0)
                return "--ms";

            return $"{pingMs}ms";
        }

        private void EnsureSubscribedToPingResults()
        {
            if (subscribed)
                return;

            var p2p = SteamP2PManager.Instance;
            if (p2p == null)
                return;

            p2p.OnPingResult -= OnPingResult;
            p2p.OnPingResult += OnPingResult;
            subscribed = true;
        }

        private void UnsubscribeFromPingResults()
        {
            if (!subscribed)
                return;

            var p2p = SteamP2PManager.Instance;
            if (p2p != null)
                p2p.OnPingResult -= OnPingResult;

            subscribed = false;
        }

        private void ResetIndicator()
        {
            shouldDraw = false;
            activePeers.Clear();
            peerStates.Clear();
            nextPeerRefreshAt = 0f;
            nextPingAt = 0f;
        }

        private void EnsureGuiResources()
        {
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 14,
                    fontStyle = FontStyle.Bold,
                    clipping = TextClipping.Clip
                };
            }

            if (shadowStyle == null)
            {
                shadowStyle = new GUIStyle(labelStyle);
            }

            if (backgroundTexture == null)
            {
                backgroundTexture = new Texture2D(1, 1, TextureFormat.ARGB32, false);
                backgroundTexture.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.48f));
                backgroundTexture.Apply();
            }
        }

        private static Color GetPingColor(int pingMs)
        {
            if (pingMs < 0)
                return new Color(0.72f, 0.72f, 0.78f, 1f);
            if (pingMs <= 50)
                return new Color(0.35f, 1f, 0.35f, 1f);
            if (pingMs <= 100)
                return new Color(1f, 0.9f, 0.35f, 1f);
            if (pingMs <= 200)
                return new Color(1f, 0.58f, 0.25f, 1f);

            return new Color(1f, 0.32f, 0.32f, 1f);
        }
    }
}

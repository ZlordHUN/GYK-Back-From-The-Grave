using UnityEngine;
using GraveyardKeeperCoop.Multiplayer;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.UI
{
    public class NetworkDebugOverlay : MonoBehaviour
    {
        private static NetworkDebugOverlay _instance;
        public static NetworkDebugOverlay Instance => _instance;

        private Rect windowRect = new Rect(20f, 80f, 390f, 430f);
        private bool visible;

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

        private void Update()
        {
            if (ModConfig.EnableNetworkDebugOverlay != null && !ModConfig.EnableNetworkDebugOverlay.Value)
                return;

            if (Input.GetKeyDown(KeyCode.F4))
                visible = !visible;
        }

        private void OnGUI()
        {
            if (!visible || (ModConfig.EnableNetworkDebugOverlay != null && !ModConfig.EnableNetworkDebugOverlay.Value))
                return;

            windowRect = GUILayout.Window(GetInstanceID(), windowRect, DrawWindow, "Back From The Grave Network");
        }

        private void DrawWindow(int windowId)
        {
            var p2p = SteamP2PManager.Instance;
            var lobby = SteamLobbyManager.Instance;
            var online = OnlineCoopManager.Instance;

            GUILayout.Label($"Lobby: {(lobby?.IsInLobby == true ? lobby.CurrentLobbyID.m_SteamID.ToString() : "none")} host={lobby?.IsHost == true} members={GetMemberCount(lobby)}");
            GUILayout.Label($"Online: {online?.IsOnlineCoopEnabled == true} role={(online?.IsHost == true ? "host" : online?.IsOnlineCoopEnabled == true ? "client" : "none")}");
            GUILayout.Label($"Remote: {(online?.RemotePlayerSteamID.m_SteamID ?? 0UL)}");

            if (p2p != null)
            {
                GUILayout.Space(4f);
                GUILayout.Label($"P2P pps: tx={p2p.PacketsSentPerSecond} rx={p2p.PacketsReceivedPerSecond}");
                GUILayout.Label($"P2P bytes/s: tx={p2p.BytesSentPerSecond} rx={p2p.BytesReceivedPerSecond}");
                GUILayout.Label($"Reliable queue: {p2p.ReliableQueueLength} dropped={p2p.ReliableDroppedPackets}");
                GUILayout.Label($"Totals: tx={p2p.TotalPacketsSent}/{p2p.TotalBytesSent}B rx={p2p.TotalPacketsReceived}/{p2p.TotalBytesReceived}B");
                if (!string.IsNullOrEmpty(p2p.LastNetworkError))
                    GUILayout.Label($"Last error: {p2p.LastNetworkError}");
            }

            GUILayout.Space(4f);
            var visual = PlayerVisualSync.Instance;
            var liveWgo = LiveWGOTransformSync.Instance;
            GUILayout.Label($"Visual sync sprites: {visual?.SpriteLibrarySize ?? 0}");
            GUILayout.Label($"Live WGO: cache={liveWgo?.CachedObjectCount ?? 0} targets={liveWgo?.TargetObjectCount ?? 0}");

            if (online?.LocalPlayerComponent != null)
                GUILayout.Label($"Local pos: {FormatVector(online.LocalPlayerComponent.transform.position)}");
            if (online?.RemotePlayerComponent != null)
                GUILayout.Label($"Remote pos: {FormatVector(online.RemotePlayerComponent.transform.position)}");

            GUILayout.Space(4f);
            if (SaveTransferManager.IsTransferActive)
            {
                GUILayout.Label($"Save transfer: {SaveTransferManager.ReceivedChunks}/{SaveTransferManager.ExpectedChunks} ({SaveTransferManager.LastTransferProgress:P0})");
            }
            else
            {
                GUILayout.Label($"Save transfer: idle ({SaveTransferManager.LastTransferProgress:P0})");
            }

            string[] recentErrors = NetworkDiagnostics.RecentErrors();
            if (recentErrors.Length > 0)
            {
                GUILayout.Space(4f);
                GUILayout.Label("Recent errors:");
                int first = Mathf.Max(0, recentErrors.Length - 5);
                for (int i = first; i < recentErrors.Length; i++)
                    GUILayout.Label(recentErrors[i]);
            }

            GUI.DragWindow();
        }

        private static int GetMemberCount(SteamLobbyManager lobby)
        {
            if (lobby == null || !lobby.IsInLobby)
                return 0;

            return lobby.GetLobbyMemberCount();
        }

        private static string FormatVector(Vector3 value)
        {
            return $"{value.x:F1}, {value.y:F1}, {value.z:F1}";
        }
    }
}

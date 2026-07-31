using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Steamworks;
using UnityEngine;

namespace GraveyardKeeperCoop.Network
{
    /// <summary>
    /// Manages Steam P2P (peer-to-peer) messaging for custom game events.
    /// Uses binary protocol for efficient message encoding.
    /// </summary>
    public class SteamP2PManager
    {
        private struct QueuedReliablePacket
        {
            public CSteamID Recipient;
            public byte[] Data;
            public int Channel;
        }

        private const int MaxUnreliablePacketBytes = 1100;
        private const int MaxReliableSendsPerSecond = 30;
        private const int MaxQueuedReliablePackets = 4000;
        private const int MaxPacketsProcessedPerFrame = 64;
        // A packet flood must not monopolize the Unity main thread. Reliable frames
        // remain queued/retransmitted, while obsolete unreliable state can be superseded.
        private const int MaxPacketProcessingMilliseconds = 4;
        private const int MaxLobbyLaneMessageBytes = 256 * 1024;
        private const float PreLobbyAdmissionSeconds = 120f;
        private const float ReliableQueueFullWarningIntervalSeconds = 1f;
        private const float SendFailureWarningIntervalSeconds = 2f;
        private const float OversizedUnreliableWarningIntervalSeconds = 2f;
        private const float ReceiveTelemetryIntervalSeconds = 5f;

        private static SteamP2PManager _instance;
        public static SteamP2PManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new SteamP2PManager();
                }
                return _instance;
            }
        }

        // Steam callbacks
        private Callback<P2PSessionRequest_t> p2pSessionRequestCallback;

        public ReliableTransport Reliable { get; private set; }

        private readonly Dictionary<ulong, float> lastPacketRealtimeFrom = new Dictionary<ulong, float>();
        private readonly HashSet<ulong> sessionPeers = new HashSet<ulong>();
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private byte[] receiveBuffer = new byte[2048];
        private bool ownsRunInBackground;
        private bool previousRunInBackground;

        // Events for game state
        public event Action<CSteamID, string> OnGameStartReceived;
        public event Action<CSteamID> OnClientGameLoaded;
        public event Action<CSteamID> OnPlayerReadyToPlay;
        public event Action<CSteamID, WorldEntryBarrierPhase> OnWorldEntryBarrier;

        // Events for player sync
        public event Action<CSteamID, Vector3, float> OnRemotePlayerPosition; // position + timestamp
        public event Action<CSteamID, Vector2, bool, Vector2> OnRemotePlayerState;
        public event Action<CSteamID, int, int> OnRemotePlayerAnimation; // animState (CharAnimState), itemType (ItemDefinition.ItemType as int)
        public event Action<CSteamID, byte[]> OnPlayerParityReceived;
        public event Action<CSteamID, byte[]> OnPlayerVisualSyncReceived;

        // Events for game sync
        public event Action<CSteamID, int, float> OnTimeSyncReceived;
        public event Action<CSteamID, long> OnWGODestroyReceived;
        public event Action<CSteamID, byte[]> OnWeatherSyncReceived;
        public event Action<CSteamID, byte[]> OnInventorySyncReceived;
        public event Action<CSteamID, byte[]> OnWGOStateSyncReceived;

        public event Action<CSteamID, byte[]> OnCraftSyncReceived;

        public event Action<CSteamID, byte[]> OnTechSyncReceived;

        public event Action<CSteamID, byte[]> OnQuestSyncReceived;

        public event Action<CSteamID, byte[]> OnZoneNavSyncReceived;

        public event Action<CSteamID, byte[]> OnCombatSyncReceived;

        public event Action<CSteamID, byte[]> OnPlayerParamSyncReceived;

        public event Action<CSteamID, byte[]> OnSpawnSyncReceived;

        public event Action<CSteamID, byte[]> OnDungeonSyncReceived;

        public event Action<CSteamID, byte[]> OnWorkerSyncReceived;

        public event Action<CSteamID, byte[]> OnFishingSyncReceived;

        public event Action<CSteamID, byte[]> OnDLCSyncReceived;
        public event Action<CSteamID, byte[]> OnWGOTransformSyncReceived;
        public event Action<CSteamID, byte[]> OnInteractionRequestReceived;
        public event Action<CSteamID, byte[]> OnInteractionZeroHpReceived;
        public event Action<CSteamID, byte[]> OnNpcVisualSyncReceived;
        public event Action<CSteamID, byte[]> OnWorkIndicatorSyncReceived;

        // Events for dialogue
        public event Action<CSteamID, Op, MsgReader> OnDialogueMessage;

        // Events for chat
        public event Action<CSteamID, string> OnChatMessageReceived;

        // Events for cosmetics
        public event Action<CSteamID, byte[]> OnCosmeticsReceived;
        public event Action<CSteamID> OnCosmeticsRequested;

        // Events for cutscene/FlowScript sync
        public event Action<CSteamID, string, Vector3, byte, string, bool> OnCutsceneSyncReceived;
        public event Action<CSteamID, string, Vector3, string> OnCutsceneCompleteReceived;
        public event Action<CSteamID, CutsceneCameraState> OnCutsceneCameraReceived;

        /// <summary>
        /// Fired when the remote player asks us to walk our local player to a position next to them.
        /// Args: senderID, target world position (where the digger is), facing direction (Direction enum byte).
        /// </summary>
        public event Action<CSteamID, Vector3, byte> OnCutsceneWalkToReceived;

        /// <summary>
        /// Fired when the remote player interacted with an NPC (donkey, Gerry, etc.) so we can
        /// replay the same interaction locally and keep both save states in sync.
        /// Args: senderID, NPC custom_tag (may be ""), NPC obj_id, NPC world position,
        /// origin zone id, whether scope metadata was present, interaction owner.
        /// </summary>
        public event Action<CSteamID, string, string, Vector3, string, bool, CSteamID> OnNpcInteractionReceived;
        public event Action<CSteamID, string, string> OnNpcInteractionCompleteReceived;

        /// <summary>
        /// Fired on the HOST when a client asks for the save slot list.
        /// Args: requesting client's steamID.
        /// </summary>
        public event Action<CSteamID> OnHostSaveListRequested;

        /// <summary>
        /// Fired on the CLIENT when the host sends their save slot list.
        /// Args: host steamID, list of save entries, and active filename.
        /// The active filename is null when the host has made no selection and
        /// empty only when the host explicitly selected New Game.
        /// </summary>
        public event Action<CSteamID, System.Collections.Generic.List<HostSaveEntryWire>, string> OnHostSaveListReceived;

        /// <summary>
        /// Fired on the CLIENT when the host picks a different save slot.
        /// Args: host steamID, selected filename_no_extension (or "" for New Game).
        /// </summary>
        public event Action<CSteamID, string> OnHostSaveSelectedReceived;

        // Intro skip sync
        public event Action<CSteamID> OnSkipIntroReceived;
        public event Action<CSteamID, IntroSkipPhase, CSteamID, uint, float> OnIntroSkipStateReceived;

        // Lobby control
        public event Action<CSteamID, string> OnLobbyKickReceived;

        // Ping measurement
        public event Action<CSteamID, int> OnPingResult; // steamID, ping in ms
        private readonly System.Collections.Generic.Dictionary<ulong, float> _pendingPings = new System.Collections.Generic.Dictionary<ulong, float>();
        private CSteamID pendingLobbyInfoHost = CSteamID.Nil;
        private readonly Dictionary<ulong, float> preLobbyAdmissionUntil = new Dictionary<ulong, float>();
        private readonly HashSet<ulong> loggedUnvalidatedPeerPackets =
            new HashSet<ulong>();

        private readonly Queue<QueuedReliablePacket> queuedReliablePackets = new Queue<QueuedReliablePacket>();
        private readonly Dictionary<ulong, float> nextSendFailureWarningAt = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, int> suppressedSendFailureWarnings = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, float> nextOversizedUnreliableWarningAt = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, int> suppressedOversizedUnreliableWarnings = new Dictionary<ulong, int>();
        private float reliableRateWindowStart;
        private int reliableSentThisWindow;
        private int reliableDroppedPackets;
        private float nextReliableQueueFullWarningAt;
        private int suppressedReliableQueueFullWarnings;

        private float packetStatsWindowStart;
        private int sentPacketsInWindow;
        private int receivedPacketsInWindow;
        private int sentBytesInWindow;
        private int receivedBytesInWindow;
        private int packetsSentPerSecond;
        private int packetsReceivedPerSecond;
        private int bytesSentPerSecond;
        private int bytesReceivedPerSecond;
        private long totalPacketsSent;
        private long totalPacketsReceived;
        private long totalBytesSent;
        private long totalBytesReceived;
        private string lastNetworkError = string.Empty;
        private float lastNetworkErrorAt;

        // Low-overhead receive telemetry. The raw Steam queue contains RNET frames,
        // while application messages may be delivered after channel reassembly, so
        // track both layers. This is reported only when the per-frame receive budget
        // leaves packets queued.
        private readonly int[] receivedAppOps = new int[256];
        private readonly long[] receivedAppOpTicks = new long[256];
        private readonly long[] receivedAppOpBytes = new long[256];
        private float receiveTelemetryWindowStart;
        private int receiveWirePackets;
        private long receiveWireBytes;
        private int receiveRnetDataFrames;
        private int receiveRnetAckFrames;
        private int receiveBudgetLimitedFrames;
        private int maxPacketsProcessedInFrame;

        public int ReliableQueueLength => queuedReliablePackets.Count;
        public int ReliableDroppedPackets => reliableDroppedPackets;
        public int PacketsSentPerSecond { get { UpdatePacketStatsWindow(); return packetsSentPerSecond; } }
        public int PacketsReceivedPerSecond { get { UpdatePacketStatsWindow(); return packetsReceivedPerSecond; } }
        public int BytesSentPerSecond { get { UpdatePacketStatsWindow(); return bytesSentPerSecond; } }
        public int BytesReceivedPerSecond { get { UpdatePacketStatsWindow(); return bytesReceivedPerSecond; } }
        public long TotalPacketsSent => totalPacketsSent;
        public long TotalPacketsReceived => totalPacketsReceived;
        public long TotalBytesSent => totalBytesSent;
        public long TotalBytesReceived => totalBytesReceived;
        public string LastNetworkError => lastNetworkError;
        public float LastNetworkErrorAt => lastNetworkErrorAt;

        private SteamP2PManager()
        {
            CoopMod.Logger.LogInfo("[P2P] ========== SteamP2PManager() CONSTRUCTOR ==========");
            CoopMod.Logger.LogInfo($"[P2P] SteamManager.Initialized = {SteamManager.Initialized}");

            if (!SteamManager.Initialized)
            {
                CoopMod.Logger.LogError("[P2P] Steam is not initialized! Cannot set up P2P manager.");
                return;
            }

            p2pSessionRequestCallback = Callback<P2PSessionRequest_t>.Create(OnP2PSessionRequest);
            Reliable = new ReliableTransport(this);
            CoopMod.Logger.LogInfo("[P2P] ✓ SteamP2PManager initialized with binary protocol");
        }

        /// <summary>
        /// Call this every frame to process incoming P2P messages
        /// </summary>
        public void Update()
        {
            if (!SteamManager.Initialized)
                return;

            DrainReliableQueue();
            long transportStartedAt = GraveyardKeeperCoop.Utils.FrameProfiler.BeginSection();
            Reliable?.Tick();
            GraveyardKeeperCoop.Utils.FrameProfiler.EndSection("P2P.Transport", transportStartedAt);
            UpdatePacketStatsWindow();

            uint msgSize;
            int processed = 0;
            long receiveStartedAt = Stopwatch.GetTimestamp();
            while (processed < MaxPacketsProcessedPerFrame &&
                   (processed == 0 ||
                    ElapsedMilliseconds(receiveStartedAt) < MaxPacketProcessingMilliseconds) &&
                   SteamNetworking.IsP2PPacketAvailable(out msgSize, 0))
            {
                if (msgSize > int.MaxValue)
                {
                    SetLastNetworkError($"Incoming P2P packet is too large ({msgSize} bytes)");
                    CoopMod.Logger.LogError($"[P2P] Incoming packet is too large: {msgSize} bytes");
                    break;
                }

                EnsureReceiveBuffer((int)msgSize);
                CSteamID senderID;
                uint bytesRead;

                if (SteamNetworking.ReadP2PPacket(
                    receiveBuffer,
                    (uint)receiveBuffer.Length,
                    out bytesRead,
                    out senderID,
                    0))
                {
                    RecordPacketReceived((int)bytesRead);
                    RecordIncomingWirePacket(receiveBuffer, (int)bytesRead);
                    lastPacketRealtimeFrom[senderID.m_SteamID] = Time.realtimeSinceStartup;
                    HandleIncomingPacket(senderID, receiveBuffer, (int)bytesRead);
                }
                else
                {
                    SetLastNetworkError("Failed to read P2P packet");
                    CoopMod.Logger.LogError("[P2P] Failed to read P2P packet!");
                }
                processed++;
            }
            if (processed > maxPacketsProcessedInFrame)
                maxPacketsProcessedInFrame = processed;

            bool processingLimitReached =
                processed >= MaxPacketsProcessedPerFrame ||
                (processed > 0 &&
                 ElapsedMilliseconds(receiveStartedAt) >= MaxPacketProcessingMilliseconds);
            if (processingLimitReached &&
                SteamNetworking.IsP2PPacketAvailable(out uint pendingPacketSize, 0))
            {
                receiveBudgetLimitedFrames++;
            }

            if (GraveyardKeeperCoop.Utils.FrameProfiler.Enabled)
            {
                GraveyardKeeperCoop.Utils.FrameProfiler.Record(
                    "P2P.Receive",
                    Stopwatch.GetTimestamp() - receiveStartedAt);
            }
            MaybeReportReceiveTelemetry();
        }

        private static long ElapsedMilliseconds(long startedAt)
        {
            return (Stopwatch.GetTimestamp() - startedAt) * 1000L / Stopwatch.Frequency;
        }

        private void EnsureReceiveBuffer(int requiredBytes)
        {
            if (receiveBuffer.Length >= requiredBytes)
                return;

            int size = receiveBuffer.Length;
            while (size < requiredBytes && size <= int.MaxValue / 2)
                size *= 2;
            if (size < requiredBytes)
                size = requiredBytes;
            receiveBuffer = new byte[size];
        }

        #region Send Methods

        /// <summary>
        /// Send a binary message to a specific user
        /// </summary>
        public bool SendBinary(CSteamID recipientID, byte[] data, EP2PSend sendType = EP2PSend.k_EP2PSendReliable, int channel = 0)
        {
            if (!SteamManager.Initialized) return false;
            if (recipientID == CSteamID.Nil || data == null || data.Length == 0) return false;

            bool isLobbyKick = data[0] == (byte)Op.LobbyKick;
            bool isUnvalidatedHostPeer =
                SteamLobbyManager.Instance.IsHost &&
                !SteamLobbyManager.Instance.IsPeerVersionValidated(recipientID) &&
                IsCurrentLobbyMember(recipientID);
            if (isUnvalidatedHostPeer)
            {
                if (isLobbyKick)
                {
                    // The application channel is deliberately unopened for rejected
                    // peers, so its rejection must use Steam native reliable directly.
                    return SendReliableWithBackpressure(recipientID, data, channel);
                }

                CoopMod.Logger.LogWarning(
                    $"[P2P] Blocked send to unvalidated lobby member " +
                    recipientID);
                return false;
            }

            bool oversizedUnreliable = IsUnreliableSend(sendType) && data.Length > MaxUnreliablePacketBytes;
            if ((IsReliableSend(sendType) || oversizedUnreliable) &&
                Reliable != null &&
                IsCurrentLobbyMember(recipientID))
            {
                ReliableSendDisposition disposition = Reliable.RouteSend(recipientID, data, channel);
                if (disposition == ReliableSendDisposition.Handled)
                    return true;
                if (disposition == ReliableSendDisposition.Rejected)
                    return false;
            }

            if (oversizedUnreliable)
            {
                RecordOversizedUnreliableFallback(recipientID, data, sendType, channel);
                return SendReliableWithBackpressure(recipientID, data, channel);
            }

            if (IsReliableSend(sendType))
            {
                return SendReliableWithBackpressure(recipientID, data, channel);
            }

            return SendBinaryImmediate(recipientID, data, sendType, channel);
        }

        public bool SendVersionHello(CSteamID hostID)
        {
            if (hostID == CSteamID.Nil)
                return false;

            using (var writer = new MsgWriter(Op.Hello))
            {
                writer.Write(PluginInfo.PLUGIN_VERSION ?? string.Empty);
                // Admission precedes RNET capability. Keep this one packet on
                // native Steam reliable so the host can validate us first.
                return SendBinaryImmediate(
                    hostID,
                    writer.ToArray(),
                    EP2PSend.k_EP2PSendReliable,
                    0);
            }
        }

        private bool SendReliableWithBackpressure(CSteamID recipientID, byte[] data, int channel)
        {
            ResetReliableRateWindowIfNeeded();

            if (queuedReliablePackets.Count == 0 && reliableSentThisWindow < MaxReliableSendsPerSecond)
            {
                bool sent = SendBinaryImmediate(recipientID, data, EP2PSend.k_EP2PSendReliable, channel);
                if (sent)
                    reliableSentThisWindow++;
                return sent;
            }

            return EnqueueReliable(recipientID, data, channel);
        }

        private bool SendBinaryImmediate(CSteamID recipientID, byte[] data, EP2PSend sendType, int channel)
        {
            if (!SteamManager.Initialized) return false;

            bool success = SteamNetworking.SendP2PPacket(recipientID, data, (uint)data.Length, sendType, channel);

            if (!success)
            {
                RecordSendFailure(recipientID, data, sendType, channel);
            }
            else
            {
                sessionPeers.Add(recipientID.m_SteamID);
                RecordPacketSent(data.Length);
            }

            return success;
        }

        private void RecordSendFailure(CSteamID recipientID, byte[] data, EP2PSend sendType, int channel)
        {
            int byteCount = data?.Length ?? 0;
            ulong key = GetSendWarningKey(recipientID, data, channel);
            float now = Time.realtimeSinceStartup;

            if (!nextSendFailureWarningAt.TryGetValue(key, out float nextWarningAt) || now >= nextWarningAt)
            {
                int suppressed = 0;
                suppressedSendFailureWarnings.TryGetValue(key, out suppressed);
                suppressedSendFailureWarnings[key] = 0;
                nextSendFailureWarningAt[key] = now + SendFailureWarningIntervalSeconds;

                string suffix = suppressed > 0 ? $", suppressed={suppressed}" : string.Empty;
                string packetName = DescribePacket(data);
                SetLastNetworkError($"Failed to send {packetName} ({byteCount} bytes, {sendType}, ch={channel}) to {recipientID}{suffix}");
                CoopMod.Logger.LogError($"[P2P] Failed to send {packetName} ({byteCount} bytes, {sendType}, ch={channel}) to {recipientID}{suffix}");
                return;
            }

            int count = 0;
            suppressedSendFailureWarnings.TryGetValue(key, out count);
            suppressedSendFailureWarnings[key] = count + 1;
        }

        private void RecordOversizedUnreliableFallback(CSteamID recipientID, byte[] data, EP2PSend sendType, int channel)
        {
            ulong key = GetSendWarningKey(recipientID, data, channel);
            float now = Time.realtimeSinceStartup;

            if (!nextOversizedUnreliableWarningAt.TryGetValue(key, out float nextWarningAt) || now >= nextWarningAt)
            {
                int suppressed = 0;
                suppressedOversizedUnreliableWarnings.TryGetValue(key, out suppressed);
                suppressedOversizedUnreliableWarnings[key] = 0;
                nextOversizedUnreliableWarningAt[key] = now + OversizedUnreliableWarningIntervalSeconds;

                string suffix = suppressed > 0 ? $", suppressed={suppressed}" : string.Empty;
                CoopMod.Logger.LogWarning(
                    $"[P2P] {DescribePacket(data)} is {data.Length} bytes, too large for {sendType} " +
                    $"(limit {MaxUnreliablePacketBytes}); sending reliable instead to {recipientID}, ch={channel}{suffix}");
                return;
            }

            int count = 0;
            suppressedOversizedUnreliableWarnings.TryGetValue(key, out count);
            suppressedOversizedUnreliableWarnings[key] = count + 1;
        }

        private bool EnqueueReliable(CSteamID recipientID, byte[] data, int channel)
        {
            if (queuedReliablePackets.Count >= MaxQueuedReliablePackets)
            {
                reliableDroppedPackets++;
                float now = Time.realtimeSinceStartup;
                if (now >= nextReliableQueueFullWarningAt)
                {
                    string suppressed = suppressedReliableQueueFullWarnings > 0
                        ? $", suppressed={suppressedReliableQueueFullWarnings}"
                        : "";
                    SetLastNetworkError($"Reliable queue full ({queuedReliablePackets.Count}); dropped {data.Length} byte packet");
                    CoopMod.Logger.LogWarning($"[P2P] Reliable queue full ({queuedReliablePackets.Count}); dropping packet to {recipientID}{suppressed}");
                    suppressedReliableQueueFullWarnings = 0;
                    nextReliableQueueFullWarningAt = now + ReliableQueueFullWarningIntervalSeconds;
                }
                else
                {
                    suppressedReliableQueueFullWarnings++;
                }
                return false;
            }

            var copy = new byte[data.Length];
            Buffer.BlockCopy(data, 0, copy, 0, data.Length);
            queuedReliablePackets.Enqueue(new QueuedReliablePacket
            {
                Recipient = recipientID,
                Data = copy,
                Channel = channel
            });
            return true;
        }

        private void DrainReliableQueue()
        {
            ResetReliableRateWindowIfNeeded();

            while (queuedReliablePackets.Count > 0 && reliableSentThisWindow < MaxReliableSendsPerSecond)
            {
                QueuedReliablePacket packet = queuedReliablePackets.Dequeue();
                if (SendBinaryImmediate(packet.Recipient, packet.Data, EP2PSend.k_EP2PSendReliable, packet.Channel))
                {
                    reliableSentThisWindow++;
                }
            }
        }

        private void ResetReliableRateWindowIfNeeded()
        {
            float now = Time.realtimeSinceStartup;
            if (reliableRateWindowStart <= 0f || now - reliableRateWindowStart >= 1f)
            {
                reliableRateWindowStart = now;
                reliableSentThisWindow = 0;
            }
        }

        private static bool IsReliableSend(EP2PSend sendType)
        {
            return sendType == EP2PSend.k_EP2PSendReliable ||
                   sendType == EP2PSend.k_EP2PSendReliableWithBuffering;
        }

        private static bool IsUnreliableSend(EP2PSend sendType)
        {
            return sendType == EP2PSend.k_EP2PSendUnreliable;
        }

        private static ulong GetSendWarningKey(CSteamID recipientID, byte[] data, int channel)
        {
            ulong op = data != null && data.Length > 0 ? data[0] : 0UL;
            return recipientID.m_SteamID ^ ((ulong)(uint)channel << 48) ^ (op << 56);
        }

        private static string DescribePacket(byte[] data)
        {
            if (data != null && data.Length > 0 && Enum.IsDefined(typeof(Op), data[0]))
                return ((Op)data[0]).ToString();

            return "packet";
        }

        private void RecordPacketSent(int bytes)
        {
            UpdatePacketStatsWindow();
            sentPacketsInWindow++;
            sentBytesInWindow += Mathf.Max(0, bytes);
            totalPacketsSent++;
            totalBytesSent += Mathf.Max(0, bytes);
        }

        private void RecordPacketReceived(int bytes)
        {
            UpdatePacketStatsWindow();
            receivedPacketsInWindow++;
            receivedBytesInWindow += Mathf.Max(0, bytes);
            totalPacketsReceived++;
            totalBytesReceived += Mathf.Max(0, bytes);
        }

        private void UpdatePacketStatsWindow()
        {
            float now = Time.realtimeSinceStartup;
            if (packetStatsWindowStart <= 0f)
            {
                packetStatsWindowStart = now;
                return;
            }

            if (now - packetStatsWindowStart < 1f)
                return;

            packetsSentPerSecond = sentPacketsInWindow;
            packetsReceivedPerSecond = receivedPacketsInWindow;
            bytesSentPerSecond = sentBytesInWindow;
            bytesReceivedPerSecond = receivedBytesInWindow;

            sentPacketsInWindow = 0;
            receivedPacketsInWindow = 0;
            sentBytesInWindow = 0;
            receivedBytesInWindow = 0;
            packetStatsWindowStart = now;
        }

        private void SetLastNetworkError(string message)
        {
            lastNetworkError = message ?? string.Empty;
            lastNetworkErrorAt = Time.realtimeSinceStartup;
            NetworkDiagnostics.RecordError("P2P", lastNetworkError);
        }

        /// <summary>
        /// Broadcast a binary message to all lobby members except self
        /// </summary>
        public void BroadcastBinary(byte[] data, EP2PSend sendType = EP2PSend.k_EP2PSendReliable, CSteamID? except = null)
        {
            var lobbyID = SteamLobbyManager.Instance?.CurrentLobbyID ?? CSteamID.Nil;
            if (lobbyID == CSteamID.Nil) return;

            CSteamID myID = SteamUser.GetSteamID();
            int memberCount = SteamMatchmaking.GetNumLobbyMembers(lobbyID);

            for (int i = 0; i < memberCount; i++)
            {
                CSteamID memberID = SteamMatchmaking.GetLobbyMemberByIndex(lobbyID, i);
                if (memberID != myID && memberID != except)
                {
                    SendBinary(memberID, data, sendType);
                }
            }
        }

        /// <summary>
        /// Send binary message to the lobby host
        /// </summary>
        public bool SendToHost(byte[] data, EP2PSend sendType = EP2PSend.k_EP2PSendReliable)
        {
            var lobbyID = SteamLobbyManager.Instance?.CurrentLobbyID ?? CSteamID.Nil;
            if (lobbyID == CSteamID.Nil) return false;

            CSteamID hostID = SteamMatchmaking.GetLobbyOwner(lobbyID);
            return SendBinary(hostID, data, sendType);
        }

        /// <summary>
        /// Legacy: Send a string P2P message (for backwards compatibility)
        /// </summary>
        public bool SendP2PMessage(CSteamID recipientID, string message, EP2PSend sendType = EP2PSend.k_EP2PSendReliable, int channel = 0)
        {
            byte[] data = Encoding.UTF8.GetBytes(message);
            return SendBinary(recipientID, data, sendType, channel);
        }

        internal bool SendRawDatagram(CSteamID recipientID, byte[] data)
        {
            return SendBinaryImmediate(recipientID, data, EP2PSend.k_EP2PSendUnreliable, 0);
        }

        internal bool SendNativeReliableFromTransport(CSteamID recipientID, byte[] data, int lane)
        {
            if (!SteamManager.Initialized || recipientID == CSteamID.Nil || data == null || data.Length == 0)
                return false;

            return SendReliableWithBackpressure(recipientID, data, lane);
        }

        internal void DispatchReassembledMessage(CSteamID senderID, byte[] data, int lane)
        {
            if (data == null || data.Length == 0)
                return;

            if (lane == 0)
            {
                if (!SteamLobbyManager.Instance.IsPeerVersionValidated(senderID))
                {
                    CoopMod.Logger.LogWarning(
                        $"[P2P] Dropped RNET application message from " +
                        $"unvalidated lobby member {senderID}");
                    return;
                }
                DispatchApplicationMessage(senderID, data, data.Length);
                return;
            }

            if (lane != Multiplayer.LobbyChatSync.CHAT_CHANNEL ||
                data.Length > MaxLobbyLaneMessageBytes ||
                !IsCurrentLobbyMember(senderID) ||
                !SteamLobbyManager.Instance.IsPeerVersionValidated(senderID))
            {
                CoopMod.Logger.LogWarning(
                    $"[P2P] Dropped invalid RNET lobby-lane message from {senderID} (lane={lane}, bytes={data.Length})");
                return;
            }

            DispatchLobbyLaneMessage(senderID, data, data.Length);
        }

        internal void DispatchNativeLobbyLaneMessage(CSteamID senderID, byte[] data, int length)
        {
            if (data == null || length <= 0 || length > data.Length || length > MaxLobbyLaneMessageBytes)
                return;

            RecordPacketReceived(length);
            lastPacketRealtimeFrom[senderID.m_SteamID] = Time.realtimeSinceStartup;

            if (!IsCurrentLobbyMember(senderID) ||
                !SteamLobbyManager.Instance.IsPeerVersionValidated(senderID))
            {
                CoopMod.Logger.LogWarning($"[P2P] Dropped native lobby-lane message from non-member {senderID}");
                return;
            }

            if (Reliable != null && !Reliable.AcceptNativeInbound(senderID))
                return;

            DispatchLobbyLaneMessage(senderID, data, length);
        }

        internal float GetLastPacketRealtimeFrom(CSteamID peer)
        {
            return lastPacketRealtimeFrom.TryGetValue(peer.m_SteamID, out float timestamp)
                ? timestamp
                : 0f;
        }

        internal bool IsCurrentLobbyMember(CSteamID peer)
        {
            if (peer == CSteamID.Nil)
                return false;

            SteamLobbyManager lobbyManager = SteamLobbyManager.Instance;
            CSteamID lobbyID = lobbyManager?.CurrentLobbyID ?? CSteamID.Nil;
            if (lobbyID == CSteamID.Nil || lobbyManager?.IsInLobby != true)
                return false;

            int memberCount = SteamMatchmaking.GetNumLobbyMembers(lobbyID);
            for (int i = 0; i < memberCount; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(lobbyID, i) == peer)
                    return true;
            }

            return false;
        }

        public void BeginLobbySession(CSteamID lobbyID)
        {
            EnableBackgroundNetworkServicing();
            queuedReliablePackets.Clear();
            lastPacketRealtimeFrom.Clear();
            pendingLobbyInfoHost = CSteamID.Nil;
            preLobbyAdmissionUntil.Clear();
            loggedUnvalidatedPeerPackets.Clear();
            Reliable?.BeginLobby(lobbyID);
        }

        public void OnPeerEnteredLobby(CSteamID peer)
        {
            if (peer != CSteamID.Nil && peer != SteamUser.GetSteamID())
            {
                preLobbyAdmissionUntil.Remove(peer.m_SteamID);
                Reliable?.OnPeerEntered(peer);
            }
        }

        public void OnPeerLeftLobby(CSteamID peer)
        {
            Multiplayer.SaveTransferManager.OnPeerUnavailable(
                peer,
                "The host left during save transfer");
            Multiplayer.SpawnSync.Instance?.OnPeerLeftLobby(peer);
            Multiplayer.CraftSync.Instance?.OnPeerLeftLobby(peer);
            Multiplayer.InventorySync.Instance?.OnPeerLeftLobby(peer);
            Reliable?.RemovePeer(peer);
            lastPacketRealtimeFrom.Remove(peer.m_SteamID);
            RemoveQueuedReliablePacketsFor(peer);
            if (SteamManager.Initialized && peer != CSteamID.Nil)
                SteamNetworking.CloseP2PSessionWithUser(peer);
            sessionPeers.Remove(peer.m_SteamID);
            loggedUnvalidatedPeerPackets.Remove(peer.m_SteamID);
        }

        public void ClearLobbySession()
        {
            if (SteamManager.Initialized)
            {
                foreach (ulong peerID in sessionPeers)
                    SteamNetworking.CloseP2PSessionWithUser(new CSteamID(peerID));
            }
            sessionPeers.Clear();

            Multiplayer.SaveTransferManager.Reset();
            Reliable?.Clear();
            lastPacketRealtimeFrom.Clear();
            queuedReliablePackets.Clear();
            pendingLobbyInfoHost = CSteamID.Nil;
            preLobbyAdmissionUntil.Clear();
            loggedUnvalidatedPeerPackets.Clear();
            RestoreBackgroundNetworkServicing();
        }

        private void EnableBackgroundNetworkServicing()
        {
            if (ownsRunInBackground)
                return;

            previousRunInBackground = Application.runInBackground;
            ownsRunInBackground = true;
            Application.runInBackground = true;
            CoopMod.Logger.LogInfo(
                $"[P2P] Background execution enabled for lobby network servicing " +
                $"(previous={previousRunInBackground})");
        }

        private void RestoreBackgroundNetworkServicing()
        {
            if (!ownsRunInBackground)
                return;

            Application.runInBackground = previousRunInBackground;
            ownsRunInBackground = false;
        }

        private void RemoveQueuedReliablePacketsFor(CSteamID peer)
        {
            int count = queuedReliablePackets.Count;
            for (int i = 0; i < count; i++)
            {
                QueuedReliablePacket packet = queuedReliablePackets.Dequeue();
                if (packet.Recipient != peer)
                    queuedReliablePackets.Enqueue(packet);
            }
        }

        #endregion

        #region Message Handling

        /// <summary>
        /// Handle incoming binary packet
        /// </summary>
        private void HandleIncomingPacket(CSteamID senderID, byte[] data, int length)
        {
            if (length < 1) return;

            bool isUnvalidatedHostMember =
                SteamLobbyManager.Instance.IsHost &&
                !SteamLobbyManager.Instance.IsPeerVersionValidated(senderID) &&
                IsCurrentLobbyMember(senderID);
            if (isUnvalidatedHostMember)
            {
                if (HandleVersionHelloPacket(senderID, data, length))
                    return;

                if (loggedUnvalidatedPeerPackets.Add(senderID.m_SteamID))
                {
                    CoopMod.Logger.LogWarning(
                        $"[P2P] Dropping traffic from unvalidated lobby member " +
                        senderID);
                }
                return;
            }

            if (Reliable != null && Reliable.HandleRawIncoming(senderID, data, length))
                return;

            bool isLobbyMember = IsCurrentLobbyMember(senderID);
            if (!isLobbyMember)
            {
                if (!IsAllowedPreLobbyPacket(senderID, data, length))
                {
                    CoopMod.Logger.LogWarning($"[P2P] Dropped application packet from non-member {senderID}");
                    return;
                }
            }
            else if (!IsKnownNativeUnreliablePacket(data, length) &&
                     Reliable != null &&
                     !Reliable.AcceptNativeInbound(senderID))
            {
                return;
            }

            DispatchApplicationMessage(senderID, data, length);
        }

        private static bool HandleVersionHelloPacket(
            CSteamID senderID,
            byte[] data,
            int length)
        {
            if (data == null || length < 2 || data[0] != (byte)Op.Hello)
                return false;

            try
            {
                var reader = new MsgReader(data, length);
                string version = reader.ReadString();
                if (reader.Remaining != 0 || version.Length > 64)
                {
                    CoopMod.Logger.LogWarning(
                        $"[P2P] Dropped malformed version hello from {senderID}");
                    return true;
                }

                SteamLobbyManager.Instance.HandleVersionHello(senderID, version);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[P2P] Dropped malformed version hello from {senderID}: " +
                    ex.Message);
            }

            return true;
        }

        private void DispatchApplicationMessage(CSteamID senderID, byte[] data, int length)
        {
            if (data == null || length < 1 || length > data.Length)
                return;

            long startedAt = Stopwatch.GetTimestamp();
            try
            {
                // Check if this is a binary protocol message
                if (BinaryProtocolExtensions.IsBinaryMessage(data, length))
                {
                    HandleBinaryMessage(senderID, data, length);
                }
                else
                {
                    // Legacy string message (for backwards compatibility during transition)
                    string message = Encoding.UTF8.GetString(data, 0, length);
                    HandleLegacyStringMessage(senderID, message);
                }
            }
            finally
            {
                RecordIncomingApplicationMessage(
                    data[0],
                    length,
                    Stopwatch.GetTimestamp() - startedAt);
            }
        }

        private void RecordIncomingWirePacket(byte[] data, int length)
        {
            receiveWirePackets++;
            receiveWireBytes += Mathf.Max(0, length);
            if (data == null || length <= 0)
                return;

            if (data[0] == 0xFE)
                receiveRnetDataFrames++;
            else if (data[0] == 0xFF)
                receiveRnetAckFrames++;
        }

        private void RecordIncomingApplicationMessage(byte op, int length, long elapsedTicks)
        {
            receivedAppOps[op]++;
            receivedAppOpBytes[op] += Mathf.Max(0, length);
            receivedAppOpTicks[op] += elapsedTicks;
        }

        private void MaybeReportReceiveTelemetry()
        {
            float now = Time.realtimeSinceStartup;
            if (receiveTelemetryWindowStart <= 0f)
            {
                receiveTelemetryWindowStart = now;
                return;
            }
            if (now - receiveTelemetryWindowStart < ReceiveTelemetryIntervalSeconds)
                return;

            if (receiveBudgetLimitedFrames > 0)
            {
                var details = new StringBuilder();
                var selected = new bool[256];
                for (int rank = 0; rank < 5; rank++)
                {
                    int best = -1;
                    long bestTicks = -1L;
                    for (int op = 0; op < 256; op++)
                    {
                        if (!selected[op] &&
                            receivedAppOps[op] > 0 &&
                            receivedAppOpTicks[op] > bestTicks)
                        {
                            best = op;
                            bestTicks = receivedAppOpTicks[op];
                        }
                    }

                    if (best < 0 || receivedAppOps[best] == 0)
                        break;

                    selected[best] = true;
                    if (details.Length > 0)
                        details.Append(", ");
                    details
                        .Append(DescribeReceiveOp((byte)best))
                        .Append('=')
                        .Append(receivedAppOps[best])
                        .Append('/')
                        .Append((receivedAppOpTicks[best] * 1000.0 / Stopwatch.Frequency).ToString("F1"))
                        .Append("ms/")
                        .Append(receivedAppOpBytes[best])
                        .Append('B');
                }

                CoopMod.Logger.LogWarning(
                    $"[P2P] Receive backlog: budget_frames={receiveBudgetLimitedFrames}, " +
                    $"max_batch={maxPacketsProcessedInFrame}, wire={receiveWirePackets}/{receiveWireBytes}B, " +
                    $"rnet_data={receiveRnetDataFrames}, rnet_ack={receiveRnetAckFrames}, " +
                    $"top_app=[{details}]");
            }

            Array.Clear(receivedAppOps, 0, receivedAppOps.Length);
            Array.Clear(receivedAppOpTicks, 0, receivedAppOpTicks.Length);
            Array.Clear(receivedAppOpBytes, 0, receivedAppOpBytes.Length);
            receiveWirePackets = 0;
            receiveWireBytes = 0L;
            receiveRnetDataFrames = 0;
            receiveRnetAckFrames = 0;
            receiveBudgetLimitedFrames = 0;
            maxPacketsProcessedInFrame = 0;
            receiveTelemetryWindowStart = now;
        }

        private static string DescribeReceiveOp(byte value)
        {
            return Enum.IsDefined(typeof(Op), value)
                ? ((Op)value).ToString()
                : $"0x{value:X2}";
        }

        private static bool IsKnownNativeUnreliablePacket(byte[] data, int length)
        {
            if (!BinaryProtocolExtensions.IsBinaryMessage(data, length))
                return false;

            // Steam does not expose the original send mode on receive. Keep this list aligned
            // with the opcodes this manager explicitly sends as native unreliable traffic.
            switch ((Op)data[0])
            {
                case Op.Ping:
                case Op.Pong:
                case Op.PlayerPosition:
                case Op.PlayerState:
                case Op.PlayerVisualSync:
                case Op.TimeSync:
                case Op.WorldEntryBarrier:
                case Op.WorkIndicatorSync:
                case Op.WGOTransformSync:
                case Op.NpcVisualSync:
                    return true;
                case Op.SpawnSync:
                    return Multiplayer.SpawnSync.IsBuildingPreviewWirePacket(
                        data,
                        length);
                default:
                    return false;
            }
        }

        private bool IsAllowedPreLobbyPacket(CSteamID senderID, byte[] data, int length)
        {
            if (!BinaryProtocolExtensions.IsBinaryMessage(data, length))
                return false;

            Op op = (Op)data[0];
            if (op == Op.LobbyInfo)
                return senderID == pendingLobbyInfoHost;

            if (!IsPreLobbyPeerAllowed(senderID))
                return false;

            return op == Op.Ping || op == Op.Pong || op == Op.Invite || op == Op.LobbyRequest;
        }

        private bool IsPreLobbyPeerAllowed(CSteamID peer)
        {
            if (peer == CSteamID.Nil)
                return false;
            if (peer == pendingLobbyInfoHost || IsImmediateFriend(peer))
                return true;

            if (!preLobbyAdmissionUntil.TryGetValue(peer.m_SteamID, out float expiresAt))
                return false;
            if (Time.realtimeSinceStartup <= expiresAt)
                return true;

            preLobbyAdmissionUntil.Remove(peer.m_SteamID);
            return false;
        }

        private static bool IsImmediateFriend(CSteamID peer)
        {
            int friendCount = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < friendCount; i++)
            {
                if (SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate) == peer)
                    return true;
            }
            return false;
        }

        private void AllowPreLobbyPeer(CSteamID peer)
        {
            if (peer != CSteamID.Nil)
                preLobbyAdmissionUntil[peer.m_SteamID] = Time.realtimeSinceStartup + PreLobbyAdmissionSeconds;
        }

        private void DispatchLobbyLaneMessage(CSteamID senderID, byte[] data, int length)
        {
            try
            {
                string message = StrictUtf8.GetString(data, 0, length);
                string senderName = SteamFriends.GetFriendPersonaName(senderID);
                CoopMod.Logger.LogInfo($"[P2P] Lobby lane from {senderName} ({length} bytes)");

                if (!Multiplayer.LobbyReadySystem.HandleReadyMessage(senderID, message))
                    Multiplayer.LobbyChatSync.HandleChatMessage(senderID, message);
            }
            catch (DecoderFallbackException)
            {
                CoopMod.Logger.LogWarning($"[P2P] Dropped invalid UTF-8 lobby-lane message from {senderID}");
            }
        }

        /// <summary>
        /// Handle binary protocol message
        /// </summary>
        private void HandleBinaryMessage(CSteamID senderID, byte[] data, int length)
        {
            var reader = new MsgReader(data, length);

            switch (reader.Op)
            {
                case Op.Heartbeat:
                    break;

                case Op.Ping:
                    HandlePing(senderID, ref reader);
                    break;

                case Op.Pong:
                    HandlePong(senderID, ref reader);
                    break;

                case Op.Invite:
                    HandleInviteMessage(senderID, ref reader);
                    break;

                case Op.GameStart:
                    HandleGameStartMessage(senderID, ref reader);
                    break;

                case Op.GameLoaded:
                    OnClientGameLoaded?.Invoke(senderID);
                    break;

                case Op.ReadyToPlay:
                    OnPlayerReadyToPlay?.Invoke(senderID);
                    break;

                case Op.WorldEntryBarrier:
                    if (reader.Remaining < 1)
                        break;
                    OnWorldEntryBarrier?.Invoke(
                        senderID,
                        (WorldEntryBarrierPhase)reader.ReadByte());
                    break;

                case Op.JoinerProfileCommit:
                    Multiplayer.JoinerProfileManager.Instance?.HandleHostSaveCommit(
                        senderID,
                        ref reader);
                    break;

                case Op.PlayerPosition:
                    HandlePositionMessage(senderID, ref reader);
                    break;

                case Op.PlayerState:
                    HandlePlayerStateMessage(senderID, ref reader);
                    break;

                case Op.PlayerAnimation:
                    HandlePlayerAnimationMessage(senderID, ref reader);
                    break;

                case Op.PlayerParity:
                    HandlePlayerParityMessage(senderID, ref reader);
                    break;

                case Op.PlayerVisualSync:
                    HandlePlayerVisualSyncMessage(senderID, ref reader);
                    break;

                case Op.TimeSync:
                    HandleTimeSyncMessage(senderID, ref reader);
                    break;

                case Op.WGODestroy:
                    HandleWGODestroyMessage(senderID, ref reader);
                    break;

                case Op.WeatherSync:
                    HandleWeatherSyncMessage(senderID, ref reader);
                    break;

                case Op.InventorySync:
                    HandleInventorySyncMessage(senderID, ref reader);
                    break;

                case Op.WGOStateSync:
                    HandleWGOStateSyncMessage(senderID, ref reader);
                    break;

                case Op.CraftSync:
                    HandleCraftSyncMessage(senderID, ref reader);
                    break;

                case Op.TechSync:
                    HandleTechSyncMessage(senderID, ref reader);
                    break;

                case Op.QuestSync:
                    HandleQuestSyncMessage(senderID, ref reader);
                    break;

                case Op.ZoneNavSync:
                    HandleZoneNavSyncMessage(senderID, ref reader);
                    break;

                case Op.CombatSync:
                    HandleCombatSyncMessage(senderID, ref reader);
                    break;

                case Op.PlayerParamSync:
                    HandlePlayerParamSyncMessage(senderID, ref reader);
                    break;

                case Op.SpawnSync:
                    HandleSpawnSyncMessage(senderID, ref reader);
                    break;

                case Op.DungeonSync:
                    HandleDungeonSyncMessage(senderID, ref reader);
                    break;

                case Op.WorkerSync:
                    HandleWorkerSyncMessage(senderID, ref reader);
                    break;

                case Op.FishingSync:
                    HandleFishingSyncMessage(senderID, ref reader);
                    break;

                case Op.DLCSync:
                    HandleDLCSyncMessage(senderID, ref reader);
                    break;

                case Op.WGOTransformSync:
                    HandleWGOTransformSyncMessage(senderID, ref reader);
                    break;

                case Op.InteractionRequest:
                    HandleInteractionRequestMessage(senderID, ref reader);
                    break;

                case Op.InteractionZeroHp:
                    HandleInteractionZeroHpMessage(senderID, ref reader);
                    break;

                case Op.NpcVisualSync:
                    HandleNpcVisualSyncMessage(senderID, ref reader);
                    break;

                case Op.WorkIndicatorSync:
                    HandleWorkIndicatorSyncMessage(senderID, ref reader);
                    break;

                case Op.SkipIntro:
                    HandleSkipIntroMessage(senderID, ref reader);
                    break;

                case Op.IntroSkipState:
                    HandleIntroSkipStateMessage(senderID, ref reader);
                    break;

                case Op.DialogueStart:
                case Op.DialogueAdvance:
                case Op.DialogueEnd:
                case Op.DialogueChoice:
                case Op.DialogueBubble:
                case Op.DialogueOptions:
                case Op.DialogueHover:
                    OnDialogueMessage?.Invoke(senderID, reader.Op, reader);
                    break;

                case Op.SaveRequest:
                case Op.SaveStart:
                case Op.SaveChunk:
                case Op.SaveEnd:
                case Op.SaveAck:
                    Multiplayer.SaveTransferManager.HandleBinaryMessage(senderID, reader.Op, data, length);
                    break;

                case Op.SleepSync:
                    Multiplayer.GameTimeSync.Instance?.HandleSleepSyncMessage(senderID, ref reader);
                    break;

                case Op.PlayerTrade:
                    Multiplayer.PlayerTradeManager.Instance?.HandleNetworkMessage(senderID, ref reader);
                    break;

                case Op.ChatMessage:
                    HandleChatMessage(senderID, ref reader);
                    break;

                case Op.PlayerCosmetics:
                    HandleCosmeticsMessage(senderID, ref reader);
                    break;

                case Op.CosmeticsRequest:
                    OnCosmeticsRequested?.Invoke(senderID);
                    break;

                case Op.LobbyRequest:
                    HandleLobbyRequest(senderID);
                    break;

                case Op.LobbyInfo:
                    HandleLobbyInfo(senderID, ref reader);
                    break;

                case Op.CutsceneSync:
                    HandleCutsceneSyncMessage(senderID, ref reader);
                    break;

                case Op.CutsceneWalkTo:
                    HandleCutsceneWalkToMessage(senderID, ref reader);
                    break;

                case Op.NpcInteraction:
                    HandleNpcInteractionMessage(senderID, ref reader);
                    break;

                case Op.NpcInteractionComplete:
                    HandleNpcInteractionCompleteMessage(senderID, ref reader);
                    break;

                case Op.CutsceneComplete:
                    HandleCutsceneCompleteMessage(senderID, ref reader);
                    break;

                case Op.CutsceneCamera:
                    HandleCutsceneCameraMessage(senderID, ref reader);
                    break;

                case Op.HostSaveListRequest:
                    CoopMod.Logger.LogInfo($"[P2P] Received HostSaveListRequest from {SteamFriends.GetFriendPersonaName(senderID)}");
                    OnHostSaveListRequested?.Invoke(senderID);
                    break;

                case Op.HostSaveList:
                    HandleHostSaveListMessage(senderID, ref reader);
                    break;

                case Op.HostSaveSelected:
                    HandleHostSaveSelectedMessage(senderID, ref reader);
                    break;

                case Op.LobbyKick:
                    HandleLobbyKickMessage(senderID, ref reader);
                    break;

                default:
                    CoopMod.Logger.LogWarning($"[P2P] Unknown binary opcode: {reader.Op}");
                    break;
            }
        }

        /// <summary>
        /// Handle legacy string messages (backwards compatibility during transition)
        /// </summary>
        private void HandleLegacyStringMessage(CSteamID senderID, string message)
        {
            // Route to SaveTransferManager if it's a save message
            if (Multiplayer.SaveTransferManager.IsSaveTransferMessage(message))
            {
                Multiplayer.SaveTransferManager.HandleMessage(senderID, message);
                return;
            }

            CoopMod.Logger.LogWarning($"[P2P] Legacy string message (migrate to binary): {message.Substring(0, Math.Min(50, message.Length))}...");
        }

        #endregion

        #region Invite Messages

        /// <summary>
        /// Send a lobby invite notification to a friend
        /// </summary>
        public bool SendInviteNotification(CSteamID friendID, CSteamID lobbyID)
        {
            if (!SteamManager.Initialized) return false;
            if (friendID == CSteamID.Nil || lobbyID == CSteamID.Nil) return false;

            string inviterName = SteamFriends.GetPersonaName();
            AllowPreLobbyPeer(friendID);

            using (var w = new MsgWriter(Op.Invite))
            {
                w.Write(lobbyID.m_SteamID);
                w.Write(inviterName);
                w.Write(ModConfig.GetDLCRequirementsString());
                w.Write(PluginInfo.PLUGIN_VERSION ?? string.Empty);
                CoopMod.Logger.LogInfo($"[P2P] Sending invite to {SteamFriends.GetFriendPersonaName(friendID)} for lobby {lobbyID}");
                return SendBinary(friendID, w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
        }

        private void HandleInviteMessage(CSteamID senderID, ref MsgReader reader)
        {
            try
            {
                ulong lobbyIDValue = reader.ReadUInt64();
                string inviterName = reader.ReadString();
                string dlcRequirements = reader.Remaining > 0 ? reader.ReadString() : "";
                string modVersion = reader.Remaining > 0 ? reader.ReadString() : "";
                CSteamID lobbyID = new CSteamID(lobbyIDValue);

                CoopMod.Logger.LogInfo($"[P2P] Received invite from '{inviterName}' for lobby {lobbyID}");
                if (!SteamLobbyManager.TryValidateHostModVersion(
                        modVersion,
                        out string versionRejectMessage))
                {
                    CoopMod.Logger.LogWarning(
                        $"[P2P] Invite rejected by mod version: " +
                        versionRejectMessage.Replace('\n', ' '));
                    GUIElements.me?.dialog?.OpenOK(versionRejectMessage);
                    return;
                }
                ShowInviteNotification(senderID, lobbyID, inviterName, dlcRequirements);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[P2P] Error parsing invite: {ex.Message}");
            }
        }

        /// <summary>
        /// Show the invite notification via JoinGameGUI
        /// </summary>
        private void ShowInviteNotification(CSteamID senderID, CSteamID lobbyID, string inviterName, string dlcRequirements)
        {
            if (UI.JoinGameGUI.Instance == null)
            {
                UI.JoinGameGUI.Create();
            }

            if (UI.JoinGameGUI.Instance != null && UI.JoinGameGUI.Instance.is_shown)
            {
                UI.JoinGameGUI.Instance.ShowInvitation(
                    inviterName,
                    () => AcceptInviteIfDLCCompatible(lobbyID, dlcRequirements),
                    () => CoopMod.Logger.LogInfo($"[P2P] Declined invite from {inviterName}")
                );
            }
            else if (UI.JoinGameGUI.Instance != null)
            {
                UI.JoinGameGUI.Instance.Open();
                var runner = UnityEngine.Object.FindObjectOfType<MonoBehaviour>();
                if (runner != null)
                {
                    runner.StartCoroutine(ShowInvitationAfterDelay(inviterName, lobbyID, dlcRequirements));
                }
            }
        }

        private System.Collections.IEnumerator ShowInvitationAfterDelay(string inviterName, CSteamID lobbyID, string dlcRequirements)
        {
            yield return new WaitForSeconds(0.3f);
            if (UI.JoinGameGUI.Instance != null && UI.JoinGameGUI.Instance.is_shown)
            {
                UI.JoinGameGUI.Instance.ShowInvitation(
                    inviterName,
                    () => AcceptInviteIfDLCCompatible(lobbyID, dlcRequirements),
                    () => { }
                );
            }
        }

        private void AcceptInviteIfDLCCompatible(CSteamID lobbyID, string dlcRequirements)
        {
            if (!SteamLobbyManager.TryValidateDLCRequirements(dlcRequirements, out string rejectMessage))
            {
                CoopMod.Logger.LogWarning($"[P2P] Invite rejected by DLC requirements: {rejectMessage}");
                if (GUIElements.me != null && GUIElements.me.dialog != null)
                {
                    GUIElements.me.dialog.OpenOK(rejectMessage);
                }
                return;
            }

            SteamLobbyManager.Instance.JoinLobby(lobbyID);
        }

        #endregion

        #region Lobby Request (Steam Rich Presence Join)

        /// <summary>
        /// Request lobby info from host (used when joining via Steam Rich Presence)
        /// </summary>
        public void RequestLobbyFromHost(CSteamID hostID)
        {
            if (!SteamManager.Initialized) return;

            pendingLobbyInfoHost = hostID;
            AllowPreLobbyPeer(hostID);

            using (var w = new MsgWriter(Op.LobbyRequest))
            {
                // No payload needed - just asking for lobby info
                CoopMod.Logger.LogInfo($"[P2P] Requesting lobby info from host {hostID}");
                if (!SendBinary(hostID, w.ToArray(), EP2PSend.k_EP2PSendReliable))
                    pendingLobbyInfoHost = CSteamID.Nil;
            }
        }

        /// <summary>
        /// Host responds to lobby request by sending their current lobby ID
        /// </summary>
        private void HandleLobbyRequest(CSteamID senderID)
        {
            CoopMod.Logger.LogInfo($"[P2P] Received lobby request from {SteamFriends.GetFriendPersonaName(senderID)}");

            var lobbyID = SteamLobbyManager.Instance?.CurrentLobbyID ?? CSteamID.Nil;
            if (lobbyID == CSteamID.Nil)
            {
                CoopMod.Logger.LogWarning("[P2P] Cannot respond to lobby request - not hosting a lobby");
                return;
            }

            if (SteamLobbyManager.Instance?.AllowsRichPresenceJoin == false)
            {
                CoopMod.Logger.LogWarning("[P2P] Ignoring lobby request because current session is private");
                return;
            }

            string hostName = SteamFriends.GetPersonaName();

            using (var w = new MsgWriter(Op.LobbyInfo))
            {
                w.Write(lobbyID.m_SteamID);
                w.Write(hostName);
                w.Write(ModConfig.GetDLCRequirementsString());
                w.Write(PluginInfo.PLUGIN_VERSION ?? string.Empty);
                CoopMod.Logger.LogInfo($"[P2P] Sending lobby info {lobbyID} to {senderID}");
                SendBinary(senderID, w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
        }

        /// <summary>
        /// Client receives lobby info from host and can now join
        /// </summary>
        private void HandleLobbyInfo(CSteamID senderID, ref MsgReader reader)
        {
            if (senderID != pendingLobbyInfoHost)
                return;

            pendingLobbyInfoHost = CSteamID.Nil;
            try
            {
                ulong lobbyIDValue = reader.ReadUInt64();
                string hostName = reader.ReadString();
                string dlcRequirements = reader.Remaining > 0 ? reader.ReadString() : "";
                string modVersion = reader.Remaining > 0 ? reader.ReadString() : "";
                CSteamID lobbyID = new CSteamID(lobbyIDValue);

                CoopMod.Logger.LogInfo($"[P2P] Received lobby info from '{hostName}': lobby {lobbyID}");

                if (!SteamLobbyManager.TryValidateDLCRequirements(dlcRequirements, out string rejectMessage))
                {
                    CoopMod.Logger.LogWarning($"[P2P] Lobby info rejected by DLC requirements: {rejectMessage}");
                    if (GUIElements.me != null && GUIElements.me.dialog != null)
                    {
                        GUIElements.me.dialog.OpenOK(rejectMessage);
                    }
                    return;
                }

                if (!SteamLobbyManager.TryValidateHostModVersion(
                        modVersion,
                        out string versionRejectMessage))
                {
                    CoopMod.Logger.LogWarning(
                        $"[P2P] Lobby info rejected by mod version: " +
                        versionRejectMessage.Replace('\n', ' '));
                    GUIElements.me?.dialog?.OpenOK(versionRejectMessage);
                    return;
                }

                // Guard: don't call JoinLobby if we're already joining or already in a lobby
                var lobbyMgr = SteamLobbyManager.Instance;
                if (lobbyMgr != null && (lobbyMgr.IsInLobby || lobbyMgr.IsJoining))
                {
                    CoopMod.Logger.LogWarning($"[P2P] Ignoring duplicate lobby info - already {(lobbyMgr.IsInLobby ? "in lobby" : "joining")}");
                    return;
                }

                // Now join the lobby
                SteamLobbyManager.Instance?.JoinLobby(lobbyID);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[P2P] Error parsing lobby info: {ex.Message}");
            }
        }

        #endregion

        #region Ping/Pong

        /// <summary>
        /// Send a ping to a specific Steam user to measure round-trip latency.
        /// Result arrives asynchronously via OnPingResult event.
        /// </summary>
        public void SendPing(CSteamID target)
        {
            if (!SteamManager.Initialized) return;

            float sendTime = Time.realtimeSinceStartup;
            _pendingPings[target.m_SteamID] = sendTime;

            using (var w = new MsgWriter(Op.Ping))
            {
                w.Write(sendTime);
                SendBinary(target, w.ToArray(), EP2PSend.k_EP2PSendUnreliable);
            }
        }

        /// <summary>
        /// Received a Ping — echo it back as Pong with the same timestamp
        /// </summary>
        private void HandlePing(CSteamID senderID, ref MsgReader reader)
        {
            try
            {
                float senderTimestamp = reader.ReadFloat();
                using (var w = new MsgWriter(Op.Pong))
                {
                    w.Write(senderTimestamp);
                    SendBinary(senderID, w.ToArray(), EP2PSend.k_EP2PSendUnreliable);
                }
            }
            catch { }
        }

        /// <summary>
        /// Received a Pong — calculate RTT and fire event
        /// </summary>
        private void HandlePong(CSteamID senderID, ref MsgReader reader)
        {
            try
            {
                float originalTimestamp = reader.ReadFloat();
                float now = Time.realtimeSinceStartup;

                // Verify this matches a pending ping
                if (_pendingPings.ContainsKey(senderID.m_SteamID))
                {
                    float rtt = (now - originalTimestamp) * 1000f; // Convert to ms
                    int pingMs = Mathf.RoundToInt(rtt);
                    if (pingMs < 0) pingMs = 0;
                    if (pingMs > 9999) pingMs = 9999;

                    _pendingPings.Remove(senderID.m_SteamID);
                    CoopMod.Logger.LogDebug($"[P2P] Ping to {SteamFriends.GetFriendPersonaName(senderID)}: {pingMs}ms");
                    OnPingResult?.Invoke(senderID, pingMs);
                }
            }
            catch { }
        }

        #endregion

        #region Game State Messages

        /// <summary>
        /// Host broadcasts game start to all lobby members
        /// </summary>
        public void BroadcastGameStart(string saveSlotName)
        {
            if (!SteamManager.Initialized) return;

            using (var w = new MsgWriter(Op.GameStart))
            {
                w.Write(saveSlotName ?? "");
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }

            CoopMod.Logger.LogInfo($"[P2P] Broadcast GAME_START: {saveSlotName}");
        }

        private void HandleGameStartMessage(CSteamID senderID, ref MsgReader reader)
        {
            string saveSlotName = reader.ReadString();
            CoopMod.Logger.LogInfo($"[P2P] Received GAME_START from {SteamFriends.GetFriendPersonaName(senderID)}: {saveSlotName}");
            OnGameStartReceived?.Invoke(senderID, saveSlotName);
        }

        /// <summary>
        /// Client notifies host that game has loaded
        /// </summary>
        public void NotifyHostGameLoaded()
        {
            using (var w = new MsgWriter(Op.GameLoaded))
            {
                SendToHost(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
            CoopMod.Logger.LogInfo("[P2P] Sent GAME_LOADED to host");
        }

        /// <summary>
        /// Broadcast ready to play status
        /// </summary>
        public void BroadcastReadyToPlay()
        {
            using (var w = new MsgWriter(Op.ReadyToPlay))
            {
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
            CoopMod.Logger.LogInfo("[P2P] Broadcast READY_TO_PLAY");
        }

        /// <summary>
        /// Send a world-entry barrier phase outside the reliable bulk stream so
        /// initial world snapshots cannot delay it. Repetition handles ordinary
        /// datagram loss; the final release also gets a reliable fallback.
        /// </summary>
        public void BroadcastWorldEntryBarrier(
            WorldEntryBarrierPhase phase,
            bool reliableFallback = false)
        {
            using (var w = new MsgWriter(Op.WorldEntryBarrier, 2))
            {
                w.Write((byte)phase);
                byte[] message = w.ToArray();
                for (int i = 0; i < 3; i++)
                    BroadcastBinary(message, EP2PSend.k_EP2PSendUnreliable);
                if (reliableFallback)
                    BroadcastBinary(message, EP2PSend.k_EP2PSendReliable);
            }
        }

        public void SendWorldEntryPreparedToHost()
        {
            using (var w = new MsgWriter(Op.WorldEntryBarrier, 2))
            {
                w.Write((byte)WorldEntryBarrierPhase.Prepared);
                byte[] message = w.ToArray();
                for (int i = 0; i < 3; i++)
                    SendToHost(message, EP2PSend.k_EP2PSendUnreliable);
            }
        }

        #endregion

        #region Position Messages

        /// <summary>
        /// Send player position to all lobby members
        /// </summary>
        public void SendPosition(Vector3 position)
        {
            using (var w = new MsgWriter(Op.PlayerPosition, 16))
            {
                w.Write(position);
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendUnreliable);
            }
        }

        /// <summary>
        /// Send position with movement state, velocity, and animation sync
        /// </summary>
        public void SendPositionWithState(Vector3 position, Vector2 direction, bool isMoving, Vector2 velocity = default)
        {
            using (var w = new MsgWriter(Op.PlayerState, 48))
            {
                w.Write(position);       // 12 bytes (Vector3)
                w.Write(direction);      // 8 bytes (Vector2)
                w.Write(isMoving);       // 1 byte
                w.Write(velocity);       // 8 bytes (Vector2) — for GhostDriver extrapolation
                w.Write(Time.realtimeSinceStartup); // 4 bytes — monotonic, pause-safe timestamp
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendUnreliable);
            }
        }

        private void HandlePositionMessage(CSteamID senderID, ref MsgReader reader)
        {
            Vector3 position = reader.ReadVector3();
            OnRemotePlayerPosition?.Invoke(senderID, position, Time.realtimeSinceStartup);
        }

        private void HandlePlayerStateMessage(CSteamID senderID, ref MsgReader reader)
        {
            Vector3 position = reader.ReadVector3();
            Vector2 direction = reader.ReadVector2();
            bool isMoving = reader.ReadBool();

            // Read velocity if present (added in movement jitter fix)
            Vector2 velocity = Vector2.zero;
            if (reader.Remaining >= 8)
            {
                velocity = reader.ReadVector2();
            }

            // Read timestamp if present (after velocity)
            float timestamp = reader.Remaining >= 4 ? reader.ReadFloat() : Time.realtimeSinceStartup;

            // Preserve the legacy accessor, but also deliver velocity with the
            // sender-specific state event so multi-peer snapshots cannot consume
            // another player's most recently received velocity.
            _lastReceivedVelocity = velocity;

            OnRemotePlayerState?.Invoke(senderID, direction, isMoving, velocity);
            OnRemotePlayerPosition?.Invoke(senderID, position, timestamp);
        }

        // Last received velocity from PlayerState packet (for GhostDriver)
        private Vector2 _lastReceivedVelocity;
        public Vector2 LastReceivedVelocity => _lastReceivedVelocity;

        #endregion

        #region Animation State Messages

        /// <summary>
        /// Send animation state change to all lobby members.
        /// Used for tool use, attack, fishing, etc. — anything beyond walk/idle.
        /// </summary>
        public void SendAnimationState(int animState, int itemType)
        {
            using (var w = new MsgWriter(Op.PlayerAnimation, 12))
            {
                w.Write(animState);   // CharAnimState as int (Tool=1, Attack=3, etc.)
                w.Write(itemType);    // ItemDefinition.ItemType as int (Shovel=3, Hand=10, etc.)
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
        }

        private void HandlePlayerAnimationMessage(CSteamID senderID, ref MsgReader reader)
        {
            int animState = reader.ReadInt32();
            int itemType = reader.ReadInt32();
            OnRemotePlayerAnimation?.Invoke(senderID, animState, itemType);
        }

        /// <summary>
        /// Send a full local player parity snapshot to the lobby host.
        /// Clients use this so the host can apply their stat/buff/action state and
        /// rebroadcast the resulting host-canonical state.
        /// </summary>
        public void SendPlayerParityToHost(byte[] payload)
        {
            using (var w = new MsgWriter(Op.PlayerParity, (payload?.Length ?? 0) + 8))
            {
                w.WriteBytes(payload ?? new byte[0]);
                SendToHost(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
        }

        /// <summary>
        /// Generic helpers to reduce broadcast/handle/send-to-host duplication.
        /// </summary>
        private void BroadcastSyncPayload(Op op, byte[] payload, CSteamID? except = null)
        {
            using (var w = new MsgWriter(op, (payload?.Length ?? 0) + 8))
            {
                w.WriteBytes(payload ?? new byte[0]);
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable, except);
            }
        }

        private void SendSyncPayloadToHost(Op op, byte[] payload)
        {
            using (var w = new MsgWriter(op, (payload?.Length ?? 0) + 8))
            {
                w.WriteBytes(payload ?? new byte[0]);
                SendToHost(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
        }

        private static void HandleSyncPayload(CSteamID senderID, ref MsgReader reader, Action<CSteamID, byte[]> handler)
        {
            byte[] payload = reader.ReadBytes();
            handler?.Invoke(senderID, payload);
        }

        /// <summary>
        /// Broadcast a full local player parity snapshot to all lobby members.
        /// </summary>
        public void BroadcastPlayerParity(byte[] payload, CSteamID? except = null)
            => BroadcastSyncPayload(Op.PlayerParity, payload, except);

        private void HandlePlayerParityMessage(CSteamID senderID, ref MsgReader reader)
            => HandleSyncPayload(senderID, ref reader, OnPlayerParityReceived);

        public void BroadcastPlayerVisualSync(byte[] payload, CSteamID? except = null)
        {
            using (var w = new MsgWriter(Op.PlayerVisualSync, (payload?.Length ?? 0) + 8))
            {
                w.WriteBytes(payload ?? new byte[0]);
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendUnreliable, except);
            }
        }

        private void HandlePlayerVisualSyncMessage(CSteamID senderID, ref MsgReader reader)
            => HandleSyncPayload(senderID, ref reader, OnPlayerVisualSyncReceived);

        #endregion

        #region Time Sync Messages

        /// <summary>
        /// Broadcast time sync (host only)
        /// </summary>
        public void BroadcastTimeSync(int day, float timeK)
        {
            using (var w = new MsgWriter(Op.TimeSync, 12))
            {
                w.Write(day);
                w.Write(timeK);
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendUnreliable);
            }
        }

        private void HandleTimeSyncMessage(CSteamID senderID, ref MsgReader reader)
        {
            int day = reader.ReadInt32();
            float timeK = reader.ReadFloat();
            OnTimeSyncReceived?.Invoke(senderID, day, timeK);
        }

        #endregion

        #region WGO Destruction Messages

        /// <summary>
        /// Send WGO destruction to host (client) or broadcast (host)
        /// </summary>
        public void SendWGODestroy(long uniqueId, bool toHost)
        {
            using (var w = new MsgWriter(Op.WGODestroy, 12))
            {
                w.Write(uniqueId);

                if (toHost)
                {
                    SendToHost(w.ToArray(), EP2PSend.k_EP2PSendReliable);
                }
                else
                {
                    BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable);
                }
            }
        }

        /// <summary>
        /// Broadcast WGO destruction to all except specified player
        /// </summary>
        public void BroadcastWGODestroy(long uniqueId, CSteamID? except = null)
        {
            using (var w = new MsgWriter(Op.WGODestroy, 12))
            {
                w.Write(uniqueId);
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable, except);
            }
        }

        private void HandleWGODestroyMessage(CSteamID senderID, ref MsgReader reader)
        {
            long uniqueId = reader.ReadInt64();
            OnWGODestroyReceived?.Invoke(senderID, uniqueId);
        }

        #endregion

        #region Weather Sync Messages

        /// <summary>
        /// Broadcast host weather timeline/state to all clients.
        /// </summary>
        public void BroadcastWeatherSync(byte[] payload)
            => BroadcastSyncPayload(Op.WeatherSync, payload);

        private void HandleWeatherSyncMessage(CSteamID senderID, ref MsgReader reader)
            => HandleSyncPayload(senderID, ref reader, OnWeatherSyncReceived);

        #endregion

        #region Inventory Sync Messages

        /// <summary>
        /// Send local inventory/drop changes to the lobby host.
        /// </summary>
        public void SendInventorySyncToHost(byte[] payload)
            => SendSyncPayloadToHost(Op.InventorySync, payload);

        /// <summary>
        /// Send a targeted inventory-operation decision back to its requesting peer.
        /// </summary>
        public void SendInventorySyncToPeer(CSteamID peer, byte[] payload)
        {
            using (var w = new MsgWriter(
                       Op.InventorySync,
                       (payload?.Length ?? 0) + 8))
            {
                w.WriteBytes(payload ?? new byte[0]);
                SendBinary(
                    peer,
                    w.ToArray(),
                    EP2PSend.k_EP2PSendReliable);
            }
        }

        /// <summary>
        /// Broadcast host-canonical inventory/drop state to all lobby members.
        /// </summary>
        public void BroadcastInventorySync(byte[] payload, CSteamID? except = null)
            => BroadcastSyncPayload(Op.InventorySync, payload, except);

        private void HandleInventorySyncMessage(CSteamID senderID, ref MsgReader reader)
            => HandleSyncPayload(senderID, ref reader, OnInventorySyncReceived);

        #endregion

        #region WGO State Sync Messages

        /// <summary>
        /// Send local WGO lifecycle/state changes to the lobby host.
        /// </summary>
        public void SendWGOStateSyncToHost(byte[] payload)
            => SendSyncPayloadToHost(Op.WGOStateSync, payload);

        /// <summary>
        /// Broadcast host-canonical WGO lifecycle/state changes to all lobby members.
        /// </summary>
        public void BroadcastWGOStateSync(byte[] payload, CSteamID? except = null)
            => BroadcastSyncPayload(Op.WGOStateSync, payload, except);

        private void HandleWGOStateSyncMessage(CSteamID senderID, ref MsgReader reader)
            => HandleSyncPayload(senderID, ref reader, OnWGOStateSyncReceived);

        #endregion

        #region Craft Sync Messages

        public void SendCraftSyncToHost(byte[] payload)
            => SendSyncPayloadToHost(Op.CraftSync, payload);

        public void BroadcastCraftSync(byte[] payload, CSteamID? except = null)
            => BroadcastSyncPayload(Op.CraftSync, payload, except);

        private void HandleCraftSyncMessage(CSteamID senderID, ref MsgReader reader)
            => HandleSyncPayload(senderID, ref reader, OnCraftSyncReceived);

        #endregion

        #region Tech Sync Messages

        public void SendTechSyncToHost(byte[] payload)
            => SendSyncPayloadToHost(Op.TechSync, payload);

        public void BroadcastTechSync(byte[] payload, CSteamID? except = null)
            => BroadcastSyncPayload(Op.TechSync, payload, except);

        public void BroadcastTechSyncUnlock(byte[] payload)
            => BroadcastSyncPayload(Op.TechSync, payload, null);

        private void HandleTechSyncMessage(CSteamID senderID, ref MsgReader reader)
            => HandleSyncPayload(senderID, ref reader, OnTechSyncReceived);

        #endregion

        #region Quest Sync Messages

        public void SendQuestSyncToHost(byte[] payload)
            => SendSyncPayloadToHost(Op.QuestSync, payload);

        public void BroadcastQuestSync(byte[] payload, CSteamID? except = null)
            => BroadcastSyncPayload(Op.QuestSync, payload, except);

        private void HandleQuestSyncMessage(CSteamID senderID, ref MsgReader reader)
            => HandleSyncPayload(senderID, ref reader, OnQuestSyncReceived);

        #endregion

        #region Zone Nav Sync Messages

        public void SendZoneNavSyncToHost(byte[] payload)
            => SendSyncPayloadToHost(Op.ZoneNavSync, payload);

        public void BroadcastZoneNavSync(byte[] payload, CSteamID? except = null)
            => BroadcastSyncPayload(Op.ZoneNavSync, payload, except);

        private void HandleZoneNavSyncMessage(CSteamID senderID, ref MsgReader reader)
            => HandleSyncPayload(senderID, ref reader, OnZoneNavSyncReceived);

        #endregion

        #region Combat Sync Messages

        public void SendCombatSyncToHost(byte[] payload)
            => SendSyncPayloadToHost(Op.CombatSync, payload);

        public void BroadcastCombatSync(byte[] payload, CSteamID? except = null)
            => BroadcastSyncPayload(Op.CombatSync, payload, except);

        private void HandleCombatSyncMessage(CSteamID senderID, ref MsgReader reader)
            => HandleSyncPayload(senderID, ref reader, OnCombatSyncReceived);

        #endregion

        #region Work Indicator Sync Messages

        public void BroadcastWorkIndicatorSync(
            byte[] payload,
            CSteamID? except = null,
            bool reliable = false)
        {
            using (var w = new MsgWriter(
                Op.WorkIndicatorSync,
                (payload?.Length ?? 0) + 8))
            {
                w.WriteBytes(payload ?? new byte[0]);
                BroadcastBinary(
                    w.ToArray(),
                    reliable
                        ? EP2PSend.k_EP2PSendReliable
                        : EP2PSend.k_EP2PSendUnreliable,
                    except);
            }
        }

        private void HandleWorkIndicatorSyncMessage(CSteamID senderID, ref MsgReader reader)
            => HandleSyncPayload(senderID, ref reader, OnWorkIndicatorSyncReceived);

        #endregion

        #region Player Param Sync Messages

        public void SendPlayerParamSyncToHost(byte[] payload)
            => SendSyncPayloadToHost(Op.PlayerParamSync, payload);

        public void BroadcastPlayerParamSync(byte[] payload, CSteamID? except = null)
            => BroadcastSyncPayload(Op.PlayerParamSync, payload, except);

        private void HandlePlayerParamSyncMessage(CSteamID senderID, ref MsgReader reader)
            => HandleSyncPayload(senderID, ref reader, OnPlayerParamSyncReceived);

        #endregion

        #region Spawn Sync Messages

        public void SendSpawnSyncToHost(byte[] payload)
            => SendSyncPayloadToHost(Op.SpawnSync, payload);

        public void BroadcastSpawnSync(byte[] payload, CSteamID? except = null)
            => BroadcastSyncPayload(Op.SpawnSync, payload, except);

        /// <summary>
        /// Build-cursor projections are transient and supersede older poses, so they use
        /// Steam unreliable packets instead of occupying the reliable gameplay stream.
        /// </summary>
        public void SendSpawnPreviewToHost(byte[] payload)
        {
            CSteamID lobbyID =
                SteamLobbyManager.Instance?.CurrentLobbyID ?? CSteamID.Nil;
            if (lobbyID == CSteamID.Nil)
                return;
            CSteamID hostID = SteamMatchmaking.GetLobbyOwner(lobbyID);
            if (!Multiplayer.SpawnSync.PeerSupportsBuildingPreview(hostID))
                return;

            using (var w = new MsgWriter(Op.SpawnSync, (payload?.Length ?? 0) + 8))
            {
                w.WriteBytes(payload ?? new byte[0]);
                SendBinary(
                    hostID,
                    w.ToArray(),
                    EP2PSend.k_EP2PSendUnreliable);
            }
        }

        public void BroadcastSpawnPreview(
            byte[] payload,
            CSteamID? except = null)
        {
            using (var w = new MsgWriter(Op.SpawnSync, (payload?.Length ?? 0) + 8))
            {
                w.WriteBytes(payload ?? new byte[0]);
                byte[] message = w.ToArray();
                CSteamID lobbyID =
                    SteamLobbyManager.Instance?.CurrentLobbyID ?? CSteamID.Nil;
                if (lobbyID == CSteamID.Nil)
                    return;

                CSteamID localID = SteamUser.GetSteamID();
                int memberCount = SteamMatchmaking.GetNumLobbyMembers(lobbyID);
                for (int i = 0; i < memberCount; i++)
                {
                    CSteamID memberID =
                        SteamMatchmaking.GetLobbyMemberByIndex(lobbyID, i);
                    if (memberID == localID || memberID == except ||
                        !Multiplayer.SpawnSync.PeerSupportsBuildingPreview(memberID))
                    {
                        continue;
                    }

                    SendBinary(
                        memberID,
                        message,
                        EP2PSend.k_EP2PSendUnreliable);
                }
            }
        }

        private void HandleSpawnSyncMessage(CSteamID senderID, ref MsgReader reader)
            => HandleSyncPayload(senderID, ref reader, OnSpawnSyncReceived);

        #endregion

        #region Dungeon Sync Messages

        public void BroadcastDungeonSync(byte[] payload, CSteamID? except = null)
            => BroadcastSyncPayload(Op.DungeonSync, payload, except);

        private void HandleDungeonSyncMessage(CSteamID senderID, ref MsgReader reader)
            => HandleSyncPayload(senderID, ref reader, OnDungeonSyncReceived);

        #endregion

        #region Worker Sync Messages

        public void BroadcastWorkerSync(byte[] payload, CSteamID? except = null)
            => BroadcastSyncPayload(Op.WorkerSync, payload, except);

        private void HandleWorkerSyncMessage(CSteamID senderID, ref MsgReader reader)
            => HandleSyncPayload(senderID, ref reader, OnWorkerSyncReceived);

        #endregion

        #region Fishing Sync Messages

        public void BroadcastFishingSync(byte[] payload, CSteamID? except = null)
            => BroadcastSyncPayload(Op.FishingSync, payload, except);

        private void HandleFishingSyncMessage(CSteamID senderID, ref MsgReader reader)
            => HandleSyncPayload(senderID, ref reader, OnFishingSyncReceived);

        #endregion

        #region DLC Sync Messages

        public void BroadcastDLCSync(byte[] payload, CSteamID? except = null)
            => BroadcastSyncPayload(Op.DLCSync, payload, except);

        private void HandleDLCSyncMessage(CSteamID senderID, ref MsgReader reader)
            => HandleSyncPayload(senderID, ref reader, OnDLCSyncReceived);

        public void BroadcastWGOTransformSync(byte[] payload, CSteamID? except = null)
        {
            using (var w = new MsgWriter(Op.WGOTransformSync, (payload?.Length ?? 0) + 8))
            {
                w.WriteBytes(payload ?? new byte[0]);
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendUnreliable, except);
            }
        }

        private void HandleWGOTransformSyncMessage(CSteamID senderID, ref MsgReader reader)
            => HandleSyncPayload(senderID, ref reader, OnWGOTransformSyncReceived);

        public bool SendInteractionRequestToHost(byte[] payload)
        {
            using (var w = new MsgWriter(Op.InteractionRequest, (payload?.Length ?? 0) + 8))
            {
                w.WriteBytes(payload ?? new byte[0]);
                return SendToHost(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
        }

        private void HandleInteractionRequestMessage(CSteamID senderID, ref MsgReader reader)
        {
            byte[] payload = reader.ReadBytes();
            OnInteractionRequestReceived?.Invoke(senderID, payload);
        }

        public bool SendInteractionZeroHpToHost(byte[] payload)
        {
            using (var w = new MsgWriter(Op.InteractionZeroHp, (payload?.Length ?? 0) + 8))
            {
                w.WriteBytes(payload ?? new byte[0]);
                return SendToHost(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
        }

        private void HandleInteractionZeroHpMessage(CSteamID senderID, ref MsgReader reader)
        {
            byte[] payload = reader.ReadBytes();
            OnInteractionZeroHpReceived?.Invoke(senderID, payload);
        }

        public void BroadcastNpcVisualSync(
            byte[] payload,
            CSteamID? except = null,
            bool reliable = false)
        {
            using (var w = new MsgWriter(Op.NpcVisualSync, (payload?.Length ?? 0) + 8))
            {
                w.WriteBytes(payload ?? new byte[0]);
                BroadcastBinary(
                    w.ToArray(),
                    reliable
                        ? EP2PSend.k_EP2PSendReliable
                        : EP2PSend.k_EP2PSendUnreliable,
                    except);
            }
        }

        private void HandleNpcVisualSyncMessage(CSteamID senderID, ref MsgReader reader)
            => HandleSyncPayload(senderID, ref reader, OnNpcVisualSyncReceived);

        public void SendSkipIntro()
        {
            using (var w = new MsgWriter(Op.SkipIntro))
            {
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
            CoopMod.Logger.LogInfo("[P2P] Sent SkipIntro");
        }

        private void HandleSkipIntroMessage(CSteamID senderID, ref MsgReader reader)
        {
            CoopMod.Logger.LogInfo($"[P2P] Received SkipIntro from {SteamFriends.GetFriendPersonaName(senderID)}");
            OnSkipIntroReceived?.Invoke(senderID);
        }

        public void SendIntroSkipRequest(uint attemptID, bool cancel)
        {
            SendIntroSkipState(
                cancel ? IntroSkipPhase.Cancel : IntroSkipPhase.Request,
                SteamUser.GetSteamID(),
                attemptID,
                0f,
                toHost: true,
                EP2PSend.k_EP2PSendReliable);
        }

        public void BroadcastIntroSkipAuthority(IntroSkipPhase phase, CSteamID ownerID, uint attemptID)
        {
            if (phase != IntroSkipPhase.Grant && phase != IntroSkipPhase.Release)
                return;

            SendIntroSkipState(
                phase,
                ownerID,
                attemptID,
                0f,
                toHost: false,
                EP2PSend.k_EP2PSendReliable);
        }

        public void BroadcastIntroSkipProgress(uint attemptID, float progress)
        {
            SendIntroSkipState(
                IntroSkipPhase.Progress,
                SteamUser.GetSteamID(),
                attemptID,
                Mathf.Clamp01(progress),
                toHost: false,
                EP2PSend.k_EP2PSendUnreliable);
        }

        private void SendIntroSkipState(
            IntroSkipPhase phase,
            CSteamID ownerID,
            uint attemptID,
            float progress,
            bool toHost,
            EP2PSend sendType)
        {
            using (var w = new MsgWriter(Op.IntroSkipState, 20))
            {
                w.Write((byte)phase);
                w.Write(ownerID.m_SteamID);
                w.Write(attemptID);
                w.Write(progress);

                if (toHost)
                    SendToHost(w.ToArray(), sendType);
                else
                    BroadcastBinary(w.ToArray(), sendType);
            }
        }

        private void HandleIntroSkipStateMessage(CSteamID senderID, ref MsgReader reader)
        {
            const int PayloadBytes = 17;
            if (reader.Remaining < PayloadBytes)
            {
                CoopMod.Logger.LogWarning($"[P2P] Ignoring truncated IntroSkipState from {senderID}");
                return;
            }

            var phase = (IntroSkipPhase)reader.ReadByte();
            var ownerID = new CSteamID(reader.ReadUInt64());
            uint attemptID = reader.ReadUInt32();
            float progress = reader.ReadFloat();

            if (phase > IntroSkipPhase.Release || ownerID == CSteamID.Nil || attemptID == 0 ||
                float.IsNaN(progress) || float.IsInfinity(progress))
            {
                CoopMod.Logger.LogWarning($"[P2P] Ignoring invalid IntroSkipState from {senderID}");
                return;
            }

            OnIntroSkipStateReceived?.Invoke(senderID, phase, ownerID, attemptID, Mathf.Clamp01(progress));
        }

        #endregion

        #region Dialogue Messages

        /// <summary>
        /// Send dialogue start notification
        /// </summary>
        public void SendDialogueStart(string npcId)
        {
            using (var w = new MsgWriter(Op.DialogueStart))
            {
                w.Write(npcId ?? "");
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
        }

        /// <summary>
        /// Send dialogue advance notification
        /// </summary>
        public void SendDialogueAdvance()
        {
            using (var w = new MsgWriter(Op.DialogueAdvance))
            {
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
        }

        /// <summary>
        /// Send dialogue end notification
        /// </summary>
        public void SendDialogueEnd()
        {
            using (var w = new MsgWriter(Op.DialogueEnd))
            {
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
        }

        /// <summary>
        /// Send dialogue choice notification
        /// </summary>
        public void SendDialogueChoice(int choiceIndex, string choiceText)
        {
            using (var w = new MsgWriter(Op.DialogueChoice))
            {
                w.Write(choiceIndex);
                w.Write(choiceText ?? "");
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
        }

        public void SendDialogueOptions(List<AnswerVisualData> answers, bool showToLeft)
        {
            if (answers == null || answers.Count == 0)
                return;

            using (var w = new MsgWriter(Op.DialogueOptions, 1024))
            {
                w.Write(showToLeft);
                int count = Math.Min(answers.Count, 64);
                w.Write((byte)count);
                for (int i = 0; i < count; i++)
                    WriteAnswerVisualData(w, answers[i], 0);
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
        }

        public void SendDialogueHover(int choiceIndex)
        {
            using (var w = new MsgWriter(Op.DialogueHover))
            {
                w.Write(choiceIndex);
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
        }

        private static void WriteAnswerVisualData(
            MsgWriter writer,
            AnswerVisualData answer,
            int depth)
        {
            answer = answer ?? new AnswerVisualData();
            bool multiple = answer is MultipleAnswerVisualData && depth == 0;
            writer.Write(multiple);
            writer.Write(answer.id ?? "");
            writer.Write(answer.icon_price ?? "");
            writer.Write(answer.icon_lock ?? "");
            writer.Write(answer.icon_reward ?? "");
            writer.Write(answer.can_be_picked);
            writer.Write(answer.inside_price_is_red);
            writer.Write(answer.price_txt ?? "");
            writer.Write(answer.icon_price_quality ?? "");
            writer.Write(answer.icon_reward_quality ?? "");
            writer.Write(answer.icon_lock_quality ?? "");
            writer.Write(answer.n_price);
            writer.Write(answer.n_reward);
            writer.Write(answer.n_lock);

            int childCount = multiple && answer.answer_visual_datas != null
                ? Math.Min(answer.answer_visual_datas.Count, 32)
                : 0;
            writer.Write((byte)childCount);
            for (int i = 0; i < childCount; i++)
                WriteAnswerVisualData(writer, answer.answer_visual_datas[i], depth + 1);
        }

        /// <summary>
        /// Send speech bubble text
        /// </summary>
        public void SendDialogueBubble(string speakerId, string text)
        {
            using (var w = new MsgWriter(Op.DialogueBubble))
            {
                w.Write(speakerId ?? "");
                w.Write(text ?? "");
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
        }

        /// <summary>
        /// Legacy dialogue message support (for DialogueSync compatibility)
        /// </summary>
        public bool SendDialogueMessage(CSteamID recipientID, string message)
        {
            if (message.StartsWith("DLG_START:"))
            {
                string npcId = message.Substring("DLG_START:".Length);
                using (var w = new MsgWriter(Op.DialogueStart))
                {
                    w.Write(npcId);
                    return SendBinary(recipientID, w.ToArray(), EP2PSend.k_EP2PSendReliable);
                }
            }
            else if (message == "DLG_ADVANCE")
            {
                using (var w = new MsgWriter(Op.DialogueAdvance))
                {
                    return SendBinary(recipientID, w.ToArray(), EP2PSend.k_EP2PSendReliable);
                }
            }
            else if (message == "DLG_END")
            {
                using (var w = new MsgWriter(Op.DialogueEnd))
                {
                    return SendBinary(recipientID, w.ToArray(), EP2PSend.k_EP2PSendReliable);
                }
            }
            else if (message.StartsWith("DLG_CHOICE:"))
            {
                string data = message.Substring("DLG_CHOICE:".Length);
                string[] parts = data.Split('|');
                if (parts.Length >= 2 && int.TryParse(parts[0], out int index))
                {
                    using (var w = new MsgWriter(Op.DialogueChoice))
                    {
                        w.Write(index);
                        w.Write(parts[1]);
                        return SendBinary(recipientID, w.ToArray(), EP2PSend.k_EP2PSendReliable);
                    }
                }
            }
            else if (message.StartsWith("DLG_BUBBLE:"))
            {
                string data = message.Substring("DLG_BUBBLE:".Length);
                string[] parts = data.Split('|');
                if (parts.Length >= 2)
                {
                    using (var w = new MsgWriter(Op.DialogueBubble))
                    {
                        w.Write(parts[0]);
                        w.Write(parts[1]);
                        return SendBinary(recipientID, w.ToArray(), EP2PSend.k_EP2PSendReliable);
                    }
                }
            }

            CoopMod.Logger.LogWarning($"[P2P] Unknown dialogue message format: {message}");
            return false;
        }

        #endregion

        #region Chat Messages

        /// <summary>
        /// Send chat message to all lobby members
        /// </summary>
        public void SendChatMessage(string message)
        {
            using (var w = new MsgWriter(Op.ChatMessage))
            {
                w.Write(message ?? "");
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
        }

        private void HandleChatMessage(CSteamID senderID, ref MsgReader reader)
        {
            string message = reader.ReadString();
            OnChatMessageReceived?.Invoke(senderID, message);
        }

        #endregion

        #region Cosmetics Messages

        /// <summary>
        /// Send cosmetics data to all lobby members
        /// </summary>
        public void BroadcastCosmetics(byte[] cosmeticsData)
        {
            using (var w = new MsgWriter(Op.PlayerCosmetics, 10))
            {
                w.Write((byte)cosmeticsData.Length);
                w.WriteRaw(cosmeticsData);
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
            CoopMod.Logger.LogInfo("[P2P] Broadcast cosmetics data");
        }

        /// <summary>
        /// Send cosmetics data to a specific player
        /// </summary>
        public void SendCosmetics(CSteamID recipientID, byte[] cosmeticsData)
        {
            using (var w = new MsgWriter(Op.PlayerCosmetics, 10))
            {
                w.Write((byte)cosmeticsData.Length);
                w.WriteRaw(cosmeticsData);
                SendBinary(recipientID, w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
        }

        /// <summary>
        /// Request cosmetics data from a player
        /// </summary>
        public void RequestCosmetics(CSteamID targetID)
        {
            using (var w = new MsgWriter(Op.CosmeticsRequest))
            {
                SendBinary(targetID, w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
        }

        private void HandleCosmeticsMessage(CSteamID senderID, ref MsgReader reader)
        {
            byte length = reader.ReadByte();
            byte[] cosmeticsData = reader.ReadRawBytes(length);
            OnCosmeticsReceived?.Invoke(senderID, cosmeticsData);
        }

        #endregion

        #region Cutscene/FlowScript Sync Messages

        /// <summary>
        /// Broadcast a FlowScript trigger to all lobby members.
        /// Used to sync cutscenes (e.g., Gerry's appearance after grave digging).
        /// </summary>
        public void SendCutsceneSync(string scriptName)
        {
            SendCutsceneSync(scriptName, Vector3.zero, 0, "");
        }

        public void SendCutsceneSync(string scriptName, Vector3 originWorldPos, byte facing, string originZoneId)
        {
            using (var w = new MsgWriter(Op.CutsceneSync))
            {
                w.Write(scriptName ?? "");
                w.Write(originWorldPos);
                w.Write(facing);
                w.Write(originZoneId ?? "");
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
            CoopMod.Logger.LogInfo($"[P2P] Sent CutsceneSync: {scriptName}, origin={originWorldPos}, facing={facing}, zone='{originZoneId}'");
        }

        private void HandleCutsceneSyncMessage(CSteamID senderID, ref MsgReader reader)
        {
            string scriptName = reader.ReadString();
            Vector3 originWorldPos = Vector3.zero;
            byte facing = 0;
            string originZoneId = "";
            bool hasScope = false;

            // v0.5.1+ packets include scope data so receivers can defer cutscenes
            // until the local player is close enough to participate.
            if (reader.Remaining >= 13)
            {
                originWorldPos = reader.ReadVector3();
                facing = reader.ReadByte();
                originZoneId = reader.Remaining > 0 ? reader.ReadString() : "";
                hasScope = true;
            }

            CoopMod.Logger.LogInfo($"[P2P] Received CutsceneSync from {SteamFriends.GetFriendPersonaName(senderID)}: {scriptName}, hasScope={hasScope}, origin={originWorldPos}, facing={facing}, zone='{originZoneId}'");
            OnCutsceneSyncReceived?.Invoke(senderID, scriptName, originWorldPos, facing, originZoneId, hasScope);
        }

        /// <summary>
        /// Tell the remote player to walk their local character over to our position and face a direction.
        /// Used so the absent player visibly arrives at the cutscene instead of being teleported.
        /// </summary>
        public void SendCutsceneWalkTo(Vector3 diggerWorldPos, byte facing)
        {
            using (var w = new MsgWriter(Op.CutsceneWalkTo))
            {
                w.Write(diggerWorldPos);
                w.Write(facing);
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
            CoopMod.Logger.LogInfo($"[P2P] Sent CutsceneWalkTo: pos={diggerWorldPos}, facing={facing}");
        }

        private void HandleCutsceneWalkToMessage(CSteamID senderID, ref MsgReader reader)
        {
            Vector3 diggerPos = reader.ReadVector3();
            byte facing = reader.ReadByte();
            CoopMod.Logger.LogInfo($"[P2P] Received CutsceneWalkTo from {SteamFriends.GetFriendPersonaName(senderID)}: pos={diggerPos}, facing={facing}");
            OnCutsceneWalkToReceived?.Invoke(senderID, diggerPos, facing);
        }

        public void SendCutsceneCamera(CutsceneCameraState state)
        {
            if (state == null)
                return;

            using (var w = new MsgWriter(Op.CutsceneCamera))
            {
                w.Write(state.FlyBack);
                w.Write(state.TargetUniqueId);
                w.Write(state.TargetCustomTag ?? "");
                w.Write(state.TargetObjId ?? "");
                w.Write(state.TargetName ?? "");
                w.Write(state.TargetRelativePath ?? "");
                w.Write(state.TargetPosition);
                w.Write(state.Duration);
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }

            string action = state.FlyBack
                ? "back"
                : $"to '{state.TargetObjId}'/'{state.TargetName}' (uid={state.TargetUniqueId})";
            CoopMod.Logger.LogInfo($"[P2P] Sent CutsceneCamera {action}, duration={state.Duration:F2}");
        }

        private void HandleCutsceneCameraMessage(
            CSteamID senderID,
            ref MsgReader reader)
        {
            var state = new CutsceneCameraState
            {
                FlyBack = reader.ReadBool(),
                TargetUniqueId = reader.ReadInt64(),
                TargetCustomTag = reader.ReadString(),
                TargetObjId = reader.ReadString(),
                TargetName = reader.ReadString(),
                TargetRelativePath = reader.ReadString(),
                TargetPosition = reader.ReadVector3(),
                Duration = reader.ReadFloat()
            };

            string action = state.FlyBack
                ? "back"
                : $"to '{state.TargetObjId}'/'{state.TargetName}' (uid={state.TargetUniqueId})";
            CoopMod.Logger.LogInfo(
                $"[P2P] Received CutsceneCamera from " +
                $"{SteamFriends.GetFriendPersonaName(senderID)}: {action}, " +
                $"duration={state.Duration:F2}");
            OnCutsceneCameraReceived?.Invoke(senderID, state);
        }

        public void SendCutsceneComplete(string scriptName, Vector3 originWorldPos, string originZoneId)
        {
            using (var w = new MsgWriter(Op.CutsceneComplete))
            {
                w.Write(scriptName ?? "");
                w.Write(originWorldPos);
                w.Write(originZoneId ?? "");
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
            CoopMod.Logger.LogInfo($"[P2P] Sent CutsceneComplete: {scriptName}, origin={originWorldPos}, zone='{originZoneId}'");
        }

        private void HandleCutsceneCompleteMessage(CSteamID senderID, ref MsgReader reader)
        {
            string scriptName = reader.ReadString();
            Vector3 originWorldPos = reader.ReadVector3();
            string originZoneId = reader.Remaining > 0 ? reader.ReadString() : "";
            CoopMod.Logger.LogInfo($"[P2P] Received CutsceneComplete from {SteamFriends.GetFriendPersonaName(senderID)}: {scriptName}, origin={originWorldPos}, zone='{originZoneId}'");
            OnCutsceneCompleteReceived?.Invoke(senderID, scriptName, originWorldPos, originZoneId);
        }

        /// <summary>
        /// Broadcast an NPC interaction so the remote machine replays it (keeps dialogue/walk-off/
        /// corpse-drop state in sync).
        /// </summary>
        public void SendNpcInteraction(
            string customTag,
            string objId,
            Vector3 npcWorldPos,
            string originZoneId = "",
            CSteamID? interactionOwner = null)
        {
            CSteamID owner = interactionOwner ?? SteamUser.GetSteamID();
            using (var w = new MsgWriter(Op.NpcInteraction))
            {
                w.Write(customTag ?? "");
                w.Write(objId ?? "");
                w.Write(npcWorldPos);
                w.Write(originZoneId ?? "");
                w.Write(owner.m_SteamID);
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
            CoopMod.Logger.LogInfo($"[P2P] Sent NpcInteraction: tag='{customTag}', obj_id='{objId}', pos={npcWorldPos}, zone='{originZoneId}', owner={owner}");
        }

        /// <summary>
        /// Send a client NPC-interaction request only to the lobby host. Spectators
        /// receive the host's approval broadcast, not an unvalidated request.
        /// </summary>
        public bool SendNpcInteractionToHost(
            string customTag,
            string objId,
            Vector3 npcWorldPos,
            string originZoneId = "")
        {
            CSteamID owner = SteamUser.GetSteamID();
            using (var w = new MsgWriter(Op.NpcInteraction))
            {
                w.Write(customTag ?? "");
                w.Write(objId ?? "");
                w.Write(npcWorldPos);
                w.Write(originZoneId ?? "");
                w.Write(owner.m_SteamID);
                bool sent = SendToHost(
                    w.ToArray(),
                    EP2PSend.k_EP2PSendReliable);
                CoopMod.Logger.LogInfo(
                    $"[P2P] Sent NpcInteraction request to host: tag='{customTag}', " +
                    $"obj_id='{objId}', pos={npcWorldPos}, zone='{originZoneId}', sent={sent}");
                return sent;
            }
        }

        private void HandleNpcInteractionMessage(CSteamID senderID, ref MsgReader reader)
        {
            string tag = reader.ReadString();
            string objId = reader.ReadString();
            Vector3 pos = reader.ReadVector3();
            string originZoneId = reader.Remaining > 0 ? reader.ReadString() : "";
            CSteamID interactionOwner = reader.Remaining >= sizeof(ulong)
                ? new CSteamID(reader.ReadUInt64())
                : senderID;
            bool hasScope = pos != Vector3.zero || !string.IsNullOrEmpty(originZoneId);
            CoopMod.Logger.LogInfo($"[P2P] Received NpcInteraction from {SteamFriends.GetFriendPersonaName(senderID)}: tag='{tag}', obj_id='{objId}', pos={pos}, zone='{originZoneId}', hasScope={hasScope}, owner={interactionOwner}");
            OnNpcInteractionReceived?.Invoke(
                senderID,
                tag,
                objId,
                pos,
                originZoneId,
                hasScope,
                interactionOwner);
        }

        public void SendNpcInteractionComplete(string customTag, string objId)
        {
            using (var w = new MsgWriter(Op.NpcInteractionComplete))
            {
                w.Write(customTag ?? "");
                w.Write(objId ?? "");
                BroadcastBinary(
                    w.ToArray(),
                    EP2PSend.k_EP2PSendReliable);
            }
            CoopMod.Logger.LogInfo(
                $"[P2P] Sent NpcInteractionComplete: tag='{customTag}', obj_id='{objId}'");
        }

        private void HandleNpcInteractionCompleteMessage(
            CSteamID senderID,
            ref MsgReader reader)
        {
            string tag = reader.ReadString();
            string objId = reader.ReadString();
            CoopMod.Logger.LogInfo(
                $"[P2P] Received NpcInteractionComplete from " +
                $"{SteamFriends.GetFriendPersonaName(senderID)}: " +
                $"tag='{tag}', obj_id='{objId}'");
            OnNpcInteractionCompleteReceived?.Invoke(senderID, tag, objId);
        }

        /// <summary>
        /// Client -> host: please send me your save slot list (used to mirror host's saves in lobby UI).
        /// </summary>
        public bool SendHostSaveListRequest()
        {
            using (var w = new MsgWriter(Op.HostSaveListRequest))
            {
                bool ok = SendToHost(w.ToArray(), EP2PSend.k_EP2PSendReliable);
                CoopMod.Logger.LogInfo($"[P2P] Sent HostSaveListRequest (ok={ok})");
                return ok;
            }
        }

        /// <summary>
        /// Host -> client: send the save slot list plus the currently-selected filename.
        /// </summary>
        public bool SendHostSaveList(CSteamID target, System.Collections.Generic.List<HostSaveEntryWire> entries, string selectedFilename)
        {
            if (entries == null) entries = new System.Collections.Generic.List<HostSaveEntryWire>();
            using (var w = new MsgWriter(Op.HostSaveList, 512))
            {
                bool hasSelection = selectedFilename != null;
                w.Write(hasSelection);
                if (hasSelection)
                {
                    w.Write(selectedFilename);
                }
                w.Write((ushort)entries.Count);
                foreach (var e in entries)
                {
                    w.Write(e.Filename ?? "");
                    w.Write(e.RealTime ?? "");
                    w.Write(e.Stats ?? "");
                    w.Write(e.GameTime);
                    w.Write(e.Version);
                }
                bool ok = SendBinary(target, w.ToArray(), EP2PSend.k_EP2PSendReliable);
                CoopMod.Logger.LogInfo(
                    $"[P2P] Sent HostSaveList to {SteamFriends.GetFriendPersonaName(target)}: " +
                    $"{entries.Count} entries, selected='{selectedFilename ?? "<none>"}' (ok={ok})");
                return ok;
            }
        }

        private void HandleHostSaveListMessage(CSteamID senderID, ref MsgReader reader)
        {
            bool hasSelection = reader.ReadBool();
            string selected = hasSelection ? reader.ReadString() : null;
            int count = reader.ReadUInt16();
            var list = new System.Collections.Generic.List<HostSaveEntryWire>(count);
            for (int i = 0; i < count; i++)
            {
                var e = new HostSaveEntryWire
                {
                    Filename = reader.ReadString(),
                    RealTime = reader.ReadString(),
                    Stats = reader.ReadString(),
                    GameTime = reader.ReadFloat(),
                    Version = reader.ReadFloat(),
                };
                list.Add(e);
            }
            CoopMod.Logger.LogInfo(
                $"[P2P] Received HostSaveList from {SteamFriends.GetFriendPersonaName(senderID)}: " +
                $"{list.Count} entries, selected='{selected ?? "<none>"}'");
            OnHostSaveListReceived?.Invoke(senderID, list, selected);
        }

        /// <summary>
        /// Host -> all clients: broadcast the filename of the currently-selected save slot ("" = New Game).
        /// </summary>
        public void BroadcastHostSaveSelected(string selectedFilename)
        {
            using (var w = new MsgWriter(Op.HostSaveSelected))
            {
                w.Write(selectedFilename ?? "");
                BroadcastBinary(w.ToArray(), EP2PSend.k_EP2PSendReliable);
            }
            CoopMod.Logger.LogInfo($"[P2P] Broadcast HostSaveSelected: '{selectedFilename}'");
        }

        private void HandleHostSaveSelectedMessage(CSteamID senderID, ref MsgReader reader)
        {
            string selected = reader.ReadString();
            CoopMod.Logger.LogInfo($"[P2P] Received HostSaveSelected from {SteamFriends.GetFriendPersonaName(senderID)}: '{selected}'");
            OnHostSaveSelectedReceived?.Invoke(senderID, selected);
        }

        /// <summary>
        /// Host -> client: ask the target client to leave the lobby. Steam lobbies do not expose a
        /// direct force-kick API, so clients running this mod honor this control packet.
        /// </summary>
        public bool SendLobbyKick(CSteamID target, string reason)
        {
            if (target == CSteamID.Nil || target == SteamUser.GetSteamID())
                return false;

            using (var w = new MsgWriter(Op.LobbyKick, 96))
            {
                w.Write(reason ?? "");
                bool ok = SendBinary(target, w.ToArray(), EP2PSend.k_EP2PSendReliable);
                CoopMod.Logger.LogInfo($"[P2P] Sent LobbyKick to {SteamFriends.GetFriendPersonaName(target)} ({target}), ok={ok}");
                return ok;
            }
        }

        private void HandleLobbyKickMessage(CSteamID senderID, ref MsgReader reader)
        {
            string reason = reader.ReadString();
            CoopMod.Logger.LogInfo($"[P2P] Received LobbyKick from {SteamFriends.GetFriendPersonaName(senderID)}: {reason}");
            OnLobbyKickReceived?.Invoke(senderID, reason);
        }

        #endregion

        #region P2P Session Management

        private void OnP2PSessionRequest(P2PSessionRequest_t callback)
        {
            CSteamID remoteID = callback.m_steamIDRemote;
            string remoteName = SteamFriends.GetFriendPersonaName(remoteID);

            if (!IsCurrentLobbyMember(remoteID) && !IsPreLobbyPeerAllowed(remoteID))
            {
                CoopMod.Logger.LogWarning($"[P2P] Rejected unauthorized P2P session request from {remoteName} ({remoteID})");
                return;
            }

            bool accepted = SteamNetworking.AcceptP2PSessionWithUser(remoteID);
            if (accepted)
                sessionPeers.Add(remoteID.m_SteamID);
            CoopMod.Logger.LogInfo($"[P2P] P2P session request from {remoteName}: {(accepted ? "accepted" : "failed")}");
        }

        public void CloseP2PSession(CSteamID userID)
        {
            if (!SteamManager.Initialized) return;
            SteamNetworking.CloseP2PSessionWithUser(userID);
            sessionPeers.Remove(userID.m_SteamID);
            CoopMod.Logger.LogInfo($"[P2P] Closed P2P session with {SteamFriends.GetFriendPersonaName(userID)}");
        }

        public bool HasActiveConnection(CSteamID userID)
        {
            if (!SteamManager.Initialized) return false;
            P2PSessionState_t sessionState;
            return SteamNetworking.GetP2PSessionState(userID, out sessionState);
        }

        #endregion
    }
}

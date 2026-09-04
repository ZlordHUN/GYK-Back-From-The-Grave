using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Synchronizes shared live item economy state that is not covered by the initial host save:
    /// container inventories, nested item params, and world drops. Player inventories and
    /// equipped toolbar slots are deliberately private to each player; a sender-keyed
    /// inventory baseline is retained by the host only to validate personal item use.
    /// Clients send only changes relative to the last host baseline; the host applies them
    /// and rebroadcasts a canonical full snapshot.
    /// </summary>
    public class InventorySync : MonoBehaviour
    {
        public const string PersonalUseCapabilityKey = "puse";
        public const string PersonalUseCapabilityValue = "1";
        private const byte PayloadVersion = 2;
        private const byte ChestPayloadVersion = 3;
        private const byte ChestMoveRequest = 1;
        private const byte ChestMoveResult = 2;
        private const byte PersonalUseRequest = 3;
        private const byte PersonalUseDecision = 4;
        private const float SyncIntervalSeconds = 3f;
        private const float EchoSuppressSeconds = 0.45f;
        private const float PersonalUseTimeoutSeconds = 10f;
        private const int MaxPayloadBytes = 1024 * 1024;
        private const int MaxWgoEntries = 4096;
        private const int MaxDropEntries = 4096;
        private const int MaxToolbarSlots = 16;
        private const double CaptureWarningMilliseconds = 20.0;
        private const int MaxDiagnosticDirtyEntries = 6;
        private const int MaxDiagnosticExcerptCharacters = 96;

        private static InventorySync _instance;
        public static InventorySync Instance => _instance;

        private readonly Dictionary<ulong, uint> lastReceivedSequenceByOrigin = new Dictionary<ulong, uint>();
        private readonly Dictionary<string, string> hostBaselineWgoFingerprints = new Dictionary<string, string>();
        private readonly HashSet<string> locallyMutatedWgoKeys = new HashSet<string>();
        private readonly Dictionary<long, uint> chestRevisions = new Dictionary<long, uint>();
        private readonly Dictionary<uint, ChestMoveCapture> pendingChestMoves = new Dictionary<uint, ChestMoveCapture>();
        private readonly HashSet<string> processedChestOperations = new HashSet<string>();
        private readonly Dictionary<ulong, Item> remotePersonalInventories =
            new Dictionary<ulong, Item>();
        private readonly Dictionary<ulong, PersonalUseReservation> personalUseReservations =
            new Dictionary<ulong, PersonalUseReservation>();
        private readonly List<ulong> expiredPersonalUsePeers = new List<ulong>(4);

        private bool isSyncEnabled;
        private bool hasHostBaseline;
        private bool isApplyingRemote;
        private float lastSendTime = -SyncIntervalSeconds;
        private float suppressSendUntil;
        private uint nextSequence;
        private string lastSentFingerprint = string.Empty;
        private string hostBaselineDropsFingerprint = string.Empty;
        private float lastImmediateSendTime;
        private bool hasPendingImmediateSend;
        private float nextCaptureFailureLogAt;
        private uint nextChestOperationId;
        private int chestMoveCaptureDepth;
        private uint nextPersonalUseOperationId;
        private PendingPersonalUse pendingPersonalUse;
        private bool isApplyingApprovedPersonalUse;
        private bool hasAnnouncedPersonalInventory;
        private long pendingOpenChestRedrawUniqueId;
        private MethodInfo chestFullRedrawMethod;
        private FieldInfo chestObjectField;

        internal sealed class ChestMoveCapture
        {
            public ChestGUI Gui;
            public WorldGameObject Chest;
            public uint OperationId;
            public uint ExpectedRevision;
            public string ExpectedToken;
            public string ItemJson;
            public string ItemId;
            public int RequestedCount;
            public int BeforeCount;
            public bool RequestedToChest;
            public bool AfterCountGui;
            public List<PersonalItemState> PlayerItemsBefore;
            public List<PersonalItemState> PlayerItemsAfter;
            public string[] PlayerEquippedItemsBefore;
            public string[] PlayerEquippedItemsAfter;
        }

        internal sealed class PersonalItemState
        {
            public Item Reference;
            public Item ParentReference;
            public string ItemId;
            public string Json;
            public int Value;
            public ItemDefinition.EquipmentType EquippedAs;
            public bool InSecondaryInventory;
        }

        private sealed class PendingPersonalUse
        {
            public uint OperationId;
            public WorldGameObject Player;
            public Item Item;
            public Vector3? EffectBubblePosition;
            public Item UseFromBag;
            public float SentAt;
        }

        private sealed class PersonalUseReservation
        {
            public uint OperationId;
            public string ItemId;
            public int BeforeCount;
            public float ExpiresAt;
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
            CoopMod.Logger.LogInfo("[InventorySync] Initialized");
        }

        private void OnEnable()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnInventorySyncReceived -= OnInventorySyncReceived;
                SteamP2PManager.Instance.OnInventorySyncReceived += OnInventorySyncReceived;
            }
        }

        private void OnDisable()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnInventorySyncReceived -= OnInventorySyncReceived;
            }
        }

        public void EnableSync()
        {
            RollBackPendingChestMoves("inventory sync reset");
            isSyncEnabled = true;
            hasHostBaseline = false;
            isApplyingRemote = false;
            lastSendTime = -SyncIntervalSeconds;
            suppressSendUntil = 0f;
            nextSequence = 0;
            lastSentFingerprint = string.Empty;
            hostBaselineWgoFingerprints.Clear();
            locallyMutatedWgoKeys.Clear();
            chestRevisions.Clear();
            pendingChestMoves.Clear();
            processedChestOperations.Clear();
            remotePersonalInventories.Clear();
            personalUseReservations.Clear();
            ItemOwnerCache.Clear();
            hostBaselineDropsFingerprint = string.Empty;
            lastImmediateSendTime = 0f;
            hasPendingImmediateSend = false;
            nextCaptureFailureLogAt = 0f;
            lastReceivedSequenceByOrigin.Clear();
            nextChestOperationId = 0;
            chestMoveCaptureDepth = 0;
            nextPersonalUseOperationId = 0;
            pendingPersonalUse = null;
            isApplyingApprovedPersonalUse = false;
            hasAnnouncedPersonalInventory = false;
            pendingOpenChestRedrawUniqueId = 0L;

            CoopMod.Logger.LogInfo("[InventorySync] Sync enabled");

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop != null && onlineCoop.IsOnlineCoopEnabled && onlineCoop.IsHost)
            {
                SendLocalSnapshot(force: true);
            }
        }

        public void DisableSync()
        {
            isSyncEnabled = false;
            isApplyingRemote = false;
            RollBackPendingChestMoves("inventory sync shutdown");
            lastReceivedSequenceByOrigin.Clear();
            hostBaselineWgoFingerprints.Clear();
            locallyMutatedWgoKeys.Clear();
            chestRevisions.Clear();
            pendingChestMoves.Clear();
            processedChestOperations.Clear();
            remotePersonalInventories.Clear();
            personalUseReservations.Clear();
            ItemOwnerCache.Clear();
            chestMoveCaptureDepth = 0;
            pendingPersonalUse = null;
            isApplyingApprovedPersonalUse = false;
            hasAnnouncedPersonalInventory = false;
            hasPendingImmediateSend = false;
            pendingOpenChestRedrawUniqueId = 0L;
            CoopMod.Logger.LogInfo("[InventorySync] Sync disabled");
        }

        internal static bool IsApplyingRemote => Instance != null && Instance.isApplyingRemote;
        internal static bool IsSuppressingMutationCapture =>
            Instance != null && (Instance.isApplyingRemote || Instance.chestMoveCaptureDepth > 0);

        /// <summary>
        /// A client defers personal consumable use until the host confirms that
        /// the sender's announced inventory contains the item. The accepted call
        /// re-enters vanilla under a narrow bypass guard.
        /// </summary>
        internal bool TryDeferPersonalConsumableUse(
            WorldGameObject player,
            Item item,
            Vector3? effectBubblePosition,
            Item useFromBag)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (!isSyncEnabled ||
                isApplyingApprovedPersonalUse ||
                onlineCoop == null ||
                !onlineCoop.IsOnlineCoopEnabled ||
                onlineCoop.IsHost ||
                !PeerSupportsPersonalUseValidation(
                    SteamLobbyManager.Instance?.GetLobbyOwner() ?? CSteamID.Nil) ||
                player == null ||
                player != MainGame.me?.player ||
                item == null ||
                item.IsEmpty() ||
                item.definition?.can_be_used != true)
            {
                return false;
            }

            if (pendingPersonalUse != null)
            {
                CoopMod.Logger.LogDebug(
                    $"[InventorySync] Ignored duplicate consumable input while " +
                    $"operation {pendingPersonalUse.OperationId} awaits host approval");
                return true;
            }

            uint operationId = ++nextPersonalUseOperationId;
            pendingPersonalUse = new PendingPersonalUse
            {
                OperationId = operationId,
                Player = player,
                Item = item,
                EffectBubblePosition = effectBubblePosition,
                UseFromBag = useFromBag,
                SentAt = Time.realtimeSinceStartup
            };

            using (var stream = new MemoryStream(96))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(ChestPayloadVersion);
                writer.Write(PersonalUseRequest);
                writer.Write(operationId);
                writer.Write(item.id ?? string.Empty);
                writer.Flush();
                SteamP2PManager.Instance?.SendInventorySyncToHost(stream.ToArray());
            }

            CoopMod.Logger.LogInfo(
                $"[InventorySync] Requested host approval to use personal item " +
                $"'{item.id}' (operation={operationId})");
            return true;
        }

        private static bool PeerSupportsPersonalUseValidation(CSteamID peer)
        {
            CSteamID lobbyID =
                SteamLobbyManager.Instance?.CurrentLobbyID ?? CSteamID.Nil;
            if (peer == CSteamID.Nil || lobbyID == CSteamID.Nil)
                return false;

            string capability = SteamMatchmaking.GetLobbyMemberData(
                lobbyID,
                peer,
                PersonalUseCapabilityKey);
            return string.Equals(
                capability,
                PersonalUseCapabilityValue,
                StringComparison.Ordinal);
        }

        internal void OnPeerLeftLobby(CSteamID peer)
        {
            if (peer == CSteamID.Nil)
                return;

            remotePersonalInventories.Remove(peer.m_SteamID);
            personalUseReservations.Remove(peer.m_SteamID);
            if (processedChestOperations.Count == 0)
                return;

            string prefix = peer.m_SteamID + ":";
            var stale = new List<string>();
            foreach (string operation in processedChestOperations)
            {
                if (operation.StartsWith(prefix, StringComparison.Ordinal))
                    stale.Add(operation);
            }
            for (int i = 0; i < stale.Count; i++)
                processedChestOperations.Remove(stale[i]);
        }

        internal void NotifyInventoryMutated(WorldGameObject wgo, bool force = false)
        {
            if (!isSyncEnabled || isApplyingRemote || !CanUseInventoryState())
                return;
            if (wgo != null && !ShouldSyncWgo(wgo))
                return;

            MarkLocallyMutated(wgo);
            SendImmediateSnapshot(force);
        }

        internal void NotifyInventoryDataMutated(Item data, bool force = false)
        {
            if (!isSyncEnabled || isApplyingRemote || data == null || !CanUseInventoryState())
                return;

            WorldGameObject owner = FindWgoByData(data);
            if (owner != null && !ShouldSyncWgo(owner))
                return;

            MarkLocallyMutated(owner);
            SendImmediateSnapshot(force);
        }

        private void MarkLocallyMutated(WorldGameObject wgo)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (wgo == null || onlineCoop == null || onlineCoop.IsHost)
                return;

            string key = BuildWgoKey(
                IsLocalPlayerWgo(wgo),
                wgo.unique_id,
                wgo.obj_id,
                wgo.custom_tag,
                wgo.transform.position);
            if (!string.IsNullOrEmpty(key))
                locallyMutatedWgoKeys.Add(key);
        }

        internal void FlushNow()
        {
            if (!isSyncEnabled || isApplyingRemote || !CanUseInventoryState())
                return;

            // Closing inventory UI still flushes immediately and bypasses the
            // duplicate fingerprint guard, but it is not a resync request. Keep
            // initial/join/critical forced snapshots full while sending only the
            // changed WGO/drop entries for this routine UI close.
            SendImmediateSnapshot(forceSend: true, forceFullSnapshot: false);
        }

        internal void PublishAuthoritativeContainerState(
            WorldGameObject container,
            string source)
        {
            OnlineCoopManager onlineCoop = OnlineCoopManager.Instance;
            if (!isSyncEnabled || isApplyingRemote || container?.data == null ||
                onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled ||
                !onlineCoop.IsHost || !ShouldSyncWgo(container))
            {
                return;
            }

            try
            {
                uint revision = IncrementChestRevision(container.unique_id);
                BroadcastChestMoveResult(
                    CSteamID.Nil,
                    0U,
                    accepted: true,
                    revision,
                    string.Empty,
                    container);
                QueueOpenChestRedraw(container.unique_id);
                CoopMod.Logger.LogInfo(
                    $"[InventorySync] Published canonical container state for " +
                    $"{source} uid={container.unique_id} revision={revision}");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[InventorySync] Could not publish canonical container state " +
                    $"for {source}: {ex.Message}");
            }
        }

        internal bool TryBeginChestMove(
            ChestGUI gui,
            WorldGameObject chest,
            Item item,
            int count,
            bool toChest,
            bool afterCountGui,
            out ChestMoveCapture capture)
        {
            capture = null;
            var onlineCoop = OnlineCoopManager.Instance;
            if (!isSyncEnabled ||
                isApplyingRemote ||
                onlineCoop == null ||
                !onlineCoop.IsOnlineCoopEnabled ||
                !CanUseInventoryState() ||
                gui == null ||
                chest?.data == null ||
                item == null ||
                item.IsEmpty() ||
                count <= 0 ||
                !ShouldSyncWgo(chest))
            {
                return true;
            }

            if (!onlineCoop.IsHost && pendingChestMoves.Count > 0)
            {
                CoopMod.Logger.LogDebug(
                    "[InventorySync] Blocked an additional chest move while the " +
                    "previous operation awaits host confirmation");
                return false;
            }

            if (!QuestSideEffectSync.TryPreparePersonalRewardContainerForMove(
                    chest,
                    out string preparationReason))
            {
                CoopMod.Logger.LogWarning(
                    $"[InventorySync] Blocked chest move for '{chest.custom_tag}': " +
                    preparationReason);
                return false;
            }

            chestRevisions.TryGetValue(chest.unique_id, out uint revision);
            bool needsRequestPayload = !onlineCoop.IsHost;
            capture = new ChestMoveCapture
            {
                Gui = gui,
                Chest = chest,
                ExpectedRevision = revision,
                // The host is authoritative and never sends a move request to
                // itself, so JSON validation payloads would be discarded.
                ExpectedToken = needsRequestPayload
                    ? BuildChestRevisionToken(chest)
                    : string.Empty,
                ItemJson = needsRequestPayload
                    ? SerializeItem(item)
                    : string.Empty,
                ItemId = item.id ?? string.Empty,
                RequestedCount = count,
                BeforeCount = chest.data.GetTotalCount(item.id, true),
                RequestedToChest = toChest,
                AfterCountGui = afterCountGui
            };

            if (needsRequestPayload)
            {
                Item playerData = MainGame.me?.player?.data;
                GameSave save = MainGame.me?.save;
                if (playerData == null || save == null)
                {
                    capture = null;
                    return false;
                }

                capture.PlayerItemsBefore = CapturePersonalItemState(playerData);
                capture.PlayerEquippedItemsBefore = save.equipped_items != null
                    ? (string[])save.equipped_items.Clone()
                    : new string[0];
            }

            chestMoveCaptureDepth++;
            return true;
        }

        internal void CompleteChestMove(ChestMoveCapture capture)
        {
            if (capture == null)
                return;

            chestMoveCaptureDepth = Math.Max(0, chestMoveCaptureDepth - 1);
            WorldGameObject chest = capture.Chest;
            if (chest?.data == null)
                return;

            int afterCount = chest.data.GetTotalCount(capture.ItemId, true);
            int delta = afterCount - capture.BeforeCount;
            if (delta == 0)
                return;

            capture.RequestedToChest = delta > 0;
            capture.RequestedCount = Math.Abs(delta);

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return;

            if (onlineCoop.IsHost)
            {
                uint revision = IncrementChestRevision(chest.unique_id);
                BroadcastChestMoveResult(
                    CSteamID.Nil,
                    0U,
                    accepted: true,
                    revision,
                    string.Empty,
                    chest);
                return;
            }

            Item playerData = MainGame.me?.player?.data;
            GameSave save = MainGame.me?.save;
            if (playerData == null || save == null)
            {
                CoopMod.Logger.LogError(
                    "[InventorySync] Could not capture the optimistic personal " +
                    "inventory delta; rolling the chest move back locally");
                RestorePersonalInventoryDelta(capture);
                return;
            }

            capture.PlayerItemsAfter = CapturePersonalItemState(playerData);
            capture.PlayerEquippedItemsAfter = save.equipped_items != null
                ? (string[])save.equipped_items.Clone()
                : new string[0];
            capture.OperationId = ++nextChestOperationId;
            pendingChestMoves[capture.OperationId] = capture;
            SendChestMoveRequest(capture);
        }

        private void SendChestMoveRequest(ChestMoveCapture capture)
        {
            WorldGameObject chest = capture.Chest;
            using (var stream = new MemoryStream(512))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(ChestPayloadVersion);
                writer.Write(ChestMoveRequest);
                writer.Write(capture.OperationId);
                writer.Write(capture.ExpectedRevision);
                writer.Write(chest.unique_id);
                writer.Write(chest.obj_id ?? string.Empty);
                writer.Write(chest.custom_tag ?? string.Empty);
                WriteVector3(writer, chest.transform.position);
                writer.Write(capture.ExpectedToken ?? string.Empty);
                writer.Write(capture.RequestedToChest);
                writer.Write(capture.RequestedCount);
                writer.Write(capture.ItemJson ?? string.Empty);
                writer.Flush();
                SteamP2PManager.Instance?.SendInventorySyncToHost(stream.ToArray());
            }
        }

        private void SendImmediateSnapshot(bool force)
        {
            SendImmediateSnapshot(force, forceFullSnapshot: force);
        }

        private void SendImmediateSnapshot(bool forceSend, bool forceFullSnapshot)
        {
            float now = Time.realtimeSinceStartup;
            if (!forceSend && now - lastImmediateSendTime < 0.15f)
            {
                // Keep the mutation pending instead of dropping the notification.
                // The same 150 ms network throttle remains in effect, but a burst is
                // represented by one snapshot containing its final combined state.
                hasPendingImmediateSend = true;
                return;
            }

            hasPendingImmediateSend = false;
            lastImmediateSendTime = now;
            SendLocalSnapshot(forceSend, forceFullSnapshot);
        }

        private void Update()
        {
            ExpirePersonalUseState();
            FlushPendingOpenChestRedraw();

            if (!isSyncEnabled || isApplyingRemote)
                return;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled || !CanUseInventoryState())
                return;

            float now = Time.realtimeSinceStartup;
            if (hasPendingImmediateSend && now - lastImmediateSendTime >= 0.15f)
            {
                hasPendingImmediateSend = false;
                lastImmediateSendTime = now;
                SendLocalSnapshot(force: false);
                return;
            }

            if (now - lastSendTime < SyncIntervalSeconds || now < suppressSendUntil)
                return;

            SendLocalSnapshot(force: false);
        }

        public void SendLocalSnapshot(bool force)
        {
            SendLocalSnapshot(force, forceFullSnapshot: force);
        }

        private void SendLocalSnapshot(bool forceSend, bool forceFullSnapshot)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled || !CanUseInventoryState())
                return;

            bool fullSnapshot = onlineCoop.IsHost &&
                                (forceFullSnapshot || !hasHostBaseline);
            if (!onlineCoop.IsHost && !hasHostBaseline)
                return;

            InventorySnapshot snapshot;
            long captureStartedAt = Stopwatch.GetTimestamp();
            try
            {
                snapshot = CaptureSnapshot(fullSnapshot);
            }
            catch (Exception ex)
            {
                // A floating build WGO can be destroyed while the eligible-WGO
                // cache still contains its Unity wrapper. Drop the stale cache and
                // wait for the normal interval instead of retrying every frame.
                InvalidateEligibleWgoCache();
                hasPendingImmediateSend = false;
                lastSendTime = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup >= nextCaptureFailureLogAt)
                {
                    nextCaptureFailureLogAt =
                        Time.realtimeSinceStartup + 5f;
                    CoopMod.Logger.LogWarning(
                        $"[InventorySync] Failed to capture inventory snapshot " +
                        $"({ex.GetType().Name}): {ex}");
                }
                return;
            }
            double captureMilliseconds =
                (Stopwatch.GetTimestamp() - captureStartedAt) * 1000.0 / Stopwatch.Frequency;
            if (GraveyardKeeperCoop.Utils.FrameProfiler.Enabled)
            {
                GraveyardKeeperCoop.Utils.FrameProfiler.Record(
                    "Inventory.Capture",
                    Stopwatch.GetTimestamp() - captureStartedAt);
            }
            if (captureMilliseconds >= CaptureWarningMilliseconds)
            {
                CoopMod.Logger.LogWarning(
                    $"[InventorySync] CaptureSnapshot took {captureMilliseconds:F1}ms " +
                    $"full={fullSnapshot} changes={snapshot.HasChanges} wgo_entries={snapshot.WgoEntries.Count} " +
                    $"drops={snapshot.Drops.Count} toolbar={snapshot.ToolbarItems.Count}");
            }

            if (!snapshot.HasChanges)
            {
                lastSendTime = Time.realtimeSinceStartup;
                return;
            }

            if (!forceSend && snapshot.Fingerprint == lastSentFingerprint)
            {
                lastSendTime = Time.realtimeSinceStartup;
                return;
            }

            snapshot.Sequence = ++nextSequence;

            byte[] payload;
            long serializationStartedAt =
                GraveyardKeeperCoop.Utils.FrameProfiler.Enabled
                    ? Stopwatch.GetTimestamp()
                    : 0L;
            try
            {
                payload = SerializeSnapshot(snapshot);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[InventorySync] Failed to serialize inventory snapshot: {ex.Message}");
                return;
            }
            long serializationTicks = serializationStartedAt != 0L
                ? Stopwatch.GetTimestamp() - serializationStartedAt
                : 0L;
            if (serializationStartedAt != 0L)
            {
                GraveyardKeeperCoop.Utils.FrameProfiler.Record(
                    "Inventory.Serialize",
                    serializationTicks);
            }

            if (payload.Length > MaxPayloadBytes)
            {
                CoopMod.Logger.LogWarning($"[InventorySync] Snapshot too large ({payload.Length} bytes); not sending");
                return;
            }

            long sendStartedAt =
                GraveyardKeeperCoop.Utils.FrameProfiler.Enabled
                    ? Stopwatch.GetTimestamp()
                    : 0L;
            if (onlineCoop.IsHost)
            {
                SteamP2PManager.Instance?.BroadcastInventorySync(payload);
            }
            else
            {
                SteamP2PManager.Instance?.SendInventorySyncToHost(payload);
            }
            long sendTicks = sendStartedAt != 0L
                ? Stopwatch.GetTimestamp() - sendStartedAt
                : 0L;
            if (sendStartedAt != 0L)
            {
                GraveyardKeeperCoop.Utils.FrameProfiler.Record(
                    "Inventory.Send",
                    sendTicks);
                LogSnapshotDiagnostics(
                    snapshot,
                    onlineCoop.IsHost,
                    forceSend,
                    payload.Length,
                    captureMilliseconds,
                    serializationTicks,
                    sendTicks);
            }

            for (int i = 0; i < snapshot.WgoEntries.Count; i++)
            {
                locallyMutatedWgoKeys.Remove(snapshot.WgoEntries[i].Key);
            }

            lastSentFingerprint = snapshot.Fingerprint;
            lastSendTime = Time.realtimeSinceStartup;

            // A sent client delta is the state it expects the host to echo back.
            // Advancing the client's baseline optimistically prevents the next
            // host packet from being mistaken for another unsent local mutation,
            // which previously created a reliable snapshot ping-pong every few
            // hundred milliseconds.
            ApplySnapshotToBaseline(snapshot);
        }

        private void OnInventorySyncReceived(CSteamID senderID, byte[] payload)
        {
            if (!isSyncEnabled)
                return;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled || !CanUseInventoryState())
                return;

            if (payload == null || payload.Length == 0 || payload.Length > MaxPayloadBytes)
                return;

            if (payload[0] == ChestPayloadVersion)
            {
                HandleChestPayload(senderID, payload);
                return;
            }

            InventorySnapshot snapshot;
            try
            {
                snapshot = DeserializeSnapshot(payload);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[InventorySync] Failed to parse snapshot: {ex.Message}");
                return;
            }

            ulong localSteamId = GetLocalSteamId();
            if (snapshot.OriginSteamId == 0UL || snapshot.OriginSteamId == localSteamId)
                return;

            if (snapshot.OriginSteamId != senderID.m_SteamID)
            {
                CoopMod.Logger.LogWarning(
                    $"[InventorySync] Rejected snapshot with mismatched sender " +
                    $"identity: sender={senderID.m_SteamID}, " +
                    $"origin={snapshot.OriginSteamId}");
                return;
            }

            if (!onlineCoop.IsHost && !IsExpectedHost(senderID))
                return;

            if (IsStaleSnapshot(snapshot))
                return;

            if (onlineCoop.IsHost)
                RecordRemotePersonalInventory(senderID, snapshot);

            try
            {
                if (!onlineCoop.IsHost && HasPendingExplicitLocalChanges())
                {
                    SendLocalSnapshot(force: true);
                    return;
                }

                ApplySnapshot(snapshot);

                if (onlineCoop.IsHost)
                {
                    // Relay every accepted client WGO mutation from the host's
                    // freshly applied canonical copy. Explicit dirty keys matter
                    // for nested corpse data whose arbitrary Item params are not
                    // represented by the cheap periodic fingerprint.
                    for (int i = 0; i < snapshot.WgoEntries.Count; i++)
                    {
                        if (snapshot.WgoEntries[i].IsLocalPlayerInventory)
                            continue;

                        locallyMutatedWgoKeys.Add(
                            snapshot.WgoEntries[i].Key);
                    }
                }
                else
                {
                    // Every host delta becomes the client's new canonical
                    // baseline. Keeping only the initial full snapshot made
                    // clients repeatedly send stale container contents back.
                    ApplySnapshotToBaseline(snapshot);
                    SeedLastSentFingerprintFromCurrentState(includeDrops: false);
                    if (!hasAnnouncedPersonalInventory)
                    {
                        // Even when both players happen to start with identical
                        // inventories, the host needs a sender-keyed baseline before
                        // it can validate that player's first consumable operation.
                        hasAnnouncedPersonalInventory = true;
                        locallyMutatedWgoKeys.Add("player:local");
                        SendLocalSnapshot(force: true);
                    }
                }

                suppressSendUntil = Time.realtimeSinceStartup + EchoSuppressSeconds;

                if (onlineCoop.IsHost)
                {
                    // Capture the freshly applied canonical state once and relay it.
                    // SendLocalSnapshot updates both the sent fingerprint and host
                    // baseline, so a preceding full seed capture was redundant.
                    SendLocalSnapshot(force: false);
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[InventorySync] Failed to apply snapshot: {ex.Message}");
            }
        }

        private void HandleChestPayload(CSteamID senderID, byte[] payload)
        {
            try
            {
                using (var stream = new MemoryStream(payload, writable: false))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadByte() != ChestPayloadVersion)
                        return;

                    byte subType = reader.ReadByte();
                    if (subType == ChestMoveRequest)
                    {
                        HandleChestMoveRequest(senderID, reader);
                    }
                    else if (subType == ChestMoveResult)
                    {
                        HandleChestMoveResult(senderID, reader);
                    }
                    else if (subType == PersonalUseRequest)
                    {
                        HandlePersonalUseRequest(senderID, reader);
                    }
                    else if (subType == PersonalUseDecision)
                    {
                        HandlePersonalUseDecision(senderID, reader);
                    }
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[InventorySync] Failed to process chest operation: {ex.Message}");
            }
        }

        private void HandleChestMoveRequest(CSteamID senderID, BinaryReader reader)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null ||
                !onlineCoop.IsHost ||
                !onlineCoop.IsRemotePlayer(senderID))
            {
                return;
            }

            uint operationId = reader.ReadUInt32();
            uint expectedRevision = reader.ReadUInt32();
            var identity = new InventoryWgoEntry
            {
                UniqueId = reader.ReadInt64(),
                ObjId = reader.ReadString(),
                CustomTag = reader.ReadString(),
                Position = ReadVector3(reader)
            };
            string expectedToken = reader.ReadString();
            bool toChest = reader.ReadBoolean();
            int count = reader.ReadInt32();
            string itemJson = reader.ReadString();

            string operationKey = senderID.m_SteamID + ":" + operationId;
            if (processedChestOperations.Count >= 8192)
                processedChestOperations.Clear();
            WorldGameObject chest = ResolveWgo(identity);
            long uniqueId = chest?.unique_id ?? identity.UniqueId;
            chestRevisions.TryGetValue(uniqueId, out uint currentRevision);

            bool duplicate = !processedChestOperations.Add(operationKey);
            bool accepted = false;
            string reason = string.Empty;
            if (duplicate)
            {
                reason = "duplicate operation";
            }
            else if (chest?.data == null || !ShouldSyncWgo(chest))
            {
                reason = "container not found";
            }
            else if (!QuestSideEffectSync.TryPreparePersonalRewardContainerForMove(
                         chest,
                         out string preparationReason))
            {
                // Conversion may increment the authoritative revision while
                // rejecting this stale move, so return the post-conversion value.
                chestRevisions.TryGetValue(uniqueId, out currentRevision);
                reason = preparationReason;
            }
            else if (count <= 0 || count > 100000)
            {
                reason = "invalid item count";
            }
            else if (expectedRevision != currentRevision ||
                     !string.Equals(expectedToken, BuildChestRevisionToken(chest), StringComparison.Ordinal))
            {
                reason = "container changed before the operation reached the host";
            }
            else
            {
                Item item = DeserializeItem(itemJson);
                if (item == null || item.IsEmpty() || string.IsNullOrEmpty(item.id))
                {
                    reason = "invalid item";
                }
                else
                {
                    item.value = count;
                    item.equipped_as = ItemDefinition.EquipmentType.None;
                    isApplyingRemote = true;
                    try
                    {
                        if (toChest)
                        {
                            accepted = chest.data.CanAddItem(item, true, true) &&
                                       chest.data.AddItem(item, true);
                            if (!accepted)
                                reason = "container has no room";
                        }
                        else
                        {
                            accepted = chest.data.GetTotalCount(item.id, true) >= count &&
                                       chest.data.RemoveItem(item, count, null);
                            if (!accepted)
                                reason = "item is no longer available";
                        }
                    }
                    finally
                    {
                        isApplyingRemote = false;
                    }
                }
            }

            if (accepted)
                currentRevision = IncrementChestRevision(uniqueId);

            BroadcastChestMoveResult(
                senderID,
                operationId,
                accepted,
                currentRevision,
                reason,
                chest);

            CoopMod.Logger.LogInfo(
                $"[InventorySync] Chest operation {operationId} from {senderID.m_SteamID} " +
                $"{(accepted ? "accepted" : "rejected")} uid={uniqueId} revision={currentRevision} " +
                $"direction={(toChest ? "deposit" : "withdraw")} item_count={count}" +
                (string.IsNullOrEmpty(reason) ? string.Empty : " reason=" + reason));
        }

        private void BroadcastChestMoveResult(
            CSteamID target,
            uint operationId,
            bool accepted,
            uint revision,
            string reason,
            WorldGameObject chest)
        {
            InventoryWgoEntry entry = chest?.data != null
                ? BuildWgoEntry(chest)
                : null;
            using (var stream = new MemoryStream(1024))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(ChestPayloadVersion);
                writer.Write(ChestMoveResult);
                writer.Write(target.m_SteamID);
                writer.Write(operationId);
                writer.Write(accepted);
                writer.Write(revision);
                writer.Write(reason ?? string.Empty);
                writer.Write(entry != null);
                if (entry != null)
                    WriteWgoEntry(writer, entry);
                writer.Flush();
                byte[] payload = stream.ToArray();
                if (payload.Length <= MaxPayloadBytes)
                    SteamP2PManager.Instance?.BroadcastInventorySync(payload);
            }
        }

        private void HandleChestMoveResult(CSteamID senderID, BinaryReader reader)
        {
            var lobbyManager = SteamLobbyManager.Instance;
            if (OnlineCoopManager.Instance?.IsHost == true ||
                lobbyManager == null ||
                senderID != lobbyManager.GetLobbyOwner())
            {
                return;
            }

            ulong targetSteamId = reader.ReadUInt64();
            uint operationId = reader.ReadUInt32();
            bool accepted = reader.ReadBoolean();
            uint revision = reader.ReadUInt32();
            string reason = reader.ReadString();
            bool hasEntry = reader.ReadBoolean();
            InventoryWgoEntry entry = hasEntry ? ReadWgoEntry(reader) : null;

            long uniqueId = entry?.UniqueId ?? 0L;
            chestRevisions.TryGetValue(uniqueId, out uint currentRevision);
            bool entryIsStale = entry != null && revision < currentRevision;

            bool targetsLocalPlayer = targetSteamId == SteamUser.GetSteamID().m_SteamID;
            ChestMoveCapture pending = null;
            if (targetsLocalPlayer)
                pendingChestMoves.TryGetValue(operationId, out pending);

            bool rollbackSucceeded = true;
            if (targetsLocalPlayer && pending != null && !accepted)
                rollbackSucceeded = RestorePersonalInventoryDelta(pending);

            if (entry != null && !entryIsStale)
            {
                isApplyingRemote = true;
                try
                {
                    ApplyWgoEntry(entry);
                    QueueOpenChestRedraw(entry.UniqueId);
                }
                finally
                {
                    isApplyingRemote = false;
                }

                chestRevisions[entry.UniqueId] = revision;
                hostBaselineWgoFingerprints[entry.Key] = entry.Fingerprint;
            }
            suppressSendUntil = Time.realtimeSinceStartup + EchoSuppressSeconds;

            if (targetsLocalPlayer)
            {
                pendingChestMoves.Remove(operationId);
                if (!accepted)
                {
                    if (pending == null)
                    {
                        CoopMod.Logger.LogWarning(
                            $"[InventorySync] Rejected chest operation {operationId} " +
                            $"had no pending local move to roll back: {reason}");
                    }
                    else if (rollbackSucceeded)
                    {
                        CoopMod.Logger.LogWarning(
                            $"[InventorySync] Reversed the optimistic personal " +
                            $"inventory delta after rejected chest operation " +
                            $"{operationId}: {reason}");
                    }
                    else
                    {
                        CoopMod.Logger.LogError(
                            $"[InventorySync] Rejected chest operation {operationId}, " +
                            $"but its optimistic personal inventory delta could " +
                            $"not be reversed: {reason}");
                    }
                }
            }
        }

        private void HandlePersonalUseRequest(
            CSteamID senderID,
            BinaryReader reader)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null ||
                !onlineCoop.IsHost ||
                !onlineCoop.IsRemotePlayer(senderID))
            {
                return;
            }

            uint operationId = reader.ReadUInt32();
            string itemId = reader.ReadString();
            bool accepted = false;
            string reason = string.Empty;
            ItemDefinition definition = null;
            int available = 0;

            if (string.IsNullOrEmpty(itemId))
            {
                reason = "invalid item";
            }
            else if (personalUseReservations.TryGetValue(
                         senderID.m_SteamID,
                         out PersonalUseReservation existing) &&
                     Time.realtimeSinceStartup < existing.ExpiresAt)
            {
                reason = "previous consumable use is still being reconciled";
            }
            else if (!remotePersonalInventories.TryGetValue(
                         senderID.m_SteamID,
                         out Item inventory))
            {
                reason = "personal inventory baseline has not arrived";
            }
            else
            {
                try
                {
                    definition = GameBalance.me?.GetDataOrNull<ItemDefinition>(itemId);
                }
                catch
                {
                    definition = null;
                }

                available = CountItemRecursive(inventory, itemId, 0);
                if (definition?.can_be_used != true)
                {
                    reason = "item is not a consumable";
                }
                else if (available < 1)
                {
                    reason = "item is not present in the sender inventory";
                }
                else
                {
                    accepted = true;
                    if (!definition.stay_on_use)
                    {
                        personalUseReservations[senderID.m_SteamID] =
                            new PersonalUseReservation
                            {
                                OperationId = operationId,
                                ItemId = itemId,
                                BeforeCount = available,
                                ExpiresAt = Time.realtimeSinceStartup +
                                            PersonalUseTimeoutSeconds
                            };
                    }
                }
            }

            SendPersonalUseDecision(
                senderID,
                operationId,
                accepted,
                reason);
            CoopMod.Logger.LogInfo(
                $"[InventorySync] Personal item use {operationId} from " +
                $"{senderID.m_SteamID} {(accepted ? "accepted" : "rejected")}: " +
                $"item='{itemId}', announced_count={available}" +
                (string.IsNullOrEmpty(reason) ? string.Empty : $", reason={reason}"));
        }

        private void SendPersonalUseDecision(
            CSteamID target,
            uint operationId,
            bool accepted,
            string reason)
        {
            using (var stream = new MemoryStream(96))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(ChestPayloadVersion);
                writer.Write(PersonalUseDecision);
                writer.Write(operationId);
                writer.Write(accepted);
                writer.Write(reason ?? string.Empty);
                writer.Flush();
                SteamP2PManager.Instance?.SendInventorySyncToPeer(
                    target,
                    stream.ToArray());
            }
        }

        private void HandlePersonalUseDecision(
            CSteamID senderID,
            BinaryReader reader)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null ||
                onlineCoop.IsHost ||
                !IsExpectedHost(senderID))
            {
                return;
            }

            uint operationId = reader.ReadUInt32();
            bool accepted = reader.ReadBoolean();
            string reason = reader.ReadString();
            PendingPersonalUse pending = pendingPersonalUse;
            if (pending == null || pending.OperationId != operationId)
                return;

            pendingPersonalUse = null;
            if (!accepted)
            {
                CoopMod.Logger.LogWarning(
                    $"[InventorySync] Host rejected personal item use " +
                    $"{operationId}: {reason}");
                GraveyardKeeperCoop.Utils.ChatManager.AddMessage(
                    "[System] Could not use that item: " + reason);
                RedrawInventoryUi();
                return;
            }

            isApplyingApprovedPersonalUse = true;
            try
            {
                pending.Player?.UseItemFromInventory(
                    pending.Item,
                    pending.EffectBubblePosition,
                    pending.UseFromBag);
                CoopMod.Logger.LogInfo(
                    $"[InventorySync] Applied host-approved personal item use " +
                    $"{operationId}: item='{pending.Item?.id ?? "unknown"}'");
            }
            finally
            {
                isApplyingApprovedPersonalUse = false;
                RedrawInventoryUi();
            }
        }

        private void ExpirePersonalUseState()
        {
            float now = Time.realtimeSinceStartup;
            if (pendingPersonalUse != null &&
                now - pendingPersonalUse.SentAt >= PersonalUseTimeoutSeconds)
            {
                uint operationId = pendingPersonalUse.OperationId;
                pendingPersonalUse = null;
                CoopMod.Logger.LogWarning(
                    $"[InventorySync] Personal item use {operationId} timed out");
                GraveyardKeeperCoop.Utils.ChatManager.AddMessage(
                    "[System] Item use timed out while waiting for the host.");
                RedrawInventoryUi();
            }

            if (personalUseReservations.Count == 0)
                return;

            expiredPersonalUsePeers.Clear();
            foreach (var pair in personalUseReservations)
            {
                if (now >= pair.Value.ExpiresAt)
                    expiredPersonalUsePeers.Add(pair.Key);
            }
            for (int i = 0; i < expiredPersonalUsePeers.Count; i++)
                personalUseReservations.Remove(expiredPersonalUsePeers[i]);
        }

        private static int CountItemRecursive(
            Item root,
            string itemId,
            int depth)
        {
            if (root == null || string.IsNullOrEmpty(itemId) || depth > 6)
                return 0;

            int count = string.Equals(
                root.id,
                itemId,
                StringComparison.OrdinalIgnoreCase)
                ? Math.Max(0, root.value)
                : 0;
            count += CountItemsRecursive(root.inventory, itemId, depth + 1);
            count += CountItemsRecursive(
                root.secondary_inventory,
                itemId,
                depth + 1);
            return count;
        }

        private static int CountItemsRecursive(
            List<Item> items,
            string itemId,
            int depth)
        {
            if (items == null || depth > 6)
                return 0;

            int count = 0;
            for (int i = 0; i < items.Count; i++)
                count += CountItemRecursive(items[i], itemId, depth);
            return count;
        }

        private bool HasPendingExplicitLocalChanges()
        {
            // Explicit mutation keys represent changes that have not reached the
            // host yet. Re-capturing every inventory and comparing it with the old
            // baseline here also classifies the host's incoming canonical delta as
            // a new local edit. During autopsy that produced a reliable echo loop:
            // client re-sent the corpse table, host echoed it, and the client sent
            // it again roughly every 150 ms. Once an explicit delta has been sent,
            // accept the host echo and advance the baseline.
            return locallyMutatedWgoKeys.Count > 0 || hasPendingImmediateSend;
        }

        private bool IsStaleSnapshot(InventorySnapshot snapshot)
        {
            uint lastSequence;
            if (lastReceivedSequenceByOrigin.TryGetValue(snapshot.OriginSteamId, out lastSequence) &&
                snapshot.Sequence <= lastSequence)
            {
                return true;
            }

            lastReceivedSequenceByOrigin[snapshot.OriginSteamId] = snapshot.Sequence;
            return false;
        }

        private void RecordRemotePersonalInventory(
            CSteamID senderID,
            InventorySnapshot snapshot)
        {
            if (snapshot?.WgoEntries == null)
                return;

            for (int i = 0; i < snapshot.WgoEntries.Count; i++)
            {
                InventoryWgoEntry entry = snapshot.WgoEntries[i];
                if (entry?.IsLocalPlayerInventory != true)
                    continue;

                Item inventory = DeserializeItem(entry.ItemJson);
                if (inventory == null)
                    continue;

                inventory.secondary_inventory =
                    DeserializeItemList(entry.SecondaryItemJsons);
                remotePersonalInventories[senderID.m_SteamID] = inventory;

                if (personalUseReservations.TryGetValue(
                        senderID.m_SteamID,
                        out PersonalUseReservation reservation))
                {
                    int currentCount = CountItemRecursive(
                        inventory,
                        reservation.ItemId,
                        0);
                    if (currentCount >= reservation.BeforeCount)
                    {
                        CoopMod.Logger.LogWarning(
                            $"[InventorySync] Approved personal item use " +
                            $"{reservation.OperationId} was not reflected in " +
                            $"{senderID.m_SteamID}'s next inventory snapshot: " +
                            $"item='{reservation.ItemId}', before={reservation.BeforeCount}, " +
                            $"after={currentCount}");
                    }
                    personalUseReservations.Remove(senderID.m_SteamID);
                }
                return;
            }
        }

        private void ApplySnapshot(InventorySnapshot snapshot)
        {
            isApplyingRemote = true;
            try
            {
                // Sender already filtered to entries it saw as changed. The
                // previous per-entry fingerprint check here fought a losing
                // battle between cheap (sender-side) and JSON (wire-recomputed)
                // fingerprints; the resulting mismatch meant the check almost
                // never short-circuited anyway. RestoreSavedInventory is just a
                // field assignment, so always applying is cheaper than running
                // another full fingerprint pass to "save" one.
                for (int i = 0; i < snapshot.WgoEntries.Count; i++)
                {
                    ApplyWgoEntry(snapshot.WgoEntries[i]);
                }

                if (snapshot.HasDrops &&
                    snapshot.DropsFingerprint != BuildDropsFingerprint(CaptureDrops()))
                {
                    ApplyDrops(snapshot.Drops);
                }

                RedrawInventoryUi();
            }
            finally
            {
                isApplyingRemote = false;
            }
        }

        private void ApplySnapshotToBaseline(InventorySnapshot snapshot)
        {
            if (snapshot.IsFull)
            {
                hostBaselineWgoFingerprints.Clear();
            }

            for (int i = 0; i < snapshot.WgoEntries.Count; i++)
            {
                InventoryWgoEntry entry = snapshot.WgoEntries[i];
                hostBaselineWgoFingerprints[entry.Key] = entry.Fingerprint;
            }

            if (snapshot.IsFull || snapshot.HasDrops)
            {
                hostBaselineDropsFingerprint = snapshot.DropsFingerprint;
            }

            hasHostBaseline = true;
        }

        private void SeedLastSentFingerprintFromCurrentState(bool includeDrops)
        {
            try
            {
                List<WgoCheapFingerprint> cheapFingerprints = CaptureCheapWgoFingerprints();
                var builder = new StringBuilder(cheapFingerprints.Count * 48);
                for (int i = 0; i < cheapFingerprints.Count; i++)
                {
                    WgoCheapFingerprint entry = cheapFingerprints[i];
                    builder.Append(entry.Key).Append('=').Append(entry.Fingerprint).Append('\n');
                }

                string dropsFingerprint = includeDrops
                    ? BuildDropsFingerprint(CaptureDrops())
                    : hostBaselineDropsFingerprint;
                lastSentFingerprint = false + "\nTB:\nWGO:\n" + builder +
                                      "\nDROPS:" + dropsFingerprint;
            }
            catch
            {
                lastSentFingerprint = string.Empty;
            }
        }

        private static bool CanUseInventoryState()
        {
            return MainGame.game_started
                && MainGame.me != null
                && MainGame.me.save != null
                && MainGame.me.player != null
                && MainGame.me.world_root != null;
        }

        private static bool IsExpectedHost(CSteamID senderID)
        {
            var lobbyID = SteamLobbyManager.Instance?.CurrentLobbyID ?? CSteamID.Nil;
            if (lobbyID == CSteamID.Nil)
                return true;

            return SteamMatchmaking.GetLobbyOwner(lobbyID) == senderID;
        }

        private static ulong GetLocalSteamId()
        {
            return SteamManager.Initialized ? SteamUser.GetSteamID().m_SteamID : 0UL;
        }

        private InventorySnapshot CaptureSnapshot(bool fullSnapshot)
        {
            bool profiling =
                GraveyardKeeperCoop.Utils.FrameProfiler.Enabled;
            var snapshot = new InventorySnapshot
            {
                Version = PayloadVersion,
                OriginSteamId = GetLocalSteamId(),
                IsFull = fullSnapshot,
                WgoEntries = new List<InventoryWgoEntry>(),
                Drops = new List<InventoryDropEntry>(),
                ToolbarItems = new List<string>()
            };
            if (profiling)
            {
                snapshot.DiagnosticDirtyEntries =
                    new List<string>(MaxDiagnosticDirtyEntries);
            }

            // Personal inventories and equipped slots are owned by the player on
            // this machine. Keep the fields on the wire for protocol compatibility,
            // but never publish them as campaign inventory state.
            snapshot.ToolbarFingerprint = string.Empty;
            snapshot.HasToolbar = false;

            // Phase 1: compute cheap structural fingerprints for all eligible WGOs.
            // This avoids the expensive Item.ToJSON() call for unchanged entries (the
            // common case). JSON serialization only happens below for entries whose
            // cheap fingerprint actually differs from the baseline.
            List<WgoCheapFingerprint> cheapFingerprints = CaptureCheapWgoFingerprints();
            snapshot.DiagnosticEligibleWgoCount = cheapFingerprints.Count;
            bool isHost = OnlineCoopManager.Instance?.IsHost == true;
            var allWgoFingerprintBuilder = new StringBuilder(cheapFingerprints.Count * 48);
            for (int i = 0; i < cheapFingerprints.Count; i++)
            {
                WgoCheapFingerprint cheap = cheapFingerprints[i];
                allWgoFingerprintBuilder.Append(cheap.Key).Append('=').Append(cheap.Fingerprint).Append('\n');
            }

            // Phase 2: serialize JSON only for WGOs whose cheap fingerprint changed
            // (or all of them, for a full snapshot).
            for (int i = 0; i < cheapFingerprints.Count; i++)
            {
                WgoCheapFingerprint cheap = cheapFingerprints[i];
                bool hasBaseline = hostBaselineWgoFingerprints.TryGetValue(
                    cheap.Key,
                    out string baselineFingerprint);
                bool explicitlyDirty =
                    locallyMutatedWgoKeys.Contains(cheap.Key);
                bool fingerprintChanged =
                    hasBaseline && baselineFingerprint != cheap.Fingerprint;
                bool changed = fullSnapshot ||
                               explicitlyDirty ||
                               (hasBaseline
                                   ? fingerprintChanged
                                   : isHost);
                if (!changed)
                    continue;

                if (profiling)
                {
                    string reason;
                    if (fullSnapshot)
                    {
                        snapshot.DiagnosticFullEntryCount++;
                        reason = "full";
                    }
                    else if (explicitlyDirty)
                    {
                        snapshot.DiagnosticExplicitDirtyCount++;
                        reason = "explicit_dirty";
                    }
                    else if (!hasBaseline)
                    {
                        snapshot.DiagnosticMissingBaselineCount++;
                        reason = "missing_baseline";
                    }
                    else
                    {
                        snapshot.DiagnosticFingerprintChangedCount++;
                        reason = "fingerprint_changed";
                    }

                    if (snapshot.DiagnosticDirtyEntries.Count <
                        MaxDiagnosticDirtyEntries)
                    {
                        snapshot.DiagnosticDirtyEntries.Add(
                            DescribeWgoDiagnostic(
                                cheap,
                                reason,
                                hasBaseline ? baselineFingerprint : null));
                    }
                }

                InventoryWgoEntry entry = BuildWgoEntry(cheap.Wgo);
                if (!string.IsNullOrEmpty(entry.Key))
                {
                    snapshot.WgoEntries.Add(entry);
                }
            }

            // CombatSync carries immediate create/collect events from either peer.
            // Inventory snapshots are the durable reconciliation layer, so only
            // the host may publish their canonical drop list. Allowing clients to
            // send drop snapshots lets an in-flight pre-pickup snapshot resurrect
            // a corpse after the host has already collected it.
            if (isHost)
            {
                snapshot.Drops = CaptureDrops();
                snapshot.DropsFingerprint = BuildDropsFingerprint(snapshot.Drops);
                snapshot.HasDrops =
                    fullSnapshot ||
                    snapshot.DropsFingerprint != hostBaselineDropsFingerprint;
                if (profiling && snapshot.HasDrops)
                {
                    snapshot.DiagnosticDropsChange =
                        DescribeFingerprintDifference(
                            hostBaselineDropsFingerprint,
                            snapshot.DropsFingerprint);
                }
            }
            else
            {
                snapshot.Drops = new List<InventoryDropEntry>();
                snapshot.DropsFingerprint = hostBaselineDropsFingerprint;
                snapshot.HasDrops = false;
            }

            snapshot.Fingerprint = snapshot.IsFull + "\nTB:" + snapshot.ToolbarFingerprint +
                                   "\nWGO:\n" + allWgoFingerprintBuilder +
                                   "\nDROPS:" + snapshot.DropsFingerprint;

            snapshot.HasChanges = fullSnapshot ||
                                  snapshot.WgoEntries.Count > 0 ||
                                  snapshot.HasDrops;

            if (!snapshot.HasToolbar)
            {
                snapshot.ToolbarItems.Clear();
            }

            if (!snapshot.HasDrops)
            {
                snapshot.Drops.Clear();
            }

            return snapshot;
        }

        private static void LogSnapshotDiagnostics(
            InventorySnapshot snapshot,
            bool isHost,
            bool force,
            int payloadBytes,
            double captureMilliseconds,
            long serializationTicks,
            long sendTicks)
        {
            string details = snapshot.DiagnosticDirtyEntries != null &&
                             snapshot.DiagnosticDirtyEntries.Count > 0
                ? string.Join(" || ", snapshot.DiagnosticDirtyEntries.ToArray())
                : "none";
            string drops = string.IsNullOrEmpty(snapshot.DiagnosticDropsChange)
                ? "unchanged"
                : snapshot.DiagnosticDropsChange;

            CoopMod.Logger.LogWarning(
                $"[PERF][InventorySync] role={(isHost ? "host" : "client")} " +
                $"sequence={snapshot.Sequence} force={force} full={snapshot.IsFull} " +
                $"eligible_wgos={snapshot.DiagnosticEligibleWgoCount} " +
                $"sent_wgos={snapshot.WgoEntries.Count} drops={snapshot.Drops.Count} " +
                $"payload={payloadBytes}B capture={captureMilliseconds:F1}ms " +
                $"serialize={TicksToMilliseconds(serializationTicks):F1}ms " +
                $"send={TicksToMilliseconds(sendTicks):F1}ms " +
                $"reasons=full:{snapshot.DiagnosticFullEntryCount}," +
                $"explicit:{snapshot.DiagnosticExplicitDirtyCount}," +
                $"missing:{snapshot.DiagnosticMissingBaselineCount}," +
                $"fingerprint:{snapshot.DiagnosticFingerprintChangedCount} " +
                $"dirty=[{details}] drops_change=[{drops}]");
        }

        private static string DescribeWgoDiagnostic(
            WgoCheapFingerprint entry,
            string reason,
            string baselineFingerprint)
        {
            string objId = entry.Wgo?.obj_id ?? string.Empty;
            return $"reason={reason} key='{ClipDiagnostic(entry.Key, 80)}' " +
                   $"obj='{ClipDiagnostic(objId, 48)}' " +
                   DescribeFingerprintDifference(
                       baselineFingerprint,
                       entry.Fingerprint);
        }

        private static string DescribeFingerprintDifference(
            string baseline,
            string current)
        {
            baseline = baseline ?? string.Empty;
            current = current ?? string.Empty;
            if (string.Equals(baseline, current, StringComparison.Ordinal))
            {
                return $"fingerprint_unchanged len={current.Length}";
            }

            int commonLength = Math.Min(baseline.Length, current.Length);
            int differenceIndex = 0;
            while (differenceIndex < commonLength &&
                   baseline[differenceIndex] == current[differenceIndex])
            {
                differenceIndex++;
            }

            int excerptStart = Math.Max(0, differenceIndex - 24);
            return $"diff_at={differenceIndex} old_len={baseline.Length} " +
                   $"new_len={current.Length} " +
                   $"old='{FingerprintExcerpt(baseline, excerptStart)}' " +
                   $"new='{FingerprintExcerpt(current, excerptStart)}'";
        }

        private static string FingerprintExcerpt(string value, int start)
        {
            if (string.IsNullOrEmpty(value) || start >= value.Length)
                return string.Empty;

            int length = Math.Min(
                MaxDiagnosticExcerptCharacters,
                value.Length - start);
            return EscapeDiagnostic(value.Substring(start, length));
        }

        private static string ClipDiagnostic(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            string escaped = EscapeDiagnostic(value);
            return escaped.Length <= maxLength
                ? escaped
                : escaped.Substring(0, maxLength) + "...";
        }

        private static string EscapeDiagnostic(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        private static List<InventoryWgoEntry> CaptureWgoEntries()
        {
            var entries = new List<InventoryWgoEntry>();
            List<WorldGameObject> worldObjects = MainGame.me.GetListOfWorldObjects();
            if (worldObjects == null)
                return entries;

            bool capturedLocalPlayer = false;
            for (int i = 0; i < worldObjects.Count; i++)
            {
                WorldGameObject wgo = worldObjects[i];
                if (!ShouldSyncWgo(wgo))
                    continue;

                capturedLocalPlayer |= IsLocalPlayerWgo(wgo);
                InventoryWgoEntry entry = BuildWgoEntry(wgo);
                if (!string.IsNullOrEmpty(entry.Key))
                {
                    entries.Add(entry);
                }
            }

            if (!capturedLocalPlayer && ShouldSyncWgo(MainGame.me.player))
            {
                entries.Add(BuildWgoEntry(MainGame.me.player));
            }

            entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return entries;
        }

        private const float EligibleWgoCacheTtlSeconds = 5f;
        private static readonly List<WorldGameObject> EligibleWgoCache = new List<WorldGameObject>();
        private static readonly Dictionary<Item, WorldGameObject> ItemOwnerCache =
            new Dictionary<Item, WorldGameObject>(ReferenceComparer<Item>.Instance);
        private static float _eligibleWgoCacheBuildTime = -1000f;

        /// <summary>
        /// Walks all sync-eligible WGOs and produces cheap structural fingerprints.
        /// This is the hot path used by CaptureSnapshot to decide which WGOs need
        /// full JSON serialization. Keep this O(N) and allocation-light: it must not
        /// call Item.ToJSON() or any other reflective serializer.
        /// </summary>
        private static List<WgoCheapFingerprint> CaptureCheapWgoFingerprints()
        {
            var entries = new List<WgoCheapFingerprint>();

            if (Time.realtimeSinceStartup - _eligibleWgoCacheBuildTime >= EligibleWgoCacheTtlSeconds)
            {
                EligibleWgoCache.Clear();
                ItemOwnerCache.Clear();
                // Use the WGORegistry's maintained snapshot (O(1)) instead of
                // MainGame.me.GetListOfWorldObjects(), which calls FindObjectsOfType
                // and scans the entire scene (~20k objects, ~100ms) on every call.
                List<WorldGameObject> worldObjects = WGORegistry.Instance?.SnapshotAll()
                                                      ?? MainGame.me?.GetListOfWorldObjects();
                if (worldObjects != null)
                {
                    for (int i = 0; i < worldObjects.Count; i++)
                    {
                        WorldGameObject wgo = worldObjects[i];
                        if (ShouldSyncWgo(wgo))
                            EligibleWgoCache.Add(wgo);
                    }
                }
                _eligibleWgoCacheBuildTime = Time.realtimeSinceStartup;
            }

            bool capturedLocalPlayer = false;
            for (int i = 0; i < EligibleWgoCache.Count; i++)
            {
                WorldGameObject wgo = EligibleWgoCache[i];
                if (!ShouldSyncWgo(wgo))
                {
                    EligibleWgoCache.RemoveAt(i);
                    i--;
                    continue;
                }
                capturedLocalPlayer |= IsLocalPlayerWgo(wgo);
                AppendCheapFingerprint(entries, wgo);
            }

            if (!capturedLocalPlayer && MainGame.me?.player != null && ShouldSyncWgo(MainGame.me.player))
            {
                AppendCheapFingerprint(entries, MainGame.me.player);
            }

            entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return entries;
        }

        internal static void InvalidateEligibleWgoCache()
        {
            EligibleWgoCache.Clear();
            ItemOwnerCache.Clear();
            _eligibleWgoCacheBuildTime = -1000f;
        }

        private static void AppendCheapFingerprint(List<WgoCheapFingerprint> entries, WorldGameObject wgo)
        {
            if (!ShouldSyncWgo(wgo))
                return;

            bool isLocalPlayerInventory = IsLocalPlayerWgo(wgo);
            string key = BuildWgoKey(isLocalPlayerInventory, wgo.unique_id, wgo.obj_id, wgo.custom_tag, wgo.transform.position);
            if (string.IsNullOrEmpty(key))
                return;

            string fingerprint = BuildCheapWgoFingerprint(wgo, isLocalPlayerInventory);
            IndexItemOwner(wgo?.data, wgo, 0);
            entries.Add(new WgoCheapFingerprint { Key = key, Fingerprint = fingerprint, Wgo = wgo });
        }

        private static void IndexItemOwner(Item item, WorldGameObject owner, int depth)
        {
            if (item == null || owner == null || depth > 4)
                return;

            ItemOwnerCache[item] = owner;
            IndexItemOwners(item.inventory, owner, depth + 1);
            IndexItemOwners(item.secondary_inventory, owner, depth + 1);
        }

        private static void IndexItemOwners(List<Item> items, WorldGameObject owner, int depth)
        {
            if (items == null)
                return;

            for (int i = 0; i < items.Count; i++)
                IndexItemOwner(items[i], owner, depth);
        }

        /// <summary>
        /// Cheap structural fingerprint that captures every field Item.ToJSON would
        /// serialize, but without invoking JSON. Detects any meaningful inventory
        /// mutation (item added/removed/reordered, value/durability/params changes).
        /// </summary>
        private static string BuildCheapWgoFingerprint(WorldGameObject wgo, bool isLocalPlayerInventory)
        {
            return BuildCheapItemFingerprint(wgo?.data);
        }

        private static string BuildCheapItemFingerprint(Item data)
        {
            if (data == null)
                return "empty";

            var sb = new StringBuilder(64);
            sb.Append("sz:").Append(data.inventory_size).Append('|');
            AppendItemListFingerprint(sb, data.inventory);
            sb.Append('|');
            AppendItemListFingerprint(sb, data.secondary_inventory);
            return sb.ToString();
        }

        private static void AppendItemListFingerprint(StringBuilder sb, List<Item> items)
        {
            if (items == null)
            {
                sb.Append("n:0");
                return;
            }

            sb.Append("c:").Append(items.Count).Append('{');
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (item == null)
                {
                    sb.Append("n;");
                    continue;
                }

                // Use public Item properties (id/value/durability/hp/progress/money)
                // — _params itself is private. inventory_size reflects nested bag
                // capacity, included so bag-size changes are detected.
                sb.Append(item.id ?? string.Empty).Append(',')
                  .Append(item.value).Append(',')
                  .Append(item.durability).Append(',')
                  .Append(item.hp).Append(',')
                  .Append(item.progress).Append(',')
                  .Append(item.money).Append(',')
                  .Append(item.inventory_size).Append(';');
            }

            sb.Append('}');
        }

        private static Dictionary<string, string> BuildCurrentWgoFingerprintMap()
        {
            // Reuse the cheap fingerprint path so ApplySnapshot does NOT re-serialize
            // every WGO inventory to JSON just to decide which entries to apply.
            var result = new Dictionary<string, string>();
            List<WgoCheapFingerprint> entries = CaptureCheapWgoFingerprints();
            for (int i = 0; i < entries.Count; i++)
            {
                WgoCheapFingerprint entry = entries[i];
                result[entry.Key] = entry.Fingerprint;
            }

            return result;
        }

        private static bool ShouldSyncWgo(WorldGameObject wgo)
        {
            if (wgo == null || wgo.data == null || wgo.is_removed || wgo.IsDisabled())
                return false;
            if (SpawnSync.IsTransientBuildingPreview(wgo))
                return false;

            if (wgo.is_player)
                return false;

            Item data = wgo.data;
            if ((data.inventory != null && data.inventory.Count > 0) ||
                (data.secondary_inventory != null && data.secondary_inventory.Count > 0))
            {
                return true;
            }

            if (data.inventory_size > 0)
                return true;

            ObjectDefinition objDef = wgo.obj_def;
            return objDef != null &&
                   (objDef.inventory_size > 0 ||
                    objDef.open_in_multiinventory ||
                    (objDef.can_insert_items != null && objDef.can_insert_items.Count > 0) ||
                    objDef.can_insert_items_limit > 0);
        }

        internal static WorldGameObject FindWgoByData(Item data)
        {
            if (data == null)
                return null;
            if (MainGame.me?.player != null && MainGame.me.player.data == data)
                return MainGame.me.player;
            if (ItemOwnerCache.TryGetValue(data, out WorldGameObject cachedOwner) &&
                cachedOwner != null &&
                !cachedOwner.is_removed &&
                ContainsItemReference(cachedOwner.data, data, 0))
            {
                return cachedOwner;
            }
            ItemOwnerCache.Remove(data);

            List<WorldGameObject> worldObjects = WGORegistry.Instance?.SnapshotAll() ?? MainGame.me?.GetListOfWorldObjects();
            if (worldObjects == null)
                return null;

            for (int i = 0; i < worldObjects.Count; i++)
            {
                WorldGameObject wgo = worldObjects[i];
                if (wgo != null && ContainsItemReference(wgo.data, data, 0))
                {
                    ItemOwnerCache[data] = wgo;
                    return wgo;
                }
            }

            return null;
        }

        private static bool ContainsItemReference(
            Item root,
            Item target,
            int depth)
        {
            if (root == null || target == null || depth > 4)
                return false;
            if (object.ReferenceEquals(root, target))
                return true;

            if (ContainsItemReference(root.inventory, target, depth + 1))
                return true;
            return ContainsItemReference(
                root.secondary_inventory,
                target,
                depth + 1);
        }

        private static bool ContainsItemReference(
            List<Item> items,
            Item target,
            int depth)
        {
            if (items == null)
                return false;

            for (int i = 0; i < items.Count; i++)
            {
                if (ContainsItemReference(items[i], target, depth))
                    return true;
            }

            return false;
        }

        private static InventoryWgoEntry BuildWgoEntry(WorldGameObject wgo)
        {
            bool isLocalPlayerInventory = IsLocalPlayerWgo(wgo);
            var entry = new InventoryWgoEntry
            {
                IsLocalPlayerInventory = isLocalPlayerInventory,
                UniqueId = wgo.unique_id,
                ObjId = wgo.obj_id ?? string.Empty,
                CustomTag = wgo.custom_tag ?? string.Empty,
                Position = wgo.transform.position,
                ItemJson = SerializeWgoInventoryData(wgo, isLocalPlayerInventory),
                SecondaryItemJsons = SerializeItemList(wgo.data?.secondary_inventory),
                Key = BuildWgoKey(isLocalPlayerInventory, wgo.unique_id, wgo.obj_id, wgo.custom_tag, wgo.transform.position)
            };

            // Use the same cheap structural fingerprint that
            // BuildCurrentWgoFingerprintMap produces on the receiver, so the
            // per-entry comparison in ApplySnapshot actually matches.
            entry.Fingerprint = BuildCheapWgoFingerprint(wgo, isLocalPlayerInventory);
            return entry;
        }

        private static List<InventoryDropEntry> CaptureDrops()
        {
            var result = new List<InventoryDropEntry>();
            List<DropResGameObject> drops = DropsList.me?.drops;
            if (drops == null)
                return result;

            for (int i = 0; i < drops.Count; i++)
            {
                DropResGameObject drop = drops[i];
                if (drop == null || drop.is_collected || drop.res == null || drop.res.IsEmpty())
                    continue;

                var entry = new InventoryDropEntry
                {
                    ItemJson = SerializeItem(drop.res),
                    Position = drop.transform.position,
                    ZoneId = drop.zone_id ?? string.Empty
                };
                entry.Fingerprint = BuildDropFingerprint(entry);
                result.Add(entry);
            }

            result.Sort((a, b) => string.CompareOrdinal(a.Fingerprint, b.Fingerprint));
            return result;
        }

        private static void ApplyWgoEntry(InventoryWgoEntry entry)
        {
            // Payload v2 peers may still send the legacy player:local entry. It is
            // personal state, not a shared container, so never apply it locally.
            if (entry == null || entry.IsLocalPlayerInventory)
                return;

            WorldGameObject wgo = ResolveWgo(entry);
            if (wgo == null)
            {
                CoopMod.Logger.LogDebug($"[InventorySync] Could not resolve WGO inventory target: {entry.ObjId} uid={entry.UniqueId}");
                return;
            }

            Item item = DeserializeItem(entry.ItemJson);
            if (item == null)
                return;

            item.secondary_inventory = DeserializeItemList(entry.SecondaryItemJsons);

            wgo.RestoreSavedInventory(item);
            try
            {
                wgo.Redraw(true);
                SmartDrawer drawer =
                    wgo.GetComponentInChildren<SmartDrawer>(true);
                drawer?.Redraw(true);
                if (wgo.show_quality_hint &&
                    wgo.obj_def != null &&
                    wgo.obj_def.quality_type !=
                    ObjectDefinition.QualityType.Hidden)
                {
                    // Quality may be derived from synchronized inventory contents
                    // (most importantly the body inside a grave). A visual redraw
                    // alone leaves the existing skull widget stale.
                    wgo.SetQualityHint(true);
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[InventorySync] Failed to redraw synchronized WGO '{wgo.obj_id}': {ex.Message}");
            }
        }

        private static WorldGameObject ResolveWgo(InventoryWgoEntry entry)
        {
            if (entry.IsLocalPlayerInventory)
            {
                return MainGame.me?.player;
            }

            if (entry.UniqueId > 0L)
            {
                if (WGORegistry.Instance != null && WGORegistry.Instance.TryGet(entry.UniqueId, out var indexed))
                    return indexed;

                WorldGameObject byUniqueId = WorldMap.GetWorldGameObjectByUniqueId(entry.UniqueId, false);
                if (byUniqueId != null)
                    return byUniqueId;
            }

            if (!string.IsNullOrEmpty(entry.CustomTag))
            {
                WorldGameObject byTag = WorldMap.GetWorldGameObjectByCustomTag(entry.CustomTag, true);
                if (byTag != null)
                    return byTag;
            }

            if (string.IsNullOrEmpty(entry.ObjId))
                return null;

            WorldGameObject registryMatch = WGORegistry.Instance?.ResolveNearest(entry.ObjId, entry.Position, 128f);
            if (registryMatch != null)
                return registryMatch;

            List<WorldGameObject> worldObjects = MainGame.me.GetListOfWorldObjects();
            WorldGameObject nearest = null;
            float nearestDistance = 128f * 128f;

            for (int i = 0; i < worldObjects.Count; i++)
            {
                WorldGameObject candidate = worldObjects[i];
                if (candidate == null || candidate.obj_id != entry.ObjId ||
                    candidate.GetComponent<RemoteBuildingPreviewMarker>() != null)
                    continue;

                float distance = (candidate.transform.position - entry.Position).sqrMagnitude;
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = candidate;
                }
            }

            return nearest;
        }

        private static void ApplyDrops(List<InventoryDropEntry> drops)
        {
            DropsList list = DropsList.me;
            if (list == null)
                return;

            CombatSync.PushDropCreateSuppression();
            try
            {
                var existingDrops = new List<DropResGameObject>(list.drops);
                var matchedDrops = new HashSet<DropResGameObject>();

                if (drops != null)
                {
                    for (int i = 0; i < drops.Count; i++)
                    {
                        InventoryDropEntry entry = drops[i];
                        Item item = DeserializeItem(entry.ItemJson);
                        if (item == null || item.IsEmpty())
                            continue;

                        DropResGameObject existing = FindMatchingExistingDrop(
                            existingDrops,
                            matchedDrops,
                            item,
                            entry.Position);
                        if (existing != null)
                        {
                            matchedDrops.Add(existing);
                            existing.res = item;
                            existing.transform.position = entry.Position;
                            existing.zone_id = entry.ZoneId ?? string.Empty;
                            existing.res.drop_zone_id = existing.zone_id;
                            continue;
                        }

                        DropResGameObject created = DropResGameObject.Drop(
                            entry.Position,
                            item,
                            MainGame.me.world_root,
                            Direction.IgnoreDirection,
                            1f,
                            -1,
                            true,
                            true);

                        if (created != null)
                        {
                            created.zone_id = entry.ZoneId ?? string.Empty;
                            if (created.res != null)
                            {
                                created.res.drop_zone_id = created.zone_id;
                            }
                        }
                    }
                }

                for (int i = 0; i < existingDrops.Count; i++)
                {
                    DropResGameObject drop = existingDrops[i];
                    if (drop == null || matchedDrops.Contains(drop))
                        continue;

                    if (drop.res != null)
                    {
                        WorldMap.OnDropItemRemoved(drop.res);
                    }
                    drop.is_collected = true;
                    drop.DestroyLinkedHint();
                    list.drops.Remove(drop);
                    Destroy(drop.gameObject);
                }
            }
            finally
            {
                CombatSync.PopDropCreateSuppression();
            }
        }

        private static DropResGameObject FindMatchingExistingDrop(
            List<DropResGameObject> existingDrops,
            HashSet<DropResGameObject> matchedDrops,
            Item incoming,
            Vector3 incomingPosition)
        {
            DropResGameObject nearest = null;
            float nearestDistance = float.MaxValue;

            for (int i = 0; i < existingDrops.Count; i++)
            {
                DropResGameObject candidate = existingDrops[i];
                if (candidate == null ||
                    candidate.is_collected ||
                    matchedDrops.Contains(candidate) ||
                    !RepresentsSameDrop(candidate.res, incoming))
                {
                    continue;
                }

                float distance = (candidate.transform.position - incomingPosition).sqrMagnitude;
                if (distance < nearestDistance)
                {
                    nearest = candidate;
                    nearestDistance = distance;
                }
            }

            return nearest;
        }

        private static bool RepresentsSameDrop(Item current, Item incoming)
        {
            if (current == null || incoming == null)
                return false;

            if (!string.Equals(current.id, incoming.id, StringComparison.Ordinal) ||
                current.value != incoming.value ||
                current.worker_unique_id != incoming.worker_unique_id)
            {
                return false;
            }

            // CombatSync's immediate create packet carries only id/value, while
            // the later inventory snapshot includes the generated corpse subtype.
            // Treat an omitted subtype as the same drop so that snapshot enriches
            // the existing object instead of recreating it and replaying drop SFX.
            return string.IsNullOrEmpty(current.sub_name) ||
                   string.IsNullOrEmpty(incoming.sub_name) ||
                   string.Equals(current.sub_name, incoming.sub_name, StringComparison.Ordinal);
        }

        private static void RedrawInventoryUi()
        {
            try
            {
                GUIElements gui = GUIElements.me;
                if (gui?.hud != null)
                {
                    gui.hud.Redraw();
                    gui.hud.Update();
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[InventorySync] Failed to redraw inventory UI: {ex.Message}");
            }
        }

        private uint IncrementChestRevision(long uniqueId)
        {
            chestRevisions.TryGetValue(uniqueId, out uint revision);
            revision++;
            chestRevisions[uniqueId] = revision;
            return revision;
        }

        private static string BuildChestRevisionToken(WorldGameObject chest)
        {
            string json = chest?.data?.ToJSON() ?? string.Empty;
            unchecked
            {
                const ulong offset = 14695981039346656037UL;
                const ulong prime = 1099511628211UL;
                ulong hash = offset;
                for (int i = 0; i < json.Length; i++)
                {
                    hash ^= json[i];
                    hash *= prime;
                }
                return hash.ToString("X16");
            }
        }

        private static List<PersonalItemState> CapturePersonalItemState(Item playerData)
        {
            var result = new List<PersonalItemState>();
            if (playerData == null)
                return result;

            var visited = new HashSet<Item>(ReferenceComparer<Item>.Instance);
            CapturePersonalItemList(
                playerData.inventory,
                playerData,
                inSecondaryInventory: false,
                result,
                visited);
            CapturePersonalItemList(
                playerData.secondary_inventory,
                playerData,
                inSecondaryInventory: true,
                result,
                visited);
            return result;
        }

        private static void CapturePersonalItemList(
            List<Item> items,
            Item parent,
            bool inSecondaryInventory,
            List<PersonalItemState> result,
            HashSet<Item> visited)
        {
            if (items == null || result == null || visited == null)
                return;

            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (item == null || !visited.Add(item))
                    continue;

                result.Add(new PersonalItemState
                {
                    Reference = item,
                    ParentReference = parent,
                    ItemId = item.id ?? string.Empty,
                    Json = SerializeItem(item),
                    Value = item.value,
                    EquippedAs = item.equipped_as,
                    InSecondaryInventory = inSecondaryInventory
                });
                CapturePersonalItemList(
                    item.inventory,
                    item,
                    inSecondaryInventory: false,
                    result,
                    visited);
                CapturePersonalItemList(
                    item.secondary_inventory,
                    item,
                    inSecondaryInventory: true,
                    result,
                    visited);
            }
        }

        private void RollBackPendingChestMoves(string reason)
        {
            if (pendingChestMoves.Count == 0)
                return;

            var pendingMoves = new List<ChestMoveCapture>(pendingChestMoves.Values);
            for (int i = 0; i < pendingMoves.Count; i++)
            {
                ChestMoveCapture pending = pendingMoves[i];
                bool restored = RestorePersonalInventoryDelta(pending);
                if (restored)
                {
                    CoopMod.Logger.LogWarning(
                        $"[InventorySync] Reversed pending optimistic chest " +
                        $"operation {pending.OperationId} during {reason}");
                }
                else
                {
                    CoopMod.Logger.LogError(
                        $"[InventorySync] Could not reverse pending optimistic " +
                        $"chest operation {pending.OperationId} during {reason}");
                }
            }

            pendingChestMoves.Clear();
        }

        private bool RestorePersonalInventoryDelta(ChestMoveCapture pending)
        {
            if (pending?.PlayerItemsBefore == null ||
                pending.PlayerItemsAfter == null ||
                pending.PlayerEquippedItemsBefore == null ||
                pending.PlayerEquippedItemsAfter == null)
            {
                return false;
            }

            Item playerData = MainGame.me?.player?.data;
            GameSave save = MainGame.me?.save;
            if (playerData == null || save == null)
                return false;

            EnsureItemLists(playerData);
            int optimisticCountDelta =
                GetCapturedPersonalItemCount(
                    pending.PlayerItemsAfter,
                    pending.ItemId) -
                GetCapturedPersonalItemCount(
                    pending.PlayerItemsBefore,
                    pending.ItemId);
            if (optimisticCountDelta == 0 && pending.RequestedCount > 0)
            {
                optimisticCountDelta = pending.RequestedToChest
                    ? -pending.RequestedCount
                    : pending.RequestedCount;
            }
            int currentCountBeforeRollback = playerData.GetTotalCount(
                pending.ItemId,
                true);
            long expectedCountLong =
                (long)currentCountBeforeRollback - optimisticCountDelta;
            int expectedCount = expectedCountLong <= 0L
                ? 0
                : expectedCountLong >= int.MaxValue
                    ? int.MaxValue
                    : (int)expectedCountLong;

            bool referenceRollbackSucceeded = true;
            isApplyingRemote = true;
            try
            {
                ReversePersonalItemDelta(
                    playerData,
                    pending.PlayerItemsBefore,
                    pending.PlayerItemsAfter);
            }
            catch (Exception ex)
            {
                referenceRollbackSucceeded = false;
                CoopMod.Logger.LogWarning(
                    $"[InventorySync] Reference-based personal inventory " +
                    $"rollback failed; applying semantic item-count fallback: " +
                    ex.Message);
            }

            try
            {
                ReverseEquippedItemDelta(
                    save,
                    pending.PlayerEquippedItemsBefore,
                    pending.PlayerEquippedItemsAfter);
            }
            catch (Exception ex)
            {
                referenceRollbackSucceeded = false;
                CoopMod.Logger.LogWarning(
                    $"[InventorySync] Could not restore equipped-item slots " +
                    $"during chest rollback: {ex.Message}");
            }

            bool countRollbackSucceeded;
            try
            {
                countRollbackSucceeded = RepairRejectedPersonalItemCount(
                    playerData,
                    pending,
                    expectedCount);
            }
            catch (Exception ex)
            {
                countRollbackSucceeded = false;
                CoopMod.Logger.LogError(
                    $"[InventorySync] Semantic item-count fallback failed for " +
                    $"rejected chest move: {ex}");
            }
            finally
            {
                isApplyingRemote = false;
            }

            if (!countRollbackSucceeded)
            {
                CoopMod.Logger.LogError(
                    $"[InventorySync] Could not reverse the optimistic personal " +
                    $"inventory delta for '{pending.ItemId}': current=" +
                    $"{playerData.GetTotalCount(pending.ItemId, true)}, " +
                    $"expected={expectedCount}");
                return false;
            }

            if (!referenceRollbackSucceeded)
            {
                CoopMod.Logger.LogWarning(
                    $"[InventorySync] Recovered rejected chest move " +
                    $"'{pending.ItemId}' with semantic count verification after " +
                    "the reference rollback failed");
            }

            try
            {
                RedrawInventoryUi();
                pending.Gui?.player_panel?.Redraw();
                TooltipsManager.Redraw();
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogDebug(
                    $"[InventorySync] Inventory UI redraw after chest rollback " +
                    $"was deferred: {ex.Message}");
            }

            JoinerProfileManager.NotifyPersonalInventoryChanged(
                "chest move delta rollback");
            return true;
        }

        private static int GetCapturedPersonalItemCount(
            List<PersonalItemState> states,
            string itemId)
        {
            if (states == null || string.IsNullOrEmpty(itemId))
                return 0;

            long total = 0L;
            for (int i = 0; i < states.Count; i++)
            {
                PersonalItemState state = states[i];
                if (state != null && state.Value > 0 &&
                    string.Equals(
                        state.ItemId,
                        itemId,
                        StringComparison.Ordinal))
                {
                    total += state.Value;
                }
            }

            return total >= int.MaxValue ? int.MaxValue : (int)total;
        }

        private static bool RepairRejectedPersonalItemCount(
            Item playerData,
            ChestMoveCapture pending,
            int expectedCount)
        {
            if (playerData == null || pending == null ||
                string.IsNullOrEmpty(pending.ItemId))
            {
                return false;
            }

            int currentCount = playerData.GetTotalCount(pending.ItemId, true);
            if (currentCount == expectedCount)
                return true;

            if (currentCount > expectedCount)
            {
                int leakedCount = currentCount - expectedCount;
                if (!playerData.RemoveItem(
                        pending.ItemId,
                        leakedCount,
                        null))
                {
                    return false;
                }

                CoopMod.Logger.LogWarning(
                    $"[InventorySync] Removed leaked optimistic withdrawal " +
                    $"'{pending.ItemId}' x{leakedCount} after reference-based " +
                    "chest rollback could not identify the moved stack");
            }
            else
            {
                int missingCount = expectedCount - currentCount;
                Item restored = DeserializeItem(pending.ItemJson);
                if (restored == null || restored.IsEmpty() ||
                    !string.Equals(
                        restored.id,
                        pending.ItemId,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                restored.value = missingCount;
                restored.equipped_as = ItemDefinition.EquipmentType.None;
                if (!playerData.AddItem(restored, true))
                    return false;

                CoopMod.Logger.LogWarning(
                    $"[InventorySync] Restored rejected optimistic deposit " +
                    $"'{pending.ItemId}' x{missingCount} by serialized value " +
                    "after reference-based chest rollback lost the moved stack");
            }

            return playerData.GetTotalCount(pending.ItemId, true) ==
                   expectedCount;
        }

        private static void ReversePersonalItemDelta(
            Item playerData,
            List<PersonalItemState> before,
            List<PersonalItemState> after)
        {
            // ChestGUI runs synchronously, so the before/after reference identity
            // isolates the optimistic move from grants or other inventory mutations
            // that may arrive while the host decision is in flight. Reversing only
            // that delta preserves every unrelated item added or changed meanwhile.
            for (int i = 0; i < before.Count; i++)
            {
                PersonalItemState beforeState = before[i];
                PersonalItemState afterState = FindPersonalItemState(
                    after,
                    beforeState.Reference);
                Item current = FindCurrentPersonalItem(
                    playerData,
                    beforeState.Reference,
                    out List<Item> currentOwner);

                if (afterState == null)
                {
                    // The optimistic deposit removed this whole stack. Restore a
                    // clone only if the exact runtime item has not independently
                    // returned to personal inventory. A removed parent clone already
                    // contains its nested contents, so descendants are skipped.
                    if (current == null &&
                        !HasRemovedAncestor(beforeState, before, after))
                    {
                        Item restored = DeserializeItem(beforeState.Json);
                        Item currentParent = ResolveCurrentPersonalParent(
                            playerData,
                            beforeState.ParentReference);
                        if (currentParent == null)
                        {
                            throw new InvalidOperationException(
                                "the optimistic chest move removed an unknown " +
                                "personal inventory parent");
                        }

                        EnsureItemLists(currentParent);
                        List<Item> destination = beforeState.InSecondaryInventory
                            ? currentParent.secondary_inventory
                            : currentParent.inventory;
                        destination.Add(restored);
                    }
                    continue;
                }

                if (current == null)
                    continue;

                int optimisticValueDelta = afterState.Value - beforeState.Value;
                if (optimisticValueDelta != 0)
                {
                    long restoredValue = (long)current.value - optimisticValueDelta;
                    if (restoredValue <= 0L)
                    {
                        currentOwner.Remove(current);
                        continue;
                    }

                    current.value = restoredValue > int.MaxValue
                        ? int.MaxValue
                        : (int)restoredValue;
                }

                if (beforeState.EquippedAs != afterState.EquippedAs &&
                    current.equipped_as == afterState.EquippedAs)
                {
                    current.equipped_as = beforeState.EquippedAs;
                }
            }

            for (int i = 0; i < after.Count; i++)
            {
                PersonalItemState afterState = after[i];
                if (FindPersonalItemState(before, afterState.Reference) != null ||
                    HasAddedAncestor(afterState, before, after))
                {
                    continue;
                }

                Item current = FindCurrentPersonalItem(
                    playerData,
                    afterState.Reference,
                    out List<Item> currentOwner);
                if (current == null)
                    continue;

                // A newly withdrawn stack can have received an unrelated grant
                // while approval was pending. Remove only the optimistic quantity.
                long restoredValue = (long)current.value - afterState.Value;
                if (restoredValue <= 0L)
                {
                    currentOwner.Remove(current);
                }
                else
                {
                    current.value = restoredValue > int.MaxValue
                        ? int.MaxValue
                        : (int)restoredValue;
                }
            }
        }

        private static bool HasRemovedAncestor(
            PersonalItemState state,
            List<PersonalItemState> before,
            List<PersonalItemState> after)
        {
            Item parent = state.ParentReference;
            while (parent != null)
            {
                PersonalItemState parentBefore =
                    FindPersonalItemState(before, parent);
                if (parentBefore == null)
                    return false;
                if (FindPersonalItemState(after, parent) == null)
                    return true;
                parent = parentBefore.ParentReference;
            }

            return false;
        }

        private static bool HasAddedAncestor(
            PersonalItemState state,
            List<PersonalItemState> before,
            List<PersonalItemState> after)
        {
            Item parent = state.ParentReference;
            while (parent != null)
            {
                PersonalItemState parentAfter = FindPersonalItemState(after, parent);
                if (parentAfter == null)
                    return false;
                if (FindPersonalItemState(before, parent) == null)
                    return true;
                parent = parentAfter.ParentReference;
            }

            return false;
        }

        private static PersonalItemState FindPersonalItemState(
            List<PersonalItemState> states,
            Item reference)
        {
            for (int i = 0; i < states.Count; i++)
            {
                if (ReferenceEquals(states[i].Reference, reference))
                    return states[i];
            }

            return null;
        }

        private static Item FindCurrentPersonalItem(
            Item playerData,
            Item reference,
            out List<Item> owner)
        {
            owner = null;
            if (playerData == null || reference == null)
                return null;

            var visited = new HashSet<Item>(ReferenceComparer<Item>.Instance);
            Item found = FindCurrentPersonalItemRecursive(
                playerData.inventory,
                reference,
                visited,
                out owner);
            return found ?? FindCurrentPersonalItemRecursive(
                playerData.secondary_inventory,
                reference,
                visited,
                out owner);
        }

        private static Item FindCurrentPersonalItemRecursive(
            List<Item> items,
            Item reference,
            HashSet<Item> visited,
            out List<Item> owner)
        {
            owner = null;
            if (items == null)
                return null;

            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (item == null)
                    continue;
                if (ReferenceEquals(item, reference))
                {
                    owner = items;
                    return item;
                }
                if (!visited.Add(item))
                    continue;

                Item found = FindCurrentPersonalItemRecursive(
                    item.inventory,
                    reference,
                    visited,
                    out owner);
                if (found != null)
                    return found;
                found = FindCurrentPersonalItemRecursive(
                    item.secondary_inventory,
                    reference,
                    visited,
                    out owner);
                if (found != null)
                    return found;
            }

            return null;
        }

        private static Item ResolveCurrentPersonalParent(
            Item playerData,
            Item capturedParent)
        {
            if (ReferenceEquals(playerData, capturedParent))
                return playerData;

            return FindCurrentPersonalItem(
                playerData,
                capturedParent,
                out List<Item> ignoredOwner);
        }

        private static void ReverseEquippedItemDelta(
            GameSave save,
            string[] before,
            string[] after)
        {
            if (save?.equipped_items == null)
                return;

            int slotCount = Math.Min(
                save.equipped_items.Length,
                Math.Min(before.Length, after.Length));
            for (int i = 0; i < slotCount; i++)
            {
                if (!string.Equals(before[i], after[i], StringComparison.Ordinal) &&
                    string.Equals(save.equipped_items[i], after[i], StringComparison.Ordinal))
                {
                    save.equipped_items[i] = before[i];
                }
            }
        }

        private void QueueOpenChestRedraw(long uniqueId)
        {
            try
            {
                ChestGUI gui = GUIElements.me?.chest;
                if (gui == null || !gui.is_shown)
                    return;

                if (chestObjectField == null)
                {
                    chestObjectField = typeof(ChestGUI).GetField(
                        "_chest_obj",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                }

                WorldGameObject openChest =
                    chestObjectField?.GetValue(gui) as WorldGameObject;
                if (openChest != null && openChest.unique_id == uniqueId)
                    pendingOpenChestRedrawUniqueId = uniqueId;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[InventorySync] Could not queue synchronized chest redraw: {ex.Message}");
            }
        }

        private void FlushPendingOpenChestRedraw()
        {
            long uniqueId = pendingOpenChestRedrawUniqueId;
            if (uniqueId == 0L)
                return;

            pendingOpenChestRedrawUniqueId = 0L;
            RedrawOpenChest(uniqueId);
        }

        private void RedrawOpenChest(long uniqueId)
        {
            try
            {
                ChestGUI gui = GUIElements.me?.chest;
                if (gui == null || !gui.is_shown)
                    return;

                if (chestObjectField == null)
                {
                    chestObjectField = typeof(ChestGUI).GetField(
                        "_chest_obj",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                }
                WorldGameObject openChest =
                    chestObjectField?.GetValue(gui) as WorldGameObject;
                if (openChest == null || openChest.unique_id != uniqueId)
                    return;

                if (chestFullRedrawMethod == null)
                {
                    chestFullRedrawMethod = typeof(ChestGUI).GetMethod(
                        "FullRedrawPanels",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                }
                chestFullRedrawMethod?.Invoke(gui, new object[] { -1 });
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[InventorySync] Could not redraw open synchronized chest: {ex.Message}");
            }
        }

        private static string SerializeItem(Item item)
        {
            return item != null ? item.ToJSON() : string.Empty;
        }

        private static string SerializeWgoInventoryData(WorldGameObject wgo, bool isLocalPlayerInventory)
        {
            if (wgo?.data == null)
                return string.Empty;

            if (!isLocalPlayerInventory)
                return SerializeItem(wgo.data);

            var inventoryOnly = new Item
            {
                inventory_size = wgo.data.inventory_size,
                inventory = wgo.data.inventory ?? new List<Item>()
            };

            return SerializeItem(inventoryOnly);
        }

        private static Item DeserializeItem(string itemJson)
        {
            if (string.IsNullOrEmpty(itemJson))
                return new Item();

            Item item = JsonUtility.FromJson<Item>(itemJson);
            EnsureItemLists(item);
            return item;
        }

        private static List<string> SerializeItemList(List<Item> items)
        {
            var result = new List<string>();
            if (items == null)
                return result;

            for (int i = 0; i < items.Count; i++)
            {
                result.Add(SerializeItem(items[i]));
            }

            return result;
        }

        private static List<Item> DeserializeItemList(List<string> itemJsons)
        {
            var result = new List<Item>();
            if (itemJsons == null)
                return result;

            for (int i = 0; i < itemJsons.Count; i++)
            {
                Item item = DeserializeItem(itemJsons[i]);
                if (item != null)
                {
                    result.Add(item);
                }
            }

            return result;
        }

        private static void EnsureItemLists(Item item)
        {
            if (item == null)
                return;

            if (item.inventory == null)
            {
                item.inventory = new List<Item>();
            }

            if (item.secondary_inventory == null)
            {
                item.secondary_inventory = new List<Item>();
            }

            for (int i = 0; i < item.inventory.Count; i++)
            {
                EnsureItemLists(item.inventory[i]);
            }

            for (int i = 0; i < item.secondary_inventory.Count; i++)
            {
                EnsureItemLists(item.secondary_inventory[i]);
            }
        }

        private static bool IsLocalPlayerWgo(WorldGameObject wgo)
        {
            return wgo != null && MainGame.me?.player != null && wgo == MainGame.me.player;
        }

        private static string BuildWgoKey(bool isLocalPlayerInventory, long uniqueId, string objId, string customTag, Vector3 position)
        {
            if (isLocalPlayerInventory)
                return "player:local";

            if (uniqueId > 0L)
                return "u:" + uniqueId;

            if (!string.IsNullOrEmpty(customTag))
                return "t:" + customTag;

            return string.Format("p:{0}:{1:F1}:{2:F1}", objId ?? string.Empty, position.x, position.y);
        }

        private static string BuildToolbarFingerprint(List<string> toolbarItems)
        {
            if (toolbarItems == null)
                return string.Empty;

            var builder = new StringBuilder();
            for (int i = 0; i < toolbarItems.Count; i++)
            {
                builder.Append(toolbarItems[i] ?? string.Empty).Append('|');
            }

            return builder.ToString();
        }

        private static string BuildDropFingerprint(InventoryDropEntry entry)
        {
            return string.Format("{0}|{1:F1}|{2:F1}|{3}",
                entry.ItemJson ?? string.Empty,
                entry.Position.x,
                entry.Position.y,
                entry.ZoneId ?? string.Empty);
        }

        private static string BuildDropsFingerprint(List<InventoryDropEntry> drops)
        {
            if (drops == null)
                return string.Empty;

            var builder = new StringBuilder(drops.Count * 64);
            for (int i = 0; i < drops.Count; i++)
            {
                builder.Append(drops[i].Fingerprint).Append('\n');
            }

            return builder.ToString();
        }

        private static byte[] SerializeSnapshot(InventorySnapshot snapshot)
        {
            using (var stream = new MemoryStream(4096))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(snapshot.Version);
                writer.Write(snapshot.OriginSteamId);
                writer.Write(snapshot.Sequence);
                writer.Write(snapshot.IsFull);
                writer.Write(snapshot.HasToolbar);
                if (snapshot.HasToolbar)
                {
                    writer.Write(snapshot.ToolbarItems.Count);
                    for (int i = 0; i < snapshot.ToolbarItems.Count; i++)
                    {
                        writer.Write(snapshot.ToolbarItems[i] ?? string.Empty);
                    }
                }

                writer.Write(snapshot.WgoEntries.Count);
                for (int i = 0; i < snapshot.WgoEntries.Count; i++)
                {
                    WriteWgoEntry(writer, snapshot.WgoEntries[i]);
                }

                writer.Write(snapshot.HasDrops);
                if (snapshot.HasDrops)
                {
                    writer.Write(snapshot.Drops.Count);
                    for (int i = 0; i < snapshot.Drops.Count; i++)
                    {
                        WriteDropEntry(writer, snapshot.Drops[i]);
                    }
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        private static InventorySnapshot DeserializeSnapshot(byte[] payload)
        {
            using (var stream = new MemoryStream(payload, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                var snapshot = new InventorySnapshot
                {
                    Version = reader.ReadByte()
                };

                if (snapshot.Version != PayloadVersion)
                {
                    throw new InvalidDataException($"Unsupported inventory sync payload version {snapshot.Version}");
                }

                snapshot.OriginSteamId = reader.ReadUInt64();
                snapshot.Sequence = reader.ReadUInt32();
                snapshot.IsFull = reader.ReadBoolean();
                snapshot.HasToolbar = reader.ReadBoolean();
                snapshot.ToolbarItems = new List<string>();
                if (snapshot.HasToolbar)
                {
                    int toolbarCount = ReadSafeCount(reader, "toolbar slot count", MaxToolbarSlots);
                    for (int i = 0; i < toolbarCount; i++)
                    {
                        snapshot.ToolbarItems.Add(reader.ReadString());
                    }
                }

                int wgoCount = ReadSafeCount(reader, "WGO inventory count", MaxWgoEntries);
                snapshot.WgoEntries = new List<InventoryWgoEntry>(wgoCount);
                for (int i = 0; i < wgoCount; i++)
                {
                    snapshot.WgoEntries.Add(ReadWgoEntry(reader));
                }

                snapshot.HasDrops = reader.ReadBoolean();
                snapshot.Drops = new List<InventoryDropEntry>();
                if (snapshot.HasDrops)
                {
                    int dropCount = ReadSafeCount(reader, "drop count", MaxDropEntries);
                    snapshot.Drops = new List<InventoryDropEntry>(dropCount);
                    for (int i = 0; i < dropCount; i++)
                    {
                        snapshot.Drops.Add(ReadDropEntry(reader));
                    }
                }

                snapshot.ToolbarFingerprint = BuildToolbarFingerprint(snapshot.ToolbarItems);
                snapshot.DropsFingerprint = BuildDropsFingerprint(snapshot.Drops);

                var fingerprintBuilder = new StringBuilder();
                for (int i = 0; i < snapshot.WgoEntries.Count; i++)
                {
                    InventoryWgoEntry entry = snapshot.WgoEntries[i];
                    fingerprintBuilder.Append(entry.Key).Append('=').Append(entry.Fingerprint).Append('\n');
                }

                snapshot.Fingerprint = snapshot.IsFull + "\nTB:" + snapshot.ToolbarFingerprint +
                                       "\nWGO:\n" + fingerprintBuilder +
                                       "\nDROPS:" + snapshot.DropsFingerprint;
                snapshot.HasChanges = snapshot.IsFull ||
                                      snapshot.HasToolbar ||
                                      snapshot.WgoEntries.Count > 0 ||
                                      snapshot.HasDrops;
                return snapshot;
            }
        }

        private static void WriteWgoEntry(BinaryWriter writer, InventoryWgoEntry entry)
        {
            writer.Write(entry.UniqueId);
            writer.Write(entry.ObjId ?? string.Empty);
            writer.Write(entry.CustomTag ?? string.Empty);
            writer.Write(entry.IsLocalPlayerInventory);
            WriteVector3(writer, entry.Position);
            writer.Write(entry.ItemJson ?? string.Empty);
            writer.Write(entry.SecondaryItemJsons.Count);
            for (int i = 0; i < entry.SecondaryItemJsons.Count; i++)
            {
                writer.Write(entry.SecondaryItemJsons[i] ?? string.Empty);
            }
        }

        private static InventoryWgoEntry ReadWgoEntry(BinaryReader reader)
        {
            var entry = new InventoryWgoEntry
            {
                UniqueId = reader.ReadInt64(),
                ObjId = reader.ReadString(),
                CustomTag = reader.ReadString(),
                IsLocalPlayerInventory = reader.ReadBoolean(),
                Position = ReadVector3(reader),
                ItemJson = reader.ReadString()
            };

            int secondaryCount = ReadSafeCount(reader, "secondary inventory count", 256);
            entry.SecondaryItemJsons = new List<string>(secondaryCount);
            for (int i = 0; i < secondaryCount; i++)
            {
                entry.SecondaryItemJsons.Add(reader.ReadString());
            }

            entry.Key = BuildWgoKey(entry.IsLocalPlayerInventory, entry.UniqueId, entry.ObjId, entry.CustomTag, entry.Position);
            Item fingerprintItem = DeserializeItem(entry.ItemJson);
            if (fingerprintItem != null)
            {
                fingerprintItem.secondary_inventory =
                    DeserializeItemList(entry.SecondaryItemJsons);
            }
            entry.Fingerprint = BuildCheapItemFingerprint(fingerprintItem);
            return entry;
        }

        private static void WriteDropEntry(BinaryWriter writer, InventoryDropEntry entry)
        {
            writer.Write(entry.ItemJson ?? string.Empty);
            WriteVector3(writer, entry.Position);
            writer.Write(entry.ZoneId ?? string.Empty);
        }

        private static InventoryDropEntry ReadDropEntry(BinaryReader reader)
        {
            var entry = new InventoryDropEntry
            {
                ItemJson = reader.ReadString(),
                Position = ReadVector3(reader),
                ZoneId = reader.ReadString()
            };
            entry.Fingerprint = BuildDropFingerprint(entry);
            return entry;
        }

        private static void WriteVector3(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        private static Vector3 ReadVector3(BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private static int ReadSafeCount(BinaryReader reader, string label, int max)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > max)
                throw new InvalidDataException($"Invalid {label}: {count}");

            return count;
        }

        private sealed class InventorySnapshot
        {
            public byte Version;
            public ulong OriginSteamId;
            public uint Sequence;
            public bool IsFull;
            public bool HasToolbar;
            public bool HasDrops;
            public bool HasChanges;
            public List<string> ToolbarItems;
            public List<InventoryWgoEntry> WgoEntries;
            public List<InventoryDropEntry> Drops;
            public string ToolbarFingerprint = string.Empty;
            public string DropsFingerprint = string.Empty;
            public string Fingerprint = string.Empty;
            public int DiagnosticEligibleWgoCount;
            public int DiagnosticFullEntryCount;
            public int DiagnosticExplicitDirtyCount;
            public int DiagnosticMissingBaselineCount;
            public int DiagnosticFingerprintChangedCount;
            public List<string> DiagnosticDirtyEntries;
            public string DiagnosticDropsChange = string.Empty;
        }

        private sealed class InventoryWgoEntry
        {
            public string Key;
            public bool IsLocalPlayerInventory;
            public long UniqueId;
            public string ObjId;
            public string CustomTag;
            public Vector3 Position;
            public string ItemJson;
            public List<string> SecondaryItemJsons = new List<string>();
            public string Fingerprint;
        }

        private sealed class InventoryDropEntry
        {
            public string ItemJson;
            public Vector3 Position;
            public string ZoneId;
            public string Fingerprint;
        }

        /// <summary>
        /// Lightweight per-WGO fingerprint used for fast change detection.
        /// Stores only the key, the cheap structural fingerprint, and a reference
        /// to the source WGO (so changed entries can be JSON-serialized on demand).
        /// </summary>
        private struct WgoCheapFingerprint
        {
            public string Key;
            public string Fingerprint;
            public WorldGameObject Wgo;
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T>
            where T : class
        {
            public static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();

            public bool Equals(T x, T y) => ReferenceEquals(x, y);

            public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}

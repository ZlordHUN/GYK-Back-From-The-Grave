using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Synchronizes live WorldGameObject lifecycle and state after the initial host save transfer.
    /// Uses the game's own SerializableWGO format so object mutations stay aligned with save data:
    /// spawn/despawn/replace, transform, tags, params/item payload, craft/movement state, variation,
    /// skin, interaction events, worker links, porter data, chunk state, and zone/GD point context.
    /// </summary>
    public class WGOStateSync : MonoBehaviour
    {
        private const byte PayloadVersion = 1;
        private const byte EnvelopeSnapshot = 0;
        private const byte EnvelopeBegin = 1;
        private const byte EnvelopeChunk = 2;
        private const byte EnvelopeEnd = 3;
        private const float SyncIntervalSeconds = 15f;
        private const float InitialSnapshotDelaySeconds = 20f;
        private const float EchoSuppressSeconds = 0.5f;
        private const int CaptureObjectsPerFrame = 32;
        private const double CaptureBudgetMilliseconds = 0.75;
        private const int ApplyObjectsPerFrame = 12;
        private const double ApplyBudgetMilliseconds = 0.75;
        private const int ChunkPayloadBytes = 32 * 1024;
        private const int MaxSnapshotBytes = 32 * 1024 * 1024;
        private const int MaxWgoEntries = 12000;
        private const int MaxRemovedEntries = 12000;

        private static WGOStateSync _instance;
        public static WGOStateSync Instance => _instance;
        public static bool IsProcessingRemote { get; private set; }

        internal static void SetProcessingRemote(bool value)
        {
            IsProcessingRemote = value;
        }

        private sealed class ReceiveState
        {
            public ulong OriginSteamId;
            public ulong TransferId;
            public int TotalBytes;
            public int ChunkSize;
            public int TotalChunks;
            public int ReceivedChunks;
            public byte[][] Chunks;
            public DateTime Started;
        }

        private sealed class CaptureState
        {
            public bool FullSnapshot;
            public bool Force;
            public bool Broadcast;
            public List<WorldGameObject> WorldObjects;
            public int Index;
            public List<WgoStateEntry> Entries = new List<WgoStateEntry>();
            public Dictionary<string, ulong> CurrentFingerprints = new Dictionary<string, ulong>();
            public DateTime Started;
        }

        private sealed class ApplyState
        {
            public WgoStateSnapshot Snapshot;
            public bool ReforwardAfterApply;
            public int EntryIndex;
            public HashSet<string> RemoteKeys = new HashSet<string>();
            public DateTime Started;
        }

        private readonly Dictionary<ulong, uint> lastReceivedSequenceByOrigin = new Dictionary<ulong, uint>();
        private readonly Dictionary<string, ulong> authoritativeFingerprints = new Dictionary<string, ulong>();
        private readonly Dictionary<string, ReceiveState> pendingReceives = new Dictionary<string, ReceiveState>();

        private bool isSyncEnabled;
        private bool hasAuthoritativeBaseline;
        private bool isApplyingRemote;
        private float lastSendTime = -SyncIntervalSeconds;
        private float suppressSendUntil;
        private float initialSnapshotAllowedTime;
        private uint nextSequence;
        private ulong nextTransferId;
        private ulong lastSentCompleteFingerprint;
        private bool hasLastSentCompleteFingerprint;
        private CaptureState activeCapture;
        private ApplyState activeApply;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
            CoopMod.Logger.LogInfo("[WGOStateSync] Initialized");
        }

        private void OnEnable()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnWGOStateSyncReceived -= OnWGOStateSyncReceived;
                SteamP2PManager.Instance.OnWGOStateSyncReceived += OnWGOStateSyncReceived;
            }
        }

        private void OnDisable()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnWGOStateSyncReceived -= OnWGOStateSyncReceived;
            }
        }

        public void EnableSync()
        {
            if (ModConfig.EnableLiveWGOStateSync != null && !ModConfig.EnableLiveWGOStateSync.Value)
            {
                isSyncEnabled = false;
                hasAuthoritativeBaseline = false;
                pendingReceives.Clear();
                authoritativeFingerprints.Clear();
                lastReceivedSequenceByOrigin.Clear();
                activeCapture = null;
                activeApply = null;
                CoopMod.Logger.LogInfo("[WGOStateSync] Live full-world WGO sync disabled by Performance.EnableLiveWGOStateSyncOptimized");
                return;
            }

            isSyncEnabled = true;
            hasAuthoritativeBaseline = false;
            isApplyingRemote = false;
            lastSendTime = Time.realtimeSinceStartup - SyncIntervalSeconds + InitialSnapshotDelaySeconds;
            suppressSendUntil = 0f;
            initialSnapshotAllowedTime = Time.realtimeSinceStartup + InitialSnapshotDelaySeconds;
            nextSequence = 0;
            nextTransferId = 0UL;
            lastSentCompleteFingerprint = 0UL;
            hasLastSentCompleteFingerprint = false;
            activeCapture = null;
            activeApply = null;
            authoritativeFingerprints.Clear();
            lastReceivedSequenceByOrigin.Clear();
            pendingReceives.Clear();

            CoopMod.Logger.LogInfo($"[WGOStateSync] Sync enabled; first budgeted full snapshot delayed {InitialSnapshotDelaySeconds:F0}s");
        }

        public void DisableSync()
        {
            isSyncEnabled = false;
            isApplyingRemote = false;
            activeCapture = null;
            activeApply = null;
            pendingReceives.Clear();
            authoritativeFingerprints.Clear();
            lastReceivedSequenceByOrigin.Clear();
            CoopMod.Logger.LogInfo("[WGOStateSync] Sync disabled");
        }

        private void Update()
        {
            if (!isSyncEnabled)
                return;

            if (activeApply != null)
            {
                ProcessActiveApply();
                return;
            }

            if (isApplyingRemote)
                return;

            if (SyncBehaviour.GlobalRemoteApplyActive)
                return;

            if (activeCapture != null)
            {
                ProcessActiveCapture();
                return;
            }

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled || !CanUseWgoState())
                return;

            if (!onlineCoop.IsHost)
                return;

            float now = Time.realtimeSinceStartup;
            if (onlineCoop.IsHost && !hasAuthoritativeBaseline && now < initialSnapshotAllowedTime)
                return;

            if (now - lastSendTime < SyncIntervalSeconds || now < suppressSendUntil)
                return;

            SendLocalSnapshot(force: false);
        }

        public void SendLocalSnapshot(bool force)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled || !CanUseWgoState())
                return;

            bool fullSnapshot = onlineCoop.IsHost && (force || !hasAuthoritativeBaseline);
            if (!onlineCoop.IsHost && !hasAuthoritativeBaseline)
                return;

            if (fullSnapshot && !hasAuthoritativeBaseline && Time.realtimeSinceStartup < initialSnapshotAllowedTime)
                return;

            StartLocalCapture(fullSnapshot, force, onlineCoop.IsHost);
        }

        private void StartLocalCapture(bool fullSnapshot, bool force, bool broadcast)
        {
            if (activeCapture != null)
            {
                if (fullSnapshot && !activeCapture.FullSnapshot)
                {
                    activeCapture = null;
                }
                else
                {
                    activeCapture.Force |= force;
                    activeCapture.Broadcast |= broadcast;
                    return;
                }
            }

            List<WorldGameObject> worldObjects = WGORegistry.Instance?.SnapshotAll() ?? MainGame.me?.GetListOfWorldObjects();
            activeCapture = new CaptureState
            {
                FullSnapshot = fullSnapshot,
                Force = force,
                Broadcast = broadcast,
                WorldObjects = worldObjects ?? new List<WorldGameObject>(),
                Started = DateTime.UtcNow
            };

            CoopMod.Logger.LogDebug($"[WGOStateSync] Started budgeted {(fullSnapshot ? "full" : "delta")} capture over {activeCapture.WorldObjects.Count} WGOs");
        }

        private void ProcessActiveCapture()
        {
            CaptureState capture = activeCapture;
            if (capture == null)
                return;

            long startTicks = Stopwatch.GetTimestamp();
            int processed = 0;

            while (capture.Index < capture.WorldObjects.Count)
            {
                WorldGameObject wgo = capture.WorldObjects[capture.Index++];
                processed++;

                if (TryCaptureEntry(wgo, out WgoStateEntry entry))
                {
                    capture.Entries.Add(entry);
                    capture.CurrentFingerprints[entry.Key] = entry.Fingerprint;
                }

                if (processed >= CaptureObjectsPerFrame ||
                    ElapsedMilliseconds(startTicks) >= CaptureBudgetMilliseconds)
                {
                    return;
                }
            }

            activeCapture = null;
            CompleteLocalCapture(capture);
        }

        private void CompleteLocalCapture(CaptureState capture)
        {
            WgoStateSnapshot snapshot = BuildSnapshotFromEntries(capture);

            if (!snapshot.HasChanges)
            {
                lastSendTime = Time.realtimeSinceStartup;
                hasLastSentCompleteFingerprint = true;
                lastSentCompleteFingerprint = snapshot.CompleteFingerprint;
                return;
            }

            if (!capture.Force &&
                hasLastSentCompleteFingerprint &&
                snapshot.CompleteFingerprint == lastSentCompleteFingerprint)
            {
                lastSendTime = Time.realtimeSinceStartup;
                return;
            }

            snapshot.Sequence = ++nextSequence;

            byte[] snapshotBytes;
            try
            {
                snapshotBytes = SerializeSnapshot(snapshot);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[WGOStateSync] Failed to serialize snapshot: {ex.Message}");
                return;
            }

            if (snapshotBytes.Length > MaxSnapshotBytes)
            {
                CoopMod.Logger.LogWarning($"[WGOStateSync] Snapshot too large ({snapshotBytes.Length} bytes); not sending");
                return;
            }

            SendSnapshotBytes(snapshotBytes, snapshot.OriginSteamId, capture.Broadcast);

            lastSentCompleteFingerprint = snapshot.CompleteFingerprint;
            hasLastSentCompleteFingerprint = true;
            lastSendTime = Time.realtimeSinceStartup;

            if (capture.Broadcast)
            {
                ApplySnapshotToAuthoritativeBaseline(snapshot);
            }

            double elapsedMs = (DateTime.UtcNow - capture.Started).TotalMilliseconds;
            CoopMod.Logger.LogDebug($"[WGOStateSync] Sent {(snapshot.IsFull ? "full" : "delta")} snapshot entries={snapshot.Entries.Count} removed={snapshot.RemovedKeys.Count} bytes={snapshotBytes.Length} capture_ms={elapsedMs:F0}");
        }

        private void StartApplySnapshot(WgoStateSnapshot snapshot, bool reforwardAfterApply)
        {
            activeApply = new ApplyState
            {
                Snapshot = snapshot,
                ReforwardAfterApply = reforwardAfterApply,
                Started = DateTime.UtcNow
            };

            CoopMod.Logger.LogDebug($"[WGOStateSync] Started budgeted apply full={snapshot.IsFull} entries={snapshot.Entries.Count} removed={snapshot.RemovedKeys.Count}");
        }

        private void ProcessActiveApply()
        {
            ApplyState apply = activeApply;
            if (apply == null)
                return;

            long startTicks = Stopwatch.GetTimestamp();
            int processed = 0;
            isApplyingRemote = true;
            IsProcessingRemote = true;

            try
            {
                while (processed < ApplyObjectsPerFrame &&
                       ElapsedMilliseconds(startTicks) < ApplyBudgetMilliseconds)
                {
                    if (apply.EntryIndex < apply.Snapshot.Entries.Count)
                    {
                        WgoStateEntry entry = apply.Snapshot.Entries[apply.EntryIndex++];
                        apply.RemoteKeys.Add(entry.Key);
                        ApplyEntry(entry);
                        processed++;
                        continue;
                    }

                    if (apply.Snapshot.RemovedKeys.Count > 0)
                    {
                        CoopMod.Logger.LogWarning($"[WGOStateSync] Ignoring {apply.Snapshot.RemovedKeys.Count} inferred removals from remote snapshot; explicit WGO destruction sync handles deletes");
                    }

                    FinishActiveApply(apply);
                    return;
                }
            }
            finally
            {
                IsProcessingRemote = false;
                isApplyingRemote = false;
            }
        }

        private void FinishActiveApply(ApplyState apply)
        {
            activeApply = null;
            ApplySnapshotToAuthoritativeBaseline(apply.Snapshot);
            suppressSendUntil = Time.realtimeSinceStartup + EchoSuppressSeconds;
            MarkLastSentFingerprintUnknown();

            double elapsedMs = (DateTime.UtcNow - apply.Started).TotalMilliseconds;
            CoopMod.Logger.LogDebug($"[WGOStateSync] Finished budgeted apply full={apply.Snapshot.IsFull} entries={apply.Snapshot.Entries.Count} removed={apply.Snapshot.RemovedKeys.Count} apply_ms={elapsedMs:F0}");

            if (apply.ReforwardAfterApply)
            {
                SendLocalSnapshot(force: false);
            }
        }

        private void OnWGOStateSyncReceived(CSteamID senderID, byte[] payload)
        {
            if (!isSyncEnabled)
                return;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled || !CanUseWgoState())
                return;

            if (payload == null || payload.Length == 0)
                return;

            try
            {
                HandleEnvelope(senderID, payload);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[WGOStateSync] Failed to handle envelope: {ex.Message}");
            }
        }

        private void HandleEnvelope(CSteamID senderID, byte[] payload)
        {
            using (var stream = new MemoryStream(payload, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                byte kind = reader.ReadByte();
                switch (kind)
                {
                    case EnvelopeSnapshot:
                        HandleSnapshotBytes(senderID, ReadLengthPrefixedBytes(reader, MaxSnapshotBytes));
                        break;
                    case EnvelopeBegin:
                        HandleTransferBegin(senderID, reader);
                        break;
                    case EnvelopeChunk:
                        HandleTransferChunk(senderID, reader);
                        break;
                    case EnvelopeEnd:
                        HandleTransferEnd(senderID, reader);
                        break;
                    default:
                        CoopMod.Logger.LogWarning($"[WGOStateSync] Unknown envelope kind {kind}");
                        break;
                }
            }
        }

        private void HandleSnapshotBytes(CSteamID senderID, byte[] snapshotBytes)
        {
            if (snapshotBytes == null || snapshotBytes.Length == 0 || snapshotBytes.Length > MaxSnapshotBytes)
                return;

            WgoStateSnapshot snapshot;
            try
            {
                snapshot = DeserializeSnapshot(snapshotBytes);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[WGOStateSync] Failed to parse snapshot: {ex.Message}");
                return;
            }

            ulong localSteamId = GetLocalSteamId();
            if (snapshot.OriginSteamId == 0UL || snapshot.OriginSteamId == localSteamId)
                return;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null)
                return;

            if (!onlineCoop.IsHost && !IsExpectedHost(senderID))
                return;

            if (IsStaleSnapshot(snapshot))
                return;

            try
            {
                if (activeApply != null)
                    return;

                if (activeCapture != null)
                {
                    activeCapture = null;
                    MarkLastSentFingerprintUnknown();
                }

                StartApplySnapshot(snapshot, onlineCoop.IsHost);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[WGOStateSync] Failed to apply snapshot: {ex.Message}");
            }
        }

        private void HandleTransferBegin(CSteamID senderID, BinaryReader reader)
        {
            ulong originSteamId = reader.ReadUInt64();
            ulong transferId = reader.ReadUInt64();
            int totalBytes = reader.ReadInt32();
            int chunkSize = reader.ReadInt32();
            int totalChunks = reader.ReadInt32();

            if (totalBytes <= 0 || totalBytes > MaxSnapshotBytes || chunkSize <= 0 || chunkSize > ChunkPayloadBytes || totalChunks <= 0)
            {
                CoopMod.Logger.LogWarning($"[WGOStateSync] Invalid transfer begin: bytes={totalBytes} chunk={chunkSize} chunks={totalChunks}");
                return;
            }

            string key = BuildTransferKey(senderID, transferId);
            pendingReceives[key] = new ReceiveState
            {
                OriginSteamId = originSteamId,
                TransferId = transferId,
                TotalBytes = totalBytes,
                ChunkSize = chunkSize,
                TotalChunks = totalChunks,
                ReceivedChunks = 0,
                Chunks = new byte[totalChunks][],
                Started = DateTime.UtcNow
            };
        }

        private void HandleTransferChunk(CSteamID senderID, BinaryReader reader)
        {
            ulong transferId = reader.ReadUInt64();
            int chunkIndex = reader.ReadInt32();
            int chunkBytes = reader.ReadInt32();
            string key = BuildTransferKey(senderID, transferId);

            if (!pendingReceives.TryGetValue(key, out ReceiveState state))
                return;

            if (chunkIndex < 0 || chunkIndex >= state.TotalChunks || chunkBytes <= 0 || chunkBytes > state.ChunkSize)
                return;

            byte[] data = reader.ReadBytes(chunkBytes);
            if (data.Length != chunkBytes)
                return;

            if (state.Chunks[chunkIndex] == null)
            {
                state.Chunks[chunkIndex] = data;
                state.ReceivedChunks++;
            }
        }

        private void HandleTransferEnd(CSteamID senderID, BinaryReader reader)
        {
            ulong transferId = reader.ReadUInt64();
            string key = BuildTransferKey(senderID, transferId);

            if (!pendingReceives.TryGetValue(key, out ReceiveState state))
                return;

            pendingReceives.Remove(key);

            if (state.ReceivedChunks != state.TotalChunks)
            {
                CoopMod.Logger.LogWarning($"[WGOStateSync] Incomplete transfer {transferId}: {state.ReceivedChunks}/{state.TotalChunks}");
                return;
            }

            byte[] snapshotBytes = new byte[state.TotalBytes];
            int offset = 0;
            for (int i = 0; i < state.TotalChunks; i++)
            {
                byte[] chunk = state.Chunks[i];
                if (chunk == null || offset + chunk.Length > snapshotBytes.Length)
                    return;

                Buffer.BlockCopy(chunk, 0, snapshotBytes, offset, chunk.Length);
                offset += chunk.Length;
            }

            double elapsedMs = (DateTime.UtcNow - state.Started).TotalMilliseconds;
            CoopMod.Logger.LogDebug($"[WGOStateSync] Reassembled transfer {transferId} ({state.TotalBytes} bytes) in {elapsedMs:F0}ms");
            HandleSnapshotBytes(senderID, snapshotBytes);
        }

        private void SendSnapshotBytes(byte[] snapshotBytes, ulong originSteamId, bool broadcast)
        {
            if (snapshotBytes.Length <= ChunkPayloadBytes)
            {
                byte[] envelope = BuildSnapshotEnvelope(snapshotBytes);
                SendEnvelope(envelope, broadcast);
                return;
            }

            ulong transferId = BuildTransferId(originSteamId);
            int totalChunks = (snapshotBytes.Length + ChunkPayloadBytes - 1) / ChunkPayloadBytes;

            SendEnvelope(BuildBeginEnvelope(originSteamId, transferId, snapshotBytes.Length, totalChunks), broadcast);

            int offset = 0;
            for (int i = 0; i < totalChunks; i++)
            {
                int count = Math.Min(ChunkPayloadBytes, snapshotBytes.Length - offset);
                SendEnvelope(BuildChunkEnvelope(transferId, i, snapshotBytes, offset, count), broadcast);
                offset += count;
            }

            SendEnvelope(BuildEndEnvelope(transferId), broadcast);
        }

        private void SendEnvelope(byte[] envelope, bool broadcast)
        {
            if (broadcast)
            {
                SteamP2PManager.Instance?.BroadcastWGOStateSync(envelope);
            }
            else
            {
                SteamP2PManager.Instance?.SendWGOStateSyncToHost(envelope);
            }
        }

        private WgoStateSnapshot BuildSnapshotFromEntries(CaptureState capture)
        {
            capture.Entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            var snapshot = new WgoStateSnapshot
            {
                Version = PayloadVersion,
                OriginSteamId = GetLocalSteamId(),
                IsFull = capture.FullSnapshot,
                Entries = new List<WgoStateEntry>(),
                RemovedKeys = new List<string>(),
                CompleteFingerprints = capture.CurrentFingerprints,
                CompleteFingerprint = ComputeCompleteFingerprint(capture.Entries)
            };

            for (int i = 0; i < capture.Entries.Count; i++)
            {
                WgoStateEntry entry = capture.Entries[i];
                if (capture.FullSnapshot ||
                    !authoritativeFingerprints.TryGetValue(entry.Key, out ulong baselineFingerprint) ||
                    baselineFingerprint != entry.Fingerprint)
                {
                    snapshot.Entries.Add(entry);
                }
            }

            // Do not infer deletes from absence in a live WGO snapshot. The game's active WGO list
            // changes during cutscenes, zone transitions, and chunk activity, so missing-from-snapshot
            // is not a reliable destruction signal. Real object deletes use WGODestructionSync.

            snapshot.HasChanges = capture.FullSnapshot || snapshot.Entries.Count > 0 || snapshot.RemovedKeys.Count > 0;
            return snapshot;
        }

        private static bool TryCaptureEntry(WorldGameObject wgo, out WgoStateEntry entry)
        {
            entry = null;
            if (!ShouldSyncWgo(wgo))
                return false;

            try
            {
                SerializableWGO serialized = SerializableWGO.FromWGO(wgo);
                string json = JsonUtility.ToJson(serialized);
                string key = BuildWgoKey(serialized.unique_id, serialized.obj_id, serialized.custom_tag, serialized.position);
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(json))
                    return false;

                entry = new WgoStateEntry
                {
                    Key = key,
                    UniqueId = serialized.unique_id,
                    ObjId = serialized.obj_id ?? string.Empty,
                    CustomTag = serialized.custom_tag ?? string.Empty,
                    Position = serialized.position,
                    WgoJson = json,
                    Fingerprint = ComputeFingerprint(json)
                };
                return true;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[WGOStateSync] Failed to serialize WGO {wgo?.obj_id}: {ex.Message}");
                return false;
            }
        }

        private static bool ShouldSyncWgo(WorldGameObject wgo)
        {
            if (wgo == null || wgo.is_removed)
                return false;

            if (wgo.is_player || wgo.GetComponent<PlayerComponent>() != null)
                return false;

            // Build previews are registered as world objects before their final position is
            // committed. SpawnSync sends the authoritative object from DoPlace instead.
            if (wgo.GetComponent<FloatingWorldGameObject>() != null ||
                wgo.GetComponent<RemoteBuildingPreviewMarker>() != null)
                return false;

            if (wgo.obj_def != null && wgo.obj_def.IsNPC())
                return false;

            if (wgo.name == "Player" || wgo.name.Contains("RemotePlayer"))
                return false;

            if (string.IsNullOrEmpty(wgo.obj_id) || wgo.obj_id == "0")
                return false;

            if (MainGame.me?.world_root != null && !wgo.transform.IsChildOf(MainGame.me.world_root))
                return false;

            return true;
        }

        private static void ApplyEntry(WgoStateEntry entry)
        {
            SerializableWGO serialized = JsonUtility.FromJson<SerializableWGO>(entry.WgoJson);
            if (string.IsNullOrEmpty(serialized.obj_id) || serialized.obj_id == "0")
            {
                CoopMod.Logger.LogWarning($"[WGOStateSync] Ignoring destructive WGO state entry for key {entry.Key}; explicit WGO destruction sync handles deletes");
                return;
            }

            WorldGameObject existing = ResolveWgo(entry);
            if (existing == null)
            {
                WorldGameObject created = WorldGameObject.InstantiateWGOPrefab();
                if (created != null)
                {
                    created.RestoreFromSerializedObject(serialized, true);
                    RestoreChunkRegistration(created, false);
                }
                return;
            }

            if (!ShouldSyncWgo(existing))
                return;

            // InventorySync is the sole authority for container contents. A
            // full-world capture can take several frames, so restoring the
            // inventory embedded in SerializableWGO here can replay an older
            // autopsy-table state after a body has already been removed.
            Item currentData = existing.data;
            int inventorySize = currentData?.inventory_size ?? 0;
            List<Item> inventory = currentData?.inventory;
            List<Item> secondaryInventory = currentData?.secondary_inventory;
            bool redrawPreservedInventory =
                existing.is_autopsy_table ||
                inventorySize > 0 ||
                (inventory != null && inventory.Count > 0) ||
                (secondaryInventory != null &&
                 secondaryInventory.Count > 0);
            bool wasActive = existing.gameObject.activeSelf;

            existing.RestoreFromSerializedObject(serialized, false);

            if (currentData != null && existing.data != null)
            {
                existing.data.inventory_size = inventorySize;
                existing.data.inventory = inventory ?? new List<Item>();
                existing.data.secondary_inventory =
                    secondaryInventory ?? new List<Item>();
            }

            if (redrawPreservedInventory)
            {
                try
                {
                    existing.Redraw(true);
                    existing.GetComponentInChildren<SmartDrawer>(true)
                        ?.Redraw(true);
                }
                catch (Exception ex)
                {
                    CoopMod.Logger.LogWarning(
                        $"[WGOStateSync] Failed to redraw preserved inventory " +
                        $"for '{existing.obj_id}': {ex.Message}");
                }
            }

            RestoreChunkRegistration(existing, wasActive);
        }

        private static void RestoreChunkRegistration(
            WorldGameObject wgo,
            bool makeVisibleNow)
        {
            if (wgo == null)
                return;

            wgo.RefreshPositionCache();
            ChunkedGameObject chunk =
                wgo.GetComponentInChildren<ChunkedGameObject>(true);
            if (chunk == null)
                return;

            chunk.destroyed = false;
            chunk.pending_to_remove = false;
            chunk.RecalculateChunk();
            ChunkManager.OnAddNewObject(chunk);

            if (makeVisibleNow)
            {
                chunk.obj_visible = true;
                chunk.UpdateVisibility();
            }
        }

        private static WorldGameObject ResolveWgo(WgoStateEntry entry)
        {
            WorldGameObject byKey = ResolveWgoByKey(entry.Key);
            if (byKey != null)
                return byKey;

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

            return ResolveNearest(entry.ObjId, entry.Position);
        }

        private static WorldGameObject ResolveWgoByKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;

            if (key.StartsWith("u:", StringComparison.Ordinal) &&
                long.TryParse(key.Substring(2), out long uniqueId) &&
                uniqueId > 0L)
            {
                if (WGORegistry.Instance != null && WGORegistry.Instance.TryGet(uniqueId, out var indexed))
                    return indexed;

                return WorldMap.GetWorldGameObjectByUniqueId(uniqueId, false);
            }

            if (key.StartsWith("t:", StringComparison.Ordinal))
            {
                return WorldMap.GetWorldGameObjectByCustomTag(key.Substring(2), true);
            }

            if (key.StartsWith("p:", StringComparison.Ordinal))
            {
                string[] parts = key.Split(':');
                if (parts.Length == 4 &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                {
                    return ResolveNearest(parts[1], new Vector3(x, y, 0f));
                }
            }

            return null;
        }

        private static WorldGameObject ResolveNearest(string objId, Vector3 position)
        {
            if (string.IsNullOrEmpty(objId))
                return null;

            WorldGameObject registryMatch = WGORegistry.Instance?.ResolveNearest(objId, position, 160f);
            if (registryMatch != null && ShouldSyncWgo(registryMatch))
                return registryMatch;

            List<WorldGameObject> worldObjects = WGORegistry.Instance?.SnapshotAll() ?? MainGame.me?.GetListOfWorldObjects();
            if (worldObjects == null)
                return null;

            WorldGameObject nearest = null;
            float nearestDistance = 160f * 160f;

            for (int i = 0; i < worldObjects.Count; i++)
            {
                WorldGameObject candidate = worldObjects[i];
                if (!ShouldSyncWgo(candidate) || candidate.obj_id != objId)
                    continue;

                float distance = (candidate.transform.position - position).sqrMagnitude;
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = candidate;
                }
            }

            return nearest;
        }

        private bool IsStaleSnapshot(WgoStateSnapshot snapshot)
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

        private void ApplySnapshotToAuthoritativeBaseline(WgoStateSnapshot snapshot)
        {
            if (snapshot.IsFull)
            {
                authoritativeFingerprints.Clear();
            }

            if (snapshot.CompleteFingerprints != null && snapshot.IsFull)
            {
                foreach (KeyValuePair<string, ulong> pair in snapshot.CompleteFingerprints)
                {
                    authoritativeFingerprints[pair.Key] = pair.Value;
                }
            }
            else
            {
                for (int i = 0; i < snapshot.Entries.Count; i++)
                {
                    WgoStateEntry entry = snapshot.Entries[i];
                    authoritativeFingerprints[entry.Key] = entry.Fingerprint;
                }

                for (int i = 0; i < snapshot.RemovedKeys.Count; i++)
                {
                    authoritativeFingerprints.Remove(snapshot.RemovedKeys[i]);
                }
            }

            if (snapshot.IsFull || authoritativeFingerprints.Count > 0)
            {
                hasAuthoritativeBaseline = true;
            }
        }

        private void MarkLastSentFingerprintUnknown()
        {
            hasLastSentCompleteFingerprint = false;
            lastSentCompleteFingerprint = 0UL;
        }

        private static byte[] SerializeSnapshot(WgoStateSnapshot snapshot)
        {
            using (var stream = new MemoryStream(4096))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(snapshot.Version);
                writer.Write(snapshot.OriginSteamId);
                writer.Write(snapshot.Sequence);
                writer.Write(snapshot.IsFull);

                writer.Write(snapshot.Entries.Count);
                for (int i = 0; i < snapshot.Entries.Count; i++)
                {
                    WriteEntry(writer, snapshot.Entries[i]);
                }

                writer.Write(snapshot.RemovedKeys.Count);
                for (int i = 0; i < snapshot.RemovedKeys.Count; i++)
                {
                    writer.Write(snapshot.RemovedKeys[i] ?? string.Empty);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        private static WgoStateSnapshot DeserializeSnapshot(byte[] payload)
        {
            using (var stream = new MemoryStream(payload, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                var snapshot = new WgoStateSnapshot
                {
                    Version = reader.ReadByte()
                };

                if (snapshot.Version != PayloadVersion)
                {
                    throw new InvalidDataException($"Unsupported WGO state payload version {snapshot.Version}");
                }

                snapshot.OriginSteamId = reader.ReadUInt64();
                snapshot.Sequence = reader.ReadUInt32();
                snapshot.IsFull = reader.ReadBoolean();

                int entryCount = ReadSafeCount(reader, "WGO state entry count", MaxWgoEntries);
                snapshot.Entries = new List<WgoStateEntry>(entryCount);
                snapshot.CompleteFingerprints = new Dictionary<string, ulong>();
                for (int i = 0; i < entryCount; i++)
                {
                    WgoStateEntry entry = ReadEntry(reader);
                    snapshot.Entries.Add(entry);
                    snapshot.CompleteFingerprints[entry.Key] = entry.Fingerprint;
                }

                int removedCount = ReadSafeCount(reader, "WGO state removed count", MaxRemovedEntries);
                snapshot.RemovedKeys = new List<string>(removedCount);
                for (int i = 0; i < removedCount; i++)
                {
                    snapshot.RemovedKeys.Add(reader.ReadString());
                }

                snapshot.HasChanges = snapshot.IsFull || snapshot.Entries.Count > 0 || snapshot.RemovedKeys.Count > 0;
                return snapshot;
            }
        }

        private static void WriteEntry(BinaryWriter writer, WgoStateEntry entry)
        {
            writer.Write(entry.Key ?? string.Empty);
            writer.Write(entry.UniqueId);
            writer.Write(entry.ObjId ?? string.Empty);
            writer.Write(entry.CustomTag ?? string.Empty);
            writer.Write(entry.Position.x);
            writer.Write(entry.Position.y);
            writer.Write(entry.Position.z);
            writer.Write(entry.Fingerprint);
            writer.Write(entry.WgoJson ?? string.Empty);
        }

        private static WgoStateEntry ReadEntry(BinaryReader reader)
        {
            var entry = new WgoStateEntry
            {
                Key = reader.ReadString(),
                UniqueId = reader.ReadInt64(),
                ObjId = reader.ReadString(),
                CustomTag = reader.ReadString(),
                Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                Fingerprint = reader.ReadUInt64(),
                WgoJson = reader.ReadString()
            };

            if (string.IsNullOrEmpty(entry.Key))
            {
                entry.Key = BuildWgoKey(entry.UniqueId, entry.ObjId, entry.CustomTag, entry.Position);
            }

            return entry;
        }

        private static byte[] BuildSnapshotEnvelope(byte[] snapshotBytes)
        {
            using (var stream = new MemoryStream(snapshotBytes.Length + 8))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(EnvelopeSnapshot);
                writer.Write(snapshotBytes.Length);
                writer.Write(snapshotBytes);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static byte[] BuildBeginEnvelope(ulong originSteamId, ulong transferId, int totalBytes, int totalChunks)
        {
            using (var stream = new MemoryStream(32))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(EnvelopeBegin);
                writer.Write(originSteamId);
                writer.Write(transferId);
                writer.Write(totalBytes);
                writer.Write(ChunkPayloadBytes);
                writer.Write(totalChunks);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static byte[] BuildChunkEnvelope(ulong transferId, int chunkIndex, byte[] data, int offset, int count)
        {
            using (var stream = new MemoryStream(count + 24))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(EnvelopeChunk);
                writer.Write(transferId);
                writer.Write(chunkIndex);
                writer.Write(count);
                writer.Write(data, offset, count);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static byte[] BuildEndEnvelope(ulong transferId)
        {
            using (var stream = new MemoryStream(16))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(EnvelopeEnd);
                writer.Write(transferId);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static byte[] ReadLengthPrefixedBytes(BinaryReader reader, int maxBytes)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > maxBytes)
                throw new InvalidDataException($"Invalid byte payload length: {length}");

            byte[] data = reader.ReadBytes(length);
            if (data.Length != length)
                throw new EndOfStreamException("Unexpected end of byte payload");

            return data;
        }

        private static string BuildWgoKey(long uniqueId, string objId, string customTag, Vector3 position)
        {
            if (uniqueId > 0L)
                return "u:" + uniqueId;

            if (!string.IsNullOrEmpty(customTag))
                return "t:" + customTag;

            return string.Format(CultureInfo.InvariantCulture, "p:{0}:{1:F1}:{2:F1}", objId ?? string.Empty, position.x, position.y);
        }

        private static double ElapsedMilliseconds(long startTicks)
        {
            return (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
        }

        private static ulong ComputeCompleteFingerprint(List<WgoStateEntry> entries)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;

            for (int i = 0; i < entries.Count; i++)
            {
                WgoStateEntry entry = entries[i];
                MixString(ref hash, prime, entry.Key);
                MixUInt64(ref hash, prime, entry.Fingerprint);
            }

            return hash;
        }

        private static void MixString(ref ulong hash, ulong prime, string value)
        {
            if (value == null)
                return;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                hash ^= (byte)c;
                hash *= prime;
                hash ^= (byte)(c >> 8);
                hash *= prime;
            }
        }

        private static void MixUInt64(ref ulong hash, ulong prime, ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                hash ^= (byte)(value >> (i * 8));
                hash *= prime;
            }
        }

        private static ulong ComputeFingerprint(string value)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            if (value == null)
                return hash;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                hash ^= (byte)c;
                hash *= prime;
                hash ^= (byte)(c >> 8);
                hash *= prime;
            }

            return hash;
        }

        private ulong BuildTransferId(ulong originSteamId)
        {
            nextTransferId++;
            ulong transferId = ((ulong)DateTime.UtcNow.Ticks ^ originSteamId ^ nextTransferId);
            return transferId == 0UL ? 1UL : transferId;
        }

        private static string BuildTransferKey(CSteamID senderID, ulong transferId)
        {
            return senderID.m_SteamID.ToString() + ":" + transferId.ToString();
        }

        private static int ReadSafeCount(BinaryReader reader, string label, int max)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > max)
                throw new InvalidDataException($"Invalid {label}: {count}");

            return count;
        }

        private static bool CanUseWgoState()
        {
            return MainGame.game_started
                && MainGame.me != null
                && MainGame.me.save != null
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

        private sealed class WgoStateSnapshot
        {
            public byte Version;
            public ulong OriginSteamId;
            public uint Sequence;
            public bool IsFull;
            public bool HasChanges;
            public List<WgoStateEntry> Entries;
            public List<string> RemovedKeys;
            public Dictionary<string, ulong> CompleteFingerprints;
            public ulong CompleteFingerprint;
        }

        private sealed class WgoStateEntry
        {
            public string Key;
            public long UniqueId;
            public string ObjId;
            public string CustomTag;
            public Vector3 Position;
            public string WgoJson;
            public ulong Fingerprint;
        }
    }
}

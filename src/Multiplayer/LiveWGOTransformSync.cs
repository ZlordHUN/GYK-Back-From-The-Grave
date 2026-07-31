using System;
using System.Collections.Generic;
using System.IO;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Lightweight live transform lane for physics-backed non-player WGOs.
    /// Full WGOStateSync remains the authoritative reconciliation path; this keeps
    /// short-lived motion looking correct between those snapshots.
    /// </summary>
    public class LiveWGOTransformSync : SyncBehaviour
    {
        public static LiveWGOTransformSync Instance => GetInstance<LiveWGOTransformSync>();

        private struct CachedWgo
        {
            public long UniqueId;
            public WorldGameObject Wgo;
        }

        private const byte PayloadVersion = 1;
        private const float BroadcastIntervalSeconds = 0.08f;
        private const float CacheRebuildSeconds = 3f;
        private const float MovedDistance = 0.35f;
        private const float SnapDistance = 4f;
        private const float CorrectionLerpSpeed = 8f;
        private const float LocalAuthoritySeconds = 0.45f;
        private const float DiagnosticIntervalSeconds = 5f;
        private const int CacheBuildObjectsPerFrame = 512;
        private const int BroadcastScanPerTick = 512;
        private const int MaxSendPerTick = 48;
        private const int MaxPayloadBytes = 16 * 1024;

        private readonly List<CachedWgo> cache = new List<CachedWgo>();
        private readonly List<CachedWgo> pendingCache = new List<CachedWgo>();
        private readonly Dictionary<long, Vector3> lastBroadcastPositions = new Dictionary<long, Vector3>();
        private readonly Dictionary<long, Vector3> targetPositions = new Dictionary<long, Vector3>();
        private readonly Dictionary<long, float> localAuthorityUntil = new Dictionary<long, float>();
        private readonly List<long> staleTargetIds = new List<long>();
        private readonly List<KeyValuePair<long, Vector3>> movedObjects =
            new List<KeyValuePair<long, Vector3>>(MaxSendPerTick);

        private List<WorldGameObject> cacheBuildSource;
        private float broadcastTimer;
        private float cacheTimer = CacheRebuildSeconds;
        private float diagnosticTimer;
        private int cacheBuildIndex;
        private int cacheBuildStartFrame;
        private int cacheBuildObjectsScanned;
        private int cacheBuildInfoLogs;
        private int broadcastScanIndex;
        private bool cacheBuildInProgress;
        private float cacheBuildStartedAt;
        private int diagSentBatches;
        private int diagSentItems;
        private int diagReceivedBatches;
        private int diagReceivedItems;
        private int diagAppliedItems;
        private int diagAuthoritySkips;
        private int diagStaleTargets;
        private int diagCacheBuilds;
        private int diagCacheObjectsScanned;
        private bool runtimeEnabled;

        public int CachedObjectCount => cache.Count;
        public int TargetObjectCount => targetPositions.Count;
        public int CacheBuildScannedCount => cacheBuildObjectsScanned;
        public int CacheBuildTotalCount => cacheBuildSource?.Count ?? 0;
        public bool IsRuntimeActive => runtimeEnabled && IsSyncEnabled;
        public bool IsCacheBuildInProgress => cacheBuildInProgress;
        public bool HasCompletedInitialCacheBuild { get; private set; }

        protected override string LogPrefix => "[LiveWGOTransformSync]";

        internal void AcceptCanonicalPlacement(WorldGameObject wgo, Vector3 position)
        {
            if (wgo == null || IsHost || wgo.unique_id == 0L)
                return;

            if (IsNpc(wgo))
            {
                targetPositions.Remove(wgo.unique_id);
                localAuthorityUntil.Remove(wgo.unique_id);
                lastBroadcastPositions.Remove(wgo.unique_id);
                return;
            }

            targetPositions[wgo.unique_id] = position;
            localAuthorityUntil.Remove(wgo.unique_id);
            lastBroadcastPositions[wgo.unique_id] = position;
        }

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnWGOTransformSyncReceived -= OnTransformSyncReceived;
                SteamP2PManager.Instance.OnWGOTransformSyncReceived += OnTransformSyncReceived;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnWGOTransformSyncReceived -= OnTransformSyncReceived;
            }
        }

        protected override void OnSyncEnabled()
        {
            runtimeEnabled = ModConfig.EnableLiveWGOTransformSync == null ||
                             ModConfig.EnableLiveWGOTransformSync.Value;
            if (!runtimeEnabled)
            {
                CoopMod.Logger.LogInfo($"{LogPrefix} Disabled by config");
                ResetSessionState();
                return;
            }

            ResetSessionState();
        }

        protected override void OnSyncDisabled()
        {
            runtimeEnabled = false;
            ResetSessionState();
        }

        public void ResetSessionState()
        {
            broadcastTimer = 0f;
            cacheTimer = CacheRebuildSeconds;
            diagnosticTimer = 0f;
            cache.Clear();
            pendingCache.Clear();
            cacheBuildSource = null;
            cacheBuildIndex = 0;
            cacheBuildStartFrame = 0;
            cacheBuildObjectsScanned = 0;
            cacheBuildInfoLogs = 0;
            cacheBuildStartedAt = 0f;
            broadcastScanIndex = 0;
            cacheBuildInProgress = false;
            HasCompletedInitialCacheBuild = false;
            lastBroadcastPositions.Clear();
            targetPositions.Clear();
            localAuthorityUntil.Clear();
            staleTargetIds.Clear();
            movedObjects.Clear();
            ResetDiagnostics();
        }

        private void Update()
        {
            long __profStart = GraveyardKeeperCoop.Utils.FrameProfiler.BeginSection();
            try { UpdateInternal(); }
            finally { GraveyardKeeperCoop.Utils.FrameProfiler.EndSection("LWGO.Update", __profStart); }
        }

        private void UpdateInternal()
        {
            if (!runtimeEnabled || !IsSyncEnabled || !IsSessionActive()) return;

            cacheTimer += Time.deltaTime;
            if (!cacheBuildInProgress && cacheTimer >= CacheRebuildSeconds)
            {
                cacheTimer = 0f;
                BeginCacheRebuild();
            }

            if (cacheBuildInProgress)
            {
                ProcessCacheRebuild();
            }

            broadcastTimer += Time.deltaTime;
            if (broadcastTimer >= BroadcastIntervalSeconds)
            {
                broadcastTimer = 0f;
                BroadcastMovedObjects();
            }

            diagnosticTimer += Time.deltaTime;
            if (diagnosticTimer >= DiagnosticIntervalSeconds)
            {
                diagnosticTimer = 0f;
                ReportDiagnostics();
            }
        }

        private void LateUpdate()
        {
            long __profStart = GraveyardKeeperCoop.Utils.FrameProfiler.BeginSection();
            try { LateUpdateInternal(); }
            finally { GraveyardKeeperCoop.Utils.FrameProfiler.EndSection("LWGO.LateUpdate", __profStart); }
        }

        private void LateUpdateInternal()
        {
            if (!runtimeEnabled || !IsSyncEnabled || !IsSessionActive() || targetPositions.Count == 0) return;

            staleTargetIds.Clear();
            float snapSqr = SnapDistance * SnapDistance;
            float lerp = Mathf.Clamp01(Time.deltaTime * CorrectionLerpSpeed);

            foreach (var pair in targetPositions)
            {
                long uniqueId = pair.Key;
                if (localAuthorityUntil.TryGetValue(uniqueId, out float authorityUntil) &&
                    Time.realtimeSinceStartup < authorityUntil)
                {
                    diagAuthoritySkips++;
                    continue;
                }

                WorldGameObject wgo = ResolveByUniqueId(uniqueId);
                if (wgo == null)
                {
                    staleTargetIds.Add(uniqueId);
                    diagStaleTargets++;
                    continue;
                }

                if (!ShouldSync(wgo))
                {
                    staleTargetIds.Add(uniqueId);
                    lastBroadcastPositions.Remove(uniqueId);
                    localAuthorityUntil.Remove(uniqueId);
                    continue;
                }

                Vector3 current = GetPosition(wgo);
                Vector3 target = pair.Value;
                target.z = current.z;
                Vector3 corrected = (current - target).sqrMagnitude > snapSqr
                    ? target
                    : Vector3.Lerp(current, target, lerp);

                ApplyPosition(wgo, corrected);
                StopPhysics(wgo);
                lastBroadcastPositions[uniqueId] = corrected;
                diagAppliedItems++;
            }

            for (int i = 0; i < staleTargetIds.Count; i++)
                targetPositions.Remove(staleTargetIds[i]);
        }

        private void BroadcastMovedObjects()
        {
            // Shared world objects are host-authoritative. Allowing a client to publish
            // its stationary observer copy can pin a host-side scripted NPC in place
            // (notably the donkey while its FlowScript is walking it off screen).
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsHost) return;

            if (cache.Count == 0) return;

            movedObjects.Clear();
            float movedSqr = MovedDistance * MovedDistance;
            int cacheCount = cache.Count;
            if (broadcastScanIndex < 0 || broadcastScanIndex >= cacheCount)
                broadcastScanIndex = 0;

            int scanned = 0;
            while (scanned < cacheCount &&
                   scanned < BroadcastScanPerTick &&
                   movedObjects.Count < MaxSendPerTick)
            {
                int index = (broadcastScanIndex + scanned) % cacheCount;
                scanned++;

                CachedWgo item = cache[index];
                WorldGameObject wgo = item.Wgo;
                if (wgo == null)
                {
                    lastBroadcastPositions.Remove(item.UniqueId);
                    continue;
                }

                Vector3 pos = GetPosition(wgo);
                if (!lastBroadcastPositions.TryGetValue(item.UniqueId, out Vector3 previous))
                {
                    lastBroadcastPositions[item.UniqueId] = pos;
                    continue;
                }

                if ((pos - previous).sqrMagnitude < movedSqr) continue;

                lastBroadcastPositions[item.UniqueId] = pos;
                localAuthorityUntil[item.UniqueId] = Time.realtimeSinceStartup + LocalAuthoritySeconds;
                targetPositions.Remove(item.UniqueId);
                movedObjects.Add(new KeyValuePair<long, Vector3>(item.UniqueId, pos));
            }

            broadcastScanIndex = cacheCount > 0 ? (broadcastScanIndex + scanned) % cacheCount : 0;

            if (movedObjects.Count == 0) return;

            byte[] payload = SerializePayload(++NextSequence, movedObjects);
            if (payload.Length > MaxPayloadBytes)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Payload too large ({payload.Length}); skipping");
                return;
            }

            SteamP2PManager.Instance?.BroadcastWGOTransformSync(payload);
            diagSentBatches++;
            diagSentItems += movedObjects.Count;
        }

        private void OnTransformSyncReceived(CSteamID senderID, byte[] payload)
        {
            if (!runtimeEnabled || !IsSyncEnabled || payload == null || payload.Length == 0 || payload.Length > MaxPayloadBytes) return;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled) return;

            // The host owns shared WGO simulation and must never consume client
            // transform corrections. Clients only accept the connected host.
            if (onlineCoop.IsHost || !onlineCoop.IsRemotePlayer(senderID)) return;

            if (!DeserializePayload(payload, out var updates)) return;

            int accepted = 0;
            for (int i = 0; i < updates.Count; i++)
            {
                long uniqueId = updates[i].Key;
                if (uniqueId == 0L) continue;

                if (localAuthorityUntil.TryGetValue(uniqueId, out float authorityUntil) &&
                    Time.realtimeSinceStartup < authorityUntil)
                {
                    diagAuthoritySkips++;
                    continue;
                }

                targetPositions[uniqueId] = updates[i].Value;
                accepted++;
            }

            if (accepted > 0)
            {
                diagReceivedBatches++;
                diagReceivedItems += accepted;
            }
        }

        private void BeginCacheRebuild()
        {
            WGORegistry registry = WGORegistry.Instance;
            cacheBuildSource = registry?.SnapshotAll() ?? MainGame.me?.GetListOfWorldObjects();
            cacheBuildIndex = 0;
            cacheBuildObjectsScanned = 0;
            cacheBuildStartFrame = Time.frameCount;
            cacheBuildStartedAt = Time.realtimeSinceStartup;
            pendingCache.Clear();
            cacheBuildInProgress = cacheBuildSource != null;
        }

        private void ProcessCacheRebuild()
        {
            if (cacheBuildSource == null)
            {
                FinishCacheRebuild();
                return;
            }

            int processed = 0;
            while (cacheBuildIndex < cacheBuildSource.Count && processed < CacheBuildObjectsPerFrame)
            {
                WorldGameObject wgo = cacheBuildSource[cacheBuildIndex++];
                processed++;
                if (!ShouldSync(wgo)) continue;

                long uniqueId = wgo.unique_id;
                if (uniqueId == 0L) continue;

                pendingCache.Add(new CachedWgo { UniqueId = uniqueId, Wgo = wgo });
                if (!lastBroadcastPositions.ContainsKey(uniqueId))
                    lastBroadcastPositions[uniqueId] = GetPosition(wgo);
            }

            diagCacheObjectsScanned += processed;
            cacheBuildObjectsScanned += processed;

            if (cacheBuildIndex >= cacheBuildSource.Count)
            {
                FinishCacheRebuild();
            }
        }

        private void FinishCacheRebuild()
        {
            cache.Clear();
            cache.AddRange(pendingCache);
            pendingCache.Clear();
            cacheBuildSource = null;
            cacheBuildIndex = 0;
            broadcastScanIndex = 0;
            cacheBuildInProgress = false;
            HasCompletedInitialCacheBuild = true;
            diagCacheBuilds++;

            if (cacheBuildInfoLogs < 3)
            {
                cacheBuildInfoLogs++;
                int frames = Mathf.Max(1, Time.frameCount - cacheBuildStartFrame + 1);
                float elapsedMs = Mathf.Max(0f, (Time.realtimeSinceStartup - cacheBuildStartedAt) * 1000f);
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Cache rebuild completed: scanned={cacheBuildObjectsScanned}, " +
                    $"cached={cache.Count}, frames={frames}, elapsed_ms={elapsedMs:F1}");
            }
        }

        private bool IsSessionActive()
        {
            return IsOnline && MainGame.me != null && MainGame.game_started;
        }

        private static bool ShouldSync(WorldGameObject wgo)
        {
            if (wgo == null || wgo.is_player || IsNpc(wgo)) return false;
            if (wgo.GetComponent<FloatingWorldGameObject>() != null) return false;
            if (wgo.GetComponent<RemoteBuildingPreviewMarker>() != null) return false;

            Scene scene = wgo.gameObject.scene;
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.name)) return false;

            if (wgo.GetComponent<DropResGameObject>() != null) return false;

            return wgo.GetComponent<Rigidbody2D>() != null ||
                   wgo.GetComponentInChildren<Rigidbody2D>(true) != null;
        }

        private static bool IsNpc(WorldGameObject wgo)
        {
            if (wgo?.obj_def == null)
                return false;

            try { return wgo.obj_def.IsNPC(); }
            catch { return false; }
        }

        private static WorldGameObject ResolveByUniqueId(long uniqueId)
        {
            if (uniqueId == 0L) return null;

            if (WGORegistry.Instance != null && WGORegistry.Instance.TryGet(uniqueId, out var indexed))
                return indexed;

            List<WorldGameObject> objects = WGORegistry.Instance?.SnapshotAll() ?? MainGame.me?.GetListOfWorldObjects();
            if (objects == null) return null;

            for (int i = 0; i < objects.Count; i++)
            {
                WorldGameObject wgo = objects[i];
                if (wgo != null && wgo.unique_id == uniqueId) return wgo;
            }

            return null;
        }

        private static Vector3 GetPosition(WorldGameObject wgo)
        {
            if (wgo == null) return Vector3.zero;
            return wgo.transform != null ? wgo.transform.position : wgo.pos3;
        }

        private static void ApplyPosition(WorldGameObject wgo, Vector3 position)
        {
            if (wgo == null) return;
            try { wgo.PlaceAtPos(position); }
            catch { if (wgo.transform != null) wgo.transform.position = position; }
        }

        private static void StopPhysics(WorldGameObject wgo)
        {
            if (wgo == null) return;

            Rigidbody2D[] bodies = wgo.GetComponentsInChildren<Rigidbody2D>(true);
            for (int i = 0; i < bodies.Length; i++)
            {
                Rigidbody2D body = bodies[i];
                if (body == null) continue;
                body.velocity = Vector2.zero;
                body.angularVelocity = 0f;
            }
        }

        private void ReportDiagnostics()
        {
            if (diagSentBatches == 0 && diagReceivedBatches == 0 && diagAppliedItems == 0 &&
                diagAuthoritySkips == 0 && diagStaleTargets == 0 && diagCacheBuilds == 0) return;

            CoopMod.Logger.LogDebug(
                $"{LogPrefix} cache={cache.Count} targets={targetPositions.Count} " +
                $"sent={diagSentBatches}/{diagSentItems} recv={diagReceivedBatches}/{diagReceivedItems} " +
                $"applied={diagAppliedItems} authority-skips={diagAuthoritySkips} stale={diagStaleTargets} " +
                $"cache-builds={diagCacheBuilds} scanned={diagCacheObjectsScanned}");
            ResetDiagnostics();
        }

        private void ResetDiagnostics()
        {
            diagSentBatches = 0;
            diagSentItems = 0;
            diagReceivedBatches = 0;
            diagReceivedItems = 0;
            diagAppliedItems = 0;
            diagAuthoritySkips = 0;
            diagStaleTargets = 0;
            diagCacheBuilds = 0;
            diagCacheObjectsScanned = 0;
        }

        private static byte[] SerializePayload(uint sequence, List<KeyValuePair<long, Vector3>> updates)
        {
            using (var stream = new MemoryStream(1024))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(PayloadVersion);
                writer.Write(sequence);
                int count = Mathf.Min(updates?.Count ?? 0, MaxSendPerTick);
                writer.Write((ushort)count);
                for (int i = 0; i < count; i++)
                {
                    writer.Write(updates[i].Key);
                    Vector3 pos = updates[i].Value;
                    writer.Write(pos.x);
                    writer.Write(pos.y);
                    writer.Write(pos.z);
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static bool DeserializePayload(byte[] payload, out List<KeyValuePair<long, Vector3>> updates)
        {
            updates = new List<KeyValuePair<long, Vector3>>();

            try
            {
                using (var stream = new MemoryStream(payload))
                using (var reader = new BinaryReader(stream))
                {
                    byte version = reader.ReadByte();
                    if (version != PayloadVersion) return false;

                    reader.ReadUInt32();
                    int count = reader.ReadUInt16();
                    if (count > MaxSendPerTick) return false;

                    for (int i = 0; i < count && stream.Position + 20 <= stream.Length; i++)
                    {
                        long uniqueId = reader.ReadInt64();
                        var pos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        updates.Add(new KeyValuePair<long, Vector3>(uniqueId, pos));
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[LiveWGOTransformSync] Failed to parse payload: {ex.Message}");
                return false;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    public class CraftSync : PeriodicSyncBehaviour
    {
        public static CraftSync Instance => GetInstance<CraftSync>();

        private const byte PayloadVersion = 4;
        private const byte SubSnapshot = 0;
        private const byte SubCraftFinished = 1;
        private const byte SubAuthorityClaim = 2;
        private const byte SubAuthorityRelease = 3;
        private const byte SubLeaseRequest = 4;
        private const byte SubLeaseDecision = 5;
        private const int MaxPayloadBytes = 256 * 1024;
        private const int MaxQueueItems = 32;
        private const float LocalAuthorityReleaseSeconds = 0.5f;
        private const float StationLeaseSeconds = 12f;
        private const float PendingLeaseRequestSeconds = 5f;
        private const float RejectedLeaseRetrySeconds = 2f;
        private const float CompletedLeaseGraceSeconds = 30f;

        private readonly HashSet<long> dirtyWgoUniqueIds = new HashSet<long>();
        private readonly HashSet<long> locallyOwnedCrafts = new HashSet<long>();
        private readonly Dictionary<long, float> localAuthorityReleaseAt =
            new Dictionary<long, float>();
        private readonly Dictionary<long, ulong> remoteCraftAuthority =
            new Dictionary<long, ulong>();
        private readonly Dictionary<long, float> remoteCraftAuthorityExpiresAt =
            new Dictionary<long, float>();
        private readonly Dictionary<long, uint> stationLeaseRevisions =
            new Dictionary<long, uint>();
        private readonly Dictionary<long, ClientLease> clientStationLeases =
            new Dictionary<long, ClientLease>();
        private readonly Dictionary<long, PendingLeaseRequest> pendingLeaseRequests =
            new Dictionary<long, PendingLeaseRequest>();
        private readonly Dictionary<long, float> stationLeaseRetryAt =
            new Dictionary<long, float>();
        private readonly Dictionary<long, float> stationBusyMessageAt =
            new Dictionary<long, float>();
        private readonly Dictionary<long, ReleasedRemoteLease> recentlyReleasedRemoteLeases =
            new Dictionary<long, ReleasedRemoteLease>();
        private readonly HashSet<string> processedRemoteCompletions =
            new HashSet<string>();
        private readonly Dictionary<long, string> locallyCompletedOperations =
            new Dictionary<long, string>();
        private readonly List<long> expiredRemoteLeaseIds = new List<long>();
        private uint nextLeaseRequestId;
        private bool isApplyingRemoteCraftState;
        private bool isApplyingRemoteCraftCompletion;

        private sealed class ClientLease
        {
            public uint Revision;
            public float ExpiresAt;
        }

        private sealed class PendingLeaseRequest
        {
            public uint RequestId;
            public float SentAt;
            public CraftComponent Craft;
            public Action Resume;
        }

        private sealed class ReleasedRemoteLease
        {
            public ulong OwnerSteamId;
            public uint Revision;
            public float ExpiresAt;
        }

        internal sealed class LocalCraftCompletion
        {
            public CraftDefinition Craft;
            public uint LeaseRevision;
            public long UniqueId;
            public string ObjId;
            public string CustomTag;
            public Vector3 Position;
        }

        internal enum StationLeaseDisposition
        {
            Granted,
            Blocked,
            RequestRequired
        }

        protected override string LogPrefix => "[CraftSync]";
        protected override float SyncIntervalSeconds => 2f;

        protected override void OnPeriodicSyncEnabled()
        {
            dirtyWgoUniqueIds.Clear();
            locallyOwnedCrafts.Clear();
            localAuthorityReleaseAt.Clear();
            remoteCraftAuthority.Clear();
            remoteCraftAuthorityExpiresAt.Clear();
            stationLeaseRevisions.Clear();
            clientStationLeases.Clear();
            pendingLeaseRequests.Clear();
            stationLeaseRetryAt.Clear();
            stationBusyMessageAt.Clear();
            recentlyReleasedRemoteLeases.Clear();
            processedRemoteCompletions.Clear();
            locallyCompletedOperations.Clear();
            expiredRemoteLeaseIds.Clear();
            nextLeaseRequestId = 0;
            isApplyingRemoteCraftState = false;
            isApplyingRemoteCraftCompletion = false;
        }

        protected override void OnSyncDisabled()
        {
            dirtyWgoUniqueIds.Clear();
            locallyOwnedCrafts.Clear();
            localAuthorityReleaseAt.Clear();
            remoteCraftAuthority.Clear();
            remoteCraftAuthorityExpiresAt.Clear();
            stationLeaseRevisions.Clear();
            clientStationLeases.Clear();
            pendingLeaseRequests.Clear();
            stationLeaseRetryAt.Clear();
            stationBusyMessageAt.Clear();
            recentlyReleasedRemoteLeases.Clear();
            processedRemoteCompletions.Clear();
            locallyCompletedOperations.Clear();
            expiredRemoteLeaseIds.Clear();
            nextLeaseRequestId = 0;
            isApplyingRemoteCraftState = false;
            isApplyingRemoteCraftCompletion = false;
        }

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnCraftSyncReceived -= OnCraftSyncReceived;
                SteamP2PManager.Instance.OnCraftSyncReceived += OnCraftSyncReceived;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnCraftSyncReceived -= OnCraftSyncReceived;
            }
        }

        protected override bool ShouldUpdate() =>
            MainGame.me != null && MainGame.game_started;

        protected override bool ShouldSend() =>
            MainGame.me != null && MainGame.game_started;

        protected override bool CanSendPeriodicSnapshot => true;

        internal bool IsApplyingRemoteCraftState =>
            isApplyingRemoteCraftState;

        internal static bool IsApplyingRemoteCraftCompletion =>
            Instance?.isApplyingRemoteCraftCompletion == true;

        /// <summary>
        /// Gate a player-driven station start or active work action before vanilla
        /// mutates state. The host grants one short-lived lease per station; a client
        /// resumes the suppressed call only after that grant arrives.
        /// </summary>
        internal bool TryAcquireStationLease(
            CraftComponent craft,
            CraftDefinition intendedCraft,
            Action resumeAfterGrant)
        {
            StationLeaseDisposition disposition =
                PrepareStationLease(craft, intendedCraft);
            if (disposition == StationLeaseDisposition.RequestRequired)
                RequestStationLease(craft, intendedCraft, resumeAfterGrant);
            return disposition == StationLeaseDisposition.Granted;
        }

        /// <summary>
        /// Determine whether a station call may proceed without allocating a
        /// continuation callback. The tool-use hot path calls this every frame.
        /// </summary>
        internal StationLeaseDisposition PrepareStationLease(
            CraftComponent craft,
            CraftDefinition intendedCraft)
        {
            if (!ShouldArbitrateStation(craft, intendedCraft))
                return StationLeaseDisposition.Granted;

            long uniqueId = craft.wgo.unique_id;
            float now = Time.realtimeSinceStartup;

            if (ShouldPreserveLocalCraftState(uniqueId))
                return StationLeaseDisposition.Granted;

            if (IsHost)
            {
                ExpireRemoteLease(uniqueId, now);
                if (remoteCraftAuthority.TryGetValue(uniqueId, out ulong owner))
                {
                    LogStationBusy(craft, owner);
                    return StationLeaseDisposition.Blocked;
                }

                locallyOwnedCrafts.Add(uniqueId);
                localAuthorityReleaseAt.Remove(uniqueId);
                return StationLeaseDisposition.Granted;
            }

            if (clientStationLeases.TryGetValue(uniqueId, out ClientLease lease))
            {
                if (now < lease.ExpiresAt)
                    return StationLeaseDisposition.Granted;
                clientStationLeases.Remove(uniqueId);
            }

            if (pendingLeaseRequests.TryGetValue(uniqueId, out PendingLeaseRequest pending))
            {
                if (now - pending.SentAt < PendingLeaseRequestSeconds)
                    return StationLeaseDisposition.Blocked;
                pendingLeaseRequests.Remove(uniqueId);
            }

            if (stationLeaseRetryAt.TryGetValue(uniqueId, out float retryAt))
            {
                if (now < retryAt)
                    return StationLeaseDisposition.Blocked;
                stationLeaseRetryAt.Remove(uniqueId);
            }

            return StationLeaseDisposition.RequestRequired;
        }

        internal void RequestStationLease(
            CraftComponent craft,
            CraftDefinition intendedCraft,
            Action resumeAfterGrant)
        {
            if (craft?.wgo == null || resumeAfterGrant == null)
                return;

            long uniqueId = craft.wgo.unique_id;
            if (pendingLeaseRequests.ContainsKey(uniqueId))
                return;

            uint requestId = ++nextLeaseRequestId;
            pendingLeaseRequests[uniqueId] = new PendingLeaseRequest
            {
                RequestId = requestId,
                SentAt = Time.realtimeSinceStartup,
                Craft = craft,
                Resume = resumeAfterGrant
            };
            SendLeaseRequest(craft, intendedCraft, requestId);
        }

        /// <summary>
        /// Release a work-continuation lease that arrived after the local player
        /// stopped holding the work action or moved to a different target.
        /// </summary>
        internal void ReleaseUnusedStationLease(CraftComponent craft)
        {
            if (craft?.wgo == null)
                return;

            long uniqueId = craft.wgo.unique_id;
            bool heldLease = locallyOwnedCrafts.Remove(uniqueId);
            heldLease |= clientStationLeases.Remove(uniqueId);
            localAuthorityReleaseAt.Remove(uniqueId);
            if (!heldLease)
                return;

            if (IsHost)
            {
                IncrementLeaseRevision(uniqueId);
                dirtyWgoUniqueIds.Add(uniqueId);
                ForceNextSend = true;
            }
            else
            {
                SendAuthorityRelease(craft);
            }

            CoopMod.Logger.LogDebug(
                $"{LogPrefix} Released unused station lease for {craft.wgo.obj_id} " +
                $"(uid={uniqueId})");
        }

        internal void NotifyLocalCraftStartFailed(CraftComponent craft)
        {
            if (craft?.wgo == null || craft.is_crafting)
                return;

            long uniqueId = craft.wgo.unique_id;
            bool heldLease = locallyOwnedCrafts.Remove(uniqueId);
            heldLease |= clientStationLeases.Remove(uniqueId);
            if (!heldLease)
                return;
            if (!IsHost)
                SendAuthorityRelease(craft);
        }

        internal void OnPeerLeftLobby(CSteamID peer)
        {
            if (peer == CSteamID.Nil)
                return;

            var released = new List<long>();
            foreach (var pair in remoteCraftAuthority)
            {
                if (pair.Value == peer.m_SteamID)
                    released.Add(pair.Key);
            }

            for (int i = 0; i < released.Count; i++)
            {
                long uniqueId = released[i];
                remoteCraftAuthority.Remove(uniqueId);
                remoteCraftAuthorityExpiresAt.Remove(uniqueId);
                IncrementLeaseRevision(uniqueId);
                dirtyWgoUniqueIds.Add(uniqueId);
            }

            if (released.Count > 0)
                ForceNextSend = true;
        }

        private void SendLeaseRequest(
            CraftComponent craft,
            CraftDefinition intendedCraft,
            uint requestId)
        {
            using (var stream = new MemoryStream(192))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubLeaseRequest);
                bw.Write(NextSequence++);
                bw.Write(requestId);
                WriteStationIdentity(bw, craft);
                bw.Write(intendedCraft?.id ?? string.Empty);
                bw.Flush();
                SteamP2PManager.Instance?.SendCraftSyncToHost(stream.ToArray());
            }
        }

        private static void WriteStationIdentity(BinaryWriter writer, CraftComponent craft)
        {
            WorldGameObject wgo = craft.wgo;
            writer.Write(wgo.unique_id);
            writer.Write(wgo.obj_id ?? string.Empty);
            writer.Write(wgo.custom_tag ?? string.Empty);
            Vector3 position = wgo.transform.position;
            writer.Write(position.x);
            writer.Write(position.y);
            writer.Write(position.z);
        }

        internal void MarkDirty(long uniqueId)
        {
            if (!IsSyncEnabled ||
                !IsOnline ||
                isApplyingRemoteCraftState)
            {
                return;
            }
            WorldGameObject wgo =
                WorldMap.GetWorldGameObjectByUniqueId(uniqueId, false);
            if (IsDynamicMobCraft(wgo?.components?.craft))
                return;

            dirtyWgoUniqueIds.Add(uniqueId);
            base.MarkDirty();
        }

        internal void NotifyLocalCraftStarted(CraftComponent craft)
        {
            if (!IsSyncEnabled ||
                !IsOnline ||
                isApplyingRemoteCraftState ||
                craft?.wgo == null ||
                !craft.is_crafting ||
                craft.current_craft == null)
                return;
            if (IsDynamicMobCraft(craft))
                return;
            if (!IsHost && craft.current_craft?.is_auto == true)
                return;

            long uniqueId = craft.wgo.unique_id;
            locallyCompletedOperations.Remove(uniqueId);
            if (IsHost)
            {
                ExpireRemoteLease(uniqueId, Time.realtimeSinceStartup);
                if (remoteCraftAuthority.TryGetValue(uniqueId, out ulong owner))
                {
                    CoopMod.Logger.LogWarning(
                        $"{LogPrefix} Refused to replace remote station owner {owner} " +
                        $"after a local craft start on {craft.wgo.obj_id} (uid={uniqueId})");
                    return;
                }
            }

            locallyOwnedCrafts.Add(uniqueId);
            localAuthorityReleaseAt.Remove(uniqueId);
            if (!IsHost && clientStationLeases.TryGetValue(uniqueId, out ClientLease lease))
            {
                lease.ExpiresAt = Time.realtimeSinceStartup + StationLeaseSeconds;
            }
            dirtyWgoUniqueIds.Add(uniqueId);
            ForceNextSend = true;

            if (!IsHost)
            {
                // The claim and snapshot share the ordered reliable lane. Sending
                // both at craft selection time lets an idle host table learn the
                // selected operation before the player begins the work animation.
                SendAuthorityClaim(craft, isWorkContinuation: false);
                SendLocalSnapshot(force: true);
            }

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Local peer owns active craft '{craft.current_craft?.id ?? string.Empty}' " +
                $"on {craft.wgo.obj_id} (uid={uniqueId})");
        }

        internal void NotifyLocalCraftWorkStarted(CraftComponent craft)
        {
            if (!IsSyncEnabled ||
                !IsOnline ||
                isApplyingRemoteCraftState ||
                craft?.wgo == null ||
                !craft.is_crafting ||
                craft.current_craft == null)
            {
                return;
            }

            long uniqueId = craft.wgo.unique_id;
            if (IsHost)
            {
                ExpireRemoteLease(uniqueId, Time.realtimeSinceStartup);
                if (remoteCraftAuthority.TryGetValue(uniqueId, out ulong owner))
                {
                    CoopMod.Logger.LogWarning(
                        $"{LogPrefix} Refused to replace active remote worker {owner} " +
                        $"on {craft.wgo.obj_id} (uid={uniqueId})");
                    return;
                }
            }

            locallyOwnedCrafts.Add(uniqueId);
            localAuthorityReleaseAt.Remove(uniqueId);
            if (!IsHost && clientStationLeases.TryGetValue(uniqueId, out ClientLease lease))
            {
                lease.ExpiresAt = Time.realtimeSinceStartup + StationLeaseSeconds;
            }
            dirtyWgoUniqueIds.Add(uniqueId);
            ForceNextSend = true;

            if (IsHost)
                return;

            SendAuthorityClaim(craft, isWorkContinuation: true);
            SendLocalSnapshot(force: true);
        }

        private void SendAuthorityClaim(
            CraftComponent craft,
            bool isWorkContinuation)
        {
            if (craft?.wgo == null || craft.current_craft == null)
                return;

            long uniqueId = craft.wgo.unique_id;
            using (var stream = new MemoryStream(192))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubAuthorityClaim);
                bw.Write(NextSequence++);
                bw.Write(uniqueId);
                bw.Write(craft.wgo.obj_id ?? string.Empty);
                bw.Write(craft.wgo.custom_tag ?? string.Empty);
                Vector3 position = craft.wgo.transform.position;
                bw.Write(position.x);
                bw.Write(position.y);
                bw.Write(position.z);
                bw.Write(craft.current_craft.id ?? string.Empty);
                bw.Write(isWorkContinuation);
                bw.Flush();
                SteamP2PManager.Instance?.SendCraftSyncToHost(stream.ToArray());
            }

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Claimed active craft work authority for " +
                $"{craft.wgo.obj_id} (uid={uniqueId})");
        }

        internal void NotifyLocalCraftEnded(CraftComponent craft)
        {
            if (isApplyingRemoteCraftState || craft?.wgo == null)
                return;

            long uniqueId = craft.wgo.unique_id;
            if (!locallyOwnedCrafts.Contains(uniqueId))
                return;

            PublishFinalOwnedState(uniqueId);
            locallyOwnedCrafts.Remove(uniqueId);
            if (IsHost)
            {
                remoteCraftAuthority.Remove(uniqueId);
                remoteCraftAuthorityExpiresAt.Remove(uniqueId);
                IncrementLeaseRevision(uniqueId);
            }
            else
            {
                clientStationLeases.Remove(uniqueId);
                SendAuthorityRelease(craft);
            }

            localAuthorityReleaseAt[uniqueId] =
                Time.realtimeSinceStartup + LocalAuthorityReleaseSeconds;
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Local craft authority released on {craft.wgo.obj_id} " +
                $"(uid={uniqueId}) after final state propagation");
        }

        internal void NotifyLocalCraftWorkStopped(CraftComponent craft)
        {
            if (!IsSyncEnabled ||
                !IsOnline ||
                isApplyingRemoteCraftState ||
                craft?.wgo == null)
            {
                return;
            }

            long uniqueId = craft.wgo.unique_id;
            if (!locallyOwnedCrafts.Contains(uniqueId))
                return;

            // The craft remains active when the player releases the work key. Send the
            // exact paused progress while we still own it, then release authority so a
            // different player can continue the same CraftComponent without restarting.
            PublishFinalOwnedState(uniqueId);
            locallyOwnedCrafts.Remove(uniqueId);
            localAuthorityReleaseAt[uniqueId] =
                Time.realtimeSinceStartup + LocalAuthorityReleaseSeconds;

            if (IsHost)
            {
                remoteCraftAuthority.Remove(uniqueId);
                remoteCraftAuthorityExpiresAt.Remove(uniqueId);
                IncrementLeaseRevision(uniqueId);
                dirtyWgoUniqueIds.Add(uniqueId);
                ForceNextSend = true;
            }
            else
            {
                clientStationLeases.Remove(uniqueId);
                SendAuthorityRelease(craft);
            }

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Released paused craft work on {craft.wgo.obj_id} " +
                $"(uid={uniqueId}, progress={craft.wgo.progress:F3})");
        }

        private void PublishFinalOwnedState(long uniqueId)
        {
            dirtyWgoUniqueIds.Add(uniqueId);
            ForceNextSend = true;
            SendLocalSnapshot(force: true);
        }

        private void SendAuthorityRelease(CraftComponent craft)
        {
            using (var stream = new MemoryStream(192))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubAuthorityRelease);
                bw.Write(NextSequence++);
                bw.Write(craft.wgo.unique_id);
                bw.Write(craft.wgo.obj_id ?? string.Empty);
                bw.Write(craft.wgo.custom_tag ?? string.Empty);
                Vector3 position = craft.wgo.transform.position;
                bw.Write(position.x);
                bw.Write(position.y);
                bw.Write(position.z);
                bw.Write(craft.current_craft?.id ?? string.Empty);
                bw.Flush();
                SteamP2PManager.Instance?.SendCraftSyncToHost(stream.ToArray());
            }
        }

        internal LocalCraftCompletion CaptureLocalCraftCompletion(
            CraftComponent component)
        {
            if (!IsSyncEnabled ||
                !IsOnline ||
                IsHost ||
                isApplyingRemoteCraftCompletion ||
                component?.wgo == null ||
                component.current_craft == null ||
                component.current_craft.is_auto ||
                string.IsNullOrEmpty(component.current_craft.id))
            {
                return null;
            }

            clientStationLeases.TryGetValue(
                component.wgo.unique_id,
                out ClientLease lease);
            return new LocalCraftCompletion
            {
                Craft = component.current_craft,
                LeaseRevision = lease?.Revision ?? 0U,
                UniqueId = component.wgo.unique_id,
                ObjId = component.wgo.obj_id ?? string.Empty,
                CustomTag = component.wgo.custom_tag ?? string.Empty,
                Position = component.wgo.transform.position
            };
        }

        internal void NotifyLocalCraftFinished(
            CraftComponent component,
            LocalCraftCompletion completion)
        {
            CraftDefinition craft = completion?.Craft;
            if (!IsSyncEnabled ||
                !IsOnline ||
                IsHost ||
                isApplyingRemoteCraftCompletion ||
                completion == null ||
                craft == null ||
                craft.is_auto ||
                string.IsNullOrEmpty(craft.id))
            {
                return;
            }

            CraftStateEntry finalState = null;
            try
            {
                if (component?.wgo != null && !component.wgo.is_removed)
                    finalState = CaptureEntry(component.wgo, component);
            }
            catch
            {
                // Replacement crafts can destroy their original station before
                // ProcessFinishedCraft returns. Their lifecycle sync owns the new
                // WGO; completion bookkeeping must still reach the host.
            }

            if (completion.UniqueId > 0L && finalState != null)
                locallyCompletedOperations[completion.UniqueId] = craft.id;

            using (var stream = new MemoryStream(512))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubCraftFinished);
                bw.Write(NextSequence++);
                bw.Write(craft.id);
                bw.Write(completion.LeaseRevision);
                bw.Write(completion.UniqueId);
                bw.Write(completion.ObjId ?? string.Empty);
                bw.Write(completion.CustomTag ?? string.Empty);
                bw.Write(completion.Position.x);
                bw.Write(completion.Position.y);
                bw.Write(completion.Position.z);
                bw.Write(finalState != null);
                if (finalState != null)
                    WriteEntry(bw, finalState);
                bw.Flush();
                SteamP2PManager.Instance?.SendCraftSyncToHost(
                    stream.ToArray());
            }

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Sent client craft completion: {craft.id}, " +
                $"uid={completion.UniqueId}, revision={completion.LeaseRevision}, " +
                $"has_final_state={finalState != null}" +
                (finalState != null
                    ? $", final_crafting={finalState.IsCrafting}, final_progress={finalState.Progress:F3}"
                    : string.Empty));
        }

        protected override byte[] CaptureSnapshot(out string fingerprint)
        {
            fingerprint = "";
            var objects = WGORegistry.Instance?.SnapshotAll() ?? MainGame.me?.GetListOfWorldObjects();
            if (objects == null) return null;

            var craftEntries = new List<CraftStateEntry>();
            var fingerprintBuilder = new StringBuilder();

            if (IsHost)
                ExpireRemoteLeases(Time.realtimeSinceStartup);

            for (int i = 0; i < objects.Count; i++)
            {
                var wgo = objects[i];
                if (wgo == null) continue;

                var craft = wgo.components?.craft;
                if (craft == null) continue;
                if (IsDynamicMobCraft(craft)) continue;

                if (IsHost && remoteCraftAuthority.ContainsKey(wgo.unique_id))
                {
                    // The host creates player-selected crafts, but the player who
                    // is actually holding the work action owns live progress.
                    // Publishing the host's idle copy would rewind that worker.
                    continue;
                }

                bool hasActiveState = craft.is_crafting || wgo.progress > 0f;
                bool hasQueue = craft.craft_queue != null && craft.craft_queue.Count > 0;
                bool isDirty = dirtyWgoUniqueIds.Contains(wgo.unique_id);
                if (!hasActiveState && !hasQueue && !isDirty) continue;
                if (!IsHost &&
                    !ShouldPreserveLocalCraftState(wgo.unique_id))
                {
                    // Clients publish only crafts they initiated. Sending every
                    // active world craft made remotely spawned/automatic crafts
                    // compete with the host's canonical state.
                    continue;
                }

                var entry = CaptureEntry(wgo, craft);
                if (entry != null)
                {
                    craftEntries.Add(entry);
                    fingerprintBuilder.Append(entry.GetKey());
                }
            }

            dirtyWgoUniqueIds.Clear();

            if (craftEntries.Count == 0) return null;

            fingerprint = fingerprintBuilder.ToString();

            using (var stream = new MemoryStream(4096))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubSnapshot);
                bw.Write(NextSequence++);

                bw.Write((ushort)craftEntries.Count);
                for (int i = 0; i < craftEntries.Count; i++)
                {
                    WriteEntry(bw, craftEntries[i]);
                }

                bw.Flush();
                return stream.ToArray();
            }
        }

        protected override void BroadcastPayload(byte[] payload)
        {
            if (IsHost)
            {
                SteamP2PManager.Instance?.BroadcastCraftSync(payload);
            }
            else
            {
                SteamP2PManager.Instance?.SendCraftSyncToHost(payload);
            }
        }

        private CraftStateEntry CaptureEntry(WorldGameObject wgo, CraftComponent craft)
        {
            try
            {
                var sc = craft.GetSerializedCraftComponent();

                var entry = new CraftStateEntry
                {
                    UniqueId = wgo.unique_id,
                    ObjId = wgo.obj_id ?? "",
                    CustomTag = wgo.custom_tag ?? "",
                    PosX = wgo.transform.position.x,
                    PosY = wgo.transform.position.y,
                    PosZ = wgo.transform.position.z,
                    Available = sc.available,
                    IsCrafting = sc.is_crafting,
                    CurrentCraftId = sc.cur_craft_id ?? "",
                    CraftAmount = sc.craft_amount,
                    Progress = wgo.progress,
                    CurItemId = sc.cur_item_id ?? "",
                    CurItemDur = sc.cur_item_dur,
                    DurItemId = sc.dur_item_id ?? "",
                    DurItemDur = sc.dur_item_dur,
                    MultiqualityItemId = sc.multiquality_item_id ?? "",
                    HasMultiqualityResult = sc.multiquality_craft_result != null,
                    MultiqualityResultValueItems = sc.multiquality_craft_result?.value_items ?? 0f,
                    MultiqualityResultValuePerks = sc.multiquality_craft_result?.value_perks ?? 0f,
                    MultiqualityResultValueDifficulty = sc.multiquality_craft_result?.value_difficulty ?? 0f,
                    MultiqualityResultQp1 = sc.multiquality_craft_result?.qp_1 ?? 0f,
                    MultiqualityResultQp2 = sc.multiquality_craft_result?.qp_2 ?? 0f,
                    MultiqualityResultQp3 = sc.multiquality_craft_result?.qp_3 ?? 0f,
                    LastCraftId = sc.last_craft_id ?? "",
                    LastCraftId2 = sc.last_craft_id_2 ?? "",
                    CurLastCraftSlot = sc.cur_last_craft_slot,
                    IsGratitudeSpent = sc.is_gratitude_points_spent_for_craft,
                    IsCurrentCraftGratitude = wgo.is_current_craft_gratitude,
                    WorkerIsPaused = craft.worker_is_paused
                };

                if (sc.queue != null && sc.queue.Count > 0)
                {
                    int count = Mathf.Min(sc.queue.Count, MaxQueueItems);
                    entry.Queue = new List<CraftQueueEntry>(count);
                    for (int i = 0; i < count; i++)
                    {
                        var qi = sc.queue[i];
                        entry.Queue.Add(new CraftQueueEntry
                        {
                            Id = qi.id ?? "",
                            N = qi.n,
                            Infinite = qi.infinite,
                            IsGratitudePointsCraft = qi.is_gratitude_points_craft
                        });
                    }
                }
                else
                {
                    entry.Queue = new List<CraftQueueEntry>(0);
                }

                return entry;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Error capturing craft entry for {wgo.obj_id}: {ex.Message}");
                return null;
            }
        }

        private static void WriteEntry(BinaryWriter bw, CraftStateEntry entry)
        {
            bw.Write(entry.UniqueId);
            bw.Write(entry.ObjId);
            bw.Write(entry.CustomTag);
            bw.Write(entry.PosX);
            bw.Write(entry.PosY);
            bw.Write(entry.PosZ);
            bw.Write(entry.Available);
            bw.Write(entry.IsCrafting);
            bw.Write(entry.CurrentCraftId);
            bw.Write(entry.CraftAmount);
            bw.Write(entry.Progress);
            bw.Write(entry.CurItemId);
            bw.Write(entry.CurItemDur);
            bw.Write(entry.DurItemId);
            bw.Write(entry.DurItemDur);
            bw.Write(entry.MultiqualityItemId);
            bw.Write(entry.HasMultiqualityResult);
            if (entry.HasMultiqualityResult)
            {
                bw.Write(entry.MultiqualityResultValueItems);
                bw.Write(entry.MultiqualityResultValuePerks);
                bw.Write(entry.MultiqualityResultValueDifficulty);
                bw.Write(entry.MultiqualityResultQp1);
                bw.Write(entry.MultiqualityResultQp2);
                bw.Write(entry.MultiqualityResultQp3);
            }
            bw.Write(entry.LastCraftId);
            bw.Write(entry.LastCraftId2);
            bw.Write(entry.CurLastCraftSlot);
            bw.Write(entry.IsGratitudeSpent);
            bw.Write(entry.IsCurrentCraftGratitude);
            bw.Write(entry.WorkerIsPaused);

            if (entry.Queue != null)
            {
                bw.Write((ushort)entry.Queue.Count);
                for (int i = 0; i < entry.Queue.Count; i++)
                {
                    bw.Write(entry.Queue[i].Id);
                    bw.Write(entry.Queue[i].N);
                    bw.Write(entry.Queue[i].Infinite);
                    bw.Write(entry.Queue[i].IsGratitudePointsCraft);
                }
            }
            else
            {
                bw.Write((ushort)0);
            }
        }

        private void OnCraftSyncReceived(CSteamID senderID, byte[] payload)
        {
            if (!IsSyncEnabled) return;
            if (payload == null || payload.Length == 0) return;
            if (!IsOnline) return;

            try
            {
                using (var stream = new MemoryStream(payload))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    byte version = reader.ReadByte();
                    if (version != PayloadVersion)
                    {
                        CoopMod.Logger.LogWarning($"{LogPrefix} Unknown payload version {version}, expected {PayloadVersion}");
                        return;
                    }

                    byte subType = reader.ReadByte();
                    if (!CheckSequence(reader, senderID)) return;

                    if (subType == SubCraftFinished)
                    {
                        HandleCraftFinished(senderID, reader);
                        return;
                    }
                    if (subType == SubAuthorityClaim)
                    {
                        HandleAuthorityClaim(senderID, reader);
                        return;
                    }
                    if (subType == SubAuthorityRelease)
                    {
                        HandleAuthorityRelease(senderID, reader);
                        return;
                    }
                    if (subType == SubLeaseRequest)
                    {
                        HandleLeaseRequest(senderID, reader);
                        return;
                    }
                    if (subType == SubLeaseDecision)
                    {
                        HandleLeaseDecision(senderID, reader);
                        return;
                    }
                    if (subType != SubSnapshot)
                    {
                        CoopMod.Logger.LogWarning(
                            $"{LogPrefix} Unknown payload subtype {subType}");
                        return;
                    }

                    int entryCount = reader.ReadUInt16();

                    for (int i = 0; i < entryCount; i++)
                    {
                        var entry = ReadEntry(reader);
                        if (entry == null) continue;

                        WorldGameObject wgo = ResolveWGO(entry);
                        if (wgo == null)
                        {
                            CoopMod.Logger.LogDebug($"{LogPrefix} Could not resolve WGO: uid={entry.UniqueId}, obj_id={entry.ObjId}, tag={entry.CustomTag}");
                            continue;
                        }

                        if (IsHost &&
                            (!remoteCraftAuthority.TryGetValue(wgo.unique_id, out ulong owner) ||
                             owner != senderID.m_SteamID))
                        {
                            CoopMod.Logger.LogWarning(
                                $"{LogPrefix} Ignored craft state from a peer without work authority: " +
                                $"sender={senderID.m_SteamID}, obj={wgo.obj_id}, uid={wgo.unique_id}");
                            continue;
                        }

                        bool applied = ApplyEntry(wgo, entry);
                        if (applied && IsHost)
                        {
                            if (remoteCraftAuthority.TryGetValue(wgo.unique_id, out ulong activeOwner) &&
                                activeOwner == senderID.m_SteamID)
                            {
                                remoteCraftAuthorityExpiresAt[wgo.unique_id] =
                                    Time.realtimeSinceStartup + StationLeaseSeconds;
                            }
                            if (!entry.IsCrafting)
                            {
                                if (remoteCraftAuthority.TryGetValue(
                                        wgo.unique_id,
                                        out ulong releasedOwner))
                                {
                                    RememberReleasedRemoteLease(
                                        wgo.unique_id,
                                        releasedOwner);
                                }
                                remoteCraftAuthority.Remove(wgo.unique_id);
                                remoteCraftAuthorityExpiresAt.Remove(wgo.unique_id);
                                IncrementLeaseRevision(wgo.unique_id);
                            }
                            // Relay the accepted client mutation as host-canonical
                            // state, including an inactive final entry.
                            dirtyWgoUniqueIds.Add(wgo.unique_id);
                        }
                    }

                    ApplyEchoSuppress();
                    if (IsHost)
                    {
                        ForceNextSend = true;
                    }
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Error processing snapshot: {ex.Message}");
            }
        }

        private void HandleLeaseRequest(CSteamID senderID, BinaryReader reader)
        {
            uint requestId = reader.ReadUInt32();
            long uniqueId = reader.ReadInt64();
            string objId = reader.ReadString();
            string customTag = reader.ReadString();
            var position = new Vector3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle());
            string craftId = reader.ReadString();

            var onlineCoop = OnlineCoopManager.Instance;
            if (!IsHost ||
                onlineCoop == null ||
                !onlineCoop.IsRemotePlayer(senderID))
            {
                return;
            }

            var identity = new CraftStateEntry
            {
                UniqueId = uniqueId,
                ObjId = objId,
                CustomTag = customTag,
                PosX = position.x,
                PosY = position.y,
                PosZ = position.z
            };
            WorldGameObject wgo = ResolveWGO(identity);
            CraftComponent craft = wgo?.components?.craft;
            CraftDefinition requestedCraft = string.IsNullOrEmpty(craftId)
                ? null
                : GameBalance.me?.GetDataOrNull<CraftDefinition>(craftId);
            long canonicalId = wgo?.unique_id ?? uniqueId;
            float now = Time.realtimeSinceStartup;
            ExpireRemoteLease(canonicalId, now);

            bool validStation = ShouldArbitrateStation(craft, requestedCraft);
            bool localBusy = canonicalId > 0L && locallyOwnedCrafts.Contains(canonicalId);
            bool remoteBusy = canonicalId > 0L &&
                              remoteCraftAuthority.TryGetValue(canonicalId, out ulong owner) &&
                              owner != senderID.m_SteamID;
            bool sameOwner = canonicalId > 0L &&
                             remoteCraftAuthority.TryGetValue(canonicalId, out owner) &&
                             owner == senderID.m_SteamID;
            bool granted = validStation && !localBusy && !remoteBusy;
            string reason = granted
                ? string.Empty
                : !validStation ? "invalid station or craft" : "station is already in use";

            if (granted)
            {
                locallyOwnedCrafts.Remove(canonicalId);
                localAuthorityReleaseAt.Remove(canonicalId);
                recentlyReleasedRemoteLeases.Remove(canonicalId);
                remoteCraftAuthority[canonicalId] = senderID.m_SteamID;
                remoteCraftAuthorityExpiresAt[canonicalId] = now + StationLeaseSeconds;
                if (!sameOwner)
                    IncrementLeaseRevision(canonicalId);
            }

            uint revision = stationLeaseRevisions.TryGetValue(canonicalId, out uint currentRevision)
                ? currentRevision
                : 0U;
            SendLeaseDecision(
                senderID,
                requestId,
                canonicalId,
                revision,
                granted,
                reason);

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} {(granted ? "Granted" : "Rejected")} station lease " +
                $"request={requestId} peer={senderID.m_SteamID} obj='{objId}' uid={canonicalId} " +
                $"craft='{craftId}' revision={revision}{(string.IsNullOrEmpty(reason) ? string.Empty : " reason=" + reason)}");
        }

        private void SendLeaseDecision(
            CSteamID target,
            uint requestId,
            long uniqueId,
            uint revision,
            bool granted,
            string reason)
        {
            using (var stream = new MemoryStream(96))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubLeaseDecision);
                bw.Write(NextSequence++);
                bw.Write(target.m_SteamID);
                bw.Write(requestId);
                bw.Write(uniqueId);
                bw.Write(revision);
                bw.Write(granted);
                bw.Write(reason ?? string.Empty);
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastCraftSync(stream.ToArray());
            }
        }

        private void HandleLeaseDecision(CSteamID senderID, BinaryReader reader)
        {
            ulong targetSteamId = reader.ReadUInt64();
            uint requestId = reader.ReadUInt32();
            long uniqueId = reader.ReadInt64();
            uint revision = reader.ReadUInt32();
            bool granted = reader.ReadBoolean();
            string reason = reader.ReadString();

            if (IsHost ||
                senderID != SteamLobbyManager.Instance?.GetLobbyOwner() ||
                targetSteamId != SteamUser.GetSteamID().m_SteamID)
            {
                return;
            }

            if (!pendingLeaseRequests.TryGetValue(uniqueId, out PendingLeaseRequest pending) ||
                pending.RequestId != requestId)
            {
                return;
            }

            pendingLeaseRequests.Remove(uniqueId);
            if (!granted)
            {
                stationLeaseRetryAt[uniqueId] =
                    Time.realtimeSinceStartup + RejectedLeaseRetrySeconds;
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Station lease rejected for uid={uniqueId}: {reason}");
                GraveyardKeeperCoop.Utils.ChatManager.AddMessage(
                    "[System] That workstation is already in use by another player.");
                return;
            }

            stationLeaseRetryAt.Remove(uniqueId);
            clientStationLeases[uniqueId] = new ClientLease
            {
                Revision = revision,
                ExpiresAt = Time.realtimeSinceStartup + StationLeaseSeconds
            };
            locallyOwnedCrafts.Add(uniqueId);
            localAuthorityReleaseAt.Remove(uniqueId);

            try
            {
                pending.Resume?.Invoke();
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Could not resume granted craft start on uid={uniqueId}: {ex.Message}");
                ReleaseUnusedStationLease(pending.Craft);
            }
        }

        private void HandleAuthorityClaim(CSteamID senderID, BinaryReader reader)
        {
            long uniqueId = reader.ReadInt64();
            string objId = reader.ReadString();
            string customTag = reader.ReadString();
            var position = new Vector3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle());
            string craftId = reader.ReadString();
            bool isWorkContinuation = reader.ReadBoolean();

            var onlineCoop = OnlineCoopManager.Instance;
            if (!IsHost ||
                onlineCoop == null ||
                !onlineCoop.IsRemotePlayer(senderID))
            {
                return;
            }

            var identity = new CraftStateEntry
            {
                UniqueId = uniqueId,
                ObjId = objId,
                CustomTag = customTag,
                PosX = position.x,
                PosY = position.y,
                PosZ = position.z
            };
            WorldGameObject wgo = ResolveWGO(identity);
            CraftComponent craft = wgo?.components?.craft;
            CraftDefinition claimedCraft = string.IsNullOrEmpty(craftId)
                ? null
                : GameBalance.me?.GetDataOrNull<CraftDefinition>(craftId);
            bool currentMatches =
                craft?.current_craft != null &&
                craft.current_craft.id == craftId;
            bool craftIsAvailable = currentMatches;
            if (!craftIsAvailable && claimedCraft != null && craft?.crafts != null)
            {
                for (int i = 0; i < craft.crafts.Count; i++)
                {
                    if (craft.crafts[i]?.id == craftId)
                    {
                        craftIsAvailable = true;
                        break;
                    }
                }
            }

            long canonicalId = wgo?.unique_id ?? 0L;
            ExpireRemoteLease(canonicalId, Time.realtimeSinceStartup);
            bool requiresPreGrantedLease =
                ShouldArbitrateStation(craft, claimedCraft);
            bool holdsGrantedLease =
                canonicalId > 0L &&
                remoteCraftAuthority.TryGetValue(canonicalId, out ulong grantedOwner) &&
                grantedOwner == senderID.m_SteamID;
            bool conflictingLocalOwner =
                canonicalId > 0L && locallyOwnedCrafts.Contains(canonicalId);
            bool conflictingRemoteOwner =
                canonicalId > 0L &&
                remoteCraftAuthority.TryGetValue(canonicalId, out ulong owner) &&
                owner != senderID.m_SteamID;
            bool conflictingActiveCraft =
                craft?.is_crafting == true && !currentMatches;

            if (craft == null ||
                claimedCraft == null ||
                !craftIsAvailable ||
                (requiresPreGrantedLease && !holdsGrantedLease) ||
                conflictingLocalOwner ||
                conflictingRemoteOwner ||
                conflictingActiveCraft)
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Rejected craft work authority claim from " +
                    $"{senderID.m_SteamID}: craft='{craftId}', obj='{objId}', uid={uniqueId}, " +
                    $"known={claimedCraft != null}, available={craftIsAvailable}, " +
                    $"requires_pregrant={requiresPreGrantedLease}, pregranted={holdsGrantedLease}, " +
                    $"local_owner={conflictingLocalOwner}, remote_owner={conflictingRemoteOwner}, " +
                    $"active_conflict={conflictingActiveCraft}");
                return;
            }

            locallyOwnedCrafts.Remove(canonicalId);
            localAuthorityReleaseAt.Remove(canonicalId);
            recentlyReleasedRemoteLeases.Remove(canonicalId);
            remoteCraftAuthority[canonicalId] = senderID.m_SteamID;
            remoteCraftAuthorityExpiresAt[canonicalId] =
                Time.realtimeSinceStartup + StationLeaseSeconds;
            dirtyWgoUniqueIds.Remove(canonicalId);

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Granted craft work authority to {senderID.m_SteamID} " +
                $"for {wgo.obj_id} (uid={canonicalId}, craft={craftId}, " +
                $"continuation={isWorkContinuation})");
        }

        private void HandleAuthorityRelease(CSteamID senderID, BinaryReader reader)
        {
            long uniqueId = reader.ReadInt64();
            string objId = reader.ReadString();
            string customTag = reader.ReadString();
            var position = new Vector3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle());
            string craftId = reader.ReadString();

            var onlineCoop = OnlineCoopManager.Instance;
            if (!IsHost ||
                onlineCoop == null ||
                !onlineCoop.IsRemotePlayer(senderID))
            {
                return;
            }

            var identity = new CraftStateEntry
            {
                UniqueId = uniqueId,
                ObjId = objId,
                CustomTag = customTag,
                PosX = position.x,
                PosY = position.y,
                PosZ = position.z
            };
            WorldGameObject wgo = ResolveWGO(identity);
            if (wgo == null ||
                !remoteCraftAuthority.TryGetValue(wgo.unique_id, out ulong owner) ||
                owner != senderID.m_SteamID)
            {
                return;
            }

            CraftComponent craft = wgo.components?.craft;
            if (!string.IsNullOrEmpty(craftId) &&
                craft?.current_craft != null &&
                craft.current_craft.id != craftId)
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Ignored stale craft authority release from " +
                    $"{senderID.m_SteamID}: craft='{craftId}', current='{craft.current_craft.id}'");
                return;
            }

            RememberReleasedRemoteLease(wgo.unique_id, owner);
            remoteCraftAuthority.Remove(wgo.unique_id);
            remoteCraftAuthorityExpiresAt.Remove(wgo.unique_id);
            IncrementLeaseRevision(wgo.unique_id);
            dirtyWgoUniqueIds.Add(wgo.unique_id);
            ForceNextSend = true;
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Released remote craft work authority for {wgo.obj_id} " +
                $"(uid={wgo.unique_id}, progress={wgo.progress:F3})");
        }

        private void HandleCraftFinished(
            CSteamID senderID,
            BinaryReader reader)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (!IsHost ||
                onlineCoop == null ||
                !onlineCoop.IsRemotePlayer(senderID) ||
                MainGame.me?.save == null)
            {
                return;
            }

            string craftId = reader.ReadString();
            uint leaseRevision = reader.ReadUInt32();
            var identity = new CraftStateEntry
            {
                UniqueId = reader.ReadInt64(),
                ObjId = reader.ReadString(),
                CustomTag = reader.ReadString(),
                PosX = reader.ReadSingle(),
                PosY = reader.ReadSingle(),
                PosZ = reader.ReadSingle()
            };
            CraftStateEntry finalState = reader.ReadBoolean()
                ? ReadEntry(reader)
                : null;
            CraftDefinition craft =
                GameBalance.me?.GetDataOrNull<CraftDefinition>(craftId);
            WorldGameObject wgo = ResolveWGO(finalState ?? identity);
            CraftComponent component = wgo?.components?.craft;
            long uniqueId = wgo?.unique_id ?? identity.UniqueId;
            if (craft == null || uniqueId <= 0L)
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Ignored unresolved completed craft '{craftId}' " +
                    $"(uid={identity.UniqueId})");
                return;
            }

            string completionKey = senderID.m_SteamID + ":" + uniqueId + ":" +
                                   leaseRevision + ":" + craftId;
            if (leaseRevision > 0U &&
                processedRemoteCompletions.Contains(completionKey))
            {
                CoopMod.Logger.LogDebug(
                    $"{LogPrefix} Ignored duplicate remote craft completion {completionKey}");
                return;
            }

            float now = Time.realtimeSinceStartup;
            bool ownsCurrentLease =
                remoteCraftAuthority.TryGetValue(uniqueId, out ulong owner) &&
                owner == senderID.m_SteamID &&
                stationLeaseRevisions.TryGetValue(uniqueId, out uint currentRevision) &&
                currentRevision == leaseRevision;
            bool justReleasedLease =
                recentlyReleasedRemoteLeases.TryGetValue(
                    uniqueId,
                    out ReleasedRemoteLease released) &&
                released.OwnerSteamId == senderID.m_SteamID &&
                released.Revision == leaseRevision &&
                now <= released.ExpiresAt;
            bool unversionedActiveOperation =
                leaseRevision == 0U &&
                (component == null ||
                 (component.is_crafting &&
                  component.current_craft?.id == craftId)) &&
                (!remoteCraftAuthority.TryGetValue(uniqueId, out owner) ||
                 owner == senderID.m_SteamID);

            if (!ownsCurrentLease && !justReleasedLease && !unversionedActiveOperation)
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Rejected craft completion without matching station lease: " +
                    $"peer={senderID.m_SteamID}, craft='{craftId}', uid={uniqueId}, " +
                    $"revision={leaseRevision}");
                return;
            }

            if (leaseRevision > 0U)
            {
                if (processedRemoteCompletions.Count >= 8192)
                    processedRemoteCompletions.Clear();
                processedRemoteCompletions.Add(completionKey);
            }

            isApplyingRemoteCraftCompletion = true;
            try
            {
                // Apply only durable completion bookkeeping. Re-running
                // ProcessFinishedCraft here would duplicate physical outputs, and
                // CraftSyncPatches suppresses CheckKeyQuests so the client's
                // explicit QuestSync/CutsceneSync events own progression presentation.
                MainGame.me.save.OnFinishedCraft(craft);
            }
            finally
            {
                isApplyingRemoteCraftCompletion = false;
            }

            // The completion packet is captured after CraftComponent.End, so it is
            // the exact canonical next state: inactive for a one-off autopsy, or the
            // next queued/amount iteration for repeat crafts. Apply it with an
            // explicit progress reset; ordinary snapshots stay monotonic while a
            // player owns the same live operation.
            bool appliedFinalState =
                wgo != null &&
                finalState != null &&
                ApplyEntry(wgo, finalState, allowProgressReset: true);
            bool removedCurrentOwner =
                remoteCraftAuthority.TryGetValue(uniqueId, out owner) &&
                owner == senderID.m_SteamID;
            remoteCraftAuthority.Remove(uniqueId);
            remoteCraftAuthorityExpiresAt.Remove(uniqueId);
            recentlyReleasedRemoteLeases.Remove(uniqueId);
            if (removedCurrentOwner)
                IncrementLeaseRevision(uniqueId);
            if (appliedFinalState)
            {
                dirtyWgoUniqueIds.Add(uniqueId);
                ForceNextSend = true;
                SendLocalSnapshot(force: true);
            }

            TechSync.Instance?.MarkDirty();
            QuestSync.Instance?.MarkDirty();
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Applied client craft completion to host progression: " +
                $"{craftId}, uid={uniqueId}, revision={leaseRevision}, " +
                $"applied_final_state={appliedFinalState}" +
                (finalState != null
                    ? $", final_crafting={finalState.IsCrafting}, final_progress={finalState.Progress:F3}"
                    : string.Empty));
        }

        private static CraftStateEntry ReadEntry(BinaryReader reader)
        {
            var entry = new CraftStateEntry();
            entry.UniqueId = reader.ReadInt64();
            entry.ObjId = reader.ReadString();
            entry.CustomTag = reader.ReadString();
            entry.PosX = reader.ReadSingle();
            entry.PosY = reader.ReadSingle();
            entry.PosZ = reader.ReadSingle();
            entry.Available = reader.ReadBoolean();
            entry.IsCrafting = reader.ReadBoolean();
            entry.CurrentCraftId = reader.ReadString();
            entry.CraftAmount = reader.ReadInt32();
            entry.Progress = reader.ReadSingle();
            entry.CurItemId = reader.ReadString();
            entry.CurItemDur = reader.ReadSingle();
            entry.DurItemId = reader.ReadString();
            entry.DurItemDur = reader.ReadSingle();
            entry.MultiqualityItemId = reader.ReadString();
            entry.HasMultiqualityResult = reader.ReadBoolean();
            if (entry.HasMultiqualityResult)
            {
                entry.MultiqualityResultValueItems = reader.ReadSingle();
                entry.MultiqualityResultValuePerks = reader.ReadSingle();
                entry.MultiqualityResultValueDifficulty = reader.ReadSingle();
                entry.MultiqualityResultQp1 = reader.ReadSingle();
                entry.MultiqualityResultQp2 = reader.ReadSingle();
                entry.MultiqualityResultQp3 = reader.ReadSingle();
            }
            entry.LastCraftId = reader.ReadString();
            entry.LastCraftId2 = reader.ReadString();
            entry.CurLastCraftSlot = reader.ReadInt32();
            entry.IsGratitudeSpent = reader.ReadBoolean();
            entry.IsCurrentCraftGratitude = reader.ReadBoolean();
            entry.WorkerIsPaused = reader.ReadBoolean();

            int queueCount = reader.ReadUInt16();
            entry.Queue = new List<CraftQueueEntry>(queueCount);
            for (int i = 0; i < queueCount; i++)
            {
                entry.Queue.Add(new CraftQueueEntry
                {
                    Id = reader.ReadString(),
                    N = reader.ReadInt32(),
                    Infinite = reader.ReadBoolean(),
                    IsGratitudePointsCraft = reader.ReadBoolean()
                });
            }

            return entry;
        }

        private WorldGameObject ResolveWGO(CraftStateEntry entry)
        {
            if (entry.UniqueId > 0)
            {
                var wgo = WorldMap.GetWorldGameObjectByUniqueId(entry.UniqueId, false);
                if (wgo != null) return wgo;
            }

            if (!string.IsNullOrEmpty(entry.CustomTag))
            {
                var wgo = WorldMap.GetWorldGameObjectByCustomTag(entry.CustomTag, true);
                if (wgo != null) return wgo;
            }

            if (!string.IsNullOrEmpty(entry.ObjId) && MainGame.me != null)
            {
                var objects = WGORegistry.Instance?.SnapshotAll() ?? MainGame.me?.GetListOfWorldObjects();
                if (objects != null)
                {
                    WorldGameObject nearest = null;
                    float nearestDist = 96f * 96f;
                    var pos = new Vector3(entry.PosX, entry.PosY, entry.PosZ);

                    for (int i = 0; i < objects.Count; i++)
                    {
                        var candidate = objects[i];
                        if (candidate == null || candidate.obj_id != entry.ObjId) continue;
                        float dist = (candidate.transform.position - pos).sqrMagnitude;
                        if (dist < nearestDist)
                        {
                            nearestDist = dist;
                            nearest = candidate;
                        }
                    }

                    return nearest;
                }
            }

            return null;
        }

        private static bool IsDynamicMobCraft(CraftComponent craft)
        {
            // MobSpawner starts internal crafts such as bat_remove to control a mob's
            // local lifetime. They are not player work or shared progression. Sending
            // them through CraftSync caused every spawned bat to publish its timer over
            // and over, while spawn/combat/NPC sync already own the actual mob state.
            return craft?.wgo?.obj_def?.dynamic_mob == true;
        }

        private bool ShouldArbitrateStation(
            CraftComponent craft,
            CraftDefinition intendedCraft)
        {
            if (!IsSyncEnabled ||
                !IsOnline ||
                isApplyingRemoteCraftState ||
                craft?.wgo == null ||
                IsDynamicMobCraft(craft))
            {
                return false;
            }

            WorldGameObject wgo = craft.wgo;
            if (wgo.unique_id <= 0L ||
                wgo.is_removing ||
                string.Equals(wgo.obj_id, "grave_ground", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            CraftDefinition definition = intendedCraft ?? craft.current_craft;
            if (definition != null &&
                (definition.hidden ||
                 definition.is_auto ||
                 (!string.IsNullOrEmpty(definition.id) && definition.id.Contains(":r:"))))
            {
                return false;
            }

            return true;
        }

        private void ExpireRemoteLease(long uniqueId, float now)
        {
            if (uniqueId <= 0L ||
                !remoteCraftAuthorityExpiresAt.TryGetValue(uniqueId, out float expiresAt) ||
                now < expiresAt)
            {
                return;
            }

            remoteCraftAuthorityExpiresAt.Remove(uniqueId);
            if (remoteCraftAuthority.TryGetValue(uniqueId, out ulong owner))
            {
                RememberReleasedRemoteLease(uniqueId, owner);
                remoteCraftAuthority.Remove(uniqueId);
                IncrementLeaseRevision(uniqueId);
                dirtyWgoUniqueIds.Add(uniqueId);
                ForceNextSend = true;
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Expired stale station lease for uid={uniqueId}");
            }
        }

        private void ExpireRemoteLeases(float now)
        {
            if (remoteCraftAuthorityExpiresAt.Count == 0)
                return;

            expiredRemoteLeaseIds.Clear();
            foreach (var pair in remoteCraftAuthorityExpiresAt)
            {
                if (now >= pair.Value)
                    expiredRemoteLeaseIds.Add(pair.Key);
            }

            for (int i = 0; i < expiredRemoteLeaseIds.Count; i++)
                ExpireRemoteLease(expiredRemoteLeaseIds[i], now);
            expiredRemoteLeaseIds.Clear();
        }

        private uint IncrementLeaseRevision(long uniqueId)
        {
            stationLeaseRevisions.TryGetValue(uniqueId, out uint revision);
            revision++;
            stationLeaseRevisions[uniqueId] = revision;
            return revision;
        }

        private void RememberReleasedRemoteLease(long uniqueId, ulong ownerSteamId)
        {
            if (uniqueId <= 0L || ownerSteamId == 0UL)
                return;

            stationLeaseRevisions.TryGetValue(uniqueId, out uint revision);
            recentlyReleasedRemoteLeases[uniqueId] = new ReleasedRemoteLease
            {
                OwnerSteamId = ownerSteamId,
                Revision = revision,
                ExpiresAt = Time.realtimeSinceStartup + CompletedLeaseGraceSeconds
            };
        }

        private void LogStationBusy(CraftComponent craft, ulong owner)
        {
            long uniqueId = craft.wgo.unique_id;
            float now = Time.realtimeSinceStartup;
            if (stationBusyMessageAt.TryGetValue(uniqueId, out float lastAt) &&
                now - lastAt < 2f)
            {
                return;
            }
            stationBusyMessageAt[uniqueId] = now;
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Blocked craft start on {craft.wgo.obj_id} " +
                $"(uid={uniqueId}); station lease belongs to {owner}");
            GraveyardKeeperCoop.Utils.ChatManager.AddMessage(
                "[System] That workstation is already in use by another player.");
        }

        private bool ApplyEntry(
            WorldGameObject wgo,
            CraftStateEntry entry,
            bool allowProgressReset = false)
        {
            var craft = wgo.components?.craft;
            if (craft == null || IsDynamicMobCraft(craft)) return false;

            bool awaitingCanonicalCompletion =
                locallyCompletedOperations.TryGetValue(
                    wgo.unique_id,
                    out string completedCraftId);
            if (awaitingCanonicalCompletion &&
                entry.IsCrafting &&
                entry.CurrentCraftId == completedCraftId &&
                entry.Progress >= 0.999f)
            {
                CoopMod.Logger.LogDebug(
                    $"{LogPrefix} Ignored stale completed craft snapshot for " +
                    $"{wgo.obj_id} (uid={wgo.unique_id}, craft={entry.CurrentCraftId})");
                return false;
            }

            if (!awaitingCanonicalCompletion &&
                ShouldPreserveLocalCraftState(wgo.unique_id))
            {
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Ignored stale remote craft state for locally-owned " +
                    $"{wgo.obj_id} (uid={wgo.unique_id}): incoming crafting={entry.IsCrafting}, " +
                    $"craft='{entry.CurrentCraftId}', progress={entry.Progress:F2}");
                return false;
            }

            try
            {
                bool sameActiveOperation =
                    craft.is_crafting &&
                    entry.IsCrafting &&
                    craft.current_craft != null &&
                    craft.current_craft.id == entry.CurrentCraftId;
                float appliedProgress = sameActiveOperation && !allowProgressReset
                    ? Mathf.Max(wgo.progress, entry.Progress)
                    : entry.Progress;

                var serializable = new SerializableWGO.SerializableCraft
                {
                    available = entry.Available,
                    is_crafting = entry.IsCrafting,
                    cur_craft_id = entry.CurrentCraftId,
                    cur_item_id = entry.CurItemId,
                    cur_item_dur = entry.CurItemDur,
                    dur_item_id = entry.DurItemId,
                    dur_item_dur = entry.DurItemDur,
                    multiquality_item_id = entry.MultiqualityItemId,
                    multiquality_craft_result = BuildMultiqualityResult(entry),
                    craft_amount = entry.CraftAmount,
                    last_craft_id = entry.LastCraftId,
                    last_craft_id_2 = entry.LastCraftId2,
                    cur_last_craft_slot = entry.CurLastCraftSlot,
                    is_gratitude_points_spent_for_craft = entry.IsGratitudeSpent,
                    queue = BuildQueueList(entry.Queue),
                    cur_craft_items_used = new List<Item>()
                };

                isApplyingRemoteCraftState = true;
                try
                {
                    craft.DeserializeCraftComponent(serializable);
                    wgo.progress = Mathf.Clamp01(appliedProgress);
                    wgo.is_current_craft_gratitude =
                        entry.IsCurrentCraftGratitude;
                    try { craft.RefreshComponentBubbleData(false); } catch { }
                }
                finally
                {
                    isApplyingRemoteCraftState = false;
                }

                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Applied craft state to {wgo.obj_id} " +
                    $"(uid={wgo.unique_id}): crafting={entry.IsCrafting}, " +
                    $"craft={entry.CurrentCraftId}, progress={appliedProgress:F2}, " +
                    $"queue={entry.Queue?.Count ?? 0}");
                if (awaitingCanonicalCompletion &&
                    (!entry.IsCrafting ||
                     entry.CurrentCraftId != completedCraftId ||
                     entry.Progress < 0.999f))
                {
                    locallyCompletedOperations.Remove(wgo.unique_id);
                }
                return true;
            }
            catch (Exception ex)
            {
                isApplyingRemoteCraftState = false;
                CoopMod.Logger.LogWarning($"{LogPrefix} Error applying craft state to {wgo.obj_id}: {ex.Message}");
                return false;
            }
        }

        private bool ShouldPreserveLocalCraftState(long uniqueId)
        {
            if (locallyOwnedCrafts.Contains(uniqueId))
                return true;

            if (!localAuthorityReleaseAt.TryGetValue(
                    uniqueId,
                    out float releaseAt))
            {
                return false;
            }

            if (Time.realtimeSinceStartup < releaseAt)
                return true;

            localAuthorityReleaseAt.Remove(uniqueId);
            return false;
        }

        private static List<CraftComponent.CraftQueueItem> BuildQueueList(List<CraftQueueEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                return new List<CraftComponent.CraftQueueItem>();

            var result = new List<CraftComponent.CraftQueueItem>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                result.Add(new CraftComponent.CraftQueueItem
                {
                    id = entries[i].Id,
                    n = entries[i].N,
                    infinite = entries[i].Infinite,
                    is_gratitude_points_craft = entries[i].IsGratitudePointsCraft
                });
            }
            return result;
        }

        private static CraftDefinition.MultiqualityCraftResult BuildMultiqualityResult(CraftStateEntry entry)
        {
            if (!entry.HasMultiqualityResult) return null;
            var result = new CraftDefinition.MultiqualityCraftResult();
            result.value_items = entry.MultiqualityResultValueItems;
            result.value_perks = entry.MultiqualityResultValuePerks;
            result.value_difficulty = entry.MultiqualityResultValueDifficulty;
            result.qp_1 = entry.MultiqualityResultQp1;
            result.qp_2 = entry.MultiqualityResultQp2;
            result.qp_3 = entry.MultiqualityResultQp3;
            return result;
        }

        internal class CraftStateEntry
        {
            public long UniqueId;
            public string ObjId;
            public string CustomTag;
            public float PosX, PosY, PosZ;
            public bool Available;
            public bool IsCrafting;
            public string CurrentCraftId;
            public int CraftAmount;
            public float Progress;
            public string CurItemId;
            public float CurItemDur;
            public string DurItemId;
            public float DurItemDur;
            public string MultiqualityItemId;
            public bool HasMultiqualityResult;
            public float MultiqualityResultValueItems;
            public float MultiqualityResultValuePerks;
            public float MultiqualityResultValueDifficulty;
            public float MultiqualityResultQp1;
            public float MultiqualityResultQp2;
            public float MultiqualityResultQp3;
            public string LastCraftId;
            public string LastCraftId2;
            public int CurLastCraftSlot;
            public bool IsGratitudeSpent;
            public bool IsCurrentCraftGratitude;
            public bool WorkerIsPaused;
            public List<CraftQueueEntry> Queue;

            public string GetKey()
            {
                return $"{UniqueId}:{IsCrafting}:{CurrentCraftId}:{CraftAmount}:{Progress:F3}:{IsGratitudeSpent}:{IsCurrentCraftGratitude}:{Queue?.Count ?? 0}";
            }
        }

        internal class CraftQueueEntry
        {
            public string Id;
            public int N;
            public bool Infinite;
            public bool IsGratitudePointsCraft;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    public class SpawnSync : SyncBehaviour
    {
        public static SpawnSync Instance => GetInstance<SpawnSync>();
        public const string BuildingPreviewCapabilityKey = "bprev";
        public const string BuildingPreviewCapabilityValue = "1";
        internal static bool IsApplyingCanonicalFlowPlacement { get; private set; }
        internal static bool IsApplyingCanonicalWgoState { get; private set; }

        private const byte PayloadVersion = 2;
        private const int MaxSerializedWgoBytes = 4 * 1024 * 1024;
        private const byte SubWgoSpawned = 0;
        private const byte SubWgoReplaced = 1;
        private const byte SubFlowPlacementRequest = 2;
        private const byte SubCanonicalFlowPlacement = 3;
        private const byte SubWgoReplaceRequest = 4;
        private const byte SubBuildingPlacementRequest = 5;
        private const byte SubCanonicalBuildingPlacement = 6;
        private const byte SubBuildingPreviewRequest = 7;
        private const byte SubCanonicalBuildingPreview = 8;
        private const byte SubBuildingPreviewEndRequest = 9;
        private const byte SubCanonicalBuildingPreviewEnd = 10;
        private const byte SubCutsceneActorSpawnRequest = 11;
        private const byte SubCanonicalCutsceneActorSpawn = 12;
        private const byte SubPuffFxRequest = 13;
        private const byte SubCanonicalPuffFx = 14;
        private const byte SubInteractionEventsRequest = 15;
        private const byte SubCanonicalInteractionEvents = 16;
        private const long TemporaryPlacementIdMin = 1000000000L;
        private const long TemporaryPlacementIdMax = 1499999999L;
        private const float PlacementRetrySeconds = 2f;
        private const float HostSpawnSettleSeconds = 0.05f;
        private const float PreviewSendIntervalSeconds = 0.08f;
        private const float PreviewHeartbeatSeconds = 0.5f;
        private const float PreviewTimeoutSeconds = 3f;
        private const float PreviewGridEndGraceSeconds = 0.4f;
        private const int PreviewEndRepeatCount = 3;
        private const int MaxPreviewFootprintCells = 512;
        private const float PuffFxRelayDedupeSeconds = 0.5f;
        private const int MaxInteractionEvents = 64;
        private const int MaxInteractionEventLength = 256;
        private static uint nextBuildingPreviewSession =
            unchecked((uint)Environment.TickCount);

        private sealed class PendingBuildingPlacement
        {
            public long TemporaryUniqueId;
            public WorldGameObject Wgo;
            public float NextRetryAt;
            public int Attempts;
            public bool DeferredMutation;
        }

        private sealed class BuildingPreviewState
        {
            public uint Session;
            public uint LastSequence;
            public bool Ended;
            public string ObjId;
            public string ZoneId;
            public string CustomSubZoneId;
            public WorldGameObject Wgo;
            public float LastUpdateAt;
        }

        private sealed class PendingCutsceneActorSpawn
        {
            public WorldGameObject Wgo;
            public float SendAt;
        }

        private sealed class PendingHostWgoSpawn
        {
            public WorldGameObject Wgo;
            public float SendAt;
        }

        private struct BuildingPreviewPose
        {
            public string ObjId;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
            public int Variation;
            public int Variation2;
            public bool CanBuild;
            public string ZoneId;
            public string CustomSubZoneId;
            public List<Vector2> FootprintCells;
        }

        private static readonly FieldInfo CurrentBuildZoneIdField =
            AccessTools.Field(typeof(BuildModeLogics), "_cur_build_zone_id");
        private static readonly FieldInfo FloatingFlowGridCellsField =
            AccessTools.Field(typeof(FloatingWorldGameObject), "_cells");

        private readonly Dictionary<long, PendingBuildingPlacement> pendingBuildingPlacements =
            new Dictionary<long, PendingBuildingPlacement>();
        private readonly Dictionary<string, long> canonicalPlacementIds =
            new Dictionary<string, long>();
        private readonly Dictionary<ulong, BuildingPreviewState> remoteBuildingPreviews =
            new Dictionary<ulong, BuildingPreviewState>();
        private readonly Dictionary<int, PendingCutsceneActorSpawn> pendingCutsceneActorSpawns =
            new Dictionary<int, PendingCutsceneActorSpawn>();
        private readonly Dictionary<int, PendingHostWgoSpawn> pendingHostWgoSpawns =
            new Dictionary<int, PendingHostWgoSpawn>();
        private readonly HashSet<int> sentCutsceneActorSpawns =
            new HashSet<int>();
        private readonly Dictionary<long, float> lastPuffFxRelayAt =
            new Dictionary<long, float>();
        private readonly Dictionary<long, float> lastBishopStockBroadcastAt =
            new Dictionary<long, float>();
        private long nextTemporaryPlacementId = TemporaryPlacementIdMax;
        private WorldGameObject localBuildingPreview;
        private uint localPreviewSession;
        private uint localPreviewSequence;
        private float nextPreviewSendAt;
        private float lastPreviewSentAt;
        private bool localPreviewDirty;
        private bool hasLastPreviewPose;
        private BuildingPreviewPose lastPreviewPose;
        private ulong presentedRemoteBuilder;
        private string presentedRemoteBuildZone = string.Empty;
        private float remoteBuildGridHideAt;
        private WorldZone presentedRemoteQualityZone;
        private readonly Dictionary<WorldGameObject, bool>
            remoteQualityHintOriginalStates =
                new Dictionary<WorldGameObject, bool>();

        protected override string LogPrefix => "[SpawnSync]";

        /// <summary>
        /// Steam's receive API does not report whether a packet was sent reliable or
        /// unreliable. Identify the transient preview envelopes precisely so RNET's
        /// mixed-lane guard admits them without admitting ordinary reliable SpawnSync.
        /// </summary>
        internal static bool IsBuildingPreviewWirePacket(byte[] data, int length)
        {
            const int envelopeHeaderBytes = 5; // Op byte + Int32 payload length.
            if (data == null || length < envelopeHeaderBytes + 2 ||
                length > data.Length || data[0] != (byte)Op.SpawnSync)
            {
                return false;
            }

            int payloadLength = BitConverter.ToInt32(data, 1);
            if (payloadLength != length - envelopeHeaderBytes ||
                data[envelopeHeaderBytes] != PayloadVersion)
            {
                return false;
            }

            byte subType = data[envelopeHeaderBytes + 1];
            return subType == SubBuildingPreviewRequest ||
                   subType == SubCanonicalBuildingPreview ||
                   subType == SubBuildingPreviewEndRequest ||
                   subType == SubCanonicalBuildingPreviewEnd;
        }

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnSpawnSyncReceived -= OnSpawnSyncReceived;
                SteamP2PManager.Instance.OnSpawnSyncReceived += OnSpawnSyncReceived;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnSpawnSyncReceived -= OnSpawnSyncReceived;
            }
        }

        protected override void OnSyncEnabled()
        {
            ClearBuildingPreviews();
            pendingBuildingPlacements.Clear();
            canonicalPlacementIds.Clear();
            pendingCutsceneActorSpawns.Clear();
            pendingHostWgoSpawns.Clear();
            sentCutsceneActorSpawns.Clear();
            lastPuffFxRelayAt.Clear();
            lastBishopStockBroadcastAt.Clear();
            nextTemporaryPlacementId = TemporaryPlacementIdMax;
        }

        protected override void OnSyncDisabled()
        {
            ClearBuildingPreviews();
            pendingBuildingPlacements.Clear();
            canonicalPlacementIds.Clear();
            pendingCutsceneActorSpawns.Clear();
            pendingHostWgoSpawns.Clear();
            sentCutsceneActorSpawns.Clear();
            lastPuffFxRelayAt.Clear();
            lastBishopStockBroadcastAt.Clear();
            nextTemporaryPlacementId = TemporaryPlacementIdMax;
        }

        private void Update()
        {
            if (!IsSyncEnabled || !IsOnline)
                return;

            float now = Time.realtimeSinceStartup;
            FlushPendingHostWgoSpawns(now);
            FlushPendingCutsceneActorSpawns(now);
            TickLocalBuildingPreview(now);
            RemoveTimedOutBuildingPreviews(now);
            if (remoteBuildGridHideAt > 0f &&
                now >= remoteBuildGridHideAt)
            {
                RefreshRemoteBuildGridPresentation();
            }

            if (IsHost || pendingBuildingPlacements.Count == 0)
                return;

            var pending = new List<PendingBuildingPlacement>(
                pendingBuildingPlacements.Values);
            for (int i = 0; i < pending.Count; i++)
            {
                PendingBuildingPlacement placement = pending[i];
                if (placement == null || now < placement.NextRetryAt)
                    continue;

                if (placement.Wgo == null || placement.Wgo.is_removed)
                {
                    pendingBuildingPlacements.Remove(placement.TemporaryUniqueId);
                    continue;
                }

                SendBuildingPlacementRequest(placement);
            }
        }

        /// <summary>
        /// Client previews must not retain a vanilla sequential ID. Every peer loads the
        /// same iterator from the host save, so independently created previews otherwise
        /// reuse IDs belonging to host objects or another builder. Temporary IDs never
        /// leave the requesting client as authoritative world IDs.
        /// </summary>
        internal void PrepareLocalBuildingPreview(WorldGameObject wgo)
        {
            if (!IsSyncEnabled || !IsOnline || wgo == null ||
                string.IsNullOrEmpty(wgo.obj_id) || wgo.obj_id == "0" ||
                wgo.obj_id == "_cursor" ||
                wgo.GetComponent<RemoteBuildingPreviewMarker>() != null)
            {
                return;
            }

            if (!IsHost && !IsTemporaryPlacementId(wgo.unique_id))
            {
                long oldUniqueId = wgo.unique_id;
                long temporaryUniqueId = AllocateTemporaryPlacementId();
                if (temporaryUniqueId == 0L ||
                    !TryRemapWgoUniqueId(wgo, temporaryUniqueId))
                {
                    CoopMod.Logger.LogWarning(
                        $"{LogPrefix} Could not reserve a temporary ID for building preview " +
                        $"uid={oldUniqueId}, obj_id={wgo.obj_id}");
                    return;
                }

                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Reserved client building preview ID: " +
                    $"old_uid={oldUniqueId}, temp_uid={temporaryUniqueId}, obj_id={wgo.obj_id}");
            }

            BeginLocalBuildingPreview(wgo);
        }

        private void BeginLocalBuildingPreview(WorldGameObject wgo)
        {
            if (localBuildingPreview == wgo)
            {
                localPreviewDirty = true;
                return;
            }

            if (localBuildingPreview != null)
                EndLocalBuildingPreview();

            localBuildingPreview = wgo;
            nextBuildingPreviewSession++;
            if (nextBuildingPreviewSession == 0)
                nextBuildingPreviewSession++;
            localPreviewSession = nextBuildingPreviewSession;
            localPreviewSequence = 0;
            nextPreviewSendAt = 0f;
            lastPreviewSentAt = 0f;
            localPreviewDirty = true;
            hasLastPreviewPose = false;
            InventorySync.InvalidateEligibleWgoCache();
            SendLocalBuildingPreview(Time.realtimeSinceStartup);
        }

        internal void NotifyBuildingPreviewChanged(
            WorldGameObject wgo,
            bool canBuild)
        {
            if (!IsSyncEnabled || !IsOnline || wgo == null ||
                wgo.GetComponent<RemoteBuildingPreviewMarker>() != null)
            {
                return;
            }

            if (localBuildingPreview != wgo)
                PrepareLocalBuildingPreview(wgo);
            if (localBuildingPreview != wgo)
                return;

            localPreviewDirty = true;
            float now = Time.realtimeSinceStartup;
            if (now >= nextPreviewSendAt)
                SendLocalBuildingPreview(now, canBuild);
        }

        internal void NotifyBuildingPreviewEnded(WorldGameObject wgo)
        {
            if (wgo == null || localBuildingPreview != wgo)
                return;

            EndLocalBuildingPreview();
        }

        internal void OnPeerLeftLobby(CSteamID peer)
        {
            if (peer == CSteamID.Nil ||
                !remoteBuildingPreviews.TryGetValue(
                    peer.m_SteamID,
                    out BuildingPreviewState state))
            {
                return;
            }

            uint sequence = state.LastSequence + 1;
            if (sequence == 0)
                sequence = state.LastSequence;
            ApplyBuildingPreviewEnd(
                peer.m_SteamID,
                state.Session,
                sequence);

            if (IsHost)
            {
                BroadcastCanonicalBuildingPreviewEnd(
                    peer.m_SteamID,
                    state.Session,
                    sequence,
                    peer);
            }
            DestroyRemoteBuildingPreview(state);
            remoteBuildingPreviews.Remove(peer.m_SteamID);
        }

        private void TickLocalBuildingPreview(float now)
        {
            if (localBuildingPreview == null ||
                localBuildingPreview.is_removed ||
                FloatingWorldGameObject.cur_floating?.wobj != localBuildingPreview)
            {
                if (localBuildingPreview != null)
                    EndLocalBuildingPreview();
                return;
            }

            if (now < nextPreviewSendAt)
                return;

            BuildingPreviewPose current = CaptureBuildingPreviewPose(
                localBuildingPreview,
                FloatingWorldGameObject.can_be_built);
            bool changed = !hasLastPreviewPose ||
                           !PreviewPosesEqual(current, lastPreviewPose);
            if (localPreviewDirty || changed ||
                now - lastPreviewSentAt >= PreviewHeartbeatSeconds)
            {
                SendLocalBuildingPreview(now, current.CanBuild);
            }
        }

        private void SendLocalBuildingPreview(float now, bool? canBuild = null)
        {
            if (localBuildingPreview == null ||
                string.IsNullOrEmpty(localBuildingPreview.obj_id) ||
                localBuildingPreview.obj_id == "0")
            {
                return;
            }

            BuildingPreviewPose pose = CaptureBuildingPreviewPose(
                localBuildingPreview,
                canBuild ?? FloatingWorldGameObject.can_be_built);
            localPreviewSequence++;
            if (localPreviewSequence == 0)
                localPreviewSequence++;

            byte subType = IsHost
                ? SubCanonicalBuildingPreview
                : SubBuildingPreviewRequest;
            byte[] payload = SerializeBuildingPreview(
                subType,
                IsHost ? SteamUser.GetSteamID().m_SteamID : 0UL,
                localPreviewSession,
                localPreviewSequence,
                pose);

            if (IsHost)
                SteamP2PManager.Instance?.BroadcastSpawnPreview(payload);
            else
                SteamP2PManager.Instance?.SendSpawnPreviewToHost(payload);

            lastPreviewPose = pose;
            hasLastPreviewPose = true;
            localPreviewDirty = false;
            lastPreviewSentAt = now;
            nextPreviewSendAt = now + PreviewSendIntervalSeconds;
        }

        private void EndLocalBuildingPreview()
        {
            if (localBuildingPreview == null)
                return;

            localPreviewSequence++;
            if (localPreviewSequence == 0)
                localPreviewSequence++;

            byte subType = IsHost
                ? SubCanonicalBuildingPreviewEnd
                : SubBuildingPreviewEndRequest;
            byte[] payload = SerializeBuildingPreviewEnd(
                subType,
                IsHost ? SteamUser.GetSteamID().m_SteamID : 0UL,
                localPreviewSession,
                localPreviewSequence);

            if (IsSyncEnabled && IsOnline)
            {
                for (int i = 0; i < PreviewEndRepeatCount; i++)
                {
                    if (IsHost)
                        SteamP2PManager.Instance?.BroadcastSpawnPreview(payload);
                    else
                        SteamP2PManager.Instance?.SendSpawnPreviewToHost(payload);
                }
            }

            localBuildingPreview = null;
            localPreviewDirty = false;
            hasLastPreviewPose = false;
            nextPreviewSendAt = 0f;
            lastPreviewSentAt = 0f;
            InventorySync.InvalidateEligibleWgoCache();
        }

        /// <summary>Called after BuildModeLogics.DoPlace has committed the final grid position.</summary>
        internal void NotifyBuildingPlaced(WorldGameObject wgo)
        {
            if (!IsSyncEnabled || !IsOnline || IsApplyingCanonicalWgoState ||
                wgo == null || wgo.is_removed || wgo.is_player ||
                wgo.GetComponent<PlayerComponent>() != null ||
                wgo.GetComponent<RemoteBuildingPreviewMarker>() != null ||
                string.IsNullOrEmpty(wgo.obj_id) || wgo.obj_id == "0")
            {
                return;
            }

            if (MainGame.me?.world_root != null &&
                !wgo.transform.IsChildOf(MainGame.me.world_root))
            {
                return;
            }

            if (IsHost)
            {
                SendWgoSpawned(wgo);
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Broadcast committed host building placement: " +
                    $"uid={wgo.unique_id}, obj_id={wgo.obj_id}, pos={wgo.transform.position}");
                return;
            }

            if (!IsTemporaryPlacementId(wgo.unique_id))
                PrepareLocalBuildingPreview(wgo);
            if (!IsTemporaryPlacementId(wgo.unique_id))
                return;

            long temporaryUniqueId = wgo.unique_id;
            var pending = new PendingBuildingPlacement
            {
                TemporaryUniqueId = temporaryUniqueId,
                Wgo = wgo,
                NextRetryAt = 0f
            };
            pendingBuildingPlacements[temporaryUniqueId] = pending;
            SendBuildingPlacementRequest(pending);
        }

        private void SendBuildingPlacementRequest(PendingBuildingPlacement pending)
        {
            if (pending?.Wgo == null ||
                !TrySerializeWgo(pending.Wgo, out string json))
            {
                return;
            }

            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            using (var stream = new MemoryStream(jsonBytes.Length + 48))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PayloadVersion);
                writer.Write(SubBuildingPlacementRequest);
                writer.Write(NextSequence++);
                writer.Write(pending.TemporaryUniqueId);
                writer.Write(pending.Wgo.obj_id ?? string.Empty);
                writer.Write(jsonBytes.Length);
                writer.Write(jsonBytes);
                writer.Flush();
                SteamP2PManager.Instance?.SendSpawnSyncToHost(stream.ToArray());
            }

            pending.Attempts++;
            pending.NextRetryAt = Time.realtimeSinceStartup + PlacementRetrySeconds;
            string action = pending.Attempts == 1 ? "Requested" : "Retried";
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} {action} canonical building placement: " +
                $"temp_uid={pending.TemporaryUniqueId}, obj_id={pending.Wgo.obj_id}, " +
                $"pos={pending.Wgo.transform.position}, attempt={pending.Attempts}");
        }

        /// <summary>
        /// WorldMap.OnAddNewWGO runs inside WorldMap.SpawnWGO, before callers such as
        /// GS.Spawn assign the custom tag and finish initializing the object. Serialize
        /// on a later update so peers receive the completed identity and initial state.
        /// </summary>
        internal void QueueHostWgoSpawn(WorldGameObject wgo)
        {
            if (!IsSyncEnabled || !IsOnline || !IsHost ||
                IsApplyingCanonicalWgoState || wgo == null || wgo.is_removed ||
                wgo.unique_id <= 0L || wgo.is_player ||
                wgo.GetComponent<PlayerComponent>() != null ||
                string.IsNullOrEmpty(wgo.obj_id) || wgo.obj_id == "0")
            {
                return;
            }

            pendingHostWgoSpawns[wgo.GetInstanceID()] =
                new PendingHostWgoSpawn
                {
                    Wgo = wgo,
                    SendAt = Time.realtimeSinceStartup + HostSpawnSettleSeconds
                };
        }

        private void FlushPendingHostWgoSpawns(float now)
        {
            if (!IsHost || pendingHostWgoSpawns.Count == 0)
                return;

            var instanceIds = new List<int>(pendingHostWgoSpawns.Keys);
            for (int i = 0; i < instanceIds.Count; i++)
            {
                int instanceId = instanceIds[i];
                if (!pendingHostWgoSpawns.TryGetValue(
                        instanceId,
                        out PendingHostWgoSpawn pending) ||
                    pending == null || now < pending.SendAt)
                {
                    continue;
                }

                pendingHostWgoSpawns.Remove(instanceId);
                WorldGameObject wgo = pending.Wgo;
                if (wgo == null || wgo.is_removed || wgo.unique_id <= 0L)
                    continue;

                SendWgoSpawned(wgo);
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Broadcast settled host WGO spawn: " +
                    $"uid={wgo.unique_id}, obj_id={wgo.obj_id}, tag={wgo.custom_tag}");
            }
        }

        /// <summary>
        /// A client-owned FlowScript can create a story NPC that does not exist on the
        /// host yet. Queue the request until the current Flow call stack has unwound so
        /// follow-up nodes have time to assign the actor's custom tag and initial state.
        /// </summary>
        internal void RequestCanonicalCutsceneActorSpawn(WorldGameObject wgo)
        {
            if (!IsSyncEnabled || !IsOnline || IsHost ||
                IsApplyingCanonicalWgoState || wgo == null || wgo.is_removed ||
                wgo.is_player || wgo.GetComponent<PlayerComponent>() != null ||
                string.IsNullOrEmpty(wgo.obj_id) || wgo.obj_id == "0" ||
                !IsNpcDefinition(wgo.obj_id) ||
                !(GraveyardKeeperCoop.Patches.CutsceneSyncPatches
                      .HasLocalNpcVisualAuthority() ||
                  GraveyardKeeperCoop.Patches.NpcInteractionSyncPatches
                      .HasLocalNpcVisualAuthority()))
            {
                return;
            }

            int instanceId = wgo.GetInstanceID();
            if (sentCutsceneActorSpawns.Contains(instanceId))
                return;

            pendingCutsceneActorSpawns[instanceId] =
                new PendingCutsceneActorSpawn
                {
                    Wgo = wgo,
                    SendAt = Time.realtimeSinceStartup + 0.05f
                };
        }

        /// <summary>
        /// Synchronize a WGO's queued one-shot interaction events. Story flows often
        /// spawn or move an NPC and then add the event in a following node, after the
        /// spawn snapshot was captured. Without this delta the host can save an actor
        /// that exists in the right place but has lost the interaction that advances it.
        /// </summary>
        internal void NotifyInteractionEventsChanged(WorldGameObject wgo)
        {
            if (!IsSyncEnabled || !IsOnline ||
                IsApplyingCanonicalWgoState ||
                SyncBehaviour.GlobalRemoteApplyActive ||
                wgo == null || wgo.is_removed || wgo.is_player ||
                wgo.GetComponent<PlayerComponent>() != null ||
                wgo.unique_id <= 0L ||
                string.IsNullOrEmpty(wgo.obj_id) || wgo.obj_id == "0")
            {
                return;
            }

            ulong originSteamId = SteamUser.GetSteamID().m_SteamID;
            byte subType = IsHost
                ? SubCanonicalInteractionEvents
                : SubInteractionEventsRequest;
            byte[] payload = SerializeInteractionEvents(
                subType,
                NextSequence++,
                originSteamId,
                wgo);

            if (IsHost)
                SteamP2PManager.Instance?.BroadcastSpawnSync(payload);
            else
                SteamP2PManager.Instance?.SendSpawnSyncToHost(payload);

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Sent interaction-event state: uid={wgo.unique_id}, " +
                $"obj_id={wgo.obj_id}, events={wgo.custom_interaction_events?.Count ?? 0}");
        }

        private void FlushPendingCutsceneActorSpawns(float now)
        {
            if (IsHost || pendingCutsceneActorSpawns.Count == 0)
                return;

            var instanceIds = new List<int>(
                pendingCutsceneActorSpawns.Keys);
            for (int i = 0; i < instanceIds.Count; i++)
            {
                int instanceId = instanceIds[i];
                if (!pendingCutsceneActorSpawns.TryGetValue(
                        instanceId,
                        out PendingCutsceneActorSpawn pending) ||
                    pending == null || now < pending.SendAt)
                {
                    continue;
                }

                pendingCutsceneActorSpawns.Remove(instanceId);
                WorldGameObject wgo = pending.Wgo;
                if (wgo == null || wgo.is_removed ||
                    !TrySerializeWgo(wgo, out string json))
                {
                    continue;
                }

                byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
                using (var stream = new MemoryStream(jsonBytes.Length + 48))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(PayloadVersion);
                    writer.Write(SubCutsceneActorSpawnRequest);
                    writer.Write(NextSequence++);
                    writer.Write(wgo.unique_id);
                    writer.Write(wgo.obj_id ?? string.Empty);
                    writer.Write(wgo.gameObject.activeSelf);
                    writer.Write(jsonBytes.Length);
                    writer.Write(jsonBytes);
                    writer.Flush();
                    SteamP2PManager.Instance?.SendSpawnSyncToHost(
                        stream.ToArray());
                }

                sentCutsceneActorSpawns.Add(instanceId);
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Requested canonical cutscene actor spawn: " +
                    $"uid={wgo.unique_id}, obj_id={wgo.obj_id}, " +
                    $"tag={wgo.custom_tag}, active={wgo.gameObject.activeSelf}");
            }
        }

        internal void SendWgoSpawned(WorldGameObject wgo)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (!IsHost) return;
            if (wgo == null) return;

            if (!TrySerializeWgo(wgo, out string json)) return;
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);

            using (var stream = new MemoryStream(jsonBytes.Length + 32))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubWgoSpawned);
                bw.Write(NextSequence++);
                bw.Write(wgo.unique_id);
                bw.Write(wgo.obj_id ?? "");
                bw.Write(jsonBytes.Length);
                bw.Write(jsonBytes);
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastSpawnSync(stream.ToArray());
            }

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Sent WgoSpawned: uid={wgo.unique_id}, " +
                $"obj_id={wgo.obj_id}, tag={wgo.custom_tag}");
        }

        internal void SendWgoReplaced(long oldUniqueId, WorldGameObject newWgo)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (!IsHost) return;
            if (newWgo == null) return;

            if (!TrySerializeWgo(newWgo, out string json)) return;
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);

            using (var stream = new MemoryStream(jsonBytes.Length + 40))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubWgoReplaced);
                bw.Write(NextSequence++);
                bw.Write(oldUniqueId);
                bw.Write(newWgo.unique_id);
                bw.Write(newWgo.obj_id ?? "");
                bw.Write(jsonBytes.Length);
                bw.Write(jsonBytes);
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastSpawnSync(stream.ToArray());
            }

            CoopMod.Logger.LogInfo($"{LogPrefix} Sent WgoReplaced: old_uid={oldUniqueId}, new_uid={newWgo.unique_id}, obj_id={newWgo.obj_id}");
        }

        internal void NotifyWgoReplaced(
            long oldUniqueId,
            string oldObjId,
            WorldGameObject newWgo)
        {
            if (!IsSyncEnabled ||
                !IsOnline ||
                IsApplyingCanonicalWgoState ||
                newWgo == null ||
                newWgo.is_player ||
                newWgo.GetComponent<PlayerComponent>() != null ||
                string.IsNullOrEmpty(newWgo.obj_id) ||
                newWgo.obj_id == "0" ||
                string.Equals(oldObjId, newWgo.obj_id, StringComparison.Ordinal))
            {
                return;
            }

            if (MainGame.me?.world_root != null &&
                !newWgo.transform.IsChildOf(MainGame.me.world_root))
            {
                return;
            }

            if (IsHost)
            {
                SendWgoReplaced(oldUniqueId, newWgo);
                return;
            }

            if (pendingBuildingPlacements.TryGetValue(
                    oldUniqueId,
                    out PendingBuildingPlacement pending) ||
                pendingBuildingPlacements.TryGetValue(
                    newWgo.unique_id,
                    out pending))
            {
                pending.Wgo = newWgo;
                pending.DeferredMutation = true;
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Deferred WGO replacement until placement is canonical: " +
                    $"temp_uid={pending.TemporaryUniqueId}, old_obj={oldObjId}, " +
                    $"new_obj={newWgo.obj_id}");
                return;
            }

            if (!TrySerializeWgo(newWgo, out string json))
                return;

            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            using (var stream = new MemoryStream(jsonBytes.Length + 40))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PayloadVersion);
                writer.Write(SubWgoReplaceRequest);
                writer.Write(NextSequence++);
                writer.Write(oldUniqueId);
                writer.Write(newWgo.unique_id);
                writer.Write(newWgo.obj_id ?? "");
                writer.Write(jsonBytes.Length);
                writer.Write(jsonBytes);
                writer.Flush();
                SteamP2PManager.Instance?.SendSpawnSyncToHost(
                    stream.ToArray());
            }

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Requested canonical WGO replacement: " +
                $"old_uid={oldUniqueId}, old_obj={oldObjId}, " +
                $"new_uid={newWgo.unique_id}, new_obj={newWgo.obj_id}");
        }

        /// <summary>
        /// Flow nodes can place an existing story WGO at a GD point without creating
        /// or replacing it. When that FlowScript was initiated by a client, ask the
        /// host to perform the same semantic placement so host NPC authority does not
        /// restore the object to its previous location.
        /// </summary>
        internal void RequestCanonicalFlowPlacement(
            WorldGameObject wgo,
            GDPoint gdPoint,
            bool routeCompleted = false)
        {
            if (!IsSyncEnabled || !IsOnline || IsHost ||
                IsApplyingCanonicalFlowPlacement || wgo == null || gdPoint == null ||
                wgo.is_player || wgo.GetComponent<PlayerComponent>() != null)
            {
                return;
            }

            string gdTag = gdPoint.gd_tag;
            if (string.IsNullOrEmpty(gdTag))
                return;

            byte[] payload = SerializeFlowPlacement(
                SubFlowPlacementRequest,
                NextSequence++,
                wgo,
                gdTag,
                wgo.gameObject.activeSelf,
                routeCompleted);
            SteamP2PManager.Instance?.SendSpawnSyncToHost(payload);

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Requested canonical Flow placement: uid={wgo.unique_id}, " +
                $"obj_id={wgo.obj_id}, gd_point={gdTag}, " +
                $"active={wgo.gameObject.activeSelf}, route_completed={routeCompleted}");
        }

        /// <summary>
        /// Bishop departures are host-owned schedules rather than client-owned
        /// cutscenes. Publish their final stock point through the same semantic
        /// placement lane so every client clears the completed route immediately.
        /// </summary>
        internal void BroadcastCanonicalBishopStockPlacement(
            WorldGameObject bishop,
            GDPoint stockPoint)
        {
            if (!IsSyncEnabled || !IsOnline || !IsHost ||
                IsApplyingCanonicalFlowPlacement || bishop == null ||
                stockPoint == null || bishop.unique_id <= 0L ||
                !string.Equals(
                    bishop.obj_id,
                    "npc_bishop",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    stockPoint.gd_tag,
                    "gd_stock_bishop",
                    StringComparison.Ordinal))
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (lastBishopStockBroadcastAt.TryGetValue(
                    bishop.unique_id,
                    out float lastBroadcast) &&
                now - lastBroadcast < 1f)
            {
                return;
            }
            lastBishopStockBroadcastAt[bishop.unique_id] = now;

            ChunkedGameObject chunk =
                bishop.GetComponent<ChunkedGameObject>();
            if (chunk != null)
            {
                chunk.active_now_because_of_movement = false;
                chunk.RecalculateChunk();
            }

            byte[] payload = SerializeFlowPlacement(
                SubCanonicalFlowPlacement,
                NextSequence++,
                bishop,
                stockPoint.gd_tag,
                bishop.gameObject.activeSelf,
                routeCompleted: true);
            SteamP2PManager.Instance?.BroadcastSpawnSync(payload);
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Host broadcast Bishop stock placement: " +
                $"uid={bishop.unique_id}, gd_point={stockPoint.gd_tag}, " +
                $"active={bishop.gameObject.activeSelf}");
        }

        /// <summary>
        /// PuffFX detaches from its WGO before story nodes teleport that actor away. Capture
        /// its world-space bounds at the call site and relay the transient presentation so
        /// an observer does not lose the smoke when NPC authority moves the actor first.
        /// </summary>
        internal bool NotifyLocalPuffFx(
            WorldGameObject wgo,
            Bounds? suppliedBounds)
        {
            if (!IsSyncEnabled || !IsOnline || wgo == null || wgo.is_removed ||
                wgo.unique_id <= 0L || string.IsNullOrEmpty(wgo.obj_id) ||
                wgo.obj_id == "0")
            {
                return true;
            }

            Bounds bounds;
            try
            {
                bounds = suppliedBounds ?? wgo.GetTotalBounds();
            }
            catch
            {
                bounds = new Bounds(wgo.transform.position, Vector3.one * 96f);
            }

            if (!IsValidPuffFxBounds(bounds))
                return true;

            ulong originSteamId = SteamUser.GetSteamID().m_SteamID;
            if (IsHost)
            {
                if (!TryMarkPuffFxRelay(wgo.unique_id))
                    return false;

                SteamP2PManager.Instance?.BroadcastSpawnSync(
                    SerializePuffFx(
                        SubCanonicalPuffFx,
                        NextSequence++,
                        originSteamId,
                        wgo,
                        bounds));
                return true;
            }

            SteamP2PManager.Instance?.SendSpawnSyncToHost(
                SerializePuffFx(
                    SubPuffFxRequest,
                    NextSequence++,
                    originSteamId,
                    wgo,
                    bounds));
            // The host replays and canonicalizes the effect. Suppress this local copy so
            // mirrored cutscene flows cannot render a second puff before/after the relay.
            return false;
        }

        private void OnSpawnSyncReceived(CSteamID senderID, byte[] payload)
        {
            if (!IsSyncEnabled) return;
            if (payload == null || payload.Length == 0) return;
            if (!IsOnline) return;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsRemotePlayer(senderID))
                return;

            try
            {
                using (var stream = new MemoryStream(payload))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    byte version = reader.ReadByte();
                    if (version != PayloadVersion)
                    {
                        CoopMod.Logger.LogWarning($"{LogPrefix} Unknown payload version {version}");
                        return;
                    }

                    byte subType = reader.ReadByte();

                    switch (subType)
                    {
                        case SubWgoSpawned:
                            if (onlineCoop.IsHost) return;
                            HandleWgoSpawned(reader, senderID);
                            break;
                        case SubWgoReplaced:
                            if (onlineCoop.IsHost) return;
                            HandleWgoReplaced(reader, senderID);
                            break;
                        case SubFlowPlacementRequest:
                            if (!onlineCoop.IsHost) return;
                            HandleFlowPlacementRequest(reader, senderID);
                            break;
                        case SubCanonicalFlowPlacement:
                            if (onlineCoop.IsHost || !IsExpectedHost(senderID)) return;
                            HandleCanonicalFlowPlacement(reader, senderID);
                            break;
                        case SubWgoReplaceRequest:
                            if (!onlineCoop.IsHost) return;
                            HandleWgoReplaceRequest(reader, senderID);
                            break;
                        case SubBuildingPlacementRequest:
                            if (!onlineCoop.IsHost) return;
                            HandleBuildingPlacementRequest(reader, senderID);
                            break;
                        case SubCanonicalBuildingPlacement:
                            if (onlineCoop.IsHost || !IsExpectedHost(senderID)) return;
                            HandleCanonicalBuildingPlacement(reader, senderID);
                            break;
                        case SubBuildingPreviewRequest:
                            if (!onlineCoop.IsHost) return;
                            HandleBuildingPreviewRequest(reader, senderID);
                            break;
                        case SubCanonicalBuildingPreview:
                            if (onlineCoop.IsHost || !IsExpectedHost(senderID)) return;
                            HandleCanonicalBuildingPreview(reader);
                            break;
                        case SubBuildingPreviewEndRequest:
                            if (!onlineCoop.IsHost) return;
                            HandleBuildingPreviewEndRequest(reader, senderID);
                            break;
                        case SubCanonicalBuildingPreviewEnd:
                            if (onlineCoop.IsHost || !IsExpectedHost(senderID)) return;
                            HandleCanonicalBuildingPreviewEnd(reader);
                            break;
                        case SubCutsceneActorSpawnRequest:
                            if (!onlineCoop.IsHost) return;
                            HandleCutsceneActorSpawnRequest(reader, senderID);
                            break;
                        case SubCanonicalCutsceneActorSpawn:
                            if (onlineCoop.IsHost || !IsExpectedHost(senderID)) return;
                            HandleCanonicalCutsceneActorSpawn(reader, senderID);
                            break;
                        case SubPuffFxRequest:
                            if (!onlineCoop.IsHost) return;
                            HandlePuffFxRequest(reader, senderID);
                            break;
                        case SubCanonicalPuffFx:
                            if (onlineCoop.IsHost || !IsExpectedHost(senderID)) return;
                            HandleCanonicalPuffFx(reader, senderID);
                            break;
                        case SubInteractionEventsRequest:
                            if (!onlineCoop.IsHost) return;
                            HandleInteractionEventsRequest(reader, senderID);
                            break;
                        case SubCanonicalInteractionEvents:
                            if (onlineCoop.IsHost || !IsExpectedHost(senderID)) return;
                            HandleCanonicalInteractionEvents(reader, senderID);
                            break;
                        default:
                            CoopMod.Logger.LogWarning($"{LogPrefix} Unknown sub-type {subType}");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Error processing message: {ex.Message}");
            }
        }

        private void HandleBuildingPreviewRequest(
            BinaryReader reader,
            CSteamID senderID)
        {
            if (!TryReadBuildingPreview(
                    reader,
                    false,
                    senderID.m_SteamID,
                    out ulong originSteamId,
                    out uint session,
                    out uint sequence,
                    out BuildingPreviewPose pose))
            {
                return;
            }

            if (!ApplyBuildingPreview(
                    originSteamId,
                    session,
                    sequence,
                    pose))
            {
                return;
            }
            byte[] canonical = SerializeBuildingPreview(
                SubCanonicalBuildingPreview,
                originSteamId,
                session,
                sequence,
                pose);
            SteamP2PManager.Instance?.BroadcastSpawnPreview(
                canonical,
                senderID);
        }

        private void HandleCanonicalBuildingPreview(BinaryReader reader)
        {
            if (!TryReadBuildingPreview(
                    reader,
                    true,
                    0UL,
                    out ulong originSteamId,
                    out uint session,
                    out uint sequence,
                    out BuildingPreviewPose pose) ||
                originSteamId == SteamUser.GetSteamID().m_SteamID)
            {
                return;
            }

            ApplyBuildingPreview(
                originSteamId,
                session,
                sequence,
                pose);
        }

        private void HandleBuildingPreviewEndRequest(
            BinaryReader reader,
            CSteamID senderID)
        {
            if (!TryReadBuildingPreviewEnd(
                    reader,
                    false,
                    senderID.m_SteamID,
                    out ulong originSteamId,
                    out uint session,
                    out uint sequence))
            {
                return;
            }

            if (!ApplyBuildingPreviewEnd(originSteamId, session, sequence))
                return;
            BroadcastCanonicalBuildingPreviewEnd(
                originSteamId,
                session,
                sequence,
                senderID);
        }

        private void HandleCanonicalBuildingPreviewEnd(BinaryReader reader)
        {
            if (!TryReadBuildingPreviewEnd(
                    reader,
                    true,
                    0UL,
                    out ulong originSteamId,
                    out uint session,
                    out uint sequence) ||
                originSteamId == SteamUser.GetSteamID().m_SteamID)
            {
                return;
            }

            ApplyBuildingPreviewEnd(originSteamId, session, sequence);
        }

        private bool ApplyBuildingPreview(
            ulong originSteamId,
            uint session,
            uint sequence,
            BuildingPreviewPose pose)
        {
            if (originSteamId == 0UL ||
                originSteamId == SteamUser.GetSteamID().m_SteamID)
            {
                return false;
            }

            if (!remoteBuildingPreviews.TryGetValue(
                    originSteamId,
                    out BuildingPreviewState state))
            {
                state = new BuildingPreviewState();
                remoteBuildingPreviews[originSteamId] = state;
            }

            if (session < state.Session ||
                (session == state.Session && sequence <= state.LastSequence))
            {
                return false;
            }

            if (session != state.Session)
            {
                DestroyRemoteBuildingPreview(state);
                state.Session = session;
                state.LastSequence = 0;
                state.Ended = false;
                state.ObjId = null;
                state.ZoneId = null;
                state.CustomSubZoneId = null;
            }

            state.LastSequence = sequence;
            state.LastUpdateAt = Time.realtimeSinceStartup;
            state.Ended = false;
            state.ZoneId = pose.ZoneId;
            state.CustomSubZoneId = pose.CustomSubZoneId;

            bool createdPreview = false;
            if (state.Wgo == null ||
                !string.Equals(state.ObjId, pose.ObjId, StringComparison.Ordinal))
            {
                DestroyRemoteBuildingPreview(state);
                state.Wgo = CreateRemoteBuildingPreview(
                    originSteamId,
                    pose);
                state.ObjId = state.Wgo != null ? pose.ObjId : null;
                createdPreview = state.Wgo != null;
            }

            if (state.Wgo != null)
            {
                ApplyBuildingPreviewPose(state.Wgo, pose);
                if (createdPreview)
                {
                    CoopMod.Logger.LogInfo(
                        $"{LogPrefix} Started remote build preview: " +
                        $"origin={originSteamId}, obj_id={pose.ObjId}, " +
                        $"footprint_cells={pose.FootprintCells?.Count ?? 0}");
                }
            }
            RefreshRemoteBuildGridPresentation();
            return true;
        }

        private bool ApplyBuildingPreviewEnd(
            ulong originSteamId,
            uint session,
            uint sequence)
        {
            if (originSteamId == 0UL)
                return false;

            if (!remoteBuildingPreviews.TryGetValue(
                    originSteamId,
                    out BuildingPreviewState state))
            {
                state = new BuildingPreviewState();
                remoteBuildingPreviews[originSteamId] = state;
            }

            if (session < state.Session ||
                (session == state.Session && sequence <= state.LastSequence))
            {
                return false;
            }

            DestroyRemoteBuildingPreview(state);
            state.Session = session;
            state.LastSequence = sequence;
            state.LastUpdateAt = Time.realtimeSinceStartup;
            state.Ended = true;
            state.ObjId = null;
            state.ZoneId = null;
            state.CustomSubZoneId = null;
            RefreshRemoteBuildGridPresentation();
            return true;
        }

        private WorldGameObject CreateRemoteBuildingPreview(
            ulong originSteamId,
            BuildingPreviewPose pose)
        {
            WorldGameObject preview = null;
            bool wasApplying = IsApplyingCanonicalWgoState;
            IsApplyingCanonicalWgoState = true;
            try
            {
                preview = WorldGameObject.InstantiateWGOPrefab();
                if (preview == null)
                    return null;

                preview.gameObject.AddComponent<RemoteBuildingPreviewMarker>();
                preview.unique_id = -2L;
                if (MainGame.me?.world_root != null)
                    preview.transform.SetParent(MainGame.me.world_root, false);
                preview.transform.position = pose.Position;
                preview.transform.rotation = pose.Rotation;
                preview.transform.localScale = pose.Scale;
                preview.variation = pose.Variation;
                preview.variation_2 = pose.Variation2;
                WorldMap.ActivateGameObject(preview.gameObject);
                preview.SetObject(pose.ObjId);

                // SetObject registers ordinary WGOs. A remote projection is only a
                // visual cursor and must never participate in saves or world lookup.
                WorldMap.OnDestroyWGO(preview);
                WGORegistry.Instance?.Unregister(preview.unique_id);
                preview.unique_id = 0L;
                preview.gameObject.name =
                    "RemoteBuildPreview_" + originSteamId.ToString();
                DisableRemoteBuildingPreviewGameplay(preview);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Could not create remote building preview " +
                    $"obj_id={pose.ObjId}: {ex.Message}");
                if (preview != null)
                    UnityEngine.Object.Destroy(preview.gameObject);
                preview = null;
            }
            finally
            {
                IsApplyingCanonicalWgoState = wasApplying;
            }
            return preview;
        }

        private static void ApplyBuildingPreviewPose(
            WorldGameObject preview,
            BuildingPreviewPose pose)
        {
            if (preview == null)
                return;

            bool redraw = preview.variation != pose.Variation ||
                          preview.variation_2 != pose.Variation2;
            preview.transform.position = pose.Position;
            preview.transform.rotation = pose.Rotation;
            preview.transform.localScale = pose.Scale;
            preview.variation = pose.Variation;
            preview.variation_2 = pose.Variation2;

            if (redraw)
                ApplyBuildingPreviewVariation(preview);

            RemoteBuildingPreviewMarker marker =
                preview.GetComponent<RemoteBuildingPreviewMarker>();
            if (marker == null || marker.SetBuildable(pose.CanBuild))
            {
                Color color = pose.CanBuild
                    ? new Color(1f, 1f, 1f, 0.55f)
                    : new Color(1f, 0.25f, 0.25f, 0.55f);
                preview.SetBuildingColor(color);
            }

            bool visualHelpersChanged = marker != null &&
                marker.ApplyFootprint(
                    pose.FootprintCells,
                    pose.CanBuild);
            if (marker != null &&
                marker.ApplyDockMarkers(preview, redraw))
            {
                visualHelpersChanged = true;
            }

            if (visualHelpersChanged)
            {
                // New visual helpers can include behaviours. Disable them once
                // when their layout changes, not on every pose packet.
                DisableRemoteBuildingPreviewGameplay(preview);
            }
        }

        private static void ApplyBuildingPreviewVariation(
            WorldGameObject preview)
        {
            WorldObjectPart part = preview?.wop;
            if (part == null)
                return;

            if (part.variations != null)
            {
                for (int i = 0; i < part.variations.Count; i++)
                {
                    GameObject variation = part.variations[i];
                    if (variation != null)
                        variation.SetActive((preview.variation & (1 << i)) > 0);
                }
            }

            if (part.variations_2 == null)
                return;

            for (int i = 0; i < part.variations_2.Count; i++)
            {
                List<GameObject> variations = part.variations_2[i].list;
                if (variations == null)
                    continue;

                bool active = (preview.variation_2 & (1 << i)) > 0;
                for (int j = 0; j < variations.Count; j++)
                {
                    if (variations[j] != null)
                        variations[j].SetActive(active);
                }
            }
        }

        private static void DisableRemoteBuildingPreviewGameplay(
            WorldGameObject preview)
        {
            foreach (Collider2D collider in
                preview.GetComponentsInChildren<Collider2D>(true))
            {
                collider.enabled = false;
            }

            foreach (Rigidbody2D body in
                preview.GetComponentsInChildren<Rigidbody2D>(true))
            {
                body.velocity = Vector2.zero;
                body.angularVelocity = 0f;
                body.isKinematic = true;
            }

            foreach (MonoBehaviour behaviour in
                preview.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is RoundAndSortComponent ||
                    behaviour is RemoteBuildingPreviewMarker)
                {
                    continue;
                }
                behaviour.enabled = false;
            }
        }

        private static void DestroyRemoteBuildingPreview(
            BuildingPreviewState state)
        {
            if (state?.Wgo == null)
                return;

            WorldGameObject preview = state.Wgo;
            state.Wgo = null;
            if (preview.unique_id != 0L)
            {
                WGORegistry.Instance?.Unregister(preview.unique_id);
                WorldMap.OnDestroyWGO(preview);
            }
            UnityEngine.Object.Destroy(preview.gameObject);
        }

        private void RemoveTimedOutBuildingPreviews(float now)
        {
            foreach (BuildingPreviewState state in
                remoteBuildingPreviews.Values)
            {
                if (state.Wgo == null ||
                    now - state.LastUpdateAt <= PreviewTimeoutSeconds)
                {
                    continue;
                }

                DestroyRemoteBuildingPreview(state);
                state.Ended = true;
                state.ObjId = null;
                state.ZoneId = null;
                state.CustomSubZoneId = null;
            }
            RefreshRemoteBuildGridPresentation();
        }

        private void ClearBuildingPreviews()
        {
            HideRemoteBuildGridPresentation();
            foreach (BuildingPreviewState state in
                remoteBuildingPreviews.Values)
            {
                DestroyRemoteBuildingPreview(state);
            }
            remoteBuildingPreviews.Clear();
            localBuildingPreview = null;
            localPreviewSession = 0;
            localPreviewSequence = 0;
            nextPreviewSendAt = 0f;
            lastPreviewSentAt = 0f;
            localPreviewDirty = false;
            hasLastPreviewPose = false;
        }

        /// <summary>
        /// Draw the same build-cell overlay used by the remote builder at its world
        /// position. The observer's camera, controls, HUD, and BuildModeLogics mode
        /// remain untouched.
        /// </summary>
        private void RefreshRemoteBuildGridPresentation()
        {
            if (MainGame.me == null || BuildGrid.me == null ||
                IsLocalBuildPresentationActive())
            {
                return;
            }

            BuildingPreviewState selected = null;
            ulong selectedBuilder = 0UL;
            if (presentedRemoteBuilder != 0UL &&
                remoteBuildingPreviews.TryGetValue(
                    presentedRemoteBuilder,
                    out BuildingPreviewState current) &&
                IsPresentableRemoteBuildState(current))
            {
                selected = current;
                selectedBuilder = presentedRemoteBuilder;
            }
            else
            {
                foreach (KeyValuePair<ulong, BuildingPreviewState> pair in
                    remoteBuildingPreviews)
                {
                    if (!IsPresentableRemoteBuildState(pair.Value))
                        continue;
                    if (selected == null ||
                        pair.Value.LastUpdateAt > selected.LastUpdateAt)
                    {
                        selected = pair.Value;
                        selectedBuilder = pair.Key;
                    }
                }
            }

            if (selected == null)
            {
                if (presentedRemoteBuilder != 0UL &&
                    remoteBuildGridHideAt <= 0f)
                {
                    // Placing one blueprint briefly ends the old floating WGO
                    // before vanilla creates the next one. Keep the grid stable
                    // across that handoff.
                    remoteBuildGridHideAt =
                        Time.realtimeSinceStartup +
                        PreviewGridEndGraceSeconds;
                    return;
                }
                if (remoteBuildGridHideAt > Time.realtimeSinceStartup)
                    return;
                HideRemoteBuildGridPresentation();
                return;
            }

            remoteBuildGridHideAt = 0f;
            string zoneId = ResolvePreviewZoneId(
                selected.ZoneId,
                selected.Wgo != null
                    ? selected.Wgo.transform.position
                    : Vector3.zero);
            if (string.IsNullOrEmpty(zoneId))
                return;

            if (presentedRemoteBuilder == selectedBuilder &&
                string.Equals(
                    presentedRemoteBuildZone,
                    zoneId,
                    StringComparison.Ordinal) &&
                BuildGrid.me.gameObject.activeSelf)
            {
                return;
            }

            WorldZone zone = WorldZone.GetZoneByID(zoneId, true);
            if (zone == null || zone.center_tf == null ||
                CurrentBuildZoneIdField == null)
            {
                return;
            }

            ShowRemoteBuildQualities(zone);

            BuildModeLogics buildMode = MainGame.me.build_mode_logics;
            string localZoneId = buildMode.cur_build_zone_id;
            try
            {
                // BuildGrid.RefreshGrid reads this private field, but does not need
                // it after the texture has been drawn. Restore it immediately so
                // the observer never enters or inherits the remote build zone.
                CurrentBuildZoneIdField.SetValue(buildMode, zoneId);
                BuildGrid.me.MoveBuildGridTo(zone.center_tf.position);
                BuildGrid.ShowBuildGrid(
                    true,
                    true,
                    selected.CustomSubZoneId ?? string.Empty);
            }
            finally
            {
                CurrentBuildZoneIdField.SetValue(
                    buildMode,
                    localZoneId ?? string.Empty);
            }

            presentedRemoteBuilder = selectedBuilder;
            presentedRemoteBuildZone = zoneId;
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Showing remote build grid for " +
                $"{selectedBuilder} in zone '{zoneId}' (camera unchanged)");
        }

        private void HideRemoteBuildGridPresentation()
        {
            RestoreRemoteBuildQualities();
            if (presentedRemoteBuilder == 0UL)
                return;

            if (MainGame.me != null &&
                !IsLocalBuildPresentationActive() &&
                BuildGrid.me != null)
                BuildGrid.ShowBuildGrid(false, true, string.Empty);

            presentedRemoteBuilder = 0UL;
            presentedRemoteBuildZone = string.Empty;
            remoteBuildGridHideAt = 0f;
        }

        private void ShowRemoteBuildQualities(WorldZone zone)
        {
            if (zone == null || presentedRemoteQualityZone == zone)
                return;

            RestoreRemoteBuildQualities();
            List<WorldGameObject> zoneObjects = zone.GetZoneWGOs();
            if (zoneObjects != null)
            {
                for (int i = 0; i < zoneObjects.Count; i++)
                {
                    WorldGameObject wgo = zoneObjects[i];
                    if (wgo == null ||
                        remoteQualityHintOriginalStates.ContainsKey(wgo))
                    {
                        continue;
                    }
                    remoteQualityHintOriginalStates[wgo] =
                        wgo.show_quality_hint;
                }
            }

            presentedRemoteQualityZone = zone;
            zone.RedrawQualities(true, true);
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Showing {remoteQualityHintOriginalStates.Count} " +
                $"remote build quality hints in zone '{zone.id}'");
        }

        private void RestoreRemoteBuildQualities()
        {
            if (presentedRemoteQualityZone == null &&
                remoteQualityHintOriginalStates.Count == 0)
            {
                return;
            }

            int restored = 0;
            foreach (KeyValuePair<WorldGameObject, bool> pair in
                remoteQualityHintOriginalStates)
            {
                WorldGameObject wgo = pair.Key;
                if (wgo == null)
                    continue;
                wgo.SetQualityHint(pair.Value);
                restored++;
            }

            string zoneId = presentedRemoteQualityZone?.id ??
                            string.Empty;
            remoteQualityHintOriginalStates.Clear();
            presentedRemoteQualityZone = null;
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Restored {restored} quality hints after " +
                $"remote build presentation in zone '{zoneId}'");
        }

        private bool IsLocalBuildPresentationActive()
        {
            return localBuildingPreview != null ||
                   MainGame.me?.game_mode == MainGame.GameMode.Building ||
                   MainGame.me?.build_mode_logics?.IsBuilding() == true;
        }

        private static bool IsPresentableRemoteBuildState(
            BuildingPreviewState state)
        {
            return state != null && !state.Ended && state.Wgo != null;
        }

        private static string ResolvePreviewZoneId(
            string advertisedZoneId,
            Vector3 position)
        {
            if (!string.IsNullOrEmpty(advertisedZoneId) &&
                advertisedZoneId.Length <= 256 &&
                WorldZone.GetZoneByID(advertisedZoneId, true) != null)
            {
                return advertisedZoneId;
            }

            return WorldZone.GetZoneOfPoint(position)?.id ?? string.Empty;
        }

        internal void NotifyLocalBuildModeEntered()
        {
            // Vanilla has already drawn the local grid. Relinquish observer
            // ownership without hiding or redrawing it.
            if (presentedRemoteQualityZone !=
                MainGame.me?.build_mode_logics?.cur_build_zone)
            {
                RestoreRemoteBuildQualities();
            }
            else
            {
                // The local BuildModeGUI has just enabled these same hints and
                // now owns their cleanup. Do not restore the pre-observer state
                // over vanilla's newly opened build presentation.
                remoteQualityHintOriginalStates.Clear();
                presentedRemoteQualityZone = null;
            }
            presentedRemoteBuilder = 0UL;
            presentedRemoteBuildZone = string.Empty;
            remoteBuildGridHideAt = 0f;
        }

        internal void NotifyLocalBuildModeExited()
        {
            RefreshRemoteBuildGridPresentation();
        }

        internal void NotifyLocalBuildZoneChanged(
            string zoneId,
            string customSubZoneId)
        {
            if (string.IsNullOrEmpty(zoneId))
            {
                RefreshRemoteBuildGridPresentation();
                return;
            }

            if (presentedRemoteBuilder == 0UL)
                return;

            RestoreRemoteBuildQualities();

            // The remote observer grid is already marked as shown globally.
            // Vanilla's non-forced ShowBuildGrid call would therefore skip the
            // local redraw when this player opens their own build desk.
            presentedRemoteBuilder = 0UL;
            presentedRemoteBuildZone = string.Empty;
            remoteBuildGridHideAt = 0f;
            if (BuildGrid.me != null)
            {
                BuildGrid.ShowBuildGrid(
                    true,
                    true,
                    customSubZoneId ?? string.Empty);
            }
        }

        internal static bool IsTransientBuildingPreview(WorldGameObject wgo)
        {
            if (wgo == null)
                return false;
            if (wgo.GetComponent<RemoteBuildingPreviewMarker>() != null)
                return true;
            if (wgo.unique_id >= TemporaryPlacementIdMin &&
                wgo.unique_id <= TemporaryPlacementIdMax)
            {
                return true;
            }

            return Instance?.localBuildingPreview == wgo;
        }

        private void HandleBuildingPlacementRequest(
            BinaryReader reader,
            CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID))
                return;

            long temporaryUniqueId = reader.ReadInt64();
            string objId = reader.ReadString();
            int jsonLen = reader.ReadInt32();
            if (!IsTemporaryPlacementId(temporaryUniqueId) ||
                string.IsNullOrEmpty(objId) || objId == "0" ||
                jsonLen <= 0 || jsonLen > MaxSerializedWgoBytes)
            {
                return;
            }

            byte[] jsonBytes = reader.ReadBytes(jsonLen);
            if (jsonBytes.Length != jsonLen ||
                !TryDeserializeWgo(
                    jsonBytes,
                    temporaryUniqueId,
                    objId,
                    out SerializableWGO serialized) ||
                !IsValidBuildingPlacement(serialized))
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Rejected invalid building placement from {senderID}: " +
                    $"temp_uid={temporaryUniqueId}, obj_id={objId}");
                return;
            }

            string placementKey = BuildPlacementKey(senderID.m_SteamID, temporaryUniqueId);
            bool alreadyCanonical = canonicalPlacementIds.TryGetValue(
                placementKey,
                out long canonicalUniqueId);
            if (!alreadyCanonical)
            {
                canonicalUniqueId = AllocateCanonicalUniqueId();
                if (canonicalUniqueId == 0L)
                {
                    CoopMod.Logger.LogError(
                        $"{LogPrefix} Could not allocate a canonical ID for client building " +
                        $"temp_uid={temporaryUniqueId}, obj_id={objId}");
                    return;
                }
                canonicalPlacementIds[placementKey] = canonicalUniqueId;
            }

            serialized.unique_id = canonicalUniqueId;
            WorldGameObject target = alreadyCanonical
                ? WorldMap.GetWorldGameObjectByUniqueId(canonicalUniqueId, false)
                : null;
            target = ApplyCanonicalBuildingState(target, serialized);
            if (target == null)
                return;

            BroadcastCanonicalBuildingPlacement(
                senderID.m_SteamID,
                temporaryUniqueId,
                target);

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Host canonicalized client building placement: " +
                $"player={senderID}, temp_uid={temporaryUniqueId}, " +
                $"canonical_uid={target.unique_id}, obj_id={target.obj_id}, " +
                $"pos={target.transform.position}, retry={alreadyCanonical}");
        }

        private void HandleCanonicalBuildingPlacement(
            BinaryReader reader,
            CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID))
                return;

            ulong originSteamId = reader.ReadUInt64();
            long temporaryUniqueId = reader.ReadInt64();
            long canonicalUniqueId = reader.ReadInt64();
            string objId = reader.ReadString();
            int jsonLen = reader.ReadInt32();
            if (originSteamId == 0UL ||
                !IsTemporaryPlacementId(temporaryUniqueId) ||
                canonicalUniqueId <= 0L ||
                IsTemporaryPlacementId(canonicalUniqueId) ||
                string.IsNullOrEmpty(objId) || objId == "0" ||
                jsonLen <= 0 || jsonLen > MaxSerializedWgoBytes)
            {
                return;
            }

            byte[] jsonBytes = reader.ReadBytes(jsonLen);
            if (jsonBytes.Length != jsonLen ||
                !TryDeserializeWgo(
                    jsonBytes,
                    canonicalUniqueId,
                    objId,
                    out SerializableWGO serialized))
            {
                return;
            }

            ulong localSteamId = SteamUser.GetSteamID().m_SteamID;
            if (originSteamId == localSteamId)
            {
                ApplyOriginBuildingPlacement(
                    temporaryUniqueId,
                    canonicalUniqueId,
                    objId,
                    serialized);
                return;
            }

            WorldGameObject existing =
                WorldMap.GetWorldGameObjectByUniqueId(canonicalUniqueId, false);
            WorldGameObject applied = ApplyCanonicalBuildingState(existing, serialized);
            if (applied != null)
            {
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Applied canonical remote building placement: " +
                    $"origin={originSteamId}, canonical_uid={canonicalUniqueId}, " +
                    $"obj_id={objId}, pos={applied.transform.position}");
            }
        }

        private void ApplyOriginBuildingPlacement(
            long temporaryUniqueId,
            long canonicalUniqueId,
            string canonicalObjId,
            SerializableWGO serialized)
        {
            pendingBuildingPlacements.TryGetValue(
                temporaryUniqueId,
                out PendingBuildingPlacement pending);
            WorldGameObject local = pending?.Wgo ??
                WorldMap.GetWorldGameObjectByUniqueId(temporaryUniqueId, false);
            bool deferredMutation = pending?.DeferredMutation == true;
            string currentObjId = local?.obj_id ?? string.Empty;

            pendingBuildingPlacements.Remove(temporaryUniqueId);

            if (local == null)
            {
                WorldGameObject existing =
                    WorldMap.GetWorldGameObjectByUniqueId(canonicalUniqueId, false);
                WorldGameObject created = ApplyCanonicalBuildingState(existing, serialized);
                if (created != null)
                {
                    string action = existing == null ? "restored" : "refreshed";
                    CoopMod.Logger.LogInfo(
                        $"{LogPrefix} Local placement acknowledgement {action} " +
                        $"canonical_uid={canonicalUniqueId}, obj_id={canonicalObjId}");
                }
                return;
            }

            bool remapped = false;
            ApplyRemote(() =>
            {
                bool wasApplying = IsApplyingCanonicalWgoState;
                IsApplyingCanonicalWgoState = true;
                try
                {
                    WorldGameObject canonicalCollision =
                        WorldMap.GetWorldGameObjectByUniqueId(
                            canonicalUniqueId,
                            false);
                    if (canonicalCollision != null && canonicalCollision != local)
                    {
                        // A periodic host snapshot can arrive after the host accepted the
                        // request but before this acknowledgement survives a channel resync.
                        // Keep the locally-interacted instance and discard that duplicate.
                        RemoveWgoInstanceWithoutSync(canonicalCollision);
                    }

                    remapped = TryRemapWgoUniqueId(local, canonicalUniqueId);
                    if (remapped)
                    {
                        local.just_built = true;
                        FinalizeRemoteWgoRestore(local);
                    }
                }
                finally
                {
                    IsApplyingCanonicalWgoState = wasApplying;
                }
            });

            if (!remapped)
            {
                ApplyCanonicalBuildingState(local, serialized);
                return;
            }

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Accepted canonical ID for local building placement: " +
                $"temp_uid={temporaryUniqueId}, canonical_uid={canonicalUniqueId}, " +
                $"obj_id={local.obj_id}, pos={local.transform.position}");

            if (deferredMutation &&
                !string.Equals(currentObjId, canonicalObjId, StringComparison.Ordinal))
            {
                NotifyWgoReplaced(canonicalUniqueId, canonicalObjId, local);
            }
        }

        private WorldGameObject ApplyCanonicalBuildingState(
            WorldGameObject target,
            SerializableWGO serialized)
        {
            WorldGameObject applied = null;
            ApplyRemote(() =>
            {
                bool wasApplying = IsApplyingCanonicalWgoState;
                IsApplyingCanonicalWgoState = true;
                try
                {
                    WorldGameObject destination = target ??
                        WorldGameObject.InstantiateWGOPrefab();
                    if (destination == null)
                        return;

                    if (destination.unique_id > 0L)
                        WGORegistry.Instance?.Unregister(destination.unique_id);

                    if (destination.unique_id > 0L &&
                        destination.unique_id != serialized.unique_id)
                    {
                        WorldMap.OnDestroyWGO(destination);
                    }

                    destination.RestoreFromSerializedObject(
                        serialized,
                        destination.transform.parent != MainGame.me?.world_root);
                    destination.just_built = true;
                    FinalizeRemoteWgoRestore(destination);
                    WGORegistry.Instance?.Register(destination);
                    applied = destination;
                }
                finally
                {
                    IsApplyingCanonicalWgoState = wasApplying;
                }
            });
            return applied;
        }

        private void BroadcastCanonicalBuildingPlacement(
            ulong originSteamId,
            long temporaryUniqueId,
            WorldGameObject wgo)
        {
            if (!TrySerializeWgo(wgo, out string json))
                return;

            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            using (var stream = new MemoryStream(jsonBytes.Length + 64))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PayloadVersion);
                writer.Write(SubCanonicalBuildingPlacement);
                writer.Write(NextSequence++);
                writer.Write(originSteamId);
                writer.Write(temporaryUniqueId);
                writer.Write(wgo.unique_id);
                writer.Write(wgo.obj_id ?? string.Empty);
                writer.Write(jsonBytes.Length);
                writer.Write(jsonBytes);
                writer.Flush();
                SteamP2PManager.Instance?.BroadcastSpawnSync(stream.ToArray());
            }
        }

        private void HandleFlowPlacementRequest(BinaryReader reader, CSteamID senderID)
        {
            if (!TryReadFlowPlacement(reader, senderID, out FlowPlacement placement))
                return;

            WorldGameObject target = ResolveFlowPlacementWgo(placement);
            GDPoint gdPoint = WorldMap.GetGDPointByGDTag(placement.GdPointTag, true, true);
            if (target == null ||
                !string.Equals(target.obj_id, placement.ObjId, StringComparison.Ordinal) ||
                gdPoint == null)
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Rejected Flow placement request: uid={placement.UniqueId}, " +
                    $"obj_id={placement.ObjId}, gd_point={placement.GdPointTag}, " +
                    $"resolved_wgo={target != null}, " +
                    $"identity_match={target != null && string.Equals(target.obj_id, placement.ObjId, StringComparison.Ordinal)}, " +
                    $"resolved_point={gdPoint != null}");
                return;
            }

            bool ownsRemoteCutscene =
                GraveyardKeeperCoop.Patches.CutsceneSyncPatches
                    .IsRemoteNpcVisualAuthority(senderID) ||
                GraveyardKeeperCoop.Patches.NpcInteractionSyncPatches
                    .IsRemoteNpcVisualAuthority(senderID);
            if (placement.RouteCompleted && !ownsRemoteCutscene)
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Rejected completed Flow route from {senderID}: " +
                    $"uid={target.unique_id}, obj_id={target.obj_id}, " +
                    $"gd_point={placement.GdPointTag}, authorized=false");
                return;
            }

            ApplyFlowPlacement(
                target,
                gdPoint,
                placement.Active,
                placement.RouteCompleted);

            byte[] canonicalPayload = SerializeFlowPlacement(
                SubCanonicalFlowPlacement,
                NextSequence++,
                target,
                placement.GdPointTag,
                placement.Active,
                placement.RouteCompleted);
            SteamP2PManager.Instance?.BroadcastSpawnSync(canonicalPayload);
            NpcVisualSync.Instance?.BroadcastAuthoritativeStateNow(target);

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Host applied canonical Flow placement from {senderID}: " +
                $"uid={target.unique_id}, obj_id={target.obj_id}, " +
                $"gd_point={placement.GdPointTag}, active={placement.Active}, " +
                $"route_completed={placement.RouteCompleted}");
        }

        private void HandleCanonicalFlowPlacement(BinaryReader reader, CSteamID senderID)
        {
            if (!TryReadFlowPlacement(reader, senderID, out FlowPlacement placement))
                return;

            WorldGameObject target = ResolveFlowPlacementWgo(placement);
            GDPoint gdPoint = WorldMap.GetGDPointByGDTag(placement.GdPointTag, true, true);
            if (target == null ||
                !string.Equals(target.obj_id, placement.ObjId, StringComparison.Ordinal) ||
                gdPoint == null)
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Could not apply canonical Flow placement: " +
                    $"uid={placement.UniqueId}, obj_id={placement.ObjId}, " +
                    $"gd_point={placement.GdPointTag}");
                return;
            }

            ApplyFlowPlacement(
                target,
                gdPoint,
                placement.Active,
                placement.RouteCompleted);
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Applied canonical Flow placement: uid={target.unique_id}, " +
                $"obj_id={target.obj_id}, gd_point={placement.GdPointTag}, " +
                $"active={placement.Active}, route_completed={placement.RouteCompleted}");
        }

        private struct PuffFxState
        {
            public ulong OriginSteamId;
            public long UniqueId;
            public string ObjId;
            public string CustomTag;
            public Bounds Bounds;
        }

        private void HandlePuffFxRequest(
            BinaryReader reader,
            CSteamID senderID)
        {
            if (!TryReadPuffFx(reader, senderID, out PuffFxState state) ||
                state.OriginSteamId != senderID.m_SteamID)
            {
                return;
            }

            WorldGameObject target = ResolvePuffFxWgo(state);
            if (target == null ||
                !string.Equals(target.obj_id, state.ObjId, StringComparison.Ordinal) ||
                !TryMarkPuffFxRelay(target.unique_id))
            {
                return;
            }

            DrawPuffFxAtBounds(state.Bounds);
            SteamP2PManager.Instance?.BroadcastSpawnSync(
                SerializePuffFx(
                    SubCanonicalPuffFx,
                    NextSequence++,
                    state.OriginSteamId,
                    target,
                    state.Bounds));
        }

        private void HandleCanonicalPuffFx(
            BinaryReader reader,
            CSteamID senderID)
        {
            if (!TryReadPuffFx(reader, senderID, out PuffFxState state))
                return;

            DrawPuffFxAtBounds(state.Bounds);
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Replayed PuffFX for {state.ObjId} " +
                $"(uid={state.UniqueId}, origin={state.OriginSteamId}, " +
                $"pos={state.Bounds.center})");
        }

        private bool TryReadPuffFx(
            BinaryReader reader,
            CSteamID senderID,
            out PuffFxState state)
        {
            state = default(PuffFxState);
            if (!CheckSequence(reader, senderID))
                return false;

            state.OriginSteamId = reader.ReadUInt64();
            state.UniqueId = reader.ReadInt64();
            state.ObjId = reader.ReadString();
            state.CustomTag = reader.ReadString();
            var center = new Vector3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle());
            var size = new Vector3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle());
            state.Bounds = new Bounds(center, size);

            return state.OriginSteamId != 0UL &&
                   state.UniqueId > 0L &&
                   !string.IsNullOrEmpty(state.ObjId) &&
                   state.ObjId != "0" &&
                   IsValidPuffFxBounds(state.Bounds);
        }

        private static byte[] SerializePuffFx(
            byte subType,
            uint sequence,
            ulong originSteamId,
            WorldGameObject wgo,
            Bounds bounds)
        {
            using (var stream = new MemoryStream(128))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PayloadVersion);
                writer.Write(subType);
                writer.Write(sequence);
                writer.Write(originSteamId);
                writer.Write(wgo.unique_id);
                writer.Write(wgo.obj_id ?? string.Empty);
                writer.Write(wgo.custom_tag ?? string.Empty);
                writer.Write(bounds.center.x);
                writer.Write(bounds.center.y);
                writer.Write(bounds.center.z);
                writer.Write(bounds.size.x);
                writer.Write(bounds.size.y);
                writer.Write(bounds.size.z);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static WorldGameObject ResolvePuffFxWgo(PuffFxState state)
        {
            WorldGameObject target =
                WorldMap.GetWorldGameObjectByUniqueId(state.UniqueId, false);
            if (target != null)
                return target;

            if (!string.IsNullOrEmpty(state.CustomTag))
            {
                target = WorldMap.GetWorldGameObjectByCustomTag(
                    state.CustomTag,
                    true);
                if (target != null &&
                    string.Equals(target.obj_id, state.ObjId, StringComparison.Ordinal))
                {
                    return target;
                }
            }

            return null;
        }

        private bool TryMarkPuffFxRelay(long uniqueId)
        {
            float now = Time.realtimeSinceStartup;
            if (lastPuffFxRelayAt.TryGetValue(uniqueId, out float lastAt) &&
                now - lastAt < PuffFxRelayDedupeSeconds)
            {
                return false;
            }

            lastPuffFxRelayAt[uniqueId] = now;
            return true;
        }

        private static bool IsValidPuffFxBounds(Bounds bounds)
        {
            Vector3 center = bounds.center;
            Vector3 size = bounds.size;
            return IsFinitePuffValue(center.x) &&
                   IsFinitePuffValue(center.y) &&
                   IsFinitePuffValue(center.z) &&
                   IsFinitePuffValue(size.x) &&
                   IsFinitePuffValue(size.y) &&
                   IsFinitePuffValue(size.z) &&
                   size.x >= 0f && size.y >= 0f && size.z >= 0f &&
                   size.x <= 4096f && size.y <= 4096f && size.z <= 4096f;
        }

        private static bool IsFinitePuffValue(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void DrawPuffFxAtBounds(Bounds bounds)
        {
            if (MainGame.me?.world_root == null)
                return;

            try
            {
                PuffFX prefab = Resources.Load<PuffFX>("puff fx");
                if (prefab == null)
                    return;

                PuffFX puff = prefab.Copy(null, true, string.Empty);
                puff.transform.SetParent(MainGame.me.world_root, false);
                puff.transform.position = bounds.center;
                bool small =
                    (new Vector2(
                         Mathf.Round(bounds.size.x),
                         Mathf.Round(bounds.size.y)) / 96f).magnitude <= 1f;
                puff.small.gameObject.SetActive(small);
                puff.large.gameObject.SetActive(!small);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[SpawnSync] Could not replay PuffFX: {ex.Message}");
            }
        }

        private struct FlowPlacement
        {
            public long UniqueId;
            public string ObjId;
            public string CustomTag;
            public string GdPointTag;
            public bool Active;
            public bool RouteCompleted;
        }

        private sealed class InteractionEventState
        {
            public ulong OriginSteamId;
            public long UniqueId;
            public string ObjId;
            public string CustomTag;
            public Vector3 Position;
            public List<string> Events;
        }

        private void HandleInteractionEventsRequest(
            BinaryReader reader,
            CSteamID senderID)
        {
            if (!TryReadInteractionEvents(reader, senderID, out InteractionEventState state) ||
                state.OriginSteamId != senderID.m_SteamID)
            {
                return;
            }

            WorldGameObject target = ResolveInteractionEventWgo(state);
            if (target == null)
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Could not resolve interaction-event target from {senderID}: " +
                    $"uid={state.UniqueId}, obj_id={state.ObjId}, tag={state.CustomTag}");
                return;
            }

            ApplyInteractionEvents(target, state.Events);
            SteamP2PManager.Instance?.BroadcastSpawnSync(
                SerializeInteractionEvents(
                    SubCanonicalInteractionEvents,
                    NextSequence++,
                    state.OriginSteamId,
                    target));

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Host canonicalized interaction-event state: " +
                $"origin={state.OriginSteamId}, uid={target.unique_id}, " +
                $"obj_id={target.obj_id}, events={state.Events.Count}");
        }

        private void HandleCanonicalInteractionEvents(
            BinaryReader reader,
            CSteamID senderID)
        {
            if (!TryReadInteractionEvents(reader, senderID, out InteractionEventState state))
                return;

            WorldGameObject target = ResolveInteractionEventWgo(state);
            if (target == null)
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Could not resolve canonical interaction-event target: " +
                    $"uid={state.UniqueId}, obj_id={state.ObjId}, tag={state.CustomTag}");
                return;
            }

            ApplyInteractionEvents(target, state.Events);
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Applied canonical interaction-event state: " +
                $"origin={state.OriginSteamId}, uid={target.unique_id}, " +
                $"obj_id={target.obj_id}, events={state.Events.Count}");
        }

        private bool TryReadInteractionEvents(
            BinaryReader reader,
            CSteamID senderID,
            out InteractionEventState state)
        {
            state = null;
            if (!CheckSequence(reader, senderID))
                return false;

            var parsed = new InteractionEventState
            {
                OriginSteamId = reader.ReadUInt64(),
                UniqueId = reader.ReadInt64(),
                ObjId = reader.ReadString(),
                CustomTag = reader.ReadString(),
                Position = new Vector3(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle())
            };

            int count = reader.ReadInt32();
            if (parsed.OriginSteamId == 0UL || parsed.UniqueId <= 0L ||
                string.IsNullOrEmpty(parsed.ObjId) || parsed.ObjId == "0" ||
                count < 0 || count > MaxInteractionEvents ||
                !IsFinite(parsed.Position.x) ||
                !IsFinite(parsed.Position.y) ||
                !IsFinite(parsed.Position.z))
            {
                return false;
            }

            parsed.Events = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                string eventId = reader.ReadString();
                if (string.IsNullOrEmpty(eventId) ||
                    eventId.Length > MaxInteractionEventLength)
                {
                    return false;
                }
                parsed.Events.Add(eventId);
            }

            state = parsed;
            return true;
        }

        private static byte[] SerializeInteractionEvents(
            byte subType,
            uint sequence,
            ulong originSteamId,
            WorldGameObject wgo)
        {
            var events = new List<string>();
            List<string> source = wgo.custom_interaction_events;
            if (source != null)
            {
                for (int i = 0;
                    i < source.Count && events.Count < MaxInteractionEvents;
                    i++)
                {
                    string eventId = source[i];
                    if (string.IsNullOrEmpty(eventId))
                        continue;

                    events.Add(eventId.Length > MaxInteractionEventLength
                        ? eventId.Substring(0, MaxInteractionEventLength)
                        : eventId);
                }
            }

            using (var stream = new MemoryStream(256))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PayloadVersion);
                writer.Write(subType);
                writer.Write(sequence);
                writer.Write(originSteamId);
                writer.Write(wgo.unique_id);
                writer.Write(wgo.obj_id ?? string.Empty);
                writer.Write(wgo.custom_tag ?? string.Empty);
                Vector3 position = wgo.transform.position;
                writer.Write(position.x);
                writer.Write(position.y);
                writer.Write(position.z);
                writer.Write(events.Count);
                for (int i = 0; i < events.Count; i++)
                    writer.Write(events[i]);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static WorldGameObject ResolveInteractionEventWgo(
            InteractionEventState state)
        {
            WorldGameObject target =
                WorldMap.GetWorldGameObjectByUniqueId(state.UniqueId, false);
            if (target != null &&
                string.Equals(target.obj_id, state.ObjId, StringComparison.Ordinal))
            {
                return target;
            }

            if (!string.IsNullOrEmpty(state.CustomTag))
            {
                target = WorldMap.GetWorldGameObjectByCustomTag(
                    state.CustomTag,
                    true);
                if (target != null &&
                    string.Equals(target.obj_id, state.ObjId, StringComparison.Ordinal))
                {
                    return target;
                }
            }

            List<WorldGameObject> candidates =
                WorldMap.GetWorldGameObjectsByObjId(state.ObjId);
            if (candidates == null)
                return null;

            WorldGameObject nearest = null;
            float nearestSqr = 256f * 256f;
            for (int i = 0; i < candidates.Count; i++)
            {
                WorldGameObject candidate = candidates[i];
                if (candidate == null || candidate.is_removed || candidate.is_player)
                    continue;

                float distanceSqr =
                    (candidate.transform.position - state.Position).sqrMagnitude;
                if (distanceSqr < nearestSqr)
                {
                    nearest = candidate;
                    nearestSqr = distanceSqr;
                }
            }
            return nearest;
        }

        private static void ApplyInteractionEvents(
            WorldGameObject target,
            List<string> events)
        {
            bool wasApplying = IsApplyingCanonicalWgoState;
            IsApplyingCanonicalWgoState = true;
            try
            {
                target.custom_interaction_events = events == null
                    ? new List<string>()
                    : new List<string>(events);
                target.RedrawBubble(null);
            }
            finally
            {
                IsApplyingCanonicalWgoState = wasApplying;
            }
        }

        private bool TryReadFlowPlacement(
            BinaryReader reader,
            CSteamID senderID,
            out FlowPlacement placement)
        {
            placement = default(FlowPlacement);
            if (!CheckSequence(reader, senderID))
                return false;

            placement.UniqueId = reader.ReadInt64();
            placement.ObjId = reader.ReadString();
            placement.CustomTag = reader.ReadString();
            placement.GdPointTag = reader.ReadString();
            placement.Active = reader.ReadBoolean();
            placement.RouteCompleted = reader.ReadBoolean();

            return placement.UniqueId != 0L &&
                   !string.IsNullOrEmpty(placement.ObjId) &&
                   !string.IsNullOrEmpty(placement.GdPointTag);
        }

        private static byte[] SerializeFlowPlacement(
            byte subType,
            uint sequence,
            WorldGameObject wgo,
            string gdPointTag,
            bool active,
            bool routeCompleted)
        {
            using (var stream = new MemoryStream(128))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PayloadVersion);
                writer.Write(subType);
                writer.Write(sequence);
                writer.Write(wgo.unique_id);
                writer.Write(wgo.obj_id ?? "");
                writer.Write(wgo.custom_tag ?? "");
                writer.Write(gdPointTag ?? "");
                writer.Write(active);
                writer.Write(routeCompleted);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static WorldGameObject ResolveFlowPlacementWgo(FlowPlacement placement)
        {
            if (WGORegistry.Instance != null &&
                WGORegistry.Instance.TryGet(placement.UniqueId, out WorldGameObject indexed))
            {
                return indexed;
            }

            WorldGameObject byUniqueId =
                WorldMap.GetWorldGameObjectByUniqueId(placement.UniqueId, false);
            if (byUniqueId != null)
                return byUniqueId;

            if (!string.IsNullOrEmpty(placement.CustomTag))
            {
                WorldGameObject byTag =
                    WorldMap.GetWorldGameObjectByCustomTag(placement.CustomTag, true);
                if (byTag != null &&
                    string.Equals(byTag.obj_id, placement.ObjId, StringComparison.Ordinal))
                {
                    return byTag;
                }
            }

            List<WorldGameObject> byObjId =
                WorldMap.GetWorldGameObjectsByObjId(placement.ObjId);
            return byObjId != null && byObjId.Count == 1 ? byObjId[0] : null;
        }

        private static void ApplyFlowPlacement(
            WorldGameObject target,
            GDPoint gdPoint,
            bool active,
            bool routeCompleted)
        {
            bool wasApplying = IsApplyingCanonicalFlowPlacement;
            IsApplyingCanonicalFlowPlacement = true;
            try
            {
                target.transform.position = gdPoint.transform.position;
                target.RefreshPositionCache();
                target.gameObject.SetActive(active);
                target.OnCameToGDPoint(gdPoint);
                bool bishopReachedStock =
                    GraveyardKeeperCoop.Patches.BishopScheduleRepairPatches
                        .ObserveGdPointArrival(target, gdPoint);
                if (bishopReachedStock)
                {
                    GraveyardKeeperCoop.Patches.BishopScheduleRepairPatches
                        .ConfirmStockPlacement(target);
                }
                ChunkedGameObject chunk =
                    target.GetComponent<ChunkedGameObject>();
                if (routeCompleted && chunk != null)
                {
                    // Flow_GoTo normally releases this in its local completion
                    // callback. Observers do not run that callback, so a client-owned
                    // route otherwise leaves the host NPC permanently chunk-active.
                    chunk.active_now_because_of_movement = false;
                }
                chunk?.RecalculateChunk();
                NpcVisualSync.Instance?.AcceptCanonicalPlacement(
                    target,
                    gdPoint.transform.position);
                LiveWGOTransformSync.Instance?.AcceptCanonicalPlacement(
                    target,
                    gdPoint.transform.position);
            }
            finally
            {
                IsApplyingCanonicalFlowPlacement = wasApplying;
            }
        }

        private void HandleCutsceneActorSpawnRequest(
            BinaryReader reader,
            CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID))
                return;

            long requestedUniqueId = reader.ReadInt64();
            string objId = reader.ReadString();
            bool active = reader.ReadBoolean();
            int jsonLen = reader.ReadInt32();
            if (requestedUniqueId <= 0L ||
                string.IsNullOrEmpty(objId) || objId == "0" ||
                jsonLen <= 0 || jsonLen > MaxSerializedWgoBytes)
            {
                return;
            }

            byte[] jsonBytes = reader.ReadBytes(jsonLen);
            bool ownsRemoteCutscene =
                GraveyardKeeperCoop.Patches.CutsceneSyncPatches
                    .IsRemoteNpcVisualAuthority(senderID) ||
                GraveyardKeeperCoop.Patches.NpcInteractionSyncPatches
                    .IsRemoteNpcVisualAuthority(senderID);
            if (jsonBytes.Length != jsonLen ||
                !ownsRemoteCutscene ||
                !TryDeserializeWgo(
                    jsonBytes,
                    requestedUniqueId,
                    objId,
                    out SerializableWGO serialized) ||
                !IsValidCutsceneActorSpawn(serialized))
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Rejected cutscene actor spawn from {senderID}: " +
                    $"uid={requestedUniqueId}, obj_id={objId}, " +
                    $"authorized={ownsRemoteCutscene}");
                return;
            }

            WorldGameObject existing = ResolveExistingCutsceneActor(
                requestedUniqueId,
                serialized);
            long canonicalUniqueId = existing?.unique_id ??
                AllocateCanonicalUniqueId();
            if (canonicalUniqueId <= 0L)
            {
                CoopMod.Logger.LogError(
                    $"{LogPrefix} Could not allocate a canonical ID for " +
                    $"cutscene actor uid={requestedUniqueId}, obj_id={objId}");
                return;
            }

            serialized.unique_id = canonicalUniqueId;
            WorldGameObject actor = ApplyCanonicalCutsceneActorState(
                existing,
                serialized,
                active);
            if (actor == null)
                return;

            BroadcastCanonicalCutsceneActorSpawn(
                senderID.m_SteamID,
                requestedUniqueId,
                actor,
                active);

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Host canonicalized client cutscene actor: " +
                $"player={senderID}, requested_uid={requestedUniqueId}, " +
                $"canonical_uid={actor.unique_id}, obj_id={actor.obj_id}, " +
                $"tag={actor.custom_tag}, reused={existing != null}");
        }

        private void HandleCanonicalCutsceneActorSpawn(
            BinaryReader reader,
            CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID))
                return;

            ulong originSteamId = reader.ReadUInt64();
            long requestedUniqueId = reader.ReadInt64();
            long canonicalUniqueId = reader.ReadInt64();
            string objId = reader.ReadString();
            bool active = reader.ReadBoolean();
            int jsonLen = reader.ReadInt32();
            if (originSteamId == 0UL || requestedUniqueId <= 0L ||
                canonicalUniqueId <= 0L ||
                string.IsNullOrEmpty(objId) || objId == "0" ||
                jsonLen <= 0 || jsonLen > MaxSerializedWgoBytes)
            {
                return;
            }

            byte[] jsonBytes = reader.ReadBytes(jsonLen);
            if (jsonBytes.Length != jsonLen ||
                !TryDeserializeWgo(
                    jsonBytes,
                    canonicalUniqueId,
                    objId,
                    out SerializableWGO serialized) ||
                !IsValidCutsceneActorSpawn(serialized))
            {
                return;
            }

            if (originSteamId == SteamUser.GetSteamID().m_SteamID &&
                ApplyOriginCutsceneActorCanonicalId(
                    requestedUniqueId,
                    canonicalUniqueId,
                    serialized,
                    active))
            {
                return;
            }

            WorldGameObject existing =
                WorldMap.GetWorldGameObjectByUniqueId(
                    canonicalUniqueId,
                    false) ??
                ResolveExistingCutsceneActor(
                    requestedUniqueId,
                    serialized);
            WorldGameObject actor = ApplyCanonicalCutsceneActorState(
                existing,
                serialized,
                active);
            if (actor != null)
            {
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Applied canonical cutscene actor: " +
                    $"origin={originSteamId}, canonical_uid={canonicalUniqueId}, " +
                    $"obj_id={objId}, tag={actor.custom_tag}");
            }
        }

        private bool ApplyOriginCutsceneActorCanonicalId(
            long requestedUniqueId,
            long canonicalUniqueId,
            SerializableWGO serialized,
            bool active)
        {
            WorldGameObject local =
                WorldMap.GetWorldGameObjectByUniqueId(
                    requestedUniqueId,
                    false) ??
                ResolveExistingCutsceneActor(
                    requestedUniqueId,
                    serialized);
            if (local == null ||
                !string.Equals(
                    local.obj_id,
                    serialized.obj_id,
                    StringComparison.Ordinal))
            {
                return false;
            }

            bool wasApplying = IsApplyingCanonicalWgoState;
            IsApplyingCanonicalWgoState = true;
            try
            {
                if (local.unique_id != canonicalUniqueId)
                {
                    WorldGameObject collision =
                        WorldMap.GetWorldGameObjectByUniqueId(
                            canonicalUniqueId,
                            false);
                    if (collision != null && collision != local)
                    {
                        CoopMod.Logger.LogWarning(
                            $"{LogPrefix} Could not remap originating cutscene actor " +
                            $"{requestedUniqueId} -> {canonicalUniqueId}: canonical ID is occupied");
                        return false;
                    }

                    if (!TryRemapWgoUniqueId(local, canonicalUniqueId))
                        return false;
                }

                if (!string.IsNullOrEmpty(serialized.custom_tag))
                    local.custom_tag = serialized.custom_tag;
                local.gameObject.SetActive(active);
                local.RefreshPositionCache();
                WGORegistry.Instance?.Register(local);
            }
            finally
            {
                IsApplyingCanonicalWgoState = wasApplying;
            }

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Accepted canonical ID for local cutscene actor: " +
                $"requested_uid={requestedUniqueId}, canonical_uid={canonicalUniqueId}, " +
                $"obj_id={serialized.obj_id}, tag={local.custom_tag}");
            return true;
        }

        private WorldGameObject ApplyCanonicalCutsceneActorState(
            WorldGameObject target,
            SerializableWGO serialized,
            bool active)
        {
            WorldGameObject applied = null;
            ApplyRemote(() =>
            {
                bool wasApplying = IsApplyingCanonicalWgoState;
                IsApplyingCanonicalWgoState = true;
                try
                {
                    WorldGameObject destination = target ??
                        WorldGameObject.InstantiateWGOPrefab();
                    if (destination == null)
                        return;

                    long previousUniqueId = destination.unique_id;
                    if (previousUniqueId > 0L &&
                        previousUniqueId != serialized.unique_id)
                    {
                        WGORegistry.Instance?.Unregister(previousUniqueId);
                        WorldMap.OnDestroyWGO(destination);
                    }

                    destination.RestoreFromSerializedObject(
                        serialized,
                        destination.transform.parent != MainGame.me?.world_root);
                    destination.gameObject.SetActive(active);
                    FinalizeRemoteWgoRestore(destination);
                    WGORegistry.Instance?.Register(destination);
                    applied = destination;
                }
                finally
                {
                    IsApplyingCanonicalWgoState = wasApplying;
                }
            });
            return applied;
        }

        private void BroadcastCanonicalCutsceneActorSpawn(
            ulong originSteamId,
            long requestedUniqueId,
            WorldGameObject actor,
            bool active)
        {
            if (!TrySerializeWgo(actor, out string json))
                return;

            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            using (var stream = new MemoryStream(jsonBytes.Length + 64))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PayloadVersion);
                writer.Write(SubCanonicalCutsceneActorSpawn);
                writer.Write(NextSequence++);
                writer.Write(originSteamId);
                writer.Write(requestedUniqueId);
                writer.Write(actor.unique_id);
                writer.Write(actor.obj_id ?? string.Empty);
                writer.Write(active);
                writer.Write(jsonBytes.Length);
                writer.Write(jsonBytes);
                writer.Flush();
                SteamP2PManager.Instance?.BroadcastSpawnSync(
                    stream.ToArray());
            }
        }

        private static WorldGameObject ResolveExistingCutsceneActor(
            long requestedUniqueId,
            SerializableWGO serialized)
        {
            WorldGameObject exact =
                WorldMap.GetWorldGameObjectByUniqueId(
                    requestedUniqueId,
                    false);
            if (exact != null &&
                string.Equals(
                    exact.obj_id,
                    serialized.obj_id,
                    StringComparison.Ordinal))
            {
                return exact;
            }

            if (!string.IsNullOrEmpty(serialized.custom_tag))
            {
                WorldGameObject byTag =
                    WorldMap.GetWorldGameObjectByCustomTag(
                        serialized.custom_tag,
                        true);
                if (byTag != null &&
                    string.Equals(
                        byTag.obj_id,
                        serialized.obj_id,
                        StringComparison.Ordinal))
                {
                    return byTag;
                }
            }

            List<WorldGameObject> candidates =
                WorldMap.GetWorldGameObjectsByObjId(serialized.obj_id);
            if (candidates == null)
                return null;

            WorldGameObject nearest = null;
            float nearestSqr = 256f * 256f;
            for (int i = 0; i < candidates.Count; i++)
            {
                WorldGameObject candidate = candidates[i];
                if (candidate == null || candidate.is_removed ||
                    candidate.is_player)
                {
                    continue;
                }

                float distanceSqr =
                    (candidate.transform.position - serialized.position)
                    .sqrMagnitude;
                if (distanceSqr < nearestSqr)
                {
                    nearest = candidate;
                    nearestSqr = distanceSqr;
                }
            }
            return nearest;
        }

        private static bool IsValidCutsceneActorSpawn(
            SerializableWGO serialized)
        {
            return serialized.unique_id > 0L &&
                   !string.IsNullOrEmpty(serialized.obj_id) &&
                   serialized.obj_id != "0" &&
                   IsFinite(serialized.position.x) &&
                   IsFinite(serialized.position.y) &&
                   IsFinite(serialized.position.z) &&
                   IsNpcDefinition(serialized.obj_id);
        }

        private static bool IsNpcDefinition(string objId)
        {
            if (string.IsNullOrEmpty(objId))
                return false;

            ObjectDefinition definition =
                GameBalance.me?.GetDataOrNull<ObjectDefinition>(objId);
            if (definition == null)
                return false;

            try { return definition.IsNPC(); }
            catch { return false; }
        }

        private void HandleWgoSpawned(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            long uid = reader.ReadInt64();
            string objId = reader.ReadString();
            int jsonLen = reader.ReadInt32();
            if (jsonLen <= 0 || jsonLen > MaxSerializedWgoBytes) return;
            byte[] jsonBytes = reader.ReadBytes(jsonLen);
            if (jsonBytes.Length != jsonLen) return;

            var existing = WorldMap.GetWorldGameObjectByUniqueId(uid, false);
            if (!TryDeserializeWgo(jsonBytes, uid, objId, out SerializableWGO serialized)) return;

            ApplyRemote(() =>
            {
                WorldGameObject target = existing ?? WorldGameObject.InstantiateWGOPrefab();
                if (target == null)
                {
                    CoopMod.Logger.LogWarning($"{LogPrefix} Could not create WGO: uid={uid}, obj_id={objId}");
                    return;
                }

                long previousUid = target.unique_id;
                if (previousUid != 0L)
                    WGORegistry.Instance?.Unregister(previousUid);

                target.RestoreFromSerializedObject(serialized, existing == null);
                FinalizeRemoteWgoRestore(target);
                WGORegistry.Instance?.Register(target);
                CoopMod.Logger.LogInfo(
                    existing != null
                        ? $"{LogPrefix} Applied spawned WGO state: uid={uid}, " +
                          $"obj_id={objId}, tag={target.custom_tag}"
                        : $"{LogPrefix} Created spawned WGO: uid={uid}, " +
                          $"obj_id={objId}, tag={target.custom_tag}");
            });
        }

        private void HandleWgoReplaced(BinaryReader reader, CSteamID senderID)
        {
            if (!TryReadWgoReplacement(
                    reader,
                    senderID,
                    out WgoReplacement replacement))
                return;

            ApplyWgoReplacement(replacement, "canonical");
        }

        private void HandleWgoReplaceRequest(
            BinaryReader reader,
            CSteamID senderID)
        {
            if (!TryReadWgoReplacement(
                    reader,
                    senderID,
                    out WgoReplacement replacement))
                return;

            string placementKey = BuildPlacementKey(
                senderID.m_SteamID,
                replacement.OldUniqueId);
            if (canonicalPlacementIds.TryGetValue(
                    placementKey,
                    out long canonicalUniqueId))
            {
                replacement.OldUniqueId = canonicalUniqueId;
                replacement.NewUniqueId = canonicalUniqueId;
                SerializableWGO remapped = replacement.Serialized;
                remapped.unique_id = canonicalUniqueId;
                replacement.Serialized = remapped;
            }

            WorldGameObject target =
                ApplyWgoReplacement(replacement, "client request");
            if (target == null)
                return;

            SendWgoReplaced(replacement.OldUniqueId, target);
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Host canonicalized WGO replacement from {senderID}: " +
                $"old_uid={replacement.OldUniqueId}, " +
                $"new_uid={target.unique_id}, obj_id={target.obj_id}");
        }

        private sealed class WgoReplacement
        {
            public long OldUniqueId;
            public long NewUniqueId;
            public string ObjId;
            public SerializableWGO Serialized;
        }

        private bool TryReadWgoReplacement(
            BinaryReader reader,
            CSteamID senderID,
            out WgoReplacement replacement)
        {
            replacement = null;
            if (!CheckSequence(reader, senderID))
                return false;

            long oldUid = reader.ReadInt64();
            long newUid = reader.ReadInt64();
            string objId = reader.ReadString();
            int jsonLen = reader.ReadInt32();
            if (oldUid == 0L ||
                newUid == 0L ||
                string.IsNullOrEmpty(objId) ||
                objId == "0" ||
                jsonLen <= 0 ||
                jsonLen > MaxSerializedWgoBytes)
            {
                return false;
            }

            byte[] jsonBytes = reader.ReadBytes(jsonLen);
            if (jsonBytes.Length != jsonLen ||
                !TryDeserializeWgo(
                    jsonBytes,
                    newUid,
                    objId,
                    out SerializableWGO serialized))
            {
                return false;
            }

            replacement = new WgoReplacement
            {
                OldUniqueId = oldUid,
                NewUniqueId = newUid,
                ObjId = objId,
                Serialized = serialized
            };
            return true;
        }

        private WorldGameObject ApplyWgoReplacement(
            WgoReplacement replacement,
            string source)
        {
            WorldGameObject applied = null;
            ApplyRemote(() =>
            {
                WorldGameObject target =
                    WorldMap.GetWorldGameObjectByUniqueId(
                        replacement.NewUniqueId,
                        false) ??
                    WorldMap.GetWorldGameObjectByUniqueId(
                        replacement.OldUniqueId,
                        false) ??
                    WorldGameObject.InstantiateWGOPrefab();
                if (target == null)
                {
                    CoopMod.Logger.LogWarning(
                        $"{LogPrefix} Could not apply {source} replacement: " +
                        $"old_uid={replacement.OldUniqueId}, " +
                        $"new_uid={replacement.NewUniqueId}, " +
                        $"obj_id={replacement.ObjId}");
                    return;
                }

                bool wasApplying = IsApplyingCanonicalWgoState;
                IsApplyingCanonicalWgoState = true;
                try
                {
                    WGORegistry.Instance?.Unregister(
                        replacement.OldUniqueId);
                    if (target.unique_id != 0L &&
                        target.unique_id != replacement.OldUniqueId)
                    {
                        WGORegistry.Instance?.Unregister(
                            target.unique_id);
                    }

                    target.RestoreFromSerializedObject(
                        replacement.Serialized,
                        target.transform.parent !=
                        MainGame.me?.world_root);
                    FinalizeRemoteWgoRestore(target);
                    WGORegistry.Instance?.Register(target);
                    applied = target;
                }
                finally
                {
                    IsApplyingCanonicalWgoState = wasApplying;
                }

                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Applied {source} WGO replacement: " +
                    $"old_uid={replacement.OldUniqueId}, " +
                    $"new_uid={replacement.NewUniqueId}, " +
                    $"obj_id={replacement.ObjId}");
            });
            return applied;
        }

        private static BuildingPreviewPose CaptureBuildingPreviewPose(
            WorldGameObject wgo,
            bool canBuild)
        {
            return new BuildingPreviewPose
            {
                ObjId = wgo.obj_id ?? string.Empty,
                Position = wgo.transform.position,
                Rotation = wgo.transform.rotation,
                Scale = wgo.transform.localScale,
                Variation = wgo.variation,
                Variation2 = wgo.variation_2,
                CanBuild = canBuild,
                ZoneId =
                    MainGame.me?.build_mode_logics?.cur_build_zone_id ??
                    string.Empty,
                CustomSubZoneId =
                    BuildGrid.me != null
                        ? BuildGrid.GetCurrentSubZoneID()
                        : string.Empty,
                FootprintCells = CaptureLocalPreviewFootprint(wgo)
            };
        }

        private static List<Vector2> CaptureLocalPreviewFootprint(
            WorldGameObject wgo)
        {
            var result = new List<Vector2>();
            if (wgo == null)
                return result;

            // Vanilla stores the active floating footprint in a private static
            // list. The cells are parented to FloatingWorldGameObject rather
            // than reliably discoverable from every WorldGameObject clone, so
            // searching the WGO hierarchy produced empty preview envelopes.
            IList<FlowGridCell> cells = null;
            FloatingWorldGameObject floating =
                FloatingWorldGameObject.cur_floating;
            if (floating != null && floating.wobj == wgo &&
                FloatingFlowGridCellsField != null)
            {
                cells = FloatingFlowGridCellsField.GetValue(null)
                    as IList<FlowGridCell>;
            }

            if (cells == null)
            {
                cells = wgo.GetComponentsInChildren<FlowGridCell>(true);
            }

            for (int i = 0;
                 i < cells.Count &&
                 result.Count < MaxPreviewFootprintCells;
                 i++)
            {
                FlowGridCell cell = cells[i];
                if (cell == null ||
                    cell.cell_type != FlowGridCell.CellType.UnderObject)
                {
                    continue;
                }

                Vector3 local =
                    wgo.transform.InverseTransformPoint(
                        cell.transform.position);
                if (float.IsNaN(local.x) || float.IsInfinity(local.x) ||
                    float.IsNaN(local.y) || float.IsInfinity(local.y))
                {
                    continue;
                }
                result.Add(new Vector2(local.x, local.y));
            }

            result.Sort((left, right) =>
            {
                int x = left.x.CompareTo(right.x);
                return x != 0 ? x : left.y.CompareTo(right.y);
            });
            return result;
        }

        private static bool PreviewPosesEqual(
            BuildingPreviewPose left,
            BuildingPreviewPose right)
        {
            return string.Equals(left.ObjId, right.ObjId, StringComparison.Ordinal) &&
                   (left.Position - right.Position).sqrMagnitude < 0.0001f &&
                   Quaternion.Angle(left.Rotation, right.Rotation) < 0.01f &&
                   (left.Scale - right.Scale).sqrMagnitude < 0.0001f &&
                   left.Variation == right.Variation &&
                   left.Variation2 == right.Variation2 &&
                   left.CanBuild == right.CanBuild &&
                   string.Equals(
                       left.ZoneId,
                       right.ZoneId,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       left.CustomSubZoneId,
                       right.CustomSubZoneId,
                       StringComparison.Ordinal) &&
                   PreviewFootprintsEqual(
                       left.FootprintCells,
                       right.FootprintCells);
        }

        private static bool PreviewFootprintsEqual(
            List<Vector2> left,
            List<Vector2> right)
        {
            int leftCount = left?.Count ?? 0;
            int rightCount = right?.Count ?? 0;
            if (leftCount != rightCount)
                return false;

            for (int i = 0; i < leftCount; i++)
            {
                if ((left[i] - right[i]).sqrMagnitude >= 0.000001f)
                    return false;
            }
            return true;
        }

        private static byte[] SerializeBuildingPreview(
            byte subType,
            ulong originSteamId,
            uint session,
            uint sequence,
            BuildingPreviewPose pose)
        {
            using (var stream = new MemoryStream(128))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PayloadVersion);
                writer.Write(subType);
                if (subType == SubCanonicalBuildingPreview)
                    writer.Write(originSteamId);
                writer.Write(session);
                writer.Write(sequence);
                writer.Write(pose.ObjId ?? string.Empty);
                writer.Write(pose.Position.x);
                writer.Write(pose.Position.y);
                writer.Write(pose.Position.z);
                writer.Write(pose.Rotation.x);
                writer.Write(pose.Rotation.y);
                writer.Write(pose.Rotation.z);
                writer.Write(pose.Rotation.w);
                writer.Write(pose.Scale.x);
                writer.Write(pose.Scale.y);
                writer.Write(pose.Scale.z);
                writer.Write(pose.Variation);
                writer.Write(pose.Variation2);
                writer.Write(pose.CanBuild);
                writer.Write(pose.ZoneId ?? string.Empty);
                writer.Write(pose.CustomSubZoneId ?? string.Empty);
                int footprintCount = Math.Min(
                    pose.FootprintCells?.Count ?? 0,
                    MaxPreviewFootprintCells);
                writer.Write(footprintCount);
                for (int i = 0; i < footprintCount; i++)
                {
                    writer.Write(pose.FootprintCells[i].x);
                    writer.Write(pose.FootprintCells[i].y);
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static byte[] SerializeBuildingPreviewEnd(
            byte subType,
            ulong originSteamId,
            uint session,
            uint sequence)
        {
            using (var stream = new MemoryStream(32))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PayloadVersion);
                writer.Write(subType);
                if (subType == SubCanonicalBuildingPreviewEnd)
                    writer.Write(originSteamId);
                writer.Write(session);
                writer.Write(sequence);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static bool TryReadBuildingPreview(
            BinaryReader reader,
            bool hasOrigin,
            ulong fallbackOrigin,
            out ulong originSteamId,
            out uint session,
            out uint sequence,
            out BuildingPreviewPose pose)
        {
            originSteamId = hasOrigin ? reader.ReadUInt64() : fallbackOrigin;
            session = reader.ReadUInt32();
            sequence = reader.ReadUInt32();
            pose = new BuildingPreviewPose
            {
                ObjId = reader.ReadString(),
                Position = new Vector3(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle()),
                Rotation = new Quaternion(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle()),
                Scale = new Vector3(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle()),
                Variation = reader.ReadInt32(),
                Variation2 = reader.ReadInt32(),
                CanBuild = reader.ReadBoolean(),
                // Zone metadata was appended to the existing preview envelope,
                // so older preview-capable peers remain wire-compatible.
                ZoneId = reader.BaseStream.Position < reader.BaseStream.Length
                    ? reader.ReadString()
                    : string.Empty,
                CustomSubZoneId =
                    reader.BaseStream.Position < reader.BaseStream.Length
                        ? reader.ReadString()
                        : string.Empty,
                FootprintCells = new List<Vector2>()
            };

            if (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                int footprintCount = reader.ReadInt32();
                long remainingBytes =
                    reader.BaseStream.Length -
                    reader.BaseStream.Position;
                if (footprintCount < 0 ||
                    footprintCount > MaxPreviewFootprintCells ||
                    remainingBytes < footprintCount * 8L)
                {
                    return false;
                }

                for (int i = 0; i < footprintCount; i++)
                {
                    var cell = new Vector2(
                        reader.ReadSingle(),
                        reader.ReadSingle());
                    if (!IsFinite(cell))
                        return false;
                    pose.FootprintCells.Add(cell);
                }
            }

            if (originSteamId == 0UL || session == 0 || sequence == 0 ||
                string.IsNullOrEmpty(pose.ObjId) || pose.ObjId == "0" ||
                pose.ObjId == "_cursor" || pose.ObjId.Length > 256 ||
                pose.ZoneId.Length > 256 ||
                pose.CustomSubZoneId.Length > 256 ||
                !IsFinite(pose.Position) || !IsFinite(pose.Rotation) ||
                !IsFinite(pose.Scale) ||
                QuaternionMagnitudeSquared(pose.Rotation) < 0.0001f ||
                Mathf.Abs(pose.Scale.x) > 100f ||
                Mathf.Abs(pose.Scale.y) > 100f ||
                Mathf.Abs(pose.Scale.z) > 100f)
            {
                return false;
            }

            ObjectDefinition definition =
                GameBalance.me?.GetDataOrNull<ObjectDefinition>(pose.ObjId);
            if (definition == null)
                return false;
            try { return !definition.IsNPC(); }
            catch { return false; }
        }

        private static bool TryReadBuildingPreviewEnd(
            BinaryReader reader,
            bool hasOrigin,
            ulong fallbackOrigin,
            out ulong originSteamId,
            out uint session,
            out uint sequence)
        {
            originSteamId = hasOrigin ? reader.ReadUInt64() : fallbackOrigin;
            session = reader.ReadUInt32();
            sequence = reader.ReadUInt32();
            return originSteamId != 0UL && session != 0 && sequence != 0;
        }

        private void BroadcastCanonicalBuildingPreviewEnd(
            ulong originSteamId,
            uint session,
            uint sequence,
            CSteamID? except = null)
        {
            byte[] payload = SerializeBuildingPreviewEnd(
                SubCanonicalBuildingPreviewEnd,
                originSteamId,
                session,
                sequence);
            for (int i = 0; i < PreviewEndRepeatCount; i++)
            {
                SteamP2PManager.Instance?.BroadcastSpawnPreview(
                    payload,
                    except);
            }
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(Quaternion value)
        {
            return IsFinite(value.x) && IsFinite(value.y) &&
                   IsFinite(value.z) && IsFinite(value.w);
        }

        private static float QuaternionMagnitudeSquared(Quaternion value)
        {
            return value.x * value.x + value.y * value.y +
                   value.z * value.z + value.w * value.w;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private long AllocateTemporaryPlacementId()
        {
            for (int i = 0; i < 100000; i++)
            {
                if (nextTemporaryPlacementId < TemporaryPlacementIdMin)
                    nextTemporaryPlacementId = TemporaryPlacementIdMax;

                long candidate = nextTemporaryPlacementId--;
                bool registryHasCandidate = WGORegistry.Instance != null &&
                    WGORegistry.Instance.TryGet(candidate, out WorldGameObject _);
                if (!registryHasCandidate &&
                    WorldMap.GetWorldGameObjectByUniqueId(candidate, false) == null)
                {
                    return candidate;
                }
            }
            return 0L;
        }

        private static void RemoveWgoInstanceWithoutSync(WorldGameObject wgo)
        {
            if (wgo == null)
                return;

            long uniqueId = wgo.unique_id;
            if (uniqueId > 0L)
            {
                WGORegistry.Instance?.Unregister(uniqueId);
                WorldMap.OnDestroyWGO(wgo);
            }
            UnityEngine.Object.Destroy(wgo.gameObject);
        }

        private static long AllocateCanonicalUniqueId()
        {
            for (int i = 0; i < 4096; i++)
            {
                long candidate = UniqueID.GetUniqueID();
                if (candidate > 0L && !IsTemporaryPlacementId(candidate) &&
                    WorldMap.GetWorldGameObjectByUniqueId(candidate, false) == null)
                {
                    return candidate;
                }
            }
            return 0L;
        }

        private static bool TryRemapWgoUniqueId(
            WorldGameObject wgo,
            long newUniqueId)
        {
            if (wgo == null || newUniqueId <= 0L)
                return false;
            if (wgo.unique_id == newUniqueId)
                return true;

            WorldGameObject collision =
                WorldMap.GetWorldGameObjectByUniqueId(newUniqueId, false);
            if (collision != null && collision != wgo)
                return false;

            long oldUniqueId = wgo.unique_id;
            try
            {
                if (oldUniqueId > 0L)
                {
                    WGORegistry.Instance?.Unregister(oldUniqueId);
                    WorldMap.OnDestroyWGO(wgo);
                }

                wgo.unique_id = newUniqueId;
                WorldMap.OnAddNewWGO(wgo);
                WGORegistry.Instance?.Register(wgo);
                if (WorldMap.GetWorldGameObjectByUniqueId(
                        newUniqueId,
                        false) != wgo)
                {
                    throw new InvalidOperationException(
                        "WorldMap did not register the remapped WGO");
                }
                return true;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefixStatic} Failed to remap WGO ID " +
                    $"{oldUniqueId} -> {newUniqueId}: {ex.Message}");
                try
                {
                    if (wgo.unique_id == newUniqueId)
                    {
                        WGORegistry.Instance?.Unregister(newUniqueId);
                        WorldMap.OnDestroyWGO(wgo);
                    }
                    wgo.unique_id = oldUniqueId;
                    WorldMap.OnAddNewWGO(wgo);
                    WGORegistry.Instance?.Register(wgo);
                }
                catch { }
                return false;
            }
        }

        private static bool IsValidBuildingPlacement(SerializableWGO serialized)
        {
            if (string.IsNullOrEmpty(serialized.obj_id) || serialized.obj_id == "0" ||
                float.IsNaN(serialized.position.x) || float.IsInfinity(serialized.position.x) ||
                float.IsNaN(serialized.position.y) || float.IsInfinity(serialized.position.y) ||
                float.IsNaN(serialized.position.z) || float.IsInfinity(serialized.position.z))
            {
                return false;
            }

            ObjectDefinition definition =
                GameBalance.me?.GetDataOrNull<ObjectDefinition>(serialized.obj_id);
            if (definition == null)
                return false;

            try { return !definition.IsNPC(); }
            catch { return false; }
        }

        private static bool IsTemporaryPlacementId(long uniqueId)
        {
            return uniqueId >= TemporaryPlacementIdMin &&
                   uniqueId <= TemporaryPlacementIdMax;
        }

        private static string BuildPlacementKey(
            ulong originSteamId,
            long temporaryUniqueId)
        {
            return originSteamId.ToString() + ":" + temporaryUniqueId.ToString();
        }

        private static bool IsExpectedHost(CSteamID senderID)
        {
            CSteamID lobbyID = SteamLobbyManager.Instance?.CurrentLobbyID ?? CSteamID.Nil;
            return lobbyID != CSteamID.Nil &&
                   SteamMatchmaking.GetLobbyOwner(lobbyID) == senderID;
        }

        internal static bool PeerSupportsBuildingPreview(CSteamID peer)
        {
            CSteamID lobbyID =
                SteamLobbyManager.Instance?.CurrentLobbyID ?? CSteamID.Nil;
            if (peer == CSteamID.Nil || lobbyID == CSteamID.Nil)
                return false;

            string capability = SteamMatchmaking.GetLobbyMemberData(
                lobbyID,
                peer,
                BuildingPreviewCapabilityKey);
            return string.Equals(
                capability,
                BuildingPreviewCapabilityValue,
                StringComparison.Ordinal);
        }

        private void FinalizeRemoteWgoRestore(WorldGameObject target)
        {
            if (target == null)
                return;

            target.RefreshPositionCache();
            // RestoreFromSerializedObject stops short of WorldGameObject.OnJustSpawned,
            // which is where vanilla registers the object with its WorldZone. Without
            // this, client-placed graves exist visually on the host but are absent from
            // the graveyard's WGO list, so neither total quality nor the skull hint can
            // include them.
            target.RecalculateZoneBelonging();
            ChunkedGameObject chunk =
                target.GetComponentInChildren<ChunkedGameObject>(true);
            if (chunk != null)
            {
                chunk.destroyed = false;
                chunk.pending_to_remove = false;
                chunk.RecalculateChunk();
                ChunkManager.OnAddNewObject(chunk);

                WorldGameObject player = MainGame.me?.player;
                if (player != null &&
                    (player.transform.position - target.transform.position)
                    .sqrMagnitude <= 2048f * 2048f)
                {
                    chunk.obj_visible = true;
                    chunk.UpdateVisibility();
                }
            }

            try
            {
                target.Redraw(true);
                target.GetComponentInChildren<SmartDrawer>(true)?.Redraw(true);

                // Replacing an object changes the quality source (for example,
                // grave_empty -> grave_corp -> grave_ground), but a graphics redraw
                // does not rebuild BubbleWidgetData. Match the observing player's
                // current quality visibility, including a remote build presentation.
                PlayerComponent localPlayer = MainGame.me?.player_char?.player;
                WorldZone objectZone = target.GetMyWorldZone();
                bool showQuality = target.show_quality_hint;
                if (objectZone != null)
                {
                    if (objectZone == presentedRemoteQualityZone)
                        showQuality = true;
                    else if (localPlayer?.current_zone == objectZone)
                        showQuality = localPlayer.show_wgo_qualities;
                }
                target.SetQualityHint(showQuality);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefixStatic} Failed to redraw restored WGO " +
                    $"uid={target.unique_id}, obj_id={target.obj_id}: {ex.Message}");
            }
        }

        private const string LogPrefixStatic = "[SpawnSync]";

        private bool TrySerializeWgo(WorldGameObject wgo, out string json)
        {
            json = null;
            try
            {
                SerializableWGO serialized = SerializableWGO.FromWGO(wgo);
                json = JsonUtility.ToJson(serialized);
                int byteCount = string.IsNullOrEmpty(json) ? 0 : Encoding.UTF8.GetByteCount(json);
                if (byteCount <= 0 || byteCount > MaxSerializedWgoBytes)
                {
                    CoopMod.Logger.LogWarning($"{LogPrefix} Serialized WGO too large/empty: uid={wgo.unique_id}, obj_id={wgo.obj_id}, bytes={byteCount}");
                    json = null;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Failed to serialize WGO uid={wgo?.unique_id}, obj_id={wgo?.obj_id}: {ex.Message}");
                return false;
            }
        }

        private bool TryDeserializeWgo(byte[] jsonBytes, long expectedUid, string expectedObjId, out SerializableWGO serialized)
        {
            serialized = default(SerializableWGO);
            try
            {
                string json = Encoding.UTF8.GetString(jsonBytes);
                serialized = JsonUtility.FromJson<SerializableWGO>(json);
                if (serialized.unique_id != expectedUid ||
                    string.IsNullOrEmpty(serialized.obj_id) ||
                    !string.Equals(serialized.obj_id, expectedObjId, StringComparison.Ordinal))
                {
                    CoopMod.Logger.LogWarning(
                        $"{LogPrefix} Spawn state identity mismatch: packet uid={expectedUid}, obj_id={expectedObjId}, " +
                        $"state uid={serialized.unique_id}, obj_id={serialized.obj_id}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Failed to deserialize WGO uid={expectedUid}, obj_id={expectedObjId}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Identifies a visual-only projection of another player's build cursor.
    /// These objects are deliberately excluded from every authoritative WGO lane.
    /// </summary>
    internal sealed class RemoteBuildingPreviewMarker : MonoBehaviour
    {
        private readonly List<Vector2> footprintOffsets =
            new List<Vector2>();
        private readonly List<FlowGridCell> footprintCells =
            new List<FlowGridCell>();
        private GameObject footprintRoot;
        private GameObject dockMarkerRoot;
        private bool dockMarkersBuilt;
        private bool hasBuildableState;
        private bool lastBuildable;

        internal bool SetBuildable(bool canBuild)
        {
            if (hasBuildableState && lastBuildable == canBuild)
                return false;

            hasBuildableState = true;
            lastBuildable = canBuild;
            return true;
        }

        internal bool ApplyFootprint(
            List<Vector2> offsets,
            bool canBuild)
        {
            int count = offsets?.Count ?? 0;
            bool layoutChanged = footprintOffsets.Count != count;
            if (!layoutChanged)
            {
                for (int i = 0; i < count; i++)
                {
                    if ((footprintOffsets[i] - offsets[i]).sqrMagnitude >=
                        0.000001f)
                    {
                        layoutChanged = true;
                        break;
                    }
                }
            }

            if (layoutChanged)
            {
                ClearFootprint();
                if (count > 0)
                {
                    footprintRoot =
                        new GameObject("RemoteBuildFootprint");
                    footprintRoot.transform.SetParent(
                        transform,
                        false);
                    for (int i = 0; i < count; i++)
                    {
                        Vector2 offset = offsets[i];
                        FlowGridCell cell = FlowGridCell.Create(
                            footprintRoot.transform,
                            offset,
                            3,
                            FlowGridCell.CellType.UnderObject);
                        footprintOffsets.Add(offset);
                        footprintCells.Add(cell);
                    }
                }
            }

            for (int i = 0; i < footprintCells.Count; i++)
            {
                FlowGridCell cell = footprintCells[i];
                if (cell != null)
                    cell.SetRedColorState(!canBuild);
            }
            return layoutChanged;
        }

        internal bool ApplyDockMarkers(
            WorldGameObject preview,
            bool forceRebuild)
        {
            if (preview == null || MainGame.me?.dock_point_marker == null)
                return false;
            if (dockMarkersBuilt && !forceRebuild)
                return false;

            ClearDockMarkers();
            dockMarkersBuilt = true;
            DockPoint[] dockPoints =
                preview.GetComponentsInChildren<DockPoint>();
            if (dockPoints.Length == 0)
                return true;

            dockMarkerRoot = new GameObject("RemoteBuildDockMarkers");
            dockMarkerRoot.transform.SetParent(transform, false);
            for (int i = 0; i < dockPoints.Length; i++)
            {
                DockPoint dockPoint = dockPoints[i];
                if (dockPoint == null)
                    continue;

                GameObject prefab;
                try
                {
                    prefab = MainGame.me.dock_point_marker.GetMarker(
                        dockPoint.GetActionDir());
                }
                catch (ArgumentOutOfRangeException)
                {
                    continue;
                }

                if (prefab == null)
                    continue;

                GameObject dockMarker = Instantiate(prefab);
                dockMarker.transform.SetParent(
                    dockMarkerRoot.transform,
                    false);
                dockMarker.transform.position =
                    dockPoint.transform.position;
            }
            return true;
        }

        private void OnDestroy()
        {
            ClearFootprint();
            ClearDockMarkers();
        }

        private void ClearFootprint()
        {
            footprintOffsets.Clear();
            footprintCells.Clear();
            if (footprintRoot == null)
                return;

            footprintRoot.SetActive(false);
            Destroy(footprintRoot);
            footprintRoot = null;
        }

        private void ClearDockMarkers()
        {
            if (dockMarkerRoot == null)
                return;

            dockMarkerRoot.SetActive(false);
            Destroy(dockMarkerRoot);
            dockMarkerRoot = null;
        }
    }
}

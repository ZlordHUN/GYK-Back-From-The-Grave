using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using HarmonyLib;
using FlowCanvas.Nodes;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;
using GraveyardKeeperCoop.Utils;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Host-driven visual correction for NPCs. During a client-triggered scripted
    /// cutscene, that initiator temporarily becomes the visual source for the named
    /// cutscene actors so every peer can observe the same performance without running
    /// a second player-bound FlowScript.
    /// </summary>
    public class NpcVisualSync : SyncBehaviour
    {
        public static NpcVisualSync Instance => GetInstance<NpcVisualSync>();

        private sealed class SourceState
        {
            public VisualSyncHelpers.VisualHierarchyMap Map;
            public readonly Dictionary<uint, string> LastSpriteSnapshot = new Dictionary<uint, string>();
            public readonly Dictionary<uint, string> LastTransformActiveSnapshot =
                new Dictionary<uint, string>();
            public Transform CharacterTransform;
            public Animator Animator;
            public Vector3 LastPosition;
            public float LastScaleX = 1f;
            public byte LastAnimDirection = byte.MaxValue;
            public string LastSkinId = string.Empty;
            public int LastAnimatorStateHash;
            public string LastAnimatorFingerprint = string.Empty;
            public bool LastCorpseDetached;
            public Vector3 LastVelocityPosition;
            public float LastVelocitySampleAt;
        }

        private struct PositionSample
        {
            public float SenderTime;
            public Vector3 Position;
            public Vector2 Velocity;
        }

        private sealed class TargetState
        {
            public WorldGameObject Wgo;
            public VisualSyncHelpers.VisualHierarchyMap Map;
            public readonly Dictionary<uint, string> LatestSpriteSnapshot =
                new Dictionary<uint, string>();
            public readonly Dictionary<uint, string> LatestTransformActiveSnapshot =
                new Dictionary<uint, string>();
            public ChunkedGameObject Chunk;
            public Transform CharacterTransform;
            public Animator Animator;
            public readonly List<PositionSample> PositionSamples =
                new List<PositionSample>(MaxPositionSamples);
            public NpcEntry LatestEntry;
            public Vector3 LastReceivedPosition;
            public bool HasReceivedPosition;
            public uint LastSequence;
            public bool HasSequence;
            public float SenderTimeOffset;
            public bool HasSenderTimeOffset;
            public uint PuppetEpoch;
        }

        private const byte PayloadVersion = 7;
        private const float BroadcastIntervalSeconds = 0.08f;
        private const float FullResendIntervalSeconds = 5f;
        private const float PositionThreshold = 0.25f;
        private const float PlaybackDelaySeconds = 0.10f;
        private const float MaxExtrapolationSeconds = 0.15f;
        private const float MaxExtrapolationSpeed = 480f;
        private const float SnapDistance = 192f;
        private const float AnimatorPhaseCorrectionThreshold = 0.20f;
        private const float MovingVelocityThreshold = 1f;
        private const int MaxPositionSamples = 24;
        private const int MaxNpcsPerPacket = 48;
        private const int MaxObjectsScannedPerBroadcast = 768;
        private const int MaxSpriteDeltasPerNpc = 96;
        private const int MaxTransformActiveDeltasPerNpc = 256;
        private const int MaxAnimatorParametersPerNpc = 64;
        private const int MaxPayloadBytes = 192 * 1024;
        private const float DiagnosticReportIntervalSeconds = 5f;
        // Steam P2P's unreliable packet limit is ~1100 bytes. Larger sends fall
        // back to reliable delivery, which is rate-limited and congests control
        // when many NPC deltas pile up. Chunk each broadcast into sub-packets
        // that stay safely under the unreliable limit.
        private const int TargetUnreliablePayloadBytes = 1000;

        private readonly Dictionary<long, SourceState> sourceStates = new Dictionary<long, SourceState>();
        private readonly Dictionary<long, TargetState> targetStates = new Dictionary<long, TargetState>();
        private readonly Dictionary<int, WorldGameObject> localCutsceneActors =
            new Dictionary<int, WorldGameObject>();
        private readonly HashSet<long> hostEpochActors = new HashSet<long>();
        private readonly HashSet<long> pendingFinalPoseActors = new HashSet<long>();
        private Dictionary<string, Sprite> spriteLibrary;

        private float nextBroadcastAt;
        private float nextFullResendAt;
        private int sourceScanIndex;
        private uint nextPuppetEpoch;
        private uint activePuppetEpoch;
        private bool hostPuppetWindowActive;
        private uint receivedPuppetEpoch;
        private uint lastEpochSequence;
        private bool hasEpochSequence;
        private CSteamID receivedVisualSource = CSteamID.Nil;
        private float nextDiagnosticReportAt;
        private long diagnosticCaptureTicks;
        private long diagnosticPackSerializeTicks;
        private long diagnosticSendTicks;
        private long diagnosticTotalTicks;
        private long diagnosticObjectsScanned;
        private long diagnosticNpcCandidates;
        private long diagnosticEntriesEmitted;
        private long diagnosticChunksSent;
        private long diagnosticBytesSent;
        private int diagnosticBroadcasts;
        private int diagnosticFullBroadcasts;
        private int diagnosticCutsceneBroadcasts;
        private int diagnosticMaxSourceObjects;
        private double diagnosticMaxCaptureMilliseconds;

        protected override string LogPrefix => "[NpcVisualSync]";

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnNpcVisualSyncReceived -= OnNpcVisualSyncReceived;
                SteamP2PManager.Instance.OnNpcVisualSyncReceived += OnNpcVisualSyncReceived;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnNpcVisualSyncReceived -= OnNpcVisualSyncReceived;
            }
        }

        protected override void OnSyncEnabled()
        {
            if (ModConfig.EnableNpcVisualSync != null && !ModConfig.EnableNpcVisualSync.Value)
            {
                CoopMod.Logger.LogInfo($"{LogPrefix} Disabled by config");
                return;
            }

            nextBroadcastAt = 0f;
            nextFullResendAt = 0f;
            ResetState();
        }

        protected override void OnSyncDisabled()
        {
            ResetState();
        }

        private void ResetState()
        {
            sourceStates.Clear();
            targetStates.Clear();
            sourceScanIndex = 0;
            nextPuppetEpoch = 0U;
            activePuppetEpoch = 0U;
            hostPuppetWindowActive = false;
            receivedPuppetEpoch = 0U;
            lastEpochSequence = 0U;
            hasEpochSequence = false;
            receivedVisualSource = CSteamID.Nil;
            localCutsceneActors.Clear();
            hostEpochActors.Clear();
            pendingFinalPoseActors.Clear();
            ResetDiagnostics();
        }

        private void Update()
        {
            if (!IsSyncEnabled || !IsSessionActive()) return;

            bool remoteCutsceneSourceActive =
                HasRemoteNpcVisualAuthority();
            bool localCutsceneSourceActive =
                !IsHost &&
                HasLocalNpcVisualAuthority();
            bool mustCloseLocalCutsceneEpoch =
                !IsHost && hostPuppetWindowActive;
            bool shouldBroadcast =
                (IsHost && !remoteCutsceneSourceActive) ||
                localCutsceneSourceActive ||
                mustCloseLocalCutsceneEpoch;

            if (shouldBroadcast &&
                Time.realtimeSinceStartup >= nextBroadcastAt)
            {
                bool epochChanged = RefreshHostPuppetEpoch();
                nextBroadcastAt = Time.realtimeSinceStartup + BroadcastIntervalSeconds;
                BroadcastHostNpcState(
                    epochChanged,
                    cutsceneActorsOnly: !IsHost || hostPuppetWindowActive);
            }
        }

        private void LateUpdate()
        {
            bool canReceiveRemoteSource =
                !IsHost ||
                HasRemoteNpcVisualAuthority();
            if (!IsSyncEnabled || !IsSessionActive() ||
                !canReceiveRemoteSource || targetStates.Count == 0)
            {
                return;
            }

            bool puppetWindowActive = IsClientPuppetWindowActive();
            bool localCutsceneSimulationOwnsMovement =
                GameLoadSync.Instance?.IsInIntroPhase == true ||
                HasLocalNpcVisualAuthority() ||
                Patches.CutsceneSyncPatches.HasActiveMirroredLocalCutscene() ||
                Patches.NpcInteractionSyncPatches.HasActiveMirroredLocalInteraction();
            if (!puppetWindowActive && HasNetworkPuppets())
            {
                ReleaseNetworkPuppets(
                    "observed cutscene ended",
                    clearReceivedEpoch: false);
            }

            float snapSqr = SnapDistance * SnapDistance;
            var stale = new List<long>();

            foreach (var pair in targetStates)
            {
                TargetState state = pair.Value;
                if (state == null || state.Wgo == null || state.Wgo.transform == null)
                {
                    stale.Add(pair.Key);
                    continue;
                }

                if (!TryEvaluatePosition(state, out Vector3 target))
                {
                    continue;
                }

                Vector3 current = state.Wgo.transform.position;
                target.z = current.z;
                if (localCutsceneSimulationOwnsMovement)
                {
                    continue;
                }

                bool snap = (current - target).sqrMagnitude > snapSqr;
                if (snap && state.PositionSamples.Count > 1)
                {
                    PositionSample newest =
                        state.PositionSamples[state.PositionSamples.Count - 1];
                    state.PositionSamples.Clear();
                    state.PositionSamples.Add(newest);
                    target = newest.Position;
                    target.z = current.z;
                }

                bool ownsMovement =
                    puppetWindowActive &&
                    state.PuppetEpoch != 0U &&
                    state.PuppetEpoch == receivedPuppetEpoch;
                if ((current - target).sqrMagnitude > 0.0001f)
                {
                    ApplyNetworkPosition(
                        state.Wgo,
                        target,
                        ownsMovement,
                        state.Chunk);
                }
                if (ownsMovement && state.LatestEntry != null)
                {
                    byte direction = state.LatestEntry.AnimDirection;
                    if (direction >= (byte)Direction.Right &&
                        direction <= (byte)Direction.Down &&
                        (byte)state.Wgo.components.character.anim_direction !=
                        direction)
                    {
                        state.Wgo.components.character.LookAt(
                            (Direction)direction);
                    }
                    ApplyAnimatorState(
                        state.Wgo,
                        state.Animator,
                        state.LatestEntry,
                        allowPhaseCorrection: false,
                        applyParameters: false);
                }
            }

            for (int i = 0; i < stale.Count; i++)
                targetStates.Remove(stale[i]);
        }

        private bool IsSessionActive() => IsOnline && MainGame.me != null && MainGame.game_started;

        private bool RefreshHostPuppetEpoch()
        {
            bool windowActive =
                Patches.CutsceneSyncPatches.IsAuthoritativeNpcAnimationWindowActive() ||
                Patches.NpcInteractionSyncPatches.IsAuthoritativeNpcAnimationWindowActive();
            if (windowActive == hostPuppetWindowActive)
            {
                return false;
            }

            hostPuppetWindowActive = windowActive;
            if (windowActive)
            {
                hostEpochActors.Clear();
                nextPuppetEpoch++;
                if (nextPuppetEpoch == 0U)
                    nextPuppetEpoch = 1U;
                activePuppetEpoch = nextPuppetEpoch;
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Started cutscene motion epoch {activePuppetEpoch}");
            }
            else
            {
                foreach (long uniqueId in hostEpochActors)
                    pendingFinalPoseActors.Add(uniqueId);
                hostEpochActors.Clear();
                localCutsceneActors.Clear();
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Ended cutscene motion epoch {activePuppetEpoch}");
                activePuppetEpoch = 0U;
            }

            return true;
        }

        private static bool IsClientPuppetWindowActive()
        {
            return Patches.CutsceneSyncPatches.IsNpcNetworkPuppetWindowActive() ||
                   Patches.NpcInteractionSyncPatches.IsNpcNetworkPuppetWindowActive();
        }

        private static bool HasRemoteNpcVisualAuthority()
        {
            return Patches.CutsceneSyncPatches.HasRemoteNpcVisualAuthority() ||
                   Patches.NpcInteractionSyncPatches.HasRemoteNpcVisualAuthority();
        }

        private static bool IsRemoteNpcVisualAuthority(CSteamID senderID)
        {
            return Patches.CutsceneSyncPatches.IsRemoteNpcVisualAuthority(senderID) ||
                   Patches.NpcInteractionSyncPatches.IsRemoteNpcVisualAuthority(senderID);
        }

        private static bool HasLocalNpcVisualAuthority()
        {
            return Patches.CutsceneSyncPatches.HasLocalNpcVisualAuthority() ||
                   Patches.NpcInteractionSyncPatches.HasLocalNpcVisualAuthority();
        }

        private static bool IsLocalCutsceneNpcActor(WorldGameObject wgo)
        {
            return (Instance?.IsRegisteredLocalCutsceneActor(wgo) ?? false) ||
                   Patches.CutsceneSyncPatches.IsLocalCutsceneNpcActor(wgo) ||
                   Patches.NpcInteractionSyncPatches.IsLocalCutsceneNpcActor(wgo);
        }

        internal void BeginLocalCutsceneActorSession(
            WorldGameObject seedActor,
            string reason)
        {
            bool alreadyActive =
                Patches.CutsceneSyncPatches.IsAuthoritativeNpcAnimationWindowActive() ||
                Patches.NpcInteractionSyncPatches.IsAuthoritativeNpcAnimationWindowActive();
            if (!alreadyActive && !hostPuppetWindowActive)
                localCutsceneActors.Clear();

            RegisterLocalCutsceneActorInternal(seedActor, reason);
        }

        internal void RegisterLocalCutsceneActor(
            WorldGameObject actor,
            string reason)
        {
            bool authorityActive =
                Patches.CutsceneSyncPatches.IsAuthoritativeNpcAnimationWindowActive() ||
                Patches.NpcInteractionSyncPatches.IsAuthoritativeNpcAnimationWindowActive();
            if (!authorityActive)
                return;

            RegisterLocalCutsceneActorInternal(actor, reason);
        }

        internal bool AreLocalCutsceneActorsMoving()
        {
            foreach (WorldGameObject actor in localCutsceneActors.Values)
            {
                if (!ShouldSyncNpc(actor))
                    continue;

                try
                {
                    MovementComponent movement = actor.components?.character;
                    if (movement != null &&
                        !movement.IsStopped &&
                        movement.movement_state !=
                        MovementComponent.MovementState.None)
                    {
                        return true;
                    }

                    Patches.CutsceneNpcPathRecovery pathRecovery =
                        actor.GetComponent<Patches.CutsceneNpcPathRecovery>();
                    if (pathRecovery?.IsActive == true)
                        return true;

                    Rigidbody2D body = actor.GetComponent<Rigidbody2D>() ??
                                       actor.GetComponentInChildren<Rigidbody2D>(
                                           true);
                    if (body != null && body.velocity.sqrMagnitude > 1f)
                        return true;
                }
                catch
                {
                    // A despawning actor is settled from the visual epoch's point of view.
                }
            }

            return false;
        }

        private void RegisterLocalCutsceneActorInternal(
            WorldGameObject actor,
            string reason)
        {
            if (!ShouldSyncNpc(actor))
                return;

            int instanceId = actor.GetInstanceID();
            if (localCutsceneActors.TryGetValue(
                    instanceId,
                    out WorldGameObject existing) &&
                existing == actor)
            {
                return;
            }

            localCutsceneActors[instanceId] = actor;
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Enrolled cutscene actor " +
                $"'{actor.obj_id}'/'{actor.custom_tag}' ({reason})");
        }

        private bool IsRegisteredLocalCutsceneActor(WorldGameObject actor)
        {
            if (actor == null)
                return false;

            return localCutsceneActors.TryGetValue(
                       actor.GetInstanceID(),
                       out WorldGameObject registered) &&
                   registered == actor;
        }

        internal bool IsRegisteredLocalCutsceneActorForPathRecovery(
            WorldGameObject actor)
        {
            return IsRegisteredLocalCutsceneActor(actor);
        }

        internal bool OwnsNetworkMovement(WorldGameObject wgo)
        {
            if (!IsSyncEnabled || wgo == null || wgo.unique_id == 0L ||
                !IsClientPuppetWindowActive())
            {
                return false;
            }

            return targetStates.TryGetValue(wgo.unique_id, out TargetState state) &&
                   state != null &&
                   state.Wgo == wgo &&
                   state.PuppetEpoch != 0U &&
                   state.PuppetEpoch == receivedPuppetEpoch;
        }

        private bool HasNetworkPuppets()
        {
            foreach (TargetState state in targetStates.Values)
            {
                if (state?.PuppetEpoch != 0U)
                    return true;
            }

            return false;
        }

        private void ReleaseNetworkPuppets(
            string reason,
            bool clearReceivedEpoch = true)
        {
            int released = 0;
            foreach (TargetState state in targetStates.Values)
            {
                if (state == null || state.PuppetEpoch == 0U)
                    continue;

                if (state.Wgo != null && state.PositionSamples.Count > 0)
                {
                    PositionSample latest =
                        state.PositionSamples[state.PositionSamples.Count - 1];
                    ApplyNetworkPosition(
                        state.Wgo,
                        latest.Position,
                        ownsMovement: false,
                        chunk: state.Chunk);
                }

                state.PuppetEpoch = 0U;
                released++;
            }

            if (clearReceivedEpoch)
                receivedPuppetEpoch = 0U;
            if (released > 0)
            {
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Released {released} cutscene NPC puppet(s) ({reason})");
            }
        }

        private static bool TryEvaluatePosition(TargetState state, out Vector3 position)
        {
            position = Vector3.zero;
            if (state == null || state.PositionSamples.Count == 0 ||
                !state.HasSenderTimeOffset)
            {
                return false;
            }

            float playbackTime =
                Time.realtimeSinceStartup +
                state.SenderTimeOffset -
                PlaybackDelaySeconds;
            while (state.PositionSamples.Count >= 2 &&
                   state.PositionSamples[1].SenderTime <= playbackTime)
            {
                state.PositionSamples.RemoveAt(0);
            }

            PositionSample first = state.PositionSamples[0];
            if (state.PositionSamples.Count >= 2)
            {
                PositionSample second = state.PositionSamples[1];
                float span = Mathf.Max(
                    0.0001f,
                    second.SenderTime - first.SenderTime);
                float t = Mathf.Clamp01(
                    (playbackTime - first.SenderTime) / span);
                position = HermitePosition(first, second, span, t);
                return true;
            }

            float extrapolation = Mathf.Clamp(
                playbackTime - first.SenderTime,
                0f,
                MaxExtrapolationSeconds);
            Vector2 velocity = Vector2.ClampMagnitude(
                first.Velocity,
                MaxExtrapolationSpeed);
            position = first.Position +
                       new Vector3(
                           velocity.x * extrapolation,
                           velocity.y * extrapolation,
                           0f);
            return true;
        }

        private static Vector3 HermitePosition(
            PositionSample first,
            PositionSample second,
            float span,
            float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;
            Vector3 firstTangent = new Vector3(
                first.Velocity.x * span,
                first.Velocity.y * span,
                0f);
            Vector3 secondTangent = new Vector3(
                second.Velocity.x * span,
                second.Velocity.y * span,
                0f);
            return h00 * first.Position +
                   h10 * firstTangent +
                   h01 * second.Position +
                   h11 * secondTangent;
        }

        private static void ApplyNetworkPosition(
            WorldGameObject wgo,
            Vector3 position,
            bool ownsMovement,
            ChunkedGameObject chunk = null)
        {
            if (wgo == null)
                return;

            try { wgo.PlaceAtPos(position); }
            catch
            {
                if (wgo.transform != null)
                    wgo.transform.position = position;
            }

            if (ownsMovement)
            {
                Rigidbody2D[] bodies =
                    wgo.GetComponentsInChildren<Rigidbody2D>(true);
                for (int i = 0; i < bodies.Length; i++)
                {
                    Rigidbody2D body = bodies[i];
                    if (body == null)
                        continue;

                    body.position = new Vector2(position.x, position.y);
                    body.velocity = Vector2.zero;
                    body.angularVelocity = 0f;
                }
            }

            wgo.RefreshPositionCache();
            wgo.round_and_sort?.MarkPositionDirty();

            // Stock/scheduled NPCs are commonly inactive while parked at an off-map
            // GD point. An inactive ChunkedGameObject does not run Update(), so merely
            // moving its transform leaves its chunk coordinates at the old stock point
            // and the local ChunkManager never makes it visible again. Recalculate only
            // when the authoritative move crosses a chunk boundary; ChunkManager still
            // decides visibility from this peer's own camera.
            if (chunk != null)
            {
                int targetChunkX = Mathf.RoundToInt(position.x / 96f);
                int targetChunkY = Mathf.RoundToInt(position.y / 96f);
                if (chunk.chunk_x != targetChunkX ||
                    chunk.chunk_y != targetChunkY)
                {
                    chunk.RecalculateChunk();
                }
            }
        }

        private void BroadcastHostNpcState(
            bool epochChanged,
            bool cutsceneActorsOnly = false)
        {
            bool profiling = FrameProfiler.Enabled;
            long broadcastStartedAt = profiling
                ? System.Diagnostics.Stopwatch.GetTimestamp()
                : 0L;
            float senderTime = Time.realtimeSinceStartup;
            bool fullSend = epochChanged || senderTime >= nextFullResendAt;
            if (fullSend)
                nextFullResendAt = senderTime + FullResendIntervalSeconds;

            long captureStartedAt = profiling
                ? System.Diagnostics.Stopwatch.GetTimestamp()
                : 0L;
            List<NpcEntry> entries = CaptureHostEntries(
                fullSend,
                cutsceneActorsOnly,
                out int sourceObjectCount,
                out int scannedObjectCount,
                out int npcCandidateCount);
            long captureTicks = profiling
                ? System.Diagnostics.Stopwatch.GetTimestamp() - captureStartedAt
                : 0L;
            AppendPendingFinalPoseEntries(entries);
            if (entries.Count == 0)
            {
                RecordDiagnostics(
                    fullSend,
                    cutsceneActorsOnly,
                    sourceObjectCount,
                    scannedObjectCount,
                    npcCandidateCount,
                    0,
                    0,
                    0,
                    captureTicks,
                    0L,
                    0L,
                    profiling
                        ? System.Diagnostics.Stopwatch.GetTimestamp() - broadcastStartedAt
                        : 0L);
                return;
            }

            // Greedily pack entries into sub-packets that stay under Steam's
            // unreliable limit. Each chunk is sent as its own unreliable packet
            // so we avoid the reliable-channel fallback for the common case.
            int index = 0;
            int chunkCount = 0;
            int payloadBytes = 0;
            long packSerializeTicks = 0L;
            long sendTicks = 0L;
            while (index < entries.Count)
            {
                long packStartedAt = profiling
                    ? System.Diagnostics.Stopwatch.GetTimestamp()
                    : 0L;
                List<NpcEntry> chunk = TakeChunkUnderBudget(entries, ref index, TargetUnreliablePayloadBytes);
                if (chunk.Count == 0)
                {
                    if (profiling)
                    {
                        packSerializeTicks +=
                            System.Diagnostics.Stopwatch.GetTimestamp() - packStartedAt;
                    }
                    if (index < entries.Count) index++; // skip an entry that alone exceeds the budget
                    continue;
                }

                byte[] payload = SerializePayload(
                    ++NextSequence,
                    senderTime,
                    activePuppetEpoch,
                    chunk);
                if (profiling)
                {
                    packSerializeTicks +=
                        System.Diagnostics.Stopwatch.GetTimestamp() - packStartedAt;
                }
                if (payload.Length > MaxPayloadBytes)
                {
                    CoopMod.Logger.LogWarning($"{LogPrefix} Payload too large ({payload.Length}); skipping");
                    NetworkDiagnostics.RecordError("NpcVisualSync", $"Payload too large: {payload.Length}");
                    continue;
                }

                long sendStartedAt = profiling
                    ? System.Diagnostics.Stopwatch.GetTimestamp()
                    : 0L;
                SteamP2PManager.Instance?.BroadcastNpcVisualSync(payload);
                if (profiling)
                {
                    sendTicks +=
                        System.Diagnostics.Stopwatch.GetTimestamp() - sendStartedAt;
                }
                chunkCount++;
                payloadBytes += payload.Length;
            }

            RecordDiagnostics(
                fullSend,
                cutsceneActorsOnly,
                sourceObjectCount,
                scannedObjectCount,
                npcCandidateCount,
                entries.Count,
                chunkCount,
                payloadBytes,
                captureTicks,
                packSerializeTicks,
                sendTicks,
                profiling
                    ? System.Diagnostics.Stopwatch.GetTimestamp() - broadcastStartedAt
                    : 0L);
        }

        private void RecordDiagnostics(
            bool fullSend,
            bool cutsceneActorsOnly,
            int sourceObjectCount,
            int scannedObjectCount,
            int npcCandidateCount,
            int entryCount,
            int chunkCount,
            int payloadBytes,
            long captureTicks,
            long packSerializeTicks,
            long sendTicks,
            long totalTicks)
        {
            if (!FrameProfiler.Enabled)
                return;

            FrameProfiler.Record("NVS.Capture", captureTicks);
            FrameProfiler.Record("NVS.PackSerialize", packSerializeTicks);
            FrameProfiler.Record("NVS.Send", sendTicks);

            diagnosticBroadcasts++;
            if (fullSend) diagnosticFullBroadcasts++;
            if (cutsceneActorsOnly) diagnosticCutsceneBroadcasts++;
            diagnosticMaxSourceObjects = Math.Max(
                diagnosticMaxSourceObjects,
                sourceObjectCount);
            diagnosticObjectsScanned += scannedObjectCount;
            diagnosticNpcCandidates += npcCandidateCount;
            diagnosticEntriesEmitted += entryCount;
            diagnosticChunksSent += chunkCount;
            diagnosticBytesSent += payloadBytes;
            diagnosticCaptureTicks += captureTicks;
            diagnosticPackSerializeTicks += packSerializeTicks;
            diagnosticSendTicks += sendTicks;
            diagnosticTotalTicks += totalTicks;
            diagnosticMaxCaptureMilliseconds = Math.Max(
                diagnosticMaxCaptureMilliseconds,
                TicksToMilliseconds(captureTicks));

            float now = Time.realtimeSinceStartup;
            if (nextDiagnosticReportAt <= 0f)
                nextDiagnosticReportAt = now + DiagnosticReportIntervalSeconds;
            if (now < nextDiagnosticReportAt)
                return;

            CoopMod.Logger.LogWarning(
                $"[PERF][NpcVisualSync] broadcasts={diagnosticBroadcasts} " +
                $"full={diagnosticFullBroadcasts} cutscene_only={diagnosticCutsceneBroadcasts} " +
                $"source_objects_max={diagnosticMaxSourceObjects} scanned={diagnosticObjectsScanned} " +
                $"npc_candidates={diagnosticNpcCandidates} entries={diagnosticEntriesEmitted} " +
                $"chunks={diagnosticChunksSent} bytes={diagnosticBytesSent} " +
                $"capture={TicksToMilliseconds(diagnosticCaptureTicks):F1}ms " +
                $"capture_max={diagnosticMaxCaptureMilliseconds:F1}ms " +
                $"pack_serialize={TicksToMilliseconds(diagnosticPackSerializeTicks):F1}ms " +
                $"send={TicksToMilliseconds(diagnosticSendTicks):F1}ms " +
                $"total={TicksToMilliseconds(diagnosticTotalTicks):F1}ms");
            ResetDiagnostics();
            nextDiagnosticReportAt = now + DiagnosticReportIntervalSeconds;
        }

        private void ResetDiagnostics()
        {
            nextDiagnosticReportAt = 0f;
            diagnosticCaptureTicks = 0L;
            diagnosticPackSerializeTicks = 0L;
            diagnosticSendTicks = 0L;
            diagnosticTotalTicks = 0L;
            diagnosticObjectsScanned = 0L;
            diagnosticNpcCandidates = 0L;
            diagnosticEntriesEmitted = 0L;
            diagnosticChunksSent = 0L;
            diagnosticBytesSent = 0L;
            diagnosticBroadcasts = 0;
            diagnosticFullBroadcasts = 0;
            diagnosticCutsceneBroadcasts = 0;
            diagnosticMaxSourceObjects = 0;
            diagnosticMaxCaptureMilliseconds = 0.0;
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        internal void BroadcastAuthoritativeStateNow(
            WorldGameObject wgo,
            bool reliable = false)
        {
            bool ownsVisualSource =
                (IsHost && !HasRemoteNpcVisualAuthority()) ||
                (!IsHost && HasLocalNpcVisualAuthority());
            if (!IsSyncEnabled ||
                !IsSessionActive() ||
                !ownsVisualSource ||
                !ShouldSyncNpc(wgo) ||
                wgo.unique_id == 0L)
            {
                return;
            }

            SourceState state = GetSourceState(wgo.unique_id, wgo);
            NpcEntry entry = CaptureEntry(wgo, state, fullSend: true);
            if (entry == null)
                return;

            byte[] payload = SerializePayload(
                ++NextSequence,
                Time.realtimeSinceStartup,
                activePuppetEpoch,
                new List<NpcEntry> { entry });
            if (payload.Length <= MaxPayloadBytes)
            {
                SteamP2PManager.Instance?.BroadcastNpcVisualSync(
                    payload,
                    reliable: reliable);
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Broadcast immediate authoritative state for " +
                    $"{wgo.obj_id} (uid={wgo.unique_id}, animator_state={entry.AnimatorStateHash}, " +
                    $"skin='{entry.SkinId}', " +
                    $"transforms={entry.TransformActiveDeltas.Count}, sprites={entry.SpriteDeltas.Count}, " +
                    $"corpse_detached={entry.CorpseDetached}, reliable={reliable})");
            }
        }

        internal void AcceptCanonicalPlacement(WorldGameObject wgo, Vector3 position)
        {
            if (wgo == null || IsHost)
                return;

            if (targetStates.TryGetValue(wgo.unique_id, out TargetState state) &&
                state != null)
            {
                state.Wgo = wgo;
                state.PositionSamples.Clear();
                state.LastReceivedPosition = position;
                state.HasReceivedPosition = true;
                ApplyNetworkPosition(
                    wgo,
                    position,
                    ownsMovement: false,
                    chunk: state.Chunk);
            }
        }

        /// <summary>
        /// Build the largest chunk of entries starting at <paramref name="index"/>
        /// whose serialized form is expected to stay under <paramref name="byteBudget"/>.
        /// Uses a per-entry cost estimate so we don't have to serialize trial
        /// payloads on every iteration.
        /// </summary>
        private static List<NpcEntry> TakeChunkUnderBudget(List<NpcEntry> entries, ref int index, int byteBudget)
        {
            var chunk = new List<NpcEntry>();
            int estimatedBytes = 24; // payload header (version + sequence + time + epoch + count)
            while (index < entries.Count && chunk.Count < MaxNpcsPerPacket)
            {
                NpcEntry entry = entries[index];
                int entryCost = EstimateEntryBytes(entry);
                if (chunk.Count > 0 && estimatedBytes + entryCost > byteBudget)
                    break;

                chunk.Add(entry);
                estimatedBytes += entryCost;
                index++;
            }

            return chunk;
        }

        private static int EstimateEntryBytes(NpcEntry entry)
        {
            // Fixed fields: long UniqueId (8) + position/velocity floats (20) +
            // float CharacterScaleX (4) + byte AnimDirection (1) +
            // animator state (9) + corpse-detached flag (1) + three ushort counts (6) +
            // ObjId string (1-4 length prefix + UTF-8 bytes) +
            // CustomTag/SkinId strings (1-4 length prefix + UTF-8 bytes).
            int cost = 49;
            cost += EncodingByteCount(entry?.ObjId);
            cost += EncodingByteCount(entry?.CustomTag);
            cost += EncodingByteCount(entry?.SkinId);

            if (entry?.AnimatorParameters != null)
            {
                for (int i = 0; i < entry.AnimatorParameters.Count; i++)
                    cost += 6 + EncodingByteCount(entry.AnimatorParameters[i].Name);
            }

            if (entry?.TransformActiveDeltas != null)
            {
                for (int i = 0;
                     i < entry.TransformActiveDeltas.Count;
                     i++)
                {
                    cost += EncodingByteCount(
                        entry.TransformActiveDeltas[i]);
                }
            }

            if (entry?.SpriteDeltas != null)
            {
                for (int i = 0; i < entry.SpriteDeltas.Count; i++)
                    cost += EncodingByteCount(entry.SpriteDeltas[i]);
            }

            return cost;
        }

        private static int EncodingByteCount(string value)
        {
            // BinaryWriter.Write(string) prefix length (1-4 bytes) + UTF-8 byte count.
            if (string.IsNullOrEmpty(value))
                return 1;
            int byteCount = System.Text.Encoding.UTF8.GetByteCount(value);
            return byteCount + (byteCount < 128 ? 1 : (byteCount < 16384 ? 2 : 4));
        }

        private List<NpcEntry> CaptureHostEntries(
            bool fullSend,
            bool cutsceneActorsOnly,
            out int sourceObjectCount,
            out int scannedObjectCount,
            out int npcCandidateCount)
        {
            sourceObjectCount = 0;
            scannedObjectCount = 0;
            npcCandidateCount = 0;
            var result = new List<NpcEntry>(MaxNpcsPerPacket);
            List<WorldGameObject> objects = cutsceneActorsOnly
                ? SnapshotLocalCutsceneActors()
                : WGORegistry.Instance?.SnapshotAll() ??
                  MainGame.me?.GetListOfWorldObjects();
            if (objects == null) return result;

            int objectCount = objects.Count;
            sourceObjectCount = objectCount;
            if (objectCount == 0) return result;

            if (sourceScanIndex < 0 || sourceScanIndex >= objectCount)
                sourceScanIndex = 0;

            int scanned = 0;
            while (scanned < objectCount &&
                   scanned < MaxObjectsScannedPerBroadcast &&
                   result.Count < MaxNpcsPerPacket)
            {
                int index = (sourceScanIndex + scanned) % objectCount;
                scanned++;

                WorldGameObject wgo = objects[index];
                if (!ShouldSyncNpc(wgo)) continue;
                if (cutsceneActorsOnly &&
                    !IsLocalCutsceneNpcActor(wgo))
                {
                    continue;
                }
                npcCandidateCount++;

                long uid = wgo.unique_id;
                if (uid == 0L) continue;

                SourceState state = GetSourceState(uid, wgo);
                if (state == null) continue;

                Vector3 position = wgo.transform.position;
                Vector2 velocity = CaptureVelocity(
                    state,
                    position,
                    Time.realtimeSinceStartup);
                float scaleX = state.CharacterTransform != null ? state.CharacterTransform.localScale.x : 1f;
                byte animDirection = (byte)wgo.components.character.anim_direction;
                string skinId = wgo.wop?.skin_id ?? string.Empty;
                bool positionChanged = (position - state.LastPosition).sqrMagnitude > PositionThreshold * PositionThreshold;
                bool scaleChanged = Mathf.Abs(scaleX - state.LastScaleX) > 0.01f;
                bool directionChanged = animDirection != state.LastAnimDirection;
                bool skinChanged = !string.Equals(
                    skinId,
                    state.LastSkinId,
                    StringComparison.Ordinal);
                List<string> deltas = CaptureSpriteDeltas(state, fullSend);
                List<string> transformActiveDeltas = IsDynamicMob(wgo)
                    ? new List<string>()
                    : CaptureTransformActiveDeltas(state, fullSend);
                List<AnimatorParameterEntry> animatorParameters =
                    CaptureAnimatorParameters(
                        state.Animator,
                        out int animatorStateHash,
                        out float animatorNormalizedTime,
                        out string animatorFingerprint);
                bool animatorChanged =
                    animatorStateHash != state.LastAnimatorStateHash ||
                    !string.Equals(
                        animatorFingerprint,
                        state.LastAnimatorFingerprint,
                        StringComparison.Ordinal);
                bool corpseDetached =
                    Patches.DonkeyCartCorpseVisualGuard.IsCorpseDetached(wgo);
                bool corpseDetachedChanged =
                    corpseDetached != state.LastCorpseDetached;

                if (activePuppetEpoch != 0U)
                    hostEpochActors.Add(uid);

                if (!fullSend &&
                    !positionChanged &&
                    !scaleChanged &&
                    !directionChanged &&
                    !skinChanged &&
                    !animatorChanged &&
                    !corpseDetachedChanged &&
                    transformActiveDeltas.Count == 0 &&
                    deltas.Count == 0)
                {
                    continue;
                }

                state.LastPosition = position;
                state.LastScaleX = scaleX;
                state.LastAnimDirection = animDirection;
                state.LastSkinId = skinId;
                state.LastAnimatorStateHash = animatorStateHash;
                state.LastAnimatorFingerprint = animatorFingerprint;
                state.LastCorpseDetached = corpseDetached;
                var entry = new NpcEntry
                {
                    UniqueId = uid,
                    ObjId = wgo.obj_id ?? string.Empty,
                    CustomTag = wgo.custom_tag ?? string.Empty,
                    SkinId = skinId,
                    Position = position,
                    Velocity = velocity,
                    CharacterScaleX = scaleX,
                    AnimDirection = animDirection,
                    HasAnimatorState = animatorStateHash != 0,
                    AnimatorStateHash = animatorStateHash,
                    AnimatorNormalizedTime = animatorNormalizedTime,
                    CorpseDetached = corpseDetached
                };
                entry.AnimatorParameters.AddRange(animatorParameters);
                entry.TransformActiveDeltas.AddRange(
                    transformActiveDeltas);
                entry.SpriteDeltas.AddRange(deltas);
                result.Add(entry);
            }

            scannedObjectCount = scanned;
            sourceScanIndex = (sourceScanIndex + scanned) % objectCount;
            return result;
        }

        private List<WorldGameObject> SnapshotLocalCutsceneActors()
        {
            var result = new List<WorldGameObject>(
                localCutsceneActors.Count);
            var stale = new List<int>();

            foreach (var pair in localCutsceneActors)
            {
                WorldGameObject actor = pair.Value;
                if (!ShouldSyncNpc(actor))
                {
                    stale.Add(pair.Key);
                    continue;
                }

                result.Add(actor);
            }

            for (int i = 0; i < stale.Count; i++)
                localCutsceneActors.Remove(stale[i]);

            return result;
        }

        private void AppendPendingFinalPoseEntries(List<NpcEntry> entries)
        {
            if (entries == null || pendingFinalPoseActors.Count == 0)
                return;

            WGORegistry registry = WGORegistry.Instance;
            if (registry == null)
            {
                pendingFinalPoseActors.Clear();
                return;
            }

            var included = new HashSet<long>();
            for (int i = 0; i < entries.Count; i++)
                included.Add(entries[i].UniqueId);

            foreach (long uniqueId in pendingFinalPoseActors)
            {
                if (included.Contains(uniqueId) ||
                    !registry.TryGet(
                        uniqueId,
                        out WorldGameObject wgo) ||
                    !ShouldSyncNpc(wgo))
                {
                    continue;
                }

                SourceState state = GetSourceState(uniqueId, wgo);
                NpcEntry entry = CaptureEntry(
                    wgo,
                    state,
                    fullSend: true);
                if (entry != null)
                {
                    entry.Velocity = Vector2.zero;
                    entries.Add(entry);
                }
            }

            pendingFinalPoseActors.Clear();
        }

        private SourceState GetSourceState(long uniqueId, WorldGameObject wgo)
        {
            if (!sourceStates.TryGetValue(uniqueId, out SourceState state) || state == null || state.Map == null)
            {
                state = new SourceState
                {
                    Map = VisualSyncHelpers.VisualHierarchyMap.From(
                        wgo.transform,
                        includeTransforms: true),
                    CharacterTransform = VisualSyncHelpers.FindChildByName(wgo.transform, "character"),
                    Animator = wgo.GetComponentInChildren<Animator>(true),
                    LastPosition = wgo.transform.position,
                    LastVelocityPosition = wgo.transform.position,
                    LastVelocitySampleAt = Time.realtimeSinceStartup
                };
                sourceStates[uniqueId] = state;
            }
            else if (state.Animator == null)
            {
                state.Animator =
                    wgo.GetComponentInChildren<Animator>(true);
            }
            return state;
        }

        private static NpcEntry CaptureEntry(
            WorldGameObject wgo,
            SourceState state,
            bool fullSend)
        {
            if (wgo == null || state == null)
                return null;

            Vector3 position = wgo.transform.position;
            Vector2 velocity = CaptureVelocity(
                state,
                position,
                Time.realtimeSinceStartup);
            float scaleX =
                state.CharacterTransform != null
                    ? state.CharacterTransform.localScale.x
                    : 1f;
            byte animDirection =
                (byte)wgo.components.character.anim_direction;
            List<AnimatorParameterEntry> animatorParameters =
                CaptureAnimatorParameters(
                    state.Animator,
                    out int animatorStateHash,
                    out float animatorNormalizedTime,
                    out string animatorFingerprint);

            state.LastPosition = position;
            state.LastScaleX = scaleX;
            state.LastAnimDirection = animDirection;
            state.LastSkinId = wgo.wop?.skin_id ?? string.Empty;
            state.LastAnimatorStateHash = animatorStateHash;
            state.LastAnimatorFingerprint = animatorFingerprint;
            bool corpseDetached =
                Patches.DonkeyCartCorpseVisualGuard.IsCorpseDetached(wgo);
            state.LastCorpseDetached = corpseDetached;

            var entry = new NpcEntry
            {
                UniqueId = wgo.unique_id,
                ObjId = wgo.obj_id ?? string.Empty,
                CustomTag = wgo.custom_tag ?? string.Empty,
                SkinId = state.LastSkinId,
                Position = position,
                Velocity = velocity,
                CharacterScaleX = scaleX,
                AnimDirection = animDirection,
                HasAnimatorState = animatorStateHash != 0,
                AnimatorStateHash = animatorStateHash,
                AnimatorNormalizedTime = animatorNormalizedTime,
                CorpseDetached = corpseDetached
            };
            entry.AnimatorParameters.AddRange(animatorParameters);
            if (!IsDynamicMob(wgo))
            {
                entry.TransformActiveDeltas.AddRange(
                    CaptureTransformActiveDeltas(state, fullSend));
            }
            entry.SpriteDeltas.AddRange(
                CaptureSpriteDeltas(state, fullSend));
            return entry;
        }

        private static Vector2 CaptureVelocity(
            SourceState state,
            Vector3 position,
            float capturedAt)
        {
            if (state == null)
                return Vector2.zero;

            float elapsed = capturedAt - state.LastVelocitySampleAt;
            Vector2 velocity = Vector2.zero;
            if (state.LastVelocitySampleAt > 0f && elapsed > 0.0001f)
            {
                velocity = new Vector2(
                    position.x - state.LastVelocityPosition.x,
                    position.y - state.LastVelocityPosition.y) / elapsed;
            }

            state.LastVelocityPosition = position;
            state.LastVelocitySampleAt = capturedAt;
            return velocity;
        }

        private static List<AnimatorParameterEntry> CaptureAnimatorParameters(
            Animator animator,
            out int stateHash,
            out float normalizedTime,
            out string fingerprint)
        {
            var result =
                new List<AnimatorParameterEntry>(
                    MaxAnimatorParametersPerNpc);
            stateHash = 0;
            normalizedTime = 0f;
            var fingerprintBuilder = new System.Text.StringBuilder(128);

            if (animator == null ||
                animator.runtimeAnimatorController == null)
            {
                fingerprint = string.Empty;
                return result;
            }

            try
            {
                if (animator.layerCount > 0)
                {
                    AnimatorStateInfo info =
                        animator.IsInTransition(0)
                            ? animator.GetNextAnimatorStateInfo(0)
                            : animator.GetCurrentAnimatorStateInfo(0);
                    stateHash = info.fullPathHash;
                    normalizedTime =
                        info.normalizedTime -
                        Mathf.Floor(info.normalizedTime);
                }

                AnimatorControllerParameter[] parameters =
                    animator.parameters;
                for (int i = 0;
                     i < parameters.Length &&
                     result.Count < MaxAnimatorParametersPerNpc;
                     i++)
                {
                    AnimatorControllerParameter parameter =
                        parameters[i];
                    var entry = new AnimatorParameterEntry
                    {
                        Name = parameter.name ?? string.Empty
                    };

                    switch (parameter.type)
                    {
                        case AnimatorControllerParameterType.Float:
                            entry.Type = AnimatorParameterEntry.FloatType;
                            entry.FloatValue =
                                animator.GetFloat(parameter.nameHash);
                            fingerprintBuilder
                                .Append(entry.Name)
                                .Append(":f:")
                                .Append(entry.FloatValue)
                                .Append('|');
                            break;
                        case AnimatorControllerParameterType.Int:
                            entry.Type = AnimatorParameterEntry.IntType;
                            entry.IntValue =
                                animator.GetInteger(parameter.nameHash);
                            fingerprintBuilder
                                .Append(entry.Name)
                                .Append(":i:")
                                .Append(entry.IntValue)
                                .Append('|');
                            break;
                        case AnimatorControllerParameterType.Bool:
                            entry.Type = AnimatorParameterEntry.BoolType;
                            entry.BoolValue =
                                animator.GetBool(parameter.nameHash);
                            fingerprintBuilder
                                .Append(entry.Name)
                                .Append(":b:")
                                .Append(entry.BoolValue ? '1' : '0')
                                .Append('|');
                            break;
                        default:
                            continue;
                    }

                    result.Add(entry);
                }
            }
            catch
            {
                result.Clear();
                stateHash = 0;
                normalizedTime = 0f;
            }

            fingerprintBuilder
                .Append("state:")
                .Append(stateHash);
            fingerprint = fingerprintBuilder.ToString();
            return result;
        }

        private static List<string> CaptureTransformActiveDeltas(
            SourceState state,
            bool fullSend)
        {
            var deltas = new List<string>();
            if (state?.Map?.Transforms == null)
                return deltas;

            foreach (var pair in state.Map.Transforms)
            {
                uint hash = pair.Key;
                Transform transform = pair.Value;
                string entry =
                    $"{hash:X8}:{(transform != null && transform.gameObject.activeSelf ? 1 : 0)}";

                if (fullSend ||
                    !state.LastTransformActiveSnapshot.TryGetValue(
                        hash,
                        out string previous) ||
                    previous != entry)
                {
                    deltas.Add(entry);
                    state.LastTransformActiveSnapshot[hash] = entry;
                    if (deltas.Count >=
                        MaxTransformActiveDeltasPerNpc)
                    {
                        break;
                    }
                }
            }

            return deltas;
        }

        private static bool ShouldSyncNpc(WorldGameObject wgo)
        {
            if (wgo == null || wgo.is_player || wgo.obj_def == null) return false;
            try { return wgo.obj_def.IsNPC(); }
            catch { return false; }
        }

        private static bool IsDynamicMob(WorldGameObject wgo)
        {
            return wgo?.obj_def?.dynamic_mob == true;
        }

        private static List<string> CaptureSpriteDeltas(SourceState state, bool fullSend)
        {
            var deltas = new List<string>();
            // Animator-backed NPCs advance their walk/action animation locally from the
            // semantic animator state below. Streaming sampled frame names makes the local
            // animator and the network alternately overwrite the same SpriteRenderers.
            if (state?.Animator != null &&
                state.Animator.runtimeAnimatorController != null)
            {
                return deltas;
            }

            foreach (var pair in state.Map.Sprites)
            {
                uint hash = pair.Key;
                SpriteRenderer sr = pair.Value;
                string entry;
                if (sr == null)
                {
                    entry = $"{hash:X8}::0";
                }
                else
                {
                    string spriteName = sr.sprite != null ? SpriteNameNormalizer.Normalize(sr.sprite.name) : string.Empty;
                    byte flags = VisualSyncHelpers.PackSpriteFlags(sr);
                    entry = $"{hash:X8}:{spriteName}:{flags}";
                }

                if (fullSend || !state.LastSpriteSnapshot.TryGetValue(hash, out string previous) || previous != entry)
                {
                    deltas.Add(entry);
                    state.LastSpriteSnapshot[hash] = entry;
                    if (deltas.Count >= MaxSpriteDeltasPerNpc) break;
                }
            }
            return deltas;
        }

        private void OnNpcVisualSyncReceived(CSteamID senderID, byte[] payload)
        {
            if (!IsSyncEnabled || payload == null || payload.Length == 0 || payload.Length > MaxPayloadBytes) return;
            if (!IsHost &&
                (HasLocalNpcVisualAuthority() ||
                 Patches.CutsceneSyncPatches.HasActiveMirroredLocalCutscene() ||
                 Patches.NpcInteractionSyncPatches.HasActiveMirroredLocalInteraction()))
            {
                return;
            }

            CSteamID host = SteamLobbyManager.Instance?.GetLobbyOwner() ?? CSteamID.Nil;
            bool hasRemoteCutsceneSource =
                HasRemoteNpcVisualAuthority();
            if (hasRemoteCutsceneSource)
            {
                if (!IsRemoteNpcVisualAuthority(senderID))
                    return;
            }
            else
            {
                if (IsHost || (host != CSteamID.Nil && senderID != host))
                    return;
            }

            if (receivedVisualSource != senderID)
            {
                ReleaseNetworkPuppets(
                    "authoritative NPC visual source changed",
                    clearReceivedEpoch: true);
                targetStates.Clear();
                receivedVisualSource = senderID;
                lastEpochSequence = 0U;
                hasEpochSequence = false;
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} NPC visual authority changed to " +
                    $"{SteamFriends.GetFriendPersonaName(senderID)}");
            }

            if (!DeserializePayload(
                    payload,
                    out uint sequence,
                    out uint puppetEpoch,
                    out var entries))
            {
                return;
            }

            if (!hasEpochSequence || IsSequenceNewer(sequence, lastEpochSequence))
            {
                hasEpochSequence = true;
                lastEpochSequence = sequence;
                bool remoteEpochEnded =
                    receivedPuppetEpoch != 0U && puppetEpoch == 0U;
                if (receivedPuppetEpoch != puppetEpoch)
                {
                    if (receivedPuppetEpoch != 0U)
                    {
                        ReleaseNetworkPuppets(
                            puppetEpoch == 0U
                                ? "authoritative cutscene epoch ended"
                                : $"authoritative epoch changed to {puppetEpoch}",
                            clearReceivedEpoch: true);
                    }

                    receivedPuppetEpoch = puppetEpoch;
                    if (puppetEpoch != 0U)
                    {
                        CoopMod.Logger.LogInfo(
                            $"{LogPrefix} Received cutscene motion epoch {puppetEpoch}");
                    }
                }

                if (remoteEpochEnded)
                {
                    Patches.CutsceneSyncPatches
                        .NotifyRemoteNpcVisualEpochEnded(senderID);
                }
            }

            Dictionary<string, Sprite> sprites = GetSpriteLibrary();
            for (int i = 0; i < entries.Count; i++)
                ApplyEntry(entries[i], sprites);
        }

        private void ApplyEntry(NpcEntry entry, Dictionary<string, Sprite> sprites)
        {
            WorldGameObject wgo = WGORegistry.Instance?.Resolve(entry.UniqueId, entry.ObjId, entry.CustomTag, entry.Position, 192f);
            if (!ShouldSyncNpc(wgo)) return;

            if (!targetStates.TryGetValue(entry.UniqueId, out TargetState state) || state == null || state.Wgo != wgo)
            {
                state = new TargetState
                {
                    Wgo = wgo,
                    Map = VisualSyncHelpers.VisualHierarchyMap.From(
                        wgo.transform,
                        includeTransforms: true),
                    Chunk = wgo.GetComponent<ChunkedGameObject>() ??
                            wgo.GetComponentInChildren<ChunkedGameObject>(true),
                    CharacterTransform = VisualSyncHelpers.FindChildByName(wgo.transform, "character"),
                    Animator = wgo.GetComponentInChildren<Animator>(true)
                };
                targetStates[entry.UniqueId] = state;
            }
            else if (state.Animator == null)
            {
                state.Animator =
                    wgo.GetComponentInChildren<Animator>(true);
            }

            if (state.HasSequence &&
                !IsSequenceNewer(entry.Sequence, state.LastSequence))
            {
                return;
            }

            state.HasSequence = true;
            state.LastSequence = entry.Sequence;
            bool moving =
                entry.Velocity.sqrMagnitude >
                MovingVelocityThreshold * MovingVelocityThreshold;
            if (state.HasReceivedPosition &&
                (entry.Position - state.LastReceivedPosition).sqrMagnitude >
                PositionThreshold * PositionThreshold)
            {
                moving = true;
            }

            state.LastReceivedPosition = entry.Position;
            state.HasReceivedPosition = true;
            state.LatestEntry = entry;
            PushPositionSample(state, entry);
            if (entry.PuppetEpoch == 0U)
            {
                state.PuppetEpoch = 0U;
            }
            else if (moving &&
                     entry.PuppetEpoch == receivedPuppetEpoch &&
                     IsClientPuppetWindowActive())
            {
                state.PuppetEpoch = entry.PuppetEpoch;
            }

            string currentSkinId = wgo.wop?.skin_id ?? string.Empty;
            if (!string.Equals(
                    currentSkinId,
                    entry.SkinId,
                    StringComparison.Ordinal))
            {
                wgo.ApplySkin(entry.SkinId ?? string.Empty);
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Applied host NPC skin for {wgo.obj_id} " +
                    $"(uid={wgo.unique_id}): '{currentSkinId}' -> '{entry.SkinId}'");
            }

            if (state.CharacterTransform != null)
            {
                Vector3 scale = state.CharacterTransform.localScale;
                scale.x = entry.CharacterScaleX;
                state.CharacterTransform.localScale = scale;
            }

            Patches.DonkeyCartCorpseVisualGuard guard = null;
            if (entry.CorpseDetached &&
                Patches.DonkeyCartCorpseVisualGuard.IsDonkey(wgo))
            {
                guard =
                    Patches.DonkeyCartCorpseVisualGuard.MarkCorpseDropped(
                        wgo,
                        "authoritative NPC visual state");
            }

            if (entry.AnimDirection >= (byte)Direction.Right &&
                entry.AnimDirection <= (byte)Direction.Down &&
                (byte)wgo.components.character.anim_direction !=
                entry.AnimDirection)
            {
                wgo.components.character.LookAt((Direction)entry.AnimDirection);
            }

            for (int i = 0;
                 i < entry.TransformActiveDeltas.Count;
                 i++)
            {
                string delta = entry.TransformActiveDeltas[i];
                if (TryGetTransformHash(delta, out uint hash))
                {
                    state.LatestTransformActiveSnapshot[hash] =
                        delta;
                }
                ApplyTransformActiveDelta(state.Map, delta);
            }

            ApplyAnimatorState(wgo, state.Animator, entry);

            for (int i = 0; i < entry.SpriteDeltas.Count; i++)
            {
                string delta = entry.SpriteDeltas[i];
                if (TryGetSpriteHash(delta, out uint hash))
                    state.LatestSpriteSnapshot[hash] = delta;

                VisualSyncHelpers.TryApplySpriteDelta(state.Map, delta, sprites);
            }

            guard?.EnforceCorpseDetachedVisual();
        }

        private static void PushPositionSample(
            TargetState state,
            NpcEntry entry)
        {
            float localTime = Time.realtimeSinceStartup;
            float desiredOffset = entry.SenderTime - localTime;
            if (!state.HasSenderTimeOffset)
            {
                state.SenderTimeOffset = desiredOffset;
                state.HasSenderTimeOffset = true;
            }
            else
            {
                state.SenderTimeOffset = Mathf.Lerp(
                    state.SenderTimeOffset,
                    desiredOffset,
                    0.1f);
            }

            var sample = new PositionSample
            {
                SenderTime = entry.SenderTime,
                Position = entry.Position,
                Velocity = entry.Velocity
            };
            if (state.PositionSamples.Count > 0 &&
                entry.SenderTime <=
                state.PositionSamples[state.PositionSamples.Count - 1].SenderTime)
            {
                state.PositionSamples[state.PositionSamples.Count - 1] = sample;
            }
            else
            {
                state.PositionSamples.Add(sample);
            }

            if (state.PositionSamples.Count > MaxPositionSamples)
            {
                state.PositionSamples.RemoveAt(0);
            }
        }

        private static bool IsSequenceNewer(uint candidate, uint current)
        {
            return candidate != current &&
                   unchecked((int)(candidate - current)) > 0;
        }

        private static void ApplyAnimatorState(
            WorldGameObject wgo,
            Animator animator,
            NpcEntry entry,
            bool allowPhaseCorrection = true,
            bool applyParameters = true)
        {
            if (animator == null ||
                animator.runtimeAnimatorController == null ||
                entry == null)
            {
                return;
            }

            try
            {
                if (applyParameters)
                {
                    for (int i = 0;
                         i < entry.AnimatorParameters.Count;
                         i++)
                    {
                        AnimatorParameterEntry parameter =
                            entry.AnimatorParameters[i];
                        if (parameter == null ||
                            string.IsNullOrEmpty(parameter.Name))
                        {
                            continue;
                        }

                        switch (parameter.Type)
                        {
                            case AnimatorParameterEntry.FloatType:
                                animator.SetFloat(
                                    parameter.Name,
                                    parameter.FloatValue);
                                break;
                            case AnimatorParameterEntry.IntType:
                                animator.SetInteger(
                                    parameter.Name,
                                    parameter.IntValue);
                                if (parameter.Name == "global_state")
                                {
                                    wgo.components.character
                                        .DeserializeGlobalState(
                                            parameter.IntValue);
                                }
                                break;
                            case AnimatorParameterEntry.BoolType:
                                animator.SetBool(
                                    parameter.Name,
                                    parameter.BoolValue);
                                break;
                        }
                    }
                }

                if (entry.HasAnimatorState &&
                    entry.AnimatorStateHash != 0 &&
                    animator.layerCount > 0)
                {
                    AnimatorStateInfo localState =
                        animator.GetCurrentAnimatorStateInfo(0);
                    float localPhase =
                        localState.normalizedTime -
                        Mathf.Floor(localState.normalizedTime);
                    float phaseError = Mathf.Abs(
                        Mathf.DeltaAngle(
                            localPhase * 360f,
                            entry.AnimatorNormalizedTime * 360f) / 360f);
                    bool stateChanged =
                        localState.fullPathHash !=
                        entry.AnimatorStateHash;
                    if (stateChanged ||
                        (allowPhaseCorrection &&
                         phaseError > AnimatorPhaseCorrectionThreshold))
                    {
                        animator.Play(
                            entry.AnimatorStateHash,
                            0,
                            entry.AnimatorNormalizedTime);
                        if (wgo.obj_id == "donkey" ||
                            wgo.custom_tag == "donkey")
                        {
                            CoopMod.Logger.LogInfo(
                                "[NpcVisualSync] Applied host donkey animator state " +
                                $"{entry.AnimatorStateHash} " +
                                $"(was {localState.fullPathHash})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                NetworkDiagnostics.RecordError(
                    "NpcVisualSync.Animator",
                    ex.Message);
            }
        }

        /// <summary>
        /// Reasserts the most recently received authoritative visual after a local
        /// NPC animator or skin component has run. During a client-owned cutscene
        /// the host is also a receiver, so this must follow source ownership rather
        /// than assuming that only clients consume network visuals.
        /// </summary>
        internal int ReapplyLatestAuthoritativeVisual(WorldGameObject wgo)
        {
            if (!IsSyncEnabled ||
                !IsSessionActive() ||
                (IsHost && !HasRemoteNpcVisualAuthority()) ||
                HasLocalNpcVisualAuthority() ||
                wgo == null ||
                wgo.unique_id == 0L ||
                !targetStates.TryGetValue(wgo.unique_id, out TargetState state) ||
                state == null ||
                state.Wgo != wgo ||
                state.Map == null ||
                (state.LatestSpriteSnapshot.Count == 0 &&
                 state.LatestTransformActiveSnapshot.Count == 0))
            {
                return 0;
            }

            Dictionary<string, Sprite> sprites = GetSpriteLibrary();
            int applied = 0;
            foreach (string delta in
                     state.LatestTransformActiveSnapshot.Values)
            {
                if (ApplyTransformActiveDelta(
                        state.Map,
                        delta))
                {
                    applied++;
                }
            }
            foreach (string delta in state.LatestSpriteSnapshot.Values)
            {
                if (VisualSyncHelpers.TryApplySpriteDelta(
                        state.Map,
                        delta,
                        sprites))
                {
                    applied++;
                }
            }

            return applied;
        }

        private static bool ApplyTransformActiveDelta(
            VisualSyncHelpers.VisualHierarchyMap map,
            string delta)
        {
            if (map == null ||
                !TryGetTransformHash(delta, out uint hash))
            {
                return false;
            }

            int separator = delta.IndexOf(':');
            if (separator < 0 ||
                separator + 1 >= delta.Length ||
                !byte.TryParse(
                    delta.Substring(separator + 1),
                    out byte activeFlag) ||
                !map.TryGetTransform(
                    hash,
                    out Transform transform) ||
                transform == null)
            {
                return false;
            }

            bool active = activeFlag != 0;
            if (transform.gameObject.activeSelf != active)
                transform.gameObject.SetActive(active);
            return true;
        }

        private static bool TryGetTransformHash(
            string delta,
            out uint hash)
        {
            hash = 0U;
            if (string.IsNullOrEmpty(delta))
                return false;

            int separator = delta.IndexOf(':');
            return separator > 0 &&
                   uint.TryParse(
                       delta.Substring(0, separator),
                       System.Globalization.NumberStyles.HexNumber,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out hash);
        }

        private static bool TryGetSpriteHash(string delta, out uint hash)
        {
            hash = 0U;
            if (string.IsNullOrEmpty(delta))
                return false;

            int separator = delta.IndexOf(':');
            return separator > 0 &&
                   uint.TryParse(
                       delta.Substring(0, separator),
                       System.Globalization.NumberStyles.HexNumber,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out hash);
        }

        private Dictionary<string, Sprite> GetSpriteLibrary()
        {
            if (spriteLibrary != null) return spriteLibrary;

            spriteLibrary = VisualSyncHelpers.BuildSpriteLibrary();
            return spriteLibrary;
        }

        private static byte[] SerializePayload(
            uint sequence,
            float senderTime,
            uint puppetEpoch,
            List<NpcEntry> entries)
        {
            using (var stream = new MemoryStream(8192))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(PayloadVersion);
                writer.Write(sequence);
                writer.Write(senderTime);
                writer.Write(puppetEpoch);
                writer.Write((ushort)Mathf.Min(entries.Count, MaxNpcsPerPacket));
                for (int i = 0; i < entries.Count && i < MaxNpcsPerPacket; i++)
                {
                    NpcEntry entry = entries[i];
                    writer.Write(entry.UniqueId);
                    writer.Write(entry.ObjId ?? string.Empty);
                    writer.Write(entry.CustomTag ?? string.Empty);
                    writer.Write(entry.SkinId ?? string.Empty);
                    writer.Write(entry.Position.x);
                    writer.Write(entry.Position.y);
                    writer.Write(entry.Position.z);
                    writer.Write(entry.Velocity.x);
                    writer.Write(entry.Velocity.y);
                    writer.Write(entry.CharacterScaleX);
                    writer.Write(entry.AnimDirection);
                    writer.Write(entry.HasAnimatorState);
                    writer.Write(entry.AnimatorStateHash);
                    writer.Write(entry.AnimatorNormalizedTime);
                    writer.Write(entry.CorpseDetached);
                    writer.Write((ushort)Mathf.Min(
                        entry.AnimatorParameters.Count,
                        MaxAnimatorParametersPerNpc));
                    for (int j = 0;
                         j < entry.AnimatorParameters.Count &&
                         j < MaxAnimatorParametersPerNpc;
                         j++)
                    {
                        AnimatorParameterEntry parameter =
                            entry.AnimatorParameters[j];
                        writer.Write(parameter.Name ?? string.Empty);
                        writer.Write(parameter.Type);
                        switch (parameter.Type)
                        {
                            case AnimatorParameterEntry.FloatType:
                                writer.Write(parameter.FloatValue);
                                break;
                            case AnimatorParameterEntry.IntType:
                                writer.Write(parameter.IntValue);
                                break;
                            case AnimatorParameterEntry.BoolType:
                                writer.Write(parameter.BoolValue);
                                break;
                        }
                    }
                    writer.Write((ushort)Mathf.Min(
                        entry.TransformActiveDeltas.Count,
                        MaxTransformActiveDeltasPerNpc));
                    for (int j = 0;
                         j < entry.TransformActiveDeltas.Count &&
                         j < MaxTransformActiveDeltasPerNpc;
                         j++)
                    {
                        writer.Write(
                            entry.TransformActiveDeltas[j] ??
                            string.Empty);
                    }
                    writer.Write((ushort)Mathf.Min(entry.SpriteDeltas.Count, MaxSpriteDeltasPerNpc));
                    for (int j = 0; j < entry.SpriteDeltas.Count && j < MaxSpriteDeltasPerNpc; j++)
                        writer.Write(entry.SpriteDeltas[j] ?? string.Empty);
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static bool DeserializePayload(
            byte[] payload,
            out uint sequence,
            out uint puppetEpoch,
            out List<NpcEntry> entries)
        {
            sequence = 0U;
            puppetEpoch = 0U;
            entries = new List<NpcEntry>();
            try
            {
                using (var stream = new MemoryStream(payload))
                using (var reader = new BinaryReader(stream))
                {
                    if (reader.ReadByte() != PayloadVersion) return false;
                    sequence = reader.ReadUInt32();
                    float senderTime = reader.ReadSingle();
                    puppetEpoch = reader.ReadUInt32();
                    int count = reader.ReadUInt16();
                    if (count > MaxNpcsPerPacket) return false;

                    for (int i = 0; i < count; i++)
                    {
                        var entry = new NpcEntry
                        {
                            UniqueId = reader.ReadInt64(),
                            ObjId = reader.ReadString(),
                            CustomTag = reader.ReadString(),
                            SkinId = reader.ReadString(),
                            Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                            Velocity = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                            CharacterScaleX = reader.ReadSingle(),
                            AnimDirection = reader.ReadByte(),
                            HasAnimatorState = reader.ReadBoolean(),
                            AnimatorStateHash = reader.ReadInt32(),
                            AnimatorNormalizedTime = reader.ReadSingle(),
                            CorpseDetached = reader.ReadBoolean()
                        };
                        entry.Sequence = sequence;
                        entry.SenderTime = senderTime;
                        entry.PuppetEpoch = puppetEpoch;
                        int animatorParameterCount =
                            reader.ReadUInt16();
                        if (animatorParameterCount >
                            MaxAnimatorParametersPerNpc)
                        {
                            return false;
                        }
                        for (int j = 0;
                             j < animatorParameterCount;
                             j++)
                        {
                            var parameter =
                                new AnimatorParameterEntry
                                {
                                    Name = reader.ReadString(),
                                    Type = reader.ReadByte()
                                };
                            switch (parameter.Type)
                            {
                                case AnimatorParameterEntry.FloatType:
                                    parameter.FloatValue =
                                        reader.ReadSingle();
                                    break;
                                case AnimatorParameterEntry.IntType:
                                    parameter.IntValue =
                                        reader.ReadInt32();
                                    break;
                                case AnimatorParameterEntry.BoolType:
                                    parameter.BoolValue =
                                        reader.ReadBoolean();
                                    break;
                                default:
                                    return false;
                            }
                            entry.AnimatorParameters.Add(parameter);
                        }
                        int transformActiveCount =
                            reader.ReadUInt16();
                        if (transformActiveCount >
                            MaxTransformActiveDeltasPerNpc)
                        {
                            return false;
                        }
                        for (int j = 0;
                             j < transformActiveCount;
                             j++)
                        {
                            entry.TransformActiveDeltas.Add(
                                reader.ReadString());
                        }
                        int deltaCount = reader.ReadUInt16();
                        if (deltaCount > MaxSpriteDeltasPerNpc) return false;
                        for (int j = 0; j < deltaCount; j++)
                            entry.SpriteDeltas.Add(reader.ReadString());
                        entries.Add(entry);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                NetworkDiagnostics.RecordError("NpcVisualSync", ex.Message);
                return false;
            }
        }

        private sealed class NpcEntry
        {
            public long UniqueId;
            public string ObjId;
            public string CustomTag;
            public string SkinId;
            public Vector3 Position;
            public Vector2 Velocity;
            public uint Sequence;
            public float SenderTime;
            public uint PuppetEpoch;
            public float CharacterScaleX;
            public byte AnimDirection;
            public bool HasAnimatorState;
            public int AnimatorStateHash;
            public float AnimatorNormalizedTime;
            public bool CorpseDetached;
            public readonly List<AnimatorParameterEntry>
                AnimatorParameters =
                    new List<AnimatorParameterEntry>();
            public readonly List<string> TransformActiveDeltas =
                new List<string>();
            public readonly List<string> SpriteDeltas = new List<string>();
        }

        private sealed class AnimatorParameterEntry
        {
            public const byte FloatType = 0;
            public const byte IntType = 1;
            public const byte BoolType = 2;

            public string Name;
            public byte Type;
            public float FloatValue;
            public int IntValue;
            public bool BoolValue;
        }
    }

    /// <summary>
    /// During an observed authoritative cutscene, the buffered NPC lane is the sole movement
    /// writer for actors that the host has marked as moving in the current motion epoch.
    /// Mirrored local FlowScripts are deliberately excluded so their movement callbacks run.
    /// </summary>
    [HarmonyPatch(
        typeof(MovementComponent),
        nameof(MovementComponent.FixedUpdateComponent))]
    internal static class NetworkNpcPuppetMovementPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(MovementComponent __instance)
        {
            WorldGameObject wgo = __instance?.wgo;
            return wgo == null ||
                   !(NpcVisualSync.Instance?.OwnsNetworkMovement(wgo) ?? false);
        }
    }

    internal static class CutsceneFlowActorRegistration
    {
        internal static void RegisterResolvedActor(
            MyFlowNode node,
            WorldGameObject actor)
        {
            if (node == null || actor == null ||
                !IsActorMutationNode(node.GetType().Name))
            {
                return;
            }

            NpcVisualSync.Instance?.RegisterLocalCutsceneActor(
                actor,
                node.GetType().Name);
        }

        internal static void RegisterDirectFlowActor(
            WorldGameObject actor,
            string operation)
        {
            if (actor == null || !IsCalledFromFlowNode())
                return;

            NpcVisualSync.Instance?.RegisterLocalCutsceneActor(
                actor,
                operation);
            if (operation == "GS.Spawn" ||
                operation == "WorldMap.SpawnWGO")
            {
                SpawnSync.Instance?.RequestCanonicalCutsceneActorSpawn(actor);
            }
        }

        private static bool IsActorMutationNode(string typeName)
        {
            switch (typeName)
            {
                case "Flow_AddWGOParam":
                case "Flow_ChangeWGO":
                case "Flow_DespawnTavernVisitor":
                case "Flow_DestroyWGO":
                case "Flow_FireEvent":
                case "Flow_FollowWGO":
                case "Flow_GoTo":
                case "Flow_MoveWithCustomAnimation":
                case "Flow_NextVariation":
                case "Flow_RemoveNPCToStock":
                case "Flow_ResetAnimator":
                case "Flow_SetAnimatorParam":
                case "Flow_SetCharacterDirection":
                case "Flow_SetCustomTag":
                case "Flow_SetCustomVariation":
                case "Flow_SetSkinToWGO":
                case "Flow_SetVariationByIndex":
                case "Flow_SetWGOParam":
                case "Flow_SetWGOScale":
                case "Flow_SetWGOState":
                case "Flow_StopAnyMovement":
                case "Flow_StopFollowWGO":
                case "Flow_Talk":
                case "Flow_TriggerAnimation":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsCalledFromFlowNode()
        {
            var trace = new System.Diagnostics.StackTrace(1, false);
            System.Diagnostics.StackFrame[] frames = trace.GetFrames();
            if (frames == null)
                return false;

            int count = Math.Min(frames.Length, 32);
            for (int i = 0; i < count; i++)
            {
                string fullName =
                    frames[i].GetMethod()?.DeclaringType?.FullName;
                if (!string.IsNullOrEmpty(fullName) &&
                    fullName.IndexOf(
                        "FlowCanvas.Nodes.Flow_",
                        StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    [HarmonyPatch(
        typeof(MyFlowNode),
        nameof(MyFlowNode.WGOParamOrSelf))]
    internal static class CutsceneFlowActorResolverPatch
    {
        [HarmonyPostfix]
        private static void Postfix(
            MyFlowNode __instance,
            WorldGameObject __result)
        {
            CutsceneFlowActorRegistration.RegisterResolvedActor(
                __instance,
                __result);
        }
    }

    [HarmonyPatch(typeof(GS), nameof(GS.Spawn))]
    internal static class CutsceneGsSpawnActorPatch
    {
        [HarmonyPostfix]
        private static void Postfix(WorldGameObject __result)
        {
            CutsceneFlowActorRegistration.RegisterDirectFlowActor(
                __result,
                "GS.Spawn");
        }
    }

    [HarmonyPatch]
    internal static class CutsceneWorldMapSpawnActorPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo method in
                     AccessTools.GetDeclaredMethods(typeof(WorldMap)))
            {
                if (method.Name == "SpawnWGO" &&
                    method.ReturnType == typeof(WorldGameObject))
                {
                    yield return method;
                }
            }
        }

        [HarmonyPostfix]
        private static void Postfix(WorldGameObject __result)
        {
            CutsceneFlowActorRegistration.RegisterDirectFlowActor(
                __result,
                "WorldMap.SpawnWGO");
        }
    }

    [HarmonyPatch(
        typeof(WorldGameObject),
        nameof(WorldGameObject.TeleportToGDPoint))]
    internal static class CutsceneWgoTeleportActorPatch
    {
        [HarmonyPrefix]
        private static void Prefix(WorldGameObject __instance)
        {
            CutsceneFlowActorRegistration.RegisterDirectFlowActor(
                __instance,
                "WorldGameObject.TeleportToGDPoint");
        }
    }

    [HarmonyPatch]
    internal static class CutsceneCharacterTeleportActorPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo method in
                     AccessTools.GetDeclaredMethods(
                         typeof(BaseCharacterComponent)))
            {
                if (method.Name == "TeleportWithFade")
                    yield return method;
            }
        }

        [HarmonyPrefix]
        private static void Prefix(BaseCharacterComponent __instance)
        {
            CutsceneFlowActorRegistration.RegisterDirectFlowActor(
                __instance?.wgo,
                "BaseCharacterComponent.TeleportWithFade");
        }
    }
}

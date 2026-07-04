using System;
using System.Collections.Generic;
using System.IO;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;
using GraveyardKeeperCoop.Utils;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Host-driven visual correction for NPCs. This is intentionally one-way: clients
    /// smooth toward host positions and apply sprite/flip/active deltas.
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
        }

        private sealed class TargetState
        {
            public WorldGameObject Wgo;
            public VisualSyncHelpers.VisualHierarchyMap Map;
            public readonly Dictionary<uint, string> LatestSpriteSnapshot =
                new Dictionary<uint, string>();
            public readonly Dictionary<uint, string> LatestTransformActiveSnapshot =
                new Dictionary<uint, string>();
            public Transform CharacterTransform;
            public Animator Animator;
            public Vector3 TargetPosition;
        }

        private const byte PayloadVersion = 5;
        private const float BroadcastIntervalSeconds = 0.15f;
        private const float FullResendIntervalSeconds = 5f;
        private const float PositionThreshold = 0.25f;
        private const float SnapDistance = 6f;
        private const float LerpSpeed = 8f;
        private const int MaxNpcsPerPacket = 48;
        private const int MaxObjectsScannedPerBroadcast = 768;
        private const int MaxSpriteDeltasPerNpc = 96;
        private const int MaxTransformActiveDeltasPerNpc = 256;
        private const int MaxAnimatorParametersPerNpc = 64;
        private const int MaxPayloadBytes = 192 * 1024;
        // Steam P2P's unreliable packet limit is ~1100 bytes. Larger sends fall
        // back to reliable delivery, which is rate-limited and congests control
        // when many NPC deltas pile up. Chunk each broadcast into sub-packets
        // that stay safely under the unreliable limit.
        private const int TargetUnreliablePayloadBytes = 1000;

        private readonly Dictionary<long, SourceState> sourceStates = new Dictionary<long, SourceState>();
        private readonly Dictionary<long, TargetState> targetStates = new Dictionary<long, TargetState>();
        private Dictionary<string, Sprite> spriteLibrary;

        private float nextBroadcastAt;
        private float nextFullResendAt;
        private int sourceScanIndex;

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
        }

        private void Update()
        {
            if (!IsSyncEnabled || !IsSessionActive()) return;

            if (IsHost && Time.realtimeSinceStartup >= nextBroadcastAt)
            {
                nextBroadcastAt = Time.realtimeSinceStartup + BroadcastIntervalSeconds;
                BroadcastHostNpcState();
            }
        }

        private void LateUpdate()
        {
            if (!IsSyncEnabled || !IsSessionActive() || IsHost || targetStates.Count == 0) return;

            float snapSqr = SnapDistance * SnapDistance;
            float lerp = Mathf.Clamp01(Time.deltaTime * LerpSpeed);
            var stale = new List<long>();

            foreach (var pair in targetStates)
            {
                TargetState state = pair.Value;
                if (state == null || state.Wgo == null || state.Wgo.transform == null)
                {
                    stale.Add(pair.Key);
                    continue;
                }

                Vector3 current = state.Wgo.transform.position;
                Vector3 target = state.TargetPosition;
                target.z = current.z;
                Vector3 next = (current - target).sqrMagnitude > snapSqr
                    ? target
                    : Vector3.Lerp(current, target, lerp);

                try { state.Wgo.PlaceAtPos(next); }
                catch { state.Wgo.transform.position = next; }
            }

            for (int i = 0; i < stale.Count; i++)
                targetStates.Remove(stale[i]);
        }

        private bool IsSessionActive() => IsOnline && MainGame.me != null && MainGame.game_started;

        private void BroadcastHostNpcState()
        {
            bool fullSend = Time.realtimeSinceStartup >= nextFullResendAt;
            if (fullSend)
                nextFullResendAt = Time.realtimeSinceStartup + FullResendIntervalSeconds;

            List<NpcEntry> entries = CaptureHostEntries(fullSend);
            if (entries.Count == 0) return;

            // Greedily pack entries into sub-packets that stay under Steam's
            // unreliable limit. Each chunk is sent as its own unreliable packet
            // so we avoid the reliable-channel fallback for the common case.
            int index = 0;
            while (index < entries.Count)
            {
                List<NpcEntry> chunk = TakeChunkUnderBudget(entries, ref index, TargetUnreliablePayloadBytes);
                if (chunk.Count == 0)
                {
                    if (index < entries.Count) index++; // skip an entry that alone exceeds the budget
                    continue;
                }

                byte[] payload = SerializePayload(++NextSequence, chunk);
                if (payload.Length > MaxPayloadBytes)
                {
                    CoopMod.Logger.LogWarning($"{LogPrefix} Payload too large ({payload.Length}); skipping");
                    NetworkDiagnostics.RecordError("NpcVisualSync", $"Payload too large: {payload.Length}");
                    continue;
                }

                SteamP2PManager.Instance?.BroadcastNpcVisualSync(payload);
            }
        }

        internal void BroadcastAuthoritativeStateNow(WorldGameObject wgo)
        {
            if (!IsSyncEnabled ||
                !IsSessionActive() ||
                !IsHost ||
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
                new List<NpcEntry> { entry });
            if (payload.Length <= MaxPayloadBytes)
            {
                SteamP2PManager.Instance?.BroadcastNpcVisualSync(payload);
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Broadcast immediate authoritative state for " +
                    $"{wgo.obj_id} (uid={wgo.unique_id}, animator_state={entry.AnimatorStateHash}, " +
                    $"skin='{entry.SkinId}', " +
                    $"transforms={entry.TransformActiveDeltas.Count}, sprites={entry.SpriteDeltas.Count})");
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
                state.TargetPosition = position;
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
            int estimatedBytes = 16; // payload header (version + sequence + count)
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
            // Fixed fields: long UniqueId (8) + 3 floats for Position (12) +
            // float CharacterScaleX (4) + byte AnimDirection (1) +
            // animator state (9) + two ushort counts (4) +
            // ObjId string (1-4 length prefix + UTF-8 bytes) +
            // CustomTag/SkinId strings (1-4 length prefix + UTF-8 bytes).
            int cost = 38;
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

        private List<NpcEntry> CaptureHostEntries(bool fullSend)
        {
            var result = new List<NpcEntry>(MaxNpcsPerPacket);
            List<WorldGameObject> objects = WGORegistry.Instance?.SnapshotAll() ?? MainGame.me?.GetListOfWorldObjects();
            if (objects == null) return result;

            int objectCount = objects.Count;
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

                long uid = wgo.unique_id;
                if (uid == 0L) continue;

                SourceState state = GetSourceState(uid, wgo);
                if (state == null) continue;

                Vector3 position = wgo.transform.position;
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
                List<string> transformActiveDeltas =
                    CaptureTransformActiveDeltas(state, fullSend);
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

                if (!fullSend &&
                    !positionChanged &&
                    !scaleChanged &&
                    !directionChanged &&
                    !skinChanged &&
                    !animatorChanged &&
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
                var entry = new NpcEntry
                {
                    UniqueId = uid,
                    ObjId = wgo.obj_id ?? string.Empty,
                    CustomTag = wgo.custom_tag ?? string.Empty,
                    SkinId = skinId,
                    Position = position,
                    CharacterScaleX = scaleX,
                    AnimDirection = animDirection,
                    HasAnimatorState = animatorStateHash != 0,
                    AnimatorStateHash = animatorStateHash,
                    AnimatorNormalizedTime = animatorNormalizedTime
                };
                entry.AnimatorParameters.AddRange(animatorParameters);
                entry.TransformActiveDeltas.AddRange(
                    transformActiveDeltas);
                entry.SpriteDeltas.AddRange(deltas);
                result.Add(entry);
            }

            sourceScanIndex = (sourceScanIndex + scanned) % objectCount;
            return result;
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
                    LastPosition = wgo.transform.position
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

            var entry = new NpcEntry
            {
                UniqueId = wgo.unique_id,
                ObjId = wgo.obj_id ?? string.Empty,
                CustomTag = wgo.custom_tag ?? string.Empty,
                SkinId = state.LastSkinId,
                Position = position,
                CharacterScaleX = scaleX,
                AnimDirection = animDirection,
                HasAnimatorState = animatorStateHash != 0,
                AnimatorStateHash = animatorStateHash,
                AnimatorNormalizedTime = animatorNormalizedTime
            };
            entry.AnimatorParameters.AddRange(animatorParameters);
            entry.TransformActiveDeltas.AddRange(
                CaptureTransformActiveDeltas(state, fullSend));
            entry.SpriteDeltas.AddRange(
                CaptureSpriteDeltas(state, fullSend));
            return entry;
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

        private static List<string> CaptureSpriteDeltas(SourceState state, bool fullSend)
        {
            var deltas = new List<string>();
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
            if (IsHost) return;

            if (!DeserializePayload(payload, out var entries)) return;

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
                    CharacterTransform = VisualSyncHelpers.FindChildByName(wgo.transform, "character"),
                    Animator = wgo.GetComponentInChildren<Animator>(true),
                    TargetPosition = entry.Position
                };
                targetStates[entry.UniqueId] = state;
            }
            else if (state.Animator == null)
            {
                state.Animator =
                    wgo.GetComponentInChildren<Animator>(true);
            }

            state.TargetPosition = entry.Position;
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

            if (entry.AnimDirection >= (byte)Direction.Right &&
                entry.AnimDirection <= (byte)Direction.Down)
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
        }

        private static void ApplyAnimatorState(
            WorldGameObject wgo,
            Animator animator,
            NpcEntry entry)
        {
            if (animator == null ||
                animator.runtimeAnimatorController == null ||
                entry == null)
            {
                return;
            }

            try
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

                if (entry.HasAnimatorState &&
                    entry.AnimatorStateHash != 0 &&
                    animator.layerCount > 0)
                {
                    AnimatorStateInfo localState =
                        animator.GetCurrentAnimatorStateInfo(0);
                    if (localState.fullPathHash !=
                        entry.AnimatorStateHash)
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
        /// Reasserts the most recently received host visual after a local NPC
        /// animator or skin component has run. Most NPCs should animate locally
        /// between packets, so callers opt in only while correcting a known
        /// authoritative visual transition.
        /// </summary>
        internal int ReapplyLatestHostVisual(WorldGameObject wgo)
        {
            if (!IsSyncEnabled ||
                !IsSessionActive() ||
                IsHost ||
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

        private static byte[] SerializePayload(uint sequence, List<NpcEntry> entries)
        {
            using (var stream = new MemoryStream(8192))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(PayloadVersion);
                writer.Write(sequence);
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
                    writer.Write(entry.CharacterScaleX);
                    writer.Write(entry.AnimDirection);
                    writer.Write(entry.HasAnimatorState);
                    writer.Write(entry.AnimatorStateHash);
                    writer.Write(entry.AnimatorNormalizedTime);
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

        private static bool DeserializePayload(byte[] payload, out List<NpcEntry> entries)
        {
            entries = new List<NpcEntry>();
            try
            {
                using (var stream = new MemoryStream(payload))
                using (var reader = new BinaryReader(stream))
                {
                    if (reader.ReadByte() != PayloadVersion) return false;
                    reader.ReadUInt32();
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
                            CharacterScaleX = reader.ReadSingle(),
                            AnimDirection = reader.ReadByte(),
                            HasAnimatorState = reader.ReadBoolean(),
                            AnimatorStateHash = reader.ReadInt32(),
                            AnimatorNormalizedTime = reader.ReadSingle()
                        };
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
            public float CharacterScaleX;
            public byte AnimDirection;
            public bool HasAnimatorState;
            public int AnimatorStateHash;
            public float AnimatorNormalizedTime;
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
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    public class CombatSync : SyncBehaviour
    {
        public static CombatSync Instance => GetInstance<CombatSync>();

        private const byte PayloadVersion = 1;
        private const float DropDuplicateDistance = 3f;
        private const float DropFallbackMatchDistance = 12f;
        private const float DropCreateBroadcastRememberSeconds = 8f;
        private const float DropPositionSyncIntervalSeconds = 0.2f;
        private const float DropPositionMoveDistance = 0.35f;
        private const float DropCollectRememberSeconds = 1f;
        private const int MaxWgoHpUpdatesPerFrame = 32;
        private const int MaxWgoFieldUpdatesPerBatch = 32;
        private const int MaxDropPositionsPerTick = 16;
        private const int MaxDropItemJsonLength = 262144;

        private readonly Dictionary<long, float> pendingHpUpdates = new Dictionary<long, float>();
        private readonly Dictionary<long, float> pendingDurabilityUpdates = new Dictionary<long, float>();
        private readonly Dictionary<long, float> pendingProgressUpdates = new Dictionary<long, float>();
        private readonly Dictionary<int, float> recentDropCreateBroadcasts = new Dictionary<int, float>();
        private readonly Dictionary<int, float> recentDropCollectBroadcasts = new Dictionary<int, float>();
        private readonly Dictionary<int, Vector3> lastDropPositions = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, string> dropNetIdsByInstance = new Dictionary<int, string>();
        private readonly Dictionary<string, DropResGameObject> dropsByNetId = new Dictionary<string, DropResGameObject>(StringComparer.Ordinal);
        private readonly HashSet<string> removedDropNetIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<long> pendingDeathWgos = new HashSet<long>();
        private float lastHpFlushTime;
        private float lastDropPositionSyncTime;
        private uint nextLocalDropNetId;
        private static int dropCreateSuppressionDepth;

        public static bool IsApplyingRemoteDrop { get; private set; }
        internal static bool IsApplyingRemoteState { get; private set; }
        public static bool IsDropCreateBroadcastSuppressed => dropCreateSuppressionDepth > 0 || IsApplyingRemoteDrop;

        internal const byte SubWgoHpChange = 0;
        internal const byte SubWgoDeath = 1;
        internal const byte SubDropCreate = 2;
        internal const byte SubDropCollect = 3;
        internal const byte SubWgoDurabilityChange = 4;
        internal const byte SubWgoProgressChange = 5;
        internal const byte SubWgoHitReaction = 6;
        internal const byte SubWgoDeathAnimation = 7;
        internal const byte SubDropPosition = 8;

        protected override string LogPrefix => "[CombatSync]";

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnCombatSyncReceived -= OnCombatSyncReceived;
                SteamP2PManager.Instance.OnCombatSyncReceived += OnCombatSyncReceived;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnCombatSyncReceived -= OnCombatSyncReceived;
            }
        }

        protected override void OnSyncEnabled()
        {
            pendingHpUpdates.Clear();
            pendingDurabilityUpdates.Clear();
            pendingProgressUpdates.Clear();
            pendingDeathWgos.Clear();
            recentDropCreateBroadcasts.Clear();
            recentDropCollectBroadcasts.Clear();
            lastDropPositions.Clear();
            dropNetIdsByInstance.Clear();
            dropsByNetId.Clear();
            removedDropNetIds.Clear();
            nextLocalDropNetId = 0;
            IsApplyingRemoteDrop = false;
            IsApplyingRemoteState = false;
            dropCreateSuppressionDepth = 0;
            lastHpFlushTime = 0f;
            lastDropPositionSyncTime = 0f;
        }

        protected override void OnSyncDisabled()
        {
            pendingHpUpdates.Clear();
            pendingDurabilityUpdates.Clear();
            pendingProgressUpdates.Clear();
            pendingDeathWgos.Clear();
            recentDropCreateBroadcasts.Clear();
            recentDropCollectBroadcasts.Clear();
            lastDropPositions.Clear();
            dropNetIdsByInstance.Clear();
            dropsByNetId.Clear();
            removedDropNetIds.Clear();
            nextLocalDropNetId = 0;
            IsApplyingRemoteDrop = false;
            IsApplyingRemoteState = false;
            dropCreateSuppressionDepth = 0;
            lastDropPositionSyncTime = 0f;
        }

        internal static void PushDropCreateSuppression()
        {
            dropCreateSuppressionDepth++;
        }

        internal static void PopDropCreateSuppression()
        {
            dropCreateSuppressionDepth = Mathf.Max(0, dropCreateSuppressionDepth - 1);
        }

        internal void NotifyLocalDropCreated(DropResGameObject drop)
        {
            if (!IsSyncEnabled || IsDropCreateBroadcastSuppressed) return;
            if (!IsOnline) return;
            if (drop == null || drop.res == null || string.IsNullOrEmpty(drop.res.id)) return;
            if (drop.res.is_tech_point) return;

            PruneDropCreateBroadcasts();

            int instanceId = drop.GetInstanceID();
            if (recentDropCreateBroadcasts.ContainsKey(instanceId))
                return;

            recentDropCreateBroadcasts[instanceId] = Time.realtimeSinceStartup;
            Vector3 pos = GetDropPosition(drop);
            lastDropPositions[instanceId] = pos;
            string netId = EnsureDropNetId(drop);
            SendDropCreateEvent(
                netId,
                drop.res.id,
                drop.res.value,
                pos.x,
                pos.y,
                pos.z,
                drop.zone_id ?? drop.res.drop_zone_id ?? string.Empty,
                drop.res.ToJSON());
        }

        internal void NotifyLocalDropCollected(DropResGameObject drop)
        {
            if (!IsSyncEnabled || drop == null || drop.res == null) return;
            if (!IsOnline) return;

            PruneDropCreateBroadcasts();
            PruneDropCollectBroadcasts();

            int instanceId = drop.GetInstanceID();
            if (recentDropCollectBroadcasts.ContainsKey(instanceId))
                return;

            recentDropCollectBroadcasts[instanceId] = Time.realtimeSinceStartup;
            dropNetIdsByInstance.TryGetValue(instanceId, out string netId);

            Vector3 pos = GetDropPosition(drop);
            string itemId = drop.res.id ?? string.Empty;
            SendDropCollectEvent(
                netId,
                itemId,
                drop.res.value,
                pos.x,
                pos.y,
                pos.z);
            RemoveTrackedDrop(netId, drop);
            FinalizeDropRemoval(drop, removeFromDropsList: false);
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Sent and finalized DropCollect: net_id='{netId ?? string.Empty}', " +
                $"item='{itemId}', pos={pos}");
        }

        internal void QueueWgoHpChange(long wgoUniqueId, float newHp)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (wgoUniqueId <= 0) return;

            if (pendingHpUpdates.Count < MaxWgoHpUpdatesPerFrame * 10)
            {
                pendingHpUpdates[wgoUniqueId] = newHp;
            }
        }

        internal void QueueWgoDurabilityChange(long wgoUniqueId, float newDurability)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (wgoUniqueId <= 0) return;

            if (pendingDurabilityUpdates.Count < MaxWgoFieldUpdatesPerBatch * 10)
            {
                pendingDurabilityUpdates[wgoUniqueId] = newDurability;
            }
        }

        internal void QueueWgoProgressChange(long wgoUniqueId, float newProgress)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (wgoUniqueId <= 0) return;

            if (pendingProgressUpdates.Count < MaxWgoFieldUpdatesPerBatch * 10)
            {
                pendingProgressUpdates[wgoUniqueId] = newProgress;
            }
        }

        internal void SendWgoDeathEvent(long wgoUniqueId, string wgoObjId)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;

            using (var stream = new MemoryStream(128))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubWgoDeath);
                bw.Write(NextSequence++);
                bw.Write(wgoUniqueId);
                bw.Write(wgoObjId ?? "");
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastCombatSync(stream.ToArray());
            }
            CoopMod.Logger.LogInfo($"{LogPrefix} Sent WgoDeath: uid={wgoUniqueId}, obj_id={wgoObjId}");
        }

        internal void SendDropCreateEvent(
            string netId,
            string itemId,
            int value,
            float x,
            float y,
            float z,
            string zoneId,
            string itemJson)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;

            if (itemJson != null && itemJson.Length > MaxDropItemJsonLength)
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Drop item payload too large for '{itemId}' ({itemJson.Length} chars); sending basic item state");
                itemJson = string.Empty;
            }

            using (var stream = new MemoryStream(512 + (itemJson?.Length ?? 0)))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubDropCreate);
                bw.Write(NextSequence++);
                bw.Write(itemId ?? "");
                bw.Write(value);
                bw.Write(x);
                bw.Write(y);
                bw.Write(z);
                bw.Write(zoneId ?? "");
                bw.Write(netId ?? "");
                bw.Write(itemJson ?? "");
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastCombatSync(stream.ToArray());
            }
        }

        internal void SendDropCollectEvent(string netId, string itemId, int value, float x, float y, float z)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;

            using (var stream = new MemoryStream(256))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubDropCollect);
                bw.Write(NextSequence++);
                bw.Write(itemId ?? "");
                bw.Write(value);
                bw.Write(x);
                bw.Write(y);
                bw.Write(z);
                bw.Write(netId ?? "");
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastCombatSync(stream.ToArray());
            }
        }

        private void SendDropPositionEvent(string netId, string itemId, int value, Vector3 pos)
        {
            if (string.IsNullOrEmpty(itemId)) return;

            using (var stream = new MemoryStream(256))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubDropPosition);
                bw.Write(NextSequence++);
                bw.Write(itemId);
                bw.Write(value);
                bw.Write(pos.x);
                bw.Write(pos.y);
                bw.Write(pos.z);
                bw.Write(netId ?? "");
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastCombatSync(stream.ToArray());
            }
        }

        internal void SendWgoHitReactionEvent(long wgoUniqueId, float damageDirection)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;

            using (var stream = new MemoryStream(64))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubWgoHitReaction);
                bw.Write(NextSequence++);
                bw.Write(wgoUniqueId);
                bw.Write(damageDirection);
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastCombatSync(stream.ToArray());
            }
        }

        internal void SendWgoDeathAnimationEvent(long wgoUniqueId)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;

            using (var stream = new MemoryStream(64))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubWgoDeathAnimation);
                bw.Write(NextSequence++);
                bw.Write(wgoUniqueId);
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastCombatSync(stream.ToArray());
            }
        }

        private void Update()
        {
            if (!IsSyncEnabled) return;

            BroadcastMovedDrops();

            if (pendingHpUpdates.Count == 0 && pendingDurabilityUpdates.Count == 0 && pendingProgressUpdates.Count == 0) return;

            float now = Time.realtimeSinceStartup;
            if (now - lastHpFlushTime < 0.1f) return;

            lastHpFlushTime = now;
            FlushPendingHpUpdates();
            FlushPendingDurabilityUpdates();
            FlushPendingProgressUpdates();
        }

        private void FlushPendingHpUpdates()
        {
            if (pendingHpUpdates.Count == 0) return;

            int count = Mathf.Min(pendingHpUpdates.Count, MaxWgoHpUpdatesPerFrame);
            using (var stream = new MemoryStream(4 + count * 12 + 4))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubWgoHpChange);
                bw.Write(NextSequence++);
                bw.Write((ushort)count);

                int written = 0;
                var enumerator = pendingHpUpdates.GetEnumerator();
                while (enumerator.MoveNext() && written < count)
                {
                    bw.Write(enumerator.Current.Key);
                    bw.Write(enumerator.Current.Value);
                    written++;
                }

                pendingHpUpdates.Clear();

                bw.Flush();
                SteamP2PManager.Instance?.BroadcastCombatSync(stream.ToArray());
            }
        }

        private void FlushPendingDurabilityUpdates()
        {
            if (pendingDurabilityUpdates.Count == 0) return;

            int count = Mathf.Min(pendingDurabilityUpdates.Count, MaxWgoFieldUpdatesPerBatch);
            using (var stream = new MemoryStream(4 + count * 12 + 4))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubWgoDurabilityChange);
                bw.Write(NextSequence++);
                bw.Write((ushort)count);

                int written = 0;
                var enumerator = pendingDurabilityUpdates.GetEnumerator();
                while (enumerator.MoveNext() && written < count)
                {
                    bw.Write(enumerator.Current.Key);
                    bw.Write(enumerator.Current.Value);
                    written++;
                }

                pendingDurabilityUpdates.Clear();

                bw.Flush();
                SteamP2PManager.Instance?.BroadcastCombatSync(stream.ToArray());
            }
        }

        private void FlushPendingProgressUpdates()
        {
            if (pendingProgressUpdates.Count == 0) return;

            int count = Mathf.Min(pendingProgressUpdates.Count, MaxWgoFieldUpdatesPerBatch);
            using (var stream = new MemoryStream(4 + count * 12 + 4))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubWgoProgressChange);
                bw.Write(NextSequence++);
                bw.Write((ushort)count);

                int written = 0;
                var enumerator = pendingProgressUpdates.GetEnumerator();
                while (enumerator.MoveNext() && written < count)
                {
                    bw.Write(enumerator.Current.Key);
                    bw.Write(enumerator.Current.Value);
                    written++;
                }

                pendingProgressUpdates.Clear();

                bw.Flush();
                SteamP2PManager.Instance?.BroadcastCombatSync(stream.ToArray());
            }
        }

        private void OnCombatSyncReceived(CSteamID senderID, byte[] payload)
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
                        CoopMod.Logger.LogWarning($"{LogPrefix} Unknown payload version {version}");
                        return;
                    }

                    byte subType = reader.ReadByte();

                    switch (subType)
                    {
                        case SubWgoHpChange:
                            HandleWgoHpChange(reader, senderID);
                            break;
                        case SubWgoDeath:
                            HandleWgoDeath(reader, senderID);
                            break;
                        case SubDropCreate:
                            HandleDropCreate(reader, senderID);
                            break;
                        case SubDropCollect:
                            HandleDropCollect(reader, senderID);
                            break;
                        case SubWgoDurabilityChange:
                            HandleWgoDurabilityChange(reader, senderID);
                            break;
                        case SubWgoProgressChange:
                            HandleWgoProgressChange(reader, senderID);
                            break;
                        case SubWgoHitReaction:
                            HandleWgoHitReaction(reader, senderID);
                            break;
                        case SubWgoDeathAnimation:
                            HandleWgoDeathAnimation(reader, senderID);
                            break;
                        case SubDropPosition:
                            HandleDropPosition(reader, senderID);
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

        private void HandleWgoHpChange(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;
            ApplyEchoSuppress();

            int count = reader.ReadUInt16();
            ApplyRemoteState(() =>
            {
                for (int i = 0; i < count; i++)
                {
                    long uid = reader.ReadInt64();
                    float newHp = reader.ReadSingle();

                    var wgo = FindWgoByUniqueId(uid);
                    if (wgo != null && !wgo.is_player)
                    {
                        wgo.hp = newHp;
                    }
                }
            });
        }

        private void HandleWgoDeath(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            long uid = reader.ReadInt64();
            string objId = reader.ReadString();

            ApplyRemoteState(() =>
            {
                var wgo = FindWgoByUniqueId(uid);
                if (wgo != null && !wgo.is_dead && !wgo.is_player)
                {
                    wgo.hp = 0f;
                    wgo.is_dead = true;
                    CoopMod.Logger.LogInfo($"{LogPrefix} Applied WgoDeath: uid={uid}, obj_id={objId}");
                }
            });
        }

        private void HandleDropCreate(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            string itemId = reader.ReadString();
            int value = reader.ReadInt32();
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            string zoneId = reader.ReadString();
            string netId = ReadOptionalString(reader);
            string itemJson = ReadOptionalString(reader);

            if (string.IsNullOrEmpty(itemId)) return;
            if (!string.IsNullOrEmpty(netId) && removedDropNetIds.Contains(netId)) return;

            Item item = DeserializeDropItem(itemJson, itemId, value);
            if (item.is_tech_point)
            {
                MainGame.me.player.AddToParams(itemId, (float)value);
                return;
            }

            var pos = new Vector3(x, y, z);
            if (!string.IsNullOrEmpty(netId))
            {
                if (dropsByNetId.TryGetValue(netId, out DropResGameObject tracked) && IsUsableDrop(tracked))
                {
                    tracked.res = item;
                    tracked.zone_id = zoneId ?? string.Empty;
                    tracked.res.drop_zone_id = tracked.zone_id;
                    return;
                }

                dropsByNetId.Remove(netId);
            }

            DropResGameObject existing = FindMatchingDrop(itemId, value, pos, DropDuplicateDistance);
            if (existing != null)
            {
                existing.res = item;
                existing.zone_id = zoneId ?? string.Empty;
                existing.res.drop_zone_id = existing.zone_id;
                RegisterDrop(netId, existing);
                return;
            }

            IsApplyingRemoteDrop = true;
            try
            {
                DropResGameObject created = DropResGameObject.Drop(pos, item, MainGame.me.world_root, Direction.None);
                if (created != null)
                {
                    created.zone_id = zoneId ?? string.Empty;
                    RegisterDrop(netId, created);
                }
            }
            finally
            {
                IsApplyingRemoteDrop = false;
            }
        }

        private void HandleDropCollect(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            string itemId = reader.ReadString();
            int value = reader.ReadInt32();
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            string netId = ReadOptionalString(reader);

            if (MainGame.me?.world_root == null) return;

            var pos = new Vector3(x, y, z);
            DropResGameObject closest = ResolveDrop(netId, itemId, value, pos);
            if (closest != null)
            {
                RemoveTrackedDrop(netId, closest);
                FinalizeDropRemoval(closest, removeFromDropsList: true);
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Applied DropCollect: net_id='{netId}', item='{itemId}', pos={pos}");
            }
        }

        private void HandleWgoDurabilityChange(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;
            ApplyEchoSuppress();

            int count = reader.ReadUInt16();
            ApplyRemoteState(() =>
            {
                for (int i = 0; i < count; i++)
                {
                    long uid = reader.ReadInt64();
                    float newDurability = reader.ReadSingle();

                    var wgo = FindWgoByUniqueId(uid);
                    if (wgo != null && !wgo.is_player && wgo.data != null)
                    {
                        wgo.data.durability = newDurability;
                    }
                }
            });
        }

        private void HandleDropPosition(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            string itemId = reader.ReadString();
            int value = reader.ReadInt32();
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            string netId = ReadOptionalString(reader);

            var pos = new Vector3(x, y, z);
            DropResGameObject drop = ResolveDrop(netId, itemId, value, pos);
            if (drop == null || drop.transform == null)
                return;

            drop.transform.position = pos;
            RegisterDrop(netId, drop);
            lastDropPositions[drop.GetInstanceID()] = pos;
        }

        private void HandleWgoProgressChange(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;
            ApplyEchoSuppress();

            int count = reader.ReadUInt16();
            ApplyRemoteState(() =>
            {
                for (int i = 0; i < count; i++)
                {
                    long uid = reader.ReadInt64();
                    float newProgress = reader.ReadSingle();

                    var wgo = FindWgoByUniqueId(uid);
                    if (wgo != null && !wgo.is_player)
                    {
                        wgo.progress = newProgress;
                    }
                }
            });
        }

        private void HandleWgoHitReaction(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            long uid = reader.ReadInt64();
            float damageDirection = reader.ReadSingle();

            ApplyRemoteState(() =>
            {
                var wgo = FindWgoByUniqueId(uid);
                if (wgo == null || wgo.is_player || wgo.components == null)
                    return;

                if (wgo.components.character != null &&
                    wgo.components.character.enabled)
                {
                    wgo.components.character.OnWasDamaged(damageDirection);
                }
                if (wgo.components.hp != null && wgo.components.hp.enabled)
                {
                    EnemiesHPBarsManager.me?.AddIfNeeded(wgo);
                }
            });
        }

        private void HandleWgoDeathAnimation(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            long uid = reader.ReadInt64();

            ApplyRemoteState(() =>
            {
                var wgo = FindWgoByUniqueId(uid);
                if (wgo == null || wgo.is_player || wgo.is_dead) return;

                wgo.hp = 0f;
                wgo.is_dead = true;

                if (wgo.components != null)
                {
                    if (wgo.components.character != null && wgo.components.character.enabled)
                    {
                        var character = wgo.components.character;
                        if (character.attack != null && character.attack.performing_attack)
                        {
                            character.attack.InterruptAttack();
                        }
                        character.StopMovement();
                        character.SetAnimationState(CharAnimState.Dying, ItemDefinition.ItemType.None);
                        if (character.body != null)
                        {
                            character.body.bodyType = RigidbodyType2D.Static;
                        }
                    }

                    if (wgo.components.animator != null)
                    {
                        try { wgo.components.animator.SetTrigger("do_dying"); }
                        catch { }
                    }

                    if (wgo.components.combat != null &&
                        wgo.components.combat.combat_colliders != null)
                    {
                        foreach (var cc in wgo.components.combat.combat_colliders)
                        {
                            if (cc != null) cc.gameObject.SetActive(false);
                        }
                    }
                }

                CoopMod.Logger.LogInfo($"{LogPrefix} Applied WgoDeathAnimation: uid={uid}");
            });
        }

        private static void ApplyRemoteState(Action action)
        {
            bool wasApplying = IsApplyingRemoteState;
            IsApplyingRemoteState = true;
            try
            {
                action();
            }
            finally
            {
                IsApplyingRemoteState = wasApplying;
            }
        }

        private static WorldGameObject FindWgoByUniqueId(long uniqueId)
        {
            if (WGORegistry.Instance != null && WGORegistry.Instance.TryGet(uniqueId, out var indexed))
                return indexed;

            if (WorldMap.objs == null) return null;
            for (int i = 0; i < WorldMap.objs.Count; i++)
            {
                var wgo = WorldMap.objs[i];
                if (wgo != null && wgo.unique_id == uniqueId)
                    return wgo;
            }
            return null;
        }

        private void PruneDropCreateBroadcasts()
        {
            if (recentDropCreateBroadcasts.Count == 0)
                return;

            float cutoff = Time.realtimeSinceStartup - DropCreateBroadcastRememberSeconds;
            var stale = new List<int>();
            foreach (var pair in recentDropCreateBroadcasts)
            {
                if (pair.Value < cutoff)
                    stale.Add(pair.Key);
            }

            for (int i = 0; i < stale.Count; i++)
                recentDropCreateBroadcasts.Remove(stale[i]);
        }

        private void PruneDropCollectBroadcasts()
        {
            if (recentDropCollectBroadcasts.Count == 0)
                return;

            float cutoff = Time.realtimeSinceStartup - DropCollectRememberSeconds;
            var stale = new List<int>();
            foreach (var pair in recentDropCollectBroadcasts)
            {
                if (pair.Value < cutoff)
                    stale.Add(pair.Key);
            }

            for (int i = 0; i < stale.Count; i++)
                recentDropCollectBroadcasts.Remove(stale[i]);
        }

        private void BroadcastMovedDrops()
        {
            if (Time.realtimeSinceStartup - lastDropPositionSyncTime < DropPositionSyncIntervalSeconds)
                return;

            lastDropPositionSyncTime = Time.realtimeSinceStartup;
            if (!IsOnline)
                return;

            List<DropResGameObject> drops = DropsList.me?.drops;
            if (drops == null || drops.Count == 0)
                return;

            int sent = 0;
            float movedSqr = DropPositionMoveDistance * DropPositionMoveDistance;
            var seen = new HashSet<int>();

            for (int i = 0; i < drops.Count && sent < MaxDropPositionsPerTick; i++)
            {
                DropResGameObject drop = drops[i];
                if (drop == null || drop.is_collected || drop.res == null || drop.res.IsEmpty() || drop.res.is_tech_point)
                    continue;

                int instanceId = drop.GetInstanceID();
                seen.Add(instanceId);
                Vector3 pos = GetDropPosition(drop);
                if (!lastDropPositions.TryGetValue(instanceId, out Vector3 previous))
                {
                    lastDropPositions[instanceId] = pos;
                    continue;
                }

                if ((pos - previous).sqrMagnitude < movedSqr)
                    continue;

                lastDropPositions[instanceId] = pos;
                string netId = EnsureDropNetId(drop);
                SendDropPositionEvent(netId, drop.res.id ?? string.Empty, drop.res.value, pos);
                sent++;
            }

            if (lastDropPositions.Count > seen.Count + 16)
            {
                var stale = new List<int>();
                foreach (var pair in lastDropPositions)
                {
                    if (!seen.Contains(pair.Key))
                        stale.Add(pair.Key);
                }

                for (int i = 0; i < stale.Count; i++)
                {
                    lastDropPositions.Remove(stale[i]);
                    if (dropNetIdsByInstance.TryGetValue(stale[i], out string netId))
                    {
                        dropNetIdsByInstance.Remove(stale[i]);
                        dropsByNetId.Remove(netId);
                    }
                }
            }
        }

        private string EnsureDropNetId(DropResGameObject drop)
        {
            if (!IsUsableDrop(drop))
                return string.Empty;

            int instanceId = drop.GetInstanceID();
            if (dropNetIdsByInstance.TryGetValue(instanceId, out string existing) && !string.IsNullOrEmpty(existing))
            {
                RegisterDrop(existing, drop);
                return existing;
            }

            ulong owner = 0UL;
            if (SteamManager.Initialized)
                owner = SteamUser.GetSteamID().m_SteamID;

            string netId = owner.ToString(CultureInfo.InvariantCulture) + ":" +
                (++nextLocalDropNetId).ToString(CultureInfo.InvariantCulture);
            RegisterDrop(netId, drop);
            return netId;
        }

        private void RegisterDrop(string netId, DropResGameObject drop)
        {
            if (!IsUsableDrop(drop))
                return;

            int instanceId = drop.GetInstanceID();
            lastDropPositions[instanceId] = GetDropPosition(drop);

            if (string.IsNullOrEmpty(netId))
                return;

            if (dropNetIdsByInstance.TryGetValue(instanceId, out string previousNetId) &&
                !string.IsNullOrEmpty(previousNetId) &&
                !string.Equals(previousNetId, netId, StringComparison.Ordinal))
            {
                dropsByNetId.Remove(previousNetId);
            }

            dropNetIdsByInstance[instanceId] = netId;
            removedDropNetIds.Remove(netId);
            dropsByNetId[netId] = drop;
        }

        private void RemoveTrackedDrop(string netId, DropResGameObject drop)
        {
            if (drop != null)
            {
                int instanceId = drop.GetInstanceID();
                if (string.IsNullOrEmpty(netId))
                    dropNetIdsByInstance.TryGetValue(instanceId, out netId);

                dropNetIdsByInstance.Remove(instanceId);
                lastDropPositions.Remove(instanceId);
            }

            if (string.IsNullOrEmpty(netId))
                return;

            dropsByNetId.Remove(netId);
            removedDropNetIds.Add(netId);
        }

        private static void FinalizeDropRemoval(
            DropResGameObject drop,
            bool removeFromDropsList)
        {
            if (drop == null)
                return;

            drop.is_collected = true;
            drop.DestroyLinkedHint();

            if (DropResGameObject.currently_higlighted_obj == drop)
            {
                DropResGameObject.currently_higlighted_obj = null;
            }

            if (removeFromDropsList)
            {
                DropsList.me?.drops?.Remove(drop);
                if (drop.gameObject != null)
                {
                    UnityEngine.Object.Destroy(drop.gameObject);
                }
            }
            else if (drop.gameObject != null)
            {
                // Local collection can occur from inside DropsList.UpdateMe.
                // Leave list removal to that loop to avoid mutating its collection
                // while it is indexing it, but hide the object immediately.
                drop.gameObject.SetActive(false);
            }
        }

        private DropResGameObject ResolveDrop(string netId, string itemId, int value, Vector3 pos)
        {
            if (!string.IsNullOrEmpty(netId))
            {
                if (removedDropNetIds.Contains(netId))
                    return null;

                if (dropsByNetId.TryGetValue(netId, out DropResGameObject tracked))
                {
                    if (IsUsableDrop(tracked))
                        return tracked;

                    dropsByNetId.Remove(netId);
                }
            }

            DropResGameObject fallback = FindMatchingDrop(itemId, value, pos, DropFallbackMatchDistance);
            if (fallback != null)
                RegisterDrop(netId, fallback);

            return fallback;
        }

        private static string ReadOptionalString(BinaryReader reader)
        {
            if (reader == null || reader.BaseStream == null || reader.BaseStream.Position >= reader.BaseStream.Length)
                return string.Empty;

            try
            {
                return reader.ReadString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static Item DeserializeDropItem(string itemJson, string fallbackItemId, int fallbackValue)
        {
            if (string.IsNullOrEmpty(itemJson) || itemJson.Length > MaxDropItemJsonLength)
                return new Item(fallbackItemId, fallbackValue);

            try
            {
                Item item = JsonUtility.FromJson<Item>(itemJson);
                if (item == null || item.IsEmpty())
                    return new Item(fallbackItemId, fallbackValue);

                EnsureDropItemLists(item);
                return item;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[CombatSync] Could not deserialize full drop state for '{fallbackItemId}': {ex.Message}");
                return new Item(fallbackItemId, fallbackValue);
            }
        }

        private static void EnsureDropItemLists(Item item)
        {
            if (item == null)
                return;

            if (item.inventory == null)
                item.inventory = new List<Item>();
            if (item.secondary_inventory == null)
                item.secondary_inventory = new List<Item>();

            for (int i = 0; i < item.inventory.Count; i++)
                EnsureDropItemLists(item.inventory[i]);
            for (int i = 0; i < item.secondary_inventory.Count; i++)
                EnsureDropItemLists(item.secondary_inventory[i]);
        }

        private static DropResGameObject FindMatchingDrop(string itemId, int value, Vector3 pos, float maxDistance)
        {
            if (string.IsNullOrEmpty(itemId))
                return null;

            DropResGameObject closest = null;
            float closestSqr = maxDistance * maxDistance;

            List<DropResGameObject> trackedDrops = DropsList.me?.drops;
            if (trackedDrops != null)
            {
                for (int i = 0; i < trackedDrops.Count; i++)
                    ConsiderDrop(trackedDrops[i], itemId, value, pos, ref closest, ref closestSqr);
            }

            if (MainGame.me?.world_root != null)
            {
                var sceneDrops = MainGame.me.world_root.GetComponentsInChildren<DropResGameObject>(true);
                for (int i = 0; i < sceneDrops.Length; i++)
                    ConsiderDrop(sceneDrops[i], itemId, value, pos, ref closest, ref closestSqr);
            }

            return closest;
        }

        private static void ConsiderDrop(
            DropResGameObject drop,
            string itemId,
            int value,
            Vector3 pos,
            ref DropResGameObject closest,
            ref float closestSqr)
        {
            if (drop == null || drop.is_collected || drop.res == null)
                return;
            if (drop.res.id != itemId)
                return;
            if (value > 0 && drop.res.value != value)
                return;

            Vector3 dropPos = GetDropPosition(drop);
            float distSqr = (dropPos - pos).sqrMagnitude;
            if (distSqr < closestSqr)
            {
                closestSqr = distSqr;
                closest = drop;
            }
        }

        private static bool IsUsableDrop(DropResGameObject drop)
        {
            return drop != null && !drop.is_collected && drop.res != null;
        }

        private static Vector3 GetDropPosition(DropResGameObject drop)
        {
            if (drop == null)
                return Vector3.zero;

            try
            {
                return drop.pos;
            }
            catch
            {
                return drop.transform != null ? drop.transform.position : Vector3.zero;
            }
        }
    }
}

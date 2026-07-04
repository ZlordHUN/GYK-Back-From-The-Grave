using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    public class SpawnSync : SyncBehaviour
    {
        public static SpawnSync Instance => GetInstance<SpawnSync>();
        internal static bool IsApplyingCanonicalFlowPlacement { get; private set; }
        internal static bool IsApplyingCanonicalWgoState { get; private set; }

        private const byte PayloadVersion = 2;
        private const int MaxSerializedWgoBytes = 4 * 1024 * 1024;
        private const byte SubWgoSpawned = 0;
        private const byte SubWgoReplaced = 1;
        private const byte SubFlowPlacementRequest = 2;
        private const byte SubCanonicalFlowPlacement = 3;
        private const byte SubWgoReplaceRequest = 4;

        protected override string LogPrefix => "[SpawnSync]";

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

            CoopMod.Logger.LogInfo($"{LogPrefix} Sent WgoSpawned: uid={wgo.unique_id}, obj_id={wgo.obj_id}");
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
        internal void RequestCanonicalFlowPlacement(WorldGameObject wgo, GDPoint gdPoint)
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
                wgo.gameObject.activeSelf);
            SteamP2PManager.Instance?.SendSpawnSyncToHost(payload);

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Requested canonical Flow placement: uid={wgo.unique_id}, " +
                $"obj_id={wgo.obj_id}, gd_point={gdTag}, active={wgo.gameObject.activeSelf}");
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
                            if (onlineCoop.IsHost) return;
                            HandleCanonicalFlowPlacement(reader, senderID);
                            break;
                        case SubWgoReplaceRequest:
                            if (!onlineCoop.IsHost) return;
                            HandleWgoReplaceRequest(reader, senderID);
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

            ApplyFlowPlacement(target, gdPoint, placement.Active);

            byte[] canonicalPayload = SerializeFlowPlacement(
                SubCanonicalFlowPlacement,
                NextSequence++,
                target,
                placement.GdPointTag,
                placement.Active);
            SteamP2PManager.Instance?.BroadcastSpawnSync(canonicalPayload);
            NpcVisualSync.Instance?.BroadcastAuthoritativeStateNow(target);

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Host applied canonical Flow placement from {senderID}: " +
                $"uid={target.unique_id}, obj_id={target.obj_id}, " +
                $"gd_point={placement.GdPointTag}, active={placement.Active}");
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

            ApplyFlowPlacement(target, gdPoint, placement.Active);
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Applied canonical Flow placement: uid={target.unique_id}, " +
                $"obj_id={target.obj_id}, gd_point={placement.GdPointTag}, active={placement.Active}");
        }

        private struct FlowPlacement
        {
            public long UniqueId;
            public string ObjId;
            public string CustomTag;
            public string GdPointTag;
            public bool Active;
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

            return placement.UniqueId != 0L &&
                   !string.IsNullOrEmpty(placement.ObjId) &&
                   !string.IsNullOrEmpty(placement.GdPointTag);
        }

        private static byte[] SerializeFlowPlacement(
            byte subType,
            uint sequence,
            WorldGameObject wgo,
            string gdPointTag,
            bool active)
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
            bool active)
        {
            bool wasApplying = IsApplyingCanonicalFlowPlacement;
            IsApplyingCanonicalFlowPlacement = true;
            try
            {
                target.transform.position = gdPoint.transform.position;
                target.RefreshPositionCache();
                target.gameObject.SetActive(active);
                target.OnCameToGDPoint(gdPoint);
                target.GetComponent<ChunkedGameObject>()?.RecalculateChunk();
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
                WGORegistry.Instance?.Register(target);
                CoopMod.Logger.LogInfo(
                    existing != null
                        ? $"{LogPrefix} Applied spawned WGO state: uid={uid}, obj_id={objId}"
                        : $"{LogPrefix} Created spawned WGO: uid={uid}, obj_id={objId}");
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
}

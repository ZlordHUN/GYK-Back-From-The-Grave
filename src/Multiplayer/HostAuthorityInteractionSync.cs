using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Relays selected high-risk WGO interactions from clients to the host so shared
    /// world mutations happen once on the authoritative machine.
    /// </summary>
    public class HostAuthorityInteractionSync : SyncBehaviour
    {
        public static HostAuthorityInteractionSync Instance => GetInstance<HostAuthorityInteractionSync>();

        private const byte PayloadVersion = 1;
        private const float SendIntervalSeconds = 0.08f;

        public static bool IsApplying { get; private set; }
        /// <summary>
        /// True only while the host applies a remote player's zero-HP world mutation.
        /// Player-bound presentation scripts spawned by that mutation must stay on
        /// the triggering peer.
        /// </summary>
        public static bool IsApplyingZeroHpMutation { get; private set; }

        private readonly Dictionary<long, float> lastSendAt = new Dictionary<long, float>();
        private readonly Dictionary<long, float> pendingDeltaByUid = new Dictionary<long, float>();
        private readonly HashSet<string> hostAuthorityObjectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private MethodInfo interactWithDeltaMethod;

        protected override string LogPrefix => "[HostAuthorityInteractionSync]";

        protected override void OnAwake()
        {
            RefreshAuthorityList();
        }

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnInteractionRequestReceived -= OnInteractionRequestReceived;
                SteamP2PManager.Instance.OnInteractionRequestReceived += OnInteractionRequestReceived;
                SteamP2PManager.Instance.OnInteractionZeroHpReceived -= OnInteractionZeroHpReceived;
                SteamP2PManager.Instance.OnInteractionZeroHpReceived += OnInteractionZeroHpReceived;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnInteractionRequestReceived -= OnInteractionRequestReceived;
                SteamP2PManager.Instance.OnInteractionZeroHpReceived -= OnInteractionZeroHpReceived;
            }
        }

        protected override void OnSyncEnabled()
        {
            if (ModConfig.EnableHostAuthorityInteractions != null && !ModConfig.EnableHostAuthorityInteractions.Value)
            {
                CoopMod.Logger.LogInfo($"{LogPrefix} Disabled by config");
                return;
            }

            RefreshAuthorityList();
            ResetSessionState();
            CoopMod.Logger.LogInfo($"{LogPrefix} Sync enabled for {hostAuthorityObjectIds.Count} object id(s)");
        }

        protected override void OnSyncDisabled()
        {
            IsApplying = false;
            IsApplyingZeroHpMutation = false;
            ResetSessionState();
        }

        public void ResetSessionState()
        {
            lastSendAt.Clear();
            pendingDeltaByUid.Clear();
        }

        public static bool ShouldRelay(WorldGameObject wgo)
        {
            var instance = Instance;
            if (instance == null || !instance.IsSyncEnabled || IsApplying) return false;
            if (WGOStateSync.IsProcessingRemote) return false;
            if (wgo == null || string.IsNullOrEmpty(wgo.obj_id)) return false;
            return instance.hostAuthorityObjectIds.Contains(wgo.obj_id);
        }

        public static bool IsClientRelayActive()
        {
            var onlineCoop = OnlineCoopManager.Instance;
            return onlineCoop != null && onlineCoop.IsOnlineCoopEnabled && !onlineCoop.IsHost;
        }

        public static void RelayLocalInteraction(WorldGameObject wgo, bool interactionStart, float deltaTime)
        {
            var instance = Instance;
            if (instance == null || wgo == null || !ShouldRelay(wgo) || !IsClientRelayActive()) return;

            long uniqueId = wgo.unique_id;
            if (uniqueId == 0L) return;

            instance.SendInteractionRequest(uniqueId, wgo.obj_id, interactionStart, deltaTime);
        }

        public static void RelayZeroHpActivity(WorldGameObject wgo)
        {
            var instance = Instance;
            if (instance == null || wgo == null || !ShouldRelay(wgo) || !IsClientRelayActive()) return;

            long uniqueId = wgo.unique_id;
            if (uniqueId == 0L || string.IsNullOrEmpty(wgo.obj_id)) return;

            byte[] payload = SerializeZeroHp(uniqueId, wgo.obj_id);
            SteamP2PManager.Instance?.SendInteractionZeroHpToHost(payload);
            CoopMod.Logger.LogInfo($"[HostAuthorityInteractionSync] Relayed DoZeroHPActivity uid={uniqueId} obj='{wgo.obj_id}'");
        }

        private void SendInteractionRequest(long uniqueId, string objId, bool interactionStart, float deltaTime)
        {
            float now = Time.realtimeSinceStartup;
            if (!interactionStart)
            {
                pendingDeltaByUid.TryGetValue(uniqueId, out float pendingDelta);
                pendingDelta += Mathf.Max(0f, deltaTime);
                pendingDeltaByUid[uniqueId] = pendingDelta;

                if (lastSendAt.TryGetValue(uniqueId, out float lastAt) && now - lastAt < SendIntervalSeconds) return;

                deltaTime = pendingDelta;
            }
            else
            {
                pendingDeltaByUid[uniqueId] = 0f;
            }

            lastSendAt[uniqueId] = now;
            if (!interactionStart) pendingDeltaByUid[uniqueId] = 0f;

            byte[] payload = SerializeInteraction(uniqueId, objId, interactionStart, deltaTime);
            SteamP2PManager.Instance?.SendInteractionRequestToHost(payload);
            CoopMod.Logger.LogInfo($"[HostAuthorityInteractionSync] Relayed interaction uid={uniqueId} obj='{objId}' start={interactionStart} dt={deltaTime:F3}");
        }

        private void OnInteractionRequestReceived(CSteamID senderID, byte[] payload)
        {
            if (!IsSyncEnabled || !IsHost) return;

            if (!DeserializeInteraction(payload, out long uniqueId, out string objId, out bool interactionStart, out float deltaTime)) return;

            WorldGameObject wgo = ResolveTarget(uniqueId, objId);
            if (wgo == null)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Host target missing for interaction uid={uniqueId} obj='{objId}' from {senderID}");
                return;
            }

            WorldGameObject instigator = MainGame.me?.player;
            if (instigator == null)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Host has no local player WGO for interaction uid={uniqueId} obj='{objId}'");
                return;
            }

            IsApplying = true;
            try
            {
                InvokeInteract(wgo, instigator, interactionStart, deltaTime);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Host interaction apply failed uid={uniqueId} obj='{objId}': {ex.Message}");
            }
            finally
            {
                IsApplying = false;
            }
        }

        private void OnInteractionZeroHpReceived(CSteamID senderID, byte[] payload)
        {
            if (!IsSyncEnabled || !IsHost) return;

            if (!DeserializeZeroHp(payload, out long uniqueId, out string objId)) return;

            WorldGameObject wgo = ResolveTarget(uniqueId, objId);
            if (wgo == null)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Host target missing for zero HP uid={uniqueId} obj='{objId}' from {senderID}");
                return;
            }

            IsApplying = true;
            IsApplyingZeroHpMutation = true;
            try
            {
                wgo.DoZeroHPActivity();
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Host zero HP apply failed uid={uniqueId} obj='{objId}': {ex.Message}");
            }
            finally
            {
                IsApplyingZeroHpMutation = false;
                IsApplying = false;
            }
        }

        private void InvokeInteract(WorldGameObject wgo, WorldGameObject instigator, bool interactionStart, float deltaTime)
        {
            if (interactWithDeltaMethod == null)
            {
                interactWithDeltaMethod = typeof(WorldGameObject).GetMethod(
                    "Interact",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { typeof(WorldGameObject), typeof(bool), typeof(float) },
                    null);
            }

            if (interactWithDeltaMethod != null)
            {
                interactWithDeltaMethod.Invoke(wgo, new object[] { instigator, interactionStart, deltaTime });
            }
            else
            {
                wgo.Interact(instigator, interactionStart);
            }
        }

        private WorldGameObject ResolveTarget(long uniqueId, string objId)
        {
            if (uniqueId == 0L || string.IsNullOrEmpty(objId)) return null;
            if (!hostAuthorityObjectIds.Contains(objId)) return null;

            if (WGORegistry.Instance != null &&
                WGORegistry.Instance.TryGet(uniqueId, out WorldGameObject indexed) &&
                indexed.obj_id == objId)
            {
                return indexed;
            }

            List<WorldGameObject> objects = WGORegistry.Instance?.SnapshotAll() ?? MainGame.me?.GetListOfWorldObjects();
            if (objects == null) return null;

            for (int i = 0; i < objects.Count; i++)
            {
                WorldGameObject wgo = objects[i];
                if (wgo != null && wgo.unique_id == uniqueId && wgo.obj_id == objId)
                    return wgo;
            }

            return null;
        }

        private void RefreshAuthorityList()
        {
            hostAuthorityObjectIds.Clear();
            string raw = ModConfig.HostAuthorityObjectIds?.Value;
            if (string.IsNullOrEmpty(raw))
                raw = "graved_skull";

            string[] parts = raw.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string value = parts[i].Trim();
                if (!string.IsNullOrEmpty(value))
                    hostAuthorityObjectIds.Add(value);
            }
        }

        private static byte[] SerializeInteraction(long uniqueId, string objId, bool interactionStart, float deltaTime)
        {
            using (var stream = new MemoryStream(128))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(PayloadVersion);
                writer.Write(uniqueId);
                writer.Write(objId ?? string.Empty);
                writer.Write(interactionStart);
                writer.Write(deltaTime);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static bool DeserializeInteraction(byte[] payload, out long uniqueId, out string objId, out bool interactionStart, out float deltaTime)
        {
            uniqueId = 0L;
            objId = string.Empty;
            interactionStart = false;
            deltaTime = 0f;

            try
            {
                using (var stream = new MemoryStream(payload))
                using (var reader = new BinaryReader(stream))
                {
                    if (reader.ReadByte() != PayloadVersion) return false;
                    uniqueId = reader.ReadInt64();
                    objId = reader.ReadString();
                    interactionStart = reader.ReadBoolean();
                    deltaTime = reader.ReadSingle();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static byte[] SerializeZeroHp(long uniqueId, string objId)
        {
            using (var stream = new MemoryStream(96))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(PayloadVersion);
                writer.Write(uniqueId);
                writer.Write(objId ?? string.Empty);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static bool DeserializeZeroHp(byte[] payload, out long uniqueId, out string objId)
        {
            uniqueId = 0L;
            objId = string.Empty;

            try
            {
                using (var stream = new MemoryStream(payload))
                using (var reader = new BinaryReader(stream))
                {
                    if (reader.ReadByte() != PayloadVersion) return false;
                    uniqueId = reader.ReadInt64();
                    objId = reader.ReadString();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}

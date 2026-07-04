using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Steamworks;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Synchronizes WorldGameObject destruction between host and clients.
    /// When a WGO is destroyed:
    /// - Client sends destruction request to host
    /// - Host broadcasts destruction to all clients
    /// - All clients destroy the WGO locally
    /// </summary>
    public class WGODestructionSync : SyncBehaviour
    {
        public static WGODestructionSync Instance => GetInstance<WGODestructionSync>();

        private const int MaxLocalDestroyBroadcastsPerWindow = 6;
        private const float LocalDestroyBurstWindowSeconds = 1f;
        private const float LocalDestroyBurstSuppressSeconds = 6f;

        private static bool isProcessingRemoteDestroy = false;
        private readonly Queue<float> recentLocalDestroyBroadcasts = new Queue<float>();
        private float suppressLocalDestroyBroadcastUntil;

        public static bool IsProcessingRemote => isProcessingRemoteDestroy || WGOStateSync.IsProcessingRemote;

        protected override string LogPrefix => "[WGODestructionSync]";

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnWGODestroyReceived -= OnWGODestroyReceived;
                SteamP2PManager.Instance.OnWGODestroyReceived += OnWGODestroyReceived;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnWGODestroyReceived -= OnWGODestroyReceived;
            }
        }

        protected override void OnSyncEnabled()
        {
            recentLocalDestroyBroadcasts.Clear();
            suppressLocalDestroyBroadcastUntil = 0f;
        }

        protected override void OnSyncDisabled()
        {
            recentLocalDestroyBroadcasts.Clear();
            suppressLocalDestroyBroadcastUntil = 0f;
        }

        public void OnLocalWGODestroyed(long uniqueId)
        {
            if (!IsSyncEnabled) return;
            if (isProcessingRemoteDestroy) return;
            if (!IsOnline) return;

            if (!TryAllowLocalDestroyBroadcast(uniqueId)) return;

            if (IsHost)
            {
                SteamP2PManager.Instance?.BroadcastWGODestroy(uniqueId, null);
                CoopMod.Logger.LogDebug($"{LogPrefix} Host broadcasting destroy: {uniqueId}");
            }
            else
            {
                SteamP2PManager.Instance?.SendWGODestroy(uniqueId, toHost: true);
                CoopMod.Logger.LogDebug($"{LogPrefix} Client sending destroy to host: {uniqueId}");
            }
        }

        private bool TryAllowLocalDestroyBroadcast(long uniqueId)
        {
            float now = Time.realtimeSinceStartup;
            if (now < suppressLocalDestroyBroadcastUntil) return false;

            while (recentLocalDestroyBroadcasts.Count > 0 &&
                   now - recentLocalDestroyBroadcasts.Peek() > LocalDestroyBurstWindowSeconds)
            {
                recentLocalDestroyBroadcasts.Dequeue();
            }

            recentLocalDestroyBroadcasts.Enqueue(now);
            if (recentLocalDestroyBroadcasts.Count <= MaxLocalDestroyBroadcastsPerWindow) return true;

            recentLocalDestroyBroadcasts.Clear();
            suppressLocalDestroyBroadcastUntil = now + LocalDestroyBurstSuppressSeconds;
            CoopMod.Logger.LogWarning($"{LogPrefix} Suppressing WGO destroy sync burst after {MaxLocalDestroyBroadcastsPerWindow} destroys in {LocalDestroyBurstWindowSeconds:F1}s; latest uid={uniqueId}");
            return false;
        }

        private void OnWGODestroyReceived(CSteamID senderID, long uniqueId)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;

            CoopMod.Logger.LogDebug($"{LogPrefix} Received destroy for {uniqueId} from {senderID}");

            if (IsHost)
            {
                SteamP2PManager.Instance?.BroadcastWGODestroy(uniqueId, senderID);
                CoopMod.Logger.LogDebug($"{LogPrefix} Host re-broadcasting destroy: {uniqueId}");
            }

            DestroyWGOLocally(uniqueId);
        }

        private void DestroyWGOLocally(long uniqueId)
        {
            try
            {
                isProcessingRemoteDestroy = true;

                if (MainGame.me == null)
                {
                    CoopMod.Logger.LogWarning($"{LogPrefix} MainGame.me is null, cannot destroy WGO");
                    return;
                }

                var worldObjects = WGORegistry.Instance?.SnapshotAll() ?? MainGame.me?.GetListOfWorldObjects();
                if (worldObjects == null)
                {
                    CoopMod.Logger.LogWarning($"{LogPrefix} World objects list is null");
                    return;
                }

                foreach (var wgo in worldObjects)
                {
                    if (wgo != null && wgo.unique_id == uniqueId)
                    {
                        CoopMod.Logger.LogDebug($"{LogPrefix} Destroying WGO locally: {wgo.obj_id} (uid: {uniqueId})");
                        wgo.DestroyMe();
                    }
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Error destroying WGO {uniqueId}: {ex.Message}");
            }
            finally
            {
                isProcessingRemoteDestroy = false;
            }
        }
    }
}

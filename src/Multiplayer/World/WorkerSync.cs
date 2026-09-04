using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    public class WorkerSync : SyncBehaviour
    {
        public static WorkerSync Instance => GetInstance<WorkerSync>();

        private const byte PayloadVersion = 1;
        private const int MaxWorkers = 64;

        internal const byte SubWorkerRegistrySync = 0;
        internal const byte SubWorkerCreated = 1;
        internal const byte SubWorkerRemoved = 2;

        protected override string LogPrefix => "[WorkerSync]";

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnWorkerSyncReceived -= OnWorkerSyncReceived;
                SteamP2PManager.Instance.OnWorkerSyncReceived += OnWorkerSyncReceived;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnWorkerSyncReceived -= OnWorkerSyncReceived;
            }
        }

        protected override void OnSyncEnabled()
        {
            SendWorkerRegistrySync();
        }

        internal void SendWorkerRegistrySync()
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (!IsHost) return;
            if (MainGame.me?.save?.workers == null) return;

            var workersField = typeof(SavedWorkersList).GetField("_workers",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            var workers = workersField?.GetValue(MainGame.me.save.workers) as List<Worker>;
            if (workers == null) return;

            int count = Mathf.Min(workers.Count, MaxWorkers);
            int estimatedSize = 256 + count * 128;
            using (var stream = new MemoryStream(estimatedSize))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubWorkerRegistrySync);
                bw.Write(NextSequence++);

                var lastIdField = typeof(SavedWorkersList).GetField("last_unique_id",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                long lastId = lastIdField != null ? (long)lastIdField.GetValue(MainGame.me.save.workers) : 0L;
                bw.Write(lastId);
                bw.Write((ushort)count);

                for (int i = 0; i < count; i++)
                {
                    var worker = workers[i];
                    bw.Write(worker.id ?? "");
                    bw.Write(worker.worker_unique_id);
                    bool forceK = GetWorkerForceK(worker);
                    bw.Write(forceK);
                    if (forceK)
                    {
                        bw.Write(GetWorkerForcedK(worker));
                    }
                    bw.Write(GetWorkerWgoUniqueId(worker));
                }

                bw.Flush();
                SteamP2PManager.Instance?.BroadcastWorkerSync(stream.ToArray());
            }
            CoopMod.Logger.LogInfo($"{LogPrefix} Sent WorkerRegistrySync: {count} workers, last_id={0}");
        }

        internal void SendWorkerCreated(Worker worker)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (!IsHost) return;

            using (var stream = new MemoryStream(128))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubWorkerCreated);
                bw.Write(NextSequence++);
                bw.Write(worker.id ?? "");
                bw.Write(worker.worker_unique_id);
                bool forceK = GetWorkerForceK(worker);
                bw.Write(forceK);
                if (forceK)
                {
                    bw.Write(GetWorkerForcedK(worker));
                }
                bw.Write(GetWorkerWgoUniqueId(worker));
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastWorkerSync(stream.ToArray());
            }
            CoopMod.Logger.LogInfo($"{LogPrefix} Sent WorkerCreated: id={worker.id}, uid={worker.worker_unique_id}");
        }

        internal void SendWorkerRemoved(long workerUniqueId)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (!IsHost) return;

            using (var stream = new MemoryStream(64))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubWorkerRemoved);
                bw.Write(NextSequence++);
                bw.Write(workerUniqueId);
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastWorkerSync(stream.ToArray());
            }
            CoopMod.Logger.LogInfo($"{LogPrefix} Sent WorkerRemoved: uid={workerUniqueId}");
        }

        private void OnWorkerSyncReceived(CSteamID senderID, byte[] payload)
        {
            if (IsHost) return;

            TryReadPayloadWithSubtype(payload, PayloadVersion, (reader, subType) =>
            {
                switch (subType)
                {
                    case SubWorkerRegistrySync:
                        HandleWorkerRegistrySync(reader, senderID);
                        break;
                    case SubWorkerCreated:
                        HandleWorkerCreated(reader, senderID);
                        break;
                    case SubWorkerRemoved:
                        HandleWorkerRemoved(reader, senderID);
                        break;
                    default:
                        CoopMod.Logger.LogWarning($"{LogPrefix} Unknown sub-type {subType}");
                        break;
                }
            });
        }

        private void HandleWorkerRegistrySync(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            if (MainGame.me?.save?.workers == null) return;

            long lastId = reader.ReadInt64();
            int count = reader.ReadUInt16();

            var lastIdField = typeof(SavedWorkersList).GetField("last_unique_id",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (lastIdField != null)
            {
                lastIdField.SetValue(MainGame.me.save.workers, lastId);
            }

            var workersField = typeof(SavedWorkersList).GetField("_workers",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            var workers = workersField?.GetValue(MainGame.me.save.workers) as List<Worker>;
            if (workers == null) return;

            workers.Clear();
            for (int i = 0; i < count; i++)
            {
                string id = reader.ReadString();
                long workerUid = reader.ReadInt64();
                bool forceK = reader.ReadBoolean();
                float forcedK = 0f;
                if (forceK)
                {
                    forcedK = reader.ReadSingle();
                }
                long wgoUid = reader.ReadInt64();

                var worker = new Worker();
                worker.id = id;
                worker.worker_unique_id = workerUid;
                SetWorkerForceK(worker, forceK, forcedK);
                SetWorkerWgoUniqueId(worker, wgoUid);
                workers.Add(worker);
            }

            CoopMod.Logger.LogInfo($"{LogPrefix} Applied WorkerRegistrySync: {count} workers, last_id={lastId}");
        }

        private void HandleWorkerCreated(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            if (MainGame.me?.save?.workers == null) return;

            string id = reader.ReadString();
            long workerUid = reader.ReadInt64();
            bool forceK = reader.ReadBoolean();
            float forcedK = 0f;
            if (forceK)
            {
                forcedK = reader.ReadSingle();
            }
            long wgoUid = reader.ReadInt64();

            var workersField = typeof(SavedWorkersList).GetField("_workers",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            var workers = workersField?.GetValue(MainGame.me.save.workers) as List<Worker>;
            if (workers == null) return;

            var worker = new Worker();
            worker.id = id;
            worker.worker_unique_id = workerUid;
            SetWorkerForceK(worker, forceK, forcedK);
            SetWorkerWgoUniqueId(worker, wgoUid);
            workers.Add(worker);

            var lastIdField = typeof(SavedWorkersList).GetField("last_unique_id",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (lastIdField != null)
            {
                long currentLastId = (long)lastIdField.GetValue(MainGame.me.save.workers);
                if (workerUid > currentLastId)
                {
                    lastIdField.SetValue(MainGame.me.save.workers, workerUid);
                }
            }

            CoopMod.Logger.LogInfo($"{LogPrefix} Applied WorkerCreated: id={id}, uid={workerUid}");
        }

        private void HandleWorkerRemoved(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            if (MainGame.me?.save?.workers == null) return;

            long workerUid = reader.ReadInt64();

            var workersField = typeof(SavedWorkersList).GetField("_workers",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            var workers = workersField?.GetValue(MainGame.me.save.workers) as List<Worker>;
            if (workers == null) return;

            for (int i = workers.Count - 1; i >= 0; i--)
            {
                if (workers[i].worker_unique_id == workerUid)
                {
                    workers.RemoveAt(i);
                    CoopMod.Logger.LogInfo($"{LogPrefix} Removed worker uid={workerUid}");
                    break;
                }
            }
        }

        private static void SetWorkerForceK(Worker worker, bool forceK, float forcedK)
        {
            if (forceK)
            {
                worker.ForcingWorkerK(true, forcedK);
            }
        }

        private static void SetWorkerWgoUniqueId(Worker worker, long wgoUid)
        {
            var field = typeof(Worker).GetField("_wgo_unique_id",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (field != null)
            {
                field.SetValue(worker, wgoUid);
            }
        }

        internal static bool GetWorkerForceK(Worker worker)
        {
            var field = typeof(Worker).GetField("_force_worker_k",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            return field != null && (bool)field.GetValue(worker);
        }

        internal static float GetWorkerForcedK(Worker worker)
        {
            var field = typeof(Worker).GetField("_forced_worker_k",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            return field != null ? (float)field.GetValue(worker) : 1f;
        }

        internal static long GetWorkerWgoUniqueId(Worker worker)
        {
            var field = typeof(Worker).GetField("_wgo_unique_id",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            return field != null ? (long)field.GetValue(worker) : 0L;
        }
    }
}

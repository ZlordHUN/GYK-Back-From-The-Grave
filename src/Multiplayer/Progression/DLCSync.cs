using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Steamworks;
using UnityEngine;
using DLCRefugees;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    public class DLCSync : PeriodicSyncBehaviour
    {
        public static DLCSync Instance => GetInstance<DLCSync>();

        private const byte PayloadVersion = 1;
        private const int MaxRefugeeEntries = 32;
        private const int MaxVisitorEntries = 16;
        private const int MaxLockEntries = 16;

        internal const byte SubRefugeeCampSync = 0;
        internal const byte SubTavernEngineSync = 1;

        private string lastRefugeeFingerprint = string.Empty;
        private string lastTavernFingerprint = string.Empty;

        protected override string LogPrefix => "[DLCSync]";
        protected override float SyncIntervalSeconds => 10f;

        protected override void OnPeriodicSyncEnabled()
        {
            lastRefugeeFingerprint = string.Empty;
            lastTavernFingerprint = string.Empty;

            if (!ModConfig.EnableDLCRefugees.Value || !DLCEngine.IsDLCAvailable(DLCEngine.DLCVersion.Refugees))
                CoopMod.Logger.LogInfo($"{LogPrefix} Refugee camp sync disabled (config or DLC not installed)");
            if (!ModConfig.EnableDLCStories.Value || !DLCEngine.IsDLCAvailable(DLCEngine.DLCVersion.Stories))
                CoopMod.Logger.LogInfo($"{LogPrefix} Tavern engine sync disabled (config or DLC not installed)");

            if (ModConfig.EnableDLCRefugees.Value && DLCEngine.IsDLCAvailable(DLCEngine.DLCVersion.Refugees))
                SendRefugeeCampSync(force: true);
            if (ModConfig.EnableDLCStories.Value && DLCEngine.IsDLCAvailable(DLCEngine.DLCVersion.Stories))
                SendTavernEngineSync(force: true);
        }

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnDLCSyncReceived -= OnDLCSyncReceived;
                SteamP2PManager.Instance.OnDLCSyncReceived += OnDLCSyncReceived;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnDLCSyncReceived -= OnDLCSyncReceived;
            }
        }

        protected override bool ShouldUpdate() =>
            MainGame.me != null && MainGame.game_started && MainGame.me.save != null;

        protected override bool ShouldSend() =>
            MainGame.me != null && MainGame.game_started && MainGame.me.save != null && IsHost;


        protected override byte[] CaptureSnapshot(out string fingerprint)
        {
            fingerprint = "";
            return null;
        }

        protected override void BroadcastPayload(byte[] payload)
        {
            SteamP2PManager.Instance?.BroadcastDLCSync(payload);
        }

        public override void SendLocalSnapshot(bool force)
        {
            if (!IsOnline) return;
            if (!ShouldSend()) return;

            SendRefugeeCampSync(force);
            SendTavernEngineSync(force);
        }

        internal void SendRefugeeCampSync(bool force)
        {
            if (!IsSyncEnabled) return;
            if (!ModConfig.EnableDLCRefugees.Value) return;
            if (!IsOnline) return;
            if (!IsHost) return;
            if (MainGame.me?.save?.refugees_camp_data == null) return;

            byte[] payload;
            string fingerprint;

            try
            {
                payload = CaptureRefugeeCamp(out fingerprint);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Failed to capture refugee camp: {ex.Message}");
                return;
            }

            if (payload == null || payload.Length == 0) return;
            if (!force && fingerprint == lastRefugeeFingerprint) return;

            lastRefugeeFingerprint = fingerprint;

            SteamP2PManager.Instance?.BroadcastDLCSync(payload);
        }

        internal void SendTavernEngineSync(bool force)
        {
            if (!IsSyncEnabled) return;
            if (!ModConfig.EnableDLCStories.Value) return;
            if (!IsOnline) return;
            if (!IsHost) return;
            if (MainGame.me?.save?.players_tavern_engine == null) return;

            byte[] payload;
            string fingerprint;

            try
            {
                payload = CaptureTavernEngine(out fingerprint);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Failed to capture tavern engine: {ex.Message}");
                return;
            }

            if (payload == null || payload.Length == 0) return;
            if (!force && fingerprint == lastTavernFingerprint) return;

            lastTavernFingerprint = fingerprint;

            SteamP2PManager.Instance?.BroadcastDLCSync(payload);
        }

        private byte[] CaptureRefugeeCamp(out string fingerprint)
        {
            var sb = new StringBuilder();
            var data = MainGame.me.save.refugees_camp_data;

            using (var stream = new MemoryStream(512))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubRefugeeCampSync);
                bw.Write(NextSequence++);

                bw.Write(data.is_camp_living);
                sb.Append("live:").Append(data.is_camp_living).Append(';');
                bw.Write(data.camp_was_started_at_once);
                sb.Append("started:").Append(data.camp_was_started_at_once).Append(';');
                bw.Write((byte)data.camp_music);
                sb.Append("music:").Append((int)data.camp_music).Append(';');
                bw.Write((byte)data.camp_music_alarich);
                sb.Append("musicA:").Append((int)data.camp_music_alarich).Append(';');

                int activeCount = Mathf.Min(data.active_refugee_list?.Count ?? 0, MaxRefugeeEntries);
                bw.Write((ushort)activeCount);
                for (int i = 0; i < activeCount; i++)
                {
                    var refugee = data.active_refugee_list[i];
                    bw.Write(refugee.unique_id);
                    bw.Write(refugee.home_gd_point_tag ?? "");
                    sb.Append("r:").Append(refugee.unique_id).Append(',').Append(refugee.home_gd_point_tag ?? "").Append(';');
                }

                int spawnedCount = Mathf.Min(data.refugee_already_spawned_list?.Count ?? 0, MaxRefugeeEntries);
                bw.Write((ushort)spawnedCount);
                for (int i = 0; i < spawnedCount; i++)
                {
                    var info = data.refugee_already_spawned_list[i];
                    bw.Write(info.obj_id ?? "");
                    bw.Write(info.custom_tag ?? "");
                }

                int toSpawnCount = Mathf.Min(data.refugee_to_spawn_ordered_list?.Count ?? 0, MaxRefugeeEntries);
                bw.Write((ushort)toSpawnCount);
                for (int i = 0; i < toSpawnCount; i++)
                {
                    var info = data.refugee_to_spawn_ordered_list[i];
                    bw.Write(info.obj_id ?? "");
                    bw.Write(info.custom_tag ?? "");
                }

                bw.Flush();
                fingerprint = sb.ToString();
                return stream.ToArray();
            }
        }

        private byte[] CaptureTavernEngine(out string fingerprint)
        {
            var sb = new StringBuilder();
            var engine = MainGame.me.save.players_tavern_engine;

            var availPointsField = typeof(PlayersTavernEngine).GetField("available_idle_points",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            var visitorsIdsField = typeof(PlayersTavernEngine).GetField("visitors_unique_ids",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

            var availPoints = availPointsField?.GetValue(engine) as List<int> ?? new List<int>();
            var visitorIds = visitorsIdsField?.GetValue(engine) as List<long> ?? new List<long>();

            using (var stream = new MemoryStream(512))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubTavernEngineSync);
                bw.Write(NextSequence++);

                bw.Write(engine.visitors_temporarily_removed);
                sb.Append("tempRem:").Append(engine.visitors_temporarily_removed).Append(';');

                int pointCount = Mathf.Min(availPoints.Count, MaxLockEntries);
                bw.Write((ushort)pointCount);
                for (int i = 0; i < pointCount; i++)
                {
                    bw.Write(availPoints[i]);
                    sb.Append("p:").Append(availPoints[i]).Append(';');
                }

                int visitorCount = Mathf.Min(visitorIds.Count, MaxVisitorEntries);
                bw.Write((ushort)visitorCount);
                for (int i = 0; i < visitorCount; i++)
                {
                    bw.Write(visitorIds[i]);
                    sb.Append("v:").Append(visitorIds[i]).Append(';');
                }

                int lockCount = Mathf.Min(engine.locks?.Count ?? 0, MaxLockEntries);
                bw.Write((ushort)lockCount);
                for (int i = 0; i < lockCount; i++)
                {
                    var loc = engine.locks[i];
                    bw.Write(loc.num);
                    bw.Write(loc.locker_unique_id);
                    sb.Append("l:").Append(loc.num).Append(',').Append(loc.locker_unique_id).Append(';');
                }

                bw.Flush();
                fingerprint = sb.ToString();
                return stream.ToArray();
            }
        }

        private void OnDLCSyncReceived(CSteamID senderID, byte[] payload)
        {
            TryReadPayloadWithSubtype(payload, PayloadVersion, (reader, subType) =>
            {
                switch (subType)
                {
                    case SubRefugeeCampSync:
                        if (ModConfig.EnableDLCRefugees.Value)
                            HandleRefugeeCampSync(reader, senderID);
                        else
                            CoopMod.Logger.LogWarning($"{LogPrefix} Received RefugeeCampSync but DLC Refugees is disabled in config");
                        break;
                    case SubTavernEngineSync:
                        if (ModConfig.EnableDLCStories.Value)
                            HandleTavernEngineSync(reader, senderID);
                        else
                            CoopMod.Logger.LogWarning($"{LogPrefix} Received TavernEngineSync but DLC Stories is disabled in config");
                        break;
                    default:
                        CoopMod.Logger.LogWarning($"{LogPrefix} Unknown sub-type {subType}");
                        break;
                }
            });
        }

        private void HandleRefugeeCampSync(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            if (MainGame.me?.save?.refugees_camp_data == null) return;

            ApplyEchoSuppress();
            ForceNextSend = true;

            var data = MainGame.me.save.refugees_camp_data;

            data.is_camp_living = reader.ReadBoolean();
            data.camp_was_started_at_once = reader.ReadBoolean();
            data.camp_music = (RefugeeCampMusic)reader.ReadByte();
            data.camp_music_alarich = (RefugeeCampMusicAlarich)reader.ReadByte();

            int activeCount = reader.ReadUInt16();
            data.active_refugee_list.Clear();
            for (int i = 0; i < activeCount; i++)
            {
                long uid = reader.ReadInt64();
                string homeTag = reader.ReadString();
                var refugee = new Refugee();
                var uidField = typeof(Refugee).GetField("_unique_id",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                var tagField = typeof(Refugee).GetField("_home_gd_point_tag",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (uidField != null) uidField.SetValue(refugee, uid);
                if (tagField != null) tagField.SetValue(refugee, homeTag);
                data.active_refugee_list.Add(refugee);
            }

            int spawnedCount = reader.ReadUInt16();
            data.refugee_already_spawned_list.Clear();
            for (int i = 0; i < spawnedCount; i++)
            {
                var info = new RefugeeInfo(reader.ReadString(), reader.ReadString());
                data.refugee_already_spawned_list.Add(info);
            }

            int toSpawnCount = reader.ReadUInt16();
            data.refugee_to_spawn_ordered_list.Clear();
            for (int i = 0; i < toSpawnCount; i++)
            {
                var info = new RefugeeInfo(reader.ReadString(), reader.ReadString());
                data.refugee_to_spawn_ordered_list.Add(info);
            }

            data.Init();
            CoopMod.Logger.LogInfo($"{LogPrefix} Applied RefugeeCampSync: live={data.is_camp_living}, active={activeCount}, spawned={spawnedCount}, toSpawn={toSpawnCount}");
        }

        private void HandleTavernEngineSync(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            if (MainGame.me?.save?.players_tavern_engine == null) return;

            ApplyEchoSuppress();
            ForceNextSend = true;

            var engine = MainGame.me.save.players_tavern_engine;

            engine.visitors_temporarily_removed = reader.ReadBoolean();

            var availPointsField = typeof(PlayersTavernEngine).GetField("available_idle_points",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            var visitorsIdsField = typeof(PlayersTavernEngine).GetField("visitors_unique_ids",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

            int pointCount = reader.ReadUInt16();
            var availPoints = availPointsField?.GetValue(engine) as List<int> ?? new List<int>();
            availPoints.Clear();
            for (int i = 0; i < pointCount; i++)
            {
                availPoints.Add(reader.ReadInt32());
            }
            if (availPointsField != null) availPointsField.SetValue(engine, availPoints);

            int visitorCount = reader.ReadUInt16();
            var visitorIds = visitorsIdsField?.GetValue(engine) as List<long> ?? new List<long>();
            visitorIds.Clear();
            for (int i = 0; i < visitorCount; i++)
            {
                visitorIds.Add(reader.ReadInt64());
            }
            if (visitorsIdsField != null) visitorsIdsField.SetValue(engine, visitorIds);

            int lockCount = reader.ReadUInt16();
            engine.locks.Clear();
            for (int i = 0; i < lockCount; i++)
            {
                int num = reader.ReadInt32();
                long lockerUid = reader.ReadInt64();
                var loc = new PlayersTavernEngine.GDPointLock();
                loc.num = num;
                loc.locker_unique_id = lockerUid;
                engine.locks.Add(loc);
            }

            engine.Init();
            CoopMod.Logger.LogInfo($"{LogPrefix} Applied TavernEngineSync: tempRemoved={engine.visitors_temporarily_removed}, points={pointCount}, visitors={visitorCount}, locks={lockCount}");
        }
    }
}

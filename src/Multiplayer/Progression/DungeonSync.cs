using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Steamworks;
using UnityEngine;
using DungeonGenerator;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    public class DungeonSync : SyncBehaviour
    {
        public static DungeonSync Instance => GetInstance<DungeonSync>();

        private const byte PayloadVersion = 1;
        private const int MaxDungeonObjects = 512;

        internal const byte SubDungeonSeedSync = 0;
        internal const byte SubDungeonLevelState = 1;
        internal const byte SubDungeonEnter = 2;
        internal const byte SubDungeonExit = 3;
        internal const byte SubDungeonMobKill = 4;
        internal const byte SubDungeonObjectInteract = 5;
        internal const byte SubDungeonLevelCompleted = 6;

        protected override string LogPrefix => "[DungeonSync]";

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnDungeonSyncReceived -= OnDungeonSyncReceived;
                SteamP2PManager.Instance.OnDungeonSyncReceived += OnDungeonSyncReceived;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnDungeonSyncReceived -= OnDungeonSyncReceived;
            }
        }

        protected override void OnSyncEnabled()
        {
            SendDungeonSeedSync();
        }

        internal void SendDungeonSeedSync()
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (!IsHost) return;
            if (MainGame.me?.save == null) return;

            using (var stream = new MemoryStream(64))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubDungeonSeedSync);
                bw.Write(NextSequence++);
                bw.Write(MainGame.me.save.dungeon_seed);
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastDungeonSync(stream.ToArray());
            }
            CoopMod.Logger.LogInfo($"{LogPrefix} Sent DungeonSeedSync: seed={MainGame.me.save.dungeon_seed}");
        }

        internal void SendDungeonLevelState(int dungeonLevel)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (!IsHost) return;
            if (MainGame.me?.save == null) return;

            var savedDungeon = MainGame.me.save.dungeons.GetSavedDungeon(dungeonLevel);
            if (savedDungeon == null) return;

            byte[] payload = CaptureSavedDungeon(dungeonLevel, savedDungeon);
            if (payload == null) return;
            SteamP2PManager.Instance?.BroadcastDungeonSync(payload);
        }

        internal void SendDungeonEnter(int dungeonLevel)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (!IsHost) return;

            using (var stream = new MemoryStream(64))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubDungeonEnter);
                bw.Write(NextSequence++);
                bw.Write(dungeonLevel);
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastDungeonSync(stream.ToArray());
            }

            SendDungeonLevelState(dungeonLevel);
            CoopMod.Logger.LogInfo($"{LogPrefix} Sent DungeonEnter: level={dungeonLevel}");
        }

        internal void SendDungeonExit()
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (!IsHost) return;

            using (var stream = new MemoryStream(16))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubDungeonExit);
                bw.Write(NextSequence++);
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastDungeonSync(stream.ToArray());
            }
            CoopMod.Logger.LogInfo($"{LogPrefix} Sent DungeonExit");
        }

        internal void SendDungeonMobKill(string mobName, float localPosX, float localPosY)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (!IsHost) return;

            using (var stream = new MemoryStream(128))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubDungeonMobKill);
                bw.Write(NextSequence++);
                bw.Write(mobName ?? "");
                bw.Write(localPosX);
                bw.Write(localPosY);
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastDungeonSync(stream.ToArray());
            }
        }

        internal void SendDungeonObjectInteract(string objId, float localPosX, float localPosY)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (!IsHost) return;

            using (var stream = new MemoryStream(128))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubDungeonObjectInteract);
                bw.Write(NextSequence++);
                bw.Write(objId ?? "");
                bw.Write(localPosX);
                bw.Write(localPosY);
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastDungeonSync(stream.ToArray());
            }
        }

        internal void SendDungeonLevelCompleted(int dungeonLevel)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (!IsHost) return;

            using (var stream = new MemoryStream(64))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubDungeonLevelCompleted);
                bw.Write(NextSequence++);
                bw.Write(dungeonLevel);
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastDungeonSync(stream.ToArray());
            }
            CoopMod.Logger.LogInfo($"{LogPrefix} Sent LevelCompleted: level={dungeonLevel}");
        }

        private byte[] CaptureSavedDungeon(int dungeonLevel, SavedDungeon savedDungeon)
        {
            int estimatedSize = 256 + (savedDungeon.objects?.Count ?? 0) * 64;
            using (var stream = new MemoryStream(estimatedSize))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubDungeonLevelState);
                bw.Write(NextSequence++);
                bw.Write(dungeonLevel);
                bw.Write(savedDungeon.is_empty);
                bw.Write(savedDungeon.dungeon_preset_name ?? "");
                bw.Write(savedDungeon.seed);
                bw.Write(savedDungeon.random_calls_count);
                bw.Write(savedDungeon.is_completed);

                int objCount = Mathf.Min(savedDungeon.objects?.Count ?? 0, MaxDungeonObjects);
                bw.Write((ushort)objCount);
                for (int i = 0; i < objCount; i++)
                {
                    var obj = savedDungeon.objects[i];
                    bw.Write(obj.name ?? "");
                    bw.Write(obj.local_position.x);
                    bw.Write(obj.local_position.y);
                    bw.Write((byte)obj.type);
                    bw.Write(obj.type == SavedDungeonObject.SavedDungeonObjectType.Mob ? obj.mob_is_alive : true);
                }

                bw.Flush();
                return stream.ToArray();
            }
        }

        private void OnDungeonSyncReceived(CSteamID senderID, byte[] payload)
        {
            if (!IsSyncEnabled) return;
            if (payload == null || payload.Length == 0) return;
            if (!IsOnline) return;
            if (IsHost) return;

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
                        case SubDungeonSeedSync:
                            HandleDungeonSeedSync(reader, senderID);
                            break;
                        case SubDungeonLevelState:
                            HandleDungeonLevelState(reader, senderID);
                            break;
                        case SubDungeonEnter:
                            HandleDungeonEnter(reader, senderID);
                            break;
                        case SubDungeonExit:
                            HandleDungeonExit(reader, senderID);
                            break;
                        case SubDungeonMobKill:
                            HandleDungeonMobKill(reader, senderID);
                            break;
                        case SubDungeonObjectInteract:
                            HandleDungeonObjectInteract(reader, senderID);
                            break;
                        case SubDungeonLevelCompleted:
                            HandleDungeonLevelCompleted(reader, senderID);
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

        private void HandleDungeonSeedSync(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            int seed = reader.ReadInt32();
            if (MainGame.me?.save != null)
            {
                MainGame.me.save.dungeon_seed = seed;
                MainGame.me.save.dungeons.SetGlobalSeed(seed);
                CoopMod.Logger.LogInfo($"{LogPrefix} Applied dungeon seed: {seed}");
            }
        }

        private void HandleDungeonLevelState(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            int dungeonLevel = reader.ReadInt32();
            bool isEmpty = reader.ReadBoolean();
            string presetName = reader.ReadString();
            int seed = reader.ReadInt32();
            int randomCallsCount = reader.ReadInt32();
            bool isCompleted = reader.ReadBoolean();

            if (MainGame.me?.save == null) return;

            var savedDungeon = MainGame.me.save.dungeons.GetSavedDungeon(dungeonLevel);
            savedDungeon.is_empty = isEmpty;
            savedDungeon.dungeon_preset_name = presetName;
            savedDungeon.seed = seed;
            savedDungeon.random_calls_count = randomCallsCount;
            savedDungeon.is_completed = isCompleted;

            int objCount = reader.ReadUInt16();
            savedDungeon.objects.Clear();
            for (int i = 0; i < objCount; i++)
            {
                var obj = new SavedDungeonObject();
                obj.name = reader.ReadString();
                float posX = reader.ReadSingle();
                float posY = reader.ReadSingle();
                obj.local_position = new Vector2(posX, posY);
                byte typeByte = reader.ReadByte();
                obj.type = (SavedDungeonObject.SavedDungeonObjectType)typeByte;
                bool mobIsAlive = reader.ReadBoolean();
                if (obj.type == SavedDungeonObject.SavedDungeonObjectType.Mob)
                {
                    obj.mob_is_alive = mobIsAlive;
                }
                savedDungeon.objects.Add(obj);
            }

            CoopMod.Logger.LogInfo($"{LogPrefix} Applied dungeon level state: level={dungeonLevel}, completed={isCompleted}, objects={objCount}");
        }

        private void HandleDungeonEnter(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            int dungeonLevel = reader.ReadInt32();
            CoopMod.Logger.LogInfo($"{LogPrefix} Host entered dungeon level {dungeonLevel}");
        }

        private void HandleDungeonExit(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            CoopMod.Logger.LogInfo($"{LogPrefix} Host exited dungeon");
        }

        private void HandleDungeonMobKill(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            string mobName = reader.ReadString();
            float posX = reader.ReadSingle();
            float posY = reader.ReadSingle();

            if (MainGame.me?.dungeon_root?.dungeon_is_loaded_now != true) return;
            if (MainGame.me?.dungeon_root?.cur_saved_dungeon == null) return;

            var savedDungeon = MainGame.me.dungeon_root.cur_saved_dungeon;
            for (int i = savedDungeon.objects.Count - 1; i >= 0; i--)
            {
                var obj = savedDungeon.objects[i];
                if (obj.type == SavedDungeonObject.SavedDungeonObjectType.Mob
                    && obj.name == mobName
                    && Mathf.Approximately(obj.local_position.x, posX)
                    && Mathf.Approximately(obj.local_position.y, posY))
                {
                    obj.mob_is_alive = false;
                    savedDungeon.objects[i] = obj;
                    CoopMod.Logger.LogInfo($"{LogPrefix} Marked mob as dead: {mobName} at ({posX:F1},{posY:F1})");
                    break;
                }
            }

            MainGame.me.dungeon_root.UpdateDungeonState();
        }

        private void HandleDungeonObjectInteract(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            string objId = reader.ReadString();
            float posX = reader.ReadSingle();
            float posY = reader.ReadSingle();

            if (MainGame.me?.dungeon_root?.dungeon_is_loaded_now != true) return;
            if (MainGame.me?.dungeon_root?.cur_saved_dungeon == null) return;

            var savedDungeon = MainGame.me.dungeon_root.cur_saved_dungeon;
            bool found = false;
            for (int i = 0; i < savedDungeon.objects.Count; i++)
            {
                var obj = savedDungeon.objects[i];
                if (obj.name == objId
                    && Mathf.Approximately(obj.local_position.x, posX)
                    && Mathf.Approximately(obj.local_position.y, posY))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                var newObj = new SavedDungeonObject();
                newObj.name = objId;
                newObj.local_position = new Vector2(posX, posY);
                newObj.type = SavedDungeonObject.SavedDungeonObjectType.WGO;
                savedDungeon.objects.Add(newObj);
                CoopMod.Logger.LogInfo($"{LogPrefix} Added interacted dungeon object: {objId} at ({posX:F1},{posY:F1})");
            }
        }

        private void HandleDungeonLevelCompleted(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            int dungeonLevel = reader.ReadInt32();
            if (MainGame.me?.save == null) return;

            var savedDungeon = MainGame.me.save.dungeons.GetSavedDungeon(dungeonLevel);
            savedDungeon.is_completed = true;
            CoopMod.Logger.LogInfo($"{LogPrefix} Marked dungeon level {dungeonLevel} as completed");
        }
    }
}

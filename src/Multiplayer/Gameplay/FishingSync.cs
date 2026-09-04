using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    public class FishingSync : PeriodicSyncBehaviour
    {
        public static FishingSync Instance => GetInstance<FishingSync>();

        private const byte PayloadVersion = 1;
        private const int MaxFishEntries = 64;
        private const int MaxBaitEntries = 32;

        internal const byte SubFishCaught = 0;
        internal const byte SubFishDiscoverySync = 1;
        internal const byte SubLastBaitSync = 2;

        private string lastFishFingerprint = string.Empty;

        protected override string LogPrefix => "[FishingSync]";
        protected override float SyncIntervalSeconds => 10f;

        protected override void OnPeriodicSyncEnabled()
        {
            lastFishFingerprint = string.Empty;
        }

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnFishingSyncReceived -= OnFishingSyncReceived;
                SteamP2PManager.Instance.OnFishingSyncReceived += OnFishingSyncReceived;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnFishingSyncReceived -= OnFishingSyncReceived;
            }
        }

        protected override bool ShouldUpdate() =>
            MainGame.me != null && MainGame.game_started && MainGame.me.save != null;

        protected override bool ShouldSend() =>
            MainGame.me != null && MainGame.game_started && MainGame.me.save != null && IsHost;


        internal void SendFishCaught(string fishId, string fishClearName)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (!IsHost) return;

            using (var stream = new MemoryStream(256))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubFishCaught);
                bw.Write(NextSequence++);
                bw.Write(fishId ?? "");
                bw.Write(fishClearName ?? "");

                string filletCraftId = "";
                if (fishClearName != null)
                {
                    filletCraftId = fishClearName.Contains("frog")
                        ? "raw_meat_sliced_from_fish_frog_green"
                        : (fishClearName + "_fillet");
                }
                bw.Write(filletCraftId ?? "");
                bw.Flush();

                SteamP2PManager.Instance?.BroadcastFishingSync(stream.ToArray());
            }
            CoopMod.Logger.LogInfo($"{LogPrefix} Sent FishCaught: id={fishId}, clear={fishClearName}");
        }

        protected override byte[] CaptureSnapshot(out string fingerprint)
        {
            var save = MainGame.me.save;

            var sb = new StringBuilder();
            int knownCount = Mathf.Min(save.known_fishes?.Count ?? 0, MaxFishEntries);
            int clearCount = Mathf.Min(save.known_fishes_clear?.Count ?? 0, MaxFishEntries);

            using (var stream = new MemoryStream(512 + (knownCount + clearCount) * 64))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubFishDiscoverySync);
                bw.Write(NextSequence++);
                bw.Write((ushort)knownCount);
                for (int i = 0; i < knownCount; i++)
                {
                    bw.Write(save.known_fishes[i] ?? "");
                    sb.Append(save.known_fishes[i]).Append(';');
                }
                bw.Write((ushort)clearCount);
                for (int i = 0; i < clearCount; i++)
                {
                    bw.Write(save.known_fishes_clear[i] ?? "");
                    sb.Append(save.known_fishes_clear[i]).Append(';');
                }
                bw.Flush();

                fingerprint = sb.ToString();
                return stream.ToArray();
            }
        }

        protected override void BroadcastPayload(byte[] payload)
        {
            SteamP2PManager.Instance?.BroadcastFishingSync(payload);
        }

        private void OnFishingSyncReceived(CSteamID senderID, byte[] payload)
        {
            TryReadPayloadWithSubtype(payload, PayloadVersion, (reader, subType) =>
            {
                switch (subType)
                {
                    case SubFishCaught:
                        HandleFishCaught(reader, senderID);
                        break;
                    case SubFishDiscoverySync:
                        HandleFishDiscoverySync(reader, senderID);
                        break;
                    case SubLastBaitSync:
                        HandleLastBaitSync(reader, senderID);
                        break;
                    default:
                        CoopMod.Logger.LogWarning($"{LogPrefix} Unknown sub-type {subType}");
                        break;
                }
            });
        }

        private void HandleFishCaught(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            string fishId = reader.ReadString();
            string fishClearName = reader.ReadString();
            string filletCraftId = reader.ReadString();

            CoopMod.Logger.LogInfo($"{LogPrefix} Applied FishCaught: id={fishId}, clear={fishClearName}");
        }

        private void HandleFishDiscoverySync(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            var save = MainGame.me?.save;
            if (save == null) return;

            int knownCount = reader.ReadUInt16();
            for (int i = 0; i < knownCount; i++)
            {
                var fish = reader.ReadString();
                if (!save.known_fishes.Contains(fish))
                    save.known_fishes.Add(fish);
            }

            int clearCount = reader.ReadUInt16();
            for (int i = 0; i < clearCount; i++)
            {
                var fish = reader.ReadString();
                if (!save.known_fishes_clear.Contains(fish))
                    save.known_fishes_clear.Add(fish);
            }

            CoopMod.Logger.LogInfo($"{LogPrefix} Applied FishDiscoverySync: known={knownCount}, clear={clearCount}");
        }

        private void HandleLastBaitSync(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            var save = MainGame.me?.save;
            if (save == null) return;

            int baitCount = reader.ReadUInt16();
            save.last_bait_reservoirs.Clear();
            save.last_bait_baits.Clear();
            for (int i = 0; i < baitCount; i++)
            {
                save.last_bait_reservoirs.Add(reader.ReadString());
                save.last_bait_baits.Add(reader.ReadString());
            }

            CoopMod.Logger.LogInfo($"{LogPrefix} Applied LastBaitSync: count={baitCount}");
        }
    }
}

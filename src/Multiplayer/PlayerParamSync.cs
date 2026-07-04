using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    public class PlayerParamSync : PeriodicSyncBehaviour
    {
        public static PlayerParamSync Instance => GetInstance<PlayerParamSync>();

        private const byte PayloadVersion = 1;
        private const int MaxParams = 2048;
        private const float MinimumForcedSendIntervalSeconds = 1f;
        private static readonly HashSet<string> LocalOnlyParams =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "speed",
                "lock_tp",
                "lock_tp_param"
            };

        private float nextForcedSendAllowedAt;

        protected override string LogPrefix => "[PlayerParamSync]";
        protected override float SyncIntervalSeconds => 5f;

        protected override void OnPeriodicSyncEnabled()
        {
            nextForcedSendAllowedAt = 0f;
        }

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnPlayerParamSyncReceived -= OnPlayerParamSyncReceived;
                SteamP2PManager.Instance.OnPlayerParamSyncReceived += OnPlayerParamSyncReceived;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnPlayerParamSyncReceived -= OnPlayerParamSyncReceived;
            }
        }

        protected override bool ShouldUpdate() =>
            MainGame.me != null && MainGame.game_started && MainGame.me.player != null;

        protected override bool ShouldSend() =>
            MainGame.me != null && MainGame.game_started && MainGame.me.player != null;

        public override void SendLocalSnapshot(bool force)
        {
            if (!IsHost)
            {
                // PlayerParamSync is host-canonical. Client-side mutations are
                // covered by PlayerParitySync and must not leave ForceNextSend
                // hot forever.
                ForceNextSend = false;
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (force && now < nextForcedSendAllowedAt)
                return;

            base.SendLocalSnapshot(force);
            nextForcedSendAllowedAt =
                now + MinimumForcedSendIntervalSeconds;
        }

        protected override byte[] CaptureSnapshot(out string fingerprint)
        {
            var sb = new StringBuilder();
            var playerData = MainGame.me.player.data;
            var gameRes = playerData.GetParams();

            var atoms = gameRes.ToAtomList(1f);
            var sharedAtoms = new List<GameResAtom>(atoms.Count);
            for (int i = 0; i < atoms.Count && sharedAtoms.Count < MaxParams; i++)
            {
                if (!LocalOnlyParams.Contains(atoms[i].type ?? ""))
                    sharedAtoms.Add(atoms[i]);
            }
            int count = sharedAtoms.Count;

            using (var stream = new MemoryStream(4096 + count * 16))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(NextSequence++);

                bw.Write(playerData.hp);
                sb.Append("hp:").Append(playerData.hp.ToString("F2")).Append(';');
                bw.Write(playerData.progress);
                sb.Append("prog:").Append(playerData.progress.ToString("F2")).Append(';');
                bw.Write(playerData.money);
                sb.Append("money:").Append(playerData.money.ToString("F2")).Append(';');
                bw.Write(playerData.durability);
                sb.Append("dur:").Append(playerData.durability.ToString("F4")).Append(';');

                bw.Write((ushort)count);
                for (int i = 0; i < count; i++)
                {
                    bw.Write(sharedAtoms[i].type ?? "");
                    bw.Write(sharedAtoms[i].value);
                    if (i < 50)
                    {
                        sb.Append(sharedAtoms[i].type).Append(':').Append(sharedAtoms[i].value.ToString("F2")).Append(';');
                    }
                }

                bw.Flush();
                fingerprint = sb.ToString();
                return stream.ToArray();
            }
        }

        protected override void BroadcastPayload(byte[] payload)
        {
            SteamP2PManager.Instance?.BroadcastPlayerParamSync(payload);
        }

        private void OnPlayerParamSyncReceived(CSteamID senderID, byte[] payload)
        {
            TryReadPayload(payload, PayloadVersion, reader =>
            {
                if (!CheckSequence(reader, senderID)) return;
                ApplySnapshot(reader);
            });
        }

        private void ApplySnapshot(BinaryReader reader)
        {
            if (MainGame.me?.player?.data == null) return;

            var playerData = MainGame.me.player.data;

            playerData.hp = reader.ReadSingle();
            playerData.progress = reader.ReadSingle();
            playerData.money = reader.ReadSingle();
            playerData.durability = reader.ReadSingle();

            int count = reader.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                string key = reader.ReadString();
                float value = reader.ReadSingle();
                if (LocalOnlyParams.Contains(key))
                    continue;
                playerData.SetParam(key, value);
            }

            ApplyEchoSuppress();

            CoopMod.Logger.LogDebug($"{LogPrefix} Applied {count} player params + core fields");
        }
    }
}

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
                "lock_tp_param",
                "money",
                "hp",
                "energy",
                "tiredness",
                "tired",
                "r",
                "g",
                "b",
                "v",
                "gratitude_points"
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
            var gameRes = playerData.GetParams().Clone();
            PersonalBuffState.SubtractContributions(
                gameRes,
                MainGame.me?.save?.buffs);

            var atoms = gameRes.ToAtomList(1f);
            var sharedAtoms = new List<GameResAtom>(atoms.Count);
            for (int i = 0; i < atoms.Count && sharedAtoms.Count < MaxParams; i++)
            {
                string type = atoms[i].type ?? string.Empty;
                if (!LocalOnlyParams.Contains(type))
                {
                    sharedAtoms.Add(atoms[i]);
                }
            }
            int count = sharedAtoms.Count;

            using (var stream = new MemoryStream(4096 + count * 16))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(NextSequence++);

                float sharedProgress = gameRes.Get("progress", playerData.progress);
                float sharedDurability = gameRes.Get("durability", playerData.durability);
                // Retain current HP in the v1 wire layout for mixed-build parsing.
                // Updated receivers deliberately ignore it because health is personal.
                bw.Write(playerData.hp);
                bw.Write(sharedProgress);
                sb.Append("prog:").Append(sharedProgress.ToString("F2")).Append(';');
                // Retain this field in the v1 wire layout for mixed-build compatibility.
                // Money is personal state and receivers deliberately do not apply it.
                bw.Write(playerData.money);
                bw.Write(sharedDurability);
                sb.Append("dur:").Append(sharedDurability.ToString("F4")).Append(';');

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
            GameRes personalEffectContributions =
                PersonalBuffState.CalculateContributions(
                    MainGame.me?.save?.buffs);

            reader.ReadSingle(); // Legacy shared-HP field; health is per-player.
            playerData.progress = reader.ReadSingle() +
                                  personalEffectContributions.Get("progress", 0f);
            reader.ReadSingle(); // Legacy shared-money field; money is per-player.
            playerData.durability = reader.ReadSingle() +
                                    personalEffectContributions.Get("durability", 0f);

            int count = reader.ReadUInt16();
            int relationshipsAdvanced = 0;
            for (int i = 0; i < count; i++)
            {
                string key = reader.ReadString();
                float value = reader.ReadSingle();
                if (LocalOnlyParams.Contains(key))
                    continue;

                float effectiveValue = value +
                    personalEffectContributions.Get(key, 0f);

                if (IsSharedRelationshipParam(key))
                {
                    float current = playerData.GetParam(key, 0f);
                    if (effectiveValue > current + 0.0001f)
                    {
                        playerData.SetParam(key, effectiveValue);
                        relationshipsAdvanced++;
                        CoopMod.Logger.LogInfo(
                            $"{LogPrefix} Advanced shared relationship {key}: {current:F0} -> {effectiveValue:F0}");
                    }
                    continue;
                }

                playerData.SetParam(key, effectiveValue);
            }

            if (relationshipsAdvanced > 0)
                RefreshRelationshipUi();

            ApplyEchoSuppress();

            CoopMod.Logger.LogDebug($"{LogPrefix} Applied {count} player params + core fields");
        }

        private static bool IsSharedRelationshipParam(string key)
        {
            return !string.IsNullOrEmpty(key) &&
                   key.StartsWith("_rel_", StringComparison.OrdinalIgnoreCase);
        }

        internal static void RefreshRelationshipUi()
        {
            try
            {
                RelationGUI relation = GUIElements.me?.relation;
                if (relation != null && relation.gameObject.activeInHierarchy)
                    relation.RedrawRelation();

                RelationGUI additional = GUIElements.me?.relation_additional;
                if (additional != null && additional.gameObject.activeInHierarchy)
                    additional.RedrawRelation();
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogDebug(
                    $"[PlayerParamSync] Relationship UI refresh skipped: {ex.Message}");
            }
        }
    }
}

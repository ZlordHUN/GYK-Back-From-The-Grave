using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    public class ZoneNavSync : PeriodicSyncBehaviour
    {
        public static ZoneNavSync Instance => GetInstance<ZoneNavSync>();

        private const byte PayloadVersion = 1;
        private const int MaxZones = 128;
        private const int MaxGDPoints = 2048;

        internal const byte SubFullSync = 0;
        internal const byte SubZoneDiscovered = 1;
        internal const byte SubGDPointState = 2;
        internal const byte SubZoneEnabled = 3;
        internal const byte SubTeleportLock = 4;
        internal const byte SubGDZoneEvent = 5;

        protected override string LogPrefix => "[ZoneNavSync]";
        protected override float SyncIntervalSeconds => 10f;

        private bool isApplyingRemoteTeleportLock;
        private bool isApplyingRemoteZoneState;
        private bool hasLastSentTeleportLock;
        private bool lastSentTeleportLocked;
        private int lastSentTeleportLockParam;

        internal bool IsApplyingRemoteTeleportLock => isApplyingRemoteTeleportLock;
        internal bool IsApplyingRemoteZoneState => isApplyingRemoteZoneState;

        protected override void OnPeriodicSyncEnabled()
        {
            ResetTeleportLockSendState();
        }

        protected override void OnSyncDisabled()
        {
            isApplyingRemoteTeleportLock = false;
            isApplyingRemoteZoneState = false;
            ResetTeleportLockSendState();
        }

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnZoneNavSyncReceived -= OnZoneNavSyncReceived;
                SteamP2PManager.Instance.OnZoneNavSyncReceived += OnZoneNavSyncReceived;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnZoneNavSyncReceived -= OnZoneNavSyncReceived;
            }
        }

        protected override bool ShouldUpdate() =>
            MainGame.me != null && MainGame.game_started && MainGame.me.save != null;

        protected override bool ShouldSend() =>
            MainGame.me != null && MainGame.game_started && MainGame.me.save != null;


        internal void SendZoneDiscoveredEvent(string zoneId)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;

            using (var stream = new MemoryStream(128))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubZoneDiscovered);
                bw.Write(NextSequence++);
                bw.Write(zoneId ?? "");
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastZoneNavSync(stream.ToArray());
            }
            CoopMod.Logger.LogInfo($"{LogPrefix} Sent ZoneDiscovered: zone={zoneId}");
        }

        internal void SendGDPointStateEvent(string gdTag, bool enabled)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;

            using (var stream = new MemoryStream(128))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubGDPointState);
                bw.Write(NextSequence++);
                bw.Write(gdTag ?? "");
                bw.Write(enabled);
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastZoneNavSync(stream.ToArray());
            }
            CoopMod.Logger.LogInfo($"{LogPrefix} Sent GDPointState: tag={gdTag}, enabled={enabled}");
        }

        internal void SendZoneEnabledEvent(string zoneId, bool enabled)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;

            using (var stream = new MemoryStream(128))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubZoneEnabled);
                bw.Write(NextSequence++);
                bw.Write(zoneId ?? "");
                bw.Write(enabled);
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastZoneNavSync(stream.ToArray());
            }
            CoopMod.Logger.LogInfo($"{LogPrefix} Sent ZoneEnabled: zone={zoneId}, enabled={enabled}");
        }

        internal void SendTeleportLockEvent(bool locked, int lockParam)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (isApplyingRemoteTeleportLock) return;

            if (hasLastSentTeleportLock && lastSentTeleportLocked == locked && lastSentTeleportLockParam == lockParam)
            {
                return;
            }

            hasLastSentTeleportLock = true;
            lastSentTeleportLocked = locked;
            lastSentTeleportLockParam = lockParam;
            ForceNextSend = true;

            using (var stream = new MemoryStream(64))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubTeleportLock);
                bw.Write(NextSequence++);
                bw.Write(locked);
                bw.Write(lockParam);
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastZoneNavSync(stream.ToArray());
            }
            CoopMod.Logger.LogInfo($"{LogPrefix} Sent TeleportLock: locked={locked}, param={lockParam}");
        }

        private void ResetTeleportLockSendState()
        {
            hasLastSentTeleportLock = false;
            lastSentTeleportLocked = false;
            lastSentTeleportLockParam = 0;
        }

        internal void SendGDZoneEvent(string zoneName, byte eventType, string ovrMusic)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;

            using (var stream = new MemoryStream(256))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubGDZoneEvent);
                bw.Write(NextSequence++);
                bw.Write(zoneName ?? "");
                bw.Write(eventType);
                bw.Write(ovrMusic ?? "");
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastZoneNavSync(stream.ToArray());
            }
            CoopMod.Logger.LogInfo($"{LogPrefix} Sent GDZoneEvent: zone={zoneName}, type={eventType}");
        }

        protected override byte[] CaptureSnapshot(out string fingerprint)
        {
            var sb = new StringBuilder();
            var save = MainGame.me.save;

            using (var stream = new MemoryStream(8192))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubFullSync);
                bw.Write(NextSequence++);

                WriteStringList(bw, save.known_world_zones, sb);
                WriteGDPointList(bw, sb);

                bool tpLocked = false;
                int tpLockParam = 0;
                if (MainGame.me.player != null && MainGame.me.player.data != null)
                {
                    tpLocked = MainGame.me.player.data.GetParam("lock_tp", 0f) > 0.5f;
                    tpLockParam = (int)MainGame.me.player.data.GetParam("lock_tp_param", 0f);
                }
                bw.Write(tpLocked);
                bw.Write(tpLockParam);
                sb.Append("tp:").Append(tpLocked ? 1 : 0).Append(',').Append(tpLockParam).Append(';');

                bw.Flush();
                fingerprint = sb.ToString();
                return stream.ToArray();
            }
        }

        protected override void BroadcastPayload(byte[] payload)
        {
            SteamP2PManager.Instance?.BroadcastZoneNavSync(payload);
        }

        private static void WriteStringList(BinaryWriter bw, List<string> list, StringBuilder sb)
        {
            if (list == null)
            {
                bw.Write((ushort)0);
                return;
            }
            int count = Mathf.Min(list.Count, MaxZones);
            bw.Write((ushort)count);
            sb.Append("zones:").Append(count).Append(';');
            for (int i = 0; i < count; i++)
            {
                bw.Write(list[i] ?? "");
            }
        }

        private static void WriteGDPointList(BinaryWriter bw, StringBuilder sb)
        {
            var gdPoints = WorldMap.gd_points;
            if (gdPoints == null)
            {
                bw.Write((ushort)0);
                return;
            }
            int count = Mathf.Min(gdPoints.Count, MaxGDPoints);
            bw.Write((ushort)count);
            sb.Append("gdp:").Append(count).Append(';');
            for (int i = 0; i < count; i++)
            {
                var gdp = gdPoints[i];
                bw.Write(gdp.gd_tag ?? "");
                bw.Write(gdp.gameObject.activeSelf);
            }
        }

        private void OnZoneNavSyncReceived(CSteamID senderID, byte[] payload)
        {
            TryReadPayloadWithSubtype(payload, PayloadVersion, (reader, subType) =>
            {
                switch (subType)
                {
                    case SubFullSync:
                        HandleFullSync(reader, senderID);
                        break;
                    case SubZoneDiscovered:
                        HandleZoneDiscovered(reader, senderID);
                        break;
                    case SubGDPointState:
                        HandleGDPointState(reader, senderID);
                        break;
                    case SubZoneEnabled:
                        HandleZoneEnabled(reader, senderID);
                        break;
                    case SubTeleportLock:
                        HandleTeleportLock(reader, senderID);
                        break;
                    case SubGDZoneEvent:
                        HandleGDZoneEvent(reader, senderID);
                        break;
                    default:
                        CoopMod.Logger.LogWarning($"{LogPrefix} Unknown sub-type {subType}");
                        break;
                }
            });
        }

        private void HandleFullSync(BinaryReader reader, CSteamID senderID)
        {
            if (MainGame.me == null || MainGame.me.save == null) return;
            if (!CheckSequence(reader, senderID)) return;

            var save = MainGame.me.save;

            try
            {
                ApplyKnownZones(reader, save);
                ApplyGDPointStates(reader);
                ApplyTeleportLock(reader);

                ApplyEchoSuppress();
                ForceNextSend = true;

                CoopMod.Logger.LogInfo($"{LogPrefix} Applied full zone/nav snapshot");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Error applying full sync: {ex.Message}");
            }
        }

        private static void ApplyKnownZones(BinaryReader reader, GameSave save)
        {
            int count = reader.ReadUInt16();
            if (save.known_world_zones == null)
                save.known_world_zones = new List<string>();

            for (int i = 0; i < count; i++)
            {
                string zoneId = reader.ReadString();
                if (!save.known_world_zones.Contains(zoneId))
                {
                    save.known_world_zones.Add(zoneId);
                }
            }
        }

        private static void ApplyGDPointStates(BinaryReader reader)
        {
            int count = reader.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                string gdTag = reader.ReadString();
                bool enabled = reader.ReadBoolean();

                var gdp = FindGDPointByTagQuiet(gdTag);
                if (gdp != null && gdp.gameObject != null)
                {
                    if (gdp.gameObject.activeSelf != enabled)
                    {
                        gdp.gameObject.SetActive(enabled);
                    }
                }
            }
        }

        private void ApplyTeleportLock(BinaryReader reader)
        {
            bool locked = reader.ReadBoolean();
            int lockParam = reader.ReadInt32();
            ApplyTeleportLockValues(locked, lockParam, logApplied: false);
        }

        private void ApplyTeleportLockValues(bool locked, int lockParam, bool logApplied)
        {
            var playerData = MainGame.me?.player?.data;
            if (playerData == null) return;

            bool currentLocked = playerData.GetParam("lock_tp", 0f) > 0.5f;
            int currentLockParam = (int)playerData.GetParam("lock_tp_param", 0f);
            if (currentLocked == locked && currentLockParam == lockParam) return;

            isApplyingRemoteTeleportLock = true;
            try
            {
                if (currentLocked != locked)
                    playerData.SetParam("lock_tp", locked ? 1f : 0f);
                if (currentLockParam != lockParam)
                    playerData.SetParam("lock_tp_param", (float)lockParam);
            }
            finally
            {
                isApplyingRemoteTeleportLock = false;
            }

            if (logApplied)
                CoopMod.Logger.LogInfo($"{LogPrefix} Applied TeleportLock: locked={locked}, param={lockParam}");
        }

        private void HandleZoneDiscovered(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            string zoneId = reader.ReadString();

            if (MainGame.me?.save?.known_world_zones != null)
            {
                if (!MainGame.me.save.known_world_zones.Contains(zoneId))
                {
                    MainGame.me.save.known_world_zones.Add(zoneId);
                    CoopMod.Logger.LogInfo($"{LogPrefix} Applied ZoneDiscovered: zone={zoneId}");
                }
            }
        }

        private void HandleGDPointState(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            string gdTag = reader.ReadString();
            bool enabled = reader.ReadBoolean();

            var gdp = FindGDPointByTagQuiet(gdTag);
            if (gdp != null && gdp.gameObject != null)
            {
                if (gdp.gameObject.activeSelf != enabled)
                {
                    gdp.gameObject.SetActive(enabled);
                    CoopMod.Logger.LogInfo($"{LogPrefix} Applied GDPointState: tag={gdTag}, enabled={enabled}");
                }
            }
        }

        private void HandleZoneEnabled(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            string zoneId = reader.ReadString();
            bool enabled = reader.ReadBoolean();

            var zone = WorldZone.GetZoneByID(zoneId, true);
            if (zone != null)
            {
                if (IsWorldZoneEnabled(zone) == enabled) return;

                isApplyingRemoteZoneState = true;
                try
                {
                    if (enabled)
                        zone.EnableWorldZone();
                    else
                        zone.DisableWorldZone();
                }
                finally
                {
                    isApplyingRemoteZoneState = false;
                }
                CoopMod.Logger.LogInfo($"{LogPrefix} Applied ZoneEnabled: zone={zoneId}, enabled={enabled}");
            }
        }

        private void HandleTeleportLock(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            bool locked = reader.ReadBoolean();
            int lockParam = reader.ReadInt32();

            ApplyTeleportLockValues(locked, lockParam, logApplied: true);
        }

        private void HandleGDZoneEvent(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            string zoneName = reader.ReadString();
            byte eventType = reader.ReadByte();
            string ovrMusic = reader.ReadString();

            if (MainGame.me?.world_root == null) return;

            var gdZones = MainGame.me.world_root.GetComponentsInChildren<GDZone>(true);
            if (gdZones == null) return;

            GDZone targetZone = null;
            foreach (var zone in gdZones)
            {
                if (zone != null && zone.name == zoneName)
                {
                    targetZone = zone;
                    break;
                }
            }

            if (targetZone == null)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} GDZone not found: {zoneName}");
                return;
            }

            if (!string.IsNullOrEmpty(ovrMusic))
            {
                SmartAudioEngine.me.PlayOvrMusic(ovrMusic);
            }

            CoopMod.Logger.LogInfo($"{LogPrefix} Applied GDZoneEvent: zone={zoneName}, type={eventType}");
        }

        private static GDPoint FindGDPointByTagQuiet(string gdTag)
        {
            if (string.IsNullOrEmpty(gdTag)) return null;

            var gdPoints = WorldMap.gd_points;
            if (gdPoints == null) return null;

            for (int i = 0; i < gdPoints.Count; i++)
            {
                GDPoint gdPoint = gdPoints[i];
                if (gdPoint == null) continue;
                if (gdPoint.gd_tag == gdTag && !gdPoint.IsDisabled())
                    return gdPoint;
            }

            return null;
        }

        private static bool IsWorldZoneEnabled(WorldZone zone)
        {
            if (zone == null) return false;

            Collider2D collider = zone.GetComponent<Collider2D>();
            if (collider != null) return collider.enabled;

            Collider2D[] colliders = zone.GetComponentsInChildren<Collider2D>(true);
            if (colliders == null || colliders.Length == 0) return true;

            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null && colliders[i].enabled)
                    return true;
            }

            return false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    public class ZoneNavSync : PeriodicSyncBehaviour
    {
        public static ZoneNavSync Instance => GetInstance<ZoneNavSync>();

        private const byte PayloadVersion = 2;
        private const int MaxZones = 128;
        private const int MaxGDPoints = 2048;
        private const int MaxFullSnapshotBytes = 256 * 1024;
        private const int MaxCompressedSnapshotBytes = 128 * 1024;
        private const float RecoverySnapshotSeconds = 30f;
        private const ulong FnvOffset64 = 14695981039346656037UL;
        private const ulong FnvPrime64 = 1099511628211UL;

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
        private readonly Dictionary<string, List<GDPoint>> gdPointsByTag =
            new Dictionary<string, List<GDPoint>>(StringComparer.Ordinal);
        private List<GDPoint> indexedGDPoints;
        private int indexedGDPointCount = -1;
        private Transform indexedWorldRoot;
        private bool hasGDPointIndex;
        private float nextRecoverySnapshotAt;
        private bool dropNextHostZoneDiscoveryForTest;
        private bool dropNextHostFullSnapshotForTest;
        private string pendingRecoveryTestZoneId;
        private float pendingRecoveryTestStartedAt;

        internal bool IsApplyingRemoteTeleportLock => isApplyingRemoteTeleportLock;
        internal bool IsApplyingRemoteZoneState => isApplyingRemoteZoneState;

        protected override void OnPeriodicSyncEnabled()
        {
            ResetTeleportLockSendState();
            InvalidateLookupCaches();
            ResetZoneRecoveryTest();
            nextRecoverySnapshotAt = Time.realtimeSinceStartup + RecoverySnapshotSeconds;
        }

        protected override void OnSyncDisabled()
        {
            isApplyingRemoteTeleportLock = false;
            isApplyingRemoteZoneState = false;
            ResetTeleportLockSendState();
            InvalidateLookupCaches();
            ResetZoneRecoveryTest();
            nextRecoverySnapshotAt = 0f;
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

        public override void SendLocalSnapshot(bool force)
        {
            float now = Time.realtimeSinceStartup;
            bool recoveryDue = IsHost && now >= nextRecoverySnapshotAt;
            base.SendLocalSnapshot(force || recoveryDue);
            if (IsHost && (force || recoveryDue))
                nextRecoverySnapshotAt = now + RecoverySnapshotSeconds;
        }

        internal bool ArmZoneRecoveryTest(out string error)
        {
            if (!IsSyncEnabled || !IsOnline)
            {
                error = "Zone recovery testing requires an active multiplayer session.";
                return false;
            }
            if (IsHost)
            {
                error = "Run /test zone-recovery on a client, not on the host.";
                return false;
            }

            ResetZoneRecoveryTest();
            dropNextHostZoneDiscoveryForTest = true;
            error = null;
            CoopMod.Logger.LogWarning(
                $"{LogPrefix} TEST ARMED: the next host ZoneDiscovered event and its " +
                "immediate full snapshot will be dropped");
            return true;
        }

        private void ResetZoneRecoveryTest()
        {
            dropNextHostZoneDiscoveryForTest = false;
            dropNextHostFullSnapshotForTest = false;
            pendingRecoveryTestZoneId = null;
            pendingRecoveryTestStartedAt = 0f;
        }


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
            if (IsHost) ForceNextSend = true;
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
            if (IsHost) ForceNextSend = true;
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

            byte[] snapshotBody;
            using (var bodyStream = new MemoryStream(8192))
            using (var bodyWriter = new BinaryWriter(bodyStream, Encoding.UTF8))
            {
                WriteStringList(bodyWriter, save.known_world_zones, sb);
                WriteGDPointList(bodyWriter, sb);

                bool tpLocked = false;
                int tpLockParam = 0;
                if (MainGame.me.player != null && MainGame.me.player.data != null)
                {
                    tpLocked = MainGame.me.player.data.GetParam("lock_tp", 0f) > 0.5f;
                    tpLockParam = (int)MainGame.me.player.data.GetParam("lock_tp_param", 0f);
                }
                bodyWriter.Write(tpLocked);
                bodyWriter.Write(tpLockParam);
                sb.Append("tp:").Append(tpLocked ? 1 : 0).Append(',').Append(tpLockParam).Append(';');

                bodyWriter.Flush();
                snapshotBody = bodyStream.ToArray();
            }

            if (snapshotBody.Length > MaxFullSnapshotBytes)
                throw new InvalidDataException($"full snapshot is too large ({snapshotBody.Length} bytes)");

            byte[] compressed = CompressSnapshot(snapshotBody);
            if (compressed.Length > MaxCompressedSnapshotBytes)
                throw new InvalidDataException($"compressed snapshot is too large ({compressed.Length} bytes)");

            using (var stream = new MemoryStream(compressed.Length + 16))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubFullSync);
                bw.Write(NextSequence++);
                bw.Write(snapshotBody.Length);
                bw.Write(compressed.Length);
                bw.Write(compressed);

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
                sb.Append("zones:0:").Append(FnvOffset64).Append(';');
                return;
            }
            int count = Mathf.Min(list.Count, MaxZones);
            bw.Write((ushort)count);
            ulong hash = FnvOffset64;
            for (int i = 0; i < count; i++)
            {
                string zoneId = list[i] ?? "";
                bw.Write(zoneId);
                HashString(ref hash, zoneId);
            }
            sb.Append("zones:").Append(count).Append(':').Append(hash).Append(';');
        }

        private static void WriteGDPointList(BinaryWriter bw, StringBuilder sb)
        {
            var gdPoints = WorldMap.gd_points;
            if (gdPoints == null)
            {
                ulong emptyLayoutHash = ComputeGDPointLayoutHash(null, 0);
                bw.Write((ushort)0);
                bw.Write(emptyLayoutHash);
                bw.Write((ushort)0);
                bw.Write(0);
                sb.Append("gdp:0:").Append(emptyLayoutHash)
                    .Append(':').Append(FnvOffset64).Append(';');
                return;
            }

            int count = Mathf.Min(gdPoints.Count, MaxGDPoints);
            bw.Write((ushort)count);
            ulong layoutHash = ComputeGDPointLayoutHash(gdPoints, count);
            bw.Write(layoutHash);

            var stateBits = new byte[(count + 7) / 8];
            ulong stateHash = FnvOffset64;
            byte[] fallback;
            using (var fallbackStream = new MemoryStream(count * 16))
            using (var fallbackWriter = new BinaryWriter(fallbackStream, Encoding.UTF8))
            {
                for (int i = 0; i < count; i++)
                {
                    GDPoint gdp = gdPoints[i];
                    bool enabled = gdp != null && gdp.gameObject != null &&
                                   gdp.gameObject.activeSelf;
                    if (enabled)
                        stateBits[i >> 3] |= (byte)(1 << (i & 7));

                    fallbackWriter.Write(gdp?.gd_tag ?? "");
                    fallbackWriter.Write(enabled);
                    HashByte(ref stateHash, enabled ? (byte)1 : (byte)0);
                }
                fallbackWriter.Flush();
                fallback = fallbackStream.ToArray();
            }

            bw.Write((ushort)stateBits.Length);
            bw.Write(stateBits);
            bw.Write(fallback.Length);
            bw.Write(fallback);
            sb.Append("gdp:").Append(count).Append(':').Append(layoutHash)
                .Append(':').Append(stateHash).Append(';');
        }

        private static byte[] CompressSnapshot(byte[] snapshot)
        {
            using (var output = new MemoryStream(snapshot.Length / 2))
            {
                using (var deflate = new DeflateStream(
                           output,
                           System.IO.Compression.CompressionLevel.Fastest,
                           leaveOpen: true))
                {
                    deflate.Write(snapshot, 0, snapshot.Length);
                }
                return output.ToArray();
            }
        }

        private static ulong ComputeGDPointLayoutHash(List<GDPoint> gdPoints, int count)
        {
            ulong hash = FnvOffset64;
            HashInt32(ref hash, count);
            for (int i = 0; i < count; i++)
            {
                GDPoint gdp = gdPoints[i];
                HashString(ref hash, gdp?.gd_tag ?? "");
                HashString(ref hash, gdp != null ? gdp.name ?? "" : "");
            }
            return hash;
        }

        private static void HashString(ref ulong hash, string value)
        {
            value = value ?? "";
            HashInt32(ref hash, value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                HashByte(ref hash, (byte)c);
                HashByte(ref hash, (byte)(c >> 8));
            }
        }

        private static void HashInt32(ref ulong hash, int value)
        {
            HashByte(ref hash, (byte)value);
            HashByte(ref hash, (byte)(value >> 8));
            HashByte(ref hash, (byte)(value >> 16));
            HashByte(ref hash, (byte)(value >> 24));
        }

        private static void HashByte(ref ulong hash, byte value)
        {
            hash ^= value;
            hash *= FnvPrime64;
        }

        private void OnZoneNavSyncReceived(CSteamID senderID, byte[] payload)
        {
            TryReadPayloadWithSubtype(payload, PayloadVersion, (reader, subType) =>
            {
                switch (subType)
                {
                    case SubFullSync:
                        if (!TryDropImmediateFullSnapshotForRecoveryTest(reader, senderID))
                            HandleFullSync(reader, senderID);
                        break;
                    case SubZoneDiscovered:
                        if (!TryDropZoneDiscoveredForRecoveryTest(reader, senderID))
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
                byte[] snapshot = ReadCompressedSnapshot(reader);
                bool knownZonesChanged;
                int changedGDPoints;
                bool usedIndexedLayout;
                using (var stream = new MemoryStream(snapshot, writable: false))
                using (var snapshotReader = new BinaryReader(stream, Encoding.UTF8))
                {
                    knownZonesChanged = ApplyKnownZones(snapshotReader, save);
                    changedGDPoints = ApplyGDPointStates(
                        snapshotReader,
                        out usedIndexedLayout);
                    ApplyTeleportLock(snapshotReader);
                    if (stream.Position != stream.Length)
                    {
                        throw new InvalidDataException(
                            $"full snapshot has {stream.Length - stream.Position} trailing bytes");
                    }
                }

                if (knownZonesChanged)
                    Patches.MapGUIPatches.RefreshKnownZoneVisibility();

                CompleteZoneRecoveryTestIfRecovered(save, senderID);

                ApplyEchoSuppress();
                if (IsHost) ForceNextSend = true;

                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Applied full zone/nav snapshot " +
                    $"(gd_changed={changedGDPoints}, " +
                    $"gd_mode={(usedIndexedLayout ? "indexed" : "tag_fallback")})");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Error applying full sync: {ex.Message}");
            }
        }

        private bool TryDropZoneDiscoveredForRecoveryTest(
            BinaryReader reader,
            CSteamID senderID)
        {
            if (!dropNextHostZoneDiscoveryForTest || !IsExpectedHost(senderID))
                return false;
            if (!CheckSequence(reader, senderID))
                return true;

            string zoneId = reader.ReadString();
            var knownZones = MainGame.me?.save?.known_world_zones;
            if (knownZones != null && knownZones.Contains(zoneId))
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} TEST SKIP: host reported already-known zone='{zoneId}'; " +
                    "the recovery test remains armed for the next unknown zone.");
                return true;
            }

            dropNextHostZoneDiscoveryForTest = false;
            pendingRecoveryTestZoneId = zoneId;
            pendingRecoveryTestStartedAt = Time.realtimeSinceStartup;
            dropNextHostFullSnapshotForTest = true;
            CoopMod.Logger.LogWarning(
                $"{LogPrefix} TEST DROP: ignored host ZoneDiscovered zone='{zoneId}'. " +
                "The live map must remain unchanged until snapshot recovery.");
            return true;
        }

        private bool TryDropImmediateFullSnapshotForRecoveryTest(
            BinaryReader reader,
            CSteamID senderID)
        {
            if (!dropNextHostFullSnapshotForTest ||
                string.IsNullOrEmpty(pendingRecoveryTestZoneId) ||
                !IsExpectedHost(senderID))
            {
                return false;
            }
            if (!CheckSequence(reader, senderID))
                return true;

            dropNextHostFullSnapshotForTest = false;
            CoopMod.Logger.LogWarning(
                $"{LogPrefix} TEST DROP: ignored the immediate host full snapshot for " +
                $"zone='{pendingRecoveryTestZoneId}'. Waiting for the periodic recovery snapshot.");
            return true;
        }

        private void CompleteZoneRecoveryTestIfRecovered(GameSave save, CSteamID senderID)
        {
            if (string.IsNullOrEmpty(pendingRecoveryTestZoneId) ||
                !IsExpectedHost(senderID) ||
                save?.known_world_zones == null ||
                !save.known_world_zones.Contains(pendingRecoveryTestZoneId))
            {
                return;
            }

            float elapsed = Time.realtimeSinceStartup - pendingRecoveryTestStartedAt;
            string recoveredZone = pendingRecoveryTestZoneId;
            ResetZoneRecoveryTest();
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} TEST PASSED: periodic full snapshot recovered zone=" +
                $"'{recoveredZone}' after {elapsed:F1}s");
        }

        private static bool IsExpectedHost(CSteamID senderID)
        {
            SteamLobbyManager lobby = SteamLobbyManager.Instance;
            return lobby != null && lobby.GetLobbyOwner() == senderID;
        }

        private static byte[] ReadCompressedSnapshot(BinaryReader reader)
        {
            int expectedLength = reader.ReadInt32();
            int compressedLength = reader.ReadInt32();
            if (expectedLength < 0 || expectedLength > MaxFullSnapshotBytes)
                throw new InvalidDataException($"invalid full snapshot length {expectedLength}");
            if (compressedLength < 0 || compressedLength > MaxCompressedSnapshotBytes)
                throw new InvalidDataException($"invalid compressed snapshot length {compressedLength}");
            if (reader.BaseStream.Length - reader.BaseStream.Position != compressedLength)
                throw new InvalidDataException("compressed snapshot length does not match payload");

            byte[] compressed = reader.ReadBytes(compressedLength);
            using (var input = new MemoryStream(compressed, writable: false))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream(expectedLength))
            {
                var buffer = new byte[8192];
                int read;
                while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > expectedLength)
                        throw new InvalidDataException("full snapshot expanded beyond its declared length");
                    output.Write(buffer, 0, read);
                }
                if (output.Length != expectedLength)
                {
                    throw new InvalidDataException(
                        $"full snapshot expanded to {output.Length}, expected {expectedLength}");
                }
                return output.ToArray();
            }
        }

        private static bool ApplyKnownZones(BinaryReader reader, GameSave save)
        {
            int count = reader.ReadUInt16();
            bool changed = false;
            if (save.known_world_zones == null)
                save.known_world_zones = new List<string>();

            for (int i = 0; i < count; i++)
            {
                string zoneId = reader.ReadString();
                if (!save.known_world_zones.Contains(zoneId))
                {
                    save.known_world_zones.Add(zoneId);
                    changed = true;
                }
            }
            return changed;
        }

        private int ApplyGDPointStates(BinaryReader reader, out bool usedIndexedLayout)
        {
            int count = reader.ReadUInt16();
            if (count < 0 || count > MaxGDPoints)
                throw new InvalidDataException($"invalid GDPoint count {count}");

            ulong remoteLayoutHash = reader.ReadUInt64();
            int stateByteCount = reader.ReadUInt16();
            int expectedStateBytes = (count + 7) / 8;
            if (stateByteCount != expectedStateBytes)
            {
                throw new InvalidDataException(
                    $"invalid GDPoint state bitset length {stateByteCount}, expected {expectedStateBytes}");
            }
            byte[] stateBits = reader.ReadBytes(stateByteCount);
            if (stateBits.Length != stateByteCount)
                throw new EndOfStreamException("truncated GDPoint state bitset");

            int fallbackLength = reader.ReadInt32();
            if (fallbackLength < 0 || fallbackLength > MaxFullSnapshotBytes ||
                reader.BaseStream.Length - reader.BaseStream.Position < fallbackLength)
            {
                throw new InvalidDataException($"invalid GDPoint fallback length {fallbackLength}");
            }

            List<GDPoint> gdPoints = WorldMap.gd_points;
            int localCount = Mathf.Min(gdPoints?.Count ?? 0, MaxGDPoints);
            usedIndexedLayout = localCount == count &&
                                ComputeGDPointLayoutHash(gdPoints, localCount) ==
                                remoteLayoutHash;

            int changed = 0;
            if (usedIndexedLayout)
            {
                for (int i = 0; i < count; i++)
                {
                    GDPoint gdp = gdPoints[i];
                    if (gdp == null || gdp.gameObject == null) continue;

                    bool enabled = (stateBits[i >> 3] & (1 << (i & 7))) != 0;
                    if (gdp.gameObject.activeSelf == enabled) continue;

                    gdp.gameObject.SetActive(enabled);
                    changed++;
                }
                reader.BaseStream.Seek(fallbackLength, SeekOrigin.Current);
                return changed;
            }

            byte[] fallback = reader.ReadBytes(fallbackLength);
            if (fallback.Length != fallbackLength)
                throw new EndOfStreamException("truncated GDPoint tag fallback");

            EnsureGDPointIndex();
            using (var fallbackStream = new MemoryStream(fallback, writable: false))
            using (var fallbackReader = new BinaryReader(fallbackStream, Encoding.UTF8))
            {
                for (int i = 0; i < count; i++)
                {
                    string gdTag = fallbackReader.ReadString();
                    bool enabled = fallbackReader.ReadBoolean();

                    GDPoint gdp = FindGDPointByTagQuiet(
                        gdTag,
                        rebuildIfMissing: false);
                    if (gdp == null || gdp.gameObject == null ||
                        gdp.gameObject.activeSelf == enabled)
                    {
                        continue;
                    }

                    gdp.gameObject.SetActive(enabled);
                    changed++;
                }
                if (fallbackStream.Position != fallbackStream.Length)
                {
                    throw new InvalidDataException(
                        $"GDPoint fallback has {fallbackStream.Length - fallbackStream.Position} trailing bytes");
                }
            }
            return changed;
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

            var save = MainGame.me?.save;
            if (save != null)
            {
                if (save.known_world_zones == null)
                    save.known_world_zones = new List<string>();

                if (!save.known_world_zones.Contains(zoneId))
                {
                    save.known_world_zones.Add(zoneId);
                    if (IsHost) ForceNextSend = true;
                    Patches.MapGUIPatches.RefreshKnownZoneVisibility();
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
                    if (IsHost) ForceNextSend = true;
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

            if (string.IsNullOrEmpty(zoneName))
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Ignored GDZone event without a zone name");
                return;
            }
            if (eventType > 1)
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Ignored GDZone event with invalid type {eventType}: " +
                    $"zone={zoneName}");
                return;
            }

            if (!string.IsNullOrEmpty(ovrMusic))
            {
                if (eventType == 0)
                    SmartAudioEngine.me?.PlayOvrMusic(ovrMusic);
                else
                    SmartAudioEngine.me?.StopOvrMusic(ovrMusic, false);
            }

            CoopMod.Logger.LogInfo($"{LogPrefix} Applied GDZoneEvent: zone={zoneName}, type={eventType}");
        }

        internal void InvalidateLookupCaches()
        {
            InvalidateGDPointIndex();
            indexedWorldRoot = null;
        }

        internal void InvalidateGDPointIndex()
        {
            gdPointsByTag.Clear();
            indexedGDPoints = null;
            indexedGDPointCount = -1;
            hasGDPointIndex = false;
        }

        private void EnsureWorldIdentity()
        {
            Transform worldRoot = MainGame.me?.world_root;
            if (indexedWorldRoot == worldRoot)
                return;

            InvalidateLookupCaches();
            indexedWorldRoot = worldRoot;
        }

        private void EnsureGDPointIndex()
        {
            EnsureWorldIdentity();
            List<GDPoint> gdPoints = WorldMap.gd_points;
            int count = gdPoints?.Count ?? 0;
            if (hasGDPointIndex &&
                ReferenceEquals(indexedGDPoints, gdPoints) &&
                indexedGDPointCount == count)
            {
                return;
            }

            RebuildGDPointIndex(gdPoints);
        }

        private void RebuildGDPointIndex(List<GDPoint> gdPoints)
        {
            gdPointsByTag.Clear();
            indexedGDPoints = gdPoints;
            indexedGDPointCount = gdPoints?.Count ?? 0;
            hasGDPointIndex = true;
            if (gdPoints == null)
                return;

            for (int i = 0; i < gdPoints.Count; i++)
            {
                GDPoint gdPoint = gdPoints[i];
                string tag = gdPoint?.gd_tag;
                if (string.IsNullOrEmpty(tag))
                    continue;

                if (!gdPointsByTag.TryGetValue(tag, out List<GDPoint> matches))
                {
                    matches = new List<GDPoint>(1);
                    gdPointsByTag.Add(tag, matches);
                }
                matches.Add(gdPoint);
            }
        }

        private GDPoint FindGDPointByTagQuiet(
            string gdTag,
            bool rebuildIfMissing = true)
        {
            if (string.IsNullOrEmpty(gdTag)) return null;

            EnsureGDPointIndex();
            GDPoint result = FindIndexedGDPoint(
                gdTag,
                out bool staleEntry,
                out bool indexedTag);
            if (result != null || !rebuildIfMissing || (indexedTag && !staleEntry))
                return result;

            // WorldMap normally replaces or changes the list when subscenes move.
            // Rebuild once on an unexpected miss as a guard against same-count edits.
            RebuildGDPointIndex(WorldMap.gd_points);
            return FindIndexedGDPoint(gdTag, out _, out _);
        }

        private GDPoint FindIndexedGDPoint(
            string gdTag,
            out bool staleEntry,
            out bool indexedTag)
        {
            staleEntry = false;
            indexedTag = gdPointsByTag.TryGetValue(
                gdTag,
                out List<GDPoint> matches);
            if (!indexedTag)
                return null;

            for (int i = 0; i < matches.Count; i++)
            {
                GDPoint gdPoint = matches[i];
                if (gdPoint == null || gdPoint.gd_tag != gdTag)
                {
                    staleEntry = true;
                    continue;
                }
                if (!gdPoint.IsDisabled())
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

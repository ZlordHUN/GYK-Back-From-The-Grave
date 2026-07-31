using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;
using GraveyardKeeperCoop.Utils;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Mirrors high-frequency visual state that is too granular for CharAnimState:
    /// SpriteRenderer sprite/flags plus child transform deltas on the player hierarchy.
    /// </summary>
    public class PlayerVisualSync : SyncBehaviour
    {
        public static PlayerVisualSync Instance => GetInstance<PlayerVisualSync>();

        private const byte PayloadVersion = 1;
        private const float SendIntervalSeconds = 0.05f;
        // Full resend exists to recover from any dropped unreliable deltas.
        // We avoid sending it more often than necessary because the full payload
        // (~3.8KB) exceeds Steam P2P's unreliable limit (~1100 bytes) and falls
        // back to reliable delivery — flooding the reliable channel every 2s was
        // a major source of avoidable network pressure during normal play.
        private const float FullResendIntervalSeconds = 15f;
        private const int MaxPayloadBytes = 128 * 1024;
        private const int MaxEntries = 1024;

        private readonly Dictionary<uint, string> lastSpriteSnapshot = new Dictionary<uint, string>();
        private readonly Dictionary<uint, string> lastTransformSnapshot = new Dictionary<uint, string>();
        private readonly List<string> capturedDeltas = new List<string>();
        private sealed class RemoteVisualState
        {
            public Transform Root;
            public Transform CharacterTransform;
            public VisualSyncHelpers.VisualHierarchyMap Map;
        }
        private readonly Dictionary<ulong, RemoteVisualState> remoteStates =
            new Dictionary<ulong, RemoteVisualState>();
        private Dictionary<string, Sprite> spriteLibrary;

        private float nextSendAt;
        private float nextFullResendAt;
        private Transform localRoot;
        private Transform localCharacterTransform;
        private VisualSyncHelpers.VisualHierarchyMap localMap;
        private float lastCharacterScaleX = 1f;
        private int spriteLibrarySize;

        public int SpriteLibrarySize => spriteLibrarySize;

        protected override string LogPrefix => "[PlayerVisualSync]";

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnPlayerVisualSyncReceived -= OnPlayerVisualSyncReceived;
                SteamP2PManager.Instance.OnPlayerVisualSyncReceived += OnPlayerVisualSyncReceived;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnPlayerVisualSyncReceived -= OnPlayerVisualSyncReceived;
            }
        }

        protected override void OnSyncEnabled()
        {
            if (ModConfig.EnablePlayerVisualSync != null && !ModConfig.EnablePlayerVisualSync.Value)
            {
                CoopMod.Logger.LogInfo($"{LogPrefix} Disabled by config");
                return;
            }

            nextSendAt = 0f;
            nextFullResendAt = 0f;
            ResetMaps();
        }

        protected override void OnSyncDisabled()
        {
            ResetMaps();
        }

        private void Update()
        {
            long __profStart = GraveyardKeeperCoop.Utils.FrameProfiler.BeginSection();
            try { UpdateInternal(); }
            finally { GraveyardKeeperCoop.Utils.FrameProfiler.EndSection("PVS.Update", __profStart); }
        }

        private void UpdateInternal()
        {
            if (!IsSyncEnabled || !IsOnline) return;

            if (Time.realtimeSinceStartup < nextSendAt) return;

            nextSendAt = Time.realtimeSinceStartup + SendIntervalSeconds;
            SendLocalVisualState();
        }

        private void SendLocalVisualState()
        {
            if (!EnsureLocalMap()) return;

            bool fullSend = Time.realtimeSinceStartup >= nextFullResendAt;
            if (fullSend)
                nextFullResendAt = Time.realtimeSinceStartup + FullResendIntervalSeconds;

            var entries = CaptureDeltas(fullSend);
            float scaleX = localCharacterTransform != null ? localCharacterTransform.localScale.x : 1f;
            bool scaleChanged = Mathf.Abs(scaleX - lastCharacterScaleX) > 0.01f;

            if (!fullSend && entries.Count == 0 && !scaleChanged) return;

            byte[] payload = SerializePayload(++NextSequence, scaleX, entries);
            if (payload == null || payload.Length == 0 || payload.Length > MaxPayloadBytes)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Refusing payload size {payload?.Length ?? 0}");
                return;
            }

            lastCharacterScaleX = scaleX;
            SteamP2PManager.Instance?.BroadcastPlayerVisualSync(payload);
        }

        private List<string> CaptureDeltas(bool fullSend)
        {
            capturedDeltas.Clear();
            int requiredCapacity = localMap.SpriteCount + localMap.TransformCount;
            if (capturedDeltas.Capacity < requiredCapacity)
                capturedDeltas.Capacity = requiredCapacity;
            List<string> entries = capturedDeltas;

            foreach (var pair in localMap.Sprites)
            {
                uint hash = pair.Key;
                SpriteRenderer sr = pair.Value;
                string entry;
                if (sr == null)
                {
                    entry = $"{hash:X8}::0";
                }
                else
                {
                    string spriteName = sr.sprite != null ? SpriteNameNormalizer.Normalize(sr.sprite.name) : string.Empty;
                    byte flags = VisualSyncHelpers.PackSpriteFlags(sr);
                    entry = $"{hash:X8}:{spriteName}:{flags}";
                }

                if (fullSend || !lastSpriteSnapshot.TryGetValue(hash, out var previous) || previous != entry)
                {
                    entries.Add(entry);
                    lastSpriteSnapshot[hash] = entry;
                    if (entries.Count >= MaxEntries) return entries;
                }
            }

            foreach (var pair in localMap.Transforms)
            {
                uint hash = pair.Key;
                Transform transform = pair.Value;
                if (transform == null) continue;

                string entry = PackTransform(hash, transform);
                if (fullSend || !lastTransformSnapshot.TryGetValue(hash, out var previous) || previous != entry)
                {
                    entries.Add(entry);
                    lastTransformSnapshot[hash] = entry;
                    if (entries.Count >= MaxEntries) return entries;
                }
            }

            return entries;
        }

        private void OnPlayerVisualSyncReceived(CSteamID senderID, byte[] payload)
        {
            if (!IsSyncEnabled || payload == null || payload.Length == 0 || payload.Length > MaxPayloadBytes) return;
            if (!IsOnline) return;
            OnlineCoopManager.Instance?.HandlePeerEnteredLobby(senderID);
            if (!EnsureRemoteMap(senderID, out RemoteVisualState state)) return;

            try
            {
                if (!DeserializePayload(payload, out float characterScaleX, out var entries)) return;
                ApplyCharacterScale(state, characterScaleX);
                ApplyDeltas(state, entries);
                OnlineCoopManager.Instance?.ReconcileRemoteActionVisual(senderID);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Failed to apply visual payload: {ex.Message}");
            }
        }

        private bool EnsureLocalMap()
        {
            Transform currentRoot = OnlineCoopManager.Instance?.LocalPlayerComponent?.transform;
            if (currentRoot == null && MainGame.me?.player != null)
                currentRoot = MainGame.me.player.transform;

            if (currentRoot == null) return false;

            if (localRoot != currentRoot || localMap == null)
            {
                localRoot = currentRoot;
                localMap = VisualSyncHelpers.VisualHierarchyMap.From(localRoot, includeTransforms: true);
                localCharacterTransform = VisualSyncHelpers.FindChildByName(localRoot, "character");
                lastSpriteSnapshot.Clear();
                lastTransformSnapshot.Clear();
                nextFullResendAt = 0f;
                CoopMod.Logger.LogInfo($"{LogPrefix} Local hierarchy indexed: sprites={localMap.SpriteCount}, transforms={localMap.TransformCount}");
            }

            return true;
        }

        private bool EnsureRemoteMap(
            CSteamID senderID,
            out RemoteVisualState state)
        {
            state = null;
            if (senderID == CSteamID.Nil)
                return false;

            Transform currentRoot = OnlineCoopManager.Instance
                ?.GetRemotePlayerComponent(senderID)
                ?.transform;
            if (currentRoot == null)
                return false;

            if (!remoteStates.TryGetValue(senderID.m_SteamID, out state) ||
                state == null || state.Root != currentRoot || state.Map == null)
            {
                state = new RemoteVisualState
                {
                    Root = currentRoot,
                    Map = VisualSyncHelpers.VisualHierarchyMap.From(
                        currentRoot,
                        includeTransforms: true),
                    CharacterTransform = VisualSyncHelpers.FindChildByName(
                        currentRoot,
                        "character")
                };
                remoteStates[senderID.m_SteamID] = state;
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Remote hierarchy indexed for " +
                    $"{senderID.m_SteamID}: sprites={state.Map.SpriteCount}, " +
                    $"transforms={state.Map.TransformCount}");
            }

            return true;
        }

        private void ApplyDeltas(
            RemoteVisualState state,
            List<string> entries)
        {
            long __profStart = GraveyardKeeperCoop.Utils.FrameProfiler.BeginSection();
            try { ApplyDeltasInternal(state, entries); }
            finally { GraveyardKeeperCoop.Utils.FrameProfiler.EndSection("PVS.Apply", __profStart); }
        }

        private void ApplyDeltasInternal(
            RemoteVisualState state,
            List<string> entries)
        {
            if (entries == null || entries.Count == 0 || state?.Map == null) return;

            Dictionary<string, Sprite> sprites = GetSpriteLibrary();
            for (int i = 0; i < entries.Count; i++)
            {
                string entry = entries[i];
                if (string.IsNullOrEmpty(entry)) continue;

                if (entry.StartsWith("T:", StringComparison.Ordinal))
                    ApplyTransformDelta(state, entry);
                else
                    VisualSyncHelpers.TryApplySpriteDelta(state.Map, entry, sprites);
            }
        }

        private static void ApplyTransformDelta(
            RemoteVisualState state,
            string entry)
        {
            string[] parts = entry.Split(':');
            if (parts.Length != 6) return;

            if (!uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash)) return;
            if (state?.Map == null ||
                !state.Map.TryGetTransform(hash, out var transform) ||
                transform == null) return;

            if (TryParseVector3(parts[2], out var localPosition)) transform.localPosition = localPosition;
            if (TryParseQuaternion(parts[3], out var localRotation)) transform.localRotation = localRotation;
            if (TryParseVector3(parts[4], out var localScale)) transform.localScale = localScale;

            if (byte.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte activeFlag))
            {
                bool active = activeFlag != 0;
                if (transform.gameObject.activeSelf != active) transform.gameObject.SetActive(active);
            }
        }

        private static void ApplyCharacterScale(
            RemoteVisualState state,
            float scaleX)
        {
            if (state?.CharacterTransform == null) return;
            Vector3 scale = state.CharacterTransform.localScale;
            scale.x = scaleX;
            state.CharacterTransform.localScale = scale;
        }

        private Dictionary<string, Sprite> GetSpriteLibrary()
        {
            if (spriteLibrary != null) return spriteLibrary;

            spriteLibrary = VisualSyncHelpers.BuildSpriteLibrary();
            spriteLibrarySize = spriteLibrary.Count;
            CoopMod.Logger.LogInfo($"{LogPrefix} Sprite library indexed: {spriteLibrarySize}");
            return spriteLibrary;
        }

        private void ResetMaps()
        {
            localRoot = null;
            localCharacterTransform = null;
            localMap = null;
            remoteStates.Clear();
            lastSpriteSnapshot.Clear();
            lastTransformSnapshot.Clear();
            capturedDeltas.Clear();
            lastCharacterScaleX = 1f;
        }

        private static string PackTransform(uint hash, Transform transform)
        {
            Vector3 p = transform.localPosition;
            Quaternion r = transform.localRotation;
            Vector3 s = transform.localScale;
            byte active = transform.gameObject.activeSelf ? (byte)1 : (byte)0;
            return $"T:{hash:X8}:{F(p.x)},{F(p.y)},{F(p.z)}:{F(r.x)},{F(r.y)},{F(r.z)},{F(r.w)}:{F(s.x)},{F(s.y)},{F(s.z)}:{active}";
        }

        private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static bool TryParseVector3(string value, out Vector3 result)
        {
            result = Vector3.zero;
            string[] parts = value.Split(',');
            if (parts.Length != 3) return false;
            if (!TryParseFloat(parts[0], out float x) || !TryParseFloat(parts[1], out float y) || !TryParseFloat(parts[2], out float z)) return false;
            result = new Vector3(x, y, z);
            return true;
        }

        private static bool TryParseQuaternion(string value, out Quaternion result)
        {
            result = Quaternion.identity;
            string[] parts = value.Split(',');
            if (parts.Length != 4) return false;
            if (!TryParseFloat(parts[0], out float x) || !TryParseFloat(parts[1], out float y) ||
                !TryParseFloat(parts[2], out float z) || !TryParseFloat(parts[3], out float w)) return false;
            result = new Quaternion(x, y, z, w);
            return true;
        }

        private static bool TryParseFloat(string value, out float result) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

        private static byte[] SerializePayload(uint sequence, float characterScaleX, List<string> entries)
        {
            using (var stream = new MemoryStream(4096))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(PayloadVersion);
                writer.Write(sequence);
                writer.Write(characterScaleX);
                int count = Mathf.Min(entries?.Count ?? 0, MaxEntries);
                writer.Write((ushort)count);
                for (int i = 0; i < count; i++)
                    writer.Write(entries[i] ?? string.Empty);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static bool DeserializePayload(byte[] payload, out float characterScaleX, out List<string> entries)
        {
            characterScaleX = 1f;
            entries = new List<string>();

            using (var stream = new MemoryStream(payload))
            using (var reader = new BinaryReader(stream))
            {
                byte version = reader.ReadByte();
                if (version != PayloadVersion) return false;

                reader.ReadUInt32();
                characterScaleX = reader.ReadSingle();
                int count = reader.ReadUInt16();
                if (count > MaxEntries) return false;

                for (int i = 0; i < count && stream.Position < stream.Length; i++)
                    entries.Add(reader.ReadString());
            }

            return true;
        }

    }
}

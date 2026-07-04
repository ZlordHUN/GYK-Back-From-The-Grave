using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using GraveyardKeeperCoop.Network;
using Steamworks;
using UnityEngine;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Stores multiplayer-only player positions next to the game's normal save files.
    /// The base game only has one GameSave.player_position, so each peer needs a
    /// SteamID-keyed position before PlayerComponent.SpawnPlayer resets the local player.
    /// </summary>
    public static class MultiplayerSavePositions
    {
        private const int CurrentVersion = 1;
        private const string SidecarExtension = ".coop_positions.json";
        private const float PositionZRepairEpsilon = 0.001f;

        [Serializable]
        public class PositionFile
        {
            public int version;
            public string slot;
            public string savedAtUtc;
            public PlayerPositionEntry[] players = new PlayerPositionEntry[0];
        }

        [Serializable]
        public class PlayerPositionEntry
        {
            public string steamId;
            public bool isHost;
            public float x;
            public float y;
            public float z;

            public Vector3 ToVector3()
            {
                return new Vector3(x, y, z);
            }
        }

        public static bool CaptureCurrentSession(SaveSlotData slot)
        {
            return CaptureCurrentSession(slot?.filename_no_extension);
        }

        public static bool CaptureCurrentSession(string slotFilename)
        {
            if (string.IsNullOrEmpty(slotFilename))
            {
                CoopMod.Logger.LogWarning("[MultiplayerSavePositions] Cannot capture positions without a slot filename");
                return false;
            }

            if (!IsInMultiplayerLobby())
            {
                return false;
            }

            try
            {
                var lobby = SteamLobbyManager.Instance;
                var data = new PositionFile
                {
                    version = CurrentVersion,
                    slot = slotFilename,
                    savedAtUtc = DateTime.UtcNow.ToString("o")
                };
                var capturedPlayers = new List<PlayerPositionEntry>();
                var capturedSteamIds = new HashSet<string>();

                CSteamID localSteamId = SteamUser.GetSteamID();
                if (TryGetLocalPlayerPosition(out Vector3 localPosition))
                {
                    AddCapturedEntry(capturedPlayers, capturedSteamIds, CreateEntry(localSteamId, lobby.IsHost, localPosition));
                    CoopMod.Logger.LogInfo($"[MultiplayerSavePositions] Captured local player {localSteamId.m_SteamID} at {localPosition}");
                }
                else
                {
                    CoopMod.Logger.LogWarning("[MultiplayerSavePositions] Could not capture local player position");
                }

                CSteamID remoteSteamId = GetRemoteSteamId();
                if (remoteSteamId != CSteamID.Nil && TryGetRemotePlayerPosition(out Vector3 remotePosition))
                {
                    AddCapturedEntry(capturedPlayers, capturedSteamIds, CreateEntry(remoteSteamId, !lobby.IsHost, remotePosition));
                    CoopMod.Logger.LogInfo($"[MultiplayerSavePositions] Captured remote player {remoteSteamId.m_SteamID} at {remotePosition}");
                }
                else
                {
                    CoopMod.Logger.LogInfo("[MultiplayerSavePositions] No remote player position available to capture");
                }

                PreservePreviousEntries(slotFilename, capturedPlayers, capturedSteamIds);
                data.players = capturedPlayers.ToArray();

                if (data.players.Length == 0)
                {
                    CoopMod.Logger.LogWarning("[MultiplayerSavePositions] No player positions captured; sidecar not written");
                    return false;
                }

                string json = SerializePositionFile(data);
                if (!TryDeserializePositionFile(json, out PositionFile serializedData) ||
                    serializedData.players == null ||
                    serializedData.players.Length != data.players.Length)
                {
                    CoopMod.Logger.LogWarning($"[MultiplayerSavePositions] Refusing to write invalid serialized sidecar for '{slotFilename}'");
                    return false;
                }

                File.WriteAllText(GetSidecarPath(slotFilename), json);
                CoopMod.Logger.LogInfo($"[MultiplayerSavePositions] Wrote {data.players.Length} player position(s) for slot '{slotFilename}' ({json.Length} bytes)");
                return true;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[MultiplayerSavePositions] Failed to capture positions for '{slotFilename}': {ex.Message}");
                return false;
            }
        }

        public static bool TryApplyLocalPositionForCurrentSlot()
        {
            if (!IsInMultiplayerLobby())
            {
                return false;
            }

            SaveSlotData slot = MainGame.me?.save_slot;
            if (slot == null || string.IsNullOrEmpty(slot.filename_no_extension) || MainGame.me?.save == null)
            {
                CoopMod.Logger.LogDebug("[MultiplayerSavePositions] No loaded save slot available while applying local position");
                return false;
            }

            try
            {
                if (!TryReadPositionFile(slot.filename_no_extension, out PositionFile data))
                {
                    CoopMod.Logger.LogInfo($"[MultiplayerSavePositions] No multiplayer position sidecar for slot '{slot.filename_no_extension}'");
                    return false;
                }

                CSteamID localSteamId = SteamUser.GetSteamID();
                PlayerPositionEntry entry = FindEntry(data, localSteamId);
                if (entry == null)
                {
                    CoopMod.Logger.LogWarning($"[MultiplayerSavePositions] Sidecar for '{slot.filename_no_extension}' has no entry for local SteamID {localSteamId.m_SteamID}");
                    return false;
                }

                Vector3 oldPosition = MainGame.me.save.player_position;
                Vector3 newPosition = GetEntryPosition(data, entry, "local apply");
                MainGame.me.save.player_position = newPosition;
                CoopMod.Logger.LogInfo($"[MultiplayerSavePositions] Applied local saved position for {localSteamId.m_SteamID}: {oldPosition} -> {newPosition}");
                return true;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[MultiplayerSavePositions] Failed to apply local position: {ex.Message}");
                return false;
            }
        }

        public static bool TryGetSavedPositionForSteam(CSteamID steamId, out Vector3 position)
        {
            position = Vector3.zero;

            if (steamId == CSteamID.Nil || !IsInMultiplayerLobby())
            {
                return false;
            }

            SaveSlotData slot = MainGame.me?.save_slot;
            if (slot == null || string.IsNullOrEmpty(slot.filename_no_extension))
            {
                return false;
            }

            if (!TryReadPositionFile(slot.filename_no_extension, out PositionFile data))
            {
                return false;
            }

            PlayerPositionEntry entry = FindEntry(data, steamId);
            if (entry == null)
            {
                return false;
            }

            position = GetEntryPosition(data, entry, "remote spawn");
            return true;
        }

        public static string ReadSidecarJson(string slotFilename)
        {
            if (string.IsNullOrEmpty(slotFilename))
            {
                return null;
            }

            try
            {
                string path = GetSidecarPath(slotFilename);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[MultiplayerSavePositions] Failed to read sidecar for '{slotFilename}': {ex.Message}");
                return null;
            }
        }

        public static bool WriteSidecarJson(string slotFilename, string json)
        {
            if (string.IsNullOrEmpty(slotFilename))
            {
                return false;
            }

            try
            {
                if (string.IsNullOrEmpty(json))
                {
                    DeleteSidecar(slotFilename);
                    return true;
                }

                // Validate before writing so corrupted transfer data does not poison a cached slot.
                if (!TryDeserializePositionFile(json, out PositionFile data) || data.players == null)
                {
                    CoopMod.Logger.LogWarning($"[MultiplayerSavePositions] Refusing invalid sidecar JSON for '{slotFilename}'");
                    return false;
                }

                if (data.players.Length == 0)
                {
                    CoopMod.Logger.LogWarning($"[MultiplayerSavePositions] Refusing empty sidecar JSON for '{slotFilename}'");
                    DeleteSidecar(slotFilename);
                    return false;
                }

                string normalizedJson = SerializePositionFile(data);
                File.WriteAllText(GetSidecarPath(slotFilename), normalizedJson);
                CoopMod.Logger.LogInfo($"[MultiplayerSavePositions] Wrote transferred position sidecar for slot '{slotFilename}' ({data.players.Length} player(s), {normalizedJson.Length} bytes)");
                return true;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[MultiplayerSavePositions] Failed to write sidecar for '{slotFilename}': {ex.Message}");
                return false;
            }
        }

        public static void DeleteSidecar(string slotFilename)
        {
            if (string.IsNullOrEmpty(slotFilename))
            {
                return;
            }

            try
            {
                string path = GetSidecarPath(slotFilename);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    CoopMod.Logger.LogInfo($"[MultiplayerSavePositions] Deleted stale position sidecar for slot '{slotFilename}'");
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[MultiplayerSavePositions] Failed to delete sidecar for '{slotFilename}': {ex.Message}");
            }
        }

        private static string GetSidecarPath(string slotFilename)
        {
            return PlatformSpecific.GetSaveFolder() + slotFilename + SidecarExtension;
        }

        private static bool TryReadPositionFile(string slotFilename, out PositionFile data)
        {
            data = null;
            string json = ReadSidecarJson(slotFilename);
            if (string.IsNullOrEmpty(json))
            {
                return false;
            }

            return TryDeserializePositionFile(json, out data) && data != null && data.players != null;
        }

        private static string SerializePositionFile(PositionFile data)
        {
            var validPlayers = new List<PlayerPositionEntry>();
            if (data.players != null)
            {
                for (int i = 0; i < data.players.Length; i++)
                {
                    if (data.players[i] != null)
                    {
                        validPlayers.Add(data.players[i]);
                    }
                }
            }

            var builder = new StringBuilder();
            builder.AppendLine("{");
            builder.Append("  \"version\": ").Append(data.version).AppendLine(",");
            builder.Append("  \"slot\": \"").Append(EscapeJson(data.slot)).AppendLine("\",");
            builder.Append("  \"savedAtUtc\": \"").Append(EscapeJson(data.savedAtUtc)).AppendLine("\",");
            builder.AppendLine("  \"players\": [");

            for (int i = 0; i < validPlayers.Count; i++)
            {
                PlayerPositionEntry entry = validPlayers[i];
                builder.Append("    { ");
                builder.Append("\"steamId\": \"").Append(EscapeJson(entry.steamId)).Append("\", ");
                builder.Append("\"isHost\": ").Append(entry.isHost ? "true" : "false").Append(", ");
                builder.Append("\"x\": ").Append(FormatFloat(entry.x)).Append(", ");
                builder.Append("\"y\": ").Append(FormatFloat(entry.y)).Append(", ");
                builder.Append("\"z\": ").Append(FormatFloat(entry.z));
                builder.Append(" }");

                if (i < validPlayers.Count - 1)
                {
                    builder.Append(",");
                }

                builder.AppendLine();
            }

            builder.AppendLine("  ]");
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static bool TryDeserializePositionFile(string json, out PositionFile data)
        {
            data = null;
            if (string.IsNullOrEmpty(json))
            {
                return false;
            }

            try
            {
                int version = CurrentVersion;
                if (!TryGetJsonInt(json, "version", out version))
                {
                    version = CurrentVersion;
                }

                TryGetJsonString(json, "slot", out string slot);
                TryGetJsonString(json, "savedAtUtc", out string savedAtUtc);

                var players = new List<PlayerPositionEntry>();
                if (TryGetJsonArray(json, "players", out string playersJson))
                {
                    int index = 0;
                    while (TryGetNextJsonObject(playersJson, ref index, out string playerJson))
                    {
                        if (TryDeserializePlayerEntry(playerJson, out PlayerPositionEntry entry))
                        {
                            players.Add(entry);
                        }
                    }
                }

                data = new PositionFile
                {
                    version = version,
                    slot = slot,
                    savedAtUtc = savedAtUtc,
                    players = players.ToArray()
                };
                return true;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[MultiplayerSavePositions] Failed to parse sidecar JSON: {ex.Message}");
                return false;
            }
        }

        private static bool TryDeserializePlayerEntry(string json, out PlayerPositionEntry entry)
        {
            entry = null;
            if (!TryGetJsonString(json, "steamId", out string steamId) ||
                !TryGetJsonFloat(json, "x", out float x) ||
                !TryGetJsonFloat(json, "y", out float y) ||
                !TryGetJsonFloat(json, "z", out float z))
            {
                return false;
            }

            TryGetJsonBool(json, "isHost", out bool isHost);
            entry = new PlayerPositionEntry
            {
                steamId = steamId,
                isHost = isHost,
                x = x,
                y = y,
                z = z
            };
            return true;
        }

        private static bool TryGetJsonInt(string json, string fieldName, out int value)
        {
            value = 0;
            if (!TryFindJsonValue(json, fieldName, out int valueIndex))
            {
                return false;
            }

            int endIndex = ReadJsonTokenEnd(json, valueIndex);
            return int.TryParse(json.Substring(valueIndex, endIndex - valueIndex), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryGetJsonFloat(string json, string fieldName, out float value)
        {
            value = 0f;
            if (!TryFindJsonValue(json, fieldName, out int valueIndex))
            {
                return false;
            }

            int endIndex = ReadJsonTokenEnd(json, valueIndex);
            return float.TryParse(json.Substring(valueIndex, endIndex - valueIndex), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryGetJsonBool(string json, string fieldName, out bool value)
        {
            value = false;
            if (!TryFindJsonValue(json, fieldName, out int valueIndex))
            {
                return false;
            }

            if (valueIndex + 4 <= json.Length && string.Compare(json, valueIndex, "true", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
            {
                value = true;
                return true;
            }

            if (valueIndex + 5 <= json.Length && string.Compare(json, valueIndex, "false", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
            {
                value = false;
                return true;
            }

            return false;
        }

        private static bool TryGetJsonString(string json, string fieldName, out string value)
        {
            value = null;
            if (!TryFindJsonValue(json, fieldName, out int valueIndex) || valueIndex >= json.Length || json[valueIndex] != '"')
            {
                return false;
            }

            var builder = new StringBuilder();
            bool isEscaped = false;
            for (int i = valueIndex + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (isEscaped)
                {
                    switch (c)
                    {
                        case '"':
                        case '\\':
                        case '/':
                            builder.Append(c);
                            break;
                        case 'b':
                            builder.Append('\b');
                            break;
                        case 'f':
                            builder.Append('\f');
                            break;
                        case 'n':
                            builder.Append('\n');
                            break;
                        case 'r':
                            builder.Append('\r');
                            break;
                        case 't':
                            builder.Append('\t');
                            break;
                        case 'u':
                            if (i + 4 < json.Length &&
                                int.TryParse(json.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int codePoint))
                            {
                                builder.Append((char)codePoint);
                                i += 4;
                            }
                            break;
                        default:
                            builder.Append(c);
                            break;
                    }

                    isEscaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    isEscaped = true;
                    continue;
                }

                if (c == '"')
                {
                    value = builder.ToString();
                    return true;
                }

                builder.Append(c);
            }

            return false;
        }

        private static bool TryGetJsonArray(string json, string fieldName, out string arrayJson)
        {
            arrayJson = null;
            if (!TryFindJsonValue(json, fieldName, out int valueIndex) || valueIndex >= json.Length || json[valueIndex] != '[')
            {
                return false;
            }

            bool inString = false;
            bool isEscaped = false;
            int depth = 0;
            int contentStart = valueIndex + 1;

            for (int i = valueIndex; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (isEscaped)
                    {
                        isEscaped = false;
                    }
                    else if (c == '\\')
                    {
                        isEscaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '[')
                {
                    depth++;
                }
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        arrayJson = json.Substring(contentStart, i - contentStart);
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryGetNextJsonObject(string json, ref int index, out string objectJson)
        {
            objectJson = null;

            while (index < json.Length && json[index] != '{')
            {
                index++;
            }

            if (index >= json.Length)
            {
                return false;
            }

            bool inString = false;
            bool isEscaped = false;
            int depth = 0;
            int objectStart = index;

            for (; index < json.Length; index++)
            {
                char c = json[index];
                if (inString)
                {
                    if (isEscaped)
                    {
                        isEscaped = false;
                    }
                    else if (c == '\\')
                    {
                        isEscaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        objectJson = json.Substring(objectStart, index - objectStart + 1);
                        index++;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryFindJsonValue(string json, string fieldName, out int valueIndex)
        {
            valueIndex = -1;
            string fieldToken = "\"" + fieldName + "\"";
            int fieldIndex = json.IndexOf(fieldToken, StringComparison.Ordinal);
            if (fieldIndex < 0)
            {
                return false;
            }

            int colonIndex = json.IndexOf(':', fieldIndex + fieldToken.Length);
            if (colonIndex < 0)
            {
                return false;
            }

            valueIndex = colonIndex + 1;
            while (valueIndex < json.Length && char.IsWhiteSpace(json[valueIndex]))
            {
                valueIndex++;
            }

            return valueIndex < json.Length;
        }

        private static int ReadJsonTokenEnd(string json, int startIndex)
        {
            int endIndex = startIndex;
            while (endIndex < json.Length)
            {
                char c = json[endIndex];
                if (char.IsWhiteSpace(c) || c == ',' || c == '}' || c == ']')
                {
                    break;
                }

                endIndex++;
            }

            return endIndex;
        }

        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            return builder.ToString();
        }

        private static string FormatFloat(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return "0";
            }

            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void AddCapturedEntry(List<PlayerPositionEntry> players, HashSet<string> capturedSteamIds, PlayerPositionEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.steamId))
            {
                return;
            }

            for (int i = players.Count - 1; i >= 0; i--)
            {
                if (players[i] != null && players[i].steamId == entry.steamId)
                {
                    players.RemoveAt(i);
                }
            }

            players.Add(entry);
            capturedSteamIds.Add(entry.steamId);
        }

        private static void PreservePreviousEntries(string slotFilename, List<PlayerPositionEntry> players, HashSet<string> capturedSteamIds)
        {
            if (!TryReadPositionFile(slotFilename, out PositionFile existing) || existing.players == null)
            {
                return;
            }

            for (int i = 0; i < existing.players.Length; i++)
            {
                PlayerPositionEntry entry = existing.players[i];
                if (entry == null || string.IsNullOrEmpty(entry.steamId) || capturedSteamIds.Contains(entry.steamId))
                {
                    continue;
                }

                players.Add(entry);
                capturedSteamIds.Add(entry.steamId);
                CoopMod.Logger.LogInfo($"[MultiplayerSavePositions] Preserved previous position for disconnected player {entry.steamId}");
            }
        }

        private static PlayerPositionEntry CreateEntry(CSteamID steamId, bool isHost, Vector3 position)
        {
            return new PlayerPositionEntry
            {
                steamId = steamId.m_SteamID.ToString(),
                isHost = isHost,
                x = position.x,
                y = position.y,
                z = position.z
            };
        }

        private static PlayerPositionEntry FindEntry(PositionFile data, CSteamID steamId)
        {
            if (data?.players == null)
            {
                return null;
            }

            string steamIdText = steamId.m_SteamID.ToString();
            for (int i = 0; i < data.players.Length; i++)
            {
                PlayerPositionEntry entry = data.players[i];
                if (entry != null && entry.steamId == steamIdText)
                {
                    return entry;
                }
            }

            return null;
        }

        private static Vector3 GetEntryPosition(PositionFile data, PlayerPositionEntry entry, string reason)
        {
            Vector3 position = entry.ToVector3();
            if (Mathf.Abs(position.z) <= PositionZRepairEpsilon &&
                TryGetFallbackZ(data, entry, out float fallbackZ))
            {
                CoopMod.Logger.LogWarning($"[MultiplayerSavePositions] Repaired zero-Z saved position for {entry.steamId} during {reason}: z={position.z:F1} -> z={fallbackZ:F1}");
                position.z = fallbackZ;
            }

            return position;
        }

        private static bool TryGetFallbackZ(PositionFile data, PlayerPositionEntry sourceEntry, out float z)
        {
            z = 0f;
            if (data?.players == null)
            {
                return false;
            }

            for (int i = 0; i < data.players.Length; i++)
            {
                PlayerPositionEntry entry = data.players[i];
                if (entry == null || entry == sourceEntry || Mathf.Abs(entry.z) <= PositionZRepairEpsilon)
                {
                    continue;
                }

                z = entry.z;
                return true;
            }

            return false;
        }

        private static bool TryGetLocalPlayerPosition(out Vector3 position)
        {
            position = Vector3.zero;

            if (MainGame.me?.player != null)
            {
                position = MainGame.me.player.transform.localPosition;
                return true;
            }

            if (MainGame.me?.save != null)
            {
                position = MainGame.me.save.player_position;
                return true;
            }

            return false;
        }

        private static bool TryGetRemotePlayerPosition(out Vector3 position)
        {
            position = Vector3.zero;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null)
            {
                return false;
            }

            return onlineCoop.TryGetRemotePlayerSavePosition(out position);
        }

        private static CSteamID GetRemoteSteamId()
        {
            var lobby = SteamLobbyManager.Instance;
            if (lobby == null || !lobby.IsInLobby || lobby.CurrentLobbyID == CSteamID.Nil)
            {
                return CSteamID.Nil;
            }

            CSteamID localSteamId = SteamUser.GetSteamID();
            if (!lobby.IsHost)
            {
                CSteamID owner = SteamMatchmaking.GetLobbyOwner(lobby.CurrentLobbyID);
                return owner != localSteamId ? owner : CSteamID.Nil;
            }

            int memberCount = lobby.GetLobbyMemberCount();
            for (int i = 0; i < memberCount; i++)
            {
                CSteamID member = SteamMatchmaking.GetLobbyMemberByIndex(lobby.CurrentLobbyID, i);
                if (member != CSteamID.Nil && member != localSteamId)
                {
                    return member;
                }
            }

            return CSteamID.Nil;
        }

        private static bool IsInMultiplayerLobby()
        {
            var lobby = SteamLobbyManager.Instance;
            return lobby != null && lobby.IsInLobby && lobby.CurrentLobbyID != CSteamID.Nil;
        }
    }
}

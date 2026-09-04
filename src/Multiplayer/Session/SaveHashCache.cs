using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Persistent cache mapping host Steam ID + host save identity -> locally cached
    /// co-op save slot.
    /// Used to skip re-downloading the host's save when its contents haven't changed
    /// since the last join.
    ///
    /// On disk layout: {saveFolder}/coop_save_cache.txt
    /// </summary>
    public static class SaveHashCache
    {
        [Serializable]
        public class Entry
        {
            public ulong hostId;
            public string hostSlotFilename;
            public int worldSeed = -1;
            public string slotFilename;   // e.g. "coop_20260424_225034"
            public string hash;           // SHA256 hex of the .dat file at the time of caching
            public string realTime;       // for UI / debugging
            public long storedAtUnix;
        }

        private const string CACHE_FILENAME = "coop_save_cache.txt";

        // Keep entries in memory after first load
        private static Dictionary<string, Entry> _entries;
        private static readonly object _lock = new object();

        private static string CachePath => PlatformSpecific.GetSaveFolder() + CACHE_FILENAME;

        private static void EnsureLoaded()
        {
            if (_entries != null) return;

            lock (_lock)
            {
                if (_entries != null) return;
                _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

                try
                {
                    string path = CachePath;
                    if (!File.Exists(path))
                    {
                        return;
                    }

                    // V2 format:
                    // <hostId>\t<worldSeed>\t<hostSlot>\t<localSlot>\t<hash>\t<storedAtUnix>\t<realTime>
                    // The previous five-column host-only format is migrated below.
                    foreach (string rawLine in File.ReadAllLines(path))
                    {
                        string line = rawLine ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(line) ||
                            line.TrimStart().StartsWith("#"))
                        {
                            continue;
                        }

                        string[] parts = line.Split(new[] { '\t' }, 7);
                        if (parts.Length < 4 ||
                            !ulong.TryParse(parts[0], out ulong hostId) ||
                            hostId == 0)
                        {
                            continue;
                        }

                        string hostSlotFilename;
                        int worldSeed;
                        string slotFilename;
                        string hash;
                        long storedAt;
                        string realTime;
                        if (parts.Length >= 7 &&
                            int.TryParse(parts[1], out worldSeed))
                        {
                            hostSlotFilename = NormalizeHostSlot(parts[2]);
                            slotFilename = parts[3];
                            hash = parts[4];
                            long.TryParse(parts[5], out storedAt);
                            realTime = parts[6];
                        }
                        else
                        {
                            // V1: hostId, localSlot, hash, storedAtUnix, realTime.
                            hostSlotFilename = string.Empty;
                            slotFilename = parts[1];
                            hash = parts[2];
                            long.TryParse(parts[3], out storedAt);
                            realTime = parts.Length >= 5 ? parts[4] : string.Empty;
                            worldSeed = ComputeSlotWorldSeed(slotFilename);
                        }

                        if (!SlotExists(slotFilename))
                        {
                            CoopMod.Logger.LogInfo(
                                $"[SaveHashCache] Dropping stale entry for host {hostId} (slot '{slotFilename}' no longer on disk)");
                            continue;
                        }

                        var entry = new Entry
                        {
                            hostId = hostId,
                            hostSlotFilename = hostSlotFilename,
                            worldSeed = worldSeed,
                            slotFilename = slotFilename,
                            hash = hash,
                            realTime = realTime,
                            storedAtUnix = storedAt
                        };
                        _entries[BuildStorageKey(entry)] = entry;
                    }

                    CoopMod.Logger.LogInfo($"[SaveHashCache] Loaded {_entries.Count} cached host save(s) from {path}");
                }
                catch (Exception ex)
                {
                    CoopMod.Logger.LogWarning($"[SaveHashCache] Failed to load cache: {ex.Message}");
                    _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
                }
            }
        }

        private static bool SlotExists(string slotFilename)
        {
            if (string.IsNullOrEmpty(slotFilename)) return false;
            try
            {
                string folder = PlatformSpecific.GetSaveFolder();
                return File.Exists(folder + slotFilename + ".dat")
                    && File.Exists(folder + slotFilename + ".info");
            }
            catch { return false; }
        }

        private static void SaveToDisk()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Graveyard Keeper Back From The Grave - cached host saves");
                sb.AppendLine("# Format v2: hostId<TAB>worldSeed<TAB>hostSlot<TAB>localSlot<TAB>hash<TAB>storedAtUnix<TAB>realTime");
                foreach (var e in _entries.Values)
                {
                    // Sanitise tabs and newlines in realTime just in case.
                    string safeReal = (e.realTime ?? string.Empty).Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
                    string safeHostSlot = NormalizeHostSlot(e.hostSlotFilename)
                        .Replace('\t', ' ')
                        .Replace('\n', ' ')
                        .Replace('\r', ' ');
                    sb.Append(e.hostId).Append('\t')
                      .Append(e.worldSeed).Append('\t')
                      .Append(safeHostSlot).Append('\t')
                      .Append(e.slotFilename ?? string.Empty).Append('\t')
                      .Append(e.hash ?? string.Empty).Append('\t')
                      .Append(e.storedAtUnix).Append('\t')
                      .Append(safeReal).Append('\n');
                }
                File.WriteAllText(CachePath, sb.ToString());
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[SaveHashCache] Failed to write cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Look up the cached slot+hash for a given host. Returns null if none.
        /// Also verifies the slot files are still on disk.
        /// </summary>
        public static Entry Get(ulong hostId)
        {
            EnsureLoaded();
            lock (_lock)
            {
                Entry newest = null;
                var staleKeys = new List<string>();
                foreach (var pair in _entries)
                {
                    Entry entry = pair.Value;
                    if (entry.hostId != hostId)
                        continue;
                    if (!SlotExists(entry.slotFilename))
                    {
                        staleKeys.Add(pair.Key);
                        continue;
                    }
                    if (newest == null || entry.storedAtUnix > newest.storedAtUnix)
                        newest = entry;
                }

                for (int i = 0; i < staleKeys.Count; i++)
                    _entries.Remove(staleKeys[i]);
                if (staleKeys.Count > 0)
                    SaveToDisk();

                return newest;
            }
        }

        /// <summary>
        /// Find the local mirror for one exact host campaign. A legacy host-only
        /// entry is upgraded when its saved world seed matches.
        /// </summary>
        public static Entry Get(
            ulong hostId,
            string hostSlotFilename,
            int worldSeed)
        {
            string normalizedHostSlot = NormalizeHostSlot(hostSlotFilename);
            if (hostId == 0 || string.IsNullOrEmpty(normalizedHostSlot) ||
                worldSeed < 0)
            {
                return null;
            }

            EnsureLoaded();
            lock (_lock)
            {
                Entry legacyMatch = null;
                string legacyKey = null;
                var staleKeys = new List<string>();
                foreach (var pair in _entries)
                {
                    Entry entry = pair.Value;
                    if (entry.hostId != hostId)
                        continue;
                    if (!SlotExists(entry.slotFilename))
                    {
                        staleKeys.Add(pair.Key);
                        continue;
                    }
                    int entryWorldSeed = entry.worldSeed;
                    if (entryWorldSeed < 0 &&
                        string.IsNullOrEmpty(entry.hostSlotFilename))
                    {
                        entryWorldSeed = ComputeSlotWorldSeed(
                            entry.slotFilename);
                    }
                    if (entryWorldSeed != worldSeed)
                        continue;

                    if (string.Equals(
                            NormalizeHostSlot(entry.hostSlotFilename),
                            normalizedHostSlot,
                            StringComparison.Ordinal))
                    {
                        return entry;
                    }

                    if (string.IsNullOrEmpty(entry.hostSlotFilename) &&
                        (legacyMatch == null ||
                         entry.storedAtUnix > legacyMatch.storedAtUnix))
                    {
                        legacyMatch = entry;
                        legacyKey = pair.Key;
                    }
                }

                for (int i = 0; i < staleKeys.Count; i++)
                    _entries.Remove(staleKeys[i]);

                if (legacyMatch != null)
                {
                    _entries.Remove(legacyKey);
                    legacyMatch.hostSlotFilename = normalizedHostSlot;
                    legacyMatch.worldSeed = worldSeed;
                    _entries[BuildStorageKey(legacyMatch)] = legacyMatch;
                    SaveToDisk();
                    CoopMod.Logger.LogInfo(
                        $"[SaveHashCache] Migrated host {hostId} world {worldSeed} " +
                        $"to host slot '{normalizedHostSlot}'");
                }
                else if (staleKeys.Count > 0)
                {
                    SaveToDisk();
                }

                return legacyMatch;
            }
        }

        /// <summary>
        /// Store or update the cached local mirror for an exact host campaign.
        /// </summary>
        public static void Put(
            ulong hostId,
            string hostSlotFilename,
            int worldSeed,
            string slotFilename,
            string hash,
            string realTime)
        {
            string normalizedHostSlot = NormalizeHostSlot(hostSlotFilename);
            if (hostId == 0 || string.IsNullOrEmpty(slotFilename) ||
                string.IsNullOrEmpty(hash))
            {
                return;
            }

            EnsureLoaded();
            lock (_lock)
            {
                var entry = new Entry
                {
                    hostId = hostId,
                    hostSlotFilename = normalizedHostSlot,
                    worldSeed = worldSeed,
                    slotFilename = slotFilename,
                    hash = hash,
                    realTime = realTime ?? string.Empty,
                    storedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                var replacedKeys = new List<string>();
                foreach (var pair in _entries)
                {
                    Entry existing = pair.Value;
                    if (existing.hostId == hostId &&
                        existing.worldSeed == worldSeed &&
                        string.Equals(
                            NormalizeHostSlot(existing.hostSlotFilename),
                            normalizedHostSlot,
                            StringComparison.Ordinal))
                    {
                        replacedKeys.Add(pair.Key);
                    }
                }
                for (int i = 0; i < replacedKeys.Count; i++)
                    _entries.Remove(replacedKeys[i]);

                _entries[BuildStorageKey(entry)] = entry;
                SaveToDisk();
            }
            CoopMod.Logger.LogInfo(
                $"[SaveHashCache] Stored local slot='{slotFilename}' for host " +
                $"{hostId}, host_slot='{normalizedHostSlot}', world={worldSeed}, " +
                $"hash={hash.Substring(0, Math.Min(12, hash.Length))}...");
        }

        /// <summary>
        /// Compute SHA256 hex digest over a byte buffer.
        /// </summary>
        public static string ComputeHash(byte[] data)
        {
            if (data == null || data.Length == 0) return string.Empty;
            using (var sha = SHA256.Create())
            {
                byte[] hashBytes = sha.ComputeHash(data);
                var sb = new StringBuilder(hashBytes.Length * 2);
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    sb.Append(hashBytes[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// Compute SHA256 hex of a save slot's .dat file. Returns empty string on error.
        /// </summary>
        public static string ComputeSlotHash(string slotFilenameNoExt)
        {
            try
            {
                string dataPath = PlatformSpecific.GetSaveFolder() + slotFilenameNoExt + ".dat";
                if (!File.Exists(dataPath)) return string.Empty;
                byte[] bytes = File.ReadAllBytes(dataPath);
                return ComputeHash(bytes);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[SaveHashCache] ComputeSlotHash('{slotFilenameNoExt}') failed: {ex.Message}");
                return string.Empty;
            }
        }

        public static int ComputeWorldSeed(byte[] saveData)
        {
            if (saveData == null || saveData.Length == 0)
                return -1;

            try
            {
                GameSave save = GameSave.FromBinary(saveData);
                return save != null ? save.dungeon_seed : -1;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[SaveHashCache] Could not read world seed: {ex.Message}");
                return -1;
            }
        }

        public static int ComputeSlotWorldSeed(string slotFilenameNoExt)
        {
            try
            {
                if (string.IsNullOrEmpty(slotFilenameNoExt))
                    return -1;
                string dataPath =
                    PlatformSpecific.GetSaveFolder() + slotFilenameNoExt + ".dat";
                return File.Exists(dataPath)
                    ? ComputeWorldSeed(File.ReadAllBytes(dataPath))
                    : -1;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[SaveHashCache] ComputeSlotWorldSeed('{slotFilenameNoExt}') " +
                    $"failed: {ex.Message}");
                return -1;
            }
        }

        private static string NormalizeHostSlot(string hostSlotFilename)
        {
            string value = (hostSlotFilename ?? string.Empty).Trim();
            return value.Length <= 256 &&
                   value.IndexOfAny(new[] { '\t', '\r', '\n' }) < 0
                ? value
                : string.Empty;
        }

        private static string BuildStorageKey(Entry entry)
        {
            string hostSlot = NormalizeHostSlot(entry?.hostSlotFilename);
            string localSlot = entry?.slotFilename ?? string.Empty;
            return $"{entry?.hostId ?? 0UL}|{entry?.worldSeed ?? -1}|" +
                   $"{hostSlot}|{localSlot}";
        }
    }
}

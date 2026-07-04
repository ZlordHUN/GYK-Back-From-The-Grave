using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Persistent cache mapping host Steam ID -> locally cached co-op save slot.
    /// Used to skip re-downloading the host's save when its contents haven't changed
    /// since the last join.
    ///
    /// On disk layout: {saveFolder}/coop_save_cache.json
    /// </summary>
    public static class SaveHashCache
    {
        [Serializable]
        public class Entry
        {
            public ulong hostId;
            public string slotFilename;   // e.g. "coop_20260424_225034"
            public string hash;           // SHA256 hex of the .dat file at the time of caching
            public string realTime;       // for UI / debugging
            public long storedAtUnix;
        }

        private const string CACHE_FILENAME = "coop_save_cache.txt";

        // Keep entries in memory after first load
        private static Dictionary<ulong, Entry> _entries;
        private static readonly object _lock = new object();

        private static string CachePath => PlatformSpecific.GetSaveFolder() + CACHE_FILENAME;

        private static void EnsureLoaded()
        {
            if (_entries != null) return;

            lock (_lock)
            {
                if (_entries != null) return;
                _entries = new Dictionary<ulong, Entry>();

                try
                {
                    string path = CachePath;
                    if (!File.Exists(path))
                    {
                        return;
                    }

                    // File format (one entry per line, tab-separated):
                    // <hostId>\t<slotFilename>\t<hash>\t<storedAtUnix>\t<realTime>
                    // realTime is last so it can safely contain spaces/commas.
                    foreach (string rawLine in File.ReadAllLines(path))
                    {
                        string line = rawLine?.Trim();
                        if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                        string[] parts = line.Split(new[] { '\t' }, 5);
                        if (parts.Length < 4) continue;

                        if (!ulong.TryParse(parts[0], out ulong hostId) || hostId == 0) continue;
                        string slotFilename = parts[1];
                        string hash = parts[2];
                        long.TryParse(parts[3], out long storedAt);
                        string realTime = parts.Length >= 5 ? parts[4] : string.Empty;

                        if (!SlotExists(slotFilename))
                        {
                            CoopMod.Logger.LogInfo(
                                $"[SaveHashCache] Dropping stale entry for host {hostId} (slot '{slotFilename}' no longer on disk)");
                            continue;
                        }

                        _entries[hostId] = new Entry
                        {
                            hostId = hostId,
                            slotFilename = slotFilename,
                            hash = hash,
                            realTime = realTime,
                            storedAtUnix = storedAt
                        };
                    }

                    CoopMod.Logger.LogInfo($"[SaveHashCache] Loaded {_entries.Count} cached host save(s) from {path}");
                }
                catch (Exception ex)
                {
                    CoopMod.Logger.LogWarning($"[SaveHashCache] Failed to load cache: {ex.Message}");
                    _entries = new Dictionary<ulong, Entry>();
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
                sb.AppendLine("# Format: hostId<TAB>slotFilename<TAB>hash<TAB>storedAtUnix<TAB>realTime");
                foreach (var e in _entries.Values)
                {
                    // Sanitise tabs and newlines in realTime just in case.
                    string safeReal = (e.realTime ?? string.Empty).Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
                    sb.Append(e.hostId).Append('\t')
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
                if (!_entries.TryGetValue(hostId, out var entry)) return null;
                if (!SlotExists(entry.slotFilename))
                {
                    _entries.Remove(hostId);
                    SaveToDisk();
                    return null;
                }
                return entry;
            }
        }

        /// <summary>
        /// Store or update the cached slot/hash for a host.
        /// </summary>
        public static void Put(ulong hostId, string slotFilename, string hash, string realTime)
        {
            if (hostId == 0 || string.IsNullOrEmpty(slotFilename) || string.IsNullOrEmpty(hash)) return;

            EnsureLoaded();
            lock (_lock)
            {
                _entries[hostId] = new Entry
                {
                    hostId = hostId,
                    slotFilename = slotFilename,
                    hash = hash,
                    realTime = realTime ?? string.Empty,
                    storedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                SaveToDisk();
            }
            CoopMod.Logger.LogInfo($"[SaveHashCache] Stored slot='{slotFilename}' hash={hash.Substring(0, Math.Min(12, hash.Length))}... for host {hostId}");
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
    }
}

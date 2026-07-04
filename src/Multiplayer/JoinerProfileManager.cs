using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Preserves a joiner's local player state across host-save loads.
    /// The host save remains authoritative for the world; these fields represent
    /// personal progression/state that should not be overwritten permanently.
    /// </summary>
    public class JoinerProfileManager : MonoBehaviour
    {
        private const int FormatVersion = 1;
        private const float PeriodicSnapshotSeconds = 60f;
        // Game load (which must finish before MainGame.game_started flips true)
        // can take well over a minute in multiplayer sessions — the previous
        // 10s timeout fired almost every join. Generous timeout keeps the
        // apply coroutine alive until the game is actually ready.
        private const float ApplyPollTimeoutSeconds = 120f;
        private const float ApplyPollIntervalSeconds = 0.25f;

        private static readonly string[] PlayerStateFieldNames =
        {
            "player_inventory",
            "inventory",
            "tech_points",
            "unlocked_crafts",
            "unlocked_techs",
            "player_stats",
            "perks",
            "skills",
            "personal_flags"
        };

        private static JoinerProfileManager _instance;
        public static JoinerProfileManager Instance => _instance;

        private bool appliedThisSession;
        private bool wasJoinerSessionActive;
        private float nextPeriodicSnapshotAt;
        private Coroutine applyCoroutine;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
            CoopMod.Logger.LogInfo("[JoinerProfile] Initialized");
        }

        private void Update()
        {
            if (ModConfig.EnableJoinerProfilePersistence != null && !ModConfig.EnableJoinerProfilePersistence.Value)
                return;

            bool joinerActive = IsJoinerSessionActive();
            if (wasJoinerSessionActive && !joinerActive)
            {
                if (appliedThisSession)
                    SnapshotNow("session ended");

                appliedThisSession = false;
                nextPeriodicSnapshotAt = 0f;
            }

            wasJoinerSessionActive = joinerActive;

            if (joinerActive && appliedThisSession && Time.realtimeSinceStartup >= nextPeriodicSnapshotAt)
            {
                nextPeriodicSnapshotAt = Time.realtimeSinceStartup + PeriodicSnapshotSeconds;
                SnapshotNow("periodic");
            }
        }

        private void OnApplicationQuit()
        {
            if (appliedThisSession && IsJoinerSessionActive())
                SnapshotNow("application quit");
        }

        public void OnHostSaveLoadStartedAsJoiner()
        {
            if (ModConfig.EnableJoinerProfilePersistence != null && !ModConfig.EnableJoinerProfilePersistence.Value)
                return;

            if (applyCoroutine != null)
                StopCoroutine(applyCoroutine);

            applyCoroutine = StartCoroutine(ApplyAfterPlayerReady());
        }

        public void SnapshotNow(string reason = "manual")
        {
            try
            {
                JoinerProfile profile = JoinerProfile.Load() ?? new JoinerProfile();
                int captured = profile.CaptureFromCurrentSave();
                if (captured > 0)
                {
                    profile.Save();
                    CoopMod.Logger.LogInfo($"[JoinerProfile] Snapshot saved ({reason}, fields={captured})");
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[JoinerProfile] Snapshot failed ({reason}): {ex.Message}");
            }
        }

        private IEnumerator ApplyAfterPlayerReady()
        {
            float deadline = Time.realtimeSinceStartup + ApplyPollTimeoutSeconds;
            while (Time.realtimeSinceStartup < deadline && !IsPlayerReady())
                yield return new WaitForSecondsRealtime(ApplyPollIntervalSeconds);

            if (!IsPlayerReady())
            {
                CoopMod.Logger.LogWarning("[JoinerProfile] Player was not ready before profile apply timeout");
                MarkAppliedForSession();
                applyCoroutine = null;
                yield break;
            }

            JoinerProfile profile = JoinerProfile.Load();
            if (profile == null)
            {
                CoopMod.Logger.LogInfo("[JoinerProfile] No prior joiner profile; this session will create one on disconnect");
                MarkAppliedForSession();
                applyCoroutine = null;
                yield break;
            }

            yield return null;
            int applied = profile.ApplyToCurrentSave();
            CoopMod.Logger.LogInfo($"[JoinerProfile] Applied saved joiner profile fields={applied}");
            MarkAppliedForSession();
            applyCoroutine = null;
        }

        private void MarkAppliedForSession()
        {
            appliedThisSession = true;
            nextPeriodicSnapshotAt = Time.realtimeSinceStartup + PeriodicSnapshotSeconds;
        }

        private static bool IsJoinerSessionActive()
        {
            var onlineCoop = OnlineCoopManager.Instance;
            return onlineCoop != null && onlineCoop.IsOnlineCoopEnabled && !onlineCoop.IsHost;
        }

        private static bool IsPlayerReady()
        {
            return MainGame.me != null &&
                   MainGame.game_started &&
                   MainGame.me.player != null &&
                   MainGame.me.save != null;
        }

        private sealed class JoinerProfile
        {
            private readonly Dictionary<string, string> fieldJson = new Dictionary<string, string>();

            private long savedAtUtcTicks;
            private string gameVersion;

            public static bool Exists => File.Exists(ProfilePath);

            private static string ProfilePath
            {
                get
                {
                    string cachePath = Paths.CachePath;
                    Directory.CreateDirectory(cachePath);
                    return Path.Combine(cachePath, "gkcoop_joiner_profile.bin");
                }
            }

            public int CaptureFromCurrentSave()
            {
                object save = MainGame.me?.save;
                if (save == null)
                    return 0;

                gameVersion = TryGetString(save, "game_version");
                int captured = 0;

                for (int i = 0; i < PlayerStateFieldNames.Length; i++)
                {
                    string fieldName = PlayerStateFieldNames[i];
                    FieldInfo field = save.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public);
                    if (field == null)
                        continue;

                    object value = field.GetValue(save);
                    if (value == null)
                        continue;

                    try
                    {
                        string json = JsonUtility.ToJson(value);
                        if (string.IsNullOrEmpty(json) || json == "{}")
                            continue;

                        fieldJson[fieldName] = json;
                        captured++;
                    }
                    catch (Exception ex)
                    {
                        CoopMod.Logger.LogDebug($"[JoinerProfile] Skipped capture field '{fieldName}': {ex.Message}");
                    }
                }

                return captured;
            }

            public int ApplyToCurrentSave()
            {
                object save = MainGame.me?.save;
                if (save == null)
                    return 0;

                int applied = 0;
                foreach (var pair in fieldJson)
                {
                    FieldInfo field = save.GetType().GetField(pair.Key, BindingFlags.Instance | BindingFlags.Public);
                    if (field == null)
                        continue;

                    try
                    {
                        object current = field.GetValue(save);
                        if (current != null)
                        {
                            JsonUtility.FromJsonOverwrite(pair.Value, current);
                        }
                        else
                        {
                            object replacement = JsonUtility.FromJson(pair.Value, field.FieldType);
                            if (replacement == null)
                                continue;

                            field.SetValue(save, replacement);
                        }

                        applied++;
                    }
                    catch (Exception ex)
                    {
                        CoopMod.Logger.LogDebug($"[JoinerProfile] Skipped apply field '{pair.Key}': {ex.Message}");
                    }
                }

                return applied;
            }

            public void Save()
            {
                savedAtUtcTicks = DateTime.UtcNow.Ticks;
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(FormatVersion);
                    writer.Write(savedAtUtcTicks);
                    writer.Write(gameVersion ?? string.Empty);
                    writer.Write(fieldJson.Count);
                    foreach (var pair in fieldJson)
                    {
                        writer.Write(pair.Key ?? string.Empty);
                        writer.Write(pair.Value ?? string.Empty);
                    }

                    writer.Flush();
                    File.WriteAllBytes(ProfilePath, stream.ToArray());
                }
            }

            public static JoinerProfile Load()
            {
                if (!Exists)
                    return null;

                try
                {
                    byte[] data = File.ReadAllBytes(ProfilePath);
                    using (var stream = new MemoryStream(data))
                    using (var reader = new BinaryReader(stream))
                    {
                        int version = reader.ReadInt32();
                        if (version != FormatVersion)
                        {
                            CoopMod.Logger.LogWarning($"[JoinerProfile] Version mismatch file={version} expected={FormatVersion}; ignoring profile");
                            return null;
                        }

                        var profile = new JoinerProfile
                        {
                            savedAtUtcTicks = reader.ReadInt64(),
                            gameVersion = reader.ReadString()
                        };

                        int count = reader.ReadInt32();
                        for (int i = 0; i < count; i++)
                        {
                            string key = reader.ReadString();
                            string value = reader.ReadString();
                            if (!string.IsNullOrEmpty(key))
                                profile.fieldJson[key] = value;
                        }

                        return profile;
                    }
                }
                catch (Exception ex)
                {
                    CoopMod.Logger.LogWarning($"[JoinerProfile] Load failed: {ex.Message}");
                    return null;
                }
            }

            private static string TryGetString(object obj, string fieldName)
            {
                try
                {
                    return obj.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(obj)?.ToString();
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}

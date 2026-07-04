using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Synchronizes host-controlled weather timelines between online co-op peers.
    /// The base game picks daily nature weather with UnityEngine.Random, so time sync alone
    /// does not guarantee both peers choose the same rain/fog/wind preset.
    /// </summary>
    public class WeatherSync : MonoBehaviour
    {
        private const byte LegacyPayloadVersion = 1;
        private const byte PayloadVersion = 2;
        private const float SyncIntervalSeconds = 2f;

        private static WeatherSync _instance;
        public static WeatherSync Instance => _instance;

        private bool isSyncEnabled;
        private float lastSendTime = -SyncIntervalSeconds;
        private int lastSentDay = -1;
        private int lastAppliedDay = -1;
        private float lastAppliedTimeK = -1f;
        private string lastAppliedEnvironmentFingerprint = string.Empty;
        private static FieldInfo environmentPresetField;
        private static FieldInfo storedEnvironmentPresetField;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);

            CoopMod.Logger.LogInfo("[WeatherSync] Initialized");
        }

        private void OnEnable()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnWeatherSyncReceived -= OnWeatherSyncReceived;
                SteamP2PManager.Instance.OnWeatherSyncReceived += OnWeatherSyncReceived;
            }
        }

        private void OnDisable()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnWeatherSyncReceived -= OnWeatherSyncReceived;
            }
        }

        public void EnableSync()
        {
            isSyncEnabled = true;
            lastSendTime = -SyncIntervalSeconds;
            lastSentDay = -1;
            lastAppliedDay = -1;
            lastAppliedTimeK = -1f;
            lastAppliedEnvironmentFingerprint = string.Empty;
            CoopMod.Logger.LogInfo("[WeatherSync] Sync enabled");

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop != null && onlineCoop.IsOnlineCoopEnabled && onlineCoop.IsHost)
            {
                SendWeatherSync();
            }
        }

        public void DisableSync()
        {
            isSyncEnabled = false;
            CoopMod.Logger.LogInfo("[WeatherSync] Sync disabled");
        }

        private void Update()
        {
            if (!isSyncEnabled)
                return;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled || !onlineCoop.IsHost)
                return;

            if (!CanUseWeatherEngine())
                return;

            int day = MainGame.me.save.day;
            bool dayChanged = day != lastSentDay;
            bool intervalElapsed = Time.realtimeSinceStartup - lastSendTime >= SyncIntervalSeconds;

            if (!dayChanged && !intervalElapsed)
                return;

            SendWeatherSync();
        }

        public void SendWeatherSync()
        {
            if (!CanUseWeatherEngine())
                return;

            byte[] payload;
            try
            {
                payload = SerializeSnapshot();
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[WeatherSync] Failed to serialize weather snapshot: {ex.Message}");
                return;
            }

            SteamP2PManager.Instance?.BroadcastWeatherSync(payload);
            lastSendTime = Time.realtimeSinceStartup;
            lastSentDay = MainGame.me.save.day;
        }

        private void OnWeatherSyncReceived(CSteamID senderID, byte[] payload)
        {
            if (!isSyncEnabled)
                return;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled || onlineCoop.IsHost)
                return;

            if (!IsExpectedHost(senderID))
                return;

            if (!CanUseWeatherEngine())
                return;

            try
            {
                WeatherSnapshot snapshot = DeserializeSnapshot(payload);
                ApplySnapshot(snapshot);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[WeatherSync] Failed to apply weather snapshot: {ex.Message}");
            }
        }

        private static bool CanUseWeatherEngine()
        {
            return MainGame.game_started
                && MainGame.me != null
                && MainGame.me.save != null
                && TimeOfDay.me != null
                && EnvironmentEngine.me != null;
        }

        private static bool IsExpectedHost(CSteamID senderID)
        {
            var lobbyID = SteamLobbyManager.Instance?.CurrentLobbyID ?? CSteamID.Nil;
            if (lobbyID == CSteamID.Nil)
                return true;

            return SteamMatchmaking.GetLobbyOwner(lobbyID) == senderID;
        }

        private static byte[] SerializeSnapshot()
        {
            EnvironmentEngine env = EnvironmentEngine.me;
            if (env.data == null)
            {
                env.data = new EnvironmentEngine.EnvironmentEngineData();
            }

            EnsureEnvironmentData(env);
            env.PrepareForSave();
            EnvironmentEngine.EnvironmentEngineData data = env.data;

            using (var stream = new MemoryStream(512))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PayloadVersion);
                writer.Write(MainGame.me.save.day);
                writer.Write(TimeOfDay.me.GetTimeK());
                writer.Write(env.weather_is_forced);

                WriteWeatherScalars(writer, env.states);
                WriteSwitchableLine(writer, data?.nature_weather_line);
                WriteForcedLine(writer, data?.forced_weather_line);
                writer.Write((int)(data?.state ?? EnvironmentEngine.State.RealTime));
                writer.Write((int)(data?.prev_state ?? EnvironmentEngine.State.RealTime));
                bool hasStoredState = data?.stored_state != null;
                writer.Write(hasStoredState);
                if (hasStoredState)
                {
                    writer.Write((int)data.stored_state.Value);
                }

                writer.Write(env.auto_adjust_time);
                writer.Write(GetSaveEnvironmentPresetName(stored: false));
                writer.Write(GetSaveEnvironmentPresetName(stored: true));
                WriteSwitchableLine(writer, data?.local_weather_line);
                WriteLutLine(writer, data?.lut_line);
                WriteSerializedWeatherStates(writer, data?.serialized_states);

                writer.Flush();
                return stream.ToArray();
            }
        }

        private static WeatherSnapshot DeserializeSnapshot(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                throw new InvalidDataException("Empty payload");

            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                byte version = reader.ReadByte();
                if (version != LegacyPayloadVersion && version != PayloadVersion)
                    throw new InvalidDataException($"Unsupported payload version {version}");

                var snapshot = new WeatherSnapshot
                {
                    Version = version,
                    Day = reader.ReadInt32(),
                    TimeK = reader.ReadSingle(),
                    WeatherIsForced = reader.ReadBoolean(),
                    Scalars = ReadWeatherScalars(reader),
                    NatureWeatherLine = ReadSwitchableLine(reader),
                    ForcedWeatherLine = ReadForcedLine(reader)
                };

                if (version >= PayloadVersion)
                {
                    snapshot.EngineState = (EnvironmentEngine.State)reader.ReadInt32();
                    snapshot.PreviousState = (EnvironmentEngine.State)reader.ReadInt32();
                    snapshot.HasStoredState = reader.ReadBoolean();
                    if (snapshot.HasStoredState)
                    {
                        snapshot.StoredState = (EnvironmentEngine.State)reader.ReadInt32();
                    }

                    snapshot.AutoAdjustTime = reader.ReadBoolean();
                    snapshot.EnvironmentPresetName = reader.ReadString();
                    snapshot.StoredEnvironmentPresetName = reader.ReadString();
                    snapshot.LocalWeatherLine = ReadSwitchableLine(reader);
                    snapshot.LutLine = ReadLutLine(reader);
                    snapshot.SerializedStates = ReadSerializedWeatherStates(reader);
                }

                return snapshot;
            }
        }

        private void ApplySnapshot(WeatherSnapshot snapshot)
        {
            // Time/weather packets are host authoritative and may legitimately rewind
            // during debug commands or host save hot-reloads, so accept the latest arrival.
            GameTimeSync.ApplyAuthoritativeTime(snapshot.Day, snapshot.TimeK);
            bool sameEnvironmentContext = IsSameLocalEnvironmentContext(snapshot);

            string environmentFingerprint = BuildEnvironmentFingerprint(snapshot);
            if (environmentFingerprint == lastAppliedEnvironmentFingerprint)
            {
                lastAppliedDay = snapshot.Day;
                lastAppliedTimeK = snapshot.TimeK;
                return;
            }

            EnvironmentEngine env = EnvironmentEngine.me;
            if (env.data == null)
            {
                env.data = new EnvironmentEngine.EnvironmentEngineData();
            }

            EnsureEnvironmentData(env);
            // Inside/outside state and current environment preset are local view context. The host
            // may be outside while this client is still in a house, so never apply those fields
            // from the weather packet. Sync the weather timelines and let the local engine evaluate
            // them for the local player's current context.
            if (sameEnvironmentContext || !snapshot.WeatherIsForced)
            {
                env.weather_is_forced = snapshot.WeatherIsForced;
            }

            env.data.nature_weather_line = BuildSwitchableLine(snapshot.NatureWeatherLine);
            if (snapshot.Version >= PayloadVersion)
            {
                env.data.local_weather_line = BuildSwitchableLine(snapshot.LocalWeatherLine);
            }
            env.data.forced_weather_line = BuildForcedLine(snapshot.ForcedWeatherLine);
            if (snapshot.Version >= PayloadVersion && sameEnvironmentContext)
            {
                env.data.lut_line = BuildLutLine(snapshot.LutLine);
                ApplySerializedWeatherStates(env, snapshot.SerializedStates);
            }

            if (env.data.local_weather_line == null)
            {
                env.data.local_weather_line = new List<SwitchableWeatherState>();
            }

            env.UpdateWeather();
            if (sameEnvironmentContext)
            {
                ApplyVisibleScalars(env, snapshot.Scalars);
            }

            lastAppliedDay = snapshot.Day;
            lastAppliedTimeK = snapshot.TimeK;
            lastAppliedEnvironmentFingerprint = environmentFingerprint;
        }

        private static bool IsSameLocalEnvironmentContext(WeatherSnapshot snapshot)
        {
            if (snapshot.Version < PayloadVersion)
                return true;

            EnvironmentEngine env = EnvironmentEngine.me;
            if (env?.data == null)
                return false;

            if (env.data.state != snapshot.EngineState)
                return false;

            string localPreset = GetSaveEnvironmentPresetName(stored: false) ?? string.Empty;
            string snapshotPreset = snapshot.EnvironmentPresetName ?? string.Empty;
            return string.Equals(localPreset, snapshotPreset, StringComparison.Ordinal);
        }

        private static string BuildEnvironmentFingerprint(WeatherSnapshot snapshot)
        {
            var builder = new StringBuilder(1024);
            builder.Append(snapshot.Version).Append('|');
            builder.Append(snapshot.Day).Append('|');
            builder.Append(snapshot.WeatherIsForced).Append('|');
            builder.Append(snapshot.AutoAdjustTime).Append('|');
            builder.Append((int)snapshot.EngineState).Append('|');
            builder.Append((int)snapshot.PreviousState).Append('|');
            builder.Append(snapshot.HasStoredState).Append('|');
            builder.Append(snapshot.HasStoredState ? (int)snapshot.StoredState : -1).Append('|');
            builder.Append(snapshot.EnvironmentPresetName ?? string.Empty).Append('|');
            builder.Append(snapshot.StoredEnvironmentPresetName ?? string.Empty).Append('|');
            AppendSwitchableLineFingerprint(builder, snapshot.NatureWeatherLine);
            AppendSwitchableLineFingerprint(builder, snapshot.LocalWeatherLine);
            AppendForcedLineFingerprint(builder, snapshot.ForcedWeatherLine);
            AppendLutLineFingerprint(builder, snapshot.LutLine);
            AppendSerializedStatesFingerprint(builder, snapshot.SerializedStates);
            AppendWeatherScalarsFingerprint(builder, snapshot.Scalars);
            return builder.ToString();
        }

        private static void AppendSwitchableLineFingerprint(StringBuilder builder, List<SwitchableWeatherWire> line)
        {
            builder.Append(line?.Count ?? 0).Append('|');
            if (line == null)
                return;

            foreach (SwitchableWeatherWire state in line)
            {
                AppendWeatherBaseFingerprint(builder, state.BaseState);
                builder.Append(state.PresetName ?? string.Empty).Append('|');
                builder.Append(state.DoDecNow).Append('|');
                builder.Append(state.StartRemovingTime).Append('|');
                builder.Append(state.DecTime).Append('|');
            }
        }

        private static void AppendForcedLineFingerprint(StringBuilder builder, List<ForcedWeatherWire> line)
        {
            builder.Append(line?.Count ?? 0).Append('|');
            if (line == null)
                return;

            foreach (ForcedWeatherWire state in line)
            {
                AppendWeatherBaseFingerprint(builder, state.BaseState);
                builder.Append(state.FlatTime).Append('|');
                builder.Append(state.DecTime).Append('|');
            }
        }

        private static void AppendLutLineFingerprint(StringBuilder builder, List<LutAtomWire> line)
        {
            builder.Append(line?.Count ?? 0).Append('|');
            if (line == null)
                return;

            foreach (LutAtomWire atom in line)
            {
                builder.Append(atom.LutName ?? string.Empty).Append('|');
                builder.Append(atom.Value).Append('|');
            }
        }

        private static void AppendSerializedStatesFingerprint(StringBuilder builder, List<SmartWeatherState.SerializedWeatherState> states)
        {
            builder.Append(states?.Count ?? 0).Append('|');
            if (states == null)
                return;

            foreach (SmartWeatherState.SerializedWeatherState state in states)
            {
                builder.Append(state.name ?? string.Empty).Append('|');
                builder.Append((int)state.type).Append('|');
                builder.Append(state.value).Append('|');
                builder.Append(state.forced_value).Append('|');
                builder.Append(state.nature_value).Append('|');
                builder.Append(state.controller_value).Append('|');
                builder.Append(state.cur_amount).Append('|');
                builder.Append(state.speed).Append('|');
                builder.Append(state.max).Append('|');
            }
        }

        private static void AppendWeatherScalarsFingerprint(StringBuilder builder, List<WeatherScalar> scalars)
        {
            builder.Append(scalars?.Count ?? 0).Append('|');
            if (scalars == null)
                return;

            foreach (WeatherScalar scalar in scalars)
            {
                builder.Append((int)scalar.Type).Append('|');
                builder.Append(scalar.Value).Append('|');
                builder.Append(scalar.ForcedValue).Append('|');
                builder.Append(scalar.NatureValue).Append('|');
                builder.Append(scalar.ControllerValue).Append('|');
                builder.Append(scalar.CurrentAmount).Append('|');
                builder.Append(scalar.Speed).Append('|');
                builder.Append(scalar.Max).Append('|');
            }
        }

        private static void AppendWeatherBaseFingerprint(StringBuilder builder, WeatherBaseWire state)
        {
            builder.Append((int)state.Type).Append('|');
            builder.Append(state.Value).Append('|');
            builder.Append(state.StartTime).Append('|');
            builder.Append(state.AttackTime).Append('|');
            builder.Append(state.LutTextureName ?? string.Empty).Append('|');
        }

        private static void ApplyEnvironmentState(EnvironmentEngine env, WeatherSnapshot snapshot)
        {
            SetSaveEnvironmentPresetNames(snapshot.EnvironmentPresetName, snapshot.StoredEnvironmentPresetName);
            env.EnableTime(snapshot.AutoAdjustTime);
            env.data.state = snapshot.EngineState;
            env.data.prev_state = snapshot.PreviousState;
            env.data.stored_state = snapshot.HasStoredState
                ? new EnvironmentEngine.State?(snapshot.StoredState)
                : null;
        }

        private static void ApplyVisibleScalars(EnvironmentEngine env, List<WeatherScalar> scalars)
        {
            if (env?.data == null || env.data.state == EnvironmentEngine.State.Inside || env.states == null)
                return;

            foreach (SmartWeatherState state in env.states)
            {
                if (state == null || state.type == SmartWeatherState.WeatherType.LUT || state.controller == null)
                    continue;

                WeatherScalar scalar;
                if (TryFindScalar(scalars, state.type, out scalar))
                {
                    state.max = scalar.Max;
                    state.speed = scalar.Speed;
                }

                state.SetValueImmediate(ClampWeatherValue(state.value, state.max));
            }
        }

        private static bool TryFindScalar(List<WeatherScalar> scalars, SmartWeatherState.WeatherType type, out WeatherScalar result)
        {
            if (scalars != null)
            {
                foreach (WeatherScalar scalar in scalars)
                {
                    if (scalar.Type == type)
                    {
                        result = scalar;
                        return true;
                    }
                }
            }

            result = default(WeatherScalar);
            return false;
        }

        private static float ClampWeatherValue(float value, float max)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return 0f;

            float safeMax = max > 0f ? max : 5f;
            return Mathf.Clamp(value, 0f, safeMax);
        }

        private static void EnsureEnvironmentData(EnvironmentEngine env)
        {
            if (env.data == null)
            {
                env.data = new EnvironmentEngine.EnvironmentEngineData();
            }

            if (env.data.nature_weather_line == null)
            {
                env.data.nature_weather_line = new List<SwitchableWeatherState>();
            }

            if (env.data.local_weather_line == null)
            {
                env.data.local_weather_line = new List<SwitchableWeatherState>();
            }

            if (env.data.forced_weather_line == null)
            {
                env.data.forced_weather_line = new List<ForcedWeatherState>();
            }

            if (env.data.lut_line == null)
            {
                env.data.lut_line = new List<LUTAtom>();
            }

            if (env.data.serialized_states == null)
            {
                env.data.serialized_states = new List<SmartWeatherState.SerializedWeatherState>();
            }

            if (env.data.lut_controller == null && MainGame.me != null)
            {
                env.data.lut_controller = MainGame.me.GetComponent<LUTController>();
            }
        }

        private static string GetSaveEnvironmentPresetName(bool stored)
        {
            GameSave save = MainGame.me?.save;
            if (save == null)
                return string.Empty;

            FieldInfo field = GetEnvironmentPresetField(stored);
            if (field == null)
                return string.Empty;

            return field.GetValue(save) as string ?? string.Empty;
        }

        private static void SetSaveEnvironmentPresetNames(string currentPreset, string storedPreset)
        {
            GameSave save = MainGame.me?.save;
            if (save == null)
                return;

            FieldInfo currentField = GetEnvironmentPresetField(stored: false);
            FieldInfo storedField = GetEnvironmentPresetField(stored: true);

            if (currentField != null)
            {
                currentField.SetValue(save, currentPreset ?? string.Empty);
            }

            if (storedField != null)
            {
                storedField.SetValue(save, storedPreset ?? string.Empty);
            }
        }

        private static FieldInfo GetEnvironmentPresetField(bool stored)
        {
            if (stored)
            {
                if (storedEnvironmentPresetField == null)
                {
                    storedEnvironmentPresetField = AccessTools.Field(typeof(GameSave), "_stored_environment_preset");
                }

                return storedEnvironmentPresetField;
            }

            if (environmentPresetField == null)
            {
                environmentPresetField = AccessTools.Field(typeof(GameSave), "_environment_preset");
            }

            return environmentPresetField;
        }

        private static void ApplyCurrentEnvironmentPreset(string presetName)
        {
            try
            {
                EnvironmentPreset preset = EnvironmentPreset.Load(string.IsNullOrEmpty(presetName) ? null : presetName);
                EnvironmentEngine.me.ApplyEnvironmentPreset(preset);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[WeatherSync] Failed to apply environment preset '{presetName}': {ex.Message}");
            }
        }

        private static void WriteWeatherScalars(BinaryWriter writer, SmartWeatherState[] states)
        {
            if (states == null)
            {
                writer.Write(0);
                return;
            }

            var serialized = new List<SmartWeatherState.SerializedWeatherState>();
            foreach (SmartWeatherState state in states)
            {
                if (state == null)
                    continue;

                serialized.Add(state.Serialize());
            }

            writer.Write(serialized.Count);
            foreach (SmartWeatherState.SerializedWeatherState state in serialized)
            {
                writer.Write((int)state.type);
                writer.Write(state.value);
                writer.Write(state.forced_value);
                writer.Write(state.nature_value);
                writer.Write(state.controller_value);
                writer.Write(state.cur_amount);
                writer.Write(state.speed);
                writer.Write(state.max);
            }
        }

        private static List<WeatherScalar> ReadWeatherScalars(BinaryReader reader)
        {
            int count = ReadSafeCount(reader, "weather scalar count");
            var scalars = new List<WeatherScalar>(count);

            for (int i = 0; i < count; i++)
            {
                scalars.Add(new WeatherScalar
                {
                    Type = (SmartWeatherState.WeatherType)reader.ReadInt32(),
                    Value = reader.ReadSingle(),
                    ForcedValue = reader.ReadSingle(),
                    NatureValue = reader.ReadSingle(),
                    ControllerValue = reader.ReadSingle(),
                    CurrentAmount = reader.ReadSingle(),
                    Speed = reader.ReadSingle(),
                    Max = reader.ReadSingle()
                });
            }

            return scalars;
        }

        private static void WriteSwitchableLine(BinaryWriter writer, List<SwitchableWeatherState> line)
        {
            if (line == null)
            {
                writer.Write(0);
                return;
            }

            writer.Write(line.Count);
            foreach (SwitchableWeatherState state in line)
            {
                writer.Write(state?.preset_name ?? string.Empty);
                WriteWeatherBase(writer, state);
                writer.Write(state != null && state.do_dec_now);
                writer.Write(state?.start_removing_time ?? -1f);
                writer.Write(state?.t_dec ?? 0f);
            }
        }

        private static List<SwitchableWeatherWire> ReadSwitchableLine(BinaryReader reader)
        {
            int count = ReadSafeCount(reader, "switchable weather count");
            var line = new List<SwitchableWeatherWire>(count);

            for (int i = 0; i < count; i++)
            {
                string presetName = reader.ReadString();
                WeatherBaseWire baseState = ReadWeatherBase(reader);
                line.Add(new SwitchableWeatherWire
                {
                    PresetName = presetName,
                    BaseState = baseState,
                    DoDecNow = reader.ReadBoolean(),
                    StartRemovingTime = reader.ReadSingle(),
                    DecTime = reader.ReadSingle()
                });
            }

            return line;
        }

        private static void WriteForcedLine(BinaryWriter writer, List<ForcedWeatherState> line)
        {
            if (line == null)
            {
                writer.Write(0);
                return;
            }

            writer.Write(line.Count);
            foreach (ForcedWeatherState state in line)
            {
                WriteWeatherBase(writer, state);
                writer.Write(state?.t_flat ?? 0f);
                writer.Write(state?.t_dec ?? 0f);
            }
        }

        private static List<ForcedWeatherWire> ReadForcedLine(BinaryReader reader)
        {
            int count = ReadSafeCount(reader, "forced weather count");
            var line = new List<ForcedWeatherWire>(count);

            for (int i = 0; i < count; i++)
            {
                WeatherBaseWire baseState = ReadWeatherBase(reader);
                line.Add(new ForcedWeatherWire
                {
                    BaseState = baseState,
                    FlatTime = reader.ReadSingle(),
                    DecTime = reader.ReadSingle()
                });
            }

            return line;
        }

        private static void WriteLutLine(BinaryWriter writer, List<LUTAtom> line)
        {
            if (line == null)
            {
                writer.Write(0);
                return;
            }

            writer.Write(line.Count);
            foreach (LUTAtom atom in line)
            {
                string lutName = atom?.lut_name;
                if (string.IsNullOrEmpty(lutName) && atom?.lut_texture != null)
                {
                    lutName = atom.lut_texture.name;
                }

                writer.Write(lutName ?? string.Empty);
                writer.Write(atom?.value ?? 0f);
            }
        }

        private static List<LutAtomWire> ReadLutLine(BinaryReader reader)
        {
            int count = ReadSafeCount(reader, "LUT atom count");
            var line = new List<LutAtomWire>(count);

            for (int i = 0; i < count; i++)
            {
                line.Add(new LutAtomWire
                {
                    LutName = reader.ReadString(),
                    Value = reader.ReadSingle()
                });
            }

            return line;
        }

        private static void WriteSerializedWeatherStates(BinaryWriter writer, List<SmartWeatherState.SerializedWeatherState> states)
        {
            if (states == null)
            {
                writer.Write(0);
                return;
            }

            writer.Write(states.Count);
            foreach (SmartWeatherState.SerializedWeatherState state in states)
            {
                writer.Write(state.name ?? string.Empty);
                writer.Write(state.controller_value);
                writer.Write(state.value);
                writer.Write(state.forced_value);
                writer.Write(state.nature_value);
                writer.Write(state.cur_amount);
                writer.Write(state.speed);
                writer.Write(state.max);
                writer.Write((int)state.type);
                writer.Write(state.enabled);
                writer.Write(state.previously_enabled);
            }
        }

        private static List<SmartWeatherState.SerializedWeatherState> ReadSerializedWeatherStates(BinaryReader reader)
        {
            int count = ReadSafeCount(reader, "serialized weather state count");
            var states = new List<SmartWeatherState.SerializedWeatherState>(count);

            for (int i = 0; i < count; i++)
            {
                states.Add(new SmartWeatherState.SerializedWeatherState
                {
                    name = reader.ReadString(),
                    controller_value = reader.ReadSingle(),
                    value = reader.ReadSingle(),
                    forced_value = reader.ReadSingle(),
                    nature_value = reader.ReadSingle(),
                    cur_amount = reader.ReadSingle(),
                    speed = reader.ReadSingle(),
                    max = reader.ReadSingle(),
                    type = (SmartWeatherState.WeatherType)reader.ReadInt32(),
                    enabled = reader.ReadBoolean(),
                    previously_enabled = reader.ReadBoolean()
                });
            }

            return states;
        }

        private static void WriteWeatherBase(BinaryWriter writer, WeatherStateBase state)
        {
            writer.Write(state?.t_start ?? 0f);
            writer.Write(state?.t_atk ?? 0f);
            writer.Write((int)(state?.type ?? SmartWeatherState.WeatherType.Rain));
            writer.Write(state?.value ?? 0f);
            writer.Write(GetLutTextureName(state));
        }

        private static WeatherBaseWire ReadWeatherBase(BinaryReader reader)
        {
            return new WeatherBaseWire
            {
                StartTime = reader.ReadSingle(),
                AttackTime = reader.ReadSingle(),
                Type = (SmartWeatherState.WeatherType)reader.ReadInt32(),
                Value = reader.ReadSingle(),
                LutTextureName = reader.ReadString()
            };
        }

        private static string GetLutTextureName(WeatherStateBase state)
        {
            if (state == null)
                return string.Empty;

            Texture2D texture = state.lut_texture;
            return texture != null ? texture.name ?? string.Empty : string.Empty;
        }

        private static List<SwitchableWeatherState> BuildSwitchableLine(List<SwitchableWeatherWire> wireLine)
        {
            var result = new List<SwitchableWeatherState>();
            if (wireLine == null)
                return result;

            foreach (SwitchableWeatherWire wire in wireLine)
            {
                SwitchableWeatherState state = BuildSwitchableState(wire);
                if (state != null)
                {
                    result.Add(state);
                }
            }

            return result;
        }

        private static SwitchableWeatherState BuildSwitchableState(SwitchableWeatherWire wire)
        {
            Texture2D lutTexture;
            if (!TryLoadLutTexture(wire.BaseState, out lutTexture))
                return null;

            return new SwitchableWeatherState
            {
                preset_name = wire.PresetName ?? string.Empty,
                t_start = wire.BaseState.StartTime,
                t_atk = wire.BaseState.AttackTime,
                type = wire.BaseState.Type,
                value = wire.BaseState.Value,
                lut_texture = lutTexture,
                do_dec_now = wire.DoDecNow,
                start_removing_time = wire.StartRemovingTime,
                t_dec = wire.DecTime
            };
        }

        private static List<ForcedWeatherState> BuildForcedLine(List<ForcedWeatherWire> wireLine)
        {
            var result = new List<ForcedWeatherState>();
            if (wireLine == null)
                return result;

            foreach (ForcedWeatherWire wire in wireLine)
            {
                ForcedWeatherState state = BuildForcedState(wire);
                if (state != null)
                {
                    result.Add(state);
                }
            }

            return result;
        }

        private static ForcedWeatherState BuildForcedState(ForcedWeatherWire wire)
        {
            Texture2D lutTexture;
            if (!TryLoadLutTexture(wire.BaseState, out lutTexture))
                return null;

            return new ForcedWeatherState(
                wire.BaseState.Type,
                lutTexture,
                wire.BaseState.Value,
                wire.BaseState.StartTime,
                wire.BaseState.AttackTime,
                wire.FlatTime,
                wire.DecTime);
        }

        private static List<LUTAtom> BuildLutLine(List<LutAtomWire> wireLine)
        {
            var result = new List<LUTAtom>();
            if (wireLine == null)
                return result;

            foreach (LutAtomWire wire in wireLine)
            {
                if (string.IsNullOrEmpty(wire.LutName))
                    continue;

                Texture2D texture = Resources.Load<Texture2D>(wire.LutName);
                if (texture == null)
                {
                    CoopMod.Logger.LogWarning($"[WeatherSync] Skipping LUT atom; texture not found: {wire.LutName}");
                    continue;
                }

                result.Add(new LUTAtom
                {
                    lut_name = wire.LutName,
                    lut_texture = texture,
                    value = wire.Value
                });
            }

            return result;
        }

        private static void ApplySerializedWeatherStates(EnvironmentEngine env, List<SmartWeatherState.SerializedWeatherState> serializedStates)
        {
            if (env?.states == null || serializedStates == null)
                return;

            env.data.serialized_states = serializedStates;
            foreach (SmartWeatherState.SerializedWeatherState serializedState in serializedStates)
            {
                SmartWeatherState target = FindWeatherState(env.states, serializedState);
                if (target == null)
                {
                    CoopMod.Logger.LogWarning($"[WeatherSync] Could not find weather state '{serializedState.name}' ({serializedState.type})");
                    continue;
                }

                target.Deserialize(serializedState);
            }
        }

        private static SmartWeatherState FindWeatherState(SmartWeatherState[] states, SmartWeatherState.SerializedWeatherState serializedState)
        {
            if (states == null)
                return null;

            for (int i = 0; i < states.Length; i++)
            {
                SmartWeatherState state = states[i];
                if (state != null && state.name == serializedState.name)
                    return state;
            }

            for (int i = 0; i < states.Length; i++)
            {
                SmartWeatherState state = states[i];
                if (state != null && state.type == serializedState.type)
                    return state;
            }

            return null;
        }

        private static bool TryLoadLutTexture(WeatherBaseWire state, out Texture2D texture)
        {
            texture = null;
            if (state.Type != SmartWeatherState.WeatherType.LUT)
                return true;

            if (string.IsNullOrEmpty(state.LutTextureName))
                return false;

            texture = Resources.Load<Texture2D>(state.LutTextureName);
            if (texture == null)
            {
                CoopMod.Logger.LogWarning($"[WeatherSync] Skipping LUT weather state; texture not found: {state.LutTextureName}");
                return false;
            }

            return true;
        }

        private static int ReadSafeCount(BinaryReader reader, string label)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > 256)
                throw new InvalidDataException($"Invalid {label}: {count}");

            return count;
        }

        private sealed class WeatherSnapshot
        {
            public byte Version;
            public int Day;
            public float TimeK;
            public bool WeatherIsForced;
            public EnvironmentEngine.State EngineState;
            public EnvironmentEngine.State PreviousState;
            public bool HasStoredState;
            public EnvironmentEngine.State StoredState;
            public bool AutoAdjustTime = true;
            public string EnvironmentPresetName = string.Empty;
            public string StoredEnvironmentPresetName = string.Empty;
            public List<WeatherScalar> Scalars;
            public List<SwitchableWeatherWire> NatureWeatherLine;
            public List<SwitchableWeatherWire> LocalWeatherLine;
            public List<ForcedWeatherWire> ForcedWeatherLine;
            public List<LutAtomWire> LutLine;
            public List<SmartWeatherState.SerializedWeatherState> SerializedStates;
        }

        private struct WeatherScalar
        {
            public SmartWeatherState.WeatherType Type;
            public float Value;
            public float ForcedValue;
            public float NatureValue;
            public float ControllerValue;
            public float CurrentAmount;
            public float Speed;
            public float Max;
        }

        private struct WeatherBaseWire
        {
            public float StartTime;
            public float AttackTime;
            public SmartWeatherState.WeatherType Type;
            public float Value;
            public string LutTextureName;
        }

        private struct SwitchableWeatherWire
        {
            public string PresetName;
            public WeatherBaseWire BaseState;
            public bool DoDecNow;
            public float StartRemovingTime;
            public float DecTime;
        }

        private struct ForcedWeatherWire
        {
            public WeatherBaseWire BaseState;
            public float FlatTime;
            public float DecTime;
        }

        private struct LutAtomWire
        {
            public string LutName;
            public float Value;
        }
    }
}

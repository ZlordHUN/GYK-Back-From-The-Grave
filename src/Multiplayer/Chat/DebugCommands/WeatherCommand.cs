using System;
using System.Collections.Generic;
using UnityEngine;

namespace GraveyardKeeperCoop.Multiplayer
{
    public static partial class DebugChatCommands
    {
        private const float ImmediateWeatherOffset = 0.001f;
        private const float LongWeatherDurationDays = 999f;
        private static string[] cachedWeatherPresetNames;

        private static void ExecuteWeatherCommand(
            List<string> args,
            Action<string> respond)
        {
            if (args.Count == 1 || IsWord(args[1], "status"))
            {
                respond(GetWeatherStatus());
                return;
            }

            if (IsWord(args[1], "clear"))
            {
                ClearWeather();
                respond("Cleared active weather.");
                return;
            }

            if (IsWord(args[1], "reset"))
            {
                ResetWeatherToDailyDefaults();
                respond("Reset weather to daily defaults.");
                return;
            }

            if (!IsWord(args[1], "set"))
            {
                respond("Usage: /weather set <preset>, /weather set " +
                        "rain|fog|wind [0..5], /weather clear, /weather reset");
                return;
            }

            if (args.Count < 3)
            {
                respond("Usage: /weather set <preset>, /weather set " +
                        "rain|fog|wind [0..5]");
                return;
            }

            if (TryParseWeatherType(
                    args[2],
                    out SmartWeatherState.WeatherType type))
            {
                float value = 5f;
                if (args.Count >= 4 && !TryParseFloat(args[3], out value))
                {
                    respond("Weather value must be a number from 0 to 5.");
                    return;
                }

                value = Mathf.Clamp(value, 0f, 5f);
                SetForcedWeatherValue(type, value);
                respond($"Set {type.ToString().ToLowerInvariant()} to " +
                        $"{value:0.##}.");
                return;
            }

            int presetStart = IsWord(args[2], "preset") ? 3 : 2;
            if (args.Count <= presetStart)
            {
                respond("Usage: /weather set preset <name>");
                return;
            }

            string presetName = string.Join(
                " ",
                args.GetRange(presetStart, args.Count - presetStart).ToArray());
            if (!SetWeatherPreset(presetName, out string loadedName))
            {
                respond($"Weather preset not found: {presetName}");
                return;
            }

            respond($"Set weather preset to {loadedName}.");
        }

        private static string GetWeatherStatus()
        {
            EnvironmentEngine env = EnvironmentEngine.me;
            int natureCount = env.data?.nature_weather_line?.Count ?? 0;
            int forcedCount = env.data?.forced_weather_line?.Count ?? 0;

            return "Weather " +
                   $"rain={GetWeatherValue(SmartWeatherState.WeatherType.Rain):0.##}, " +
                   $"fog={GetWeatherValue(SmartWeatherState.WeatherType.Fog):0.##}, " +
                   $"wind={GetWeatherValue(SmartWeatherState.WeatherType.Wind):0.##}, " +
                   $"nature={natureCount}, forced={forcedCount}.";
        }

        private static float GetWeatherValue(
            SmartWeatherState.WeatherType type)
        {
            SmartWeatherState state = FindWeatherState(type);
            return state != null ? state.value : 0f;
        }

        private static bool SetWeatherPreset(
            string presetName,
            out string loadedName)
        {
            loadedName = presetName;
            WeatherPreset preset = WeatherPreset.GetPreset(presetName);
            if (preset == null)
                return false;

            EnvironmentEngine env = EnvironmentEngine.me;
            EnsureWeatherData(env);

            env.weather_is_forced = false;
            env.data.nature_weather_line.Clear();
            env.data.forced_weather_line.Clear();

            float startTime = MainGame.game_time - ImmediateWeatherOffset;
            foreach (SwitchableWeatherState state in
                     SwitchableWeatherState.GetStatesFromPreset(startTime, preset))
            {
                state.t_start = startTime;
                state.t_atk = ImmediateWeatherOffset;
                state.do_dec_now = false;
                state.start_removing_time = -1f;
                state.t_dec = 0f;
                env.AddNatureWeatherState(state);
            }

            env.UpdateWeather();
            WeatherSync.Instance?.SendWeatherSync();

            loadedName = preset.preset_name;
            return true;
        }

        private static void SetForcedWeatherValue(
            SmartWeatherState.WeatherType type,
            float value)
        {
            EnvironmentEngine env = EnvironmentEngine.me;
            EnsureWeatherData(env);

            env.weather_is_forced = false;
            env.data.forced_weather_line.RemoveAll(
                state => state != null && state.type == type);

            env.AddForcedWeatherState(new ForcedWeatherState(
                type,
                null,
                value,
                MainGame.game_time - ImmediateWeatherOffset,
                ImmediateWeatherOffset,
                LongWeatherDurationDays,
                ImmediateWeatherOffset));

            env.UpdateWeather();
            WeatherSync.Instance?.SendWeatherSync();
        }

        private static void ClearWeather()
        {
            EnvironmentEngine env = EnvironmentEngine.me;
            EnsureWeatherData(env);

            env.weather_is_forced = false;
            env.data.nature_weather_line.Clear();
            env.data.local_weather_line.Clear();
            env.data.forced_weather_line.Clear();
            env.data.lut_line.Clear();

            if (env.states != null)
            {
                foreach (SmartWeatherState state in env.states)
                {
                    if (state == null)
                        continue;

                    state.value = 0f;
                    state.nature_value = 0f;
                    state.forced_value = 0f;
                    state.SetValueImmediate(0f);
                }
            }

            WeatherSync.Instance?.SendWeatherSync();
        }

        private static void ResetWeatherToDailyDefaults()
        {
            EnvironmentEngine env = EnvironmentEngine.me;
            env.weather_is_forced = false;
            env.ResetStates();
            SmartWeatherEngine.me.UpdateWeather();
            env.UpdateWeather();
            WeatherSync.Instance?.SendWeatherSync();
        }

        private static void EnsureWeatherData(EnvironmentEngine env)
        {
            if (env.data == null)
                env.data = new EnvironmentEngine.EnvironmentEngineData();

            if (env.data.nature_weather_line == null)
                env.data.nature_weather_line = new List<SwitchableWeatherState>();

            if (env.data.local_weather_line == null)
                env.data.local_weather_line = new List<SwitchableWeatherState>();

            if (env.data.forced_weather_line == null)
                env.data.forced_weather_line = new List<ForcedWeatherState>();

            if (env.data.lut_line == null)
                env.data.lut_line = new List<LUTAtom>();
        }

        private static SmartWeatherState FindWeatherState(
            SmartWeatherState.WeatherType type)
        {
            SmartWeatherState[] states = EnvironmentEngine.me?.states;
            if (states == null)
                return null;

            foreach (SmartWeatherState state in states)
            {
                if (state != null && state.type == type)
                    return state;
            }

            return null;
        }

        private static bool TryParseWeatherType(
            string token,
            out SmartWeatherState.WeatherType type)
        {
            if (IsWord(token, "rain"))
            {
                type = SmartWeatherState.WeatherType.Rain;
                return true;
            }

            if (IsWord(token, "fog"))
            {
                type = SmartWeatherState.WeatherType.Fog;
                return true;
            }

            if (IsWord(token, "wind"))
            {
                type = SmartWeatherState.WeatherType.Wind;
                return true;
            }

            type = SmartWeatherState.WeatherType.Rain;
            return false;
        }

        private static string[] CompleteWeatherCommand(
            CompletionContext context)
        {
            if (context.ArgumentIndex == 1)
            {
                return MatchPrefix(
                    context.Partial,
                    new[] { "set", "clear", "reset", "status" });
            }

            if (context.ArgumentIndex == 2 &&
                context.Args.Count > 1 &&
                IsWord(context.Args[1], "set"))
            {
                return MatchPrefix(
                    context.Partial,
                    MergeCandidates(
                        new[] { "preset", "rain", "fog", "wind" },
                        GetWeatherPresetNames()));
            }

            if (context.ArgumentIndex == 3 &&
                context.Args.Count > 2 &&
                IsWord(context.Args[1], "set"))
            {
                if (IsWord(context.Args[2], "preset"))
                {
                    return MatchPrefix(
                        context.Partial,
                        GetWeatherPresetNames());
                }

                if (TryParseWeatherType(context.Args[2], out _))
                {
                    return MatchPrefix(
                        context.Partial,
                        new[] { "0", "1", "2", "3", "4", "5" });
                }
            }

            return null;
        }

        private static string[] GetWeatherPresetNames()
        {
            if (cachedWeatherPresetNames != null)
                return cachedWeatherPresetNames;

            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            WeatherPreset[] presets = Resources.LoadAll<WeatherPreset>("Weather");
            if (presets != null)
            {
                for (int i = 0; i < presets.Length; i++)
                {
                    string name = presets[i]?.preset_name;
                    if (IsSupportedCompletionValue(name) && seen.Add(name))
                        names.Add(name);
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            cachedWeatherPresetNames = names.ToArray();
            return cachedWeatherPresetNames;
        }
    }
}

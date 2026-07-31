using System;
using System.Collections.Generic;
using System.Globalization;
using GraveyardKeeperCoop.Network;
using GraveyardKeeperCoopMod.Utils;
using Steamworks;
using UnityEngine;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Temporary host-only debug commands entered through the in-game chat overlay.
    /// Keep this isolated so it can be disabled or removed cleanly for release builds.
    /// </summary>
    public static class DebugChatCommands
    {
        private const float ImmediateWeatherOffset = 0.001f;
        private const float LongWeatherDurationDays = 999f;
        private const int MaxMoneyGrantBronze = 10000000;
        private const int MaxCorpseSpawnCount = 20;

        public static bool TryExecute(string text, Action<string> respond)
        {
            if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith("/"))
                return false;

            List<string> args = SplitArgs(text.Trim());
            if (args.Count == 0)
                return false;

            string command = NormalizeCommand(args[0]);
            if (command != "time" && command != "weather" && command != "give" &&
                command != "spawn" && command != "help")
                return false;

            // /help works anytime, no host/game check needed
            if (command == "help")
            {
                ExecuteHelpCommand(args, respond);
                return true;
            }


            respond = respond ?? (_ => { });

            if (!ModConfig.EnableCheats.Value && !IsHost())
            {
                respond("Cheats are disabled. Enable them in Multiplayer Settings.");
                return true;
            }

            if (!CanUseGameState())
            {
                respond("Debug commands require an active game.");
                return true;
            }

            try
            {
                if (command == "time")
                    ExecuteTimeCommand(args, respond);
                else if (command == "weather")
                    ExecuteWeatherCommand(args, respond);
                else if (command == "give")
                    ExecuteGiveCommand(args, respond);
                else
                    ExecuteSpawnCommand(args, respond);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[DebugCommands] Failed to execute '{text}': {ex}");
                respond($"Command failed: {ex.Message}");
            }

            return true;
        }

        private static void ExecuteHelpCommand(List<string> args, Action<string> respond)
        {
            string subcommand = args.Count > 1 ? NormalizeCommand(args[1]) : "";

            if (subcommand == "time")
            {
                respond("/time set <time> — set time (e.g. 12:00, 0.5)");
                respond("/time set <day> <time> — set day and time");
                respond("/time status — show current day/time");
            }
            else if (subcommand == "weather")
            {
                respond("/weather set preset <name> — set weather preset");
                respond("/weather set rain|fog|wind <0-5> — override weather value");
                respond("/weather clear — clear all forced weather");
                respond("/weather reset — reset to daily defaults");
            }
            else if (subcommand == "give")
            {
                respond("/give <item_id> [amount] — give item to yourself");
                respond("/give <player> <item_id> [amount] — give item to player");
                respond("/give money <bronze> — add to your personal balance");
                respond("Examples: /give money 100, /give PlayerName diamond 5");
            }
            else if (subcommand == "spawn")
            {
                respond("/spawn corpse [amount] — spawn complete corpses near you");
                respond($"Amount is limited to {MaxCorpseSpawnCount}.");
            }
            else
            {
                respond("/help [command] — show command help\n" +
                "/time — set or view time of day\n" +
                "/weather — set weather, rain, fog, wind\n" +
                "/give — give items to yourself or others\n" +
                "/spawn — spawn world objects near you\n" +
                "Use /help <command> for details. Cheats can be enabled in Multiplayer Settings.");
            }
        }

        private static void ExecuteTimeCommand(List<string> args, Action<string> respond)
        {
            if (args.Count == 1 || IsWord(args[1], "status"))
            {
                respond($"Time is day {MainGame.me.save.day}, {FormatTimeK(TimeOfDay.me.GetTimeK())}.");
                return;
            }

            if (!IsWord(args[1], "set"))
            {
                respond("Usage: /time set <time>, /time set <day> <time>, /time status");
                return;
            }

            if (!TryParseTimeSet(args, out int? day, out float timeK, out string error))
            {
                respond(error);
                return;
            }

            if (day.HasValue)
            {
                MainGame.me.save.day = Mathf.Max(1, day.Value);
            }

            TimeOfDay.me.SetTimeK(timeK);
            EnvironmentEngine.me.UpdateWeather();

            GameTimeSync.Instance?.SendTimeSync();
            WeatherSync.Instance?.SendWeatherSync();

            respond($"Set time to day {MainGame.me.save.day}, {FormatTimeK(timeK)}.");
        }

        private static bool TryParseTimeSet(List<string> args, out int? day, out float timeK, out string error)
        {
            day = null;
            timeK = 0f;
            error = null;

            int first = 2;
            if (args.Count <= first)
            {
                error = "Usage: /time set <time>, /time set <day> <time>";
                return false;
            }

            if (IsWord(args[first], "day") && args.Count > first + 1 && TryParseInt(args[first + 1], out int explicitDay))
            {
                day = explicitDay;
                first += 2;

                if (args.Count <= first)
                {
                    timeK = TimeOfDay.me.GetTimeK();
                    return true;
                }
            }
            else if (args.Count > first + 1 && TryParseInt(args[first], out int leadingDay))
            {
                day = leadingDay;
                first++;
            }

            if (!TryParseTimeToken(args[first], out timeK))
            {
                error = "Unknown time. Use night, morning, day, evening, 0..1, HH:mm, or hour 0..24.";
                return false;
            }

            for (int i = first + 1; i < args.Count; i++)
            {
                if (IsWord(args[i], "day") && i + 1 < args.Count && TryParseInt(args[i + 1], out int trailingDay))
                {
                    day = trailingDay;
                    i++;
                }
            }

            return true;
        }

        private static void ExecuteWeatherCommand(List<string> args, Action<string> respond)
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
                respond("Usage: /weather set <preset>, /weather set rain|fog|wind [0..5], /weather clear, /weather reset");
                return;
            }

            if (args.Count < 3)
            {
                respond("Usage: /weather set <preset>, /weather set rain|fog|wind [0..5]");
                return;
            }

            if (TryParseWeatherType(args[2], out SmartWeatherState.WeatherType type))
            {
                float value = 5f;
                if (args.Count >= 4 && !TryParseFloat(args[3], out value))
                {
                    respond("Weather value must be a number from 0 to 5.");
                    return;
                }

                value = Mathf.Clamp(value, 0f, 5f);
                SetForcedWeatherValue(type, value);
                respond($"Set {type.ToString().ToLowerInvariant()} to {value:0.##}.");
                return;
            }

            int presetStart = IsWord(args[2], "preset") ? 3 : 2;
            if (args.Count <= presetStart)
            {
                respond("Usage: /weather set preset <name>");
                return;
            }

            string presetName = string.Join(" ", args.GetRange(presetStart, args.Count - presetStart).ToArray());
            if (!SetWeatherPreset(presetName, out string loadedName))
            {
                respond($"Weather preset not found: {presetName}");
                return;
            }

            respond($"Set weather preset to {loadedName}.");
        }

        private static void ExecuteGiveCommand(List<string> args, Action<string> respond)
        {
            // /give <item_id> [amount]
            // /give <player> <item_id> [amount]
            if (args.Count < 2)
            {
                respond("Usage: /give <item_id> [amount]  or  /give <player> <item_id> [amount]");
                return;
            }

            WorldGameObject targetPlayer = MainGame.me?.player;
            int itemIdIndex = 1;
            int amountIndex = 2;

            // Try to parse the first arg as a player name
            string possiblePlayer = args[1];
            WorldGameObject namedPlayer = FindPlayerByName(possiblePlayer);
            if (namedPlayer != null)
            {
                if (args.Count < 3)
                {
                    respond($"Usage: /give {possiblePlayer} <item_id> [amount]");
                    return;
                }
                targetPlayer = namedPlayer;
                itemIdIndex = 2;
                amountIndex = 3;
            }

            string itemId = args[itemIdIndex];
            int amount = 1;
            if (args.Count > amountIndex && !TryParseInt(args[amountIndex], out amount))
            {
                respond($"Invalid amount: {args[amountIndex]}");
                return;
            }

            if (targetPlayer == null)
            {
                respond("No target player found.");
                return;
            }

            if (IsWord(itemId, "money"))
            {
                int bronze = Mathf.Clamp(amount, 1, MaxMoneyGrantBronze);
                ExecuteGiveMoney(targetPlayer, bronze, respond);
                return;
            }

            amount = Mathf.Max(1, Mathf.Min(amount, 999));

            // Create and add the item
            var item = new Item(itemId, amount);
            if (item?.definition == null)
            {
                respond($"Unknown item: {itemId}");
                return;
            }

            if (targetPlayer.AddToInventory(item))
            {
                string playerName = targetPlayer.is_player
                    ? (targetPlayer == MainGame.me?.player ? "You" : targetPlayer.obj_id)
                    : targetPlayer.obj_id;
                respond($"Gave {amount}x {itemId} to {playerName}.");
            }
            else
            {
                respond($"Failed to add {itemId} to inventory (full or cannot accept).");
            }
        }

        private static void ExecuteGiveMoney(
            WorldGameObject targetPlayer,
            int bronze,
            Action<string> respond)
        {
            if (targetPlayer != MainGame.me?.player)
            {
                respond("Money is personal; that player must run /give money locally.");
                return;
            }

            if (targetPlayer?.data == null)
            {
                respond("Money cannot be changed before the game world is ready.");
                return;
            }

            float amount = bronze / 100f;
            targetPlayer.data.money += amount;
            DropCollectGUI.OnMoneyCollected(amount);

            respond($"Added {FormatMoneyFromBronze(bronze)} to your balance. " +
                    $"Balance: {Trading.FormatMoney(targetPlayer.data.money, true, true)}.");
            CoopMod.Logger.LogInfo(
                $"[DebugCommands] Added {bronze} bronze to local money; " +
                $"balance={targetPlayer.data.money:F2}");
        }

        private static void ExecuteSpawnCommand(List<string> args, Action<string> respond)
        {
            if (args.Count < 2 || !IsWord(args[1], "corpse"))
            {
                respond("Usage: /spawn corpse [amount]");
                return;
            }

            int amount = 1;
            if (args.Count > 2 && !TryParseInt(args[2], out amount))
            {
                respond($"Invalid amount: {args[2]}");
                return;
            }

            if (amount < 1 || amount > MaxCorpseSpawnCount)
            {
                respond($"Amount must be from 1 to {MaxCorpseSpawnCount}.");
                return;
            }

            WorldGameObject player = MainGame.me?.player;
            GameSave save = MainGame.me?.save;
            if (player == null || save == null || MainGame.me.world_root == null)
            {
                respond("Corpses cannot be spawned before the game world is ready.");
                return;
            }

            int spawned = 0;
            for (int i = 0; i < amount; i++)
            {
                Item corpse = save.GenerateBody(1, 3);
                if (corpse == null || corpse.definition == null)
                    break;

                player.DropItem(corpse, Direction.None);
                spawned++;
            }

            if (spawned == 0)
            {
                respond("Failed to generate a corpse.");
                return;
            }

            respond(spawned == 1
                ? "Spawned 1 corpse."
                : $"Spawned {spawned} corpses.");
            CoopMod.Logger.LogInfo(
                $"[DebugCommands] Spawned {spawned} complete corpse(s) near the local player");
        }

        private static string FormatMoneyFromBronze(int bronze)
        {
            return Trading.FormatMoney(bronze / 100f, true, true);
        }

        private static WorldGameObject FindPlayerByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            string lower = name.ToLowerInvariant();

            // Check local player
            var localPlayer = MainGame.me?.player;
            if (localPlayer != null)
            {
                string localName = SteamHelper.GetLocalPlayerName()?.ToLowerInvariant();
                if (localName == lower) return localPlayer;

                // Also check by obj_id or display name
                string localTag = localPlayer.obj_id?.ToLowerInvariant();
                if (localTag == lower) return localPlayer;
            }

            OnlineCoopManager online = OnlineCoopManager.Instance;
            List<KeyValuePair<CSteamID, PlayerComponent>> remotes =
                online?.GetRemotePlayersSnapshot();
            if (remotes != null)
            {
                for (int i = 0; i < remotes.Count; i++)
                {
                    WorldGameObject remotePlayer = remotes[i].Value?.wgo;
                    if (remotePlayer == null)
                        continue;

                    string remoteName = SteamFriends
                        .GetFriendPersonaName(remotes[i].Key)
                        ?.ToLowerInvariant();
                    if (remoteName == lower)
                        return remotePlayer;

                    string remoteTag = remotePlayer.obj_id?.ToLowerInvariant();
                    if (remoteTag == lower)
                        return remotePlayer;
                }
            }

            return null;
        }

        private static bool IsHost()
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop != null && onlineCoop.IsOnlineCoopEnabled)
                return onlineCoop.IsHost;

            var lobby = SteamLobbyManager.Instance;
            return lobby != null && lobby.IsInLobby && lobby.IsHost;
        }

        private static bool CanUseGameState()
        {
            return MainGame.game_started
                && MainGame.me != null
                && MainGame.me.save != null
                && TimeOfDay.me != null
                && EnvironmentEngine.me != null;
        }

        private static string GetWeatherStatus()
        {
            EnvironmentEngine env = EnvironmentEngine.me;
            int natureCount = env.data?.nature_weather_line?.Count ?? 0;
            int forcedCount = env.data?.forced_weather_line?.Count ?? 0;

            return $"Weather rain={GetWeatherValue(SmartWeatherState.WeatherType.Rain):0.##}, fog={GetWeatherValue(SmartWeatherState.WeatherType.Fog):0.##}, wind={GetWeatherValue(SmartWeatherState.WeatherType.Wind):0.##}, nature={natureCount}, forced={forcedCount}.";
        }

        private static float GetWeatherValue(SmartWeatherState.WeatherType type)
        {
            SmartWeatherState state = FindWeatherState(type);
            return state != null ? state.value : 0f;
        }

        private static bool SetWeatherPreset(string presetName, out string loadedName)
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
            foreach (SwitchableWeatherState state in SwitchableWeatherState.GetStatesFromPreset(startTime, preset))
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

        private static void SetForcedWeatherValue(SmartWeatherState.WeatherType type, float value)
        {
            EnvironmentEngine env = EnvironmentEngine.me;
            EnsureWeatherData(env);

            env.weather_is_forced = false;
            env.data.forced_weather_line.RemoveAll(state => state != null && state.type == type);

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

        private static SmartWeatherState FindWeatherState(SmartWeatherState.WeatherType type)
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

        private static bool TryParseWeatherType(string token, out SmartWeatherState.WeatherType type)
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

        private static bool TryParseTimeToken(string token, out float timeK)
        {
            timeK = 0f;
            if (string.IsNullOrWhiteSpace(token))
                return false;

            string normalized = token.Trim().ToLowerInvariant();
            if (normalized == "night" || normalized == "midnight")
            {
                timeK = 0f;
                return true;
            }

            if (normalized == "morning" || normalized == "dawn")
            {
                timeK = 0.15f;
                return true;
            }

            if (normalized == "day" || normalized == "daytime")
            {
                timeK = 0.35f;
                return true;
            }

            if (normalized == "noon")
            {
                timeK = 0.5f;
                return true;
            }

            if (normalized == "evening" || normalized == "dusk")
            {
                timeK = 0.7f;
                return true;
            }

            if (normalized.Contains(":"))
                return TryParseClockTime(normalized, out timeK);

            if (!TryParseFloat(normalized, out float number))
                return false;

            if (number >= 0f && number <= 1f)
            {
                timeK = number;
                return true;
            }

            if (number >= 0f && number <= 24f)
            {
                timeK = Mathf.Clamp01(number / 24f);
                return true;
            }

            return false;
        }

        private static bool TryParseClockTime(string token, out float timeK)
        {
            timeK = 0f;
            string[] pieces = token.Split(':');
            if (pieces.Length != 2)
                return false;

            if (!int.TryParse(pieces[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hour)
                || !int.TryParse(pieces[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minute))
            {
                return false;
            }

            if (hour < 0 || hour > 24 || minute < 0 || minute > 59 || (hour == 24 && minute != 0))
                return false;

            timeK = Mathf.Clamp01((hour + minute / 60f) / 24f);
            return true;
        }

        private static string FormatTimeK(float timeK)
        {
            float hours = Mathf.Clamp01(timeK) * 24f;
            int hour = Mathf.FloorToInt(hours);
            int minute = Mathf.RoundToInt((hours - hour) * 60f);
            if (minute >= 60)
            {
                minute -= 60;
                hour++;
            }

            if (hour >= 24)
                hour = 0;

            return $"{hour:00}:{minute:00} (k={timeK:0.###})";
        }

        private static List<string> SplitArgs(string input)
        {
            var result = new List<string>();
            bool inQuotes = false;
            var current = new System.Text.StringBuilder();

            foreach (char c in input)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    AddCurrentArg(result, current);
                    continue;
                }

                current.Append(c);
            }

            AddCurrentArg(result, current);
            return result;
        }

        private static void AddCurrentArg(List<string> result, System.Text.StringBuilder current)
        {
            if (current.Length == 0)
                return;

            result.Add(current.ToString());
            current.Length = 0;
        }

        private static string NormalizeCommand(string token)
        {
            return (token ?? string.Empty).Trim().TrimStart('/').ToLowerInvariant();
        }

        private static bool IsWord(string actual, string expected)
        {
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseFloat(string token, out float value)
        {
            return float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseInt(string token, out int value)
        {
            return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        #region Autocomplete

        /// <summary>
        /// Tab-completion for debug commands. Called from ChatOverlay on Tab key.
        /// Returns the new text to set, or null if no completion available.
        /// </summary>
        public static string TryAutocomplete(string currentText, ref string lastCompletionText,
            ref string[] lastOptions, ref int lastIndex)
        {
            if (string.IsNullOrWhiteSpace(currentText) || !currentText.TrimStart().StartsWith("/"))
                return null;

            string text = currentText.TrimStart();
            List<string> args = SplitArgs(text);

            // If this is a new completion session (different text), generate fresh options
            if (lastCompletionText != text)
            {
                lastCompletionText = text;
                lastOptions = GenerateCompletions(text, args);
                lastIndex = -1;
            }

            if (lastOptions == null || lastOptions.Length == 0)
                return null;

            // Cycle to next option
            lastIndex = (lastIndex + 1) % lastOptions.Length;
            string completion = lastOptions[lastIndex];

            // Build the result: replace the last token with the completion
            return BuildCompletedText(text, args, completion);
        }

        private static string[] GenerateCompletions(string text, List<string> args)
        {
            string command = args.Count > 0 ? NormalizeCommand(args[0]) : "";

            // Completing command name
            if (args.Count == 1 && !text.EndsWith(" "))
            {
                return MatchPrefix(command, new[] { "time", "weather", "give", "help" });
            }

            // Completing arguments
            if (command == "time")
                return CompleteTimeCommand(args);
            if (command == "weather")
                return CompleteWeatherCommand(args);
            if (command == "give")
                return CompleteGiveCommand(args);

            return null;
        }

        private static string[] CompleteTimeCommand(List<string> args)
        {
            string lastArg = args.Count > 0 ? args[args.Count - 1] : "";
            bool endsWithSpace = args.Count > 0 && args[args.Count - 1].Length > 0
                && char.IsWhiteSpace(args[args.Count - 1][args[args.Count - 1].Length - 1]);

            // After /time: suggest "set", "status"
            if (args.Count <= 1 || (args.Count == 2 && args[1].Length == 0))
                return MatchPrefix(args.Count == 2 ? args[1] : "", new[] { "set", "status" });

            return null;
        }

        private static string[] CompleteWeatherCommand(List<string> args)
        {
            if (args.Count <= 1 || (args.Count == 2 && args[1].Length == 0))
                return MatchPrefix(args.Count == 2 ? args[1] : "", new[] { "set", "clear", "reset", "status" });

            if (args.Count >= 2 && IsWord(args[1], "set"))
            {
                if (args.Count <= 2 || (args.Count == 3 && args[2].Length == 0))
                    return MatchPrefix(args.Count == 3 ? args[2] : "",
                        new[] { "preset", "rain", "fog", "wind" });
            }

            return null;
        }

        private static string[] CompleteGiveCommand(List<string> args)
        {
            int argCount = args.Count;
            // Detect if the last token is a complete arg followed by space
            bool trailingSpace = argCount > 0 && args[argCount - 1].EndsWith(" ");

            // Player name or item ID
            if (argCount == 2)
            {
                string partial = args[1];
                var playerNames = GetOnlinePlayerNames();
                return MatchPrefix(partial, playerNames);
            }

            // Item ID
            if (argCount == 3)
            {
                // Could be player name + partial item, or item + partial amount
                string partial = args[2];
                // Check if arg[1] is a player name
                if (FindPlayerByName(args[1]) != null)
                    return MatchPrefix(partial, GetCommonItemIds());
                return null; // already have item + amount is numeric
            }

            return null;
        }

        private static string[] GetOnlinePlayerNames()
        {
            var names = new List<string>();
            string local = SteamHelper.GetLocalPlayerName();
            if (!string.IsNullOrEmpty(local)) names.Add(local);

            List<KeyValuePair<CSteamID, PlayerComponent>> remotes =
                OnlineCoopManager.Instance?.GetRemotePlayersSnapshot();
            if (remotes != null)
            {
                for (int i = 0; i < remotes.Count; i++)
                {
                    string remote =
                        SteamFriends.GetFriendPersonaName(remotes[i].Key);
                    if (!string.IsNullOrEmpty(remote) && !names.Contains(remote))
                        names.Add(remote);
                }
            }

            return names.ToArray();
        }

        private static string[] GetCommonItemIds()
        {
            return new[] {
                "money", "diamond", "gold_nugget", "silver_nugget",
                "body", "skull", "bone", "flesh", "fat", "blood", "skin",
                "brain", "heart", "intestine",
                "wood", "stone", "iron_ore", "coal", "clay", "sand",
                "carrot", "beet", "cabbage", "wheat", "onion", "pumpkin",
                "apple", "berry", "hops", "grape",
                "fish_perch", "fish_bream", "fish_sardine", "fish_tuna",
                "ceramic_1", "ceramic_2", "ceramic_3",
                "nails", "simple_iron_parts", "complex_iron_parts",
                "steel_parts", "wooden_beam", "wooden_plank",
                "paper", "glass", "concrete",
                "bee", "moth", "butterfly",
                "tr_key", "repair_tool",
                "bottle_berry_juice", "bottle_berry_braga",
            };
        }

        private static string[] MatchPrefix(string prefix, string[] candidates)
        {
            if (candidates == null || candidates.Length == 0) return null;
            string lower = (prefix ?? "").ToLowerInvariant();
            var matches = new List<string>();
            foreach (var c in candidates)
            {
                if (c != null && c.ToLowerInvariant().StartsWith(lower))
                    matches.Add(c);
            }
            return matches.Count > 0 ? matches.ToArray() : null;
        }

        private static string BuildCompletedText(string text, List<string> args, string completion)
        {
            // Find the last token (which may be empty if text ends with space)
            int lastSpace = text.LastIndexOf(' ');
            if (lastSpace < 0)
                return "/" + completion + " ";

            return text.Substring(0, lastSpace + 1) + completion + " ";
        }

        #endregion
    }
}

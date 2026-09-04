using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace GraveyardKeeperCoop.Multiplayer
{
    public static partial class DebugChatCommands
    {
        private static void ExecuteTimeCommand(
            List<string> args,
            Action<string> respond)
        {
            if (args.Count == 1 || IsWord(args[1], "status"))
            {
                respond($"Time is day {MainGame.me.save.day}, " +
                        $"{FormatTimeK(TimeOfDay.me.GetTimeK())}.");
                return;
            }

            if (!IsWord(args[1], "set"))
            {
                respond("Usage: /time set <time>, /time set <day> <time>, " +
                        "/time status");
                return;
            }

            if (!TryParseTimeSet(
                    args,
                    out int? day,
                    out float timeK,
                    out string error))
            {
                respond(error);
                return;
            }

            if (day.HasValue)
                MainGame.me.save.day = Mathf.Max(1, day.Value);

            TimeOfDay.me.SetTimeK(timeK);
            EnvironmentEngine.me.UpdateWeather();

            GameTimeSync.Instance?.SendTimeSync();
            WeatherSync.Instance?.SendWeatherSync();

            respond($"Set time to day {MainGame.me.save.day}, " +
                    $"{FormatTimeK(timeK)}.");
        }

        private static bool TryParseTimeSet(
            List<string> args,
            out int? day,
            out float timeK,
            out string error)
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

            if (IsWord(args[first], "day") &&
                args.Count > first + 1 &&
                TryParseInt(args[first + 1], out int explicitDay))
            {
                day = explicitDay;
                first += 2;

                if (args.Count <= first)
                {
                    timeK = TimeOfDay.me.GetTimeK();
                    return true;
                }
            }
            else if (args.Count > first + 1 &&
                     TryParseInt(args[first], out int leadingDay))
            {
                day = leadingDay;
                first++;
            }

            if (!TryParseTimeToken(args[first], out timeK))
            {
                error = "Unknown time. Use night, morning, day, evening, 0..1, " +
                        "HH:mm, or hour 0..24.";
                return false;
            }

            for (int i = first + 1; i < args.Count; i++)
            {
                if (IsWord(args[i], "day") &&
                    i + 1 < args.Count &&
                    TryParseInt(args[i + 1], out int trailingDay))
                {
                    day = trailingDay;
                    i++;
                }
            }

            return true;
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

            if (!int.TryParse(
                    pieces[0],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int hour) ||
                !int.TryParse(
                    pieces[1],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int minute))
            {
                return false;
            }

            if (hour < 0 || hour > 24 || minute < 0 || minute > 59 ||
                (hour == 24 && minute != 0))
            {
                return false;
            }

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

        private static string[] CompleteTimeCommand(CompletionContext context)
        {
            if (context.ArgumentIndex == 1)
                return MatchPrefix(context.Partial, new[] { "set", "status" });

            if (context.ArgumentIndex == 2 &&
                context.Args.Count > 1 &&
                IsWord(context.Args[1], "set"))
            {
                return MatchPrefix(
                    context.Partial,
                    new[]
                    {
                        "dawn", "morning", "noon", "evening", "night", "day"
                    });
            }

            return null;
        }
    }
}

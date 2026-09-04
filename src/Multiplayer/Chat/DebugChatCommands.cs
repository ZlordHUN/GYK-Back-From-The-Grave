using System;
using System.Collections.Generic;
using System.Globalization;
using GraveyardKeeperCoop.Network;
using GraveyardKeeperCoopMod.Utils;
using Steamworks;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Routes slash commands entered through the in-game chat overlay.
    /// Command implementations live in the DebugCommands directory so each
    /// command can be maintained independently.
    /// </summary>
    public static partial class DebugChatCommands
    {
        private static readonly string[] CommandNames =
        {
            "give", "help", "spawn", "test", "time", "tp", "weather"
        };

        public static bool TryExecute(string text, Action<string> respond)
        {
            if (string.IsNullOrWhiteSpace(text) ||
                !text.TrimStart().StartsWith("/", StringComparison.Ordinal))
            {
                return false;
            }

            List<string> args = SplitArgs(text.Trim());
            if (args.Count == 0)
                return false;

            string command = NormalizeCommand(args[0]);
            if (!IsKnownCommand(command))
                return false;

            respond = respond ?? (_ => { });

            // /help only reads command metadata and remains available outside a game.
            if (command == "help")
            {
                ExecuteHelpCommand(args, respond);
                return true;
            }

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
                switch (command)
                {
                    case "give":
                        ExecuteGiveCommand(args, respond);
                        break;
                    case "spawn":
                        ExecuteSpawnCommand(args, respond);
                        break;
                    case "test":
                        ExecuteTestCommand(args, respond);
                        break;
                    case "time":
                        ExecuteTimeCommand(args, respond);
                        break;
                    case "tp":
                        ExecuteTeleportCommand(args, respond);
                        break;
                    case "weather":
                        ExecuteWeatherCommand(args, respond);
                        break;
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[DebugCommands] Failed to execute '{text}': {ex}");
                respond($"Command failed: {ex.Message}");
            }

            return true;
        }

        private static bool IsKnownCommand(string command)
        {
            for (int i = 0; i < CommandNames.Length; i++)
            {
                if (IsWord(command, CommandNames[i]))
                    return true;
            }

            return false;
        }

        private static WorldGameObject FindPlayerByName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            string lower = name.ToLowerInvariant();
            WorldGameObject localPlayer = MainGame.me?.player;
            if (localPlayer != null)
            {
                string localName = SteamHelper.GetLocalPlayerName()?.ToLowerInvariant();
                if (localName == lower)
                    return localPlayer;

                string localTag = localPlayer.obj_id?.ToLowerInvariant();
                if (localTag == lower)
                    return localPlayer;
            }

            List<KeyValuePair<CSteamID, PlayerComponent>> remotes =
                OnlineCoopManager.Instance?.GetRemotePlayersSnapshot();
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
            OnlineCoopManager onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop != null && onlineCoop.IsOnlineCoopEnabled)
                return onlineCoop.IsHost;

            SteamLobbyManager lobby = SteamLobbyManager.Instance;
            return lobby != null && lobby.IsInLobby && lobby.IsHost;
        }

        private static bool CanUseGameState()
        {
            return MainGame.game_started &&
                   MainGame.me != null &&
                   MainGame.me.save != null &&
                   TimeOfDay.me != null &&
                   EnvironmentEngine.me != null;
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

        private static void AddCurrentArg(
            List<string> result,
            System.Text.StringBuilder current)
        {
            if (current.Length == 0)
                return;

            result.Add(current.ToString());
            current.Length = 0;
        }

        private static string NormalizeCommand(string token)
        {
            return (token ?? string.Empty)
                .Trim()
                .TrimStart('/')
                .ToLowerInvariant();
        }

        private static bool IsWord(string actual, string expected)
        {
            return string.Equals(
                actual,
                expected,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseFloat(string token, out float value)
        {
            return float.TryParse(
                token,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static bool TryParseInt(string token, out int value)
        {
            return int.TryParse(
                token,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
        }

        #region Autocomplete

        internal sealed class CommandCompletion
        {
            internal readonly string CompletedText;
            internal readonly string DisplayText;

            internal CommandCompletion(string completedText, string displayText)
            {
                CompletedText = completedText;
                DisplayText = displayText;
            }
        }

        /// <summary>
        /// Returns the first completion as ghost text while the player types.
        /// </summary>
        public static bool TryGetAutocompleteSuggestion(
            string currentText,
            out string completedText)
        {
            return TryGetAutocompleteSuggestion(
                currentText,
                out completedText,
                out _);
        }

        public static bool TryGetAutocompleteSuggestion(
            string currentText,
            out string completedText,
            out string displayText)
        {
            completedText = null;
            displayText = null;
            CommandCompletion[] options =
                GetAutocompleteSuggestionOptions(currentText);
            if (options.Length == 0)
                return false;

            completedText = options[0].CompletedText;
            // Ghost text previews the exact text Tab inserts. The friendly label
            // is carried separately for the popup list.
            displayText = completedText;
            return !string.Equals(
                currentText,
                completedText,
                StringComparison.Ordinal);
        }

        public static string[] GetAutocompleteSuggestions(string currentText)
        {
            CommandCompletion[] options =
                GetAutocompleteSuggestionOptions(currentText);
            var completions = new string[options.Length];
            for (int i = 0; i < options.Length; i++)
                completions[i] = options[i].CompletedText;
            return completions;
        }

        internal static CommandCompletion[] GetAutocompleteSuggestionOptions(
            string currentText)
        {
            if (!IsCommandInput(currentText))
                return new CommandCompletion[0];

            CompletionContext context = BuildCompletionContext(currentText);
            if (context.Args.Count == 0)
                return new CommandCompletion[0];

            string[] candidates = GenerateCompletionCandidates(context);
            if (candidates == null || candidates.Length == 0)
                return new CommandCompletion[0];

            var completions = new List<CommandCompletion>(candidates.Length);
            for (int i = 0; i < candidates.Length; i++)
            {
                string completion = BuildCompletedText(context, candidates[i]);
                if (!string.Equals(
                        currentText,
                        completion,
                        StringComparison.Ordinal))
                {
                    completions.Add(new CommandCompletion(
                        completion,
                        GetCompletionCandidateDisplayText(
                            context,
                            candidates[i])));
                }
            }

            return completions.ToArray();
        }

        private static string GetCompletionCandidateDisplayText(
            CompletionContext context,
            string candidate)
        {
            string command = context.Args.Count > 0
                ? NormalizeCommand(context.Args[0])
                : string.Empty;
            return command == "give" && context.ArgumentIndex > 0
                ? GetGiveCompletionDisplay(candidate)
                : candidate;
        }

        /// <summary>
        /// Applies the completion currently shown as ghost text.
        /// </summary>
        public static string TryAutocomplete(string currentText)
        {
            return TryGetAutocompleteSuggestion(
                currentText,
                out string completedText)
                ? completedText
                : null;
        }

        private static string[] GenerateCompletionCandidates(
            CompletionContext context)
        {
            List<string> args = context.Args;
            string command = NormalizeCommand(args[0]);

            if (context.ArgumentIndex == 0)
                return MatchPrefix(command, CommandNames);

            if (command == "help" && context.ArgumentIndex == 1)
                return MatchPrefix(context.Partial, CommandNames);
            if (command == "give")
                return CompleteGiveCommand(context);
            if (command == "spawn")
                return CompleteSpawnCommand(context);
            if (command == "test")
                return CompleteTestCommand(context);
            if (command == "time")
                return CompleteTimeCommand(context);
            if (command == "tp")
                return CompleteTeleportCommand(context);
            if (command == "weather")
                return CompleteWeatherCommand(context);

            return null;
        }

        private static bool IsCommandInput(string text)
        {
            return !string.IsNullOrWhiteSpace(text) &&
                   text.TrimStart().StartsWith("/", StringComparison.Ordinal);
        }

        private static CompletionContext BuildCompletionContext(string text)
        {
            List<string> args = SplitArgs(text);
            bool trailingWhitespace =
                text.Length > 0 && char.IsWhiteSpace(text[text.Length - 1]);
            if (trailingWhitespace)
                args.Add(string.Empty);

            int tokenStart = FindCurrentTokenStart(text);
            string partial = args.Count > 0
                ? args[args.Count - 1]
                : string.Empty;
            return new CompletionContext(
                text,
                args,
                args.Count - 1,
                tokenStart,
                partial);
        }

        private static int FindCurrentTokenStart(string text)
        {
            bool inQuotes = false;
            int tokenStart = 0;
            bool tokenStarted = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"')
                {
                    if (!tokenStarted)
                    {
                        tokenStart = i;
                        tokenStarted = true;
                    }

                    inQuotes = !inQuotes;
                    continue;
                }

                if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    tokenStarted = false;
                    tokenStart = i + 1;
                }
                else if (!tokenStarted)
                {
                    tokenStart = i;
                    tokenStarted = true;
                }
            }

            return tokenStart;
        }

        private static string BuildCompletedText(
            CompletionContext context,
            string completion)
        {
            string token = context.ArgumentIndex == 0
                ? "/" + completion
                : QuoteIfNeeded(completion);
            return context.Text.Substring(0, context.TokenStart) + token + " ";
        }

        private static string QuoteIfNeeded(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsWhiteSpace(value[i]))
                    return "\"" + value + "\"";
            }

            return value;
        }

        private static string[] MergeCandidates(
            string[] first,
            string[] second)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddCandidates(result, seen, first);
            AddCandidates(result, seen, second);
            return result.ToArray();
        }

        private static void AddCandidates(
            List<string> result,
            HashSet<string> seen,
            string[] candidates)
        {
            if (candidates == null)
                return;

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
                    result.Add(candidate);
            }
        }

        private static string[] GetOnlinePlayerNames()
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string local = SteamHelper.GetLocalPlayerName();
            if (IsSupportedCompletionValue(local) && seen.Add(local))
                names.Add(local);

            List<KeyValuePair<CSteamID, PlayerComponent>> remotes =
                OnlineCoopManager.Instance?.GetRemotePlayersSnapshot();
            if (remotes != null)
            {
                for (int i = 0; i < remotes.Count; i++)
                {
                    string remote =
                        SteamFriends.GetFriendPersonaName(remotes[i].Key);
                    if (IsSupportedCompletionValue(remote) && seen.Add(remote))
                        names.Add(remote);
                }
            }

            return names.ToArray();
        }

        private static bool IsSupportedCompletionValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOf('"') >= 0)
                return false;

            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsControl(value[i]))
                    return false;
            }

            return true;
        }

        private static string[] MatchPrefix(
            string prefix,
            string[] candidates)
        {
            if (candidates == null || candidates.Length == 0)
                return null;

            string lower = (prefix ?? string.Empty).ToLowerInvariant();
            var matches = new List<string>();
            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (candidate != null &&
                    candidate.ToLowerInvariant().StartsWith(lower))
                {
                    matches.Add(candidate);
                }
            }

            return matches.Count > 0 ? matches.ToArray() : null;
        }

        private sealed class CompletionContext
        {
            public readonly string Text;
            public readonly List<string> Args;
            public readonly int ArgumentIndex;
            public readonly int TokenStart;
            public readonly string Partial;

            public CompletionContext(
                string text,
                List<string> args,
                int argumentIndex,
                int tokenStart,
                string partial)
            {
                Text = text;
                Args = args;
                ArgumentIndex = argumentIndex;
                TokenStart = tokenStart;
                Partial = partial;
            }
        }

        #endregion
    }
}

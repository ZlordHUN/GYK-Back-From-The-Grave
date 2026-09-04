using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Builds the /give item vocabulary from the live game balance. This keeps
    /// base-game, DLC, and future item definitions available without maintaining
    /// a second hard-coded item list.
    /// </summary>
    internal static class GiveItemCatalog
    {
        private const string BronzeBeerId = "cup_beer:1";

        private sealed class Entry
        {
            public ItemDefinition Definition;
            public string Id;
            public string FamilyId;
            public string FriendlyName;
            public string QualifiedName;
            public string BaseAlias;
            public string CanonicalAlias;
            public string CompletionDisplay;
            public readonly List<string> MatchAliases = new List<string>();
        }

        private static GameBalance cachedBalance;
        private static int cachedItemCount = -1;
        private static string cachedLanguage;
        private static Entry[] cachedEntries = new Entry[0];
        private static Dictionary<string, Entry> entriesById =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, List<Entry>> entriesByAlias =
            new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);

        internal static bool TryResolve(
            string nameOrId,
            out string itemId,
            out string friendlyName,
            out string error)
        {
            EnsureBuilt();
            itemId = null;
            friendlyName = null;
            error = null;

            string requested = (nameOrId ?? string.Empty).Trim();
            if (requested.Length == 0)
            {
                error = "Item name cannot be empty.";
                return false;
            }

            if (entriesById.TryGetValue(requested, out Entry exact))
            {
                itemId = exact.Id;
                friendlyName = exact.QualifiedName;
                return true;
            }

            string alias = NormalizeAlias(requested);
            if (!entriesByAlias.TryGetValue(alias, out List<Entry> matches) ||
                matches.Count == 0)
            {
                error = $"Unknown item or friendly name: {nameOrId}";
                return false;
            }

            if (matches.Count > 1)
            {
                var choices = new List<string>(matches.Count);
                for (int i = 0; i < matches.Count; i++)
                {
                    choices.Add(matches[i].CanonicalAlias +
                                " [" + matches[i].Id + "]");
                }

                error = $"Ambiguous item name '{nameOrId}'. Use one of: " +
                        string.Join(", ", choices.ToArray());
                return false;
            }

            Entry resolved = matches[0];
            itemId = resolved.Id;
            friendlyName = resolved.QualifiedName;
            return true;
        }

        internal static bool IsRecognizedToken(string token)
        {
            EnsureBuilt();
            string requested = (token ?? string.Empty).Trim();
            if (requested.Length == 0)
                return false;

            return entriesById.ContainsKey(requested) ||
                   entriesByAlias.ContainsKey(NormalizeAlias(requested));
        }

        internal static string[] GetCompletionCandidates(
            string partial,
            bool includePersonalValues)
        {
            EnsureBuilt();
            string rawPartial = (partial ?? string.Empty).Trim().Trim('"');
            string normalizedPartial = NormalizeAlias(rawPartial);
            var result = new List<string>(cachedEntries.Length + 2);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (includePersonalValues)
            {
                AddPseudoCompletion(
                    result,
                    seen,
                    "energy",
                    rawPartial,
                    normalizedPartial);
                AddPseudoCompletion(
                    result,
                    seen,
                    "money",
                    rawPartial,
                    normalizedPartial);
            }

            for (int i = 0; i < cachedEntries.Length; i++)
            {
                Entry entry = cachedEntries[i];
                bool rawIdMatch = StartsWith(entry.Id, rawPartial);
                if (!rawIdMatch && !MatchesFriendlyPrefix(
                        entry,
                        rawPartial,
                        normalizedPartial))
                {
                    continue;
                }

                // Preserve a technical-ID completion when the player started one;
                // otherwise insert the readable, parser-safe canonical alias.
                string completion = rawIdMatch && rawPartial.Length > 0 &&
                                    !IsReservedAlias(entry.Id)
                    ? entry.Id
                    : entry.CanonicalAlias;
                if (seen.Add(completion))
                    result.Add(completion);
            }

            return result.ToArray();
        }

        internal static bool TryGetCompletionDisplay(
            string candidate,
            out string displayText)
        {
            EnsureBuilt();
            if (string.Equals(candidate, "energy", StringComparison.OrdinalIgnoreCase))
            {
                displayText = "Energy — personal energy";
                return true;
            }

            if (string.Equals(candidate, "money", StringComparison.OrdinalIgnoreCase))
            {
                displayText = "Money — personal balance (bronze)";
                return true;
            }

            if (entriesById.TryGetValue(candidate ?? string.Empty, out Entry exact))
            {
                displayText = exact.CompletionDisplay;
                return true;
            }

            string alias = NormalizeAlias(candidate);
            if (entriesByAlias.TryGetValue(alias, out List<Entry> matches) &&
                matches.Count == 1)
            {
                displayText = matches[0].CompletionDisplay;
                return true;
            }

            displayText = null;
            return false;
        }

        private static void EnsureBuilt()
        {
            GameBalance balance = GameBalance.me;
            List<ItemDefinition> definitions = balance?.items_data;
            int itemCount = definitions?.Count ?? 0;
            string language = GetCurrentLanguage();
            if (ReferenceEquals(balance, cachedBalance) &&
                itemCount == cachedItemCount &&
                string.Equals(language, cachedLanguage, StringComparison.Ordinal))
            {
                return;
            }

            cachedBalance = balance;
            cachedItemCount = itemCount;
            cachedLanguage = language;
            Build(definitions);
        }

        private static void Build(List<ItemDefinition> definitions)
        {
            var entries = new List<Entry>();
            var exactIds = new Dictionary<string, Entry>(
                StringComparer.OrdinalIgnoreCase);
            if (definitions != null)
            {
                for (int i = 0; i < definitions.Count; i++)
                {
                    ItemDefinition definition = definitions[i];
                    string id = definition?.id;
                    if (string.IsNullOrWhiteSpace(id) || exactIds.ContainsKey(id))
                        continue;

                    string friendlyName = GetFriendlyName(definition);
                    string baseAlias = NormalizeAlias(friendlyName);
                    if (baseAlias.Length == 0)
                        baseAlias = NormalizeAlias(id);

                    var entry = new Entry
                    {
                        Definition = definition,
                        Id = id,
                        FamilyId = definition.quality_type ==
                                   ItemDefinition.QualityType.Stars
                            ? definition.GetNameWithoutQualitySuffix()
                            : id,
                        FriendlyName = friendlyName,
                        BaseAlias = baseAlias
                    };
                    entries.Add(entry);
                    exactIds.Add(id, entry);
                }
            }

            entries.Sort(CompareEntries);
            var groups = new Dictionary<string, List<Entry>>(
                StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (!groups.TryGetValue(entry.BaseAlias, out List<Entry> group))
                {
                    group = new List<Entry>();
                    groups.Add(entry.BaseAlias, group);
                }

                group.Add(entry);
            }

            var aliases = new Dictionary<string, List<Entry>>(
                StringComparer.OrdinalIgnoreCase);
            var canonicalOwners = new Dictionary<string, Entry>(
                StringComparer.OrdinalIgnoreCase);
            var groupKeys = new List<string>(groups.Keys);
            groupKeys.Sort(StringComparer.OrdinalIgnoreCase);
            for (int groupIndex = 0; groupIndex < groupKeys.Count; groupIndex++)
            {
                List<Entry> group = groups[groupKeys[groupIndex]];
                group.Sort(CompareQualityThenId);
                bool qualityFamily = IsSingleQualityFamily(group);

                for (int i = 0; i < group.Count; i++)
                {
                    Entry entry = group[i];
                    string qualityToken = GetQualityToken(entry.Definition);
                    bool hasQuality = qualityToken.Length > 0;
                    string proposedAlias;
                    if (hasQuality)
                    {
                        proposedAlias = qualityToken + "_" + entry.BaseAlias;
                        entry.QualifiedName = entry.FriendlyName + " (" +
                                              QualityTokenToDisplay(qualityToken) +
                                              ")";
                    }
                    else if (group.Count == 1)
                    {
                        proposedAlias = entry.BaseAlias;
                        entry.QualifiedName = entry.FriendlyName;
                    }
                    else
                    {
                        proposedAlias = entry.BaseAlias + "_" +
                                        NormalizeAlias(entry.Id);
                        entry.QualifiedName = entry.FriendlyName;
                    }

                    entry.CanonicalAlias = MakeUniqueAlias(
                        proposedAlias,
                        entry,
                        canonicalOwners,
                        aliases,
                        exactIds);
                    entry.CompletionDisplay = entry.QualifiedName + " — " +
                                              entry.CanonicalAlias + " [" +
                                              entry.Id + "]";

                    RegisterAlias(
                        aliases,
                        entry.CanonicalAlias,
                        entry,
                        canonicalOwners);
                    RegisterAlias(
                        aliases,
                        NormalizeAlias(entry.Id),
                        entry,
                        canonicalOwners);
                    AddMatchAlias(entry, entry.BaseAlias);

                    if (hasQuality)
                    {
                        RegisterAlias(
                            aliases,
                            entry.BaseAlias + "_" + qualityToken,
                            entry,
                            canonicalOwners);
                    }
                }

                if (group.Count == 1 || qualityFamily)
                {
                    RegisterAlias(
                        aliases,
                        group[0].BaseAlias,
                        group[0],
                        canonicalOwners);
                }
                else
                {
                    // Keep a colliding friendly name detectable so execution can
                    // explain the ambiguity instead of silently choosing an item.
                    for (int i = 0; i < group.Count; i++)
                        RegisterAlias(
                            aliases,
                            group[i].BaseAlias,
                            group[i],
                            canonicalOwners);
                }
            }

            // Keep the original shorthand deterministic in every language.
            if (exactIds.TryGetValue(BronzeBeerId, out Entry bronzeBeer))
            {
                aliases["beer"] = new List<Entry> { bronzeBeer };
                AddMatchAlias(bronzeBeer, "beer");
            }

            cachedEntries = entries.ToArray();
            entriesById = exactIds;
            entriesByAlias = aliases;
        }

        private static bool MatchesFriendlyPrefix(
            Entry entry,
            string rawPartial,
            string normalizedPartial)
        {
            if (rawPartial.Length == 0 ||
                StartsWith(entry.FriendlyName, rawPartial) ||
                StartsWith(entry.QualifiedName, rawPartial))
            {
                return true;
            }

            for (int i = 0; i < entry.MatchAliases.Count; i++)
            {
                if (StartsWith(entry.MatchAliases[i], normalizedPartial))
                    return true;
            }

            return false;
        }

        private static void AddPseudoCompletion(
            List<string> result,
            HashSet<string> seen,
            string value,
            string rawPartial,
            string normalizedPartial)
        {
            if ((rawPartial.Length == 0 ||
                 StartsWith(value, rawPartial) ||
                 StartsWith(value, normalizedPartial)) &&
                seen.Add(value))
            {
                result.Add(value);
            }
        }

        private static void RegisterAlias(
            Dictionary<string, List<Entry>> aliases,
            string alias,
            Entry entry,
            Dictionary<string, Entry> canonicalOwners)
        {
            alias = NormalizeAlias(alias);
            if (alias.Length == 0 || IsReservedAlias(alias))
            {
                return;
            }

            // A canonical alias must always resolve to exactly one item. Optional
            // synonyms yield when their spelling belongs to another entry.
            if (canonicalOwners.TryGetValue(alias, out Entry canonicalOwner) &&
                canonicalOwner != entry)
            {
                return;
            }

            if (!aliases.TryGetValue(alias, out List<Entry> matches))
            {
                matches = new List<Entry>();
                aliases.Add(alias, matches);
            }

            for (int i = 0; i < matches.Count; i++)
            {
                if (string.Equals(matches[i].Id, entry.Id,
                        StringComparison.OrdinalIgnoreCase))
                {
                    AddMatchAlias(entry, alias);
                    return;
                }
            }

            matches.Add(entry);
            AddMatchAlias(entry, alias);
        }

        private static void AddMatchAlias(Entry entry, string alias)
        {
            alias = NormalizeAlias(alias);
            if (alias.Length == 0)
                return;

            for (int i = 0; i < entry.MatchAliases.Count; i++)
            {
                if (string.Equals(entry.MatchAliases[i], alias,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            entry.MatchAliases.Add(alias);
        }

        private static string MakeUniqueAlias(
            string proposed,
            Entry entry,
            Dictionary<string, Entry> owners,
            Dictionary<string, List<Entry>> aliases,
            Dictionary<string, Entry> exactIds)
        {
            string candidate = NormalizeAlias(proposed);
            if (candidate.Length == 0)
                candidate = NormalizeAlias(entry.Id);

            if (IsAliasAvailable(
                    candidate,
                    entry,
                    owners,
                    aliases,
                    exactIds))
            {
                owners[candidate] = entry;
                return candidate;
            }

            candidate += "_" + NormalizeAlias(entry.Id);
            if (IsAliasAvailable(
                    candidate,
                    entry,
                    owners,
                    aliases,
                    exactIds))
            {
                owners[candidate] = entry;
                return candidate;
            }

            candidate += "_" + StableHash(entry.Id).ToString(
                "x8",
                CultureInfo.InvariantCulture);
            owners[candidate] = entry;
            return candidate;
        }

        private static bool IsAliasAvailable(
            string alias,
            Entry entry,
            Dictionary<string, Entry> owners,
            Dictionary<string, List<Entry>> aliases,
            Dictionary<string, Entry> exactIds)
        {
            if (IsReservedAlias(alias))
                return false;

            if (exactIds.TryGetValue(alias, out Entry exact) && exact != entry)
                return false;

            if (owners.TryGetValue(alias, out Entry owner) && owner != entry)
                return false;

            if (!aliases.TryGetValue(alias, out List<Entry> matches))
                return true;

            for (int i = 0; i < matches.Count; i++)
            {
                if (matches[i] != entry)
                    return false;
            }

            return true;
        }

        private static bool IsReservedAlias(string alias)
        {
            return string.Equals(alias, "beer", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(alias, "energy", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(alias, "money", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSingleQualityFamily(List<Entry> group)
        {
            if (group.Count < 2)
                return false;

            string family = group[0].FamilyId;
            for (int i = 0; i < group.Count; i++)
            {
                Entry entry = group[i];
                if (entry.Definition.quality_type !=
                        ItemDefinition.QualityType.Stars ||
                    !string.Equals(entry.FamilyId, family,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static int CompareEntries(Entry left, Entry right)
        {
            int byName = StringComparer.OrdinalIgnoreCase.Compare(
                left.FriendlyName,
                right.FriendlyName);
            return byName != 0
                ? byName
                : StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id);
        }

        private static int CompareQualityThenId(Entry left, Entry right)
        {
            int byQuality = left.Definition.quality.CompareTo(
                right.Definition.quality);
            return byQuality != 0
                ? byQuality
                : StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id);
        }

        private static string GetFriendlyName(ItemDefinition definition)
        {
            string localized = string.Empty;
            try
            {
                localized = SanitizeDisplay(definition.GetItemName(true));
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[DebugCommands] Could not localize item '{definition.id}': " +
                    ex.Message);
            }

            string baseId = definition.quality_type ==
                            ItemDefinition.QualityType.Stars
                ? definition.GetNameWithoutQualitySuffix()
                : definition.id;
            if (localized.Length == 0 ||
                string.Equals(localized, definition.id,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(localized, baseId,
                    StringComparison.OrdinalIgnoreCase))
            {
                localized = HumanizeIdentifier(baseId);
            }

            return localized.Length > 0 ? localized : definition.id;
        }

        private static string GetQualityToken(ItemDefinition definition)
        {
            if (definition.quality_type != ItemDefinition.QualityType.Stars ||
                definition.quality <= 0.1f)
            {
                return string.Empty;
            }

            int rounded = Mathf.RoundToInt(definition.quality);
            if (Mathf.Approximately(definition.quality, rounded))
            {
                if (rounded == 1) return "bronze";
                if (rounded == 2) return "silver";
                if (rounded == 3) return "gold";
            }

            return "quality_" + definition.quality.ToString(
                "0.#",
                CultureInfo.InvariantCulture).Replace('.', '_');
        }

        private static string QualityTokenToDisplay(string token)
        {
            return token.Replace('_', ' ');
        }

        private static string GetCurrentLanguage()
        {
            try
            {
                return GameSettings.GetCurrentLanguage() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SanitizeDisplay(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var result = new StringBuilder(value.Length);
            bool insideTag = false;
            bool pendingSpace = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '[')
                {
                    insideTag = true;
                    continue;
                }

                if (insideTag)
                {
                    if (c == ']')
                        insideTag = false;
                    continue;
                }

                if (char.IsControl(c) || char.IsWhiteSpace(c))
                {
                    pendingSpace = result.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    result.Append(' ');
                    pendingSpace = false;
                }

                result.Append(c);
            }

            return result.ToString().Trim();
        }

        private static string HumanizeIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var result = new StringBuilder(value.Length);
            bool newWord = true;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!char.IsLetterOrDigit(c))
                {
                    if (result.Length > 0 && result[result.Length - 1] != ' ')
                        result.Append(' ');
                    newWord = true;
                    continue;
                }

                result.Append(newWord ? char.ToUpperInvariant(c) : c);
                newWord = false;
            }

            return result.ToString().Trim();
        }

        private static string NormalizeAlias(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var result = new StringBuilder(value.Length);
            bool separatorPending = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsLetterOrDigit(c))
                {
                    if (separatorPending && result.Length > 0)
                        result.Append('_');
                    result.Append(char.ToLowerInvariant(c));
                    separatorPending = false;
                }
                else
                {
                    separatorPending = result.Length > 0;
                }
            }

            return result.ToString();
        }

        private static bool StartsWith(string value, string prefix)
        {
            return value != null && prefix != null &&
                   value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static uint StableHash(string value)
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;
            uint hash = offset;
            for (int i = 0; i < value.Length; i++)
            {
                hash ^= char.ToLowerInvariant(value[i]);
                hash *= prime;
            }

            return hash;
        }
    }
}

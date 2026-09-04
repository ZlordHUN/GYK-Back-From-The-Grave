using System.Diagnostics;
using UnityEngine;

namespace GraveyardKeeperCoop.Utils
{
    internal static class SpriteNameNormalizer
    {
        // Diagnostic accumulation — how long Normalize is costing per 5s window.
        private static long _totalTicks;
        private static long _callCount;
        private static float _nextReport;

        public static string Normalize(string spriteName)
        {
            long start = Stopwatch.GetTimestamp();
            string result = NormalizeCore(spriteName);
            _totalTicks += Stopwatch.GetTimestamp() - start;
            _callCount++;

            float now = Time.realtimeSinceStartup;
            if (now >= _nextReport)
            {
                _nextReport = now + 5f;
                CoopMod.Logger.LogWarning($"[PROFILER] SpriteNormalize={(_totalTicks * 1000.0 / Stopwatch.Frequency):F1}ms calls={_callCount}");
                _totalTicks = 0;
                _callCount = 0;
            }

            return result;
        }

        private static string NormalizeCore(string spriteName)
        {
            if (string.IsNullOrEmpty(spriteName))
                return spriteName;

            // Fast path: the overwhelming majority of sprite names contain no '(',
            // so there is no "(clone)" / "(suffix)" to strip. A single ordinal scan
            // and we are done (Trim is a cheap no-op for already-clean names).
            if (spriteName.IndexOf('(') < 0)
                return spriteName.Trim();

            // Has a paren — strip "(clone)" (case-insensitive, ordinal) or the first
            // parenthesized suffix, matching the previous behavior but without the
            // culture-aware CompareInfo search that dominated cost.
            string normalized = spriteName.Trim();
            int cloneIndex = normalized.IndexOf("(clone)", System.StringComparison.OrdinalIgnoreCase);
            if (cloneIndex > 0)
                return normalized.Substring(0, cloneIndex).Trim();

            int parenIndex = normalized.IndexOf('(');
            if (parenIndex > 0)
                return normalized.Substring(0, parenIndex).Trim();

            return normalized;
        }
    }
}

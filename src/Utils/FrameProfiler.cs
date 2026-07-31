using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace GraveyardKeeperCoop.Utils
{
    public static class FrameProfiler
    {
        private static readonly Dictionary<string, long> _sectionTicks = new Dictionary<string, long>();
        private static float _nextReport;
        private const float Interval = 5f;

        public static bool Enabled => ModConfig.EnablePerformanceProfiling?.Value == true;

        public static long BeginSection()
        {
            return Enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void EndSection(string section, long startedAt)
        {
            if (startedAt == 0L || !Enabled)
                return;

            Record(section, Stopwatch.GetTimestamp() - startedAt);
        }

        public static void Record(string section, long elapsedTicks)
        {
            if (!Enabled)
                return;

            _sectionTicks.TryGetValue(section, out long acc);
            _sectionTicks[section] = acc + elapsedTicks;
            MaybeReport();
        }

        private static void MaybeReport()
        {
            if (Time.realtimeSinceStartup < _nextReport) return;
            _nextReport = Time.realtimeSinceStartup + Interval;
            if (_sectionTicks.Count == 0) return;
            var sb = new StringBuilder();
            foreach (var kvp in _sectionTicks)
                sb.Append(kvp.Key).Append('=').Append((kvp.Value * 1000.0 / Stopwatch.Frequency).ToString("F1")).Append("ms ");
            CoopMod.Logger.LogWarning($"[PROFILER] {sb}");
            _sectionTicks.Clear();
        }
    }

    internal static class ProfilerState
    {
        public static float ManagedStart;
        public static float FrameSum;
        public static float ManagedSum;
        public static int Frames;
        public static float LastFrame;
        public static float NextReport;
        public static int GcGen0Start;
        public static int GcGen1Start;
    }

    // Runs FIRST in the Update phase — marks the start of managed work for the frame.
    [DefaultExecutionOrder(-30000)]
    public class FrameProfilerStart : MonoBehaviour
    {
        private void Update()
        {
            if (!FrameProfiler.Enabled)
                return;

            float now = Time.realtimeSinceStartup;
            ProfilerState.ManagedStart = now;
            if (ProfilerState.LastFrame > 0f)
            {
                ProfilerState.FrameSum += now - ProfilerState.LastFrame;
                ProfilerState.Frames++;
            }
            ProfilerState.LastFrame = now;

            if (now < ProfilerState.NextReport) return;
            ProfilerState.NextReport = now + 5f;
            if (ProfilerState.Frames == 0) return;
            float avgFrame = ProfilerState.FrameSum / ProfilerState.Frames;
            float fps = ProfilerState.Frames / ProfilerState.FrameSum;
            float avgManaged = ProfilerState.ManagedSum / ProfilerState.Frames;
            float avgNative = avgFrame - avgManaged;
            int gcGen0 = System.GC.CollectionCount(0) - ProfilerState.GcGen0Start;
            int gcGen1 = System.GC.CollectionCount(1) - ProfilerState.GcGen1Start;
            ProfilerState.GcGen0Start = System.GC.CollectionCount(0);
            ProfilerState.GcGen1Start = System.GC.CollectionCount(1);
            long mem = System.GC.GetTotalMemory(false);
            CoopMod.Logger.LogWarning($"[PROFILER] fps={fps:F1} frame={avgFrame * 1000f:F1}ms managed-all={avgManaged * 1000f:F1}ms native={avgNative * 1000f:F1}ms gc0={gcGen0} gc1={gcGen1} heap={mem / 1048576}mb frames={ProfilerState.Frames}");
            ProfilerState.FrameSum = 0f;
            ProfilerState.Frames = 0;
            ProfilerState.ManagedSum = 0f;
        }
    }

    // Runs LAST in the LateUpdate phase — marks the end of managed work for the frame.
    [DefaultExecutionOrder(30000)]
    public class FrameProfilerEnd : MonoBehaviour
    {
        private void LateUpdate()
        {
            if (!FrameProfiler.Enabled)
                return;

            ProfilerState.ManagedSum += Time.realtimeSinceStartup - ProfilerState.ManagedStart;
        }
    }
}
